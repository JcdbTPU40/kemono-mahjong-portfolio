#if UNITY_EDITOR
// 宣言演出の骨格生成と配線。
//   Tools > Kemono Mahjong > Build Call Effects    … 全卓に CallEffect のリグを冪等生成
//   Tools > Kemono Mahjong > Wire Call Effects     … U#コンポーネント追加＋参照割り当て＋卓への逆参照
//   Tools > Kemono Mahjong > Validate Call Effects … 参照欠損の洗い出し
// どれも2回実行して安全（冪等）。見た目を変えたい場合は下の定数を変えて Build を再実行する
// （手でシーンをいじった調整は Build 再実行で上書きされる）。
//
// 見た目は2通り（MahjongCallEffect のコメント参照）。
//   (A) 帯カットイン … チー/ポン/カン/リーチ/北抜き（黒い斜め帯＋キャラ立ち絵＋左下の筆文字）
//   (B) 紅雲カットイン … ロン/ツモ（紅い流雲＋金グラデの焼き筆文字＋予兆の巨大文字）
// 帯カットインの筆文字は筆書体フォント（YujiMai）＋白フチ（Outline）。
// 紅雲の筆文字だけは Build 時に同じフォントを一時カメラでテクスチャへ焼く
// （縦グラデとクリームの縁は UI Text の単色では出せない）。テクスチャは手続き生成し、
// Assets/Mahjong/Textures/FX に置く（同名PNGで上書きすれば差し替えられる）。
//
// 卓は Object.FindObjectsOfType<Mahjong>(true) で拾う（BuildAgariEffect と同じ流儀）。
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using SuzuFactory.AozoraMahjong;
using VRC.Udon;

public static class BuildCallEffect
{
    // 席ごとに1本ずつ持つ（ダブロン・トリロンで複数席が同時に宣言するため）。
    const string RootPrefix = "CallEffect";
    const string LegacyRootName = "CallEffect";   // 単一リグだった頃の遺物
    const int NumSeats = 4;
    static string RigName(int seat) { return RootPrefix + seat.ToString(); }
    const string ProgramAssetDir = "Assets/Mahjong/scene_UdonProgramSources";
    const string FxTextureDir = "Assets/Mahjong/Textures/FX";
    const string BrushFontPath = "Assets/Mahjong/Fonts/YujiMai-Regular.ttf";
    const string CharaTextureDir = "Assets/Mahjong/Textures/UI/Characters";
    const string TablePrefabPath = "Assets/Mahjong/Prefabas/KemonoMahjong.prefab";

    const string TexBand = FxTextureDir + "/fx_band.png";
    const string TexInk = FxTextureDir + "/fx_ink.png";
    const string TexStreak = FxTextureDir + "/fx_streak.png";
    const string TexDrop = FxTextureDir + "/fx_drop.png";

    // ---- 紅雲カットイン（ロン/ツモ）----
    const string TexCloud = FxTextureDir + "/fx_cloud.png";
    const string TexPuff = FxTextureDir + "/fx_puff.png";
    const string TexGlyphRon = FxTextureDir + "/fx_glyph_ron.png";
    const string TexGlyphTsumo = FxTextureDir + "/fx_glyph_tsumo.png";

    // キャラIDの並びは Mahjong 側の ResolveSeatChara と揃えること。
    static readonly string[] CharaPortraitFiles = new string[]
    {
        CharaTextureDir + "/vol_mascot_pic.png",
        CharaTextureDir + "/Shine_mascot_pic.png",
        CharaTextureDir + "/Tsumuntema_mascot_pic.png",
    };

    // Canvas は 1400x760 の仮想解像度。実寸 = px * CanvasScale（m）＝0.448m x 0.243m。
    // 手牌13枚はおよそ 0.27m 幅なので、その上に少しはみ出す大きさになる。
    const float CanvasScale = 0.00032f;
    static readonly Vector2 CanvasSize = new Vector2(1400f, 760f);

    const int NumDrops = 6;

    // ---- 帯カットイン ----
    // 帯は「上下が水平・左右が斜め」の平行四辺形。傾きは RectTransform の回転ではなく
    // テクスチャの形そのもので出す。こうすると帯の下辺が水平になり、
    // キャラの下端（水平に切る）とぴったり揃って切り口が見えなくなる。
    //
    // 参照画像の実測：幅605 / 高さ765 / 上下で430pxの左傾き（垂直から約29°）。
    const float BandSlantRatio = 430f / 765f;  // 高さ1に対する横のずれ
    const float BandSlabWidth = 560f;          // 斜辺に挟まれた実体の幅
    const float BandHeight = 500f;
    // バウンディングボックス幅 = 実体幅 + 傾きぶん
    static readonly Vector2 BandSize =
        new Vector2(BandSlabWidth + BandHeight * BandSlantRatio, BandHeight);

    // 帯はキャラより右へずらす。参照画像では帯の実体がキャラ幅のおよそ1/4だけ
    // 右にずれていて、その右側に黒地が見える（真後ろに置くとキャラに全部隠れる）。
    static readonly Vector2 BandPos = new Vector2(360f, 10f);

    // 下辺のアクセント線。帯の下辺（水平）に沿って置く。
    static readonly Vector2 BandEdgeSize = new Vector2(BandSlabWidth, 6f);
    // 立ち絵は素材（700x1080）の上部 700x700 だけを使う＝正方形に切り取る。
    // uvRect は左下原点なので、上端から 700px は y = 1 - 700/1080 から高さ 700/1080。
    const float PortraitSrcWidth = 700f;
    const float PortraitSrcHeight = 1080f;
    const float PortraitCropPixels = 700f;
    static readonly Rect PortraitUvRect = new Rect(
        0f,
        1f - PortraitCropPixels / PortraitSrcHeight,
        1f,
        PortraitCropPixels / PortraitSrcHeight);

    // 切り取りが正方形なので枠も正方形にする（縦横比を変えると立ち絵が歪む）。
    // **下端を帯の下辺と同じ高さに置く**のが肝。切り口と黒帯の境界が一致するので、
    // 「窓から覗いている」ように見えて切り口が目立たない（参照画像と同じ）。
    // 頭は帯の上辺を突き抜ける。
    const float PortraitHeight = 560f;
    static readonly Vector2 PortraitSize = new Vector2(PortraitHeight, PortraitHeight);

    // 下端は帯の下辺より少しだけ上に置く。参照画像もキャラの下に黒地が一筋残っていて、
    // そのほうが「窓から覗いている」感じが出る。
    const float PortraitBottomLift = 18f;
    static readonly Vector2 PortraitPos = new Vector2(
        262f,
        BandPos.y - BandHeight * 0.5f + PortraitBottomLift + PortraitHeight * 0.5f);

    // ---- 墨しぶき（帯側の BandEdge がまだ TexStreak を使う）----
    static readonly Vector2 StreakSize = new Vector2(1180f, 120f);

    // ---- 紅雲カットイン ----
    // 雲は手牌の幅（約0.27m）より一回り広く出す。Canvas は 1px = 0.00032m。
    static readonly Vector2 CloudSize = new Vector2(1280f, 580f);
    static readonly Vector2 CloudPos = new Vector2(0f, 0f);

    // 後ろの霞は雲より一回り大きく。輪郭の外へ紅がにじんで見える。
    static readonly Vector2 HazeSize = new Vector2(1420f, 650f);
    static readonly Vector2 HazePos = new Vector2(34f, -12f);

    // ちぎれ雲。雲本体の左側に散らし、実行時にさらに左へ流す。
    static readonly Vector2 PuffSize = new Vector2(130f, 46f);
    static readonly Vector2[] PuffHomes = new Vector2[]
    {
        new Vector2(-470f,   44f),
        new Vector2(-556f,  -74f),
        new Vector2(-396f, -142f),
        new Vector2(-612f,  106f),
        new Vector2(-332f,  152f),
        new Vector2(-516f, -182f),
    };
    static readonly float[] PuffScales = new float[] { 1.00f, 0.72f, 0.86f, 0.58f, 0.64f, 0.78f };

    // 焼き筆文字。テクスチャと同じ縦横比にしないと字が伸びる。
    const int GlyphTexW = 1024;
    const int GlyphTexH = 512;
    static readonly Vector2 GlyphSize = new Vector2(1000f, 500f);
    static readonly Vector2 GlyphPos = new Vector2(-28f, 14f);

    // 焼きの設定。フォントサイズは Text の 300 上限とは無関係（テクスチャへ直に焼くため）。
    const int GlyphBakeFontSize = 430;
    // YujiMai は参照映像の筆より線が細い。焼くときに距離変換で太らせる。
    const int GlyphBoldPixels = 6;
    const int GlyphRimPixels = 8;
    static readonly Color GlyphRimColor = new Color(1.000f, 0.925f, 0.855f, 1f);   // クリームの縁
    static readonly Color GlyphTopColor = new Color(0.945f, 0.318f, 0.125f, 1f);   // 上：濃い橙
    static readonly Color GlyphMidColor = new Color(1.000f, 0.702f, 0.212f, 1f);   // 中：橙
    static readonly Color GlyphBottomColor = new Color(1.000f, 0.898f, 0.604f, 1f);// 下：淡い金

    // ---- 筆文字 ----
    // Unity の UI Text はフォントサイズを 300 で頭打ちにする（FontData の Range 制限。
    // 430 を書いても保存時に 300 へ丸められ、実行時は小さい字になる）。
    // 上限いっぱいの 300 で焼き、足りないぶんは RectTransform のスケールで稼ぐ。
    const int CallFontSize = 300;
    static readonly Vector2 TextSize = new Vector2(1380f, 560f);

    // 白フチ。参照デザイン（雀魂の宣言カットイン）は色地＋白フチ＋暗い影。
    // Outline.useGraphicAlpha が true なので、実行時に Text のアルファを動かせば
    // 縁も一緒にフェードする＝実行時に Outline を触らなくてよい（Udonのホワイトリスト回避）。
    static readonly Vector2 OutlineDistance = new Vector2(2f, 2f);
    static readonly Color OutlineColor = new Color(1f, 1f, 1f, 0.95f);

    // ============================================================
    // Build：骨格生成
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Build Call Effects")]
    public static void Build()
    {
        EnsureFxTextures();

        var tables = Object.FindObjectsOfType<Mahjong>(true);
        System.Array.Sort(tables, (a, b) => string.CompareOrdinal(GetPath(a.transform), GetPath(b.transform)));
        if (tables.Length == 0)
        {
            Debug.LogError("[BuildCallEffect] Mahjong（卓）が見つかりません。シーンを開くか、卓のPrefab Modeで実行してください。");
            return;
        }

        foreach (var table in tables)
        {
            BuildOne(table.transform);
        }

        MarkDirty(tables[0].gameObject);
        Debug.Log("[BuildCallEffect] 完了：" + tables.Length
            + " 卓に宣言演出リグを生成/更新。次に Wire Call Effects を実行してください。");
    }

    static void BuildOne(Transform tableRoot)
    {
        // 単一リグだった頃の "CallEffect" が残っていたら消す（席別に作り直すため）。
        var legacy = tableRoot.Find(LegacyRootName);
        if (legacy != null) { Undo.DestroyObjectImmediate(legacy.gameObject); }

        for (int seat = 0; seat < NumSeats; ++seat)
        {
            var root = EnsureChild(tableRoot, RigName(seat));
            // スクリプトが載るルートは常時アクティブを厳守（非アクティブなU#は外から叩けない）。
            // 表示ON/OFFは子の FxRoot でのみ行う。位置は実行時に Play が置き直す。
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;
            root.SetActive(true);

            BuildRig(root.transform);
        }
    }

    static void BuildRig(Transform root)
    {
        root.gameObject.SetActive(true);

        // 宣言演出はSEを持たない（2026-09-01）。過去に付けた AudioSource が残っていたら剥がす。
        // 宣言の音はキャラボイス（MahjongSoundsV2）が担当し、ロン/ツモの風切り音は
        // あがり画面側の演出が鳴らす。ここに置くと二重になるうえ、
        // 鳴らない AudioSource が卓あたり4本ぶん残ってしまう。
        var staleAudio = root.GetComponent<AudioSource>();
        if (staleAudio != null) { Undo.DestroyObjectImmediate(staleAudio); }

        var fxRoot = EnsureChild(root, "FxRoot");
        fxRoot.transform.localPosition = Vector3.zero;
        fxRoot.transform.localRotation = Quaternion.identity;
        fxRoot.transform.localScale = Vector3.one;

        // ---- ビルボード層（実行時に水平だけプレイヤーへ向く）----
        var billboard = EnsureChild(fxRoot.transform, "Billboard");
        billboard.transform.localPosition = Vector3.zero;
        billboard.transform.localRotation = Quaternion.identity;
        billboard.transform.localScale = Vector3.one;
        var canvas = EnsureCanvas(billboard.transform);

        // 描画順＝ヒエラルキー順。奥から 帯 → 墨 → 文字影 → 文字。
        BuildBandGroup(canvas.transform);
        BuildInkGroup(canvas.transform);
        EnsureCallText(canvas.transform, "CallShadow", false);
        EnsureCallText(canvas.transform, "CallText", true);

        CleanupChildren(canvas.transform,
            new string[] { "BandGroup", "InkGroup", "CallShadow", "CallText" });

        // 描画順＝ヒエラルキー順。**必ず並べ直す。**
        // EnsureUiChild は既にある子をそのまま返すので、レイアウトを変えたときに
        // 順序だけ古いまま残る（実際に帯と墨が筆文字の上に描かれていた）。
        SetSiblingOrder(canvas.transform,
            new string[] { "BandGroup", "InkGroup", "CallShadow", "CallText" });

        CleanupChildren(fxRoot.transform, new string[] { "Billboard" });

        // 編集中に見えっぱなしにならないよう畳んでおく（実行時も Start で畳まれる）。
        fxRoot.SetActive(false);
    }

    // (A) 帯カットイン：黒い斜め帯＋下辺のライン＋右のキャラ立ち絵
    static void BuildBandGroup(Transform canvas)
    {
        var group = EnsureUiChild(canvas, "BandGroup");
        var groupRect = group.GetComponent<RectTransform>();
        groupRect.anchorMin = new Vector2(0.5f, 0.5f);
        groupRect.anchorMax = new Vector2(0.5f, 0.5f);
        groupRect.pivot = new Vector2(0.5f, 0.5f);
        groupRect.anchoredPosition = Vector2.zero;
        groupRect.sizeDelta = CanvasSize;
        groupRect.localScale = Vector3.one;
        groupRect.localRotation = Quaternion.identity;

        // 傾きはテクスチャの形で出すので RectTransform は回さない。
        var band = EnsureFxImage(group.transform, "Band", TexBand, BandSize);
        band.rectTransform.anchoredPosition = BandPos;

        // 下辺のアクセント線。帯の下辺（水平）に沿わせる。
        // 平行四辺形の下辺は左に寄っているので、その位置に合わせる。
        var edge = EnsureFxImage(group.transform, "BandEdge", TexStreak, BandEdgeSize);
        edge.rectTransform.anchoredPosition = new Vector2(
            BandPos.x - BandSize.x * 0.5f + BandSlabWidth * 0.5f,
            BandPos.y - BandHeight * 0.5f);

        // 立ち絵。テクスチャは Wire で割り当てる（ここでは枠と切り抜き範囲だけ）。
        var portrait = EnsureFxImage(group.transform, "Portrait", null, PortraitSize);
        portrait.rectTransform.anchoredPosition = PortraitPos;
        portrait.uvRect = PortraitUvRect;

        CleanupChildren(group.transform, new string[] { "Band", "BandEdge", "Portrait" });
    }

    // (B) 紅雲：霞 → 雲 → ちぎれ雲 → 予兆 → 焼き文字
    static void BuildInkGroup(Transform canvas)
    {
        var group = EnsureUiChild(canvas, "InkGroup");
        var groupRect = group.GetComponent<RectTransform>();
        groupRect.anchorMin = new Vector2(0.5f, 0.5f);
        groupRect.anchorMax = new Vector2(0.5f, 0.5f);
        groupRect.pivot = new Vector2(0.5f, 0.5f);
        groupRect.anchoredPosition = Vector2.zero;
        groupRect.sizeDelta = CanvasSize;
        groupRect.localScale = Vector3.one;
        groupRect.localRotation = Quaternion.identity;

        // オブジェクト名は旧「墨しぶき」の頃のまま（Streak=霞 / Ink=雲 / Drop=ちぎれ雲）。
        // 名前を変えると CleanupChildren が作り直して手調整が飛ぶので据え置く。
        var haze = EnsureFxImage(group.transform, "Streak", TexCloud, HazeSize);
        haze.rectTransform.anchoredPosition = HazePos;

        var cloud = EnsureFxImage(group.transform, "Ink", TexCloud, CloudSize);
        cloud.rectTransform.anchoredPosition = CloudPos;

        for (int i = 0; i < NumDrops; ++i)
        {
            var puff = EnsureFxImage(group.transform, "Drop" + i, TexPuff, PuffSize);
            puff.rectTransform.anchoredPosition =
                i < PuffHomes.Length ? PuffHomes[i] : Vector2.zero;
            puff.rectTransform.localScale =
                Vector3.one * (i < PuffScales.Length ? PuffScales[i] : 1f);
        }

        // 予兆の巨大文字。テクスチャは Play が宣言に応じて差し替える（ここでは仮にロン）。
        var ghost = EnsureFxImage(group.transform, "Ghost", TexGlyphRon, GlyphSize);
        ghost.rectTransform.anchoredPosition = GlyphPos;

        var glyph = EnsureFxImage(group.transform, "Glyph", TexGlyphRon, GlyphSize);
        glyph.rectTransform.anchoredPosition = GlyphPos;

        var keep = new string[4 + NumDrops];
        keep[0] = "Streak";
        keep[1] = "Ink";
        for (int i = 0; i < NumDrops; ++i) { keep[2 + i] = "Drop" + i; }
        keep[2 + NumDrops] = "Ghost";
        keep[3 + NumDrops] = "Glyph";
        CleanupChildren(group.transform, keep);

        // 描画順＝ヒエラルキー順。文字が雲より奥に行くと読めなくなるので毎回並べ直す。
        SetSiblingOrder(group.transform, keep);
    }

    static GameObject EnsureCanvas(Transform parent)
    {
        var canvasGo = EnsureUiChild(parent, "Canvas");

        var canvas = canvasGo.GetComponent<Canvas>();
        if (canvas == null) { canvas = canvasGo.AddComponent<Canvas>(); }
        canvas.renderMode = RenderMode.WorldSpace;

        var scaler = canvasGo.GetComponent<CanvasScaler>();
        if (scaler == null) { scaler = canvasGo.AddComponent<CanvasScaler>(); }
        scaler.dynamicPixelsPerUnit = 2f;

        // 表示専用。GraphicRaycaster は付けない（卓上ボタンの操作を遮らないため）。
        var raycaster = canvasGo.GetComponent<GraphicRaycaster>();
        if (raycaster != null) { Object.DestroyImmediate(raycaster); }

        var rt = canvasGo.GetComponent<RectTransform>();
        rt.localPosition = Vector3.zero;
        rt.localRotation = Quaternion.identity;
        rt.localScale = new Vector3(CanvasScale, CanvasScale, CanvasScale);
        rt.sizeDelta = CanvasSize;

        return canvasGo;
    }

    static RawImage EnsureFxImage(Transform parent, string name, string texturePath, Vector2 size)
    {
        var go = EnsureUiChild(parent, name);

        var raw = go.GetComponent<RawImage>();
        if (raw == null) { raw = go.AddComponent<RawImage>(); }
        raw.raycastTarget = false;
        if (texturePath != null)
        {
            raw.texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
        }
        raw.color = new Color(1f, 1f, 1f, 0f); // 初期は透明（実行時にスクリプトが塗る）

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = size;
        rt.localScale = Vector3.one;
        rt.localRotation = Quaternion.identity;

        return raw;
    }

    static Text EnsureCallText(Transform parent, string name, bool withOutline)
    {
        var go = EnsureUiChild(parent, name);

        var text = go.GetComponent<Text>();
        if (text == null) { text = go.AddComponent<Text>(); }

        var font = AssetDatabase.LoadAssetAtPath<Font>(BrushFontPath);
        if (font == null)
        {
            Debug.LogWarning("[BuildCallEffect] 筆書体フォントが見つかりません: " + BrushFontPath
                + " … 既定フォントで作ります（見た目は筆文字になりません）。");
        }
        else
        {
            text.font = font;
        }

        text.fontSize = CallFontSize;
        text.fontStyle = FontStyle.Normal;
        text.alignment = TextAnchor.MiddleCenter;
        text.raycastTarget = false;
        // あふれても切らない。折り返しは事故のもとなので明示的に殺す
        // （実機ラベル崩壊の件と同じ理由。ここは2〜3文字しか入らない）。
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.resizeTextForBestFit = false;
        text.text = "北抜き";
        text.color = new Color(1f, 1f, 1f, 0f);

        // 縁取り（前面の文字だけ）。影側に付けると輪郭が二重になって濁る。
        var outline = go.GetComponent<Outline>();
        if (withOutline)
        {
            if (outline == null) { outline = go.AddComponent<Outline>(); }
            outline.effectColor = OutlineColor;
            outline.effectDistance = OutlineDistance;
            outline.useGraphicAlpha = true;
        }
        else if (outline != null)
        {
            Object.DestroyImmediate(outline);
        }

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = TextSize;
        rt.localScale = Vector3.one;
        rt.localRotation = Quaternion.identity;

        return text;
    }

    // ============================================================
    // Prefab 直接ビルド
    // ------------------------------------------------------------
    //  卓は KemonoMahjong.prefab のインスタンス。演出リグは Prefab 資産側に置くのが
    //  このプロジェクトの流儀（AgariEffect / ReachDiscardEffect などと同じ）。
    //  ところが Object.FindObjectsOfType は Prefab Mode を開いても Prefab 側の
    //  Mahjong を拾わない（シーンの卓しか返さない）ため、通常の Build ではリグが
    //  インスタンスへの「追加GameObject」になってしまい、卓ごとにバラバラになる。
    //  そこで Prefab ステージのルートを直接叩く経路を用意する。
    //
    //  ※ Prefab を直したあとインスタンス側で Build を回すと override が増えるので、
    //    見た目を変えるときは基本こちらを使うこと。
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Build Call Effects (Prefab)")]
    public static void BuildPrefab()
    {
        EnsureFxTextures();

        var stage = UnityEditor.SceneManagement.PrefabStageUtility.OpenPrefab(TablePrefabPath);
        if (stage == null)
        {
            Debug.LogError("[BuildCallEffect] Prefab を開けません: " + TablePrefabPath);
            return;
        }

        var root = stage.prefabContentsRoot;
        if (root.GetComponent<Mahjong>() == null)
        {
            Debug.LogError("[BuildCallEffect] Prefab のルートに Mahjong がありません: " + TablePrefabPath);
            return;
        }

        BuildOne(root.transform);

        SavePrefabStage(stage);

        Debug.Log("[BuildCallEffect] Prefab に宣言演出リグを生成/更新しました: " + TablePrefabPath
            + " … 次に Wire Call Effects (Prefab) を実行してください。");
    }

    [MenuItem("Tools/Kemono Mahjong/Wire Call Effects (Prefab)")]
    public static void WirePrefab()
    {
        if (EnsureProgramAsset(
            "Assets/Mahjong/Sources/MahjongCallEffect.cs",
            ProgramAssetDir + "/MahjongCallEffect Udon C# Program Asset.asset"))
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[WireCallEffect] U#プログラムアセットを生成しました。コンパイル完了後、もう一度実行してください。");
            return;
        }

        var stage = UnityEditor.SceneManagement.PrefabStageUtility.OpenPrefab(TablePrefabPath);
        if (stage == null)
        {
            Debug.LogError("[WireCallEffect] Prefab を開けません: " + TablePrefabPath);
            return;
        }

        var root = stage.prefabContentsRoot;
        var comps = WireAllRigs(root.transform);
        if (comps == null) { return; }

        // Prefab 側の卓 → 演出の参照も繋いでおく（新しく置いた卓が最初から動く）。
        // 既存インスタンスは publicVariables を丸ごと override しているので影響しない。
        var table = root.GetComponent<Mahjong>();
        if (table != null)
        {
            table.callEffects = comps;
            UdonSharpEditorUtility.CopyProxyToUdon(table);
            EditorUtility.SetDirty(table);
        }

        SavePrefabStage(stage);
        VerifyPrefab();
    }

    // Prefab ステージを保存する。
    //  ※ SaveAsPrefabAsset の途中で UdonSharp の Odin シリアライザが
    //    「ArgumentNullException: unityObject」を吐くことがあるが、保存自体は通る。
    //    紛らわしいので、保存後に必ず VerifyPrefab で実体を検証すること。
    static void SavePrefabStage(UnityEditor.SceneManagement.PrefabStage stage)
    {
        EditorSceneManager.MarkSceneDirty(stage.scene);
        PrefabUtility.SaveAsPrefabAsset(stage.prefabContentsRoot, stage.assetPath);
        AssetDatabase.SaveAssets();
    }

    // 保存後の Prefab 資産を読み直して、実際に中身が揃っているか確かめる。
    static void VerifyPrefab()
    {
        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(TablePrefabPath);
        if (asset == null)
        {
            Debug.LogError("[WireCallEffect] Prefab を読み直せません: " + TablePrefabPath);
            return;
        }

        var comps = new MahjongCallEffect[NumSeats];
        for (int seat = 0; seat < NumSeats; ++seat)
        {
            var t = asset.transform.Find(RigName(seat));
            comps[seat] = t != null ? t.GetComponent<MahjongCallEffect>() : null;
        }

        if (CountHoles(comps, "Prefab") > 0) { return; }

        Debug.Log("[WireCallEffect] Prefab を配線しました（" + NumSeats + "席・参照欠損0）: " + TablePrefabPath
            + " … 途中の ArgumentNullException(UdonSharp) は無視して構いません。");
    }

    // ============================================================
    // Wire：U#コンポーネント追加＋参照割り当て
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Wire Call Effects")]
    public static void Wire()
    {
        if (EnsureProgramAsset(
            "Assets/Mahjong/Sources/MahjongCallEffect.cs",
            ProgramAssetDir + "/MahjongCallEffect Udon C# Program Asset.asset"))
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[WireCallEffect] U#プログラムアセットを生成しました。コンパイル完了後、もう一度 Wire Call Effects を実行してください。");
            return;
        }

        var tables = Object.FindObjectsOfType<Mahjong>(true);
        System.Array.Sort(tables, (a, b) => string.CompareOrdinal(GetPath(a.transform), GetPath(b.transform)));
        if (tables.Length == 0)
        {
            Debug.LogError("[WireCallEffect] Mahjong（卓）が見つかりません。");
            return;
        }

        int wired = 0;
        foreach (var table in tables)
        {
            var comps = WireAllRigs(table.transform);
            if (comps == null) { return; }

            // 卓本体 → 宣言演出の参照（未割当なら宣言演出なし＝従来動作）。
            table.callEffects = comps;
            UdonSharpEditorUtility.CopyProxyToUdon(table);
            EditorUtility.SetDirty(table);
            ++wired;
        }

        MarkDirty(tables[0].gameObject);
        Debug.Log("[WireCallEffect] 完了：" + wired + "/" + tables.Length + " 卓を配線。");
    }

    // 卓（またはPrefabルート）の下の CallEffect0..3 をまとめて配線する。
    static MahjongCallEffect[] WireAllRigs(Transform tableRoot)
    {
        var comps = new MahjongCallEffect[NumSeats];
        for (int seat = 0; seat < NumSeats; ++seat)
        {
            var rootT = tableRoot.Find(RigName(seat));
            if (rootT == null)
            {
                Debug.LogError("[WireCallEffect] " + GetPath(tableRoot) + " に " + RigName(seat)
                    + " がありません。先に Build を実行してください。");
                return null;
            }

            var comp = WireRig(rootT);
            if (comp == null) { return null; }
            comps[seat] = comp;
        }
        return comps;
    }

    static MahjongCallEffect WireRig(Transform rootT)
    {
        var comp = (MahjongCallEffect)EnsureUSharpComponent(rootT.gameObject, typeof(MahjongCallEffect));
        if (comp == null)
        {
            Debug.LogError("[WireCallEffect] MahjongCallEffect の追加に失敗（プログラムアセット未コンパイル？）。コンパイル後に再実行してください。");
            return null;
        }

        var fxRoot = rootT.Find("FxRoot");
        if (fxRoot == null)
        {
            Debug.LogError("[WireCallEffect] " + GetPath(rootT) + " に FxRoot がありません。Build をやり直してください。");
            return null;
        }

        var canvas = fxRoot.Find("Billboard/Canvas");
        var bandGroup = canvas != null ? canvas.Find("BandGroup") : null;
        var inkGroup = canvas != null ? canvas.Find("InkGroup") : null;

        comp.fxRoot = fxRoot.gameObject;
        comp.billboardRoot = fxRoot.Find("Billboard");

        comp.bandGroup = bandGroup != null ? bandGroup.gameObject : null;
        comp.bandImage = FindRaw(bandGroup, "Band");
        comp.bandRect = FindRect(bandGroup, "Band");
        comp.bandEdgeImage = FindRaw(bandGroup, "BandEdge");
        comp.bandEdgeRect = FindRect(bandGroup, "BandEdge");
        comp.portraitImage = FindRaw(bandGroup, "Portrait");
        comp.portraitRect = FindRect(bandGroup, "Portrait");

        var portraits = new Texture[CharaPortraitFiles.Length];
        for (int i = 0; i < CharaPortraitFiles.Length; ++i)
        {
            portraits[i] = AssetDatabase.LoadAssetAtPath<Texture2D>(CharaPortraitFiles[i]);
            if (portraits[i] == null)
            {
                Debug.LogWarning("[WireCallEffect] 立ち絵が見つかりません: " + CharaPortraitFiles[i]);
            }
        }
        comp.charaPortraits = portraits;
        comp.defaultPortrait = portraits.Length > 0 ? portraits[0] : null;

        comp.inkGroup = inkGroup != null ? inkGroup.gameObject : null;
        comp.inkImage = FindRaw(inkGroup, "Ink");
        comp.inkRect = FindRect(inkGroup, "Ink");
        comp.streakImage = FindRaw(inkGroup, "Streak");
        comp.streakRect = FindRect(inkGroup, "Streak");

        var dropImages = new RawImage[NumDrops];
        var dropRects = new RectTransform[NumDrops];
        for (int i = 0; i < NumDrops; ++i)
        {
            dropImages[i] = FindRaw(inkGroup, "Drop" + i);
            dropRects[i] = FindRect(inkGroup, "Drop" + i);
        }
        comp.dropImages = dropImages;
        comp.dropRects = dropRects;

        comp.glyphImage = FindRaw(inkGroup, "Glyph");
        comp.glyphRect = FindRect(inkGroup, "Glyph");
        comp.ghostImage = FindRaw(inkGroup, "Ghost");
        comp.ghostRect = FindRect(inkGroup, "Ghost");
        comp.ronGlyph = AssetDatabase.LoadAssetAtPath<Texture2D>(TexGlyphRon);
        comp.tsumoGlyph = AssetDatabase.LoadAssetAtPath<Texture2D>(TexGlyphTsumo);
        if (comp.ronGlyph == null || comp.tsumoGlyph == null)
        {
            Debug.LogWarning("[WireCallEffect] 焼き筆文字が見つかりません。"
                + "Build Call Effects を先に実行してください（" + FxTextureDir + "）。");
        }

        comp.callText = FindText(canvas, "CallText");
        comp.callTextRect = FindRect(canvas, "CallText");
        comp.callShadowText = FindText(canvas, "CallShadow");
        comp.callShadowRect = FindRect(canvas, "CallShadow");


        // --- 見た目の確定値 ---
        // C#側の既定値を変えても、既にシーンに載っているコンポーネントの値は更新されない
        // （U#のシリアライズ済みフィールドはそのまま残る）。ここで毎回明示して揃える。
        // ＝この配色・寸法は Wire が正。インスペクタで手直ししても Wire 再実行で戻る。
        comp.chiiColor = new Color(0.40f, 0.95f, 0.52f, 1f);
        comp.ponColor = new Color(1.00f, 0.68f, 0.22f, 1f);
        comp.kanColor = new Color(0.80f, 0.56f, 1.00f, 1f);
        comp.riichiColor = new Color(0.55f, 0.86f, 1.00f, 1f);
        // ロン/ツモは雲の色。参照映像の実測に寄せた深紅／琥珀。
        // 雲テクスチャの RGB（0.78〜1.0のムラ）に乗算されるので、ここは最も明るい部分の色。
        comp.ronColor = new Color(0.86f, 0.13f, 0.17f, 1f);
        // ツモの雲も紅系にする。金の雲に金の筆文字を乗せると字が沈むため
        // （参照映像もロン/ツモとも紅系で、色ではなく字で区別している）。
        // ロンより少し紫寄りの深紅にして、並べたときに別物と分かるようにした。
        comp.tsumoColor = new Color(0.82f, 0.14f, 0.22f, 1f);
        comp.kitaColor = new Color(1.00f, 0.18f, 0.56f, 1f);   // 参照デザインの濃いピンク
        comp.bandColor = new Color(0.045f, 0.035f, 0.06f, 0.93f);
        comp.shadowColor = new Color(0.04f, 0.02f, 0.06f, 0.85f);

        // inkTint は旧・墨しぶき時代の残り。紅雲では参照していないので、値を変えても効かない
        //（雲の濃淡はテクスチャ側の RGB と cloudAlpha が作る）。
        comp.inkTint = 0.62f;
        // 文字の大きさ。フォントサイズが300で頭打ちなので、ここのスケールが最終的な
        // 見た目の大きさを決める（帯=300*0.88=264px相当／墨=300px相当）。
        // 小さくすると筆線が細くなり、白フチに食われて字が白く見える。
        comp.bandTextScale = 0.88f;
        comp.inkTextScale = 1.0f;
        comp.bandTextHome = new Vector2(-330f, -190f);
        comp.inkTextHome = Vector2.zero;

        // ---- 紅雲カットインの確定値 ----
        // 参照映像（25fps）の実測＋ユーザー指定：
        // 予兆（拡大＋フェードイン）→ 一気に通常サイズで出る → 約0.6秒静止 → 一気に消える。
        comp.ghostSeconds = 0.30f;
        comp.cloudEnterSeconds = 0.07f;
        comp.cloudHoldSeconds = 0.60f;
        comp.cloudExitSeconds = 0.05f;
        comp.cloudAlpha = 0.88f;
        comp.hazeAlpha = 0.0f;   // 0=霞なし（雲の二重像になるので既定は切る）
        comp.puffAlpha = 0.80f;
        comp.hazeDriftDistance = 54f;
        comp.puffDistance = 150f;
        comp.glyphScale = 1.0f;
        comp.glyphPunchScale = 1.0f;    // 予兆が大きさの芝居をするので本体は拡大しない
        comp.glyphRiseDistance = 0f;
        comp.ghostColor = Color.white;
        comp.ghostScale = 2.0f;
        comp.inkScale = 1.0f;

        UdonSharpEditorUtility.CopyProxyToUdon(comp);
        EditorUtility.SetDirty(comp);
        return comp;
    }

    // ============================================================
    // 検証：参照欠損の洗い出し
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Validate Call Effects")]
    public static void Validate()
    {
        var tables = Object.FindObjectsOfType<Mahjong>(true);
        System.Array.Sort(tables, (a, b) => string.CompareOrdinal(GetPath(a.transform), GetPath(b.transform)));

        int ok = 0;
        int missing = 0;
        foreach (var table in tables)
        {
            int holes = CountHoles(table.callEffects, GetPath(table.transform));
            if (holes > 0) { ++missing; } else { ++ok; }
        }

        Debug.Log("[ValidateCallEffect] 完了：正常 " + ok + " 卓 / 要修正 " + missing + " 卓。");
    }

    // 席ごと4本ぶんの参照欠損を数える。0なら健全。
    static int CountHoles(MahjongCallEffect[] comps, string label)
    {
        if (comps == null || comps.Length != NumSeats)
        {
            Debug.LogError("[ValidateCallEffect] " + label + " : callEffects が " + NumSeats + " 本ありません。");
            return 1;
        }

        int holes = 0;
        for (int seat = 0; seat < NumSeats; ++seat)
        {
            var comp = comps[seat];
            if (comp == null) { ++holes; continue; }

            if (comp.fxRoot == null) { holes++; }
            if (comp.billboardRoot == null) { holes++; }
            if (comp.bandGroup == null || comp.bandImage == null || comp.bandRect == null) { holes++; }
            if (comp.bandEdgeImage == null || comp.bandEdgeRect == null) { holes++; }
            if (comp.portraitImage == null || comp.portraitRect == null) { holes++; }
            if (comp.charaPortraits == null || comp.charaPortraits.Length == 0) { holes++; }
            if (comp.inkGroup == null || comp.inkImage == null || comp.streakImage == null) { holes++; }
            if (comp.dropImages == null || comp.dropImages.Length != NumDrops) { holes++; }
            if (comp.glyphImage == null || comp.glyphRect == null) { holes++; }
            if (comp.ghostImage == null || comp.ghostRect == null) { holes++; }
            if (comp.ronGlyph == null || comp.tsumoGlyph == null) { holes++; }
            if (comp.callText == null || comp.callShadowText == null) { holes++; }
            if (comp.callText != null && comp.callText.font == null) { holes++; }
        }

        if (holes > 0)
        {
            Debug.LogError("[ValidateCallEffect] " + label + " : 参照欠損 " + holes + " 件。Wire を再実行してください。");
        }
        return holes;
    }

    static RawImage FindRaw(Transform parent, string name)
    {
        if (parent == null) { return null; }
        var t = parent.Find(name);
        return t != null ? t.GetComponent<RawImage>() : null;
    }

    static Text FindText(Transform parent, string name)
    {
        if (parent == null) { return null; }
        var t = parent.Find(name);
        return t != null ? t.GetComponent<Text>() : null;
    }

    static RectTransform FindRect(Transform parent, string name)
    {
        if (parent == null) { return null; }
        var t = parent.Find(name);
        return t != null ? t as RectTransform : null;
    }

    // ============================================================
    // テクスチャの手続き生成
    //  ・アルファのみの形。色はスクリプトが RawImage.color で乗せる
    //    ＝1枚で全宣言色を賄える（Quest向けにメモリ最小）。
    //  ・既に同じパスにファイルがあれば触らない（差し替え素材を尊重する）。
    // ============================================================
    public static void EnsureFxTextures()
    {
        EnsureFolder(FxTextureDir);

        bool created = false;
        created |= WriteTextureIfMissing(TexBand, 841, 500, MakeBand(841, 500));
        created |= WriteTextureIfMissing(TexInk, 1024, 460, MakeInk(1024, 460));
        created |= WriteTextureIfMissing(TexStreak, 1024, 128, MakeStreak(1024, 128));
        created |= WriteTextureIfMissing(TexDrop, 128, 128, MakeDrop(128));
        created |= WriteTextureIfMissing(TexCloud, 1024, 470, MakeCloud(1024, 470));
        created |= WriteTextureIfMissing(TexPuff, 256, 96, MakePuff(256, 96));
        created |= BakeGlyphIfMissing(TexGlyphRon, "ロン");
        created |= BakeGlyphIfMissing(TexGlyphTsumo, "ツモ");

        if (created)
        {
            AssetDatabase.Refresh();
            Debug.Log("[BuildCallEffect] テクスチャを " + FxTextureDir + " に生成しました。差し替えたい場合は同名PNGで上書きしてください。");
        }
    }

    // カットインの帯。上下が水平・左右が斜めの平行四辺形（参照デザインの形）。
    // 下辺が水平になるので、立ち絵を水平に切った下端とぴったり揃う。
    static Color[] MakeBand(int w, int h)
    {
        var px = new Color[w * h];

        // canvas 上の「傾き幅 : バウンディングボックス幅」と同じ比率でテクスチャを塗る。
        // こうしておけば解像度を変えても形は変わらない。
        float slantCanvas = BandHeight * BandSlantRatio;
        float slantPx = w * slantCanvas / (BandSlabWidth + slantCanvas);
        float slabPx = w - slantPx;

        for (int y = 0; y < h; ++y)
        {
            float v = (y + 0.5f) / h;          // 0=下 1=上
            float left = slantPx * v;          // 下辺は左端、上辺は右へずれる
            float right = left + slabPx;

            for (int x = 0; x < w; ++x)
            {
                float fx = x + 0.5f;

                // 斜辺は1.5pxだけぼかす（ジャギ防止）。上下辺も同様。
                float a = Mathf.Clamp01(Mathf.Min(fx - left, right - fx) / 1.5f);

                float edgeY = Mathf.Min(y + 0.5f, h - (y + 0.5f));
                a = Mathf.Min(a, Mathf.Clamp01(edgeY / 1.5f));

                px[y * w + x] = new Color(1f, 1f, 1f, a);
            }
        }

        return px;
    }

    // 飛び散った墨。
    //  ・丸い塊を重ねると必ず「雲」になるので、横一閃のかすれた筆跡を数本重ねて作る。
    //    太さを sin で揺らして縁を破り、両端をすぼめて筆のかすれにする。
    //  ・そのまわりに小さな雫を散らす。
    //  ・縁は硬く落とす（ぼかすと墨に見えない）。
    static Color[] MakeInk(int w, int h)
    {
        var px = new Color[w * h];
        var rnd = new System.Random(20260824); // 毎回同じ形になるよう固定シード

        const int Strokes = 7;
        var sy = new float[Strokes];     // 筆跡の中心の高さ（v）
        var sx0 = new float[Strokes];    // 始点・終点（u）
        var sx1 = new float[Strokes];
        var sthick = new float[Strokes]; // 最大の太さ（v単位の半幅）
        var sfreq = new float[Strokes];  // 縁を破る波の周波数
        var sphase = new float[Strokes];

        for (int i = 0; i < Strokes; ++i)
        {
            float center = (i + 0.5f) / Strokes;               // 0..1 を縦に割り当てる
            sy[i] = 0.5f + (center - 0.5f) * 0.62f + ((float)rnd.NextDouble() - 0.5f) * 0.05f;

            // 中央の筆跡ほど長く太く、端の筆跡は短く細い＝一閃の形になる。
            float weight = 1f - Mathf.Abs(center - 0.5f) * 2f; // 中央=1 端=0
            sx0[i] = 0.10f + (1f - weight) * 0.22f + (float)rnd.NextDouble() * 0.06f;
            sx1[i] = 0.90f - (1f - weight) * 0.22f - (float)rnd.NextDouble() * 0.06f;
            sthick[i] = (0.035f + weight * 0.075f) * (0.8f + (float)rnd.NextDouble() * 0.45f);

            sfreq[i] = 18f + (float)rnd.NextDouble() * 26f;
            sphase[i] = (float)(rnd.NextDouble() * System.Math.PI * 2.0);
        }

        const int Droplets = 26;
        var dx = new float[Droplets];
        var dy = new float[Droplets];
        var dr = new float[Droplets];
        var dstretch = new float[Droplets];
        var dang = new float[Droplets];

        for (int i = 0; i < Droplets; ++i)
        {
            float a = (float)(rnd.NextDouble() * System.Math.PI * 2.0);
            float d = 0.20f + (float)rnd.NextDouble() * 0.28f;

            dx[i] = 0.5f + Mathf.Cos(a) * d;
            dy[i] = 0.5f + Mathf.Sin(a) * d * 0.55f;

            float far = Mathf.Clamp01((d - 0.20f) / 0.28f);
            dr[i] = Mathf.Lerp(0.026f, 0.006f, far) * (0.7f + (float)rnd.NextDouble() * 0.8f);
            dstretch[i] = Mathf.Lerp(1.4f, 3.2f, far);
            dang[i] = Mathf.Atan2(Mathf.Sin(a) * 0.55f, Mathf.Cos(a));
        }

        float aspect = (float)w / h; // uv を正方形換算に戻すための比

        for (int y = 0; y < h; ++y)
        {
            float v = (y + 0.5f) / h;
            for (int x = 0; x < w; ++x)
            {
                float u = (x + 0.5f) / w;

                float a = 0f;

                // ---- 横一閃の筆跡 ----
                for (int i = 0; i < Strokes; ++i)
                {
                    if (u <= sx0[i] || u >= sx1[i]) { continue; }

                    float t = (u - sx0[i]) / (sx1[i] - sx0[i]);

                    // 両端をすぼめる（中央が最も太い）。
                    float profile = Mathf.Pow(Mathf.Sin(t * Mathf.PI), 0.55f);

                    // 太さを揺らして縁を破る＝かすれ。位相の違う2波を掛ける。
                    float jitter = 0.62f
                        + 0.38f * Mathf.Abs(Mathf.Sin(u * sfreq[i] + sphase[i])
                                          * Mathf.Sin(u * sfreq[i] * 0.37f + sphase[i] * 1.7f));

                    float halfT = sthick[i] * profile * jitter;
                    if (halfT <= 0.0005f) { continue; }

                    float dy2 = Mathf.Abs(v - sy[i]);
                    if (dy2 >= halfT) { continue; }

                    float stroke = Mathf.Clamp01((1f - dy2 / halfT) / 0.28f);
                    if (stroke > a) { a = stroke; }
                }

                // ---- 飛んだ雫 ----
                for (int i = 0; i < Droplets; ++i)
                {
                    float ox = (u - dx[i]);
                    float oy = (v - dy[i]) / aspect;

                    float c = Mathf.Cos(-dang[i]);
                    float sn = Mathf.Sin(-dang[i]);
                    float rx = ox * c - oy * sn;
                    float ry = ox * sn + oy * c;

                    float radius = Mathf.Max(0.0001f, dr[i]);
                    float nx = rx / (radius * dstretch[i]);
                    float ny = ry / radius;

                    float d = Mathf.Sqrt(nx * nx + ny * ny);
                    if (d >= 1f) { continue; }

                    float blob = Mathf.Clamp01((1f - d) / 0.22f);
                    if (blob > a) { a = blob; }
                }

                px[y * w + x] = new Color(1f, 1f, 1f, a);
            }
        }

        return px;
    }

    // 横に走る筆の帯。左が細く、右へ向かって太く、末端でかすれる。
    static Color[] MakeStreak(int w, int h)
    {
        var px = new Color[w * h];
        float half = h * 0.5f;

        for (int x = 0; x < w; ++x)
        {
            float u = (x + 0.5f) / w;

            float thick = u < 0.35f
                ? Mathf.SmoothStep(0f, 1f, u / 0.35f)
                : Mathf.Lerp(1f, 0.18f, (u - 0.35f) / 0.65f);

            // 末端のかすれ（1に近いほど濃い）。
            float dryness = u < 0.72f ? 1f : Mathf.Lerp(1f, 0.25f, (u - 0.72f) / 0.28f);

            // 筆の毛によるムラ。位相をずらした2つの波で縞を作る。
            float streaks = 0.82f + 0.18f * Mathf.Sin(u * 47f) * Mathf.Sin(u * 13f + 1.3f);

            float bandHalf = half * thick;

            for (int y = 0; y < h; ++y)
            {
                float dy = Mathf.Abs((y + 0.5f) - half);
                float a = 0f;
                if (bandHalf > 0.5f && dy < bandHalf)
                {
                    a = Mathf.Clamp01((1f - dy / bandHalf) / 0.22f) * dryness * streaks;
                }
                px[y * w + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(a));
            }
        }

        return px;
    }

    // 飛び散る墨の粒。真円ではなく縦にわずかに潰した形。
    static Color[] MakeDrop(int size)
    {
        var px = new Color[size * size];
        float half = size * 0.5f;

        for (int y = 0; y < size; ++y)
        {
            for (int x = 0; x < size; ++x)
            {
                float dx = ((x + 0.5f) - half) / half;
                float dy = ((y + 0.5f) - half) / (half * 0.82f);
                float d = Mathf.Sqrt(dx * dx + dy * dy);

                float a = d >= 1f ? 0f : Mathf.Clamp01((1f - d) / 0.30f);
                px[y * size + x] = new Color(1f, 1f, 1f, a);
            }
        }

        return px;
    }

    // ============================================================
    // 紅雲カットインの素材
    // ============================================================

    // 紅い流雲。参照映像の形＝角丸の太い横棒（カプセル）を数本だけ重ねた一塊。
    //  ・本数を増やすと輪郭が均されて「ただの角丸長方形」になる。少なく太くが肝。
    //  ・右端はほぼ揃えて切り落とし（画面外へ続く感じ）、2本だけ引っ込めて段を作る。
    //  ・左端はばらばらに引き、さらに離れた短い棒を撒いてちぎれさせる。
    //  ・RGB に濃淡のムラを焼く。実行時は宣言色を乗算するので、この1枚で紅にも金にもなる。
    //    （アルファだけの板では参照映像のまだら感が出ない。）
    //
    //  数値は u（横）と v（縦）の 0..1。r は u 基準の半径。
    static readonly float[] CloudBars = new float[]
    {
        // x0     x1     y      r
        0.42f, 0.79f, 0.790f, 0.026f,
        0.55f, 0.94f, 0.745f, 0.022f,
        0.30f, 0.99f, 0.660f, 0.048f,
        0.18f, 0.97f, 0.575f, 0.056f,
        0.10f, 1.00f, 0.490f, 0.060f,
        0.16f, 0.99f, 0.405f, 0.053f,
        0.27f, 0.95f, 0.325f, 0.043f,
        0.38f, 0.87f, 0.255f, 0.032f,
        0.06f, 0.42f, 0.440f, 0.033f,
        0.13f, 0.37f, 0.350f, 0.025f,
    };

    static readonly float[] CloudFrags = new float[]
    {
        // x0     x1     y      r
        0.02f, 0.14f, 0.285f, 0.021f,
        0.09f, 0.22f, 0.170f, 0.016f,
        0.00f, 0.10f, 0.560f, 0.018f,
        0.19f, 0.34f, 0.870f, 0.015f,
        0.07f, 0.18f, 0.715f, 0.015f,
        0.44f, 0.60f, 0.112f, 0.014f,
        0.66f, 0.79f, 0.075f, 0.011f,
    };

    static Color[] MakeCloud(int w, int h)
    {
        var px = new Color[w * h];
        var rnd = new System.Random(20260911);

        // ---- 濃淡のムラ ----
        const int Spots = 10;
        var mx = new float[Spots];
        var my = new float[Spots];
        var mrx = new float[Spots];
        var mry = new float[Spots];
        for (int i = 0; i < Spots; ++i)
        {
            mx[i] = (0.14f + (float)rnd.NextDouble() * 0.82f) * w;
            my[i] = (0.26f + (float)rnd.NextDouble() * 0.50f) * h;
            mrx[i] = (0.04f + (float)rnd.NextDouble() * 0.10f) * w;
            mry[i] = (0.07f + (float)rnd.NextDouble() * 0.15f) * h;
        }

        for (int y = 0; y < h; ++y)
        {
            float fy = y + 0.5f;
            for (int x = 0; x < w; ++x)
            {
                float fx = x + 0.5f;
                float a = 0f;

                a = Mathf.Max(a, CapsuleCoverage(CloudBars, fx, fy, w, h));
                a = Mathf.Max(a, CapsuleCoverage(CloudFrags, fx, fy, w, h));

                // 濃淡：基準 0.74、ムラの中心で 1.0。
                float lit = 0.74f;
                for (int i = 0; i < Spots; ++i)
                {
                    float nx = (fx - mx[i]) / mrx[i];
                    float ny = (fy - my[i]) / mry[i];
                    float d = nx * nx + ny * ny;
                    if (d >= 1f) { continue; }
                    float v = 0.74f + 0.19f * (1f - d) * (1f - d);
                    if (v > lit) { lit = v; }
                }

                px[y * w + x] = new Color(lit, lit, lit, a);
            }
        }

        return px;
    }

    // カプセル（角丸の横棒）の被覆率。table は {x0, x1, y, r} の並び。
    static float CapsuleCoverage(float[] table, float fx, float fy, int w, int h)
    {
        float best = 0f;
        for (int i = 0; i + 3 < table.Length; i += 4)
        {
            float x0 = table[i] * w;
            float x1 = table[i + 1] * w;
            float cy = table[i + 2] * h;
            float r = table[i + 3] * w;

            float cx = Mathf.Clamp(fx, x0, x1);
            float dx = fx - cx;
            float dy = fy - cy;
            float d = Mathf.Sqrt(dx * dx + dy * dy);

            float c = Mathf.Clamp01((r - d) / 1.5f);
            if (c > best) { best = c; }
        }
        return best;
    }

    // ちぎれ雲1個ぶん。角丸の横棒（カプセル）。
    static Color[] MakePuff(int w, int h)
    {
        var px = new Color[w * h];
        float r = h * 0.5f;
        float x0 = r;
        float x1 = w - r;

        for (int y = 0; y < h; ++y)
        {
            float fy = y + 0.5f;
            for (int x = 0; x < w; ++x)
            {
                float fx = x + 0.5f;
                float cx = Mathf.Clamp(fx, x0, x1);
                float dx = fx - cx;
                float dy = fy - r;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Clamp01((r - 0.5f - d) / 1.5f);
                px[y * w + x] = new Color(1f, 1f, 1f, a);
            }
        }

        return px;
    }

    // ============================================================
    // 焼き筆文字
    //  UI Text は単色しか塗れない。参照映像の「縦グラデ＋クリームの縁」を出すために、
    //  Build 時に一時カメラで字のアルファだけ撮り、CPU側でグラデと縁を合成して焼く。
    //  実行時はただの RawImage ＝ 追加コストゼロ。
    // ============================================================
    static bool BakeGlyphIfMissing(string path, string glyph)
    {
        if (AssetDatabase.LoadAssetAtPath<Texture2D>(path) != null) { return false; }

        var mask = RenderGlyphMask(glyph, GlyphTexW, GlyphTexH, GlyphBakeFontSize);
        if (mask == null) { return false; }

        var px = ComposeGlyph(mask, GlyphTexW, GlyphTexH);
        WriteTexture(path, GlyphTexW, GlyphTexH, px);
        return true;
    }

    // 字のアルファだけを撮る。専用レイヤ＋専用カメラなので、開いているシーンは写り込まない。
    static float[] RenderGlyphMask(string glyph, int w, int h, int fontSize)
    {
        var font = AssetDatabase.LoadAssetAtPath<Font>(BrushFontPath);
        if (font == null)
        {
            Debug.LogWarning("[BuildCallEffect] 筆書体フォントが無いので焼き筆文字を作れません: "
                + BrushFontPath);
            return null;
        }

        const int BakeLayer = 31;

        var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
        rt.Create();

        var camGo = new GameObject("__kmj_glyph_cam");
        camGo.hideFlags = HideFlags.HideAndDontSave;
        camGo.layer = BakeLayer;
        camGo.transform.position = new Vector3(0f, 0f, -10f);
        var cam = camGo.AddComponent<Camera>();
        cam.enabled = false;
        cam.orthographic = true;
        cam.orthographicSize = h * 0.5f;
        cam.aspect = (float)w / h;
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 100f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
        cam.cullingMask = 1 << BakeLayer;
        cam.allowHDR = false;
        cam.allowMSAA = false;
        cam.targetTexture = rt;

        var canvasGo = new GameObject("__kmj_glyph_canvas", typeof(RectTransform));
        canvasGo.hideFlags = HideFlags.HideAndDontSave;
        canvasGo.layer = BakeLayer;
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = cam;
        var crt = canvasGo.GetComponent<RectTransform>();
        crt.position = Vector3.zero;
        crt.rotation = Quaternion.identity;
        crt.localScale = Vector3.one;
        crt.sizeDelta = new Vector2(w, h);

        var textGo = new GameObject("__kmj_glyph_text", typeof(RectTransform));
        textGo.hideFlags = HideFlags.HideAndDontSave;
        textGo.layer = BakeLayer;
        textGo.transform.SetParent(canvasGo.transform, false);
        var trt = textGo.GetComponent<RectTransform>();
        trt.anchorMin = new Vector2(0.5f, 0.5f);
        trt.anchorMax = new Vector2(0.5f, 0.5f);
        trt.pivot = new Vector2(0.5f, 0.5f);
        trt.anchoredPosition = Vector2.zero;
        trt.sizeDelta = new Vector2(w, h);

        var text = textGo.AddComponent<Text>();
        text.font = font;
        text.fontSize = fontSize;
        text.fontStyle = FontStyle.Normal;
        text.alignment = TextAnchor.MiddleCenter;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.resizeTextForBestFit = false;
        text.raycastTarget = false;
        text.color = Color.white;
        text.text = glyph;

        // 動的フォントは要求しないとアトラスに字が入らない＝真っ白のまま撮れてしまう。
        font.RequestCharactersInTexture(glyph, fontSize, FontStyle.Normal);
        Canvas.ForceUpdateCanvases();
        text.SetAllDirty();
        Canvas.ForceUpdateCanvases();

        cam.Render();

        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var shot = new Texture2D(w, h, TextureFormat.RGBA32, false);
        shot.ReadPixels(new Rect(0f, 0f, w, h), 0, 0);
        shot.Apply();
        RenderTexture.active = prev;

        var raw = shot.GetPixels();
        var mask = new float[w * h];
        float peak = 0f;
        for (int i = 0; i < raw.Length; ++i)
        {
            mask[i] = raw[i].a;
            if (raw[i].a > peak) { peak = raw[i].a; }
        }

        Object.DestroyImmediate(shot);
        Object.DestroyImmediate(textGo);
        Object.DestroyImmediate(canvasGo);
        Object.DestroyImmediate(camGo);
        rt.Release();
        Object.DestroyImmediate(rt);

        if (peak < 0.2f)
        {
            Debug.LogWarning("[BuildCallEffect] 焼き筆文字「" + glyph + "」が空でした。"
                + "フォントのアトラス生成に失敗しています。");
            return null;
        }
        return mask;
    }

    // アルファのマスクへ「太らせ」「縦グラデ」「クリームの縁」を合成する。
    static Color[] ComposeGlyph(float[] mask, int w, int h)
    {
        // 距離場を1回だけ作り、太らせと縁の両方に使う。
        var dist = DistanceFromMask(mask, w, h);

        var fill = new float[w * h];
        var rim = new float[w * h];
        for (int i = 0; i < fill.Length; ++i)
        {
            float d = dist[i];
            fill[i] = Mathf.Max(mask[i], Mathf.Clamp01(GlyphBoldPixels + 1.0f - d));
            rim[i] = Mathf.Clamp01((GlyphBoldPixels + GlyphRimPixels + 1.5f - d) / 1.5f);
            if (fill[i] > rim[i]) { rim[i] = fill[i]; }
        }

        // グラデの範囲は字の実測バウンディングボックス。キャンバス全体で取ると
        // 余白まで勘定に入って、字にかかるグラデが眠くなる。
        int top = 0;
        int bottom = h - 1;
        bool found = false;
        for (int y = 0; y < h; ++y)
        {
            bool any = false;
            int row = y * w;
            for (int x = 0; x < w; ++x) { if (fill[row + x] > 0.5f) { any = true; break; } }
            if (!any) { continue; }
            if (!found) { bottom = y; top = y; found = true; }
            if (y < bottom) { bottom = y; }
            if (y > top) { top = y; }
        }
        if (!found) { bottom = 0; top = h - 1; }
        float span = Mathf.Max(1f, top - bottom);

        var px = new Color[w * h];
        for (int y = 0; y < h; ++y)
        {
            float t = Mathf.Clamp01((y - bottom) / span);   // 0=下 1=上
            Color c = t < 0.55f
                ? Color.Lerp(GlyphBottomColor, GlyphMidColor, t / 0.55f)
                : Color.Lerp(GlyphMidColor, GlyphTopColor, (t - 0.55f) / 0.45f);

            int row = y * w;
            for (int x = 0; x < w; ++x)
            {
                int i = row + x;
                float a = fill[i];

                // 透明な場所まで縁色で塗っておく。こうしないとバイリニア補間で
                // 縁の外側に黒いフリンジが出る（ストレートアルファの定石）。
                Color rgb = Color.Lerp(GlyphRimColor, c, a);
                float outA = Mathf.Max(a, rim[i] * GlyphRimColor.a);
                px[i] = new Color(rgb.r, rgb.g, rgb.b, outA);
            }
        }
        return px;
    }

    // 字の外側への距離（px）。チャンファー3-4近似の2パス。
    // 最大値フィルタで代用すると角が四角くなるので使わない。
    static float[] DistanceFromMask(float[] mask, int w, int h)
    {
        const float Far = 1e9f;
        var d = new float[w * h];
        for (int i = 0; i < d.Length; ++i) { d[i] = mask[i] > 0.5f ? 0f : Far; }

        for (int y = 0; y < h; ++y)
        {
            int row = y * w;
            for (int x = 0; x < w; ++x)
            {
                float m = d[row + x];
                if (x > 0) { m = Mathf.Min(m, d[row + x - 1] + 3f); }
                if (y > 0)
                {
                    int up = row - w;
                    m = Mathf.Min(m, d[up + x] + 3f);
                    if (x > 0) { m = Mathf.Min(m, d[up + x - 1] + 4f); }
                    if (x < w - 1) { m = Mathf.Min(m, d[up + x + 1] + 4f); }
                }
                d[row + x] = m;
            }
        }
        for (int y = h - 1; y >= 0; --y)
        {
            int row = y * w;
            for (int x = w - 1; x >= 0; --x)
            {
                float m = d[row + x];
                if (x < w - 1) { m = Mathf.Min(m, d[row + x + 1] + 3f); }
                if (y < h - 1)
                {
                    int dn = row + w;
                    m = Mathf.Min(m, d[dn + x] + 3f);
                    if (x < w - 1) { m = Mathf.Min(m, d[dn + x + 1] + 4f); }
                    if (x > 0) { m = Mathf.Min(m, d[dn + x - 1] + 4f); }
                }
                d[row + x] = m;
            }
        }

        for (int i = 0; i < d.Length; ++i) { d[i] = d[i] / 3f; }   // チャンファーはpx*3
        return d;
    }

    // ============================================================
    // 共通ヘルパ（BuildReachDiscardEffect と同等の実装）
    // ============================================================
    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) { return; }

        var parts = path.Split('/');
        var current = parts[0];
        for (int i = 1; i < parts.Length; ++i)
        {
            var next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
            {
                AssetDatabase.CreateFolder(current, parts[i]);
            }
            current = next;
        }
    }

    static bool WriteTextureIfMissing(string path, int width, int height, Color[] pixels)
    {
        if (AssetDatabase.LoadAssetAtPath<Texture2D>(path) != null) { return false; }
        WriteTexture(path, width, height, pixels);
        return true;
    }

    static void WriteTexture(string path, int width, int height, Color[] pixels)
    {
        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
        tex.SetPixels(pixels);
        tex.Apply();

        var png = tex.EncodeToPNG();
        Object.DestroyImmediate(tex);
        System.IO.File.WriteAllBytes(path, png);
        AssetDatabase.ImportAsset(path);

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

    static bool EnsureProgramAsset(string scriptPath, string assetPath)
    {
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
        if (script == null)
        {
            Debug.LogError("[WireCallEffect] スクリプトが見つかりません: " + scriptPath);
            return false;
        }
        var type = script.GetClass();
        if (type != null && UdonSharpProgramAsset.GetProgramAssetForClass(type) != null) { return false; }
        if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(assetPath) != null) { return false; }

        var programAsset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
        programAsset.sourceCsScript = script;
        AssetDatabase.CreateAsset(programAsset, assetPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[WireCallEffect] U#プログラムアセットを生成: " + assetPath);
        return true;
    }

    static UdonSharpBehaviour EnsureUSharpComponent(GameObject go, System.Type type)
    {
        var proxy = (UdonSharpBehaviour)go.GetComponent(type);
        if (proxy != null)
        {
            if (UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy) != null) { return proxy; }

            Debug.LogWarning("[WireCallEffect] " + type.Name + " の backing UdonBehaviour が無いため作り直します。");
            Object.DestroyImmediate(proxy);
            foreach (var ub in go.GetComponents<UdonBehaviour>())
            {
                if (ub.programSource == null) { Object.DestroyImmediate(ub); }
            }
        }

        var added = (UdonSharpBehaviour)UdonSharpUndo.AddComponent(go, type);
        if (added != null && UdonSharpEditorUtility.GetBackingUdonBehaviour(added) == null)
        {
            Debug.LogError("[WireCallEffect] " + type.Name + " の追加に失敗（プログラムアセット未コンパイル？）。");
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

    // 指定した順にきょうだいを並べ直す（描画順の固定）。
    static void SetSiblingOrder(Transform parent, string[] orderedNames)
    {
        for (int i = 0; i < orderedNames.Length; ++i)
        {
            var c = parent.Find(orderedNames[i]);
            if (c != null) { c.SetSiblingIndex(i); }
        }
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
