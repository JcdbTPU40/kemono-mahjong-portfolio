using System.Collections.Generic;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SuzuFactory.AozoraMahjong;

/// <summary>
/// 役満の「和了牌と手牌が空へ飛ぶ」アニメーションを組み立てる。
///
/// 段取りの中の位置：
///   ロン/ツモ宣言＋墨しぶき（CallEffect）
///   → 勝者カットイン（CutinPanel）
///   → ドン＋和了牌と手牌の公開
///   → <b>ここ</b>（和了牌と手牌が空へ飛ぶ）
///   → 役満カットイン → あがり画面
///
/// 動きは Animation Clip が持つ。クリップは「牌そのもの」ではなく
/// <b>スロット（空の Transform）</b>を動かし、実物の牌は Mahjong.cs が
/// スロットの姿勢を差分として乗せて運ぶ。
/// 手牌は枚数も種類も局ごとに変わるので、牌に直接カーブは焼けない。
///
/// スロットの読み方（Animation ウィンドウで触るのはここ）:
///   Position … 和了者から見たずらし量（m）。+Y が空、+X が手前（自分側）、+Z が右
///   Rotation … 卓に置かれていたときの向きからの回転（度）
///   Scale    … 大きさの倍率。0 まで落とすとその牌は消える
///              （牌は不透明なのでアルファでは消せない＝縮めて消す）
/// </summary>
public static class BuildYakumanFly
{
    const string TablePrefabPath = "Assets/Mahjong/Prefabas/KemonoMahjong.prefab";
    const string CutinRootName = "YakumanCutin";
    const string RigName = "FlyRig";
    const string WinSlotName = "WinTile";
    const string CometName = "FlyComet";
    const string CometVfxChildName = "Vfx";

    /// <summary>彗星の見た目。Vefects/Trails VFX の BIRP（Built-in）版プレハブ。
    /// 色を変えたいときはここだけ差し替える（Fire=金〜橙 / Cosmos=紫 / Electric=青白 …）。</summary>
    const string CometVfxPath = "Assets/Vefects/Trails VFX/VFX/Particles/VFX_Trail_Fire.prefab";

    /// <summary>GrabPass（画面のつかみ取り）で歪ませる子。Quest では高すぎるので必ず落とす。</summary>
    const string CometDistortionName = "Distortion";

    /// <summary>元プレハブの幅・尺への倍率。元素材は「人が振る剣」想定で 1〜2m あるので、
    /// 牌（約3cm）に合わせて大きく縮める。層ごとの比率（芯:外側=1:2）は元のまま保つ。</summary>
    /// 牌の実寸は 0.021 x 0.029 x 0.016m。芯を牌幅の約1.4倍、外側を約2.9倍にしている。
    const float CometWidthScale = 0.03f;
    /// 打ち上げは約0.67秒で1m。尾が 0.15m（芯）/ 0.30m（外側）＝飛距離の1/4前後に収まる長さ。
    const float CometTimeScale = 0.10f;

    /// <summary>点を打つ間隔（m）。既定の 0.1m は牌の飛距離（約1m）に対して粗すぎてカクつく。</summary>
    const float CometMinVertexDistance = 0.004f;

    // ---- 打ち上げのSE ----
    // ここに音源を置いて Wire を回すと自動で割り当たる（YakumanImpactSFX と同じ流儀）。
    // ファイル名の頭で役割が決まる（大文字小文字は無視）:
    //   fly*.wav    … 1本もの。打ち上げ全体（2.083秒）を覆う。これがあれば下2つは使わない
    //   charge*.wav … 溜め（0.00秒〜。牌が卓上で浮いている約1.41秒ぶん）
    //   launch*.wav … 発射（85フレーム＝1.408秒。牌が上がり始める瞬間）
    // 接頭辞なしの音源を1本だけ置いた場合は、それを1本もの（fly*）として扱う。
    const string SfxDir = "Assets/Mahjong/Sounds/YakumanFlySFX";
    const string FullPrefix = "fly";
    const string ChargePrefix = "charge";
    const string LaunchPrefix = "launch";

    /// <summary>発射SEのフレーム。クリップは 60fps・全125フレームで、85から牌が上がり始める。</summary>
    const int LaunchFrame = 85;

    // 音量。溜めは鳴りっぱなしなので段B寄り、発射は単発の決定的な瞬間なので段A。
    // 素材を差し替えたら実測して入れ直すこと:
    //   ffmpeg -hide_banner -i <file> -af ebur128=peak=true -f null -
    //   volume = 10^((目標LUFS - 実測LUFS) / 20)   溜め=-39 / 発射=-35
    // 1本ものは「役満打ち上げ演出.wav」の実測 -8.1 LUFS を目標 -37 LUFS へ落とした値。
    const float FullVolume = 0.0565f;     // 1本もの（段B寄り。中に発射の当たりを含むため少し上げ）
    const float ChargeVolume = 0.085f;
    const float LaunchVolume = 0.12f;

    /// <summary>尾を点けるフレーム。クリップは 60fps・全125フレームで、
    /// 打ち上げが始まるのが85フレーム目（1.417秒）。その4フレーム後に点火する。
    /// Wire がクリップの実尺から正規化時刻へ直して component に入れるので、
    /// クリップを焼き直しても Wire を回せば追従する。</summary>
    const int CometStartFrame = 89;

    /// <summary>点火した瞬間に敷く尾の長さ（m）。溜まるのを待つと、点火から
    /// 数フレームは芯だけの短い線に見える＝「一瞬で尾が付く」感じにならない。</summary>
    const float CometSeedLength = 0.20f;
    const int CometSeedPoints = 8;

    /// <summary>牌の中心から少し下げて出す。真芯から出すと最初の1cmが牌に隠れる。</summary>
    static readonly Vector3 CometOffset = new Vector3(0.0f, -0.012f, 0.0f);

    // ---- コマ（核を包む光）----
    // 彗星は 核・コマ・尾 の3点で彗星に見える。尾（TrailRenderer）だけだと
    // 「線が飛んでいる」だけになるので、核を包む光をここで足す。
    const string FxTextureDir = "Assets/Mahjong/Textures/FX";
    const string ComaName = "Coma";
    const string ComaTexturePath = FxTextureDir + "/fx_yakuman_nosecone.png";
    const string ComaMaterialDir = "Assets/Mahjong/Materials/FX";
    const string ComaMaterialPath = ComaMaterialDir + "/M_YakumanComa.mat";

    /// <summary>重ねる板の一辺（m）。内側＝芯 から 外側＝外套 へ。牌は約3cm。</summary>
    static readonly float[] ComaSizes = new float[] { 0.115f, 0.200f, 0.300f };

    /// <summary>各層の色。火の尾（VFX_Trail_Fire）に合わせて 白金 → 金 → 琥珀。</summary>
    static readonly Color[] ComaColors = new Color[]
    {
        new Color(1.00f, 1.00f, 1.00f, 1.0f),
        new Color(1.00f, 0.94f, 0.82f, 1.0f),
        new Color(0.86f, 0.95f, 1.00f, 1.0f),
    };

    /// <summary>各層の濃さ。加算なので重なるほど明るい。</summary>
    static readonly float[] ComaAlphas = new float[] { 0.30f, 0.15f, 0.060f };

    /// <summary>視点から見て牌の背面へ置く量（m）。負値で牌面を白飛びさせず、側光だけを見せる。</summary>
    const float ComaFront = -0.014f;

    // 動きの既定値。Reset Yakuman Coma (Look) を回したときだけここへ戻る。
    const float ComaScale = 1.0f;
    const float ComaFadeInSeconds = 0.08f;
    const float ComaIgnitionFlash = 0.35f;
    const float ComaSpeedFull = 2.5f;
    const float ComaSpeedGain = 0.12f;
    const float ComaStretch = 0.12f;
    const float ComaLag = 0.08f;
    const float ComaFlicker = 0.06f;
    const float ComaFlickerHz = 24.0f;

    const string ClipDir = "Assets/Mahjong/Animations";
    const string ClipPath = ClipDir + "/YakumanFly.anim";
    const string ControllerPath = ClipDir + "/YakumanFly.controller";
    const string BackupDir = ClipDir + "/Backup";
    const string StateName = "Fly";
    const string StampKey = "KemonoMahjong.YakumanFlyClipStamp";

    /// <summary>手牌スロットの数。Mahjong.cs の AGARI_YAKUMAN_SWEEP_MAX と揃えること。</summary>
    const int HandSlots = 20;

    /// <summary>クリップの長さ（秒）。Wire がこの値を component へも入れる。</summary>
    const float ClipSeconds = 2.20f;

    // ================================================================ メニュー

    [MenuItem("Tools/Kemono Mahjong/Build Yakuman Fly (Prefab)")]
    public static void BuildPrefab()
    {
        EnsureClip(false);
        EnsureFolder(SfxDir);   // 音源の置き場所を先に作っておく

        var stage = PrefabStageUtility.OpenPrefab(TablePrefabPath);
        if (stage == null)
        {
            Debug.LogError("[BuildYakumanFly] Prefab を開けません: " + TablePrefabPath);
            return;
        }

        var root = stage.prefabContentsRoot;
        if (root.GetComponent<Mahjong>() == null)
        {
            Debug.LogError("[BuildYakumanFly] Prefab のルートに Mahjong がありません。");
            StageUtility.GoToMainStage();
            return;
        }

        bool ok = BuildOne(root.transform);
        SavePrefabStage(stage);

        if (ok)
        {
            Debug.Log("[BuildYakumanFly] Prefab に打ち上げリグを生成/更新しました。"
                + " … 次に Wire Yakuman Fly (Prefab) → Wire Yakuman Fly を実行してください。");
        }
    }

    [MenuItem("Tools/Kemono Mahjong/Build Yakuman Fly")]
    public static void Build()
    {
        EnsureClip(false);

        var tables = Object.FindObjectsOfType<Mahjong>(true);
        if (tables == null || tables.Length == 0)
        {
            Debug.LogWarning("[BuildYakumanFly] シーンに Mahjong が見つかりません"
                + "（Prefab Mode なら (Prefab) 版を使ってください）。");
            return;
        }

        int built = 0;
        foreach (var t in tables) { if (BuildOne(t.transform)) { ++built; } }

        MarkSceneDirty();
        Debug.Log("[BuildYakumanFly] " + built + " / " + tables.Length + " 卓にリグを生成/更新しました。");
    }

    [MenuItem("Tools/Kemono Mahjong/Wire Yakuman Fly (Prefab)")]
    public static void WirePrefab()
    {
        var stage = PrefabStageUtility.OpenPrefab(TablePrefabPath);
        if (stage == null)
        {
            Debug.LogError("[BuildYakumanFly] Prefab を開けません: " + TablePrefabPath);
            return;
        }

        int n = WireOne(stage.prefabContentsRoot.transform) ? 1 : 0;
        SavePrefabStage(stage);

        Debug.Log("[BuildYakumanFly] Prefab を配線しました（" + n + "）。"
            + " Prefab だけでは Udon 側の publicVariables に入らないので、"
            + "続けて Wire Yakuman Fly（シーン版）も実行してください。");
    }

    [MenuItem("Tools/Kemono Mahjong/Wire Yakuman Fly")]
    public static void Wire()
    {
        var tables = Object.FindObjectsOfType<Mahjong>(true);
        if (tables == null || tables.Length == 0)
        {
            Debug.LogWarning("[BuildYakumanFly] シーンに Mahjong が見つかりません。");
            return;
        }

        int wired = 0;
        foreach (var t in tables) { if (WireOne(t.transform)) { ++wired; } }

        MarkSceneDirty();
        Debug.Log("[BuildYakumanFly] " + wired + " / " + tables.Length + " 卓を配線しました。");
    }

    /// <summary>
    /// クリップを作り直す。<b>手で編集したカーブは残らない</b>ので、
    /// 控えを取り、前回の焼き込み以降に変わっていたら必ず尋ねる。
    /// </summary>
    [MenuItem("Tools/Kemono Mahjong/Bake Yakuman Fly Clip")]
    public static void Bake()
    {
        EnsureClip(true);
    }

    [MenuItem("Tools/Kemono Mahjong/Validate Yakuman Fly")]
    public static void Validate()
    {
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipPath);
        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);

        var lines = new List<string>();
        lines.Add("clip: " + (clip == null ? "なし" : ClipPath + " / " + clip.length.ToString("F2") + "秒 / カーブ "
            + AnimationUtility.GetCurveBindings(clip).Length + " 本"));
        lines.Add("controller: " + (ctrl == null ? "なし" : ControllerPath));

        var tables = Object.FindObjectsOfType<Mahjong>(true);
        int ok = 0;
        int udonOk = 0;
        int cometOk = 0;
        int comaOk = 0;
        int cometAudio = 0;

        foreach (var t in tables)
        {
            var cutin = FindCutin(t.transform);
            if (cutin == null) { lines.Add(t.name + ": MahjongYakumanCutin なし"); continue; }

            cometAudio += CountCometAudio(cutin.transform);

            // 彗星の尾。参照が空でも打ち上げ自体は動く（尾が出ないだけ）ので、別枠で数える。
            bool comet = cutin.flyComet != null
                && cutin.flyCometTrails != null
                && cutin.flyCometTrails.Length > 0
                && AllSetTrails(cutin.flyCometTrails);
            if (comet) { ++cometOk; }
            else
            {
                lines.Add(t.name + ": 彗星なし"
                    + " root=" + (cutin.flyComet != null)
                    + " trails=" + (cutin.flyCometTrails == null ? 0 : cutin.flyCometTrails.Length));
            }

            // コマ。これも無くても打ち上げは動くので別枠。
            bool coma = cutin.flyComa != null
                && cutin.flyComaLayers != null
                && cutin.flyComaLayers.Length == ComaSizes.Length
                && AllSetRenderers(cutin.flyComaLayers);
            if (coma) { ++comaOk; }
            else
            {
                lines.Add(t.name + ": コマなし"
                    + " root=" + (cutin.flyComa != null)
                    + " layers=" + (cutin.flyComaLayers == null ? 0 : cutin.flyComaLayers.Length));
            }

            bool full = cutin.flyAnimator != null
                && cutin.flyAnimator.runtimeAnimatorController != null
                && cutin.flyWinTile != null
                && cutin.flyHandSlots != null
                && cutin.flyHandSlots.Length == HandSlots
                && AllSet(cutin.flyHandSlots)
                // 参照が埋まっていても、リグが非アクティブなら実行時に何も動かない。
                && cutin.flyWinTile.gameObject.activeInHierarchy;
            if (full) { ++ok; }
            else
            {
                lines.Add(t.name + ": 未配線"
                    + " animator=" + (cutin.flyAnimator != null)
                    + " ctrl=" + (cutin.flyAnimator != null && cutin.flyAnimator.runtimeAnimatorController != null)
                    + " win=" + (cutin.flyWinTile != null)
                    + " rigActive=" + (cutin.flyWinTile != null
                        && cutin.flyWinTile.gameObject.activeInHierarchy)
                    + " slots=" + (cutin.flyHandSlots == null ? 0 : cutin.flyHandSlots.Length));
            }

            // Udon 側（実行時に読まれるのはこちら）。
            var ub = UdonSharpEditorUtility.GetBackingUdonBehaviour(cutin);
            object v;
            if (ub != null && ub.publicVariables != null
                && ub.publicVariables.TryGetVariableValue("flyWinTile", out v) && v != null)
            {
                ++udonOk;
            }
        }

        lines.Add("配線済み（プロキシ）: " + ok + " / " + tables.Length);
        lines.Add("配線済み（Udon publicVariables）: " + udonOk + " / " + tables.Length);
        var fireClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipPath);
        float fireFrames = fireClip != null ? fireClip.length * fireClip.frameRate : 0.0f;
        lines.Add("彗星トレイル: " + cometOk + " / " + tables.Length + "（素材: " + CometVfxPath + "）");
        lines.Add("  素材同梱のSE: " + (cometAudio == 0
            ? "なし（除去済み）"
            : "⚠ AudioSource が " + cometAudio + " 個残っています。打ち上げSEと二重に鳴るので Build Yakuman Fly を回してください。"));
        lines.Add("コマ（核を包む光）: " + comaOk + " / " + tables.Length
            + "（板 " + ComaSizes.Length + "枚・" + ComaMaterialPath + "）");
        lines.Add("  " + DescribeComa(tables));
        var fullSfx = FindFullSfx();
        var chargeSfx = FindSfx(ChargePrefix);
        var launchSfx = FindSfx(LaunchPrefix);
        float secPerFrame = fireClip != null ? 1.0f / fireClip.frameRate : (1.0f / 60.0f);
        float flySeconds = fireClip != null ? fireClip.length : ClipSeconds;
        float launchAt = LaunchFrame * secPerFrame;

        lines.Add("SE置き場: " + SfxDir + "（" + (AssetDatabase.IsValidFolder(SfxDir) ? "あり" : "なし") + "）");
        if (fullSfx != null)
        {
            lines.Add("  方式: 1本もの（" + fullSfx.name + " が全体を覆う。charge*/launch* は鳴らさない）");
            lines.Add("  fly*   : " + DescribeClip(fullSfx, FullVolume)
                + " / 0.00秒〜 打ち上げの尺 " + flySeconds.ToString("F3") + "秒");

            float diff = fullSfx.length - flySeconds;
            if (diff > 0.05f)
            {
                lines.Add("  ⚠ " + diff.ToString("F2") + "秒ぶん長い。役満カットインへ音がはみ出します"
                    + "（尾なら自然ですが、意図した尺か確認してください）。");
            }
            else if (diff < -0.05f)
            {
                lines.Add("  ⚠ " + (-diff).ToString("F2") + "秒ぶん短い。打ち上げの終わりが無音になります。");
            }
            else
            {
                lines.Add("  ✅ 尺が打ち上げクリップと一致しています。");
            }
        }
        else
        {
            lines.Add("  方式: 2本立て（溜め＋発射）");
            var loose = FindUntaggedSfx();
            if (loose.Count > 0)
            {
                lines.Add("  ⚠ 接頭辞のない音源が " + loose.Count + " 本あります（2本以上あるとどれも鳴りません）。"
                    + "1本ものにするなら fly、2本立てにするなら charge / launch を頭に付けてください。");
            }
            lines.Add("  charge*: " + DescribeClip(chargeSfx, ChargeVolume)
                + " / 0.00秒〜 持ち時間 " + launchAt.ToString("F2") + "秒");
            lines.Add("  launch*: " + DescribeClip(launchSfx, LaunchVolume)
                + " / " + launchAt.ToString("F2") + "秒〜（尾は役満カットインへ被ってよい）");
            if (chargeSfx != null && chargeSfx.length > launchAt + 0.05f)
            {
                lines.Add("  ⚠ charge が長すぎます（" + chargeSfx.length.ToString("F2")
                    + "秒）。発射SEと重なって濁ります。");
            }
        }

        lines.Add("点火: " + CometStartFrame + " フレーム目 / 全 " + fireFrames.ToString("F0")
            + " フレーム = 正規化 "
            + (fireFrames > 0.5f ? (CometStartFrame / fireFrames).ToString("F4") : "?")
            + " = " + (fireClip != null ? (CometStartFrame / fireClip.frameRate).ToString("F3") : "?") + "秒"
            + " / 敷く尾 " + CometSeedLength.ToString("F2") + "m");
        Debug.Log("[BuildYakumanFly] 検証\n" + string.Join("\n", lines.ToArray()));
    }

    static string DescribeClip(AudioClip clip, float volume)
    {
        if (clip == null) { return "なし（無音のまま）"; }
        return clip.name + " " + clip.length.ToString("F2") + "秒 / volume " + volume.ToString("F3");
    }

    static bool AllSet(Transform[] arr)
    {
        foreach (var t in arr) { if (t == null) { return false; } }
        return true;
    }

    static bool AllSetTrails(TrailRenderer[] arr)
    {
        foreach (var t in arr) { if (t == null) { return false; } }
        return true;
    }

    // ================================================================ リグ

    static bool BuildOne(Transform tableRoot)
    {
        var cutinRoot = tableRoot.Find(CutinRootName);
        if (cutinRoot == null)
        {
            Debug.LogWarning("[BuildYakumanFly] " + tableRoot.name + " に " + CutinRootName
                + " がありません。先に Build Yakuman Cutin を実行してください。");
            return false;
        }

        var rig = EnsureChild(cutinRoot, RigName);

        // リグが寝ていると Animator が評価されず、クリップが一切進まない。
        // （卓ごとに手で落とされていて、その卓だけ打ち上げが無反応になる事故が実際にあった）
        if (!rig.gameObject.activeSelf) { rig.gameObject.SetActive(true); }

        // リグは「ずらし量の入れ物」なので、必ず原点・無回転・等倍から始まる。
        // ここがズレるとクリップの数値の意味が変わる。
        rig.localPosition = Vector3.zero;
        rig.localRotation = Quaternion.identity;
        rig.localScale = Vector3.one;

        EnsureSlot(rig, WinSlotName);
        for (int i = 0; i < HandSlots; ++i) { EnsureSlot(rig, SlotName(i)); }

        // 余分なスロットが残っていると Animation ウィンドウで迷うので掃除する。
        for (int i = rig.childCount - 1; i >= 0; --i)
        {
            var c = rig.GetChild(i);
            if (c.name == WinSlotName) { continue; }
            if (c.name.StartsWith("Hand") && SlotIndex(c.name) >= 0 && SlotIndex(c.name) < HandSlots) { continue; }
            Object.DestroyImmediate(c.gameObject);
        }

        var animator = rig.GetComponent<Animator>();
        if (animator == null) { animator = rig.gameObject.AddComponent<Animator>(); }

        animator.runtimeAnimatorController =
            AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        animator.applyRootMotion = false;
        // リグに Renderer は無い。CullUpdateTransforms のままだと「見えていない」と
        // 判定されて評価が止まり、牌が固まる。必ず AlwaysAnimate にすること。
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        animator.updateMode = AnimatorUpdateMode.Normal;

        EnsureComet(cutinRoot);

        EditorUtility.SetDirty(rig.gameObject);
        return true;
    }

    /// <summary>
    /// 和了牌に付ける彗星の尾を組み立てる。
    ///
    /// 牌そのものにトレイルを親子付けしないのは、牌オブジェクトが136枚を毎局
    /// 使い回すため（尾が次の局へ持ち越される）。実行時は Mahjong.cs が渡してくる
    /// 和了牌の世界座標を、この入れ物が毎フレーム追いかける。
    ///
    /// FlyRig の外（YakumanCutin 直下）へ置くのは、リグの子は Animator が
    /// カーブで localPosition を上書きしてしまうため。
    /// </summary>
    static void EnsureComet(Transform cutinRoot)
    {
        var root = EnsureChild(cutinRoot, CometName);
        root.localPosition = Vector3.zero;
        root.localRotation = Quaternion.identity;
        root.localScale = Vector3.one;

        // コマ（核を包む光）は尾の素材の有無に関係なく置く。
        EnsureComa(root);

        var src = AssetDatabase.LoadAssetAtPath<GameObject>(CometVfxPath);
        if (src == null)
        {
            Debug.LogWarning("[BuildYakumanFly] 彗星の素材が見つかりません: " + CometVfxPath);
            root.gameObject.SetActive(false);
            return;
        }

        var srcTrails = src.GetComponentsInChildren<TrailRenderer>(true);

        // 中身が既に素材どおりなら作り直さない。作り直すとシーンの8卓それぞれに
        // 「子を足した」差分が乗ってしまう（卓 Prefab の継承が切れる）。
        var kept = root.Find(CometVfxChildName);
        if (kept != null && !MatchesSource(kept, srcTrails))
        {
            Object.DestroyImmediate(kept.gameObject);
            kept = null;
        }

        if (kept != null)
        {
            ApplyCometTrails(kept, srcTrails);
            root.gameObject.SetActive(false);
            return;
        }

        var inst = (GameObject)PrefabUtility.InstantiatePrefab(src);

        // 入れ子 Prefab のままにすると、卓 Prefab 側の差分と素材側の更新が
        // ぶつかって値がどちらに乗るか読めなくなる。完全に展開して素の
        // GameObject にしておく（マテリアルは参照のまま）。
        PrefabUtility.UnpackPrefabInstance(inst, PrefabUnpackMode.Completely,
            InteractionMode.AutomatedAction);

        inst.name = CometVfxChildName;
        inst.transform.SetParent(root, false);
        inst.transform.localPosition = Vector3.zero;
        inst.transform.localRotation = Quaternion.identity;
        inst.transform.localScale = Vector3.one;

        ApplyCometTrails(inst.transform, srcTrails);

        // 打ち上げが始まるまでは寝かせておく。
        root.gameObject.SetActive(false);
        EditorUtility.SetDirty(root.gameObject);
    }

    /// <summary>組み立て済みの中身が素材と同じ形か。違えば作り直す。</summary>
    static bool MatchesSource(Transform vfx, TrailRenderer[] srcTrails)
    {
        var dst = vfx.GetComponentsInChildren<TrailRenderer>(true);
        if (dst.Length != srcTrails.Length) { return false; }
        for (int i = 0; i < dst.Length; ++i)
        {
            if (dst[i] == null || dst[i].sharedMaterial != srcTrails[i].sharedMaterial) { return false; }
        }
        return true;
    }

    /// <summary>幅と尺は「元の値 × 倍率」。層ごとの比率（芯:外側）を壊さずに縮められる。</summary>
static void ApplyCometTrails(Transform vfx, TrailRenderer[] srcTrails)
    {
        var distortion = vfx.Find(CometDistortionName);
        if (distortion != null && distortion.gameObject.activeSelf)
        {
            distortion.gameObject.SetActive(false);
        }

        // 素材に付いてくる SE（Vefects_SFX_Trail_Fire）を落とす。playOnAwake なので
        // 尾を点けた瞬間に鳴り、こちらで配った打ち上げSEと二重になる。
        StripCometAudio(vfx);

        var dst = vfx.GetComponentsInChildren<TrailRenderer>(true);
        for (int i = 0; i < dst.Length; ++i)
        {
            var t = dst[i];
            float baseWidth = (i < srcTrails.Length) ? srcTrails[i].widthMultiplier : 1.0f;
            float baseTime = (i < srcTrails.Length) ? srcTrails[i].time : 1.0f;

            t.widthMultiplier = baseWidth * CometWidthScale;
            t.time = baseTime * CometTimeScale;
            t.minVertexDistance = CometMinVertexDistance;
            t.autodestruct = false;
            t.emitting = false;

            // 影・プローブは要らない。Quest では純粋な無駄。
            t.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            t.receiveShadows = false;
            t.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            t.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }
    }

    // ================================================================ コマ（核を包む光）

    /// <summary>
    /// 和了牌を包む「コマ」を組み立てる。
    ///
    /// 彗星は <b>核（Nucleus）・コマ（Coma）・尾（Tail）</b> の3点が揃って
    /// 彗星に見える。核＝飛んでいる牌、尾＝TrailRenderer、ここが真ん中のコマ。
    ///
    /// 中身は加算のビルボード板を重ねただけ（内側ほど小さく熱い）。
    /// 実行時は MahjongYakumanCutin が毎フレーム、牌の位置へ置いて視点の方へ
    /// 向け、速さに応じて膨らませる。<b>板の localScale が基準の大きさ</b>なので、
    /// 大きさを変えるときは ComaSizes を触って Build を回す。
    /// </summary>
    static void EnsureComa(Transform cometRoot)
    {
        var mat = EnsureComaMaterial();
        var mesh = QuadMesh();

        var coma = EnsureChild(cometRoot, ComaName);
        coma.localPosition = Vector3.zero;
        coma.localRotation = Quaternion.identity;
        coma.localScale = Vector3.one;

        var keep = new string[ComaSizes.Length];
        for (int i = 0; i < ComaSizes.Length; ++i)
        {
            string name = ComaName + i;
            keep[i] = name;

            var q = EnsureQuad(coma, name, mesh, mat);
            q.localPosition = Vector3.zero;
            q.localRotation = Quaternion.identity;
            q.localScale = Vector3.one * ComaSizes[i];
        }

        CleanupChildren(coma, keep);
    }

    /// <summary>今のコマの大きさを1行で出す。卓ごとに違っていたらそれも言う。</summary>
    static string DescribeComa(Mahjong[] tables)
    {
        MahjongYakumanCutin first = null;
        bool mixed = false;

        foreach (var t in tables)
        {
            var c = FindCutin(t.transform);
            if (c == null) { continue; }
            if (first == null) { first = c; continue; }
            if (!SameComaLook(first, c)) { mixed = true; }
        }

        if (first == null) { return "大きさ: 卓が見つかりません"; }

        var sb = new System.Text.StringBuilder();
        sb.Append("大きさ: 倍率 ").Append(first.comaScale.ToString("F2")).Append(" × [");
        if (first.flyComaSizes != null)
        {
            for (int i = 0; i < first.flyComaSizes.Length; ++i)
            {
                if (i > 0) { sb.Append(" / "); }
                sb.Append((first.flyComaSizes[i] * first.comaScale).ToString("F3"));
            }
        }
        sb.Append("]m　手前へ ").Append(first.comaFront.ToString("F3")).Append("m");
        if (mixed) { sb.Append("　※卓ごとに値が違います（Apply Yakuman Coma To All Tables で揃う）"); }
        return sb.ToString();
    }

    static bool SameComaLook(MahjongYakumanCutin a, MahjongYakumanCutin b)
    {
        if (!Mathf.Approximately(a.comaScale, b.comaScale)) { return false; }
        if (!Mathf.Approximately(a.comaFront, b.comaFront)) { return false; }
        if (a.flyComaSizes == null || b.flyComaSizes == null) { return a.flyComaSizes == b.flyComaSizes; }
        if (a.flyComaSizes.Length != b.flyComaSizes.Length) { return false; }
        for (int i = 0; i < a.flyComaSizes.Length; ++i)
        {
            if (!Mathf.Approximately(a.flyComaSizes[i], b.flyComaSizes[i])) { return false; }
        }
        return true;
    }

    // ---------------------------------------------------------------- 調整

    /// <summary>
    /// 1卓の Inspector で合わせたコマの見た目を、残りの卓へ丸ごと配る。
    ///
    /// 調整の流れはこれだけ:
    ///   1. 卓をひとつ選んで YakumanCutin の「コマ」欄を触る
    ///      （まとめて大小したいだけなら <b>comaScale</b> だけでよい）
    ///   2. このメニューを回す → 8卓ぜんぶに同じ値が入る
    ///
    /// 選択が無ければ最初に見つかった卓を元にする。
    /// </summary>
    [MenuItem("Tools/Kemono Mahjong/Apply Yakuman Coma To All Tables")]
    public static void ApplyComaToAll()
    {
        var tables = Object.FindObjectsOfType<Mahjong>(true);
        if (tables == null || tables.Length == 0)
        {
            Debug.LogWarning("[BuildYakumanFly] シーンに Mahjong が見つかりません。");
            return;
        }

        MahjongYakumanCutin src = null;
        if (Selection.activeGameObject != null)
        {
            var t = Selection.activeGameObject.transform;
            while (t != null && src == null)
            {
                if (t.GetComponent<Mahjong>() != null) { src = FindCutin(t); }
                t = t.parent;
            }
        }
        if (src == null) { src = FindCutin(tables[0].transform); }
        if (src == null)
        {
            Debug.LogWarning("[BuildYakumanFly] 元になる MahjongYakumanCutin が見つかりません。");
            return;
        }

        int n = 0;
        foreach (var t in tables)
        {
            var dst = FindCutin(t.transform);
            if (dst == null) { continue; }
            if (dst != src) { CopyComaLook(src, dst); }
            UdonSharpEditorUtility.CopyProxyToUdon(dst);
            EditorUtility.SetDirty(dst);
            ++n;
        }

        MarkSceneDirty();
        Debug.Log("[BuildYakumanFly] コマの見た目を " + n + " 卓へ配りました（元: "
            + src.transform.root.name + " / 倍率 " + src.comaScale.ToString("F2") + "）。");
    }

    /// <summary>コマの見た目を builder の既定値へ戻す（触りすぎて分からなくなったとき用）。</summary>
public static void ResetComaLook()
    {
        var tables = Object.FindObjectsOfType<Mahjong>(true);
        int n = 0;
        foreach (var t in tables)
        {
            var cutin = FindCutin(t.transform);
            if (cutin == null) { continue; }
            ApplyComaDefaults(cutin);
            UdonSharpEditorUtility.CopyProxyToUdon(cutin);
            EditorUtility.SetDirty(cutin);
            ++n;
        }

        int prefabN = ResetComaLookInPrefab();
        MarkSceneDirty();
        Debug.Log("[BuildYakumanFly] コマの見た目を既定値へ戻しました（"
            + n + " 卓 / Prefab " + prefabN + " 件）。");
    }

static int ResetComaLookInPrefab()
    {
        // UdonSharpBehaviour は LoadPrefabContents + SaveAsPrefabAsset だと
        // Odin のシリアライズ callback で null を踏むため、標準 Prefab Stage 保存を通す。
        var stage = PrefabStageUtility.OpenPrefab(TablePrefabPath);
        if (stage == null || stage.prefabContentsRoot == null)
        {
            Debug.LogWarning("[BuildYakumanFly] 卓Prefabを開けません: " + TablePrefabPath);
            StageUtility.GoToMainStage();
            return 0;
        }

        int n = 0;
        try
        {
            var cutins = stage.prefabContentsRoot.GetComponentsInChildren<MahjongYakumanCutin>(true);
            foreach (var cutin in cutins)
            {
                if (cutin == null) { continue; }
                ApplyComaDefaults(cutin);
                UdonSharpEditorUtility.CopyProxyToUdon(cutin);
                EditorUtility.SetDirty(cutin);
                ++n;
            }

            if (n > 0) { SavePrefabStage(stage); }
            else { StageUtility.GoToMainStage(); }
            return n;
        }
        catch
        {
            StageUtility.GoToMainStage();
            throw;
        }
    }


    /// <summary>見た目の数値が入っているか。空なら Wire が既定値を流し込む。</summary>
    static bool HasComaLook(MahjongYakumanCutin cutin)
    {
        int n = ComaSizes.Length;
        return cutin.flyComaSizes != null && cutin.flyComaSizes.Length == n
            && cutin.flyComaColors != null && cutin.flyComaColors.Length == n
            && cutin.flyComaAlphas != null && cutin.flyComaAlphas.Length == n
            && cutin.comaScale > 0.0001f;
    }

    static void ApplyComaDefaults(MahjongYakumanCutin cutin)
    {
        cutin.flyComaSizes = (float[])ComaSizes.Clone();
        cutin.flyComaColors = (Color[])ComaColors.Clone();
        cutin.flyComaAlphas = (float[])ComaAlphas.Clone();
        cutin.comaScale = ComaScale;
        cutin.comaFront = ComaFront;
        cutin.comaFadeInSeconds = ComaFadeInSeconds;
        cutin.comaIgnitionFlash = ComaIgnitionFlash;
        cutin.comaSpeedFull = ComaSpeedFull;
        cutin.comaSpeedGain = ComaSpeedGain;
        cutin.comaStretch = ComaStretch;
        cutin.comaLag = ComaLag;
        cutin.comaFlicker = ComaFlicker;
        cutin.comaFlickerHz = ComaFlickerHz;
    }

    static void CopyComaLook(MahjongYakumanCutin src, MahjongYakumanCutin dst)
    {
        dst.flyComaSizes = src.flyComaSizes == null ? null : (float[])src.flyComaSizes.Clone();
        dst.flyComaColors = src.flyComaColors == null ? null : (Color[])src.flyComaColors.Clone();
        dst.flyComaAlphas = src.flyComaAlphas == null ? null : (float[])src.flyComaAlphas.Clone();
        dst.comaScale = src.comaScale;
        dst.comaFront = src.comaFront;
        dst.comaFadeInSeconds = src.comaFadeInSeconds;
        dst.comaIgnitionFlash = src.comaIgnitionFlash;
        dst.comaSpeedFull = src.comaSpeedFull;
        dst.comaSpeedGain = src.comaSpeedGain;
        dst.comaStretch = src.comaStretch;
        dst.comaLag = src.comaLag;
        dst.comaFlicker = src.comaFlicker;
        dst.comaFlickerHz = src.comaFlickerHz;
    }

    /// <summary>加算のパーティクル用マテリアル。絵が無ければ先に焼く。</summary>
    static Material EnsureComaMaterial()
    {
        EnsureComaTexture();

        var mat = AssetDatabase.LoadAssetAtPath<Material>(ComaMaterialPath);
        if (mat == null)
        {
            var sh = FindComaShader();
            if (sh == null)
            {
                Debug.LogError("[BuildYakumanFly] 加算シェーダーが見つからず、コマを作れません。");
                return null;
            }

            EnsureFolder(ComaMaterialDir);
            mat = new Material(sh);
            AssetDatabase.CreateAsset(mat, ComaMaterialPath);
        }
        else if (!mat.HasProperty("_TintColor"))
        {
            // 濃さは実行時に MaterialPropertyBlock の _TintColor で作る。
            // この口が無いシェーダー（Mobile/Particles/Additive は _MainTex だけ）を
            // つかんでいたら差し替える。放置するとコマが白で出っぱなしになる。
            var sh = FindComaShader();
            if (sh != null) { mat.shader = sh; }
        }

        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(ComaTexturePath);
        if (tex != null && mat.HasProperty("_MainTex")) { mat.SetTexture("_MainTex", tex); }

        // 既定は消えている状態。実行時に MaterialPropertyBlock が塗るので、
        // ここを明るくしておくとエディタで卓の上に光の玉が残る。
        if (mat.HasProperty("_TintColor")) { mat.SetColor("_TintColor", new Color(1f, 1f, 1f, 0f)); }

        EditorUtility.SetDirty(mat);
        return mat;
    }

    /// <summary>
    /// 加算（Blend SrcAlpha One）で、かつ <b>_TintColor を持つ</b>シェーダー。
    /// 濃さの口が無いと実行時に消せない（コマが白で出っぱなしになる）。
    /// Mobile 版は安いが _MainTex しか無いので使えない。
    /// </summary>
    static Shader FindComaShader()
    {
        var names = new string[]
        {
            "Legacy Shaders/Particles/Additive",
            "Particles/Additive",
            "Mobile/Particles/Additive",
        };

        Shader first = null;
        for (int i = 0; i < names.Length; ++i)
        {
            var sh = Shader.Find(names[i]);
            if (sh == null) { continue; }
            if (first == null) { first = sh; }

            var probe = new Material(sh);
            bool hasTint = probe.HasProperty("_TintColor");
            Object.DestroyImmediate(probe);
            if (hasTint) { return sh; }
        }

        if (first != null)
        {
            Debug.LogWarning("[BuildYakumanFly] _TintColor を持つ加算シェーダーが無く、"
                + first.name + " で代用します（実行時に濃さを変えられません）。");
        }
        return first;
    }

    /// <summary>
    /// コマの絵。中心から外へなだらかに消える丸い光。
    /// <b>芯はわざと少し抜いてある</b>＝加算で3枚重ねても和了牌の絵柄が白飛びしない
    /// （核が見えなくなったら「牌が飛んでいる」演出として本末転倒）。
    /// </summary>
static void EnsureComaTexture()
    {
        // Regenerate on every explicit Build while keeping this asset's GUID stable.
        // The mask separates a compact nose cap from a soft, progressively fading tail.
        EnsureFolder(FxTextureDir);

        const int size = 256;
        var px = new Color[size * size];
        float half = size * 0.5f;

        for (int y = 0; y < size; ++y)
        {
            for (int x = 0; x < size; ++x)
            {
                // +Y follows the launch direction. The winning tile is centred at dy = 0.
                float dx = (x + 0.5f - half) / half;
                float dy = (y + 0.5f - half) / half;

                // Rounded golden nose: taller and wider than the tile, without a flat cyan rim.
                float capX = dx / 0.15f;
                float capY = (dy - 0.14f) / 0.10f;
                float capR = Mathf.Sqrt(capX * capX + capY * capY);
                float cap = Mathf.SmoothStep(0.0f, 1.0f, Mathf.Clamp01(1.0f - capR));

                // Broad upper shell, then a decisive taper through the lower half.
                float tailT = Mathf.Clamp01((0.14f - dy) / 1.14f);
                float tailHalf = Mathf.Lerp(0.58f, 0.025f, Mathf.Pow(tailT, 0.80f));
                float tailSide = Mathf.Clamp01(1.0f - Mathf.Abs(dx) / Mathf.Max(0.001f, tailHalf));
                tailSide = Mathf.SmoothStep(0.0f, 1.0f, tailSide);
                float tailTop = Mathf.Clamp01((0.16f - dy) / 0.10f);
                float tailBottom = Mathf.Clamp01((dy + 1.0f) / 0.10f);
                float tailFade = Mathf.Lerp(1.0f, 0.10f, Mathf.Pow(tailT, 1.20f));
                float tail = tailSide * tailTop * tailBottom * tailFade;

                // Keep the white centre subordinate to the wide coloured shell.
                float coreHalf = Mathf.Lerp(0.24f, 0.008f, Mathf.Pow(tailT, 0.75f));
                float coreSide = Mathf.Clamp01(1.0f - Mathf.Abs(dx) / Mathf.Max(0.001f, coreHalf));
                coreSide = Mathf.SmoothStep(0.0f, 1.0f, coreSide);
                float coreFade = Mathf.Lerp(1.0f, 0.02f, Mathf.Pow(tailT, 0.45f));
                float core = coreSide * coreSide * tailTop * tailBottom * coreFade;

                float shape = Mathf.Max(cap * 0.92f, tail * 0.82f);
                shape = Mathf.Max(shape, core * 0.52f);

                // Fallback face window for oblique views. Normally the whole sheet is
                // placed behind the opaque tile, so the tile itself remains readable.
                float insideFaceX = 1.0f - Mathf.SmoothStep(0.07f, 0.13f, Mathf.Abs(dx));
                float insideFaceY = Mathf.SmoothStep(-0.13f, -0.05f, dy)
                    * (1.0f - Mathf.SmoothStep(0.13f, 0.22f, dy));
                float faceReveal = 1.0f - 0.82f * insideFaceX * insideFaceY;

                float alpha = Mathf.Clamp01(shape * faceReveal);

                // Gold head -> cream shoulder -> pale cyan body, with a faint pink filament at the tip.
                float warmBody = Mathf.SmoothStep(-0.65f, -0.05f, dy);
                Color bodyHue = Color.Lerp(
                    new Color(0.58f, 0.84f, 1.00f, 1.0f),
                    new Color(1.00f, 0.92f, 0.72f, 1.0f),
                    warmBody);
                float headBlend = Mathf.SmoothStep(-0.02f, 0.20f, dy);
                Color hue = Color.Lerp(
                    bodyHue,
                    new Color(1.00f, 0.72f, 0.18f, 1.0f),
                    headBlend);
                float ember = 1.0f - Mathf.SmoothStep(-0.90f, -0.55f, dy);
                hue = Color.Lerp(hue, new Color(1.00f, 0.62f, 0.72f, 1.0f), ember * 0.22f);
                float whiten = Mathf.Clamp01(core * 0.38f + cap * 0.32f);
                hue = Color.Lerp(hue, Color.white, whiten);
                px[y * size + x] = new Color(hue.r, hue.g, hue.b, alpha);
            }
        }

        WriteTexture(ComaTexturePath, size, size, px);

    }

static void WriteTexture(string path, int width, int height, Color[] pixels)
    {
        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
        tex.SetPixels(pixels);
        tex.Apply();

        var png = tex.EncodeToPNG();
        Object.DestroyImmediate(tex);
        System.IO.File.WriteAllBytes(path, png);

        // Finish the first import before touching the importer. One synchronous
        // SaveAndReimport avoids competing writes to the generated .meta file.
        AssetDatabase.ImportAsset(path,
            ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer != null)
        {
            importer.textureType = TextureImporterType.Default;
            importer.alphaIsTransparency = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.mipmapEnabled = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
        }
    }

    /// <summary>組み込みの Quad。取れない版では一度作って形だけ借りる。</summary>
    static Mesh QuadMesh()
    {
        var mesh = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
        if (mesh != null) { return mesh; }

        var tmp = GameObject.CreatePrimitive(PrimitiveType.Quad);
        var mf = tmp.GetComponent<MeshFilter>();
        mesh = mf != null ? mf.sharedMesh : null;
        Object.DestroyImmediate(tmp);
        return mesh;
    }

    static Transform EnsureQuad(Transform parent, string name, Mesh mesh, Material mat)
    {
        var t = parent.Find(name);
        GameObject go;
        if (t != null)
        {
            go = t.gameObject;
        }
        else
        {
            go = new GameObject(name);
            go.transform.SetParent(parent, false);
        }

        var mf = go.GetComponent<MeshFilter>();
        if (mf == null) { mf = go.AddComponent<MeshFilter>(); }
        mf.sharedMesh = mesh;

        var mr = go.GetComponent<MeshRenderer>();
        if (mr == null) { mr = go.AddComponent<MeshRenderer>(); }
        if (mat != null) { mr.sharedMaterial = mat; }

        // 影・プローブ・当たり判定は要らない。Quest では純粋な無駄。
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        mr.allowOcclusionWhenDynamic = false;

        var col = go.GetComponent<Collider>();
        if (col != null) { Object.DestroyImmediate(col); }

        return go.transform;
    }

    static void CleanupChildren(Transform parent, string[] keepNames)
    {
        for (int i = parent.childCount - 1; i >= 0; --i)
        {
            var child = parent.GetChild(i);
            bool keep = false;
            for (int k = 0; k < keepNames.Length; ++k)
            {
                if (child.name == keepNames[k]) { keep = true; break; }
            }
            if (!keep) { Object.DestroyImmediate(child.gameObject); }
        }
    }

    static bool AllSetRenderers(MeshRenderer[] arr)
    {
        if (arr == null) { return false; }
        for (int i = 0; i < arr.Length; ++i) { if (arr[i] == null) { return false; } }
        return true;
    }

    static string SlotName(int i) { return "Hand" + i.ToString("00"); }

    static int SlotIndex(string name)
    {
        int v;
        if (name.Length == 6 && int.TryParse(name.Substring(4), out v)) { return v; }
        return -1;
    }

    static Transform EnsureSlot(Transform parent, string name)
    {
        var t = EnsureChild(parent, name);
        if (!t.gameObject.activeSelf) { t.gameObject.SetActive(true); }
        t.localPosition = Vector3.zero;
        t.localRotation = Quaternion.identity;
        t.localScale = Vector3.one;
        return t;
    }

    /// <summary>彗星の素材に同梱された SE を消す。打ち上げSEと二重に鳴るため。</summary>
    static int StripCometAudio(Transform vfx)
    {
        if (vfx == null) { return 0; }

        // VRCSpatialAudioSource が AudioSource を RequireComponent で掴んでいる。
        // 先に外さないと、AudioSource を消した瞬間に Unity が空のものを付け直す
        //（clip だけ消えて器が残る＝Validate が毎回警告する）。
        var comps = vfx.GetComponentsInChildren<Component>(true);
        for (int i = comps.Length - 1; i >= 0; --i)
        {
            if (comps[i] == null) { continue; }
            if (comps[i].GetType().Name != "VRCSpatialAudioSource") { continue; }
            Object.DestroyImmediate(comps[i]);
        }

        var sources = vfx.GetComponentsInChildren<AudioSource>(true);
        int removed = 0;
        for (int i = sources.Length - 1; i >= 0; --i)
        {
            if (sources[i] == null) { continue; }
            var go = sources[i].gameObject;
            Object.DestroyImmediate(sources[i]);
            ++removed;

            // それでも付け直されるなら（別の何かが掴んでいる）、せめて鳴らないようにする。
            var left = go.GetComponent<AudioSource>();
            if (left != null)
            {
                left.clip = null;
                left.playOnAwake = false;
            }
        }
        return removed;
    }

    /// <summary>彗星の下に残っている AudioSource の数。Validate 用。</summary>
    static int CountCometAudio(Transform cutinRoot)
    {
        if (cutinRoot == null) { return 0; }
        var comet = cutinRoot.Find(CometName);
        return comet == null ? 0 : comet.GetComponentsInChildren<AudioSource>(true).Length;
    }

    /// <summary>SEフォルダから、名前がこの接頭辞で始まる最初のクリップを拾う。</summary>
    static AudioClip FindSfx(string prefix)
    {
        if (!AssetDatabase.IsValidFolder(SfxDir)) { return null; }

        foreach (var guid in AssetDatabase.FindAssets("t:AudioClip", new[] { SfxDir }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (name == null) { continue; }
            if (!name.ToLowerInvariant().StartsWith(prefix)) { continue; }

            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (clip != null) { return clip; }
        }
        return null;
    }

    /// <summary>
    /// 1本もの（打ち上げ全体を覆うSE）を拾う。正は fly* だが、接頭辞の無い音源が
    /// 1本だけ置かれている場合（DAW から出したままの日本語名など）はそれを1本ものとして扱う。
    /// charge* / launch* があるときは2本立ての意思表示なので横取りしない。
    /// </summary>
    static AudioClip FindFullSfx()
    {
        var tagged = FindSfx(FullPrefix);
        if (tagged != null) { return tagged; }
        if (FindSfx(ChargePrefix) != null || FindSfx(LaunchPrefix) != null) { return null; }

        // 2本以上あるとどれを全体に使うのか決められない＝黙って選ばず、Validate で知らせる。
        var loose = FindUntaggedSfx();
        return loose.Count == 1 ? loose[0] : null;
    }

    /// <summary>接頭辞のどれにも当てはまらない音源を集める。</summary>
    static List<AudioClip> FindUntaggedSfx()
    {
        var found = new List<AudioClip>();
        if (!AssetDatabase.IsValidFolder(SfxDir)) { return found; }

        foreach (var guid in AssetDatabase.FindAssets("t:AudioClip", new[] { SfxDir }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (name == null) { continue; }

            var lower = name.ToLowerInvariant();
            if (lower.StartsWith(FullPrefix)) { continue; }
            if (lower.StartsWith(ChargePrefix)) { continue; }
            if (lower.StartsWith(LaunchPrefix)) { continue; }

            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (clip != null) { found.Add(clip); }
        }
        return found;
    }

    static Transform EnsureChild(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null) { return t; }

        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go.transform;
    }

    // ================================================================ 配線

    static bool WireOne(Transform tableRoot)
    {
        var cutin = FindCutin(tableRoot);
        if (cutin == null) { return false; }

        var rig = cutin.transform.Find(RigName);
        if (rig == null)
        {
            Debug.LogWarning("[BuildYakumanFly] " + tableRoot.name + " に " + RigName
                + " がありません。先に Build Yakuman Fly を実行してください。");
            return false;
        }

        var animator = rig.GetComponent<Animator>();
        if (animator != null && animator.runtimeAnimatorController == null)
        {
            animator.runtimeAnimatorController =
                AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        }

        var slots = new Transform[HandSlots];
        for (int i = 0; i < HandSlots; ++i) { slots[i] = rig.Find(SlotName(i)); }

        cutin.flyAnimator = animator;
        cutin.flyStateName = StateName;
        cutin.flyWinTile = rig.Find(WinSlotName);
        cutin.flyHandSlots = slots;

        var comet = cutin.transform.Find(CometName);
        cutin.flyComet = comet;
        cutin.flyCometTrails = comet == null
            ? new TrailRenderer[0]
            : comet.GetComponentsInChildren<TrailRenderer>(true);
        // 打ち上げのSE。フォルダに音源があれば拾う。無ければ手で割り当てた分を消さない。
        var fullClip = FindFullSfx();
        var chargeClip = FindSfx(ChargePrefix);
        var launchClip = FindSfx(LaunchPrefix);
        if (fullClip != null) { cutin.flyFullSound = fullClip; }
        if (chargeClip != null) { cutin.flyChargeSound = chargeClip; }
        if (launchClip != null) { cutin.flyLaunchSound = launchClip; }
        cutin.flyFullVolume = KemonoAudioNormalizer.SeScale(cutin.flyFullSound, FullVolume);
        cutin.flyChargeVolume = ChargeVolume;
        cutin.flyLaunchVolume = LaunchVolume;

        cutin.cometSeedLength = CometSeedLength;
        cutin.cometSeedPoints = CometSeedPoints;
        cutin.cometOffset = CometOffset;

        // コマ（核を包む光）。板は Build が置くので、ここは参照だけ入れる。
        var coma = comet != null ? comet.Find(ComaName) : null;
        cutin.flyComa = coma;
        cutin.flyComaLayers = coma == null
            ? new MeshRenderer[0]
            : coma.GetComponentsInChildren<MeshRenderer>(true);

        // 見た目の数値（大きさ・色・濃さ）は「空のときだけ」入れる。
        // ここで毎回上書きすると Inspector で合わせた値が Wire のたびに消える
        // ＝調整できない。既定値へ戻したいときは Reset Yakuman Coma (Look) を回す。
        if (!HasComaLook(cutin)) { ApplyComaDefaults(cutin); }

        // 尺はクリップが正。手で入れた値とズレると後段（役満カットイン・あがり画面）が
        // 前後するので、クリップの長さをそのまま入れる。
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipPath);
        cutin.flySeconds = clip != null ? clip.length : ClipSeconds;

        // 点火フレーム → 正規化時刻。クリップを焼き直しても Wire で追従する。
        float totalFrames = clip != null ? clip.length * clip.frameRate : 0.0f;
        // 半フレーム手前に置く。ちょうど N/総数 だと、実行時の t が同じ値でも
        // float の丸めで「< 開始時刻」となって1フレーム遅れる（実測で90フレーム目に点いた）。
        cutin.cometStartT = totalFrames > 0.5f
            ? Mathf.Clamp01((CometStartFrame - 0.5f) / totalFrames)
            : 0.708f;

        // 発射SEも同じくフレーム→正規化時刻。半フレーム手前に置くのは彗星と同じ理由。
        cutin.flyLaunchT = totalFrames > 0.5f
            ? Mathf.Clamp01((LaunchFrame - 0.5f) / totalFrames)
            : 0.676f;

        // U# はフィールドを増減したあと必ずこれを通す（Udon 側へ値が移る）。
        UdonSharpEditorUtility.CopyProxyToUdon(cutin);
        EditorUtility.SetDirty(cutin);
        return true;
    }

    static MahjongYakumanCutin FindCutin(Transform tableRoot)
    {
        var root = tableRoot.Find(CutinRootName);
        if (root == null) { return null; }
        return root.GetComponent<MahjongYakumanCutin>();
    }

    // ================================================================ クリップ

    static void EnsureClip(bool force)
    {
        EnsureFolder(ClipDir);

        var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipPath);
        if (existing != null && !force)
        {
            EnsureController();
            return;
        }

        if (existing != null && !ConfirmOverwrite(existing)) { return; }

        var clip = BuildClip();
        AssetDatabase.CreateAsset(clip, ClipPath);
        AssetDatabase.SaveAssets();

        EnsureController();
        RememberStamp();

        int keys = 0;
        var bindings = AnimationUtility.GetCurveBindings(clip);
        foreach (var b in bindings) { keys += AnimationUtility.GetEditorCurve(clip, b).length; }

        Debug.Log("[BuildYakumanFly] クリップを焼きました: " + ClipPath
            + " / " + clip.length.ToString("F2") + "秒 / カーブ " + bindings.Length
            + " 本 / キー " + keys + " 個");
    }

    static bool ConfirmOverwrite(AnimationClip clip)
    {
        string now = Fingerprint(clip);
        string stamp = EditorPrefs.GetString(StampKey, "");

        if (stamp.Length > 0 && stamp != now)
        {
            bool go = EditorUtility.DisplayDialog(
                "打ち上げクリップを焼き直す",
                "前回の焼き込み以降にクリップが変更されています。\n"
                + "焼き直すと、手で編集したカーブは残りません。\n\n"
                + "続けますか？（控えは " + BackupDir + " に残ります）",
                "焼き直す", "やめる");
            if (!go) { return false; }
        }

        EnsureFolder(BackupDir);
        string dst = BackupDir + "/YakumanFly_"
            + System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".anim";
        if (AssetDatabase.CopyAsset(ClipPath, dst))
        {
            Debug.Log("[BuildYakumanFly] 上書き前の控え: " + dst);
        }
        return true;
    }

    static string Fingerprint(AnimationClip clip)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var b in AnimationUtility.GetCurveBindings(clip))
        {
            var c = AnimationUtility.GetEditorCurve(clip, b);
            sb.Append(b.path).Append('/').Append(b.propertyName).Append(':').Append(c.length);
            for (int i = 0; i < c.length; ++i)
            {
                sb.Append(',').Append(c[i].time.ToString("F3"))
                  .Append('=').Append(c[i].value.ToString("F4"));
            }
            sb.Append(';');
        }
        return sb.ToString();
    }

    static void RememberStamp()
    {
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipPath);
        if (clip != null) { EditorPrefs.SetString(StampKey, Fingerprint(clip)); }
    }

    // ---------------------------------------------------------------- 振り付け

    /// <summary>
    /// 既定の振り付けを作る。段は4つ。
    ///
    ///   0.00 - 0.10  溜め   … その場で小刻みに震え、わずかに沈む
    ///   0.10 - 0.22  浮上   … 卓から離れて立ち上がる
    ///   0.22 - 0.55  滞空   … ゆっくり漂う（ここで「見せる」）
    ///   0.55 - 1.00  打ち上げ … 加速して空へ抜ける。終盤で縮んで消える
    ///
    /// 和了牌だけは先に動き出し、一番高く、最後に抜ける（主役として立たせる）。
    /// 手牌は席の左右へ少し散らし、打ち上げの時刻をずらして流れを作る。
    /// </summary>
    static AnimationClip BuildClip()
    {
        var clip = new AnimationClip();
        clip.frameRate = 60.0f;

        var settings = AnimationUtility.GetAnimationClipSettings(clip);
        settings.loopTime = false;
        settings.stopTime = ClipSeconds;
        AnimationUtility.SetAnimationClipSettings(clip, settings);

        WriteSlot(clip, WinSlotName, -1);
        for (int i = 0; i < HandSlots; ++i) { WriteSlot(clip, SlotName(i), i); }

        return clip;
    }

    static void WriteSlot(AnimationClip clip, string path, int index)
    {
        bool win = index < 0;
        int k = win ? 0 : index;

        // 打ち上げの時間差。和了牌が先、手牌はばらけて続く。
        float lead = win ? 0.0f : 0.045f + (k % 7) * 0.014f;
        // 席の左右への散らし（+Z が和了者から見て右）。
        float side = win ? 0.0f : ((k % 2 == 0) ? 1.0f : -1.0f) * (0.018f + (k % 5) * 0.011f);
        // 手前／奥への散らし（+X が和了者の手前）。
        float fwd = win ? 0.030f : ((k % 3) - 1) * 0.014f;
        // 抜け切りの高さ（m）。和了牌が一番高い。
        float peak = win ? 3.20f : 2.30f + (k % 4) * 0.20f;
        // 抜けながらの回転（度）。
        float spin = win ? -110.0f : ((k % 2 == 0) ? 1.0f : -1.0f) * (150.0f + (k % 5) * 45.0f);
        // 溜めの震えの向き。
        float shake = ((k % 2 == 0) ? 1.0f : -1.0f) * 0.0045f;

        float launch = Mathf.Clamp01(0.55f + lead);
        float mid = Mathf.Lerp(launch, 1.0f, 0.55f);
        float vanish = Mathf.Lerp(launch, 1.0f, 0.80f);

        // ---- Position ------------------------------------------------
        var px = Curve(
            0.00f, 0.0f,
            0.03f, shake,
            0.06f, -shake,
            0.10f, 0.0f,
            0.22f, fwd * 0.35f,
            0.55f, fwd * 0.7f,
            1.00f, fwd);

        var py = Curve(
            0.00f, 0.0f,
            0.10f, -0.004f,          // 溜めで少し沈む
            0.22f, 0.055f,           // 浮上
            0.34f, 0.075f,
            0.46f, 0.060f,           // 滞空の揺れ
            launch, 0.090f,
            mid, peak * 0.42f,
            1.00f, peak);

        var pz = Curve(
            0.00f, 0.0f,
            0.03f, -shake * 0.6f,
            0.06f, shake * 0.6f,
            0.10f, 0.0f,
            0.34f, side * 0.4f,
            launch, side * 0.7f,
            1.00f, side);

        AddCurve(clip, path, "m_LocalPosition.x", px);
        AddCurve(clip, path, "m_LocalPosition.y", py);
        AddCurve(clip, path, "m_LocalPosition.z", pz);

        // ---- Rotation（Euler で持つ。Animation ウィンドウで直に読めるように） ----
        float tilt = 26.0f;
        var rx = Curve(
            0.00f, 0.0f,
            0.10f, -5.0f,            // 溜めで前へつんのめる
            0.22f, tilt,             // 立ち上がる
            0.46f, tilt + 3.0f,
            launch, tilt + 6.0f,
            1.00f, tilt + 6.0f + spin * 0.35f);

        var ry = Curve(
            0.00f, 0.0f,
            launch, 0.0f,
            1.00f, spin);

        var rz = Curve(
            0.00f, 0.0f,
            0.22f, win ? 0.0f : side * 90.0f,
            launch, win ? 0.0f : side * 120.0f,
            1.00f, spin * 0.25f);

        AddCurve(clip, path, "localEulerAnglesRaw.x", rx);
        AddCurve(clip, path, "localEulerAnglesRaw.y", ry);
        AddCurve(clip, path, "localEulerAnglesRaw.z", rz);

        // ---- Scale ---------------------------------------------------
        // 牌は不透明（renderQueue 2000）なのでアルファでは消せない。
        // 抜け切りぎわに一気に縮めて消す。0 まで落ちた牌は Mahjong.cs が非表示にする。
        var s = Curve(
            0.00f, 1.0f,
            0.10f, 1.06f,            // 溜めでわずかに膨らむ
            0.22f, 1.0f,
            vanish, 1.0f,
            1.00f, 0.0f);

        AddCurve(clip, path, "m_LocalScale.x", s);
        AddCurve(clip, path, "m_LocalScale.y", s);
        AddCurve(clip, path, "m_LocalScale.z", s);
    }

    /// <summary>正規化時刻（0..1）と値の並びからカーブを作る。接線は自動（なめらか）。</summary>
    static AnimationCurve Curve(params float[] pairs)
    {
        var keys = new List<Keyframe>();
        float last = -1.0f;

        for (int i = 0; i + 1 < pairs.Length; i += 2)
        {
            float t = Mathf.Clamp01(pairs[i]) * ClipSeconds;
            // 同じ時刻のキーが2つ並ぶとカーブが壊れるので、必ず前へ進める。
            if (t <= last) { t = last + 1.0f / 60.0f; }
            last = t;
            keys.Add(new Keyframe(t, pairs[i + 1]));
        }

        var curve = new AnimationCurve(keys.ToArray());
        for (int i = 0; i < curve.length; ++i)
        {
            AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.ClampedAuto);
            AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.ClampedAuto);
        }
        return curve;
    }

    static void AddCurve(AnimationClip clip, string path, string prop, AnimationCurve curve)
    {
        AnimationUtility.SetEditorCurve(clip,
            EditorCurveBinding.FloatCurve(path, typeof(Transform), prop), curve);
    }

    static void EnsureController()
    {
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipPath);
        if (clip == null) { return; }

        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        if (ctrl == null)
        {
            ctrl = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        }

        var layer = ctrl.layers[0];
        var machine = layer.stateMachine;

        AnimatorState state = null;
        foreach (var s in machine.states)
        {
            if (s.state.name == StateName) { state = s.state; break; }
        }
        if (state == null) { state = machine.AddState(StateName); }

        state.motion = clip;
        state.writeDefaultValues = true;
        machine.defaultState = state;

        EditorUtility.SetDirty(ctrl);
        AssetDatabase.SaveAssets();
    }

    // ================================================================ 雑用

    static void SavePrefabStage(PrefabStage stage)
    {
        if (stage == null || stage.prefabContentsRoot == null)
        {
            Debug.LogError("[BuildYakumanFly] Prefab Stage が無効なため保存できません。");
            StageUtility.GoToMainStage();
            return;
        }

        // Prefab Stage の標準保存経路を通す（UdonSharp のシリアライズが File/Save に乗るため）。
        EditorSceneManager.MarkSceneDirty(stage.scene);
        bool saved = EditorApplication.ExecuteMenuItem("File/Save");
        if (!saved || stage.scene.isDirty)
        {
            Debug.LogError("[BuildYakumanFly] Prefab の保存に失敗しました: " + stage.assetPath);
        }

        AssetDatabase.SaveAssets();
        // 閉じ忘れると Unity が固まる（過去に踏んだ）。必ずメインステージへ戻す。
        StageUtility.GoToMainStage();
    }

    static void MarkSceneDirty()
    {
        var scene = SceneManager.GetActiveScene();
        if (scene.IsValid()) { EditorSceneManager.MarkSceneDirty(scene); }
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
