using UdonSharp;
using UnityEngine;
using VRC.SDK3.Components;
using VRC.SDKBase;

namespace SuzuFactory.AozoraMahjong
{
    /// <summary>
    /// スタッフ用マイク（持っている間だけ、その人の声がワールド中に同じ大きさで届く）。
    /// 声の設定そのものは TableVoiceRouter が各自の端末で掛ける（ここは「誰が持っているか」を同期するだけ）。
    ///
    /// - 持てるのはスタッフ（MatchmakingManager の名簿）だけ。それ以外の人には pickupable を落とし、
    ///   万一掴めても即座に手放させる。受け側も名簿で検査するので、同期を偽装されても拡声はしない。
    /// - 手放したら受付の定位置へ戻す（VRCObjectSync.Respawn）。
    /// </summary>
    // VRCObjectSync と同じ GameObject の Udon は Manual 同期にできない（SDK の制約：ビルド前チェックで警告が出る）。
    // 持ち主（掴んでいる人）が holderId_ を書き、ObjectSync と一緒に連続同期で届く
    [UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
    public class KemonoStaffMic : UdonSharpBehaviour
    {
        public MatchmakingManager matchmaking;
        public VRCPickup pickup;
        public VRCObjectSync objectSync;
        [Tooltip("スタッフ名簿を読み直す間隔（秒）。名簿は入場直後に読めないことがある")]
        public float staffCheckInterval = 5f;

        [UdonSynced(UdonSyncMode.None)] private int holderId_ = 0;

        private void Start()
        {
            _CheckStaff();
        }

        // 持っている人の playerId（0＝誰も持っていない）
        public int GetHolderId() { return holderId_; }

        private bool IsLocalStaff()
        {
            return matchmaking != null && matchmaking.IsLocalStaff();
        }

        public void _CheckStaff()
        {
            if (pickup != null)
            {
                bool staff = IsLocalStaff();
                if (pickup.pickupable != staff) { pickup.pickupable = staff; }
            }
            SendCustomEventDelayedSeconds(nameof(_CheckStaff), staffCheckInterval);
        }

        public override void OnPickup()
        {
            var lp = Networking.LocalPlayer;
            if (lp == null || !IsLocalStaff())
            {
                if (pickup != null) { pickup.Drop(); }
                return;
            }
            if (!Networking.IsOwner(lp, gameObject)) { Networking.SetOwner(lp, gameObject); }
            holderId_ = lp.playerId;
        }

        public override void OnDrop()
        {
            if (!Networking.IsOwner(gameObject)) { return; }
            if (holderId_ == 0) { return; }
            holderId_ = 0;
            if (objectSync != null) { objectSync.Respawn(); }
        }

        // 持っていた人が抜けて持ち主が移ったら、誰も持っていない状態へ戻す
        public override void OnOwnershipTransferred(VRCPlayerApi player)
        {
            if (!Networking.IsOwner(gameObject) || holderId_ == 0) { return; }
            var holder = VRCPlayerApi.GetPlayerById(holderId_);
            if (holder != null && Utilities.IsValid(holder)) { return; }
            holderId_ = 0;
            if (objectSync != null) { objectSync.Respawn(); }
        }
    }
}
