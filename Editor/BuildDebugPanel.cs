#if UNITY_EDITOR
// デバッグパネル（モニター型）の生成。
//   Tools > Kemono Mahjong > Build Debug Panel
//
// 旧構成はボタン23個をパネル矩形の外へベタ並べしていて、見づらく押しづらかった。
// ここでは「1枚のモニター＋◀▶で項目を選ぶ」方式に作り替える。
//   ・画面表示は DebugStatusText（Text 1枚）に集約。ボタンにラベルは持たせない
//     ……のではなく、押す場所が分かるよう最小限の記号ラベル（◀ ▶ など）だけ付ける。
//   ・ボタンは従来どおり Cube+BoxCollider+MahjongPushButton（VRChat の Interact 方式）。
//     ワールド空間 UI の Button は使わない（既存の卓ボタンと操作感を揃えるため）。
//
// 冪等：2回実行しても同じ結果。旧ボタン（PresetXxx など）は掃除して作り直す。
// プレハブステージが開いていればその中身を、開いていなければシーンの全卓を対象にする。
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using SuzuFactory.AozoraMahjong;
using VRC.Udon.Common.Interfaces;

public static class BuildDebugPanel
{
    const string FontPath = "Assets/Mahjong/Fonts/NotoSansJP-Bold.ttf";

    // モニターの大きさ（Canvas 単位）。DebugPanel の localScale 0.0005 と合わせて
    // 実寸 0.36m × 0.28m 程度。手が届く距離で読める最小限。
    // 縦の割り当て（y:-330..330・重なりゼロ）：
    //   読み取り面   (0,150) 680x340 → 上端320 / 下端-20
    //   見出し1      y=-55／ボタン1段目 y=-110（高さ62 → -141..-79）
    //   見出し2      y=-155／ボタン2段目 y=-210（-241..-179）
    //   ボタン3段目  y=-285（-316..-254）
    static readonly Vector2 PanelSize = new Vector2(720f, 660f);

    const float CaptionY0 = -55f;
    const float ButtonRowY0 = -110f;   // 1段目（対象席 / CPU速度）
    const float CaptionY1 = -155f;
    const float ButtonRowY1 = -210f;   // 2段目（項目 / カテゴリ）
    const float ButtonRowY2 = -285f;   // 3段目（仕込む / クリア）

    static readonly Color ScreenBg = new Color(0.04f, 0.05f, 0.06f, 0.94f);
    static readonly Color ScreenText = new Color(0.55f, 1.00f, 0.70f);   // 端末風の緑
    static readonly Color BtnLabel = new Color(0.95f, 0.95f, 0.95f);
    // 実行系は色で区別する（白いキューブに白文字だと実機で読めなかった）。
    static readonly Color BtnLabelApply = new Color(0.55f, 1.00f, 0.70f);
    static readonly Color BtnLabelClear = new Color(1.00f, 0.62f, 0.55f);

    // ボタンのキューブは既定の白マテリアルのままだとモニターの黒地から浮いて見づらい。
    // 暗いスレート色を1枚だけ作って全ボタンで共有する（4面ぶんバッチも効く）。
    const string BtnMatPath = "Assets/Mahjong/Materials/DebugButton.mat";
    static readonly Color BtnFace = new Color(0.13f, 0.16f, 0.19f);

    [MenuItem("Tools/Kemono Mahjong/Build Debug Panel")]
    public static void Build()
    {
        var font = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
        if (font == null) { font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }

        var targets = CollectTargets();
        if (targets.Length == 0)
        {
            Debug.LogError("[BuildDebugPanel] Mahjong（卓）が見つかりません。");
            return;
        }

        int done = 0;
        foreach (var mj in targets)
        {
            if (BuildOne(mj, font)) { ++done; }
        }

        var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
        if (stage != null)
        {
            Debug.Log("[BuildDebugPanel] 完了：プレハブ " + stage.assetPath + " の DebugPanel を作り替えました（" + done + " 件）。プレハブを保存してください。");
        }
        else
        {
            EditorSceneManager.MarkSceneDirty(targets[0].gameObject.scene);
            Debug.Log("[BuildDebugPanel] 完了：" + done + "/" + targets.Length + " 卓の DebugPanel を作り替えました。シーンを保存してください。");
        }
    }

    static Mahjong[] CollectTargets()
    {
        var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
        if (stage != null)
        {
            var m = stage.prefabContentsRoot.GetComponentInChildren<Mahjong>(true);
            if (m != null) { return new Mahjong[] { m }; }
        }
        var found = Object.FindObjectsOfType<Mahjong>(true);
        System.Array.Sort(found, (a, b) => string.CompareOrdinal(GetPath(a.transform), GetPath(b.transform)));
        return found;
    }

    static bool BuildOne(Mahjong mj, Font font)
    {
        var panelGo = mj.debugPanel;
        if (panelGo == null)
        {
            Debug.LogError("[BuildDebugPanel] " + GetPath(mj.transform) + " の Mahjong.debugPanel が未割当です。");
            return false;
        }

        var panel = panelGo.transform;

        // Canvas 本体。位置・スケールは既存の調整を尊重して触らない（大きさだけ整える）。
        var rt = panelGo.GetComponent<RectTransform>();
        if (rt != null) { rt.sizeDelta = PanelSize; }

        // パネル直下に付いていた素の Text（cpuSpeedLabel の誤割当先）は表示を消す。
        // 画面表示は DebugStatusText に一本化する。
        var strayText = panelGo.GetComponent<Text>();
        if (strayText != null) { strayText.text = ""; }

        // ---- 画面（背景＋読み取りテキスト） ----
        var bg = EnsureUiChild(panel, "ScreenBg");
        var bgImg = bg.GetComponent<Image>();
        if (bgImg == null) { bgImg = bg.AddComponent<Image>(); }
        bgImg.color = ScreenBg;
        bgImg.raycastTarget = false;
        var bgRt = bg.GetComponent<RectTransform>();
        bgRt.anchorMin = new Vector2(0.5f, 0.5f);
        bgRt.anchorMax = new Vector2(0.5f, 0.5f);
        bgRt.anchoredPosition = Vector2.zero;
        bgRt.sizeDelta = PanelSize;
        bg.transform.SetAsFirstSibling();

        // 画面テキストは13行。矩形は上端320／下端-20 に置き、見出し1(上端-43)と重ならない。
        // 行数を増やすときは preferredHeight を実測してこの余白を確認すること。
        var status = EnsureText(panel, "DebugStatusText", font, 18, new Vector2(0f, 150f), new Vector2(PanelSize.x - 40f, 340f), "DEBUG");
        status.color = ScreenText;
        status.alignment = TextAnchor.UpperLeft;
        status.lineSpacing = 1.05f;

        // ---- 見出し（どのボタンが何用かを示す） ----
        var cap0a = EnsureText(panel, "CapSeat", font, 18, new Vector2(-258f, CaptionY0), new Vector2(200f, 24f), "対象席");
        cap0a.alignment = TextAnchor.MiddleCenter; cap0a.color = BtnLabel;
        var cap0b = EnsureText(panel, "CapSpeed", font, 18, new Vector2(25f, CaptionY0), new Vector2(240f, 24f), "CPU速度");
        cap0b.alignment = TextAnchor.MiddleCenter; cap0b.color = BtnLabel;
        var cap1 = EnsureText(panel, "CapPreset", font, 18, new Vector2(-258f, CaptionY1), new Vector2(200f, 24f), "項目");
        cap1.alignment = TextAnchor.MiddleCenter; cap1.color = BtnLabel;
        var cap1b = EnsureText(panel, "CapCat", font, 18, new Vector2(-18f, CaptionY1), new Vector2(200f, 24f), "カテゴリ");
        cap1b.alignment = TextAnchor.MiddleCenter; cap1b.color = BtnLabel;

        // ---- ボタン列 ----
        var btnMat = EnsureButtonMaterial();

        // 1段目：対象席 ◀ ▶ ／ CPU速度 ◀ ×2 ▶（真ん中は既定値に戻す）
        EnsureButton(panel, "SeatPrev", font, "◀", "DebugSeatPrev", new Vector2(-300f, ButtonRowY0), new Vector2(70f, 62f), btnMat, BtnLabel);
        EnsureButton(panel, "SeatNext", font, "▶", "DebugSeatNext", new Vector2(-215f, ButtonRowY0), new Vector2(70f, 62f), btnMat, BtnLabel);
        EnsureButton(panel, "SpeedDown", font, "◀", "OnCpuSpeedDown", new Vector2(-60f, ButtonRowY0), new Vector2(70f, 62f), btnMat, BtnLabel);
        EnsureButton(panel, "SpeedReset", font, "×2", "OnCpuSpeedReset", new Vector2(25f, ButtonRowY0), new Vector2(70f, 62f), btnMat, BtnLabel);
        EnsureButton(panel, "SpeedUp", font, "▶", "OnCpuSpeedUp", new Vector2(110f, ButtonRowY0), new Vector2(70f, 62f), btnMat, BtnLabel);

        // 2段目：項目 ◀ ▶ ／ カテゴリ ◀ ▶
        EnsureButton(panel, "PresetPrev", font, "◀", "DebugPresetPrev", new Vector2(-300f, ButtonRowY1), new Vector2(70f, 62f), btnMat, BtnLabel);
        EnsureButton(panel, "PresetNext", font, "▶", "DebugPresetNext", new Vector2(-215f, ButtonRowY1), new Vector2(70f, 62f), btnMat, BtnLabel);
        EnsureButton(panel, "CatPrev", font, "◀", "DebugCatPrev", new Vector2(-60f, ButtonRowY1), new Vector2(70f, 62f), btnMat, BtnLabel);
        EnsureButton(panel, "CatNext", font, "▶", "DebugCatNext", new Vector2(25f, ButtonRowY1), new Vector2(70f, 62f), btnMat, BtnLabel);

        // 3段目：仕込む ／ クリア
        EnsureButton(panel, "PresetApply", font, "仕込む", "DebugPresetApply", new Vector2(-180f, ButtonRowY2), new Vector2(200f, 62f), btnMat, BtnLabelApply);
        EnsureButton(panel, "ClearWall", font, "クリア", "DebugClearWall", new Vector2(90f, ButtonRowY2), new Vector2(180f, 62f), btnMat, BtnLabelClear);

        Cleanup(panel);

        // 参照の貼り直し（旧UIから引き継ぐ）。
        mj.debugStatusText = status;
        // cpuSpeedLabel はパネル直下の素Textを指していた誤割当。画面に一本化したので外す。
        mj.cpuSpeedLabel = null;
        UdonSharpEditorUtility.CopyProxyToUdon(mj);
        EditorUtility.SetDirty(mj);
        return true;
    }

    // 期待する子だけ残す（旧 PresetXxx / SetSeatN などを撤去して冪等に）。
    static void Cleanup(Transform panel)
    {
        var keep = new System.Collections.Generic.HashSet<string>
        {
            "ScreenBg", "DebugStatusText",
            "CapSeat", "CapSpeed", "CapPreset", "CapCat",
            "CatPrev", "CatNext",
            "SeatPrev", "SeatNext",
            "SpeedDown", "SpeedReset", "SpeedUp",
            "PresetPrev", "PresetNext", "PresetApply", "ClearWall",
        };
        for (int i = panel.childCount - 1; i >= 0; --i)
        {
            var c = panel.GetChild(i);
            if (!keep.Contains(c.name)) { Undo.DestroyObjectImmediate(c.gameObject); }
        }
    }

    // Cube + BoxCollider + MahjongPushButton の押しボタン。既存の卓ボタンと同じ方式。
    // MahjongPushButton は transform.parent.GetComponentInParent<UdonSharpBehaviour>() へ
    // 送るので、DebugPanel の親（＝卓ルートの Mahjong）に届く。
    // ボタン面の共有マテリアル。無ければ作る。
    static Material EnsureButtonMaterial()
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(BtnMatPath);
        if (mat == null)
        {
            var sh = Shader.Find("VRChat/Mobile/Toon Standard");
            if (sh == null) { sh = Shader.Find("Unlit/Color"); }
            if (sh == null) { sh = Shader.Find("Standard"); }
            mat = new Material(sh);
            var dir = System.IO.Path.GetDirectoryName(BtnMatPath);
            if (!AssetDatabase.IsValidFolder(dir)) { AssetDatabase.CreateFolder("Assets/Mahjong", "Materials"); }
            AssetDatabase.CreateAsset(mat, BtnMatPath);
        }
        mat.color = BtnFace;
        // ラベル崩壊事故（2026-07-20）の再発防止でインスタンシングは切っておく。
        mat.enableInstancing = false;
        EditorUtility.SetDirty(mat);
        AssetDatabase.SaveAssets();
        return mat;
    }

    static void EnsureButton(Transform panel, string name, Font font, string label, string id, Vector2 pos, Vector2 size, Material mat, Color labelColor)
    {
        var t = panel.Find(name);
        GameObject go;
        if (t != null) { go = t.gameObject; }
        else
        {
            go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            go.transform.SetParent(panel, false);
        }

        // 厚みは薄く。Canvas のローカル単位なのでそのまま px 換算。
        go.transform.localPosition = new Vector3(pos.x, pos.y, 0f);
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = new Vector3(size.x, size.y, 12f);

        var col = go.GetComponent<BoxCollider>();
        if (col == null) { col = go.AddComponent<BoxCollider>(); }
        col.isTrigger = false;

        // 面の見た目。既定の白マテリアルだとモニターの黒地から浮いて実機で見づらかった。
        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; // Quest配慮（影OFF方針）
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        var btn = go.GetComponent<MahjongPushButton>();
        if (btn == null) { btn = (MahjongPushButton)UdonSharpUndo.AddComponent(go, typeof(MahjongPushButton)); }
        if (btn == null)
        {
            Debug.LogError("[BuildDebugPanel] MahjongPushButton を追加できません（プログラムアセット未コンパイル？）");
            return;
        }
        btn.id = id;
        // 仕込み状態は非同期フィールドで、牌山を生成する StartGame は卓 Owner 上でだけ走る。
        // 選択操作も含めて同じ Owner に集約しないと、Owner 以外が押した内容が次局へ届かない。
        btn.local = false;
        btn.target = NetworkEventTarget.Owner;
        btn.disableOnPush = false; // 卓のボタン群を巻き添えで無効化しない
        UdonSharpEditorUtility.CopyProxyToUdon(btn);
        EditorUtility.SetDirty(btn);

        // ラベル（キューブの手前面に貼る子Text）。キューブのスケールを打ち消す。
        var lt = go.transform.Find("Text");
        GameObject lgo;
        if (lt != null) { lgo = lt.gameObject; }
        else
        {
            lgo = new GameObject("Text");
            Undo.RegisterCreatedObjectUndo(lgo, "Create Text");
            lgo.transform.SetParent(go.transform, false);
            lgo.AddComponent<RectTransform>();
        }
        var text = lgo.GetComponent<Text>();
        if (text == null) { text = lgo.AddComponent<Text>(); }
        text.font = font;
        text.fontSize = 30;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = labelColor;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.text = label;
        var lrt = lgo.GetComponent<RectTransform>();
        lrt.anchorMin = new Vector2(0.5f, 0.5f);
        lrt.anchorMax = new Vector2(0.5f, 0.5f);
        lrt.anchoredPosition = Vector2.zero;
        lrt.sizeDelta = new Vector2(size.x, size.y);
        // 親キューブの非一様スケールを打ち消して、文字を等方・手前面へ出す。
        lgo.transform.localScale = new Vector3(1f / size.x, 1f / size.y, 1f);
        lgo.transform.localPosition = new Vector3(0f, 0f, -0.55f);
    }

    static Text EnsureText(Transform parent, string name, Font font, int size, Vector2 pos, Vector2 sizeDelta, string placeholder)
    {
        var go = EnsureUiChild(parent, name);
        var text = go.GetComponent<Text>();
        if (text == null) { text = go.AddComponent<Text>(); }
        text.font = font;
        text.fontSize = size;
        text.alignment = TextAnchor.UpperLeft;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.text = placeholder;
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = sizeDelta;
        go.transform.localScale = Vector3.one;
        go.transform.localPosition = new Vector3(pos.x, pos.y, -1f);
        return text;
    }

    static GameObject EnsureUiChild(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null) { return t.gameObject; }
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Create " + name);
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();
        return go;
    }

    static string GetPath(Transform t)
    {
        var path = t.name;
        while (t.parent != null) { t = t.parent; path = t.name + "/" + path; }
        return path;
    }
}
#endif
