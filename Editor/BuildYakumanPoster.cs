#if UNITY_EDITOR
// 役満ポスター（インスタンス内へ一斉掲示）の生成・配線・検証。
//
// ■ 何を作るか
//   1) Assets/Mahjong/Textures/YakumanPoster.renderTexture … ポスター1枚ぶんの絵
//   2) シーンルート "YakumanPoster"（誰も来ない y=-500 に置く）
//        YakumanPoster            … YakumanPosterManager（U#）
//         ├ Poster (Canvas)       … A4比の World Space Canvas。ここに絵を組む
//         │   ├ BG / Portrait / Title / Yaku / NameBand/Name
//         └ Camera                … Canvas だけを撮って RenderTexture へ出す
//   3) 卓（Mahjong）・広告パネル・自画像ストア・言語マネージャへの配線
//
// ■ なぜカメラ＋RenderTexture なのか
//   ポスターの中身は毎回変わるので焼いた画像では出せない。かといってパネル23枚に
//   Canvas を持たせると掲示中ずっと23枚ぶんの Canvas が積み上がる。1枚だけ撮って
//   全パネルへ配れば、パネル側は普段の広告と同じ「テクスチャ差し替え」で済む。
//
// ■ 使い方
//   a) Tools > Kemono Mahjong > Build Yakuman Poster     … 一式を作る（何度でも実行可）
//   b) Tools > Kemono Mahjong > Wire Yakuman Poster      … 卓・広告パネル等へ配線
//   c) Tools > Kemono Mahjong > Validate Yakuman Poster  … 参照欠けの点検
//
// ■ 落とし穴
//   ・レイヤー "YakumanPoster" が要る（カメラがポスター以外を撮らないため）。
//     無ければ Build が作りに行く。作れなければエラーで止まる。
//   ・スタジオは y=-500。ワールド本体と重ならなければどこでもよいが、
//     動かすときは Canvas とカメラを一緒に動かすこと（親ごと動かせば済む）。
using System;
using System.Collections.Generic;
using TMPro;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using SuzuFactory.AozoraMahjong;

public static class BuildYakumanPoster
{
    const string RootName = "YakumanPoster";
    const string LayerName = "YakumanPoster";
    const string ScriptPath = "Assets/Mahjong/Sources/YakumanPosterManager.cs";
    const string ProgramAssetDir = "Assets/Mahjong/scene_UdonProgramSources";
    const string ProgramAssetPath = ProgramAssetDir + "/YakumanPosterManager Udon C# Program Asset.asset";
    const string RtPath = "Assets/Mahjong/Textures/YakumanPoster.renderTexture";
    const string IntroRtPath = "Assets/Mahjong/Textures/YakumanPosterIntro.renderTexture";
    const string IntroRigName = "IntroRig";
    // 導入ステージのレイヤー（役満カットインが使っているもの）。
    const string StageLayerName = "YakumanStage";

    // 導入の映像をどの縦横比で撮るか。
    //   ポスターと同じ比（既定）… A4いっぱいに映る。牌が大きく出て見栄えがする代わり、
    //                             卓のカットイン（16:9）より左右が狭い＝端の動きは切れる。
    //   16f / 9f              … 卓とまったく同じ構図。A4の中央へ帯状に入り、上下は黒。
    // カメラの縦画角はクリップが持っている（40度固定）ので、ここは横の見え方だけを決める。
    static readonly float IntroAspect = (float)PosterW / PosterH;
    const string FontAssetPath = "Assets/Mahjong/Fonts/NotoSansJP-Bold SDF.asset";

    // --- ポスターの寸法（px＝Canvasローカル＝RenderTextureの画素）---
    // A4（1:1.4142）。広告パネルの面が A4 実寸なので比を合わせる。
    const int PosterW = 512;
    const int PosterH = 724;
    const float CanvasScale = 0.01f;

    // スタジオの置き場所。ワールド本体と重ならなければどこでもよい。
    static readonly Vector3 StudioPos = new Vector3(0f, -500f, 0f);

    // --- レイアウト（左上原点・yは下方向がマイナス）---
    static readonly Vector2 TitlePos = new Vector2(16f, -6f);
    static readonly Vector2 TitleSize = new Vector2(480f, 152f);
    const float TitleFontMax = 120f;
    const float TitleFontMin = 44f;
    const float TitleSpacing = 88f;   // 1/100em 単位。役満の2文字を左右へ開く
    const float PortraitTopMargin = 118f;  // 称号の帯にキャラの頭がかぶらないよう下げる量

    static readonly Vector2 YakuPos = new Vector2(376f, -140f);
    static readonly Vector2 YakuSize = new Vector2(124f, 580f);
    const float YakuFontMax = 92f;
    const float YakuFontMin = 28f;
    const float YakuLineSpacing = -20f;  // 縦組みは字間を詰めないと間延びする

    static readonly Vector2 BandPos = new Vector2(0f, -550f);
    static readonly Vector2 BandSize = new Vector2(384f, 80f);
    const float NameFontMax = 56f;
    const float NameFontMin = 18f;

    // --- 既定値（U#のシリアライズ済み値は既定値を変えても更新されないので毎回代入する）---
    const float DisplaySeconds = 90f;
    const float MinIntervalSeconds = 5f;
    const float RenderBurstSeconds = 0.15f;
    const float RerenderIntervalSeconds = 10f;

    static TMP_FontAsset font_;

    // ============================================================
    // Build
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Build Yakuman Poster")]
    public static void Build()
    {
        font_ = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
        if (font_ == null)
        {
            Debug.LogError("[BuildYakumanPoster] 日本語TMPフォントが見つかりません: " + FontAssetPath);
            return;
        }

        int layer = EnsureLayer();
        if (layer < 0) { return; }

        if (EnsureProgramAsset())
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[BuildYakumanPoster] U# Program Asset を生成しました。" +
                      "コンパイル完了後、もう一度 Build Yakuman Poster を実行してください。");
            return;
        }

        RenderTexture rt = EnsureRenderTexture();
        if (rt == null) { return; }

        GameObject root = FindSceneObject(RootName);
        if (root == null)
        {
            root = new GameObject(RootName);
            Undo.RegisterCreatedObjectUndo(root, "Build Yakuman Poster");
            root.transform.position = StudioPos;
        }

        Transform canvasT = BuildCanvas(root.transform);
        Camera cam = BuildCamera(root.transform, rt, layer);
        SetLayerRecursively(root, layer);

        // 導入（YakumanIntro）を流すためのカットイン複製。レイヤーは元のまま
        // （ステージは YakumanStage・カットインのパネルは Default）なので、
        // SetLayerRecursively のあとに作る＝ポスター用レイヤーで塗り潰さない。
        RenderTexture introRt = EnsureIntroRenderTexture();
        MahjongYakumanCutin introCutin = EnsureIntroRig(root, introRt);

        var poster = (YakumanPosterManager)EnsureUSharpComponent(root, typeof(YakumanPosterManager));
        if (poster == null) { return; }

        poster.posterCamera = cam;
        poster.posterTexture = rt;
        poster.posterRect = (RectTransform)canvasT;
        poster.titleText = FindTmp(canvasT, "Title");
        poster.yakuText = FindTmp(canvasT, "Yaku");
        poster.yakuRect = (RectTransform)canvasT.Find("Yaku");
        poster.nameText = FindTmp(canvasT, "NameBand/Name");
        poster.portraitImage = canvasT.Find("Portrait").GetComponent<RawImage>();
        poster.portraitRect = (RectTransform)canvasT.Find("Portrait");

        poster.introCutin = introCutin;
        poster.introTexture = introRt;
        // 導入の尺はカットインの実尺が正。ここで固定値を持つと演出を直したときにズレる。
        poster.introSeconds = introCutin != null ? introCutin.GetIntroSeconds() : 0f;

        poster.displaySeconds = DisplaySeconds;
        poster.minIntervalSeconds = MinIntervalSeconds;
        poster.renderBurstSeconds = RenderBurstSeconds;
        poster.rerenderIntervalSeconds = RerenderIntervalSeconds;
        poster.portraitAnchorY = 0f;
        poster.portraitTopMargin = PortraitTopMargin;

        UdonSharpEditorUtility.CopyProxyToUdon(poster);
        EditorUtility.SetDirty(poster);
        EditorSceneManager.MarkSceneDirty(root.scene);

        Debug.Log("[BuildYakumanPoster] 完了：" + RootName + " を用意しました（" +
                  PosterW + "x" + PosterH + " / レイヤー " + LayerName + "）。\n" +
                  "続けて Tools > Kemono Mahjong > Wire Yakuman Poster を実行してください。");
    }

    // ============================================================
    // Wire
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Wire Yakuman Poster")]
    public static void Wire()
    {
        GameObject root = FindSceneObject(RootName);
        var poster = root != null ? root.GetComponent<YakumanPosterManager>() : null;
        if (poster == null)
        {
            Debug.LogError("[WireYakumanPoster] " + RootName + " がありません。先に Build Yakuman Poster を実行してください。");
            return;
        }

        // --- 掲示先（広告パネル）---
        var adPanels = UnityEngine.Object.FindObjectOfType<AdPanelManager>(true);
        if (adPanels == null)
        {
            Debug.LogError("[WireYakumanPoster] AdPanelManager が見つかりません。先に Build Ad Panels を実行してください。");
            return;
        }
        poster.adPanels = adPanels;

        // --- キャラ立ち絵（PresentationScreen と同じ配列を使い回す）---
        int charaCount = 0;
        var presentation = UnityEngine.Object.FindObjectOfType<MahjongPresentationScreen>(true);
        if (presentation != null)
        {
            poster.charaTextures = presentation.charaMascotTextures;
            poster.defaultCharaTexture = presentation.defaultMascotTexture;
            charaCount = presentation.charaMascotTextures != null ? presentation.charaMascotTextures.Length : 0;

            // 立ち絵は元画像 700x1080 が POT へ丸められて 512x1024 で入っている（npotScale=ToNearest）。
            // テクスチャの実寸から比を求めると縦に伸びるので、元画像の比をここで焼き込む。
            // 圧縮とmipmapを保つためテクスチャ側は丸めたままにする（NPOTだと非圧縮に落ちる）。
            poster.charaAspects = new float[charaCount];
            for (int i = 0; i < charaCount; ++i)
            {
                poster.charaAspects[i] = SourceAspect(presentation.charaMascotTextures[i]);
            }
            poster.defaultCharaAspect = SourceAspect(presentation.defaultMascotTexture);
        }
        else
        {
            Debug.LogWarning("[WireYakumanPoster] MahjongPresentationScreen が見つかりません。キャラ立ち絵は未割当のままです。");
        }

        // --- 自画像キャラ ---
        poster.selfieStore = UnityEngine.Object.FindObjectOfType<SelfiePortraitStore>(true);
        if (poster.selfieStore == null)
        {
            Debug.LogWarning("[WireYakumanPoster] SelfiePortraitStore が見つかりません。自画像キャラは既定の立ち絵に落ちます。");
        }

        // --- 言語（切替時に組み直せるよう extraTargets へも登録）---
        var language = UnityEngine.Object.FindObjectOfType<KemonoLanguage>(true);
        poster.languageManager = language;
        if (language != null)
        {
            if (AddExtraTarget(language, poster))
            {
                UdonSharpEditorUtility.CopyProxyToUdon(language);
                EditorUtility.SetDirty(language);
            }
        }
        else
        {
            Debug.LogWarning("[WireYakumanPoster] KemonoLanguage が見つかりません。ポスターは日本語で出ます。");
        }

        UdonSharpEditorUtility.CopyProxyToUdon(poster);
        EditorUtility.SetDirty(poster);

        // --- 卓（Mahjong）→ ポスターの逆参照 ---
        var tables = UnityEngine.Object.FindObjectsOfType<Mahjong>(true);
        int wired = 0;
        for (int i = 0; i < tables.Length; ++i)
        {
            if (tables[i] == null) { continue; }
            tables[i].yakumanPoster = poster;
            UdonSharpEditorUtility.CopyProxyToUdon(tables[i]);
            EditorUtility.SetDirty(tables[i]);
            ++wired;
        }

        // --- ポスター → 卓（受信側の資格確認に使う）---
        var tableList = new System.Collections.Generic.List<Mahjong>();
        for (int i = 0; i < tables.Length; ++i)
        {
            if (tables[i] != null) { tableList.Add(tables[i]); }
        }
        poster.tables = tableList.ToArray();
        UdonSharpEditorUtility.CopyProxyToUdon(poster);
        EditorUtility.SetDirty(poster);

        EditorSceneManager.MarkSceneDirty(root.scene);
        string aspects = "";
        for (int i = 0; poster.charaAspects != null && i < poster.charaAspects.Length; ++i)
        {
            aspects += (i > 0 ? "/" : "") + poster.charaAspects[i].ToString("F3");
        }
        Debug.Log("[WireYakumanPoster] 完了：卓 " + wired + " 卓 / 広告パネル " +
                  CountPanels(adPanels) + "枚 / キャラ絵 " + charaCount + "件（比 " + aspects + "）" +
                  (poster.selfieStore != null ? " / 自画像OK" : "") +
                  (language != null ? " / 言語OK" : ""));
    }

    // ============================================================
    // Preview（エディタで絵を確かめる）
    //
    // 実行時と同じ組み方で仮のポスターを1枚撮って RenderTexture に残す。
    // Project ウィンドウで YakumanPoster.renderTexture を選べばそのまま見える。
    // レイアウト（文字の大きさ・帯の位置）を詰めるときはこれを回す。
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Preview Yakuman Poster")]
    public static void Preview()
    {
        GameObject root = FindSceneObject(RootName);
        var poster = root != null ? root.GetComponent<YakumanPosterManager>() : null;
        if (poster == null || poster.posterCamera == null || poster.posterTexture == null)
        {
            Debug.LogError("[PreviewYakumanPoster] 先に Build Yakuman Poster を実行してください。");
            return;
        }

        if (poster.titleText != null) { poster.titleText.text = "役満"; }
        if (poster.yakuText != null) { poster.yakuText.text = "国\n士\n無\n双\n十\n三\n面\n待\nち"; }
        if (poster.nameText != null) { poster.nameText.text = "プレイヤー名"; }

        Texture tex = poster.defaultCharaTexture;
        if (poster.charaTextures != null && poster.charaTextures.Length > 0 && poster.charaTextures[0] != null)
        {
            tex = poster.charaTextures[0];
        }
        if (tex != null && poster.portraitImage != null && poster.portraitRect != null && poster.posterRect != null)
        {
            poster.portraitImage.texture = tex;
            poster.portraitImage.enabled = true;

            // 実行時と同じく元画像の比で cover する（テクスチャの実寸はPOTへ丸められている）。
            float aspect = SourceAspect(tex);
            if (aspect <= 0f) { aspect = (float)tex.width / tex.height; }

            float pw = poster.posterRect.rect.width;
            float ph = poster.posterRect.rect.height - poster.portraitTopMargin;
            float w = pw;
            float h = pw / aspect;
            if (h < ph) { h = ph; w = ph * aspect; }

            var pr = poster.portraitRect;
            pr.anchorMin = new Vector2(0.5f, 1f);
            pr.anchorMax = new Vector2(0.5f, 1f);
            pr.pivot = new Vector2(0.5f, 1f);
            pr.sizeDelta = new Vector2(w, h);
            pr.anchoredPosition = new Vector2(0f, -poster.portraitTopMargin + (h - ph) * poster.portraitAnchorY);
        }

        Canvas.ForceUpdateCanvases();
        poster.posterCamera.Render();
        Debug.Log("[PreviewYakumanPoster] " + RtPath + " に1枚撮りました（Projectウィンドウで選ぶと見えます）。");
    }

    // ------------------------------------------------------------
    // 導入の1コマを撮る（構図合わせ用）。t は 0〜1＝クリップ内の位置。
    // 実行時と同じ手順（Play → クリップを時刻で固定 → ステージカメラで撮る）を踏む。
    // ------------------------------------------------------------
    [MenuItem("Tools/Kemono Mahjong/Preview Yakuman Intro")]
    public static void PreviewIntro()
    {
        PreviewIntroFrame(0.62f);
    }

    public static void PreviewIntroFrame(float t)
    {
        GameObject root = FindSceneObject(RootName);
        var poster = root != null ? root.GetComponent<YakumanPosterManager>() : null;
        var cutin = poster != null ? poster.introCutin : null;
        if (cutin == null || cutin.stageCamera == null || cutin.introAnimator == null)
        {
            Debug.LogError("[PreviewYakumanIntro] 先に Build Yakuman Poster を実行してください。");
            return;
        }

        // 仮の手（万子1・筒子1・索子1・東・萬5・筒5）と和了牌（中）で飛ばす。
        int[] hand = new int[] { 0, 9, 18, 27, 4, 13 };
        cutin.SetWinnerPlayerId(0);
        cutin.SetHandData(hand, hand.Length, 33, new int[0], new bool[0], new bool[0], 0, 0);
        cutin.Play(YakumanPosterManager.YAKU_KOKUSHIMUSO13, "国士無双十三面待ち", "役満");

        // 実行時と同じ Apply(elapsed) を通す（クリップのサンプル・立ち絵・花びら・
        // 光の粒まで一度に入る）。private なのでエディタからはリフレクションで叩く。
        float elapsed = Mathf.Clamp01(t) * cutin.GetIntroSeconds();
        var apply = typeof(MahjongYakumanCutin).GetMethod("Apply",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (apply == null)
        {
            Debug.LogError("[PreviewYakumanIntro] Apply が見つかりません（実装が変わった？）。");
            return;
        }
        apply.Invoke(cutin, new object[] { elapsed });

        // LateUpdate 相当（和了牌の大きさ戻し）。実行時は毎フレーム走る。
        if (cutin.standaloneStage && cutin.winningTileRect != null)
        {
            cutin.winningTileRect.sizeDelta = cutin.imageWinTileSize;
        }

        Canvas.ForceUpdateCanvases();
        cutin.stageCamera.Render();

        // 撮り終えたらリグを畳む（カメラを回したままシーンを保存しない）。
        // RenderTexture の中身は残るので、撮った絵はそのまま見られる。
        cutin.HideImmediate();
        cutin.stageCamera.enabled = false;

        Debug.Log("[PreviewYakumanIntro] t=" + t.ToString("F2") + "（" + elapsed.ToString("F2") +
                  "秒）を " + IntroRtPath + " へ撮りました。");
    }

    // ============================================================
    // Validate
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Validate Yakuman Poster")]
    public static void Validate()
    {
        GameObject root = FindSceneObject(RootName);
        if (root == null)
        {
            Debug.LogError("[ValidateYakumanPoster] " + RootName + " がシーンにありません。");
            return;
        }

        var poster = root.GetComponent<YakumanPosterManager>();
        if (poster == null)
        {
            Debug.LogError("[ValidateYakumanPoster] YakumanPosterManager がありません。");
            return;
        }

        var missing = new List<string>();
        if (poster.posterCamera == null) { missing.Add("posterCamera"); }
        if (poster.posterTexture == null) { missing.Add("posterTexture"); }
        if (poster.posterRect == null) { missing.Add("posterRect"); }
        if (poster.adPanels == null) { missing.Add("adPanels"); }
        if (poster.titleText == null) { missing.Add("titleText"); }
        if (poster.yakuText == null) { missing.Add("yakuText"); }
        if (poster.yakuRect == null) { missing.Add("yakuRect"); }
        if (poster.nameText == null) { missing.Add("nameText"); }
        if (poster.portraitImage == null) { missing.Add("portraitImage"); }
        if (poster.portraitRect == null) { missing.Add("portraitRect"); }
        if (poster.introCutin == null) { missing.Add("introCutin（導入が流れない）"); }
        if (poster.introTexture == null) { missing.Add("introTexture"); }
        if (poster.introCutin != null)
        {
            if (poster.introCutin.useRealIntroTiles)
            {
                missing.Add("introCutin.useRealIntroTiles が ON（スタジオには実物の牌が無い）");
            }
            if (poster.introCutin.billboardRoot != null)
            {
                missing.Add("introCutin.billboardRoot が残っている（カメラに正対しなくなる）");
            }
            if (!poster.introCutin.standaloneStage)
            {
                missing.Add("introCutin.standaloneStage が OFF（和了牌も立ち絵も出ない）");
            }
            // 卓のカットインが単体版になっていないか（実物の牌と二重に出る）。
            var tableCutins = UnityEngine.Object.FindObjectsOfType<MahjongYakumanCutin>(true);
            for (int i = 0; i < tableCutins.Length; ++i)
            {
                var c = tableCutins[i];
                if (c == null || c == poster.introCutin) { continue; }
                if (c.standaloneStage)
                {
                    missing.Add("卓の " + c.name + " が standaloneStage ON になっている");
                }
            }
            var sc = poster.introCutin.stageCamera;
            if (sc == null) { missing.Add("introCutin.stageCamera"); }
            else
            {
                if (sc.targetTexture != poster.introTexture)
                {
                    missing.Add("introCutin.stageCamera.targetTexture が introTexture でない");
                }
                if (sc.enabled) { missing.Add("introCutin.stageCamera が有効のまま"); }
            }
            if (poster.introSeconds <= 0.01f) { missing.Add("introSeconds が0（導入が飛ぶ）"); }
        }
        if (poster.charaTextures != null && poster.charaTextures.Length > 0)
        {
            int n = poster.charaAspects != null ? poster.charaAspects.Length : 0;
            if (n != poster.charaTextures.Length)
            {
                missing.Add("charaAspects の数が charaTextures と違う（" + n + "/" + poster.charaTextures.Length + "）");
            }
            else
            {
                for (int i = 0; i < n; ++i)
                {
                    if (poster.charaTextures[i] != null && poster.charaAspects[i] <= 0f)
                    {
                        missing.Add("charaAspects[" + i + "] が未設定（立ち絵が縦に伸びる）");
                    }
                }
            }
        }
        if (poster.selfieStore == null) { missing.Add("selfieStore（自画像が出ない）"); }
        if (poster.languageManager == null) { missing.Add("languageManager（日本語固定になる）"); }

        if (poster.posterCamera != null)
        {
            if (poster.posterCamera.enabled)
            {
                missing.Add("posterCamera が有効のまま（掲示していない間は止めておくこと）");
            }
            if (poster.posterCamera.targetTexture != poster.posterTexture)
            {
                missing.Add("posterCamera.targetTexture が posterTexture と違う");
            }
            int layer = LayerMask.NameToLayer(LayerName);
            if (layer >= 0 && poster.posterCamera.cullingMask != (1 << layer))
            {
                missing.Add("posterCamera.cullingMask が " + LayerName + " 単独でない");
            }
        }

        // 卓側の逆参照
        var tables = UnityEngine.Object.FindObjectsOfType<Mahjong>(true);
        int linked = 0;
        for (int i = 0; i < tables.Length; ++i)
        {
            if (tables[i] != null && tables[i].yakumanPoster == poster) { ++linked; }
        }
        if (linked != tables.Length)
        {
            missing.Add("卓の yakumanPoster が " + linked + "/" + tables.Length + " しか埋まっていない");
        }
        int posterTables = 0;
        for (int i = 0; poster.tables != null && i < poster.tables.Length; ++i)
        {
            if (poster.tables[i] != null) { ++posterTables; }
        }
        if (posterTables != tables.Length)
        {
            missing.Add("poster.tables が " + posterTables + "/" + tables.Length + "（申告を受け付けない卓がある）");
        }

        if (missing.Count > 0)
        {
            Debug.LogError("[ValidateYakumanPoster] 不足 " + missing.Count + "件：\n  " + string.Join("\n  ", missing.ToArray()));
            return;
        }

        Debug.Log("[ValidateYakumanPoster] OK（参照欠け0 / 卓 " + linked + "卓 / パネル " +
                  CountPanels(poster.adPanels) + "枚 / 導入 " + poster.introSeconds.ToString("F2") +
                  "秒 → 掲示 " + poster.displaySeconds + "秒）");
    }

    // ============================================================
    // 生成の中身
    // ============================================================

    private static Transform BuildCanvas(Transform root)
    {
        Transform t = root.Find("Poster");
        if (t == null)
        {
            var go = new GameObject("Poster", typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(go, "Build Yakuman Poster");
            go.transform.SetParent(root, false);
            t = go.transform;
        }

        var rt = (RectTransform)t;
        rt.localPosition = Vector3.zero;
        rt.localRotation = Quaternion.identity;
        rt.localScale = new Vector3(CanvasScale, CanvasScale, CanvasScale);
        rt.sizeDelta = new Vector2(PosterW, PosterH);
        rt.pivot = new Vector2(0.5f, 0.5f);

        var canvas = t.GetComponent<Canvas>();
        if (canvas == null) { canvas = Undo.AddComponent<Canvas>(t.gameObject); }
        canvas.renderMode = RenderMode.WorldSpace;

        var scaler = t.GetComponent<CanvasScaler>();
        if (scaler == null) { scaler = Undo.AddComponent<CanvasScaler>(t.gameObject); }
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.dynamicPixelsPerUnit = 10f;

        // 触られる面ではないので GraphicRaycaster / VRCUiShape は付けない
        // （付けるとレーザーが地下のポスターを拾い得る）。

        // 下地の黒。カメラのクリア色と同じだが、絵として明示しておく。
        Transform bg = Child(t, "BG");
        Stretch(bg);
        var bgImage = EnsureImage(bg, Color.black);
        bgImage.raycastTarget = false;

        // キャラ絵。大きさは実行時に写真の縦横比から決めるのでここでは仮。
        Transform portrait = Child(t, "Portrait");
        var portraitRect = (RectTransform)portrait;
        portraitRect.anchorMin = new Vector2(0.5f, 1f);
        portraitRect.anchorMax = new Vector2(0.5f, 1f);
        portraitRect.pivot = new Vector2(0.5f, 1f);
        portraitRect.sizeDelta = new Vector2(PosterW, PosterH);
        portraitRect.anchoredPosition = Vector2.zero;
        var raw = portrait.GetComponent<RawImage>();
        if (raw == null) { raw = Undo.AddComponent<RawImage>(portrait.gameObject); }
        raw.color = Color.white;
        raw.raycastTarget = false;
        raw.enabled = false;   // 実行時に絵が入ってから出す（真っ白の板を出さない）

        // 称号（上）
        var title = Text(t, "Title", "役満", TitlePos, TitleSize, Color.white,
            TextAlignmentOptions.Center, TitleFontMin, TitleFontMax);
        title.characterSpacing = TitleSpacing;

        // 役名（右・縦組み）
        var yaku = Text(t, "Yaku", "国\n士\n無\n双\n十\n三\n面\n待\nち", YakuPos, YakuSize, Color.white,
            TextAlignmentOptions.Top, YakuFontMin, YakuFontMax);
        yaku.lineSpacing = YakuLineSpacing;

        // プレイヤー名（下の白帯）
        Transform band = Child(t, "NameBand");
        SetTopRect(band, BandPos, BandSize);
        var bandImage = EnsureImage(band, Color.white);
        bandImage.raycastTarget = false;
        var name = Text(band, "Name", "プレイヤー名", new Vector2(10f, -6f),
            new Vector2(BandSize.x - 20f, BandSize.y - 12f), Color.black,
            TextAlignmentOptions.Center, NameFontMin, NameFontMax);
        name.enableWordWrapping = false;
        // 長い名前は自動縮小で入るが、限界を超えたら末尾を省略する（帯からはみ出させない）。
        name.overflowMode = TextOverflowModes.Ellipsis;

        // 描き順（後の兄弟が手前）
        bg.SetSiblingIndex(0);
        portrait.SetSiblingIndex(1);
        title.transform.SetSiblingIndex(2);
        yaku.transform.SetSiblingIndex(3);
        band.SetSiblingIndex(4);

        return t;
    }

    private static Camera BuildCamera(Transform root, RenderTexture rt, int layer)
    {
        Transform t = root.Find("Camera");
        if (t == null)
        {
            var go = new GameObject("Camera");
            Undo.RegisterCreatedObjectUndo(go, "Build Yakuman Poster");
            go.transform.SetParent(root, false);
            t = go.transform;
        }
        t.localPosition = new Vector3(0f, 0f, -5f);
        t.localRotation = Quaternion.identity;
        t.localScale = Vector3.one;

        var cam = t.GetComponent<Camera>();
        if (cam == null) { cam = Undo.AddComponent<Camera>(t.gameObject); }
        cam.orthographic = true;
        cam.orthographicSize = PosterH * CanvasScale * 0.5f;
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 20f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Color.black;
        cam.cullingMask = 1 << layer;
        cam.targetTexture = rt;
        cam.allowHDR = false;
        cam.allowMSAA = false;
        cam.useOcclusionCulling = false;
        cam.depth = -20f;          // 何かの拍子に有効化されても本編の描画順を乱さない
        cam.enabled = false;       // 撮る瞬間だけ U# が回す
        t.gameObject.tag = "Untagged";

        // AudioListener が付いていると VRChat のビルドで警告になる（付けない・消す）。
        var listener = t.GetComponent<AudioListener>();
        if (listener != null) { UnityEngine.Object.DestroyImmediate(listener); }

        return cam;
    }

    // ------------------------------------------------------------
    // 導入（YakumanIntro）のリグ
    //
    // 卓のカットインをまるごと1本複製してスタジオへ置く。演出の中身（牌・立ち絵・
    // カメラワーク・タイミング）は既存のコードとクリップがそのまま面倒を見るので、
    // こちらは「実物の3D牌を使わない」「誰の方も向かない」「専用のRTへ撮る」の
    // 3点だけを差し替える。
    // ------------------------------------------------------------
    private static MahjongYakumanCutin EnsureIntroRig(GameObject root, RenderTexture introRt)
    {
        Transform found = root.transform.Find(IntroRigName);
        GameObject rig = found != null ? found.gameObject : null;

        if (rig == null)
        {
            MahjongYakumanCutin source = FindSourceCutin(root);
            if (source == null)
            {
                Debug.LogError("[BuildYakumanPoster] 複製元の役満カットインが見つかりません。" +
                               "先に Build Yakuman Cutin を実行してください。");
                return null;
            }

            rig = UnityEngine.Object.Instantiate(source.gameObject);
            rig.name = IntroRigName;
            rig.transform.SetParent(root.transform, false);
            rig.transform.localScale = source.transform.lossyScale;
            Undo.RegisterCreatedObjectUndo(rig, "Build Yakuman Poster");

            if (PrefabUtility.IsPartOfPrefabInstance(rig))
            {
                PrefabUtility.UnpackPrefabInstance(rig, PrefabUnpackMode.Completely,
                    InteractionMode.AutomatedAction);
            }
        }

        rig.transform.localPosition = Vector3.zero;
        rig.transform.localRotation = Quaternion.identity;
        rig.SetActive(true);

        // レイヤーを自分で入れ直す。Build はポスター側のルートを丸ごと塗るので、
        // 2回目以降の実行で導入リグまで塗られると、ステージカメラが
        // 何も撮らなくなる（真っ暗な導入になる）。
        SetLayerRecursively(rig, 0);   // カットインのパネルは Default のまま
        Transform stage = FindStageTransform(rig);
        int stageLayer = LayerMask.NameToLayer(StageLayerName);
        if (stage != null && stageLayer >= 0)
        {
            SetLayerRecursively(stage.gameObject, stageLayer);
        }
        else if (stageLayer < 0)
        {
            Debug.LogError("[BuildYakumanPoster] レイヤー '" + StageLayerName + "' がありません。" +
                           "役満カットインを先に組み込んでください。");
        }

        var cutin = rig.GetComponent<MahjongYakumanCutin>();
        if (cutin == null)
        {
            Debug.LogError("[BuildYakumanPoster] " + IntroRigName + " に MahjongYakumanCutin がありません。");
            return null;
        }

        // 実物の3D牌は卓のもの＝スタジオには無いので、ステージ上のRawImageの牌を使う。
        cutin.useRealIntroTiles = false;

        // ステージだけを撮る単体版。和了牌・立ち絵・花びら・光の粒をステージ側で出す。
        cutin.standaloneStage = true;

        // 立ち絵の帯ワイプはパネル側の演出＝ここでは撮らないので切る（ステージの
        // Mascot が使えるようになる）。
        cutin.mascotWipeEnabled = false;

        // 誰の方も向かない。向くと専用カメラから見た絵が回ってしまう。
        cutin.billboardRoot = null;

        if (cutin.stageCamera != null)
        {
            cutin.stageCamera.targetTexture = introRt;
            cutin.stageCamera.enabled = false;   // 演出中だけカットイン側が回す
            cutin.stageCamera.allowHDR = false;
            cutin.stageCamera.allowMSAA = false;
            cutin.stageCamera.useOcclusionCulling = false;
            cutin.stageCamera.depth = -19f;
            cutin.stageCamera.tag = "Untagged";

            // A4のRTの中央へ16:9で描く（上下は黒帯）。ビューポートを絞ると
            // カメラのアスペクトもここから決まるので、構図は卓のカットインと同じになる。
            float band = Mathf.Clamp01((PosterW / IntroAspect) / PosterH);
            cutin.stageCamera.rect = new Rect(0f, (1f - band) * 0.5f, 1f, band);
        }

        if (cutin.introAnimator != null)
        {
            // 画面外でも評価させる（クリップは時刻指定でサンプルするが、
            // カリングされると Update(0) が効かない個体差を踏まないため）。
            cutin.introAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }

        if (cutin.fxRoot != null) { cutin.fxRoot.SetActive(false); }

        // スタジオは地下だが、SEが鳴くと事故になるので黙らせる。
        var audio = rig.GetComponent<AudioSource>();
        if (audio != null) { audio.volume = 0f; audio.playOnAwake = false; }

        UdonSharpEditorUtility.CopyProxyToUdon(cutin);
        EditorUtility.SetDirty(cutin);
        return cutin;
    }

    // 導入ステージ（Animator が乗っている入れ物）。
    private static Transform FindStageTransform(GameObject rig)
    {
        var cutin = rig.GetComponent<MahjongYakumanCutin>();
        if (cutin != null && cutin.introAnimator != null) { return cutin.introAnimator.transform; }
        if (cutin != null && cutin.stageCamera != null) { return cutin.stageCamera.transform.parent; }
        return null;
    }

    // 複製元のカットイン（卓に付いているもの）。スタジオの複製自身は除く。
    private static MahjongYakumanCutin FindSourceCutin(GameObject root)
    {
        var all = UnityEngine.Object.FindObjectsOfType<MahjongYakumanCutin>(true);
        for (int i = 0; i < all.Length; ++i)
        {
            var c = all[i];
            if (c == null) { continue; }
            if (c.transform.IsChildOf(root.transform)) { continue; }
            if (c.stageCamera == null || c.introAnimator == null) { continue; }
            return c;
        }
        return null;
    }

    private static RenderTexture EnsureIntroRenderTexture()
    {
        return EnsureRenderTextureAt(IntroRtPath, "YakumanPosterIntro");
    }

    private static RenderTexture EnsureRenderTexture()
    {
        return EnsureRenderTextureAt(RtPath, "YakumanPoster");
    }

    // ポスターと導入で同じ寸法（A4比）のRTを2枚使う。1枚を撮り分けると
    // 導入→ポスターの切り替えでクロスフェードできない（パネル側が同じ絵と見なす）。
    private static RenderTexture EnsureRenderTextureAt(string path, string name)
    {
        var rt = AssetDatabase.LoadAssetAtPath<RenderTexture>(path);
        if (rt != null)
        {
            if (rt.width != PosterW || rt.height != PosterH)
            {
                rt.Release();
                rt.width = PosterW;
                rt.height = PosterH;
                EditorUtility.SetDirty(rt);
                AssetDatabase.SaveAssets();
            }
            return rt;
        }

        rt = new RenderTexture(PosterW, PosterH, 16, RenderTextureFormat.Default);
        rt.name = name;
        rt.useMipMap = false;
        rt.autoGenerateMips = false;
        rt.antiAliasing = 1;
        rt.filterMode = FilterMode.Bilinear;
        rt.wrapMode = TextureWrapMode.Clamp;
        AssetDatabase.CreateAsset(rt, path);
        AssetDatabase.SaveAssets();
        Debug.Log("[BuildYakumanPoster] RenderTexture を生成: " + path);
        return AssetDatabase.LoadAssetAtPath<RenderTexture>(path);
    }

    // レイヤーが無ければ空きスロットへ作る。作れなければ -1。
    private static int EnsureLayer()
    {
        int layer = LayerMask.NameToLayer(LayerName);
        if (layer >= 0) { return layer; }

        var tagManager = new SerializedObject(
            AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        var layers = tagManager.FindProperty("layers");
        if (layers == null || !layers.isArray)
        {
            Debug.LogError("[BuildYakumanPoster] TagManager を読めません。レイヤー \"" + LayerName + "\" を手で作ってください。");
            return -1;
        }

        // 0〜22 は VRChat が使う。空きは 24 以降から探す（23 は YakumanStage）。
        for (int i = 24; i < layers.arraySize; ++i)
        {
            var element = layers.GetArrayElementAtIndex(i);
            if (string.IsNullOrEmpty(element.stringValue))
            {
                element.stringValue = LayerName;
                tagManager.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();
                Debug.Log("[BuildYakumanPoster] レイヤー \"" + LayerName + "\" を " + i + " 番に作りました。");
                return i;
            }
        }

        Debug.LogError("[BuildYakumanPoster] 空きレイヤーがありません。\"" + LayerName + "\" を手で作ってください。");
        return -1;
    }

    // ============================================================
    // 小物
    // ============================================================

    private static bool AddExtraTarget(KemonoLanguage language, UdonSharpBehaviour target)
    {
        var list = new List<UdonSharpBehaviour>();
        if (language.extraTargets != null)
        {
            for (int i = 0; i < language.extraTargets.Length; ++i)
            {
                var b = language.extraTargets[i];
                if (b == null) { continue; }
                if (b == target) { return false; }   // 既に入っている
                list.Add(b);
            }
        }
        list.Add(target);
        language.extraTargets = list.ToArray();
        return true;
    }

    // 元画像（インポート前）の縦横比。読めなければ 0（実行時はテクスチャの実寸に落ちる）。
    private static float SourceAspect(Texture tex)
    {
        if (tex == null) { return 0f; }

        string path = AssetDatabase.GetAssetPath(tex);
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer != null)
        {
            // GetWidthAndHeight は非公開だが、元画像の寸法を知る唯一の手段。
            var method = typeof(TextureImporter).GetMethod("GetWidthAndHeight",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (method != null)
            {
                object[] args = new object[] { 0, 0 };
                method.Invoke(importer, args);
                int w = (int)args[0];
                int h = (int)args[1];
                if (w > 0 && h > 0) { return (float)w / h; }
            }
        }

        return tex.height > 0 ? (float)tex.width / tex.height : 0f;
    }

    private static int CountPanels(AdPanelManager manager)
    {
        if (manager == null) { return 0; }
        if (manager.panels != null && manager.panels.Length > 0) { return manager.panels.Length; }
        return manager.GetComponentsInChildren<AdPanel>(true).Length;
    }

    private static TextMeshProUGUI Text(Transform parent, string name, string value,
        Vector2 topLeft, Vector2 size, Color color, TextAlignmentOptions alignment,
        float fontMin, float fontMax)
    {
        Transform t = Child(parent, name);
        SetTopRect(t, topLeft, size);
        var tmp = t.GetComponent<TextMeshProUGUI>();
        if (tmp == null) { tmp = Undo.AddComponent<TextMeshProUGUI>(t.gameObject); }
        tmp.font = font_;
        tmp.text = value;
        tmp.color = color;
        tmp.alignment = alignment;
        tmp.raycastTarget = false;
        tmp.enableWordWrapping = false;
        tmp.overflowMode = TextOverflowModes.Overflow;
        // 文字数の増減（役満／二倍役満、3文字／9文字）を枠に収めるのは自動縮小に任せる。
        tmp.enableAutoSizing = true;
        tmp.fontSizeMin = fontMin;
        tmp.fontSizeMax = fontMax;
        tmp.fontSize = fontMax;
        return tmp;
    }

    private static Transform Child(Transform parent, string name)
    {
        Transform existing = parent.Find(name);
        if (existing != null) { return existing; }
        var go = new GameObject(name, typeof(RectTransform));
        Undo.RegisterCreatedObjectUndo(go, "Build Yakuman Poster");
        go.transform.SetParent(parent, false);
        return go.transform;
    }

    private static void SetTopRect(Transform t, Vector2 topLeft, Vector2 size)
    {
        var rt = (RectTransform)t;
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = topLeft;
        rt.sizeDelta = size;
        rt.localRotation = Quaternion.identity;
        rt.localScale = Vector3.one;
        rt.localPosition = new Vector3(rt.localPosition.x, rt.localPosition.y, 0f);
    }

    private static void Stretch(Transform t)
    {
        var rt = (RectTransform)t;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        rt.localRotation = Quaternion.identity;
        rt.localScale = Vector3.one;
        rt.localPosition = new Vector3(rt.localPosition.x, rt.localPosition.y, 0f);
    }

    private static Image EnsureImage(Transform t, Color color)
    {
        var image = t.GetComponent<Image>();
        if (image == null) { image = Undo.AddComponent<Image>(t.gameObject); }
        image.color = color;
        return image;
    }

    private static TextMeshProUGUI FindTmp(Transform root, string path)
    {
        Transform t = root.Find(path);
        if (t == null)
        {
            Debug.LogError("[BuildYakumanPoster] UIが見つかりません: " + path);
            return null;
        }
        return t.GetComponent<TextMeshProUGUI>();
    }

    private static GameObject FindSceneObject(string name)
    {
        var transforms = UnityEngine.Object.FindObjectsOfType<Transform>(true);
        for (int i = 0; i < transforms.Length; ++i)
        {
            var candidate = transforms[i];
            if (candidate != null && candidate.name == name && candidate.gameObject.scene.IsValid()
                && candidate.parent == null)
            {
                return candidate.gameObject;
            }
        }
        return null;
    }

    private static bool EnsureProgramAsset()
    {
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(ScriptPath);
        if (script == null)
        {
            Debug.LogError("[BuildYakumanPoster] スクリプトが見つかりません: " + ScriptPath);
            return false;
        }
        Type type = script.GetClass();
        if (type != null && UdonSharpProgramAsset.GetProgramAssetForClass(type) != null) { return false; }
        if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(ProgramAssetPath) != null) { return false; }

        var asset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
        asset.sourceCsScript = script;
        AssetDatabase.CreateAsset(asset, ProgramAssetPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[BuildYakumanPoster] U# Program Assetを生成: " + ProgramAssetPath);
        return true;
    }

    private static UdonSharpBehaviour EnsureUSharpComponent(GameObject go, Type type)
    {
        var proxy = (UdonSharpBehaviour)go.GetComponent(type);
        if (proxy != null && UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy) != null) { return proxy; }
        if (proxy != null)
        {
            UnityEngine.Object.DestroyImmediate(proxy);
            var oldUdon = go.GetComponents<VRC.Udon.UdonBehaviour>();
            for (int i = 0; i < oldUdon.Length; ++i)
            {
                if (oldUdon[i].programSource == null) { UnityEngine.Object.DestroyImmediate(oldUdon[i]); }
            }
        }

        var added = (UdonSharpBehaviour)UdonSharpUndo.AddComponent(go, type);
        if (added == null || UdonSharpEditorUtility.GetBackingUdonBehaviour(added) == null)
        {
            Debug.LogError("[BuildYakumanPoster] YakumanPosterManager を追加できません。U#コンパイル後に再実行してください。");
            if (added != null) { UnityEngine.Object.DestroyImmediate(added); }
            return null;
        }
        return added;
    }

    private static void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform) { SetLayerRecursively(child.gameObject, layer); }
    }
}
#endif
