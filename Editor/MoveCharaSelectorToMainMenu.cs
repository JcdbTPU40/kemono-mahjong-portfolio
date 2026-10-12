#if UNITY_EDITOR
// 卓Prefab（KemonoMahjong.prefab）内の CharacterSelector（キャラ3Dモデル）を
// シーンの MainMenu 脇へ「MainMenuCharaPreview」として移設し、Prefab側からは削除する。
//
// ■ 背景（2026-07-15 確定仕様）
//   キャラ選択は卓ではなく MainMenu(Canvas) で行う。移設したモデルは
//   「自分が選択中のキャラをローカル表示するプレビュー」として使う
//   （MainMenuController.charaPreviewModels が SetActive を切り替える）。
//
// ■ 実行順
//   1. Tools > Kemono Mahjong > Build Main Menu
//   2. Tools > Kemono Mahjong > Move CharacterSelector To MainMenu（このスクリプト）
//   3. Tools > Kemono Mahjong > Wire Main Menu
//
// ■ 冪等性
//   - シーンに MainMenuCharaPreview が既にあれば複製しない。
//   - Prefab に CharacterSelector が無ければ削除もスキップする。
//   - MainMenuCharaPreview は Canvas の子にしない（Canvasのスケールで
//     3Dモデルが潰れるため、シーンルートに置いて位置だけ寄せる）。
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class MoveCharaSelectorToMainMenu
{
    const string PrefabPath = "Assets/Mahjong/Prefabas/KemonoMahjong.prefab";
    const string SelectorName = "CharacterSelector";
    const string PreviewName = "MainMenuCharaPreview";

    [MenuItem("Tools/Kemono Mahjong/Move CharacterSelector To MainMenu")]
    public static void Run()
    {
        var mainMenu = GameObject.Find("MainMenu");
        if (mainMenu == null)
        {
            Debug.LogError("[MoveCharaSelector] シーンに 'MainMenu' が見つかりません。中止します。");
            return;
        }

        bool sceneDirty = false;

        var prefabRoot = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (prefabRoot == null)
        {
            Debug.LogError("[MoveCharaSelector] Prefab を開けません: " + PrefabPath);
            return;
        }

        try
        {
            var selector = FindDeep(prefabRoot.transform, SelectorName);

            // --- 1) シーンへプレビューを複製（未存在のときのみ） ---
            var existing = GameObject.Find(PreviewName);
            if (existing == null)
            {
                if (selector == null)
                {
                    Debug.LogError("[MoveCharaSelector] Prefab 内に '" + SelectorName +
                        "' が見つからず、シーンに '" + PreviewName + "' もありません。移設元が失われています。中止します。");
                    return;
                }

                var copy = Object.Instantiate(selector.gameObject);
                copy.name = PreviewName;
                // Prefab編集用の隔離シーンに生まれるため、アクティブシーンへ移す。
                SceneManager.MoveGameObjectToScene(copy, SceneManager.GetActiveScene());
                Undo.RegisterCreatedObjectUndo(copy, "Move CharacterSelector To MainMenu");

                // MainMenu の右脇 1.5m に仮置き（位置はシーンで調整する前提）。
                copy.transform.SetPositionAndRotation(
                    mainMenu.transform.position + mainMenu.transform.right * 1.5f,
                    mainMenu.transform.rotation);
                copy.SetActive(true);

                Debug.Log("[MoveCharaSelector] '" + PreviewName + "' をシーンに作成しました（位置は要調整）。");
                sceneDirty = true;
            }
            else
            {
                Debug.Log("[MoveCharaSelector] '" + PreviewName + "' は既に存在するため複製をスキップします。");
            }

            // --- 2) Prefab から CharacterSelector を削除 ---
            if (selector != null)
            {
                Object.DestroyImmediate(selector.gameObject);
                PrefabUtility.SaveAsPrefabAsset(prefabRoot, PrefabPath);
                Debug.Log("[MoveCharaSelector] Prefab から '" + SelectorName + "' を削除しました: " + PrefabPath);
            }
            else
            {
                Debug.Log("[MoveCharaSelector] Prefab に '" + SelectorName + "' は無いため削除をスキップします（実施済み）。");
            }
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(prefabRoot);
        }

        if (sceneDirty)
        {
            EditorSceneManager.MarkSceneDirty(mainMenu.scene);
        }

        Debug.Log("[MoveCharaSelector] 完了。次に Tools > Kemono Mahjong > Wire Main Menu を実行して " +
                  "charaPreviewModels の割り当てを反映してください。");
    }

    // 名前一致の子孫を深さ優先で探す。
    static Transform FindDeep(Transform t, string name)
    {
        if (t.name == name) { return t; }
        foreach (Transform child in t)
        {
            var found = FindDeep(child, name);
            if (found != null) { return found; }
        }
        return null;
    }
}
#endif
