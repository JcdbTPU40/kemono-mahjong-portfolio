#if UNITY_EDITOR
// ロリコライダー（自動調節ロリコライダー / called_D 氏・CC0）の全卓展開。
//   Tools > Kemono Mahjong > Build Loli Colliders    … 卓プレハブに LoliColliderArea（板＋判定エリアのみ）を
//                                                      冪等生成。旧・卓ごとの AutoAdjustLoliCollider_Full
//                                                      （U#＋Canvas 一式）は撤去する。
//                                                      MainMenu 横の本体も原点/ターゲット高さを正規化。
//   Tools > Kemono Mahjong > Wire Loli Colliders     … 本体の _additionalAreas に全卓のエリアを配線。
//   Tools > Kemono Mahjong > Validate Loli Colliders … 参照・寸法・高さの検算のみ（変更なし）。
//
// 【設計】高さ制御は「MainMenu 横の本体1つ」だけが持ち、卓側はエリア（板＋判定ボックス）しか置かない。
//   AutoAdjustLoliCollider は全 Floor を同じローカル高さへ動かすので、本体1つに全卓のエリアを
//   ぶら下げれば「MainMenu で決めた高さ＝各卓の高さ」が構造的に保証される（同期も分岐も不要）。
//   卓ごとに U# を置く案は UdonBehaviour×8＋Canvas×8 の無駄＋高さの持ち回りが必要になるので採らない。
//
// 何度実行しても安全（冪等）。寸法を変えたいときは下の定数を変えて Build を再実行する。
// 【重要】MainMenu 横の本体を置き直した・動かした時は必ず Build を実行し直すこと。
//   本体の _additionalAreas（全卓エリアの参照）はシーン側の値なので、プレハブを置き直すと空に戻り、
//   「メニューでは高さが変わるが卓では戻らない」という壊れ方をする。
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SuzuFactory.AozoraMahjong;
using LoliCollider = called_D.AutoAdjustLoliCollider.AutoAdjustLoliCollider;

public static class BuildLoliCollider
{
    // 卓プレハブ（8卓すべての実体）。卓側のエリアはインスタンスではなくここに作る＝8卓で構成が揃う。
    const string TablePrefabPath = "Assets/Mahjong/Prefabas/KemonoMahjong.prefab";
    // 卓に置くエリアの親。旧構成（U#込みの丸ごとプレハブ）は LegacyName で撤去する。
    const string AreaRootName = "LoliColliderArea";
    const string LegacyName = "AutoAdjustLoliCollider_Full";

    // 卓ルート原点から見た床の高さ。tablefoot と同じ -0.867m（卓0/1/2/6 は実測一致）。
    const float TableFloorLocalY = -0.867f;
    // 卓エリアの一辺(m)。4席ぶんの立ち位置を覆いつつ、卓カリングのトリガー(3.5m角)より内側に収める
    // ＝エリアに入る前に必ず卓が表示済みになり、板が突然生える事故を防ぐ。
    const float TableAreaSize = 3.0f;
    // MainMenu 横のエリアの一辺(m)。スライダーを触りながら少し動ける程度。
    const float MenuAreaSize = 2.0f;
    // 目標の目線高さ(m)。エリアの床からこの高さに目が来るよう板が上がる（手動・自動とも共通）。
    const float TargetViewHeight = 1.8f;
    // 判定ボックスの高さ(m)。床から上へこの範囲に頭or足があればエリア内。
    const float TriggerHeight = 2.0f;
    // 板の厚み(m)。素の配布物と同じ 1mm。
    const float FloorThickness = 0.001f;
    // 板を実地面に合わせる時の許容範囲(m)。これ以内なら卓インスタンス側で上下させ、
    // 超えるものは測定ミス（穴・遥か下の床を拾った等）を疑って動かさず警告のみ。
    // 板が実地面より上にあると「見えない天井/段差」になり、下にあると持ち上げ量が目減りするので、
    // 基本は必ず実地面に合わせる。
    const float GroundFitMax = 0.6f;
    // これを超えるズレを埋めた卓は、地形が卓の設計高さ(-0.867m)と食い違っている
    // ＝卓そのものが浮いている/沈んでいる。板は合わせた上で警告を出す。
    const float GroundFitNotice = 0.25f;
    const float GroundFitEpsilon = 0.005f;

    const string ColliderMatPath = "Assets/called_D/AutoAdjustLoliCollider/Materials/Collider.mat";
    const string AlphaControllerPath = "Assets/called_D/AutoAdjustLoliCollider/Animations/ColliderAlpha.controller";

    // ============================================================
    // Build：卓プレハブへエリア生成＋本体の正規化
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Build Loli Colliders")]
    public static void Build()
    {
        if (!BuildTablePrefabArea()) return;
        AdjustTableAreasToGround();
        NormalizeManager();
        // 配線まで一気にやる。本体を置き直した/動かした時に Wire を忘れると
        // 「メニューでは効くが卓では効かない」という分かりにくい壊れ方をするため。
        Wire();
        Debug.Log("[BuildLoliCollider] 完了：卓プレハブに " + AreaRootName + " を生成/更新し、本体の正規化と配線まで済ませました。");
    }

    /// <summary>卓プレハブ側。U#を持たない「板＋判定エリア」だけを置く。</summary>
    static bool BuildTablePrefabArea()
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(ColliderMatPath);
        var ctrl = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(AlphaControllerPath);
        if (mat == null) Debug.LogWarning("[BuildLoliCollider] " + ColliderMatPath + " が見つかりません。板のマテリアル無しで生成します。");
        if (ctrl == null) Debug.LogWarning("[BuildLoliCollider] " + AlphaControllerPath + " が見つかりません。板のフェード演出無しで生成します。");

        var root = PrefabUtility.LoadPrefabContents(TablePrefabPath);
        if (root == null)
        {
            Debug.LogError("[BuildLoliCollider] 卓プレハブが開けません：" + TablePrefabPath);
            return false;
        }
        try
        {
            // 旧構成（卓ごとに U#＋Canvas を持つ丸ごとプレハブ）を撤去。
            var legacy = root.transform.Find(LegacyName);
            if (legacy != null) Object.DestroyImmediate(legacy.gameObject);

            var areaRoot = EnsureChild(root.transform, AreaRootName);
            areaRoot.transform.localPosition = new Vector3(0f, TableFloorLocalY, 0f);
            areaRoot.transform.localRotation = Quaternion.identity;
            areaRoot.transform.localScale = Vector3.one;

            var areas = EnsureChild(areaRoot.transform, "Areas");
            areas.transform.localPosition = Vector3.zero;
            areas.transform.localRotation = Quaternion.identity;
            areas.transform.localScale = Vector3.one;

            BuildArea(areas.transform, "Area (0)", Vector3.zero, TableAreaSize, mat, ctrl);

            PrefabUtility.SaveAsPrefabAsset(root, TablePrefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
        AssetDatabase.SaveAssets();
        return true;
    }

    /// <summary>
    /// エリア1つ（Area/Floor/Cube＋Trigger×2）を生成/更新する。
    /// 構成名は AutoAdjustLoliCollider._InitAreaRefs() が名前で拾うので変えないこと
    /// （"Floor" と "Trigger" が固定キー）。
    /// </summary>
    static void BuildArea(Transform areasRoot, string areaName, Vector3 localPos, float size, Material mat, RuntimeAnimatorController ctrl)
    {
        var area = EnsureChild(areasRoot, areaName);
        area.transform.localPosition = localPos;
        area.transform.localRotation = Quaternion.identity;
        // Y は必ず 1。板の昇降量はエリアのローカル単位なので、Y を縮めると高さがズレる。
        area.transform.localScale = new Vector3(size, 1f, size);

        var floor = EnsureChild(area.transform, "Floor");
        floor.transform.localPosition = Vector3.zero;
        floor.transform.localRotation = Quaternion.identity;
        floor.transform.localScale = Vector3.one;

        var cube = EnsureChild(floor.transform, "Cube");
        cube.transform.localPosition = Vector3.zero;
        cube.transform.localRotation = Quaternion.identity;
        cube.transform.localScale = new Vector3(1f, FloorThickness, 1f);
        cube.layer = 0; // Default＝プレイヤーが乗れる層。ここを変えると板が効かない。

        var mf = EnsureComponent<MeshFilter>(cube);
        if (mf.sharedMesh == null) mf.sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
        var mr = EnsureComponent<MeshRenderer>(cube);
        if (mat != null) mr.sharedMaterial = mat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; // Quest：影は落とさない
        mr.receiveShadows = false;
        var bc = EnsureComponent<BoxCollider>(cube);
        bc.isTrigger = false;
        bc.size = Vector3.one;
        bc.center = Vector3.zero;
        if (ctrl != null)
        {
            var an = EnsureComponent<Animator>(cube);
            an.runtimeAnimatorController = ctrl;
            an.applyRootMotion = false;
            an.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }

        // 判定ボックスは2つ。Floor の子＝板と一緒に上がる判定、Area の子＝素の床の判定。
        // （素の配布物と同じ構成。どちらか一方だと昇降中に判定が抜ける。）
        BuildTrigger(floor.transform);
        BuildTrigger(area.transform);
    }

    static void BuildTrigger(Transform parent)
    {
        var trigger = EnsureChild(parent, "Trigger");
        trigger.transform.localPosition = new Vector3(0f, TriggerHeight * 0.5f, 0f);
        trigger.transform.localRotation = Quaternion.identity;
        trigger.transform.localScale = new Vector3(1f, TriggerHeight, 1f);
        var col = EnsureComponent<BoxCollider>(trigger);
        col.isTrigger = true;
        col.size = Vector3.one;
        col.center = Vector3.zero;
    }

    // ============================================================
    // 卓ごとの板の高さを実地面に合わせる（プレハブ既定値からのインスタンス差分）
    // ============================================================
    /// <summary>
    /// 卓のまわりの素の地面を測り、板が地面と揃うよう卓インスタンス側で上下させる。
    /// プレハブ既定値(-0.867m)で合っている卓は動かない。ズレが GroundFitMax を超える卓
    /// （＝地形そのものが卓の設計高さと違う。例：池の上の卓5）は地形側の問題なので動かさず警告のみ。
    /// 板を勝手に地面まで落とすと、卓面が手の届かない高さになって余計に悪化する。
    /// </summary>
    static void AdjustTableAreasToGround()
    {
        var tables = Object.FindObjectsOfType<Mahjong>(true);
        System.Array.Sort(tables, (a, b) => string.CompareOrdinal(GetPath(a.transform), GetPath(b.transform)));
        foreach (var table in tables)
        {
            var areaRoot = table.transform.Find(AreaRootName);
            if (areaRoot == null) continue;
            float ground;
            if (!MeasureGroundY(areaRoot.position, null, out ground)) continue;
            float d = ground - areaRoot.position.y;
            if (Mathf.Abs(d) <= GroundFitEpsilon) continue;
            if (Mathf.Abs(d) > GroundFitMax)
            {
                Debug.LogWarning("[BuildLoliCollider] " + GetPath(table.transform) + " は板と地面が " + d.ToString("F3") + "m もズレています。測定ミスの可能性が高いので板は動かしません。" + AreaRootName + " の Y を手で調整してください。");
                continue;
            }
            areaRoot.position = new Vector3(areaRoot.position.x, ground, areaRoot.position.z);
            EditorUtility.SetDirty(areaRoot);
            if (Mathf.Abs(d) > GroundFitNotice)
            {
                Debug.LogWarning("[BuildLoliCollider] " + GetPath(table.transform) + " は地形が卓の設計高さから " + d.ToString("F3") + "m 外れています（卓が浮いている/沈んでいる）。板は実地面に合わせましたが、卓の高さ自体を見直してください。");
            }
            else
            {
                Debug.Log("[BuildLoliCollider] " + GetPath(table.transform) + " の板を地面に合わせました（" + d.ToString("F3") + "m）。");
            }
        }
        if (tables.Length > 0) EditorSceneManager.MarkSceneDirty(tables[0].gameObject.scene);
    }

    // ============================================================
    // 本体（MainMenu 横）の正規化
    // ============================================================
    static void NormalizeManager()
    {
        var manager = FindManager();
        if (manager == null) return;

        var t = manager.transform;
        var areas = t.Find("Areas");
        var target = t.Find("TargetHeight");
        if (areas == null || target == null)
        {
            Debug.LogError("[BuildLoliCollider] 本体に Areas / TargetHeight がありません：" + GetPath(t));
            return;
        }

        // 本体の原点をエリアの床に合わせる。TargetHeight・Canvas はここからの相対で解釈されるので、
        // 原点が床から浮いていると「目標の目線高さ」も Canvas 追従もその分ズレる。
        var areasWorld = areas.position;
        // 板が実地面から浮いていると、メニュー前に見えない段差ができる。測って合わせる。
        var probe = areas.Find("Area (0)");
        float ground;
        if (MeasureGroundY(probe != null ? probe.position : areasWorld, t, out ground)
            && Mathf.Abs(ground - areasWorld.y) <= GroundFitMax)
        {
            areasWorld.y = ground;
        }
        var p = t.position;
        t.position = new Vector3(p.x, areasWorld.y, p.z);
        areas.position = areasWorld;
        areas.localRotation = Quaternion.identity;
        areas.localScale = Vector3.one;

        // 目標の目線高さ。手で調整された値（0.1m以上）は尊重して上書きしない。
        // ここを毎回 TargetViewHeight で潰すと、Build のたびに調整が巻き戻る。
        if (target.localPosition.y < 0.1f)
        {
            target.localPosition = new Vector3(0f, TargetViewHeight, 0f);
        }
        else
        {
            target.localPosition = new Vector3(0f, target.localPosition.y, 0f);
            if (Mathf.Abs(target.localPosition.y - TargetViewHeight) > 0.001f)
            {
                Debug.Log("[BuildLoliCollider] 目標の目線高さは手動設定値 " + target.localPosition.y.ToString("F3") + "m を維持します（既定 " + TargetViewHeight + "m）。");
            }
        }

        // 手動スライダーの左端＝「アバターの目線が TargetHeight と同じ高さ」になるよう
        // _maxViewPointHeight を TargetHeight に合わせる。
        //   板の高さ = TargetHeight - アバター目線高さ（0でクランプ）
        //   スライダーは アバター目線高さ を _maxViewPointHeight〜0.16m に割り当てる
        // ので、_maxViewPointHeight > TargetHeight だとスライダー左側が「全部0」の
        // 効かない範囲になる。両者を揃えると 0〜(TargetHeight-0.16m) が全域に綺麗に乗る。
        {
            float th = target.localPosition.y;
            var so = new SerializedObject(manager);
            var maxProp = so.FindProperty("_maxViewPointHeight");
            if (maxProp != null && Mathf.Abs(maxProp.floatValue - th) > 0.001f)
            {
                Debug.Log("[BuildLoliCollider] _maxViewPointHeight を " + maxProp.floatValue.ToString("F3")
                    + " → " + th.ToString("F3") + " に合わせました（スライダー全域を使えるようにする）。");
                maxProp.floatValue = th;
                so.ApplyModifiedPropertiesWithoutUndo();
                UdonSharpEditorUtility.CopyProxyToUdon(manager);
            }
        }

        // MainMenu 横のエリアも卓と同じ構成へ揃える（板の一辺だけ別値）。
        var area0 = areas.Find("Area (0)");
        var area0Pos = area0 != null ? area0.localPosition : Vector3.zero;
        BuildArea(areas, "Area (0)",
            area0Pos,
            MenuAreaSize,
            AssetDatabase.LoadAssetAtPath<Material>(ColliderMatPath),
            AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(AlphaControllerPath));

        EditorUtility.SetDirty(manager);
        EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);
    }

    // ============================================================
    // Wire：本体 → 全卓エリアの配線
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Wire Loli Colliders")]
    public static void Wire()
    {
        var manager = FindManager();
        if (manager == null) return;

        var tables = Object.FindObjectsOfType<Mahjong>(true);
        System.Array.Sort(tables, (a, b) => string.CompareOrdinal(GetPath(a.transform), GetPath(b.transform)));
        if (tables.Length == 0)
        {
            Debug.LogError("[WireLoliCollider] シーンに Mahjong（卓）が見つかりません。");
            return;
        }

        var list = new System.Collections.Generic.List<Transform>();
        foreach (var table in tables)
        {
            var areas = table.transform.Find(AreaRootName + "/Areas");
            if (areas == null)
            {
                Debug.LogError("[WireLoliCollider] " + GetPath(table.transform) + " に " + AreaRootName + "/Areas がありません。先に Build Loli Colliders を実行してください。");
                continue;
            }
            list.Add(areas);
        }

        var so = new SerializedObject(manager);
        var prop = so.FindProperty("_additionalAreas");
        if (prop == null)
        {
            Debug.LogError("[WireLoliCollider] AutoAdjustLoliCollider に _additionalAreas がありません。改造版スクリプトが入っているか確認してください。");
            return;
        }
        prop.arraySize = list.Count;
        for (int i = 0; i < list.Count; ++i) prop.GetArrayElementAtIndex(i).objectReferenceValue = list[i];
        so.ApplyModifiedPropertiesWithoutUndo();

        // U# はプロキシ（MonoBehaviour）側の値を UdonBehaviour へコピーしないと実行時に反映されない。
        UdonSharpEditorUtility.CopyProxyToUdon(manager);
        EditorUtility.SetDirty(manager);
        EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);

        Debug.Log("[WireLoliCollider] 完了：本体 " + GetPath(manager.transform) + " に卓エリア " + list.Count + "/" + tables.Length + " 件を配線しました。");
    }

    // ============================================================
    // Validate：検算のみ
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Validate Loli Colliders")]
    public static void Validate()
    {
        Debug.Log(ValidateReport());
    }

    /// <summary>検算結果を文字列で返す（Validate はこれを Console に出すだけ）。</summary>
    public static string ValidateReport()
    {
        var manager = FindManager();
        if (manager == null) return "[ValidateLoliCollider] 本体が見つかりません。";

        int errors = 0;
        var t = manager.transform;
        var areas = t.Find("Areas");
        var target = t.Find("TargetHeight");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[ValidateLoliCollider] 本体 = " + GetPath(t));

        if (areas == null || target == null) return "[ValidateLoliCollider] Areas / TargetHeight が見つかりません。";
        float baseY = areas.position.y;
        sb.AppendLine("  本体原点Y=" + t.position.y.ToString("F3") + " / エリア床Y=" + baseY.ToString("F3") + " / 目標目線=床+" + (target.position.y - baseY).ToString("F3") + "m");
        if (Mathf.Abs(t.position.y - baseY) > 0.001f) { sb.AppendLine("  × 本体原点がエリア床と一致していません（Build Loli Colliders を実行）。"); ++errors; }
        // 目標の目線高さは手動調整を許すので、既定と違っても異常ではない（情報として出す）。
        if (Mathf.Abs((target.position.y - baseY) - TargetViewHeight) > 0.001f) { sb.AppendLine("  ! 目標目線高さが既定(" + TargetViewHeight + "m)と違います＝手動調整値。意図通りか確認してください。"); }
        if (target.position.y - baseY < 0.1f) { sb.AppendLine("  × 目標目線高さが低すぎます（TargetHeight の Y を確認）。"); ++errors; }
        var sov = new SerializedObject(manager);
        var maxv = sov.FindProperty("_maxViewPointHeight");
        if (maxv != null)
        {
            sb.AppendLine("  _maxViewPointHeight=" + maxv.floatValue.ToString("F3") + " / 手動スライダーの可動範囲=0〜" + Mathf.Max(0f, target.localPosition.y - 0.16f).ToString("F3") + "m");
            if (Mathf.Abs(maxv.floatValue - target.localPosition.y) > 0.001f) { sb.AppendLine("  × _maxViewPointHeight が TargetHeight と違います＝スライダー左側が効きません（Build を実行）。"); ++errors; }
        }

        var so = new SerializedObject(manager);
        var prop = so.FindProperty("_additionalAreas");
        int wired = prop != null ? prop.arraySize : 0;
        var tables = Object.FindObjectsOfType<Mahjong>(true);
        sb.AppendLine("  _additionalAreas = " + wired + " 件 / 卓 " + tables.Length + " 卓");
        if (prop == null || wired != tables.Length) { sb.AppendLine("  × 卓数と一致しません（Wire Loli Colliders を実行）。"); ++errors; }

        for (int i = 0; i < wired; ++i)
        {
            var a = prop.GetArrayElementAtIndex(i).objectReferenceValue as Transform;
            if (a == null) { sb.AppendLine("  × _additionalAreas[" + i + "] が未割当。"); ++errors; continue; }
            var area0 = a.Find("Area (0)");
            var floor = area0 != null ? area0.Find("Floor") : null;
            if (area0 == null || floor == null) { sb.AppendLine("  × " + GetPath(a) + " の構成が不正（Area (0)/Floor が無い）。"); ++errors; continue; }
            if (Mathf.Abs(area0.localScale.y - 1f) > 0.0001f) { sb.AppendLine("  × " + GetPath(area0) + " の Scale.Y が 1 ではありません（昇降量がズレます）。"); ++errors; }
            if (Mathf.Abs(floor.localPosition.y) > 0.0001f) { sb.AppendLine("  ! " + GetPath(floor) + " の Y が 0 ではありません（実行時に上がったまま保存された可能性）。"); }

            // 板そのものの状態。シーンで手を入れると静かに機能停止するのでここで拾う。
            // （MeshRenderer の OFF は「板を見せない」意図なので警告しない。）
            var cube = floor.Find("Cube");
            if (cube == null) { sb.AppendLine("  × " + GetPath(floor) + " に Cube（板の実体）がありません。"); ++errors; }
            else
            {
                if (!cube.gameObject.activeSelf) { sb.AppendLine("  × " + GetPath(cube) + " が非アクティブです＝この卓では板が効きません。"); ++errors; }
                var col = cube.GetComponent<BoxCollider>();
                if (col == null || !col.enabled) { sb.AppendLine("  × " + GetPath(cube) + " の BoxCollider が無い/無効です＝この卓では板が効きません。"); ++errors; }
                if (Mathf.Abs(cube.localPosition.y) > 0.0001f) { sb.AppendLine("  × " + GetPath(cube) + " の Y が " + cube.localPosition.y.ToString("F3") + " です。板の高さは LoliColliderArea の Y で調整すること（Cube をずらすと持ち上げ量がその分ズレる）。"); ++errors; }
            }

            // 板の高さと実際の地面のズレを見る（傾斜地・池の上の卓はここで差が出る）。
            float ground;
            if (MeasureGroundY(area0.position, null, out ground))
            {
                float d = ground - area0.position.y;
                string mark = Mathf.Abs(d) > 0.15f ? "  ! " : "    ";
                sb.AppendLine(mark + GetPath(a.parent) + " 板Y=" + area0.position.y.ToString("F3") + " 実地面Y(中央値)=" + ground.ToString("F3") + " 差=" + d.ToString("F3") + "m");
            }
        }

        if (errors == 0) sb.AppendLine("  → 問題なし。");
        return sb.ToString();
    }

    // ============================================================
    // ヘルパ
    // ============================================================
    /// <summary>卓の外（＝MainMenu 横）に置かれた本体を1つだけ探す。</summary>
    static LoliCollider FindManager()
    {
        var all = Object.FindObjectsOfType<LoliCollider>(true);
        var found = new System.Collections.Generic.List<LoliCollider>();
        foreach (var c in all)
        {
            if (c.GetComponentInParent<Mahjong>() != null) continue; // 卓側に残っている旧構成は無視
            found.Add(c);
        }
        if (found.Count == 0)
        {
            Debug.LogError("[BuildLoliCollider] MainMenu 横に AutoAdjustLoliCollider（本体）がありません。AutoAdjustLoliCollider_Full.prefab を1つだけシーンに置いてください。");
            return null;
        }
        if (found.Count > 1)
        {
            var names = "";
            foreach (var f in found) names += "\n  - " + GetPath(f.transform);
            Debug.LogError("[BuildLoliCollider] 本体が複数あります（1つだけにしてください）：" + names);
            return null;
        }
        return found[0];
    }

    /// <summary>
    /// エリア中心のまわり（半径0.9m/1.3m×8方向）で素の地面を測り、中央値を返す。
    /// 卓の部品とロリコライダーの板自身は無視する（自分の板に当たると意味が無い）。
    /// </summary>
    static bool MeasureGroundY(Vector3 center, Transform excludeRoot, out float medianY)
    {
        var hits = new System.Collections.Generic.List<float>();
        float[] radii = { 0.9f, 1.3f };
        foreach (var r in radii)
        {
            for (int i = 0; i < 8; ++i)
            {
                float a = i * Mathf.PI / 4f;
                var from = center + new Vector3(Mathf.Cos(a) * r, 1.2f, Mathf.Sin(a) * r);
                var all = Physics.RaycastAll(from, Vector3.down, 30f, ~0, QueryTriggerInteraction.Ignore);
                System.Array.Sort(all, (x, y) => y.point.y.CompareTo(x.point.y)); // 上から順
                foreach (var h in all)
                {
                    if (h.collider.GetComponentInParent<Mahjong>() != null) continue;     // 卓の部品
                    if (IsUnder(h.collider.transform, AreaRootName)) continue;            // 卓側の板
                    if (excludeRoot != null && IsUnder(h.collider.transform, excludeRoot)) continue; // 本体側の板
                    hits.Add(h.point.y);
                    break;
                }
            }
        }
        if (hits.Count == 0) { medianY = 0f; return false; }
        hits.Sort();
        medianY = hits[hits.Count / 2];
        return true;
    }

    static bool IsUnder(Transform t, string ancestorName)
    {
        while (t != null) { if (t.name == ancestorName) return true; t = t.parent; }
        return false;
    }

    static bool IsUnder(Transform t, Transform ancestor)
    {
        while (t != null) { if (t == ancestor) return true; t = t.parent; }
        return false;
    }

    static GameObject EnsureChild(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null) return t.gameObject;
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go;
    }

    static T EnsureComponent<T>(GameObject go) where T : Component
    {
        var c = go.GetComponent<T>();
        if (c == null) c = go.AddComponent<T>();
        return c;
    }

    static string GetPath(Transform t)
    {
        var path = t.name;
        var p = t.parent;
        while (p != null) { path = p.name + "/" + path; p = p.parent; }
        return path;
    }
}
#endif
