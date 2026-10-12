#if UNITY_EDITOR
// 卓の番号表示（リセットボタンの上）。
//   Tools > Kemono Mahjong > Build Table Numbers    … 生成／更新（冪等）
//   Tools > Kemono Mahjong > Validate Table Numbers … 検算のみ（変更なし）
//
// 【なぜ卓ごとに作るのか】
//   番号は卓ごとに違うので Prefab には入れられない。シーンの卓インスタンスに
//   直接ぶら下げる（Prefab のオーバーライドになる）。番号は GameObject 名
//   "KemonoMahjong (N)" の N をそのまま使う＝シーンの名前が正。
//
// 【なぜ4面あるのか】
//   卓は4人が四方から囲む。Udon のビルボードは毎フレーム回す必要があって
//   8卓ぶんは無駄なので、静的に4方向へ向けた板を置く（リセットボタン自身の
//   文字も同じ作りになっている）。
//
// 【カリング】
//   卓ルートの子なので TableProximityCuller が卓ごと寝かせた時に一緒に消える。
using System.Collections.Generic;
using TMPro;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SuzuFactory.AozoraMahjong;
using VRC.Udon;

public static class BuildTableNumbers
{
    const string RootName = "TableNumberLabel";
    const string FontAssetPath = "Assets/Mahjong/Fonts/NotoSansJP-Bold SDF.asset";
    const string MaterialPath = "Assets/Mahjong/Materials/TableNumberText.mat";

    // 色分けの監視役。卓ごとではなく**常時アクティブな1体**に持たせる
    // （卓の子に置くと近接カリングで寝て、起きた時に古い色のまま残る）。
    const string TintRootName = "TableNumberTint";
    const string TintScriptPath = "Assets/Mahjong/Sources/TableNumberTint.cs";
    const string TintProgramAssetPath =
        "Assets/Mahjong/scene_UdonProgramSources/TableNumberTint Udon C# Program Asset.asset";

    // 卓ルートからの高さ(m)。リセットボタンは +1.00 にある 0.1m 角なので上端は +1.05。
    // その少し上に置く。ここを下げるとボタンに、上げすぎると和了画面の下辺に近づく。
    const float LabelHeight = 1.17f;
    // 文字の高さ(m)。生成したメッシュの実寸から逆算するので、フォントを差し替えても
    // 見た目の高さは変わらない。SDFのフチ用パディングを含むので、実際の字面は少し小さい。
    const float TargetTextHeight = 0.16f;

    // 全卓・全面で共有する倍率。**数字ごとに測ってはいけない**。
    //   ・字によって字面の高さが違う（丸い字は上下にわずかにはみ出す）ので、
    //     1字ずつ正規化すると卓ごとに大きさが変わる。
    //   ・MeshRenderer.bounds は寝ている卓（SetActive(false)）だと 0 を返すため、
    //     卓の活性状態で結果が変わる＝ビルドのたびに違う大きさになる。
    // なので「基準の1字」を使い捨ての実体で1回だけ測り、その倍率を全部に配る。
    static float scale_;

    [MenuItem("Tools/Kemono Mahjong/Build Table Numbers")]
    public static void Build()
    {
        var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
        if (font == null)
        {
            Debug.LogError("[BuildTableNumbers] フォントが見つかりません: " + FontAssetPath);
            return;
        }
        var mat = EnsureMaterial(font);
        scale_ = MeasureScale(font, mat);

        var tables = Object.FindObjectsOfType<Mahjong>(true);
        if (tables.Length == 0)
        {
            Debug.LogError("[BuildTableNumbers] 卓（Mahjong）がシーンにありません。");
            return;
        }

        int built = 0;
        int skipped = 0;
        var byNumber = new Dictionary<int, Transform>();
        foreach (var table in tables)
        {
            int number = ParseTableNumber(table.gameObject.name);
            if (number <= 0)
            {
                Debug.LogWarning("[BuildTableNumbers] 卓名から番号を読めません: "
                    + table.gameObject.name + "（\"KemonoMahjong (N)\" の形にしてください）", table);
                ++skipped;
                continue;
            }
            BuildOne(table.transform, number, font, mat);
            byNumber[number] = table.transform;
            ++built;
        }

        bool tintWaiting = !WireTint(byNumber, tables.Length);

        EditorSceneManager.MarkAllScenesDirty();
        if (tintWaiting)
        {
            Debug.Log("[BuildTableNumbers] U# プログラムアセットを生成しました。"
                + "コンパイル完了後にもう一度実行すると色分けが配線されます。");
            return;
        }
        Debug.Log("[BuildTableNumbers] 完了：" + built + " 卓に番号を付けました"
            + (skipped > 0 ? "（名前を読めず飛ばした卓 " + skipped + "）" : "")
            + "。色分け（使用中=赤／空き=緑）も配線しました。");
    }

    [MenuItem("Tools/Kemono Mahjong/Validate Table Numbers")]
    public static void Validate()
    {
        int errors = 0;
        float firstScale = -1f;
        var tables = Object.FindObjectsOfType<Mahjong>(true);
        foreach (var table in tables)
        {
            int number = ParseTableNumber(table.gameObject.name);
            var root = FindLabel(table.transform);
            if (root == null)
            {
                Debug.LogError("[ValidateTableNumbers] " + table.gameObject.name
                    + " に " + RootName + " がありません。Build Table Numbers を実行してください。", table);
                ++errors;
                continue;
            }
            if (root.IsChildOf(table.transform) || !root.gameObject.activeInHierarchy)
            {
                Debug.LogError("[ValidateTableNumbers] 番号がカリング対象または非表示です。", root);
                ++errors;
            }
            if (root.childCount != 4)
            {
                Debug.LogError("[ValidateTableNumbers] " + table.gameObject.name
                    + " の面が4枚ではありません（" + root.childCount + "）。", root);
                ++errors;
            }
            foreach (Transform face in root)
            {
                var tmp = face.GetComponent<TextMeshPro>();
                if (tmp == null)
                {
                    Debug.LogError("[ValidateTableNumbers] " + face.name + " に TextMeshPro がありません。", face);
                    ++errors;
                    continue;
                }
                if (tmp.text != number.ToString())
                {
                    Debug.LogError("[ValidateTableNumbers] " + table.gameObject.name
                        + " の表示が「" + tmp.text + "」で名前の番号 " + number + " と違います。", tmp);
                    ++errors;
                }
                // 大きさは全卓そろっていること（1字ずつ測って正規化すると卓ごとにズレる）。
                if (firstScale <= 0f) { firstScale = face.localScale.x; }
                else if (Mathf.Abs(face.localScale.x - firstScale) > 0.0001f)
                {
                    Debug.LogError("[ValidateTableNumbers] " + table.gameObject.name
                        + " の文字の大きさが他の卓と違います（" + face.localScale.x
                        + " / 基準 " + firstScale + "）。Build Table Numbers を実行し直してください。", face);
                    ++errors;
                }
            }
        }

        // 色分けの配線。
        var tintGo = GameObject.Find(TintRootName);
        var tint = (tintGo != null) ? tintGo.GetComponent<TableNumberTint>() : null;
        if (tint == null)
        {
            Debug.LogError("[ValidateTableNumbers] " + TintRootName
                + " がありません。Build Table Numbers を実行してください（色分けが動きません）。");
            ++errors;
        }
        else
        {
            if (tint.matchmaking == null)
            {
                Debug.LogError("[ValidateTableNumbers] TableNumberTint.matchmaking が未割当です。", tint);
                ++errors;
            }
            if (tint.texts == null || tint.texts.Length != tables.Length * 4)
            {
                Debug.LogError("[ValidateTableNumbers] TableNumberTint.texts が "
                    + (tables.Length * 4) + " 件ではありません。", tint);
                ++errors;
            }
            else
            {
                for (int i = 0; i < tint.texts.Length; ++i)
                {
                    if (tint.texts[i] == null)
                    {
                        Debug.LogError("[ValidateTableNumbers] TableNumberTint.texts[" + i + "] が未割当。", tint);
                        ++errors;
                    }
                }
            }
        }

        if (errors == 0) { Debug.Log("[ValidateTableNumbers] OK：" + tables.Length + " 卓すべて番号が一致しています（色分けの配線も揃っています）。"); }
        else { Debug.LogError("[ValidateTableNumbers] エラー " + errors + " 件。"); }
    }

    // ------------------------------------------------------------
    // 色分けの監視役。1体だけ作って 8卓×4面 の TMP を渡す。
    // 配線できたら true、プログラムアセットを作った直後（要再実行）は false。
    // ------------------------------------------------------------
    static bool WireTint(Dictionary<int, Transform> byNumber, int tableCount)
    {
        if (EnsureTintProgramAsset())
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            return false;
        }

        var go = GameObject.Find(TintRootName);
        if (go == null)
        {
            go = new GameObject(TintRootName);
            Undo.RegisterCreatedObjectUndo(go, "Build Table Numbers");
        }
        go.transform.position = Vector3.zero;

        var tint = EnsureUSharpComponent(go, typeof(TableNumberTint)) as TableNumberTint;
        if (tint == null) { return false; }

        var matchmaking = Object.FindObjectOfType<MatchmakingManager>(true);
        if (matchmaking == null)
        {
            Debug.LogWarning("[BuildTableNumbers] MatchmakingManager が見つかりません。"
                + "色分けは配線できません（先に Wire Matchmaking を実行してください）。");
        }
        tint.matchmaking = matchmaking;

        // 並びは「卓インデックス i の面が [i*4 .. i*4+3]」。卓インデックス i の卓番号は i+1
        // （tables の並びは Wire Matchmaking が番号順に固定している）。
        int n = tableCount;
        var texts = new TextMeshPro[n * 4];
        int missing = 0;
        for (int i = 0; i < n; ++i)
        {
            Transform table;
            if (!byNumber.TryGetValue(i + 1, out table)) { missing += 4; continue; }
            var labelRoot = FindLabel(table);
            if (labelRoot == null) { missing += 4; continue; }
            for (int f = 0; f < 4; ++f)
            {
                var face = labelRoot.Find("Face" + f);
                texts[i * 4 + f] = (face != null) ? face.GetComponent<TextMeshPro>() : null;
                if (texts[i * 4 + f] == null) { ++missing; }
            }
        }
        tint.texts = texts;
        if (missing > 0)
        {
            Debug.LogWarning("[BuildTableNumbers] 色分けに渡せなかった面が " + missing + " 件あります。");
        }

        UdonSharpEditorUtility.CopyProxyToUdon(tint);
        EditorUtility.SetDirty(tint);

        // Editor でも状態が分かるように、いまの色を1度置いておく。
        if (matchmaking != null)
        {
            for (int i = 0; i < n; ++i)
            {
                bool free = matchmaking.IsTableFree(i);
                for (int f = 0; f < 4; ++f)
                {
                    var tmp = texts[i * 4 + f];
                    if (tmp != null) { tmp.color = free ? tint.freeColor : tint.busyColor; }
                }
            }
        }
        return true;
    }

    static bool EnsureTintProgramAsset()
    {
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(TintScriptPath);
        if (script == null)
        {
            Debug.LogError("[BuildTableNumbers] スクリプトが見つかりません: " + TintScriptPath);
            return false;
        }
        var type = script.GetClass();
        if (type != null && UdonSharpProgramAsset.GetProgramAssetForClass(type) != null) { return false; }
        if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(TintProgramAssetPath) != null) { return false; }

        var programAsset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
        programAsset.sourceCsScript = script;
        AssetDatabase.CreateAsset(programAsset, TintProgramAssetPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[BuildTableNumbers] U# プログラムアセットを生成: " + TintProgramAssetPath);
        return true;
    }

    static UdonSharpBehaviour EnsureUSharpComponent(GameObject go, System.Type type)
    {
        var proxy = (UdonSharpBehaviour)go.GetComponent(type);
        if (proxy != null)
        {
            if (UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy) != null) { return proxy; }
            Debug.LogWarning("[BuildTableNumbers] " + type.Name + " の backing UdonBehaviour が無いため作り直します。");
            Object.DestroyImmediate(proxy);
            foreach (var ub in go.GetComponents<UdonBehaviour>())
            {
                if (ub.programSource == null) { Object.DestroyImmediate(ub); }
            }
        }

        var added = (UdonSharpBehaviour)UdonSharpUndo.AddComponent(go, type);
        if (added != null && UdonSharpEditorUtility.GetBackingUdonBehaviour(added) == null)
        {
            Debug.LogError("[BuildTableNumbers] " + type.Name + " の追加に失敗（プログラムアセット未コンパイル？）。");
            Object.DestroyImmediate(added);
            return null;
        }
        return added;
    }

    // ------------------------------------------------------------

    // "KemonoMahjong (3)" → 3。読めなければ 0。
    static int ParseTableNumber(string name)
    {
        int open = name.LastIndexOf('(');
        int close = name.LastIndexOf(')');
        if (open < 0 || close < 0 || close <= open + 1) { return 0; }
        int value;
        if (!int.TryParse(name.Substring(open + 1, close - open - 1), out value)) { return 0; }
        return value;
    }

    static Transform FindLabel(Transform table)
    {
        var standalone = GameObject.Find(RootName + "_" + ParseTableNumber(table.name));
        return standalone != null ? standalone.transform : table.Find(RootName);
    }

    static void BuildOne(Transform table, int number, TMP_FontAsset font, Material mat)
    {
        var root = FindLabel(table);
        if (root == null)
        {
            var go = new GameObject(RootName);
            Undo.RegisterCreatedObjectUndo(go, "Build Table Numbers");
            go.transform.SetParent(table, false);
            root = go.transform;
        }
        Undo.SetTransformParent(root, null, "Keep Table Numbers Visible");
        root.name = RootName + "_" + number;
        root.position = table.TransformPoint(new Vector3(0f, LabelHeight, 0f));
        root.rotation = table.rotation;
        root.localScale = table.lossyScale;
        root.gameObject.SetActive(true);

        for (int i = 0; i < 4; ++i)
        {
            BuildFace(root, i, number, font, mat);
        }
        // 面を減らした場合の掃除。
        for (int i = root.childCount - 1; i >= 0; --i)
        {
            var c = root.GetChild(i);
            if (!c.name.StartsWith("Face")) { Undo.DestroyObjectImmediate(c.gameObject); }
        }

        SetLayerRecursively(root.gameObject, 0);
    }

    static void BuildFace(Transform root, int index, int number, TMP_FontAsset font, Material mat)
    {
        string faceName = "Face" + index;
        var t = root.Find(faceName);
        if (t == null)
        {
            var go = new GameObject(faceName, typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(go, "Build Table Numbers");
            go.transform.SetParent(root, false);
            t = go.transform;
        }

        var tmp = t.GetComponent<TextMeshPro>();
        if (tmp == null) { tmp = Undo.AddComponent<TextMeshPro>(t.gameObject); }
        tmp.font = font;
        tmp.fontSharedMaterial = mat;
        tmp.text = number.ToString();
        tmp.fontSize = 4f;
        tmp.enableAutoSizing = false;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        tmp.color = Color.white;

        var rt = (RectTransform)t;
        rt.sizeDelta = new Vector2(4f, 2f);
        rt.anchoredPosition = Vector2.zero;
        rt.localPosition = Vector3.zero;
        rt.localRotation = Quaternion.Euler(0f, index * 90f, 0f);

        tmp.ForceMeshUpdate();
        // 倍率は Build の頭で1回だけ測った共有値。ここで測り直すと卓ごとに大きさがズレる。
        rt.localScale = new Vector3(scale_, scale_, scale_);
    }

    // 基準の1字を使い捨ての実体で組んで、狙いの高さになる倍率を求める。
    //
    // 【メッシュの寸法は当てにならない】
    //   MeshRenderer.bounds も TMP の生成メッシュの bounds も、Editor で組み立てた直後は
    //   0 のまま返ってくる（描画が一度も走っていないので更新されない）。これを高さとして
    //   割ると倍率がフォールバック値に落ち、卓ごと・実行ごとに大きさが変わる。
    //   一方 textInfo.characterInfo は ForceMeshUpdate の時点で必ず埋まる。
    // 【textBounds は使わない】
    //   あれは「行の高さ」で字面の倍近くある（実測 0.579 対 0.336）。
    static float MeasureScale(TMP_FontAsset font, Material mat)
    {
        var probe = new GameObject("__TableNumberProbe", typeof(RectTransform));
        probe.hideFlags = HideFlags.HideAndDontSave;
        try
        {
            var tmp = probe.AddComponent<TextMeshPro>();
            tmp.font = font;
            tmp.fontSharedMaterial = mat;
            tmp.text = "8";                 // 丸い字＝上下のはみ出しが一番大きい字で合わせる
            tmp.fontSize = 4f;
            tmp.enableAutoSizing = false;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableWordWrapping = false;
            ((RectTransform)probe.transform).sizeDelta = new Vector2(4f, 2f);
            probe.transform.localScale = Vector3.one;
            tmp.ForceMeshUpdate();

            float h = 0f;
            if (tmp.textInfo != null && tmp.textInfo.characterCount > 0)
            {
                var ci = tmp.textInfo.characterInfo[0];
                h = ci.topLeft.y - ci.bottomLeft.y;   // 字の四角形（SDFのフチ用パディング込み）
            }
            if (h < 0.0001f)
            {
                Debug.LogWarning("[BuildTableNumbers] 文字の実寸を測れませんでした。既定の倍率を使います。");
                return 0.05f;
            }
            return TargetTextHeight / h;
        }
        finally
        {
            Object.DestroyImmediate(probe);
        }
    }

    // 白抜き＋墨のフチ。フチが無いと明るい背景で白文字が消える。
    // 8卓×4面で32個あるので、マテリアルは1枚を共有する（インスタンス化させない）。
    static Material EnsureMaterial(TMP_FontAsset font)
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (mat == null)
        {
            mat = new Material(font.material);
            mat.name = "TableNumberText";
            AssetDatabase.CreateAsset(mat, MaterialPath);
            Debug.Log("[BuildTableNumbers] 文字マテリアルを生成: " + MaterialPath);
        }
        mat.EnableKeyword("OUTLINE_ON");
        mat.SetColor(ShaderUtilities.ID_FaceColor, Color.white);
        mat.SetColor(ShaderUtilities.ID_OutlineColor, new Color(0.05f, 0.05f, 0.05f, 1f));
        mat.SetFloat(ShaderUtilities.ID_OutlineWidth, 0.22f);
        // 裏面を消す。TMPのSDFシェーダーは既定が Cull Off で、真後ろを向いた面（180度）が
        // 手前の面に鏡像で重なって「二重に見える」（4方向を同じ位置に置いているため）。
        mat.SetFloat("_CullMode", (float)UnityEngine.Rendering.CullMode.Back);
        EditorUtility.SetDirty(mat);
        AssetDatabase.SaveAssets();
        return mat;
    }

    static void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform c in go.transform) { SetLayerRecursively(c.gameObject, layer); }
    }
}
#endif
