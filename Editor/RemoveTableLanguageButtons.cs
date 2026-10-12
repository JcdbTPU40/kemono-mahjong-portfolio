#if UNITY_EDITOR
// 卓に付いていた Jp/En 切替ボタン（LanguageNextButton0-3）を撤去する一回きりの移行ツール。
//
//   Tools > Kemono Mahjong > Remove Table Language Buttons
//
// ■ なぜ消すのか
//   表示言語は入場時に VRChat 本体の設定から自動で決まり、変えたい人は
//   MainMenu > ワールド設定 で切り替える。卓ごとにボタンを置く必要がなくなった。
//   （卓上ボタンは z スロットに排他ボタンを重ねる規約なので、使わないボタンを
//     残しておくと押し間違いと当たり判定の無駄が残る。）
//
// ■ どこを消すか
//   プレハブ Assets/Mahjong/Prefabas/KemonoMahjong.prefab の Table 直下。
//   シーンの8卓はこのプレハブのインスタンスなので、プレハブを直せば全卓から消える。
//   プレハブ外に置かれた野良のボタン（id が SwitchLanguageNext のもの）もシーンから消す。
//
// ■ 注意
//   RestoreTableButtonComponents は「LanguageNextButtonN という名前の
//   GameObject が既にある場合だけ」コンポーネントを復元する。オブジェクトごと
//   消えていれば何も復元しないので、あちらを再実行しても復活しない。
using System.Collections.Generic;
using System.Linq;
using SuzuFactory.AozoraMahjong;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class RemoveTableLanguageButtons
{
    const string PrefabPath = "Assets/Mahjong/Prefabas/KemonoMahjong.prefab";
    const string ButtonId = "SwitchLanguageNext";

    [MenuItem("Tools/Kemono Mahjong/Remove Table Language Buttons")]
    public static void Run()
    {
        int fromPrefab = RemoveFromPrefab();
        int fromScene = RemoveFromScene();

        Debug.Log("[RemoveTableLanguageButtons] 完了: プレハブ " + fromPrefab
                  + " 個 / シーン（プレハブ外） " + fromScene + " 個を削除しました。"
                  + (fromPrefab + fromScene == 0
                     ? " 既に撤去済みです。"
                     : " 言語切替は MainMenu > ワールド設定 に集約されました。"));
    }

    static int RemoveFromPrefab()
    {
        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null)
        {
            Debug.LogError("[RemoveTableLanguageButtons] プレハブを読めません: " + PrefabPath);
            return 0;
        }

        int removed = 0;
        try
        {
            foreach (var go in CollectButtons(root.transform))
            {
                Object.DestroyImmediate(go);
                ++removed;
            }

            if (removed > 0)
            {
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        return removed;
    }

    // プレハブのインスタンスに属さない野良ボタンだけを消す。
    // インスタンス側の子はプレハブを直した時点で消えるので触らない。
    static int RemoveFromScene()
    {
        var targets = new List<GameObject>();

        foreach (var b in Object.FindObjectsOfType<MahjongPushButton>(true))
        {
            if (b == null || b.id != ButtonId) { continue; }
            if (!b.gameObject.scene.IsValid()) { continue; }
            if (PrefabUtility.IsPartOfPrefabInstance(b.gameObject)) { continue; }
            targets.Add(b.gameObject);
        }

        foreach (var go in targets)
        {
            Undo.DestroyObjectImmediate(go);
        }

        if (targets.Count > 0)
        {
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        }
        return targets.Count;
    }

    // 言語ボタン本体を集める。id で拾えなかった場合の保険として名前でも拾う
    // （プレハブ側で MahjongPushButton が外れている個体が過去にあった）。
    static List<GameObject> CollectButtons(Transform root)
    {
        var found = new List<GameObject>();

        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t == null) { continue; }

            var button = t.GetComponent<MahjongPushButton>();
            bool byId = button != null && button.id == ButtonId;
            bool byName = t.name.StartsWith("LanguageNextButton");
            if (!byId && !byName) { continue; }

            found.Add(t.gameObject);
        }

        // 親子で二重に拾った場合、子を消してから親を消すと参照が壊れるので
        // 祖先が同じリストに入っているものは除外する。
        return found
            .Where(go => !found.Any(other => other != go && go.transform.IsChildOf(other.transform)))
            .ToList();
    }

    [MenuItem("Tools/Kemono Mahjong/Validate Table Language Buttons Removed")]
    public static void Validate()
    {
        int left = Object.FindObjectsOfType<MahjongPushButton>(true)
            .Count(b => b != null && b.id == ButtonId);

        if (left == 0)
        {
            Debug.Log("[RemoveTableLanguageButtons] OK: 卓の言語ボタンは残っていません。");
            return;
        }

        Debug.LogError("[RemoveTableLanguageButtons] まだ " + left
                       + " 個残っています。Remove Table Language Buttons を実行してください。");
    }
}
#endif
