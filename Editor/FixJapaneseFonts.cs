#if UNITY_EDITOR
// ============================================================
//  日本語が細く（別フォントで）出るのを直す
//    Tools > Kemono Mahjong > Fix Japanese Fonts       … 張り替え
//    Tools > Kemono Mahjong > Validate Japanese Fonts  … 検査だけ（変更なし）
//
//  【何が起きていたか】
//   MainMenu 以外の TMP（ランキング3枚・役満ポスター・役種一覧板・入場注意書き・
//   スタッフパネル・段位管理モニタ）に LiberationSans SDF が残っていた。
//   2026-09-09 に TMP_Settings のグローバルフォールバックへ NotoSansJP-Bold SDF を
//   足したので豆腐（□）は出なくなったが、**日本語はフォールバック経由で描かれる**。
//   フォールバックのマテリアルは呼び出し元（LiberationSans）のプリセットから作られ、
//   SDF のパディング・太さが別物なので、直接 NotoSansJP-Bold を指定した文字より細い。
//   ＝「前回と変わってちょっと細い」の正体。フォントは実物を割り当てるのが正。
//
//  【なぜビルダーの再実行では直らないか】
//   各ビルダーは既存の GameObject を使い回すが、パネルごと作り直す経路を通らない限り
//   font を入れ直さない（作られたのが日本語フォントアセットを用意する前だった）。
//   ここで一括して張り替える。以後 Build を回しても font は上書きされるだけなので安全。
//
//  【Prefab も直す】
//   卓の Prefab 側に LiberationSans が残っていると、卓を作り直したときに戻る。
// ============================================================
using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEngine;

public static class FixJapaneseFonts
{
    const string JpFontPath = "Assets/Mahjong/Fonts/NotoSansJP-Bold SDF.asset";
    const string TablePrefabPath = "Assets/Mahjong/Prefabas/KemonoMahjong.prefab";

    [MenuItem("Tools/Kemono Mahjong/Fix Japanese Fonts")]
    public static void Fix()
    {
        Run(false);
    }

    [MenuItem("Tools/Kemono Mahjong/Validate Japanese Fonts")]
    public static void Validate()
    {
        Run(true);
    }

    static void Run(bool dryRun)
    {
        var jp = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(JpFontPath);
        if (jp == null)
        {
            Debug.LogError("[FixJapaneseFonts] 日本語フォントが見つかりません: " + JpFontPath);
            return;
        }

        // font_ を張り替えるとき、マテリアルも必ず一緒に揃える。
        // material だけ古いフォントのものが残ると、アトラスとテクスチャがズレて
        // 文字化け・にじみになる（TMP は font と material を対で見る）。
        Material jpMaterial = jp.material;

        int scene = 0;
        var scenePaths = new List<string>();
        foreach (var tmp in Object.FindObjectsOfType<TMP_Text>(true))
        {
            if (!NeedsFix(tmp, jp)) { continue; }
            ++scene;
            if (scenePaths.Count < 12) { scenePaths.Add(PathOf(tmp.transform)); }
            if (dryRun) { continue; }
            Undo.RecordObject(tmp, "Fix Japanese Fonts");
            tmp.font = jp;
            tmp.fontSharedMaterial = jpMaterial;
            EditorUtility.SetDirty(tmp);
        }

        int prefab = FixPrefab(jp, jpMaterial, dryRun);

        if (!dryRun && scene > 0)
        {
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        }

        string head = dryRun ? "[ValidateJapaneseFonts] " : "[FixJapaneseFonts] ";
        if (scene == 0 && prefab == 0)
        {
            Debug.Log(head + "日本語フォント以外の TMP はありません（シーン・Prefab とも 0 件）。");
            return;
        }

        string sample = scenePaths.Count > 0 ? "\n  例: " + string.Join("\n  例: ", scenePaths.ToArray()) : "";
        if (dryRun)
        {
            Debug.LogWarning(head + "張り替えが必要な TMP: シーン " + scene + " 件 / Prefab " + prefab + " 件。"
                             + "Tools > Kemono Mahjong > Fix Japanese Fonts で直せます。" + sample);
        }
        else
        {
            Debug.Log(head + "NotoSansJP-Bold SDF へ張り替え: シーン " + scene + " 件 / Prefab " + prefab + " 件。"
                      + "シーンは未保存なので Ctrl+S で保存してください。" + sample);
        }
    }

    static int FixPrefab(TMP_FontAsset jp, Material jpMaterial, bool dryRun)
    {
        var root = AssetDatabase.LoadAssetAtPath<GameObject>(TablePrefabPath);
        if (root == null) { return 0; }

        int n = 0;
        foreach (var tmp in root.GetComponentsInChildren<TMP_Text>(true))
        {
            if (!NeedsFix(tmp, jp)) { continue; }
            ++n;
            if (dryRun) { continue; }
            tmp.font = jp;
            tmp.fontSharedMaterial = jpMaterial;
            EditorUtility.SetDirty(tmp);
        }
        if (!dryRun && n > 0)
        {
            PrefabUtility.SavePrefabAsset(root);
        }
        return n;
    }

    // 直す対象か。font が空のもの（＝TMP の既定＝LiberationSans で描かれる）も含める。
    static bool NeedsFix(TMP_Text tmp, TMP_FontAsset jp)
    {
        if (tmp == null) { return false; }
        if (tmp.font == jp) { return false; }
        // 筆文字など、日本語を持つ別フォントを意図して使っている所は触らない。
        if (tmp.font != null && tmp.font.HasCharacter('麻')) { return false; }
        return true;
    }

    static string PathOf(Transform t)
    {
        string s = t.name;
        for (var p = t.parent; p != null; p = p.parent) { s = p.name + "/" + s; }
        return s;
    }
}
#endif
