#if UNITY_EDITOR
// 役満の和了牌の着弾演出（雀魂スタイル）の骨格生成と配線。
//
//   Tools > Kemono Mahjong > Build Yakuman Impact (Prefab)  … 卓Prefabへリグを冪等生成
//   Tools > Kemono Mahjong > Wire Yakuman Impact (Prefab)   … Prefab側の参照割り当て
//   Tools > Kemono Mahjong > Wire Yakuman Impact            … シーン8卓（Udon側に入るのはこちら）
//   Tools > Kemono Mahjong > Validate Yakuman Impact        … 参照欠損の確認
//
// 通常の和了演出（BuildAgariEffect＝光柱が立つタイプ）とは別物。あちらは触らない。
//
// リグは卓Prefab（KemonoMahjong.prefab）に1本だけ置き、8卓が継承する。
// シーン側で Build を回さないのは、卓ごとに「子を足した」Prefab差分が乗って
// 継承が切れるため（打ち上げリグで同じ罠を踏んだ）。
//
// テクスチャは無ければ手続き的に生成して Assets/Mahjong/Textures/FX/ に保存する。
// リング・粒・光条は既存の和了演出のものを流用し、円盤とサンバーストだけ新規。
// 差し替えたいときは同じパスのPNGを上書きすればよい（Buildは既存を消さない）。
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using SuzuFactory.AozoraMahjong;
using VRC.Udon;

public static class BuildYakumanImpact
{
    const string TablePrefabPath = "Assets/Mahjong/Prefabas/KemonoMahjong.prefab";
    const string RootName = "YakumanImpact";
    const string ProgramAssetDir = "Assets/Mahjong/scene_UdonProgramSources";
    const string ScriptPath = "Assets/Mahjong/Sources/MahjongYakumanImpact.cs";
    const string ProgramAssetPath = ProgramAssetDir + "/MahjongYakumanImpact Udon C# Program Asset.asset";

    const string FxTextureDir = "Assets/Mahjong/Textures/FX";

    // 既存の和了演出と共用（無ければ BuildAgariEffect 側が作る／ここでも作る）。
    const string TexRing = FxTextureDir + "/fx_ring.png";
    const string TexSpark = FxTextureDir + "/fx_spark.png";
    const string TexBeam = FxTextureDir + "/fx_beam.png";
    const string TexFlash = FxTextureDir + "/fx_flash.png";

    // この演出だけの新規。
    const string TexDisc = FxTextureDir + "/fx_disc.png";
    const string TexSun = FxTextureDir + "/fx_sun.png";

    // Canvas は 1000x1000 の仮想解像度。実寸 = 1000 * CanvasScale（m）＝0.25m。
    // 既存の和了演出と同じ縮尺にして、両者の大きさ感を揃えている。
    const float CanvasScale = 0.00025f;
    static readonly Vector2 CanvasSize = new Vector2(1000f, 1000f);

    const int NumMotes = 8;

    // 牌の実寸は 0.021 x 0.029 x 0.016m。実寸の見当（1000単位 = 0.25m。テクスチャは quad いっぱいには描かれていない）:
    //   円盤 … 最大 380 * 1.45 * 0.86 ≒ 0.118m ＝ 牌の約5.6枚ぶん
    //   リング… 最大 420 * 1.80 * 0.78 ≒ 0.147m ＝ 牌の約7枚ぶん
    // 既存の和了演出（Glow 600 / Ring 560）と同じ大きさの系統に揃えてある。
    static readonly Vector2 DiscSize = new Vector2(380f, 380f);
    static readonly Vector2 RingSize = new Vector2(420f, 420f);
    static readonly Vector2 SunSize = new Vector2(460f, 460f);
    static readonly Vector2 FlashSize = new Vector2(260f, 260f);
    static readonly Vector2 StreakSize = new Vector2(40f, 380f);
    static readonly Vector2 MoteSize = new Vector2(44f, 44f);

    // Wire が毎回入れ直す値（U# のシリアライズ済み値は既定値の変更では更新されない）。
    const float Duration = 1.90f;
    const float StreakSecs = 0.09f;
    const float FlashSecs = 0.18f;
    const float DiscSecs = 1.30f;
    const float DiscOpenSecs = 0.16f;
    const float RingSecs = 1.90f;

    /// <summary>第2波の開始。彗星トレイルの点火（89フレーム＝1.483秒）の少し前に置く。</summary>
    const float SunStartSecs = 1.28f;
    const float SunSecs = 0.62f;

    // ---- SE ----
    // ここに音源を置いて Wire を回すと自動で割り当たる（AgariReadoutSFX と同じ流儀）。
    // ファイル名の頭で役割が決まる（大文字小文字は無視）:
    //   impact*.wav … 着弾（0.00秒）
    //   burst*.wav  … 第2波の金バースト（1.28秒）
    const string SfxDir = "Assets/Mahjong/Sounds/YakumanImpactSFX";
    const string ImpactPrefix = "impact";
    const string BurstPrefix = "burst";

    // 音量は「単発の決定的な瞬間」＝段A（目標 -35 LUFS）。
    // 素材を差し替えたら必ず実測して入れ直すこと:
    //   ffmpeg -hide_banner -i <file> -af ebur128=peak=true -f null -
    //   volume = 10^((-35 - 実測LUFS) / 20)
    const float ImpactVolume = 0.085f;
    const float BurstVolume = 0.085f;

    const float CenterHeightOffset = 0.012f;
    const float GroundHeightOffset = 0.004f;

    // 色は固定（コスメでは変えない）。動画のフレームから拾った値。
    //   円盤   … 白に近い金 → 橙 → 淡い琥珀
    //   リング … 橙 → 淡い黄 → 淡いピンク白（終盤はほとんど色が抜ける）
    //   第2波  … 白に近い芯 → 金
    static readonly Color DiscHot = new Color(1.00f, 0.94f, 0.75f, 1f);
    static readonly Color DiscWarm = new Color(1.00f, 0.60f, 0.16f, 1f);
    static readonly Color DiscFade = new Color(1.00f, 0.79f, 0.42f, 1f);
    static readonly Color RingEarly = new Color(1.00f, 0.70f, 0.28f, 1f);
    static readonly Color RingMid = new Color(1.00f, 0.91f, 0.66f, 1f);
    static readonly Color RingLate = new Color(0.91f, 0.77f, 0.69f, 1f);
    static readonly Color SunCore = new Color(1.00f, 0.97f, 0.86f, 1f);
    static readonly Color SunGold = new Color(1.00f, 0.77f, 0.13f, 1f);
    static readonly Color FlashCol = new Color(1f, 1f, 1f, 1f);
    static readonly Color StreakCol = new Color(0.89f, 1.00f, 0.55f, 1f);
    static readonly Color MoteCol = new Color(1.00f, 0.95f, 0.82f, 1f);
    const float ImpactScale = 1.0f;
    const float StreakAngle = 34.0f;
    const float StreakDistance = 520.0f;
    const float MoteDistance = 240.0f;

    // ================================================================ メニュー

    [MenuItem("Tools/Kemono Mahjong/Build Yakuman Impact (Prefab)")]
    public static void BuildPrefab()
    {
        EnsureFxTextures();
        EnsureFolder(SfxDir);   // 音源の置き場所を先に作っておく

        var stage = PrefabStageUtility.OpenPrefab(TablePrefabPath);
        if (stage == null)
        {
            Debug.LogError("[BuildYakumanImpact] Prefab を開けません: " + TablePrefabPath);
            return;
        }

        var root = stage.prefabContentsRoot;
        if (root.GetComponent<Mahjong>() == null)
        {
            Debug.LogError("[BuildYakumanImpact] Prefab のルートに Mahjong がありません。");
            StageUtility.GoToMainStage();
            return;
        }

        BuildOne(root.transform);
        SavePrefabStage(stage);

        Debug.Log("[BuildYakumanImpact] Prefab に着弾リグを生成/更新しました。"
            + " … 次に Wire Yakuman Impact (Prefab) → Wire Yakuman Impact を実行してください。");
    }

    [MenuItem("Tools/Kemono Mahjong/Wire Yakuman Impact (Prefab)")]
    public static void WirePrefab()
    {
        if (EnsureProgramAsset())
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[BuildYakumanImpact] U#プログラムアセットを生成しました。"
                + "コンパイル完了後、もう一度 Wire Yakuman Impact (Prefab) を実行してください。");
            return;
        }

        var stage = PrefabStageUtility.OpenPrefab(TablePrefabPath);
        if (stage == null)
        {
            Debug.LogError("[BuildYakumanImpact] Prefab を開けません: " + TablePrefabPath);
            return;
        }

        int n = WireOne(stage.prefabContentsRoot.transform) ? 1 : 0;
        SavePrefabStage(stage);

        Debug.Log("[BuildYakumanImpact] Prefab を配線しました（" + n + "）。"
            + " Prefab だけでは Udon 側の publicVariables に入らないので、"
            + "続けて Wire Yakuman Impact（シーン版）も実行してください。");
    }

    [MenuItem("Tools/Kemono Mahjong/Wire Yakuman Impact")]
    public static void Wire()
    {
        if (EnsureProgramAsset())
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[BuildYakumanImpact] U#プログラムアセットを生成しました。"
                + "コンパイル完了後、もう一度 Wire Yakuman Impact を実行してください。");
            return;
        }

        var tables = Object.FindObjectsOfType<Mahjong>(true);
        if (tables == null || tables.Length == 0)
        {
            Debug.LogWarning("[BuildYakumanImpact] シーンに Mahjong が見つかりません。");
            return;
        }

        int wired = 0;
        foreach (var t in tables) { if (WireOne(t.transform)) { ++wired; } }

        MarkSceneDirty();
        Debug.Log("[BuildYakumanImpact] " + wired + " / " + tables.Length + " 卓を配線しました。");
    }

    [MenuItem("Tools/Kemono Mahjong/Validate Yakuman Impact")]
    public static void Validate()
    {
        var lines = new System.Collections.Generic.List<string>();
        var tables = Object.FindObjectsOfType<Mahjong>(true);

        int ok = 0;
        int udonOk = 0;
        int linked = 0;

        foreach (var t in tables)
        {
            var rootT = t.transform.Find(RootName);
            var comp = rootT != null ? rootT.GetComponent<MahjongYakumanImpact>() : null;
            if (comp == null) { lines.Add(t.name + ": " + RootName + " / コンポーネントなし"); continue; }

            bool full = comp.fxRoot != null
                && comp.groundRoot != null
                && comp.billboardRoot != null
                && comp.discImage != null && comp.ringImage != null
                && comp.sunImage != null && comp.flashImage != null && comp.streakImage != null
                && comp.moteRects != null && comp.moteRects.Length == NumMotes
                && AllSetRects(comp.moteRects)
                // 参照が埋まっていても、ルートが寝ていると外から叩けない。
                && rootT.gameObject.activeInHierarchy;
            if (full) { ++ok; }
            else
            {
                lines.Add(t.name + ": 未配線"
                    + " fxRoot=" + (comp.fxRoot != null)
                    + " disc=" + (comp.discImage != null)
                    + " ring=" + (comp.ringImage != null)
                    + " sun=" + (comp.sunImage != null)
                    + " motes=" + (comp.moteRects == null ? 0 : comp.moteRects.Length)
                    + " rootActive=" + rootT.gameObject.activeInHierarchy);
            }

            var ub = UdonSharpEditorUtility.GetBackingUdonBehaviour(comp);
            object v;
            if (ub != null && ub.publicVariables != null
                && ub.publicVariables.TryGetVariableValue("fxRoot", out v) && v != null)
            {
                ++udonOk;
            }

            if (t.yakumanImpact == comp) { ++linked; }
        }

        lines.Add("配線済み（プロキシ）: " + ok + " / " + tables.Length);
        lines.Add("配線済み（Udon publicVariables）: " + udonOk + " / " + tables.Length);
        lines.Add("卓からの逆参照 Mahjong.yakumanImpact: " + linked + " / " + tables.Length);
        var impactClip = FindSfx(ImpactPrefix);
        var burstClip = FindSfx(BurstPrefix);
        lines.Add("SE置き場: " + SfxDir + "（" + (AssetDatabase.IsValidFolder(SfxDir) ? "あり" : "なし") + "）");
        lines.Add("  impact*: " + DescribeClip(impactClip, ImpactVolume));
        lines.Add("  burst* : " + DescribeClip(burstClip, BurstVolume));
        WarnIfTooLong(lines, "impact", impactClip, Duration);
        WarnIfTooLong(lines, "burst", burstClip, Duration - SunStartSecs);

        lines.Add("段取り: 着弾0.00s → 円盤" + DiscSecs.ToString("F2") + "s → 第2波"
            + SunStartSecs.ToString("F2") + "s（彗星の点火1.483sの直前）→ 終了" + Duration.ToString("F2") + "s");
        Debug.Log("[BuildYakumanImpact] 検証\n" + string.Join("\n", lines.ToArray()));
    }

    static string DescribeClip(AudioClip clip, float volume)
    {
        if (clip == null) { return "なし（無音のまま／手で割り当てていればそれが使われる）"; }
        return clip.name + " / " + clip.length.ToString("F2") + "秒 / volume " + volume.ToString("F3");
    }

    // 演出より長いクリップは、演出が畳まれたあとも鳴り続ける
    // （PlayOneShot は fxRoot を消しても止まらない）。次の段（役満カットイン・
    // あがり画面）へかぶるので、気づけるように出しておく。
    static void WarnIfTooLong(System.Collections.Generic.List<string> lines,
        string label, AudioClip clip, float budget)
    {
        if (clip == null || clip.length <= budget + 0.05f) { return; }
        lines.Add("  ⚠ " + label + " が長すぎます: " + clip.length.ToString("F2")
            + "秒 > 持ち時間 " + budget.ToString("F2") + "秒。"
            + "PlayOneShot は演出を畳んでも止まらないので、このぶん次の段へ音がかぶります。");
    }

    static bool AllSetRects(RectTransform[] arr)
    {
        foreach (var r in arr) { if (r == null) { return false; } }
        return true;
    }

    // ================================================================ リグ

    static void BuildOne(Transform tableRoot)
    {
        var root = EnsureChild(tableRoot, RootName);

        // スクリプトが載るルートは常時アクティブを厳守（非アクティブなU#は外から叩けない）。
        // 表示ON/OFFは子の FxRoot でのみ行う。
        root.transform.localPosition = Vector3.zero;
        root.transform.localRotation = Quaternion.identity;
        root.transform.localScale = Vector3.one;
        root.SetActive(true);

        // SE用（未設定なら鳴らないだけ）。3D音として卓の位置から鳴らす。
        var audio = root.GetComponent<AudioSource>();
        if (audio == null) { audio = root.AddComponent<AudioSource>(); }
        audio.playOnAwake = false;
        audio.loop = false;
        audio.spatialBlend = 1f;
        audio.dopplerLevel = 0f;
        audio.minDistance = 1.5f;
        audio.maxDistance = 14f;
        audio.rolloffMode = AudioRolloffMode.Linear;

        var fxRoot = EnsureChild(root.transform, "FxRoot");
        fxRoot.transform.localPosition = Vector3.zero;
        fxRoot.transform.localRotation = Quaternion.identity;
        fxRoot.transform.localScale = Vector3.one;

        // ---- 卓面レイヤー（寝かせる。円いものは全部こちら）----
        // 動画のリングは牌の手前を横切って奥へ回り込む＝卓に置かれた水平な円。
        // ビルボードで立てると別物になる。
        var ground = EnsureChild(fxRoot.transform, "Ground");
        ground.transform.localPosition = Vector3.zero;
        // 編集中の見え方合わせ（実行時は Play が絶対姿勢で置き直す）。+Zが真上＝上から見える。
        ground.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
        ground.transform.localScale = Vector3.one;

        var groundCanvas = EnsureCanvas(ground.transform);
        // 描画順＝ヒエラルキー順。奥から リング → 円盤 → サンバースト。
        EnsureFxImage(groundCanvas.transform, "Ring", TexRing, RingSize);
        EnsureFxImage(groundCanvas.transform, "Disc", TexDisc, DiscSize);
        EnsureFxImage(groundCanvas.transform, "Sun", TexSun, SunSize);
        CleanupChildren(groundCanvas.transform, new string[] { "Ring", "Disc", "Sun" });

        // ---- 縦レイヤー（プレイヤーの方を向く。空中にあるものだけ）----
        var billboard = EnsureChild(fxRoot.transform, "Billboard");
        billboard.transform.localPosition = Vector3.zero;
        billboard.transform.localRotation = Quaternion.identity;
        billboard.transform.localScale = Vector3.one;

        var canvas = EnsureCanvas(billboard.transform);
        for (int i = 0; i < NumMotes; ++i)
        {
            EnsureFxImage(canvas.transform, "Mote" + i, TexSpark, MoteSize);
        }
        EnsureFxImage(canvas.transform, "Streak", TexBeam, StreakSize);
        EnsureFxImage(canvas.transform, "Flash", TexFlash, FlashSize);

        var keep = new string[2 + NumMotes];
        keep[0] = "Streak";
        keep[1] = "Flash";
        for (int i = 0; i < NumMotes; ++i) { keep[2 + i] = "Mote" + i; }
        CleanupChildren(canvas.transform, keep);

        CleanupChildren(fxRoot.transform, new string[] { "Ground", "Billboard" });

        // 編集中に見えっぱなしにならないよう畳んでおく（実行時も Start で畳まれる）。
        fxRoot.SetActive(false);
        EditorUtility.SetDirty(root);
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

        // 表示専用。GraphicRaycaster は付けない（卓のボタン操作を遮らないため）。
        var raycaster = canvasGo.GetComponent<GraphicRaycaster>();
        if (raycaster != null) { Object.DestroyImmediate(raycaster); }

        var rt = canvasGo.GetComponent<RectTransform>();
        rt.localPosition = Vector3.zero;
        rt.localRotation = Quaternion.identity;
        rt.localScale = new Vector3(CanvasScale, CanvasScale, CanvasScale);
        rt.sizeDelta = CanvasSize;

        return canvasGo;
    }

    static RawImage EnsureFxImage(Transform parent, string name, string texturePath, Vector2 size)
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
        // 光条だけは「下端が中心」＝外から中心へ突き刺さる向きに伸ばす。
        rt.pivot = (name == "Streak") ? new Vector2(0.5f, 0f) : new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = size;
        rt.localScale = Vector3.one;
        rt.localRotation = Quaternion.identity;

        return raw;
    }

    // ================================================================ 配線

    static bool WireOne(Transform tableRoot)
    {
        var rootT = tableRoot.Find(RootName);
        if (rootT == null)
        {
            Debug.LogWarning("[BuildYakumanImpact] " + tableRoot.name + " に " + RootName
                + " がありません。先に Build Yakuman Impact (Prefab) を実行してください。");
            return false;
        }

        var comp = (MahjongYakumanImpact)EnsureUSharpComponent(rootT.gameObject, typeof(MahjongYakumanImpact));
        if (comp == null) { return false; }

        var fxRoot = rootT.Find("FxRoot");
        var ground = fxRoot != null ? fxRoot.Find("Ground") : null;
        var billboard = fxRoot != null ? fxRoot.Find("Billboard") : null;
        var groundCanvas = ground != null ? ground.Find("Canvas") : null;
        var canvas = billboard != null ? billboard.Find("Canvas") : null;
        if (groundCanvas == null || canvas == null)
        {
            Debug.LogWarning("[BuildYakumanImpact] " + tableRoot.name + " のリグが不完全です。Build を先に。");
            return false;
        }

        comp.fxRoot = fxRoot.gameObject;
        comp.groundRoot = ground;
        comp.billboardRoot = billboard;
        comp.seSource = rootT.GetComponent<AudioSource>();

        // 円いものは卓面レイヤー、空中のものは縦レイヤー。
        comp.ringImage = Grab(groundCanvas, "Ring", out comp.ringRect);
        comp.discImage = Grab(groundCanvas, "Disc", out comp.discRect);
        comp.sunImage = Grab(groundCanvas, "Sun", out comp.sunRect);
        comp.streakImage = Grab(canvas, "Streak", out comp.streakRect);
        comp.flashImage = Grab(canvas, "Flash", out comp.flashRect);

        var moteImages = new RawImage[NumMotes];
        var moteRects = new RectTransform[NumMotes];
        for (int i = 0; i < NumMotes; ++i)
        {
            moteImages[i] = Grab(canvas, "Mote" + i, out moteRects[i]);
        }
        comp.moteImages = moteImages;
        comp.moteRects = moteRects;

        // 見た目の確定値は毎回ここで入れ直す
        // （U# のシリアライズ済み値は既定値を変えても更新されないため）。
        comp.duration = Duration;
        comp.streakSeconds = StreakSecs;
        comp.flashSeconds = FlashSecs;
        comp.discSeconds = DiscSecs;
        comp.discOpenSeconds = DiscOpenSecs;
        comp.ringSeconds = RingSecs;
        comp.sunStartSeconds = SunStartSecs;
        comp.sunSeconds = SunSecs;
        comp.centerHeightOffset = CenterHeightOffset;
        comp.groundHeightOffset = GroundHeightOffset;

        // 色は固定。動画から拾った値をここで毎回入れ直す
        // （Inspector で手直ししても Wire で巻き戻る＝ここが正）。
        comp.discColorHot = DiscHot;
        comp.discColorWarm = DiscWarm;
        comp.discColorFade = DiscFade;
        comp.ringColorEarly = RingEarly;
        comp.ringColorMid = RingMid;
        comp.ringColorLate = RingLate;
        comp.sunColorCore = SunCore;
        comp.sunColorGold = SunGold;
        comp.flashColor = FlashCol;
        comp.streakColor = StreakCol;
        comp.moteColor = MoteCol;

        // SE。フォルダに音源があれば拾う。無ければ手で割り当てた分を消さない。
        var impactClip = FindSfx(ImpactPrefix);
        var burstClip = FindSfx(BurstPrefix);
        if (impactClip != null) { comp.impactSound = impactClip; }
        if (burstClip != null) { comp.burstSound = burstClip; }
        comp.impactVolume = ImpactVolume;
        comp.burstVolume = BurstVolume;
        comp.impactScale = ImpactScale;
        comp.streakAngle = StreakAngle;
        comp.streakDistance = StreakDistance;
        comp.moteDistance = MoteDistance;

        UdonSharpEditorUtility.CopyProxyToUdon(comp);
        EditorUtility.SetDirty(comp);

        // 卓本体 → 着弾演出の逆参照（未割当なら演出なし＝従来動作）。
        var table = tableRoot.GetComponent<Mahjong>();
        if (table != null && table.yakumanImpact != comp)
        {
            table.yakumanImpact = comp;
            UdonSharpEditorUtility.CopyProxyToUdon(table);
            EditorUtility.SetDirty(table);
        }
        return true;
    }

    /// <summary>SEフォルダから、名前がこの接頭辞で始まる最初のクリップを拾う。</summary>
    static AudioClip FindSfx(string prefix)
    {
        if (!AssetDatabase.IsValidFolder(SfxDir)) { return null; }

        foreach (var guid in AssetDatabase.FindAssets("t:AudioClip", new[] { SfxDir }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (name == null) { continue; }
            if (!name.ToLowerInvariant().StartsWith(prefix)) { continue; }

            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (clip != null) { return clip; }
        }
        return null;
    }

    static RawImage Grab(Transform canvas, string name, out RectTransform rect)
    {
        var t = canvas.Find(name);
        rect = t != null ? t.GetComponent<RectTransform>() : null;
        return t != null ? t.GetComponent<RawImage>() : null;
    }

    // ================================================================ テクスチャ

    public static void EnsureFxTextures()
    {
        EnsureFolder(FxTextureDir);

        bool created = false;
        // 既存の和了演出と共用するもの（向こうの Build が先に走っていれば作られている）。
        created |= WriteTextureIfMissing(TexRing, 256, 256, MakeRing(256));
        created |= WriteTextureIfMissing(TexSpark, 64, 64, MakeSpark(64));
        created |= WriteTextureIfMissing(TexBeam, 64, 512, MakeBeam(64, 512));
        created |= WriteTextureIfMissing(TexFlash, 256, 256, MakeFlash(256));
        // この演出だけの新規。
        created |= WriteTextureIfMissing(TexDisc, 256, 256, MakeDisc(256));
        created |= WriteTextureIfMissing(TexSun, 512, 512, MakeSun(512, 16));

        if (created)
        {
            AssetDatabase.Refresh();
            Debug.Log("[BuildYakumanImpact] エフェクト用テクスチャを " + FxTextureDir
                + " に生成しました。差し替えたい場合は同名PNGで上書きしてください。");
        }
    }

    // 中心が濃く、縁がわずかに締まる円盤（着弾のフレア）。
    // グロー（外へ強く落ちるだけ）と違って「輪郭のある円」に見えるのが要点。
    static Color[] MakeDisc(int size)
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

                float body = Mathf.Clamp01(1f - r);
                body = body * body * 0.85f;

                float d = (r - 0.86f) / 0.10f;
                float rim = Mathf.Exp(-d * d) * 0.55f;

                px[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(body + rim));
            }
        }
        return px;
    }

    // 中心の白核＋放射状の光条（第2波のサンバースト）。
    static Color[] MakeSun(int size, int rays)
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

                float ang = Mathf.Atan2(dy, dx);
                float spoke = 0.5f + 0.5f * Mathf.Cos(ang * rays);
                spoke = Mathf.Pow(spoke, 3.5f);          // 細い光条に絞る

                float radial = Mathf.Clamp01(1f - r);
                radial = radial * radial;

                float core = Mathf.Exp(-(r * r) * 26f);  // 中心の白核

                float a = Mathf.Clamp01(radial * spoke * 1.15f + core);
                px[y * size + x] = new Color(1f, 1f, 1f, a);
            }
        }
        return px;
    }

    // ---- 以下は BuildAgariEffect と同じ生成式（共用テクスチャが未生成でも動くように） ----

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

                px[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(ring + inner));
            }
        }
        return px;
    }

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
                px[y * size + x] = new Color(1f, 1f, 1f, a * a);
            }
        }
        return px;
    }

    static Color[] MakeBeam(int width, int height)
    {
        var px = new Color[width * height];
        float halfW = width * 0.5f;
        for (int y = 0; y < height; ++y)
        {
            float v = (float)y / (height - 1);
            float vFade = Mathf.Clamp01(1f - v);
            vFade = vFade * vFade;
            float bottom = Mathf.Clamp01(v / 0.06f);

            for (int x = 0; x < width; ++x)
            {
                float dx = (x + 0.5f - halfW) / halfW;
                float hFall = Mathf.Exp(-(dx * dx) * 7.0f);
                float a = Mathf.Clamp01(hFall * vFade * Mathf.Lerp(0.35f, 1f, bottom));
                px[y * width + x] = new Color(1f, 1f, 1f, a);
            }
        }
        return px;
    }

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

                float core = Mathf.Exp(-(r * r) * 18f);
                float cross = Mathf.Exp(-(dy * dy) * 900f) + Mathf.Exp(-(dx * dx) * 900f);
                cross *= Mathf.Clamp01(1f - r);

                px[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(core + cross * 0.8f));
            }
        }
        return px;
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

    // ================================================================ 小物

    static bool EnsureProgramAsset()
    {
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(ScriptPath);
        if (script == null)
        {
            Debug.LogError("[BuildYakumanImpact] スクリプトが見つかりません: " + ScriptPath);
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
        Debug.Log("[BuildYakumanImpact] U#プログラムアセットを生成: " + ProgramAssetPath);
        return true;
    }

    static UdonSharpBehaviour EnsureUSharpComponent(GameObject go, System.Type type)
    {
        var proxy = (UdonSharpBehaviour)go.GetComponent(type);
        if (proxy != null)
        {
            if (UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy) != null) { return proxy; }

            Debug.LogWarning("[BuildYakumanImpact] " + type.Name
                + " の backing UdonBehaviour が無いため作り直します。");
            Object.DestroyImmediate(proxy);
            foreach (var ub in go.GetComponents<UdonBehaviour>())
            {
                if (ub.programSource == null) { Object.DestroyImmediate(ub); }
            }
        }

        var added = (UdonSharpBehaviour)UdonSharpUndo.AddComponent(go, type);
        if (added != null && UdonSharpEditorUtility.GetBackingUdonBehaviour(added) == null)
        {
            Debug.LogError("[BuildYakumanImpact] " + type.Name
                + " の追加に失敗（プログラムアセット未コンパイル？）。");
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
        go.transform.SetParent(parent, false);
        return go;
    }

    static GameObject EnsureUiChild(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null)
        {
            if (t.GetComponent<RectTransform>() != null) { return t.gameObject; }
            // Transform で作られていた場合は RectTransform へ作り直す。
            Object.DestroyImmediate(t.gameObject);
        }

        var go = new GameObject(name);
        go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);
        return go;
    }

    static void CleanupChildren(Transform parent, string[] keepNames)
    {
        var keep = new System.Collections.Generic.HashSet<string>(keepNames);
        for (int i = parent.childCount - 1; i >= 0; --i)
        {
            var c = parent.GetChild(i);
            if (!keep.Contains(c.name)) { Object.DestroyImmediate(c.gameObject); }
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
            if (!AssetDatabase.IsValidFolder(next)) { AssetDatabase.CreateFolder(current, parts[i]); }
            current = next;
        }
    }

    static void SavePrefabStage(PrefabStage stage)
    {
        if (stage == null || stage.prefabContentsRoot == null)
        {
            Debug.LogError("[BuildYakumanImpact] Prefab Stage が無効なため保存できません。");
            StageUtility.GoToMainStage();
            return;
        }

        // Prefab Stage の標準保存経路を通す（UdonSharp のシリアライズが File/Save に乗るため）。
        EditorSceneManager.MarkSceneDirty(stage.scene);
        bool saved = EditorApplication.ExecuteMenuItem("File/Save");
        if (!saved || stage.scene.isDirty)
        {
            Debug.LogError("[BuildYakumanImpact] Prefab の保存に失敗しました: " + stage.assetPath);
        }

        AssetDatabase.SaveAssets();
        // 閉じ忘れると Unity が固まる。必ずメインステージへ戻す。
        StageUtility.GoToMainStage();
    }

    static void MarkSceneDirty()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (scene.IsValid()) { EditorSceneManager.MarkSceneDirty(scene); }
    }
}
#endif
