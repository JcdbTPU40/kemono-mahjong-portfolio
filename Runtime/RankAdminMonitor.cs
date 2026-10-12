using TMPro;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace SuzuFactory.AozoraMahjong
{
    // PlayerDataは本人だけが書けるため、運営は補正依頼を同期し、対象本人の承認後に本人端末で保存する。
    // 表示名allowlistは誤操作防止用。最終的な安全境界は対象本人の確認操作である。
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class RankAdminMonitor : UdonSharpBehaviour
    {
        public const string AUTHORIZED_PRIMARY_NAME = "<STAFF_NAME_1>";
        public const string AUTHORIZED_STAFF_NAME = "<STAFF_NAME_2>";

        [Header("Data")]
        public PlayerStatsStore statsStore;

        [Header("Panels")]
        public GameObject adminPanel;
        public GameObject confirmationPanel;

        [Header("Admin UI")]
        public TextMeshProUGUI targetText;
        public TextMeshProUGUI currentRankText;
        public TextMeshProUGUI draftRankText;
        public TMP_InputField rankPointInput;
        public TMP_InputField tenjuOrbX10Input;
        public TextMeshProUGUI adminStatusText;

        [Header("Target confirmation UI")]
        public TextMeshProUGUI confirmationText;
        public TextMeshProUGUI confirmationStatusText;

        [UdonSynced] private int requestTargetPlayerId_;
        [UdonSynced] private string requestTargetName_;
        [UdonSynced] private int requestOperatorPlayerId_;
        [UdonSynced] private string requestOperatorName_;
        [UdonSynced] private int requestRankLv_;
        [UdonSynced] private int requestRankPt_;
        [UdonSynced] private int requestTenjuOrbX10_;
        [UdonSynced] private int requestSerial_;

        private VRCPlayerApi[] playerBuffer_ = new VRCPlayerApi[100];
        private int selectedPlayerId_;
        private int draftRankLv_;
        private int draftRankPt_;
        private int draftTenjuOrbX10_ = PlayerStatsStore.TENJU_ORB_INITIAL_X10;

        private bool pendingConfirmation_;
        private int pendingOperatorPlayerId_;
        private string pendingOperatorName_;
        private int pendingRankLv_;
        private int pendingRankPt_;
        private int pendingTenjuOrbX10_;
        private int pendingSerial_;
        private int lastSeenOperatorPlayerId_;
        private int lastSeenSerial_;

        private void Start()
        {
            VRCPlayerApi local = Networking.LocalPlayer;
            bool authorized = IsValidPlayer(local) && IsAuthorizedDisplayName(local.displayName);
            if (adminPanel != null) { adminPanel.SetActive(authorized); }
            if (confirmationPanel != null) { confirmationPanel.SetActive(false); }

            if (authorized)
            {
                SelectInitialTarget();
            }
            ProcessIncomingRequest();
        }

        public bool IsAuthorizedDisplayName(string displayName)
        {
            return displayName == AUTHORIZED_PRIMARY_NAME || displayName == AUTHORIZED_STAFF_NAME;
        }

        public override bool OnOwnershipRequest(VRCPlayerApi requestingPlayer, VRCPlayerApi requestedOwner)
        {
            return IsValidPlayer(requestedOwner) && IsAuthorizedDisplayName(requestedOwner.displayName);
        }

        public override void OnDeserialization()
        {
            ProcessIncomingRequest();
            RefreshSelectedTargetDisplay();
        }

        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            RefreshSelectedTargetDisplay();
        }

        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            if (player != null && player.playerId == selectedPlayerId_)
            {
                selectedPlayerId_ = 0;
                SelectInitialTarget();
            }
            else
            {
                RefreshSelectedTargetDisplay();
            }

            if (pendingConfirmation_ && player != null && player.playerId == pendingOperatorPlayerId_)
            {
                SetConfirmationStatus("依頼した運営が退出したため適用できません。拒否してください。");
            }
        }

        public void TargetPrev() { CycleTarget(-1); }
        public void TargetNext() { CycleTarget(1); }

        public void RankPrev()
        {
            if (!IsLocalAuthorized()) { return; }
            --draftRankLv_;
            if (draftRankLv_ < PlayerStatsStore.RANK_MIN_LV) { draftRankLv_ = PlayerStatsStore.RANK_MAX_LV; }
            ResetValueForDraftRank();
            RenderDraft();
        }

        public void RankNext()
        {
            if (!IsLocalAuthorized()) { return; }
            ++draftRankLv_;
            if (draftRankLv_ > PlayerStatsStore.RANK_MAX_LV) { draftRankLv_ = PlayerStatsStore.RANK_MIN_LV; }
            ResetValueForDraftRank();
            RenderDraft();
        }

        public void LoadSelected()
        {
            if (!IsLocalAuthorized()) { return; }
            VRCPlayerApi target = FindPlayerById(selectedPlayerId_);
            if (!IsValidPlayer(target))
            {
                SetAdminStatus("対象プレイヤーが見つかりません。");
                SelectInitialTarget();
                return;
            }

            draftRankLv_ = statsStore != null ? statsStore.GetRankLvOf(target) : 0;
            draftRankPt_ = statsStore != null ? statsStore.GetRankPtOf(target) : 0;
            draftTenjuOrbX10_ = statsStore != null
                ? statsStore.GetTenjuOrbX10Of(target)
                : PlayerStatsStore.TENJU_ORB_INITIAL_X10;
            RenderDraft();
            SetAdminStatus("現在値を読み込みました。PlayerData復元前は初期値に見える場合があります。");
        }

        public void SendCorrection()
        {
            VRCPlayerApi local = Networking.LocalPlayer;
            if (!IsValidPlayer(local) || !IsAuthorizedDisplayName(local.displayName)) { return; }
            if (statsStore == null)
            {
                SetAdminStatus("PlayerStatsStoreが未設定です。");
                return;
            }

            VRCPlayerApi target = FindPlayerById(selectedPlayerId_);
            if (!IsValidPlayer(target))
            {
                SetAdminStatus("対象プレイヤーが同じインスタンスにいません。");
                return;
            }

            ReadAndSanitizeInputs();
            Networking.SetOwner(local, gameObject);
            VRCPlayerApi owner = Networking.GetOwner(gameObject);
            if (!IsValidPlayer(owner) || owner.playerId != local.playerId)
            {
                SetAdminStatus("補正依頼オブジェクトのOwner取得に失敗しました。");
                return;
            }

            requestTargetPlayerId_ = target.playerId;
            requestTargetName_ = target.displayName;
            requestOperatorPlayerId_ = local.playerId;
            requestOperatorName_ = local.displayName;
            requestRankLv_ = draftRankLv_;
            requestRankPt_ = draftRankPt_;
            requestTenjuOrbX10_ = draftTenjuOrbX10_;
            if (requestSerial_ >= 2147483647) { requestSerial_ = 1; }
            else
            {
                ++requestSerial_;
                if (requestSerial_ <= 0) { requestSerial_ = 1; }
            }

            RequestSerialization();
            SetAdminStatus("補正依頼を送信しました。対象本人の承認待ちです。");

            // 自分自身を対象にした場合、OwnerではOnDeserializationが呼ばれないため直接処理する。
            ProcessIncomingRequest();
        }

        public void AcceptCorrection()
        {
            if (!pendingConfirmation_) { return; }
            VRCPlayerApi local = Networking.LocalPlayer;
            if (!IsValidPlayer(local) || local.playerId != requestTargetPlayerId_ || local.displayName != requestTargetName_)
            {
                RejectCorrection();
                return;
            }
            if (!IsPendingRequestStillCurrent())
            {
                SetConfirmationStatus("依頼者を再確認できないため適用しません。拒否してください。");
                return;
            }
            if (statsStore == null || !statsStore.IsLocalPlayerDataRestored())
            {
                SetConfirmationStatus("PlayerData復元前です。復元後にもう一度「適用する」を押してください。");
                return;
            }

            bool applied = statsStore.ApplyAdminRankCorrection(
                pendingRankLv_, pendingRankPt_, pendingTenjuOrbX10_, pendingOperatorName_);
            if (!applied)
            {
                SetConfirmationStatus("保存できませんでした。PlayerData復元後に再試行してください。");
                return;
            }

            pendingConfirmation_ = false;
            if (confirmationPanel != null) { confirmationPanel.SetActive(false); }
            if (IsLocalAuthorized())
            {
                RefreshSelectedTargetDisplay();
                SetAdminStatus("本人の承認により段位補正を保存しました。");
            }
        }

        public void RejectCorrection()
        {
            pendingConfirmation_ = false;
            if (confirmationPanel != null) { confirmationPanel.SetActive(false); }
            SetConfirmationStatus("");
            if (IsLocalAuthorized()) { SetAdminStatus("補正依頼を拒否しました。"); }
        }

        private void ProcessIncomingRequest()
        {
            VRCPlayerApi local = Networking.LocalPlayer;
            if (!IsValidPlayer(local)) { return; }
            if (requestSerial_ <= 0 || requestTargetPlayerId_ != local.playerId || requestTargetName_ != local.displayName) { return; }
            if (requestOperatorPlayerId_ == lastSeenOperatorPlayerId_ && requestSerial_ == lastSeenSerial_) { return; }
            // 確認待ちの最中に届いた新しい依頼もここで受け直す（表示中の旧依頼と番号を比べて弾かない）。
            if (!IsRequestSenderTrusted()) { return; }

            lastSeenOperatorPlayerId_ = requestOperatorPlayerId_;
            lastSeenSerial_ = requestSerial_;
            pendingOperatorPlayerId_ = requestOperatorPlayerId_;
            pendingOperatorName_ = requestOperatorName_;
            pendingRankLv_ = statsStore != null ? statsStore.SanitizeRankLv(requestRankLv_) : requestRankLv_;
            pendingRankPt_ = statsStore != null ? statsStore.SanitizeRankPtForLv(pendingRankLv_, requestRankPt_) : requestRankPt_;
            pendingTenjuOrbX10_ = statsStore != null
                ? statsStore.SanitizeTenjuOrbX10(requestTenjuOrbX10_)
                : requestTenjuOrbX10_;
            pendingSerial_ = requestSerial_;
            pendingConfirmation_ = true;

            string before = statsStore != null ? statsStore.GetRankDisplayText() : "（現在値を取得できません）";
            string after = statsStore != null
                ? statsStore.GetRankDisplayTextForValues(pendingRankLv_, pendingRankPt_, pendingTenjuOrbX10_)
                : "（補正値を表示できません）";
            if (confirmationText != null)
            {
                confirmationText.text = "運営「" + pendingOperatorName_ + "」から段位補正の依頼です。\n\n現在: "
                    + before + "\n変更後: " + after + "\n\n内容を確認し、同意する場合だけ適用してください。";
            }
            SetConfirmationStatus(statsStore != null && statsStore.IsLocalPlayerDataRestored()
                ? "対局中は適用しないでください。"
                : "PlayerData復元待ちです。復元後に適用してください。");
            if (confirmationPanel != null) { confirmationPanel.SetActive(true); }
        }

        // 受信時：同期に載っている依頼の送り主（＝Owner）が、名乗った運営本人で、許可名簿に居るか。
        private bool IsRequestSenderTrusted()
        {
            VRCPlayerApi owner = Networking.GetOwner(gameObject);
            if (!IsValidPlayer(owner)) { return false; }
            if (owner.playerId != requestOperatorPlayerId_ || owner.displayName != requestOperatorName_) { return false; }
            if (!IsAuthorizedDisplayName(owner.displayName)) { return false; }
            return true;
        }

        // 承認時：画面に出している依頼が、いま同期に載っている依頼そのもので、送り主も信頼できるか。
        // 依頼が差し替わっていれば受信側で画面も差し替わるので、古い画面の承認だけを弾けばよい。
        private bool IsPendingRequestStillCurrent()
        {
            if (!IsRequestSenderTrusted()) { return false; }
            if (pendingSerial_ != requestSerial_) { return false; }
            if (pendingOperatorPlayerId_ != requestOperatorPlayerId_) { return false; }
            return true;
        }

        private void SelectInitialTarget()
        {
            int count = FillPlayerBuffer();
            if (count <= 0)
            {
                selectedPlayerId_ = 0;
                RefreshSelectedTargetDisplay();
                return;
            }
            selectedPlayerId_ = playerBuffer_[0].playerId;
            LoadSelected();
        }

        private void CycleTarget(int direction)
        {
            if (!IsLocalAuthorized()) { return; }
            int count = FillPlayerBuffer();
            if (count <= 0)
            {
                selectedPlayerId_ = 0;
                RefreshSelectedTargetDisplay();
                return;
            }

            int index = 0;
            for (int i = 0; i < count; ++i)
            {
                if (playerBuffer_[i] != null && playerBuffer_[i].playerId == selectedPlayerId_)
                {
                    index = i;
                    break;
                }
            }
            index += direction;
            if (index < 0) { index = count - 1; }
            if (index >= count) { index = 0; }
            selectedPlayerId_ = playerBuffer_[index].playerId;
            LoadSelected();
        }

        private int FillPlayerBuffer()
        {
            int count = VRCPlayerApi.GetPlayerCount();
            if (count > playerBuffer_.Length) { count = playerBuffer_.Length; }
            VRCPlayerApi.GetPlayers(playerBuffer_);
            return count;
        }

        private VRCPlayerApi FindPlayerById(int playerId)
        {
            int count = FillPlayerBuffer();
            for (int i = 0; i < count; ++i)
            {
                VRCPlayerApi player = playerBuffer_[i];
                if (IsValidPlayer(player) && player.playerId == playerId) { return player; }
            }
            return null;
        }

        private void RefreshSelectedTargetDisplay()
        {
            if (!IsLocalAuthorized()) { return; }
            VRCPlayerApi target = FindPlayerById(selectedPlayerId_);
            if (!IsValidPlayer(target))
            {
                if (targetText != null) { targetText.text = "対象: （未選択）"; }
                if (currentRankText != null) { currentRankText.text = "現在: --"; }
                return;
            }

            if (targetText != null) { targetText.text = "対象: " + target.displayName + " [" + target.playerId.ToString() + "]"; }
            if (currentRankText != null)
            {
                currentRankText.text = statsStore != null
                    ? "現在: " + statsStore.GetRankDisplayTextForValues(
                        statsStore.GetRankLvOf(target), statsStore.GetRankPtOf(target), statsStore.GetTenjuOrbX10Of(target))
                    : "現在: --";
            }
        }

        private void ResetValueForDraftRank()
        {
            if (statsStore == null) { return; }
            draftRankLv_ = statsStore.SanitizeRankLv(draftRankLv_);
            if (draftRankLv_ >= PlayerStatsStore.RANK_TENJU_FIRST_LV)
            {
                draftRankPt_ = 0;
                draftTenjuOrbX10_ = PlayerStatsStore.TENJU_ORB_INITIAL_X10;
            }
            else
            {
                draftRankPt_ = statsStore.GetInitialRankPtForLv(draftRankLv_);
                draftTenjuOrbX10_ = PlayerStatsStore.TENJU_ORB_INITIAL_X10;
            }
        }

        private void ReadAndSanitizeInputs()
        {
            int parsed;
            if (rankPointInput != null && int.TryParse(rankPointInput.text, out parsed)) { draftRankPt_ = parsed; }
            if (tenjuOrbX10Input != null && int.TryParse(tenjuOrbX10Input.text, out parsed)) { draftTenjuOrbX10_ = parsed; }
            if (statsStore != null)
            {
                draftRankLv_ = statsStore.SanitizeRankLv(draftRankLv_);
                draftRankPt_ = statsStore.SanitizeRankPtForLv(draftRankLv_, draftRankPt_);
                draftTenjuOrbX10_ = statsStore.SanitizeTenjuOrbX10(draftTenjuOrbX10_);
            }
            RenderDraft();
        }

        private void RenderDraft()
        {
            RefreshSelectedTargetDisplay();
            if (statsStore != null)
            {
                draftRankLv_ = statsStore.SanitizeRankLv(draftRankLv_);
                draftRankPt_ = statsStore.SanitizeRankPtForLv(draftRankLv_, draftRankPt_);
                draftTenjuOrbX10_ = statsStore.SanitizeTenjuOrbX10(draftTenjuOrbX10_);
                if (draftRankText != null)
                {
                    draftRankText.text = "変更後: " + statsStore.GetRankDisplayTextForValues(
                        draftRankLv_, draftRankPt_, draftTenjuOrbX10_);
                }
            }
            if (rankPointInput != null) { rankPointInput.text = draftRankPt_.ToString(); }
            if (tenjuOrbX10Input != null) { tenjuOrbX10Input.text = draftTenjuOrbX10_.ToString(); }
        }

        private bool IsLocalAuthorized()
        {
            VRCPlayerApi local = Networking.LocalPlayer;
            return IsValidPlayer(local) && IsAuthorizedDisplayName(local.displayName);
        }

        private bool IsValidPlayer(VRCPlayerApi player)
        {
            return player != null && Utilities.IsValid(player);
        }

        private void SetAdminStatus(string value)
        {
            if (adminStatusText != null) { adminStatusText.text = value; }
        }

        private void SetConfirmationStatus(string value)
        {
            if (confirmationStatusText != null) { confirmationStatusText.text = value; }
        }
    }
}
