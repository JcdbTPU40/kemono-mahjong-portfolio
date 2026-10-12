#if UNITY_EDITOR
// 役満の着弾演出を、Play Mode に入らずシーンビューで自動ループさせる。
//
//   Tools > Kemono Mahjong > Preview Yakuman Impact
//
// 実行時は MahjongYakumanImpact.Update が時刻を進めるが、編集モードでは
// 誰も呼ばない＝リグが止まったままで「プレビューされない」に見える。
// ここが SampleAt を毎フレーム叩いて代わりに進める（打ち上げプレビューと同じ流儀）。
using UnityEditor;
using UnityEngine;
using SuzuFactory.AozoraMahjong;

[InitializeOnLoad]
public static class YakumanImpactPreview
{
    const string MenuPath = "Tools/Kemono Mahjong/Preview Yakuman Impact";
    const string PrefKey = "KemonoMahjong.YakumanImpactPreview";

    /// <summary>繰り返しの切れ目に入れる間（秒）。0だとどこが頭か分からない。</summary>
    const double LoopPause = 0.6;

    static MahjongYakumanImpact s_target;
    static double s_loopStart;

    static YakumanImpactPreview()
    {
        EditorApplication.delayCall += () => { if (Enabled) { Start(); } };
        EditorApplication.playModeStateChanged += OnPlayMode;
    }

    static bool Enabled
    {
        get { return EditorPrefs.GetBool(PrefKey, false); }
        set { EditorPrefs.SetBool(PrefKey, value); }
    }

    [MenuItem(MenuPath)]
    static void Toggle()
    {
        if (Enabled) { Enabled = false; Stop(); }
        else { Enabled = true; Start(); }
    }

    [MenuItem(MenuPath, true)]
    static bool ToggleValidate()
    {
        Menu.SetChecked(MenuPath, Enabled);
        return !EditorApplication.isPlayingOrWillChangePlaymode;
    }

    static void OnPlayMode(PlayModeStateChange s)
    {
        // 再生中は Mahjong.cs が本物を動かすので、プレビューは必ず畳む。
        if (s == PlayModeStateChange.ExitingEditMode) { Stop(); }
        else if (s == PlayModeStateChange.EnteredEditMode && Enabled) { Start(); }
    }

    static void Start()
    {
        Stop();

        s_target = Find();
        if (s_target == null)
        {
            Debug.LogWarning("[YakumanImpactPreview] MahjongYakumanImpact が見つかりません。"
                + "先に Build / Wire Yakuman Impact を実行してください。");
            Enabled = false;
            return;
        }
        if (s_target.fxRoot == null)
        {
            Debug.LogWarning("[YakumanImpactPreview] リグが未配線です（fxRoot が空）。");
            Enabled = false;
            s_target = null;
            return;
        }

        // 卓は8つある。選択が無いと世界の反対側で回っていて「出ない」に見えるので寄せる。
        Selection.activeGameObject = s_target.fxRoot;
        if (SceneView.lastActiveSceneView != null) { SceneView.lastActiveSceneView.FrameSelected(); }

        s_target.fxRoot.SetActive(true);
        s_loopStart = EditorApplication.timeSinceStartup;

        EditorApplication.update -= Drive;
        EditorApplication.update += Drive;

        Debug.Log("[YakumanImpactPreview] 着弾演出のプレビューを開始しました（卓 "
            + RootPath(s_target.transform) + "）。シーンビューで自動ループします。");
    }

    static void Stop()
    {
        EditorApplication.update -= Drive;

        if (s_target != null && s_target.fxRoot != null)
        {
            // 出しっぱなしにしない。編集モードでは Update が回らないので明示的に畳む。
            s_target.fxRoot.SetActive(false);
        }
        s_target = null;
    }

    static void Drive()
    {
        if (!Enabled || s_target == null || s_target.fxRoot == null) { Stop(); return; }

        double total = Mathf.Max(0.05f, s_target.duration);
        double cycle = total + LoopPause;
        double phase = (EditorApplication.timeSinceStartup - s_loopStart) % cycle;

        if (phase > total)
        {
            // 間（ま）の区間。全部消えた状態を作る（duration を超えた時刻を渡せばよい）。
            s_target.SampleAt((float)total);
        }
        else
        {
            s_target.SampleAt((float)phase);
        }

        SceneView.RepaintAll();
    }

    static MahjongYakumanImpact Find()
    {
        // 選択中の卓を優先する（卓ごとに見比べたいことがある）。
        if (Selection.activeTransform != null)
        {
            var t = Selection.activeTransform;
            while (t != null)
            {
                var c = t.GetComponentInChildren<MahjongYakumanImpact>(true);
                if (c != null) { return c; }
                t = t.parent;
            }
        }
        foreach (var c in Object.FindObjectsOfType<MahjongYakumanImpact>(true)) { return c; }
        return null;
    }

    static string RootPath(Transform t)
    {
        var path = t.name;
        while (t.parent != null) { t = t.parent; path = t.name + "/" + path; }
        return path;
    }
}
#endif
