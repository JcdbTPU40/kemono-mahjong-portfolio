#if UNITY_EDITOR
// 音量設定パネル（VolumePanel）をシーンに生成・再生成するエディタスクリプト。
//
// ■ 何を作るか
//   performancemode_switch/VolumePanel   … World Space Canvas（AssetCredits の隣）
//    ├ Panel
//    │   ├ Title / Subtitle
//    │   ├ Row0..Row3                    … BGM / 効果音 / ボイス / 環境音
//    │   │   ├ Label / BtnXxxDown / Bar(Seg0..9) / BtnXxxUp / Value
//    │   ├ Footer / BtnReset
//    └ PreviewSource                     … 試聴用 AudioSource（2D・ローカル）
//
// ■ 配置
//   AssetCredits（performancemode_switch のローカル -0.62）の左隣に並べる。
//   上辺を AssetCredits と揃えてある。桟橋(Sanbashi)の床は x=-17.0 まであり、
//   パネル左端(-16.68)は桟橋の内側。手すりはパネルより手前・下にあり、
//   立ち位置(z=+0.3〜0.8)からの視線は実測で全面クリア。
//
// ■ 対象 AudioSource の収集
//   「シーン内の AudioSource を全部」ではなく、コンポーネントの参照から拾う。
//   VVMW（動画プレイヤー）やプレビュー用オブジェクトを巻き込まないため。
//     ボイス … MahjongSoundsV2.voiceSources / MatchmakingManager.matchVoiceSource
//              / MahjongVoiceSelector.previewSource
//     効果音 … MahjongSoundsV2.sfxSource / 各演出の seSource（宣言演出はSEを持たない）
//     BGM   … MahjongRiichiBgm.bgmSource（卓ごとの立直BGM。0本ならBGM行は実行時に隠れる）
//             ＋ MainMenuController.riichiBgmPreviewSource（装飾品画面の試聴1個）
//             ※ 音量の権威は MahjongRiichiBgm.Update 側。ここでは行を出す判定にしか使わない。
//     環境音 … EnvironmentAudio 配下
//   どこにも入らなかった AudioSource は「対象外」としてログに出す。
//
// ■ ボイスだけ扱いが違う
//   MahjongSoundsV2 は発声のたびに voiceSources[i].volume を上書きするため、
//   .volume を書いても消える。各卓の MahjongSoundsV2.volumePanel に本パネルを
//   割り当て、向こうから voiceAmp を読ませる（本ビルダーが自動で割り当てる）。
//
// 実行: Tools > Kemono Mahjong > Build Volume Panel
//       Tools > Kemono Mahjong > Validate Volume Panel
using System.Collections.Generic;
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

public static class BuildVolumePanel
{
    const string MainMenuName = "MainMenu";
    // ワールド設定の「サウンド」タブのページ（BuildMainMenu が作る）。
    const string WorldScreenName = "ScreenWorld/PageSound";
    const string LegacyParentName = "performancemode_switch";
    const string RootName = "VolumePanel";
    // 設定コンポーネントの置き場（MainMenu 直下・常時アクティブ）。
    // UI の ScreenWorld は入場時に非アクティブなので、そこに置くと
    // OnPlayerRestored（入場時1回きり）を取りこぼして音量が保存も復元もされない。
    const string HostName = "VolumeSettings";
    const string FontAssetPath = "Assets/Mahjong/Fonts/NotoSansJP-Bold SDF.asset";
    const string ScriptPath = "Assets/Mahjong/Sources/VolumeSettingsPanel.cs";
    const string ProgramAssetPath = "Assets/Mahjong/scene_UdonProgramSources/VolumeSettingsPanel Udon C# Program Asset.asset";

    // --- 配置（MainMenu/ScreenWorld/PageSound のCanvasローカル座標）---
    // ワールド設定は雀魂式（左に項目タブ、右にそのページ）。このパネルは「サウンド」ページを
    // 丸ごと使うので、以前のように 0.75 倍へ縮めず等倍で置く（VR で押しやすい）。
    // Canvas は 1920x1080＝中央から上下540・左右960。左のタブ列と仕切りは x -530 まで、
    // 上は「もどる」（y 410..）とタイトル。
    //   中心 (200, -65) / 1200x860 * 1.0 → x -400..800 / y -495..365
    static readonly Vector3 LocalPos = new Vector3(200f, -65f, 0f);
    static readonly Vector2 CanvasSize = new Vector2(1200f, 860f);
    const float CanvasScale = 1f;


    // --- レイアウト（px＝Canvasローカル）---
    const float Pad = 45f;
    const float RowTop = -168f;      // 1行目の上端（Panel 上端から）
    const float RowHeight = 62f;   // 5行に増えたので詰めた
    const float LabelX = 45f;
    const float LabelW = 280f;
    const float BtnX0 = 340f;        // − ボタン
    const float BtnW = 120f;
    const float BtnH = 50f;
    const float BarX = 480f;
    const float BarW = 320f;
    const float BarH = 34f;
    const float SegW = 26f;
    const float SegGap = 6f;
    const float BtnX1 = 820f;        // ＋ ボタン
    const float ValueX = 955f;
    const float ValueW = 200f;

    // --- 色（MainMenu の象牙／墨テーマ。正は StyleMainMenu）---
    // StyleMainMenu.Apply は MainMenu 配下の Button 画像を白＋墨枠へ、文字を墨へ
    // 一括で塗り替える。ここで同じ色を使っておけば Build の実行順に依存しない。
    static readonly Color PanelColor = StyleMainMenu.White;
    static readonly Color RowColor = StyleMainMenu.Paper;
    static readonly Color BtnColor = StyleMainMenu.White;
    static readonly Color ResetColor = StyleMainMenu.White;
    static readonly Color TextColor = StyleMainMenu.Ink;
    static readonly Color SubTextColor = StyleMainMenu.Ink;
    static readonly Color BarColor = new Color32(0xE4, 0xE0, 0xCC, 0xFF);
    static readonly Color SegOnColor = StyleMainMenu.Teal;
    static readonly Color SegOffColor = new Color32(0xC9, 0xC5, 0xB1, 0xFF);
    static readonly Color SegMuteColor = StyleMainMenu.Pink;

    // --- 文言（ここが正。変えたら再実行する）---
    const string TitleText = "音量設定  /  SOUND VOLUME";
    const string SubtitleText = "自分にだけ効きます・設定は次回も引き継がれます / Local to you, saved to your profile";
    const string FooterText = "ワールド全体を下げたいときは VRChat メニューの World 音量もどうぞ。\n" +
                             "For everything at once, use the World volume in your VRChat menu.";
    const string ResetLabel = "リセット / RESET";

    static readonly string[] RowLabels =
    {
        "立直BGM",
        "打牌音  TILE",
        "演出音  EFFECTS",
        "ボイス  VOICE",
        "環境音  AMBIENCE",
        "卓操作  TABLE",
        "時間警告  TIMER",
        "UI操作  UI",
        "通知  NOTICES",
    };

    // Row 名 / ボタン名の接頭辞（イベント名と対応）
    static readonly string[] RowKeys = { "Bgm", "Discard", "Fx", "Voice", "Amb", "Table", "Warn", "Ui", "System" };
    static readonly int[] RowOrder = { 0, 1, 3, 7, 8, 2, 4, 5, 6 };

    static TMP_FontAsset font_;
    static int created_;
    static int reused_;

    // ============================================================
    // ビルド
    // ============================================================

    [MenuItem("Tools/Kemono Mahjong/Build Volume Panel")]
    public static void Run()
    {
        created_ = 0;
        reused_ = 0;
        font_ = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
        if (font_ == null)
        {
            Debug.LogWarning("[BuildVolumePanel] " + FontAssetPath + " が見つかりません（日本語が豆腐になります）。");
        }

        if (EnsureProgramAsset())
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[BuildVolumePanel] U#プログラムアセットを生成しました。" +
                      "コンパイル完了後、もう一度 Build Volume Panel を実行してください。");
            return;
        }

        var mainMenu = GameObject.Find(MainMenuName);
        Transform parent = mainMenu != null ? mainMenu.transform.Find(WorldScreenName) : null;
        if (parent == null)
        {
            Debug.LogError("[BuildVolumePanel] MainMenu/ScreenWorld/PageSound がシーンにありません。先に Build Main Menu を実行してください。");
            return;
        }

        // 旧配置の同一パネルを再利用して、設定コンポーネントや参照を失わず移行する。
        var legacyParent = GameObject.Find(LegacyParentName);
        Transform legacyRoot = legacyParent != null ? legacyParent.transform.Find(RootName) : null;
        if (parent.Find(RootName) == null && legacyRoot != null)
        {
            Undo.SetTransformParent(legacyRoot, parent, "Move Volume Panel To MainMenu");
        }


        // 音源の正規化は独立したオーサリング作業。UIの再構築・移設では
        // 卓Prefabやシーン内AudioSourceを書き換えない。

        var canvasGo = BuildCanvas(parent);
        SetLayerRecursively(canvasGo, 0);

        var host = Node3D(mainMenu.transform, HostName);
        host.gameObject.SetActive(true);
        SetLayerRecursively(host.gameObject, 0);

        // 旧配置（UI と同じ VolumePanel 上）のコンポーネントは外す。
        // 参照していた卓・MainMenu・KemonoLanguage は下の Wire / Wire Language で張り替える。
        bool migrated = RemoveLegacyComponent(canvasGo);

        var panelComponent = EnsurePanelComponent(host.gameObject);
        if (panelComponent == null) { return; }

        Wire(panelComponent, canvasGo);

        if (migrated)
        {
            // 言語の配布先（extraTargets）が消えた旧コンポーネントを指したままになるので張り直す。
            BuildLanguage.Wire();
        }

        EditorSceneManager.MarkSceneDirty(canvasGo.scene);
        Debug.Log("[BuildVolumePanel] 完了: 新規 " + created_ + " / 再利用 " + reused_ +
                  "\nパネル中心=" + canvasGo.transform.position.ToString("F3") +
                  " / 大きさ=" + (CanvasSize.x * CanvasScale).ToString("F2") + " x " +
                  (CanvasSize.y * CanvasScale).ToString("F2") + " m");
    }

    // ============================================================
    // Canvas と UI
    // ============================================================

    static GameObject BuildCanvas(Transform parent)
    {
        var canvasT = Node(parent, RootName);
        var canvasGo = canvasT.gameObject;
        canvasT.localPosition = LocalPos;
        canvasT.localRotation = Quaternion.identity;
        canvasT.localScale = new Vector3(CanvasScale, CanvasScale, CanvasScale);
        ((RectTransform)canvasT).sizeDelta = CanvasSize;

        // MainMenu本体のWorld Space Canvasと入力面を共有する。
        // 旧独立パネル由来のCanvas類を残すと、非表示画面でも別Canvasとして描画される。
        var box = canvasGo.GetComponent<BoxCollider>();
        if (box != null) { Undo.DestroyObjectImmediate(box); }
        var shape = canvasGo.GetComponent<VRC.SDK3.Components.VRCUiShape>();
        if (shape != null) { Undo.DestroyObjectImmediate(shape); }
        var raycaster = canvasGo.GetComponent<GraphicRaycaster>();
        if (raycaster != null) { Undo.DestroyObjectImmediate(raycaster); }
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        if (scaler != null) { Undo.DestroyObjectImmediate(scaler); }
        var canvas = canvasGo.GetComponent<Canvas>();
        if (canvas != null) { Undo.DestroyObjectImmediate(canvas); }

        var panel = Node(canvasT, "Panel");
        Stretch(panel);
        // CharaDetail / StatsBlockSel と同じ「白い札」。枠と落ち影は StyleMainMenu と共通。
        StyleMainMenu.Frame(Img(panel, PanelColor, true), 3f, 8f);

        var title = Node(panel, "Title");
        Rect(title, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -26f), new Vector2(1100f, 66f));
        Tmp(title, TitleText, 46f, TextAlignmentOptions.Center, TextColor, false);

        var subtitle = Node(panel, "Subtitle");
        Rect(subtitle, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -100f), new Vector2(1120f, 40f));
        Tmp(subtitle, SubtitleText, 23f, TextAlignmentOptions.Center, SubTextColor, false);

        for (int i = 0; i < RowKeys.Length; ++i) { BuildRow(panel, i); }

        var footer = Node(panel, "Footer");
        Rect(footer, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(Pad, 24f), new Vector2(690f, 76f));
        Tmp(footer, FooterText, 20f, TextAlignmentOptions.BottomLeft, SubTextColor, true);

        Btn(panel, "BtnReset", ResetLabel, ResetColor,
            new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-Pad, 26f), new Vector2(280f, 76f), 26f);
        // ボタンの地は StyleMainMenu が白へ揃えてしまうので、目印は帯で付ける
        //（NeoAccent は名前で塗り替えから外れる＝ホーム画面のボタンと同じ作法）。
        StyleMainMenu.Accent(panel.Find("BtnReset"), StyleMainMenu.Yellow);

        BuildPreviewSource(canvasT);

        return canvasGo;
    }

    static void BuildRow(Transform panel, int index)
    {
        var row = Node(panel, "Row" + index);
        Rect(row, new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(0f, RowTop - RowOrder[index] * RowHeight), new Vector2(CanvasSize.x, RowHeight - 8f));
        Img(row, RowColor, false);

        // 行の入れ替え（効果音→演出音、環境音の追加）で、前の並びの ± ボタンが
        // 別の行に取り残される。位置がぴったり重なるので見た目では気づけず、
        // クリックがどちらへ入るかは兄弟順まかせになる。自分の行の2個以外は消す。
        string keepDown = "Btn" + RowKeys[index] + "Down";
        string keepUp = "Btn" + RowKeys[index] + "Up";
        for (int i = row.childCount - 1; i >= 0; --i)
        {
            var child = row.GetChild(i);
            if (!child.name.StartsWith("Btn")) { continue; }
            if (child.name == keepDown || child.name == keepUp) { continue; }
            Undo.DestroyObjectImmediate(child.gameObject);
        }

        var label = Node(row, "Label");
        Rect(label, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(LabelX, 0f), new Vector2(LabelW, 60f));
        Tmp(label, RowLabels[index], 30f, TextAlignmentOptions.Left, TextColor, false);

        Btn(row, "Btn" + RowKeys[index] + "Down", "-", BtnColor,
            new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(BtnX0, 0f), new Vector2(BtnW, BtnH), 36f);

        var bar = Node(row, "Bar");
        Rect(bar, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(BarX, 0f), new Vector2(BarW, BarH));
        StyleMainMenu.Frame(Img(bar, BarColor, false), 2f, 0f);

        for (int i = 0; i < VolumeSettingsPanel.SEGMENTS; ++i)
        {
            var seg = Node(bar, "Seg" + i);
            Rect(seg, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(4f + i * (SegW + SegGap), 0f), new Vector2(SegW, BarH - 12f));
            // 既定は50%＝5つ点灯。実行時に Start が描き直すが、エディタで見たときに
            // 実機と違う見た目だと混乱するので、生成時から既定値の状態にしておく。
            Img(seg, i < VolumeSettingsPanel.NORMAL_STEP ? SegOnColor : SegOffColor, false);
        }

        Btn(row, "Btn" + RowKeys[index] + "Up", "+", BtnColor,
            new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(BtnX1, 0f), new Vector2(BtnW, BtnH), 36f);

        var value = Node(row, "Value");
        Rect(value, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(ValueX, 0f), new Vector2(ValueW, 60f));
        Tmp(value, "50% 標準", 30f, TextAlignmentOptions.Right, TextColor, false);
    }

    static AudioSource BuildPreviewSource(Transform canvasT)
    {
        var t = Node3D(canvasT, "PreviewSource");
        t.localPosition = Vector3.zero;
        t.localRotation = Quaternion.identity;
        t.localScale = Vector3.one;

        var src = t.GetComponent<AudioSource>();
        if (src == null) { src = Undo.AddComponent<AudioSource>(t.gameObject); }
        src.playOnAwake = false;
        src.loop = false;
        src.spatialBlend = 0f; // 本人にしか聞こえないUIフィードバック
        src.volume = 1f;       // 実際の音量は PlayOneShot 側で掛ける
        return src;
    }

    // ============================================================
    // U# コンポーネント
    // ============================================================

    static bool EnsureProgramAsset()
    {
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(ScriptPath);
        if (script == null)
        {
            Debug.LogError("[BuildVolumePanel] スクリプトが見つかりません: " + ScriptPath);
            return false;
        }

        var type = script.GetClass();
        if (type != null && UdonSharpProgramAsset.GetProgramAssetForClass(type) != null) { return false; }
        if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(ProgramAssetPath) != null) { return false; }

        var programAsset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
        programAsset.sourceCsScript = script;
        AssetDatabase.CreateAsset(programAsset, ProgramAssetPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[BuildVolumePanel] U#プログラムアセットを生成: " + ProgramAssetPath);
        return true;
    }

    static bool RemoveLegacyComponent(GameObject canvasGo)
    {
        var old = canvasGo.GetComponent<VolumeSettingsPanel>();
        if (old == null) { return false; }

        var ub = UdonSharpEditorUtility.GetBackingUdonBehaviour(old);
        Object.DestroyImmediate(old);
        if (ub != null) { Object.DestroyImmediate(ub); }
        Debug.Log("[BuildVolumePanel] VolumePanel 上の旧 VolumeSettingsPanel を外し、MainMenu/" + HostName + " へ移しました。");
        return true;
    }

    static VolumeSettingsPanel EnsurePanelComponent(GameObject go)
    {
        var panel = go.GetComponent<VolumeSettingsPanel>();
        if (panel != null)
        {
            var existing = UdonSharpEditorUtility.GetBackingUdonBehaviour(panel);
            if (existing != null && existing.programSource != null) { return panel; }

            Debug.LogWarning("[BuildVolumePanel] VolumeSettingsPanel の UdonBehaviour が壊れているため作り直します。");
            Object.DestroyImmediate(panel);
            foreach (var ub in go.GetComponents<UdonBehaviour>())
            {
                if (ub.programSource == null) { Object.DestroyImmediate(ub); }
            }
        }

        var added = (VolumeSettingsPanel)UdonSharpUndo.AddComponent(go, typeof(VolumeSettingsPanel));
        if (added == null || UdonSharpEditorUtility.GetBackingUdonBehaviour(added) == null)
        {
            Debug.LogError("[BuildVolumePanel] VolumeSettingsPanel を追加できません（U#未コンパイル？）。コンパイル後に再実行してください。");
            if (added != null) { Object.DestroyImmediate(added); }
            return null;
        }
        return added;
    }

    // ============================================================
    // 配線
    // ============================================================

    static void Wire(VolumeSettingsPanel panel, GameObject canvasGo)
    {
        var canvasT = canvasGo.transform;
        var panelT = canvasT.Find("Panel");

        // --- 対象 AudioSource の収集 ---
        var bgm = new List<AudioSource>();
        var se = new List<AudioSource>();       // 演出音
        var discard = new List<AudioSource>();  // 打牌音（専用ソース）
        var voice = new List<AudioSource>();
        var amb = new List<AudioSource>();
        var table = new List<AudioSource>();
        var warn = new List<AudioSource>();
        var ui = new List<AudioSource>();
        var system = new List<AudioSource>();
        var claimed = new HashSet<AudioSource>();

        var soundsList = Object.FindObjectsOfType<MahjongSoundsV2>(true);
        foreach (var sounds in soundsList)
        {
            CollectSoundsV2(sounds, se, voice, claimed);
            Add(discard, sounds.discardSource, claimed);
            // 持ち時間の警告音は専用ソース。分類は「演出音」。
            Add(warn, sounds.warnSource, claimed);
            Add(table, sounds.tableOperationSource, claimed);
            Add(system, sounds.systemSource, claimed);
            Add(voice, sounds.announceSource, claimed);
        }

        foreach (var c in Object.FindObjectsOfType<MahjongAgariEffect>(true)) { Add(se, c.seSource, claimed); }
        foreach (var c in Object.FindObjectsOfType<MahjongReachDiscardEffect>(true)) { Add(se, c.seSource, claimed); }
        foreach (var c in Object.FindObjectsOfType<MahjongPresentationScreen>(true)) { Add(se, c.seSource, claimed); }
        foreach (var c in Object.FindObjectsOfType<MahjongYakumanCutin>(true)) { Add(se, c.seSource, claimed); }
        foreach (var c in Object.FindObjectsOfType<MahjongYakumanImpact>(true)) { Add(se, c.seSource, claimed); }
        foreach (var c in Object.FindObjectsOfType<MahjongYakumanBanner>(true)) { Add(se, c.seSource, claimed); }

        foreach (var m in Object.FindObjectsOfType<MatchmakingManager>(true)) { Add(voice, m.matchVoiceSource, claimed); }
        foreach (var v in Object.FindObjectsOfType<MahjongVoiceSelector>(true)) { Add(voice, v.previewSource, claimed); }

        // 2026-09-09 追加のローカルSE。ここへ登録しないとスライダーが効かないうえ、
        // 下の leftover 検査に引っかかって Validate がエラーを出す。
        // マッチングSEは「演出音」側（成立ボイスだけがボイス側）。
        foreach (var m in Object.FindObjectsOfType<MatchmakingManager>(true)) { Add(system, m.matchSfxSource, claimed); }
        foreach (var c in Object.FindObjectsOfType<StaffOnlyArea>(true)) { Add(system, c.joinSource, claimed); }
        foreach (var c in Object.FindObjectsOfType<MenuButtonPressFx>(true)) { Add(ui, c.clickSource, claimed); }

        foreach (var c in Object.FindObjectsOfType<SelfiePortraitCamera>(true)) { Add(ui, c.audioSource, claimed); }

        foreach (var b in Object.FindObjectsOfType<MahjongRiichiBgm>(true))
        {
            var src = b.bgmSource != null ? b.bgmSource : b.GetComponent<AudioSource>();
            Add(bgm, src, claimed);
            b.volumePanel = panel;
            EditorUtility.SetDirty(b);
            UdonSharpEditorUtility.CopyProxyToUdon(b);
        }

        // メインメニューの立直BGM試聴。ここへ登録しないとBGMスライダーが効かず、
        // 下の leftover 検査にも引っかかる。卓の立直BGMと同じく、実際の音量を書くのは
        // MainMenuController.Update 側（トラック別ゲイン × bgmAmp）。
        foreach (var m in Object.FindObjectsOfType<MainMenuController>(true))
        {
            Add(bgm, m.riichiBgmPreviewSource, claimed);
            m.volumePanel = panel;
            EditorUtility.SetDirty(m);
            UdonSharpEditorUtility.CopyProxyToUdon(m);
        }

        var previewSource = canvasT.Find("PreviewSource").GetComponent<AudioSource>();
        claimed.Add(previewSource);

        var leftovers = new List<string>();
        foreach (var src in Object.FindObjectsOfType<AudioSource>(true))
        {
            if (claimed.Contains(src)) { continue; }

            if (IsAmbience(src)) { Add(amb, src, claimed); continue; }

            leftovers.Add(Path(src.transform));
        }

        panel.bgmSources = bgm.ToArray();
        panel.discardSources = discard.ToArray();
        panel.fxSources = se.ToArray();
        panel.voiceSources = voice.ToArray();
        panel.ambienceSources = amb.ToArray();
        panel.tableSources = table.ToArray();
        panel.warningSources = warn.ToArray();
        panel.uiSources = ui.ToArray();
        panel.systemSources = system.ToArray();

        // --- UI 参照 ---
        var rows = new GameObject[RowKeys.Length];
        var values = new TextMeshProUGUI[RowKeys.Length];
        var segments = new Image[RowKeys.Length * VolumeSettingsPanel.SEGMENTS];
        for (int i = 0; i < RowKeys.Length; ++i)
        {
            var row = panelT.Find("Row" + i);
            rows[i] = row.gameObject;
            values[i] = row.Find("Value").GetComponent<TextMeshProUGUI>();
            for (int s = 0; s < VolumeSettingsPanel.SEGMENTS; ++s)
            {
                segments[i * VolumeSettingsPanel.SEGMENTS + s] = row.Find("Bar/Seg" + s).GetComponent<Image>();
            }
        }
        panel.rowObjects = rows;
        panel.valueLabels = values;
        panel.segmentImages = segments;

        panel.segmentOnColor = SegOnColor;
        panel.segmentOffColor = SegOffColor;
        panel.segmentMuteColor = SegMuteColor;
        panel.valueTextColor = TextColor;

        // --- 試聴 ---
        panel.previewSource = previewSource;
        panel.voiceAmp = 1.0f;
        if (soundsList.Length > 0)
        {
            var sample = soundsList[0];
            panel.discardPreviewClip = sample.discard;
            panel.fxPreviewClip = sample.haipai != null ? sample.haipai : sample.agariScoreSfx;
            panel.voicePreviewClip = sample.pon != null && sample.pon.Length > 0 ? sample.pon[0] : null;
        }

        // U# は proxy の値を Udon 側へ写さないと実行時に反映されない。
        EditorUtility.SetDirty(panel);
        UdonSharpEditorUtility.CopyProxyToUdon(panel);

        // --- ボタン ---
        var udon = UdonSharpEditorUtility.GetBackingUdonBehaviour(panel);
        if (udon == null)
        {
            Debug.LogError("[BuildVolumePanel] backing UdonBehaviour が取得できません。");
            return;
        }
        for (int i = 0; i < RowKeys.Length; ++i)
        {
            var row = panelT.Find("Row" + i);
            WireButton(row, "Btn" + RowKeys[i] + "Down", udon, "On" + RowKeys[i] + "Down");
            WireButton(row, "Btn" + RowKeys[i] + "Up", udon, "On" + RowKeys[i] + "Up");
        }
        WireButton(panelT, "BtnReset", udon, "OnResetAll");

        // --- 各卓の MahjongSoundsV2 へパネル参照を配る（発声の音量はここを読む）---
        int wiredSounds = 0;
        foreach (var sounds in soundsList)
        {
            sounds.volumePanel = panel;
            EditorUtility.SetDirty(sounds);
            UdonSharpEditorUtility.CopyProxyToUdon(sounds);
            ++wiredSounds;
        }

        Debug.Log("[BuildVolumePanel] 収集: BGM " + bgm.Count + " / 効果音 " + se.Count +
                  " / ボイス " + voice.Count + " / 環境音 " + amb.Count +
                  " ／ MahjongSoundsV2 へ配線 " + wiredSounds + " 卓" +
                  (bgm.Count == 0 ? "\n※ BGM の AudioSource が無いため BGM 行は実行時に隠れます（MahjongRiichiBgm 未設置）。" : "") +
                  (leftovers.Count > 0 ? "\n対象外（音量パネルの影響を受けない）: " + string.Join(", ", leftovers) : ""));
    }

    // MahjongSoundsV2 の AudioSource。未割当なら実行時と同じ規則で子から拾う。
    static void CollectSoundsV2(MahjongSoundsV2 sounds, List<AudioSource> se, List<AudioSource> voice, HashSet<AudioSource> claimed)
    {
        bool hasVoice = false;
        if (sounds.voiceSources != null)
        {
            for (int i = 0; i < sounds.voiceSources.Length; ++i)
            {
                if (sounds.voiceSources[i] == null) { continue; }
                Add(voice, sounds.voiceSources[i], claimed);
                hasVoice = true;
            }
        }
        if (!hasVoice)
        {
            for (int i = 0; i < 4; ++i)
            {
                var t = sounds.transform.Find("AudioSource" + i);
                if (t != null) { Add(voice, t.GetComponent<AudioSource>(), claimed); }
            }
        }

        if (sounds.sfxSource != null)
        {
            Add(se, sounds.sfxSource, claimed);
        }
        else
        {
            var t = sounds.transform.Find("SfxSource");
            if (t != null) { Add(se, t.GetComponent<AudioSource>(), claimed); }
        }
    }

    static bool IsAmbience(AudioSource src)
    {
        if (src.transform.root.name == "EnvironmentAudio") { return true; }
        return src.gameObject.name.Contains("Ambience");
    }

    static void Add(List<AudioSource> list, AudioSource src, HashSet<AudioSource> claimed)
    {
        if (src == null) { return; }
        if (!claimed.Add(src)) { return; }
        list.Add(src);
    }

    static string Path(Transform t)
    {
        var name = t.name;
        var p = t.parent;
        while (p != null) { name = p.name + "/" + name; p = p.parent; }
        return name;
    }

    static void WireButton(Transform parent, string name, UdonBehaviour udon, string eventName)
    {
        var t = parent.Find(name);
        if (t == null)
        {
            Debug.LogError("[BuildVolumePanel] ボタンが見つかりません: " + name);
            return;
        }
        var button = t.GetComponent<Button>();
        if (button == null)
        {
            Debug.LogError("[BuildVolumePanel] Button コンポーネントがありません: " + name);
            return;
        }
        while (button.onClick.GetPersistentEventCount() > 0)
        {
            UnityEventTools.RemovePersistentListener(button.onClick, 0);
        }
        UnityEventTools.AddStringPersistentListener(button.onClick, udon.SendCustomEvent, eventName);
        EditorUtility.SetDirty(button);
    }

    // ============================================================
    // 検証
    // ============================================================

    [MenuItem("Tools/Kemono Mahjong/Validate Volume Panel")]
    public static void Validate()
    {
        var mainMenu = GameObject.Find(MainMenuName);
        Transform parent = mainMenu != null ? mainMenu.transform.Find(WorldScreenName) : null;
        var root = parent != null ? parent.Find(RootName) : null;
        if (root == null)
        {
            Debug.LogError("[ValidateVolumePanel] MainMenu/ScreenWorld/PageSound/VolumePanel がシーンにありません。先に Build Volume Panel を実行してください。");
            return;
        }


        var problems = new List<string>();
        var hostT = mainMenu.transform.Find(HostName);
        var panel = hostT != null ? hostT.GetComponent<VolumeSettingsPanel>() : null;
        if (root.GetComponent<VolumeSettingsPanel>() != null)
        {
            problems.Add("VolumePanel に旧 VolumeSettingsPanel が残っている（非アクティブ画面で OnPlayerRestored を取りこぼす）");
        }
        if (hostT != null && !hostT.gameObject.activeSelf)
        {
            problems.Add("MainMenu/" + HostName + " が非アクティブ（音量が保存・復元されない）");
        }
        if (panel == null)
        {
            problems.Add("MainMenu/" + HostName + " に VolumeSettingsPanel が付いていない");
        }
        else
        {
            if (UdonSharpEditorUtility.GetBackingUdonBehaviour(panel) == null) { problems.Add("backing UdonBehaviour が無い"); }
            if (Count(panel.discardSources) == 0) { problems.Add("discardSources が空（打牌音の専用ソース未生成）"); }
            if (Count(panel.fxSources) == 0) { problems.Add("fxSources が空"); }
            if (Count(panel.ambienceSources) == 0) { problems.Add("ambienceSources が空（環境音が未設置なら想定内）"); }
            if (panel.previewSource == null) { problems.Add("previewSource が未割当"); }
            if (panel.rowObjects == null || panel.rowObjects.Length != RowKeys.Length) { problems.Add("rowObjects が" + RowKeys.Length + "個でない"); }
            if (panel.valueLabels == null || panel.valueLabels.Length != RowKeys.Length) { problems.Add("valueLabels が" + RowKeys.Length + "個でない"); }
            int wantSegments = RowKeys.Length * VolumeSettingsPanel.SEGMENTS;
            if (panel.segmentImages == null || panel.segmentImages.Length != wantSegments)
            {
                problems.Add("segmentImages が " + wantSegments + " 個でない");
            }
            else
            {
                for (int i = 0; i < panel.segmentImages.Length; ++i)
                {
                    if (panel.segmentImages[i] == null) { problems.Add("segmentImages[" + i + "] が null"); break; }
                }
            }
        }

        // ボタン
        var panelT = root.Find("Panel");
        if (panelT == null)
        {
            problems.Add("Panel が無い");
        }
        else
        {
            for (int i = 0; i < RowKeys.Length; ++i)
            {
                var row = panelT.Find("Row" + i);
                if (row == null) { problems.Add("Row" + i + " が無い"); continue; }

                int rowButtons = 0;
                for (int c = 0; c < row.childCount; ++c)
                {
                    if (row.GetChild(c).name.StartsWith("Btn")) { ++rowButtons; }
                }
                if (rowButtons != 2)
                {
                    problems.Add("Row" + i + " の ± ボタンが " + rowButtons
                                 + " 個（他の行のボタンが重なって残っている）");
                }
                CheckButton(row, "Btn" + RowKeys[i] + "Down", "On" + RowKeys[i] + "Down", problems);
                CheckButton(row, "Btn" + RowKeys[i] + "Up", "On" + RowKeys[i] + "Up", problems);
            }
            CheckButton(panelT, "BtnReset", "OnResetAll", problems);
        }

        // MainMenu本体の入力面を共有し、子に独立Canvasを残さない。
        if (root.GetComponent<Canvas>() != null) { problems.Add("VolumePanel に独立Canvasが残っている"); }
        if (mainMenu.GetComponent<VRC.SDK3.Components.VRCUiShape>() == null) { problems.Add("MainMenu に VRCUiShape が無い"); }
        if (mainMenu.GetComponent<GraphicRaycaster>() == null) { problems.Add("MainMenu に GraphicRaycaster が無い"); }


        // 卓側の参照
        int missing = 0;
        var soundsList = Object.FindObjectsOfType<MahjongSoundsV2>(true);
        foreach (var sounds in soundsList)
        {
            if (sounds.volumePanel == null) { ++missing; }
        }
        if (missing > 0) { problems.Add("MahjongSoundsV2.volumePanel 未割当 " + missing + " / " + soundsList.Length + " 卓（ボイス音量が効かない）"); }

        // Prefab に書いた音量が実行時に効いているか（Udonヒープと proxy の突き合わせ）。
        // 卓は publicVariables をインスタンス側で override しているので、
        // Prefab だけ直しても実行時は旧値のまま、という事故が起きる。
        int stale = 0;
        stale += CountStaleHeap<MahjongSoundsV2>();
        stale += CountStaleHeap<MahjongAgariEffect>();
        stale += CountStaleHeap<MahjongPresentationScreen>();
        stale += CountStaleHeap<MahjongReachDiscardEffect>();
        stale += CountStaleHeap<MahjongYakumanCutin>();
        stale += CountStaleHeap<MahjongYakumanBanner>();
        if (stale > 0)
        {
            problems.Add("Udonヒープが proxy と食い違う UdonBehaviour が " + stale + " 個（音量が実行時に反映されない）");
        }

        // 音量パネルの +dB 側が効くか（基準音量が 1.0 のままだとクランプで死ぬ）。
        int noHeadroom = 0;
        foreach (var src in Object.FindObjectsOfType<AudioSource>(true))
        {
            if (!src.transform.root.name.StartsWith("KemonoMahjong")) { continue; }
            if (src.volume > KemonoAudioNormalizer.BaseVolume + 0.001f) { ++noHeadroom; }
        }
        if (noHeadroom > 0)
        {
            problems.Add("基準音量が " + KemonoAudioNormalizer.BaseVolume + " を超える AudioSource が " + noHeadroom + " 本（80%以上が頭打ちになる）");
        }

        if (root.parent != parent) { problems.Add("VolumePanel が ScreenWorld/PageSound の直下にない"); }


        if (problems.Count == 0)
        {
            Debug.Log("[ValidateVolumePanel] OK: 打牌音 " + Count(panel.discardSources) +
                      " / 演出音 " + Count(panel.fxSources) +
                      " / ボイス " + Count(panel.voiceSources) +
                      " / 環境音 " + Count(panel.ambienceSources) +
                      " / BGM " + Count(panel.bgmSources) +
                      " ／ 卓 " + soundsList.Length + " すべて配線済み");
        }
        else
        {
            Debug.LogError("[ValidateVolumePanel] 問題 " + problems.Count + " 件:\n - " + string.Join("\n - ", problems));
        }
    }

    // proxy と Udon ヒープがズレている UdonBehaviour の数。
    static int CountStaleHeap<T>() where T : UdonSharp.UdonSharpBehaviour
    {
        int n = 0;
        foreach (var comp in Object.FindObjectsOfType<T>(true))
        {
            var ub = UdonSharpEditorUtility.GetBackingUdonBehaviour(comp);
            if (ub == null) { continue; }
            if (KemonoAudioNormalizer.HeapDiffers(comp, ub)) { ++n; }
        }
        return n;
    }

    static int Count(AudioSource[] arr)
    {
        if (arr == null) { return 0; }
        int n = 0;
        for (int i = 0; i < arr.Length; ++i) { if (arr[i] != null) { ++n; } }
        return n;
    }

    static void CheckButton(Transform parent, string name, string eventName, List<string> problems)
    {
        var t = parent.Find(name);
        if (t == null) { problems.Add(name + " が無い"); return; }
        var button = t.GetComponent<Button>();
        if (button == null) { problems.Add(name + " に Button が無い"); return; }

        // VolumePanel を MainMenu の下へ移してから、Wire Main Menu のクリックアニメが
        // 同じボタンへ「押し込み中継」を1件足す。中継はボタン自身に載っているので、
        // 自分以外を指す呼び出しだけを本番の配線として数える。
        int calls = 0;
        string method = null;
        for (int i = 0; i < button.onClick.GetPersistentEventCount(); ++i)
        {
            var target = button.onClick.GetPersistentTarget(i) as Component;
            if (target != null && target.gameObject == t.gameObject) { continue; }
            ++calls;
            method = button.onClick.GetPersistentMethodName(i);
        }
        if (calls != 1)
        {
            problems.Add(name + " の onClick が " + calls + " 件");
            return;
        }
        if (method != "SendCustomEvent") { problems.Add(name + " の onClick が SendCustomEvent でない"); }
    }

    // ============================================================
    // 生成ヘルパ（同名があれば再利用＝重複生成しない）
    // ============================================================

    static Transform Node(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null) { ++reused_; return t; }
        var go = new GameObject(name, typeof(RectTransform));
        Undo.RegisterCreatedObjectUndo(go, "Build Volume Panel");
        go.transform.SetParent(parent, false);
        ++created_;
        return go.transform;
    }

    static Transform Node3D(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null) { ++reused_; return t; }
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Build Volume Panel");
        go.transform.SetParent(parent, false);
        ++created_;
        return go.transform;
    }

    static void Rect(Transform t, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        var rt = (RectTransform)t;
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        rt.localRotation = Quaternion.identity;
        rt.localScale = Vector3.one;
    }

    static void Stretch(Transform t)
    {
        var rt = (RectTransform)t;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        rt.localRotation = Quaternion.identity;
        rt.localScale = Vector3.one;
    }

    static Image Img(Transform t, Color color, bool raycastTarget)
    {
        var image = t.GetComponent<Image>();
        if (image == null) { image = Undo.AddComponent<Image>(t.gameObject); }
        image.color = color;
        image.raycastTarget = raycastTarget;
        return image;
    }

    static TextMeshProUGUI Tmp(Transform t, string text, float size, TextAlignmentOptions alignment, Color color, bool wrap)
    {
        var tmp = t.GetComponent<TextMeshProUGUI>();
        if (tmp == null) { tmp = Undo.AddComponent<TextMeshProUGUI>(t.gameObject); }
        if (font_ != null) { tmp.font = font_; }
        tmp.text = text;
        tmp.fontSize = size;
        tmp.alignment = alignment;
        tmp.color = color;
        tmp.raycastTarget = false; // ラベルはレーザーを遮らない
        tmp.enableWordWrapping = wrap;
        tmp.overflowMode = TextOverflowModes.Overflow;
        EditorUtility.SetDirty(tmp);
        return tmp;
    }

    static void Btn(Transform parent, string name, string label, Color color,
        Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size, float fontSize)
    {
        var t = Node(parent, name);
        Rect(t, anchor, pivot, pos, size);
        var image = Img(t, color, true);

        var button = t.GetComponent<Button>();
        if (button == null) { button = Undo.AddComponent<Button>(t.gameObject); }
        button.targetGraphic = image;
        StyleMainMenu.Frame(image, 4f, 9f);
        StyleMainMenu.ButtonTint(button);

        var labelT = Node(t, "Label");
        Stretch(labelT);
        Tmp(labelT, label, fontSize, TextAlignmentOptions.Center, TextColor, false);
    }

    static void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform)
        {
            SetLayerRecursively(child.gameObject, layer);
        }
    }
}
#endif
