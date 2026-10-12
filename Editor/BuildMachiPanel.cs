#if UNITY_EDITOR
// 聴牌時の待ち牌パネル（雀魂スタイル）の骨格生成と配線。
//   Tools > Kemono Mahjong > Build Machi Panels (Prefab) … Prefab にリグを生成
//   Tools > Kemono Mahjong > Wire Machi Panels (Prefab)  … U#追加＋参照割り当て
//   Tools > Kemono Mahjong > Validate Machi Panels       … 参照欠損の洗い出し
//
// 卓は KemonoMahjong.prefab のインスタンスで、演出リグは Prefab 側に置くのが流儀。
// FindObjectsOfType は Prefab Mode を開いても Prefab 側の卓を拾わないため、
// 通常の Build を作らず Prefab 直接版だけにしてある（インスタンスに override を積まないため）。
//
// 待ち牌そのもの（実物の牌）は Mahjong.cs が従来どおり置く。ここが作るのは
// 背面のパネルと枚数バッジだけ。
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using SuzuFactory.AozoraMahjong;
using VRC.Udon;

public static class BuildMachiPanel
{
    const string RootName = "MachiPanel";
    const string ProgramAssetDir = "Assets/Mahjong/scene_UdonProgramSources";
    const string ProgramAssetPath = ProgramAssetDir + "/MahjongMachiPanel Udon C# Program Asset.asset";
    const string ScriptPath = "Assets/Mahjong/Sources/MahjongMachiPanel.cs";
    const string TablePrefabPath = "Assets/Mahjong/Prefabas/KemonoMahjong.prefab";
    const string FontPath = "Assets/Mahjong/Fonts/NotoSansJP-Bold.ttf";

    const string TexDir = "Assets/Mahjong/Textures/UI";
    const string TexPanel = TexDir + "/machi_panel.png";
    const string TexBadge = TexDir + "/machi_badge.png";
    const string TexOrnament = TexDir + "/machi_ornament.png";

    // 待ちの最大は国士無双十三面待ちの13。
    const int MaxSlots = 13;

    // Canvas は 1px = 0.1mm。牌幅0.0212m=212px、牌高0.029m=290px。
    const float CanvasScale = 0.0001f;
    static readonly Vector2 CanvasSize = new Vector2(4000f, 1400f);

    const int BadgeFontSize = 54;

    // ============================================================
    // Build
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Build Machi Panels (Prefab)")]
    public static void BuildPrefab()
    {
        EnsureTextures();

        var stage = UnityEditor.SceneManagement.PrefabStageUtility.OpenPrefab(TablePrefabPath);
        if (stage == null)
        {
            Debug.LogError("[BuildMachiPanel] Prefab を開けません: " + TablePrefabPath);
            return;
        }

        var root = stage.prefabContentsRoot;
        if (root.GetComponent<Mahjong>() == null)
        {
            Debug.LogError("[BuildMachiPanel] Prefab のルートに Mahjong がありません。");
            return;
        }

        BuildOne(root.transform);

        EditorSceneManager.MarkSceneDirty(stage.scene);
        PrefabUtility.SaveAsPrefabAsset(root, stage.assetPath);
        AssetDatabase.SaveAssets();
        stage.ClearDirtiness();
        UnityEditor.SceneManagement.StageUtility.GoToMainStage();

        Debug.Log("[BuildMachiPanel] Prefab に待ち牌パネルを生成/更新しました。"
            + " 次に Wire Machi Panels (Prefab) を実行してください。");
    }

    static void BuildOne(Transform tableRoot)
    {
        var root = EnsureChild(tableRoot, RootName);
        // スクリプトが載るルートは常時アクティブ（非アクティブなU#は外から叩けない）。
        root.transform.localPosition = Vector3.zero;
        root.transform.localRotation = Quaternion.identity;
        root.transform.localScale = Vector3.one;
        root.SetActive(true);

        var font = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
        if (font == null) { font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }

        var panelRoot = EnsureChild(root.transform, "PanelRoot");
        panelRoot.transform.localPosition = Vector3.zero;
        panelRoot.transform.localRotation = Quaternion.identity;
        panelRoot.transform.localScale = Vector3.one;

        var canvasGo = EnsureUiChild(panelRoot.transform, "Canvas");
        var canvas = canvasGo.GetComponent<Canvas>();
        if (canvas == null) { canvas = canvasGo.AddComponent<Canvas>(); }
        canvas.renderMode = RenderMode.WorldSpace;

        var scaler = canvasGo.GetComponent<CanvasScaler>();
        if (scaler == null) { scaler = canvasGo.AddComponent<CanvasScaler>(); }
        scaler.dynamicPixelsPerUnit = 2f;

        var raycaster = canvasGo.GetComponent<GraphicRaycaster>();
        if (raycaster != null) { Object.DestroyImmediate(raycaster); }

        var canvasRt = canvasGo.GetComponent<RectTransform>();
        canvasRt.localPosition = Vector3.zero;
        canvasRt.localRotation = Quaternion.identity;
        canvasRt.localScale = new Vector3(CanvasScale, CanvasScale, CanvasScale);
        canvasRt.sizeDelta = CanvasSize;

        // 描画順＝ヒエラルキー順。縁 → 地 → 装飾 → バッジ。
        EnsureSliced(canvasRt, "Border", TexPanel);
        EnsureSliced(canvasRt, "Fill", TexPanel);
        EnsureOrnament(canvasRt, "Ornament0", false);
        EnsureOrnament(canvasRt, "Ornament1", true);

        for (int i = 0; i < MaxSlots; ++i)
        {
            BuildBadge(canvasRt, i, font);
        }

        BuildFuriten(canvasRt, font);
        BuildHandFuriten(canvasRt, font);

        var keep = new System.Collections.Generic.List<string>
        { "Border", "Fill", "Ornament0", "Ornament1" };
        for (int i = 0; i < MaxSlots; ++i) { keep.Add("Badge" + i); }
        keep.Add("Furiten");
        keep.Add("HandFuriten");
        CleanupChildren(canvasRt, keep.ToArray());
        for (int k = 0; k < keep.Count; ++k)
        {
            var c = canvasRt.Find(keep[k]);
            if (c != null) { c.SetSiblingIndex(k); }
        }

        CleanupChildren(panelRoot.transform, new[] { "Canvas" });
        panelRoot.SetActive(false);
    }

    static Image EnsureSliced(Transform parent, string name, string spritePath)
    {
        var go = EnsureUiChild(parent, name);
        var img = go.GetComponent<Image>();
        if (img == null) { img = go.AddComponent<Image>(); }
        img.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
        img.type = Image.Type.Sliced;
        img.raycastTarget = false;
        img.color = Color.white;

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(600f, 400f);
        rt.localScale = Vector3.one;
        rt.localRotation = Quaternion.identity;
        return img;
    }

    static RawImage EnsureOrnament(Transform parent, string name, bool flip)
    {
        var go = EnsureUiChild(parent, name);
        var raw = go.GetComponent<RawImage>();
        if (raw == null) { raw = go.AddComponent<RawImage>(); }
        raw.texture = AssetDatabase.LoadAssetAtPath<Texture2D>(TexOrnament);
        raw.raycastTarget = false;
        raw.color = Color.white;

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(190f, 190f);
        rt.localScale = Vector3.one;
        // 右下側は反転させて左右対称に見せる。
        rt.localRotation = Quaternion.Euler(0f, 0f, flip ? 180f : 0f);
        return raw;
    }

    // 振り聴ラベル。パネル上辺の中央にまたがるように置く（位置は実行時に決まる）。
    static void BuildFuriten(Transform parent, Font font)
    {
        var go = EnsureUiChild(parent, "Furiten");

        var img = go.GetComponent<Image>();
        if (img == null) { img = go.AddComponent<Image>(); }
        img.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(TexBadge);
        img.type = Image.Type.Sliced;
        img.raycastTarget = false;
        img.color = Color.white;

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(230f, 72f);
        rt.localScale = Vector3.one;
        rt.localRotation = Quaternion.identity;

        var textGo = EnsureUiChild(go.transform, "Text");
        var text = textGo.GetComponent<Text>();
        if (text == null) { text = textGo.AddComponent<Text>(); }
        if (font != null) { text.font = font; }
        text.fontSize = 46;
        text.alignment = TextAnchor.MiddleCenter;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.resizeTextForBestFit = false;
        text.raycastTarget = false;
        text.text = "振り聴";
        text.color = Color.white;

        var trt = textGo.GetComponent<RectTransform>();
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero;
        trt.offsetMax = Vector2.zero;
        trt.localScale = Vector3.one;
        trt.localRotation = Quaternion.identity;

        CleanupChildren(go.transform, new[] { "Text" });
        go.SetActive(false);
    }

    // 手牌の左端に出す縦書きの振り聴ラベル。位置は実行時にワールド座標で決まる。
    static void BuildHandFuriten(Transform parent, Font font)
    {
        var go = EnsureUiChild(parent, "HandFuriten");

        var img = go.GetComponent<Image>();
        if (img == null) { img = go.AddComponent<Image>(); }
        img.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(TexBadge);
        img.type = Image.Type.Sliced;
        img.raycastTarget = false;
        img.color = Color.white;

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(150f, 430f);
        rt.localScale = Vector3.one;
        rt.localRotation = Quaternion.identity;

        var textGo = EnsureUiChild(go.transform, "Text");
        var text = textGo.GetComponent<Text>();
        if (text == null) { text = textGo.AddComponent<Text>(); }
        if (font != null) { text.font = font; }
        text.fontSize = 74;
        text.lineSpacing = 0.92f;
        text.alignment = TextAnchor.MiddleCenter;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.resizeTextForBestFit = false;
        text.raycastTarget = false;
        text.text = "振\nり\n聴";   // 1文字ずつ縦1行に積む
        text.color = Color.white;

        var trt = textGo.GetComponent<RectTransform>();
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero;
        trt.offsetMax = Vector2.zero;
        trt.localScale = Vector3.one;
        trt.localRotation = Quaternion.identity;

        CleanupChildren(go.transform, new[] { "Text" });
        go.SetActive(false);
    }

    static void BuildBadge(Transform parent, int index, Font font)
    {
        var go = EnsureUiChild(parent, "Badge" + index);

        var img = go.GetComponent<Image>();
        if (img == null) { img = go.AddComponent<Image>(); }
        img.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(TexBadge);
        img.type = Image.Type.Sliced;
        img.raycastTarget = false;
        img.color = Color.white;

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(190f, 92f);
        rt.localScale = Vector3.one;
        rt.localRotation = Quaternion.identity;

        var textGo = EnsureUiChild(go.transform, "Text");
        var text = textGo.GetComponent<Text>();
        if (text == null) { text = textGo.AddComponent<Text>(); }
        if (font != null) { text.font = font; }
        text.fontSize = BadgeFontSize;
        text.alignment = TextAnchor.MiddleCenter;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.resizeTextForBestFit = false;
        text.raycastTarget = false;
        text.text = "4枚";
        text.color = Color.white;

        var trt = textGo.GetComponent<RectTransform>();
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero;
        trt.offsetMax = Vector2.zero;
        trt.localScale = Vector3.one;
        trt.localRotation = Quaternion.identity;

        CleanupChildren(go.transform, new[] { "Text" });
        go.SetActive(false);
    }

    // ============================================================
    // Wire
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Wire Machi Panels (Prefab)")]
    public static void WirePrefab()
    {
        if (EnsureProgramAsset(ScriptPath, ProgramAssetPath))
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[WireMachiPanel] U#プログラムアセットを生成。コンパイル後にもう一度実行してください。");
            return;
        }

        var stage = UnityEditor.SceneManagement.PrefabStageUtility.OpenPrefab(TablePrefabPath);
        if (stage == null)
        {
            Debug.LogError("[WireMachiPanel] Prefab を開けません: " + TablePrefabPath);
            return;
        }

        var root = stage.prefabContentsRoot;
        var rootT = root.transform.Find(RootName);
        if (rootT == null)
        {
            Debug.LogError("[WireMachiPanel] Prefab に " + RootName + " がありません。先に Build してください。");
            return;
        }

        var comp = (MahjongMachiPanel)EnsureUSharpComponent(rootT.gameObject, typeof(MahjongMachiPanel));
        if (comp == null) { return; }

        var panelRoot = rootT.Find("PanelRoot");
        var canvas = panelRoot != null ? panelRoot.Find("Canvas") : null;
        if (canvas == null)
        {
            Debug.LogError("[WireMachiPanel] UI階層が不足しています。Build をやり直してください。");
            return;
        }

        comp.panelRoot = panelRoot.gameObject;
        comp.canvasRect = canvas as RectTransform;
        comp.borderImage = FindImage(canvas, "Border");
        comp.borderRect = FindRect(canvas, "Border");
        comp.fillImage = FindImage(canvas, "Fill");
        comp.fillRect = FindRect(canvas, "Fill");

        comp.ornamentImages = new RawImage[]
        {
            FindRaw(canvas, "Ornament0"), FindRaw(canvas, "Ornament1")
        };
        comp.ornamentRects = new RectTransform[]
        {
            FindRect(canvas, "Ornament0"), FindRect(canvas, "Ornament1")
        };

        var roots = new GameObject[MaxSlots];
        var rects = new RectTransform[MaxSlots];
        var texts = new Text[MaxSlots];
        var bgs = new Image[MaxSlots];
        for (int i = 0; i < MaxSlots; ++i)
        {
            var b = canvas.Find("Badge" + i);
            if (b == null) { continue; }
            roots[i] = b.gameObject;
            rects[i] = b as RectTransform;
            bgs[i] = b.GetComponent<Image>();
            var t = b.Find("Text");
            texts[i] = t != null ? t.GetComponent<Text>() : null;
        }
        comp.badgeRoots = roots;
        comp.badgeRects = rects;
        comp.badgeTexts = texts;
        comp.badgeBackgrounds = bgs;

        var furiten = canvas.Find("Furiten");
        comp.furitenRoot = furiten != null ? furiten.gameObject : null;
        comp.furitenRect = furiten as RectTransform;
        comp.furitenImage = furiten != null ? furiten.GetComponent<Image>() : null;
        var ft = furiten != null ? furiten.Find("Text") : null;
        comp.furitenText = ft != null ? ft.GetComponent<Text>() : null;

        var handFuriten = canvas.Find("HandFuriten");
        comp.handFuritenRoot = handFuriten != null ? handFuriten.gameObject : null;
        comp.handFuritenRect = handFuriten as RectTransform;
        comp.handFuritenImage = handFuriten != null ? handFuriten.GetComponent<Image>() : null;
        var hft = handFuriten != null ? handFuriten.Find("Text") : null;
        comp.handFuritenText = hft != null ? hft.GetComponent<Text>() : null;

        // 見た目の確定値。C#側の既定値を変えてもシーンに載っている値は更新されないので毎回明示する。
        comp.slotPixels = 212f;
        comp.tilePixels = 290f;
        comp.badgeGap = 24f;
        comp.badgeHeight = 92f;
        comp.panelPadding = 58f;
        comp.borderThickness = 7f;
        comp.depthOffset = -0.007f;
        comp.borderColor = new Color(0.85f, 0.62f, 0.30f, 1f);
        comp.fillColor = new Color(0.055f, 0.085f, 0.16f, 0.96f);
        comp.badgeColor = new Color(0.24f, 0.34f, 0.58f, 1f);
        comp.badgeEmptyColor = new Color(0.45f, 0.20f, 0.24f, 1f);
        comp.badgeTextColor = new Color(0.96f, 0.98f, 1f, 1f);
        comp.ornamentColor = new Color(0.86f, 0.60f, 0.28f, 1f);
        comp.badgeYakunashiColor = new Color(0.78f, 0.44f, 0.12f, 1f);
        comp.furitenColor = new Color(0.72f, 0.14f, 0.18f, 1f);
        comp.furitenSize = new Vector2(230f, 72f);
        comp.handFuritenSize = new Vector2(150f, 430f);

        UdonSharpEditorUtility.CopyProxyToUdon(comp);
        EditorUtility.SetDirty(comp);

        var table = root.GetComponent<Mahjong>();
        if (table != null)
        {
            table.machiPanel = comp;
            UdonSharpEditorUtility.CopyProxyToUdon(table);
            EditorUtility.SetDirty(table);
        }

        EditorSceneManager.MarkSceneDirty(stage.scene);
        PrefabUtility.SaveAsPrefabAsset(root, stage.assetPath);
        AssetDatabase.SaveAssets();
        stage.ClearDirtiness();
        UnityEditor.SceneManagement.StageUtility.GoToMainStage();

        VerifyPrefab();
    }

    static void VerifyPrefab()
    {
        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(TablePrefabPath);
        var t = asset != null ? asset.transform.Find(RootName) : null;
        var comp = t != null ? t.GetComponent<MahjongMachiPanel>() : null;
        if (comp == null)
        {
            Debug.LogError("[WireMachiPanel] Prefab に MahjongMachiPanel がありません。");
            return;
        }

        int holes = CountHoles(comp);
        if (holes > 0)
        {
            Debug.LogError("[WireMachiPanel] Prefab の参照欠損 " + holes + " 件。Build→Wire をやり直してください。");
            return;
        }
        Debug.Log("[WireMachiPanel] Prefab を配線しました（参照欠損0）。"
            + " 途中の ArgumentNullException(UdonSharp) は無視して構いません。");
    }

    static int CountHoles(MahjongMachiPanel comp)
    {
        int holes = 0;
        if (comp.panelRoot == null) { ++holes; }
        if (comp.canvasRect == null) { ++holes; }
        if (comp.borderImage == null || comp.borderRect == null) { ++holes; }
        if (comp.fillImage == null || comp.fillRect == null) { ++holes; }
        if (comp.badgeRects == null || comp.badgeRects.Length != MaxSlots) { ++holes; }
        else
        {
            for (int i = 0; i < MaxSlots; ++i)
            {
                if (comp.badgeRects[i] == null || comp.badgeTexts[i] == null
                    || comp.badgeBackgrounds[i] == null) { ++holes; }
            }
        }
        if (comp.furitenRoot == null || comp.furitenRect == null
            || comp.furitenImage == null || comp.furitenText == null) { ++holes; }
        if (comp.handFuritenRoot == null || comp.handFuritenRect == null
            || comp.handFuritenImage == null || comp.handFuritenText == null) { ++holes; }
        if (comp.borderImage != null && comp.borderImage.sprite == null) { ++holes; }
        if (comp.badgeTexts != null && comp.badgeTexts.Length > 0
            && comp.badgeTexts[0] != null && comp.badgeTexts[0].font == null) { ++holes; }
        return holes;
    }

    // シーンの卓 → 自分の MachiPanel への参照を張り直す。
    // リグ自体は Prefab 側にあるので、ここで直すのは卓本体の参照だけ。
    // （卓インスタンスは Mahjong の publicVariables を丸ごと override しているため、
    //   Prefab 側で参照を入れてもインスタンスには降りてこない）
    [MenuItem("Tools/Kemono Mahjong/Wire Machi Panels (Scene)")]
    public static void WireScene()
    {
        var tables = Object.FindObjectsOfType<Mahjong>(true);
        System.Array.Sort(tables, (a, b) => string.CompareOrdinal(GetPath(a.transform), GetPath(b.transform)));
        if (tables.Length == 0)
        {
            Debug.LogError("[WireMachiPanel] Mahjong（卓）が見つかりません。");
            return;
        }

        int wired = 0;
        foreach (var table in tables)
        {
            var t = table.transform.Find(RootName);
            var comp = t != null ? t.GetComponent<MahjongMachiPanel>() : null;
            if (comp == null)
            {
                Debug.LogError("[WireMachiPanel] " + GetPath(table.transform) + " に "
                    + RootName + " がありません。先に Build Machi Panels (Prefab) を実行してください。");
                continue;
            }

            table.machiPanel = comp;
            UdonSharpEditorUtility.CopyProxyToUdon(table);
            EditorUtility.SetDirty(table);
            ++wired;
        }

        if (tables[0].gameObject.scene.IsValid())
        {
            EditorSceneManager.MarkSceneDirty(tables[0].gameObject.scene);
        }
        Debug.Log("[WireMachiPanel] シーン配線完了：" + wired + "/" + tables.Length + " 卓。");
    }

    [MenuItem("Tools/Kemono Mahjong/Validate Machi Panels")]
    public static void Validate()
    {
        var tables = Object.FindObjectsOfType<Mahjong>(true);
        int ok = 0, bad = 0;
        foreach (var table in tables)
        {
            var comp = table.machiPanel;
            if (comp == null || CountHoles(comp) > 0)
            {
                Debug.LogError("[ValidateMachiPanel] " + GetPath(table.transform) + " : 未配線または参照欠損。");
                ++bad;
            }
            else { ++ok; }
        }
        Debug.Log("[ValidateMachiPanel] 完了：正常 " + ok + " 卓 / 要修正 " + bad + " 卓。");
    }

    // ============================================================
    // テクスチャ生成
    // ============================================================
    public static void EnsureTextures()
    {
        // パネル：角丸＋左上/右下を斜めに切り落としたかたち（参照デザインと同じ）。
        WriteSpriteIfMissing(TexPanel, 128, 128, MakePanel(128, 128), 44f);
        // バッジ：ただの角丸。
        WriteSpriteIfMissing(TexBadge, 64, 48, MakeRoundRect(64, 48, 14f, 0f, 0f), 18f);
        // 角飾り：肉球（ケモノ麻雀なので紅葉ではなく肉球にした）。
        WriteTextureIfMissing(TexOrnament, 256, 256, MakePaw(256));
    }

    // 角丸＋左上と右下だけ斜めカット。
    static Color[] MakePanel(int w, int h)
    {
        const float radius = 20f;
        const float chamfer = 40f;
        return MakeRoundRect(w, h, radius, chamfer, 1f);
    }

    // 角丸矩形。chamfer>0 なら左上と右下を斜めに落とす。
    static Color[] MakeRoundRect(int w, int h, float radius, float chamfer, float unused)
    {
        var px = new Color[w * h];

        for (int y = 0; y < h; ++y)
        {
            for (int x = 0; x < w; ++x)
            {
                float fx = x + 0.5f;
                float fy = y + 0.5f;

                // 角丸：四隅の円の外なら透明。
                float d = 0f;
                bool inside = true;

                float left = fx, right = w - fx, bottom = fy, top = h - fy;

                if (left < radius && bottom < radius)
                {
                    d = radius - Mathf.Sqrt((radius - left) * (radius - left) + (radius - bottom) * (radius - bottom));
                }
                else if (left < radius && top < radius)
                {
                    d = radius - Mathf.Sqrt((radius - left) * (radius - left) + (radius - top) * (radius - top));
                }
                else if (right < radius && bottom < radius)
                {
                    d = radius - Mathf.Sqrt((radius - right) * (radius - right) + (radius - bottom) * (radius - bottom));
                }
                else if (right < radius && top < radius)
                {
                    d = radius - Mathf.Sqrt((radius - right) * (radius - right) + (radius - top) * (radius - top));
                }
                else
                {
                    d = Mathf.Min(Mathf.Min(left, right), Mathf.Min(bottom, top));
                }

                // 斜めカット（左上・右下）。
                if (chamfer > 0f)
                {
                    float tl = left + top - chamfer;        // 左上：x + (h-y) が小さいほど角
                    float br = right + bottom - chamfer;    // 右下
                    d = Mathf.Min(d, Mathf.Min(tl, br));
                }

                float a = Mathf.Clamp01(d);
                if (!inside) { a = 0f; }
                px[y * w + x] = new Color(1f, 1f, 1f, a);
            }
        }

        return px;
    }

    // 肉球のシルエット。指球4つ＋掌球1つ。
    static Color[] MakePaw(int size)
    {
        var px = new Color[size * size];

        // (中心x, 中心y, 半径x, 半径y) すべて 0..1 の正規化座標。
        float[] cx = { 0.22f, 0.40f, 0.60f, 0.78f, 0.50f };
        float[] cy = { 0.66f, 0.80f, 0.80f, 0.66f, 0.34f };
        float[] rx = { 0.11f, 0.12f, 0.12f, 0.11f, 0.26f };
        float[] ry = { 0.15f, 0.16f, 0.16f, 0.15f, 0.22f };

        for (int y = 0; y < size; ++y)
        {
            float v = (y + 0.5f) / size;
            for (int x = 0; x < size; ++x)
            {
                float u = (x + 0.5f) / size;

                float a = 0f;
                for (int i = 0; i < cx.Length; ++i)
                {
                    float dx = (u - cx[i]) / rx[i];
                    float dy = (v - cy[i]) / ry[i];
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    if (d >= 1f) { continue; }

                    float blob = Mathf.Clamp01((1f - d) / 0.12f);
                    if (blob > a) { a = blob; }
                }
                px[y * size + x] = new Color(1f, 1f, 1f, a);
            }
        }

        return px;
    }

    // ============================================================
    // ヘルパ
    // ============================================================
    static void WriteSpriteIfMissing(string path, int w, int h, Color[] pixels, float border)
    {
        if (AssetDatabase.LoadAssetAtPath<Sprite>(path) != null) { return; }

        WritePng(path, w, h, pixels);

        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer != null)
        {
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.mipmapEnabled = false;
            // 9スライスの境界。横に伸ばしても角が潰れない。
            importer.spriteBorder = new Vector4(border, border, border, border);
            importer.SaveAndReimport();
        }
    }

    static void WriteTextureIfMissing(string path, int w, int h, Color[] pixels)
    {
        if (AssetDatabase.LoadAssetAtPath<Texture2D>(path) != null) { return; }

        WritePng(path, w, h, pixels);

        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer != null)
        {
            importer.textureType = TextureImporterType.Default;
            importer.alphaIsTransparency = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.mipmapEnabled = true;
            importer.SaveAndReimport();
        }
    }

    static void WritePng(string path, int w, int h, Color[] pixels)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tex.SetPixels(pixels);
        tex.Apply();
        var png = tex.EncodeToPNG();
        Object.DestroyImmediate(tex);
        System.IO.File.WriteAllBytes(path, png);
        AssetDatabase.ImportAsset(path);
    }

    static Image FindImage(Transform parent, string name)
    {
        var t = parent != null ? parent.Find(name) : null;
        return t != null ? t.GetComponent<Image>() : null;
    }

    static RawImage FindRaw(Transform parent, string name)
    {
        var t = parent != null ? parent.Find(name) : null;
        return t != null ? t.GetComponent<RawImage>() : null;
    }

    static RectTransform FindRect(Transform parent, string name)
    {
        var t = parent != null ? parent.Find(name) : null;
        return t as RectTransform;
    }

    static bool EnsureProgramAsset(string scriptPath, string assetPath)
    {
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
        if (script == null)
        {
            Debug.LogError("[WireMachiPanel] スクリプトが見つかりません: " + scriptPath);
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
            Debug.LogError("[WireMachiPanel] " + type.Name + " の追加に失敗（プログラムアセット未コンパイル？）。");
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

    static GameObject EnsureUiChild(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null)
        {
            if (t.GetComponent<RectTransform>() == null) { Undo.DestroyObjectImmediate(t.gameObject); }
            else { return t.gameObject; }
        }
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Create " + name);
        go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);
        return go;
    }

    static void CleanupChildren(Transform parent, string[] keepNames)
    {
        var keep = new System.Collections.Generic.HashSet<string>(keepNames);
        for (int i = parent.childCount - 1; i >= 0; --i)
        {
            var c = parent.GetChild(i);
            if (!keep.Contains(c.name)) { Undo.DestroyObjectImmediate(c.gameObject); }
        }
    }

    static string GetPath(Transform t)
    {
        var path = t.name;
        while (t.parent != null) { t = t.parent; path = t.name + "/" + path; }
        return path;
    }
}
#endif
