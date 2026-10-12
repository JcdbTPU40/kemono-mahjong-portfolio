using TMPro;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    // スタッフパネル：段位戦の待機列の開閉
    //
    // 「三麻 半荘戦」限定イベントのように、段位戦の待機列を絞るための操作卓。
    // カウンター裏のスタッフ専用エリアに置く（RankAdminMonitor の真下）。
    //
    // 【同期変数はゼロ】
    //   開閉の状態は MatchmakingManager の enabledColumnMask（同期）が持つ。
    //   ここは「要求を送る」「同期値を読んで描く」だけのローカル UI。
    //
    // 【権限】
    //   表示名 allowlist（StaffOnlyArea）に載っている人にだけパネルを出す。
    //   ただしこれは誤操作防止であって防壁ではないので、実際の受理は
    //   MatchmakingManager の Owner 側が同じ判定でもう一度行う（二重ロック）。
    //
    // 【フリー対局は対象外】
    //   レートが動かないので運営が止める必要がない。列としても持たない。
    //
    // 【イベントルール】
    //   「列の開閉」と「イベントルール」の2ページ。段位戦へ入れてよいルールだけを並べる。
    //   青天井と一局戦のボタンはここに無い。UIに出さないだけでなく MatchmakingManager 側に
    //   同期変数すら持たせていないので、UIを外しても入れる場所が無い。
    //
    // 【なぜ Update を使わないか】
    //   開閉は他のスタッフが操作すると遠隔で変わるので描き直しが要るが、
    //   Udon の Update はビヘイビア単位で固定費が乗る。1秒の遅延イベントで回し、
    //   権限が無い端末では最初の1回で止める（＝一般プレイヤーの負荷はゼロ）。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class StaffEventPanel : UdonSharpBehaviour
    {
        [Header("参照（Build Staff Panel が自動割り当て）")]
        public MatchmakingManager matchmaking;
        [Tooltip("スタッフ表示名の allowlist。判定はここへ一本化する。")]
        public StaffOnlyArea staffArea;
        [Tooltip("パネルの見た目のルート。権限が無い端末では消す。")]
        public GameObject panelRoot;

        [Header("UI")]
        [Tooltip("列ボタンのラベル。0=四麻半荘 / 1=四麻東風 / 2=三麻半荘 / 3=三麻東風。")]
        public TextMeshProUGUI[] columnLabels;
        [Tooltip("操作結果・注意の表示。")]
        public TextMeshProUGUI statusText;

        [Header("ページ")]
        [Tooltip("列の開閉ページのルート。")]
        public GameObject pageColumns;
        [Tooltip("イベントルールページのルート。")]
        public GameObject pageRules;
        [Tooltip("表示ページのルート（頭上スタッフバナー・観戦ルームの防音の切替）。")]
        public GameObject pageDisplay;
        [Tooltip("頭上スタッフバナーの切替ボタンのラベル。")]
        public TextMeshProUGUI staffBadgeLabel;
        [Tooltip("観戦ルームの防音 ON/OFF ボタンのラベル。")]
        public TextMeshProUGUI soundproofLabel;
        [Tooltip("卓順ページのルート（空卓を埋める順番）。")]
        public GameObject pageTables;
        [Tooltip("卓順の行ラベル。0が1番目。8件。")]
        public TextMeshProUGUI[] tableOrderLabels;
        [Tooltip("卓順の行の塗り。0が1番目。8件。使用中は赤くする。")]
        public Image[] tableOrderImages;
        [Tooltip("卓順の行の色（空いている卓）。Build Staff Panel が入れる。")]
        public Color orderIdleColor = new Color(0.16f, 0.27f, 0.43f, 1f);
        [Tooltip("卓順の行の色（使用中＝押せない）。Build Staff Panel が入れる。")]
        public Color orderBusyColor = new Color(0.52f, 0.14f, 0.16f, 1f);
        [Tooltip("卓順の行の色（入れ替え先に選択中）。Build Staff Panel が入れる。")]
        public Color orderSelectedColor = new Color(0.85f, 0.66f, 0.12f, 1f);
        [Tooltip("卓順の行の色（強制リセットの対象に選択中＝次に潰れる卓）。")]
        public Color orderArmedColor = new Color(0.78f, 0.20f, 0.10f, 1f);

        [Header("強制リセット（二段階：モードON → 卓を選ぶ → 実行）")]
        [Tooltip("強制リセットモードの切替ボタンのラベル。")]
        public TextMeshProUGUI forceResetModeLabel;
        [Tooltip("強制リセットの実行ボタンのラベル。武装していない間は押しても効かない。")]
        public TextMeshProUGUI forceResetConfirmLabel;
        [Tooltip("強制リセットの実行ボタンの塗り。武装すると赤くする。")]
        public Image forceResetConfirmImage;
        [Tooltip("実行ボタンの色（未武装＝押しても効かない）。")]
        public Color forceIdleColor = new Color(0.22f, 0.20f, 0.19f, 1f);
        [Tooltip("実行ボタンの色（武装中＝押すと潰れる）。")]
        public Color forceArmedColor = new Color(0.78f, 0.20f, 0.10f, 1f);

        [Tooltip("ルールボタンのラベル。0=赤ドラ 1=ローカル役 2=オープン立直 3=トビ 4=ツモ損 5=能力 6=長考時間。")]
        public TextMeshProUGUI[] ruleLabels;

        private bool resolved_;
        private bool authorized_;
        // 描き直しの間引き。マスクと待機人数の両方が変わらなければ触らない。
        private int shownMask_ = -1;
        private int shownCounts_ = -1;
        // ルール表示の間引き。PackEventRules と同じ詰め方なので1つのintで足りる。
        private int shownRules_ = -1;
        // 頭上バナーの表示状態の間引き。-1=未描画 / 0=非表示 / 1=表示。
        private int shownBadge_ = -1;
        // 防音の表示状態の間引き。-1=未描画 / 0=OFF / 1=ON。
        private int shownSoundproof_ = -1;
        // 卓順の間引き。packed そのものを覚える（-1=未描画）。
        private int shownOrder_ = -1;
        // 使用中の卓の間引き。-1=未描画。
        private int shownBusy_ = -1;
        // 入れ替えのために選んでいる行。-1=未選択。**ローカル状態＝同期しない**
        // （選択は「今この人が触っている途中」でしかなく、他のスタッフに送る意味がない）。
        private int selectedSlot_ = -1;
        private int shownSelected_ = -2;

        // --- 強制リセット（二段階確認） ---
        // 誤爆すると進行中の半荘が消えるので、
        //   ① モードをONにする（通常の入れ替え操作と口を分ける）
        //   ② 潰す卓の行を押して武装する（どの卓かを画面に出す）
        //   ③ 別ボタンの【実行】を押す
        // の3手を要求し、②から15秒で自動的に武装解除する。
        // 全部ローカル状態＝同期しない（他のスタッフの画面に途中経過を出す意味がない）。
        private bool forceMode_ = false;
        private int armedTable_ = -1;      // 卓index。-1=未武装
        private int armedAtMs_ = 0;
        private const int FORCE_ARM_TIMEOUT_MS = 15000;
        private int shownForce_ = -2;      // 描き分けの間引き（-2=未描画）
        private int shownPending_ = -1;

        private void Start()
        {
            Resolve();
        }

        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            // Start の時点で LocalPlayer が立っていない環境があるので、自分の入室でもう一度だけ確定させる。
            if (!resolved_ && Utilities.IsValid(player) && player.isLocal) { Resolve(); }
        }

        private void Resolve()
        {
            VRCPlayerApi local = Networking.LocalPlayer;
            if (!Utilities.IsValid(local)) { return; }
            resolved_ = true;

            authorized_ = staffArea != null && staffArea.IsLocalStaff();
            if (panelRoot != null) { panelRoot.SetActive(authorized_); }
            if (!authorized_) { return; } // 一般プレイヤーはここで止まる（Tickも回さない）

            ShowPageColumns();
            Render();
            SendCustomEventDelayedSeconds(nameof(_StaffTick), 1f);
        }

        public void _StaffTick()
        {
            if (!authorized_) { return; }
            ExpireForceArm();
            Render();
            SendCustomEventDelayedSeconds(nameof(_StaffTick), 1f);
        }

        // ------------------------------------------------------------
        // 列 index ⇔ 定数
        // ------------------------------------------------------------

        private int ColumnBitOf(int index)
        {
            if (index == 0) { return MatchmakingManager.COLUMN_BIT_YONMA_HAN; }
            if (index == 1) { return MatchmakingManager.COLUMN_BIT_YONMA_TON; }
            if (index == 2) { return MatchmakingManager.COLUMN_BIT_SANMA_HAN; }
            if (index == 3) { return MatchmakingManager.COLUMN_BIT_SANMA_TON; }
            return 0;
        }

        private int ColumnStatusOf(int index)
        {
            if (index == 0) { return MatchmakingManager.STATUS_WAIT_YONMA; }
            if (index == 1) { return MatchmakingManager.STATUS_WAIT_YONMA_TONPU; }
            if (index == 2) { return MatchmakingManager.STATUS_WAIT_SANMA; }
            if (index == 3) { return MatchmakingManager.STATUS_WAIT_SANMA_TONPU; }
            return MatchmakingManager.STATUS_IDLE;
        }

        private string ColumnNameOf(int index)
        {
            if (index == 0) { return "四麻 半荘戦"; }
            if (index == 1) { return "四麻 東風戦"; }
            if (index == 2) { return "三麻 半荘戦"; }
            if (index == 3) { return "三麻 東風戦"; }
            return "";
        }

        // ------------------------------------------------------------
        // ボタン（uGUI Button onClick → SendCustomEvent）
        // ------------------------------------------------------------

        public void ToggleColumn0() { ToggleColumn(0); }
        public void ToggleColumn1() { ToggleColumn(1); }
        public void ToggleColumn2() { ToggleColumn(2); }
        public void ToggleColumn3() { ToggleColumn(3); }

        // ------------------------------------------------------------
        // 卓順（空卓を埋める順番）
        //
        // 操作は「行をタップして選ぶ → もう1行タップで入れ替え」。
        // 上へ／下へ方式より押す回数が少なく、離れた行同士も1手で入れ替わる。
        // ------------------------------------------------------------

        public void SelectSlot0() { SelectSlot(0); }
        public void SelectSlot1() { SelectSlot(1); }
        public void SelectSlot2() { SelectSlot(2); }
        public void SelectSlot3() { SelectSlot(3); }
        public void SelectSlot4() { SelectSlot(4); }
        public void SelectSlot5() { SelectSlot(5); }
        public void SelectSlot6() { SelectSlot(6); }
        public void SelectSlot7() { SelectSlot(7); }

        private void SelectSlot(int slot)
        {
            if (!authorized_ || matchmaking == null) { return; }

            // 強制リセットモード中は行タップの意味が変わる（入れ替えではなく「潰す卓を選ぶ」）。
            if (forceMode_) { ArmForceReset(slot); return; }

            // 使用中の卓は動かせない。対局が始まっている卓の順番を入れ替えても
            // その卓が空くわけではなく、並びだけが実態とズレて読めなくなる。
            if (!matchmaking.IsTableFree(matchmaking.GetTableAtOrder(slot)))
            {
                SetStatus("使用中の卓は動かせません。空くまで待ってください。");
                return;
            }

            if (selectedSlot_ < 0)
            {
                selectedSlot_ = slot;
                SetStatus("入れ替え先の行を押してください（同じ行をもう一度押すと取り消し）。");
                RenderOrderNow();
                return;
            }
            if (selectedSlot_ == slot)
            {
                selectedSlot_ = -1;
                SetStatus("選択を取り消しました。");
                RenderOrderNow();
                return;
            }

            int packed = matchmaking.SwapTableOrder(matchmaking.GetTableOrderPacked(), selectedSlot_, slot);
            int from = selectedSlot_;
            selectedSlot_ = -1;
            matchmaking.RequestSetTableOrder(packed);
            SetStatus((from + 1) + "番目と" + (slot + 1) + "番目を入れ替えました。"
                      + "対局中の卓はそのままです。");
            RenderOrderNow();
        }

        // ------------------------------------------------------------
        // 卓の強制リセット（二段階確認）
        //
        // 用途：着席タイムアウトとENTRYが競合して「ENTRYボタンが出ない・STARTも来ない・
        // リセットも効かない」状態で固まった卓を、スタッフがその場から空卓へ戻す。
        // 卓側にも30秒の自動復旧があるが、対局中に固まった卓や、もっと早く開けたい時の手動口。
        //
        // 破壊的な操作（進行中の半荘も消える）なので、必ずモード→選択→実行の3手を踏ませる。
        // ------------------------------------------------------------

        public void ToggleForceResetMode()
        {
            if (!authorized_ || matchmaking == null) { return; }
            forceMode_ = !forceMode_;
            armedTable_ = -1;
            selectedSlot_ = -1;   // 入れ替えの選びかけを持ち越さない（口が変わるので必ず捨てる）
            if (forceMode_)
            {
                SetStatus("強制リセット：潰す卓の行を押してください。\n"
                          + "対局中でも押せます。進行中の半荘は消えます。");
            }
            else
            {
                SetStatus("強制リセットをやめました。行タップは並べ替えに戻ります。");
            }
            RenderOrderNow();
        }

        private void ArmForceReset(int slot)
        {
            int tableIndex = matchmaking.GetTableAtOrder(slot);
            if (tableIndex < 0)
            {
                SetStatus("この行には卓が割り当たっていません。");
                return;
            }

            if (armedTable_ == tableIndex)
            {
                armedTable_ = -1;
                SetStatus("選択を取り消しました。");
                RenderOrderNow();
                return;
            }

            armedTable_ = tableIndex;
            armedAtMs_ = Networking.GetServerTimeInMilliseconds();
            SetStatus("卓 " + (tableIndex + 1) + " を強制リセットします。\n"
                      + "下の【実行】を押すと確定します（15秒で取り消し）。");
            RenderOrderNow();
        }

        public void ExecuteForceReset()
        {
            if (!authorized_ || matchmaking == null) { return; }

            if (!forceMode_ || armedTable_ < 0)
            {
                SetStatus("先に「強制リセット」をONにして、潰す卓の行を押してください。");
                return;
            }

            // サーバー時刻は負値になり得るので、比較は必ず減算で行う（ラップアラウンド安全）。
            if (Networking.GetServerTimeInMilliseconds() - armedAtMs_ > FORCE_ARM_TIMEOUT_MS)
            {
                armedTable_ = -1;
                SetStatus("時間切れです。もう一度、卓の行から選び直してください。");
                RenderOrderNow();
                return;
            }

            int tableIndex = armedTable_;
            matchmaking.RequestForceResetTable(tableIndex);

            // 実行したらモードごと畳む。ONのまま残すと次の行タップが二度目の破壊になる。
            armedTable_ = -1;
            forceMode_ = false;
            SetStatus("卓 " + (tableIndex + 1) + " を強制リセットしました。\n"
                      + "数秒で空卓に戻ります（戻らない場合は卓の近くに人がいるか確認）。");
            RenderOrderNow();
        }

        // 武装の時間切れ。1秒Tickから見る。
        private void ExpireForceArm()
        {
            if (armedTable_ < 0) { return; }
            if (Networking.GetServerTimeInMilliseconds() - armedAtMs_ <= FORCE_ARM_TIMEOUT_MS) { return; }
            armedTable_ = -1;
            SetStatus("強制リセットの選択が時間切れで解除されました。");
            RenderOrderNow();
        }

        public void ResetTableOrder()
        {
            if (!authorized_ || matchmaking == null) { return; }
            if (forceMode_)
            {
                SetStatus("強制リセット中は並べ替えできません。先にOFFにしてください。");
                return;
            }
            selectedSlot_ = -1;
            matchmaking.RequestSetTableOrder(MatchmakingManager.TABLE_ORDER_DEFAULT);
            SetStatus("卓順を (1)→(8) に戻しました。");
            RenderOrderNow();
        }

        // 押した直後は同期値の反映を待たずに描き直したい。差分キャッシュを捨ててから描く。
        private void RenderOrderNow()
        {
            shownOrder_ = -1;
            shownSelected_ = -2;
            shownBusy_ = -1;
            shownForce_ = -2;
            Render();
        }

        // 頭上のスタッフバナーを世界ぜんぶで出す／隠す。
        // 実際に効かせるのは Owner なので、ここは要求を出して描き直すだけ。
        public void ToggleStaffBadge()
        {
            if (!authorized_ || matchmaking == null) { return; }
            bool next = !matchmaking.GetStaffBadgeVisible();
            matchmaking.RequestSetStaffBadgeVisible(next);
            SetStatus(next
                ? "頭上のスタッフバナーを表示にしました。"
                : "頭上のスタッフバナーを非表示にしました。");
            // Owner なら即時反映。Owner でなければ次のTickで追いつく。
            Render();
        }

        // 観戦ルームの防音（ルームの声を外へ漏らさない）を世界ぜんぶで ON/OFF する。
        public void ToggleSoundproof()
        {
            if (!authorized_ || matchmaking == null) { return; }
            bool next = !matchmaking.GetSoundproofOn();
            matchmaking.RequestSetSoundproof(next);
            SetStatus(next
                ? "観戦ルームの防音をONにしました。ルームの声は外へ聞こえません。"
                : "観戦ルームの防音をOFFにしました。ルームの声が外へも聞こえます。");
            Render();
        }

        public void OpenAllColumns()
        {
            if (!authorized_ || matchmaking == null) { return; }
            matchmaking.RequestSetEnabledColumns(MatchmakingManager.COLUMN_MASK_ALL);
            SetStatus("すべての列を公開しました。");
            Render();
        }

        private void ToggleColumn(int index)
        {
            if (!authorized_ || matchmaking == null) { return; }
            int bit = ColumnBitOf(index);
            if (bit == 0) { return; }

            int mask = matchmaking.GetEnabledColumnMask();
            bool open = (mask & bit) != 0;
            int next = open ? (mask & ~bit) : (mask | bit);

            // 全部閉じると段位戦の入口が完全に消える。誤操作で「何も押せない」状態を作らない。
            if (next == 0)
            {
                SetStatus("最後の1列は閉じられません。先に別の列を公開してください。");
                return;
            }

            matchmaking.RequestSetEnabledColumns(next);

            if (open)
            {
                int waiting = matchmaking.GetWaitingCount(ColumnStatusOf(index));
                if (waiting > 0)
                {
                    SetStatus(ColumnNameOf(index) + " を停止しました（待機中の "
                              + waiting + " 人を列から降ろしました）。");
                }
                else
                {
                    SetStatus(ColumnNameOf(index) + " を停止しました。");
                }
            }
            else
            {
                SetStatus(ColumnNameOf(index) + " を公開しました。");
            }
            Render();
        }

        // ------------------------------------------------------------
        // 描画
        // ------------------------------------------------------------

        private void Render()
        {
            RenderRules();
            RenderDisplay();
            RenderTableOrder();
            if (matchmaking == null || columnLabels == null) { return; }

            int mask = matchmaking.GetEnabledColumnMask();
            // 待機人数を1つの整数へ畳んで、変化が無ければ TMP を触らない（Quest対策）。
            int counts = 0;
            for (int i = 0; i < 4; ++i)
            {
                int n = matchmaking.GetWaitingCount(ColumnStatusOf(i));
                if (n > 99) { n = 99; }
                counts = counts * 100 + n;
            }
            if (mask == shownMask_ && counts == shownCounts_) { return; }
            shownMask_ = mask;
            shownCounts_ = counts;

            for (int i = 0; i < columnLabels.Length && i < 4; ++i)
            {
                TextMeshProUGUI label = columnLabels[i];
                if (label == null) { continue; }
                bool open = (mask & ColumnBitOf(i)) != 0;
                int waiting = matchmaking.GetWaitingCount(ColumnStatusOf(i));
                label.text = ColumnNameOf(i) + "\n" + (open ? "公開中" : "停止中")
                             + "（" + waiting + "人待機）";
            }
        }

        // ------------------------------------------------------------
        // ページ切替
        // ------------------------------------------------------------

        public void ShowPageColumns()
        {
            SetPage(0);
            SetStatus("段位戦の待機列を開閉できます。");
        }

        public void ShowPageRules()
        {
            SetPage(1);
            SetStatus("いま成立する段位戦に適用されます（対局中の卓は変わりません）。");
        }

        public void ShowPageDisplay()
        {
            SetPage(2);
            SetStatus("頭上の「スタッフ」バナーと観戦ルームの防音の切替です。全員に効きます。");
        }

        public void ShowPageTables()
        {
            SetPage(3);
            SetStatus("マッチング成立時に上から順に空いている卓へ案内します。\n"
                      + "固まった卓は「強制リセット」で空卓へ戻せます。");
        }

        private void SetPage(int page)
        {
            if (pageColumns != null) { pageColumns.SetActive(page == 0); }
            if (pageRules != null) { pageRules.SetActive(page == 1); }
            if (pageDisplay != null) { pageDisplay.SetActive(page == 2); }
            if (pageTables != null) { pageTables.SetActive(page == 3); }
            // ページを離れたら選択は捨てる（戻ってきた時に選びかけが残っていると誤爆する）。
            if (page != 3 && selectedSlot_ >= 0) { selectedSlot_ = -1; shownSelected_ = -2; }
            // 強制リセットも同じ。ONのまま別ページへ行き、戻ってきて行を押すと誤爆する。
            if (page != 3 && (forceMode_ || armedTable_ >= 0))
            {
                forceMode_ = false;
                armedTable_ = -1;
                shownForce_ = -2;
            }
        }

        // 卓順の行。「k番目 → 卓N」。選択中の行は ▶ を付ける。
        // 卓インデックス i の卓番号は i+1（tables の並びは Wire Matchmaking が番号順に固定）。
        private void RenderTableOrder()
        {
            if (tableOrderLabels == null || matchmaking == null) { return; }
            int packed = matchmaking.GetTableOrderPacked();
            int busy = matchmaking.GetBusyTableMaskForView();
            int pendingMask = 0;
            for (int t = 0; t < 8; ++t)
            {
                if (matchmaking.IsForceResetPending(t)) { pendingMask |= 1 << t; }
            }
            // 強制リセットの状態も間引きの鍵に混ぜる（モードと武装先で見た目が変わるため）。
            int force = forceMode_ ? (armedTable_ + 1) : -1;
            if (packed == shownOrder_ && selectedSlot_ == shownSelected_
                && busy == shownBusy_ && force == shownForce_ && pendingMask == shownPending_) { return; }
            shownOrder_ = packed;
            shownSelected_ = selectedSlot_;
            shownBusy_ = busy;
            shownForce_ = force;
            shownPending_ = pendingMask;

            for (int k = 0; k < tableOrderLabels.Length && k < 8; ++k)
            {
                int tableIndex = matchmaking.GetTableAtOrder(k);
                bool isBusy = (busy & (1 << tableIndex)) != 0;
                bool isArmed = forceMode_ && armedTable_ == tableIndex;
                bool pending = (pendingMask & (1 << tableIndex)) != 0;

                var label = tableOrderLabels[k];
                if (label != null)
                {
                    string head = isArmed ? "⚠ " : ((!forceMode_ && k == selectedSlot_) ? "▶ " : "　");
                    string tail = pending ? "　リセット中" : (isBusy ? "　使用中" : "");
                    label.text = head + (k + 1) + "番目　　卓 " + (tableIndex + 1) + tail;
                }

                // 使用中は赤。押しても動かないことを色で先に見せる（押してから断るのは遅い）。
                // 強制リセットモード中は「使用中でも押せる」ので、赤は武装先だけに使う。
                if (tableOrderImages != null && k < tableOrderImages.Length && tableOrderImages[k] != null)
                {
                    var img = tableOrderImages[k];
                    if (isArmed) { img.color = orderArmedColor; }
                    else if (forceMode_) { img.color = orderIdleColor; }
                    else if (isBusy) { img.color = orderBusyColor; }
                    else if (k == selectedSlot_) { img.color = orderSelectedColor; }
                    else { img.color = orderIdleColor; }
                }
            }

            if (forceResetModeLabel != null)
            {
                forceResetModeLabel.text = forceMode_ ? "強制リセット：ON" : "強制リセット：OFF";
            }
            if (forceResetConfirmLabel != null)
            {
                forceResetConfirmLabel.text = (forceMode_ && armedTable_ >= 0)
                    ? "⚠ 実行：卓 " + (armedTable_ + 1) + " を空にする"
                    : "実行（先に卓を選ぶ）";
            }
            if (forceResetConfirmImage != null)
            {
                forceResetConfirmImage.color = (forceMode_ && armedTable_ >= 0)
                    ? forceArmedColor : forceIdleColor;
            }
        }

        // 頭上バナーの現在状態。ボタンのラベルは「今どうなっているか」を出す
        // （押した先の状態を書くと、他のスタッフが操作した時に意味が反転して読めない）。
        private void RenderDisplay()
        {
            if (matchmaking == null) { return; }
            int sp = matchmaking.GetSoundproofOn() ? 1 : 0;
            if (soundproofLabel != null && sp != shownSoundproof_)
            {
                shownSoundproof_ = sp;
                soundproofLabel.text = "観戦ルームの防音：" + (sp != 0 ? "ON" : "OFF");
            }
            if (staffBadgeLabel == null) { return; }
            int now = matchmaking.GetStaffBadgeVisible() ? 1 : 0;
            if (now == shownBadge_) { return; }
            shownBadge_ = now;
            staffBadgeLabel.text = "頭上のスタッフ表示：" + (now != 0 ? "表示中" : "非表示");
        }

        // ------------------------------------------------------------
        // イベントルール
        //
        // 送信は「今の同期値を読んで1つだけ変えて詰め直す」。差分を持たないので、
        // 別のスタッフが同時に触っても取りこぼしが最後の1回に収束する。
        // ------------------------------------------------------------

        private int PackCurrent(int redDora, bool localYaku, bool openReach,
            bool tsumoLoss, bool bust, bool abilityOn, int timer)
        {
            return matchmaking.PackEventRules(redDora, localYaku, openReach,
                tsumoLoss, bust, abilityOn, timer);
        }

        private void SendRules(int packed)
        {
            if (!authorized_ || matchmaking == null) { return; }
            matchmaking.RequestSetEventRules(packed);
            // Owner なら即時に反映されるので描き直す。Owner でなければ次のTickで追いつく。
            Render();
        }

        public void CycleRedDora()
        {
            if (!authorized_ || matchmaking == null) { return; }
            int next = matchmaking.GetEventRedDoraMode() + 1;
            if (next > MatchmakingManager.RED_DORA_MODE_ALL) { next = MatchmakingManager.RED_DORA_MODE_NONE; }
            SendRules(PackCurrent(next, matchmaking.GetEventLocalYaku(), matchmaking.GetEventOpenReach(),
                matchmaking.GetEventTsumoLoss(), matchmaking.GetEventBust(),
                matchmaking.GetEventAbilityOn(), matchmaking.GetEventTurnTimerMode()));
        }

        public void ToggleLocalYaku()
        {
            if (!authorized_ || matchmaking == null) { return; }
            SendRules(PackCurrent(matchmaking.GetEventRedDoraMode(), !matchmaking.GetEventLocalYaku(),
                matchmaking.GetEventOpenReach(), matchmaking.GetEventTsumoLoss(),
                matchmaking.GetEventBust(), matchmaking.GetEventAbilityOn(),
                matchmaking.GetEventTurnTimerMode()));
        }

        public void ToggleOpenReach()
        {
            if (!authorized_ || matchmaking == null) { return; }
            SendRules(PackCurrent(matchmaking.GetEventRedDoraMode(), matchmaking.GetEventLocalYaku(),
                !matchmaking.GetEventOpenReach(), matchmaking.GetEventTsumoLoss(),
                matchmaking.GetEventBust(), matchmaking.GetEventAbilityOn(),
                matchmaking.GetEventTurnTimerMode()));
        }

        public void ToggleBust()
        {
            if (!authorized_ || matchmaking == null) { return; }
            SendRules(PackCurrent(matchmaking.GetEventRedDoraMode(), matchmaking.GetEventLocalYaku(),
                matchmaking.GetEventOpenReach(), matchmaking.GetEventTsumoLoss(),
                !matchmaking.GetEventBust(), matchmaking.GetEventAbilityOn(),
                matchmaking.GetEventTurnTimerMode()));
        }

        public void ToggleTsumoLoss()
        {
            if (!authorized_ || matchmaking == null) { return; }
            SendRules(PackCurrent(matchmaking.GetEventRedDoraMode(), matchmaking.GetEventLocalYaku(),
                matchmaking.GetEventOpenReach(), !matchmaking.GetEventTsumoLoss(),
                matchmaking.GetEventBust(), matchmaking.GetEventAbilityOn(),
                matchmaking.GetEventTurnTimerMode()));
        }

        public void ToggleAbility()
        {
            if (!authorized_ || matchmaking == null) { return; }
            SendRules(PackCurrent(matchmaking.GetEventRedDoraMode(), matchmaking.GetEventLocalYaku(),
                matchmaking.GetEventOpenReach(), matchmaking.GetEventTsumoLoss(),
                matchmaking.GetEventBust(), !matchmaking.GetEventAbilityOn(),
                matchmaking.GetEventTurnTimerMode()));
        }

        public void CycleTurnTimer()
        {
            if (!authorized_ || matchmaking == null) { return; }
            int next = matchmaking.GetEventTurnTimerMode() + 1;
            if (next > MatchmakingManager.TURN_TIMER_60_255) { next = MatchmakingManager.TURN_TIMER_8_30; }
            SendRules(PackCurrent(matchmaking.GetEventRedDoraMode(), matchmaking.GetEventLocalYaku(),
                matchmaking.GetEventOpenReach(), matchmaking.GetEventTsumoLoss(),
                matchmaking.GetEventBust(), matchmaking.GetEventAbilityOn(), next));
        }

        public void ResetEventRules()
        {
            if (!authorized_ || matchmaking == null) { return; }
            SendRules(PackCurrent(MatchmakingManager.RED_DORA_MODE_THREE, false, false,
                true, true, true, MatchmakingManager.TURN_TIMER_8_30));
            SetStatus("標準ルールに戻しました。");
        }

        private string RedDoraName(int mode)
        {
            if (mode == MatchmakingManager.RED_DORA_MODE_NONE) { return "なし"; }
            if (mode == MatchmakingManager.RED_DORA_MODE_FOUR) { return "4枚"; }
            if (mode == MatchmakingManager.RED_DORA_MODE_ALL) { return "全赤"; }
            return "3枚";
        }

        private string TurnTimerName(int mode)
        {
            if (mode == MatchmakingManager.TURN_TIMER_15_60) { return "15+60"; }
            if (mode == MatchmakingManager.TURN_TIMER_60_255) { return "60+255"; }
            return "8+30";
        }

        private void RenderRules()
        {
            if (ruleLabels == null || matchmaking == null) { return; }

            int packed = matchmaking.PackEventRules(
                matchmaking.GetEventRedDoraMode(), matchmaking.GetEventLocalYaku(),
                matchmaking.GetEventOpenReach(), matchmaking.GetEventTsumoLoss(),
                matchmaking.GetEventBust(), matchmaking.GetEventAbilityOn(),
                matchmaking.GetEventTurnTimerMode());
            if (packed == shownRules_) { return; }
            shownRules_ = packed;

            SetRuleLabel(0, "赤ドラ：" + RedDoraName(matchmaking.GetEventRedDoraMode()));
            SetRuleLabel(1, "ローカル役：" + (matchmaking.GetEventLocalYaku() ? "あり" : "なし"));
            SetRuleLabel(2, "オープン立直：" + (matchmaking.GetEventOpenReach() ? "あり" : "なし"));
            SetRuleLabel(3, "トビ：" + (matchmaking.GetEventBust() ? "あり" : "なし"));
            SetRuleLabel(4, "ツモ損：" + (matchmaking.GetEventTsumoLoss() ? "あり" : "なし"));
            SetRuleLabel(5, "能力：" + (matchmaking.GetEventAbilityOn() ? "あり" : "なし"));
            SetRuleLabel(6, "長考時間：" + TurnTimerName(matchmaking.GetEventTurnTimerMode()));
        }

        private void SetRuleLabel(int i, string value)
        {
            if (ruleLabels == null || i >= ruleLabels.Length) { return; }
            if (ruleLabels[i] != null) { ruleLabels[i].text = value; }
        }

        private void SetStatus(string value)
        {
            if (statusText != null) { statusText.text = value; }
        }
    }
}
