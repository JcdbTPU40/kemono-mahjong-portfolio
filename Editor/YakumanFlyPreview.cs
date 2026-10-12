using UnityEditor;
using UnityEngine;
using SuzuFactory.AozoraMahjong;

/// <summary>
/// 役満の打ち上げ（和了牌と手牌が空へ飛ぶ）を編集するとき、
/// 実物の3D牌を<b>編集モードでも</b>出す。
///
/// クリップが動かすのは空のスロット（FlyRig の子）だけなので、
/// Animation ウィンドウで触っても何も見えない。ここでは
/// SampleTiles の複製を「和了時の手牌の位置」に並べ、
/// スロットの姿勢を実行時とまったく同じ式で乗せて見せる。
///
/// ・実行時のコードには一切触らない（エディタ専用の追加）
/// ・複製は HideFlags.DontSave なのでシーンには保存されない
/// ・止めると必ず片付ける／再生モードに入るときは自動で畳む
/// </summary>
[InitializeOnLoad]
public static class YakumanFlyPreview
{
    const string MenuPath = "Tools/Kemono Mahjong/Preview Yakuman Fly Tiles";
    const string PrefKey = "KemonoMahjong.YakumanFlyPreview";
    const string RootName = "~YakumanFlyPreview";

    // Mahjong.cs の牌の寸法と手牌の並べ方（UpdateView）に合わせてある。
    // ここがズレるとプレビューが嘘になるので、向こうを変えたらこちらも直すこと。
    const float TileWidth = 0.0212f;
    const float TileHeight = 0.029f;   // 立てたときの高さ（公開前の手牌）
    const float TileDepth = 0.0162f;   // 倒したときの高さ（公開後の手牌＝打ち上げの原点）
    const float HandTileX = -0.05f;   // 副露なしの手牌の左端
    const float SeatX = 0.35f;        // 卓中心から自席までの距離

    /// <summary>並べる手牌の枚数（門前13枚＋和了牌1枚のつもり）。</summary>
    const int HandCount = 13;

    // 代用に使う牌。種類が重複すると同じ絵が並ぶので散らす。
    static readonly string[] TileNames =
        { "M1", "M2", "M3", "P4", "P5", "P6", "S7", "S8", "S9", "Ton", "Nan", "Sha", "Pei", "Haku" };

    static GameObject s_root;
    static Transform[] s_tiles;       // [0] = 和了牌 / [1..] = 手牌
    static Vector3[] s_homePos;
    static Quaternion[] s_homeRot;
    static Vector3[] s_baseScale;
    static MahjongYakumanCutin s_cutin;

    /// <summary>繰り返しの切れ目に入れる間（秒）。0だとどこが頭か分からない。</summary>
    const double LoopPause = 0.7;

    static double s_loopStart;

    static YakumanFlyPreview()
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

    // ---- ホールド（コマの調整用）----
    //
    // コマ（核を包む光）は点火（1.483秒）から終わりまでの約0.6秒しか出ない。
    // ループさせたままだと一瞬すぎて大きさを詰められないので、
    // 「そこで止める」モードを別に用意する。ON のあいだ打ち上げは HoldT で固定され、
    // Inspector の comaScale を触るとその場で大きさが変わる。
    const string HoldMenuPath = "Tools/Kemono Mahjong/Preview Yakuman Fly Tiles (Hold・コマ調整)";
    const string HoldPrefKey = "KemonoMahjong.YakumanFlyPreviewHold";

    /// <summary>止める位置（クリップの正規化時刻）。点火0.712＋少し飛んだあたり。</summary>
    const float HoldT = 0.88f;

    static bool Hold
    {
        get { return EditorPrefs.GetBool(HoldPrefKey, false); }
        set { EditorPrefs.SetBool(HoldPrefKey, value); }
    }

    [MenuItem(HoldMenuPath)]
    static void ToggleHold()
    {
        Hold = !Hold;
        if (Hold && !Enabled) { Enabled = true; Start(); }
        if (Hold)
        {
            Debug.Log("[YakumanFlyPreview] 打ち上げを " + HoldT.ToString("F2")
                + "（コマが開ききったところ）で止めました。"
                + " YakumanCutin の comaScale を触ると、その場で大きさが変わります。"
                + " 出るのは素の大きさで、実機では速さで外套がもう少し膨らみます。");
        }
        SceneView.RepaintAll();
    }

    [MenuItem(HoldMenuPath, true)]
    static bool ToggleHoldValidate()
    {
        Menu.SetChecked(HoldMenuPath, Hold);
        return !EditorApplication.isPlayingOrWillChangePlaymode;
    }

    static void OnPlayMode(PlayModeStateChange s)
    {
        // 再生中は Mahjong.cs が本物を動かすので、プレビューは必ず畳む。
        if (s == PlayModeStateChange.ExitingEditMode) { Stop(); }
        else if (s == PlayModeStateChange.EnteredEditMode && Enabled) { Start(); }
    }

    // ---------------------------------------------------------------- 開始 / 停止

    static void Start()
    {
        Stop();

        s_cutin = FindCutin();
        if (s_cutin == null)
        {
            Debug.LogWarning("[YakumanFlyPreview] MahjongYakumanCutin が見つかりません。");
            Enabled = false;
            return;
        }
        if (s_cutin.flyWinTile == null || s_cutin.flyHandSlots == null)
        {
            Debug.LogWarning("[YakumanFlyPreview] 打ち上げリグが未配線です。"
                + "先に Build Yakuman Fly → Wire Yakuman Fly を実行してください。");
            Enabled = false;
            return;
        }

        Transform table = FindTable(s_cutin);
        Transform sampleSet = table != null ? table.Find("TilesSet/SampleTiles0") : null;
        if (table == null || sampleSet == null)
        {
            Debug.LogWarning("[YakumanFlyPreview] 卓の SampleTiles が見つかりません（TilesSet/SampleTiles0）。");
            Enabled = false;
            return;
        }

        int n = HandCount + 1;
        s_root = new GameObject(RootName);
        s_root.hideFlags = HideFlags.DontSave;

        s_tiles = new Transform[n];
        s_homePos = new Vector3[n];
        s_homeRot = new Quaternion[n];
        s_baseScale = new Vector3[n];

        // 和了公開のあと（ドンで手牌を倒したあと）に打ち上がるので、原点は
        // 「倒して表向き」＝Mahjong.UpdateView の open 枝。立てた姿勢を原点にすると
        // GetFlyEuler の回転軸が実行時と 90 度ズレて、別の回り方になる。
        Quaternion localRot = Quaternion.Euler(270.0f, 90.0f, 0.0f);

        for (int i = 0; i < n; ++i)
        {
            Transform src = sampleSet.Find(TileNames[i % TileNames.Length]);
            if (src == null) { src = sampleSet.childCount > 0 ? sampleSet.GetChild(0) : null; }
            if (src == null) { continue; }

            var go = Object.Instantiate(src.gameObject, s_root.transform);
            go.name = (i == 0 ? "PreviewWin_" : "PreviewHand" + (i - 1).ToString("00") + "_") + src.name;
            go.hideFlags = HideFlags.DontSave;
            go.SetActive(true);

            // 和了牌は列の右端に、少し離して置く（雀魂と同じで一枚だけ浮いて見える）。
            float slot = i == 0 ? HandCount + 0.6f : i - 1;
            var local = new Vector3(SeatX, TileDepth * 0.5f, slot * TileWidth + HandTileX);

            s_tiles[i] = go.transform;
            s_homePos[i] = table.TransformPoint(local);
            s_homeRot[i] = table.rotation * localRot;
            s_baseScale[i] = src.localScale;
        }

        s_loopStart = EditorApplication.timeSinceStartup;

        EditorApplication.update -= Drive;
        EditorApplication.update += Drive;

        // 卓は8つあり、選択が無いと「どこかの卓」で始まる。世界の反対側で
        // 回っていて「プレビューされない」と見えるのを防ぐため、必ず寄せる。
        Selection.activeGameObject = s_root;
        SceneView.lastActiveSceneView?.FrameSelected();

        Debug.Log("[YakumanFlyPreview] 打ち上げプレビューを開始しました（牌 " + n + " 枚 / 卓 "
            + table.name + "）。シーンビューで自動ループします。"
            + " Animation ウィンドウを Preview/Record にすると、そちらの再生ヘッドが優先されます。");
    }

    static void Stop()
    {
        EditorApplication.update -= Drive;

        if (s_root != null) { Object.DestroyImmediate(s_root); }

        // リロードで参照が切れても、名前で残骸を掃除する。
        // FindObjectsOfType は HideFlags.DontSave のものを返さないので全件走査する。
        //
        // scene.IsValid() で絞ってはいけない。DontSave の GameObject は
        // ドメインリロードを跨ぐとシーンから外れて "scene=(無効)" になり、
        // その条件では二度と掃除されずに再コンパイルのたび増える（実際に3体溜まっていた）。
        // 代わりに「アセットではないこと」で守る。
        foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
        {
            if (go == null || go.name != RootName) { continue; }
            if (EditorUtility.IsPersistent(go)) { continue; }
            Object.DestroyImmediate(go);
        }

        s_root = null;
        s_tiles = null;
        s_homePos = null;
        s_homeRot = null;
        s_baseScale = null;

        // 尾を出しっぱなしにしない。エディタでは UpdateCometTail が回らないので、
        // ここで明示的に畳む。
        if (s_cutin != null)
        {
            s_cutin.HideComet();
            if (s_cutin.flyComet != null) { s_cutin.flyComet.gameObject.SetActive(false); }
        }
        s_cutin = null;
    }

    // ---------------------------------------------------------------- 毎フレーム

    /// <summary>
    /// スロットの姿勢を牌へ乗せる。式は Mahjong.DriveYakumanWinTile /
    /// DriveYakumanTileSweep と同じ。<b>片方だけ直すとプレビューが嘘になる。</b>
    /// </summary>
    static void Drive()
    {
        if (!Enabled || s_cutin == null || s_tiles == null) { Stop(); return; }

        Transform table = FindTable(s_cutin);
        if (table == null) { Stop(); return; }

        // クリップを自分で送る。実行時は Mahjong.SampleYakumanFly がこれをやるが、
        // 編集モードでは誰も呼ばない＝スロットが止まったままで「プレビューされない」。
        //
        // Animation ウィンドウが Preview/Record に入っているときは、向こうが
        // Animator を握っているので手を出さない（取り合うとどちらも荒れる）。
        bool inPause = false;

        // Animation ウィンドウが握っているときは正規化時刻が分からない。
        // 1.0 を渡して「点火済み」扱いにする＝スクラブ中も尾が見える。
        float previewT = 1.0f;

        if (!AnimationMode.InAnimationMode())
        {
            if (Hold)
            {
                // コマの調整用。ここで止めたまま Inspector を触ってもらう。
                previewT = HoldT;
            }
            else
            {
                double total = Mathf.Max(0.05f, s_cutin.GetFlySeconds());
                double cycle = total + LoopPause;
                double phase = (EditorApplication.timeSinceStartup - s_loopStart) % cycle;

                // クリップの末尾で牌は縮まない（Scale カーブが1本キーのまま＝既知の残件）ので、
                // 間の区間はこちらで伏せる。伏せないと1mの高さに牌が浮いたまま止まって見える。
                inPause = phase > total;
                previewT = (float)(phase / total);
            }
            s_cutin.SampleFly(previewT);
        }

        // 席0（プレビューの見せ方）。実行時は和了者の席で回る。
        Quaternion flyRot = table.rotation;

        for (int i = 0; i < s_tiles.Length; ++i)
        {
            if (s_tiles[i] == null) { continue; }

            int index = i == 0 ? -1 : i - 1;

            bool show = !inPause && s_cutin.IsFlyVisible(index);
            if (s_tiles[i].gameObject.activeSelf != show)
            {
                s_tiles[i].gameObject.SetActive(show);
            }
            if (!show) { continue; }

            s_tiles[i].position = s_homePos[i] + flyRot * s_cutin.GetFlyOffset(index);
            s_tiles[i].rotation = s_homeRot[i] * Quaternion.Euler(s_cutin.GetFlyEuler(index));

            float m = s_cutin.GetFlyScale(index);
            s_tiles[i].localScale = new Vector3(
                s_baseScale[i].x * m, s_baseScale[i].y * m, s_baseScale[i].z * m);
        }

        // 彗星の尾。実行時と同じ入口（DriveComet / HideComet）を通す＝
        // プレビューと本番で挙動が食い違わない。
        // ただし尾は「速さ」で点くので、Animation ウィンドウの再生ヘッドを
        // ゆっくり動かすと点かない。実速度で見たいときは再生（▶）で流すこと。
        if (s_tiles.Length > 0 && s_tiles[0] != null && s_tiles[0].gameObject.activeSelf)
        {
            // コマ（核を包む光）は本来ローカルプレイヤーの頭を向くが、編集モードには
            // プレイヤーがいない（`Camera.main` も Udon 非公開で参照できない）。
            // 代わりにこちらから Scene ビューのカメラへ向けておく。DriveComa は
            // 視点が取れないとき「今の向き」を保つので、これがそのまま効く。
            FaceComaToSceneView();

            // ホールド中は牌が動かない＝速さ0なので、コマは「素の大きさ」で出る
            // （実機では速さで外套が最大+50%ほど膨らむ）。速さを偽装しないのは、
            // 編集モードの Time.deltaTime が当てにならず、値が跳ねるため。
            s_cutin.DriveComet(s_tiles[0].position, previewT);
        }
        else
        {
            s_cutin.HideComet();
        }

        SceneView.RepaintAll();
    }

    /// <summary>コマの板を Scene ビューのカメラへ向ける（編集モードのビルボード代わり）。</summary>
    static void FaceComaToSceneView()
    {
        if (s_cutin == null || s_cutin.flyComa == null) { return; }

        var view = SceneView.lastActiveSceneView;
        if (view == null || view.camera == null) { return; }

        Vector3 forward = s_cutin.flyComa.position - view.camera.transform.position;
        if (forward.sqrMagnitude < 0.000001f) { return; }

        s_cutin.flyComa.rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
    }

    // ---------------------------------------------------------------- 探索

    static MahjongYakumanCutin FindCutin()
    {
        // 選択中の卓を優先する（卓ごとに見比べたいことがある）。
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

    static Transform FindTable(MahjongYakumanCutin cutin)
    {
        Transform t = cutin.transform;
        while (t != null)
        {
            if (t.GetComponent<Mahjong>() != null) { return t; }
            t = t.parent;
        }
        return null;
    }
}
