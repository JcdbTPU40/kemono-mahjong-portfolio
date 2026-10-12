#if UNITY_EDITOR
// FBX差し替えで KemonoMahjong.prefab の Table 配下ボタンから消えた
// BoxCollider + MahjongPushButton / MahjongTopCameraViewButton (U#) を
// ベースの Mahjong.prefab から同名オブジェクトへ移植する一回きりの復元ツール。
// 実行: メニュー Tools > Kemono Mahjong > Restore Table Button Components
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UdonSharp;
using UdonSharpEditor;
using SuzuFactory.AozoraMahjong;

public static class RestoreTableButtonComponents
{
    const string SrcPath = "Assets/Mahjong/Prefabas/Mahjong.prefab";
    const string DstPath = "Assets/Mahjong/Prefabas/KemonoMahjong.prefab";

    [MenuItem("Tools/Kemono Mahjong/Restore Table Button Components")]
    public static void Run()
    {
        var srcRoot = PrefabUtility.LoadPrefabContents(SrcPath);
        var dstRoot = PrefabUtility.LoadPrefabContents(DstPath);
        int restored = 0;
        var notFound = new List<string>();
        MahjongPushButton languageTemplate = null;
        BoxCollider languageTemplateCol = null;

        try
        {
            // 移植先を名前で索引化（Table配下に限らず全子孫）
            var dstByName = new Dictionary<string, Transform>();
            foreach (var t in dstRoot.GetComponentsInChildren<Transform>(true))
                if (!dstByName.ContainsKey(t.name))
                    dstByName[t.name] = t;

            foreach (var srcProxy in srcRoot.GetComponentsInChildren<UdonSharpBehaviour>(true))
            {
                bool isPush = srcProxy is MahjongPushButton;
                bool isTopCam = srcProxy is MahjongTopCameraViewButton;
                if (!isPush && !isTopCam) continue;

                string name = srcProxy.gameObject.name;

                if (isPush && name.StartsWith("LanguageNextButton"))
                {
                    languageTemplate = (MahjongPushButton)srcProxy;
                    languageTemplateCol = srcProxy.GetComponent<BoxCollider>();
                }

                if (!dstByName.TryGetValue(name, out var dstT)) { notFound.Add(name); continue; }
                var dstGO = dstT.gameObject;

                // 既にU#が付いているもの（PlayerUI系など無事なボタン）はスキップ
                if (dstGO.GetComponent<UdonSharpBehaviour>() != null) continue;

                RestoreOne(srcProxy, dstGO, dstByName, ref restored);
            }

            // LanguageNextButton0-3: 元prefab側に全席分ない場合はテンプレから複製
            if (languageTemplate != null)
            {
                for (int i = 0; i < 4; i++)
                {
                    string n = "LanguageNextButton" + i;
                    if (dstByName.TryGetValue(n, out var t) &&
                        t.GetComponent<UdonSharpBehaviour>() == null)
                    {
                        CopyCollider(languageTemplateCol, t.gameObject);
                        var p = (MahjongPushButton)UdonSharpUndo.AddComponent(t.gameObject, typeof(MahjongPushButton));
                        p.local = languageTemplate.local;
                        p.disableOnPush = languageTemplate.disableOnPush;
                        p.target = languageTemplate.target;
                        p.id = languageTemplate.id; // SwitchLanguageNext（席番号なし共通）
                        UdonSharpEditorUtility.CopyProxyToUdon(p);
                        restored++;
                        Debug.Log($"[Restore] {n}: BoxCollider + MahjongPushButton (template)");
                    }
                }
            }

            PrefabUtility.SaveAsPrefabAsset(dstRoot, DstPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(srcRoot);
            PrefabUtility.UnloadPrefabContents(dstRoot);
        }

        Debug.Log($"[Restore] 完了: {restored} 個のボタンを復元。" +
                  (notFound.Count > 0 ? " KemonoMahjong側に見つからず: " + string.Join(", ", notFound.Distinct()) : ""));
    }

    static void RestoreOne(UdonSharpBehaviour srcProxy, GameObject dstGO,
                           Dictionary<string, Transform> dstByName, ref int restored)
    {
        CopyCollider(srcProxy.GetComponent<BoxCollider>(), dstGO);
        dstGO.layer = srcProxy.gameObject.layer;

        var newProxy = UdonSharpUndo.AddComponent(dstGO, srcProxy.GetType());

        if (srcProxy is MahjongPushButton pb)
        {
            var p = (MahjongPushButton)newProxy;
            p.local = pb.local;
            p.disableOnPush = pb.disableOnPush;
            p.target = pb.target;
            p.id = pb.id;
        }
        else if (srcProxy is MahjongTopCameraViewButton tb)
        {
            var p = (MahjongTopCameraViewButton)newProxy;
            // 参照はプレハブをまたげないため、同名オブジェクトで再解決
            if (tb.view != null && dstByName.TryGetValue(tb.view.name, out var v)) p.view = v.gameObject;
            if (tb.cam != null && dstByName.TryGetValue(tb.cam.name, out var c)) p.cam = c.gameObject;
            if (p.view == null || p.cam == null)
                Debug.LogWarning($"[Restore] {dstGO.name}: view/cam の参照を解決できず。手動で設定してください。");
        }

        UdonSharpEditorUtility.CopyProxyToUdon(newProxy);
        restored++;
        Debug.Log($"[Restore] {dstGO.name}: BoxCollider + {srcProxy.GetType().Name} (id={(srcProxy as MahjongPushButton)?.id ?? "-"})");
    }

    static void CopyCollider(BoxCollider src, GameObject dstGO)
    {
        if (src == null || dstGO.GetComponent<BoxCollider>() != null) return;
        var c = dstGO.AddComponent<BoxCollider>();
        c.center = src.center;
        c.size = src.size;
        c.isTrigger = src.isTrigger;
    }
}
#endif
