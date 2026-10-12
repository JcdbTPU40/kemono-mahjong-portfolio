using TMPro;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    // 静的ラベルの日英対訳表（完全ローカル・同期変数ゼロ）
    //
    // ■ 役割
    //   「ビルダーが作った固定文言」を言語で差し替えるだけの受け皿。
    //   KemonoLanguage から Apply(lang) を呼ばれて、対象 Text の中身を書き換える。
    //
    // ■ なぜオブジェクト差し替え（JA_/EN_）ではなく文字列差し替えなのか
    //   卓は既に JA_Text / EN_Text の対を27組持っていて、そちらは Mahjong 側の
    //   ActivateLanguage が面倒を見る。一方メニュー系のパネルでラベルを二重化すると
    //   Canvas の要素数が倍になり、Quest では Canvas リビルドの実費がそのまま増える。
    //   文言だけの差し替えなら要素数は増えない。
    //
    // ■ 置き場所
    //   「言語が切り替わった瞬間にアクティブでいるオブジェクト」に置くこと。
    //   Udon は非アクティブだと動かないので、畳まれる画面の中に置くと取りこぼす。
    //   メインメニューなら MainMenu ルートに1個置いて、全画面のラベルをまとめて持たせる。
    //   どうしても畳まれる場所に置く場合は languageManager を割り当てておけば、
    //   復帰時（OnEnable）に自分から現在の言語を取りに来る。
    //
    // ■ 配列の対応
    //   tmpTexts[i] に tmpJa[i] / tmpEn[i] / tmpKo[i] / tmpZhs[i] / tmpZht[i] が対応する（同じ長さ・同じ順）。
    //   長さがズレている分は無視する（ビルダーの作り損ないで全滅させない）。
    //   韓国語・中国語が空の行は英語を出す（訳し漏れで日本語や空欄にしない）。
    //   画像などのオブジェクトは、中国語では英語側（englishObjects）を出す。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class KemonoLocalizer : UdonSharpBehaviour
    {
        [Tooltip("畳まれる場所に置く場合の保険。復帰時に現在の言語を取りに行く。")]
        public KemonoLanguage languageManager;

        [Header("TextMeshPro ラベル（6つの配列は同じ長さ・同じ順）")]
        public TextMeshProUGUI[] tmpTexts;
        [TextArea] public string[] tmpJa;
        [TextArea] public string[] tmpEn;
        [TextArea] public string[] tmpKo;
        [TextArea] public string[] tmpZhs;
        [TextArea] public string[] tmpZht;

        [Header("uGUI Text ラベル（6つの配列は同じ長さ・同じ順）")]
        public Text[] uiTexts;
        [TextArea] public string[] uiJa;
        [TextArea] public string[] uiEn;
        [TextArea] public string[] uiKo;
        [TextArea] public string[] uiZhs;
        [TextArea] public string[] uiZht;

        [Header("言語ごとに出し分けるオブジェクト（画像・帯など、文字でないもの）")]
        public GameObject[] japaneseObjects;
        public GameObject[] englishObjects;
        [Tooltip("韓国語用。空なら韓国語でも englishObjects を出す。")]
        public GameObject[] koreanObjects;

        // 最後に適用した言語（-1＝未適用）。
        private int applied_ = -1;

        void OnEnable()
        {
            // 畳まれている間に言語が変わっていた場合の取りこぼし回収。
            if (languageManager == null) { return; }
            Apply(languageManager.GetLanguage());
        }

        public void ApplyJapanese() { Apply(KemonoLanguage.LANG_JA); }
        public void ApplyEnglish() { Apply(KemonoLanguage.LANG_EN); }
        public void ApplyKorean() { Apply(KemonoLanguage.LANG_KO); }
        public void ApplyChineseSimplified() { Apply(KemonoLanguage.LANG_ZHS); }
        public void ApplyChineseTraditional() { Apply(KemonoLanguage.LANG_ZHT); }

        public int GetAppliedLanguage() { return applied_; }

        public void Apply(int lang)
        {
            if (lang < 0 || lang >= KemonoLanguage.NUM_LANG) { lang = KemonoLanguage.LANG_JA; }
            bool ko = (lang == KemonoLanguage.LANG_KO);
            bool en = (lang == KemonoLanguage.LANG_EN);
            bool zhs = (lang == KemonoLanguage.LANG_ZHS);
            bool zht = (lang == KemonoLanguage.LANG_ZHT);
            // 日本語以外は英語の行が土台（韓国語・中国語は自分の行があればそれ）。
            bool latin = lang != KemonoLanguage.LANG_JA;
            applied_ = lang;

            if (tmpTexts != null)
            {
                var src = latin ? tmpEn : tmpJa;
                var own = ko ? tmpKo : zhs ? tmpZhs : zht ? tmpZht : null;
                for (int i = 0; i < tmpTexts.Length; ++i)
                {
                    var t = tmpTexts[i];
                    if (t == null) { continue; }
                    var s = Pick(src, own, i);
                    if (s == null) { continue; }
                    if (t.text != s) { t.text = s; }
                }
            }

            if (uiTexts != null)
            {
                var src = latin ? uiEn : uiJa;
                var own = ko ? uiKo : zhs ? uiZhs : zht ? uiZht : null;
                for (int i = 0; i < uiTexts.Length; ++i)
                {
                    var t = uiTexts[i];
                    if (t == null) { continue; }
                    var s = Pick(src, own, i);
                    if (s == null) { continue; }
                    if (t.text != s) { t.text = s; }
                }
            }

            bool hasKoObjects = koreanObjects != null && koreanObjects.Length > 0;
            SetObjects(japaneseObjects, !latin);
            SetObjects(englishObjects, en || zhs || zht || (ko && !hasKoObjects));
            SetObjects(koreanObjects, ko);
        }

        // その言語の行があればそれ、無ければ基本（日本語か英語）の行。
        private string Pick(string[] src, string[] own, int i)
        {
            if (own != null && i < own.Length && own[i] != null && own[i].Length > 0) { return own[i]; }
            if (src == null || i >= src.Length) { return null; }
            return src[i];
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
    }
}
