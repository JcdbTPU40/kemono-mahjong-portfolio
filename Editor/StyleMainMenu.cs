#if UNITY_EDITOR
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Existing MainMenu wiring is retained; this applies presentation only.</summary>
public static class StyleMainMenu
{
    // MainMenu 全体で使う配色。ProfileAnalyticsBuilder など他のビルダーもここを正として参照する
    // （値を複製すると、片方だけ塗り替えたときに画面ごとテーマが割れる）。
    public static readonly Color Ink = new Color32(17, 17, 17, 255);
    public static readonly Color Paper = new Color32(247, 244, 230, 255);
    public static readonly Color White = new Color32(255, 254, 249, 255);
    public static readonly Color Yellow = new Color32(255, 215, 0, 255);
    public static readonly Color Teal = new Color32(0, 194, 203, 255);
    public static readonly Color Pink = new Color32(255, 83, 142, 255);
    public static readonly Color Purple = new Color32(161, 139, 245, 255);
    /// <summary>罫線・区切り線。Line* に塗っているのと同じ色。</summary>
    public static readonly Color Rule = new Color32(171, 167, 151, 255);
    /// <summary>補足テキスト。墨より一段弱く、象牙地でも読める濃さ。</summary>
    public static readonly Color Muted = new Color32(90, 86, 72, 255);

    // ホームのボタン下段に出す待機状況の行の高さ。ここを取るぶんだけ見出しの領域を詰める。
    const float QueueRowHeight = 44f;
    // 枠の発光。ボタンの外側へこれだけ出して、この太さの板を4枚置く。
    // ボタンには Frame() が右下 9px の墨の影を落としている。発光板がその上に重なると、
    // 半透明の金が黒地に乗って右下だけ暗く見える（左上とで太さが違って見える）。
    // 影の外側だけを使うように、外へ出す量は影の 9px より大きく取る。
    const float GlowOutset = 17f;
    const float GlowThickness = 6f;
    const string GlowShaderPath = "Assets/Mahjong/Shaders/UiPulseGlow.shader";
    const string GlowMaterialPath = "Assets/Mahjong/Materials/UiPulseGlow.mat";

    [MenuItem("Tools/Kemono Mahjong/Style Main Menu")]
    public static void Run()
    {
        var root = GameObject.Find("MainMenu");
        if (root == null || EditorApplication.isPlaying) return;
        Apply(root);
    }

    public static void Apply(GameObject root)
    {
        var canvas = root.GetComponent<Canvas>();
        if (canvas == null || canvas.renderMode != RenderMode.WorldSpace)
        {
            // Build の途中で例外を投げると以降（MarkSceneDirty など）が丸ごと飛ぶため、報告して抜ける。
            Debug.LogError("[StyleMainMenu] MainMenu は World Space Canvas ではありません。装飾の適用を中止しました。");
            return;
        }
        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Style Main Menu");
        Undo.RegisterFullObjectHierarchyUndo(root, "Style Main Menu");

        foreach (var text in root.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            if (InAnalytics(text.transform)) continue;
            text.color = text.name == "LockLabel" || text.name == "CountLabel" || text.name == "Hint" ? White : Ink;
            text.raycastTarget = false;
            EditorUtility.SetDirty(text);
        }
        foreach (var img in root.GetComponentsInChildren<Image>(true))
        {
            string n = img.name;
            if (n.StartsWith("Neo")) continue;
            if (InAnalytics(img.transform)) continue;
            if (img.GetComponent<Button>() != null)
            {
                img.color = White;
                Frame(img, 4f, 9f);
                ButtonTint(img.GetComponent<Button>());
            }
            else if (n == "Image" && img.transform.parent == root.transform)
            {
                img.sprite = null;
                img.color = Paper;
                Frame(img, 6f, 14f);
            }
            else if (n == "HeaderBar" || n == "CharaDetail" || n == "FxDetail"
                || n == "StatsBlockSel" || n == "RecentGraph" || n == "WaitMemberList")
            {
                img.color = White;
                Frame(img, 3f, n == "HeaderBar" ? 0f : 8f);
            }
            else if (n.StartsWith("Line")) img.color = new Color32(171, 167, 151, 255);
            else if (n.StartsWith("Dot")) img.color = Teal;
            else if (n == "SelectedBadge") { img.color = Yellow; Frame(img, 3f, 0f); }
            EditorUtility.SetDirty(img);
        }

        // Waiting messages can span multiple lines after localization.
        // The roster sits between the message and the cancel button.
        var waiting = root.transform.Find("ScreenWaiting");
        if (waiting != null)
        {
            Rect(waiting.Find("WaitTitle"), 0f, 400f, 1500f, 120f);
            Rect(waiting.Find("WaitInfo"), 0f, 268f, 1500f, 130f);
            Rect(waiting.Find("WaitMemberList"), 0f, -30f, 900f, 400f);
            foreach (var text in waiting.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                if (text.name != "WaitTitle" && text.name != "WaitInfo") continue;
                text.enableAutoSizing = true;
                text.fontSizeMax = text.name == "WaitTitle" ? 56f : 40f;
                text.fontSizeMin = 28f;
                text.alignment = TextAlignmentOptions.Center;
            }
        }
        var home = root.transform.Find("ScreenHome");
        if (home != null)
        {
            var header = home.Find("HeaderBar");
            Rect(header, 0f, 465f, 1832f, 110f);
            Label(header, "NeoBrand", "ようこそ！麻雀集会所へ", 400f, 0f, 860f, 70f, 46f);
            var oldTitle = home.Find("NeoHeroTitle");
            if (oldTitle != null) Undo.DestroyObjectImmediate(oldTitle.gameObject);
            Label(home, "NeoRankedTitle", "RANKED MATCH", 510f, 330f, 760f, 64f, 44f);
            HomeButton(home, "BtnRanked4Han", 310f, 195f, 350f, 160f, Yellow);
            HomeButton(home, "BtnRanked4Ton", 710f, 195f, 350f, 160f, White);
            HomeButton(home, "BtnRanked3Han", 310f, -15f, 350f, 160f, Teal);
            HomeButton(home, "BtnRanked3Ton", 710f, -15f, 350f, 160f, White);
            HomeButton(home, "BtnFree", 510f, -225f, 750f, 150f, White);
            string[] names = { "BtnProfile", "BtnChara", "BtnCosmetic", "BtnWorld" };
            Color[] accents = { Teal, Yellow, Pink, Purple };
            for (int i = 0; i < names.Length; i++)
            {
                var t = home.Find(names[i]);
                Rect(t, -660f + i * 440f, -465f, 400f, 90f);
                Accent(t, accents[i]);
            }
        }
        var profile = root.transform.Find("ScreenProfile");
        if (profile != null)
        {
            // 戦績タブはホーム下段の4ボタンと同じ「白地＋差し色の帯」に揃える。
            // 選択中の金色は MainMenuController が Image.color を上書きして表現するので、
            // 帯は子オブジェクト（NeoAccent）へ置き、塗り替えに巻き込まれないようにする。
            Accent(profile.Find("BtnTab4"), Teal);
            Accent(profile.Find("BtnTab3"), Teal);
            Accent(profile.Find("BtnTabF"), Teal);
        }
        EditorSceneManager.MarkSceneDirty(root.scene);
        Undo.CollapseUndoOperations(group);
        Debug.Log("[StyleMainMenu] Applied ivory / ink / accent theme; existing events and portraits preserved.");
    }

    // プロフィールの分析セクション（AnalyticsScroll 配下）は ProfileAnalyticsBuilder が
    // 同じ象牙／墨テーマの上で、要素ごとに差し色（順位・和了種別・レーダー）を割り当てて組み立てる。
    // ここの一括塗りを通すと全TMPが墨色になり Dot*/Line* も潰れて色分けが消えるので、丸ごと除外する。
    static bool InAnalytics(Transform t)
    {
        for (var p = t; p != null; p = p.parent)
        {
            if (p.name == "AnalyticsScroll") return true;
        }
        return false;
    }

    static void HomeButton(Transform home, string name, float x, float y, float w, float h, Color color)
    {
        var t = home.Find(name);
        if (t == null) return;
        Rect(t, x, y, w, h);
        t.GetComponent<Image>().color = color;
        var label = t.Find("Label");
        if (label != null)
        {
            var tmp = label.GetComponent<TextMeshProUGUI>();
            tmp.fontSize = name == "BtnFree" ? 48f : 38f;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMax = tmp.fontSize;
            tmp.fontSizeMin = 26f;
            // 見出しは2行目のぶんだけ上へ詰める（Label は全面ストレッチのまま下だけ上げる）。
            var lrt = (RectTransform)label;
            lrt.offsetMin = new Vector2(0f, QueueRowHeight);
            lrt.offsetMax = Vector2.zero;
        }
        // 塗りが白のボタンだけモード色の帯を出す。金/翠のボタンは帯が同色で消えるため墨色にする。
        Color accent = name.Contains("3") ? Teal : Yellow;
        Accent(t, color == White ? accent : Ink);

        QueueInfo(t);
        // 発光は列ごとに色を変えない。ボタンの塗り（金/翠/白）に合わせて差し色にすると、
        // 金のボタンでは墨色になって既定の枠線と見分けが付かなくなる（＝光って見えない）。
        // 「この列は今動いている」という1種類の合図なので、常に金一色で出す。
        Glow(t, Yellow);
    }

    // ボタン下段の「N人待機中... / 卓へ案内中...」。中身は MainMenuController が毎秒書き換える。
    static void QueueInfo(Transform button)
    {
        var t = Child(button, "QueueInfo");
        var rt = (RectTransform)t;
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(0.5f, 0f);
        rt.anchoredPosition = new Vector2(0f, 6f);
        rt.sizeDelta = new Vector2(0f, QueueRowHeight - 6f);
        var text = t.GetComponent<TextMeshProUGUI>();
        if (text == null) text = Undo.AddComponent<TextMeshProUGUI>(t.gameObject);
        text.font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/Mahjong/Fonts/NotoSansJP-Bold SDF.asset");
        // 初期値は空にしない。未起動の Editor で「行があること」が見えるようにしておく。
        if (string.IsNullOrEmpty(text.text)) text.text = "0人待機中...";
        text.fontSize = 26f;
        text.enableAutoSizing = true;
        text.fontSizeMax = 26f;
        text.fontSizeMin = 18f;
        text.color = Muted;
        text.alignment = TextAlignmentOptions.Center;
        text.raycastTarget = false;
        EditorUtility.SetDirty(text);
    }

    // 待機者がいる列のボタンを光らせる枠。上下左右の細板4枚＝ボタンの縦横比に依らず太さが均一。
    // 点滅は KemonoMahjong/UiPulseGlow が _Time から自走するので、光っている間の CPU はゼロ。
    // 既定は消灯（MainMenuController が SetActive で点ける）。
    static void Glow(Transform button, Color color)
    {
        var glow = Child(button, "Glow");
        var grt = (RectTransform)glow;
        grt.anchorMin = Vector2.zero;
        grt.anchorMax = Vector2.one;
        grt.pivot = new Vector2(0.5f, 0.5f);
        grt.offsetMin = new Vector2(-GlowOutset, -GlowOutset);
        grt.offsetMax = new Vector2(GlowOutset, GlowOutset);

        var mat = GetGlowMaterial();
        GlowBar(glow, "GlowT", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0f, 0f), new Vector2(0f, GlowThickness), color, mat);
        GlowBar(glow, "GlowB", new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f),
            new Vector2(0f, 0f), new Vector2(0f, GlowThickness), color, mat);
        GlowBar(glow, "GlowL", new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f),
            new Vector2(0f, 0f), new Vector2(GlowThickness, 0f), color, mat);
        GlowBar(glow, "GlowR", new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f),
            new Vector2(0f, 0f), new Vector2(GlowThickness, 0f), color, mat);

        // 既に点いている状態で Build し直しても、実行時に MainMenuController が毎秒直す。
        glow.gameObject.SetActive(false);
    }

    static void GlowBar(Transform parent, string name, Vector2 aMin, Vector2 aMax, Vector2 pivot,
        Vector2 pos, Vector2 size, Color color, Material mat)
    {
        var t = Child(parent, name);
        var rt = (RectTransform)t;
        rt.anchorMin = aMin;
        rt.anchorMax = aMax;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        var img = t.GetComponent<Image>();
        if (img == null) img = Undo.AddComponent<Image>(t.gameObject);
        img.sprite = null;
        img.color = color;          // シェーダーが _Color * 頂点色 なので、ここが列ごとの差し色になる
        img.material = mat;
        img.raycastTarget = false;  // ボタンの外側にあるので、押下を横取りさせない
        EditorUtility.SetDirty(img);
    }

    static Material GetGlowMaterial()
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(GlowMaterialPath);
        if (mat != null) return mat;
        var shader = AssetDatabase.LoadAssetAtPath<Shader>(GlowShaderPath);
        if (shader == null)
        {
            Debug.LogError("[StyleMainMenu] 発光シェーダーが見つかりません: " + GlowShaderPath);
            return null;
        }
        mat = new Material(shader);
        mat.SetColor("_Color", Color.white); // 色は Image.color 側で決める
        AssetDatabase.CreateAsset(mat, GlowMaterialPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[StyleMainMenu] 発光マテリアルを生成: " + GlowMaterialPath);
        return mat;
    }

    /// <summary>ボタンの押下感（ColorTint）。白地・墨枠のボタンはすべてこの遷移色で統一する。</summary>
    public static void ButtonTint(Button button)
    {
        if (button == null) return;
        var colors = button.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(0.80f, 0.94f, 1f, 1f);
        colors.selectedColor = colors.highlightedColor;
        colors.pressedColor = new Color(0.65f, 0.78f, 0.83f, 1f);
        colors.disabledColor = new Color(0.68f, 0.68f, 0.68f, 0.8f);
        colors.colorMultiplier = 1f;
        colors.fadeDuration = 0.08f;
        button.colors = colors;
        button.transition = Selectable.Transition.ColorTint;
        EditorUtility.SetDirty(button);
    }

    public static void Accent(Transform parent, Color color)
    {
        if (parent == null) return;
        var t = Child(parent, "NeoAccent");
        var rt = (RectTransform)t;
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(18f, -12f);
        rt.sizeDelta = new Vector2(56f, 8f);
        var image = t.GetComponent<Image>();
        if (image == null) image = Undo.AddComponent<Image>(t.gameObject);
        image.color = color;
        image.raycastTarget = false;
    }

    public static void Frame(Image image, float stroke, float depth)
    {
        // Mesh effects share the existing Graphic and follow its transition alpha.
        var outline = image.GetComponent<Outline>();
        if (outline == null) outline = Undo.AddComponent<Outline>(image.gameObject);
        outline.effectColor = Ink;
        outline.effectDistance = new Vector2(stroke, -stroke);
        outline.useGraphicAlpha = true;
        if (depth <= 0f) return;
        Shadow shadow = null;
        foreach (var candidate in image.GetComponents<Shadow>())
            if (!(candidate is Outline)) { shadow = candidate; break; }
        if (shadow == null) shadow = Undo.AddComponent<Shadow>(image.gameObject);
        shadow.effectColor = Ink;
        shadow.effectDistance = new Vector2(depth, -depth);
        shadow.useGraphicAlpha = true;
    }

    static Transform Child(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null) return t;
        var go = new GameObject(name, typeof(RectTransform));
        Undo.RegisterCreatedObjectUndo(go, "Style Main Menu");
        go.layer = parent.gameObject.layer;
        go.transform.SetParent(parent, false);
        return go.transform;
    }

    static void Rect(Transform t, float x, float y, float w, float h)
    {
        if (t == null) return;
        var rt = (RectTransform)t;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(w, h);
    }

    static void Label(Transform parent, string name, string value, float x, float y, float w, float h, float size)
    {
        var t = Child(parent, name);
        Rect(t, x, y, w, h);
        var text = t.GetComponent<TextMeshProUGUI>();
        if (text == null) text = Undo.AddComponent<TextMeshProUGUI>(t.gameObject);
        text.font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/Mahjong/Fonts/NotoSansJP-Bold SDF.asset");
        text.text = value;
        text.fontSize = size;
        text.color = Ink;
        text.alignment = TextAlignmentOptions.Left;
        text.raycastTarget = false;
    }
}
#endif
