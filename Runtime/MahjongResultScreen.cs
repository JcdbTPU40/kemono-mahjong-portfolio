using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    //  終局（半荘終了）結果画面 ＝ 雀魂スタイルの順位表
    // ------------------------------------------------------------
    //  設計思想（あがり画面 MahjongAgariScreen と同一系統）:
    //   ・純ローカル表示。同期変数を一切持たない（NoVariableSync）。
    //     順位・点数は points / firstLeader / numPlayers など Mahjong.cs が
    //     既に全クライアントへ同期している値だけから作られるため、各自が
    //     UpdateView 末尾のフックで同じ入力から描けば表示は必ず一致する。
    //   ・アニメは Graphic.color の alpha ＋ RectTransform の位置/スケールのみ
    //     （Quest での Canvas rebuild・パーティクルのコストを避ける）。
    //   ・UIは World Space Canvas 必須（Screen Space Overlay は描画されない）。
    //   ・スクリプト自身は「常時アクティブな親」に置くこと。表示ON/OFFは
    //     panelRoot（子）の SetActive で行う（非アクティブな UdonBehaviour は
    //     外部から叩けないため）。
    // ------------------------------------------------------------
    //  行の並びは「順位順」。index 0 = 1位。三麻では numRows=3 で呼ばれ、
    //  余った行は非表示になる。
    //
    //  PT（段位ポイント）欄は自分の行だけ埋まる。他家のPT増減は相手の
    //  対局開始時の段位に依存し、それはこちらのクライアントからは正しく
    //  読めない（PlayerDataは対局後に更新済みの値しか見えない）ため、
    //  推測値を出さずに空欄にする＝Mahjong.cs 側が空文字を渡す契約。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class MahjongResultScreen : UdonSharpBehaviour
    {
        [Header("必須参照")]
        [Tooltip("パネル全体のルート（World Space Canvas を含む子）。表示ON/OFF対象。")]
        public GameObject panelRoot;

        [Tooltip("見出しの Text（「終局」）")]
        public Text headerText;

        [Header("順位行（index 0 = 1位。4要素すべて同じ長さ・同じ順で割り当てる）")]
        [Tooltip("行のルート GameObject（未使用行を非表示にする）")]
        public GameObject[] rowRoots;

        [Tooltip("順位の数字 Text（「1」）")]
        public Text[] rankTexts;

        [Tooltip("順位の単位 Text（「位」。英語卓では空文字が入る）")]
        public Text[] rankUnitTexts;

        [Tooltip("「自家」タグの Text（文言は言語に合わせて Mahjong.cs から渡される）")]
        public Text[] selfTagTexts;

        [Tooltip("プレイヤー名 Text")]
        public Text[] nameTexts;

        [Tooltip("最終点数 Text（「66400」）")]
        public Text[] scoreTexts;

        [Tooltip("素点差 Text（「+41.4」）。開始点との差を1000点単位で表示。")]
        public Text[] deltaTexts;

        [Tooltip("段位ポイント増減 Text（「+77」）。自分の行以外は空文字で渡される。")]
        public Text[] ptTexts;

        [Tooltip("PT欄のルート（ラベルごと隠す用）。空文字のときは行ごと非表示にする。")]
        public GameObject[] ptRoots;

        [Tooltip("「自家」タグのルート（自分の行だけ表示）")]
        public GameObject[] selfTagRoots;

        [Tooltip("キャラ絵の RawImage（行ごと）")]
        public RawImage[] portraitImages;

        [Tooltip("行の背景 Image（1位だけ色を変える）")]
        public Image[] rowBackgrounds;

        [Header("キャラ絵")]
        [Tooltip("キャラID別テクスチャ（0=Vol / 1=Shine / 2=Tsumuntema）")]
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

        // 行ごとのプレイヤーID（charas と同じ並び）。Mahjong 側が RenderResult の直前に入れる。
        private int[] rowPlayerIds_;

        public void SetRowPlayerIds(int[] playerIds) { rowPlayerIds_ = playerIds; }


        [Tooltip("CPU席・PlayerData未復元時のフォールバック。nullなら RawImage ごと非表示。")]
        public Texture defaultMascotTexture;

        [Header("配色")]
        [Tooltip("1位の行の背景色")]
        public Color topRowColor = new Color(0.62f, 0.13f, 0.18f, 0.92f);

        [Tooltip("2位以下の行の背景色")]
        public Color normalRowColor = new Color(0.10f, 0.16f, 0.30f, 0.88f);

        [Tooltip("プラスの素点差／PTの色")]
        public Color gainColor = new Color(0.45f, 0.95f, 0.60f);

        [Tooltip("マイナスの素点差／PTの色")]
        public Color lossColor = new Color(0.98f, 0.35f, 0.38f);

        [Header("挙動")]
        [Tooltip("パネルをローカルプレイヤーの方へ（ヨーのみ）向ける。VRでの推奨。")]
        public bool billboardToLocalPlayer = true;

        [Tooltip("パネル全体の表示アニメ秒数（alpha＋スケール）。0で無効。")]
        public float enterSeconds = 0.35f;

        [Tooltip("フェード対象の CanvasGroup（任意）")]
        public CanvasGroup canvasGroup;

        [Tooltip("スケールアニメ対象の RectTransform（任意。Canvas直下のContent推奨）")]
        public RectTransform contentRect;

        [Header("順次表示（雀魂風・1位から1行ずつ）")]
        [Tooltip("行を1つずつ出す。OFFで一括表示。")]
        public bool sequentialReveal = true;

        [Tooltip("行が出る間隔（秒）")]
        public float rowRevealInterval = 0.45f;

        [Tooltip("1行のフェードイン秒数")]
        public float rowFadeSeconds = 0.3f;

        [Tooltip("行が滑り込む距離（px。右から入る）")]
        public float rowSlideDistance = 140.0f;

        [Tooltip("点数のカウントアップ秒数（開始点→最終点）。0で即時表示。")]
        public float scoreCountSeconds = 0.7f;

        [Tooltip("行が滑り込むときの行き過ぎ量（easeOutBack）。0で行き過ぎなし。")]
        public float rowOvershoot = 1.6f;

        [Header("見出し「終局」のドーン")]
        [Tooltip("見出しのルート（未設定なら headerText 自身を使う）")]
        public GameObject headerRoot;

        [Tooltip("見出しのパンチ秒数。0で無効。")]
        public float headerPunchSeconds = 0.4f;

        [Tooltip("見出しの初期スケール（ここから1.0へ収束）")]
        public float headerPunchScale = 1.5f;

        [Header("素点差・PT・自家タグの後乗せ")]
        [Tooltip("点数のカウントアップ完了から、素点差／PT／自家タグが出るまでの間（秒）")]
        public float valuePopDelay = 0.12f;

        [Tooltip("後乗せ要素のポップ秒数。0で即時表示。")]
        public float valuePopSeconds = 0.3f;

        [Tooltip("後乗せ要素の初期スケール（ここから1.0へ収束）")]
        public float valuePopScale = 1.6f;

        [Header("段位変動パネル（順位表のあとに出す自分専用の画面）")]
        [Tooltip("段位パネルのルート（順位表と入れ替えで表示する）")]
        public GameObject rankPanelRoot;

        [Tooltip("段位アイコンの RawImage")]
        public RawImage rankIconImage;

        [Tooltip("大段位index別アイコン（0=初獣 … 5=天獣）")]
        public Texture[] rankIconTextures;

        [Tooltip("段位名 Text（「初獣★1」「天獣 Lv.3」）")]
        public Text rankNameText;

        [Tooltip("段位ポイント Text（「535 / 1000」）")]
        public Text rankValueText;

        [Tooltip("段位ゲージ（Image Type=Filled / Horizontal）")]
        public Image rankGaugeFill;

        [Tooltip("増減 Text（「+77」）")]
        public Text rankDeltaText;

        [Tooltip("上向き矢印のルート（増加時のみ表示）")]
        public GameObject rankArrowUpRoot;

        [Tooltip("下向き矢印のルート（減少時のみ表示）")]
        public GameObject rankArrowDownRoot;

        [Tooltip("順位表の行が出そろってから NEXT ボタンが出るまでの間（秒）。waitForNextButton が OFF のときは自動送りまでの待ち時間になる。")]
        public float rankScreenDelay = 1.2f;

        [Tooltip("順位表 → 段位パネル の送りをNEXTボタン待ちにする。OFFなら rankScreenDelay 秒で自動送り（従来動作）。")]
        public bool waitForNextButton = true;

        [Tooltip("順位表のフェードアウト＝段位パネルのフェードイン秒数")]
        public float rankFadeSeconds = 0.35f;

        [Tooltip("ゲージが伸びる秒数")]
        public float gaugeSeconds = 0.9f;

        [Tooltip("昇段／降段時、ゲージが端まで行ってから段位が切り替わるまでの溜め（秒）")]
        public float promotionHoldSeconds = 0.5f;

        [Tooltip("昇段時の段位名・アイコンのパンチスケール")]
        public float promotionPunchScale = 1.5f;

        // ---- 表示中の状態 ----
        private bool visible_ = false;
        private float enterTime_ = 0.0f;
        private float showStartTime_ = 0.0f;
        private string signature_ = "";
        private int numRows_ = 0;
        private int startScore_ = 25000;
        private int[] finalScores_;

        // ---- 行ごとの進行状態 ----
        private float[] rowRevealTimes_;   // 0=未表示 / 正=フェード中(開始時刻) / 負=完了
        private bool[] rowScoreDone_;
        private float[] rowScoreEndTime_;  // 点数カウントが終わった時刻（後乗せの起点）
        private float[] rowValueTimes_;    // 後乗せ：0=未 / 正=進行中(開始時刻) / 負=完了
        private bool[] rowHasPt_;
        private bool[] rowIsSelf_;

        // ---- 見出しの進行状態 ----
        private float headerTime_ = -1.0f; // 負=完了/未開始
        private GameObject headerRootResolved_;

        // ---- 段位パネルの進行状態 ----
        private const int PHASE_ROWS = 0;   // 順位表を見せている
        private const int PHASE_SWITCH = 1; // 順位表フェードアウト中
        private const int PHASE_RANK = 2;   // 段位パネル表示中
        private bool hasRank_ = false;
        private int rankPhase_ = PHASE_ROWS;
        private float rowsSettledTime_ = 0.0f;
        private float switchStartTime_ = 0.0f;
        // Mahjong.cs（同期済み resultStep_）から渡される画面送り。0=順位表 / 1=段位ポイント。
        private int requestedStep_ = 0;
        private float rankStartTime_ = 0.0f;
        private bool rankNameSwitched_ = false;
        private Graphic[] rankGraphics_;
        private Color[] rankColors_;

        // 段位データ（Mahjong.cs から RenderRank で受け取る）
        private string rankNameBefore_ = "";
        private string rankNameAfter_ = "";
        private int rankIconBefore_ = 0;
        private int rankIconAfter_ = 0;
        private int rankValueBefore_ = 0;
        private int rankValueAfter_ = 0;
        private int rankMaxBefore_ = 1;
        private int rankMaxAfter_ = 1;
        private bool rankTenths_ = false;
        private string rankDeltaLabel_ = "";
        private int rankDeltaSign_ = 0;
        private int rankLvDelta_ = 0;

        // ---- グラフィックキャッシュ（CanvasGroupを行ごとに足さずにフェードする）----
        private bool cached_ = false;
        private Graphic[][] rowGraphics_;
        private Color[][] rowGraphicColors_;
        private RectTransform[] rowRects_;
        private Vector2[] rowBasePos_;

        // 後乗せ要素は行フェードとは別タイミングで動くので、行とは独立にキャッシュする。
        private Graphic[][] ptGraphics_;
        private Color[][] ptColors_;
        private Graphic[][] selfGraphics_;
        private Color[][] selfColors_;
        private Color[] deltaColors_;
        private Graphic[] headerGraphics_;
        private Color[] headerColors_;

        void Start()
        {
            CacheRows();

            // late joiner で RenderResult が Start() より先に走った場合、ここで無条件に
            // 隠すと表示済みの結果画面が消える。既に表示中なら隠さない。
            if (!visible_)
            {
                if (rankPanelRoot != null) { rankPanelRoot.SetActive(false); }
                if (panelRoot != null) { panelRoot.SetActive(false); }
            }
        }

        void Update()
        {
            if (!visible_)
            {
                return;
            }

            // 入場アニメ（alpha + スケールのみ。Quest配慮）
            if (enterSeconds > 0.001f && enterTime_ < enterSeconds)
            {
                enterTime_ += Time.deltaTime;
                float t = Mathf.Clamp01(enterTime_ / enterSeconds);

                if (canvasGroup != null)
                {
                    canvasGroup.alpha = t;
                }

                if (contentRect != null)
                {
                    float s = Mathf.Lerp(0.94f, 1.0f, t);
                    contentRect.localScale = new Vector3(s, s, s);
                }
            }

            UpdateHeaderPunch();

            if (rankPhase_ == PHASE_ROWS)
            {
                UpdateRowReveal();
                UpdateRankHandoff();
            }
            else if (rankPhase_ == PHASE_SWITCH)
            {
                UpdateRankSwitch();
            }
            else
            {
                UpdateRankPanel();
            }

            RefreshUnlockNotice();

            // ローカルプレイヤーへヨーのみで向ける（純ローカル＝各自が正面から見える）
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

        // ---- チュンビーム解放のお知らせ（順位表の下の1〜2行・自分の画面だけ）----
        // 配線はせず、Build Result Screens が作る Content/UnlockNotice を名前で引く
        // （Prefab の Wire だけでは Udon 側へ入らない事故を避ける）。
        private Text unlockNoticeText_;
        private bool unlockNoticeResolved_ = false;
        private string unlockNotice_ = "";
        private bool unlockNoticeShown_ = false;

        public void SetUnlockNotice(string text)
        {
            if (text == null) { text = ""; }
            ResolveUnlockNotice();
            if (unlockNoticeText_ == null) { return; }
            if (text != unlockNotice_)
            {
                unlockNotice_ = text;
                unlockNoticeText_.text = text;
            }
            RefreshUnlockNotice();
        }

        private void ResolveUnlockNotice()
        {
            if (unlockNoticeResolved_) { return; }
            unlockNoticeResolved_ = true;
            if (panelRoot == null) { return; }
            var t = panelRoot.transform.Find("Canvas/Content/UnlockNotice");
            if (t != null) { unlockNoticeText_ = t.GetComponent<Text>(); }
        }

        // 順位表を見せている間だけ出す（段位パネルと重ねない）。
        private void RefreshUnlockNotice()
        {
            if (unlockNoticeText_ == null) { return; }
            bool show = visible_ && rankPhase_ == PHASE_ROWS && unlockNotice_.Length > 0;
            if (show == unlockNoticeShown_) { return; }
            unlockNoticeShown_ = show;
            unlockNoticeText_.gameObject.SetActive(show);
        }

        public void HideScreen()
        {
            if (!visible_)
            {
                return;
            }

            visible_ = false;
            rankPhase_ = PHASE_ROWS;
            requestedStep_ = 0;
            rowsSettledTime_ = 0.0f;

            if (rankPanelRoot != null)
            {
                rankPanelRoot.SetActive(false);
            }

            if (panelRoot != null)
            {
                panelRoot.SetActive(false);
            }
        }

        // 順位表の描画。文字列は Mahjong.cs 側が整形済みのものを渡す。
        //   numRows      : 表示する行数（四麻=4 / 三麻=3）
        //   rankLabels   : 「1」「2」… （index 0 = 1位）
        //   names        : 表示名（CPU・退席時は家名フォールバック済み）
        //   finalScores  : 最終点数（カウントアップの終点）
        //   startScore   : 開始点（カウントアップの始点。四麻25000/三麻35000）
        //   deltaLabels  : 素点差（「+41.4」）
        //   deltaSigns   : 素点差の符号（+1/0/-1。色分け用）
        //   ptLabels     : 段位pt増減（自分の行だけ。他家は空文字）
        //   ptSigns      : ptの符号（+1/0/-1）
        //   charas       : キャラID（キャラ絵の差し替え用）
        //   selfFlags    : 自分の行か（「自家」タグ）
        public void RenderResult(
            string headerLabel,
            string rankUnitLabel,
            string selfTagLabel,
            int numRows,
            string[] rankLabels,
            string[] names,
            int[] finalScores,
            int startScore,
            string[] deltaLabels,
            int[] deltaSigns,
            string[] ptLabels,
            int[] ptSigns,
            int[] charas,
            bool[] selfFlags)
        {
            CacheRows(); // late joiner（RenderResult が Start より先）対策

            int maxRows = rowRoots != null ? rowRoots.Length : 0;
            if (numRows > maxRows) { numRows = maxRows; }
            if (numRows < 0) { numRows = 0; }

            // 同一表示の再呼び出し（UpdateView は同期のたびに走る）ではアニメを
            // リスタートしない。点数・PTまで含めた署名で比較する。
            string sig = headerLabel + "|" + numRows;
            for (int i = 0; i < numRows; ++i)
            {
                sig += "|" + names[i] + "," + finalScores[i] + "," + deltaLabels[i] + "," + ptLabels[i];
            }

            bool newShow = !visible_ || sig != signature_;
            signature_ = sig;
            numRows_ = numRows;
            startScore_ = startScore;
            finalScores_ = finalScores;

            if (newShow)
            {
                BeginShow(numRows);
            }

            if (headerText != null) { headerText.text = headerLabel; }

            // 段位パネルへ移った後の再描画で、順位表の行が復活しないようにする。
            bool rowsRetired = rankPhase_ != PHASE_ROWS;

            for (int i = 0; i < maxRows; ++i)
            {
                bool used = i < numRows;
                bool revealed = used && !rowsRetired && (!sequentialReveal
                    || (rowRevealTimes_ != null && i < rowRevealTimes_.Length && rowRevealTimes_[i] != 0.0f));

                if (rowRoots[i] != null)
                {
                    rowRoots[i].SetActive(revealed);
                }

                if (!used) { continue; }

                if (rankTexts != null && i < rankTexts.Length && rankTexts[i] != null)
                {
                    rankTexts[i].text = rankLabels[i];
                }

                if (rankUnitTexts != null && i < rankUnitTexts.Length && rankUnitTexts[i] != null)
                {
                    rankUnitTexts[i].text = rankUnitLabel;
                }

                if (selfTagTexts != null && i < selfTagTexts.Length && selfTagTexts[i] != null)
                {
                    selfTagTexts[i].text = selfTagLabel;
                }

                if (nameTexts != null && i < nameTexts.Length && nameTexts[i] != null)
                {
                    nameTexts[i].text = names[i];
                }

                // 点数はカウントアップ中は UpdateRowReveal が書く。
                if (scoreTexts != null && i < scoreTexts.Length && scoreTexts[i] != null)
                {
                    bool counting = sequentialReveal && scoreCountSeconds > 0.001f
                        && rowScoreDone_ != null && i < rowScoreDone_.Length && !rowScoreDone_[i];
                    if (!counting)
                    {
                        scoreTexts[i].text = finalScores[i].ToString();
                    }
                }

                // 後乗せ（素点差／PT／自家タグ）はポップ開始まで出さない。
                // ポップ進行中は色を書き換えない（フェード途中の色を基準色にしないため）。
                bool popped = !sequentialReveal
                    || (rowValueTimes_ != null && i < rowValueTimes_.Length && rowValueTimes_[i] != 0.0f);
                bool popIdle = rowValueTimes_ == null || i >= rowValueTimes_.Length || rowValueTimes_[i] <= 0.0f;
                bool hasPt = ptLabels[i] != null && ptLabels[i].Length > 0;

                if (rowHasPt_ != null && i < rowHasPt_.Length) { rowHasPt_[i] = hasPt; }
                if (rowIsSelf_ != null && i < rowIsSelf_.Length) { rowIsSelf_[i] = selfFlags[i]; }

                if (deltaTexts != null && i < deltaTexts.Length && deltaTexts[i] != null)
                {
                    deltaTexts[i].text = deltaLabels[i];
                    if (popIdle)
                    {
                        SetBaseColor(i, deltaTexts[i], SignColor(i, deltaTexts[i], deltaSigns[i]));
                    }
                    deltaTexts[i].gameObject.SetActive(popped);
                }

                if (ptTexts != null && i < ptTexts.Length && ptTexts[i] != null)
                {
                    ptTexts[i].text = ptLabels[i];
                    if (popIdle)
                    {
                        SetBaseColor(i, ptTexts[i], SignColor(i, ptTexts[i], ptSigns[i]));
                    }
                }

                if (ptRoots != null && i < ptRoots.Length && ptRoots[i] != null)
                {
                    ptRoots[i].SetActive(hasPt && popped);
                }

                if (selfTagRoots != null && i < selfTagRoots.Length && selfTagRoots[i] != null)
                {
                    selfTagRoots[i].SetActive(selfFlags[i] && popped);
                }

                if (rowBackgrounds != null && i < rowBackgrounds.Length && rowBackgrounds[i] != null)
                {
                    SetBaseColor(i, rowBackgrounds[i], i == 0 ? topRowColor : normalRowColor);
                }

                if (portraitImages != null && i < portraitImages.Length && portraitImages[i] != null)
                {
                    Texture texture = defaultMascotTexture;
                    int chara = charas[i];
                    int rowPlayerId = (rowPlayerIds_ != null && i < rowPlayerIds_.Length) ? rowPlayerIds_[i] : 0;
                    var selfie = ResolveSelfieTexture(chara, rowPlayerId);
                    if (selfie != null)
                    {
                        texture = selfie;
                    }
                    else if (charaMascotTextures != null && chara >= 0 && chara < charaMascotTextures.Length
                        && charaMascotTextures[chara] != null)
                    {
                        texture = charaMascotTextures[chara];
                    }

                    portraitImages[i].texture = texture;
                    portraitImages[i].raycastTarget = false;
                    portraitImages[i].gameObject.SetActive(texture != null);
                }
            }

            if (!rowsRetired)
            {
                ReapplyRowAlpha();
            }

            if (panelRoot != null && !panelRoot.activeSelf)
            {
                panelRoot.transform.localPosition = Vector3.down * 0.20f;
                panelRoot.transform.localScale = Vector3.one * 0.7f;
                panelRoot.SetActive(true);
            }

            visible_ = true;

            if (newShow)
            {
                enterTime_ = 0.0f;

                if (canvasGroup != null && enterSeconds > 0.001f)
                {
                    canvasGroup.alpha = 0.0f;
                }
            }
        }

        // ============================================================
        //  段位変動パネル（雀魂の終局後の段位画面）
        // ------------------------------------------------------------
        //  自分専用の画面。段位戦でローカルプレイヤーが対局していたときだけ
        //  Mahjong.cs が RenderRank でデータを渡し、順位表の演出が終わってから
        //  入れ替わりで出る。観戦者・フリー対局・手動開始卓では ClearRank が
        //  呼ばれ、順位表が出たままになる。
        // ============================================================

        // 段位データの受け渡し。RenderResult より前に毎回呼ぶこと。
        //   iconBefore/After : 大段位index（0=初獣 … 5=天獣）
        //   valueBefore/After: 段位pt（天獣なら天珠x10）
        //   maxBefore/After  : 昇段pt（天獣なら 200＝20.0）
        //   tenths           : true なら値を小数1桁で表示する（天珠）
        //   lvDelta          : +1以上=昇段 / -1以下=降段 / 0=据え置き
        public void RenderRank(
            string nameBefore,
            string nameAfter,
            int iconBefore,
            int iconAfter,
            int valueBefore,
            int valueAfter,
            int maxBefore,
            int maxAfter,
            bool tenths,
            string deltaLabel,
            int deltaSign,
            int lvDelta)
        {
            hasRank_ = true;
            rankNameBefore_ = nameBefore;
            rankNameAfter_ = nameAfter;
            rankIconBefore_ = iconBefore;
            rankIconAfter_ = iconAfter;
            rankValueBefore_ = valueBefore;
            rankValueAfter_ = valueAfter;
            rankMaxBefore_ = maxBefore > 0 ? maxBefore : 1;
            rankMaxAfter_ = maxAfter > 0 ? maxAfter : 1;
            rankTenths_ = tenths;
            rankDeltaLabel_ = deltaLabel;
            rankDeltaSign_ = deltaSign;
            rankLvDelta_ = lvDelta;
        }

        // 段位パネルを出さない構成（観戦者・フリー対局・段位無効卓）。
        public void ClearRank()
        {
            hasRank_ = false;
        }

        // 画面送り（Mahjong.cs の同期済み resultStep_）。0=順位表 / 1=段位ポイント。
        // RenderResult より前に毎回呼ぶこと。実際の切り替えは Update から行う
        // （RenderResult と同じフレームで rankPhase_ を進めると、行がフェードせず消える）。
        public void SetStep(int step)
        {
            requestedStep_ = step;
        }

        // 順位表の演出が全部終わったか（全行のポップ完了）。
        private bool AreRowsSettled()
        {
            if (rowValueTimes_ == null) { return false; }

            for (int i = 0; i < numRows_ && i < rowValueTimes_.Length; ++i)
            {
                if (rowValueTimes_[i] >= 0.0f) { return false; }
            }
            return true;
        }

        // 順位表 → 段位パネルへの切り替え。NEXT（requestedStep_）で移る。
        // 段位パネルを持たないクライアント（観戦・フリー対局）は順位表のまま待つ。
        private void UpdateRankHandoff()
        {
            // 行が出そろった時刻は段位パネルの有無にかかわらず記録する。以前は段位パネルの無い人
            // （フリー対局・観戦）で記録されず、順位表に NEXT が出ないうえ、その人が卓Owner だと
            // IsRowsBusy が永久に true＝自動送りも止まり、30秒の自動リセットしか効かなかった。
            if (!AreRowsSettled())
            {
                rowsSettledTime_ = 0.0f;
                return;
            }

            if (rowsSettledTime_ == 0.0f)
            {
                rowsSettledTime_ = Time.time;
                return;
            }

            if (!hasRank_ || rankPanelRoot == null) { return; }

            if (requestedStep_ < 1) { return; }

            // NEXT が早くて行の演出が途中なら、先に出し切ってから入れ替える。
            SnapRowsComplete();

            rankPhase_ = PHASE_SWITCH;
            switchStartTime_ = Time.time;
        }

        // 順位表の演出がまだ進行中か（＝NEXTを出せる状態になっていない）。卓（Mahjong.cs）の
        // 自動送りが順位表を追い越さないためのガード。IsWaitingForNext と違い段位パネルの
        // 有無（hasRank_）を見ないので、フリー対局・観戦Ownerでも必ず false に落ちる
        // ＝自動送りが永久に止まることはない。
        public bool IsRowsBusy()
        {
            if (!visible_) { return false; }
            if (rankPhase_ != PHASE_ROWS) { return false; }
            if (!AreRowsSettled()) { return true; }
            if (rowsSettledTime_ == 0.0f) { return true; }
            return Time.time - rowsSettledTime_ < rankScreenDelay;
        }

        // 順位表を見終わり、段位パネルへ送れる状態か。卓（Mahjong.cs）がNEXTボタンを
        // 出す条件として毎フレーム問い合わせる。rankScreenDelay は「ボタンが出るまでの間」。
        public bool IsWaitingForNext()
        {
            if (!visible_ || !waitForNextButton) { return false; }
            if (!hasRank_ || rankPanelRoot == null) { return false; }
            if (rankPhase_ != PHASE_ROWS) { return false; }
            if (!AreRowsSettled()) { return false; }
            if (rowsSettledTime_ == 0.0f) { return false; }
            return Time.time - rowsSettledTime_ >= rankScreenDelay;
        }

        // 順位表を見終えて NEXT を出せる状態か（段位パネルの有無を問わない）。
        // IsWaitingForNext は「段位パネルへ送れるか」なので、段位パネルの無い人では常に false になる。
        public bool IsRankingReadyForNext()
        {
            if (!visible_) { return false; }
            if (rankPhase_ != PHASE_ROWS) { return false; }
            if (!AreRowsSettled()) { return false; }
            if (rowsSettledTime_ == 0.0f) { return false; }
            return Time.time - rowsSettledTime_ >= rankScreenDelay;
        }

        // 段位パネルのゲージ演出が終わってから NEXT を出すまでの間（秒）。
        // 公開フィールドにしないのは、既存の8卓のUdonヒープへ値を入れ直す手間を避けるため。
        private const float RANK_NEXT_DELAY_SECONDS = 0.4f;

        // 段位ポイントの画面を見終えられる状態か。卓（Mahjong.cs）が段位ポイントの NEXT
        // （＝押すと卓がリセットされる）を出す条件・自動送りを数え始める条件に使う。
        // 段位パネルを持たない人（フリー対局・観戦・段位無効）と非表示中は即 true
        // ＝自動送りが永久に止まることはない。
        public bool IsRankPanelSettled()
        {
            if (!visible_) { return true; }
            if (!hasRank_ || rankPanelRoot == null) { return true; }
            if (rankPhase_ != PHASE_RANK) { return false; }

            float total = rankFadeSeconds + gaugeSeconds + RANK_NEXT_DELAY_SECONDS;
            if (rankLvDelta_ != 0) { total += promotionHoldSeconds; }
            return Time.time - rankStartTime_ >= total;
        }

        // 行の順次表示・点数カウント・後乗せを一気に完了状態へ飛ばす（NEXTで送られたとき用）。
        private void SnapRowsComplete()
        {
            if (rowRevealTimes_ == null) { return; }

            for (int i = 0; i < numRows_ && i < rowRevealTimes_.Length; ++i)
            {
                if (rowRoots != null && i < rowRoots.Length && rowRoots[i] != null)
                {
                    rowRoots[i].SetActive(true);
                }

                rowRevealTimes_[i] = -1.0f;
                rowScoreDone_[i] = true;
                rowScoreEndTime_[i] = Time.time;

                if (scoreTexts != null && i < scoreTexts.Length && scoreTexts[i] != null
                    && finalScores_ != null && i < finalScores_.Length)
                {
                    scoreTexts[i].text = finalScores_[i].ToString();
                }

                // 未ポップの後乗せ（素点差／PT／自家タグ）は表示状態にしてから完了扱いにする。
                if (rowValueTimes_[i] >= 0.0f)
                {
                    ShowValueObjects(i);
                }
                rowValueTimes_[i] = -1.0f;

                SetRowVisual(i, 1.0f);
                SetValueVisual(i, 1.0f);
            }
        }

        // 順位表のフェードアウト（行が右へ滑って消える）。
        private void UpdateRankSwitch()
        {
            float t = rankFadeSeconds > 0.001f
                ? Mathf.Clamp01((Time.time - switchStartTime_) / rankFadeSeconds)
                : 1.0f;

            for (int i = 0; i < numRows_ && rowRoots != null && i < rowRoots.Length; ++i)
            {
                SetRowVisual(i, 1.0f - t);
                SetValueVisual(i, 1.0f - t);
            }

            if (t < 1.0f)
            {
                return;
            }

            for (int i = 0; i < numRows_ && i < rowRoots.Length; ++i)
            {
                if (rowRoots[i] != null) { rowRoots[i].SetActive(false); }
            }

            BeginRankPanel();
        }

        private void BeginRankPanel()
        {
            rankPhase_ = PHASE_RANK;
            rankStartTime_ = Time.time;
            rankNameSwitched_ = rankLvDelta_ == 0;

            if (rankPanelRoot != null) { rankPanelRoot.SetActive(true); }

            ApplyRankIdentity(rankLvDelta_ == 0 ? rankNameAfter_ : rankNameBefore_,
                              rankLvDelta_ == 0 ? rankIconAfter_ : rankIconBefore_);

            if (rankDeltaText != null)
            {
                rankDeltaText.text = rankDeltaLabel_;
                rankDeltaText.color = rankDeltaSign_ >= 0 ? gainColor : lossColor;
            }

            if (rankArrowUpRoot != null) { rankArrowUpRoot.SetActive(rankDeltaSign_ > 0); }
            if (rankArrowDownRoot != null) { rankArrowDownRoot.SetActive(rankDeltaSign_ < 0); }

            ApplyGauge(rankValueBefore_, rankMaxBefore_);
            SetGroupAlpha(rankGraphics_, rankColors_, 0.0f);
        }

        // 段位パネルの進行：フェードイン → ゲージ伸長 →（昇段なら）段位切り替え → 続き。
        private void UpdateRankPanel()
        {
            float elapsed = Time.time - rankStartTime_;

            // 1) フェードイン
            float fade = rankFadeSeconds > 0.001f ? Mathf.Clamp01(elapsed / rankFadeSeconds) : 1.0f;
            SetGroupAlpha(rankGraphics_, rankColors_, fade);

            float gaugeElapsed = elapsed - rankFadeSeconds;
            if (gaugeElapsed < 0.0f)
            {
                return;
            }

            // 2) ゲージ。昇段・降段は「端まで振り切る → 段位が変わる → 続きを伸ばす」の2段。
            if (rankLvDelta_ == 0)
            {
                float t = gaugeSeconds > 0.001f ? Mathf.Clamp01(gaugeElapsed / gaugeSeconds) : 1.0f;
                float ease = 1.0f - (1.0f - t) * (1.0f - t);
                ApplyGauge(Mathf.RoundToInt(Mathf.Lerp(rankValueBefore_, rankValueAfter_, ease)), rankMaxAfter_);
                return;
            }

            float half = gaugeSeconds * 0.5f;
            int edgeValue = rankLvDelta_ > 0 ? rankMaxBefore_ : 0;

            if (gaugeElapsed < half)
            {
                float t = half > 0.001f ? Mathf.Clamp01(gaugeElapsed / half) : 1.0f;
                float ease = 1.0f - (1.0f - t) * (1.0f - t);
                ApplyGauge(Mathf.RoundToInt(Mathf.Lerp(rankValueBefore_, edgeValue, ease)), rankMaxBefore_);
                return;
            }

            // 3) 段位の切り替え（1回だけ）＋パンチ
            if (!rankNameSwitched_)
            {
                rankNameSwitched_ = true;
                ApplyRankIdentity(rankNameAfter_, rankIconAfter_);
                ApplyGauge(rankLvDelta_ > 0 ? 0 : rankMaxAfter_, rankMaxAfter_);
            }

            float holdEnd = half + promotionHoldSeconds;

            if (gaugeElapsed < holdEnd)
            {
                // 溜め中：段位名をパンチさせながら止める
                float p = promotionHoldSeconds > 0.001f
                    ? Mathf.Clamp01((gaugeElapsed - half) / promotionHoldSeconds)
                    : 1.0f;
                float ease = 1.0f - (1.0f - p) * (1.0f - p);
                float s = Mathf.Lerp(promotionPunchScale, 1.0f, ease);

                if (rankNameText != null) { rankNameText.transform.localScale = new Vector3(s, s, 1.0f); }
                if (rankIconImage != null) { rankIconImage.transform.localScale = new Vector3(s, s, 1.0f); }
                return;
            }

            // 4) 新しい段位での続き
            {
                float t = half > 0.001f ? Mathf.Clamp01((gaugeElapsed - holdEnd) / half) : 1.0f;
                float ease = 1.0f - (1.0f - t) * (1.0f - t);
                int from = rankLvDelta_ > 0 ? 0 : rankMaxAfter_;
                ApplyGauge(Mathf.RoundToInt(Mathf.Lerp(from, rankValueAfter_, ease)), rankMaxAfter_);
            }
        }

        private void ApplyRankIdentity(string rankName, int iconIndex)
        {
            if (rankNameText != null)
            {
                rankNameText.text = rankName;
                rankNameText.transform.localScale = Vector3.one;
            }

            if (rankIconImage != null)
            {
                Texture texture = null;
                if (rankIconTextures != null && iconIndex >= 0 && iconIndex < rankIconTextures.Length)
                {
                    texture = rankIconTextures[iconIndex];
                }

                rankIconImage.texture = texture;
                rankIconImage.transform.localScale = Vector3.one;
                rankIconImage.gameObject.SetActive(texture != null);
            }
        }

        private void ApplyGauge(int value, int max)
        {
            if (max <= 0) { max = 1; }
            if (value < 0) { value = 0; }
            if (value > max) { value = max; }

            if (rankGaugeFill != null)
            {
                rankGaugeFill.fillAmount = (float)value / (float)max;
            }

            if (rankValueText != null)
            {
                rankValueText.text = FormatRankValue(value) + " / " + FormatRankValue(max);
            }
        }

        private string FormatRankValue(int value)
        {
            if (!rankTenths_)
            {
                return value.ToString();
            }
            return (value / 10).ToString() + "." + (value % 10).ToString();
        }

        // ============================================================
        //  内部：順次表示
        // ============================================================

        private void BeginShow(int numRows)
        {
            showStartTime_ = Time.time;
            headerTime_ = sequentialReveal && headerPunchSeconds > 0.001f ? Time.time : -1.0f;

            // 段位パネルは必ず順位表の後。表示のやり直しでは順位表から始める。
            rankPhase_ = PHASE_ROWS;
            rowsSettledTime_ = 0.0f;
            rankNameSwitched_ = false;
            if (rankPanelRoot != null) { rankPanelRoot.SetActive(false); }

            int maxRows = rowRoots != null ? rowRoots.Length : 0;

            if (rowRevealTimes_ == null || rowRevealTimes_.Length != maxRows)
            {
                rowRevealTimes_ = new float[maxRows];
                rowScoreDone_ = new bool[maxRows];
                rowScoreEndTime_ = new float[maxRows];
                rowValueTimes_ = new float[maxRows];
            }

            for (int i = 0; i < maxRows; ++i)
            {
                // 順次表示OFFなら最初から全行を開示済み扱いにする。
                rowRevealTimes_[i] = sequentialReveal ? 0.0f : -1.0f;
                rowScoreDone_[i] = !sequentialReveal || scoreCountSeconds <= 0.001f;
                rowScoreEndTime_[i] = 0.0f;
                rowValueTimes_[i] = sequentialReveal ? 0.0f : -1.0f;

                if (!sequentialReveal)
                {
                    SetRowVisual(i, 1.0f);
                    SetValueVisual(i, 1.0f);
                }
            }

            if (headerTime_ > 0.0f)
            {
                SetGroupAlpha(headerGraphics_, headerColors_, 0.0f);
            }
            else
            {
                SetGroupAlpha(headerGraphics_, headerColors_, 1.0f);
                if (headerRootResolved_ != null) { headerRootResolved_.transform.localScale = Vector3.one; }
            }
        }

        // 見出し「終局」のドーン（スケールは素早く収束・アルファは少し遅れて追いつく）。
        private void UpdateHeaderPunch()
        {
            if (headerTime_ <= 0.0f)
            {
                return;
            }

            float t = Mathf.Clamp01((Time.time - headerTime_) / headerPunchSeconds);
            float ease = 1.0f - (1.0f - t) * (1.0f - t); // easeOutQuad

            SetGroupAlpha(headerGraphics_, headerColors_, ease);

            if (headerRootResolved_ != null)
            {
                float s = Mathf.Lerp(headerPunchScale, 1.0f, ease);
                headerRootResolved_.transform.localScale = new Vector3(s, s, 1.0f);
            }

            if (t >= 1.0f) { headerTime_ = -1.0f; }
        }

        private void UpdateRowReveal()
        {
            if (!sequentialReveal || rowRevealTimes_ == null)
            {
                return;
            }

            float elapsed = Time.time - showStartTime_;

            for (int i = 0; i < numRows_ && i < rowRevealTimes_.Length; ++i)
            {
                // 1) 開示タイミング（1位から順に rowRevealInterval 間隔）。
                //    見出しのドーンが終わってから1行目が出るよう、頭に一拍入れる。
                if (rowRevealTimes_[i] == 0.0f)
                {
                    if (elapsed < headerPunchSeconds + i * rowRevealInterval) { continue; }

                    rowRevealTimes_[i] = Time.time;

                    if (rowRoots[i] != null) { rowRoots[i].SetActive(true); }
                    SetRowVisual(i, 0.0f); // 初回フレームのフラッシュ防止

                    // カウントアップ無効の構成では、この時点を後乗せの起点にする。
                    if (rowScoreDone_[i]) { rowScoreEndTime_[i] = Time.time; }
                }

                // 2) フェード＋スライド（スライドは easeOutBack で少し行き過ぎてから収まる）
                if (rowRevealTimes_[i] > 0.0f)
                {
                    float t = rowFadeSeconds > 0.001f
                        ? Mathf.Clamp01((Time.time - rowRevealTimes_[i]) / rowFadeSeconds)
                        : 1.0f;

                    SetRowVisual(i, t);

                    if (t >= 1.0f) { rowRevealTimes_[i] = -1.0f; }
                }

                // 3) 点数のカウントアップ（開示時刻からの経過で進める）
                UpdateScoreCount(i);

                // 4) 素点差／PT／自家タグの後乗せポップ
                UpdateValuePop(i);
            }
        }

        // 点数が止まったあと一拍おいて、素点差・PT・自家タグをポップさせる。
        private void UpdateValuePop(int row)
        {
            if (rowValueTimes_ == null || row >= rowValueTimes_.Length) { return; }
            if (rowValueTimes_[row] < 0.0f) { return; } // 完了

            if (rowValueTimes_[row] == 0.0f)
            {
                if (!rowScoreDone_[row]) { return; }
                if (Time.time - rowScoreEndTime_[row] < valuePopDelay) { return; }

                rowValueTimes_[row] = Time.time;
                ShowValueObjects(row);
                SetValueVisual(row, 0.0f); // 初回フレームのフラッシュ防止
            }

            float t = valuePopSeconds > 0.001f
                ? Mathf.Clamp01((Time.time - rowValueTimes_[row]) / valuePopSeconds)
                : 1.0f;

            SetValueVisual(row, t);

            if (t >= 1.0f) { rowValueTimes_[row] = -1.0f; }
        }

        // 後乗せ対象を表示状態にする（PT・自家タグは対象の行だけ）。
        private void ShowValueObjects(int row)
        {
            if (deltaTexts != null && row < deltaTexts.Length && deltaTexts[row] != null)
            {
                deltaTexts[row].gameObject.SetActive(true);
            }

            if (rowHasPt_ != null && row < rowHasPt_.Length && rowHasPt_[row]
                && ptRoots != null && row < ptRoots.Length && ptRoots[row] != null)
            {
                ptRoots[row].SetActive(true);
            }

            if (rowIsSelf_ != null && row < rowIsSelf_.Length && rowIsSelf_[row]
                && selfTagRoots != null && row < selfTagRoots.Length && selfTagRoots[row] != null)
            {
                selfTagRoots[row].SetActive(true);
            }
        }

        // 後乗せ要素のアルファ＋スケール（valuePopScale から 1.0 へ収束）。
        private void SetValueVisual(int row, float t)
        {
            float ease = 1.0f - (1.0f - t) * (1.0f - t); // easeOutQuad
            float s = Mathf.Lerp(valuePopScale, 1.0f, ease);
            var scale = new Vector3(s, s, 1.0f);

            if (deltaTexts != null && row < deltaTexts.Length && deltaTexts[row] != null
                && deltaColors_ != null && row < deltaColors_.Length)
            {
                var c = deltaColors_[row];
                deltaTexts[row].color = new Color(c.r, c.g, c.b, c.a * ease);
                deltaTexts[row].transform.localScale = scale;
            }

            if (ptGraphics_ != null && row < ptGraphics_.Length)
            {
                SetGroupAlpha(ptGraphics_[row], ptColors_[row], ease);
                if (ptRoots != null && row < ptRoots.Length && ptRoots[row] != null)
                {
                    ptRoots[row].transform.localScale = scale;
                }
            }

            if (selfGraphics_ != null && row < selfGraphics_.Length)
            {
                SetGroupAlpha(selfGraphics_[row], selfColors_[row], ease);
                if (selfTagRoots != null && row < selfTagRoots.Length && selfTagRoots[row] != null)
                {
                    selfTagRoots[row].transform.localScale = scale;
                }
            }
        }

        private void UpdateScoreCount(int row)
        {
            if (rowScoreDone_ == null || row >= rowScoreDone_.Length || rowScoreDone_[row]) { return; }
            if (scoreTexts == null || row >= scoreTexts.Length || scoreTexts[row] == null)
            {
                rowScoreDone_[row] = true;
                return;
            }

            // 開示開始からの経過。rowRevealTimes_ は完了で負になるので、
            // 開示済み判定だけに使い、経過は全体経過から求める。
            float rowStart = showStartTime_ + row * rowRevealInterval;
            float t = scoreCountSeconds > 0.001f
                ? Mathf.Clamp01((Time.time - rowStart) / scoreCountSeconds)
                : 1.0f;
            float ease = 1.0f - (1.0f - t) * (1.0f - t); // easeOutQuad

            int target = finalScores_ != null && row < finalScores_.Length ? finalScores_[row] : 0;
            int value = target;

            if (t < 1.0f)
            {
                value = Mathf.RoundToInt(Mathf.Lerp(startScore_, target, ease));
                value = (value / 100) * 100; // 100点単位で回す（麻雀の点数刻み）
            }

            scoreTexts[row].text = value.ToString();

            if (t >= 1.0f)
            {
                rowScoreDone_[row] = true;
                rowScoreEndTime_[row] = Time.time; // 後乗せポップの起点
            }
        }

        // 行のアルファ（キャッシュ色×t）＋右からのスライドを一括設定。
        // スライドは easeOutBack で少し行き過ぎてから収まる（雀魂の入り方に寄せる）。
        private void SetRowVisual(int row, float t)
        {
            if (rowGraphics_ != null && row < rowGraphics_.Length)
            {
                SetGroupAlpha(rowGraphics_[row], rowGraphicColors_[row], t);
            }

            if (rowRects_ != null && row < rowRects_.Length && rowRects_[row] != null)
            {
                var basePos = rowBasePos_[row];
                rowRects_[row].anchoredPosition =
                    new Vector2(basePos.x + Mathf.Lerp(rowSlideDistance, 0.0f, EaseOutBack(t)), basePos.y);
            }
        }

        // easeOutBack。rowOvershoot=0 なら easeOutQuad 相当のなめらかな減速になる。
        private float EaseOutBack(float t)
        {
            float c1 = rowOvershoot < 0.0f ? 0.0f : rowOvershoot;
            if (c1 <= 0.0001f)
            {
                return 1.0f - (1.0f - t) * (1.0f - t);
            }

            float c3 = c1 + 1.0f;
            float u = t - 1.0f;
            return 1.0f + c3 * u * u * u + c1 * u * u;
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

        // 素点差・PTの符号色。0（増減なし）はキャッシュ済みの基準色へ戻す
        // （フェード途中の色を拾って基準色にしてしまわないため）。
        private Color SignColor(int row, Graphic graphic, int sign)
        {
            if (sign > 0) { return gainColor; }
            if (sign < 0) { return lossColor; }
            return CachedBaseColor(row, graphic);
        }

        private Color CachedBaseColor(int row, Graphic graphic)
        {
            if (rowGraphics_ != null && row >= 0 && row < rowGraphics_.Length && rowGraphics_[row] != null)
            {
                var graphics = rowGraphics_[row];
                for (int i = 0; i < graphics.Length; ++i)
                {
                    if (graphics[i] == graphic) { return rowGraphicColors_[row][i]; }
                }
            }
            return graphic.color;
        }

        // 色を「アルファ1.0の基準色」として更新する。キャッシュを書き換えないと
        // 次のフェードフレームで元の色に戻ってしまう（1位のハイライト・増減色が消える）。
        // 行フェード用と後乗せポップ用で別のキャッシュを持つため、両方を更新する。
        private void SetBaseColor(int row, Graphic graphic, Color color)
        {
            graphic.color = color;

            UpdateCachedColor(rowGraphics_, rowGraphicColors_, row, graphic, color);
            UpdateCachedColor(ptGraphics_, ptColors_, row, graphic, color);
            UpdateCachedColor(selfGraphics_, selfColors_, row, graphic, color);

            if (deltaColors_ != null && deltaTexts != null && row >= 0 && row < deltaColors_.Length
                && row < deltaTexts.Length && deltaTexts[row] == graphic)
            {
                deltaColors_[row] = color;
            }
        }

        private void UpdateCachedColor(Graphic[][] groups, Color[][] colors, int row, Graphic graphic, Color color)
        {
            if (groups == null || colors == null || row < 0 || row >= groups.Length || groups[row] == null)
            {
                return;
            }

            var graphics = groups[row];
            for (int i = 0; i < graphics.Length; ++i)
            {
                if (graphics[i] == graphic)
                {
                    colors[row][i] = color;
                    return;
                }
            }
        }

        // 色を書き換えた直後に、その行の現在のフェード進行度を塗り直す
        // （開示途中の行が1フレームだけ全開で見えるのを防ぐ）。
        private void ReapplyRowAlpha()
        {
            if (rowRevealTimes_ == null) { return; }

            for (int i = 0; i < numRows_ && i < rowRevealTimes_.Length; ++i)
            {
                if (rowRevealTimes_[i] == 0.0f) { continue; } // 未開示（行ごと非表示）

                float t = 1.0f;
                if (rowRevealTimes_[i] > 0.0f && rowFadeSeconds > 0.001f)
                {
                    t = Mathf.Clamp01((Time.time - rowRevealTimes_[i]) / rowFadeSeconds);
                }

                SetRowVisual(i, t);

                // 行フェードは後乗せ要素も一緒に塗ってしまうので、ポップ側の進行度で上書きし直す。
                if (rowValueTimes_ != null && i < rowValueTimes_.Length && rowValueTimes_[i] != 0.0f)
                {
                    float vt = 1.0f;
                    if (rowValueTimes_[i] > 0.0f && valuePopSeconds > 0.001f)
                    {
                        vt = Mathf.Clamp01((Time.time - rowValueTimes_[i]) / valuePopSeconds);
                    }

                    SetValueVisual(i, vt);
                }
            }
        }

        // 行のGraphicと基準色・基準位置を一度だけキャッシュする。
        // 色はここで拾った値を「アルファ1.0の基準」として扱うため、
        // フェード途中の値を拾わないよう表示前（Start）に呼ぶのが正。
        private void CacheRows()
        {
            if (cached_)
            {
                return;
            }

            cached_ = true;

            int numRows = rowRoots != null ? rowRoots.Length : 0;
            rowGraphics_ = new Graphic[numRows][];
            rowGraphicColors_ = new Color[numRows][];
            rowRects_ = new RectTransform[numRows];
            rowBasePos_ = new Vector2[numRows];
            rowRevealTimes_ = new float[numRows];
            rowScoreDone_ = new bool[numRows];
            rowScoreEndTime_ = new float[numRows];
            rowValueTimes_ = new float[numRows];
            rowHasPt_ = new bool[numRows];
            rowIsSelf_ = new bool[numRows];
            ptGraphics_ = new Graphic[numRows][];
            ptColors_ = new Color[numRows][];
            selfGraphics_ = new Graphic[numRows][];
            selfColors_ = new Color[numRows][];
            deltaColors_ = new Color[numRows];

            for (int i = 0; i < numRows; ++i)
            {
                if (rowRoots[i] == null) { continue; }

                var graphics = rowRoots[i].GetComponentsInChildren<Graphic>(true);
                rowGraphics_[i] = graphics;
                rowGraphicColors_[i] = CaptureColors(graphics);

                var rt = rowRoots[i].GetComponent<RectTransform>();
                rowRects_[i] = rt;
                rowBasePos_[i] = rt != null ? rt.anchoredPosition : Vector2.zero;

                // 後乗せ要素（PT欄・自家タグ・素点差）は行とは別タイミングで動かす。
                if (ptRoots != null && i < ptRoots.Length && ptRoots[i] != null)
                {
                    var ptGraphics = ptRoots[i].GetComponentsInChildren<Graphic>(true);
                    ptGraphics_[i] = ptGraphics;
                    ptColors_[i] = CaptureColors(ptGraphics);
                }

                if (selfTagRoots != null && i < selfTagRoots.Length && selfTagRoots[i] != null)
                {
                    var selfGraphics = selfTagRoots[i].GetComponentsInChildren<Graphic>(true);
                    selfGraphics_[i] = selfGraphics;
                    selfColors_[i] = CaptureColors(selfGraphics);
                }

                if (deltaTexts != null && i < deltaTexts.Length && deltaTexts[i] != null)
                {
                    deltaColors_[i] = deltaTexts[i].color;
                }
            }

            // 見出しのドーン用。ルート未指定なら Text 自身を動かす。
            headerRootResolved_ = headerRoot != null
                ? headerRoot
                : (headerText != null ? headerText.gameObject : null);

            if (headerRootResolved_ != null)
            {
                headerGraphics_ = headerRootResolved_.GetComponentsInChildren<Graphic>(true);
                headerColors_ = CaptureColors(headerGraphics_);
            }

            // 段位パネルのフェード用。
            if (rankPanelRoot != null)
            {
                rankGraphics_ = rankPanelRoot.GetComponentsInChildren<Graphic>(true);
                rankColors_ = CaptureColors(rankGraphics_);
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
    }
}
