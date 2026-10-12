#if UNITY_EDITOR
// 自動化パネル（MahjongAutoPanel）を卓 Prefab に組み立てる。
//
//   KemonoMahjong/AutoPanel             … MahjongAutoPanel（常時アクティブ）
//     Root                              … panelRoot（表示ON/OFF）
//       Content                         … 本人の正面座標（+X＝右）を20度後ろへ傾けたもの（スキップボタンと同じ傾き）
//         TabCanvas                     … たたんだタブの絵（和・鳴・切・抜＋▲）
//         Tab                           … 当たり判定＋MahjongPushButton(_AutoTab)
//         List                          … listRoot（展開したときだけ出る）
//           ListCanvas                  … 行の絵（ランプ＋文言）
//           Row0..3                     … 当たり判定＋MahjongPushButton(_AutoItem0..3)
//
// 行の上下位置と表示/非表示（自動北抜きは三麻だけ）は実行時に MahjongAutoPanel が決める。
// 2回実行しても安全（冪等）。
// 実行: Tools > Kemono Mahjong > Build Auto Panel
//       （初回は U# プログラムアセット生成→コンパイル待ちで止まるので、もう一度実行）
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using SuzuFactory.AozoraMahjong;

public static class BuildAutoPanel
{
    const string PrefabPath = "Assets/Mahjong/Prefabas/KemonoMahjong.prefab";
    const string FontPath = "Assets/Mahjong/Fonts/NotoSansJP-Bold.ttf";
    const string ScriptPath = "Assets/Mahjong/Sources/MahjongAutoPanel.cs";
    const string ProgramAssetDir = "Assets/Mahjong/scene_UdonProgramSources";
    const string ProgramAssetPath = ProgramAssetDir + "/MahjongAutoPanel Udon C# Program Asset.asset";

    const float PixelsPerMeter = 4000f;
    const float Width = 0.13f;        // m（タブと行で共通）
    const float TabHeight = 0.032f;
    const float RowHeight = 0.030f;
    const float RowGap = 0.004f;
    const float TiltDegrees = 20f;    // スキップボタンと同じだけ上を奥へ倒す
    const float Proximity = 0.25f;    // TuneButtonReach と同じ

    static readonly Color BorderColor = new Color(0.86f, 0.66f, 0.34f, 1f);
    static readonly Color FillColor = new Color(0.07f, 0.10f, 0.22f, 0.96f);
    static readonly Color RowBorderColor = new Color(0.33f, 0.40f, 0.62f, 1f);
    static readonly Color RowFillColor = new Color(0.09f, 0.13f, 0.27f, 0.96f);
    static readonly Color ArrowColor = new Color(0.95f, 0.66f, 0.25f, 1f);

    [MenuItem("Tools/Kemono Mahjong/Build Auto Panel")]
    public static void Run()
    {
        if (EnsureProgramAsset(ScriptPath, ProgramAssetPath))
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[BuildAutoPanel] U#プログラムアセットを生成しました。コンパイル完了後、もう一度実行してください。");
            return;
        }

        var font = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
        if (font == null) { Debug.LogError("[BuildAutoPanel] フォントがありません: " + FontPath); return; }
        var rounded = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        var knob = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd");

        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null) { Debug.LogError("[BuildAutoPanel] Prefab を読み込めません: " + PrefabPath); return; }

        try
        {
            var refButton = root.transform.Find("PlayerUIWrapper/PlayerUI/PonButton");
            int layer = refButton != null ? refButton.gameObject.layer : 0;

            var panelGo = EnsureChild(root.transform, "AutoPanel");
            panelGo.SetActive(true);
            var panel = (MahjongAutoPanel)EnsureUSharpComponent(panelGo, typeof(MahjongAutoPanel));
            if (panel == null) { return; }

            var rootGo = EnsureChild(panelGo.transform, "Root");
            rootGo.SetActive(false);

            var content = EnsureChild(rootGo.transform, "Content").transform;
            content.localPosition = Vector3.zero;
            content.localRotation = Quaternion.Euler(0f, 180f, 0f) * Quaternion.Euler(TiltDegrees, 0f, 0f);
            content.localScale = Vector3.one;

            float wPx = Width * PixelsPerMeter;
            float tabPx = TabHeight * PixelsPerMeter;
            float rowPx = RowHeight * PixelsPerMeter;

            // ---- タブ ----
            var tabCanvas = EnsureCanvas(content, "TabCanvas", layer);
            var tabBorder = EnsureImage(tabCanvas, "Border", BorderColor, rounded, 0.35f);
            tabBorder.sizeDelta = new Vector2(wPx, tabPx);
            var tabFill = EnsureImage(tabBorder, "Fill", FillColor, rounded, 0.35f);
            Stretch(tabFill, 6f);

            var letters = new Text[4];
            for (int i = 0; i < 4; ++i)
            {
                letters[i] = EnsureText(tabBorder, "Letter" + i, font, 84, Color.white, "和", TextAnchor.MiddleCenter);
                var rt = letters[i].rectTransform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(100f, tabPx);
                rt.anchoredPosition = new Vector2(-195f + i * 92f, 0f);
            }
            var arrow = EnsureText(tabBorder, "Arrow", font, 56, ArrowColor, "▲", TextAnchor.MiddleCenter);
            arrow.rectTransform.anchorMin = arrow.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            arrow.rectTransform.sizeDelta = new Vector2(80f, tabPx);
            arrow.rectTransform.anchoredPosition = new Vector2(205f, 0f);

            var tab = EnsureChild(content, "Tab");
            tab.layer = layer;
            tab.transform.localPosition = Vector3.zero;
            tab.transform.localRotation = Quaternion.identity;
            EnsureTrigger(tab, new Vector3(Width, TabHeight, 0.02f));
            if (!EnsurePushButton(tab, "_AutoTab")) { return; }

            // ---- 一覧 ----
            var list = EnsureChild(content, "List");
            list.layer = layer;
            list.transform.localPosition = Vector3.zero;
            list.transform.localRotation = Quaternion.identity;
            list.SetActive(false);

            var listCanvas = EnsureCanvas(list.transform, "ListCanvas", layer);

            var rowRoots = new Transform[4];
            var rowRects = new RectTransform[4];
            var lamps = new Image[4];
            var texts = new Text[4];
            for (int i = 0; i < 4; ++i)
            {
                var rowBorder = EnsureImage(listCanvas, "Row" + i, RowBorderColor, rounded, 0.35f);
                rowBorder.sizeDelta = new Vector2(wPx, rowPx);
                var rowFill = EnsureImage(rowBorder, "Fill", RowFillColor, rounded, 0.35f);
                Stretch(rowFill, 5f);

                var lamp = EnsureImage(rowBorder, "Lamp", Color.gray, knob, 1f);
                lamp.sizeDelta = new Vector2(46f, 46f);
                lamp.anchoredPosition = new Vector2(-wPx * 0.5f + 60f, 0f);

                var label = EnsureText(rowBorder, "Label", font, 64, Color.white, "自動和了", TextAnchor.MiddleLeft);
                var lrt = label.rectTransform;
                lrt.anchorMin = lrt.anchorMax = new Vector2(0.5f, 0.5f);
                lrt.sizeDelta = new Vector2(wPx - 130f, rowPx);
                lrt.anchoredPosition = new Vector2(40f, 0f);

                var hit = EnsureChild(list.transform, "Row" + i);
                hit.layer = layer;
                hit.transform.localRotation = Quaternion.identity;
                EnsureTrigger(hit, new Vector3(Width, RowHeight, 0.02f));
                if (!EnsurePushButton(hit, "_AutoItem" + i)) { return; }

                rowRoots[i] = hit.transform;
                rowRects[i] = rowBorder;
                lamps[i] = lamp.GetComponent<Image>();
                texts[i] = label;
            }

            // ---- 配線 ----
            panel.panelRoot = rootGo;
            panel.tabLetters = letters;
            panel.tabArrow = arrow;
            panel.listRoot = list;
            panel.rowRoots = rowRoots;
            panel.rowRects = rowRects;
            panel.rowLamps = lamps;
            panel.rowTexts = texts;
            panel.pixelsPerMeter = PixelsPerMeter;
            panel.tabHeight = TabHeight;
            panel.rowHeight = RowHeight;
            panel.rowGap = RowGap;
            UdonSharpEditorUtility.CopyProxyToUdon(panel);

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log("[BuildAutoPanel] 完了: タブ＋4行の自動化パネルを組み立て、Prefab を保存しました（シーンの全卓に反映）。");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Validate();
    }

    [MenuItem("Tools/Kemono Mahjong/Validate Auto Panel")]
    public static void Validate()
    {
        int tables = 0, ok = 0;
        var sb = new System.Text.StringBuilder();
        foreach (var m in Object.FindObjectsOfType<Mahjong>(true))
        {
            ++tables;
            string why = Check(m.transform);
            if (why == null) { ++ok; } else { sb.Append(m.name).Append(": ").Append(why).Append("\n"); }
        }
        if (ok == tables) { Debug.Log("[ValidateAutoPanel] OK: 卓 " + tables + " 個すべてに自動化パネルあり・配線済み。"); }
        else { Debug.LogError("[ValidateAutoPanel] NG " + (tables - ok) + " / " + tables + "\n" + sb); }
    }

    static string Check(Transform table)
    {
        var t = table.Find("AutoPanel");
        if (t == null) { return "AutoPanel が無い"; }
        if (!t.gameObject.activeSelf) { return "AutoPanel が非アクティブ"; }
        var p = t.GetComponent<MahjongAutoPanel>();
        if (p == null) { return "MahjongAutoPanel が無い"; }
        if (p.panelRoot == null || p.listRoot == null || p.tabArrow == null) { return "panelRoot/listRoot/tabArrow の配線切れ"; }
        if (p.tabLetters == null || p.tabLetters.Length != 4) { return "tabLetters が4個でない"; }
        if (p.rowRoots == null || p.rowRoots.Length != 4 || p.rowRects == null || p.rowLamps == null || p.rowTexts == null)
        {
            return "行の配線が4個そろっていない";
        }
        for (int i = 0; i < 4; ++i)
        {
            if (p.rowRoots[i] == null || p.rowRects[i] == null || p.rowLamps[i] == null || p.rowTexts[i] == null) { return "行 " + i + " の配線切れ"; }
            var pb = p.rowRoots[i].GetComponent<MahjongPushButton>();
            if (pb == null || pb.id != "_AutoItem" + i || !pb.local || pb.disableOnPush) { return "行 " + i + " の MahjongPushButton 設定が違う"; }
        }
        foreach (var col in t.GetComponentsInChildren<Collider>(true))
        {
            if (!col.isTrigger) { return col.name + " の当たり判定が固体（プレイヤーを押し戻す）"; }
        }
        return null;
    }

    // ============================================================
    // 部品
    // ============================================================
    static Transform EnsureCanvas(Transform parent, string name, int layer)
    {
        var go = EnsureUiChild(parent, name);
        go.layer = layer;
        var canvas = go.GetComponent<Canvas>();
        if (canvas == null) { canvas = go.AddComponent<Canvas>(); }
        canvas.renderMode = RenderMode.WorldSpace;
        var rt = go.GetComponent<RectTransform>();
        rt.localPosition = Vector3.zero;
        rt.localRotation = Quaternion.identity;
        rt.localScale = Vector3.one / PixelsPerMeter;
        rt.sizeDelta = new Vector2(100f, 100f);
        var raycaster = go.GetComponent<GraphicRaycaster>();
        if (raycaster != null) { Object.DestroyImmediate(raycaster); }
        return go.transform;
    }

    static RectTransform EnsureImage(Transform parent, string name, Color color, Sprite sprite, float ppuMultiplier)
    {
        var go = EnsureUiChild(parent, name);
        go.layer = parent.gameObject.layer;
        var img = go.GetComponent<Image>();
        if (img == null) { img = go.AddComponent<Image>(); }
        img.sprite = sprite;
        img.type = sprite != null && sprite.border != Vector4.zero ? Image.Type.Sliced : Image.Type.Simple;
        img.pixelsPerUnitMultiplier = ppuMultiplier;
        img.color = color;
        img.raycastTarget = false;
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.localScale = Vector3.one;
        rt.localRotation = Quaternion.identity;
        return rt;
    }

    static Text EnsureText(Transform parent, string name, Font font, int size, Color color, string text, TextAnchor anchor)
    {
        var go = EnsureUiChild(parent, name);
        go.layer = parent.gameObject.layer;
        var tx = go.GetComponent<Text>();
        if (tx == null) { tx = go.AddComponent<Text>(); }
        tx.font = font;
        tx.fontSize = size;
        tx.color = color;
        tx.text = text;
        tx.alignment = anchor;
        tx.horizontalOverflow = HorizontalWrapMode.Overflow;
        tx.verticalOverflow = VerticalWrapMode.Overflow;
        tx.raycastTarget = false;
        var rt = go.GetComponent<RectTransform>();
        rt.localScale = Vector3.one;
        rt.localRotation = Quaternion.identity;
        return tx;
    }

    static void Stretch(RectTransform rt, float inset)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(inset, inset);
        rt.offsetMax = new Vector2(-inset, -inset);
    }

    // 卓UIの当たり判定は必ずトリガー（固体だと体が卓から押し戻される。2026-10-02 に踏んだ）。
    static void EnsureTrigger(GameObject go, Vector3 size)
    {
        var c = go.GetComponent<BoxCollider>();
        if (c == null) { c = go.AddComponent<BoxCollider>(); }
        c.isTrigger = true;
        c.center = Vector3.zero;
        c.size = size;
    }

    static bool EnsurePushButton(GameObject go, string id)
    {
        var pb = (MahjongPushButton)EnsureUSharpComponent(go, typeof(MahjongPushButton));
        if (pb == null) { return false; }
        pb.id = id;
        pb.local = true;
        pb.disableOnPush = false;
        UdonSharpEditorUtility.CopyProxyToUdon(pb);
        var ub = UdonSharpEditorUtility.GetBackingUdonBehaviour(pb);
        if (ub != null) { ub.proximity = Proximity; }
        return true;
    }

    static GameObject EnsureChild(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null) { return t.gameObject; }
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go;
    }

    static GameObject EnsureUiChild(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null && t.GetComponent<RectTransform>() != null) { return t.gameObject; }
        if (t != null) { Object.DestroyImmediate(t.gameObject); }
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go;
    }

    static bool EnsureProgramAsset(string scriptPath, string assetPath)
    {
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
        if (script == null) { Debug.LogError("[BuildAutoPanel] スクリプトが見つかりません: " + scriptPath); return false; }
        var type = script.GetClass();
        if (type != null && UdonSharpProgramAsset.GetProgramAssetForClass(type) != null) { return false; }
        if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(assetPath) != null) { return false; }

        var programAsset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
        programAsset.sourceCsScript = script;
        AssetDatabase.CreateAsset(programAsset, assetPath);
        AssetDatabase.SaveAssets();
        return true;
    }

    static UdonSharpBehaviour EnsureUSharpComponent(GameObject go, System.Type type)
    {
        var proxy = (UdonSharpBehaviour)go.GetComponent(type);
        if (proxy != null && UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy) != null) { return proxy; }
        if (proxy != null) { Object.DestroyImmediate(proxy); }

        var added = (UdonSharpBehaviour)UdonSharpUndo.AddComponent(go, type);
        if (added != null && UdonSharpEditorUtility.GetBackingUdonBehaviour(added) == null)
        {
            Debug.LogError("[BuildAutoPanel] " + type.Name + " の追加に失敗（プログラムアセット未コンパイル？）。");
            Object.DestroyImmediate(added);
            return null;
        }
        return added;
    }
}
#endif
