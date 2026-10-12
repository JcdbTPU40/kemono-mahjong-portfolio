// じゃんたま動画を参照した「情報階層・配色・モーション」を、プロジェクト固有アセットで再構成する。
// 固有ロゴ・背景・筆文字画像・SEは複製しない。
//
// Tools > Kemono Mahjong > Build Presentation Screens
// Tools > Kemono Mahjong > Wire Presentation Screens
//
// どちらも冪等。UdonSharp Behaviour は常時activeのPresentationScreenへ置き、
// 表示ON/OFFは子のCutinPanel / SettlementPanelだけで行う。
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UdonSharp;
using UdonSharpEditor;
using VRC.Udon;
using SuzuFactory.AozoraMahjong;

public static class BuildPresentationScreens
{
    const string RootName = "PresentationScreen";
    const string FontPath = "Assets/Mahjong/Fonts/NotoSansJP-Bold.ttf";
    const string ScriptPath = "Assets/Mahjong/Sources/MahjongPresentationScreen.cs";
    const string ProgramAssetDir = "Assets/Mahjong/scene_UdonProgramSources";
    const string ProgramAssetPath = ProgramAssetDir + "/MahjongPresentationScreen Udon C# Program Asset.asset";
    const string ScoreCountSoundPath = "Assets/Mahjong/Sounds/点数移動のときの音.wav";

    static readonly string[] CharaPortraitPaths =
    {
        "Assets/Mahjong/Textures/UI/Characters/vol_mascot_pic.png",
        "Assets/Mahjong/Textures/UI/Characters/Shine_mascot_pic.png",
        "Assets/Mahjong/Textures/UI/Characters/Tsumuntema_mascot_pic.png"
    };

    // あがり画面・ResultScreen・役満パネルと中心Yを揃える（world 中心 Y 0.045）。
    // 卓ルート -0.305 + 0.55 - 0.20(パネルのオフセット) = 0.045。
    const float PanelHeight = 0.55f;
    const float CanvasScale = 0.00073f;
    static readonly Vector2 CanvasSize = new Vector2(1920f, 1080f);

    static readonly Color Navy = new Color(0.035f, 0.075f, 0.14f, 0.97f);
    static readonly Color NavyLight = new Color(0.065f, 0.13f, 0.21f, 0.98f);
    static readonly Color Gold = new Color(1.0f, 0.64f, 0.18f, 1.0f);
    static readonly Color White = new Color(0.94f, 0.97f, 1.0f, 1.0f);
    static readonly Color Backdrop = new Color(0.01f, 0.02f, 0.045f, 0.68f);

    const string TablePrefabPath = "Assets/Mahjong/Prefabas/KemonoMahjong.prefab";

    // ============================================================
    // Prefab 直接ビルド
    //  卓は KemonoMahjong.prefab のインスタンスで、この演出リグも Prefab 側にある。
    //  FindObjectsOfType は Prefab Mode を開いても Prefab 側の卓を拾わない（シーンの卓しか
    //  返さない）ため、通常の Build ではインスタンス側に override が積まれてしまう。
    //  見た目を変えるときはこちらを使うこと。
    //
    //  ※ SaveAsPrefabAsset の途中で UdonSharp が ArgumentNullException を吐くことがあるが
    //    保存自体は通る。閉じるときは ClearDirtiness してから GoToMainStage しないと、
    //    未保存ダイアログで Unity のメインスレッドが止まる。
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Build Presentation Screens (Prefab)")]
    public static void BuildPrefab()
    {
        var font = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
        if (font == null) { font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }

        var stage = UnityEditor.SceneManagement.PrefabStageUtility.OpenPrefab(TablePrefabPath);
        if (stage == null)
        {
            Debug.LogError("[BuildPresentationScreens] Prefab を開けません: " + TablePrefabPath);
            return;
        }

        var root = stage.prefabContentsRoot;
        if (root.GetComponent<Mahjong>() == null)
        {
            Debug.LogError("[BuildPresentationScreens] Prefab のルートに Mahjong がありません。");
            return;
        }

        BuildOne(root.transform, font);

        EditorSceneManager.MarkSceneDirty(stage.scene);
        PrefabUtility.SaveAsPrefabAsset(root, stage.assetPath);
        AssetDatabase.SaveAssets();
        stage.ClearDirtiness();
        UnityEditor.SceneManagement.StageUtility.GoToMainStage();

        Debug.Log("[BuildPresentationScreens] Prefab に生成/更新しました: " + TablePrefabPath
            + " … 次に Wire Presentation Screens (Prefab) を実行してください。");
    }

    [MenuItem("Tools/Kemono Mahjong/Wire Presentation Screens (Prefab)")]
    public static void WirePrefab()
    {
        if (EnsureProgramAsset(ScriptPath, ProgramAssetPath))
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[WirePresentationScreens] U#プログラムアセットを生成。コンパイル後にもう一度実行してください。");
            return;
        }

        var stage = UnityEditor.SceneManagement.PrefabStageUtility.OpenPrefab(TablePrefabPath);
        if (stage == null)
        {
            Debug.LogError("[WirePresentationScreens] Prefab を開けません: " + TablePrefabPath);
            return;
        }

        var root = stage.prefabContentsRoot;
        var table = root.GetComponent<Mahjong>();
        if (table == null)
        {
            Debug.LogError("[WirePresentationScreens] Prefab のルートに Mahjong がありません。");
            return;
        }

        if (!WireOne(table, LoadTextures(CharaPortraitPaths)))
        {
            Debug.LogError("[WirePresentationScreens] Prefab の配線に失敗しました。");
            return;
        }

        EditorSceneManager.MarkSceneDirty(stage.scene);
        PrefabUtility.SaveAsPrefabAsset(root, stage.assetPath);
        AssetDatabase.SaveAssets();
        stage.ClearDirtiness();
        UnityEditor.SceneManagement.StageUtility.GoToMainStage();

        Debug.Log("[WirePresentationScreens] Prefab を配線しました: " + TablePrefabPath);
    }

    [MenuItem("Tools/Kemono Mahjong/Build Presentation Screens")]
    public static void Build()
    {
        var font = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
        if (font == null)
        {
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            Debug.LogWarning("[BuildPresentationScreens] 日本語フォントが見つからないためLegacyRuntimeを使用: " + FontPath);
        }

        var tables = UnityEngine.Object.FindObjectsOfType<Mahjong>(true);
        Array.Sort(tables, (a, b) => string.CompareOrdinal(GetPath(a.transform), GetPath(b.transform)));
        if (tables.Length == 0)
        {
            Debug.LogError("[BuildPresentationScreens] Mahjong（卓）が見つかりません。");
            return;
        }

        foreach (var table in tables)
        {
            BuildOne(table.transform, font);
        }

        MarkDirty(tables[0].gameObject);
        Debug.Log("[BuildPresentationScreens] 完了：" + tables.Length + " 卓に対局開始・和了カットイン・点数精算UIを生成/更新。");
    }

    static void BuildOne(Transform tableRoot, Font font)
    {
        var root = EnsureChild(tableRoot, RootName);
        root.transform.localPosition = new Vector3(0f, PanelHeight, 0f);
        root.transform.localRotation = Quaternion.identity;
        root.transform.localScale = Vector3.one;
        root.SetActive(true);

        // SE用（クリップ未割当なら鳴らないだけ）。3D音として卓の位置から鳴らす。
        var audio = root.GetComponent<AudioSource>();
        if (audio == null) { audio = Undo.AddComponent<AudioSource>(root); }
        audio.playOnAwake = false;
        audio.loop = false;
        audio.spatialBlend = 1f;
        audio.dopplerLevel = 0f;
        audio.minDistance = 1.5f;
        audio.maxDistance = 12f;
        audio.rolloffMode = AudioRolloffMode.Linear;

        BuildCutin(root.transform, font);
        BuildSettlement(root.transform, font);
        CleanupChildren(root.transform, new[] { "CutinPanel", "SettlementPanel" });
    }

    static void BuildCutin(Transform root, Font font)
    {
        var panel = EnsureChild(root, "CutinPanel");
        // **エディタ表示を実行時に一致させるための姿勢。**
        //  MahjongPresentationScreen.cs は表示するとき
        //    localPosition = Vector3.down * 0.20 / localScale = Vector3.one * 0.7
        //  を「代入」し、そのまま元へ戻さない。代入なので最初から同じ値にしておけば
        //  冪等になり、シーンビューの見た目がそのまま実機の見え方になる。
        //  ※ Vector3.zero / Vector3.one に戻すと、エディタでは大きく見えるのに
        //    実機では小さく低く出る、という食い違いが復活する。
        panel.transform.localPosition = Vector3.down * 0.20f;
        panel.transform.localRotation = Quaternion.identity;
        panel.transform.localScale = Vector3.one * 0.7f;

        var content = ConfigureCanvas(panel.transform, 42);

        var backdrop = EnsureImage(content.transform, "Backdrop", Backdrop);
        StretchFull(backdrop.rectTransform);
        backdrop.raycastTarget = false;

        var speed0 = EnsureImage(content.transform, "Speed0", new Color(0.10f, 0.48f, 0.65f, 0.30f));
        SetRect(speed0.rectTransform, new Vector2(0f, 170f), new Vector2(2200f, 28f));
        speed0.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -8f);

        var speed1 = EnsureImage(content.transform, "Speed1", new Color(1.0f, 0.60f, 0.18f, 0.45f));
        SetRect(speed1.rectTransform, new Vector2(0f, -205f), new Vector2(2200f, 18f));
        speed1.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -8f);

        var band = EnsureImage(content.transform, "Band", Navy);
        SetRect(band.rectTransform, Vector2.zero, new Vector2(2200f, 430f));
        band.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -8f);

        var accent = EnsureImage(band.transform, "Accent", Gold);
        SetRect(accent.rectTransform, new Vector2(0f, 202f), new Vector2(2200f, 12f));
        accent.raycastTarget = false;

        var inner = EnsureImage(band.transform, "Inner", new Color(0.05f, 0.11f, 0.20f, 0.68f));
        SetRect(inner.rectTransform, new Vector2(0f, -4f), new Vector2(2200f, 330f));
        inner.raycastTarget = false;

        var mascotGo = EnsureUiChild(content.transform, "Mascot");
        var mascot = mascotGo.GetComponent<RawImage>();
        if (mascot == null) { mascot = mascotGo.AddComponent<RawImage>(); }
        mascot.color = Color.white;
        mascot.raycastTarget = false;
        SetRect(mascot.rectTransform, new Vector2(-430f, -18f), new Vector2(620f, 860f));

        var call = EnsureText(content.transform, "CallText", font, 170, new Vector2(430f, 75f), new Vector2(760f, 220f), "ロン");
        call.alignment = TextAnchor.MiddleCenter;
        call.color = new Color(1.0f, 0.30f, 0.34f, 1.0f);

        var player = EnsureText(content.transform, "PlayerText", font, 48, new Vector2(430f, -120f), new Vector2(700f, 80f), "Player");
        player.alignment = TextAnchor.MiddleCenter;
        player.color = White;

        // ダブロン・トリロン用の追加スロット。位置と大きさは実行時に人数で決まるので、
        // ここでは Mascot と同じ寸法で置いておくだけでよい。
        for (int i = 1; i < 3; ++i)
        {
            var extraGo = EnsureUiChild(content.transform, "Mascot" + i.ToString());
            var extra = extraGo.GetComponent<RawImage>();
            if (extra == null) { extra = extraGo.AddComponent<RawImage>(); }
            extra.color = Color.white;
            extra.raycastTarget = false;
            SetRect(extra.rectTransform, new Vector2(-430f, -18f), new Vector2(620f, 860f));
            extraGo.SetActive(false);
        }

        // 立ち絵ごとの名前。複数人のときだけ使う。立ち絵の上に描くので縁取りを付ける。
        for (int i = 0; i < 3; ++i)
        {
            var nameText = EnsureText(content.transform, "Name" + i.ToString(), font, 44,
                new Vector2(-430f, -150f), new Vector2(520f, 72f), "Player");
            nameText.alignment = TextAnchor.MiddleCenter;
            nameText.color = White;

            var outline = nameText.GetComponent<Outline>();
            if (outline == null) { outline = nameText.gameObject.AddComponent<Outline>(); }
            outline.effectColor = new Color(0.02f, 0.05f, 0.10f, 0.95f);
            outline.effectDistance = new Vector2(3f, 3f);
            outline.useGraphicAlpha = true;

            nameText.gameObject.SetActive(false);
        }

        EnsureCanvasGroup(content.gameObject);
        CleanupChildren(content.transform, new[] {
            "Backdrop", "Speed0", "Speed1", "Band", "Mascot", "Mascot1", "Mascot2",
            "CallText", "PlayerText", "Name0", "Name1", "Name2" });
        panel.SetActive(false);
    }

    static void BuildSettlement(Transform root, Font font)
    {
        var panel = EnsureChild(root, "SettlementPanel");
        // **エディタ表示を実行時に一致させるための姿勢。**
        //  MahjongPresentationScreen.cs は表示するとき
        //    localPosition = Vector3.down * 0.20 / localScale = Vector3.one * 0.7
        //  を「代入」し、そのまま元へ戻さない。代入なので最初から同じ値にしておけば
        //  冪等になり、シーンビューの見た目がそのまま実機の見え方になる。
        //  ※ Vector3.zero / Vector3.one に戻すと、エディタでは大きく見えるのに
        //    実機では小さく低く出る、という食い違いが復活する。
        panel.transform.localPosition = Vector3.down * 0.20f;
        panel.transform.localRotation = Quaternion.identity;
        panel.transform.localScale = Vector3.one * 0.7f;

        var content = ConfigureCanvas(panel.transform, 41);

        var backdrop = EnsureImage(content.transform, "Backdrop", Backdrop);
        StretchFull(backdrop.rectTransform);
        backdrop.raycastTarget = false;

        var topLine = EnsureImage(content.transform, "TopLine", Gold);
        SetRect(topLine.rectTransform, new Vector2(0f, 528f), new Vector2(1720f, 8f));
        topLine.raycastTarget = false;

        var header = EnsureText(content.transform, "Header", font, 68, new Vector2(0f, 468f), new Vector2(760f, 92f), "精算");
        header.alignment = TextAnchor.MiddleCenter;
        header.color = White;

        // 上段（対面）のカードと重なるため既定では出さない。必要なら SetActive(true) で復活する。
        var sub = EnsureText(content.transform, "Sub", font, 28, new Vector2(0f, 362f), new Vector2(600f, 50f), "");
        sub.alignment = TextAnchor.MiddleCenter;
        sub.color = Gold;
        sub.gameObject.SetActive(false);

        // 矢印はカードより先＝背面に置く（カードの上に線が乗ると点数が読みにくい）。
        EnsureTransferArrowTexture();
        for (int a = 0; a < NumTransferArrows; ++a)
        {
            BuildArrow(content.transform, a);
        }

        for (int i = 0; i < 4; ++i)
        {
            BuildCard(content.transform, i, font);
        }

        EnsureCanvasGroup(content.gameObject);

        var keep = new System.Collections.Generic.List<string>
        { "Backdrop", "TopLine", "Header", "Sub" };
        for (int a = 0; a < NumTransferArrows; ++a) { keep.Add("Arrow" + a); }
        for (int i = 0; i < 4; ++i) { keep.Add("Card" + i); }
        CleanupChildren(content.transform, keep.ToArray());

        // 描画順を固定（背景 → 矢印 → カード）。EnsureUiChild は既存の子を
        // そのまま返すので、並び替えないと古い順序が残る。
        for (int k = 0; k < keep.Count; ++k)
        {
            var c = content.transform.Find(keep[k]);
            if (c != null) { c.SetSiblingIndex(k); }
        }

        panel.SetActive(false);
    }

    // 雀魂の点数移動パネル。横長で、左に順位・右に増減と点数・右端に立ち絵。
    // 位置は実行時に MahjongPresentationScreen が席に合わせて置き直す。
    static readonly Vector2 CardSize = new Vector2(640f, 180f);

    static void BuildCard(Transform parent, int index, Font font)
    {
        var card = EnsureUiChild(parent, "Card" + index);
        var rt = card.GetComponent<RectTransform>();
        SetRect(rt, Vector2.zero, CardSize);
        EnsureCanvasGroup(card);

        var shadow = EnsureImage(card.transform, "Shadow", new Color(0f, 0f, 0f, 0.42f));
        SetRect(shadow.rectTransform, new Vector2(10f, -12f), CardSize);
        shadow.raycastTarget = false;

        var bg = EnsureImage(card.transform, "Bg", Navy);
        StretchFull(bg.rectTransform);
        bg.raycastTarget = false;

        // 増減の色が乗る帯（左端の縦ライン）。
        var accent = EnsureImage(card.transform, "Accent", Gold);
        SetRect(accent.rectTransform, new Vector2(-312f, 0f), new Vector2(16f, 180f));
        accent.raycastTarget = false;

        // 名前の帯（上端）。
        var namePlate = EnsureImage(card.transform, "NamePlate", NavyLight);
        SetRect(namePlate.rectTransform, new Vector2(0f, 62f), new Vector2(640f, 52f));
        namePlate.raycastTarget = false;

        var name = EnsureText(card.transform, "Name", font, 34, new Vector2(-90f, 62f), new Vector2(420f, 44f), "Player");
        name.alignment = TextAnchor.MiddleLeft;
        name.color = White;

        // 立ち絵は右端。パネルの上へ少しはみ出す。
        var portraitGo = EnsureUiChild(card.transform, "Portrait");
        var portrait = portraitGo.GetComponent<RawImage>();
        if (portrait == null) { portrait = portraitGo.AddComponent<RawImage>(); }
        portrait.color = Color.white;
        portrait.raycastTarget = false;
        SetRect(portrait.rectTransform, new Vector2(252f, 44f), new Vector2(112f, 112f));

        // 順位（左の大きい字）。
        var rank = EnsureText(card.transform, "Rank", font, 66, new Vector2(-215f, -20f), new Vector2(200f, 76f), "1位");
        rank.alignment = TextAnchor.MiddleLeft;
        rank.color = White;

        // 増減（右上）。
        var delta = EnsureText(card.transform, "Delta", font, 36, new Vector2(80f, -6f), new Vector2(300f, 46f), "+0");
        delta.alignment = TextAnchor.MiddleRight;
        delta.color = new Color(0.80f, 0.88f, 0.94f, 1.0f);

        // 精算後の持ち点（右の大きい字）。
        var after = EnsureText(card.transform, "After", font, 60, new Vector2(80f, -54f), new Vector2(340f, 74f), "25000");
        after.alignment = TextAnchor.MiddleRight;
        after.color = White;

        // 精算前の持ち点はカウント表示の起点に使う。雀魂の見た目には目立たせない。
        var before = EnsureText(card.transform, "Before", font, 20, new Vector2(-212f, -72f), new Vector2(200f, 28f), "25000");
        before.alignment = TextAnchor.MiddleLeft;
        before.color = new Color(0.55f, 0.63f, 0.72f, 1.0f);

        CleanupChildren(card.transform, new[]
        {
            "Shadow", "Bg", "Accent", "NamePlate", "Name", "Portrait",
            "Rank", "Delta", "After", "Before"
        });
    }

    // 点棒の流れを示す矢印。位置・長さ・角度は実行時に決まるので、ここでは枠だけ作る。
    // 払う席 × 受け取る席の最大組み合わせ数。四麻の流局（ノーテン2 × テンパイ2）が最大で4本。
    // トリロンは 1 × 3 = 3本、ツモは 3 × 1 = 3本なのでここが足りていれば全ケース引ける。
    const int NumTransferArrows = 4;
    const string TexTransferArrow = "Assets/Mahjong/Textures/FX/fx_transfer_arrow.png";

    static void BuildArrow(Transform parent, int index)
    {
        var go = EnsureUiChild(parent, "Arrow" + index);
        var raw = go.GetComponent<RawImage>();
        if (raw == null) { raw = go.AddComponent<RawImage>(); }
        raw.raycastTarget = false;
        raw.texture = AssetDatabase.LoadAssetAtPath<Texture2D>(TexTransferArrow);
        raw.color = new Color(1f, 1f, 1f, 0f);

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(400f, 52f);
        rt.localScale = Vector3.one;
        rt.localRotation = Quaternion.identity;
        go.SetActive(false);
    }

    // 先端に三角のついた矢印。左から右へ向く。色はスクリプトが乗せる。
    static void EnsureTransferArrowTexture()
    {
        if (AssetDatabase.LoadAssetAtPath<Texture2D>(TexTransferArrow) != null) { return; }

        const int W = 512;
        const int H = 128;
        var px = new Color[W * H];

        float headStart = 0.72f;   // ここから先が三角
        float shaftHalf = 0.16f;   // 軸の半分の太さ（v単位）
        float headHalf = 0.46f;    // 三角の根元の半分の太さ

        for (int y = 0; y < H; ++y)
        {
            float v = (y + 0.5f) / H;
            float dy = Mathf.Abs(v - 0.5f);
            for (int x = 0; x < W; ++x)
            {
                float u = (x + 0.5f) / W;

                float half;
                if (u < headStart)
                {
                    // 軸。根元は細く、先へ向かってわずかに太る。
                    half = Mathf.Lerp(shaftHalf * 0.65f, shaftHalf, u / headStart);
                }
                else
                {
                    // 三角。先端で0へ。
                    float t = (u - headStart) / (1f - headStart);
                    half = Mathf.Lerp(headHalf, 0f, t);
                }

                float a = half <= 0.001f ? 0f : Mathf.Clamp01((half - dy) * H / 2f);
                px[y * W + x] = new Color(1f, 1f, 1f, a);
            }
        }

        var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
        tex.SetPixels(px);
        tex.Apply();
        var png = tex.EncodeToPNG();
        UnityEngine.Object.DestroyImmediate(tex);

        System.IO.File.WriteAllBytes(TexTransferArrow, png);
        AssetDatabase.ImportAsset(TexTransferArrow);

        var importer = AssetImporter.GetAtPath(TexTransferArrow) as TextureImporter;
        if (importer != null)
        {
            importer.textureType = TextureImporterType.Default;
            importer.alphaIsTransparency = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.mipmapEnabled = true;
            importer.SaveAndReimport();
        }
    }

    [MenuItem("Tools/Kemono Mahjong/Wire Presentation Screens")]
    public static void Wire()
    {
        if (EnsureProgramAsset(ScriptPath, ProgramAssetPath))
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[WirePresentationScreens] U#プログラムアセットを生成。コンパイル後にWireをもう一度実行してください。");
            return;
        }

        var portraits = LoadTextures(CharaPortraitPaths);
        var tables = UnityEngine.Object.FindObjectsOfType<Mahjong>(true);
        Array.Sort(tables, (a, b) => string.CompareOrdinal(GetPath(a.transform), GetPath(b.transform)));
        if (tables.Length == 0)
        {
            Debug.LogError("[WirePresentationScreens] Mahjong（卓）が見つかりません。");
            return;
        }

        int wired = 0;
        foreach (var table in tables)
        {
            if (WireOne(table, portraits)) { ++wired; }
        }

        MarkDirty(tables[0].gameObject);
        Debug.Log("[WirePresentationScreens] 完了：" + wired + "/" + tables.Length + " 卓を配線。");
    }

    // 卓1つぶんの配線。シーンの卓にも Prefab のルートにも使える。
    static bool WireOne(Mahjong table, Texture[] portraits)
    {
        var root = table.transform.Find(RootName);
        if (root == null)
        {
            Debug.LogError("[WirePresentationScreens] " + GetPath(table.transform) + " に " + RootName + " がありません。先にBuildしてください。");
            return false;
        }

        var comp = (MahjongPresentationScreen)EnsureUSharpComponent(root.gameObject, typeof(MahjongPresentationScreen));
        if (comp == null) { return false; }

        var cutinPanel = root.Find("CutinPanel");
        var cutinContent = cutinPanel != null ? cutinPanel.Find("Canvas/Content") : null;
        var settlementPanel = root.Find("SettlementPanel");
        var settlementContent = settlementPanel != null ? settlementPanel.Find("Canvas/Content") : null;
        if (cutinContent == null || settlementContent == null)
        {
            Debug.LogError("[WirePresentationScreens] UI階層が不足しています。Build Presentation Screensを再実行してください: " + GetPath(root));
            return false;
        }

        comp.mahjong = table;
        comp.cutinPanelRoot = cutinPanel.gameObject;
        comp.cutinCanvasGroup = cutinContent.GetComponent<CanvasGroup>();
        comp.cutinBandRect = GetRect(cutinContent, "Band");
        comp.cutinMascotRect = GetRect(cutinContent, "Mascot");
        comp.cutinMascotImage = GetRawImage(cutinContent, "Mascot");

        // ダブロン・トリロンの同時表示スロット（[0] は上と同じ Mascot を共有する）。
        comp.cutinMascotRects = new RectTransform[] {
            GetRect(cutinContent, "Mascot"),
            GetRect(cutinContent, "Mascot1"),
            GetRect(cutinContent, "Mascot2") };
        comp.cutinMascotImages = new RawImage[] {
            GetRawImage(cutinContent, "Mascot"),
            GetRawImage(cutinContent, "Mascot1"),
            GetRawImage(cutinContent, "Mascot2") };
        comp.cutinNameTexts = new Text[] {
            GetText(cutinContent, "Name0"),
            GetText(cutinContent, "Name1"),
            GetText(cutinContent, "Name2") };
        comp.cutinCallText = GetText(cutinContent, "CallText");
        comp.cutinPlayerText = GetText(cutinContent, "PlayerText");
        comp.cutinBandImage = GetImage(cutinContent, "Band");
        comp.cutinAccentImage = GetImage(cutinContent, "Band/Accent");
        comp.charaMascotTextures = portraits;
        comp.defaultMascotTexture = portraits.Length > 0 ? portraits[0] : null;

        comp.settlementPanelRoot = settlementPanel.gameObject;
        comp.settlementCanvasGroup = settlementContent.GetComponent<CanvasGroup>();
        comp.settlementHeaderText = GetText(settlementContent, "Header");
        comp.settlementSubText = null;   // 上段カードと重なるため未使用

        var cardRoots = new GameObject[4];
        var cardGroups = new CanvasGroup[4];
        var cardRects = new RectTransform[4];
        var cardPortraits = new RawImage[4];
        var cardNames = new Text[4];
        var beforeTexts = new Text[4];
        var deltaTexts = new Text[4];
        var afterTexts = new Text[4];
        var backgrounds = new Image[4];
        var accents = new Image[4];
        var rankTexts = new Text[4];

        for (int i = 0; i < 4; ++i)
        {
            var card = settlementContent.Find("Card" + i);
            if (card == null) { continue; }

            cardRoots[i] = card.gameObject;
            cardGroups[i] = card.GetComponent<CanvasGroup>();
            cardRects[i] = card.GetComponent<RectTransform>();
            cardPortraits[i] = GetRawImage(card, "Portrait");
            cardNames[i] = GetText(card, "Name");
            beforeTexts[i] = GetText(card, "Before");
            deltaTexts[i] = GetText(card, "Delta");
            afterTexts[i] = GetText(card, "After");
            backgrounds[i] = GetImage(card, "Bg");
            accents[i] = GetImage(card, "Accent");
            rankTexts[i] = GetText(card, "Rank");

            if (cardPortraits[i] != null)
            {
                cardPortraits[i].texture = portraits.Length > 0 ? portraits[0] : null;
                EditorUtility.SetDirty(cardPortraits[i]);
            }
        }

        var arrowImages = new RawImage[NumTransferArrows];
        var arrowRects = new RectTransform[NumTransferArrows];
        for (int a = 0; a < NumTransferArrows; ++a)
        {
            var arrow = settlementContent.Find("Arrow" + a);
            if (arrow == null) { continue; }
            arrowImages[a] = arrow.GetComponent<RawImage>();
            arrowRects[a] = arrow.GetComponent<RectTransform>();
        }

        comp.settlementCardRoots = cardRoots;
        comp.settlementCardCanvasGroups = cardGroups;
        comp.settlementCardRects = cardRects;
        comp.settlementPortraitImages = cardPortraits;
        comp.settlementNameTexts = cardNames;
        comp.settlementBeforeTexts = beforeTexts;
        comp.settlementDeltaTexts = deltaTexts;
        comp.settlementAfterTexts = afterTexts;
        comp.settlementCardBackgrounds = backgrounds;
        comp.settlementAccentImages = accents;
        comp.settlementRankTexts = rankTexts;
        comp.settlementArrowImages = arrowImages;
        comp.settlementArrowRects = arrowRects;

        // 席の並び。自分が手前（下）に来るよう実行時に割り当てられる。
        // C#側の既定値を変えてもシーンに載っている値は更新されないので毎回明示する。
        comp.slotSelf = new Vector2(0f, -320f);
        comp.slotRight = new Vector2(600f, 10f);
        comp.slotTop = new Vector2(0f, 300f);
        comp.slotLeft = new Vector2(-600f, 10f);
        comp.settlementArrowThickness = 52f;
        comp.settlementArrowMargin = 210f;
        comp.arrowColor = new Color(1f, 1f, 1f, 0.95f);
        comp.settlementSlideDistance = 180f;

        // 動画から抽出した値。長い保持は対局操作を隠すため、局間精算は短い側へ寄せる。
        // 複数人カットインの並び。U#のシリアライズ済み値は既定値変更で更新されないので毎回代入する。
        comp.cutinMultiSpacing2 = 470f;
        comp.cutinMultiSpacing3 = 415f;
        comp.cutinMultiScale2 = 0.82f;
        comp.cutinMultiScale3 = 0.70f;
        comp.cutinNameY = -150f;

        comp.cutinEnterSeconds = 0.20f;
        comp.cutinHoldSeconds = 0.85f;
        comp.cutinExitSeconds = 0.20f;
        comp.settlementEnterSeconds = 0.28f;
        comp.settlementCardStagger = 0.08f;
        // 点数カウントの音。カウント時間は音の実尺に合わせる
        // （鳴り終わりと数字が止まるタイミングを一致させる）。
        var countClip = AssetDatabase.LoadAssetAtPath<AudioClip>(ScoreCountSoundPath);
        comp.scoreCountSound = countClip;
        comp.seSource = root.GetComponent<AudioSource>();

        // 勝者カットイン（キャラ立ち絵＋ロン/ツモ）と同時に鳴らす風切り音。
        //   風切り -16.6 LUFS → 0.12 で約 -35 LUFS 相当（牌エフェクトの爆発と同格）。
        comp.winCutinSound = AssetDatabase.LoadAssetAtPath<AudioClip>(
            "Assets/Mahjong/Sounds/AgariSFX/パンチの風切り音（スローモーション）1.mp3");
        comp.winCutinVolume = KemonoAudioNormalizer.SeScale(comp.winCutinSound, 0.253f);
        // 点数カウント音 -24.3 LUFS → 0.18 で約 -39 LUFS 相当。
        // 1.96秒の持続音で、鳴っている間は他のSEが無いぶん控えめでよい。
        comp.seVolume = KemonoAudioNormalizer.SeScale(comp.scoreCountSound, 0.567f);
        comp.settlementCountSeconds = countClip != null ? countClip.length : 1.40f;
        comp.settlementHoldSeconds = 1.20f;
        comp.settlementExitSeconds = 0.30f;
        comp.matchIntroHoldSeconds = 2.40f;

        UdonSharpEditorUtility.CopyProxyToUdon(comp);
        EditorUtility.SetDirty(comp);

        if (table.presentationScreen != comp)
        {
            table.presentationScreen = comp;
            UdonSharpEditorUtility.CopyProxyToUdon(table);
            EditorUtility.SetDirty(table);
        }

        return true;
    }

    static Transform ConfigureCanvas(Transform panel, int sortingOrder)
    {
        var canvasGo = EnsureUiChild(panel, "Canvas");
        var canvas = canvasGo.GetComponent<Canvas>();
        if (canvas == null) { canvas = canvasGo.AddComponent<Canvas>(); }
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.overrideSorting = true;
        canvas.sortingOrder = sortingOrder;

        var scaler = canvasGo.GetComponent<CanvasScaler>();
        if (scaler == null) { scaler = canvasGo.AddComponent<CanvasScaler>(); }
        scaler.dynamicPixelsPerUnit = 2f;

        var raycaster = canvasGo.GetComponent<GraphicRaycaster>();
        if (raycaster != null) { UnityEngine.Object.DestroyImmediate(raycaster); }

        var canvasRt = canvasGo.GetComponent<RectTransform>();
        canvasRt.localPosition = Vector3.zero;
        canvasRt.localRotation = Quaternion.identity;
        canvasRt.localScale = new Vector3(CanvasScale, CanvasScale, CanvasScale);
        canvasRt.sizeDelta = CanvasSize;

        var content = EnsureUiChild(canvasGo.transform, "Content");
        StretchFull(content.GetComponent<RectTransform>());
        EnsureCanvasGroup(content);
        return content.transform;
    }

    static CanvasGroup EnsureCanvasGroup(GameObject go)
    {
        var group = go.GetComponent<CanvasGroup>();
        if (group == null) { group = go.AddComponent<CanvasGroup>(); }
        group.interactable = false;
        group.blocksRaycasts = false;
        return group;
    }

    static Image EnsureImage(Transform parent, string name, Color color)
    {
        var go = EnsureUiChild(parent, name);
        var image = go.GetComponent<Image>();
        if (image == null) { image = go.AddComponent<Image>(); }
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    static Text EnsureText(Transform parent, string name, Font font, int fontSize, Vector2 pos, Vector2 size, string placeholder)
    {
        var go = EnsureUiChild(parent, name);
        var text = go.GetComponent<Text>();
        if (text == null) { text = go.AddComponent<Text>(); }
        text.font = font;
        text.fontSize = fontSize;
        text.fontStyle = FontStyle.Bold;
        text.text = placeholder;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.raycastTarget = false;
        SetRect(go.GetComponent<RectTransform>(), pos, size);

        var outline = go.GetComponent<Outline>();
        if (outline == null) { outline = go.AddComponent<Outline>(); }
        outline.effectColor = new Color(0f, 0f, 0f, 0.80f);
        outline.effectDistance = new Vector2(2f, -2f);
        return text;
    }

    static void SetRect(RectTransform rt, Vector2 pos, Vector2 size)
    {
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        rt.localScale = Vector3.one;
    }

    static void StretchFull(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        rt.localScale = Vector3.one;
    }

    static Texture[] LoadTextures(string[] paths)
    {
        var result = new Texture[paths.Length];
        for (int i = 0; i < paths.Length; ++i)
        {
            result[i] = AssetDatabase.LoadAssetAtPath<Texture>(paths[i]);
            if (result[i] == null)
            {
                Debug.LogWarning("[BuildPresentationScreens] キャラ画像が見つかりません: " + paths[i]);
            }
        }
        return result;
    }

    static Text GetText(Transform root, string path)
    {
        var t = root.Find(path);
        return t != null ? t.GetComponent<Text>() : null;
    }

    static Image GetImage(Transform root, string path)
    {
        var t = root.Find(path);
        return t != null ? t.GetComponent<Image>() : null;
    }

    static RawImage GetRawImage(Transform root, string path)
    {
        var t = root.Find(path);
        return t != null ? t.GetComponent<RawImage>() : null;
    }

    static RectTransform GetRect(Transform root, string path)
    {
        var t = root.Find(path);
        return t != null ? t.GetComponent<RectTransform>() : null;
    }

    static bool EnsureProgramAsset(string scriptPath, string assetPath)
    {
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
        if (script == null)
        {
            Debug.LogError("[WirePresentationScreens] スクリプトが見つかりません: " + scriptPath);
            return false;
        }

        var type = script.GetClass();
        if (type != null && UdonSharpProgramAsset.GetProgramAssetForClass(type) != null) { return false; }
        if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(assetPath) != null) { return false; }

        var programAsset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
        programAsset.sourceCsScript = script;
        AssetDatabase.CreateAsset(programAsset, assetPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[WirePresentationScreens] U#プログラムアセットを生成: " + assetPath);
        return true;
    }

    static UdonSharpBehaviour EnsureUSharpComponent(GameObject go, Type type)
    {
        var proxy = (UdonSharpBehaviour)go.GetComponent(type);
        if (proxy != null)
        {
            if (UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy) != null) { return proxy; }

            Debug.LogWarning("[WirePresentationScreens] " + type.Name + " のbacking UdonBehaviourが無いため作り直します。");
            UnityEngine.Object.DestroyImmediate(proxy);
            foreach (var ub in go.GetComponents<UdonBehaviour>())
            {
                if (ub.programSource == null) { UnityEngine.Object.DestroyImmediate(ub); }
            }
        }

        var added = (UdonSharpBehaviour)UdonSharpUndo.AddComponent(go, type);
        if (added != null && UdonSharpEditorUtility.GetBackingUdonBehaviour(added) == null)
        {
            Debug.LogError("[WirePresentationScreens] " + type.Name + " の追加に失敗（プログラムアセット未コンパイル？）。");
            UnityEngine.Object.DestroyImmediate(added);
            return null;
        }
        return added;
    }

    static GameObject EnsureChild(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null) { return t.gameObject; }

        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Create " + name);
        go.transform.SetParent(parent, false);
        return go;
    }

    static GameObject EnsureUiChild(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null)
        {
            if (t.GetComponent<RectTransform>() != null) { return t.gameObject; }
            Undo.DestroyObjectImmediate(t.gameObject);
        }

        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Create " + name);
        go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);
        return go;
    }

    static void CleanupChildren(Transform parent, string[] keepNames)
    {
        var keep = new HashSet<string>(keepNames);
        for (int i = parent.childCount - 1; i >= 0; --i)
        {
            var child = parent.GetChild(i);
            if (!keep.Contains(child.name)) { Undo.DestroyObjectImmediate(child.gameObject); }
        }
    }

    static void MarkDirty(GameObject go)
    {
        if (go.scene.IsValid()) { EditorSceneManager.MarkSceneDirty(go.scene); }
        else { EditorUtility.SetDirty(go); }
    }

    static string GetPath(Transform t)
    {
        string path = t.name;
        while (t.parent != null)
        {
            t = t.parent;
            path = t.name + "/" + path;
        }
        return path;
    }
}
