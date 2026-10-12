#if UNITY_EDITOR
// スタッフパネル（段位戦の待機列の開閉）の生成・配線。
//   Tools > Kemono Mahjong > Build Staff Panel    … 生成＋配線（冪等・何度実行してもよい）
//   Tools > Kemono Mahjong > Validate Staff Panel … 参照の検算のみ（変更なし）
//
// 【置き場所】
//   カウンター裏のスタッフ専用エリア。RankAdminMonitor（段位補正）と同じ壁面の下側。
//   向きは RankAdminMonitor と同じ（rotY=270 ＝ forward -X）。World Space Canvas は
//   forward の裏側から読むので、+X 側（＝立てる場所がある側）から読める。
//   この壁面は上下左右を実物に挟まれている（下の DefaultPosition のコメントに実測値）。
//   位置・大きさを動かすと木箱かカウンターに埋まるので、変える時は必ず実地で撮って確認する。
//
// 【新規オブジェクトは1回目で作られ、配線は2回目で入る】
//   U# Program Asset を作った直後はコンパイルが終わっていないためコンポーネントを足せない。
//   初回は「生成しました。もう一度実行してください」で終わる（テンプレの BuildRankAdminMonitor と同じ）。
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

public static class BuildStaffPanel
{
    private const string RootName = "StaffEventPanel";
    private const string ProgramAssetDir = "Assets/Mahjong/scene_UdonProgramSources";
    private const string ScriptPath = "Assets/Mahjong/Sources/StaffEventPanel.cs";
    private const string ProgramAssetPath = ProgramAssetDir + "/StaffEventPanel Udon C# Program Asset.asset";
    private const string FontAssetPath = "Assets/Mahjong/Fonts/NotoSansJP-Bold SDF.asset";

    private const float PanelWidth = 750f;
    private const float PanelHeight = 1000f;

    // カウンター裏の実測で決めた「空いている面」。x=5.82 の壁面は
    //   ・木箱 Wood_box_01 が z <= -0.29 を塞ぐ
    //   ・カウンター Table_02 が z >= 0.49（y <= 1.74）を塞ぐ
    //   ・上は RankAdminMonitor が y 2.07 から始まる
    // ので、空きは z -0.29..0.49 / y 1.00..2.07。そこへ 0.75 x 1.00 m で収める。
    // 幅が取れないぶんボタンは2x2ではなく1列に積む。
    private static readonly Vector3 DefaultPosition = new Vector3(5.82f, 1.51f, 0.10f);
    private static readonly Vector3 DefaultEuler = new Vector3(0f, 270f, 0f);
    private static readonly Vector3 DefaultScale = new Vector3(0.001f, 0.001f, 0.001f);

    private static readonly Color PanelColor = new Color(0.05f, 0.035f, 0.03f, 0.99f);
    private static readonly Color ButtonColor = new Color(0.16f, 0.27f, 0.43f, 1f);
    private static readonly Color OpenAllColor = new Color(0.1f, 0.48f, 0.35f, 1f);
    // 卓順ページの行の色。使用中は赤（押しても動かないことを押す前に見せる）。
    private static readonly Color OrderBusyColor = new Color(0.52f, 0.14f, 0.16f, 1f);
    private static readonly Color OrderSelectedColor = new Color(0.85f, 0.66f, 0.12f, 1f);
    // 強制リセット。武装した卓と実行ボタンだけを赤くする（未武装は押しても効かないので灰）。
    private static readonly Color ForceArmedColor = new Color(0.78f, 0.20f, 0.10f, 1f);
    private static readonly Color ForceIdleColor = new Color(0.22f, 0.20f, 0.19f, 1f);

    private static readonly string[] ColumnButtonNames =
    {
        "BtnColumn0", "BtnColumn1", "BtnColumn2", "BtnColumn3"
    };
    private static readonly string[] ColumnEventNames =
    {
        "ToggleColumn0", "ToggleColumn1", "ToggleColumn2", "ToggleColumn3"
    };
    // 0=赤ドラ 1=ローカル役 2=オープン立直 3=トビ 4=ツモ損 5=能力 6=長考時間。
    // **青天井と局数はここに無い**。段位戦へ入れてはいけないルールなので、
    // ボタンを作らないだけでなく MatchmakingManager 側に同期変数も置いていない。
    private static readonly string[] RuleButtonNames =
    {
        "BtnRuleRedDora", "BtnRuleLocalYaku", "BtnRuleOpenReach",
        "BtnRuleBust", "BtnRuleTsumoLoss", "BtnRuleAbility", "BtnRuleTimer"
    };
    private static readonly string[] RuleEventNames =
    {
        "CycleRedDora", "ToggleLocalYaku", "ToggleOpenReach",
        "ToggleBust", "ToggleTsumoLoss", "ToggleAbility", "CycleTurnTimer"
    };
    private static readonly string[] RuleInitialLabels =
    {
        "赤ドラ：3枚", "ローカル役：なし", "オープン立直：なし",
        "トビ：あり", "ツモ損：あり", "能力：あり", "長考時間：8+30"
    };

    // 卓順（空卓を埋める順番）。行をタップして選び、もう1行タップで入れ替える。
    private static readonly string[] OrderButtonNames =
    {
        "BtnOrder0", "BtnOrder1", "BtnOrder2", "BtnOrder3",
        "BtnOrder4", "BtnOrder5", "BtnOrder6", "BtnOrder7"
    };
    private static readonly string[] OrderEventNames =
    {
        "SelectSlot0", "SelectSlot1", "SelectSlot2", "SelectSlot3",
        "SelectSlot4", "SelectSlot5", "SelectSlot6", "SelectSlot7"
    };

    private static readonly string[] ColumnInitialLabels =
    {
        "四麻 半荘戦\n公開中（0人待機）",
        "四麻 東風戦\n公開中（0人待機）",
        "三麻 半荘戦\n公開中（0人待機）",
        "三麻 東風戦\n公開中（0人待機）"
    };

    private static TMP_FontAsset font_;

    [MenuItem("Tools/Kemono Mahjong/Build Staff Panel")]
    public static void Run()
    {
        font_ = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
        if (font_ == null)
        {
            Debug.LogError("[BuildStaffPanel] 日本語TMPフォントが見つかりません: " + FontAssetPath);
            return;
        }

        GameObject root = FindSceneObject(RootName);
        if (root == null)
        {
            root = new GameObject(RootName, typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(root, "Build Staff Panel");
            RectTransform rt = (RectTransform)root.transform;
            rt.SetParent(null, false);
            rt.position = DefaultPosition;
            rt.rotation = Quaternion.Euler(DefaultEuler);
            rt.localScale = DefaultScale;
            Debug.Log("[BuildStaffPanel] " + RootName + " を生成しました（カウンター裏・RankAdminMonitorの下）。");
        }
        // 既存なら位置・回転には触れない＝Sceneビューで自由に置ける。

        EnsureRootComponents(root);
        BuildUi(root.transform);
        SetLayerRecursively(root, 0);

        if (EnsureProgramAsset(ScriptPath, ProgramAssetPath))
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            EditorSceneManager.MarkSceneDirty(root.scene);
            Debug.Log("[BuildStaffPanel] U# Program Asset を生成しました。コンパイル完了後にもう一度実行してください。");
            return;
        }

        MatchmakingManager matchmaking = UnityEngine.Object.FindObjectOfType<MatchmakingManager>(true);
        if (matchmaking == null)
        {
            Debug.LogError("[BuildStaffPanel] MatchmakingManager が見つかりません。先に Wire Matchmaking を実行してください。");
            return;
        }
        StaffOnlyArea staffArea = UnityEngine.Object.FindObjectOfType<StaffOnlyArea>(true);
        if (staffArea == null)
        {
            Debug.LogError("[BuildStaffPanel] StaffOnlyArea が見つかりません。先に Build Staff Only Area を実行してください。");
            return;
        }

        StaffEventPanel panel = (StaffEventPanel)EnsureUSharpComponent(root, typeof(StaffEventPanel));
        if (panel == null) { return; }

        Transform t = root.transform;
        panel.matchmaking = matchmaking;
        panel.staffArea = staffArea;
        panel.panelRoot = Find(t, "Panel").gameObject;
        panel.statusText = FindTmp(t, "Panel/StaffStatus");
        var labels = new TextMeshProUGUI[4];
        for (int i = 0; i < 4; ++i)
        {
            labels[i] = FindTmp(t, "Panel/PageColumns/" + ColumnButtonNames[i] + "/Label");
        }
        panel.columnLabels = labels;
        panel.pageColumns = Find(t, "Panel/PageColumns").gameObject;
        panel.pageRules = Find(t, "Panel/PageRules").gameObject;
        panel.pageDisplay = Find(t, "Panel/PageDisplay").gameObject;
        panel.staffBadgeLabel = FindTmp(t, "Panel/PageDisplay/BtnStaffBadge/Label");
        panel.soundproofLabel = FindTmp(t, "Panel/PageDisplay/BtnSoundproof/Label");
        panel.pageTables = Find(t, "Panel/PageTables").gameObject;
        var orderLabels = new TextMeshProUGUI[OrderButtonNames.Length];
        for (int i = 0; i < OrderButtonNames.Length; ++i)
        {
            orderLabels[i] = FindTmp(t, "Panel/PageTables/" + OrderButtonNames[i] + "/Label");
        }
        panel.tableOrderLabels = orderLabels;
        var orderImages = new Image[OrderButtonNames.Length];
        for (int i = 0; i < OrderButtonNames.Length; ++i)
        {
            var row = Find(t, "Panel/PageTables/" + OrderButtonNames[i]);
            orderImages[i] = (row != null) ? row.GetComponent<Image>() : null;
        }
        panel.tableOrderImages = orderImages;
        // U# のシリアライズ済み値は既定値を変えても更新されないので、Wire で毎回入れ直す。
        panel.orderIdleColor = ButtonColor;
        panel.orderBusyColor = OrderBusyColor;
        panel.orderSelectedColor = OrderSelectedColor;
        panel.orderArmedColor = ForceArmedColor;
        panel.forceResetModeLabel = FindTmp(t, "Panel/PageTables/BtnForceMode/Label");
        panel.forceResetConfirmLabel = FindTmp(t, "Panel/PageTables/BtnForceExec/Label");
        var forceExec = Find(t, "Panel/PageTables/BtnForceExec");
        panel.forceResetConfirmImage = (forceExec != null) ? forceExec.GetComponent<Image>() : null;
        panel.forceIdleColor = ForceIdleColor;
        panel.forceArmedColor = ForceArmedColor;
        var ruleLabels = new TextMeshProUGUI[RuleButtonNames.Length];
        for (int i = 0; i < RuleButtonNames.Length; ++i)
        {
            ruleLabels[i] = FindTmp(t, "Panel/PageRules/" + RuleButtonNames[i] + "/Label");
        }
        panel.ruleLabels = ruleLabels;
        UdonSharpEditorUtility.CopyProxyToUdon(panel);
        EditorUtility.SetDirty(panel);

        // Owner 側でスタッフ判定を行うため、Manager にも同じ allowlist を渡す。
        if (matchmaking.staffArea != staffArea)
        {
            matchmaking.staffArea = staffArea;
            UdonSharpEditorUtility.CopyProxyToUdon(matchmaking);
            EditorUtility.SetDirty(matchmaking);
            Debug.Log("[BuildStaffPanel] MatchmakingManager.staffArea を割り当てました。");
        }

        UdonBehaviour udon = UdonSharpEditorUtility.GetBackingUdonBehaviour(panel);
        if (udon == null)
        {
            Debug.LogError("[BuildStaffPanel] StaffEventPanel の backing UdonBehaviour が見つかりません。");
            return;
        }

        int wired = 0;
        for (int i = 0; i < 4; ++i)
        {
            wired += WireButton(t, "Panel/PageColumns/" + ColumnButtonNames[i], udon, ColumnEventNames[i]);
        }
        wired += WireButton(t, "Panel/PageColumns/BtnOpenAll", udon, "OpenAllColumns");
        for (int i = 0; i < RuleButtonNames.Length; ++i)
        {
            wired += WireButton(t, "Panel/PageRules/" + RuleButtonNames[i], udon, RuleEventNames[i]);
        }
        wired += WireButton(t, "Panel/PageRules/BtnRuleReset", udon, "ResetEventRules");
        wired += WireButton(t, "Panel/BtnTabColumns", udon, "ShowPageColumns");
        wired += WireButton(t, "Panel/BtnTabRules", udon, "ShowPageRules");
        wired += WireButton(t, "Panel/BtnTabDisplay", udon, "ShowPageDisplay");
        wired += WireButton(t, "Panel/PageDisplay/BtnStaffBadge", udon, "ToggleStaffBadge");
        wired += WireButton(t, "Panel/PageDisplay/BtnSoundproof", udon, "ToggleSoundproof");
        wired += WireButton(t, "Panel/BtnTabTables", udon, "ShowPageTables");
        for (int i = 0; i < OrderButtonNames.Length; ++i)
        {
            wired += WireButton(t, "Panel/PageTables/" + OrderButtonNames[i], udon, OrderEventNames[i]);
        }
        wired += WireButton(t, "Panel/PageTables/BtnOrderReset", udon, "ResetTableOrder");
        wired += WireButton(t, "Panel/PageTables/BtnForceMode", udon, "ToggleForceResetMode");
        wired += WireButton(t, "Panel/PageTables/BtnForceExec", udon, "ExecuteForceReset");

        // 権限のない端末では実行時に消える。Editor では中身が見えるように出しておく。
        Find(t, "Panel").gameObject.SetActive(true);
        EditorSceneManager.MarkSceneDirty(root.scene);
        Debug.Log("[BuildStaffPanel] 完了：ボタン配線 " + wired + " 件。スタッフ表示名は StaffOnlyArea.staffDisplayNames が正です。");
    }

    [MenuItem("Tools/Kemono Mahjong/Validate Staff Panel")]
    public static void Validate()
    {
        int errors = 0;
        GameObject root = FindSceneObject(RootName);
        if (root == null)
        {
            Debug.LogError("[ValidateStaffPanel] " + RootName + " がシーンにありません。Build Staff Panel を実行してください。");
            return;
        }

        StaffEventPanel panel = root.GetComponent<StaffEventPanel>();
        if (panel == null) { Debug.LogError("[ValidateStaffPanel] StaffEventPanel が付いていません。"); return; }
        if (panel.matchmaking == null) { Debug.LogError("[ValidateStaffPanel] matchmaking が未割当。"); ++errors; }
        if (panel.staffArea == null) { Debug.LogError("[ValidateStaffPanel] staffArea が未割当。"); ++errors; }
        if (panel.panelRoot == null) { Debug.LogError("[ValidateStaffPanel] panelRoot が未割当。"); ++errors; }
        if (panel.statusText == null) { Debug.LogError("[ValidateStaffPanel] statusText が未割当。"); ++errors; }
        if (panel.columnLabels == null || panel.columnLabels.Length != 4)
        {
            Debug.LogError("[ValidateStaffPanel] columnLabels が4件ではありません。");
            ++errors;
        }
        else
        {
            for (int i = 0; i < 4; ++i)
            {
                if (panel.columnLabels[i] == null)
                {
                    Debug.LogError("[ValidateStaffPanel] columnLabels[" + i + "] が未割当。");
                    ++errors;
                }
            }
        }

        // Owner 側の検証に使う allowlist。ここが欠けると誰も列を操作できない。
        MatchmakingManager mm = UnityEngine.Object.FindObjectOfType<MatchmakingManager>(true);
        if (mm == null) { Debug.LogError("[ValidateStaffPanel] MatchmakingManager がありません。"); ++errors; }
        else if (mm.staffArea == null)
        {
            Debug.LogError("[ValidateStaffPanel] MatchmakingManager.staffArea が未割当＝Owner側の承認が通りません。");
            ++errors;
        }

        StaffOnlyArea area = UnityEngine.Object.FindObjectOfType<StaffOnlyArea>(true);
        if (area != null && (area.staffDisplayNames == null || area.staffDisplayNames.Length == 0))
        {
            Debug.LogWarning("[ValidateStaffPanel] StaffOnlyArea.staffDisplayNames が空です。誰にもパネルが出ません。");
        }

        if (panel.pageColumns == null) { Debug.LogError("[ValidateStaffPanel] pageColumns が未割当。"); ++errors; }
        if (panel.pageRules == null) { Debug.LogError("[ValidateStaffPanel] pageRules が未割当。"); ++errors; }
        if (panel.pageDisplay == null) { Debug.LogError("[ValidateStaffPanel] pageDisplay が未割当。"); ++errors; }
        if (panel.staffBadgeLabel == null) { Debug.LogError("[ValidateStaffPanel] staffBadgeLabel が未割当。"); ++errors; }
        if (panel.soundproofLabel == null) { Debug.LogError("[ValidateStaffPanel] soundproofLabel が未割当。"); ++errors; }
        if (panel.pageTables == null) { Debug.LogError("[ValidateStaffPanel] pageTables が未割当。"); ++errors; }
        if (panel.tableOrderImages == null || panel.tableOrderImages.Length != OrderButtonNames.Length)
        {
            Debug.LogError("[ValidateStaffPanel] tableOrderImages が " + OrderButtonNames.Length + " 件ではありません。");
            ++errors;
        }
        else
        {
            for (int i = 0; i < OrderButtonNames.Length; ++i)
            {
                if (panel.tableOrderImages[i] == null)
                {
                    Debug.LogError("[ValidateStaffPanel] tableOrderImages[" + i + "] が未割当。");
                    ++errors;
                }
            }
        }
        if (panel.tableOrderLabels == null || panel.tableOrderLabels.Length != OrderButtonNames.Length)
        {
            Debug.LogError("[ValidateStaffPanel] tableOrderLabels が " + OrderButtonNames.Length + " 件ではありません。");
            ++errors;
        }
        else
        {
            for (int i = 0; i < OrderButtonNames.Length; ++i)
            {
                if (panel.tableOrderLabels[i] == null)
                {
                    Debug.LogError("[ValidateStaffPanel] tableOrderLabels[" + i + "] が未割当。");
                    ++errors;
                }
            }
        }
        if (panel.forceResetModeLabel == null)
        {
            Debug.LogError("[ValidateStaffPanel] forceResetModeLabel が未割当。");
            ++errors;
        }
        if (panel.forceResetConfirmLabel == null)
        {
            Debug.LogError("[ValidateStaffPanel] forceResetConfirmLabel が未割当。");
            ++errors;
        }
        if (panel.forceResetConfirmImage == null)
        {
            Debug.LogError("[ValidateStaffPanel] forceResetConfirmImage が未割当。");
            ++errors;
        }
        if (panel.ruleLabels == null || panel.ruleLabels.Length != RuleButtonNames.Length)
        {
            Debug.LogError("[ValidateStaffPanel] ruleLabels が " + RuleButtonNames.Length + " 件ではありません。");
            ++errors;
        }

        Transform t = root.transform;
        errors += CheckButtons(t, "Panel/PageColumns/", ColumnButtonNames);
        errors += CheckButtons(t, "Panel/PageRules/", RuleButtonNames);
        errors += CheckButtons(t, "Panel/", new string[] { "BtnTabColumns", "BtnTabRules", "BtnTabDisplay", "BtnTabTables" });
        errors += CheckButtons(t, "Panel/PageDisplay/", new string[] { "BtnStaffBadge", "BtnSoundproof" });
        errors += CheckButtons(t, "Panel/PageTables/", OrderButtonNames);
        errors += CheckButtons(t, "Panel/PageTables/",
            new string[] { "BtnOrderReset", "BtnForceMode", "BtnForceExec" });
        errors += CheckButtons(t, "Panel/PageColumns/", new string[] { "BtnOpenAll" });
        errors += CheckButtons(t, "Panel/PageRules/", new string[] { "BtnRuleReset" });

        // 段位戦へ入れてはいけないルールのボタンが紛れ込んでいないか。
        foreach (var b in root.GetComponentsInChildren<Button>(true))
        {
            string n = b.name;
            if (n.Contains("Aotenjou") || n.Contains("Length") || n.Contains("Ikkyoku"))
            {
                Debug.LogError("[ValidateStaffPanel] 段位戦に出してはいけないボタンがあります: " + n, b);
                ++errors;
            }
        }

        if (errors == 0) { Debug.Log("[ValidateStaffPanel] OK：参照・配線に欠けはありません。"); }
        else { Debug.LogError("[ValidateStaffPanel] エラー " + errors + " 件。"); }
    }

    // ============================================================
    // UI
    // ============================================================

    private static void BuildUi(Transform root)
    {
        Transform panel = FullPanel(root, "Panel", PanelColor);

        Text(panel, "Title", "スタッフパネル", 36f, new Vector2(0f, -12f), new Vector2(750f, 52f));

        // 旧レイアウト（1ページだった頃）の置き土産。名前ごと残ると Validate が拾えないので消す。
        DestroyIfExists(panel, "Subtitle");
        foreach (var stale in ColumnButtonNames) { DestroyIfExists(panel, stale); }
        DestroyIfExists(panel, "BtnOpenAll");
        DestroyIfExists(panel, "Note");

        // タブ。ページ切替はローカルなので同期は要らない。
        // 4本に増えたので幅を 165 へ詰める（30 + 165*4 + 10*3 = 720、右余白30）。
        Button(panel, "BtnTabColumns", "待機列", new Vector2(30f, -70f), new Vector2(165f, 62f), 20f);
        Button(panel, "BtnTabRules", "イベントルール", new Vector2(205f, -70f), new Vector2(165f, 62f), 20f);
        Button(panel, "BtnTabDisplay", "表示", new Vector2(380f, -70f), new Vector2(165f, 62f), 20f);
        Button(panel, "BtnTabTables", "卓順", new Vector2(555f, -70f), new Vector2(165f, 62f), 20f);

        // --- ページ1：列の開閉 ---
        var pageColumns = Page(panel, "PageColumns", true);
        Button(pageColumns, ColumnButtonNames[0], ColumnInitialLabels[0], new Vector2(30f, -145f), new Vector2(690f, 100f), 26f);
        Button(pageColumns, ColumnButtonNames[1], ColumnInitialLabels[1], new Vector2(30f, -255f), new Vector2(690f, 100f), 26f);
        Button(pageColumns, ColumnButtonNames[2], ColumnInitialLabels[2], new Vector2(30f, -365f), new Vector2(690f, 100f), 26f);
        Button(pageColumns, ColumnButtonNames[3], ColumnInitialLabels[3], new Vector2(30f, -475f), new Vector2(690f, 100f), 26f);
        Button(pageColumns, "BtnOpenAll", "すべて公開に戻す", new Vector2(30f, -595f), new Vector2(690f, 76f), 27f, OpenAllColor);
        Text(pageColumns, "Note",
            "・停止した列は全員のメインメニューから消えます。\n"
            + "　並んでいた人は列から降ります。\n"
            + "・対局中の卓には影響しません。最後まで進みます。\n"
            + "・フリー対局はこのパネルの対象外です。",
            20f, new Vector2(35f, -690f), new Vector2(680f, 130f),
            new Color(0.78f, 0.76f, 0.7f, 1f), TextAlignmentOptions.TopLeft);

        // --- ページ2：イベントルール ---
        // 2列4行。押すたびに次の値へ回る（フリー対局の設定画面と同じ操作感）。
        var pageRules = Page(panel, "PageRules", false);
        for (int i = 0; i < RuleButtonNames.Length; ++i)
        {
            float x = (i % 2 == 0) ? 30f : 380f;
            float y = -145f - (i / 2) * 95f;
            Button(pageRules, RuleButtonNames[i], RuleInitialLabels[i],
                new Vector2(x, y), new Vector2(340f, 84f), 22f);
        }
        Button(pageRules, "BtnRuleReset", "標準ルールに戻す", new Vector2(30f, -540f), new Vector2(690f, 76f), 27f, OpenAllColor);
        Text(pageRules, "RuleNote",
            "・いま成立する段位戦に適用されます（対局中の卓は変わりません）。\n"
            + "・段位・天珠には通常どおり反映されます。\n"
            + "・青天井と一局戦は段位が壊れるため選べません。\n"
            + "　青天井のイベントはフリー対局で立ててください。",
            20f, new Vector2(35f, -635f), new Vector2(680f, 150f),
            new Color(0.78f, 0.76f, 0.7f, 1f), TextAlignmentOptions.TopLeft);

        // --- ページ3：表示（頭上のスタッフバナー） ---
        // ルールではないのでイベントルールのページには置かない。ここは「見た目の出し入れ」の棚。
        var pageDisplay = Page(panel, "PageDisplay", false);
        Button(pageDisplay, "BtnStaffBadge", "頭上のスタッフ表示：非表示",
            new Vector2(30f, -160f), new Vector2(690f, 110f), 28f);
        // 観戦ルームの防音（既定 ON）。ルールではなく「場の設定」なのでこの棚に置く。
        Button(pageDisplay, "BtnSoundproof", "観戦ルームの防音：ON",
            new Vector2(30f, -285f), new Vector2(690f, 110f), 28f);
        Text(pageDisplay, "DisplayNote",
            "・スタッフ表示：段位アイコンの上に出る「スタッフ」の札を、\n"
            + "　全スタッフぶんまとめて出し入れします。\n"
            + "・防音：ONの間、観戦ルームにいる人の声はルームの外へ\n"
            + "　聞こえません（床の防音マークも出ます）。OFFで普通に戻ります。\n"
            + "・どちらも、その場の全員の画面に効きます。\n"
            + "・インスタンス内だけの設定です。立て直すと既定\n"
            + "　（スタッフ表示＝非表示／防音＝ON）に戻ります。",
            20f, new Vector2(35f, -420f), new Vector2(680f, 230f),
            new Color(0.78f, 0.76f, 0.7f, 1f), TextAlignmentOptions.TopLeft);

        // --- ページ4：卓順（空卓を埋める順番） ---
        // 上から順に「空いていれば案内する」卓。行をタップして選び、もう1行で入れ替える。
        var pageTables = Page(panel, "PageTables", false);
        // 行は 58 高さ / 64 ピッチまで詰める。下に強制リセットの2段を置くため。
        for (int i = 0; i < OrderButtonNames.Length; ++i)
        {
            Button(pageTables, OrderButtonNames[i], "　" + (i + 1) + "番目　　卓 " + (i + 1),
                new Vector2(30f, -145f - i * 64f), new Vector2(690f, 58f), 24f);
        }
        // 並べ替えの戻しと、強制リセットのモード切替を横に並べる。
        Button(pageTables, "BtnOrderReset", "(1)→(8) に戻す",
            new Vector2(30f, -665f), new Vector2(340f, 60f), 22f, OpenAllColor);
        Button(pageTables, "BtnForceMode", "強制リセット：OFF",
            new Vector2(380f, -665f), new Vector2(340f, 60f), 22f);
        // 実行は必ず別ボタン（同じ行を2度押す方式だと連打で抜けてしまう）。
        Button(pageTables, "BtnForceExec", "実行（先に卓を選ぶ）",
            new Vector2(30f, -733f), new Vector2(690f, 64f), 24f, ForceIdleColor);

        Text(panel, "StaffStatus", "段位戦の待機列を開閉できます。", 21f,
            new Vector2(35f, -808f), new Vector2(680f, 180f),
            Color.white, TextAlignmentOptions.TopLeft);
    }

    // ============================================================
    // 以下、BuildRankAdminMonitor と同じ作りのヘルパー
    // ============================================================

    private static void EnsureRootComponents(GameObject root)
    {
        RectTransform rt = root.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(PanelWidth, PanelHeight);

        Canvas canvas = root.GetComponent<Canvas>();
        if (canvas == null) { canvas = Undo.AddComponent<Canvas>(root); }
        canvas.renderMode = RenderMode.WorldSpace;

        CanvasScaler scaler = root.GetComponent<CanvasScaler>();
        if (scaler == null) { scaler = Undo.AddComponent<CanvasScaler>(root); }
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.dynamicPixelsPerUnit = 10f;

        if (root.GetComponent<GraphicRaycaster>() == null) { Undo.AddComponent<GraphicRaycaster>(root); }
        if (root.GetComponent<VRCUiShape>() == null) { Undo.AddComponent<VRCUiShape>(root); }

        // VRCUiShape だけではポインタが当たらない。Canvas の rect と同じ BoxCollider が要る。
        BoxCollider shapeCollider = root.GetComponent<BoxCollider>();
        if (shapeCollider == null) { shapeCollider = Undo.AddComponent<BoxCollider>(root); }
        shapeCollider.isTrigger = true;
        shapeCollider.size = new Vector3(PanelWidth, PanelHeight, 0.31f);
        shapeCollider.center = new Vector3(0f, 0f, 0.16f);
    }

    private static Transform FullPanel(Transform root, string name, Color color)
    {
        Transform t = Child(root, name);
        RectTransform rt = (RectTransform)t;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        // パネルが Canvas 平面から浮く・回るとレーザーが当たらなくなる（見えているのに押せない）。
        rt.localRotation = Quaternion.identity;
        rt.localScale = Vector3.one;
        rt.localPosition = new Vector3(rt.localPosition.x, rt.localPosition.y, 0f);
        EnsureImage(t, color);
        t.gameObject.SetActive(true);
        return t;
    }

    // ページのルート。絵は持たない（枠は親のパネルが描く）。
    private static Transform Page(Transform parent, string name, bool active)
    {
        var t = Child(parent, name);
        var rt = (RectTransform)t;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        rt.localRotation = Quaternion.identity;
        rt.localScale = Vector3.one;
        rt.localPosition = new Vector3(rt.localPosition.x, rt.localPosition.y, 0f);
        t.gameObject.SetActive(active);
        return t;
    }

    private static void DestroyIfExists(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null) { Undo.DestroyObjectImmediate(t.gameObject); }
    }

    private static Transform Child(Transform parent, string name)
    {
        Transform t = parent.Find(name);
        if (t == null)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(go, "Build Staff Panel");
            t = go.transform;
            t.SetParent(parent, false);
        }
        return t;
    }

    private static void SetTopRect(Transform t, Vector2 topLeft, Vector2 size)
    {
        RectTransform rt = (RectTransform)t;
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = topLeft;
        rt.sizeDelta = size;
        rt.localRotation = Quaternion.identity;
        rt.localScale = Vector3.one;
        rt.localPosition = new Vector3(rt.localPosition.x, rt.localPosition.y, 0f);
    }

    private static void Stretch(Transform t, float inset)
    {
        RectTransform rt = (RectTransform)t;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(inset, inset);
        rt.offsetMax = new Vector2(-inset, -inset);
        rt.localRotation = Quaternion.identity;
        rt.localScale = Vector3.one;
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
        return Button(parent, name, label, topLeft, size, fontSize, ButtonColor);
    }

    private static Button Button(Transform parent, string name, string label, Vector2 topLeft, Vector2 size,
        float fontSize, Color color)
    {
        Transform t = Child(parent, name);
        SetTopRect(t, topLeft, size);
        Image image = EnsureImage(t, color);
        Button button = t.GetComponent<Button>();
        if (button == null) { button = Undo.AddComponent<Button>(t.gameObject); }
        button.targetGraphic = image;

        Transform labelTransform = Child(t, "Label");
        Stretch(labelTransform, 8f);
        TextMeshProUGUI tmp = labelTransform.GetComponent<TextMeshProUGUI>();
        if (tmp == null) { tmp = Undo.AddComponent<TextMeshProUGUI>(labelTransform.gameObject); }
        tmp.font = font_;
        // ラベルは実行時に StaffEventPanel が書き換える。ここでは初期表示だけ入れる。
        tmp.text = label;
        tmp.fontSize = fontSize;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = Color.white;
        tmp.raycastTarget = false;
        return button;
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
        if (t == null) { Debug.LogError("[BuildStaffPanel] UIが見つかりません: " + path); }
        return t;
    }

    private static TextMeshProUGUI FindTmp(Transform root, string path)
    {
        Transform t = Find(root, path);
        return t != null ? t.GetComponent<TextMeshProUGUI>() : null;
    }

    private static bool EnsureProgramAsset(string scriptPath, string assetPath)
    {
        MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
        if (script == null)
        {
            Debug.LogError("[BuildStaffPanel] スクリプトが見つかりません: " + scriptPath);
            return false;
        }
        Type type = script.GetClass();
        if (type != null && UdonSharpProgramAsset.GetProgramAssetForClass(type) != null) { return false; }
        if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(assetPath) != null) { return false; }

        UdonSharpProgramAsset asset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
        asset.sourceCsScript = script;
        AssetDatabase.CreateAsset(asset, assetPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[BuildStaffPanel] U# Program Asset を生成: " + assetPath);
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
            Debug.LogError("[BuildStaffPanel] StaffEventPanel を追加できません。U#コンパイル後に再実行してください。");
            if (added != null) { UnityEngine.Object.DestroyImmediate(added); }
            return null;
        }
        return added;
    }

    private static int CheckButtons(Transform root, string prefix, string[] names)
    {
        int errors = 0;
        for (int i = 0; i < names.Length; ++i)
        {
            var b = root.Find(prefix + names[i]);
            if (b == null)
            {
                Debug.LogError("[ValidateStaffPanel] " + names[i] + " がありません。");
                ++errors;
                continue;
            }
            var btn = b.GetComponent<Button>();
            if (btn == null || btn.onClick.GetPersistentEventCount() == 0)
            {
                Debug.LogError("[ValidateStaffPanel] " + names[i] + " の onClick が空です。", b);
                ++errors;
            }
        }
        return errors;
    }

    private static int WireButton(Transform root, string path, UdonBehaviour udon, string eventName)
    {
        Transform t = Find(root, path);
        if (t == null) { return 0; }
        Button button = t.GetComponent<Button>();
        if (button == null)
        {
            Debug.LogError("[BuildStaffPanel] Button がありません: " + path);
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
