#if UNITY_EDITOR
using System;
using TMPro;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using SuzuFactory.AozoraMahjong;
using VRC.SDK3.Components;
using VRC.Udon;

/// <summary>
/// MainMenuの隣へWorld Spaceの段位補正モニターを生成し、UdonSharpとボタンを配線する。
/// 同名オブジェクトを再利用するため、何度実行しても重複生成しない。
/// </summary>
public static class BuildRankAdminMonitor
{
    private const string ProgramAssetDir = "Assets/Mahjong/scene_UdonProgramSources";
    private const string FontAssetPath = "Assets/Mahjong/Fonts/NotoSansJP-Bold SDF.asset";
    private const float MonitorWidth = 1050f;
    private const float MonitorHeight = 1080f;
    private const string ConfirmRootName = "RankConfirmMonitor";

    private static TMP_FontAsset font_;

    [MenuItem("Tools/Kemono Mahjong/Build Rank Admin Monitor")]
    public static void Run()
    {
        GameObject mainMenu = GameObject.Find("MainMenu");
        if (mainMenu == null)
        {
            Debug.LogError("[BuildRankAdminMonitor] MainMenuが見つかりません。");
            return;
        }
        Canvas mainCanvas = mainMenu.GetComponent<Canvas>();
        if (mainCanvas == null || mainCanvas.renderMode != RenderMode.WorldSpace)
        {
            Debug.LogError("[BuildRankAdminMonitor] MainMenuがWorld Space Canvasではありません。");
            return;
        }

        font_ = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
        if (font_ == null)
        {
            Debug.LogError("[BuildRankAdminMonitor] 日本語TMPフォントが見つかりません: " + FontAssetPath);
            return;
        }

        GameObject root = FindSceneObject("RankAdminMonitor");
        bool createdRoot = root == null;
        if (createdRoot)
        {
            root = new GameObject("RankAdminMonitor", typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(root, "Build Rank Admin Monitor");
            PlaceNextToMainMenu((RectTransform)root.transform, (RectTransform)mainMenu.transform);
        }
        EnsureRootComponents(root);
        BuildAdminUi(root.transform);
        SetLayerRecursively(root, 0);

        // ConfirmationPanelは対象本人向けなので別Canvasへ分離する。
        // 同じCanvasの子のまま位置を動かすと平面から外れ、描画だけ残って触れなくなる。
        GameObject confirmRoot = FindSceneObject(ConfirmRootName);
        if (confirmRoot == null)
        {
            confirmRoot = new GameObject(ConfirmRootName, typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(confirmRoot, "Build Rank Admin Monitor");
            PlaceConfirmMonitorDefault((RectTransform)confirmRoot.transform, (RectTransform)root.transform);
        }
        // 既存なら位置・回転には触れない＝Sceneビューで自由に置ける。
        EnsureRootComponents(confirmRoot);
        BuildConfirmUi(confirmRoot.transform);
        SetLayerRecursively(confirmRoot, 0);
        Transform legacyConfirm = root.transform.Find("ConfirmationPanel");
        if (legacyConfirm != null) { Undo.DestroyObjectImmediate(legacyConfirm.gameObject); }

        bool createdProgramAsset = false;
        createdProgramAsset |= EnsureProgramAsset(
            "Assets/Mahjong/Sources/PlayerStatsStore.cs",
            ProgramAssetDir + "/PlayerStatsStore Udon C# Program Asset.asset");
        createdProgramAsset |= EnsureProgramAsset(
            "Assets/Mahjong/Sources/RankAdminMonitor.cs",
            ProgramAssetDir + "/RankAdminMonitor Udon C# Program Asset.asset");
        if (createdProgramAsset)
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            EditorSceneManager.MarkSceneDirty(root.scene);
            Debug.Log("[BuildRankAdminMonitor] U# Program Assetを生成しました。コンパイル完了後にもう一度実行してください。");
            return;
        }

        PlayerStatsStore store = UnityEngine.Object.FindObjectOfType<PlayerStatsStore>(true);
        if (store == null)
        {
            Debug.LogError("[BuildRankAdminMonitor] PlayerStatsStoreが見つかりません。先にWire Main Menuを実行してください。");
            return;
        }

        RankAdminMonitor monitor = (RankAdminMonitor)EnsureUSharpComponent(root, typeof(RankAdminMonitor));
        if (monitor == null) { return; }

        Transform t = root.transform;
        Transform ct = confirmRoot.transform;
        monitor.statsStore = store;
        monitor.adminPanel = Find(t, "AdminPanel").gameObject;
        // パネルではなくCanvasルートごとON/OFFする。非表示中はコライダーも消える。
        monitor.confirmationPanel = confirmRoot;
        monitor.targetText = FindTmp(t, "AdminPanel/TargetText");
        monitor.currentRankText = FindTmp(t, "AdminPanel/CurrentRankText");
        monitor.draftRankText = FindTmp(t, "AdminPanel/DraftRankText");
        monitor.rankPointInput = FindInput(t, "AdminPanel/RankPointInput");
        monitor.tenjuOrbX10Input = FindInput(t, "AdminPanel/TenjuOrbX10Input");
        monitor.adminStatusText = FindTmp(t, "AdminPanel/AdminStatus");
        monitor.confirmationText = FindTmp(ct, "ConfirmationPanel/ConfirmationText");
        monitor.confirmationStatusText = FindTmp(ct, "ConfirmationPanel/ConfirmationStatus");

        UdonSharpEditorUtility.CopyProxyToUdon(monitor);
        EditorUtility.SetDirty(monitor);

        UdonBehaviour udon = UdonSharpEditorUtility.GetBackingUdonBehaviour(monitor);
        if (udon == null)
        {
            Debug.LogError("[BuildRankAdminMonitor] RankAdminMonitorのbacking UdonBehaviourが見つかりません。");
            return;
        }
        // 新規UdonBehaviourはEditor既定値がContinuousになる場合があるため、実コンポーネントも明示的にManualへ固定する。
        udon.SyncMethod = VRC.SDKBase.Networking.SyncType.Manual;
        EditorUtility.SetDirty(udon);

        int wired = 0;
        wired += WireButton(t, "AdminPanel/BtnTargetPrev", udon, "TargetPrev");
        wired += WireButton(t, "AdminPanel/BtnTargetNext", udon, "TargetNext");
        wired += WireButton(t, "AdminPanel/BtnRankPrev", udon, "RankPrev");
        wired += WireButton(t, "AdminPanel/BtnRankNext", udon, "RankNext");
        wired += WireButton(t, "AdminPanel/BtnLoad", udon, "LoadSelected");
        wired += WireButton(t, "AdminPanel/BtnSend", udon, "SendCorrection");
        wired += WireButton(ct, "ConfirmationPanel/BtnAccept", udon, "AcceptCorrection");
        wired += WireButton(ct, "ConfirmationPanel/BtnReject", udon, "RejectCorrection");

        Find(t, "AdminPanel").gameObject.SetActive(true);
        confirmRoot.SetActive(false);
        EditorSceneManager.MarkSceneDirty(root.scene);
        Debug.Log("[BuildRankAdminMonitor] 完了: MainMenu隣へ生成・配線 " + wired + "件。権限者は「<STAFF_NAME_1>」「<STAFF_NAME_2>」の完全一致です。");
    }

    private static void EnsureRootComponents(GameObject root)
    {
        RectTransform rt = root.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(MonitorWidth, MonitorHeight);

        Canvas canvas = root.GetComponent<Canvas>();
        if (canvas == null) { canvas = Undo.AddComponent<Canvas>(root); }
        canvas.renderMode = RenderMode.WorldSpace;

        CanvasScaler scaler = root.GetComponent<CanvasScaler>();
        if (scaler == null) { scaler = Undo.AddComponent<CanvasScaler>(root); }
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.dynamicPixelsPerUnit = 10f;

        if (root.GetComponent<GraphicRaycaster>() == null) { Undo.AddComponent<GraphicRaycaster>(root); }
        if (root.GetComponent<VRCUiShape>() == null) { Undo.AddComponent<VRCUiShape>(root); }

        // VRCUiShapeだけではレーザー/ポインタは当たらない。VRChatもClientSimも
        // 物理レイキャストでコライダーを拾ってから同オブジェクトのVRC_UiShapeを見るため、
        // Canvasのrectと同じ大きさのBoxColliderが必須（既存のMainUIと同じtrigger方式）。
        BoxCollider shapeCollider = root.GetComponent<BoxCollider>();
        if (shapeCollider == null) { shapeCollider = Undo.AddComponent<BoxCollider>(root); }
        shapeCollider.isTrigger = true;
        shapeCollider.size = new Vector3(MonitorWidth, MonitorHeight, 0.31f);
        shapeCollider.center = new Vector3(0f, 0f, 0.16f);
    }

    private static void PlaceNextToMainMenu(RectTransform monitor, RectTransform mainMenu)
    {
        monitor.SetParent(mainMenu.parent, false);
        monitor.localRotation = mainMenu.localRotation;
        monitor.localScale = mainMenu.localScale;
        monitor.sizeDelta = new Vector2(MonitorWidth, MonitorHeight);

        float mainWidth = mainMenu.rect.width > 0f ? mainMenu.rect.width : 1920f;
        float scaleX = Mathf.Abs(mainMenu.localScale.x);
        float offset = (mainWidth * 0.5f + MonitorWidth * 0.5f) * scaleX + 0.1f;
        monitor.localPosition = mainMenu.localPosition + mainMenu.localRotation * (Vector3.right * offset);
    }

    private static void PlaceConfirmMonitorDefault(RectTransform confirm, RectTransform monitor)
    {
        // 分離前に手で置かれていた場所（モニターのローカルz -974／さらにY270度）を再現する。
        confirm.SetParent(null, false);
        confirm.localScale = monitor.localScale;
        confirm.sizeDelta = new Vector2(MonitorWidth, MonitorHeight);
        confirm.rotation = monitor.rotation * Quaternion.Euler(0f, 270f, 0f);
        confirm.position = monitor.TransformPoint(new Vector3(0f, 0f, -974f));
    }

    private static void BuildAdminUi(Transform root)
    {
        Transform admin = FullPanel(root, "AdminPanel", new Color(0.025f, 0.05f, 0.09f, 0.98f), true);
        Text(admin, "Title", "段位補正モニター", 46f, new Vector2(0f, -35f), new Vector2(980f, 70f));
        Text(admin, "Authorized", "権限: <STAFF_NAME_1> / <STAFF_NAME_2>", 24f, new Vector2(0f, -100f), new Vector2(980f, 45f));
        Text(admin, "Warning", "対象本人の承認後に保存されます。対局中は補正しないでください。", 25f,
            new Vector2(0f, -150f), new Vector2(980f, 55f), new Color(1f, 0.73f, 0.25f, 1f));

        Button(admin, "BtnTargetPrev", "◀", new Vector2(35f, -220f), new Vector2(120f, 68f), 34f);
        Text(admin, "TargetText", "対象: （未選択）", 30f, new Vector2(175f, -220f), new Vector2(700f, 68f), Color.white,
            TextAlignmentOptions.Left);
        Button(admin, "BtnTargetNext", "▶", new Vector2(895f, -220f), new Vector2(120f, 68f), 34f);
        Text(admin, "CurrentRankText", "現在: --", 27f, new Vector2(45f, -298f), new Vector2(960f, 55f), Color.white,
            TextAlignmentOptions.Left);

        Button(admin, "BtnRankPrev", "◀", new Vector2(35f, -375f), new Vector2(120f, 68f), 34f);
        Text(admin, "DraftRankText", "変更後: 初獣★1  0 / 20 pt", 30f, new Vector2(175f, -375f), new Vector2(700f, 68f), Color.white,
            TextAlignmentOptions.Left);
        Button(admin, "BtnRankNext", "▶", new Vector2(895f, -375f), new Vector2(120f, 68f), 34f);

        Text(admin, "RankPointLabel", "段位ポイント", 28f, new Vector2(45f, -465f), new Vector2(270f, 64f), Color.white,
            TextAlignmentOptions.Left);
        Input(admin, "RankPointInput", "0", new Vector2(330f, -465f), new Vector2(675f, 64f));
        Text(admin, "TenjuOrbLabel", "天珠×10（12.5=125）", 25f, new Vector2(45f, -550f), new Vector2(300f, 64f), Color.white,
            TextAlignmentOptions.Left);
        Input(admin, "TenjuOrbX10Input", "100", new Vector2(365f, -550f), new Vector2(640f, 64f));

        Button(admin, "BtnLoad", "現在値を再読込", new Vector2(110f, -660f), new Vector2(380f, 90f), 31f);
        Button(admin, "BtnSend", "本人へ補正依頼", new Vector2(560f, -660f), new Vector2(380f, 90f), 31f,
            new Color(0.1f, 0.48f, 0.35f, 1f));
        Text(admin, "AdminStatus", "同じインスタンスにいるプレイヤーだけ補正できます。", 26f,
            new Vector2(45f, -770f), new Vector2(960f, 210f), Color.white, TextAlignmentOptions.TopLeft);
    }

    private static void BuildConfirmUi(Transform root)
    {
        Transform confirm = FullPanel(root, "ConfirmationPanel", new Color(0.05f, 0.035f, 0.03f, 0.99f), true);
        Text(confirm, "Title", "段位補正の確認", 48f, new Vector2(0f, -65f), new Vector2(950f, 80f));
        Text(confirm, "ConfirmationText", "運営から段位補正の依頼です。", 32f,
            new Vector2(70f, -185f), new Vector2(910f, 420f), Color.white, TextAlignmentOptions.TopLeft);
        Text(confirm, "ConfirmationStatus", "内容を確認してください。", 28f,
            new Vector2(70f, -620f), new Vector2(910f, 110f), new Color(1f, 0.73f, 0.25f, 1f), TextAlignmentOptions.TopLeft);
        Button(confirm, "BtnAccept", "適用する", new Vector2(105f, -790f), new Vector2(380f, 100f), 34f,
            new Color(0.1f, 0.48f, 0.35f, 1f));
        Button(confirm, "BtnReject", "拒否する", new Vector2(565f, -790f), new Vector2(380f, 100f), 34f,
            new Color(0.5f, 0.16f, 0.16f, 1f));
    }

    private static Transform FullPanel(Transform root, string name, Color color, bool active)
    {
        Transform t = Child(root, name);
        RectTransform rt = (RectTransform)t;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        // パネルがCanvas平面から浮く・回るとレーザーが当たらなくなる。
        // UIシェーダーはCull Offなので描画だけは残り、「見えているのに押せない」になる。
        // anchor/offsetではzと回転を戻せないため、ここで明示的に平面へ戻す。
        rt.localRotation = Quaternion.identity;
        rt.localScale = Vector3.one;
        rt.localPosition = new Vector3(rt.localPosition.x, rt.localPosition.y, 0f);
        Image image = EnsureImage(t, color);
        image.color = color;
        t.gameObject.SetActive(active);
        return t;
    }

    private static TextMeshProUGUI Text(Transform parent, string name, string value, float size,
        Vector2 topLeft, Vector2 rectSize)
    {
        return Text(parent, name, value, size, topLeft, rectSize, Color.white, TextAlignmentOptions.Center);
    }

    private static TextMeshProUGUI Text(Transform parent, string name, string value, float size,
        Vector2 topLeft, Vector2 rectSize, Color color)
    {
        return Text(parent, name, value, size, topLeft, rectSize, color, TextAlignmentOptions.Center);
    }

    private static TextMeshProUGUI Text(Transform parent, string name, string value, float size,
        Vector2 topLeft, Vector2 rectSize, Color color, TextAlignmentOptions alignment)
    {
        Transform t = Child(parent, name);
        SetTopRect(t, topLeft, rectSize);
        TextMeshProUGUI tmp = t.GetComponent<TextMeshProUGUI>();
        if (tmp == null) { tmp = Undo.AddComponent<TextMeshProUGUI>(t.gameObject); }
        tmp.font = font_;
        tmp.text = value;
        tmp.fontSize = size;
        tmp.color = color;
        tmp.alignment = alignment;
        tmp.enableWordWrapping = true;
        tmp.raycastTarget = false;
        return tmp;
    }

    private static Button Button(Transform parent, string name, string label, Vector2 topLeft, Vector2 size, float fontSize)
    {
        return Button(parent, name, label, topLeft, size, fontSize, new Color(0.16f, 0.27f, 0.43f, 1f));
    }

    private static Button Button(Transform parent, string name, string label, Vector2 topLeft, Vector2 size,
        float fontSize, Color color)
    {
        Transform t = Child(parent, name);
        SetTopRect(t, topLeft, size);
        Image image = EnsureImage(t, color);
        image.color = color;
        Button button = t.GetComponent<Button>();
        if (button == null) { button = Undo.AddComponent<Button>(t.gameObject); }
        button.targetGraphic = image;

        Transform labelTransform = Child(t, "Label");
        Stretch(labelTransform, 8f);
        TextMeshProUGUI tmp = labelTransform.GetComponent<TextMeshProUGUI>();
        if (tmp == null) { tmp = Undo.AddComponent<TextMeshProUGUI>(labelTransform.gameObject); }
        tmp.font = font_;
        tmp.text = label;
        tmp.fontSize = fontSize;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = Color.white;
        tmp.raycastTarget = false;
        return button;
    }

    private static TMP_InputField Input(Transform parent, string name, string initialValue, Vector2 topLeft, Vector2 size)
    {
        Transform root = Child(parent, name);
        SetTopRect(root, topLeft, size);
        Image image = EnsureImage(root, new Color(0.08f, 0.1f, 0.14f, 1f));
        image.color = new Color(0.08f, 0.1f, 0.14f, 1f);

        TMP_InputField input = root.GetComponent<TMP_InputField>();
        if (input == null) { input = Undo.AddComponent<TMP_InputField>(root.gameObject); }
        input.targetGraphic = image;
        input.contentType = TMP_InputField.ContentType.IntegerNumber;
        input.lineType = TMP_InputField.LineType.SingleLine;

        Transform viewport = Child(root, "Text Area");
        Stretch(viewport, 12f);
        if (viewport.GetComponent<RectMask2D>() == null) { Undo.AddComponent<RectMask2D>(viewport.gameObject); }

        Transform placeholderTransform = Child(viewport, "Placeholder");
        Stretch(placeholderTransform, 0f);
        TextMeshProUGUI placeholder = placeholderTransform.GetComponent<TextMeshProUGUI>();
        if (placeholder == null) { placeholder = Undo.AddComponent<TextMeshProUGUI>(placeholderTransform.gameObject); }
        placeholder.font = font_;
        placeholder.text = "整数を入力";
        placeholder.fontSize = 28f;
        placeholder.fontStyle = FontStyles.Italic;
        placeholder.color = new Color(1f, 1f, 1f, 0.35f);
        placeholder.alignment = TextAlignmentOptions.MidlineLeft;

        Transform textTransform = Child(viewport, "Text");
        Stretch(textTransform, 0f);
        TextMeshProUGUI text = textTransform.GetComponent<TextMeshProUGUI>();
        if (text == null) { text = Undo.AddComponent<TextMeshProUGUI>(textTransform.gameObject); }
        text.font = font_;
        text.fontSize = 30f;
        text.color = Color.white;
        text.alignment = TextAlignmentOptions.MidlineLeft;

        input.textViewport = (RectTransform)viewport;
        input.textComponent = text;
        input.placeholder = placeholder;
        if (!input.isFocused) { input.text = initialValue; }
        return input;
    }

    private static Transform Child(Transform parent, string name)
    {
        Transform existing = parent.Find(name);
        if (existing != null) { return existing; }
        GameObject go = new GameObject(name, typeof(RectTransform));
        Undo.RegisterCreatedObjectUndo(go, "Build Rank Admin Monitor");
        go.transform.SetParent(parent, false);
        return go.transform;
    }

    private static void SetTopRect(Transform t, Vector2 topLeft, Vector2 size)
    {
        RectTransform rt = (RectTransform)t;
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = topLeft;
        rt.sizeDelta = size;
    }

    private static void Stretch(Transform t, float inset)
    {
        RectTransform rt = (RectTransform)t;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(inset, inset);
        rt.offsetMax = new Vector2(-inset, -inset);
    }

    private static Image EnsureImage(Transform t, Color color)
    {
        Image image = t.GetComponent<Image>();
        if (image == null) { image = Undo.AddComponent<Image>(t.gameObject); }
        image.color = color;
        return image;
    }

    private static GameObject FindSceneObject(string name)
    {
        Transform[] transforms = UnityEngine.Object.FindObjectsOfType<Transform>(true);
        for (int i = 0; i < transforms.Length; ++i)
        {
            Transform candidate = transforms[i];
            if (candidate != null && candidate.name == name && candidate.gameObject.scene.IsValid())
            {
                return candidate.gameObject;
            }
        }
        return null;
    }

    private static Transform Find(Transform root, string path)
    {
        Transform t = root.Find(path);
        if (t == null) { Debug.LogError("[BuildRankAdminMonitor] UIが見つかりません: " + path); }
        return t;
    }

    private static TextMeshProUGUI FindTmp(Transform root, string path)
    {
        Transform t = Find(root, path);
        return t != null ? t.GetComponent<TextMeshProUGUI>() : null;
    }

    private static TMP_InputField FindInput(Transform root, string path)
    {
        Transform t = Find(root, path);
        return t != null ? t.GetComponent<TMP_InputField>() : null;
    }

    private static bool EnsureProgramAsset(string scriptPath, string assetPath)
    {
        MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
        if (script == null)
        {
            Debug.LogError("[BuildRankAdminMonitor] スクリプトが見つかりません: " + scriptPath);
            return false;
        }
        Type type = script.GetClass();
        if (type != null && UdonSharpProgramAsset.GetProgramAssetForClass(type) != null) { return false; }
        if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(assetPath) != null) { return false; }

        UdonSharpProgramAsset asset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
        asset.sourceCsScript = script;
        AssetDatabase.CreateAsset(asset, assetPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[BuildRankAdminMonitor] U# Program Assetを生成: " + assetPath);
        return true;
    }

    private static UdonSharpBehaviour EnsureUSharpComponent(GameObject go, Type type)
    {
        UdonSharpBehaviour proxy = (UdonSharpBehaviour)go.GetComponent(type);
        if (proxy != null && UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy) != null) { return proxy; }
        if (proxy != null)
        {
            UnityEngine.Object.DestroyImmediate(proxy);
            UdonBehaviour[] oldUdon = go.GetComponents<UdonBehaviour>();
            for (int i = 0; i < oldUdon.Length; ++i)
            {
                if (oldUdon[i].programSource == null) { UnityEngine.Object.DestroyImmediate(oldUdon[i]); }
            }
        }

        UdonSharpBehaviour added = (UdonSharpBehaviour)UdonSharpUndo.AddComponent(go, type);
        if (added == null || UdonSharpEditorUtility.GetBackingUdonBehaviour(added) == null)
        {
            Debug.LogError("[BuildRankAdminMonitor] RankAdminMonitorを追加できません。U#コンパイル後に再実行してください。");
            if (added != null) { UnityEngine.Object.DestroyImmediate(added); }
            return null;
        }
        return added;
    }

    private static int WireButton(Transform root, string path, UdonBehaviour udon, string eventName)
    {
        Transform t = Find(root, path);
        if (t == null) { return 0; }
        Button button = t.GetComponent<Button>();
        if (button == null)
        {
            Debug.LogError("[BuildRankAdminMonitor] Buttonがありません: " + path);
            return 0;
        }
        while (button.onClick.GetPersistentEventCount() > 0)
        {
            UnityEventTools.RemovePersistentListener(button.onClick, 0);
        }
        UnityEventTools.AddStringPersistentListener(button.onClick, udon.SendCustomEvent, eventName);
        EditorUtility.SetDirty(button);
        return 1;
    }

    private static void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform) { SetLayerRecursively(child.gameObject, layer); }
    }
}
#endif
