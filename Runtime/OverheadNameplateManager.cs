using TMPro;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    // 頭上ネームプレート（追従処理を1系統に統合）
    //
    // ■ 役割（docs/マッチング・フリー対局戦_確定仕様.md C）
    //   全プレイヤーの頭上に「ランクアイコン（常時）」＋「待機/募集ステータス
    //   （マッチング中のみ）」を表示する。追従・ビルボード・カリングを
    //   この1コンポーネントの LateUpdate に集約する（＝1系統）。頭上への
    //   Transform追従は毎フレーム、対象者/ランク/ステータスの再評価は
    //   dataRefreshInterval ごとに分離して行う。
    //
    //   - ランク値：PlayerStatsStore.GetMajorRankIndexOf(player)（クロスインスタンス
    //     PlayerData。他家ぶんも読める）。アイコン画像は rankIcons[大段位0..5]。
    //   - ステータス：MatchmakingManager.GetStatusOf(player)（Manual同期）。
    //   - このコンポーネントは同期変数を持たない（完全ローカル描画）。
    //
    // ■ プール方式
    //   Udon はランタイム Instantiate 不可のため、表示枠（プレート）は
    //   Editor で N 個作り置きする（BuildNameplates）。近接カリングで
    //   実際に見えるのは近くの数枚のみ。プールが足りない時は近い順で埋め、
    //   あふれた分は表示しない（degrade。poolSize は用途に応じ調整）。
    //
    // ■ Quest性能メモ
    //   - プレートは各自 World Space Canvas。位置/回転更新だけなら
    //     Canvas の頂点再構築は起きない。テキスト/アイコンは「値が変わった
    //     時だけ」書き換える（差分キャッシュ）ことで再構築を抑制。
    //   - showDistance で毎フレームの表示枚数を絞る。
    //
    // ■ Udon地雷メモ
    //   - 一括非表示は「親プレートを畳むだけ」。子（アイコン/テキスト）の
    //     SetActive はプレートが表示状態の時のみ操作する（親を先に畳んで
    //     子SetActiveが漏れる不具合を回避）。
    //   - 負のint(uint)化なし。空き番兵は playerId=0。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class OverheadNameplateManager : UdonSharpBehaviour
    {
        [Header("表示言語（Wire Language で自動割り当て）")]
        [Tooltip("待機ステータスの言語。未割当なら日本語のまま。")]
        public KemonoLanguage languageManager;
        // 0=日本語 / 1=英語 / 2=韓国語 / 3=簡体字 / 4=繁体字。-1＝未解決（初回参照時に languageManager から取りに行く）。
        // Start の実行順は保証されないので、push を待たずに遅延で解決する。
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
            RebuildStatusLabels();
            InvalidatePlateCache();
        }
        
        [Header("参照（Wire Nameplates で自動割り当て）")]
        public PlayerStatsStore statsStore;
        public MatchmakingManager matchmaking;
        [Tooltip("スタッフ表示名の allowlist。実体は StaffOnlyArea に一本化してある（スタッフ追加は1か所で済む）。")]
        public StaffOnlyArea staffArea;

        [Header("プレート・プール（BuildNameplates で生成）")]
        [Tooltip("各プレートのルート（World Space Canvas）")]
        public GameObject[] plates;
        [Tooltip("各プレートのランクアイコン（RawImage）。plates と同数・同順")]
        public RawImage[] plateRankIcons;
        [Tooltip("各プレートのステータス文字（TextMeshProUGUI）。plates と同数・同順")]
        public TextMeshProUGUI[] plateStatusTexts;
        [Tooltip("待機中だけ出す象牙の札。plates と同じ順・同じ数。")]
        public GameObject[] plateBackgrounds;
        [Tooltip("札の左端の差し色。plates と同じ順・同じ数。")]
        public Image[] plateAccents;
        [Tooltip("差し色（index = MatchmakingManager.STATUS_*）。Wire Nameplates が割り当てる。")]
        public Color[] statusAccentColors;
        [Tooltip("スタッフ章（段位アイコンの上）。plates と同じ順・同じ数。")]
        public GameObject[] plateStaffBadges;
        [Tooltip("スタッフ章の文字。plates と同じ順・同じ数。")]
        public TextMeshProUGUI[] plateStaffTexts;

        [Header("ランクアイコン（index=大段位0～5）。未割当なら非表示")]
        [Tooltip("素材が揃うまでは空でよい。空の間はステータスだけ表示される。")]
        public Texture[] rankIcons;

        [Header("表示調整")]
        [Tooltip("頭の位置からの上方向オフセット(m)。VRChat標準ネームプレートと重ならない高さに。")]
        public float headHeightOffset = 0.55f;
        [Tooltip("この距離(m)以内のプレイヤーだけ表示。毎フレームの枚数を絞る。")]
        public float showDistance = 15.0f;
        [Tooltip("自分自身の頭上にも出すか（通常OFF。自分には見えない）")]
        public bool showOnLocalPlayer = false;
        [Tooltip("プレートの localScale（World Space Canvas の見かけサイズ）")]
        public Vector3 plateScale = new Vector3(0.0015f, 0.0015f, 0.0015f);
        [Tooltip("対象者・ランク・待機ステータスを再評価する間隔(秒)。頭上への位置/回転追従は毎フレーム行う。")]
        public float dataRefreshInterval = 0.25f;

        // PlateSize=640、RankIcon=136、ステータス最大幅448（BuildNameplatesで実測・折り返し無し）。
        // 待機中は「アイコン136＋間隔24＋文字448」の塊を中央寄せした位置（塊の左端 -304）へ寄せる。
        // ここを右に寄せすぎると文字の左端がロゴに被る。BuildNameplates 側の Rect と対で調整すること。
        // 待機中のロゴ位置。札は 152..640 に置くので、ロゴはその外側（0..136）へ出す。
        // 「文字だけを枠内に入れる」ための位置。札の Rect（BuildNameplates）と対で決まる。
        private const float ICON_WAITING_X = -252f;
        private const float MIN_DATA_REFRESH_INTERVAL = 0.05f;

        // スタッフ章は段位アイコンの真上に置く。アイコンは136角＝上端が +68 なので、その少し上へ。
        // 章はプレートの Rect（高さ160）からはみ出すが、Mask を付けていないので
        // World Space Canvas でもそのまま描画される。
        private const float STAFF_BADGE_Y = 112f;

        // ステータス文言（index = status code）。0=idle は空文字。
        private string[] statusLabels_;
        // スタッフ章の文言（言語で切り替わる）。
        private string staffLabel_;

        // 再利用バッファ（Start で確保。GetPlayers は最大82人）。
        private const int PLAYER_BUF = 82;
        private VRCPlayerApi[] buf_;
        private int worthyCount_;
        private VRCPlayerApi[] worthyPlayer_;
        private int[] worthyId_;
        private int[] worthyRank_;
        private int[] worthyStatus_;
        private bool[] worthyHasIcon_;
        private bool[] worthyHasStatus_;
        private bool[] worthyIsStaff_;
        private bool[] worthyHandled_;

        // スタッフ判定は表示名の比較（ToLower）なので、毎回引くとゴミが出る。
        // playerId ごとに一度だけ引いて覚える。表示名は在室中に変わらない。
        private int[] staffCacheId_;
        private bool[] staffCacheVal_;
        private int staffCacheCount_;

        // プレートごとの現在割当・差分キャッシュ。
        private int poolSize_;
        private VRCPlayerApi[] platePlayer_;
        private int[] plateOwnerId_;     // 表示中のplayerId（0=空）
        private int[] plateCachedRank_;
        private int[] plateCachedStatus_;
        private int[] plateCachedStaff_;   // -1=未設定 / 0=一般 / 1=スタッフ
        private bool[] plateClaimed_;     // データ更新時に使われたか

        private VRCPlayerApi localPlayer_;
        private float nextDataRefreshTime_;

        void Start()
        {
            localPlayer_ = Networking.LocalPlayer;

            // index = MatchmakingManager.STATUS_*。段位戦は人数×局数の4列なので4種類ある。
            RebuildStatusLabels();

            buf_ = new VRCPlayerApi[PLAYER_BUF];
            worthyPlayer_ = new VRCPlayerApi[PLAYER_BUF];
            worthyId_ = new int[PLAYER_BUF];
            worthyRank_ = new int[PLAYER_BUF];
            worthyStatus_ = new int[PLAYER_BUF];
            worthyHasIcon_ = new bool[PLAYER_BUF];
            worthyHasStatus_ = new bool[PLAYER_BUF];
            worthyIsStaff_ = new bool[PLAYER_BUF];
            worthyHandled_ = new bool[PLAYER_BUF];
            staffCacheId_ = new int[PLAYER_BUF];
            staffCacheVal_ = new bool[PLAYER_BUF];
            staffCacheCount_ = 0;

            poolSize_ = plates != null ? plates.Length : 0;
            platePlayer_ = new VRCPlayerApi[poolSize_];
            plateOwnerId_ = new int[poolSize_];
            plateCachedRank_ = new int[poolSize_];
            plateCachedStatus_ = new int[poolSize_];
            plateCachedStaff_ = new int[poolSize_];
            plateClaimed_ = new bool[poolSize_];
            nextDataRefreshTime_ = 0f;

            for (int i = 0; i < poolSize_; ++i)
            {
                plateOwnerId_[i] = 0;
                plateCachedRank_[i] = -1;   // 未設定マーカ（差分検出用のローカルフラグ。同期しない）
                plateCachedStatus_[i] = -1;
                plateCachedStaff_[i] = -1;
                if (plates[i] != null) { plates[i].SetActive(false); }
            }
        }

        // ステータス文言を現在の言語で作り直す。index = MatchmakingManager.STATUS_*。
        // 段位戦は人数×局数の4列なので4種類ある。
        private void RebuildStatusLabels()
        {
            staffLabel_ = En() ? "STAFF" : Ko() ? "스태프" : Zhs() ? "工作人员" : Zht() ? "工作人員" : "スタッフ";

            if (En())
            {
                statusLabels_ = new string[]
                {
                    "",                        // STATUS_IDLE
                    "4P Half Game - waiting",  // STATUS_WAIT_YONMA
                    "3P Half Game - waiting",  // STATUS_WAIT_SANMA
                    "Recruiting players",      // STATUS_FREE_RECRUIT
                    "4P East Only - waiting",  // STATUS_WAIT_YONMA_TONPU
                    "3P East Only - waiting",  // STATUS_WAIT_SANMA_TONPU
                };
                return;
            }

            if (Ko())
            {
                statusLabels_ = new string[]
                {
                    "",                   // STATUS_IDLE
                    "4인 반장전 대기 중", // STATUS_WAIT_YONMA
                    "3인 반장전 대기 중", // STATUS_WAIT_SANMA
                    "플레이어 모집 중",   // STATUS_FREE_RECRUIT
                    "4인 동풍전 대기 중", // STATUS_WAIT_YONMA_TONPU
                    "3인 동풍전 대기 중", // STATUS_WAIT_SANMA_TONPU
                };
                return;
            }

            if (Zhs())
            {
                statusLabels_ = new string[]
                {
                    "",                   // STATUS_IDLE
                    "四人半庄 等待中",    // STATUS_WAIT_YONMA
                    "三人半庄 等待中",    // STATUS_WAIT_SANMA
                    "招募玩家中",         // STATUS_FREE_RECRUIT
                    "四人东风 等待中",    // STATUS_WAIT_YONMA_TONPU
                    "三人东风 等待中",    // STATUS_WAIT_SANMA_TONPU
                };
                return;
            }

            if (Zht())
            {
                statusLabels_ = new string[]
                {
                    "",                   // STATUS_IDLE
                    "四人半莊 等待中",    // STATUS_WAIT_YONMA
                    "三人半莊 等待中",    // STATUS_WAIT_SANMA
                    "招募玩家中",         // STATUS_FREE_RECRUIT
                    "四人東風 等待中",    // STATUS_WAIT_YONMA_TONPU
                    "三人東風 等待中",    // STATUS_WAIT_SANMA_TONPU
                };
                return;
            }

            statusLabels_ = new string[]
            {
                "",                  // STATUS_IDLE
                "四麻半荘 待機中",   // STATUS_WAIT_YONMA
                "三麻半荘 待機中",   // STATUS_WAIT_SANMA
                "プレイヤー募集中",  // STATUS_FREE_RECRUIT
                "四麻東風 待機中",   // STATUS_WAIT_YONMA_TONPU
                "三麻東風 待機中",   // STATUS_WAIT_SANMA_TONPU
            };
        }

        // 言語切替のあとは差分キャッシュを捨てる。
        // 捨てないと status が変わっていないプレートは旧言語のまま残る。
        private void InvalidatePlateCache()
        {
            if (plateCachedStatus_ == null) { return; }
            for (int i = 0; i < plateCachedStatus_.Length; ++i) { plateCachedStatus_[i] = -1; }
            if (plateCachedStaff_ != null)
            {
                for (int i = 0; i < plateCachedStaff_.Length; ++i) { plateCachedStaff_[i] = -1; }
            }
            nextDataRefreshTime_ = 0f;
        }

        void LateUpdate()
        {
            if (poolSize_ == 0) { return; }

            if (localPlayer_ == null || !Utilities.IsValid(localPlayer_))
            {
                localPlayer_ = Networking.LocalPlayer;
                if (localPlayer_ == null) { return; }
            }

            Vector3 camPos = localPlayer_.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;

            // PlayerData参照、全プレイヤー取得、Matchmakingの線形検索は低頻度で行う。
            // Transform追従だけは毎フレーム行い、頭上表示の滑らかさを維持する。
            float now = Time.time;
            if (now >= nextDataRefreshTime_)
            {
                float interval = dataRefreshInterval;
                if (interval < MIN_DATA_REFRESH_INTERVAL) { interval = MIN_DATA_REFRESH_INTERVAL; }
                nextDataRefreshTime_ = now + interval;
                RefreshPlateData(camPos);
            }

            UpdatePlateTransforms(camPos);
        }

        // 表示対象と表示内容を低頻度で再評価し、プレート割当を更新する。
        private void RefreshPlateData(Vector3 camPos)
        {
            // --- 1) 表示対象プレイヤーを収集 ---
            CollectWorthy(camPos);

            // --- 2) 今回のデータ更新の claim をリセット ---
            for (int i = 0; i < poolSize_; ++i) { plateClaimed_[i] = false; }

            // --- 3) 既存割当を優先（フレーム間でプレートを固定＝チラつき防止） ---
            for (int w = 0; w < worthyCount_; ++w)
            {
                worthyHandled_[w] = false;
                int id = worthyId_[w];
                for (int i = 0; i < poolSize_; ++i)
                {
                    if (!plateClaimed_[i] && plateOwnerId_[i] == id)
                    {
                        ApplyPlateContent(i, w);
                        plateClaimed_[i] = true;
                        worthyHandled_[w] = true;
                        break;
                    }
                }
            }

            // --- 4) 未割当プレイヤーを空きプレートへ ---
            for (int w = 0; w < worthyCount_; ++w)
            {
                if (worthyHandled_[w]) { continue; }
                for (int i = 0; i < poolSize_; ++i)
                {
                    if (!plateClaimed_[i])
                    {
                        ApplyPlateContent(i, w);
                        plateClaimed_[i] = true;
                        worthyHandled_[w] = true;
                        break;
                    }
                }
                // 空きが無ければ（プール枯渇）このプレイヤーは非表示。
            }

            // --- 5) 使われなかったプレートを畳む ---
            for (int i = 0; i < poolSize_; ++i)
            {
                if (!plateClaimed_[i] && plateOwnerId_[i] != 0)
                {
                    plateOwnerId_[i] = 0;
                    platePlayer_[i] = null;
                    if (plates[i] != null) { plates[i].SetActive(false); }
                }
            }
        }

        // 表示すべきプレイヤーを worthy 配列に詰める。
        private void CollectWorthy(Vector3 camPos)
        {
            worthyCount_ = 0;

            // 世界スイッチ（スタッフパネル）。OFFの間は誰にも章を出さない。
            // 1回のデータ更新で1度だけ読む（プレイヤーごとに引くと同期値を人数分なめる）。
            bool badgeOn = matchmaking == null || matchmaking.GetStaffBadgeVisible();

            int count = VRCPlayerApi.GetPlayerCount();
            VRCPlayerApi.GetPlayers(buf_);

            for (int i = 0; i < count && i < PLAYER_BUF; ++i)
            {
                var p = buf_[i];
                if (p == null || !Utilities.IsValid(p)) { continue; }
                if (!showOnLocalPlayer && p.isLocal) { continue; }

                Vector3 headPos = p.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;
                if (Vector3.Distance(camPos, headPos) > showDistance) { continue; }

                // 内部Lv0～34を直接indexにしない。store未配線時は-1で安全に非表示。
                int rank = statsStore != null ? statsStore.GetMajorRankIndexOf(p) : -1;
                int status = matchmaking != null ? matchmaking.GetStatusOf(p) : 0;

                bool hasIcon = rankIcons != null && rank >= 0 && rank < rankIcons.Length && rankIcons[rank] != null;
                bool hasStatus = status > 0 && status < statusLabels_.Length;
                bool isStaff = badgeOn && IsStaffCached(p);

                // 出す中身が何も無いなら枠を消費しない。
                // スタッフ章は段位アイコンが無くても単独で出す（初回入室のスタッフでも肩書は見せる）。
                if (!hasIcon && !hasStatus && !isStaff) { continue; }
                if (worthyCount_ >= PLAYER_BUF) { break; }

                int w = worthyCount_;
                worthyPlayer_[w] = p;
                worthyId_[w] = p.playerId;
                worthyRank_[w] = rank;
                worthyStatus_[w] = status;
                worthyHasIcon_[w] = hasIcon;
                worthyHasStatus_[w] = hasStatus;
                worthyIsStaff_[w] = isStaff;
                ++worthyCount_;
            }
        }

        // スタッフ判定（playerId でメモ化）。allowlist の実体は StaffOnlyArea に一本化してある。
        // キャッシュが埋まりきった時は毎回引きに行く（正しさを優先。実際には82人で頭打ち）。
        private bool IsStaffCached(VRCPlayerApi p)
        {
            if (staffArea == null) { return false; }

            int id = p.playerId;
            for (int i = 0; i < staffCacheCount_; ++i)
            {
                if (staffCacheId_[i] == id) { return staffCacheVal_[i]; }
            }

            bool v = staffArea.IsStaffName(p.displayName);
            if (staffCacheCount_ < PLAYER_BUF)
            {
                staffCacheId_[staffCacheCount_] = id;
                staffCacheVal_[staffCacheCount_] = v;
                ++staffCacheCount_;
            }
            return v;
        }

        // プレート idx に worthy[w] の表示内容を適用（データ更新時かつ差分時のみ）。
        private void ApplyPlateContent(int idx, int w)
        {
            var go = plates[idx];
            if (go == null) { return; }
            if (!go.activeSelf) { go.SetActive(true); }

            int playerId = worthyId_[w];
            int rank = worthyRank_[w];
            int status = worthyStatus_[w];
            bool hasIcon = worthyHasIcon_[w];
            bool hasStatus = worthyHasStatus_[w];
            bool isStaff = worthyIsStaff_[w];

            bool ownerChanged = plateOwnerId_[idx] != playerId;
            if (ownerChanged) { plateOwnerId_[idx] = playerId; }
            platePlayer_[idx] = worthyPlayer_[w];

            bool rankChanged = ownerChanged || plateCachedRank_[idx] != rank;
            bool statusChanged = ownerChanged || plateCachedStatus_[idx] != status;
            int staffCode = isStaff ? 1 : 0;
            bool staffChanged = ownerChanged || plateCachedStaff_[idx] != staffCode;

            // ランクアイコン（差分時のみ）。
            if (rankChanged)
            {
                plateCachedRank_[idx] = rank;
                if (plateRankIcons != null && idx < plateRankIcons.Length && plateRankIcons[idx] != null)
                {
                    var img = plateRankIcons[idx];
                    if (hasIcon)
                    {
                        img.texture = rankIcons[rank];
                        if (!img.gameObject.activeSelf) { img.gameObject.SetActive(true); }
                    }
                    else
                    {
                        if (img.gameObject.activeSelf) { img.gameObject.SetActive(false); }
                    }
                }
            }

            // ステータス文字（差分時のみ）。
            if (statusChanged)
            {
                plateCachedStatus_[idx] = status;
                if (plateStatusTexts != null && idx < plateStatusTexts.Length && plateStatusTexts[idx] != null)
                {
                    var txt = plateStatusTexts[idx];
                    if (hasStatus)
                    {
                        txt.text = statusLabels_[status];
                        if (!txt.gameObject.activeSelf) { txt.gameObject.SetActive(true); }
                    }
                    else
                    {
                        if (txt.gameObject.activeSelf) { txt.gameObject.SetActive(false); }
                    }
                }
            }

            // 待機中だけ札と差し色を出す。通常時はロゴだけ＝頭上が札で埋まらない。
            // 親（プレート）は上で activeSelf を立ててあるので、ここで子を触ってよい。
            if (statusChanged)
            {
                if (plateBackgrounds != null && idx < plateBackgrounds.Length && plateBackgrounds[idx] != null)
                {
                    var bg = plateBackgrounds[idx];
                    if (bg.activeSelf != hasStatus) { bg.SetActive(hasStatus); }
                }
                if (plateAccents != null && idx < plateAccents.Length && plateAccents[idx] != null)
                {
                    var ac = plateAccents[idx];
                    if (hasStatus && statusAccentColors != null && status < statusAccentColors.Length)
                    {
                        ac.color = statusAccentColors[status];
                    }
                    if (ac.gameObject.activeSelf != hasStatus) { ac.gameObject.SetActive(hasStatus); }
                }
            }

            // スタッフ章（差分時のみ）。段位アイコンの上に出す。
            if (staffChanged)
            {
                plateCachedStaff_[idx] = staffCode;
                if (plateStaffTexts != null && idx < plateStaffTexts.Length && plateStaffTexts[idx] != null)
                {
                    var stf = plateStaffTexts[idx];
                    if (isStaff && stf.text != staffLabel_) { stf.text = staffLabel_; }
                }
                if (plateStaffBadges != null && idx < plateStaffBadges.Length && plateStaffBadges[idx] != null)
                {
                    var badge = plateStaffBadges[idx];
                    if (badge.activeSelf != isStaff) { badge.SetActive(isStaff); }
                }
            }

            // 通常時はロゴを中央、待機表示中だけ左端へ移す。
            // ステータスが変わった時だけ更新し、毎フレームのUI再構築を避ける。
            float iconX = hasStatus ? ICON_WAITING_X : 0f;
            if ((rankChanged || statusChanged) && plateRankIcons != null && idx < plateRankIcons.Length && plateRankIcons[idx] != null)
            {
                var iconRt = plateRankIcons[idx].rectTransform;
                iconRt.anchoredPosition = new Vector2(iconX, 0f);
            }

            // 章はアイコンの真上に貼り付く。アイコンだけ左へ動くと章が置き去りになるので、
            // ステータスが変わった時は章も一緒に運ぶ。
            if ((staffChanged || statusChanged) && plateStaffBadges != null && idx < plateStaffBadges.Length && plateStaffBadges[idx] != null)
            {
                var badgeRt = (RectTransform)plateStaffBadges[idx].transform;
                badgeRt.anchoredPosition = new Vector2(iconX, STAFF_BADGE_Y);
            }
        }

        // 現在割り当て済みのプレートだけ、毎フレーム頭上へ追従させる。
        // プレイヤー列挙・PlayerData・Matchmaking参照はここでは行わない。
        private void UpdatePlateTransforms(Vector3 camPos)
        {
            for (int i = 0; i < poolSize_; ++i)
            {
                if (plateOwnerId_[i] == 0) { continue; }

                var go = plates[i];
                var player = platePlayer_[i];
                if (go == null || player == null || !Utilities.IsValid(player))
                {
                    plateOwnerId_[i] = 0;
                    platePlayer_[i] = null;
                    if (go != null && go.activeSelf) { go.SetActive(false); }
                    continue;
                }

                Vector3 pos = player.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;
                pos.y += headHeightOffset;

                var t = go.transform;
                t.position = pos;

                Vector3 dir = pos - camPos;
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.0001f)
                {
                    t.rotation = Quaternion.LookRotation(dir);
                }
                t.localScale = plateScale;
            }
        }
    }
}
