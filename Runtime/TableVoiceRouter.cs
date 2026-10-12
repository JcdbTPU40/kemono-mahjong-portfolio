using UdonSharp;
using UnityEngine;
using VRC.SDK3.Data;
using VRC.SDKBase;

namespace SuzuFactory.AozoraMahjong
{
    /// <summary>
    /// マイクの声の聞こえ方を振り分ける（観戦ルーム＋スタッフ用マイク）。
    /// VRChat の声は「自分の端末で、他の人の声をどう聞くか」を一人ずつ決める仕組みなので、
    /// 各自の端末が他の人の声の届き方を変えるだけで済む（同期変数ゼロ）。
    ///
    ///   観戦ルーム（AetherLoft の Carpet_01 の上）にいる人の声は、ルームの外では聞こえない。
    ///   ルームの中の人は外の人の声も聞こえる（一方向）。それ以外は全員普通。
    ///   スタッフがマイク（KemonoStaffMic）を持っている間は、その人の声を全員が大きく聞く。
    ///   防音はスタッフパネルで ON/OFF できる（MatchmakingManager.GetSoundproofOn・既定 ON）。OFF の間は消音せず、床の防音マークも隠す。
    ///
    /// ルームの範囲は roomArea（シーンの SpectatorRoomArea）の箱＝その Transform のローカルで ±0.5 の立方体。
    /// 位置・大きさ・向きを Scene ビューで動かすだけで範囲が変わる（緑の箱が TableVoiceRouterGizmo で見える）。
    /// 初期値は Build Table Voice が Carpet_01 の床から測る（高さも区切る＝真上の卓は入らない）。一度入った人は exitMargin だけ広く見る（境目で声が切れたり戻ったりしない）。
    /// 観戦モニターは IsLocalInRoom() を見て、ルームの中でだけ使えるようにする。
    /// 状態が変わった人にだけ声の設定を掛け直す。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class TableVoiceRouter : UdonSharpBehaviour
    {
        [Tooltip("観戦ルームの箱。この Transform のローカルで ±0.5 の立方体（位置・大きさ・向きで範囲を決める）。")]
        public Transform roomArea;
        [Tooltip("スタッフ判定に使う。")]
        public MatchmakingManager matchmaking;
        public KemonoStaffMic staffMic;
        [Tooltip("境界の床の帯（防音マーク）。防音 OFF の間は隠す（効いていない防音を案内しない）。")]
        public GameObject boundary;

        [Tooltip("判定の間隔（秒）")]
        public float pollInterval = 0.25f;
        [Tooltip("一度入った人が出たとみなすまでの余白（m）")]
        public float exitMargin = 0.3f;

        // VRChat の既定値（声の大きさは dB。0 にしても消音にはならない）
        public float normalGain = 15f;
        public float normalNear = 0f;
        public float normalFar = 25f;
        // マイク：どこにいても同じ大きさで届く
        public float micGain = 15f;
        public float micNear = 100f;
        public float micFar = 250f;

        private const int ST_NORMAL = 1;
        private const int ST_MUTE = 3;
        private const int ST_MIC = 4;

        private VRCPlayerApi[] players_ = new VRCPlayerApi[100];
        private DataDictionary inRoom_ = new DataDictionary();   // playerId → ルームにいるか
        private DataDictionary applied_ = new DataDictionary();  // playerId → 掛けてある状態
        private bool localInRoom_ = false;
        private float nextPoll_ = 0f;
        private int shownBoundary_ = -1;   // 帯の表示の間引き（-1=未反映）

        private void Update()
        {
            float now = Time.time;
            if (now < nextPoll_) { return; }
            nextPoll_ = now + pollInterval;
            Route();
        }

        // 自分が観戦ルームにいるか（観戦モニターが読む）
        public bool IsLocalInRoom() { return localInRoom_; }

        private bool InRoom(Vector3 p, bool wasIn)
        {
            if (roomArea == null) { return false; }
            // 箱のローカルで ±0.5。出るときの余白（m）は大きさで割ってローカルに直す
            Vector3 lp = roomArea.InverseTransformPoint(p);
            Vector3 s = roomArea.lossyScale;
            float m = wasIn ? exitMargin : 0f;
            float hx = 0.5f + (s.x != 0f ? m / Mathf.Abs(s.x) : 0f);
            float hy = 0.5f + (s.y != 0f ? m / Mathf.Abs(s.y) : 0f);
            float hz = 0.5f + (s.z != 0f ? m / Mathf.Abs(s.z) : 0f);
            return Mathf.Abs(lp.x) <= hx && Mathf.Abs(lp.y) <= hy && Mathf.Abs(lp.z) <= hz;
        }

        private int GetMicHolder()
        {
            if (staffMic == null) { return 0; }
            int id = staffMic.GetHolderId();
            if (id <= 0) { return 0; }
            // 名簿に無い人の「持っている」は信じない（同期を偽装されても拡声させない）
            if (matchmaking == null) { return 0; }
            var p = VRCPlayerApi.GetPlayerById(id);
            if (p == null || !Utilities.IsValid(p) || !matchmaking.IsStaffPlayer(p)) { return 0; }
            return id;
        }

        private void Route()
        {
            var local = Networking.LocalPlayer;
            if (local == null || !Utilities.IsValid(local)) { return; }
            localInRoom_ = InRoom(local.GetPosition(), localInRoom_);
            int micHolder = GetMicHolder();
            // 防音はスタッフパネルで切り替える（MatchmakingManager の同期値・既定 ON）。
            // OFF でもルームの出入りは見続ける（観戦モニターはルーム内だけのまま）。
            bool soundproof = matchmaking == null || matchmaking.GetSoundproofOn();
            int b = soundproof ? 1 : 0;
            if (b != shownBoundary_)
            {
                shownBoundary_ = b;
                if (boundary != null) { boundary.SetActive(soundproof); }
            }

            int count = VRCPlayerApi.GetPlayerCount();
            if (count > players_.Length) { players_ = new VRCPlayerApi[count + 20]; }
            VRCPlayerApi.GetPlayers(players_);
            for (int i = 0; i < count; ++i)
            {
                var p = players_[i];
                if (p == null || !Utilities.IsValid(p) || p.isLocal) { continue; }
                int id = p.playerId;

                bool was = false;
                DataToken rt;
                if (inRoom_.TryGetValue(id, out rt)) { was = rt.Boolean; }
                bool inside = InRoom(p.GetPosition(), was);
                inRoom_[id] = inside;

                int st;
                if (id == micHolder) { st = ST_MIC; }
                else if (soundproof && inside && !localInRoom_) { st = ST_MUTE; }   // ルームの声は外へ漏らさない
                else { st = ST_NORMAL; }

                int prev = 0;
                DataToken at;
                if (applied_.TryGetValue(id, out at)) { prev = at.Int; }
                if (st != prev)
                {
                    Apply(p, st);
                    applied_[id] = st;
                }
            }
        }

        private void Apply(VRCPlayerApi p, int st)
        {
            if (st == ST_MIC)
            {
                p.SetVoiceGain(micGain);
                p.SetVoiceDistanceNear(micNear);
                p.SetVoiceDistanceFar(micFar);
                p.SetVoiceLowpass(false);
                return;
            }
            p.SetVoiceLowpass(true);
            p.SetVoiceGain(normalGain);
            p.SetVoiceDistanceNear(normalNear);
            // 消音は届く距離を 0 にする（声の大きさの dB を 0 にしても消えない）
            p.SetVoiceDistanceFar(st == ST_MUTE ? 0f : normalFar);
        }

        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            if (player == null) { return; }
            inRoom_.Remove(player.playerId);
            applied_.Remove(player.playerId);
        }
    }
}
