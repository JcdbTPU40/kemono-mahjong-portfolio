using UdonSharp;
using UnityEngine;
using UnityEngine.UI;

namespace SuzuFactory.AozoraMahjong
{
    /// <summary>
    /// 入口の特大グループ勧誘パネル（GroupAdPanel）を、対局中は押せなくする。
    ///
    /// VRChat の UI レーザーは遠くまで届くので、卓で牌やボタンを押すつもりのトリガーが
    /// レーザーの先にある 1.45 x 2.05 m のポスターに当たり、対局中にグループページが開いてしまう。
    /// 対局中（MatchmakingManager.IsLocalPlayerSeatedInOngoingGame）の間だけ Canvas の当たり判定を切る。
    /// 当たり判定ごと切る＝レーザー自体がポスターに当たらず奥へ抜ける。押せる Image の raycastTarget も念のため切る
    /// （Udon は UnityEngine.UI.Button を扱えないので、Button ではなく Image で止める）。
    ///
    /// 見る人の端末だけの判定（同期変数ゼロ）。配線は Build Group Ad Panel が行う。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class GroupAdPanelGuard : UdonSharpBehaviour
    {
        [Tooltip("対局中かどうかの判定に使う。")]
        public MatchmakingManager matchmaking;
        [Tooltip("ポスターの当たり判定（Canvas の BoxCollider）。")]
        public Collider panelCollider;
        [Tooltip("ポスターの押せる面（Canvas/Image・Button 付き）。")]
        public Image panelImage;

        [Tooltip("判定の間隔（秒）")]
        public float pollInterval = 0.5f;

        private float nextPoll_ = 0.0f;
        private bool blocked_ = false;
        private bool applied_ = false;

        private void Update()
        {
            float now = Time.time;
            if (now < nextPoll_) { return; }
            nextPoll_ = now + pollInterval;

            bool block = matchmaking != null && matchmaking.IsLocalPlayerSeatedInOngoingGame();
            if (applied_ && block == blocked_) { return; }

            blocked_ = block;
            applied_ = true;
            if (panelCollider != null) { panelCollider.enabled = !block; }
            if (panelImage != null) { panelImage.raycastTarget = !block; }
        }
    }
}
