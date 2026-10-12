#if UNITY_EDITOR
// ============================================================
//  50%＝通常音量に揃えるための音量ノーマライザ
// ------------------------------------------------------------
//  ■ 何をするか
//   素材ごとの録音レベル差を実測して、**50%のとき分類内が同じ大きさで鳴る**ように
//   再生倍率を計算し、Prefab とシーンの両方へ書き込む。素材ファイルは加工しない。
//
//  ■ 分類ごとの目標（実効レベル＝素材RMS＋倍率＋AudioSource.volume＋SpatialGain）
//   演出音   +1.2 dB … 打牌音より 4dB 上（OtherSeBoostDb）。「打牌音以外を上げたい」に対応。
//   打牌音   -2.8 dB … 実機試聴で「ちょうどいい」と確認した音量（2026-09-02）。素材の上限。
//                       これ以上は物理的に出せないので、効果音の上げ余地は 0 にしてある。
//   ボイス   -17.4 dB … 現行の中央値（＝聞こえ方を変えない）。内部の目標値
//                       TargetVoiceDbfs は SpatialGain 24dB を引いた -41.39dB。
//   環境音   -50.8 dB … 水（3D・減衰ゼロ地点）と風（2D）を同じ大きさに揃えた値。
//                       2026-09-01 ユーザー確定：「水と同じ大きさ」を優先する
//                       （風は従来比 +16.7dB になるが、これは意図どおり）。
//
//  ■ 基準音量 0.2754 の理由
//   パネルは 50% を 0dB とし、効果音は 100% で +11.2dB する。AudioSource.volume は 1.0 で
//   クランプされるので、基準を 0.2754 に置いて +11.2dB ぶんの余地を確保する。
//   これをやらないと 80/90/100% が完全に無反応になる（2026-09-01 に実測で確認）。
//   上げ幅は分類ごとに違う（効果音+11.2 / ボイス+9.0 / 環境音+3.5dB）＝素材の実力どおり。
//
//  ■ Prefab だけでは効かない
//   シーンの8卓は Udon ヒープ（serializedPublicVariablesBytesString）を
//   インスタンス側で override しているため、**Prefab に書いても実行時には入らない**。
//   ApplyToScene() で proxy と heap を突き合わせ、ズレているものだけ
//   CopyProxyToUdon する。Validate Volume Panel でも同じ検査をしている。
//
//  ■ 測定
//   50ms窓のRMSを取り、最大窓の -18dB 未満（＝無音・余韻）を捨てた平均。
//   短いSEが多く LUFS のゲーティング(0.4s)が効かないため LUFS は使わない。
//   AudioClip.GetData を使うので Load Type が Streaming の素材は測れない
//   （測れなかったものは倍率を据え置き、必ずエラーを出す）。
//
//  実行: Tools > Kemono Mahjong > Normalize Audio (50% Standard)
//        （Build Volume Panel からも自動で呼ばれる）
// ============================================================
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UdonSharp;
using UdonSharpEditor;
using SuzuFactory.AozoraMahjong;

public static class KemonoAudioNormalizer
{
    const string PrefabPath = "Assets/Mahjong/Prefabas/KemonoMahjong.prefab";
    const string VolFolder = "Assets/Mahjong/Sounds/Vol";
    const string ShineFolder = "Assets/Mahjong/Sounds/Shine";
    const string TsumuntemaFolder = "Assets/Mahjong/Sounds/Tsumuntema";
    const string NormalizedFolder = "Assets/Mahjong/Sounds/Normalized";

    // ===== 目標レベル（dB）=====
    // 標準（50%）。上下ほぼ均等に振れるよう中央へ置いた（2026-09-02 ユーザー確定）。
    // 下は -25.2dB、上は -2.8dB まで。上限は打牌音の正規化コピーで決まる。
    public const float TargetSeDbfs = -2.8f;

    // 打牌音以外に足す下駄（dB）。いまは0＝全部同じ目標。演出音だけさらに上げたい
    // ときに使う調整ノブとして残してある。
    // 打牌音は素材の実力（正規化しても rms -26.6dBFS）で上限 -2.6dB しかないため、
    // 目標に届かず自動的に 2.8dB 下がる（Clamp が倍率1.0で止める）。常時鳴る音なので
    // 少し控えめ、という関係になる＝「打牌音以外を上げたい」というご要望どおり。

    public const float OtherSeBoostDb = 4.0f;
    // ボイスは AudioSource.volume に直接入るため SpatialGain を引いた値で持つ。
    // 実効は TargetVoiceDbfs + SourceGainDb = -17.39dB（現行の中央値と同じ）。
    public const float TargetVoiceDbfs = -41.39f;
    // 風（2D・基準音量がそのまま効く）に +3.5dB ぶんの余地を残すため -49.0 から 1.75dB 下げた。
    // 下げないと 90%以上で風だけ頭打ちになり、水と揃わなくなる。
    public const float TargetAmbienceDbfs = -50.75f;

    // ===== 共通の前提 =====
    public const float SourceGainDb = 24.0f;  // 効果音・ボイスの VRCSpatialAudioSource.Gain
    // 効果音の基準音量。1.0＝上げ余地なし＝打牌音を素材の上限まで出し切る設定。
    // 0.707 などに下げると上げ余地は作れるが、そのぶん打牌音が上限に届かなくなる。
    // 効果音は既に天井なので「下げ方向だけ効くスライダー」で正しい。
    public const float BaseVolume = 1.0f;        // 打牌音（上限まで出し切る）
    public const float FxBaseVolume = 0.668f;    // 演出音（+3.5dB の上げ余地）
    public const float VoiceBaseVolume = 0.354f; // ボイス（+9dB の上げ余地）
    public const float MinCorrectionGain = 0.001f;
    public const float MaxCorrectionGain = 1.0f;

    static readonly float[] PackGains = { 1.0f, 0.53f, 0.28f };

    static Dictionary<AudioClip, float> rmsCache_;
    static readonly List<string> unmeasured_ = new List<string>();
    static readonly List<string> unreachable_ = new List<string>();

    // ============================================================
    // 外向けAPI（各ビルダーはここを呼ぶ＝数値を直書きしない）
    // ============================================================

    /// <summary>
    /// 正規化コピーがあればそちらを返す。
    /// 素材が小さすぎて上限を下げているクリップ（打牌音・自ターン音・点数移動音）は、
    /// ピークを -0.3dBFS まで持ち上げたコピーを Sounds/Normalized/<名前>_norm.wav に置いてある。
    /// 元ファイルは消していない。ここで差し替えることでスライダーの上げ幅を稼ぐ。
    /// </summary>
    public static AudioClip Loud(AudioClip clip)
    {
        if (clip == null) { return null; }

        string path = AssetDatabase.GetAssetPath(clip);
        if (string.IsNullOrEmpty(path) || path.Contains("/Normalized/")) { return clip; }

        string candidate = NormalizedFolder + "/" +
            System.IO.Path.GetFileNameWithoutExtension(path) + "_norm.wav";
        var loud = AssetDatabase.LoadAssetAtPath<AudioClip>(candidate);
        return loud != null ? loud : clip;
    }

    /// <summary>
    /// PlayOneShot に渡す倍率。効果音の目標レベルへ揃える。
    /// trimDb ＝ その音だけ意図的に基準から外したいときの補正[dB]（負で小さく）。
    /// 「他より控えめに鳴らしたい」ものは音量を直書きせず必ずここを使う。
    /// Inspector を手で触っても次の Normalize で巻き戻るため。
    /// </summary>
    public static float SeScale(AudioClip clip, float fallback = 0.12f, float trimDb = 0f)
    {
        float rms;
        if (!TryMeasure(clip, out rms)) { return fallback; }

        // 打牌音だけは素材の上限が低いので下駄を履かせない。
        // 基準音量も役割で違う（打牌音は 1.0＝上限まで、演出音は上げ余地を残す）。
        bool isDiscard = IsTileDiscard(clip);
        float target = TargetSeDbfs + (isDiscard ? 0f : OtherSeBoostDb) + trimDb;
        float baseVol = isDiscard ? BaseVolume : FxBaseVolume;
        // 実効 = rms + 20log10(scale) + 20log10(BaseVolume) + SourceGainDb
        float headroomDb = -20f * Mathf.Log10(baseVol);
        return Clamp(clip, Mathf.Pow(10f, (target - rms - SourceGainDb + headroomDb) / 20f));
    }

    /// <summary>
    /// 2D ローカル再生のソースから鳴らす効果音の倍率。
    /// SeScale は VRCSpatialAudioSource の Gain（+24dB）が乗る前提で引き算しているが、
    /// spatialBlend 0 のソース（MatchSfx など）ではその 24dB が乗らないため、
    /// そのまま使うと計算どおりに鳴らずゲームの他の音に埋もれる
    /// （「卓が決まる前のカウントダウンが聞こえない」2026-09-09）。
    /// trimDb に SourceGainDb を渡して下駄を打ち消す＝素材の実力ぶんまで出し切る。
    /// </summary>
    public static float SeScale2D(AudioClip clip, float fallback = 0.5f)
    {
        return SeScale(clip, fallback, SourceGainDb);
    }

    /// <summary>発声1クリップぶんのゲイン。実行時は packVolume と掛け合わされる。</summary>
    public static float VoiceGain(AudioClip clip, int pack, float fallback = 0.2f)
    {
        float rms;
        if (!TryMeasure(clip, out rms)) { return fallback; }

        float packDb = 20f * Mathf.Log10(Mathf.Max(0.0001f, PackGains[pack]));
        return Clamp(clip, Mathf.Pow(10f, (TargetVoiceDbfs - rms - packDb) / 20f));
    }

    /// <summary>環境音の AudioSource.volume。SpatialGain は使わない（風が2Dで効かないため）。</summary>
    public static float AmbienceVolume(AudioClip clip, float fallback = 0.12f)
    {
        float rms;
        if (!TryMeasure(clip, out rms)) { return fallback; }
        // 風・水とも基準音量の枠（BaseVolume）に収まる目標にしてある。
        float v = Mathf.Pow(10f, (TargetAmbienceDbfs - rms) / 20f);
        return Mathf.Clamp(v, MinCorrectionGain, 1.0f);
    }

    // 打牌音か（正規化コピーも含めて名前で判定する）。
    //
    // ここに載っていない素材は「演出音」扱いになり、OtherSeBoostDb(+4dB) の下駄と
    // 基準音量の差（FxBaseVolume 0.668 と BaseVolume 1.0 ＝ +3.5dB）で、実際に鳴る
    // discardSource から合計 +7.5dB 大きく出る。discardSource から鳴らす素材を
    // 増やしたら必ずここへ足すこと。
    static readonly string[] TileDiscardNames =
    {
        "DiscardVFX",                     // 既存の打牌音（正規化コピー _norm 含む）
        "通常牌打牌の音",
        "ドラ牌打牌の音",
        "鳴いた牌を持ってきて整理する音",
    };

    public static bool IsTileDiscard(AudioClip clip)
    {
        if (clip == null) { return false; }
        string path = AssetDatabase.GetAssetPath(clip);
        if (string.IsNullOrEmpty(path)) { return false; }
        for (int i = 0; i < TileDiscardNames.Length; ++i)
        {
            if (path.Contains(TileDiscardNames[i])) { return true; }
        }
        return false;
    }

    static float Clamp(AudioClip clip, float value)
    {
        if (value > MaxCorrectionGain)
        {
            // 打牌音は素材の実力で届かないのが分かっているので警告しない（想定内）。
            if (IsTileDiscard(clip)) { return MaxCorrectionGain; }

            // 素材が小さすぎて目標に届かない。1.0 で頭打ちにし、あとで必ず報告する。
            string path = AssetDatabase.GetAssetPath(clip);
            if (!unreachable_.Contains(path)) { unreachable_.Add(path); }
            return MaxCorrectionGain;
        }
        return Mathf.Max(MinCorrectionGain, value);
    }

    // ============================================================
    // 実行
    // ============================================================

    [MenuItem("Tools/Kemono Mahjong/Normalize Audio (50% Standard)")]
    public static void Run()
    {
        if (!ApplyToPrefab(true)) { return; }
        ApplyToScene(true);
    }

    public static bool ApplyToPrefab(bool verbose)
    {
        ResetReports();

        var clips = new List<AudioClip>();
        var packs = new List<int>();
        AddFolderClips(VolFolder, 0, clips, packs);
        AddFolderClips(ShineFolder, 1, clips, packs);
        AddFolderClips(TsumuntemaFolder, 2, clips, packs);

        if (clips.Count == 0)
        {
            Debug.LogError("[KemonoAudioNormalizer] キャラクターボイスが見つかりません。");
            return false;
        }

        // ボイス：席の発声用ゲインと、SFXソースから鳴らすとき用の倍率を両方作る。
        // （システムアナウンスはボイス素材だが SfxSource から鳴る＝効果音の目標に揃える）
        var voiceGains = new float[clips.Count];
        var announceScales = new float[clips.Count];
        for (int i = 0; i < clips.Count; ++i)
        {
            voiceGains[i] = VoiceGain(clips[i], packs[i]);
            announceScales[i] = SeScale(clips[i]);
        }

        var stage = UnityEditor.SceneManagement.PrefabStageUtility.OpenPrefab(PrefabPath);
        if (stage == null || stage.prefabContentsRoot == null)
        {
            Debug.LogError("[KemonoAudioNormalizer] Prefab Stageを開けません: " + PrefabPath);
            UnityEditor.SceneManagement.StageUtility.GoToMainStage();
            return false;
        }
        var root = stage.prefabContentsRoot;

        try
        {
            var sounds = root.GetComponentInChildren<MahjongSoundsV2>(true);
            if (sounds == null)
            {
                Debug.LogError("[KemonoAudioNormalizer] MahjongSoundsV2がPrefabにありません。");
                return false;
            }

            // 打牌音の専用ソースを用意する（音量パネルで独立して下げるため）。
            // 持ち時間の警告音も専用ソースにする（手番が終わったら Stop するため）。
            foreach (var s in root.GetComponentsInChildren<MahjongSoundsV2>(true))
            {
                EnsureDiscardSource(s);
                EnsureWarnSource(s);
            }

            ApplyValues(
                root.GetComponentsInChildren<MahjongSoundsV2>(true),
                root.GetComponentsInChildren<MahjongAgariEffect>(true),
                root.GetComponentsInChildren<MahjongPresentationScreen>(true),
                root.GetComponentsInChildren<MahjongReachDiscardEffect>(true),
                root.GetComponentsInChildren<MahjongYakumanCutin>(true),
                root.GetComponentsInChildren<MahjongYakumanBanner>(true),
                clips.ToArray(), voiceGains, announceScales);

            // AudioSource 側：基準音量と SpatialGain を揃える。
            // これをやらないと 80/90/100% がクランプで効かない。
            int touched = ApplyToSources(root.GetComponentsInChildren<AudioSource>(true), false);

            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(stage.scene);
            bool saved = EditorApplication.ExecuteMenuItem("File/Save");
            if (!saved || stage.scene.isDirty)
            {
                Debug.LogError("[KemonoAudioNormalizer] Prefabの保存に失敗しました: " + PrefabPath);
                return false;
            }

            if (verbose)
            {
                Debug.Log("[KemonoAudioNormalizer] Prefabへ反映: ボイス " + clips.Count + "本 / AudioSource " + touched + "本");
            }
        }
        finally
        {
            stage.ClearDirtiness();
            UnityEditor.SceneManagement.StageUtility.GoToMainStage();
        }

        AssetDatabase.SaveAssets();
        return ReportProblems();
    }

    // 6種のコンポーネントへ、測定から計算した音量を入れる。
    // Prefab とシーンの両方で同じ計算を通すのが肝。
    // シーン側のインスタンスには「Prefabより古い値の property override」が残っている
    // ことがあり（過去の Wire が instance に直接書いたもの）、Prefab を直しただけでは
    // その override が勝ってしまう。だから同期ではなく毎回代入し直す。
    static void ApplyValues(
        MahjongSoundsV2[] soundsList,
        MahjongAgariEffect[] agariList,
        MahjongPresentationScreen[] presentationList,
        MahjongReachDiscardEffect[] reachList,
        MahjongYakumanCutin[] cutinList,
        MahjongYakumanBanner[] bannerList,
        AudioClip[] clips, float[] voiceGains, float[] announceScales)
    {
        foreach (var s in soundsList)
        {
            // 素材が小さすぎるものは正規化コピーへ差し替える（上げ幅を稼ぐため）。
            s.myTurn = Loud(s.myTurn);
            s.discard = Loud(s.discard);
            s.haipai = Loud(s.haipai);
            s.discardDora = Loud(s.discardDora);
            s.nakiArrange = Loud(s.nakiArrange);
            s.reachStick = Loud(s.reachStick);
            s.thinkWarn = Loud(s.thinkWarn);
            s.thinkWarnLong = Loud(s.thinkWarnLong);
            s.agariYakuRowSfx = Loud(s.agariYakuRowSfx);
            s.agariScoreSfx = Loud(s.agariScoreSfx);
            s.agariRankSfx = Loud(s.agariRankSfx);

            s.voiceNormalizationClips = clips;
            s.voiceNormalizationGains = voiceGains;
            s.voiceAnnounceScales = announceScales;
            s.myTurnSfxVolume = SeScale(s.myTurn);
            s.discardSfxVolume = SeScale(s.discard);
            s.haipaiSfxVolume = SeScale(s.haipai);
            s.agariYakuRowSfxVolume = SeScale(s.agariYakuRowSfx);
            s.agariScoreSfxVolume = SeScale(s.agariScoreSfx);
            s.agariRankSfxVolume = SeScale(s.agariRankSfx);
            s.discardDoraSfxVolume = SeScale(s.discardDora);
            s.nakiArrangeVolume = SeScale(s.nakiArrange);
            s.reachStickVolume = SeScale(s.reachStick);
            s.thinkWarnVolume = SeScale(s.thinkWarn);
            s.thinkWarnLongVolume = SeScale(s.thinkWarnLong);
            SyncProxy(s);
        }
        foreach (var c in agariList)
        {
            c.defaultSound = Loud(c.defaultSound);
            c.handOpenSound = Loud(c.handOpenSound);
            c.seVolume = SeScale(c.defaultSound);
            c.handOpenVolume = SeScale(c.handOpenSound);
            SyncProxy(c);
        }
        foreach (var c in presentationList)
        {
            c.winCutinSound = Loud(c.winCutinSound);
            c.scoreCountSound = Loud(c.scoreCountSound);
            c.winCutinVolume = SeScale(c.winCutinSound);
            c.seVolume = SeScale(c.scoreCountSound);
            SyncProxy(c);
        }
        foreach (var c in reachList)
        {
            c.discardSound = Loud(c.discardSound);
            c.seVolume = SeScale(c.discardSound);
            SyncProxy(c);
        }
        foreach (var c in cutinList)
        {
            c.flyFullSound = Loud(c.flyFullSound);
            c.startSound = Loud(c.startSound);
            c.revealSound = Loud(c.revealSound);
            c.rankSound = Loud(c.rankSound);
            c.flyFullVolume = SeScale(c.flyFullSound);
            c.startVolume = SeScale(c.startSound);
            c.revealVolume = SeScale(c.revealSound);
            c.rankVolume = SeScale(c.rankSound);
            SyncProxy(c);
        }
        foreach (var c in bannerList)
        {
            c.kakuteiSe = Loud(c.kakuteiSe);
            c.chanceSe = Loud(c.chanceSe);
            c.kakuteiVolume = SeScale(c.kakuteiSe);
            c.chanceVolume = SeScale(c.chanceSe);
            SyncProxy(c);
        }
    }

    // Prefab に書いた値をシーンの実体へ通す。ここまでやらないと実行時に効かない。
    public static bool ApplyToScene(bool verbose)
    {
        // シーンのインスタンスにも同じ計算を代入し直す（古い override を潰す）。
        var clips = new List<AudioClip>();
        var packs = new List<int>();
        AddFolderClips(VolFolder, 0, clips, packs);
        AddFolderClips(ShineFolder, 1, clips, packs);
        AddFolderClips(TsumuntemaFolder, 2, clips, packs);
        var voiceGains = new float[clips.Count];
        var announceScales = new float[clips.Count];
        for (int i = 0; i < clips.Count; ++i)
        {
            voiceGains[i] = VoiceGain(clips[i], packs[i]);
            announceScales[i] = SeScale(clips[i]);
        }

        // 専用ソース（打牌音・警告音）はシーンの卓にも用意する。Prefab へ足しただけでは
        // 参照が切れている古いインスタンスに入らない。
        foreach (var s in UnityEngine.Object.FindObjectsOfType<MahjongSoundsV2>(true))
        {
            EnsureDiscardSource(s);
            EnsureWarnSource(s);
        }

        ApplyValues(
            UnityEngine.Object.FindObjectsOfType<MahjongSoundsV2>(true),
            UnityEngine.Object.FindObjectsOfType<MahjongAgariEffect>(true),
            UnityEngine.Object.FindObjectsOfType<MahjongPresentationScreen>(true),
            UnityEngine.Object.FindObjectsOfType<MahjongReachDiscardEffect>(true),
            UnityEngine.Object.FindObjectsOfType<MahjongYakumanCutin>(true),
            UnityEngine.Object.FindObjectsOfType<MahjongYakumanBanner>(true),
            clips.ToArray(), voiceGains, announceScales);

        // シーンにしか居ないコンポーネントのSE音量。KemonoMahjong.prefab の中には
        // 入っていないので ApplyValues とは別に、シーン側だけで代入する。
        ApplySceneOnlySfx();

        int synced = 0;
        synced += SyncSceneInstances<MahjongSoundsV2>();
        synced += SyncSceneInstances<MahjongAgariEffect>();
        synced += SyncSceneInstances<MahjongPresentationScreen>();
        synced += SyncSceneInstances<MahjongReachDiscardEffect>();
        synced += SyncSceneInstances<MahjongYakumanCutin>();
        synced += SyncSceneInstances<MahjongYakumanBanner>();
        synced += SyncSceneInstances<StaffOnlyArea>();
        synced += SyncSceneInstances<MenuButtonPressFx>();
        synced += SyncSceneInstances<MatchmakingManager>();

        // シーンにしか居ない AudioSource（インスタンス側で足された Spatial 等）も揃える。
        int touched = ApplyToSources(UnityEngine.Object.FindObjectsOfType<AudioSource>(true), true);

        // 環境音：シーンの値が正（実行時に上書きしない）。
        int ambience = 0;
        foreach (var src in UnityEngine.Object.FindObjectsOfType<AudioSource>(true))
        {
            if (src.transform.root.name != "EnvironmentAudio" || src.clip == null) { continue; }
            float v = AmbienceVolume(src.clip);
            if (Mathf.Abs(src.volume - v) > 0.0005f)
            {
                Undo.RecordObject(src, "Normalize Ambience");
                src.volume = v;
                EditorUtility.SetDirty(src);
                ++ambience;
            }
        }

        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);

        if (verbose)
        {
            Debug.Log("[KemonoAudioNormalizer] シーンへ反映: Udonヒープ同期 " + synced + " 個 / AudioSource " + touched + "本 / 環境音 " + ambience + "本"
                + "\n※ シーンの保存を忘れずに（Build Volume Panel 経由なら最後に保存される）");
        }
        return ReportProblems();
    }

    // proxy と heap がズレている（＝実行時に効かない）ものだけ同期する。
    // 全部に CopyProxyToUdon すると、今は正しく継承できているインスタンスにまで
    // 無駄な override を作ってしまうため。
    static int SyncSceneInstances<T>() where T : UdonSharpBehaviour
    {
        int synced = 0;
        foreach (var comp in UnityEngine.Object.FindObjectsOfType<T>(true))
        {
            var ub = UdonSharpEditorUtility.GetBackingUdonBehaviour(comp);
            if (ub == null) { continue; }
            if (!HeapDiffers(comp, ub)) { continue; }

            EditorUtility.SetDirty(comp);
            UdonSharpEditorUtility.CopyProxyToUdon(comp);
            ++synced;
        }
        return synced;
    }

    // 入室通知（スタッフ限定）は案内の合図であって主役ではないので、他のSEより
    // これだけ意図的に下げる。素材が飛び抜けて大きいぶんは SeScale の実測補正が
    // 吸収するので、ここは「基準からどれだけ引くか」だけを持つ。
    public const float JoinChimeTrimDb = -9.0f;

    // メニューのクリック音。ボタンを押すたびに鳴る連打系なので、単発の演出音と同じ
    // 基準だと耳につく。実測 0.698 に対して手で 0.4 まで落とされていたのを、
    // ここへ写して恒久化した（-4.8dB ＝ 20*log10(0.4/0.698)）。
    // Inspector で直しても次の Normalize で必ず巻き戻るため、値はここに置くこと。
    public const float MenuClickTrimDb = -4.8f;

    // シーンにしか存在しないコンポーネントのSE音量。
    static void ApplySceneOnlySfx()
    {
        foreach (var c in UnityEngine.Object.FindObjectsOfType<StaffOnlyArea>(true))
        {
            c.joinClip = Loud(c.joinClip);
            c.joinVolume = SeScale(c.joinClip, 0.12f, JoinChimeTrimDb);
            SyncProxy(c);
        }
        foreach (var c in UnityEngine.Object.FindObjectsOfType<MenuButtonPressFx>(true))
        {
            c.clickClip = Loud(c.clickClip);
            c.clickVolume = SeScale(c.clickClip, 0.12f, MenuClickTrimDb);
            SyncProxy(c);
        }
        foreach (var c in UnityEngine.Object.FindObjectsOfType<MatchmakingManager>(true))
        {
            c.matchCountdownClip = Loud(c.matchCountdownClip);
            c.matchReadyClip = Loud(c.matchReadyClip);
            // MatchSfx は spatialBlend 0 の 2D ローカル再生＝SpatialGain が乗らない。
            c.matchCountdownVolume = SeScale2D(c.matchCountdownClip);
            c.matchReadyVolume = SeScale2D(c.matchReadyClip);
            SyncProxy(c);
        }
    }

    // ノーマライザが書き込むフィールドだけを検査対象にする。
    // 全フィールドを総当たりすると、C#側に初期値を持つ配列（voicePackVolumes など）で
    // 誤検知する：UdonSharp は「プログラム既定値と同じ変数を publicVariables に持たない」ため、
    // シンボルが無い＝既定値が入る、が正しい状態なのに「ズレ」と見えてしまう。
    static readonly Dictionary<string, string[]> ManagedFields = new Dictionary<string, string[]>
    {
        { "MahjongSoundsV2", new[] { "voiceNormalizationClips", "voiceNormalizationGains", "voiceAnnounceScales",
                                     "myTurnSfxVolume", "discardSfxVolume", "haipaiSfxVolume",
                                     "agariYakuRowSfxVolume", "agariScoreSfxVolume", "agariRankSfxVolume",
                                     "discardDoraSfxVolume", "nakiArrangeVolume",
                                     "reachStickVolume", "thinkWarnVolume", "thinkWarnLongVolume" } },
        { "MahjongAgariEffect", new[] { "seVolume", "handOpenVolume" } },
        { "MahjongPresentationScreen", new[] { "winCutinVolume", "seVolume" } },
        { "MahjongReachDiscardEffect", new[] { "seVolume" } },
        { "MahjongYakumanCutin", new[] { "flyFullVolume", "startVolume", "revealVolume", "rankVolume" } },
        { "MahjongYakumanBanner", new[] { "kakuteiVolume", "chanceVolume" } },
        { "StaffOnlyArea", new[] { "joinVolume" } },
        { "MenuButtonPressFx", new[] { "clickVolume" } },
        { "MatchmakingManager", new[] { "matchCountdownVolume", "matchReadyVolume" } },
    };

    /// <summary>
    /// 実行時に効く値（heap。シンボルが無ければプログラム既定値）が proxy と食い違うか。
    /// 対象はノーマライザが書き込むフィールドのみ。
    /// </summary>
    public static bool HeapDiffers(UdonSharpBehaviour comp, VRC.Udon.UdonBehaviour ub)
    {
        var type = comp.GetType();
        string[] names;
        if (!ManagedFields.TryGetValue(type.Name, out names)) { return false; }

        foreach (var name in names)
        {
            var f = type.GetField(name);
            if (f == null) { continue; }

            bool isFloat = f.FieldType == typeof(float);
            object proxy = f.GetValue(comp);
            object heap;
            bool has = ub.publicVariables.TryGetVariableValue(f.Name, out heap);

            if (!has)
            {
                // シンボルが無い＝プログラム既定値が入る。配列なら null になるので、
                // proxy に中身があるならズレている。
                var newArr = proxy as Array;
                if (newArr != null && newArr.Length > 0) { return true; }
                continue;
            }

            if (isFloat)
            {
                float p = (float)proxy;
                float h = heap == null ? float.MinValue : (float)heap;
                if (Mathf.Abs(p - h) > 0.0005f) { return true; }
            }
            else
            {
                var pa = proxy as Array;
                var ha = heap as Array;
                int pl = pa == null ? -1 : pa.Length;
                int hl = ha == null ? -1 : ha.Length;
                if (pl != hl) { return true; }
            }
        }
        return false;
    }

    // 効果音・ボイスの AudioSource へ基準音量と Gain を入れる。環境音・動画は触らない。
    // 対象は「うちのコンポーネントが参照しているソース」＝卓の外にある検証用リグ
    // （AgariEffectPreview）も拾う。ここを取りこぼすと、倍率だけ新基準・Gainだけ旧値の
    // ちぐはぐな音源が残り、そこだけ11dB小さくなる（2026-09-01 に踏んだ）。
    static int ApplyToSources(AudioSource[] sources, bool sceneSide)
    {
        // ボイス系のソースは音量パネルの上げ幅(+9dB)ぶんの余地を残す。
        // 効果音は BaseVolume=1.0（＝打牌音を上限まで出し切る）ので上げ余地なし。
        var voiceSet = CollectVoiceSources();
        var discardSet = CollectDiscardSources();
        HashSet<AudioSource> managed = null;
        if (sceneSide) { managed = CollectManagedSources(); }

        int touched = 0;
        foreach (var src in sources)
        {
            if (src == null) { continue; }
            if (src.transform.root.name == "EnvironmentAudio") { continue; }
            if (sceneSide && !managed.Contains(src)) { continue; }

            float wantVolume = voiceSet.Contains(src) ? VoiceBaseVolume
                : (discardSet.Contains(src) ? BaseVolume : FxBaseVolume);
            bool changed = false;
            if (Mathf.Abs(src.volume - wantVolume) > 0.0005f)
            {
                Undo.RecordObject(src, "Normalize Audio");
                src.volume = wantVolume;
                EditorUtility.SetDirty(src);
                changed = true;
            }

            var sp = src.GetComponent<VRC.SDK3.Components.VRCSpatialAudioSource>();
            if (sp != null && Mathf.Abs(sp.Gain - SourceGainDb) > 0.01f)
            {
                Undo.RecordObject(sp, "Normalize Audio");
                sp.Gain = SourceGainDb;
                EditorUtility.SetDirty(sp);
                changed = true;
            }
            if (changed) { ++touched; }
        }
        return touched;
    }

    // SfxSource の隣に打牌音専用の AudioSource を用意し、設定をコピーする。
    // 打牌音と演出音は同じ SfxSource から鳴っていたため、片方だけ下げられなかった。
    static void EnsureDiscardSource(MahjongSoundsV2 sounds)
    {
        if (sounds == null || sounds.sfxSource == null) { return; }

        var parent = sounds.sfxSource.transform.parent;
        var t = parent != null ? parent.Find("DiscardSource") : null;
        if (t == null)
        {
            var go = new GameObject("DiscardSource");
            go.transform.SetParent(parent != null ? parent : sounds.transform, false);
            go.transform.localPosition = sounds.sfxSource.transform.localPosition;
            t = go.transform;
        }

        var src = t.GetComponent<AudioSource>();
        if (src == null) { src = t.gameObject.AddComponent<AudioSource>(); }

        // 鳴り方は SfxSource と揃える（音量だけ別管理にしたいだけなので）。
        var from = sounds.sfxSource;
        src.playOnAwake = false;
        src.loop = false;
        src.spatialBlend = from.spatialBlend;
        src.dopplerLevel = from.dopplerLevel;
        src.rolloffMode = from.rolloffMode;
        src.minDistance = from.minDistance;
        src.maxDistance = from.maxDistance;
        src.outputAudioMixerGroup = from.outputAudioMixerGroup;

        var fromSpatial = from.GetComponent<VRC.SDK3.Components.VRCSpatialAudioSource>();
        var spatial = t.GetComponent<VRC.SDK3.Components.VRCSpatialAudioSource>();
        if (fromSpatial != null && spatial == null)
        {
            spatial = t.gameObject.AddComponent<VRC.SDK3.Components.VRCSpatialAudioSource>();
        }
        if (spatial != null && fromSpatial != null)
        {
            spatial.Gain = fromSpatial.Gain;
            spatial.Far = fromSpatial.Far;
            spatial.Near = fromSpatial.Near;
            spatial.VolumetricRadius = fromSpatial.VolumetricRadius;
            spatial.EnableSpatialization = fromSpatial.EnableSpatialization;
        }

        sounds.discardSource = src;
        EditorUtility.SetDirty(sounds);
    }

    // SfxSource の隣に持ち時間警告音の専用 AudioSource を用意する。
    // 3秒/5秒のカウントダウン素材なので、手番が終わったら途中で Stop する必要がある。
    // 共用の SfxSource を止めると役読み・アナウンスまで切れるため分けている。
    static void EnsureWarnSource(MahjongSoundsV2 sounds)
    {
        if (sounds == null || sounds.sfxSource == null) { return; }

        var parent = sounds.sfxSource.transform.parent;
        var t = parent != null ? parent.Find("WarnSource") : null;
        if (t == null)
        {
            var go = new GameObject("WarnSource");
            go.transform.SetParent(parent != null ? parent : sounds.transform, false);
            go.transform.localPosition = sounds.sfxSource.transform.localPosition;
            t = go.transform;
        }

        var src = t.GetComponent<AudioSource>();
        if (src == null) { src = t.gameObject.AddComponent<AudioSource>(); }

        var from = sounds.sfxSource;
        src.playOnAwake = false;
        src.loop = false;
        src.spatialBlend = from.spatialBlend;
        src.dopplerLevel = from.dopplerLevel;
        src.rolloffMode = from.rolloffMode;
        src.minDistance = from.minDistance;
        src.maxDistance = from.maxDistance;
        src.outputAudioMixerGroup = from.outputAudioMixerGroup;

        var fromSpatial = from.GetComponent<VRC.SDK3.Components.VRCSpatialAudioSource>();
        var spatial = t.GetComponent<VRC.SDK3.Components.VRCSpatialAudioSource>();
        if (fromSpatial != null && spatial == null)
        {
            spatial = t.gameObject.AddComponent<VRC.SDK3.Components.VRCSpatialAudioSource>();
        }
        if (spatial != null && fromSpatial != null)
        {
            spatial.Gain = fromSpatial.Gain;
            spatial.Far = fromSpatial.Far;
            spatial.Near = fromSpatial.Near;
            spatial.VolumetricRadius = fromSpatial.VolumetricRadius;
            spatial.EnableSpatialization = fromSpatial.EnableSpatialization;
        }

        sounds.warnSource = src;
        EditorUtility.SetDirty(sounds);
    }

    // 打牌音の専用ソース。
    static HashSet<AudioSource> CollectDiscardSources()
    {
        var set = new HashSet<AudioSource>();
        foreach (var c in UnityEngine.Object.FindObjectsOfType<MahjongSoundsV2>(true))
        {
            if (c.discardSource != null) { set.Add(c.discardSource); }
        }
        return set;
    }

    // ボイスとして鳴るソース（席の発声・マッチング成立ボイス・試聴）。
    static HashSet<AudioSource> CollectVoiceSources()
    {
        var set = new HashSet<AudioSource>();
        foreach (var c in UnityEngine.Object.FindObjectsOfType<MahjongSoundsV2>(true))
        {
            if (c.voiceSources == null) { continue; }
            foreach (var v in c.voiceSources) { if (v != null) { set.Add(v); } }
        }
        foreach (var c in UnityEngine.Object.FindObjectsOfType<MatchmakingManager>(true))
        {
            if (c.matchVoiceSource != null) { set.Add(c.matchVoiceSource); }
        }
        foreach (var c in UnityEngine.Object.FindObjectsOfType<MahjongVoiceSelector>(true))
        {
            if (c.previewSource != null) { set.Add(c.previewSource); }
        }
        return set;
    }

    // 音量パネルが管理するのと同じ集合（＝うちのコンポーネントが鳴らすソース）を集める。
    static HashSet<AudioSource> CollectManagedSources()
    {
        var set = new HashSet<AudioSource>();
        System.Action<AudioSource> add = (s) => { if (s != null) { set.Add(s); } };

        foreach (var c in UnityEngine.Object.FindObjectsOfType<MahjongSoundsV2>(true))
        {
            add(c.discardSource);
            add(c.warnSource);
            add(c.sfxSource);
            if (c.voiceSources != null) { foreach (var v in c.voiceSources) { add(v); } }
        }
        foreach (var c in UnityEngine.Object.FindObjectsOfType<MahjongAgariEffect>(true)) { add(c.seSource); }
        foreach (var c in UnityEngine.Object.FindObjectsOfType<MahjongPresentationScreen>(true)) { add(c.seSource); }
        foreach (var c in UnityEngine.Object.FindObjectsOfType<MahjongReachDiscardEffect>(true)) { add(c.seSource); }
        foreach (var c in UnityEngine.Object.FindObjectsOfType<MahjongYakumanCutin>(true)) { add(c.seSource); }
        foreach (var c in UnityEngine.Object.FindObjectsOfType<MahjongYakumanImpact>(true)) { add(c.seSource); }
        foreach (var c in UnityEngine.Object.FindObjectsOfType<MahjongYakumanBanner>(true)) { add(c.seSource); }
        foreach (var c in UnityEngine.Object.FindObjectsOfType<MatchmakingManager>(true))
        {
            add(c.matchVoiceSource);
            add(c.matchSfxSource);
        }
        foreach (var c in UnityEngine.Object.FindObjectsOfType<MahjongVoiceSelector>(true)) { add(c.previewSource); }
        foreach (var c in UnityEngine.Object.FindObjectsOfType<StaffOnlyArea>(true)) { add(c.joinSource); }
        foreach (var c in UnityEngine.Object.FindObjectsOfType<MenuButtonPressFx>(true)) { add(c.clickSource); }
        return set;
    }

    // ============================================================
    // 測定
    // ============================================================

    public static bool TryMeasure(AudioClip clip, out float db)
    {
        db = -80f;
        if (clip == null) { return false; }

        if (rmsCache_ == null) { rmsCache_ = new Dictionary<AudioClip, float>(); }
        if (rmsCache_.TryGetValue(clip, out db)) { return db < 900f; }

        float measured;
        bool ok = MeasureActiveRmsDb(clip, out measured);
        rmsCache_[clip] = ok ? measured : 999f;
        if (!ok)
        {
            string path = AssetDatabase.GetAssetPath(clip);
            if (!unmeasured_.Contains(path)) { unmeasured_.Add(path); }
            db = -80f;
            return false;
        }
        db = measured;
        return true;
    }

    static bool MeasureActiveRmsDb(AudioClip clip, out float db)
    {
        db = -80.0f;
        if (clip.samples <= 0 || clip.channels <= 0) { return false; }

        int sampleCount = clip.samples * clip.channels;
        var data = new float[sampleCount];
        if (!clip.GetData(data, 0)) { return false; }

        int windowSamples = Mathf.Max(clip.channels, (clip.frequency / 20) * clip.channels);
        var powers = new List<double>();
        double maxPower = 0.0;

        for (int start = 0; start < sampleCount; start += windowSamples)
        {
            int end = Math.Min(sampleCount, start + windowSamples);
            double sum = 0.0;
            for (int i = start; i < end; ++i)
            {
                double value = data[i];
                sum += value * value;
            }

            double power = sum / Math.Max(1, end - start);
            powers.Add(power);
            if (power > maxPower) { maxPower = power; }
        }

        if (maxPower <= 0.0000000001) { return false; }

        // 最大アクティブ窓の-18dB未満と、絶対-60dB未満の無音区間を除外。
        double gate = Math.Max(0.000001, maxPower * 0.0158489319);
        double activeSum = 0.0;
        int activeCount = 0;
        foreach (double power in powers)
        {
            if (power < gate) { continue; }
            activeSum += power;
            ++activeCount;
        }
        if (activeCount == 0) { return false; }

        db = 10.0f * Mathf.Log10((float)(activeSum / activeCount));
        return true;
    }

    static void AddFolderClips(string folder, int pack, List<AudioClip> clips, List<int> packs)
    {
        var found = new List<AudioClip>();
        foreach (string guid in AssetDatabase.FindAssets("t:AudioClip", new[] { folder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (clip != null) { found.Add(clip); }
        }
        found.Sort((a, b) => string.CompareOrdinal(AssetDatabase.GetAssetPath(a), AssetDatabase.GetAssetPath(b)));
        foreach (var clip in found)
        {
            clips.Add(clip);
            packs.Add(pack);
        }
    }

    static void ResetReports()
    {
        unmeasured_.Clear();
        unreachable_.Clear();
    }

    // 測れなかった／目標に届かなかったものは必ず表に出す（黙って等倍にしない）。
    static bool ReportProblems()
    {
        bool ok = true;
        if (unmeasured_.Count > 0)
        {
            ok = false;
            Debug.LogError("[KemonoAudioNormalizer] 測定できないクリップが " + unmeasured_.Count + " 件あります"
                + "（Load Type が Streaming だと GetData で読めません）。倍率が暫定値のままです:\n - "
                + string.Join("\n - ", unmeasured_.ToArray()));
        }
        if (unreachable_.Count > 0)
        {
            Debug.LogWarning("[KemonoAudioNormalizer] 素材が小さすぎて目標レベルに届かないクリップが "
                + unreachable_.Count + " 件あります（倍率1.0で頭打ち）:\n - "
                + string.Join("\n - ", unreachable_.ToArray()));
        }
        return ok;
    }

    static void SyncProxy(UdonSharpBehaviour behaviour)
    {
        EditorUtility.SetDirty(behaviour);
        UdonSharpEditorUtility.CopyProxyToUdon(behaviour);
    }
}
#endif
