using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    // 卓誘導の矢印（自分の足元から「自分が座る席の ENTRY ボタン」まで矢羽根を並べる）
    //
    // ■ なぜ要るか
    //   光柱（tableBeacons）は「どこにあるか」は分かるが、8卓が並ぶと
    //   どれが自分の柱なのか、どちらへ歩けばいいのかが分かりにくい。
    //   足元から伸びる矢印なら、向きと道のりが一目で分かる。
    //
    // ■ 的
    //   行き先の Transform は MatchmakingManager が決める。段位戦は席まで
    //   決まっているので、その席の ENTRY ボタンが的になる（卓の中心ではない）。
    //
    // ■ 設計
    //   ・完全にローカル。同期変数ゼロ（自分の卓は MatchmakingManager が
    //     同期済みの成立情報から各クライアントで自前に判定している）。
    //   ・行き先の指定は MatchmakingManager の _Tick（1秒周期）から SetTarget で押し込む。
    //     こちらから毎フレーム問い合わせない（8卓×4人の走査を60回/秒やる意味がない）。
    //   ・矢羽根は使い回し。並べ替えるだけで生成も破棄もしない。
    //   ・行き先が無い間は Update の頭で1行返す＝止まっているときの負荷はゼロに近い。
    //
    // ■ 高さの扱い ── 卓の高さは見ない。必ず「自分の足元の高さ」に置く。
    //   このワールドは多層で、卓のYは -0.30〜+5.39 まで散っている。
    //   足元から卓へ高さを補間すると、矢羽根が空中を斜めに登って壁や床を貫く。
    //   常に自分が立っている床に寝かせておけば、どの階にいても必ず正しく見える。
    //   毎フレーム Raycast で実地面を拾うのは Quest で高すぎるので採らない。
    //
    // ■ maxLength ── 「道順」ではなく「あっち」を指すもの
    //   階をまたぐ卓へは直線で行けない。長い矢印を伸ばすと道案内に見えて
    //   壁へ突き当たらせてしまうので、一定の長さで打ち切って方向だけを示す。
    //   「どこにあるか」は光柱（tableBeacons）の担当。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class MatchGuideArrow : UdonSharpBehaviour
    {
        [Header("参照（Build Match Guide Arrow で自動割り当て）")]
        [Tooltip("床に並べる矢羽根。先頭から順に足元側へ並ぶ。")]
        public Transform[] chevrons;

        [Header("並べ方")]
        [Tooltip("矢羽根の間隔(m)。")]
        public float spacing = 0.9f;

        [Tooltip("足元から何m先から並べ始めるか。近すぎると視界に入らない。")]
        public float startOffset = 1.2f;

        [Tooltip("的の手前で何m止めるか。ボタンに刺さると汚い。")]
        public float endOffset = 0.8f;

        [Tooltip("これより近づいたら矢印を消す。MatchmakingManager.beaconHideDistance と揃える。")]
        public float hideDistance = 2.0f;

        [Tooltip("矢羽根が流れる速さ(m/s)。0で止まる。")]
        public float flowSpeed = 1.4f;

        [Tooltip("足元から浮かせる高さ(m)。0だと床とZファイティングする。真下すぎると視界に入らないので少し浮かせる。")]
        public float groundOffset = 0.23f;

        [Tooltip("矢印を伸ばす最大の長さ(m)。階をまたぐ卓へ直線の道順を描かないための打ち切り。")]
        public float maxLength = 8.0f;

        [Tooltip("矢羽根の大きさ。")]
        public float chevronScale = 1.0f;

        // --- ローカル状態 ---
        private Transform target_;
        private bool active_ = false;
        // 全部隠し終えたか（消灯処理を1回で済ませる）。false 始まりにしないと Start の消灯が空振りする。
        private bool cleared_ = false;

        void Start()
        {
            HideAll();
        }

        // ============================================================
        //  行き先の指定（MatchmakingManager の _Tick から1秒ごと）
        // ============================================================

        /// <summary>行き先の卓。null で消灯。</summary>
        public void SetTarget(Transform table)
        {
            target_ = table;
            active_ = table != null;
            if (!active_) { HideAll(); }
        }

        /// <summary>今この矢印が出ているか（メニュー側の文言に使う）。</summary>
        public bool IsGuiding()
        {
            return active_ && !cleared_;
        }

        // ============================================================
        //  毎フレーム
        // ============================================================

        void Update()
        {
            if (!active_) { return; }

            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp) || target_ == null || chevrons == null)
            {
                HideAll();
                return;
            }

            Vector3 from = lp.GetPosition();
            Vector3 to = target_.position;

            Vector3 flat = new Vector3(to.x - from.x, 0.0f, to.z - from.z);
            float dist = flat.magnitude;

            // 卓に着いた／要素が入る余地が無いときは畳む。
            float span = dist - startOffset - endOffset;
            if (dist <= hideDistance || span <= 0.0f)
            {
                HideAll();
                return;
            }

            Vector3 dir = flat / dist;
            var rot = Quaternion.LookRotation(dir, Vector3.up);
            var scale = new Vector3(chevronScale, chevronScale, chevronScale);

            // 矢羽根を1本ぶん前へ送り続ける。1本進んだら先頭へ戻す＝
            // 隣の矢羽根が同じ位置に来るので、見た目は途切れずに流れる。
            float step = Mathf.Max(0.05f, spacing);
            float phase = flowSpeed <= 0.0f ? 0.0f : Mathf.Repeat(Time.time * flowSpeed, step);

            // 遠い卓へは伸ばし切らない（上の maxLength の項を参照）。
            float limit = Mathf.Min(dist - endOffset, Mathf.Max(startOffset + step, maxLength));
            int n = chevrons.Length;

            for (int i = 0; i < n; ++i)
            {
                var ch = chevrons[i];
                if (ch == null) { continue; }

                float s = startOffset + i * step + phase;
                if (s > limit)
                {
                    if (ch.gameObject.activeSelf) { ch.gameObject.SetActive(false); }
                    continue;
                }

                // 両端は縮めて出入りさせる。いきなり現れると点滅して見える。
                float fade = Mathf.Min(
                    Mathf.Clamp01((s - startOffset) / step),
                    Mathf.Clamp01((limit - s) / step));
                if (fade <= 0.01f)
                {
                    if (ch.gameObject.activeSelf) { ch.gameObject.SetActive(false); }
                    continue;
                }

                Vector3 p = from + dir * s;
                // 高さは常に自分の足元。卓のYへ寄せると壁を貫く（上の「高さの扱い」を参照）。
                p.y = from.y + groundOffset;

                if (!ch.gameObject.activeSelf) { ch.gameObject.SetActive(true); }
                // SetPositionAndRotation は Udon の許可リスト頼みになるので使わない。
                ch.position = p;
                ch.rotation = rot;
                ch.localScale = scale * fade;
            }

            cleared_ = false;
        }

        private void HideAll()
        {
            if (cleared_) { return; }
            cleared_ = true;

            if (chevrons == null) { return; }
            for (int i = 0; i < chevrons.Length; ++i)
            {
                if (chevrons[i] == null) { continue; }
                if (chevrons[i].gameObject.activeSelf) { chevrons[i].gameObject.SetActive(false); }
            }
        }
    }
}
