using UdonSharp;
using UnityEngine;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    //  鳴ける牌のマーカー（雀魂スタイル）
    // ------------------------------------------------------------
    //  自分に鳴き／ロンの確認が来ている間、その対象の捨て牌に「緑に光る枠」と
    //  「真上の下向き矢印」を付けて点滅させる。何の牌で鳴くのかを一目で分からせるため。
    //
    //  ・純ローカル。同期変数なし。確認が来ている本人の画面にだけ出る（Mahjong.UpdateView が指定）。
    //  ・点滅と矢印の揺れはシェーダー（KemonoMahjong/CallTargetGlow）が _Time で解く＝CPUゼロ。
    //  ・ここでやるのは位置の追従だけ。捨て牌は立直の横滑りなどで動くので、毎フレーム牌へ合わせる。
    //    対象が無いときは Update の先頭で抜ける。
    //  ・スクリプトの載るルートは常時アクティブ。表示ON/OFFは visualRoot だけで行う。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class MahjongCallTargetMarker : UdonSharpBehaviour
    {
        public GameObject visualRoot;

        // 牌の姿勢にそのまま合わせる枠（牌の厚みの中央の面に、牌の縁から外へにじむ輪）。
        public Transform frame;

        // 牌の真上に浮かべる下向きの矢印。牌が横向きでも常に鉛直に立てる。
        public Transform arrow;

        // 牌の中心から矢印の先端までの高さ（m）。寝かせた牌の厚みは約0.016。
        public float arrowHeight = 0.024f;

        private Transform target_;

        public void SetTarget(Transform target)
        {
            target_ = target;
            Follow();
        }

        public void Clear()
        {
            target_ = null;
            if (visualRoot != null && visualRoot.activeSelf) { visualRoot.SetActive(false); }
        }

        private void Update()
        {
            if (target_ == null) { return; }
            Follow();
        }

        private void Follow()
        {
            if (target_ == null || visualRoot == null) { return; }

            // 牌が伏せられている（UpdateView が一度全部 SetActive(false) にしてから置き直す等）間は出さない。
            bool on = target_.gameObject.activeInHierarchy;
            if (visualRoot.activeSelf != on) { visualRoot.SetActive(on); }
            if (!on) { return; }

            var pos = target_.position;
            if (frame != null)
            {
                frame.position = pos;
                frame.rotation = target_.rotation;
            }
            if (arrow != null)
            {
                arrow.position = pos + Vector3.up * arrowHeight;
                arrow.rotation = Quaternion.identity;
            }
        }
    }
}
