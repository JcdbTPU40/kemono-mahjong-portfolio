#if UNITY_EDITOR
// 席の名札（Mリーグの卓の手前に置かれた名前のプレート）の骨格生成と配線。
//   Tools > Kemono Mahjong > Build Seat Name Plates    … 全卓に SeatNamePlates/Plate0..3 を冪等生成＋U#配線
//   Tools > Kemono Mahjong > Validate Seat Name Plates … 配線の欠けを検査
// 位置・大きさ・傾きを変えたい時は下の定数を直して Build を再実行する（シーンを手で動かしても Build で戻る）。
// Build はシーンの卓だけに走る（点数板と同じ。Prefab へは伝わらない）。
using TMPro;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SuzuFactory.AozoraMahjong;
using VRC.Udon;

public static class BuildSeatNamePlates
{
    const string RootName = "SeatNamePlates";
    const string ScriptPath = "Assets/Mahjong/Sources/MahjongSeatNamePlates.cs";
    const string ProgramAssetPath = "Assets/Mahjong/scene_UdonProgramSources/MahjongSeatNamePlates Udon C# Program Asset.asset";
    const string FontAssetPath = "Assets/Mahjong/Fonts/NotoSansJP-Bold SDF.asset";
    const string BaseMatPath = "Assets/Mahjong/Materials/SeatNamePlate.mat";
    const string AccentMatPath = "Assets/Mahjong/Materials/SeatNamePlateAccent.mat";
    // 段位紋章（デイリーランキングと同じ素材 BuildRankingPanelArt.RankEmblemPaths）を載せる透過マテリアル
    const string RankMatPath = "Assets/Mahjong/Materials/SeatNamePlateRank.mat";
    const float RankLift = 0.0015f;     // 紋章を小窓から浮かせる（重ねるとチカチカする）
    static Material rankMat_;
    // 名札専用の文字マテリアル（フォントの既定マテリアルの複製）。線を少し太く・輪郭を少し柔らかくして
    // VR のちらつきを抑える。共有マテリアルを直接いじると全部の文字が太るので分ける
    const string TextMatPath = "Assets/Mahjong/Fonts/NotoSansJP-Bold SDF SeatNamePlate.mat";
    const float TextDilate = 0.12f;
    const float TextSoftness = 0.06f;

    // 席0（卓ローカル +X）基準の配置。手牌は中心から 0.35m・立てた牌の手前の面が 0.358m、
    // 天板の縁が 0.43m（その外は点数板）。名札はその間のフェルトに置き、手前（本人側）へ傾けて立てかける。
    // 横は手牌の真ん中の手前（2026-10-05 ユーザー指定。当初は左半分の手前）。
    const float PlateRadius = 0.393f;   // 名札の中心の半径
    const float PlateLeft = 0f;         // 中心から本人の左へ（0＝手牌の真ん中の手前）
    const float PlateLength = 0.120f;   // 横の長さ
    const float PlateDepth = 0.020f;    // 縦（半径方向）の幅
    // 本人側へ起こす角度（奥の縁が上がる）。40°に起こす案はユーザーに否定された（寝かせたままにする）
    const float TiltDeg = 18f;
    const float LiftY = 0.0012f;        // フェルトとの Z ファイト回避
    // 文字は板から浮かせる。0.8mm だと VR の深度精度で板と取り合ってチカチカした（2026-10-05 指摘）
    const float TextLift = 0.0025f;
    const float AccentWidth = 0.022f;   // 左の風の小窓

    static readonly Color AccentColor = new Color(0.06f, 0.06f, 0.08f, 1f);
    static readonly Color BaseDefault = new Color(0.30f, 0.32f, 0.36f, 1f);

    [MenuItem("Tools/Kemono Mahjong/Build Seat Name Plates")]
    public static void Build()
    {
        if (EnsureProgramAsset())
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[BuildSeatNamePlates] U#プログラムアセットを生成しました。コンパイル完了後、もう一度 Build Seat Name Plates を実行してください。");
            return;
        }

        var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
        if (font == null) { Debug.LogError("[BuildSeatNamePlates] フォントが見つかりません: " + FontAssetPath); return; }
        var baseMat = EnsureMaterial(BaseMatPath, BaseDefault);
        var accentMat = EnsureMaterial(AccentMatPath, AccentColor);
        var textMat = EnsureTextMaterial(font);
        rankMat_ = AssetDatabase.LoadAssetAtPath<Material>(RankMatPath);
        if (rankMat_ == null)
        {
            rankMat_ = new Material(Shader.Find("Unlit/Transparent"));
            AssetDatabase.CreateAsset(rankMat_, RankMatPath);
            AssetDatabase.SaveAssets();
        }
        var emblems = new Texture[BuildRankingPanelArt.RankEmblemPaths.Length];
        for (int i = 0; i < emblems.Length; ++i)
        {
            emblems[i] = AssetDatabase.LoadAssetAtPath<Texture2D>(BuildRankingPanelArt.RankEmblemPaths[i]);
            if (emblems[i] == null) { Debug.LogError("[BuildSeatNamePlates] 段位紋章が見つかりません: " + BuildRankingPanelArt.RankEmblemPaths[i]); return; }
        }
        if (rankMat_.mainTexture == null) { rankMat_.mainTexture = emblems[0]; EditorUtility.SetDirty(rankMat_); }

        var tables = Object.FindObjectsOfType<Mahjong>(true);
        System.Array.Sort(tables, (a, b) => string.CompareOrdinal(GetPath(a.transform), GetPath(b.transform)));
        if (tables.Length == 0) { Debug.LogError("[BuildSeatNamePlates] シーンに Mahjong（卓）が見つかりません。"); return; }

        int wired = 0;
        foreach (var table in tables)
        {
            if (BuildOne(table, font, textMat, baseMat, accentMat, emblems)) { ++wired; }
        }
        EditorSceneManager.MarkSceneDirty(tables[0].gameObject.scene);
        Debug.Log("[BuildSeatNamePlates] 完了：" + wired + "/" + tables.Length + " 卓に席の名札を生成・配線。");
    }

    static bool BuildOne(Mahjong table, TMP_FontAsset font, Material textMat, Material baseMat, Material accentMat, Texture[] emblems)
    {
        // 本体（スクリプトの載る親）は常時アクティブ。表示の ON/OFF は子の Plate だけで行う
        var root = EnsureChild(table.transform, RootName);
        root.transform.localPosition = Vector3.zero;
        root.transform.localRotation = Quaternion.identity;
        root.transform.localScale = Vector3.one;
        root.SetActive(true);

        var plates = new GameObject[4];
        var names = new TextMeshPro[4];
        var ranks = new Renderer[4];
        var bases = new Renderer[4];
        for (int i = 0; i < 4; ++i)
        {
            BuildPlate(root.transform, i, font, textMat, baseMat, accentMat, out plates[i], out names[i], out ranks[i], out bases[i]);
        }

        var comp = (MahjongSeatNamePlates)EnsureUSharpComponent(root, typeof(MahjongSeatNamePlates));
        if (comp == null)
        {
            Debug.LogError("[BuildSeatNamePlates] MahjongSeatNamePlates の追加に失敗（プログラムアセット未コンパイル？）。コンパイル後に再実行してください。");
            return false;
        }
        Undo.RecordObject(comp, "Wire Seat Name Plates");
        comp.table = table;
        comp.plates = plates;
        comp.nameTexts = names;
        comp.rankRenderers = ranks;
        comp.rankEmblems = emblems;
        comp.baseRenderers = bases;
        UdonSharpEditorUtility.CopyProxyToUdon(comp);
        EditorUtility.SetDirty(comp);
        return true;
    }

    static void BuildPlate(Transform root, int seat, TMP_FontAsset font, Material textMat, Material baseMat, Material accentMat,
        out GameObject plateGo, out TextMeshPro nameText, out Renderer rankRenderer, out Renderer baseRenderer)
    {
        plateGo = EnsureChild(root, "Plate" + seat);
        var pt = plateGo.transform;

        // 席0の枠で組んでから席の回転（Mahjong.cs と同じ Euler(0, seat*-90, 0)・席0=+X）を掛ける。
        // 板の面（ローカル XY）：右＝本人の右（+Z）・上＝卓の中心側（-X）を本人側へ TiltDeg 起こす。
        float t = TiltDeg * Mathf.Deg2Rad;
        var seatRot = Quaternion.Euler(0f, seat * -90f, 0f);
        var up0 = new Vector3(-Mathf.Cos(t), Mathf.Sin(t), 0f);
        var fwd0 = new Vector3(-Mathf.Sin(t), -Mathf.Cos(t), 0f);   // 見る人の視線の向き（面の裏へ）
        var center0 = new Vector3(PlateRadius, LiftY + 0.5f * PlateDepth * Mathf.Sin(t), -PlateLeft);
        pt.localPosition = seatRot * center0;
        pt.localRotation = seatRot * Quaternion.LookRotation(fwd0, up0);
        pt.localScale = Vector3.one;

        // 地（席の人の色）。小窓とは重ねずに並べる（同じ面に重ねるとチカチカする）
        var bgo = EnsureQuad(pt, "Base", baseMat);
        bgo.transform.localPosition = new Vector3(0.5f * AccentWidth, 0f, 0f);
        bgo.transform.localRotation = Quaternion.identity;
        bgo.transform.localScale = new Vector3(PlateLength - AccentWidth, PlateDepth, 1f);
        baseRenderer = bgo.GetComponent<Renderer>();

        // 左の小窓（段位紋章の台）
        var ago = EnsureQuad(pt, "Accent", accentMat);
        float ax = -0.5f * PlateLength + 0.5f * AccentWidth;
        ago.transform.localPosition = new Vector3(ax, 0f, 0f);
        ago.transform.localRotation = Quaternion.identity;
        ago.transform.localScale = new Vector3(AccentWidth, PlateDepth, 1f);

        // 旧版の風の文字（2026-10-05 ユーザー指示で段位紋章へ置き換え）
        var oldWind = pt.Find("WindText");
        if (oldWind != null) { Undo.DestroyObjectImmediate(oldWind.gameObject); }

        // 段位紋章（正方形の絵）。小窓の高さいっぱいに置く
        var rgo = EnsureQuad(pt, "RankEmblem", rankMat_);
        float rsz = Mathf.Min(AccentWidth, PlateDepth) * 0.96f;
        rgo.transform.localPosition = new Vector3(ax, 0f, -RankLift);
        rgo.transform.localRotation = Quaternion.identity;
        rgo.transform.localScale = new Vector3(rsz, rsz, 1f);
        rankRenderer = rgo.GetComponent<Renderer>();

        nameText = EnsureTmp(pt, "NameText", font, textMat);
        float nameLeft = -0.5f * PlateLength + AccentWidth + 0.006f;
        float nameRight = 0.5f * PlateLength - 0.006f;
        nameText.rectTransform.localPosition = new Vector3(0.5f * (nameLeft + nameRight), 0f, -TextLift);
        nameText.rectTransform.sizeDelta = new Vector2(nameRight - nameLeft, PlateDepth * 0.82f);
        nameText.alignment = TextAlignmentOptions.MidlineLeft;
        nameText.enableAutoSizing = true;
        nameText.fontSizeMin = 0.05f;
        nameText.fontSizeMax = 0.20f;
        nameText.characterSpacing = 4f;
        nameText.overflowMode = TextOverflowModes.Ellipsis;
        nameText.text = "PLAYER NAME";

        plateGo.SetActive(false);   // 席が埋まるまで伏せる（実行時は MahjongSeatNamePlates が切り替える）
    }

    // ============================================================
    // Validate
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Validate Seat Name Plates")]
    public static void Validate()
    {
        var tables = Object.FindObjectsOfType<Mahjong>(true);
        int bad = 0;
        foreach (var table in tables)
        {
            var rt = table.transform.Find(RootName);
            var comp = rt != null ? rt.GetComponent<MahjongSeatNamePlates>() : null;
            string p = GetPath(table.transform);
            if (comp == null) { Debug.LogError("[ValidateSeatNamePlates] " + p + "：名札が未生成（Build Seat Name Plates）"); ++bad; continue; }
            if (comp.table != table) { Debug.LogError("[ValidateSeatNamePlates] " + p + "：table 参照が別の卓／空"); ++bad; }
            for (int i = 0; i < 4; ++i)
            {
                bool ok = comp.plates != null && comp.plates.Length == 4 && comp.plates[i] != null
                    && comp.nameTexts != null && comp.nameTexts.Length == 4 && comp.nameTexts[i] != null
                    && comp.rankRenderers != null && comp.rankRenderers.Length == 4 && comp.rankRenderers[i] != null
                    && comp.baseRenderers != null && comp.baseRenderers.Length == 4 && comp.baseRenderers[i] != null;
                if (!ok) { Debug.LogError("[ValidateSeatNamePlates] " + p + "：席" + i + " の参照が欠けている"); ++bad; }
            }
            if (comp.rankEmblems == null || comp.rankEmblems.Length != 6) { Debug.LogError("[ValidateSeatNamePlates] " + p + "：段位紋章が6枚そろっていない"); ++bad; }
            else { for (int k = 0; k < 6; ++k) { if (comp.rankEmblems[k] == null) { Debug.LogError("[ValidateSeatNamePlates] " + p + "：段位紋章" + k + " が空"); ++bad; } } }
            if (!rt.gameObject.activeSelf) { Debug.LogError("[ValidateSeatNamePlates] " + p + "：本体が非アクティブ（Update が回らない）"); ++bad; }
        }
        if (bad == 0) { Debug.Log("[ValidateSeatNamePlates] OK：" + tables.Length + " 卓すべて配線済み。"); }
        else { Debug.LogError("[ValidateSeatNamePlates] NG：" + bad + " 件。"); }
    }

    // ============================================================
    // ヘルパ
    // ============================================================
    static Material EnsureMaterial(string path, Color color)
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat != null) { return mat; }
        // 牌と同じくシーンのライトを見ない固定色（Quest でも軽い）
        mat = new Material(Shader.Find("Unlit/Color"));
        mat.color = color;
        AssetDatabase.CreateAsset(mat, path);
        AssetDatabase.SaveAssets();
        return mat;
    }

    static Material EnsureTextMaterial(TMP_FontAsset font)
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(TextMatPath);
        if (mat == null)
        {
            mat = new Material(font.material);
            AssetDatabase.CreateAsset(mat, TextMatPath);
        }
        // 毎回フォント側にそろえ直す（アトラスが作り直されても追従する）
        mat.shader = font.material.shader;
        mat.SetTexture("_MainTex", font.atlasTexture);
        if (mat.HasProperty("_FaceDilate")) { mat.SetFloat("_FaceDilate", TextDilate); }
        if (mat.HasProperty("_OutlineSoftness")) { mat.SetFloat("_OutlineSoftness", TextSoftness); }
        EditorUtility.SetDirty(mat);
        AssetDatabase.SaveAssets();
        return mat;
    }

    static GameObject EnsureQuad(Transform parent, string name, Material mat)
    {
        var t = parent.Find(name);
        GameObject go;
        if (t != null) { go = t.gameObject; }
        else
        {
            go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            go.transform.SetParent(parent, false);
        }
        var col = go.GetComponent<Collider>();
        if (col != null) { Object.DestroyImmediate(col); }   // 操作しない表示専用（手牌のクリックを邪魔しない）
        var mr = go.GetComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        return go;
    }

    static TextMeshPro EnsureTmp(Transform parent, string name, TMP_FontAsset font, Material textMat)
    {
        var t = parent.Find(name);
        GameObject go;
        if (t != null) { go = t.gameObject; }
        else
        {
            go = new GameObject(name, typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            go.transform.SetParent(parent, false);
        }
        var tmp = go.GetComponent<TextMeshPro>();
        if (tmp == null) { tmp = go.AddComponent<TextMeshPro>(); }
        tmp.font = font;
        tmp.fontSharedMaterial = textMat;
        tmp.color = Color.white;
        tmp.enableWordWrapping = false;
        tmp.raycastTarget = false;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;
        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }
        return tmp;
    }

    static bool EnsureProgramAsset()
    {
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(ScriptPath);
        if (script == null) { Debug.LogError("[BuildSeatNamePlates] スクリプトが見つかりません: " + ScriptPath); return false; }
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
        if (proxy != null)
        {
            if (UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy) != null) { return proxy; }
            Object.DestroyImmediate(proxy);
            foreach (var ub in go.GetComponents<UdonBehaviour>())
            {
                if (ub.programSource == null) { Object.DestroyImmediate(ub); }
            }
        }
        var added = (UdonSharpBehaviour)UdonSharpUndo.AddComponent(go, type);
        if (added != null && UdonSharpEditorUtility.GetBackingUdonBehaviour(added) == null)
        {
            Object.DestroyImmediate(added);
            return null;
        }
        return added;
    }

    static GameObject EnsureChild(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null) { return t.gameObject; }
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Create " + name);
        go.transform.SetParent(parent, false);
        return go;
    }

    static string GetPath(Transform t)
    {
        var path = t.name;
        while (t.parent != null) { t = t.parent; path = t.name + "/" + path; }
        return path;
    }
}
#endif
