using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    //  役満カットイン（雀魂スタイル）
    // ------------------------------------------------------------
    //  雀魂の役満演出を動画から分解すると、役が変わってもタイムラインは同一で、
    //  変わるのは「背景イラスト1枚」「役名の字」「光の色」の3点だけだった。
    //  そこで演出コードは1本にし、役IDでテクスチャと色を引く形にしてある。
    //
    //  段取り（雀魂実測に合わせて全体 8.0秒。段の配分も実測フレームどおり）:
    //    1. 暗幕がパネル全面に閉じる      … 雀魂の「暗転」の代替
    //    2. 金色の光弾が下から打ち上がる
    //    3. 横一文字の閃光
    //    4. 背景イラストが白飛びからフェードイン
    //    5. 役名が左から書き出される（筆先を光球が走る）
    //    6. 「役満」「二倍役満」がポップ
    //    7. ホールド → フェードアウト → あがり画面へ
    //
    //  なぜ全画面ではなく卓上パネルなのか:
    //    VRChat には Screen Space Overlay が無く、他プレイヤーの視界を暗転させる
    //    手段も無い。雀魂の「暗転＋全画面」は原理的に再現できないので、
    //    卓の上に大きな不透明パネルを出し、パネル自身が暗幕を持つことで代替する。
    //    卓ごとに閉じるので8卓・近接カリングとも整合する。
    //
    //  設計思想（MahjongCallEffect / MahjongAgariEffect と同じ）:
    //   ・純ローカル表示。同期変数を一切持たない（NoVariableSync）。
    //     役満かどうかは同期済みの handState から各クライアントが自前で判定できる
    //     （あがり画面が既に同じ情報でローカル描画している）＝追加同期ゼロ。
    //   ・パーティクルは使わず World Space Canvas 上の RawImage / Text を
    //     RectTransform と color.a だけで動かす（Quest基準）。
    //   ・スクリプトが載るルートは常時アクティブ。表示ON/OFFは子の fxRoot だけで行う
    //     （非アクティブな UdonBehaviour は外部から叩けない）。
    //   ・テクスチャ未割当でも成立する。背景は defaultBackground、役名は Text に
    //     フォールバックするので、素材が揃う前からタイミング調整ができる。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class MahjongYakumanCutin : UdonSharpBehaviour
    {
        [Header("必須参照")]
        [Tooltip("演出全体のルート。非アクティブにして置いておく（演出中だけONになる）。")]
        public GameObject fxRoot;

        [Tooltip("Canvas のルート。演出中は毎フレーム自分の方へ水平ビルボードする。")]
        public Transform billboardRoot;

        [Header("ステージ（専用カメラで撮ってパネルに映す）")]
        [Tooltip("導入を撮る専用カメラ。普段は無効で、演出の導入中だけ回す。"
            + "卓から離れた場所のステージを、専用レイヤーだけ・単色クリアで撮る。")]
        public Camera stageCamera;

        [Tooltip("カメラ映像（RenderTexture）を映す面。導入の間だけ出す。")]
        public RawImage stageViewImage;
        public RectTransform stageViewRect;

        [Header("バースト（パーティクル）")]
        [Tooltip("和了牌が打ち上がる瞬間に1回だけ鳴らす。位置は牌に合わせる。")]
        public ParticleSystem launchBurst;

        [Tooltip("全面フラッシュの瞬間に1回だけ鳴らす。")]
        public ParticleSystem flashBurst;

        [Header("パネル")]
        [Tooltip("暗幕。雀魂の暗転の代替。最初に閉じて背景の下地になる。")]
        public RawImage vignetteImage;

        [Tooltip("役ごとの背景イラスト。未割当なら defaultBackground。")]
        public RawImage bgImage;
        public RectTransform bgRect;

        [Tooltip("横一文字の閃光。背景が出る直前に走る。")]
        public RawImage flashImage;
        public RectTransform flashRect;

        [Header("役名（筆文字）")]
        [Tooltip("役名を画像で出す場合の RawImage。pivot は左端(0,0.5)。テクスチャが引けた時だけ使う。")]
        public RawImage nameImage;
        public RectTransform nameRect;

        [Tooltip("役名テクスチャが無いときのフォールバック。筆フォント推奨。")]
        public Text nameText;
        public RectTransform nameTextRect;

        [Tooltip("役名の影（読みやすさ用）。nameText と同じ文字を1フレーム遅れずに出す。")]
        public Text nameShadowText;
        public RectTransform nameShadowRect;

        [Tooltip("筆先を走る光球。書き出し位置に追従する。")]
        public RawImage brushGlowImage;
        public RectTransform brushGlowRect;

        [Header("倍率ラベル")]
        [Tooltip("「役満」「二倍役満」等。Mahjong.cs 側から翻訳済みの文字列で渡される。")]
        public Text rankText;
        public RectTransform rankRect;

        [Header("光弾")]
        [Tooltip("下から打ち上がる光の粒。6〜10個程度。")]
        public RawImage[] sparkImages;
        public RectTransform[] sparkRects;

        [Header("花びら（導入で勝者キャラと一緒に舞う）")]
        [Tooltip("舞い落ちる花びら。8枚程度。")]
        public RawImage[] petalImages;
        public RectTransform[] petalRects;

        [Tooltip("花びらの色。")]
        public Color petalColor = new Color(1.00f, 0.72f, 0.82f, 1.0f);

        [Tooltip("花びらが落ちる距離（Canvas単位）。")]
        public float petalDrift = 300.0f;

        [Header("牌の彗星（導入で開いて閃光へ収束する）")]
        [Tooltip("彗星状に舞う牌。5〜6枚。和了手の牌をそのまま使う。")]
        public RawImage[] cometTileImages;
        public RectTransform[] cometTileRects;

        [Tooltip("牌の横の散らばり幅（Canvas単位）。真っ直ぐ上へ飛ぶので横は散らすだけ。")]
        public float cometRadius = 430.0f;

        [Tooltip("和了牌の周りを何周するか。参考動画は1周弱で寄り切る。")]
        public float cometOrbitTurns = 0.85f;

        [Tooltip("周回の縦つぶし。1で真円、小さいほど寝かせた輪に見える。")]
        public float cometOrbitFlatten = 0.62f;

        [Tooltip("和了牌の方を向く度合い。1で常に正面を和了牌へ向ける。0で常に正立。")]
        public float cometTilt = 1.0f;

        [Tooltip("牌が下から上へ飛ぶ距離（Canvas単位）。")]
        public float cometRise = 980.0f;

        [Tooltip("飛び去ったときの小ささ（1=等倍）。遠ざかる感じを作る。")]
        public float cometRecede = 0.26f;

        [Tooltip("各牌の下に付く炎の尾。牌と同じ枚数。")]
        public RawImage[] cometTrailImages;
        public RectTransform[] cometTrailRects;

        [Header("牌の光暈（牌そのものは不透明で光らせられないので背後に置く）")]
        [Tooltip("主役牌の光暈。")]
        public RawImage winTileGlowImage;
        public RectTransform winTileGlowRect;

        [Tooltip("主役牌の炎の尾。参考動画では牌の真下に大きなプルームが出ている。")]
        public RawImage winTileTrailImage;
        public RectTransform winTileTrailRect;

        [Tooltip("光弾（背景の筋）の濃さ。1だと独立した黄色い棒に見えて動画と違う。")]
        public float sparkAlpha = 0.22f;

        [Header("奥行き")]
        [Tooltip("ONで実際に手前／奥へ動かす（遠近が本物になる）。OFFなら拡大縮小で近似。")]
        public bool useStageDepth = true;

        [Tooltip("ステージカメラから Canvas までの距離（Canvas単位）。"
            + "StageCamDistance 12.4m ÷ StageCanvasScale 0.01 = 1240。")]
        public float stageDepthBase = 1240.0f;

        [Header("導入のアニメーション")]
        [Tooltip("導入ステージを Animation Clip で動かす。"
            + "未設定ならコード側の振り付け（ApplyWinningTile 等）がそのまま使われる。")]
        public Animator introAnimator;

        [Tooltip("クリップが入っているステート名。")]
        public string introStateName = "Intro";

        [Header("カメラワーク")]
        [Tooltip("ONでステージカメラを振り付ける。OFFで従来どおり固定。"
            + "パネルに映す映像なので、揺らしてもVR酔いの原因にはならない。")]
        public bool cinematicCamera = true;

        [Tooltip("開始の高さ（m・正で和了牌より上）。ここから見下ろす。")]
        public float camRiseFrom = 2.6f;

        [Tooltip("終わりの高さ（m・負で和了牌より下）。ここまで降りて見上げる。")]
        public float camRiseTo = -6.5f;

        [Tooltip("滞空の間に寄る距離（m）。大きいと牌が膨らみすぎる。")]
        public float camPushIn = 0.6f;

        [Tooltip("打ち上げで引く距離（m）。")]
        public float camPullBack = 0.8f;

        [Tooltip("打ち上げで足す画角（度）。開くと速度感が出る。")]
        public float camFovPunch = 12.0f;

        [Tooltip("打ち上げで狙点を牌の下へずらす量（m）。"
            + "牌が画面上へ逃げるほど速く見える。0で常に中央。")]
        public float camAimLead = 1.1f;

        [Tooltip("傾き（度）。終盤ほど深く傾ける。参考動画は最後に25度近く倒れている。")]
        public float camRoll = 22.0f;

        [Tooltip("手持ちの揺らぎ（m）。0で完全固定。")]
        public float camDrift = 0.09f;

        [Tooltip("打ち上げの衝撃で揺れる幅（m）。")]
        public float camShake = 0.13f;

        [Tooltip("衝撃の揺れが収まるまでの秒数。")]
        public float camShakeSeconds = 0.38f;

        [Tooltip("背景をゆっくり引き続ける量。静止画のまま止めると後半が間延びする。")]
        public float bgKenBurns = 0.045f;

        [Tooltip("彗星の牌それぞれの光暈。牌と同じ枚数。")]
        public RawImage[] cometGlowImages;
        public RectTransform[] cometGlowRects;

        [Tooltip("光暈の色。動画は白飛びした金色。")]
        public Color tileGlowColor = new Color(1.00f, 0.86f, 0.45f, 1.0f);

        [Tooltip("光暈の大きさ（牌の何倍か）。")]
        public float tileGlowScale = 2.6f;

        [Header("導入の牌を実物の3D牌で出す")]
        [Tooltip("ONにすると和了牌と彗星をパネル上の画像ではなく卓の実物3D牌で見せる。"
            + "位置決めはここが行い、実際に牌を置くのは Mahjong.cs（牌の所有者）。")]
        public bool useRealIntroTiles = true;

        [Tooltip("実物3D牌をパネル上の画像と同じ見かけの大きさにするための倍率。")]
        public float realTileScale = 2.4f;

        [Tooltip("実物3D牌の向き補正（度）。牌のメッシュの正面とパネルの正面を合わせる。")]
        public Vector3 realTileEuler = new Vector3(0.0f, 0.0f, 0.0f);

        [Tooltip("実物3D牌をパネル面から手前へどれだけ浮かせるか（m）。"
            + "0だとCanvasと同一平面でZファイティングする。")]
        public float realTileDepth = 0.035f;

        [Header("和了牌の浮上と打ち上げ（牌エフェクトと同時）")]
        [Tooltip("牌が浮くときの傾き（度）。")]
        public float winTileTiltDegrees = 26.0f;

        [Tooltip("浮いているときの高さ（m）。")]
        public float winTileFloatHeight = 0.035f;

        [Tooltip("打ち上がったときの高さ（m）。")]
        public float winTileLaunchHeight = 0.90f;

        [Tooltip("浮上→打ち上げに移る時刻（エフェクト全体を 0..1 とした割合）。")]
        public float winTileLaunchFrom = 0.72f;

        [Tooltip("浮いている間の上下の揺れ幅（m）。")]
        public float winTileBob = 0.004f;

        [Header("和了手の掃き出し（カットインの直前）")]
        [Tooltip("和了者の牌が小刻みに震える時間（秒）。")]
        public float sweepShakeSeconds = 0.12f;

        [Tooltip("震えたあと上へ上がって消えるまでの時間（秒）。")]
        public float sweepRiseSeconds = 0.18f;

        [Tooltip("震えの振れ幅（m）。")]
        public float sweepShakeAmp = 0.0045f;

        [Tooltip("震えの速さ（Hz相当）。")]
        public float sweepShakeSpeed = 68.0f;

        [Tooltip("上へ上がる距離（m）。")]
        public float sweepRise = 0.32f;

        [Header("和了牌と手牌の打ち上げ（Animation Clip 駆動）")]
        [Tooltip("打ち上げをクリップで動かす。未設定なら上の数値（浮上・掃き出し）がそのまま使われる。")]
        public Animator flyAnimator;

        [Tooltip("クリップが入っているステート名。")]
        public string flyStateName = "Fly";

        [Tooltip("打ち上げの長さ（秒）。クリップの長さと揃えること。")]
        public float flySeconds = 2.20f;

        [Tooltip("和了牌のスロット。localPosition=席相対のずらし量(m)／"
            + "localEulerAngles=元の向きからの回転(度)／localScale.x=大きさの倍率(0で消える)。")]
        public Transform flyWinTile;

        [Tooltip("手牌のスロット。並び順は 門前→副露。足りないぶんは先頭から回して使う。")]
        public Transform[] flyHandSlots;

        [Header("打ち上げの彗星トレイル")]
        [Tooltip("トレイル一式の親。和了牌の世界座標へ毎フレーム置く。既定は非アクティブ。")]
        public Transform flyComet;

        [Tooltip("親の下にあるトレイル本体。開始時に Clear するために直接持つ。")]
        public TrailRenderer[] flyCometTrails;

        [Tooltip("尾を点ける瞬間。クリップの正規化時刻(0..1)。既定は89フレーム目"
            + "（60fps・全125フレーム＝0.712）。Wire がクリップの実尺から計算して入れる。")]
        public float cometStartT = 0.712f;

        [Tooltip("点火した瞬間に敷く尾の長さ（m）。0で「溜まるのを待つ」従来動作。"
            + "溜まるのを待つと点火から数フレームは芯だけの短い線になる。")]
        public float cometSeedLength = 0.20f;

        [Tooltip("敷く点の数。多いほど滑らかだが、点火の1フレームだけの処理なので安い。")]
        public int cometSeedPoints = 8;

        [Tooltip("和了牌からのずらし量（m・世界座標）。牌の中心から出すと芯が牌に隠れる。")]
        public Vector3 cometOffset = Vector3.zero;

        [Header("打ち上げの彗星のコマ（核を包む光）")]
        // 彗星は「核（Nucleus）＝牌」だけでは彗星に見えない。核から吹き出した
        // ガスと塵が核を包む球状の光＝コマ（Coma）になり、それが後ろへ流されて
        // 尾（Tail）になる。尾は TrailRenderer が持っているので、ここで足すのは
        // 核を包むほうだけ。
        [Tooltip("コマ一式の親。FlyComet の子。毎フレーム牌の位置へ置いて視点の方へ向ける。")]
        public Transform flyComa;

        [Tooltip("重ねる加算の板。並び順は 芯 → 外套（内側から外側へ）。")]
        public MeshRenderer[] flyComaLayers;

        [Tooltip("各層の大きさ＝板の一辺（m）。flyComaLayers と同じ並び。牌は約3cm。"
            + "0 を入れるとその層は組み立て時の localScale がそのまま使われる。")]
        public float[] flyComaSizes;

        [Tooltip("コマ全体の大きさの倍率。ここだけ触れば3層まとめて大小できる。")]
        public float comaScale = 1.0f;

        [Tooltip("各層の色。flyComaLayers と同じ並び。")]
        public Color[] flyComaColors;

        [Tooltip("各層の濃さ。flyComaLayers と同じ並び。")]
        public float[] flyComaAlphas;

        [Tooltip("牌より手前へ出す量（m）。0だと不透明な牌に中心を食われて穴が開く。")]
        public float comaFront = 0.018f;

        [Tooltip("点いてから開ききるまで（秒）。")]
        public float comaFadeInSeconds = 0.08f;

        [Tooltip("点いた瞬間だけの増光（倍）。0で無し。")]
        public float comaIgnitionFlash = 0.7f;

        [Tooltip("この速さ（m/s）でコマが膨らみきる。打ち上げは約1mを0.67秒＝最大3m/s前後。")]
        public float comaSpeedFull = 2.5f;

        [Tooltip("速いほど膨らむ量（外套で最大）。実際の彗星も太陽に近づくほどコマが育つ。")]
        public float comaSpeedGain = 0.5f;

        [Tooltip("進行方向へ伸びる量（外套で最大）。")]
        public float comaStretch = 0.6f;

        [Tooltip("外側の層ほど後ろへ流れる量（自分の大きさに対する比）。")]
        public float comaLag = 0.35f;

        [Tooltip("ゆらぎの振れ幅（倍）と速さ（Hz相当）。")]
        public float comaFlicker = 0.06f;
        public float comaFlickerHz = 24.0f;

        [Header("打ち上げのSE")]
        [Tooltip("打ち上げ全体（クリップと同じ2.083秒）を1本で覆う音。"
            + "これが入っていると溜め／発射の2本は鳴らさない（二重に鳴るのを防ぐ）。")]
        public AudioClip flyFullSound;
        public float flyFullVolume = 0.0565f;

        [Tooltip("溜め。打ち上げの頭（0秒）で鳴る。牌が卓上で浮いている約1.42秒ぶん。"
            + "flyFullSound が入っているときは使われない。")]
        public AudioClip flyChargeSound;
        public float flyChargeVolume = 0.085f;

        [Tooltip("発射。牌が実際に上がり始める瞬間に鳴る。既定は85フレーム目（1.417秒）。")]
        public AudioClip flyLaunchSound;
        public float flyLaunchVolume = 0.12f;

        [Tooltip("発射SEを鳴らす正規化時刻(0..1)。Wire がクリップの実尺から計算して入れる。"
            + "彗星の点火（cometStartT）より少し前＝牌が動き出す瞬間。")]
        public float flyLaunchT = 0.676f;
        [Header("動的な勝者・和了手")]
        [Tooltip("暗紺の導入中に短く表示する勝者キャラ。")]
        public RawImage mascotImage;
        public RectTransform mascotRect;
        public Texture[] charaMascotTextures;

        [Tooltip("自画像キャラの共有ストア（任意）。割り当てると、自画像を選んでいる人は"
                 + "その人が撮った写真をキャラ絵として出す。未割当なら既定のキャラ絵に落ちる。")]
        public SelfiePortraitStore selfieStore;

        // 自画像キャラ（CHARA_SELFIE）を選んでいる人は、その人が PlayerData へ焼いた
        // 写真をキャラ絵として使う。写真が無い／ストア未割当なら null を返して従来の絵へ落ちる。
        private Texture ResolveSelfieTexture(int chara, int playerId)
        {
            if (selfieStore == null) { return null; }
            if (chara != PlayerStatsStore.CHARA_SELFIE) { return null; }
            if (playerId <= 0) { return null; }

            var player = VRCPlayerApi.GetPlayerById(playerId);
            if (player == null || !player.IsValid()) { return null; }

            return selfieStore.GetPortraitOf(player);
        }

        // 和了者のプレイヤーID。Mahjong 側が SetHandData の直前に入れる（0＝CPU/不明）。
        private int winnerPlayerId_ = 0;

        public void SetWinnerPlayerId(int playerId) { winnerPlayerId_ = playerId; }

        public Texture defaultMascotTexture;

        // ---- 立ち絵の左→右フェード（Mascot の上に重ねる別パネル）----
        // 焼き込み済みの導入クリップ（YakumanIntro.anim）は Mascot を動かしているので、
        // そこへ手を入れずに済むよう、帯に切った専用パネルを重ねてこちらで出す。
        [Tooltip("立ち絵を帯に切って左から順に出す専用パネル。Mascot の1つ上に重ねる。")]
        public RectTransform mascotWipeRoot;

        [Tooltip("帯パネルの短冊。左端から順に並べる。")]
        public RawImage[] mascotWipeStrips;

        [Tooltip("true なら帯パネルで立ち絵を出す（従来の Mascot は隠す）。false で元の挙動へ戻せる。")]
        public bool mascotWipeEnabled = true;

        [Tooltip("左端の帯が出てから右端の帯が出始めるまで（立ち絵の持ち時間に対する割合）。")]
        public float mascotWipeSpread = 0.14f;

        [Tooltip("帯1本が出きるまで（同上の割合）。大きいほど境目がぼやける。")]
        public float mascotWipeStripFade = 0.07f;

        [Tooltip("出るときに右へずらす量（px）。0 でずらさない。")]
        public float mascotWipeSlide = 34.0f;

        [Tooltip("出るときの拡大率。1.0 で拡大しない。")]
        public float mascotWipeZoom = 1.04f;

        [Tooltip("導入で彗星の核として使う実際の和了牌。")]
        public RawImage winningTileImage;
        public RectTransform winningTileRect;

        [Header("スタジオ単体版（役満ポスター用）")]
        [Tooltip("卓に付いていない単体のリグとして回す。ONにするとステージだけで絵が完結する：\n"
            + "・和了牌をステージ上の RawImage で描く（実物の3D牌が無いため）\n"
            + "・立ち絵／花びら／光の粒もステージ側で出す（帯ワイプはパネル側なので撮らない）\n"
            + "卓のカットインでは必ずOFF。ONにすると実物の牌と二重に出る。")]
        public bool standaloneStage = false;

        [Tooltip("スタジオ単体版で和了牌を描く大きさ（Canvas単位）。"
            + "クリップは実物の牌を使う前提で 0 を書いているので、ここで上書きする。")]
        public Vector2 imageWinTileSize = new Vector2(126.0f, 184.0f);

        [Tooltip("役別カード上へ並べる手牌。18枚を事前生成して使い回す。")]
        public RawImage[] handTileImages;
        public RectTransform[] handTileRects;
        public Texture tileAtlas;
        public Color faceDownColor = new Color(0.45f, 0.60f, 0.90f, 1.0f);

        [Header("役名の第2レイヤー")]
        [Tooltip("国士無双十三面待ちなど、役名を2ブロックに分ける場合の表示先。")]
        public Text nameSecondaryText;
        public RectTransform nameSecondaryTextRect;
        public Text nameSecondaryShadowText;
        public RectTransform nameSecondaryShadowRect;

        [Header("役別レイアウト")]
        [Tooltip("0=下弧、1=上弧、2=右上がり、3=中央の緩い斜線。yakuIds と同じ並び。")]
        public int[] yakuHandPathModes;
        [Tooltip("0=標準、1=長い横書き、2=左右分割、3=中央寄せ。yakuIds と同じ並び。")]
        public int[] yakuTitleLayoutModes;



        // ---- 役ごとのデータ（3つの配列は同じ長さ・同じ並びにすること）----
        [Header("役ごとのデータ")]
        [Tooltip("役ID。Mahjong.cs の YAKU_* と同じ値。例: 16=四暗刻 / 29=字一色 / 32=国士無双")]
        public int[] yakuIds;

        [Tooltip("役IDに対応する背景イラスト。yakuIds と同じ並び。未割当の枠は defaultBackground。")]
        public Texture[] yakuBackgrounds;

        [Tooltip("役IDに対応する筆文字テクスチャ。yakuIds と同じ並び。未割当なら nameText で描く。")]
        public Texture[] yakuNameTextures;

        [Tooltip("役IDに対応する光・文字の色。yakuIds と同じ並び。長さが足りない枠は defaultColor。")]
        public Color[] yakuColors;

        [Tooltip("役IDが表に無い／背景が未割当のときに使う背景。")]
        public Texture defaultBackground;

        [Tooltip("色が未割当のときに使う色。")]
        public Color defaultColor = new Color(1.00f, 0.86f, 0.42f, 1.0f);

        [Header("暗幕")]
        [Tooltip("暗幕の色（アルファは最大値として使う）。")]
        public Color vignetteColor = new Color(0.043f, 0.115f, 0.180f, 1.0f);

        [Header("タイミング（秒）")]
        [Tooltip("暗幕が閉じるまで。")]
        public float darkenSeconds = 0.12f;

        [Tooltip("光弾が打ち上がっている時間。")]
        public float sparkSeconds = 2.98f;

        [Tooltip("閃光の時間。")]
        public float flashSeconds = 0.16f;

        [Tooltip("背景イラストのフェードイン。")]
        public float bgFadeSeconds = 0.28f;

        [Tooltip("背景が出てから役名を書き始めるまでの間。")]
        public float writeDelaySeconds = 0.88f;

        [Tooltip("役名を書き切るまで。")]
        public float writeSeconds = 1.55f;

        [Tooltip("役名を書き終えてから倍率ラベルを出すまでの間。")]
        public float rankDelaySeconds = 0.06f;

        [Tooltip("倍率ラベルのポップ。")]
        public float rankSeconds = 0.18f;

        [Tooltip("完成形を見せている時間。")]
        public float holdSeconds = 1.71f;

        [Tooltip("フェードアウト。")]
        public float exitSeconds = 0.20f;

        [Header("見た目の調整")]
        [Tooltip("光弾が下から上へ進む距離（Canvas単位）。")]
        public float sparkRise = 900.0f;

        [Tooltip("光弾の横の散らばり幅（Canvas単位）。")]
        public float sparkSpread = 620.0f;

        [Tooltip("背景の寄り（フェードイン中に少しだけ縮んで落ち着く）。")]
        public float bgZoom = 1.10f;

        [Tooltip("倍率ラベルが飛び出す倍率。")]
        public float rankPunchScale = 1.70f;

        [Header("SE")]
        public AudioSource seSource;

        [Tooltip("暗幕が閉じる瞬間。")]
        public AudioClip startSound;

        [Tooltip("閃光＋背景が出る瞬間。")]
        public AudioClip revealSound;

        [Tooltip("倍率ラベルが出る瞬間。")]
        public AudioClip rankSound;

        [Tooltip("3本まとめての音量。スロット別の値が 0 以下のときだけ使う。")]
        public float seVolume = 0.45f;

        // 素の音圧はクリップごとに違うので、つまみは段ごとに分ける。
        // 正は BuildYakumanCutin の *Volume 定数で、Wire のたびに代入し直される。
        [Tooltip("導入SEの音量。0以下なら seVolume を使う。")]
        public float startVolume = 0.119f;

        [Tooltip("閃光SEの音量。0以下なら seVolume を使う。")]
        public float revealVolume = 0.108f;

        [Tooltip("役名SEの音量。0以下なら seVolume を使う。")]
        public float rankVolume = 0.0885f;

        // ---- 再生中のローカル状態（同期しない）----
        private const int MAX_HAND_SLOTS = 18;
        private int activeHandPathMode_ = 0;
        private int activeTitleLayoutMode_ = 0;
        private int activeWinnerChara_ = -1;
        private int activeWinningTileCode_ = -1;
        private bool splitTitle_ = false;
        private string secondaryNameString_ = "";
        private int secondaryNameLength_ = 0;

        private int[] handCodes_;
        private bool[] handFaceDown_;
        private bool[] handGroupStart_;
        private float[] handUnits_;
        private int handCount_ = 0;
        private int handAgariIndex_ = -1;

        private float[] atlasColX_;
        private float[] atlasRowY_;
        private const float ATLAS_SIZE = 2048.0f;
        private const float ATLAS_INSET = 2.0f;

        private Vector2 mascotHome_ = Vector2.zero;
        private Vector3 mascotHomeScale_ = Vector3.one;
        private Vector2 mascotWipeHome_ = Vector2.zero;
        private Vector3 mascotWipeHomeScale_ = Vector3.one;
        private Vector2 winningTileHome_ = Vector2.zero;
        private Vector3 winningTileHomeScale_ = Vector3.one;
        private float[] petalPhase_;
        private float[] petalSide_;
        private Vector2[] petalHome_;
        private float[] cometAngle_;
        private float trailHalfHeight_ = 95.0f;
        private float winTrailHalfHeight_ = 160.0f;
        private float[] cometAlpha_;      // 彗星の今フレームの可視度（3D牌の出し入れに使う）
        private float[] cometScale_;      // 同・今フレームの大きさ
        private float introWinAlpha_ = 0.0f;   // 和了牌の今フレームの可視度
        private float introWinScale_ = 1.0f;
        private float introWinYOff_ = 0.0f;   // 彗星の周回中心に使う
        private int introStateHash_ = 0;
        private int flyStateHash_ = 0;

        [Header("VR向け調整")]
        [Range(0.0f, 1.0f)]
        public float fullFlashAlpha = 0.64f;
        [Range(0.0f, 1.0f)]
        public float mascotMaxAlpha = 0.92f;
        public float handEnterSeconds = 0.24f;

        [Tooltip("完成手牌の牌ごとの微小な時間差。左右同時に弾けるよう小さく保つ。")]
        public float handTileStaggerSeconds = 0.003f;

        [Tooltip("和了牌だけを遅れて着地させる時間。")]
        public float handWinningDelaySeconds = 0.028f;

        [Tooltip("完成手牌が最初に密集している高さ（Canvas pixel）。")]
        public float handLaunchHeight = 245.0f;

        [Tooltip("完成手牌の発射時点での横方向の広がり（Canvas pixel）。")]
        public float handLaunchSpread = 92.0f;

        [Tooltip("完成手牌が飛ぶ途中の弧の高さ（Canvas pixel）。")]
        public float handArcHeight = 58.0f;

        [Tooltip("完成手牌が終点を行き過ぎて戻る量（Canvas pixel）。")]
        public float handImpactOvershoot = 18.0f;

        [Range(0.0f, 0.3f)]
        [Tooltip("完成手牌の着地時に出す短い補助フラッシュ。")]
        public float handHitFlashAlpha = 0.09f;

        [Tooltip("役名の1文字ごとの打撃間隔（秒）。")]
        public float titleGlyphInterval = 0.18f;

        [Tooltip("役名着弾時の短い補助フラッシュ。VR向けに低く抑える。")]
        public float titleHitFlashAlpha = 0.14f;

        [Tooltip("UI内容だけを揺らす衝撃時間。視点・カメラは揺らさない。")]
        public float impactShakeSeconds = 0.13f;

        [Tooltip("UI衝撃揺れの最大量（Canvas pixel）。")]
        public float impactShakePixels = 12.0f;

        [Tooltip("役名が着弾する直前の移動量（Canvas pixel）。")]
        public float titleTravelPixels = 42.0f;
        [Tooltip("参考動画では倍率は結果画面側に出るため、通常はOFF。")]
        public bool showRankInCutin = false;


        private float startTime_ = -1.0f;    // <0 = 非表示
        private Color activeColor_ = Color.white;
        private bool useNameImage_ = false;
        private string nameString_ = "";
        private int nameLength_ = 0;
        private bool revealSePlayed_ = false;
        private bool rankSePlayed_ = false;
        private bool launchBurstPlayed_ = false;
        private Vector3 camHome_ = Vector3.zero;
        private Quaternion camHomeRot_ = Quaternion.identity;
        private float camHomeFov_ = 40.0f;
        private bool flashBurstPlayed_ = false;
        private int primaryShown_ = -1;
        private int secondaryShown_ = -1;

        private float[] sparkOffsets_;   // 打ち上げの遅れ（0〜1の相対）
        private float[] sparkX_;         // 横位置
        private float[] sparkScale_;     // 大きさのばらつき

        private bool layoutCached_ = false;
        private Vector2 nameFullSize_ = Vector2.zero;
        private Vector2 nameHome_ = Vector2.zero;
        private Vector3 rankHomeScale_ = Vector3.one;
        private Vector3 bgHomeScale_ = Vector3.one;
        private Vector2 bgHomePosition_ = Vector2.zero;
        private Vector2 nameTextHome_ = Vector2.zero;
        private Vector2 nameShadowHome_ = Vector2.zero;
        private Vector2 nameSecondaryHome_ = Vector2.zero;
        private Vector2 nameSecondaryShadowHome_ = Vector2.zero;
        private Vector2 flashHomeSize_ = Vector2.zero;

        // 彗星トレイル。すべてローカル駆動＝同期変数は増えない。
        private bool cometOn_ = false;
        private bool cometHasPrev_ = false;
        private Vector3 cometPrev_ = Vector3.zero;
        private float cometOffAt_ = -1.0f;
        private float cometTailSeconds_ = 0.0f;

        // コマ（核を包む光）。これも純ローカル＝同期変数は増えない。
        private MaterialPropertyBlock comaMpb_;
        private float[] comaBaseScale_;
        private float comaSpeed_ = 0.0f;

        // 打ち上げSEの発火ラッチ。1回の打ち上げにつき各1回だけ鳴らす。
        private bool flyChargeFired_ = false;
        private bool flyLaunchFired_ = false;

void Start()
        {
            CacheLayout();
            EnsureRuntimeBuffers();

            // コマの基準の大きさは組み立て時の localScale。実行時はこれに倍率を掛ける。
            // CacheLayout は何度でも回るので、ここ（Start）で一度だけ拾う。
            comaMpb_ = new MaterialPropertyBlock();
            int comaN = flyComaLayers != null ? flyComaLayers.Length : 0;
            comaBaseScale_ = new float[comaN];
            for (int i = 0; i < comaN; ++i)
            {
                var cr = flyComaLayers[i];
                comaBaseScale_[i] = (cr != null) ? cr.transform.localScale.x : 0.0f;
            }
            HideComaNow();

            int n = sparkRects != null ? sparkRects.Length : 0;
            sparkOffsets_ = new float[n];
            sparkX_ = new float[n];
            sparkScale_ = new float[n];
            for (int i = 0; i < n; ++i)
            {
                sparkOffsets_[i] = (n <= 1) ? 0.0f : ((float)i / (float)n) * 0.55f;

                float side = (i % 2 == 0) ? 1.0f : -1.0f;
                float step = ((i / 2) + 1) / (float)((n / 2) + 1);
                sparkX_[i] = side * step * sparkSpread * 0.5f;
                sparkScale_[i] = 0.70f + 0.22f * (i % 3);
            }

            HideNow();
        }

private void CacheLayout()
        {
            if (layoutCached_) { return; }

            if (nameRect != null)
            {
                nameFullSize_ = nameRect.sizeDelta;
                nameHome_ = nameRect.anchoredPosition;
            }
            if (rankRect != null) { rankHomeScale_ = rankRect.localScale; }
            if (bgRect != null)
            {
                bgHomeScale_ = bgRect.localScale;
                bgHomePosition_ = bgRect.anchoredPosition;
            }
            if (flashRect != null) { flashHomeSize_ = flashRect.sizeDelta; }
            if (stageCamera != null)
            {
                camHome_ = stageCamera.transform.localPosition;
                camHomeRot_ = stageCamera.transform.localRotation;
                camHomeFov_ = stageCamera.fieldOfView;
            }
            if (mascotRect != null)
            {
                mascotHome_ = mascotRect.anchoredPosition;
                mascotHomeScale_ = mascotRect.localScale;
            }
            if (mascotWipeRoot != null)
            {
                mascotWipeHome_ = mascotWipeRoot.anchoredPosition;
                mascotWipeHomeScale_ = mascotWipeRoot.localScale;
            }
            if (winningTileRect != null)
            {
                winningTileHome_ = winningTileRect.anchoredPosition;
                winningTileHomeScale_ = winningTileRect.localScale;
            }

            // 花びらの散らし方は決定的に作る（乱数だとクライアントごとに絵が変わる）。
            int pn = petalRects != null ? petalRects.Length : 0;
            petalPhase_ = new float[pn];
            petalSide_ = new float[pn];
            petalHome_ = new Vector2[pn];
            for (int i = 0; i < pn; ++i)
            {
                petalPhase_[i] = pn <= 1 ? 0.0f : (float)i / (float)pn;
                petalSide_[i] = (i % 2 == 0) ? 1.0f : -1.0f;
                petalHome_[i] = petalRects[i] != null ? petalRects[i].anchoredPosition : Vector2.zero;
            }

            // 彗星は和了牌を中心に扇状（真上を挟んで左右へ）。
            if (cometTrailRects != null && cometTrailRects.Length > 0 && cometTrailRects[0] != null)
            {
                trailHalfHeight_ = cometTrailRects[0].sizeDelta.y * 0.5f;
            }
            if (winTileTrailRect != null)
            {
                winTrailHalfHeight_ = winTileTrailRect.sizeDelta.y * 0.5f;
            }

            int cn = cometTileRects != null ? cometTileRects.Length : 0;
            cometAngle_ = new float[cn];
            cometAlpha_ = new float[cn];
            cometScale_ = new float[cn];
            for (int i = 0; i < cn; ++i)
            {
                cometAngle_[i] = cn <= 1 ? 90.0f : (18.0f + (144.0f * i) / (float)(cn - 1));
            }

            layoutCached_ = true;
        }

        private void HideNow()
        {
            startTime_ = -1.0f;
            // 回しっぱなしは Quest で純粋な無駄なので、Animator も必ず止める。
            if (introAnimator != null) { introAnimator.enabled = false; }
            StopFly();

            // カメラは必ず止める。回しっぱなしは Quest で純粋な無駄。
            if (stageCamera != null)
            {
                stageCamera.enabled = false;
                stageCamera.transform.localPosition = camHome_;
                stageCamera.transform.localRotation = camHomeRot_;
                stageCamera.fieldOfView = camHomeFov_;
            }
            if (launchBurst != null) { launchBurst.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); }
            if (flashBurst != null) { flashBurst.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); }
            if (fxRoot != null) { fxRoot.SetActive(false); }
        }

        // ---- 段の開始時刻（すべて演出開始からの相対秒）----
        private float SparkStart() { return Mathf.Max(0.0f, darkenSeconds) * 0.55f; }
        private float FlashStart() { return SparkStart() + Mathf.Max(0.0f, sparkSeconds); }

        // 導入（暗紺空間）の進み具合 0..1。立ち絵・花びら・彗星はすべてこの中で進む。
        //   0.06..0.46 … 勝者キャラ（短時間表示）
        //   0.06..0.58 … 花びら
        //   0.38..1.00 … 牌の彗星（開いて → 中心へ収束し、そのまま閃光へ）
        private float IntroT(float elapsed)
        {
            return (elapsed - SparkStart()) / Mathf.Max(0.0001f, sparkSeconds);
        }
        private float BgStart() { return FlashStart() + Mathf.Max(0.0f, flashSeconds) * 0.35f; }
        private float WriteStart() { return BgStart() + Mathf.Max(0.0f, bgFadeSeconds) + Mathf.Max(0.0f, writeDelaySeconds); }

private float HandStart()
        {
            return BgStart() + Mathf.Max(0.0f, bgFadeSeconds) + 0.30f;
        }

        private float RankStart() { return WriteStart() + Mathf.Max(0.0f, writeSeconds) + Mathf.Max(0.0f, rankDelaySeconds); }
        private float ExitStart() { return RankStart() + Mathf.Max(0.0f, rankSeconds) + Mathf.Max(0.0f, holdSeconds); }

        /// <summary>演出全体の長さ（秒）。Mahjong.cs があがり画面の時刻を後ろへずらすのに使う。</summary>
        public float GetDuration()
        {
            return ExitStart() + Mathf.Max(0.0f, exitSeconds);
        }

        /// <summary>
        /// 導入（ステージ映像）が消え切るまでの秒数。ステージカメラが止まる時刻でもある。
        /// 役満ポスターが「導入を流し終えたら貼る」の切り替え時刻に使う。
        /// </summary>
        public float GetIntroSeconds()
        {
            return BgStart() + Mathf.Max(0.0f, bgFadeSeconds) * 0.6f;
        }

        /// <summary>役満カットインを出す。yakuName / rankLabel は Mahjong.cs 側で翻訳済みの文字列。</summary>
public void Play(int yakuId, string yakuName, string rankLabel)
        {
            if (fxRoot == null) { return; }

            CacheLayout();
            EnsureRuntimeBuffers();

            int slot = ResolveSlot(yakuId);
            activeColor_ = ResolveColor(slot);
            activeHandPathMode_ = ResolveInt(yakuHandPathModes, slot, 0);
            activeTitleLayoutMode_ = ResolveInt(yakuTitleLayoutModes, slot, 0);

            Texture bg = ResolveBackground(slot);
            if (bgImage != null)
            {
                bgImage.texture = bg;
                bgImage.color = new Color(1.0f, 1.0f, 1.0f, 0.0f);
                bgImage.gameObject.SetActive(bg != null);
            }

            nameString_ = yakuName != null ? yakuName : "";
            secondaryNameString_ = "";
            splitTitle_ = activeTitleLayoutMode_ == 2 && nameString_.IndexOf("国士") >= 0;
            if (splitTitle_ && nameString_.Length > 4)
            {
                secondaryNameString_ = BuildVerticalText(nameString_.Substring(4));
                nameString_ = BuildVerticalText(nameString_.Substring(0, 4));
            }

            nameLength_ = nameString_.Length;
            secondaryNameLength_ = secondaryNameString_.Length;
            primaryShown_ = -1;
            secondaryShown_ = -1;
            ConfigureTitleLayout();

            Texture nameTex = ResolveNameTexture(slot);
            useNameImage_ = nameTex != null && nameImage != null;

            if (nameImage != null)
            {
                nameImage.texture = nameTex;
                nameImage.color = new Color(1.0f, 1.0f, 1.0f, 0.0f);
                nameImage.uvRect = new Rect(0.0f, 0.0f, 0.0f, 1.0f);
                nameImage.gameObject.SetActive(useNameImage_);
            }
            if (nameText != null)
            {
                nameText.text = "";
                nameText.color = new Color(activeColor_.r, activeColor_.g, activeColor_.b, 0.0f);
                nameText.gameObject.SetActive(!useNameImage_);
            }
            if (nameShadowText != null)
            {
                nameShadowText.text = "";
                nameShadowText.color = new Color(0.03f, 0.02f, 0.04f, 0.0f);
                nameShadowText.gameObject.SetActive(!useNameImage_);
            }
            if (nameSecondaryText != null)
            {
                nameSecondaryText.text = "";
                nameSecondaryText.color = new Color(activeColor_.r, activeColor_.g, activeColor_.b, 0.0f);
                nameSecondaryText.gameObject.SetActive(!useNameImage_ && splitTitle_);
            }
            if (nameSecondaryShadowText != null)
            {
                nameSecondaryShadowText.text = "";
                nameSecondaryShadowText.color = new Color(0.03f, 0.02f, 0.04f, 0.0f);
                nameSecondaryShadowText.gameObject.SetActive(!useNameImage_ && splitTitle_);
            }

            if (rankText != null)
            {
                rankText.text = rankLabel != null ? rankLabel : "";
                rankText.color = new Color(activeColor_.r, activeColor_.g, activeColor_.b, 0.0f);
                rankText.gameObject.SetActive(showRankInCutin);
            }

            Texture mascot = ResolveMascot(activeWinnerChara_);
            bool useWipe = mascotWipeEnabled && mascotWipeStrips != null && mascotWipeStrips.Length > 0;
            if (mascotWipeStrips != null)
            {
                for (int i = 0; i < mascotWipeStrips.Length; ++i)
                {
                    if (mascotWipeStrips[i] == null) { continue; }
                    mascotWipeStrips[i].texture = mascot;
                    mascotWipeStrips[i].color = new Color(1.0f, 1.0f, 1.0f, 0.0f);
                }
            }
            if (mascotWipeRoot != null)
            {
                mascotWipeRoot.gameObject.SetActive(useWipe && mascot != null);
            }
            if (mascotImage != null)
            {
                mascotImage.texture = mascot;
                mascotImage.color = new Color(1.0f, 1.0f, 1.0f, 0.0f);
                // 帯パネルを使うときは二重に出るので従来の1枚は伏せる。
                mascotImage.gameObject.SetActive(mascot != null && !useWipe);
            }

            PrepareTileVisuals();

            if (vignetteImage != null)
            {
                vignetteImage.color = new Color(vignetteColor.r, vignetteColor.g, vignetteColor.b, 0.0f);
            }
            if (flashImage != null)
            {
                flashImage.color = new Color(1.0f, 1.0f, 1.0f, 0.0f);
            }
            if (flashRect != null)
            {
                flashRect.sizeDelta = flashHomeSize_;
                flashRect.localScale = Vector3.one;
            }
            if (brushGlowImage != null)
            {
                brushGlowImage.color = new Color(activeColor_.r, activeColor_.g, activeColor_.b, 0.0f);
            }

            int n = sparkImages != null ? sparkImages.Length : 0;
            for (int i = 0; i < n; ++i)
            {
                if (sparkImages[i] == null) { continue; }
                sparkImages[i].color = new Color(1.0f, 0.88f, 0.52f, 0.0f);
            }

            revealSePlayed_ = false;
            rankSePlayed_ = false;
            launchBurstPlayed_ = false;
            flashBurstPlayed_ = false;
            if (launchBurst != null) { launchBurst.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); }
            if (flashBurst != null) { flashBurst.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); }

            if (stageViewImage != null)
            {
                stageViewImage.color = new Color(1.0f, 1.0f, 1.0f, 0.0f);
            }

            fxRoot.SetActive(true);
            if (stageCamera != null) { stageCamera.enabled = true; }
            startTime_ = Time.time;
            PlaySe(startSound, startVolume);
            Apply(0.0f);
        }

        void Update()
        {
            // 打ち上げはカットインより前の段。startTime_ がまだ立っていないので、
            // 尾の後始末はこのガードより先に回す。
            UpdateCometTail();

            if (startTime_ < 0.0f) { return; }

            float elapsed = Time.time - startTime_;

            if (elapsed >= GetDuration())
            {
                HideNow();
                return;
            }

            FaceLocalViewer();
            Apply(elapsed);
        }

        /// <summary>Canvas をローカルプレイヤーの方へ水平だけ向ける（見上げ・見下ろしはしない）。</summary>
        private void FaceLocalViewer()
        {
            if (billboardRoot == null) { return; }

            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null || !Utilities.IsValid(localPlayer)) { return; }

            VRCPlayerApi.TrackingData head =
                localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);

            Vector3 forward = billboardRoot.position - head.position;
            forward.y = 0.0f;
            if (forward.sqrMagnitude < 0.0001f) { return; }

            billboardRoot.rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
        }

        /// <summary>経過時間から各パーツの姿勢と透明度を作る。</summary>
private void Apply(float elapsed)
        {
        // The reference holds the completed frame through the exit window, then Duration() hard-cuts it.
        float alpha = 1.0f;

            ApplyVignette(elapsed, alpha);
            ApplyStageView(elapsed, alpha);
            ApplyBackground(elapsed, alpha);
            // 導入ステージはクリップが持っているならそちらが正。
            // 未設定なら従来どおりコードで動かす（切り戻し用）。
            if (introAnimator != null)
            {
                DriveIntroFromAnimator(elapsed);

                // スタジオ単体版はステージだけを撮るので、クリップが動かさない
                // 花びらと光の粒をここで足す（立ち絵は出さない。半透明の影に
                // なって濁るうえ、キャラはこのあとのポスターが主役で見せる）。
                if (standaloneStage)
                {
                    ApplyPetals(elapsed, alpha);
                    ApplySparks(elapsed, alpha);
                }
            }
            else
            {
                ApplyMascot(elapsed, alpha);
                ApplyPetals(elapsed, alpha);
                ApplySparks(elapsed, alpha);
                ApplyWinningTile(elapsed, alpha);
                ApplyCometTiles(elapsed, alpha);
                // カメラは和了牌を狙うので、牌を置き終えてから回す。
                ApplyStageCamera(elapsed);
            }
            // 帯パネルは導入クリップの管轄外なので、アニメータの有無に関係なくここで動かす。
            ApplyMascotWipe(elapsed, alpha);
            ApplyHand(elapsed, alpha);
            ApplyFlash(elapsed, alpha);
            ApplyName(elapsed, alpha);
            ApplyRank(elapsed, alpha);
        }

        // ステージのカメラ映像。導入の間だけ出し、背景イラストが出てきたら引く。
        // カメラは単色クリアなので、この面が出ている間は世界が一切透けない。
        private void ApplyStageView(float elapsed, float alpha)
        {
            if (stageViewImage == null) { return; }

            // 背景イラストが立ち上がるまでが導入。そこから短くクロスフェードして退場。
            float outStart = BgStart();
            float outSpan = Mathf.Max(0.0001f, bgFadeSeconds * 0.6f);
            float fade = elapsed <= outStart ? 1.0f : (1.0f - Mathf.Clamp01((elapsed - outStart) / outSpan));

            // 出だしは暗幕が閉じるのに合わせて出す。
            float inP = Mathf.Clamp01(elapsed / Mathf.Max(0.0001f, darkenSeconds));

            stageViewImage.color = new Color(1.0f, 1.0f, 1.0f, inP * fade * alpha);

            // カメラは映している間だけ回す（Quest対策。役満の導入だけの数秒）。
            if (stageCamera != null)
            {
                bool want = fade > 0.001f;
                if (stageCamera.enabled != want) { stageCamera.enabled = want; }
            }
        }

        // ステージカメラの振り付け。段は主役牌（IntroT）にそのまま合わせてある。
        // t は clamp して使う＝演出の前後で姿勢が飛ばない。
        private void ApplyStageCamera(float elapsed)
        {
            if (stageCamera == null) { return; }

            Transform tr = stageCamera.transform;
            if (!cinematicCamera)
            {
                tr.localPosition = camHome_;
                tr.localRotation = camHomeRot_;
                stageCamera.fieldOfView = camHomeFov_;
                return;
            }

            float t = Mathf.Clamp01(IntroT(elapsed));

            // 滞空中の牌の高さ。ここまで降りると「正面から捉えた」絵になる。
            // Canvas単位→m は、カメラ距離と stageDepthBase の比からそのまま出せる。
            float unitsToM = stageDepthBase > 0.001f
                ? camHome_.magnitude / stageDepthBase
                : 0.01f;
            float eyeLevel = winningTileHome_.y * unitsToM;

            float rise;               // 高さ（m）。上から下へ降りるのが今回の主役
            float dolly;              // + で寄る
            float roll;
            float fovAdd = 0.0f;
            float follow = 1.0f;      // 和了牌をどれだけ狙うか（0で構図の中心）
            float aimDrop = 0.0f;     // 狙点を牌の下へずらす量（m）

            if (t < 0.103f)
            {
                // 入り。カメラは牌より上にいて見下ろす。牌は画面の下から入ってくる。
                float p = EaseOutCubic(t / 0.103f);
                rise = Mathf.Lerp(camRiseFrom, camRiseFrom * 0.93f, p);
                dolly = Mathf.Lerp(-0.55f, 0.0f, p);
                roll = Mathf.Lerp(-0.10f, -0.06f, p) * camRoll;
                fovAdd = Mathf.Lerp(2.5f, 0.0f, p);
                // ここで牌を狙うと中央に貼り付いて「下から出てくる」に見えない。
                follow = Mathf.Lerp(0.0f, 0.70f, p);
            }
            else if (t < 0.500f)
            {
                // 滞空。カメラが上から降りてきて、牌の高さで正面から捉える。
                float p = (t - 0.103f) / 0.397f;
                float e = p * p * (3.0f - 2.0f * p);
                rise = Mathf.Lerp(camRiseFrom * 0.93f, eyeLevel, e);
                dolly = Mathf.Lerp(0.0f, camPushIn, e);
                roll = Mathf.Lerp(-0.06f, 0.05f, e) * camRoll;
                follow = Mathf.Lerp(0.70f, 1.00f, e);
            }
            else if (t < 0.621f)
            {
                // 予備動作。降下を続けたまま傾けて、跳ねる前の緊張を作る。
                float p = (t - 0.500f) / 0.121f;
                float e = EaseOutCubic(p);
                rise = Mathf.Lerp(eyeLevel, eyeLevel - 1.6f, e);
                dolly = Mathf.Lerp(camPushIn, camPushIn * 0.55f, p);
                roll = Mathf.Lerp(0.05f, -0.12f, e) * camRoll;
            }
            else if (t < 0.690f)
            {
                // 打ち上げ。牌が上へ抜け、カメラは下へ回り込んで見上げる側になる。
                float p = (t - 0.621f) / 0.069f;
                float e = Mathf.Pow(p, 1.5f);
                rise = Mathf.Lerp(eyeLevel - 1.6f, camRiseTo * 0.75f, e);
                dolly = Mathf.Lerp(camPushIn * 0.55f, -camPullBack, p * p);
                fovAdd = camFovPunch * EaseOutCubic(p);
                roll = Mathf.Lerp(-0.12f, -0.45f, e) * camRoll;
                // 狙点を牌の下へ置いて、牌を画面上へ逃がす＝カメラが追いつけない絵。
                aimDrop = camAimLead * EaseOutCubic(p);
            }
            else
            {
                // 収束。下から見上げたまま傾きを深めて、遠ざかる牌を見送る。
                float p = (t - 0.690f) / 0.310f;
                float e = EaseOutCubic(p);
                rise = Mathf.Lerp(camRiseTo * 0.75f, camRiseTo, e);
                dolly = Mathf.Lerp(-camPullBack, 0.0f, e);
                fovAdd = camFovPunch * (1.0f - e);
                roll = Mathf.Lerp(-0.45f, -1.00f, e) * camRoll;
                aimDrop = camAimLead * (1.0f - e);
                follow = Mathf.Lerp(1.00f, 0.85f, e);
            }

            // 手持ちの揺らぎ。完全固定だと作り物に見える。
            // elapsed は同期した開始時刻からの経過なので、全クライアントで同じ絵になる。
            float ox = Mathf.Sin(elapsed * 1.7f) * camDrift;
            float oy = Mathf.Sin(elapsed * 2.3f + 1.1f) * camDrift * 0.6f;

            // 打ち上げの衝撃。減衰させて短く切る。
            float launchAt = SparkStart() + Mathf.Max(0.0001f, sparkSeconds) * 0.621f;
            float since = elapsed - launchAt;
            float shakeSpan = Mathf.Max(0.0001f, camShakeSeconds);
            if (since >= 0.0f && since < shakeSpan)
            {
                float k = 1.0f - since / shakeSpan;
                k *= k;
                ox += Mathf.Sin(since * 97.0f) * camShake * k;
                oy += Mathf.Sin(since * 113.0f + 2.0f) * camShake * k;
            }

            // 寄り引き・上下はカメラ自身の軸で測る（親の向きに依存しない）。
            Vector3 fwd = camHomeRot_ * Vector3.forward;
            Vector3 right = camHomeRot_ * Vector3.right;
            Vector3 up = camHomeRot_ * Vector3.up;

            Vector3 pos = camHome_ + fwd * dolly + up * (rise + oy) + right * ox;
            tr.localPosition = pos;

            // 和了牌を狙う。8m も上下に振るのでピッチを手で書くと必ずフレーム落ちする。
            // 牌は PlaceDepth で毎フレーム置き直されているので、その実座標をそのまま使う。
            Vector3 aim = Vector3.zero;
            if (winningTileRect != null && tr.parent != null)
            {
                aim = tr.parent.InverseTransformPoint(winningTileRect.position);
            }
            else if (winningTileRect != null)
            {
                aim = winningTileRect.position;
            }
            aim = Vector3.Lerp(Vector3.zero, aim, Mathf.Clamp01(follow));
            aim -= up * aimDrop;

            Vector3 dir = aim - pos;
            if (dir.sqrMagnitude > 0.0001f)
            {
                tr.localRotation = Quaternion.LookRotation(dir.normalized, up)
                    * Quaternion.Euler(0.0f, 0.0f, roll);
            }
            else
            {
                tr.localRotation = camHomeRot_ * Quaternion.Euler(0.0f, 0.0f, roll);
            }

            stageCamera.fieldOfView = camHomeFov_ + fovAdd;
        }

        // クリップを「経過秒で指定した時刻」に固定して評価する。
        // 流しっぱなしにしないので、途中参加や処理落ちがあっても正しいコマへ飛ぶ＝
        // 全クライアントで同じ絵になる（同期変数は増えない）。
        private void DriveIntroFromAnimator(float elapsed)
        {
            float t = Mathf.Clamp01(IntroT(elapsed));

            if (!introAnimator.enabled) { introAnimator.enabled = true; }
            if (introStateHash_ == 0) { introStateHash_ = Animator.StringToHash(introStateName); }

            introAnimator.speed = 0.0f;
            introAnimator.Play(introStateHash_, 0, t);
            introAnimator.Update(0.0f);

            // バーストはクリップに乗せられない（Udon はアニメーションイベントを受け取らない）。
            // 主役牌が抜け始める t=0.621 で1回だけ鳴らす。
            if (!launchBurstPlayed_ && launchBurst != null && t >= 0.621f && t < 0.98f)
            {
                launchBurstPlayed_ = true;
                if (winningTileRect != null)
                {
                    launchBurst.transform.position = winningTileRect.position;
                }
                launchBurst.Play(true);
            }
        }

        // Animator は Update のあとにも自前で評価するので、
        // 読み戻しと UI の打ち消しは LateUpdate で行う（Update でやると上書きされる）。
        void LateUpdate()
        {
            if (startTime_ < 0.0f || introAnimator == null) { return; }

            // 実物3D牌に渡す倍率は、クリップが書いた Z から逆算する。
            // PlaceDepth は z = stageDepthBase * (1/s - 1) を書いているので、これで元に戻る。
            if (winningTileRect != null)
            {
                introWinScale_ = ScaleFromDepth(winningTileRect.localPosition.z);

                // クリップは「和了牌は実物の3D牌で出す」前提で焼いてあり、ステージ上の
                // RawImage は大きさ0にされている。単体版は実物が無いのでここで戻す
                // （奥行きはクリップが書いた z がそのまま効く）。
                if (standaloneStage)
                {
                    winningTileRect.sizeDelta = imageWinTileSize;
                }
            }
            if (winningTileImage != null)
            {
                introWinAlpha_ = winningTileImage.color.a;
                if (useRealIntroTiles)
                {
                    Color c = winningTileImage.color;
                    winningTileImage.color = new Color(c.r, c.g, c.b, 0.0f);
                }
            }

            int n = cometTileImages != null ? cometTileImages.Length : 0;
            for (int i = 0; i < n; ++i)
            {
                if (cometTileImages[i] == null) { continue; }

                if (cometAlpha_ != null && i < cometAlpha_.Length)
                {
                    cometAlpha_[i] = cometTileImages[i].color.a;
                }
                if (cometScale_ != null && i < cometScale_.Length
                    && cometTileRects != null && i < cometTileRects.Length
                    && cometTileRects[i] != null)
                {
                    cometScale_[i] = ScaleFromDepth(cometTileRects[i].localPosition.z);
                }
                if (useRealIntroTiles)
                {
                    Color c = cometTileImages[i].color;
                    cometTileImages[i].color = new Color(c.r, c.g, c.b, 0.0f);
                }
            }
        }

        private float ScaleFromDepth(float z)
        {
            if (!useStageDepth || stageDepthBase < 0.0001f) { return 1.0f; }
            float inv = 1.0f + z / stageDepthBase;
            if (inv < 0.0001f) { return 1.0f; }
            return 1.0f / inv;
        }

        private void ApplyVignette(float elapsed, float alpha)
        {
            if (vignetteImage == null) { return; }

            float darken = Mathf.Max(0.0001f, darkenSeconds);
            float p = Mathf.Clamp01(elapsed / darken);
            float eased = 1.0f - (1.0f - p) * (1.0f - p); // easeOutQuad

            vignetteImage.color = new Color(
                vignetteColor.r, vignetteColor.g, vignetteColor.b,
                vignetteColor.a * eased * alpha);
        }

private void ApplySparks(float elapsed, float alpha)
        {
            int n = sparkImages != null ? sparkImages.Length : 0;
            if (n == 0) { return; }

            float begin = SparkStart();
            float span = Mathf.Max(0.0001f, sparkSeconds);

            for (int i = 0; i < n; ++i)
            {
                if (sparkImages[i] == null) { continue; }

                float delay = (sparkOffsets_ != null && i < sparkOffsets_.Length) ? sparkOffsets_[i] : 0.0f;
                float life = span * (1.0f - delay);
                if (life < 0.0001f) { life = 0.0001f; }

                float t = (elapsed - begin - span * delay) / life;
                if (t <= 0.0f || t >= 1.0f)
                {
                    sparkImages[i].color = new Color(1.0f, 0.88f, 0.52f, 0.0f);
                    continue;
                }

                float rise = 1.0f - (1.0f - t) * (1.0f - t);
                float a = t < 0.18f ? (t / 0.18f) : (1.0f - (t - 0.18f) / 0.82f);
                if (a < 0.0f) { a = 0.0f; }

                Color gold = new Color(1.0f, 0.72f, 0.26f, 1.0f);
                Color c = Color.Lerp(gold, Color.white, rise * 0.85f);
                // 独立した黄色い棒に見えないよう、背景の筋くらいまで落とす。
                sparkImages[i].color = new Color(c.r, c.g, c.b, a * alpha * sparkAlpha);

                if (sparkRects != null && i < sparkRects.Length && sparkRects[i] != null)
                {
                    float baseX = (sparkX_ != null && i < sparkX_.Length) ? sparkX_[i] : 0.0f;
                    float x = baseX * (1.0f - rise * 0.78f);
                    float y = -sparkRise * 0.5f + sparkRise * rise;
                    sparkRects[i].anchoredPosition = new Vector2(x, y);

                    float s = (sparkScale_ != null && i < sparkScale_.Length) ? sparkScale_[i] : 1.0f;
                    sparkRects[i].localScale = new Vector3(
                        s * (1.0f - rise * 0.42f),
                        s * (1.0f + rise * 0.75f),
                        1.0f);
                }
            }
        }

private void ApplyFlash(float elapsed, float alpha)
        {
            if (flashImage == null) { return; }
            float begin = FlashStart();
            float span = Mathf.Max(0.0001f, flashSeconds);
            float t = (elapsed - begin) / span;
            float mainAlpha = 0.0f;
            if (t > 0.0f && t < 1.0f)
            {
                if (!revealSePlayed_)
                {
                    revealSePlayed_ = true;
                    PlaySe(revealSound, revealVolume);
                }

                if (!flashBurstPlayed_ && flashBurst != null)
                {
                    flashBurstPlayed_ = true;
                    flashBurst.Play(true);
                }
                float attack = 0.18f;
                float a = t < attack ? t / attack : 1.0f - (t - attack) / (1.0f - attack);
                a = Mathf.Clamp01(a);
                mainAlpha = a * a * fullFlashAlpha;
            }

            float handHitTime = HandStart() + Mathf.Max(0.0f, handEnterSeconds) * 0.70f;
            float handAlpha = GetHitAlpha(elapsed, handHitTime, 0.10f) * handHitFlashAlpha;
            float titleAlpha = GetHitAlpha(elapsed, WriteStart(), 0.11f) * titleHitFlashAlpha;
            if (splitTitle_)
            {
                float secondHit = WriteStart() + Mathf.Max(0.0f, writeSeconds) * 0.44f;
                titleAlpha = Mathf.Max(titleAlpha,
                    GetHitAlpha(elapsed, secondHit, 0.11f) * titleHitFlashAlpha * 0.82f);
            }

            float finalAlpha = Mathf.Max(mainAlpha, Mathf.Max(handAlpha, titleAlpha)) * alpha;
            flashImage.color = new Color(1.0f, 0.96f, 0.82f, finalAlpha);
            if (flashRect != null)
            {
                flashRect.sizeDelta = flashHomeSize_;
                float hitAlpha = Mathf.Max(handAlpha, titleAlpha);
                float pulseT = t > 0.0f && t < 1.0f
                    ? t
                    : 1.0f - Mathf.Clamp01(hitAlpha
                        / Mathf.Max(0.0001f, Mathf.Max(handHitFlashAlpha, titleHitFlashAlpha)));
                float pulse = 0.96f + 0.08f * EaseOutCubic(Mathf.Clamp01(pulseT));
                flashRect.localScale = new Vector3(pulse, pulse, 1.0f);
            }
        }

private void ApplyBackground(float elapsed, float alpha)
        {
            if (bgImage == null) { return; }
            float begin = BgStart();
            float span = Mathf.Max(0.0001f, bgFadeSeconds);
            float t = Mathf.Clamp01((elapsed - begin) / span);
            Vector2 impact = GetImpactOffset(elapsed);
            if (t <= 0.0f)
            {
                bgImage.color = new Color(1.0f, 1.0f, 1.0f, 0.0f);
                if (bgRect != null) { bgRect.anchoredPosition = bgHomePosition_ + impact; }
                return;
            }
            float eased = EaseOutCubic(t);
            bgImage.color = new Color(1.0f, 1.0f, 1.0f, eased * alpha);
            if (bgRect != null)
            {
                // 立ち上がりで寄りから引きへ。そのまま最後までごく緩く引き続ける。
                // 静止画のまま止めると後半のホールドが間延びする。
                // 終端をちょうど 1.0 にして、縮んで縁が見えることがないようにする。
                float settle = 1.0f + Mathf.Max(0.0f, bgKenBurns);
                float z = Mathf.Lerp(bgZoom, settle, eased);
                if (t >= 1.0f)
                {
                    float tail = Mathf.Max(0.0001f, GetDuration() - begin - span);
                    float k = Mathf.Clamp01((elapsed - begin - span) / tail);
                    z = Mathf.Lerp(settle, 1.0f, k);
                }
                bgRect.localScale = new Vector3(bgHomeScale_.x * z, bgHomeScale_.y * z, bgHomeScale_.z);
                bgRect.anchoredPosition = bgHomePosition_ + impact;
            }
        }

private void ApplyName(float elapsed, float alpha)
        {
            float begin = WriteStart();
            float span = Mathf.Max(0.0001f, writeSeconds);
            float t = Mathf.Clamp01((elapsed - begin) / span);
            if (t > 0.0f && !rankSePlayed_)
            {
                rankSePlayed_ = true;
                PlaySe(rankSound, rankVolume);
            }
            if (useNameImage_) { ApplyNameImage(t, alpha); }
            else { ApplyNameText(t, alpha); }
            ApplyBrushGlow(t, alpha);
        }

        // 筆文字テクスチャを左から出す。uvRect を切りながら幅も同じ比率で伸ばすので、
        // 画像が横に潰れず「書かれていく」見え方になる。
        private void ApplyNameImage(float t, float alpha)
        {
            float reveal = Mathf.Clamp01(t);
            float seconds = reveal * Mathf.Max(0.0001f, writeSeconds);
            float hit = Mathf.Clamp01(seconds / 0.22f);

            nameImage.uvRect = new Rect(0.0f, 0.0f, reveal, 1.0f);
            nameImage.color = new Color(1.0f, 1.0f, 1.0f, reveal > 0.0f ? alpha : 0.0f);

            Vector2 wipeHome = nameHome_;
            if (nameRect != null)
            {
                float hiddenWidth = nameFullSize_.x * (1.0f - reveal);
                wipeHome.x -= hiddenWidth * nameRect.pivot.x;
                nameRect.sizeDelta = new Vector2(nameFullSize_.x * reveal, nameFullSize_.y);
            }
            ApplyTitleTransform(nameRect, null, wipeHome, Vector2.zero, hit, 1, -1.0f);
        }

        // フォールバック。1文字ずつ増やす（筆文字テクスチャが無い役でも成立させる）。
private void ApplyNameText(float t, float alpha)
        {
            if (nameText == null) { return; }
            float seconds = t * Mathf.Max(0.0001f, writeSeconds);
            if (!splitTitle_)
            {
                float duration = Mathf.Max(0.12f,
                    Mathf.Max(1, nameLength_) * Mathf.Max(0.08f, titleGlyphInterval));
                float reveal = Mathf.Clamp01(seconds / duration);
                ApplyTextReveal(nameText, nameShadowText, nameString_, nameLength_, reveal, alpha, 0);
                ApplyTitleTransform(nameTextRect, nameShadowRect,
                    nameTextHome_, nameShadowHome_, reveal, Mathf.Max(1, nameLength_), -1.0f);
                return;
            }
            float first = Mathf.Clamp01(seconds / 0.72f);
            float second = Mathf.Clamp01((seconds - 0.68f) / 0.72f);
            ApplyTextBlock(nameText, nameShadowText, nameString_, first, alpha, 0);
            ApplyTextBlock(nameSecondaryText, nameSecondaryShadowText,
                secondaryNameString_, second, alpha, 1);
            ApplyTitleTransform(nameTextRect, nameShadowRect,
                nameTextHome_, nameShadowHome_, first, 1, -1.0f);
            ApplyTitleTransform(nameSecondaryTextRect, nameSecondaryShadowRect,
                nameSecondaryHome_, nameSecondaryShadowHome_, second, 1, 1.0f);
        }

        // 筆先の光球。書き出し位置（左端→右端）に追従し、書き終わりで消える。
private void ApplyBrushGlow(float t, float alpha)
        {
            if (brushGlowImage == null) { return; }
            if (splitTitle_ || t <= 0.0f || t >= 1.0f)
            {
                brushGlowImage.color = new Color(activeColor_.r, activeColor_.g, activeColor_.b, 0.0f);
                return;
            }
            int glyphs = Mathf.Max(1, nameLength_);
            float seconds = t * Mathf.Max(0.0001f, writeSeconds);
            float raw = seconds / Mathf.Max(0.08f, titleGlyphInterval);
            float phase = raw - Mathf.Floor(raw);
            int shown = Mathf.Clamp(Mathf.FloorToInt(raw) + 1, 1, glyphs);
            float a = 1.0f - EaseOutCubic(phase);
            brushGlowImage.color = new Color(activeColor_.r, activeColor_.g, activeColor_.b,
                a * alpha * 0.72f);
            if (brushGlowRect != null)
            {
                float x = nameHome_.x + nameFullSize_.x * ((float)shown / (float)glyphs);
                brushGlowRect.anchoredPosition = new Vector2(x, nameHome_.y);
                float s = 0.55f + phase * 0.95f;
                brushGlowRect.localScale = new Vector3(s, s, 1.0f);
            }
        }

private void ApplyRank(float elapsed, float alpha)
        {
            float begin = RankStart();
            float span = Mathf.Max(0.0001f, rankSeconds);
            float t = Mathf.Clamp01((elapsed - begin) / span);
            if (t <= 0.0f)
            {
                if (rankText != null)
                {
                    rankText.color = new Color(activeColor_.r, activeColor_.g, activeColor_.b, 0.0f);
                }
                return;
            }
            if (!showRankInCutin || rankText == null) { return; }
            rankText.color = new Color(activeColor_.r, activeColor_.g, activeColor_.b, alpha);
            if (rankRect != null)
            {
                float s = Mathf.Lerp(rankPunchScale, 1.0f, EaseOutCubic(t));
                rankRect.localScale = new Vector3(
                    rankHomeScale_.x * s, rankHomeScale_.y * s, rankHomeScale_.z);
            }
        }
        /// <summary>役満カードへ表示する実際の和了手と勝者キャラを準備する。</summary>
        public void SetHandData(
            int[] closedCodes, int numClosed, int agariCode,
            int[] openCodes, bool[] openFaceDown, bool[] openGroupStart, int numOpen,
            int winnerChara)
        {
            EnsureRuntimeBuffers();

            handCount_ = 0;
            handAgariIndex_ = -1;
            activeWinningTileCode_ = agariCode;
            activeWinnerChara_ = winnerChara;

            int closedN = closedCodes != null ? Mathf.Clamp(numClosed, 0, closedCodes.Length) : 0;
            for (int i = 0; i < closedN; ++i)
            {
                AddHandTile(closedCodes[i], false, false, false);
            }

            if (agariCode >= 0)
            {
                handAgariIndex_ = handCount_;
                AddHandTile(agariCode, false, false, true);
            }

            int openN = openCodes != null ? Mathf.Clamp(numOpen, 0, openCodes.Length) : 0;
            for (int i = 0; i < openN; ++i)
            {
                bool down = openFaceDown != null && i < openFaceDown.Length && openFaceDown[i];
                bool group = openGroupStart != null && i < openGroupStart.Length && openGroupStart[i];
                AddHandTile(openCodes[i], down, group, false);
            }
        }

        private void EnsureRuntimeBuffers()
        {
            if (handCodes_ == null || handCodes_.Length != MAX_HAND_SLOTS)
            {
                handCodes_ = new int[MAX_HAND_SLOTS];
                handFaceDown_ = new bool[MAX_HAND_SLOTS];
                handGroupStart_ = new bool[MAX_HAND_SLOTS];
                handUnits_ = new float[MAX_HAND_SLOTS];
            }

            if (atlasColX_ == null)
            {
                atlasColX_ = new float[]
                {
                    5.5f, 228.5f, 456.5f, 683.5f, 910.5f,
                    1137.5f, 1365.0f, 1592.0f, 1819.5f, 2042.5f
                };
                atlasRowY_ = new float[]
                {
                    73.5f, 397.5f, 726.5f, 1056.5f, 1386.5f, 1716.5f, 2041.5f
                };
            }
        }

        private void AddHandTile(int code, bool faceDown, bool groupStart, bool agari)
        {
            if (handCount_ >= MAX_HAND_SLOTS) { return; }

            float unit = handCount_ <= 0 ? 0.0f : handUnits_[handCount_ - 1] + 1.0f;
            if (handCount_ > 0 && groupStart) { unit += 0.35f; }
            if (handCount_ > 0 && agari) { unit += 0.55f; }

            handCodes_[handCount_] = code;
            handFaceDown_[handCount_] = faceDown;
            handGroupStart_[handCount_] = groupStart;
            handUnits_[handCount_] = unit;
            ++handCount_;
        }

        private void PrepareTileVisuals()
        {
            if (winningTileImage != null)
            {
                bool show = tileAtlas != null && activeWinningTileCode_ >= 0;
                winningTileImage.gameObject.SetActive(show);
                if (show)
                {
                    winningTileImage.texture = tileAtlas;
                    winningTileImage.uvRect = GetTileUv(activeWinningTileCode_);
                    winningTileImage.color = new Color(1.0f, 0.94f, 0.62f, 0.0f);
                }
            }

            int n = handTileImages != null ? handTileImages.Length : 0;
            for (int i = 0; i < n; ++i)
            {
                RawImage img = handTileImages[i];
                if (img == null) { continue; }

                bool show = tileAtlas != null && i < handCount_;
                img.gameObject.SetActive(show);
                if (!show) { continue; }

                img.texture = tileAtlas;
                img.uvRect = GetTileUv(handFaceDown_[i] ? -1 : handCodes_[i]);
                Color c = handFaceDown_[i] ? faceDownColor : Color.white;
                img.color = new Color(c.r, c.g, c.b, 0.0f);
            }

            // 彗星の牌は和了手から拾う（枚数が足りなければ先頭から回す）。
            // 抽象的な光ではなく実際の牌が飛ぶので、何で和了ったかが導入から伝わる。
            int cn = cometTileImages != null ? cometTileImages.Length : 0;
            for (int i = 0; i < cn; ++i)
            {
                RawImage cimg = cometTileImages[i];
                if (cimg == null) { continue; }

                bool cshow = tileAtlas != null && handCount_ > 0;
                cimg.gameObject.SetActive(cshow);
                if (!cshow) { continue; }

                cimg.texture = tileAtlas;
                cimg.uvRect = GetTileUv(handCodes_[i % handCount_]);
                cimg.color = new Color(1.0f, 1.0f, 1.0f, 0.0f);
            }
        }

private void ApplyMascot(float elapsed, float alpha)
        {
            if (mascotImage == null || mascotImage.texture == null) { return; }
            float t = (IntroT(elapsed) - 0.16f) / 0.30f;
            if (t <= 0.0f || t >= 1.0f)
            {
                mascotImage.color = new Color(1.0f, 1.0f, 1.0f, 0.0f);
                return;
            }
            float a = t < 0.18f ? t / 0.18f : Mathf.Clamp01((1.0f - t) / 0.24f);
            mascotImage.color = new Color(1.0f, 1.0f, 1.0f, a * mascotMaxAlpha * alpha);
            if (mascotRect != null)
            {
                float eased = EaseOutCubic(t);
                mascotRect.anchoredPosition = mascotHome_ + new Vector2(100.0f * (1.0f - eased), 0.0f);
                float s = Mathf.Lerp(1.10f, 1.0f, eased);
                mascotRect.localScale = new Vector3(
                    mascotHomeScale_.x * s, mascotHomeScale_.y * s, mascotHomeScale_.z);
            }
        }

        /// <summary>
        /// 立ち絵を帯に切って左から右へ順に出す。Mascot と同じ持ち時間に乗せ、
        /// 出きったあとは1枚絵として保ち、終わりは全体でフェードアウトする。
        /// </summary>
        private void ApplyMascotWipe(float elapsed, float alpha)
        {
            if (mascotWipeStrips == null || mascotWipeStrips.Length == 0) { return; }
            if (!mascotWipeEnabled) { return; }

            int n = mascotWipeStrips.Length;
            float t = (IntroT(elapsed) - 0.16f) / 0.30f;   // 従来の立ち絵と同じ窓
            if (t <= 0.0f || t >= 1.0f)
            {
                for (int i = 0; i < n; ++i)
                {
                    if (mascotWipeStrips[i] == null) { continue; }
                    mascotWipeStrips[i].color = new Color(1.0f, 1.0f, 1.0f, 0.0f);
                }
                return;
            }

            float spread = Mathf.Max(0.0f, mascotWipeSpread);
            float fade = Mathf.Max(0.0001f, mascotWipeStripFade);
            float outA = Mathf.Clamp01((1.0f - t) / 0.24f);
            float denom = n > 1 ? (float)(n - 1) : 1.0f;

            for (int i = 0; i < n; ++i)
            {
                var img = mascotWipeStrips[i];
                if (img == null) { continue; }

                float begin = ((float)i / denom) * spread;
                float a = Mathf.Clamp01((t - begin) / fade);
                img.color = new Color(1.0f, 1.0f, 1.0f, a * outA * mascotMaxAlpha * alpha);
            }

            if (mascotWipeRoot != null)
            {
                // 出し切るまでを使って、わずかに右へ寄せながら等倍へ落ち着かせる。
                float eased = EaseOutCubic(Mathf.Clamp01(t / Mathf.Max(0.0001f, spread + fade)));
                mascotWipeRoot.anchoredPosition =
                    mascotWipeHome_ + new Vector2(mascotWipeSlide * (1.0f - eased), 0.0f);
                float s = Mathf.Lerp(mascotWipeZoom, 1.0f, eased);
                mascotWipeRoot.localScale = new Vector3(
                    mascotWipeHomeScale_.x * s, mascotWipeHomeScale_.y * s, mascotWipeHomeScale_.z);
            }
        }

private void ApplyWinningTile(float elapsed, float alpha)
        {
            if (winningTileImage == null || winningTileImage.texture == null) { return; }

            float begin = SparkStart();
            float span = Mathf.Max(0.0001f, sparkSeconds);
            float t = (elapsed - begin) / span;
            if (t <= 0.0f || t >= 1.0f)
            {
                winningTileImage.color = new Color(1.0f, 0.94f, 0.62f, 0.0f);
                introWinAlpha_ = 0.0f;
                if (winTileGlowImage != null)
                {
                    winTileGlowImage.color = new Color(
                        tileGlowColor.r, tileGlowColor.g, tileGlowColor.b, 0.0f);
                }
                if (winTileTrailImage != null)
                {
                    winTileTrailImage.color = new Color(1.0f, 0.92f, 0.62f, 0.0f);
                }
                return;
            }

            // 参考動画の実測どおりの5段階（動画2.90秒を t=0..1 に写している）。
            //   0.000-0.103 下から中央へ急上昇
            //   0.103-0.500 中央で滞空（キャラが出て消える間）
            //   0.500-0.621 わずかに沈みつつ手前へ寄って拡大
            //   0.621-0.690 一気に上へ抜ける
            //   0.690-1.000 遠ざかって縮小
            float yOff;
            float s;

            if (t < 0.103f)
            {
                float p = EaseOutCubic(t / 0.103f);
                yOff = Mathf.Lerp(-350.0f, -50.0f, p);
                s = Mathf.Lerp(0.45f, 1.00f, p);
            }
            else if (t < 0.500f)
            {
                float p = (t - 0.103f) / 0.397f;
                yOff = Mathf.Lerp(-50.0f, 10.0f, p);
                s = Mathf.Lerp(1.00f, 1.05f, p);
            }
            else if (t < 0.621f)
            {
                float p = (t - 0.500f) / 0.121f;
                yOff = Mathf.Lerp(10.0f, -50.0f, p);
                // PlaceDepth で手前に出るぶんと牌自体の拡大が掛け算になるので、
                // 見かけの大きさは s の二乗で効く。1.55 だと画面の半分近くを占めた。
                s = Mathf.Lerp(1.05f, 1.15f, p);
            }
            else if (t < 0.690f)
            {
                // 打ち上げの瞬間に1回だけ。位置は今の牌に合わせる。
                if (!launchBurstPlayed_ && launchBurst != null)
                {
                    launchBurstPlayed_ = true;
                    if (winningTileRect != null)
                    {
                        launchBurst.transform.position = winningTileRect.position;
                    }
                    launchBurst.Play(true);
                }

                // 一気に抜ける。後半ほど速い。
                float p = (t - 0.621f) / 0.069f;
                yOff = Mathf.Lerp(-50.0f, 290.0f, p * p);
                s = Mathf.Lerp(1.15f, 0.90f, p);
            }
            else
            {
                // 遠ざかる。消失点（中央）へ寄りながら縮む。
                float p = (t - 0.690f) / 0.310f;
                yOff = Mathf.Lerp(290.0f, 120.0f, p);
                // 「ちょっとだけ遠くなる」程度。0.18 だと点になって消えていた。
                s = Mathf.Lerp(0.90f, 0.74f, p * p);
            }

            float attack = Mathf.Clamp01(t / 0.045f);
            float fade = 1.0f - Mathf.Clamp01((t - 0.86f) / 0.14f);
            float a = attack * fade;

            // 実物3D牌に差し替えるときは画像側を出さない（二重に見える）。
            winningTileImage.color = new Color(1.0f, 0.94f, 0.62f,
                useRealIntroTiles ? 0.0f : a * alpha);
            introWinAlpha_ = a * alpha;

            introWinScale_ = s;
            introWinYOff_ = yOff;

            // 炎の尾。牌の真下へ、牌と同じ縮尺で伸ばす。
            if (winTileTrailImage != null)
            {
                winTileTrailImage.color = new Color(1.0f, 0.92f, 0.62f, a * alpha * 0.9f);

                if (winTileTrailRect != null)
                {
                    // 打ち上げの後半ほど長く伸びる。尾は牌と同じ奥行きに置く。
                    float len = 1.0f + Mathf.Clamp01(t) * 1.2f;
                    PlaceDepth(winTileTrailRect,
                        winningTileHome_.x, winningTileHome_.y + yOff, s,
                        0.95f, len, -winTrailHalfHeight_ * len);
                }
            }

            // 光暈。牌と同じ位置・同じ縮尺で、ひと回り大きく出す。
            if (winTileGlowImage != null)
            {
                winTileGlowImage.color = new Color(
                    tileGlowColor.r, tileGlowColor.g, tileGlowColor.b, a * alpha);

                if (winTileGlowRect != null)
                {
                    PlaceDepth(winTileGlowRect,
                        winningTileHome_.x, winningTileHome_.y + yOff, s,
                        tileGlowScale, tileGlowScale, 0.0f);
                }
            }

            if (winningTileRect != null)
            {
                PlaceDepth(winningTileRect,
                    winningTileHome_.x, winningTileHome_.y + yOff, s,
                    winningTileHomeScale_.x, winningTileHomeScale_.y, 0.0f);
                // 動画では牌はほぼ直立のまま（Z回転しない）。
                // 打ち上げの一瞬だけ僅かに傾いて、すぐ真っ直ぐに戻る。
                float rotation = t < 0.103f
                    ? Mathf.Lerp(-4.0f, 0.0f, EaseOutCubic(t / 0.103f))
                    : 0.0f;
                winningTileRect.localRotation = Quaternion.Euler(0.0f, 0.0f, rotation);
            }
        }

        // 花びら。勝者キャラと同じ短い窓で、舞い落ちながら左右に揺れる。
        private void ApplyPetals(float elapsed, float alpha)
        {
            int n = petalImages != null ? petalImages.Length : 0;
            if (n == 0) { return; }

            float u = (IntroT(elapsed) - 0.06f) / 0.52f;

            for (int i = 0; i < n; ++i)
            {
                if (petalImages[i] == null) { continue; }

                float delay = (petalPhase_ != null && i < petalPhase_.Length) ? petalPhase_[i] : 0.0f;
                float lead = delay * 0.35f;
                float t = (u - lead) / (1.0f - lead);

                if (t <= 0.0f || t >= 1.0f)
                {
                    petalImages[i].color = new Color(petalColor.r, petalColor.g, petalColor.b, 0.0f);
                    continue;
                }

                float a = t < 0.22f ? (t / 0.22f) : Mathf.Clamp01((1.0f - t) / 0.38f);
                petalImages[i].color = new Color(petalColor.r, petalColor.g, petalColor.b, a * alpha);

                if (petalRects == null || i >= petalRects.Length || petalRects[i] == null) { continue; }

                float side = (petalSide_ != null && i < petalSide_.Length) ? petalSide_[i] : 1.0f;
                Vector2 home = (petalHome_ != null && i < petalHome_.Length) ? petalHome_[i] : Vector2.zero;

                float fall = -petalDrift * t;
                float sway = Mathf.Sin(t * 3.4f + delay * 6.283f) * 46.0f * side;

                petalRects[i].anchoredPosition = home + new Vector2(sway, fall);
                petalRects[i].localRotation =
                    Quaternion.Euler(0.0f, 0.0f, (t * 210.0f + delay * 360.0f) * side);
            }
        }

        // 後続6牌。下端から内側ペア→中間ペア→外側ペアの順に打ち上げ、上方へ収束して閃光へ渡す。
        // 彗星牌の、和了牌からの相対位置（Canvas単位）。
        // t を戻して呼べる純関数にしてあるので、差分から進行方向が出せる。
        private Vector2 CometOffset(int i, int n, float t)
        {
            float c = Mathf.Clamp01(t);

            // 開始角は下半分に散らす＝下から湧いて回り込む。
            float lane = n <= 1 ? 0.5f : (float)i / (float)(n - 1);
            float a0 = Mathf.PI * (1.12f + 0.76f * lane);
            float ang = a0 + cometOrbitTurns * Mathf.PI * 2.0f * EaseOutCubic(c);

            // 半径は 0 まで詰める＝和了牌に重なって合体する。
            float r = cometRadius * Mathf.Pow(1.0f - c, 1.5f);

            // 最初は和了牌より下にいて、寄りながら追いつく。
            float drop = cometRise * 0.35f * Mathf.Pow(1.0f - c, 1.4f);

            return new Vector2(
                Mathf.Cos(ang) * r,
                Mathf.Sin(ang) * r * cometOrbitFlatten - drop);
        }

private void ApplyCometTiles(float elapsed, float alpha)
        {
            int n = cometTileImages != null ? cometTileImages.Length : 0;
            if (n == 0 || tileAtlas == null) { return; }

            // 動画では主役牌が抜けるのと入れ違いに、1.90秒あたりから下から加わる。
            float u = (IntroT(elapsed) - 0.655f) / 0.345f;

            for (int i = 0; i < n; ++i)
            {
                if (cometTileImages[i] == null) { continue; }

                float delay = i * 0.075f;
                float t = (u - delay) / Mathf.Max(0.0001f, 1.0f - delay);

                if (t <= 0.0f || t >= 1.0f)
                {
                    cometTileImages[i].color = new Color(1.0f, 1.0f, 1.0f, 0.0f);
                    if (cometAlpha_ != null && i < cometAlpha_.Length) { cometAlpha_[i] = 0.0f; }
                    if (cometTrailImages != null && i < cometTrailImages.Length
                        && cometTrailImages[i] != null)
                    {
                        cometTrailImages[i].color = new Color(1.0f, 0.92f, 0.62f, 0.0f);
                    }
                    if (cometGlowImages != null && i < cometGlowImages.Length
                        && cometGlowImages[i] != null)
                    {
                        cometGlowImages[i].color = new Color(
                            tileGlowColor.r, tileGlowColor.g, tileGlowColor.b, 0.0f);
                    }
                    continue;
                }

                // 和了牌を中心に回り込みながら半径を詰め、最後に重なる（合体）。
                float cx = winningTileHome_.x;
                float cy = winningTileHome_.y + introWinYOff_;

                Vector2 off = CometOffset(i, n, t);
                float x = cx + off.x;
                float y = cy + off.y;

                // 牌は常に和了牌の方を向く。牌から和了牌へのベクトルで決める。
                // 寄りきると off が 0 に潰れて角度が暴れるので、
                // 近づくほど正立（＝合体先の和了牌と同じ向き）へ寝かせる。
                float faceAngle = off.sqrMagnitude > 0.0001f
                    ? Mathf.Atan2(-off.y, -off.x) * Mathf.Rad2Deg - 90.0f
                    : 0.0f;
                float faceBlend = Mathf.Clamp01(
                    off.magnitude / Mathf.Max(1.0f, cometRadius * 0.25f)) * cometTilt;
                Quaternion facing = Quaternion.Slerp(
                    Quaternion.identity, Quaternion.Euler(0.0f, 0.0f, faceAngle), faceBlend);

                // 寄りきったところで和了牌と同じ大きさになる＝重なって一つに見える。
                float sc = Mathf.Lerp(cometRecede, introWinScale_, t * t);

                float attack = Mathf.Clamp01(t / 0.08f);
                float tailFade = Mathf.Clamp01((1.0f - t) / 0.22f);
                float a = attack * tailFade;

                cometTileImages[i].color = new Color(
                    1.0f, 0.98f, 0.86f,
                    useRealIntroTiles ? 0.0f : a * alpha);
                if (cometAlpha_ != null && i < cometAlpha_.Length) { cometAlpha_[i] = a * alpha; }

                if (cometTileRects != null && i < cometTileRects.Length && cometTileRects[i] != null)
                {
                    PlaceDepth(cometTileRects[i], x, y, sc, 1.0f, 1.0f, 0.0f);
                    // 実物3D牌の向きは cometTileRects の回転から作られるので、
                    // ここを回せば牌ごと和了牌の方を向く。
                    cometTileRects[i].localRotation = facing;
                }
                if (cometScale_ != null && i < cometScale_.Length) { cometScale_[i] = sc; }

                // 光暈。牌と同じ位置に、ひと回り大きく。
                if (cometGlowImages != null && i < cometGlowImages.Length
                    && cometGlowImages[i] != null)
                {
                    cometGlowImages[i].color = new Color(
                        tileGlowColor.r, tileGlowColor.g, tileGlowColor.b, a * alpha);

                    if (cometGlowRects != null && i < cometGlowRects.Length
                        && cometGlowRects[i] != null)
                    {
                        PlaceDepth(cometGlowRects[i], x, y, sc,
                            tileGlowScale, tileGlowScale, 0.0f);
                    }
                }

                // 炎の尾。牌の真下に、牌と同じ縮尺で伸ばす。
                if (cometTrailImages != null && i < cometTrailImages.Length
                    && cometTrailImages[i] != null)
                {
                    cometTrailImages[i].color = new Color(1.0f, 0.92f, 0.62f, a * alpha * 0.95f);

                    if (cometTrailRects != null && i < cometTrailRects.Length
                        && cometTrailRects[i] != null)
                    {
                        // 尾は牌の背中側＝和了牌の反対側へ噴く。
                        // PlaceDepth の localYOffset は縦にしかずらせないため、
                        // 回転させたあとで自前に後方へ押し出す。
                        float len = 1.0f + t * 0.9f;
                        PlaceDepth(cometTrailRects[i], x, y, sc, 0.9f, len, 0.0f);

                        cometTrailRects[i].localRotation = facing;
                        cometTrailRects[i].anchoredPosition3D +=
                            facing * new Vector3(0.0f, -trailHalfHeight_ * len, 0.0f);
                    }
                }
            }
        }

private void ApplyHand(float elapsed, float alpha)
        {
            int n = handTileImages != null ? Mathf.Min(handTileImages.Length, handCount_) : 0;
            if (n <= 0) { return; }

            float begin = HandStart();
            float span = Mathf.Max(0.0001f, handEnterSeconds);
            float maxUnit = handUnits_ != null && n > 1 ? handUnits_[n - 1] : 1.0f;
            if (maxUnit < 0.0001f) { maxUnit = 1.0f; }
            Vector2 impact = GetImpactOffset(elapsed);

            for (int i = 0; i < n; ++i)
            {
                RawImage img = handTileImages[i];
                if (img == null) { continue; }

                float normalized = n <= 1 ? 0.5f : handUnits_[i] / maxUnit;
                float nx = normalized * 2.0f - 1.0f;
                float delay = Mathf.Abs(nx) * Mathf.Max(0.0f, handTileStaggerSeconds);
                if (i == handAgariIndex_) { delay += Mathf.Max(0.0f, handWinningDelaySeconds); }

                float t = Mathf.Clamp01((elapsed - begin - delay) / span);
                float burstEnd = 0.70f;
                float burstT = Mathf.Clamp01(t / burstEnd);
                float settleT = Mathf.Clamp01((t - burstEnd) / (1.0f - burstEnd));
                float burst = EaseOutCubic(burstT);
                float settle = EaseOutCubic(settleT);

                Vector2 target = ResolveHandPosition(nx) + impact;
                float centerLift = (1.0f - nx * nx) * 28.0f;
                Vector2 launch = new Vector2(
                    nx * handLaunchSpread,
                    handLaunchHeight + centerLift) + impact;

                float side = Mathf.Abs(nx) > 0.01f
                    ? nx : ((i % 2 == 0) ? -0.15f : 0.15f);
                Vector2 overshoot = target + new Vector2(
                    side * handImpactOvershoot,
                    -handImpactOvershoot);

                Vector2 position;
                if (t < burstEnd)
                {
                    position = launch + (overshoot - launch) * burst;
                    position.y += Mathf.Sin(burstT * Mathf.PI) * handArcHeight;
                }
                else
                {
                    position = overshoot + (target - overshoot) * settle;
                    position.y += Mathf.Sin(settleT * Mathf.PI)
                        * handImpactOvershoot * 0.32f * (1.0f - settleT);
                }

                float angle = ResolveHandAngle(nx);
                float spinSide = (i % 2 == 0) ? -1.0f : 1.0f;
                float startAngle = spinSide * (28.0f + Mathf.Abs(nx) * 18.0f);
                float impactAngle = angle + spinSide * 5.5f;
                float rotation = t < burstEnd
                    ? Mathf.Lerp(startAngle, impactAngle, burst)
                    : Mathf.Lerp(impactAngle, angle, settle);

                float hitScale = i == handAgariIndex_ ? 1.18f : 1.10f;
                float scale = t < burstEnd
                    ? Mathf.Lerp(0.42f, hitScale, burst)
                    : Mathf.Lerp(hitScale, 1.0f, settle);

                Color baseColor = handFaceDown_[i] ? faceDownColor : Color.white;
                float visible = Mathf.Clamp01(t / 0.08f);
                img.color = new Color(baseColor.r, baseColor.g, baseColor.b, visible * alpha);

                if (handTileRects != null && i < handTileRects.Length && handTileRects[i] != null)
                {
                    RectTransform rt = handTileRects[i];
                    rt.anchoredPosition = position;
                    rt.localRotation = Quaternion.Euler(0.0f, 0.0f, rotation);
                    rt.localScale = new Vector3(scale, scale, 1.0f);
                }
            }
        }

        private Vector2 ResolveHandPosition(float nx)
        {
            if (activeHandPathMode_ == 1)
            {
                return new Vector2(nx * 570.0f, 120.0f + (1.0f - nx * nx) * 92.0f);
            }
            if (activeHandPathMode_ == 2)
            {
                return new Vector2(nx * 570.0f, 50.0f + nx * 145.0f);
            }
            if (activeHandPathMode_ == 3)
            {
                return new Vector2(nx * 570.0f, 72.0f + nx * 82.0f + (1.0f - nx * nx) * 34.0f);
            }
            return new Vector2(nx * 570.0f, -12.0f + (1.0f - nx * nx) * 92.0f);
        }

        private float ResolveHandAngle(float nx)
        {
            if (activeHandPathMode_ == 1) { return -nx * 10.0f; }
            if (activeHandPathMode_ == 2) { return 10.0f; }
            if (activeHandPathMode_ == 3) { return 6.0f - nx * 4.0f; }
            return -nx * 10.0f;
        }

private void ConfigureTitleLayout()
        {
            Vector2 pos = new Vector2(-560.0f, -235.0f);
            Vector2 size = new Vector2(1120.0f, 250.0f);
            int fontSize = 180;
            if (activeTitleLayoutMode_ == 1)
            {
                pos = new Vector2(-650.0f, -255.0f);
                size = new Vector2(1300.0f, 220.0f);
                fontSize = 145;
            }
            else if (activeTitleLayoutMode_ == 2)
            {
                pos = new Vector2(-430.0f, 15.0f);
                size = new Vector2(250.0f, 660.0f);
                fontSize = 112;
            }
            else if (activeTitleLayoutMode_ == 3)
            {
                pos = new Vector2(-520.0f, -190.0f);
                size = new Vector2(1040.0f, 260.0f);
                fontSize = 190;
            }
            Vector2 pivot = activeTitleLayoutMode_ == 2 ? new Vector2(0.5f, 0.5f) : new Vector2(0.0f, 0.5f);
            Vector2 shadowPos = pos + new Vector2(8.0f, -8.0f);
            SetTitleRect(nameRect, pos, size, pivot);
            SetTitleRect(nameTextRect, pos, size, pivot);
            SetTitleRect(nameShadowRect, shadowPos, size, pivot);
            nameTextHome_ = pos;
            nameShadowHome_ = shadowPos;
            if (activeTitleLayoutMode_ == 2)
            {
                Vector2 secondPos = new Vector2(430.0f, -20.0f);
                Vector2 secondShadow = secondPos + new Vector2(8.0f, -8.0f);
                SetTitleRect(nameSecondaryTextRect, secondPos, size, pivot);
                SetTitleRect(nameSecondaryShadowRect, secondShadow, size, pivot);
                nameSecondaryHome_ = secondPos;
                nameSecondaryShadowHome_ = secondShadow;
            }
            if (nameText != null)
            {
                nameText.fontSize = fontSize;
                nameText.alignment = activeTitleLayoutMode_ == 2 ? TextAnchor.MiddleCenter : TextAnchor.MiddleLeft;
            }
            if (nameShadowText != null)
            {
                nameShadowText.fontSize = fontSize;
                nameShadowText.alignment = activeTitleLayoutMode_ == 2 ? TextAnchor.MiddleCenter : TextAnchor.MiddleLeft;
            }
            if (nameSecondaryText != null)
            {
                nameSecondaryText.fontSize = fontSize;
                nameSecondaryText.alignment = TextAnchor.MiddleCenter;
            }
            if (nameSecondaryShadowText != null)
            {
                nameSecondaryShadowText.fontSize = fontSize;
                nameSecondaryShadowText.alignment = TextAnchor.MiddleCenter;
            }
            nameHome_ = pos;
            nameFullSize_ = size;
        }

        private void SetTitleRect(RectTransform rt, Vector2 pos, Vector2 size, Vector2 pivot)
        {
            if (rt == null) { return; }
            rt.pivot = pivot;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            rt.localRotation = Quaternion.identity;
            rt.localScale = Vector3.one;
        }

private void ApplyTextReveal(Text text, Text shadow, string value, int length,
            float t, float alpha, int channel)
        {
            if (text == null) { return; }
            int shown = length <= 0 ? 0 : Mathf.Clamp(Mathf.CeilToInt(length * t), 0, length);
            int previous = channel == 0 ? primaryShown_ : secondaryShown_;
            if (shown != previous)
            {
                string s = shown <= 0 ? "" : value.Substring(0, shown);
                text.text = s;
                if (shadow != null) { shadow.text = s; }
                if (channel == 0) { primaryShown_ = shown; }
                else { secondaryShown_ = shown; }
            }
            float a = t <= 0.0f ? 0.0f : alpha;
            text.color = new Color(activeColor_.r, activeColor_.g, activeColor_.b, a);
            if (shadow != null)
            {
                shadow.color = new Color(0.03f, 0.02f, 0.04f, a * 0.88f);
            }
        }

private float GetHitAlpha(float elapsed, float hitTime, float duration)
        {
            float span = Mathf.Max(0.0001f, duration);
            float t = (elapsed - hitTime) / span;
            if (t <= 0.0f || t >= 1.0f) { return 0.0f; }
            float attack = 0.20f;
            if (t < attack) { return t / attack; }
            float tail = 1.0f - (t - attack) / (1.0f - attack);
            return tail * tail;
        }


private Vector2 GetImpactOffset(float elapsed)
        {
            float wave = ImpactWave(elapsed - FlashStart(), 1.0f);
            float handHitTime = HandStart() + Mathf.Max(0.0f, handEnterSeconds) * 0.70f;
            wave += ImpactWave(elapsed - handHitTime, 0.42f);
            wave += ImpactWave(elapsed - WriteStart(), 0.70f);
            if (splitTitle_)
            {
                float secondHit = WriteStart() + Mathf.Max(0.0f, writeSeconds) * 0.44f;
                wave += ImpactWave(elapsed - secondHit, 0.55f);
            }
            return new Vector2(wave, -wave * 0.55f);
        }


private float ImpactWave(float delta, float strength)
        {
            float span = Mathf.Max(0.0001f, impactShakeSeconds);
            if (delta < 0.0f || delta >= span) { return 0.0f; }
            float t = delta / span;
            float envelope = 1.0f - t;
            envelope *= envelope;
            return Mathf.Sin(t * Mathf.PI * 5.0f) * impactShakePixels * strength * envelope;
        }


private float EaseOutBack(float t)
        {
            float x = Mathf.Clamp01(t) - 1.0f;
            return 1.0f + 2.70158f * x * x * x + 1.70158f * x * x;
        }


private float EaseOutCubic(float t)
        {
            float p = 1.0f - Mathf.Clamp01(t);
            return 1.0f - p * p * p;
        }


private void ApplyTitleTransform(RectTransform rect, RectTransform shadowRect,
            Vector2 home, Vector2 shadowHome, float t, int glyphCount, float direction)
        {
            if (rect == null) { return; }
            int count = Mathf.Max(1, glyphCount);
            float raw = Mathf.Clamp01(t) * count;
            float phase = t >= 1.0f ? 1.0f : raw - Mathf.Floor(raw);
            float settle = EaseOutCubic(phase);
            float travel = titleTravelPixels * (1.0f - settle) * direction;
            float lift = Mathf.Abs(travel) * 0.10f;
            float scale = 1.0f + 0.30f * (1.0f - settle)
                - Mathf.Sin(phase * Mathf.PI) * 0.04f;
            float rotation = direction * 7.0f * (1.0f - settle);

            rect.anchoredPosition = home + new Vector2(travel, -lift);
            rect.localScale = new Vector3(scale, scale, 1.0f);
            rect.localRotation = Quaternion.Euler(0.0f, 0.0f, rotation);

            if (shadowRect != null)
            {
                shadowRect.anchoredPosition = shadowHome + new Vector2(travel * 1.08f, -lift);
                float shadowScale = scale * 1.06f;
                shadowRect.localScale = new Vector3(shadowScale, shadowScale, 1.0f);
                shadowRect.localRotation = Quaternion.Euler(0.0f, 0.0f, rotation * 1.08f);
            }
        }


private void ApplyTextBlock(Text text, Text shadow, string value, float t, float alpha, int channel)
        {
            if (text == null) { return; }
            int visible = t > 0.0f ? 1 : 0;
            int previous = channel == 0 ? primaryShown_ : secondaryShown_;
            if (visible != previous)
            {
                string s = visible == 0 ? "" : value;
                text.text = s;
                if (shadow != null) { shadow.text = s; }
                if (channel == 0) { primaryShown_ = visible; }
                else { secondaryShown_ = visible; }
            }
            float a = visible == 0 ? 0.0f : alpha;
            text.color = new Color(activeColor_.r, activeColor_.g, activeColor_.b, a);
            if (shadow != null)
            {
                shadow.color = new Color(0.03f, 0.02f, 0.04f, a * 0.88f);
            }
        }


        private string BuildVerticalText(string value)
        {
            if (value == null || value.Length <= 1) { return value != null ? value : ""; }

            string result = "";
            for (int i = 0; i < value.Length; ++i)
            {
                result += value.Substring(i, 1);
                if (i + 1 < value.Length) { result += "\n"; }
            }
            return result;
        }

        private Texture ResolveMascot(int chara)
        {
            var selfie = ResolveSelfieTexture(chara, winnerPlayerId_);
            if (selfie != null) { return selfie; }

            if (chara >= 0 && charaMascotTextures != null && chara < charaMascotTextures.Length
                && charaMascotTextures[chara] != null)
            {
                return charaMascotTextures[chara];
            }
            return defaultMascotTexture;
        }

        private int ResolveInt(int[] values, int slot, int fallback)
        {
            if (slot >= 0 && values != null && slot < values.Length) { return values[slot]; }
            return fallback;
        }

        private Rect GetTileUv(int code)
        {
            int col;
            int row;

            if (code < 0) { col = 0; row = 0; }
            else if (code < 9) { col = code; row = 5; }
            else if (code < 18) { col = code - 9; row = 3; }
            else if (code < 27) { col = code - 18; row = 4; }
            else if (code < 34) { col = code - 27; row = 2; }
            else if (code == 34) { col = 8; row = 2; }
            else if (code == 35) { col = 8; row = 1; }
            else { col = 7; row = 2; }

            float x0 = atlasColX_[col] + ATLAS_INSET;
            float x1 = atlasColX_[col + 1] - ATLAS_INSET;
            float yTop = atlasRowY_[row] + ATLAS_INSET;
            float yBottom = atlasRowY_[row + 1] - ATLAS_INSET;

            return new Rect(
                x0 / ATLAS_SIZE,
                1.0f - yBottom / ATLAS_SIZE,
                (x1 - x0) / ATLAS_SIZE,
                (yBottom - yTop) / ATLAS_SIZE);
        }



        // ---- 役IDからデータを引く ----

        /// <summary>役IDが yakuIds の何番目かを返す。無ければ -1。</summary>
        private int ResolveSlot(int yakuId)
        {
            if (yakuIds == null) { return -1; }
            for (int i = 0; i < yakuIds.Length; ++i)
            {
                if (yakuIds[i] == yakuId) { return i; }
            }
            return -1;
        }

        private Texture ResolveBackground(int slot)
        {
            if (slot >= 0 && yakuBackgrounds != null && slot < yakuBackgrounds.Length
                && yakuBackgrounds[slot] != null)
            {
                return yakuBackgrounds[slot];
            }
            return defaultBackground;
        }

        private Texture ResolveNameTexture(int slot)
        {
            if (slot >= 0 && yakuNameTextures != null && slot < yakuNameTextures.Length)
            {
                return yakuNameTextures[slot];
            }
            return null;
        }

        private Color ResolveColor(int slot)
        {
            if (slot >= 0 && yakuColors != null && slot < yakuColors.Length)
            {
                Color c = yakuColors[slot];
                // 未設定の枠（全ゼロ）は既定色にフォールバックする。
                // Inspector で配列を伸ばすと透明の黒が入るので、それを演出色にしない。
                if (c.a > 0.001f || c.r > 0.001f || c.g > 0.001f || c.b > 0.001f)
                {
                    return c;
                }
            }
            return defaultColor;
        }

        private void PlaySe(AudioClip clip, float volume)
        {
            if (seSource == null || clip == null) { return; }
            seSource.PlayOneShot(clip, volume > 0.0f ? volume : seVolume);
        }

        // ============================================================
        //  実物3D牌の姿勢（Mahjong.cs が毎フレーム読んで牌を置く）
        // ------------------------------------------------------------
        //  index 0 = 和了牌 / 1.. = 彗星。
        //  牌は Mahjong.cs の持ち物で、UpdateView が再描画のたびに全部
        //  SetActive(false) するので、所有者側から毎フレーム置き直す必要がある。
        //  ここは「今フレームどこに何を出すか」を答えるだけで、牌には触らない。
        // ============================================================

        /// <summary>
        /// ステージのレイヤー。導入中の3D牌はここへ移さないとステージカメラに映らない
        /// （カメラのカリングマスクがこのレイヤーだけなので）。無効なら -1。
        /// </summary>
        public int GetStageLayer()
        {
            if (!useRealIntroTiles || stageCamera == null) { return -1; }
            return stageCamera.gameObject.layer;
        }

        /// <summary>導入で3D牌を出す枠の数（0=和了牌 / 1..=彗星）。0なら3D牌は使わない。</summary>
        public int GetIntroTileCount()
        {
            if (!useRealIntroTiles) { return 0; }
            return (cometTileRects != null ? cometTileRects.Length : 0) + 1;
        }

        /// <summary>その枠を今フレーム出すか。</summary>
        public bool IsIntroTileVisible(int index)
        {
            if (!useRealIntroTiles || startTime_ < 0.0f) { return false; }

            if (index == 0)
            {
                return winningTileRect != null && introWinAlpha_ > 0.01f;
            }

            int i = index - 1;
            if (cometTileRects == null || i < 0 || i >= cometTileRects.Length) { return false; }
            if (cometTileRects[i] == null) { return false; }
            return cometAlpha_ != null && i < cometAlpha_.Length && cometAlpha_[i] > 0.01f;
        }

        /// <summary>その枠の world 位置。パネル面と同一平面だとZファイティングするので手前へ浮かせる。</summary>
        public Vector3 GetIntroTilePosition(int index)
        {
            Transform t = null;
            if (index == 0) { t = winningTileRect; }
            else
            {
                int i = index - 1;
                if (cometTileRects != null && i >= 0 && i < cometTileRects.Length)
                {
                    t = cometTileRects[i];
                }
            }
            if (t == null) { return Vector3.zero; }

            // Canvas の +forward は見る人と反対向き（ビルボードが LookRotation で
            // 「自分から見た向き」を作っているため）。手前は -forward。
            return t.position - t.forward * realTileDepth;
        }

        /// <summary>その枠の world 回転（パネルと同じ向き＋補正）。</summary>
        public Quaternion GetIntroTileRotation(int index)
        {
            Transform t = null;
            if (index == 0) { t = winningTileRect; }
            else
            {
                int i = index - 1;
                if (cometTileRects != null && i >= 0 && i < cometTileRects.Length)
                {
                    t = cometTileRects[i];
                }
            }
            if (t == null) { return Quaternion.identity; }
            return t.rotation * Quaternion.Euler(realTileEuler);
        }

        /// <summary>
        /// その枠の大きさ倍率（実物牌の元スケールに掛ける）。
        ///
        /// 牌自体の拡大は常に 1 に固定する。以前は奥行きから逆算した倍率を
        /// ここに掛けていたが、牌はその奥行きに実際に置かれるので遠近でも小さくなり、
        /// 見かけが倍率の二乗で効いていた（ピークで画面の46%を占めた）。
        /// 大きさは Pos Z（奥行き）だけで決まるようにする。
        /// </summary>
        public float GetIntroTileScale(int index)
        {
            return realTileScale;
        }

        /// <summary>
        /// 和了手の掃き出し（震え→上昇して消える）の長さ（秒）。
        /// Mahjong.cs がこのぶんカットインの開始を後ろへずらす。
        /// </summary>
        public float GetTileSweepSeconds()
        {
            return Mathf.Max(0.0f, sweepShakeSeconds) + Mathf.Max(0.0f, sweepRiseSeconds);
        }

        /// <summary>掃き出しの進み具合から、牌の「持ち上げ量」と「縮小率」を返す。</summary>
        /// <remarks>
        /// 牌のマテリアルは不透明（renderQueue 2000）なのでアルファでは消せない。
        /// 上昇させながら縮小して消す＝見た目のフェードアウトを作る。
        /// </remarks>
        public float GetSweepRiseAt(float elapsed)
        {
            float shake = Mathf.Max(0.0f, sweepShakeSeconds);
            float rise = Mathf.Max(0.0001f, sweepRiseSeconds);
            float t = Mathf.Clamp01((elapsed - shake) / rise);
            // 出だしはゆっくり、後半で加速して抜ける（easeInQuad）。
            return sweepRise * t * t;
        }

        /// <summary>同・縮小率（1=等倍 → 0=消滅）。</summary>
        public float GetSweepScaleAt(float elapsed)
        {
            float shake = Mathf.Max(0.0f, sweepShakeSeconds);
            float rise = Mathf.Max(0.0001f, sweepRiseSeconds);
            float t = Mathf.Clamp01((elapsed - shake) / rise);
            // 最初は縮まず、終盤で一気に消える。
            return 1.0f - t * t * t;
        }

        /// <summary>同・震えの横ぶれ量（m）。index ごとに位相をずらす。</summary>
        public float GetSweepShakeAt(float elapsed, int index)
        {
            float shake = Mathf.Max(0.0001f, sweepShakeSeconds);
            if (elapsed >= shake) { return 0.0f; }

            // 後半ほど強く震える（溜めてから飛ぶ）。
            float grow = Mathf.Clamp01(elapsed / shake);
            float phase = index * 1.7f;
            return Mathf.Sin(elapsed * sweepShakeSpeed + phase) * sweepShakeAmp * grow;
        }

        // ============================================================
        //  和了牌の動き（Mahjong.cs が毎フレーム読んで牌を置く）
        // ------------------------------------------------------------
        //  引数 t は「牌エフェクトの経過 / エフェクトの長さ」＝ 0..1。
        //  リングや火花は MahjongAgariEffect が同じ尺で出しているので、
        //  牌だけをそこへ合わせて動かせば雀魂の絵になる。
        //    0.00-0.10 … カクッと傾いて浮く
        //    0.10-0.72 … 漂う（ゆっくり上下）
        //    0.72-1.00 … 加速して打ち上がる
        // ============================================================

        /// <summary>和了牌の持ち上げ量（m）。</summary>
        public float GetWinTileRise(float t)
        {
            float launchFrom = Mathf.Clamp01(winTileLaunchFrom);

            if (t < 0.10f)
            {
                // 出だしは勢いよく浮く（easeOutCubic）。
                float p = Mathf.Clamp01(t / 0.10f);
                float e = 1.0f - (1.0f - p) * (1.0f - p) * (1.0f - p);
                return winTileFloatHeight * e;
            }

            if (t < launchFrom)
            {
                // 漂い。ゆっくり上下に揺れるだけ。
                return winTileFloatHeight + Mathf.Sin((t - 0.10f) * 9.0f) * winTileBob;
            }

            // 打ち上げ。後半ほど速い（easeInQuad）。
            float span = Mathf.Max(0.0001f, 1.0f - launchFrom);
            float q = Mathf.Clamp01((t - launchFrom) / span);
            return Mathf.Lerp(winTileFloatHeight, winTileLaunchHeight, q * q);
        }

        /// <summary>和了牌の傾き（度）。浮いたあとも少しずつ回り続ける。</summary>
        public float GetWinTileTilt(float t)
        {
            if (t < 0.10f)
            {
                float p = Mathf.Clamp01(t / 0.10f);
                float e = 1.0f - (1.0f - p) * (1.0f - p) * (1.0f - p);
                return winTileTiltDegrees * e;
            }
            return winTileTiltDegrees + (t - 0.10f) * 18.0f;
        }

        /// <summary>和了牌を今フレーム出すか（打ち上がりきったら消す）。</summary>
        public bool IsWinTileVisible(float t)
        {
            return t >= 0.0f && t < 1.0f;
        }

        // ============================================================
        //  和了牌と手牌の打ち上げ（Animation Clip 駆動）
        // ------------------------------------------------------------
        //  牌そのものを動かすのは Mahjong.cs（牌の所有者）。ここは
        //  「元の位置からどれだけずらすか」をクリップから読んで渡すだけ。
        //
        //  クリップは 0..1 の正規化時刻で固定評価する（導入ステージと同じ方式）。
        //  流しっぱなしにしないので、途中参加や処理落ちがあっても正しいコマへ飛ぶ＝
        //  全クライアントで同じ絵になり、同期変数は1つも増えない。
        //
        //  スロットの読み方（Animation ウィンドウで触るのはここ）:
        //    Position … 席相対のずらし量（m）。+Y が空へ、+Z が和了者から見て奥。
        //    Rotation … 元の向きからの回転（度）
        //    Scale.x  … 大きさの倍率。0 まで落とすとその牌は消える
        // ============================================================

        /// <summary>打ち上げをクリップで動かせるか。false なら従来の数値で動く。</summary>
        public bool HasFlyClip()
        {
            return flyAnimator != null && flyWinTile != null;
        }

        /// <summary>打ち上げの長さ（秒）。Mahjong.cs がこのぶん後段を後ろへ倒す。</summary>
        public float GetFlySeconds()
        {
            return Mathf.Max(0.05f, flySeconds);
        }

        /// <summary>手牌スロットの数。</summary>
        public int GetFlySlotCount()
        {
            return flyHandSlots == null ? 0 : flyHandSlots.Length;
        }

        /// <summary>
        /// 打ち上げの Animator を止める。回しっぱなしは Quest で純粋な無駄
        /// （スロットは見えないので絵は変わらないが、毎フレーム評価はされる）。
        /// </summary>
        public void StopFly()
        {
            if (flyAnimator != null) { flyAnimator.enabled = false; }
            HideComet();

            // 次の打ち上げでまた鳴るように戻す。
            flyChargeFired_ = false;
            flyLaunchFired_ = false;
        }

        /// <summary>クリップを正規化時刻 t（0..1）で固定評価する。1フレームに1回だけ呼ぶ。</summary>
        public void SampleFly(float t)
        {
            AdvanceFlySe(t);

            if (flyAnimator == null) { return; }

            if (!flyAnimator.enabled) { flyAnimator.enabled = true; }
            if (flyStateHash_ == 0) { flyStateHash_ = Animator.StringToHash(flyStateName); }

            flyAnimator.speed = 0.0f;
            flyAnimator.Play(flyStateHash_, 0, Mathf.Clamp01(t));
            flyAnimator.Update(0.0f);
        }

        /// <summary>スロットの Transform。index が負なら和了牌。</summary>
        // 打ち上げのSEを時刻で鳴らす。SampleFly は打ち上げ中だけ毎フレーム
        // 呼ばれるので、ここに乗せておけば Mahjong.cs 側に段を足さずに済む。
        //
        // 溜めは「頭で入った人」にだけ鳴らす。カリング復帰などで途中から
        // 見え始めたクライアントで溜めが鳴ると、発射より後ろにずれて聞こえる。
        private void AdvanceFlySe(float t)
        {
            if (seSource == null) { return; }

            // 1本もの（クリップ全体を覆う音）が入っていたらそれだけを鳴らす。
            // 溜め／発射も一緒に鳴らすと必ず二重になる。
            if (flyFullSound != null)
            {
                if (!flyChargeFired_ && t <= 0.25f)
                {
                    flyChargeFired_ = true;
                    seSource.PlayOneShot(flyFullSound, flyFullVolume);
                }
                return;
            }

            if (!flyChargeFired_ && t <= 0.25f)
            {
                flyChargeFired_ = true;
                if (flyChargeSound != null) { seSource.PlayOneShot(flyChargeSound, flyChargeVolume); }
            }

            if (!flyLaunchFired_ && t >= flyLaunchT)
            {
                flyLaunchFired_ = true;
                if (flyLaunchSound != null) { seSource.PlayOneShot(flyLaunchSound, flyLaunchVolume); }
            }
        }

        private Transform FlySlot(int index)
        {
            if (index < 0) { return flyWinTile; }
            if (flyHandSlots == null || flyHandSlots.Length <= 0) { return null; }
            return flyHandSlots[index % flyHandSlots.Length];
        }

        /// <summary>席相対のずらし量（m）。</summary>
        public Vector3 GetFlyOffset(int index)
        {
            var slot = FlySlot(index);
            if (slot == null) { return Vector3.zero; }
            return slot.localPosition;
        }

        /// <summary>元の向きからの回転（度）。</summary>
        public Vector3 GetFlyEuler(int index)
        {
            var slot = FlySlot(index);
            if (slot == null) { return Vector3.zero; }
            return slot.localEulerAngles;
        }

        /// <summary>大きさの倍率（1=等倍）。</summary>
        public float GetFlyScale(int index)
        {
            var slot = FlySlot(index);
            if (slot == null) { return 1.0f; }
            return slot.localScale.x;
        }

        /// <summary>今フレーム出すか。倍率が潰れていたら消す（不透明なのでアルファでは消せない）。</summary>
        public bool IsFlyVisible(int index)
        {
            var slot = FlySlot(index);
            if (slot == null) { return false; }
            return slot.localScale.x > 0.002f;
        }

        // ============================================================ 彗星トレイル

        // 和了牌の打ち上げに彗星の尾を付ける。牌の世界座標が毎フレーム
        // Mahjong.cs から渡ってくるので、それを追いかけるだけ。
        // 牌そのものにトレイルを親子付けしないのは、牌オブジェクトが136枚を
        // 毎局使い回すため（尾が次局へ持ち越される）。
        //
        // 点灯は「時刻」ではなく「速さ」で決める。牌は打ち上げの直前まで卓上で
        // ほぼ静止していて、そこで尾を出すと一点に溜まって団子になる。
        public void DriveComet(Vector3 worldPos, float t)
        {
            if (flyComet == null) { return; }

            Vector3 p = worldPos + cometOffset;
            Vector3 prev = cometPrev_;
            bool hadPrev = cometHasPrev_;
            cometPrev_ = p;
            cometHasPrev_ = true;

            // 速さ。コマの膨らみ・進行方向への伸び・後ろへの流れは全部これで決まる。
            float dt = Time.deltaTime;
            Vector3 vel = (hadPrev && dt > 0.0001f) ? ((p - prev) / dt) : Vector3.zero;

            if (cometOn_)
            {
                flyComet.position = p;
                // コマは牌そのものを包む＝ずらす前の位置で置く。
                DriveComa(worldPos, vel, t);
                return;
            }

            // 点火はクリップの決め打ちフレーム。牌は打ち上げの直前まで卓上で
            // ほぼ静止していて（クリップの最初の1.4秒は高さ1cmで浮くだけ）、
            // そこで尾を出すと一点に溜まって団子になる。
            if (t < cometStartT) { return; }

            cometOffAt_ = -1.0f;
            if (!flyComet.gameObject.activeSelf) { flyComet.gameObject.SetActive(true); }

            // 位置を入れてから Clear する。逆にすると前回の終点から現在地まで
            // 一本の線が引かれる（トレイルは点を世界座標で溜めるため）。
            flyComet.position = p;
            ClearCometTrails();
            SetCometEmitting(true);

            // 尾を「敷いて」から見せる。溜まるのを待つと点火から数フレームは
            // 芯だけの短い線に見える＝一瞬で尾が付く感じにならない。
            Vector3 back = hadPrev ? (prev - p) : Vector3.zero;
            if (back.sqrMagnitude < 0.0000001f) { back = Vector3.down; }
            SeedCometTrails(p, back.normalized);

            cometOn_ = true;

            // コマも同じ瞬間に点ける。尾だけ先に出ると「線が飛んでいる」だけで
            // 彗星に見えない（核・コマ・尾の3点が揃って彗星になる）。
            comaSpeed_ = vel.magnitude;
            DriveComa(worldPos, vel, t);
        }

        // 進行方向の逆へ点を並べて、点火の瞬間から尾がある状態にする。
        //
        // 敷いた点は同時に生まれるので同時に消えるが、そのころには実際の移動で
        // 溜まった点が同じ長さまで育っているので継ぎ目は出ない
        // （尾の寿命0.10/0.20秒 ＜ 打ち上げの残り約0.6秒）。
        private void SeedCometTrails(Vector3 head, Vector3 back)
        {
            if (flyCometTrails == null) { return; }
            if (cometSeedLength <= 0.0001f) { return; }

            int n = Mathf.Max(2, cometSeedPoints);
            Vector3[] pts = new Vector3[n];
            for (int i = 0; i < n; ++i)
            {
                // AddPositions は配列の順に「新しい点」として積まれるので、
                // 末端から先頭へ向かう順で作る。
                float k = 1.0f - ((float)i / (float)(n - 1));
                pts[i] = head + back * (cometSeedLength * k);
            }

            for (int i = 0; i < flyCometTrails.Length; ++i)
            {
                var tr = flyCometTrails[i];
                if (tr != null) { tr.AddPositions(pts); }
            }
        }

        // 尾は切らずに消す。牌が消えた瞬間に尾まで消えると勢いがそこで断ち切られる。
        // 放出だけ止めて、消えきってから UpdateCometTail が畳む。
        public void HideComet()
        {
            cometHasPrev_ = false;
            if (!cometOn_) { return; }

            cometOn_ = false;
            SetCometEmitting(false);
            cometOffAt_ = Time.time;
            cometTailSeconds_ = LongestCometTime();
        }

        // 尾が消えきったら畳む。回しっぱなしは Quest で純粋な無駄。
        private void UpdateCometTail()
        {
            if (cometOffAt_ < 0.0f) { return; }

            float left = (cometOffAt_ + cometTailSeconds_) - Time.time;
            if (left > 0.0f)
            {
                if (cometTailSeconds_ > 0.0001f)
                {
                    ApplyComaColors(Mathf.Clamp01(left / cometTailSeconds_));
                }
                return;
            }

            cometOffAt_ = -1.0f;
            HideComaNow();
            if (flyComet == null) { return; }

            ClearCometTrails();
            flyComet.gameObject.SetActive(false);
        }

        private void SetCometEmitting(bool on)
        {
            if (flyCometTrails == null) { return; }
            for (int i = 0; i < flyCometTrails.Length; ++i)
            {
                var t = flyCometTrails[i];
                if (t != null) { t.emitting = on; }
            }
        }

        private void ClearCometTrails()
        {
            if (flyCometTrails == null) { return; }
            for (int i = 0; i < flyCometTrails.Length; ++i)
            {
                var t = flyCometTrails[i];
                if (t != null) { t.Clear(); }
            }
        }

        private float LongestCometTime()
        {
            float longest = 0.0f;
            if (flyCometTrails == null) { return longest; }
            for (int i = 0; i < flyCometTrails.Length; ++i)
            {
                var t = flyCometTrails[i];
                if (t != null && t.time > longest) { longest = t.time; }
            }
            return longest;
        }

        // ============================================================ コマ（核を包む光）

        // 彗星の見た目は3点で決まる:
        //   核（Nucleus）… 飛んでいる和了牌そのもの
        //   コマ（Coma） … 核を包む球状の光。ここで足すのがこれ
        //   尾（Tail）   … TrailRenderer が引く筋
        //
        // 作りは加算のビルボード板を数枚重ねるだけ（Quest基準）。
        //   ・板は視点の方へ向け、進行方向をローカルの +Y に取る。
        //     こうすると「進行方向へ伸ばす」「後ろへずらす」が Y だけで書ける。
        //   ・板は牌より comaFront だけ手前へ出す。真芯に置くと不透明な牌に
        //     半分食われて中心が抜け、牌が回るたびにチラつく。
        //   ・テクスチャは中心をわざと抜いてある＝牌の絵柄が白飛びしない。
        //   ・速いほど膨らみ、進行方向へ伸び、外側の層ほど後ろへ流れる
        //     （実際の彗星も太陽に近づく＝速いほどコマが育つ）。
        private void DriveComa(Vector3 center, Vector3 vel, float t)
        {
            // Start() を当てにしない。エディタのプレビューは Start が走らないので、
            // ここで初期化を当てにすると「何も出ない」になる（実際に踏んだ）。
            if (flyComa == null || flyComaLayers == null) { return; }

            // 視点。実機は必ずローカルプレイヤーの頭。プレイヤーがいないのは
            // エディタのプレビュー（Preview Yakuman Fly Tiles）のときだけ。
            Vector3 viewDir;
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer != null && Utilities.IsValid(localPlayer))
            {
                Vector3 toView =
                    localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position - center;
                float dist = toView.magnitude;
                if (dist < 0.0001f) { return; }
                viewDir = toView / dist;
            }
            else
            {
                // プレビュー用の逃げ道。Camera.main は Udon に公開されていないので
                // 視点は取れない。ビルボードは諦めて今の向きのまま置く
                // （大きさと色だけはエディタで確かめられる）。
                viewDir = -flyComa.forward;
                if (viewDir.sqrMagnitude < 0.0001f) { viewDir = Vector3.back; }
            }

            // 速さは1フレームぶんの生値だと跳ねるので均す。
            comaSpeed_ = Mathf.Lerp(comaSpeed_, vel.magnitude, 0.30f);
            float speedN = comaSpeedFull > 0.0001f
                ? Mathf.Clamp01(comaSpeed_ / comaSpeedFull) : 0.0f;

            // 進行方向。止まっているときは上（打ち上げなので上が自然）。
            Vector3 up = vel;
            if (up.sqrMagnitude < 0.000001f) { up = Vector3.up; }
            up = up.normalized;
            // 視線と進行方向が重なると LookRotation が壊れる。逃がす。
            if (Mathf.Abs(Vector3.Dot(up, viewDir)) > 0.995f)
            {
                up = Mathf.Abs(viewDir.y) > 0.9f ? Vector3.forward : Vector3.up;
            }

            if (!flyComa.gameObject.activeSelf) { flyComa.gameObject.SetActive(true); }
            flyComa.position = center + viewDir * comaFront;
            flyComa.rotation = Quaternion.LookRotation(-viewDir, up);

            // 開き具合はクリップの時刻から作る。Time.time は編集モードで進まないので、
            // それを基準にすると プレビューでコマが 25% のまま開かない。
            float open = 1.0f;
            if (comaFadeInSeconds > 0.0001f && flySeconds > 0.0001f)
            {
                float span = comaFadeInSeconds / flySeconds;   // 正規化した長さ
                if (span > 0.000001f) { open = Mathf.Clamp01((t - cometStartT) / span); }
            }
            float ease = 1.0f - (1.0f - open) * (1.0f - open);

            int n = flyComaLayers.Length;
            for (int i = 0; i < n; ++i)
            {
                var r = flyComaLayers[i];
                if (r == null) { continue; }

                float baseS = ComaBaseSize(i);
                if (baseS <= 0.0001f) { continue; }

                // 0=芯 〜 1=外套。外側ほど膨らみ・伸び・流れが強い。
                float w = (n <= 1) ? 1.0f : ((float)i / (float)(n - 1));

                float flick = 1.0f + comaFlicker
                    * Mathf.Sin(Time.time * comaFlickerHz + i * 2.1f);
                float openScale = Mathf.Lerp(0.25f, 1.0f, ease);
                float widthMul = Mathf.Max(0.0f, comaScale)
                    * (1.0f + comaSpeedGain * speedN * w)
                    * flick
                    * openScale;
                float s = baseS * widthMul;

                // Width and length are independent: the compact bright layer still
                // forms a long filled core beneath the tile.
                float length = Mathf.Lerp(0.25f, 0.30f, w)
                    * Mathf.Max(0.0f, comaScale)
                    * flick
                    * openScale;

                var tr = r.transform;
                tr.localScale = new Vector3(
                    s,
                    length * (1.0f + comaStretch * speedN * w),
                    s);
                tr.localPosition = new Vector3(0.0f, -s * comaLag * w * speedN, 0.0f);
            }

            // 点いた瞬間だけ増光する。これが「火が点いた」合図になる。
            ApplyComaColors(1.0f + comaIgnitionFlash * (1.0f - ease));
        }

        // 濃さだけ塗り直す。MaterialPropertyBlock なのでマテリアルは増えない。
        private void ApplyComaColors(float mul)
        {
            if (flyComaLayers == null) { return; }
            // Start を当てにしない（エディタのプレビューでは走らない）。
            if (comaMpb_ == null) { comaMpb_ = new MaterialPropertyBlock(); }

            for (int i = 0; i < flyComaLayers.Length; ++i)
            {
                var r = flyComaLayers[i];
                if (r == null) { continue; }

                Color c = (flyComaColors != null && i < flyComaColors.Length)
                    ? flyComaColors[i] : Color.white;
                float a = (flyComaAlphas != null && i < flyComaAlphas.Length)
                    ? flyComaAlphas[i] : 0.3f;

                comaMpb_.SetColor("_TintColor",
                    new Color(c.r, c.g, c.b, Mathf.Clamp01(a * mul)));
                r.SetPropertyBlock(comaMpb_);
            }
        }

        // 大きさは毎フレーム読む＝再生中に Inspector を触ってもすぐ効く。
        // flyComaSizes（m）が正。空／0 の層だけ、組み立て時の localScale で代用する。
        private float ComaBaseSize(int i)
        {
            if (flyComaSizes != null && i < flyComaSizes.Length && flyComaSizes[i] > 0.0001f)
            {
                return flyComaSizes[i];
            }
            if (comaBaseScale_ != null && i < comaBaseScale_.Length)
            {
                return comaBaseScale_[i];
            }
            return 0.0f;
        }

        private void HideComaNow()
        {
            comaSpeed_ = 0.0f;
            ApplyComaColors(0.0f);
        }

        // 画面上の位置と見かけの大きさを、奥行き込みの配置に変換して適用する。
        //   ・useStageDepth ON  … 実際に Z を動かし、大きさは遠近に任せる
        //   ・useStageDepth OFF … 従来どおり localScale で近似する
        // extraX / extraY は牌以外（尾など）が持つ固有の伸縮。
        // localYOffset は画面上ではなく「その奥行きでの」ずらし量。
        private void PlaceDepth(RectTransform rt, float screenX, float screenY,
            float apparent, float extraX, float extraY, float localYOffset)
        {
            if (rt == null) { return; }

            if (!useStageDepth || apparent <= 0.001f)
            {
                rt.anchoredPosition3D = new Vector3(
                    screenX, screenY + localYOffset * apparent, 0.0f);
                rt.localScale = new Vector3(apparent * extraX, apparent * extraY, 1.0f);
                return;
            }

            float inv = 1.0f / apparent;
            rt.anchoredPosition3D = new Vector3(
                screenX * inv,
                screenY * inv + localYOffset,
                stageDepthBase * (inv - 1.0f));
            rt.localScale = new Vector3(extraX, extraY, 1.0f);
        }

        /// <summary>局跨ぎ等で強制的に畳む。</summary>
        public void HideImmediate()
        {
            HideNow();
        }

        /// <summary>再生中かどうか（あがり画面の待ちに使う）。</summary>
        public bool IsPlaying()
        {
            return startTime_ >= 0.0f;
        }
    }
}
