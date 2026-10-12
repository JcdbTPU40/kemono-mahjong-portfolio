#if UNITY_EDITOR
// カメラパネル（MahjongCameraPanel）を卓 Prefab に組み立てる。TOP CAMERA の操作盤。
//
//   KemonoMahjong/CameraPanel           … MahjongCameraPanel（常時アクティブ）
//     Root                              … panelRoot（表示ON/OFF）
//       Content                         … 本人の正面座標（+X＝右）を20度後ろへ傾けたもの（自動化パネルと同じ）
//         TabCanvas                     … たたんだタブの絵（ランプ＋カメラ＋▲）
//         Tab                           … 当たり判定＋MahjongPushButton(_CamTab)
//         List                          … listRoot（展開したときだけ出る）
//           ListCanvas                  … 行の絵（上から 視点／透明度／大きさ／表示）
//           View0..3                    … 視点の区画（_CamView0..3：自分・下家・対面・上家）
//           AlphaDown / AlphaUp         … 透明度 −／＋（_CamAlphaDown / _CamAlphaUp）
//           SizeDown / SizeUp           … 板の大きさ −／＋（_CamSizeDown / _CamSizeUp）
//           Show                        … カメラ表示 ON/OFF（_CamShow）
//
// あわせて TopCamera.mat を透過用シェーダー（KemonoMahjong/TopCameraView）へ差し替える。
// 副露の小窓カメラ（KemonoMahjong/TopCameraMelds/MeldCam0..3・添え字＝席番号）を作り、4つのボタンへ配線する。
// 卓の TOP CAMERA のキューブ（Table/TopCameraViewButton0..3）は見た目・当たり判定・文字を外し、
// スクリプトだけ残す（カメラの向きの計算と板を出す位置としてパネルが使う）。
// 視点の区画の横位置と三麻の伏せは実行時に MahjongCameraPanel が決める。
// 2回実行しても安全（冪等）。
// 実行: Tools > Kemono Mahjong > Build Camera Panel
//       （初回は U# プログラムアセット生成→コンパイル待ちで止まるので、もう一度実行）
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using SuzuFactory.AozoraMahjong;

public static class BuildCameraPanel
{
    const string PrefabPath = "Assets/Mahjong/Prefabas/KemonoMahjong.prefab";
    const string FontPath = "Assets/Mahjong/Fonts/NotoSansJP-Bold.ttf";
    const string ScriptPath = "Assets/Mahjong/Sources/MahjongCameraPanel.cs";
    const string ProgramAssetDir = "Assets/Mahjong/scene_UdonProgramSources";
    const string ProgramAssetPath = ProgramAssetDir + "/MahjongCameraPanel Udon C# Program Asset.asset";
    const string ViewMaterialPath = "Assets/Mahjong/Materials/TopCamera.mat";
    const string ViewShaderName = "KemonoMahjong/TopCameraView";

    const float PixelsPerMeter = 4000f;
    const float Width = 0.12f;        // m（タブと行で共通）
    const float TabHeight = 0.032f;
    const float RowHeight = 0.030f;
    const float RowGap = 0.004f;
    const float SegmentGap = 0.002f;
    const float StepButtonWidth = 0.028f;  // 透明度の −／＋ の当たり判定の幅
    const float TiltDegrees = 20f;    // 自動化パネル・スキップボタンと同じだけ上を奥へ倒す
    const float Proximity = 0.25f;    // TuneButtonReach と同じ

    // 上から何段目か。一覧は下へ開くので、よく使うものほどタブの近く（上）に置く（2026-10-05）。
    const int RowShow = 0;
    const int RowSize = 1;
    const int RowAlpha = 2;
    const int RowView = 3;
    const int RowCount = 4;

    static readonly Color BorderColor = new Color(0.86f, 0.66f, 0.34f, 1f);
    static readonly Color FillColor = new Color(0.07f, 0.10f, 0.22f, 0.96f);
    static readonly Color RowBorderColor = new Color(0.33f, 0.40f, 0.62f, 1f);
    static readonly Color RowFillColor = new Color(0.09f, 0.13f, 0.27f, 0.96f);
    static readonly Color ArrowColor = new Color(0.95f, 0.66f, 0.25f, 1f);
    static readonly Color MeldInsetBackground = new Color(0.06f, 0.08f, 0.16f, 1f);

    [MenuItem("Tools/Kemono Mahjong/Build Camera Panel")]
    public static void Run()
    {
        if (EnsureProgramAsset(ScriptPath, ProgramAssetPath))
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[BuildCameraPanel] U#プログラムアセットを生成しました。コンパイル完了後、もう一度実行してください。");
            return;
        }

        var font = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
        if (font == null) { Debug.LogError("[BuildCameraPanel] フォントがありません: " + FontPath); return; }
        var rounded = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        var knob = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd");

        if (!ApplyViewShader()) { return; }

        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null) { Debug.LogError("[BuildCameraPanel] Prefab を読み込めません: " + PrefabPath); return; }

        try
        {
            var refButton = root.transform.Find("PlayerUIWrapper/PlayerUI/PonButton");
            int layer = refButton != null ? refButton.gameObject.layer : 0;

            // ---- 各席の TOP CAMERA ボタン（添え字＝席番号）----
            var viewButtons = new MahjongTopCameraViewButton[4];
            for (int i = 0; i < 4; ++i)
            {
                var t = root.transform.Find("Table/TopCameraViewButton" + i);
                viewButtons[i] = t != null ? t.GetComponent<MahjongTopCameraViewButton>() : null;
                if (viewButtons[i] == null) { Debug.LogError("[BuildCameraPanel] Table/TopCameraViewButton" + i + " がありません。"); return; }
                string why = CheckSeatDirection(root.transform, viewButtons[i], i);
                if (why != null) { Debug.LogError("[BuildCameraPanel] " + why); return; }
                StripCubeButton(viewButtons[i].gameObject);
            }

            // ---- 副露の小窓カメラ ----
            var meldCams = BuildMeldCams(root.transform);
            if (meldCams == null) { return; }
            for (int i = 0; i < 4; ++i)
            {
                viewButtons[i].meldCams = meldCams;
                UdonSharpEditorUtility.CopyProxyToUdon(viewButtons[i]);
            }

            var panelGo = EnsureChild(root.transform, "CameraPanel");
            panelGo.SetActive(true);
            var panel = (MahjongCameraPanel)EnsureUSharpComponent(panelGo, typeof(MahjongCameraPanel));
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
            tabBorder.anchoredPosition = Vector2.zero;
            var tabFill = EnsureImage(tabBorder, "Fill", FillColor, rounded, 0.35f);
            Stretch(tabFill, 6f);

            var tabLamp = EnsureImage(tabBorder, "Lamp", Color.gray, knob, 1f);
            tabLamp.sizeDelta = new Vector2(40f, 40f);
            tabLamp.anchoredPosition = new Vector2(-wPx * 0.5f + 52f, 0f);

            var title = EnsureText(tabBorder, "Title", font, 64, Color.white, "カメラ", TextAnchor.MiddleCenter);
            title.rectTransform.anchorMin = title.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            title.rectTransform.sizeDelta = new Vector2(wPx - 200f, tabPx);
            title.rectTransform.anchoredPosition = new Vector2(-6f, 0f);

            var arrow = EnsureText(tabBorder, "Arrow", font, 56, ArrowColor, "▲", TextAnchor.MiddleCenter);
            arrow.rectTransform.anchorMin = arrow.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            arrow.rectTransform.sizeDelta = new Vector2(80f, tabPx);
            arrow.rectTransform.anchoredPosition = new Vector2(wPx * 0.5f - 45f, 0f);

            var tab = EnsureChild(content, "Tab");
            tab.layer = layer;
            tab.transform.localPosition = Vector3.zero;
            tab.transform.localRotation = Quaternion.identity;
            tab.transform.localScale = Vector3.one;
            EnsureTrigger(tab, new Vector3(Width, TabHeight, 0.02f));
            if (!EnsurePushButton(tab, "_CamTab")) { return; }

            // ---- 一覧 ----
            var list = EnsureChild(content, "List");
            list.layer = layer;
            list.transform.localPosition = Vector3.zero;
            list.transform.localRotation = Quaternion.identity;
            list.transform.localScale = Vector3.one;
            list.SetActive(false);

            var listCanvas = EnsureCanvas(list.transform, "ListCanvas", layer);

            // 視点（上の行）：4区画。横位置と幅は実行時に並べ直す（三麻は3等分）。
            float yView = RowY(RowView);
            float segW = Width / 4f;
            var viewHits = new Transform[4];
            var viewRects = new RectTransform[4];
            var viewFills = new Image[4];
            var viewTexts = new Text[4];
            string[] jaViews = { "自分", "下家", "対面", "上家" };
            for (int i = 0; i < 4; ++i)
            {
                float x = -Width * 0.5f + segW * (i + 0.5f);
                var segBorder = EnsureImage(listCanvas, "View" + i, RowBorderColor, rounded, 0.35f);
                segBorder.sizeDelta = new Vector2((segW - SegmentGap) * PixelsPerMeter, rowPx);
                segBorder.anchoredPosition = new Vector2(x * PixelsPerMeter, yView * PixelsPerMeter);
                var segFill = EnsureImage(segBorder, "Fill", RowFillColor, rounded, 0.35f);
                Stretch(segFill, 5f);
                var label = EnsureText(segBorder, "Label", font, 48, Color.white, jaViews[i], TextAnchor.MiddleCenter);
                Stretch(label.rectTransform, 0f);

                var hit = EnsureChild(list.transform, "View" + i);
                hit.layer = layer;
                hit.transform.localPosition = new Vector3(x, yView, 0f);
                hit.transform.localRotation = Quaternion.identity;
                hit.transform.localScale = Vector3.one;
                EnsureTrigger(hit, new Vector3(segW - SegmentGap, RowHeight, 0.02f));
                if (!EnsurePushButton(hit, "_CamView" + i)) { return; }

                viewHits[i] = hit.transform;
                viewRects[i] = segBorder;
                viewFills[i] = segFill.GetComponent<Image>();
                viewTexts[i] = label;
            }

            // 透明度・大きさ：− ／ 文言 ／ ＋
            var alphaText = BuildStepRow(listCanvas, list.transform, "Alpha", RowY(RowAlpha), "透明度 0%",
                                         "_CamAlphaDown", "_CamAlphaUp", font, rounded, layer);
            if (alphaText == null) { return; }
            var sizeText = BuildStepRow(listCanvas, list.transform, "Size", RowY(RowSize), "大きさ 100%",
                                        "_CamSizeDown", "_CamSizeUp", font, rounded, layer);
            if (sizeText != null) { sizeText.fontSize = 44; }   // 実行時の大きさと揃える
            if (sizeText == null) { return; }

            // 表示（下の行）：ランプ＋カメラ表示
            float yShow = RowY(RowShow);
            var showBorder = EnsureImage(listCanvas, "Show", RowBorderColor, rounded, 0.35f);
            showBorder.sizeDelta = new Vector2(wPx, rowPx);
            showBorder.anchoredPosition = new Vector2(0f, yShow * PixelsPerMeter);
            var showFill = EnsureImage(showBorder, "Fill", RowFillColor, rounded, 0.35f);
            Stretch(showFill, 5f);
            var showLamp = EnsureImage(showBorder, "Lamp", Color.gray, knob, 1f);
            showLamp.sizeDelta = new Vector2(46f, 46f);
            showLamp.anchoredPosition = new Vector2(-wPx * 0.5f + 60f, 0f);
            var showText = EnsureText(showBorder, "Label", font, 60, Color.white, "カメラ表示", TextAnchor.MiddleLeft);
            showText.rectTransform.anchorMin = showText.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            showText.rectTransform.sizeDelta = new Vector2(wPx - 130f, rowPx);
            showText.rectTransform.anchoredPosition = new Vector2(40f, 0f);

            var show = EnsureChild(list.transform, "Show");
            show.layer = layer;
            show.transform.localPosition = new Vector3(0f, yShow, 0f);
            show.transform.localRotation = Quaternion.identity;
            show.transform.localScale = Vector3.one;
            EnsureTrigger(show, new Vector3(Width, RowHeight, 0.02f));
            if (!EnsurePushButton(show, "_CamShow")) { return; }

            // ---- 配線 ----
            panel.panelRoot = rootGo;
            panel.tabTitle = title;
            panel.tabLamp = tabLamp.GetComponent<Image>();
            panel.tabArrow = arrow;
            panel.listRoot = list;
            panel.viewHits = viewHits;
            panel.viewRects = viewRects;
            panel.viewFills = viewFills;
            panel.viewTexts = viewTexts;
            panel.alphaText = alphaText;
            panel.sizeText = sizeText;
            panel.showLamp = showLamp.GetComponent<Image>();
            panel.showText = showText;
            panel.viewButtons = viewButtons;
            panel.pixelsPerMeter = PixelsPerMeter;
            panel.width = Width;
            panel.segmentGap = SegmentGap;
            UdonSharpEditorUtility.CopyProxyToUdon(panel);

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log("[BuildCameraPanel] 完了: カメラパネル（タブ＋視点／透明度／大きさ／表示）を組み立て、Prefab を保存しました（シーンの全卓に反映）。");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Validate();
    }

    // − ／ 文言 ／ ＋ の1行（name の絵と、name+"Down"／name+"Up" の当たり判定）。失敗したら null。
    static Text BuildStepRow(Transform listCanvas, Transform list, string name, float y, string initial,
                             string downId, string upId, Font font, Sprite rounded, int layer)
    {
        float wPx = Width * PixelsPerMeter;
        float rowPx = RowHeight * PixelsPerMeter;
        float stepPx = StepButtonWidth * PixelsPerMeter;

        var border = EnsureImage(listCanvas, name, RowBorderColor, rounded, 0.35f);
        border.sizeDelta = new Vector2(wPx, rowPx);
        border.anchoredPosition = new Vector2(0f, y * PixelsPerMeter);
        var fill = EnsureImage(border, "Fill", RowFillColor, rounded, 0.35f);
        Stretch(fill, 5f);
        var minus = EnsureText(border, "Minus", font, 72, ArrowColor, "−", TextAnchor.MiddleCenter);
        minus.rectTransform.anchorMin = minus.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        minus.rectTransform.sizeDelta = new Vector2(stepPx, rowPx);
        minus.rectTransform.anchoredPosition = new Vector2(-wPx * 0.5f + stepPx * 0.5f, 0f);
        var plus = EnsureText(border, "Plus", font, 72, ArrowColor, "＋", TextAnchor.MiddleCenter);
        plus.rectTransform.anchorMin = plus.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        plus.rectTransform.sizeDelta = new Vector2(stepPx, rowPx);
        plus.rectTransform.anchoredPosition = new Vector2(wPx * 0.5f - stepPx * 0.5f, 0f);
        var label = EnsureText(border, "Label", font, 52, Color.white, initial, TextAnchor.MiddleCenter);
        label.rectTransform.anchorMin = label.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        label.rectTransform.sizeDelta = new Vector2(wPx - stepPx * 2f, rowPx);
        label.rectTransform.anchoredPosition = Vector2.zero;

        var down = EnsureChild(list, name + "Down");
        down.layer = layer;
        down.transform.localPosition = new Vector3(-Width * 0.5f + StepButtonWidth * 0.5f, y, 0f);
        down.transform.localRotation = Quaternion.identity;
        down.transform.localScale = Vector3.one;
        EnsureTrigger(down, new Vector3(StepButtonWidth, RowHeight, 0.02f));
        if (!EnsurePushButton(down, downId)) { return null; }

        var up = EnsureChild(list, name + "Up");
        up.layer = layer;
        up.transform.localPosition = new Vector3(Width * 0.5f - StepButtonWidth * 0.5f, y, 0f);
        up.transform.localRotation = Quaternion.identity;
        up.transform.localScale = Vector3.one;
        EnsureTrigger(up, new Vector3(StepButtonWidth, RowHeight, 0.02f));
        if (!EnsurePushButton(up, upId)) { return null; }

        return label;
    }

    // 副露の小窓カメラ4台。主カメラ（TopCamera）と同じ RenderTexture に、主カメラの後から描く。
    // 位置・向き・描く範囲は実行時にボタンが決めるので、ここでは固定の設定と無効化だけ。
    static Camera[] BuildMeldCams(Transform table)
    {
        var mainT = table.Find("TopCamera");
        var main = mainT != null ? mainT.GetComponent<Camera>() : null;
        if (main == null || main.targetTexture == null) { Debug.LogError("[BuildCameraPanel] TopCamera（RenderTexture 付き）がありません。"); return null; }

        var holder = EnsureChild(table, "TopCameraMelds");
        holder.SetActive(true);
        holder.transform.localPosition = Vector3.zero;
        holder.transform.localRotation = Quaternion.identity;
        holder.transform.localScale = Vector3.one;

        var cams = new Camera[4];
        for (int i = 0; i < 4; ++i)
        {
            var go = EnsureChild(holder.transform, "MeldCam" + i);
            go.SetActive(true);
            var cam = go.GetComponent<Camera>();
            if (cam == null) { cam = go.AddComponent<Camera>(); }
            cam.enabled = false;
            cam.orthographic = true;
            cam.orthographicSize = 0.05f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = MeldInsetBackground;
            cam.cullingMask = main.cullingMask;
            cam.targetTexture = main.targetTexture;
            cam.depth = main.depth + 1 + i;
            cam.nearClipPlane = 0.26f;
            cam.farClipPlane = 0.32f;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.useOcclusionCulling = false;
            var listener = go.GetComponent<AudioListener>();
            if (listener != null) { Object.DestroyImmediate(listener); }
            cams[i] = cam;
        }
        return cams;
    }

    // TOP CAMERA のキューブをパネルに置き換えたので、キューブの見た目・当たり判定・文字を外す。
    // GameObject とスクリプトは残す（非アクティブにすると Udon が初期化されず、パネルからの呼び出しが届かない）。
    static void StripCubeButton(GameObject go)
    {
        for (int i = go.transform.childCount - 1; i >= 0; --i)
        {
            Object.DestroyImmediate(go.transform.GetChild(i).gameObject);
        }
        foreach (var c in go.GetComponents<Collider>()) { Object.DestroyImmediate(c); }
        var r = go.GetComponent<MeshRenderer>();
        if (r != null) { Object.DestroyImmediate(r); }
        var f = go.GetComponent<MeshFilter>();
        if (f != null) { Object.DestroyImmediate(f); }
    }

    // タブ（中心 y=0）の下に、上の行から積む（下へ開く。パネルは天板より下にある）。
    static float RowY(int row)
    {
        return -(TabHeight * 0.5f + RowGap + RowHeight * 0.5f + row * (RowHeight + RowGap));
    }

    // 板のマテリアルを透過用シェーダーへ（テクスチャはそのまま）。
    static bool ApplyViewShader()
    {
        var shader = Shader.Find(ViewShaderName);
        if (shader == null) { Debug.LogError("[BuildCameraPanel] シェーダーがありません: " + ViewShaderName); return false; }
        var mat = AssetDatabase.LoadAssetAtPath<Material>(ViewMaterialPath);
        if (mat == null) { Debug.LogError("[BuildCameraPanel] マテリアルがありません: " + ViewMaterialPath); return false; }
        if (mat.shader != shader)
        {
            var tex = mat.mainTexture;
            mat.shader = shader;
            mat.mainTexture = tex;
            mat.SetFloat("_Alpha", 1f);
            mat.enableInstancing = false;
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssetIfDirty(mat);
        }
        return true;
    }

    // ボタン i が席 i の側を向いているか（卓ルート座標で -right ＝ 席 i の方向）。
    static string CheckSeatDirection(Transform table, MahjongTopCameraViewButton b, int seat)
    {
        var d = table.InverseTransformDirection(-b.transform.right);
        d.y = 0f;
        var expect = Quaternion.Euler(0f, seat * -90f, 0f) * Vector3.right;
        if (d.sqrMagnitude < 1e-6f || Vector3.Dot(d.normalized, expect) < 0.99f)
        {
            return "TopCameraViewButton" + seat + " が席" + seat + "の向きでない（-right=" + d + "）";
        }
        return null;
    }

    [MenuItem("Tools/Kemono Mahjong/Validate Camera Panel")]
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
        var mat = AssetDatabase.LoadAssetAtPath<Material>(ViewMaterialPath);
        if (mat == null || mat.shader == null || mat.shader.name != ViewShaderName)
        {
            sb.Append(ViewMaterialPath).Append(": シェーダーが ").Append(ViewShaderName).Append(" でない\n");
            ok = -1;
        }
        if (ok == tables) { Debug.Log("[ValidateCameraPanel] OK: 卓 " + tables + " 個すべてにカメラパネルあり・配線済み。"); }
        else { Debug.LogError("[ValidateCameraPanel] NG\n" + sb); }
    }

    static string Check(Transform table)
    {
        var t = table.Find("CameraPanel");
        if (t == null) { return "CameraPanel が無い"; }
        if (!t.gameObject.activeSelf) { return "CameraPanel が非アクティブ"; }
        var p = t.GetComponent<MahjongCameraPanel>();
        if (p == null) { return "MahjongCameraPanel が無い"; }
        if (p.panelRoot == null || p.listRoot == null || p.tabArrow == null || p.tabTitle == null || p.tabLamp == null)
        {
            return "panelRoot/listRoot/タブの配線切れ";
        }
        if (p.alphaText == null || p.sizeText == null || p.showLamp == null || p.showText == null) { return "透明度/大きさ/表示の行の配線切れ"; }
        if (p.viewHits == null || p.viewHits.Length != 4 || p.viewRects == null || p.viewRects.Length != 4 ||
            p.viewFills == null || p.viewFills.Length != 4 || p.viewTexts == null || p.viewTexts.Length != 4)
        {
            return "視点の区画が4個そろっていない";
        }
        for (int i = 0; i < 4; ++i)
        {
            if (p.viewHits[i] == null || p.viewRects[i] == null || p.viewFills[i] == null || p.viewTexts[i] == null) { return "視点 " + i + " の配線切れ"; }
            string why = CheckButton(p.viewHits[i], "_CamView" + i);
            if (why != null) { return why; }
        }
        string[] names = { "Tab", "_CamTab", "AlphaDown", "_CamAlphaDown", "AlphaUp", "_CamAlphaUp",
                           "SizeDown", "_CamSizeDown", "SizeUp", "_CamSizeUp", "Show", "_CamShow" };
        for (int k = 0; k < names.Length; k += 2)
        {
            Transform hit = names[k] == "Tab" ? t.Find("Root/Content/Tab") : t.Find("Root/Content/List/" + names[k]);
            if (hit == null) { return names[k] + " が無い"; }
            string why = CheckButton(hit, names[k + 1]);
            if (why != null) { return why; }
        }
        if (p.viewButtons == null || p.viewButtons.Length != 4) { return "viewButtons が4個でない"; }
        for (int i = 0; i < 4; ++i)
        {
            if (p.viewButtons[i] == null) { return "viewButtons[" + i + "] の配線切れ"; }
            var bgo = p.viewButtons[i].gameObject;
            if (!bgo.activeSelf) { return bgo.name + " が非アクティブ（パネルからの呼び出しが届かない）"; }
            if (bgo.GetComponent<Collider>() != null || bgo.GetComponent<Renderer>() != null || bgo.transform.childCount > 0)
            {
                return bgo.name + " にキューブ（見た目/当たり判定/文字）が残っている";
            }
            string why = CheckSeatDirection(table, p.viewButtons[i], i);
            if (why != null) { return why; }
            var mc = p.viewButtons[i].meldCams;
            if (mc == null || mc.Length != 4) { return bgo.name + " の副露カメラ（meldCams）が4台でない"; }
            for (int s2 = 0; s2 < 4; ++s2)
            {
                if (mc[s2] == null) { return bgo.name + " の meldCams[" + s2 + "] の配線切れ"; }
                if (mc[s2].transform.parent == null || mc[s2].transform.parent.parent != table) { return "meldCams[" + s2 + "] が別の卓のカメラ"; }
                if (mc[s2].targetTexture == null) { return "meldCams[" + s2 + "] に RenderTexture が無い"; }
            }
        }
        foreach (var col in t.GetComponentsInChildren<Collider>(true))
        {
            if (!col.isTrigger) { return col.name + " の当たり判定が固体（プレイヤーを押し戻す）"; }
        }
        return null;
    }

    static string CheckButton(Transform hit, string id)
    {
        var pb = hit.GetComponent<MahjongPushButton>();
        if (pb == null || pb.id != id || !pb.local || pb.disableOnPush) { return hit.name + " の MahjongPushButton 設定が違う（" + id + "）"; }
        return null;
    }

    // ============================================================
    // 部品（BuildAutoPanel と同じ）
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
        if (script == null) { Debug.LogError("[BuildCameraPanel] スクリプトが見つかりません: " + scriptPath); return false; }
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
            Debug.LogError("[BuildCameraPanel] " + type.Name + " の追加に失敗（プログラムアセット未コンパイル？）。");
            Object.DestroyImmediate(added);
            return null;
        }
        return added;
    }
}
#endif
