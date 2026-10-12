// 観戦ルームの声を外へ漏らさない仕組み（TableVoiceRouter）と、スタッフ用マイク（KemonoStaffMic）を置く。
//
//   Tools/Kemono Mahjong/Build Table Voice     … 振り分け役とマイクを作る／配線を張り直す
//   Tools/Kemono Mahjong/Validate Table Voice  … 配線を検査する
//
// - 振り分け役はシーン直下の TableVoiceRouter（常時アクティブ・同期なし）。観戦ルームの範囲は
//   AetherLoft の Carpet_01 の床を測った箱（高さも区切る＝真上の卓 4 は入らない）。
//   観戦モニターにも渡し、ルームの中だけで観戦できるようにする。
// - マイクはシーン直下の StaffMic（VRCPickup＋VRCObjectSync＋KemonoStaffMic）。初回だけ受付の
//   スタッフパネル（StaffEventPanel）の脇の台へ置く。2回目以降は位置を動かさない（手で置き直せる）。
using System.Collections.Generic;
using SuzuFactory.AozoraMahjong;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Components;
using VRC.Udon;

public static class BuildTableVoice
{
    const string RouterName = "TableVoiceRouter";
    const string MicName = "StaffMic";
    const string RouterScriptPath = "Assets/Mahjong/Sources/TableVoiceRouter.cs";
    const string MicScriptPath = "Assets/Mahjong/Sources/KemonoStaffMic.cs";
    const string RouterProgramPath = "Assets/Mahjong/scene_UdonProgramSources/TableVoiceRouter Udon C# Program Asset.asset";
    const string MicProgramPath = "Assets/Mahjong/scene_UdonProgramSources/KemonoStaffMic Udon C# Program Asset.asset";
    const string MicModelPath = "Assets/udonmic/item/mic.fbx";
    const string StaffPanelName = "StaffEventPanel";
    const string CounterPath = "Counter/Table_02";   // 受付カウンターの天板
    const string RoomAreaName = "SpectatorRoomArea";  // 観戦ルームの箱（Scene ビューで動かして範囲を調整する）
    const string CarpetName = "Carpet_01";            // 観戦ルームの床（AetherLoft/Prop/Carpet_01）
    const float RoomHeight = 2.5f;                    // 床からこの高さまでをルームとみなす
    const float MicLength = 0.22f;   // マイクの長い辺（m）

    [MenuItem("Tools/Kemono Mahjong/Build Table Voice")]
    public static void Run()
    {
        if (EditorApplication.isPlaying)
        {
            Debug.LogError("[BuildTableVoice] 再生中は実行できません。");
            return;
        }
        bool created = EnsureProgramAsset(RouterScriptPath, RouterProgramPath);
        created |= EnsureProgramAsset(MicScriptPath, MicProgramPath);
        if (created)
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[BuildTableVoice] U#プログラムアセットを生成しました。コンパイル完了後、もう一度 Build Table Voice を実行してください。");
            return;
        }

        var matchmaking = Object.FindObjectOfType<MatchmakingManager>(true);
        var monitor = Object.FindObjectOfType<SpectatorMonitor>(true);
        if (matchmaking == null) { Debug.LogError("[BuildTableVoice] MatchmakingManager がありません。"); return; }
        // 観戦ルームの箱。初回だけ Carpet_01 から測って置き、2回目以降は手で調整した位置・大きさを保つ
        var areaGo = GameObject.Find(RoomAreaName);
        bool areaNew = areaGo == null;
        if (areaNew)
        {
            if (!MeasureRoom(out Vector3 roomMin, out Vector3 roomMax)) { return; }
            areaGo = new GameObject(RoomAreaName);
            Undo.RegisterCreatedObjectUndo(areaGo, "Build Table Voice");
            areaGo.transform.position = (roomMin + roomMax) * 0.5f;
            areaGo.transform.rotation = Quaternion.identity;
            areaGo.transform.localScale = roomMax - roomMin;
        }

        // --- マイク ---
        var micGo = GameObject.Find(MicName);
        bool micNew = micGo == null;
        if (micNew)
        {
            micGo = new GameObject(MicName);
            Undo.RegisterCreatedObjectUndo(micGo, "Build Table Voice");
        }
        var mic = EnsureUdon<KemonoStaffMic>(micGo);
        if (mic == null) { return; }
        EnsureMicBody(micGo, micNew);
        var pickup = micGo.GetComponent<VRCPickup>();
        if (pickup == null) { pickup = Undo.AddComponent<VRCPickup>(micGo); }
        pickup.AutoHold = VRC.SDKBase.VRC_Pickup.AutoHoldMode.Yes;
        pickup.orientation = VRC.SDKBase.VRC_Pickup.PickupOrientation.Grip;
        pickup.InteractionText = "スタッフ用マイク";
        pickup.UseText = "";
        var objectSync = micGo.GetComponent<VRCObjectSync>();
        if (objectSync == null) { objectSync = Undo.AddComponent<VRCObjectSync>(micGo); }
        objectSync.AllowCollisionOwnershipTransfer = false;
        mic.matchmaking = matchmaking;
        mic.pickup = pickup;
        mic.objectSync = objectSync;
        EditorUtility.SetDirty(mic);

        // --- 振り分け役 ---
        var routerGo = GameObject.Find(RouterName);
        if (routerGo == null)
        {
            routerGo = new GameObject(RouterName);
            Undo.RegisterCreatedObjectUndo(routerGo, "Build Table Voice");
        }
        var router = EnsureUdon<TableVoiceRouter>(routerGo);
        if (router == null) { return; }
        router.roomArea = areaGo.transform;
        router.matchmaking = matchmaking;
        router.staffMic = mic;
        EditorUtility.SetDirty(router);

        // 観戦モニターはルームの中だけ（入ったら ON・出たら OFF）
        if (monitor != null)
        {
            monitor.voiceRouter = router;
            EditorUtility.SetDirty(monitor);
        }
        else { Debug.LogWarning("[BuildTableVoice] SpectatorMonitor がありません（観戦のルーム制限は掛かりません）。"); }

        EditorSceneManager.MarkSceneDirty(routerGo.scene);
        BuildSpectatorRoomBoundary.Run();   // 床の防音マークの帯を箱に合わせ直す
        Debug.Log("[BuildTableVoice] 完了: 観戦ルーム 中心 " + areaGo.transform.position.ToString("F2") + " 大きさ " + areaGo.transform.localScale.ToString("F2")
                  + (areaNew ? "（Carpet_01 から新規に作った）" : "（手で調整した箱をそのまま使う）") + " / マイク " + micGo.transform.position
                  + (micNew ? "（新規に受付へ置いた）" : "（位置はそのまま）"));
    }

    [MenuItem("Tools/Kemono Mahjong/Validate Table Voice")]
    public static void Validate()
    {
        var problems = new List<string>();
        var router = Object.FindObjectOfType<TableVoiceRouter>(true);
        var mic = Object.FindObjectOfType<KemonoStaffMic>(true);
        if (router == null) { problems.Add("TableVoiceRouter がシーンにない"); }
        else
        {
            if (!router.gameObject.activeInHierarchy) { problems.Add("TableVoiceRouter が非アクティブ（常時アクティブでないと振り分けが止まる）"); }
            if (router.roomArea == null) { problems.Add("roomArea（観戦ルームの箱）が空。Build Table Voice を実行する"); }
            else
            {
                Vector3 size = router.roomArea.lossyScale;
                if (Mathf.Abs(size.x) < 1f || Mathf.Abs(size.y) < 1f || Mathf.Abs(size.z) < 1f) { problems.Add("観戦ルームの箱が小さすぎる（" + size + "）"); }
                if (!router.roomArea.gameObject.scene.IsValid()) { problems.Add("観戦ルームの箱がシーンに無い"); }
            }
            var monitor = Object.FindObjectOfType<SpectatorMonitor>(true);
            if (monitor != null && monitor.voiceRouter != router) { problems.Add("観戦モニターの voiceRouter が空（ルームの外でも観戦できてしまう）"); }
            if (router.matchmaking == null) { problems.Add("matchmaking が空"); }
            if (router.staffMic == null) { problems.Add("staffMic が空"); }
        }
        if (mic == null) { problems.Add("KemonoStaffMic がシーンにない"); }
        else
        {
            if (mic.matchmaking == null) { problems.Add("マイクの matchmaking が空"); }
            if (mic.pickup == null) { problems.Add("マイクの pickup が空"); }
            if (mic.objectSync == null) { problems.Add("マイクの objectSync が空"); }
            if (mic.GetComponent<Collider>() == null) { problems.Add("マイクに Collider が無い（掴めない）"); }
            // VRCObjectSync と同じ GameObject の Udon は Manual 同期にできない（SDK のビルド前チェックで警告）
            var ub = UdonSharpEditorUtility.GetBackingUdonBehaviour(mic);
            if (ub != null && ub.SyncMethod == VRC.SDKBase.Networking.SyncType.Manual) { problems.Add("マイクの Udon が Manual 同期（ObjectSync と同居できない。Continuous にする）"); }
        }
        if (problems.Count == 0) { Debug.Log("[ValidateTableVoice] 問題なし"); }
        else { Debug.LogError("[ValidateTableVoice] " + problems.Count + " 件\n- " + string.Join("\n- ", problems)); }
    }

    // 観戦ルームの箱を選んで Scene ビューを寄せる（移動 W／拡大 R／矩形 T ツールで範囲を調整する）
    [MenuItem("Tools/Kemono Mahjong/Select Spectator Room Area")]
    public static void SelectRoomArea()
    {
        var go = GameObject.Find(RoomAreaName);
        if (go == null) { Debug.LogWarning("[BuildTableVoice] " + RoomAreaName + " がありません。先に Build Table Voice を実行してください。"); return; }
        Selection.activeGameObject = go;
        if (SceneView.lastActiveSceneView != null) { SceneView.lastActiveSceneView.FrameSelected(); }
    }

    // 箱をカーペットの大きさへ戻す（調整をやり直したいとき）
    [MenuItem("Tools/Kemono Mahjong/Reset Spectator Room Area To Carpet")]
    public static void ResetRoomArea()
    {
        var go = GameObject.Find(RoomAreaName);
        if (go == null) { Debug.LogWarning("[BuildTableVoice] " + RoomAreaName + " がありません。先に Build Table Voice を実行してください。"); return; }
        if (!MeasureRoom(out Vector3 roomMin, out Vector3 roomMax)) { return; }
        Undo.RecordObject(go.transform, "Reset Spectator Room Area");
        go.transform.position = (roomMin + roomMax) * 0.5f;
        go.transform.rotation = Quaternion.identity;
        go.transform.localScale = roomMax - roomMin;
        BuildSpectatorRoomBoundary.RebuildMesh(go.transform);
        EditorSceneManager.MarkSceneDirty(go.scene);
        Debug.Log("[BuildTableVoice] 観戦ルームの箱を Carpet_01 の大きさへ戻しました。");
    }


    // 卓番号順（MatchmakingManager.tables の並び）に、その卓を tableRoot に持つカリングを並べる
    // 観戦ルーム：Carpet_01 の床の広さ。高さは床から RoomHeight まで（足元の位置で判定するので、
    // 真上の卓 4（床の約 4.5m 上）にいる人は入らない）。
    static bool MeasureRoom(out Vector3 min, out Vector3 max)
    {
        min = Vector3.zero;
        max = Vector3.zero;
        GameObject carpet = null;
        foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
        {
            if (!go.scene.IsValid() || go.name != CarpetName) { continue; }
            if (go.transform.parent != null && go.transform.parent.parent != null && go.transform.parent.parent.name == "AetherLoft") { carpet = go; break; }
            if (carpet == null) { carpet = go; }
        }
        var r = carpet != null ? carpet.GetComponent<Renderer>() : null;
        if (r == null) { Debug.LogError("[BuildTableVoice] " + CarpetName + "（観戦ルームの床）が見つかりません。"); return false; }
        var b = r.bounds;
        min = new Vector3(b.min.x, b.min.y - 0.3f, b.min.z);
        max = new Vector3(b.max.x, b.min.y + RoomHeight, b.max.z);
        return true;
    }

    // マイクの見た目（mic.fbx）・掴むための当たり判定・物理。初回だけ受付へ置く
    static void EnsureMicBody(GameObject micGo, bool place)
    {
        var t = micGo.transform;
        var model = t.Find("Model");
        if (model == null)
        {
            var src = AssetDatabase.LoadAssetAtPath<GameObject>(MicModelPath);
            GameObject inst = src != null ? (GameObject)PrefabUtility.InstantiatePrefab(src) : GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Undo.RegisterCreatedObjectUndo(inst, "Build Table Voice");
            inst.name = "Model";
            inst.transform.SetParent(t, false);
            foreach (var col in inst.GetComponentsInChildren<Collider>(true)) { Object.DestroyImmediate(col); }
            model = inst.transform;
        }
        // 長い辺を MicLength にそろえ、中心を親の原点へ
        model.localPosition = Vector3.zero;
        model.localRotation = Quaternion.identity;
        model.localScale = Vector3.one;
        var rs = model.GetComponentsInChildren<Renderer>(true);
        if (rs.Length > 0)
        {
            var b = rs[0].bounds;
            for (int i = 1; i < rs.Length; ++i) { b.Encapsulate(rs[i].bounds); }
            float longest = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            if (longest > 0.0001f)
            {
                float s = MicLength / longest;
                model.localScale = Vector3.one * s;
                // 縮めた後の見た目の中心を親の原点へ
                var b2 = rs[0].bounds;
                for (int i = 1; i < rs.Length; ++i) { b2.Encapsulate(rs[i].bounds); }
                model.position += t.position - b2.center;
            }
        }

        var box = micGo.GetComponent<BoxCollider>();
        if (box == null) { box = Undo.AddComponent<BoxCollider>(micGo); }
        rs = model.GetComponentsInChildren<Renderer>(true);
        if (rs.Length > 0)
        {
            var b = rs[0].bounds;
            for (int i = 1; i < rs.Length; ++i) { b.Encapsulate(rs[i].bounds); }
            box.center = t.InverseTransformPoint(b.center);
            Vector3 size = t.InverseTransformVector(b.size);
            box.size = new Vector3(Mathf.Abs(size.x) + 0.02f, Mathf.Abs(size.y) + 0.02f, Mathf.Abs(size.z) + 0.02f);
        }
        box.isTrigger = false;

        var rb = micGo.GetComponent<Rigidbody>();
        if (rb == null) { rb = Undo.AddComponent<Rigidbody>(micGo); }
        rb.isKinematic = true;
        rb.useGravity = false;

        if (place)
        {
            // 受付カウンター（Counter/Table_02）の天板の、スタッフ側（奥＝z が小さい側）の端へ載せる。
            // カウンターには当たり判定が無いので、見た目（Renderer）の上面を使う。
            // 天板はメインメニューの画面（下端 y 1.86）より低いので、メニューのクリックを邪魔しない。
            var counter = GameObject.Find(CounterPath);
            var cr = counter != null ? counter.GetComponent<Renderer>() : null;
            if (cr != null)
            {
                var cb = cr.bounds;
                t.position = new Vector3(cb.max.x - 0.25f, cb.max.y + 0.04f, cb.min.z + 0.13f);
            }
            else
            {
                var panel = GameObject.Find(StaffPanelName);
                t.position = panel != null ? panel.transform.position : Vector3.zero;
            }
            t.rotation = Quaternion.Euler(0f, 0f, 90f);
        }
    }

    static T EnsureUdon<T>(GameObject go) where T : UdonSharpBehaviour
    {
        var comp = go.GetComponent<T>();
        if (comp != null)
        {
            var existing = UdonSharpEditorUtility.GetBackingUdonBehaviour(comp);
            if (existing != null && existing.programSource != null) { return comp; }
            Object.DestroyImmediate(comp);
            foreach (var ub in go.GetComponents<UdonBehaviour>())
            {
                if (ub.programSource == null) { Object.DestroyImmediate(ub); }
            }
        }
        var added = (T)UdonSharpUndo.AddComponent(go, typeof(T));
        if (added == null || UdonSharpEditorUtility.GetBackingUdonBehaviour(added) == null)
        {
            Debug.LogError("[BuildTableVoice] " + typeof(T).Name + " を追加できません（U#未コンパイル？）。コンパイル後に再実行してください。");
            if (added != null) { Object.DestroyImmediate(added); }
            return null;
        }
        return added;
    }

    static bool EnsureProgramAsset(string scriptPath, string programPath)
    {
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
        if (script == null) { Debug.LogError("[BuildTableVoice] スクリプトが見つかりません: " + scriptPath); return false; }
        var type = script.GetClass();
        if (type != null && UdonSharpProgramAsset.GetProgramAssetForClass(type) != null) { return false; }
        if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(programPath) != null) { return false; }
        var programAsset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
        programAsset.sourceCsScript = script;
        AssetDatabase.CreateAsset(programAsset, programPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[BuildTableVoice] U#プログラムアセットを生成: " + programPath);
        return true;
    }
}
