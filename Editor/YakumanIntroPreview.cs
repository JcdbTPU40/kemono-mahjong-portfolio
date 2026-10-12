using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using SuzuFactory.AozoraMahjong;

/// <summary>
/// 役満カットインの導入を編集するとき、実物の3D牌を編集モードでも出す。
///
/// 実行時は Mahjong.cs が SampleTiles を運んでいるが、編集モードでは動かないので
/// Animation ウィンドウで牌が見えない（UI の代用画像しか出ない）。
/// ここでは SampleTiles の複製を一時オブジェクトとして置き、
/// カットイン側の公開アクセサから毎フレーム座標を読んで並べる。
///
/// ・実行時のコードには一切触らない（エディタ専用の追加）
/// ・複製は HideFlags.DontSave なのでシーンには保存されない
/// ・止めると必ず片付ける
/// </summary>
[InitializeOnLoad]
public static class YakumanIntroPreview
{
    const string MenuPath = "Tools/Kemono Mahjong/Preview Yakuman 3D Tiles";
    const string PrefKey = "KemonoMahjong.YakumanIntroPreview";
    const string RootName = "~YakumanIntroPreview";

    // 代用に使う牌。種類が重複すると同じ絵が並ぶので散らす。
    static readonly string[] TileNames =
        { "M1", "P9", "S3", "Haku", "M9", "P1", "S7", "Chun", "M5", "Ton" };

    static GameObject s_root;
    static Transform[] s_tiles;
    static Vector3[] s_baseScale;
    static MahjongYakumanCutin s_cutin;
    static Graphic[] s_hidden;          // プレビュー中だけ消すUI（二重に見えるため）

    static YakumanIntroPreview()
    {
        // ドメインリロードを跨いでも、有効なら勝手に復帰する。
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

    // ---------------------------------------------------------------- 開始 / 停止

    static void Start()
    {
        Stop();

        s_cutin = FindCutin();
        if (s_cutin == null)
        {
            Debug.LogWarning("[YakumanIntroPreview] MahjongYakumanCutin が見つかりません。");
            Enabled = false;
            return;
        }

        Transform sampleSet = FindSampleTiles(s_cutin);
        if (sampleSet == null)
        {
            Debug.LogWarning("[YakumanIntroPreview] SampleTiles が見つかりません（TilesSet/SampleTiles0）。");
            Enabled = false;
            return;
        }

        int count = s_cutin.GetIntroTileCount();
        if (count <= 0)
        {
            Debug.LogWarning("[YakumanIntroPreview] 牌の枠がありません。先に Wire Yakuman Cutin を実行してください。");
            Enabled = false;
            return;
        }

        s_root = new GameObject(RootName);
        s_root.hideFlags = HideFlags.DontSave;

        s_tiles = new Transform[count];
        s_baseScale = new Vector3[count];

        int layer = s_cutin.GetStageLayer();
        for (int i = 0; i < count; ++i)
        {
            Transform src = sampleSet.Find(TileNames[i % TileNames.Length]);
            if (src == null) { src = sampleSet.childCount > 0 ? sampleSet.GetChild(0) : null; }
            if (src == null) { continue; }

            var go = Object.Instantiate(src.gameObject, s_root.transform);
            go.name = "Preview" + i + "_" + src.name;
            go.hideFlags = HideFlags.DontSave;
            if (layer >= 0) { SetLayer(go.transform, layer); }

            s_tiles[i] = go.transform;
            s_baseScale[i] = src.localScale;
        }

        HideUiTiles(true);

        EditorApplication.update -= Drive;
        EditorApplication.update += Drive;
        Debug.Log("[YakumanIntroPreview] 3D牌プレビューを開始しました（牌 " + count + " 枚）。"
            + "見えないときは Scene ビューの Layers で "
            + LayerMask.LayerToName(Mathf.Max(0, layer)) + " が表示されているか確認してください。");
    }

    static void Stop()
    {
        EditorApplication.update -= Drive;
        HideUiTiles(false);

        if (s_root != null) { Object.DestroyImmediate(s_root); }
        // リロードで参照が切れても、名前で残骸を掃除する。
        // FindObjectsOfType は HideFlags.DontSave のものを返さないので全件走査する。
        foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
        {
            if (go != null && go.name == RootName && go.scene.IsValid())
            {
                Object.DestroyImmediate(go);
            }
        }

        s_root = null;
        s_tiles = null;
        s_baseScale = null;
        s_hidden = null;
        s_cutin = null;
    }

    // ---------------------------------------------------------------- 毎フレーム

    static void Drive()
    {
        if (!Enabled || s_cutin == null || s_tiles == null) { Stop(); return; }

        for (int i = 0; i < s_tiles.Length; ++i)
        {
            if (s_tiles[i] == null) { continue; }

            bool show = IsVisible(i);
            if (s_tiles[i].gameObject.activeSelf != show)
            {
                s_tiles[i].gameObject.SetActive(show);
            }
            if (!show) { continue; }

            s_tiles[i].position = s_cutin.GetIntroTilePosition(i);
            s_tiles[i].rotation = s_cutin.GetIntroTileRotation(i);

            float m = ScaleAt(i);
            s_tiles[i].localScale = new Vector3(
                s_baseScale[i].x * m, s_baseScale[i].y * m, s_baseScale[i].z * m);
        }

        SceneView.RepaintAll();
    }

    /// <summary>
    /// 大きさ。牌自体の拡大は常に 1（GetIntroTileScale と同じ規則）。
    /// 大きさは Pos Z（奥行き）による遠近だけで決まる。
    /// ここを実行時と食い違わせるとプレビューが嘘になるので、必ず揃えること。
    /// </summary>
    static float ScaleAt(int index)
    {
        return s_cutin.realTileScale;
    }

    /// <summary>可視度もクリップが色に書いているので、そこから読む。</summary>
    static bool IsVisible(int index)
    {
        RawImage img = index == 0
            ? s_cutin.winningTileImage
            : (s_cutin.cometTileImages != null && index - 1 < s_cutin.cometTileImages.Length
                ? s_cutin.cometTileImages[index - 1] : null);
        return img != null && img.color.a > 0.01f;
    }

    static RectTransform RectAt(int index)
    {
        if (index == 0) { return s_cutin.winningTileRect; }
        int i = index - 1;
        if (s_cutin.cometTileRects == null || i >= s_cutin.cometTileRects.Length) { return null; }
        return s_cutin.cometTileRects[i];
    }

    /// <summary>
    /// UI の代用牌はプレビュー中だけ消す（3D牌と二重に見えるため）。
    ///
    /// 戻すときに s_hidden へ頼ってはいけない。スクリプトを再コンパイルすると
    /// 静的フィールドは消えるのに、コンポーネントの enabled=false はシーンに残るので、
    /// 「UI も 3D も出ない」状態で固まる（実際にそうなった）。
    /// 覚えていなくても、卓から辿って必ず戻せるようにする。
    /// </summary>
    static void HideUiTiles(bool hide)
    {
        var list = new System.Collections.Generic.List<Graphic>();
        var c = s_cutin != null ? s_cutin : FindCutin();
        if (c != null)
        {
            if (c.winningTileImage != null) { list.Add(c.winningTileImage); }
            if (c.cometTileImages != null)
            {
                foreach (var g in c.cometTileImages) { if (g != null) { list.Add(g); } }
            }
        }
        // 前回の記録が残っていれば、そちらも合わせて戻す。
        if (!hide && s_hidden != null)
        {
            foreach (var g in s_hidden) { if (g != null && !list.Contains(g)) { list.Add(g); } }
        }

        foreach (var g in list) { g.enabled = !hide; }
        s_hidden = hide ? list.ToArray() : null;
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

    /// <summary>同じ卓の TilesSet/SampleTiles0 を探す。</summary>
    static Transform FindSampleTiles(MahjongYakumanCutin cutin)
    {
        Transform t = cutin.transform;
        while (t != null)
        {
            var set = t.Find("TilesSet/SampleTiles0");
            if (set != null) { return set; }
            t = t.parent;
        }
        return null;
    }

    static void SetLayer(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        for (int i = 0; i < t.childCount; ++i) { SetLayer(t.GetChild(i), layer); }
    }
}
