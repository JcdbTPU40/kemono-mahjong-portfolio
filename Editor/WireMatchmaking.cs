#if UNITY_EDITOR
// 段位戦マッチング（実装③）の配線ツール。
//   Tools > Kemono Mahjong > Wire Matchmaking (Ranked Queue)
// やること：
//   1) シーンの全卓（Mahjong）を指定優先順に MatchmakingManager.tables へ割り当て
//      （順序＝同期される卓インデックス＝空卓割当の優先順。実行のたびに同じ並びになるようソート必須）。
//   2) 卓誘導ビーコン（光柱）を各卓位置に冪等生成し tableBeacons へ割り当て
//      （カリング対象の卓ルートの外＝常時アクティブな MatchmakingBeacons 配下に置く。
//       遠くから見える必要があるため卓と一緒にカリングしてはならない）。
//   3) 成立ボイス用 AudioSource（2D・ローカル再生）を MatchmakingManager 子に冪等生成。
//      AudioClip（「お待たせしました」wav）は素材支給後に Inspector で割り当てる。
//   4) 卓誘導の矢印（足元から自分の卓まで床に並ぶ矢羽根）を冪等生成し guideArrow へ割り当て。
//      光柱と同じくカリング対象の外に置く。初回は U# プログラムアセットを作って
//      矢印だけ飛ばすので、コンパイル後にもう一度実行すること。
// 前提：Build Nameplates → Wire Nameplates（×2）実行済み＝MatchmakingManager がシーンに存在。
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SuzuFactory.AozoraMahjong;

public static class WireMatchmaking
{
    const int MaxTables = 8; // MatchmakingManager.MAX_TABLES と一致させる
    // マッチング成立時の空卓優先順（GameObject 名の完全一致）。未掲載の卓は末尾・パス順。
    // **卓名の番号順に固定する。** ここの並びが MatchmakingManager.tables の並びになり、
    // 「卓インデックス i ＝ 卓番号 i+1」という前提でスタッフパネルの卓順ページと
    // 卓上の番号表示が読める。番号と並びをズラすと表示と実際の卓が食い違う。
    // 実行時の割当順は MatchmakingManager.tableOrderPacked（スタッフが並べ替え可能）で、
    // その既定値がこの並びそのもの＝(1)→(8)。
    static readonly string[] TableAssignOrder =
    {
        "KemonoMahjong (1)",
        "KemonoMahjong (2)",
        "KemonoMahjong (3)",
        "KemonoMahjong (4)",
        "KemonoMahjong (5)",
        "KemonoMahjong (6)",
        "KemonoMahjong (7)",
        "KemonoMahjong (8)",
    };
    const string BeaconRootName = "MatchmakingBeacons";
    const string BeaconMaterialPath = "Assets/Mahjong/Materials/MatchmakingBeacon.mat";

    const string GuideRootName = "MatchGuideArrow";
    const string GuideMaterialPath = "Assets/Mahjong/Materials/MatchGuideArrow.mat";
    const string GuideMeshPath = "Assets/Mahjong/Meshes/GuideChevron.asset";
    const string GuideScriptPath = "Assets/Mahjong/Sources/MatchGuideArrow.cs";
    const string ProgramAssetDir = "Assets/Mahjong/scene_UdonProgramSources";
    const string GuideProgramAssetPath =
        ProgramAssetDir + "/MatchGuideArrow Udon C# Program Asset.asset";

    // 矢印の確定値。U# のシリアライズ済み値は .cs の既定値を変えても更新されないので、
    // 見た目を決める数値は毎回ここから代入する（他のビルダーと同じ方針）。
    const float GuideSpacing = 0.9f;      // 矢羽根の間隔(m)
    const float GuideStartOffset = 1.2f;  // 足元から並べ始める距離(m)
    const float GuideEndOffset = 0.8f;    // 的（ENTRYボタン）の手前で止める距離(m)
    const float GuideMaxLength = 8.0f;    // 伸ばす最大の長さ(m)
    const float GuideFlowSpeed = 1.4f;    // 流れる速さ(m/s)
    const float GuideGroundOffset = 0.23f;
    const float GuideChevronScale = 1.0f;

    /// <summary>
    /// 床に並べる矢羽根の数。(GuideMaxLength - GuideStartOffset) / GuideSpacing に
    /// 流れの送り込み1枚を足した数。増やしても maxLength で頭打ちになるだけ。
    /// </summary>
    const int GuideChevronCount = 10;

    [MenuItem("Tools/Kemono Mahjong/Wire Matchmaking (Ranked Queue)")]
    public static void Run()
    {
        var matchmaking = Object.FindObjectOfType<MatchmakingManager>(true);
        if (matchmaking == null)
        {
            Debug.LogError("[WireMatchmaking] MatchmakingManager が見つかりません。先に Build Nameplates → Wire Nameplates（×2）を実行してください。");
            return;
        }

        // --- 1) 卓の収集（指定優先順。未掲載はパス順で末尾） ---
        var found = Object.FindObjectsOfType<Mahjong>(true);
        System.Array.Sort(found, (a, b) =>
        {
            int ia = TableOrderIndex(a.name);
            int ib = TableOrderIndex(b.name);
            if (ia != ib) { return ia.CompareTo(ib); }
            return string.CompareOrdinal(GetPath(a.transform), GetPath(b.transform));
        });

        if (found.Length == 0)
        {
            Debug.LogError("[WireMatchmaking] シーンに Mahjong（卓）が見つかりません。");
            return;
        }
        if (found.Length > MaxTables)
        {
            Debug.LogWarning("[WireMatchmaking] 卓が " + found.Length + " 卓あります。先頭 " + MaxTables + " 卓のみマッチング対象にします。");
        }

        int n = Mathf.Min(found.Length, MaxTables);
        var tables = new Mahjong[n];
        for (int i = 0; i < n; ++i) { tables[i] = found[i]; }

        // --- 2) ビーコン生成（常時アクティブなルート配下・卓カリングの影響を受けない） ---
        var beaconRoot = GameObject.Find(BeaconRootName);
        if (beaconRoot == null)
        {
            beaconRoot = new GameObject(BeaconRootName);
            Undo.RegisterCreatedObjectUndo(beaconRoot, "Create " + BeaconRootName);
        }

        var beaconMat = GetOrCreateBeaconMaterial();
        var beacons = new GameObject[n];
        for (int i = 0; i < n; ++i)
        {
            beacons[i] = EnsureBeacon(beaconRoot.transform, i, tables[i].transform.position, beaconMat);
        }
        // 余分な旧ビーコン（卓が減った場合）を掃除。
        for (int i = beaconRoot.transform.childCount - 1; i >= 0; --i)
        {
            var c = beaconRoot.transform.GetChild(i);
            if (c.name.StartsWith("Beacon"))
            {
                int idx;
                if (int.TryParse(c.name.Substring(6), out idx) && idx >= n)
                {
                    Undo.DestroyObjectImmediate(c.gameObject);
                }
            }
        }

        // --- 2.5) 卓誘導の矢印（床の矢羽根。光柱と同じく卓カリングの外） ---
        var guideArrow = EnsureGuideArrow();

        // --- 3) 成立ボイス用 AudioSource（2D・ローカル） ---
        var mmGo = matchmaking.gameObject;
        var voiceT = mmGo.transform.Find("MatchVoice");
        GameObject voiceGo = voiceT != null ? voiceT.gameObject : null;
        if (voiceGo == null)
        {
            voiceGo = new GameObject("MatchVoice");
            Undo.RegisterCreatedObjectUndo(voiceGo, "Create MatchVoice");
            voiceGo.transform.SetParent(mmGo.transform, false);
        }
        var voiceSource = voiceGo.GetComponent<AudioSource>();
        if (voiceSource == null)
        {
            voiceSource = Undo.AddComponent<AudioSource>(voiceGo);
        }
        voiceSource.playOnAwake = false;
        voiceSource.spatialBlend = 0f; // 2D（当事者ローカル再生。仕様B）
        voiceSource.loop = false;

        // --- 卓側 → matchmaking の逆参照（実装④：局数ルール参照・フリー戦の戦績除外に使う） ---
        int backRefs = 0;
        for (int i = 0; i < n; ++i)
        {
            if (tables[i].matchmaking != matchmaking)
            {
                tables[i].matchmaking = matchmaking;
                UdonSharpEditorUtility.CopyProxyToUdon(tables[i]);
                EditorUtility.SetDirty(tables[i]);
            }
            ++backRefs;
        }

        // --- フィールド割り当て ---
        matchmaking.tables = tables;
        matchmaking.tableBeacons = beacons;
        // 矢印は初回実行（プログラムアセット生成直後）だけ null になる。
        // そのときは既存の割り当てを消さない＝2回目の実行で埋まる。
        if (guideArrow != null)
        {
            guideArrow.spacing = GuideSpacing;
            guideArrow.startOffset = GuideStartOffset;
            guideArrow.endOffset = GuideEndOffset;
            guideArrow.maxLength = GuideMaxLength;
            guideArrow.flowSpeed = GuideFlowSpeed;
            guideArrow.groundOffset = GuideGroundOffset;
            guideArrow.chevronScale = GuideChevronScale;
            // 光柱が消える距離と揃える（矢印だけ残ると卓に着いたのに案内が続く）。
            guideArrow.hideDistance = matchmaking.beaconHideDistance + 0.2f;

            UdonSharpEditorUtility.CopyProxyToUdon(guideArrow);
            EditorUtility.SetDirty(guideArrow);
            matchmaking.guideArrow = guideArrow;
            // 卓へ来たのに何もできない人を送る先。矢印は卓誘導と同じものを使い回す。
            var mainMenuGo = GameObject.Find("MainMenu");
            matchmaking.mainMenuGuideTarget = mainMenuGo != null ? mainMenuGo.transform : null;
            if (mainMenuGo == null)
            {
                Debug.LogWarning("[WireMatchmaking] MainMenu が見つかりません。卓からメインメニューへの案内矢印は出ません。");
            }
        }
        matchmaking.matchVoiceSource = voiceSource;

        // スタッフ表示名の allowlist。段位戦の待機列の開閉を Owner 側で承認するのに使う。
        // allowlist の実体は StaffOnlyArea に一本化してある（スタッフ追加は1か所で済む）。
        var staffArea = UnityEngine.Object.FindObjectOfType<StaffOnlyArea>(true);
        if (staffArea == null)
        {
            Debug.LogWarning("[WireMatchmaking] StaffOnlyArea が見つかりません。段位戦の待機列の開閉が使えません（Build Staff Only Area を先に実行）。");
        }
        matchmaking.staffArea = staffArea;
        // matchVoiceClip は素材（お待たせしました wav 48kHz mono・Vorbisインポート）支給後に Inspector で設定。
        if (matchmaking.matchVoiceClip == null)
        {
            Debug.LogWarning("[WireMatchmaking] matchVoiceClip が未設定です。成立ボイスは素材支給後に Inspector で割り当ててください（未設定でも動作はします）。");
        }

        UdonSharpEditorUtility.CopyProxyToUdon(matchmaking);
        EditorUtility.SetDirty(matchmaking);
        EditorSceneManager.MarkSceneDirty(mmGo.scene);

        var names = "";
        for (int i = 0; i < n; ++i)
        {
            names += "\n  [" + i + "] " + GetPath(tables[i].transform);
            // 「卓インデックス i ＝ 卓番号 i+1」はスタッフパネルの卓順ページと
            // 卓上の番号表示が前提にしている。ズレたらここで気付けるようにする。
            var want = "KemonoMahjong (" + (i + 1) + ")";
            if (tables[i].gameObject.name != want)
            {
                Debug.LogWarning("[WireMatchmaking] 卓インデックス " + i + " の名前が " + want
                    + " ではありません（" + tables[i].gameObject.name
                    + "）。卓上の番号表示・スタッフパネルの卓順と食い違います。", tables[i]);
            }
        }
        Debug.Log("[WireMatchmaking] 完了：卓 " + n + " 卓＋ビーコン割り当て・卓への逆参照 " + backRefs + " 卓。卓インデックス順：" + names);
    }

    // ============================================================
    //  卓誘導の矢印（床の矢羽根）
    // ------------------------------------------------------------
    //  光柱は「どこにあるか」しか言えない。8卓並ぶとどれが自分の柱か
    //  分かりにくいので、足元から卓まで矢羽根を並べて向きと道のりを見せる。
    //  完全にローカル表示（同期変数ゼロ）。並べ替えは MatchGuideArrow が毎フレーム行う。
    // ============================================================
    static MatchGuideArrow EnsureGuideArrow()
    {
        if (EnsureGuideProgramAsset())
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.LogWarning("[WireMatchmaking] MatchGuideArrow の U#プログラムアセットを生成しました。"
                + "コンパイル完了後、もう一度 Wire Matchmaking を実行すると矢印が配線されます。");
            return null;
        }

        var rootGo = GameObject.Find(GuideRootName);
        if (rootGo == null)
        {
            rootGo = new GameObject(GuideRootName);
            Undo.RegisterCreatedObjectUndo(rootGo, "Create " + GuideRootName);
        }
        rootGo.transform.position = Vector3.zero;
        rootGo.transform.rotation = Quaternion.identity;
        rootGo.transform.localScale = Vector3.one;

        var comp = rootGo.GetComponent<MatchGuideArrow>();
        if (comp == null)
        {
            comp = (MatchGuideArrow)UdonSharpUndo.AddComponent(rootGo, typeof(MatchGuideArrow));
        }
        if (comp == null || UdonSharpEditorUtility.GetBackingUdonBehaviour(comp) == null)
        {
            Debug.LogError("[WireMatchmaking] MatchGuideArrow の追加に失敗しました（プログラムアセット未コンパイル？）。");
            return null;
        }

        var mesh = GetOrCreateChevronMesh();
        var mat = GetOrCreateGuideMaterial();

        var chevrons = new Transform[GuideChevronCount];
        for (int i = 0; i < GuideChevronCount; ++i)
        {
            chevrons[i] = EnsureChevron(rootGo.transform, i, mesh, mat);
        }
        // 数を減らしたときの余りを掃除する。
        for (int i = rootGo.transform.childCount - 1; i >= 0; --i)
        {
            var c = rootGo.transform.GetChild(i);
            if (!c.name.StartsWith("Chevron")) { continue; }

            int idx;
            if (int.TryParse(c.name.Substring(7), out idx) && idx >= GuideChevronCount)
            {
                Undo.DestroyObjectImmediate(c.gameObject);
            }
        }

        comp.chevrons = chevrons;
        return comp;
    }

    static bool EnsureGuideProgramAsset()
    {
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(GuideScriptPath);
        if (script == null)
        {
            Debug.LogError("[WireMatchmaking] スクリプトが見つかりません: " + GuideScriptPath);
            return false;
        }

        var type = script.GetClass();
        if (type != null && UdonSharpProgramAsset.GetProgramAssetForClass(type) != null) { return false; }
        if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(GuideProgramAssetPath) != null) { return false; }

        EnsureFolder(ProgramAssetDir);

        var programAsset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
        programAsset.sourceCsScript = script;
        AssetDatabase.CreateAsset(programAsset, GuideProgramAssetPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[WireMatchmaking] U#プログラムアセットを生成: " + GuideProgramAssetPath);
        return true;
    }

    static Transform EnsureChevron(Transform root, int index, Mesh mesh, Material mat)
    {
        string name = "Chevron" + index;
        var t = root.Find(name);
        GameObject go = t != null ? t.gameObject : null;
        if (go == null)
        {
            go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            go.transform.SetParent(root, false);
        }

        var mf = go.GetComponent<MeshFilter>();
        if (mf == null) { mf = Undo.AddComponent<MeshFilter>(go); }
        mf.sharedMesh = mesh;

        var mr = go.GetComponent<MeshRenderer>();
        if (mr == null) { mr = Undo.AddComponent<MeshRenderer>(go); }
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

        go.SetActive(false); // MatchGuideArrow が当事者ローカルでのみ並べる
        return go.transform;
    }

    /// <summary>
    /// 床に寝かせる矢羽根（&gt; の形）。+Z を進行方向として XZ 平面に作る。
    /// 裏からも見えるように両面ぶんの三角形を持たせる（8三角形＝Questでも誤差）。
    /// </summary>
    static Mesh GetOrCreateChevronMesh()
    {
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(GuideMeshPath);
        if (mesh != null) { return mesh; }

        const float w = 0.34f;   // 半幅
        const float len = 0.42f; // 先端の突き出し
        const float th = 0.17f;  // 帯の太さ（Z方向）

        var verts = new Vector3[]
        {
            new Vector3(-w, 0f, 0f),        // 0 前縁 左
            new Vector3(0f, 0f, len),       // 1 前縁 先端
            new Vector3(w, 0f, 0f),         // 2 前縁 右
            new Vector3(-w, 0f, -th),       // 3 後縁 左
            new Vector3(0f, 0f, len - th),  // 4 後縁 先端
            new Vector3(w, 0f, -th),        // 5 後縁 右
        };

        // 表（+Y から見た形）と裏（巻きを反転）。どちらから見ても消えない。
        var tris = new int[]
        {
            0, 1, 4,  0, 4, 3,
            1, 2, 5,  1, 5, 4,
            4, 1, 0,  3, 4, 0,
            5, 2, 1,  4, 5, 1,
        };

        var normals = new Vector3[verts.Length];
        for (int i = 0; i < normals.Length; ++i) { normals[i] = Vector3.up; }

        var uvs = new Vector2[verts.Length];
        for (int i = 0; i < uvs.Length; ++i)
        {
            uvs[i] = new Vector2((verts[i].x + w) / (2f * w), (verts[i].z + th) / (len + th));
        }

        mesh = new Mesh();
        mesh.name = "GuideChevron";
        mesh.vertices = verts;
        mesh.triangles = tris;
        mesh.normals = normals;
        mesh.uv = uvs;
        mesh.RecalculateBounds();

        EnsureFolder(System.IO.Path.GetDirectoryName(GuideMeshPath).Replace('\\', '/'));
        AssetDatabase.CreateAsset(mesh, GuideMeshPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[WireMatchmaking] 矢羽根メッシュを生成: " + GuideMeshPath);
        return mesh;
    }

    static Material GetOrCreateGuideMaterial()
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(GuideMaterialPath);
        if (mat != null) { return mat; }

        // 光柱と同じ系統の加算。床に置くので不透明だと地面とZファイティングしやすい。
        var shader = Shader.Find("VRChat/Mobile/Particles/Additive");
        if (shader == null) { shader = Shader.Find("Legacy Shaders/Particles/Additive"); }
        if (shader == null) { shader = Shader.Find("Unlit/Color"); }
        if (shader == null)
        {
            Debug.LogWarning("[WireMatchmaking] 矢印用シェーダーが見つかりません。");
            return null;
        }

        mat = new Material(shader);
        var color = new Color(1f, 0.82f, 0.28f, 0.85f);
        if (mat.HasProperty("_Color")) { mat.SetColor("_Color", color); }
        if (mat.HasProperty("_TintColor")) { mat.SetColor("_TintColor", color); }
        mat.enableInstancing = false; // 実機だけ描画が壊れた前例があるため明示的に切る

        EnsureFolder(System.IO.Path.GetDirectoryName(GuideMaterialPath).Replace('\\', '/'));
        AssetDatabase.CreateAsset(mat, GuideMaterialPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[WireMatchmaking] 矢印用マテリアルを生成: " + GuideMaterialPath
            + "（shader=" + shader.name + "）");
        return mat;
    }

    static void EnsureFolder(string path)
    {
        if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) { return; }

        string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
        string leaf = System.IO.Path.GetFileName(path);
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }

    // ビーコン（光柱）1本を冪等生成。卓中心の上空に高さ8mの柱。初期非表示。
    static GameObject EnsureBeacon(Transform root, int index, Vector3 tablePos, Material mat)
    {
        string name = "Beacon" + index;
        var t = root.Find(name);
        GameObject go = t != null ? t.gameObject : null;
        if (go == null)
        {
            go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            go.transform.SetParent(root, false);

            // 当たり判定は不要（UIレーザー・移動を邪魔しない）。
            var col = go.GetComponent<Collider>();
            if (col != null) { Object.DestroyImmediate(col); }
        }

        // Cylinder は scale.y の2倍が高さ。卓中心から上へ立てる。
        go.transform.position = tablePos + new Vector3(0f, 4f, 0f);
        go.transform.localScale = new Vector3(0.35f, 4f, 0.35f);

        var renderer = go.GetComponent<MeshRenderer>();
        if (renderer != null && mat != null)
        {
            renderer.sharedMaterial = mat;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        go.SetActive(false); // MatchmakingManager が当事者ローカルでのみ表示する
        return go;
    }

    // ビーコン用マテリアル（Quest対応の加算系→無ければUnlit）。無ければ生成して保存。
    static Material GetOrCreateBeaconMaterial()
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(BeaconMaterialPath);
        if (mat != null) { return mat; }

        var shader = Shader.Find("VRChat/Mobile/Particles/Additive");
        if (shader == null) { shader = Shader.Find("Legacy Shaders/Particles/Additive"); }
        if (shader == null) { shader = Shader.Find("Unlit/Color"); }
        if (shader == null)
        {
            Debug.LogWarning("[WireMatchmaking] ビーコン用シェーダーが見つかりません。デフォルトマテリアルのまま生成します。");
            return null;
        }

        mat = new Material(shader);
        if (mat.HasProperty("_Color")) { mat.SetColor("_Color", new Color(1f, 0.85f, 0.3f, 0.6f)); }
        if (mat.HasProperty("_TintColor")) { mat.SetColor("_TintColor", new Color(1f, 0.85f, 0.3f, 0.6f)); }

        var dir = System.IO.Path.GetDirectoryName(BeaconMaterialPath).Replace('\\', '/');
        if (!AssetDatabase.IsValidFolder(dir))
        {
            AssetDatabase.CreateFolder(System.IO.Path.GetDirectoryName(dir).Replace('\\', '/'),
                                       System.IO.Path.GetFileName(dir));
        }
        AssetDatabase.CreateAsset(mat, BeaconMaterialPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[WireMatchmaking] ビーコン用マテリアルを生成: " + BeaconMaterialPath + "（shader=" + shader.name + "）");
        return mat;
    }

    static int TableOrderIndex(string name)
    {
        for (int i = 0; i < TableAssignOrder.Length; ++i)
        {
            if (TableAssignOrder[i] == name) { return i; }
        }
        return TableAssignOrder.Length;
    }

    static string GetPath(Transform t)
    {
        var path = t.name;
        while (t.parent != null)
        {
            t = t.parent;
            path = t.name + "/" + path;
        }
        return path;
    }
}
#endif
