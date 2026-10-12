#if UNITY_EDITOR
// 卓エッジ点数板（自動卓スタイル・俯瞰マップ型）の骨格生成と配線（点数板_組み込み手順.md 準拠）。
//   Tools > Kemono Mahjong > Build Score Boards … 全卓に ScoreBoard/BoardRoot/Panel0..3 を冪等生成
//                                                 （旧4面センター型の Face0..3 は撤去）。
//   Tools > Kemono Mahjong > Wire Score Boards  … U#コンポーネント追加＋参照割り当て＋卓への逆参照。
// どちらも2回実行しても安全（冪等）。パネルの位置・傾き・サイズを変えたい場合は下の定数を
// 変えて Build を再実行する（手でシーンをいじった調整は Build 再実行で上書きされる）。
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using SuzuFactory.AozoraMahjong;
using VRC.Udon;

public static class BuildScoreBoard
{
    const string RootName = "ScoreBoard";
    const string FontPath = "Assets/Mahjong/Fonts/NotoSansJP-Bold.ttf";
    const string ProgramAssetDir = "Assets/Mahjong/scene_UdonProgramSources";

    // 点数の7セグLED表示に使うフォント（DSEG系・数字専用・OFL）。ここに .ttf を置く。
    // 見つからない時は NotoSans で暫定生成（ゴースト背景なし）＝配置して再Buildで LED化。
    const string LedFontPath = "Assets/Mahjong/Fonts/DSEG7Classic-Bold.ttf";
    const bool SevenSegStyle = true;
    // 点数セルの共通サイズ/フォント（ダイヤ4隅＋ゴースト背景で共用）。
    // セル幅は最長ケース（マイナス6桁 "-12300"＝72px @size15）に合わせてある。
    const int ScoreFontSize = 15;
    static readonly Vector2 ScoreCell = new Vector2(84f, 20f);
    // LED配色（基本＝赤・ゴースト＝暗赤）。立直は TableScoreBoard 側のリッチ色が上書き。
    static readonly Color LedColor = new Color(0.92f, 0.11f, 0.11f);
    static readonly Color GhostColor = new Color(0.26f, 0.03f, 0.03f);

    // 残り牌数（WallText）を生成・配線するか。false にして Build→Wire を再実行すると
    // 表示ごと消え、ツモ毎の再描画も発生しなくなる（TableScoreBoard 側仕様）。
    const bool UseWallText = true;
    // 名前表示（Slotk_Name）は生成しない（任意機能・モック準拠のミニマル構成）。
    // 使う場合はパネルに Text を足して nameTexts（16要素・面*4+スロット）を手で配線する。

    // 卓の縁：天板半幅 0.43m（卓面=卓ルート原点）。パネル中心を縁の外側へ出し、
    // 内側端が縁の下に潜る位置に置く。
    //
    // 2026-09-09：シーンの卓だけ点数板が下へずれていた（Prefab は正常）。原因は
    // 「Prefab 側を手で調整したのに、この定数が古いままでシーンを Build し直した」こと。
    // Build はシーンの卓にしか走らない（FindObjectsOfType<Mahjong>）ので、Prefab の
    // 手調整はシーンへ伝わらず、逆に Build がシーンを旧値へ引き戻していた。
    // 下の2つは Prefab の実測値（4パネルの平均：半径 0.4296〜0.4302 / 高さ -0.0114〜-0.0119）。
    // **見た目を直すときは Prefab を手で動かさず、必ずここを直して Build し直すこと。**
    const float PanelRadius = 0.43f;
    // パネル中心の高さ（卓面から下げる量）。内側端が天板の直下に来る。
    // 旧値 -0.06 は天板から 4.8cm 低く、実機で「点数板だけ下にずれている」ように見えた。
    const float PanelDropY = -0.0117f;
    // 水平からの下向き傾斜（外側端が下がる）。手順書の目安 15〜30°。
    const float TiltDeg = 20f;
    // 実寸 0.4m×0.065m（400×65px・Scale 0.001）。中身は全てこの矩形に収める
    // （はみ出させない。各Textの実寸は Unity で計測済み＝下のレイアウト表を参照）。
    static readonly Vector2 PanelSize = new Vector2(400f, 65f);
    const float PanelScale = 0.001f;
    const int WalkthroughLayer = 17; // コライダー誤当たり防止（BOOTH版と同じ知見）

    // ============================================================
    // Build：骨格生成
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Build Score Boards")]
    public static void Build()
    {
        var font = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
        if (font == null)
        {
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            Debug.LogWarning("[BuildScoreBoard] 日本語フォントが見つかりません（" + FontPath + "）。LegacyRuntime で生成します。");
        }

        // 7セグLED用フォント（任意）。無ければ点数は NotoSans 暫定＝ゴースト背景なし。
        var ledFont = AssetDatabase.LoadAssetAtPath<Font>(LedFontPath);
        bool hasLed = SevenSegStyle && ledFont != null;
        if (SevenSegStyle && ledFont == null)
        {
            Debug.LogWarning("[BuildScoreBoard] 7セグフォントが見つかりません（" + LedFontPath + "）。点数は NotoSans で暫定生成します（ゴースト背景なし）。DSEG系の .ttf を配置して再Buildすると LED表示になります。");
        }

        var tables = Object.FindObjectsOfType<Mahjong>(true);
        System.Array.Sort(tables, (a, b) => string.CompareOrdinal(GetPath(a.transform), GetPath(b.transform)));
        if (tables.Length == 0)
        {
            Debug.LogError("[BuildScoreBoard] シーンに Mahjong（卓）が見つかりません。");
            return;
        }

        foreach (var table in tables)
        {
            BuildOne(table.transform, font, ledFont, hasLed);
        }

        EditorSceneManager.MarkSceneDirty(tables[0].gameObject.scene);
        Debug.Log("[BuildScoreBoard] 完了：" + tables.Length + " 卓に点数板（エッジパネル型）を生成/更新。次に Wire Score Boards を実行してください。");
    }

    static void BuildOne(Transform tableRoot, Font font, Font ledFont, bool hasLed)
    {
        // ScoreBoard（スクリプト本体の載る親）は常時アクティブを厳守。
        // 表示ON/OFFは子の BoardRoot でのみ行う（U#の他Behaviour呼び出しは
        // 非アクティブなObjectに届かないため）。
        var board = EnsureChild(tableRoot, RootName);
        board.transform.localPosition = Vector3.zero;
        board.transform.localRotation = Quaternion.identity;
        board.SetActive(true);

        var boardRoot = EnsureChild(board.transform, "BoardRoot");
        boardRoot.transform.localPosition = Vector3.zero;
        boardRoot.transform.localRotation = Quaternion.identity;

        // 旧4面センター型（Face0..3）や名残の子を撤去。
        for (int i = boardRoot.transform.childCount - 1; i >= 0; --i)
        {
            var c = boardRoot.transform.GetChild(i);
            if (!c.name.StartsWith("Panel"))
            {
                Undo.DestroyObjectImmediate(c.gameObject);
            }
        }

        for (int i = 0; i < 4; ++i)
        {
            BuildPanel(boardRoot.transform, i, font, ledFont, hasLed);
        }

        SetLayerRecursively(board, WalkthroughLayer);
    }

    // Panel i は席 i の縁の下に置く。席の物理方向は Mahjong.cs の席回転
    // （Quaternion.Euler(0, seat * -90, 0)・席0=+X）に合わせる。
    // 水平（面が真上向き・Canvas は -Z 側=上から読める）から外側へ TiltDeg
    // 倒す＝Euler X = 90-TiltDeg。
    // 内部はダイヤ（俯瞰マップ）配置（600×100）：上=対面/下=自分/右=下家/左=上家の
    // 4スコアを7セグLED風で置き、中央に局ラベル、四隅に本場/供託/残牌。点数は
    // ゴースト背景（全セグ点灯の"88888"）＋実値の2層。順位/トップ差サブは省略。
    static void BuildPanel(Transform boardRoot, int index, Font font, Font ledFont, bool hasLed)
    {
        var panel = EnsureChild(boardRoot, "Panel" + index);

        var seatDir = Quaternion.Euler(0f, index * -90f, 0f) * Vector3.right;
        panel.transform.localPosition = seatDir * PanelRadius + new Vector3(0f, PanelDropY, 0f);
        panel.transform.localRotation = Quaternion.Euler(90f - TiltDeg, 270f - 90f * index, 0f);
        panel.transform.localScale = new Vector3(PanelScale, PanelScale, PanelScale);

        var canvas = panel.GetComponent<Canvas>();
        if (canvas == null) { canvas = panel.AddComponent<Canvas>(); }
        canvas.renderMode = RenderMode.WorldSpace;

        var scaler = panel.GetComponent<CanvasScaler>();
        if (scaler == null) { scaler = panel.AddComponent<CanvasScaler>(); }
        scaler.dynamicPixelsPerUnit = 2f;

        // GraphicRaycaster は付けない（操作しない表示専用・厳守）。
        var raycaster = panel.GetComponent<GraphicRaycaster>();
        if (raycaster != null) { Object.DestroyImmediate(raycaster); }

        var rt = panel.GetComponent<RectTransform>();
        rt.sizeDelta = PanelSize;

        // 背景（LED表示の黒地。4面で同一設定＝バッチ）。
        var bg = EnsureUiChild(panel.transform, "Background");
        var bgImg = bg.GetComponent<Image>();
        if (bgImg == null) { bgImg = bg.AddComponent<Image>(); }
        // 注意：(0.16,0.16,0.16,0.9) はエディタのカメラキャプチャで描画されない
        // 事象を確認（原因不明・3回再現）。黒αは正常。色を変えるときは要目視確認。
        bgImg.color = new Color(0.03f, 0.03f, 0.03f, 0.9f);
        bgImg.raycastTarget = false;
        StretchFull(bg.GetComponent<RectTransform>());

        // ---- 4スコア：ダイヤ配置（0自分=下/1下家=右/2対面=上/3上家=左） ----
        // スクリプト側が「面faceのSlot k＝席(face+k)%4」で割り付けるので全面同一配置。
        // 席方向の物理関係（下家=右隣…）と一致し、自席が常に手前（下）に来る。
        // 400×65（x:-200..200 / y:-32.5..32.5）に3段で収める。段は隣接させ、重なりゼロ。
        //   上段 y=+20（h20 → +10..+30）：対面スコア／本場・供託
        //   中段 y=  0（h20 → -10..+10）：上家スコア・局ラベル・下家スコア
        //   下段 y=-20（h20 → -30..-10）：自分スコア／残牌
        Vector2[] slotPos =
        {
            new Vector2(0f, -20f),   // slot0 自分（手前＝下）  x:-42..42
            new Vector2(136f, 0f),   // slot1 下家（右）        x: 94..178
            new Vector2(0f, 20f),    // slot2 対面（上）        x:-42..42
            new Vector2(-136f, 0f),  // slot3 上家（左）        x:-178..-94
        };
        for (int slot = 0; slot < 4; ++slot)
        {
            BuildSlot(panel.transform, slot, ledFont, font, hasLed, slotPos[slot]);
        }

        // ---- 中央：局ラベル（日本語＝NotoSans）。"東1局"@12=31px / "East 1"相当も収まる ----
        var round = EnsureText(panel.transform, "RoundText", font, 12, new Vector2(0f, 0f), new Vector2(64f, 18f), "東1局");
        round.color = new Color(0.86f, 0.84f, 0.78f);

        // ---- 端：本場（左上）・供託（右上）・残牌（右下）。日本語＝NotoSans ----
        // 幅72は英語の最長ケース "Honba x0"@9=42px に対して余裕あり。
        var honba = EnsureText(panel.transform, "HonbaText", font, 9, new Vector2(-158f, 20f), new Vector2(72f, 14f), "0本場");
        for (int d = 0; d < 5; ++d)
        {
            var go = EnsureUiChild(panel.transform, "Dora" + d);
            var img = go.GetComponent<RawImage>();
            if (img == null) { img = go.AddComponent<RawImage>(); }
            img.texture = Texture2D.whiteTexture;
            img.color = new Color(0.93f, 0.55f, 0.12f, 1f);
            img.raycastTarget = false;
            var drt = go.GetComponent<RectTransform>();
            drt.anchorMin = drt.anchorMax = new Vector2(0.5f, 0.5f);
            drt.anchoredPosition = new Vector2(-128f + d * 14f, 20f);
            drt.sizeDelta = new Vector2(12f, 18f);
        }
        honba.color = new Color(0.62f, 0.62f, 0.62f);
        // 供託は（立直棒の絵）×N。0本のときは絵を隠して「×0」だけを中央に置く。
        // ここの座標は TableScoreBoard.KYOTAKU_TEXT_X_ALONE / _WITH_STICK と対。
        // 片方だけ動かすと絵と文字が重なるので、必ず両方そろえて直すこと。
        // 幅36：EnsureText は horizontalOverflow=Overflow なので「×10」でも切れない。
        var kyotaku = EnsureText(panel.transform, "KyotakuText", font, 9, new Vector2(158f, 20f), new Vector2(36f, 14f), "×0");
        kyotaku.color = new Color(0.62f, 0.62f, 0.62f);
        EnsureKyotakuStick(panel.transform);
        if (UseWallText)
        {
            var wall = EnsureText(panel.transform, "WallText", font, 9, new Vector2(158f, -20f), new Vector2(72f, 14f), "残70");
            wall.color = new Color(0.62f, 0.62f, 0.62f);
        }
        else
        {
            var wall = panel.transform.Find("WallText");
            if (wall != null) { Undo.DestroyObjectImmediate(wall.gameObject); }
        }

        // ---- 左下：局数ルール（半荘戦/東風戦/一局戦）。右下の残牌と対になる位置 ----
        // 幅72は英語の最長ケース "Single Hand"@9=52px に対して余裕あり（Overflow なので
        // はみ出しても切れないが、左端 x=-194 はパネル内（-200..200）に収まっている）。
        var length = EnsureText(panel.transform, "LengthText", font, 9, new Vector2(-158f, -20f), new Vector2(72f, 14f), "半荘戦");
        length.color = new Color(0.62f, 0.62f, 0.62f);

        // 旧レイアウトの遺物（Slotk_Pos / Slotk_Sub など）を掃除して冪等に。
        CleanupPanelChildren(panel.transform);
    }

    // 期待する子だけ残す（横1列時代の Slotk_Pos / Slotk_Sub を撤去）。
    static void CleanupPanelChildren(Transform panel)
    {
        var keep = new System.Collections.Generic.HashSet<string>
        {
            "Dora0", "Dora1", "Dora2", "Dora3", "Dora4",
            "Background", "RoundText", "HonbaText", "KyotakuText", "KyotakuStick", "WallText", "LengthText",
            "Slot0_Score", "Slot1_Score", "Slot2_Score", "Slot3_Score",
            "Slot0_Ghost", "Slot1_Ghost", "Slot2_Ghost", "Slot3_Ghost",
        };
        for (int i = panel.childCount - 1; i >= 0; --i)
        {
            var c = panel.GetChild(i);
            if (!keep.Contains(c.name)) { Undo.DestroyObjectImmediate(c.gameObject); }
        }
    }

    // 1家ぶんの点数を center に生成。7セグ時はゴースト背景（全セグ"88888"）＋実値の2層。
    static void BuildSlot(Transform panel, int slot, Font ledFont, Font jpFont, bool hasLed, Vector2 center)
    {
        var scoreFont = hasLed ? ledFont : jpFont;

        if (hasLed)
        {
            // 全セグ点灯の暗い背景（消えているセグも薄く見える＝LEDらしさ）。
            var ghost = EnsureText(panel, "Slot" + slot + "_Ghost", scoreFont, ScoreFontSize, center, ScoreCell, "88888");
            ghost.color = GhostColor;
            ghost.supportRichText = false;
        }
        else
        {
            // 7セグ無し時はゴーストを作らない（"88888" が普通のフォントだと不自然なため）。
            var g = panel.Find("Slot" + slot + "_Ghost");
            if (g != null) { Undo.DestroyObjectImmediate(g.gameObject); }
        }

        var score = EnsureText(panel, "Slot" + slot + "_Score", scoreFont, ScoreFontSize, center, ScoreCell, "25000");
        score.color = LedColor;
        score.supportRichText = true;                 // 立直の赤字（リッチ色）に必要（厳守）
        score.transform.SetAsLastSibling();           // ゴーストの上に重ねる
    }

    // 供託の立直棒アイコン。
    //
    // 【Sprite にしない】この png は Texture Type = Default のまま RawImage で貼る。
    // Sprite へ入れ直すと他所の参照が壊れる（広告ポスターと同じ扱い）。
    //
    // 【比を tex.width/height から取らない】素材は 400x56 だが NPOT のため Unity が
    // Power-of-Two の 512x64 へリサンプルして取り込む。取り込み後のサイズから比を
    // 出すと 8.00 になり、正しい 7.143 に対して横へ 12% 伸びる。だから実寸を直接書く。
    // （npotScale を切れば 512x64 化は止まるが、非圧縮 RGBA32 に落ちるので触らない）
    const string StickTexturePath = "Assets/Mahjong/Textures/ma-jan_tenbou1000.png";
    const float StickSourceWidth = 400f;   // png の実寸
    const float StickSourceHeight = 56f;
    const float StickHeight = 7f;          // 供託テキスト（font 9）の隣に置いて釣り合う高さ
    const float StickCenterX = 136f;       // 右端が 161＝テキスト（中心180・幅36）と重ならない

    static RawImage EnsureKyotakuStick(Transform panel)
    {
        var go = EnsureUiChild(panel, "KyotakuStick");
        var img = go.GetComponent<RawImage>();
        if (img == null) { img = go.AddComponent<RawImage>(); }

        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(StickTexturePath);
        if (tex == null)
        {
            Debug.LogWarning("[BuildScoreBoard] 立直棒のテクスチャが見つかりません: " + StickTexturePath);
        }
        img.texture = tex;
        img.color = Color.white;
        img.raycastTarget = false;

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(StickCenterX, 20f);
        rt.sizeDelta = new Vector2(StickHeight * (StickSourceWidth / StickSourceHeight), StickHeight);

        // 供託0本が既定。TableScoreBoard が本数を見て出し入れする。
        if (go.activeSelf) { go.SetActive(false); }
        return img;
    }

    static Text EnsureText(Transform parent, string name, Font font, int size, Vector2 pos, Vector2 sizeDelta, string placeholder)
    {
        var go = EnsureUiChild(parent, name);
        var text = go.GetComponent<Text>();
        if (text == null) { text = go.AddComponent<Text>(); }
        text.font = font;
        text.fontSize = size;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.text = placeholder;
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = sizeDelta;
        return text;
    }

    // ============================================================
    // Wire：U#コンポーネント追加＋参照割り当て
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Wire Score Boards")]
    public static void Wire()
    {
        // U#プログラムアセット生成（.cs直接作成のスクリプトには無いため）。
        if (EnsureProgramAsset(
            "Assets/Mahjong/Sources/TableScoreBoard.cs",
            ProgramAssetDir + "/TableScoreBoard Udon C# Program Asset.asset"))
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[WireScoreBoard] U#プログラムアセットを生成しました。コンパイル完了後、もう一度 Wire Score Boards を実行してください。");
            return;
        }

        var tables = Object.FindObjectsOfType<Mahjong>(true);
        System.Array.Sort(tables, (a, b) => string.CompareOrdinal(GetPath(a.transform), GetPath(b.transform)));
        if (tables.Length == 0)
        {
            Debug.LogError("[WireScoreBoard] シーンに Mahjong（卓）が見つかりません。");
            return;
        }

        int wired = 0;
        foreach (var table in tables)
        {
            var boardT = table.transform.Find(RootName);
            if (boardT == null)
            {
                Debug.LogError("[WireScoreBoard] " + GetPath(table.transform) + " に ScoreBoard がありません。先に Build Score Boards を実行してください。");
                continue;
            }

            var comp = (TableScoreBoard)EnsureUSharpComponent(boardT.gameObject, typeof(TableScoreBoard));
            if (comp == null)
            {
                Debug.LogError("[WireScoreBoard] TableScoreBoard の追加に失敗（プログラムアセット未コンパイル？）。コンパイル後に再実行してください。");
                return;
            }

            var boardRootT = boardT.Find("BoardRoot");
            var rounds = new Text[4];
            var honbas = new Text[4];
            var kyotakus = new Text[4];
            var doraImages = new RawImage[20];
            var kyotakuSticks = new RawImage[4];
            var walls = new Text[UseWallText ? 4 : 0];
            var lengths = new Text[4];
            var scores = new Text[16];
            var subs = new Text[16];
            var poses = new Text[16];
            for (int face = 0; face < 4; ++face)
            {
                var panel = boardRootT != null ? boardRootT.Find("Panel" + face) : null;
                if (panel == null) { continue; }
                rounds[face] = FindText(panel, "RoundText");
                for (int d = 0; d < 5; ++d)
                {
                    var indicator = panel.Find("Dora" + d);
                    doraImages[face * 5 + d] = indicator != null ? indicator.GetComponent<RawImage>() : null;
                }
                honbas[face] = FindText(panel, "HonbaText");
                kyotakus[face] = FindText(panel, "KyotakuText");
                var stickT = panel.Find("KyotakuStick");
                kyotakuSticks[face] = stickT != null ? stickT.GetComponent<RawImage>() : null;
                if (UseWallText) { walls[face] = FindText(panel, "WallText"); }
                lengths[face] = FindText(panel, "LengthText");
                for (int slot = 0; slot < 4; ++slot)
                {
                    // index = 面 * 4 + スロット（0自分/1下家/2対面/3上家）
                    scores[face * 4 + slot] = FindText(panel, "Slot" + slot + "_Score");
                    subs[face * 4 + slot] = FindText(panel, "Slot" + slot + "_Sub");
                    poses[face * 4 + slot] = FindText(panel, "Slot" + slot + "_Pos");
                }
            }

            comp.mahjong = table;
            comp.boardRoot = boardRootT != null ? boardRootT.gameObject : null;
            comp.roundTexts = rounds;
            comp.doraImages = doraImages;
            comp.doraAtlas = AssetDatabase.LoadAssetAtPath<Texture>("Assets/Mahjong/Textures/tiles.png");
            comp.doraUvRects = MeasureDoraRects();
            comp.honbaTexts = honbas;
            comp.kyotakuTexts = kyotakus;
            comp.kyotakuSticks = kyotakuSticks;
            comp.wallTexts = walls;
            comp.lengthTexts = lengths;
            comp.scoreTexts = scores;
            comp.subTexts = subs;
            comp.posTexts = poses;
            // 名前表示は未使用（空配列＝TableScoreBoard 側で安全にスキップ）。
            if (comp.nameTexts == null || comp.nameTexts.Length != 16)
            {
                comp.nameTexts = new Text[0];
            }
            UdonSharpEditorUtility.CopyProxyToUdon(comp);
            EditorUtility.SetDirty(comp);

            // 卓本体 → 点数板の参照（未割当なら機能無効＝従来動作、の逆方向）。
            if (table.scoreBoard != comp)
            {
                table.scoreBoard = comp;
                UdonSharpEditorUtility.CopyProxyToUdon(table);
                EditorUtility.SetDirty(table);
            }
            ++wired;
        }

        EditorSceneManager.MarkSceneDirty(tables[0].gameObject.scene);
        Debug.Log("[WireScoreBoard] 完了：" + wired + "/" + tables.Length + " 卓を配線（round/honba/kyotaku" + (UseWallText ? "/wall" : "") + "/length＋立直棒アイコン＋score/sub/pos 16要素＋卓側 scoreBoard）。");
    }

    static Rect[] MeasureDoraRects()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Mahjong/Prefabas/KemonoMahjong.prefab");
        var rects = new Rect[37];
        string[] names = {
            "M1","M2","M3","M4","M5","M6","M7","M8","M9",
            "P1","P2","P3","P4","P5","P6","P7","P8","P9",
            "S1","S2","S3","S4","S5","S6","S7","S8","S9",
            "Ton","Nan","Sha","Pey","Haku","Hatsu","Chun","M5R","P5R","S5R"
        };
        Transform tiles = null;
        foreach (var t in prefab.GetComponentsInChildren<Transform>(true))
            if (t.name == "Tiles0") { tiles = t; break; }
        if (tiles == null) { throw new System.InvalidOperationException("Dora: Tiles0 missing"); }
        for (int i = 0; i < names.Length; ++i)
        {
            var tile = tiles.Find(names[i]);
            if (tile == null) { throw new System.InvalidOperationException("Dora: " + names[i] + " missing"); }
            var mesh = tile.GetComponent<MeshFilter>().sharedMesh;
            var uv = mesh.uv;
            var tri = mesh.GetTriangles(1);
            float minU = 1f, minV = 1f, maxU = 0f, maxV = 0f;
            for (int v = 0; v < 6; ++v)
            {
                var pt = uv[tri[v]];
                minU = Mathf.Min(minU, pt.x); minV = Mathf.Min(minV, pt.y);
                maxU = Mathf.Max(maxU, pt.x); maxV = Mathf.Max(maxV, pt.y);
            }
            rects[i] = new Rect(minU, minV, maxU - minU, maxV - minV);
        }
        return rects;
    }

    static Text FindText(Transform parent, string name)
    {
        var t = parent.Find(name);
        return t != null ? t.GetComponent<Text>() : null;
    }

    // ============================================================
    // 共通ヘルパ（BuildNameplates と同等の実装を複製）
    // ============================================================
    static bool EnsureProgramAsset(string scriptPath, string assetPath)
    {
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
        if (script == null)
        {
            Debug.LogError("[WireScoreBoard] スクリプトが見つかりません: " + scriptPath);
            return false;
        }
        var type = script.GetClass();
        if (type != null && UdonSharpProgramAsset.GetProgramAssetForClass(type) != null) { return false; }
        if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(assetPath) != null) { return false; }

        var programAsset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
        programAsset.sourceCsScript = script;
        AssetDatabase.CreateAsset(programAsset, assetPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[WireScoreBoard] U#プログラムアセットを生成: " + assetPath);
        return true;
    }

    static UdonSharpBehaviour EnsureUSharpComponent(GameObject go, System.Type type)
    {
        var proxy = (UdonSharpBehaviour)go.GetComponent(type);
        if (proxy != null)
        {
            if (UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy) != null) { return proxy; }

            Debug.LogWarning("[WireScoreBoard] " + type.Name + " の backing UdonBehaviour が無いため作り直します。");
            Object.DestroyImmediate(proxy);
            foreach (var ub in go.GetComponents<UdonBehaviour>())
            {
                if (ub.programSource == null) { Object.DestroyImmediate(ub); }
            }
        }

        var added = (UdonSharpBehaviour)UdonSharpUndo.AddComponent(go, type);
        if (added != null && UdonSharpEditorUtility.GetBackingUdonBehaviour(added) == null)
        {
            Debug.LogError("[WireScoreBoard] " + type.Name + " の追加に失敗（プログラムアセット未コンパイル？）。");
            Object.DestroyImmediate(added);
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
        if (t != null) { return t.gameObject; }
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Create " + name);
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();
        return go;
    }

    static void StretchFull(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    static void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform c in go.transform) { SetLayerRecursively(c.gameObject, layer); }
    }

    static string GetPath(Transform t)
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
