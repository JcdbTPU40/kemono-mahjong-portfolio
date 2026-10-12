using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    //  オーラス バナー ＝ 雀魂スタイル（ローカル演出・配牌直前に出す）
    // ------------------------------------------------------------
    //  設計思想（開発ナレッジ準拠）:
    //   ・純ローカル表示。同期変数を一切持たない（NoVariableSync）。
    //     発火は Mahjong.cs の PrepareDealAnimation（＝同期済み gameState が
    //     GAMESTATE_DEALING になったエッジ）なので、追加同期はゼロ。
    //     「オーラスか」の判定も各クライアントが同期済みの round / leader /
    //     firstLeader から自前で出す（和了演出・宣言演出と同じ流儀）。
    //   ・卓に1本だけ持つ（席ごとには持たない）。卓の全員に同じものを見せるため、
    //     卓中心の上に置いて各クライアントが自分の方へ水平ビルボードする。
    //   ・アニメは RectTransform のスケール／位置 と CanvasGroup.alpha のみ
    //     （Quest基準：Canvas rebuild を起こさない。あがり画面・役満バナーと同方針）。
    //   ・UIはWorld Space Canvas必須（Screen Space Overlayは描画されない）。
    //   ・スクリプト自身は「常時アクティブな親」（リグのルート）に置くこと。
    //     表示のON/OFFは bannerRoot（子）のSetActiveで行う
    //     （非アクティブなUdonBehaviourは外部から叩けない）。
    //   ・文字は画像（筆文字の金グラデ）。UI Text ではグラデが焼けないため、
    //     宣言演出（ロン/ツモ）と同じく焼き込み済みテクスチャを貼る。
    //     画像の縦横比は **実寸から取らない**（NPOTはPOT丸めで比が変わる）。
    //     ビルダーが原寸比 2172:724 = 3:1 で板を作る。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class MahjongAllLastBanner : UdonSharpBehaviour
    {
        [Header("必須参照")]
        [Tooltip("バナーのルート（World Space Canvas を含む子）。非アクティブにして置いておく。")]
        public GameObject bannerRoot;

        [Tooltip("フェード用 CanvasGroup（bannerRoot 配下）")]
        public CanvasGroup canvasGroup;

        [Tooltip("「オーラス」画像の RawImage")]
        public RawImage glyphImage;

        [Tooltip("「オーラス」画像の RectTransform（スケール・ドリフトの対象）")]
        public RectTransform glyphRect;

        [Tooltip("影用の RawImage（任意）。明るい背景でも金文字が沈まないよう後ろに敷く。")]
        public RawImage shadowImage;

        [Tooltip("影用の RectTransform（任意）")]
        public RectTransform shadowRect;

        [Header("素材")]
        [Tooltip("日本語の「オーラス」画像")]
        public Texture glyphJa;

        [Tooltip("英語の「ALL LAST」画像（任意）。未設定なら日本語画像を使う。")]
        public Texture glyphEn;

        [Header("配置")]
        [Tooltip("表示中、自分の方へ水平ビルボードする。置いた向きで固定したいときはOFF。")]
        public bool billboardToLocalPlayer = true;

        [Header("出す条件")]
        [Tooltip("延長（半荘の西入／東風戦の南入）でも毎局出す。サドンデスはどの局も最終局になり得るため既定ON。")]
        public bool showOnSuddenDeath = true;

        [Header("段取り（秒）")]
        [Tooltip("大きく出て等倍へ叩き込むまで")]
        public float enterSeconds = 0.14f;

        [Tooltip("行き過ぎ（少し小さい）から等倍へ戻すまで")]
        public float settleSeconds = 0.10f;

        [Tooltip("等倍で見せている時間")]
        public float holdSeconds = 1.12f;

        [Tooltip("消えるまで（この間に配牌が始まる）")]
        public float exitSeconds = 0.34f;

        [Tooltip("配牌を待たせる時間。既定は「消え始めるまで」＝バナーが引きながら牌が飛び出す。" +
                 "0 にすると配牌を待たせない（バナーと同時に配牌）。")]
        public float dealDelaySeconds = 1.36f;

        [Header("見た目")]
        [Tooltip("出てくるときの倍率（ここから等倍へ叩き込む）")]
        public float enterScale = 2.10f;

        [Tooltip("叩き込みの行き過ぎ倍率（等倍より少し小さく）")]
        public float overshootScale = 0.94f;

        [Tooltip("消えるときの倍率（少し大きくなりながら抜ける）")]
        public float exitScale = 1.08f;

        [Tooltip("保持中にゆっくり上へ流す量（px・Canvas座標）")]
        public float driftDistance = 16.0f;

        [Tooltip("文字の色（素材が金文字なので既定は白＝素材そのまま）")]
        public Color glyphColor = new Color(1.0f, 1.0f, 1.0f, 1.0f);

        [Tooltip("影の色")]
        public Color shadowColor = new Color(0.03f, 0.02f, 0.01f, 0.55f);

        [Tooltip("影のずらし量（px）")]
        public Vector2 shadowOffset = new Vector2(12.0f, -12.0f);

        [Header("サウンド（任意）")]
        [Tooltip("SE再生用 AudioSource。常時アクティブな親（このスクリプトと同じGameObject推奨）に置く。" +
                 "ローカル再生＝SoundsV2の発声キューには乗せない（局跨ぎの取り消しに巻き込まれないため）。")]
        public AudioSource seSource;

        [Tooltip("オーラスのSE（任意）。未設定なら無音。")]
        public AudioClip se;

        [Tooltip("SEの音量（実測ラウドネスから逆算した値を入れる）")]
        [Range(0.0f, 1.0f)]
        public float seVolume = 0.10f;

        // 表示開始時刻。<0 は非表示（Update 早期リターン用の番兵）
        private float startTime_ = -1.0f;

        // glyphRect の素の位置（ドリフトの基準）。Show で1回だけ取る。
        private Vector2 glyphHome_;
        private bool homeCaptured_ = false;

        // 演出の総尺。
        public float GetDuration()
        {
            float d = enterSeconds + settleSeconds + holdSeconds + exitSeconds;
            return d > 0.0f ? d : 0.0f;
        }

        // 配牌演出を待たせる時間。総尺を超えないようにクランプする
        // （超えると牌が出る前にバナーが消えて、ただ間が空くだけになる）。
        public float GetDealDelay()
        {
            float d = dealDelaySeconds;
            if (d < 0.0f) { d = 0.0f; }
            float duration = GetDuration();
            if (d > duration) { d = duration; }
            return d;
        }

        // language: 0=日本語 / それ以外=英語の筆文字（韓国語の筆文字画像は無いので英語を出す）。
        // Mahjong.cs の配牌演出の開始点（PrepareDealAnimation）からのみ呼ぶこと。
        public void Show(int language)
        {
            if (bannerRoot == null || canvasGroup == null || glyphRect == null)
            {
                return;
            }

            if (!homeCaptured_)
            {
                glyphHome_ = glyphRect.anchoredPosition;
                homeCaptured_ = true;
            }

            Texture tex = glyphJa;
            if (language != 0 && glyphEn != null) { tex = glyphEn; }

            if (glyphImage != null)
            {
                glyphImage.texture = tex;
                glyphImage.color = glyphColor;
            }
            if (shadowImage != null)
            {
                shadowImage.texture = tex;
                shadowImage.color = shadowColor;
            }

            canvasGroup.alpha = 0.0f;
            ApplyFrame(0.0f);
            if (billboardToLocalPlayer)
            {
                FaceLocalPlayer();
            }

            bannerRoot.SetActive(true);
            startTime_ = Time.time;

            if (seSource != null && se != null)
            {
                seSource.PlayOneShot(se, seVolume);
            }
        }

        // 局のリセット等で即座に消したいとき用。
        public void HideNow()
        {
            startTime_ = -1.0f;
            if (bannerRoot != null)
            {
                bannerRoot.SetActive(false);
            }
        }

        public bool IsShowing()
        {
            return startTime_ >= 0.0f;
        }

        void Update()
        {
            if (startTime_ < 0.0f)
            {
                return;
            }

            float t = Time.time - startTime_;

            if (t >= GetDuration())
            {
                HideNow();
                return;
            }

            ApplyFrame(t);

            if (billboardToLocalPlayer)
            {
                FaceLocalPlayer();
            }
        }

        // t 秒目の見た目を作る（スケール・ドリフト・アルファ）。
        // Show でも同じ関数を通して1フレーム目を作る＝出た瞬間に素の大きさが見えない。
        private void ApplyFrame(float t)
        {
            float enter = enterSeconds > 0.0f ? enterSeconds : 0.0001f;
            float settle = settleSeconds > 0.0f ? settleSeconds : 0.0001f;
            float exit = exitSeconds > 0.0f ? exitSeconds : 0.0001f;
            float holdEnd = enterSeconds + settleSeconds + holdSeconds;

            float scale;
            float alpha;
            float drift = 0.0f;

            if (t < enterSeconds)
            {
                float k = t / enter;
                scale = Mathf.Lerp(enterScale, overshootScale, k);
                // アルファは叩き込みの前半で入れきる（大きい残像を長く見せない）。
                alpha = Mathf.Clamp01(k * 2.0f);
            }
            else if (t < enterSeconds + settleSeconds)
            {
                float k = (t - enterSeconds) / settle;
                scale = Mathf.Lerp(overshootScale, 1.0f, k);
                alpha = 1.0f;
            }
            else if (t < holdEnd)
            {
                float k = holdSeconds > 0.0f ? (t - enterSeconds - settleSeconds) / holdSeconds : 1.0f;
                scale = 1.0f;
                alpha = 1.0f;
                drift = driftDistance * k;
            }
            else
            {
                float k = Mathf.Clamp01((t - holdEnd) / exit);
                scale = Mathf.Lerp(1.0f, exitScale, k);
                alpha = 1.0f - k;
                drift = driftDistance;
            }

            glyphRect.localScale = new Vector3(scale, scale, 1.0f);
            glyphRect.anchoredPosition = glyphHome_ + new Vector2(0.0f, drift);

            if (shadowRect != null)
            {
                shadowRect.localScale = glyphRect.localScale;
                shadowRect.anchoredPosition = glyphRect.anchoredPosition + shadowOffset;
            }

            canvasGroup.alpha = Mathf.Clamp01(alpha);
        }

        // 自分（ローカルプレイヤー）の方へ水平ビルボード。
        // World Space Canvas は +Z がプレイヤーから遠ざかる向きのとき正読みになる。
        private void FaceLocalPlayer()
        {
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp) || bannerRoot == null)
            {
                return;
            }

            var away = bannerRoot.transform.position - lp.GetPosition();
            away.y = 0.0f;
            if (away.sqrMagnitude > 0.0001f)
            {
                bannerRoot.transform.rotation = Quaternion.LookRotation(away);
            }
        }
    }
}
