#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using TMPro;
using UdonSharpEditor;
using SuzuFactory.AozoraMahjong;

public static class ProfileAnalyticsBuilder
{
    // 配色は MainMenu 全体（StyleMainMenu）の象牙／墨テーマを正として参照する。
    // ここで値を複製すると、あちらを塗り替えたときにプロフィールだけ浮く。
    static readonly Color Panel = StyleMainMenu.White;
    static readonly Color Ink = StyleMainMenu.Ink;
    static readonly Color Muted = StyleMainMenu.Muted;
    static readonly Color Accent = StyleMainMenu.Teal;   // ホームの「プロフィール」ボタンと同じ差し色
    /// <summary>レーダーの目盛りや円グラフの地。象牙地に沈む程度の薄さ。</summary>
    static readonly Color Soft = new Color32(226, 221, 200, 255);
    // データの色分けは象牙地でも読める濃さに落とす（薄い蛍光色は白地で飛ぶ）。
    // 凡例は文字色そのものなので、ここを下げないと和了種別が読めなくなる。
    static readonly Color[] WinColors = { new Color32(14,140,151,255), new Color32(107,79,196,255), new Color32(196,42,99,255) };
    static readonly Color[] RankColors = { new Color32(224,161,0,255), new Color32(222,122,34,255), new Color32(199,69,43,255), new Color32(158,31,62,255) };
    const string AssetRoot = "Assets/Mahjong/Textures/UI";
    static TMP_FontAsset font;

    [MenuItem("Tools/Kemono Mahjong/Build Profile Analytics")]
    public static void Build()
    {
        var menu = GameObject.Find("MainMenu");
        if (menu == null) { throw new System.InvalidOperationException("MainMenu not found"); }
        BuildFor(menu);
    }

    public static void BuildFor(GameObject menu)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) { throw new System.InvalidOperationException("Exit Play Mode first"); }
        var screen = menu.transform.Find("ScreenProfile");
        if (screen == null) { throw new System.InvalidOperationException("ScreenProfile not found"); }
        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Build Profile Analytics");
        var tmp = screen.GetComponentInChildren<TextMeshProUGUI>(true);
        font = tmp != null ? tmp.font : TMP_Settings.defaultFontAsset;
        var previous = screen.Find("AnalyticsScroll");
        if (previous != null) { Undo.DestroyObjectImmediate(previous.gameObject); }

        // Preserve the existing navigation, statistics references and all other menu screens.
        Move(screen.Find("BtnBack"), 40, -24, 175, 76);
        Move(screen.Find("ProfileName"), 250, -28, 620, 55);
        Move(screen.Find("ProfileRank"), 250, -88, 620, 45);
        // 戻るボタンは墨の影が 9px 下へ伸びる（StyleMainMenu.Frame）。-104 だと段位バッジがその影に食い込む。
        Move(screen.Find("ProfileRankIcon"), 70, -114, 90, 90);
        Move(screen.Find("BtnTab4"), 270, -175, 420, 76);
        Move(screen.Find("BtnTab3"), 720, -175, 420, 76);
        Move(screen.Find("BtnTabF"), 1170, -175, 420, 76);
        var oldGraph = screen.Find("RecentGraph");
        if (oldGraph != null) { Undo.RecordObject(oldGraph.gameObject, "Hide old graph"); oldGraph.gameObject.SetActive(false); }
        var stats = screen.Find("StatsBlockSel");
        if (stats != null)
        {
            Move(stats, 935, -24, 920, 118);
            var title = stats.Find("Title");
            if (title != null) { Undo.RecordObject(title.gameObject, "Hide redundant title"); title.gameObject.SetActive(false); }
            string[] rows = {"RowPlay","RowWin","RowAgari","RowHoju"};
            for (int i = 0; i < 4; i++)
            {
                var row = stats.Find(rows[i]);
                if (row == null) { continue; }
                Move(row, 15 + i * 225, -8, 220, 104);
                foreach (var label in row.GetComponentsInChildren<TextMeshProUGUI>(true))
                {
                    Undo.RecordObject(label, "Compact profile totals");
                    bool value = label.name == "Value";
                    Move(label.transform, 4, value ? -46 : -2, 212, 44);
                    label.fontSize = value ? 25 : 26;
                    label.alignment = TextAlignmentOptions.Center;
                }
            }
        }

        var scroll = Rect(screen, "AnalyticsScroll", 40, -270, 1840, 775);
        var bg = scroll.gameObject.AddComponent<Image>(); bg.color = Panel;
        StyleMainMenu.Frame(bg, 3f, 8f);   // StatsBlockSel など他のパネルと同じ墨の枠＋影
        // 画面フェードはこの CanvasGroup 1個で畳む（MainMenuController.CacheScreenGroups）。
        // 配下は Graphic が300個超あり、1枚ずつ色を塗ると Quest でヒッチする。
        scroll.gameObject.AddComponent<CanvasGroup>();
        var view = Rect(scroll, "Viewport", 18, -10, 1780, 750);
        view.gameObject.AddComponent<RectMask2D>();
        var content = Rect(view, "Content", 0, 0, 1780, 1450);
        var sr = scroll.gameObject.AddComponent<ScrollRect>();
        sr.viewport = view; sr.content = content; sr.horizontal = false; sr.vertical = true;
        sr.movementType = ScrollRect.MovementType.Clamped; sr.scrollSensitivity = 65;
        var track = Rect(scroll, "Scrollbar", 1806, -14, 18, 742);
        Img(track, Soft).raycastTarget = true;
        var handle = Rect(track, "Handle", 0, 0, 18, 0);
        // ハンドルは墨だと白地に黒い棒が立って重いので、操作できるものを示す差し色にする。
        var hi = Img(handle, Accent); hi.raycastTarget = true;
        var sb = track.gameObject.AddComponent<Scrollbar>();
        sb.handleRect = handle; sb.targetGraphic = hi; sb.direction = Scrollbar.Direction.BottomToTop;
        sr.verticalScrollbar = sb;

        // 見出しの幅は文字ぶんだけ（右に通常/イベントの切替を置くので広げない）。
        Title(content, "HighlightTitle", "ハイライト", 28, -16, 380, 58);
        // 通常／イベントの切替。イベントルールの段位戦の記録がある人にだけ
        // MainMenuController が出す（既定は非表示）。
        var scope = Rect(content, "HighlightScope", 0, 0, 0, 0);
        Button(scope, "BtnHighlightNormal", "通常", 430, -18, 150, 46, "ProfileHighlightNormal");
        Button(scope, "BtnHighlightEvent", "イベント", 596, -18, 150, 46, "ProfileHighlightEvent");
        scope.gameObject.SetActive(false);
        Label(content, "HighlightNote", "導入後の最高打点 / 完了した対局から記録", 30, -72, 1030, 35, 24, Muted);
        Label(content, "HighlightValue", "", 1100, -20, 620, 60, 37, Ink).alignment = TextAlignmentOptions.Right;
        var tiles = Rect(content, "HighlightTiles", 30, -122, 1700, 100);
        var atlas = AssetDatabase.LoadAssetAtPath<Texture>("Assets/Mahjong/Textures/tiles.png");
        if (atlas == null) { throw new System.InvalidOperationException("Mahjong tile atlas missing"); }
        for (int i = 0; i < 18; i++)
        {
            var tile = Rect(tiles, "Tile" + i, i * 68, 0, 64, 95);
            var raw = tile.gameObject.AddComponent<RawImage>(); raw.texture = atlas; raw.raycastTarget = false;
            tile.gameObject.SetActive(false);
        }
        Label(content, "HighlightEmpty", "記録なし — 対局を完了すると最高打点の和了を保存します", 35, -140, 1660, 55, 30, Muted);

        Title(content, "StyleTitle", "スタイル", 28, -267, 680, 58);
        Button(content, "StyleHelpButton", "?", 700, -275, 55, 50, "ProfileToggleStyleHelp");
        Label(content, "StyleValues", "攻  --\n防  --\n速  --\n運  --", 35, -354, 180, 240, 34, Ink);
        var radar = Rect(content, "Radar", 470, -465, 0, 0);
        var triangle = Shape(false);
        for (int i = 0; i < 4; i++)
        {
            var grid = Rect(radar, "Grid" + i, 0, 0, 125, 125);
            grid.pivot = Vector2.zero; grid.localEulerAngles = new Vector3(0,0,90*i);
            var gi = Img(grid, Soft); gi.sprite = triangle;
            var fill = Rect(radar, "Fill" + i, 0, 0, 0, 0);
            fill.pivot = Vector2.zero; fill.localEulerAngles = new Vector3(0,0,90*i);
            Img(fill, Accent).sprite = triangle;
        }
        Label(content, "Attack", "攻", 452, -297, 60, 50, 29, WinColors[2]);
        Label(content, "Defense", "防", 452, -595, 60, 50, 29, new Color32(20,124,96,255));
        Label(content, "Speed", "速", 284, -440, 60, 50, 29, WinColors[1]);
        Label(content, "Luck", "運", 626, -440, 60, 50, 29, new Color32(154,106,0,255));
        Label(content, "StyleDescription", "プレイスタイルの目安\n\n攻：和了時の平均打点\n防：放銃の少なさ\n速：和了までの打牌数\n運：ドラ・一発・役満で乗った翻", 875, -285, 820, 330, 30, Muted);
        var help = Rect(content, "StyleHelp", 820, -280, 910, 365);
        StyleMainMenu.Frame(Img(help, StyleMainMenu.Paper), 3f, 8f);
        // 最後の1行は母集団の断り。順位・和了グラフを通算へ変えたので、スタイルだけ直近100戦だと画面から読み取れない。
        Label(help, "Formula", "本ワールド独自の指標（雀魂の計算式ではありません）\n攻 = 平均和了点 ÷ 12,000 × 100\n防 = 100 − 放銃率 × 500\n速 = 100 − 平均和了打牌数 × 5\n運 = (ドラ+裏+赤+抜き+一発 の翻 + 役満×8) ÷ 和了数 × 25\n各値は0〜100。和了未記録の項目は --。\nこの4値は直近100戦から算出（順位グラフ・和了グラフは通算）。\n運はこの更新以降に記録した和了だけが対象。", 20, -16, 865, 330, 27, Ink);
        help.gameObject.SetActive(false);

        Title(content, "TrendTitle", "対戦記録", 28, -692, 630, 58);
        Button(content, "Fewer", "−", 1200, -696, 62, 52, "ProfileFewerGames");
        Label(content, "TrendValue", "対戦記録なし", 1274, -698, 380, 50, 27, Ink).alignment = TextAlignmentOptions.Center;
        Button(content, "More", "+", 1660, -696, 62, 52, "ProfileMoreGames");
        var trend = Rect(content, "Trend", 900, -865, 0, 0);
        for (int i = 0; i < 4; i++)
        {
            var row = Rect(trend, "Rank" + i, 0, 75 - i * 50, 0, 0);
            var line = Rect(row, "Rule", -760, 0, 1520, 3);
            Img(line, StyleMainMenu.Rule);
            Label(row, "Label", (i+1)+"位", -865, 22, 95, 44, 28, Muted);
        }
        for (int i = 0; i < 99; i++)
        {
            var line = Rect(trend, "Line" + i, 0, 0, 0, 4);
            line.pivot = new Vector2(0, 0.5f);
            Img(line, Accent);
            line.gameObject.SetActive(false);
        }
        Sprite circle = Shape(true);
        for (int i = 0; i < 100; i++)
        {
            var dot = Rect(trend, "Dot" + i, 0, 0, 12, 12);
            dot.pivot = new Vector2(0.5f,0.5f);
            Img(dot, Ink).sprite = circle;
            dot.gameObject.SetActive(false);
        }

        Title(content, "WinTitle", "和了グラフ", 28, -1010, 650, 58);
        var pie = Rect(content, "Pie", 35, -1105, 250, 250);
        Img(pie, Soft).sprite = circle;
        // Reverse sibling order exposes three cumulative radial fills as adjacent arcs.
        for (int i = 2; i >= 0; i--)
        {
            var slice = Rect(pie, "Slice" + i, 0, 0, 250, 250);
            var si = Img(slice, WinColors[i]); si.sprite = circle;
            si.type = Image.Type.Filled; si.fillMethod = Image.FillMethod.Radial360;
            si.fillOrigin = (int)Image.Origin360.Top; si.fillClockwise = true; si.fillAmount = 0;
        }
        var hole = Rect(pie, "Hole", 56, -56, 138, 138);
        Img(hole, Panel).sprite = circle;
        string[] winNames = {"立直","副露","闇聴"};
        for (int i = 0; i < 3; i++)
        {
            Label(content, "WinName" + i, "● " + winNames[i], 335, -1130-i*68, 185, 48, 31, WinColors[i]);
            Label(content, "WinValue" + i, "--", 540, -1130-i*68, 260, 48, 31, Ink);
        }
        Title(content, "PlaceTitle", "順位グラフ", 890, -1010, 810, 58);
        for (int i = 0; i < 4; i++)
        {
            var row = Rect(content, "Place" + i, 905, -1120-i*60, 820, 50);
            Label(row, "Label", (i+1)+"位", 0, 0, 90, 48, 29, Muted);
            var bar = Rect(row, "Bar", 100, -10, 0, 30); Img(bar, RankColors[i]);
            Label(row, "Value", "--", 570, 0, 210, 48, 30, Ink);
        }
        Label(content, "Sample", "新しい対戦データは対局完了後に反映されます", 30, -1385, 1690, 45, 23, Muted);
        Wire(menu);
        sr.verticalNormalizedPosition = 1f;
        EditorSceneManager.MarkSceneDirty(menu.scene);
        Debug.Log("[ProfileAnalytics] Built and wired. Scene left unsaved.");
    }

    public static void Wire(GameObject menu)
    {
        var c = menu.GetComponent<MainMenuController>();
        var root = menu.transform.Find("ScreenProfile/AnalyticsScroll/Viewport/Content");
        if (c == null || root == null) { return; }
        Undo.RecordObject(c, "Wire profile analytics");
        c.profileTrendDots = new RectTransform[100]; c.profileTrendLines = new RectTransform[99];
        for (int i = 0; i < 100; i++) { c.profileTrendDots[i] = (RectTransform)root.Find("Trend/Dot"+i); }
        for (int i = 0; i < 99; i++) { c.profileTrendLines[i] = (RectTransform)root.Find("Trend/Line"+i); }
        c.profileRankRows = new GameObject[4]; c.profileRadar = new RectTransform[4];
        c.profilePlaceBars = new RectTransform[4]; c.profilePlaceLabels = new TextMeshProUGUI[4];
        for (int i = 0; i < 4; i++)
        {
            c.profileRankRows[i] = root.Find("Trend/Rank"+i).gameObject;
            c.profileRadar[i] = (RectTransform)root.Find("Radar/Fill"+i);
            c.profilePlaceBars[i] = (RectTransform)root.Find("Place"+i+"/Bar");
            c.profilePlaceLabels[i] = root.Find("Place"+i+"/Value").GetComponent<TextMeshProUGUI>();
        }
        c.profileWinSlices = new Image[3]; c.profileWinLabels = new TextMeshProUGUI[3];
        for (int i = 0; i < 3; i++)
        {
            c.profileWinSlices[i] = root.Find("Pie/Slice"+i).GetComponent<Image>();
            c.profileWinLabels[i] = root.Find("WinValue"+i).GetComponent<TextMeshProUGUI>();
        }
        c.profileHighlightTiles = new RawImage[18];
        for (int i = 0; i < 18; i++) { c.profileHighlightTiles[i] = root.Find("HighlightTiles/Tile"+i).GetComponent<RawImage>(); }
        c.profileStyleText = root.Find("StyleValues").GetComponent<TextMeshProUGUI>();
        c.profileSampleText = root.Find("Sample").GetComponent<TextMeshProUGUI>();
        c.profileTrendText = root.Find("TrendValue").GetComponent<TextMeshProUGUI>();
        c.profileHighlightText = root.Find("HighlightValue").GetComponent<TextMeshProUGUI>();
        c.profileHighlightEmpty = root.Find("HighlightEmpty").GetComponent<TextMeshProUGUI>();
        c.profileHighlightScope = root.Find("HighlightScope").gameObject;
        c.profileHighlightScopeNormalImage = root.Find("HighlightScope/BtnHighlightNormal").GetComponent<Image>();
        c.profileHighlightScopeEventImage = root.Find("HighlightScope/BtnHighlightEvent").GetComponent<Image>();
        c.profileStyleHelp = root.Find("StyleHelp").gameObject;
        c.profileScroll = menu.transform.Find("ScreenProfile/AnalyticsScroll").GetComponent<ScrollRect>();
        var udon = UdonSharpEditorUtility.GetBackingUdonBehaviour(c);
        string[] buttons = {"Fewer","More","StyleHelpButton",
            "HighlightScope/BtnHighlightNormal","HighlightScope/BtnHighlightEvent"};
        string[] events = {"ProfileFewerGames","ProfileMoreGames","ProfileToggleStyleHelp",
            "ProfileHighlightNormal","ProfileHighlightEvent"};
        for (int i = 0; i < buttons.Length; i++)
        {
            var b = root.Find(buttons[i]).GetComponent<Button>();
            while (b.onClick.GetPersistentEventCount() > 0) { UnityEventTools.RemovePersistentListener(b.onClick, 0); }
            UnityEventTools.AddStringPersistentListener(b.onClick, udon.SendCustomEvent, events[i]);
            EditorUtility.SetDirty(b);
        }
        UdonSharpEditorUtility.CopyProxyToUdon(c);
        EditorUtility.SetDirty(c);
    }

    static void Move(Transform t, float x, float y, float w, float h)
    {
        if (t == null) { return; }
        var r = t as RectTransform;
        Undo.RecordObject(r, "Layout profile");
        r.anchorMin = r.anchorMax = new Vector2(0,1); r.pivot = new Vector2(0,1);
        r.anchoredPosition = new Vector2(x,y); r.sizeDelta = new Vector2(w,h);
    }
    static RectTransform Rect(Transform parent, string name, float x, float y, float w, float h)
    {
        var go = new GameObject(name, typeof(RectTransform));
        Undo.RegisterCreatedObjectUndo(go, "Build profile");
        go.transform.SetParent(parent, false);
        Move(go.transform, x,y,w,h);
        return (RectTransform)go.transform;
    }
    static Image Img(RectTransform r, Color color)
    {
        var i = r.gameObject.AddComponent<Image>(); i.color = color; i.raycastTarget = false; return i;
    }
    static TextMeshProUGUI Label(Transform parent, string name, string value, float x, float y, float w, float h, int size, Color color)
    {
        var r = Rect(parent,name,x,y,w,h);
        var t = r.gameObject.AddComponent<TextMeshProUGUI>();
        t.font = font; t.text = value; t.fontSize = size; t.color = color;
        t.raycastTarget = false; t.enableWordWrapping = true; t.alignment = TextAlignmentOptions.Left;
        return t;
    }
    // 章見出し。ホームのボタンと同じ差し色の帯を頭に置いて、MainMenu 全体の意匠へ揃える。
    static void Title(Transform parent, string name, string value, float x, float y, float w, float h)
    {
        Img(Rect(parent, name + "Accent", x + 2, y + 14, 56, 8), Accent);
        Label(parent, name, value, x, y, w, h, 42, Ink);
    }
    static void Button(Transform parent, string name, string value, float x, float y, float w, float h, string eventName)
    {
        var r = Rect(parent,name,x,y,w,h);
        var im = Img(r, Panel); im.raycastTarget = true;
        StyleMainMenu.Frame(im, 4f, 9f);
        var b = r.gameObject.AddComponent<Button>(); b.targetGraphic = im;
        StyleMainMenu.ButtonTint(b);
        Label(r,"Label",value,0,0,w,h,30,Ink).alignment = TextAlignmentOptions.Center;
    }
    static Sprite Shape(bool circle)
    {
        string path = AssetRoot + (circle ? "/ProfileCircle.asset" : "/ProfileTriangle.asset");
        foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path)) { if (asset is Sprite) { return (Sprite)asset; } }
        const int size = 128;
        var tex = new Texture2D(size,size,TextureFormat.RGBA32,false);
        tex.name = circle ? "ProfileCircle" : "ProfileTriangle";
        var pixels = new Color[size*size];
        for (int y=0;y<size;y++) for(int x=0;x<size;x++)
        {
            float alpha = circle ? Mathf.Clamp01(64f - Vector2.Distance(new Vector2(x+0.5f,y+0.5f),new Vector2(64,64))) :
                Mathf.Clamp01((127.5f-x-y)*0.7071f);
            pixels[y*size+x] = new Color(1,1,1,alpha);
        }
        tex.SetPixels(pixels); tex.Apply(); tex.wrapMode = TextureWrapMode.Clamp;
        AssetDatabase.CreateAsset(tex,path);
        var sprite = Sprite.Create(tex,new Rect(0,0,size,size),new Vector2(0.5f,0.5f),100,0,SpriteMeshType.FullRect);
        sprite.name = tex.name + "Sprite";
        AssetDatabase.AddObjectToAsset(sprite,path);
        AssetDatabase.SaveAssets();
        return sprite;
    }
}
#endif
