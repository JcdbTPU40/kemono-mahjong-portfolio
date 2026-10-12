#if UNITY_EDITOR
// 役満カットインの骨格生成と配線。
//   Tools > Kemono Mahjong > Build Yakuman Cutin (Prefab)    … 卓Prefabにリグを冪等生成
//   Tools > Kemono Mahjong > Wire Yakuman Cutin (Prefab)     … U#追加＋参照割当＋素材の自動割当
//   Tools > Kemono Mahjong > Validate Yakuman Cutin          … 参照欠損と素材の埋まり具合を報告
//   Tools > Kemono Mahjong > Build Yakuman Cutin             … シーン上の全卓に生成（Prefab版の予備）
//   Tools > Kemono Mahjong > Wire Yakuman Cutin              … 同上の配線
// どれも2回実行して安全（冪等）。
//
// リグは卓Prefab(KemonoMahjong.prefab)に1本だけ置く。ダブロンでも役満カットインは
// 和了者ごとに順番に出る（あがり画面の切り替えに追従する）ので、席ごとに持つ必要はない。
// ※Build/Wire は必ず (Prefab) 版を使うこと。FindObjectsOfType は Prefab Mode では
//   卓を拾わないため、シーン版を Prefab ステージで走らせても何も起きない。
//
// ---- 素材の置き方（役ごとの背景イラストと筆文字）----
// Assets/Mahjong/Textures/Yakuman/ に、下の YakuKeys のキーで PNG を置いて Wire を再実行する。
//   背景 … bg_<key>.png     （推奨 1024x576 / 16:9・不透明・ASTC）
//   筆文字 … name_<key>.png （推奨 1024x256・背景透過・左端から書き出される）
// 例: bg_tsuuiisou.png / name_tsuuiisou.png
// 置かれていない役は既定背景＋Textフォールバック（筆書体）で成立する＝素材待ちで止まらない。
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using SuzuFactory.AozoraMahjong;
using VRC.Udon;

public static class BuildYakumanCutin
{
    const string RootName = "YakumanCutin";

    const string TablePrefabPath = "Assets/Mahjong/Prefabas/KemonoMahjong.prefab";
    const string ProgramAssetDir = "Assets/Mahjong/scene_UdonProgramSources";
    const string ScriptPath = "Assets/Mahjong/Sources/MahjongYakumanCutin.cs";
    const string BrushFontPath = "Assets/Mahjong/Fonts/YujiMai-Regular.ttf";

    const string FxTextureDir = "Assets/Mahjong/Textures/FX";
    const string YakumanTextureDir = "Assets/Mahjong/Textures/Yakuman";

    const string TexSolid = FxTextureDir + "/yk_solid.png";
    const string TexSpark = FxTextureDir + "/yk_spark.png";
    const string TexFlash = FxTextureDir + "/yk_flash.png";
    const string TexGlow = FxTextureDir + "/yk_glow.png";
    const string TexBgDefault = FxTextureDir + "/yk_bg_default.png";
    const string TexPetal = FxTextureDir + "/yk_petal.png";
    const string TexFlashFull = FxTextureDir + "/yk_flash_full.png";
    const string TileAtlasPath = "Assets/Mahjong/Textures/tiles.png";

    // ---- 役満カットイン（YakumanIntro）のSE ----
    // ここに音源を置いて Wire を回すと自動で割り当たる（YakumanFlySFX と同じ流儀）。
    // ファイル名の頭で役割が決まる（大文字小文字は無視・拡張子は wav / mp3 / ogg いずれも可）:
    //   intro*  … 導入（暗紺空間）の頭。0.00秒〜。閃光まで約3.05秒ある
    //   flash*  … 閃光＝役名が確定する瞬間。約3.05秒
    //   write*  … 役名の筆文字が書かれ始める瞬間。約4.26秒
    // 置いていないスロットは下の既定（暫定素材）のまま。
    const string SfxDir = "Assets/Mahjong/Sounds/YakumanIntroSFX";
    const string IntroPrefix = "intro";
    const string FlashPrefix = "flash";
    const string WritePrefix = "write";

    // 音量は段ごとに分ける（1本のつまみに素の音圧が違うクリップをぶら下げない）。
    // 他のSEと同じく実測ラウドネス（ffmpeg ebur128）から逆算し、役割別の目標へ揃えた値。
    //   導入 intro.wav      -13.1 LUFS → 0.051  目標 -39（3秒鳴り続ける持続音＝段B）
    //   閃光 kakutei_sting  -14.5 LUFS → 0.094  目標 -35（単発の決定的な瞬間＝段A）
    //   役名 Agari Hai Don  -21.3 LUFS → 0.207  目標 -35（同上）
    // 以前はカットインだけ他より10dB以上大きかった（旧 seVolume 0.45 一律）。
    const float IntroVolume = 0.119f;
    const float FlashVolume = 0.108f;
    const float WriteVolume = 0.0885f;

    // 既定（暫定素材）。差し替えフォルダに音源が無いときだけ使われる。
    const string DefaultIntroSound = "Assets/Mahjong/Sounds/Placeholder/yakuman_chance_sting.wav";
    const string DefaultFlashSound = "Assets/Mahjong/Sounds/Placeholder/yakuman_kakutei_sting.wav";
    const string DefaultWriteSound = "Assets/Mahjong/Sounds/AgariSFX/Agari Hai Don.mp3";

    // ---- ステージ（専用カメラで撮ってパネルに映す）----
    // 導入は「世界の上に半透明の板を出す」のではなく、卓から離れた専用空間を
    // 専用カメラで撮って映す。こうしないと世界が透けて空間に見えない。
    const string StageLayerName = "YakumanStage";
    const string StageRtPath = "Assets/Mahjong/Textures/YakumanStage.renderTexture";

    // ---- パーティクルのバースト（Hovl Studio）----
    // 素のマテリアルは Particles/Standard Unlit で、Quest の許可シェーダーではない。
    // 複製して VRChat/Mobile/Particles/Additive へ差し替えたものを使う。
    // Star hit は横方向の星の飛び散りで、☆の輪郭スプライトが漫画的に出る。
    // 打ち上げには合わないので、放射フレア＋衝撃波リングの Holy hit に統一する。
    const string FxLaunchSrc = "Assets/Hovl Studio/Magic effects pack/Prefabs/Hits and explosions/Holy hit.prefab";
    const string FxFlashSrc = "Assets/Hovl Studio/Magic effects pack/Prefabs/Hits and explosions/Holy hit.prefab";
    const string FxMaterialDir = "Assets/Mahjong/Materials/YakumanFX";
    const string QuestParticleShader = "VRChat/Mobile/Particles/Additive";

    // Hovl の素の大きさは 1m 前後。ステージは 16m x 9m なので、そのままだと小さい。
    const float FxLaunchScale = 1.4f;
    const float FxFlashScale = 1.6f;

    // 散った粒が次の段（彗星）まで残るとゴミに見える。
    const float FxSparkLifetime = 0.5f;

    // バースト全体の寿命の上限。打ち上げは火の粉が尾として効くので長め、
    // フラッシュは 0.16秒の白が引いた後まで残らないよう短く。
    const float FxLaunchLifetime = 0.6f;
    const float FxFlashLifetime = 0.30f;
    // 卓の 300m 下。プレイヤーからは見えないが、カメラのカリングマスクでも二重に守る。
    static readonly Vector3 StageOffset = new Vector3(0f, -300f, 0f);
    // Canvas 1600x900 * 0.01 = 16m x 9m。カメラはその正面 12.4m。
    const float StageCanvasScale = 0.01f;
    const float StageCamDistance = 12.4f;
    const float StageCamFov = 40f;
    const string UiMaterialPath = "Packages/com.vrchat.worlds/Editor/VRCSDK/SDK3/VRCSuperSampledUIMaterial.mat";



    // Canvas は 1600x900（16:9）の仮想解像度。実寸 = px * CanvasScale（m）＝0.981m x 0.552m。
    //
    // **卓上パネルは全部 あがり画面の実寸（0.981m x 0.552m）・中心Y 0.045 に揃えてある。**
    // あがり画面が 16:9（1920x1080 @ 0.00073 → 実行時 x0.7）なので、こちらも 16:9 にする。
    //   CanvasScale = 0.98112 / 1600 = 0.0006132
    //
    // ※CanvasScale を変えると文字・牌・彗星の半径まで全部比例追従するので、
    //   内部レイアウト定数を触る必要は無い（Canvas の仮想解像度そのものを
    //   変えたときだけ、横方向の定数がはみ出さないか測り直すこと）。
    //
    // ---- 卓上パネルの「エディタ表示 と 実行時」のズレについて ----
    // MahjongAgariScreen / MahjongPresentationScreen は表示するとき panelRoot へ
    //   localPosition = Vector3.down * 0.20 / localScale = Vector3.one * 0.7
    // を代入したまま元へ戻さない（入場アニメが動かすのは contentRect の方）。
    // よって**あがり画面と勝者カットインは、実行時にエディタ表示より 0.20m 低く 0.7倍**になる。
    // ・ResultScreen は同じ代入をするが、Prefab 側も最初から -0.20 / 0.7 にしてあるので
    //   代入が冪等になり、エディタ表示＝実行時の見た目になっている（BuildResultScreen.cs 参照）。
    // ・役満カットインにはこの仕掛けが無い＝もともとエディタ表示＝実行時。
    // したがって**シーンビューであがり画面と大きさを見比べても意味が無い**。
    // 比較するならエディタ上のあがり画面を 0.7 倍して考えること。
    //
    // アスペクトは **16:9**。背景素材もこの比で作る（1600x900 推奨）。
    const float CanvasScale = 0.0006132f;
    static readonly Vector2 CanvasSize = new Vector2(1600f, 900f);

    // 卓ルートからの高さ（m）。卓ルート world Y = -0.305 なので中心は world Y 0.045、
    // パネルは world Y -0.231 .. 0.321 を占める（あがり画面とまったく同じ）。
    //
    // **この帯より上へ動かすな。** 一度 1.69（world 0.82..1.95）まで上げたが、
    // 着席したプレイヤーからは頭上すぎて誰にも見えなかった。
    //
    // 実測の根拠:
    //   ・あがり画面・ResultScreen の実行時の中心が world Y 0.045。ここが
    //     プレイヤーが必ず見ている帯なので、役満パネルもここへ合わせる。
    //   ・卓中心の真上に ResetButton（world Y 0.645..0.745）が浮いている。
    //     パネル上端 0.321 なので余裕 0.324m。ここを超えると役名にボタンが刺さる。
    //   ・卓の天面（Cube）は world Y 0.23 まで。下辺が卓に潜るのは
    //     あがり画面も同じ（ビルボードなので手前に描かれる）。
    const float PanelHeight = 0.350f;

    const int NumSparks = 8;

    const int NameFontSize = 200;
    const int RankFontSize = 110;

    static readonly Vector2 NameSize = new Vector2(1180f, 300f);
    static readonly Vector2 NameHome = new Vector2(-560f, -150f); // pivot が左端なのでここが書き出し開始点
    static readonly Vector2 RankHome = new Vector2(377f, -320f);
    static readonly Vector2 FlashSize = new Vector2(1600f, 150f);
    static readonly Vector2 SparkSize = new Vector2(26f, 120f);
    static readonly Vector2 GlowSize = new Vector2(150f, 150f);
    const int MaxHandTiles = 18;
    static readonly Vector2 MascotSize = new Vector2(470f, 660f);
    static readonly Vector2 MascotHome = new Vector2(420f, 45f);

    // 立ち絵の左→右フェード用パネル。立ち絵を縦帯に切り、左から順に出す。
    // 焼き込み済みの導入クリップ（YakumanIntro.anim）は従来の Mascot を動かしているので、
    // クリップを焼き直さずに済むよう、独立した別パネルとして重ねる。
    //
    // 置き場所は「パネル側の Canvas」＝ StageView（ステージカメラの映像）の1つ上。
    // ステージ側の Canvas に置くと、カメラが常に振り付けで動いているので立ち絵まで
    // 一緒に流れて画面から外れる。カメラの影響を受けない板の上に独立して出す。
    // 大きさは素材（700x1080）の比のまま、パネルの高さいっぱいに近づけた値。
    static readonly Vector2 MascotWipeSize = new Vector2(570f, 880f);
    static readonly Vector2 MascotWipeHome = new Vector2(300f, -10f);
    // 帯の本数と重なり具合が滑らかさを決める。16本・fade0.07 では段差が見えたので、
    // 本数を増やし、1本の出方を出し始めの差より長くして常に十数本が混ざるようにした。
    const int MascotWipeStripCount = 20;
    const float MascotWipeSpread = 0.13f;      // 左端→右端の出し始めの差（立ち絵の持ち時間に対する割合）
    const float MascotWipeStripFade = 0.09f;   // 帯1本が出きるまで（同上）
    const float MascotWipeSlide = 34f;         // 出るときに右へずらす量（px）
    const float MascotWipeZoom = 1.04f;        // 出るときの拡大率
    static readonly Vector2 WinningTileSize = new Vector2(92f, 134f);
    static readonly Vector2 WinningTileHome = new Vector2(0f, -110f);
    static readonly Vector2 HandTileSize = new Vector2(70f, 102f);
    const int NumPetals = 8;
    const int NumCometTiles = 6;
    static readonly Vector2 PetalSize = new Vector2(46f, 30f);
    static readonly Vector2 CometTileSize = new Vector2(84f, 122f);
    // 炎の尾。牌の真下に伸ばすので縦長。yk_spark は上が芯・下が尾のグラデ。
    static readonly Vector2 CometTrailSize = new Vector2(56f, 190f);
    // 主役牌の尾。牌が大きいぶん彗星より太く長い。
    static readonly Vector2 WinTileTrailSize = new Vector2(110f, 320f);
    // 牌の光暈。牌よりひと回り大きい正方形（yk_glow は放射状）。
    static readonly Vector2 TileGlowSize = new Vector2(150f, 150f);



    static readonly Color OutlineColor = new Color(0.03f, 0.02f, 0.05f, 0.9f);
    static readonly Vector2 OutlineDistance = new Vector2(3f, -3f);

    // ---- 役満の一覧。ID は Mahjong.cs の YAKU_* と同じ値にすること ----
    static readonly int[] YakuIds =
    {
        15, // 四槓子
        16, // 四暗刻
        17, // 四暗刻単騎（ダブル）
        27, // 大三元
        29, // 字一色
        32, // 国士無双
        33, // 国士無双十三面（ダブル）
        37, // 天和
        38, // 地和
        40, // 緑一色
        41, // 大四喜（ダブル）
        42, // 小四喜
        43, // 九蓮宝燈
        44, // 清老頭
        57, // 純正九蓮宝燈（ダブル）
    };

    // 素材ファイル名のキー。YakuIds と同じ並び。
    static readonly string[] YakuKeys =
    {
        "suukantsu",
        "suuankou",
        "suuankou_tanki",
        "daisangen",
        "tsuuiisou",
        "kokushi",
        "kokushi13",
        "tenhou",
        "chiihou",
        "ryuuiisou",
        "daisuushii",
        "shousuushii",
        "chuuren",
        "chinroutou",
        "junsei_chuuren",
    };

    // 素材が無いときの光・文字の色。YakuIds と同じ並び。
    // 四暗刻単騎＝水色／字一色＝金／緑一色＝緑 は雀魂の実物に合わせてある。
    static readonly Color[] YakuColors =
    {
        new Color(0.80f, 0.58f, 1.00f, 1f), // 四槓子
        new Color(0.64f, 0.72f, 1.00f, 1f), // 四暗刻
        new Color(0.55f, 0.92f, 1.00f, 1f), // 四暗刻単騎
        new Color(1.00f, 0.76f, 0.32f, 1f), // 大三元
        new Color(1.00f, 0.86f, 0.36f, 1f), // 字一色
        new Color(1.00f, 0.95f, 0.74f, 1f), // 国士無双
        new Color(1.00f, 1.00f, 0.94f, 1f), // 国士無双十三面
        new Color(1.00f, 0.88f, 0.46f, 1f), // 天和
        new Color(0.74f, 0.95f, 0.56f, 1f), // 地和
        new Color(0.46f, 1.00f, 0.64f, 1f), // 緑一色
        new Color(1.00f, 0.72f, 0.95f, 1f), // 大四喜
        new Color(0.82f, 0.80f, 1.00f, 1f), // 小四喜
        new Color(1.00f, 0.72f, 0.42f, 1f), // 九蓮宝燈
        new Color(1.00f, 0.54f, 0.44f, 1f), // 清老頭
        new Color(1.00f, 0.86f, 0.62f, 1f), // 純正九蓮宝燈
    };
    // 参考動画で確認した手牌の軌道。未確認の役も近い構図へ寄せ、素材差し替えだけで調整できる。
    static readonly int[] YakuHandPathModes =
    {
        3, // 四槓子
        3, // 四暗刻
        3, // 四暗刻単騎
        1, // 大三元
        2, // 字一色
        0, // 国士無双
        0, // 国士無双十三面
        0, // 天和
        0, // 地和
        0, // 緑一色
        2, // 大四喜
        2, // 小四喜
        0, // 九蓮宝燈
        2, // 清老頭
        0, // 純正九蓮宝燈
    };

    static readonly int[] YakuTitleLayoutModes =
    {
        0, // 四槓子
        1, // 四暗刻
        1, // 四暗刻単騎
        3, // 大三元
        0, // 字一色
        1, // 国士無双
        2, // 国士無双十三面（左右分割）
        3, // 天和
        3, // 地和
        0, // 緑一色
        0, // 大四喜
        0, // 小四喜
        1, // 九蓮宝燈
        0, // 清老頭
        1, // 純正九蓮宝燈
    };



    // ============================================================
    //  Build（骨格生成）
    // ============================================================

    [MenuItem("Tools/Kemono Mahjong/Build Yakuman Cutin (Prefab)")]
    public static void BuildPrefab()
    {
        EnsureFxTextures();

        var stage = PrefabStageUtility.OpenPrefab(TablePrefabPath);
        if (stage == null)
        {
            Debug.LogError("[BuildYakumanCutin] Prefab を開けません: " + TablePrefabPath);
            return;
        }

        var root = stage.prefabContentsRoot;
        if (root.GetComponent<Mahjong>() == null)
        {
            Debug.LogError("[BuildYakumanCutin] Prefab のルートに Mahjong がありません: " + TablePrefabPath);
            return;
        }

        BuildOne(root.transform);
        SavePrefabStage(stage);

        Debug.Log("[BuildYakumanCutin] Prefab に役満カットインのリグを生成/更新しました: " + TablePrefabPath
            + " … 次に Wire Yakuman Cutin (Prefab) を実行してください。");
    }

    [MenuItem("Tools/Kemono Mahjong/Build Yakuman Cutin")]
    public static void Build()
    {
        EnsureFxTextures();

        var tables = Object.FindObjectsOfType<Mahjong>(true);
        if (tables == null || tables.Length == 0)
        {
            Debug.LogWarning("[BuildYakumanCutin] シーンに Mahjong が見つかりません（Prefab Mode なら (Prefab) 版を使ってください）。");
            return;
        }

        foreach (var t in tables) { BuildOne(t.transform); }

        Debug.Log("[BuildYakumanCutin] " + tables.Length + " 卓にリグを生成/更新しました。"
            + " … 次に Wire Yakuman Cutin を実行してください。");
    }

static void BuildOne(Transform tableRoot)
    {
        var root = EnsureChild(tableRoot, RootName);
        root.transform.localPosition = new Vector3(0f, PanelHeight, 0f);
        root.transform.localRotation = Quaternion.identity;
        root.transform.localScale = Vector3.one;
        root.SetActive(true);

        var audio = root.GetComponent<AudioSource>();
        if (audio == null) { audio = Undo.AddComponent<AudioSource>(root); }
        audio.playOnAwake = false;
        audio.loop = false;
        audio.spatialBlend = 1f;
        audio.dopplerLevel = 0f;
        audio.minDistance = 2f;
        audio.maxDistance = 16f;
        audio.rolloffMode = AudioRolloffMode.Linear;

        var fxRoot = EnsureChild(root.transform, "FxRoot");
        fxRoot.transform.localPosition = Vector3.zero;
        fxRoot.transform.localRotation = Quaternion.identity;
        fxRoot.transform.localScale = Vector3.one;

        var billboard = EnsureChild(fxRoot.transform, "Billboard");
        billboard.transform.localPosition = Vector3.zero;
        billboard.transform.localRotation = Quaternion.identity;
        billboard.transform.localScale = Vector3.one;

        var canvas = EnsureCanvas(billboard.transform).transform;

        // ---- パネル側（卓の上に出る板）----
        // 奥から、暗幕→ステージ映像→背景→実牌→全面フラッシュ→役名。
        EnsureFxImage(canvas, "Vignette", TexSolid, CanvasSize, Vector2.zero, new Vector2(0.5f, 0.5f));
        // 導入はここにステージのカメラ映像（RenderTexture）を映す。
        var stageView = EnsureFxImage(canvas, "StageView", null, CanvasSize, Vector2.zero, new Vector2(0.5f, 0.5f));
        stageView.texture = AssetDatabase.LoadAssetAtPath<RenderTexture>(StageRtPath);
        // 立ち絵の帯パネル。StageView の上・Bg の下に独立して重ねる。
        BuildMascotWipe(canvas);
        EnsureFxImage(canvas, "Bg", TexBgDefault, CanvasSize, Vector2.zero, new Vector2(0.5f, 0.5f));
        BuildHandTiles(canvas);
        // 全面フラッシュ。四隅まで届く放射ブルームで、横帯ではなく画面全体が飛ぶ。
        EnsureFxImage(canvas, "Flash", TexFlashFull, CanvasSize, Vector2.zero, new Vector2(0.5f, 0.5f));

        EnsureNameText(canvas, "NameShadow", false);
        EnsureNameText(canvas, "NameText", true);
        EnsureNameText(canvas, "NameSecondaryShadow", false);
        EnsureNameText(canvas, "NameSecondaryText", true);

        EnsureFxImage(canvas, "NameImage", null, NameSize, NameHome, new Vector2(0f, 0.5f));
        EnsureFxImage(canvas, "BrushGlow", TexGlow, GlowSize, NameHome, new Vector2(0.5f, 0.5f));

        EnsureRankText(canvas);

        string[] order =
        {
            "Vignette", "StageView", "MascotWipe", "Bg", "HandTiles", "Flash",
            "NameShadow", "NameText", "NameSecondaryShadow", "NameSecondaryText",
            "NameImage", "BrushGlow", "RankText"
        };
        CleanupChildren(canvas, order);
        SetSiblingOrder(canvas, order);

        // ---- ステージ側（専用カメラで撮る空間）----
        var stage = EnsureChild(fxRoot.transform, "Stage");
        stage.transform.localPosition = StageOffset;
        stage.transform.localRotation = Quaternion.identity;
        stage.transform.localScale = Vector3.one;

        var stageCanvas = EnsureStageCanvas(stage.transform).transform;

        EnsureFxImage(stageCanvas, "Mascot", null, MascotSize, MascotHome, new Vector2(0.5f, 0.5f));
        BuildSparks(stageCanvas);
        BuildPetals(stageCanvas);
        BuildCometGlows(stageCanvas);
        BuildCometTrails(stageCanvas);
        EnsureFxImage(stageCanvas, "WinTileTrail", TexSpark, WinTileTrailSize,
            WinningTileHome, new Vector2(0.5f, 0.5f));
        EnsureFxImage(stageCanvas, "WinTileGlow", TexGlow, TileGlowSize,
            WinningTileHome, new Vector2(0.5f, 0.5f));

        var winning = EnsureFxImage(stageCanvas, "WinningTile", TileAtlasPath,
            WinningTileSize, WinningTileHome, new Vector2(0.5f, 0.5f));
        winning.material = AssetDatabase.LoadAssetAtPath<Material>(UiMaterialPath);

        BuildCometTiles(stageCanvas);

        string[] stageOrder =
        {
            "CometGlows", "CometTrails", "Mascot", "Petals", "Sparks",
            "WinTileTrail", "WinTileGlow", "WinningTile", "CometTiles"
        };
        CleanupChildren(stageCanvas, stageOrder);
        SetSiblingOrder(stageCanvas, stageOrder);

        EnsureStageCamera(stage.transform);

        // 打ち上げと全面フラッシュのバースト。位置は実行時に牌へ合わせる。
        EnsureBurst(stage.transform, "LaunchBurst", FxLaunchSrc, FxLaunchScale, FxLaunchLifetime);
        EnsureBurst(stage.transform, "FlashBurst", FxFlashSrc, FxFlashScale, FxFlashLifetime);

        CleanupChildren(stage.transform,
            new string[] { "StageCanvas", "StageCam", "LaunchBurst", "FlashBurst" });

        // ステージ一式を専用レイヤーへ。カメラはこのレイヤーだけを描く。
        int stageLayer = LayerMask.NameToLayer(StageLayerName);
        if (stageLayer < 0)
        {
            Debug.LogError("[BuildYakumanCutin] レイヤー '" + StageLayerName + "' がありません。先に作成してください。");
        }
        else
        {
            SetLayerRecursive(stage.transform, stageLayer);
        }

        CleanupChildren(fxRoot.transform, new string[] { "Billboard", "Stage" });
        // YakumanCutin の直下には BuildYakumanFly が打ち上げリグ（FlyRig / FlyComet）を置く。
        // ここの keep から漏らすと Build Yakuman Cutin を回すたびに打ち上げが丸ごと消える
        // （実際に消して配線が 0/8 になった）。他所が置くものは必ず名前を足すこと。
        CleanupChildren(root.transform, new string[] { "FxRoot", "FlyRig", "FlyComet" });

        fxRoot.SetActive(false);
        MarkDirty(root);
    }

    static void BuildSparks(Transform canvas)
    {
        var group = EnsureUiChild(canvas, "Sparks");
        var groupRect = group.GetComponent<RectTransform>();
        groupRect.anchorMin = new Vector2(0.5f, 0.5f);
        groupRect.anchorMax = new Vector2(0.5f, 0.5f);
        groupRect.pivot = new Vector2(0.5f, 0.5f);
        groupRect.anchoredPosition = Vector2.zero;
        groupRect.sizeDelta = CanvasSize;
        groupRect.localScale = Vector3.one;

        var keep = new string[NumSparks];
        for (int i = 0; i < NumSparks; ++i)
        {
            keep[i] = "Spark" + i;
            EnsureFxImage(group.transform, keep[i], TexSpark, SparkSize,
                Vector2.zero, new Vector2(0.5f, 0.5f));
        }
        CleanupChildren(group.transform, keep);
    }
    static void BuildHandTiles(Transform canvas)
    {
        var group = EnsureUiChild(canvas, "HandTiles");
        var groupRect = group.GetComponent<RectTransform>();
        groupRect.anchorMin = new Vector2(0.5f, 0.5f);
        groupRect.anchorMax = new Vector2(0.5f, 0.5f);
        groupRect.pivot = new Vector2(0.5f, 0.5f);
        groupRect.anchoredPosition = Vector2.zero;
        groupRect.sizeDelta = CanvasSize;
        groupRect.localScale = Vector3.one;

        var uiMaterial = AssetDatabase.LoadAssetAtPath<Material>(UiMaterialPath);
        var keep = new string[MaxHandTiles];
        for (int i = 0; i < MaxHandTiles; ++i)
        {
            keep[i] = "HandTile" + i;
            var img = EnsureFxImage(group.transform, keep[i], TileAtlasPath,
                HandTileSize, Vector2.zero, new Vector2(0.5f, 0.5f));
            img.material = uiMaterial;
        }
        CleanupChildren(group.transform, keep);
    }



    // 花びら。勝者キャラのまわりに散らして置き、実行時に舞い落ちる。
    /// <summary>
    /// 立ち絵の帯パネル。Mascot と同じ大きさ・同じ位置の入れ物に、
    /// uvRect で横に切り分けた短冊を左から並べる。塗りは実行時にスクリプトが行う。
    /// </summary>
    static void BuildMascotWipe(Transform panelCanvas)
    {
        var group = EnsureUiChild(panelCanvas, "MascotWipe");
        var rt = group.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = MascotWipeHome;
        rt.sizeDelta = MascotWipeSize;
        rt.localScale = Vector3.one;
        rt.localRotation = Quaternion.identity;

        var keep = new string[MascotWipeStripCount];
        float w = MascotWipeSize.x / MascotWipeStripCount;
        for (int i = 0; i < MascotWipeStripCount; ++i)
        {
            keep[i] = "Strip" + i;
            var pos = new Vector2(-MascotWipeSize.x * 0.5f + w * (i + 0.5f), 0f);
            var img = EnsureFxImage(group.transform, keep[i], null,
                new Vector2(w, MascotWipeSize.y), pos, new Vector2(0.5f, 0.5f));
            // 帯の見る範囲。テクスチャは実行時に和了者のキャラ絵が入る。
            img.uvRect = new Rect(i / (float)MascotWipeStripCount, 0f,
                1f / MascotWipeStripCount, 1f);
        }
        CleanupChildren(group.transform, keep);
        SetSiblingOrder(group.transform, keep);
    }

    static void BuildPetals(Transform canvas)
    {
        var group = EnsureUiChild(canvas, "Petals");
        var groupRect = group.GetComponent<RectTransform>();
        groupRect.anchorMin = new Vector2(0.5f, 0.5f);
        groupRect.anchorMax = new Vector2(0.5f, 0.5f);
        groupRect.pivot = new Vector2(0.5f, 0.5f);
        groupRect.anchoredPosition = Vector2.zero;
        groupRect.sizeDelta = CanvasSize;
        groupRect.localScale = Vector3.one;

        var keep = new string[NumPetals];
        for (int i = 0; i < NumPetals; ++i)
        {
            keep[i] = "Petal" + i;

            // 立ち絵（MascotHome）の周りへ決定的に散らす。
            float fx = (i % 4) / 3f;              // 0..1
            float fy = (i / 4) / 1f;              // 0..1
            var pos = new Vector2(
                MascotHome.x - 320f + fx * 594f,
                MascotHome.y + 120f + fy * 210f);

            EnsureFxImage(group.transform, keep[i], TexPetal, PetalSize, pos, new Vector2(0.5f, 0.5f));
        }
        CleanupChildren(group.transform, keep);
    }

    // 牌の光暈。牌より先（奥）に描く。牌そのものは不透明で光らせられないので、
    // 背後に置いた光の玉で「自発光している」ように見せる。
    static void BuildCometGlows(Transform canvas)
    {
        var group = EnsureUiChild(canvas, "CometGlows");
        var groupRect = group.GetComponent<RectTransform>();
        groupRect.anchorMin = new Vector2(0.5f, 0.5f);
        groupRect.anchorMax = new Vector2(0.5f, 0.5f);
        groupRect.pivot = new Vector2(0.5f, 0.5f);
        groupRect.anchoredPosition = Vector2.zero;
        groupRect.sizeDelta = CanvasSize;
        groupRect.localScale = Vector3.one;

        var keep = new string[NumCometTiles];
        for (int i = 0; i < NumCometTiles; ++i)
        {
            keep[i] = "CometGlow" + i;
            EnsureFxImage(group.transform, keep[i], TexGlow,
                TileGlowSize, WinningTileHome, new Vector2(0.5f, 0.5f));
        }
        CleanupChildren(group.transform, keep);
    }

    // 牌の下に付く炎の尾。牌より先（奥）に描く。
    static void BuildCometTrails(Transform canvas)
    {
        var group = EnsureUiChild(canvas, "CometTrails");
        var groupRect = group.GetComponent<RectTransform>();
        groupRect.anchorMin = new Vector2(0.5f, 0.5f);
        groupRect.anchorMax = new Vector2(0.5f, 0.5f);
        groupRect.pivot = new Vector2(0.5f, 0.5f);
        groupRect.anchoredPosition = Vector2.zero;
        groupRect.sizeDelta = CanvasSize;
        groupRect.localScale = Vector3.one;

        var keep = new string[NumCometTiles];
        for (int i = 0; i < NumCometTiles; ++i)
        {
            keep[i] = "CometTrail" + i;
            EnsureFxImage(group.transform, keep[i], TexSpark,
                CometTrailSize, WinningTileHome, new Vector2(0.5f, 0.5f));
        }
        CleanupChildren(group.transform, keep);
    }

    // 牌の彗星。位置と回転は実行時に決まるので、ここでは和了牌の位置に畳んで置くだけ。
    static void BuildCometTiles(Transform canvas)
    {
        var group = EnsureUiChild(canvas, "CometTiles");
        var groupRect = group.GetComponent<RectTransform>();
        groupRect.anchorMin = new Vector2(0.5f, 0.5f);
        groupRect.anchorMax = new Vector2(0.5f, 0.5f);
        groupRect.pivot = new Vector2(0.5f, 0.5f);
        groupRect.anchoredPosition = Vector2.zero;
        groupRect.sizeDelta = CanvasSize;
        groupRect.localScale = Vector3.one;

        var uiMaterial = AssetDatabase.LoadAssetAtPath<Material>(UiMaterialPath);
        var keep = new string[NumCometTiles];
        for (int i = 0; i < NumCometTiles; ++i)
        {
            keep[i] = "CometTile" + i;
            var img = EnsureFxImage(group.transform, keep[i], TileAtlasPath,
                CometTileSize, WinningTileHome, new Vector2(0.5f, 0.5f));
            img.material = uiMaterial;
        }
        CleanupChildren(group.transform, keep);
    }

    // ============================================================
    //  Wire（U#追加＋参照割当）
    // ============================================================

    [MenuItem("Tools/Kemono Mahjong/Wire Yakuman Cutin (Prefab)")]
    public static void WirePrefab()
    {
        if (EnsureProgramAsset(ScriptPath,
            ProgramAssetDir + "/MahjongYakumanCutin Udon C# Program Asset.asset"))
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[WireYakumanCutin] U#プログラムアセットを生成しました。コンパイル完了後、もう一度実行してください。");
            return;
        }

        var stage = PrefabStageUtility.OpenPrefab(TablePrefabPath);
        if (stage == null)
        {
            Debug.LogError("[WireYakumanCutin] Prefab を開けません: " + TablePrefabPath);
            return;
        }

        var root = stage.prefabContentsRoot;
        var comp = WireOne(root.transform);
        if (comp == null) { SavePrefabStage(stage); return; }

        var table = root.GetComponent<Mahjong>();
        if (table != null)
        {
            table.yakumanCutin = comp;
            UdonSharpEditorUtility.CopyProxyToUdon(table);
            EditorUtility.SetDirty(table);
        }

        SavePrefabStage(stage);
        VerifyPrefab();
    }

    [MenuItem("Tools/Kemono Mahjong/Wire Yakuman Cutin")]
    public static void Wire()
    {
        if (EnsureProgramAsset(ScriptPath,
            ProgramAssetDir + "/MahjongYakumanCutin Udon C# Program Asset.asset"))
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[WireYakumanCutin] U#プログラムアセットを生成しました。コンパイル完了後、もう一度実行してください。");
            return;
        }

        EnsureFolder(SfxDir);   // 音源の置き場所を先に作っておく

        var tables = Object.FindObjectsOfType<Mahjong>(true);
        if (tables == null || tables.Length == 0)
        {
            Debug.LogWarning("[WireYakumanCutin] シーンに Mahjong が見つかりません。");
            return;
        }

        int wired = 0;
        foreach (var t in tables)
        {
            var comp = WireOne(t.transform);
            if (comp == null) { continue; }

            t.yakumanCutin = comp;
            UdonSharpEditorUtility.CopyProxyToUdon(t);
            EditorUtility.SetDirty(t);
            ++wired;
        }

        Debug.Log("[WireYakumanCutin] " + wired + " / " + tables.Length + " 卓を配線しました。");
    }

static MahjongYakumanCutin WireOne(Transform tableRoot)
    {
        var rootT = tableRoot.Find(RootName);
        if (rootT == null)
        {
            Debug.LogError("[WireYakumanCutin] " + RootName + " がありません。先に Build を実行してください: "
                + GetPath(tableRoot));
            return null;
        }

        var comp = (MahjongYakumanCutin)EnsureUSharpComponent(rootT.gameObject, typeof(MahjongYakumanCutin));
        if (comp == null) { return null; }

        var fxRoot = rootT.Find("FxRoot");
        if (fxRoot == null) { Debug.LogError("[WireYakumanCutin] FxRoot がありません。"); return null; }

        var billboard = fxRoot.Find("Billboard");
        var canvas = billboard != null ? billboard.Find("Canvas") : null;
        if (canvas == null) { Debug.LogError("[WireYakumanCutin] Canvas がありません。"); return null; }

        comp.fxRoot = fxRoot.gameObject;

        var stage = fxRoot.Find("Stage");
        var stageCanvas = stage != null ? stage.Find("StageCanvas") : null;
        var stageCam = stage != null ? stage.Find("StageCam") : null;
        if (stageCanvas == null || stageCam == null)
        {
            Debug.LogError("[WireYakumanCutin] Stage が見つかりません。先に Build を実行してください。");
            return null;
        }
        comp.stageCamera = stageCam.GetComponent<Camera>();

        var lb = stage.Find("LaunchBurst");
        var fb = stage.Find("FlashBurst");
        comp.launchBurst = lb != null ? lb.GetComponent<ParticleSystem>() : null;
        comp.flashBurst = fb != null ? fb.GetComponent<ParticleSystem>() : null;
        comp.stageViewImage = FindRaw(canvas, "StageView");
        comp.stageViewRect = FindRect(canvas, "StageView");
        comp.billboardRoot = billboard;

        comp.vignetteImage = FindRaw(canvas, "Vignette");
        comp.bgImage = FindRaw(canvas, "Bg");
        comp.bgRect = FindRect(canvas, "Bg");
        comp.flashImage = FindRaw(canvas, "Flash");
        comp.flashRect = FindRect(canvas, "Flash");

        comp.mascotImage = FindRaw(stageCanvas, "Mascot");
        comp.mascotRect = FindRect(stageCanvas, "Mascot");

        var wipeRoot = FindRect(canvas, "MascotWipe");
        comp.mascotWipeRoot = wipeRoot;
        var wipeStrips = new RawImage[MascotWipeStripCount];
        for (int i = 0; i < MascotWipeStripCount; ++i)
        {
            wipeStrips[i] = wipeRoot != null ? FindRaw(wipeRoot, "Strip" + i) : null;
        }
        comp.mascotWipeStrips = wipeStrips;
        // 見た目の確定値は Wire で毎回入れる（U# は既定値を変えても既存の値が残るため）。
        comp.mascotWipeEnabled = true;
        comp.mascotWipeSpread = MascotWipeSpread;
        comp.mascotWipeStripFade = MascotWipeStripFade;
        comp.mascotWipeSlide = MascotWipeSlide;
        comp.mascotWipeZoom = MascotWipeZoom;
        comp.winningTileImage = FindRaw(stageCanvas, "WinningTile");
        comp.winningTileRect = FindRect(stageCanvas, "WinningTile");

        comp.nameImage = FindRaw(canvas, "NameImage");
        comp.nameRect = FindRect(canvas, "NameImage");
        comp.nameText = FindText(canvas, "NameText");
        comp.nameTextRect = FindRect(canvas, "NameText");
        comp.nameShadowText = FindText(canvas, "NameShadow");
        comp.nameShadowRect = FindRect(canvas, "NameShadow");
        comp.nameSecondaryText = FindText(canvas, "NameSecondaryText");
        comp.nameSecondaryTextRect = FindRect(canvas, "NameSecondaryText");
        comp.nameSecondaryShadowText = FindText(canvas, "NameSecondaryShadow");
        comp.nameSecondaryShadowRect = FindRect(canvas, "NameSecondaryShadow");

        comp.brushGlowImage = FindRaw(canvas, "BrushGlow");
        comp.brushGlowRect = FindRect(canvas, "BrushGlow");

        comp.rankText = FindText(canvas, "RankText");
        comp.rankRect = FindRect(canvas, "RankText");

        var sparks = stageCanvas.Find("Sparks");
        var sparkImages = new RawImage[NumSparks];
        var sparkRects = new RectTransform[NumSparks];
        for (int i = 0; i < NumSparks; ++i)
        {
            sparkImages[i] = sparks != null ? FindRaw(sparks, "Spark" + i) : null;
            sparkRects[i] = sparks != null ? FindRect(sparks, "Spark" + i) : null;
        }
        comp.sparkImages = sparkImages;
        comp.sparkRects = sparkRects;

        var petals = stageCanvas.Find("Petals");
        var petalImages = new RawImage[NumPetals];
        var petalRects = new RectTransform[NumPetals];
        for (int i = 0; i < NumPetals; ++i)
        {
            petalImages[i] = petals != null ? FindRaw(petals, "Petal" + i) : null;
            petalRects[i] = petals != null ? FindRect(petals, "Petal" + i) : null;
        }
        comp.petalImages = petalImages;
        comp.petalRects = petalRects;

        var comets = stageCanvas.Find("CometTiles");
        var cometImages = new RawImage[NumCometTiles];
        var cometRects = new RectTransform[NumCometTiles];
        for (int i = 0; i < NumCometTiles; ++i)
        {
            cometImages[i] = comets != null ? FindRaw(comets, "CometTile" + i) : null;
            cometRects[i] = comets != null ? FindRect(comets, "CometTile" + i) : null;
        }
        comp.cometTileImages = cometImages;
        comp.cometTileRects = cometRects;

        var trails = stageCanvas.Find("CometTrails");
        var trailImages = new RawImage[NumCometTiles];
        var trailRects = new RectTransform[NumCometTiles];
        for (int i = 0; i < NumCometTiles; ++i)
        {
            trailImages[i] = trails != null ? FindRaw(trails, "CometTrail" + i) : null;
            trailRects[i] = trails != null ? FindRect(trails, "CometTrail" + i) : null;
        }
        comp.cometTrailImages = trailImages;
        comp.cometTrailRects = trailRects;

        comp.winTileGlowImage = FindRaw(stageCanvas, "WinTileGlow");
        comp.winTileGlowRect = FindRect(stageCanvas, "WinTileGlow");
        comp.winTileTrailImage = FindRaw(stageCanvas, "WinTileTrail");
        comp.winTileTrailRect = FindRect(stageCanvas, "WinTileTrail");

        var glows = stageCanvas.Find("CometGlows");
        var glowImages = new RawImage[NumCometTiles];
        var glowRects = new RectTransform[NumCometTiles];
        for (int i = 0; i < NumCometTiles; ++i)
        {
            glowImages[i] = glows != null ? FindRaw(glows, "CometGlow" + i) : null;
            glowRects[i] = glows != null ? FindRect(glows, "CometGlow" + i) : null;
        }
        comp.cometGlowImages = glowImages;
        comp.cometGlowRects = glowRects;

        var hand = canvas.Find("HandTiles");
        var handImages = new RawImage[MaxHandTiles];
        var handRects = new RectTransform[MaxHandTiles];
        for (int i = 0; i < MaxHandTiles; ++i)
        {
            handImages[i] = hand != null ? FindRaw(hand, "HandTile" + i) : null;
            handRects[i] = hand != null ? FindRect(hand, "HandTile" + i) : null;
        }
        comp.handTileImages = handImages;
        comp.handTileRects = handRects;

        comp.seSource = rootT.GetComponent<AudioSource>();
        comp.startSound = ResolveSfx(IntroPrefix, DefaultIntroSound);
        comp.revealSound = ResolveSfx(FlashPrefix, DefaultFlashSound);
        comp.rankSound = ResolveSfx(WritePrefix, DefaultWriteSound);
        comp.startVolume = KemonoAudioNormalizer.SeScale(comp.startSound, IntroVolume);
        comp.revealVolume = KemonoAudioNormalizer.SeScale(comp.revealSound, FlashVolume);
        comp.rankVolume = KemonoAudioNormalizer.SeScale(comp.rankSound, WriteVolume);

        var presentation = tableRoot.GetComponentInChildren<MahjongPresentationScreen>(true);
        if (presentation != null)
        {
            comp.charaMascotTextures = presentation.charaMascotTextures;
            comp.defaultMascotTexture = presentation.defaultMascotTexture;
        }

        var agariScreen = tableRoot.GetComponentInChildren<MahjongAgariScreen>(true);
        if (agariScreen != null && agariScreen.tileTemplate != null)
        {
            comp.tileAtlas = agariScreen.tileTemplate.texture;
            comp.faceDownColor = agariScreen.faceDownColor;
        }
        else
        {
            comp.tileAtlas = AssetDatabase.LoadAssetAtPath<Texture2D>(TileAtlasPath);
            comp.faceDownColor = new Color(0.45f, 0.60f, 0.90f, 1f);
        }

        int n = YakuIds.Length;
        var ids = new int[n];
        var bgs = new Texture[n];
        var names = new Texture[n];
        var colors = new Color[n];
        var handPaths = new int[n];
        var titleLayouts = new int[n];

        int bgFound = 0;
        int nameFound = 0;
        for (int i = 0; i < n; ++i)
        {
            ids[i] = YakuIds[i];
            colors[i] = YakuColors[i];
            handPaths[i] = YakuHandPathModes[i];
            titleLayouts[i] = YakuTitleLayoutModes[i];

            bgs[i] = LoadYakumanTexture("bg_" + YakuKeys[i]);
            names[i] = LoadYakumanTexture("name_" + YakuKeys[i]);
            if (bgs[i] != null) { ++bgFound; }
            if (names[i] != null) { ++nameFound; }
        }

        comp.yakuIds = ids;
        comp.yakuBackgrounds = bgs;
        comp.yakuNameTextures = names;
        comp.yakuColors = colors;
        comp.yakuHandPathModes = handPaths;
        comp.yakuTitleLayoutModes = titleLayouts;
        comp.defaultBackground = AssetDatabase.LoadAssetAtPath<Texture2D>(TexBgDefault);

        comp.defaultColor = new Color(1.00f, 0.86f, 0.42f, 1f);
        // 暗紺空間。真っ黒だと卓の暗い柄と区別がつかず「切り替わった」感が出ない。
        // 暗紺空間。参考動画の背景を実測した色 RGB(11,29,46)。
        // **α は 1.0 必須。** 0.97 だと世界が 8% 透けて桟橋の形が見え、
        // 「別空間へ切り替わった」ではなく「暗いガラス板」に見えてしまう。
        comp.vignetteColor = new Color(0.043f, 0.115f, 0.180f, 1.0f);

        comp.darkenSeconds = 0.12f;
        comp.sparkSeconds = 2.98f;
        comp.flashSeconds = 0.16f;
        comp.bgFadeSeconds = 0.28f;
        comp.writeDelaySeconds = 0.88f;
        comp.writeSeconds = 1.55f;
        comp.rankDelaySeconds = 0.06f;
        comp.rankSeconds = 0.18f;
        comp.holdSeconds = 1.71f;
        comp.exitSeconds = 0.20f;

        comp.sweepShakeSeconds = 0.12f;
        comp.sweepRiseSeconds = 0.18f;
        comp.sweepShakeAmp = 0.0045f;
        comp.sweepShakeSpeed = 68.0f;
        comp.sweepRise = 0.32f;

        comp.sparkRise = 900f;
        comp.sparkSpread = 620f;
        comp.bgZoom = 1.10f;
        comp.rankPunchScale = 1.70f;
        comp.seVolume = 0.45f;

        comp.fullFlashAlpha = 0.64f;
        comp.mascotMaxAlpha = 0.92f;
        comp.handEnterSeconds = 0.24f;
        comp.handTileStaggerSeconds = 0.003f;
        comp.handWinningDelaySeconds = 0.028f;
        comp.handLaunchHeight = 245.0f;
        comp.handLaunchSpread = 92.0f;
        comp.handArcHeight = 58.0f;
        comp.handImpactOvershoot = 18.0f;
        comp.handHitFlashAlpha = 0.09f;
        comp.titleGlyphInterval = 0.18f;
        comp.titleHitFlashAlpha = 0.14f;
        comp.impactShakeSeconds = 0.13f;
        comp.impactShakePixels = 12.0f;
        comp.titleTravelPixels = 42.0f;
        comp.petalColor = new Color(1.00f, 0.72f, 0.82f, 1f);
        comp.petalDrift = 300f;
        comp.cometRadius = 430f;
        comp.cometRise = 980f;
        // 周回して和了牌に合体するので、これは「入ってくるときの大きさ」。
        // 寄りきったところで和了牌と同じ大きさになる。
        comp.cometRecede = 0.55f;
        comp.cometOrbitTurns = 0.85f;
        comp.cometOrbitFlatten = 0.62f;
        // 1 = 常に正面を和了牌へ向ける。
        comp.cometTilt = 1.0f;
        comp.tileGlowColor = new Color(1.00f, 0.86f, 0.45f, 1f);
        // 2.6 だと光の玉だけが目立って牌が埋もれる。動画の暈は牌にぴったり寄っている。
        comp.tileGlowScale = 1.7f;

        // カメラワーク。牌より上から降りてきて正面で捉え、抜けた後は下から見上げる。
        comp.cinematicCamera = true;
        comp.camRiseFrom = 2.6f;
        comp.camRiseTo = -6.5f;
        comp.camPushIn = 0.6f;
        comp.camPullBack = 0.8f;
        comp.camFovPunch = 12.0f;
        comp.camAimLead = 1.1f;
        comp.camRoll = 22.0f;
        comp.camDrift = 0.09f;
        comp.camShake = 0.13f;
        comp.camShakeSeconds = 0.38f;
        comp.bgKenBurns = 0.045f;
        comp.sparkAlpha = 0.22f;
        // 奥行き。StageCamDistance(12.4m) / StageCanvasScale(0.01) = 1240 Canvas単位。
        // ステージの距離や縮尺を変えたらここも合わせること。
        comp.useStageDepth = true;
        comp.stageDepthBase = StageCamDistance / StageCanvasScale;
        // 立ち絵の濃さ。ステージ映像の中に居た頃は 0.55（濃いと牌が負けた）だが、
        // 帯パネルへ移してステージ映像の上に独立して乗るようになったので、
        // 参照動画に寄せて濃くする。半透明なので牌は透けたまま見える。
        comp.mascotMaxAlpha = 0.80f;

        // 導入の和了牌・彗星を卓の実物3D牌で見せる（置くのは Mahjong.cs 側）。
        //
        // realTileEuler の 180 度は必須。Canvas の forward は「見る人と反対向き」
        // （ビルボードが LookRotation で自分から見た向きを作っているため）なので、
        // そのまま牌に渡すと**裏面（緑）がこちらを向く**。UIシェーダーは Cull Off で
        // 両面見えるが牌メッシュは片面なので、ここで反転させる必要がある。
        comp.useRealIntroTiles = true;
        // **ステージの縮尺に合わせること。** ステージ Canvas は 1600x900 @ 0.01 = 16m x 9m で、
        // パネル(@0.0006132)の約16倍。実物の牌は幅 0.0212m しかないので、パネル用の 2.4 のままだと
        // ステージ上では点にしかならず「牌が消えた」ように見える（実際にそうなった）。
        //   CometTileSize.x(84) * StageCanvasScale(0.01) / 牌の実幅(0.0212) = 39.6
        comp.realTileScale = 39.6f;
        // 動画では牌が奥へわずかに倒れて（＝上面が少し見える）飛んでいる。
        // Y=180 は面をこちらへ向けるための必須の反転（無いと裏の緑が見える）。
        comp.realTileEuler = new Vector3(12f, 180f, 0f);
        // 手前へ浮かせる量もステージ縮尺に合わせる（0.035 * 16 ≒ 0.5m）。
        comp.realTileDepth = 0.5f;
        comp.showRankInCutin = false;

        UdonSharpEditorUtility.CopyProxyToUdon(comp);
        EditorUtility.SetDirty(comp);
        MarkDirty(rootT.gameObject);

        Debug.Log("[WireYakumanCutin] " + GetPath(rootT) + " を配線（背景 " + bgFound + "/" + n
            + "・筆文字 " + nameFound + "/" + n + "・動的手牌 " + MaxHandTiles + " 枚）。"
            + (bgFound < n ? " 未割当の役は既定背景＋Textで出ます。" : ""));

        return comp;
    }

    static Texture2D LoadYakumanTexture(string baseName)
    {
        var path = YakumanTextureDir + "/" + baseName + ".png";
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    // ============================================================
    //  Validate
    // ============================================================

[MenuItem("Tools/Kemono Mahjong/Validate Yakuman Cutin")]
    public static void Validate()
    {
        var tables = Object.FindObjectsOfType<Mahjong>(true);
        if (tables == null || tables.Length == 0)
        {
            Debug.LogWarning("[ValidateYakumanCutin] シーンに Mahjong が見つかりません。");
            return;
        }

        int bad = 0;
        foreach (var t in tables)
        {
            if (t.yakumanCutin == null)
            {
                Debug.LogError("[ValidateYakumanCutin] 卓に yakumanCutin が未割当: " + GetPath(t.transform));
                ++bad;
                continue;
            }

            var c = t.yakumanCutin;
            string p = GetPath(c.transform);

            if (c.fxRoot == null) { Debug.LogError("[ValidateYakumanCutin] fxRoot 欠損: " + p); ++bad; }
            if (c.billboardRoot == null) { Debug.LogError("[ValidateYakumanCutin] billboardRoot 欠損: " + p); ++bad; }
            if (c.bgImage == null) { Debug.LogError("[ValidateYakumanCutin] bgImage 欠損: " + p); ++bad; }
            if (c.flashImage == null) { Debug.LogError("[ValidateYakumanCutin] 全面Flash欠損: " + p); ++bad; }
            if (c.mascotImage == null) { Debug.LogError("[ValidateYakumanCutin] Mascot欠損: " + p); ++bad; }
            if (c.winningTileImage == null) { Debug.LogError("[ValidateYakumanCutin] WinningTile欠損: " + p); ++bad; }
            if (c.tileAtlas == null) { Debug.LogError("[ValidateYakumanCutin] tiles.png欠損: " + p); ++bad; }
            int wipeN = 0;
            if (c.mascotWipeStrips != null)
            {
                foreach (var x in c.mascotWipeStrips) { if (x != null) { ++wipeN; } }
            }
            if (c.mascotWipeRoot == null || wipeN != MascotWipeStripCount)
            {
                Debug.LogError("[ValidateYakumanCutin] 立ち絵の帯 " + wipeN + "/" + MascotWipeStripCount
                    + "（root=" + (c.mascotWipeRoot != null) + "）: " + p);
                ++bad;
            }

            int petalN = 0;
            if (c.petalImages != null) { foreach (var x in c.petalImages) { if (x != null) { ++petalN; } } }
            if (petalN != NumPetals) { Debug.LogError("[ValidateYakumanCutin] 花びら " + petalN + "/" + NumPetals + ": " + p); ++bad; }

            int cometN = 0;
            if (c.cometTileImages != null) { foreach (var x in c.cometTileImages) { if (x != null) { ++cometN; } } }
            if (cometN != NumCometTiles) { Debug.LogError("[ValidateYakumanCutin] 彗星牌 " + cometN + "/" + NumCometTiles + ": " + p); ++bad; }
            if (c.handTileImages == null || c.handTileImages.Length != MaxHandTiles)
            {
                Debug.LogError("[ValidateYakumanCutin] 手牌プールが " + MaxHandTiles + " 枚ではありません: " + p);
                ++bad;
            }
            if (c.nameImage == null && c.nameText == null)
            {
                Debug.LogError("[ValidateYakumanCutin] 役名の表示先が両方欠損: " + p);
                ++bad;
            }
            if (c.nameSecondaryText == null)
            {
                Debug.LogError("[ValidateYakumanCutin] 分割役名の第2表示先が欠損: " + p);
                ++bad;
            }
            if (c.yakuIds == null || c.yakuIds.Length != YakuIds.Length
                || c.yakuHandPathModes == null || c.yakuHandPathModes.Length != YakuIds.Length
                || c.yakuTitleLayoutModes == null || c.yakuTitleLayoutModes.Length != YakuIds.Length)
            {
                Debug.LogError("[ValidateYakumanCutin] 役満データ配列が " + YakuIds.Length + " 件ではありません: " + p);
                ++bad;
            }
            if (c.fxRoot != null && c.fxRoot.activeSelf)
            {
                Debug.LogWarning("[ValidateYakumanCutin] FxRoot が出しっぱなしです: " + p);
            }

            int bgN = 0;
            int nameN = 0;
            if (c.yakuBackgrounds != null)
            {
                foreach (var tex in c.yakuBackgrounds) { if (tex != null) { ++bgN; } }
            }
            if (c.yakuNameTextures != null)
            {
                foreach (var tex in c.yakuNameTextures) { if (tex != null) { ++nameN; } }
            }

            Debug.Log("[ValidateYakumanCutin] " + p + " 背景 " + bgN + "/" + YakuIds.Length
                + "・筆文字 " + nameN + "/" + YakuIds.Length
                + "・手牌 " + (c.handTileImages != null ? c.handTileImages.Length : 0)
                + "・演出長 " + c.GetDuration().ToString("0.00") + "s");
        }

        var introSfx = ResolveSfx(IntroPrefix, DefaultIntroSound);
        var flashSfx = ResolveSfx(FlashPrefix, DefaultFlashSound);
        var writeSfx = ResolveSfx(WritePrefix, DefaultWriteSound);
        Debug.Log("[ValidateYakumanCutin] SE置き場: " + SfxDir
            + "（" + (AssetDatabase.IsValidFolder(SfxDir) ? "あり" : "なし") + "）"
            + "\n  intro* 導入 0.00秒〜 : " + DescribeSfx(introSfx)
            + "\n  flash* 閃光 約3.05秒 : " + DescribeSfx(flashSfx)
            + "\n  write* 役名 約4.26秒 : " + DescribeSfx(writeSfx));

        if (bad == 0) { Debug.Log("[ValidateYakumanCutin] 参照欠損なし（" + tables.Length + " 卓）。"); }
        else { Debug.LogError("[ValidateYakumanCutin] 問題 " + bad + " 件。Build → Wire を実行してください。"); }
    }

    // ============================================================
    //  素材（手続き生成のプレースホルダ）
    // ============================================================

    public static void EnsureFxTextures()
    {
        EnsureFolder(FxTextureDir);
        EnsureFolder(YakumanTextureDir);

        // 単色（暗幕用）。色はスクリプト側で塗る。
        {
            var px = new Color[4 * 4];
            for (int i = 0; i < px.Length; ++i) { px[i] = Color.white; }
            WriteTextureIfMissing(TexSolid, 4, 4, px);
        }

        // 光弾：縦に細長い、上が明るく下が消えるソフトな筋。
        {
            const int W = 32, H = 128;
            var px = new Color[W * H];
            for (int y = 0; y < H; ++y)
            {
                float v = (float)y / (H - 1);
                // 上端(v=1)が芯。下へ尾を引く。
                float tail = Mathf.Pow(v, 2.2f);
                for (int x = 0; x < W; ++x)
                {
                    float u = Mathf.Abs((x / (float)(W - 1)) - 0.5f) * 2f;
                    float core = Mathf.Clamp01(1f - u);
                    float a = tail * core * core;
                    px[y * W + x] = new Color(1f, 1f, 1f, a);
                }
            }
            WriteTextureIfMissing(TexSpark, W, H, px);
        }

        // 閃光：横一文字。中央が白く両端へ消える。
        {
            const int W = 256, H = 32;
            var px = new Color[W * H];
            for (int y = 0; y < H; ++y)
            {
                float dv = Mathf.Abs((y / (float)(H - 1)) - 0.5f) * 2f;
                float vy = Mathf.Clamp01(1f - dv);
                for (int x = 0; x < W; ++x)
                {
                    float du = Mathf.Abs((x / (float)(W - 1)) - 0.5f) * 2f;
                    float vx = Mathf.Clamp01(1f - du * du);
                    px[y * W + x] = new Color(1f, 1f, 1f, vx * vy * vy);
                }
            }
            WriteTextureIfMissing(TexFlash, W, H, px);
        }

        // 光球：放射状のグロー。
        {
            const int S = 64;
            var px = new Color[S * S];
            for (int y = 0; y < S; ++y)
            {
                for (int x = 0; x < S; ++x)
                {
                    float dx = (x / (float)(S - 1)) - 0.5f;
                    float dy = (y / (float)(S - 1)) - 0.5f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy) * 2f;
                    float a = Mathf.Clamp01(1f - d);
                    px[y * S + x] = new Color(1f, 1f, 1f, a * a * a);
                }
            }
            WriteTextureIfMissing(TexGlow, S, S, px);
        }

        // 花びら：先の尖った楕円。回転しながら舞う。
        {
            const int W = 64, H = 42;
            var px = new Color[W * H];
            for (int y = 0; y < H; ++y)
            {
                float v = (y / (float)(H - 1)) * 2f - 1f;       // -1..1
                for (int x = 0; x < W; ++x)
                {
                    float u = x / (float)(W - 1);              // 0..1
                    // 左が細く右が丸い花弁のシルエット。
                    float half = Mathf.Sin(u * Mathf.PI) * Mathf.Lerp(0.45f, 1.0f, u);
                    float d = half <= 0.001f ? 2f : Mathf.Abs(v) / half;
                    float a = Mathf.Clamp01(1f - d);
                    px[y * W + x] = new Color(1f, 1f, 1f, a * a);
                }
            }
            WriteTextureIfMissing(TexPetal, W, H, px);
        }

        // 全面フラッシュ：四隅まで届く放射ブルーム（中心が白熱、端も完全には落ちない）。
        {
            const int W = 256, H = 144;
            var px = new Color[W * H];
            for (int y = 0; y < H; ++y)
            {
                for (int x = 0; x < W; ++x)
                {
                    float dx = ((x / (float)(W - 1)) - 0.5f) * 2f;
                    float dy = ((y / (float)(H - 1)) - 0.5f) * 2f;
                    float d = Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy) / 1.414f);
                    // 端でも 0.55 残す＝画面全体が飛ぶ。
                    float a = Mathf.Lerp(1f, 0.55f, d * d);
                    px[y * W + x] = new Color(1f, 1f, 1f, a);
                }
            }
            WriteTextureIfMissing(TexFlashFull, W, H, px);
        }

        // 既定背景：役ごとの絵が入るまでの下地。中央が明るい紺の放射。
        {
            const int W = 256, H = 144;
            var px = new Color[W * H];
            for (int y = 0; y < H; ++y)
            {
                for (int x = 0; x < W; ++x)
                {
                    float dx = (x / (float)(W - 1)) - 0.5f;
                    float dy = (y / (float)(H - 1)) - 0.5f;
                    float d = Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy) * 1.9f);
                    float v = 1f - d;
                    px[y * W + x] = new Color(
                        0.05f + 0.16f * v,
                        0.06f + 0.20f * v,
                        0.12f + 0.34f * v,
                        1f);
                }
            }
            WriteTextureIfMissing(TexBgDefault, W, H, px);
        }
    }

    // ============================================================
    //  以下、共通ヘルパー（BuildCallEffect と同じ流儀）
    // ============================================================

    // ステージの Canvas。World Space だが卓から離れた場所にあり、専用カメラだけが見る。
    static GameObject EnsureStageCanvas(Transform parent)
    {
        var go = EnsureUiChild(parent, "StageCanvas");

        var canvas = go.GetComponent<Canvas>();
        if (canvas == null) { canvas = go.AddComponent<Canvas>(); }
        canvas.renderMode = RenderMode.WorldSpace;

        var scaler = go.GetComponent<CanvasScaler>();
        if (scaler == null) { scaler = go.AddComponent<CanvasScaler>(); }
        scaler.dynamicPixelsPerUnit = 2f;

        var raycaster = go.GetComponent<GraphicRaycaster>();
        if (raycaster != null) { Object.DestroyImmediate(raycaster); }

        var rt = go.GetComponent<RectTransform>();
        rt.localPosition = Vector3.zero;
        rt.localRotation = Quaternion.identity;
        rt.localScale = new Vector3(StageCanvasScale, StageCanvasScale, StageCanvasScale);
        rt.sizeDelta = CanvasSize;

        return go;
    }

    // ステージ専用カメラ。RenderTexture へ描き、パネルの StageView に映す。
    //  ・単色クリア＝背景が「空間」になる（世界は一切写らない）
    //  ・カリングマスクはステージのレイヤーのみ
    //  ・影・HDR・MSAA・オクルージョンは全部切る（Quest基準）
    //  ・普段は無効。役満の導入の間だけスクリプトが有効化する
    static Camera EnsureStageCamera(Transform parent)
    {
        var go = EnsureChild(parent, "StageCam");
        go.transform.localPosition = new Vector3(0f, 0f, -StageCamDistance);
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;

        var cam = go.GetComponent<Camera>();
        if (cam == null) { cam = Undo.AddComponent<Camera>(go); }

        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.043f, 0.115f, 0.180f, 1f);
        cam.orthographic = false;
        cam.fieldOfView = StageCamFov;
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 200f;
        cam.depth = -20f;
        cam.allowHDR = false;
        cam.allowMSAA = false;
        cam.useOcclusionCulling = false;
        cam.renderingPath = RenderingPath.Forward;

        int stageLayer = LayerMask.NameToLayer(StageLayerName);
        cam.cullingMask = stageLayer >= 0 ? (1 << stageLayer) : 0;

        cam.targetTexture = AssetDatabase.LoadAssetAtPath<RenderTexture>(StageRtPath);

        var al = go.GetComponent<AudioListener>();
        if (al != null) { Object.DestroyImmediate(al); }

        cam.enabled = false;   // 演出中だけ有効化する
        return cam;
    }

    // Hovl のバーストを1個取り込む。マテリアルは Quest 対応へ差し替える。
    static GameObject EnsureBurst(Transform parent, string name, string sourcePath,
        float scale, float lifeCap)
    {
        // 同名の重複を掃除する。Stage はプレハブインスタンスの中なので、
        // プレハブ由来の子は DestroyImmediate が拒否される。以前ここで
        // 「毎回作り直す」ようにしていたら、削除に失敗したまま追加され続けて
        // 1回の Build ごとに1組ずつ増えた（9組まで増えた）。
        // 先頭（プレハブ由来）を残し、あとから足された分だけ消す。
        Transform keep = null;
        var dupes = new System.Collections.Generic.List<Transform>();
        for (int i = 0; i < parent.childCount; ++i)
        {
            var ch = parent.GetChild(i);
            if (ch.name != name) { continue; }
            if (keep == null) { keep = ch; } else { dupes.Add(ch); }
        }
        foreach (var d in dupes) { Object.DestroyImmediate(d.gameObject); }

        if (keep != null)
        {
            keep.localScale = Vector3.one * scale;
            ConvertParticleMaterials(keep.gameObject);
            PrepareBurst(keep.gameObject, lifeCap);
            return keep.gameObject;
        }

        // 素材そのものを差し替えたいときは、プレハブ側で該当オブジェクトを
        // 手で消してから Build する（ここでは消せないため）。
        var src = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
        if (src == null)
        {
            Debug.LogWarning("[BuildYakumanCutin] バーストのプレハブが見つかりません: " + sourcePath
                + " … バースト無しで続行します。");
            return null;
        }

        var go = (GameObject)PrefabUtility.InstantiatePrefab(src, parent);
        // 素材側の更新に引きずられないよう解除する（マテリアルを差し替えるため）。
        PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        go.name = name;
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one * scale;

        ConvertParticleMaterials(go);
        PrepareBurst(go, lifeCap);
        return go;
    }

    // 勝手に鳴り出さないよう止めておく。再生はスクリプトが指示する。
    static void PrepareBurst(GameObject go, float lifeCap)
    {
        foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
        {
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = false;

            // 金へ塗り替える。テクスチャは白黒なので色は startColor が全部決めている。
            main.startColor = GoldFor(ps.name, main.startColor);

            // 長く残ると次の段に被る。粒は特に目立つので上限をもう一段厳しくする。
            float cap = ps.name == "Sparks" ? Mathf.Min(lifeCap, FxSparkLifetime) : lifeCap;
            var life = main.startLifetime;
            if (life.mode == ParticleSystemCurveMode.TwoConstants)
            {
                life.constantMin = Mathf.Min(life.constantMin, cap);
                life.constantMax = Mathf.Min(life.constantMax, cap);
            }
            else
            {
                life.constant = Mathf.Min(life.constant, cap);
            }
            main.startLifetime = life;

            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }
        foreach (var r in go.GetComponentsInChildren<ParticleSystemRenderer>(true))
        {
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }
    }

    // Holy hit（実地で色が正しいと確認できている方）の配色に揃える。
    // アルファは元の値を残す＝濃さの設計はプレハブ側を尊重する。
    static ParticleSystem.MinMaxGradient GoldFor(string psName, ParticleSystem.MinMaxGradient current)
    {
        float a = current.mode == ParticleSystemGradientMode.TwoColors
            ? current.colorMax.a
            : current.color.a;

        // 子の系統名は Hovl 側で固定。部分一致だと、ルートの "FlashBurst" が
        // 子の "Flash" の枝に落ちて本体の火花まで暗くなる。
        Color gold;
        if (psName == "Glow")
        {
            gold = new Color(1.00f, 0.95f, 0.33f, a);
        }
        else if (psName == "Sparks")
        {
            gold = new Color(1.00f, 0.86f, 0.31f, a);
        }
        else if (psName == "Flash")
        {
            gold = new Color(0.68f, 0.57f, 0.22f, a);
        }
        else if (psName == "ShockWave")
        {
            gold = new Color(1.00f, 0.95f, 0.22f, a);
        }
        else
        {
            // 本体。動画の火花と同じで、芯が濃い金・縁が淡い金。
            return new ParticleSystem.MinMaxGradient(
                new Color(1.00f, 0.77f, 0.00f, a),
                new Color(1.00f, 0.92f, 0.55f, a));
        }
        return new ParticleSystem.MinMaxGradient(gold);
    }

    // Particles/Standard Unlit は Quest の許可シェーダーに無い。
    // 複製を作って VRChat/Mobile/Particles/Additive に差し替える。
    static void ConvertParticleMaterials(GameObject go)
    {
        var shader = Shader.Find(QuestParticleShader);
        if (shader == null)
        {
            Debug.LogWarning("[BuildYakumanCutin] " + QuestParticleShader + " が見つかりません。変換をスキップします。");
            return;
        }

        EnsureFolder(FxMaterialDir);

        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
        {
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; ++i)
            {
                var m = mats[i];
                if (m == null) { continue; }
                if (m.shader == shader) { continue; }   // 変換済み

                string path = FxMaterialDir + "/" + m.name + "_Quest.mat";
                var conv = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (conv == null)
                {
                    conv = new Material(shader);
                    conv.name = m.name + "_Quest";
                    if (m.HasProperty("_MainTex") && conv.HasProperty("_MainTex"))
                    {
                        conv.SetTexture("_MainTex", m.GetTexture("_MainTex"));
                    }
                    // 元は _Color、VRChat 側は _TintColor。
                    if (conv.HasProperty("_TintColor"))
                    {
                        Color tint = m.HasProperty("_Color") ? m.GetColor("_Color") : Color.white;
                        conv.SetColor("_TintColor", tint);
                    }
                    AssetDatabase.CreateAsset(conv, path);
                }
                mats[i] = conv;
            }
            r.sharedMaterials = mats;
        }
        AssetDatabase.SaveAssets();
    }

    static void SetLayerRecursive(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        for (int i = 0; i < t.childCount; ++i) { SetLayerRecursive(t.GetChild(i), layer); }
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

    static RawImage EnsureFxImage(Transform parent, string name, string texturePath,
        Vector2 size, Vector2 pos, Vector2 pivot)
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
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        rt.localScale = Vector3.one;
        rt.localRotation = Quaternion.identity;

        return raw;
    }

    static Text EnsureNameText(Transform parent, string name, bool withOutline)
    {
        var go = EnsureUiChild(parent, name);

        var text = go.GetComponent<Text>();
        if (text == null) { text = go.AddComponent<Text>(); }

        var font = AssetDatabase.LoadAssetAtPath<Font>(BrushFontPath);
        if (font == null)
        {
            Debug.LogWarning("[BuildYakumanCutin] 筆書体フォントが見つかりません: " + BrushFontPath
                + " … 既定フォントで作ります（見た目は筆文字になりません）。");
        }
        else
        {
            text.font = font;
        }

        // UI Text のフォントサイズは 300 で頭打ち。大きさは RectTransform のスケールで稼ぐ
        // （ネームプレート／宣言演出で踏んだのと同じ制約）。
        text.fontSize = NameFontSize;
        text.alignment = TextAnchor.MiddleLeft;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.resizeTextForBestFit = false;
        text.text = "";
        text.color = new Color(1f, 1f, 1f, 0f);

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
        rt.pivot = new Vector2(0f, 0.5f);
        // 影は右下へ少しずらす（前面の文字と同じ位置だと縁が濁る）。
        rt.anchoredPosition = withOutline ? NameHome : (NameHome + new Vector2(8f, -8f));
        rt.sizeDelta = NameSize;
        rt.localScale = Vector3.one;
        rt.localRotation = Quaternion.identity;

        return text;
    }

    static Text EnsureRankText(Transform parent)
    {
        var go = EnsureUiChild(parent, "RankText");

        var text = go.GetComponent<Text>();
        if (text == null) { text = go.AddComponent<Text>(); }

        var font = AssetDatabase.LoadAssetAtPath<Font>(BrushFontPath);
        if (font != null) { text.font = font; }

        text.fontSize = RankFontSize;
        text.alignment = TextAnchor.MiddleCenter;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.resizeTextForBestFit = false;
        text.text = "";
        text.color = new Color(1f, 1f, 1f, 0f);

        var outline = go.GetComponent<Outline>();
        if (outline == null) { outline = go.AddComponent<Outline>(); }
        outline.effectColor = OutlineColor;
        outline.effectDistance = OutlineDistance;
        outline.useGraphicAlpha = true;

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = RankHome;
        rt.sizeDelta = new Vector2(700f, 180f);
        rt.localScale = Vector3.one;
        rt.localRotation = Quaternion.identity;

        return text;
    }

    static RawImage FindRaw(Transform parent, string name)
    {
        var t = parent.Find(name);
        return t != null ? t.GetComponent<RawImage>() : null;
    }

    static Text FindText(Transform parent, string name)
    {
        var t = parent.Find(name);
        return t != null ? t.GetComponent<Text>() : null;
    }

    static RectTransform FindRect(Transform parent, string name)
    {
        var t = parent.Find(name);
        return t != null ? t.GetComponent<RectTransform>() : null;
    }

    // Prefab ステージを保存して**必ず閉じる**。
    //  ※ SaveAsPrefabAsset の途中で UdonSharp の Odin シリアライザが
    //    「ArgumentNullException: unityObject」を吐くことがあるが、保存自体は通る。
    //    紛らわしいので、保存後に必ず VerifyPrefab で実体を検証すること。
    //  ※ 閉じるときは ClearDirtiness してから GoToMainStage する。
    //    開いたまま／dirty のままにすると Unity が固まる（精算パネルで踏んだ地雷）。
    //    BuildCallEffect は古い版でこの閉じ処理が無いので、あちらを真似ないこと。
static void SavePrefabStage(PrefabStage stage)
    {
        if (stage == null || stage.prefabContentsRoot == null)
        {
            Debug.LogError("[BuildYakumanCutin] Prefab Stage が無効なため保存できません。");
            StageUtility.GoToMainStage();
            return;
        }

        // Prefab Stage の標準保存経路を通す。UdonSharp のシリアライズ処理も
        // File/Save のコールバック内で実行されるため、SaveAsPrefabAsset の直呼びはしない。
        EditorSceneManager.MarkSceneDirty(stage.scene);
        bool saveInvoked = EditorApplication.ExecuteMenuItem("File/Save");
        if (!saveInvoked || stage.scene.isDirty)
        {
            Debug.LogError("[BuildYakumanCutin] Prefab の保存に失敗しました: " + stage.assetPath);
            return;
        }

        AssetDatabase.SaveAssets();
        StageUtility.GoToMainStage();
    }

    static void VerifyPrefab()
    {
        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(TablePrefabPath);
        if (asset == null)
        {
            Debug.LogError("[WireYakumanCutin] Prefab を読み直せません: " + TablePrefabPath);
            return;
        }

        var comp = asset.GetComponentInChildren<MahjongYakumanCutin>(true);
        if (comp == null)
        {
            Debug.LogError("[WireYakumanCutin] 保存後の Prefab に MahjongYakumanCutin がありません。");
            return;
        }

        var table = asset.GetComponent<Mahjong>();
        if (table == null || table.yakumanCutin == null)
        {
            Debug.LogError("[WireYakumanCutin] 保存後の Prefab で 卓→カットイン の参照が空です。");
            return;
        }

        Debug.Log("[WireYakumanCutin] Prefab 検証OK（演出長 " + comp.GetDuration().ToString("0.00") + "s）: "
            + TablePrefabPath);
    }

    static string DescribeSfx(AudioClip clip)
    {
        if (clip == null) { return "なし（無音）"; }

        string path = AssetDatabase.GetAssetPath(clip);
        bool fromDir = path != null && path.StartsWith(SfxDir);
        return clip.name + " " + clip.length.ToString("F2") + "秒"
            + (fromDir ? "（差し替えフォルダ）" : "（既定＝暫定素材 " + path + "）");
    }

    /// <summary>差し替えフォルダの音源（接頭辞一致）→ 無ければ既定パス、の順で拾う。</summary>
    static AudioClip ResolveSfx(string prefix, string defaultPath)
    {
        if (AssetDatabase.IsValidFolder(SfxDir))
        {
            foreach (var guid in AssetDatabase.FindAssets("t:AudioClip", new[] { SfxDir }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var name = System.IO.Path.GetFileNameWithoutExtension(path);
                if (name == null) { continue; }
                if (!name.ToLowerInvariant().StartsWith(prefix)) { continue; }

                var found = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                if (found != null) { return found; }
            }
        }
        return AssetDatabase.LoadAssetAtPath<AudioClip>(defaultPath);
    }

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
        return true;
    }

    static bool EnsureProgramAsset(string scriptPath, string assetPath)
    {
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
        if (script == null)
        {
            Debug.LogError("[WireYakumanCutin] スクリプトが見つかりません: " + scriptPath);
            return false;
        }
        var type = script.GetClass();
        if (type != null && UdonSharpProgramAsset.GetProgramAssetForClass(type) != null) { return false; }
        if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(assetPath) != null) { return false; }

        EnsureFolder(ProgramAssetDir);

        var programAsset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
        programAsset.sourceCsScript = script;
        AssetDatabase.CreateAsset(programAsset, assetPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[WireYakumanCutin] U#プログラムアセットを生成: " + assetPath);
        return true;
    }

    static UdonSharpBehaviour EnsureUSharpComponent(GameObject go, System.Type type)
    {
        var proxy = (UdonSharpBehaviour)go.GetComponent(type);
        if (proxy != null)
        {
            if (UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy) != null) { return proxy; }

            Debug.LogWarning("[WireYakumanCutin] " + type.Name + " の backing UdonBehaviour が無いため作り直します。");
            Object.DestroyImmediate(proxy);
            foreach (var ub in go.GetComponents<UdonBehaviour>())
            {
                if (ub.programSource == null) { Object.DestroyImmediate(ub); }
            }
        }

        var added = (UdonSharpBehaviour)UdonSharpUndo.AddComponent(go, type);
        if (added != null && UdonSharpEditorUtility.GetBackingUdonBehaviour(added) == null)
        {
            Debug.LogError("[WireYakumanCutin] " + type.Name + " の追加に失敗（プログラムアセット未コンパイル？）。");
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

    static void SetSiblingOrder(Transform parent, string[] orderedNames)
    {
        for (int i = 0; i < orderedNames.Length; ++i)
        {
            var c = parent.Find(orderedNames[i]);
            if (c != null) { c.SetSiblingIndex(i); }
        }
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
