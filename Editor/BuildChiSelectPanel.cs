#if UNITY_EDITOR
// チーの候補パネル（MahjongChiPanel）を卓 Prefab に組み立てる。
//
//   KemonoMahjong/ChiSelectPanel          … MahjongChiPanel（常時アクティブ）
//     Root                                … panelRoot（表示ON/OFF）
//       Content                           … 牌の正面から見た座標系（牌の姿勢を180度回す）
//         Canvas                          … 枠・見出し・候補の下敷き・戻るボタンの絵
//         Option0..5                      … 当たり判定＋MahjongPushButton(_ChiOptionN)
//           Tile0, Tile1                  … 牌の板（実行時に実物の牌のメッシュへ差し替え）
//         Back                            … 当たり判定＋MahjongPushButton(_ChiBack)
//
// 寸法・配置は実行時に MahjongChiPanel.Show が候補数に合わせて決める（ここでは部品を作るだけ）。
// 2回実行しても安全（冪等）。
// 実行: Tools > Kemono Mahjong > Build Chi Select Panel
//       （初回は U# プログラムアセット生成→コンパイル待ちで止まるので、もう一度実行）
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using SuzuFactory.AozoraMahjong;

public static class BuildChiSelectPanel
{
    const string PrefabPath = "Assets/Mahjong/Prefabas/KemonoMahjong.prefab";
    const string FontPath = "Assets/Mahjong/Fonts/NotoSansJP-Bold.ttf";
    const string PanelScriptPath = "Assets/Mahjong/Sources/MahjongChiPanel.cs";
    const string ProgramAssetDir = "Assets/Mahjong/scene_UdonProgramSources";
    const string PanelProgramAssetPath = ProgramAssetDir + "/MahjongChiPanel Udon C# Program Asset.asset";
    const string SampleTilePath = "TilesSet/SampleTiles0/M1";

    const int OptionMax = 6;
    const float PixelsPerMeter = 4000f;
    const float Proximity = 0.25f;   // TuneButtonReach と同じ（VRの手の届く範囲）

    // 雀魂の候補パネルの配色。
    static readonly Color BorderColor = new Color(0.86f, 0.66f, 0.34f, 1f);
    static readonly Color FillColor = new Color(0.10f, 0.15f, 0.30f, 0.96f);
    static readonly Color PlateColor = new Color(0.20f, 0.28f, 0.48f, 0.90f);
    static readonly Color HeaderColor = new Color(0.36f, 0.10f, 0.13f, 0.97f);
    static readonly Color HeaderTextColor = new Color(0.98f, 0.90f, 0.82f, 1f);
    static readonly Color OrnamentColor = new Color(0.93f, 0.74f, 0.42f, 1f);

    [MenuItem("Tools/Kemono Mahjong/Build Chi Select Panel")]
    public static void Run()
    {
        if (EnsureProgramAsset(PanelScriptPath, PanelProgramAssetPath))
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[BuildChiSelectPanel] U#プログラムアセットを生成しました。コンパイル完了後、もう一度実行してください。");
            return;
        }

        var font = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
        if (font == null) { Debug.LogError("[BuildChiSelectPanel] フォントがありません: " + FontPath); return; }

        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null) { Debug.LogError("[BuildChiSelectPanel] Prefab を読み込めません: " + PrefabPath); return; }

        try
        {
            var sample = root.transform.Find(SampleTilePath);
            var sampleMf = sample != null ? sample.GetComponent<MeshFilter>() : null;
            var sampleMr = sample != null ? sample.GetComponent<MeshRenderer>() : null;
            if (sampleMf == null || sampleMr == null)
            {
                Debug.LogError("[BuildChiSelectPanel] 見本の牌が見つかりません: " + SampleTilePath);
                return;
            }

            var refButton = root.transform.Find("PlayerUIWrapper/PlayerUI/PonButton");
            int buttonLayer = refButton != null ? refButton.gameObject.layer : 0;

            var panelGo = EnsureChild(root.transform, "ChiSelectPanel");
            panelGo.SetActive(true);
            var panel = (MahjongChiPanel)EnsureUSharpComponent(panelGo, typeof(MahjongChiPanel));
            if (panel == null) { return; }

            var rootGo = EnsureChild(panelGo.transform, "Root");
            rootGo.SetActive(false);

            var content = EnsureChild(rootGo.transform, "Content").transform;
            content.localPosition = Vector3.zero;
            // 待ち牌パネルと同じ約束：牌の姿勢を180度回すと「+X＝本人の右」の正面座標になる。
            content.localRotation = Quaternion.Euler(0f, 180f, 0f);
            content.localScale = Vector3.one;

            // ---- Canvas ----
            var canvasGo = EnsureUiChild(content, "Canvas");
            canvasGo.layer = buttonLayer;
            var canvas = canvasGo.GetComponent<Canvas>();
            if (canvas == null) { canvas = canvasGo.AddComponent<Canvas>(); }
            canvas.renderMode = RenderMode.WorldSpace;
            var canvasRect = canvasGo.GetComponent<RectTransform>();
            canvasRect.localRotation = Quaternion.identity;
            canvasRect.localScale = Vector3.one / PixelsPerMeter;
            canvasRect.sizeDelta = new Vector2(100f, 100f);
            // 当たり判定は BoxCollider で取る。uGUI のレイキャスタは付けない（付いていたら外す）。
            var raycaster = canvasGo.GetComponent<GraphicRaycaster>();
            if (raycaster != null) { Object.DestroyImmediate(raycaster); }

            var border = EnsureImage(canvasGo.transform, "Border", BorderColor);
            var fill = EnsureImage(canvasGo.transform, "Fill", FillColor);

            var plates = new RectTransform[OptionMax];
            for (int k = 0; k < OptionMax; ++k)
            {
                plates[k] = EnsureImage(canvasGo.transform, "Plate" + k, PlateColor);
            }

            var header = EnsureImage(canvasGo.transform, "Header", HeaderColor);
            var headerText = EnsureText(header, "HeaderText", font, 52, HeaderTextColor, "選択してください");

            var backPlate = EnsureImage(canvasGo.transform, "BackPlate", BorderColor);
            var backInner = EnsureImage(backPlate, "BackInner", FillColor);
            Stretch(backInner, 8f);
            var backText = EnsureText(backPlate, "BackText", font, 70, HeaderTextColor, "↩");
            backText.alignment = TextAnchor.MiddleCenter;

            var ornaments = new RectTransform[4];
            for (int i = 0; i < 4; ++i)
            {
                ornaments[i] = EnsureImage(canvasGo.transform, "Ornament" + i, OrnamentColor);
                ornaments[i].sizeDelta = new Vector2(44f, 44f);
                ornaments[i].localRotation = Quaternion.Euler(0f, 0f, 45f);
            }

            // 描画順：枠 → 地 → 下敷き → 見出し → 戻る → 角飾り
            int order = 0;
            border.SetSiblingIndex(order++);
            fill.SetSiblingIndex(order++);
            for (int k = 0; k < OptionMax; ++k) { plates[k].SetSiblingIndex(order++); }
            header.SetSiblingIndex(order++);
            backPlate.SetSiblingIndex(order++);
            for (int i = 0; i < 4; ++i) { ornaments[i].SetSiblingIndex(order++); }

            // ---- 候補（当たり判定＋牌2枚）----
            var optionRoots = new GameObject[OptionMax];
            var optionColliders = new BoxCollider[OptionMax];
            var tileFilters = new MeshFilter[OptionMax * 2];

            for (int k = 0; k < OptionMax; ++k)
            {
                var opt = EnsureChild(content, "Option" + k);
                opt.layer = buttonLayer;
                opt.transform.localRotation = Quaternion.identity;
                opt.transform.localScale = Vector3.one;
                opt.SetActive(false);

                optionColliders[k] = EnsureBoxCollider(opt);
                if (!EnsurePushButton(opt, "_ChiOption" + k)) { return; }
                optionRoots[k] = opt;

                for (int t = 0; t < 2; ++t)
                {
                    var tileGo = EnsureChild(opt.transform, "Tile" + t);
                    tileGo.layer = sample.gameObject.layer;
                    tileGo.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);  // Content の180度を打ち消す＝立てた牌と同じ姿勢
                    tileGo.transform.localScale = sample.lossyScale;

                    var mf = tileGo.GetComponent<MeshFilter>();
                    if (mf == null) { mf = tileGo.AddComponent<MeshFilter>(); }
                    mf.sharedMesh = sampleMf.sharedMesh;

                    var mr = tileGo.GetComponent<MeshRenderer>();
                    if (mr == null) { mr = tileGo.AddComponent<MeshRenderer>(); }
                    mr.sharedMaterials = sampleMr.sharedMaterials;
                    mr.shadowCastingMode = ShadowCastingMode.Off;
                    mr.receiveShadows = false;
                    mr.lightProbeUsage = LightProbeUsage.Off;
                    mr.reflectionProbeUsage = ReflectionProbeUsage.Off;

                    // 牌に当たり判定があると候補の BoxCollider より手前で Interact を奪う。
                    foreach (var col in tileGo.GetComponents<Collider>()) { Object.DestroyImmediate(col); }

                    tileFilters[k * 2 + t] = mf;
                }
            }

            // ---- 戻る ----
            var back = EnsureChild(content, "Back");
            back.layer = buttonLayer;
            back.transform.localRotation = Quaternion.identity;
            var backCollider = EnsureBoxCollider(back);
            if (!EnsurePushButton(back, "_ChiBack")) { return; }

            // ---- 配線 ----
            panel.panelRoot = rootGo;
            panel.content = content;
            panel.canvasRect = canvasRect;
            panel.borderRect = border;
            panel.fillRect = fill;
            panel.headerRect = header;
            panel.headerText = headerText;
            panel.ornamentRects = ornaments;
            panel.optionRoots = optionRoots;
            panel.optionColliders = optionColliders;
            panel.tileFilters = tileFilters;
            panel.optionPlateRects = plates;
            panel.backButton = back.transform;
            panel.backCollider = backCollider;
            panel.backPlateRect = backPlate;
            panel.pixelsPerMeter = PixelsPerMeter;
            UdonSharpEditorUtility.CopyProxyToUdon(panel);

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log("[BuildChiSelectPanel] 完了: 候補 " + OptionMax + " 枠＋戻るボタンを組み立て、Prefab を保存しました（シーンの全卓に反映）。");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Validate();
    }

    [MenuItem("Tools/Kemono Mahjong/Validate Chi Select Panel")]
    public static void Validate()
    {
        int tables = 0, ok = 0;
        var sb = new System.Text.StringBuilder();

        foreach (var m in Object.FindObjectsOfType<Mahjong>(true))
        {
            ++tables;
            string why = Check(m.transform);
            if (why == null) { ++ok; }
            else { sb.Append(m.name).Append(": ").Append(why).Append("\n"); }
        }

        if (ok == tables)
        {
            Debug.Log("[ValidateChiSelectPanel] OK: 卓 " + tables + " 個すべてに候補パネルあり・配線済み。");
        }
        else
        {
            Debug.LogError("[ValidateChiSelectPanel] NG " + (tables - ok) + " / " + tables + "\n" + sb);
        }
    }

    static string Check(Transform table)
    {
        var t = table.Find("ChiSelectPanel");
        if (t == null) { return "ChiSelectPanel が無い"; }
        if (!t.gameObject.activeSelf) { return "ChiSelectPanel が非アクティブ（Show が呼ばれても出ない）"; }

        var p = t.GetComponent<MahjongChiPanel>();
        if (p == null) { return "MahjongChiPanel が無い"; }
        if (p.panelRoot == null || p.content == null || p.canvasRect == null) { return "panelRoot/content/canvasRect の配線切れ"; }
        if (p.optionRoots == null || p.optionRoots.Length != OptionMax) { return "optionRoots が " + OptionMax + " 個でない"; }
        if (p.tileFilters == null || p.tileFilters.Length != OptionMax * 2) { return "tileFilters が " + (OptionMax * 2) + " 個でない"; }
        if (p.backButton == null || p.backCollider == null) { return "戻るボタンの配線切れ"; }

        for (int k = 0; k < OptionMax; ++k)
        {
            if (p.optionRoots[k] == null || p.tileFilters[k * 2] == null || p.tileFilters[k * 2 + 1] == null)
            {
                return "候補 " + k + " の配線切れ";
            }
            var pb = p.optionRoots[k].GetComponent<MahjongPushButton>();
            if (pb == null || pb.id != "_ChiOption" + k || !pb.local || pb.disableOnPush)
            {
                return "候補 " + k + " の MahjongPushButton 設定が違う";
            }
        }
        foreach (var col in t.GetComponentsInChildren<Collider>(true))
        {
            if (!col.isTrigger) { return col.name + " の当たり判定が固体（プレイヤーを押し戻す）"; }
        }

        var bpb = p.backButton.GetComponent<MahjongPushButton>();
        if (bpb == null || bpb.id != "_ChiBack" || !bpb.local || bpb.disableOnPush) { return "戻るボタンの MahjongPushButton 設定が違う"; }
        return null;
    }

    // ============================================================
    // 部品
    // ============================================================
    static bool EnsurePushButton(GameObject go, string id)
    {
        var pb = (MahjongPushButton)EnsureUSharpComponent(go, typeof(MahjongPushButton));
        if (pb == null) { return false; }
        pb.id = id;
        pb.local = true;
        pb.disableOnPush = false;
        UdonSharpEditorUtility.CopyProxyToUdon(pb);

        var ub = UdonSharpEditorUtility.GetBackingUdonBehaviour(pb);
        if (ub != null) { ub.proximity = Proximity; }
        return true;
    }

    static BoxCollider EnsureBoxCollider(GameObject go)
    {
        var c = go.GetComponent<BoxCollider>();
        if (c == null) { c = go.AddComponent<BoxCollider>(); }
        // 必ずトリガー。固体だとパネルが出た瞬間にプレイヤーの体を卓から押し戻し、河が見えなくなる
        // （手牌のすぐ上＝体の目の前に出るため）。Interact はトリガーでも効く（卓の他のボタンも全部トリガー）。
        c.isTrigger = true;
        c.size = new Vector3(0.04f, 0.03f, 0.03f);   // 実寸は Show が候補数に合わせて入れる
        return c;
    }

    static RectTransform EnsureImage(Transform parent, string name, Color color)
    {
        var go = EnsureUiChild(parent, name);
        go.layer = parent.gameObject.layer;
        var img = go.GetComponent<Image>();
        if (img == null) { img = go.AddComponent<Image>(); }
        img.color = color;
        img.raycastTarget = false;
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.localScale = Vector3.one;
        return rt;
    }

    static Text EnsureText(Transform parent, string name, Font font, int size, Color color, string text)
    {
        var go = EnsureUiChild(parent, name);
        go.layer = parent.gameObject.layer;
        var tx = go.GetComponent<Text>();
        if (tx == null) { tx = go.AddComponent<Text>(); }
        tx.font = font;
        tx.fontSize = size;
        tx.color = color;
        tx.text = text;
        tx.alignment = TextAnchor.MiddleCenter;
        tx.horizontalOverflow = HorizontalWrapMode.Overflow;
        tx.verticalOverflow = VerticalWrapMode.Overflow;
        tx.raycastTarget = false;
        Stretch(go.GetComponent<RectTransform>(), 0f);
        return tx;
    }

    static void Stretch(RectTransform rt, float inset)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(inset, inset);
        rt.offsetMax = new Vector2(-inset, -inset);
        rt.localScale = Vector3.one;
        rt.localRotation = Quaternion.identity;
    }

    static GameObject EnsureChild(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null) { return t.gameObject; }
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go;
    }

    static GameObject EnsureUiChild(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null && t.GetComponent<RectTransform>() != null) { return t.gameObject; }
        if (t != null) { Object.DestroyImmediate(t.gameObject); }
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go;
    }

    static bool EnsureProgramAsset(string scriptPath, string assetPath)
    {
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
        if (script == null)
        {
            Debug.LogError("[BuildChiSelectPanel] スクリプトが見つかりません: " + scriptPath);
            return false;
        }
        var type = script.GetClass();
        if (type != null && UdonSharpProgramAsset.GetProgramAssetForClass(type) != null) { return false; }
        if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(assetPath) != null) { return false; }

        var programAsset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
        programAsset.sourceCsScript = script;
        AssetDatabase.CreateAsset(programAsset, assetPath);
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
            Debug.LogError("[BuildChiSelectPanel] " + type.Name + " の追加に失敗（プログラムアセット未コンパイル？）。");
            Object.DestroyImmediate(added);
            return null;
        }
        return added;
    }
}
#endif
