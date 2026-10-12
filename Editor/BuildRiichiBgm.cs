using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UdonSharp;
using UdonSharpEditor;
using SuzuFactory.AozoraMahjong;

public static class BuildRiichiBgm
{
    const string PrefabPath = "Assets/Mahjong/Prefabas/KemonoMahjong.prefab";
    const string ProgramPath = "Assets/Mahjong/scene_UdonProgramSources/MahjongRiichiBgm Udon C# Program Asset.asset";
    // メインメニュー脇の試聴用 AudioSource（装飾品画面で曲を選ぶときに鳴らす1個）。
    const string PreviewSourceName = "RiichiBgmPreviewSource";

    // ---- 立直BGMの正（曲を足すときはここだけ触る） ----------------------------
    //   index = トラック番号。0 は「なし」＝クリップ null / ゲイン 0。
    //   Gains は 50%（0dB）のときの AudioSource.volume。実測LUFSからの逆算値。
    //   MahjongRiichiBgm.Update がこの値で毎フレーム volume を上書きする＝音量の権威。
    //   曲を足したら MainMenuController.COSMETIC_BGM_COUNT と
    //   MainMenuController.RiichiBgmName() も揃えること（Validate が食い違いを検出する）。
    static readonly string[] TrackPaths =
    {
        null,
        "Assets/Mahjong/Sounds/RiichiBGM/Whistle-in-the-Dark.wav",
        "Assets/Mahjong/Sounds/RiichiBGM/ROGLIK01.wav",
    };
    static readonly float[] TrackGains = { 0f, 0.1862087f, 0.2511886f };

    [MenuItem("Tools/Kemono Mahjong/Build Riichi BGM")]
    public static void Run()
    {
        if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(ProgramPath) == null)
        {
            var program = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
            program.sourceCsScript = AssetDatabase.LoadAssetAtPath<MonoScript>("Assets/Mahjong/Sources/MahjongRiichiBgm.cs");
            AssetDatabase.CreateAsset(program, ProgramPath);
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[BuildRiichiBgm] Program created; run Build again after compilation.");
            return;
        }
        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            foreach (var sounds in root.GetComponentsInChildren<MahjongSoundsV2>(true)) { Wire(sounds, null); }
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        foreach (var culler in Object.FindObjectsOfType<TableProximityCuller>(true))
        {
            if (culler.tableRoot == null) { continue; }
            foreach (var sounds in culler.tableRoot.GetComponentsInChildren<MahjongSoundsV2>(true)) { Wire(sounds, culler); }
        }
        BuildMainMenu.BuildCosmeticOnly();
        WireMenuPreview();
        BuildVolumePanel.Run();
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log("[BuildRiichiBgm] Built prefab, table audio, menu preview, cosmetics and nine volume categories.");
    }

    // 曲表（TrackPaths）を AudioClip の配列にする。[0] は「なし」＝null のまま。
    static AudioClip[] LoadClips()
    {
        var clips = new AudioClip[TrackPaths.Length];
        for (int i = 0; i < TrackPaths.Length; ++i)
        {
            if (TrackPaths[i] == null) { continue; }
            clips[i] = AssetDatabase.LoadAssetAtPath<AudioClip>(TrackPaths[i]);
            if (clips[i] == null) { throw new System.InvalidOperationException("[BuildRiichiBgm] クリップがありません: " + TrackPaths[i]); }
        }
        return clips;
    }

    // 装飾品画面の試聴（ローカル）を配線する。
    // 卓の MahjongRiichiBgm は近接カリングで寝ているので試聴には使えない＝メニュー脇に1個持つ
    //（和了エフェクトの試写を fxCatalog の1個でやっているのと同じ作法）。
    static void WireMenuPreview()
    {
        var menu = Object.FindObjectOfType<MainMenuController>(true);
        if (menu == null)
        {
            Debug.LogWarning("[BuildRiichiBgm] MainMenuController がシーンにありません。立直BGMの試聴は未配線です。");
            return;
        }

        var t = menu.transform.Find(PreviewSourceName);
        if (t == null)
        {
            var go = new GameObject(PreviewSourceName);
            go.transform.SetParent(menu.transform, false);
            t = go.transform;
        }
        t.localPosition = Vector3.zero;
        t.localRotation = Quaternion.identity;
        t.localScale = Vector3.one;

        var src = t.GetComponent<AudioSource>();
        if (src == null) { src = Undo.AddComponent<AudioSource>(t.gameObject); }
        src.playOnAwake = false;
        // loop は切る。試聴は1回流して終わり＝メニューを開けっ放しでも鳴り続けない。
        src.loop = false;
        src.spatialBlend = 0f;  // 本人にしか聞こえないUIフィードバック
        src.spatialize = false;
        // 実行時は MainMenuController が「トラック別ゲイン × bgmAmp」で上書きする。ここは見た目の初期値。
        src.volume = TrackGains.Length > 1 ? TrackGains[1] : 0.2f;

        menu.riichiBgmPreviewSource = src;
        menu.riichiBgmPreviewClips = LoadClips();
        // 試聴と本番で音量が揃うよう、卓と同じゲイン表を渡す。
        menu.riichiBgmPreviewGains = (float[])TrackGains.Clone();
        EditorUtility.SetDirty(menu);
        UdonSharpEditorUtility.CopyProxyToUdon(menu);
    }

    static AudioSource Source(MahjongSoundsV2 sounds, string name)
    {
        var child = sounds.transform.Find(name);
        if (child == null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(sounds.transform, false);
            child = go.transform;
            var created = go.AddComponent<AudioSource>();
            if (sounds.sfxSource != null)
            {
                EditorUtility.CopySerialized(sounds.sfxSource, created);
                var spatial = sounds.sfxSource.GetComponent<VRC.SDK3.Components.VRCSpatialAudioSource>();
                if (spatial != null) { EditorUtility.CopySerialized(spatial, go.AddComponent<VRC.SDK3.Components.VRCSpatialAudioSource>()); }
            }
        }
        var source = child.GetComponent<AudioSource>();
        source.playOnAwake = false;
        return source;
    }

    static void Wire(MahjongSoundsV2 sounds, TableProximityCuller culler)
    {
        sounds.tableOperationSource = Source(sounds, "TableOperationSource");
        sounds.systemSource = Source(sounds, "SystemNoticeSource");
        sounds.announceSource = Source(sounds, "AnnounceSource");
        var t = sounds.transform.Find("RiichiBgm");
        if (t == null)
        {
            var go = new GameObject("RiichiBgm");
            go.transform.SetParent(sounds.transform, false);
            t = go.transform;
        }
        var audio = t.GetComponent<AudioSource>();
        if (audio == null) { audio = t.gameObject.AddComponent<AudioSource>(); }
        audio.playOnAwake = false;
        audio.loop = true;
        audio.spatialBlend = 0f;
        audio.spatialize = false;
        // 実行時は MahjongRiichiBgm.Update が毎フレーム上書きする。ここは Play 前の見た目の初期値。
        audio.volume = TrackGains.Length > 1 ? TrackGains[1] : 0.2f;
        var bgm = t.GetComponent<MahjongRiichiBgm>();
        if (bgm == null) { bgm = (MahjongRiichiBgm)UdonSharpUndo.AddComponent(t.gameObject, typeof(MahjongRiichiBgm)); }
        bgm.bgmSource = audio;
        bgm.sounds = sounds;
        bgm.culler = culler;
        bgm.riichiBgm = LoadClips();
        bgm.riichiBgmGains = (float[])TrackGains.Clone();
        sounds.worldBgm = bgm;
        EditorUtility.SetDirty(bgm);
        EditorUtility.SetDirty(sounds);
        UdonSharpEditorUtility.CopyProxyToUdon(bgm);
        UdonSharpEditorUtility.CopyProxyToUdon(sounds);
    }

    [MenuItem("Tools/Kemono Mahjong/Validate Riichi BGM")]
    public static void Validate()
    {
        var problems = new List<string>();
        var cullers = Object.FindObjectsOfType<TableProximityCuller>(true);
        foreach (var c in cullers)
        {
            var s = c.tableRoot != null ? c.tableRoot.GetComponentInChildren<MahjongSoundsV2>(true) : null;
            if (s == null || s.worldBgm == null) { problems.Add(c.name + ": missing BGM"); continue; }
            var b = s.worldBgm;
            if (b.culler != c || b.sounds != s || b.volumePanel == null) { problems.Add(c.name + ": wiring"); }
            if (b.bgmSource == null || b.bgmSource.playOnAwake || !b.bgmSource.loop || b.bgmSource.spatialBlend != 0) { problems.Add(c.name + ": audio"); }
            if (b.riichiBgm == null || b.riichiBgm.Length != TrackPaths.Length || b.riichiBgm[0] != null) { problems.Add(c.name + ": clips"); }
            else { for (int i = 1; i < b.riichiBgm.Length; ++i) { if (b.riichiBgm[i] == null) { problems.Add(c.name + ": clip" + i); } } }
            // ゲイン表は曲と同じ並びで、要素数も一致していること。
            // ずれると「選べるのに音量だけ違う／鳴らない」になる。
            if (b.riichiBgmGains == null || b.riichiBgmGains.Length != TrackPaths.Length) { problems.Add(c.name + ": gains"); }
            else if (b.riichiBgmGains[0] != 0f) { problems.Add(c.name + ": gain[0] must be 0"); }
            if (s.tableOperationSource == null || s.systemSource == null || s.announceSource == null) { problems.Add(c.name + ": categories"); }
        }
        // 立直BGMが2卓ぶん同時に鳴らないことは「卓どうしが判定半径の2倍より離れている」ことで
        // 幾何学的に担保している（TableProximityCuller.IsAudioInRange のコメント参照）。
        // 卓を動かしたり増やしたりしたときに、その前提が崩れていないかを検査する。
        for (int i = 0; i < cullers.Length; ++i)
        {
            for (int j = i + 1; j < cullers.Length; ++j)
            {
                float need = cullers[i].showDistance + cullers[j].showDistance;
                float d = Vector3.Distance(cullers[i].transform.position, cullers[j].transform.position);
                if (d < need)
                {
                    problems.Add(cullers[i].name + " と " + cullers[j].name + " が近すぎます（"
                        + d.ToString("F2") + "m < " + need.ToString("F2") + "m）＝立直BGMが二重に鳴ります");
                }
            }
        }

        var menu = Object.FindObjectOfType<MainMenuController>(true);
        if (menu == null || menu.cosmeticCategoryImages == null || menu.cosmeticCategoryImages.Length != 4) { problems.Add("cosmetic categories"); }
        // 立直BGMの選択カード枚数（「なし」を含む）は、曲の本数と一致していなければならない。
        if (MainMenuController.COSMETIC_BGM_COUNT != TrackPaths.Length)
        {
            problems.Add("COSMETIC_BGM_COUNT=" + MainMenuController.COSMETIC_BGM_COUNT + " but tracks=" + TrackPaths.Length);
        }
        // 試聴（メニュー脇の1個）。曲表とゲイン表は卓側と同じ並び・同じ本数であること。
        if (menu != null)
        {
            if (menu.riichiBgmPreviewSource == null) { problems.Add("menu preview source"); }
            else if (menu.riichiBgmPreviewSource.spatialBlend != 0f || menu.riichiBgmPreviewSource.playOnAwake) { problems.Add("menu preview audio"); }
            if (menu.riichiBgmPreviewClips == null || menu.riichiBgmPreviewClips.Length != TrackPaths.Length || menu.riichiBgmPreviewClips[0] != null) { problems.Add("menu preview clips"); }
            if (menu.riichiBgmPreviewGains == null || menu.riichiBgmPreviewGains.Length != TrackPaths.Length) { problems.Add("menu preview gains"); }
            if (menu.volumePanel == null) { problems.Add("menu preview volume panel"); }
        }
        var panel = Object.FindObjectOfType<VolumeSettingsPanel>(true);
        // BGM の収集対象は「卓ごとの立直BGM＋メニューの試聴1個」。
        if (panel == null || panel.rowObjects.Length != 9 || panel.bgmSources.Length != cullers.Length + 1) { problems.Add("volume categories"); }
        if (problems.Count > 0) { Debug.LogError("[ValidateRiichiBgm] " + string.Join("; ", problems)); }
        else { Debug.Log("[ValidateRiichiBgm] passed=" + cullers.Length + " errors=0; Editor only"); }
    }
}

