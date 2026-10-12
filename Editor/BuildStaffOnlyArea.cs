#if UNITY_EDITOR
// カウンター（AetherLoft/AddedProps/Counter の Table_01 / Table_02）の裏をスタッフ専用エリアにする。
//   Tools > Kemono Mahjong > Build Staff Only Area    … 見えない壁を実測して置き直し、StaffOnlyArea を配線する（冪等）。
//   Tools > Kemono Mahjong > Validate Staff Only Area … 参照の検算＋「本当に入れないか」の到達判定のみ（変更なし）。
//
// 【なぜ壁が要るのか】
//   Table_01 / Table_02 はコライダーを持っていない（MeshFilter + MeshRenderer だけ）。
//   つまり現状は誰でもカウンターをすり抜けて裏へ入れる。裏には RankAdminMonitor（段位補正パネル）、
//   udonmic、Night_switch が置いてあるので、ここは運営側の場所として仕切る。
//
// 【壁の位置を実測で決める理由】
//   数字を直書きすると、カウンターを動かした瞬間に壁だけ取り残されて「見えない壁が空中にある」状態になる。
//   ここでは毎回シーンから測り直す：
//     ・カウンターの箱     … Table_01 / Table_02 のレンダラー境界の和
//     ・床の高さ           … カウンター手前へ下向きレイキャスト（Floor_1 = y 0.96）
//     ・東の行き止まり     … カウンター東端からプレイヤーカプセルを東へ進め、最初に詰まる x（手すり Wood_1F_fence_1）
//     ・西の袖の奥行き     … カウンター西端から南（-Z）へ同じ判定。最初に詰まる z（階段スラブ Col_2 と木箱）
//     ・西の袖の高さ       … 真上へレイキャスト。階段の裏側（Col_2, y≈2.28）に頭をぶつけない高さで止める
//
// 【MainMenu を塞がないための位置決め】
//   MainMenu の Canvas はカウンターの真上（z = 1.000）に立っていて、プレイヤーは +Z 側から読む。
//   VRChatのUIポインタは手前のコライダーで止まるので、壁を Canvas より手前に置くと
//   メインメニューが一切押せなくなる。よって前面の壁は全区間を Canvas 平面より数cm奥へ引く
//   （＝レイは必ず先に Canvas に当たる）。代償は「カウンター前面の約10cm手前で止まる」だけ。
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SuzuFactory.AozoraMahjong;
using VRC.Udon;

public static class BuildStaffOnlyArea
{
    const string PropsPath = "AetherLoft/AddedProps";
    const string CounterPath = "AetherLoft/AddedProps/Counter";
    const string TableAName = "Table_01";
    const string TableBName = "Table_02";
    const string MenuName = "MainMenu";

    const string AreaRootName = "StaffOnlyArea";
    const string BarrierRootName = "Barriers";
    const string WallFrontName = "Wall_Front";
    // 旧版（前面を2分割していた頃）の名前。残っていたら片付ける。
    const string LegacyWallMenuName = "Wall_Front_MenuSide";
    const string LegacyWallEastName = "Wall_Front_East";
    const string WallFlankName = "Wall_Flank_West";

    const string ScriptPath = "Assets/Mahjong/Sources/StaffOnlyArea.cs";
    const string ProgramAssetPath = "Assets/Mahjong/scene_UdonProgramSources/StaffOnlyArea Udon C# Program Asset.asset";

    // 壁の厚み。薄すぎるとすり抜けるので、プレイヤーの1フレーム移動より厚くする。
    const float WallThickness = 0.06f;
    // 床のわずかな段差で足元に隙間ができないよう、床より少し下から立ち上げる。
    const float WallSink = 0.06f;
    // 正面の壁の高さ（床から）。ジャンプ(約0.46m)では絶対に越えられない。
    const float FrontWallHeight = 2.30f;
    // 西の袖は階段の裏（Col_2）に頭をぶつけるので、天井から余裕を取って打ち切る。
    const float FlankCeilingMargin = 0.04f;
    const float FlankMaxHeight = 2.40f;
    // MainMenu の Canvas 平面より、この分だけ壁の手前面を奥へ引く。
    const float MenuClearance = 0.015f;
    // 実測した行き止まりより、この分だけ壁を伸ばして確実に塞ぐ。
    const float SealMargin = 0.15f;
    // 東は手すり(Wood_1F_fence_1)の向こうまで伸ばして、13cmの上がり框を踏んで回り込む経路も潰す。
    const float EastSealMargin = 0.45f;

    // プレイヤーカプセルの近似。到達判定と行き止まり探索で共用する。
    const float ProbeRadius = 0.18f;
    const float ProbeFootClearance = 0.30f;
    const float ProbeHeadClearance = 1.40f;

    // ============================================================
    // Build
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Build Staff Only Area")]
    public static void Build()
    {
        Bounds counter;
        if (!TryGetCounterBounds(out counter)) { return; }

        var props = GameObject.Find(PropsPath);
        if (props == null)
        {
            Debug.LogError("[BuildStaffOnlyArea] " + PropsPath + " がシーンにありません。");
            return;
        }
        // 前回のBuildで置いた壁が残っていると、この後の実測（どこで詰まるか）が
        // 自分の壁で詰まってしまい、壁がどんどん短くなる。測る前に必ず畳んでおく。
        var areaRoot = EnsureChild(props.transform, AreaRootName);
        areaRoot.transform.position = Vector3.zero;
        areaRoot.transform.rotation = Quaternion.identity;
        areaRoot.transform.localScale = Vector3.one;
        DestroyChild(areaRoot.transform, BarrierRootName);
        Physics.SyncTransforms();

        float floorY;
        if (!TryGetFloorY(counter, out floorY))
        {
            Debug.LogError("[BuildStaffOnlyArea] カウンター手前の床が取れませんでした（Floor_1 のコライダーを確認してください）。");
            return;
        }

        // 東の行き止まり（手すりの手前）。
        float eastBound = ProbeBlockedX(counter.max.x + 0.05f, counter.max.z - 0.30f, floorY, 2.4f);
        // 西の袖をどこまで伸ばすか（階段スラブ・木箱が引き継ぐところまで）。
        float flankSouthZ = ProbeBlockedZ(counter.min.x, counter.min.z - 0.05f, floorY, 2.0f);

        // MainMenu の Canvas 平面。取れない時はカウンター前面をそのまま使う（＝Canvas無しとして扱う）。
        float menuZ, menuMinX, menuMaxX;
        bool hasMenu = TryGetMenuPlane(out menuZ, out menuMinX, out menuMaxX);
        if (!hasMenu)
        {
            Debug.LogWarning("[BuildStaffOnlyArea] " + MenuName + " の Canvas が取れませんでした。UIを塞がない逃げが効きません（壁はカウンター前面に置きます）。");
            menuZ = counter.max.z;
            menuMinX = counter.min.x;
            menuMaxX = counter.min.x; // Canvas側の区間を無くす
        }

        // 前面の壁は「MainMenu の Canvas 平面より必ず奥」に置く。
        // VRChatのUIポインタは手前のコライダーで止まるので、Canvasより少しでも手前に出すと
        // メインメニューが押せなくなる。斜めから狙われた分だけ区間を分ける手もあるが、
        // 全区間を奥へ引く方が確実で、代償は「カウンター前面の約10cm手前で止まる」だけ。
        float frontFaceZ = Mathf.Min(menuZ - MenuClearance, counter.max.z);

        float wallBottom = floorY - WallSink;
        float frontTop = floorY + FrontWallHeight;
        float eastEdge = eastBound + EastSealMargin;

        var barrierRoot = EnsureChild(areaRoot.transform, BarrierRootName);
        barrierRoot.transform.position = Vector3.zero;
        barrierRoot.transform.rotation = Quaternion.identity;
        barrierRoot.transform.localScale = Vector3.one;
        barrierRoot.SetActive(true);

        // 旧版が置いた「前面2分割」の壁が残っていたら片付ける。
        DestroyChild(barrierRoot.transform, LegacyWallMenuName);
        DestroyChild(barrierRoot.transform, LegacyWallEastName);

        // 1) 前面: カウンター西端から東の行き止まり（手すりの向こう）まで一枚で塞ぐ。
        var wallFront = EnsureBox(barrierRoot.transform, WallFrontName,
            new Vector3(counter.min.x, wallBottom, frontFaceZ - WallThickness),
            new Vector3(eastEdge, frontTop, frontFaceZ));

        // 2) 西の袖: カウンター西端から南へ。階段の裏（Col_2）に頭をぶつけない高さで止める。
        float flankTop = ProbeCeiling(counter.min.x, flankSouthZ - SealMargin, frontFaceZ, floorY);
        flankTop = Mathf.Min(flankTop - FlankCeilingMargin, floorY + FlankMaxHeight);
        var wallFlank = EnsureBox(barrierRoot.transform, WallFlankName,
            new Vector3(counter.min.x - WallThickness * 0.5f, wallBottom, flankSouthZ - SealMargin),
            new Vector3(counter.min.x + WallThickness * 0.5f, flankTop, frontFaceZ));

        // ---- U# 本体 ----
        EnsureProgramAsset(ScriptPath, ProgramAssetPath);
        var comp = EnsureUSharpComponent(areaRoot, typeof(StaffOnlyArea)) as StaffOnlyArea;
        if (comp == null) { return; }

        var walls = new System.Collections.Generic.List<GameObject>();
        if (wallFront != null) { walls.Add(wallFront); }
        if (wallFlank != null) { walls.Add(wallFlank); }
        comp.barriers = walls.ToArray();

        // 名簿は運用で書き換えるものなので、空の時だけ既存のallowlist（RankAdminMonitor）から種を入れる。
        if (comp.staffDisplayNames == null || comp.staffDisplayNames.Length == 0)
        {
            comp.staffDisplayNames = new string[]
            {
                RankAdminMonitor.AUTHORIZED_PRIMARY_NAME,
                RankAdminMonitor.AUTHORIZED_STAFF_NAME,
                "<STAFF_NAME_3>",
            };
            Debug.Log("[BuildStaffOnlyArea] スタッフ名簿が空だったので RankAdminMonitor の allowlist を種として入れました（Inspectorで編集可）。");
        }

        // 入室通知音の再生元。2D にすること（スタッフが卓や2階にいても聞こえないと
        // 案内の合図として役に立たない）。VRCSpatialAudioSource が無いと
        // KemonoAudioNormalizer が Gain=24dB を入れられず、狙いより24dB小さく鳴る。
        comp.joinSource = EnsureLocalSfxSource(areaRoot.transform, "JoinChimeSource");

        UdonSharpEditorUtility.CopyProxyToUdon(comp);
        EditorUtility.SetDirty(comp);
        MarkDirty(areaRoot);

        Debug.Log(string.Format(
            "[BuildStaffOnlyArea] 完了：床 y={0:F2} / 前面 z={1:F3}（MainMenu Canvas z={2:F3}・x {3:F2}〜{4:F2} の奥）/ 東の行き止まり x={5:F2}→壁の東端 {6:F2} / 西の袖 z={7:F2}〜{8:F2}・上端 y={9:F2}",
            floorY, frontFaceZ, menuZ, menuMinX, menuMaxX, eastBound, eastEdge, flankSouthZ - SealMargin, frontFaceZ, flankTop));

        Validate();
    }

    // 2D のローカルSE用 AudioSource を用意する（既存があれば設定だけ揃える）。
    // 設定値は MatchmakingManager/MatchVoice に合わせてある＝ノーマライザの前提
    // （SourceGainDb = 24dB）と一致する唯一の組み合わせ。
    public static AudioSource EnsureLocalSfxSource(Transform parent, string name)
    {
        var t = parent.Find(name);
        GameObject go;
        if (t == null)
        {
            go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            go.transform.SetParent(parent, false);
        }
        else
        {
            go = t.gameObject;
        }

        var src = go.GetComponent<AudioSource>();
        if (src == null) { src = Undo.AddComponent<AudioSource>(go); }
        Undo.RecordObject(src, "Configure " + name);
        src.playOnAwake = false;
        src.loop = false;
        src.spatialBlend = 0f;          // 2D。位置に関係なく聞こえる
        src.minDistance = 1f;
        src.maxDistance = 500f;
        src.rolloffMode = AudioRolloffMode.Logarithmic;
        EditorUtility.SetDirty(src);

        var sp = go.GetComponent<VRC.SDK3.Components.VRCSpatialAudioSource>();
        if (sp == null) { sp = Undo.AddComponent<VRC.SDK3.Components.VRCSpatialAudioSource>(go); }
        Undo.RecordObject(sp, "Configure " + name);
        sp.Gain = KemonoAudioNormalizer.SourceGainDb;
        sp.Far = 80f;
        sp.Near = 0f;
        sp.VolumetricRadius = 0f;
        // 既定の true だと spatialBlend=0 を無視して距離減衰（逆二乗）が掛かる＝
        // 原点に置いた MatchSfx が卓では聞こえない。2D にするにはここを切る（VRChat公式の手順）。
        sp.EnableSpatialization = false;
        sp.UseAudioSourceVolumeCurve = false;
        EditorUtility.SetDirty(sp);

        return src;
    }

    // ============================================================
    // Validate（変更なし）
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Validate Staff Only Area")]
    public static void Validate()
    {
        var areaRoot = GameObject.Find(PropsPath + "/" + AreaRootName);
        if (areaRoot == null)
        {
            Debug.LogError("[ValidateStaffOnlyArea] " + AreaRootName + " がありません。Build Staff Only Area を実行してください。");
            return;
        }

        var comp = areaRoot.GetComponent<StaffOnlyArea>();
        if (comp == null)
        {
            Debug.LogError("[ValidateStaffOnlyArea] StaffOnlyArea が付いていません。Build を実行してください。");
            return;
        }
        if (UdonSharpEditorUtility.GetBackingUdonBehaviour(comp) == null)
        {
            Debug.LogError("[ValidateStaffOnlyArea] backing UdonBehaviour がありません（ビルドに乗りません）。Build を実行し直してください。");
        }

        int errors = 0;
        if (comp.barriers == null || comp.barriers.Length == 0)
        {
            Debug.LogError("[ValidateStaffOnlyArea] barriers が空です。"); errors++;
        }
        else
        {
            for (int i = 0; i < comp.barriers.Length; i++)
            {
                var go = comp.barriers[i];
                if (go == null) { Debug.LogError("[ValidateStaffOnlyArea] barriers[" + i + "] が null です。"); errors++; continue; }
                var box = go.GetComponent<BoxCollider>();
                if (box == null) { Debug.LogError("[ValidateStaffOnlyArea] " + go.name + " に BoxCollider がありません。"); errors++; continue; }
                if (box.isTrigger) { Debug.LogError("[ValidateStaffOnlyArea] " + go.name + " が isTrigger です（すり抜けます）。"); errors++; }
                if (go.GetComponent<Renderer>() != null) { Debug.LogWarning("[ValidateStaffOnlyArea] " + go.name + " にレンダラーが付いています（見えてしまいます）。"); }
                if (go.layer != 0) { Debug.LogWarning("[ValidateStaffOnlyArea] " + go.name + " のレイヤーが Default ではありません（プレイヤーと当たらない可能性）。"); }
            }
        }

        if (comp.staffDisplayNames == null || comp.staffDisplayNames.Length == 0)
        {
            Debug.LogWarning("[ValidateStaffOnlyArea] スタッフ名簿が空です。この状態では全員が締め出されます。");
        }
        else
        {
            Debug.Log("[ValidateStaffOnlyArea] スタッフ名簿 " + comp.staffDisplayNames.Length + " 名: " + string.Join(" / ", comp.staffDisplayNames));
        }

        // ---- 本当に入れないかを歩いて確かめる ----
        Bounds counter;
        if (!TryGetCounterBounds(out counter)) { return; }
        float floorY;
        if (!TryGetFloorY(counter, out floorY)) { return; }

        bool blockedNow = !CanWalkIntoPocket(counter, floorY);
        SetBarriers(comp, false);
        bool openWhenDisabled = CanWalkIntoPocket(counter, floorY);
        SetBarriers(comp, true);

        if (!blockedNow)
        {
            Debug.LogError("[ValidateStaffOnlyArea] 壁があるのに裏へ歩いて入れます（どこかに隙間があります）。"); errors++;
        }
        else
        {
            Debug.Log("[ValidateStaffOnlyArea] 非スタッフはカウンター裏へ到達できません ✓");
        }
        if (!openWhenDisabled)
        {
            Debug.LogError("[ValidateStaffOnlyArea] 壁を外してもスタッフが裏へ入れません（別の物が塞いでいます）。"); errors++;
        }
        else
        {
            Debug.Log("[ValidateStaffOnlyArea] スタッフ（壁OFF）はカウンター裏へ到達できます ✓");
        }

        if (errors == 0) { Debug.Log("[ValidateStaffOnlyArea] 検算OK。"); }
        else { Debug.LogError("[ValidateStaffOnlyArea] 検算NG: " + errors + " 件。"); }
    }

    // ============================================================
    // 実測ヘルパー
    // ============================================================
    static bool TryGetCounterBounds(out Bounds bounds)
    {
        bounds = new Bounds();
        var counter = GameObject.Find(CounterPath);
        if (counter == null)
        {
            Debug.LogError("[BuildStaffOnlyArea] " + CounterPath + " がシーンにありません。");
            return false;
        }
        var a = FindRenderer(counter.transform, TableAName);
        var b = FindRenderer(counter.transform, TableBName);
        if (a == null || b == null)
        {
            Debug.LogError("[BuildStaffOnlyArea] " + TableAName + " / " + TableBName + " のレンダラーが見つかりません。");
            return false;
        }
        bounds = a.bounds;
        bounds.Encapsulate(b.bounds);
        return true;
    }

    static Renderer FindRenderer(Transform parent, string name)
    {
        var t = parent.Find(name);
        return t != null ? t.GetComponent<Renderer>() : null;
    }

    static bool TryGetFloorY(Bounds counter, out float floorY)
    {
        floorY = 0f;
        RaycastHit hit;
        var from = new Vector3(counter.center.x, counter.max.y + 0.6f, counter.max.z + 0.40f);
        if (!Physics.Raycast(from, Vector3.down, out hit, 4f, ~0, QueryTriggerInteraction.Ignore)) { return false; }
        floorY = hit.point.y;
        return true;
    }

    static bool TryGetMenuPlane(out float z, out float minX, out float maxX)
    {
        z = 0f; minX = 0f; maxX = 0f;
        var menu = GameObject.Find(MenuName);
        if (menu == null) { return false; }
        var rect = menu.GetComponent<RectTransform>();
        if (rect == null) { return false; }
        var corners = new Vector3[4];
        rect.GetWorldCorners(corners);
        z = corners[0].z;
        minX = corners[0].x; maxX = corners[0].x;
        for (int i = 1; i < 4; i++)
        {
            minX = Mathf.Min(minX, corners[i].x);
            maxX = Mathf.Max(maxX, corners[i].x);
            z = Mathf.Min(z, corners[i].z);
        }
        return true;
    }

    // 東へプレイヤーを進め、最初に立てなくなる x を返す。
    static float ProbeBlockedX(float startX, float z, float floorY, float maxDistance)
    {
        for (float x = startX; x <= startX + maxDistance; x += 0.05f)
        {
            if (!CanStand(x, z, floorY)) { return x; }
        }
        return startX + maxDistance;
    }

    // 南（-Z）へプレイヤーを進め、最初に立てなくなる z を返す。
    static float ProbeBlockedZ(float x, float startZ, float floorY, float maxDistance)
    {
        for (float z = startZ; z >= startZ - maxDistance; z -= 0.05f)
        {
            if (!CanStand(x, z, floorY)) { return z; }
        }
        return startZ - maxDistance;
    }

    // 指定xの区間で一番低い天井を返す。
    static float ProbeCeiling(float x, float zFrom, float zTo, float floorY)
    {
        float lowest = float.MaxValue;
        float a = Mathf.Min(zFrom, zTo), b = Mathf.Max(zFrom, zTo);
        for (float z = a; z <= b; z += 0.10f)
        {
            RaycastHit hit;
            if (Physics.Raycast(new Vector3(x, floorY + 0.15f, z), Vector3.up, out hit, 8f, ~0, QueryTriggerInteraction.Ignore))
            {
                lowest = Mathf.Min(lowest, hit.point.y);
            }
        }
        return lowest == float.MaxValue ? floorY + FlankMaxHeight : lowest;
    }

    static bool CanStand(float x, float z, float floorY)
    {
        RaycastHit hit;
        var from = new Vector3(x, floorY + 0.60f, z);
        if (!Physics.Raycast(from, Vector3.down, out hit, 1.4f, ~0, QueryTriggerInteraction.Ignore)) { return false; }
        float y = hit.point.y;
        if (Mathf.Abs(y - floorY) > 0.35f) { return false; }
        return !Physics.CheckCapsule(
            new Vector3(x, y + ProbeFootClearance, z),
            new Vector3(x, y + ProbeHeadClearance, z),
            ProbeRadius, ~0, QueryTriggerInteraction.Ignore);
    }

    // カウンター手前の公共エリアから、裏の空間へ歩いて到達できるか（床の塗りつぶし探索）。
    static bool CanWalkIntoPocket(Bounds counter, float floorY)
    {
        const float cell = 0.125f;
        float minX = counter.min.x - 3.5f, maxX = counter.max.x + 1.2f;
        float minZ = counter.min.z - 2.2f, maxZ = counter.max.z + 2.5f;
        int nx = Mathf.CeilToInt((maxX - minX) / cell) + 1;
        int nz = Mathf.CeilToInt((maxZ - minZ) / cell) + 1;

        var open = new bool[nx, nz];
        for (int i = 0; i < nx; i++)
        {
            for (int j = 0; j < nz; j++)
            {
                open[i, j] = CanStand(minX + i * cell, minZ + j * cell, floorY);
            }
        }

        int sx = Mathf.RoundToInt((counter.center.x - minX) / cell);
        int sz = Mathf.RoundToInt((counter.max.z + 1.6f - minZ) / cell);
        if (sx < 0 || sx >= nx || sz < 0 || sz >= nz || !open[sx, sz])
        {
            Debug.LogWarning("[ValidateStaffOnlyArea] 到達判定の開始点（カウンター手前）に立てません。判定を省略します。");
            return false;
        }

        var seen = new bool[nx, nz];
        var queue = new System.Collections.Generic.Queue<int>();
        queue.Enqueue(sx * nz + sz);
        seen[sx, sz] = true;
        int[] dx = { 1, -1, 0, 0 };
        int[] dz = { 0, 0, 1, -1 };
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            int cx = cur / nz, cz = cur % nz;
            float wx = minX + cx * cell, wz = minZ + cz * cell;
            // カウンターの裏側（前面より奥）に一歩でも入れたら「入れた」。
            if (wz < counter.min.z && wx > counter.min.x && wx < counter.max.x + 1.0f) { return true; }
            for (int d = 0; d < 4; d++)
            {
                int nxi = cx + dx[d], nzi = cz + dz[d];
                if (nxi < 0 || nxi >= nx || nzi < 0 || nzi >= nz) { continue; }
                if (seen[nxi, nzi] || !open[nxi, nzi]) { continue; }
                seen[nxi, nzi] = true;
                queue.Enqueue(nxi * nz + nzi);
            }
        }
        return false;
    }

    static void SetBarriers(StaffOnlyArea comp, bool active)
    {
        if (comp.barriers == null) { return; }
        for (int i = 0; i < comp.barriers.Length; i++)
        {
            if (comp.barriers[i] != null) { comp.barriers[i].SetActive(active); }
        }
    }

    // ============================================================
    // 生成ヘルパー
    // ============================================================
    static GameObject EnsureChild(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null) { return t.gameObject; }
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Build Staff Only Area");
        go.transform.SetParent(parent, false);
        return go;
    }

    static void DestroyChild(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null) { Object.DestroyImmediate(t.gameObject); }
    }

    static GameObject EnsureBox(Transform parent, string name, Vector3 min, Vector3 max)
    {
        var go = EnsureChild(parent, name);
        go.layer = 0; // Default = プレイヤーカプセルが当たるレイヤー
        go.transform.rotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;
        go.transform.position = (min + max) * 0.5f;

        var box = go.GetComponent<BoxCollider>();
        if (box == null) { box = go.AddComponent<BoxCollider>(); }
        box.isTrigger = false;
        box.center = Vector3.zero;
        box.size = new Vector3(
            Mathf.Max(0.02f, max.x - min.x),
            Mathf.Max(0.02f, max.y - min.y),
            Mathf.Max(0.02f, max.z - min.z));
        go.SetActive(true);
        return go;
    }

    static bool EnsureProgramAsset(string scriptPath, string assetPath)
    {
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
        if (script == null)
        {
            Debug.LogError("[BuildStaffOnlyArea] スクリプトが見つかりません: " + scriptPath);
            return false;
        }
        var type = script.GetClass();
        if (type != null && UdonSharpProgramAsset.GetProgramAssetForClass(type) != null) { return false; }
        if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(assetPath) != null) { return false; }

        var programAsset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
        programAsset.sourceCsScript = script;
        AssetDatabase.CreateAsset(programAsset, assetPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[BuildStaffOnlyArea] U#プログラムアセットを生成: " + assetPath);
        return true;
    }

    static UdonSharpBehaviour EnsureUSharpComponent(GameObject go, System.Type type)
    {
        var proxy = (UdonSharpBehaviour)go.GetComponent(type);
        if (proxy != null)
        {
            if (UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy) != null) { return proxy; }

            Debug.LogWarning("[BuildStaffOnlyArea] " + type.Name + " の backing UdonBehaviour が無いため作り直します。");
            Object.DestroyImmediate(proxy);
            foreach (var ub in go.GetComponents<UdonBehaviour>())
            {
                if (ub.programSource == null) { Object.DestroyImmediate(ub); }
            }
        }

        var added = (UdonSharpBehaviour)UdonSharpUndo.AddComponent(go, type);
        if (added != null && UdonSharpEditorUtility.GetBackingUdonBehaviour(added) == null)
        {
            Debug.LogError("[BuildStaffOnlyArea] " + type.Name + " の追加に失敗（プログラムアセット未コンパイル？）。");
            Object.DestroyImmediate(added);
            return null;
        }
        return added;
    }

    static void MarkDirty(GameObject go)
    {
        if (go.scene.IsValid()) { EditorSceneManager.MarkSceneDirty(go.scene); }
        else { EditorUtility.SetDirty(go); }
    }
}
#endif
