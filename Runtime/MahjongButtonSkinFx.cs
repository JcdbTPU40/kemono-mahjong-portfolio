using UdonSharp;
using UnityEngine;

namespace SuzuFactory.AozoraMahjong
{
    // 卓の対局ボタン（チー/ポン/ロン…）の見た目を雀魂風にした「帯」の出現アニメ。
    // 右から滑り込みながらフェードインする（雀魂の鳴きボタンと同じ段取り）。
    //
    // ■ 同じフレームで消えて点いたときはやり直さない
    //   Mahjong.UpdateView は呼ばれるたびに全ボタンを SetActive(false) してから、
    //   必要なものだけ SetActive(true) し直す。素直に OnEnable で再生すると、
    //   同期が届くたびに滑り込みが頭からやり直されてチラつく。
    //   OnDisable のフレーム番号を覚えておき、同じフレームの再表示は「出しっぱなし」とみなす。
    //
    // ■ 同期変数ゼロ・ローカル専用
    //   ボタンは各自の画面にしか出ないので同期は要らない。Update が走るのは
    //   表示中のボタンだけで、滑り込みが終わったら即 return する。
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class MahjongButtonSkinFx : UdonSharpBehaviour
    {
        // 日英の帯（JA_Skin / EN_Skin）。アルファは MaterialPropertyBlock で配る。
        public Renderer[] renderers;

        // 滑り込みの所要時間（秒）。雀魂は約3フレーム＝0.12秒。VRでは少しだけ長めに見せる。
        public float slideSeconds = 0.15f;

        // 右から何m滑り込むか。帯の幅は約0.11m。
        public float slideDistance = 0.03f;

        private Vector3 home_;
        private Vector3 slideDir_;
        private bool homeSaved_;
        private bool animating_;
        private float startTime_;
        private int disabledFrame_ = -10;
        private MaterialPropertyBlock mpb_;

        private void OnEnable()
        {
            if (!homeSaved_)
            {
                home_ = transform.localPosition;
                // 帯の右方向を親の座標系で持つ（Skin は帯の向きに合わせて回してある）。
                slideDir_ = transform.localRotation * Vector3.right;
                homeSaved_ = true;
            }

            // UpdateView の「全部消してから点け直す」は同じフレームで起きる。
            if (Time.frameCount == disabledFrame_) { return; }

            startTime_ = Time.time;
            animating_ = true;
            Apply(0f);
        }

        private void OnDisable()
        {
            disabledFrame_ = Time.frameCount;
        }

        private void Update()
        {
            if (!animating_) { return; }

            float t = slideSeconds > 0f ? (Time.time - startTime_) / slideSeconds : 1f;
            if (t >= 1f)
            {
                t = 1f;
                animating_ = false;
            }
            Apply(t);
        }

        private void Apply(float t)
        {
            // ease-out cubic：最初に速く、最後にすっと止まる。
            float u = 1f - t;
            float e = 1f - u * u * u;

            transform.localPosition = home_ + slideDir_ * (slideDistance * (1f - e));

            if (renderers == null) { return; }
            if (mpb_ == null) { mpb_ = new MaterialPropertyBlock(); }

            mpb_.SetColor("_Color", new Color(1f, 1f, 1f, e));
            for (int i = 0; i < renderers.Length; ++i)
            {
                if (renderers[i] != null) { renderers[i].SetPropertyBlock(mpb_); }
            }
        }
    }
}
