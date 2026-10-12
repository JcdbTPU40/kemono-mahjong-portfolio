using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    //  聴牌時の待ち牌パネル（雀魂スタイル）
    // ------------------------------------------------------------
    //  自席が聴牌しているとき、手牌の上に立つ待ち牌の背面へ濃紺のパネルを敷き、
    //  各牌の真下に「N枚」のバッジを出す。
    //
    //  牌そのものは Mahjong.cs が従来どおり実物（sampleTileTransformMap）を置く。
    //  ここが持つのは背景と枚数バッジだけ＝牌の絵柄・赤ドラの扱いを二重管理しない。
    //
    //  設計思想（他の演出コンポーネントと同じ）:
    //   ・純ローカル表示。同期変数を持たない（NoVariableSync）。待ちは各クライアントが
    //     自分の手牌から計算しており、そもそも自席にしか出さないので同期の必要がない。
    //   ・スクリプトが載るルートは常時アクティブ。表示ON/OFFは子の panelRoot だけで行う。
    //   ・ビルボードしない。牌が席の方向を向いて置かれるので、パネルも同じ姿勢に揃える
    //     （自席にしか出ないため、その向きがそのまま本人の正面になる）。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class MahjongMachiPanel : UdonSharpBehaviour
    {
        // 表示言語。卓が自分の言語をここへ押し込む（Mahjong.SwitchLanguage）。
        // 卓カリングで寝ている間の push は届かないが、復帰後は卓が描き直すので
        // 次に聴牌したときには正しい言語になっている。
        private int lang_ = KemonoLanguage.LANG_JA;

        public void SetLanguageInt(int lang)
        {
            lang_ = (lang > 0 && lang < KemonoLanguage.NUM_LANG) ? lang : KemonoLanguage.LANG_JA;
        }

        private bool En() { return lang_ == KemonoLanguage.LANG_EN; }
        private bool Ko() { return lang_ == KemonoLanguage.LANG_KO; }
        private bool Zhs() { return lang_ == KemonoLanguage.LANG_ZHS; }
        private bool Zht() { return lang_ == KemonoLanguage.LANG_ZHT; }

        // 待ち牌ごとの状態。Mahjong.cs 側の MACHI_FLAG_* と必ず同じ値にすること。
        public const int FLAG_NORMAL = 0;     // 普通に和了れる → 残り枚数を出す
        public const int FLAG_YAKUNASHI = 1;  // その牌で和了っても役なし → 「無役」
        public const int FLAG_FURITEN = 2;    // 振り聴（手全体の状態）

        [Header("必須参照")]
        [Tooltip("表示のルート。非アクティブにして置いておく（聴牌中だけONになる）。")]
        public GameObject panelRoot;

        [Tooltip("Canvas の RectTransform。実行時に位置と姿勢を牌に合わせる。")]
        public RectTransform canvasRect;

        [Tooltip("パネルの縁（金）。9スライスで横に伸ばす。")]
        public Image borderImage;
        public RectTransform borderRect;

        [Tooltip("パネルの地（濃紺）。縁より内側に置く。")]
        public Image fillImage;
        public RectTransform fillRect;

        [Tooltip("角の装飾（左上・右下）。無くても成立する。")]
        public RawImage[] ornamentImages;
        public RectTransform[] ornamentRects;

        [Header("枚数バッジ（待ちの最大は国士の13）")]
        public GameObject[] badgeRoots;
        public RectTransform[] badgeRects;
        public Text[] badgeTexts;

        [Tooltip("バッジの地。GetComponent を毎回叩かないよう配線して持っておく。")]
        public Image[] badgeBackgrounds;

        [Header("振り聴ラベル（パネル上辺の赤いラベル）")]
        [Tooltip("振り聴のときだけ出す。フリテンは手全体の状態なので1つで足りる。")]
        public GameObject furitenRoot;
        public RectTransform furitenRect;
        public Image furitenImage;
        public Text furitenText;

        [Tooltip("手牌の左端に出す縦書きの振り聴ラベル。位置は Mahjong.cs がワールド座標で渡す。")]
        public GameObject handFuritenRoot;
        public RectTransform handFuritenRect;
        public Image handFuritenImage;
        public Text handFuritenText;

        [Header("寸法（Canvas px。1px = 0.1mm）")]
        [Tooltip("牌1枚ぶんの間隔。牌幅0.0212mに対応する212pxが既定。")]
        public float slotPixels = 212.0f;

        [Tooltip("牌の高さ。0.029mに対応する290pxが既定。")]
        public float tilePixels = 290.0f;

        [Tooltip("牌の下端からバッジ上端までの間隔。")]
        public float badgeGap = 24.0f;

        [Tooltip("バッジの高さ。")]
        public float badgeHeight = 92.0f;

        [Tooltip("パネルの内側余白。斜めから見たときに牌がはみ出さないよう少し広めに取る。")]
        public float panelPadding = 58.0f;

        [Tooltip("縁の太さ。")]
        public float borderThickness = 7.0f;

        // 牌は厚み 0.0162m（中心から前面/後面まで 0.0081m）。
        //  ・パネルを牌の奥（正の値）に置くと、牌が厚みぶん手前へ飛び出して見える。
        //  ・負の値で「牌の前面のすぐ手前」に置くと、牌の本体はパネルの地に隠れ、
        //    前面だけが 1mm ほど出て見える＝雀魂のように平らに収まる。
        //    0 に近づけすぎるとZファイトするので、前面より 1mm ほど奥に置くこと。
        [Tooltip("パネルを牌の奥へ下げる距離（m）。負の値で牌の前面側へ出す。-0.007 で牌が面一に収まる。")]
        public float depthOffset = -0.007f;

        // 表示倍率。Mahjong が待ち牌（実物の見本牌）を同じ倍率で縮めて並べ、ここへ同じ値を入れる。
        // Canvas ごと縮めるので、1枚ぶんの間隔（slotPixels）も牌の縮んだ幅にそのまま一致する。
        [HideInInspector] public float displayScale = 1.0f;
        private Vector3 canvasBaseScale_;
        private bool canvasBaseScaleCached_ = false;

        [Header("色")]
        public Color borderColor = new Color(0.85f, 0.62f, 0.30f, 1.0f);
        public Color fillColor = new Color(0.055f, 0.085f, 0.16f, 0.96f);
        public Color badgeColor = new Color(0.24f, 0.34f, 0.58f, 1.0f);
        public Color badgeTextColor = new Color(0.96f, 0.98f, 1.0f, 1.0f);
        public Color ornamentColor = new Color(0.86f, 0.60f, 0.28f, 1.0f);

        [Tooltip("残り0枚のときのバッジ色（地獄待ち）。")]
        public Color badgeEmptyColor = new Color(0.45f, 0.20f, 0.24f, 1.0f);

        [Tooltip("無役のときのバッジ色（雀魂は橙）。")]
        public Color badgeYakunashiColor = new Color(0.78f, 0.44f, 0.12f, 1.0f);

        [Tooltip("振り聴ラベルの地色。")]
        public Color furitenColor = new Color(0.72f, 0.14f, 0.18f, 1.0f);

        [Tooltip("振り聴ラベルの大きさ（px）。")]
        public Vector2 furitenSize = new Vector2(230.0f, 72.0f);

        [Tooltip("手牌の左に出す縦の振り聴ラベルの大きさ（px）。1文字ずつ縦に積むので細長い。")]
        public Vector2 handFuritenSize = new Vector2(150.0f, 430.0f);

        void Start()
        {
            HideNow();
        }

        private void HideNow()
        {
            if (panelRoot != null) { panelRoot.SetActive(false); }
        }

        public void Hide()
        {
            HideNow();
        }

        /// <summary>
        /// 待ち牌パネルを出す。
        ///  originPos … 並びの先頭（k=0）の牌のワールド位置
        ///  rot       … 牌と同じワールド姿勢
        ///  count     … 待ちの枚数（並ぶ牌の数）
        ///  remains   … 各待ち牌の残り枚数。長さは count 以上であること。
        ///  flags     … 各待ち牌の状態（FLAG_*）。長さは count 以上であること。
        ///  furiten   … 手全体が振り聴か。
        ///  handLabelPos … 手牌の左に出す振り聴ラベルのワールド位置。
        /// Canvas は牌と表裏が逆なので180度回して使う。その結果、牌の並びは Canvas の +X 方向になる。
        /// </summary>
        public void Show(Vector3 originPos, Quaternion rot, int count, int[] remains, int[] flags, bool furiten, Vector3 handLabelPos)
        {
            if (panelRoot == null || canvasRect == null) { return; }

            int n = count;
            if (n <= 0) { HideNow(); return; }

            int maxSlots = badgeRects != null ? badgeRects.Length : 0;
            if (n > maxSlots) { n = maxSlots; }

            // 牌と Canvas は表裏が逆（牌の面が向く側から見ると Canvas は裏返って見える）。
            // 180度回して表を牌の面と同じ側へ向け、奥行きぶんだけ牌の後ろへ下げる。
            Quaternion canvasRot = rot * Quaternion.Euler(0.0f, 180.0f, 0.0f);
            canvasRect.rotation = canvasRot;

            // 牌が縮めば厚みも縮むので、奥行きも同じ倍率にする。
            if (!canvasBaseScaleCached_)
            {
                canvasBaseScale_ = canvasRect.localScale;
                canvasBaseScaleCached_ = true;
            }
            canvasRect.localScale = canvasBaseScale_ * displayScale;
            canvasRect.position = originPos + (canvasRot * Vector3.forward) * (depthOffset * displayScale);

            // ---- パネルの寸法 ----
            float innerWidth = n * slotPixels;
            float innerHeight = tilePixels + badgeGap + badgeHeight;

            float panelWidth = innerWidth + panelPadding * 2.0f;
            float panelHeight = innerHeight + panelPadding * 2.0f;

            // 牌の中心が y=0。パネルは牌の上端から badge の下端までを覆う。
            float top = tilePixels * 0.5f + panelPadding;
            float bottom = -(tilePixels * 0.5f + badgeGap + badgeHeight + panelPadding);
            float centerY = (top + bottom) * 0.5f;

            // 180度回した Canvas では、牌の並ぶ向きが +X になる。
            float centerX = (n - 1) * slotPixels * 0.5f;

            if (borderRect != null)
            {
                borderRect.anchoredPosition = new Vector2(centerX, centerY);
                borderRect.sizeDelta = new Vector2(panelWidth, panelHeight);
            }
            if (borderImage != null) { borderImage.color = borderColor; }

            if (fillRect != null)
            {
                fillRect.anchoredPosition = new Vector2(centerX, centerY);
                fillRect.sizeDelta = new Vector2(
                    panelWidth - borderThickness * 2.0f,
                    panelHeight - borderThickness * 2.0f);
            }
            if (fillImage != null) { fillImage.color = fillColor; }

            // ---- 角の装飾（左上・右下）----
            int orn = ornamentRects != null ? ornamentRects.Length : 0;
            for (int i = 0; i < orn; ++i)
            {
                if (ornamentRects[i] == null) { continue; }

                float ox = centerX + (i == 0 ? -panelWidth * 0.5f : panelWidth * 0.5f);   // 0=左上 1=右下
                float oy = centerY + (i == 0 ? panelHeight * 0.5f : -panelHeight * 0.5f);
                ornamentRects[i].anchoredPosition = new Vector2(ox, oy);

                if (ornamentImages != null && i < ornamentImages.Length && ornamentImages[i] != null)
                {
                    ornamentImages[i].color = ornamentColor;
                }
            }

            // ---- 枚数バッジ ----
            float badgeY = -(tilePixels * 0.5f + badgeGap + badgeHeight * 0.5f);

            for (int k = 0; k < maxSlots; ++k)
            {
                bool on = k < n;
                if (badgeRoots != null && k < badgeRoots.Length && badgeRoots[k] != null)
                {
                    badgeRoots[k].SetActive(on);
                }
                if (!on) { continue; }

                if (badgeRects[k] != null)
                {
                    badgeRects[k].anchoredPosition = new Vector2(k * slotPixels, badgeY);
                    badgeRects[k].sizeDelta = new Vector2(slotPixels - 22.0f, badgeHeight);
                }

                int remain = (remains != null && k < remains.Length) ? remains[k] : 0;
                if (remain < 0) { remain = 0; }

                int flag = (flags != null && k < flags.Length) ? flags[k] : FLAG_NORMAL;

                // 無役の牌は枚数ではなく「無役」を出す（雀魂と同じ）。
                bool yakunashi = flag == FLAG_YAKUNASHI;

                if (badgeTexts != null && k < badgeTexts.Length && badgeTexts[k] != null)
                {
                    // 英語は数字だけ（"3枚"→"3"）。バッジが小さく、単語を入れると潰れる。
                    badgeTexts[k].text = yakunashi
                        ? (En() ? "No yaku" : Ko() ? "역 없음" : Zhs() ? "无役" : "無役")
                        : (remain.ToString() + (En() ? "" : Ko() ? "장" : Zhs() ? "张" : Zht() ? "張" : "枚"));
                    badgeTexts[k].color = badgeTextColor;
                }

                // バッジの地色。無役は橙、残り0枚（地獄待ち）は赤、それ以外は青。
                if (badgeBackgrounds != null && k < badgeBackgrounds.Length && badgeBackgrounds[k] != null)
                {
                    badgeBackgrounds[k].color = yakunashi
                        ? badgeYakunashiColor
                        : (remain > 0 ? badgeColor : badgeEmptyColor);
                }
            }

            // ---- 振り聴ラベル（パネル上辺の中央にまたがるように置く）----
            if (furitenRoot != null)
            {
                furitenRoot.SetActive(furiten);

                if (furiten && furitenRect != null)
                {
                    furitenRect.sizeDelta = furitenSize;
                    furitenRect.anchoredPosition = new Vector2(centerX, top);
                }
                if (furiten && furitenImage != null) { furitenImage.color = furitenColor; }
                if (furiten && furitenText != null)
                {
                    furitenText.text = En() ? "FURITEN" : Ko() ? "후리텐" : Zhs() ? "振听" : Zht() ? "振聽" : "振り聴";
                    furitenText.color = badgeTextColor;
                }
            }

            // ---- 手牌の左に出す縦の振り聴ラベル ----
            // Canvas の子だが位置だけワールド座標で置く（手牌はパネルより下・左にあるため）。
            if (handFuritenRoot != null)
            {
                handFuritenRoot.SetActive(furiten);

                if (furiten && handFuritenRect != null)
                {
                    handFuritenRect.sizeDelta = handFuritenSize;

                    // 渡ってくるのは牌の中心の高さ。pivot が中央なのでそのまま置くと
                    // ラベルの下半分が卓に潜る（牌より背が高いぶんだけはみ出す）。
                    // 牌の高さ（tilePixels）との差の半分だけ持ち上げて、
                    // ラベルの下端を牌の下端＝卓の面に合わせる。
                    float lift = (handFuritenSize.y - tilePixels) * 0.5f * canvasRect.lossyScale.y;
                    handFuritenRect.position = handLabelPos + canvasRect.up * lift;
                }
                if (furiten && handFuritenImage != null) { handFuritenImage.color = furitenColor; }
                if (furiten && handFuritenText != null)
                {
                    // 1文字ずつ縦1行に積む。英語も同じく縦積みにする。
                    handFuritenText.text = En() ? "F\nU\nR\nI\nT\nE\nN" : Ko() ? "후\n리\n텐"
                        : Zhs() ? "振\n听" : Zht() ? "振\n聽" : "振\nり\n聴";
                    handFuritenText.color = badgeTextColor;
                }
            }

            panelRoot.SetActive(true);
        }
    }
}
