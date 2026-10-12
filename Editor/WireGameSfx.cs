#if UNITY_EDITOR
// ============================================================
//  Assets/Mahjong/Sounds/SFX の素材を、鳴らす場所へ割り当てる。
//    Tools > Kemono Mahjong > Wire Game SFX      … 割り当て（冪等）
//    Tools > Kemono Mahjong > Validate Game SFX  … 割り当て漏れの検査（変更なし）
//
//  【音量はここで決めない】
//    このスクリプトが触るのは AudioClip の参照だけ。音量は KemonoAudioNormalizer が
//    素材の実測RMSから逆算して入れる（Build Volume Panel からも自動で呼ばれる）。
//    ここで数値を書くと、次の Normalize で必ず巻き戻る。
//
//  【新しい素材を足すときの手順】
//    1) ここへ「ファイル名 → 代入先フィールド」を足す
//    2) discardSource から鳴らすものなら KemonoAudioNormalizer.TileDiscardNames にも足す
//       （漏らすと +7.5dB 大きくなる）
//    3) AudioSource を新設したなら
//       KemonoAudioNormalizer.CollectManagedSources() と BuildVolumePanel の収集にも足す
//       （漏らすと音量が設定されず、Validate Volume Panel が leftover として弾く）
//
//  【卓は8面ある。Prefab とシーンの両方へ配る】
//    MahjongSoundsV2 は KemonoMahjong.prefab の中と、シーンの卓8面の両方に居る。
//    Prefab を直さないと「卓を作り直したら旧素材に戻る」ので両方へ入れる。
//
//  【順序：Prefab が先・シーンが後】
//    シーンを配線したあとに Prefab を保存すると、卓のプロキシ値が Prefab 側へ
//    巻き戻り、Play で配線が消える。だから必ず Prefab → シーン の順で書く。
//    Prefab ステージは ClearDirtiness → GoToMainStage で必ず閉じること
//    （閉じ忘れると Unity が固まる）。
// ============================================================
using System.Collections.Generic;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SuzuFactory.AozoraMahjong;

public static class WireGameSfx
{
    const string SfxFolder = "Assets/Mahjong/Sounds/SFX";

    // ファイル名（拡張子なし）。実ファイル名と1文字でも違うと見つからない。
    const string ClipDiscardNormal = "通常牌打牌の音";
    const string ClipDiscardDora = "ドラ牌打牌の音";
    const string ClipNakiArrange = "鳴いた牌を持ってきて整理する音";
    const string ClipReachStick = "立直棒を置く音";
    const string ClipThinkWarn = "思考時間終わる3秒前の音";
    const string ClipThinkWarnLong = "長考時間終わる5秒前の音";
    const string ClipMatchCountdown = "卓が決まる前のカウントダウン用(1秒ごとになる)";
    const string ClipMatchReady = "卓が決まったことのお知らせ";
    const string ClipMenuButton = "MainMenu用ボタン音";
    const string ClipJoinChime = "誰かがJoinしたときに鳴るする音";

    [MenuItem("Tools/Kemono Mahjong/Wire Game SFX")]
    public static void Wire()
    {
        var missing = new List<string>();

        var discardNormal = Load(ClipDiscardNormal, missing);
        var discardDora = Load(ClipDiscardDora, missing);
        var nakiArrange = Load(ClipNakiArrange, missing);
        var reachStick = Load(ClipReachStick, missing);
        var thinkWarn = Load(ClipThinkWarn, missing);
        var thinkWarnLong = Load(ClipThinkWarnLong, missing);
        var matchCountdown = Load(ClipMatchCountdown, missing);
        var matchReady = Load(ClipMatchReady, missing);
        var menuButton = Load(ClipMenuButton, missing);
        var joinChime = Load(ClipJoinChime, missing);

        if (missing.Count > 0)
        {
            Debug.LogError("[WireGameSfx] 素材が見つかりません（" + SfxFolder + "）: "
                           + string.Join(" / ", missing.ToArray()));
            return;
        }

        // --- 1) Prefab（シーンより先）---
        int prefabTables = ApplyToPrefab(discardNormal, discardDora, nakiArrange,
                                         reachStick, thinkWarn, thinkWarnLong);

        // --- 2) シーン ---
        int tables = 0;
        foreach (var s in Object.FindObjectsOfType<MahjongSoundsV2>(true))
        {
            // 打牌音は既存の正規化コピー（_norm）から新素材へ差し替える。
            // Normalize Audio が _norm を探して自動で差し戻すので、ここは生の素材でよい。
            s.discard = discardNormal;
            s.discardDora = discardDora;
            s.nakiArrange = nakiArrange;
            s.reachStick = reachStick;
            s.thinkWarn = thinkWarn;
            s.thinkWarnLong = thinkWarnLong;
            UdonSharpEditorUtility.CopyProxyToUdon(s);
            EditorUtility.SetDirty(s);
            ++tables;
        }

        int mm = 0;
        foreach (var m in Object.FindObjectsOfType<MatchmakingManager>(true))
        {
            m.matchCountdownClip = matchCountdown;
            m.matchReadyClip = matchReady;
            if (m.matchSfxSource == null)
            {
                m.matchSfxSource = BuildStaffOnlyArea.EnsureLocalSfxSource(m.transform, "MatchSfx");
            }
            UdonSharpEditorUtility.CopyProxyToUdon(m);
            EditorUtility.SetDirty(m);
            ++mm;
        }

        int menus = 0;
        foreach (var f in Object.FindObjectsOfType<MenuButtonPressFx>(true))
        {
            f.clickClip = menuButton;
            if (f.clickSource == null)
            {
                f.clickSource = BuildStaffOnlyArea.EnsureLocalSfxSource(f.transform, "MenuClickSfx");
            }
            UdonSharpEditorUtility.CopyProxyToUdon(f);
            EditorUtility.SetDirty(f);
            ++menus;
        }

        int staff = 0;
        foreach (var a in Object.FindObjectsOfType<StaffOnlyArea>(true))
        {
            a.joinClip = joinChime;
            if (a.joinSource == null)
            {
                a.joinSource = BuildStaffOnlyArea.EnsureLocalSfxSource(a.transform, "JoinChimeSource");
            }
            UdonSharpEditorUtility.CopyProxyToUdon(a);
            EditorUtility.SetDirty(a);
            ++staff;
        }

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log("[WireGameSfx] 完了：Prefab " + prefabTables + " / 卓 " + tables
                  + " / Matchmaking " + mm + " / MainMenu " + menus + " / StaffOnlyArea " + staff
                  + "。音量は Normalize Audio (50% Standard) が入れます。");
    }

    const string PrefabPath = "Assets/Mahjong/Prefabas/KemonoMahjong.prefab";

    // Prefab の中の MahjongSoundsV2 へ同じクリップを入れる。
    // Prefab ステージは try/finally で必ず閉じる（開いたままだと Unity が固まる）。
    static int ApplyToPrefab(AudioClip discardNormal, AudioClip discardDora, AudioClip nakiArrange,
                             AudioClip reachStick, AudioClip thinkWarn, AudioClip thinkWarnLong)
    {
        var stage = UnityEditor.SceneManagement.PrefabStageUtility.OpenPrefab(PrefabPath);
        if (stage == null || stage.prefabContentsRoot == null)
        {
            Debug.LogError("[WireGameSfx] Prefab Stage を開けません: " + PrefabPath);
            UnityEditor.SceneManagement.StageUtility.GoToMainStage();
            return 0;
        }

        int n = 0;
        try
        {
            foreach (var s in stage.prefabContentsRoot.GetComponentsInChildren<MahjongSoundsV2>(true))
            {
                s.discard = discardNormal;
                s.discardDora = discardDora;
                s.nakiArrange = nakiArrange;
                s.reachStick = reachStick;
                s.thinkWarn = thinkWarn;
                s.thinkWarnLong = thinkWarnLong;
                UdonSharpEditorUtility.CopyProxyToUdon(s);
                EditorUtility.SetDirty(s);
                ++n;
            }
            if (n > 0)
            {
                // PrefabStage の中身は File/Save で保存する。
                // PrefabUtility.SaveAsPrefabAsset を PrefabStage の root へ使うと、
                // UdonSharp の OdinSerializer が OnBeforeSerialize/OnAfterDeserialize で
                // ArgumentNullException(unityObject) を大量に吐く（保存自体は通るが
                // Console がエラーで埋まる）。KemonoAudioNormalizer と同じ手順に揃える。
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(stage.scene);
                bool saved = EditorApplication.ExecuteMenuItem("File/Save");
                if (!saved || stage.scene.isDirty)
                {
                    Debug.LogError("[WireGameSfx] Prefab の保存に失敗しました: " + PrefabPath);
                    return 0;
                }
            }
        }
        finally
        {
            stage.ClearDirtiness();
            UnityEditor.SceneManagement.StageUtility.GoToMainStage();
        }
        AssetDatabase.SaveAssets();
        return n;
    }

    [MenuItem("Tools/Kemono Mahjong/Validate Game SFX")]
    public static void Validate()
    {
        int errors = 0;

        foreach (var s in Object.FindObjectsOfType<MahjongSoundsV2>(true))
        {
            errors += Need(s.discard, s, "discard（通常打牌音）");
            errors += Need(s.discardDora, s, "discardDora（ドラ打牌音）");
            errors += Need(s.nakiArrange, s, "nakiArrange（鳴き整理音）");
            errors += Need(s.reachStick, s, "reachStick（立直棒）");
            errors += Need(s.thinkWarn, s, "thinkWarn（思考3秒前）");
            errors += Need(s.thinkWarnLong, s, "thinkWarnLong（長考5秒前）");
            if (s.discardSource == null)
            {
                Debug.LogError("[ValidateGameSfx] " + Path(s) + " の discardSource が空です。", s);
                ++errors;
            }
        }

        foreach (var m in Object.FindObjectsOfType<MatchmakingManager>(true))
        {
            errors += Need(m.matchCountdownClip, m, "matchCountdownClip");
            errors += Need(m.matchReadyClip, m, "matchReadyClip");
            errors += NeedSource(m.matchSfxSource, m, "matchSfxSource");
        }

        foreach (var f in Object.FindObjectsOfType<MenuButtonPressFx>(true))
        {
            errors += Need(f.clickClip, f, "clickClip");
            errors += NeedSource(f.clickSource, f, "clickSource");
        }

        foreach (var a in Object.FindObjectsOfType<StaffOnlyArea>(true))
        {
            errors += Need(a.joinClip, a, "joinClip");
            errors += NeedSource(a.joinSource, a, "joinSource");
            if (a.staffDisplayNames == null || a.staffDisplayNames.Length == 0)
            {
                Debug.LogWarning("[ValidateGameSfx] StaffOnlyArea のスタッフ名簿が空です"
                                 + "（入室通知音が誰にも鳴りません）。", a);
            }
        }

        // Prefab も見る。ここが古いと「卓を作り直したら音が旧素材に戻る」。
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab != null)
        {
            foreach (var s in prefab.GetComponentsInChildren<MahjongSoundsV2>(true))
            {
                errors += Need(s.discard, prefab, "Prefab の discard");
                errors += Need(s.discardDora, prefab, "Prefab の discardDora");
                errors += Need(s.nakiArrange, prefab, "Prefab の nakiArrange");
                errors += Need(s.reachStick, prefab, "Prefab の reachStick");
                errors += Need(s.thinkWarn, prefab, "Prefab の thinkWarn");
                errors += Need(s.thinkWarnLong, prefab, "Prefab の thinkWarnLong");
            }
        }

        if (errors == 0) { Debug.Log("[ValidateGameSfx] OK：SFXの割り当てに欠けはありません（Prefab／シーン両方）。"); }
        else { Debug.LogError("[ValidateGameSfx] エラー " + errors + " 件。Wire Game SFX を実行してください。"); }
    }

    // ============================================================
    // ヘルパー
    // ============================================================

    static AudioClip Load(string fileName, List<string> missing)
    {
        // 拡張子は素材ごとに違う（mp3 / wav）ので GUID 検索で拾う。
        var guids = AssetDatabase.FindAssets("t:AudioClip", new[] { SfxFolder });
        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (System.IO.Path.GetFileNameWithoutExtension(path) != fileName) { continue; }
            return AssetDatabase.LoadAssetAtPath<AudioClip>(path);
        }
        missing.Add(fileName);
        return null;
    }

    static int Need(AudioClip clip, Object context, string label)
    {
        if (clip != null) { return 0; }
        Debug.LogError("[ValidateGameSfx] " + Path(context) + " の " + label + " が空です。", context);
        return 1;
    }

    static int NeedSource(AudioSource src, Object context, string label)
    {
        if (src == null)
        {
            Debug.LogError("[ValidateGameSfx] " + Path(context) + " の " + label + " が空です。", context);
            return 1;
        }

        // VRCSpatialAudioSource が無いと Normalize が Gain=24dB を入れられず、
        // 倍率だけ 24dB ぶん小さい前提のまま鳴る＝ほぼ聞こえない。
        if (src.GetComponent<VRC.SDK3.Components.VRCSpatialAudioSource>() == null)
        {
            Debug.LogError("[ValidateGameSfx] " + Path(src) + " に VRCSpatialAudioSource がありません"
                           + "（Gain 24dB が入らず、狙いより24dB小さく鳴ります）。", src);
            return 1;
        }
        return 0;
    }

    static string Path(Object o)
    {
        var comp = o as Component;
        var t = comp != null ? comp.transform : null;
        if (t == null) { return o != null ? o.name : "(null)"; }
        var s = t.name;
        for (var p = t.parent; p != null; p = p.parent) { s = p.name + "/" + s; }
        return s;
    }
}
#endif
