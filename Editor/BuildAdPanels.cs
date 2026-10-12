#if UNITY_EDITOR
// 広告パネル（A4）の生成・検証エディタスクリプト。
//
// ■ 何を作るか
//   1) Assets/Mahjong/AdPanels/AdPanel.prefab
//        AdPanel                … 空の親。AdPanel（U#）を持つ。原点＝壁に接する面。+Z が表。
//         ├ Frame               … 額縁（Cube・原点から背面へ 12mm）。ポスターの外周 8mm が縁に見える。
//         └ Poster              … A4 の絵が出る Quad（0.210 x 0.297 m ＝ A4 実寸）
//   2) Assets/Mahjong/AdPanels/Materials/AdPanel_Poster.mat（Unlit/Texture・全パネル共有）
//      Assets/Mahjong/AdPanels/Materials/AdPanel_Frame.mat （Unlit/Color）
//   3) Assets/Mahjong/AdPanels/Textures/AdPanel_Placeholder.png（画像を入れるまでの仮絵）
//   4) シーンルート "AdPanels"（AdPanelManager を持つ）
//
// ■ 使い方（画像を入れるのはユーザー）
//   a) Tools > Kemono Mahjong > Build Ad Panels          … 上の一式を作る（何度実行してもよい）
//   b) A4 の画像を Assets/Mahjong/AdPanels/Posters/ に入れる
//   c) Tools > Kemono Mahjong > Setup Ad Poster Textures … 画像のインポート設定をQuest向けに揃える
//   d) AdPanel.prefab を "AdPanels" の子として好きなだけ置く
//      （Tools > Kemono Mahjong > Add Ad Panel (Scene View) でシーンビューの中心に1枚置ける）
//   e) 各パネルの Inspector の posters に画像を入れる（1分ごとにランダム順で切り替わる）
//   f) Tools > Kemono Mahjong > Validate Ad Panels       … 配線とテクスチャ容量を点検
//
// ■ 落とし穴（設計上の約束）
//   ・パネルは必ず "AdPanels" の子に置くこと。Manager は Start 時に自分の子から
//     AdPanel を自動収集するため、外に置いたパネルは巡回しない（Validate が検出する）。
//   ・マテリアルは全パネルで1枚を共有し、絵の差し替えは MaterialPropertyBlock で行う。
//     Inspector でパネルごとにマテリアルを差し替えると、その枚数だけマテリアルが増える。
//   ・A4 実寸は小さい。A3 は root の Scale 1.414、A2 は 2.0、A1 は 2.83、A0 は 4.0。
//     横長にしたい時は root の Z 回転を 90 度にする。
using System.Collections.Generic;
using System.IO;
using SuzuFactory.AozoraMahjong;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.Udon;

public static class BuildAdPanels
{
    const string RootFolder = "Assets/Mahjong/AdPanels";
    const string MatFolder = RootFolder + "/Materials";
    const string TexFolder = RootFolder + "/Textures";
    const string PosterFolder = RootFolder + "/Posters";
    const string PrefabPath = RootFolder + "/AdPanel.prefab";
    const string PosterMatPath = MatFolder + "/AdPanel_Poster.mat";
    const string FrameMatPath = MatFolder + "/AdPanel_Frame.mat";
    const string PlaceholderPath = TexFolder + "/AdPanel_Placeholder.png";
    const string ProgramFolder = "Assets/Mahjong/scene_UdonProgramSources";
    const string PanelScriptPath = "Assets/Mahjong/Sources/AdPanel.cs";
    const string ManagerScriptPath = "Assets/Mahjong/Sources/AdPanelManager.cs";
    const string PanelProgramPath = ProgramFolder + "/AdPanel Udon C# Program Asset.asset";
    const string ManagerProgramPath = ProgramFolder + "/AdPanelManager Udon C# Program Asset.asset";
    const string SceneRootName = "AdPanels";

    // --- 寸法（メートル）---
    const float A4Width = 0.210f;    // A4 実寸
    const float A4Height = 0.297f;
    const float FrameBorder = 0.008f; // ポスターの外に出る縁の幅
    const float FrameDepth = 0.012f;  // 額縁の厚み（原点から背面へ）
    const float PosterOffset = 0.001f; // Zファイト回避のため額縁の前面から1mm浮かせる

    const float DefaultIntervalSeconds = 60f; // 1分
    const float DefaultFadeSeconds = 0.6f;
    const string PosterShaderName = "KemonoMahjong/AdPanelCrossfade";

    static readonly Color FrameColor = new Color(0.075f, 0.078f, 0.090f, 1f);

    // ============================================================
    // Build
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Build Ad Panels")]
    public static void Build()
    {
        EnsureFolders();
        EnsureProgramAsset(PanelScriptPath, PanelProgramPath, typeof(AdPanel));
        EnsureProgramAsset(ManagerScriptPath, ManagerProgramPath, typeof(AdPanelManager));

        var placeholder = EnsurePlaceholderTexture();
        var posterMat = EnsurePosterMaterial(placeholder);
        var frameMat = EnsureFrameMaterial();

        // 既にプレハブがある＝シーンにインスタンスが置かれている可能性がある。
        // SaveAsPrefabAsset で上書きするとインスタンス側の設定（posters など）を壊しかねないので、
        // 既存は温存する。中身を作り直したい時だけ Rebuild Ad Panel Prefab (Overwrite) を使う。
        if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null)
        {
            if (!BuildPrefab(posterMat, frameMat, placeholder)) { return; }
        }
        else
        {
            Debug.Log("[BuildAdPanels] 既存の " + PrefabPath + " は温存しました（配置済みインスタンスを壊さないため）。\n" +
                      "中身を作り直すときは Tools > Kemono Mahjong > Rebuild Ad Panel Prefab (Overwrite)。");
        }
        if (!BuildSceneRoot()) { return; }

        AssetDatabase.SaveAssets();
        Debug.Log("[BuildAdPanels] 完了：" + PrefabPath + " と シーンルート \"" + SceneRootName + "\" を用意しました。\n" +
                  "次は A4 画像を " + PosterFolder + " に入れて、Setup Ad Poster Textures を実行してください。");
        Validate();
    }
    // ------------------------------------------------------------
    // プレハブを作り直す（既存を上書き）
    // ------------------------------------------------------------
    [MenuItem("Tools/Kemono Mahjong/Rebuild Ad Panel Prefab (Overwrite)")]
    public static void RebuildPrefab()
    {
        // 配置済みインスタンスの上書き設定（posters・Start Offset）は Unity 側で引き継がれるが、
        // 保証はされない。実行前にシーンを保存しておくこと。
        EnsureFolders();
        var placeholder = EnsurePlaceholderTexture();
        var posterMat = EnsurePosterMaterial(placeholder);
        var frameMat = EnsureFrameMaterial();
        if (!BuildPrefab(posterMat, frameMat, placeholder)) { return; }
        AssetDatabase.SaveAssets();
        Validate();
    }


    // ------------------------------------------------------------
    // プレハブ本体
    // ------------------------------------------------------------
    static bool BuildPrefab(Material posterMat, Material frameMat, Texture2D placeholder)
    {
        // 一度シーン上に組んでからプレハブとして保存する。
        // （U# コンポーネントはプレハブアセットへ直接 AddComponent できないため。）
        var root = new GameObject("AdPanel");
        try
        {
            // --- 額縁（原点より背面。壁に接する面が原点になる）---
            var frame = GameObject.CreatePrimitive(PrimitiveType.Cube);
            frame.name = "Frame";
            Object.DestroyImmediate(frame.GetComponent<Collider>()); // 壁付け前提。物理は要らない
            frame.transform.SetParent(root.transform, false);
            frame.transform.localPosition = new Vector3(0f, 0f, -FrameDepth * 0.5f);
            frame.transform.localScale = new Vector3(A4Width + FrameBorder * 2f, A4Height + FrameBorder * 2f, FrameDepth);
            var frameRenderer = frame.GetComponent<MeshRenderer>();
            frameRenderer.sharedMaterial = frameMat;
            TuneRenderer(frameRenderer);

            // --- ポスター面（Quad の表は -Z なので Y を180度回して root の +Z に向ける）---
            var poster = GameObject.CreatePrimitive(PrimitiveType.Quad);
            poster.name = "Poster";
            Object.DestroyImmediate(poster.GetComponent<Collider>());
            poster.transform.SetParent(root.transform, false);
            poster.transform.localPosition = new Vector3(0f, 0f, PosterOffset);
            poster.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            poster.transform.localScale = new Vector3(A4Width, A4Height, 1f);
            var posterRenderer = poster.GetComponent<MeshRenderer>();
            posterRenderer.sharedMaterial = posterMat;
            TuneRenderer(posterRenderer);

            // --- U# コンポーネント ---
            var panel = (AdPanel)EnsureUSharpComponent(root, typeof(AdPanel));
            if (panel == null) { return false; }

            panel.posterRenderer = posterRenderer;
            panel.materialSlot = 0;
            panel.textureProperty = "_MainTex";
            panel.startOffset = 0;
            panel.shuffle = true;
            panel.posters = new Texture[] { placeholder };
            UdonSharpEditorUtility.CopyProxyToUdon(panel);
            EditorUtility.SetDirty(panel);

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log("[BuildAdPanels] プレハブを保存しました: " + PrefabPath);
            return true;
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    static void TuneRenderer(MeshRenderer r)
    {
        // Unlit なので影もライトプローブも要らない。Quest 向けに全部落とす。
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
    }

    // ------------------------------------------------------------
    // シーンルート（Manager）
    // ------------------------------------------------------------
    static bool BuildSceneRoot()
    {
        var rootGo = GameObject.Find(SceneRootName);
        if (rootGo == null)
        {
            rootGo = new GameObject(SceneRootName);
            Undo.RegisterCreatedObjectUndo(rootGo, "Create AdPanels root");
            Debug.Log("[BuildAdPanels] シーンに \"" + SceneRootName + "\" を作りました。広告パネルはこの子に置いてください。");
        }

        var manager = (AdPanelManager)EnsureUSharpComponent(rootGo, typeof(AdPanelManager));
        if (manager == null) { return false; }

        if (manager.intervalSeconds < 1f) { manager.intervalSeconds = DefaultIntervalSeconds; }
        if (manager.checkIntervalSeconds < 0.1f) { manager.checkIntervalSeconds = 1f; }
        if (manager.panels == null) { manager.panels = new AdPanel[0]; }
        UdonSharpEditorUtility.CopyProxyToUdon(manager);
        EditorUtility.SetDirty(manager);
        EditorSceneManager.MarkSceneDirty(rootGo.scene);
        return true;
    }

    // ============================================================
    // シーンビューの中心に1枚置く
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Add Ad Panel (Scene View)")]
    public static void AddPanelAtSceneView()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null)
        {
            Debug.LogError("[AddAdPanel] " + PrefabPath + " がありません。先に Build Ad Panels を実行してください。");
            return;
        }
        var rootGo = GameObject.Find(SceneRootName);
        if (rootGo == null)
        {
            Debug.LogError("[AddAdPanel] シーンに \"" + SceneRootName + "\" がありません。先に Build Ad Panels を実行してください。");
            return;
        }

        var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, rootGo.transform);
        var view = SceneView.lastActiveSceneView;
        if (view != null && view.camera != null)
        {
            // シーンビューの注視点に、カメラ側を向けて置く（+Z が表なので forward の逆を向ける）。
            instance.transform.position = view.pivot;
            var fwd = -view.camera.transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f) { fwd = Vector3.forward; }
            instance.transform.rotation = Quaternion.LookRotation(fwd.normalized, Vector3.up);
        }
        instance.name = "AdPanel_" + (rootGo.transform.childCount);

        // ランダム順の種はパネルごとに変えないと、全パネルが同じ絵で揃ってしまう。
        var addedPanel = instance.GetComponent<AdPanel>();
        if (addedPanel != null)
        {
            addedPanel.startOffset = rootGo.transform.childCount - 1;
            UdonSharpEditorUtility.CopyProxyToUdon(addedPanel);
            EditorUtility.SetDirty(addedPanel);
        }
        Undo.RegisterCreatedObjectUndo(instance, "Add Ad Panel");
        Selection.activeGameObject = instance;
        EditorSceneManager.MarkSceneDirty(instance.scene);
        Debug.Log("[AddAdPanel] " + instance.name + " を置きました。Inspector の posters に画像を入れてください。");
    }

    // ============================================================
    // パネル固有番号（Start Offset）の一括採番
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Reseed Ad Panels")]
    public static void ReseedPanels()
    {
        var rootGo = GameObject.Find(SceneRootName);
        if (rootGo == null)
        {
            Debug.LogError("[ReseedAdPanels] シーンに \"" + SceneRootName + "\" がありません。");
            return;
        }

        // 同じ startOffset のパネルは乱数の種も同じ＝同じ画像セットなら全部同じ絵で揃ってしまう。
        // プレハブをまとめてドラッグしたあとはこれを実行する。
        var list = rootGo.GetComponentsInChildren<AdPanel>(true);
        for (int i = 0; i < list.Length; i++)
        {
            list[i].startOffset = i;
            UdonSharpEditorUtility.CopyProxyToUdon(list[i]);
            EditorUtility.SetDirty(list[i]);
        }
        EditorSceneManager.MarkSceneDirty(rootGo.scene);
        Debug.Log("[ReseedAdPanels] " + list.Length + " 枚の Start Offset を 0.." + (list.Length - 1) + " へ振り直しました。");
    }

    // ============================================================
    // ポスター画像のインポート設定を揃える
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Setup Ad Poster Textures")]
    public static void SetupPosterTextures()
    {
        if (!AssetDatabase.IsValidFolder(PosterFolder))
        {
            EnsureFolders();
            Debug.LogWarning("[SetupAdPosters] " + PosterFolder + " を作りました。ここに A4 画像を入れてから再実行してください。");
            return;
        }

        var guids = AssetDatabase.FindAssets("t:Texture2D", new string[] { PosterFolder });
        if (guids.Length == 0)
        {
            Debug.LogWarning("[SetupAdPosters] " + PosterFolder + " に画像がありません。");
            return;
        }

        int done = 0;
        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (ApplyPosterImportSettings(path)) { done++; }
        }
        AssetDatabase.SaveAssets();
        Debug.Log("[SetupAdPosters] " + done + " 枚のインポート設定を更新しました（長辺1024・PC:DXT1 / Quest:ASTC6x6・mipmap ON・Clamp）。\n" +
                  "NPOT の元画像（例 724x1024）はインポート時に POT へ拡大されます。貼る先は UV 0-1 なので見た目は変わりません。");
    }

    static bool ApplyPosterImportSettings(string path)
    {
        var ti = AssetImporter.GetAtPath(path) as TextureImporter;
        if (ti == null) { return false; }

        ti.textureType = TextureImporterType.Default;
        ti.sRGBTexture = true;
        ti.alphaSource = TextureImporterAlphaSource.None;  // ポスターに透過は要らない（容量の無駄）
        ti.alphaIsTransparency = false;
        ti.mipmapEnabled = true;                            // 遠景でジャリつかせない
        ti.wrapMode = TextureWrapMode.Clamp;                // 端の色が反対側へ回り込むのを防ぐ
        ti.filterMode = FilterMode.Bilinear;
        ti.anisoLevel = 1;
        // NPOT のままだと mipmap 付きの圧縮フォーマットが使えず、下の ASTC/DXT 指定が黙って無視され
        // RGB24（非圧縮）で焼かれる（1枚 6.6MB）。エラーも警告も出ないので見つけにくい。
        // 貼る先は UV 0-1 の固定クアド（AdPanel は _MainTex を MPB で差し替えるだけ）なので、
        // インポート時に POT へ伸ばしても見た目は変わらない（A4比はクアド側が持っている）。
        ti.npotScale = TextureImporterNPOTScale.ToLarger;   // 拡大で POT 化＝元画像の情報量を落とさない
        ti.maxTextureSize = 1024;
        ti.textureCompression = TextureImporterCompression.Compressed;

        var pc = new TextureImporterPlatformSettings();
        pc.name = "Standalone";
        pc.overridden = true;
        pc.maxTextureSize = 1024;
        pc.format = TextureImporterFormat.DXT1;
        pc.textureCompression = TextureImporterCompression.Compressed;
        pc.compressionQuality = 50;
        ti.SetPlatformTextureSettings(pc);

        var android = new TextureImporterPlatformSettings();
        android.name = "Android";
        android.overridden = true;
        android.maxTextureSize = 1024;
        android.format = TextureImporterFormat.ASTC_6x6;
        android.textureCompression = TextureImporterCompression.Compressed;
        android.compressionQuality = 50;
        ti.SetPlatformTextureSettings(android);

        ti.SaveAndReimport();
        return true;
    }

    // ============================================================
    // Validate
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Validate Ad Panels")]
    public static void Validate()
    {
        int errors = 0;
        int warnings = 0;

        var rootGo = GameObject.Find(SceneRootName);
        if (rootGo == null)
        {
            Debug.LogError("[ValidateAdPanels] シーンに \"" + SceneRootName + "\" がありません。Build Ad Panels を実行してください。");
            return;
        }

        var manager = rootGo.GetComponent<AdPanelManager>();
        if (manager == null)
        {
            Debug.LogError("[ValidateAdPanels] " + SceneRootName + " に AdPanelManager が付いていません。");
            errors++;
        }
        else
        {
            if (UdonSharpEditorUtility.GetBackingUdonBehaviour(manager) == null)
            {
                Debug.LogError("[ValidateAdPanels] AdPanelManager の backing UdonBehaviour がありません（ビルドに乗りません）。");
                errors++;
            }
            Debug.Log("[ValidateAdPanels] 切り替え間隔: " + manager.intervalSeconds + " 秒");
        }

        var panels = Object.FindObjectsOfType<AdPanel>(true);
        int inside = 0;
        var uniqueTextures = new HashSet<Texture>();

        foreach (var p in panels)
        {
            bool underRoot = p.transform.IsChildOf(rootGo.transform);
            if (!underRoot)
            {
                Debug.LogError("[ValidateAdPanels] " + Path(p.transform) + " が \"" + SceneRootName +
                               "\" の外にあります。Manager は子からしか集めないので巡回しません。", p);
                errors++;
            }
            else { inside++; }

            if (UdonSharpEditorUtility.GetBackingUdonBehaviour(p) == null)
            {
                Debug.LogError("[ValidateAdPanels] " + Path(p.transform) + " の backing UdonBehaviour がありません。", p);
                errors++;
            }
            if (p.posterRenderer == null)
            {
                Debug.LogError("[ValidateAdPanels] " + Path(p.transform) + " の posterRenderer が空です。", p);
                errors++;
            }
            if (p.posters == null || p.posters.Length == 0)
            {
                Debug.LogWarning("[ValidateAdPanels] " + Path(p.transform) + " の posters が空です（マテリアル既定の絵のままになります）。", p);
                warnings++;
            }
            else
            {
                for (int i = 0; i < p.posters.Length; i++)
                {
                    if (p.posters[i] == null)
                    {
                        Debug.LogWarning("[ValidateAdPanels] " + Path(p.transform) + " の posters[" + i + "] が空です（飛ばされます）。", p);
                        warnings++;
                        continue;
                    }
                    uniqueTextures.Add(p.posters[i]);
                }
            }
        }

        // テクスチャの実寸・容量点検（Quest のVRAMに直接効く）
        float totalMb = 0f;
        foreach (var t in uniqueTextures)
        {
            var t2d = t as Texture2D;
            if (t2d == null) { continue; }
            totalMb += EstimateMegabytes(t2d);
            if ((t2d.width % 4) != 0 || (t2d.height % 4) != 0)
            {
                Debug.LogWarning("[ValidateAdPanels] " + t2d.name + " は " + t2d.width + "x" + t2d.height +
                                 "（4の倍数でない）＝圧縮が効かず非圧縮に落ちます。書き出しサイズを直してください。", t2d);
                warnings++;
            }
            if (t2d.width > 1024 || t2d.height > 1024)
            {
                Debug.LogWarning("[ValidateAdPanels] " + t2d.name + " が " + t2d.width + "x" + t2d.height +
                                 " と大きいままです。Setup Ad Poster Textures を実行してください。", t2d);
                warnings++;
            }
        }

        // 同じ Start Offset のパネルが複数あると、同じ画像セットなら全部同じ絵で揃ってしまう。
        var seedCount = new Dictionary<int, int>();
        int shuffleCount = 0;
        foreach (var p in panels)
        {
            if (p.shuffle) { shuffleCount++; }
            if (!p.transform.IsChildOf(rootGo.transform)) { continue; }
            int seed = p.startOffset;
            seedCount[seed] = seedCount.ContainsKey(seed) ? seedCount[seed] + 1 : 1;
        }
        foreach (var kv in seedCount)
        {
            if (kv.Value > 1)
            {
                Debug.LogWarning("[ValidateAdPanels] Start Offset = " + kv.Key + " のパネルが " + kv.Value +
                                 " 枚あります。同じ画像セットなら同じ絵で揃います。Reseed Ad Panels で振り直せます。");
                warnings++;
            }
        }
        Debug.Log("[ValidateAdPanels] 表示順：ランダム " + shuffleCount + " 枚／順番通り " + (panels.Length - shuffleCount) + " 枚");

        Debug.Log("[ValidateAdPanels] パネル " + panels.Length + " 枚（うち巡回対象 " + inside + " 枚）／" +
                  "ポスター実体 " + uniqueTextures.Count + " 種・推定 " + totalMb.ToString("F1") + " MB\n" +
                  "エラー " + errors + " ／ 警告 " + warnings);
    }

    static float EstimateMegabytes(Texture2D t)
    {
        // mipmap 込みでおよそ 4/3 倍。ASTC6x6 ≒ 0.36 byte/px、DXT1 = 0.5 byte/px。重い方(PC)で見積もる。
        float px = t.width * (float)t.height;
        return px * 0.5f * 1.333f / (1024f * 1024f);
    }

    static string Path(Transform t)
    {
        string s = t.name;
        var p = t.parent;
        while (p != null) { s = p.name + "/" + s; p = p.parent; }
        return s;
    }

    // ============================================================
    // アセットの用意
    // ============================================================
    static void EnsureFolders()
    {
        EnsureFolder("Assets/Mahjong", "AdPanels");
        EnsureFolder(RootFolder, "Materials");
        EnsureFolder(RootFolder, "Textures");
        EnsureFolder(RootFolder, "Posters");
        EnsurePostersReadme();
    }

    static void EnsureFolder(string parent, string name)
    {
        var full = parent + "/" + name;
        if (!AssetDatabase.IsValidFolder(full)) { AssetDatabase.CreateFolder(parent, name); }
    }

    static void EnsurePostersReadme()
    {
        var path = PosterFolder + "/README.txt";
        if (File.Exists(path)) { return; }
        var text =
            "広告ポスター画像はこのフォルダに入れてください。\r\n" +
            "\r\n" +
            "推奨の書き出し設定\r\n" +
            "  ・A4 比（210:297 ＝ 1:1.4142）\r\n" +
            "  ・長辺 1024px、縦横とも4の倍数（例：724 x 1024）\r\n" +
            "    → 4の倍数でないと圧縮が効かず、1枚で数MBの非圧縮テクスチャになります。\r\n" +
            "  ・PNG または JPG（透過は不要）\r\n" +
            "\r\n" +
            "入れたあとに Tools > Kemono Mahjong > Setup Ad Poster Textures を実行すると、\r\n" +
            "PC:DXT1 / Quest:ASTC6x6・mipmap ON・Clamp にまとめて揃います。\r\n" +
            "\r\n" +
            "同じ画像を複数のパネルで使い回してもメモリは1枚ぶんしか増えません。\r\n" +
            "パネルごとに全部違う絵にすると、その枚数ぶんそのまま VRAM に乗ります。\r\n";
        File.WriteAllText(path, text, System.Text.Encoding.UTF8);
        AssetDatabase.ImportAsset(path);
    }

    static Texture2D EnsurePlaceholderTexture()
    {
        var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(PlaceholderPath);
        if (existing != null) { return existing; }

        // 画像を入れるまでの仮絵。A4比・4の倍数（512 x 724）。
        const int w = 512;
        const int h = 724;
        var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        var bg = new Color32(28, 30, 38, 255);
        var border = new Color32(196, 168, 92, 255);
        var inner = new Color32(44, 47, 58, 255);

        var pixels = new Color32[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Color32 c = bg;
                bool onBorder = x < 10 || x >= w - 10 || y < 10 || y >= h - 10;
                bool onInnerLine = (x >= 22 && x < 24) || (x >= w - 24 && x < w - 22) ||
                                   (y >= 22 && y < 24) || (y >= h - 24 && y < h - 22);
                if (onBorder || onInnerLine) { c = border; }
                else if (x >= 24 && x < w - 24 && y >= 24 && y < h - 24)
                {
                    // 中央に斜めの縞を入れて「仮」だと一目で分かるようにする
                    c = (((x + y) / 24) % 2 == 0) ? inner : bg;
                }
                pixels[y * w + x] = c;
            }
        }
        tex.SetPixels32(pixels);
        tex.Apply();

        File.WriteAllBytes(PlaceholderPath, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(PlaceholderPath);
        ApplyPosterImportSettings(PlaceholderPath);
        Debug.Log("[BuildAdPanels] 仮のポスター画像を作りました: " + PlaceholderPath);
        return AssetDatabase.LoadAssetAtPath<Texture2D>(PlaceholderPath);
    }

    static Material EnsurePosterMaterial(Texture2D placeholder)
    {
        // ポスターは常に同じ明るさで読めてほしいので Unlit（ワールドのライトの影響を受けない）。
        // 切り替えのクロスフェードは専用シェーダーが _Time から自走する（CPU は切り替え時の1回だけ）。
        var shader = Shader.Find(PosterShaderName);
        if (shader == null)
        {
            Debug.LogError("[BuildAdPanels] シェーダー " + PosterShaderName + " が見つかりません。" +
                           "Assets/Mahjong/Shaders/AdPanelCrossfade.shader があるか確認してください。フェードなしで続行します。");
            shader = Shader.Find("Unlit/Texture");
        }

        var mat = AssetDatabase.LoadAssetAtPath<Material>(PosterMatPath);
        if (mat == null)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, PosterMatPath);
        }
        else if (mat.shader != shader)
        {
            // 既存マテリアルは全パネルが共有しているので、作り直さずシェーダーだけ差し替える。
            mat.shader = shader;
            Debug.Log("[BuildAdPanels] " + PosterMatPath + " のシェーダーを " + shader.name + " に変更しました。");
        }

        mat.mainTexture = placeholder;
        if (mat.HasProperty("_PrevTex")) { mat.SetTexture("_PrevTex", placeholder); }
        // 何も書き込まれていない状態（プレハブプレビュー等）ではフェード完了済みに見せる。
        if (mat.HasProperty("_FadeStart")) { mat.SetFloat("_FadeStart", -1000f); }
        if (mat.HasProperty("_FadeDuration")) { mat.SetFloat("_FadeDuration", DefaultFadeSeconds); }
        // GPUインスタンシングは触らない。過去に Standard Lite + instancing で実機のみ描画が
        // 壊れた事例があるため、広告のような静物では最初から切っておく。
        mat.enableInstancing = false;
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static Material EnsureFrameMaterial()
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(FrameMatPath);
        if (mat == null)
        {
            mat = new Material(Shader.Find("Unlit/Color"));
            AssetDatabase.CreateAsset(mat, FrameMatPath);
        }
        mat.color = FrameColor;
        mat.enableInstancing = false;
        EditorUtility.SetDirty(mat);
        return mat;
    }

    // ============================================================
    // U# の下ごしらえ（他のビルダーと同じ作法）
    // ============================================================
    static bool EnsureProgramAsset(string scriptPath, string assetPath, System.Type type)
    {
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
        if (script == null)
        {
            Debug.LogError("[BuildAdPanels] スクリプトが見つかりません: " + scriptPath);
            return false;
        }
        if (type != null && UdonSharpProgramAsset.GetProgramAssetForClass(type) != null) { return false; }
        if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(assetPath) != null) { return false; }

        var programAsset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
        programAsset.sourceCsScript = script;
        AssetDatabase.CreateAsset(programAsset, assetPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[BuildAdPanels] U#プログラムアセットを生成: " + assetPath);
        return true;
    }

    static UdonSharpBehaviour EnsureUSharpComponent(GameObject go, System.Type type)
    {
        var proxy = (UdonSharpBehaviour)go.GetComponent(type);
        if (proxy != null)
        {
            if (UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy) != null) { return proxy; }

            Debug.LogWarning("[BuildAdPanels] " + type.Name + " の backing UdonBehaviour が無いため作り直します。");
            Object.DestroyImmediate(proxy);
            foreach (var ub in go.GetComponents<UdonBehaviour>())
            {
                if (ub.programSource == null) { Object.DestroyImmediate(ub); }
            }
        }

        var added = (UdonSharpBehaviour)UdonSharpUndo.AddComponent(go, type);
        if (added != null && UdonSharpEditorUtility.GetBackingUdonBehaviour(added) == null)
        {
            Debug.LogError("[BuildAdPanels] " + type.Name + " の追加に失敗（プログラムアセットが未コンパイル？）。" +
                           "コンパイルが終わってからもう一度 Build Ad Panels を実行してください。");
            Object.DestroyImmediate(added);
            return null;
        }
        return added;
    }
}
#endif
