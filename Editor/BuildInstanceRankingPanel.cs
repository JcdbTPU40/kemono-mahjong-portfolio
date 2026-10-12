#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using TMPro;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using SuzuFactory.AozoraMahjong;
using VRC.SDK3.Components;
using VRC.Udon;

/// <summary>
/// インスタンス内ランキング表（デイリー）の World Space パネルを生成し、
/// InstanceRankingBoard と PlayerStatsStore・ボタンを配線する。
/// 同名オブジェクトを再利用するため何度実行しても重複しない。
/// 既存パネルの位置・回転には触れないので、Sceneビューで自由に置ける。
/// </summary>
public static class BuildInstanceRankingPanel
{
    private const string RootNameDaily = "InstanceRankingPanel"; // 本日のスコア
    private const string RootNameRank = "InstanceRankPanel";     // 段位
    private const string RootNameBest = "InstanceBestAgariPanel"; // 本日の最高和了
    private const string ProgramAssetDir = "Assets/Mahjong/scene_UdonProgramSources";
    private const string ProgramAssetPath =
        ProgramAssetDir + "/InstanceRankingBoard Udon C# Program Asset.asset";
    private const string ScriptPath = "Assets/Mahjong/Sources/InstanceRankingBoard.cs";
    private const string FontAssetPath = "Assets/Mahjong/Fonts/NotoSansJP-Bold SDF.asset";

    // --- 配置（初回生成時のみ。既存パネルの位置・回転には触らない）---
    // MainMenu の隣の壁（z≈-1.53）に、スコア版を上・段位版を下へずらして並べる。
    // 2026-09-03 に実際の見え方を見て手で決めた位置。ここの値を変えても
    // 既にあるパネルは動かない（新規生成時だけに使う）。
    private static readonly Vector3 DefaultPosDaily = new Vector3(3.542f, 3.436f, -1.529f);
    private static readonly Vector3 DefaultPosRank = new Vector3(2.358f, 2.636f, -1.528f);
    // 最高和了版は段位版の真上。段位版の上端(2.636+0.78=3.416)に接する高さ＝中心3.816。
    // ここより上げると2階の梁 Wood_frame_2F（y 4.180〜）がタイトルを隠す＝隙間は取れない。
    // 右隣のスコア版（x 2.967〜4.117）とはX方向で重ならない。
    private static readonly Vector3 DefaultPosBest = new Vector3(2.358f, 3.816f, -1.528f);
    private static readonly Vector3 DefaultEuler = new Vector3(0f, 180f, 0f);
    private const float CanvasScale = 0.001f;

    // --- レイアウト（px＝Canvasローカル）---
    // 参照デザイン：赤地 → 木枠（9スライス）→ 濃い赤の地 → 横長プレートが縦に並ぶ。
    private const float PanelW = 1150f;
    private const float FrameInset = 18f;  // 赤地の幅
    private const float BoardInset = 56f;  // 木枠の内側
    private const float ContentX = 66f;
    private const float ContentW = 1018f;
    private const float RowX = ContentX;
    private const float RowW = ContentW;

    // 幅と左右の位置は3枚とも共通（横1150で固定）。
    private const float ScrewLeftX = 14f;
    private const float ScrewRightX = 984f;
    private const float ColBadgeX = 40f;
    private const float ColBadgeW = 76f;
    private const float ColNameX = 128f;

    // --- 縦方向と一部の列幅は SetLayout がパネルごとに入れ直す ---
    // 通常版（スコア・段位）は高さ1560で10行、最高和了版は高さ800で5行。
    private static float panelH_;
    private static int rows_;
    private static float rowH_, rowTop_, rowPitch_, badgeH_, badgeFont_, screwSize_;
    private static float colNameW_, colRankX_, colRankW_, colScoreX_, colScoreW_, colGameX_, colGameW_;
    private static float titleY_, titleH_, titleFont_;
    private static float subtitleY_, subtitleH_, subtitleFont_;
    private static float dateY_, dateH_, dateFont_;
    private static float headerY_, headerH_, headerFont_;
    private static float emptyY_, emptyH_, emptyFont_;
    private static float selfY_, selfH_, selfFont_;
    private static float buttonY_, buttonH_, buttonFont_, refreshFont_;
    private static float footerY_, footerH_, footerFont_;
    private static float cellNameFont_, cellRankFont_, cellScoreFont_, cellGameFont_;

    private static void SetLayout(int mode)
    {
        if (mode == InstanceRankingBoard.MODE_BEST)
        {
            // 高さ800。木枠の内側（56〜744px）に見出し・5行・自分の行・ボタン・脚注を収める。
            panelH_ = 800f;
            rows_ = 5;
            rowH_ = 72f; rowTop_ = -190f; rowPitch_ = 82f; badgeH_ = 62f; badgeFont_ = 27f; screwSize_ = 17f;
            // 列幅は実測で決めた（preferredWidth）。段位列は英語の
            // 「Celestial Beast★3」=168px、手役列は「13 han 110 fu」=124px が入る幅。
            colNameW_ = 306f;
            colRankX_ = 442f; colRankW_ = 176f;
            colScoreX_ = 628f; colScoreW_ = 140f;
            colGameX_ = 800f; colGameW_ = 174f;
            titleY_ = -46f; titleH_ = 48f; titleFont_ = 36f;
            subtitleY_ = -96f; subtitleH_ = 30f; subtitleFont_ = 21f;
            dateY_ = -126f; dateH_ = 26f; dateFont_ = 18f;
            headerY_ = -160f; headerH_ = 28f; headerFont_ = 19f;
            emptyY_ = -246f; emptyH_ = 150f; emptyFont_ = 24f;
            selfY_ = -596f; selfH_ = 32f; selfFont_ = 21f;
            buttonY_ = -632f; buttonH_ = 56f; buttonFont_ = 23f; refreshFont_ = 20f;
            footerY_ = -694f; footerH_ = 24f; footerFont_ = 15f;
            cellNameFont_ = 23f; cellRankFont_ = 19f; cellScoreFont_ = 25f; cellGameFont_ = 19f;
            return;
        }

        panelH_ = 1560f;
        rows_ = 10;
        rowH_ = 88f; rowTop_ = -232f; rowPitch_ = 98f; badgeH_ = 76f; badgeFont_ = 33f; screwSize_ = 20f;
        colNameW_ = 356f;
        colRankX_ = 490f; colRankW_ = 170f;
        colScoreX_ = 668f; colScoreW_ = 212f; // 段位版の「1180 / 1400」が入る幅
        colGameX_ = 886f; colGameW_ = 88f;
        titleY_ = -66f; titleH_ = 58f; titleFont_ = 42f;
        subtitleY_ = -126f; subtitleH_ = 34f; subtitleFont_ = 23f;
        dateY_ = -162f; dateH_ = 30f; dateFont_ = 20f;
        headerY_ = -196f; headerH_ = 32f; headerFont_ = 21f;
        emptyY_ = -560f; emptyH_ = 200f; emptyFont_ = 30f;
        selfY_ = -1216f; selfH_ = 46f; selfFont_ = 26f;
        buttonY_ = -1266f; buttonH_ = 82f; buttonFont_ = 28f; refreshFont_ = 25f;
        footerY_ = -1360f; footerH_ = 100f; footerFont_ = 19f;
        cellNameFont_ = 26f; cellRankFont_ = 22f; cellScoreFont_ = 26f; cellGameFont_ = 19f;
    }

    // --- 色（参照画像から）---
    private static readonly Color RedBackColor = new Color(0.784f, 0.071f, 0.118f, 1f);
    private static readonly Color BoardColor = new Color(0.353f, 0.082f, 0.094f, 1f);
    private static readonly Color TitleColor = new Color(0.99f, 0.95f, 0.85f, 1f);
    private static readonly Color SubTextColor = new Color(0.91f, 0.81f, 0.63f, 1f);
    private static readonly Color FaintTextColor = new Color(0.79f, 0.68f, 0.52f, 1f);
    private static readonly Color HeaderTextColor = new Color(0.88f, 0.75f, 0.54f, 1f);
    private static readonly Color SelfTextColor = new Color(1f, 0.88f, 0.55f, 1f);
    private static readonly Color BtnColor = new Color(0.37f, 0.13f, 0.13f, 1f);
    private static readonly Color RefreshColor = new Color(0.48f, 0.18f, 0.18f, 1f);

    // プレートの上に乗る文字は暗い色でないと読めない。
    private static readonly Color TextOnTop3 = new Color(0.24f, 0.16f, 0.05f, 1f);
    private static readonly Color SubTextOnTop3 = new Color(0.42f, 0.31f, 0.13f, 1f);
    private static readonly Color TextOnNormal = new Color(0.14f, 0.15f, 0.18f, 1f);
    private static readonly Color SubTextOnNormal = new Color(0.36f, 0.38f, 0.43f, 1f);
    // 自分の行。プレートはごく薄く青味にするだけにし、名前の色で見せる
    // （強い青を金プレートに乗せると緑に濁る）。
    private static readonly Color PlateTintSelf = new Color(0.88f, 0.94f, 1f, 1f);
    private static readonly Color NameColorSelf = new Color(0.07f, 0.27f, 0.50f, 1f);


    private static TMP_FontAsset font_;

    // 現在ビルド中のパネルの文言（BuildOne が先頭で入れ替える）。
    private static string title_ = "";
    private static string subtitle_ = "";
    private static string dateLine_ = "";
    private static string valueHeader_ = "";
    private static string gamesHeader_ = "";
    private static string emptyMessage_ = "";
    private static string footer_ = "";

    [MenuItem("Tools/Kemono Mahjong/Build Instance Ranking Panel")]
    public static void Run()
    {
        font_ = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
        if (font_ == null)
        {
            Debug.LogError("[BuildInstanceRankingPanel] 日本語TMPフォントが見つかりません: " + FontAssetPath);
            return;
        }

        if (EnsureProgramAsset())
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[BuildInstanceRankingPanel] U# Program Assetを生成しました。" +
                      "コンパイル完了後、もう一度 Build Instance Ranking Panel を実行してください。");
            return;
        }

        PlayerStatsStore store = UnityEngine.Object.FindObjectOfType<PlayerStatsStore>(true);
        if (store == null)
        {
            Debug.LogError("[BuildInstanceRankingPanel] PlayerStatsStoreが見つかりません。先に Wire Main Menu を実行してください。");
            return;
        }

        // 1枚目：本日のスコア（デイリー）
        title_ = "デイリーランキング  /  DAILY RANKING";
        subtitle_ = "本日の段位戦スコア";
        dateLine_ = "集計日 ----/--/--（JST 朝5時リセット）";
        valueHeader_ = "スコア";
        gamesHeader_ = "対局";
        emptyMessage_ = "本日の段位戦の記録がまだありません。\n段位戦（三麻／四麻）を打つとここに載ります。";
        footer_ = "スコア＝素点（1000点＝1）＋順位点（1位+30 / 2位+10 / 3位-10 / 4位-30・三麻は+30/0/-30・東風戦は半分）。\n" +
                  "フリー対局は集計外。JST 朝5時にリセット。退出した人は表から外れます。";
        bool okDaily = BuildOne(RootNameDaily, DefaultPosDaily, InstanceRankingBoard.MODE_DAILY, store);

        // 2枚目：段位（PlayerDataの永続値をそのまま並べる）
        title_ = "段位ランキング  /  RANK STANDINGS";
        subtitle_ = "いまこのインスタンスにいる人の段位順";
        dateLine_ = "段位は全モード共通・段位戦だけで変動します";
        valueHeader_ = "ポイント";
        gamesHeader_ = "通算";
        emptyMessage_ = "段位データを読めるプレイヤーがいません。\n入場直後は復元待ちなので、数秒待ってください。";
        footer_ = "ポイントは同じ段位の中での位置（現在pt / 昇段pt）。天獣は天珠の値を出します。\n" +
                  "通算は段位戦（段位が動いた対局）の数で2026-09-14から数え直し。入場直後でまだ読めない人は載りません。";
        bool okRank = BuildOne(RootNameRank, DefaultPosRank, InstanceRankingBoard.MODE_RANK, store);

        // 3枚目：本日の最高和了（1回の和了の最大打点）。段位版の真上・高さ800の小型パネル。
        title_ = "最高和了ランキング  /  BEST HAND TODAY";
        subtitle_ = "本日の段位戦で出た1回の最高打点";
        dateLine_ = "集計日 ----/--/--（JST 朝5時リセット）";
        valueHeader_ = "打点";
        gamesHeader_ = "手役";
        emptyMessage_ = "本日の和了がまだありません。\n段位戦（三麻／四麻）で和了るとここに載ります。";
        footer_ = "打点は1回の和了で受け取った点数（供託・積み棒を含む）。段位戦のみ集計・JST 朝5時リセット。";
        bool okBest = BuildOne(RootNameBest, DefaultPosBest, InstanceRankingBoard.MODE_BEST, store);

        if (okDaily && okRank && okBest)
        {
            Debug.Log("[BuildInstanceRankingPanel] 完了: スコア版・段位版・最高和了版の3枚を生成・配線しました。");
        }
    }

    private static bool BuildOne(string rootName, Vector3 defaultPos, int mode, PlayerStatsStore store)
    {
        // 高さ・行数・列幅はモードで変わる。UIを触る前に必ず入れ替える。
        SetLayout(mode);

        GameObject root = FindSceneObject(rootName);
        if (root == null)
        {
            root = new GameObject(rootName, typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(root, "Build Instance Ranking Panel");
            root.transform.position = defaultPos;
            root.transform.rotation = Quaternion.Euler(DefaultEuler);
            root.transform.localScale = new Vector3(CanvasScale, CanvasScale, CanvasScale);
        }
        EnsureRootComponents(root);
        BuildUi(root.transform);
        SetLayerRecursively(root, 0);

        InstanceRankingBoard board = (InstanceRankingBoard)EnsureUSharpComponent(root, typeof(InstanceRankingBoard));
        if (board == null) { return false; }

        Transform t = root.transform;
        Transform table = t.Find("Panel/Table");

        board.statsStore = store;
        board.mode = mode;

        GameObject[] rowObjects = new GameObject[rows_];
        Image[] plates = new Image[rows_];
        Image[] badges = new Image[rows_];
        Image[] screwL = new Image[rows_];
        Image[] screwR = new Image[rows_];
        TextMeshProUGUI[] place = new TextMeshProUGUI[rows_];
        TextMeshProUGUI[] name = new TextMeshProUGUI[rows_];
        TextMeshProUGUI[] rank = new TextMeshProUGUI[rows_];
        RawImage[] rankImages = new RawImage[rows_];
        TextMeshProUGUI[] rankLevels = new TextMeshProUGUI[rows_];
        TextMeshProUGUI[] score = new TextMeshProUGUI[rows_];
        TextMeshProUGUI[] games = new TextMeshProUGUI[rows_];
        for (int i = 0; i < rows_; ++i)
        {
            Transform row = table.Find("Row" + i.ToString());
            rowObjects[i] = row.gameObject;
            plates[i] = row.GetComponent<Image>();
            badges[i] = row.Find("Badge").GetComponent<Image>();
            screwL[i] = row.Find("ScrewLeft").GetComponent<Image>();
            screwR[i] = row.Find("ScrewRight").GetComponent<Image>();
            place[i] = row.Find("Badge/Place").GetComponent<TextMeshProUGUI>();
            name[i] = row.Find("Name").GetComponent<TextMeshProUGUI>();
            rank[i] = row.Find("Rank").GetComponent<TextMeshProUGUI>();
            rankImages[i] = row.Find("RankIcon").GetComponent<RawImage>();
            rankLevels[i] = row.Find("RankLevel").GetComponent<TextMeshProUGUI>();
            score[i] = row.Find("Score").GetComponent<TextMeshProUGUI>();
            games[i] = row.Find("Games").GetComponent<TextMeshProUGUI>();
        }
        board.rowObjects = rowObjects;
        board.rowPlates = plates;
        board.rowBadges = badges;
        board.rowScrewLeft = screwL;
        board.rowScrewRight = screwR;
        board.rowPlaceTexts = place;
        board.rowNameTexts = name;
        board.rowRankTexts = rank;
        board.rowRankImages = rankImages;
        board.rowRankLevelTexts = rankLevels;
        board.rowScoreTexts = score;
        board.rowGameTexts = games;

        board.dateText = FindTmp(t, "Panel/DateText");
        board.pageText = FindTmp(t, "Panel/PageText");
        board.selfText = FindTmp(t, "Panel/SelfText");
        board.emptyText = FindTmp(t, "Panel/EmptyText");

        board.plateTop3 = LoadSprite(BuildRankingPanelArt.PlateTopPath);
        board.plateNormal = LoadSprite(BuildRankingPanelArt.PlateNormalPath);
        board.badgeCrown1 = LoadSprite(BuildRankingPanelArt.Crown1Path);
        board.badgeCrown2 = LoadSprite(BuildRankingPanelArt.Crown2Path);
        board.badgeCrown3 = LoadSprite(BuildRankingPanelArt.Crown3Path);
        board.badgeTag = LoadSprite(BuildRankingPanelArt.TagPath);
        board.screwGold = LoadSprite(BuildRankingPanelArt.ScrewGoldPath);
        board.screwSilver = LoadSprite(BuildRankingPanelArt.ScrewSilverPath);
        board.rankEmblems = new Texture[BuildRankingPanelArt.RankEmblemPaths.Length];
        for (int i = 0; i < board.rankEmblems.Length; ++i)
        {
            board.rankEmblems[i] = LoadRankEmblem(BuildRankingPanelArt.RankEmblemPaths[i]);
        }

        // U#のシリアライズ済み値はスクリプト側の既定値を変えても更新されない。
        // 見た目の確定値はここで毎回代入する（call-effect で踏んだ地雷）。
        board.plateTintSelf = PlateTintSelf;
        board.nameColorSelf = NameColorSelf;
        board.plateTintNormal = Color.white;
        board.textOnTop3 = TextOnTop3;
        board.subTextOnTop3 = SubTextOnTop3;
        board.textOnNormal = TextOnNormal;
        board.subTextOnNormal = SubTextOnNormal;
        board.placeNumberColor = Color.white;
        board.scoreColorPlus = new Color(0.09f, 0.42f, 0.20f, 1f);
        board.scoreColorMinus = new Color(0.62f, 0.15f, 0.12f, 1f);
        board.refreshInterval = 5f;

        UdonSharpEditorUtility.CopyProxyToUdon(board);
        EditorUtility.SetDirty(board);

        UdonBehaviour udon = UdonSharpEditorUtility.GetBackingUdonBehaviour(board);
        if (udon == null)
        {
            Debug.LogError("[BuildInstanceRankingPanel] " + rootName + ": backing UdonBehaviour が見つかりません。");
            return false;
        }

        int wired = 0;
        wired += WireButton(t, "Panel/BtnPrev", udon, "OnPrevPage");
        wired += WireButton(t, "Panel/BtnNext", udon, "OnNextPage");
        wired += WireButton(t, "Panel/BtnRefresh", udon, "OnRefresh");

        EditorSceneManager.MarkSceneDirty(root.scene);
        Debug.Log("[BuildInstanceRankingPanel] " + rootName + ": 行" + rows_ + " / ボタン配線" + wired + "件" +
                  " / mode=" + mode +
                  "\nパネル中心=" + root.transform.position.ToString("F3") +
                  " / 大きさ=" + (PanelW * CanvasScale).ToString("F2") + " x " +
                  (panelH_ * CanvasScale).ToString("F2") + " m");
        return wired == 3;
    }

    [MenuItem("Tools/Kemono Mahjong/Validate Instance Ranking Panel")]
    public static void Validate()
    {
        bool okDaily = ValidateOne(RootNameDaily, InstanceRankingBoard.MODE_DAILY);
        bool okRank = ValidateOne(RootNameRank, InstanceRankingBoard.MODE_RANK);
        bool okBest = ValidateOne(RootNameBest, InstanceRankingBoard.MODE_BEST);
        if (okDaily && okRank && okBest)
        {
            Debug.Log("[ValidateInstanceRankingPanel] 3枚とも OK（参照欠け0）。");
        }
    }

    private static bool ValidateOne(string rootName, int expectedMode)
    {
        // 期待する行数・高さはモードで変わる（CheckArray が rows_ を見る）。
        SetLayout(expectedMode);

        GameObject root = FindSceneObject(rootName);
        if (root == null)
        {
            Debug.LogError("[ValidateInstanceRankingPanel] " + rootName + " がシーンにありません。");
            return false;
        }

        InstanceRankingBoard board = root.GetComponent<InstanceRankingBoard>();
        if (board == null)
        {
            Debug.LogError("[ValidateInstanceRankingPanel] " + rootName + ": InstanceRankingBoard がありません。");
            return false;
        }

        List<string> missing = new List<string>();
        if (board.statsStore == null) { missing.Add("statsStore"); }
        if (board.mode != expectedMode) { missing.Add("mode（" + board.mode + "・期待" + expectedMode + "）"); }
        CheckArray(missing, "rowObjects", board.rowObjects != null ? board.rowObjects.Length : 0, board.rowObjects);
        CheckArray(missing, "rowPlates", board.rowPlates != null ? board.rowPlates.Length : 0, board.rowPlates);
        CheckArray(missing, "rowBadges", board.rowBadges != null ? board.rowBadges.Length : 0, board.rowBadges);
        CheckArray(missing, "rowScrewLeft", board.rowScrewLeft != null ? board.rowScrewLeft.Length : 0, board.rowScrewLeft);
        CheckArray(missing, "rowScrewRight", board.rowScrewRight != null ? board.rowScrewRight.Length : 0, board.rowScrewRight);
        if (board.plateTop3 == null) { missing.Add("plateTop3"); }
        if (board.plateNormal == null) { missing.Add("plateNormal"); }
        if (board.badgeCrown1 == null) { missing.Add("badgeCrown1"); }
        if (board.badgeCrown2 == null) { missing.Add("badgeCrown2"); }
        if (board.badgeCrown3 == null) { missing.Add("badgeCrown3"); }
        if (board.badgeTag == null) { missing.Add("badgeTag"); }
        if (board.screwGold == null) { missing.Add("screwGold"); }
        if (board.screwSilver == null) { missing.Add("screwSilver"); }
        CheckArray(missing, "rankEmblems", board.rankEmblems != null ? board.rankEmblems.Length : 0, board.rankEmblems, 6);
        CheckArray(missing, "rowPlaceTexts", board.rowPlaceTexts != null ? board.rowPlaceTexts.Length : 0, board.rowPlaceTexts);
        CheckArray(missing, "rowNameTexts", board.rowNameTexts != null ? board.rowNameTexts.Length : 0, board.rowNameTexts);
        CheckArray(missing, "rowRankTexts", board.rowRankTexts != null ? board.rowRankTexts.Length : 0, board.rowRankTexts);
        CheckArray(missing, "rowRankImages", board.rowRankImages != null ? board.rowRankImages.Length : 0, board.rowRankImages);
        CheckArray(missing, "rowRankLevelTexts", board.rowRankLevelTexts != null ? board.rowRankLevelTexts.Length : 0, board.rowRankLevelTexts);
        CheckArray(missing, "rowScoreTexts", board.rowScoreTexts != null ? board.rowScoreTexts.Length : 0, board.rowScoreTexts);
        CheckArray(missing, "rowGameTexts", board.rowGameTexts != null ? board.rowGameTexts.Length : 0, board.rowGameTexts);
        if (board.dateText == null) { missing.Add("dateText"); }
        if (board.pageText == null) { missing.Add("pageText"); }
        if (board.selfText == null) { missing.Add("selfText"); }
        if (board.emptyText == null) { missing.Add("emptyText"); }

        int buttons = 0;
        buttons += CheckButton(root.transform, "Panel/BtnPrev", missing);
        buttons += CheckButton(root.transform, "Panel/BtnNext", missing);
        buttons += CheckButton(root.transform, "Panel/BtnRefresh", missing);

        if (root.GetComponent<VRCUiShape>() == null) { missing.Add("VRCUiShape"); }
        if (root.GetComponent<BoxCollider>() == null) { missing.Add("BoxCollider（押せなくなる）"); }
        if (!root.activeInHierarchy) { missing.Add("非アクティブ（Sceneで有効化してください）"); }

        // プロキシだけでなく Udon のヒープも見る（値が巻き戻る事故の検出）。
        UdonBehaviour udon = UdonSharpEditorUtility.GetBackingUdonBehaviour(board);
        if (udon == null)
        {
            missing.Add("backing UdonBehaviour");
        }
        else
        {
            object heapMode;
            bool hasMode = udon.publicVariables.TryGetVariableValue("mode", out heapMode);
            if (!hasMode)
            {
                // 既定値と同じ値はヒープに乗らないので、MODE_DAILY(0) のときだけ未設定を許す。
                if (expectedMode != InstanceRankingBoard.MODE_DAILY)
                {
                    missing.Add("Udonヒープのmode（未設定＝0のまま）");
                }
            }
            else if (heapMode == null || (int)heapMode != expectedMode)
            {
                missing.Add("Udonヒープのmode（" + heapMode + "・期待" + expectedMode + "）");
            }

            object heapStore;
            if (!udon.publicVariables.TryGetVariableValue("statsStore", out heapStore) || heapStore == null)
            {
                missing.Add("UdonヒープのstatsStore");
            }
        }

        if (missing.Count == 0)
        {
            Debug.Log("[ValidateInstanceRankingPanel] " + rootName + " OK: 参照欠け0 / ボタン" + buttons +
                      "件 / 行" + (board.rowObjects != null ? board.rowObjects.Length : 0) + "件");
            return true;
        }

        Debug.LogError("[ValidateInstanceRankingPanel] " + rootName + " 欠け " + missing.Count + "件: " +
                       string.Join(", ", missing));
        return false;
    }

    private static void CheckArray(List<string> missing, string label, int length, Array array, int expectedLength = -1)
    {
        int expected = expectedLength >= 0 ? expectedLength : rows_;
        if (array == null || length != expected)
        {
            missing.Add(label + "（長さ" + length + "・期待" + expected + "）");
            return;
        }
        for (int i = 0; i < length; ++i)
        {
            UnityEngine.Object element = array.GetValue(i) as UnityEngine.Object;
            if (element == null)
            {
                missing.Add(label + "[" + i + "]");
            }
        }
    }

    private static int CheckButton(Transform root, string path, List<string> missing)
    {
        Transform t = root.Find(path);
        Button button = t != null ? t.GetComponent<Button>() : null;
        if (button == null || button.onClick.GetPersistentEventCount() == 0)
        {
            missing.Add(path + "（未配線）");
            return 0;
        }
        return 1;
    }

    // ============================================================
    // UI
    // ============================================================

    private static void EnsureRootComponents(GameObject root)
    {
        RectTransform rt = root.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(PanelW, panelH_);

        Canvas canvas = root.GetComponent<Canvas>();
        if (canvas == null) { canvas = Undo.AddComponent<Canvas>(root); }
        canvas.renderMode = RenderMode.WorldSpace;

        CanvasScaler scaler = root.GetComponent<CanvasScaler>();
        if (scaler == null) { scaler = Undo.AddComponent<CanvasScaler>(root); }
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.dynamicPixelsPerUnit = 10f;

        if (root.GetComponent<GraphicRaycaster>() == null) { Undo.AddComponent<GraphicRaycaster>(root); }
        if (root.GetComponent<VRCUiShape>() == null) { Undo.AddComponent<VRCUiShape>(root); }

        // VRCUiShape だけではレーザーが当たらない。ClientSim も実機も
        // 同じGameObjectのコライダーを物理レイキャストで拾ってから UiShape を見る。
        BoxCollider box = root.GetComponent<BoxCollider>();
        if (box == null) { box = Undo.AddComponent<BoxCollider>(root); }
        box.isTrigger = true;
        box.size = new Vector3(PanelW, panelH_, 0.31f);
        box.center = new Vector3(0f, 0f, 0.16f);
    }

    private static void BuildUi(Transform root)
    {
        // 一番下＝外周の赤地（参照デザインの額の外側）。
        Transform panel = FullPanel(root, "Panel", RedBackColor);

        // 木枠の内側の濃い赤。プレートはこの上に並ぶ。
        Transform board = Child(panel, "Board");
        Inset(board, BoardInset);
        EnsureImage(board, BoardColor).raycastTarget = false;

        // 木枠（9スライス・中央は塗らない）。Board の縁にかぶせるので後の兵弟にする。
        Transform frame = Child(panel, "Frame");
        Inset(frame, FrameInset);
        Image frameImage = EnsureImage(frame, Color.white);
        frameImage.sprite = LoadSprite(BuildRankingPanelArt.FramePath);
        frameImage.type = Image.Type.Sliced;
        frameImage.fillCenter = false;
        frameImage.raycastTarget = false;
        frame.SetSiblingIndex(1);

        Text(panel, "Title", title_, titleFont_, new Vector2(ContentX, titleY_), new Vector2(ContentW, titleH_),
            TitleColor, TextAlignmentOptions.Center);
        Text(panel, "Subtitle", subtitle_, subtitleFont_, new Vector2(ContentX, subtitleY_),
            new Vector2(ContentW, subtitleH_), SubTextColor, TextAlignmentOptions.Center);
        // スコア版・最高和了版はここへ集計日を毎回書き込む。段位版はこの文言がそのまま残る。
        Text(panel, "DateText", dateLine_, dateFont_, new Vector2(ContentX, dateY_),
            new Vector2(ContentW, dateH_), FaintTextColor, TextAlignmentOptions.Center);

        // 見出し（プレートと同じ列位置に揃えるので、幅は行と同じ）。
        Transform header = Child(panel, "Header");
        SetTopRect(header, new Vector2(RowX, headerY_), new Vector2(RowW, headerH_));
        HeaderLabel(header, "HPlace", "順位", ColBadgeX, ColBadgeW, TextAlignmentOptions.Center);
        HeaderLabel(header, "HName", "プレイヤー", ColNameX, colNameW_, TextAlignmentOptions.Left);
        HeaderLabel(header, "HRank", "段位", colRankX_, colRankW_, TextAlignmentOptions.Center);
        HeaderLabel(header, "HScore", valueHeader_, colScoreX_, colScoreW_, TextAlignmentOptions.Right);
        HeaderLabel(header, "HGames", gamesHeader_, colGameX_, colGameW_, TextAlignmentOptions.Right);

        Transform table = Child(panel, "Table");
        SetTopRect(table, Vector2.zero, new Vector2(PanelW, panelH_));
        for (int i = 0; i < rows_; ++i)
        {
            BuildRow(table, i);
        }
        // 行数を減らしてビルドし直したときに、前回の余った行が残らないようにする。
        for (int i = rows_; i < 24; ++i)
        {
            Transform stale = table.Find("Row" + i.ToString());
            if (stale != null) { Undo.DestroyObjectImmediate(stale.gameObject); }
        }

        TextMeshProUGUI empty = Text(panel, "EmptyText", emptyMessage_, emptyFont_,
            new Vector2(140f, emptyY_), new Vector2(870f, emptyH_), SubTextColor, TextAlignmentOptions.Center);
        empty.gameObject.SetActive(true);

        Text(panel, "SelfText", "", selfFont_, new Vector2(ContentX, selfY_), new Vector2(ContentW, selfH_),
            SelfTextColor, TextAlignmentOptions.Center);

        Button(panel, "BtnPrev", "◀ 前へ", new Vector2(ContentX, buttonY_), new Vector2(190f, buttonH_),
            buttonFont_, BtnColor);
        Text(panel, "PageText", "1 / 1", buttonFont_ - 1f, new Vector2(270f, buttonY_),
            new Vector2(300f, buttonH_), TitleColor, TextAlignmentOptions.Center);
        Button(panel, "BtnNext", "次へ ▶", new Vector2(585f, buttonY_), new Vector2(190f, buttonH_),
            buttonFont_, BtnColor);
        Button(panel, "BtnRefresh", "更新 / REFRESH", new Vector2(790f, buttonY_), new Vector2(294f, buttonH_),
            refreshFont_, RefreshColor);

        Text(panel, "Footer", footer_, footerFont_, new Vector2(ContentX, footerY_),
            new Vector2(ContentW, footerH_), FaintTextColor, TextAlignmentOptions.TopLeft);
    }

    private static void BuildRow(Transform table, int index)
    {
        bool top3 = index < 3;

        Transform row = Child(table, "Row" + index.ToString());
        SetTopRect(row, new Vector2(RowX, rowTop_ - rowPitch_ * index), new Vector2(RowW, rowH_));
        Image plate = EnsureImage(row, Color.white);
        plate.sprite = LoadSprite(top3 ? BuildRankingPanelArt.PlateTopPath : BuildRankingPanelArt.PlateNormalPath);
        plate.type = Image.Type.Sliced;
        plate.raycastTarget = false;

        Screw(row, "ScrewLeft", ScrewLeftX, top3);
        Screw(row, "ScrewRight", ScrewRightX, top3);
        BuildBadge(row, index, top3);

        Color main = top3 ? TextOnTop3 : TextOnNormal;
        Color sub = top3 ? SubTextOnTop3 : SubTextOnNormal;
        Cell(row, "Name", "-", cellNameFont_, ColNameX, colNameW_, TextAlignmentOptions.Left, main);
        Cell(row, "Rank", "-", cellRankFont_, colRankX_, colRankW_, TextAlignmentOptions.Center, sub);

        // 紋章は Textures/UI/rank の実素材（Texture2D・1024x1024）＝RawImage で出す。
        // RawImage に preserveAspect は無いので、枠を正方形にして歪みを防ぐ。
        Transform rankIcon = Child(row, "RankIcon");
        float emblemSize = Mathf.Min(76f, rowH_ - 12f);
        CenteredRect(rankIcon, colRankX_ + 4f, emblemSize, emblemSize);
        RawImage rankImage = EnsureRawImage(rankIcon);
        rankImage.texture = LoadRankEmblem(BuildRankingPanelArt.RankEmblemPaths[0]);
        rankImage.raycastTarget = false;
        rankIcon.gameObject.SetActive(false);

        Cell(row, "RankLevel", "★1", cellRankFont_, colRankX_ + emblemSize + 8f,
            colRankW_ - emblemSize - 8f, TextAlignmentOptions.Center, sub);
        row.Find("RankLevel").gameObject.SetActive(false);

        // 段位は「紋章＋★N」か段位名テキストのどちらか一方だけを出す。
        // 実行時は FillRow が切り替えるが、ビルド直後の見た目も文字側に固定する
        //（ここを書かないと、前に紋章側で残った行がそのまま残る）。
        row.Find("Rank").gameObject.SetActive(true);

        Cell(row, "Score", "0", cellScoreFont_, colScoreX_, colScoreW_, TextAlignmentOptions.Right, main);
        Cell(row, "Games", "-", cellGameFont_, colGameX_, colGameW_, TextAlignmentOptions.Right, sub);

        row.gameObject.SetActive(index == 0);
    }

    private static void Screw(Transform row, string name, float x, bool gold)
    {
        Transform t = Child(row, name);
        CenteredRect(t, x, screwSize_, screwSize_);
        Image image = EnsureImage(t, Color.white);
        image.sprite = LoadSprite(gold ? BuildRankingPanelArt.ScrewGoldPath : BuildRankingPanelArt.ScrewSilverPath);
        image.type = Image.Type.Simple;
        image.preserveAspect = true;
        image.raycastTarget = false;
    }

    // 左端のバッジと、その上に乗る番号。番号をバッジの子にすることで
    // 王冠でもタグでも同じ位置に乗る。中心より少し下げるのは、
    // 王冠の山の間ではなく胴体に乗せるため。
    private static void BuildBadge(Transform row, int index, bool top3)
    {
        Transform badge = Child(row, "Badge");
        CenteredRect(badge, ColBadgeX, ColBadgeW, badgeH_);
        Image badgeImage = EnsureImage(badge, Color.white);
        badgeImage.sprite = LoadSprite(top3
            ? (index == 0 ? BuildRankingPanelArt.Crown1Path
                          : (index == 1 ? BuildRankingPanelArt.Crown2Path : BuildRankingPanelArt.Crown3Path))
            : BuildRankingPanelArt.TagPath);
        badgeImage.type = Image.Type.Simple;
        badgeImage.preserveAspect = true;
        badgeImage.raycastTarget = false;

        Transform placeText = Child(badge, "Place");
        Stretch(placeText, 0f);
        RectTransform placeRect = (RectTransform)placeText;
        placeRect.offsetMin = new Vector2(0f, -6f);
        placeRect.offsetMax = new Vector2(0f, -6f);
        TextMeshProUGUI tmp = placeText.GetComponent<TextMeshProUGUI>();
        if (tmp == null) { tmp = Undo.AddComponent<TextMeshProUGUI>(placeText.gameObject); }
        tmp.font = font_;
        tmp.text = (index + 1).ToString();
        tmp.fontSize = badgeFont_;
        tmp.color = Color.white;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        tmp.raycastTarget = false;
    }



    private static void Cell(Transform row, string name, string value, float size, float x, float w,
        TextAlignmentOptions alignment, Color color)
    {
        Transform t = Child(row, name);
        RectTransform rt = (RectTransform)t;
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 0.5f);
        // anchorMin/Maxで上下だけ伸ばし、幅はsizeDelta.x・位置はanchoredPosition.xで決める。
        rt.sizeDelta = new Vector2(w, 0f);
        rt.anchoredPosition = new Vector2(x, 0f);

        TextMeshProUGUI tmp = t.GetComponent<TextMeshProUGUI>();
        if (tmp == null) { tmp = Undo.AddComponent<TextMeshProUGUI>(t.gameObject); }
        tmp.font = font_;
        tmp.text = value;
        tmp.fontSize = size;
        tmp.color = color;
        tmp.alignment = alignment;
        tmp.enableWordWrapping = false;
        tmp.overflowMode = TextOverflowModes.Ellipsis;
        tmp.raycastTarget = false;
    }

    private static void HeaderLabel(Transform header, string name, string label, float x, float w,
        TextAlignmentOptions alignment)
    {
        Cell(header, name, label, headerFont_, x, w, alignment, HeaderTextColor);
    }
    // 行の中で上下中央揃えの固定サイズな要素（バッジ・ネジ）。
    private static void CenteredRect(Transform t, float x, float w, float h)
    {
        RectTransform rt = (RectTransform)t;
        rt.anchorMin = new Vector2(0f, 0.5f);
        rt.anchorMax = new Vector2(0f, 0.5f);
        rt.pivot = new Vector2(0f, 0.5f);
        rt.sizeDelta = new Vector2(w, h);
        rt.anchoredPosition = new Vector2(x, 0f);
        rt.localRotation = Quaternion.identity;
        rt.localScale = Vector3.one;
        rt.localPosition = new Vector3(rt.localPosition.x, rt.localPosition.y, 0f);
    }

    // 四辺を同じ量だけ内側へ寄せたストレッチ。
    private static void Inset(Transform t, float inset)
    {
        RectTransform rt = (RectTransform)t;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(inset, inset);
        rt.offsetMax = new Vector2(-inset, -inset);
        rt.localRotation = Quaternion.identity;
        rt.localScale = Vector3.one;
        rt.localPosition = new Vector3(rt.localPosition.x, rt.localPosition.y, 0f);
    }

    // 段位の紋章。ネームプレート・終局画面と同じ実素材で、Sprite ではなく Texture2D。
    private static Texture2D LoadRankEmblem(string path)
    {
        Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (texture == null)
        {
            Debug.LogError("[BuildInstanceRankingPanel] 段位アイコンがありません: " + path);
        }
        return texture;
    }

    private static RawImage EnsureRawImage(Transform t)
    {
        // 旧版は同じ枠を Image で作っていた。Graphic は1つしか載らないので、
        // 先に Image を外さないと Undo.AddComponent<RawImage> が null を返す。
        Image stale = t.GetComponent<Image>();
        if (stale != null) { Undo.DestroyObjectImmediate(stale); }

        RawImage image = t.GetComponent<RawImage>();
        if (image == null) { image = Undo.AddComponent<RawImage>(t.gameObject); }
        image.color = Color.white;
        return image;
    }

    private static Sprite LoadSprite(string path)
    {
        Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
        if (sprite == null)
        {
            Debug.LogError("[BuildInstanceRankingPanel] スプライトがありません: " + path +
                           "\nTools > Kemono Mahjong > Build Ranking Panel Art を先に実行してください。");
        }
        return sprite;
    }


    private static Transform FullPanel(Transform root, string name, Color color)
    {
        Transform t = Child(root, name);
        RectTransform rt = (RectTransform)t;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        // 平面から浮く・回るとレーザーが当たらない（見えているのに押せない）。
        rt.localRotation = Quaternion.identity;
        rt.localScale = Vector3.one;
        rt.localPosition = new Vector3(rt.localPosition.x, rt.localPosition.y, 0f);
        EnsureImage(t, color);
        t.gameObject.SetActive(true);
        return t;
    }

    private static TextMeshProUGUI Text(Transform parent, string name, string value, float size,
        Vector2 topLeft, Vector2 rectSize, Color color, TextAlignmentOptions alignment)
    {
        Transform t = Child(parent, name);
        SetTopRect(t, topLeft, rectSize);
        TextMeshProUGUI tmp = t.GetComponent<TextMeshProUGUI>();
        if (tmp == null) { tmp = Undo.AddComponent<TextMeshProUGUI>(t.gameObject); }
        tmp.font = font_;
        tmp.text = value;
        tmp.fontSize = size;
        tmp.color = color;
        tmp.alignment = alignment;
        tmp.enableWordWrapping = true;
        tmp.raycastTarget = false;
        return tmp;
    }

    private static Button Button(Transform parent, string name, string label, Vector2 topLeft, Vector2 size,
        float fontSize, Color color)
    {
        Transform t = Child(parent, name);
        SetTopRect(t, topLeft, size);
        Image image = EnsureImage(t, color);
        Button button = t.GetComponent<Button>();
        if (button == null) { button = Undo.AddComponent<Button>(t.gameObject); }
        button.targetGraphic = image;

        Transform labelTransform = Child(t, "Label");
        Stretch(labelTransform, 6f);
        TextMeshProUGUI tmp = labelTransform.GetComponent<TextMeshProUGUI>();
        if (tmp == null) { tmp = Undo.AddComponent<TextMeshProUGUI>(labelTransform.gameObject); }
        tmp.font = font_;
        tmp.text = label;
        tmp.fontSize = fontSize;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = Color.white;
        tmp.enableWordWrapping = false;
        tmp.raycastTarget = false;
        return button;
    }

    private static Transform Child(Transform parent, string name)
    {
        Transform existing = parent.Find(name);
        if (existing != null) { return existing; }
        GameObject go = new GameObject(name, typeof(RectTransform));
        Undo.RegisterCreatedObjectUndo(go, "Build Instance Ranking Panel");
        go.transform.SetParent(parent, false);
        return go.transform;
    }

    private static void SetTopRect(Transform t, Vector2 topLeft, Vector2 size)
    {
        RectTransform rt = (RectTransform)t;
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = topLeft;
        rt.sizeDelta = size;
        rt.localRotation = Quaternion.identity;
        rt.localScale = Vector3.one;
        rt.localPosition = new Vector3(rt.localPosition.x, rt.localPosition.y, 0f);
    }

    private static void Stretch(Transform t, float inset)
    {
        RectTransform rt = (RectTransform)t;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(inset, inset);
        rt.offsetMax = new Vector2(-inset, -inset);
    }

    private static Image EnsureImage(Transform t, Color color)
    {
        Image image = t.GetComponent<Image>();
        if (image == null) { image = Undo.AddComponent<Image>(t.gameObject); }
        image.color = color;
        return image;
    }

    private static GameObject FindSceneObject(string name)
    {
        Transform[] transforms = UnityEngine.Object.FindObjectsOfType<Transform>(true);
        for (int i = 0; i < transforms.Length; ++i)
        {
            Transform candidate = transforms[i];
            if (candidate != null && candidate.name == name && candidate.gameObject.scene.IsValid())
            {
                return candidate.gameObject;
            }
        }
        return null;
    }

    private static TextMeshProUGUI FindTmp(Transform root, string path)
    {
        Transform t = root.Find(path);
        if (t == null)
        {
            Debug.LogError("[BuildInstanceRankingPanel] UIが見つかりません: " + path);
            return null;
        }
        return t.GetComponent<TextMeshProUGUI>();
    }

    private static bool EnsureProgramAsset()
    {
        MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(ScriptPath);
        if (script == null)
        {
            Debug.LogError("[BuildInstanceRankingPanel] スクリプトが見つかりません: " + ScriptPath);
            return false;
        }
        Type type = script.GetClass();
        if (type != null && UdonSharpProgramAsset.GetProgramAssetForClass(type) != null) { return false; }
        if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(ProgramAssetPath) != null) { return false; }

        UdonSharpProgramAsset asset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
        asset.sourceCsScript = script;
        AssetDatabase.CreateAsset(asset, ProgramAssetPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[BuildInstanceRankingPanel] U# Program Assetを生成: " + ProgramAssetPath);
        return true;
    }

    private static UdonSharpBehaviour EnsureUSharpComponent(GameObject go, Type type)
    {
        UdonSharpBehaviour proxy = (UdonSharpBehaviour)go.GetComponent(type);
        if (proxy != null && UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy) != null) { return proxy; }
        if (proxy != null)
        {
            UnityEngine.Object.DestroyImmediate(proxy);
            UdonBehaviour[] oldUdon = go.GetComponents<UdonBehaviour>();
            for (int i = 0; i < oldUdon.Length; ++i)
            {
                if (oldUdon[i].programSource == null) { UnityEngine.Object.DestroyImmediate(oldUdon[i]); }
            }
        }

        UdonSharpBehaviour added = (UdonSharpBehaviour)UdonSharpUndo.AddComponent(go, type);
        if (added == null || UdonSharpEditorUtility.GetBackingUdonBehaviour(added) == null)
        {
            Debug.LogError("[BuildInstanceRankingPanel] InstanceRankingBoardを追加できません。U#コンパイル後に再実行してください。");
            if (added != null) { UnityEngine.Object.DestroyImmediate(added); }
            return null;
        }
        return added;
    }

    private static int WireButton(Transform root, string path, UdonBehaviour udon, string eventName)
    {
        Transform t = root.Find(path);
        if (t == null)
        {
            Debug.LogError("[BuildInstanceRankingPanel] UIが見つかりません: " + path);
            return 0;
        }
        Button button = t.GetComponent<Button>();
        if (button == null)
        {
            Debug.LogError("[BuildInstanceRankingPanel] Buttonがありません: " + path);
            return 0;
        }
        while (button.onClick.GetPersistentEventCount() > 0)
        {
            UnityEventTools.RemovePersistentListener(button.onClick, 0);
        }
        UnityEventTools.AddStringPersistentListener(button.onClick, udon.SendCustomEvent, eventName);
        EditorUtility.SetDirty(button);
        return 1;
    }

    private static void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform) { SetLayerRecursively(child.gameObject, layer); }
    }
}
#endif
