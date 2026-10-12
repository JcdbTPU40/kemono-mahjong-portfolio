#if UNITY_EDITOR
// チュンビーム（中を捨てたときの帯カットイン）の部品生成・ボイス割り当て・検証。
//   Tools > Kemono Mahjong > Build Chun Beam (Prefab)   … テクスチャ生成＋卓Prefabの宣言演出リグ4本へ部品を追加
//   Tools > Kemono Mahjong > Wire Chun Beam Voices      … Assets/Mahjong/Sounds/{Vol|Shine} の
//                                                          ChunBeam_{キャラ}_{1..4} または chunbeam1x/2x/3x/final - {キャラ}
//                                                          を MahjongSoundsV2 へ割り当て
//   Tools > Kemono Mahjong > Validate Chun Beam         … シーンの全卓・全リグに部品とボイス枠があるか
//
// 部品は各リグの Portrait の子 ChunGroup に置く（MahjongCallEffect が名前で引く＝配線なし）。
// 既存の Build Call Effects は Canvas と BandGroup の知らない子を消すが、Portrait の子は触らないので
// そちらを再実行しても消えない。
// Prefab は LoadPrefabContents → SaveAsPrefabAsset で編集する（Prefab ステージを開かない＝閉じ忘れが無い）。
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using SuzuFactory.AozoraMahjong;

public static class BuildChunBeam
{
    const string PrefabPath = "Assets/Mahjong/Prefabas/KemonoMahjong.prefab";
    const string BeamTexPath = "Assets/Mahjong/Textures/FX/fx_chun_beam.png";
    const string FlareTexPath = "Assets/Mahjong/Textures/FX/fx_chun_flare.png";

    static readonly string[] CharaVoiceNames = { "Vol", "Shine" };

    // ============================================================
    // Build
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Build Chun Beam (Prefab)")]
    public static void BuildPrefab()
    {
        var beamTex = EnsureTexture(BeamTexPath, 256, 64, BeamPixel);
        var flareTex = EnsureTexture(FlareTexPath, 128, 128, FlarePixel);

        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        int rigs = 0;
        try
        {
            foreach (var fx in root.GetComponentsInChildren<MahjongCallEffect>(true))
            {
                if (BuildRig(fx, beamTex, flareTex)) { ++rigs; }
            }
            AssignVoices(root.GetComponentsInChildren<MahjongSoundsV2>(true));
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
        Debug.Log("[BuildChunBeam] 完了：宣言演出リグ " + rigs + " 本へ部品を追加／更新しました。");
    }

    static bool BuildRig(MahjongCallEffect fx, Texture beamTex, Texture flareTex)
    {
        if (fx.portraitRect == null || fx.portraitImage == null)
        {
            Debug.LogError("[BuildChunBeam] Portrait が未配線のリグ: " + fx.name);
            return false;
        }
        var portrait = fx.portraitRect;
        var mat = fx.portraitImage.material;

        var group = EnsureUi(portrait, "ChunGroup");
        var groupRt = group.GetComponent<RectTransform>();
        groupRt.anchorMin = Vector2.zero;
        groupRt.anchorMax = Vector2.one;
        groupRt.pivot = new Vector2(0.5f, 0.5f);
        groupRt.offsetMin = Vector2.zero;
        groupRt.offsetMax = Vector2.zero;
        groupRt.localScale = Vector3.one;
        groupRt.localRotation = Quaternion.identity;

        // 白飛び：目の光と同じ柔らかい光を Canvas の幅いっぱいに広げる（四角い縁を出さない）。
        // 位置は実行時に両目の間へ置く。FxRoot は非アクティブで保存されているので includeInactive が要る。
        var canvas = portrait.GetComponentInParent<Canvas>(true);
        if (canvas == null)
        {
            Debug.LogError("[BuildChunBeam] Canvas が見つからないリグ: " + fx.name);
            return false;
        }
        var canvasRt = canvas.GetComponent<RectTransform>();
        var flash = EnsureRaw(group.transform, "Flash", flareTex, mat, new Vector2(0.5f, 0.5f));
        var flashRt = flash.rectTransform;
        float flashSize = canvasRt.sizeDelta.x * 1.2f;
        flashRt.sizeDelta = new Vector2(flashSize, flashSize);
        flash.color = new Color(1f, 0.97f, 0.86f, 0f);
        flash.gameObject.SetActive(false);

        for (int i = 0; i < 2; ++i)
        {
            var glow = EnsureRaw(group.transform, "Glow" + i, beamTex, mat, new Vector2(1f, 0.5f));
            glow.rectTransform.sizeDelta = new Vector2(0f, 46f);
            glow.color = new Color(1f, 0.2f, 0.2f, 0f);

            var core = EnsureRaw(group.transform, "Core" + i, beamTex, mat, new Vector2(1f, 0.5f));
            core.rectTransform.sizeDelta = new Vector2(0f, 16f);
            core.color = new Color(1f, 1f, 1f, 0f);

            var flare = EnsureRaw(group.transform, "Flare" + i, flareTex, mat, new Vector2(0.5f, 0.5f));
            flare.rectTransform.sizeDelta = new Vector2(120f, 120f);
            flare.color = new Color(1f, 1f, 1f, 0f);
        }

        // 描画順：白飛び → にじみ → 芯 → 目の光。
        string[] order = { "Flash", "Glow0", "Glow1", "Core0", "Core1", "Flare0", "Flare1" };
        for (int i = 0; i < order.Length; ++i)
        {
            var t = group.transform.Find(order[i]);
            if (t != null) { t.SetSiblingIndex(i); }
        }

        // 実行時に MahjongCallEffect が出し入れする。
        group.SetActive(false);
        return true;
    }

    static GameObject EnsureUi(Transform parent, string name)
    {
        var t = parent.Find(name);
        GameObject go;
        if (t == null)
        {
            go = new GameObject(name, typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
        }
        else
        {
            go = t.gameObject;
        }
        return go;
    }

    static RawImage EnsureRaw(Transform parent, string name, Texture tex, Material mat, Vector2 pivot)
    {
        var go = EnsureUi(parent, name);
        var raw = go.GetComponent<RawImage>();
        if (raw == null) { raw = go.AddComponent<RawImage>(); }
        raw.texture = tex;
        raw.material = mat;
        raw.raycastTarget = false;
        var rt = raw.rectTransform;
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = pivot;
        rt.localScale = Vector3.one;
        rt.localRotation = Quaternion.identity;
        rt.anchoredPosition = Vector2.zero;
        return raw;
    }

    // ============================================================
    // テクスチャ（白＋アルファ。色は RawImage.color で付ける）
    // ============================================================
    delegate float PixelFunc(float u, float v);

    // ビーム：縦はガウスでにじみ、横は均一。先端（左端）だけ細く消える。
    static float BeamPixel(float u, float v)
    {
        float y = (v - 0.5f) * 2f;
        float body = Mathf.Exp(-(y * y) / (2f * 0.30f * 0.30f));
        float tip = Mathf.Clamp01(u / 0.18f);          // 左端 18% で先細り
        float root = Mathf.Clamp01((1f - u) / 0.03f);  // 目の側は目の光が隠すので短く
        return body * tip * tip * root;
    }

    // 目の光：丸いにじみ＋十字の光条。
    static float FlarePixel(float u, float v)
    {
        float x = (u - 0.5f) * 2f;
        float y = (v - 0.5f) * 2f;
        float r2 = x * x + y * y;
        float glow = Mathf.Exp(-r2 * 5f);
        float star = Mathf.Exp(-Mathf.Abs(x) * 30f) * Mathf.Exp(-Mathf.Abs(y) * 2.2f)
                   + Mathf.Exp(-Mathf.Abs(y) * 30f) * Mathf.Exp(-Mathf.Abs(x) * 2.2f);
        return Mathf.Clamp01(glow + 0.6f * star) * Mathf.Clamp01((1f - Mathf.Sqrt(r2)) / 0.15f);
    }

    static Texture2D EnsureTexture(string path, int w, int h, PixelFunc f)
    {
        var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (existing != null) { return existing; }

        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        var px = new Color[w * h];
        for (int y = 0; y < h; ++y)
        {
            for (int x = 0; x < w; ++x)
            {
                float a = f((x + 0.5f) / w, (y + 0.5f) / h);
                px[y * w + x] = new Color(1f, 1f, 1f, a);
            }
        }
        tex.SetPixels(px);
        tex.Apply();
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);

        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = TextureImporterType.Default;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = false;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.npotScale = TextureImporterNPOTScale.None; // 縦横とも2の累乗
        importer.textureCompression = TextureImporterCompression.Compressed;
        importer.SaveAndReimport();

        Debug.Log("[BuildChunBeam] テクスチャを生成: " + path);
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    // ============================================================
    // ボイス（ファイル名規約で割り当て）
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Wire Chun Beam Voices")]
    public static void WireVoices()
    {
        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        int found;
        try
        {
            found = AssignVoices(root.GetComponentsInChildren<MahjongSoundsV2>(true));
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
        Debug.Log("[BuildChunBeam] チュンビームのボイス " + found + " / 8 本を割り当てました。"
                  + "音量を揃えるため、素材はキャラのボイスフォルダへ置いて Normalize Audio も実行すること。");
    }

    // 段ごとに受け付けるファイル名（拡張子なし・大文字小文字は区別しない）。{0}=キャラ名。
    //   規約名   … ChunBeam_Shine_1
    //   収録名   … chunbeam1x - Shine / chunbeam2x / chunbeam3x / chunbeamfinal（支給素材の名前）
    static readonly string[][] VoiceNamePatterns =
    {
        new[] { "ChunBeam_{0}_1", "chunbeam1x - {0}", "chunbeam - {0}" },
        new[] { "ChunBeam_{0}_2", "chunbeam2x - {0}" },
        new[] { "ChunBeam_{0}_3", "chunbeam3x - {0}" },
        new[] { "ChunBeam_{0}_4", "chunbeam4x - {0}", "chunbeamfinal - {0}" },
    };

    // キャラのボイスフォルダ（Normalize Audio が音量をそろえる場所）から探して
    // [キャラ*4 + 段-1] に入れる。見つかった本数を返す。
    static int AssignVoices(MahjongSoundsV2[] sounds)
    {
        var clips = new AudioClip[CharaVoiceNames.Length * 4];
        int found = 0;
        for (int c = 0; c < CharaVoiceNames.Length; ++c)
        {
            string folder = "Assets/Mahjong/Sounds/" + CharaVoiceNames[c];
            if (!AssetDatabase.IsValidFolder(folder)) { continue; }

            var byName = new Dictionary<string, AudioClip>();
            foreach (string guid in AssetDatabase.FindAssets("t:AudioClip", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                byName[Path.GetFileNameWithoutExtension(path).ToLowerInvariant()] =
                    AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            }

            for (int level = 1; level <= 4; ++level)
            {
                foreach (string pattern in VoiceNamePatterns[level - 1])
                {
                    AudioClip clip;
                    if (!byName.TryGetValue(string.Format(pattern, CharaVoiceNames[c]).ToLowerInvariant(), out clip)) { continue; }
                    clips[c * 4 + level - 1] = clip;
                    ++found;
                    Debug.Log("[BuildChunBeam] " + CharaVoiceNames[c] + " " + level + "段目 = " + AssetDatabase.GetAssetPath(clip));
                    break;
                }
            }
        }
        foreach (var s in sounds) { s.chunBeamVoices = (AudioClip[])clips.Clone(); }
        return found;
    }

    // ============================================================
    // Validate（シーンの全卓）
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Validate Chun Beam")]
    public static void Validate()
    {
        string[] parts = { "Flash", "Glow0", "Glow1", "Core0", "Core1", "Flare0", "Flare1" };
        int ok = 0, total = 0, errors = 0;
        foreach (var table in Object.FindObjectsOfType<Mahjong>(true))
        {
            if (table.callEffects == null) { continue; }
            foreach (var fx in table.callEffects)
            {
                if (fx == null) { continue; }
                ++total;
                var group = fx.portraitRect != null ? fx.portraitRect.Find("ChunGroup") : null;
                bool good = group != null;
                if (good)
                {
                    foreach (var p in parts)
                    {
                        var t = group.Find(p);
                        if (t == null || t.GetComponent<RawImage>() == null) { good = false; break; }
                    }
                }
                if (good) { ++ok; }
                else { ++errors; Debug.LogError("[ValidateChunBeam] 部品が足りない: " + table.name + "/" + fx.name); }
            }
            if (table.soundsV2 == null || table.soundsV2.chunBeamVoices == null || table.soundsV2.chunBeamVoices.Length < 8)
            {
                ++errors;
                Debug.LogError("[ValidateChunBeam] ボイス枠（8本）が無い: " + table.name);
            }
        }
        int voices = 0;
        var any = Object.FindObjectOfType<MahjongSoundsV2>(true);
        if (any != null && any.chunBeamVoices != null)
        {
            foreach (var c in any.chunBeamVoices) { if (c != null) { ++voices; } }
        }
        string msg = "[ValidateChunBeam] リグ " + ok + "/" + total + " OK／ボイス " + voices + "/8 本割当済み";
        if (errors == 0) { Debug.Log(msg); } else { Debug.LogError(msg + "／エラー " + errors + " 件"); }
    }
}
#endif
