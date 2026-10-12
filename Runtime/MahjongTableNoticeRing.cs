using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace SuzuFactory.AozoraMahjong
{
    /// <summary>
    /// 卓を囲む案内リングの出し入れ。
    ///
    /// 2026-09-13 に卓の ENTRY / CPU を封鎖したので、卓へ直接来た人には押せるものが何も無い。
    /// 「この卓を使うにはメインメニューで操作してください」を輪っか状に流して、
    /// 何も起きない理由と次にやることを、その場で読ませる。
    ///
    /// ■ 負荷について
    ///   文字はビルド時にテクスチャへ焼き、流すのは TableNoticeRing シェーダーが
    ///   _Time.y から自走する＝流れている間の CPU コストはゼロ。
    ///   ここでやるのは「出すか消すか」の判定だけで、しかも1秒に1回しか見ない。
    ///   卓が近接カリングで寝れば、このコンポーネントごと止まる。
    ///
    /// ■ 出す条件
    ///   卓がどこにも登録されていない（＝MainMenu から始めるしかない）ときだけ。
    ///   着席誘導中・対局中・強制リセット待ちは IsTableIndexInUse が true になるので消える。
    ///   卓に直接座れる人（スタッフ／インスタンスマスター、または封鎖を戻した状態）には出さない
    ///   ——その人にとっては嘘になるので。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class MahjongTableNoticeRing : UdonSharpBehaviour
    {
        [Tooltip("この案内が属する卓。")]
        public Mahjong table;
        [Tooltip("マッチング管理。未割当なら案内は出さない（判定材料が無いため）。")]
        public MatchmakingManager matchmaking;
        [Tooltip("リング本体（メッシュ＋マテリアル）。出し入れするのはこれだけ。")]
        public GameObject ring;
        [Tooltip("判定の間隔（秒）。見た目が1秒遅れて出るだけなので長くてよい。")]
        public float checkInterval = 1.0f;

        [Tooltip("表示言語。未割当なら日本語のまま。")]
        public KemonoLanguage language;
        [Tooltip("日本語の文言を焼いたマテリアル。")]
        public Material materialJa;
        [Tooltip("英語の文言を焼いたマテリアル。")]
        public Material materialEn;
        [Tooltip("韓国語の文言を焼いたマテリアル。未割当なら韓国語でも英語を出す。")]
        public Material materialKo;
        [Tooltip("簡体字の文言を焼いたマテリアル。未割当なら簡体字でも英語を出す。")]
        public Material materialZhs;
        [Tooltip("繁体字の文言を焼いたマテリアル。未割当なら繁体字でも英語を出す。")]
        public Material materialZht;
        [Tooltip("リングの MeshRenderer。マテリアルを差し替える先。")]
        public MeshRenderer ringRenderer;

        private float nextCheckTime_ = 0f;
        private bool shown_ = true;   // 初期値は Start の1回目で必ず引き直す
        private int appliedLang_ = -1;

        void Start()
        {
            // 最初の判定が済むまでは消しておく。卓が使用中のところへ
            // 一瞬だけ案内が出るのを避ける。
            shown_ = true;
            Apply(false);
            nextCheckTime_ = 0f;
        }

        void Update()
        {
            if (Time.time < nextCheckTime_) { return; }
            nextCheckTime_ = Time.time + (checkInterval > 0.1f ? checkInterval : 0.1f);
            ApplyLanguage();
            Apply(ShouldShow());
        }

        private bool ShouldShow()
        {
            if (table == null || matchmaking == null) { return false; }

            // 卓に直接座れる人には出さない（その人には「使える卓」なので）。
            if (matchmaking.CanLocalPlayerEnterTableSeat(table, 0)) { return false; }

            int t = matchmaking.GetTableIndexOf(table);
            if (t < 0) { return false; }

            // 使用中（着席誘導・対局中・フリー部屋の予約・強制リセット待ち）は消す。
            if (matchmaking.IsTableIndexInUse(t)) { return false; }

            // 卓の実体が対局に入っていたら消す。カリング中は古い値になり得るが、
            // その場合は上の IsTableIndexInUse（同期済み）が拾う。
            return table.gameState == Mahjong.GAMESTATE_INITIAL;
        }

        // 文字はテクスチャに焼いてあるので、言語の切り替えはマテリアル1枚の差し替えで済む。
        // 卓は push が届かない（カリングで寝る）ので、こちらから毎秒 pull する。
        private void ApplyLanguage()
        {
            if (ringRenderer == null || language == null) { return; }
            int lang = language.GetLanguage();
            if (lang == appliedLang_) { return; }
            appliedLang_ = lang;
            var mat = (lang == KemonoLanguage.LANG_EN) ? materialEn
                : (lang == KemonoLanguage.LANG_KO) ? (materialKo != null ? materialKo : materialEn)
                : (lang == KemonoLanguage.LANG_ZHS) ? (materialZhs != null ? materialZhs : materialEn)
                : (lang == KemonoLanguage.LANG_ZHT) ? (materialZht != null ? materialZht : materialEn)
                : materialJa;
            if (mat != null) { ringRenderer.sharedMaterial = mat; }
        }

        private void Apply(bool show)
        {
            if (show == shown_) { return; }
            shown_ = show;
            if (ring != null) { ring.SetActive(show); }
        }
    }
}
