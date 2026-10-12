using TMPro;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    // インスタンス内ランキング表（デイリーランキング・ベータ版）
    //
    // ■ 役割
    //   「本日（JST）の段位戦の累計スコア」を、いまこのインスタンスにいる人だけで
    //   並べて表示する World Space パネル。docs の 2.5／3.6 の
    //   「ベータはインスタンス内限定・反映指標は累計スコア・JST5時リセット」に対応する。
    //
    // ■ 同期変数ゼロ
    //   各自のスコアは PlayerStatsStore が本人の PlayerData
    //   （kmj_day_index / kmj_day_score / kmj_day_games / kmj_day_top）へ書く。
    //   PlayerData は他人の分も読めるので、この盤面は「全員分を読んで並べ替えるだけ」の
    //   完全ローカル表示になる。Owner も同期変数も持たないため、Owner移譲・
    //   同時終局の競合・帯域のどれも発生しない。
    //   代償：退出した人は表から消える（＝いま居る人のランキングになる）。
    //   横断ランキングは将来の外部サーバー版で解決する前提。
    //
    // ■ 集計対象
    //   PlayerStatsStore.ApplyRankedGameResultNow から1対局1回だけ加算する。
    //   よって段位が動く対局＝段位戦だけが対象で、フリー対局・デバッグ局・
    //   途中入室の観戦は最初から入らない（除外処理を別に書かない）。
    //
    // ■ 更新
    //   Update は「時刻を1回比べるだけ」の間引きポーリング（既定5秒）。
    //   他人の PlayerData は変更イベントが取れないため、これが最も安いやり方。
    //   参加・退出・復元完了時は即座に組み直す。
    //
    // ■ 使い方
    //   Tools > Kemono Mahjong > Build Instance Ranking Panel    … 生成＋配線
    //   Tools > Kemono Mahjong > Validate Instance Ranking Panel … 参照欠けの検査
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class InstanceRankingBoard : UdonSharpBehaviour
    {
        [Header("表示言語（Wire Language で自動割り当て）")]
        [Tooltip("集計日・自分の順位行などの言語。見出しや脚注は KemonoLocalizer 側が差し替える。")]
        public KemonoLanguage languageManager;
        // 0=日本語 / 1=英語 / 2=韓国語 / 3=簡体字 / 4=繁体字。-1＝未解決（初回参照時に languageManager から取りに行く）。
        private int lang_ = -1;

        private int Lang()
        {
            if (lang_ < 0) { lang_ = (languageManager != null) ? languageManager.GetLanguage() : KemonoLanguage.LANG_JA; }
            return lang_;
        }

        private bool En() { return Lang() == KemonoLanguage.LANG_EN; }
        private bool Ko() { return Lang() == KemonoLanguage.LANG_KO; }
        private bool Zhs() { return Lang() == KemonoLanguage.LANG_ZHS; }
        private bool Zht() { return Lang() == KemonoLanguage.LANG_ZHT; }

        // KemonoLanguage から SendCustomEvent で呼ばれる配布口。
        public void ApplyLanguageJa() { SetLang(KemonoLanguage.LANG_JA); }
        public void ApplyLanguageEn() { SetLang(KemonoLanguage.LANG_EN); }
        public void ApplyLanguageKo() { SetLang(KemonoLanguage.LANG_KO); }
        public void ApplyLanguageZhs() { SetLang(KemonoLanguage.LANG_ZHS); }
        public void ApplyLanguageZht() { SetLang(KemonoLanguage.LANG_ZHT); }

        private void SetLang(int lang)
        {
            if (lang_ == lang) { return; }
            lang_ = lang;
            // Start より先に呼ばれたら描かない（集計用の配列がまだ無い）。
            // Update の定期 Rebuild がすぐに拾い直すので取りこぼしにはならない。
            if (entryName_ == null) { return; }
            UpdateView();
        }

        // 英語の序数（1st / 2nd / 3rd / 4th …）。11〜13 は th 固定。
        private string Ordinal(int n)
        {
            int tens = n % 100;
            if (tens >= 11 && tens <= 13) { return n.ToString() + "th"; }
            int ones = n % 10;
            if (ones == 1) { return n.ToString() + "st"; }
            if (ones == 2) { return n.ToString() + "nd"; }
            if (ones == 3) { return n.ToString() + "rd"; }
            return n.ToString() + "th";
        }

        public const int MAX_PLAYERS = 64; // VRChatの上限50より余裕を持たせる

        // 表示モード。レイアウト・ページ送り・並べ替えの仕組みは共通なので、1つのスクリプトを
        // 2枚のパネルで共有する（Build時にこの値だけを変える）。
        //   MODE_DAILY … 本日の段位戦スコア（累計スコア・JST5時リセット）
        //   MODE_RANK  … 段位そのもの（PlayerDataの永続値・リセットなし）
        //   MODE_BEST  … 本日の最高和了（1回の和了の最大打点・JST5時リセット）
        public const int MODE_DAILY = 0;
        public const int MODE_RANK = 1;
        public const int MODE_BEST = 2;

        [Header("表示モード（0=本日のスコア / 1=段位 / 2=本日の最高和了）")]
        public int mode = MODE_DAILY;

        [Header("Data")]
        public PlayerStatsStore statsStore;

        [Header("行のプレートUI（Build Instance Ranking Panel が自動配線）")]
        [Tooltip("1ページ分の行ルート。表示件数がこれより少ない行は隠す。")]
        public GameObject[] rowObjects;
        [Tooltip("プレート本体。上位3位は金、それ以下は銀に差し替える。")]
        public Image[] rowPlates;
        [Tooltip("左端のバッジ。上位3位は王冠（金銀銅）、それ以下は番号タグ。")]
        public Image[] rowBadges;
        public Image[] rowScrewLeft;
        public Image[] rowScrewRight;
        public TextMeshProUGUI[] rowPlaceTexts;
        public TextMeshProUGUI[] rowNameTexts;
        public TextMeshProUGUI[] rowRankTexts;
        public RawImage[] rowRankImages;
        public TextMeshProUGUI[] rowRankLevelTexts;
        public TextMeshProUGUI[] rowScoreTexts;
        public TextMeshProUGUI[] rowGameTexts;

        [Header("その他UI")]
        public TextMeshProUGUI dateText;
        public TextMeshProUGUI pageText;
        public TextMeshProUGUI selfText;
        public TextMeshProUGUI emptyText;

        [Header("スプライト（Build Ranking Panel Art が生成）")]
        public Sprite plateTop3;
        public Sprite plateNormal;
        public Sprite badgeCrown1;
        public Sprite badgeCrown2;
        public Sprite badgeCrown3;
        public Sprite badgeTag;
        public Sprite screwGold;
        public Sprite screwSilver;
        [Tooltip("段位大分類（初獣、鋭爪、剛牙、獣威、幻獣、天獣）の紋章。実素材は Textures/UI/rank。")]
        public Texture[] rankEmblems;

        [Header("色")]
        [Tooltip("自分の行のプレートに掛ける色。白なら素のまま。")]
        public Color plateTintSelf = new Color(0.88f, 0.94f, 1f, 1f);
        [Tooltip("自分の行の名前の色。")]
        public Color nameColorSelf = new Color(0.07f, 0.27f, 0.50f, 1f);
        public Color plateTintNormal = Color.white;
        [Tooltip("金プレートの上の文字色（濃い茶）。")]
        public Color textOnTop3 = new Color(0.24f, 0.16f, 0.05f, 1f);
        public Color subTextOnTop3 = new Color(0.42f, 0.31f, 0.13f, 1f);
        [Tooltip("銀プレートの上の文字色（濃いグレー）。")]
        public Color textOnNormal = new Color(0.14f, 0.15f, 0.18f, 1f);
        public Color subTextOnNormal = new Color(0.36f, 0.38f, 0.43f, 1f);
        [Tooltip("バッジの上の番号の色。")]
        public Color placeNumberColor = Color.white;
        // 明るいプレートの上に乗るので、プラスマイナスの色は深めに取る。
        public Color scoreColorPlus = new Color(0.09f, 0.42f, 0.20f, 1f);
        public Color scoreColorMinus = new Color(0.62f, 0.15f, 0.12f, 1f);

        [Header("更新間隔（秒）")]
        [Tooltip("他人のPlayerDataは変更通知が来ないため、この間隔で読み直す。")]
        public float refreshInterval = 5f;

        // 並べ替え用バッファ（Startで確保して以降は使い回す＝毎回のnewを避ける）
        private VRCPlayerApi[] playerBuffer_;
        private int[] entryPlayerId_;
        private string[] entryName_;
        // 配列はモードで意味が変わる（本数を増やさずに使い回す）。
        //   スコア版　: score=スコア / games=対局数 / top=1位数
        //   段位版　　: rankLv=内部Lv / rankValue=pt（天獣は天珠x10）/ games=段位戦の通算対局数
        //   最高和了版: score=打点 / games=翻 / top=符 / rankValue=役満の重ね数
        private int[] entryScore_;
        private int[] entryGames_;
        private int[] entryTop_;
        private int[] entryRankLv_;
        private int[] entryRankValue_;
        private int entryCount_;

        private int page_;
        private int rowsPerPage_;
        private float nextRefreshTime_;
        private int selfPlace_;
        private int selfIndex_;

        private void Start()
        {
            playerBuffer_ = new VRCPlayerApi[MAX_PLAYERS];
            entryPlayerId_ = new int[MAX_PLAYERS];
            entryName_ = new string[MAX_PLAYERS];
            entryScore_ = new int[MAX_PLAYERS];
            entryGames_ = new int[MAX_PLAYERS];
            entryTop_ = new int[MAX_PLAYERS];
            entryRankLv_ = new int[MAX_PLAYERS];
            entryRankValue_ = new int[MAX_PLAYERS];

            rowsPerPage_ = rowObjects != null ? rowObjects.Length : 0;
            if (refreshInterval < 1f) { refreshInterval = 1f; }
            nextRefreshTime_ = Time.time + refreshInterval;
            Rebuild();
        }

        private void Update()
        {
            // 毎フレームやるのは float の比較1回だけ。実際の読み直しは refreshInterval ごと。
            if (Time.time < nextRefreshTime_) { return; }
            nextRefreshTime_ = Time.time + refreshInterval;
            Rebuild();
        }

        public override void OnPlayerJoined(VRCPlayerApi player) { Rebuild(); }
        public override void OnPlayerLeft(VRCPlayerApi player) { Rebuild(); }

        // 自分・他人どちらの復元完了でも呼ばれる。復元前は PlayerData が読めないため、
        // ここで組み直さないと「入ったばかりの人が空欄のまま」に見える。
        public override void OnPlayerRestored(VRCPlayerApi player) { Rebuild(); }

        // ============================================================
        // ボタン（Button.onClick から SendCustomEvent で呼ばれる）
        // ============================================================

        public void OnPrevPage()
        {
            if (page_ <= 0) { return; }
            --page_;
            UpdateView();
        }

        public void OnNextPage()
        {
            if (page_ + 1 >= GetPageCount()) { return; }
            ++page_;
            UpdateView();
        }

        public void OnRefresh()
        {
            nextRefreshTime_ = Time.time + refreshInterval;
            Rebuild();
        }

        // ============================================================
        // 収集と並べ替え
        // ============================================================

        private void Rebuild()
        {
            CollectEntries();
            SortEntries();
            ResolveSelfPlace();
            ClampPage();
            UpdateView();
        }

        private void CollectEntries()
        {
            entryCount_ = 0;
            if (statsStore == null || playerBuffer_ == null) { return; }

            int count = VRCPlayerApi.GetPlayerCount();
            if (count > MAX_PLAYERS) { count = MAX_PLAYERS; }
            VRCPlayerApi.GetPlayers(playerBuffer_);
            int today = mode == MODE_RANK ? 0 : statsStore.GetJstDayIndex();

            for (int i = 0; i < count; ++i)
            {
                VRCPlayerApi player = playerBuffer_[i];
                if (player == null || !Utilities.IsValid(player)) { continue; }

                if (mode == MODE_RANK)
                {
                    // PlayerData未復元の人は読めず「初獣★1 0pt」に見えるので載せない。
                    if (!statsStore.HasRankDataOf(player)) { continue; }

                    int lv = statsStore.GetRankLvOf(player);
                    entryRankLv_[entryCount_] = lv;
                    // 天獣帯は天珠x10、それ未満は段位ptが「同じ段位の中での順序」になる。
                    entryRankValue_[entryCount_] = lv >= PlayerStatsStore.RANK_TENJU_FIRST_LV
                        ? statsStore.GetTenjuOrbX10Of(player)
                        : statsStore.GetRankPtOf(player);
                    // 段位版の「通算」列は段位が動いた対局だけ（四麻＋三麻の段位戦）。
                    // KEY_ST4_PLAY + KEY_ST3_PLAY は手動着席・CPU混じり・未登録卓・
                    // デバッグ局まで含むので使わない（2026-09-14 に専用カウンタへ差し替え）。
                    entryGames_[entryCount_] = statsStore.GetRankedPlayCountOf(player);
                    entryScore_[entryCount_] = 0;
                    entryTop_[entryCount_] = 0;
                }
                else if (mode == MODE_BEST)
                {
                    // 日付が違う＝前日以前の記録。朝5時をまたいだら自動的に消える。
                    if (statsStore.GetDailyDayIndexOf(player) != today) { continue; }
                    int points = statsStore.GetDailyBestPointsOf(player);
                    if (points <= 0) { continue; }

                    entryScore_[entryCount_] = points;
                    entryGames_[entryCount_] = statsStore.GetDailyBestHanOf(player);
                    entryTop_[entryCount_] = statsStore.GetDailyBestFuOf(player);
                    entryRankValue_[entryCount_] = statsStore.GetDailyBestYakumanOf(player);
                    entryRankLv_[entryCount_] = statsStore.GetRankLvOf(player);
                }
                else
                {
                    // 日付が違う＝前日以前の記録。朝5時をまたいだら自動的に消える。
                    if (statsStore.GetDailyDayIndexOf(player) != today) { continue; }
                    int games = statsStore.GetDailyGamesOf(player);
                    if (games <= 0) { continue; }

                    entryScore_[entryCount_] = statsStore.GetDailyScoreOf(player);
                    entryGames_[entryCount_] = games;
                    entryTop_[entryCount_] = statsStore.GetDailyTopOf(player);
                    entryRankLv_[entryCount_] = statsStore.GetRankLvOf(player);
                    entryRankValue_[entryCount_] = 0;
                }

                entryPlayerId_[entryCount_] = player.playerId;
                entryName_[entryCount_] = player.displayName;
                ++entryCount_;
            }
        }

        // 挿入ソート（最大64件・5秒に1回なのでこれで十分）。
        private void SortEntries()
        {
            for (int i = 1; i < entryCount_; ++i)
            {
                int id = entryPlayerId_[i];
                string name = entryName_[i];
                int score = entryScore_[i];
                int games = entryGames_[i];
                int top = entryTop_[i];
                int lv = entryRankLv_[i];
                int value = entryRankValue_[i];

                int j = i - 1;
                while (j >= 0 && IsBefore(score, top, games, lv, value, id,
                                          entryScore_[j], entryTop_[j], entryGames_[j],
                                          entryRankLv_[j], entryRankValue_[j], entryPlayerId_[j]))
                {
                    entryPlayerId_[j + 1] = entryPlayerId_[j];
                    entryName_[j + 1] = entryName_[j];
                    entryScore_[j + 1] = entryScore_[j];
                    entryGames_[j + 1] = entryGames_[j];
                    entryTop_[j + 1] = entryTop_[j];
                    entryRankLv_[j + 1] = entryRankLv_[j];
                    entryRankValue_[j + 1] = entryRankValue_[j];
                    --j;
                }

                entryPlayerId_[j + 1] = id;
                entryName_[j + 1] = name;
                entryScore_[j + 1] = score;
                entryGames_[j + 1] = games;
                entryTop_[j + 1] = top;
                entryRankLv_[j + 1] = lv;
                entryRankValue_[j + 1] = value;
            }
        }

        // A が B より上か。
        //   スコア版　：スコア降順 → 1位数降順 → 対局数昇順（少ない試合で稼いだ方が上）→ playerId昇順
        //   段位版　　：内部Lv降順 → pt（天獣なら天珠）降順 → 累計対局数降順 → playerId昇順
        //   最高和了版：打点降順 → 翻降順 → 符降順 → playerId昇順
        private bool IsBefore(int scoreA, int topA, int gamesA, int lvA, int valueA, int idA,
                              int scoreB, int topB, int gamesB, int lvB, int valueB, int idB)
        {
            if (mode == MODE_RANK)
            {
                if (lvA != lvB) { return lvA > lvB; }
                if (valueA != valueB) { return valueA > valueB; }
                if (gamesA != gamesB) { return gamesA > gamesB; }
                return idA < idB;
            }

            if (mode == MODE_BEST)
            {
                if (scoreA != scoreB) { return scoreA > scoreB; }
                if (gamesA != gamesB) { return gamesA > gamesB; }
                if (topA != topB) { return topA > topB; }
                return idA < idB;
            }

            if (scoreA != scoreB) { return scoreA > scoreB; }
            if (topA != topB) { return topA > topB; }
            if (gamesA != gamesB) { return gamesA < gamesB; }
            return idA < idB;
        }

        private void ResolveSelfPlace()
        {
            selfPlace_ = 0;
            selfIndex_ = -1;

            VRCPlayerApi local = Networking.LocalPlayer;
            if (local == null || !Utilities.IsValid(local)) { return; }

            for (int i = 0; i < entryCount_; ++i)
            {
                if (entryPlayerId_[i] == local.playerId)
                {
                    selfIndex_ = i;
                    selfPlace_ = i + 1;
                    return;
                }
            }
        }

        private int GetPageCount()
        {
            if (rowsPerPage_ <= 0) { return 1; }
            int pages = (entryCount_ + rowsPerPage_ - 1) / rowsPerPage_;
            return pages <= 0 ? 1 : pages;
        }

        private void ClampPage()
        {
            int pages = GetPageCount();
            if (page_ >= pages) { page_ = pages - 1; }
            if (page_ < 0) { page_ = 0; }
        }

        // ============================================================
        // 描画
        // ============================================================

        private void UpdateView()
        {
            // 段位版に集計日は無い（Builderが書いた説明文をそのまま残す）。
            if (mode != MODE_RANK && dateText != null && statsStore != null)
            {
                dateText.text = Ko()
                    ? ("집계일 " + statsStore.GetJstDateText() + " (한국 시간 오전 5시 초기화)")
                    : Zhs()
                    ? ("统计日 " + statsStore.GetJstDateText() + "（日本时间早上5点重置）")
                    : Zht()
                    ? ("統計日 " + statsStore.GetJstDateText() + "（日本時間早上5點重置）")
                    : En()
                    ? ("Tallied " + statsStore.GetJstDateText() + " (resets at 5:00 AM JST)")
                    : ("集計日 " + statsStore.GetJstDateText() + "（JST 朝5時リセット）");
            }

            int first = page_ * rowsPerPage_;
            for (int r = 0; r < rowsPerPage_; ++r)
            {
                int index = first + r;
                bool used = index < entryCount_;
                if (rowObjects != null && r < rowObjects.Length && rowObjects[r] != null)
                {
                    rowObjects[r].SetActive(used);
                }
                if (used) { FillRow(r, index); }
            }

            if (emptyText != null)
            {
                emptyText.gameObject.SetActive(entryCount_ <= 0);
            }

            if (pageText != null)
            {
                pageText.text = (page_ + 1).ToString() + " / " + GetPageCount().ToString()
                                + (En()
                                    ? ("  (" + entryCount_.ToString() + ")")
                                    : Ko()
                                    ? ("  (" + entryCount_.ToString() + "명)")
                                    : ("  （" + entryCount_.ToString() + "人）"));
            }

            if (selfText != null) { selfText.text = BuildSelfText(); }
        }

        private string BuildSelfText()
        {
            if (entryCount_ <= 0) { return ""; }

            bool en = En();
            bool ko = Ko();
            bool zhs = Zhs();
            bool zht = Zht();

            if (mode == MODE_RANK)
            {
                if (selfIndex_ < 0)
                {
                    return ko ? "내 단위 데이터를 불러오는 중입니다."
                         : zhs ? "正在读取你的段位数据…"
                         : zht ? "正在讀取你的段位資料…"
                         : en ? "Loading your rank data..." : "あなたの段位データを読み込み中です。";
                }

                string rankName = statsStore != null
                    ? statsStore.GetRankDisplayNameForLv(entryRankLv_[selfIndex_]) : "";
                string rankValue = FormatRankValue(entryRankLv_[selfIndex_], entryRankValue_[selfIndex_]);

                if (en)
                {
                    return "You: " + Ordinal(selfPlace_) + " of " + entryCount_.ToString()
                           + "   " + rankName + "   " + rankValue;
                }
                if (ko)
                {
                    return "나: " + entryCount_.ToString() + "명 중 " + selfPlace_.ToString() + "위"
                           + "   " + rankName + "   " + rankValue;
                }
                if (zhs || zht)
                {
                    return SelfPlaceZh() + "　" + rankName + "　" + rankValue;
                }
                return "あなた: " + selfPlace_.ToString() + "位 / " + entryCount_.ToString() + "人中"
                       + "　" + rankName + "　" + rankValue;
            }

            if (mode == MODE_BEST)
            {
                if (selfIndex_ < 0)
                {
                    return ko ? "오늘은 아직 화료가 없습니다."
                         : zhs ? "今天还没有和牌。"
                         : zht ? "今天還沒有和牌。"
                         : en ? "You have no winning hand today yet."
                              : "あなたは本日まだ和了がありません。";
                }
                string hand = FormatBestHand(entryGames_[selfIndex_], entryTop_[selfIndex_],
                                             entryRankValue_[selfIndex_]);
                if (en)
                {
                    return "You: " + Ordinal(selfPlace_) + " of " + entryCount_.ToString()
                           + "   " + entryScore_[selfIndex_].ToString() + " pts   " + hand;
                }
                if (ko)
                {
                    return "나: " + entryCount_.ToString() + "명 중 " + selfPlace_.ToString() + "위"
                           + "   " + entryScore_[selfIndex_].ToString() + "점   " + hand;
                }
                if (zhs || zht)
                {
                    return SelfPlaceZh() + "　" + entryScore_[selfIndex_].ToString()
                           + (zht ? "點　" : "点　") + hand;
                }
                return "あなた: " + selfPlace_.ToString() + "位 / " + entryCount_.ToString() + "人中"
                       + "　" + entryScore_[selfIndex_].ToString() + "点　" + hand;
            }

            if (selfIndex_ < 0)
            {
                return ko ? "오늘은 아직 단위전 기록이 없습니다."
                     : zhs ? "今天还没有段位战记录。"
                     : zht ? "今天還沒有段位戰記錄。"
                     : en ? "You have no ranked results today yet."
                          : "あなたは本日まだ段位戦の記録がありません。";
            }

            if (en)
            {
                return "You: " + Ordinal(selfPlace_) + " of " + entryCount_.ToString()
                       + "   Score " + FormatScore(entryScore_[selfIndex_])
                       + "   " + entryGames_[selfIndex_].ToString() + " games, "
                       + entryTop_[selfIndex_].ToString() + " wins";
            }
            if (ko)
            {
                return "나: " + entryCount_.ToString() + "명 중 " + selfPlace_.ToString() + "위"
                       + "   스코어 " + FormatScore(entryScore_[selfIndex_])
                       + "   " + entryGames_[selfIndex_].ToString() + "전 "
                       + entryTop_[selfIndex_].ToString() + "승";
            }
            if (zhs || zht)
            {
                return SelfPlaceZh() + "　得分 " + FormatScore(entryScore_[selfIndex_])
                       + "　" + entryGames_[selfIndex_].ToString() + (zht ? "戰 " : "战 ")
                       + entryTop_[selfIndex_].ToString() + (zht ? "勝" : "胜");
            }
            return "あなた: " + selfPlace_.ToString() + "位 / " + entryCount_.ToString() + "人中"
                   + "　スコア " + FormatScore(entryScore_[selfIndex_])
                   + "　" + entryGames_[selfIndex_].ToString() + "戦 "
                   + entryTop_[selfIndex_].ToString() + "勝";
        }

        // 中国語の「我：第N名 / 共M人」。
        private string SelfPlaceZh()
        {
            return "我：第" + selfPlace_.ToString() + "名 / 共" + entryCount_.ToString() + "人";
        }

        private void FillRow(int row, int index)
        {
            int place = index + 1;
            bool isSelf = index == selfIndex_;
            bool isTop3 = place <= 3;
            Color mainColor = isTop3 ? textOnTop3 : textOnNormal;
            Color subColor = isTop3 ? subTextOnTop3 : subTextOnNormal;

            if (rowPlates != null && row < rowPlates.Length && rowPlates[row] != null)
            {
                Sprite plate = isTop3 ? plateTop3 : plateNormal;
                if (plate != null) { rowPlates[row].sprite = plate; }
                rowPlates[row].color = isSelf ? plateTintSelf : plateTintNormal;
            }
            if (rowBadges != null && row < rowBadges.Length && rowBadges[row] != null)
            {
                Sprite badge = badgeTag;
                if (place == 1) { badge = badgeCrown1; }
                else if (place == 2) { badge = badgeCrown2; }
                else if (place == 3) { badge = badgeCrown3; }
                if (badge != null) { rowBadges[row].sprite = badge; }
            }

            Sprite screw = isTop3 ? screwGold : screwSilver;
            if (screw != null)
            {
                if (rowScrewLeft != null && row < rowScrewLeft.Length && rowScrewLeft[row] != null)
                {
                    rowScrewLeft[row].sprite = screw;
                }
                if (rowScrewRight != null && row < rowScrewRight.Length && rowScrewRight[row] != null)
                {
                    rowScrewRight[row].sprite = screw;
                }
            }

            if (rowPlaceTexts != null && row < rowPlaceTexts.Length && rowPlaceTexts[row] != null)
            {
                rowPlaceTexts[row].text = place.ToString();
                rowPlaceTexts[row].color = placeNumberColor;
            }
            if (rowNameTexts != null && row < rowNameTexts.Length && rowNameTexts[row] != null)
            {
                rowNameTexts[row].text = entryName_[index];
                // 自分の行は名前を濃い青にする。プレートを強く染めると
                // 金プレートが緑に濁るので、地はごく薄く・文字で見せる。
                rowNameTexts[row].color = isSelf ? nameColorSelf : mainColor;
            }
            int rankLv = entryRankLv_[index];
            int majorRank = statsStore != null ? statsStore.GetMajorRankIndexForLv(rankLv) : -1;
            // 段位は3枚とも紋章＋★Nで出す（表の見た目を揃える）。
            // 素材が無い・段位が読めない場合だけ従来の段位名テキストへ落ちる。
            bool showRankEmblem = majorRank >= 0
                && rankEmblems != null
                && majorRank < rankEmblems.Length
                && rankEmblems[majorRank] != null;

            if (rowRankTexts != null && row < rowRankTexts.Length && rowRankTexts[row] != null)
            {
                rowRankTexts[row].gameObject.SetActive(!showRankEmblem);
                rowRankTexts[row].text = statsStore != null
                    ? statsStore.GetRankDisplayNameForLv(rankLv)
                    : "";
                rowRankTexts[row].color = subColor;
            }
            if (rowRankImages != null && row < rowRankImages.Length && rowRankImages[row] != null)
            {
                rowRankImages[row].gameObject.SetActive(showRankEmblem);
                if (showRankEmblem) { rowRankImages[row].texture = rankEmblems[majorRank]; }
            }
            if (rowRankLevelTexts != null && row < rowRankLevelTexts.Length && rowRankLevelTexts[row] != null)
            {
                rowRankLevelTexts[row].gameObject.SetActive(showRankEmblem);
                rowRankLevelTexts[row].color = subColor;
                int emblemLevel = majorRank == 5
                    ? rankLv - PlayerStatsStore.RANK_TENJU_FIRST_LV + 1
                    : (rankLv % 3) + 1;
                rowRankLevelTexts[row].text = "★" + emblemLevel.ToString();
            }
            if (rowScoreTexts != null && row < rowScoreTexts.Length && rowScoreTexts[row] != null)
            {
                if (mode == MODE_RANK)
                {
                    rowScoreTexts[row].text = FormatRankValue(entryRankLv_[index], entryRankValue_[index]);
                    rowScoreTexts[row].color = mainColor;
                }
                else if (mode == MODE_BEST)
                {
                    // 打点は必ず正。プラス記号もマイナス色も付けない。
                    rowScoreTexts[row].text = entryScore_[index].ToString();
                    rowScoreTexts[row].color = mainColor;
                }
                else
                {
                    int score = entryScore_[index];
                    rowScoreTexts[row].text = FormatScore(score);
                    rowScoreTexts[row].color = score > 0
                        ? scoreColorPlus
                        : (score < 0 ? scoreColorMinus : mainColor);
                }
            }
            if (rowGameTexts != null && row < rowGameTexts.Length && rowGameTexts[row] != null)
            {
                if (mode == MODE_BEST)
                {
                    rowGameTexts[row].text = FormatBestHand(entryGames_[index], entryTop_[index],
                                                           entryRankValue_[index]);
                }
                else
                {
                    // 英語は列幅が足りないので数字だけ（見出しの Games / Total で意味が取れる）。
                    rowGameTexts[row].text = entryGames_[index].ToString() + (En() ? "" : Ko() ? "전" : Zhs() ? "战" : Zht() ? "戰" : "戦");
                }
                rowGameTexts[row].color = subColor;
            }
        }

        // 段位版の値列。天獣帯は天珠（12.5）、それ未満は「現在pt / 昇段pt」。
        private string FormatRankValue(int lv, int value)
        {
            if (statsStore == null) { return ""; }
            if (lv >= PlayerStatsStore.RANK_TENJU_FIRST_LV)
            {
                return (value / 10).ToString() + "." + (value % 10).ToString();
            }
            return value.ToString() + " / " + statsStore.GetPromotionRankPtForLv(lv).ToString();
        }



        // 最高和了版の右端。役満は翻数を出さず格で見せる（雀魂と同じ読み方）。
        private string FormatBestHand(int han, int fu, int yakuman)
        {
            bool en = En();
            bool ko = Ko();
            bool zhs = Zhs();
            bool zht = Zht();
            if (yakuman >= 1)
            {
                if (yakuman == 1) { return ko ? "역만" : zhs ? "役满" : zht ? "役滿" : en ? "Yakuman" : "役満"; }
                if (yakuman == 2) { return ko ? "더블 역만" : zhs ? "双倍役满" : zht ? "雙倍役滿" : en ? "Double" : "W役満"; }
                return ko ? (yakuman.ToString() + "배 역만")
                     : zhs ? (yakuman.ToString() + "倍役满")
                     : zht ? (yakuman.ToString() + "倍役滿")
                     : en ? (yakuman.ToString() + "x YM") : (yakuman.ToString() + "倍役満");
            }
            if (han <= 0) { return "-"; }
            if (fu <= 0) { return han.ToString() + (ko ? "판" : (zhs || zht) ? "番" : en ? " han" : "翻"); }
            if (zhs || zht) { return han.ToString() + "番" + fu.ToString() + "符"; }
            return ko
                ? (han.ToString() + "판 " + fu.ToString() + "부")
                : en
                ? (han.ToString() + " han " + fu.ToString() + " fu")
                : (han.ToString() + "翻" + fu.ToString() + "符");
        }

        private string FormatScore(int score)
        {
            return score > 0 ? "+" + score.ToString() : score.ToString();
        }
    }
}
