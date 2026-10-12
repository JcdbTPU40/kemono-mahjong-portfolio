#if UNITY_EDITOR
// 卓のボタンを指で押し込む入力（MahjongTouchInput）を卓 Prefab に組み込む。
//
//   KemonoMahjong/TouchInput   … MahjongTouchInput（常時アクティブ）
//     buttons / colliders      … PlayerUIWrapper/PlayerUI の対局ボタン全部＋ChiSelectPanel の候補6つと戻る
//
// 牌（PickTiles）・NEXT・ENTRY・自動化/カメラパネルは対象外（レーザーのまま）。
// 2回実行しても安全（冪等）。Prefab を保存するのでシーンの全卓に反映される。
// 実行: Tools > Kemono Mahjong > Build Touch Input
//       （初回は U# プログラムアセット生成→コンパイル待ちで止まるので、もう一度実行）
using System.Collections.Generic;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using SuzuFactory.AozoraMahjong;

public static class BuildTouchInput
{
    const string PrefabPath = "Assets/Mahjong/Prefabas/KemonoMahjong.prefab";
    const string ScriptPath = "Assets/Mahjong/Sources/MahjongTouchInput.cs";
    const string ProgramAssetPath = "Assets/Mahjong/scene_UdonProgramSources/MahjongTouchInput Udon C# Program Asset.asset";
    const string PlayerUiPath = "PlayerUIWrapper/PlayerUI";
    const string ChiContentPath = "ChiSelectPanel/Root/Content";
    const int ChiOptionMax = 6;

    [MenuItem("Tools/Kemono Mahjong/Build Touch Input")]
    public static void Run()
    {
        if (EnsureProgramAsset())
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[BuildTouchInput] U#プログラムアセットを生成しました。コンパイル完了後、もう一度実行してください。");
            return;
        }

        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null) { Debug.LogError("[BuildTouchInput] Prefab を読み込めません: " + PrefabPath); return; }

        try
        {
            var buttons = new List<MahjongPushButton>();
            var colliders = new List<BoxCollider>();
            if (!CollectTargets(root.transform, buttons, colliders, out string why))
            {
                Debug.LogError("[BuildTouchInput] " + why);
                return;
            }

            var go = root.transform.Find("TouchInput") != null
                ? root.transform.Find("TouchInput").gameObject
                : new GameObject("TouchInput");
            go.transform.SetParent(root.transform, false);
            go.SetActive(true);

            var touch = (MahjongTouchInput)EnsureUSharpComponent(go, typeof(MahjongTouchInput));
            if (touch == null) { return; }
            touch.buttons = buttons.ToArray();
            touch.colliders = colliders.ToArray();
            UdonSharpEditorUtility.CopyProxyToUdon(touch);

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log("[BuildTouchInput] 完了: タッチの対象 " + buttons.Count + " 個を配線し、Prefab を保存しました（シーンの全卓に反映）。");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Validate();
    }

    [MenuItem("Tools/Kemono Mahjong/Validate Touch Input")]
    public static void Validate()
    {
        int tables = 0, ok = 0;
        var sb = new System.Text.StringBuilder();
        foreach (var m in Object.FindObjectsOfType<Mahjong>(true))
        {
            if (!m.gameObject.scene.IsValid()) { continue; }
            ++tables;
            string why = Check(m.transform);
            if (why == null) { ++ok; }
            else { sb.Append(m.name).Append(": ").Append(why).Append("\n"); }
        }

        if (tables > 0 && ok == tables)
        {
            Debug.Log("[ValidateTouchInput] OK: 卓 " + tables + " 個すべてにタッチ入力あり・配線済み。");
        }
        else
        {
            Debug.LogError("[ValidateTouchInput] NG " + (tables - ok) + " / " + tables + "\n" + sb);
        }
    }

    static string Check(Transform table)
    {
        var t = table.Find("TouchInput");
        if (t == null) { return "TouchInput が無い"; }
        if (!t.gameObject.activeSelf) { return "TouchInput が非アクティブ"; }
        var touch = t.GetComponent<MahjongTouchInput>();
        if (touch == null) { return "MahjongTouchInput が無い"; }

        var buttons = new List<MahjongPushButton>();
        var colliders = new List<BoxCollider>();
        if (!CollectTargets(table, buttons, colliders, out string why)) { return why; }

        if (touch.buttons == null || touch.colliders == null) { return "配列が空"; }
        if (touch.buttons.Length != buttons.Count || touch.colliders.Length != buttons.Count)
        {
            return "対象の数が違う（配線 " + touch.buttons.Length + " / 実物 " + buttons.Count + "）＝Build Touch Input をやり直す";
        }
        for (int i = 0; i < buttons.Count; ++i)
        {
            if (touch.buttons[i] != buttons[i] || touch.colliders[i] != colliders[i])
            {
                return "配線 " + i + " が実物と違う（" + (touch.buttons[i] != null ? touch.buttons[i].name : "null") + "）";
            }
        }
        return null;
    }

    // 対象＝PlayerUI 直下の MahjongPushButton 全部（並び順のまま）＋チー候補パネルの Option0..5 と Back。
    static bool CollectTargets(Transform table, List<MahjongPushButton> buttons, List<BoxCollider> colliders, out string why)
    {
        why = null;
        var ui = table.Find(PlayerUiPath);
        if (ui == null) { why = PlayerUiPath + " が無い"; return false; }
        foreach (Transform c in ui)
        {
            var pb = c.GetComponent<MahjongPushButton>();
            if (pb == null) { continue; }
            if (!Add(c, pb, buttons, colliders, out why)) { return false; }
        }
        if (buttons.Count == 0) { why = "PlayerUI に MahjongPushButton が無い"; return false; }

        var chi = table.Find(ChiContentPath);
        if (chi == null) { why = ChiContentPath + " が無い（先に Build Chi Select Panel）"; return false; }
        for (int k = 0; k <= ChiOptionMax; ++k)
        {
            string name = k < ChiOptionMax ? "Option" + k : "Back";
            var c = chi.Find(name);
            var pb = c != null ? c.GetComponent<MahjongPushButton>() : null;
            if (pb == null) { why = "チー候補パネルの " + name + " に MahjongPushButton が無い"; return false; }
            if (!Add(c, pb, buttons, colliders, out why)) { return false; }
        }
        return true;
    }

    static bool Add(Transform c, MahjongPushButton pb, List<MahjongPushButton> buttons, List<BoxCollider> colliders, out string why)
    {
        why = null;
        var col = c.GetComponent<BoxCollider>();
        if (col == null) { why = c.name + " に BoxCollider が無い（タッチの判定に使う）"; return false; }
        buttons.Add(pb);
        colliders.Add(col);
        return true;
    }

    static bool EnsureProgramAsset()
    {
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(ScriptPath);
        if (script == null)
        {
            Debug.LogError("[BuildTouchInput] スクリプトが見つかりません: " + ScriptPath);
            return false;
        }
        var type = script.GetClass();
        if (type != null && UdonSharpProgramAsset.GetProgramAssetForClass(type) != null) { return false; }
        if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(ProgramAssetPath) != null) { return false; }

        var programAsset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
        programAsset.sourceCsScript = script;
        AssetDatabase.CreateAsset(programAsset, ProgramAssetPath);
        AssetDatabase.SaveAssets();
        return true;
    }

    static UdonSharpBehaviour EnsureUSharpComponent(GameObject go, System.Type type)
    {
        var proxy = (UdonSharpBehaviour)go.GetComponent(type);
        if (proxy != null && UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy) != null) { return proxy; }
        if (proxy != null) { Object.DestroyImmediate(proxy); }

        var added = (UdonSharpBehaviour)UdonSharpUndo.AddComponent(go, type);
        if (added != null && UdonSharpEditorUtility.GetBackingUdonBehaviour(added) == null)
        {
            Debug.LogError("[BuildTouchInput] " + type.Name + " の追加に失敗（プログラムアセット未コンパイル？）。");
            Object.DestroyImmediate(added);
            return null;
        }
        return added;
    }
}
#endif
