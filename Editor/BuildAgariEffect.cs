#if UNITY_EDITOR
// 和了演出（ロン／ツモの牌に出るエフェクト）の骨格生成と配線。
//   Tools > Kemono Mahjong > Build Agari Effects … 全卓＋メインメニュー脇に AgariEffect のリグを冪等生成
//   Tools > Kemono Mahjong > Wire Agari Effects  … U#コンポーネント追加＋参照割り当て＋卓/メニューへの逆参照
// どちらも2回実行しても安全（冪等）。見た目を変えたい場合は下の定数を変えて Build を
// 再実行する（手でシーンをいじった調整は Build 再実行で上書きされる）。
//
// Prefab Mode（KemonoMahjong.prefab を開いた状態）でも、通常シーンでも動く。
// 卓は Object.FindObjectsOfType<Mahjong>(true) で拾う（BuildResultScreen と同じ流儀）。
//
// エフェクト用テクスチャ（光柱・リング・閃光・粒・グロー）はアセットが無ければ
// この場で手続き的に生成して Assets/Mahjong/Textures/FX/ に保存する。
// 差し替えたいときは同じパスのPNGを上書きすればよい（Buildは既存を消さない）。
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using SuzuFactory.AozoraMahjong;
using VRC.Udon;

public static class BuildAgariEffect
{
    const string RootName = "AgariEffect";
    const string PreviewName = "AgariEffectPreview";
    const string ProgramAssetDir = "Assets/Mahjong/scene_UdonProgramSources";
    const string FxTextureDir = "Assets/Mahjong/Textures/FX";

    const string TexGlow = FxTextureDir + "/fx_glow.png";
    const string TexFlash = FxTextureDir + "/fx_flash.png";
    const string TexBeam = FxTextureDir + "/fx_beam.png";
    const string TexRing = FxTextureDir + "/fx_ring.png";
    const string TexSpark = FxTextureDir + "/fx_spark.png";

    // Canvas は 1000x1000 の仮想解像度。実寸 = 1000 * CanvasScale（m）。
    // 牌が約2.5cmなので、0.25m四方の枠に演出を収める。
    const float CanvasScale = 0.00025f;
    static readonly Vector2 CanvasSize = new Vector2(1000f, 1000f);

    const int NumSparks = 6;

    const string TablePrefabPath = "Assets/Mahjong/Prefabas/KemonoMahjong.prefab";
    const string ZapsName = "Zaps";
    const float ZapScale = 0.12f;
    const float ZapLifeCap = 1.0f;


    // コスメ ID と同順（0金雷 / 1黒雷 / 2蒼雷 / 3紅雷 / 4翠雷 / 5紫雷 / 6白雷）。
    // 並びを変えたら下の VariantNames / VariantMainColors も必ず同じ順に直すこと。
    static readonly string[] ZapPrefabPaths = new string[]
    {
        "Assets/Vefects/Zap VFX/VFX/Zap/Particles/VFX_Zap_03_Yellow.prefab",
        "Assets/Vefects/Zap VFX/VFX/Zap/Particles/VFX_Zap_07_Black.prefab",
        "Assets/Vefects/Zap VFX/VFX/Zap/Particles/VFX_Zap_02_Blue.prefab",
        "Assets/Vefects/Zap VFX/VFX/Zap/Particles/VFX_Zap_01_Red.prefab",
        "Assets/Vefects/Zap VFX/VFX/Zap/Particles/VFX_Zap_04_Green.prefab",
        "Assets/Vefects/Zap VFX/VFX/Zap/Particles/VFX_Zap_05_Purple.prefab",
        "Assets/Vefects/Zap VFX/VFX/Zap/Particles/VFX_Zap_06_White.prefab",
    };

    // メインメニューの装飾品画面は MahjongAgariEffect の variant* をそのまま読む。
    // C# の既定値を直してもシリアライズ済みのコンポーネントには入らないので、
    // 見た目の確定値は Wire のたびにここから上書きする（全卓＋試写リグに同じ値が入る）。
    // 色は各 Zap の LUT テクスチャ（T_VFX_Zap_LUT_0x）を実測した色相。
    static readonly string[] VariantNames = new string[]
    {
        "金雷", "黒雷", "蒼雷", "紅雷", "翠雷", "紫雷", "白雷",
    };

    static readonly string[] VariantDescs = new string[]
    {
        "既定の和了演出。黄金の雷が牌を撃ち抜く。",
        "漆黒の雷が牌を呑み込む。",
        "蒼い雷が牌を撃ち抜く。",
        "紅い雷が牌を焼き貫く。",
        "翠の雷が牌を弾けさせる。",
        "紫電が牌を撃ち抜く。",
        "白銀の雷が牌を貫く。",
    };

    // 英語版。VariantNames / VariantDescs と同じ長さ・同じ順にすること。
    // 表示言語が英語の人には MahjongAgariEffect がこちらを返す。
    static readonly string[] VariantNamesEn = new string[]
    {
        "Gold Bolt", "Black Bolt", "Azure Bolt", "Crimson Bolt",
        "Jade Bolt", "Violet Bolt", "Silver Bolt",
    };

    static readonly string[] VariantDescsEn = new string[]
    {
        "The default win effect. A golden bolt tears through the tile.",
        "A pitch-black bolt swallows the tile whole.",
        "An azure bolt punches through the tile.",
        "A crimson bolt sears its way through the tile.",
        "A jade bolt bursts the tile apart.",
        "A violet bolt strikes clean through the tile.",
        "A silver bolt runs the tile through.",
    };

    // 韓国語版。並びは上と同じ。名前は日本語の漢字を韓国語の音で読む（금뢰＝金雷）。
    static readonly string[] VariantNamesKo = new string[]
    {
        "금뢰", "흑뢰", "창뢰", "홍뢰", "취뢰", "자뢰", "백뢰"
    };

    static readonly string[] VariantDescsKo = new string[]
    {
        "기본 화료 연출. 황금 번개가 패를 꿰뚫는다.",
        "칠흑의 번개가 패를 집어삼킨다.",
        "푸른 번개가 패를 꿰뚫는다.",
        "붉은 번개가 패를 태워 관통한다.",
        "비취색 번개가 패를 터뜨린다.",
        "보랏빛 번개가 패를 꿰뚫는다.",
        "은빛 번개가 패를 관통한다."
    };

    // 中国語版（簡体字・繁体字）。並びは上と同じ。名前は漢字をそのまま字形だけ合わせる。
    static readonly string[] VariantNamesZhs = new string[]
    {
        "金雷", "黑雷", "苍雷", "红雷", "翠雷", "紫雷", "白雷"
    };

    static readonly string[] VariantDescsZhs = new string[]
    {
        "默认的和了演出。金色雷电击穿牌面。",
        "漆黑的雷电将牌吞没。",
        "苍蓝的雷电击穿牌面。",
        "赤红的雷电灼穿牌面。",
        "翠绿的雷电让牌迸裂。",
        "紫电击穿牌面。",
        "白银的雷电贯穿牌面。"
    };

    static readonly string[] VariantNamesZht = new string[]
    {
        "金雷", "黑雷", "蒼雷", "紅雷", "翠雷", "紫雷", "白雷"
    };

    static readonly string[] VariantDescsZht = new string[]
    {
        "預設的和了演出。金色雷電擊穿牌面。",
        "漆黑的雷電將牌吞沒。",
        "蒼藍的雷電擊穿牌面。",
        "赤紅的雷電灼穿牌面。",
        "翠綠的雷電讓牌迸裂。",
        "紫電擊穿牌面。",
        "白銀的雷電貫穿牌面。"
    };

    static readonly Color[] VariantMainColors = new Color[]
    {
        new Color(1.00f, 0.62f, 0.12f, 1.0f),
        new Color(0.22f, 0.20f, 0.26f, 1.0f),
        new Color(0.18f, 0.52f, 1.00f, 1.0f),
        new Color(1.00f, 0.20f, 0.24f, 1.0f),
        new Color(0.20f, 1.00f, 0.38f, 1.0f),
        new Color(0.42f, 0.24f, 1.00f, 1.0f),
        new Color(0.92f, 0.96f, 1.00f, 1.0f),
    };

    static readonly Color[] VariantSubColors = new Color[]
    {
        new Color(1.00f, 0.88f, 0.55f, 1.0f),
        new Color(0.62f, 0.58f, 0.70f, 1.0f),
        new Color(0.72f, 0.90f, 1.00f, 1.0f),
        new Color(1.00f, 0.72f, 0.72f, 1.0f),
        new Color(0.78f, 1.00f, 0.85f, 1.0f),
        new Color(0.80f, 0.74f, 1.00f, 1.0f),
        new Color(1.00f, 1.00f, 1.00f, 1.0f),
    };


    // 各パーツの基準サイズ（Canvas px）。実行時はスクリプトがスケールで動かす。
    static readonly Vector2 GlowSize = new Vector2(600f, 600f);
    static readonly Vector2 FlashSize = new Vector2(460f, 460f);
    static readonly Vector2 PillarSize = new Vector2(120f, 900f);
    static readonly Vector2 RingSize = new Vector2(560f, 560f);
    static readonly Vector2 SparkSize = new Vector2(64f, 64f);

    // ============================================================
    // Build：骨格生成
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Build Agari Effects")]
    public static void Build()
    {
        EnsureFxTextures();

        BuildPrefabRig();

        var tables = Object.FindObjectsOfType<Mahjong>(true);
        System.Array.Sort(tables, (a, b) => string.CompareOrdinal(GetPath(a.transform), GetPath(b.transform)));
        if (tables.Length == 0)
        {
            Debug.LogError("[BuildAgariEffect] Mahjong（卓）が見つかりません。シーンを開くか、卓のPrefab Modeで実行してください。");
            return;
        }

        foreach (var table in tables)
        {
            BuildOne(table.transform, RootName);
        }

        // メインメニュー脇の試写用（卓側はカリングで落ちるため、装飾品画面はこちらを使う）。
        int previews = BuildPreview();

        MarkDirty(tables[0].gameObject);
        Debug.Log("[BuildAgariEffect] 完了：" + tables.Length + " 卓 + 試写用 " + previews
            + " 個に和了演出リグを生成/更新。次に Wire Agari Effects を実行してください。");
    }

static int BuildPrefabRig()
    {
        var stage = PrefabStageUtility.OpenPrefab(TablePrefabPath);
        if (stage == null || stage.prefabContentsRoot == null)
        {
            Debug.LogError("[BuildAgariEffect] Prefab を開けません: " + TablePrefabPath);
            StageUtility.GoToMainStage();
            return 0;
        }

        if (stage.prefabContentsRoot.GetComponent<Mahjong>() == null)
        {
            Debug.LogError("[BuildAgariEffect] Prefab のルートに Mahjong がありません: " + TablePrefabPath);
            StageUtility.GoToMainStage();
            return 0;
        }

        BuildOne(stage.prefabContentsRoot.transform, RootName);
        SavePrefabStage(stage);
        return 1;
    }

    static void SavePrefabStage(PrefabStage stage)
    {
        if (stage == null || stage.prefabContentsRoot == null)
        {
            Debug.LogError("[BuildAgariEffect] Prefab Stage が無効なため保存できません。");
            StageUtility.GoToMainStage();
            return;
        }

        EditorSceneManager.MarkSceneDirty(stage.scene);
        bool saved = EditorApplication.ExecuteMenuItem("File/Save");
        if (!saved || stage.scene.isDirty)
        {
            Debug.LogError("[BuildAgariEffect] Prefab の保存に失敗しました: " + stage.assetPath);
        }

        AssetDatabase.SaveAssets();
        StageUtility.GoToMainStage();
    }


    // メインメニューの隣に置く試写用リグ。MainMenu の Canvas 配下に入れると
    // Canvas のスケール（1e-3級）を継承して見えなくなるため、MainMenu の
    // 「兄弟」として置く。位置は新規作成時だけ決め、以後は動かさない。
    static int BuildPreview()
    {
        var mainMenu = GameObject.Find("MainMenu");
        if (mainMenu == null)
        {
            Debug.LogWarning("[BuildAgariEffect] シーンに MainMenu が無いため試写用リグは作りませんでした（卓側の演出は生成済み）。");
            return 0;
        }

        var parent = mainMenu.transform.parent;
        var existing = parent != null ? parent.Find(PreviewName) : FindSceneRoot(mainMenu, PreviewName);
        bool isNew = existing == null;

        Transform root;
        if (isNew)
        {
            var go = new GameObject(PreviewName);
            Undo.RegisterCreatedObjectUndo(go, "Create " + PreviewName);
            go.transform.SetParent(parent, false);
            root = go.transform;

            // メニューの手前・少し下。見づらければ手で動かしてよい（Build再実行では動かさない）。
            var menuT = mainMenu.transform;
            root.position = menuT.position - menuT.forward * 0.55f - menuT.up * 0.35f;
            root.rotation = Quaternion.identity;
            root.localScale = Vector3.one;
        }
        else
        {
            root = existing;
        }

        // 親のスケールを打ち消して実寸を保つ（MainMenu 直下に手で移動された場合の保険）。
        var lossy = root.parent != null ? root.parent.lossyScale : Vector3.one;
        if (Mathf.Abs(lossy.x) > 1e-6f && Mathf.Abs(lossy.y) > 1e-6f && Mathf.Abs(lossy.z) > 1e-6f)
        {
            root.localScale = new Vector3(1f / lossy.x, 1f / lossy.y, 1f / lossy.z);
        }

        BuildRig(root);
        return 1;
    }

    static Transform FindSceneRoot(GameObject sibling, string name)
    {
        foreach (var go in sibling.scene.GetRootGameObjects())
        {
            if (go.name == name) { return go.transform; }
        }
        return null;
    }

    static void BuildOne(Transform tableRoot, string rootName)
    {
        var root = EnsureChild(tableRoot, rootName);
        // スクリプトが載るルートは常時アクティブを厳守（非アクティブなU#は外から叩けない）。
        // 表示ON/OFFは子の FxRoot でのみ行う。
        root.transform.localPosition = Vector3.zero;
        root.transform.localRotation = Quaternion.identity;
        root.transform.localScale = Vector3.one;
        root.SetActive(true);

        BuildRig(root.transform);
    }

    // AgariEffect ルート配下のリグ本体（卓・試写で共通）。
    static void BuildRig(Transform root)
    {
        root.gameObject.SetActive(true);

        // SE用（未設定なら鳴らないだけ）。3D音として卓の位置から鳴らす。
        var audio = root.GetComponent<AudioSource>();
        if (audio == null) { audio = Undo.AddComponent<AudioSource>(root.gameObject); }
        audio.playOnAwake = false;
        audio.loop = false;
        audio.spatialBlend = 1f;
        audio.dopplerLevel = 0f;
        audio.minDistance = 1.5f;
        audio.maxDistance = 14f;
        audio.rolloffMode = AudioRolloffMode.Linear;

        var fxRoot = EnsureChild(root, "FxRoot");
        fxRoot.transform.localPosition = Vector3.zero;
        fxRoot.transform.localRotation = Quaternion.identity;
        fxRoot.transform.localScale = Vector3.one;

        // ---- 縦レイヤー（プレイヤーの方を向く）----
        var billboard = EnsureChild(fxRoot.transform, "Billboard");
        billboard.transform.localPosition = Vector3.zero;
        billboard.transform.localRotation = Quaternion.identity;
        billboard.transform.localScale = Vector3.one;
        var billboardCanvas = EnsureCanvas(billboard.transform);

        // 描画順＝ヒエラルキー順。奥から グロー → 光柱 → 粒 → 閃光。
        EnsureFxImage(billboardCanvas.transform, "Glow", TexGlow, GlowSize, new Vector2(0.5f, 0.5f), Vector2.zero);
        EnsureFxImage(billboardCanvas.transform, "Pillar", TexBeam, PillarSize, new Vector2(0.5f, 0f), Vector2.zero);
        for (int i = 0; i < NumSparks; ++i)
        {
            EnsureFxImage(billboardCanvas.transform, "Spark" + i, TexSpark, SparkSize, new Vector2(0.5f, 0.5f), Vector2.zero);
        }
        EnsureFxImage(billboardCanvas.transform, "Flash", TexFlash, FlashSize, new Vector2(0.5f, 0.5f), Vector2.zero);

        var keepBillboard = new string[3 + NumSparks];
        keepBillboard[0] = "Glow";
        keepBillboard[1] = "Pillar";
        keepBillboard[2] = "Flash";
        for (int i = 0; i < NumSparks; ++i) { keepBillboard[3 + i] = "Spark" + i; }
        CleanupChildren(billboardCanvas.transform, keepBillboard);

        // ---- 卓面レイヤー（寝かせる。向きは実行時に Play が決める）----
        var ground = EnsureChild(fxRoot.transform, "Ground");
        ground.transform.localPosition = Vector3.zero;
        // 編集中の見え方合わせ（実行時は Play が絶対姿勢で置き直す）。+Zが真上＝上から見える。
        ground.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
        ground.transform.localScale = Vector3.one;
        var groundCanvas = EnsureCanvas(ground.transform);

        EnsureFxImage(groundCanvas.transform, "Ring", TexRing, RingSize, new Vector2(0.5f, 0.5f), Vector2.zero);
        CleanupChildren(groundCanvas.transform, new string[] { "Ring" });

        EnsureZapVariants(fxRoot.transform);
        CleanupChildren(fxRoot.transform, new string[] { "Billboard", "Ground", ZapsName });

        // 編集中に見えっぱなしにならないよう畳んでおく（実行時も Start で畳まれる）。
        fxRoot.SetActive(false);
    }

    static GameObject EnsureCanvas(Transform parent)
    {
        var canvasGo = EnsureUiChild(parent, "Canvas");

        var canvas = canvasGo.GetComponent<Canvas>();
        if (canvas == null) { canvas = canvasGo.AddComponent<Canvas>(); }
        canvas.renderMode = RenderMode.WorldSpace;

        var scaler = canvasGo.GetComponent<CanvasScaler>();
        if (scaler == null) { scaler = canvasGo.AddComponent<CanvasScaler>(); }
        scaler.dynamicPixelsPerUnit = 2f;

        // 操作しない表示専用。GraphicRaycaster は付けない（ボタン操作を遮らないため）。
        var raycaster = canvasGo.GetComponent<GraphicRaycaster>();
        if (raycaster != null) { Object.DestroyImmediate(raycaster); }

        var rt = canvasGo.GetComponent<RectTransform>();
        rt.localPosition = Vector3.zero;
        rt.localRotation = Quaternion.identity;
        rt.localScale = new Vector3(CanvasScale, CanvasScale, CanvasScale);
        rt.sizeDelta = CanvasSize;

        return canvasGo;
    }

    static RawImage EnsureFxImage(Transform parent, string name, string texturePath, Vector2 size, Vector2 pivot, Vector2 pos)
    {
        var go = EnsureUiChild(parent, name);

        var raw = go.GetComponent<RawImage>();
        if (raw == null) { raw = go.AddComponent<RawImage>(); }
        raw.raycastTarget = false;
        raw.texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
        raw.color = new Color(1f, 1f, 1f, 0f); // 初期は透明（実行時にスクリプトが塗る）

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        rt.localScale = Vector3.one;
        rt.localRotation = Quaternion.identity;

        return raw;
    }

    // ============================================================
    // Wire：U#コンポーネント追加＋参照割り当て
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Wire Agari Effects")]
    public static void Wire()
    {
        if (EnsureProgramAsset(
            "Assets/Mahjong/Sources/MahjongAgariEffect.cs",
            ProgramAssetDir + "/MahjongAgariEffect Udon C# Program Asset.asset"))
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[WireAgariEffect] U#プログラムアセットを生成しました。コンパイル完了後、もう一度 Wire Agari Effects を実行してください。");
            return;
        }

        var tables = Object.FindObjectsOfType<Mahjong>(true);
        System.Array.Sort(tables, (a, b) => string.CompareOrdinal(GetPath(a.transform), GetPath(b.transform)));
        if (tables.Length == 0)
        {
            Debug.LogError("[WireAgariEffect] Mahjong（卓）が見つかりません。");
            return;
        }

        int wired = 0;
        foreach (var table in tables)
        {
            var rootT = table.transform.Find(RootName);
            if (rootT == null)
            {
                Debug.LogError("[WireAgariEffect] " + GetPath(table.transform) + " に " + RootName
                    + " がありません。先に Build Agari Effects を実行してください。");
                continue;
            }

            var comp = WireRig(rootT);
            if (comp == null) { return; }

            // 卓本体 → 和了演出の参照（未割当なら演出なし＝従来動作）。
            if (table.agariEffect != comp)
            {
                table.agariEffect = comp;
                UdonSharpEditorUtility.CopyProxyToUdon(table);
                EditorUtility.SetDirty(table);
            }
            ++wired;
        }

        // 試写用リグ → MainMenuController.fxCatalog
        int previewWired = 0;
        var preview = FindPreviewRig();
        if (preview != null)
        {
            var comp = WireRig(preview);
            if (comp != null)
            {
                var controller = Object.FindObjectOfType<MainMenuController>(true);
                if (controller != null && controller.fxCatalog != comp)
                {
                    controller.fxCatalog = comp;
                    UdonSharpEditorUtility.CopyProxyToUdon(controller);
                    EditorUtility.SetDirty(controller);
                }
                previewWired = 1;
            }
        }
        else
        {
            Debug.LogWarning("[WireAgariEffect] 試写用リグ（" + PreviewName + "）が見つかりません。装飾品画面のカード名・試写は出ません。");
        }

        MarkDirty(tables[0].gameObject);
        Debug.Log("[WireAgariEffect] 完了：" + wired + "/" + tables.Length + " 卓 + 試写用 " + previewWired + " 個を配線。");
    }

    // ============================================================
    // Validate：実際に動く配線になっているかを点検する。
    //   卓の AgariEffect は Prefab の中にあるため、**Wire のあとに卓Prefabを保存すると
    //   インスタンス側のプロキシ値が巻き戻る**ことがある。そのまま Play に入ると
    //   UdonSharp が「プロキシ→Udon」へコピーするので、古い値で上書きされて配線が消える。
    //   （実例：白雷を選んだのに Zap が6個しか無い状態に戻り、廃止した光柱演出が出た）
    //   手順は必ず「Prefabを先に直す → 最後に Wire → Validate」の順。
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Validate Agari Effects")]
    public static void Validate()
    {
        var comps = Object.FindObjectsOfType<MahjongAgariEffect>(true);
        if (comps.Length == 0)
        {
            Debug.LogWarning("[ValidateAgariEffect] MahjongAgariEffect がシーンにありません。");
            return;
        }

        int want = ZapPrefabPaths.Length;
        int ng = 0;
        foreach (var c in comps)
        {
            int proxy = c.variantExtraRoots != null ? c.variantExtraRoots.Length : 0;
            int filled = 0;
            if (c.variantExtraRoots != null)
            {
                for (int i = 0; i < c.variantExtraRoots.Length; ++i)
                {
                    if (c.variantExtraRoots[i] != null) { ++filled; }
                }
            }

            int udon = -1;
            var ub = c.GetComponent<UdonBehaviour>();
            if (ub != null && ub.publicVariables != null)
            {
                object v;
                if (ub.publicVariables.TryGetVariableValue("variantExtraRoots", out v))
                {
                    var arr = v as GameObject[];
                    udon = arr != null ? arr.Length : -1;
                }
            }

            int names = c.variantNames != null ? c.variantNames.Length : 0;
            if (proxy == want && filled == want && udon == want && names == want) { continue; }

            ++ng;
            Debug.LogError("[ValidateAgariEffect] " + GetPath(c.transform) + " が不整合：Zap想定 " + want
                + " / proxy=" + proxy + "（実体 " + filled + "） udon=" + udon + " names=" + names
                + " → Wire Agari Effects を実行してください。", c.gameObject);
        }

        Debug.Log("[ValidateAgariEffect] " + (comps.Length - ng) + "/" + comps.Length
            + " 個が正常（Zap " + want + " 色・proxyとUdonの両方を照合）。");
    }

    static Transform FindPreviewRig()
    {
        var go = GameObject.Find(PreviewName);
        return go != null ? go.transform : null;
    }

    static MahjongAgariEffect WireRig(Transform rootT)
    {
        var comp = (MahjongAgariEffect)EnsureUSharpComponent(rootT.gameObject, typeof(MahjongAgariEffect));
        if (comp == null)
        {
            Debug.LogError("[WireAgariEffect] MahjongAgariEffect の追加に失敗（プログラムアセット未コンパイル？）。コンパイル後に再実行してください。");
            return null;
        }

        var fxRoot = rootT.Find("FxRoot");
        if (fxRoot == null)
        {
            Debug.LogError("[WireAgariEffect] " + GetPath(rootT) + " に FxRoot がありません。Build をやり直してください。");
            return null;
        }

        var billboardCanvas = fxRoot.Find("Billboard/Canvas");
        var groundCanvas = fxRoot.Find("Ground/Canvas");

        comp.fxRoot = fxRoot.gameObject;
        comp.billboardRoot = fxRoot.Find("Billboard");
        comp.groundRoot = fxRoot.Find("Ground");

        comp.glowImage = FindRaw(billboardCanvas, "Glow");
        comp.glowRect = FindRect(billboardCanvas, "Glow");
        comp.pillarImage = FindRaw(billboardCanvas, "Pillar");
        comp.pillarRect = FindRect(billboardCanvas, "Pillar");
        comp.flashImage = FindRaw(billboardCanvas, "Flash");
        comp.flashRect = FindRect(billboardCanvas, "Flash");
        comp.ringImage = FindRaw(groundCanvas, "Ring");
        comp.ringRect = FindRect(groundCanvas, "Ring");

        var sparkImages = new RawImage[NumSparks];
        var sparkRects = new RectTransform[NumSparks];
        for (int i = 0; i < NumSparks; ++i)
        {
            sparkImages[i] = FindRaw(billboardCanvas, "Spark" + i);
            sparkRects[i] = FindRect(billboardCanvas, "Spark" + i);
        }
        comp.sparkImages = sparkImages;
        comp.sparkRects = sparkRects;

        int zapCount = ZapPrefabPaths.Length;
        var extras = new GameObject[zapCount];
        var zaps = fxRoot.Find(ZapsName);
        if (zaps != null)
        {
            for (int i = 0; i < zapCount; ++i)
            {
                var zt = zaps.Find("Zap" + i);
                extras[i] = zt != null ? zt.gameObject : null;
            }
        }
        comp.variantExtraRoots = extras;

        // 装飾品カードの表示（名前・説明・色見本）も Zap の実物と 1:1 に揃える。
        // U# はシリアライズ済みの値を優先するので、C# の既定値任せにせず毎回上書きする。
        if (VariantNames.Length != zapCount || VariantDescs.Length != zapCount
            || VariantMainColors.Length != zapCount || VariantSubColors.Length != zapCount)
        {
            Debug.LogError("[BuildAgariEffect] コスメ表示テーブルの数が Zap の数(" + zapCount
                + ")と合っていません。ZapPrefabPaths と並びを揃えてください。");
        }
        comp.variantNames = (string[])VariantNames.Clone();
        comp.variantDescs = (string[])VariantDescs.Clone();
        comp.variantNamesEn = (string[])VariantNamesEn.Clone();
        comp.variantDescsEn = (string[])VariantDescsEn.Clone();
        comp.variantNamesKo = (string[])VariantNamesKo.Clone();
        comp.variantDescsKo = (string[])VariantDescsKo.Clone();
        comp.variantNamesZhs = (string[])VariantNamesZhs.Clone();
        comp.variantDescsZhs = (string[])VariantDescsZhs.Clone();
        comp.variantNamesZht = (string[])VariantNamesZht.Clone();
        comp.variantDescsZht = (string[])VariantDescsZht.Clone();
        comp.variantMainColors = (Color[])VariantMainColors.Clone();
        comp.variantSubColors = (Color[])VariantSubColors.Clone();

        
comp.seSource = rootT.GetComponent<AudioSource>();
        // 音量はクリップの実測ラウドネス（ffmpeg ebur128）から逆算した値。
        //   爆発 -13.5 LUFS → 0.085 で約 -35 LUFS 相当
        //   ドン -21.3 LUFS → 0.13  で約 -39 LUFS 相当
        comp.seVolume = KemonoAudioNormalizer.SeScale(comp.defaultSound, 0.0885f);
        comp.handOpenVolume = KemonoAudioNormalizer.SeScale(comp.handOpenSound, 0.0885f);
        comp.duration = 2.0f;
        comp.leadSeconds = 1.5f;

        comp.fadeOutSeconds = 0.25f;


        UdonSharpEditorUtility.CopyProxyToUdon(comp);
        EditorUtility.SetDirty(comp);
        return comp;
    }

    static RawImage FindRaw(Transform parent, string name)
    {
        if (parent == null) { return null; }
        var t = parent.Find(name);
        return t != null ? t.GetComponent<RawImage>() : null;
    }

    static RectTransform FindRect(Transform parent, string name)
    {
        if (parent == null) { return null; }
        var t = parent.Find(name);
        return t != null ? t as RectTransform : null;
    }

    // ============================================================
    // エフェクト用テクスチャの手続き生成
    //  ・アルファのみのソフトな形。色はスクリプトが RawImage.color で乗せる
    //    ＝1枚のテクスチャで全コスメを賄える（Quest向けにメモリ最小）。
    //  ・既に同じパスにファイルがあれば触らない（差し替え素材を尊重する）。
    // ============================================================
    public static void EnsureFxTextures()
    {
        EnsureFolder(FxTextureDir);

        bool created = false;
        created |= WriteTextureIfMissing(TexGlow, 256, 256, MakeGlow(256));
        created |= WriteTextureIfMissing(TexFlash, 256, 256, MakeFlash(256));
        created |= WriteTextureIfMissing(TexBeam, 64, 512, MakeBeam(64, 512));
        created |= WriteTextureIfMissing(TexRing, 256, 256, MakeRing(256));
        created |= WriteTextureIfMissing(TexSpark, 64, 64, MakeSpark(64));

        if (created)
        {
            AssetDatabase.Refresh();
            Debug.Log("[BuildAgariEffect] エフェクト用テクスチャを " + FxTextureDir + " に生成しました。差し替えたい場合は同名PNGで上書きしてください。");
        }
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

    static bool WriteTextureIfMissing(string path, int width, int height, Color[] pixels)
    {
        if (AssetDatabase.LoadAssetAtPath<Texture2D>(path) != null) { return false; }

        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
        tex.SetPixels(pixels);
        tex.Apply();

        var png = tex.EncodeToPNG();
        Object.DestroyImmediate(tex);
        System.IO.File.WriteAllBytes(path, png);
        AssetDatabase.ImportAsset(path);

        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer != null)
        {
            importer.textureType = TextureImporterType.Default;
            importer.alphaIsTransparency = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.mipmapEnabled = true;
            importer.SaveAndReimport();
        }
        return true;
    }

    // 中心が明るく外へ滑らかに消える円（背後の光）。
    static Color[] MakeGlow(int size)
    {
        var px = new Color[size * size];
        float half = size * 0.5f;
        for (int y = 0; y < size; ++y)
        {
            for (int x = 0; x < size; ++x)
            {
                float dx = (x + 0.5f - half) / half;
                float dy = (y + 0.5f - half) / half;
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Clamp01(1f - r);
                a = a * a * a;                       // 外へ強く落とす
                px[y * size + x] = new Color(1f, 1f, 1f, a);
            }
        }
        return px;
    }

    // 中心の白核＋十字の光条（着弾の閃光）。
    static Color[] MakeFlash(int size)
    {
        var px = new Color[size * size];
        float half = size * 0.5f;
        for (int y = 0; y < size; ++y)
        {
            for (int x = 0; x < size; ++x)
            {
                float dx = (x + 0.5f - half) / half;
                float dy = (y + 0.5f - half) / half;
                float r = Mathf.Sqrt(dx * dx + dy * dy);

                float core = Mathf.Clamp01(1f - r * 2.6f);
                core = core * core;

                // 十字の光条（縦・横）。細い帯を距離で減衰させる。
                float ax = Mathf.Abs(dx);
                float ay = Mathf.Abs(dy);
                float spikeH = Mathf.Clamp01(1f - ay * 26f) * Mathf.Clamp01(1f - ax);
                float spikeV = Mathf.Clamp01(1f - ax * 26f) * Mathf.Clamp01(1f - ay);
                float spike = Mathf.Max(spikeH, spikeV);
                spike = spike * spike * 0.85f;

                float a = Mathf.Clamp01(Mathf.Max(core, spike));
                px[y * size + x] = new Color(1f, 1f, 1f, a);
            }
        }
        return px;
    }

    // 下端が濃く上端へ薄れる縦の光帯（光柱）。左右はガウシアンで丸める。
    static Color[] MakeBeam(int width, int height)
    {
        var px = new Color[width * height];
        float halfW = width * 0.5f;
        for (int y = 0; y < height; ++y)
        {
            float v = (float)y / (height - 1);          // 0=下端 1=上端
            float vFade = Mathf.Clamp01(1f - v);
            vFade = vFade * vFade;                       // 上へ向かって細く消える
            // 下端も少しだけ絞ってラインの端を目立たせない
            float bottom = Mathf.Clamp01(v / 0.06f);

            for (int x = 0; x < width; ++x)
            {
                float dx = (x + 0.5f - halfW) / halfW;   // -1..1
                float hFall = Mathf.Exp(-(dx * dx) * 7.0f);
                float a = Mathf.Clamp01(hFall * vFade * Mathf.Lerp(0.35f, 1f, bottom));
                px[y * width + x] = new Color(1f, 1f, 1f, a);
            }
        }
        return px;
    }

    // 細い円環（卓面の衝撃波）。内側にわずかな塗りを残して波らしくする。
    static Color[] MakeRing(int size)
    {
        var px = new Color[size * size];
        float half = size * 0.5f;
        for (int y = 0; y < size; ++y)
        {
            for (int x = 0; x < size; ++x)
            {
                float dx = (x + 0.5f - half) / half;
                float dy = (y + 0.5f - half) / half;
                float r = Mathf.Sqrt(dx * dx + dy * dy);

                float d = (r - 0.78f) / 0.085f;
                float ring = Mathf.Exp(-d * d);
                float inner = Mathf.Clamp01(0.78f - r) * 0.18f;

                float a = Mathf.Clamp01(ring + inner);
                px[y * size + x] = new Color(1f, 1f, 1f, a);
            }
        }
        return px;
    }

    // 小さな粒。中心が硬めの円。
    static Color[] MakeSpark(int size)
    {
        var px = new Color[size * size];
        float half = size * 0.5f;
        for (int y = 0; y < size; ++y)
        {
            for (int x = 0; x < size; ++x)
            {
                float dx = (x + 0.5f - half) / half;
                float dy = (y + 0.5f - half) / half;
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Clamp01(1f - r);
                a = a * a;
                px[y * size + x] = new Color(1f, 1f, 1f, a);
            }
        }
        return px;
    }

    // ============================================================
    // 共通ヘルパ（BuildResultScreen と同等の実装）
    // ============================================================
    static bool EnsureProgramAsset(string scriptPath, string assetPath)
    {
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
        if (script == null)
        {
            Debug.LogError("[WireAgariEffect] スクリプトが見つかりません: " + scriptPath);
            return false;
        }
        var type = script.GetClass();
        if (type != null && UdonSharpProgramAsset.GetProgramAssetForClass(type) != null) { return false; }
        if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(assetPath) != null) { return false; }

        var programAsset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
        programAsset.sourceCsScript = script;
        AssetDatabase.CreateAsset(programAsset, assetPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[WireAgariEffect] U#プログラムアセットを生成: " + assetPath);
        return true;
    }

    static UdonSharpBehaviour EnsureUSharpComponent(GameObject go, System.Type type)
    {
        var proxy = (UdonSharpBehaviour)go.GetComponent(type);
        if (proxy != null)
        {
            if (UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy) != null) { return proxy; }

            Debug.LogWarning("[WireAgariEffect] " + type.Name + " の backing UdonBehaviour が無いため作り直します。");
            Object.DestroyImmediate(proxy);
            foreach (var ub in go.GetComponents<UdonBehaviour>())
            {
                if (ub.programSource == null) { Object.DestroyImmediate(ub); }
            }
        }

        var added = (UdonSharpBehaviour)UdonSharpUndo.AddComponent(go, type);
        if (added != null && UdonSharpEditorUtility.GetBackingUdonBehaviour(added) == null)
        {
            Debug.LogError("[WireAgariEffect] " + type.Name + " の追加に失敗（プログラムアセット未コンパイル？）。");
            Object.DestroyImmediate(added);
            return null;
        }
        return added;
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

static void EnsureZapVariants(Transform fxRoot)
    {
        var zaps = EnsureChild(fxRoot, ZapsName);
        zaps.transform.localPosition = Vector3.zero;
        zaps.transform.localRotation = Quaternion.identity;
        zaps.transform.localScale = Vector3.one;

        int n = ZapPrefabPaths.Length;
        var keep = new string[n];
        for (int i = 0; i < n; ++i)
        {
            keep[i] = "Zap" + i;
            EnsureZapInstance(zaps.transform, keep[i], ZapPrefabPaths[i]);
        }
        CleanupChildren(zaps.transform, keep);
    }

    static void EnsureZapInstance(Transform parent, string name, string assetPath)
    {
        var src = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
        if (src == null)
        {
            Debug.LogError("[BuildAgariEffect] Zap Prefab が見つかりません: " + assetPath);
            return;
        }

        var existing = parent.Find(name);
        GameObject go;
        if (existing != null)
        {
            var srcOf = PrefabUtility.GetCorrespondingObjectFromSource(existing.gameObject);
            if (srcOf == src)
            {
                go = existing.gameObject;
            }
            else
            {
                Undo.DestroyObjectImmediate(existing.gameObject);
                go = (GameObject)PrefabUtility.InstantiatePrefab(src, parent);
                Undo.RegisterCreatedObjectUndo(go, "Create " + name);
                go.name = name;
            }
        }
        else
        {
            go = (GameObject)PrefabUtility.InstantiatePrefab(src, parent);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            go.name = name;
        }

        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one * ZapScale;
        PrepareZap(go);
        go.SetActive(false);
    }

static void PrepareZap(GameObject go)
    {
        foreach (var t in go.GetComponentsInChildren<Transform>(true))
        {
            if (t.name == "Light Spawn" || t.name == "Point Light")
            {
                t.gameObject.SetActive(false);
            }
        }

        foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
        {
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            if (main.duration > ZapLifeCap)
            {
                main.duration = ZapLifeCap;
            }

            var life = main.startLifetime;
            if (life.mode == ParticleSystemCurveMode.TwoConstants)
            {
                life.constantMin = Mathf.Min(life.constantMin, ZapLifeCap);
                life.constantMax = Mathf.Min(life.constantMax, ZapLifeCap);
            }
            else if (life.mode == ParticleSystemCurveMode.Constant)
            {
                life.constant = Mathf.Min(life.constant, ZapLifeCap);
            }
            main.startLifetime = life;

            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }
    }


    static GameObject EnsureUiChild(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null)
        {
            if (t.GetComponent<RectTransform>() == null)
            {
                // Transform で作られていた場合は RectTransform へ作り直す。
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

    // 期待する子だけ残す（レイアウト変更後の遺物を掃除して冪等に）。
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
