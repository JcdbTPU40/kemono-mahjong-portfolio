using UdonSharp;
using UnityEngine;
using UnityEngine.UI;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    //  チーの候補パネル（雀魂スタイル）
    // ------------------------------------------------------------
    //  チーの形が複数あるときだけ、鳴く人の手牌の上に「選択してください」パネルを出し、
    //  候補（手牌から出す2枚）を並べる。1つ押すと Mahjong.ChiPanelPick でチーが確定する。
    //  右端の戻るボタンでパネルを閉じてボタン選択へ戻る。
    //
    //  ・表示だけを担当する。候補を作る・確定するのは Mahjong.cs（Owner 再検証の経路はそのまま）。
    //  ・純ローカル。同期変数なし。鳴く本人の画面にしか出ない。
    //  ・牌は牌の形の板（MeshFilter）に、実物の牌のメッシュを差し替えて描く。
    //    同じ牌が複数の候補に出る（例：一二／二四）ので、1種1個の見本牌（SampleTiles）は使えない。
    //  ・スクリプトの載るルートは常時アクティブ。表示ON/OFFは panelRoot だけで行う。
    //
    //  座標：content は「牌の正面から見た」座標系（+X＝本人の右、+Y＝上）。
    //  待ち牌パネル（MahjongMachiPanel）と同じく、牌の姿勢を180度回した向き。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class MahjongChiPanel : UdonSharpBehaviour
    {
        public GameObject panelRoot;
        public Transform content;

        public RectTransform canvasRect;
        public RectTransform borderRect;
        public RectTransform fillRect;
        public RectTransform headerRect;
        public Text headerText;
        public RectTransform[] ornamentRects;   // 0=左上 1=右上 2=左下 3=右下

        // 候補 k は optionRoots[k]（当たり判定）と tileFilters[2k], [2k+1]（牌2枚）。
        public GameObject[] optionRoots;
        public BoxCollider[] optionColliders;
        public MeshFilter[] tileFilters;
        public RectTransform[] optionPlateRects; // 候補ごとの下敷き（押せる範囲を見せる）

        public Transform backButton;
        public BoxCollider backCollider;
        public RectTransform backPlateRect;

        // 1m あたりの Canvas ピクセル数（Canvas の localScale = 1 / pixelsPerMeter）。
        public float pixelsPerMeter = 4000.0f;

        // 寸法はすべて牌の幅（tileW）・高さ（tileH）に対する比。
        public float optionGapRatio = 0.6f;     // 候補と候補のすき間（牌幅比）
        public float paddingRatio = 0.55f;      // 枠の内側の余白（牌幅比）
        public float headerHeightRatio = 0.62f; // 見出し帯の高さ（牌高比）
        public float canvasDepth = -0.006f;     // 牌の後ろへ下げる量（m）

        private Mahjong table_;

        public void SetTable(Mahjong table)
        {
            table_ = table;
        }

        public void Hide()
        {
            if (panelRoot != null && panelRoot.activeSelf) { panelRoot.SetActive(false); }
        }

        // pos/rot：手牌の上の中心と、立てた牌の姿勢。srcTiles：候補ごとに2枚ずつの実物の牌（メッシュと大きさを借りる）。
        public void Show(Vector3 pos, Quaternion rot, int count, Transform[] srcTiles,
                         float tileW, float tileH, int lang)
        {
            if (panelRoot == null || content == null) { return; }

            int maxOptions = optionRoots != null ? optionRoots.Length : 0;
            int n = count < maxOptions ? count : maxOptions;
            if (n <= 0) { Hide(); return; }

            transform.position = pos;
            transform.rotation = rot;
            if (!panelRoot.activeSelf) { panelRoot.SetActive(true); }

            float gap = tileW * optionGapRatio;
            float pad = tileW * paddingRatio;
            float optW = tileW * 2.0f;
            float backW = tileH * 0.95f;

            float rowW = n * optW + (n - 1) * gap;
            float totalW = rowW + gap + backW;
            float left = -totalW * 0.5f;

            float ppm = pixelsPerMeter;
            float scaleFix = content.lossyScale.x != 0.0f ? 1.0f / content.lossyScale.x : 1.0f;

            // ---- 候補 ----
            for (int k = 0; k < maxOptions; ++k)
            {
                bool on = k < n;
                if (optionRoots[k] != null && optionRoots[k].activeSelf != on) { optionRoots[k].SetActive(on); }
                if (optionPlateRects != null && k < optionPlateRects.Length && optionPlateRects[k] != null)
                {
                    optionPlateRects[k].gameObject.SetActive(on);
                }
                if (!on) { continue; }

                float cx = left + k * (optW + gap) + optW * 0.5f;
                optionRoots[k].transform.localPosition = new Vector3(cx, 0.0f, 0.0f);

                if (optionColliders != null && k < optionColliders.Length && optionColliders[k] != null)
                {
                    optionColliders[k].size = new Vector3(optW + gap * 0.8f, tileH * 1.15f, 0.03f);
                    optionColliders[k].center = Vector3.zero;
                }

                for (int t = 0; t < 2; ++t)
                {
                    int slot = k * 2 + t;
                    if (tileFilters == null || slot >= tileFilters.Length || tileFilters[slot] == null) { continue; }

                    var mf = tileFilters[slot];
                    var tf = mf.transform;
                    tf.localPosition = new Vector3((t == 0 ? -0.5f : 0.5f) * tileW, 0.0f, 0.0f);

                    var src = srcTiles != null && slot < srcTiles.Length ? srcTiles[slot] : null;
                    if (src == null) { mf.gameObject.SetActive(false); continue; }

                    var srcMf = src.GetComponent<MeshFilter>();
                    if (srcMf != null) { mf.sharedMesh = srcMf.sharedMesh; }
                    tf.localScale = src.lossyScale * scaleFix;
                    mf.gameObject.SetActive(true);
                }

                if (optionPlateRects != null && k < optionPlateRects.Length && optionPlateRects[k] != null)
                {
                    optionPlateRects[k].anchoredPosition = new Vector2(cx * ppm, 0.0f);
                    optionPlateRects[k].sizeDelta = new Vector2((optW + gap * 0.5f) * ppm, tileH * 1.12f * ppm);
                }
            }

            // ---- 戻るボタン ----
            float backX = left + rowW + gap + backW * 0.5f;
            if (backButton != null) { backButton.localPosition = new Vector3(backX, 0.0f, 0.0f); }
            if (backCollider != null)
            {
                backCollider.size = new Vector3(backW, backW, 0.03f);
                backCollider.center = Vector3.zero;
            }
            if (backPlateRect != null)
            {
                backPlateRect.anchoredPosition = new Vector2(backX * ppm, 0.0f);
                backPlateRect.sizeDelta = new Vector2(backW * ppm, backW * ppm);
            }

            // ---- 枠と見出し ----
            float panelW = totalW + pad * 2.0f;
            float headerH = tileH * headerHeightRatio;
            float top = tileH * 0.5f + pad;
            float bottom = -(tileH * 0.5f + pad);
            float panelH = top - bottom;

            if (canvasRect != null)
            {
                canvasRect.localPosition = new Vector3(0.0f, 0.0f, canvasDepth);
            }
            if (borderRect != null)
            {
                borderRect.anchoredPosition = Vector2.zero;
                borderRect.sizeDelta = new Vector2(panelW * ppm, panelH * ppm);
            }
            if (fillRect != null)
            {
                fillRect.anchoredPosition = Vector2.zero;
                fillRect.sizeDelta = new Vector2((panelW - tileW * 0.12f) * ppm, (panelH - tileW * 0.12f) * ppm);
            }
            if (headerRect != null)
            {
                // 見出しは枠の上辺にまたがせる（雀魂と同じ）。
                float headerW = Mathf.Min(panelW * 0.8f, tileW * 9.0f);
                headerRect.anchoredPosition = new Vector2(-totalW * 0.08f * ppm, (top + headerH * 0.25f) * ppm);
                headerRect.sizeDelta = new Vector2(headerW * ppm, headerH * ppm);
            }
            if (headerText != null)
            {
                headerText.text = lang == 2 ? "선택해 주세요" : lang == 3 ? "请选择" : lang == 4 ? "請選擇"
                    : lang == 1 ? "Select" : "選択してください";
            }

            if (ornamentRects != null)
            {
                for (int i = 0; i < ornamentRects.Length; ++i)
                {
                    if (ornamentRects[i] == null) { continue; }
                    float ox = (i % 2 == 0 ? -panelW : panelW) * 0.5f;
                    float oy = (i < 2 ? panelH : -panelH) * 0.5f;
                    ornamentRects[i].anchoredPosition = new Vector2(ox * ppm, oy * ppm);
                }
            }
        }

        // ---- 押下（MahjongPushButton: local=true / disableOnPush=false）----
        // 名前が「_」で始まるメソッドはネットワークイベントとして呼べない＝他人に押されない。
        public void _ChiOption0() { Pick(0); }
        public void _ChiOption1() { Pick(1); }
        public void _ChiOption2() { Pick(2); }
        public void _ChiOption3() { Pick(3); }
        public void _ChiOption4() { Pick(4); }
        public void _ChiOption5() { Pick(5); }

        public void _ChiBack()
        {
            if (table_ != null) { table_._ChiPanelBack(); }
        }

        private void Pick(int option)
        {
            if (table_ != null) { table_.ChiPanelPick(option); }
        }
    }
}
