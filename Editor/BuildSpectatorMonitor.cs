#if UNITY_EDITOR
// 観戦モニター（SpectatorMonitor）の生成・配線・検証。
//
//   Tools > Kemono Mahjong > Build Spectator Monitor     … 生成＋配線（何度でも再実行できる）
//   Tools > Kemono Mahjong > Validate Spectator Monitor  … 参照欠損・配線漏れの洗い出し
//
// ■ 何を作るか（シーン直下 SpectatorMonitor）
//   SpectatorMonitor                 … VVMW の画面の 2cm 手前・同じ向き（U# SpectatorMonitor）
//    ├ Monitor                       … 観戦画面 World Space 1920x1080（3.56m x 2.00m）。押せる物は無い
//    │   ├ Bg / IdleText
//    │   └ Board                     … 上部の帯・盤面（河・中央の箱）・右側の4行（手牌＋副露）
//    ├ Controls                      … 画面の下の操作帯（VRCUiShape）。動画⇔観戦／映像／自動／卓1〜8
//    └ SpectatorCam                  … 配信卓を撮るカメラ（RenderTexture へ・普段は無効）
//   Board の中：Analysis（解説表示＝上部の帯・Diagram 盤面図・CameraView カメラ・Row0..3 手牌）／
//   Broadcast（中継表示＝全画面の映像＋左上の局＋下の4人の帯）／Conditions（オーラスの条件）／
//   CutIn（立直・鳴き・和了のカットイン。最前面）
//
// ■ VVMW には触らない
//   VVMW の Core は毎フレーム画面へ動画を書き込むので、マテリアルは差し替えない。
//   観戦中は「画面の Renderer を止める」「操作UI(MainUI)を隠す」だけ（どちらもローカル）。
//   操作帯を画面の上に置かないのは、MainUI の VRCUiShape とクリックを奪い合うため。
//
// ■ 置き直し
//   位置は毎回 VVMW の画面から取り直す。VVMW を動かしたら Build をもう一度押すだけでよい。
//   中身（Monitor / Controls）は Build のたびに作り直す（手で直した見た目は残らない）。
//
// ■ 牌の絵
//   卓と同じ tiles.png を RawImage.uvRect で切り出す（役種一覧と同じ方式）。
//   矩形は KemonoMahjong.prefab の牌メッシュの UV から実測し、牌ID>>2 を添え字にして焼く。
using System.Collections.Generic;
using System.Reflection;
using SuzuFactory.AozoraMahjong;
using TMPro;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using VRC.Udon;

public static class BuildSpectatorMonitor
{
    const string RootName = "SpectatorMonitor";
    const string ScriptPath = "Assets/Mahjong/Sources/SpectatorMonitor.cs";
    const string ProgramAssetPath = "Assets/Mahjong/scene_UdonProgramSources/SpectatorMonitor Udon C# Program Asset.asset";
    const string FontAssetPath = "Assets/Mahjong/Fonts/NotoSansJP-Bold SDF.asset";
    const string TilePrefabPath = "Assets/Mahjong/Prefabas/KemonoMahjong.prefab";
    const string TileTexturePath = "Assets/Mahjong/Textures/tiles.png";

    const string VvmwRootName = "VVMW (On-Screen Controls)";
    const string VvmwScreenPath = "Screen With Overlay/Screen";
    const string VvmwOverlayPath = "Screen With Overlay/MainUI";

    // --- 大きさ（VVMW の画面と同じ 3.56m x 2.00m）---
    static readonly Vector2 CanvasSize = new Vector2(1920f, 1080f);
    const float ScreenWidthM = 3.56f;
    static float CanvasScale { get { return ScreenWidthM / CanvasSize.x; } }
    // 画面の手前へ出す距離。0 だと VVMW の画面と Z ファイトする。
    const float FrontOffset = 0.02f;
    // 操作帯（画面の下）
    static readonly Vector2 StripSize = new Vector2(1920f, 100f);
    const float StripGapM = 0.015f;

    // --- レイアウト（Canvasピクセル・中心原点）。SpectatorMonitor の public 値と対 ---
    const float TopBarY = 505f;
    const float TopBarH = 70f;
    static readonly Vector2 BoardCenter = new Vector2(-430f, -35f);
    const float BoardSize = 1000f;
    const float CenterBox = 280f;
    const float RiverTileW = 48f;
    const float RiverStartY = -150f;
    const float RowTileW = 52f;
    const float RowW = 840f;
    const float RowH = 240f;
    const float RowCenterX = 520f;
    const float RowTopCenterY = 345f;
    const float RowStep = 252f;

    // --- 色（中継のテロップ風：紺地＋金）---
    static readonly Color Navy = new Color32(10, 18, 38, 255);
    static readonly Color NavyBar = new Color32(17, 28, 56, 255);
    static readonly Color Gold = new Color32(232, 184, 74, 255);
    static readonly Color Felt = new Color32(15, 77, 60, 255);
    static readonly Color FeltFrame = new Color32(201, 162, 74, 255);
    static readonly Color CenterBg = new Color32(11, 26, 22, 245);
    static readonly Color ReachRed = new Color32(220, 60, 50, 255);
    static readonly Color StatusRed = new Color32(255, 110, 94, 255);

    // --- 中継表示の帯（参考動画の 1920x1080 の実寸から）---
    const float BandX0 = -660f;       // 行0 の帯の中心
    const float BandStep = 440f;
    const float BandY = -409f;
    const float BandW = 400f;
    const float BandH = 172f;
    const float PortraitX = -133f;    // 帯の中の座標
    const float PortraitW = 134f;
    const float PortraitH = 160f;
    const float PlateX = 67f;
    const float PlateY = -47f;
    const float PlateW = 266f;
    const float PlateH = 78f;
    const float WaitY = -296f;        // 帯の上の待ち牌の段
    const float BadgeY = -282f;       // 鳴きの札
    const float TopLeftScale = 1.4f;  // 左上（局・供託・本場・山）の大きさ
    const float WaitStripW = 300f;    // 待ち牌の細帯（帯の上端・右寄せ）
    const float WaitStripH = 50f;
    const float WaitStripY = 108f;    // 帯の中の y（帯の上端は 86）
    const float DeltaY = 26f;         // 増減の下段（帯の中の y）
    const float DeltaSubY = 68f;      // 増減の上段
    // 帯の色は実行時にその人の和了演出（雷）の色で塗る（SpectatorMonitor.ApplyBandColor）。ここは既定＝金雷×0.85
    static readonly Color BandDefaultColor = new Color(0.85f, 0.527f, 0.102f, 1f);
    const string GradHPath = "Assets/Mahjong/Textures/SpectatorGradH.png";
    const string GradVPath = "Assets/Mahjong/Textures/SpectatorGradV.png";
    const string OutlineMatPath = "Assets/Mahjong/Fonts/NotoSansJP-Bold SDF Spectator Outline.mat";

    const string CameraRtPath = "Assets/Mahjong/Textures/SpectatorCam.renderTexture";
    const int CameraRtSize = 768;
    const string WideRtPath = "Assets/Mahjong/Textures/SpectatorCamWide.renderTexture";
    const string WideRtQuestPath = "Assets/Mahjong/Textures/SpectatorCamWideQuest.renderTexture";
    // 録画モード（PC）：中継カメラの高解像度の描き先と、モニターの中継画面を合成した映像
    const string WideRtRecordPath = "Assets/Mahjong/Textures/SpectatorCamWideRecord.renderTexture";
    const string RecordRtPath = "Assets/Mahjong/Textures/SpectatorRecord.renderTexture";
    static readonly Color CutInRed = new Color(0.36f, 0.05f, 0.07f, 0.94f);

    static TMP_FontAsset font_;

    // ============================================================
    // ビルド
    // ============================================================
    // 卓の演出のうち「見ている本人の方を向く」表示専用の板（カットイン・あがり画面・精算・待ち牌・オーラス・役満・結果）は、
    // 観戦カメラから見ると牌や顔の前に斜めの板として浮く。モニターには同じ役目の表示が自前であるので、観戦カメラだけ写さないレイヤーへ移す。
    // ・UI(5) は VRChat の手持ちカメラ（写真・配信）も既定で写さないので使わない＝未使用のユーザーレイヤー 25 を使う
    // ・卓の上に置く演出（Ground）・点数板・長考タイマーは残す。操作するパネル（チー候補・自動化・カメラ）は UI ポインタへの影響を避けて動かさない
    public const int SpectatorHiddenLayer = 25;
    const string SpectatorHiddenLayerName = "SpectatorHidden";
    // 録画モード：手持ちカメラだけに写す板のレイヤー（本人の視界からは実行時に外す）。観戦カメラにも写さない
    public const int SpectatorRecordLayer = 26;
    const string SpectatorRecordLayerName = "SpectatorRecord";
    const string RecordPlateMatPath = "Assets/Mahjong/Materials/SpectatorRecordPlate.mat";
    static readonly string[] SpectatorHiddenPaths =
    {
        "PresentationScreen/CutinPanel",
        "PresentationScreen/SettlementPanel",
        "AgariScreen/PanelRoot",
        "MachiPanel/PanelRoot",
        "AllLastBanner/Banner",
        "AgariEffect/FxRoot/Billboard",
        "ReachDiscardEffect/FxRoot/Billboard",
        "CallEffect0/FxRoot/Billboard",
        "CallEffect1/FxRoot/Billboard",
        "CallEffect2/FxRoot/Billboard",
        "CallEffect3/FxRoot/Billboard",
        "YakumanCutin/FxRoot/Billboard",
        "YakumanImpact/FxRoot/Billboard",
        "YakumanBanner/BannerRoot",
        "ResultScreen/PanelRoot",
    };

    // 観戦カメラが撮らないレイヤー：UI(5)・PlayerLocal(10)・UiMenu(12)・MirrorReflection(18)・卓の本人向けの板(25)
    static int SpectatorCullingMask()
    {
        return ~((1 << 5) | (1 << 10) | (1 << 12) | (1 << 18) | (1 << SpectatorHiddenLayer) | (1 << SpectatorRecordLayer));
    }

    [MenuItem("Tools/Kemono Mahjong/Apply Spectator Hidden Layer")]
    public static void ApplySpectatorHiddenLayerMenu()
    {
        if (EditorApplication.isPlaying) { Debug.LogError("[BuildSpectatorMonitor] 再生中は実行できません。"); return; }
        ApplySpectatorHiddenLayer();
    }

    static void ApplySpectatorHiddenLayer()
    {
        // レイヤー名（無くても動くが、Inspector で何のレイヤーか分かるように）
        var tagAssets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
        if (tagAssets != null && tagAssets.Length > 0)
        {
            var tm = new SerializedObject(tagAssets[0]);
            var layers = tm.FindProperty("layers");
            if (layers != null && layers.arraySize > SpectatorHiddenLayer)
            {
                var el = layers.GetArrayElementAtIndex(SpectatorHiddenLayer);
                if (string.IsNullOrEmpty(el.stringValue)) { el.stringValue = SpectatorHiddenLayerName; tm.ApplyModifiedProperties(); }
                else if (el.stringValue != SpectatorHiddenLayerName) { Debug.LogWarning("[BuildSpectatorMonitor] レイヤー " + SpectatorHiddenLayer + " は「" + el.stringValue + "」として使われています。"); }
            }
            if (layers != null && layers.arraySize > SpectatorRecordLayer)
            {
                var el = layers.GetArrayElementAtIndex(SpectatorRecordLayer);
                if (string.IsNullOrEmpty(el.stringValue)) { el.stringValue = SpectatorRecordLayerName; tm.ApplyModifiedProperties(); }
                else if (el.stringValue != SpectatorRecordLayerName) { Debug.LogWarning("[BuildSpectatorMonitor] レイヤー " + SpectatorRecordLayer + " は「" + el.stringValue + "」として使われています。"); }
            }
        }

        // 先に Prefab を直す（シーンの卓はその Prefab のインスタンス）
        int prefabCount = 0;
        var prefabRoot = PrefabUtility.LoadPrefabContents(TilePrefabPath);
        if (prefabRoot != null)
        {
            var pm = prefabRoot.GetComponentInChildren<Mahjong>(true);
            if (pm != null) { prefabCount = SetHiddenLayer(pm.transform, null); }
            if (prefabCount > 0) { PrefabUtility.SaveAsPrefabAsset(prefabRoot, TilePrefabPath); }
            PrefabUtility.UnloadPrefabContents(prefabRoot);
        }

        int sceneCount = 0;
        int tables = 0;
        var missing = new List<string>();
        foreach (var tb in Object.FindObjectsOfType<Mahjong>(true))
        {
            ++tables;
            sceneCount += SetHiddenLayer(tb.transform, missing);
        }

        var monitor = Object.FindObjectOfType<SpectatorMonitor>(true);
        if (monitor != null && monitor.spectatorCamera != null && monitor.spectatorCamera.cullingMask != SpectatorCullingMask())
        {
            monitor.spectatorCamera.cullingMask = SpectatorCullingMask();
            EditorUtility.SetDirty(monitor.spectatorCamera);
        }
        if (sceneCount > 0 || monitor != null) { EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene()); }
        Debug.Log("[BuildSpectatorMonitor] 観戦カメラに写さない板：Prefab " + prefabCount + " 個 / シーン " + tables + " 卓で " + sceneCount + " 個をレイヤー " + SpectatorHiddenLayer + " へ"
            + (missing.Count > 0 ? "（見つからない：" + string.Join(", ", missing) + "）" : ""));
    }

    // 卓の下の対象の板を、子まで丸ごとレイヤー 25 へ。変えた GameObject の数を返す
    static int SetHiddenLayer(Transform table, List<string> missing)
    {
        int n = 0;
        foreach (var path in SpectatorHiddenPaths)
        {
            var t = table.Find(path);
            if (t == null) { if (missing != null && !missing.Contains(path)) { missing.Add(path); } continue; }
            foreach (var c in t.GetComponentsInChildren<Transform>(true))
            {
                if (c.gameObject.layer == SpectatorHiddenLayer) { continue; }
                c.gameObject.layer = SpectatorHiddenLayer;
                EditorUtility.SetDirty(c.gameObject);
                ++n;
            }
        }
        return n;
    }

    [MenuItem("Tools/Kemono Mahjong/Build Spectator Monitor")]
    public static void Run()
    {
        if (EditorApplication.isPlaying)
        {
            Debug.LogError("[BuildSpectatorMonitor] 再生中は実行できません。Play を止めてから実行してください。");
            return;
        }

        font_ = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
        if (font_ == null) { Debug.LogWarning("[BuildSpectatorMonitor] " + FontAssetPath + " が見つかりません（日本語が豆腐になります）。"); }

        if (EnsureProgramAsset())
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[BuildSpectatorMonitor] U#プログラムアセットを生成しました。コンパイル完了後、もう一度 Build Spectator Monitor を実行してください。");
            return;
        }

        var vvmw = GameObject.Find(VvmwRootName);
        if (vvmw == null) { Debug.LogError("[BuildSpectatorMonitor] " + VvmwRootName + " がシーンにありません。"); return; }
        var screen = vvmw.transform.Find(VvmwScreenPath);
        var overlay = vvmw.transform.Find(VvmwOverlayPath);
        if (screen == null || overlay == null) { Debug.LogError("[BuildSpectatorMonitor] VVMW の Screen / MainUI が見つかりません。"); return; }
        var screenRenderer = screen.GetComponent<Renderer>();
        if (screenRenderer == null) { Debug.LogError("[BuildSpectatorMonitor] VVMW の Screen に Renderer がありません。"); return; }

        var matchmaking = Object.FindObjectOfType<MatchmakingManager>(true);
        if (matchmaking == null) { Debug.LogError("[BuildSpectatorMonitor] MatchmakingManager がシーンにありません。"); return; }
        var stats = Object.FindObjectOfType<PlayerStatsStore>(true);
        if (stats == null) { Debug.LogWarning("[BuildSpectatorMonitor] PlayerStatsStore が見つかりません（自動選択で段位を見ません）。"); }

        var keys = MeasureTileKeys(out float tileAspect);
        if (keys == null) { return; }

        // --- ルート ---
        var rootGo = GameObject.Find(RootName);
        if (rootGo == null)
        {
            rootGo = new GameObject(RootName);
            Undo.RegisterCreatedObjectUndo(rootGo, "Build Spectator Monitor");
        }
        var rootT = rootGo.transform;
        rootT.SetParent(null, true);
        // Canvas は +forward 方向を向いて読む（裏から読むと鏡像）。VVMW の MainUI と同じ向きにそろえ、
        // 見る人の側（-forward）へ少しだけ出す。
        rootT.rotation = screen.rotation;
        rootT.position = screen.position - screen.forward * FrontOffset;
        rootT.localScale = Vector3.one;

        var monitor = EnsureComponent(rootGo);
        if (monitor == null) { return; }

        // 中身は毎回作り直す
        DestroyChild(rootT, "Monitor");
        DestroyChild(rootT, "Controls");

        float riverTileH = Mathf.Round(RiverTileW / tileAspect);
        float rowTileH = Mathf.Round(RowTileW / tileAspect);

        var refs = new Refs();
        BuildMonitorCanvas(rootT, refs, riverTileH, rowTileH);
        BuildYakumanAlert(refs.monitorRoot.transform, refs);
        BuildControls(rootT, refs, screen);

        // --- 配線 ---
        var menu = Object.FindObjectOfType<MainMenuController>(true);
        monitor.matchmaking = matchmaking;
        monitor.statsStore = stats;
        monitor.language = Object.FindObjectOfType<KemonoLanguage>(true);
        if (monitor.language == null) { Debug.LogWarning("[BuildSpectatorMonitor] KemonoLanguage が見つかりません（常に日本語で表示します）。"); }
        monitor.vvmwScreenRenderer = screenRenderer;
        monitor.vvmwOverlay = overlay.gameObject;
        monitor.monitorRoot = refs.monitorRoot;
        monitor.boardRoot = refs.boardRoot;
        monitor.idleText = refs.idleText;
        monitor.rangeCenter = rootT;
        monitor.keepAwakeRange = 20f;

        monitor.tileTexture = AssetDatabase.LoadAssetAtPath<Texture>(TileTexturePath);
        monitor.tileU = keys[0];
        monitor.tileV = keys[1];
        monitor.tileW = keys[2];
        monitor.tileH = keys[3];
        if (menu != null) { monitor.tileBackColor = menu.GetTileBackColor(0); }

        monitor.riverTileW = RiverTileW;
        monitor.riverTileH = riverTileH;
        monitor.riverStartY = RiverStartY;
        monitor.rowTileW = RowTileW;
        monitor.rowTileH = rowTileH;
        monitor.rowTileBaseY = 18f;
        monitor.rowTileLeft = 16f;
        monitor.rowTileMaxWidth = RowW - 32f;

        monitor.riverTiles = refs.riverTiles.ToArray();
        monitor.seatBoxTexts = refs.seatBoxTexts.ToArray();
        monitor.reachSticks = refs.reachSticks.ToArray();
        monitor.centerRoundText = refs.centerRoundText;
        monitor.centerSubText = refs.centerSubText;

        monitor.rowRoots = refs.rowRoots.ToArray();
        monitor.rowBacks = refs.rowBacks.ToArray();
        monitor.rowAccents = refs.rowAccents.ToArray();
        monitor.rowWindTexts = refs.rowWind.ToArray();
        monitor.rowNameTexts = refs.rowName.ToArray();
        monitor.rowPointTexts = refs.rowPoint.ToArray();
        monitor.rowRankTexts = refs.rowRank.ToArray();
        monitor.rowStatusTexts = refs.rowStatus.ToArray();
        monitor.rowTiles = refs.rowTiles.ToArray();

        monitor.roundText = refs.roundText;
        monitor.honbaText = refs.honbaText;
        monitor.remainText = refs.remainText;
        monitor.tableText = refs.tableText;
        monitor.ruleText = refs.ruleText;
        monitor.doraLabelText = refs.doraLabel;
        monitor.doraTiles = refs.doraTiles.ToArray();

        monitor.modeButtonBack = refs.modeBack;
        monitor.modeButtonText = refs.modeText;
        monitor.selectGroup = refs.selectGroup;
        monitor.autoButtonBack = refs.autoBack;
        monitor.autoButtonText = refs.autoText;
        monitor.tableButtonBacks = refs.tableBacks.ToArray();
        monitor.tableButtonTexts = refs.tableTexts.ToArray();

        // --- 第2段：カメラ ---
        var rt = EnsureCameraTexture();
        var cam = EnsureCamera(rootT, rt);
        refs.cameraImage.texture = rt;
        var wideRt = EnsureRenderTexture(WideRtPath, 1280, 720, "SpectatorCamWide");
        var wideRtQuest = EnsureRenderTexture(WideRtQuestPath, 960, 540, "SpectatorCamWideQuest");
        refs.broadcastImage.texture = wideRt;
        monitor.squareTexture = rt;
        monitor.wideTexture = wideRt;
        monitor.wideTextureQuest = wideRtQuest;
        monitor.viewModeDefaultPc = 0;
        monitor.viewModeDefaultQuest = 0;
        monitor.analysisRoot = refs.analysisRoot;
        monitor.broadcastRoot = refs.broadcastRoot;
        monitor.broadcastImage = refs.broadcastImage;
        monitor.bcRoundText = refs.bcRound;
        monitor.bcSubText = refs.bcSub;
        monitor.bcTableText = refs.bcTable;
        monitor.bcCards = refs.bcCards.ToArray();
        monitor.bcPortraits = refs.bcPortraits.ToArray();
        monitor.bcWindTexts = refs.bcWind.ToArray();
        monitor.bcNameTexts = refs.bcName.ToArray();
        monitor.bcPointTexts = refs.bcPoint.ToArray();
        monitor.bcRiichi = refs.bcRiichi.ToArray();
        monitor.bcTurnBars = refs.bcTurn.ToArray();
        monitor.bcDeltaTexts = refs.bcDelta.ToArray();
        monitor.bcWaitRoots = refs.bcWaitRoots.ToArray();
        monitor.bcWaitTiles = refs.bcWaitTiles.ToArray();
        monitor.bcWaitRemain = refs.bcWaitRemain.ToArray();
        monitor.bcWaitLabels = refs.bcWaitLabels.ToArray();
        monitor.bcLabelGroups = refs.bcLabelGroups.ToArray();
        monitor.bcCardGroups = refs.bcCardGroups.ToArray();
        monitor.bcWaitGroups = refs.bcWaitGroups.ToArray();
        monitor.bcPlates = refs.bcPlates.ToArray();
        monitor.bcPortraitBgs = refs.bcPortraitBgs.ToArray();
        monitor.bcPlateBrightness = 0.85f;
        monitor.bcKyotakuText = refs.bcKyotaku;
        monitor.bcWallText = refs.bcWall;
        monitor.bcPortraitHeight = PortraitH;
        // 点数移動の時間（参考動画の実測：名前が消えて0.4秒で＋、−は後から、数えは τ＝0.45 秒の指数減速）
        // 参考動画「点数移動UI」の実測（0.2秒刻み）：名前が 0.3 秒で消え 0.6 秒で和了者の増分、2.6 秒で払った人。
        // 4.5 秒で増減が点数へ沈んで数え始め（τ＝0.22 秒）、0.7 秒遅れて名前が戻る
        monitor.bcLabelFade = 0.3f;
        // ＋ → 1秒後に− → ＋−を2秒見せて → 数え込み1秒（卓の精算パネルが出た瞬間から）
        monitor.bcPlusAt = 0f;
        monitor.bcMinusAt = 1.0f;
        monitor.bcCountAt = 3.0f;
        monitor.bcCountTau = 0.19f;
        monitor.bcCountSnap = 1.0f;
        monitor.bcLabelBackDelay = 0.7f;
        monitor.bcDeltaY = DeltaY;
        monitor.bcDeltaSubY = DeltaSubY;
        monitor.bcDeltaSubTexts = refs.bcDeltaSub.ToArray();
        monitor.bcWaitBgs = refs.bcWaitBgs.ToArray();
        monitor.bcWaitStripW = WaitStripW;
        monitor.bcWaitStripH = WaitStripH;
        monitor.bcBandGroup = refs.bcBandGroup;
        monitor.winRoot = refs.winRoot;
        monitor.winGroup = refs.winGroup;
        monitor.winPortraitBg = refs.winPortraitBg;
        monitor.winPortrait = refs.winPortrait;
        monitor.winPlate = refs.winPlate;
        monitor.winWind = refs.winWind;
        monitor.winName = refs.winName;
        monitor.winPoints = refs.winPoints;
        monitor.winYakuGroup = refs.winYakuGroup;
        monitor.winYakuText = refs.winYakuText;
        monitor.winTotalLabel = refs.winTotalLabel;
        monitor.winTotalText = refs.winTotalText;
        monitor.deltaPlus = new Color32(96, 255, 96, 255);
        monitor.deltaMinus = new Color32(255, 46, 46, 255);
        monitor.badgeBroadcastY = BadgeY;
        monitor.badgeCardX0 = BandX0;
        monitor.badgeCardStep = BandStep;
        monitor.condPosAnalysis = BoardCenter + new Vector2(0f, -BoardSize * 0.5f + 100f);
        monitor.condPosBroadcast = new Vector2(190f, 360f);  // 左上の局表示（1.4倍＝右端 x-312・下端 y389）と右上の卓番号（下端 y458）を避ける
        monitor.spectatorCamera = cam;
        monitor.cameraViewRoot = refs.cameraViewRoot;
        monitor.diagramRoot = refs.diagramRoot;
        monitor.viewButtonBack = refs.viewBack;
        monitor.viewButtonText = refs.viewText;

        // --- 第3段：カットイン（立ち絵と焼き文字は卓の宣言演出から借りる）---
        monitor.cutInRoot = refs.cutInRoot;
        monitor.cutInGroup = refs.cutInGroup;
        monitor.cutInBand = refs.cutInBand;
        monitor.cutInContent = refs.cutInContent;
        monitor.cutInPortrait = refs.cutInPortrait;
        monitor.cutInText = refs.cutInText;
        monitor.cutInGlyph = refs.cutInGlyph;
        monitor.cutInName = refs.cutInName;
        monitor.riichiFxRoot = refs.riichiFxRoot;
        monitor.riichiFxStick = refs.riichiFxStick;
        monitor.riichiFxStickGroup = refs.riichiFxStickGroup;
        monitor.riichiFxBlock = refs.riichiFxBlock;
        monitor.riichiFxText = refs.riichiFxText;
        monitor.riichiFxGhost = refs.riichiFxGhost;
        monitor.riichiFxLines = refs.riichiFxLines.ToArray();
        monitor.bcHud = refs.bcHud;
        monitor.riichiFxDuration = 3.2f;
        monitor.callBadgeRoot = refs.callBadgeRoot;
        monitor.callBadgeRect = refs.callBadgeRect;
        monitor.callBadgeGroup = refs.callBadgeGroup;
        monitor.callBadgeText = refs.callBadgeText;
        var callFx = Object.FindObjectOfType<MahjongCallEffect>(true);
        if (callFx != null)
        {
            monitor.charaPortraits = callFx.charaPortraits;
            monitor.defaultPortrait = callFx.defaultPortrait;
            monitor.selfieStore = callFx.selfieStore;
            monitor.ronGlyph = callFx.ronGlyph;
            monitor.tsumoGlyph = callFx.tsumoGlyph;
        }
        else { Debug.LogWarning("[BuildSpectatorMonitor] MahjongCallEffect が見つかりません（カットインは立ち絵なし・文字だけ）。"); }

        // --- 第3段：待ち・トップ差・条件 ---
        monitor.waitRoots = refs.waitRoots.ToArray();
        monitor.waitTiles = refs.waitTiles.ToArray();
        monitor.waitRemainTexts = refs.waitRemain.ToArray();
        monitor.waitLabelTexts = refs.waitLabel.ToArray();
        monitor.rowDiffTexts = refs.rowDiff.ToArray();
        monitor.conditionRoot = refs.conditionRoot;
        monitor.conditionText = refs.conditionText;
        monitor.waitGroups = refs.waitGroups.ToArray();
        monitor.conditionGroup = refs.conditionGroup;
        monitor.yakumanAlertRoot = refs.yakumanAlertRoot;
        monitor.yakumanAlertGroup = refs.yakumanAlertGroup;
        monitor.yakumanAlertBack = refs.yakumanAlertBack;
        monitor.yakumanAlertTitle = refs.yakumanAlertTitle;
        monitor.yakumanAlertBody = refs.yakumanAlertBody;
        monitor.recordButtonBack = refs.recordBack;
        monitor.recordButtonText = refs.recordText;
        monitor.recordDebugText = refs.recordDebug;
        monitor.recordLayer = SpectatorRecordLayer;
        var recordRt = EnsureRenderTexture(RecordRtPath, 1920, 1080, "SpectatorRecord");
        monitor.recordTexture = recordRt;
        monitor.wideTextureRecord = EnsureRenderTexture(WideRtRecordPath, 1920, 1080, "SpectatorCamWideRecord");
        monitor.recordCamera = EnsureRecordCamera(rootT, recordRt);
        monitor.recordFps = 60;
        monitor.recordDebug = false;
        monitor.recordPlate = EnsureRecordPlate(rootT, recordRt);
        monitor.finalRoot = refs.finalRoot;
        monitor.finalGroup = refs.finalGroup;
        monitor.finalTitle = refs.finalTitle;
        monitor.finalRowRts = refs.finalRowRts.ToArray();
        monitor.finalRowGroups = refs.finalRowGroups.ToArray();
        monitor.finalRankTexts = refs.finalRanks.ToArray();
        monitor.finalNameTexts = refs.finalNames.ToArray();
        monitor.finalPointTexts = refs.finalPoints.ToArray();
        monitor.finalDiffTexts = refs.finalDiffs.ToArray();
        monitor.finalPortraits = refs.finalPortraits.ToArray();
        monitor.finalPortraitBgs = refs.finalPortraitBgs.ToArray();
        monitor.finalPlates = refs.finalPlates.ToArray();
        monitor.finalTopBadges = refs.finalTopBadges.ToArray();

        // --- 第3段：音（モニターの位置で宣言ボイスを鳴らし直す）---
        // 音源は子の MonitorAudio にまとめる（録画中は親ごと配信者の頭へ付いて回る）
        var audioRoot = EnsureMonitorAudioRoot(rootGo);
        monitor.monitorAudioRoot = audioRoot;
        monitor.monitorVoice = EnsureMonitorAudio(audioRoot, "Voice", false);
        monitor.monitorBgm = EnsureMonitorAudio(audioRoot, "Bgm", true);

        EditorUtility.SetDirty(monitor);
        UdonSharpEditorUtility.CopyProxyToUdon(monitor);

        // ボタン → U# の SendCustomEvent
        var udon = UdonSharpEditorUtility.GetBackingUdonBehaviour(monitor);
        WireButton(refs.modeButton, udon, "OnToggleMode");
        WireButton(refs.autoButton, udon, "OnSelectAuto");
        WireButton(refs.viewButton, udon, "OnToggleView");
        WireButton(refs.recordButton, udon, "OnToggleRecord");
        for (int i = 0; i < refs.tableButtons.Count; ++i) { WireButton(refs.tableButtons[i], udon, "OnSelectTable" + i); }

        // カリングは matchmaking 経由で monitor を引く
        Undo.RecordObject(matchmaking, "Build Spectator Monitor");
        matchmaking.spectatorMonitor = monitor;
        EditorUtility.SetDirty(matchmaking);
        UdonSharpEditorUtility.CopyProxyToUdon(matchmaking);

        // 起動時の見た目（観戦OFF）にそろえておく
        refs.monitorRoot.SetActive(false);
        refs.selectGroup.SetActive(false);

        EditorSceneManager.MarkSceneDirty(rootGo.scene);
        ApplySpectatorHiddenLayer();
        Debug.Log("[BuildSpectatorMonitor] 完了: 位置 " + rootT.position.ToString("F3") +
                  " / 大きさ " + (CanvasSize.x * CanvasScale).ToString("F2") + " x " + (CanvasSize.y * CanvasScale).ToString("F2") + " m" +
                  " / 牌の比 " + tileAspect.ToString("F3") + "（河 " + RiverTileW + "x" + riverTileH + "・行 " + RowTileW + "x" + rowTileH + "）" +
                  " / 卓 " + (matchmaking.tables != null ? matchmaking.tables.Length : 0));
    }

    class Refs
    {
        public GameObject monitorRoot, boardRoot, selectGroup;
        public TextMeshProUGUI idleText, centerRoundText, centerSubText;
        public TextMeshProUGUI roundText, honbaText, remainText, tableText, ruleText, doraLabel;
        public List<RawImage> riverTiles = new List<RawImage>();
        public List<TextMeshProUGUI> seatBoxTexts = new List<TextMeshProUGUI>();
        public List<GameObject> reachSticks = new List<GameObject>();
        public List<GameObject> rowRoots = new List<GameObject>();
        public List<Image> rowBacks = new List<Image>();
        public List<Image> rowAccents = new List<Image>();
        public List<TextMeshProUGUI> rowWind = new List<TextMeshProUGUI>();
        public List<TextMeshProUGUI> rowName = new List<TextMeshProUGUI>();
        public List<TextMeshProUGUI> rowPoint = new List<TextMeshProUGUI>();
        public List<TextMeshProUGUI> rowRank = new List<TextMeshProUGUI>();
        public List<TextMeshProUGUI> rowStatus = new List<TextMeshProUGUI>();
        public List<RawImage> rowTiles = new List<RawImage>();
        public List<RawImage> doraTiles = new List<RawImage>();
        public Button modeButton, autoButton, recordButton;
        public Image recordBack;
        public TextMeshProUGUI recordText, recordDebug;
        public Image modeBack, autoBack;
        public TextMeshProUGUI modeText, autoText;
        public List<Button> tableButtons = new List<Button>();
        public List<Image> tableBacks = new List<Image>();
        public List<TextMeshProUGUI> tableTexts = new List<TextMeshProUGUI>();
        // 第2段・第3段
        public GameObject diagramRoot, cameraViewRoot, conditionRoot, cutInRoot;
        public RawImage cameraImage;
        public TextMeshProUGUI conditionText;
        public CanvasGroup cutInGroup;
        public RectTransform cutInBand, cutInContent;
        public RawImage cutInPortrait, cutInGlyph;
        public GameObject riichiFxRoot, riichiFxBlock;
        public RectTransform riichiFxStick;
        public CanvasGroup riichiFxStickGroup;
        public TextMeshProUGUI riichiFxText, riichiFxGhost;
        public List<RectTransform> riichiFxLines = new List<RectTransform>();
        public CanvasGroup bcHud;
        public TextMeshProUGUI cutInText, cutInName;
        public GameObject callBadgeRoot;
        public RectTransform callBadgeRect;
        public CanvasGroup callBadgeGroup;
        public TextMeshProUGUI callBadgeText;
        public List<TextMeshProUGUI> rowDiff = new List<TextMeshProUGUI>();
        public List<GameObject> waitRoots = new List<GameObject>();
        public List<RawImage> waitTiles = new List<RawImage>();
        public List<TextMeshProUGUI> waitRemain = new List<TextMeshProUGUI>();
        public List<TextMeshProUGUI> waitLabel = new List<TextMeshProUGUI>();
        public Button viewButton;
        public Image viewBack;
        public TextMeshProUGUI viewText;
        // 中継表示
        public GameObject analysisRoot, broadcastRoot;
        public RawImage broadcastImage;
        public TextMeshProUGUI bcRound, bcSub, bcTable, bcKyotaku, bcWall;
        public List<CanvasGroup> bcLabelGroups = new List<CanvasGroup>();
        public List<RawImage> bcPlates = new List<RawImage>();
        public List<RawImage> bcPortraitBgs = new List<RawImage>();
        public List<GameObject> bcCards = new List<GameObject>();
        public List<RawImage> bcPortraits = new List<RawImage>();
        public List<TextMeshProUGUI> bcWind = new List<TextMeshProUGUI>();
        public List<TextMeshProUGUI> bcName = new List<TextMeshProUGUI>();
        public List<TextMeshProUGUI> bcPoint = new List<TextMeshProUGUI>();
        public List<GameObject> bcRiichi = new List<GameObject>();
        public List<GameObject> bcTurn = new List<GameObject>();
        public List<TextMeshProUGUI> bcDelta = new List<TextMeshProUGUI>();
        public List<TextMeshProUGUI> bcDeltaSub = new List<TextMeshProUGUI>();
        public List<RectTransform> bcWaitBgs = new List<RectTransform>();
        public CanvasGroup bcBandGroup, winGroup, winYakuGroup;
        public GameObject winRoot;
        public RawImage winPortraitBg, winPortrait, winPlate;
        public TextMeshProUGUI winWind, winName, winPoints, winYakuText, winTotalLabel, winTotalText;
        public List<GameObject> bcWaitRoots = new List<GameObject>();
        public List<RawImage> bcWaitTiles = new List<RawImage>();
        public List<TextMeshProUGUI> bcWaitRemain = new List<TextMeshProUGUI>();
        public List<TextMeshProUGUI> bcWaitLabels = new List<TextMeshProUGUI>();
        // UIのアニメーション用（フェードは CanvasGroup の alpha で動かす）
        public List<CanvasGroup> bcCardGroups = new List<CanvasGroup>();
        public List<CanvasGroup> bcWaitGroups = new List<CanvasGroup>();
        public List<CanvasGroup> waitGroups = new List<CanvasGroup>();
        public CanvasGroup conditionGroup;
        // 役満通知
        public GameObject yakumanAlertRoot;
        public CanvasGroup yakumanAlertGroup;
        public Image yakumanAlertBack;
        public TextMeshProUGUI yakumanAlertTitle, yakumanAlertBody;
        // 終局の最終結果
        public GameObject finalRoot;
        public CanvasGroup finalGroup;
        public TextMeshProUGUI finalTitle;
        public List<RectTransform> finalRowRts = new List<RectTransform>();
        public List<CanvasGroup> finalRowGroups = new List<CanvasGroup>();
        public List<TextMeshProUGUI> finalRanks = new List<TextMeshProUGUI>();
        public List<TextMeshProUGUI> finalNames = new List<TextMeshProUGUI>();
        public List<TextMeshProUGUI> finalPoints = new List<TextMeshProUGUI>();
        public List<TextMeshProUGUI> finalDiffs = new List<TextMeshProUGUI>();
        public List<RawImage> finalPortraits = new List<RawImage>();
        public List<RawImage> finalPortraitBgs = new List<RawImage>();
        public List<RawImage> finalPlates = new List<RawImage>();
        public List<GameObject> finalTopBadges = new List<GameObject>();
    }

    // ============================================================
    // 観戦画面
    // ============================================================
    static void BuildMonitorCanvas(Transform rootT, Refs r, float riverTileH, float rowTileH)
    {
        var canvasT = NewRect(rootT, "Monitor", Vector2.zero, CanvasSize);
        canvasT.localPosition = Vector3.zero;
        canvasT.localRotation = Quaternion.identity;
        canvasT.localScale = Vector3.one * CanvasScale;
        var canvas = canvasT.gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvasT.gameObject.AddComponent<CanvasScaler>();
        r.monitorRoot = canvasT.gameObject;

        Img(canvasT, "Bg", Vector2.zero, CanvasSize, Navy);

        var idle = Txt(canvasT, "IdleText", Vector2.zero, new Vector2(1600f, 420f), 64f, Color.white, TextAlignmentOptions.Center);
        idle.text = "観戦できる対局はありません";
        r.idleText = idle;

        var board = NewRect(canvasT, "Board", Vector2.zero, CanvasSize);
        r.boardRoot = board.gameObject;
        var analysis = NewRect(board, "Analysis", Vector2.zero, CanvasSize);
        r.analysisRoot = analysis.gameObject;

        // --- 上部の帯 ---
        Img(analysis, "TopBar", new Vector2(0f, TopBarY), new Vector2(CanvasSize.x, TopBarH), NavyBar);
        Img(analysis, "TopLine", new Vector2(0f, TopBarY - TopBarH * 0.5f - 1.5f), new Vector2(CanvasSize.x, 3f), Gold);
        r.roundText = Txt(analysis, "RoundText", new Vector2(-800f, TopBarY), new Vector2(260f, TopBarH), 44f, Gold, TextAlignmentOptions.Left);
        r.honbaText = Txt(analysis, "HonbaText", new Vector2(-480f, TopBarY), new Vector2(380f, TopBarH), 30f, Color.white, TextAlignmentOptions.Left);
        var doraLabel = Txt(analysis, "DoraLabel", new Vector2(-185f, TopBarY), new Vector2(150f, TopBarH), 26f, Gold, TextAlignmentOptions.Right);
        doraLabel.text = "ドラ表示";
        r.doraLabel = doraLabel;
        for (int i = 0; i < 5; ++i)
        {
            r.doraTiles.Add(Raw(analysis, "Dora" + i, new Vector2(-80f + i * 46f, TopBarY), new Vector2(40f, Mathf.Round(40f * riverTileH / RiverTileW))));
        }
        r.remainText = Txt(analysis, "RemainText", new Vector2(240f, TopBarY), new Vector2(220f, TopBarH), 30f, Color.white, TextAlignmentOptions.Center);
        r.ruleText = Txt(analysis, "RuleText", new Vector2(530f, TopBarY), new Vector2(380f, TopBarH), 28f, Color.white, TextAlignmentOptions.Center);
        r.tableText = Txt(analysis, "TableText", new Vector2(830f, TopBarY), new Vector2(240f, TopBarH), 34f, Gold, TextAlignmentOptions.Right);

        // --- 盤面（席0が下・下家が右。物理席の向き＝卓を真上から見た並び）---
        var diagram = NewRect(analysis, "Diagram", Vector2.zero, CanvasSize);
        r.diagramRoot = diagram.gameObject;
        Img(diagram, "FeltFrame", BoardCenter, new Vector2(BoardSize + 10f, BoardSize + 10f), FeltFrame);
        Img(diagram, "Felt", BoardCenter, new Vector2(BoardSize, BoardSize), Felt);
        Img(diagram, "CenterBox", BoardCenter, new Vector2(CenterBox, CenterBox), CenterBg);
        r.centerRoundText = Txt(diagram, "CenterRound", BoardCenter + new Vector2(0f, 18f), new Vector2(180f, 60f), 44f, Gold, TextAlignmentOptions.Center);
        r.centerSubText = Txt(diagram, "CenterSub", BoardCenter + new Vector2(0f, -32f), new Vector2(180f, 40f), 26f, Color.white, TextAlignmentOptions.Center);

        for (int seat = 0; seat < 4; ++seat)
        {
            var river = NewRect(diagram, "River" + seat, BoardCenter, Vector2.zero);
            river.localEulerAngles = new Vector3(0f, 0f, seat * 90f);

            var seatText = Txt(river, "SeatText", new Vector2(0f, -CenterBox * 0.5f + 28f), new Vector2(CenterBox - 20f, 44f), 30f, Color.white, TextAlignmentOptions.Center);
            r.seatBoxTexts.Add(seatText);

            var stick = Img(river, "ReachStick", new Vector2(0f, -CenterBox * 0.5f + 62f), new Vector2(150f, 12f), Color.white);
            Img(stick.transform, "Dot", Vector2.zero, new Vector2(12f, 12f), ReachRed);
            r.reachSticks.Add(stick.gameObject);

            for (int j = 0; j < SpectatorMonitor.RIVER_MAX; ++j)
            {
                var img = Raw(river, "T" + j, Vector2.zero, new Vector2(RiverTileW, riverTileH));
                img.gameObject.SetActive(false);
                r.riverTiles.Add(img);
            }
        }

        // --- カメラ映像（盤面図と同じ場所。どちらか一方だけ出す）---
        var camView = NewRect(analysis, "CameraView", Vector2.zero, CanvasSize);
        r.cameraViewRoot = camView.gameObject;
        Img(camView, "Frame", BoardCenter, new Vector2(BoardSize + 10f, BoardSize + 10f), FeltFrame);
        r.cameraImage = Raw(camView, "CameraImage", BoardCenter, new Vector2(BoardSize, BoardSize));

        // --- 中継表示（Mリーグ式）---
        BuildBroadcast(board, r);

        // --- オーラスの逆転条件（盤面の下側に重ねる）---
        var cond = Img(board, "Conditions", BoardCenter + new Vector2(0f, -BoardSize * 0.5f + 100f), new Vector2(BoardSize - 20f, 180f), new Color(0f, 0f, 0f, 0.74f));
        r.conditionRoot = cond.gameObject;
        r.conditionGroup = AnimGroup(cond.gameObject);
        r.conditionText = Txt(cond.transform, "Text", Vector2.zero, new Vector2(BoardSize - 60f, 160f), 26f, Color.white, TextAlignmentOptions.TopLeft);
        r.conditionText.enableWordWrapping = true;
        r.conditionText.richText = true;
        cond.gameObject.SetActive(false);

        // --- 右側の4行（行0=東家…）---
        for (int row = 0; row < 4; ++row)
        {
            var rowT = NewRect(analysis, "Row" + row, new Vector2(RowCenterX, RowTopCenterY - row * RowStep), new Vector2(RowW, RowH));
            r.rowRoots.Add(rowT.gameObject);

            var back = Img(rowT, "Back", Vector2.zero, new Vector2(RowW, RowH), new Color(0.07f, 0.11f, 0.22f, 0.92f));
            r.rowBacks.Add(back);
            var accent = Img(rowT, "Accent", new Vector2(-RowW * 0.5f + 4f, 0f), new Vector2(8f, RowH), Gold);
            r.rowAccents.Add(accent);

            // 行の中身は左下原点で置く（牌の座標計算をそのまま使うため）
            var content = NewRect(rowT, "Content", Vector2.zero, new Vector2(RowW, RowH));
            float headY = RowH - 45f;
            r.rowWind.Add(TxtBL(content, "Wind", new Vector2(50f, headY), new Vector2(70f, 70f), 52f, Gold, TextAlignmentOptions.Center));
            var nameText = TxtBL(content, "Name", new Vector2(250f, headY), new Vector2(310f, 64f), 36f, Color.white, TextAlignmentOptions.Left);
            nameText.overflowMode = TextOverflowModes.Ellipsis;
            r.rowName.Add(nameText);
            r.rowStatus.Add(TxtBL(content, "Status", new Vector2(480f, headY), new Vector2(150f, 60f), 28f, StatusRed, TextAlignmentOptions.Center));
            r.rowPoint.Add(TxtBL(content, "Points", new Vector2(650f, headY), new Vector2(190f, 60f), 46f, Color.white, TextAlignmentOptions.Right));
            r.rowRank.Add(TxtBL(content, "Rank", new Vector2(790f, headY), new Vector2(90f, 60f), 30f, Gold, TextAlignmentOptions.Center));
            r.rowDiff.Add(TxtBL(content, "Diff", new Vector2(700f, headY - 48f), new Vector2(260f, 36f), 24f, new Color(0.80f, 0.85f, 0.95f, 1f), TextAlignmentOptions.Right));

            // 待ち牌（手牌の上の段・左寄せ）。中身の座標は SpectatorMonitor の waitTile* と対
            var wait = NewRectBL(content, "Wait", new Vector2(RowW * 0.5f, RowH * 0.5f), new Vector2(RowW, RowH));
            r.waitRoots.Add(wait.gameObject);
            r.waitGroups.Add(AnimGroup(wait.gameObject));
            r.waitLabel.Add(TxtBL(wait, "Label", new Vector2(44f, 128f), new Vector2(70f, 36f), 24f, Gold, TextAlignmentOptions.Center));
            for (int k = 0; k < SpectatorMonitor.WAIT_MAX; ++k)
            {
                r.waitTiles.Add(RawBL(wait, "W" + k, Vector2.zero, new Vector2(26f, 39f)));
            }
            r.waitRemain.Add(TxtBL(wait, "Remain", new Vector2(200f, 128f), new Vector2(140f, 36f), 24f, Color.white, TextAlignmentOptions.Center));
            wait.gameObject.SetActive(false);

            for (int n = 0; n < SpectatorMonitor.ROW_MAX; ++n)
            {
                var img = RawBL(content, "T" + n, Vector2.zero, new Vector2(RowTileW, rowTileH));
                img.gameObject.SetActive(false);
                r.rowTiles.Add(img);
            }
        }

        // --- 小さな札（鳴き・北抜き・和了・流局）。位置は SpectatorMonitor が席ごとに動かす ---
        var badge = Img(board, "CallBadge", Vector2.zero, new Vector2(300f, 72f), new Color(Navy.r, Navy.g, Navy.b, 0.94f));
        Img(badge.transform, "Accent", new Vector2(-146f, 0f), new Vector2(8f, 72f), Gold);
        Img(badge.transform, "Line", new Vector2(0f, -34f), new Vector2(300f, 4f), Gold);
        r.callBadgeRoot = badge.gameObject;
        r.callBadgeRect = badge.rectTransform;
        r.callBadgeGroup = badge.gameObject.AddComponent<CanvasGroup>();
        r.callBadgeGroup.interactable = false;
        r.callBadgeGroup.blocksRaycasts = false;
        r.callBadgeText = Txt(badge.transform, "Text", new Vector2(6f, 2f), new Vector2(280f, 64f), 40f, Gold, TextAlignmentOptions.Center);
        r.callBadgeText.fontStyle = FontStyles.Bold;
        badge.gameObject.SetActive(false);

        // --- カットイン（立直系だけ）。Board の最後の子＝最前面 ---
        var cut = NewRect(board, "CutIn", Vector2.zero, CanvasSize);
        r.cutInRoot = cut.gameObject;
        r.cutInGroup = cut.gameObject.AddComponent<CanvasGroup>();
        r.cutInGroup.interactable = false;
        r.cutInGroup.blocksRaycasts = false;
        var band = Img(cut, "Band", new Vector2(0f, -40f), new Vector2(CanvasSize.x, 300f), CutInRed);
        r.cutInBand = band.rectTransform;
        Img(band.transform, "EdgeTop", new Vector2(0f, 150f), new Vector2(CanvasSize.x, 6f), Gold);
        Img(band.transform, "EdgeBottom", new Vector2(0f, -150f), new Vector2(CanvasSize.x, 6f), Gold);
        var cutContent = NewRect(cut, "Content", Vector2.zero, CanvasSize);
        r.cutInContent = cutContent;
        r.cutInPortrait = Raw(cutContent, "Portrait", new Vector2(-600f, -10f), new Vector2(300f, 420f));
        r.cutInText = Txt(cutContent, "Text", new Vector2(140f, -20f), new Vector2(1000f, 250f), 190f, Gold, TextAlignmentOptions.Center);
        r.cutInText.fontStyle = FontStyles.Bold;
        r.cutInGlyph = Raw(cutContent, "Glyph", new Vector2(140f, -20f), new Vector2(460f, 230f));
        r.cutInName = Txt(cutContent, "Name", new Vector2(140f, -155f), new Vector2(1000f, 60f), 44f, Color.white, TextAlignmentOptions.Center);
        cut.gameObject.SetActive(false);

        BuildRiichiFx(board, r);

        SetLayerRecursively(canvasT.gameObject, 0);
    }

    // ============================================================
    // 操作帯（画面の下）
    // ============================================================
    static void BuildControls(Transform rootT, Refs r, Transform screen)
    {
        float screenH = screen.lossyScale.y;   // Quad の高さ＝スケール
        var canvasT = NewRect(rootT, "Controls", Vector2.zero, StripSize);
        canvasT.localPosition = new Vector3(0f, -(screenH * 0.5f + StripGapM + StripSize.y * CanvasScale * 0.5f), 0f);
        canvasT.localRotation = Quaternion.identity;
        canvasT.localScale = Vector3.one * CanvasScale;

        var go = canvasT.gameObject;
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        go.AddComponent<CanvasScaler>();
        go.AddComponent<GraphicRaycaster>();
        go.AddComponent<VRC.SDK3.Components.VRCUiShape>();
        // ClientSim は VRCUiShape と同じ GameObject のコライダー内でしかクリックを通さない。
        // トリガーにして、操作帯がプレイヤーを押す壁にならないようにする。
        var box = go.AddComponent<BoxCollider>();
        box.center = Vector3.zero;
        box.size = new Vector3(1900f, StripSize.y, 1f);
        box.isTrigger = true;

        Img(canvasT, "Bg", Vector2.zero, new Vector2(1900f, 96f), new Color(Navy.r, Navy.g, Navy.b, 0.9f));

        r.modeButton = Btn(canvasT, "ModeButton", new Vector2(-760f, 0f), new Vector2(320f, 80f), 32f, "観戦モニター", out r.modeBack, out r.modeText);

        var sel = NewRect(canvasT, "Select", Vector2.zero, StripSize);
        r.selectGroup = sel.gameObject;
        r.viewButton = Btn(sel, "ViewButton", new Vector2(-345f, 0f), new Vector2(250f, 80f), 28f, "表示：中継", out r.viewBack, out r.viewText);
        r.autoButton = Btn(sel, "AutoButton", new Vector2(-120f, 0f), new Vector2(180f, 80f), 32f, "自動", out r.autoBack, out r.autoText);
        // 録画モード（検証版）。押した本人の端末だけで動く
        r.recordButton = Btn(sel, "RecordButton", new Vector2(-535f, 0f), new Vector2(110f, 80f), 30f, "録画", out r.recordBack, out r.recordText);
        r.recordDebug = Txt(sel, "RecordDebug", new Vector2(0f, -80f), new Vector2(1900f, 44f), 24f, new Color(1f, 0.85f, 0.4f, 1f), TextAlignmentOptions.Left);
        r.recordDebug.gameObject.SetActive(false);
        for (int i = 0; i < 8; ++i)
        {
            Image b; TextMeshProUGUI t;
            var btn = Btn(sel, "Table" + (i + 1), new Vector2(70f + i * 104f, 0f), new Vector2(96f, 80f), 32f, "卓" + (i + 1), out b, out t);
            r.tableButtons.Add(btn);
            r.tableBacks.Add(b);
            r.tableTexts.Add(t);
        }

        SetLayerRecursively(go, 0);
    }

    // ============================================================
    // 第2段：カメラと RenderTexture
    // ============================================================
    static RenderTexture EnsureCameraTexture()
    {
        var rt = AssetDatabase.LoadAssetAtPath<RenderTexture>(CameraRtPath);
        if (rt == null)
        {
            rt = new RenderTexture(CameraRtSize, CameraRtSize, 16, RenderTextureFormat.ARGB32);
            rt.name = "SpectatorCam";
            AssetDatabase.CreateAsset(rt, CameraRtPath);
        }
        rt.antiAliasing = 1;
        rt.useMipMap = false;
        rt.autoGenerateMips = false;
        EditorUtility.SetDirty(rt);
        return rt;
    }

    static RenderTexture EnsureRenderTexture(string path, int w, int h, string name)
    {
        var rt = AssetDatabase.LoadAssetAtPath<RenderTexture>(path);
        if (rt == null)
        {
            rt = new RenderTexture(w, h, 16, RenderTextureFormat.ARGB32);
            rt.name = name;
            AssetDatabase.CreateAsset(rt, path);
        }
        rt.antiAliasing = 1;
        rt.useMipMap = false;
        rt.autoGenerateMips = false;
        EditorUtility.SetDirty(rt);
        return rt;
    }

    // 中継表示：全画面の映像＋左上の局＋右上の卓＋下の4人の帯。
    // 帯の上の待ち牌は解説表示の行と同じ内部座標（SpectatorMonitor.waitTile* と対）で組む。
    // 中継表示：全画面の映像＋左上の局＋右上の卓＋下の4人の帯。
    // 見た目は Mリーグ系の中継（参考動画）に寄せた：帯は映像に直接載せる透過（地の板なし）、
    // 人ごとの色の顔の台＋名前＋色のグラデの点数板。文字は黒縁。左上は枠付きの局＋棒と山の記号。
    // 帯の上の待ち牌は解説表示の行と同じ内部座標（SpectatorMonitor.waitTile* と対）で組む。
    static void BuildBroadcast(Transform board, Refs r)
    {
        var bc = NewRect(board, "Broadcast", Vector2.zero, CanvasSize);
        r.broadcastRoot = bc.gameObject;
        r.broadcastImage = Raw(bc, "Video", Vector2.zero, CanvasSize);

        // 映像の上の表示はすべて HUD の下へ（立直の演出中はまとめて消す）
        var hud = NewRect(bc, "Hud", Vector2.zero, CanvasSize);
        r.bcHud = hud.gameObject.AddComponent<CanvasGroup>();
        r.bcHud.interactable = false;
        r.bcHud.blocksRaycasts = false;
        bc = hud;

        var gradH = EnsureGradientTexture(GradHPath);
        var gradV = EnsureGradientTexture(GradVPath);
        var outline = EnsureOutlineMaterial();
        var rounded = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");

        // 左上：白い枠の局＋供託棒・本場棒・山の記号と数。まとめて画面の左上の角を中心に拡大する
        // （子の座標は Canvas と同じ＝大きさは TopLeftScale だけで変えられる）
        var tl = NewRect(bc, "TopLeft", Vector2.zero, CanvasSize);
        tl.pivot = new Vector2(0f, 1f);
        tl.anchoredPosition = new Vector2(-CanvasSize.x * 0.5f, CanvasSize.y * 0.5f);
        tl.localScale = Vector3.one * TopLeftScale;
        var roundFrame = Img(tl, "RoundBox", new Vector2(-838f, 470f), new Vector2(196f, 76f), Color.white);
        roundFrame.sprite = rounded; roundFrame.type = Image.Type.Sliced;
        // 中の地は不透明の黒。白の上に半透明の黒を重ねると、リニア色空間の合成で明るい灰色に見える
        Img(roundFrame.transform, "Fill", Vector2.zero, new Vector2(186f, 66f), new Color(0.03f, 0.03f, 0.05f, 1f));
        r.bcRound = TxtO(roundFrame.transform, "Round", new Vector2(0f, 1f), new Vector2(180f, 64f), 42f, Color.white, TextAlignmentOptions.Center, outline);

        // 供託（1000点棒：真ん中に赤い点）と本場（100点棒：黒い点が並ぶ）
        var stick1 = Img(tl, "KyotakuStick", new Vector2(-694f, 486f), new Vector2(64f, 11f), Color.white);
        Img(stick1.transform, "Dot", Vector2.zero, new Vector2(9f, 9f), ReachRed);
        r.bcKyotaku = TxtO(tl, "Kyotaku", new Vector2(-636f, 486f), new Vector2(40f, 34f), 28f, Color.white, TextAlignmentOptions.Left, outline);
        var stick2 = Img(tl, "HonbaStick", new Vector2(-694f, 454f), new Vector2(64f, 11f), Color.white);
        for (int k = 0; k < 4; ++k)
        {
            float dx = k < 2 ? -20f + k * 8f : 12f + (k - 2) * 8f;
            Img(stick2.transform, "Dot" + k, new Vector2(dx, 0f), new Vector2(5f, 5f), new Color(0.1f, 0.1f, 0.1f, 1f));
        }
        r.bcSub = TxtO(tl, "Honba", new Vector2(-636f, 454f), new Vector2(40f, 34f), 28f, Color.white, TextAlignmentOptions.Left, outline);
        // 山（縦線の入った小箱）と残り枚数
        var wall = Img(tl, "WallIcon", new Vector2(-590f, 470f), new Vector2(30f, 48f), Color.white);
        Img(wall.transform, "Fill", Vector2.zero, new Vector2(24f, 42f), new Color(0.03f, 0.03f, 0.05f, 1f));
        for (int k = 0; k < 3; ++k)
        {
            Img(wall.transform, "Line" + k, new Vector2(-6f + k * 6f, 0f), new Vector2(2f, 36f), Color.white);
        }
        r.bcWall = TxtO(tl, "Wall", new Vector2(-532f, 470f), new Vector2(70f, 40f), 30f, Color.white, TextAlignmentOptions.Left, outline);

        // 右上：卓番号と種類（地なし・黒縁）
        r.bcTable = TxtO(bc, "Table", new Vector2(770f, 486f), new Vector2(340f, 56f), 34f, Color.white, TextAlignmentOptions.Right, outline);

        // 下：4人の帯（行0..3＝起家からの席順で固定）。地の板は置かず映像に直接載せる。
        // 帯と待ち牌は Band にまとめる（和了者パネルを出す間は帯ごと消す）
        var bandRoot = NewRect(bc, "Band", Vector2.zero, CanvasSize);
        r.bcBandGroup = bandRoot.gameObject.AddComponent<CanvasGroup>();
        r.bcBandGroup.interactable = false;
        r.bcBandGroup.blocksRaycasts = false;
        for (int row = 0; row < 4; ++row)
        {
            float x = BandX0 + row * BandStep;
            var card = NewRect(bandRoot, "Card" + row, new Vector2(x, BandY), new Vector2(BandW, BandH));
            r.bcCards.Add(card.gameObject);
            r.bcCardGroups.Add(AnimGroup(card.gameObject));
            Color col = BandDefaultColor;

            // 顔の台（人の色・上が明るい）＋立ち絵
            var face = Raw(card, "PortraitBg", new Vector2(PortraitX, 0f), new Vector2(PortraitW, BandH));
            face.texture = gradV;
            face.color = col;
            r.bcPortraitBgs.Add(face);
            r.bcPortraits.Add(Raw(card, "Portrait", new Vector2(PortraitX, -2f), new Vector2(110f, PortraitH)));

            // 点数板（人の色・左が暗く右が明るい）＋点数
            var plate = Raw(card, "Plate", new Vector2(PlateX, PlateY), new Vector2(PlateW, PlateH));
            plate.texture = gradH;
            plate.color = col;
            r.bcPlates.Add(plate);
            r.bcPoint.Add(TxtO(card, "Points", new Vector2(PlateX - 6f, PlateY + 1f), new Vector2(PlateW - 24f, PlateH), 56f, Color.white, TextAlignmentOptions.Right, outline));

            // 手番：点数板の下の赤い線
            var turn = Img(card, "TurnBar", new Vector2(PlateX, PlateY - PlateH * 0.5f - 5f), new Vector2(PlateW, 5f), ReachRed);
            // 左端を軸にする（SpectatorMonitor が横の大きさで左から伸ばす）
            turn.rectTransform.pivot = new Vector2(0f, 0.5f);
            turn.rectTransform.anchoredPosition = new Vector2(PlateX - PlateW * 0.5f, PlateY - PlateH * 0.5f - 5f);
            turn.gameObject.SetActive(false);
            r.bcTurn.Add(turn.gameObject);

            // 名前と風（点数移動の間はまとめて消す）
            var labels = NewRect(card, "Labels", Vector2.zero, new Vector2(BandW, BandH));
            r.bcLabelGroups.Add(labels.gameObject.AddComponent<CanvasGroup>());
            r.bcWind.Add(TxtO(labels, "Wind", new Vector2(PlateX - 12f, 62f), new Vector2(PlateW - 24f, 28f), 22f, new Color(0.88f, 0.88f, 0.88f, 1f), TextAlignmentOptions.Right, outline));
            var name = TxtO(labels, "Name", new Vector2(PlateX + 18f, 28f), new Vector2(PlateW - 84f, 56f), 32f, Color.white, TextAlignmentOptions.Right, outline);
            name.overflowMode = TextOverflowModes.Ellipsis;
            r.bcName.Add(name);

            // 立直棒（名前の左。点数移動の間は名前と一緒に消える）
            var stick = Img(labels, "Riichi", new Vector2(PlateX - PlateW * 0.5f + 34f, 28f), new Vector2(60f, 10f), Color.white);
            Img(stick.transform, "Dot", Vector2.zero, new Vector2(9f, 9f), ReachRed);
            stick.gameObject.SetActive(false);
            r.bcRiichi.Add(stick.gameObject);

            // 点数の増減（名前の位置に出る）
            var delta = TxtO(card, "Delta", new Vector2(PlateX - 6f, DeltaY), new Vector2(PlateW - 24f, 50f), 44f, Color.white, TextAlignmentOptions.Right, outline);
            delta.gameObject.SetActive(false);
            r.bcDelta.Add(delta);
            // 上段（供託・立直棒）。例：和了者は +1,000 の下に +12,000
            var deltaSub = TxtO(card, "DeltaSub", new Vector2(PlateX - 6f, DeltaSubY), new Vector2(PlateW - 24f, 46f), 40f, Color.white, TextAlignmentOptions.Right, outline);
            deltaSub.gameObject.SetActive(false);
            r.bcDeltaSub.Add(deltaSub);

            // 待ち牌（参考動画「待ち牌UI」）：帯の上端・右寄せの暗い細帯に、小さな牌 → 縦線 → 山に残っている枚数。
            // 中の座標は左下原点（SpectatorMonitor.bcWait* と対）。牌は右詰めで左へ伸びる
            var wait = NewRect(card, "Wait", new Vector2(BandW * 0.5f - WaitStripW * 0.5f, WaitStripY), new Vector2(WaitStripW, WaitStripH));
            r.bcWaitRoots.Add(wait.gameObject);
            r.bcWaitGroups.Add(AnimGroup(wait.gameObject));
            var waitBg = Raw(wait, "Bg", Vector2.zero, new Vector2(WaitStripW, WaitStripH));
            var waitBgRt = waitBg.rectTransform;
            waitBgRt.anchorMin = waitBgRt.anchorMax = new Vector2(1f, 0.5f);
            waitBgRt.pivot = new Vector2(1f, 0.5f);
            waitBgRt.anchoredPosition = Vector2.zero;
            waitBg.texture = gradH;
            waitBg.color = new Color(0f, 0f, 0f, 0.62f);
            r.bcWaitBgs.Add(waitBgRt);
            Img(wait, "Divider", new Vector2(WaitStripW * 0.5f - 58f, 0f), new Vector2(3f, 34f), new Color(1f, 1f, 1f, 0.9f));
            for (int k = 0; k < SpectatorMonitor.WAIT_MAX; ++k)
            {
                r.bcWaitTiles.Add(RawBL(wait, "W" + k, Vector2.zero, new Vector2(27f, 40f)));
            }
            r.bcWaitRemain.Add(TxtO(wait, "Remain", new Vector2(WaitStripW * 0.5f - 28f, 1f), new Vector2(52f, WaitStripH), 36f, Color.white, TextAlignmentOptions.Center, outline));
            wait.gameObject.SetActive(false);
        }

        BuildWinPanel(bc, r, gradH, gradV, outline);
        BuildFinalPanel(bc, r, gradH, gradV, outline, rounded);
    }

    // 終局の最終結果（中継表示）：暗幕の上に「FINAL RESULT」と 4 行（行 0＝1位）。行は順位・立ち絵・名前・持ち点・開始からの増減。
    // 中身と動きは SpectatorMonitor.BeginFinal / StepFinal（4位から順にせり上がる）
    // ============================================================
    // 役満通知（「◯卓 役満確定／役満チャンス」）。上の中央・中継と解説の両方で最前面
    // 中継表示の左上の局（1.4倍＝右端 x-312）と右上の卓番号（左端 x600）の間に収める。
    // オーラスの逆転条件（中継で y270〜450）とは下端が少し重なるが、6秒だけ上に被せる
    // ============================================================
    static readonly Vector2 YakumanAlertPos = new Vector2(150f, 452f);
    static readonly Vector2 YakumanAlertSize = new Vector2(780f, 112f);

    static void BuildYakumanAlert(Transform canvasT, Refs r)
    {
        var back = Img(canvasT, "YakumanAlert", YakumanAlertPos, YakumanAlertSize, new Color(0.50f, 0.03f, 0.05f, 0.95f));
        r.yakumanAlertRoot = back.gameObject;
        r.yakumanAlertBack = back;
        r.yakumanAlertGroup = AnimGroup(back.gameObject);
        float w = YakumanAlertSize.x;
        float h = YakumanAlertSize.y;
        Img(back.transform, "TopLine", new Vector2(0f, h * 0.5f - 2f), new Vector2(w, 4f), Gold);
        Img(back.transform, "BottomLine", new Vector2(0f, -h * 0.5f + 2f), new Vector2(w, 4f), Gold);
        Img(back.transform, "Divider", new Vector2(-w * 0.5f + 330f, 0f), new Vector2(3f, h - 28f), new Color(Gold.r, Gold.g, Gold.b, 0.6f));
        r.yakumanAlertTitle = Txt(back.transform, "Title", new Vector2(-w * 0.5f + 168f, 0f), new Vector2(310f, h - 16f), 54f, Gold, TextAlignmentOptions.Center);
        r.yakumanAlertTitle.fontStyle = FontStyles.Bold;
        r.yakumanAlertTitle.enableAutoSizing = true;
        r.yakumanAlertTitle.fontSizeMin = 30f;
        r.yakumanAlertTitle.fontSizeMax = 54f;
        r.yakumanAlertBody = Txt(back.transform, "Body", new Vector2(-w * 0.5f + 350f + 205f, 0f), new Vector2(410f, h - 16f), 30f, Color.white, TextAlignmentOptions.Left);
        r.yakumanAlertBody.richText = true;
        r.yakumanAlertBody.lineSpacing = -8f;
        r.yakumanAlertTitle.text = "役満確定";
        r.yakumanAlertBody.text = "1卓　東家 Name\n大三元";
        back.gameObject.SetActive(false);
    }

    // ============================================================
    // 録画モードの板：手持ちカメラのレンズ直前に置く中継映像。実行時に SpectatorMonitor が毎フレーム動かす。
    // 本人の視界からは SpectatorRecordLayer を外すので、ストリームカメラの出力にだけ写る
    // ============================================================
    // 録画モードの合成カメラ：モニターの Canvas（中継画面）を正面 1cm から正投影で撮り、録画の板へ映す RT に描く。
    // 奥行きは 1.5cm だけ（Canvas の 2cm 奥にある VVMW の画面や、前を通る人は写らない）。操作帯は別の Canvas なので写らない
    const float RecordCamOffset = 0.01f;

    static Camera EnsureRecordCamera(Transform rootT, RenderTexture rt)
    {
        DestroyChild(rootT, "RecordCamera");
        var go = new GameObject("RecordCamera");
        Undo.RegisterCreatedObjectUndo(go, "Build Spectator Monitor");
        go.transform.SetParent(rootT, false);
        // Canvas は -forward 側から読む（+forward を向いて見る）
        go.transform.localPosition = new Vector3(0f, 0f, -RecordCamOffset);
        go.transform.localRotation = Quaternion.identity;
        var cam = go.AddComponent<Camera>();
        cam.orthographic = true;
        cam.orthographicSize = CanvasSize.y * CanvasScale * 0.5f;
        cam.aspect = CanvasSize.x / CanvasSize.y;
        cam.nearClipPlane = 0.001f;
        cam.farClipPlane = RecordCamOffset + 0.005f;
        cam.cullingMask = 1 << 0;   // モニターの Canvas は Default
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Color.black;
        cam.allowHDR = false;
        cam.allowMSAA = false;
        cam.useOcclusionCulling = false;
        cam.depth = -50f;
        cam.targetTexture = rt;
        cam.enabled = false;        // 録画中・カメラが開いている間だけ SpectatorMonitor が動かす
        return cam;
    }

    static Transform EnsureRecordPlate(Transform rootT, RenderTexture wideRt)
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(RecordPlateMatPath);
        if (mat == null)
        {
            mat = new Material(Shader.Find("Unlit/Texture"));
            AssetDatabase.CreateAsset(mat, RecordPlateMatPath);
        }
        mat.shader = Shader.Find("Unlit/Texture");
        mat.mainTexture = wideRt;
        mat.renderQueue = 4000;  // 最後に描く
        EditorUtility.SetDirty(mat);

        DestroyChild(rootT, "RecordPlate");
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Undo.RegisterCreatedObjectUndo(go, "Build Spectator Monitor");
        go.name = "RecordPlate";
        var col = go.GetComponent<Collider>();
        if (col != null) { Object.DestroyImmediate(col); }
        go.transform.SetParent(rootT, false);
        go.layer = SpectatorRecordLayer;
        var mr = go.GetComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        go.SetActive(false);
        return go.transform;
    }

    static void BuildFinalPanel(Transform bc, Refs r, Texture2D gradH, Texture2D gradV, Material outline, Sprite rounded)
    {
        var root = NewRect(bc, "FinalPanel", Vector2.zero, CanvasSize);
        r.finalRoot = root.gameObject;
        r.finalGroup = AnimGroup(root.gameObject);
        Img(root, "Backdrop", Vector2.zero, CanvasSize, new Color(0f, 0f, 0f, 0.55f));
        r.finalTitle = TxtO(root, "Title", new Vector2(0f, 385f), new Vector2(1200f, 84f), 60f, Gold, TextAlignmentOptions.Center, outline);
        r.finalTitle.characterSpacing = 10f;
        r.finalTitle.text = "FINAL RESULT";
        for (int i = 0; i < 4; ++i)
        {
            var row = NewRect(root, "Row" + i, new Vector2(0f, 220f - i * 170f), new Vector2(1320f, 150f));
            r.finalRowRts.Add(row);
            r.finalRowGroups.Add(AnimGroup(row.gameObject));
            r.finalRanks.Add(TxtO(row, "Rank", new Vector2(-560f, -4f), new Vector2(170f, 90f), 64f, Color.white, TextAlignmentOptions.Center, outline));
            var badge = Img(row, "TopBadge", new Vector2(-560f, 56f), new Vector2(96f, 34f), Gold);
            badge.sprite = rounded; badge.type = Image.Type.Sliced;
            var badgeText = TxtO(badge.transform, "Text", Vector2.zero, new Vector2(96f, 34f), 26f, new Color(0.08f, 0.06f, 0.02f, 1f), TextAlignmentOptions.Center, null);
            badgeText.text = "TOP";
            badge.gameObject.SetActive(i == 0);
            r.finalTopBadges.Add(badge.gameObject);
            var faceBg = Raw(row, "PortraitBg", new Vector2(-405f, 0f), new Vector2(130f, 150f));
            faceBg.texture = gradV;
            faceBg.color = BandDefaultColor;
            r.finalPortraitBgs.Add(faceBg);
            r.finalPortraits.Add(Raw(row, "Portrait", new Vector2(-405f, -2f), new Vector2(110f, 140f)));
            var plate = Raw(row, "Plate", new Vector2(95f, 0f), new Vector2(860f, 112f));
            plate.texture = gradH;
            plate.color = BandDefaultColor;
            r.finalPlates.Add(plate);
            var name = TxtO(row, "Name", new Vector2(-95f, 0f), new Vector2(440f, 64f), 40f, Color.white, TextAlignmentOptions.Left, outline);
            name.overflowMode = TextOverflowModes.Ellipsis;
            name.enableWordWrapping = false;
            r.finalNames.Add(name);
            r.finalPoints.Add(TxtO(row, "Points", new Vector2(345f, 0f), new Vector2(300f, 84f), 62f, Color.white, TextAlignmentOptions.Right, outline));
            r.finalDiffs.Add(TxtO(row, "Diff", new Vector2(640f, 0f), new Vector2(220f, 60f), 38f, Color.white, TextAlignmentOptions.Left, outline));
        }
        root.gameObject.SetActive(false);
    }

    // 和了者のパネル（参考動画「点数移動UI」）：数え終わったら帯を消し、行0 の位置に和了者 1 人の帯、
    // その右に役の一覧と縦線・「合計」。中身は SpectatorMonitor.PrepareWinPanel が帯のその人の行から写す
    static void BuildWinPanel(Transform bc, Refs r, Texture2D gradH, Texture2D gradV, Material outline)
    {
        var root = NewRect(bc, "WinPanel", Vector2.zero, CanvasSize);
        r.winRoot = root.gameObject;
        r.winGroup = root.gameObject.AddComponent<CanvasGroup>();
        r.winGroup.interactable = false;
        r.winGroup.blocksRaycasts = false;

        var card = NewRect(root, "Card", new Vector2(BandX0, BandY), new Vector2(BandW, BandH));
        r.winPortraitBg = Raw(card, "PortraitBg", new Vector2(PortraitX, 0f), new Vector2(PortraitW, BandH));
        r.winPortraitBg.texture = gradV;
        r.winPortraitBg.color = BandDefaultColor;
        r.winPortrait = Raw(card, "Portrait", new Vector2(PortraitX, -2f), new Vector2(110f, PortraitH));
        r.winPlate = Raw(card, "Plate", new Vector2(PlateX, PlateY), new Vector2(PlateW, PlateH));
        r.winPlate.texture = gradH;
        r.winPlate.color = BandDefaultColor;
        r.winPoints = TxtO(card, "Points", new Vector2(PlateX - 6f, PlateY + 1f), new Vector2(PlateW - 24f, PlateH), 56f, Color.white, TextAlignmentOptions.Right, outline);
        r.winWind = TxtO(card, "Wind", new Vector2(PlateX - 12f, 62f), new Vector2(PlateW - 24f, 28f), 22f, new Color(0.88f, 0.88f, 0.88f, 1f), TextAlignmentOptions.Right, outline);
        var name = TxtO(card, "Name", new Vector2(PlateX + 18f, 28f), new Vector2(PlateW - 84f, 56f), 32f, Color.white, TextAlignmentOptions.Right, outline);
        name.overflowMode = TextOverflowModes.Ellipsis;
        r.winName = name;

        // 役の一覧（帯の右）→ 縦線 → 「合計」と翻・点数の格
        var yaku = NewRect(root, "Yaku", Vector2.zero, CanvasSize);
        r.winYakuGroup = yaku.gameObject.AddComponent<CanvasGroup>();
        float left = BandX0 + BandW * 0.5f + 60f;
        const float yakuW = 920f;
        r.winYakuText = TxtO(yaku, "List", new Vector2(left + yakuW * 0.5f, BandY + 6f), new Vector2(yakuW, 60f), 36f, Color.white, TextAlignmentOptions.Left, outline);
        r.winYakuText.overflowMode = TextOverflowModes.Ellipsis;
        r.winYakuText.enableAutoSizing = true;
        r.winYakuText.fontSizeMin = 22f;
        r.winYakuText.fontSizeMax = 36f;
        float divX = left + yakuW + 40f;
        Img(yaku, "Divider", new Vector2(divX, BandY + 4f), new Vector2(3f, 120f), new Color(1f, 1f, 1f, 0.85f));
        r.winTotalLabel = TxtO(yaku, "TotalLabel", new Vector2(divX + 110f, BandY + 40f), new Vector2(180f, 36f), 24f, Color.white, TextAlignmentOptions.Left, outline);
        r.winTotalText = TxtO(yaku, "Total", new Vector2(divX + 200f, BandY - 14f), new Vector2(360f, 64f), 44f, Color.white, TextAlignmentOptions.Left, outline);
        root.gameObject.SetActive(false);
    }

    // 立直の演出（Mリーグの中継：帯を全部消した引きの画の真ん中に、白い千点棒 → 棒が縮んで
    // グリッチで「REACH」に化ける）。動画のチーム紋章に当たる絵は出さない（ユーザー指示）。動きは SpectatorMonitor.UpdateRiichiFx
    static void BuildRiichiFx(Transform board, Refs r)
    {
        var outline = EnsureOutlineMaterial();
        var knob = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd");
        var rounded = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");

        var root = NewRect(board, "RiichiFx", Vector2.zero, CanvasSize);
        r.riichiFxRoot = root.gameObject;
        var grp = root.gameObject.AddComponent<CanvasGroup>();
        grp.interactable = false;
        grp.blocksRaycasts = false;

        // 白い千点棒（真ん中に赤い点）
        var stick = Img(root, "Stick", new Vector2(0f, 0f), new Vector2(440f, 40f), Color.white);
        stick.sprite = rounded; stick.type = Image.Type.Sliced;
        Img(stick.transform, "Dot", Vector2.zero, new Vector2(22f, 22f), ReachRed).sprite = knob;
        r.riichiFxStick = stick.rectTransform;
        r.riichiFxStickGroup = stick.gameObject.AddComponent<CanvasGroup>();

        // グリッチの小箱（棒が消える瞬間に一瞬だけ）
        r.riichiFxBlock = Img(root, "Block", new Vector2(0f, 10f), new Vector2(46f, 52f), new Color(0.35f, 0.85f, 0.65f, 0.75f)).gameObject;

        // 残像（大きめ・緑がかった半透明）と本文字
        var ghost = Txt(root, "Ghost", new Vector2(0f, 0f), new Vector2(1400f, 220f), 170f, new Color(0.40f, 0.95f, 0.75f, 0.45f), TextAlignmentOptions.Center);
        ghost.fontStyle = FontStyles.Bold;
        ghost.characterSpacing = 12f;
        ghost.enableWordWrapping = false;   // DOUBLE REACH / OPEN REACH を折り返さない
        r.riichiFxGhost = ghost;
        var text = TxtO(root, "Text", new Vector2(0f, 0f), new Vector2(1400f, 220f), 140f, Color.white, TextAlignmentOptions.Center, outline);
        text.fontStyle = FontStyles.Bold;
        text.characterSpacing = 12f;
        text.enableWordWrapping = false;
        r.riichiFxText = text;

        // 横に走る線（左右に2本ずつ。白と緑）
        float[] ys = { 26f, -26f, 14f, -14f };
        for (int k = 0; k < 4; ++k)
        {
            Color lc = k % 2 == 0 ? new Color(1f, 1f, 1f, 0.9f) : new Color(0.35f, 0.9f, 0.7f, 0.8f);
            var line = Img(root, "Line" + k, new Vector2(0f, ys[k]), new Vector2(300f, 3f), lc);
            r.riichiFxLines.Add(line.rectTransform);
        }
        root.gameObject.SetActive(false);
    }


    // 黒縁の文字（映像の上でも読めるように）
    static TextMeshProUGUI TxtO(Transform parent, string name, Vector2 pos, Vector2 size, float fontSize, Color color,
                                TextAlignmentOptions align, Material outline)
    {
        var t = Txt(parent, name, pos, size, fontSize, color, align);
        if (outline != null) { t.fontSharedMaterial = outline; }
        return t;
    }

    // 黒縁の TMP マテリアル（フォントの既定マテリアルの複製＋縁）。文字ごとに作るとバッチが割れるので 1 つを共有
    static Material EnsureOutlineMaterial()
    {
        if (font_ == null || font_.material == null) { return null; }
        var m = AssetDatabase.LoadAssetAtPath<Material>(OutlineMatPath);
        if (m == null)
        {
            m = new Material(font_.material);
            AssetDatabase.CreateAsset(m, OutlineMatPath);
        }
        m.shader = font_.material.shader;
        m.SetTexture("_MainTex", font_.material.GetTexture("_MainTex"));
        m.EnableKeyword("OUTLINE_ON");
        m.SetFloat("_OutlineWidth", 0.24f);
        m.SetColor("_OutlineColor", new Color(0f, 0f, 0f, 1f));
        m.SetFloat("_FaceDilate", 0.12f);
        EditorUtility.SetDirty(m);
        return m;
    }

    // 帯のグラデ素材（白の明暗＋α。色は RawImage.color で掛ける）。小さいので非圧縮・ミップなし
    static Texture2D EnsureGradientTexture(string path)
    {
        var imp = AssetImporter.GetAtPath(path) as TextureImporter;
        if (imp == null) { Debug.LogWarning("[BuildSpectatorMonitor] " + path + " がありません（帯が単色になります）。"); return null; }
        bool dirty = false;
        if (imp.textureType != TextureImporterType.Default) { imp.textureType = TextureImporterType.Default; dirty = true; }
        if (imp.mipmapEnabled) { imp.mipmapEnabled = false; dirty = true; }
        if (imp.wrapMode != TextureWrapMode.Clamp) { imp.wrapMode = TextureWrapMode.Clamp; dirty = true; }
        if (!imp.alphaIsTransparency) { imp.alphaIsTransparency = true; dirty = true; }
        if (imp.npotScale != TextureImporterNPOTScale.None) { imp.npotScale = TextureImporterNPOTScale.None; dirty = true; }
        if (imp.textureCompression != TextureImporterCompression.Uncompressed) { imp.textureCompression = TextureImporterCompression.Uncompressed; dirty = true; }
        if (dirty) { imp.SaveAndReimport(); }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }


    static Camera EnsureCamera(Transform rootT, RenderTexture rt)
    {
        var t = rootT.Find("SpectatorCam");
        if (t == null)
        {
            var go = new GameObject("SpectatorCam");
            Undo.RegisterCreatedObjectUndo(go, "Build Spectator Monitor");
            go.transform.SetParent(rootT, false);
            t = go.transform;
        }
        var cam = t.GetComponent<Camera>();
        if (cam == null) { cam = t.gameObject.AddComponent<Camera>(); }
        // 普段は無効。観戦中・カメラ表示の時だけ SpectatorMonitor が必要なフレームだけ有効にする
        cam.enabled = false;
        cam.targetTexture = rt;
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 8f;          // 卓のまわりだけ描く（Quest 負荷）
        cam.fieldOfView = 48f;
        cam.clearFlags = CameraClearFlags.Skybox;
        cam.allowHDR = false;
        cam.allowMSAA = false;
        cam.useOcclusionCulling = true;
        cam.depth = -5f;
        cam.cullingMask = SpectatorCullingMask();
        var listener = t.GetComponent<AudioListener>();
        if (listener != null) { Object.DestroyImmediate(listener); }
        t.localPosition = new Vector3(0f, 0f, -0.5f);
        t.localRotation = Quaternion.identity;
        return cam;
    }

    // モニターの音源の親。以前はモニター本体に直接 AudioSource を付けていたので、残っていれば外す
    static Transform EnsureMonitorAudioRoot(GameObject rootGo)
    {
        var oldSpatial = rootGo.GetComponent<VRC.SDK3.Components.VRCSpatialAudioSource>();
        if (oldSpatial != null) { Object.DestroyImmediate(oldSpatial); }
        var oldSrc = rootGo.GetComponent<AudioSource>();
        if (oldSrc != null) { Object.DestroyImmediate(oldSrc); }
        var t = rootGo.transform.Find("MonitorAudio");
        if (t == null)
        {
            var go = new GameObject("MonitorAudio");
            Undo.RegisterCreatedObjectUndo(go, "Build Spectator Monitor");
            t = go.transform;
            t.SetParent(rootGo.transform, false);
        }
        t.localPosition = Vector3.zero;
        t.localRotation = Quaternion.identity;
        t.localScale = Vector3.one;
        return t;
    }

    // 声・効果音（Voice）と立直 BGM（Bgm・ループ）。どちらも 4m までは最大音量・25m で消える
    static AudioSource EnsureMonitorAudio(Transform parent, string name, bool loop)
    {
        var t = parent.Find(name);
        if (t == null)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Build Spectator Monitor");
            t = go.transform;
            t.SetParent(parent, false);
        }
        t.localPosition = Vector3.zero;
        var rootGo = t.gameObject;
        var src = rootGo.GetComponent<AudioSource>();
        if (src == null) { src = rootGo.AddComponent<AudioSource>(); }
        src.playOnAwake = false;
        src.loop = loop;
        src.spatialBlend = 1f;
        src.rolloffMode = AudioRolloffMode.Linear;
        src.minDistance = 4f;
        src.maxDistance = 25f;
        src.volume = 1f;
        var spatial = rootGo.GetComponent<VRC.SDK3.Components.VRCSpatialAudioSource>();
        if (spatial == null) { spatial = rootGo.AddComponent<VRC.SDK3.Components.VRCSpatialAudioSource>(); }
        spatial.Near = 4f;
        spatial.Far = 25f;
        spatial.EnableSpatialization = true;
        EditorUtility.SetDirty(src);
        EditorUtility.SetDirty(spatial);
        return src;
    }

    // ============================================================
    // 牌アトラスの実測（添え字＝牌ID>>2 ＝ (種類<<4)|(数字の枠)）
    // ============================================================
    static float[][] MeasureTileKeys(out float aspect)
    {
        aspect = 0.75f;
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(TilePrefabPath);
        if (prefab == null) { Debug.LogError("[BuildSpectatorMonitor] 卓 Prefab が見つかりません: " + TilePrefabPath); return null; }

        Transform tiles0 = null;
        foreach (var t in prefab.GetComponentsInChildren<Transform>(true))
        {
            if (t.name == "Tiles0") { tiles0 = t; break; }
        }
        if (tiles0 == null) { Debug.LogError("[BuildSpectatorMonitor] Prefab に Tiles0 がありません。"); return null; }

        var map = new Dictionary<string, Rect>();
        foreach (Transform c in tiles0)
        {
            var mf = c.GetComponent<MeshFilter>();
            var mr = c.GetComponent<MeshRenderer>();
            if (mf == null || mf.sharedMesh == null || mr == null) { continue; }
            int face = (mr.sharedMaterials != null && mr.sharedMaterials.Length >= 2) ? 1 : 0;
            var mesh = mf.sharedMesh;
            var uvs = mesh.uv;
            var tris = mesh.GetTriangles(face);
            if (tris.Length < 6) { continue; }
            float minU = 9f, minV = 9f, maxU = -9f, maxV = -9f;
            for (int i = 0; i < 6; ++i)
            {
                var uv = uvs[tris[i]];
                minU = Mathf.Min(minU, uv.x); maxU = Mathf.Max(maxU, uv.x);
                minV = Mathf.Min(minV, uv.y); maxV = Mathf.Max(maxV, uv.y);
            }
            map[c.name] = new Rect(minU, minV, maxU - minU, maxV - minV);
        }

        var u = new float[64]; var v = new float[64]; var w = new float[64]; var h = new float[64];
        var missing = new List<string>();
        int found = 0;
        for (int key = 0; key < 64; ++key)
        {
            string name = KeyName(key);
            if (name == null) { continue; }
            Rect rc;
            if (!map.TryGetValue(name, out rc)) { missing.Add(name); continue; }
            u[key] = rc.x; v[key] = rc.y; w[key] = rc.width; h[key] = rc.height;
            ++found;
        }
        if (missing.Count > 0) { Debug.LogError("[BuildSpectatorMonitor] 牌の実測に失敗: " + string.Join(", ", missing)); return null; }

        // 牌1枚の見た目の縦横比。NPOT の取り込み丸めに引きずられないよう元画像の寸法で測る。
        int texW = 0, texH = 0;
        var importer = AssetImporter.GetAtPath(TileTexturePath) as TextureImporter;
        var m = typeof(TextureImporter).GetMethod("GetWidthAndHeight", BindingFlags.NonPublic | BindingFlags.Instance);
        if (importer != null && m != null)
        {
            var args = new object[] { 0, 0 };
            m.Invoke(importer, args);
            texW = (int)args[0]; texH = (int)args[1];
        }
        int m1 = (0 << 4) | 1;
        if (texW > 0 && texH > 0 && h[m1] > 0f) { aspect = (w[m1] * texW) / (h[m1] * texH); }

        Debug.Log("[BuildSpectatorMonitor] 牌 " + found + " 種を実測（元画像 " + texW + "x" + texH + "）");
        return new[] { u, v, w, h };
    }

    // Mahjong.GetTileId / GetTileName と同じ符号化：slot 1..5＝1..5、6＝赤5、7..10＝6..9（字牌は 1..5,7,8）
    static string KeyName(int key)
    {
        int type = key >> 4;
        int slot = key & 15;
        if (type < 3)
        {
            if (slot < 1 || slot > 10) { return null; }
            string p = type == 0 ? "M" : type == 1 ? "P" : "S";
            if (slot == 6) { return p + "5R"; }
            return p + (slot >= 7 ? slot - 1 : slot);
        }
        switch (slot)
        {
            case 1: return "Ton";
            case 2: return "Nan";
            case 3: return "Sha";
            case 4: return "Pey";
            case 5: return "Haku";
            case 7: return "Hatsu";
            case 8: return "Chun";
        }
        return null;
    }

    // ============================================================
    // U# コンポーネント
    // ============================================================
    static bool EnsureProgramAsset()
    {
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(ScriptPath);
        if (script == null) { Debug.LogError("[BuildSpectatorMonitor] スクリプトが見つかりません: " + ScriptPath); return false; }
        var type = script.GetClass();
        if (type != null && UdonSharpProgramAsset.GetProgramAssetForClass(type) != null) { return false; }
        if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(ProgramAssetPath) != null) { return false; }

        var programAsset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
        programAsset.sourceCsScript = script;
        AssetDatabase.CreateAsset(programAsset, ProgramAssetPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[BuildSpectatorMonitor] U#プログラムアセットを生成: " + ProgramAssetPath);
        return true;
    }

    static SpectatorMonitor EnsureComponent(GameObject go)
    {
        var comp = go.GetComponent<SpectatorMonitor>();
        if (comp != null)
        {
            var existing = UdonSharpEditorUtility.GetBackingUdonBehaviour(comp);
            if (existing != null && existing.programSource != null) { return comp; }
            Object.DestroyImmediate(comp);
            foreach (var ub in go.GetComponents<UdonBehaviour>())
            {
                if (ub.programSource == null) { Object.DestroyImmediate(ub); }
            }
        }
        var added = (SpectatorMonitor)UdonSharpUndo.AddComponent(go, typeof(SpectatorMonitor));
        if (added == null || UdonSharpEditorUtility.GetBackingUdonBehaviour(added) == null)
        {
            Debug.LogError("[BuildSpectatorMonitor] SpectatorMonitor を追加できません（U#未コンパイル？）。コンパイル後に再実行してください。");
            if (added != null) { Object.DestroyImmediate(added); }
            return null;
        }
        return added;
    }

    static void WireButton(Button button, UdonBehaviour udon, string eventName)
    {
        while (button.onClick.GetPersistentEventCount() > 0) { UnityEventTools.RemovePersistentListener(button.onClick, 0); }
        UnityEventTools.AddStringPersistentListener(button.onClick, udon.SendCustomEvent, eventName);
        EditorUtility.SetDirty(button);
    }

    // ============================================================
    // UI 部品
    // ============================================================
    static void DestroyChild(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null) { Undo.DestroyObjectImmediate(t.gameObject); }
    }

    static RectTransform NewRect(Transform parent, string name, Vector2 pos, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        Undo.RegisterCreatedObjectUndo(go, "Build Spectator Monitor");
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        return rt;
    }

    // 左下原点（行の中身用）
    static RectTransform NewRectBL(Transform parent, string name, Vector2 pos, Vector2 size)
    {
        var rt = NewRect(parent, name, pos, size);
        rt.anchorMin = rt.anchorMax = Vector2.zero;
        rt.anchoredPosition = pos;
        return rt;
    }

    // アニメーションのフェード用（押せる部品ではないのでレイキャストは通す）
    static CanvasGroup AnimGroup(GameObject go)
    {
        var g = go.AddComponent<CanvasGroup>();
        g.interactable = false;
        g.blocksRaycasts = false;
        return g;
    }

    static Image Img(Transform parent, string name, Vector2 pos, Vector2 size, Color color)
    {
        var rt = NewRect(parent, name, pos, size);
        var img = rt.gameObject.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    static RawImage Raw(Transform parent, string name, Vector2 pos, Vector2 size)
    {
        var rt = NewRect(parent, name, pos, size);
        var img = rt.gameObject.AddComponent<RawImage>();
        img.raycastTarget = false;
        return img;
    }

    static RawImage RawBL(Transform parent, string name, Vector2 pos, Vector2 size)
    {
        var rt = NewRectBL(parent, name, pos, size);
        var img = rt.gameObject.AddComponent<RawImage>();
        img.raycastTarget = false;
        return img;
    }

    static TextMeshProUGUI Txt(Transform parent, string name, Vector2 pos, Vector2 size, float fontSize, Color color, TextAlignmentOptions align)
    {
        var rt = NewRect(parent, name, pos, size);
        return SetupText(rt, fontSize, color, align);
    }

    static TextMeshProUGUI TxtBL(Transform parent, string name, Vector2 pos, Vector2 size, float fontSize, Color color, TextAlignmentOptions align)
    {
        var rt = NewRectBL(parent, name, pos, size);
        return SetupText(rt, fontSize, color, align);
    }

    static TextMeshProUGUI SetupText(RectTransform rt, float fontSize, Color color, TextAlignmentOptions align)
    {
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        if (font_ != null) { t.font = font_; t.fontSharedMaterial = font_.material; }
        t.fontSize = fontSize;
        t.color = color;
        t.alignment = align;
        t.enableWordWrapping = false;
        // Ellipsis は「高さが1行ぶんに足りない」だけでも全文を … に潰す（実測で風・点数が消えた）。
        // 既定ははみ出し表示にして、横に長くなりうる名前だけ呼び出し側で Ellipsis にする。
        t.overflowMode = TextOverflowModes.Overflow;
        t.raycastTarget = false;
        t.text = "";
        return t;
    }

    static Button Btn(Transform parent, string name, Vector2 pos, Vector2 size, float fontSize, string label,
                      out Image back, out TextMeshProUGUI text)
    {
        var rt = NewRect(parent, name, pos, size);
        back = rt.gameObject.AddComponent<Image>();
        back.color = new Color(0.12f, 0.17f, 0.30f, 1f);
        back.raycastTarget = true;
        var btn = rt.gameObject.AddComponent<Button>();
        btn.targetGraphic = back;
        var colors = btn.colors;
        colors.highlightedColor = new Color(1f, 1f, 1f, 1f);
        colors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
        btn.colors = colors;
        text = Txt(rt, "Text", Vector2.zero, size, fontSize, Color.white, TextAlignmentOptions.Center);
        text.text = label;
        return btn;
    }

    static void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform c in go.transform) { SetLayerRecursively(c.gameObject, layer); }
    }

    // ============================================================
    // 検証
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Validate Spectator Monitor")]
    public static void Validate()
    {
        var problems = new List<string>();
        var monitor = Object.FindObjectOfType<SpectatorMonitor>(true);
        if (monitor == null) { Debug.LogError("[ValidateSpectatorMonitor] SpectatorMonitor がシーンにありません。Build を実行してください。"); return; }

        if (monitor.matchmaking == null) { problems.Add("matchmaking が空"); }
        else if (monitor.matchmaking.spectatorMonitor != monitor) { problems.Add("MatchmakingManager.spectatorMonitor が未配線（カリングが配信卓を起こせない）"); }
        if (monitor.vvmwScreenRenderer == null) { problems.Add("vvmwScreenRenderer が空"); }
        if (monitor.vvmwOverlay == null) { problems.Add("vvmwOverlay が空"); }
        if (monitor.language == null) { problems.Add("language（KemonoLanguage）が空＝英語に切り替わらない"); }
        if (monitor.monitorRoot == null || monitor.boardRoot == null || monitor.idleText == null) { problems.Add("monitorRoot / boardRoot / idleText のどれかが空"); }
        if (monitor.tileTexture == null) { problems.Add("tileTexture が空"); }
        if (monitor.tileU == null || monitor.tileU.Length != 64) { problems.Add("牌の矩形表が 64 件でない"); }
        CheckArray(problems, "riverTiles", monitor.riverTiles, 4 * SpectatorMonitor.RIVER_MAX);
        CheckArray(problems, "rowTiles", monitor.rowTiles, 4 * SpectatorMonitor.ROW_MAX);
        CheckArray(problems, "seatBoxTexts", monitor.seatBoxTexts, 4);
        CheckArray(problems, "reachSticks", monitor.reachSticks, 4);
        CheckArray(problems, "rowRoots", monitor.rowRoots, 4);
        CheckArray(problems, "rowNameTexts", monitor.rowNameTexts, 4);
        CheckArray(problems, "rowPointTexts", monitor.rowPointTexts, 4);
        CheckArray(problems, "doraTiles", monitor.doraTiles, 5);
        CheckArray(problems, "tableButtonBacks", monitor.tableButtonBacks, 8);
        CheckArray(problems, "waitTiles", monitor.waitTiles, 4 * SpectatorMonitor.WAIT_MAX);
        CheckArray(problems, "waitRoots", monitor.waitRoots, 4);
        CheckArray(problems, "rowDiffTexts", monitor.rowDiffTexts, 4);
        if (monitor.spectatorCamera == null || monitor.spectatorCamera.targetTexture == null) { problems.Add("カメラか RenderTexture が空"); }
        else if (monitor.spectatorCamera.enabled) { problems.Add("カメラが有効のまま保存されている（常時描画になる）"); }
        if (monitor.cameraViewRoot == null || monitor.diagramRoot == null) { problems.Add("cameraViewRoot / diagramRoot が空"); }
        if (monitor.cutInRoot == null || monitor.cutInText == null || monitor.cutInGroup == null) { problems.Add("カットインの部品が空"); }
        if (monitor.callBadgeRoot == null || monitor.callBadgeText == null || monitor.callBadgeGroup == null) { problems.Add("小さな札（鳴き・北抜き）の部品が空"); }
        if (monitor.conditionRoot == null || monitor.conditionText == null) { problems.Add("条件パネルが空"); }
        if (monitor.monitorVoice == null) { problems.Add("monitorVoice が空"); }
        if (monitor.analysisRoot == null || monitor.broadcastRoot == null || monitor.broadcastImage == null) { problems.Add("中継表示／解説表示のルートが空"); }
        if (monitor.squareTexture == null || monitor.wideTexture == null || monitor.wideTextureQuest == null) { problems.Add("カメラの RenderTexture（正方形／16:9）が空"); }
        CheckArray(problems, "bcCards", monitor.bcCards, 4);
        CheckArray(problems, "bcPointTexts", monitor.bcPointTexts, 4);
        CheckArray(problems, "bcTurnBars", monitor.bcTurnBars, 4);
        CheckArray(problems, "bcLabelGroups", monitor.bcLabelGroups, 4);
        CheckArray(problems, "bcCardGroups", monitor.bcCardGroups, 4);
        CheckArray(problems, "bcWaitGroups", monitor.bcWaitGroups, 4);
        CheckArray(problems, "waitGroups", monitor.waitGroups, 4);
        if (monitor.conditionGroup == null) { problems.Add("conditionGroup が空"); }
        if (monitor.yakumanAlertRoot == null || monitor.yakumanAlertGroup == null || monitor.yakumanAlertBack == null
            || monitor.yakumanAlertTitle == null || monitor.yakumanAlertBody == null) { problems.Add("役満通知の部品が空"); }
        if (monitor.recordPlate == null || monitor.recordButtonBack == null || monitor.recordButtonText == null) { problems.Add("録画モードの部品が空"); }
        else if (monitor.recordPlate.gameObject.layer != SpectatorRecordLayer) { problems.Add("録画の板がレイヤー " + SpectatorRecordLayer + " にない"); }
        else if (monitor.recordPlate.gameObject.activeSelf) { problems.Add("録画の板が出たまま（起動時は隠す）"); }
        if (monitor.recordCamera == null || monitor.recordTexture == null || monitor.wideTextureRecord == null) { problems.Add("録画の合成カメラ／RT が空"); }
        if (monitor.monitorAudioRoot == null || monitor.monitorVoice == null || monitor.monitorBgm == null) { problems.Add("モニターの音源（MonitorAudio/Voice・Bgm）が空"); }
        else if (monitor.monitorVoice.transform.parent != monitor.monitorAudioRoot || monitor.monitorBgm.transform.parent != monitor.monitorAudioRoot) { problems.Add("モニターの音源が MonitorAudio の子にない（録画中に頭へ付いて回らない）"); }
        else if (!monitor.monitorBgm.loop || monitor.monitorBgm.playOnAwake) { problems.Add("立直 BGM の中継音源が loop でない／playOnAwake が ON"); }
        else if (monitor.recordCamera != null && monitor.recordCamera.enabled) { problems.Add("録画の合成カメラが ON のまま（起動時は OFF）"); }
        else if (monitor.recordCamera != null && monitor.recordCamera.targetTexture != monitor.recordTexture) { problems.Add("録画の合成カメラの描き先が違う"); }
        if (monitor.finalRoot == null || monitor.finalGroup == null) { problems.Add("終局の最終結果パネルが空"); }
        CheckArray(problems, "finalRowGroups", monitor.finalRowGroups, 4);
        CheckArray(problems, "finalRowRts", monitor.finalRowRts, 4);
        CheckArray(problems, "finalRankTexts", monitor.finalRankTexts, 4);
        CheckArray(problems, "finalNameTexts", monitor.finalNameTexts, 4);
        CheckArray(problems, "finalPointTexts", monitor.finalPointTexts, 4);
        CheckArray(problems, "finalDiffTexts", monitor.finalDiffTexts, 4);
        CheckArray(problems, "finalPortraits", monitor.finalPortraits, 4);
        CheckArray(problems, "finalPlates", monitor.finalPlates, 4);
        CheckArray(problems, "finalTopBadges", monitor.finalTopBadges, 4);
        CheckArray(problems, "bcPlates", monitor.bcPlates, 4);
        CheckArray(problems, "bcPortraitBgs", monitor.bcPortraitBgs, 4);
        CheckArray(problems, "riichiFxLines", monitor.riichiFxLines, 4);
        CheckArray(problems, "bcDeltaSubTexts", monitor.bcDeltaSubTexts, 4);
        if (monitor.bcBandGroup == null || monitor.winRoot == null || monitor.winYakuText == null) { problems.Add("点数移動の帯・和了者パネルの部品が空"); }
        if (monitor.riichiFxRoot == null || monitor.riichiFxText == null || monitor.riichiFxStick == null) { problems.Add("立直演出の部品が空"); }
        if (monitor.bcHud == null) { problems.Add("中継表示の HUD（立直演出中に消す帯）が空"); }
        CheckArray(problems, "bcWaitTiles", monitor.bcWaitTiles, 4 * SpectatorMonitor.WAIT_MAX);
        if (monitor.bcRoundText == null || monitor.bcSubText == null) { problems.Add("中継表示の局の表示が空"); }
        if (monitor.ronGlyph == null || monitor.tsumoGlyph == null) { problems.Add("ロン/ツモの焼き文字が空（文字表示に落ちる）"); }

        // 画面と同じ面・同じ向きにあるか（VVMW を動かしたのに Build し直していない検出）
        if (monitor.vvmwScreenRenderer != null)
        {
            var s = monitor.vvmwScreenRenderer.transform;
            var expect = s.position - s.forward * FrontOffset;
            if (Vector3.Distance(monitor.transform.position, expect) > 0.005f) { problems.Add("位置が VVMW の画面からずれている（Build し直す）"); }
            if (Quaternion.Angle(monitor.transform.rotation, s.rotation) > 0.5f) { problems.Add("向きが VVMW の画面と違う（Build し直す）"); }
        }

        // ボタン配線
        var udon = UdonSharpEditorUtility.GetBackingUdonBehaviour(monitor);
        var controls = monitor.transform.Find("Controls");
        if (controls == null) { problems.Add("Controls が無い"); }
        else
        {
            CheckButton(problems, controls, "ModeButton", udon);
            CheckButton(problems, controls, "Select/AutoButton", udon);
            CheckButton(problems, controls, "Select/ViewButton", udon);
            for (int i = 1; i <= 8; ++i) { CheckButton(problems, controls, "Select/Table" + i, udon); }
        }

        // 卓の本人向けの板が観戦カメラから外れているか
        if (monitor.spectatorCamera != null && (monitor.spectatorCamera.cullingMask & (1 << SpectatorHiddenLayer)) != 0) { problems.Add("観戦カメラがレイヤー " + SpectatorHiddenLayer + "（卓の本人向けの板）を写している＝Apply Spectator Hidden Layer"); }
        foreach (var tb in Object.FindObjectsOfType<Mahjong>(true))
        {
            foreach (var path in SpectatorHiddenPaths)
            {
                var t = tb.transform.Find(path);
                if (t == null) { problems.Add(tb.name + " に " + path + " が無い"); continue; }
                foreach (var c in t.GetComponentsInChildren<Transform>(true))
                {
                    if (c.gameObject.layer != SpectatorHiddenLayer) { problems.Add(tb.name + "/" + path + " の " + c.name + " がレイヤー " + c.gameObject.layer + "（観戦カメラに写る）＝Apply Spectator Hidden Layer"); break; }
                }
            }
        }
        var desc = Object.FindObjectOfType<VRC.SDKBase.VRC_SceneDescriptor>(true);
        if (desc != null && desc.ReferenceCamera != null)
        {
            var rc = desc.ReferenceCamera.GetComponent<Camera>();
            if (rc != null && (rc.cullingMask & (1 << SpectatorHiddenLayer)) == 0) { problems.Add("リファレンスカメラがレイヤー " + SpectatorHiddenLayer + " を写さない＝プレイヤーから卓の演出が消える"); }
        }

        if (problems.Count == 0) { Debug.Log("[ValidateSpectatorMonitor] 問題なし"); }
        else { Debug.LogError("[ValidateSpectatorMonitor] " + problems.Count + " 件\n- " + string.Join("\n- ", problems)); }
    }

    static void CheckArray<T>(List<string> problems, string name, T[] arr, int expect) where T : Object
    {
        if (arr == null || arr.Length != expect) { problems.Add(name + " が " + expect + " 件でない"); return; }
        for (int i = 0; i < arr.Length; ++i)
        {
            if (arr[i] == null) { problems.Add(name + "[" + i + "] が空"); return; }
        }
    }

    static void CheckButton(List<string> problems, Transform root, string path, UdonBehaviour udon)
    {
        var t = root.Find(path);
        var b = t != null ? t.GetComponent<Button>() : null;
        if (b == null) { problems.Add(path + " に Button が無い"); return; }
        bool wired = false;
        for (int k = 0; k < b.onClick.GetPersistentEventCount(); ++k)
        {
            if (b.onClick.GetPersistentTarget(k) == (Object)udon && b.onClick.GetPersistentMethodName(k) == "SendCustomEvent") { wired = true; }
        }
        if (!wired) { problems.Add(path + " が SpectatorMonitor へ配線されていない"); }
    }
}
#endif
