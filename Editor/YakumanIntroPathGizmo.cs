using UnityEditor;
using UnityEngine;
using SuzuFactory.AozoraMahjong;

/// <summary>
/// 導入の牌がどう動くかを Scene ビューに描く。
///
/// 今のコマだけ見ても動きの善し悪しは分からないので、
/// クリップ全体を通した軌道と、途中の姿勢（ゴースト）をまとめて出す。
///
/// クリップのカーブを直接評価している（AnimationClip.SampleAnimation は
/// シーンを書き換えるので、編集中の姿勢と喧嘩する）。
/// シーンには一切触らないので、いつ描いても安全。
/// </summary>
[InitializeOnLoad]
public static class YakumanIntroPathGizmo
{
    const string MenuPath = "Tools/Kemono Mahjong/Preview Yakuman Tile Paths";
    const string PrefKey = "KemonoMahjong.YakumanIntroPathGizmo";
    const string ClipPath = "Assets/Mahjong/Animations/YakumanIntro.anim";

    /// <summary>軌道を刻む数。増やすと滑らかだが重い。</summary>
    const int PathSamples = 120;

    /// <summary>ゴーストを何サンプルおきに置くか。</summary>
    const int GhostEvery = 10;

    // 参考動画の実測にもとづく段の境目（IntroT）。ここに印を打つ。
    static readonly float[] BeatT = { 0.103f, 0.500f, 0.621f, 0.690f };
    static readonly string[] BeatName = { "滞空へ", "溜めへ", "打ち上げ", "抜け切り" };

    static YakumanIntroPathGizmo()
    {
        SceneView.duringSceneGui -= OnScene;
        SceneView.duringSceneGui += OnScene;
    }

    static bool Enabled
    {
        get { return EditorPrefs.GetBool(PrefKey, false); }
        set { EditorPrefs.SetBool(PrefKey, value); }
    }

    [MenuItem(MenuPath)]
    static void Toggle() { Enabled = !Enabled; SceneView.RepaintAll(); }

    [MenuItem(MenuPath, true)]
    static bool ToggleValidate() { Menu.SetChecked(MenuPath, Enabled); return true; }

    // ---------------------------------------------------------------- 描画

    static void OnScene(SceneView view)
    {
        if (!Enabled) { return; }

        var cutin = FindCutin();
        if (cutin == null || cutin.fxRoot == null) { return; }

        var stage = cutin.fxRoot.transform.Find("Stage");
        var canvas = stage != null ? stage.Find("StageCanvas") : null;
        if (canvas == null) { return; }

        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipPath);
        if (clip == null) { return; }

        int count = cutin.GetIntroTileCount();
        for (int k = 0; k < count; ++k)
        {
            RectTransform rt = RectAt(cutin, k);
            if (rt == null) { continue; }

            string path = RelPath(stage, rt.transform);
            if (path == null) { continue; }

            DrawOne(clip, cutin, canvas, rt, path, k, count);
        }

        DrawBeats(clip, cutin, canvas, RectAt(cutin, 0), stage);
    }

    static void DrawOne(AnimationClip clip, MahjongYakumanCutin cutin, Transform canvas,
        RectTransform rt, string path, int index, int count)
    {
        bool hero = index == 0;
        Color col = hero
            ? new Color(1.00f, 0.82f, 0.25f)
            : Color.HSVToRGB(((index - 1) / (float)Mathf.Max(1, count - 1)) * 0.6f + 0.45f, 0.65f, 1.0f);

        float span = clip.length;
        Vector3 prev = Vector3.zero;
        bool prevOn = false;

        for (int s = 0; s <= PathSamples; ++s)
        {
            float t = (float)s / PathSamples;
            float time = t * span;

            float a = Eval(clip, path, typeof(UnityEngine.UI.RawImage), "m_Color.a", time, 1.0f);
            bool on = a > 0.01f;

            Vector3 lp = new Vector3(
                Eval(clip, path, typeof(RectTransform), "m_AnchoredPosition.x", time, rt.anchoredPosition.x),
                Eval(clip, path, typeof(RectTransform), "m_AnchoredPosition.y", time, rt.anchoredPosition.y),
                Eval(clip, path, typeof(RectTransform), "m_LocalPosition.z", time, rt.localPosition.z));
            Vector3 world = canvas.TransformPoint(lp);

            if (on && prevOn)
            {
                Handles.color = col;
                Handles.DrawAAPolyLine(hero ? 4.0f : 2.0f, prev, world);
            }
            prev = world;
            prevOn = on;

            if (on && (s % GhostEvery == 0))
            {
                DrawGhost(clip, cutin, canvas, rt, path, time, world, col, a);
            }
        }
    }

    /// <summary>その時刻の姿勢と大きさで、牌の面を四角く描く。</summary>
    static void DrawGhost(AnimationClip clip, MahjongYakumanCutin cutin, Transform canvas,
        RectTransform rt, string path, float time, Vector3 world, Color col, float alpha)
    {
        Quaternion lr = new Quaternion(
            Eval(clip, path, typeof(RectTransform), "m_LocalRotation.x", time, rt.localRotation.x),
            Eval(clip, path, typeof(RectTransform), "m_LocalRotation.y", time, rt.localRotation.y),
            Eval(clip, path, typeof(RectTransform), "m_LocalRotation.z", time, rt.localRotation.z),
            Eval(clip, path, typeof(RectTransform), "m_LocalRotation.w", time, rt.localRotation.w));
        if (lr.x == 0 && lr.y == 0 && lr.z == 0 && lr.w == 0) { lr = Quaternion.identity; }
        lr.Normalize();

        Quaternion wr = canvas.rotation * lr;

        float sx = Eval(clip, path, typeof(RectTransform), "m_LocalScale.x", time, rt.localScale.x);
        float sy = Eval(clip, path, typeof(RectTransform), "m_LocalScale.y", time, rt.localScale.y);

        // 板の実寸 = UIのサイズ × 自身の拡縮 × Canvas の世界スケール。
        // 奥行きで小さく見えるぶんは Scene ビューの遠近が付ける（実行時と同じ）。
        float cs = canvas.lossyScale.x;
        float hw = rt.sizeDelta.x * 0.5f * sx * cs;
        float hh = rt.sizeDelta.y * 0.5f * sy * cs;

        Vector3 right = wr * Vector3.right * hw;
        Vector3 up = wr * Vector3.up * hh;

        var verts = new Vector3[4];
        verts[0] = world - right - up;
        verts[1] = world + right - up;
        verts[2] = world + right + up;
        verts[3] = world - right + up;

        var face = new Color(col.r, col.g, col.b, 0.10f * alpha);
        var line = new Color(col.r, col.g, col.b, 0.55f * alpha);
        Handles.DrawSolidRectangleWithOutline(verts, face, line);
    }

    /// <summary>段の境目に印と名前を出す。どこで何が起きるか一目で分かるように。</summary>
    static void DrawBeats(AnimationClip clip, MahjongYakumanCutin cutin, Transform canvas,
        RectTransform hero, Transform stage)
    {
        if (hero == null) { return; }
        string path = RelPath(stage, hero.transform);
        if (path == null) { return; }

        var style = new GUIStyle(EditorStyles.miniLabel);
        style.normal.textColor = Color.white;

        for (int i = 0; i < BeatT.Length; ++i)
        {
            float time = BeatT[i] * clip.length;
            Vector3 lp = new Vector3(
                Eval(clip, path, typeof(RectTransform), "m_AnchoredPosition.x", time, hero.anchoredPosition.x),
                Eval(clip, path, typeof(RectTransform), "m_AnchoredPosition.y", time, hero.anchoredPosition.y),
                Eval(clip, path, typeof(RectTransform), "m_LocalPosition.z", time, hero.localPosition.z));
            Vector3 world = canvas.TransformPoint(lp);

            Handles.color = Color.white;
            float size = HandleUtility.GetHandleSize(world) * 0.08f;
            Handles.SphereHandleCap(0, world, Quaternion.identity, size, EventType.Repaint);
            Handles.Label(world + Vector3.up * size * 3.0f,
                BeatName[i] + "  " + time.ToString("F2") + "s / " + Mathf.RoundToInt(time * clip.frameRate) + "f",
                style);
        }
    }

    // ---------------------------------------------------------------- 小物

    /// <summary>カーブがあれば評価、無ければ今の値をそのまま使う。</summary>
    static float Eval(AnimationClip clip, string path, System.Type type, string prop,
        float time, float fallback)
    {
        var binding = EditorCurveBinding.FloatCurve(path, type, prop);
        var curve = AnimationUtility.GetEditorCurve(clip, binding);
        if (curve == null || curve.length == 0) { return fallback; }
        return curve.Evaluate(time);
    }

    static RectTransform RectAt(MahjongYakumanCutin c, int index)
    {
        if (index == 0) { return c.winningTileRect; }
        int i = index - 1;
        if (c.cometTileRects == null || i >= c.cometTileRects.Length) { return null; }
        return c.cometTileRects[i];
    }

    /// <summary>Animator のルート（Stage）から見た相対パス。カーブの path と同じ形。</summary>
    static string RelPath(Transform root, Transform t)
    {
        if (root == null || t == null) { return null; }
        string p = "";
        var cur = t;
        while (cur != null && cur != root)
        {
            p = p.Length == 0 ? cur.name : cur.name + "/" + p;
            cur = cur.parent;
        }
        return cur == root ? p : null;
    }

    static MahjongYakumanCutin FindCutin()
    {
        if (Selection.activeTransform != null)
        {
            var t = Selection.activeTransform;
            while (t != null)
            {
                var c = t.GetComponentInChildren<MahjongYakumanCutin>(true);
                if (c != null) { return c; }
                t = t.parent;
            }
        }
        foreach (var c in Object.FindObjectsOfType<MahjongYakumanCutin>(true)) { return c; }
        return null;
    }
}
