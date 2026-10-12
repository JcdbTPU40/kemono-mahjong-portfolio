using UdonSharp;
using UnityEngine;
using VRC.SDK3.Persistence;
using VRC.SDKBase;
using VRC.Udon;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    // 軽量モード（パフォーマンスモード）スイッチ
    //
    // ■ 役割
    //   壁のスイッチ（performancemode_switch）を押した本人のクライアントだけで、
    //   描画負荷の高い装飾を消す。ネットワーク同期は一切しない＝押した人だけが軽くなる。
    //   （負荷はクライアント個々の事情なので、他人の見た目を変えてはいけない。）
    //
    // ■ 何を消すか（2026-08-20 の実測で「重い」と確認できたものだけ）
    //   1) foliageRenderers … 葉・草・花・茂み・木。
    //      PlantWind / GrassWind / BushWind は Cull Off ＋ clip() の
    //      アルファテスト（Queue=AlphaTest 2450）。Quest のタイル型GPUでは
    //      早期Z棄却が効かず、重なった葉の枚数だけ塗り直しになる（オーバードロー）。
    //      「葉にカメラを向けるとFPSが落ちる」の主犯はこれ。三角形数ではなく塗り負荷。
    //      木の幹(BarkDetail)も同じ木の一部として一緒に消す＝葉だけ消えて幹が浮く事故を防ぐ。
    //   2) particleObjects … 街灯/ランタンの LampParticle。
    //      Particles/Standard Unlit（モバイル版ではない）× maxParticles 1000 が10基。
    //      パーティクルは半透明＝これも塗り負荷。GameObject ごと落として
    //      シミュレーション自体を止める（Renderer だけ切っても計算は走るため）。
    //   3) glassRenderers … ランタン/街灯のガラス。Standard・Silent/Filamented の
    //      半透明(Queue>=3000)。枚数は少ないが1枚あたりが重く、近づくと画面を覆う。
    //   4) extraRenderers … 後から手で足す枠（自動収集の対象外を個別に指定する用）。
    //
    // ■ 消し方の方針：GameObject ではなく Renderer.enabled を切る
    //   SetActive(false) はコライダーや子の挙動まで巻き添えにする（木に当たり判定が
    //   ある場合、軽量モードの人だけすり抜けて位置がズレる）。描画だけ止めれば
    //   GPU負荷は同じだけ下がるので、パーティクル以外は Renderer.enabled で切る。
    //
    // ■ 永続化
    //   PlayerData("kmj_perf_mode")：0=通常 / 1=軽量。クロスインスタンスで復元される。
    //   PlayerData の復元は OnPlayerRestored 後なので、それより前の書き込みは
    //   保留（pending）して復元後に流す（MahjongVoiceSelector と同じ作法）。
    //
    // ■ 使い方
    //   Tools > Kemono Mahjong > Build Performance Mode  で
    //   performancemode_switch へ本コンポーネントを設置＋対象を自動収集する。
    //   カテゴリ単位で切りたくない物が出たら hideFoliage 等のチェックを外す。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class PerformanceModeController : UdonSharpBehaviour
    {
        // PlayerData キー。0=通常品質 / 1=軽量モード。
        public const string PERF_MODE_KEY = "kmj_perf_mode";

        [Header("スイッチのランプ（任意）")]
        [Tooltip("通常品質のときに表示するオブジェクト（装飾が出ている＝Effect On）。")]
        public GameObject onIndicator;
        [Tooltip("軽量モードのときに表示するオブジェクト（装飾を消している＝Effect Off）。")]
        public GameObject offIndicator;

        [Header("カテゴリ別の有効/無効（切りたくないものを外す）")]
        public bool hideFoliage = true;
        public bool hideParticles = true;
        public bool hideGlass = true;
        public bool hideExtra = true;

        [Header("対象（Build Performance Mode が自動収集）")]
        [Tooltip("葉・草・花・茂み・木。アルファテスト＋Cull Off で塗り負荷が高い。")]
        public Renderer[] foliageRenderers;
        [Tooltip("パーティクル。GameObject ごと落として計算も止める。")]
        public GameObject[] particleObjects;
        [Tooltip("半透明ガラス（Queue>=3000）。")]
        public Renderer[] glassRenderers;
        [Tooltip("自動収集の対象外を手で足す枠。")]
        public Renderer[] extraRenderers;

        // 現在の状態。true=軽量モード。
        private bool lowMode_ = false;
        // 自分の PlayerData が復元済みか。復元前の書き込みは消える恐れがあるためガード。
        private bool restored_ = false;
        // 復元前に切り替えた場合の保留フラグ（最後の操作が勝つ）。
        private bool pendingSave_ = false;

        void Start()
        {
            // シーン保存時の状態に関わらず、起動直後は必ず「通常品質」に揃える。
            // （Build/検証中にレンダラーを切ったまま保存してしまった時の保険）
            Apply();
        }

        public override void OnPlayerRestored(VRCPlayerApi player)
        {
            if (player == null || !player.isLocal) { return; }

            restored_ = true;

            if (pendingSave_)
            {
                // 復元前に押されていた＝そちらが本人の最新の意思。保存し直す。
                pendingSave_ = false;
                PlayerData.SetInt(PERF_MODE_KEY, lowMode_ ? 1 : 0);
                return;
            }

            int v;
            if (PlayerData.TryGetInt(player, PERF_MODE_KEY, out v))
            {
                bool low = v != 0;
                if (low != lowMode_)
                {
                    lowMode_ = low;
                    Apply();
                }
            }
        }

        // 壁のスイッチ本体（このオブジェクトのコライダー）を押したとき。
        public override void Interact()
        {
            Toggle();
        }

        // --- SendCustomEvent から呼べるエントリ（メニュー等から使う想定） ---
        public void Toggle() { SetLowMode(!lowMode_); }
        public void SetNormalQuality() { SetLowMode(false); }
        public void SetLowQuality() { SetLowMode(true); }

        public bool IsLowMode() { return lowMode_; }

        public void SetLowMode(bool low)
        {
            lowMode_ = low;
            Apply();
            Save();
        }

        // 現在の状態を全対象へ反映する。
        // カテゴリのチェックが外れているものは「常に表示」に戻す
        //  ＝インスペクタでチェックを外した直後に消えっぱなしにならない。
        private void Apply()
        {
            bool show = !lowMode_;

            SetRenderers(foliageRenderers, hideFoliage ? show : true);
            SetRenderers(glassRenderers, hideGlass ? show : true);
            SetRenderers(extraRenderers, hideExtra ? show : true);
            SetObjects(particleObjects, hideParticles ? show : true);

            if (onIndicator != null) { onIndicator.SetActive(show); }
            if (offIndicator != null) { offIndicator.SetActive(!show); }
        }

        private void SetRenderers(Renderer[] targets, bool visible)
        {
            if (targets == null) { return; }
            for (int i = 0; i < targets.Length; ++i)
            {
                var r = targets[i];
                if (r == null) { continue; }
                if (r.enabled != visible) { r.enabled = visible; }
            }
        }

        private void SetObjects(GameObject[] targets, bool visible)
        {
            if (targets == null) { return; }
            for (int i = 0; i < targets.Length; ++i)
            {
                var go = targets[i];
                if (go == null) { continue; }
                if (go.activeSelf != visible) { go.SetActive(visible); }
            }
        }

        private void Save()
        {
            if (!restored_)
            {
                pendingSave_ = true;
                return;
            }
            PlayerData.SetInt(PERF_MODE_KEY, lowMode_ ? 1 : 0);
        }
    }
}
