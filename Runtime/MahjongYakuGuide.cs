using TMPro;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Persistence;
using VRC.SDKBase;
using VRC.Udon;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    // 役種一覧パネル（完全ローカル・同期変数ゼロ）
    //
    // ■ 何をするもの
    //   このワールドで実際に採用している役を、翻数のタブで引ける一覧にして見せる。
    //   段位戦の標準役に加えて、フリー対局の「ローカル役あり」でしか出ない役も
    //   ボタンひとつで なし / あり / のみ の3段に切り替えられる。表示される内容は Mahjong.cs の判定と
    //   1:1 で対応させてある（データの正は BuildYakuGuide.cs の表）。
    //
    // ■ なぜ完全ローカルなのか
    //   誰がどこを読んでいようと他人には関係が無い。同期変数を1つでも持つと
    //   「他人がスクロールした位置に飛ばされる」事故が起きるうえ、持ち運べる＝各自が
    //   別々の場所に持っている前提と噛み合わない。タブ・スクロール位置・トグルは
    //   すべてこのクライアントだけの状態。
    //
    // ■ スクロールは「枠を使い回す」方式
    //   役満タブはローカル役込みで36件ある。全部を GameObject で並べると
    //   1件あたり23オブジェクト×36件＝800個超になり、Canvas のリビルドだけで
    //   Quest が沈む。そこで Entry の枠は SLOT_COUNT 個だけ作り、
    //   スクロール位置に応じて「何番目の役を描くか」を差し替える
    //   （いわゆるリサイクルビュー）。Content の高さだけが件数ぶん伸びるので、
    //   スクロールバーの見た目と手触りは全件並んでいるのと変わらない。
    //
    // ■ スクロール検出は Update のポーリング
    //   ScrollRect.onValueChanged に配線する手もあるが、UnityEvent の永続リスナは
    //   ビルド時のフィルタで落ちても実機まで気づけない。ここは
    //   「Content の y が前フレームと違うか」を毎フレーム1回比べるだけで済むので、
    //   確実なほうを採る（float 比較1回ぶんのコスト）。
    //
    // ■ 牌の絵
    //   牌は 3D の実物ではなく、卓と同じアトラス（Assets/Mahjong/Textures/tiles.png）を
    //   RawImage.uvRect で切り出して描く。1枚のテクスチャで全部まかなえるので
    //   ドローコールが増えず、Quest でも安い。牌1枚ごとの矩形は
    //   ビルダーが牌メッシュの UV から実測して焼き込む（手打ちしない）。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class MahjongYakuGuide : UdonSharpBehaviour
    {
        // 使い回す Entry 枠の数。ビューポートに収まる行数＋1（スクロール中の継ぎ目ぶん）。
        public const int SLOT_COUNT = 5;
        // 1つの役に並べられる牌の最大枚数（四槓子の 4カン＋雀頭＝18枚）。
        public const int MAX_TILES = 18;
        // 大きさの段（ワールド設定から選ぶ）。0=小（既定）/ 1=中 / 2=大。
        public const int SIZE_SMALL = 0;
        public const int SIZE_COUNT = 3;
        // 本人のプロフィールに保存する。次回入場でも同じ大きさで出す。
        public const string SIZE_KEY = "kmj_yaku_size";

        // ローカル役の見せ方。ボタンを押すたびに なし → あり → のみ と回る。
        // 「のみ」を足したのは、フリー対局でローカル役だけを確かめたいときに
        // 全113件の中から拾い出すのが現実的でないため（標準51＋ローカル62）。
        public const int LOCAL_NONE = 0;   // 標準役だけ
        public const int LOCAL_WITH = 1;   // 標準役＋ローカル役
        public const int LOCAL_ONLY = 2;   // ローカル役だけ
        public const int LOCAL_MODE_COUNT = 3;

        // 左サイドのセクション。0 だけが実装済みで、1/2 は「準備中」を出す。
        public const int SECTION_YAKU = 0;
        public const int SECTION_TUTORIAL = 1;
        public const int SECTION_SCORE = 2;

        // チュートリアルは「1画面＝1ページ」で矢印送りにする（雀魂と同じ作法）。
        // 一覧のようにスクロールさせないのは、VRで板を持ったままドラッグすると
        // 行が滑って読めないため。読み物は1ページに収める側で作る。
        // ページを構成する箱（ブロック）の上限。枠はこの数だけ作ってページごとに置き直す。
        public const int TUT_BLOCKS = 8;
        // ブロックの見出しチップの高さ。
        public const float TUT_HEAD_H = 24f;

        // ============================================================
        // データ（すべてビルダーが流し込む。手で触らない）
        // ============================================================
        [Header("役データ（ビルダーが流し込む）")]
        [Tooltip("役ごとのタブ番号。0=一翻 1=二翻 2=三翻 3=六翻 4=満貫 5=倍満 6=役満 7=ダブル役満 8=流局")]
        public int[] yakuTab;
        [Tooltip("ローカル役か（true ならトグルONのときだけ出す）")]
        public bool[] yakuLocal;
        public string[] yakuNameJa;
        public string[] yakuNameEn;
        [Tooltip("右肩の但し書き（門前清の場合のみ／食い下がり一翻 など）。空なら出さない")]
        public string[] yakuBadgeJa;
        public string[] yakuBadgeEn;
        [TextArea] public string[] yakuDescJa;
        [TextArea] public string[] yakuDescEn;
        [Tooltip("韓国語（名前・但し書き・説明）。空の欄は英語を出す")]
        public string[] yakuNameKo;
        public string[] yakuBadgeKo;
        [TextArea] public string[] yakuDescKo;
        [Tooltip("簡体字・繁体字（名前・但し書き・説明）。空の欄は英語を出す")]
        public string[] yakuNameZhs;
        public string[] yakuBadgeZhs;
        [TextArea] public string[] yakuDescZhs;
        public string[] yakuNameZht;
        public string[] yakuBadgeZht;
        [TextArea] public string[] yakuDescZht;
        [Tooltip("牌並びを平らに並べた配列。-1 は面子の区切り（すき間）")]
        public int[] tilePlan;
        public int[] tilePlanStart;
        public int[] tilePlanCount;

        [Header("チュートリアル（ビルダーが流し込む）")]
        public string[] tutPageTitleJa;
        public string[] tutPageTitleEn;
        public string[] tutPageTitleKo;
        public string[] tutPageTitleZhs;
        public string[] tutPageTitleZht;
        [Tooltip("ページ → ブロックの範囲")]
        public int[] tutPageStart;
        public int[] tutPageCount;
        [Tooltip("ブロックの矩形。原点はチュートリアル領域の左上、+Y は下向き")]
        public float[] tutBlockX;
        public float[] tutBlockY;
        public float[] tutBlockW;
        public float[] tutBlockH;
        public string[] tutBlockHeadJa;
        public string[] tutBlockHeadEn;
        [TextArea] public string[] tutBlockBodyJa;
        [TextArea] public string[] tutBlockBodyEn;
        public string[] tutBlockHeadKo;
        [TextArea] public string[] tutBlockBodyKo;
        public string[] tutBlockHeadZhs;
        [TextArea] public string[] tutBlockBodyZhs;
        public string[] tutBlockHeadZht;
        [TextArea] public string[] tutBlockBodyZht;
        [Tooltip("ブロックごとの牌の高さ。0 なら tileHeight（一覧と同じ34px）を使う")]
        public float[] tutBlockTileH;
        [Tooltip("ブロックごとの牌並び。-1 は区切り（すき間）")]
        public int[] tutTilePlan;
        public int[] tutTileStart;
        public int[] tutTileCount;
        [Tooltip("true のブロックだけ、牌の下に読み方を出す")]
        public bool[] tutBlockRuby;

        [Header("牌アトラス（ビルダーが実測して焼き込む）")]
        public Texture tileTexture;
        // 伏せ牌（id == -2）の色。正は MainMenuController.GetTileBackColor(0)＝既定の牌背色で、
        // 実値は Build Yaku Guide がそこから焼く。ここの初期値は焼き忘れたときの保険。
        public Color tileBackColor = new Color(217f / 255f, 163f / 255f, 35f / 255f, 1f);
        public float[] tileU;
        public float[] tileV;
        public float[] tileW;
        public float[] tileH;

        // ============================================================
        // UI 参照
        // ============================================================
        [Header("タブ")]
        public Image[] tabImages;
        public TextMeshProUGUI[] tabLabels;
        public string[] tabNameJa;
        public string[] tabNameEn;
        public string[] tabNameKo;
        public string[] tabNameZhs;
        public string[] tabNameZht;

        [Header("左サイド（セクション）")]
        public Image[] sectionImages;
        public TextMeshProUGUI[] sectionLabels;
        public string[] sectionNameJa;
        public string[] sectionNameEn;
        public string[] sectionNameKo;
        public string[] sectionNameZhs;
        public string[] sectionNameZht;

        [Header("一覧（スクロール）")]
        public GameObject listRoot;
        public ScrollRect scrollRect;
        public RectTransform contentRect;
        public GameObject[] entryRoots;
        public RectTransform[] entryRects;
        public TextMeshProUGUI[] entryNames;
        public TextMeshProUGUI[] entryBadges;
        public TextMeshProUGUI[] entryDescs;
        [Tooltip("entry ごとに MAX_TILES 枚ずつ、slot * MAX_TILES + k で引く")]
        public RawImage[] entryTiles;
        [Tooltip("牌が1枚も無い役の行を隠すための土台")]
        public GameObject[] entryTileRows;
        public TextMeshProUGUI emptyLabel;

        [Header("見出し・件数")]
        [Tooltip("左上のワールド名。言語で差し替える")]
        public TextMeshProUGUI brandLabel;
        public TextMeshProUGUI titleLabel;
        public TextMeshProUGUI countLabel;
        public TextMeshProUGUI localLabel;
        public Image localImage;
        public TextMeshProUGUI hintLabel;

        [Header("準備中の画面（未実装セクションの受け皿）")]
        public GameObject noticeRoot;
        public TextMeshProUGUI noticeLabel;

        [Header("チュートリアル UI")]
        public GameObject tutorialRoot;
        public GameObject[] tutBlockRoots;
        public RectTransform[] tutBlockRects;
        public GameObject[] tutHeadRoots;
        public TextMeshProUGUI[] tutHeadLabels;
        public RectTransform[] tutBodyRects;
        public TextMeshProUGUI[] tutBodyLabels;
        [Tooltip("ブロックごとに MAX_TILES 枚ずつ、block * MAX_TILES + k で引く")]
        public RawImage[] tutTiles;
        public RectTransform[] tutTileRows;
        [Tooltip("牌の下の読み方。tutTiles と同じ並び（block * MAX_TILES + k）")]
        public TextMeshProUGUI[] tutTileRuby;
        [Header("読み方の色（Wire が入れ直す）")]
        public Color rubyColorMan = Color.black;
        public Color rubyColorPin = Color.black;
        public Color rubyColorSou = Color.black;
        public Color rubyColorHonor = Color.black;
        public GameObject tutPrevButton;
        public GameObject tutNextButton;
        public TextMeshProUGUI tutPageLabel;
        [Tooltip("役種一覧のときだけ出すもの（翻数タブ・▲▼・件数・ローカル役トグル）")]
        public GameObject[] listOnlyObjects;

        [Header("点数計算 UI")]
        // 点数早見表（子／親）と符計算早見表。数字はビルダーが Mahjong.cs と同じ式で
        // 計算して焼いてある（実行時に組み立てない＝Questで表を開くたびに計算しない）。
        // ここが持つのは「どのページを出すか」と「日英どちらの文字を入れるか」だけ。
        public GameObject scoreRoot;
        [Tooltip("0=点数早見表（子） 1=点数早見表（親） 2=符計算早見表")]
        public GameObject[] scorePages;
        public Image[] scoreTabImages;
        public TextMeshProUGUI[] scoreTabLabels;
        public string[] scoreTabNameJa;
        public string[] scoreTabNameEn;
        public string[] scoreTabNameKo;
        public string[] scoreTabNameZhs;
        public string[] scoreTabNameZht;
        [Tooltip("日英で文字が変わるラベルだけを並べてある（数字だけのマスは含まない）")]
        public TextMeshProUGUI[] scoreTexts;
        public string[] scoreTextJa;
        public string[] scoreTextEn;
        public string[] scoreTextKo;
        public string[] scoreTextZhs;
        public string[] scoreTextZht;

        [Header("並べ方（Canvasピクセル）")]
        [Tooltip("Entry 1行ぶんの送り幅")]
        public float entryStep = 86f;
        [Tooltip("スクロールの見えている高さ（Viewport の高さ）")]
        public float viewportHeight = 346f;
        [Tooltip("牌1枚の高さ。幅は牌の縦横比から決める")]
        public float tileHeight = 34f;
        [Tooltip("面子の区切りに入れるすき間")]
        public float tileGap = 7f;
        [Tooltip("牌を並べられる横幅。これを超える並びは自動で縮める")]
        public float tileAreaWidth = 590f;

        [Header("色（MainMenu と同じ象牙／墨テーマ）")]
        public Color tabOnColor = new Color(1f, 0.843f, 0f, 1f);
        public Color tabOffColor = new Color(1f, 0.996f, 0.976f, 1f);
        public Color tabDisabledColor = new Color(0.85f, 0.84f, 0.80f, 1f);
        public Color toggleOnColor = new Color(0f, 0.761f, 0.796f, 1f);
        public Color toggleOffColor = new Color(1f, 0.996f, 0.976f, 1f);
        // 「ローカル役 のみ」の段の色。あり（Teal）と混ざらない色を当てる。
        public Color toggleOnlyColor = new Color(0.631f, 0.545f, 0.961f, 1f);

        [Header("大きさ（MainMenu > ワールド設定から切り替える）")]
        [Tooltip("小・中・大の倍率。小(1.0)が既定＝設計どおりの大きさ")]
        public float[] sizeScales = { 1f, 1.35f, 1.8f };
        [Tooltip("ワールド設定にある 小/中/大 ボタンの Image（選択中を塗り分ける）")]
        public Image[] sizeButtonImages;
        public Color sizeOnColor = new Color(1f, 0.843f, 0f, 1f);
        public Color sizeOffColor = new Color(1f, 0.996f, 0.976f, 1f);

        [Header("持ち運び")]
        [Tooltip("「元に戻す」で帰る位置。ビルダーがホーム位置に空オブジェクトを置く")]
        public Transform homeAnchor;
        [Tooltip("パネル本体（Pickup が付いている側）。未割当なら transform を使う")]
        public Transform pickupRoot;
        public VRC.SDK3.Components.VRCPickup pickup;

        [Header("言語")]
        public KemonoLanguage languageManager;

        // ------------------------------------------------------------
        // ローカル状態
        // ------------------------------------------------------------
        private int section_ = SECTION_YAKU;
        private int tab_ = 0;
        private int page_ = 0;       // チュートリアルの現在ページ
        private int scoreTab_ = 0;   // 点数計算の小タブ（0=子 / 1=親 / 2=符）
        // 焼いてある文字は日本語。英語へ入れ替えたあとは言語が変わるまで触らない
        // （100枚超の TMP に毎回 text を書くと、タブを押すたびに Canvas を丸ごと組み直す）。
        private int scoreTextLang_ = KemonoLanguage.LANG_JA;
        private int localMode_ = LOCAL_NONE;
        private int lang_ = -1;      // -1 = 未解決
        // タブ内の該当役（Refresh のたびに詰め直す）。
        private int[] filtered_;
        private int numFiltered_ = 0;
        // タブごとの件数。0件のタブはボタンを落として空振りを防ぐ。
        private int[] tabCount_;
        // 使い回している枠が今どこを描いているか。
        private int firstSlotIndex_ = -1;
        private float lastScrollY_ = -99999f;
        private bool started_ = false;
        // 大きさ。PlayerData の復元は OnPlayerRestored 後なので、
        // それより前に押された操作は保留して復元後に流す
        // （VolumeSettingsPanel / PerformanceModeController と同じ作法）。
        private int size_ = SIZE_SMALL;
        private bool restored_ = false;
        private bool pendingSave_ = false;

        void Start()
        {
            filtered_ = new int[yakuTab != null ? yakuTab.Length : 0];
            tabCount_ = new int[tabImages != null ? tabImages.Length : 0];
            started_ = true;
            ApplySize();
            Refresh();
        }

        void OnEnable()
        {
            // 畳まれている間に言語が変わっていた場合の取りこぼし回収。
            if (!started_) { return; }
            Refresh();
        }

        void Update()
        {
            // スクロールの追従。動いていないフレームは float 比較1回で抜ける。
            if (!started_ || section_ != SECTION_YAKU || contentRect == null) { return; }

            float y = contentRect.anchoredPosition.y;
            if (y == lastScrollY_) { return; }
            lastScrollY_ = y;
            LayoutSlots(false);
        }

        // ------------------------------------------------------------
        // 言語（KemonoLanguage.extraTargets から呼ばれる）
        // ------------------------------------------------------------
        public void ApplyLanguageJa() { lang_ = KemonoLanguage.LANG_JA; if (started_) { Refresh(); } }
        public void ApplyLanguageEn() { lang_ = KemonoLanguage.LANG_EN; if (started_) { Refresh(); } }
        public void ApplyLanguageKo() { lang_ = KemonoLanguage.LANG_KO; if (started_) { Refresh(); } }
        public void ApplyLanguageZhs() { lang_ = KemonoLanguage.LANG_ZHS; if (started_) { Refresh(); } }
        public void ApplyLanguageZht() { lang_ = KemonoLanguage.LANG_ZHT; if (started_) { Refresh(); } }

        private int Lang()
        {
            if (lang_ < 0)
            {
                // 初回だけ取りに行く。言語マネージャが無いワールドでは日本語。
                lang_ = languageManager != null ? languageManager.GetLanguage() : KemonoLanguage.LANG_JA;
            }
            return lang_;
        }

        // 英語の配置か（韓国語も英語と同じ配置・同じ字詰めで出す）。文言は Pick で選ぶ。
        private bool IsEnglish() { return Lang() != KemonoLanguage.LANG_JA; }

        // 日英韓簡繁の表から1つ選ぶ。韓国語・中国語が空なら英語、英語も無ければ日本語。
        private string Pick(string[] ja, string[] en, string[] ko, string[] zhs, string[] zht, int i)
        {
            int lang = Lang();
            string[] own = lang == KemonoLanguage.LANG_KO ? ko
                : lang == KemonoLanguage.LANG_ZHS ? zhs
                : lang == KemonoLanguage.LANG_ZHT ? zht : null;
            if (own != null && i < own.Length && own[i] != null && own[i].Length > 0)
            {
                return own[i];
            }
            if (lang != KemonoLanguage.LANG_JA && en != null && i < en.Length && en[i] != null)
            {
                return en[i];
            }
            return ja != null && i < ja.Length && ja[i] != null ? ja[i] : "";
        }

        // コードに直書きした文言の5択（日・英・韓・簡・繁）。
        private string L(string ja, string en, string ko, string zhs, string zht)
        {
            int lang = Lang();
            if (lang == KemonoLanguage.LANG_JA) { return ja; }
            if (lang == KemonoLanguage.LANG_KO) { return ko; }
            if (lang == KemonoLanguage.LANG_ZHS) { return zhs; }
            if (lang == KemonoLanguage.LANG_ZHT) { return zht; }
            return en;
        }

        // ------------------------------------------------------------
        // ボタン（Button.onClick → SendCustomEvent）
        // ------------------------------------------------------------
        public void OnSectionYaku() { SetSection(SECTION_YAKU); }
        public void OnSectionTutorial() { SetSection(SECTION_TUTORIAL); }
        public void OnSectionScore() { SetSection(SECTION_SCORE); }

        /// <summary>点数計算の小タブ。0=点数早見表（子） 1=（親） 2=符計算早見表。</summary>
        public void OnScoreTab0() { SetScoreTab(0); }
        public void OnScoreTab1() { SetScoreTab(1); }
        public void OnScoreTab2() { SetScoreTab(2); }

        private void SetScoreTab(int tab)
        {
            if (tab == scoreTab_) { return; }
            scoreTab_ = tab;
            Refresh();
        }

        /// <summary>チュートリアルの前ページ（右下の▲）。</summary>
        public void OnPagePrev() { SetPage(page_ - 1); }
        /// <summary>チュートリアルの次ページ（右下の▼）。</summary>
        public void OnPageNext() { SetPage(page_ + 1); }

        private void SetPage(int page)
        {
            int pages = tutPageTitleJa != null ? tutPageTitleJa.Length : 0;
            if (pages <= 0) { return; }
            if (page < 0) { page = 0; }
            if (page >= pages) { page = pages - 1; }
            if (page == page_) { return; }
            page_ = page;
            Refresh();
        }

        public void OnTab0() { SetTab(0); }
        public void OnTab1() { SetTab(1); }
        public void OnTab2() { SetTab(2); }
        public void OnTab3() { SetTab(3); }
        public void OnTab4() { SetTab(4); }
        public void OnTab5() { SetTab(5); }
        public void OnTab6() { SetTab(6); }
        public void OnTab7() { SetTab(7); }
        public void OnTab8() { SetTab(8); }

        public void OnToggleLocal()
        {
            localMode_ = (localMode_ + 1) % LOCAL_MODE_COUNT;

            // 段を回すと、いま開いているタブが空になることがある
            // （「のみ」には該当するローカル役が1件も無い翻数がある）。
            // 空の板だけ見せても次にどこを押せばいいか分からないので、
            // 件数のあるいちばん手前のタブへ寄せてから描き直す。
            CountTabs();
            if (tabCount_ != null && tab_ >= 0 && tab_ < tabCount_.Length && tabCount_[tab_] == 0)
            {
                for (int i = 0; i < tabCount_.Length; ++i)
                {
                    if (tabCount_[i] > 0) { tab_ = i; break; }
                }
            }

            Refresh();
        }

        /// <summary>いまの段でこの役を出すか。</summary>
        private bool IsYakuShown(int i)
        {
            if (localMode_ == LOCAL_WITH) { return true; }
            bool isLocal = yakuLocal != null && i < yakuLocal.Length && yakuLocal[i];
            return localMode_ == LOCAL_ONLY ? isLocal : !isLocal;
        }

        /// <summary>▲ボタン。ドラッグしづらい環境むけに1行ぶん送る。</summary>
        public void OnScrollUp() { ScrollBy(-entryStep); }
        /// <summary>▼ボタン。</summary>
        public void OnScrollDown() { ScrollBy(entryStep); }

        // ScrollRect は自分が持っている位置を毎フレーム書き戻すので、
        // Content の anchoredPosition を直接いじると「投げられた」と解釈されて
        // 慣性で流れ続ける（実測：1行送りのつもりが末尾まで飛ぶ）。
        // 外から動かすときは必ず verticalNormalizedPosition を経由すること。
        private void ScrollBy(float delta)
        {
            if (contentRect == null) { return; }

            float max = contentRect.sizeDelta.y - viewportHeight;
            if (max <= 0f)
            {
                if (scrollRect != null) { scrollRect.StopMovement(); scrollRect.verticalNormalizedPosition = 1f; }
                return;
            }

            float y = contentRect.anchoredPosition.y + delta;
            if (y < 0f) { y = 0f; }
            if (y > max) { y = max; }

            if (scrollRect != null)
            {
                scrollRect.StopMovement();
                scrollRect.verticalNormalizedPosition = 1f - (y / max);
            }
            else
            {
                contentRect.anchoredPosition = new Vector2(contentRect.anchoredPosition.x, y);
            }

            lastScrollY_ = contentRect.anchoredPosition.y;
            LayoutSlots(false);
        }

        /// <summary>
        /// 持ち出したパネルをホーム位置へ戻す。
        /// 画面上のボタンは外したが（要望）、拾えない場所へ落としたときの逃がし道として
        /// メソッドは残してある。任意のボタンから SendCustomEvent("OnReturnHome") で呼べる。
        /// </summary>
        public void OnReturnHome()
        {
            if (pickup != null) { pickup.Drop(); }

            var target = pickupRoot != null ? pickupRoot : transform;
            if (homeAnchor != null)
            {
                target.SetPositionAndRotation(homeAnchor.position, homeAnchor.rotation);
            }
        }

        // ------------------------------------------------------------
        // 大きさ（ワールド設定のボタンから SendCustomEvent で呼ばれる）
        // ------------------------------------------------------------
        public void OnSizeSmall() { SetSize(0); }
        public void OnSizeMedium() { SetSize(1); }
        public void OnSizeLarge() { SetSize(2); }

        private void SetSize(int size)
        {
            if (size < 0) { size = 0; }
            if (size >= SIZE_COUNT) { size = SIZE_COUNT - 1; }
            size_ = size;
            ApplySize();
            Save();
        }

        // 倍率はルートに掛ける。Canvas も筐体も掴み判定もまとめて追従するので、
        // 大きさを変えても「画面の裏に掴み判定」「縁がはみ出す」の関係は崩れない。
        private void ApplySize()
        {
            var target = pickupRoot != null ? pickupRoot : transform;
            float k = (sizeScales != null && size_ < sizeScales.Length) ? sizeScales[size_] : 1f;
            if (k <= 0f) { k = 1f; }
            target.localScale = new Vector3(k, k, k);

            if (sizeButtonImages != null)
            {
                for (int i = 0; i < sizeButtonImages.Length; ++i)
                {
                    if (sizeButtonImages[i] == null) { continue; }
                    sizeButtonImages[i].color = (i == size_) ? sizeOnColor : sizeOffColor;
                }
            }
        }

        public override void OnPlayerRestored(VRCPlayerApi player)
        {
            if (player == null || !player.isLocal) { return; }

            restored_ = true;

            if (pendingSave_)
            {
                // 復元前に押していた＝そちらが本人の最新の意思。保存し直す。
                pendingSave_ = false;
                Save();
                return;
            }

            int v;
            if (!PlayerData.TryGetInt(player, SIZE_KEY, out v)) { return; }
            if (v < 0) { v = 0; }
            if (v >= SIZE_COUNT) { v = SIZE_COUNT - 1; }
            size_ = v;
            ApplySize();
        }

        private void Save()
        {
            if (!restored_) { pendingSave_ = true; return; }
            PlayerData.SetInt(SIZE_KEY, size_);
        }

        private void SetSection(int section)
        {
            section_ = section;
            Refresh();
        }

        private void SetTab(int tab)
        {
            if (tab == tab_) { return; }
            tab_ = tab;
            Refresh();
        }

        // ------------------------------------------------------------
        // 描き直し
        // ------------------------------------------------------------
        public void Refresh()
        {
            bool en = IsEnglish();

            // --- 左サイド ---
            if (sectionImages != null && sectionLabels != null)
            {
                for (int i = 0; i < sectionImages.Length; ++i)
                {
                    if (sectionImages[i] != null)
                    {
                        sectionImages[i].color = i == section_ ? tabOnColor : tabOffColor;
                    }
                    if (sectionLabels[i] != null && sectionNameJa != null && i < sectionNameJa.Length)
                    {
                        sectionLabels[i].text = Pick(sectionNameJa, sectionNameEn, sectionNameKo, sectionNameZhs, sectionNameZht, i);
                    }
                }
            }

            if (titleLabel != null && sectionNameJa != null && section_ < sectionNameJa.Length)
            {
                titleLabel.text = Pick(sectionNameJa, sectionNameEn, sectionNameKo, sectionNameZhs, sectionNameZht, section_);
            }

            if (brandLabel != null)
            {
                brandLabel.text = L("麻雀集会所", "Mahjong\nGathering Hall", "마작\n집회소",
                    "麻将\n集会所", "麻將\n集會所");
            }

            if (hintLabel != null)
            {
                hintLabel.text = L("つかむと持ち運べます", "Grab the panel to carry it.",
                    "잡으면 들고 다닐 수 있습니다", "抓住面板即可携带", "抓住面板即可攜帶");
            }

            // --- セクションの出し分け ---
            //   役種一覧       … 翻数タブ＋スクロールの一覧
            //   チュートリアル … 1画面1ページの読み物（右下の矢印で送る）
            //   点数計算       … 点数早見表（子／親）＋符計算早見表
            bool showList = section_ == SECTION_YAKU;
            bool showTutorial = section_ == SECTION_TUTORIAL
                && tutPageTitleJa != null && tutPageTitleJa.Length > 0;
            bool showScore = section_ == SECTION_SCORE
                && scoreRoot != null && scorePages != null && scorePages.Length > 0;
            bool showNotice = !showList && !showTutorial && !showScore;

            if (listRoot != null) { listRoot.SetActive(showList); }
            if (tutorialRoot != null) { tutorialRoot.SetActive(showTutorial); }
            if (scoreRoot != null) { scoreRoot.SetActive(showScore); }
            if (noticeRoot != null) { noticeRoot.SetActive(showNotice); }

            // 翻数タブ・▲▼・件数・ローカル役トグルは一覧のときだけ出す。
            // 消し忘れると読み物の画面に翻数タブが残る（一覧の操作に見えて押せてしまう）。
            if (listOnlyObjects != null)
            {
                for (int i = 0; i < listOnlyObjects.Length; ++i)
                {
                    if (listOnlyObjects[i] != null) { listOnlyObjects[i].SetActive(showList); }
                }
            }

            if (!showList && emptyLabel != null) { emptyLabel.gameObject.SetActive(false); }

            if (showTutorial)
            {
                DrawTutorial(en);
                return;
            }

            if (showScore)
            {
                DrawScore(en);
                return;
            }

            if (showNotice)
            {
                // 見出しはこの上でセクション名にしてある（チュートリアルだけページ題で上書きする）。
                if (noticeLabel != null)
                {
                    noticeLabel.text = en
                        ? "Coming soon.\nThis section is still being built."
                        : "準備中です。\nこの画面は後日実装します。";
                }
                return;
            }

            // --- タブの件数を数える ---
            CountTabs();

            if (tabImages != null)
            {
                for (int i = 0; i < tabImages.Length; ++i)
                {
                    bool empty = tabCount_ != null && i < tabCount_.Length && tabCount_[i] == 0;
                    if (tabImages[i] != null)
                    {
                        tabImages[i].color = i == tab_ ? tabOnColor : (empty ? tabDisabledColor : tabOffColor);
                    }
                    if (tabLabels != null && i < tabLabels.Length && tabLabels[i] != null
                        && tabNameJa != null && i < tabNameJa.Length)
                    {
                        tabLabels[i].text = Pick(tabNameJa, tabNameEn, tabNameKo, tabNameZhs, tabNameZht, i);
                    }
                }
            }

            // --- ローカル役トグル ---
            if (localLabel != null)
            {
                if (localMode_ == LOCAL_ONLY)
                {
                    localLabel.text = L("ローカル役 のみ", "Local yaku : ONLY", "로컬 역 : 전용", "地方役：仅", "地方役：僅");
                }
                else if (localMode_ == LOCAL_WITH)
                {
                    localLabel.text = L("ローカル役 あり", "Local yaku : ON", "로컬 역 : ON", "地方役：有", "地方役：有");
                }
                else
                {
                    localLabel.text = L("ローカル役 なし", "Local yaku : OFF", "로컬 역 : OFF", "地方役：无", "地方役：無");
                }
            }
            if (localImage != null)
            {
                localImage.color = localMode_ == LOCAL_ONLY ? toggleOnlyColor
                    : (localMode_ == LOCAL_WITH ? toggleOnColor : toggleOffColor);
            }

            // --- 該当役を詰める ---
            Filter();

            if (countLabel != null)
            {
                countLabel.text = L("全 " + numFiltered_ + " 件", numFiltered_ + " yaku", "전체 " + numFiltered_ + "개",
                    "共 " + numFiltered_ + " 种", "共 " + numFiltered_ + " 種");
            }

            if (emptyLabel != null)
            {
                bool empty = numFiltered_ == 0;
                emptyLabel.gameObject.SetActive(empty);
                if (empty)
                {
                    if (localMode_ == LOCAL_ONLY)
                    {
                        emptyLabel.text = L("この翻数のローカル役はありません。\n他の翻数のタブを見てください。",
                            "No local yaku here.\nOther tabs have them.",
                            "이 판수의 로컬 역은 없습니다.\n다른 판수의 탭을 확인해 주세요.",
                            "此番数没有地方役。\n请查看其他番数的标签。",
                            "此番數沒有地方役。\n請查看其他番數的標籤。");
                    }
                    else
                    {
                        emptyLabel.text = L("この翻数の役はありません。\n「ローカル役 あり」にすると増えます。",
                            "No yaku here.\nTurn Local yaku ON to see the optional ones.",
                            "이 판수의 역은 없습니다.\n'로컬 역 있음'으로 하면 늘어납니다.",
                            "此番数没有役。\n开启「地方役：有」即可显示更多。",
                            "此番數沒有役。\n開啟「地方役：有」即可顯示更多。");
                    }
                }
            }

            // --- スクロール範囲を件数に合わせて、いちばん上へ戻す ---
            if (contentRect != null)
            {
                float height = numFiltered_ * entryStep;
                if (height < viewportHeight) { height = viewportHeight; }
                contentRect.sizeDelta = new Vector2(contentRect.sizeDelta.x, height);
            }
            if (scrollRect != null)
            {
                scrollRect.StopMovement();
                scrollRect.verticalNormalizedPosition = 1f;   // 1=最上段
            }
            else if (contentRect != null)
            {
                contentRect.anchoredPosition = new Vector2(contentRect.anchoredPosition.x, 0f);
            }
            lastScrollY_ = contentRect != null ? contentRect.anchoredPosition.y : 0f;

            LayoutSlots(true);
        }

        private void CountTabs()
        {
            if (tabCount_ == null || yakuTab == null) { return; }

            for (int i = 0; i < tabCount_.Length; ++i) { tabCount_[i] = 0; }

            for (int i = 0; i < yakuTab.Length; ++i)
            {
                if (!IsYakuShown(i)) { continue; }
                int t = yakuTab[i];
                if (t < 0 || t >= tabCount_.Length) { continue; }
                ++tabCount_[t];
            }
        }

        private void Filter()
        {
            numFiltered_ = 0;
            if (yakuTab == null || filtered_ == null) { return; }

            for (int i = 0; i < yakuTab.Length; ++i)
            {
                if (yakuTab[i] != tab_) { continue; }
                if (!IsYakuShown(i)) { continue; }
                if (numFiltered_ >= filtered_.Length) { break; }
                filtered_[numFiltered_] = i;
                ++numFiltered_;
            }
        }

        // スクロール位置から「いま先頭に来るべき役」を割り出し、枠をそこへ移して描き直す。
        // 先頭が変わっていなければ何もしない（ドラッグ中に毎フレーム描き直さないため）。
        private void LayoutSlots(bool force)
        {
            if (entryRoots == null || contentRect == null) { return; }

            float y = contentRect.anchoredPosition.y;
            int first = Mathf.FloorToInt(y / entryStep);

            int maxFirst = numFiltered_ - SLOT_COUNT;
            if (maxFirst < 0) { maxFirst = 0; }
            if (first > maxFirst) { first = maxFirst; }
            if (first < 0) { first = 0; }

            if (!force && first == firstSlotIndex_) { return; }
            firstSlotIndex_ = first;

            bool en = IsEnglish();

            for (int i = 0; i < SLOT_COUNT; ++i)
            {
                if (i >= entryRoots.Length) { break; }

                int at = first + i;
                bool has = at < numFiltered_;

                if (entryRoots[i] != null) { entryRoots[i].SetActive(has); }
                if (!has) { continue; }

                if (entryRects != null && i < entryRects.Length && entryRects[i] != null)
                {
                    entryRects[i].anchoredPosition = new Vector2(0f, -at * entryStep);
                }

                DrawEntry(i, filtered_[at], en);
            }
        }

        private void DrawEntry(int slot, int yaku, bool en)
        {
            if (entryNames != null && slot < entryNames.Length && entryNames[slot] != null)
            {
                entryNames[slot].text = Pick(yakuNameJa, yakuNameEn, yakuNameKo, yakuNameZhs, yakuNameZht, yaku);
            }

            if (entryBadges != null && slot < entryBadges.Length && entryBadges[slot] != null)
            {
                string badge = Pick(yakuBadgeJa, yakuBadgeEn, yakuBadgeKo, yakuBadgeZhs, yakuBadgeZht, yaku);
                entryBadges[slot].text = badge == null ? "" : badge;
            }

            if (entryDescs != null && slot < entryDescs.Length && entryDescs[slot] != null)
            {
                entryDescs[slot].text = Pick(yakuDescJa, yakuDescEn, yakuDescKo, yakuDescZhs, yakuDescZht, yaku);
            }

            DrawTiles(slot, yaku);
        }

        // 牌を左詰めで並べる。並びが枠に入りきらないときだけ、行ごと縮めて収める
        // （牌を間引くと役の形が読めなくなるので、枚数は絶対に減らさない）。
        private void DrawTiles(int slot, int yaku)
        {
            if (entryTiles == null) { return; }

            int start = tilePlanStart != null && yaku < tilePlanStart.Length ? tilePlanStart[yaku] : 0;
            // count は「牌＋区切り」の総数。牌の枚数の上限（MAX_TILES）は下のループで
            // スロットが尽きたところで頭打ちにする（ここで切ると区切りのぶん牌が消える）。
            int count = tilePlanCount != null && yaku < tilePlanCount.Length ? tilePlanCount[yaku] : 0;

            if (entryTileRows != null && slot < entryTileRows.Length && entryTileRows[slot] != null)
            {
                entryTileRows[slot].SetActive(count > 0);
            }

            int numTiles = 0;
            int numGaps = 0;
            for (int k = 0; k < count; ++k)
            {
                if (tilePlan[start + k] == -1) { ++numGaps; } else { ++numTiles; }
            }

            // 牌1枚の縦横比はアトラスの実測値そのまま（幅／高さ）。
            float aspect = 0.6689f;
            if (tileW != null && tileH != null && tileW.Length > 0 && tileH.Length > 0 && tileH[0] > 0f)
            {
                aspect = tileW[0] / tileH[0];
            }

            float w = tileHeight * aspect;
            float need = numTiles * w + numGaps * tileGap;
            float scale = (need > tileAreaWidth && need > 0f) ? tileAreaWidth / need : 1f;

            float x = 0f;
            int drawn = 0;

            for (int k = 0; k < count; ++k)
            {
                int id = tilePlan[start + k];
                if (id == -1) { x += tileGap * scale; continue; }

                int at = slot * MAX_TILES + drawn;
                if (at >= entryTiles.Length) { break; }

                var img = entryTiles[at];
                ++drawn;
                if (img == null) { continue; }

                img.gameObject.SetActive(true);
                img.texture = id == -2 ? Texture2D.whiteTexture : tileTexture;
                img.color = id == -2 ? tileBackColor : Color.white;
                if (id == -2) { img.uvRect = new Rect(0f, 0f, 1f, 1f); }
                else if (tileU != null && id >= 0 && id < tileU.Length)
                {
                    img.uvRect = new Rect(tileU[id], tileV[id], tileW[id], tileH[id]);
                }

                var rt = img.rectTransform;
                rt.sizeDelta = new Vector2(w * scale, tileHeight * scale);
                rt.anchoredPosition = new Vector2(x, 0f);
                x += w * scale;
            }

            // 余った牌スロットは畳む。
            for (int k = drawn; k < MAX_TILES; ++k)
            {
                int at = slot * MAX_TILES + k;
                if (at >= entryTiles.Length) { break; }
                if (entryTiles[at] != null) { entryTiles[at].gameObject.SetActive(false); }
            }
        }

        // ------------------------------------------------------------
        // 点数計算（点数早見表・符計算早見表）
        //
        // 表そのものはビルダーが焼いた静的なラベルの集まり。実行時にやるのは
        //   ・小タブに応じてページを出し分ける
        //   ・日英の文字を入れ替える（言語が変わったときの1回だけ）
        // の2つだけで、数字には一切触らない。
        // ------------------------------------------------------------
        private void DrawScore(bool en)
        {
            if (scorePages == null) { return; }

            if (scoreTab_ < 0) { scoreTab_ = 0; }
            if (scoreTab_ >= scorePages.Length) { scoreTab_ = scorePages.Length - 1; }

            for (int i = 0; i < scorePages.Length; ++i)
            {
                if (scorePages[i] != null) { scorePages[i].SetActive(i == scoreTab_); }
            }

            if (scoreTabImages != null)
            {
                for (int i = 0; i < scoreTabImages.Length; ++i)
                {
                    if (scoreTabImages[i] != null)
                    {
                        scoreTabImages[i].color = i == scoreTab_ ? tabOnColor : tabOffColor;
                    }
                    if (scoreTabLabels != null && i < scoreTabLabels.Length && scoreTabLabels[i] != null
                        && scoreTabNameJa != null && i < scoreTabNameJa.Length)
                    {
                        scoreTabLabels[i].text = Pick(scoreTabNameJa, scoreTabNameEn, scoreTabNameKo, scoreTabNameZhs, scoreTabNameZht, i);
                    }
                }
            }

            int want = Lang();
            if (want == scoreTextLang_) { return; }
            scoreTextLang_ = want;

            if (scoreTexts == null) { return; }
            for (int i = 0; i < scoreTexts.Length; ++i)
            {
                if (scoreTexts[i] == null) { continue; }
                scoreTexts[i].text = Pick(scoreTextJa, scoreTextEn, scoreTextKo, scoreTextZhs, scoreTextZht, i);
            }
        }

        // ------------------------------------------------------------
        // チュートリアル（1画面＝1ページ・矢印で送る）
        //
        // ページの中身は「ブロック」の集まり。ブロックは
        //   見出しチップ（任意）＋ 本文（任意）＋ 牌の並び（任意）
        // の箱で、位置と大きさはビルダーの表が持っている。枠は TUT_BLOCKS 個だけ
        // 作ってページごとに置き直す（一覧のリサイクルビューと同じ考え）。
        // ------------------------------------------------------------
        private void DrawTutorial(bool en)
        {
            int pages = tutPageTitleJa != null ? tutPageTitleJa.Length : 0;
            if (pages <= 0) { return; }
            if (page_ < 0) { page_ = 0; }
            if (page_ >= pages) { page_ = pages - 1; }

            if (titleLabel != null)
            {
                titleLabel.text = Pick(tutPageTitleJa, tutPageTitleEn, tutPageTitleKo, tutPageTitleZhs, tutPageTitleZht, page_);
            }

            if (tutPageLabel != null) { tutPageLabel.text = (page_ + 1) + " / " + pages; }

            // 端では矢印を消す（押しても何も起きないボタンを残さない＝雀魂と同じ）。
            if (tutPrevButton != null) { tutPrevButton.SetActive(page_ > 0); }
            if (tutNextButton != null) { tutNextButton.SetActive(page_ < pages - 1); }

            int start = tutPageStart != null && page_ < tutPageStart.Length ? tutPageStart[page_] : 0;
            int count = tutPageCount != null && page_ < tutPageCount.Length ? tutPageCount[page_] : 0;

            for (int b = 0; b < TUT_BLOCKS; ++b)
            {
                bool has = b < count;
                if (tutBlockRoots != null && b < tutBlockRoots.Length && tutBlockRoots[b] != null)
                {
                    tutBlockRoots[b].SetActive(has);
                }
                if (has) { DrawTutorialBlock(b, start + b, en); }
            }
        }

        private void DrawTutorialBlock(int slot, int idx, bool en)
        {
            if (tutBlockW == null || idx >= tutBlockW.Length) { return; }

            float w = tutBlockW[idx];
            float h = tutBlockH[idx];

            if (tutBlockRects != null && slot < tutBlockRects.Length && tutBlockRects[slot] != null)
            {
                var rt = tutBlockRects[slot];
                rt.anchoredPosition = new Vector2(tutBlockX[idx], -tutBlockY[idx]);
                rt.sizeDelta = new Vector2(w, h);
            }

            string head = Pick(tutBlockHeadJa, tutBlockHeadEn, tutBlockHeadKo, tutBlockHeadZhs, tutBlockHeadZht, idx);
            string body = Pick(tutBlockBodyJa, tutBlockBodyEn, tutBlockBodyKo, tutBlockBodyZhs, tutBlockBodyZht, idx);
            if (head == null) { head = ""; }
            if (body == null) { body = ""; }

            bool hasHead = head.Length > 0;
            bool hasBody = body.Length > 0;

            int tileStart = tutTileStart != null && idx < tutTileStart.Length ? tutTileStart[idx] : 0;
            int tileCount = tutTileCount != null && idx < tutTileCount.Length ? tutTileCount[idx] : 0;
            bool hasTiles = tileCount > 0;

            if (tutHeadRoots != null && slot < tutHeadRoots.Length && tutHeadRoots[slot] != null)
            {
                tutHeadRoots[slot].SetActive(hasHead);
            }
            if (hasHead && tutHeadLabels != null && slot < tutHeadLabels.Length && tutHeadLabels[slot] != null)
            {
                tutHeadLabels[slot].text = head;
            }

            // 牌の大きさはブロックごとに変えられる（牌だけ見せるページは大きく出す）。
            float th = tileHeight;
            if (tutBlockTileH != null && idx < tutBlockTileH.Length && tutBlockTileH[idx] > 0f)
            {
                th = tutBlockTileH[idx];
            }

            // 見出しの有無で本文の始まりが変わる。牌があるときは下に1行ぶん空ける。
            float top = hasHead ? TUT_HEAD_H + 12f : 10f;
            float tileY = h - th - 10f;
            // 本文が無い箱（牌だけ見せる箱）は、牌を空き地の真ん中に置く。
            if (!hasBody && hasTiles) { tileY = top + (h - top - 10f - th) * 0.5f; }
            // 読み方を出すブロックは、牌の下にルビの分の場所が要るので牌を少し上げる。
            bool ruby = tutBlockRuby != null && idx < tutBlockRuby.Length && tutBlockRuby[idx];
            if (ruby && hasTiles) { tileY -= 6f; }

            if (tutBodyRects != null && slot < tutBodyRects.Length && tutBodyRects[slot] != null)
            {
                float bodyH = (hasTiles ? tileY - 6f : h - 8f) - top;
                if (bodyH < 12f) { bodyH = 12f; }
                var brt = tutBodyRects[slot];
                brt.anchoredPosition = new Vector2(12f, -top);
                brt.sizeDelta = new Vector2(w - 24f, bodyH);
            }
            if (tutBodyLabels != null && slot < tutBodyLabels.Length && tutBodyLabels[slot] != null)
            {
                tutBodyLabels[slot].gameObject.SetActive(hasBody);
                tutBodyLabels[slot].text = body;
            }

            if (tutTileRows != null && slot < tutTileRows.Length && tutTileRows[slot] != null)
            {
                tutTileRows[slot].gameObject.SetActive(hasTiles);
                tutTileRows[slot].anchoredPosition = new Vector2(12f, -tileY);
                tutTileRows[slot].sizeDelta = new Vector2(w - 24f, th);
            }

            DrawTutorialTiles(slot, tileStart, tileCount, w - 24f, th, ruby, en);
        }

        // 牌の並べ方は一覧と同じ（枚数は減らさず、入りきらない行だけ縮める）。
        private void DrawTutorialTiles(int slot, int start, int count, float areaW, float tileH_,
                                       bool ruby, bool en)
        {
            if (tutTiles == null || tutTilePlan == null) { return; }

            int numTiles = 0;
            int numGaps = 0;
            for (int k = 0; k < count; ++k)
            {
                if (start + k >= tutTilePlan.Length) { break; }
                if (tutTilePlan[start + k] < 0) { ++numGaps; } else { ++numTiles; }
            }

            float aspect = 0.6689f;
            if (tileW != null && tileH != null && tileW.Length > 0 && tileH.Length > 0 && tileH[0] > 0f)
            {
                aspect = tileW[0] / tileH[0];
            }

            float w = tileH_ * aspect;
            float need = numTiles * w + numGaps * tileGap;
            float scale = (need > areaW && need > 0f) ? areaW / need : 1f;

            float x = 0f;
            int drawn = 0;

            for (int k = 0; k < count; ++k)
            {
                if (start + k >= tutTilePlan.Length) { break; }
                int id = tutTilePlan[start + k];
                if (id < 0) { x += tileGap * scale; continue; }

                int at = slot * MAX_TILES + drawn;
                if (at >= tutTiles.Length) { break; }

                var img = tutTiles[at];
                ++drawn;
                if (img == null) { continue; }

                img.gameObject.SetActive(true);
                img.texture = tileTexture;
                if (tileU != null && id < tileU.Length)
                {
                    img.uvRect = new Rect(tileU[id], tileV[id], tileW[id], tileH[id]);
                }

                var rt = img.rectTransform;
                rt.sizeDelta = new Vector2(w * scale, tileH_ * scale);
                rt.anchoredPosition = new Vector2(x, 0f);
                x += w * scale;

                if (tutTileRuby != null && at < tutTileRuby.Length && tutTileRuby[at] != null)
                {
                    var rb = tutTileRuby[at];
                    rb.gameObject.SetActive(ruby);
                    if (ruby)
                    {
                        rb.text = RubyTextFor(id, en);
                        rb.color = RubyColorFor(id);
                    }
                }
            }

            for (int k = drawn; k < MAX_TILES; ++k)
            {
                int at = slot * MAX_TILES + k;
                if (at >= tutTiles.Length) { break; }
                if (tutTiles[at] != null) { tutTiles[at].gameObject.SetActive(false); }
            }
        }

        // 牌の下に出す読み方。牌ID はビルダーの TileOrder 順で、
        // 0..26 が数牌（M/P/S の交互・id/3+1 が数字）、27..29 が赤五、30..36 が字牌。
        private string RubyTextFor(int id, bool en)
        {
            if (id >= 0 && id < 27) { return "" + (id / 3 + 1); }
            if (id >= 27 && id <= 29) { return "5"; }
            if (Lang() == KemonoLanguage.LANG_KO)
            {
                if (id == 30) { return "동"; }
                if (id == 31) { return "남"; }
                if (id == 32) { return "서"; }
                if (id == 33) { return "북"; }
                if (id == 34) { return "백"; }
                if (id == 35) { return "발"; }
                if (id == 36) { return "중"; }
            }
            if (Lang() == KemonoLanguage.LANG_ZHS || Lang() == KemonoLanguage.LANG_ZHT)
            {
                bool t = Lang() == KemonoLanguage.LANG_ZHT;
                if (id == 30) { return t ? "東" : "东"; }
                if (id == 31) { return "南"; }
                if (id == 32) { return "西"; }
                if (id == 33) { return "北"; }
                if (id == 34) { return "白"; }
                if (id == 35) { return t ? "發" : "发"; }
                if (id == 36) { return "中"; }
            }
            if (id == 30) { return en ? "East" : "トン"; }
            if (id == 31) { return en ? "South" : "ナン"; }
            if (id == 32) { return en ? "West" : "シャー"; }
            if (id == 33) { return en ? "North" : "ペー"; }
            if (id == 34) { return en ? "White" : "ハク"; }
            if (id == 35) { return en ? "Green" : "ハツ"; }
            if (id == 36) { return en ? "Red" : "チュン"; }
            return "";
        }

        private Color RubyColorFor(int id)
        {
            if (id >= 0 && id < 27)
            {
                int suit = id % 3;
                if (suit == 0) { return rubyColorMan; }
                if (suit == 1) { return rubyColorPin; }
                return rubyColorSou;
            }
            if (id == 27) { return rubyColorMan; }
            if (id == 28) { return rubyColorPin; }
            if (id == 29) { return rubyColorSou; }
            return rubyColorHonor;
        }
    }
}
