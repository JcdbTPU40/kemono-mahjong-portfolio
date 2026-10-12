using UdonSharp;
using UnityEngine;
using UnityEngine.UI;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    //  自動化パネル（雀魂スタイル）
    // ------------------------------------------------------------
    //  自席の右手前・天板より下（スキップボタンの下）に横長のタブ（和・鳴・切・抜。オンの項目だけ緑）を置き、
    //  押すと下へ縦に展開して「自動和了／鳴きなし／ツモ切り／自動北抜き」のトグルを並べる。
    //  天板より上に置くと、VRで右端の牌を指すレイの通り道に入り、牌が切りづらい・行を誤爆する（2026-10-05）。
    //  自分に新しい決断が来たら Mahjong.cs が Collapse() で畳む（開いたまま放置した行の誤爆を防ぐ）。
    //  自動北抜きは三麻のときだけ出す。
    //
    //  ・表示だけを担当する。オン/オフの状態と自動処理は Mahjong.cs（RunAutomatedAction）が持つ。
    //  ・純ローカル。同期変数なし。自席の本人の画面にだけ出る。
    //  ・展開/折りたたみはこのパネルだけの状態（卓へは伝えない）。
    //  ・押下イベントは「_」始まり＝ネットワークイベントとしては呼べない（他人に押されない）。
    //  ・スクリプトの載るルートは常時アクティブ。表示ON/OFFは panelRoot だけで行う。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class MahjongAutoPanel : UdonSharpBehaviour
    {
        public const int ITEM_AGARI = 0;
        public const int ITEM_NAKI = 1;
        public const int ITEM_TSUMOGIRI = 2;
        public const int ITEM_KITA = 3;

        public GameObject panelRoot;

        // ---- たたんだタブ ----
        public Text[] tabLetters;          // 和・鳴・切・抜（ITEM_* の順）
        public Text tabArrow;              // ▲（展開中は ▼）

        // ---- 展開した一覧（配列は ITEM_* の順。画面の並びは rowSlot_ が決める）----
        public GameObject listRoot;
        public Transform[] rowRoots;       // 当たり判定（BoxCollider＋MahjongPushButton）
        public RectTransform[] rowRects;   // Canvas 上の行の絵
        public Image[] rowLamps;
        public Text[] rowTexts;

        public float pixelsPerMeter = 4000.0f;
        public float tabHeight = 0.032f;   // m
        public float rowHeight = 0.030f;   // m
        public float rowGap = 0.004f;      // m

        public Color onColor = new Color(0.30f, 0.92f, 0.40f, 1.0f);
        public Color offLetterColor = new Color(0.45f, 0.52f, 0.70f, 1.0f);
        public Color offLampColor = new Color(0.22f, 0.27f, 0.42f, 1.0f);
        public Color onTextColor = new Color(1.0f, 1.0f, 1.0f, 1.0f);
        public Color offTextColor = new Color(0.62f, 0.68f, 0.82f, 1.0f);

        private Mahjong table_;
        private bool expanded_;
        private bool[] on_ = new bool[4];
        private bool sanma_;
        private int lang_;

        // 各項目を上から何段目に置くか（ITEM_* の順）。一覧は下へ開くので、よく使うものほどタブの近く（上）。
        // 上から 鳴きなし → ツモ切り → 自動和了 → 自動北抜き（三麻だけ・一番下）。
        private int[] rowSlot_ = { 2, 0, 1, 3 };

        // タブの文字の横位置（Canvas の px）。BuildAutoPanel が最初に置く位置と同じ（-195 + 段 × 92）。
        private const float TAB_LETTER_LEFT_PX = -195.0f;
        private const float TAB_LETTER_PITCH_PX = 92.0f;

        private string[] jaRows = { "自動和了", "鳴きなし", "ツモ切り", "自動北抜き" };
        private string[] enRows = { "Auto Win", "No Calls", "Auto Discard", "Auto Kita" };
        private string[] jaLetters = { "和", "鳴", "切", "抜" };
        private string[] enLetters = { "W", "C", "D", "K" };
        private string[] koRows = { "자동 화료", "울기 없음", "쯔모기리", "자동 북 빼기" };
        private string[] koLetters = { "화", "울", "버", "북" };
        private string[] zhsRows = { "自动和牌", "不吃碰杠", "自动摸切", "自动拔北" };
        private string[] zhsLetters = { "和", "鸣", "切", "北" };
        private string[] zhtRows = { "自動和牌", "不吃碰槓", "自動摸切", "自動拔北" };
        private string[] zhtLetters = { "和", "鳴", "切", "北" };

        public void SetTable(Mahjong table)
        {
            table_ = table;
        }

        public void Hide()
        {
            expanded_ = false;
            if (panelRoot != null && panelRoot.activeSelf) { panelRoot.SetActive(false); }
        }

        // 一覧を畳む（タブは出したまま）。次の Show で描き直される。
        public void Collapse()
        {
            if (!expanded_) { return; }
            expanded_ = false;
            Render();
        }

        public void Show(Vector3 pos, Quaternion rot, bool agari, bool naki, bool tsumogiri, bool kita,
                         bool sanma, int lang)
        {
            if (panelRoot == null) { return; }

            transform.position = pos;
            transform.rotation = rot;

            on_[ITEM_AGARI] = agari;
            on_[ITEM_NAKI] = naki;
            on_[ITEM_TSUMOGIRI] = tsumogiri;
            on_[ITEM_KITA] = kita && sanma;
            sanma_ = sanma;
            lang_ = lang;

            if (!panelRoot.activeSelf) { panelRoot.SetActive(true); }
            Render();
        }

        private void Render()
        {
            bool en = lang_ == 1;
            bool ko = lang_ == 2;
            bool zhs = lang_ == 3;
            bool zht = lang_ == 4;

            // ---- タブ ----
            if (tabLetters != null)
            {
                for (int i = 0; i < tabLetters.Length; ++i)
                {
                    var t = tabLetters[i];
                    if (t == null) { continue; }
                    bool visible = i != ITEM_KITA || sanma_;
                    if (t.gameObject.activeSelf != visible) { t.gameObject.SetActive(visible); }
                    t.text = ko ? koLetters[i] : zhs ? zhsLetters[i] : zht ? zhtLetters[i] : en ? enLetters[i] : jaLetters[i];
                    t.color = on_[i] ? onColor : offLetterColor;
                    // 文字も一覧と同じ並び（左から rowSlot_ 順＝鳴・切・和・抜）。間隔は BuildAutoPanel の 92px。
                    var rt = t.rectTransform;
                    rt.anchoredPosition = new Vector2(TAB_LETTER_LEFT_PX + rowSlot_[i] * TAB_LETTER_PITCH_PX, rt.anchoredPosition.y);
                }
            }
            if (tabArrow != null) { tabArrow.text = expanded_ ? "▲" : "▼"; }   // 下へ開く

            // ---- 一覧 ----
            if (listRoot != null && listRoot.activeSelf != expanded_) { listRoot.SetActive(expanded_); }
            if (!expanded_ || rowRoots == null) { return; }

            int count = sanma_ ? 4 : 3;
            float ppm = pixelsPerMeter;
            for (int i = 0; i < rowRoots.Length; ++i)
            {
                bool visible = i < count;
                if (rowRoots[i] != null && rowRoots[i].gameObject.activeSelf != visible) { rowRoots[i].gameObject.SetActive(visible); }
                if (rowRects != null && i < rowRects.Length && rowRects[i] != null && rowRects[i].gameObject.activeSelf != visible)
                {
                    rowRects[i].gameObject.SetActive(visible);
                }
                if (!visible) { continue; }

                // 上から rowSlot_[i] 段目。タブのすぐ下に一番上の行が来るよう、下へ積む。
                float y = -(tabHeight * 0.5f + rowGap + rowHeight * 0.5f + rowSlot_[i] * (rowHeight + rowGap));
                if (rowRoots[i] != null) { rowRoots[i].localPosition = new Vector3(0.0f, y, 0.0f); }
                if (rowRects != null && i < rowRects.Length && rowRects[i] != null)
                {
                    rowRects[i].anchoredPosition = new Vector2(0.0f, y * ppm);
                }
                if (rowLamps != null && i < rowLamps.Length && rowLamps[i] != null)
                {
                    rowLamps[i].color = on_[i] ? onColor : offLampColor;
                }
                if (rowTexts != null && i < rowTexts.Length && rowTexts[i] != null)
                {
                    rowTexts[i].text = ko ? koRows[i] : zhs ? zhsRows[i] : zht ? zhtRows[i] : en ? enRows[i] : jaRows[i];
                    rowTexts[i].color = on_[i] ? onTextColor : offTextColor;
                }
            }
        }

        // ---- 押下（MahjongPushButton: local=true / disableOnPush=false）----
        public void _AutoTab()
        {
            expanded_ = !expanded_;
            Render();
        }

        public void _AutoItem0() { Toggle(ITEM_AGARI); }
        public void _AutoItem1() { Toggle(ITEM_NAKI); }
        public void _AutoItem2() { Toggle(ITEM_TSUMOGIRI); }
        public void _AutoItem3() { Toggle(ITEM_KITA); }

        private void Toggle(int item)
        {
            if (table_ != null) { table_.AutoPanelToggle(item); }
        }
    }
}
