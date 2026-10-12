// 卓を囲む案内リング（「メインメニューで操作してください」が輪っかに流れる）のビルダー。
//
// ■ 作るもの
//   1. リングのメッシュ（円筒の側面だけ・両面）… Assets/Mahjong/Meshes/TableNoticeRing.mesh
//   2. 文言を焼いたテクスチャ（日本語／英語の2枚）… Assets/Mahjong/Textures/UI/TableNotice/
//   3. マテリアル2枚（KemonoMahjong/TableNoticeRing）
//   4. 各卓に NoticeRing（MeshFilter+MeshRenderer）と MahjongTableNoticeRing を配置・配線
//
// ■ なぜテクスチャに焼くのか
//   卓は8つある。TMP を Update で流すと常時アクティブな卓の数だけ CPU を食う。
//   文字を焼いてしまえば、流すのはシェーダーが _Time.y から自走する＝毎フレーム CPU ゼロ、
//   ドローコールも1卓1枚で済む。日英の切り替えはマテリアルの差し替えだけ。
//
// ■ テクスチャの寸法
//   2048x128（どちらも4の倍数・POT）。NPOT にすると圧縮指定が黙殺されて
//   RGB24 に落ちるので、ここは必ず POT のままにすること。
using System.Collections.Generic;
using System.IO;
using SuzuFactory.AozoraMahjong;
using TMPro;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using VRC.Udon;

public static class BuildTableNoticeRing
{
    const string MeshDir = "Assets/Mahjong/Meshes";
    const string MeshPath = MeshDir + "/TableNoticeRing.mesh";
    const string TexDir = "Assets/Mahjong/Textures/UI/TableNotice";
    const string MatDir = "Assets/Mahjong/Materials/UI";
    const string ShaderName = "KemonoMahjong/TableNoticeRing";

    const string RingObjectName = "NoticeRing";
    // 本体（MahjongTableNoticeRing）を載せる子。卓ルートには置かない（理由は BuildForTable 参照）。
    const string CtrlObjectName = "NoticeRingCtrl";
    const string ScriptPath = "Assets/Mahjong/Sources/MahjongTableNoticeRing.cs";
    const string ProgramAssetPath =
        "Assets/Mahjong/scene_UdonProgramSources/MahjongTableNoticeRing Udon C# Program Asset.asset";

    // --- リングの寸法（卓のローカル座標） ---
    // 半径は卓の縁より少し外、高さは卓面より上。牌や河と重ならない位置。
    const float Radius = 0.78f;
    const float Height = 0.13f;
    const float CenterY = 0.30f;
    const int Segments = 64;
    // テクスチャを何周ぶん貼るか。1＝1周に文言がちょうど1回。
    // 2周にすると1周あたり 2.45m しか使えず、全角30文字が豆粒になる。
    const float Tiling = 1.0f;

    // 4096x128。日本語の文言は全角30文字あり、2048 では font 82 でも収まらず両端が切れる
    // （焼いた PNG のインクが端まで届いていたので発覚）。幅を倍にして字を大きく保つ。
    // NPOT にすると圧縮指定が黙殺されて RGB24 に落ちるので、必ず 4 の倍数かつ POT にすること。
    const int TexWidth = 4096;
    const int TexHeight = 128;
    // 左右に空ける余白（割合）。リングは端と端が繋がるので、
    // ここを 0 にすると文言の末尾と先頭がくっついて読めなくなる。
    const float TexMarginRatio = 0.06f;
    // 128px の枠に収めるための上限。これ以上大きくすると上下がはみ出す。
    const float MaxFontSize = 96f;

    static readonly string JapaneseText = "この卓を使うには　メインメニューで操作してください　　◆　　";
    static readonly string EnglishText = "To use this table, start from the Main Menu   *   ";
    static readonly string HangulText = "이 탁을 사용하려면　메인 메뉴에서 조작해 주세요　　◆　　";
    static readonly string SimplifiedText = "要使用这张牌桌　请在主菜单中操作　　◆　　";
    static readonly string TraditionalText = "要使用這張牌桌　請在主選單中操作　　◆　　";

    [MenuItem("Tools/Kemono Mahjong/Build Table Notice Rings")]
    public static void Run()
    {
        var shader = Shader.Find(ShaderName);
        if (shader == null)
        {
            Debug.LogError("[BuildTableNoticeRing] シェーダーが見つかりません: " + ShaderName);
            return;
        }

        // 新しい U# スクリプトはプログラムアセットを先に作る。無いまま AddComponent すると
        // NRE で落ち、卓には壊れた UdonBehaviour だけが残る。
        if (EnsureProgramAsset())
        {
            Debug.Log("[BuildTableNoticeRing] U#プログラムアセットを生成しました。"
                + "コンパイルが終わってからもう一度実行してください。");
            return;
        }

        var mesh = EnsureMesh();
        var matJa = EnsureMaterial(shader, "TableNoticeRing_JA", EnsureTexture(JapaneseText, "TableNotice_JA"));
        var matEn = EnsureMaterial(shader, "TableNoticeRing_EN", EnsureTexture(EnglishText, "TableNotice_EN"));
        var matKo = EnsureMaterial(shader, "TableNoticeRing_KO", EnsureTexture(HangulText, "TableNotice_KO"));
        var matZhs = EnsureMaterial(shader, "TableNoticeRing_ZHS", EnsureTexture(SimplifiedText, "TableNotice_ZHS"));
        var matZht = EnsureMaterial(shader, "TableNoticeRing_ZHT", EnsureTexture(TraditionalText, "TableNotice_ZHT"));

        var matchmaking = Object.FindObjectOfType<MatchmakingManager>();
        if (matchmaking == null)
        {
            Debug.LogError("[BuildTableNoticeRing] MatchmakingManager がシーンにありません。");
            return;
        }

        var tables = Object.FindObjectsOfType<Mahjong>();
        if (tables == null || tables.Length == 0)
        {
            Debug.LogError("[BuildTableNoticeRing] 卓がシーンにありません。");
            return;
        }

        int built = 0;
        foreach (var table in tables)
        {
            if (table == null) { continue; }
            BuildForTable(table, matchmaking, mesh, matJa, matEn, matKo, matZhs, matZht);
            ++built;
        }

        AssetDatabase.SaveAssets();
        Debug.Log("[BuildTableNoticeRing] 完了: 卓 " + built + " 台へ案内リングを配置しました。");
    }

    // ------------------------------------------------------------ メッシュ
    //
    // 円筒の側面だけを作る。Unity の Cylinder プリミティブは蓋が付いていて、
    // 平たくすると蓋が主役になってしまう（テクスチャが円盤に伸びる）ので使えない。
    // 裏からも読めるよう、同じ面を法線反転で2枚ぶん持つ（Cull Off だけだと
    // ライティングは無いので実害は無いが、UV の向きを内外で揃えたい）。
    static Mesh EnsureMesh()
    {
        Directory.CreateDirectory(MeshDir);
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
        if (mesh == null)
        {
            mesh = new Mesh();
            AssetDatabase.CreateAsset(mesh, MeshPath);
        }
        mesh.Clear();
        mesh.name = "TableNoticeRing";

        int ringVerts = Segments + 1; // 継ぎ目の UV を 0 と 1 で分けるため1つ多い
        var verts = new List<Vector3>(ringVerts * 2);
        var uvs = new List<Vector2>(ringVerts * 2);
        for (int i = 0; i <= Segments; ++i)
        {
            float f = (float)i / Segments;
            float a = f * Mathf.PI * 2f;
            float x = Mathf.Sin(a) * Radius;
            float z = Mathf.Cos(a) * Radius;
            verts.Add(new Vector3(x, -Height * 0.5f, z));
            verts.Add(new Vector3(x, Height * 0.5f, z));
            uvs.Add(new Vector2(f * Tiling, 0f));
            uvs.Add(new Vector2(f * Tiling, 1f));
        }

        // 面は片側だけ持つ。両面（巻き順を逆にした複製）を持たせると、
        // 手前側と奥側が同時に描かれて、手前に鏡像が重なる＝卓の上が読めなくなる。
        // シェーダー側の Cull Front で「見る人から遠い側だけ」を残し、
        // 文字はいつも卓の向こう側に、正しい向きで1周ぶんだけ見えるようにする。
        var tris = new List<int>(Segments * 6);
        for (int i = 0; i < Segments; ++i)
        {
            int b = i * 2;
            tris.Add(b); tris.Add(b + 1); tris.Add(b + 3);
            tris.Add(b); tris.Add(b + 3); tris.Add(b + 2);
        }

        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
        EditorUtility.SetDirty(mesh);
        return mesh;
    }

    // ------------------------------------------------------------ テクスチャ
    //
    // TMP を一時カメラ＋RenderTexture で撮って PNG に焼く。
    // 実行時に TMP を持たせない（＝Canvas も Update も増やさない）のが狙い。
    static Texture2D EnsureTexture(string text, string fileName)
    {
        Directory.CreateDirectory(TexDir);
        string path = TexDir + "/" + fileName + ".png";

        var root = new GameObject("__NoticeBake", typeof(Canvas));
        var canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var canvasRt = (RectTransform)root.transform;
        canvasRt.sizeDelta = new Vector2(TexWidth, TexHeight);
        canvasRt.position = new Vector3(10000f, 10000f, 10000f);

        var textGo = new GameObject("Text", typeof(RectTransform));
        textGo.transform.SetParent(root.transform, false);
        var textRt = (RectTransform)textGo.transform;
        textRt.anchorMin = Vector2.zero;
        textRt.anchorMax = Vector2.one;
        textRt.offsetMin = Vector2.zero;
        textRt.offsetMax = Vector2.zero;

        var tmp = textGo.AddComponent<TextMeshProUGUI>();
        tmp.font = LoadJapaneseFont();
        tmp.text = text;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        tmp.color = Color.white;

        // 枠に収まる最大サイズを実測して決める。固定値だと文言を変えたときに黙って切れる。
        tmp.fontSize = MaxFontSize;
        tmp.ForceMeshUpdate();
        float usable = TexWidth * (1f - TexMarginRatio * 2f);
        float needed = tmp.preferredWidth;
        if (needed > usable && needed > 1f)
        {
            tmp.fontSize = Mathf.Floor(MaxFontSize * usable / needed);
        }
        tmp.ForceMeshUpdate();
        Debug.Log("[BuildTableNoticeRing] " + fileName + ": fontSize=" + tmp.fontSize
            + " width=" + tmp.preferredWidth.ToString("F0") + " / usable=" + usable.ToString("F0"));

        var camGo = new GameObject("__NoticeCam", typeof(Camera));
        var cam = camGo.GetComponent<Camera>();
        cam.orthographic = true;
        cam.orthographicSize = TexHeight * 0.5f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0f, 0f, 0f, 0f); // 背景は透明のまま抜く
        cam.transform.position = canvasRt.position + new Vector3(0f, 0f, -10f);
        cam.transform.rotation = Quaternion.identity;
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 100f;
        canvas.worldCamera = cam;

        var rt = new RenderTexture(TexWidth, TexHeight, 0, RenderTextureFormat.ARGB32);
        cam.targetTexture = rt;
        cam.Render();

        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(TexWidth, TexHeight, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, TexWidth, TexHeight), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;

        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        cam.targetTexture = null;
        Object.DestroyImmediate(rt);
        Object.DestroyImmediate(camGo);
        Object.DestroyImmediate(root);

        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer != null)
        {
            importer.textureType = TextureImporterType.Default;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = true;
            // 横方向はタイリングして流すので Repeat 必須。縦は端をフェードするので Clamp。
            importer.wrapModeU = TextureWrapMode.Repeat;
            importer.wrapModeV = TextureWrapMode.Clamp;
            importer.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    static TMP_FontAsset LoadJapaneseFont()
    {
        // 日本語が豆腐にならないよう、既定の LiberationSans ではなく本文フォントを使う。
        var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(
            "Assets/Mahjong/Fonts/NotoSansJP-Bold SDF.asset");
        if (font == null)
        {
            Debug.LogWarning("[BuildTableNoticeRing] 日本語フォントが見つかりません。日本語が豆腐になります。");
        }
        return font;
    }

    // ------------------------------------------------------------ マテリアル
    static Material EnsureMaterial(Shader shader, string name, Texture2D tex)
    {
        Directory.CreateDirectory(MatDir);
        string path = MatDir + "/" + name + ".mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.shader = shader;
        mat.SetTexture("_MainTex", tex);
        mat.SetColor("_Color", new Color(1f, 0.93f, 0.72f, 0.92f)); // 象牙寄りの淡い金
        mat.SetFloat("_ScrollSpeed", 0.055f);
        mat.SetFloat("_EdgeFade", 0.18f);
        mat.renderQueue = 3000;
        EditorUtility.SetDirty(mat);
        return mat;
    }

    // ------------------------------------------------------------ U# プログラムアセット
    static bool EnsureProgramAsset()
    {
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(ScriptPath);
        if (script == null)
        {
            Debug.LogError("[BuildTableNoticeRing] スクリプトが見つかりません: " + ScriptPath);
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

    // 前回の失敗で残った「プログラムを持たない UdonBehaviour」を掃除する。
    // 放っておくと Build のたびに増え、ビルド時に原因不明の警告になる。
    static void CleanupBrokenBehaviours(GameObject go)
    {
        foreach (var ub in go.GetComponents<UdonBehaviour>())
        {
            if (ub != null && ub.programSource == null) { Object.DestroyImmediate(ub, true); }
        }
    }

    // ------------------------------------------------------------ 卓への配置
    static void BuildForTable(Mahjong table, MatchmakingManager matchmaking,
        Mesh mesh, Material matJa, Material matEn, Material matKo, Material matZhs, Material matZht)
    {
        var ringTf = table.transform.Find(RingObjectName);
        GameObject ring;
        if (ringTf == null)
        {
            ring = new GameObject(RingObjectName, typeof(MeshFilter), typeof(MeshRenderer));
            Undo.RegisterCreatedObjectUndo(ring, "Build Table Notice Rings");
            ring.transform.SetParent(table.transform, false);
        }
        else
        {
            ring = ringTf.gameObject;
        }

        ring.transform.localPosition = new Vector3(0f, CenterY, 0f);
        ring.transform.localRotation = Quaternion.identity;
        ring.transform.localScale = Vector3.one;

        var mf = ring.GetComponent<MeshFilter>();
        if (mf == null) { mf = Undo.AddComponent<MeshFilter>(ring); }
        mf.sharedMesh = mesh;

        var mr = ring.GetComponent<MeshRenderer>();
        if (mr == null) { mr = Undo.AddComponent<MeshRenderer>(ring); }
        mr.sharedMaterial = matJa;
        // ライティングは一切見ない板なので、影もプローブも切る（Quest 対策）。
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        mr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;

        // 制御コンポーネントは専用の子（NoticeRingCtrl）へ付ける。
        //   - リング自体には付けられない。リングは SetActive で寝るため自分で自分を起こせない。
        //   - 卓のルートにも付けられない。ルートの Mahjong は Manual 同期で、
        //     BehaviourSyncMode.None の本体を同居させると UdonSharp が「同期方式の混在＝
        //     最後のコンポーネントの方式に全部揃う」エラーを出す（免除されるのは NoVariableSync だけ）。
        //     Mahjong の Manual が崩れると卓が同期しなくなる。
        //   - 子は常時アクティブなので、卓が起きていれば本体も動く（従来と同じ条件）。
        var ctrlTf = table.transform.Find(CtrlObjectName);
        GameObject ctrl;
        if (ctrlTf == null)
        {
            ctrl = new GameObject(CtrlObjectName);
            Undo.RegisterCreatedObjectUndo(ctrl, "Build Table Notice Rings");
            ctrl.transform.SetParent(table.transform, false);
        }
        else
        {
            ctrl = ctrlTf.gameObject;
        }
        ctrl.transform.localPosition = Vector3.zero;
        ctrl.transform.localRotation = Quaternion.identity;
        ctrl.transform.localScale = Vector3.one;
        ctrl.SetActive(true);

        // 旧版が卓ルートへ直付けしていた本体を剥がす（同期方式の混在を解消）。
        var legacy = table.GetComponent<MahjongTableNoticeRing>();
        if (legacy != null)
        {
            var legacyUdon = UdonSharpEditorUtility.GetBackingUdonBehaviour(legacy);
            Object.DestroyImmediate(legacy, true);
            if (legacyUdon != null) { Object.DestroyImmediate(legacyUdon, true); }
            Debug.Log("[BuildTableNoticeRing] " + table.name
                + ": 卓ルートの本体を " + CtrlObjectName + " へ移しました。");
        }
        CleanupBrokenBehaviours(table.gameObject);
        CleanupBrokenBehaviours(ctrl);

        var notice = ctrl.GetComponent<MahjongTableNoticeRing>();
        if (notice != null && UdonSharpEditorUtility.GetBackingUdonBehaviour(notice) == null)
        {
            // 過去の失敗で proxy だけ残っている状態。作り直す。
            Object.DestroyImmediate(notice);
            CleanupBrokenBehaviours(ctrl);
            notice = null;
        }
        if (notice == null)
        {
            notice = UdonSharpUndo.AddComponent<MahjongTableNoticeRing>(ctrl);
        }
        if (notice == null)
        {
            Debug.LogError("[BuildTableNoticeRing] MahjongTableNoticeRing を追加できません（U#未コンパイル？）: "
                + table.name);
            return;
        }
        // 新規追加だと SyncMethod が Continuous になる。BehaviourSyncMode.None の実態（同期変数ゼロ）
        // に揃えて None へ戻す。将来 Ctrl に別の本体を同居させた時の事故防止も兼ねる。
        var noticeUdon = UdonSharpEditorUtility.GetBackingUdonBehaviour(notice);
        if (noticeUdon != null && noticeUdon.SyncMethod != VRC.SDKBase.Networking.SyncType.None)
        {
            noticeUdon.SyncMethod = VRC.SDKBase.Networking.SyncType.None;
            EditorUtility.SetDirty(noticeUdon);
        }

        notice.table = table;
        notice.matchmaking = matchmaking;
        notice.ring = ring;
        notice.checkInterval = 1.0f;
        notice.ringRenderer = mr;
        notice.materialJa = matJa;
        notice.materialEn = matEn;
        notice.materialKo = matKo;
        notice.materialZhs = matZhs;
        notice.materialZht = matZht;
        notice.language = Object.FindObjectOfType<KemonoLanguage>();
        if (notice.language == null)
        {
            Debug.LogWarning("[BuildTableNoticeRing] KemonoLanguage がシーンにありません。案内は日本語のままになります。");
        }
        UdonSharpEditorUtility.CopyProxyToUdon(notice);
        EditorUtility.SetDirty(notice);
        EditorUtility.SetDirty(ring);
        EditorUtility.SetDirty(ctrl);
    }

    [MenuItem("Tools/Kemono Mahjong/Validate Table Notice Rings")]
    public static void Validate()
    {
        int errors = 0;
        var tables = Object.FindObjectsOfType<Mahjong>();
        foreach (var table in tables)
        {
            var notice = table.GetComponentInChildren<MahjongTableNoticeRing>(true);
            if (notice == null)
            {
                Debug.LogError("[ValidateTableNoticeRing] " + table.name + ": コンポーネントがありません。", table);
                ++errors;
                continue;
            }
            // 卓ルートにいると Mahjong（Manual）と同期方式が混在する。
            if (notice.gameObject == table.gameObject)
            {
                Debug.LogError("[ValidateTableNoticeRing] " + table.name
                    + ": 本体が卓ルートに付いています（Mahjong の Manual 同期と混在）。Build し直してください。", table);
                ++errors;
                continue;
            }
            if (notice.table == null || notice.matchmaking == null || notice.ring == null
                || notice.ringRenderer == null || notice.materialJa == null || notice.materialEn == null
                || notice.materialKo == null || notice.materialZhs == null || notice.materialZht == null)
            {
                Debug.LogError("[ValidateTableNoticeRing] " + table.name + ": 参照が未割当です"
                    + " (table=" + (notice.table != null) + " matchmaking=" + (notice.matchmaking != null)
                    + " ring=" + (notice.ring != null) + " renderer=" + (notice.ringRenderer != null)
                    + " ja=" + (notice.materialJa != null) + " en=" + (notice.materialEn != null)
                    + " ko=" + (notice.materialKo != null) + " zhs=" + (notice.materialZhs != null)
                    + " zht=" + (notice.materialZht != null) + ")", table);
                ++errors;
                continue;
            }
            var mr = notice.ring.GetComponent<MeshRenderer>();
            var mf = notice.ring.GetComponent<MeshFilter>();
            if (mr == null || mf == null || mf.sharedMesh == null || mr.sharedMaterial == null)
            {
                Debug.LogError("[ValidateTableNoticeRing] " + table.name + ": リングの描画が欠けています。", table);
                ++errors;
            }
        }
        if (errors == 0)
        {
            Debug.Log("[ValidateTableNoticeRing] OK: 卓 " + tables.Length + " 台すべて健全です。");
        }
        else
        {
            Debug.LogError("[ValidateTableNoticeRing] エラー " + errors + " 件");
        }
    }
}
