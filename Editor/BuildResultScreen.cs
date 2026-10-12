#if UNITY_EDITOR
// 終局結果画面（雀魂スタイルの順位表）の骨格生成と配線。
//   Tools > Kemono Mahjong > Build Result Screens … 全卓に ResultScreen/PanelRoot/Canvas/Content/Row0..3 を冪等生成
//   Tools > Kemono Mahjong > Wire Result Screens  … U#コンポーネント追加＋参照割り当て＋卓への逆参照
// どちらも2回実行しても安全（冪等）。レイアウトを変えたい場合は下の定数を変えて Build を
// 再実行する（手でシーンをいじった調整は Build 再実行で上書きされる）。
//
// Prefab Mode（KemonoMahjong.prefab を開いた状態）でも、通常シーンでも動く。
// 卓は Object.FindObjectsOfType<Mahjong>(true) で拾う。
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using SuzuFactory.AozoraMahjong;
using VRC.Udon;

public static class BuildResultScreen
{
    const string RootName = "ResultScreen";
    const string FontPath = "Assets/Mahjong/Fonts/NotoSansJP-Bold.ttf";
    const string ProgramAssetDir = "Assets/Mahjong/scene_UdonProgramSources";

    static readonly string[] CharaPortraitPaths =
    {
        "Assets/Mahjong/Textures/UI/Characters/vol_mascot_pic.png",
        "Assets/Mahjong/Textures/UI/Characters/Shine_mascot_pic.png",
        "Assets/Mahjong/Textures/UI/Characters/Tsumuntema_mascot_pic.png"
    };

    // 大段位アイコン（0=初獣 … 5=天獣）。ネームプレート／メインメニューと同じ素材。
    static readonly string[] RankIconPaths =
    {
        "Assets/Mahjong/Textures/UI/rank/初獣.png",
        "Assets/Mahjong/Textures/UI/rank/鋭爪.png",
        "Assets/Mahjong/Textures/UI/rank/剛牙.png",
        "Assets/Mahjong/Textures/UI/rank/獣威.png",
        "Assets/Mahjong/Textures/UI/rank/幻獣.png",
        "Assets/Mahjong/Textures/UI/rank/天獣.png"
    };

    // 卓面からの高さ。あがり画面（AgariScreen y=0.55）と同じ高さに置く。
    // 両者は gameState で排他（RESULT ではあがり画面は出ない）なので重なっても問題ない。
    const float PanelHeight = 0.55f;
    // Canvas は あがり画面と同じ 1920x1080 / scale 0.00073。
    // 実行時は panelRoot が 0.7 倍されるので、実寸は 0.981m x 0.552m になる。
    // **卓上パネルは全部この実寸・中心Y 0.045 に揃えてある。片方だけ変えないこと。**
    // ※Canvas 直下の Content だけがストレッチ配置で、その下は全て中央アンカー。
    //   よって Canvas の矩形を変えても子はリフローしない（Content の矩形が変わるだけ）。
    static readonly Vector2 CanvasSize = new Vector2(1920f, 1080f);
    const float CanvasScale = 0.00073f;

    // 行のサイズと配置（Canvas座標）。
    static readonly Vector2 RowSize = new Vector2(1180f, 150f);
    const float RowTopY = 170f;      // 1位の行のY
    const float RowStepY = -160f;    // 行間（下方向）
    const float RowStepX = 40f;      // 雀魂風に下位ほど右へずらす
    const float RowBaseX = -60f;     // 1位の行のX
    const float HeaderY = 330f;

    // キャラ絵の元画像は 700x1080。上端から 700x700 を切り出して使う（あがり画面と同じ）。
    const float PortraitSourceHeight = 1080f;
    const float PortraitCropSize = 700f;

    static readonly Color TopRowColor = new Color(0.62f, 0.13f, 0.18f, 0.92f);
    static readonly Color NormalRowColor = new Color(0.10f, 0.16f, 0.30f, 0.88f);
    static readonly Color SelfTagColor = new Color(0.78f, 0.16f, 0.20f, 0.95f);
    static readonly Color SubTextColor = new Color(0.78f, 0.80f, 0.86f);

    // ============================================================
    // Build：骨格生成
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Build Result Screens")]
    public static void Build()
    {
        var font = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
        if (font == null)
        {
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            Debug.LogWarning("[BuildResultScreen] 日本語フォントが見つかりません（" + FontPath + "）。LegacyRuntime で生成します。");
        }

        var tables = Object.FindObjectsOfType<Mahjong>(true);
        System.Array.Sort(tables, (a, b) => string.CompareOrdinal(GetPath(a.transform), GetPath(b.transform)));
        if (tables.Length == 0)
        {
            Debug.LogError("[BuildResultScreen] Mahjong（卓）が見つかりません。シーンを開くか、卓のPrefab Modeで実行してください。");
            return;
        }

        foreach (var table in tables)
        {
            BuildOne(table.transform, font);
        }

        MarkDirty(tables[0].gameObject);
        Debug.Log("[BuildResultScreen] 完了：" + tables.Length + " 卓に結果画面を生成/更新。次に Wire Result Screens を実行してください。");
    }

    static void BuildOne(Transform tableRoot, Font font)
    {
        // スクリプトが載るルートは常時アクティブを厳守（非アクティブなU#は外から叩けない）。
        // 表示ON/OFFは子の PanelRoot でのみ行う。
        var root = EnsureChild(tableRoot, RootName);
        root.transform.localPosition = new Vector3(0f, PanelHeight, 0f);
        root.transform.localRotation = Quaternion.identity;
        root.transform.localScale = Vector3.one;
        root.SetActive(true);

        var panelRoot = EnsureChild(root.transform, "PanelRoot");
        panelRoot.transform.localRotation = Quaternion.identity;

        // **エディタ上の見た目を実行時に一致させるための姿勢。**
        //  MahjongResultScreen.cs は表示するとき panelRoot へ
        //    localPosition = Vector3.down * 0.20 / localScale = Vector3.one * 0.7
        //  を「代入」し、そのまま元へ戻さない（入場アニメが動かすのは contentRect の方）。
        //  代入なので最初から同じ値にしておけば冪等で、
        //  シーンビューで見た大きさ・位置がそのまま実機の見え方になる。
        //  → 実効 0.715m x 0.460m / 卓ルートからの中心 Y 0.35。
        //  ※ここを Vector3.zero / Vector3.one に戻すと、エディタでは大きく見えるのに
        //    実機では小さく低く出る、という食い違いが復活する。
        panelRoot.transform.localPosition = Vector3.down * 0.20f;
        panelRoot.transform.localScale = Vector3.one * 0.7f;

        // ---- Canvas（World Space 必須。Screen Space Overlay は VRChat で描画されない）----
        var canvasGo = EnsureUiChild(panelRoot.transform, "Canvas");
        var canvas = canvasGo.GetComponent<Canvas>();
        if (canvas == null) { canvas = canvasGo.AddComponent<Canvas>(); }
        canvas.renderMode = RenderMode.WorldSpace;

        var scaler = canvasGo.GetComponent<CanvasScaler>();
        if (scaler == null) { scaler = canvasGo.AddComponent<CanvasScaler>(); }
        scaler.dynamicPixelsPerUnit = 2f;

        // 操作しない表示専用。GraphicRaycaster は付けない。
        var raycaster = canvasGo.GetComponent<GraphicRaycaster>();
        if (raycaster != null) { Object.DestroyImmediate(raycaster); }

        var canvasRt = canvasGo.GetComponent<RectTransform>();
        canvasRt.localPosition = Vector3.zero;
        canvasRt.localRotation = Quaternion.identity;
        canvasRt.localScale = new Vector3(CanvasScale, CanvasScale, CanvasScale);
        canvasRt.sizeDelta = CanvasSize;

        // ---- Content（フェード/スケールアニメの対象）----
        var content = EnsureUiChild(canvasGo.transform, "Content");
        var contentRt = content.GetComponent<RectTransform>();
        StretchFull(contentRt);
        contentRt.localScale = Vector3.one;
        if (content.GetComponent<CanvasGroup>() == null) { content.AddComponent<CanvasGroup>(); }

        // ---- 背景（順位表全体の下敷き）----
        var bg = EnsureUiChild(content.transform, "Backdrop");
        var bgImage = bg.GetComponent<Image>();
        if (bgImage == null) { bgImage = bg.AddComponent<Image>(); }
        bgImage.color = new Color(0.02f, 0.03f, 0.06f, 0.55f);
        bgImage.raycastTarget = false;
        var bgRt = bg.GetComponent<RectTransform>();
        bgRt.anchorMin = new Vector2(0.5f, 0.5f);
        bgRt.anchorMax = new Vector2(0.5f, 0.5f);
        bgRt.anchoredPosition = new Vector2(0f, 0f);
        bgRt.sizeDelta = CanvasSize;   // Backdrop = Canvas と同寸
        bg.transform.SetAsFirstSibling();

        // ---- 見出し「終局」----
        var header = EnsureText(content.transform, "Header", font, 84, new Vector2(0f, HeaderY), new Vector2(600f, 120f), "終局");
        header.alignment = TextAnchor.MiddleCenter;

        for (int i = 0; i < 4; ++i)
        {
            BuildRow(content.transform, i, font);
        }

        BuildRankPanel(content.transform, font);

        EnsureUnlockNotice(content.transform, font);

        CleanupChildren(content.transform,
            new string[] { "Backdrop", "Header", "Row0", "Row1", "Row2", "Row3", "RankPanel", UnlockNoticeName });

        // 対局前・編集中に見えっぱなしにならないよう畳んでおく（実行時は Start でも畳まれる）。
        panelRoot.SetActive(false);
    }

    // ---- チュンビーム解放のお知らせ（順位表の下）----
    // MahjongResultScreen が "Canvas/Content/UnlockNotice" を名前で引く（配線しない）。
    // 最下段の行（Row3）の下端は 170 - 3*160 - 75 = -385。その下の -395..-505 に2行まで入る。
    const string UnlockNoticeName = "UnlockNotice";
    const string KemonoMahjongPrefabPath = "Assets/Mahjong/Prefabas/KemonoMahjong.prefab";
    static readonly Color UnlockNoticeColor = new Color(1.0f, 0.84f, 0.30f, 1.0f);

    static void EnsureUnlockNotice(Transform content, Font font)
    {
        var notice = EnsureText(content, UnlockNoticeName, font, 44, new Vector2(0f, -450f), new Vector2(1500f, 110f), "");
        notice.color = UnlockNoticeColor;
        notice.alignment = TextAnchor.MiddleCenter;
        var outline = notice.GetComponent<Outline>();
        if (outline == null) { outline = notice.gameObject.AddComponent<Outline>(); }
        outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
        outline.effectDistance = new Vector2(2f, -2f);
        // 出し入れは実行時に MahjongResultScreen が行う。
        notice.gameObject.SetActive(false);
    }

    // 卓Prefabの結果画面へ、お知らせ欄だけを足す（結果画面全体は作り直さない）。
    // シーンの8卓は Prefab から継承する。
    [MenuItem("Tools/Kemono Mahjong/Add Result Unlock Notice (Prefab)")]
    public static void AddUnlockNoticeToPrefab()
    {
        var font = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
        if (font == null) { Debug.LogError("[BuildResultScreen] フォントが見つかりません: " + FontPath); return; }

        var root = PrefabUtility.LoadPrefabContents(KemonoMahjongPrefabPath);
        try
        {
            var content = root.transform.Find(RootName + "/PanelRoot/Canvas/Content");
            if (content == null)
            {
                Debug.LogError("[BuildResultScreen] Prefab に " + RootName + "/PanelRoot/Canvas/Content がありません。");
                return;
            }
            EnsureUnlockNotice(content, font);
            PrefabUtility.SaveAsPrefabAsset(root, KemonoMahjongPrefabPath);
            Debug.Log("[BuildResultScreen] Prefab に UnlockNotice を追加しました。");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    // お知らせ欄が全卓（シーン）に届いているかを確認する。
    [MenuItem("Tools/Kemono Mahjong/Validate Result Unlock Notice")]
    public static void ValidateUnlockNotice()
    {
        var screens = Object.FindObjectsOfType<MahjongResultScreen>(true);
        int ok = 0;
        foreach (var s in screens)
        {
            var t = s.panelRoot != null ? s.panelRoot.transform.Find("Canvas/Content/" + UnlockNoticeName) : null;
            if (t != null && t.GetComponent<Text>() != null) { ++ok; }
            else { Debug.LogError("[BuildResultScreen] UnlockNotice が無い: " + GetPath(s.transform)); }
        }
        Debug.Log("[BuildResultScreen] UnlockNotice 検証: " + ok + "/" + screens.Length + " 卓 OK");
    }

    // 1行ぶん（順位・キャラ絵・名前・点数・素点差・PT・自家タグ）。
    // 行ルートの anchoredPosition は MahjongResultScreen がスライドアニメの基準として
    // キャッシュするので、行の中身は「行ルート内の相対座標」で置くこと。
    static void BuildRow(Transform content, int index, Font font)
    {
        var row = EnsureUiChild(content, "Row" + index);
        var rowRt = row.GetComponent<RectTransform>();
        rowRt.anchorMin = new Vector2(0.5f, 0.5f);
        rowRt.anchorMax = new Vector2(0.5f, 0.5f);
        rowRt.pivot = new Vector2(0.5f, 0.5f);
        rowRt.anchoredPosition = new Vector2(RowBaseX + index * RowStepX, RowTopY + index * RowStepY);
        rowRt.sizeDelta = RowSize;
        rowRt.localScale = Vector3.one;

        // 背景（1位だけ赤。実行時にも MahjongResultScreen が同じ色を入れる）
        var bg = EnsureUiChild(row.transform, "Bg");
        var bgImage = bg.GetComponent<Image>();
        if (bgImage == null) { bgImage = bg.AddComponent<Image>(); }
        bgImage.color = index == 0 ? TopRowColor : NormalRowColor;
        bgImage.raycastTarget = false;
        StretchFull(bg.GetComponent<RectTransform>());
        bg.transform.SetAsFirstSibling();

        // 順位「1」＋「位」。行の左端（x=-590）からはみ出さない位置に置く。
        var rank = EnsureText(row.transform, "RankText", font, 92, new Vector2(-525f, 4f), new Vector2(110f, 120f), (index + 1).ToString());
        rank.alignment = TextAnchor.MiddleCenter;
        var rankUnit = EnsureText(row.transform, "RankUnit", font, 32, new Vector2(-472f, -24f), new Vector2(56f, 44f), "位");
        rankUnit.alignment = TextAnchor.MiddleLeft;
        rankUnit.color = SubTextColor;

        // キャラ絵（上端 700x700 を切り出し）
        var portrait = EnsureUiChild(row.transform, "Portrait");
        var raw = portrait.GetComponent<RawImage>();
        if (raw == null) { raw = portrait.AddComponent<RawImage>(); }
        raw.color = Color.white;
        raw.raycastTarget = false;
        raw.uvRect = new Rect(0f, (PortraitSourceHeight - PortraitCropSize) / PortraitSourceHeight,
                              1f, PortraitCropSize / PortraitSourceHeight);
        var portraitRt = portrait.GetComponent<RectTransform>();
        portraitRt.anchorMin = new Vector2(0.5f, 0.5f);
        portraitRt.anchorMax = new Vector2(0.5f, 0.5f);
        portraitRt.anchoredPosition = new Vector2(-350f, 0f);
        portraitRt.sizeDelta = new Vector2(124f, 124f);

        // 名前（左寄せ）
        var name = EnsureText(row.transform, "NameText", font, 38, new Vector2(-262f, 40f), new Vector2(470f, 52f), "Player");
        name.alignment = TextAnchor.MiddleLeft;
        SetPivotLeft(name.rectTransform);

        // 最終点数（大きく左寄せ）
        var score = EnsureText(row.transform, "ScoreText", font, 74, new Vector2(-262f, -34f), new Vector2(360f, 92f), "25000");
        score.alignment = TextAnchor.MiddleLeft;
        SetPivotLeft(score.rectTransform);

        // 素点差（点数の右）
        var delta = EnsureText(row.transform, "DeltaText", font, 46, new Vector2(120f, -34f), new Vector2(200f, 64f), "+0.0");
        delta.alignment = TextAnchor.MiddleLeft;
        SetPivotLeft(delta.rectTransform);

        // 「自家」タグ（自分の行だけ表示）
        var selfTag = EnsureUiChild(row.transform, "SelfTag");
        var selfImage = selfTag.GetComponent<Image>();
        if (selfImage == null) { selfImage = selfTag.AddComponent<Image>(); }
        selfImage.color = SelfTagColor;
        selfImage.raycastTarget = false;
        var selfRt = selfTag.GetComponent<RectTransform>();
        selfRt.anchorMin = new Vector2(0.5f, 0.5f);
        selfRt.anchorMax = new Vector2(0.5f, 0.5f);
        selfRt.anchoredPosition = new Vector2(300f, 44f);
        selfRt.sizeDelta = new Vector2(120f, 48f);
        var selfText = EnsureText(selfTag.transform, "Text", font, 30, Vector2.zero, new Vector2(120f, 48f), "自家");
        selfText.alignment = TextAnchor.MiddleCenter;
        selfTag.SetActive(false);

        // PT（段位ポイント増減。自分の行だけ値が入る）
        var ptRoot = EnsureUiChild(row.transform, "PtRoot");
        var ptRootRt = ptRoot.GetComponent<RectTransform>();
        ptRootRt.anchorMin = new Vector2(0.5f, 0.5f);
        ptRootRt.anchorMax = new Vector2(0.5f, 0.5f);
        ptRootRt.anchoredPosition = new Vector2(420f, -34f);
        ptRootRt.sizeDelta = new Vector2(240f, 64f);
        var ptLabel = EnsureText(ptRoot.transform, "PtLabel", font, 28, new Vector2(-110f, 0f), new Vector2(60f, 44f), "PT");
        ptLabel.alignment = TextAnchor.MiddleLeft;
        SetPivotLeft(ptLabel.rectTransform);
        ptLabel.color = new Color(0.55f, 0.78f, 1f);
        var ptText = EnsureText(ptRoot.transform, "PtText", font, 46, new Vector2(-50f, 0f), new Vector2(180f, 64f), "+0");
        ptText.alignment = TextAnchor.MiddleLeft;
        SetPivotLeft(ptText.rectTransform);
        ptRoot.SetActive(false);

        CleanupChildren(row.transform, new string[]
        {
            "Bg", "RankText", "RankUnit", "Portrait", "NameText", "ScoreText", "DeltaText", "SelfTag", "PtRoot"
        });
    }

    // 段位変動パネル（順位表のあとに出る自分専用の画面）。
    // 左に大段位アイコンと段位名、右に増減・ゲージ・pt表示。
    static void BuildRankPanel(Transform content, Font font)
    {
        var panel = EnsureUiChild(content, "RankPanel");
        var rt = panel.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(0f, -30f);
        rt.sizeDelta = new Vector2(1100f, 420f);
        rt.localScale = Vector3.one;

        var bg = EnsureUiChild(panel.transform, "Bg");
        var bgImage = bg.GetComponent<Image>();
        if (bgImage == null) { bgImage = bg.AddComponent<Image>(); }
        bgImage.color = new Color(0.08f, 0.12f, 0.22f, 0.90f);
        bgImage.raycastTarget = false;
        StretchFull(bg.GetComponent<RectTransform>());
        bg.transform.SetAsFirstSibling();

        // 大段位アイコン（RawImage。素材は Texture2D なので Image ではなく RawImage）
        var icon = EnsureUiChild(panel.transform, "RankIcon");
        var iconRaw = icon.GetComponent<RawImage>();
        if (iconRaw == null) { iconRaw = icon.AddComponent<RawImage>(); }
        iconRaw.color = Color.white;
        iconRaw.raycastTarget = false;
        iconRaw.uvRect = new Rect(0f, 0f, 1f, 1f);
        var iconRt = icon.GetComponent<RectTransform>();
        iconRt.anchorMin = new Vector2(0.5f, 0.5f);
        iconRt.anchorMax = new Vector2(0.5f, 0.5f);
        iconRt.anchoredPosition = new Vector2(-330f, 30f);
        iconRt.sizeDelta = new Vector2(240f, 240f);

        var rankName = EnsureText(panel.transform, "RankName", font, 52, new Vector2(-330f, -140f), new Vector2(420f, 76f), "初獣★1");
        rankName.alignment = TextAnchor.MiddleCenter;

        // 増減（矢印＋数値）
        var arrowUp = EnsureText(panel.transform, "ArrowUp", font, 52, new Vector2(105f, 105f), new Vector2(60f, 70f), "▲");
        arrowUp.alignment = TextAnchor.MiddleCenter;
        arrowUp.color = new Color(0.45f, 0.95f, 0.60f);
        var arrowDown = EnsureText(panel.transform, "ArrowDown", font, 52, new Vector2(105f, 105f), new Vector2(60f, 70f), "▼");
        arrowDown.alignment = TextAnchor.MiddleCenter;
        arrowDown.color = new Color(0.98f, 0.35f, 0.38f);
        arrowDown.gameObject.SetActive(false);

        var delta = EnsureText(panel.transform, "DeltaText", font, 64, new Vector2(230f, 105f), new Vector2(280f, 86f), "+77");
        delta.alignment = TextAnchor.MiddleLeft;
        SetPivotLeft(delta.rectTransform);

        // ゲージ（背景＋Filled の伸びる帯）
        var gaugeBg = EnsureUiChild(panel.transform, "GaugeBg");
        var gaugeBgImage = gaugeBg.GetComponent<Image>();
        if (gaugeBgImage == null) { gaugeBgImage = gaugeBg.AddComponent<Image>(); }
        gaugeBgImage.sprite = UiSprite();
        gaugeBgImage.type = Image.Type.Sliced;
        gaugeBgImage.color = new Color(0.04f, 0.06f, 0.12f, 0.95f);
        gaugeBgImage.raycastTarget = false;
        var gaugeRt = gaugeBg.GetComponent<RectTransform>();
        gaugeRt.anchorMin = new Vector2(0.5f, 0.5f);
        gaugeRt.anchorMax = new Vector2(0.5f, 0.5f);
        gaugeRt.anchoredPosition = new Vector2(200f, 10f);
        gaugeRt.sizeDelta = new Vector2(520f, 42f);

        var fill = EnsureUiChild(gaugeBg.transform, "GaugeFill");
        var fillImage = fill.GetComponent<Image>();
        if (fillImage == null) { fillImage = fill.AddComponent<Image>(); }
        fillImage.sprite = UiSprite();
        fillImage.type = Image.Type.Filled;
        fillImage.fillMethod = Image.FillMethod.Horizontal;
        fillImage.fillOrigin = (int)Image.OriginHorizontal.Left;
        fillImage.fillAmount = 0.5f;
        fillImage.color = new Color(0.30f, 0.85f, 1f, 1f);
        fillImage.raycastTarget = false;
        StretchFull(fill.GetComponent<RectTransform>());

        var value = EnsureText(panel.transform, "ValueText", font, 44, new Vector2(200f, -70f), new Vector2(520f, 60f), "535 / 1000");
        value.alignment = TextAnchor.MiddleCenter;

        CleanupChildren(panel.transform, new string[]
        {
            "Bg", "RankIcon", "RankName", "ArrowUp", "ArrowDown", "DeltaText", "GaugeBg", "ValueText"
        });

        panel.SetActive(false);
    }

    static Sprite UiSprite()
    {
        return AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
    }

    // ============================================================
    // Wire：U#コンポーネント追加＋参照割り当て
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Wire Result Screens")]
    public static void Wire()
    {
        if (EnsureProgramAsset(
            "Assets/Mahjong/Sources/MahjongResultScreen.cs",
            ProgramAssetDir + "/MahjongResultScreen Udon C# Program Asset.asset"))
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[WireResultScreen] U#プログラムアセットを生成しました。コンパイル完了後、もう一度 Wire Result Screens を実行してください。");
            return;
        }

        var portraits = LoadCharaPortraits();
        var rankIcons = LoadTextures(RankIconPaths, "段位アイコン");

        var tables = Object.FindObjectsOfType<Mahjong>(true);
        System.Array.Sort(tables, (a, b) => string.CompareOrdinal(GetPath(a.transform), GetPath(b.transform)));
        if (tables.Length == 0)
        {
            Debug.LogError("[WireResultScreen] Mahjong（卓）が見つかりません。");
            return;
        }

        int wired = 0;
        foreach (var table in tables)
        {
            var rootT = table.transform.Find(RootName);
            if (rootT == null)
            {
                Debug.LogError("[WireResultScreen] " + GetPath(table.transform) + " に " + RootName + " がありません。先に Build Result Screens を実行してください。");
                continue;
            }

            var comp = (MahjongResultScreen)EnsureUSharpComponent(rootT.gameObject, typeof(MahjongResultScreen));
            if (comp == null)
            {
                Debug.LogError("[WireResultScreen] MahjongResultScreen の追加に失敗（プログラムアセット未コンパイル？）。コンパイル後に再実行してください。");
                return;
            }

            var panelRootT = rootT.Find("PanelRoot");
            var contentT = panelRootT != null ? panelRootT.Find("Canvas/Content") : null;
            if (contentT == null)
            {
                Debug.LogError("[WireResultScreen] " + GetPath(table.transform) + " の PanelRoot/Canvas/Content が見つかりません。Build をやり直してください。");
                continue;
            }

            var rowRoots = new GameObject[4];
            var rankTexts = new Text[4];
            var rankUnitTexts = new Text[4];
            var nameTexts = new Text[4];
            var scoreTexts = new Text[4];
            var deltaTexts = new Text[4];
            var ptTexts = new Text[4];
            var ptRoots = new GameObject[4];
            var selfTagRoots = new GameObject[4];
            var selfTagTexts = new Text[4];
            var portraitImages = new RawImage[4];
            var rowBackgrounds = new Image[4];

            for (int i = 0; i < 4; ++i)
            {
                var row = contentT.Find("Row" + i);
                if (row == null) { continue; }

                rowRoots[i] = row.gameObject;
                rankTexts[i] = FindText(row, "RankText");
                rankUnitTexts[i] = FindText(row, "RankUnit");
                nameTexts[i] = FindText(row, "NameText");
                scoreTexts[i] = FindText(row, "ScoreText");
                deltaTexts[i] = FindText(row, "DeltaText");
                ptTexts[i] = FindText(row, "PtRoot/PtText");
                var ptRoot = row.Find("PtRoot");
                ptRoots[i] = ptRoot != null ? ptRoot.gameObject : null;
                var selfTag = row.Find("SelfTag");
                selfTagRoots[i] = selfTag != null ? selfTag.gameObject : null;
                selfTagTexts[i] = FindText(row, "SelfTag/Text");

                var portrait = row.Find("Portrait");
                portraitImages[i] = portrait != null ? portrait.GetComponent<RawImage>() : null;
                if (portraitImages[i] != null)
                {
                    portraitImages[i].texture = portraits.Length > 0 ? portraits[0] : null;
                    EditorUtility.SetDirty(portraitImages[i]);
                }

                var bg = row.Find("Bg");
                rowBackgrounds[i] = bg != null ? bg.GetComponent<Image>() : null;
            }

            comp.panelRoot = panelRootT != null ? panelRootT.gameObject : null;
            comp.headerText = FindText(contentT, "Header");
            comp.contentRect = contentT.GetComponent<RectTransform>();
            comp.canvasGroup = contentT.GetComponent<CanvasGroup>();
            comp.rowRoots = rowRoots;
            comp.rankTexts = rankTexts;
            comp.rankUnitTexts = rankUnitTexts;
            comp.nameTexts = nameTexts;
            comp.scoreTexts = scoreTexts;
            comp.deltaTexts = deltaTexts;
            comp.ptTexts = ptTexts;
            comp.ptRoots = ptRoots;
            comp.selfTagRoots = selfTagRoots;
            comp.selfTagTexts = selfTagTexts;
            comp.portraitImages = portraitImages;
            comp.rowBackgrounds = rowBackgrounds;
            comp.charaMascotTextures = portraits;
            comp.defaultMascotTexture = portraits.Length > 0 ? portraits[0] : null;

            // ---- 段位変動パネル ----
            var rankPanel = contentT.Find("RankPanel");
            if (rankPanel == null)
            {
                Debug.LogError("[WireResultScreen] " + GetPath(table.transform) + " に RankPanel がありません。Build Result Screens を再実行してください。");
            }
            else
            {
                var gaugeBg = rankPanel.Find("GaugeBg");
                var iconT = rankPanel.Find("RankIcon");
                var arrowUp = rankPanel.Find("ArrowUp");
                var arrowDown = rankPanel.Find("ArrowDown");

                comp.rankPanelRoot = rankPanel.gameObject;
                comp.rankIconImage = iconT != null ? iconT.GetComponent<RawImage>() : null;
                comp.rankIconTextures = rankIcons;
                comp.rankNameText = FindText(rankPanel, "RankName");
                comp.rankValueText = FindText(rankPanel, "ValueText");
                comp.rankDeltaText = FindText(rankPanel, "DeltaText");
                comp.rankGaugeFill = gaugeBg != null && gaugeBg.Find("GaugeFill") != null
                    ? gaugeBg.Find("GaugeFill").GetComponent<Image>()
                    : null;
                comp.rankArrowUpRoot = arrowUp != null ? arrowUp.gameObject : null;
                comp.rankArrowDownRoot = arrowDown != null ? arrowDown.gameObject : null;

                if (comp.rankIconImage != null && rankIcons.Length > 0)
                {
                    comp.rankIconImage.texture = rankIcons[0];
                    EditorUtility.SetDirty(comp.rankIconImage);
                }
            }

            UdonSharpEditorUtility.CopyProxyToUdon(comp);
            EditorUtility.SetDirty(comp);

            // 卓本体 → 結果画面の参照（未割当なら機能無効＝従来のテキスト表示）。
            if (table.resultScreen != comp)
            {
                table.resultScreen = comp;
                UdonSharpEditorUtility.CopyProxyToUdon(table);
                EditorUtility.SetDirty(table);
            }
            ++wired;
        }

        MarkDirty(tables[0].gameObject);
        Debug.Log("[WireResultScreen] 完了：" + wired + "/" + tables.Length + " 卓を配線（行4件＋キャラ絵＋卓側 resultScreen）。");
    }

    static Texture[] LoadCharaPortraits()
    {
        return LoadTextures(CharaPortraitPaths, "キャラ画像");
    }

    static Texture[] LoadTextures(string[] paths, string label)
    {
        var textures = new Texture[paths.Length];
        for (int i = 0; i < paths.Length; ++i)
        {
            textures[i] = AssetDatabase.LoadAssetAtPath<Texture2D>(paths[i]);
            if (textures[i] == null)
            {
                Debug.LogWarning("[WireResultScreen] " + label + "が見つかりません: " + paths[i]);
            }
        }
        return textures;
    }

    // ============================================================
    // 共通ヘルパ（BuildScoreBoard と同等の実装）
    // ============================================================
    static Text FindText(Transform parent, string path)
    {
        var t = parent.Find(path);
        return t != null ? t.GetComponent<Text>() : null;
    }

    static Text EnsureText(Transform parent, string name, Font font, int size, Vector2 pos, Vector2 sizeDelta, string placeholder)
    {
        var go = EnsureUiChild(parent, name);
        var text = go.GetComponent<Text>();
        if (text == null) { text = go.AddComponent<Text>(); }
        text.font = font;
        text.fontSize = size;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.text = placeholder;
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = sizeDelta;
        rt.localScale = Vector3.one;
        return text;
    }

    // 左寄せテキストは pivot も左にしておく（フォントサイズ変更で位置が動かないように）。
    static void SetPivotLeft(RectTransform rt)
    {
        var pos = rt.anchoredPosition;
        rt.pivot = new Vector2(0f, 0.5f);
        rt.anchoredPosition = pos;
    }

    static bool EnsureProgramAsset(string scriptPath, string assetPath)
    {
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
        if (script == null)
        {
            Debug.LogError("[WireResultScreen] スクリプトが見つかりません: " + scriptPath);
            return false;
        }
        var type = script.GetClass();
        if (type != null && UdonSharpProgramAsset.GetProgramAssetForClass(type) != null) { return false; }
        if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(assetPath) != null) { return false; }

        var programAsset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
        programAsset.sourceCsScript = script;
        AssetDatabase.CreateAsset(programAsset, assetPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[WireResultScreen] U#プログラムアセットを生成: " + assetPath);
        return true;
    }

    static UdonSharpBehaviour EnsureUSharpComponent(GameObject go, System.Type type)
    {
        var proxy = (UdonSharpBehaviour)go.GetComponent(type);
        if (proxy != null)
        {
            if (UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy) != null) { return proxy; }

            Debug.LogWarning("[WireResultScreen] " + type.Name + " の backing UdonBehaviour が無いため作り直します。");
            Object.DestroyImmediate(proxy);
            foreach (var ub in go.GetComponents<UdonBehaviour>())
            {
                if (ub.programSource == null) { Object.DestroyImmediate(ub); }
            }
        }

        var added = (UdonSharpBehaviour)UdonSharpUndo.AddComponent(go, type);
        if (added != null && UdonSharpEditorUtility.GetBackingUdonBehaviour(added) == null)
        {
            Debug.LogError("[WireResultScreen] " + type.Name + " の追加に失敗（プログラムアセット未コンパイル？）。");
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
            if (t.GetComponent<RectTransform>() == null)
            {
                // Transform で作られていた場合は RectTransform へ作り直す。
                Undo.DestroyObjectImmediate(t.gameObject);
            }
            else
            {
                return t.gameObject;
            }
        }
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Create " + name);
        go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);
        return go;
    }

    // 期待する子だけ残す（レイアウト変更後の遺物を掃除して冪等に）。
    static void CleanupChildren(Transform parent, string[] keepNames)
    {
        var keep = new System.Collections.Generic.HashSet<string>(keepNames);
        for (int i = parent.childCount - 1; i >= 0; --i)
        {
            var c = parent.GetChild(i);
            if (!keep.Contains(c.name)) { Undo.DestroyObjectImmediate(c.gameObject); }
        }
    }

    static void StretchFull(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    static void MarkDirty(GameObject go)
    {
        if (go.scene.IsValid())
        {
            EditorSceneManager.MarkSceneDirty(go.scene);
        }
        else
        {
            EditorUtility.SetDirty(go);
        }
    }

    static string GetPath(Transform t)
    {
        var path = t.name;
        while (t.parent != null)
        {
            t = t.parent;
            path = t.name + "/" + path;
        }
        return path;
    }
}
#endif
