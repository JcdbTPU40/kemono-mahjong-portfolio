using UdonSharp;
using UnityEngine;
using VRC.SDK3.Persistence;
using VRC.SDKBase;

namespace SuzuFactory.AozoraMahjong
{
    // 卓のボタンを「指で押し込む」入力（ワールド設定の〔レーザー／タッチ〕でタッチを選んだ VR の人だけ）。
    //
    //  ・タッチの人の端末では、対象ボタンのレーザー判定（Interact）を DisableInteractive で消す。
    //    牌（PickTiles）・NEXT・ENTRY などは対象外＝レーザーのまま（牌は幅2.1cmでタッチだと誤爆する）。
    //  ・押下＝指先がボタンの当たり判定の「手前の面」を奥へ抜けた瞬間だけ。横・下・奥から入った指や、
    //    触れたまま留まっている指は押さない（牌の方へ手を伸ばしたときの素通りを拾わないため）。
    //    一度押したら、指が手前へ戻るまで同じボタンは押せない。
    //  ・手前の向きは「その瞬間の頭がある側」で決める（ボタンの向きを決め打ちしない）。
    //  ・押下は MahjongPushButton.TouchPress を呼ぶだけ＝出現直後／押下直後の保護も、送る先もレーザーと同じ。
    //  ・指先は人差し指の先の骨から求める。骨が無いアバターは手の骨を前腕の向きへ延ばした点、
    //    それも無ければコントローラーの位置で代用する（少しずれる。ワールド設定の説明でその人に知らせる）。
    // 設定は PlayerData（PlayerStatsStore.KEY_TOUCH_INPUT）。ローカル専用・同期変数ゼロ。
    // 卓は近接カリングで寝る＝起きている卓だけ Update が回る。タッチでない人は0.5秒ごとの設定確認だけ。
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class MahjongTouchInput : UdonSharpBehaviour
    {
        [Tooltip("タッチで押せるボタン（卓の対局ボタン＋チー候補パネル）。Build Touch Input が配線する。")]
        public MahjongPushButton[] buttons;
        [Tooltip("buttons と同じ並びの当たり判定。")]
        public BoxCollider[] colliders;

        // 手前の面の外側へ戻ったら「次の押下を受け付ける」状態に戻す余白（m）。
        private const float REARM_MARGIN = 0.008f;
        // 横・縦の判定の余白（m）。帯の絵と判定はほぼ同じ大きさなので、指先のぶれのぶんだけ足す。
        private const float SIDE_MARGIN = 0.004f;
        // 指先からこの距離より遠いボタンは調べない（m）。
        private const float NEAR_DISTANCE = 0.35f;
        // 設定を読み直す間隔（秒）。
        private const float POLL_INTERVAL = 0.5f;

        private bool touchOn_ = false;
        private bool interactDisabled_ = false;
        private float nextPoll_ = 0.0f;

        // ボタンごと・手ごとの直前の深さ（手前の面からの距離。正＝手前）と、押下を受け付けられるか。
        private float[] prevDepthR_;
        private float[] prevDepthL_;
        private bool[] armedR_;
        private bool[] armedL_;
        private bool[] wasActive_;

        private void Start()
        {
            int n = buttons != null ? buttons.Length : 0;
            prevDepthR_ = new float[n];
            prevDepthL_ = new float[n];
            armedR_ = new bool[n];
            armedL_ = new bool[n];
            wasActive_ = new bool[n];
        }

        private void OnEnable()
        {
            // 寝ている間に設定が変わっていてもすぐ拾う。
            nextPoll_ = 0.0f;
        }

        private void Update()
        {
            if (Time.time >= nextPoll_)
            {
                nextPoll_ = Time.time + POLL_INTERVAL;
                PollSetting();
            }
            if (!touchOn_ || buttons == null || colliders == null || wasActive_ == null) { return; }

            var lp = Networking.LocalPlayer;
            if (!Utilities.IsValid(lp)) { return; }

            Vector3 head = lp.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;
            Vector3 tipR = GetFingertip(lp, true);
            Vector3 tipL = GetFingertip(lp, false);

            int n = buttons.Length;
            if (colliders.Length < n) { n = colliders.Length; }
            if (wasActive_.Length < n) { n = wasActive_.Length; }
            for (int i = 0; i < n; ++i)
            {
                var pb = buttons[i];
                var col = colliders[i];
                if (pb == null || col == null || !col.gameObject.activeInHierarchy)
                {
                    wasActive_[i] = false;
                    continue;
                }

                bool first = !wasActive_[i];
                wasActive_[i] = true;

                // 両手とも毎フレーム進める（片方で押しても、もう片方の状態を更新し続ける）。
                bool pressR = StepHand(i, col, head, tipR, true, first);
                bool pressL = StepHand(i, col, head, tipL, false, first);
                if ((pressR || pressL) && pb.TouchPress())
                {
                    lp.PlayHapticEventInHand(pressR ? VRC_Pickup.PickupHand.Right : VRC_Pickup.PickupHand.Left, 0.06f, 0.7f, 180.0f);
                }
            }
        }

        // 片手ぶん進める。手前の面を奥へ抜けたフレームだけ true。
        private bool StepHand(int i, BoxCollider col, Vector3 head, Vector3 tip, bool right, bool first)
        {
            if (tip == Vector3.zero) { return false; }

            var t = col.transform;
            if ((t.position - tip).sqrMagnitude > NEAR_DISTANCE * NEAR_DISTANCE)
            {
                // 遠い：いったん受け付けない状態にする。手前から近づけば面の外側で受け付け状態に戻る
                // （卓の下や奥から戻ってきた指は手前の面を通らないので押さない）。
                SetHandState(i, right, -1.0f, false);
                return false;
            }

            // 当たり判定の箱の座標（中心を原点・ローカル単位）。薄い軸が押し込む向き。
            Vector3 size = col.size;
            Vector3 p = t.InverseTransformPoint(tip) - col.center;
            Vector3 h = t.InverseTransformPoint(head) - col.center;
            Vector3 scale = t.lossyScale;

            int axis = 0;
            if (size.y < size.x && size.y <= size.z) { axis = 1; }
            else if (size.z < size.x && size.z < size.y) { axis = 2; }

            float half = GetAxis(size, axis) * 0.5f;
            float sideScale = Mathf.Max(Mathf.Abs(GetAxis(scale, axis == 0 ? 1 : 0)), 0.0001f);
            float margin = SIDE_MARGIN / sideScale;
            float depthScale = Mathf.Max(Mathf.Abs(GetAxis(scale, axis)), 0.0001f);

            float front = GetAxis(h, axis) >= 0.0f ? 1.0f : -1.0f;
            // 手前の面からの距離（m）。正＝手前（頭の側）・0以下＝面より奥。
            float depth = (GetAxis(p, axis) * front - half) * depthScale;

            bool inside = true;
            for (int a = 0; a < 3; ++a)
            {
                if (a == axis) { continue; }
                if (Mathf.Abs(GetAxis(p, a)) > GetAxis(size, a) * 0.5f + margin) { inside = false; }
            }

            float prev = right ? prevDepthR_[i] : prevDepthL_[i];
            bool armed = right ? armedR_[i] : armedL_[i];

            if (first)
            {
                // 出た瞬間に指が中にあっても押さない。指が手前へ出るまで待つ。
                SetHandState(i, right, depth, depth > REARM_MARGIN);
                return false;
            }

            bool pressed = false;
            if (armed && inside && prev > 0.0f && depth <= 0.0f)
            {
                pressed = true;
                armed = false;
            }
            else if (!armed && depth > REARM_MARGIN)
            {
                armed = true;
            }
            else if (armed && depth <= 0.0f)
            {
                // 横・下・奥から入った指（手前の面を通らずに中にいる）は押さず、手前へ出るまで止める。
                armed = false;
            }

            SetHandState(i, right, depth, armed);
            return pressed;
        }

        private void SetHandState(int i, bool right, float depth, bool armed)
        {
            if (right) { prevDepthR_[i] = depth; armedR_[i] = armed; }
            else { prevDepthL_[i] = depth; armedL_[i] = armed; }
        }

        private float GetAxis(Vector3 v, int axis)
        {
            if (axis == 0) { return v.x; }
            if (axis == 1) { return v.y; }
            return v.z;
        }

        // 人差し指の先。骨が無ければ手の骨を前腕の向きへ延ばした点、それも無ければコントローラーの位置。
        private Vector3 GetFingertip(VRCPlayerApi lp, bool right)
        {
            Vector3 distal = lp.GetBonePosition(right ? HumanBodyBones.RightIndexDistal : HumanBodyBones.LeftIndexDistal);
            if (distal != Vector3.zero)
            {
                Vector3 mid = lp.GetBonePosition(right ? HumanBodyBones.RightIndexIntermediate : HumanBodyBones.LeftIndexIntermediate);
                // 末節の骨の位置は第一関節＝指先より手前。中節の長さぶん先へ延ばすと指先に近い。
                return mid != Vector3.zero ? distal + (distal - mid) * 0.9f : distal;
            }

            Vector3 hand = lp.GetBonePosition(right ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
            Vector3 arm = lp.GetBonePosition(right ? HumanBodyBones.RightLowerArm : HumanBodyBones.LeftLowerArm);
            if (hand != Vector3.zero && arm != Vector3.zero && hand != arm)
            {
                // 手首から指先まで約0.17m（目の高さ1.5mの大人基準）。アバターの大きさで伸び縮みさせる。
                float k = Mathf.Clamp(lp.GetAvatarEyeHeightAsMeters() / 1.5f, 0.3f, 3.0f);
                return hand + (hand - arm).normalized * (0.17f * k);
            }

            return lp.GetTrackingData(right ? VRCPlayerApi.TrackingDataType.RightHand : VRCPlayerApi.TrackingDataType.LeftHand).position;
        }

        // 設定を読み、タッチの ON/OFF に合わせて対象ボタンのレーザー判定を出し入れする。
        private void PollSetting()
        {
            var lp = Networking.LocalPlayer;
            bool on = false;
            if (Utilities.IsValid(lp) && lp.IsUserInVR())
            {
                int v;
                on = PlayerData.TryGetInt(lp, PlayerStatsStore.KEY_TOUCH_INPUT, out v) && v == PlayerStatsStore.TOUCH_INPUT_TOUCH;
            }
            if (on != touchOn_ && wasActive_ != null)
            {
                for (int i = 0; i < wasActive_.Length; ++i) { wasActive_[i] = false; }
            }
            touchOn_ = on;

            if (interactDisabled_ == on || buttons == null) { return; }
            interactDisabled_ = on;
            for (int i = 0; i < buttons.Length; ++i)
            {
                if (buttons[i] != null) { buttons[i].DisableInteractive = on; }
            }
        }
    }
}
