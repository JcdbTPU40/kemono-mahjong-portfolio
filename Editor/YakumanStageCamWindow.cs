using UnityEditor;
using UnityEngine;
using SuzuFactory.AozoraMahjong;

/// <summary>
/// ステージカメラの映像を出しっぱなしにする窓。
///
/// Scene ビューのカメラプレビューは、そのカメラを選んでいる間しか出ない。
/// 導入を編集するときは「牌を選んで動かしながら、実際に映る絵を見たい」ので、
/// ドッキングできる窓にして常時描く。
///
/// 3D牌プレビューの複製も YakumanStage レイヤーに置いてあるので、
/// この窓には実機と同じ構図がそのまま出る。
/// </summary>
public class YakumanStageCamWindow : EditorWindow
{
    [SerializeField] bool live_ = true;
    [SerializeField] bool showSafe_ = true;
    Camera cam_;
    double lastRepaint_;

    [MenuItem("Tools/Kemono Mahjong/ステージカメラ")]
    static void Open()
    {
        var w = GetWindow<YakumanStageCamWindow>("ステージカメラ");
        w.minSize = new Vector2(280f, 200f);
        w.Show();
    }

    void OnEnable()
    {
        EditorApplication.update -= Tick;
        EditorApplication.update += Tick;
    }

    void OnDisable()
    {
        EditorApplication.update -= Tick;
    }

    /// <summary>約30fpsで描き直す。毎tick描くとエディタが重くなる。</summary>
    void Tick()
    {
        if (!live_) { return; }
        double now = EditorApplication.timeSinceStartup;
        if (now - lastRepaint_ < 0.033) { return; }
        lastRepaint_ = now;
        Repaint();
    }

    void OnGUI()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            live_ = GUILayout.Toggle(live_, "常時更新", EditorStyles.toolbarButton, GUILayout.Width(72));
            showSafe_ = GUILayout.Toggle(showSafe_, "中心線", EditorStyles.toolbarButton, GUILayout.Width(60));
            GUILayout.FlexibleSpace();
            if (cam_ != null)
            {
                GUILayout.Label(cam_.fieldOfView.ToString("F0") + "°  "
                    + cam_.transform.localPosition.ToString("F1"), EditorStyles.miniLabel);
            }
            if (GUILayout.Button("卓を選び直す", EditorStyles.toolbarButton, GUILayout.Width(88)))
            {
                cam_ = null;
            }
        }

        if (cam_ == null) { cam_ = FindCamera(); }
        if (cam_ == null)
        {
            EditorGUILayout.HelpBox(
                "ステージカメラが見つかりません。先に Wire Yakuman Cutin を実行してください。",
                MessageType.Warning);
            return;
        }

        var rt = cam_.targetTexture;
        if (rt == null)
        {
            EditorGUILayout.HelpBox("カメラに RenderTexture が割り当たっていません。", MessageType.Warning);
            return;
        }

        Rect area = GUILayoutUtility.GetRect(10f, 10f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        Rect fit = FitAspect(area, (float)rt.width / rt.height);

        if (Event.current.type == EventType.Repaint)
        {
            // 無効のままのカメラでも Render() は明示的に描ける。
            // enabled を触ると実行時の「導入の間だけ有効」という前提が崩れるので触らない。
            cam_.Render();
            GUI.DrawTexture(fit, rt, ScaleMode.StretchToFill, false);

            if (showSafe_)
            {
                Handles.BeginGUI();
                Handles.color = new Color(1f, 1f, 1f, 0.25f);
                Handles.DrawLine(new Vector3(fit.center.x, fit.yMin), new Vector3(fit.center.x, fit.yMax));
                Handles.DrawLine(new Vector3(fit.xMin, fit.center.y), new Vector3(fit.xMax, fit.center.y));
                Handles.color = new Color(1f, 1f, 1f, 0.12f);
                for (int i = 1; i <= 2; ++i)
                {
                    float x = fit.xMin + fit.width * i / 3f;
                    float y = fit.yMin + fit.height * i / 3f;
                    Handles.DrawLine(new Vector3(x, fit.yMin), new Vector3(x, fit.yMax));
                    Handles.DrawLine(new Vector3(fit.xMin, y), new Vector3(fit.xMax, y));
                }
                Handles.EndGUI();
            }
        }
    }

    static Rect FitAspect(Rect area, float aspect)
    {
        float w = area.width;
        float h = w / aspect;
        if (h > area.height) { h = area.height; w = h * aspect; }
        return new Rect(area.center.x - w * 0.5f, area.center.y - h * 0.5f, w, h);
    }

    /// <summary>選択中の卓を優先。無ければ最初の卓。</summary>
    static Camera FindCamera()
    {
        if (Selection.activeTransform != null)
        {
            var t = Selection.activeTransform;
            while (t != null)
            {
                var c = t.GetComponentInChildren<MahjongYakumanCutin>(true);
                if (c != null && c.stageCamera != null) { return c.stageCamera; }
                t = t.parent;
            }
        }
        foreach (var c in Object.FindObjectsOfType<MahjongYakumanCutin>(true))
        {
            if (c.stageCamera != null) { return c.stageCamera; }
        }
        return null;
    }
}
