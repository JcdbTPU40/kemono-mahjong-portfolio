using UdonSharp;
using UnityEngine;

namespace SuzuFactory.AozoraMahjong
{
    /// <summary>
    /// メニューのボタンを押したときの「沈んで戻る」アニメーション。
    ///
    /// 各ボタンには MenuButtonPress（Update を持たない中継）が付いていて、
    /// onClick からここへ集まる。Update をここ1本に集約しているのは、
    /// ボタン54個ぶんの Update を回すと Quest で効いてくるため。
    /// 押されていない間は count_ == 0 で即 return する＝実質ゼロコスト。
    ///
    /// 色の変化（ホバー・押下）は Selectable の ColorTint がやっているので触らない。
    /// ここは localScale だけを動かす。画面フェードが触るのは Graphic.color なので、
    /// 役割が分かれていれば競合しない。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class MenuButtonPressFx : UdonSharpBehaviour
    {
        [Tooltip("押し込みの深さ。1.0で変化なし、小さいほど深く沈む。")]
        public float pressScale = 0.90f;

        [Tooltip("押し込みから戻りきるまでの尺（秒）。")]
        public float duration = 0.16f;

        [Tooltip("戻るときの行き過ぎ量。1.0で跳ねない。")]
        public float overshoot = 1.05f;

        // ---- クリック音 ----
        //   ボタン84個ぶんの onClick は既に Punch() へ集まっているので、ここ1か所で
        //   全ボタンのクリック音になる。個別配線は不要。ローカル再生＝同期ゼロ。
        [Header("クリック音（MainMenuの全ボタン共通・ローカル再生）")]
        [Tooltip("2D想定。Build Volume Panel が音量スライダーへ登録する。")]
        public AudioSource clickSource;
        public AudioClip clickClip;
        [Range(0.0f, 1.0f)] public float clickVolume = 0.12f;

        // 同時にアニメーションできる数。操作するのはローカルの自分だけなので、
        // 連打とダブルクリックぶんの余裕があれば足りる。
        private const int MAX_ACTIVE = 6;

        private RectTransform[] targets_ = new RectTransform[MAX_ACTIVE];
        private float[] startTimes_ = new float[MAX_ACTIVE];
        private int count_ = 0;

        /// <summary>押されたボタンを登録する（MenuButtonPress から呼ぶ）。</summary>
        public void Punch(RectTransform target)
        {
            if (target == null) { return; }

            // クリック音は下の「連打は開始時刻を戻して return」より手前で鳴らすこと。
            // 後ろに置くと、同じボタンを続けて押したとき2回目以降が無音になる。
            if (clickSource != null && clickClip != null)
            {
                clickSource.PlayOneShot(clickClip, clickVolume);
            }

            // 同じボタンの連打は開始時刻を巻き戻すだけ。二重登録すると戻し忘れが起きる。
            for (int i = 0; i < count_; ++i)
            {
                if (targets_[i] == target)
                {
                    startTimes_[i] = Time.time;
                    return;
                }
            }

            if (count_ >= MAX_ACTIVE)
            {
                // あふれたら一番古いものを畳んで枠を空ける（縮んだまま残さない）。
                if (targets_[0] != null) { targets_[0].localScale = Vector3.one; }
                RemoveAt(0);
            }

            targets_[count_] = target;
            startTimes_[count_] = Time.time;
            ++count_;
        }

        void Update()
        {
            if (count_ == 0) { return; }

            float now = Time.time;
            for (int i = count_ - 1; i >= 0; --i)
            {
                var target = targets_[i];
                if (target == null) { RemoveAt(i); continue; }

                float k = (duration > 0.0f) ? (now - startTimes_[i]) / duration : 1.0f;
                if (k >= 1.0f)
                {
                    // 画面が閉じていても最後に必ず等倍へ戻す（縮んだまま次に開かない）。
                    target.localScale = Vector3.one;
                    RemoveAt(i);
                    continue;
                }

                float s = ScaleAt(k);
                target.localScale = new Vector3(s, s, 1.0f);
            }
        }

        // 沈む → 行き過ぎ → 等倍の三区間。各区間をスムーズステップで繋ぐと
        // 直線補間より「カチッ」と押した感じになる。
        private float ScaleAt(float k)
        {
            if (k < 0.30f) { return Mathf.Lerp(1.0f, pressScale, Smooth(k / 0.30f)); }
            if (k < 0.70f) { return Mathf.Lerp(pressScale, overshoot, Smooth((k - 0.30f) / 0.40f)); }
            return Mathf.Lerp(overshoot, 1.0f, Smooth((k - 0.70f) / 0.30f));
        }

        private float Smooth(float x)
        {
            return x * x * (3.0f - 2.0f * x);
        }

        private void RemoveAt(int index)
        {
            for (int i = index + 1; i < count_; ++i)
            {
                targets_[i - 1] = targets_[i];
                startTimes_[i - 1] = startTimes_[i];
            }
            --count_;
            targets_[count_] = null;
        }
    }
}
