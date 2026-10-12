using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    //  立直の打牌演出（雀魂スタイル）
    // ------------------------------------------------------------
    //  役割は2つ:
    //   (1) 横向きに置かれる立直牌が「右からスライドインして止まる」ときの
    //       パラメータ置き場（実際に牌を動かすのは Mahjong.cs 側。牌の配置は
    //       UpdateView が握っているため、そこに乗せないと毎フレーム戻される）。
    //   (2) 止まった瞬間の着地エフェクト（Play）。
    //
    //  設計思想（MahjongAgariEffect と同じ・開発ナレッジ準拠）:
    //   ・純ローカル表示。同期変数は一切持たない（NoVariableSync）。
    //     立直したか＝ reachIndex は Mahjong.cs が同期済みなので、各クライアントが
    //     同じ入力で Play() を叩けば全員同じ演出になる（追加同期ゼロ）。
    //   ・パーティクルは使わず World Space Canvas 上の RawImage を
    //     RectTransform スケール／anchoredPosition／color.a だけで動かす（Quest基準）。
    //   ・スクリプトが載るルートは常時アクティブ。表示ON/OFFは子の fxRoot だけで行う
    //     （非アクティブな UdonBehaviour は外部から叩けない）。
    //   ・和了演出とは別リグにする。一発ロンでは「立直牌の着地」と「和了演出」が
    //     ほぼ同時に走るため、リグを共有すると片方が消える。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class MahjongReachDiscardEffect : UdonSharpBehaviour
    {
        [Header("必須参照")]
        [Tooltip("演出全体のルート。非アクティブにして置いておく（演出中だけONになる）。")]
        public GameObject fxRoot;

        [Tooltip("縦向きレイヤーのルート（World Space Canvas）。演出中は毎フレーム自分の方へ水平ビルボードする。")]
        public Transform billboardRoot;

        [Tooltip("卓面レイヤーのルート（World Space Canvas を寝かせたもの）。リングを卓に貼り付ける。")]
        public Transform groundRoot;

        [Header("パーツ（無いパーツは未割当でOK）")]
        [Tooltip("着地の白閃光。一瞬だけ。")]
        public RawImage flashImage;
        public RectTransform flashRect;

        [Tooltip("卓面を走る衝撃波リング。")]
        public RawImage ringImage;
        public RectTransform ringRect;

        [Tooltip("牌の周りに残る柔らかい光。")]
        public RawImage glowImage;
        public RectTransform glowRect;

        [Tooltip("横方向へ散る粒。0枚でも成立する。")]
        public RawImage[] sparkImages;
        public RectTransform[] sparkRects;

        [Header("色（雀魂の立直＝青白い波）")]
        public Color mainColor = new Color(0.75f, 0.92f, 1.00f, 1.0f);
        public Color subColor = new Color(0.35f, 0.70f, 1.00f, 1.0f);

        [Header("打牌スライド（Mahjong.cs が読む）")]
        [Tooltip("立直牌が右からスライドインする時間（秒）。0以下でスライドなし＝従来どおり即着地。")]
        public float slideSeconds = 0.26f;

        [Tooltip("スライドの開始距離（m）。牌1枚の幅は約0.021m。")]
        public float slideDistance = 0.095f;

        [Tooltip("スライド中に卓面から浮かせる高さ（m）。着地に向けて0へ落ちる。")]
        public float slideLift = 0.008f;

        [Header("着地エフェクト")]
        [Tooltip("演出全体の長さ（秒）。")]
        public float duration = 0.70f;

        [Tooltip("末尾のフェードアウト時間（秒・duration の内数）。")]
        public float fadeOutSeconds = 0.28f;

        [Tooltip("牌の位置から演出中心を持ち上げる高さ（m）。")]
        public float centerHeightOffset = 0.012f;

        [Tooltip("リングを卓面から浮かせる高さ（m）。Zファイト回避。")]
        public float ringHeightOffset = 0.012f;

        [Tooltip("リングの最終スケール倍率。")]
        public float ringScale = 1.0f;

        [Tooltip("粒が飛ぶ距離（Canvas ローカル px）。")]
        public float sparkDistance = 140.0f;

        [Header("SE（任意）")]
        [Tooltip("立直の打牌SE。未割当なら無音。各クライアントでローカル再生される（結果的に全員に聞こえる）。")]
        public AudioClip discardSound;

        [Tooltip("SEをスライド開始と同時に鳴らす。斬撃系のSEは牌の動きに被せた方が合う。OFFにすると着地の瞬間に鳴る。")]
        public bool soundAtSlideStart = true;

        [Tooltip("SE再生用 AudioSource（常時アクティブな親側に置く）。未割当ならSEなし。")]
        public AudioSource seSource;

        [Range(0.0f, 1.0f)]
        public float seVolume = 0.159f;

        // ---- 再生中のローカル状態（同期しない）----
        private float startTime_ = -1.0f;   // <0 = 非表示
        private float[] sparkAngles_;

        // 各パーツの時間配分（秒）。duration を変えても立ち上がりは保つ。
        private const float FLASH_SECONDS = 0.20f;
        private const float RING_SECONDS = 0.55f;
        private const float SPARK_SECONDS = 0.45f;

        void Start()
        {
            // 粒は左右へ低く散らす（牌が卓に着いた飛沫のイメージ。上へは飛ばさない）。
            int n = sparkRects != null ? sparkRects.Length : 0;
            sparkAngles_ = new float[n];
            for (int i = 0; i < n; ++i)
            {
                int div = n > 1 ? n - 1 : 1;
                float u = (float)i / (float)div;                 // 0..1
                bool right = (i % 2) == 0;
                float deg = Mathf.Lerp(8.0f, 62.0f, u);
                sparkAngles_[i] = right ? deg : 180.0f - deg;
            }

            // late joiner 対策：演出中でなければ確実に消しておく。
            if (fxRoot != null && startTime_ < 0.0f)
            {
                fxRoot.SetActive(false);
            }
        }

        // ============================================================
        //  外部API（Mahjong.cs が読む）
        // ============================================================
        public float GetSlideSeconds() { return slideSeconds < 0.0f ? 0.0f : slideSeconds; }
        public float GetSlideDistance() { return slideDistance < 0.0f ? 0.0f : slideDistance; }
        public float GetSlideLift() { return slideLift < 0.0f ? 0.0f : slideLift; }

        // 着地エフェクトの再生。tileWorldPos = 立直牌のワールド位置（着地後）。
        public void Play(Vector3 tileWorldPos)
        {
            if (fxRoot == null) { return; }

            var center = tileWorldPos + new Vector3(0.0f, centerHeightOffset, 0.0f);
            fxRoot.transform.position = center;

            if (billboardRoot != null)
            {
                billboardRoot.position = center;
            }

            if (groundRoot != null)
            {
                // 卓面に寝かせる：X軸-90°で Canvas の +Z が真上を向く＝上から見える向き。
                groundRoot.position = new Vector3(tileWorldPos.x, tileWorldPos.y + ringHeightOffset, tileWorldPos.z);
                groundRoot.rotation = Quaternion.Euler(-90.0f, 0.0f, 0.0f);
            }

            startTime_ = Time.time;
            ApplyFrame(0.0f);

            FaceLocalPlayer();
            fxRoot.SetActive(true);

            // 既定では SE はスライド開始で鳴っているので、ここでは鳴らさない。
            if (!soundAtSlideStart)
            {
                PlaySlideSound();
            }
        }

        // 自分の位置でそのまま再生する（試写・デバッグ用）。
        public void PlayHere()
        {
            Play(transform.position);
        }

        // 打牌SEだけ鳴らす（スライド開始と同時。Mahjong.cs から叩く）。
        public void PlaySlideSound()
        {
            if (seSource == null || discardSound == null) { return; }

            seSource.PlayOneShot(discardSound, seVolume);
        }

        // 局のリセット等で即座に消したいとき用。
        public void HideNow()
        {
            startTime_ = -1.0f;

            if (fxRoot != null)
            {
                fxRoot.SetActive(false);
            }
        }

        // ============================================================
        //  アニメーション（RectTransform と color.a のみ）
        // ============================================================
        void Update()
        {
            if (startTime_ < 0.0f)
            {
                return;
            }

            float t = Time.time - startTime_;

            if (t >= duration)
            {
                HideNow();
                return;
            }

            ApplyFrame(t);
            FaceLocalPlayer();
        }

        private void ApplyFrame(float t)
        {
            // 末尾フェード（全パーツ共通の掛け算）
            float tail = 1.0f;
            float fadeStart = duration - fadeOutSeconds;
            if (fadeOutSeconds > 0.001f && t > fadeStart)
            {
                tail = Mathf.Clamp01(1.0f - (t - fadeStart) / fadeOutSeconds);
            }

            // ---- 閃光：着地の一瞬だけ ----
            if (flashImage != null)
            {
                float f = Mathf.Clamp01(t / FLASH_SECONDS);
                float ease = 1.0f - (1.0f - f) * (1.0f - f);
                if (flashRect != null)
                {
                    float s = Mathf.Lerp(0.30f, 1.40f, ease);
                    flashRect.localScale = new Vector3(s, s, 1.0f);
                }
                SetAlpha(flashImage, Color.white, (1.0f - f) * 0.9f * tail);
            }

            // ---- リング：卓面を走って薄れる ----
            if (ringImage != null)
            {
                float r = Mathf.Clamp01(t / RING_SECONDS);
                float ease = 1.0f - (1.0f - r) * (1.0f - r) * (1.0f - r);
                if (ringRect != null)
                {
                    float s = Mathf.Lerp(0.12f, 1.30f * ringScale, ease);
                    ringRect.localScale = new Vector3(s, s, 1.0f);
                }
                SetAlpha(ringImage, subColor, (1.0f - r) * 0.95f * tail);
            }

            // ---- 牌の周りの光 ----
            if (glowImage != null)
            {
                if (glowRect != null)
                {
                    float s = Mathf.Lerp(0.45f, 0.85f, Mathf.Clamp01(t / 0.25f));
                    glowRect.localScale = new Vector3(s, s, 1.0f);
                }
                SetAlpha(glowImage, mainColor, Mathf.Clamp01(t / 0.06f) * 0.7f * tail);
            }

            // ---- 粒：左右へ低く散る ----
            int n = sparkRects != null ? sparkRects.Length : 0;
            for (int i = 0; i < n; ++i)
            {
                var rect = sparkRects[i];
                if (rect == null) { continue; }

                float p = Mathf.Clamp01(t / SPARK_SECONDS);
                float ease = 1.0f - (1.0f - p) * (1.0f - p) * (1.0f - p);
                float deg = (sparkAngles_ != null && i < sparkAngles_.Length) ? sparkAngles_[i] : 0.0f;
                float rad = deg * Mathf.Deg2Rad;
                float dist = sparkDistance * ease;

                rect.anchoredPosition = new Vector2(Mathf.Cos(rad) * dist, Mathf.Sin(rad) * dist * 0.45f);
                float s = Mathf.Lerp(0.9f, 0.2f, ease);
                rect.localScale = new Vector3(s, s, 1.0f);

                if (sparkImages != null && i < sparkImages.Length && sparkImages[i] != null)
                {
                    SetAlpha(sparkImages[i], mainColor, (1.0f - p) * tail);
                }
            }
        }

        // 色は「基準色 × このフレームのアルファ」。Graphic.color はインスタンス
        // プロパティなのでマテリアル複製は発生しない（Quest配慮）。
        private void SetAlpha(RawImage image, Color baseColor, float alpha)
        {
            image.color = new Color(baseColor.r, baseColor.g, baseColor.b, Mathf.Clamp01(alpha));
        }

        // 縦レイヤーだけ自分の方へ水平ビルボード（卓面のリングは回さない）。
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
