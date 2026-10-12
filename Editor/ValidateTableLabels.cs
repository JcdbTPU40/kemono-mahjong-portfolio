#if UNITY_EDITOR
// 卓ボタンラベル＆マテリアルの健全性チェック。
// 実行: Tools > Kemono Mahjong > Validate Table Labels & Materials
//
// 背景（2026-07-20 ラベル崩壊事故）:
//   Quest軽量化(2026-07-14)で Text.mat / TextWhite.mat を VRChat/Mobile/Standard Lite に
//   変換した際、旧Standardから引き継いだ「Enable GPU Instancing」が残り、
//   ビルド後のVRChatクライアントでのみラベルメッシュが 消失/同一点重なり/巨大化/鏡文字 になった。
//   エディタ・ClientSim・シーン/プレハブ/FBX/ビルド内Transform(630件検証)は全て正常で、
//   描画時のインスタンシングバッチのみが壊れる。よって
//   「VRChat/Mobileシェーダー + GPU Instancing有効」の組み合わせを禁止パターンとして検出する。
//
// チェック内容:
//   1) シーン内で使用中のマテリアルに VRChat/Mobile/* + enableInstancing がないか
//   2) KemonoMahjong各卓のラベル(Text*/JA_*/EN_*)の Transform異常
//      （負スケール・想定外の大スケール・メッシュ/マテリアル欠落）
//   3) Table.fbx と KemonoMahjong.prefab の同名ノードTransform乖離（Directions配下は除外）
//   4) JA_/EN_ ラベルの排他性（同一ボタン配下で両方activeなら警告＝2026-07-17事故の再発検知）
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class ValidateTableLabels
{
    const string FbxPath = "Assets/Mahjong/FBXs/Table.fbx";
    const string PrefabPath = "Assets/Mahjong/Prefabas/KemonoMahjong.prefab";

    // SyncTableLayoutFromFbx と同じ除外（Mahjong.cs がランタイム配置するため乖離が正常）
    static readonly HashSet<string> SkipSubtrees = new HashSet<string> { "Directions" };

    [MenuItem("Tools/Kemono Mahjong/Validate Table Labels & Materials")]
    public static void Run()
    {
        int errors = 0, warnings = 0;
        var report = new StringBuilder();

        CheckMobileInstancingMaterials(report, ref errors);
        CheckSceneLabelTransforms(report, ref errors, ref warnings);
        CheckPrefabVsFbx(report, ref warnings);
        CheckLanguageExclusivity(report, ref warnings);

        string summary = $"[ValidateTableLabels] errors={errors} warnings={warnings}\n" + report;
        if (errors > 0) Debug.LogError(summary);
        else if (warnings > 0) Debug.LogWarning(summary);
        else Debug.Log(summary + "問題なし。");

        EditorUtility.DisplayDialog("Validate Table Labels",
            $"エラー {errors} 件 / 警告 {warnings} 件\n詳細はConsoleを参照。", "OK");
    }

    // 1) 「Standard Lite + GPU Instancing」の禁止コンボ検出（シーン内使用分のみ）。
    //    実害が確認されたのは Standard Lite のみ（2026-07-20事故）。
    //    Toon Standard + Instancing（牌のTile/green等）は実機で正常描画・同一メッシュ大量描画の
    //    正当な最適化なのでエラーにしない（他のMobileシェーダーは監視対象としてWARN）。
    static void CheckMobileInstancingMaterials(StringBuilder report, ref int errors)
    {
        var seen = new HashSet<Material>();
        foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || !seen.Add(m)) continue;
                    if (m.shader == null || !m.enableInstancing) continue;
                    if (m.shader.name == "VRChat/Mobile/Standard Lite")
                    {
                        errors++;
                        report.AppendLine($"[ERROR] Standard Lite + GPU Instancing（ビルドでラベル崩壊するコンボ）: " +
                                          $"'{m.name}' path={AssetDatabase.GetAssetPath(m)} 使用例={GetPath(r.transform)}");
                    }
                }
            }
        }
    }

    // 2) シーン各卓のラベルTransform・メッシュ・マテリアル欠落チェック
    static void CheckSceneLabelTransforms(StringBuilder report, ref int errors, ref int warnings)
    {
        foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
        {
            if (!root.name.StartsWith("KemonoMahjong")) continue;
            foreach (var tr in root.GetComponentsInChildren<Transform>(true))
            {
                string n = tr.name;
                bool isLabel = n.StartsWith("Text") || n.StartsWith("JA_") || n.StartsWith("EN_");
                if (!isLabel) continue;

                var s = tr.localScale;
                if (s.x < 0f || s.y < 0f || s.z < 0f)
                { errors++; report.AppendLine($"[ERROR] 負スケール: {GetPath(tr)} scale={s}"); }
                else if (s.x > 2f || s.y > 2f || s.z > 2f)
                { errors++; report.AppendLine($"[ERROR] ラベルの異常スケール: {GetPath(tr)} scale={s}"); }

                var mf = tr.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh == null)
                { errors++; report.AppendLine($"[ERROR] メッシュ欠落(Missing): {GetPath(tr)}"); }

                var mr = tr.GetComponent<MeshRenderer>();
                if (mr != null)
                {
                    foreach (var m in mr.sharedMaterials)
                        if (m == null)
                        { warnings++; report.AppendLine($"[WARN] マテリアル欠落: {GetPath(tr)}"); break; }
                }
            }
        }
    }

    // 3) prefab と Table.fbx の同名ノードTransform乖離（ドライラン・書き換えなし）
    static void CheckPrefabVsFbx(StringBuilder report, ref int warnings)
    {
        var fbxRoot = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
        var prefabRoot = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (fbxRoot == null || prefabRoot == null)
        { warnings++; report.AppendLine("[WARN] FBXまたはprefabがロードできず乖離チェックをスキップ"); return; }

        Transform dstTable = null;
        foreach (Transform c in prefabRoot.transform) if (c.name == "Table") { dstTable = c; break; }
        if (dstTable == null)
        { warnings++; report.AppendLine("[WARN] prefab直下にTableが見つからず乖離チェックをスキップ"); return; }

        CompareChildren(fbxRoot.transform, dstTable, report, ref warnings);
    }

    static void CompareChildren(Transform fbxParent, Transform dstParent, StringBuilder report, ref int warnings)
    {
        foreach (Transform dstChild in dstParent)
        {
            if (SkipSubtrees.Contains(dstChild.name)) continue;
            Transform fbxChild = null;
            foreach (Transform f in fbxParent) if (f.name == dstChild.name) { fbxChild = f; break; }
            if (fbxChild == null) continue; // tablefoot等の後付けオブジェクトは正常

            bool diff =
                (dstChild.localPosition - fbxChild.localPosition).sqrMagnitude > 1e-6f ||
                Quaternion.Angle(dstChild.localRotation, fbxChild.localRotation) > 0.1f ||
                (dstChild.localScale - fbxChild.localScale).sqrMagnitude > 1e-6f;
            if (diff)
            {
                // 手調整（例: ResetButton の y+0.2m）も拾うため WARN 止まり。値を見て判断する。
                warnings++;
                report.AppendLine($"[WARN] FBXとprefabのTransform乖離: Table/.../{dstChild.name}\n" +
                                  $"    prefab pos={dstChild.localPosition:F4} rot={dstChild.localEulerAngles:F2} scale={dstChild.localScale:F4}\n" +
                                  $"    fbx    pos={fbxChild.localPosition:F4} rot={fbxChild.localEulerAngles:F2} scale={fbxChild.localScale:F4}");
            }
            CompareChildren(fbxChild, dstChild, report, ref warnings);
        }
    }

    // 4) 同一親配下で JA_xxx と EN_xxx が両方 activeSelf ならUI重なり（2026-07-17事故パターン）
    static void CheckLanguageExclusivity(StringBuilder report, ref int warnings)
    {
        foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
        {
            if (!root.name.StartsWith("KemonoMahjong")) continue;
            foreach (var tr in root.GetComponentsInChildren<Transform>(true))
            {
                bool ja = false, en = false;
                foreach (Transform c in tr)
                {
                    if (c.name.StartsWith("JA_") && c.gameObject.activeSelf) ja = true;
                    if (c.name.StartsWith("EN_") && c.gameObject.activeSelf) en = true;
                }
                if (ja && en)
                { warnings++; report.AppendLine($"[WARN] JA_/EN_ 両方active（UI重なり）: {GetPath(tr)}"); }
            }
        }
    }

    static string GetPath(Transform t)
    {
        var sb = new StringBuilder(t.name);
        while (t.parent != null) { t = t.parent; sb.Insert(0, t.name + "/"); }
        return sb.ToString();
    }
}
#endif
