#if UNITY_EDITOR
// オーラスバナーの骨格生成と配線。
//   Tools > Kemono Mahjong > Build All Last Banner (Prefab)  … 卓Prefabにリグを冪等生成
//   Tools > Kemono Mahjong > Wire All Last Banner (Prefab)   … U#追加＋参照割当＋素材の自動割当
//   Tools > Kemono Mahjong > Build All Last Banner           … シーン上の全卓に生成
//   Tools > Kemono Mahjong > Wire All Last Banner            … 同上の配線
//   Tools > Kemono Mahjong > Validate All Last Banner        … 参照欠損と素材の埋まり具合を報告
// どれも2回実行して安全（冪等）。
//
// **Prefab版とシーン版の両方を回すこと。**
//   ・Build/Wire (Prefab) … 卓Prefab(KemonoMahjong.prefab)が正。8卓はこれを継承する。
//   ・Build/Wire（シーン）… 過去のPlayで焼き付いた prefab override を上書きし直すために必要
//     （リグに実行時の値が残るとPrefab版だけでは直らない）。
//   ・FindObjectsOfType は Prefab Mode では卓を拾わないので、Prefab ステージを開いた
//     状態でシーン版を走らせても何も起きない。
//
// ---- 素材の置き方 ----
// 文字画像 … Assets/Mahjong/Textures/UI/o-rasu/ に置く。
//   日本語 … 「オーラス.png」（既存）。ファイル名は任意（下の EN 規約に当たらないもの）
//   英語   … 名前を "_en" で終わらせる（例 all_last_en.png）。未設置なら日本語画像を流用。
//   ※板は原寸比 2172:724 = 3:1 で作る。テクスチャの width/height から比を取ってはいけない
//     （NPOTはPOT丸め 2048x512 されるので比が 4:1 に化ける）。
// SE … Assets/Mahjong/Sounds/AllLastSFX/ に AudioClip を1本置くと自動で割り当たる。
//   置かなければ無音（バナーだけ出る）。
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using SuzuFactory.AozoraMahjong;
using VRC.Udon;

public static class BuildAllLastBanner
{
    const string RootName = "AllLastBanner";
    const string BannerName = "Banner";
    const string CanvasName = "Canvas";
    const string GlyphName = "Glyph";
    const string ShadowName = "GlyphShadow";

    const string TablePrefabPath = "Assets/Mahjong/Prefabas/KemonoMahjong.prefab";
    const string ProgramAssetDir = "Assets/Mahjong/scene_UdonProgramSources";
    const string ProgramAssetPath = ProgramAssetDir + "/MahjongAllLastBanner Udon C# Program Asset.asset";
    const string ScriptPath = "Assets/Mahjong/Sources/MahjongAllLastBanner.cs";

    const string GlyphTextureDir = "Assets/Mahjong/Textures/UI/o-rasu";
    const string SfxDir = "Assets/Mahjong/Sounds/AllLastSFX";

    // Canvas は他の卓上パネルと同じ 1600x900 の仮想解像度・同じ実寸スケール。
    // 1px = 0.0006132m なので、Canvas 全体は 0.981m x 0.552m。
    const float CanvasScale = 0.0006132f;
    static readonly Vector2 CanvasSize = new Vector2(1600f, 900f);

    // 卓ルートからの高さ（m）。**STARTボタンと同じ高さ**に合わせている。
    //   StartButton は Table/StartButton の local Y 0.100
    //   （卓ルート world -0.305 の卓なら world -0.205、実体は world -0.255..-0.155 の浮きボタン）。
    //   文字の高さ 0.140m なので world -0.275 .. -0.135 を占める。
    // 注意:
    //   ・卓の天面は world Y 0.23 まで＝この帯は卓の手前（下側）。ビルボードで常に
    //     手前に描かれるので卓に隠れない（あがり画面の下辺が卓に潜るのと同じ扱い）。
    //   ・卓中心の真上の ResetButton（world 0.645..0.745）とは完全に離れている。
    const float PanelHeight = 0.100f;

    // 文字板。原寸 2172x724 = 3:1 は必ず保つ（NPOTはPOT丸めで比が変わるので実測しない）。
    // 684x228 px = 0.419m x 0.140m（初版 1140x380＝0.699m から 40% 縮小）。
    static readonly Vector2 GlyphSize = new Vector2(684f, 228f);

    // ============================================================
    //  Build（骨格生成）
    // ============================================================

    [MenuItem("Tools/Kemono Mahjong/Build All Last Banner (Prefab)")]
    public static void BuildPrefab()
    {
        EnsureFolder(SfxDir);
        EnsureGlyphImport();

        var stage = PrefabStageUtility.OpenPrefab(TablePrefabPath);
        if (stage == null)
        {
            Debug.LogError("[BuildAllLastBanner] Prefab を開けません: " + TablePrefabPath);
            return;
        }

        var root = stage.prefabContentsRoot;
        if (root.GetComponent<Mahjong>() == null)
        {
            Debug.LogError("[BuildAllLastBanner] Prefab のルートに Mahjong がありません: " + TablePrefabPath);
            StageUtility.GoToMainStage();
            return;
        }

        BuildOne(root.transform);
        SavePrefabStage(stage);

        Debug.Log("[BuildAllLastBanner] Prefab にオーラスバナーのリグを生成/更新しました: " + TablePrefabPath
            + " … 次に Wire All Last Banner (Prefab) を実行してください。");
    }

    [MenuItem("Tools/Kemono Mahjong/Build All Last Banner")]
    public static void Build()
    {
        EnsureFolder(SfxDir);
        EnsureGlyphImport();

        var tables = Object.FindObjectsOfType<Mahjong>(true);
        if (tables == null || tables.Length == 0)
        {
            Debug.LogWarning("[BuildAllLastBanner] シーンに Mahjong が見つかりません（Prefab Mode なら (Prefab) 版を使ってください）。");
            return;
        }

        foreach (var t in tables) { BuildOne(t.transform); }
        EditorSceneManager.MarkAllScenesDirty();

        Debug.Log("[BuildAllLastBanner] " + tables.Length + " 卓にリグを生成/更新しました。"
            + " … 次に Wire All Last Banner を実行してください。");
    }

    static void BuildOne(Transform tableRoot)
    {
        var root = EnsureChild(tableRoot, RootName);
        root.transform.localPosition = new Vector3(0f, PanelHeight, 0f);
        root.transform.localRotation = Quaternion.identity;
        root.transform.localScale = Vector3.one;
        root.SetActive(true);   // スクリプトを載せる親は常時アクティブ

        var audio = root.GetComponent<AudioSource>();
        if (audio == null) { audio = Undo.AddComponent<AudioSource>(root); }
        audio.playOnAwake = false;
        audio.loop = false;
        audio.spatialBlend = 1f;
        audio.dopplerLevel = 0f;
        audio.minDistance = 2f;
        audio.maxDistance = 16f;
        audio.rolloffMode = AudioRolloffMode.Linear;

        // 表示のON/OFFはここ。ビルボードもこの Transform を回す。
        var banner = EnsureChild(root.transform, BannerName);
        banner.transform.localPosition = Vector3.zero;
        banner.transform.localRotation = Quaternion.identity;
        banner.transform.localScale = Vector3.one;

        var canvas = EnsureCanvas(banner.transform);

        EnsureGlyphImage(canvas.transform, ShadowName);
        EnsureGlyphImage(canvas.transform, GlyphName);

        string[] order = { ShadowName, GlyphName };
        CleanupChildren(canvas.transform, order);
        SetSiblingOrder(canvas.transform, order);

        CleanupChildren(banner.transform, new string[] { CanvasName });
        CleanupChildren(root.transform, new string[] { BannerName });

        banner.SetActive(false);   // 出すのは実行時だけ

        MarkDirty(root);
    }

    static GameObject EnsureCanvas(Transform parent)
    {
        var canvasGo = EnsureUiChild(parent, CanvasName);

        var canvas = canvasGo.GetComponent<Canvas>();
        if (canvas == null) { canvas = canvasGo.AddComponent<Canvas>(); }
        canvas.renderMode = RenderMode.WorldSpace;

        var scaler = canvasGo.GetComponent<CanvasScaler>();
        if (scaler == null) { scaler = canvasGo.AddComponent<CanvasScaler>(); }
        scaler.dynamicPixelsPerUnit = 2f;

        var group = canvasGo.GetComponent<CanvasGroup>();
        if (group == null) { group = canvasGo.AddComponent<CanvasGroup>(); }
        group.alpha = 0f;
        group.interactable = false;
        group.blocksRaycasts = false;

        // 表示専用。GraphicRaycaster は付けない（卓上ボタンの操作を遮らないため）。
        var raycaster = canvasGo.GetComponent<GraphicRaycaster>();
        if (raycaster != null) { Object.DestroyImmediate(raycaster); }

        var rt = canvasGo.GetComponent<RectTransform>();
        rt.localPosition = Vector3.zero;
        rt.localRotation = Quaternion.identity;
        rt.localScale = new Vector3(CanvasScale, CanvasScale, CanvasScale);
        rt.sizeDelta = CanvasSize;

        return canvasGo;
    }

    static RawImage EnsureGlyphImage(Transform parent, string name)
    {
        var go = EnsureUiChild(parent, name);

        var raw = go.GetComponent<RawImage>();
        if (raw == null) { raw = go.AddComponent<RawImage>(); }
        raw.raycastTarget = false;
        raw.color = new Color(1f, 1f, 1f, 0f);   // 実行時にスクリプトが塗る

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = GlyphSize;
        rt.localScale = Vector3.one;
        rt.localRotation = Quaternion.identity;

        return raw;
    }

    // ============================================================
    //  Wire（U#追加＋参照割当）
    // ============================================================

    [MenuItem("Tools/Kemono Mahjong/Wire All Last Banner (Prefab)")]
    public static void WirePrefab()
    {
        if (EnsureProgramAsset())
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[WireAllLastBanner] U#プログラムアセットを生成しました。コンパイル完了後、もう一度実行してください。");
            return;
        }

        var stage = PrefabStageUtility.OpenPrefab(TablePrefabPath);
        if (stage == null)
        {
            Debug.LogError("[WireAllLastBanner] Prefab を開けません: " + TablePrefabPath);
            return;
        }

        var root = stage.prefabContentsRoot;
        var comp = WireOne(root.transform);
        if (comp == null) { SavePrefabStage(stage); return; }

        var table = root.GetComponent<Mahjong>();
        if (table != null)
        {
            table.allLastBanner = comp;
            UdonSharpEditorUtility.CopyProxyToUdon(table);
            EditorUtility.SetDirty(table);
        }

        SavePrefabStage(stage);
        VerifyPrefab();
    }

    [MenuItem("Tools/Kemono Mahjong/Wire All Last Banner")]
    public static void Wire()
    {
        if (EnsureProgramAsset())
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[WireAllLastBanner] U#プログラムアセットを生成しました。コンパイル完了後、もう一度実行してください。");
            return;
        }

        var tables = Object.FindObjectsOfType<Mahjong>(true);
        if (tables == null || tables.Length == 0)
        {
            Debug.LogWarning("[WireAllLastBanner] シーンに Mahjong が見つかりません。");
            return;
        }

        int wired = 0;
        foreach (var t in tables)
        {
            var comp = WireOne(t.transform);
            if (comp == null) { continue; }

            t.allLastBanner = comp;
            UdonSharpEditorUtility.CopyProxyToUdon(t);
            EditorUtility.SetDirty(t);
            ++wired;
        }

        EditorSceneManager.MarkAllScenesDirty();
        Debug.Log("[WireAllLastBanner] " + wired + " / " + tables.Length + " 卓を配線しました（SE: "
            + DescribeSfx(ResolveSfx()) + "）。");
    }

    static MahjongAllLastBanner WireOne(Transform tableRoot)
    {
        var rootT = tableRoot.Find(RootName);
        if (rootT == null)
        {
            Debug.LogError("[WireAllLastBanner] " + RootName + " がありません。先に Build を実行してください: "
                + GetPath(tableRoot));
            return null;
        }

        var comp = (MahjongAllLastBanner)EnsureUSharpComponent(rootT.gameObject, typeof(MahjongAllLastBanner));
        if (comp == null) { return null; }

        var banner = rootT.Find(BannerName);
        var canvas = banner != null ? banner.Find(CanvasName) : null;
        if (banner == null || canvas == null)
        {
            Debug.LogError("[WireAllLastBanner] Banner/Canvas がありません。先に Build を実行してください。");
            return null;
        }

        comp.bannerRoot = banner.gameObject;
        comp.canvasGroup = canvas.GetComponent<CanvasGroup>();
        comp.glyphImage = FindRaw(canvas, GlyphName);
        comp.glyphRect = FindRect(canvas, GlyphName);
        comp.shadowImage = FindRaw(canvas, ShadowName);
        comp.shadowRect = FindRect(canvas, ShadowName);

        // 素材（文字画像）。日本語は必須、英語は任意。
        Texture ja, en;
        ResolveGlyphTextures(out ja, out en);
        comp.glyphJa = ja;
        comp.glyphEn = en;
        if (comp.glyphImage != null && ja != null) { comp.glyphImage.texture = ja; }
        if (comp.shadowImage != null && ja != null) { comp.shadowImage.texture = ja; }

        comp.seSource = rootT.GetComponent<AudioSource>();
        comp.se = ResolveSfx();

        // U# のシリアライズ済み値は既定値を変えても更新されない＝見た目の確定値はWireで毎回代入する。
        comp.billboardToLocalPlayer = true;
        comp.showOnSuddenDeath = true;
        comp.enterSeconds = 0.14f;
        comp.settleSeconds = 0.10f;
        comp.holdSeconds = 1.12f;
        comp.exitSeconds = 0.34f;
        comp.dealDelaySeconds = 1.36f;
        comp.enterScale = 2.10f;
        comp.overshootScale = 0.94f;
        comp.exitScale = 1.08f;
        comp.driftDistance = 16f;
        comp.glyphColor = new Color(1f, 1f, 1f, 1f);
        comp.shadowColor = new Color(0.03f, 0.02f, 0.01f, 0.55f);
        comp.shadowOffset = new Vector2(12f, -12f);
        comp.seVolume = 0.10f;

        UdonSharpEditorUtility.CopyProxyToUdon(comp);
        EditorUtility.SetDirty(comp);
        MarkDirty(rootT.gameObject);

        return comp;
    }

    // ============================================================
    //  Validate
    // ============================================================

    [MenuItem("Tools/Kemono Mahjong/Validate All Last Banner")]
    public static void Validate()
    {
        int errors = 0;
        int checkedTables = 0;

        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(TablePrefabPath);
        if (asset == null)
        {
            Debug.LogError("[ValidateAllLastBanner] Prefab を読めません: " + TablePrefabPath);
            ++errors;
        }
        else
        {
            errors += ValidateOne(asset.transform, "Prefab:" + asset.name);
            ++checkedTables;
        }

        var tables = Object.FindObjectsOfType<Mahjong>(true);
        if (tables != null)
        {
            foreach (var t in tables)
            {
                errors += ValidateOne(t.transform, GetPath(t.transform));
                ++checkedTables;
            }
        }

        Texture ja, en;
        ResolveGlyphTextures(out ja, out en);
        Debug.Log("[ValidateAllLastBanner] 検査 " + checkedTables + " 件 / エラー " + errors
            + " … 文字画像 JA=" + (ja != null ? ja.name : "なし(必須)")
            + " / EN=" + (en != null ? en.name : "なし(日本語を流用)")
            + " / SE=" + DescribeSfx(ResolveSfx()));
    }

    static int ValidateOne(Transform tableRoot, string label)
    {
        int errors = 0;

        var rootT = tableRoot.Find(RootName);
        if (rootT == null)
        {
            Debug.LogError("[ValidateAllLastBanner] " + label + ": " + RootName + " が無い（Build未実行）");
            return 1;
        }
        if (!rootT.gameObject.activeSelf)
        {
            Debug.LogError("[ValidateAllLastBanner] " + label + ": " + RootName
                + " が非アクティブ＝スクリプトが動かない（役満の打ち上げで踏んだ地雷と同じ）");
            ++errors;
        }

        var comp = rootT.GetComponent<MahjongAllLastBanner>();
        if (comp == null)
        {
            Debug.LogError("[ValidateAllLastBanner] " + label + ": MahjongAllLastBanner が無い（Wire未実行）");
            return errors + 1;
        }

        if (comp.bannerRoot == null) { Debug.LogError("[ValidateAllLastBanner] " + label + ": bannerRoot 未割当"); ++errors; }
        if (comp.canvasGroup == null) { Debug.LogError("[ValidateAllLastBanner] " + label + ": canvasGroup 未割当"); ++errors; }
        if (comp.glyphImage == null) { Debug.LogError("[ValidateAllLastBanner] " + label + ": glyphImage 未割当"); ++errors; }
        if (comp.glyphRect == null) { Debug.LogError("[ValidateAllLastBanner] " + label + ": glyphRect 未割当"); ++errors; }
        if (comp.glyphJa == null) { Debug.LogError("[ValidateAllLastBanner] " + label + ": 文字画像(JA) 未割当"); ++errors; }
        if (comp.bannerRoot != null && comp.bannerRoot.activeSelf)
        {
            Debug.LogWarning("[ValidateAllLastBanner] " + label
                + ": bannerRoot が出たまま（実行時に出し入れするので通常はOFF）");
        }

        // 板の比（3:1）が崩れていないか。素材の縦横比ではなく原寸比で見る。
        if (comp.glyphRect != null)
        {
            var size = comp.glyphRect.sizeDelta;
            if (size.y > 0.01f && Mathf.Abs(size.x / size.y - 3.0f) > 0.05f)
            {
                Debug.LogError("[ValidateAllLastBanner] " + label + ": 文字板の比が 3:1 でない ("
                    + size.x + "x" + size.y + ")＝素材が縦横に潰れる");
                ++errors;
            }
        }

        var table = tableRoot.GetComponent<Mahjong>();
        if (table == null)
        {
            Debug.LogError("[ValidateAllLastBanner] " + label + ": ルートに Mahjong が無い");
            ++errors;
        }
        else if (table.allLastBanner == null)
        {
            Debug.LogError("[ValidateAllLastBanner] " + label + ": 卓→バナー の参照が空（Wire未実行）");
            ++errors;
        }

        return errors;
    }

    static void VerifyPrefab()
    {
        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(TablePrefabPath);
        if (asset == null)
        {
            Debug.LogError("[WireAllLastBanner] Prefab を読み直せません: " + TablePrefabPath);
            return;
        }

        var comp = asset.GetComponentInChildren<MahjongAllLastBanner>(true);
        if (comp == null)
        {
            Debug.LogError("[WireAllLastBanner] 保存後の Prefab に MahjongAllLastBanner がありません。");
            return;
        }

        var table = asset.GetComponent<Mahjong>();
        if (table == null || table.allLastBanner == null)
        {
            Debug.LogError("[WireAllLastBanner] 保存後の Prefab で 卓→バナー の参照が空です。");
            return;
        }

        Debug.Log("[WireAllLastBanner] Prefab 検証OK（演出長 " + comp.GetDuration().ToString("0.00")
            + "s／配牌待ち " + comp.GetDealDelay().ToString("0.00") + "s）: " + TablePrefabPath);
    }

    // ============================================================
    //  素材の解決
    // ============================================================

    // 文字画像。フォルダ内のテクスチャから、名前が "_en" で終わるものを英語、
    // それ以外の最初の1枚を日本語として拾う（日本語のファイル名に依存しない）。
    static void ResolveGlyphTextures(out Texture ja, out Texture en)
    {
        ja = null;
        en = null;
        if (!AssetDatabase.IsValidFolder(GlyphTextureDir)) { return; }

        foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { GlyphTextureDir }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (name == null) { continue; }
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (tex == null) { continue; }

            bool isEn = name.ToLowerInvariant().EndsWith("_en");
            if (isEn) { if (en == null) { en = tex; } }
            else if (ja == null) { ja = tex; }
        }
    }

    // 差し替えフォルダの音源（最初の1本）。無ければ null＝無音。
    static AudioClip ResolveSfx()
    {
        if (!AssetDatabase.IsValidFolder(SfxDir)) { return null; }
        foreach (var guid in AssetDatabase.FindAssets("t:AudioClip", new[] { SfxDir }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var found = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (found != null) { return found; }
        }
        return null;
    }

    static string DescribeSfx(AudioClip clip)
    {
        if (clip == null) { return "なし（無音）"; }
        return clip.name + " " + clip.length.ToString("F2") + "秒";
    }

    // 文字画像の取り込み設定。UI用の透過テクスチャとして揃える。
    // NPOT は圧縮指定が黙殺されて RGB24 に落ちるので、POT丸め（ToNearest）を必ず有効にする。
    static void EnsureGlyphImport()
    {
        Texture ja, en;
        ResolveGlyphTextures(out ja, out en);

        ApplyGlyphImport(ja);
        ApplyGlyphImport(en);
    }

    static void ApplyGlyphImport(Texture tex)
    {
        if (tex == null) { return; }
        var path = AssetDatabase.GetAssetPath(tex);
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null) { return; }

        bool changed = false;
        if (importer.textureType != TextureImporterType.Default) { importer.textureType = TextureImporterType.Default; changed = true; }
        if (!importer.alphaIsTransparency) { importer.alphaIsTransparency = true; changed = true; }
        if (importer.wrapMode != TextureWrapMode.Clamp) { importer.wrapMode = TextureWrapMode.Clamp; changed = true; }
        if (!importer.mipmapEnabled) { importer.mipmapEnabled = true; changed = true; }
        if (importer.npotScale != TextureImporterNPOTScale.ToNearest) { importer.npotScale = TextureImporterNPOTScale.ToNearest; changed = true; }
        if (importer.textureCompression != TextureImporterCompression.Compressed) { importer.textureCompression = TextureImporterCompression.Compressed; changed = true; }
        // 容量削減：表示幅 0.419m なので 1024px でも 2444px/m（256px/m で「くっきり」）。
        // 2048 だと POT丸めで 2048x512 になり1枚で数MB食う。Questビルドの容量に直結する。
        if (importer.maxTextureSize > 1024) { importer.maxTextureSize = 1024; changed = true; }

        if (changed)
        {
            importer.SaveAndReimport();
            Debug.Log("[BuildAllLastBanner] 文字画像の取り込み設定を整えました: " + path);
        }
    }

    // ============================================================
    //  共通ヘルパー（BuildYakumanCutin と同じ流儀）
    // ============================================================

    // Prefab ステージを保存して**必ず閉じる**。
    //  ※ 開いたまま／dirty のままにすると Unity が固まる。
    static void SavePrefabStage(PrefabStage stage)
    {
        if (stage == null || stage.prefabContentsRoot == null)
        {
            Debug.LogError("[BuildAllLastBanner] Prefab Stage が無効なため保存できません。");
            StageUtility.GoToMainStage();
            return;
        }

        EditorSceneManager.MarkSceneDirty(stage.scene);
        bool saveInvoked = EditorApplication.ExecuteMenuItem("File/Save");
        if (!saveInvoked || stage.scene.isDirty)
        {
            Debug.LogError("[BuildAllLastBanner] Prefab の保存に失敗しました: " + stage.assetPath);
            return;
        }

        AssetDatabase.SaveAssets();
        StageUtility.GoToMainStage();
    }

    static bool EnsureProgramAsset()
    {
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(ScriptPath);
        if (script == null)
        {
            Debug.LogError("[WireAllLastBanner] スクリプトが見つかりません: " + ScriptPath);
            return false;
        }
        var type = script.GetClass();
        if (type != null && UdonSharpProgramAsset.GetProgramAssetForClass(type) != null) { return false; }
        if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(ProgramAssetPath) != null) { return false; }

        EnsureFolder(ProgramAssetDir);

        var programAsset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
        programAsset.sourceCsScript = script;
        AssetDatabase.CreateAsset(programAsset, ProgramAssetPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[WireAllLastBanner] U#プログラムアセットを生成: " + ProgramAssetPath);
        return true;
    }

    static UdonSharpBehaviour EnsureUSharpComponent(GameObject go, System.Type type)
    {
        var proxy = (UdonSharpBehaviour)go.GetComponent(type);
        if (proxy != null)
        {
            if (UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy) != null) { return proxy; }

            Debug.LogWarning("[WireAllLastBanner] " + type.Name + " の backing UdonBehaviour が無いため作り直します。");
            Object.DestroyImmediate(proxy);
            foreach (var ub in go.GetComponents<UdonBehaviour>())
            {
                if (ub.programSource == null) { Object.DestroyImmediate(ub); }
            }
        }

        var added = (UdonSharpBehaviour)UdonSharpUndo.AddComponent(go, type);
        if (added != null && UdonSharpEditorUtility.GetBackingUdonBehaviour(added) == null)
        {
            Debug.LogError("[WireAllLastBanner] " + type.Name + " の追加に失敗（プログラムアセット未コンパイル？）。");
            Object.DestroyImmediate(added);
            return null;
        }
        return added;
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) { return; }

        var parts = path.Split('/');
        var current = parts[0];
        for (int i = 1; i < parts.Length; ++i)
        {
            var next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
            {
                AssetDatabase.CreateFolder(current, parts[i]);
            }
            current = next;
        }
    }

    static GameObject EnsureChild(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null) { return t.gameObject; }
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Create " + name);
        go.transform.SetParent(parent, false);
        return go;
    }

    static GameObject EnsureUiChild(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null)
        {
            if (t.GetComponent<RectTransform>() == null)
            {
                Undo.DestroyObjectImmediate(t.gameObject);
            }
            else
            {
                return t.gameObject;
            }
        }
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Create " + name);
        go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);
        return go;
    }

    static RawImage FindRaw(Transform parent, string name)
    {
        var t = parent.Find(name);
        return t != null ? t.GetComponent<RawImage>() : null;
    }

    static RectTransform FindRect(Transform parent, string name)
    {
        var t = parent.Find(name);
        return t != null ? t.GetComponent<RectTransform>() : null;
    }

    static void SetSiblingOrder(Transform parent, string[] orderedNames)
    {
        for (int i = 0; i < orderedNames.Length; ++i)
        {
            var c = parent.Find(orderedNames[i]);
            if (c != null) { c.SetSiblingIndex(i); }
        }
    }

    static void CleanupChildren(Transform parent, string[] keepNames)
    {
        var keep = new System.Collections.Generic.HashSet<string>(keepNames);
        for (int i = parent.childCount - 1; i >= 0; --i)
        {
            var c = parent.GetChild(i);
            if (!keep.Contains(c.name)) { Undo.DestroyObjectImmediate(c.gameObject); }
        }
    }

    static void MarkDirty(GameObject go)
    {
        if (go.scene.IsValid())
        {
            EditorSceneManager.MarkSceneDirty(go.scene);
        }
        else
        {
            EditorUtility.SetDirty(go);
        }
    }

    static string GetPath(Transform t)
    {
        var path = t.name;
        while (t.parent != null)
        {
            t = t.parent;
            path = t.name + "/" + path;
        }
        return path;
    }
}
#endif
