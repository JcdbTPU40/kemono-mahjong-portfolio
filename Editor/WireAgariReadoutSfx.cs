#if UNITY_EDITOR
// あがり画面の読み上げSFX（役行 / 点数表示 / 称号バナー）を全卓の MahjongSoundsV2 へ一括割当する。
//   Tools > Kemono Mahjong > Wire Agari Readout SFX     … 全卓へクリップを割当（冪等）
//   Tools > Kemono Mahjong > Validate Agari Readout SFX … 卓ごとの割当状況をConsoleに出す
//
// クリップの決め方（上から順に探して最初に見つかったものを使う）:
//   1) Assets/Mahjong/Sounds/AgariReadoutSFX/ に置かれた音声ファイル。
//      ファイル名の先頭が yaku / score / rank のものをそれぞれ拾う（拡張子・大小文字自由）。
//      → 差し替えたいときはここへ3ファイル置いて Wire を再実行するだけでよい。
//   2) 既にどれかの卓に割り当て済みのクリップ（＝手でInspectorに入れた1卓を他卓へ複製）。
//   3) 下の Default* パス（暫定素材）。
//
// 音そのものは Mahjong.cs ではなく MahjongAgariScreen が各クライアントでローカル再生する。
// 同期変数もネットワークイベントも増えない。クリップ未設定なら無音＝従来動作のまま。
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SuzuFactory.AozoraMahjong;

public static class WireAgariReadoutSfx
{
    const string OverrideDir = "Assets/Mahjong/Sounds/AgariReadoutSFX";

    // 音量はクリップごとの実測ラウドネス（ffmpeg ebur128）から逆算し、
    // 3本とも約 -43 LUFS 相当に揃えた値。読み上げは短い間隔で連続して鳴るので、
    // 単発の演出SE（和了エフェクト等）より一段控えめにする。
    //   役行 3mp3      -20.4 LUFS → 0.074
    //   点数 Hai Don   -21.3 LUFS → 0.082
    //   称号 rank.wav  -10.0 LUFS → 0.022   ※素の音圧が高いので大きく絞る
    const float YakuRowVolume = 0.123f;
    const float ScoreVolume = 0.028f;
    const float RankVolume = 0.022f;


    // 暫定素材（プロジェクトに既にあるものを流用）。
    //   役行  … 3mp3（差し替え済み。旧・風切り音はロン/ツモのカットインで使用中）
    //   点数  … 手牌公開と同じドン（鳴るタイミングが数秒離れるので被らない）
    //   称号  … 牌エフェクトと同じ爆発（同上。差し替えフォルダに rank.wav があるので通常は使われない）
    const string DefaultYakuRow = "Assets/Mahjong/Sounds/3mp3.wav";
    const string DefaultScore = "Assets/Mahjong/Sounds/AgariSFX/Agari Hai Don.mp3";
    const string DefaultRank = "Assets/Mahjong/Sounds/AgariSFX/freesound_community-medium-explosion-40472.mp3";

    [MenuItem("Tools/Kemono Mahjong/Wire Agari Readout SFX")]
    public static void Wire()
    {
        var targets = Object.FindObjectsOfType<MahjongSoundsV2>(true);
        if (targets.Length == 0)
        {
            Debug.LogError("[WireAgariReadoutSfx] MahjongSoundsV2 が見つかりません。卓のあるシーンを開いてから実行してください。");
            return;
        }

        var yaku = ResolveClip("yaku", DefaultYakuRow, targets, 0);
        var score = ResolveClip("score", DefaultScore, targets, 1);
        var rank = ResolveClip("rank", DefaultRank, targets, 2);

        int assigned = 0;
        foreach (var s in targets)
        {
            if (s == null) { continue; }

            s.agariYakuRowSfx = yaku;
            s.agariScoreSfx = score;
            s.agariRankSfx = rank;

            // 3本は素の音圧がバラバラなので、一律の値ではなくクリップ別の値を毎回代入する。
            s.agariYakuRowSfxVolume = KemonoAudioNormalizer.SeScale(yaku, YakuRowVolume);
            s.agariScoreSfxVolume = KemonoAudioNormalizer.SeScale(score, ScoreVolume);
            s.agariRankSfxVolume = KemonoAudioNormalizer.SeScale(rank, RankVolume);

            EditorUtility.SetDirty(s);
            UdonSharpEditorUtility.CopyProxyToUdon(s);
            ++assigned;
        }

        EditorSceneManager.MarkSceneDirty(targets[0].gameObject.scene);
        EditorSceneManager.SaveOpenScenes();

        Debug.Log(string.Format(
            "[WireAgariReadoutSfx] {0}卓へ割当。役行={1} / 点数={2} / 称号={3}",
            assigned, Name(yaku), Name(score), Name(rank)));
    }

    [MenuItem("Tools/Kemono Mahjong/Validate Agari Readout SFX")]
    public static void Validate()
    {
        var targets = Object.FindObjectsOfType<MahjongSoundsV2>(true);
        if (targets.Length == 0)
        {
            Debug.LogError("[WireAgariReadoutSfx] MahjongSoundsV2 が見つかりません。");
            return;
        }

        int missing = 0;
        foreach (var s in targets)
        {
            if (s == null) { continue; }

            bool ok = s.agariYakuRowSfx != null && s.agariScoreSfx != null && s.agariRankSfx != null;
            if (!ok) { ++missing; }

            Debug.Log(string.Format(
                "[WireAgariReadoutSfx] {0} : 役行={1} / 点数={2} / 称号={3} / sfxSource={4}",
                HierarchyPath(s.transform), Name(s.agariYakuRowSfx), Name(s.agariScoreSfx), Name(s.agariRankSfx),
                s.sfxSource == null ? "(なし＝鳴らない)" : s.sfxSource.name), s);
        }

        if (missing > 0)
        {
            Debug.LogWarning(string.Format("[WireAgariReadoutSfx] 未割当の卓が {0}/{1}。Wire Agari Readout SFX を実行してください。", missing, targets.Length));
        }
        else
        {
            Debug.Log(string.Format("[WireAgariReadoutSfx] {0}卓すべて割当済み。", targets.Length));
        }
    }

    // 1) 差し替えフォルダ → 2) 既存の割当 → 3) 既定パス の順で解決する。
    static AudioClip ResolveClip(string prefix, string defaultPath, MahjongSoundsV2[] targets, int slot)
    {
        var fromDir = FindInOverrideDir(prefix);
        if (fromDir != null) { return fromDir; }

        foreach (var s in targets)
        {
            if (s == null) { continue; }
            var existing = slot == 0 ? s.agariYakuRowSfx : (slot == 1 ? s.agariScoreSfx : s.agariRankSfx);
            if (existing != null) { return existing; }
        }

        var fallback = AssetDatabase.LoadAssetAtPath<AudioClip>(defaultPath);
        if (fallback == null)
        {
            Debug.LogWarning("[WireAgariReadoutSfx] 既定クリップが見つかりません: " + defaultPath);
        }
        return fallback;
    }

    static AudioClip FindInOverrideDir(string prefix)
    {
        if (!AssetDatabase.IsValidFolder(OverrideDir)) { return null; }

        foreach (var guid in AssetDatabase.FindAssets("t:AudioClip", new[] { OverrideDir }))
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

    static string Name(AudioClip clip)
    {
        return clip == null ? "(未設定)" : clip.name;
    }

    static string HierarchyPath(Transform t)
    {
        var path = t.name;
        while (t.parent != null)
        {
            t = t.parent;
            path = t.name + "/" + path;
        }
        return path;
    }
}
#endif
