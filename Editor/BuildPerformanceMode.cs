#if UNITY_EDITOR
// 軽量モード（performancemode_switch）の設置と対象の自動収集。
//   Tools > Kemono Mahjong > Build Performance Mode    … スイッチへ PerformanceModeController を設置し、
//                                                        消す対象を実測ルールで自動収集して配線する（冪等）。
//   Tools > Kemono Mahjong > Validate Performance Mode … 参照・状態の検算のみ（変更なし）。
//
// 【なぜ自動収集か】
//   「重いもの」を人が名前で選ぶと、装飾を足すたびに配線が腐る。ここでは
//   「使っているシェーダーが何か」＝実際のGPUコストの根拠でしか拾わないので、
//   AetherLoft に草木や街灯を足しても Build を回し直せば追従する。
//
// 【収集ルール（2026-08-20 の実測に基づく）】
//   1) 葉/草/花/茂み/木 … マテリアルのシェーダーが FoliageShaders のいずれか。
//      PlantWind/GrassWind/BushWind は Cull Off ＋ clip() のアルファテスト。
//      Quest のタイル型GPUでは早期Z棄却が効かず、重なった葉の枚数だけ塗り直しになる
//      ＝「葉にカメラを向けるとFPSが落ちる」の主犯。三角形数の問題ではない。
//      木の幹(BarkDetail)も同じ木の一部として一緒に拾う＝葉だけ消えて幹が浮くのを防ぐ。
//   2) パーティクル … ParticleSystem を持つ GameObject。
//      LampParticle は Particles/Standard Unlit（モバイル版ではない）× maxParticles 1000 が10基。
//   3) 半透明ガラス … renderQueue >= 3000 のマテリアルを持つレンダラー（パーティクル除く）。
//      Standard / Silent/Filamented の半透明。枚数は少ないが近づくと画面を覆う。
//
// 【触らないもの】
//   探索範囲は AetherLoft 配下だけに固定する。卓(KemonoMahjong)・UI・ネームプレートは
//   ゲーム進行に必要なので絶対に消さない。卓の描画は TableProximityCuller が別途面倒を見る。
//   地面・岩・家などの不透明構造物も、消すと世界が壊れる割に塗り負荷が低いので対象外。
//
// 【影について】
//   このシーンは Directional Light が shadows=None、点光源は全て Baked ＝リアルタイム影が無い。
//   「軽量モードで影を切る」系の処理は効果ゼロなので入れていない（入れると効いた気になるだけ無害でない）。
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using SuzuFactory.AozoraMahjong;
using VRC.Udon;

public static class BuildPerformanceMode
{
    // スイッチ本体（シーン直下）。AetherLoft 付属の汎用スイッチを流用したもの。
    const string SwitchName = "performancemode_switch";
    // ランプ。汎用スイッチが持っていた子をそのまま流用する。
    const string OnIndicatorName = "Effect_on";
    const string OffIndicatorName = "Effect_off";
    // 収集の探索範囲。ここより外は絶対に触らない。
    const string SearchRootName = "AetherLoft";

    const string ScriptPath = "Assets/Mahjong/Sources/PerformanceModeController.cs";
    const string ProgramAssetPath = "Assets/Mahjong/scene_UdonProgramSources/PerformanceModeController Udon C# Program Asset.asset";

    const string InteractText = "軽量モード / Performance Mode";
    const float InteractProximity = 2.0f;

    // 葉/草/花/茂み/木として扱うシェーダー。
    static readonly string[] FoliageShaders =
    {
        "UdonCatWorks/PlantWind",
        "UdonCatWorks/GrassWind",
        "UdonCatWorks/BushWind",
        "UdonCatWorks/BarkDetail",
    };

    // 半透明とみなす境界。Unity の Transparent キューの先頭。
    const int TransparentQueue = 3000;

    // ---- 案内看板 ----
    // スイッチは桟橋(Sanbashi)に埋まる形で付いていて、しかもスポーン地点の真後ろにある。
    // 案内が無いと存在に気づかれないので、スイッチの真上に World Space Canvas の看板を立てる。
    // 看板はスイッチの子＝スイッチを動かせば一緒に付いてくる。
    const string LabelRootName = "Label";
    // 日本語フォント。Unity 既定の Arial は日本語が豆腐になるので必ず指定する。
    const string FontPath = "Assets/Mahjong/Fonts/NotoSansJP-Bold.ttf";
    // スイッチ原点から見た看板の位置。
    // 高さは手すりで決まる：桟橋(Sanbashi)の最上端が y=0.83 で、看板をスイッチのすぐ上
    // （中心 y=0.84）に置くと下半分が手すりの梁に食われて本文が読めない（キャプチャで確認済み）。
    // 看板の高さ 0.18m なので、下端が 0.88 に来る中心 y=0.97 ＝ローカル 0.30 まで上げる。
    // z を少し手前(+Z)に出すのは桟橋の面とのZファイト回避。
    static readonly Vector3 LabelLocalPos = new Vector3(0f, 0.30f, 0.03f);
    // Canvas は 800x360 の仮想解像度。実寸 = px * LabelScale ＝ 0.40m x 0.18m。
    const float LabelScale = 0.0005f;
    static readonly Vector2 LabelSize = new Vector2(800f, 360f);
    const string LabelTitleJa = "軽量モード";
    const string LabelTitleEn = "Performance Mode";
    const string LabelBody = "押すと草木や光の演出が消えて軽くなります\n（変わるのは自分の画面だけ）";

    // ============================================================
    // Build
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Build Performance Mode")]
    public static void Build()
    {
        var switchGo = GameObject.Find(SwitchName);
        if (switchGo == null)
        {
            Debug.LogError("[BuildPerformanceMode] シーンに " + SwitchName + " がありません。スイッチを置いてから実行してください。");
            return;
        }

        var searchRoot = GameObject.Find(SearchRootName);
        if (searchRoot == null)
        {
            Debug.LogError("[BuildPerformanceMode] シーンに " + SearchRootName + " がありません（収集範囲が特定できません）。");
            return;
        }

        EnsureProgramAsset(ScriptPath, ProgramAssetPath);

        // 汎用スイッチ(UdonCat_Switch)が残っていると、こちらと二重に SetActive を撃ち合う。
        // 元の target_object は空（未配線）なので、消して困るものは無い。
        RemoveLegacySwitch(switchGo);

        var comp = EnsureUSharpComponent(switchGo, typeof(PerformanceModeController)) as PerformanceModeController;
        if (comp == null) { return; }

        comp.onIndicator = FindChild(switchGo.transform, OnIndicatorName);
        comp.offIndicator = FindChild(switchGo.transform, OffIndicatorName);
        if (comp.onIndicator == null || comp.offIndicator == null)
        {
            Debug.LogWarning("[BuildPerformanceMode] ランプ（" + OnIndicatorName + " / " + OffIndicatorName + "）が見つかりません。ON/OFFの見分けが付かなくなります。");
        }

        BuildSign(switchGo.transform);

        var foliage = CollectFoliage(searchRoot.transform);
        var particles = CollectParticles(searchRoot.transform);
        var glass = CollectGlass(searchRoot.transform);

        comp.foliageRenderers = foliage;
        comp.particleObjects = particles;
        comp.glassRenderers = glass;
        if (comp.extraRenderers == null) { comp.extraRenderers = new Renderer[0]; }

        // シーンの保存状態は必ず「通常品質」にしておく。検証中に切ったまま保存すると
        // 軽量モードを一度も押していない人にまで消えて見える。
        RestoreVisible(foliage, particles, glass, comp.extraRenderers);

        // インタラクト表示は UdonBehaviour 側が持つ（U#プロキシからは書けない）。
        var ub = UdonSharpEditorUtility.GetBackingUdonBehaviour(comp);
        if (ub != null)
        {
            var so = new SerializedObject(ub);
            var textProp = so.FindProperty("interactText");
            if (textProp != null) { textProp.stringValue = InteractText; }
            var proxProp = so.FindProperty("proximity");
            if (proxProp != null) { proxProp.floatValue = InteractProximity; }
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        UdonSharpEditorUtility.CopyProxyToUdon(comp);
        EditorUtility.SetDirty(comp);
        MarkDirty(switchGo);

        Debug.Log("[BuildPerformanceMode] 完了：葉/草/木 " + foliage.Length +
                  " レンダラー、パーティクル " + particles.Length +
                  " 基、半透明ガラス " + glass.Length + " レンダラーを軽量モードの対象にしました。");
        Validate();
    }

    // ============================================================
    // Validate（変更なし）
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Validate Performance Mode")]
    public static void Validate()
    {
        var switchGo = GameObject.Find(SwitchName);
        if (switchGo == null)
        {
            Debug.LogError("[ValidatePerformanceMode] " + SwitchName + " がシーンにありません。");
            return;
        }

        var legacy = switchGo.GetComponent("UdonCat_Switch");
        if (legacy != null)
        {
            Debug.LogError("[ValidatePerformanceMode] 旧 UdonCat_Switch が残っています。Build Performance Mode を実行してください。");
        }

        var comp = switchGo.GetComponent<PerformanceModeController>();
        if (comp == null)
        {
            Debug.LogError("[ValidatePerformanceMode] PerformanceModeController が付いていません。Build Performance Mode を実行してください。");
            return;
        }
        if (UdonSharpEditorUtility.GetBackingUdonBehaviour(comp) == null)
        {
            Debug.LogError("[ValidatePerformanceMode] backing UdonBehaviour がありません（ビルドに乗りません）。Build を実行し直してください。");
        }

        int errors = 0;
        errors += CheckRenderers("葉/草/木", comp.foliageRenderers);
        errors += CheckRenderers("半透明ガラス", comp.glassRenderers);
        errors += CheckRenderers("追加枠", comp.extraRenderers);
        errors += CheckObjects("パーティクル", comp.particleObjects);

        if (comp.onIndicator == null || comp.offIndicator == null)
        {
            Debug.LogWarning("[ValidatePerformanceMode] ランプの参照が空です（ON/OFFが見分けられません）。");
        }

        // 案内看板：無い／World Space でない／日本語フォントでない、はどれも実機で気づきにくい。
        var sign = switchGo.transform.Find(LabelRootName);
        if (sign == null)
        {
            Debug.LogWarning("[ValidatePerformanceMode] 案内看板(" + LabelRootName + ")がありません。Build を実行してください。");
        }
        else
        {
            var canvas = sign.GetComponent<Canvas>();
            if (canvas == null || canvas.renderMode != RenderMode.WorldSpace)
            {
                Debug.LogError("[ValidatePerformanceMode] 看板の Canvas が World Space ではありません（VRChatでは描画されません）。");
                errors++;
            }
            foreach (var t in sign.GetComponentsInChildren<Text>(true))
            {
                if (t.font == null || t.font.name != "NotoSansJP-Bold")
                {
                    Debug.LogWarning("[ValidatePerformanceMode] 看板 " + t.name + " のフォントが NotoSansJP-Bold ではありません（日本語が豆腐になります）。");
                }
            }
        }

        // 未収集の取りこぼしを検出：探索範囲に、対象に入っていない葉シェーダーが残っていないか。
        var searchRoot = GameObject.Find(SearchRootName);
        if (searchRoot != null)
        {
            var expected = CollectFoliage(searchRoot.transform);
            if (comp.foliageRenderers == null || expected.Length != comp.foliageRenderers.Length)
            {
                Debug.LogWarning("[ValidatePerformanceMode] 葉/草/木の収集数がシーンと食い違います（配線 " +
                                 (comp.foliageRenderers != null ? comp.foliageRenderers.Length : 0) +
                                 " / シーン " + expected.Length + "）。装飾を足したなら Build を実行し直してください。");
            }
        }

        Debug.Log("[ValidatePerformanceMode] 検算完了：葉/草/木 " + Count(comp.foliageRenderers) +
                  " / パーティクル " + Count(comp.particleObjects) +
                  " / ガラス " + Count(comp.glassRenderers) +
                  " / 追加 " + Count(comp.extraRenderers) +
                  "、エラー " + errors + " 件。");
    }

    // ============================================================
    // 案内看板
    // ============================================================
    /// <summary>
    /// スイッチの真上に「何のボタンか」を示す World Space Canvas の看板を冪等生成する。
    /// VRChat では Screen Space Overlay は描画されないので World Space 固定。
    /// 押す物ではないので GraphicRaycaster は付けない（当たり判定と負荷の無駄）。
    /// 日英を1枚に併記する＝JA_/EN_ の出し分け（両方activeで重なる事故が過去にある）に依存しない。
    /// </summary>
    static void BuildSign(Transform switchRoot)
    {
        var font = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
        if (font == null)
        {
            Debug.LogWarning("[BuildPerformanceMode] フォントが見つかりません: " + FontPath + " （日本語が豆腐になります）");
        }

        var root = EnsureUiChild(switchRoot, LabelRootName);
        root.layer = switchRoot.gameObject.layer;
        var rt = (RectTransform)root.transform;
        rt.localPosition = LabelLocalPos;
        // Canvas は「forward と同じ向きに見た時」に文字が読める＝読ませたい面と逆を向ける。
        // プレイヤーはスポーン(-15,0,0.5)側＝+Z からスイッチを見るので、看板は -Z を向ける。
        // identity のままだと実機でも鏡文字になる（キャプチャで確認済み）。
        rt.localRotation = Quaternion.Euler(0f, 180f, 0f);
        rt.localScale = Vector3.one * LabelScale;
        rt.sizeDelta = LabelSize;
        rt.pivot = new Vector2(0.5f, 0.5f);

        var canvas = EnsureComponent<Canvas>(root);
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = null;
        // 押す物ではないので Raycaster は付けない。既に付いていたら剥がす。
        var caster = root.GetComponent<GraphicRaycaster>();
        if (caster != null) { Object.DestroyImmediate(caster); }

        // レイアウト変更時の遺物を掃除して冪等に。
        CleanupChildren(rt, new string[] { "Bg", "TitleJa", "TitleEn", "Body" });

        // 背景。空を背にしても文字が読めるように暗い板を敷く。
        var bg = EnsureUiChild(rt, "Bg");
        bg.layer = root.layer;
        var bgRt = (RectTransform)bg.transform;
        bgRt.anchorMin = Vector2.zero;
        bgRt.anchorMax = Vector2.one;
        bgRt.offsetMin = Vector2.zero;
        bgRt.offsetMax = Vector2.zero;
        bgRt.localScale = Vector3.one;
        var img = EnsureComponent<Image>(bg);
        img.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        img.type = Image.Type.Sliced;
        img.color = new Color(0.07f, 0.09f, 0.13f, 0.86f);
        img.raycastTarget = false;

        // 縦位置と文字サイズは Text.preferredWidth/Height の実測で詰めてある（枠は溢れ防止の目安）。
        // 本文40px は幅800px＝背景板(760px)からはみ出したので36pxへ。大見出し96pxも縦に溢れたので88pxへ。
        MakeText(rt, "TitleJa", LabelTitleJa, font, 88, FontStyle.Bold,
                 new Vector2(0f, 102f), new Vector2(760f, 130f), Color.white);
        MakeText(rt, "TitleEn", LabelTitleEn, font, 46, FontStyle.Normal,
                 new Vector2(0f, -10f), new Vector2(760f, 70f), new Color(0.70f, 0.84f, 1f, 1f));
        MakeText(rt, "Body", LabelBody, font, 36, FontStyle.Normal,
                 new Vector2(0f, -111f), new Vector2(760f, 110f), new Color(0.84f, 0.87f, 0.92f, 1f));
    }

    static void MakeText(RectTransform parent, string name, string body, Font font,
                         int fontSize, FontStyle style, Vector2 pos, Vector2 size, Color color)
    {
        var go = EnsureUiChild(parent, name);
        go.layer = parent.gameObject.layer;
        var rt = (RectTransform)go.transform;
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        rt.localScale = Vector3.one;
        rt.localRotation = Quaternion.identity;

        var text = EnsureComponent<Text>(go);
        if (font != null) { text.font = font; }
        text.text = body;
        text.fontSize = fontSize;
        text.fontStyle = style;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = color;
        text.raycastTarget = false;
        // 収まらない時は縮めず溢れさせる（縮むと読めなくなる方が困る）。
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.resizeTextForBestFit = false;
    }

    static T EnsureComponent<T>(GameObject go) where T : Component
    {
        var c = go.GetComponent<T>();
        return c != null ? c : go.AddComponent<T>();
    }

    static GameObject EnsureUiChild(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null)
        {
            if (t.GetComponent<RectTransform>() != null) { return t.gameObject; }
            Object.DestroyImmediate(t.gameObject); // RectTransform 以外は作り直す
        }
        var go = new GameObject(name);
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
            if (!keep.Contains(c.name)) { Object.DestroyImmediate(c.gameObject); }
        }
    }

    // ============================================================
    // 収集
    // ============================================================
    static Renderer[] CollectFoliage(Transform root)
    {
        var list = new System.Collections.Generic.List<Renderer>();
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (r is ParticleSystemRenderer) { continue; }
            if (!UsesFoliageShader(r)) { continue; }
            list.Add(r);
        }
        return SortByPath(list);
    }

    static GameObject[] CollectParticles(Transform root)
    {
        var list = new System.Collections.Generic.List<GameObject>();
        foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(true))
        {
            if (!list.Contains(ps.gameObject)) { list.Add(ps.gameObject); }
        }
        list.Sort((a, b) => string.CompareOrdinal(GetPath(a.transform), GetPath(b.transform)));
        return list.ToArray();
    }

    static Renderer[] CollectGlass(Transform root)
    {
        var list = new System.Collections.Generic.List<Renderer>();
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (r is ParticleSystemRenderer) { continue; }
            if (UsesFoliageShader(r)) { continue; } // 葉は葉のカテゴリで扱う
            bool transparent = false;
            foreach (var m in r.sharedMaterials)
            {
                if (m == null) { continue; }
                if (m.renderQueue >= TransparentQueue) { transparent = true; }
            }
            if (transparent) { list.Add(r); }
        }
        return SortByPath(list);
    }

    static bool UsesFoliageShader(Renderer r)
    {
        foreach (var m in r.sharedMaterials)
        {
            if (m == null || m.shader == null) { continue; }
            foreach (var name in FoliageShaders)
            {
                if (m.shader.name == name) { return true; }
            }
        }
        return false;
    }

    static Renderer[] SortByPath(System.Collections.Generic.List<Renderer> list)
    {
        list.Sort((a, b) => string.CompareOrdinal(GetPath(a.transform), GetPath(b.transform)));
        return list.ToArray();
    }

    // ============================================================
    // 検算ヘルパ
    // ============================================================
    static int CheckRenderers(string label, Renderer[] arr)
    {
        if (arr == null) { return 0; }
        int errors = 0;
        for (int i = 0; i < arr.Length; ++i)
        {
            if (arr[i] == null)
            {
                Debug.LogError("[ValidatePerformanceMode] " + label + "[" + i + "] が空です。");
                errors++;
                continue;
            }
            if (!arr[i].enabled)
            {
                Debug.LogWarning("[ValidatePerformanceMode] " + label + "[" + i + "] " + GetPath(arr[i].transform) +
                                 " が非表示のまま保存されています。Build を実行し直して通常品質に戻してください。");
            }
        }
        return errors;
    }

    static int CheckObjects(string label, GameObject[] arr)
    {
        if (arr == null) { return 0; }
        int errors = 0;
        for (int i = 0; i < arr.Length; ++i)
        {
            if (arr[i] == null)
            {
                Debug.LogError("[ValidatePerformanceMode] " + label + "[" + i + "] が空です。");
                errors++;
                continue;
            }
            if (!arr[i].activeSelf)
            {
                Debug.LogWarning("[ValidatePerformanceMode] " + label + "[" + i + "] " + GetPath(arr[i].transform) +
                                 " が非表示のまま保存されています。Build を実行し直して通常品質に戻してください。");
            }
        }
        return errors;
    }

    static int Count(System.Array arr) { return arr != null ? arr.Length : 0; }

    static void RestoreVisible(Renderer[] foliage, GameObject[] particles, Renderer[] glass, Renderer[] extra)
    {
        RestoreRenderers(foliage);
        RestoreRenderers(glass);
        RestoreRenderers(extra);
        foreach (var go in particles)
        {
            if (go != null && !go.activeSelf) { Undo.RecordObject(go, "Restore"); go.SetActive(true); }
        }
    }

    static void RestoreRenderers(Renderer[] arr)
    {
        if (arr == null) { return; }
        foreach (var r in arr)
        {
            if (r != null && !r.enabled) { Undo.RecordObject(r, "Restore"); r.enabled = true; }
        }
    }

    // ============================================================
    // 共通ヘルパ（BuildReachDiscardEffect と同等の実装）
    // ============================================================
    static void RemoveLegacySwitch(GameObject go)
    {
        var legacy = go.GetComponent("UdonCat_Switch");
        if (legacy == null) { return; }

        var proxy = legacy as UdonSharpBehaviour;
        var backing = proxy != null ? UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy) : null;
        Object.DestroyImmediate(legacy);
        if (backing != null) { Object.DestroyImmediate(backing); }
        Debug.Log("[BuildPerformanceMode] 旧 UdonCat_Switch を撤去しました（target_object は未配線だったため実害なし）。");
    }

    static bool EnsureProgramAsset(string scriptPath, string assetPath)
    {
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
        if (script == null)
        {
            Debug.LogError("[BuildPerformanceMode] スクリプトが見つかりません: " + scriptPath);
            return false;
        }
        var type = script.GetClass();
        if (type != null && UdonSharpProgramAsset.GetProgramAssetForClass(type) != null) { return false; }
        if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(assetPath) != null) { return false; }

        var programAsset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
        programAsset.sourceCsScript = script;
        AssetDatabase.CreateAsset(programAsset, assetPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[BuildPerformanceMode] U#プログラムアセットを生成: " + assetPath);
        return true;
    }

    static UdonSharpBehaviour EnsureUSharpComponent(GameObject go, System.Type type)
    {
        var proxy = (UdonSharpBehaviour)go.GetComponent(type);
        if (proxy != null)
        {
            if (UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy) != null) { return proxy; }

            Debug.LogWarning("[BuildPerformanceMode] " + type.Name + " の backing UdonBehaviour が無いため作り直します。");
            Object.DestroyImmediate(proxy);
            foreach (var ub in go.GetComponents<UdonBehaviour>())
            {
                if (ub.programSource == null) { Object.DestroyImmediate(ub); }
            }
        }

        var added = (UdonSharpBehaviour)UdonSharpUndo.AddComponent(go, type);
        if (added != null && UdonSharpEditorUtility.GetBackingUdonBehaviour(added) == null)
        {
            Debug.LogError("[BuildPerformanceMode] " + type.Name + " の追加に失敗（プログラムアセット未コンパイル？）。");
            Object.DestroyImmediate(added);
            return null;
        }
        return added;
    }

    static GameObject FindChild(Transform parent, string name)
    {
        var t = parent.Find(name);
        return t != null ? t.gameObject : null;
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
}
#endif
