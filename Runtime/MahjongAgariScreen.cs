using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    //  あがり（和了）画面パネル ＝ 雀魂スタイル
    // ------------------------------------------------------------
    //  設計思想（開発ナレッジ準拠）:
    //   ・純ローカル表示。同期変数を一切持たない（NoVariableSync）。
    //     手牌/役/点数は青空麻雀本体(Mahjong.cs)が既に全クライアントへ
    //     同期しているため、各クライアントが UpdateView 末尾のフックから
    //     このコンポーネントを「同じ同期済みデータ」で駆動するだけで
    //     全員の表示が一致する。追加の同期負荷ゼロ。
    //   ・牌は2D表示。3D牌テクスチャ(tiles.png / 2048x2048アトラス)を
    //     RawImage の uvRect で切り出して描く。テクスチャは3D牌と共用
    //     なので追加メモリゼロ、全牌が同一マテリアル＝Canvas内でバッチ。
    //     3D牌Transformの借用/復元は廃止（テーブル上の実物牌は動かない）。
    //   ・スロット（RawImage）は Start() でテンプレを Instantiate して
    //     生成（手牌18・ドラ5・裏ドラ5）。純ローカルの複製で同期対象なし。
    //   ・アニメは Graphic.color の alpha と Transform スケールのみ
    //     （QuestでのCanvas rebuild/パーティクルコスト回避）。
    //   ・UIはWorld Space Canvas必須（Screen Space Overlayは描画されない）。
    // ------------------------------------------------------------
    //  段階表示（雀魂風・役の順次フェードイン）:
    //   ・役行は最初すべて非表示。MahjongSoundsV2 のあがり読み上げクリップが
    //     「鳴り始めた」数（GetAgariStepCount）をポーリングし、対応する行を
    //     1つずつフェードインさせる（クリップ開始＝行出現なので読み上げと同期）。
    //   ・行→ステップの対応表は Mahjong.cs が SetRevealPlan で毎回渡す
    //     （連風の2行1クリップ、ドラ13枚以上の複数行1クリップも吸収）。
    //   ・終盤の演出順：全役表示 →（一拍）→ 点数表示 →（一拍）→
    //     称号バナー「ドーン」＋SFX →（間）→ 格読み（満貫〜役満）ボイス。
    //     バナーの時刻は SoundsV2 が予約する（格読みの直前に
    //     rankVoicePreDelaySeconds の間を置いた時点＝GetAgariRankCueTime）。
    //     声はそこから rankVoiceLeadSeconds 後に鳴るので、バナー＋SFXが先に立ち、
    //     声が被らない。予約が読めない場合は従来どおり格読み開始で出す。
    //     格読みが無い和了（満貫未満）はバナー無し・点数表示のみ。
    //     流し満貫などクリップ無しの称号は titleDelaySeconds の自動タイマーで出す。
    //     **ボイスなしの和了者も同じ時間送りへ落とす**（HasRankVoicePacing）。
    //     格読みが鳴らないと点数と称号が同じフレームで揃ってしまうため。
    //   ・音声が来ない環境（後入室/クリップ未設定/soundsV2未割当）では
    //     voiceStepTimeoutSeconds 間隔の自動送りで必ず最後まで進む。
    // ------------------------------------------------------------
    //  スクリプト自身は「常時アクティブな親」に置くこと。
    //  表示のON/OFFは panelRoot（子）のSetActiveで行う。
    //  （非アクティブなUdonBehaviourは外部から叩けないため）
    // ------------------------------------------------------------
    //  牌コード（Mahjong.cs の GetAgariTileCode と一致させること）:
    //    0- 8 = 一萬..九萬 / 9-17 = 1筒..9筒 / 18-26 = 1索..9索
    //   27-33 = 東南西北白發中 / 34 = 赤五萬 / 35 = 赤5筒 / 36 = 赤5索
    //      -1 = 牌なし（スロット非表示）
    //  裏向きは bool フラグで指定し、blankセル＋faceDownColorで描く。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class MahjongAgariScreen : UdonSharpBehaviour
    {
        [Header("必須参照")]
        [Tooltip("パネル全体のルート（World Space Canvas を含む子オブジェクト）。表示ON/OFF対象。")]
        public GameObject panelRoot;

        [Tooltip("和了者名の Text（雀魂の左下ネームプレート相当）")]
        public Text winnerNameText;

        [Tooltip("ツモ/ロンのタグ Text（右上の赤リボン相当）")]
        public Text tsumoRonText;

        [Tooltip("ツモ/ロンタグのルート（背景画像ごと隠す用・任意）。流し満貫などタグ無し表示のとき自動非表示。")]
        public GameObject tsumoRonRoot;

        [Tooltip("点数の Text（右下の大きな「8000点」相当）")]
        public Text scoreText;

        [Tooltip("中央の飜数 Text（「8飜」/役満時は「役満」）")]
        public Text hanText;

        [Tooltip("中央の符 Text（「40符」。役満時は自動で空にする）")]
        public Text fuText;

        [Tooltip("中央の「翻」静的Text（任意）。hanText を数字のみにする場合に割当。役満時（hanTextが「役満」になる時）は自動非表示。")]
        public GameObject hanUnitRoot;

        [Header("役リスト（左列→右列の順に並べた行を割当。推奨12行）")]
        [Tooltip("役名 Text（行ごと）。yakuValueTexts / yakuRowRoots と同じ長さ・同じ順にする。")]
        public Text[] yakuNameTexts;

        [Tooltip("飜数バッジ Text（行ごと。「1飜」等）。役満時は自動で空にする。")]
        public Text[] yakuValueTexts;

        [Tooltip("行のルート GameObject（未使用行を非表示にする）")]
        public GameObject[] yakuRowRoots;

        [Header("称号バナー（満貫/跳満/倍満/三倍満/役満…。任意）")]
        [Tooltip("称号の Text。満貫未満のときは行ごと非表示。")]
        public Text titleText;

        [Tooltip("称号バナーのルート（背景ごと隠すため）")]
        public GameObject titleRoot;

        [Header("ダブロン/トリロン表記（任意）")]
        [Tooltip("「ダブロン 1/2」等の Text。単独和了時は行ごと非表示。")]
        public Text multiRonText;

        [Tooltip("その行のルート")]
        public GameObject multiRonRoot;

        [Header("マスコット（和了者の選択キャラ）")]
        [Tooltip("左側のキャラ写真 RawImage。和了者がPlayerDataで選択しているキャラへ差し替える。")]
        public RawImage mascotImage;

        [Tooltip("キャラID別テクスチャ（0=Vol / 1=Shine / 2=Tsumuntema）。")]
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

        // 和了者のプレイヤーID。Mahjong 側が RenderView の直前に入れる（0＝CPU/不明）。
        private int winnerPlayerId_ = 0;

        // 自画像の引き当て用。和了者のキャラIDを渡すのと同じタイミングで呼ぶこと。
        public void SetWinnerPlayerId(int playerId) { winnerPlayerId_ = playerId; }


        [Tooltip("CPU席・PlayerData未復元・未設定時のフォールバック。通常はVol。nullならRawImageごと非表示。")]
        public Texture defaultMascotTexture;

        [Header("2D牌表示（tiles.pngアトラス）")]
        [Tooltip("牌1枚のテンプレRawImage。Texture に Mahjong/Textures/tiles.png を割当て、サイズ（sizeDelta）を牌1枚分（例 62x90）にし、GameObjectは非アクティブにしておく。パネルのCanvas配下に置くこと。")]
        public RawImage tileTemplate;

        [Tooltip("和了手牌の行（RectTransform）。左端中央を原点にスロットを左→右へ生成する。")]
        public RectTransform handRowRect;

        [Tooltip("ドラ表示牌の行（5スロット）")]
        public RectTransform doraRowRect;

        [Tooltip("裏ドラ表示牌の行（5スロット）")]
        public RectTransform uraRowRect;

        [Tooltip("裏向き牌の乗算色（blankセルをこの色で塗って牌背に見せる）")]
        public Color faceDownColor = new Color(0.45f, 0.60f, 0.90f, 1.0f);

        [Header("牌の見た目調整")]
        [Tooltip("牌同士の間隔（牌幅に対する倍率）")]
        public float tileSpacingFactor = 1.06f;

        [Tooltip("副露グループ前の追加ギャップ（牌幅の倍数）")]
        public float groupGapFactor = 0.4f;

        [Tooltip("和了牌の前の追加ギャップ（牌幅の倍数）")]
        public float agariGapFactor = 0.8f;

        [Header("挙動")]
        [Tooltip("パネルをローカルプレイヤーの方へ（ヨーのみ）向ける。VRでの推奨。")]
        public bool billboardToLocalPlayer = true;

        [Tooltip("表示アニメ秒数（alpha/スケールのみ）。0で無効。")]
        public float enterSeconds = 0.35f;

        [Tooltip("フェード対象の CanvasGroup（任意）")]
        public CanvasGroup canvasGroup;

        [Tooltip("スケールアニメ対象の RectTransform（任意。Canvas直下のContent推奨）")]
        public RectTransform contentRect;

        [Header("段階表示（雀魂風・役の順次フェードイン）")]
        [Tooltip("役行を読み上げに同期して1つずつ出す。OFFで従来の一括表示。")]
        public bool sequentialReveal = true;

        [Tooltip("役行1つのフェードイン秒数")]
        public float rowFadeSeconds = 0.25f;

        [Tooltip("役行出現時のスケールパンチ（1.0で無効）")]
        public float rowPunchScale = 1.08f;

        [Tooltip("読み上げ音声が進まないときの自動送り間隔（後入室/クリップ未設定対策）")]
        public float voiceStepTimeoutSeconds = 2.5f;

        [Tooltip("和了者が「ボイスなし」のときの役の送り間隔[秒]。声で送れないぶん、"
                 + "全役をこの間隔で順に出す。")]
        public float voicelessStepSeconds = 0.5f;

        [Tooltip("最終役表示から点数表示までの間（秒）")]
        public float finalDelaySeconds = 0.9f;

        [Tooltip("格読みクリップが無い称号（流し満貫等）を、点数表示から何秒後に出すか")]
        public float titleDelaySeconds = 0.8f;

        [Tooltip("点数表示と称号バナーの間として必ず確保する秒数。称号バナーの予約時刻から"
                 + "逆算して点数を先に出す。0にすると最後の役ボイスの長さがそのまま間になり、"
                 + "短いクリップ（ドラ・一発など）で間が消える。")]
        public float scoreLeadSeconds = 0.6f;

        [Tooltip("称号バナー「ドーン」の秒数")]
        public float titlePunchSeconds = 0.35f;

        [Tooltip("称号バナーのフェードイン秒数（スケールパンチとは別に、アルファをゆっくり入れる）")]
        public float titleFadeSeconds = 0.4f;

        [Tooltip("称号バナーの初期スケール（ここから1.0へ収束）")]
        public float titlePunchScale = 1.8f;

        [Tooltip("点数表示の初期スケール（ここから1.0へ収束）")]
        public float scorePunchScale = 1.25f;

        [Tooltip("中央サークル（飜/符）のルート。未設定なら hanText の親を自動使用。")]
        public GameObject hanCircleRoot;

        [Tooltip("点数表示のルート。未設定なら scoreText 自身を使用。")]
        public GameObject scoreRoot;

        // ---- スロット数（手牌13+和了1、カン4回で副露16+門前1+和了1=18が最大）----
        private const int NUM_HAND_SLOTS = 18;
        private const int NUM_INDICATOR_SLOTS = 5;

        // ---- tiles.png (2048x2048) の格子境界。黒枠の中心線を実測した値。
        //      均等割りではない（最上段に緑マーカー分のオフセットがある）ため
        //      テーブルで持つ。列10本・行7本。 ----
        private float[] atlasColX_;
        private float[] atlasRowY_;
        private const float ATLAS_SIZE = 2048.0f;
        // 隣セルへのにじみ（バイリニア/ミップ）防止の内側詰め(px)
        private const float ATLAS_INSET = 2.0f;

        // ---- 生成済みスロット ----
        private RawImage[] handSlots_;
        private RectTransform[] handSlotRects_;
        private RawImage[] doraSlots_;
        private RectTransform[] doraSlotRects_;
        private RawImage[] uraSlots_;
        private RectTransform[] uraSlotRects_;

        // ---- 表示中の状態 ----
        private bool visible_ = false;
        private float enterTime_ = 0.0f;
        private string signature_ = "";

        // ---- 和了演出（牌のエフェクト）待ち ----
        //  MahjongAgariEffect を割り当てた卓では、和了牌の演出が一段落してから
        //  パネルを出す（宣言ボイス → 手牌公開 → 牌のエフェクト → あがり画面）。
        //  Mahjong.cs が和了を検知した時点で「何時に開くか」を絶対時刻(Time.time基準)で
        //  SetShowAtTime に渡す。相対秒だと、手牌公開待ちのぶんエフェクトが後ろへずれた
        //  ときに画面だけ先に出てしまう（RenderAgari は和了の1フレーム目に走るため）。
        //  未設定（0＝過去）なら従来どおり即表示。
        private float showAtRequest_ = 0.0f;
        private bool holding_ = false;
        private float showAtTime_ = 0.0f;

        // ---- 段階表示：Mahjong.cs から渡される表示計画（RenderAgari直前にセット）----
        private MahjongSoundsV2 sounds_;      // 読み上げステップのポーリング先
        private int planSeat_ = -1;           // 和了者の席（読み上げキューの席）
        private int[] planRowSteps_;          // 行ごとの「この読み上げステップで出す」対応
        private int[] planRowValues_;         // 行ごとの飜数（中央サークルのカウントアップ用）
        private int planTotalSteps_ = 0;      // 総クリップ数（格読み含む）
        private bool planHasRankVoice_ = false; // 格読み（満貫〜役満）クリップがあるか
        private bool planValid_ = false;

        // ---- 段階表示：今回の表示の進行状態 ----
        private bool seqActive_ = false;
        // seqNumRows_ は「いま表示中のページで埋まっている行数」。
        // 役が行数（yakuRowRoots の長さ）を超えると、全行を消して次のページを
        // 1行目から出し直すので、ページが変わるたびに 0 から数え直す。
        private int seqNumRows_ = 0;
        private int seqMaxRowStep_ = 0;
        // ---- 役行のページ送り ----
        // 役が行数を超える手（青天井＋ローカル役で起こる）は、行を使い切ったところで
        // 全行を消し、続きを1行目から出す。中央サークルの飜数は通し（リセットしない）。
        private int seqRowSlots_ = 0;         // 1ページぶんの行数（= yakuRowRoots.Length）
        private int seqTotalYaku_ = 0;        // 表示対象の役の総数（行数で切らない）
        private int seqRevealedCount_ = 0;    // 開示済みの役の数（通し）
        private string[] seqNames_;           // 役名（全件。行ではなく役の添字）
        private string[] seqValueLabels_;     // 飜数バッジ（全件）
        private bool seqIsYakuman_ = false;
        private bool seqTitleVisible_ = false;
        private string hanFinalLabel_ = "";
        private int shownStep_ = 0;           // 開示済みステップ数
        private bool voiceless_ = false;      // 和了者がボイスなしか（Mahjong.csが毎回渡す）
        private bool rankVoiceAudible_ = true; // 格読み（満貫〜役満）ボイスが実際に鳴るか
        private int fallbackStep_ = 0;        // 自動送りの到達ステップ
        private float lastAdvanceTime_ = 0.0f;
        private float lastRowRevealTime_ = 0.0f;
        private float[] rowRevealTimes_;      // 0=未表示 / 正=フェード中(開始時刻) / 負=フェード完了
        private int revealedHan_ = 0;
        private bool circleShown_ = false;
        private float circleRevealTime_ = 0.0f;
        private bool scoreShown_ = false;      // 点数表示（全役の後・一拍おいて）
        private float scoreShownTime_ = 0.0f;
        private bool scoreAnimDone_ = false;
        private bool titleShown_ = false;      // 称号バナー（点数の後・格読みと同期）
        private float titleShownTime_ = 0.0f;
        private bool titleAnimDone_ = false;

        // ---- 段階表示：グラフィックキャッシュ（色フェード用。CanvasGroup追加の手動配線を不要にする）----
        private bool graphicsCached_ = false;
        private Graphic[][] rowGraphics_;
        private Color[][] rowGraphicColors_;
        private Graphic[] titleGraphics_;
        private Color[] titleGraphicColors_;
        private Graphic[] scoreGraphics_;
        private Color[] scoreGraphicColors_;
        private Graphic[] circleGraphics_;
        private Color[] circleGraphicColors_;
        private GameObject circleRootResolved_;
        private GameObject scoreRootResolved_;

        // ---- 雀魂風モーション（Quest向け：RectTransform + alpha のみ）----
        private const float ENTER_SLIDE_PIXELS = 180.0f;
        private const float ENTER_START_SCALE = 0.96f;
        private const float ENTER_TILT_DEGREES = 1.5f;
        private const float MASCOT_SLIDE_PIXELS = 220.0f;
        private const float MASCOT_DROP_PIXELS = 24.0f;
        private const float MASCOT_START_SCALE = 1.08f;
        private const float ROW_SLIDE_PIXELS = 84.0f;
        private const float CIRCLE_DROP_PIXELS = 22.0f;
        private const float CIRCLE_START_SCALE = 1.18f;
        // 点数テキストを枠内へ収めるための余白率と下限フォントサイズ。
        private const float SCORE_FIT_MARGIN = 0.98f;
        private const int SCORE_MIN_FONT_SIZE = 48;

        // 役名の自動縮小。英語の役名は日本語より横に長い（Yakuhai: Round Wind /
        // Dealing into Open Riichi など）。役名Textは幅260・Wrap+Truncate なので、
        // はみ出すと黙って2行に割れて下半分が切れる（ScoreTextで踏んだのと同じ罠）。
        private const float YAKU_NAME_FIT_MARGIN = 0.98f;
        private const int YAKU_NAME_MIN_FONT_SIZE = 16;

        private const float SCORE_SLIDE_PIXELS = 120.0f;
        private const float TITLE_SLIDE_PIXELS = 150.0f;
        private const float TITLE_START_Y_SCALE = 0.82f;

        private bool motionBasesCached_ = false;
        private RectTransform mascotRect_;
        private RectTransform circleRect_;
        private RectTransform scoreRect_;
        private int scoreBaseFontSize_ = 0;   // 建て込み時の点数フォントサイズ（FitScoreTextの上限）
        private int yakuNameBaseFontSize_ = 0; // 建て込み時の役名フォントサイズ（FitYakuNameTextの上限）

        // 観戦モニター用：最後に描いた和了の中身（精算の後で帯に役を並べる。同期ゼロ）
        private string[] specYakuNames_;
        private int specNumYaku_ = 0;
        private string specHanLabel_ = "";
        private string specTitleLabel_ = "";
        private string specPointLabel_ = "";
        private bool specYakuman_ = false;
        private int specSerial_ = 0;
        private string specFuLabel_ = "";
        private string specHanUnitLabel_ = "";
        private Text hanUnitText_;             // hanUnitRoot の Text（初回に GetComponent で解決）
        private int hanUnitBaseFontSize_ = 0;  // 建て込み時の「翻」フォントサイズ
        private RectTransform titleRect_;
        private RectTransform[] rowRects_;
        private Vector2 contentBasePosition_;
        private Vector3 contentBaseScale_;
        private Vector3 contentBaseEuler_;
        private Vector2 mascotBasePosition_;
        private Vector3 mascotBaseScale_;
        private Vector2 circleBasePosition_;
        private Vector3 circleBaseScale_;
        private Vector2 scoreBasePosition_;
        private Vector3 scoreBaseScale_;
        private Vector2 titleBasePosition_;
        private Vector3 titleBaseScale_;
        private Vector2[] rowBasePositions_;
        private Vector3[] rowBaseScales_;

        void Start()
        {
            // 実測格子（px）。tiles.png を差し替える場合はここを更新する。
            atlasColX_ = new float[] { 5.5f, 228.5f, 456.5f, 683.5f, 910.5f, 1137.5f, 1365.0f, 1592.0f, 1819.5f, 2042.5f };
            atlasRowY_ = new float[] { 73.5f, 397.5f, 726.5f, 1056.5f, 1386.5f, 1716.5f, 2041.5f };

            handSlots_ = new RawImage[NUM_HAND_SLOTS];
            handSlotRects_ = new RectTransform[NUM_HAND_SLOTS];
            doraSlots_ = new RawImage[NUM_INDICATOR_SLOTS];
            doraSlotRects_ = new RectTransform[NUM_INDICATOR_SLOTS];
            uraSlots_ = new RawImage[NUM_INDICATOR_SLOTS];
            uraSlotRects_ = new RectTransform[NUM_INDICATOR_SLOTS];

            for (int i = 0; i < NUM_HAND_SLOTS; ++i)
            {
                handSlots_[i] = CreateSlot(handRowRect);
                handSlotRects_[i] = handSlots_[i] != null ? (RectTransform)handSlots_[i].transform : null;
            }

            for (int i = 0; i < NUM_INDICATOR_SLOTS; ++i)
            {
                doraSlots_[i] = CreateSlot(doraRowRect);
                doraSlotRects_[i] = doraSlots_[i] != null ? (RectTransform)doraSlots_[i].transform : null;
                uraSlots_[i] = CreateSlot(uraRowRect);
                uraSlotRects_[i] = uraSlots_[i] != null ? (RectTransform)uraSlots_[i].transform : null;
            }

            // テンプレ自身が描画されないよう強制非表示（シーン側の消し忘れ保険）
            if (tileTemplate != null)
            {
                tileTemplate.gameObject.SetActive(false);
            }

            CacheRevealGraphics();
            CacheMotionBases();

            // late joiner で RenderAgari が Start() より先に走った場合、ここで無条件に隠すと
            // 表示済みのあがり画面が消える。既に表示中(visible_)なら隠さない。
            if (panelRoot != null && !visible_)
            {
                panelRoot.SetActive(false);
            }
        }

void Update()
        {
            if (!visible_)
            {
                return;
            }

            // 和了演出（牌のエフェクト）待ち。出るまではパネルを伏せたまま
            // 段階表示も止め、出た瞬間から入場アニメ・読み上げ追従を開始する。
            if (holding_)
            {
                if (Time.time < showAtTime_)
                {
                    return;
                }

                holding_ = false;

                if (panelRoot != null)
                {
                    panelRoot.transform.localPosition = Vector3.down * 0.20f;
                    panelRoot.transform.localScale = Vector3.one * 0.7f;
                    panelRoot.SetActive(true);
                }

                enterTime_ = 0.0f;
                ResetEntryMotion();
                if (canvasGroup != null && enterSeconds > 0.001f)
                {
                    canvasGroup.alpha = 0.0f;
                }

                // 自動送りのタイマーは「パネルが出た時刻」から数え直す。
                lastAdvanceTime_ = Time.time;
                lastRowRevealTime_ = Time.time;
            }

            // 入場アニメ。参照動画の斜めワイプ感を、負荷の軽い
            // anchoredPosition + scale + alpha だけで再構成する。
            if (enterSeconds > 0.001f && enterTime_ < enterSeconds)
            {
                enterTime_ += Time.deltaTime;
                float t = Mathf.Clamp01(enterTime_ / enterSeconds);
                float ease = EaseOutCubic(t);
                float fade = t * t * (3.0f - 2.0f * t);

                if (canvasGroup != null)
                {
                    canvasGroup.alpha = fade;
                }

                if (contentRect != null)
                {
                    contentRect.anchoredPosition = Vector2.Lerp(
                        contentBasePosition_ + new Vector2(-ENTER_SLIDE_PIXELS, 0.0f),
                        contentBasePosition_,
                        ease);
                    float s = Mathf.Lerp(ENTER_START_SCALE, 1.0f, ease);
                    contentRect.localScale = contentBaseScale_ * s;
                    contentRect.localEulerAngles = contentBaseEuler_
                        + new Vector3(0.0f, 0.0f, Mathf.Lerp(-ENTER_TILT_DEGREES, 0.0f, ease));
                }

                if (mascotRect_ != null)
                {
                    float mascotT = Mathf.Clamp01((t - 0.08f) / 0.92f);
                    float mascotEase = EaseOutCubic(mascotT);
                    mascotRect_.anchoredPosition = Vector2.Lerp(
                        mascotBasePosition_ + new Vector2(-MASCOT_SLIDE_PIXELS, -MASCOT_DROP_PIXELS),
                        mascotBasePosition_,
                        mascotEase);
                    mascotRect_.localScale = mascotBaseScale_
                        * Mathf.Lerp(MASCOT_START_SCALE, 1.0f, mascotEase);
                }

                if (t >= 1.0f)
                {
                    RestoreEntryMotion();
                }
            }

            // 段階表示の進行（役の順次フェードイン→格ドーン→点数）
            if (seqActive_)
            {
                UpdateSequentialReveal();
            }

            // ローカルプレイヤーへヨーのみで向ける（純ローカル。各自が見やすい向きになる）
            // 牌はCanvasの子なのでパネルごと一緒に回る。置き直し不要。
            if (billboardToLocalPlayer)
            {
                var lp = Networking.LocalPlayer;

                if (lp != null && Utilities.IsValid(lp))
                {
                    var head = lp.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;
                    var dir = transform.position - head; // Canvasの+Zは「見る人と反対」を向くのが正
                    dir.y = 0.0f;

                    if (dir.sqrMagnitude > 0.0001f)
                    {
                        transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
                    }
                }
            }
        }

        // ============================================================
        //  Mahjong.cs（UpdateView末尾フック）から毎回呼ばれる公開API
        // ============================================================

        // 読み上げステップのポーリング先。UpdateAgariScreen が毎回渡す（null可＝自動送りのみ）。
        public void SetVoiceLink(MahjongSoundsV2 sounds)
        {
            sounds_ = sounds;
        }

        // 和了者が「ボイスなし」を選んでいるか。RenderAgari の直前に毎回渡す。
        // true のときは声で送れないので、役行を voicelessStepSeconds 間隔で順に出す
        // （false のときは従来どおり読み上げクリップの開始に追従し、
        //   声が来ないときだけ voiceStepTimeoutSeconds の保険で送る）。
        public void SetVoiceless(bool voiceless)
        {
            voiceless_ = voiceless;
        }

        // 格読み（満貫〜役満）ボイスが実際に鳴るか。RenderAgari の直前に毎回渡す。
        //
        // voiceless_ と分けているのは、フリー対局の「役読み上げミュート」では
        // 役名・ドラは読まないが**点数格だけは読む**ため。両者を1つのフラグに
        // まとめると、称号バナーが titleDelaySeconds のローカルタイマーで出て
        // しまい、実際に鳴る格読みボイスと別の時計で動く（＝ズレる）。
        public void SetRankVoiceAudible(bool audible)
        {
            rankVoiceAudible_ = audible;
        }

        // 役の送り間隔。ボイスなしは「保険」ではなく本来の演出速度なので短くする。
        private float GetStepInterval()
        {
            if (!voiceless_) { return voiceStepTimeoutSeconds; }
            return voicelessStepSeconds > 0.0f ? voicelessStepSeconds : 0.5f;
        }

        // 格読みボイスの「時間差」を当てにしてよいか。
        //
        // ボイスなしの席は格読みクリップが鳴らないので、
        //   ・SoundsV2 が予約するバナー時刻（GetAgariRankCueTime）は 0 のまま
        //   ・ステップは自動送りで進むので、最終ステップ到達が点数と称号の
        //     両方の条件を**同じフレームで**満たしてしまう
        // ＝点数表示と称号バナーが同時に出る。
        // そこでボイスなしのときは「格読み無し」（流し満貫等）と同じ、
        // finalDelaySeconds → titleDelaySeconds の時間送りへ落とす。
        private bool HasRankVoicePacing()
        {
            return planHasRankVoice_ && rankVoiceAudible_;
        }

        // 役行は時間送り・格読みだけボイスがある状態か（＝フリー対局の役読みミュート）。
        // この形のときだけ最後の1ステップを自動送りから外す。
        // 段位戦は voiceless_ が false なので従来どおり（自動送りは失敗時の保険のまま）。
        private bool HoldsLastStepForRankVoice()
        {
            return voiceless_ && rankVoiceAudible_ && planHasRankVoice_;
        }

        // 格読みボイスが来ないまま止まるのを防ぐ保険（クリップ未設定のパック等）。
        // 実際の解禁は「役行の送り終わり＋点数の一拍＋rankVoicePreDelaySeconds」なので、
        // 通常はこの秒数に達する前にボイスが始まる。
        private const float RANK_STEP_RELEASE_SECONDS = 6.0f;

        // 表示計画。RenderAgari の直前に毎回呼ぶこと。
        //   seat          : 和了者の席（読み上げキューの席）
        //   rowSteps      : 行ごとの読み上げステップ（この番号のクリップ開始で行を出す）
        //   rowValues     : 行ごとの飜数（中央サークルのカウントアップ用。役満時は0でよい）
        //   totalSteps    : 総クリップ数（格読み含む）
        //   hasRankVoice  : 格読み（満貫〜役満）クリップが最後に鳴るか
        public void SetRevealPlan(int seat, int[] rowSteps, int[] rowValues, int totalSteps, bool hasRankVoice)
        {
            planSeat_ = seat;
            planRowSteps_ = rowSteps;
            planRowValues_ = rowValues;
            planTotalSteps_ = totalSteps;
            planHasRankVoice_ = hasRankVoice;
            planValid_ = rowSteps != null;
        }

        // 次に新規表示するときだけ有効な「パネルを開く時刻」（Time.time基準の絶対時刻）。
        // Mahjong.cs が和了を検知した時点で 牌エフェクト時刻+leadSeconds を渡す。
        // 演出なしの卓・流し満貫・途中参加では0（＝過去）を渡す＝即表示。
        public void SetShowAtTime(float absoluteTime)
        {
            showAtRequest_ = absoluteTime;
        }

        // 段階表示（役の読み上げ追従）が最後まで終わったか。卓のNEXTボタンを
        // 「読み上げが終わってから」出すために Mahjong.cs が毎フレーム問い合わせる。
        //  ・パネルが出ていない／和了演出待ちで保持中は false（まだ何も読んでいない）
        //  ・段階表示OFF・表示計画なしの卓は、パネルが出た時点で true（従来動作）
        //  ・読み上げが来ない場合も UpdateSequentialReveal の自動送りで
        //    shownStep_ は必ず planTotalSteps_ まで進むため、ここで止まり続けることはない
        public bool IsRevealFinished()
        {
            if (!visible_ || holding_) { return false; }
            if (!seqActive_) { return true; }

            if (shownStep_ < planTotalSteps_) { return false; }

            // 役行のフェードイン完了（0＝未表示 / 正＝フェード中 / 負＝完了）
            if (rowRevealTimes_ != null)
            {
                for (int i = 0; i < seqNumRows_ && i < rowRevealTimes_.Length; ++i)
                {
                    if (rowRevealTimes_[i] >= 0.0f) { return false; }
                }
            }

            if (!scoreShown_ || !scoreAnimDone_) { return false; }
            if (seqTitleVisible_ && (!titleShown_ || !titleAnimDone_)) { return false; }

            return true;
        }

        public bool IsScreenVisible()
        {
            return visible_;
        }

        public void HideScreen()
        {
            if (!visible_)
            {
                return;
            }

            visible_ = false;
            holding_ = false;

            if (panelRoot != null)
            {
                panelRoot.SetActive(false);
            }
        }

        // 点数テキストの自動縮小。
        //  ScoreText は折り返し=Wrap／縦=Truncate で建ててあるので、枠幅(565px)を超える
        //  表記は2行に割れて下半分が消える。実測 fontSize=120 で
        //    「18000点 」505px ／「6000点オール」764px ／「3000 / 6000点 」838px
        //  ＝ツモの内訳表記も6桁の役満点(288000点=576px)もはみ出す。
        //  建て込み時のサイズを上限に、1行で収まるところまでフォントを落とす。
        //  preferredWidth は折り返し前の幅を返すので、これで実寸に追従できる。
        private void FitScoreText()
        {
            if (scoreText == null) { return; }

            // 建て込み時のサイズは最初の1回だけ覚える（縮小後の値を基準にしない）。
            if (scoreBaseFontSize_ <= 0) { scoreBaseFontSize_ = scoreText.fontSize; }
            if (scoreBaseFontSize_ <= 0) { return; }

            scoreText.fontSize = scoreBaseFontSize_;

            RectTransform rt = (RectTransform)scoreText.transform;
            float limit = rt.rect.width * SCORE_FIT_MARGIN;
            if (limit <= 1.0f) { return; }

            float need = scoreText.preferredWidth;
            if (need <= limit) { return; }

            int size = (int)(scoreBaseFontSize_ * limit / need);
            if (size < SCORE_MIN_FONT_SIZE) { size = SCORE_MIN_FONT_SIZE; }

            scoreText.fontSize = size;
        }

        // 役名1行の自動縮小。FitScoreText と同じ方式で、建て込み時のサイズを上限に縮める。
        // 全行が同じサイズで建っているので基準は1つだけ覚える（縮小後の値を基準にしない）。
        private void FitYakuNameText(Text t)
        {
            if (t == null) { return; }

            if (yakuNameBaseFontSize_ <= 0) { yakuNameBaseFontSize_ = t.fontSize; }
            if (yakuNameBaseFontSize_ <= 0) { return; }

            t.fontSize = yakuNameBaseFontSize_;
            if (t.text.Length == 0) { return; }

            RectTransform rt = t.rectTransform;
            float limit = rt.rect.width * YAKU_NAME_FIT_MARGIN;
            if (limit <= 1.0f) { return; }

            float need = t.preferredWidth;
            if (need <= limit) { return; }

            int size = (int)(yakuNameBaseFontSize_ * limit / need);
            if (size < YAKU_NAME_MIN_FONT_SIZE) { size = YAKU_NAME_MIN_FONT_SIZE; }

            t.fontSize = size;
        }

        // 中央サークルの飜数の単位。Prefab に「翻」を焼いてあると英語にしても日本語のまま
        // 出るので、Prefab 側は初期値扱いにして毎回ここで差し替える。
        // 枠は75px・font50 で「翻」（50px）は入るが "Han"（101px）は溢れるため、
        // 役名と同じ方式で縮める。
        private void ApplyHanUnitLabel(string label)
        {
            if (hanUnitRoot == null) { return; }
            if (label == null || label.Length == 0) { return; }

            if (hanUnitText_ == null) { hanUnitText_ = hanUnitRoot.GetComponent<Text>(); }
            if (hanUnitText_ == null) { return; }

            if (hanUnitBaseFontSize_ <= 0) { hanUnitBaseFontSize_ = hanUnitText_.fontSize; }
            if (hanUnitBaseFontSize_ <= 0) { return; }

            hanUnitText_.text = label;
            hanUnitText_.fontSize = hanUnitBaseFontSize_;

            float limit = hanUnitText_.rectTransform.rect.width * YAKU_NAME_FIT_MARGIN;
            if (limit <= 1.0f) { return; }

            float need = hanUnitText_.preferredWidth;
            if (need <= limit) { return; }

            int size = (int)(hanUnitBaseFontSize_ * limit / need);
            if (size < YAKU_NAME_MIN_FONT_SIZE) { size = YAKU_NAME_MIN_FONT_SIZE; }

            hanUnitText_.fontSize = size;
        }

        // 和了情報の描画。文字列は Mahjong.cs 側が MahjongTranslation で整形済みのものを渡す。
        // 牌は牌コード（クラス冒頭コメント参照）で受け取り、2Dスロットに描く。
        //   agariCode      : 和了牌。-1で「和了牌なし」（流し満貫）
        //   openFaceDown   : アンカン両端など裏向き表示
        //   doraUp / uraUp : false のスロットは裏向き表示
        public void RenderAgari(
            string winnerName,
            string tagLabel,
            string multiRonLabel,
            string[] yakuNames,
            string[] yakuValueLabels,
            int numYaku,
            string hanLabel,
            string fuLabel,
            bool isYakuman,
            string titleLabel,
            string pointLabel,
            int winnerChara,
            int[] closedCodes,
            int numClosed,
            int agariCode,
            int[] openCodes,
            bool[] openFaceDown,
            bool[] openGroupStart,
            int numOpen,
            int[] doraCodes,
            bool[] doraUp,
            int[] uraCodes,
            bool[] uraUp,
            string hanUnitLabel)
        {
            // 同一表示の再呼び出し（UpdateViewは同期のたびに走る）ではアニメを
            // リスタートしない。
            string sig = winnerName + "|" + tagLabel + "|" + multiRonLabel + "|" + pointLabel + "|" + numYaku;
            bool newShow = !visible_ || sig != signature_;
            if (sig != signature_) { CacheForSpectate(yakuNames, numYaku, hanLabel, titleLabel, pointLabel, isYakuman, fuLabel, hanUnitLabel); }
            signature_ = sig;

            // ---- 段階表示のリセット（行ループより前に確定させる）----
            if (newShow)
            {
                // 和了演出待ち。ダブロンの2人目以降も「その和了者の演出 → 画面」の
                // 順になるよう、新規表示のたびに掛け直す。前の和了者のパネルが
                // 出たままだと待ちにならないので、ここで一旦畳む。
                showAtTime_ = showAtRequest_;
                holding_ = showAtTime_ > Time.time + 0.001f;

                if (holding_ && panelRoot != null && panelRoot.activeSelf)
                {
                    panelRoot.SetActive(false);
                }

                seqActive_ = sequentialReveal && planValid_;

                if (seqActive_)
                {
                    BeginSequentialShow(numYaku, isYakuman, hanLabel, titleLabel);
                }
                else
                {
                    RestoreRevealDefaults();
                }
            }

            // ---- テキスト類 ----
            if (winnerNameText != null) { winnerNameText.text = winnerName; }
            if (tsumoRonText != null) { tsumoRonText.text = tagLabel; }

            // タグ無し（流し満貫など）は背景画像ごと隠す。
            if (tsumoRonRoot != null) { tsumoRonRoot.SetActive(tagLabel != null && tagLabel.Length > 0); }
            if (scoreText != null)
            {
                scoreText.text = pointLabel;
                FitScoreText();
            }

            // hanText は段階表示中（カウントアップ中）は Update() 側が書く。
            if (hanText != null && (!seqActive_ || seqIsYakuman_ || scoreShown_)) { hanText.text = hanLabel; }
            if (fuText != null) { fuText.text = fuLabel; }

            // 役満時は hanText が「役満」になるため、静的な「翻」を消す。
            if (hanUnitRoot != null) { hanUnitRoot.SetActive(!isYakuman); }
            ApplyHanUnitLabel(hanUnitLabel);

            if (multiRonRoot != null)
            {
                multiRonRoot.SetActive(multiRonLabel != null && multiRonLabel.Length > 0);
            }
            if (multiRonText != null)
            {
                multiRonText.text = multiRonLabel;
            }

            // 称号バナー/点数/中央サークルは、段階表示中は開示済みになるまで隠す。
            if (titleRoot != null)
            {
                titleRoot.SetActive(titleLabel != null && titleLabel.Length > 0 && (!seqActive_ || titleShown_));
            }
            if (titleText != null)
            {
                titleText.text = titleLabel;
            }

            if (scoreRootResolved_ != null)
            {
                scoreRootResolved_.SetActive(!seqActive_ || scoreShown_);
            }

            if (circleRootResolved_ != null)
            {
                circleRootResolved_.SetActive(!seqActive_ || circleShown_);
            }
            else if (seqActive_ && !circleShown_)
            {
                // サークルのルートが特定できない構成では飜/符テキストを個別に隠す。
                if (hanText != null) { hanText.gameObject.SetActive(false); }
                if (fuText != null) { fuText.gameObject.SetActive(false); }
            }

            // ---- 役リスト ----
            // 段階表示中は、行の中身（役名・飜数バッジ）を RevealRowsUpToStep が持つ。
            // ここで書き直すと、ページ送りしたあとに1ページ目へ巻き戻る（UpdateView は
            // 同期のたびに走るので、次の同期で必ず巻き戻ってしまう）。
            seqNames_ = yakuNames;
            seqValueLabels_ = yakuValueLabels;

            int numRows = yakuNameTexts != null ? yakuNameTexts.Length : 0;

            // 段階表示を使わない即時表示（途中入室など）も、役の数で並べ方を決める。
            if (!seqActive_) { ApplyYakuLayout(Mathf.Min(numYaku, numRows)); }

            for (int i = 0; i < numRows; ++i)
            {
                if (seqActive_)
                {
                    bool revealed = rowRevealTimes_ != null && i < rowRevealTimes_.Length
                        && rowRevealTimes_[i] != 0.0f;

                    if (yakuRowRoots != null && i < yakuRowRoots.Length && yakuRowRoots[i] != null)
                    {
                        yakuRowRoots[i].SetActive(revealed);
                    }

                    continue;
                }

                bool used = i < numYaku;

                if (yakuRowRoots != null && i < yakuRowRoots.Length && yakuRowRoots[i] != null)
                {
                    yakuRowRoots[i].SetActive(used);
                }

                if (yakuNameTexts[i] != null)
                {
                    yakuNameTexts[i].text = used ? yakuNames[i] : "";
                    FitYakuNameText(yakuNameTexts[i]);
                }

                if (yakuValueTexts != null && i < yakuValueTexts.Length && yakuValueTexts[i] != null)
                {
                    string valueLabel = used ? yakuValueLabels[i] : "";
                    yakuValueTexts[i].text = valueLabel;

                    // 役満時（バッジなし＝空文字）はValueオブジェクトごと消す。
                    // 静的な「翻」Textを Value の子にしておけば一緒に消える。
                    yakuValueTexts[i].gameObject.SetActive(valueLabel.Length > 0);
                }
            }

            // ---- マスコット（和了者のPlayerDataで選択されているキャラ）----
            if (mascotImage != null)
            {
                Texture texture = defaultMascotTexture;
                var selfie = ResolveSelfieTexture(winnerChara, winnerPlayerId_);
                if (selfie != null)
                {
                    texture = selfie;
                }
                else if (charaMascotTextures != null && winnerChara >= 0 &&
                    winnerChara < charaMascotTextures.Length && charaMascotTextures[winnerChara] != null)
                {
                    texture = charaMascotTextures[winnerChara];
                }

                mascotImage.texture = texture;
                mascotImage.color = Color.white;
                mascotImage.raycastTarget = false;
                mascotImage.gameObject.SetActive(texture != null);
            }

            // ---- 牌スロットの描画（表示中の再呼び出しでも安価：uvRect/位置の代入のみ）----
            LayoutHandRow(closedCodes, numClosed, agariCode, openCodes, openFaceDown, openGroupStart, numOpen);
            LayoutIndicatorRow(doraSlots_, doraSlotRects_, doraCodes, doraUp);
            LayoutIndicatorRow(uraSlots_, uraSlotRects_, uraCodes, uraUp);

            // ---- 表示ON・入場アニメ ----
            // 和了演出（牌のエフェクト）待ちの間は伏せたまま。Update が時間で開く。
            if (panelRoot != null && !panelRoot.activeSelf && !holding_)
            {
                panelRoot.transform.localPosition = Vector3.down * 0.20f;
                panelRoot.transform.localScale = Vector3.one * 0.7f;
                panelRoot.SetActive(true);
            }

            visible_ = true;

            if (newShow)
            {
                enterTime_ = 0.0f;
                ResetEntryMotion();

                if (canvasGroup != null && enterSeconds > 0.001f)
                {
                    canvasGroup.alpha = 0.0f;
                }
            }
        }

        // ============================================================
        //  段階表示（雀魂風）
        // ============================================================

        // 新規表示の開始：全行を隠し、進行状態をリセットする。
        private void BeginSequentialShow(int numYaku, bool isYakuman, string hanLabel, string titleLabel)
        {
            CacheRevealGraphics(); // late joiner（RenderAgariがStartより先）対策

            int numRows = yakuRowRoots != null ? yakuRowRoots.Length : 0;

            // ページ送り：行数を超える役はページを切り替えて出すので、総数は行数で切らない。
            seqRowSlots_ = numRows;
            seqTotalYaku_ = numYaku;

            if (planRowSteps_ != null && seqTotalYaku_ > planRowSteps_.Length)
            {
                seqTotalYaku_ = planRowSteps_.Length;
            }

            seqRevealedCount_ = 0;
            seqNumRows_ = 0;   // 1ページ目はまだ1行も埋まっていない

            // 1ページ目に出す役の数で、列の分け方と行の大きさを決める（雀魂式）。
            ApplyYakuLayout(Mathf.Min(seqTotalYaku_, numRows));
            seqIsYakuman_ = isYakuman;
            seqTitleVisible_ = titleLabel != null && titleLabel.Length > 0;
            hanFinalLabel_ = hanLabel;

            // 表示対象の最大ステップ（全役の開示が終わったかの判定に使う）
            seqMaxRowStep_ = 0;
            for (int i = 0; i < seqTotalYaku_; ++i)
            {
                if (planRowSteps_ != null && i < planRowSteps_.Length && planRowSteps_[i] > seqMaxRowStep_)
                {
                    seqMaxRowStep_ = planRowSteps_[i];
                }
            }

            if (rowRevealTimes_ == null || rowRevealTimes_.Length != numRows)
            {
                rowRevealTimes_ = new float[numRows];
            }
            for (int i = 0; i < numRows; ++i)
            {
                rowRevealTimes_[i] = 0.0f;

                if (yakuNameTexts != null && i < yakuNameTexts.Length && yakuNameTexts[i] != null)
                {
                    yakuNameTexts[i].text = "";
                }

                if (yakuValueTexts != null && i < yakuValueTexts.Length && yakuValueTexts[i] != null)
                {
                    yakuValueTexts[i].text = "";
                    yakuValueTexts[i].gameObject.SetActive(false);
                }

                if (yakuRowRoots != null && i < yakuRowRoots.Length && yakuRowRoots[i] != null)
                {
                    yakuRowRoots[i].SetActive(false);
                }
            }

            shownStep_ = 0;
            fallbackStep_ = 0;
            lastAdvanceTime_ = Time.time;
            lastRowRevealTime_ = Time.time;
            revealedHan_ = 0;
            circleShown_ = false;
            circleRevealTime_ = 0.0f;
            scoreShown_ = false;
            scoreShownTime_ = 0.0f;
            scoreAnimDone_ = false;
            titleShown_ = false;
            titleShownTime_ = 0.0f;
            titleAnimDone_ = false;
        }

        // 毎フレームの進行：読み上げステップの取得→行開示→格ドーン→アニメ。
        private void UpdateSequentialReveal()
        {
            // 1) ステップ進行（読み上げクリップ開始数と自動送りの大きい方）
            int voiceStep = 0;
            if (sounds_ != null && planSeat_ >= 0)
            {
                voiceStep = sounds_.GetAgariStepCount(planSeat_);
            }

            int target = voiceStep > fallbackStep_ ? voiceStep : fallbackStep_;
            if (target > planTotalSteps_) { target = planTotalSteps_; }

            if (target > shownStep_)
            {
                shownStep_ = target;
                lastAdvanceTime_ = Time.time;
                RevealRowsUpToStep();
            }
            else if (shownStep_ < planTotalSteps_
                     && Time.time - lastAdvanceTime_ > GetStepInterval())
            {
                // 音声が来ない（ボイスなし/後入室/クリップ未設定/別卓Owner不在等）→自動送り。
                //
                // ただし役読みミュート（フリー対局の特殊ルール）では、最後の1ステップ＝
                // 格読みだけは**実際に鳴るボイスの開始**で進める。自動送りで先に消費すると
                // 点数の表示が finalDelaySeconds を待たずに前倒しで出て、称号バナーとの間が
                // 段位戦より詰まる（「点数が出た直後に称号」になる）。
                // ボイスが来ないまま固まらないよう、保険の時間で解放する。
                bool holdForRankVoice = HoldsLastStepForRankVoice()
                    && shownStep_ >= planTotalSteps_ - 1
                    && Time.time - lastAdvanceTime_ < RANK_STEP_RELEASE_SECONDS;

                if (!holdForRankVoice) { fallbackStep_ = shownStep_ + 1; }
            }

            // 2) 点数表示（全役表示から一拍。格読みが先に始まってしまった場合は即時）
            if (!scoreShown_)
            {
                bool allRowsRevealed = shownStep_ > seqMaxRowStep_;
                bool rankStarted = HasRankVoicePacing() && shownStep_ >= planTotalSteps_;

                // 称号バナーの予約時刻が分かったら、そこから scoreLeadSeconds だけ
                // 逆算して点数を先に出す。
                //
                // 「全役表示＋finalDelaySeconds」だけで待つと、点数と称号の間が
                //   （最後の役ボイスの長さ）＋rankVoicePreDelaySeconds − finalDelaySeconds
                // になる＝**最後に読んだ役のクリップ長がそのまま間の長さになる**。
                // 「ドラ」「一発」のような短いクリップで終わると間がほぼ消える
                // （2026-09-09 報告：点数と称号の間がたまに無くなる／短くなる）。
                float bannerCue = HasRankVoicePacing() && sounds_ != null
                    ? sounds_.GetAgariRankCueTime(planSeat_)
                    : 0.0f;
                bool leadFromBanner = allRowsRevealed && bannerCue > 0.0f
                    && Time.time >= bannerCue - scoreLeadSeconds;

                if (rankStarted || leadFromBanner
                    || (allRowsRevealed && Time.time - lastRowRevealTime_ >= finalDelaySeconds))
                {
                    ShowScore();
                }
            }

            // 3) 称号バナー「ドーン」（点数の後）。
            //    格読みクリップがあるときは SoundsV2 が予約したバナー時刻で出す
            //    （点数 →rankVoicePreDelaySeconds→ バナー＋SFX
            //      →rankVoiceLeadSeconds→ 格読みボイス）。予約が0＝読めない環境では
            //    従来どおり格読みの開始（ステップ到達）で出す。
            //    無いとき（流し満貫等）は点数から titleDelaySeconds 後に出す。
            if (scoreShown_ && !titleShown_ && seqTitleVisible_)
            {
                if (HasRankVoicePacing())
                {
                    float rankCue = sounds_ != null ? sounds_.GetAgariRankCueTime(planSeat_) : 0.0f;

                    if (rankCue > 0.0f)
                    {
                        // 予約が入っているなら、それだけを待つ。
                        // ステップ到達を OR で混ぜてはいけない：役読みミュート時は
                        // 役行が自動送りで進むため、最終ステップ到達が格読みボイスより
                        // 先に来て、バナーだけ先走る（段位戦とタイミングが変わる）。
                        if (Time.time >= rankCue) { ShowTitle(); }
                    }
                    else if (shownStep_ >= planTotalSteps_
                             && Time.time - scoreShownTime_ >= titleDelaySeconds)
                    {
                        // 予約が来ない環境（クリップ未設定・後入室・別卓Owner不在）。
                        // この経路は点数の表示条件（rankStarted）と同じフレームで成立するので、
                        // 間を入れないと点数と称号が同時に出る。
                        ShowTitle();
                    }
                }
                else if (Time.time - scoreShownTime_ >= titleDelaySeconds)
                {
                    ShowTitle();
                }
            }

            // 3) フェード/パンチのアニメ進行
            AnimateReveals();
        }

        // 開示済みステップまでの行を表示開始する（同一ステップ共有行は同時に出る）。
        private void RevealRowsUpToStep()
        {
            if (rowRevealTimes_ == null || planRowSteps_ == null)
            {
                return;
            }

            // この呼び出しで実際に行が出たか。同一ステップの行（複合役の同時表示）で
            // SFXを重ね撃ちしないよう、ループ後に1回だけ鳴らす。
            bool revealedAny = false;

            while (seqRevealedCount_ < seqTotalYaku_ && seqRowSlots_ > 0)
            {
                int yakuIndex = seqRevealedCount_;

                if (yakuIndex >= planRowSteps_.Length) { break; }
                if (planRowSteps_[yakuIndex] >= shownStep_) { break; }

                int row = yakuIndex % seqRowSlots_;

                // 行を使い切ったら、全行を消してから次のページの1行目を出す。
                // 中央サークルの飜数（revealedHan_）は通しなのでここでは触らない。
                if (row == 0 && yakuIndex > 0)
                {
                    ClearYakuRowsForNextPage();

                    // 次のページに出す役の数で並べ直す。
                    ApplyYakuLayout(Mathf.Min(seqTotalYaku_ - yakuIndex, seqRowSlots_));
                }

                // 行の中身はページごとに差し替わる。
                if (seqNames_ != null && yakuIndex < seqNames_.Length
                    && yakuNameTexts != null && row < yakuNameTexts.Length
                    && yakuNameTexts[row] != null)
                {
                    yakuNameTexts[row].text = seqNames_[yakuIndex];
                    FitYakuNameText(yakuNameTexts[row]);
                }

                if (seqValueLabels_ != null && yakuIndex < seqValueLabels_.Length
                    && yakuValueTexts != null && row < yakuValueTexts.Length
                    && yakuValueTexts[row] != null)
                {
                    string valueLabel = seqValueLabels_[yakuIndex];
                    yakuValueTexts[row].text = valueLabel;

                    // 役満時（バッジなし＝空文字）はValueオブジェクトごと消す。
                    yakuValueTexts[row].gameObject.SetActive(valueLabel.Length > 0);
                }

                rowRevealTimes_[row] = Time.time;
                lastRowRevealTime_ = Time.time;
                revealedAny = true;

                if (row + 1 > seqNumRows_) { seqNumRows_ = row + 1; }

                if (yakuRowRoots != null && row < yakuRowRoots.Length && yakuRowRoots[row] != null)
                {
                    yakuRowRoots[row].SetActive(true);
                }

                SetRowVisual(row, 0.0f); // 初回フレームのフラッシュ防止

                if (planRowValues_ != null && yakuIndex < planRowValues_.Length)
                {
                    revealedHan_ += planRowValues_[yakuIndex];
                }

                if (!circleShown_)
                {
                    ShowCircle();
                }

                // 中央サークルの飜数カウントアップ（雀魂風）。ページを送っても
                // ここは積み上げたまま。役満時は「役満」固定。
                if (!seqIsYakuman_ && hanText != null)
                {
                    hanText.text = revealedHan_.ToString();
                }

                ++seqRevealedCount_;
            }

            if (revealedAny && sounds_ != null)
            {
                sounds_.PlayAgariYakuRowSfxLocal();
            }
        }

        // 役行を使い切ったときに全行を消す。次の役はここから1行目に入る。
        // 飜数のカウンタ（revealedHan_）と点数・称号の進行には触らない。
        private void ClearYakuRowsForNextPage()
        {
            int numRows = rowRevealTimes_ != null ? rowRevealTimes_.Length : 0;

            for (int i = 0; i < numRows; ++i)
            {
                rowRevealTimes_[i] = 0.0f;

                if (yakuNameTexts != null && i < yakuNameTexts.Length && yakuNameTexts[i] != null)
                {
                    yakuNameTexts[i].text = "";
                }

                if (yakuValueTexts != null && i < yakuValueTexts.Length && yakuValueTexts[i] != null)
                {
                    yakuValueTexts[i].text = "";
                    yakuValueTexts[i].gameObject.SetActive(false);
                }

                if (yakuRowRoots != null && i < yakuRowRoots.Length && yakuRowRoots[i] != null)
                {
                    yakuRowRoots[i].SetActive(false);
                }
            }

            seqNumRows_ = 0;
        }

private void ShowCircle()
        {
            circleShown_ = true;
            circleRevealTime_ = Time.time;
            CacheMotionBases();

            if (circleRootResolved_ != null)
            {
                circleRootResolved_.SetActive(true);
            }
            else
            {
                if (hanText != null) { hanText.gameObject.SetActive(true); }
                if (fuText != null) { fuText.gameObject.SetActive(true); }
            }

            if (hanUnitRoot != null) { hanUnitRoot.SetActive(!seqIsYakuman_); }

            if (seqIsYakuman_ && hanText != null)
            {
                hanText.text = hanFinalLabel_;
            }

            SetGroupAlpha(circleGraphics_, circleGraphicColors_, 0.0f);

            if (circleRect_ != null)
            {
                circleRect_.anchoredPosition = circleBasePosition_ + new Vector2(0.0f, -CIRCLE_DROP_PIXELS);
                circleRect_.localScale = circleBaseScale_ * CIRCLE_START_SCALE;
            }
        }

        // 点数表示の開始（全役表示から一拍後）。
private void ShowScore()
        {
            scoreShown_ = true;
            scoreShownTime_ = Time.time;
            scoreAnimDone_ = false;
            CacheMotionBases();

            // 途中行の取りこぼしがあっても最終値へ揃える（12行超の役があふれた場合等）
            if (hanText != null) { hanText.text = hanFinalLabel_; }
            if (!circleShown_) { ShowCircle(); }

            if (scoreRootResolved_ != null)
            {
                scoreRootResolved_.SetActive(true);
                SetGroupAlpha(scoreGraphics_, scoreGraphicColors_, 0.0f);
            }

            if (scoreRect_ != null)
            {
                scoreRect_.anchoredPosition = scoreBasePosition_ + new Vector2(SCORE_SLIDE_PIXELS, 0.0f);
                scoreRect_.localScale = scoreBaseScale_ * scorePunchScale;
            }

            if (sounds_ != null) { sounds_.PlayAgariScoreSfxLocal(); }
        }

        // 称号バナー「ドーン」の開始（点数の後・格読みボイスの rankVoiceLeadSeconds 前）。
private void ShowTitle()
        {
            titleShown_ = true;
            titleShownTime_ = Time.time;
            titleAnimDone_ = false;
            CacheMotionBases();

            if (titleRoot != null)
            {
                titleRoot.SetActive(true);
                SetGroupAlpha(titleGraphics_, titleGraphicColors_, 0.0f);
            }

            if (titleRect_ != null)
            {
                titleRect_.anchoredPosition = titleBasePosition_ + new Vector2(-TITLE_SLIDE_PIXELS, 0.0f);
                titleRect_.localScale = new Vector3(
                    titleBaseScale_.x * titlePunchScale,
                    titleBaseScale_.y * TITLE_START_Y_SCALE,
                    titleBaseScale_.z);
            }

            // 満貫/跳満/倍満/三倍満/役満の「ドーン」に重ねるSFX（バナー表示と同時）。
            // 格読みボイスはこの後 rankVoiceLeadSeconds 遅れて鳴る（UpdateSequentialReveal 参照）。
            if (sounds_ != null) { sounds_.PlayAgariRankSfxLocal(); }
        }

        // フェード/パンチのフレーム更新（進行中の要素のみ書く。完了要素は負値マークで飛ばす）。
private void AnimateReveals()
        {
            // 役行のフェードイン＋右からの短いスライド。
            if (rowRevealTimes_ != null)
            {
                for (int i = 0; i < seqNumRows_ && i < rowRevealTimes_.Length; ++i)
                {
                    float startTime = rowRevealTimes_[i];
                    if (startTime <= 0.0f) { continue; } // 未表示 or 完了

                    float t = rowFadeSeconds > 0.001f
                        ? Mathf.Clamp01((Time.time - startTime) / rowFadeSeconds)
                        : 1.0f;

                    SetRowVisual(i, t);

                    if (t >= 1.0f)
                    {
                        rowRevealTimes_[i] = -1.0f; // 完了マーク（以降は塗り直さない）
                    }
                }
            }

            // 中央サークルのフェード＋下からの着地。
            if (circleShown_ && circleRevealTime_ > 0.0f)
            {
                float t = rowFadeSeconds > 0.001f
                    ? Mathf.Clamp01((Time.time - circleRevealTime_) / rowFadeSeconds)
                    : 1.0f;
                float ease = EaseOutCubic(t);

                SetGroupAlpha(circleGraphics_, circleGraphicColors_, t);

                if (circleRect_ != null)
                {
                    circleRect_.anchoredPosition = Vector2.Lerp(
                        circleBasePosition_ + new Vector2(0.0f, -CIRCLE_DROP_PIXELS),
                        circleBasePosition_,
                        ease);
                    circleRect_.localScale = circleBaseScale_
                        * Mathf.Lerp(CIRCLE_START_SCALE, 1.0f, ease);
                }

                if (t >= 1.0f) { circleRevealTime_ = -1.0f; }
            }

            // 点数は右から滑り込み、少し大きい状態から着地する。
            if (scoreShown_ && !scoreAnimDone_)
            {
                float t = titlePunchSeconds > 0.001f
                    ? Mathf.Clamp01((Time.time - scoreShownTime_) / titlePunchSeconds)
                    : 1.0f;
                float ease = EaseOutCubic(t);

                if (scoreRootResolved_ != null)
                {
                    SetGroupAlpha(scoreGraphics_, scoreGraphicColors_, ease);
                }

                if (scoreRect_ != null)
                {
                    scoreRect_.anchoredPosition = Vector2.Lerp(
                        scoreBasePosition_ + new Vector2(SCORE_SLIDE_PIXELS, 0.0f),
                        scoreBasePosition_,
                        ease);
                    scoreRect_.localScale = scoreBaseScale_
                        * Mathf.Lerp(scorePunchScale, 1.0f, ease);
                }

                if (t >= 1.0f) { scoreAnimDone_ = true; }
            }

            // 称号は左から横長に叩き込み、点数より強い余韻を残す。
            if (titleShown_ && !titleAnimDone_)
            {
                float dt = Time.time - titleShownTime_;

                float tPunch = titlePunchSeconds > 0.001f
                    ? Mathf.Clamp01(dt / titlePunchSeconds)
                    : 1.0f;
                float ease = EaseOutCubic(tPunch);

                float tFade = titleFadeSeconds > 0.001f
                    ? Mathf.Clamp01(dt / titleFadeSeconds)
                    : 1.0f;

                if (titleRoot != null)
                {
                    SetGroupAlpha(titleGraphics_, titleGraphicColors_, tFade);
                }

                if (titleRect_ != null)
                {
                    titleRect_.anchoredPosition = Vector2.Lerp(
                        titleBasePosition_ + new Vector2(-TITLE_SLIDE_PIXELS, 0.0f),
                        titleBasePosition_,
                        ease);
                    titleRect_.localScale = new Vector3(
                        Mathf.Lerp(titleBaseScale_.x * titlePunchScale, titleBaseScale_.x, ease),
                        Mathf.Lerp(titleBaseScale_.y * TITLE_START_Y_SCALE, titleBaseScale_.y, ease),
                        titleBaseScale_.z);
                }

                if (tPunch >= 1.0f && tFade >= 1.0f) { titleAnimDone_ = true; }
            }
        }

        // 行のアルファ＋スケールを一括設定（キャッシュ済みGraphicの色に基準alphaを乗算）。
private void SetRowVisual(int row, float t)
        {
            float clamped = Mathf.Clamp01(t);
            float ease = EaseOutCubic(clamped);

            if (rowGraphics_ != null && row < rowGraphics_.Length)
            {
                SetGroupAlpha(rowGraphics_[row], rowGraphicColors_[row], clamped);
            }

            if (rowRects_ != null && row < rowRects_.Length && rowRects_[row] != null)
            {
                rowRects_[row].anchoredPosition = Vector2.Lerp(
                    rowBasePositions_[row] + new Vector2(ROW_SLIDE_PIXELS, 0.0f),
                    rowBasePositions_[row],
                    ease);
                rowRects_[row].localScale = rowBaseScales_[row]
                    * Mathf.Lerp(rowPunchScale, 1.0f, ease);
            }
        }

        private void SetGroupAlpha(Graphic[] graphics, Color[] baseColors, float alpha)
        {
            if (graphics == null || baseColors == null)
            {
                return;
            }

            for (int i = 0; i < graphics.Length; ++i)
            {
                if (graphics[i] == null) { continue; }

                Color c = baseColors[i];
                graphics[i].color = new Color(c.r, c.g, c.b, c.a * alpha);
            }
        }

        // 段階表示OFF（従来の一括表示）に戻すとき、フェードの痕跡を全て初期状態へ復元する。
private void RestoreRevealDefaults()
        {
            CacheRevealGraphics();
            CacheMotionBases();

            if (rowGraphics_ != null)
            {
                for (int i = 0; i < rowGraphics_.Length; ++i)
                {
                    SetGroupAlpha(rowGraphics_[i], rowGraphicColors_[i], 1.0f);
                }
            }

            SetGroupAlpha(titleGraphics_, titleGraphicColors_, 1.0f);
            SetGroupAlpha(scoreGraphics_, scoreGraphicColors_, 1.0f);
            SetGroupAlpha(circleGraphics_, circleGraphicColors_, 1.0f);
            RestoreMotionDefaults();

            if (scoreRootResolved_ != null)
            {
                scoreRootResolved_.SetActive(true);
            }

            if (circleRootResolved_ != null) { circleRootResolved_.SetActive(true); }
            if (hanText != null) { hanText.gameObject.SetActive(true); }
            if (fuText != null) { fuText.gameObject.SetActive(true); }
        }

        // フェード対象のGraphic（Text/Image等）と基準色を一度だけキャッシュする。
        // CanvasGroupの手動追加なしで行単位のフェードを可能にするための構成。
        private void CacheRevealGraphics()
        {
            if (graphicsCached_)
            {
                return;
            }

            graphicsCached_ = true;

            int numRows = yakuRowRoots != null ? yakuRowRoots.Length : 0;
            rowGraphics_ = new Graphic[numRows][];
            rowGraphicColors_ = new Color[numRows][];

            for (int i = 0; i < numRows; ++i)
            {
                if (yakuRowRoots[i] == null) { continue; }

                var graphics = yakuRowRoots[i].GetComponentsInChildren<Graphic>(true);
                rowGraphics_[i] = graphics;
                rowGraphicColors_[i] = CaptureColors(graphics);
            }

            if (titleRoot != null)
            {
                titleGraphics_ = titleRoot.GetComponentsInChildren<Graphic>(true);
                titleGraphicColors_ = CaptureColors(titleGraphics_);
            }

            scoreRootResolved_ = scoreRoot != null ? scoreRoot : (scoreText != null ? scoreText.gameObject : null);
            if (scoreRootResolved_ != null)
            {
                scoreGraphics_ = scoreRootResolved_.GetComponentsInChildren<Graphic>(true);
                scoreGraphicColors_ = CaptureColors(scoreGraphics_);
            }

            // 中央サークルのルート解決：明示指定 > hanTextの親（Content直下ならルート扱いしない）
            circleRootResolved_ = hanCircleRoot;
            if (circleRootResolved_ == null && hanText != null)
            {
                Transform parent = hanText.transform.parent;
                bool parentIsContent = contentRect != null && parent == contentRect.transform;
                bool parentIsPanel = panelRoot != null && parent != null && parent.gameObject == panelRoot;

                if (parent != null && !parentIsContent && !parentIsPanel)
                {
                    circleRootResolved_ = parent.gameObject;
                }
            }

            if (circleRootResolved_ != null)
            {
                circleGraphics_ = circleRootResolved_.GetComponentsInChildren<Graphic>(true);
                circleGraphicColors_ = CaptureColors(circleGraphics_);
            }
            else if (hanText != null)
            {
                // ルート不明時は飜/符テキストのみをフェード対象にする
                var list = new Graphic[2];
                list[0] = hanText;
                list[1] = fuText;
                circleGraphics_ = list;
                circleGraphicColors_ = CaptureColors(list);
            }
        }

        private Color[] CaptureColors(Graphic[] graphics)
        {
            if (graphics == null)
            {
                return null;
            }

            var colors = new Color[graphics.Length];

            for (int i = 0; i < graphics.Length; ++i)
            {
                if (graphics[i] != null)
                {
                    colors[i] = graphics[i].color;
                }
            }

            return colors;
        }

        // ============================================================
        //  内部処理
        // ============================================================

        // テンプレRawImageを複製して行の子にする。純ローカル・非同期オブジェクト。
        private RawImage CreateSlot(RectTransform parent)
        {
            if (tileTemplate == null || parent == null)
            {
                return null;
            }

            var go = Instantiate(tileTemplate.gameObject);
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);

            // 行の左端・上下中央を原点に、左→右へ並べる前提のアンカー/ピボット。
            rt.anchorMin = new Vector2(0.0f, 0.5f);
            rt.anchorMax = new Vector2(0.0f, 0.5f);
            rt.pivot = new Vector2(0.0f, 0.5f);
            rt.sizeDelta = ((RectTransform)tileTemplate.transform).sizeDelta;
            rt.localScale = Vector3.one;

            go.SetActive(false);
            return go.GetComponent<RawImage>();
        }

        private float TileWidth()
        {
            if (tileTemplate == null)
            {
                return 60.0f;
            }

            return ((RectTransform)tileTemplate.transform).sizeDelta.x;
        }

        // 和了手牌の行：門前牌 → 副露（グループ間ギャップ）→（ギャップ）→ 和了牌
        private void LayoutHandRow(
            int[] closedCodes,
            int numClosed,
            int agariCode,
            int[] openCodes,
            bool[] openFaceDown,
            bool[] openGroupStart,
            int numOpen)
        {
            if (handSlots_ == null)
            {
                return;
            }

            float w = TileWidth();
            float spacing = w * tileSpacingFactor;
            float groupGap = w * groupGapFactor;
            float agariGap = w * agariGapFactor;

            int slot = 0;
            float x = 0.0f;

            for (int i = 0; i < numClosed && slot < NUM_HAND_SLOTS; ++i)
            {
                int code = closedCodes != null && i < closedCodes.Length ? closedCodes[i] : -1;
                SetSlot(handSlots_[slot], handSlotRects_[slot], x, code, false);
                ++slot;
                x += spacing;
            }

            for (int i = 0; i < numOpen && slot < NUM_HAND_SLOTS; ++i)
            {
                if (openGroupStart != null && i < openGroupStart.Length && openGroupStart[i])
                {
                    x += groupGap;
                }

                int code = openCodes != null && i < openCodes.Length ? openCodes[i] : -1;
                bool faceDown = openFaceDown != null && i < openFaceDown.Length && openFaceDown[i];
                SetSlot(handSlots_[slot], handSlotRects_[slot], x, code, faceDown);
                ++slot;
                x += spacing;
            }

            if (agariCode >= 0 && slot < NUM_HAND_SLOTS)
            {
                x += agariGap;
                SetSlot(handSlots_[slot], handSlotRects_[slot], x, agariCode, false);
                ++slot;
            }

            // 残りスロットは非表示
            for (; slot < NUM_HAND_SLOTS; ++slot)
            {
                if (handSlots_[slot] != null)
                {
                    handSlots_[slot].gameObject.SetActive(false);
                }
            }
        }

        // ドラ/裏ドラの行（5スロット固定。faceUp=false は裏向き表示）
        private void LayoutIndicatorRow(RawImage[] slots, RectTransform[] rects, int[] codes, bool[] faceUp)
        {
            if (slots == null)
            {
                return;
            }

            float spacing = TileWidth() * tileSpacingFactor;
            float x = 0.0f;

            for (int i = 0; i < slots.Length; ++i)
            {
                int code = codes != null && i < codes.Length ? codes[i] : -1;
                bool up = faceUp != null && i < faceUp.Length && faceUp[i];
                SetSlot(slots[i], rects[i], x, code, !up);
                x += spacing;
            }
        }

        // スロット1枚の表示設定。code<0 は非表示（ただし faceDown 時は牌背として表示）。
        private void SetSlot(RawImage img, RectTransform rt, float x, int code, bool faceDown)
        {
            if (img == null || rt == null)
            {
                return;
            }

            if (code < 0 && !faceDown)
            {
                img.gameObject.SetActive(false);
                return;
            }

            rt.anchoredPosition = new Vector2(x, 0.0f);
            img.uvRect = GetTileUv(faceDown ? -1 : code);
            img.color = faceDown ? faceDownColor : Color.white;
            img.gameObject.SetActive(true);
        }

        // 牌コード → tiles.png 上のUV矩形。-1（裏向き）は最上段のblankセル。
        private Rect GetTileUv(int code)
        {
            int col;
            int row; // 0=最上段

            if (code < 0)
            {
                col = 0; row = 0;                        // blank（牌背として使用）
            }
            else if (code < 9)
            {
                col = code; row = 5;                     // 一萬..九萬
            }
            else if (code < 18)
            {
                col = code - 9; row = 3;                 // 1筒..9筒
            }
            else if (code < 27)
            {
                col = code - 18; row = 4;                // 1索..9索
            }
            else if (code < 34)
            {
                col = code - 27; row = 2;                // 東南西北白發中
            }
            else if (code == 34)
            {
                col = 8; row = 2;                        // 赤五萬
            }
            else if (code == 35)
            {
                col = 8; row = 1;                        // 赤5筒
            }
            else
            {
                col = 7; row = 2;                        // 赤5索
            }

            float x0 = atlasColX_[col] + ATLAS_INSET;
            float x1 = atlasColX_[col + 1] - ATLAS_INSET;
            float yTop = atlasRowY_[row] + ATLAS_INSET;
            float yBottom = atlasRowY_[row + 1] - ATLAS_INSET;

            // uvRect は左下原点
            return new Rect(
                x0 / ATLAS_SIZE,
                1.0f - yBottom / ATLAS_SIZE,
                (x1 - x0) / ATLAS_SIZE,
                (yBottom - yTop) / ATLAS_SIZE);
        }

    

private float EaseOutCubic(float t)
        {
            float inv = 1.0f - Mathf.Clamp01(t);
            return 1.0f - inv * inv * inv;
        }

        // ============================================================
        //  役行の並べ方（雀魂式：役の数で列の分け方と行の大きさを変える）
        // ------------------------------------------------------------
        //   1〜4役 … 左の列だけ
        //   5〜8役 … 左に4行、残りを右
        //   9役〜  … 左右に半分ずつ（左が多め）。列が6行を超えたら、今の6行ぶんの高さに
        //            収まるよう行の間隔を詰め、行もその割合で縮める
        //   1ページの上限は行の器の数（16）。超えたぶんは従来どおりページ送り。
        //
        // 列の形（左は「＜」・右は「＞」に折れる斜め）は、Prefab に置いた最初の12行
        // （左6・右6）の位置を「型」として起動時に控え、縦位置に応じて型の折れ線から
        // 横位置を補間して決める。行数が変わっても同じ線の上に並ぶので形が崩れない。
        // 位置は実行時だけ書き換える（Prefab・シーンの値は触らない）。
        // ============================================================
        private const int LAYOUT_TEMPLATE_ROWS = 6;   // 型にする1列ぶんの行数（Prefabの配置）
        private const int LAYOUT_SINGLE_COLUMN_MAX = 4;
        private const int LAYOUT_TWO_COLUMN_LEFT = 4;
        private const int LAYOUT_TWO_COLUMN_MAX = 8;

        private bool layoutTplValid_ = false;
        private float[] layoutTplLeftY_;
        private float[] layoutTplLeftX_;
        private float[] layoutTplRightY_;
        private float[] layoutTplRightX_;
        private float layoutTplTop_;
        private float layoutTplBottom_;
        private float layoutTplStep_;
        private Vector3 layoutTplScale_;

        private void CaptureYakuLayoutTemplate()
        {
            layoutTplValid_ = false;
            if (rowRects_ == null || rowRects_.Length < LAYOUT_TEMPLATE_ROWS * 2) { return; }
            for (int i = 0; i < LAYOUT_TEMPLATE_ROWS * 2; ++i)
            {
                if (rowRects_[i] == null) { return; }
            }

            layoutTplLeftY_ = new float[LAYOUT_TEMPLATE_ROWS];
            layoutTplLeftX_ = new float[LAYOUT_TEMPLATE_ROWS];
            layoutTplRightY_ = new float[LAYOUT_TEMPLATE_ROWS];
            layoutTplRightX_ = new float[LAYOUT_TEMPLATE_ROWS];
            for (int i = 0; i < LAYOUT_TEMPLATE_ROWS; ++i)
            {
                layoutTplLeftY_[i] = rowBasePositions_[i].y;
                layoutTplLeftX_[i] = rowBasePositions_[i].x;
                layoutTplRightY_[i] = rowBasePositions_[LAYOUT_TEMPLATE_ROWS + i].y;
                layoutTplRightX_[i] = rowBasePositions_[LAYOUT_TEMPLATE_ROWS + i].x;
            }

            layoutTplTop_ = layoutTplLeftY_[0];
            layoutTplBottom_ = layoutTplLeftY_[LAYOUT_TEMPLATE_ROWS - 1];
            layoutTplStep_ = layoutTplLeftY_[0] - layoutTplLeftY_[1];
            layoutTplScale_ = rowBaseScales_[0];
            layoutTplValid_ = layoutTplStep_ > 0.0f;
        }

        // 型の折れ線（上から下へ並んだ点）の上で、縦位置 y の横位置を求める（両端は外挿）。
        private float InterpolateTemplateX(float[] ys, float[] xs, float y)
        {
            int n = ys.Length;
            int k = 0;
            if (y >= ys[0]) { k = 0; }
            else if (y <= ys[n - 1]) { k = n - 2; }
            else
            {
                for (int i = 0; i < n - 1; ++i)
                {
                    if (y <= ys[i] && y >= ys[i + 1]) { k = i; break; }
                }
            }

            float dy = ys[k] - ys[k + 1];
            if (dy == 0.0f) { return xs[k]; }
            float t = (ys[k] - y) / dy;
            return xs[k] + (xs[k + 1] - xs[k]) * t;
        }

        // このページに出す役の数 count に合わせて、行 0..count-1 の基準位置と大きさを決める。
        // 行のアニメ（SetRowVisual）はこの基準位置・大きさから動かす。
        private void ApplyYakuLayout(int count)
        {
            CacheMotionBases();
            if (!layoutTplValid_ || rowRects_ == null) { return; }

            int numRows = rowRects_.Length;
            if (count > numRows) { count = numRows; }
            if (count <= 0) { return; }

            int left;
            if (count <= LAYOUT_SINGLE_COLUMN_MAX) { left = count; }
            else if (count <= LAYOUT_TWO_COLUMN_MAX) { left = LAYOUT_TWO_COLUMN_LEFT; }
            else { left = (count + 1) / 2; }
            int right = count - left;

            int longest = left > right ? left : right;
            float step = layoutTplStep_;
            if (longest > LAYOUT_TEMPLATE_ROWS)
            {
                step = (layoutTplTop_ - layoutTplBottom_) / (longest - 1);
            }
            float shrink = step < layoutTplStep_ ? step / layoutTplStep_ : 1.0f;
            Vector3 scale = layoutTplScale_ * shrink;

            for (int i = 0; i < count; ++i)
            {
                if (rowRects_[i] == null) { continue; }

                bool isLeft = i < left;
                int slot = isLeft ? i : i - left;
                float y = layoutTplTop_ - slot * step;
                float x = isLeft
                    ? InterpolateTemplateX(layoutTplLeftY_, layoutTplLeftX_, y)
                    : InterpolateTemplateX(layoutTplRightY_, layoutTplRightX_, y);

                rowBasePositions_[i] = new Vector2(x, y);
                rowBaseScales_[i] = scale;
                rowRects_[i].anchoredPosition = rowBasePositions_[i];
                rowRects_[i].localScale = scale;
            }
        }

        private void CacheMotionBases()
        {
            if (motionBasesCached_)
            {
                return;
            }

            if (contentRect != null)
            {
                contentBasePosition_ = contentRect.anchoredPosition;
                contentBaseScale_ = contentRect.localScale;
                contentBaseEuler_ = contentRect.localEulerAngles;
            }

            mascotRect_ = mascotImage != null ? (RectTransform)mascotImage.transform : null;
            if (mascotRect_ != null)
            {
                mascotBasePosition_ = mascotRect_.anchoredPosition;
                mascotBaseScale_ = mascotRect_.localScale;
            }

            int numRows = yakuRowRoots != null ? yakuRowRoots.Length : 0;
            rowRects_ = new RectTransform[numRows];
            rowBasePositions_ = new Vector2[numRows];
            rowBaseScales_ = new Vector3[numRows];
            for (int i = 0; i < numRows; ++i)
            {
                if (yakuRowRoots[i] == null) { continue; }
                RectTransform rt = (RectTransform)yakuRowRoots[i].transform;
                rowRects_[i] = rt;
                rowBasePositions_[i] = rt.anchoredPosition;
                rowBaseScales_[i] = rt.localScale;
            }

            CaptureYakuLayoutTemplate();

            circleRect_ = circleRootResolved_ != null ? (RectTransform)circleRootResolved_.transform : null;
            if (circleRect_ != null)
            {
                circleBasePosition_ = circleRect_.anchoredPosition;
                circleBaseScale_ = circleRect_.localScale;
            }

            scoreRect_ = scoreRootResolved_ != null ? (RectTransform)scoreRootResolved_.transform : null;
            if (scoreRect_ != null)
            {
                scoreBasePosition_ = scoreRect_.anchoredPosition;
                scoreBaseScale_ = scoreRect_.localScale;
            }

            titleRect_ = titleRoot != null ? (RectTransform)titleRoot.transform : null;
            if (titleRect_ != null)
            {
                titleBasePosition_ = titleRect_.anchoredPosition;
                titleBaseScale_ = titleRect_.localScale;
            }

            motionBasesCached_ = true;
        }

        private void ResetEntryMotion()
        {
            CacheMotionBases();

            if (enterSeconds <= 0.001f)
            {
                RestoreEntryMotion();
                return;
            }

            if (contentRect != null)
            {
                contentRect.anchoredPosition = contentBasePosition_ + new Vector2(-ENTER_SLIDE_PIXELS, 0.0f);
                contentRect.localScale = contentBaseScale_ * ENTER_START_SCALE;
                contentRect.localEulerAngles = contentBaseEuler_ + new Vector3(0.0f, 0.0f, -ENTER_TILT_DEGREES);
            }

            if (mascotRect_ != null)
            {
                mascotRect_.anchoredPosition = mascotBasePosition_
                    + new Vector2(-MASCOT_SLIDE_PIXELS, -MASCOT_DROP_PIXELS);
                mascotRect_.localScale = mascotBaseScale_ * MASCOT_START_SCALE;
            }
        }

        private void RestoreEntryMotion()
        {
            CacheMotionBases();

            if (contentRect != null)
            {
                contentRect.anchoredPosition = contentBasePosition_;
                contentRect.localScale = contentBaseScale_;
                contentRect.localEulerAngles = contentBaseEuler_;
            }

            if (mascotRect_ != null)
            {
                mascotRect_.anchoredPosition = mascotBasePosition_;
                mascotRect_.localScale = mascotBaseScale_;
            }
        }

        private void RestoreMotionDefaults()
        {
            CacheMotionBases();
            RestoreEntryMotion();

            if (rowRects_ != null)
            {
                for (int i = 0; i < rowRects_.Length; ++i)
                {
                    if (rowRects_[i] == null) { continue; }
                    rowRects_[i].anchoredPosition = rowBasePositions_[i];
                    rowRects_[i].localScale = rowBaseScales_[i];
                }
            }

            if (circleRect_ != null)
            {
                circleRect_.anchoredPosition = circleBasePosition_;
                circleRect_.localScale = circleBaseScale_;
            }

            if (scoreRect_ != null)
            {
                scoreRect_.anchoredPosition = scoreBasePosition_;
                scoreRect_.localScale = scoreBaseScale_;
            }

            if (titleRect_ != null)
            {
                titleRect_.anchoredPosition = titleBasePosition_;
                titleRect_.localScale = titleBaseScale_;
            }
        }


        private void CacheForSpectate(string[] yakuNames, int numYaku, string hanLabel, string titleLabel, string pointLabel, bool isYakuman, string fuLabel, string hanUnitLabel)
        {
            specFuLabel_ = fuLabel != null ? fuLabel : "";
            specHanUnitLabel_ = hanUnitLabel != null ? hanUnitLabel : "";
            ++specSerial_;
            specNumYaku_ = 0;
            if (yakuNames != null)
            {
                if (specYakuNames_ == null || specYakuNames_.Length < yakuNames.Length) { specYakuNames_ = new string[yakuNames.Length]; }
                for (int i = 0; i < numYaku && i < yakuNames.Length; ++i)
                {
                    specYakuNames_[i] = yakuNames[i];
                    ++specNumYaku_;
                }
            }
            specHanLabel_ = hanLabel != null ? hanLabel : "";
            specTitleLabel_ = titleLabel != null ? titleLabel : "";
            specPointLabel_ = pointLabel != null ? pointLabel : "";
            specYakuman_ = isYakuman;
        }

        public int Spectate_AgariSerial() { return specSerial_; }
        // あがり画面のパネルが実際に開いているか（和了演出待ちで伏せている間は false）。観戦モニターの和了者パネルを同時に出す
        public bool Spectate_IsPanelShown() { return visible_ && !holding_; }
        public int Spectate_NumYaku() { return specNumYaku_; }
        public string Spectate_YakuName(int i)
        {
            if (specYakuNames_ == null || i < 0 || i >= specNumYaku_) { return ""; }
            return specYakuNames_[i] != null ? specYakuNames_[i] : "";
        }
        public string Spectate_HanLabel() { return specHanLabel_; }
        public string Spectate_TitleLabel() { return specTitleLabel_; }
        public string Spectate_PointLabel() { return specPointLabel_; }
        public bool Spectate_IsYakuman() { return specYakuman_; }
        // 符（例「40符」「 40 Fu」。役満は空）と翻の単位（「翻」「Han」）。卓の言語で作られている
        public string Spectate_FuLabel() { return specFuLabel_; }
        public string Spectate_HanUnitLabel() { return specHanUnitLabel_; }
}
}
