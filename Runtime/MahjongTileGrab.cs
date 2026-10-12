using UdonSharp;
using UnityEngine;
using VRC.SDK3.Persistence;
using VRC.SDKBase;
using VRC.Udon.Common;

namespace SuzuFactory.AozoraMahjong
{
    // 牌を掴んで打牌する入力（ワールド設定〔VR操作〕で ON にした VR の人だけ）。
    //
    //  ・掴む：グリップを握った瞬間、指先（親指と人差し指の先の中点）から一番近い「今切れる牌」を掴む。
    //    切れる牌＝卓の PickTiles の子のうち表示中のもの（立直後のツモ牌だけ・食い替え除外・
    //    立直宣言牌の候補…は Mahjong.UpdateView が既に出し入れしている）。カンの牌選びは対象外。
    //  ・離す：牌山の手前の縁（Mahjong.TILE_GRAB_DISCARD_RADIUS）より卓の中央側で離すと打牌。
    //    それ以外は元へ戻すだけ。打牌はクリックの2回目と同じ OnPickTileLocal(index, true)
    //    ＝卓 Owner が手番の署名と打牌枚数で検証する経路そのまま（チート耐性は変わらない）。
    //  ・見せ方：掴んだ／離したの2つだけを全員へ送り（Mahjong.OnTileGrabNet / OnTileReleaseNet）、
    //    手への追従は各端末がその人の手の骨（IK）を見て行う＝位置は送らない・同期変数ゼロ。
    //    手を近づけただけの「どれにしようかな」は本人の画面だけ（牌を少し浮かせる）で、他人には送らない。
    //    伏せ牌として見えている人の画面では、実物の代わりに裏面だけの代役（proxy）を持たせる
    //    （実物を回すと絵柄が覗けるため）。
    //  ・持ち時間：掴んでいる間は一時停止。1回の決断（手番の署名）につき合計 Mahjong.TILE_GRAB_PAUSE_BUDGET_MS まで。
    //    使い切ったら牌は自動で戻り時計が再開する。期限の権威は卓 Owner（Mahjong.TileGrabPauseBegin/End）で、
    //    ここは本人の画面の表示と本人側の時間切れ判定を止めるだけ（GetLocalDeadlineOverrideMs）。
    // 設定は PlayerData（PlayerStatsStore.KEY_TILE_GRAB）。卓は近接カリングで寝る＝起きている卓だけ動く。
    // ネットワークの送受信はすべて卓本体（Mahjong）が持つので、ここは Network ID も要らない（None）。
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class MahjongTileGrab : UdonSharpBehaviour
    {
        [Tooltip("この卓の本体。Build Tile Grab が配線する。")]
        public Mahjong mahjong;
        [Tooltip("伏せ牌として見えている人に持たせる裏面だけの代役。Build Tile Grab が作る（普段は非アクティブ）。")]
        public Transform proxy;

        // 指先から牌の中心までこの距離以内なら掴める（m）。牌は幅2.12cm・高さ約2.8cm。
        private const float GRAB_RADIUS = 0.035f;
        // 掴める牌の候補を乗り換えるのは、今の候補よりこれだけ近いときだけ（m）。境目でのチラつき防止。
        private const float HOVER_HYSTERESIS = 0.004f;
        // 離してから次に掴めるまで（秒）。掴む／離すの連打でイベントを撒かないため。
        private const float REGRAB_COOLDOWN = 0.3f;
        // 打牌して離した牌を、その場に留めておく上限（秒）。普通は同期で河へ移るのが先。
        private const float DISCARD_HOLD_SECONDS = 1.5f;
        // 離した直後、本人の画面の残り時間を「再開した値」で出し続ける上限（秒）。Owner の期限が届くまでのつなぎ。
        private const float RESUME_WINDOW_SECONDS = 3.0f;
        // 他人の掴みを追従させる上限（秒）。離したイベントを取りこぼしても必ず畳む。
        private const float REMOTE_HOLD_LIMIT = 20.0f;
        private const float POLL_INTERVAL = 0.5f;

        private bool settingOn_ = false;
        private bool inVr_ = false;
        private float nextPoll_ = 0.0f;

        // 掴んでいる牌（全端末で同じ値。席が -1 なら誰も掴んでいない）。
        private int heldSeat_ = -1;
        private int heldIndex_ = -1;
        private int heldSig_ = 0;
        private bool heldRight_ = true;
        private int heldPlayerId_ = 0;
        private bool heldMine_ = false;
        private float heldStart_ = 0.0f;
        private Transform heldTile_;
        private bool discarding_ = false;    // 打牌して離した＝同期が来るまでその場に留める
        private bool releaseSent_ = false;   // 離したイベントを送った（戻ってくるまで二重に送らない）
        private float discardUntil_ = 0.0f;

        // 持ち時間の一時停止（本人の端末だけ意味がある）。
        private int pauseSig_ = 0;
        private float pauseUsed_ = 0.0f;     // この決断で使った秒数
        private float pauseStart_ = -1.0f;   // 止めている間は開始時刻、止めていなければ負
        private int frozenRemainMs_ = -1;    // 止めた瞬間の残り（ms）
        private int resumeSig_ = 0;
        private int resumeDeadlineMs_ = 0;
        private float resumeUntil_ = 0.0f;

        // 本人の画面だけの「掴める牌」の候補。浮かせるのはその牌1枚を直接動かす
        // （UpdateView は1回で十数ms かかり、手を滑らせるたびに呼ぶと Quest でカクつく）。
        private int hoverIndex_ = -1;        // PickTiles の番号（乗り換えの余白に使う）
        private int hoverTileId_ = -1;       // 浮かせている牌の ID
        private Transform hoverTile_;        // 浮かせている牌
        private Vector3 hoverLiftedPos_;     // 浮かせた直後の位置（誰かが置き直したかの判定）
        private bool hoverRight_ = true;
        private float lastRelease_ = -10.0f;

        private void OnEnable()
        {
            nextPoll_ = 0.0f;
        }

        private void OnDisable()
        {
            // 卓が寝た（近接カリング）。追従を続けられないので黙って畳む。起きたら UpdateView が置き直す。
            ResetHeldState();
            // 寝ている間の牌は起きたときに UpdateView が置き直すので、戻さず記録だけ捨てる。
            ForgetHover();
        }

        private void Update()
        {
            if (mahjong == null) { return; }

            if (Time.time >= nextPoll_)
            {
                nextPoll_ = Time.time + POLL_INTERVAL;
                PollSetting();
            }

            if (heldSeat_ >= 0)
            {
                UpdateHeld();
                return;
            }

            UpdateHover();
        }

        // ============================================================
        // 本人の入力
        // ============================================================
        public override void InputGrab(bool value, UdonInputEventArgs args)
        {
            if (mahjong == null) { return; }
            bool right = args.handType == HandType.RIGHT;

            if (value)
            {
                TryGrab(right);
                return;
            }

            if (heldSeat_ < 0 || !heldMine_ || discarding_ || releaseSent_ || heldRight_ != right) { return; }
            ReleaseLocal(IsDiscardPose());
        }

        private void TryGrab(bool right)
        {
            if (heldSeat_ >= 0) { return; }
            if (!settingOn_ || !inVr_) { return; }
            if (Time.time - lastRelease_ < REGRAB_COOLDOWN) { return; }
            if (!mahjong.TileGrab_CanGrabLocal()) { return; }

            var lp = Networking.LocalPlayer;
            if (!Utilities.IsValid(lp)) { return; }
            // VRChat の Pickup（役の早見板など）を持っている手では掴まない。
            if (lp.GetPickupInHand(right ? VRC_Pickup.PickupHand.Right : VRC_Pickup.PickupHand.Left) != null) { return; }

            Vector3 grip = GetGripPoint(lp, right);
            if (grip == Vector3.zero) { return; }
            // 浮かせている候補を優先する（光った牌と掴む牌がずれないように、乗り換えの余白を同じにする）。
            int index = FindNearestPickTile(grip, hoverIndex_);
            if (index < 0) { return; }

            int sig = mahjong.TileGrab_GetSignature();
            if (sig == 0) { return; }
            mahjong.TileGrab_SendGrab(index, right ? 0 : 1, sig);
        }

        private void ReleaseLocal(bool discard)
        {
            if (releaseSent_) { return; }
            releaseSent_ = true;
            lastRelease_ = Time.time;
            var lp = Networking.LocalPlayer;
            if (Utilities.IsValid(lp))
            {
                lp.PlayHapticEventInHand(heldRight_ ? VRC_Pickup.PickupHand.Right : VRC_Pickup.PickupHand.Left,
                    discard ? 0.08f : 0.04f, discard ? 0.8f : 0.4f, 180.0f);
            }
            mahjong.TileGrab_SendRelease(heldSeat_, heldIndex_, heldSig_, discard);
        }

        // 持っている牌（指先）が牌山の手前の縁を越えたか。卓のローカル座標で「自席から見た距離」を測る
        // （どこを摘まんだか・どこから動かし始めたかに左右されない）。
        private bool IsDiscardPose()
        {
            var lp = Networking.LocalPlayer;
            if (!Utilities.IsValid(lp)) { return false; }
            Vector3 cur = GetGripPoint(lp, heldRight_);
            if (cur == Vector3.zero) { return false; }
            Vector3 local = mahjong.transform.InverseTransformPoint(cur);
            Vector3 seatOut = Quaternion.Euler(0.0f, heldSeat_ * -90.0f, 0.0f) * Vector3.right;
            return Vector3.Dot(local, seatOut) <= Mahjong.TILE_GRAB_DISCARD_RADIUS;
        }

        // 本人が掴み打牌を使える状態か（ワールド設定 ON かつ VR）。北抜きを2段にするかの判定にも使う。
        public bool IsGrabEnabledLocal()
        {
            if (Time.time >= nextPoll_) { nextPoll_ = Time.time + POLL_INTERVAL; PollSetting(); }
            return settingOn_ && inVr_;
        }

        // 手を近づけた牌を少し浮かせる（本人の画面だけ・送らない）。
        private void UpdateHover()
        {
            int next = -1;
            bool grabbable = mahjong.TileGrab_CanGrabLocal();
            if (settingOn_ && inVr_ && grabbable)
            {
                var lp = Networking.LocalPlayer;
                if (Utilities.IsValid(lp))
                {
                    int best = -1;
                    bool bestRight = true;
                    float bestD = float.MaxValue;
                    for (int h = 0; h < 2; ++h)
                    {
                        bool right = h == 0;
                        if (lp.GetPickupInHand(right ? VRC_Pickup.PickupHand.Right : VRC_Pickup.PickupHand.Left) != null) { continue; }
                        Vector3 grip = GetGripPoint(lp, right);
                        if (grip == Vector3.zero) { continue; }
                        float d;
                        int idx = FindNearestPickTileDist(grip, hoverIndex_, out d);
                        if (idx >= 0 && d < bestD) { bestD = d; best = idx; bestRight = right; }
                    }
                    next = best;
                    hoverRight_ = bestRight;
                }
            }

            hoverIndex_ = next;
            int nextId = next >= 0 ? mahjong.TileGrab_GetHandTileId(mahjong.TileGrab_GetTurn(), next) : -1;
            if (nextId == hoverTileId_) { return; }

            // 前の牌を下ろす。浮かせた後に誰か（同期の UpdateView・ツモの出現演出）が置き直していたら
            // 位置の前提が崩れているので、手で戻さず UpdateView に正しく置き直させる（まれ）。
            // 打牌を選ぶ局面を抜けた（打牌が通った等）なら、その同期の UpdateView が既に浮かせずに置いている。
            bool needRefresh = false;
            if (hoverTile_ != null)
            {
                if ((hoverTile_.position - hoverLiftedPos_).sqrMagnitude < 0.00000001f) { hoverTile_.position -= GetLiftVector(); }
                else if (grabbable) { needRefresh = true; }
            }

            hoverTileId_ = nextId;
            mahjong.tileGrabHoverTileId = nextId;
            hoverTile_ = next >= 0 ? mahjong.TileGrab_GetHandTile(mahjong.TileGrab_GetTurn(), next) : null;

            if (needRefresh)
            {
                // UpdateView が新しい候補（tileGrabHoverTileId）ごと置き直す＝浮かせるのもやってくれる。
                mahjong.TileGrab_Refresh();
                if (hoverTile_ != null) { hoverLiftedPos_ = hoverTile_.position; }
            }
            else if (hoverTile_ != null)
            {
                hoverTile_.position += GetLiftVector();
                hoverLiftedPos_ = hoverTile_.position;
            }

            if (hoverTile_ != null)
            {
                var lp2 = Networking.LocalPlayer;
                if (Utilities.IsValid(lp2))
                {
                    lp2.PlayHapticEventInHand(hoverRight_ ? VRC_Pickup.PickupHand.Right : VRC_Pickup.PickupHand.Left, 0.02f, 0.15f, 120.0f);
                }
            }
        }

        // 卓の上向きに TILE_GRAB_HOVER_LIFT（UpdateView が浮かせる量と同じ）。
        private Vector3 GetLiftVector()
        {
            return mahjong.transform.TransformVector(new Vector3(0.0f, Mahjong.TILE_GRAB_HOVER_LIFT, 0.0f));
        }

        // 浮かせた記録を捨てる（牌はこの後 UpdateView が置き直す前提）。
        private void ForgetHover()
        {
            hoverIndex_ = -1;
            hoverTileId_ = -1;
            hoverTile_ = null;
            if (mahjong != null) { mahjong.tileGrabHoverTileId = -1; }
        }

        private int FindNearestPickTile(Vector3 grip, int current)
        {
            float d;
            return FindNearestPickTileDist(grip, current, out d);
        }

        // 表示中の PickTiles の子のうち、指先に一番近いもの。current は今の候補（乗り換えに余白を持たせる）。
        private int FindNearestPickTileDist(Vector3 grip, int current, out float bestDist)
        {
            bestDist = float.MaxValue;
            var root = mahjong.TileGrab_GetPickTilesRoot();
            if (root == null) { return -1; }

            int best = -1;
            float r2 = GRAB_RADIUS * GRAB_RADIUS;
            int n = root.childCount;
            for (int k = 0; k < n; ++k)
            {
                var t = root.GetChild(k);
                if (!t.gameObject.activeInHierarchy) { continue; }
                float d = (t.position - grip).sqrMagnitude;
                if (d > r2) { continue; }
                float dist = Mathf.Sqrt(d);
                if (k == current) { dist -= HOVER_HYSTERESIS; }
                if (dist < bestDist) { bestDist = dist; best = k; }
            }
            return best;
        }

        // ============================================================
        // 全員の端末（Mahjong のネットワークイベントから）
        // ============================================================
        public void NetGrab(int seat, int index, int hand, int sig, int playerId)
        {
            if (mahjong == null) { return; }
            if (heldSeat_ >= 0) { ClearHeld(false); }

            var lp = Networking.LocalPlayer;
            heldSeat_ = seat;
            heldIndex_ = index;
            heldSig_ = sig;
            heldRight_ = hand == 0;
            heldPlayerId_ = playerId;
            heldMine_ = Utilities.IsValid(lp) && lp.playerId == playerId;
            heldStart_ = Time.time;
            discarding_ = false;
            releaseSent_ = false;
            heldTile_ = mahjong.TileGrab_GetHandTile(seat, index);

            if (heldMine_)
            {
                if (pauseSig_ != sig) { pauseSig_ = sig; pauseUsed_ = 0.0f; }
                resumeUntil_ = 0.0f;
                int remain = mahjong.TileGrab_GetLocalRemainMs();
                if (remain >= 0 && GetPauseLeft() > 0.05f)
                {
                    pauseStart_ = Time.time;
                    frozenRemainMs_ = remain;
                }

                if (Utilities.IsValid(lp))
                {
                    lp.PlayHapticEventInHand(heldRight_ ? VRC_Pickup.PickupHand.Right : VRC_Pickup.PickupHand.Left, 0.05f, 0.6f, 160.0f);
                }
            }

            ForgetHover();   // 直後の Refresh（UpdateView）が浮かせた牌を元の高さへ置き直す
            mahjong.tileGrabHeldSeat = seat;
            mahjong.tileGrabHeldIndex = index;
            mahjong.tileGrabHeldTileId = mahjong.TileGrab_GetHandTileId(seat, index);
            mahjong.TileGrab_OnGrabbed(seat);
            mahjong.TileGrab_Refresh();   // 伏せ牌の人の画面では実物を隠す（代役に替える）
            FollowHand();
        }

        public void NetRelease(int seat, int index, int sig, bool discard)
        {
            if (heldSeat_ != seat || heldIndex_ != index || heldSig_ != sig) { return; }
            if (heldMine_) { EndLocalPause(); }

            if (discard)
            {
                // 同期で河へ移るまで、離した場所に留める（手牌へ一度戻ってから河へ飛ぶのを防ぐ）。
                discarding_ = true;
                discardUntil_ = Time.time + DISCARD_HOLD_SECONDS;
                return;
            }
            ClearHeld(true);
        }

        private void UpdateHeld()
        {
            // 局面が進んだ（打牌が通った・時間切れ・鳴き・リセット）＝掴みは終わり。
            if (mahjong.TileGrab_GetSignature() != heldSig_)
            {
                ClearHeld(true);
                return;
            }

            if (discarding_)
            {
                if (Time.time >= discardUntil_) { ClearHeld(true); }
                return;
            }

            if (heldMine_)
            {
                // 一時停止の予算を使い切った：牌を戻して時計を再開する。
                if (pauseStart_ >= 0.0f && Time.time - pauseStart_ >= GetPauseLeft())
                {
                    ReleaseLocal(false);
                }
                // 設定を切った・VRを外した：その場で戻す。
                else if (!settingOn_ || !inVr_)
                {
                    ReleaseLocal(false);
                }
            }
            else if (Time.time - heldStart_ > REMOTE_HOLD_LIMIT)
            {
                ClearHeld(true);
                return;
            }

            FollowHand();
        }

        private void FollowHand()
        {
            if (heldSeat_ < 0 || discarding_) { return; }
            var p = VRCPlayerApi.GetPlayerById(heldPlayerId_);
            if (!Utilities.IsValid(p))
            {
                ClearHeld(true);
                return;
            }
            Vector3 grip = GetGripPoint(p, heldRight_);
            if (grip == Vector3.zero)
            {
                // 手の位置が取れない（アバター非表示・読み込み中など）。代役は卓の中央に浮かせず隠しておく。
                if (proxy != null && proxy.gameObject.activeSelf) { proxy.gameObject.SetActive(false); }
                return;
            }

            // 立てたまま、その席の向き（絵柄は掴んだ本人の側）で持つ。UpdateView の手牌と同じ姿勢。
            Quaternion rot = mahjong.transform.rotation
                * Quaternion.Euler(0.0f, heldSeat_ * -90.0f, 0.0f)
                * Quaternion.Euler(0.0f, 90.0f, 0.0f);

            bool real = mahjong.tileGrabHeldFaceVisible;
            Transform target = real ? heldTile_ : proxy;
            if (proxy != null && proxy.gameObject.activeSelf == real) { proxy.gameObject.SetActive(!real); }
            if (target == null) { return; }
            target.position = grip;
            target.rotation = rot;
        }

        // 掴みを畳む。refresh=true なら UpdateView で実物を手牌へ戻す。
        private void ClearHeld(bool refresh)
        {
            if (heldMine_) { EndLocalPause(); }
            ResetHeldState();
            if (mahjong != null && refresh) { mahjong.TileGrab_Refresh(); }
        }

        private void ResetHeldState()
        {
            heldSeat_ = -1;
            heldIndex_ = -1;
            heldSig_ = 0;
            heldPlayerId_ = 0;
            heldMine_ = false;
            heldTile_ = null;
            discarding_ = false;
            releaseSent_ = false;
            pauseStart_ = -1.0f;
            frozenRemainMs_ = -1;
            if (proxy != null && proxy.gameObject.activeSelf) { proxy.gameObject.SetActive(false); }
            if (mahjong != null)
            {
                mahjong.tileGrabHeldSeat = -1;
                mahjong.tileGrabHeldIndex = -1;
                mahjong.tileGrabHeldTileId = -1;
            }
        }

        // ============================================================
        // 持ち時間の一時停止（本人の端末）
        // ============================================================
        private float GetPauseLeft()
        {
            float left = Mahjong.TILE_GRAB_PAUSE_BUDGET_MS * 0.001f - pauseUsed_;
            return left > 0.0f ? left : 0.0f;
        }

        private void EndLocalPause()
        {
            if (pauseStart_ < 0.0f) { return; }
            float left = GetPauseLeft();
            float el = Time.time - pauseStart_;
            if (el > left) { el = left; }
            if (el < 0.0f) { el = 0.0f; }
            pauseUsed_ += el;
            pauseStart_ = -1.0f;

            // Owner が戻した期限が届くまで、本人の画面では「止めた瞬間の残り」から再開させる。
            if (frozenRemainMs_ >= 0)
            {
                int d = Networking.GetServerTimeInMilliseconds() + frozenRemainMs_;
                if (d == 0) { d = 1; }
                resumeDeadlineMs_ = d;
                resumeSig_ = pauseSig_;
                resumeUntil_ = Time.time + RESUME_WINDOW_SECONDS;
            }
            frozenRemainMs_ = -1;
        }

        // 本人の残り時間の表示・時間切れ判定に使う期限の上書き（ms）。0 なら上書きしない。
        // 掴んでいる間＝今から「止めた瞬間の残り」後（＝進まない）。離した直後＝再開した期限。
        public int GetLocalDeadlineOverrideMs(int sig)
        {
            if (sig == 0) { return 0; }
            if (heldMine_ && pauseStart_ >= 0.0f && pauseSig_ == sig && frozenRemainMs_ >= 0
                && Time.time - pauseStart_ < GetPauseLeft())
            {
                int d = Networking.GetServerTimeInMilliseconds() + frozenRemainMs_;
                return d == 0 ? 1 : d;
            }
            if (resumeSig_ == sig && Time.time < resumeUntil_)
            {
                // Owner の期限が届いた（再開値とほぼ同じ）ら、そちらへ戻す。
                int diff = mahjong.TileGrab_GetSyncedDeadlineMs() - resumeDeadlineMs_;
                if (diff > -1500 && diff < 1500)
                {
                    resumeUntil_ = 0.0f;
                    return 0;
                }
                return resumeDeadlineMs_;
            }
            return 0;
        }

        // ============================================================
        // 共通
        // ============================================================
        private void PollSetting()
        {
            var lp = Networking.LocalPlayer;
            bool vr = Utilities.IsValid(lp) && lp.IsUserInVR();
            bool on = false;
            if (vr)
            {
                int v;
                on = PlayerData.TryGetInt(lp, PlayerStatsStore.KEY_TILE_GRAB, out v) && v == PlayerStatsStore.TILE_GRAB_ON;
            }
            inVr_ = vr;
            settingOn_ = on;
        }

        // 摘まむ位置＝親指の先と人差し指の先の中点。骨が無ければ手の骨から指の付け根の向きへ延ばした点、
        // それも無ければ手のトラッキング位置。他人の手は IK で同期された骨を読む。
        private Vector3 GetGripPoint(VRCPlayerApi p, bool right)
        {
            Vector3 thumb = p.GetBonePosition(right ? HumanBodyBones.RightThumbDistal : HumanBodyBones.LeftThumbDistal);
            Vector3 index = p.GetBonePosition(right ? HumanBodyBones.RightIndexDistal : HumanBodyBones.LeftIndexDistal);
            if (thumb != Vector3.zero && index != Vector3.zero) { return (thumb + index) * 0.5f; }

            Vector3 hand = p.GetBonePosition(right ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
            if (hand != Vector3.zero)
            {
                Vector3 mid = p.GetBonePosition(right ? HumanBodyBones.RightMiddleProximal : HumanBodyBones.LeftMiddleProximal);
                return mid != Vector3.zero ? hand + (mid - hand) * 1.6f : hand;
            }

            // 最後の手段：手のトラッキング位置（他人の分も IK の同期で取れる）。
            return p.GetTrackingData(right ? VRCPlayerApi.TrackingDataType.RightHand : VRCPlayerApi.TrackingDataType.LeftHand).position;
        }
    }
}
