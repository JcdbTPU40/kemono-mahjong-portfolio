#if UNITY_EDITOR
// あがり画面（MahjongAgariScreen）の役行の器を 16 行にそろえる。
//
// 行の位置と大きさは実行時に MahjongAgariScreen.ApplyYakuLayout が役の数から決める（雀魂式）。
// その「型」は Prefab に置いた最初の12行（左6・右6）の位置なので、ここでは最初の12行には触らず、
// 足りない分だけ最後の行を複製して配列（yakuRowRoots / yakuNameTexts / yakuValueTexts）へ足す。
// 複製した行の Prefab 上の位置は使われない（実行時に毎回決め直す）。
//
// 2回実行しても安全（16行あれば何もしない）。
// 実行: Tools > Kemono Mahjong > Build Agari Yaku Rows
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using SuzuFactory.AozoraMahjong;

public static class BuildAgariYakuRows
{
    const string PrefabPath = "Assets/Mahjong/Prefabas/KemonoMahjong.prefab";
    const int TargetRows = 16;
    const int TemplateRows = 12;   // 型（左6・右6）。ここは触らない

    [MenuItem("Tools/Kemono Mahjong/Build Agari Yaku Rows")]
    public static void Run()
    {
        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null) { Debug.LogError("[BuildAgariYakuRows] Prefab を読み込めません: " + PrefabPath); return; }

        int added = 0;
        try
        {
            var screen = root.GetComponentInChildren<MahjongAgariScreen>(true);
            if (screen == null) { Debug.LogError("[BuildAgariYakuRows] MahjongAgariScreen が見つかりません。"); return; }

            var rows = screen.yakuRowRoots;
            var names = screen.yakuNameTexts;
            var values = screen.yakuValueTexts;
            if (rows == null || names == null || values == null
                || rows.Length < TemplateRows || names.Length != rows.Length || values.Length != rows.Length)
            {
                Debug.LogError("[BuildAgariYakuRows] 役行の配列が想定外です（12行以上・3つの配列が同じ長さであること）。");
                return;
            }

            if (rows.Length >= TargetRows)
            {
                Debug.Log("[BuildAgariYakuRows] 既に " + rows.Length + " 行あります。何もしません。");
                Validate();
                return;
            }

            var src = rows[rows.Length - 1];
            string namePath = RelativePath(src.transform, names[rows.Length - 1].transform);
            string valuePath = RelativePath(src.transform, values[rows.Length - 1].transform);
            if (namePath == null || valuePath == null)
            {
                Debug.LogError("[BuildAgariYakuRows] 最後の行の役名/飜数 Text が行の子ではありません。");
                return;
            }

            var newRows = new GameObject[TargetRows];
            var newNames = new Text[TargetRows];
            var newValues = new Text[TargetRows];
            for (int i = 0; i < rows.Length; ++i)
            {
                newRows[i] = rows[i];
                newNames[i] = names[i];
                newValues[i] = values[i];
            }

            for (int i = rows.Length; i < TargetRows; ++i)
            {
                var copy = Object.Instantiate(src, src.transform.parent);
                copy.name = "YakuRow" + i;
                copy.SetActive(false);
                newRows[i] = copy;
                newNames[i] = copy.transform.Find(namePath).GetComponent<Text>();
                newValues[i] = copy.transform.Find(valuePath).GetComponent<Text>();
                ++added;
            }

            screen.yakuRowRoots = newRows;
            screen.yakuNameTexts = newNames;
            screen.yakuValueTexts = newValues;
            UdonSharpEditorUtility.CopyProxyToUdon(screen);

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log("[BuildAgariYakuRows] 完了: 役行を " + added + " 行足して " + TargetRows + " 行にしました（Prefab を保存・シーンの全卓に反映）。");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Validate();
    }

    [MenuItem("Tools/Kemono Mahjong/Validate Agari Yaku Rows")]
    public static void Validate()
    {
        int tables = 0, ok = 0;
        var sb = new System.Text.StringBuilder();
        foreach (var m in Object.FindObjectsOfType<Mahjong>(true))
        {
            ++tables;
            var s = m.GetComponentInChildren<MahjongAgariScreen>(true);
            string why = null;
            if (s == null) { why = "MahjongAgariScreen が無い"; }
            else if (s.yakuRowRoots == null || s.yakuRowRoots.Length != TargetRows) { why = "行が " + (s.yakuRowRoots == null ? 0 : s.yakuRowRoots.Length) + " 行（" + TargetRows + " 行であるべき）"; }
            else if (s.yakuNameTexts == null || s.yakuNameTexts.Length != TargetRows || s.yakuValueTexts == null || s.yakuValueTexts.Length != TargetRows) { why = "役名/飜数の配列の長さが行数と合わない"; }
            else
            {
                for (int i = 0; i < TargetRows && why == null; ++i)
                {
                    if (s.yakuRowRoots[i] == null || s.yakuNameTexts[i] == null || s.yakuValueTexts[i] == null) { why = "行 " + i + " の配線切れ"; }
                }
            }
            if (why == null) { ++ok; } else { sb.Append(m.name).Append(": ").Append(why).Append("\n"); }
        }
        if (ok == tables) { Debug.Log("[ValidateAgariYakuRows] OK: 卓 " + tables + " 個すべて役行 " + TargetRows + " 行・配線済み。"); }
        else { Debug.LogError("[ValidateAgariYakuRows] NG " + (tables - ok) + " / " + tables + "\n" + sb); }
    }

    static string RelativePath(Transform root, Transform t)
    {
        if (t == root) { return ""; }
        string p = t.name;
        for (var x = t.parent; x != null; x = x.parent)
        {
            if (x == root) { return p; }
            p = x.name + "/" + p;
        }
        return null;
    }
}
#endif
