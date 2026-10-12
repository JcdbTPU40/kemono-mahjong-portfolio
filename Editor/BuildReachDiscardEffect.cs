#if UNITY_EDITOR
// 立直の打牌演出（右からスライドイン → 着地エフェクト）の骨格生成と配線。
//   Tools > Kemono Mahjong > Build Reach Discard Effects … 全卓に ReachDiscardEffect のリグを冪等生成
//   Tools > Kemono Mahjong > Wire Reach Discard Effects  … U#コンポーネント追加＋参照割り当て＋卓への逆参照
// どちらも2回実行しても安全（冪等）。見た目を変えたい場合は下の定数を変えて Build を再実行する
// （手でシーンをいじった調整は Build 再実行で上書きされる）。
//
// エフェクト用テクスチャは和了演出と共有する（Assets/Mahjong/Textures/FX）。
// 無ければ BuildAgariEffect.EnsureFxTextures() がその場で生成する。
//
// 卓は Object.FindObjectsOfType<Mahjong>(true) で拾う（BuildAgariEffect と同じ流儀）。
// スライド自体は Mahjong.cs 側だけで動くので、このリグ未生成でも打牌は滑る
// （出ないのは着地エフェクトだけ）。
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using SuzuFactory.AozoraMahjong;
using VRC.Udon;

public static class BuildReachDiscardEffect
{
    const string RootName = "ReachDiscardEffect";
    const string ProgramAssetDir = "Assets/Mahjong/scene_UdonProgramSources";
    const string FxTextureDir = "Assets/Mahjong/Textures/FX";

    const string TexGlow = FxTextureDir + "/fx_glow.png";
    const string TexFlash = FxTextureDir + "/fx_flash.png";
    const string TexRing = FxTextureDir + "/fx_ring.png";
    const string TexSpark = FxTextureDir + "/fx_spark.png";

    // Canvas は 1000x1000 の仮想解像度。実寸 = 1000 * CanvasScale（m）＝0.25m四方。
    const float CanvasScale = 0.00025f;
    static readonly Vector2 CanvasSize = new Vector2(1000f, 1000f);

    const int NumSparks = 4;

    // 各パーツの基準サイズ（Canvas px）。牌1枚は約2.1cm＝84px相当なので、
    // 和了演出よりひと回り小さくして「打牌の着地」に見合う規模に収める。
    static readonly Vector2 GlowSize = new Vector2(260f, 260f);
    static readonly Vector2 FlashSize = new Vector2(220f, 220f);
    static readonly Vector2 RingSize = new Vector2(420f, 420f);
    static readonly Vector2 SparkSize = new Vector2(40f, 40f);

    // ============================================================
    // Build：骨格生成
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Build Reach Discard Effects")]
    public static void Build()
    {
        // テクスチャは和了演出と共有（無ければ生成される）。
        BuildAgariEffect.EnsureFxTextures();

        var tables = Object.FindObjectsOfType<Mahjong>(true);
        System.Array.Sort(tables, (a, b) => string.CompareOrdinal(GetPath(a.transform), GetPath(b.transform)));
        if (tables.Length == 0)
        {
            Debug.LogError("[BuildReachDiscardEffect] Mahjong（卓）が見つかりません。シーンを開くか、卓のPrefab Modeで実行してください。");
            return;
        }

        foreach (var table in tables)
        {
            BuildOne(table.transform);
        }

        MarkDirty(tables[0].gameObject);
        Debug.Log("[BuildReachDiscardEffect] 完了：" + tables.Length
            + " 卓に立直打牌演出リグを生成/更新。次に Wire Reach Discard Effects を実行してください。");
    }

    static void BuildOne(Transform tableRoot)
    {
        var root = EnsureChild(tableRoot, RootName);
        // スクリプトが載るルートは常時アクティブを厳守（非アクティブなU#は外から叩けない）。
        // 表示ON/OFFは子の FxRoot でのみ行う。
        root.transform.localPosition = Vector3.zero;
        root.transform.localRotation = Quaternion.identity;
        root.transform.localScale = Vector3.one;
        root.SetActive(true);

        BuildRig(root.transform);
    }

    static void BuildRig(Transform root)
    {
        root.gameObject.SetActive(true);

        // SE用（クリップ未割当なら鳴らないだけ）。3D音として卓の位置から鳴らす。
        var audio = root.GetComponent<AudioSource>();
        if (audio == null) { audio = Undo.AddComponent<AudioSource>(root.gameObject); }
        audio.playOnAwake = false;
        audio.loop = false;
        audio.spatialBlend = 1f;
        audio.dopplerLevel = 0f;
        audio.minDistance = 1.5f;
        audio.maxDistance = 12f;
        audio.rolloffMode = AudioRolloffMode.Linear;

        var fxRoot = EnsureChild(root, "FxRoot");
        fxRoot.transform.localPosition = Vector3.zero;
        fxRoot.transform.localRotation = Quaternion.identity;
        fxRoot.transform.localScale = Vector3.one;

        // ---- 縦レイヤー（プレイヤーの方を向く）----
        var billboard = EnsureChild(fxRoot.transform, "Billboard");
        billboard.transform.localPosition = Vector3.zero;
        billboard.transform.localRotation = Quaternion.identity;
        billboard.transform.localScale = Vector3.one;
        var billboardCanvas = EnsureCanvas(billboard.transform);

        // 描画順＝ヒエラルキー順。奥から グロー → 粒 → 閃光。
        EnsureFxImage(billboardCanvas.transform, "Glow", TexGlow, GlowSize, new Vector2(0.5f, 0.5f), Vector2.zero);
        for (int i = 0; i < NumSparks; ++i)
        {
            EnsureFxImage(billboardCanvas.transform, "Spark" + i, TexSpark, SparkSize, new Vector2(0.5f, 0.5f), Vector2.zero);
        }
        EnsureFxImage(billboardCanvas.transform, "Flash", TexFlash, FlashSize, new Vector2(0.5f, 0.5f), Vector2.zero);

        var keepBillboard = new string[2 + NumSparks];
        keepBillboard[0] = "Glow";
        keepBillboard[1] = "Flash";
        for (int i = 0; i < NumSparks; ++i) { keepBillboard[2 + i] = "Spark" + i; }
        CleanupChildren(billboardCanvas.transform, keepBillboard);

        // ---- 卓面レイヤー（寝かせる。向きは実行時に Play が絶対姿勢で置き直す）----
        var ground = EnsureChild(fxRoot.transform, "Ground");
        ground.transform.localPosition = Vector3.zero;
        ground.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
        ground.transform.localScale = Vector3.one;
        var groundCanvas = EnsureCanvas(ground.transform);

        EnsureFxImage(groundCanvas.transform, "Ring", TexRing, RingSize, new Vector2(0.5f, 0.5f), Vector2.zero);
        CleanupChildren(groundCanvas.transform, new string[] { "Ring" });

        CleanupChildren(fxRoot.transform, new string[] { "Billboard", "Ground" });

        // 編集中に見えっぱなしにならないよう畳んでおく（実行時も Start で畳まれる）。
        fxRoot.SetActive(false);
    }

    static GameObject EnsureCanvas(Transform parent)
    {
        var canvasGo = EnsureUiChild(parent, "Canvas");

        var canvas = canvasGo.GetComponent<Canvas>();
        if (canvas == null) { canvas = canvasGo.AddComponent<Canvas>(); }
        canvas.renderMode = RenderMode.WorldSpace;

        var scaler = canvasGo.GetComponent<CanvasScaler>();
        if (scaler == null) { scaler = canvasGo.AddComponent<CanvasScaler>(); }
        scaler.dynamicPixelsPerUnit = 2f;

        // 表示専用。GraphicRaycaster は付けない（卓上ボタンの操作を遮らないため）。
        var raycaster = canvasGo.GetComponent<GraphicRaycaster>();
        if (raycaster != null) { Object.DestroyImmediate(raycaster); }

        var rt = canvasGo.GetComponent<RectTransform>();
        rt.localPosition = Vector3.zero;
        rt.localRotation = Quaternion.identity;
        rt.localScale = new Vector3(CanvasScale, CanvasScale, CanvasScale);
        rt.sizeDelta = CanvasSize;

        return canvasGo;
    }

    static RawImage EnsureFxImage(Transform parent, string name, string texturePath, Vector2 size, Vector2 pivot, Vector2 pos)
    {
        var go = EnsureUiChild(parent, name);

        var raw = go.GetComponent<RawImage>();
        if (raw == null) { raw = go.AddComponent<RawImage>(); }
        raw.raycastTarget = false;
        raw.texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
        raw.color = new Color(1f, 1f, 1f, 0f); // 初期は透明（実行時にスクリプトが塗る）

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        rt.localScale = Vector3.one;
        rt.localRotation = Quaternion.identity;

        return raw;
    }

    // ============================================================
    // Wire：U#コンポーネント追加＋参照割り当て
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Wire Reach Discard Effects")]
    public static void Wire()
    {
        if (EnsureProgramAsset(
            "Assets/Mahjong/Sources/MahjongReachDiscardEffect.cs",
            ProgramAssetDir + "/MahjongReachDiscardEffect Udon C# Program Asset.asset"))
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[WireReachDiscardEffect] U#プログラムアセットを生成しました。コンパイル完了後、もう一度 Wire Reach Discard Effects を実行してください。");
            return;
        }

        var tables = Object.FindObjectsOfType<Mahjong>(true);
        System.Array.Sort(tables, (a, b) => string.CompareOrdinal(GetPath(a.transform), GetPath(b.transform)));
        if (tables.Length == 0)
        {
            Debug.LogError("[WireReachDiscardEffect] Mahjong（卓）が見つかりません。");
            return;
        }

        int wired = 0;
        foreach (var table in tables)
        {
            var rootT = table.transform.Find(RootName);
            if (rootT == null)
            {
                Debug.LogError("[WireReachDiscardEffect] " + GetPath(table.transform) + " に " + RootName
                    + " がありません。先に Build Reach Discard Effects を実行してください。");
                continue;
            }

            var comp = WireRig(rootT);
            if (comp == null) { return; }

            // 卓本体 → 立直打牌演出の参照（未割当なら着地エフェクトなし）。
            if (table.reachDiscardEffect != comp)
            {
                table.reachDiscardEffect = comp;
                UdonSharpEditorUtility.CopyProxyToUdon(table);
                EditorUtility.SetDirty(table);
            }
            ++wired;
        }

        MarkDirty(tables[0].gameObject);
        Debug.Log("[WireReachDiscardEffect] 完了：" + wired + "/" + tables.Length + " 卓を配線。");
    }

    static MahjongReachDiscardEffect WireRig(Transform rootT)
    {
        var comp = (MahjongReachDiscardEffect)EnsureUSharpComponent(rootT.gameObject, typeof(MahjongReachDiscardEffect));
        if (comp == null)
        {
            Debug.LogError("[WireReachDiscardEffect] MahjongReachDiscardEffect の追加に失敗（プログラムアセット未コンパイル？）。コンパイル後に再実行してください。");
            return null;
        }

        var fxRoot = rootT.Find("FxRoot");
        if (fxRoot == null)
        {
            Debug.LogError("[WireReachDiscardEffect] " + GetPath(rootT) + " に FxRoot がありません。Build をやり直してください。");
            return null;
        }

        var billboardCanvas = fxRoot.Find("Billboard/Canvas");
        var groundCanvas = fxRoot.Find("Ground/Canvas");

        comp.fxRoot = fxRoot.gameObject;
        comp.billboardRoot = fxRoot.Find("Billboard");
        comp.groundRoot = fxRoot.Find("Ground");

        comp.glowImage = FindRaw(billboardCanvas, "Glow");
        comp.glowRect = FindRect(billboardCanvas, "Glow");
        comp.flashImage = FindRaw(billboardCanvas, "Flash");
        comp.flashRect = FindRect(billboardCanvas, "Flash");
        comp.ringImage = FindRaw(groundCanvas, "Ring");
        comp.ringRect = FindRect(groundCanvas, "Ring");

        var sparkImages = new RawImage[NumSparks];
        var sparkRects = new RectTransform[NumSparks];
        for (int i = 0; i < NumSparks; ++i)
        {
            sparkImages[i] = FindRaw(billboardCanvas, "Spark" + i);
            sparkRects[i] = FindRect(billboardCanvas, "Spark" + i);
        }
        comp.sparkImages = sparkImages;
        comp.sparkRects = sparkRects;

        comp.seSource = rootT.GetComponent<AudioSource>();
        // 斬撃 -20.7 LUFS → 0.12 で約 -39 LUFS 相当（手牌公開のドンと同格）。
        comp.seVolume = KemonoAudioNormalizer.SeScale(comp.discardSound, 0.159f);

        UdonSharpEditorUtility.CopyProxyToUdon(comp);
        EditorUtility.SetDirty(comp);
        return comp;
    }

    static RawImage FindRaw(Transform parent, string name)
    {
        if (parent == null) { return null; }
        var t = parent.Find(name);
        return t != null ? t.GetComponent<RawImage>() : null;
    }

    static RectTransform FindRect(Transform parent, string name)
    {
        if (parent == null) { return null; }
        var t = parent.Find(name);
        return t != null ? t as RectTransform : null;
    }

    // ============================================================
    // 共通ヘルパ（BuildAgariEffect と同等の実装）
    // ============================================================
    static bool EnsureProgramAsset(string scriptPath, string assetPath)
    {
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
        if (script == null)
        {
            Debug.LogError("[WireReachDiscardEffect] スクリプトが見つかりません: " + scriptPath);
            return false;
        }
        var type = script.GetClass();
        if (type != null && UdonSharpProgramAsset.GetProgramAssetForClass(type) != null) { return false; }
        if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(assetPath) != null) { return false; }

        var programAsset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
        programAsset.sourceCsScript = script;
        AssetDatabase.CreateAsset(programAsset, assetPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[WireReachDiscardEffect] U#プログラムアセットを生成: " + assetPath);
        return true;
    }

    static UdonSharpBehaviour EnsureUSharpComponent(GameObject go, System.Type type)
    {
        var proxy = (UdonSharpBehaviour)go.GetComponent(type);
        if (proxy != null)
        {
            if (UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy) != null) { return proxy; }

            Debug.LogWarning("[WireReachDiscardEffect] " + type.Name + " の backing UdonBehaviour が無いため作り直します。");
            Object.DestroyImmediate(proxy);
            foreach (var ub in go.GetComponents<UdonBehaviour>())
            {
                if (ub.programSource == null) { Object.DestroyImmediate(ub); }
            }
        }

        var added = (UdonSharpBehaviour)UdonSharpUndo.AddComponent(go, type);
        if (added != null && UdonSharpEditorUtility.GetBackingUdonBehaviour(added) == null)
        {
            Debug.LogError("[WireReachDiscardEffect] " + type.Name + " の追加に失敗（プログラムアセット未コンパイル？）。");
            Object.DestroyImmediate(added);
            return null;
        }
        return added;
    }

    static GameObject EnsureChild(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null) { return t.gameObject; }
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Create " + name);
        go.transform.SetParent(parent, false);
        return go;
    }

    static GameObject EnsureUiChild(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null)
        {
            if (t.GetComponent<RectTransform>() == null)
            {
                Undo.DestroyObjectImmediate(t.gameObject);
            }
            else
            {
                return t.gameObject;
            }
        }
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Create " + name);
        go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);
        return go;
    }

    // 期待する子だけ残す（レイアウト変更後の遺物を掃除して冪等に）。
    static void CleanupChildren(Transform parent, string[] keepNames)
    {
        var keep = new System.Collections.Generic.HashSet<string>(keepNames);
        for (int i = parent.childCount - 1; i >= 0; --i)
        {
            var c = parent.GetChild(i);
            if (!keep.Contains(c.name)) { Undo.DestroyObjectImmediate(c.gameObject); }
        }
    }

    static void MarkDirty(GameObject go)
    {
        if (go.scene.IsValid())
        {
            EditorSceneManager.MarkSceneDirty(go.scene);
        }
        else
        {
            EditorUtility.SetDirty(go);
        }
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
