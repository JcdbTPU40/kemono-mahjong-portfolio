using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.UI;
using SuzuFactory.AozoraMahjong;

/// <summary>
/// 役満カットインの導入（ステージ側）を Animation Clip へ焼き込む。
///
/// 手で写すと必ずズレるので、<b>今のコードを実際に走らせて値を採取する</b>。
/// MahjongYakumanCutin.Apply(elapsed) を刻んで呼び、Stage 配下の
/// Transform / RawImage / Camera の値を記録してカーブにする。
///
/// 焼いたあとはクリップ側が正になる。数値を変えたいときは
/// Animation ウィンドウでカーブを触ればよく、Build/Wire は要らない。
/// クリップは Prefab 上の Animator が持つので、直せば 8 卓すべてに即反映される。
/// </summary>
public static class BakeYakumanIntroClip
{
    const string ClipDir = "Assets/Mahjong/Animations";
    const string ClipPath = ClipDir + "/YakumanIntro.anim";
    const string ControllerPath = ClipDir + "/YakumanIntro.controller";
    const string BackupDir = ClipDir + "/Backup";
    /// <summary>最後に焼いた直後のクリップの中身。手編集の検出に使う。</summary>
    const string StampKey = "KemonoMahjong.YakumanIntroClipStamp";
    const string StateName = "Intro";

    /// <summary>採取の刻み。細かすぎるとキーが増えて手で触れなくなる。</summary>
    const int SampleFps = 60;

    /// <summary>間引きの許容誤差。相対も見るので桁の違う値でも効く。</summary>
    const float TolPosition = 0.35f;      // Canvas 単位
    const float TolRotation = 0.0008f;    // クォータニオンの成分
    const float TolScale = 0.0015f;
    const float TolColor = 0.003f;
    const float TolFov = 0.04f;           // 度
    const float TolRelative = 0.0006f;    // 値域に対する割合

    // ---------------------------------------------------------------- メニュー

    [MenuItem("Tools/Kemono Mahjong/Bake Yakuman Intro Clip")]
    public static void Bake()
    {
        var comp = FindCutin();
        if (comp == null)
        {
            Debug.LogError("[BakeYakumanIntroClip] シーンに MahjongYakumanCutin が見つかりません。"
                + "先に Build / Wire Yakuman Cutin を実行してください。");
            return;
        }

        Transform stage = FindStage(comp);
        if (stage == null)
        {
            Debug.LogError("[BakeYakumanIntroClip] Stage が見つかりません（FxRoot/Stage）。");
            return;
        }

        EnsureFolder(ClipDir);

        // 焼き込みはクリップを丸ごと作り直す破壊的操作。
        // 手で編集したカーブは残らないので、必ず控えを取り、
        // 前回の焼き込み以降に変わっていたら確認する。
        if (!ConfirmOverwrite()) { return; }

        var targets = CollectTargets(stage);
        Debug.Log("[BakeYakumanIntroClip] 対象 " + targets.Count + " 個を採取します。");

        var samples = Sample(comp, stage, targets);
        if (samples == null) { return; }

        var clip = BuildClip(targets, samples, comp.sparkSeconds);
        AssetDatabase.CreateAsset(clip, ClipPath);
        AssetDatabase.SaveAssets();

        EnsureController(clip);
        RememberStamp();

        int curves = AnimationUtility.GetCurveBindings(clip).Length;
        int keys = 0;
        foreach (var b in AnimationUtility.GetCurveBindings(clip))
        {
            keys += AnimationUtility.GetEditorCurve(clip, b).length;
        }
        Debug.Log("[BakeYakumanIntroClip] 焼き込み完了: " + ClipPath
            + " / カーブ " + curves + " 本 / キー " + keys + " 個 / 長さ "
            + comp.sparkSeconds.ToString("F2") + "秒");
    }

    /// <summary>
    /// 上書き前の安全確認。手編集を消したことがあるので必ず通す。
    /// ・現在のクリップを Backup/ へ日時つきで複製する
    /// ・前回の焼き込み以降に中身が変わっていたら、消してよいか尋ねる
    /// </summary>
    static bool ConfirmOverwrite()
    {
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipPath);
        if (clip == null) { return true; }   // まだ無いなら消すものが無い

        string now = Fingerprint(clip);
        string stamp = EditorPrefs.GetString(StampKey, "");
        bool edited = stamp.Length > 0 && stamp != now;

        if (edited)
        {
            bool go = EditorUtility.DisplayDialog(
                "役満クリップを焼き直す",
                "前回の焼き込み以降にクリップが変更されています。\n"
                + "焼き直すと、手で編集したカーブは残りません。\n\n"
                + "続けますか？（控えは " + BackupDir + " に残ります）",
                "焼き直す", "やめる");
            if (!go) { return false; }
        }

        EnsureFolder(BackupDir);
        string dst = BackupDir + "/YakumanIntro_"
            + System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".anim";
        if (AssetDatabase.CopyAsset(ClipPath, dst))
        {
            Debug.Log("[BakeYakumanIntroClip] 上書き前の控え: " + dst);
        }
        return true;
    }

    /// <summary>クリップの中身の指紋。カーブ本数とキーの数・値から作る。</summary>
    static string Fingerprint(AnimationClip clip)
    {
        var bindings = AnimationUtility.GetCurveBindings(clip);
        int keys = 0;
        double sum = 0.0;
        foreach (var b in bindings)
        {
            var c = AnimationUtility.GetEditorCurve(clip, b);
            if (c == null) { continue; }
            keys += c.length;
            for (int i = 0; i < c.length; ++i) { sum += c[i].time * 31.0 + c[i].value; }
        }
        return bindings.Length + ":" + keys + ":" + sum.ToString("F3");
    }

    /// <summary>焼いた直後の状態を覚える。次回この指紋と違えば手編集とみなす。</summary>
    static void RememberStamp()
    {
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipPath);
        if (clip != null) { EditorPrefs.SetString(StampKey, Fingerprint(clip)); }
    }

    /// <summary>焼いたクリップが元のコードと一致しているか、再サンプリングして確かめる。</summary>
    [MenuItem("Tools/Kemono Mahjong/Validate Yakuman Intro Clip")]
    public static void Validate()
    {
        Debug.Log(ValidateReport());
    }

    /// <summary>
    /// 検証してプロジェクトの Temp/ へ書き出す。
    /// 長くかかると呼び出し側が結果を取り逃がすので、ファイルに残す。
    /// </summary>
    public static void ValidateToFile()
    {
        string text;
        try { text = ValidateReport(); }
        catch (System.Exception e) { text = "THREW: " + e; }
        string path = Application.dataPath + "/../Temp/yakuman_validate.txt";
        System.IO.File.WriteAllText(path, text, System.Text.Encoding.UTF8);
        Debug.Log(text);
    }

    /// <summary>検証結果を文字列で返す（Console を経由せず結果を受け取りたいとき用）。</summary>
    public static string ValidateReport()
    {
        var comp = FindCutin();
        Transform stage = comp != null ? FindStage(comp) : null;
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipPath);
        if (comp == null || stage == null || clip == null)
        {
            return "[BakeYakumanIntroClip] 検証できません（卓 / Stage / クリップのいずれかが無い）。";
        }

        var targets = CollectTargets(stage);
        var saved = SaveState(targets);
        var guard = BeginBakeGuard(comp);

        MethodInfo cache = Method(comp, "CacheLayout");
        MethodInfo apply = Method(comp, "Apply");
        if (cache == null || apply == null)
        {
            EndBakeGuard(comp, guard);
            RestoreState(targets, saved);
            return "[BakeYakumanIntroClip] Apply / CacheLayout が見つかりません。";
        }
        ForceRecacheLayout(comp, cache);

        float sparkStart = Mathf.Max(0.0f, comp.darkenSeconds) * 0.55f;
        float span = Mathf.Max(0.0001f, comp.sparkSeconds);

        float worstPos = 0.0f, worstRot = 0.0f, worstScale = 0.0f, worstCol = 0.0f, worstFov = 0.0f;
        string worstWhere = "-";
        string worstRotWhere = "-";
        string worstScaleWhere = "-";
        string worstColWhere = "-";

        // 13点だと入りの区間（値が急に動く）を跨いでしまい、
        // 実際に最大1258もずれていたのを素通りした。
        // 多すぎるとメインスレッドが止まるので、その手前で刻む。
        const int Checks = 41;
        for (int k = 0; k < Checks; ++k)
        {
            float t = (float)k / (Checks - 1);

            // 毎回もとの姿勢へ戻してから測る。
            // Apply は範囲外や非表示のとき「何も書かない」ので、戻さないと
            // 直前にクリップが書いた値が残り、それをコード側の値として比べてしまう。
            RestoreState(targets, saved);
            apply.Invoke(comp, new object[] { sparkStart + t * span });
            var codeState = SaveState(targets);

            RestoreState(targets, saved);
            clip.SampleAnimation(stage.gameObject, t * span);
            var clipState = SaveState(targets);

            for (int i = 0; i < targets.Count; ++i)
            {
                var a = codeState[i];
                var b = clipState[i];
                float dp = (a.pos - b.pos).magnitude;
                // q と -q は同じ回転。成分で比べると符号反転で 2.0 になるので角度で測る。
                float dr = Quaternion.Angle(a.rot, b.rot);
                float ds = (a.scale - b.scale).magnitude;
                float dc = Mathf.Max(Mathf.Abs(a.color.r - b.color.r),
                           Mathf.Max(Mathf.Abs(a.color.g - b.color.g),
                           Mathf.Max(Mathf.Abs(a.color.b - b.color.b), Mathf.Abs(a.color.a - b.color.a))));
                float df = Mathf.Abs(a.fov - b.fov);

                // 見えていないコマの姿勢はどこにあっても構わないので、
                // 両方とも透明なものは位置・回転・拡縮を数えない。
                bool visible = targets[i].raw == null || a.color.a > 0.004f || b.color.a > 0.004f;

                if (visible)
                {
                    if (dp > worstPos) { worstPos = dp; worstWhere = targets[i].path + " @t=" + t.ToString("F2"); }
                    if (dr > worstRot) { worstRot = dr; worstRotWhere = targets[i].path + " @t=" + t.ToString("F2"); }
                    if (ds > worstScale) { worstScale = ds; worstScaleWhere = targets[i].path + " @t=" + t.ToString("F2"); }
                }
                if (dc > worstCol) { worstCol = dc; worstColWhere = targets[i].path + " @t=" + t.ToString("F2"); }
                if (df > worstFov) { worstFov = df; }
            }
        }

        EndBakeGuard(comp, guard);
        RestoreState(targets, saved);

        return "[BakeYakumanIntroClip] 検証（コード vs クリップ・" + Checks + "点）\n"
            + "  位置 最大差 " + worstPos.ToString("F3") + " Canvas単位（" + worstWhere + "）\n"
            + "  回転 最大差 " + worstRot.ToString("F3") + " 度（" + worstRotWhere + "）\n"
            + "  拡縮 最大差 " + worstScale.ToString("F5") + "（" + worstScaleWhere + "）\n"
            + "  色   最大差 " + worstCol.ToString("F5") + "（" + worstColWhere + "）\n"
            + "  画角 最大差 " + worstFov.ToString("F4") + " 度";
    }

    // ---------------------------------------------------------------- 対象集め

    class Target
    {
        public string path;
        public Transform tr;
        public RectTransform rect;      // null なら素の Transform
        public RawImage raw;            // null なら色は焼かない
        public Camera cam;              // null なら画角は焼かない
    }

    struct State
    {
        public Vector3 pos;             // rect なら (anchored.x, anchored.y, localPosition.z)
        public Quaternion rot;
        public Vector3 scale;
        public Color color;
        public float fov;
    }

    static List<Target> CollectTargets(Transform stage)
    {
        var list = new List<Target>();
        Collect(stage, stage, "", list);
        return list;
    }

    static void Collect(Transform root, Transform t, string path, List<Target> list)
    {
        if (t != root)
        {
            var tg = new Target();
            tg.path = path;
            tg.tr = t;
            tg.rect = t as RectTransform;
            tg.raw = t.GetComponent<RawImage>();
            tg.cam = t.GetComponent<Camera>();
            list.Add(tg);
        }

        for (int i = 0; i < t.childCount; ++i)
        {
            var c = t.GetChild(i);
            string p = path.Length == 0 ? c.name : path + "/" + c.name;
            Collect(root, c, p, list);
        }
    }

    static State[] SaveState(List<Target> targets)
    {
        var arr = new State[targets.Count];
        for (int i = 0; i < targets.Count; ++i)
        {
            var g = targets[i];
            var s = new State();
            if (g.rect != null)
            {
                Vector2 a = g.rect.anchoredPosition;
                s.pos = new Vector3(a.x, a.y, g.rect.localPosition.z);
            }
            else
            {
                s.pos = g.tr.localPosition;
            }
            s.rot = g.tr.localRotation;
            s.scale = g.tr.localScale;
            s.color = g.raw != null ? g.raw.color : Color.white;
            s.fov = g.cam != null ? g.cam.fieldOfView : 0.0f;
            arr[i] = s;
        }
        return arr;
    }

    static void RestoreState(List<Target> targets, State[] arr)
    {
        for (int i = 0; i < targets.Count; ++i)
        {
            var g = targets[i];
            var s = arr[i];
            if (g.rect != null)
            {
                g.rect.anchoredPosition = new Vector2(s.pos.x, s.pos.y);
                var lp = g.rect.localPosition; lp.z = s.pos.z; g.rect.localPosition = lp;
            }
            else
            {
                g.tr.localPosition = s.pos;
            }
            g.tr.localRotation = s.rot;
            g.tr.localScale = s.scale;
            if (g.raw != null) { g.raw.color = s.color; }
            if (g.cam != null) { g.cam.fieldOfView = s.fov; }
        }
    }

    // ---------------------------------------------------------------- 採取

    /// <summary>
    /// 採取のあいだだけ状態を差し替える。
    /// ・useRealIntroTiles を false … true だと UI の色が 0 に潰され、
    ///   3D牌の表示判定に使う本来の可視度がクリップに残らない。
    /// ・バーストと音を切る … エディタで鳴ってしまうため。
    /// ・テクスチャ未設定の RawImage に仮の絵を入れる … Apply が早期リターンするため。
    /// </summary>
    class Guard
    {
        public Animator introAnimator;   // アニメ駆動のままだとコード側の振り付けが測れない
        public bool realTiles;
        public ParticleSystem launch;
        public ParticleSystem flash;
        public AudioSource se;
        public List<RawImage> texPatched = new List<RawImage>();
    }

    static Guard BeginBakeGuard(MahjongYakumanCutin comp)
    {
        var g = new Guard();
        g.introAnimator = comp.introAnimator;
        comp.introAnimator = null;
        g.realTiles = comp.useRealIntroTiles;
        g.launch = comp.launchBurst;
        g.flash = comp.flashBurst;
        g.se = comp.seSource;

        comp.useRealIntroTiles = false;
        comp.launchBurst = null;
        comp.flashBurst = null;
        comp.seSource = null;

        Texture stand = comp.tileAtlas;
        if (stand != null)
        {
            PatchTexture(comp.winningTileImage, stand, g);
            PatchTexture(comp.mascotImage, stand, g);
            PatchTexture(comp.winTileGlowImage, stand, g);
            PatchTexture(comp.winTileTrailImage, stand, g);
            PatchArray(comp.cometTileImages, stand, g);
            PatchArray(comp.cometGlowImages, stand, g);
            PatchArray(comp.cometTrailImages, stand, g);
            PatchArray(comp.petalImages, stand, g);
            PatchArray(comp.sparkImages, stand, g);
        }
        return g;
    }

    static void PatchTexture(RawImage img, Texture stand, Guard g)
    {
        if (img == null || img.texture != null) { return; }
        img.texture = stand;
        g.texPatched.Add(img);
    }

    static void PatchArray(RawImage[] arr, Texture stand, Guard g)
    {
        if (arr == null) { return; }
        foreach (var img in arr) { PatchTexture(img, stand, g); }
    }

    static void EndBakeGuard(MahjongYakumanCutin comp, Guard g)
    {
        comp.introAnimator = g.introAnimator;
        comp.useRealIntroTiles = g.realTiles;
        comp.launchBurst = g.launch;
        comp.flashBurst = g.flash;
        comp.seSource = g.se;
        foreach (var img in g.texPatched) { if (img != null) { img.texture = null; } }
    }

    /// <summary>採取前の姿勢。一定値でも「静止時と違うなら焼く」判定に使う。</summary>
    static State[] s_rest;

    /// <summary>
    /// CacheLayout をやり直させる。layoutCached_ が立っていると素通りするので、
    /// 演出途中の姿勢で一度キャッシュされていると静止位置が狂ったまま焼いてしまう。
    /// </summary>
    static void ForceRecacheLayout(MahjongYakumanCutin comp, MethodInfo cache)
    {
        var flag = comp.GetType().GetField("layoutCached_",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (flag != null) { flag.SetValue(comp, false); }
        if (cache != null) { cache.Invoke(comp, null); }
    }

    static State[][] Sample(MahjongYakumanCutin comp, Transform stage, List<Target> targets)
    {
        MethodInfo cache = Method(comp, "CacheLayout");
        MethodInfo apply = Method(comp, "Apply");
        if (cache == null || apply == null)
        {
            Debug.LogError("[BakeYakumanIntroClip] Apply / CacheLayout が見つかりません。"
                + "名前が変わっていないか確認してください。");
            return null;
        }

        var saved = SaveState(targets);
        s_rest = saved;
        var guard = BeginBakeGuard(comp);

        // 今の姿勢＝静止位置として取り直す。ここを間違えると全部ずれる。
        ForceRecacheLayout(comp, cache);

        float sparkStart = Mathf.Max(0.0f, comp.darkenSeconds) * 0.55f;
        float span = Mathf.Max(0.0001f, comp.sparkSeconds);
        int count = Mathf.Max(2, Mathf.RoundToInt(span * SampleFps) + 1);

        var frames = new State[count][];
        for (int k = 0; k < count; ++k)
        {
            float t = (float)k / (count - 1);
            apply.Invoke(comp, new object[] { sparkStart + t * span });
            frames[k] = SaveState(targets);
        }

        EndBakeGuard(comp, guard);
        RestoreState(targets, saved);
        return frames;
    }

    // ---------------------------------------------------------------- クリップ作成

    static AnimationClip BuildClip(List<Target> targets, State[][] frames, float span)
    {
        var clip = new AnimationClip();
        clip.name = "YakumanIntro";
        clip.frameRate = SampleFps;
        clip.wrapMode = WrapMode.ClampForever;

        var settings = AnimationUtility.GetAnimationClipSettings(clip);
        settings.loopTime = false;
        AnimationUtility.SetAnimationClipSettings(clip, settings);

        int n = frames.Length;
        var times = new float[n];
        for (int k = 0; k < n; ++k) { times[k] = (float)k / (n - 1) * span; }

        for (int i = 0; i < targets.Count; ++i)
        {
            var g = targets[i];
            System.Type trType = g.rect != null ? typeof(RectTransform) : typeof(Transform);

            // 位置。RectTransform は anchoredPosition + localPosition.z が実体。
            string px = g.rect != null ? "m_AnchoredPosition.x" : "m_LocalPosition.x";
            string py = g.rect != null ? "m_AnchoredPosition.y" : "m_LocalPosition.y";
            AddCurve(clip, g.path, trType, px, times, Pick(frames, i, 0), TolPosition, Rest(i, 0));
            AddCurve(clip, g.path, trType, py, times, Pick(frames, i, 1), TolPosition, Rest(i, 1));
            AddCurve(clip, g.path, trType, "m_LocalPosition.z", times, Pick(frames, i, 2), TolPosition, Rest(i, 2));

            // 回転。4成分そろえて入れる（欠けると Unity が補間できない）。
            bool rotMoves = Varies(frames, i, 3, TolRotation) || Varies(frames, i, 4, TolRotation)
                         || Varies(frames, i, 5, TolRotation) || Varies(frames, i, 6, TolRotation);
            if (rotMoves)
            {
                AddCurve(clip, g.path, trType, "m_LocalRotation.x", times, Pick(frames, i, 3), 0.0f, Rest(i, 3));
                AddCurve(clip, g.path, trType, "m_LocalRotation.y", times, Pick(frames, i, 4), 0.0f, Rest(i, 4));
                AddCurve(clip, g.path, trType, "m_LocalRotation.z", times, Pick(frames, i, 5), 0.0f, Rest(i, 5));
                AddCurve(clip, g.path, trType, "m_LocalRotation.w", times, Pick(frames, i, 6), 0.0f, Rest(i, 6));
            }

            AddCurve(clip, g.path, trType, "m_LocalScale.x", times, Pick(frames, i, 7), TolScale, Rest(i, 7));
            AddCurve(clip, g.path, trType, "m_LocalScale.y", times, Pick(frames, i, 8), TolScale, Rest(i, 8));
            AddCurve(clip, g.path, trType, "m_LocalScale.z", times, Pick(frames, i, 9), TolScale, Rest(i, 9));

            if (g.raw != null)
            {
                AddCurve(clip, g.path, typeof(RawImage), "m_Color.r", times, Pick(frames, i, 10), TolColor, Rest(i, 10));
                AddCurve(clip, g.path, typeof(RawImage), "m_Color.g", times, Pick(frames, i, 11), TolColor, Rest(i, 11));
                AddCurve(clip, g.path, typeof(RawImage), "m_Color.b", times, Pick(frames, i, 12), TolColor, Rest(i, 12));
                AddCurve(clip, g.path, typeof(RawImage), "m_Color.a", times, Pick(frames, i, 13), TolColor, Rest(i, 13));
            }

            if (g.cam != null)
            {
                AddCurve(clip, g.path, typeof(Camera), "field of view", times, Pick(frames, i, 14), TolFov, Rest(i, 14));
            }
        }

        clip.EnsureQuaternionContinuity();
        return clip;
    }

    /// <summary>採取前（静止時）の値。チャンネル番号は Pick と同じ並び。</summary>
    static float Rest(int target, int channel)
    {
        if (s_rest == null || target >= s_rest.Length) { return float.NaN; }
        var one = new State[1][];
        one[0] = new State[] { s_rest[target] };
        return Pick(one, 0, channel)[0];
    }

    static float[] Pick(State[][] frames, int target, int channel)
    {
        var v = new float[frames.Length];
        for (int k = 0; k < frames.Length; ++k)
        {
            var s = frames[k][target];
            switch (channel)
            {
                case 0: v[k] = s.pos.x; break;
                case 1: v[k] = s.pos.y; break;
                case 2: v[k] = s.pos.z; break;
                case 3: v[k] = s.rot.x; break;
                case 4: v[k] = s.rot.y; break;
                case 5: v[k] = s.rot.z; break;
                case 6: v[k] = s.rot.w; break;
                case 7: v[k] = s.scale.x; break;
                case 8: v[k] = s.scale.y; break;
                case 9: v[k] = s.scale.z; break;
                case 10: v[k] = s.color.r; break;
                case 11: v[k] = s.color.g; break;
                case 12: v[k] = s.color.b; break;
                case 13: v[k] = s.color.a; break;
                default: v[k] = s.fov; break;
            }
        }
        return v;
    }

    static bool Varies(State[][] frames, int target, int channel, float tol)
    {
        var v = Pick(frames, target, channel);
        float lo = v[0], hi = v[0];
        for (int k = 1; k < v.Length; ++k)
        {
            if (v[k] < lo) { lo = v[k]; }
            if (v[k] > hi) { hi = v[k]; }
        }
        return (hi - lo) > tol;
    }

    /// <summary>動かないチャンネルはカーブを作らない（クリップが太るだけ）。</summary>
    static void AddCurve(AnimationClip clip, string path, System.Type type, string prop,
        float[] times, float[] values, float tol, float rest)
    {
        float lo = values[0], hi = values[0];
        for (int k = 1; k < values.Length; ++k)
        {
            if (values[k] < lo) { lo = values[k]; }
            if (values[k] > hi) { hi = values[k]; }
        }
        // tol=0 は「動かなくても必ず入れる」（回転の4成分そろえ用）。
        // 動かなくても静止時の値と違うなら焼く。焼かないとクリップが値を書かず、
        // プレハブに保存された値のまま出る（彗星の光暈が白いままになった）。
        bool varies = (hi - lo) > tol;
        bool offRest = Mathf.Abs(lo - rest) > tol || Mathf.Abs(hi - rest) > tol;
        if (tol > 0.0f && !varies && !offRest) { return; }

        float eps = Mathf.Max(tol, Mathf.Abs(hi - lo) * TolRelative);
        var keep = Simplify(times, values, eps);

        var keys = new Keyframe[keep.Count];
        for (int k = 0; k < keep.Count; ++k)
        {
            keys[k] = new Keyframe(times[keep[k]], values[keep[k]]);
        }
        var curve = new AnimationCurve(keys);

        // 接線は Linear に「モードとして」指定する。
        // Keyframe に数値を入れるだけだと Unity が tangentMode を見て上書きし、
        // キーの間で範囲外へ大きく振れる（実際に -461 のはずが -1588 まで行った）。
        // Linear なら間引きの許容誤差がそのまま画面上の誤差になる。
        for (int k = 0; k < curve.length; ++k)
        {
            AnimationUtility.SetKeyLeftTangentMode(curve, k, AnimationUtility.TangentMode.Linear);
            AnimationUtility.SetKeyRightTangentMode(curve, k, AnimationUtility.TangentMode.Linear);
        }
        AnimationUtility.SetEditorCurve(clip,
            EditorCurveBinding.FloatCurve(path, type, prop), curve);
    }

    /// <summary>
    /// 折れ線の間引き（Ramer-Douglas-Peucker）。
    /// 60fps のキーをそのまま残すと Animation ウィンドウで手が付けられない。
    /// </summary>
    static List<int> Simplify(float[] times, float[] values, float eps)
    {
        var keep = new List<int>();
        keep.Add(0);
        Recurse(times, values, 0, values.Length - 1, eps, keep);
        keep.Add(values.Length - 1);
        keep.Sort();
        return keep;
    }

    static void Recurse(float[] times, float[] values, int a, int b, float eps, List<int> keep)
    {
        if (b - a < 2) { return; }

        float t0 = times[a], t1 = times[b];
        float v0 = values[a], v1 = values[b];
        float dt = t1 - t0;

        int worst = -1;
        float worstErr = eps;
        for (int i = a + 1; i < b; ++i)
        {
            float lin = dt > 0.0001f ? Mathf.Lerp(v0, v1, (times[i] - t0) / dt) : v0;
            float err = Mathf.Abs(values[i] - lin);
            if (err > worstErr) { worstErr = err; worst = i; }
        }
        if (worst < 0) { return; }

        keep.Add(worst);
        Recurse(times, values, a, worst, eps, keep);
        Recurse(times, values, worst, b, eps, keep);
    }

    // ---------------------------------------------------------------- Animator

    static void EnsureController(AnimationClip clip)
    {
        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        if (ctrl == null)
        {
            ctrl = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        }

        var sm = ctrl.layers[0].stateMachine;
        AnimatorState state = null;
        foreach (var ch in sm.states)
        {
            if (ch.state != null && ch.state.name == StateName) { state = ch.state; break; }
        }
        if (state == null) { state = sm.AddState(StateName); }

        state.motion = clip;
        // 演出前後で値を持ち越さない。
        state.writeDefaultValues = true;
        state.speed = 1.0f;
        sm.defaultState = state;

        EditorUtility.SetDirty(ctrl);
        AssetDatabase.SaveAssets();
    }

    /// <summary>Stage に Animator を付けて、カットイン側から参照させる。</summary>
    [MenuItem("Tools/Kemono Mahjong/Wire Yakuman Intro Clip")]
    public static void WireAnimators()
    {
        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        if (ctrl == null)
        {
            Debug.LogError("[BakeYakumanIntroClip] コントローラがありません。先に Bake してください。");
            return;
        }

        int done = 0;
        foreach (var comp in Object.FindObjectsOfType<MahjongYakumanCutin>(true))
        {
            Transform stage = FindStage(comp);
            if (stage == null) { continue; }

            var anim = stage.GetComponent<Animator>();
            if (anim == null) { anim = stage.gameObject.AddComponent<Animator>(); }
            anim.runtimeAnimatorController = ctrl;
            anim.applyRootMotion = false;
            anim.updateMode = AnimatorUpdateMode.Normal;
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;   // 300m 下なので必須
            anim.enabled = false;                                    // 演出中だけ有効化する

            comp.introAnimator = anim;
            comp.introStateName = StateName;

            EditorUtility.SetDirty(comp);
            EditorUtility.SetDirty(anim);
            UdonSharpEditor.UdonSharpEditorUtility.CopyProxyToUdon(comp);
            ++done;
        }

        UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();
        Debug.Log("[BakeYakumanIntroClip] Animator を " + done + " 卓へ配線しました。");
    }

    // ---------------------------------------------------------------- 小物

    static MahjongYakumanCutin FindCutin()
    {
        foreach (var c in Object.FindObjectsOfType<MahjongYakumanCutin>(true)) { return c; }
        return null;
    }

    static Transform FindStage(MahjongYakumanCutin comp)
    {
        if (comp == null || comp.fxRoot == null) { return null; }
        return comp.fxRoot.transform.Find("Stage");
    }

    static MethodInfo Method(object o, string name)
    {
        return o.GetType().GetMethod(name,
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) { return; }
        string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
        string leaf = System.IO.Path.GetFileName(path);
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }
}
