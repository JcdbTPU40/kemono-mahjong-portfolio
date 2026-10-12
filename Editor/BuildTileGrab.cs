#if UNITY_EDITOR
// 牌を掴んで打牌する入力（MahjongTileGrab）をシーンの全卓に組み込む。
//
//   <卓>/TileGrab               … MahjongTileGrab（常時アクティブ・同期なし）
//   <卓>/TileGrab/TileGrabProxy … 伏せ牌として見えている人に持たせる裏面だけの代役（普段は非アクティブ）
//   <卓>.tileGrab               … 卓本体からの参照（ネットワークの受け口は卓本体が持つ）
//   <卓>/PlayerUIWrapper/PlayerUI/KitaButton … id=_OnKitaButtonLocal・local=true に付け替える
//     （押したら本人の端末で Mahjong._OnKitaButtonLocal が受け、掴み打牌が ON なら北を選ぶ2段、
//       OFF なら従来どおり Owner へ OnKitaButton を送る＝レーザーだけの人の動作は変わらない）
//
// 代役は卓の牌（TilesSet/Tiles*/ の最初の牌）のメッシュを使い、絵柄面にも裏面のマテリアルを貼る
// （実物を手に持たせると、伏せ牌の人が回り込んで絵柄を覗けるため）。
// 2回実行しても安全（冪等）。設定のON/OFFは MainMenu のワールド設定〔VR操作〕（Build Main Menu → Wire Main Menu）。
// 実行: Tools > Kemono Mahjong > Build Tile Grab
//       （初回は U# プログラムアセット生成→コンパイル待ちで止まるので、もう一度実行）
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SuzuFactory.AozoraMahjong;

public static class BuildTileGrab
{
    const string ScriptPath = "Assets/Mahjong/Sources/MahjongTileGrab.cs";
    const string ProgramAssetPath = "Assets/Mahjong/scene_UdonProgramSources/MahjongTileGrab Udon C# Program Asset.asset";
    const string RootName = "TileGrab";
    const string ProxyName = "TileGrabProxy";
    const string KitaButtonPath = "PlayerUIWrapper/PlayerUI/KitaButton";
    const string KitaButtonId = "_OnKitaButtonLocal";

    [MenuItem("Tools/Kemono Mahjong/Build Tile Grab")]
    public static void Run()
    {
        if (EnsureProgramAsset())
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[BuildTileGrab] U#プログラムアセットを生成しました。コンパイル完了後、もう一度実行してください。");
            return;
        }

        var tables = Object.FindObjectsOfType<Mahjong>(true);
        int built = 0;
        foreach (var table in tables)
        {
            if (!table.gameObject.scene.IsValid()) { continue; }
            string why = BuildOne(table);
            if (why != null)
            {
                Debug.LogError("[BuildTileGrab] " + table.name + ": " + why);
                return;
            }
            ++built;
        }

        if (built == 0)
        {
            Debug.LogError("[BuildTileGrab] シーンに卓（Mahjong）が見つかりません。");
            return;
        }
        EditorSceneManager.MarkSceneDirty(tables[0].gameObject.scene);
        Debug.Log("[BuildTileGrab] 完了: " + built + " 卓に牌を掴む入力を組み込みました。シーンを保存してください。");
        Validate();
    }

    static string BuildOne(Mahjong table)
    {
        var tableT = table.transform;
        var sample = FindSampleTile(tableT);
        if (sample == null) { return "TilesSet/Tiles*/ に牌（MeshRenderer＋MeshFilter）が無い"; }

        var rootT = tableT.Find(RootName);
        GameObject root;
        if (rootT == null)
        {
            root = new GameObject(RootName);
            Undo.RegisterCreatedObjectUndo(root, "Build Tile Grab");
            root.transform.SetParent(tableT, false);
        }
        else
        {
            root = rootT.gameObject;
        }
        root.transform.localPosition = Vector3.zero;
        root.transform.localRotation = Quaternion.identity;
        root.transform.localScale = Vector3.one;
        // スクリプトが載るルートは常時アクティブ（非アクティブな U# は外から叩けない）。
        root.SetActive(true);

        var proxy = BuildProxy(root.transform, sample, tableT);

        var grab = (MahjongTileGrab)EnsureUSharpComponent(root, typeof(MahjongTileGrab));
        if (grab == null) { return "MahjongTileGrab を追加できない（プログラムアセット未コンパイル？）"; }
        grab.mahjong = table;
        grab.proxy = proxy;
        UdonSharpEditorUtility.CopyProxyToUdon(grab);
        EditorUtility.SetDirty(grab);

        if (table.tileGrab != grab)
        {
            table.tileGrab = grab;
            UdonSharpEditorUtility.CopyProxyToUdon(table);
            EditorUtility.SetDirty(table);
        }

        // 北抜きボタンを本人の端末で受ける（押した直後にボタンを伏せる disableOnPush は今のまま）。
        var kitaT = tableT.Find(KitaButtonPath);
        var kita = kitaT != null ? kitaT.GetComponent<MahjongPushButton>() : null;
        if (kita == null) { return KitaButtonPath + " に MahjongPushButton が無い"; }
        if (kita.id != KitaButtonId || !kita.local)
        {
            Undo.RecordObject(kita, "Rewire Kita Button");
            kita.id = KitaButtonId;
            kita.local = true;
            UdonSharpEditorUtility.CopyProxyToUdon(kita);
            EditorUtility.SetDirty(kita);
        }
        return null;
    }

    static Transform BuildProxy(Transform root, Transform sample, Transform tableT)
    {
        var t = root.Find(ProxyName);
        GameObject go;
        if (t == null)
        {
            go = new GameObject(ProxyName);
            Undo.RegisterCreatedObjectUndo(go, "Build Tile Grab Proxy");
            go.transform.SetParent(root, false);
        }
        else
        {
            go = t.gameObject;
        }
        go.layer = sample.gameObject.layer;

        var srcFilter = sample.GetComponent<MeshFilter>();
        var srcRenderer = sample.GetComponent<MeshRenderer>();
        var mf = go.GetComponent<MeshFilter>();
        if (mf == null) { mf = Undo.AddComponent<MeshFilter>(go); }
        mf.sharedMesh = srcFilter.sharedMesh;

        var mr = go.GetComponent<MeshRenderer>();
        if (mr == null) { mr = Undo.AddComponent<MeshRenderer>(go); }
        // 全部の submesh に裏面（0番）のマテリアルを貼る＝どこから見ても絵柄が無い。
        var src = srcRenderer.sharedMaterials;
        var mats = new Material[src.Length];
        for (int i = 0; i < mats.Length; ++i) { mats[i] = src[0]; }
        mr.sharedMaterials = mats;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        mr.lightProbeUsage = srcRenderer.lightProbeUsage;
        mr.reflectionProbeUsage = srcRenderer.reflectionProbeUsage;

        // 見た目の大きさを実物の牌に揃える（卓ルートの縮尺で割る）。
        var ls = sample.lossyScale;
        var ts = tableT.lossyScale;
        go.transform.localScale = new Vector3(ls.x / ts.x, ls.y / ts.y, ls.z / ts.z);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.SetActive(false);
        return go.transform;
    }

    // TilesSet/Tiles0/ の最初の牌（絵柄面つき＝マテリアル2枚以上）。
    static Transform FindSampleTile(Transform tableT)
    {
        var set = tableT.Find("TilesSet");
        if (set == null) { return null; }
        foreach (Transform tiles in set)
        {
            if (!tiles.name.StartsWith("Tiles")) { continue; }
            foreach (Transform tile in tiles)
            {
                var mf = tile.GetComponent<MeshFilter>();
                var mr = tile.GetComponent<MeshRenderer>();
                if (mf == null || mr == null || mf.sharedMesh == null) { continue; }
                if (mr.sharedMaterials == null || mr.sharedMaterials.Length < 2) { continue; }
                return tile;
            }
        }
        return null;
    }

    [MenuItem("Tools/Kemono Mahjong/Validate Tile Grab")]
    public static void Validate()
    {
        int tables = 0, ok = 0;
        var sb = new System.Text.StringBuilder();
        foreach (var m in Object.FindObjectsOfType<Mahjong>(true))
        {
            if (!m.gameObject.scene.IsValid()) { continue; }
            ++tables;
            string why = Check(m);
            if (why == null) { ++ok; }
            else { sb.Append(m.name).Append(": ").Append(why).Append("\n"); }
        }

        if (tables > 0 && ok == tables)
        {
            Debug.Log("[ValidateTileGrab] OK: 卓 " + tables + " 個すべてに牌を掴む入力あり・配線済み。");
        }
        else
        {
            Debug.LogError("[ValidateTileGrab] NG " + (tables - ok) + " / " + tables + "\n" + sb);
        }
    }

    static string Check(Mahjong table)
    {
        var t = table.transform.Find(RootName);
        if (t == null) { return RootName + " が無い"; }
        if (!t.gameObject.activeSelf) { return RootName + " が非アクティブ"; }
        var grab = t.GetComponent<MahjongTileGrab>();
        if (grab == null) { return "MahjongTileGrab が無い"; }
        if (UdonSharpEditorUtility.GetBackingUdonBehaviour(grab) == null) { return "MahjongTileGrab の Udon が無い"; }
        if (grab.mahjong != table) { return "mahjong の参照が自分の卓でない"; }
        if (grab.proxy == null) { return "proxy（代役）が未配線"; }
        if (grab.proxy.parent != t) { return "proxy が " + RootName + " の直下にない"; }
        if (grab.proxy.gameObject.activeSelf) { return "proxy が最初からアクティブ（常に見えてしまう）"; }
        var mr = grab.proxy.GetComponent<MeshRenderer>();
        if (mr == null || mr.sharedMaterials.Length < 2) { return "proxy の MeshRenderer が無い／マテリアル不足"; }
        for (int i = 1; i < mr.sharedMaterials.Length; ++i)
        {
            if (mr.sharedMaterials[i] != mr.sharedMaterials[0]) { return "proxy に絵柄のマテリアルが残っている（覗ける）"; }
        }
        if (table.tileGrab != grab) { return "卓本体の tileGrab が未配線"; }
        var kitaT = table.transform.Find(KitaButtonPath);
        var kita = kitaT != null ? kitaT.GetComponent<MahjongPushButton>() : null;
        if (kita == null) { return KitaButtonPath + " に MahjongPushButton が無い"; }
        if (kita.id != KitaButtonId || !kita.local) { return "北抜きボタンが旧配線（id=" + kita.id + " local=" + kita.local + "）＝Build Tile Grab をやり直す"; }
        return null;
    }

    static bool EnsureProgramAsset()
    {
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(ScriptPath);
        if (script == null)
        {
            Debug.LogError("[BuildTileGrab] スクリプトが見つかりません: " + ScriptPath);
            return false;
        }
        var type = script.GetClass();
        if (type != null && UdonSharpProgramAsset.GetProgramAssetForClass(type) != null) { return false; }
        if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(ProgramAssetPath) != null) { return false; }

        var programAsset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
        programAsset.sourceCsScript = script;
        AssetDatabase.CreateAsset(programAsset, ProgramAssetPath);
        AssetDatabase.SaveAssets();
        return true;
    }

    static UdonSharpBehaviour EnsureUSharpComponent(GameObject go, System.Type type)
    {
        var proxy = (UdonSharpBehaviour)go.GetComponent(type);
        if (proxy != null && UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy) != null) { return proxy; }
        if (proxy != null) { Object.DestroyImmediate(proxy); }

        var added = (UdonSharpBehaviour)UdonSharpUndo.AddComponent(go, type);
        if (added != null && UdonSharpEditorUtility.GetBackingUdonBehaviour(added) == null)
        {
            Debug.LogError("[BuildTileGrab] " + type.Name + " の追加に失敗（プログラムアセット未コンパイル？）。");
            Object.DestroyImmediate(added);
            return null;
        }
        return added;
    }
}
#endif
