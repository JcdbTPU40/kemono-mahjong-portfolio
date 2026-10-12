#if UNITY_EDITOR
// KemonoMahjong.prefab の Table 配下（FBXから切り離された素のGameObject群）の
// localPosition / localRotation / localScale を、Table.fbx のインポート結果の
// 同名ノードの値に同期する。メッシュ差し替えで凍結された旧Transformを新モデルに合わせる。
// 実行: Tools > Kemono Mahjong > Sync Table Layout From FBX
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class SyncTableLayoutFromFbx
{
    const string FbxPath = "Assets/Mahjong/FBXs/Table.fbx";
    const string DstPath = "Assets/Mahjong/Prefabas/KemonoMahjong.prefab";
    const string TableRootName = "Table"; // KemonoMahjong 直下の Table 親

    // 同期対象から除外するサブツリーのルート名。
    // ここに載せたノードは「自身も子孫も」Transformを上書きしない。
    // 理由: これらは Mahjong.cs がランタイムに配置・回転させる手調整オブジェクトで、
    //       FBXの生値で上書きすると中央方角ラベル等が壊れる（2026-07-07 事故）。
    //       Directions 配下の Text.041〜044 (東南西北=sanmaWindLabels) が該当。
    static readonly HashSet<string> SkipSubtrees = new HashSet<string>
    {
        "Directions",
    };

    [MenuItem("Tools/Kemono Mahjong/Sync Table Layout From FBX")]
    public static void Run()
    {
        var fbxRoot = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
        if (fbxRoot == null) { Debug.LogError("[Sync] FBXが読めません: " + FbxPath); return; }

        var dstRoot = PrefabUtility.LoadPrefabContents(DstPath);
        int changed = 0;
        var report = new StringBuilder();
        var missing = new List<string>();

        try
        {
            Transform dstTable = null;
            foreach (Transform c in dstRoot.transform)
                if (c.name == TableRootName) { dstTable = c; break; }
            if (dstTable == null) { Debug.LogError("[Sync] Table が見つかりません"); return; }

            // FBXルート直下 と prefabのTable直下 を対応付けて再帰同期
            // （Table親自体の配置はいじらない）
            SyncChildren(fbxRoot.transform, dstTable, report, missing, ref changed);

            if (changed == 0)
            {
                Debug.Log("[Sync] 差分なし。Transformは既に一致しています。");
                return;
            }

            bool apply = EditorUtility.DisplayDialog(
                "Sync Table Layout",
                $"{changed} 個のTransformに差分があります。FBXの値を適用しますか？\n（詳細はConsoleに出力済み）",
                "適用", "キャンセル(ログのみ)");

            Debug.Log("[Sync] 差分一覧:\n" + report);
            if (missing.Count > 0)
                Debug.LogWarning("[Sync] FBXに同名ノードなし(スキップ): " + string.Join(", ", missing));

            if (apply)
            {
                PrefabUtility.SaveAsPrefabAsset(dstRoot, DstPath);
                Debug.Log($"[Sync] 適用完了: {changed} 件");
            }
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(dstRoot);
        }
    }

    static void SyncChildren(Transform fbxParent, Transform dstParent,
                             StringBuilder report, List<string> missing, ref int changed)
    {
        foreach (Transform dstChild in dstParent)
        {
            // 除外サブツリー：自身も子孫も一切さわらない（ランタイム配置の保護）
            if (SkipSubtrees.Contains(dstChild.name))
            {
                report.AppendLine($"[SKIP] {dstParent.name}/{dstChild.name} (除外サブツリー・子孫ごとスキップ)");
                continue;
            }

            // 同じ親スコープ内で名前一致を探す（Text.001等の重複対策）
            Transform fbxChild = null;
            foreach (Transform f in fbxParent)
                if (f.name == dstChild.name) { fbxChild = f; break; }

            if (fbxChild == null)
            {
                missing.Add(dstParent.name + "/" + dstChild.name); // tablefoot等はここに出る（正常）
                continue;
            }

            bool diff =
                (dstChild.localPosition - fbxChild.localPosition).sqrMagnitude > 1e-10f ||
                Quaternion.Angle(dstChild.localRotation, fbxChild.localRotation) > 0.01f ||
                (dstChild.localScale - fbxChild.localScale).sqrMagnitude > 1e-10f;

            if (diff)
            {
                report.AppendLine(
                    $"{dstChild.name}: pos {dstChild.localPosition}->{fbxChild.localPosition} " +
                    $"rot {dstChild.localEulerAngles}->{fbxChild.localEulerAngles} " +
                    $"scale {dstChild.localScale}->{fbxChild.localScale}");
                dstChild.localPosition = fbxChild.localPosition;
                dstChild.localRotation = fbxChild.localRotation;
                dstChild.localScale = fbxChild.localScale;
                changed++;
            }

            SyncChildren(fbxChild, dstChild, report, missing, ref changed);
        }
    }
}
#endif
