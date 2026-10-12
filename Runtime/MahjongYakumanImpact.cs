using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    //  役満の和了牌の着弾演出（雀魂スタイル）
    // ------------------------------------------------------------
    //  通常の和了演出（MahjongAgariEffect＝光柱が立つタイプ）とは別物で、
    //  役満のときだけ和了牌に重ねて出す。通常の和了は従来どおり。
    //
    //  段取り上の位置（役満）:
    //    ロン/ツモ宣言＋墨しぶき → 勝者カットイン → ドン＋和了牌と手牌の公開
    //      → <b>ここ（着弾）</b>＋打ち上げ（YakumanFly）
    //      → 役満カットイン → あがり画面
    //
    //  打ち上げクリップの最初の1.4秒は牌が高さ1cmで浮くだけの「溜め」なので、
    //  その間をこの着弾演出が埋める。金のバースト（1.28秒）が、彗星トレイルの
    //  点火（89フレーム＝1.483秒）へそのまま繋がるように尺を合わせてある。
    // ------------------------------------------------------------
    //  レイヤーは2枚に分かれる。<b>円いものは卓面に寝ている</b>のが要点で、
    //  ビルボードで立てると別物になる（動画ではリングが牌の手前を横切って
    //  奥へ回り込む＝卓に置かれた水平な円だと分かる）。
    //
    //    groundRoot（卓面に寝かせた World Space Canvas）
    //      + Disc … 牌の足元に開く円盤。急拡大 → ゆっくり退色
    //      + Ring … 細い輪郭リング。最後まで残って外へ広がりながら消える
    //      + Sun  … 金の放射状バースト（第2波）
    //    billboardRoot（プレイヤーの方を向く World Space Canvas）
    //      + Streak … 斜め上から飛び込む細い光条（着弾のきっかけ）
    //      + Flash  … 着弾の白閃光
    //      + Mote0..N … 第2波で舞い上がる粒
    // ------------------------------------------------------------
    //  色は固定。コスメ（PlayerData "kmj_agari_fx"）では変えない。
    //  代わりに動画どおり時間で色が動く:
    //    円盤 … 白に近い金 → 橙 → 淡い琥珀
    //    リング… 橙 → 淡い黄 → 淡いピンク白（終盤はほとんど色が抜ける）
    //    バースト… 白に近い芯 → 金
    // ------------------------------------------------------------
    //  設計（既存の和了演出と同じ流儀）:
    //   ・純ローカル表示。同期変数ゼロ。誰がどの牌で和了ったかは Mahjong.cs が
    //     既に同期しているので、各クライアントが同じ入力で Play() を叩けばよい。
    //   ・スクリプトは常時アクティブな親に置く。表示ON/OFFは子の fxRoot だけ。
    //   ・RectTransform のスケール／位置／color しか動かさない（Quest基準）。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class MahjongYakumanImpact : UdonSharpBehaviour
    {
        [Header("必須参照")]
        [Tooltip("演出全体のルート。非アクティブで置いておく（演出中だけONになる）。")]
        public GameObject fxRoot;

        [Tooltip("卓面レイヤーのルート（World Space Canvas を寝かせたもの）。円いものは全部こちら。")]
        public Transform groundRoot;

        [Tooltip("縦向きレイヤーのルート。毎フレーム自分の方へ水平ビルボードする。")]
        public Transform billboardRoot;

        [Header("レイヤー（卓面に寝ている）")]
        public RawImage discImage;
        public RectTransform discRect;

        public RawImage ringImage;
        public RectTransform ringRect;

        public RawImage sunImage;
        public RectTransform sunRect;

        [Header("レイヤー（縦・ビルボード）")]
        public RawImage streakImage;
        public RectTransform streakRect;

        public RawImage flashImage;
        public RectTransform flashRect;

        public RawImage[] moteImages;
        public RectTransform[] moteRects;

        [Header("色（固定。時間で移り変わる）")]
        [Tooltip("円盤：着弾直後。白に近い金。")]
        public Color discColorHot = new Color(1.00f, 0.94f, 0.75f, 1.0f);

        [Tooltip("円盤：主部。橙。")]
        public Color discColorWarm = new Color(1.00f, 0.60f, 0.16f, 1.0f);

        [Tooltip("円盤：退色。淡い琥珀。")]
        public Color discColorFade = new Color(1.00f, 0.79f, 0.42f, 1.0f);

        [Tooltip("リング：序盤。橙。")]
        public Color ringColorEarly = new Color(1.00f, 0.70f, 0.28f, 1.0f);

        [Tooltip("リング：中盤。淡い黄。")]
        public Color ringColorMid = new Color(1.00f, 0.91f, 0.66f, 1.0f);

        [Tooltip("リング：終盤。淡いピンク白（動画では最後ほとんど色が抜ける）。")]
        public Color ringColorLate = new Color(0.91f, 0.77f, 0.69f, 1.0f);

        [Tooltip("第2波：芯。白に近い。")]
        public Color sunColorCore = new Color(1.00f, 0.97f, 0.86f, 1.0f);

        [Tooltip("第2波：外側。金。")]
        public Color sunColorGold = new Color(1.00f, 0.77f, 0.13f, 1.0f);

        [Tooltip("着弾の閃光。")]
        public Color flashColor = new Color(1.00f, 1.00f, 1.00f, 1.0f);

        [Tooltip("飛び込む光条。動画では黄緑がかっている。")]
        public Color streakColor = new Color(0.89f, 1.00f, 0.55f, 1.0f);

        [Tooltip("舞い上がる粒。")]
        public Color moteColor = new Color(1.00f, 0.95f, 0.82f, 1.0f);

        [Header("SE")]
        public AudioSource seSource;

        [Tooltip("着弾（0秒）。未設定なら鳴らない。")]
        public AudioClip impactSound;
        public float impactVolume = 0.085f;

        [Tooltip("第2波の金バースト。未設定なら鳴らない。")]
        public AudioClip burstSound;
        public float burstVolume = 0.075f;

        [Header("段取りの秒数")]
        [Tooltip("演出全体の長さ。打ち上げクリップ（2.08秒）より短くすること。")]
        public float duration = 1.90f;

        [Tooltip("飛び込む光条が中心へ届くまで。")]
        public float streakSeconds = 0.09f;

        [Tooltip("着弾の白閃光。")]
        public float flashSeconds = 0.18f;

        [Tooltip("円盤が消えるまで。")]
        public float discSeconds = 1.30f;

        [Tooltip("円盤が最大まで開くまで（この後はゆっくり広がりながら薄れる）。")]
        public float discOpenSeconds = 0.16f;

        [Tooltip("細いリングが消えるまで。")]
        public float ringSeconds = 1.90f;

        [Tooltip("第2波（金のバースト）が始まる時刻。彗星トレイルの点火より少し前に置く。")]
        public float sunStartSeconds = 1.28f;

        [Tooltip("第2波の長さ。")]
        public float sunSeconds = 0.62f;

        [Header("位置と大きさ")]
        [Tooltip("縦レイヤーの中心を牌からどれだけ上げるか（m）。")]
        public float centerHeightOffset = 0.012f;

        [Tooltip("卓面レイヤーを牌の足元からどれだけ浮かせるか（m）。0だと卓面と重なってちらつく。")]
        public float groundHeightOffset = 0.004f;

        [Tooltip("全レイヤーに掛かる倍率。")]
        public float impactScale = 1.0f;

        [Tooltip("光条が飛んでくる向き（度）。0=右、90=上。既定は右上から。")]
        public float streakAngle = 34.0f;

        [Tooltip("光条の飛来距離（Canvas単位）。")]
        public float streakDistance = 520.0f;

        [Tooltip("粒が舞い上がる距離（Canvas単位）。")]
        public float moteDistance = 240.0f;

        // ---- 再生中のローカル状態（同期しない）----
        private float startTime_ = -1.0f;
        private float[] moteAngles_;
        private float[] moteSpeeds_;
        private bool burstFired_ = false;

        void Start()
        {
            EnsureMoteTable();
            if (fxRoot != null) { fxRoot.SetActive(false); }
        }

        // 粒の飛ぶ向きは固定（Random を使わない＝各クライアントで同じ絵になる）。
        private void EnsureMoteTable()
        {
            int n = moteRects != null ? moteRects.Length : 0;
            if (moteAngles_ != null && moteAngles_.Length == n) { return; }

            moteAngles_ = new float[n];
            moteSpeeds_ = new float[n];
            for (int i = 0; i < n; ++i)
            {
                // 上向き（90度）を中心に扇状。真横や下へは飛ばさない。
                float k = (n <= 1) ? 0.5f : ((float)i / (float)(n - 1));
                moteAngles_[i] = 30.0f + 120.0f * k;
                moteSpeeds_[i] = 0.65f + 0.35f * ((i % 3) * 0.5f);
            }
        }

        // ------------------------------------------------------------
        //  再生
        // ------------------------------------------------------------

        /// <summary>役満の和了牌の位置で着弾演出を出す。</summary>
        public void Play(Vector3 tileWorldPos)
        {
            if (fxRoot == null) { return; }

            EnsureMoteTable();

            fxRoot.transform.position = tileWorldPos;

            if (billboardRoot != null)
            {
                billboardRoot.position = tileWorldPos + new Vector3(0.0f, centerHeightOffset, 0.0f);
            }

            if (groundRoot != null)
            {
                // 卓面に寝かせる：X軸-90°で Canvas の +Z が真上を向く＝上から見える向き。
                // LookRotation を使わず絶対姿勢で固定するので、卓の向きに依存しない。
                groundRoot.position = new Vector3(
                    tileWorldPos.x, tileWorldPos.y + groundHeightOffset, tileWorldPos.z);
                groundRoot.rotation = Quaternion.Euler(-90.0f, 0.0f, 0.0f);
            }

            startTime_ = Time.time;
            burstFired_ = false;

            ApplyFrame(0.0f);
            FaceLocalPlayer();
            fxRoot.SetActive(true);

            if (seSource != null && impactSound != null)
            {
                seSource.PlayOneShot(impactSound, impactVolume);
            }
        }

        /// <summary>その場で出す（メインメニューの試写用）。</summary>
        public void PlayHere()
        {
            Play(transform.position);
        }

        /// <summary>局のリセット等で即座に消す。</summary>
        public void HideNow()
        {
            startTime_ = -1.0f;
            burstFired_ = false;
            if (fxRoot != null) { fxRoot.SetActive(false); }
        }

        /// <summary>
        /// 指定した時刻の絵をそのまま作る。エディタのプレビュー専用。
        /// 実行時は Update が回すので、こちらは使わない。
        /// </summary>
        public void SampleAt(float t)
        {
            EnsureMoteTable();
            ApplyFrame(t);
        }

        void Update()
        {
            if (startTime_ < 0.0f) { return; }

            float t = Time.time - startTime_;
            if (t >= Mathf.Max(0.05f, duration))
            {
                HideNow();
                return;
            }

            FaceLocalPlayer();
            ApplyFrame(t);

            // 第2波のSEは時刻で1回だけ。段の頭に合わせる。
            if (!burstFired_ && t >= sunStartSeconds)
            {
                burstFired_ = true;
                if (seSource != null && burstSound != null)
                {
                    seSource.PlayOneShot(burstSound, burstVolume);
                }
            }
        }

        // ------------------------------------------------------------
        //  1フレームぶんの見た目
        // ------------------------------------------------------------
        private void ApplyFrame(float t)
        {
            float scale = Mathf.Max(0.05f, impactScale);

            // ---- 飛び込む光条：外から中心へ。届いた瞬間に消える ----
            if (streakImage != null)
            {
                float s = Mathf.Clamp01(t / Mathf.Max(0.01f, streakSeconds));
                float ease = s * s;                                   // 加速しながら突っ込む
                if (streakRect != null)
                {
                    float rad = streakAngle * Mathf.Deg2Rad;
                    float dist = streakDistance * scale * (1.0f - ease);
                    streakRect.anchoredPosition = new Vector2(
                        Mathf.Cos(rad) * dist, Mathf.Sin(rad) * dist);
                    // 進行方向へ寝かせる（テクスチャは縦長なので -90 度ぶんずらす）。
                    streakRect.localRotation = Quaternion.Euler(0.0f, 0.0f, streakAngle - 90.0f);
                    streakRect.localScale = new Vector3(scale, scale * Mathf.Lerp(1.0f, 0.35f, ease), 1.0f);
                }
                Paint(streakImage, streakColor, (1.0f - s) * 0.9f);
            }

            // ---- 着弾の白閃光：一瞬で膨らんで消える ----
            if (flashImage != null)
            {
                float f = Mathf.Clamp01((t - streakSeconds * 0.7f) / Mathf.Max(0.01f, flashSeconds));
                float ease = 1.0f - (1.0f - f) * (1.0f - f);
                if (flashRect != null)
                {
                    float s = Mathf.Lerp(0.20f, 1.35f, ease) * scale;
                    flashRect.localScale = new Vector3(s, s, 1.0f);
                }
                float a = (t < streakSeconds * 0.7f) ? 0.0f : (1.0f - f);
                Paint(flashImage, flashColor, a);
            }

            // ---- 円盤：一気に開いて、あとはゆっくり広がりながら薄れる ----
            if (discImage != null)
            {
                float d = Mathf.Clamp01(t / Mathf.Max(0.05f, discSeconds));
                float open = Mathf.Clamp01(t / Mathf.Max(0.01f, discOpenSeconds));
                float openEase = 1.0f - (1.0f - open) * (1.0f - open) * (1.0f - open);
                if (discRect != null)
                {
                    // 開ききったあとも 1.0→1.45 までゆっくり伸ばす（動画の「じわっと広がる」感）。
                    float s = (Mathf.Lerp(0.25f, 1.0f, openEase) + 0.45f * d) * scale;
                    discRect.localScale = new Vector3(s, s, 1.0f);
                }
                // 白に近い金 →（0.15で）橙 → 淡い琥珀。
                Paint(discImage, Lerp3(discColorHot, discColorWarm, discColorFade, d, 0.15f),
                    (1.0f - d) * (1.0f - d) * 0.95f);
            }

            // ---- 細いリング：最後まで残って外へ消える ----
            if (ringImage != null)
            {
                float r = Mathf.Clamp01(t / Mathf.Max(0.05f, ringSeconds));
                float ease = 1.0f - (1.0f - r) * (1.0f - r) * (1.0f - r);
                if (ringRect != null)
                {
                    float s = Mathf.Lerp(0.30f, 1.80f, ease) * scale;
                    ringRect.localScale = new Vector3(s, s, 1.0f);
                }
                // 橙 →（0.45で）淡い黄 → 淡いピンク白。動画の「色が抜けていく」ところ。
                Paint(ringImage, Lerp3(ringColorEarly, ringColorMid, ringColorLate, r, 0.45f),
                    Mathf.Clamp01(t / 0.18f) * (1.0f - r) * 0.75f);
            }

            // ---- 第2波：金の放射バースト ----
            if (sunImage != null)
            {
                float u = (t - sunStartSeconds) / Mathf.Max(0.05f, sunSeconds);
                if (u <= 0.0f || u >= 1.0f)
                {
                    Paint(sunImage, sunColorGold, 0.0f);
                }
                else
                {
                    // 前半0.22で開ききり、あとは回りながら薄れる。
                    float open = Mathf.Clamp01(u / 0.22f);
                    float openEase = 1.0f - (1.0f - open) * (1.0f - open) * (1.0f - open);
                    if (sunRect != null)
                    {
                        float s = Mathf.Lerp(0.15f, 1.0f, openEase) * (1.0f + 0.35f * u) * scale;
                        sunRect.localScale = new Vector3(s, s, 1.0f);
                        sunRect.localRotation = Quaternion.Euler(0.0f, 0.0f, u * 26.0f);
                    }
                    // 出た瞬間は白に近く、開くにつれて金へ落ちる。
                    Paint(sunImage, LerpC(sunColorCore, sunColorGold, u), open * (1.0f - u));
                }
            }

            // ---- 粒：第2波と一緒に舞い上がる ----
            int n = moteRects != null ? moteRects.Length : 0;
            for (int i = 0; i < n; ++i)
            {
                var rect = moteRects[i];
                if (rect == null) { continue; }

                float u = (t - sunStartSeconds) / Mathf.Max(0.05f, sunSeconds);
                float a = 0.0f;

                if (u > 0.0f && u < 1.0f)
                {
                    float ease = 1.0f - (1.0f - u) * (1.0f - u);
                    float deg = (moteAngles_ != null && i < moteAngles_.Length) ? moteAngles_[i] : 90.0f;
                    float spd = (moteSpeeds_ != null && i < moteSpeeds_.Length) ? moteSpeeds_[i] : 1.0f;
                    float rad = deg * Mathf.Deg2Rad;
                    float dist = moteDistance * scale * ease * spd;

                    rect.anchoredPosition = new Vector2(Mathf.Cos(rad) * dist, Mathf.Sin(rad) * dist);
                    float s = Mathf.Lerp(1.0f, 0.30f, ease) * scale;
                    rect.localScale = new Vector3(s, s, 1.0f);

                    // 出だしに短い立ち上がりを入れる。いきなり全開で出すと、
                    // 第2波の頭で粒だけが「点いた」ように浮いて見える。
                    a = Mathf.Clamp01(u / 0.10f) * (1.0f - u) * 0.95f;
                }

                if (moteImages != null && i < moteImages.Length && moteImages[i] != null)
                {
                    Paint(moteImages[i], moteColor, a);
                }
            }
        }

        // 色は「そのフレームの色 × アルファ」。Graphic.color はインスタンス
        // プロパティなのでマテリアル複製は起きない（Quest配慮）。
        private void Paint(RawImage image, Color rgb, float alpha)
        {
            image.color = new Color(rgb.r, rgb.g, rgb.b, Mathf.Clamp01(alpha));
        }

        // 2色の線形補間。Color.Lerp を使わず成分計算にしてあるのは、
        // Udon の extern を1つ減らせて意図もはっきりするため。
        private Color LerpC(Color a, Color b, float t)
        {
            t = Mathf.Clamp01(t);
            return new Color(
                a.r + (b.r - a.r) * t,
                a.g + (b.g - a.g) * t,
                a.b + (b.b - a.b) * t,
                1.0f);
        }

        // 3色を mid で折り返して繋ぐ。0→a、mid→b、1→c。
        private Color Lerp3(Color a, Color b, Color c, float t, float mid)
        {
            mid = Mathf.Clamp(mid, 0.01f, 0.99f);
            if (t <= mid) { return LerpC(a, b, t / mid); }
            return LerpC(b, c, (t - mid) / (1.0f - mid));
        }

        // 縦レイヤーだけ自分の方へ水平ビルボード（卓面のレイヤーは回さない）。
        private void FaceLocalPlayer()
        {
            if (billboardRoot == null) { return; }

            var lp = Networking.LocalPlayer;
            if (lp == null) { return; }

            var away = billboardRoot.position - lp.GetPosition();
            away.y = 0.0f;
            if (away.sqrMagnitude > 0.0001f)
            {
                billboardRoot.rotation = Quaternion.LookRotation(away);
            }
        }
    }
}
