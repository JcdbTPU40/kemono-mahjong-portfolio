using UdonSharp;
using UnityEngine;
using VRC.SDK3.Persistence;
using VRC.SDKBase;
using VRC.Udon;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    // 表示言語マネージャ（完全ローカル・同期変数ゼロ）
    //
    // ■ 役割
    //   入場した本人のクライアントだけで、ワールド全体の表示言語を決めて配る。
    //   - VRChat本体の表示言語が日本語の人 → 日本語
    //   - 韓国語の人 → 韓国語
    //   - 中国語（簡体字 zh-CN / 繁体字 zh-HK・zh-TW）の人 → 簡体字／繁体字
    //   - 英語（その他の言語も含む）に切り替えている人 → 英語
    //   翻訳は日英韓＋中国語（簡体・繁体）の5言語。それ以外はすべて英語に寄せる。
    //
    // ■ 韓国語（2026-10-10 追加）
    //   日英の対があるところへ韓国語を3つ目として足す（文字列は各スクリプトが日英韓で持つ）。
    //   画面の配置は英語と同じものを使う（卓の EN_ オブジェクト等）。KO_ があればそちらを優先。
    //   受け手で「英語か？」を lang==EN で判定している箇所は、韓国語の人に日本語が出る原因になる。
    //   2択の分岐は「ja かどうか」で書き、韓国語の文言は3つ目の引数で渡すこと。
    //
    // ■ 中国語（2026-10-10 追加）
    //   簡体字（LANG_ZHS）と繁体字（LANG_ZHT）を別の言語として持つ。並びは 日・英・韓・簡・繁。
    //   画面の配置は韓国語と同じく英語のものを使う（ZHS_ / ZHT_ があればそちらを優先）。
    //   文言は各スクリプトが5つ並べて持つ。中国語が空の欄は英語を出す（日本語や空欄にしない）。
    //   VRChat の言語コードは ClientSim の表で zh-CN（簡体）/ zh-HK（繁体）。zh-TW・zh-MO・
    //   zh-Hant も繁体へ、それ以外の zh は簡体へ寄せる。
    //
    // ■ 手動切替との関係（優先度）
    //   1. 本人が卓／メニューの言語ボタンを押した   … PlayerData に固定保存。最優先。
    //   2. 何も押していない                        … VRChat本体の表示言語から自動判定。
    //   自動判定のまま VRChat 側の言語を変えた人は OnLanguageChanged で追随する
    //   （手動で選んだ人は追随しない＝本人の明示的な選択を上書きしない）。
    //
    // ■ 永続化
    //   PlayerData("kmj_lang")：0=未設定（自動）/ 1=日本語固定 / 2=英語固定 / 3=韓国語固定
    //                           / 4=簡体字固定 / 5=繁体字固定。
    //   PlayerData の復元は OnPlayerRestored 後なので、それより前の書き込みは
    //   保留して復元後に流す（PerformanceModeController と同じ作法）。
    //   復元前は自動判定の結果で表示しておく＝「一瞬だけ日本語」が出ない。
    //
    // ■ 配布先
    //   - tables      … 各卓の Mahjong。JA_ / EN_ のオブジェクト差し替えは Mahjong 側が持つ。
    //                   卓カリングで寝ている卓へは届かない（SendCustomEvent が走らない）ので、
    //                   復帰時に Mahjong.OnEnable が自分から取りに来る（pull）。
    //   - mainMenu    … MainMenuController。動的文字列は向こうが言語で出し分ける。
    //   - localizers  … KemonoLocalizer。ビルダーが焼いた静的ラベルの日英対訳表。
    //
    // ■ 使い方
    //   Tools > Kemono Mahjong > Build Language  … シーンに LanguageManager を作る
    //   Tools > Kemono Mahjong > Wire Language   … 卓・メニュー・Localizer を集めて割り当て
    //   Tools > Kemono Mahjong > Validate Language … 参照欠けの検査
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class KemonoLanguage : UdonSharpBehaviour
    {
        // MahjongLanguage と同じ並び（0=Japanese / 1=English）。int で扱うのは
        // U# のビヘイビア間呼び出しで enum を渡さずに済ませるため。
        public const int LANG_JA = 0;
        public const int LANG_EN = 1;
        // 韓国語。画面の配置は英語と同じものを使い、文言だけ韓国語にする。
        public const int LANG_KO = 2;
        // 中国語。簡体字と繁体字は別の言語として扱う（字形も語彙も違う）。配置は英語と同じ。
        public const int LANG_ZHS = 3;
        public const int LANG_ZHT = 4;
        public const int NUM_LANG = 5;

        public const string LANG_KEY = "kmj_lang";

        // PlayerData に入れる値。0 と「キー無し」を同じ意味（＝自動）にしてある。
        private const int SAVED_AUTO = 0;
        private const int SAVED_JA = 1;
        private const int SAVED_EN = 2;
        private const int SAVED_KO = 3;
        private const int SAVED_ZHS = 4;
        private const int SAVED_ZHT = 5;

        [Header("配布先（Wire Language で自動割り当て）")]
        [Tooltip("各卓の Mahjong。カリングで寝ている卓は OnEnable で自分から取りに来る。")]
        public Mahjong[] tables;
        [Tooltip("メインメニュー。動的文字列の出し分けを担当する。")]
        public MainMenuController mainMenu;
        [Tooltip("静的ラベルの日英対訳表。パネルのルート（常時アクティブな側）に置くこと。")]
        public KemonoLocalizer[] localizers;
        [Tooltip("入場注意書きゲート。日英の本文を自前で持っているので、切替だけ伝える。")]
        public EntryNoticeGate entryNotice;
        [Tooltip("段位名の出どころ。メニュー・結果画面・ランキングが全部ここを引用するので最初に配る。")]
        public PlayerStatsStore statsStore;
        [Tooltip("ApplyLanguageJa / En / Ko / Zhs / Zht を持つその他の受け手（ランキング表・音量パネル・ネームプレート・和了演出カタログ）。")]
        public UdonSharpBehaviour[] extraTargets;

        [Header("動作")]
        [Tooltip("VRChat本体の表示言語から自動判定する。オフにすると常に fallbackJapanese で開始する。")]
        public bool autoDetect = true;
        [Tooltip("自動判定できなかったときの言語。true=日本語。")]
        public bool fallbackJapanese = true;
        [Tooltip("自動判定のままの人は、VRChat側の言語変更に追随する。")]
        public bool followClientChanges = true;

        // 現在の表示言語。
        private int lang_ = LANG_JA;
        // 本人が言語ボタンで明示的に選んだか（true なら自動判定で上書きしない）。
        private bool manual_ = false;
        // 自分の PlayerData が復元済みか。
        private bool restored_ = false;
        // 復元前に切り替えた場合の保留フラグ（最後の操作が勝つ）。
        private bool pendingSave_ = false;
        // 初期言語の判定が済んだか。
        private bool resolved_ = false;

        void Start()
        {
            Resolve();
            Apply();
        }

        // 初期言語をVRChat本体から決める。Start より先に GetLanguage を読まれても
        // 正しい答えを返せるように、遅延解決にしてある（Start の実行順は保証されない）。
        private void Resolve()
        {
            if (resolved_) { return; }
            resolved_ = true;
            lang_ = DetectFromClient();
        }

        // ------------------------------------------------------------
        // 判定
        // ------------------------------------------------------------

        private int DetectFromClient()
        {
            if (!autoDetect) { return fallbackJapanese ? LANG_JA : LANG_EN; }
            return LangFromCode(VRCPlayerApi.GetCurrentLanguage());
        }

        // VRChat の言語コード（"ja" / "en" / "ko" / "zh-CN" / "zh-HK" …）を5言語へ丸める。
        // 日本語・韓国語・中国語以外は英語表示。
        private int LangFromCode(string code)
        {
            if (code == null || code.Length < 2) { return fallbackJapanese ? LANG_JA : LANG_EN; }
            var lower = code.ToLower();
            var head = lower.Substring(0, 2);
            if (head == "ja") { return LANG_JA; }
            if (head == "ko") { return LANG_KO; }
            if (head == "zh")
            {
                // 繁体字の地域（香港・台湾・澳門）と明示の Hant。それ以外の zh は簡体字。
                if (lower.Contains("hk") || lower.Contains("tw") || lower.Contains("mo") || lower.Contains("hant"))
                {
                    return LANG_ZHT;
                }
                return LANG_ZHS;
            }
            return LANG_EN;
        }

        // ------------------------------------------------------------
        // PlayerData
        // ------------------------------------------------------------

        public override void OnPlayerRestored(VRCPlayerApi player)
        {
            if (player == null || !player.isLocal) { return; }

            restored_ = true;

            if (pendingSave_)
            {
                // 復元前に押されていた＝そちらが本人の最新の意思。保存し直す。
                pendingSave_ = false;
                WriteSave();
                return;
            }

            int v;
            if (!PlayerData.TryGetInt(player, LANG_KEY, out v)) { return; }
            if (v == SAVED_AUTO) { return; } // 自動のまま＝Startの判定を維持

            manual_ = true;
            int saved = (v == SAVED_EN) ? LANG_EN : (v == SAVED_KO) ? LANG_KO
                : (v == SAVED_ZHS) ? LANG_ZHS : (v == SAVED_ZHT) ? LANG_ZHT : LANG_JA;
            if (saved == lang_) { return; }

            lang_ = saved;
            Apply();
        }

        private void WriteSave()
        {
            int v = SAVED_AUTO;
            if (manual_)
            {
                v = (lang_ == LANG_EN) ? SAVED_EN : (lang_ == LANG_KO) ? SAVED_KO
                    : (lang_ == LANG_ZHS) ? SAVED_ZHS : (lang_ == LANG_ZHT) ? SAVED_ZHT : SAVED_JA;
            }
            PlayerData.SetInt(LANG_KEY, v);
        }

        private void Save()
        {
            if (!restored_)
            {
                pendingSave_ = true;
                return;
            }
            WriteSave();
        }

        // ------------------------------------------------------------
        // VRChat本体の言語変更に追随（自動判定の人だけ）
        // ------------------------------------------------------------

        public override void OnLanguageChanged(string language)
        {
            if (!followClientChanges) { return; }
            if (manual_) { return; }

            int detected = LangFromCode(language);
            if (detected == lang_) { return; }

            lang_ = detected;
            Apply();
        }

        // ------------------------------------------------------------
        // 外部API
        // ------------------------------------------------------------

        public int GetLanguage() { Resolve(); return lang_; }
        // 英語そのものか（韓国語・中国語は false）。「英語の配置か」を知りたいなら IsJapanese の否定を使う。
        public bool IsEnglish() { Resolve(); return lang_ == LANG_EN; }
        public bool IsJapanese() { Resolve(); return lang_ == LANG_JA; }
        public bool IsKorean() { Resolve(); return lang_ == LANG_KO; }
        public bool IsChineseSimplified() { Resolve(); return lang_ == LANG_ZHS; }
        public bool IsChineseTraditional() { Resolve(); return lang_ == LANG_ZHT; }
        public bool IsManual() { return manual_; }

        // 言語ボタン（卓／メニュー）から呼ぶ。ここを通ると手動固定になる。
        public void SetLanguage(int lang)
        {
            if (lang < 0 || lang >= NUM_LANG) { return; }

            Resolve();
            manual_ = true;
            if (lang != lang_)
            {
                lang_ = lang;
                Apply();
            }
            Save();
        }

        public void SwitchNext() { SetLanguage((lang_ + 1) % NUM_LANG); }
        public void SetJapanese() { SetLanguage(LANG_JA); }
        public void SetEnglish() { SetLanguage(LANG_EN); }
        public void SetKorean() { SetLanguage(LANG_KO); }
        public void SetChineseSimplified() { SetLanguage(LANG_ZHS); }
        public void SetChineseTraditional() { SetLanguage(LANG_ZHT); }

        // 手動固定を解除して自動判定へ戻す。
        public void ResetToAuto()
        {
            resolved_ = true;
            manual_ = false;
            int detected = DetectFromClient();
            if (detected != lang_)
            {
                lang_ = detected;
                Apply();
            }
            Save();
        }

        // ------------------------------------------------------------
        // 配布
        // ------------------------------------------------------------

        private void Apply()
        {
            // 段位名がいちばん先。メニュー・結果画面・ランキングがこの文字列を引用するので、
            // 後回しにすると「英語の画面に日本語の段位名」が1フレーム出る。
            if (statsStore != null)
            {
                if (lang_ == LANG_EN) { statsStore.ApplyLanguageEn(); }
                else if (lang_ == LANG_KO) { statsStore.ApplyLanguageKo(); }
                else if (lang_ == LANG_ZHS) { statsStore.ApplyLanguageZhs(); }
                else if (lang_ == LANG_ZHT) { statsStore.ApplyLanguageZht(); }
                else { statsStore.ApplyLanguageJa(); }
            }

            if (extraTargets != null)
            {
                var ev = (lang_ == LANG_EN) ? "ApplyLanguageEn"
                    : (lang_ == LANG_KO) ? "ApplyLanguageKo"
                    : (lang_ == LANG_ZHS) ? "ApplyLanguageZhs"
                    : (lang_ == LANG_ZHT) ? "ApplyLanguageZht" : "ApplyLanguageJa";
                for (int i = 0; i < extraTargets.Length; ++i)
                {
                    var b = extraTargets[i];
                    if (b == null) { continue; }
                    // 非アクティブな受け手には Udon が走らない。あちらの Lang() が
                    // 初回参照時にここへ取りに来るので取りこぼしにはならない。
                    if (!b.gameObject.activeInHierarchy) { continue; }
                    b.SendCustomEvent(ev);
                }
            }

            if (tables != null)
            {
                for (int i = 0; i < tables.Length; ++i)
                {
                    var t = tables[i];
                    if (t == null) { continue; }
                    // カリングで寝ている卓には届かない（Udonが動かない）。
                    // 復帰時に Mahjong.OnEnable がここへ取りに来るので取りこぼさない。
                    if (!t.gameObject.activeInHierarchy) { continue; }
                    t.ApplyLanguageInt(lang_);
                }
            }

            if (mainMenu != null && mainMenu.gameObject.activeInHierarchy)
            {
                mainMenu.SetLanguage(lang_);
            }

            if (entryNotice != null && entryNotice.gameObject.activeInHierarchy)
            {
                entryNotice.ApplyLanguageInt(lang_);
            }

            if (localizers != null)
            {
                for (int i = 0; i < localizers.Length; ++i)
                {
                    var l = localizers[i];
                    if (l == null) { continue; }
                    if (!l.gameObject.activeInHierarchy) { continue; }
                    l.Apply(lang_);
                }
            }
        }
    }
}
