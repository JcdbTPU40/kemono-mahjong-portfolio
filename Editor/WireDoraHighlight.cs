#if UNITY_EDITOR
// ドラ強調（金色ティント＋明滅）の設定ツール。
//   Tools > Kemono Mahjong > Wire Dora Highlight
// やること：
//   シーンの全卓（Mahjong）の doraEffectMode を All にし、
//   未設定（0／黒）の調整値を推奨値で埋める。
//
// 注意：
//   ・この効果は完全ローカル（同期しない）。牌の同期・保存則には一切触れない。
//   ・染まるのは「このクライアントで絵柄が見えている牌」の絵柄面(submesh)だけ。
//     伏せ牌・暗槓の伏せ2枚・他家の非公開手牌には付かない。
//   ・王牌のドラ表示牌そのものは染めない（表示牌＝ドラという誤解を避けるため）。
//   ・加算発光は牌面が白いため効かないことを実測済み（Mahjong.cs のコメント参照）。
//     見た目の調整は doraTintMin / doraTintRange / doraTintColor で行う。
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SuzuFactory.AozoraMahjong;

public static class WireDoraHighlight
{
    [MenuItem("Tools/Kemono Mahjong/Wire Dora Highlight")]
    public static void Run()
    {
        var tables = Object.FindObjectsOfType<Mahjong>(true);
        if (tables.Length == 0)
        {
            Debug.LogError("[WireDoraHighlight] シーンに Mahjong（卓）が見つかりません。");
            return;
        }

        System.Array.Sort(tables, (a, b) => string.CompareOrdinal(GetPath(a.transform), GetPath(b.transform)));

        int changed = 0;
        foreach (var t in tables)
        {
            bool dirty = false;

            if (t.doraEffectMode != DoraEffectMode.All) { t.doraEffectMode = DoraEffectMode.All; dirty = true; }

            // 新規フィールドはシーン側に値が無いと 0／黒 で入ってくることがあるため、
            // 「明らかに未設定」な値だけ推奨値で埋める（調整済みの値は尊重する）。
            // 帯の色は「黒（未設定）」だけでなく「白」も無効扱いにする。牌の絵柄面はほぼ白なので、
            // 白い帯は素の牌と同じ＝画面上まったく見えないため、設定として成立しない。
            // （旧実装では doraShineColor が「白＝素に戻す」意味だったので、その値が残り得る。）
            if (IsUnsetColor(t.doraShineColor) || IsWhite(t.doraShineColor))
            { t.doraShineColor = new Color(1.0f, 0.80f, 0.35f, 1.0f); dirty = true; }
            if (IsUnsetColor(t.doraAkaShineColor) || IsWhite(t.doraAkaShineColor))
            { t.doraAkaShineColor = new Color(1.0f, 0.55f, 0.32f, 1.0f); dirty = true; }
            if (t.doraShineBoost < 1.0f) { t.doraShineBoost = 1.0f; dirty = true; }
            if (t.doraShineWidth <= 0.0f) { t.doraShineWidth = 0.34f; dirty = true; }
            if (t.doraShineSpeed <= 0.0f) { t.doraShineSpeed = 0.45f; dirty = true; }
            if (t.doraRankDeepen <= 0.0f) { t.doraRankDeepen = 0.15f; dirty = true; }

            // ツモ切り牌の暗さ（河の色付けはドラ強調と同じ経路で書き込むのでここで面倒を見る）。
            if (IsUnsetColor(t.tsumogiriTintColor)) { t.tsumogiriTintColor = new Color(0.55f, 0.55f, 0.55f, 1.0f); dirty = true; }

            // 点数変動アニメ。点数板と同じ表示経路なのでここでまとめて面倒を見る。
            if (t.pointAnimDuration <= 0.0f) { t.pointAnimDuration = 0.9f; dirty = true; }
            if (t.pointAnimInterval < 0.0f) { t.pointAnimInterval = 0.05f; dirty = true; }
            if (t.pointAnimFlashDuration <= 0.0f) { t.pointAnimFlashDuration = 1.6f; dirty = true; }
            if (IsUnsetColor(t.pointAnimGainColor)) { t.pointAnimGainColor = new Color(0.40f, 1.00f, 0.55f, 1.0f); dirty = true; }
            if (IsUnsetColor(t.pointAnimLossColor)) { t.pointAnimLossColor = new Color(1.00f, 0.45f, 0.45f, 1.0f); dirty = true; }

            // CopyProxyToUdon は値が変わっていなくても必ず呼ぶ。
            // フィールドを増減した直後は UdonBehaviour 側の変数テーブルが旧スキーマのままで、
            // 「値が既定値と一致していて dirty にならない → 同期されない」と
            // 新フィールドが Udon 側に存在しないまま実行される（2026-08-12 に踏んだ）。
            UdonSharpEditorUtility.CopyProxyToUdon(t);
            EditorUtility.SetDirty(t);
            EditorSceneManager.MarkSceneDirty(t.gameObject.scene);
            if (dirty) { ++changed; }
        }

        Debug.Log("[WireDoraHighlight] 完了：卓 " + tables.Length + " 卓へ同期（うち値を補正 " + changed + " 卓）。");
    }

    // 真っ黒（=シリアライズされていない）かどうか。アルファは見ない。
    static bool IsUnsetColor(Color c)
    {
        return c.r <= 0.0f && c.g <= 0.0f && c.b <= 0.0f;
    }

    // ほぼ真っ白かどうか（帯の色としては無効）。
    static bool IsWhite(Color c)
    {
        return c.r >= 0.99f && c.g >= 0.99f && c.b >= 0.99f;
    }

    static string GetPath(Transform t)
    {
        var sb = new System.Text.StringBuilder(t.name);
        while (t.parent != null) { t = t.parent; sb.Insert(0, t.name + "/"); }
        return sb.ToString();
    }
}
#endif
