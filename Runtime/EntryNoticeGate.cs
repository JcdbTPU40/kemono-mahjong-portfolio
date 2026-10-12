using TMPro;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    // 入場時の注意書きゲート（EntryNotice）
    //
    // ■ 役割
    //   ワールドに入った直後、スポーン地点（桟橋の先端）でプレイヤーを足止めし、
    //   開発中である旨・利用規約・ルールを読ませる。「同意して入場」を押すまで
    //   前へ進ませない。押した時点でパネルと不可視の壁を消し、移動を解放する。
    //
    // ■ 完全ローカル＝同期変数ゼロ
    //   パネルの表示/非表示も拘束も、押した本人のクライアントだけで完結する
    //   （BehaviourSyncMode.None）。したがって
    //     ・承諾済みの人にはパネルが見えず、未承諾の人にだけ見える
    //     ・PlayerData も使わないので「参加のたびに必ず出る」が自動的に満たされる
    //     ・遅れて入ってきた人の状態を気にする必要がない
    //   という要件が、追加の仕組みなしで成立する。
    //
    // ■ 拘束は3枚重ね（1枚では穴が開く）
    //   1) Immobilize(true)      … デスクトップのWASD・VRのスティック移動を止める。
    //   2) 不可視の壁（cageRoot） … VRの実歩行（ルームスケール）は Immobilize では
    //      止まらない。スポーン地点は桟橋の先端で、背後0.25m・右1.3mで水面へ落ちる。
    //      箱状のコライダーで物理的に囲って落下を防ぐ。
    //   3) 距離テザー（Update）  … 1)2) をすり抜けた場合の保険。anchor（= このオブジェクト
    //      の Transform）から tetherRadius を超えたら TeleportTo で引き戻す。
    //      壁（最大1.45m）より外側の値にしないと、壁に張り付いた人を延々と
    //      テレポートさせ続けることになるため tetherRadius > 壁の半径 を必ず守る。
    //
    // ■ スポーン位置ではなく anchor へ寄せる
    //   VRCSceneDescriptor の spawnRadius が 0.2 あるため、素のスポーン位置を基準に
    //   壁を組むと「壁の外側にスポーンする」事故が起きる。開始時に anchor（桟橋の
    //   安全な位置）へ TeleportTo して、向きごとパネル正面に揃える。
    //
    // ■ 性能
    //   Update は「未承諾のあいだ」だけ、しかも pollIntervalSeconds 間隔でしか
    //   中身を実行しない。承諾後は enabled=false にして Update 自体を止める。
    //
    // ■ 使い方
    //   Tools > Kemono Mahjong > Build Entry Notice でシーンに生成・再生成する
    //   （冪等。文言の正は BuildEntryNotice.cs 側にあり、実行のたび上書きされる）。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class EntryNoticeGate : UdonSharpBehaviour
    {
        [Header("ゲート全体")]
        [Tooltip("オフにすると注意書きも拘束も行わない（正式公開後に拘束だけ外す用）。")]
        public bool gateEnabled = true;

        [Header("参照（Build Entry Notice が自動設定）")]
        [Tooltip("表示言語マネージャ（Wire Language で自動割り当て）。入場時の初期言語をここから受け取る。")]
        public KemonoLanguage languageManager;
        [Tooltip("注意書きのCanvas。承諾で非表示にする。")]
        public GameObject canvasRoot;
        [Tooltip("不可視の壁の親。承諾で非表示にする。")]
        public GameObject cageRoot;
        [Tooltip("日本語の本文まとめ。")]
        public GameObject japaneseBody;
        [Tooltip("英語の本文まとめ。")]
        public GameObject englishBody;
        [Tooltip("韓国語の本文まとめ。未割当なら韓国語でも英語の本文を出す。")]
        public GameObject koreanBody;
        [Tooltip("簡体字の本文まとめ。未割当なら簡体字でも英語の本文を出す。")]
        public GameObject chineseSimplifiedBody;
        [Tooltip("繁体字の本文まとめ。未割当なら繁体字でも英語の本文を出す。")]
        public GameObject chineseTraditionalBody;
        [Tooltip("入場ボタンのラベル。言語切替で書き換える。")]
        public TextMeshProUGUI agreeLabel;
        [Tooltip("言語切替ボタンのラベル。押すと切り替わる先の言語名を出す。")]
        public TextMeshProUGUI languageLabel;

        [Header("ボタン文言")]
        public string agreeLabelJa = "同意して入場 / AGREE & PLAY";
        public string agreeLabelEn = "AGREE & PLAY / 同意して入場";
        public string agreeLabelKo = "동의하고 입장 / AGREE & PLAY";
        public string agreeLabelZhs = "同意并进入 / AGREE & PLAY";
        public string agreeLabelZht = "同意並進入 / AGREE & PLAY";
        // 切替ボタンは 日本語→English→한국어→简体中文→繁體中文→日本語 の順に回る。ラベルは「押すと切り替わる先」。
        public string languageLabelJa = "English";
        public string languageLabelEn = "한국어";
        public string languageLabelKo = "简体中文";
        public string languageLabelZhs = "繁體中文";
        public string languageLabelZht = "日本語";

        [Header("拘束の数値")]
        [Tooltip("参加直後は LocalPlayer やスポーン処理が安定しないため、この秒数だけ待ってから拘束を開始する。")]
        public float initDelaySeconds = 0.25f;
        [Tooltip("テザー判定の間隔（秒）。短くするほど厳密だが Update の実行回数が増える。")]
        public float pollIntervalSeconds = 0.25f;
        [Tooltip("anchor から水平方向にこの距離を超えたら引き戻す。不可視の壁より外側にすること。")]
        public float tetherRadius = 1.9f;
        [Tooltip("anchor からこの高さより下へ落ちたら引き戻す（負値）。")]
        public float fallDepth = -1.5f;

        [Header("スキップ")]
        [Tooltip("ここに表示名を入れたプレイヤーには注意書きを出さない（製作者のテスト用）。")]
        public string[] skipDisplayNames;

        // 承諾済み（または gateEnabled=false）。true になったらもう何もしない。
        private bool gateOpen_ = false;
        // 拘束を開始済みか。Immobilize(false) を呼ぶ必要があるかの判断に使う。
        private bool immobilized_ = false;
        // 拘束開始を試みる時刻。LocalPlayer が未確定なら後ろへずらして再試行する。
        private float initAt_ = 0f;
        // 次にテザー判定を行う時刻。
        private float nextPollAt_ = 0f;
        // 現在の言語（KemonoLanguage.LANG_*）。
        private int lang_ = KemonoLanguage.LANG_JA;

        void Start()
        {
            // シーン保存時の状態に関わらず、起動直後に必ず片方へ揃える
            // （JA/EN 両方が active のまま保存されている事故を初期化で潰す）。
            // どちらで始めるかは VRChat 本体の表示言語（マネージャが判定）に従う。
            // このゲートは入場直後に出るので、読めない言語で出すとその時点で詰む。
            lang_ = (languageManager == null) ? KemonoLanguage.LANG_JA : languageManager.GetLanguage();
            ApplyLanguage();

            if (!gateEnabled)
            {
                OpenGate();
                return;
            }

            if (canvasRoot != null) { canvasRoot.SetActive(true); }
            // 壁は「anchor へ引き寄せた後」に出す。スポーンは spawnRadius 0.2 のばらつきが
            // あり、先に壁を出すと壁の中／外側にスポーンして押し出され、桟橋から落ちる。
            if (cageRoot != null) { cageRoot.SetActive(false); }

            initAt_ = Time.time + initDelaySeconds;
        }

        private void Update()
        {
            if (gateOpen_) { return; }

            if (!immobilized_)
            {
                if (Time.time < initAt_) { return; }
                BeginGate();
                return;
            }

            if (Time.time < nextPollAt_) { return; }
            nextPollAt_ = Time.time + pollIntervalSeconds;
            TetherLocalPlayer();
        }

        // ============================================================
        // 拘束の開始・解放
        // ============================================================

        private void BeginGate()
        {
            var lp = Networking.LocalPlayer;
            if (!Utilities.IsValid(lp))
            {
                // 参加直後は LocalPlayer が未確定なことがある。少し待って再試行する。
                initAt_ = Time.time + initDelaySeconds;
                return;
            }

            if (IsSkipped(lp))
            {
                OpenGate();
                return;
            }

            immobilized_ = true;
            // 向きごと anchor に揃える＝パネルが必ず正面に来る。
            lp.TeleportTo(transform.position, transform.rotation);
            // 壁は引き寄せた後に出す（順序を入れ替えると壁の中にスポーンし得る）。
            if (cageRoot != null) { cageRoot.SetActive(true); }
            lp.Immobilize(true);
            nextPollAt_ = Time.time + pollIntervalSeconds;
        }

        // 承諾ボタン（uGUI Button の onClick から SendCustomEvent で呼ばれる）。
        public void OnAgree()
        {
            if (gateOpen_) { return; }
            OpenGate();
        }

        private void OpenGate()
        {
            gateOpen_ = true;

            // 一括非表示は必ず子→親の順（親を先に落とすと子の SetActive が漏れる）。
            if (japaneseBody != null) { japaneseBody.SetActive(false); }
            if (englishBody != null) { englishBody.SetActive(false); }
            if (koreanBody != null) { koreanBody.SetActive(false); }
            if (chineseSimplifiedBody != null) { chineseSimplifiedBody.SetActive(false); }
            if (chineseTraditionalBody != null) { chineseTraditionalBody.SetActive(false); }
            if (canvasRoot != null) { canvasRoot.SetActive(false); }
            if (cageRoot != null) { cageRoot.SetActive(false); }

            if (immobilized_)
            {
                var lp = Networking.LocalPlayer;
                if (Utilities.IsValid(lp)) { lp.Immobilize(false); }
                immobilized_ = false;
            }

            // 以後 Update を回す必要がない。
            enabled = false;
        }

        // ============================================================
        // テザー（Immobilize と壁をすり抜けた場合の保険）
        // ============================================================

        private void TetherLocalPlayer()
        {
            var lp = Networking.LocalPlayer;
            if (!Utilities.IsValid(lp)) { return; }

            var anchor = transform.position;
            var pos = lp.GetPosition();

            var flat = pos - anchor;
            flat.y = 0f;

            bool tooFar = flat.magnitude > tetherRadius;
            bool fell = (pos.y - anchor.y) < fallDepth;
            if (!tooFar && !fell) { return; }

            lp.TeleportTo(anchor, transform.rotation);
        }

        // ============================================================
        // 言語切替
        // ============================================================

        // 言語切替ボタン（uGUI Button の onClick から SendCustomEvent で呼ばれる）。
        public void OnToggleLanguage()
        {
            // ここで選んだ言語はワールド全体（卓・メインメニュー）にも引き継ぐ。
            // 注意書きを英語で読んだ人に、入場した途端日本語の卓を見せない。
            if (languageManager != null)
            {
                languageManager.SwitchNext();
                return; // マネージャから ApplyLanguageInt が折り返して来る
            }

            lang_ = (lang_ + 1) % KemonoLanguage.NUM_LANG;
            ApplyLanguage();
        }

        // KemonoLanguage から呼ばれる配布口。
        public void ApplyLanguageInt(int lang)
        {
            if (lang < 0 || lang >= KemonoLanguage.NUM_LANG) { lang = KemonoLanguage.LANG_JA; }
            if (lang == lang_) { return; }
            lang_ = lang;
            ApplyLanguage();
        }

        private void ApplyLanguage()
        {
            bool ja = lang_ == KemonoLanguage.LANG_JA;
            bool ko = lang_ == KemonoLanguage.LANG_KO;
            bool zhs = lang_ == KemonoLanguage.LANG_ZHS;
            bool zht = lang_ == KemonoLanguage.LANG_ZHT;
            bool koBody = ko && koreanBody != null;
            bool zhsBody = zhs && chineseSimplifiedBody != null;
            bool zhtBody = zht && chineseTraditionalBody != null;
            if (japaneseBody != null) { japaneseBody.SetActive(ja); }
            if (englishBody != null) { englishBody.SetActive(!ja && !koBody && !zhsBody && !zhtBody); }
            if (koreanBody != null) { koreanBody.SetActive(koBody); }
            if (chineseSimplifiedBody != null) { chineseSimplifiedBody.SetActive(zhsBody); }
            if (chineseTraditionalBody != null) { chineseTraditionalBody.SetActive(zhtBody); }
            if (agreeLabel != null)
            {
                agreeLabel.text = ja ? agreeLabelJa : ko ? agreeLabelKo
                    : zhs ? agreeLabelZhs : zht ? agreeLabelZht : agreeLabelEn;
            }
            // 切替ボタンには「押すと切り替わる先の言語」を出す。
            if (languageLabel != null)
            {
                languageLabel.text = ja ? languageLabelJa : ko ? languageLabelKo
                    : zhs ? languageLabelZhs : zht ? languageLabelZht : languageLabelEn;
            }
        }

        // ============================================================
        // スキップ判定
        // ============================================================

        private bool IsSkipped(VRCPlayerApi player)
        {
            if (skipDisplayNames == null || skipDisplayNames.Length == 0) { return false; }

            var name = player.displayName;
            if (name == null || name.Length == 0) { return false; }

            for (int i = 0; i < skipDisplayNames.Length; ++i)
            {
                var entry = skipDisplayNames[i];
                if (entry == null || entry.Length == 0) { continue; }
                if (entry == name) { return true; }
            }
            return false;
        }
    }
}
