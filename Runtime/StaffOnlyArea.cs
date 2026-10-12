using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace SuzuFactory.AozoraMahjong
{
    // カウンター裏のスタッフ専用エリア。見えない壁を「スタッフ以外の端末でだけ」有効にする。
    //
    // 【なぜ同期変数がゼロで済むか】
    //   コライダーは各クライアントがローカルに持つ資源で、自分の端末で消しても
    //   他人の端末の壁は消えない。だから「自分がスタッフなら自分の壁だけ消す」で完結する。
    //   同期もOwner移譲も不要＝Quest基準でも負荷ゼロ。
    //
    // 【これは鍵ではない】
    //   VRChatはサーバー権威が無いので、改造クライアントは壁を無視して入れる。
    //   ここでの目的は「一般プレイヤーの迷い込み・悪ノリの抑止」であって機密の防護ではない。
    //   本当に触られたくない操作（段位補正など）は RankAdminMonitor 側の
    //   表示名allowlist＋対象本人の承認で守る。この壁はその手前の一次仕切り。
    //
    // 【表示名で判定する理由】
    //   Udonから安定して読める本人性の手がかりは表示名しか無い（PlayerDataは本人しか書けない、
    //   playerIdは毎インスタンスで変わる）。VRChatの表示名は同時に2人存在しないので、
    //   誤操作防止のallowlistとしては十分に働く。
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class StaffOnlyArea : UdonSharpBehaviour
    {
        [Header("スタッフの表示名（VRChat Display Name・完全一致／大小文字は無視）")]
        public string[] staffDisplayNames;

        [Header("スタッフ以外の端末でだけ有効にする見えない壁")]
        public GameObject[] barriers;

        [Header("インスタンスマスターも通す（イベント運用の保険）")]
        public bool allowInstanceMaster;

        // ---- 入室通知音（スタッフの端末でだけローカルに鳴る・同期ゼロ）----
        //   スタッフが入室者を案内するための合図。判定も再生も完全にローカルなので、
        //   スタッフ以外の端末では AudioSource が一度も鳴らない。
        //   ソースは 2D にすること。卓や2階にいても聞こえないと案内の役に立たない。
        [Header("入室通知音（スタッフの端末でのみローカル再生）")]
        [Tooltip("2D想定。Build Staff Only Area が生成し、Normalize Audio が音量を入れる。")]
        public AudioSource joinSource;
        public AudioClip joinClip;
        [Range(0.0f, 1.0f)] public float joinVolume = 0.12f;
        [Tooltip("ワールド読み込み直後に入室通知を無視する秒数。自分の入室時は" +
                 "「既にいる全員ぶん」の OnPlayerJoined が一斉に飛ぶため、" +
                 "これが無いと人数ぶん音が重なって鳴る。")]
        public float joinArmSeconds = 5.0f;

        private bool resolved_;
        private float joinArmAt_;

        private void Start()
        {
            joinArmAt_ = Time.time + joinArmSeconds;
            Apply();
        }

        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            // Startの時点でLocalPlayerがまだ立っていない環境があるので、自分の入室でもう一度だけ確定させる。
            if (!resolved_ && Utilities.IsValid(player) && player.isLocal) { Apply(); }
            PlayJoinChime(player);
        }

        // 入室通知音。スタッフ以外・自分自身・読み込み直後の一斉発火は鳴らさない。
        private void PlayJoinChime(VRCPlayerApi player)
        {
            if (joinSource == null || joinClip == null) { return; }
            if (!Utilities.IsValid(player) || player.isLocal) { return; }
            if (Time.time < joinArmAt_) { return; }
            if (!IsLocalStaff()) { return; }
            joinSource.PlayOneShot(joinClip, joinVolume);
        }

        public void Apply()
        {
            VRCPlayerApi local = Networking.LocalPlayer;
            if (!Utilities.IsValid(local)) { return; }
            resolved_ = true;
            SetBarriersActive(!IsStaff(local));
        }

        public bool IsLocalStaff()
        {
            VRCPlayerApi local = Networking.LocalPlayer;
            return Utilities.IsValid(local) && IsStaff(local);
        }

        private bool IsStaff(VRCPlayerApi player)
        {
            if (allowInstanceMaster && player.isMaster) { return true; }
            return IsStaffName(player.displayName);
        }

        public bool IsStaffName(string displayName)
        {
            if (staffDisplayNames == null) { return false; }
            if (displayName == null || displayName.Length == 0) { return false; }
            string lowered = displayName.ToLower();
            for (int i = 0; i < staffDisplayNames.Length; i++)
            {
                string entry = staffDisplayNames[i];
                if (entry == null || entry.Length == 0) { continue; }
                if (entry.ToLower() == lowered) { return true; }
            }
            return false;
        }

        private void SetBarriersActive(bool active)
        {
            if (barriers == null) { return; }
            for (int i = 0; i < barriers.Length; i++)
            {
                GameObject go = barriers[i];
                if (go == null) { continue; }
                if (go.activeSelf != active) { go.SetActive(active); }
            }
        }
    }
}
