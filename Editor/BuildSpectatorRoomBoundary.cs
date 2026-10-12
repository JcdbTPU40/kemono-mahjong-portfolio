// 観戦ルーム（防音エリア）の境界を床に示す帯のビルダー。
//
// ■ 作るもの
//   1. 防音マーク（スピーカー＋×を丸で囲む）と縁の線を焼いたテクスチャ … Assets/Mahjong/Textures/UI/SoundproofBoundary.png
//   2. マテリアル（KemonoMahjong/SoundproofBoundary）… Assets/Mahjong/Materials/UI/SoundproofBoundary.mat
//   3. 帯のメッシュ … Assets/Mahjong/Meshes/SoundproofBoundary.mesh
//   4. シーンルートの SpectatorRoomBoundary（MeshFilter+MeshRenderer）
//
// ■ 範囲は TableVoiceRouter.roomArea（SpectatorRoomArea）の箱から作る
//   声の判定と同じ箱の縁の内側に、幅 StripWidth の帯を床の高さで一周させる。
//   箱を動かしたら作り直す（SpectatorRoomAreaHandles は面をドラッグするたびに自動で呼ぶ。
//   移動/拡大ツールで動かしたときは Build Spectator Room Boundary を実行する）。
//
// ■ 文字は入れない
//   マークだけにして日英の出し分けを不要にした（U# も同期も持たない＝CPU ゼロ）。
//
// ■ テクスチャは POT（512x128）
//   NPOT は圧縮指定が黙殺されて RGB24 に落ちる。マーク1個＝この1枚で、縦横比 4:1 を
//   帯の「マーク間隔 : 帯の幅」に合わせてある（周長がマーク間隔の整数倍になるよう間隔を微調整する）。
using System.Collections.Generic;
using System.IO;
using SuzuFactory.AozoraMahjong;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class BuildSpectatorRoomBoundary
{
    const string ObjectName = "SpectatorRoomBoundary";
    const string CarpetName = "Carpet_01";
    const string ShaderName = "KemonoMahjong/SoundproofBoundary";
    const string MeshDir = "Assets/Mahjong/Meshes";
    const string MeshPath = MeshDir + "/SoundproofBoundary.mesh";
    const string TexDir = "Assets/Mahjong/Textures/UI";
    const string TexPath = TexDir + "/SoundproofBoundary.png";
    const string MatDir = "Assets/Mahjong/Materials/UI";
    const string MatPath = MatDir + "/SoundproofBoundary.mat";

    const float StripWidth = 0.32f;          // 帯の幅（m）
    const float FloorLift = 0.008f;          // 床からの浮かせ（カーペットの凹凸＋Offset と合わせて z ファイト防止）
    const int TexW = 512;
    const int TexH = 128;
    const float Aspect = (float)TexW / TexH; // マーク間隔＝帯の幅×これ

    [MenuItem("Tools/Kemono Mahjong/Build Spectator Room Boundary")]
    public static void Run()
    {
        var router = Object.FindObjectOfType<TableVoiceRouter>(true);
        if (router == null || router.roomArea == null)
        {
            Debug.LogError("[BuildSpectatorRoomBoundary] TableVoiceRouter.roomArea がありません。先に Build Table Voice を実行してください。");
            return;
        }
        var shader = Shader.Find(ShaderName);
        if (shader == null) { Debug.LogError("[BuildSpectatorRoomBoundary] シェーダーが見つかりません: " + ShaderName); return; }

        var tex = EnsureTexture();
        var mat = EnsureMaterial(shader, tex);
        var go = EnsureObject(mat);
        int marks = RebuildMesh(router.roomArea, go);
        // 防音 OFF の間は Router が帯を隠す（スタッフパネルの切替）
        if (router.boundary != go)
        {
            Undo.RecordObject(router, "Build Spectator Room Boundary");
            router.boundary = go;
            EditorUtility.SetDirty(router);
        }
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(go.scene);
        Debug.Log("[BuildSpectatorRoomBoundary] 完了: 防音マーク " + marks + " 個で観戦ルームの縁を一周（幅 " + StripWidth + "m）。");
    }

    // ------------------------------------------------------------ メッシュ
    // 箱の縁から内側へ StripWidth の帯を4辺ぶん。角は台形で継ぐ。
    // U は帯の中心線に沿った距離（マーク間隔で割る）、V は 0＝外縁 / 1＝内縁
    // （外から歩いてきた人にマークの上が奥＝正立して見える向き）。
    // 戻り値はマークの個数。箱が無い・小さすぎるときは 0。
    public static int RebuildMesh(Transform area, GameObject go = null)
    {
        if (area == null) { return 0; }
        if (go == null)
        {
            go = GameObject.Find(ObjectName);
            if (go == null) { return 0; }
        }
        var mf = go.GetComponent<MeshFilter>();
        if (mf == null) { return 0; }

        Vector3 c = area.position;
        Vector3 s = area.lossyScale;
        float hx = Mathf.Abs(s.x) * 0.5f;
        float hz = Mathf.Abs(s.z) * 0.5f;
        float w = Mathf.Min(StripWidth, Mathf.Min(hx, hz) * 0.5f);
        if (w <= 0.01f) { return 0; }
        var rot = Quaternion.Euler(0f, area.eulerAngles.y, 0f);
        float y = MeasureFloorY(area) + FloorLift;

        // 角（外）と角（内）。反時計回り（上から見て）
        Vector2[] outer = { new Vector2(-hx, -hz), new Vector2(hx, -hz), new Vector2(hx, hz), new Vector2(-hx, hz) };
        Vector2[] inner = { new Vector2(-hx + w, -hz + w), new Vector2(hx - w, -hz + w), new Vector2(hx - w, hz - w), new Vector2(-hx + w, hz - w) };

        // 中心線の周長から、マーク間隔が整数個で割り切れるよう調整
        float lenX = 2f * hx - w, lenZ = 2f * hz - w;
        float perimeter = 2f * (lenX + lenZ);
        int marks = Mathf.Max(4, Mathf.RoundToInt(perimeter / (w * Aspect)));
        float uPerMeter = marks / perimeter;

        // 1辺 = 長方形の本体＋両端の三角（角の正方形を隣の辺と対角線で分け合う）。
        // 辺を台形1枚にすると2つの三角形で UV の伸び方が変わり、マークが対角線で折れて歪む。
        // 本体は外縁と内縁の長さが同じ長方形にして、マークを正しい形のまま流す。
        var verts = new List<Vector3>(40);
        var uvs = new List<Vector2>(40);
        var tris = new List<int>(48);
        float uSide = 0f;
        for (int i = 0; i < 4; ++i)
        {
            int j = (i + 1) % 4;
            Vector2 dir = (outer[j] - outer[i]).normalized;
            float body = (outer[j] - outer[i]).magnitude - 2f * w;   // 本体の長さ
            float ub = uSide + w * 0.5f * uPerMeter;                  // 本体の始まり（中心線上の位置）
            float ue = ub + body * uPerMeter;
            Vector2 oa = outer[i] + dir * w, ob = outer[j] - dir * w;
            int b = verts.Count;
            // 本体
            verts.Add(ToWorld(oa, c, rot, y)); uvs.Add(new Vector2(ub, 0f));
            verts.Add(ToWorld(ob, c, rot, y)); uvs.Add(new Vector2(ue, 0f));
            verts.Add(ToWorld(inner[j], c, rot, y)); uvs.Add(new Vector2(ue, 1f));
            verts.Add(ToWorld(inner[i], c, rot, y)); uvs.Add(new Vector2(ub, 1f));
            tris.Add(b); tris.Add(b + 2); tris.Add(b + 1);
            tris.Add(b); tris.Add(b + 3); tris.Add(b + 2);
            // 始まりの角（外の角・本体の外・本体の内）
            int k = verts.Count;
            verts.Add(ToWorld(outer[i], c, rot, y)); uvs.Add(new Vector2(ub - w * uPerMeter, 0f));
            verts.Add(ToWorld(oa, c, rot, y)); uvs.Add(new Vector2(ub, 0f));
            verts.Add(ToWorld(inner[i], c, rot, y)); uvs.Add(new Vector2(ub, 1f));
            tris.Add(k); tris.Add(k + 2); tris.Add(k + 1);
            // 終わりの角（本体の外・外の角・本体の内）
            k = verts.Count;
            verts.Add(ToWorld(ob, c, rot, y)); uvs.Add(new Vector2(ue, 0f));
            verts.Add(ToWorld(outer[j], c, rot, y)); uvs.Add(new Vector2(ue + w * uPerMeter, 0f));
            verts.Add(ToWorld(inner[j], c, rot, y)); uvs.Add(new Vector2(ue, 1f));
            tris.Add(k); tris.Add(k + 2); tris.Add(k + 1);
            uSide += ((i % 2 == 0) ? lenX : lenZ) * uPerMeter;
        }

        Directory.CreateDirectory(MeshDir);
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
        if (mesh == null)
        {
            mesh = new Mesh();
            AssetDatabase.CreateAsset(mesh, MeshPath);
        }
        mesh.Clear();
        mesh.name = "SoundproofBoundary";
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        EditorUtility.SetDirty(mesh);
        if (mf.sharedMesh != mesh) { mf.sharedMesh = mesh; EditorUtility.SetDirty(mf); }
        return marks;
    }

    static Vector3 ToWorld(Vector2 p, Vector3 c, Quaternion rot, float y)
    {
        Vector3 v = c + rot * new Vector3(p.x, 0f, p.y);
        v.y = y;
        return v;
    }

    // 床の高さ。Carpet_01 があればその上面、無ければ箱の中心から真下へレイを撃つ。どちらも無理なら箱の底。
    static float MeasureFloorY(Transform area)
    {
        foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
        {
            if (t.name != CarpetName || !t.gameObject.scene.IsValid()) { continue; }
            var r = t.GetComponent<Renderer>();
            if (r != null) { return r.bounds.max.y; }
        }
        Vector3 top = area.position + Vector3.up * Mathf.Abs(area.lossyScale.y) * 0.5f;
        if (Physics.Raycast(top, Vector3.down, out RaycastHit hit, Mathf.Abs(area.lossyScale.y) + 1f, ~0, QueryTriggerInteraction.Ignore))
        {
            return hit.point.y;
        }
        return area.position.y - Mathf.Abs(area.lossyScale.y) * 0.5f;
    }

    // ------------------------------------------------------------ シーンのオブジェクト
    static GameObject EnsureObject(Material mat)
    {
        var go = GameObject.Find(ObjectName);
        if (go == null)
        {
            go = new GameObject(ObjectName, typeof(MeshFilter), typeof(MeshRenderer));
            Undo.RegisterCreatedObjectUndo(go, "Build Spectator Room Boundary");
        }
        // 頂点はワールド座標で持つので、変換は単位のまま
        go.transform.SetParent(null, false);
        go.transform.position = Vector3.zero;
        go.transform.rotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;
        // 静的バッチに入れない（UV を時間で送る板なので結合の得が無い）
        GameObjectUtility.SetStaticEditorFlags(go, 0);

        var mr = go.GetComponent<MeshRenderer>();
        if (mr == null) { mr = Undo.AddComponent<MeshRenderer>(go); }
        if (go.GetComponent<MeshFilter>() == null) { Undo.AddComponent<MeshFilter>(go); }
        mr.sharedMaterial = mat;
        // ライティングを見ない板なので影もプローブも切る（Quest 対策）
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        mr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        EditorUtility.SetDirty(go);
        return go;
    }

    // ------------------------------------------------------------ マテリアル
    static Material EnsureMaterial(Shader shader, Texture2D tex)
    {
        Directory.CreateDirectory(MatDir);
        var mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
        if (mat == null)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, MatPath);
        }
        mat.shader = shader;
        mat.SetTexture("_MainTex", tex);
        mat.SetColor("_Color", new Color(0.62f, 0.93f, 1f, 1f));
        // 柄の濃いカーペットの上ではアルファ0.6だとほぼ埋もれる（実測）。0.85まで上げる
        mat.SetColor("_BgColor", new Color(0.03f, 0.08f, 0.14f, 0.85f));
        mat.SetFloat("_ScrollSpeed", 0.35f);
        mat.SetFloat("_Pulse", 0.25f);
        mat.SetFloat("_PulseSpeed", 2.0f);
        mat.renderQueue = 3000;
        mat.enableInstancing = false;   // Standard Lite の件以来、インスタンシングは使わない
        EditorUtility.SetDirty(mat);
        return mat;
    }

    // ------------------------------------------------------------ テクスチャ
    // 512x128 の1枚にマーク1個。白で描き、色はマテリアルの _Color で付ける。
    // 座標は 0..1（v 方向は帯の幅）。4x4 のスーパーサンプリングで縁を滑らかにする。
    static Texture2D EnsureTexture()
    {
        Directory.CreateDirectory(TexDir);
        var tex = new Texture2D(TexW, TexH, TextureFormat.RGBA32, false);
        var px = new Color32[TexW * TexH];
        const int SS = 4;
        for (int py = 0; py < TexH; ++py)
        {
            for (int pxX = 0; pxX < TexW; ++pxX)
            {
                float cov = 0f;
                for (int sy = 0; sy < SS; ++sy)
                {
                    for (int sx = 0; sx < SS; ++sx)
                    {
                        float u = (pxX + (sx + 0.5f) / SS) / TexW;
                        float v = (py + (sy + 0.5f) / SS) / TexH;
                        if (Inside(u, v)) { cov += 1f; }
                    }
                }
                byte a = (byte)Mathf.RoundToInt(255f * cov / (SS * SS));
                px[py * TexW + pxX] = new Color32(255, 255, 255, a);
            }
        }
        tex.SetPixels32(px);
        tex.Apply();
        File.WriteAllBytes(TexPath, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);

        AssetDatabase.ImportAsset(TexPath, ImportAssetOptions.ForceUpdate);
        var importer = AssetImporter.GetAtPath(TexPath) as TextureImporter;
        if (importer != null)
        {
            importer.textureType = TextureImporterType.Default;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = true;
            importer.wrapModeU = TextureWrapMode.Repeat;   // 帯に沿って流す
            importer.wrapModeV = TextureWrapMode.Clamp;
            importer.anisoLevel = 4;                        // 床を斜めから見るので
            importer.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(TexPath);
    }

    // 1点がインクの上か。u: 0..1 がマーク間隔、v: 0..1 が帯の幅。
    static bool Inside(float u, float v)
    {
        // 縁の線（外縁と内縁に沿って途切れず続く）
        if (v < 0.06f || v > 0.94f) { return true; }

        // マーク：横幅 1/Aspect の正方形に描く（u の中央）
        float x = (u - 0.5f) * Aspect + 0.5f;   // 正方形内の 0..1
        float y = v;
        if (x < 0f || x > 1f) { return false; }
        float dx = x - 0.5f, dy = y - 0.5f;
        float r = Mathf.Sqrt(dx * dx + dy * dy);

        // 丸の枠
        if (r > 0.33f && r < 0.40f) { return true; }

        // スピーカー（左寄り）：箱＋ラッパ
        if (x > 0.24f && x < 0.33f && y > 0.43f && y < 0.57f) { return true; }
        if (x >= 0.33f && x < 0.47f)
        {
            float t = (x - 0.33f) / 0.14f;          // 0..1 で開く
            float half = Mathf.Lerp(0.07f, 0.17f, t);
            if (Mathf.Abs(y - 0.5f) < half) { return true; }
        }

        // ×（右寄り）
        float cx = 0.62f, cy = 0.5f, k = 0.08f, th = 0.036f;
        if (x > cx - k - th && x < cx + k + th && y > cy - k - th && y < cy + k + th)
        {
            float ax = x - cx, ay = y - cy;
            if (Mathf.Abs(ax - ay) < th * 1.414f || Mathf.Abs(ax + ay) < th * 1.414f) { return true; }
        }
        return false;
    }

    [MenuItem("Tools/Kemono Mahjong/Validate Spectator Room Boundary")]
    public static void Validate()
    {
        var router = Object.FindObjectOfType<TableVoiceRouter>(true);
        var go = GameObject.Find(ObjectName);
        if (go == null) { Debug.LogError("[ValidateSpectatorRoomBoundary] " + ObjectName + " がありません。Build Spectator Room Boundary を実行してください。"); return; }
        var mf = go.GetComponent<MeshFilter>();
        var mr = go.GetComponent<MeshRenderer>();
        if (mf == null || mf.sharedMesh == null || mr == null || mr.sharedMaterial == null)
        {
            Debug.LogError("[ValidateSpectatorRoomBoundary] 描画が欠けています。Build し直してください。", go);
            return;
        }
        if (router != null && router.boundary != go)
        {
            Debug.LogError("[ValidateSpectatorRoomBoundary] TableVoiceRouter.boundary が帯を指していません（防音 OFF で帯が隠れない）。Build Spectator Room Boundary を実行してください。", go);
            return;
        }
        if (router != null && router.roomArea != null)
        {
            // 帯の外周と箱の XZ がずれていないか（箱を動かして作り直し忘れ）
            var b = mf.sharedMesh.bounds;
            Vector3 s = router.roomArea.lossyScale;
            float ex = Mathf.Abs(b.size.x - Mathf.Abs(s.x)), ez = Mathf.Abs(b.size.z - Mathf.Abs(s.z));
            float ec = Vector2.Distance(new Vector2(b.center.x, b.center.z), new Vector2(router.roomArea.position.x, router.roomArea.position.z));
            if (Mathf.Abs(router.roomArea.eulerAngles.y) < 0.01f && (ex > 0.02f || ez > 0.02f || ec > 0.02f))
            {
                Debug.LogError("[ValidateSpectatorRoomBoundary] 帯が観戦ルームの箱とずれています（箱を動かした後に作り直していない）。Build Spectator Room Boundary を実行してください。", go);
                return;
            }
        }
        Debug.Log("[ValidateSpectatorRoomBoundary] OK");
    }
}
