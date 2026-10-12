#if UNITY_EDITOR
// 鳴ける牌のマーカー（MahjongCallTargetMarker）を卓 Prefab に組み立てる。
//
//   KemonoMahjong/CallTargetMarker      … MahjongCallTargetMarker（常時アクティブ）
//     Visual                            … visualRoot（表示ON/OFF）
//       Frame                           … 牌の縁から外へにじむ緑の輪（牌の姿勢に合わせる）
//       Arrow                           … 牌の真上の下向き矢印（四角錐・上下に揺れる）
//
// 枠の寸法は見本の牌（TilesSet/Tiles0/M1）のメッシュ×スケールから測る＝牌を作り直しても追従する。
// 2回実行しても安全（冪等）。メッシュとマテリアルは中身だけ更新する（参照が切れない）。
// 実行: Tools > Kemono Mahjong > Build Call Target Marker
//       （初回は U# プログラムアセット生成→コンパイル待ちで止まるので、もう一度実行）
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using SuzuFactory.AozoraMahjong;

public static class BuildCallTargetMarker
{
    const string PrefabPath = "Assets/Mahjong/Prefabas/KemonoMahjong.prefab";
    const string Dir = "Assets/Mahjong/Textures/UI/CallTarget";
    const string MeshAssetPath = Dir + "/CallTargetMeshes.asset";
    const string FrameMatPath = Dir + "/CallTargetFrame.mat";
    const string ArrowMatPath = Dir + "/CallTargetArrow.mat";
    const string ShaderName = "KemonoMahjong/CallTargetGlow";
    const string ScriptPath = "Assets/Mahjong/Sources/MahjongCallTargetMarker.cs";
    const string ProgramAssetDir = "Assets/Mahjong/scene_UdonProgramSources";
    const string ProgramAssetPath = ProgramAssetDir + "/MahjongCallTargetMarker Udon C# Program Asset.asset";
    const string SampleTilePath = "TilesSet/Tiles0/M1";

    // 枠：牌の縁からの内側のすき間と、外へにじむ幅（m）。
    const float FrameInnerGap = 0.0006f;
    const float FrameGlowWidth = 0.0065f;

    // 矢印：四角錐の底面の半幅と高さ（m）。先端（下）が牌を指す。
    const float ArrowHalfWidth = 0.0075f;
    const float ArrowHeight = 0.012f;

    // 雀魂の鳴き牌の緑。卓が緑の羅紗なので、枠は地に溶けないよう黄緑寄りに明るくしてある。
    static readonly Color FrameColor = new Color(0.82f, 1.0f, 0.62f, 1f);
    static readonly Color ArrowColor = new Color(0.50f, 1.0f, 0.68f, 1f);

    [MenuItem("Tools/Kemono Mahjong/Build Call Target Marker")]
    public static void Run()
    {
        EnsureFolder(Dir);

        if (EnsureProgramAsset(ScriptPath, ProgramAssetPath))
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[BuildCallTargetMarker] U#プログラムアセットを生成しました。コンパイル完了後、もう一度実行してください。");
            return;
        }

        var shader = Shader.Find(ShaderName);
        if (shader == null) { Debug.LogError("[BuildCallTargetMarker] シェーダーがありません: " + ShaderName); return; }

        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null) { Debug.LogError("[BuildCallTargetMarker] Prefab を読み込めません: " + PrefabPath); return; }

        try
        {
            var sample = root.transform.Find(SampleTilePath);
            var sampleMf = sample != null ? sample.GetComponent<MeshFilter>() : null;
            if (sampleMf == null || sampleMf.sharedMesh == null)
            {
                Debug.LogError("[BuildCallTargetMarker] 見本の牌が見つかりません: " + SampleTilePath);
                return;
            }

            // 牌の実寸（ローカル軸ごと）。厚みはローカルZ、寝かせると XY 面が水平になる。
            var b = sampleMf.sharedMesh.bounds;
            var s = sample.lossyScale;
            var half = new Vector2(b.extents.x * Mathf.Abs(s.x), b.extents.y * Mathf.Abs(s.y));
            var center = new Vector3(b.center.x * s.x, b.center.y * s.y, b.center.z * s.z);

            Mesh frameMesh, arrowMesh;
            EnsureMeshes(out frameMesh, out arrowMesh);
            FillFrameMesh(frameMesh, half, center);
            FillArrowMesh(arrowMesh);
            AssetDatabase.SaveAssets();

            var frameMat = EnsureMaterial(FrameMatPath, shader, FrameColor, 0.0f, 0.75f);
            var arrowMat = EnsureMaterial(ArrowMatPath, shader, ArrowColor, 0.004f, 1.0f);

            var markerGo = EnsureChild(root.transform, "CallTargetMarker");
            markerGo.SetActive(true);
            markerGo.transform.localPosition = Vector3.zero;
            markerGo.transform.localRotation = Quaternion.identity;
            markerGo.transform.localScale = Vector3.one;

            var marker = (MahjongCallTargetMarker)EnsureUSharpComponent(markerGo, typeof(MahjongCallTargetMarker));
            if (marker == null) { return; }

            var visual = EnsureChild(markerGo.transform, "Visual");
            visual.SetActive(false);

            var frame = EnsureRenderer(visual.transform, "Frame", frameMesh, frameMat);
            var arrow = EnsureRenderer(visual.transform, "Arrow", arrowMesh, arrowMat);

            marker.visualRoot = visual;
            marker.frame = frame.transform;
            marker.arrow = arrow.transform;
            // 牌の中心から上面までは厚みの半分。そこから約1.6cm 上に矢印の先端を浮かべる。
            marker.arrowHeight = b.extents.z * Mathf.Abs(s.z) + 0.016f;
            UdonSharpEditorUtility.CopyProxyToUdon(marker);

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log("[BuildCallTargetMarker] 完了: 牌 " + (half.x * 2f).ToString("F4") + "×" + (half.y * 2f).ToString("F4")
                + "m に合わせた枠と矢印を組み立て、Prefab を保存しました（シーンの全卓に反映）。");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Validate();
    }

    [MenuItem("Tools/Kemono Mahjong/Validate Call Target Marker")]
    public static void Validate()
    {
        int tables = 0, ok = 0;
        var sb = new System.Text.StringBuilder();
        foreach (var m in Object.FindObjectsOfType<Mahjong>(true))
        {
            ++tables;
            string why = Check(m.transform);
            if (why == null) { ++ok; } else { sb.Append(m.name).Append(": ").Append(why).Append("\n"); }
        }

        if (ok == tables) { Debug.Log("[ValidateCallTargetMarker] OK: 卓 " + tables + " 個すべてにマーカーあり・配線済み。"); }
        else { Debug.LogError("[ValidateCallTargetMarker] NG " + (tables - ok) + " / " + tables + "\n" + sb); }
    }

    static string Check(Transform table)
    {
        var t = table.Find("CallTargetMarker");
        if (t == null) { return "CallTargetMarker が無い"; }
        if (!t.gameObject.activeSelf) { return "CallTargetMarker が非アクティブ（追従の Update が走らない）"; }
        var mk = t.GetComponent<MahjongCallTargetMarker>();
        if (mk == null) { return "MahjongCallTargetMarker が無い"; }
        if (mk.visualRoot == null || mk.frame == null || mk.arrow == null) { return "visualRoot/frame/arrow の配線切れ"; }
        if (t.GetComponentsInChildren<Collider>(true).Length > 0) { return "当たり判定が付いている（牌のクリックや体の動きを邪魔する）"; }
        foreach (var r in t.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (r.sharedMaterial == null) { return r.name + " のマテリアルが空"; }
        }
        return null;
    }

    // ============================================================
    // メッシュ
    // ============================================================
    static void EnsureMeshes(out Mesh frame, out Mesh arrow)
    {
        frame = null;
        arrow = null;
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(MeshAssetPath))
        {
            var m = o as Mesh;
            if (m == null) { continue; }
            if (m.name == "CallTargetFrame") { frame = m; }
            if (m.name == "CallTargetArrow") { arrow = m; }
        }

        Object container = AssetDatabase.LoadMainAssetAtPath(MeshAssetPath);
        if (frame == null)
        {
            frame = new Mesh { name = "CallTargetFrame" };
            if (container == null) { AssetDatabase.CreateAsset(frame, MeshAssetPath); container = frame; }
            else { AssetDatabase.AddObjectToAsset(frame, container); }
        }
        if (arrow == null)
        {
            arrow = new Mesh { name = "CallTargetArrow" };
            AssetDatabase.AddObjectToAsset(arrow, container);
        }
    }

    // 牌の縁を囲む輪。内側の縁で濃さ1、外側で0（UV.x）。牌の厚みの中央の面（Z=中心）に置くので、
    // 牌に隠れる内側は見えず、外へにじむ光だけが牌のまわりに見える。
    static void FillFrameMesh(Mesh mesh, Vector2 half, Vector3 center)
    {
        float ix = half.x + FrameInnerGap, iy = half.y + FrameInnerGap;
        float ox = ix + FrameGlowWidth, oy = iy + FrameGlowWidth;

        var v = new Vector3[]
        {
            new Vector3(-ix, -iy, 0f), new Vector3(ix, -iy, 0f), new Vector3(ix, iy, 0f), new Vector3(-ix, iy, 0f),
            new Vector3(-ox, -oy, 0f), new Vector3(ox, -oy, 0f), new Vector3(ox, oy, 0f), new Vector3(-ox, oy, 0f),
        };
        for (int i = 0; i < v.Length; ++i) { v[i] += center; }

        var uv = new Vector2[]
        {
            new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f),
            new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f),
        };

        var tri = new int[]
        {
            0, 4, 5, 0, 5, 1,   // 下辺
            1, 5, 6, 1, 6, 2,   // 右辺
            2, 6, 7, 2, 7, 3,   // 上辺
            3, 7, 4, 3, 4, 0,   // 左辺
        };

        mesh.Clear();
        mesh.vertices = v;
        mesh.uv = uv;
        mesh.triangles = tri;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        EditorUtility.SetDirty(mesh);
    }

    // 下向きの四角錐。先端が原点（牌側）、底面が +Y。
    static void FillArrowMesh(Mesh mesh)
    {
        float w = ArrowHalfWidth, h = ArrowHeight;
        var v = new Vector3[]
        {
            new Vector3(0f, 0f, 0f),
            new Vector3(-w, h, -w), new Vector3(w, h, -w), new Vector3(w, h, w), new Vector3(-w, h, w),
        };
        var uv = new Vector2[] { Vector2.right, Vector2.right, Vector2.right, Vector2.right, Vector2.right };
        var tri = new int[]
        {
            0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 1,   // 側面
            1, 3, 2, 1, 4, 3,                     // 底面
        };

        mesh.Clear();
        mesh.vertices = v;
        mesh.uv = uv;
        mesh.triangles = tri;
        mesh.RecalculateNormals();
        // 揺れ（_BobAmp）で上へずれても視錐台カリングで消えないよう、少し余裕を持たせる。
        mesh.bounds = new Bounds(new Vector3(0f, h * 0.5f + 0.003f, 0f), new Vector3(w * 2f, h + 0.008f, w * 2f));
        EditorUtility.SetDirty(mesh);
    }

    // ============================================================
    // マテリアル・部品
    // ============================================================
    static Material EnsureMaterial(string path, Shader shader, Color color, float bob, float falloff)
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.shader = shader;
        mat.SetColor("_Color", color);
        mat.SetFloat("_MinAlpha", 0.25f);
        mat.SetFloat("_MaxAlpha", 1.0f);
        mat.SetFloat("_Speed", 1.25f);
        mat.SetFloat("_Falloff", falloff);
        mat.SetFloat("_BobAmp", bob);
        mat.enableInstancing = false;
        EditorUtility.SetDirty(mat);
        AssetDatabase.SaveAssets();
        return mat;
    }

    static GameObject EnsureRenderer(Transform parent, string name, Mesh mesh, Material mat)
    {
        var go = EnsureChild(parent, name);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;

        var mf = go.GetComponent<MeshFilter>();
        if (mf == null) { mf = go.AddComponent<MeshFilter>(); }
        mf.sharedMesh = mesh;

        var mr = go.GetComponent<MeshRenderer>();
        if (mr == null) { mr = go.AddComponent<MeshRenderer>(); }
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
        mr.lightProbeUsage = LightProbeUsage.Off;
        mr.reflectionProbeUsage = ReflectionProbeUsage.Off;

        foreach (var col in go.GetComponents<Collider>()) { Object.DestroyImmediate(col); }
        return go;
    }

    static GameObject EnsureChild(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null) { return t.gameObject; }
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go;
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) { return; }
        var parts = path.Split('/');
        var current = parts[0];
        for (int i = 1; i < parts.Length; ++i)
        {
            var next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next)) { AssetDatabase.CreateFolder(current, parts[i]); }
            current = next;
        }
    }

    static bool EnsureProgramAsset(string scriptPath, string assetPath)
    {
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
        if (script == null)
        {
            Debug.LogError("[BuildCallTargetMarker] スクリプトが見つかりません: " + scriptPath);
            return false;
        }
        var type = script.GetClass();
        if (type != null && UdonSharpProgramAsset.GetProgramAssetForClass(type) != null) { return false; }
        if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(assetPath) != null) { return false; }

        var programAsset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
        programAsset.sourceCsScript = script;
        AssetDatabase.CreateAsset(programAsset, assetPath);
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
            Debug.LogError("[BuildCallTargetMarker] " + type.Name + " の追加に失敗（プログラムアセット未コンパイル？）。");
            Object.DestroyImmediate(added);
            return null;
        }
        return added;
    }
}
#endif
