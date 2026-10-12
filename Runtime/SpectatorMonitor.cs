using TMPro;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Rendering;
using VRC.SDKBase;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    //  観戦モニター（VVMW の画面と切り替えて使う・第1段＝テロップのみ）
    // ------------------------------------------------------------
    //  ・VVMW の画面の 2cm 手前に同じ大きさ（3.56m x 2.00m）の Canvas を重ね、
    //    見ている人の端末でだけ「動画 ⇔ 観戦」を切り替える。同期変数ゼロ。
    //    VVMW 本体は止めない（画面の Renderer と操作UIを隠すだけ）＝他の人の動画に影響しない。
    //  ・配信卓は「自動（既定）」か「1〜8 の手動」。自動は MatchmakingManager の同期マスクと
    //    PlayerData（段位）だけで選ぶ＝寝ている卓の中身は読まない。1試合を最後まで追い、
    //    終局してから次の卓へ移る（局の途中で切り替えない）。
    //  ・配信卓は、観戦中かつモニターの近くにいる端末でだけ起こす（TableProximityCuller が
    //    IsKeepingTableAwake を見る）。起こす卓は端末ごとに最大1つ。
    //  ・描画は卓の Spectate_Revision が変わった時と、点数の表示値が動いた時だけ。
    //    手牌の表裏は卓の秘匿ポリシー（Mahjong.Spectate_IsHandVisible）をそのまま使う。
    //  ・レイアウトの部品は BuildSpectatorMonitor が作る。座標の定数は両者で対になっている。
    //
    //  第2段（カメラ）：配信卓の上を飛ぶカメラ1台を RenderTexture へ描き、盤面図の代わりに映す。
    //    ショットは 俯瞰（ゆっくり回る）／手番の肩越し／宣言者のアップ／和了者の手元。切り替えは
    //    卓の状態変化だけから各端末で決める（同期ゼロ）。描く頻度を落とし（PC 30fps・Quest 15fps）、
    //    Quest の既定は盤面図（カメラは手動で ON）。
    //  中継表示（Mリーグ式・既定）：カメラ映像を全画面に出し、左上に局、下に4人の帯だけを重ねる。
    //    主役は手番の人の「手元カメラ」（卓の縁・本人の左・卓面から12cm）。手番が移ったら切り替え
    //    （最低2秒は保つ）、人間の長考6秒で顔、宣言で顔、和了は顔→手元、配牌・流局・精算は全景。
    //    2026-10-04 Mリーグの1局分の分析（約40ショット・平均2.9秒）に合わせて演出順を作り直した：
    //      局の開始＝全景→ドラ表示牌の真上アップ→顔の紹介／人間の手番＝顔1.2秒→手元（速い手番は手元→手元）／
    //      長考＝顔と手元を角度を変えて行き来／立直＝顔へ寄る→以後は他家の顔を長めに／
    //      和了＝手元→放銃者の顔→他家の顔→和了者の顔→全景。顔は正面・肩越し・横顔を順に使う。
    //      手元カメラは固定（動かすと手牌が揺れて読みにくい）。
    //    解説表示（手牌の行＋カメラ or 盤面図）へは操作帯の「表示」で切り替える。
    //  第3段（演出・解説）：立直／鳴き／北抜き／ツモ／ロン／流局のカットイン、各家の待ち牌と残り枚数、
    //    トップ差、オーラスの逆転条件（三麻・四麻）、宣言ボイスをモニターの位置で鳴らし直す。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class SpectatorMonitor : UdonSharpBehaviour
    {
        // ---- 参照 ----
        public MatchmakingManager matchmaking;
        public PlayerStatsStore statsStore;
        [Tooltip("表示言語（ワールド共通）。日本語なら日本語だけ、英語なら英語だけを出す")]
        public KemonoLanguage language;

        [Tooltip("VVMW の画面（Screen With Overlay/Screen）の Renderer。観戦中は描画だけ止める")]
        public Renderer vvmwScreenRenderer;
        [Tooltip("VVMW の操作UI（Screen With Overlay/MainUI）。観戦中は隠す（見えない操作UIを誤って押させない）")]
        public GameObject vvmwOverlay;

        [Tooltip("観戦画面の Canvas。観戦中だけ表示")]
        public GameObject monitorRoot;
        [Tooltip("盤面と各家の行（配信卓が無い時は隠す）")]
        public GameObject boardRoot;
        public TextMeshProUGUI idleText;

        [Tooltip("この距離より遠くでは配信卓を起こさない（観戦モードのまま離れた人が卓を起こし続けないように）")]
        public Transform rangeCenter;
        public float keepAwakeRange = 20.0f;
        [Tooltip("観戦ルーム（Carpet_01 の上）の判定。ルームに入ったら観戦を ON、出たら OFF にして切替ボタンも隠す。\n空なら従来どおりどこでも使える。")]
        public TableVoiceRouter voiceRouter;

        // ---- 牌の絵（卓と同じ tiles.png。添え字は 牌ID >> 2）----
        public Texture tileTexture;
        public float[] tileU;
        public float[] tileV;
        public float[] tileW;
        public float[] tileH;
        public Color tileBackColor = new Color(0.85f, 0.55f, 0.15f, 1f);
        public Color tsumogiriTint = new Color(0.62f, 0.62f, 0.62f, 1f);

        // ---- 寸法（Canvasピクセル。BuildSpectatorMonitor と対）----
        public float riverTileW = 52f;
        public float riverTileH = 70f;
        public float riverStartY = -150f;
        public float rowTileW = 44f;
        public float rowTileH = 59f;
        public float rowTileBaseY = 18f;
        public float rowTileLeft = 12f;
        public float rowTileMaxWidth = 816f;

        // ---- 盤面（席0..3。River の入れ物は席の向きへ回してある）----
        public RawImage[] riverTiles;            // 席 * RIVER_MAX + j
        public TextMeshProUGUI[] seatBoxTexts;   // 中央の箱の各辺「東 25000」
        public GameObject[] reachSticks;         // 中央の箱の各辺の立直棒
        public TextMeshProUGUI centerRoundText;
        public TextMeshProUGUI centerSubText;

        // ---- 右側の行（行0..3＝東南西北の順。席は (親 + 行) % 人数）----
        public GameObject[] rowRoots;
        public Image[] rowBacks;
        public Image[] rowAccents;
        public TextMeshProUGUI[] rowWindTexts;
        public TextMeshProUGUI[] rowNameTexts;
        public TextMeshProUGUI[] rowPointTexts;
        public TextMeshProUGUI[] rowRankTexts;
        public TextMeshProUGUI[] rowStatusTexts;
        public RawImage[] rowTiles;              // 行 * ROW_MAX + n

        // ---- 上部の帯 ----
        public TextMeshProUGUI roundText;
        public TextMeshProUGUI honbaText;
        public TextMeshProUGUI remainText;
        public TextMeshProUGUI tableText;
        public TextMeshProUGUI ruleText;
        public TextMeshProUGUI doraLabelText;
        public RawImage[] doraTiles;             // 5

        // ---- 操作帯（画面の下）----
        public Image modeButtonBack;
        public TextMeshProUGUI modeButtonText;
        public GameObject selectGroup;
        public Image autoButtonBack;
        public TextMeshProUGUI autoButtonText;
        public Image[] tableButtonBacks;         // 8
        public TextMeshProUGUI[] tableButtonTexts;
        public Color buttonOn = new Color(0.91f, 0.72f, 0.29f, 1f);
        public Color buttonOff = new Color(0.12f, 0.17f, 0.30f, 1f);
        public Color buttonDisabled = new Color(0.20f, 0.20f, 0.22f, 1f);
        public Color textOn = new Color(0.08f, 0.08f, 0.10f, 1f);
        public Color textOff = Color.white;
        public Color textDisabled = new Color(0.45f, 0.45f, 0.48f, 1f);

        public Color rowBackNormal = new Color(0.07f, 0.11f, 0.22f, 0.92f);
        public Color rowBackTurn = new Color(0.16f, 0.25f, 0.48f, 0.95f);
        public Color seatTextNormal = Color.white;
        public Color seatTextTurn = new Color(1.0f, 0.84f, 0.35f, 1f);

        // ---- 第2段：カメラ ----
        public Camera spectatorCamera;
        [Tooltip("カメラ映像（RawImage と枠）。カメラ表示の時だけ出す")]
        public GameObject cameraViewRoot;
        [Tooltip("盤面図（フェルト・中央の箱・河）。盤面図表示の時だけ出す")]
        public GameObject diagramRoot;
        public Image viewButtonBack;
        public TextMeshProUGUI viewButtonText;
        public int cameraFpsPc = 30;
        public int cameraFpsQuest = 15;
        // 表示の既定（0=中継 1=解説・カメラ 2=解説・盤面図）
        public int viewModeDefaultPc = 0;
        public int viewModeDefaultQuest = 0;

        // ---- 中継表示（Mリーグ式）----
        public GameObject analysisRoot;          // 解説表示の部品一式（上部の帯・盤面・行）
        public GameObject broadcastRoot;         // 中継表示の部品一式
        public RawImage broadcastImage;          // 全画面の映像
        public RenderTexture squareTexture;      // 解説表示のカメラ（正方形）
        public RenderTexture wideTexture;        // 中継表示のカメラ（16:9・PC）
        public RenderTexture wideTextureQuest;   // 中継表示のカメラ（16:9・Quest は小さめ）
        public TextMeshProUGUI bcRoundText;
        public TextMeshProUGUI bcSubText;
        public TextMeshProUGUI bcTableText;
        public GameObject[] bcCards;             // 下の帯（行0..3＝東南西北）
        public RawImage[] bcPortraits;
        public TextMeshProUGUI[] bcWindTexts;
        public TextMeshProUGUI[] bcNameTexts;
        public TextMeshProUGUI[] bcPointTexts;
        public GameObject[] bcRiichi;
        public GameObject[] bcTurnBars;
        public TextMeshProUGUI[] bcDeltaTexts;
        public GameObject[] bcWaitRoots;
        public RawImage[] bcWaitTiles;
        public TextMeshProUGUI[] bcWaitRemain;
        public TextMeshProUGUI[] bcWaitLabels;
        public float bcPortraitHeight = 104f;
        // 手元カメラ（席基準：中心からの距離・本人の左へ・卓面からの高さ／注視点の距離・右へ・画角）
        // M リーグの手元カメラの1コマ（2026-10-05 ユーザー提供の静止画）から逆算した値（手牌の両端の上下・卓中央の5点を、
        // こちらの牌の実寸で最小二乗に合わせた。誤差は画面の0.2%）。本人の左25cm・卓面から12.8cmから、
        // 卓の中心方向より右へ約39°振って約22°見下ろす。縦の画角43.3°。
        // ※河はこの卓だと中心寄り（0.10m）で参考より小さく写る。河の見やすさのために高さを上げる案は不採用（ユーザー判断）
        public float handRadial = 0.568f;
        public float handLeft = 0.248f;
        public float handHeight = 0.128f;
        public float handLookRadial = 0.317f;
        public float handLookRight = -0.043f;
        public float handFov = 43.3f;
        [Tooltip("手元カメラのニアクリップ。席の人の体のすぐ前に置くので、レンズの直前に被る体の一部を描かない")]
        public float handNearClip = 0.12f;
        [Tooltip("打牌の余韻（秒）。手番が移っても、この間は捨てた人の手元を写したままにする（捨てた後の手牌を見せる）")]
        public float discardLinger = 1.0f;
        [Tooltip("同じ人の手元を最低何秒保つか（CPU の速打ちが続いても画面がちらつかない）")]
        public float handMinHold = 2.5f;
        [Tooltip("手元を写してから顔へ切り替えるまでの秒数（人間の手番）。これより速く打てば顔は入らない。以後は約8.5秒ごと")]
        public float handBeforeFace = 5f;
        [Tooltip("誰かが立直している間の handBeforeFace（秒）。立直中は普段より早めに顔を入れる")]
        public float handBeforeFaceRiichi = 1.5f;
        public Vector2 condPosAnalysis = new Vector2(-430f, -435f);
        public Vector2 condPosBroadcast = new Vector2(190f, 360f);
        public Color deltaPlus = new Color(0.45f, 1.0f, 0.55f, 1f);
        public Color deltaMinus = new Color(1.0f, 0.45f, 0.40f, 1f);
        // 点数移動（Mリーグの中継を写した順）：名前と風が消える → 和了者の増分 → 払った人の減分
        // → 増減が消えて名前が戻り、点数が勢いよく回って最後はゆっくり止まる（指数の減速）
        public CanvasGroup[] bcLabelGroups;      // 行ごとの名前・風（増減を出している間は消す）
        public TextMeshProUGUI bcKyotakuText;    // 左上：供託棒の数（bcSubText は本場の数になる）
        public TextMeshProUGUI bcWallText;       // 左上：残り山
        // 帯の色＝その人が選んだ和了演出（雷）の色。PlayerData(kmj_agari_fx) を各自が読む＝同期ゼロ。CPU は既定の金雷
        public RawImage[] bcPlates;              // 点数板
        public RawImage[] bcPortraitBgs;         // 顔の台
        public float bcPlateBrightness = 0.85f;  // 白い数字が潰れないように少し落とす

        // ---- 立直の演出（Mリーグ式：帯を消した引きの画に千点棒 → グリッチで REACH）----
        public GameObject riichiFxRoot;
        public RectTransform riichiFxStick;
        public CanvasGroup riichiFxStickGroup;
        public GameObject riichiFxBlock;
        public TextMeshProUGUI riichiFxText;
        public TextMeshProUGUI riichiFxGhost;
        public RectTransform[] riichiFxLines;
        public CanvasGroup bcHud;                // 中継表示の帯・左上・右上（立直の演出中は消す）
        public float riichiFxDuration = 3.2f;
        [Tooltip("立直のカメラ：斜めの顔で止めておく秒数（千点棒が出る間）と、全景へ引き切るまでの秒数（REACH のグリッチに合わせる）")]
        public float riichiPullHold = 0.95f;
        public float riichiPullTime = 0.5f;
        public Color riichiGhostColor = new Color(0.40f, 0.95f, 0.75f, 0.45f);  // グリッチの残像
        public float bcLabelFade = 0.35f;
        // 点数移動（卓の精算パネルが出た瞬間を 0 秒）：＋を出す → 1 秒後に−を出す → ＋と−を並べて 2 秒見せてから（3 秒目から）1 秒で点数へ数え込む
        public float bcPlusAt = 0f;
        public float bcMinusAt = 1.0f;
        public float bcCountAt = 3.0f;
        public float bcCountTau = 0.19f;
        public float bcCountSnap = 1.0f;
        public float bcLabelBackDelay = 0.7f;     // 数え始めから名前が戻り始めるまで
        public TextMeshProUGUI[] bcDeltaSubTexts; // 増減の上段（供託・立直棒。例：+1,000 の下に +12,000）
        public float bcDeltaY = 26f;
        public float bcDeltaSubY = 68f;
        // 待ち牌の細帯（参考動画「待ち牌UI」）：帯の上端・右寄せに小さな牌 → 縦線 → 山に残っている枚数
        public RectTransform[] bcWaitBgs;
        public float bcWaitStripW = 300f;
        public float bcWaitStripH = 50f;
        public float bcWaitTileW = 27f;
        public float bcWaitTileH = 40f;
        public float bcWaitTileStep = 31f;
        public CanvasGroup bcBandGroup;           // 下の4人の帯（和了者パネルの間は消す）
        // 数え終わったら帯を消し、和了者 1 人のパネル＋役の一覧を出す（参考動画「点数移動UI」）
        public GameObject winRoot;
        public CanvasGroup winGroup;
        public RawImage winPortraitBg;
        public RawImage winPortrait;
        public RawImage winPlate;
        public TextMeshProUGUI winWind;
        public TextMeshProUGUI winName;
        public TextMeshProUGUI winPoints;
        public CanvasGroup winYakuGroup;
        public TextMeshProUGUI winYakuText;
        public TextMeshProUGUI winTotalLabel;
        public TextMeshProUGUI winTotalText;

        // ---- 第3段：カットイン ----
        public GameObject cutInRoot;
        public CanvasGroup cutInGroup;
        public RectTransform cutInBand;
        public RectTransform cutInContent;
        public RawImage cutInPortrait;
        public TextMeshProUGUI cutInText;
        public RawImage cutInGlyph;
        public TextMeshProUGUI cutInName;
        public float cutInPortraitHeight = 420f;
        public float cutInGlyphHeight = 230f;
        public Texture[] charaPortraits;
        public Texture defaultPortrait;
        public SelfiePortraitStore selfieStore;
        public Texture ronGlyph;
        public Texture tsumoGlyph;

        // ---- 小さな札（鳴き・北抜き・和了・流局）。大きな帯のカットインは立直系だけ ----
        public GameObject callBadgeRoot;
        public RectTransform callBadgeRect;
        public CanvasGroup callBadgeGroup;
        public TextMeshProUGUI callBadgeText;
        // 札を出す位置：中継表示＝その人の帯の上／解説表示＝その人の行の右上（Canvasピクセル）
        public float badgeBroadcastY = -378f;
        public float badgeCardX0 = -690f;
        public float badgeCardStep = 460f;
        public Vector2 badgeAnalysisRow0 = new Vector2(820f, 410f);
        public float badgeAnalysisRowStep = 252f;

        // ---- 第3段：待ち牌・トップ差・オーラスの条件 ----
        public GameObject[] waitRoots;           // 行ごと
        public RawImage[] waitTiles;             // 行 * WAIT_MAX + k
        public TextMeshProUGUI[] waitRemainTexts;
        public TextMeshProUGUI[] waitLabelTexts;
        public float waitTileW = 26f;
        public float waitTileH = 39f;
        public float waitTileLeft = 84f;
        public float waitTileStep = 28f;
        public float waitBaseY = 128f;
        public TextMeshProUGUI[] rowDiffTexts;
        public GameObject conditionRoot;
        public TextMeshProUGUI conditionText;

        // ---- UIのアニメーション（すべて一度きり。常時ループは置かない＝Canvas の組み直しを短い間に閉じ込める）----
        public CanvasGroup[] bcCardGroups;       // 下の4人の帯（登場のフェード）
        public CanvasGroup[] bcWaitGroups;       // 待ち牌の細帯（中継表示）
        public CanvasGroup[] waitGroups;         // 待ち牌の行（解説表示）
        public CanvasGroup conditionGroup;       // オーラスの逆転条件
        [Tooltip("待ち牌の帯が出る時間（秒）と右からずれてくる幅")]
        public float animWaitIn = 0.25f;
        public float animWaitSlide = 40f;
        [Tooltip("待ちの牌が1枚ずつ出る時間（秒）と1枚ごとの遅れ")]
        public float animTilePop = 0.18f;
        public float animTileStagger = 0.04f;
        [Tooltip("残り0枚になった牌が光ってから暗くなるまで（秒）")]
        public float animZeroFlash = 0.35f;
        [Tooltip("残り枚数の数字が回る時間（秒）")]
        public float animRemainRoll = 0.3f;
        [Tooltip("手番の下線が伸びる／縮む時間（秒）")]
        public float animTurnBar = 0.15f;
        [Tooltip("立直棒が出る時間（秒）。立直の演出中は演出が終わってから出す")]
        public float animRiichiPop = 0.25f;
        [Tooltip("局・本場の表示が入れ替わる時間（秒）と右からずれてくる幅")]
        public float animRound = 0.3f;
        public float animRoundSlide = 24f;
        [Tooltip("卓の切替・対局の開始で4人の帯が下から出る時間（秒）・1人ごとの遅れ・せり上がる高さ")]
        public float animEnter = 0.35f;
        public float animEnterStagger = 0.06f;
        public float animEnterRise = 50f;
        [Tooltip("オーラスの逆転条件が横から出る時間（秒）と幅")]
        public float animCond = 0.3f;
        public float animCondSlide = 60f;
        [Tooltip("局の頭の通知（連荘・本場・供託・トビ寸前）を上の枠に出す秒数。オーラスは逆転条件が優先で出さない")]
        public float noticeSeconds = 5f;
        [Tooltip("この点数未満になった人をトビ寸前として通知する（トビなしの卓では出さない）")]
        public int bustWarnPoints = 5000;
        [Tooltip("連荘でなくても、この本場以上なら通知する")]
        public int noticeHonbaFrom = 2;
        [Tooltip("供託がこの点数以上なら通知する")]
        public int noticeKyotakuFrom = 2000;

        // ---- 役満通知（「◯卓 役満確定／役満チャンス」）。全卓ぶん。中継・解説どちらの表示でも上に出す ----
        // 判定は各卓の Owner（Mahjong.AdvanceSpectatorYakumanReport）・中継は MatchmakingManager の同期値
        [Tooltip("役満確定／役満チャンスの通知を出す秒数")]
        public float yakumanAlertSeconds = 6f;
        public GameObject yakumanAlertRoot;
        public CanvasGroup yakumanAlertGroup;
        public Image yakumanAlertBack;
        public TextMeshProUGUI yakumanAlertTitle;
        public TextMeshProUGUI yakumanAlertBody;
        public Color yakumanKakuteiColor = new Color(0.50f, 0.03f, 0.05f, 0.95f);
        public Color yakumanChanceColor = new Color(0.07f, 0.11f, 0.26f, 0.95f);

        // ---- 録画モード（検証版）：配信者が OBS でストリームカメラを録るための差し替え ----
        // 手持ちカメラ（VRCCameraSettings.PhotoCamera）のレンズの直前に中継映像の板を置く。
        // 板は recordLayer に置き、自分の視界（ScreenCamera）からそのレイヤーを外す＝本人には見えず、
        // ストリームカメラの出力だけが中継になる。押した本人の端末だけで動く（同期ゼロ）。
        public Transform recordPlate;
        public int recordLayer = 26;
        [Tooltip("板をレンズから何 m 前に置くか（手持ちカメラのニアクリップより奥）")]
        public float recordPlateDistance = 0.05f;
        [Tooltip("板を画角より少し大きくして縁の隙間を消す倍率")]
        public float recordPlateOverscan = 1.05f;
        [Tooltip("VRChat の不具合の補正：VRCCameraSettings.PhotoCamera.FieldOfView は実際より大きく返る"
               + "（本当は約0.77〜0.795倍・2025-07時点で未修正）。VRChat が直したら 1 に戻すこと")]
        public float recordFovScale = 0.79f;
        public Image recordButtonBack;
        public TextMeshProUGUI recordButtonText;
        [Tooltip("検証用：カメラの状態を操作帯の下に出す")]
        public TextMeshProUGUI recordDebugText;
        [Tooltip("検証用の1行を録画中に出すか（普段は OFF）")]
        public bool recordDebug = false;
        // モニターの中継画面（映像＋帯・カットイン・役満通知）を正面から撮って recordTexture へ描く。板はこれを映す
        public Camera recordCamera;
        public RenderTexture recordTexture;
        [Tooltip("録画中だけ中継カメラの描き先にする高解像度の RT（PC）")]
        public RenderTexture wideTextureRecord;
        [Tooltip("録画中の中継カメラの fps（PC）")]
        public int recordFps = 60;

        // ---- 終局の演出（GAME SET の帯 → 4位から順に最終結果）。中継表示だけ ----
        public GameObject finalRoot;
        public CanvasGroup finalGroup;
        public TextMeshProUGUI finalTitle;
        public CanvasGroup[] finalRowGroups;     // 行 0＝1位
        public RectTransform[] finalRowRts;
        public TextMeshProUGUI[] finalRankTexts;
        public TextMeshProUGUI[] finalNameTexts;
        public TextMeshProUGUI[] finalPointTexts;
        public TextMeshProUGUI[] finalDiffTexts;
        public RawImage[] finalPortraits;
        public RawImage[] finalPortraitBgs;
        public RawImage[] finalPlates;
        public GameObject[] finalTopBadges;
        [Tooltip("GAME SET の帯の長さ（秒）・結果の1行ごとの間隔・行がせり上がる時間と高さ・結果を出しておく秒数")]
        public float finalCutDur = 1.8f;
        public float finalRowInterval = 0.55f;
        public float finalRowDur = 0.35f;
        public float finalRowRise = 40f;
        public float finalHold = 14f;

        // ---- 第3段：音（配信卓の宣言ボイスをモニターの位置で鳴らし直す）----
        public AudioSource monitorVoice;
        // 立直 BGM の中継（ループ）。monitorVoice と同じ親（monitorAudioRoot）の下
        public AudioSource monitorBgm;
        // 音源の親。録画中は配信者の頭の位置へ付いて回り、距離に関係なく一定の大きさで鳴らす
        public Transform monitorAudioRoot;
        [Tooltip("卓の効果音（打牌音以外）を鳴らし直す倍率")]
        public float sfxGain = 0.8f;
        [Tooltip("打牌音を鳴らし直す倍率（数が多いので控えめ）")]
        public float discardGain = 0.5f;
        [Tooltip("立直 BGM を鳴らし直す倍率")]
        public float bgmGain = 1.0f;
        public float voiceGain = 1.0f;
        [Tooltip("配信卓にこの距離まで近づいている人には鳴らし直さない（卓の声と二重になる）")]
        public float voiceSkipNearTable = 4.0f;

        public const int RIVER_MAX = 32;
        public const int ROW_MAX = 20;
        public const int WAIT_MAX = 13;   // 国士無双十三面待ちまで並べる
        private const int NUM_TABLES = 8;

        // 副露の種類と向き（Mahjong.cs の EATED_SET_* と同じ値。あちらを変えたらここも直す）
        private const int EATED_SET_TYPE_ANKAN = 2;
        private const int EATED_SET_TYPE_MINKAN = 3;
        private const int EATED_SET_TYPE_KAKAN = 4;
        private const int EATED_SET_DIRECTION_LEFT = 1;
        private const int EATED_SET_DIRECTION_CENTER = 2;
        private const int EATED_SET_DIRECTION_RIGHT = 3;

        // 終局してから次の卓へ移るまで（結果をしばらく映す）
        private const float FINISHED_HOLD_SECONDS = 20.0f;
        private const float AUTO_CHECK_INTERVAL = 1.0f;
        private const float RANGE_CHECK_INTERVAL = 0.5f;

        // ---- 状態（すべてローカル）----
        private bool spectating_ = false;
        private bool inRoom_ = false;
        private float nextRoomCheck_ = 0f;
        private int lastLang_ = -1;
        private float nextLangCheck_ = 0f;
        private bool autoMode_ = true;
        private int manualTable_ = -1;
        private int watchedIndex_ = -1;
        private Mahjong watched_;
        private bool inRange_ = false;
        private float nextAutoCheck_ = 0f;
        private float nextRangeCheck_ = 0f;
        private float finishedSeenTime_ = -1f;
        private int lastRevision_ = 0;
        private int lastPointSig_ = 0;
        private int lastStripSig_ = 0;
        private int lastIdleKind_ = -1;

        // 卓の gameState（Mahjong.cs の GAMESTATE_* と同じ値）
        private const int GS_EXHAUSTIVE_DRAW = 8;
        private const int GS_TSUMO = 13;
        private const int GS_RON = 14;
        private const int EATED_SET_TYPE_PON = 1;
        private const int EATED_SET_TYPE_CHI_PREV = 5;
        private const int PEY_KEY = (3 << 4) | 4;   // 北の正規化キー

        // カットインの種類
        private const int CUT_RIICHI = 1;
        private const int CUT_PON = 2;
        private const int CUT_CHI = 3;
        private const int CUT_KAN = 4;
        private const int CUT_KITA = 5;
        private const int CUT_TSUMO = 6;
        private const int CUT_RON = 7;
        private const int CUT_DRAW = 8;
        private const int CUT_GAMESET = 9;
        private const int GS_RESULT = 18;
        private const int GS_SETTLEMENT = 19;

        // カメラのショット
        private const int SHOT_OVERVIEW = 0;
        private const int SHOT_SHOULDER = 1;
        private const int SHOT_CLOSE = 2;
        private const int SHOT_AGARI = 3;

        // 出来事の検出（直前の状態との差分。表示を切り替えた直後は記録だけして鳴らさない）
        private bool snapValid_ = false;
        private int snapState_ = 0;
        private int[] snapReach_ = new int[4];
        private int[] snapMelds_ = new int[4];
        private int[] snapKakan_ = new int[4];
        private int[] snapKita_ = new int[4];
        // カンドラ：めくれているドラ表示牌の枚数の控えと、まだ写していない新ドラ（表示牌の添字・-1＝なし）
        private int snapDora_ = 0;
        private int doraShotPending_ = -1;

        // カットイン
        private float cutInStart_ = -100f;
        private float cutInDur_ = 0f;
        private bool cutInActive_ = false;
        private float badgeStart_ = -100f;
        private float badgeDur_ = 0f;
        private bool badgeActive_ = false;

        // カメラ
        private bool cameraMode_ = true;
        private int viewMode_ = 0;

        // 中継表示のカメラ運び
        private const int SHOT_HAND = 4;
        private const int SHOT_WIDE = 5;
        private int bcTurnSeat_ = -99;
        private float bcTurnSince_ = 0f;
        private bool bcFaceDone_ = false;
        private int bcHandSeat_ = -1;
        private float bcHandSince_ = 0f;
        private int bcShotSeat_ = -1;

        // 中継の演出順（ショットの列。局の開始・手番の頭・宣言・和了で積み、順に流す）
        private const int SHOT_DORA = 6;
        private const int SHOT_MELD = 7;   // 晒した牌（鳴き・北抜き）の真上アップ
        private const int SHOT_RIICHI_PULL = 8;   // 立直：宣言者の斜めの顔から全景へ一気に引く
        private const int FACE_FRONT = 0;
        private const int FACE_SHOULDER = 1;
        private const int FACE_SIDE = 2;
        private const int FACE_PUNCH = 3;
        private const int FACE_ACROSS = 4;   // 対面の背中越しに本人（卓と両脇も入る引きの画）
        private const int GS_DEALING = 20;
        private const int SEQ_MAX = 8;
        private int[] seqShot_ = new int[SEQ_MAX];
        private int[] seqSeat_ = new int[SEQ_MAX];
        private int[] seqVar_ = new int[SEQ_MAX];
        private float[] seqDur_ = new float[SEQ_MAX];
        private int seqCount_ = 0;
        private int seqIndex_ = 0;
        private float seqStart_ = 0f;
        private int seqCurShot_ = 0;
        private int seqCurSeat_ = -1;
        private int seqCurVar_ = 0;
        private int faceVariant_ = 0;
        private int bcThinkFaces_ = 0;
        // 手番の人が最後に牌へ手を掛けていた時刻（Mahjong.Spectate_HandIntent）。外してすぐ顔へ飛ばない余白に使う。
        private float bcIntentLast_ = -100.0f;
        private const float INTENT_FACE_GAP = 2.0f;
        private bool bcSeqTurnFace_ = false;   // 今の演出順が手番の顔（打牌したら打ち切る）。宣言・和了の列は false
        private float bcThinkFrom_ = 0f;   // 「手元を handBeforeFace 秒写したら顔」の起点（手番が移った時刻＋余韻）
        private int curVar_ = 0;

        // 中継表示の点数の増減
        private int[] bcLastPts_ = new int[4];
        private bool bcPtsInit_ = false;
        private int[] bcFromPts_ = new int[4];
        private int[] bcCurPts_ = new int[4];    // 行ごと：今回読んだ卓の実値
        private int[] bcPendRiichi_ = new int[4]; // 行ごと：立直棒で減ったが帯にはまだ出していない点（Mリーグは精算まで動かさない）
        private int[] bcMainDelta_ = new int[4];  // 行ごと：増減の下段（和了・支払い）
        private int[] bcSubDelta_ = new int[4];   // 行ごと：増減の上段（供託・立直棒）
        private int bcPrevKyotaku_ = 0;
        private bool winPending_ = false;          // 和了者のパネル＋役を出している（卓のあがり画面と同時）
        private float winBase_ = 0f;               // パネルを出し始めた時刻
        private float winReleaseAt_ = -1f;         // パネルを畳み始めた時刻（-1＝出したまま）
        private int winSeat_ = -1;                 // パネルに出している和了者の席
        private int winSerial_ = -1;               // パネルに出しているあがり画面の中身の版（ダブロンの切替を拾う）
        private int[] bcShownPts_ = new int[4];  // 行ごと：いま帯に出している値
        private bool bcAnimActive_ = false;
        private float bcAnimStart_ = 0f;
        private float bcAnimCountAt_ = 0f;
        private float bcAnimSnap_ = 1.0f;     // 数え終わるまでの秒数
        private float bcAnimTau_ = 0.19f;
        private float bcAnimPlusAt_ = 0f;
        private float bcAnimMinusAt_ = 1.0f;
        private float bcAnimSink_ = 0.6f;     // 増減が点数へ沈みきるまでの秒数
        private bool bcAgariThisRound_ = false;  // 和了の後の点数移動だけ顔の演出順を積む
        private float bcScoreSeqEnd_ = 0f;       // 点数移動の顔の列が終わる時刻（局の開始の全景はその後）
        private bool bcRoundOpenPending_ = false;
        private float bcRoundOpenQueuedAt_ = -99f;
        private bool riichiFxActive_ = false;
        private float riichiFxStart_ = 0f;
        private float riichiTextY_ = -110f;
        private int eventShot_ = -1;
        private int eventSeat_ = -1;
        private float eventShotUntil_ = 0f;
        private int agariSeat_ = -1;
        private int curShot_ = -1;
        private int curSeat_ = -1;
        private float curShotStart_ = 0f;
        private float nextCamRender_ = 0f;
        private float orbitYaw_ = 0f;

        // 音
        private AudioClip[] voiceClip_ = new AudioClip[4];
        private float[] voiceTime_ = new float[4];
        private bool voiceArmed_ = false;
        private int sfxSeen_ = -1;          // 鳴らし直した効果音の通し番号（-1＝次の読み取りは記録だけ）
        private bool bgmPaused_ = false;

        // 残り枚数の集計（正規化キー＝種類<<4|数字。赤5は5に寄せる）
        private int[] seenCount_ = new int[64];

        // オーラスの条件計算
        private int[] condPts_ = new int[4];
        private int[] RON_CHILD = new int[] { 1000, 2000, 3900, 7700, 8000, 12000, 16000, 24000, 32000 };
        private int[] RON_DEALER = new int[] { 1500, 2900, 5800, 11600, 12000, 18000, 24000, 36000, 48000 };
        private int[] TSUMO_CHILD_C = new int[] { 300, 500, 1000, 2000, 2000, 3000, 4000, 6000, 8000 };
        private int[] TSUMO_CHILD_D = new int[] { 500, 1000, 2000, 3900, 4000, 6000, 8000, 12000, 16000 };
        private int[] TSUMO_DEALER = new int[] { 500, 1000, 2000, 3900, 4000, 6000, 8000, 12000, 16000 };

        private void Start()
        {
#if UNITY_ANDROID
            viewMode_ = viewModeDefaultQuest;
#else
            viewMode_ = viewModeDefaultPc;
#endif
            if (viewMode_ < 0 || viewMode_ > 2) { viewMode_ = 0; }
            cameraMode_ = viewMode_ != 2;
            StopSpectatorCamera();
            if (cutInRoot != null) { cutInRoot.SetActive(false); }
            InitUiAnim();
            ApplyLanguageTexts();
            ApplyMode();
            // 観戦ルームの外では切替ボタンを出さない（入ったら UpdateRoom が出す）
            if (voiceRouter != null && modeButtonBack != null) { modeButtonBack.gameObject.SetActive(false); }
        }

        // ============================================================
        // 言語（日本語と英語は混ぜない。KemonoLanguage の現在値だけで出し分ける）
        // 文言は「ja ? 日本語 : ko_ ? 韓国語 : zhs_ ? 簡体字 : zht_ ? 繁体字 : 英語」。
        // ko_ / zhs_ / zht_ は言語が変わった時に ApplyLanguageTexts が更新する。
        // ============================================================
        private bool IsJa()
        {
            return language == null || language.GetLanguage() == KemonoLanguage.LANG_JA;
        }

        private int CurLang()
        {
            return language == null ? KemonoLanguage.LANG_JA : language.GetLanguage();
        }

        // 韓国語・簡体字・繁体字か（ApplyLanguageTexts の時点の値）。毎回マネージャへ聞きに行かない。
        private bool ko_ = false;
        private bool zhs_ = false;
        private bool zht_ = false;

        // KemonoLanguage の extraTargets に入れた場合の受け口（入れなくても Update のポーリングで追随する）
        public void ApplyLanguageJa() { CheckLanguage(); }
        public void ApplyLanguageEn() { CheckLanguage(); }
        public void ApplyLanguageKo() { CheckLanguage(); }
        public void ApplyLanguageZhs() { CheckLanguage(); }
        public void ApplyLanguageZht() { CheckLanguage(); }

        private void CheckLanguage()
        {
            int lang = CurLang();
            if (lang == lastLang_) { return; }
            ApplyLanguageTexts();
            ApplyMode();   // ForceRedraw も兼ねる（盤面・案内文も新しい言語で描き直す）
        }

        // 操作帯の固定ラベル。言語が変わった時だけ書き換える。
        private void ApplyLanguageTexts()
        {
            lastLang_ = CurLang();
            bool ja = lastLang_ == KemonoLanguage.LANG_JA;
            ko_ = lastLang_ == KemonoLanguage.LANG_KO;
            zhs_ = lastLang_ == KemonoLanguage.LANG_ZHS;
            zht_ = lastLang_ == KemonoLanguage.LANG_ZHT;
            if (autoButtonText != null) { autoButtonText.text = ja ? "自動" : ko_ ? "자동" : zhs_ ? "自动" : zht_ ? "自動" : "Auto"; }
            ApplyViewButton();
            if (tableButtonTexts != null)
            {
                for (int t = 0; t < tableButtonTexts.Length; ++t)
                {
                    if (tableButtonTexts[t] == null) { continue; }
                    tableButtonTexts[t].text = ja ? ("卓" + (t + 1)) : ko_ ? ("탁" + (t + 1)) : zhs_ ? ("桌" + (t + 1)) : zht_ ? ("桌" + (t + 1)) : ("#" + (t + 1));
                }
            }
        }

        // ============================================================
        // カリングからの問い合わせ
        // ============================================================
        public bool IsKeepingTableAwake(Mahjong tb)
        {
            return spectating_ && inRange_ && tb != null && watched_ == tb;
        }

        // ============================================================
        // ボタン（操作帯の Button.onClick → SendCustomEvent）
        // ============================================================
        public void OnToggleMode()
        {
            // 観戦は観戦ルームの中だけ（ボタンは隠してあるが、念のため強制もここで弾く）
            if (!spectating_ && voiceRouter != null && !inRoom_) { return; }
            spectating_ = !spectating_;
            if (spectating_)
            {
                bcEnterPending_ = true;
                // 入ったらすぐ選ぶ（自動の初回判定を待たせない）
                nextAutoCheck_ = 0f;
                nextRangeCheck_ = 0f;
                UpdateRange();
                UpdateSelection();
            }
            ApplyMode();
        }

        // 観戦ルーム（TableVoiceRouter が持つ Carpet_01 の箱）の出入り。
        // 入ったら毎回観戦 ON（中で自分で OFF にしたら、いる間は OFF のまま）。
        // 出たら強制 OFF にして切替ボタンを隠す（外の人には同じ画面が普通の動画として見える）。
        private void UpdateRoom()
        {
            if (voiceRouter == null) { return; }
            bool r = voiceRouter.IsLocalInRoom();
            if (r == inRoom_) { return; }
            inRoom_ = r;
            if (modeButtonBack != null && modeButtonBack.gameObject.activeSelf != r) { modeButtonBack.gameObject.SetActive(r); }
            if (r && !spectating_) { OnToggleMode(); }
            else if (!r && spectating_) { OnToggleMode(); }
        }


        // ============================================================
        // 録画モード（検証版）
        //  ・「録画」ボタンで ON/OFF（観戦中だけ）。ON の間、手持ちカメラが開いていれば
        //    毎フレーム（PostLateUpdate）レンズの位置・向き・画角を読んで、直前に中継映像の板を置く
        //  ・自分の視界からは recordLayer を外す（OFF で元に戻す）。VRChat 側が戻すことに備えて毎フレーム確かめる
        //  ・検証用に、カメラの状態と位置の更新頻度を操作帯の下へ出す
        // ============================================================
        private bool recordOn_ = false;
        private int recordSavedView_ = 0;
        private bool recordMaskApplied_ = false;
        private bool recordNearSaved_ = false;
        private float recordNearOrig_ = 0.05f;
        private Vector3 recordLastPos_;
        private Quaternion recordLastRot_;
        private int recordFrames_ = 0;
        private int recordPosChanges_ = 0;
        private int recordRotChanges_ = 0;
        private float recordStatAt_ = 0f;
        private int recordMaskReapplied_ = 0;

        public void OnToggleRecord()
        {
            SetRecord(!recordOn_);
        }

        private void SetRecord(bool on)
        {
#if UNITY_ANDROID
            on = false;   // Quest にはストリームカメラの配信が無い（ボタンも出さない）
#endif
            bool was = recordOn_;
            recordOn_ = on && spectating_;
            if (recordOn_ && !was)
            {
                // 録画中は中継表示に固定する（止めたら元の表示へ戻す）
                recordSavedView_ = viewMode_;
                if (viewMode_ != 0) { viewMode_ = 0; cameraMode_ = true; bcEnterPending_ = true; }
                ApplyView();
                ForceRedraw();
            }
            else if (!recordOn_ && was)
            {
                if (recordCamera != null) { recordCamera.enabled = false; }
                if (monitorAudioRoot != null) { monitorAudioRoot.localPosition = Vector3.zero; }
                if (recordSavedView_ != 0 && spectating_)
                {
                    viewMode_ = recordSavedView_;
                    cameraMode_ = viewMode_ != 2;
                }
                ApplyView();
                ForceRedraw();
            }
            if (recordOn_)
            {
                recordFrames_ = 0;
                recordPosChanges_ = 0;
                recordRotChanges_ = 0;
                recordMaskReapplied_ = 0;
                recordStatAt_ = Time.time;
            }
            else
            {
                if (recordPlate != null && recordPlate.gameObject.activeSelf) { recordPlate.gameObject.SetActive(false); }
                RestoreRecordCamera();
            }
            bool dbg = recordOn_ && recordDebug;
            if (recordDebugText != null && recordDebugText.gameObject.activeSelf != dbg) { recordDebugText.gameObject.SetActive(dbg); }
            ApplyRecordButton();
        }

        private void ApplyRecordButton()
        {
#if UNITY_ANDROID
            if (recordButtonBack != null && recordButtonBack.gameObject.activeSelf) { recordButtonBack.gameObject.SetActive(false); }
            return;
#else
            bool ja = IsJa();
            if (recordButtonText != null)
            {
                recordButtonText.text = recordOn_ ? (ja ? "録画中" : ko_ ? "녹화 중" : zhs_ ? "录制中" : zht_ ? "錄製中" : "REC ON") : (ja ? "録画" : ko_ ? "녹화" : zhs_ ? "录制" : zht_ ? "錄製" : "REC");
                recordButtonText.color = recordOn_ ? textOn : textOff;
            }
            if (recordButtonBack != null) { recordButtonBack.color = recordOn_ ? buttonOn : buttonOff; }
#endif
        }

        // 自分の視界から録画用レイヤーを外す（すでに外れていれば何もしない）
        private void ApplyRecordMask()
        {
            VRCCameraSettings sc = VRCCameraSettings.ScreenCamera;
            if (sc == null) { return; }
            int bit = 1 << recordLayer;
            LayerMask m = sc.CullingMask;
            int v = m.value;
            if ((v & bit) == 0) { return; }
            if (recordMaskApplied_) { ++recordMaskReapplied_; }
            m.value = v & ~bit;
            sc.CullingMask = m;
            recordMaskApplied_ = true;
        }

        private void RestoreRecordCamera()
        {
            if (recordMaskApplied_)
            {
                VRCCameraSettings sc = VRCCameraSettings.ScreenCamera;
                if (sc != null)
                {
                    LayerMask m = sc.CullingMask;
                    m.value = m.value | (1 << recordLayer);
                    sc.CullingMask = m;
                }
                recordMaskApplied_ = false;
            }
            if (recordNearSaved_)
            {
                VRCCameraSettings pc = VRCCameraSettings.PhotoCamera;
                if (pc != null) { pc.NearClipPlane = recordNearOrig_; }
                recordNearSaved_ = false;
            }
        }

        public override void PostLateUpdate()
        {
            if (!recordOn_) { return; }
            UpdateRecordPlate();
        }

        private void UpdateRecordPlate()
        {
            ApplyRecordMask();
            // 録画中は音を配信者の頭の位置で鳴らす＝モニターからの距離に関係なく一定の大きさ（OBS は本人の耳の音を録る）
            if (monitorAudioRoot != null)
            {
                var lp = Networking.LocalPlayer;
                if (lp != null && Utilities.IsValid(lp))
                {
                    monitorAudioRoot.position = lp.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;
                }
            }
            VRCCameraSettings pc = VRCCameraSettings.PhotoCamera;
            bool active = pc != null && pc.Active;
            if (recordPlate != null && recordPlate.gameObject.activeSelf != active) { recordPlate.gameObject.SetActive(active); }
            if (recordCamera != null && recordCamera.enabled != active) { recordCamera.enabled = active; }

            float fov = 0f;
            float aspect = 0f;
            if (active)
            {
                Vector3 p = pc.Position;
                Quaternion q = pc.Rotation;
                fov = pc.FieldOfView;
                aspect = pc.Aspect;
                if (aspect < 0.1f) { aspect = 16f / 9f; }
                // 手持ちカメラの FOV は実際より大きく返る（VRChat の不具合）。そのままだと画面が約1.3倍に拡大されて四隅が切れる。
                // 描画中のカメラ（GetCurrentCamera）から本物を読む方法は実機で取れなかった（2026-10-07）ので補正値で合わせる
                fov = fov * recordFovScale;
                float d = recordPlateDistance;
                // 板がニアクリップで切られないよう、レンズの手前側を詰める（OFF で元に戻す）
                if (!recordNearSaved_) { recordNearOrig_ = pc.NearClipPlane; recordNearSaved_ = true; }
                if (pc.NearClipPlane > d * 0.5f) { pc.NearClipPlane = d * 0.5f; }
                float h = 2f * d * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * recordPlateOverscan;
                if (recordPlate != null)
                {
                    recordPlate.SetPositionAndRotation(p + q * Vector3.forward * d, q);
                    recordPlate.localScale = new Vector3(h * aspect, h, 1f);
                }
                ++recordFrames_;
                if (p != recordLastPos_) { ++recordPosChanges_; }
                if (q != recordLastRot_) { ++recordRotChanges_; }
                recordLastPos_ = p;
                recordLastRot_ = q;
            }

            // 検証用の表示（0.5秒ごと）
            float now = Time.time;
            if (!recordDebug || recordDebugText == null || now - recordStatAt_ < 0.5f) { return; }
            VRCCameraSettings sc = VRCCameraSettings.ScreenCamera;
            bool hidden = sc != null && (sc.CullingMask.value & (1 << recordLayer)) == 0;
            string s = "録画検証  カメラ:" + (active ? "開" : "閉");
            if (pc != null)
            {
                s += "  モード:" + (int)pc.CameraMode
                   + "  位置更新:" + recordPosChanges_ + "/" + recordFrames_
                   + "  向き更新:" + recordRotChanges_ + "/" + recordFrames_
                   + "  FOV:" + fov.ToString("F1") + "  比:" + aspect.ToString("F2")
                   + "  near:" + pc.NearClipPlane.ToString("F3")
                   + "  " + pc.PixelWidth + "x" + pc.PixelHeight;
            }
            s += "  視界から除外:" + (hidden ? "済" : "未") + "  戻され:" + recordMaskReapplied_;
            s += "  報告FOV:" + (pc != null ? pc.FieldOfView.ToString("F1") : "-");
            recordDebugText.text = s;
            recordStatAt_ = now;
            recordFrames_ = 0;
            recordPosChanges_ = 0;
            recordRotChanges_ = 0;
        }

        public void OnSelectAuto()
        {
            autoMode_ = true;
            manualTable_ = -1;
            nextAutoCheck_ = 0f;
            UpdateSelection();
            ForceRedraw();
        }

        // 表示：中継 → 解説（カメラ）→ 解説（盤面図）→ 中継（ローカル）
        public void OnToggleView()
        {
            if (recordOn_) { return; }   // 録画中は中継表示に固定
            viewMode_ = (viewMode_ + 1) % 3;
            if (viewMode_ == 0) { bcEnterPending_ = true; }
            cameraMode_ = viewMode_ != 2;
            ApplyView();
            ForceRedraw();
        }

        private void ApplyView()
        {
            bool broadcast = viewMode_ == 0;
            if (analysisRoot != null) { analysisRoot.SetActive(!broadcast); }
            if (broadcastRoot != null) { broadcastRoot.SetActive(broadcast); }
            if (cameraViewRoot != null) { cameraViewRoot.SetActive(viewMode_ == 1); }
            if (diagramRoot != null) { diagramRoot.SetActive(viewMode_ == 2); }
            // 出力先を差し替える前に見張り描画を畳む（畳まないと後で古い出力先へ戻してしまう）。
            EndKeepAlive();
            if (!cameraMode_) { StopSpectatorCamera(); }

#if UNITY_ANDROID
            RenderTexture wide = wideTextureQuest != null ? wideTextureQuest : wideTexture;
#else
            RenderTexture wide = (recordOn_ && wideTextureRecord != null) ? wideTextureRecord : wideTexture;
#endif
            if (spectatorCamera != null)
            {
                RenderTexture target = broadcast ? wide : squareTexture;
                if (target != null) { spectatorCamera.targetTexture = target; }
            }
            if (broadcastImage != null && wide != null) { broadcastImage.texture = wide; }
            if (conditionRoot != null)
            {
                conditionRoot.GetComponent<RectTransform>().anchoredPosition = broadcast ? condPosBroadcast : condPosAnalysis;
            }
            curShot_ = -1;
            ApplyViewButton();
        }

        private void ApplyViewButton()
        {
            bool ja = IsJa();
            if (viewButtonText != null)
            {
                if (viewMode_ == 0) { viewButtonText.text = ja ? "表示：中継" : ko_ ? "표시: 중계" : zhs_ ? "显示：直播" : zht_ ? "顯示：直播" : "View: Broadcast"; }
                else if (viewMode_ == 1) { viewButtonText.text = ja ? "表示：解説・カメラ" : ko_ ? "표시: 해설·카메라" : zhs_ ? "显示：解说・镜头" : zht_ ? "顯示：解說・鏡頭" : "View: Analysis+Cam"; }
                else { viewButtonText.text = ja ? "表示：解説・盤面図" : ko_ ? "표시: 해설·판면도" : zhs_ ? "显示：解说・牌面图" : zht_ ? "顯示：解說・牌面圖" : "View: Analysis+Board"; }
            }
            if (viewButtonBack != null) { viewButtonBack.color = buttonOff; }
        }

        public void OnSelectTable0() { SelectManual(0); }
        public void OnSelectTable1() { SelectManual(1); }
        public void OnSelectTable2() { SelectManual(2); }
        public void OnSelectTable3() { SelectManual(3); }
        public void OnSelectTable4() { SelectManual(4); }
        public void OnSelectTable5() { SelectManual(5); }
        public void OnSelectTable6() { SelectManual(6); }
        public void OnSelectTable7() { SelectManual(7); }

        private void SelectManual(int t)
        {
            if (matchmaking == null || t < 0 || t >= TableCount()) { return; }
            // 使われていない卓は押せない（灰色）。押されても何もしない。
            if (!matchmaking.IsTableIndexInUse(t)) { return; }
            autoMode_ = false;
            manualTable_ = t;
            SetWatched(t);
            ForceRedraw();
        }

        private void ApplyMode()
        {
            if (monitorRoot != null) { monitorRoot.SetActive(spectating_); }
            if (vvmwScreenRenderer != null) { vvmwScreenRenderer.enabled = !spectating_; }
            if (vvmwOverlay != null) { vvmwOverlay.SetActive(!spectating_); }
            if (selectGroup != null) { selectGroup.SetActive(spectating_); }
            if (!spectating_)
            {
                StopSpectatorCamera();
                HideCutIn();
                HideCallBadge();
                HideYakumanAlert();
                if (recordOn_) { SetRecord(false); }
                StopMonitorBgm();
            }
            ApplyView();
            ApplyRecordButton();

            if (modeButtonBack != null) { modeButtonBack.color = spectating_ ? buttonOn : buttonOff; }
            if (modeButtonText != null)
            {
                bool ja = IsJa();
                modeButtonText.text = spectating_
                    ? (ja ? "動画に戻す" : ko_ ? "동영상으로 돌아가기" : zhs_ ? "返回视频" : zht_ ? "返回影片" : "Back to Video")
                    : (ja ? "観戦モニター" : ko_ ? "관전 모니터" : zhs_ ? "观战屏幕" : zht_ ? "觀戰螢幕" : "Watch Matches");
                modeButtonText.color = spectating_ ? textOn : textOff;
            }
            lastStripSig_ = 0;
            ForceRedraw();
        }

        private void ForceRedraw()
        {
            lastRevision_ = 0;
            lastPointSig_ = 0;
            lastIdleKind_ = -1;
            uiArmed_ = false;
        }

        // ============================================================
        // 毎フレーム（観戦中だけ働く）
        // ============================================================
        private void Update()
        {
            float now = Time.time;
            // 観戦ルームの出入り：入ったら観戦 ON、出たら OFF（観戦していない時も追う）
            if (now >= nextRoomCheck_)
            {
                nextRoomCheck_ = now + 0.25f;
                UpdateRoom();
            }
            // 言語は観戦していない時も追う（操作帯のボタンは常に見えている）
            if (now >= nextLangCheck_)
            {
                nextLangCheck_ = now + 1.0f;
                CheckLanguage();
            }

            if (!spectating_) { return; }

            if (now >= nextRangeCheck_)
            {
                nextRangeCheck_ = now + RANGE_CHECK_INTERVAL;
                UpdateRange();
            }
            if (now >= nextAutoCheck_)
            {
                nextAutoCheck_ = now + AUTO_CHECK_INTERVAL;
                UpdateSelection();
                UpdateStrip();
            }

            RefreshMonitor();

            var tb = watched_;
            bool live = tb != null && lastIdleKind_ == 0;
            UpdateCutIn(now);
            UpdateRiichiFx(now);
            UpdateCallBadge(now);
            ServiceDoraShot(tb, live, now);
            UpdateCamera(tb, live, now);
            if (live) { RelayVoices(tb); RelaySfx(tb); }
            RelayBgm(tb, live);
            UpdateScoreAnim(now);
            StepUiAnim(now);
            UpdateAgariWinPanel(tb, live, now);
            UpdateWinPanel(now);
            StepFinal(tb, now);
            UpdateNotice(live, now);
            UpdateYakumanAlerts(now);
            // 点数移動の顔の列を流し終えてから、待たせていた局の開始（全景→ドラ→顔）を流す
            if (bcRoundOpenPending_ && now >= bcScoreSeqEnd_)
            {
                bcRoundOpenPending_ = false;
                if (live && viewMode_ == 0) { QueueRoundOpen(tb, tb.numPlayers >= 3 && tb.numPlayers <= 4 ? tb.numPlayers : 4, now); }
            }
        }

        private void UpdateRange()
        {
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp) || rangeCenter == null) { inRange_ = true; return; }
            inRange_ = Vector3.Distance(lp.GetPosition(), rangeCenter.position) <= keepAwakeRange;
        }

        private int TableCount()
        {
            if (matchmaking == null || matchmaking.tables == null) { return 0; }
            int n = matchmaking.GetTableCount();
            if (n > matchmaking.tables.Length) { n = matchmaking.tables.Length; }
            if (n > NUM_TABLES) { n = NUM_TABLES; }
            return n;
        }

        private void SetWatched(int t)
        {
            if (t == watchedIndex_) { return; }
            watchedIndex_ = t;
            watched_ = (t >= 0 && matchmaking != null && matchmaking.tables != null && t < matchmaking.tables.Length)
                ? matchmaking.tables[t] : null;
            finishedSeenTime_ = -1f;
            lastStripSig_ = 0;
            ResetEventState();
            ForceRedraw();
            bcEnterPending_ = true;
        }

        // 待ちの計算結果のキャッシュ（席ごと）。手牌の中身が変わった席だけ計算し直す
        //（待ちの計算は Udon では軽くないので、打牌のたびに4席ぶん回さない）
        private int[][] waitCache_ = new int[4][];
        private int[] waitSig_ = new int[4];

        // UIのアニメーションの状態（StepUiAnim が毎フレーム進める）
        private bool uiBaseInit_ = false;
        private bool uiArmed_ = false;           // false の間（切替直後の最初の描画）は動かさず最終形を置くだけ
        private bool bcEnterPending_ = false;    // 次の中継表示の描画で4人の帯を下から出す
        private float uiAnimEnd_ = 0f;           // ここを過ぎたら StepUiAnim は何もしない
        // 待ち牌：添字 ai = 表示（0..3＝中継の行・4..7＝解説の行）
        private float[] waitShowAt_ = new float[8];
        private float[] waitPopAt_ = new float[8];
        private int[] waitSetSig_ = new int[8];      // 出している待ちの種類の印（0＝出していない）
        private int[] waitCount_ = new int[8];
        private int[] waitRemainShown_ = new int[8];
        private int[] waitRemainFrom_ = new int[8];
        private int[] waitRemainTo_ = new int[8];
        private float[] waitRemainAt_ = new float[8];
        private float[] tileFlashAt_ = new float[8 * WAIT_MAX];
        private int[] tilePrevKey_ = new int[8 * WAIT_MAX];
        private int[] tilePrevLeft_ = new int[8 * WAIT_MAX];
        private RectTransform[] bcWaitRts_ = new RectTransform[4];
        private RectTransform[] waitRts_ = new RectTransform[4];
        private Vector2[] bcWaitBase_ = new Vector2[4];
        private Vector2[] waitBase_ = new Vector2[4];
        // 手番の下線・立直棒・帯の登場
        private bool[] turnOn_ = new bool[4];
        private float[] turnAt_ = new float[4];
        private bool[] riichiOn_ = new bool[4];
        private float[] riichiPopAt_ = new float[4];
        private RectTransform[] bcTurnRts_ = new RectTransform[4];
        private RectTransform[] bcRiichiRts_ = new RectTransform[4];
        private RectTransform[] bcCardRts_ = new RectTransform[4];
        private Vector2[] bcCardBase_ = new Vector2[4];
        private float enterAt_ = -100f;
        // 局の表示
        private string roundShown_ = "";
        private int honbaShown_ = -1;
        private float roundAt_ = -100f;
        private RectTransform roundRt_;
        private RectTransform subRt_;
        private Vector2 roundBase_;
        private Vector2 subBase_;
        private Color roundColor_ = Color.white;
        private Color subColor_ = Color.white;
        // オーラスの逆転条件
        private float condAt_ = -100f;
        private RectTransform condRt_;
        private bool condShown_ = false;
        // 局の頭の通知（上の枠を借りて noticeSeconds 秒だけ出す）
        private bool noticePending_ = false;
        private bool noticeActive_ = false;
        private string noticeText_ = "";
        private float noticeUntil_ = 0f;
        private bool dealValid_ = false;
        private int dealRound_ = 0;
        private int dealLeader_ = 0;
        private bool[] bustWarned_ = new bool[4];
        // 局表示の保持（和了・流局から次の配牌まで、左上と風は和了/流局した局のまま）
        private bool hdrHave_ = false;
        private bool hdrHold_ = false;
        private string hdrRound_ = "";
        private int hdrHonba_ = 0;
        private int hdrKyotaku_ = 0;
        private int hdrWall_ = 0;
        private string[] hdrWinds_ = new string[4];
        // 終局の演出
        private bool finalPending_ = false;
        private bool finalActive_ = false;
        private bool finalSawAnim_ = false;
        private bool finalInstant_ = false;
        private float finalDetectAt_ = 0f;
        private float finalStart_ = 0f;
        private float finalUntil_ = 0f;
        private int finalRows_ = 0;

        private void ResetEventState()
        {
            for (int i = 0; i < 4; ++i) { waitSig_[i] = 0; waitCache_[i] = null; bustWarned_[i] = false; }
            snapValid_ = false;
            doraShotPending_ = -1;
            noticePending_ = false;
            noticeActive_ = false;
            dealValid_ = false;
            voiceArmed_ = false;
            sfxSeen_ = -1;
            StopMonitorBgm();
            agariSeat_ = -1;
            eventShot_ = -1;
            curShot_ = -1;
            bcTurnSeat_ = -99;
            bcHandSeat_ = -1;
            bcThinkFaces_ = 0;
            bcThinkFrom_ = 0f;
            SeqClear();
            bcPtsInit_ = false;
            HideDeltas();
            HideCutIn();
            HideRiichiFx();
            HideCallBadge();
            HideFinal();
            hdrHave_ = false;
            hdrHold_ = false;
            ResetUiAnim();
        }

        // ============================================================
        // 配信卓の選択
        // ============================================================
        private void UpdateSelection()
        {
            if (matchmaking == null) { SetWatched(-1); return; }
            if (!autoMode_)
            {
                SetWatched(manualTable_);
                return;
            }

            int mySeated = matchmaking.GetLocalSeatedTable();
            if (IsAutoKeepable(watchedIndex_, mySeated)) { return; }
            SetWatched(ChooseAutoTable(mySeated));
        }

        // 今の卓を見続けてよいか。終局後は結果をしばらく映してから離れる。
        private bool IsAutoKeepable(int t, int mySeated)
        {
            // 終局の演出を見せ終えるまでは次の卓へ移らない
            if (t >= 0 && t == watchedIndex_ && FinalHolding(Time.time)) { return true; }
            if (t < 0 || t == mySeated) { return false; }
            if (!matchmaking.IsTableBusyIndex(t)) { return false; }
            if (matchmaking.IsTableFinishedIndex(t))
            {
                if (finishedSeenTime_ < 0f) { finishedSeenTime_ = Time.time; }
                return Time.time - finishedSeenTime_ < FINISHED_HOLD_SECONDS;
            }
            finishedSeenTime_ = -1f;
            return true;
        }

        private int ChooseAutoTable(int mySeated)
        {
            int n = TableCount();
            int best = -1;
            int bestScore = int.MinValue;
            for (int t = 0; t < n; ++t)
            {
                if (t == mySeated) { continue; }
                if (!matchmaking.IsTableBusyIndex(t) || matchmaking.IsTableFinishedIndex(t)) { continue; }
                int score = ScoreTable(t);
                if (score > bestScore) { bestScore = score; best = t; }
            }
            return best;
        }

        // 段位戦 ＞ 人間の人数 ＞ 平均段位。同点は卓番号の若い方（ループ順）。
        // 材料は全部同期済み（Manager のマスクと席ID・各自の PlayerData）なので全員が同じ卓を選ぶ。
        private int ScoreTable(int t)
        {
            int score = matchmaking.IsRankedTableIndex(t) ? 100000 : 0;
            int humans = 0;
            int lvSum = 0;
            int lvCount = 0;
            for (int s = 0; s < 4; ++s)
            {
                int id = matchmaking.GetTableSeatIdAt(t, s);
                if (id <= 0) { continue; }
                ++humans;
                if (statsStore == null) { continue; }
                var p = VRCPlayerApi.GetPlayerById(id);
                if (p == null || !Utilities.IsValid(p)) { continue; }
                if (!statsStore.HasRankDataOf(p)) { continue; }
                lvSum += statsStore.GetRankLvOf(p);
                ++lvCount;
            }
            score += humans * 1000;
            if (lvCount > 0) { score += (lvSum * 10) / lvCount; }
            return score;
        }

        // ============================================================
        // 操作帯の見た目（1秒ごと・変化時だけ）
        // ============================================================
        private void UpdateStrip()
        {
            int n = TableCount();
            int sig = 7 + (autoMode_ ? 1 : 0) * 3 + (watchedIndex_ + 2) * 17;
            for (int t = 0; t < NUM_TABLES; ++t)
            {
                bool inUse = t < n && matchmaking != null && matchmaking.IsTableIndexInUse(t);
                sig = sig * 2 + (inUse ? 1 : 0);
            }
            if (sig == lastStripSig_) { return; }
            lastStripSig_ = sig;

            if (autoButtonBack != null) { autoButtonBack.color = autoMode_ ? buttonOn : buttonOff; }
            if (autoButtonText != null) { autoButtonText.color = autoMode_ ? textOn : textOff; }
            for (int t = 0; t < NUM_TABLES; ++t)
            {
                if (tableButtonBacks == null || t >= tableButtonBacks.Length || tableButtonBacks[t] == null) { continue; }
                bool exists = t < n;
                bool inUse = exists && matchmaking != null && matchmaking.IsTableIndexInUse(t);
                bool selected = !autoMode_ && manualTable_ == t;
                bool watching = autoMode_ && watchedIndex_ == t;
                tableButtonBacks[t].gameObject.SetActive(exists);
                tableButtonBacks[t].color = selected ? buttonOn : (inUse ? buttonOff : buttonDisabled);
                if (tableButtonTexts != null && t < tableButtonTexts.Length && tableButtonTexts[t] != null)
                {
                    tableButtonTexts[t].color = selected ? textOn : (inUse ? (watching ? seatTextTurn : textOff) : textDisabled);
                }
            }
        }

        // ============================================================
        // 描画
        // ============================================================
        private void RefreshMonitor()
        {
            var tb = watched_;

            // 配信卓が無い・まだ起きていない・対局前 → 案内文だけ
            int idleKind = 0;
            if (tb == null) { idleKind = 1; }
            else if (!tb.gameObject.activeInHierarchy) { idleKind = 2; }
            else if (tb.points == null || tb.ScoreBoard_IsIdle()) { idleKind = 3; }

            if (idleKind != 0)
            {
                // 終局の演出中は、卓がリセットされても案内文へ戻さない（順位表の NEXT で卓はすぐ空く）
                if (lastIdleKind_ == 0 && FinalHolding(Time.time)) { return; }
                if (idleKind != lastIdleKind_) { ShowIdle(idleKind); ResetEventState(); }
                lastIdleKind_ = idleKind;
                lastRevision_ = 0;
                return;
            }
            if (lastIdleKind_ != 0)
            {
                // 案内文から対局へ（対局の開始・卓が起きた）＝4人の帯を下から出す
                if (lastIdleKind_ > 0) { bcEnterPending_ = true; }
                lastIdleKind_ = 0;
                SetBoardShown(true);
            }

            int rev = tb.Spectate_Revision();
            int psig = ComputePointSignature(tb);
            if (rev == lastRevision_ && psig == lastPointSig_) { return; }

            bool full = rev != lastRevision_;
            lastRevision_ = rev;
            lastPointSig_ = psig;

            if (full)
            {
                DetectEvents(tb);
                RenderAll(tb);
            }
            else { RenderPoints(tb); }
            // 最初の描画で最終形を置いたら、以後の変化は動かす
            uiArmed_ = true;
        }

        private void ShowIdle(int kind)
        {
            SetBoardShown(false);
            if (idleText == null) { return; }
            bool ja = IsJa();
            string msg;
            if (kind == 1)
            {
                if (autoMode_)
                {
                    msg = ja
                        ? "観戦できる対局はありません\n<size=60%>対局が始まると自動で映します</size>"
                        : ko_ ? "관전할 수 있는 대국이 없습니다\n<size=60%>대국이 시작되면 자동으로 비춥니다</size>"
                        : zhs_ ? "没有可以观战的对局\n<size=60%>对局开始后会自动播放</size>" : zht_ ? "沒有可以觀戰的對局\n<size=60%>對局開始後會自動播放</size>" : "No match to watch\n<size=60%>A match will appear here automatically when one starts</size>";
                }
                else
                {
                    msg = ja ? "この卓では対局していません" : ko_ ? "이 탁에서는 대국하고 있지 않습니다" : zhs_ ? "这张桌没有在对局" : zht_ ? "這張桌沒有在對局" : "Nothing is being played on this table";
                }
            }
            else if (kind == 2)
            {
                msg = ja ? "読み込み中…" : ko_ ? "불러오는 중…" : zhs_ ? "读取中…" : zht_ ? "讀取中…" : "Loading table…";
            }
            else
            {
                msg = ja
                    ? ("卓" + (watchedIndex_ + 1) + "：対局開始前です")
                    : ko_ ? ("탁" + (watchedIndex_ + 1) + ": 대국 시작 전입니다")
                    : zhs_ ? ("桌" + (watchedIndex_ + 1) + "：对局尚未开始") : zht_ ? ("桌" + (watchedIndex_ + 1) + "：對局尚未開始") : ("Table " + (watchedIndex_ + 1) + ": waiting for the game to start");
            }
            idleText.text = msg;
        }

        private void SetBoardShown(bool v)
        {
            if (boardRoot != null) { boardRoot.SetActive(v); }
            if (idleText != null) { idleText.gameObject.SetActive(!v); }
        }

        private int ComputePointSignature(Mahjong tb)
        {
            int sig = 13;
            for (int i = 0; i < 4; ++i) { sig = sig * 31 + tb.ScoreBoard_DisplayPoints(i); }
            sig = sig * 31 + tb.ScoreBoard_LanguageInt();
            sig = sig * 2 + (IsJa() ? 1 : 0);
            if (sig == 0) { sig = 1; }
            return sig;
        }

        private void RenderAll(Mahjong tb)
        {
            int n = tb.numPlayers >= 3 && tb.numPlayers <= 4 ? tb.numPlayers : 4;
            bool ja = IsJa();
            int turn = tb.Spectate_Turn();

            UpdateHeaderHold(tb, n);
            CountSeenTiles(tb, n);
            RenderConditions(tb, n, ja);
            if (viewMode_ == 0)
            {
                RenderBroadcast(tb, n, ja, turn, true);
                return;
            }
            bcEnterPending_ = false;
            RenderTopBar(tb, n, ja);

            for (int seat = 0; seat < 4; ++seat)
            {
                RenderRiver(tb, seat, seat < n);
                RenderSeatBox(tb, seat, seat < n, turn);
            }

            for (int row = 0; row < 4; ++row)
            {
                bool used = row < n;
                if (rowRoots != null && row < rowRoots.Length && rowRoots[row] != null)
                {
                    rowRoots[row].SetActive(used);
                }
                if (!used) { continue; }
                int seat = (tb.leader + row) % n;
                RenderRowHeader(tb, row, seat, n, ja, turn);
                RenderRowTiles(tb, row, seat);
            }
        }

        private void RenderPoints(Mahjong tb)
        {
            int n = tb.numPlayers >= 3 && tb.numPlayers <= 4 ? tb.numPlayers : 4;
            bool ja = IsJa();
            int turn = tb.Spectate_Turn();
            if (viewMode_ == 0)
            {
                RenderBroadcast(tb, n, ja, turn, false);
                return;
            }
            for (int seat = 0; seat < n; ++seat) { RenderSeatBox(tb, seat, true, turn); }
            for (int row = 0; row < n; ++row)
            {
                RenderRowHeader(tb, row, (tb.leader + row) % n, n, ja, turn);
            }
        }

        // ---- 上部の帯 ----
        private void RenderTopBar(Mahjong tb, int n, bool ja)
        {
            if (roundText != null) { roundText.text = HdrRound(tb); }
            int sticks = HdrKyotaku(tb) / 1000;
            int honba = HdrHonba(tb);
            if (honbaText != null)
            {
                honbaText.text = ja
                    ? (honba + "本場　供託 " + sticks)
                    : ko_ ? (honba + "본장   공탁 " + sticks)
                    : zhs_ ? (honba + "本场　供托 " + sticks) : zht_ ? (honba + "本場　供託 " + sticks) : ("Honba " + honba + "   Riichi sticks " + sticks);
            }
            if (remainText != null)
            {
                remainText.text = (ja ? "残り " : ko_ ? "남은 패 " : zhs_ ? "余牌 " : zht_ ? "餘牌 " : "Wall ") + HdrWall(tb);
            }
            if (tableText != null)
            {
                string t = (ja ? "卓" : ko_ ? "탁" : zhs_ ? "桌" : zht_ ? "桌" : "Table ") + (watchedIndex_ + 1);
                if (autoMode_) { t += ja ? "（自動）" : ko_ ? " (자동)" : zhs_ ? "（自动）" : zht_ ? "（自動）" : " (Auto)"; }
                tableText.text = t;
            }
            if (ruleText != null)
            {
                string kind;
                if (matchmaking != null && matchmaking.IsRankedTableIndex(watchedIndex_)) { kind = ja ? "段位戦" : ko_ ? "단위전" : zhs_ ? "段位战" : zht_ ? "段位戰" : "Ranked"; }
                else if (matchmaking != null && matchmaking.IsFreeTableIndex(watchedIndex_)) { kind = ja ? "フリー" : ko_ ? "프리" : zhs_ ? "自由" : zht_ ? "自由" : "Free"; }
                else { kind = ja ? "対局" : ko_ ? "대국" : zhs_ ? "对局" : zht_ ? "對局" : "Match"; }
                string players = n == 3 ? (ja ? "三麻" : ko_ ? "3인" : zhs_ ? "三麻" : zht_ ? "三麻" : "3P") : (ja ? "四麻" : ko_ ? "4인" : zhs_ ? "四麻" : zht_ ? "四麻" : "4P");
                ruleText.text = kind + "　" + players + "　" + tb.ScoreBoard_LengthLabel();
            }
            if (doraLabelText != null) { doraLabelText.text = ja ? "ドラ表示" : ko_ ? "도라 표시" : zhs_ ? "宝牌指示" : zht_ ? "寶牌指示" : "Dora"; }
            if (doraTiles != null)
            {
                for (int i = 0; i < doraTiles.Length; ++i)
                {
                    var img = doraTiles[i];
                    if (img == null) { continue; }
                    int id = tb.Spectate_DoraTile(i);
                    if (id >= 0) { SetFace(img, id, Color.white); }
                    else { SetBack(img); }
                }
            }
            if (centerRoundText != null) { centerRoundText.text = HdrRound(tb); }
            if (centerSubText != null)
            {
                centerSubText.text = (ja ? "残り " : ko_ ? "남은 패 " : zhs_ ? "余牌 " : zht_ ? "餘牌 " : "Wall ") + HdrWall(tb);
            }
        }

        // ---- 中央の箱の各辺 ----
        private void RenderSeatBox(Mahjong tb, int seat, bool used, int turn)
        {
            if (seatBoxTexts != null && seat < seatBoxTexts.Length && seatBoxTexts[seat] != null)
            {
                var txt = seatBoxTexts[seat];
                txt.gameObject.SetActive(used);
                if (used)
                {
                    txt.text = HdrWind(tb, seat) + "  " + tb.ScoreBoard_DisplayPoints(seat);
                    txt.color = seat == turn ? seatTextTurn : seatTextNormal;
                }
            }
            if (reachSticks != null && seat < reachSticks.Length && reachSticks[seat] != null)
            {
                reachSticks[seat].SetActive(used && tb.ScoreBoard_IsReached(seat));
            }
        }

        // ---- 河（入れ物を席の向きへ回してあるので、ここでは「手前の席」の座標で並べる）----
        private void RenderRiver(Mahjong tb, int seat, bool used)
        {
            if (riverTiles == null) { return; }
            int count = used ? tb.Spectate_NumDiscards(seat) : 0;
            int[] tiles = used ? tb.Spectate_DiscardTiles(seat) : null;
            bool[] tg = used ? tb.Spectate_DiscardTsumogiri(seat) : null;
            int reach = used ? tb.Spectate_ReachIndex(seat) : -1;
            if (tiles == null) { count = 0; }
            if (count > RIVER_MAX) { count = RIVER_MAX; }

            float x = 0f;
            for (int j = 0; j < RIVER_MAX; ++j)
            {
                int idx = seat * RIVER_MAX + j;
                if (idx >= riverTiles.Length) { break; }
                var img = riverTiles[idx];
                if (img == null) { continue; }
                if (j >= count)
                {
                    if (img.gameObject.activeSelf) { img.gameObject.SetActive(false); }
                    continue;
                }

                if (j % 6 == 0) { x = 0f; }
                bool rotate = reach != -1 && j == reach;
                float width = rotate ? riverTileH : riverTileW;
                float cx = -3.0f * riverTileW + x + width * 0.5f;
                float cy = riverStartY - (j / 6) * riverTileH - riverTileH * 0.5f;
                x += width;

                bool dim = tg != null && j < tg.Length && tg[j];
                PlaceTile(img, tiles[j], true, dim, rotate, riverTileW, riverTileH, cx, cy);
            }
        }

        // ---- 右側の行：見出し ----
        private void RenderRowHeader(Mahjong tb, int row, int seat, int n, bool ja, int turn)
        {
            bool isTurn = seat == turn;
            if (rowBacks != null && row < rowBacks.Length && rowBacks[row] != null)
            {
                rowBacks[row].color = isTurn ? rowBackTurn : rowBackNormal;
            }
            if (rowAccents != null && row < rowAccents.Length && rowAccents[row] != null)
            {
                rowAccents[row].gameObject.SetActive(isTurn);
            }
            if (rowWindTexts != null && row < rowWindTexts.Length && rowWindTexts[row] != null)
            {
                rowWindTexts[row].text = HdrWind(tb, seat);
            }
            if (rowNameTexts != null && row < rowNameTexts.Length && rowNameTexts[row] != null)
            {
                rowNameTexts[row].text = tb.Presentation_SeatName(seat);
            }
            if (rowPointTexts != null && row < rowPointTexts.Length && rowPointTexts[row] != null)
            {
                rowPointTexts[row].text = tb.ScoreBoard_DisplayPoints(seat).ToString();
            }
            if (rowRankTexts != null && row < rowRankTexts.Length && rowRankTexts[row] != null)
            {
                int rank = 1;
                int mine = tb.ScoreBoard_DisplayPoints(seat);
                int first = tb.Hanchan_GetFirstLeader();
                for (int i = 0; i < n; ++i)
                {
                    if (i == seat) { continue; }
                    int other = tb.ScoreBoard_DisplayPoints(i);
                    // 同点は起家に近い方が上（点数板・本体の半荘順位と同じ）
                    if (other > mine || (other == mine && (i - first + n) % n < (seat - first + n) % n)) { ++rank; }
                }
                rowRankTexts[row].text = ja ? (rank + "位") : ko_ ? (rank + "위") : zhs_ ? (rank + "位") : zht_ ? (rank + "位") : (rank == 1 ? "1st" : rank == 2 ? "2nd" : rank == 3 ? "3rd" : "4th");
            }
            if (rowStatusTexts != null && row < rowStatusTexts.Length && rowStatusTexts[row] != null)
            {
                string st = "";
                if (tb.ScoreBoard_IsReached(seat)) { st = ja ? "立直" : ko_ ? "리치" : zhs_ ? "立直" : zht_ ? "立直" : "Riichi"; }
                int kita = tb.Spectate_NumKita(seat);
                if (kita > 0) { st += (st.Length > 0 ? "  " : "") + (ja ? "北" : ko_ ? "북" : zhs_ ? "北" : zht_ ? "北" : "N") + "×" + kita; }
                rowStatusTexts[row].text = st;
            }
            if (rowDiffTexts != null && row < rowDiffTexts.Length && rowDiffTexts[row] != null)
            {
                rowDiffTexts[row].text = TopDiffLabel(tb, seat, n, ja);
            }
        }

        // ---- 右側の行：手牌＋副露（左から 手牌 → 副露。副露は古いものほど右）----
        private void RenderRowTiles(Mahjong tb, int row, int seat)
        {
            if (rowTiles == null) { return; }
            int baseIdx = row * ROW_MAX;

            int numHand = tb.Spectate_NumHand(seat);
            int[] hand = tb.Spectate_HandTiles(seat);
            if (hand == null) { numHand = 0; }
            bool visible = tb.Spectate_IsHandVisible(seat);
            bool gap = tb.Spectate_HasTsumoGap(seat) && numHand > 0;

            int numMelds = tb.Spectate_NumMelds(seat);
            int[] types = tb.Spectate_MeldTypes(seat);
            int[] dirs = tb.Spectate_MeldDirections(seat);
            int[][] melds = tb.Spectate_MeldTiles(seat);
            if (types == null || dirs == null || melds == null) { numMelds = 0; }

            // 1周目：幅を測って、はみ出すなら全体を縮める
            float gapW = rowTileW * 0.3f;
            float meldGap = rowTileW * 0.5f;
            float setGap = rowTileW * 0.15f;
            float total = numHand * rowTileW + (gap ? gapW : 0f);
            if (numMelds > 0) { total += meldGap; }
            for (int j = numMelds - 1; j >= 0; --j)
            {
                int type = types[j];
                int num = tb.Spectate_NumMeldTiles(type);
                for (int k = 0; k < num; ++k)
                {
                    if (type == EATED_SET_TYPE_KAKAN && k == 3) { continue; }
                    total += IsMeldRotated(type, dirs[j], k) ? rowTileH : rowTileW;
                }
                total += setGap;
            }
            float scale = total > rowTileMaxWidth ? rowTileMaxWidth / total : 1f;
            float w = rowTileW * scale;
            float h = rowTileH * scale;

            int used = 0;
            float x = rowTileLeft;
            float cyNormal = rowTileBaseY + h * 0.5f;

            for (int j = 0; j < numHand && used < ROW_MAX; ++j)
            {
                if (gap && j == numHand - 1) { x += gapW * scale; }
                PlaceTile(rowTiles[baseIdx + used], hand[j], visible, false, false, w, h, x + w * 0.5f, cyNormal);
                ++used;
                x += w;
            }

            if (numMelds > 0) { x += meldGap * scale; }
            for (int j = numMelds - 1; j >= 0; --j)
            {
                int type = types[j];
                int dir = dirs[j];
                int num = tb.Spectate_NumMeldTiles(type);
                int[] set = melds[j];
                if (set == null) { continue; }
                for (int k = 0; k < num && used < ROW_MAX; ++k)
                {
                    if (type == EATED_SET_TYPE_KAKAN && k == 3) { continue; }
                    bool rotate = IsMeldRotated(type, dir, k);
                    bool faceUp = !(type == EATED_SET_TYPE_ANKAN && (k == 0 || k == 3));
                    float width = rotate ? h : w;
                    float cy = rotate ? rowTileBaseY + w * 0.5f : cyNormal;
                    PlaceTile(rowTiles[baseIdx + used], set[k], faceUp, false, rotate, w, h, x + width * 0.5f, cy);
                    ++used;

                    // 加槓の4枚目は横向きの牌の上に重ねる（卓と同じ）
                    if (type == EATED_SET_TYPE_KAKAN && rotate && num >= 4 && used < ROW_MAX)
                    {
                        PlaceTile(rowTiles[baseIdx + used], set[3], true, false, true, w, h, x + width * 0.5f, cy + w);
                        ++used;
                    }
                    x += width;
                }
                x += setGap * scale;
            }

            for (int i = used; i < ROW_MAX; ++i)
            {
                var img = rowTiles[baseIdx + i];
                if (img != null && img.gameObject.activeSelf) { img.gameObject.SetActive(false); }
            }

            RenderRowWaits(tb, row, seat, numHand, visible);
        }

        // 副露のどの牌を横にするか（Mahjong.UpdateView の副露描画と同じ規則）
        private bool IsMeldRotated(int type, int dir, int k)
        {
            if (type == EATED_SET_TYPE_ANKAN) { return false; }
            if (dir == EATED_SET_DIRECTION_LEFT) { return k == 0; }
            if (dir == EATED_SET_DIRECTION_CENTER) { return k == 1; }
            if (dir == EATED_SET_DIRECTION_RIGHT) { return k == (type == EATED_SET_TYPE_MINKAN ? 3 : 2); }
            return false;
        }

        // ============================================================
        // 牌1枚
        // ============================================================
        private void PlaceTile(RawImage img, int tileId, bool faceUp, bool dim, bool rotate,
                               float w, float h, float cx, float cy)
        {
            if (img == null) { return; }
            if (!img.gameObject.activeSelf) { img.gameObject.SetActive(true); }
            if (faceUp) { SetFace(img, tileId, dim ? tsumogiriTint : Color.white); }
            else { SetBack(img); }
            var rt = img.rectTransform;
            // 伏せ牌は単色なので、隣と接すると1本の帯に見える。少しだけ小さく描いて隙間を作る。
            rt.sizeDelta = faceUp ? new Vector2(w, h) : new Vector2(w - 4f, h - 4f);
            rt.anchoredPosition = new Vector2(cx, cy);
            rt.localEulerAngles = new Vector3(0f, 0f, rotate ? 90f : 0f);
        }

        private void SetFace(RawImage img, int tileId, Color tint)
        {
            int key = tileId >> 2;
            if (tileU == null || key < 0 || key >= tileU.Length || tileW[key] <= 0f)
            {
                SetBack(img);
                return;
            }
            img.texture = tileTexture;
            img.uvRect = new Rect(tileU[key], tileV[key], tileW[key], tileH[key]);
            img.color = tint;
        }

        private void SetBack(RawImage img)
        {
            img.texture = Texture2D.whiteTexture;
            img.uvRect = new Rect(0f, 0f, 1f, 1f);
            img.color = tileBackColor;
        }

        // ============================================================
        // 第3段：出来事の検出 → カットイン＋カメラのアップ
        // ============================================================
        private void DetectEvents(Mahjong tb)
        {
            int n = tb.numPlayers >= 3 && tb.numPlayers <= 4 ? tb.numPlayers : 4;
            int state = tb.gameState;

            int[] reach = new int[4];
            int[] melds = new int[4];
            int[] kakan = new int[4];
            int[] kita = new int[4];
            for (int s = 0; s < 4; ++s)
            {
                bool used = s < n;
                reach[s] = used ? tb.Spectate_ReachIndex(s) : -1;
                melds[s] = used ? tb.Spectate_NumMelds(s) : 0;
                kita[s] = used ? tb.Spectate_NumKita(s) : 0;
                int k = 0;
                int[] types = used ? tb.Spectate_MeldTypes(s) : null;
                if (types != null)
                {
                    for (int j = 0; j < melds[s] && j < types.Length; ++j)
                    {
                        if (types[j] == EATED_SET_TYPE_KAKAN) { ++k; }
                    }
                }
                kakan[s] = k;
            }

            if (snapValid_)
            {
                int kind = 0;
                int who = -1;

                if (state != snapState_)
                {
                    if (state == GS_TSUMO) { kind = CUT_TSUMO; who = tb.Spectate_Turn(); }
                    else if (state == GS_RON)
                    {
                        for (int s = 0; s < n; ++s)
                        {
                            int seat = (tb.Spectate_Turn() + 1 + s) % n;   // 上家取り＝放銃者の次から
                            if (tb.Spectate_IsRonWinner(seat)) { who = seat; break; }
                        }
                        kind = CUT_RON;
                    }
                    else if (state == GS_EXHAUSTIVE_DRAW) { kind = CUT_DRAW; }
                    else if (state == GS_RESULT) { BeginFinal(tb, n, false); }
                    else if (state == GS_DEALING && viewMode_ == 0)
                    {
                        // 点数移動の顔を流している間は局の開始を待たせる
                        if (Time.time < bcScoreSeqEnd_) { bcRoundOpenPending_ = true; }
                        else { QueueRoundOpen(tb, n, Time.time); bcRoundOpenQueuedAt_ = Time.time; }
                        // 精算で消化されなかった立直棒（全員聴牌・全員ノーテンの流局など）は局の頭で帯に反映する
                        FlushPendingRiichi(tb, n);
                    }
                }

                if (kind == 0)
                {
                    for (int s = 0; s < n && kind == 0; ++s)
                    {
                        if (snapReach_[s] == -1 && reach[s] != -1) { kind = CUT_RIICHI; who = s; }
                        else if (melds[s] > snapMelds_[s])
                        {
                            int[] types = tb.Spectate_MeldTypes(s);
                            int t = (types != null && melds[s] >= 1 && melds[s] - 1 < types.Length) ? types[melds[s] - 1] : 0;
                            if (t == EATED_SET_TYPE_PON) { kind = CUT_PON; }
                            else if (t >= EATED_SET_TYPE_CHI_PREV) { kind = CUT_CHI; }
                            else { kind = CUT_KAN; }
                            who = s;
                        }
                        else if (kakan[s] > snapKakan_[s]) { kind = CUT_KAN; who = s; }
                        else if (kita[s] > snapKita_[s]) { kind = CUT_KITA; who = s; }
                    }
                }

                if (kind != 0) { TriggerCutIn(tb, kind, who); }
                if (state != snapState_ && state == GS_DEALING) { PrepareRoundNotice(tb, n); }

                // カンで新ドラがめくれた（宣言ではなく枚数の増加を見る＝明槓・加槓の打牌後めくりにも合う）。
                // 配牌の1枚目（0→1）は局の開始のアップが写すので数えない
                int dora = CountDoraIndicators(tb);
                if (dora >= 2 && dora > snapDora_ && !DoraShotDropState(state)) { doraShotPending_ = dora - 1; }
            }
            else
            {
                dealValid_ = true;
                dealRound_ = tb.round;
                dealLeader_ = tb.leader;
            }

            // 終局の順位表の最中に観戦を始めた：動かさず最終結果を出す
            if (!snapValid_ && state == GS_RESULT) { BeginFinal(tb, n, true); }
            snapValid_ = true;
            snapState_ = state;
            snapDora_ = CountDoraIndicators(tb);
            for (int s = 0; s < 4; ++s)
            {
                snapReach_[s] = reach[s];
                snapMelds_[s] = melds[s];
                snapKakan_[s] = kakan[s];
                snapKita_[s] = kita[s];
            }
            if (state != GS_TSUMO && state != GS_RON) { agariSeat_ = -1; }
        }

        private void TriggerCutIn(Mahjong tb, int kind, int seat)
        {
            bool ja = IsJa();
            string label;
            float dur;
            switch (kind)
            {
                case CUT_RIICHI:
                    // 立直は言語に関わらず英語表記（ユーザー指定：REACH の方が映える）
                    if (seat >= 0 && tb.Spectate_IsOpenReach(seat)) { label = "OPEN REACH"; }
                    else if (seat >= 0 && tb.Spectate_IsDoubleReach(seat)) { label = "DOUBLE REACH"; }
                    else { label = "REACH"; }
                    dur = 2.2f;
                    break;
                case CUT_PON: label = ja ? "ポン" : ko_ ? "퐁" : zhs_ ? "碰" : zht_ ? "碰" : "PON"; dur = 1.3f; break;
                case CUT_CHI: label = ja ? "チー" : ko_ ? "치" : zhs_ ? "吃" : zht_ ? "吃" : "CHI"; dur = 1.3f; break;
                case CUT_KAN: label = ja ? "カン" : ko_ ? "깡" : zhs_ ? "杠" : zht_ ? "槓" : "KAN"; dur = 1.5f; break;
                case CUT_KITA: label = ja ? "北" : ko_ ? "북" : zhs_ ? "拔北" : zht_ ? "拔北" : "KITA"; dur = 1.3f; break;
                case CUT_TSUMO: label = ja ? "ツモ" : ko_ ? "쯔모" : zhs_ ? "自摸" : zht_ ? "自摸" : "TSUMO"; dur = 2.6f; break;
                case CUT_RON: label = ja ? "ロン" : ko_ ? "론" : zhs_ ? "荣和" : zht_ ? "榮和" : "RON"; dur = 2.6f; break;
                case CUT_GAMESET: label = "GAME SET"; dur = finalCutDur; break;   // 言語に関わらず英語（REACH とそろえる）
                default: label = ja ? "流局" : ko_ ? "유국" : zhs_ ? "流局" : zht_ ? "流局" : "DRAW"; dur = 2.2f; break;
            }

            // 立直：Mリーグ式の演出（千点棒 → グリッチで文字）。部品が無ければ従来の帯へ落とす
            if (kind == CUT_RIICHI && riichiFxRoot != null)
            {
                HideCutIn();
                string fxLabel;
                // 言語に関わらず英語表記（Mリーグと同じ REACH）
                if (seat >= 0 && tb.Spectate_IsOpenReach(seat)) { fxLabel = "OPEN REACH"; }
                else if (seat >= 0 && tb.Spectate_IsDoubleReach(seat)) { fxLabel = "DOUBLE REACH"; }
                else { fxLabel = "REACH"; }
                ShowRiichiFx(tb, seat, fxLabel);
                QueueCameraForEvent(tb, kind, seat);
                return;
            }

            // 大きな帯のカットインは立直・オープン立直・ダブル立直とツモ・ロンだけ。鳴き・北抜き・流局は
            // その人の帯（行）の上に小さな札を出す（毎回大きく出すとうるさい・ユーザー指摘）
            if (kind != CUT_RIICHI && kind != CUT_TSUMO && kind != CUT_RON && kind != CUT_GAMESET)
            {
                ShowCallBadge(tb, seat, label, kind);
                QueueCameraForEvent(tb, kind, seat);
                return;
            }

            // 文字：ロン／ツモは宣言演出と同じ焼き文字を使う（無ければ文字で出す）
            Texture glyph = kind == CUT_RON ? ronGlyph : (kind == CUT_TSUMO ? tsumoGlyph : null);
            if (cutInGlyph != null)
            {
                cutInGlyph.gameObject.SetActive(glyph != null);
                if (glyph != null)
                {
                    cutInGlyph.texture = glyph;
                    float aspect = glyph.height > 0 ? (float)glyph.width / glyph.height : 2f;
                    cutInGlyph.rectTransform.sizeDelta = new Vector2(cutInGlyphHeight * aspect, cutInGlyphHeight);
                }
            }
            if (cutInText != null)
            {
                cutInText.gameObject.SetActive(glyph == null);
                cutInText.text = label;
            }

            // 名前と立ち絵（流局は人がいない）
            if (cutInName != null)
            {
                cutInName.text = seat >= 0 ? (tb.ScoreBoard_SeatWindNow(seat) + "  " + tb.Presentation_SeatName(seat)) : "";
            }
            if (cutInPortrait != null)
            {
                Texture tex = seat >= 0 ? ResolvePortrait(tb.Presentation_SeatChara(seat), tb.Presentation_SeatPlayerId(seat)) : null;
                cutInPortrait.gameObject.SetActive(tex != null);
                if (tex != null)
                {
                    cutInPortrait.texture = tex;
                    float aspect = tex.height > 0 ? (float)tex.width / tex.height : 1f;
                    cutInPortrait.rectTransform.sizeDelta = new Vector2(cutInPortraitHeight * aspect, cutInPortraitHeight);
                }
            }

            cutInStart_ = Time.time;
            cutInDur_ = dur;
            cutInActive_ = true;
            if (cutInRoot != null) { cutInRoot.SetActive(true); }
            UpdateCutIn(Time.time);

            QueueCameraForEvent(tb, kind, seat);
        }

        // カメラ：中継表示は演出順を積む。解説表示は宣言者のアップ→和了は手元
        private void QueueCameraForEvent(Mahjong tb, int kind, int seat)
        {
            if (kind == CUT_GAMESET) { return; }   // 終局のカメラは QueueFinalShots
            if (viewMode_ == 0) { QueueEventShots(tb, kind, seat, Time.time); }
            else if (seat >= 0)
            {
                eventShot_ = SHOT_CLOSE;
                eventSeat_ = seat;
                eventShotUntil_ = Time.time + (kind == CUT_TSUMO || kind == CUT_RON ? 2.0f : 2.6f);
            }
            if (kind == CUT_TSUMO || kind == CUT_RON) { agariSeat_ = seat; }
        }

        // 小さな札：その人の帯（行）の上にポンと出て、少し置いて消える
        private void ShowCallBadge(Mahjong tb, int seat, string label, int kind)
        {
            if (callBadgeRoot == null || callBadgeRect == null) { return; }
            int n = tb.numPlayers >= 3 && tb.numPlayers <= 4 ? tb.numPlayers : 4;
            Vector2 pos;
            if (seat < 0)
            {
                pos = viewMode_ == 0 ? new Vector2(0f, badgeBroadcastY) : new Vector2(badgeAnalysisRow0.x, badgeAnalysisRow0.y);
            }
            else
            {
                int row = viewMode_ == 0 ? BcRowOfSeat(tb, seat, n) : (seat - tb.leader + n) % n;
                pos = viewMode_ == 0
                    ? new Vector2(badgeCardX0 + row * badgeCardStep, badgeBroadcastY)
                    : new Vector2(badgeAnalysisRow0.x, badgeAnalysisRow0.y - row * badgeAnalysisRowStep);
            }
            callBadgeRect.anchoredPosition = pos;
            if (callBadgeText != null)
            {
                // 誰の宣言かは札の位置（その人の帯の真上）で分かるので、風は付けない
                callBadgeText.text = label;
            }
            badgeStart_ = Time.time;
            badgeDur_ = (kind == CUT_TSUMO || kind == CUT_RON || kind == CUT_DRAW) ? 2.2f : 1.4f;
            badgeActive_ = true;
            callBadgeRoot.SetActive(true);
            UpdateCallBadge(Time.time);
        }

        private void UpdateCallBadge(float now)
        {
            if (!badgeActive_) { return; }
            float t = now - badgeStart_;
            if (t >= badgeDur_)
            {
                HideCallBadge();
                return;
            }
            float pop = EaseOut(Mathf.Clamp01(t / 0.12f));
            float fade = Mathf.Clamp01((badgeDur_ - t) / 0.2f);
            if (callBadgeRect != null) { callBadgeRect.localScale = Vector3.one * Mathf.Lerp(0.6f, 1f, pop); }
            if (callBadgeGroup != null) { callBadgeGroup.alpha = fade; }
        }

        private void HideCallBadge()
        {
            badgeActive_ = false;
            if (callBadgeRoot != null && callBadgeRoot.activeSelf) { callBadgeRoot.SetActive(false); }
        }

        private Texture ResolvePortrait(int chara, int playerId)
        {
            if (selfieStore != null && chara == PlayerStatsStore.CHARA_SELFIE && playerId > 0)
            {
                var p = VRCPlayerApi.GetPlayerById(playerId);
                if (p != null && Utilities.IsValid(p))
                {
                    var selfie = selfieStore.GetPortraitOf(p);
                    if (selfie != null) { return selfie; }
                }
            }
            if (charaPortraits != null && chara >= 0 && chara < charaPortraits.Length && charaPortraits[chara] != null)
            {
                return charaPortraits[chara];
            }
            return defaultPortrait;
        }

        // 帯が縦に開く → 文字が右から入る → 最後に薄れて消える
        private void UpdateCutIn(float now)
        {
            if (!cutInActive_) { return; }
            float t = now - cutInStart_;
            if (t >= cutInDur_)
            {
                HideCutIn();
                return;
            }
            float open = EaseOut(Mathf.Clamp01(t / 0.15f));
            float slide = EaseOut(Mathf.Clamp01(t / 0.28f));
            float fade = Mathf.Clamp01((cutInDur_ - t) / 0.25f);
            if (cutInBand != null) { cutInBand.localScale = new Vector3(1f, Mathf.Max(0.001f, open), 1f); }
            if (cutInContent != null) { cutInContent.anchoredPosition = new Vector2(Mathf.Lerp(320f, 0f, slide), 0f); }
            if (cutInGroup != null) { cutInGroup.alpha = fade; }
        }

        private void HideCutIn()
        {
            cutInActive_ = false;
            if (cutInRoot != null && cutInRoot.activeSelf) { cutInRoot.SetActive(false); }
        }

        // 立直の演出を始める（動画のチーム紋章に当たる絵は出さない＝ユーザー指示。千点棒と文字だけ）
        private void ShowRiichiFx(Mahjong tb, int seat, string label)
        {
            if (riichiFxText != null)
            {
                riichiFxText.text = label;
                riichiTextY_ = riichiFxText.transform.localPosition.y;
            }
            if (riichiFxGhost != null) { riichiFxGhost.text = label; }
            riichiFxStart_ = Time.time;
            riichiFxActive_ = true;
            if (riichiFxRoot != null) { riichiFxRoot.SetActive(true); }
            UpdateRiichiFx(Time.time);
        }

        // 参考動画の実測（0.1秒刻み）：切替の 0.45 秒後に千点棒 → 0.75〜0.95 秒で棒が縮んで消える
        // → 1.0 秒から 0.4 秒グリッチ（明滅・横揺れ・大きな残像・左右へ走る線）→ 静止して切替まで保つ
        private void UpdateRiichiFx(float now)
        {
            if (!riichiFxActive_) { return; }
            float t = now - riichiFxStart_;
            if (t >= riichiFxDuration)
            {
                HideRiichiFx();
                return;
            }
            if (bcHud != null) { bcHud.alpha = 0f; }

            float sIn = Mathf.Clamp01((t - 0.45f) / 0.15f);
            float sShrink = Mathf.Clamp01((t - 0.75f) / 0.2f);
            bool stickOn = t >= 0.45f && t < 0.95f;
            if (riichiFxStickGroup != null) { riichiFxStickGroup.alpha = stickOn ? sIn : 0f; }
            if (riichiFxStick != null)
            {
                float sx = Mathf.Lerp(0.6f, 1f, EaseOut(sIn)) * Mathf.Lerp(1f, 0.72f, sShrink);
                riichiFxStick.localScale = new Vector3(sx, 1f, 1f);
            }
            if (riichiFxBlock != null)
            {
                bool blockOn = t >= 0.95f && t < 1.02f;
                if (riichiFxBlock.activeSelf != blockOn) { riichiFxBlock.SetActive(blockOn); }
            }

            float g = t - 1.0f;
            bool textOn = g >= 0f;
            float jitter = 0f;
            if (g >= 0f && g < 0.4f)
            {
                int f = (int)(g / 0.05f);
                textOn = (f % 3) != 1;
                jitter = ((f * 37) % 7 - 3) * 6f;
            }
            if (riichiFxText != null)
            {
                if (riichiFxText.gameObject.activeSelf != textOn) { riichiFxText.gameObject.SetActive(textOn); }
                riichiFxText.transform.localPosition = new Vector3(jitter, riichiTextY_, 0f);
            }
            if (riichiFxGhost != null)
            {
                bool ghostOn = g >= 0f && g < 0.55f;
                if (riichiFxGhost.gameObject.activeSelf != ghostOn) { riichiFxGhost.gameObject.SetActive(ghostOn); }
                if (ghostOn)
                {
                    // TMP の .alpha は Udon 非公開＝color ごと入れる
                    riichiFxGhost.color = new Color(riichiGhostColor.r, riichiGhostColor.g, riichiGhostColor.b, riichiGhostColor.a * (1f - g / 0.55f));
                    riichiFxGhost.transform.localPosition = new Vector3(40f - jitter * 1.5f, riichiTextY_, 0f);
                    riichiFxGhost.transform.localScale = Vector3.one * 1.15f;
                }
            }
            if (riichiFxLines != null)
            {
                float lp = Mathf.Clamp01((g + 0.05f) / 0.35f);
                bool linesOn = g >= -0.05f && g < 0.6f;
                for (int k = 0; k < riichiFxLines.Length; ++k)
                {
                    var ln = riichiFxLines[k];
                    if (ln == null) { continue; }
                    if (ln.gameObject.activeSelf != linesOn) { ln.gameObject.SetActive(linesOn); }
                    if (!linesOn) { continue; }
                    float w = Mathf.Lerp(80f, 520f + k * 60f, EaseOut(lp)) * (1f - Mathf.Clamp01((g - 0.3f) / 0.3f));
                    float side = k < 2 ? -1f : 1f;
                    ln.sizeDelta = new Vector2(w, ln.sizeDelta.y);
                    ln.anchoredPosition = new Vector2(side * (360f + w * 0.5f), ln.anchoredPosition.y);
                }
            }
        }

        private void HideRiichiFx()
        {
            riichiFxActive_ = false;
            if (riichiFxRoot != null && riichiFxRoot.activeSelf) { riichiFxRoot.SetActive(false); }
            if (bcHud != null) { bcHud.alpha = 1f; }
        }


        // ============================================================
        // UIのアニメーション（第1弾：待ち牌・手番の下線・立直棒／第2弾：局の表示・帯の登場・オーラス条件）
        // すべて一度きり。描画は「始めた時刻」を置くだけで、動かすのは毎フレームの StepUiAnim。
        // 切替直後の最初の描画（uiArmed_ = false）は動かさず最終形を置く＝卓の切替・途中観戦で全部が一斉に動かない。
        // 位置は Start で控えた Build の定位置からのずれで動かす（定位置そのものは書き換えない）。
        // ============================================================
        private void InitUiAnim()
        {
            for (int i = 0; i < 4; ++i)
            {
                bcWaitRts_[i] = RtOf(bcWaitRoots, i);
                if (bcWaitRts_[i] != null) { bcWaitBase_[i] = bcWaitRts_[i].anchoredPosition; }
                waitRts_[i] = RtOf(waitRoots, i);
                if (waitRts_[i] != null) { waitBase_[i] = waitRts_[i].anchoredPosition; }
                bcTurnRts_[i] = RtOf(bcTurnBars, i);
                bcRiichiRts_[i] = RtOf(bcRiichi, i);
                bcCardRts_[i] = RtOf(bcCards, i);
                if (bcCardRts_[i] != null) { bcCardBase_[i] = bcCardRts_[i].anchoredPosition; }
            }
            if (bcRoundText != null)
            {
                roundRt_ = bcRoundText.GetComponent<RectTransform>();
                roundBase_ = roundRt_.anchoredPosition;
                roundColor_ = bcRoundText.color;
            }
            if (bcSubText != null)
            {
                subRt_ = bcSubText.GetComponent<RectTransform>();
                subBase_ = subRt_.anchoredPosition;
                subColor_ = bcSubText.color;
            }
            if (conditionRoot != null) { condRt_ = conditionRoot.GetComponent<RectTransform>(); }
            uiBaseInit_ = true;
            ResetUiAnim();
        }

        private RectTransform RtOf(GameObject[] arr, int i)
        {
            if (arr == null || i >= arr.Length || arr[i] == null) { return null; }
            return arr[i].GetComponent<RectTransform>();
        }

        // 演出の記録を捨て、途中で止めた演出を最終形へ戻す（卓の切替・待機へ戻った時）
        private void ResetUiAnim()
        {
            uiArmed_ = false;
            for (int i = 0; i < 8; ++i)
            {
                waitShowAt_[i] = -100f;
                waitPopAt_[i] = -100f;
                waitRemainAt_[i] = -100f;
                waitSetSig_[i] = 0;
                waitRemainShown_[i] = -1;
                waitRemainTo_[i] = -1;
            }
            for (int i = 0; i < tileFlashAt_.Length; ++i)
            {
                tileFlashAt_[i] = -100f;
                tilePrevKey_[i] = -1;
                tilePrevLeft_[i] = 0;
            }
            for (int i = 0; i < 4; ++i)
            {
                turnAt_[i] = -100f;
                riichiPopAt_[i] = -100f;
            }
            enterAt_ = -100f;
            roundAt_ = -100f;
            condAt_ = -100f;
            roundShown_ = "";
            honbaShown_ = -1;
            // 「100 秒前に始まった」扱いで1回だけ進める＝全部が最終形になる
            float now = Time.time;
            uiAnimEnd_ = now;
            StepUiAnim(now);
        }

        private void ExtendAnim(float t)
        {
            if (t > uiAnimEnd_) { uiAnimEnd_ = t; }
        }

        // 0→1（減速）。t < 0 は 0
        private float Ease01(float t, float dur)
        {
            if (dur <= 0.001f) { return t >= 0f ? 1f : 0f; }
            return EaseOut(Mathf.Clamp01(t / dur));
        }

        // 少し行き過ぎて戻る（ポンと出る）
        private float BackOut(float x)
        {
            float c1 = 1.70158f;
            float a = x - 1f;
            return 1f + (c1 + 1f) * a * a * a + c1 * a * a;
        }

        private void StepUiAnim(float now)
        {
            // 最後の演出が終わって 0.5 秒（フレーム落ちしても最終形を書き切る余裕）を過ぎたら何もしない
            if (!uiBaseInit_ || now > uiAnimEnd_ + 0.5f) { return; }
            StepWaitStrips(now);
            StepBandMarks(now);
            StepRoundLabel(now);
            StepEnter(now);
            StepCondition(now);
        }

        // 待ち牌：帯が右から滑り込む → 牌が1枚ずつポンと出る（中継は右から・解説は左から）。
        // 残り0枚になった牌は少し膨らんで白から暗く沈む。残り枚数は数字が回る
        private void StepWaitStrips(float now)
        {
            for (int ai = 0; ai < 8; ++ai)
            {
                bool bc = ai < 4;
                int row = ai & 3;
                GameObject[] roots = bc ? bcWaitRoots : waitRoots;
                if (roots == null || row >= roots.Length || roots[row] == null || !roots[row].activeSelf) { continue; }

                float p = Ease01(now - waitShowAt_[ai], animWaitIn);
                CanvasGroup[] groups = bc ? bcWaitGroups : waitGroups;
                if (groups != null && row < groups.Length && groups[row] != null) { groups[row].alpha = p; }
                RectTransform rt = bc ? bcWaitRts_[row] : waitRts_[row];
                if (rt != null)
                {
                    Vector2 b = bc ? bcWaitBase_[row] : waitBase_[row];
                    rt.anchoredPosition = new Vector2(b.x + animWaitSlide * (1f - p), b.y);
                }

                int count = waitCount_[ai];
                RawImage[] tiles = bc ? bcWaitTiles : waitTiles;
                for (int k = 0; k < count && k < WAIT_MAX; ++k)
                {
                    int idx = row * WAIT_MAX + k;
                    if (tiles == null || idx >= tiles.Length || tiles[idx] == null) { continue; }
                    var img = tiles[idx];
                    int order = bc ? (count - 1 - k) : k;
                    float tp = now - waitPopAt_[ai] - order * animTileStagger;
                    float s = tp < 0f ? 0f : Mathf.Lerp(0.5f, 1f, BackOut(Mathf.Clamp01(tp / animTilePop)));
                    float tf = now - tileFlashAt_[ai * WAIT_MAX + k];
                    if (tf >= 0f && tf < animZeroFlash)
                    {
                        float f = tf / animZeroFlash;
                        s *= 1f + 0.3f * (1f - f);
                        img.color = Color.Lerp(Color.white, tsumogiriTint, f * f);
                    }
                    else if (tf >= animZeroFlash && tf < animZeroFlash + 0.6f) { img.color = tsumogiriTint; }
                    img.rectTransform.localScale = new Vector3(s, s, 1f);
                }

                if (waitRemainAt_[ai] > -50f && waitRemainShown_[ai] != waitRemainTo_[ai])
                {
                    int v = Mathf.RoundToInt(Mathf.Lerp(waitRemainFrom_[ai], waitRemainTo_[ai], Ease01(now - waitRemainAt_[ai], animRemainRoll)));
                    if (v != waitRemainShown_[ai])
                    {
                        waitRemainShown_[ai] = v;
                        TextMeshProUGUI[] rem = bc ? bcWaitRemain : waitRemainTexts;
                        if (rem != null && row < rem.Length && rem[row] != null) { rem[row].text = FormatRemain(ai, v, IsJa()); }
                    }
                }
            }
        }

        // 手番の下線：移ってきた席で左から伸び、前の席では縮んで消える
        private void SetTurnBar(int row, bool on)
        {
            if (bcTurnBars == null || row >= bcTurnBars.Length || bcTurnBars[row] == null) { return; }
            var go = bcTurnBars[row];
            if (!uiArmed_)
            {
                turnOn_[row] = on;
                turnAt_[row] = -100f;
                if (go.activeSelf != on) { go.SetActive(on); }
                if (bcTurnRts_[row] != null) { bcTurnRts_[row].localScale = Vector3.one; }
                return;
            }
            if (on == turnOn_[row])
            {
                if (on && !go.activeSelf) { go.SetActive(true); }
                return;
            }
            turnOn_[row] = on;
            turnAt_[row] = Time.time;
            ExtendAnim(turnAt_[row] + animTurnBar);
            if (on && !go.activeSelf) { go.SetActive(true); }
        }

        // 立直棒：立った瞬間にポンと出る。立直の演出（HUD を消している間）中なら演出が終わってから
        private void SetRiichiMark(int row, bool on)
        {
            if (bcRiichi == null || row >= bcRiichi.Length || bcRiichi[row] == null) { return; }
            if (!uiArmed_)
            {
                riichiPopAt_[row] = -100f;
                if (bcRiichiRts_[row] != null) { bcRiichiRts_[row].localScale = Vector3.one; }
            }
            else if (on && !riichiOn_[row])
            {
                float at = riichiFxActive_ ? riichiFxStart_ + riichiFxDuration : Time.time;
                if (at < Time.time) { at = Time.time; }
                riichiPopAt_[row] = at;
                ExtendAnim(at + animRiichiPop);
            }
            riichiOn_[row] = on;
            if (bcRiichi[row].activeSelf != on) { bcRiichi[row].SetActive(on); }
        }

        private void StepBandMarks(float now)
        {
            for (int row = 0; row < 4; ++row)
            {
                if (bcTurnBars != null && row < bcTurnBars.Length && bcTurnBars[row] != null && bcTurnBars[row].activeSelf)
                {
                    float p = Ease01(now - turnAt_[row], animTurnBar);
                    if (bcTurnRts_[row] != null) { bcTurnRts_[row].localScale = new Vector3(turnOn_[row] ? p : 1f - p, 1f, 1f); }
                    if (!turnOn_[row] && p >= 1f) { bcTurnBars[row].SetActive(false); }
                }
                if (bcRiichi != null && row < bcRiichi.Length && bcRiichi[row] != null && bcRiichi[row].activeSelf && bcRiichiRts_[row] != null)
                {
                    float t = now - riichiPopAt_[row];
                    float s = t < 0f ? 0f : Mathf.Lerp(0.6f, 1f, BackOut(Mathf.Clamp01(t / animRiichiPop)));
                    bcRiichiRts_[row].localScale = new Vector3(s, s, 1f);
                }
            }
        }

        // 局・本場が変わったら、左上の局の文字と本場の数を右から差し替える（最初の描画は記録だけ）
        private void TrackRoundLabel(string label, int honba)
        {
            if (uiArmed_ && roundShown_.Length > 0 && (label != roundShown_ || honba != honbaShown_))
            {
                roundAt_ = Time.time;
                ExtendAnim(roundAt_ + animRound);
            }
            roundShown_ = label;
            honbaShown_ = honba;
        }

        private void StepRoundLabel(float now)
        {
            float p = Ease01(now - roundAt_, animRound);
            float dx = animRoundSlide * (1f - p);
            // TMP の .alpha は Udon 非公開＝color ごと入れる
            if (roundRt_ != null) { roundRt_.anchoredPosition = new Vector2(roundBase_.x + dx, roundBase_.y); }
            if (bcRoundText != null) { bcRoundText.color = new Color(roundColor_.r, roundColor_.g, roundColor_.b, roundColor_.a * p); }
            if (subRt_ != null) { subRt_.anchoredPosition = new Vector2(subBase_.x + dx, subBase_.y); }
            if (bcSubText != null) { bcSubText.color = new Color(subColor_.r, subColor_.g, subColor_.b, subColor_.a * p); }
        }

        // 4人の帯：起家の行から順に、下からせり上がりながら出る
        private void StepEnter(float now)
        {
            for (int row = 0; row < 4; ++row)
            {
                float p = Ease01(now - enterAt_ - row * animEnterStagger, animEnter);
                if (bcCardGroups != null && row < bcCardGroups.Length && bcCardGroups[row] != null) { bcCardGroups[row].alpha = p; }
                if (bcCardRts_[row] != null)
                {
                    Vector2 b = bcCardBase_[row];
                    bcCardRts_[row].anchoredPosition = new Vector2(b.x, b.y - animEnterRise * (1f - p));
                }
            }
        }

        // オーラスの逆転条件：左から滑り込む
        private void StepCondition(float now)
        {
            if (conditionRoot == null || !conditionRoot.activeSelf) { return; }
            float p = Ease01(now - condAt_, animCond);
            if (conditionGroup != null) { conditionGroup.alpha = p; }
            if (condRt_ != null)
            {
                Vector2 b = viewMode_ == 0 ? condPosBroadcast : condPosAnalysis;
                condRt_.anchoredPosition = new Vector2(b.x - animCondSlide * (1f - p), b.y);
            }
        }

        private float EaseOut(float x)
        {
            float a = 1f - x;
            return 1f - a * a * a;
        }

        // ============================================================
        // 局表示の保持：和了・流局・精算・終局の間は、和了/流局した瞬間の局・本場・供託・残り山・風のまま。
        // 卓は精算の時点で次の局へ進むので、そのままだと点数移動の間に次の局が出る（ユーザー指定：配牌が始まってから切り替える）。
        // 精算の途中から観戦を始めたときは前の局を知らないので、卓の今の値を出す
        // ============================================================
        private void UpdateHeaderHold(Mahjong tb, int n)
        {
            int gs = tb.gameState;
            bool ended = (gs >= GS_EXHAUSTIVE_DRAW && gs <= GS_RON) || gs == GS_RESULT || gs == GS_SETTLEMENT;
            if (ended && hdrHave_) { hdrHold_ = true; return; }
            hdrHold_ = false;
            if (ended) { return; }
            hdrHave_ = true;
            hdrRound_ = tb.ScoreBoard_RoundLabel();
            hdrHonba_ = tb.honba;
            hdrKyotaku_ = tb.kyotaku;
            hdrWall_ = tb.GetWallRemain();
            for (int s = 0; s < 4; ++s) { hdrWinds_[s] = s < n ? tb.ScoreBoard_SeatWindNow(s) : ""; }
        }

        private string HdrRound(Mahjong tb) { return hdrHold_ ? hdrRound_ : tb.ScoreBoard_RoundLabel(); }
        private int HdrHonba(Mahjong tb) { return hdrHold_ ? hdrHonba_ : tb.honba; }
        private int HdrKyotaku(Mahjong tb) { return hdrHold_ ? hdrKyotaku_ : tb.kyotaku; }
        private int HdrWall(Mahjong tb) { return hdrHold_ ? hdrWall_ : tb.GetWallRemain(); }
        private string HdrWind(Mahjong tb, int seat) { return hdrHold_ && seat >= 0 && seat < 4 ? hdrWinds_[seat] : tb.ScoreBoard_SeatWindNow(seat); }

        // ============================================================
        // 終局の演出（中継表示）：最後の点数移動を数え終えたら GAME SET の帯 → 帯を消して最終結果を 4位から順にせり上げる。
        // 順位は卓と同じ Hanchan_GetRank（同点は起家順）。卓は順位表の NEXT ですぐリセットされるので、
        // 終局を見つけた瞬間に中身を控え、リセット後も finalHold 秒は残す。段位ポイントは本人の端末にしか無いので出さない
        // ============================================================
        private int finalTopSeat_ = -1;
        private Vector2[] finalRowBase_ = new Vector2[4];
        private bool finalBaseInit_ = false;

        private bool FinalHolding(float now)
        {
            if (finalPending_) { return now - finalDetectAt_ < 12f + finalCutDur + finalHold; }
            return finalActive_ && now < finalUntil_;
        }

        private void BeginFinal(Mahjong tb, int n, bool instant)
        {
            if (viewMode_ != 0 || finalRoot == null || finalRowGroups == null) { return; }
            if (!finalBaseInit_ && finalRowRts != null)
            {
                for (int i = 0; i < 4 && i < finalRowRts.Length; ++i) { if (finalRowRts[i] != null) { finalRowBase_[i] = finalRowRts[i].anchoredPosition; } }
                finalBaseInit_ = true;
            }
            int sum = 0;
            for (int s = 0; s < n; ++s) { sum += tb.Hanchan_GetPoints(s); }
            int start = n > 0 ? sum / n : 0;   // 終局時は供託も精算済み＝合計は開始点×人数
            bool ja = IsJa();
            finalRows_ = 0;
            finalTopSeat_ = -1;
            for (int r = 0; r < 4 && r < finalRowGroups.Length; ++r)
            {
                bool used = r < n;
                if (finalRowGroups[r] != null) { finalRowGroups[r].gameObject.SetActive(used); }
                if (!used) { continue; }
                int seat = tb.Hanchan_GetSeatByRank(r + 1);
                if (seat < 0 || seat >= n) { seat = r; }
                if (r == 0) { finalTopSeat_ = seat; }
                int pts = tb.Hanchan_GetPoints(seat);
                int diff = pts - start;
                SetFinalText(finalRankTexts, r, ja ? ((r + 1) + "位") : ko_ ? ((r + 1) + "위") : zhs_ ? ((r + 1) + "位") : zht_ ? ((r + 1) + "位") : FinalOrdinal(r + 1), Color.white);
                SetFinalText(finalNameTexts, r, tb.Presentation_SeatName(seat), Color.white);
                SetFinalText(finalPointTexts, r, (pts < 0 ? "-" : "") + FormatPts(pts < 0 ? -pts : pts), Color.white);
                SetFinalText(finalDiffTexts, r, (diff > 0 ? "+" : (diff < 0 ? "-" : "±")) + FormatPts(diff < 0 ? -diff : diff),
                             diff > 0 ? deltaPlus : (diff < 0 ? deltaMinus : Color.white));
                if (finalPortraits != null && r < finalPortraits.Length && finalPortraits[r] != null)
                {
                    Texture tex = ResolvePortrait(tb.Presentation_SeatChara(seat), tb.Presentation_SeatPlayerId(seat));
                    finalPortraits[r].gameObject.SetActive(tex != null);
                    if (tex != null) { finalPortraits[r].texture = tex; }
                }
                if (tb.agariEffect != null)
                {
                    Color c = BoltColorOf(tb, seat);
                    float b = bcPlateBrightness;
                    Color plate = new Color(c.r * b, c.g * b, c.b * b, 1f);
                    if (finalPlates != null && r < finalPlates.Length && finalPlates[r] != null) { finalPlates[r].color = plate; }
                    if (finalPortraitBgs != null && r < finalPortraitBgs.Length && finalPortraitBgs[r] != null) { finalPortraitBgs[r].color = plate; }
                }
                if (finalTopBadges != null && r < finalTopBadges.Length && finalTopBadges[r] != null) { finalTopBadges[r].SetActive(r == 0); }
                ++finalRows_;
            }
            if (finalTitle != null) { finalTitle.text = "FINAL RESULT"; }
            finalPending_ = true;
            finalActive_ = false;
            finalSawAnim_ = bcAnimActive_;
            finalInstant_ = instant || !uiArmed_;
            finalDetectAt_ = Time.time;
            if (finalRoot.activeSelf) { finalRoot.SetActive(false); }
        }

        private string FinalOrdinal(int r)
        {
            if (r == 1) { return "1st"; }
            if (r == 2) { return "2nd"; }
            if (r == 3) { return "3rd"; }
            return r + "th";
        }

        private void SetFinalText(TextMeshProUGUI[] arr, int i, string s, Color c)
        {
            if (arr == null || i >= arr.Length || arr[i] == null) { return; }
            arr[i].text = s;
            arr[i].color = c;
        }

        private void StepFinal(Mahjong tb, float now)
        {
            if (finalPending_)
            {
                // 最後の点数移動（卓の精算パネルと同時）を数え終えてから。点数移動が始まらない終局は 2.5 秒待って出す
                if (bcAnimActive_) { finalSawAnim_ = true; }
                bool settled = !bcAnimActive_ && now >= bcScoreSeqEnd_ && (finalSawAnim_ || now - finalDetectAt_ > 2.5f);
                if (!finalInstant_ && !settled && now - finalDetectAt_ < 10f) { return; }
                finalPending_ = false;
                finalActive_ = true;
                finalStart_ = finalInstant_ ? now - finalCutDur - 10f : now;
                finalUntil_ = now + (finalInstant_ ? finalHold : finalCutDur + finalHold);
                if (!finalInstant_ && tb != null) { TriggerCutIn(tb, CUT_GAMESET, -1); }
                if (tb != null) { QueueFinalShots(tb, now); }
            }
            if (!finalActive_) { return; }
            if (now >= finalUntil_) { HideFinal(); return; }

            float t = now - finalStart_ - finalCutDur;
            if (t < 0f) { return; }
            if (!finalRoot.activeSelf) { finalRoot.SetActive(true); }
            if (finalGroup != null) { finalGroup.alpha = Ease01(t, 0.3f); }
            if (bcBandGroup != null) { bcBandGroup.alpha = 0f; }
            for (int r = 0; r < finalRows_; ++r)
            {
                int order = finalRows_ - 1 - r;   // 4位（最下位）から
                float p = Ease01(t - 0.3f - order * finalRowInterval, finalRowDur);
                if (finalRowGroups[r] != null) { finalRowGroups[r].alpha = p; }
                if (finalRowRts != null && r < finalRowRts.Length && finalRowRts[r] != null)
                {
                    Vector2 b = finalRowBase_[r];
                    finalRowRts[r].anchoredPosition = new Vector2(b.x, b.y - finalRowRise * (1f - p));
                }
            }
        }

        // 終局のカメラ：全景（GAME SET と結果を出し終えるまで）→ 1位の人の顔 → 全景
        private void QueueFinalShots(Mahjong tb, float now)
        {
            if (viewMode_ != 0) { return; }
            SeqClear();
            float reveal = (finalInstant_ ? 0f : finalCutDur) + 0.3f + finalRows_ * finalRowInterval + finalRowDur;
            SeqAdd(SHOT_WIDE, -1, reveal, 0);
            SeqAddFace(tb, finalTopSeat_, 3.0f, FACE_FRONT);
            SeqAdd(SHOT_WIDE, -1, 60f, 0);
            SeqStart(now);
        }

        private void HideFinal()
        {
            bool was = finalActive_;
            finalPending_ = false;
            finalActive_ = false;
            if (finalRoot != null && finalRoot.activeSelf) { finalRoot.SetActive(false); }
            if (was && bcBandGroup != null) { bcBandGroup.alpha = 1f; }
        }

        // 0→1（ゆっくり出て一気に動き、ゆっくり止まる）
        private float EaseInOut3(float x)
        {
            if (x < 0.5f) { return 4f * x * x * x; }
            float a = -2f * x + 2f;
            return 1f - a * a * a * 0.5f;
        }

        // ============================================================
        // アバターの見張り描画（2026-10-11）
        //
        // カメラを「描くフレームだけ有効」にすると、卓のアバターは有効なフレームにしか誰にも見られて
        // いない（観戦ルームの人のメインカメラからは卓が見えない）。VRChat / Unity は見えていない
        // アバターのアニメーション・IK の更新を省くので、見える／見えないが30fpsで入れ替わり、
        // モニターの中でアバターが古い姿勢のまま写ったり、急に追いついて高速に動いたりチカチカした。
        // 録画中（60fps＝ほぼ毎フレーム有効）で直るのも同じ理由。
        //
        // 間のフレームもカメラを止めず、Player レイヤーだけを、今は画面に出していない方の RT の隅
        // へ描く。投影（画角・縦横比・位置）は本番の描画と同じに保つので、
        // 「どのアバターが見えている扱いか」は本番と変わらない。描く量はアバターの分だけ。
        // 写すのは Player レイヤー（他の人のアバター）だけ。範囲は RT の隅 1%。
        private const int KEEP_ALIVE_LAYER_MASK = 1 << 9;
        private const float KEEP_ALIVE_RECT_SIZE = 0.01f;

        private bool keepAliveActive_ = false;
        private RenderTexture keepAliveMainTarget_ = null;
        private int keepAliveMainMask_ = 0;

        // 今は画面に出していない方の RT（中継表示なら正方形、解説表示なら 16:9）。無ければ null＝見張りしない。
        private RenderTexture KeepAliveTarget(RenderTexture mainTarget)
        {
            if (mainTarget == null) { return null; }
            if (squareTexture != null && mainTarget != squareTexture) { return squareTexture; }
#if UNITY_ANDROID
            RenderTexture wide = wideTextureQuest != null ? wideTextureQuest : wideTexture;
#else
            RenderTexture wide = wideTexture;
#endif
            if (wide != null && mainTarget != wide) { return wide; }
            return null;
        }

        private void KeepAliveFrame(Camera cam)
        {
            if (!keepAliveActive_)
            {
                RenderTexture main = cam.targetTexture;
                RenderTexture spare = KeepAliveTarget(main);
                if (spare == null)
                {
                    if (cam.enabled) { cam.enabled = false; }
                    return;
                }
                keepAliveMainTarget_ = main;
                keepAliveMainMask_ = cam.cullingMask;
                // 縦横比を本番の出力先に固定してから差し替える（見えている扱いの範囲を変えない）。
                cam.aspect = main.height > 0 ? (float)main.width / main.height : 1f;
                cam.targetTexture = spare;
                cam.rect = new Rect(0f, 0f, KEEP_ALIVE_RECT_SIZE, KEEP_ALIVE_RECT_SIZE);
                cam.cullingMask = KEEP_ALIVE_LAYER_MASK;
                keepAliveActive_ = true;
            }
            if (!cam.enabled) { cam.enabled = true; }
        }

        private void EndKeepAlive()
        {
            if (!keepAliveActive_) { return; }
            keepAliveActive_ = false;
            var cam = spectatorCamera;
            if (cam == null) { return; }
            cam.cullingMask = keepAliveMainMask_;
            cam.rect = new Rect(0f, 0f, 1f, 1f);
            cam.targetTexture = keepAliveMainTarget_;
            cam.ResetAspect();
            keepAliveMainTarget_ = null;
        }

        private void StopSpectatorCamera()
        {
            EndKeepAlive();
            if (spectatorCamera != null && spectatorCamera.enabled) { spectatorCamera.enabled = false; }
        }

        // ============================================================
        // 第2段：カメラ
        // ============================================================
        private void UpdateCamera(Mahjong tb, bool live, float now)
        {
            var cam = spectatorCamera;
            if (cam == null) { return; }
            if (!live || !cameraMode_ || !tb.gameObject.activeInHierarchy)
            {
                StopSpectatorCamera();
                return;
            }

            // 描く頻度を落とす：画面へ描くのは間引いたフレームだけ（有効なフレームの終わりに1回描かれる）。
            // 間のフレームもカメラは止めず、アバターだけを小さく見張り描画する（KeepAliveFrame）。
            if (now < nextCamRender_)
            {
                KeepAliveFrame(cam);
                return;
            }
            EndKeepAlive();
#if UNITY_ANDROID
            int fps = cameraFpsQuest;
#else
            int fps = cameraFpsPc;
            if (recordOn_ && recordFps > fps) { fps = recordFps; }
#endif
            if (fps < 1) { fps = 1; }
            nextCamRender_ = now + 1.0f / fps - 0.002f;

            // ショットの決定
            int shot;
            int seat;
            int n = tb.numPlayers >= 3 && tb.numPlayers <= 4 ? tb.numPlayers : 4;
            int state = tb.gameState;
            int shotVar = 0;
            if (viewMode_ == 0)
            {
                eventShot_ = -1;
                // 手番の顔を写している最中に打牌したら、顔を打ち切って捨てた人の手元（余韻）へ戻す
                if (bcSeqTurnFace_ && seqIndex_ < seqCount_ && tb.Spectate_Turn() != bcTurnSeat_) { SeqClear(); }
                // 手番の人が牌に手を掛けた（1回選択・掴み・立直の宣言牌選び）＝顔を打ち切って手元へ
                if (bcSeqTurnFace_ && seqIndex_ < seqCount_ && IsTurnHandIntent(tb, n, now)) { SeqClear(); }
                if (SeqCurrent(now))
                {
                    shot = seqCurShot_; seat = seqCurSeat_; shotVar = seqCurVar_;
                }
                else
                {
                    shot = BroadcastBaseShot(tb, n, state, now);
                    seat = bcShotSeat_;
                    // 手番の頭で「顔」を積んだ直後は、その顔から始める（手元が1コマ挟まらないように）
                    if (SeqCurrent(now)) { shot = seqCurShot_; seat = seqCurSeat_; shotVar = seqCurVar_; }
                }
            }
            else if (eventShot_ >= 0 && now < eventShotUntil_) { shot = eventShot_; seat = eventSeat_; }
            else if (agariSeat_ >= 0 && (state == GS_TSUMO || state == GS_RON)) { shot = SHOT_AGARI; seat = agariSeat_; }
            else
            {
                eventShot_ = -1;
                // 俯瞰 8秒 → 手番の肩越し 4秒 を繰り返す（打牌の流れを追う）
                bool shoulder = Mathf.Repeat(now, 12f) >= 8f;
                int turn = tb.Spectate_Turn();
                if (shoulder && turn >= 0 && turn < n) { shot = SHOT_SHOULDER; seat = turn; }
                else { shot = SHOT_OVERVIEW; seat = -1; }
            }
            if (shot != curShot_ || seat != curSeat_ || shotVar != curVar_)
            {
                curShot_ = shot;
                curSeat_ = seat;
                curVar_ = shotVar;
                curShotStart_ = now;
            }

            var tt = tb.transform;
            Vector3 c = tt.position;
            Vector3 up = tt.up;
            Vector3 pos;
            Vector3 look;
            Vector3 camUp = up;
            float fov;
            float push = Mathf.Clamp01((now - curShotStart_) / 4f);   // ショットの中でじわっと寄る
            float tShot = now - curShotStart_;

            if (shot == SHOT_OVERVIEW)
            {
                orbitYaw_ = Mathf.Repeat(now * 5f, 360f);
                Vector3 d = tt.rotation * (Quaternion.Euler(0f, orbitYaw_, 0f) * Vector3.right);
                pos = c + d * 0.62f + up * 0.92f;
                look = c;
                fov = 46f;
            }
            else if (shot == SHOT_HAND)
            {
                // Mリーグの手元カメラ：卓の縁・本人の左・卓面から12cm。手牌の列を右斜め奥へ見通す
                Vector3 d = SeatDir(tt, seat);
                Vector3 right = Vector3.Cross(up, -d).normalized;
                pos = c + d * handRadial - right * handLeft + up * handHeight;
                look = c + d * handLookRadial + right * handLookRight;
                // 手元カメラは固定（流したり寄せたりすると手牌が揺れて読みにくい・ユーザー指摘）
                fov = handFov;
            }
            else if (shot == SHOT_WIDE)
            {
                // 全景：卓と4人を斜め上から（ゆっくり回る）
                orbitYaw_ = Mathf.Repeat(now * 4f, 360f);
                Vector3 d = tt.rotation * (Quaternion.Euler(0f, orbitYaw_, 0f) * Vector3.right);
                pos = c + d * 1.45f + up * 1.15f;
                look = c + up * 0.05f;
                fov = 52f;
            }
            else if (shot == SHOT_RIICHI_PULL)
            {
                // 立直（Mリーグ式）：卓の向こう（対面の脇）から宣言者を斜めに望遠で写して止め、
                // REACH のグリッチに合わせて同じ向きのまま後ろの高い所まで一気に引く（寄り→引きを画角と位置の両方で）
                Vector3 d = SeatDir(tt, seat);
                Vector3 right = Vector3.Cross(up, -d).normalized;
                Vector3 head = Vector3.zero;
                int pid = tb.Presentation_SeatPlayerId(seat);
                VRCPlayerApi p = pid > 0 ? VRCPlayerApi.GetPlayerById(pid) : null;
                if (p != null && Utilities.IsValid(p))
                {
                    head = p.GetBonePosition(HumanBodyBones.Head);
                    if (head == Vector3.zero) { head = p.GetPosition() + up * 1.3f; }
                    // 席を離れている人の顔は撮らない（カメラがワールドの反対側へ飛ぶ）
                    if (Vector3.ProjectOnPlane(head - c, up).magnitude > 2.0f) { p = null; }
                }
                Vector3 pos0;
                Vector3 look0;
                float fov0;
                if (p != null)
                {
                    // 顔と胸元・手元。対面の頭が手前に入る位置（本人から見て左前）
                    look0 = head - up * 0.12f;
                    pos0 = head - d * 1.35f - right * 0.55f + up * 0.05f;
                    fov0 = 26f;
                }
                else
                {
                    // CPU 席は顔が無い（席の向こうは柵や空）ので、同じ向きから河と立直の宣言牌・手牌を斜めに見下ろす
                    look0 = c + d * 0.18f;
                    pos0 = look0 - d * 0.55f - right * 0.30f + up * 0.32f;
                    fov0 = 36f;
                }
                // 引き切った画：宣言者の向かい側の高い所から、卓と4人を
                Vector3 look1 = c + up * 0.05f;
                Vector3 pos1 = c - d * 1.30f - right * 0.75f + up * 1.20f;
                float fov1 = 50f;
                float hold = Mathf.Clamp01(tShot / Mathf.Max(0.01f, riichiPullHold));
                float k = EaseInOut3(Mathf.Clamp01((tShot - riichiPullHold) / Mathf.Max(0.01f, riichiPullTime)));
                pos = Vector3.Lerp(pos0, pos1, k);
                look = Vector3.Lerp(look0, look1, k);
                // 止めている間はわずかに寄る
                fov = Mathf.Lerp(fov0 - 2f * hold, fov1, k);
            }
            else if (shot == SHOT_SHOULDER)
            {
                Vector3 d = SeatDir(tt, seat);
                Vector3 target = c + d * 0.05f;
                pos = target + d * 0.50f + up * 0.62f;
                look = target;
                fov = 44f - 4f * push;
            }
            else if (shot == SHOT_CLOSE)
            {
                Vector3 d = SeatDir(tt, seat);
                int pid = tb.Presentation_SeatPlayerId(seat);
                VRCPlayerApi p = pid > 0 ? VRCPlayerApi.GetPlayerById(pid) : null;
                Vector3 head = Vector3.zero;
                if (p != null && Utilities.IsValid(p))
                {
                    head = p.GetBonePosition(HumanBodyBones.Head);
                    if (head == Vector3.zero) { head = p.GetPosition() + up * 1.3f; }
                    // 席を離れている人（卓中心から水平に2m超）の顔は撮らない＝カメラがワールドの反対側へ飛ぶ
                    if (Vector3.ProjectOnPlane(head - c, up).magnitude > 2.0f) { p = null; }
                }
                if (p != null)
                {
                    Vector3 right = Vector3.Cross(up, -d).normalized;
                    look = head;
                    if (shotVar == FACE_SHOULDER)
                    {
                        // 本人の左隣の肩越し（隣のアバターが手前に入る）
                        pos = head - d * 0.70f - right * 0.60f - up * 0.04f;
                        fov = 30f - 3f * push;
                    }
                    else if (shotVar == FACE_SIDE)
                    {
                        // 横顔
                        pos = head - right * 0.85f - d * 0.15f + up * 0.02f;
                        fov = 30f - 3f * push;
                    }
                    else if (shotVar == FACE_ACROSS)
                    {
                        // 対面の背中越しに本人（Mリーグの和了の締め：卓と両脇の人も入る引きの画）
                        float hh = Vector3.Dot(head - c, up);
                        pos = c - d * 1.35f + up * (hh + 0.02f);
                        look = Vector3.Lerp(c + up * (hh * 0.6f), head, 0.7f);
                        fov = 42f - 3f * push;
                    }
                    else if (shotVar == FACE_PUNCH)
                    {
                        // 立直：正面からパッと寄る
                        pos = head - d * 0.95f - up * 0.05f;
                        fov = Mathf.Lerp(34f, 24f, EaseOut(Mathf.Clamp01(tShot / 0.35f)));
                    }
                    else
                    {
                        pos = head - d * 0.95f - up * 0.05f;
                        fov = 32f - 5f * push;
                    }
                }
                else
                {
                    // CPU 席は顔が無いので、席の背後の上から手牌を見下ろす（牌の表が読める向き）。
                    // 低いと卓の縁の点数板が手前に大きく入るので、高めから見下ろす
                    pos = c + d * 0.58f + up * 0.50f;
                    look = c + d * 0.26f;
                    fov = 40f - 4f * push;
                }
            }
            else if (shot == SHOT_MELD)
            {
                // 晒した牌を真上近くから大きく。画面は約35°傾けて斜めに写す（Mリーグの鳴きのアップ）
                Vector3 d = SeatDir(tt, seat);
                Vector3 right = Vector3.Cross(up, -d).normalized;
                Vector3 target = MeldTarget(tb, seat, shotVar == 1, c, d, right);
                pos = target + up * 0.20f + d * 0.05f;
                look = target;
                camUp = (-d * 0.82f + right * 0.57f).normalized;
                fov = 30f;
            }
            else if (shot == SHOT_DORA)
            {
                // ドラ表示牌を真上から。画面の上＝卓の中央側＝牌の天地が正しく見える向き
                int did = tb.Spectate_DoraTile(shotVar);
                if (did < 0) { did = tb.Spectate_DoraTile(0); }
                Transform dt = did >= 0 ? tb.Spectate_TileTransform(did) : null;
                Vector3 dp = (dt != null && dt.gameObject.activeInHierarchy) ? dt.position : c;
                Vector3 radial = Vector3.ProjectOnPlane(dp - c, up);
                if (radial.sqrMagnitude < 0.0001f) { radial = tt.right; }
                // 山は卓の辺に沿って並ぶので、天地はいちばん近い辺の向きにそろえる（斜めに写さない）
                Vector3 side = SeatDir(tt, 0);
                float best = -9f;
                for (int s4 = 0; s4 < 4; ++s4)
                {
                    Vector3 sd = SeatDir(tt, s4);
                    float dot = Vector3.Dot(radial, sd);
                    if (dot > best) { best = dot; side = sd; }
                }
                radial = side;
                pos = dp + up * 0.30f + radial * 0.05f;
                look = dp;
                camUp = -radial;
                fov = 20f - 3f * push;
            }
            else
            {
                // 和了者の手元を背後の上から（倒した手牌が正位置で読める向き）
                Vector3 d = SeatDir(tt, seat);
                pos = c + d * 0.78f + up * 0.55f;
                look = c + d * 0.28f;
                fov = 36f - 4f * push;
            }

            // 手元カメラだけ手前を切る（座っている人の体がレンズの直前に被るため）
            float near = shot == SHOT_HAND ? handNearClip : 0.05f;
            if (cam.nearClipPlane != near) { cam.nearClipPlane = near; }

            var ct = cam.transform;
            ct.position = pos;
            ct.rotation = Quaternion.LookRotation(look - pos, camUp);
            cam.fieldOfView = fov;
            if (!cam.enabled) { cam.enabled = true; }
        }

        // 晒した牌の位置。鳴きはいちばん新しい副露の牌の平均、北抜きは最後に抜いた北。
        // 取れなければ副露の列のあたり（席から見て右手前）
        private Vector3 MeldTarget(Mahjong tb, int seat, bool kita, Vector3 c, Vector3 d, Vector3 right)
        {
            if (kita)
            {
                Transform kt = tb.Spectate_KitaTileTransform(seat);
                if (kt != null && kt.gameObject.activeInHierarchy) { return kt.position; }
            }
            else
            {
                int nm = tb.Spectate_NumMelds(seat);
                int[] types = tb.Spectate_MeldTypes(seat);
                int[][] melds = tb.Spectate_MeldTiles(seat);
                if (nm > 0 && types != null && melds != null && nm - 1 < melds.Length && nm - 1 < types.Length && melds[nm - 1] != null)
                {
                    int[] set = melds[nm - 1];
                    int num = tb.Spectate_NumMeldTiles(types[nm - 1]);
                    Vector3 sum = Vector3.zero;
                    int cnt = 0;
                    for (int k = 0; k < num && k < set.Length; ++k)
                    {
                        Transform t = tb.Spectate_TileTransform(set[k]);
                        if (t != null && t.gameObject.activeInHierarchy) { sum += t.position; ++cnt; }
                    }
                    if (cnt > 0) { return sum / cnt; }
                }
            }
            return c + d * 0.35f + right * 0.30f;
        }

        // 卓中心 → 席 s の水平方向（Mahjong.cs の席の並び＝Y 軸まわり -90°×s）
        private Vector3 SeatDir(Transform tt, int s)
        {
            if (s < 0) { s = 0; }
            return tt.rotation * (Quaternion.Euler(0f, s * -90f, 0f) * Vector3.right);
        }

        // ============================================================
        // 第3段：宣言ボイスをモニターで鳴らし直す
        // ============================================================
        // 卓の声は席ごとの AudioSource に clip を差して Play している（MahjongSoundsV2）。
        // 新しく鳴り始めた clip を見つけたら、モニターの AudioSource で同じ clip を鳴らす。
        private void RelayVoices(Mahjong tb)
        {
            if (monitorVoice == null || !inRange_) { return; }
            var snd = tb.soundsV2;
            if (snd == null || snd.voiceSources == null) { return; }

            bool skip = false;
            var lp = Networking.LocalPlayer;
            if (lp != null && Utilities.IsValid(lp))
            {
                skip = Vector3.Distance(lp.GetPosition(), tb.transform.position) < voiceSkipNearTable;
            }

            for (int s = 0; s < 4 && s < snd.voiceSources.Length; ++s)
            {
                var src = snd.voiceSources[s];
                if (src == null) { continue; }
                if (src.isPlaying)
                {
                    var clip = src.clip;
                    float t = src.time;
                    if (clip != voiceClip_[s] || t + 0.05f < voiceTime_[s])
                    {
                        if (voiceArmed_ && !skip && clip != null) { monitorVoice.PlayOneShot(clip, src.volume * voiceGain); }
                        voiceClip_[s] = clip;
                    }
                    voiceTime_[s] = t;
                }
                else
                {
                    voiceClip_[s] = null;
                    voiceTime_[s] = 0f;
                }
            }
            // 切り替えた瞬間に鳴っていた声は拾わない（途中から鳴らすと唐突）
            voiceArmed_ = true;
        }

        // ============================================================
        // 卓の効果音（打牌・鳴きの整理・立直棒・配牌・あがり画面・アナウンス）をモニターで鳴らし直す。
        // 卓の MahjongSoundsV2 が鳴らした一撃を通し番号つきのリングに控えているので、差分だけ鳴らす。
        // 切替直後の1回目は記録だけ（溜まっていた音をまとめて鳴らさない）。卓の近く（声と同じ距離）では鳴らさない
        // ============================================================
        private void RelaySfx(Mahjong tb)
        {
            var snd = tb.soundsV2;
            if (snd == null || monitorVoice == null) { return; }
            int ser = snd.Spectate_SfxSerial();
            int ring = snd.Spectate_SfxRing();
            if (sfxSeen_ < 0 || ser < sfxSeen_ || ser - sfxSeen_ > ring || !inRange_)
            {
                sfxSeen_ = ser;
                return;
            }
            if (ser == sfxSeen_) { return; }

            bool skip = false;
            var lp = Networking.LocalPlayer;
            if (lp != null && Utilities.IsValid(lp))
            {
                skip = Vector3.Distance(lp.GetPosition(), tb.transform.position) < voiceSkipNearTable;
            }
            if (!skip)
            {
                for (int k = sfxSeen_; k < ser; ++k)
                {
                    var clip = snd.Spectate_SfxClip(k);
                    if (clip == null) { continue; }
                    float gain = snd.Spectate_SfxIsDiscard(k) ? discardGain : sfxGain;
                    monitorVoice.PlayOneShot(clip, snd.Spectate_SfxVolume(k) * gain);
                }
            }
            sfxSeen_ = ser;
        }

        // ============================================================
        // 立直 BGM をモニターで鳴らし直す。卓の曲は卓の近くにしか鳴らない（範囲外は Pause）ので、
        // 「本来なら鳴っている曲」を卓の MahjongRiichiBgm から読み、同じ曲をモニターの位置でループさせる。
        // 宣言ボイスの間は卓と同じく下げる。自分が卓の音の範囲にいる（卓で本物が聞こえる）間は鳴らさない
        // ============================================================
        private void RelayBgm(Mahjong tb, bool live)
        {
            if (monitorBgm == null) { return; }
            AudioClip clip = null;
            float vol = 0f;
            bool ducked = false;
            if (live && inRange_ && tb != null && tb.soundsV2 != null && tb.soundsV2.worldBgm != null)
            {
                var rb = tb.soundsV2.worldBgm;
                bool nearTable = rb.culler != null && rb.culler.IsAudioInRange();
                if (!nearTable)
                {
                    clip = rb.Spectate_ActiveClip();
                    vol = rb.Spectate_Volume() * bgmGain;
                    ducked = rb.Spectate_IsDucked();
                }
            }
            if (clip == null) { StopMonitorBgm(); return; }

            if (monitorBgm.clip != clip)
            {
                monitorBgm.Stop();
                monitorBgm.clip = clip;
                monitorBgm.loop = true;
                bgmPaused_ = false;
            }
            monitorBgm.volume = vol;
            if (ducked)
            {
                if (monitorBgm.isPlaying) { monitorBgm.Pause(); bgmPaused_ = true; }
                return;
            }
            if (bgmPaused_) { monitorBgm.UnPause(); bgmPaused_ = false; }
            else if (!monitorBgm.isPlaying) { monitorBgm.Play(); }
        }

        private void StopMonitorBgm()
        {
            bgmPaused_ = false;
            if (monitorBgm == null) { return; }
            if (monitorBgm.isPlaying) { monitorBgm.Stop(); }
            if (monitorBgm.clip != null) { monitorBgm.clip = null; }
        }

        // ============================================================
        // 第3段：待ち牌と残り枚数
        // ============================================================
        private int NormKey(int tileId)
        {
            int k = tileId >> 2;
            int type = k >> 4;
            int slot = k & 15;
            if (type < 3 && slot == 6) { slot = 5; }
            return (type << 4) | slot;
        }

        private void AddSeen(int tileId)
        {
            int k = NormKey(tileId);
            if (k >= 0 && k < 64) { ++seenCount_[k]; }
        }

        // この端末から見えている牌を数える（河・副露・ドラ表示・北抜き・表で見えている手牌）。
        // 伏せている手牌は数えない＝座っている人に他家の手の中身を漏らさない。
        private void CountSeenTiles(Mahjong tb, int n)
        {
            for (int i = 0; i < 64; ++i) { seenCount_[i] = 0; }
            for (int s = 0; s < n; ++s)
            {
                int nd = tb.Spectate_NumDiscards(s);
                int[] river = tb.Spectate_DiscardTiles(s);
                if (river != null)
                {
                    for (int j = 0; j < nd && j < river.Length; ++j) { AddSeen(river[j]); }
                }

                int nm = tb.Spectate_NumMelds(s);
                int[] types = tb.Spectate_MeldTypes(s);
                int[][] melds = tb.Spectate_MeldTiles(s);
                if (types != null && melds != null)
                {
                    for (int j = 0; j < nm && j < types.Length && j < melds.Length; ++j)
                    {
                        int num = tb.Spectate_NumMeldTiles(types[j]);
                        int[] set = melds[j];
                        if (set == null) { continue; }
                        for (int k = 0; k < num && k < set.Length; ++k) { AddSeen(set[k]); }
                    }
                }

                seenCount_[PEY_KEY] += tb.Spectate_NumKita(s);

                if (tb.Spectate_IsHandVisible(s))
                {
                    int nh = tb.Spectate_NumHand(s);
                    int[] hand = tb.Spectate_HandTiles(s);
                    if (hand != null)
                    {
                        for (int j = 0; j < nh && j < hand.Length; ++j) { AddSeen(hand[j]); }
                    }
                }
            }
            for (int i = 0; i < 5; ++i)
            {
                int d = tb.Spectate_DoraTile(i);
                if (d >= 0) { AddSeen(d); }
            }
        }

        // その席の待ち。手牌が表で見えていて待ちを出せる形（IsWaitShape）の時だけ計算する（それ以外は null）。
        // 14枚形（手番のツモ後）は Spectate_CalculateWaits がツモ牌（最右）を落として計算する＝ツモる前の待ち。
        // 打牌して13枚形に戻ると印が変わって計算し直す＝待ち替えした牌を切ればその場で切り替わる。
        // 手牌の中身（枚数・副露の数・牌ID）から作った印が前回と同じなら計算し直さない。
        private int[] SeatWaits(Mahjong tb, int seat, int numHand, bool visible)
        {
            if (!visible || seat < 0 || seat >= 4 || !IsWaitShape(tb, seat, numHand)) { return null; }
            int[] hand = tb.Spectate_HandTiles(seat);
            if (hand == null) { return null; }

            int sig = 17 + numHand * 131 + tb.Spectate_NumMelds(seat) * 7;
            for (int j = 0; j < numHand && j < hand.Length; ++j) { sig = sig * 31 + hand[j] + 1; }
            if (sig == 0) { sig = 1; }

            if (sig != waitSig_[seat] || waitCache_[seat] == null)
            {
                waitSig_[seat] = sig;
                waitCache_[seat] = tb.Spectate_CalculateWaits(seat);
            }
            return waitCache_[seat];
        }

        // 待ちを出せる手の形か。13枚形（打牌後）と、手番でツモ牌を離して置いている14枚形。
        // ポン・チー直後の14枚形はツモ牌が無く、最右を落とすと待ちが化けるので出さない（打牌で戻る）。
        private bool IsWaitShape(Mahjong tb, int seat, int numHand)
        {
            int r = numHand % 3;
            if (r == 1) { return true; }
            return r == 2 && tb.Spectate_HasTsumoGap(seat);
        }

        private void RenderRowWaits(Mahjong tb, int row, int seat, int numHand, bool visible)
        {
            RenderWaits(waitRoots, waitTiles, waitRemainTexts, waitLabelTexts, tb, row, seat, numHand, visible);
        }

        private void RenderWaits(GameObject[] roots, RawImage[] tiles, TextMeshProUGUI[] remains, TextMeshProUGUI[] labels,
                                 Mahjong tb, int row, int seat, int numHand, bool visible)
        {
            if (roots == null || row >= roots.Length || roots[row] == null) { return; }
            int ai = 4 + row;
            int[] waits = SeatWaits(tb, seat, numHand, visible);
            int count = waits != null ? waits.Length : 0;
            // 待ちを出せる形（打牌後／手番のツモ後）で聴牌している時だけ。伏せている手の待ちは出さない。局が終わったら（和了・流局・精算）消す
            bool show = visible && count > 0 && IsPlayState(tb.gameState);
            if (!show)
            {
                waitSetSig_[ai] = 0;
                if (roots[row].activeSelf) { roots[row].SetActive(false); }
                return;
            }
            if (count > WAIT_MAX) { count = WAIT_MAX; }
            bool sameSet = BeginWaitStrip(ai, roots[row], waits, count);

            int remain = 0;
            for (int k = 0; k < WAIT_MAX; ++k)
            {
                int idx = row * WAIT_MAX + k;
                if (tiles == null || idx >= tiles.Length || tiles[idx] == null) { continue; }
                var img = tiles[idx];
                if (k >= count)
                {
                    if (img.gameObject.activeSelf) { img.gameObject.SetActive(false); }
                    continue;
                }
                int left = 4 - seenCount_[NormKey(waits[k])];
                if (left < 0) { left = 0; }
                remain += left;
                // 10 枚以上（国士無双十三面など）は 9 枚ぶんの幅に詰める
                float sc = count > 9 ? 9f / count : 1f;
                PlaceTile(img, waits[k], true, left == 0, false, waitTileW * sc, waitTileH * sc,
                          waitTileLeft + k * waitTileStep * sc + waitTileW * sc * 0.5f, waitBaseY);
                TrackWaitTile(ai, k, img, waits[k], left, sameSet);
            }

            bool ja = IsJa();
            if (labels != null && row < labels.Length && labels[row] != null)
            {
                labels[row].text = ja ? "待ち" : ko_ ? "대기" : zhs_ ? "听牌" : zht_ ? "聽牌" : "Wait";
            }
            if (remains != null && row < remains.Length && remains[row] != null)
            {
                var rt = remains[row];
                SetWaitRemain(ai, rt, remain, sameSet, ja);
                rt.GetComponent<RectTransform>().anchoredPosition = new Vector2(waitTileLeft + count * waitTileStep + 64f, waitBaseY);
            }
        }

        // 中継表示の待ち牌：細帯の中を左下原点で右詰め。右端に残り枚数、その左に縦線、さらに左へ牌。
        // 残り枚数＝4 − 見えている枚数（河・副露・ドラ表示牌・公開されている手牌）。残り 0 の牌は暗くする
        private void RenderBroadcastWaits(Mahjong tb, int row, int seat, int numHand, bool visible)
        {
            if (bcWaitRoots == null || row >= bcWaitRoots.Length || bcWaitRoots[row] == null) { return; }
            var root = bcWaitRoots[row];
            int[] waits = SeatWaits(tb, seat, numHand, visible);
            int count = waits != null ? waits.Length : 0;
            // 待ちを出せる形（打牌後／手番のツモ後）で聴牌している時だけ。伏せている手の待ちは出さない。局が終わったら（和了・流局・精算）消す
            bool show = visible && count > 0 && IsPlayState(tb.gameState);
            if (!show)
            {
                waitSetSig_[row] = 0;
                if (root.activeSelf) { root.SetActive(false); }
                return;
            }
            if (count > WAIT_MAX) { count = WAIT_MAX; }
            bool sameSet = BeginWaitStrip(row, root, waits, count);

            // 10 枚以上（国士無双十三面など）は 9 枚ぶんの幅に詰める（そのままだと東家の帯で画面の左端からはみ出す）
            float sc = count > 9 ? 9f / count : 1f;
            float tw = bcWaitTileW * sc;
            float th = bcWaitTileH * sc;
            float step = bcWaitTileStep * sc;
            float divX = bcWaitStripW - 58f;
            float rightCenter = divX - 10f - tw * 0.5f;
            float cy = bcWaitStripH * 0.5f;
            int remain = 0;
            for (int k = 0; k < WAIT_MAX; ++k)
            {
                int idx = row * WAIT_MAX + k;
                if (bcWaitTiles == null || idx >= bcWaitTiles.Length || bcWaitTiles[idx] == null) { continue; }
                var img = bcWaitTiles[idx];
                if (k >= count)
                {
                    if (img.gameObject.activeSelf) { img.gameObject.SetActive(false); }
                    continue;
                }
                int left = 4 - seenCount_[NormKey(waits[k])];
                if (left < 0) { left = 0; }
                remain += left;
                PlaceTile(img, waits[k], true, left == 0, false, tw, th,
                          rightCenter - (count - 1 - k) * step, cy);
                TrackWaitTile(row, k, img, waits[k], left, sameSet);
            }
            if (bcWaitRemain != null && row < bcWaitRemain.Length && bcWaitRemain[row] != null) { SetWaitRemain(row, bcWaitRemain[row], remain, sameSet, true); }
            // 地の暗い帯は牌の数に合わせて左へ伸ばす
            if (bcWaitBgs != null && row < bcWaitBgs.Length && bcWaitBgs[row] != null)
            {
                bcWaitBgs[row].sizeDelta = new Vector2(58f + 10f + count * step + 14f, bcWaitStripH);
            }
        }

        // 待ち牌の帯を出し、出現・待ちの入れ替わりの演出を仕込む。前回と同じ待ちを出し続けているなら true
        private bool BeginWaitStrip(int ai, GameObject root, int[] waits, int count)
        {
            bool wasShown = root.activeSelf;
            if (!wasShown) { root.SetActive(true); }
            int sig = 7;
            for (int k = 0; k < count; ++k) { sig = sig * 31 + NormKey(waits[k]) + 1; }
            if (sig == 0) { sig = 1; }
            bool fresh = !wasShown || waitSetSig_[ai] == 0;
            bool changed = !fresh && sig != waitSetSig_[ai];
            float now = Time.time;
            if (fresh)
            {
                if (uiArmed_) { waitShowAt_[ai] = now; ExtendAnim(now + animWaitIn); }
                else { waitShowAt_[ai] = -100f; SnapWaitStrip(ai); }
            }
            // 出た時と、待ちが替わった時（待ち替えの打牌）は牌を1枚ずつ出し直す
            if (uiArmed_ && (fresh || changed))
            {
                waitPopAt_[ai] = now;
                ExtendAnim(now + animTilePop + animTileStagger * count);
            }
            else if (!uiArmed_) { waitPopAt_[ai] = -100f; }
            waitSetSig_[ai] = sig;
            waitCount_[ai] = count;
            return !fresh && !changed;
        }

        // 牌1枚の後始末：同じ牌の残りが 0 になった瞬間を拾って光らせる。演出中でなければ素の大きさに戻す
        private void TrackWaitTile(int ai, int k, RawImage img, int tileId, int left, bool sameSet)
        {
            int ti = ai * WAIT_MAX + k;
            int key = NormKey(tileId);
            float now = Time.time;
            if (uiArmed_ && sameSet && tilePrevKey_[ti] == key && tilePrevLeft_[ti] > 0 && left == 0)
            {
                tileFlashAt_[ti] = now;
                ExtendAnim(now + animZeroFlash);
            }
            else if (tilePrevKey_[ti] != key || left > 0) { tileFlashAt_[ti] = -100f; }
            tilePrevKey_[ti] = key;
            tilePrevLeft_[ti] = left;
            // 演出中は StepUiAnim が同じフレームのうちに大きさと色を上書きする
            if (now - waitPopAt_[ai] >= animTilePop + animTileStagger * WAIT_MAX) { img.rectTransform.localScale = Vector3.one; }
        }

        // 残り枚数：同じ待ちのまま数が変わった時だけ回す（出た瞬間・待ち替えは即値）
        private void SetWaitRemain(int ai, TextMeshProUGUI txt, int remain, bool sameSet, bool ja)
        {
            float now = Time.time;
            if (uiArmed_ && sameSet && waitRemainShown_[ai] >= 0 && remain != waitRemainTo_[ai])
            {
                waitRemainFrom_[ai] = waitRemainShown_[ai];
                waitRemainTo_[ai] = remain;
                waitRemainAt_[ai] = now;
                ExtendAnim(now + animRemainRoll);
                return;   // 数字は StepUiAnim が回す
            }
            if (remain == waitRemainTo_[ai] && waitRemainShown_[ai] != waitRemainTo_[ai])
            {
                // 回している最中に描き直しが来た：今の表示のまま（言語だけ合わせる）
                txt.text = FormatRemain(ai, waitRemainShown_[ai], ja);
                return;
            }
            waitRemainAt_[ai] = -100f;
            waitRemainTo_[ai] = remain;
            waitRemainShown_[ai] = remain;
            txt.text = FormatRemain(ai, remain, ja);
        }

        private string FormatRemain(int ai, int n, bool ja)
        {
            if (ai < 4) { return n.ToString(); }
            return ja ? ("残り" + n + "枚") : ko_ ? (n + "장 남음") : zhs_ ? ("剩" + n + "张") : zht_ ? ("剩" + n + "張") : (n + " left");
        }

        private void SnapWaitStrip(int ai)
        {
            bool bc = ai < 4;
            int row = ai & 3;
            CanvasGroup[] groups = bc ? bcWaitGroups : waitGroups;
            if (groups != null && row < groups.Length && groups[row] != null) { groups[row].alpha = 1f; }
            RectTransform rt = bc ? bcWaitRts_[row] : waitRts_[row];
            if (rt != null) { rt.anchoredPosition = bc ? bcWaitBase_[row] : waitBase_[row]; }
        }


        // ============================================================
        // 第3段：トップ差とオーラスの逆転条件
        // ============================================================
        private int SeatOrder(Mahjong tb, int seat, int n)
        {
            return (seat - tb.Hanchan_GetFirstLeader() + n) % n;
        }

        // 表示点数での順位1位の席
        private int TopSeat(Mahjong tb, int n, int except)
        {
            int best = -1;
            for (int s = 0; s < n; ++s)
            {
                if (s == except) { continue; }
                if (best < 0) { best = s; continue; }
                int ps = tb.ScoreBoard_DisplayPoints(s);
                int pb = tb.ScoreBoard_DisplayPoints(best);
                if (ps > pb || (ps == pb && SeatOrder(tb, s, n) < SeatOrder(tb, best, n))) { best = s; }
            }
            return best;
        }

        private string TopDiffLabel(Mahjong tb, int seat, int n, bool ja)
        {
            int top = TopSeat(tb, n, -1);
            if (top < 0) { return ""; }
            if (seat == top)
            {
                int second = TopSeat(tb, n, top);
                if (second < 0) { return ""; }
                int lead = tb.ScoreBoard_DisplayPoints(top) - tb.ScoreBoard_DisplayPoints(second);
                return (ja ? "2着差 +" : ko_ ? "2위와 차 +" : zhs_ ? "领先第2名 +" : zht_ ? "領先第2名 +" : "Lead +") + lead;
            }
            int gap = tb.ScoreBoard_DisplayPoints(top) - tb.ScoreBoard_DisplayPoints(seat);
            return (ja ? "トップ差 −" : ko_ ? "1위와 차 −" : zhs_ ? "距第1名 −" : zht_ ? "距第1名 −" : "To top −") + gap;
        }

        private void RenderConditions(Mahjong tb, int n, bool ja)
        {
            if (conditionRoot == null || conditionText == null) { return; }
            // オーラスだけ（三麻・四麻とも。三麻のツモはツモ損あり/なしを卓のルールに合わせて計算する）
            // 終局の順位表（卓の結果画面・モニターの FINAL RESULT）が出ている間は消す
            bool show = n >= 3 && tb.Spectate_IsAllLast() && tb.points != null
                        && tb.gameState != GS_TSUMO && tb.gameState != GS_RON
                        && tb.gameState != GS_RESULT && !finalPending_ && !finalActive_;
            condShown_ = show;
            if (!show)
            {
                if (noticeActive_) { return; }
                if (conditionRoot.activeSelf) { conditionRoot.SetActive(false); }
                return;
            }
            noticeActive_ = false;
            noticePending_ = false;

            int top = TopSeat(tb, n, -1);
            string text = ja ? "<color=#E8B84A>オーラス 逆転条件</color>" : ko_ ? "<color=#E8B84A>오라스 역전 조건</color>" : zhs_ ? "<color=#E8B84A>最终局 逆转条件</color>" : zht_ ? "<color=#E8B84A>最終局 逆轉條件</color>" : "<color=#E8B84A>All-Last: to take 1st</color>";
            for (int r = 0; r < n; ++r)
            {
                int p = (tb.leader + r) % n;
                if (p == top) { continue; }
                string name = tb.Presentation_SeatName(p);
                if (name.Length > 10) { name = name.Substring(0, 10); }
                string direct = CondLabel(tb, p, top, 0, n, ja);
                string ron = CondLabel(tb, p, top, 1, n, ja);
                string tsumo = CondLabel(tb, p, top, 2, n, ja);
                text += "\n" + tb.ScoreBoard_SeatWindNow(p) + " " + name + "　"
                      + (ja ? "直撃 " : ko_ ? "직격 " : zhs_ ? "直击 " : zht_ ? "直擊 " : "Direct ") + direct + "／"
                      + (ja ? "ロン " : ko_ ? "론 " : zhs_ ? "荣和 " : zht_ ? "榮和 " : "Ron ") + ron + "／"
                      + (ja ? "ツモ " : ko_ ? "쯔모 " : zhs_ ? "自摸 " : zht_ ? "自摸 " : "Tsumo ") + tsumo;
            }
            conditionText.text = text;
            if (!conditionRoot.activeSelf)
            {
                conditionRoot.SetActive(true);
                if (uiArmed_) { condAt_ = Time.time; ExtendAnim(condAt_ + animCond); }
                else
                {
                    condAt_ = -100f;
                    if (conditionGroup != null) { conditionGroup.alpha = 1f; }
                    if (condRt_ != null) { condRt_.anchoredPosition = viewMode_ == 0 ? condPosBroadcast : condPosAnalysis; }
                }
            }
        }

        // ============================================================
        // 局の頭の通知：連荘・本場・供託の節目とトビ寸前を、上の枠に noticeSeconds 秒だけ出す。
        // 配牌を見つけた時に中身を組み、点数移動の顔の列を流し終えてから出す。
        // オーラスは逆転条件が枠を使うので出さない（逆転条件は終局まで出しっぱなし）
        // ============================================================
        private void PrepareRoundNotice(Mahjong tb, int n)
        {
            bool ja = IsJa();
            int dealer = tb.leader;
            bool renchan = dealValid_ && tb.round == dealRound_ && dealer == dealLeader_ && tb.honba > 0;
            dealValid_ = true;
            dealRound_ = tb.round;
            dealLeader_ = dealer;

            string text = "";
            string head = "";
            if (renchan)
            {
                head = tb.ScoreBoard_SeatWindNow(dealer) + " " + ShortName(tb, dealer) + " "
                     + (ja ? "<color=#E8B84A>連荘</color> " + tb.honba + "本場"
                        : ko_ ? "<color=#E8B84A>연장</color> " + tb.honba + "본장"
                        : zhs_ ? "<color=#E8B84A>连庄</color> " + tb.honba + "本场" : zht_ ? "<color=#E8B84A>連莊</color> " + tb.honba + "本場" : "<color=#E8B84A>Dealer repeat</color> " + tb.honba + " honba");
            }
            else if (tb.honba >= noticeHonbaFrom)
            {
                head = ja ? "<color=#E8B84A>" + tb.honba + "本場</color>" : ko_ ? "<color=#E8B84A>" + tb.honba + "본장</color>" : zhs_ ? "<color=#E8B84A>" + tb.honba + "本场</color>" : zht_ ? "<color=#E8B84A>" + tb.honba + "本場</color>" : "<color=#E8B84A>" + tb.honba + " honba</color>";
            }
            if (tb.kyotaku >= noticeKyotakuFrom)
            {
                if (head.Length > 0) { head += "　"; }
                head += ja ? "供託 <color=#E8B84A>" + tb.kyotaku + "点</color>" : ko_ ? "공탁 <color=#E8B84A>" + tb.kyotaku + "점</color>" : zhs_ ? "供托 <color=#E8B84A>" + tb.kyotaku + "点</color>" : zht_ ? "供託 <color=#E8B84A>" + tb.kyotaku + "點</color>" : "Riichi sticks <color=#E8B84A>" + tb.kyotaku + "</color>";
            }
            text = head;

            bool bustOn = tb.points != null && !tb.Spectate_IsNoBust();
            for (int r = 0; r < n; ++r)
            {
                int s = (dealer + r) % n;
                int pts = tb.points != null ? tb.points[s] : 0;
                if (pts >= bustWarnPoints) { bustWarned_[s] = false; continue; }
                if (!bustOn || pts < 0 || bustWarned_[s]) { continue; }
                bustWarned_[s] = true;
                if (text.Length > 0) { text += "\n"; }
                text += tb.ScoreBoard_SeatWindNow(s) + " " + ShortName(tb, s) + " "
                      + (ja ? "残り" + pts + "点 <color=#FF7A6B>トビ寸前</color>" : ko_ ? pts + "점 남음 <color=#FF7A6B>토비 직전</color>" : zhs_ ? "剩" + pts + "点 <color=#FF7A6B>即将被击飞</color>" : zht_ ? "剩" + pts + "點 <color=#FF7A6B>即將被擊飛</color>" : pts + " left <color=#FF7A6B>near bust</color>");
            }

            // オーラスは逆転条件が優先（トビの記録だけ進めておく）
            if (text.Length == 0 || (n >= 3 && tb.Spectate_IsAllLast())) { noticePending_ = false; return; }
            noticeText_ = text;
            noticePending_ = true;
        }

        // ============================================================
        // 役満通知：「◯卓 役満確定／役満チャンス」を全卓ぶん上に出す。
        // 卓は寝ていると手牌を読めないので、各卓の Owner が判定して MatchmakingManager へ申告した
        // 同期値（4席のレベル＋役）を0.5秒ごとに読む。席ごとに「この局で出した最高のレベル」を覚え、
        // それを超えた時だけ出す（チャンス→確定は出し直す・同じレベルの往復は出さない）。
        //  ・観戦を始めた最初の読み取りは記録だけ（切替直後に古い通知を並べない）
        //  ・自分が座っている卓は出さない（他家の手を教えてしまう）
        //  ・複数届いたら順番に1件ずつ（同じ席の通知は待っている間に上書き）
        // ============================================================
        private const float YM_POLL_INTERVAL = 0.5f;
        private const float YM_IN = 0.3f;
        private const float YM_OUT = 0.4f;
        private const float YM_SLIDE = 40f;
        private const int YM_QUEUE_MAX = 8;
        private bool ymInit_ = false;
        private bool ymArmed_ = false;
        private float ymPollAt_ = 0f;
        private int[] ymEpoch_ = new int[NUM_TABLES];
        private int[] ymShown_ = new int[NUM_TABLES * 4];
        private int[] ymQTable_ = new int[YM_QUEUE_MAX];
        private int[] ymQSeat_ = new int[YM_QUEUE_MAX];
        private int[] ymQLevel_ = new int[YM_QUEUE_MAX];
        private int[] ymQYaku_ = new int[YM_QUEUE_MAX];
        private int[] ymQAlert_ = new int[YM_QUEUE_MAX];
        private int ymQCount_ = 0;
        private bool ymActive_ = false;
        private bool ymKakutei_ = false;
        private float ymAt_ = -100f;
        private RectTransform ymRt_;
        private Vector2 ymBase_;

        private void UpdateYakumanAlerts(float now)
        {
            if (yakumanAlertRoot == null || matchmaking == null) { return; }
            if (now >= ymPollAt_)
            {
                ymPollAt_ = now + YM_POLL_INTERVAL;
                PollYakumanAlerts();
            }
            StepYakumanAlert(now);
        }

        private void PollYakumanAlerts()
        {
            if (!ymInit_)
            {
                for (int i = 0; i < NUM_TABLES; ++i) { ymEpoch_[i] = -1; }
                ymInit_ = true;
            }
            int count = TableCount();
            int mine = matchmaking.GetLocalSeatedTable();
            for (int t = 0; t < count; ++t)
            {
                // 対局が終わった卓は覚えている局を捨てる（次の対局の東1局と局IDが重なるため）
                if (!matchmaking.IsTableBusyIndex(t)) { ymEpoch_[t] = -1; continue; }
                int alert = matchmaking.GetTableYakumanAlert(t);
                if (alert <= 0) { continue; }
                int epoch = (alert >> 11) & 0xFFFF;
                if (epoch != ymEpoch_[t])
                {
                    ymEpoch_[t] = epoch;
                    for (int s = 0; s < 4; ++s) { ymShown_[t * 4 + s] = 0; }
                }
                int yaku = matchmaking.GetTableYakumanYaku(t);
                for (int s = 0; s < 4; ++s)
                {
                    int lv = (alert >> (s * 2)) & 3;
                    if (lv <= ymShown_[t * 4 + s]) { continue; }
                    ymShown_[t * 4 + s] = lv;
                    if (!ymArmed_ || t == mine) { continue; }
                    EnqueueYakumanAlert(t, s, lv, (yaku >> (s * 7)) & 127, alert);
                }
            }
            ymArmed_ = true;
        }

        private void EnqueueYakumanAlert(int t, int s, int lv, int yaku, int alert)
        {
            for (int i = 0; i < ymQCount_; ++i)
            {
                if (ymQTable_[i] == t && ymQSeat_[i] == s)
                {
                    ymQLevel_[i] = lv;
                    ymQYaku_[i] = yaku;
                    ymQAlert_[i] = alert;
                    return;
                }
            }
            if (ymQCount_ >= YM_QUEUE_MAX) { return; }
            ymQTable_[ymQCount_] = t;
            ymQSeat_[ymQCount_] = s;
            ymQLevel_[ymQCount_] = lv;
            ymQYaku_[ymQCount_] = yaku;
            ymQAlert_[ymQCount_] = alert;
            ++ymQCount_;
        }

        private void StepYakumanAlert(float now)
        {
            if (!ymActive_)
            {
                if (ymQCount_ == 0) { return; }
                ShowYakumanAlert(ymQTable_[0], ymQSeat_[0], ymQLevel_[0], ymQYaku_[0], ymQAlert_[0]);
                for (int i = 1; i < ymQCount_; ++i)
                {
                    ymQTable_[i - 1] = ymQTable_[i];
                    ymQSeat_[i - 1] = ymQSeat_[i];
                    ymQLevel_[i - 1] = ymQLevel_[i];
                    ymQYaku_[i - 1] = ymQYaku_[i];
                    ymQAlert_[i - 1] = ymQAlert_[i];
                }
                --ymQCount_;
                ymActive_ = true;
                ymAt_ = now;
                if (!yakumanAlertRoot.activeSelf) { yakumanAlertRoot.SetActive(true); }
            }

            float e = now - ymAt_;
            float dur = yakumanAlertSeconds;
            if (e >= dur)
            {
                ymActive_ = false;
                if (yakumanAlertRoot.activeSelf) { yakumanAlertRoot.SetActive(false); }
                return;
            }
            float a = 1f;
            float dy = 0f;
            if (e < YM_IN)
            {
                float k = e / YM_IN;
                a = k;
                dy = YM_SLIDE * (1f - k) * (1f - k);  // 上から降りてきて止まる
            }
            else if (e > dur - YM_OUT)
            {
                a = (dur - e) / YM_OUT;
            }
            if (yakumanAlertGroup != null) { yakumanAlertGroup.alpha = a; }
            if (ymRt_ != null) { ymRt_.anchoredPosition = ymBase_ + new Vector2(0f, dy); }
            // 確定はタイトルを金⇔白でゆっくり明滅させる
            if (yakumanAlertTitle != null)
            {
                float p = ymKakutei_ ? 0.5f + 0.5f * Mathf.Sin(e * 6f) : 0f;
                yakumanAlertTitle.color = Color.Lerp(new Color(0.91f, 0.72f, 0.29f, 1f), Color.white, p);
            }
        }

        private void ShowYakumanAlert(int t, int s, int lv, int yaku, int alert)
        {
            if (ymRt_ == null)
            {
                ymRt_ = yakumanAlertRoot.GetComponent<RectTransform>();
                if (ymRt_ != null) { ymBase_ = ymRt_.anchoredPosition; }
            }
            bool ja = IsJa();
            ymKakutei_ = lv >= 2;
            if (yakumanAlertTitle != null)
            {
                yakumanAlertTitle.text = ymKakutei_ ? (ja ? "役満確定" : ko_ ? "역만 확정" : zhs_ ? "役满确定" : zht_ ? "役滿確定" : "YAKUMAN LOCKED") : (ja ? "役満チャンス" : ko_ ? "역만 찬스" : zhs_ ? "役满机会" : zht_ ? "役滿機會" : "YAKUMAN CHANCE");
            }
            if (yakumanAlertBack != null) { yakumanAlertBack.color = ymKakutei_ ? yakumanKakuteiColor : yakumanChanceColor; }

            int leader = (alert >> 8) & 3;
            int n = ((alert >> 10) & 1) != 0 ? 3 : 4;
            string wind = YakumanAlertWind((s - leader + n) % n, ja);
            string name = YakumanAlertSeatName(t, s, ja);
            string body = (ja ? (t + 1) + "卓" : ko_ ? (t + 1) + "번 탁" : zhs_ ? (t + 1) + "号桌" : zht_ ? (t + 1) + "號桌" : "Table " + (t + 1)) + "　" + wind;
            if (name.Length > 0) { body += " " + name; }
            string yakuName = yaku == 0 ? (ja ? "数え役満" : ko_ ? "헤아림 역만" : zhs_ ? "累计役满" : zht_ ? "累計役滿" : "Kazoe Yakuman")
                            : (watched_ != null ? watched_.Spectate_YakuNameOf(yaku) : "");
            if (yakuName.Length > 0) { body += "\n<color=#E8B84A>" + yakuName + "</color>"; }
            if (yakumanAlertBody != null) { yakumanAlertBody.text = body; }
            if (yakumanAlertGroup != null) { yakumanAlertGroup.alpha = 0f; }
        }

        private string YakumanAlertWind(int w, bool ja)
        {
            if (w == 0) { return ja ? "東家" : ko_ ? "동가" : zhs_ ? "东家" : zht_ ? "東家" : "East"; }
            if (w == 1) { return ja ? "南家" : ko_ ? "남가" : zhs_ ? "南家" : zht_ ? "南家" : "South"; }
            if (w == 2) { return ja ? "西家" : ko_ ? "서가" : zhs_ ? "西家" : zht_ ? "西家" : "West"; }
            return ja ? "北家" : ko_ ? "북가" : zhs_ ? "北家" : zht_ ? "北家" : "North";
        }

        // 席の主の名前。人間は Manager の同期席ID から引く（寝ている卓でも読める）。
        // CPU は見ている卓なら卓の表記、それ以外は「CPU」。
        private string YakumanAlertSeatName(int t, int s, bool ja)
        {
            int id = matchmaking.GetTableSeatIdAt(t, s);
            if (id > 0)
            {
                VRCPlayerApi p = VRCPlayerApi.GetPlayerById(id);
                if (p != null && Utilities.IsValid(p))
                {
                    string name = p.displayName;
                    if (name.Length > 12) { name = name.Substring(0, 12); }
                    return name;
                }
            }
            if (watched_ != null && t == watchedIndex_) { return ShortName(watched_, s); }
            return "CPU";
        }

        private void HideYakumanAlert()
        {
            ymActive_ = false;
            ymArmed_ = false;
            ymQCount_ = 0;
            if (yakumanAlertRoot != null && yakumanAlertRoot.activeSelf) { yakumanAlertRoot.SetActive(false); }
        }

        private string ShortName(Mahjong tb, int seat)
        {
            string name = tb.Presentation_SeatName(seat);
            if (name.Length > 10) { name = name.Substring(0, 10); }
            return name;
        }

        private void UpdateNotice(bool live, float now)
        {
            if (conditionRoot == null || conditionText == null) { return; }
            if (!live) { noticePending_ = false; noticeActive_ = false; return; }
            if (noticePending_ && now >= bcScoreSeqEnd_)
            {
                noticePending_ = false;
                if (condShown_) { return; }
                conditionText.text = noticeText_;
                noticeActive_ = true;
                noticeUntil_ = now + noticeSeconds;
                if (!conditionRoot.activeSelf) { conditionRoot.SetActive(true); }
                if (uiArmed_) { condAt_ = now; ExtendAnim(condAt_ + animCond); }
                else
                {
                    condAt_ = -100f;
                    if (conditionGroup != null) { conditionGroup.alpha = 1f; }
                    if (condRt_ != null) { condRt_.anchoredPosition = viewMode_ == 0 ? condPosBroadcast : condPosAnalysis; }
                }
                return;
            }
            if (noticeActive_ && now >= noticeUntil_)
            {
                noticeActive_ = false;
                if (!condShown_ && conditionRoot.activeSelf) { conditionRoot.SetActive(false); }
            }
        }

        // kind 0=トップから直撃 1=他家からロン 2=ツモ。条件を満たす最小の手を返す。
        private string CondLabel(Mahjong tb, int p, int top, int kind, int n, bool ja)
        {
            bool dealer = p == tb.leader;
            int honba = tb.honba;
            int kyo = tb.kyotaku;
            bool noTsumoLoss = n == 3 && tb.Spectate_IsNoTsumoLoss();
            for (int i = 0; i < 9; ++i)
            {
                if (kind == 2)
                {
                    // 三麻のツモ損なし：欠けている子の払いを残りの2人で折半して上乗せ（親はロン値/6、子はロン値/8）
                    int share = !noTsumoLoss ? 0 : (dealer ? Ceil100(RON_DEALER[i] / 6) : Ceil100(RON_CHILD[i] / 8));
                    LoadPoints(tb, n);
                    int gain = 0;
                    for (int q = 0; q < n; ++q)
                    {
                        if (q == p) { continue; }
                        int pay = dealer ? TSUMO_DEALER[i] : (q == tb.leader ? TSUMO_CHILD_D[i] : TSUMO_CHILD_C[i]);
                        pay += share + 100 * honba;
                        condPts_[q] -= pay;
                        gain += pay;
                    }
                    condPts_[p] += gain + kyo;
                    if (IsTopAfter(tb, p, n)) { return HandLabel(i, dealer, true, share, ja); }
                }
                else
                {
                    int value = (dealer ? RON_DEALER[i] : RON_CHILD[i]) + 300 * honba;
                    for (int loser = 0; loser < n; ++loser)
                    {
                        if (loser == p) { continue; }
                        if (kind == 0 && loser != top) { continue; }
                        if (kind == 1 && loser == top) { continue; }
                        LoadPoints(tb, n);
                        condPts_[p] += value + kyo;
                        condPts_[loser] -= value;
                        if (IsTopAfter(tb, p, n)) { return HandLabel(i, dealer, false, 0, ja); }
                    }
                }
            }
            return ja ? "なし" : ko_ ? "없음" : zhs_ ? "无" : zht_ ? "無" : "none";
        }

        private void LoadPoints(Mahjong tb, int n)
        {
            for (int q = 0; q < 4; ++q) { condPts_[q] = q < n ? tb.points[q] : 0; }
        }

        private bool IsTopAfter(Mahjong tb, int p, int n)
        {
            for (int q = 0; q < n; ++q)
            {
                if (q == p) { continue; }
                if (condPts_[q] > condPts_[p]) { return false; }
                if (condPts_[q] == condPts_[p] && SeatOrder(tb, q, n) < SeatOrder(tb, p, n)) { return false; }
            }
            return true;
        }

        private int Ceil100(int x)
        {
            return x % 100 != 0 ? (x / 100 + 1) * 100 : x;
        }

        private string HandLabel(int i, bool dealer, bool tsumo, int share, bool ja)
        {
            if (i >= 4)
            {
                if (ja)
                {
                    string[] jn = { "満貫", "跳満", "倍満", "三倍満", "役満" };
                    return jn[i - 4];
                }
                if (ko_)
                {
                    string[] kn = { "만관", "하네만", "배만", "삼배만", "역만" };
                    return kn[i - 4];
                }
                if (zhs_)
                {
                    string[] sn = { "满贯", "跳满", "倍满", "三倍满", "役满" };
                    return sn[i - 4];
                }
                if (zht_)
                {
                    string[] tn = { "滿貫", "跳滿", "倍滿", "三倍滿", "役滿" };
                    return tn[i - 4];
                }
                string[] en = { "Mangan", "Haneman", "Baiman", "Sanbaiman", "Yakuman" };
                return en[i - 4];
            }
            if (!tsumo) { return (dealer ? RON_DEALER[i] : RON_CHILD[i]).ToString(); }
            if (dealer) { return (TSUMO_DEALER[i] + share) + (ja ? "オール" : ko_ ? " 올" : zhs_ ? " ALL" : zht_ ? " ALL" : " all"); }
            return (TSUMO_CHILD_C[i] + share) + "-" + (TSUMO_CHILD_D[i] + share);
        }

        // ============================================================
        // 中継表示（Mリーグ式）
        // ============================================================
        // 打牌の流れの中の基本ショット（演出順が空のとき）。手番の頭と長考では顔を演出順に積む。
        private int BroadcastBaseShot(Mahjong tb, int n, int state, float now)
        {
            int turn = tb.Spectate_Turn();
            // 打牌中（ツモ後・鳴き確認・カン/立直の選択）だけ手元。配牌・流局・精算・結果は全景
            bool play = IsPlayState(state);

            if (turn != bcTurnSeat_)
            {
                bcTurnSeat_ = turn;
                bcTurnSince_ = now;
                bcThinkFaces_ = 0;
                bcThinkFrom_ = now + discardLinger;   // 新しい手番の手元が映り始めるのは余韻が明けてから
                // 手番の頭は手元から手元へ直接つなぐ。顔は新しい人の手元を一定時間写してから（下の顔の判定）。
                // 手番の頭に顔を挟むと、2.5〜3秒で打つ人が続いたとき顔ばかりになり手元が見えなかった
            }

            // 打牌の余韻：手番が移ってしばらくは、捨てた人の手元を写したまま（捨てた後の手牌を見せる）
            bool lingering = now - bcTurnSince_ < discardLinger;

            if (!play || turn < 0 || turn >= n)
            {
                bcShotSeat_ = -1;
                return SHOT_WIDE;
            }

            // 顔：人間の手番は手元を handBeforeFace 秒（既定5秒・誰かが立直している間は handBeforeFaceRiichi 秒）
            // 写したら顔を2.5秒、以後約8.5秒ごと（角度を変えて手元と行き来）。打牌したら顔はその場で打ち切る
            float firstFace = AnyRiichi(tb, n) ? handBeforeFaceRiichi : handBeforeFace;
            // 牌に手を掛けている間（と外してから INTENT_FACE_GAP 秒）は顔を挟まない。
            // 予定の顔はその間ずらす（手を離した直後に溜まった顔が続けて出ないように）。
            if (IsTurnHandIntent(tb, n, now) || now - bcIntentLast_ < INTENT_FACE_GAP)
            {
                float due = bcThinkFrom_ + firstFace + bcThinkFaces_ * 8.5f;
                float earliest = bcIntentLast_ + INTENT_FACE_GAP;
                if (due < earliest) { bcThinkFrom_ += earliest - due; }
            }
            else if (IsHumanSeat(tb, turn) && now - bcThinkFrom_ >= firstFace + bcThinkFaces_ * 8.5f)
            {
                ++bcThinkFaces_;
                SeqClear();
                SeqAdd(SHOT_CLOSE, turn, 2.5f, NextFaceVariant());
                SeqStart(now);
                bcSeqTurnFace_ = true;
            }

            // 手元：手番が移ったら切り替える。ただし同じ手元を最低 handMinHold 秒は保つ（CPU の速打ちでちらつかない）
            if (bcHandSeat_ < 0 || bcHandSeat_ >= n || (turn != bcHandSeat_ && !lingering && now - bcHandSince_ >= handMinHold))
            {
                bcHandSeat_ = turn;
                bcHandSince_ = now;
            }
            bcShotSeat_ = bcHandSeat_;
            return SHOT_HAND;
        }

        // ============================================================
        // カンドラ：新しくめくれたドラ表示牌を真上から写す（中継表示のカメラだけ）
        //  ・カンの列（顔→手元→晒した牌）が流れている間は、その後ろに足す
        //  ・手番の顔の列・何も流れていない時は、すぐ差し込む
        //  ・立直・和了など別の演出の最中は、それが終わるまで待つ（局が終われば捨てる）
        // ============================================================
        private const float DORA_SHOT_SECONDS = 2.5f;

        private int CountDoraIndicators(Mahjong tb)
        {
            int c = 0;
            while (c < 5 && tb.Spectate_DoraTile(c) >= 0) { ++c; }
            return c;
        }

        // 局が動いていない（配牌・和了・流局・終局）なら新ドラのアップは捨てる（嶺上開花などは和了の演出が優先）
        private bool DoraShotDropState(int state)
        {
            return state == GS_DEALING || state == GS_TSUMO || state == GS_RON
                || state == GS_EXHAUSTIVE_DRAW || state == GS_RESULT;
        }

        private void ServiceDoraShot(Mahjong tb, bool live, float now)
        {
            if (doraShotPending_ < 0) { return; }
            if (!live || viewMode_ != 0 || DoraShotDropState(tb.gameState)) { doraShotPending_ = -1; return; }

            bool running = SeqCurrent(now);
            if (running && !bcSeqTurnFace_)
            {
                // 流している列の最後が「晒した牌」＝カンの列。その後ろに続ける
                if (seqCount_ > 0 && seqShot_[seqCount_ - 1] == SHOT_MELD && seqCount_ < SEQ_MAX)
                {
                    SeqAdd(SHOT_DORA, -1, DORA_SHOT_SECONDS, doraShotPending_);
                    doraShotPending_ = -1;
                }
                return;  // ほかの演出の最中は待つ
            }
            SeqClear();
            SeqAdd(SHOT_DORA, -1, DORA_SHOT_SECONDS, doraShotPending_);
            SeqStart(now);
            doraShotPending_ = -1;
        }

        // ---- 演出順（ショットの列）----
        private void SeqClear()
        {
            seqCount_ = 0;
            seqIndex_ = 0;
            bcSeqTurnFace_ = false;
        }

        private void SeqAdd(int shot, int seat, float dur, int v)
        {
            if (seqCount_ >= SEQ_MAX) { return; }
            seqShot_[seqCount_] = shot;
            seqSeat_[seqCount_] = seat;
            seqDur_[seqCount_] = dur;
            seqVar_[seqCount_] = v;
            ++seqCount_;
        }

        // 顔のショット。顔の無い CPU 席は飛ばす
        private void SeqAddFace(Mahjong tb, int seat, float dur, int v)
        {
            if (seat < 0 || !IsHumanSeat(tb, seat)) { return; }
            SeqAdd(SHOT_CLOSE, seat, dur, v);
        }

        private void SeqStart(float now)
        {
            seqIndex_ = 0;
            seqStart_ = now;
        }

        // 今流すショット（seqCur* に入れる）。列を流し終えていれば false
        private bool SeqCurrent(float now)
        {
            while (seqIndex_ < seqCount_)
            {
                if (now - seqStart_ < seqDur_[seqIndex_])
                {
                    seqCurShot_ = seqShot_[seqIndex_];
                    seqCurSeat_ = seqSeat_[seqIndex_];
                    seqCurVar_ = seqVar_[seqIndex_];
                    return true;
                }
                seqStart_ += seqDur_[seqIndex_];
                ++seqIndex_;
            }
            return false;
        }

        // 手番の人が切る牌に手を掛けているか（掛けていれば時刻を控える）。
        private bool IsTurnHandIntent(Mahjong tb, int n, float now)
        {
            int turn = tb.Spectate_Turn();
            if (turn < 0 || turn >= n) { return false; }
            if (!tb.Spectate_HandIntent(turn)) { return false; }
            bcIntentLast_ = now;
            return true;
        }

        private bool IsHumanSeat(Mahjong tb, int seat)
        {
            int pid = tb.Presentation_SeatPlayerId(seat);
            if (pid <= 0) { return false; }
            var p = VRCPlayerApi.GetPlayerById(pid);
            return p != null && Utilities.IsValid(p);
        }

        // 顔の角度を 正面 → 肩越し → 横顔 の順に回す（同じ角度が続かないように）
        private int NextFaceVariant()
        {
            faceVariant_ = (faceVariant_ + 1) % 3;
            return faceVariant_;
        }

        // 打牌の流れの中か（ツモ後・鳴き確認・カン/立直の選択）。配牌・和了・流局・精算・結果は false
        private bool IsPlayState(int state)
        {
            return state == 2 || (state >= 3 && state <= 7) || state == 15 || state == 16 || state == 17;
        }

        private bool AnyRiichi(Mahjong tb, int n)
        {
            for (int s = 0; s < n; ++s)
            {
                if (tb.Spectate_ReachIndex(s) != -1) { return true; }
            }
            return false;
        }

        // 局の開始：全景 → ドラ表示牌の真上アップ → 顔の紹介（東家から・CPU は飛ばす）
        private void QueueRoundOpen(Mahjong tb, int n, float now)
        {
            SeqClear();
            SeqAdd(SHOT_WIDE, -1, 1.8f, 0);
            SeqAdd(SHOT_DORA, -1, 3.0f, 0);
            for (int r = 0; r < n; ++r) { SeqAddFace(tb, (tb.leader + r) % n, 1.8f, FACE_FRONT); }
            SeqStart(now);
        }

        // 宣言・和了の演出順
        private void QueueEventShots(Mahjong tb, int kind, int seat, float now)
        {
            int n = tb.numPlayers >= 3 && tb.numPlayers <= 4 ? tb.numPlayers : 4;
            SeqClear();
            if (kind == CUT_RIICHI)
            {
                // 立直：正面からパッと寄る（CPU 席は手牌の見下ろしになる）
                if (riichiFxRoot != null)
                {
                    // Mリーグ式：宣言者の斜めの顔 → REACH のグリッチに合わせて全景まで一気に引く（帯は演出が消す）。
                    // 顔は頭で写すので、引き切った後に顔は積み直さない（演出が終われば手元へ戻る）
                    SeqAdd(SHOT_RIICHI_PULL, seat, riichiFxDuration, 0);
                }
                else { SeqAdd(SHOT_CLOSE, seat, 3.0f, FACE_PUNCH); }
            }
            else if (kind == CUT_TSUMO || kind == CUT_RON)
            {
                // 和了（Mリーグのロンの中継を写した順。卓のあがり画面が開いたら UpdateAgariWinPanel が手牌の見下ろしへ切り替える）：和了者の手元（手牌を倒す）→ 放銃者の顔 → 和了者の顔
                // → 和了者の手元（倒した手牌）→ 放銃者の顔 → 他家の顔 → 和了者の顔 → 和了の形を上から（役の読み上げの間）
                // 点数の増減は卓の精算（NEXT の後）で出るので、そちらは QueueScoreShots が顔を積み直す
                bcAgariThisRound_ = true;
                int loser = -1;
                if (kind == CUT_RON)
                {
                    loser = tb.Spectate_Turn();
                    if (loser == seat || loser < 0 || loser >= n || tb.Spectate_IsRonWinner(loser)) { loser = -1; }
                }
                // それ以外の人間（ツモは放銃者の代わりにも使う）
                int other = FindOtherHuman(tb, n, seat, loser, -1);
                int other2 = FindOtherHuman(tb, n, seat, loser, other);
                int react = loser >= 0 ? loser : other;
                // 卓のあがり画面がもう開いている（検知が遅れた）なら、顔の列は飛ばして手牌の見下ろしだけ
                if (tb.agariScreen != null && tb.agariScreen.Spectate_IsPanelShown())
                {
                    SeqAdd(SHOT_AGARI, seat, 60f, 0);
                    SeqStart(now);
                    return;
                }
                SeqAdd(SHOT_HAND, seat, 1.9f, 0);
                SeqAddFace(tb, react, 1.9f, FACE_FRONT);
                SeqAddFace(tb, seat, 1.5f, FACE_FRONT);
                SeqAdd(SHOT_HAND, seat, 4.1f, 0);
                SeqAddFace(tb, loser, 1.1f, FACE_SIDE);
                SeqAddFace(tb, loser >= 0 ? other : other2, 3.7f, FACE_SHOULDER);
                SeqAddFace(tb, seat, 1.5f, FACE_SIDE);
                SeqAdd(SHOT_AGARI, seat, 60f, 0);
            }
            else if (kind == CUT_DRAW)
            {
                SeqAdd(SHOT_WIDE, -1, 30f, 0);
            }
            else if (seat >= 0)
            {
                // 鳴き・北抜き（Mリーグ式）：宣言者の顔 → 宣言者の手元（牌を倒して晒す）→ 晒した牌の真上アップ
                SeqAddFace(tb, seat, 1.2f, NextFaceVariant());
                SeqAdd(SHOT_HAND, seat, IsHumanSeat(tb, seat) ? 2.6f : 2.0f, 0);
                SeqAdd(SHOT_MELD, seat, 1.5f, kind == CUT_KITA ? 1 : 0);
            }
            SeqStart(now);
        }

        // 左上の局・右上の卓・下の4人の帯（起家からの席順で固定＝Mリーグ式。局が進んでも並びは動かさない）
        private void RenderBroadcast(Mahjong tb, int n, bool ja, int turn, bool full)
        {
            int sticks = HdrKyotaku(tb) / 1000;
            int honba = HdrHonba(tb);
            int wallRemain = HdrWall(tb);
            if (bcEnterPending_)
            {
                bcEnterPending_ = false;
                enterAt_ = Time.time;
                ExtendAnim(enterAt_ + animEnter + animEnterStagger * 3f);
            }
            string roundLabel = HdrRound(tb);
            TrackRoundLabel(roundLabel, honba);
            if (bcRoundText != null) { bcRoundText.text = roundLabel; }
            if (bcKyotakuText != null)
            {
                // 記号＋数字（本場の棒・供託の棒・山）
                if (bcSubText != null) { bcSubText.text = honba.ToString(); }
                bcKyotakuText.text = sticks.ToString();
                if (bcWallText != null) { bcWallText.text = wallRemain.ToString(); }
            }
            else if (bcSubText != null)
            {
                bcSubText.text = ja
                    ? (honba + "本場　供託 " + sticks + "　残り " + wallRemain)
                    : ko_ ? (honba + "본장   공탁 " + sticks + "   남은 패 " + wallRemain)
                    : zhs_ ? (honba + "本场　供托 " + sticks + "　余牌 " + wallRemain) : zht_ ? (honba + "本場　供託 " + sticks + "　餘牌 " + wallRemain) : ("Honba " + honba + "   Sticks " + sticks + "   Wall " + wallRemain);
            }
            if (bcTableText != null)
            {
                string kind;
                if (matchmaking != null && matchmaking.IsRankedTableIndex(watchedIndex_)) { kind = ja ? "段位戦" : ko_ ? "단위전" : zhs_ ? "段位战" : zht_ ? "段位戰" : "Ranked"; }
                else if (matchmaking != null && matchmaking.IsFreeTableIndex(watchedIndex_)) { kind = ja ? "フリー" : ko_ ? "프리" : zhs_ ? "自由" : zht_ ? "自由" : "Free"; }
                else { kind = ja ? "対局" : ko_ ? "대국" : zhs_ ? "对局" : zht_ ? "對局" : "Match"; }
                bcTableText.text = (ja ? ("卓" + (watchedIndex_ + 1)) : ko_ ? ("탁" + (watchedIndex_ + 1)) : zhs_ ? ("桌" + (watchedIndex_ + 1)) : zht_ ? ("桌" + (watchedIndex_ + 1)) : ("Table " + (watchedIndex_ + 1))) + "　" + kind;
            }

            for (int row = 0; row < 4; ++row)
            {
                bool used = row < n;
                if (bcCards != null && row < bcCards.Length && bcCards[row] != null) { bcCards[row].SetActive(used); }
                if (!used)
                {
                    if (bcWaitRoots != null && row < bcWaitRoots.Length && bcWaitRoots[row] != null) { bcWaitRoots[row].SetActive(false); }
                    continue;
                }
                int seat = BcSeatOfRow(tb, row, n);

                if (bcWindTexts != null && row < bcWindTexts.Length && bcWindTexts[row] != null) { bcWindTexts[row].text = HdrWind(tb, seat); }
                if (bcNameTexts != null && row < bcNameTexts.Length && bcNameTexts[row] != null) { bcNameTexts[row].text = tb.Presentation_SeatName(seat); }
                SetRiichiMark(row, tb.ScoreBoard_IsReached(seat));
                SetTurnBar(row, seat == turn);

                // 点数：卓の実値を集める（帯の数字は UpdateBroadcastPoints が自前の演出で動かす）
                bcCurPts_[row] = (tb.points != null && seat < tb.points.Length) ? tb.points[seat] : 0;

                if (!full) { continue; }

                ApplyBandColor(tb, row, seat);

                if (bcPortraits != null && row < bcPortraits.Length && bcPortraits[row] != null)
                {
                    var img = bcPortraits[row];
                    Texture tex = ResolvePortrait(tb.Presentation_SeatChara(seat), tb.Presentation_SeatPlayerId(seat));
                    img.gameObject.SetActive(tex != null);
                    if (tex != null)
                    {
                        img.texture = tex;
                        float aspect = tex.height > 0 ? (float)tex.width / tex.height : 1f;
                        if (aspect > 1f) { aspect = 1f; }
                        img.rectTransform.sizeDelta = new Vector2(bcPortraitHeight * aspect, bcPortraitHeight);
                    }
                }

                int numHand = tb.Spectate_NumHand(seat);
                RenderBroadcastWaits(tb, row, seat, numHand, tb.Spectate_IsHandVisible(seat));
            }

            UpdateBroadcastPoints(tb, n);
        }

        // 点数移動の演出を止めて平常の帯に戻す（卓の切替・待機へ戻った時）
        // 点数移動の演出を止めて平常の帯に戻す（卓の切替・待機へ戻った時）
        private void HideDeltas()
        {
            bcAnimActive_ = false;
            bcRoundOpenPending_ = false;
            bcScoreSeqEnd_ = 0f;
            for (int i = 0; i < 4; ++i) { bcPendRiichi_[i] = 0; }
            HideDeltaArray(bcDeltaTexts);
            HideDeltaArray(bcDeltaSubTexts);
            if (bcLabelGroups != null)
            {
                for (int i = 0; i < bcLabelGroups.Length; ++i)
                {
                    if (bcLabelGroups[i] != null) { bcLabelGroups[i].alpha = 1f; }
                }
            }
            HideWinPanel();
        }

        private void HideDeltaArray(TextMeshProUGUI[] arr)
        {
            if (arr == null) { return; }
            for (int i = 0; i < arr.Length; ++i)
            {
                if (arr[i] != null && arr[i].gameObject.activeSelf) { arr[i].gameObject.SetActive(false); }
            }
        }

        // 和了者のパネル：帯のその人の行を写し、右に役の一覧（あがり画面が描いた最後の和了）。作れなければ false
        private bool PrepareWinPanel(Mahjong tb, int row, int seat)
        {
            if (winRoot == null) { return false; }
            var scr = tb.agariScreen;
            if (scr == null || scr.Spectate_NumYaku() <= 0) { return false; }
            if (bcPlates != null && row < bcPlates.Length && bcPlates[row] != null)
            {
                if (winPlate != null) { winPlate.color = bcPlates[row].color; }
                if (winPortraitBg != null) { winPortraitBg.color = bcPlates[row].color; }
            }
            if (winPortrait != null && bcPortraits != null && row < bcPortraits.Length && bcPortraits[row] != null)
            {
                var src = bcPortraits[row];
                winPortrait.texture = src.texture;
                winPortrait.gameObject.SetActive(src.gameObject.activeSelf);
                winPortrait.GetComponent<RectTransform>().sizeDelta = src.GetComponent<RectTransform>().sizeDelta;
            }
            // 名前と風は帯に出している文字をそのまま写す
            if (winName != null && bcNameTexts != null && row < bcNameTexts.Length && bcNameTexts[row] != null) { winName.text = bcNameTexts[row].text; }
            if (winWind != null && bcWindTexts != null && row < bcWindTexts.Length && bcWindTexts[row] != null) { winWind.text = bcWindTexts[row].text; }
            if (winPoints != null) { winPoints.text = FormatPts(bcLastPts_[row]); }
            if (winYakuText != null)
            {
                string s = "";
                int num = scr.Spectate_NumYaku();
                for (int i = 0; i < num; ++i)
                {
                    if (i > 0) { s += "　　"; }
                    s += scr.Spectate_YakuName(i);
                }
                winYakuText.text = s;
            }
            bool ja = IsJa();
            if (winTotalLabel != null) { winTotalLabel.text = ja ? "合計" : ko_ ? "합계" : zhs_ ? "合计" : zht_ ? "合計" : "Total"; }
            if (winTotalText != null)
            {
                // 「3翻40符」（英語の卓は「3 Han 40 Fu」）。役満は翻・符が無いので称号（役満・二倍役満…）
                string han = scr.Spectate_HanLabel();
                string fu = scr.Spectate_FuLabel();
                string unit = scr.Spectate_HanUnitLabel();
                string v;
                if (scr.Spectate_IsYakuman() || fu.Length == 0) { v = scr.Spectate_TitleLabel(); }
                else if (unit == "翻" || unit == "판" || unit == "番") { v = han + unit + fu; }
                else { v = han + " " + unit + fu; }
                if (v.Length == 0) { v = scr.Spectate_PointLabel(); }
                winTotalText.text = v;
            }
            return true;
        }

        // 和了者のパネル＋役は、卓のあがり画面（役の読み上げ）と同時に出し、あがり画面が閉じる（NEXT）まで出したまま。
        // ダブロン・トリロンであがり画面の和了者が替わったら、パネルも替える
        private void UpdateAgariWinPanel(Mahjong tb, bool live, float now)
        {
            bool want = false;
            int seat = -1;
            int serial = -1;
            if (live && tb != null && tb.agariScreen != null && (tb.gameState == GS_TSUMO || tb.gameState == GS_RON) && tb.agariScreen.Spectate_IsPanelShown())
            {
                seat = tb.Spectate_AgariScreenSeat();
                serial = tb.agariScreen.Spectate_AgariSerial();
                want = seat >= 0 && seat < 4;
            }
            if (!want)
            {
                if (winPending_ && winReleaseAt_ < 0f) { winReleaseAt_ = now; }
                return;
            }
            if (winPending_ && winReleaseAt_ < 0f && seat == winSeat_ && serial == winSerial_) { return; }

            int n = tb.numPlayers >= 3 && tb.numPlayers <= 4 ? tb.numPlayers : 4;
            if (!PrepareWinPanel(tb, BcRowOfSeat(tb, seat, n), seat)) { return; }
            bool switching = winPending_ && winReleaseAt_ < 0f;
            winPending_ = true;
            winReleaseAt_ = -1f;
            winSeat_ = seat;
            winSerial_ = serial;
            // 和了者の入れ替え（ダブロン）は帯を出し直さず、パネルはそのまま・役の一覧だけ出し直す
            winBase_ = switching ? now - 0.4f : now;
            // カメラ：役の読み上げの間はずっと和了者の手牌を見下ろす（和了の顔の列は、あがり画面が開くまでの間だけ）
            agariSeat_ = seat;
            if (viewMode_ == 0) { QueueAgariHandShot(seat, now); }
        }

        private void QueueAgariHandShot(int seat, float now)
        {
            SeqClear();
            SeqAdd(SHOT_AGARI, seat, 60f, 0);
            SeqStart(now);
        }

        // 参考動画の実測：帯が 0.2 秒で消え → 和了者のパネルが 0.2 秒で出る → 0.6 秒遅れて役の一覧。
        // 畳むときは 0.3 秒でパネルを消して帯を戻す
        private void UpdateWinPanel(float now)
        {
            if (!winPending_) { return; }
            float w = now - winBase_;
            if (w < 0f) { return; }
            bool closing = winReleaseAt_ >= 0f;
            float o = closing ? Mathf.Clamp01((now - winReleaseAt_) / 0.3f) : 0f;
            if (closing && o >= 1f)
            {
                HideWinPanel();
                return;
            }
            if (winRoot != null && !winRoot.activeSelf) { winRoot.SetActive(true); }
            float inA = Mathf.Clamp01((w - 0.2f) / 0.2f);
            float bandA = closing ? Mathf.Max(o, 1f - Mathf.Clamp01(w / 0.2f)) : 1f - Mathf.Clamp01(w / 0.2f);
            float winA = closing ? inA * (1f - o) : inA;
            if (bcBandGroup != null) { bcBandGroup.alpha = bandA; }
            if (winGroup != null) { winGroup.alpha = winA; }
            if (winYakuGroup != null) { winYakuGroup.alpha = Mathf.Clamp01((w - 0.8f) / 0.3f); }
        }

        private void HideWinPanel()
        {
            winPending_ = false;
            winReleaseAt_ = -1f;
            winSeat_ = -1;
            winSerial_ = -1;
            if (winRoot != null && winRoot.activeSelf) { winRoot.SetActive(false); }
            if (bcBandGroup != null) { bcBandGroup.alpha = 1f; }
        }

        // 中継表示の帯の並び：起家から席順に固定
        private int BcSeatOfRow(Mahjong tb, int row, int n)
        {
            int f = tb.firstLeader;
            if (f < 0 || f >= n) { f = 0; }
            return (f + row) % n;
        }

        private int BcRowOfSeat(Mahjong tb, int seat, int n)
        {
            int f = tb.firstLeader;
            if (f < 0 || f >= n) { f = 0; }
            return (seat - f + n) % n;
        }

        // 帯の色：その席の人が選んだ和了演出（雷）の主色。未設定・CPU は 0＝金雷
        // 帯の色：その席の人が選んだ和了演出（雷）の色を少し落として点数板と顔の台に塗る
        private void ApplyBandColor(Mahjong tb, int row, int seat)
        {
            if (tb.agariEffect == null) { return; }
            Color c = BoltColorOf(tb, seat);
            float b = bcPlateBrightness;
            Color plate = new Color(c.r * b, c.g * b, c.b * b, 1f);
            if (bcPlates != null && row < bcPlates.Length && bcPlates[row] != null) { bcPlates[row].color = plate; }
            if (bcPortraitBgs != null && row < bcPortraitBgs.Length && bcPortraitBgs[row] != null) { bcPortraitBgs[row].color = plate; }
        }

        // その席の人の雷の主色。未設定・CPU は 0＝金雷。黒雷のように暗すぎる色は映像に沈むので、
        // 副色（リングの色）へ少し寄せて底上げする
        private Color BoltColorOf(Mahjong tb, int seat)
        {
            if (tb.agariEffect == null) { return Color.white; }
            int fx = 0;
            int pid = seat >= 0 ? tb.Presentation_SeatPlayerId(seat) : 0;
            if (pid > 0 && statsStore != null)
            {
                var p = VRCPlayerApi.GetPlayerById(pid);
                if (p != null && Utilities.IsValid(p)) { fx = statsStore.GetAgariFxOf(p); }
            }
            Color c = tb.agariEffect.GetVariantMainColor(fx);
            if (Mathf.Max(c.r, Mathf.Max(c.g, c.b)) < 0.45f) { c = Color.Lerp(c, tb.agariEffect.GetVariantSubColor(fx), 0.45f); }
            return c;
        }


        private void SetPointText(int row, int v)
        {
            bcShownPts_[row] = v;
            if (bcPointTexts != null && row < bcPointTexts.Length && bcPointTexts[row] != null) { bcPointTexts[row].text = FormatPts(v); }
        }

        // 3桁区切り（32,000 / -8,000）
        private string FormatPts(int v)
        {
            bool neg = v < 0;
            int a = neg ? -v : v;
            string s = "";
            while (a >= 1000)
            {
                int r = a % 1000;
                s = "," + (r < 10 ? "00" : (r < 100 ? "0" : "")) + r + s;
                a /= 1000;
            }
            s = a + s;
            return neg ? "-" + s : s;
        }

        // 点数が動いた：増えた人がいる動き（和了・流局の支払い）は増減を出してから数える。
        // 立直棒の 1000 点のように減るだけの動きは、増減を出さずにすぐ数える
        // 点数が動いた（参考動画「点数移動UI」の順）：名前が消える → 和了者の増分 → 払った人の減分 →
        // 増減が点数へ沈んで数える → 名前が戻る →（和了なら）帯を消して和了者のパネル＋役。
        // 増減は 2 段：上段＝供託・立直棒（和了者は取った供託−自分の棒）、下段＝それ以外（和了・支払い・本場）
        private void StartScoreAnim(Mahjong tb, int n, int kyoDrop)
        {
            float now = Time.time;
            // 和了者のパネルが残っていれば畳む（ふつうは卓のあがり画面が閉じた時点で畳み始めている）
            if (winPending_ && winReleaseAt_ < 0f) { winReleaseAt_ = now; }
            bool anyPlus = false;
            bool anyPend = false;
            int head = -1;
            int best = 0;
            for (int row = 0; row < n; ++row)
            {
                int total = bcLastPts_[row] - bcFromPts_[row];
                if (total > 0) { anyPlus = true; }
                if (total > best) { best = total; head = row; }
                if (bcPendRiichi_[row] > 0) { anyPend = true; }
            }
            bool full = anyPlus || anyPend;
            bcAnimActive_ = true;
            bcAnimStart_ = now;
            bcAnimCountAt_ = full ? bcCountAt : 0f;
            bcAnimSnap_ = bcCountSnap;
            bcAnimTau_ = bcCountTau;
            bcAnimPlusAt_ = bcPlusAt;
            bcAnimMinusAt_ = bcMinusAt;
            // 数え込みの前半で、増減は点数の上へ沈みながら消える
            bcAnimSink_ = bcCountSnap * 0.6f;
            for (int row = 0; row < 4; ++row)
            {
                int total = row < n ? bcLastPts_[row] - bcFromPts_[row] : 0;
                int sub = row < n ? (row == head ? kyoDrop : 0) - bcPendRiichi_[row] : 0;
                bcSubDelta_[row] = sub;
                bcMainDelta_[row] = total - sub;
                bcPendRiichi_[row] = 0;
                if (row < n) { SetPointText(row, bcFromPts_[row]); }
                SetDeltaText(bcDeltaTexts, row, full ? bcMainDelta_[row] : 0);
                SetDeltaText(bcDeltaSubTexts, row, full ? bcSubDelta_[row] : 0);
            }
            if (anyPlus && bcAgariThisRound_ && head >= 0)
            {
                int seat = BcSeatOfRow(tb, head, n);
                if (viewMode_ == 0) { QueueScoreShots(tb, n, now, seat); }
            }
            if (anyPlus) { bcAgariThisRound_ = false; }
            UpdateScoreAnim(now);
        }

        private void SetDeltaText(TextMeshProUGUI[] arr, int row, int d)
        {
            if (arr == null || row >= arr.Length || arr[row] == null) { return; }
            var dt = arr[row];
            dt.gameObject.SetActive(false);
            if (d == 0) { dt.text = ""; return; }
            dt.text = (d > 0 ? "+" : "-") + FormatPts(d > 0 ? d : -d);
            dt.color = d > 0 ? deltaPlus : deltaMinus;
        }

        // 卓の実値が動いたかを見て、帯の演出を始める。立直棒だけの動きは Mリーグと同じく帯を動かさず、
        // 次の点数移動で「-1,000」の段として出す
        private void UpdateBroadcastPoints(Mahjong tb, int n)
        {
            int kyo = tb.kyotaku;
            if (!bcPtsInit_)
            {
                for (int row = 0; row < n; ++row)
                {
                    bcLastPts_[row] = bcCurPts_[row];
                    bcFromPts_[row] = bcCurPts_[row];
                    bcPendRiichi_[row] = 0;
                    SetPointText(row, bcCurPts_[row]);
                }
                bcPrevKyotaku_ = kyo;
                bcPtsInit_ = true;
                return;
            }

            // 卓は和了の点数をあがり画面の最中に動かし、精算パネルはあがり画面が閉じる（NEXT）まで待たせる。
            // 帯の点数移動も卓の精算パネルと同時にしたいので、それまでは動いた点数を帯に反映しない
            if (WaitTableSettlement(tb)) { return; }

            bool changed = false;
            bool anyPlus = false;
            for (int row = 0; row < n; ++row)
            {
                int d = bcCurPts_[row] - bcLastPts_[row];
                if (d != 0) { changed = true; }
                if (d > 0) { anyPlus = true; }
            }
            // 減っただけ（増えた人がいない）動きが、立直した人の 1000 点単位だけなら立直棒
            bool riichiOnly = changed && !anyPlus;
            for (int row = 0; row < n && riichiOnly; ++row)
            {
                int d = bcCurPts_[row] - bcLastPts_[row];
                if (d == 0) { continue; }
                if (d % 1000 != 0 || tb.Spectate_ReachIndex(BcSeatOfRow(tb, row, n)) == -1) { riichiOnly = false; }
            }
            int kyoDrop = bcPrevKyotaku_ > kyo ? bcPrevKyotaku_ - kyo : 0;
            bcPrevKyotaku_ = kyo;

            if (changed && riichiOnly && !anyPlus)
            {
                for (int row = 0; row < n; ++row)
                {
                    int d = bcCurPts_[row] - bcLastPts_[row];
                    if (d < 0) { bcPendRiichi_[row] += -d; }
                    bcLastPts_[row] = bcCurPts_[row];
                }
                return;
            }
            if (changed)
            {
                for (int row = 0; row < n; ++row)
                {
                    bcFromPts_[row] = bcShownPts_[row];
                    bcLastPts_[row] = bcCurPts_[row];
                }
                StartScoreAnim(tb, n, kyoDrop);
            }
            else if (!bcAnimActive_)
            {
                for (int row = 0; row < n; ++row) { SetPointText(row, bcShownPts_[row]); }
            }
        }

        // 卓の精算パネルを待つ間か：和了の状態（あがり画面）の間と、精算パネルが出る順番待ちの間。
        // 卓が精算を出さない動き（立直棒・途中参加の追いつき等）は待たない
        private bool WaitTableSettlement(Mahjong tb)
        {
            if (tb == null) { return false; }
            if (tb.gameState == GS_TSUMO || tb.gameState == GS_RON) { return true; }
            var ps = tb.presentationScreen;
            return ps != null && ps.IsSettlementVisible() && ps.Spectate_SettlementCountStartTime() < 0f;
        }

        // 局の頭：精算で消化されなかった立直棒を「-1,000」として帯に反映する
        private void FlushPendingRiichi(Mahjong tb, int n)
        {
            if (!bcPtsInit_ || bcAnimActive_) { return; }
            bool any = false;
            for (int row = 0; row < n; ++row) { if (bcPendRiichi_[row] > 0) { any = true; } }
            if (!any) { return; }
            for (int row = 0; row < n; ++row) { bcFromPts_[row] = bcShownPts_[row]; }
            StartScoreAnim(tb, n, 0);
        }

        private void UpdateScoreAnim(float now)
        {
            if (!bcAnimActive_) { return; }
            var tb = watched_;
            int n = (tb != null && tb.numPlayers >= 3 && tb.numPlayers <= 4) ? tb.numPlayers : 4;
            float t = now - bcAnimStart_;
            float countAt = bcAnimCountAt_;
            float fade = bcLabelFade > 0.01f ? bcLabelFade : 0.01f;
            float k = t - countAt;
            float back = countAt > 0f ? bcLabelBackDelay : 0f;
            bool done = k >= bcAnimSnap_ && (countAt <= 0f || k >= back + fade);

            // 名前と風：増減を出している間は消し、数え始めて少ししてから戻す
            float la = 1f;
            if (countAt > 0f && !done)
            {
                la = t < countAt ? 1f - Mathf.Clamp01(t / fade) : Mathf.Clamp01((k - back) / fade);
            }
            // 数え方：残りが exp(-k/τ) で減る。τ は参考動画の実測（0.2 秒刻みで 26→78→94→98.5%）
            float tau = bcAnimTau_ > 0.01f ? bcAnimTau_ : 0.01f;
            float rest = k <= 0f ? 1f : (k >= bcAnimSnap_ ? 0f : Mathf.Exp(-k / tau));
            // 数え始めから bcAnimSink_ 秒で、増減は点数の上へ沈みながら消える
            float sinkDur = bcAnimSink_ > 0.01f ? bcAnimSink_ : 0.01f;
            float sink = countAt > 0f ? Mathf.Clamp01(k / sinkDur) : 1f;

            for (int row = 0; row < 4; ++row)
            {
                if (bcLabelGroups != null && row < bcLabelGroups.Length && bcLabelGroups[row] != null) { bcLabelGroups[row].alpha = la; }
                if (row >= n) { continue; }
                int from = bcFromPts_[row];
                int to = bcLastPts_[row];
                int shown = done ? to : to - Mathf.RoundToInt((to - from) * rest);
                if (shown != bcShownPts_[row]) { SetPointText(row, shown); }

                // 和了者（増えた人）は先、払った人は後。上段と下段は同時に出る
                float at = (to - from) > 0 ? bcAnimPlusAt_ : bcAnimMinusAt_;
                AnimDelta(bcDeltaTexts, row, bcMainDelta_[row], t, at, countAt, sink, bcDeltaY);
                AnimDelta(bcDeltaSubTexts, row, bcSubDelta_[row], t, at, countAt, sink, bcDeltaSubY);
            }
            if (done) { bcAnimActive_ = false; }
        }

        private void AnimDelta(TextMeshProUGUI[] arr, int row, int d, float t, float at, float countAt, float sink, float baseY)
        {
            if (arr == null || row >= arr.Length || arr[row] == null) { return; }
            var dt = arr[row];
            bool on = countAt > 0f && d != 0 && t >= at && sink < 1f;
            if (dt.gameObject.activeSelf != on) { dt.gameObject.SetActive(on); }
            if (!on) { return; }
            Color c = d > 0 ? deltaPlus : deltaMinus;
            float p = Mathf.Clamp01((t - at) / 0.18f);
            // TMP の .alpha は Udon 非公開＝color ごと入れる
            dt.color = new Color(c.r, c.g, c.b, (1f - sink) * EaseOut(p));
            Vector3 lp = dt.transform.localPosition;
            dt.transform.localPosition = new Vector3(lp.x, baseY - 44f * sink, 0f);
            dt.transform.localScale = Vector3.one * (1f + 0.2f * (1f - EaseOut(p)));
        }

        // 和了の後の点数移動のカメラ（Mリーグ式）：和了者の顔（＋が出る）→ 放銃者の顔（−が出る）
        // → 他家の顔（点数が回る）→ 和了者の顔 → 対面の背中越しに和了者。局の開始の全景はこの後に回す
        // 和了の後の点数移動のカメラ（参考動画「点数移動UI」）：数え終わるまで和了者の顔 → 全景。
        // 和了者のパネルを出し終えるまで、局の開始の全景（→ドラ→顔）は待たせる
        private void QueueScoreShots(Mahjong tb, int n, float now, int winner)
        {
            // 数え終わるまで和了者の顔
            float faceDur = bcAnimCountAt_ + Mathf.Max(bcAnimSnap_, bcLabelBackDelay) + 0.3f;
            SeqClear();
            SeqAddFace(tb, winner, faceDur, FACE_FRONT);
            SeqAdd(SHOT_WIDE, -1, 30f, 0);
            SeqStart(now);
            bcScoreSeqEnd_ = now + faceDur;
            // 同じ更新で局の開始を積んだ直後なら、そちらは後へ回す
            if (now - bcRoundOpenQueuedAt_ < 1.0f) { bcRoundOpenPending_ = true; }
        }

        // seat と除外 2 席以外で、その下家から最初に見つかる人間の席（ロンの和了者は除く）。無ければ -1
        private int FindOtherHuman(Mahjong tb, int n, int seat, int ex1, int ex2)
        {
            for (int r = 0; r < n; ++r)
            {
                int s = (seat + 1 + r) % n;
                if (s == seat || s == ex1 || s == ex2) { continue; }
                if (tb.Spectate_IsRonWinner(s)) { continue; }
                if (IsHumanSeat(tb, s)) { return s; }
            }
            return -1;
        }

    }
}
