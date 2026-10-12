#if UNITY_EDITOR
// ビルドプラットフォーム（Windows / Android）に応じて AetherLoft 周りのマテリアルを差し替える。
//
//   ビルド時   … IProcessSceneWithReport でビルド用シーンのコピーに適用する。
//                 シーンファイル自体は書き換わらない＝git差分も出ないし、Editorの表示も変わらない。
//                 適用の有無はビルドログの [PlatformMaterialSwitcher] 行で確認できる。
//   手動       … Tools > Kemono Mahjong > Platform Materials > Apply PC / Apply Quest
//                 Editor上の見た目をどちらかに寄せたいときだけ使う（こちらはシーンが汚れる）。
//   確認       … Tools > Kemono Mahjong > Platform Materials > Validate
//
// 対応表を変えたいときは下の Rules を直す。オブジェクトは「シーンルートからのパス」で探すので、
// 同名オブジェクトが他所にあっても誤爆しない。
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public class PlatformMaterialSwitcher : IProcessSceneWithReport
{
    const string Cloud = "Assets/UdonCatWorks/AetherLoft/Texture/Cloud/";
    const string Frag  = "Assets/UdonCatWorks/AetherLoft/Texture/Frag/";
    const string River = "Assets/UdonCatWorks/AetherLoft/Texture/River/";

    struct Rule
    {
        public readonly string Path;   // シーンルートからのパス
        public readonly int Slot;      // Renderer のマテリアルスロット
        public readonly string Pc;     // Windows ビルドで使うマテリアル
        public readonly string Quest;  // Android ビルドで使うマテリアル
        public Rule(string path, int slot, string pc, string quest)
        {
            Path = path; Slot = slot; Pc = pc; Quest = quest;
        }
    }

    // ※ WaterDay の Quest 側が River_night_for_quest.mat なのは指定通り（daylight の Quest 版は存在しない）。
    static readonly Rule[] Rules = new Rule[]
    {
        new Rule("CloudSea",                        0, Cloud + "CloudSea_daylight.mat", Cloud + "CloudSea_daylight_for_Quest.mat"),
        new Rule("CloudSeaNight",                   0, Cloud + "CloudSea_night.mat",    Cloud + "CloudSea_night_for_Quest.mat"),
        new Rule("AetherLoft/Wood_house/Plane.002", 0, Frag  + "Frag_mat.mat",          Frag  + "Frag_mat _for_quest.mat"),
        new Rule("AetherLoft/Ground/WaterDay",      0, River + "River_daylight.mat",    River + "River_night_for_quest.mat"),
        new Rule("AetherLoft/Ground/WaterNight",    0, River + "River_night.mat",       River + "River_night_for_quest.mat"),
    };

    // VRChat SDK のビルド後処理より前に済ませたいので早めに走らせる。
    public int callbackOrder { get { return -1000; } }

    public void OnProcessScene(Scene scene, BuildReport report)
    {
        // Play Mode 開始時も同じコールバックが飛んでくるが、そのときは report が null で、
        // 渡ってくるのは「実シーン」＝書き換えるとエディタの中身が壊れる。必ず弾く。
        if (report == null) return;

        bool android = report.summary.platform == BuildTarget.Android;
        var problems = new List<string>();
        int changed, matched;
        Apply(scene, android, false, problems, out changed, out matched);

        if (matched == 0) return; // 対象を含まないシーンは黙って素通り

        Debug.Log("[PlatformMaterialSwitcher] " + (android ? "Android(Quest)" : "Windows(PC)")
                  + " 向けに差し替え: scene=" + scene.name
                  + " 対象=" + matched + "/" + Rules.Length + " 変更=" + changed);
        foreach (var p in problems) Debug.LogWarning("[PlatformMaterialSwitcher] " + p);
    }

    // ---------------------------------------------------------------- 本体

    static void Apply(Scene scene, bool android, bool recordUndo, List<string> problems,
                      out int changed, out int matched)
    {
        changed = 0;
        matched = 0;
        foreach (var rule in Rules)
        {
            var go = FindByPath(scene, rule.Path);
            if (go == null) continue; // このシーンには無い

            matched++;

            var renderer = go.GetComponent<Renderer>();
            if (renderer == null)
            {
                problems.Add(rule.Path + " に Renderer が無い");
                continue;
            }

            string matPath = android ? rule.Quest : rule.Pc;
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                problems.Add("マテリアルが見つからない: " + matPath);
                continue;
            }

            var mats = renderer.sharedMaterials;
            if (rule.Slot >= mats.Length)
            {
                problems.Add(rule.Path + " のマテリアルスロット " + rule.Slot + " が無い（実際は " + mats.Length + " 個）");
                continue;
            }
            if (mats[rule.Slot] == mat) continue;

            if (recordUndo) Undo.RecordObject(renderer, "Switch Platform Materials");
            mats[rule.Slot] = mat;
            renderer.sharedMaterials = mats;
            if (recordUndo) EditorUtility.SetDirty(renderer);
            changed++;
        }
    }

    static GameObject FindByPath(Scene scene, string path)
    {
        if (!scene.IsValid() || !scene.isLoaded) return null;
        string[] parts = path.Split('/');
        Transform cur = null;
        foreach (var root in scene.GetRootGameObjects())
        {
            if (root.name != parts[0]) continue;
            cur = root.transform;
            break;
        }
        if (cur == null) return null;
        for (int i = 1; i < parts.Length; i++)
        {
            cur = cur.Find(parts[i]);
            if (cur == null) return null;
        }
        return cur.gameObject;
    }

    // ---------------------------------------------------------------- メニュー

    [MenuItem("Tools/Kemono Mahjong/Platform Materials/Apply PC (Windows)")]
    static void MenuApplyPc() { MenuApply(false); }

    [MenuItem("Tools/Kemono Mahjong/Platform Materials/Apply Quest (Android)")]
    static void MenuApplyQuest() { MenuApply(true); }

    static void MenuApply(bool android)
    {
        var problems = new List<string>();
        int totalChanged = 0, totalMatched = 0;
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            int changed, matched;
            Apply(scene, android, true, problems, out changed, out matched);
            if (changed > 0) EditorSceneManager.MarkSceneDirty(scene);
            totalChanged += changed;
            totalMatched += matched;
        }

        foreach (var p in problems) Debug.LogWarning("[PlatformMaterialSwitcher] " + p);
        if (totalMatched == 0)
        {
            Debug.LogWarning("[PlatformMaterialSwitcher] 対象オブジェクトが1つも見つからなかった（シーンが開かれているか確認）");
            return;
        }
        Debug.Log("[PlatformMaterialSwitcher] 手動適用: " + (android ? "Android(Quest)" : "Windows(PC)")
                  + " 対象=" + totalMatched + "/" + Rules.Length + " 変更=" + totalChanged
                  + (totalChanged > 0 ? "（シーン保存が必要）" : "（すでに適用済み）"));
    }

    [MenuItem("Tools/Kemono Mahjong/Platform Materials/Validate")]
    static void MenuValidate()
    {
        var sb = new StringBuilder();
        sb.AppendLine("[PlatformMaterialSwitcher] 現在のシーンの割り当て");
        sb.AppendLine("  ビルドターゲット: " + EditorUserBuildSettings.activeBuildTarget);

        int missing = 0, unknown = 0;
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            foreach (var rule in Rules)
            {
                var go = FindByPath(scene, rule.Path);
                if (go == null) continue;

                var renderer = go.GetComponent<Renderer>();
                var mats = renderer != null ? renderer.sharedMaterials : new Material[0];
                var cur = (rule.Slot < mats.Length && mats[rule.Slot] != null)
                          ? AssetDatabase.GetAssetPath(mats[rule.Slot]) : "(なし)";

                string state = cur == rule.Pc ? "PC" : (cur == rule.Quest ? "Quest" : "対応表に無い");
                sb.AppendLine("  " + rule.Path + " => [" + state + "] " + cur);
                if (state == "対応表に無い") unknown++;
            }
        }
        foreach (var rule in Rules)
        {
            if (AssetDatabase.LoadAssetAtPath<Material>(rule.Pc) == null)
            { sb.AppendLine("  !! 欠番(PC): " + rule.Pc); missing++; }
            if (AssetDatabase.LoadAssetAtPath<Material>(rule.Quest) == null)
            { sb.AppendLine("  !! 欠番(Quest): " + rule.Quest); missing++; }
        }

        if (missing > 0 || unknown > 0) Debug.LogWarning(sb.ToString());
        else Debug.Log(sb.ToString());
    }
}
#endif
