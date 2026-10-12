using TMPro;
using UdonSharp;
using UnityEngine;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    // 卓番号の色分け（使用中＝赤／空き＝緑）
    //
    // 【なぜ卓ごとに置かないのか】
    //   番号の板は卓ルートの子なので、近接カリングで卓が寝ると一緒に止まる。
    //   卓ごとに監視役を置くと「寝ている間に色が変わって、起きた時に古い色のまま」に
    //   なるうえ、遅延イベントのループが卓の数だけ増える。
    //   そこで **常時アクティブな1体**が全部（8卓×4面）の色を書く。無効な TMP へ
    //   color を書くのは合法で、卓が起きた瞬間から正しい色で描かれる。
    //
    // 【同期変数ゼロ】
    //   使用中かどうかは MatchmakingManager の同期値から各自が導くだけ。
    //   色そのものを配る必要はない。
    //
    // 【Update を使わない】
    //   1秒の遅延イベントで回す。色の追従に1秒の遅れが出るが、卓の使用状況は
    //   そもそも数十秒単位でしか変わらない。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class TableNumberTint : UdonSharpBehaviour
    {
        [Header("参照（Build Table Numbers が自動割り当て）")]
        public MatchmakingManager matchmaking;

        [Tooltip("卓番号の文字。卓インデックス i の面が [i*4 .. i*4+3] に入る。")]
        public TextMeshPro[] texts;

        [Header("色")]
        [Tooltip("空いている卓。")]
        public Color freeColor = new Color(0.36f, 0.90f, 0.44f, 1f);
        [Tooltip("使用中の卓。")]
        public Color busyColor = new Color(0.95f, 0.27f, 0.27f, 1f);

        [Tooltip("色を見直す間隔(秒)。")]
        public float tickSeconds = 1.0f;

        // 前回描いた使用中ビット。変化が無ければ TMP を触らない（Quest対策）。
        private int shownMask_ = -1;

        private void Start()
        {
            Apply();
            SendCustomEventDelayedSeconds(nameof(_TableTintTick), TickSeconds());
        }

        private float TickSeconds()
        {
            return tickSeconds < 0.25f ? 0.25f : tickSeconds;
        }

        public void _TableTintTick()
        {
            Apply();
            SendCustomEventDelayedSeconds(nameof(_TableTintTick), TickSeconds());
        }

        private void Apply()
        {
            if (matchmaking == null || texts == null) { return; }

            int mask = matchmaking.GetBusyTableMaskForView();
            if (mask == shownMask_) { return; }
            shownMask_ = mask;

            for (int i = 0; i < texts.Length; ++i)
            {
                var tmp = texts[i];
                if (tmp == null) { continue; }
                int tableIndex = i / 4;
                bool busy = (mask & (1 << tableIndex)) != 0;
                tmp.color = busy ? busyColor : freeColor;
            }
        }
    }
}
