#if UNITY_EDITOR
// 頭上ネームプレート（ランクアイコン＋待機ステータス）の骨格生成と配線。
//   Tools > Kemono Mahjong > Build Nameplates … プール(World Space Canvas)と
//                                                 マネージャ用の空オブジェクトを生成。
//   Tools > Kemono Mahjong > Wire Nameplates  … U#コンポーネント追加＋参照割り当て。
// どちらも2回実行しても安全（冪等）。デザインは後で調整する前提。
using TMPro;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using SuzuFactory.AozoraMahjong;
using VRC.Udon;

public static class BuildNameplates
{
    static readonly string[] RankIconPaths =
    {
        "Assets/Mahjong/Textures/UI/rank/初獣.png",
        "Assets/Mahjong/Textures/UI/rank/鋭爪.png",
        "Assets/Mahjong/Textures/UI/rank/剛牙.png",
        "Assets/Mahjong/Textures/UI/rank/獣威.png",
        "Assets/Mahjong/Textures/UI/rank/幻獣.png",
        "Assets/Mahjong/Textures/UI/rank/天獣.png"
    };

    // 作り置きするプレート枚数。近接カリングで実際の表示は数枚。
    // 近くに大人数が密集する運用なら増やす（Quest負荷とのトレードオフ）。
    const int PoolSize = 30;

    const string RootName = "Nameplates";
    const string FontAssetPath = "Assets/Mahjong/Fonts/NotoSansJP-Bold SDF.asset";
    const string ProgramAssetDir = "Assets/Mahjong/scene_UdonProgramSources";

    // プレートの見かけサイズ（OverheadNameplateManager.plateScale と整合）。
    // 幅640 = アイコン136 ＋ 間隔24 ＋ ステータス最大幅448（「プレイヤー募集中」実測）＋ 余白32。
    // 幅を詰めると文字が折り返し無しであふれ、左のロゴに重なる（過去の不具合）。
    static readonly Vector2 PlateSize = new Vector2(640f, 160f);
    static readonly Vector3 PlateScale = new Vector3(0.0015f, 0.0015f, 0.0015f);

    // スタッフ章。段位アイコン(136角)より少しだけ広く・薄く。
    // Y は OverheadNameplateManager.STAFF_BADGE_Y と同じ値にすること
    // （生成時とランタイムの追従で高さが食い違うと、章だけ跳ねて見える）。
    static readonly Vector2 StaffBadgeSize = new Vector2(133f, 38f);
    const float StaffBadgeY = 112f;

    static TMP_FontAsset font_;

    // ============================================================
    // Build：骨格生成
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Build Nameplates")]
    public static void Build()
    {
        font_ = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
        if (font_ == null)
        {
            Debug.LogWarning("[BuildNameplates] 日本語フォントアセットが見つかりません（" + FontAssetPath + "）。TMPの既定フォントで生成します。");
        }

        var root = GameObject.Find(RootName);
        if (root == null)
        {
            root = new GameObject(RootName);
            Undo.RegisterCreatedObjectUndo(root, "Create Nameplates");
        }
        root.transform.position = Vector3.zero;

        EnsureChild(root.transform, "MatchmakingManager");
        EnsureChild(root.transform, "OverheadNameplateManager");
        var pool = EnsureChild(root.transform, "Pool");

        for (int i = 0; i < PoolSize; ++i)
        {
            BuildPlate(pool.transform, i);
        }
        // 余分な旧プレート（PoolSize を減らした場合）を掃除。
        for (int i = pool.transform.childCount - 1; i >= 0; --i)
        {
            var c = pool.transform.GetChild(i);
            if (c.name.StartsWith("Plate"))
            {
                int idx;
                if (int.TryParse(c.name.Substring(5), out idx) && idx >= PoolSize)
                {
                    Undo.DestroyObjectImmediate(c.gameObject);
                }
            }
        }

        // VRChatのUIはDefaultレイヤー前提（描画・レーザー整合）。
        SetLayerRecursively(root, 0);

        EditorSceneManager.MarkSceneDirty(root.scene);
        Debug.Log("[BuildNameplates] 完了：プール " + PoolSize + " 枚を生成/更新。次に Wire Nameplates を実行してください。");
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

    static void BuildPlate(Transform pool, int index)
    {
        string plateName = "Plate" + index;
        var existing = pool.Find(plateName);
        GameObject plate = existing != null ? existing.gameObject : null;
        if (plate == null)
        {
            plate = new GameObject(plateName);
            Undo.RegisterCreatedObjectUndo(plate, "Create " + plateName);
            plate.transform.SetParent(pool, false);
        }

        // Canvas（World Space）。位置/回転/スケールは実行時にManagerが上書きする。
        var canvas = plate.GetComponent<Canvas>();
        if (canvas == null) { canvas = plate.AddComponent<Canvas>(); }
        canvas.renderMode = RenderMode.WorldSpace;

        var scaler = plate.GetComponent<CanvasScaler>();
        if (scaler == null) { scaler = plate.AddComponent<CanvasScaler>(); }
        scaler.dynamicPixelsPerUnit = 3f;
        // GraphicRaycaster は付けない（入力不要・負荷減）。

        var rt = plate.GetComponent<RectTransform>();
        rt.sizeDelta = PlateSize;
        plate.transform.localScale = PlateScale;

        // 待機中だけ出す象牙の札。MainMenu と同じ配色・同じ墨の縁取りにする。
        //
        // 【なぜ常時出さないのか】
        //   段位ロゴは全員の頭上に常時出る。そこへ不透明の札まで常時足すと、
        //   50人規模で視界が札で埋まる。札は「待機中」という一時的な状態の時だけ出し、
        //   通常時はロゴだけ（＝従来どおり背景なし）にする。出し入れは Manager が行う。
        var bg = EnsureUiChild(plate.transform, "BG");
        var bgImg = bg.GetComponent<Image>();
        if (bgImg == null) { bgImg = bg.AddComponent<Image>(); }
        bgImg.sprite = null;
        // 完全な不透明にはしない。後ろのアバターが少し透けたほうが頭上の圧迫感が減る。
        bgImg.color = new Color(StyleMainMenu.Paper.r, StyleMainMenu.Paper.g, StyleMainMenu.Paper.b, 0.94f);
        bgImg.raycastTarget = false;
        bgImg.enabled = true;
        // 枠に入れるのは文字だけ。段位ロゴは枠の外（左）に置く。
        //   ロゴ 0..136 / 間隔 16 / 札 152..640（幅488）
        // 上下も少し詰めて、ロゴ(136)より札(132)を低くする＝ロゴが枠から少しはみ出て見える。
        var bgRt = bg.GetComponent<RectTransform>();
        bgRt.anchorMin = Vector2.zero;
        bgRt.anchorMax = Vector2.one;
        bgRt.pivot = new Vector2(0.5f, 0.5f);
        bgRt.offsetMin = new Vector2(152f, 14f);
        bgRt.offsetMax = new Vector2(0f, -14f);
        StyleMainMenu.Frame(bgImg, 5f, 0f);   // 墨の縁取り。影は付けない（頭上で二重に見える）
        bg.transform.SetSiblingIndex(0);
        bg.SetActive(false);                   // 既定は消灯。Manager が待機中だけ出す

        // 列の差し色（四麻=金 / 三麻=翠 / フリー=桃）。左端の細い帯。
        // 文字を読まなくても「どの列で待っているか」が色で分かる。
        var accent = EnsureUiChild(plate.transform, "Accent");
        var accentImg = accent.GetComponent<Image>();
        if (accentImg == null) { accentImg = accent.AddComponent<Image>(); }
        accentImg.sprite = null;
        accentImg.color = StyleMainMenu.Yellow;
        accentImg.raycastTarget = false;
        var acRt = accent.GetComponent<RectTransform>();
        acRt.anchorMin = new Vector2(0f, 0.5f);
        acRt.anchorMax = new Vector2(0f, 0.5f);
        acRt.pivot = new Vector2(0f, 0.5f);
        // 札の左端の内側。札が 152 から始まるので、その少し内へ入れる。
        acRt.anchoredPosition = new Vector2(166f, 0f);
        acRt.sizeDelta = new Vector2(12f, 88f);
        accent.transform.SetSiblingIndex(1);
        accent.SetActive(false);

        // 通常時は中央。待機ステータス表示中だけ Manager が左（札の外）へ移動する。
        var icon = EnsureUiChild(plate.transform, "RankIcon");
        var raw = icon.GetComponent<RawImage>();
        if (raw == null) { raw = icon.AddComponent<RawImage>(); }
        var iconRt = icon.GetComponent<RectTransform>();
        iconRt.anchorMin = new Vector2(0.5f, 0.5f);
        iconRt.anchorMax = new Vector2(0.5f, 0.5f);
        iconRt.pivot = new Vector2(0.5f, 0.5f);
        iconRt.anchoredPosition = Vector2.zero;
        iconRt.sizeDelta = new Vector2(136f, 136f);

        // ステータス文字（アイコンの右）。Rect は x:-144〜+304（幅448）でロゴと重ならない。
        var status = EnsureUiChild(plate.transform, "Status");
        var tmp = status.GetComponent<TextMeshProUGUI>();
        if (tmp == null) { tmp = status.AddComponent<TextMeshProUGUI>(); }
        if (font_ != null) { tmp.font = font_; }
        tmp.text = "四麻半荘 待機中";
        tmp.fontSize = 54f;
        // 英語の「4P Half Game - waiting」は日本語より長く、折り返し無しのままだと
        // 枠からあふれて段位ロゴに重なる（2026-08-18 に一度踏んだ不具合）。
        // 幅を広げる余地はもう無いので、あふれる時は字を縮めて逃がす。
        tmp.enableAutoSizing = true;
        tmp.fontSizeMax = 54f;
        tmp.fontSizeMin = 34f;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        // 象牙の札の上に載るので墨。白＋黒フチ（旧デザイン）はもう要らない。
        tmp.color = StyleMainMenu.Ink;
        tmp.outlineWidth = 0f;
        var stRt = status.GetComponent<RectTransform>();
        stRt.anchorMin = new Vector2(0f, 0f);
        stRt.anchorMax = new Vector2(1f, 1f);
        // 札（152..640）の内側。左は差し色の帯を避け、右は縁取りぶんを空ける。
        stRt.offsetMin = new Vector2(190f, 0f);
        stRt.offsetMax = new Vector2(-24f, 0f);

        // スタッフ章（段位アイコンの上）。MainMenu と同じ象牙紙＋墨の縁取り＋墨文字。
        // 常時出す札ではなく allowlist に載っている人だけなので、待機札と違って
        // 頭上が埋まる心配はない。既定は消灯で、Manager がスタッフの時だけ点ける。
        // 位置(X)は Manager が段位アイコンに追従させる（待機中はアイコンごと左へ動く）。
        var staff = EnsureUiChild(plate.transform, "StaffBadge");
        var staffImg = staff.GetComponent<Image>();
        if (staffImg == null) { staffImg = staff.AddComponent<Image>(); }
        staffImg.sprite = null;
        staffImg.color = new Color(StyleMainMenu.Paper.r, StyleMainMenu.Paper.g, StyleMainMenu.Paper.b, 0.94f);
        staffImg.raycastTarget = false;
        var stfRt = staff.GetComponent<RectTransform>();
        stfRt.anchorMin = new Vector2(0.5f, 0.5f);
        stfRt.anchorMax = new Vector2(0.5f, 0.5f);
        stfRt.pivot = new Vector2(0.5f, 0.5f);
        stfRt.anchoredPosition = new Vector2(0f, StaffBadgeY);
        stfRt.sizeDelta = StaffBadgeSize;
        StyleMainMenu.Frame(staffImg, 3f, 0f);   // 待機札と同じ墨の縁取り。影は付けない（札が小さいぶん細く）
        staff.SetActive(false);

        var staffLabel = EnsureUiChild(staff.transform, "Label");
        var staffTmp = staffLabel.GetComponent<TextMeshProUGUI>();
        if (staffTmp == null) { staffTmp = staffLabel.AddComponent<TextMeshProUGUI>(); }
        if (font_ != null) { staffTmp.font = font_; }
        staffTmp.text = "スタッフ";
        staffTmp.fontSize = 27f;
        staffTmp.enableAutoSizing = true;
        staffTmp.fontSizeMax = 27f;
        staffTmp.fontSizeMin = 18f;
        staffTmp.alignment = TextAlignmentOptions.Center;
        staffTmp.enableWordWrapping = false;
        staffTmp.color = StyleMainMenu.Ink;
        staffTmp.outlineWidth = 0f;
        var slRt = staffLabel.GetComponent<RectTransform>();
        slRt.anchorMin = Vector2.zero;
        slRt.anchorMax = Vector2.one;
        slRt.offsetMin = new Vector2(8f, 0f);
        slRt.offsetMax = new Vector2(-8f, 0f);

        // 生成直後は非表示（Managerが必要時に出す）。子は活かしたまま親だけ畳む。
        plate.SetActive(false);
    }

    static GameObject EnsureUiChild(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null) { return t.gameObject; }
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Create " + name);
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();
        return go;
    }

    static void StretchFull(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    // ============================================================
    // Wire：U#コンポーネント追加＋参照割り当て
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Wire Nameplates")]
    public static void Wire()
    {
        var root = GameObject.Find(RootName);
        if (root == null)
        {
            Debug.LogError("[WireNameplates] '" + RootName + "' が見つかりません。先に Build Nameplates を実行してください。");
            return;
        }

        // --- U#プログラムアセット生成（.cs直接作成のスクリプトには無いため） ---
        bool created = false;
        created |= EnsureProgramAsset(
            "Assets/Mahjong/Sources/MatchmakingManager.cs",
            ProgramAssetDir + "/MatchmakingManager Udon C# Program Asset.asset");
        created |= EnsureProgramAsset(
            "Assets/Mahjong/Sources/OverheadNameplateManager.cs",
            ProgramAssetDir + "/OverheadNameplateManager Udon C# Program Asset.asset");
        if (created)
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[WireNameplates] U#プログラムアセットを生成しました。コンパイル完了後、もう一度 Wire Nameplates を実行してください。");
            return;
        }

        var mmGo = root.transform.Find("MatchmakingManager");
        var npGo = root.transform.Find("OverheadNameplateManager");
        var poolT = root.transform.Find("Pool");
        if (mmGo == null || npGo == null || poolT == null)
        {
            Debug.LogError("[WireNameplates] 子オブジェクト（MatchmakingManager/OverheadNameplateManager/Pool）が不足。Build Nameplates を再実行してください。");
            return;
        }

        var matchmaking = (MatchmakingManager)EnsureUSharpComponent(mmGo.gameObject, typeof(MatchmakingManager));
        var nameplate = (OverheadNameplateManager)EnsureUSharpComponent(npGo.gameObject, typeof(OverheadNameplateManager));
        if (matchmaking == null || nameplate == null)
        {
            Debug.LogError("[WireNameplates] U#コンポーネント追加に失敗（プログラムアセット未コンパイル？）。コンパイル後に再実行してください。");
            return;
        }

        // PlayerStatsStore（Wire Main Menu 実行済み前提）。
        var store = Object.FindObjectOfType<PlayerStatsStore>(true);
        if (store == null)
        {
            Debug.LogWarning("[WireNameplates] PlayerStatsStore がシーンに見つかりません。先に Wire Main Menu を実行してください。ランクアイコンはstore設定後に機能します。");
        }

        nameplate.statsStore = store;
        nameplate.matchmaking = matchmaking;

        // スタッフ章の判定元。allowlist の実体は StaffOnlyArea に一本化してある
        // （＝スタッフを増やす時に触るのは StaffOnlyArea.staffDisplayNames の1か所だけ）。
        var staffArea = Object.FindObjectOfType<StaffOnlyArea>(true);
        if (staffArea == null)
        {
            Debug.LogWarning("[WireNameplates] StaffOnlyArea がシーンに見つかりません。先に Build Staff Only Area を実行してください。スタッフ章は出ません。");
        }
        nameplate.staffArea = staffArea;

        // プールから plates / plateRankIcons / plateStatusTexts を収集（Plate0..N-1 順）。
        int n = 0;
        foreach (Transform c in poolT) { if (c.name.StartsWith("Plate")) { ++n; } }

        var plates = new GameObject[n];
        var icons = new RawImage[n];
        var texts = new TextMeshProUGUI[n];
        var bgs = new GameObject[n];
        var accents = new Image[n];
        var staffBadges = new GameObject[n];
        var staffTexts = new TextMeshProUGUI[n];
        for (int i = 0; i < n; ++i)
        {
            var plate = poolT.Find("Plate" + i);
            if (plate == null) { continue; }
            plates[i] = plate.gameObject;
            var iconT = plate.Find("RankIcon");
            icons[i] = iconT != null ? iconT.GetComponent<RawImage>() : null;
            var stT = plate.Find("Status");
            texts[i] = stT != null ? stT.GetComponent<TextMeshProUGUI>() : null;
            var bgT = plate.Find("BG");
            bgs[i] = bgT != null ? bgT.gameObject : null;
            var acT = plate.Find("Accent");
            accents[i] = acT != null ? acT.GetComponent<Image>() : null;
            var stfT = plate.Find("StaffBadge");
            staffBadges[i] = stfT != null ? stfT.gameObject : null;
            var stfLabelT = stfT != null ? stfT.Find("Label") : null;
            staffTexts[i] = stfLabelT != null ? stfLabelT.GetComponent<TextMeshProUGUI>() : null;
        }
        nameplate.plates = plates;
        nameplate.plateRankIcons = icons;
        nameplate.plateStatusTexts = texts;
        nameplate.plateBackgrounds = bgs;
        nameplate.plateAccents = accents;
        nameplate.plateStaffBadges = staffBadges;
        nameplate.plateStaffTexts = staffTexts;

        // 差し色は MatchmakingManager.STATUS_* の並びで持つ（0=IDLEは使わない）。
        // MainMenu のホームと同じ色にして「金＝四麻／翠＝三麻」を世界で統一する。
        nameplate.statusAccentColors = new Color[]
        {
            StyleMainMenu.Ink,      // 0 IDLE（使わない）
            StyleMainMenu.Yellow,   // 1 四麻 半荘
            StyleMainMenu.Teal,     // 2 三麻 半荘
            StyleMainMenu.Pink,     // 3 プレイヤー募集中（フリー対局）
            StyleMainMenu.Yellow,   // 4 四麻 東風
            StyleMainMenu.Teal,     // 5 三麻 東風
        };

        // 大段位6種を確定順で自動割り当てする。
        // アセット欠損時だけ既存割当を保全し、ランタイム側では安全に非表示へ倒す。
        var oldRankIcons = nameplate.rankIcons;
        var rankIcons = new Texture[RankIconPaths.Length];
        for (int i = 0; i < RankIconPaths.Length; ++i)
        {
            rankIcons[i] = AssetDatabase.LoadAssetAtPath<Texture2D>(RankIconPaths[i]);
            if (rankIcons[i] == null && oldRankIcons != null && i < oldRankIcons.Length)
            {
                rankIcons[i] = oldRankIcons[i];
            }
            if (rankIcons[i] == null)
            {
                Debug.LogWarning("[WireNameplates] 段位アイコンが見つかりません: " + RankIconPaths[i]);
            }
        }
        nameplate.rankIcons = rankIcons;

        UdonSharpEditorUtility.CopyProxyToUdon(matchmaking);
        UdonSharpEditorUtility.CopyProxyToUdon(nameplate);
        EditorUtility.SetDirty(matchmaking);
        EditorUtility.SetDirty(nameplate);

        EditorSceneManager.MarkSceneDirty(root.scene);
        Debug.Log("[WireNameplates] 完了：プレート " + n + " 枚を割り当て。ランクアイコン(rankIcons)とサイズ/高さは Inspector で調整可。");
    }

    // ============================================================
    // 共通ヘルパ（WireMainMenu と同等の実装を複製）
    // ============================================================
    static bool EnsureProgramAsset(string scriptPath, string assetPath)
    {
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
        if (script == null)
        {
            Debug.LogError("[WireNameplates] スクリプトが見つかりません: " + scriptPath);
            return false;
        }
        var type = script.GetClass();
        if (type != null && UdonSharpProgramAsset.GetProgramAssetForClass(type) != null) { return false; }
        if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(assetPath) != null) { return false; }

        var programAsset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
        programAsset.sourceCsScript = script;
        AssetDatabase.CreateAsset(programAsset, assetPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[WireNameplates] U#プログラムアセットを生成: " + assetPath);
        return true;
    }

    static UdonSharpBehaviour EnsureUSharpComponent(GameObject go, System.Type type)
    {
        var proxy = (UdonSharpBehaviour)go.GetComponent(type);
        if (proxy != null)
        {
            if (UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy) != null) { return proxy; }

            Debug.LogWarning("[WireNameplates] " + type.Name + " の backing UdonBehaviour が無いため作り直します。");
            Object.DestroyImmediate(proxy);
            foreach (var ub in go.GetComponents<UdonBehaviour>())
            {
                if (ub.programSource == null) { Object.DestroyImmediate(ub); }
            }
        }

        var added = (UdonSharpBehaviour)UdonSharpUndo.AddComponent(go, type);
        if (added != null && UdonSharpEditorUtility.GetBackingUdonBehaviour(added) == null)
        {
            Debug.LogError("[WireNameplates] " + type.Name + " の追加に失敗（プログラムアセット未コンパイル？）。");
            Object.DestroyImmediate(added);
            return null;
        }
        Debug.Log("[WireNameplates] " + type.Name + " を " + go.name + " に追加しました。");
        return added;
    }

    static void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform c in go.transform) { SetLayerRecursively(c.gameObject, layer); }
    }
}
#endif
