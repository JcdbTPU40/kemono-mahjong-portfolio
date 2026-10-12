#if UNITY_EDITOR
// 卓の対局ボタン（チー/ポン/カン/ロン/ツモ/リーチ/北抜き/九種九牌/いいえ/取消/オープン立直）を
// 雀魂風の「斜めの色帯＋筆文字」へ着せ替える。
//
// ■ 何を変えて、何を変えないか
//   変えるのは見た目だけ。白い板（MeshRenderer）と3D文字の描画を切り、帯の板を手前に1枚置く。
//   BoxCollider・MahjongPushButton・位置・id は一切触らない
//   （当たり判定は TuneButtonReach が調整済み／NoButton の位置は「動かすな」の教訓がある）。
//
// ■ 帯の絵はここで焼く
//   1枚のアトラス（2048²）に全ボタン×日英を焼き、各ボタンはそのセルを UV で切り出した板。
//   全卓・全ボタンが同じマテリアルを共有する＝Quest でも描画コストがほぼ増えない。
//   筆文字は BuildCallEffect と同じ手順（一時カメラで字のアルファを撮る→距離場で太らせ＋縁＋縦グラデ）。
//
// ■ 言語切替
//   帯は JA_Skin / EN_Skin という名前にしてある。Mahjong.SwitchLanguage が名前の接頭辞
//   JA_ / EN_ で SetActive を切り替えるので、配線なしで日英が切り替わる。
//
// ■ 出現アニメ
//   帯の親（Skin）に MahjongButtonSkinFx を付ける。右から滑り込み＋フェード、同期変数ゼロ。
//
// 2回実行しても安全（冪等）。アトラスは毎回焼き直す。
// 実行: Tools > Kemono Mahjong > Build Table Button Skins
//       （初回は U# プログラムアセット生成→コンパイル待ちで止まるので、もう一度実行）
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using SuzuFactory.AozoraMahjong;

public static class BuildTableButtonSkins
{
    const string PrefabPath = "Assets/Mahjong/Prefabas/KemonoMahjong.prefab";
    const string Dir = "Assets/Mahjong/Textures/UI/TableButtons";
    const string AtlasPath = Dir + "/TableButtonAtlas.png";
    const string MeshAssetPath = Dir + "/TableButtonSkinMeshes.asset";
    const string MaterialPath = Dir + "/TableButtonSkin.mat";
    const string ShaderName = "KemonoMahjong/TableButtonSkin";
    const string BrushFontPath = "Assets/Mahjong/Fonts/YujiMai-Regular.ttf";
    const string FxScriptPath = "Assets/Mahjong/Sources/MahjongButtonSkinFx.cs";
    const string ProgramAssetDir = "Assets/Mahjong/scene_UdonProgramSources";
    const string FxProgramAssetPath = ProgramAssetDir + "/MahjongButtonSkinFx Udon C# Program Asset.asset";

    // アトラスの1セル。帯の縦横比 512:208 ≒ 2.46:1（雀魂の鳴きボタンに近い）。
    const int CellW = 512;
    const int CellH = 208;
    const int Cols = 4;
    const int AtlasSize = 2048;

    // 帯の実寸（m）。白い板は 0.10×0.05。隣のスロットとは 0.12m 間隔なので、
    // 斜めの角が透明な分だけ板より少し広くしても隣とは重ならない。
    const float QuadW = 0.112f;

    // 板の表面からどれだけ手前に浮かすか（m）。旧3D文字と同じ 0.011 より少しだけ手前。
    const float FaceOffset = 0.0115f;

    // ---- ボタンの種類（色と文字）----
    class Style
    {
        public string key;
        public string ja;
        public string en;
        public Color top;      // 帯の上側の色
        public Color bottom;   // 帯の下側の色
        public Style(string key, string ja, string en, string topHex, string bottomHex)
        {
            this.key = key;
            this.ja = ja;
            this.en = en;
            this.top = Hex(topHex);
            this.bottom = Hex(bottomHex);
        }
    }

    // 雀魂準拠の色分け：チー緑／ポン青／カン紫／ロン紅／ツモ朱／リーチ橙／北抜き藍／九種九牌 青緑／スキップ灰。
    // オープン立直も文字を焼く（重ね文字 OpenReachText は帯とほぼ同じ面にあり、帯の陰に隠れて見えなかった）。
    // 重ね文字は「切替済み（ボタンを伏せた後）」の ON 表示にだけ使う。
    static readonly Style[] Styles =
    {
        new Style("chi",    "チー",     "Chi",    "#4cc27f", "#1c6a43"),
        new Style("pon",    "ポン",     "Pon",    "#4aa6e8", "#1b5596"),
        new Style("kan",    "カン",     "Kan",    "#a77ae6", "#55309a"),
        new Style("ron",    "ロン",     "Ron",    "#f05a4a", "#9a1f17"),
        new Style("tsumo",  "ツモ",     "Tsumo",  "#f5873a", "#a8400f"),
        new Style("reach",  "リーチ",   "Riichi", "#f7b733", "#b0650a"),
        new Style("kita",   "北抜き",   "Kita",   "#5b7fe0", "#253f94"),
        new Style("nine",   "九種九牌", "Abort",  "#3fbfb5", "#15726c"),
        new Style("skip",   "スキップ", "Skip",   "#8b8f99", "#43464f"),
        new Style("cancel", "取消",     "Cancel", "#8b8f99", "#43464f"),
        new Style("open",   "オープン立直", "Open Riichi", "#f7b733", "#b0650a"),
    };

    // ---- 着せ替えるボタン ----
    class Slot
    {
        public string button;
        public string style;
        public float yOffset;  // 帯を上へずらす量（m）。今は全ボタン 0（チーの牌番号表示を廃止したため）
        public Slot(string button, string style, float yOffset)
        {
            this.button = button;
            this.style = style;
            this.yOffset = yOffset;
        }
    }

    static readonly Slot[] Slots =
    {
        new Slot("ChiPrevButton",      "chi",    0f),
        new Slot("ChiCenterButton",    "chi",    0f),
        new Slot("ChiNextButton",      "chi",    0f),
        new Slot("ChiPrevRedButton",   "chi",    0f),
        new Slot("ChiCenterRedButton", "chi",    0f),
        new Slot("ChiNextRedButton",   "chi",    0f),
        new Slot("PonButton",          "pon",    0f),
        new Slot("PonRedButton",       "pon",    0f),
        new Slot("KanButton",          "kan",    0f),
        new Slot("RonButton",          "ron",    0f),
        new Slot("TsumoButton",        "tsumo",  0f),
        new Slot("ReachButton",        "reach",  0f),
        new Slot("OpenReachButton",    "open",   0f),
        new Slot("KitaButton",         "kita",   0f),
        new Slot("NineButton",         "nine",   0f),
        new Slot("NoButton",           "skip",   0f),
        new Slot("ReachCancelButton",  "cancel", 0f),
    };

    // 帯の上に乗る実行時テキスト（チーの牌表示など）。白い板が消えて卓の緑に直に乗るので、
    // 黒文字のままだと読めない。白文字＋濃い縁取りへ変える。
    static readonly string[] OverlayTexts =
    {
        "ChiPrevText", "ChiCenterText", "ChiNextText",
        "ChiPrevRedText", "ChiCenterRedText", "ChiNextRedText",
        "PonText", "PonRedText", "OpenReachText",
    };

    static readonly Color OverlayTextColor = new Color(1f, 0.98f, 0.92f, 1f);
    static readonly Color OverlayOutlineColor = new Color(0.07f, 0.05f, 0.04f, 0.95f);

    // ============================================================
    // メニュー
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Build Table Button Skins")]
    public static void Run()
    {
        EnsureFolder(Dir);
        EnsureFolder(ProgramAssetDir);

        if (EnsureProgramAsset(FxScriptPath, FxProgramAssetPath))
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[BuildTableButtonSkins] U#プログラムアセットを生成しました。コンパイル完了後、もう一度実行してください。");
            return;
        }

        Rect[] uvRects;
        if (!BakeAtlas(out uvRects)) { return; }

        var mat = EnsureMaterial();
        if (mat == null) { return; }

        var meshes = EnsureMeshes(uvRects);

        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null)
        {
            Debug.LogError("[BuildTableButtonSkins] Prefab を読み込めません: " + PrefabPath);
            return;
        }

        int skinned = 0;
        int texts = 0;
        try
        {
            var wrapper = root.transform.Find("PlayerUIWrapper");
            var playerUI = wrapper != null ? wrapper.Find("PlayerUI") : null;
            if (playerUI == null)
            {
                Debug.LogError("[BuildTableButtonSkins] PlayerUIWrapper/PlayerUI が見つかりません。");
                return;
            }

            foreach (var slot in Slots)
            {
                var btn = playerUI.Find(slot.button);
                if (btn == null) { continue; }  // 赤ドラ鳴きボタンは未生成の卓がある
                if (ApplySkin(root.transform, btn, slot, meshes, mat)) { ++skinned; }
            }

            ConfigureChiSelectButton(playerUI);

            foreach (var name in OverlayTexts)
            {
                var t = wrapper.Find(name);
                if (t == null) { continue; }
                if (RestyleOverlayText(t)) { ++texts; }
            }

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Debug.Log("[BuildTableButtonSkins] 完了: ボタン " + skinned + " 個を着せ替え・重ね文字 " + texts
            + " 個を白文字化。Prefab を保存しました（シーンの全卓に反映）。");
        Validate();
    }

    [MenuItem("Tools/Kemono Mahjong/Validate Table Button Skins")]
    public static void Validate()
    {
        int tables = 0;
        int ok = 0;
        int bad = 0;
        var sb = new System.Text.StringBuilder();

        foreach (var m in Object.FindObjectsOfType<Mahjong>(true))
        {
            ++tables;
            var ui = m.transform.Find("PlayerUIWrapper/PlayerUI");
            if (ui == null) { ++bad; sb.Append(m.name).Append(": PlayerUI なし\n"); continue; }

            string chiWhy = CheckChiSelectButton(ui);
            if (chiWhy == null) { ++ok; }
            else { ++bad; sb.Append(m.name).Append("（チー/ポン選択）: ").Append(chiWhy).Append("\n"); }

            foreach (var slot in Slots)
            {
                var btn = ui.Find(slot.button);
                if (btn == null) { continue; }

                string why = CheckButton(btn, slot);
                if (why == null) { ++ok; }
                else { ++bad; sb.Append(m.name).Append("/").Append(slot.button).Append(": ").Append(why).Append("\n"); }
            }
        }

        if (bad == 0)
        {
            Debug.Log("[ValidateTableButtonSkins] OK: 卓 " + tables + " / ボタン " + ok + " 個すべて着せ替え済み。");
        }
        else
        {
            Debug.LogError("[ValidateTableButtonSkins] NG " + bad + " 件（OK " + ok + "）\n" + sb);
        }
    }

    // ============================================================
    // チーボタン（雀魂式の牌選択）
    //  チーボタンは ChiPrevButton の1つだけを使う。押すと Mahjong._OnChiSelect が
    //  候補が1通りなら即チー、複数なら候補パネル（MahjongChiPanel）を開く。
    //  ＝ボタン自体はネットワークへ送らない（local）し、押してもボタンを畳まない（選択中も出しておく）。
    //  ChiCenter/ChiNext と赤版は Mahjong.cs が出さなくなった（GameObject と id は互換のため残す）。
    // ============================================================
    const string ChiSelectButtonName = "ChiPrevButton";
    const string ChiSelectEventId = "_OnChiSelect";

    // ポンボタンも同じ作り。押すと Mahjong._OnPonSelect が、赤5を晒すか選べるときだけ
    // 候補パネル（チーと共用）を開き、選べなければその場で（赤温存の）ポンを送る。
    // PonRedButton は使わない（元から未生成の卓が多い）。
    const string PonSelectButtonName = "PonButton";
    const string PonSelectEventId = "_OnPonSelect";

    // カンボタンも端末側で受ける。ロン確認中（ロンと鳴きを同時に聞く）は席＋決断の目印付きの
    // 予約要求を送り、それ以外は従来どおり OnKanButton を卓 Owner へ送る（Mahjong._OnKanSelect）。
    const string KanSelectButtonName = "KanButton";
    const string KanSelectEventId = "_OnKanSelect";

    static void ConfigureChiSelectButton(Transform playerUI)
    {
        ConfigureSelectButton(playerUI, ChiSelectButtonName, ChiSelectEventId);
        ConfigureSelectButton(playerUI, PonSelectButtonName, PonSelectEventId);
        ConfigureSelectButton(playerUI, KanSelectButtonName, KanSelectEventId);
    }

    static void ConfigureSelectButton(Transform playerUI, string buttonName, string eventId)
    {
        var btn = playerUI.Find(buttonName);
        var pb = btn != null ? btn.GetComponent<MahjongPushButton>() : null;
        if (pb == null)
        {
            Debug.LogError("[BuildTableButtonSkins] " + buttonName + " の MahjongPushButton が見つかりません。");
            return;
        }
        pb.id = eventId;
        pb.local = true;
        pb.disableOnPush = false;
        UdonSharpEditorUtility.CopyProxyToUdon(pb);
    }

    static string CheckChiSelectButton(Transform playerUI)
    {
        return CheckSelectButton(playerUI, ChiSelectButtonName, ChiSelectEventId)
            ?? CheckSelectButton(playerUI, PonSelectButtonName, PonSelectEventId)
            ?? CheckSelectButton(playerUI, KanSelectButtonName, KanSelectEventId);
    }

    static string CheckSelectButton(Transform playerUI, string buttonName, string eventId)
    {
        var btn = playerUI.Find(buttonName);
        var pb = btn != null ? btn.GetComponent<MahjongPushButton>() : null;
        if (pb == null) { return buttonName + " に MahjongPushButton が無い"; }
        if (pb.id != eventId) { return buttonName + " の id が " + pb.id + "（" + eventId + " であるべき）"; }
        if (!pb.local) { return buttonName + " の local が false（ネットワークへ直接鳴きを送ってしまう）"; }
        if (pb.disableOnPush) { return buttonName + " の disableOnPush が true（選択中にボタンが消える）"; }
        return null;
    }

    static string CheckButton(Transform btn, Slot slot)
    {
        var plate = btn.GetComponent<MeshRenderer>();
        if (plate != null && plate.enabled) { return "白い板の描画が残っている"; }
        if (btn.GetComponent<Collider>() == null) { return "当たり判定（Collider）が無い"; }

        var skin = btn.Find("Skin");
        if (skin == null) { return "Skin が無い"; }

        var fx = skin.GetComponent<MahjongButtonSkinFx>();
        if (fx == null) { return "MahjongButtonSkinFx が無い"; }
        if (fx.renderers == null || fx.renderers.Length != 2 || fx.renderers[0] == null || fx.renderers[1] == null)
        {
            return "MahjongButtonSkinFx.renderers の配線切れ";
        }

        foreach (var n in new[] { "JA_Skin", "EN_Skin" })
        {
            var t = skin.Find(n);
            if (t == null) { return n + " が無い"; }
            var mf = t.GetComponent<MeshFilter>();
            var mr = t.GetComponent<MeshRenderer>();
            if (mf == null || mf.sharedMesh == null) { return n + " のメッシュが空"; }
            if (mr == null || mr.sharedMaterial == null) { return n + " のマテリアルが空"; }
            if (t.GetComponent<Collider>() != null) { return n + " に Collider が付いている（押下を奪う）"; }
        }

        foreach (Transform c in btn)
        {
            if (!(c.name.StartsWith("JA_Text") || c.name.StartsWith("EN_Text"))) { continue; }
            var r = c.GetComponent<MeshRenderer>();
            if (r != null && r.enabled) { return "旧3D文字 " + c.name + " の描画が残っている"; }
        }
        return null;
    }

    // ============================================================
    // ボタン1個の着せ替え
    // ============================================================
    static bool ApplySkin(Transform root, Transform btn, Slot slot, Mesh[,] meshes, Material mat)
    {
        int styleIndex = FindStyle(slot.style);
        if (styleIndex < 0) { return false; }

        // 視点側は板の -up（旧3D文字が -0.011 に置かれている側）。帯は -forward が表なので
        // forward を板の +up（奥）へ向け、上方向は卓の上を板の面へ投影したものにする。
        // NoButton のように 20 度傾いた板でも文字が起きて読める。
        Vector3 n = btn.up;
        Vector3 up = Vector3.ProjectOnPlane(root.up, n);
        if (up.sqrMagnitude < 1e-6f) { up = btn.forward; }
        up.Normalize();

        var skin = EnsureChild(btn, "Skin");
        skin.transform.position = btn.position - n * FaceOffset + up * slot.yOffset;
        skin.transform.rotation = Quaternion.LookRotation(n, up);
        skin.transform.localScale = Vector3.one;
        skin.SetActive(true);

        var ja = EnsureQuad(skin.transform, "JA_Skin", meshes[styleIndex, 0], mat);
        var en = EnsureQuad(skin.transform, "EN_Skin", meshes[styleIndex, 1], mat);
        // 既定は日本語（旧 EN_Text と同じく英語側は伏せておく。実行時は SwitchLanguage が切り替える）。
        ja.SetActive(true);
        en.SetActive(false);

        var fx = (MahjongButtonSkinFx)EnsureUSharpComponent(skin, typeof(MahjongButtonSkinFx));
        if (fx == null) { return false; }
        fx.renderers = new Renderer[] { ja.GetComponent<MeshRenderer>(), en.GetComponent<MeshRenderer>() };
        fx.slideSeconds = 0.15f;
        fx.slideDistance = 0.03f;
        UdonSharpEditorUtility.CopyProxyToUdon(fx);

        // 白い板と旧ラベルの描画を切る（GameObject は残す＝当たり判定と言語切替の対象はそのまま）。
        var plate = btn.GetComponent<MeshRenderer>();
        if (plate != null) { plate.enabled = false; }

        foreach (Transform c in btn)
        {
            if (c.name.StartsWith("JA_Text") || c.name.StartsWith("EN_Text"))
            {
                var r = c.GetComponent<MeshRenderer>();
                if (r != null) { r.enabled = false; }
            }
        }

        // 北抜きだけはラベルが uGUI Text（KemonoLocalizer が文言を書く）。Text の描画だけ切る。
        var kitaCanvas = btn.Find("Canvas");
        if (kitaCanvas != null)
        {
            foreach (var t in kitaCanvas.GetComponentsInChildren<Text>(true)) { t.enabled = false; }
        }

        return true;
    }

    static GameObject EnsureQuad(Transform parent, string name, Mesh mesh, Material mat)
    {
        var go = EnsureChild(parent, name);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;

        var mf = go.GetComponent<MeshFilter>();
        if (mf == null) { mf = go.AddComponent<MeshFilter>(); }
        mf.sharedMesh = mesh;

        var mr = go.GetComponent<MeshRenderer>();
        if (mr == null) { mr = go.AddComponent<MeshRenderer>(); }
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
        mr.lightProbeUsage = LightProbeUsage.Off;
        mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
        mr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        mr.allowOcclusionWhenDynamic = false;

        // 板に当たり判定があると、本体の BoxCollider より手前で Interact を奪う。
        foreach (var col in go.GetComponents<Collider>()) { Object.DestroyImmediate(col); }
        return go;
    }

    static bool RestyleOverlayText(Transform t)
    {
        bool any = false;
        foreach (var text in t.GetComponentsInChildren<Text>(true))
        {
            text.color = OverlayTextColor;
            var outline = text.GetComponent<Outline>();
            if (outline == null) { outline = text.gameObject.AddComponent<Outline>(); }
            outline.effectColor = OverlayOutlineColor;
            // フォント 180〜200 に対して約 4%。Canvas の縮尺 0.0001 で実寸 0.8mm。
            outline.effectDistance = new Vector2(8f, -8f);
            outline.useGraphicAlpha = true;
            any = true;
        }
        return any;
    }

    static int FindStyle(string key)
    {
        for (int i = 0; i < Styles.Length; ++i) { if (Styles[i].key == key) { return i; } }
        Debug.LogError("[BuildTableButtonSkins] 未定義のスタイル: " + key);
        return -1;
    }

    // ============================================================
    // アトラス
    // ============================================================
    // セルの並び：Styles の順に [日, 英] の2セルずつ。文字の無いスタイル（open）は日英で同じ絵。
    static bool BakeAtlas(out Rect[] uvRects)
    {
        int cells = Styles.Length * 2;
        uvRects = new Rect[cells];

        int rows = (cells + Cols - 1) / Cols;
        if (rows * CellH > AtlasSize)
        {
            Debug.LogError("[BuildTableButtonSkins] アトラスに入りきりません（" + cells + " セル）。");
            return false;
        }

        var atlas = new Color[AtlasSize * AtlasSize];
        // 透明部分の色は中間の灰にしておく（真っ黒だとミップで帯の縁が黒ずむ）。
        var clear = new Color(0.5f, 0.5f, 0.5f, 0f);
        for (int i = 0; i < atlas.Length; ++i) { atlas[i] = clear; }

        for (int s = 0; s < Styles.Length; ++s)
        {
            for (int lang = 0; lang < 2; ++lang)
            {
                var style = Styles[s];
                string label = lang == 0 ? style.ja : style.en;

                var cell = ComposeCell(style, label);
                if (cell == null) { return false; }

                int idx = s * 2 + lang;
                int cx = (idx % Cols) * CellW;
                int cy = AtlasSize - (idx / Cols + 1) * CellH;   // 上から詰める

                for (int y = 0; y < CellH; ++y)
                {
                    System.Array.Copy(cell, y * CellW, atlas, (cy + y) * AtlasSize + cx, CellW);
                }

                // 0.5px 内側を取る（隣のセルを拾わない）。
                uvRects[idx] = new Rect(
                    (cx + 0.5f) / AtlasSize, (cy + 0.5f) / AtlasSize,
                    (CellW - 1f) / AtlasSize, (CellH - 1f) / AtlasSize);
            }
        }

        WriteTexture(AtlasPath, AtlasSize, AtlasSize, atlas);
        return true;
    }

    // 帯の形（正規化座標・y は上向き）。雀魂の鳴きボタンの輪郭：
    // 左の辺は「/」、上の辺は右上がり、右の辺は「\」。反時計回り。
    static readonly Vector2[] BannerShape =
    {
        new Vector2(0.050f, 0.110f),
        new Vector2(0.960f, 0.110f),
        new Vector2(0.815f, 0.880f),
        new Vector2(0.145f, 0.720f),
    };

    // 帯の左上にずらして引く細い枠線（雀魂の「二重の帯」）。
    static readonly Vector2 FrameOffset = new Vector2(-0.030f, 0.075f);

    static Color[] ComposeCell(Style style, string label)
    {
        int w = CellW;
        int h = CellH;
        var px = new Color[w * h];
        var bg = new Color(style.bottom.r, style.bottom.g, style.bottom.b, 0f);
        for (int i = 0; i < px.Length; ++i) { px[i] = bg; }

        var shape = ToPixels(BannerShape, w, h, Vector2.zero);
        var frame = ToPixels(BannerShape, w, h, new Vector2(FrameOffset.x * w, FrameOffset.y * h));
        var shadow = ToPixels(BannerShape, w, h, new Vector2(4f, -6f));

        float yMin = BannerShape[0].y * h;
        float yMax = BannerShape[2].y * h;

        Color frameColor = Color.Lerp(style.top, Color.white, 0.35f);
        frameColor.a = 0.9f;
        Color flowerLight = new Color(1f, 1f, 1f, 0.55f);
        Color flowerFaint = Color.Lerp(style.top, Color.white, 0.25f);
        flowerFaint.a = 0.20f;

        for (int y = 0; y < h; ++y)
        {
            float fy = y + 0.5f;
            for (int x = 0; x < w; ++x)
            {
                float fx = x + 0.5f;
                var p = new Vector2(fx, fy);
                Color c = px[y * w + x];

                // 1) 影（帯の右下へ柔らかく）
                float sd = SignedDistance(shadow, p);
                float sa = Mathf.Clamp01((sd + 4f) / 8f) * 0.35f;
                c = Over(c, new Color(0f, 0f, 0f, sa));

                // 2) 左上にずれた細い枠線（帯の外側だけに見える）
                float fd = SignedDistance(frame, p);
                float line = Mathf.Clamp01(1.6f - Mathf.Abs(fd)) ;
                c = Over(c, new Color(frameColor.r, frameColor.g, frameColor.b, frameColor.a * line));

                // 3) 帯本体
                float d = SignedDistance(shape, p);
                float cov = Mathf.Clamp01(d + 0.5f);
                if (cov > 0f)
                {
                    float t = Mathf.Clamp01((fy - yMin) / Mathf.Max(1f, yMax - yMin));
                    Color fill = Color.Lerp(style.bottom, style.top, Mathf.SmoothStep(0f, 1f, t));

                    // 左ほど少し明るく（光が左上から当たっている感じ）
                    fill = Color.Lerp(fill, Color.white, 0.10f * (1f - fx / w));

                    // 内側の縁：上は白く光らせ、全周を少し暗く締める
                    float edge = Mathf.Clamp01(1f - d / 5f);
                    fill = Color.Lerp(fill, style.bottom * 0.6f, edge * 0.55f);
                    if (t > 0.80f) { fill = Color.Lerp(fill, Color.white, Mathf.Clamp01(1f - d / 3f) * 0.35f); }

                    // 桜紋：左下に小さくはっきり、右側に大きく淡く
                    float f1 = Flower(p, new Vector2(0.135f * w, 0.300f * h), 0.135f * h);
                    float f2 = Flower(p, new Vector2(0.830f * w, 0.330f * h), 0.300f * h);
                    float f3 = Flower(p, new Vector2(0.700f * w, 0.760f * h), 0.170f * h);
                    fill = Over(fill, new Color(flowerLight.r, flowerLight.g, flowerLight.b, flowerLight.a * f1));
                    fill = Over(fill, new Color(flowerFaint.r, flowerFaint.g, flowerFaint.b, flowerFaint.a * Mathf.Max(f2, f3)));

                    fill.a = cov;
                    c = Over(c, fill);
                }

                px[y * w + x] = c;
            }
        }

        if (!string.IsNullOrEmpty(label))
        {
            if (!DrawLabel(px, w, h, label, style)) { return null; }
        }
        return px;
    }

    // 筆文字を帯の上へ重ねる：影 → 濃い縁 → 明るい縦グラデの字。
    static bool DrawLabel(Color[] px, int w, int h, string label, Style style)
    {
        // 字の入る枠（帯の中央より少し左。雀魂も字は左寄り）。
        float boxW = w * 0.68f;   // 長いラベル（オープン立直など）が小さくなりすぎない幅。短いラベルは高さで決まる
        float boxH = h * 0.62f;
        Vector2 center = new Vector2(w * 0.485f, h * 0.47f);

        // 長いラベル（「オープン立直」「Open Riichi」）は最初の大きさだとキャンバスからはみ出して切れる。
        // 切れた幅を測っても実寸より小さく出るので、端に触れている間は縮めて描き直し、
        // 収まってから枠に合わせる（はみ出しを見落とすと帯の外まで字が出て、セルの端で切れる）。
        int font = 140;
        float[] mask = null;
        RectInt bb = new RectInt();
        for (int attempt = 0; attempt < 8; ++attempt)
        {
            mask = RenderGlyphMask(label, w, h, font);
            if (mask == null) { return false; }
            bb = MaskBounds(mask, w, h);

            bool clipped = bb.x <= 1 || bb.y <= 1 || bb.xMax >= w - 1 || bb.yMax >= h - 1;
            if (clipped)
            {
                font = Mathf.Max(24, Mathf.FloorToInt(font * 0.7f));
                continue;
            }

            float scale = Mathf.Min(boxW / Mathf.Max(1, bb.width), boxH / Mathf.Max(1, bb.height));
            int fitted = Mathf.Clamp(Mathf.FloorToInt(font * scale), 24, 220);
            if (fitted == font) { break; }
            font = fitted;
            mask = RenderGlyphMask(label, w, h, font);
            if (mask == null) { return false; }
            bb = MaskBounds(mask, w, h);
            break;
        }

        // 字の実測の中心を枠の中心へ合わせる（フォントの行の高さで上下にずれるため）。
        int dx = Mathf.RoundToInt(center.x - (bb.x + bb.width * 0.5f));
        int dy = Mathf.RoundToInt(center.y - (bb.y + bb.height * 0.5f));
        mask = Shift(mask, w, h, dx, dy);

        const float Bold = 1.5f;   // 字を太らせる量（px）
        const float Rim = 6.0f;    // 縁の太さ（px）
        var dist = DistanceFromMask(mask, w, h);

        var fill = new float[w * h];
        var rim = new float[w * h];
        for (int i = 0; i < fill.Length; ++i)
        {
            fill[i] = Mathf.Max(mask[i], Mathf.Clamp01(Bold + 1f - dist[i]));
            rim[i] = Mathf.Clamp01((Bold + Rim + 1.5f - dist[i]) / 1.5f);
        }

        int top = bb.y + dy + bb.height;
        int bottom = bb.y + dy;
        float span = Mathf.Max(1f, top - bottom);

        Color rimColor = style.bottom * 0.35f;
        rimColor.a = 1f;
        Color topColor = new Color(1f, 0.995f, 0.96f, 1f);
        Color bottomColor = Color.Lerp(Color.white, style.top, 0.45f);

        for (int y = 0; y < h; ++y)
        {
            float t = Mathf.Clamp01((y - bottom) / span);
            Color g = Color.Lerp(bottomColor, topColor, Mathf.SmoothStep(0f, 1f, t));

            for (int x = 0; x < w; ++x)
            {
                int i = y * w + x;
                Color c = px[i];

                // 影は縁の形を右下へずらしたもの
                int sx = x - 3;
                int sy = y + 4;
                if (sx >= 0 && sy < h)
                {
                    float s = rim[sy * w + sx] * 0.45f;
                    c = Over(c, new Color(0f, 0f, 0f, s));
                }

                c = Over(c, new Color(rimColor.r, rimColor.g, rimColor.b, rim[i]));
                c = Over(c, new Color(g.r, g.g, g.b, fill[i]));
                px[i] = c;
            }
        }
        return true;
    }

    // ---- 図形ヘルパ ----
    static Vector2[] ToPixels(Vector2[] norm, int w, int h, Vector2 offset)
    {
        var r = new Vector2[norm.Length];
        for (int i = 0; i < norm.Length; ++i) { r[i] = new Vector2(norm[i].x * w, norm[i].y * h) + offset; }
        return r;
    }

    // 凸多角形（反時計回り）の内側への符号付き距離（内側が正・px）。
    static float SignedDistance(Vector2[] poly, Vector2 p)
    {
        float best = float.MaxValue;
        for (int i = 0; i < poly.Length; ++i)
        {
            Vector2 a = poly[i];
            Vector2 b = poly[(i + 1) % poly.Length];
            Vector2 e = (b - a).normalized;
            Vector2 inward = new Vector2(-e.y, e.x);
            float d = Vector2.Dot(p - a, inward);
            if (d < best) { best = d; }
        }
        return best;
    }

    // 五弁の桜（花びらの先に切れ込み・真ん中は抜く）。被覆率 0..1。
    static float Flower(Vector2 p, Vector2 c, float radius)
    {
        Vector2 v = p - c;
        float r = v.magnitude;
        if (r > radius * 1.05f) { return 0f; }

        float th = Mathf.Atan2(v.y, v.x) + Mathf.PI * 0.5f;
        float lobe = Mathf.Abs(Mathf.Cos(th * 2.5f));
        float edge = radius * (0.45f + 0.55f * Mathf.Pow(lobe, 0.6f));
        edge -= radius * 0.16f * Mathf.Pow(lobe, 30f);   // 花びらの先の切れ込み

        float a = Mathf.Clamp01((edge - r) / 1.5f);
        float hole = Mathf.Clamp01((r - radius * 0.16f) / 1.5f);
        return a * hole;
    }

    // ストレートアルファの over 合成。
    static Color Over(Color dst, Color src)
    {
        float a = src.a + dst.a * (1f - src.a);
        if (a <= 1e-5f) { return new Color(dst.r, dst.g, dst.b, 0f); }
        float k = dst.a * (1f - src.a);
        return new Color(
            (src.r * src.a + dst.r * k) / a,
            (src.g * src.a + dst.g * k) / a,
            (src.b * src.a + dst.b * k) / a,
            a);
    }

    static RectInt MaskBounds(float[] mask, int w, int h)
    {
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h; ++y)
        {
            for (int x = 0; x < w; ++x)
            {
                if (mask[y * w + x] < 0.5f) { continue; }
                if (x < x0) { x0 = x; }
                if (x > x1) { x1 = x; }
                if (y < y0) { y0 = y; }
                if (y > y1) { y1 = y; }
            }
        }
        if (x1 < 0) { return new RectInt(0, 0, w, h); }
        return new RectInt(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    static float[] Shift(float[] mask, int w, int h, int dx, int dy)
    {
        var r = new float[w * h];
        for (int y = 0; y < h; ++y)
        {
            int sy = y - dy;
            if (sy < 0 || sy >= h) { continue; }
            for (int x = 0; x < w; ++x)
            {
                int sx = x - dx;
                if (sx < 0 || sx >= w) { continue; }
                r[y * w + x] = mask[sy * w + sx];
            }
        }
        return r;
    }

    // ============================================================
    // 筆文字のマスク（BuildCallEffect.RenderGlyphMask と同じ手順）
    //  専用レイヤ＋専用カメラで字のアルファだけを撮る。開いているシーンは写り込まない。
    // ============================================================
    static float[] RenderGlyphMask(string glyph, int w, int h, int fontSize)
    {
        var font = AssetDatabase.LoadAssetAtPath<Font>(BrushFontPath);
        if (font == null)
        {
            Debug.LogError("[BuildTableButtonSkins] 筆書体フォントがありません: " + BrushFontPath);
            return null;
        }

        const int BakeLayer = 31;

        var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
        rt.Create();

        var camGo = new GameObject("__kmj_btn_glyph_cam");
        camGo.hideFlags = HideFlags.HideAndDontSave;
        camGo.layer = BakeLayer;
        camGo.transform.position = new Vector3(0f, 0f, -10f);
        var cam = camGo.AddComponent<Camera>();
        cam.enabled = false;
        cam.orthographic = true;
        cam.orthographicSize = h * 0.5f;
        cam.aspect = (float)w / h;
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 100f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
        cam.cullingMask = 1 << BakeLayer;
        cam.allowHDR = false;
        cam.allowMSAA = false;
        cam.targetTexture = rt;

        var canvasGo = new GameObject("__kmj_btn_glyph_canvas", typeof(RectTransform));
        canvasGo.hideFlags = HideFlags.HideAndDontSave;
        canvasGo.layer = BakeLayer;
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = cam;
        var crt = canvasGo.GetComponent<RectTransform>();
        crt.position = Vector3.zero;
        crt.rotation = Quaternion.identity;
        crt.localScale = Vector3.one;
        crt.sizeDelta = new Vector2(w, h);

        var textGo = new GameObject("__kmj_btn_glyph_text", typeof(RectTransform));
        textGo.hideFlags = HideFlags.HideAndDontSave;
        textGo.layer = BakeLayer;
        textGo.transform.SetParent(canvasGo.transform, false);
        var trt = textGo.GetComponent<RectTransform>();
        trt.anchorMin = new Vector2(0.5f, 0.5f);
        trt.anchorMax = new Vector2(0.5f, 0.5f);
        trt.pivot = new Vector2(0.5f, 0.5f);
        trt.anchoredPosition = Vector2.zero;
        trt.sizeDelta = new Vector2(w, h);

        var text = textGo.AddComponent<Text>();
        text.font = font;
        text.fontSize = fontSize;
        text.fontStyle = FontStyle.Normal;
        text.alignment = TextAnchor.MiddleCenter;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.resizeTextForBestFit = false;
        text.raycastTarget = false;
        text.color = Color.white;
        text.text = glyph;

        // 動的フォントは要求しないとアトラスに字が入らない。
        font.RequestCharactersInTexture(glyph, fontSize, FontStyle.Normal);
        Canvas.ForceUpdateCanvases();
        text.SetAllDirty();
        Canvas.ForceUpdateCanvases();

        cam.Render();

        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var shot = new Texture2D(w, h, TextureFormat.RGBA32, false);
        shot.ReadPixels(new Rect(0f, 0f, w, h), 0, 0);
        shot.Apply();
        RenderTexture.active = prev;

        var raw = shot.GetPixels();
        var mask = new float[w * h];
        float peak = 0f;
        for (int i = 0; i < raw.Length; ++i)
        {
            mask[i] = raw[i].a;
            if (raw[i].a > peak) { peak = raw[i].a; }
        }

        Object.DestroyImmediate(shot);
        Object.DestroyImmediate(textGo);
        Object.DestroyImmediate(canvasGo);
        Object.DestroyImmediate(camGo);
        rt.Release();
        Object.DestroyImmediate(rt);

        if (peak < 0.2f)
        {
            Debug.LogError("[BuildTableButtonSkins] 筆文字「" + glyph + "」が空でした（フォントのアトラス生成失敗）。");
            return null;
        }
        return mask;
    }

    // 字の外側への距離（px）。チャンファー3-4近似の2パス（BuildCallEffect と同じ）。
    static float[] DistanceFromMask(float[] mask, int w, int h)
    {
        const float Far = 1e9f;
        var d = new float[w * h];
        for (int i = 0; i < d.Length; ++i) { d[i] = mask[i] > 0.5f ? 0f : Far; }

        for (int y = 0; y < h; ++y)
        {
            int row = y * w;
            for (int x = 0; x < w; ++x)
            {
                float m = d[row + x];
                if (x > 0) { m = Mathf.Min(m, d[row + x - 1] + 3f); }
                if (y > 0)
                {
                    int up = row - w;
                    m = Mathf.Min(m, d[up + x] + 3f);
                    if (x > 0) { m = Mathf.Min(m, d[up + x - 1] + 4f); }
                    if (x < w - 1) { m = Mathf.Min(m, d[up + x + 1] + 4f); }
                }
                d[row + x] = m;
            }
        }
        for (int y = h - 1; y >= 0; --y)
        {
            int row = y * w;
            for (int x = w - 1; x >= 0; --x)
            {
                float m = d[row + x];
                if (x < w - 1) { m = Mathf.Min(m, d[row + x + 1] + 3f); }
                if (y < h - 1)
                {
                    int dn = row + w;
                    m = Mathf.Min(m, d[dn + x] + 3f);
                    if (x < w - 1) { m = Mathf.Min(m, d[dn + x + 1] + 4f); }
                    if (x > 0) { m = Mathf.Min(m, d[dn + x - 1] + 4f); }
                }
                d[row + x] = m;
            }
        }

        for (int i = 0; i < d.Length; ++i) { d[i] = d[i] / 3f; }
        return d;
    }

    // ============================================================
    // アセット
    // ============================================================
    static Material EnsureMaterial()
    {
        var shader = Shader.Find(ShaderName);
        if (shader == null)
        {
            Debug.LogError("[BuildTableButtonSkins] シェーダーが見つかりません: " + ShaderName);
            return null;
        }

        var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (mat == null)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, MaterialPath);
        }
        mat.shader = shader;
        mat.mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(AtlasPath);
        mat.color = Color.white;
        mat.enableInstancing = false;
        mat.renderQueue = 3000;
        EditorUtility.SetDirty(mat);
        AssetDatabase.SaveAssets();
        return mat;
    }

    // スタイル×日英ぶんの板メッシュ。1つのアセットにまとめ、名前で引いて中身だけ更新する
    // （作り直すとプレハブの参照が切れるため）。
    static Mesh[,] EnsureMeshes(Rect[] uvRects)
    {
        var result = new Mesh[Styles.Length, 2];

        var existing = new System.Collections.Generic.Dictionary<string, Mesh>();
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(MeshAssetPath))
        {
            var m = o as Mesh;
            if (m != null) { existing[m.name] = m; }
        }

        Object container = AssetDatabase.LoadMainAssetAtPath(MeshAssetPath);

        float quadH = QuadW * CellH / CellW;
        for (int s = 0; s < Styles.Length; ++s)
        {
            for (int lang = 0; lang < 2; ++lang)
            {
                string name = "Skin_" + Styles[s].key + (lang == 0 ? "_JA" : "_EN");
                Mesh mesh;
                if (!existing.TryGetValue(name, out mesh))
                {
                    mesh = new Mesh();
                    mesh.name = name;
                    if (container == null)
                    {
                        AssetDatabase.CreateAsset(mesh, MeshAssetPath);
                        container = mesh;
                    }
                    else
                    {
                        AssetDatabase.AddObjectToAsset(mesh, container);
                    }
                }

                var r = uvRects[s * 2 + lang];
                float hw = QuadW * 0.5f;
                float hh = quadH * 0.5f;
                mesh.Clear();
                mesh.vertices = new[]
                {
                    new Vector3(-hw, -hh, 0f), new Vector3(hw, -hh, 0f),
                    new Vector3(-hw, hh, 0f), new Vector3(hw, hh, 0f),
                };
                mesh.uv = new[]
                {
                    new Vector2(r.xMin, r.yMin), new Vector2(r.xMax, r.yMin),
                    new Vector2(r.xMin, r.yMax), new Vector2(r.xMax, r.yMax),
                };
                // 表は -Z（Unity の Quad と同じ）。カメラが -Z 側から見ると +X が右。
                mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
                mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
                mesh.RecalculateBounds();
                EditorUtility.SetDirty(mesh);

                result[s, lang] = mesh;
            }
        }

        AssetDatabase.SaveAssets();
        return result;
    }

    static void WriteTexture(string path, int width, int height, Color[] pixels)
    {
        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
        tex.SetPixels(pixels);
        tex.Apply();
        var png = tex.EncodeToPNG();
        Object.DestroyImmediate(tex);
        System.IO.File.WriteAllBytes(path, png);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer != null)
        {
            importer.textureType = TextureImporterType.Default;
            importer.alphaIsTransparency = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Trilinear;
            importer.mipmapEnabled = true;
            importer.maxTextureSize = 2048;
            importer.sRGBTexture = true;
            importer.SaveAndReimport();
        }
    }

    static Color Hex(string hex)
    {
        Color c;
        ColorUtility.TryParseHtmlString(hex, out c);
        return c;
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) { return; }
        var parts = path.Split('/');
        var current = parts[0];
        for (int i = 1; i < parts.Length; ++i)
        {
            var next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next)) { AssetDatabase.CreateFolder(current, parts[i]); }
            current = next;
        }
    }

    static GameObject EnsureChild(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null) { return t.gameObject; }
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.layer = parent.gameObject.layer;
        return go;
    }

    static bool EnsureProgramAsset(string scriptPath, string assetPath)
    {
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
        if (script == null)
        {
            Debug.LogError("[BuildTableButtonSkins] スクリプトが見つかりません: " + scriptPath);
            return false;
        }
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
            Debug.LogError("[BuildTableButtonSkins] " + type.Name + " の追加に失敗（プログラムアセット未コンパイル？）。");
            Object.DestroyImmediate(added);
            return null;
        }
        return added;
    }
}
#endif
