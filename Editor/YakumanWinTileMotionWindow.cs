using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using SuzuFactory.AozoraMahjong;

/// <summary>
/// 和了牌の動きを「秒 / 画面のどこ / 大きさ何倍」の表で編集する窓。
///
/// Animation ウィンドウで直接触ると、
///   ・Anchored Position.y と Position.z が別々の行に分かれている
///   ・大きさが z の生の値（-161〜2152）で、何倍になるのか読めない
///   ・キーが20個以上あって、どれが段の境目なのか分からない
/// という三重苦になるので、段の境目だけを表にして扱う。
///
/// 書き込むとカーブは段の数だけのキーに作り直される（触れる数になる）。
/// </summary>
public class YakumanWinTileMotionWindow : EditorWindow
{
    const string ClipPath = "Assets/Mahjong/Animations/YakumanIntro.anim";
    const string TilePath = "StageCanvas/WinningTile";
    const string PropY = "m_AnchoredPosition.y";
    const string PropZ = "m_LocalPosition.z";

    class Beat
    {
        public string name;
        public float t;        // 0〜1
        public float screenY;  // Canvas 単位（上が＋）
        public float scale;    // 見かけの倍率
    }

    // 既定は参考動画の実測にもとづく段の境目。増減しても構わない。
    static readonly string[] DefaultNames =
        { "出現", "中央へ", "滞空おわり", "溜めおわり", "打ち上げ頂点", "終端" };
    static readonly float[] DefaultT =
        { 0.000f, 0.103f, 0.500f, 0.621f, 0.690f, 1.000f };

    List<Beat> beats_ = new List<Beat>();
    AnimationClip clip_;
    MahjongYakumanCutin cutin_;
    Vector2 scroll_;
    string status_ = "";

    [MenuItem("Tools/Kemono Mahjong/和了牌の動き")]
    static void Open()
    {
        var w = GetWindow<YakumanWinTileMotionWindow>("和了牌の動き");
        w.minSize = new Vector2(460f, 300f);
        w.Reload();
    }

    void OnEnable() { Reload(); }

    // ---------------------------------------------------------------- 読み込み

    void Reload()
    {
        clip_ = AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipPath);
        cutin_ = FindCutin();
        beats_.Clear();

        if (clip_ == null || cutin_ == null)
        {
            status_ = "クリップかカットインが見つかりません。";
            return;
        }

        var cy = GetCurve(PropY);
        var cz = GetCurve(PropZ);
        if (cy == null)
        {
            status_ = "和了牌のカーブがありません。先に Bake Yakuman Intro Clip を実行してください。";
            return;
        }

        for (int i = 0; i < DefaultT.Length; ++i)
        {
            float time = DefaultT[i] * clip_.length;
            var b = new Beat();
            b.name = DefaultNames[i];
            b.t = DefaultT[i];
            b.screenY = cy.Evaluate(time);
            b.scale = ScaleFromZ(cz != null ? cz.Evaluate(time) : 0.0f);
            beats_.Add(b);
        }
        status_ = "クリップから読み込みました。";
    }

    // ---------------------------------------------------------------- 画面

    void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "秒＝いつ／画面Y＝画面のどこ（上が＋）／大きさ＝見かけの倍率。\n"
            + "書き込むと和了牌のカーブは段の数だけのキーに作り直されます。",
            MessageType.None);

        if (clip_ == null || cutin_ == null)
        {
            EditorGUILayout.HelpBox(status_, MessageType.Warning);
            if (GUILayout.Button("読み込み直す")) { Reload(); }
            return;
        }

        EditorGUILayout.LabelField("クリップ", clip_.name + "（" + clip_.length.ToString("F2") + "秒 / "
            + clip_.frameRate + "fps）");
        EditorGUILayout.Space();

        scroll_ = EditorGUILayout.BeginScrollView(scroll_);

        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField("段", GUILayout.Width(96));
            EditorGUILayout.LabelField("秒", GUILayout.Width(56));
            EditorGUILayout.LabelField("フレーム", GUILayout.Width(56));
            EditorGUILayout.LabelField("画面Y", GUILayout.Width(72));
            EditorGUILayout.LabelField("大きさ", GUILayout.Width(72));
            EditorGUILayout.LabelField("(z)", GUILayout.Width(64));
        }

        int remove = -1;
        for (int i = 0; i < beats_.Count; ++i)
        {
            var b = beats_[i];
            using (new EditorGUILayout.HorizontalScope())
            {
                b.name = EditorGUILayout.TextField(b.name, GUILayout.Width(96));

                float sec = EditorGUILayout.FloatField(b.t * clip_.length, GUILayout.Width(56));
                b.t = clip_.length > 0.0001f ? Mathf.Clamp01(sec / clip_.length) : 0.0f;

                EditorGUILayout.LabelField(
                    Mathf.RoundToInt(b.t * clip_.length * clip_.frameRate).ToString(),
                    GUILayout.Width(56));

                b.screenY = EditorGUILayout.FloatField(b.screenY, GUILayout.Width(72));

                float sc = EditorGUILayout.FloatField(b.scale, GUILayout.Width(72));
                b.scale = Mathf.Max(0.02f, sc);

                EditorGUILayout.LabelField(ZFromScale(b.scale).ToString("F0"), GUILayout.Width(64));

                if (GUILayout.Button("−", GUILayout.Width(22))) { remove = i; }
            }
        }
        if (remove >= 0 && beats_.Count > 2) { beats_.RemoveAt(remove); }

        EditorGUILayout.Space();
        if (GUILayout.Button("段を足す"))
        {
            var last = beats_[beats_.Count - 1];
            beats_.Add(new Beat { name = "新しい段", t = Mathf.Clamp01(last.t + 0.05f),
                screenY = last.screenY, scale = last.scale });
        }

        EditorGUILayout.EndScrollView();
        EditorGUILayout.Space();

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("クリップから読み込む")) { Reload(); }
            GUI.backgroundColor = new Color(0.7f, 1.0f, 0.7f);
            if (GUILayout.Button("クリップへ書き込む")) { Write(); }
            GUI.backgroundColor = Color.white;
        }

        if (!string.IsNullOrEmpty(status_))
        {
            EditorGUILayout.HelpBox(status_, MessageType.Info);
        }
    }

    // ---------------------------------------------------------------- 書き込み

    void Write()
    {
        beats_.Sort((a, b) => a.t.CompareTo(b.t));

        var before = new float[41];
        var beforeZ = new float[41];
        var cyOld = GetCurve(PropY);
        var czOld = GetCurve(PropZ);
        for (int i = 0; i < before.Length; ++i)
        {
            float time = (float)i / (before.Length - 1) * clip_.length;
            before[i] = cyOld != null ? cyOld.Evaluate(time) : 0.0f;
            beforeZ[i] = czOld != null ? czOld.Evaluate(time) : 0.0f;
        }

        var ky = new Keyframe[beats_.Count];
        var kz = new Keyframe[beats_.Count];
        for (int i = 0; i < beats_.Count; ++i)
        {
            float time = beats_[i].t * clip_.length;
            ky[i] = new Keyframe(time, beats_[i].screenY);
            kz[i] = new Keyframe(time, ZFromScale(beats_[i].scale));
        }

        var cy = new AnimationCurve(ky);
        var cz = new AnimationCurve(kz);
        // 段の間は素直に繋ぐ。細かい緩急は Animation ウィンドウで接線を触って調整する。
        for (int i = 0; i < cy.length; ++i) { cy.SmoothTangents(i, 0.0f); }
        for (int i = 0; i < cz.length; ++i) { cz.SmoothTangents(i, 0.0f); }

        Undo.RegisterCompleteObjectUndo(clip_, "和了牌の動きを書き込む");
        AnimationUtility.SetEditorCurve(clip_,
            EditorCurveBinding.FloatCurve(TilePath, typeof(RectTransform), PropY), cy);
        AnimationUtility.SetEditorCurve(clip_,
            EditorCurveBinding.FloatCurve(TilePath, typeof(RectTransform), PropZ), cz);
        EditorUtility.SetDirty(clip_);
        AssetDatabase.SaveAssets();

        float dy = 0.0f, dsc = 0.0f;
        for (int i = 0; i < before.Length; ++i)
        {
            float time = (float)i / (before.Length - 1) * clip_.length;
            dy = Mathf.Max(dy, Mathf.Abs(before[i] - cy.Evaluate(time)));
            dsc = Mathf.Max(dsc,
                Mathf.Abs(ScaleFromZ(beforeZ[i]) - ScaleFromZ(cz.Evaluate(time))));
        }

        status_ = "書き込みました（キー " + beats_.Count + " 個）。\n"
            + "書き込む前との差: 画面Y 最大 " + dy.ToString("F1") + " / 大きさ 最大 "
            + dsc.ToString("F3") + " 倍。\n"
            + "Scene ビューの軌道もこの場で変わります。";
        SceneView.RepaintAll();
    }

    // ---------------------------------------------------------------- 変換

    /// <summary>PlaceDepth の z ⇔ 見かけの倍率。z = base * (1/s - 1)。</summary>
    float ScaleFromZ(float z)
    {
        float b = cutin_ != null ? cutin_.stageDepthBase : 1240.0f;
        if (b < 0.0001f) { return 1.0f; }
        float inv = 1.0f + z / b;
        return inv > 0.0001f ? 1.0f / inv : 1.0f;
    }

    float ZFromScale(float s)
    {
        float b = cutin_ != null ? cutin_.stageDepthBase : 1240.0f;
        if (s < 0.0001f) { s = 0.0001f; }
        return b * (1.0f / s - 1.0f);
    }

    AnimationCurve GetCurve(string prop)
    {
        return AnimationUtility.GetEditorCurve(clip_,
            EditorCurveBinding.FloatCurve(TilePath, typeof(RectTransform), prop));
    }

    static MahjongYakumanCutin FindCutin()
    {
        foreach (var c in Object.FindObjectsOfType<MahjongYakumanCutin>(true)) { return c; }
        return null;
    }
}
