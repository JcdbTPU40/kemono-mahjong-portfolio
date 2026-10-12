using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    //  役満確定／役満チャンス バナー ＝ 雀魂スタイル（ローカル演出）
    // ------------------------------------------------------------
    //  設計思想（開発ナレッジ準拠）:
    //   ・純ローカル表示。同期変数を一切持たない（NoVariableSync）。
    //     Mahjong.cs が「自席(entrySelf)の打牌で役満聴牌に入った」ことを
    //     各クライアントのローカル判定で検出して Show() を叩く。
    //     他クライアントでは Show() 自体が呼ばれないため、他プレイヤー
    //     には一切見えない（待ち牌表示・手牌秘匿と同じ思想）。
    //   ・アニメは RectTransform スケールと CanvasGroup.alpha のみ
    //     （Quest基準：Canvas rebuild／パーティクル回避。あがり画面と同方針）。
    //   ・UIはWorld Space Canvas必須（Screen Space Overlayは描画されない）。
    //   ・スクリプト自身は「常時アクティブな親」（卓ルート等）に置くこと。
    //     表示のON/OFFは bannerRoot（子）のSetActiveで行う。
    //     （非アクティブなUdonBehaviourは外部から叩けないため）
    //   ・表示中は毎フレーム自分の方へ水平ビルボードする
    //     （Mahjong.cs の statusTransform_ と同じ流儀）。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class MahjongYakumanBanner : UdonSharpBehaviour
    {
        [Header("必須参照")]
        [Tooltip("バナーのルート（World Space Canvas を含む子）。非アクティブにして置いておく。")]
        public GameObject bannerRoot;

        [Tooltip("フェード用 CanvasGroup（bannerRoot 配下）")]
        public CanvasGroup canvasGroup;

        [Tooltip("ポップ（スケール）対象の RectTransform。テキストと背景をまとめた子（Canvas直下推奨）。")]
        public RectTransform popTarget;

        [Tooltip("「役満確定」「役満チャンス」を出す Text")]
        public Text label;

        [Header("配置（任意）")]
        [Tooltip("固定表示アンカー。割り当てると「席0で見たときのこの位置・向き」に表示し、他の席では卓中心で90°ずつ回した同じ相対位置に出す。エディタでこのTransformを動かせば表示場所を自由に決められる。未割当なら従来どおり手牌中央の上方に自動配置。")]
        public Transform placementAnchor;

        [Tooltip("表示中、自分の方へ水平ビルボードする。アンカーの向きのまま固定したいときはOFF。")]
        public bool billboardToLocalPlayer = true;

        [Header("サウンド（任意）")]
        [Tooltip("SE再生用 AudioSource。常時アクティブな親（このスクリプトと同じGameObject推奨）に置く。表示と同じくローカル再生＝自分にしか聞こえない（グローバル発声キューに乗せると情報リークになるため SoundsV2 は使わない）。")]
        public AudioSource seSource;

        [Tooltip("「役満確定」のSE。未設定ならチャンス用で代用")]
        public AudioClip kakuteiSe;

        [Tooltip("「役満チャンス」のSE。未設定なら確定用で代用")]
        public AudioClip chanceSe;

        [Tooltip("旧データ互換用の共通音量。個別値が0以下のときだけ使用。")]
        [Range(0.0f, 1.0f)]
        public float seVolume = 0.9f;

        [Tooltip("役満確定SEの測定済み基準音量。")]
        [Range(0.0f, 1.0f)]
        public float kakuteiVolume = 0.0616f;

        [Tooltip("役満チャンスSEの測定済み基準音量。")]
        [Range(0.0f, 1.0f)]
        public float chanceVolume = 0.0956f;

        [Header("見た目（任意）")]
        [Tooltip("確定時の文字色（雀魂の金文字風）")]
        public Color kakuteiColor = new Color(1.0f, 0.84f, 0.25f, 1.0f);

        [Tooltip("チャンス時の文字色")]
        public Color chanceColor = new Color(1.0f, 0.55f, 0.2f, 1.0f);

        [Tooltip("表示時間（秒）。「一瞬だけ」出してフェードアウトする")]
        public float duration = 2.4f;

        [Tooltip("フェードアウトにかける時間（秒・durationの末尾に含む）")]
        public float fadeOutSeconds = 0.6f;

        [Tooltip("鼓動パルスの振幅（0で無効）。表示保持中にドクン…と脈打たせる")]
        public float pulseAmount = 0.04f;

        [Tooltip("鼓動パルスの速さ（Hz）")]
        public float pulseHz = 1.4f;

        // 表示開始時刻。<0 は非表示（Update 早期リターン用の番兵。負intの(uint)化問題とは無関係のfloat）
        private float startTime_ = -1.0f;

        // ポップイン演出の時間配分（秒）
        private const float POP_IN_SECONDS = 0.16f;      // 大→小に叩き込む
        private const float POP_SETTLE_SECONDS = 0.12f;  // 行き過ぎ(0.92)から1.0へ戻す
        private const float FADE_IN_SECONDS = 0.08f;

        // level: 1=役満チャンス / 2=役満確定。worldPos/worldRot: バナーのワールド位置・向き。
        // lang: 卓の表示言語（0=日本語 / 1=英語 / 2=韓国語 / 3=簡体字 / 4=繁体字。呼び出し元の Mahjong.language を int で渡す）。
        // 向きは billboardToLocalPlayer=ON なら毎フレーム上書きされる（OFFなら固定）。
        // Mahjong.cs（自席判定済みのローカルコード）からのみ呼ぶこと。
public void Show(int level, Vector3 worldPos, Quaternion worldRot, int lang)
        {
            if (bannerRoot == null || canvasGroup == null || popTarget == null)
            {
                return;
            }

            if (label != null)
            {
                // 字の大きさは Text の Best Fit 任せ（800x200 の枠で YAKUMAN CHANCE も1行に収まる）。
                if (lang == 1) { label.text = level >= 2 ? "YAKUMAN!" : "YAKUMAN CHANCE"; }
                else if (lang == 2) { label.text = level >= 2 ? "역만 확정" : "역만 찬스"; }
                else if (lang == 3) { label.text = level >= 2 ? "役满确定" : "役满机会"; }
                else if (lang == 4) { label.text = level >= 2 ? "役滿確定" : "役滿機會"; }
                else { label.text = level >= 2 ? "役満確定" : "役満チャンス"; }
                label.color = level >= 2 ? kakuteiColor : chanceColor;
            }

            bannerRoot.transform.position = worldPos;
            bannerRoot.transform.rotation = worldRot;
            canvasGroup.alpha = 0.0f;
            popTarget.localScale = new Vector3(2.4f, 2.4f, 1.0f);
            if (billboardToLocalPlayer)
            {
                FaceLocalPlayer();
            }
            bannerRoot.SetActive(true);
            startTime_ = Time.time;

            // クリップごとの測定値を使い、片方未設定時は代用側の音量も使う。
            if (seSource != null)
            {
                AudioClip clip;
                float volume;
                if (level >= 2)
                {
                    clip = kakuteiSe;
                    volume = kakuteiVolume;
                    if (clip == null)
                    {
                        clip = chanceSe;
                        volume = chanceVolume;
                    }
                }
                else
                {
                    clip = chanceSe;
                    volume = chanceVolume;
                    if (clip == null)
                    {
                        clip = kakuteiSe;
                        volume = kakuteiVolume;
                    }
                }

                if (clip != null)
                {
                    seSource.PlayOneShot(clip, volume > 0.0f ? volume : seVolume);
                }
            }
        }

        // 局のリセット等で即座に消したいとき用（未使用でも害なし）
        public void HideNow()
        {
            startTime_ = -1.0f;
            if (bannerRoot != null)
            {
                bannerRoot.SetActive(false);
            }
        }

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

            // --- スケール：ポップイン（2.4→0.92→1.0）＋保持中の鼓動パルス ---
            float scale;
            if (t < POP_IN_SECONDS)
            {
                scale = Mathf.Lerp(2.4f, 0.92f, t / POP_IN_SECONDS);
            }
            else if (t < POP_IN_SECONDS + POP_SETTLE_SECONDS)
            {
                scale = Mathf.Lerp(0.92f, 1.0f, (t - POP_IN_SECONDS) / POP_SETTLE_SECONDS);
            }
            else
            {
                // 心拍数を上げさせる仕様：保持中にわずかに脈打たせる
                float pulse = Mathf.Sin((t - POP_IN_SECONDS - POP_SETTLE_SECONDS) * pulseHz * 2.0f * Mathf.PI);
                scale = 1.0f + Mathf.Max(0.0f, pulse) * pulseAmount;
            }
            popTarget.localScale = new Vector3(scale, scale, 1.0f);

            // --- アルファ：フェードイン → 保持 → フェードアウト ---
            float alpha = 1.0f;
            if (t < FADE_IN_SECONDS)
            {
                alpha = t / FADE_IN_SECONDS;
            }
            float fadeStart = duration - fadeOutSeconds;
            if (t > fadeStart && fadeOutSeconds > 0.0f)
            {
                alpha = 1.0f - (t - fadeStart) / fadeOutSeconds;
            }
            canvasGroup.alpha = Mathf.Clamp01(alpha);

            if (billboardToLocalPlayer)
            {
                FaceLocalPlayer();
            }
        }

        // 自分（ローカルプレイヤー）の方へ水平ビルボード。
        // World Space Canvas は +Z がプレイヤーから遠ざかる向きのとき正読みになる。
        private void FaceLocalPlayer()
        {
            var lp = Networking.LocalPlayer;
            if (lp == null || bannerRoot == null)
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
