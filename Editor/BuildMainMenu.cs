#if UNITY_EDITOR
// 雀魂風メインメニューのUI骨格を、シーン内の既存 "MainMenu"
// （World Space Canvas, 1920x1080）配下に生成するエディタスクリプト。
// デザインは後で調整する前提で、配置とサイズの目安だけ正しく作る。
// 2回実行しても重複生成しない（同名オブジェクトがあれば再利用）。
// 実行: メニュー Tools > Kemono Mahjong > Build Main Menu
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

public static class BuildMainMenu
{
    const string FontAssetPath = "Assets/Mahjong/Fonts/NotoSansJP-Bold SDF.asset";
    const string FontSourcePath = "Assets/Mahjong/Fonts/NotoSansJP-Bold.ttf";

    static TMP_FontAsset font_;
    static int created_;
    static int reused_;

    [MenuItem("Tools/Kemono Mahjong/Build Main Menu")]
    public static void Run()
    {
        var mainMenu = GameObject.Find("MainMenu");
        if (mainMenu == null)
        {
            Debug.LogError("[BuildMainMenu] シーンに 'MainMenu' が見つかりません。中止します。");
            return;
        }

        var canvas = mainMenu.GetComponent<Canvas>();
        if (canvas == null || canvas.renderMode != RenderMode.WorldSpace)
        {
            Debug.LogError("[BuildMainMenu] 'MainMenu' に World Space Canvas がありません。中止します。");
            return;
        }

        created_ = 0;
        reused_ = 0;
        font_ = GetOrCreateJpFontAsset();
        if (font_ == null)
        {
            // 日本語フォントが無いまま組むと、TMP が既定フォントで作られて豆腐になり、
            // 後から Build し直しても直らない。何も触らずに止める。
            Debug.LogError("[BuildMainMenu] 日本語フォントが用意できないため中止しました。");
            return;
        }

        // VRC_UiShape（レーザー操作に必須）。既にあればスキップ。
        if (mainMenu.GetComponent<VRC.SDK3.Components.VRCUiShape>() == null)
        {
            Undo.AddComponent<VRC.SDK3.Components.VRCUiShape>(mainMenu);
            Debug.Log("[BuildMainMenu] VRCUiShape を追加しました。");
        }

        BuildScreenHome(mainMenu.transform);
        BuildScreenProfile(mainMenu.transform);
        BuildScreenChara(mainMenu.transform);
        BuildScreenWaiting(mainMenu.transform);
        BuildScreenFreeSetup(mainMenu.transform);
        BuildScreenFreeRoom(mainMenu.transform);
        BuildScreenFreeConfirm(mainMenu.transform);
        BuildScreenInGame(mainMenu.transform);
        BuildScreenAbandonConfirm(mainMenu.transform);
        BuildScreenCosmetic(mainMenu.transform);
        BuildScreenWorld(mainMenu.transform);
        BuildScreenTableReset(mainMenu.transform);
        BuildScreenFreeList(mainMenu.transform);
        BuildScreenSelfie(mainMenu.transform);

        // レイヤーを Default に変更（子も含む）。VRChatのUIレーザーはDefaultレイヤーで判定される。
        SetLayerRecursively(mainMenu, 0);
        StyleMainMenu.Apply(mainMenu);

        EditorSceneManager.MarkSceneDirty(mainMenu.scene);

        Debug.Log("[BuildMainMenu] 完了: 新規 " + created_ + " / 再利用 " + reused_ + " オブジェクト。\n" +
                  DumpHierarchy(mainMenu.transform, 0));
    }

    // ============================================================
    // 画面ごとの生成
    // ============================================================

    static void BuildScreenHome(Transform root)
    {
        var screen = Screen(root, "ScreenHome", true);

        // 上部ヘッダー
        var header = Child(screen, "HeaderBar");
        SetRect(header, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, 0f), new Vector2(0f, 140f));
        EnsureImage(header, new Color(0f, 0f, 0f, 0.35f));

        var icon = Child(header, "PlayerIcon");
        SetRect(icon, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(40f, 0f), new Vector2(100f, 100f));
        var iconBackground = EnsureImage(icon, Color.clear);
        iconBackground.color = Color.clear;
        iconBackground.raycastTarget = false;

        var headerRankIcon = Child(icon, "RankIcon");
        SetRect(headerRankIcon, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(100f, 100f));
        var headerRankRaw = EnsureRawImage(headerRankIcon);
        headerRankRaw.color = Color.white;
        headerRankRaw.raycastTarget = false;

        var rankBadge = Child(header, "RankBadge");
        SetRect(rankBadge, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(170f, -28f), new Vector2(420f, 46f));
        EnsureTmp(rankBadge, "段位 Lv0 / 0pt", 30f, TextAlignmentOptions.Left);

        var playerName = Child(header, "PlayerName");
        SetRect(playerName, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(170f, 26f), new Vector2(420f, 54f));
        EnsureTmp(playerName, "プレイヤー名", 38f, TextAlignmentOptions.Left);

        // 左側：現在選択中のキャラ写真。元画像700x1080の上側700pxを正方形で表示する。
        var selectedMascot = Child(screen, "SelectedMascot");
        // Y=-85 は実機の見え方を見て詰めた確定値（2026-09-04）。
        // **ここを直さずにシーン側だけ動かすと、次の Build Main Menu で戻る。**
        SetRect(selectedMascot,
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
            new Vector2(-520f, -85f), new Vector2(700f, 700f));
        var selectedMascotRaw = EnsureRawImage(selectedMascot);
        selectedMascotRaw.color = Color.white;
        selectedMascotRaw.raycastTarget = false;
        selectedMascotRaw.uvRect = new Rect(0f, 380f / 1080f, 1f, 700f / 1080f);
        selectedMascot.SetSiblingIndex(Mathf.Min(1, screen.childCount - 1));

        // 旧レイアウト（段位戦＝半荘固定の2ボタン）の残骸を削除（2026-08-18 東風戦追加）。
        // EnsureTmp は既存TMPのtextを書き換えないため、同名を再利用するとラベルが
        // 「四麻 段位戦」のまま残る。名前ごと作り直すのが確実。
        for (int i = 0; i < 2; ++i)
        {
            var staleName = i == 0 ? "BtnRanked4" : "BtnRanked3";
            var stale = screen.Find(staleName);
            if (stale != null)
            {
                Undo.DestroyObjectImmediate(stale.gameObject);
                Debug.Log("[BuildMainMenu] 旧段位戦ボタンを削除: " + staleName);
            }
        }
        // 右側中央：段位戦は「人数（四麻/三麻）×局数（半荘/東風）」の2×2グリッド。
        // 左列=半荘戦・右列=東風戦。従来の3ボタンと同じ横幅620pxの帯に収める
        // （620 = 300 + 20(間隔) + 300）。フリー対局は従来どおり最下段に全幅。
        const float ColW = 300f;
        const float RightX = -140f;              // 右列（東風戦）
        const float LeftX = RightX - ColW - 20f; // 左列（半荘戦）
        var btnAnchor = new Vector2(1f, 0.5f);

        EnsureButton(screen, "BtnRanked4Han", "四麻 半荘戦",
            btnAnchor, btnAnchor, new Vector2(LeftX, 210f), new Vector2(ColW, 150f), 38f);
        EnsureButton(screen, "BtnRanked4Ton", "四麻 東風戦",
            btnAnchor, btnAnchor, new Vector2(RightX, 210f), new Vector2(ColW, 150f), 38f);
        EnsureButton(screen, "BtnRanked3Han", "三麻 半荘戦",
            btnAnchor, btnAnchor, new Vector2(LeftX, 0f), new Vector2(ColW, 150f), 38f);
        EnsureButton(screen, "BtnRanked3Ton", "三麻 東風戦",
            btnAnchor, btnAnchor, new Vector2(RightX, 0f), new Vector2(ColW, 150f), 38f);
        // 文言の正は MainMenuController.UpdateFreeButton（毎回上書きされる）。
        // ここは新規作成時と Editor プレビュー用の初期値だが、見た目がズレると
        // 調整を誤るので同じ文字列を入れておく。
        EnsureButton(screen, "BtnFree", "フリー対局\n<size=55%>CPU戦・練習もできる</size>",
            btnAnchor, btnAnchor, new Vector2(RightX, -210f), new Vector2(620f, 150f), 52f);

        // 下部の4ボタン（プロフィール／キャラ選択／装飾品／ワールド設定）。
        // 340px×4＋間隔20px×3 = 1420px の帯に収める（Canvas幅1920）。
        EnsureButton(screen, "BtnProfile", "プロフィール",
            new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(-540f, 45f), new Vector2(340f, 110f), 36f);
        EnsureButton(screen, "BtnChara", "キャラ選択",
            new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(-180f, 45f), new Vector2(340f, 110f), 36f);
        EnsureButton(screen, "BtnCosmetic", "装飾品",
            new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(180f, 45f), new Vector2(340f, 110f), 36f);
        EnsureButton(screen, "BtnWorld", "ワールド設定",
            new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(540f, 45f), new Vector2(340f, 110f), 36f);

        // 一時メッセージ（「満員です」等。MainMenuController が書き換え・自動消去）。
        var homeInfo = Child(screen, "HomeInfo");
        SetRect(homeInfo, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 180f), new Vector2(1200f, 60f));
        EnsureTmp(homeInfo, "", 36f, TextAlignmentOptions.Center);
    }

    static void BuildScreenProfile(Transform root)
    {
        var screen = Screen(root, "ScreenProfile", false);

        EnsureButton(screen, "BtnBack", "もどる",
            new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(40f, -40f), new Vector2(180f, 90f), 32f);

        var nameText = Child(screen, "ProfileName");
        SetRect(nameText, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(260f, -45f), new Vector2(600f, 60f));
        EnsureTmp(nameText, "プレイヤー名", 44f, TextAlignmentOptions.Left);

        var rankText = Child(screen, "ProfileRank");
        SetRect(rankText, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(260f, -110f), new Vector2(600f, 48f));
        EnsureTmp(rankText, "段位 Lv0 / 0pt", 32f, TextAlignmentOptions.Left);

        var profileRankIcon = Child(screen, "ProfileRankIcon");
        // 左上の戻るボタン（y=-40, 高さ90）の下へ配置し、操作領域を覆わない。
        SetRect(profileRankIcon, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(60f, -160f), new Vector2(160f, 160f));
        var profileRankRaw = EnsureRawImage(profileRankIcon);
        profileRankRaw.color = Color.white;
        profileRankRaw.raycastTarget = false;

        // 旧レイアウト（四麻/三麻の2ブロック並列）の残骸を削除（2026-07-17 タブ式へ変更）。
        for (int i = 0; i < 2; ++i)
        {
            var staleName = i == 0 ? "StatsBlock4" : "StatsBlock3";
            var stale = screen.Find(staleName);
            if (stale != null)
            {
                Undo.DestroyObjectImmediate(stale.gameObject);
                Debug.Log("[BuildMainMenu] 旧統計ブロックを削除: " + staleName);
            }
        }

        // 戦績タブ（四麻段位戦/三麻段位戦/フリー対局）。onClick は WireMainMenu が
        // ShowProfileTab4 / ShowProfileTab3 / ShowProfileTabFree へ配線する。
        // 選択中のハイライト色は MainMenuController.RenderProfile が Image.color で上書きする。
        EnsureButton(screen, "BtnTab4", "四麻 段位戦",
            new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-460f, -160f), new Vector2(430f, 95f), 36f);
        EnsureButton(screen, "BtnTab3", "三麻 段位戦",
            new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -160f), new Vector2(430f, 95f), 36f);
        EnsureButton(screen, "BtnTabF", "フリー対局",
            new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(460f, -160f), new Vector2(430f, 95f), 36f);

        // 選択中カテゴリの統計ブロック（1つだけ。タイトルはスクリプトが書き換える）。
        BuildStatsBlock(screen, "StatsBlockSel", "四麻 段位戦", new Vector2(600f, -290f));

        // 直近10戦グラフ：4本の横罫線 + Dot0〜9。Dotの縦位置はスクリプトが動かす。
        // 罫線 y=120(1位) / 40 / -40 / -120(4位) は MainMenuController の
        // graphRank1Y=120, graphRankStep=80 と対応させている。
        var graph = Child(screen, "RecentGraph");
        SetRect(graph, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 50f), new Vector2(1240f, 330f));
        EnsureImage(graph, new Color(0f, 0f, 0f, 0.25f));

        for (int rank = 1; rank <= 4; ++rank)
        {
            var line = Child(graph, "Line" + rank);
            float y = 120f - (rank - 1) * 80f;
            SetRect(line, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, y), new Vector2(1140f, 4f));
            EnsureImage(line, new Color(1f, 1f, 1f, 0.4f));
        }

        for (int i = 0; i < 10; ++i)
        {
            var dot = Child(graph, "Dot" + i);
            float x = -510f + i * 113f;
            SetRect(dot, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(x, 120f), new Vector2(22f, 22f));
            EnsureImage(dot, new Color(1f, 0.65f, 0.2f, 1f));
        }
        ProfileAnalyticsBuilder.BuildFor(root.gameObject);
    }

    // 対局数/1位/和了/放銃 のラベル+値を4行持つ統計ブロック。
    static void BuildStatsBlock(Transform screen, string blockName, string title, Vector2 pos)
    {
        var block = Child(screen, blockName);
        SetRect(block, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), pos, new Vector2(720f, 400f));
        EnsureImage(block, new Color(0f, 0f, 0f, 0.25f));

        var titleText = Child(block, "Title");
        SetRect(titleText, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -10f), new Vector2(660f, 52f));
        EnsureTmp(titleText, title, 34f, TextAlignmentOptions.Left);

        string[] rowNames = { "RowPlay", "RowWin", "RowAgari", "RowHoju" };
        string[] rowLabels = { "対局数", "1位", "和了", "放銃" };

        for (int i = 0; i < 4; ++i)
        {
            var row = Child(block, rowNames[i]);
            float y = -85f - i * 75f;
            SetRect(row, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), new Vector2(30f, y), new Vector2(660f, 60f));

            var label = Child(row, "Label");
            SetRect(label, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0f), new Vector2(220f, 56f));
            EnsureTmp(label, rowLabels[i], 30f, TextAlignmentOptions.Left);

            var value = Child(row, "Value");
            SetRect(value, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(240f, 0f), new Vector2(420f, 56f));
            EnsureTmp(value, "--", 30f, TextAlignmentOptions.Right);
        }
    }

    static void BuildScreenChara(Transform root)
    {
        var screen = Screen(root, "ScreenChara", false);

        EnsureButton(screen, "BtnBack", "もどる",
            new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(40f, -40f), new Vector2(180f, 90f), 32f);

        // 左側：大ポートレート + 能力説明
        var detail = Child(screen, "CharaDetail");
        SetRect(detail, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(80f, -40f), new Vector2(620f, 820f));
        EnsureImage(detail, new Color(0f, 0f, 0f, 0.25f));

        var portrait = Child(detail, "DetailPortrait");
        // マスコット画像は700x1080の縦長。正方形へ引き伸ばさず、ほぼ同じ縦横比で表示する。
        // 324x500 → 259x400（2026-09-09）：自画像キャラの説明文が3段落になり、下の
        // DetailText を 240→350 へ広げる必要があった。立ち絵の下端は 820-30-400 = 390 で、
        // DetailText の上端 30+350 = 380 との間に 10px 残る。片方だけ動かすと文字が絵に食い込む。
        //
        // **高さだけ縮めてはいけない。** RawImage に preserveAspect は無いので、素材の比
        // 700:1080 = 0.648 から外れた枠に入れると絵がそのまま伸びる。324x500 は 0.648 で
        // ぴったりだったが、324x400 にすると 0.810 ＝ 横へ 25% 太る。幅も 400*0.648 = 259 へ。
        SetRect(portrait, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -30f), new Vector2(259f, 400f));
        var detailPortraitRaw = EnsureRawImage(portrait);
        detailPortraitRaw.color = Color.white;
        detailPortraitRaw.raycastTarget = false;

        var desc = Child(detail, "DetailText");
        // 枠 240→350・font 30→24（2026-09-09）。自画像の説明が3段落になり、font30 では
        // 12行 h=521 で全く入らない。TMP実測で font24 なら日英とも 9行 h=313 に収まる。
        // 数字を変えるときは DetailPortrait の高さ（上の 400）と必ず一緒に見ること。
        SetRect(desc, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 30f), new Vector2(560f, 350f));
        var descTmp = EnsureTmp(desc, "キャラを選択してください", 24f, TextAlignmentOptions.TopLeft);
        // EnsureTmp は TMP を新規に付けたときしか fontSize を入れない（実行時に書かれる
        // 文言や手直しを潰さないため）。既存の画面へサイズ変更を通すには明示代入が要る。
        descTmp.fontSize = 24f;
        descTmp.enableWordWrapping = true;

        // 右側：現行キャラ4体のグリッド（Vol/Shine/Tsumuntema/自画像）
        var grid = Child(screen, "CharaGrid");
        SetRect(grid, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-100f, 40f), new Vector2(1020f, 470f));

        // 旧キット時代の余剰カード（CharaCard4）が残っていたら削除する。
        // CharaCard3 は自画像キャラで再利用するので消さない。
        {
            var stale = grid.Find("CharaCard4");
            if (stale != null)
            {
                Undo.DestroyObjectImmediate(stale.gameObject);
                Debug.Log("[BuildMainMenu] 旧キャラカードを削除: CharaCard4");
            }
        }

        var layout = grid.GetComponent<GridLayoutGroup>();
        if (layout == null) { layout = Undo.AddComponent<GridLayoutGroup>(grid.gameObject); }
        // 4枚を1行に収める。幅 = 4*240 + 3*20 = 1020 でグリッド幅ちょうど。
        layout.cellSize = new Vector2(240f, 450f);
        layout.spacing = new Vector2(20f, 20f);
        layout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        layout.constraintCount = 4;
        layout.childAlignment = TextAnchor.UpperLeft;

        string[] charaNames = { "Vol", "Shine", "Tsumuntema", "自画像" };

        for (int i = 0; i < charaNames.Length; ++i)
        {
            var card = Child(grid, "CharaCard" + i);
            // GridLayoutGroup がサイズを制御するため anchoredPosition は設定不要。
            var cardImage = EnsureImage(card, new Color(0.15f, 0.15f, 0.2f, 0.9f));

            var button = card.GetComponent<Button>();
            if (button == null)
            {
                button = Undo.AddComponent<Button>(card.gameObject);
                button.targetGraphic = cardImage;
            }

            var cardPortrait = Child(card, "Portrait");
            SetRect(cardPortrait, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -16f), new Vector2(216f, 336f));
            var cardPortraitRaw = EnsureRawImage(cardPortrait);
            cardPortraitRaw.color = Color.white;
            cardPortraitRaw.raycastTarget = false;

            var cardName = Child(card, "Name");
            SetRect(cardName, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 16f), new Vector2(232f, 58f));
            // EnsureTmp は既存オブジェクトのテキストを更新しないため、キャラ名は明示的に上書きする
            // （旧キット時代のカード名「キツネ」等が残る事故の防止）。
            var nameTmp = EnsureTmp(cardName, charaNames[i], 32f, TextAlignmentOptions.Center);
            nameTmp.text = charaNames[i];
            // 4列化でカードが狭くなったので、長い名前は自動で縮める（Tsumuntema がはみ出す）。
            nameTmp.enableAutoSizing = true;
            nameTmp.fontSizeMax = 32f;
            nameTmp.fontSizeMin = 20f;
            nameTmp.enableWordWrapping = false;

            var badge = Child(card, "SelectedBadge");
            SetRect(badge, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-8f, -8f), new Vector2(48f, 48f));
            EnsureImage(badge, new Color(1f, 0.75f, 0.1f, 1f));
            badge.gameObject.SetActive(false); // 初期非表示（選択中カードのみスクリプトが表示）

            // ロック表示（解禁条件つきキャラ用）。カード全面を暗幕で覆い、条件を書く。
            // 出し入れは MainMenuController.RenderCharaLocks が行う。
            var lockRoot = Child(card, "LockOverlay");
            SetRect(lockRoot, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            var lockRt = (RectTransform)lockRoot;
            lockRt.offsetMin = Vector2.zero;
            lockRt.offsetMax = Vector2.zero;
            var lockImage = EnsureImage(lockRoot, new Color(0f, 0f, 0f, 0.72f));
            lockImage.raycastTarget = false; // カード本体のボタンを塞がない

            var lockLabel = Child(lockRoot, "LockLabel");
            SetRect(lockLabel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(216f, 160f));
            var lockTmp = EnsureTmp(lockLabel, "", 30f, TextAlignmentOptions.Center);
            lockTmp.enableWordWrapping = true;
            // 「鋭爪★1 で解禁」は240幅のカードだと1行に収まらない。自動で縮める。
            lockTmp.enableAutoSizing = true;
            lockTmp.fontSizeMax = 30f;
            lockTmp.fontSizeMin = 18f;

            lockRoot.gameObject.SetActive(false); // 初期非表示（ロック中のみスクリプトが表示）

            // 自画像カードだけ、写真未撮影のときにポートレート枠へ出す案内。
            // 出し入れは MainMenuController.ApplyPortraits が行う。
            if (i == 3)
            {
                var hint = Child(card, "Hint");
                SetRect(hint, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                    new Vector2(0f, -150f), new Vector2(200f, 120f));
                var hintTmp = EnsureTmp(hint, "写真を\nとる", 34f, TextAlignmentOptions.Center);
                hintTmp.enableWordWrapping = true;
            }
        }

        // 能力スイッチ（ON/OFFトグルボタン）。ラベルは MainMenuController が書き換える。
        // CharaDetail（左）と重ならないよう右下（グリッドの下）に置く。
        EnsureButton(screen, "BtnAbilityToggle", "能力：OFF",
            new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-100f, 45f), new Vector2(620f, 110f), 40f);
        var abilityToggle = screen.Find("BtnAbilityToggle");
        if (abilityToggle != null) { abilityToggle.gameObject.SetActive(false); }

        // チュンビームのON/OFF。能力スイッチ（非表示で温存）と同じ右下の枠を使う。
        // 左隣（グリッド左端〜ボタン左端）に解放条件／説明を置く。
        // 出し入れ（チュンビームを持たないキャラでは隠す）は MainMenuController.RenderChunBeamToggle。
        EnsureButton(screen, "BtnChunBeamToggle", "チュンビーム：未解放",
            new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-100f, 45f), new Vector2(620f, 110f), 40f);
        var chunNote = Child(screen, "ChunBeamNote");
        SetRect(chunNote, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f),
            new Vector2(-740f, 45f), new Vector2(400f, 110f));
        var chunNoteTmp = EnsureTmp(chunNote, "", 26f, TextAlignmentOptions.Right);
        chunNoteTmp.enableWordWrapping = true;
        chunNoteTmp.enableAutoSizing = true;
        chunNoteTmp.fontSizeMax = 26f;
        chunNoteTmp.fontSizeMin = 16f;
    }

    // 装飾品（和了演出コスメ）画面。雀魂の「装飾品 > 和了演出」相当。
    // カードの枚数は上限8枚ぶん作っておき、実際に使う枚数は実行時に
    // MainMenuController が MahjongAgariEffect のカタログ数で出し分ける。
    [MenuItem("Tools/Kemono Mahjong/Build Cosmetic Customization")]
    public static void BuildCosmeticOnly()
    {
        var menu = GameObject.Find("MainMenu");
        if (menu == null) { return; }
        font_ = GetOrCreateJpFontAsset();
        BuildScreenCosmetic(menu.transform);
        WireMainMenu.WireCosmetics(menu.GetComponent<SuzuFactory.AozoraMahjong.MainMenuController>());
        EditorSceneManager.MarkSceneDirty(menu.scene);
    }

    static void BuildScreenCosmetic(Transform root)
    {
        const int MaxCards = 8;

        var screen = Screen(root, "ScreenCosmetic", false);

        EnsureButton(screen, "BtnBack", "もどる",
            new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(40f, -40f), new Vector2(180f, 90f), 32f);

        var title = Child(screen, "Title");
        SetRect(title, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -50f), new Vector2(900f, 80f));
        EnsureTmp(title, "装飾品　和了演出", 52f, TextAlignmentOptions.Center);

        // --- 左：コスメカードのグリッド（4列×2行） ---
        var grid = Child(screen, "FxGrid");
        SetRect(grid, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(70f, -30f), new Vector2(1120f, 470f));

        var layout = grid.GetComponent<GridLayoutGroup>();
        if (layout == null) { layout = Undo.AddComponent<GridLayoutGroup>(grid.gameObject); }
        layout.cellSize = new Vector2(260f, 220f);
        layout.spacing = new Vector2(24f, 24f);
        layout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        layout.constraintCount = 4;
        layout.childAlignment = TextAnchor.UpperLeft;

        for (int i = 0; i < MaxCards; ++i)
        {
            var card = Child(grid, "FxCard" + i);
            // サイズは GridLayoutGroup が制御するため anchoredPosition は設定不要。
            var cardImage = EnsureImage(card, new Color(0.15f, 0.15f, 0.2f, 0.9f));

            var button = card.GetComponent<Button>();
            if (button == null)
            {
                button = Undo.AddComponent<Button>(card.gameObject);
                button.targetGraphic = cardImage;
            }

            // 色見本（コスメの主色で塗る＝アート未完成でも見分けがつく）。
            // 後でサムネイル画像を入れる場合はこの Image を差し替える。
            var swatch = Child(card, "Swatch");
            SetRect(swatch, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -16f), new Vector2(220f, 130f));
            var swatchImage = EnsureImage(swatch, Color.white);
            swatchImage.raycastTarget = false;

            var cardName = Child(card, "Name");
            SetRect(cardName, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 14f), new Vector2(240f, 56f));
            EnsureTmp(cardName, "", 34f, TextAlignmentOptions.Center);

            var badge = Child(card, "SelectedBadge");
            SetRect(badge, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-8f, -8f), new Vector2(48f, 48f));
            EnsureImage(badge, new Color(1f, 0.75f, 0.1f, 1f));
            badge.gameObject.SetActive(false); // 初期非表示（選択中のみスクリプトが表示）

            // ロック暗幕（解禁条件つきコスメ用。いまは立直BGMの曲だけ）。
            // キャラ選択の LockOverlay と同じ作りで、出し入れは MainMenuController.RenderCosmetic。
            // raycastTarget は切る＝カード自体は押せる（解禁前でも試聴はできる）。
            var lockRoot = Child(card, "LockOverlay");
            SetRect(lockRoot, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            var lockRt = (RectTransform)lockRoot;
            lockRt.offsetMin = Vector2.zero;
            lockRt.offsetMax = Vector2.zero;
            var lockImage = EnsureImage(lockRoot, new Color(0f, 0f, 0f, 0.72f));
            lockImage.raycastTarget = false;

            var lockLabel = Child(lockRoot, "LockLabel");
            SetRect(lockLabel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(190f, 150f));
            var lockTmp = EnsureTmp(lockLabel, "", 28f, TextAlignmentOptions.Center);
            lockTmp.enableWordWrapping = true;
            // 「鋭爪★1 で解禁」はカード幅216だと1行に収まらない。自動で縮める。
            lockTmp.enableAutoSizing = true;
            lockTmp.fontSizeMax = 28f;
            lockTmp.fontSizeMin = 16f;

            lockRoot.gameObject.SetActive(false); // 初期非表示（ロック中のみスクリプトが表示）
        }

        // --- 右：詳細（名前・説明・試写） ---
        var detail = Child(screen, "FxDetail");
        SetRect(detail, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-70f, -30f), new Vector2(560f, 560f));
        EnsureImage(detail, new Color(0f, 0f, 0f, 0.25f));

        var detailName = Child(detail, "DetailName");
        SetRect(detailName, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -40f), new Vector2(500f, 70f));
        EnsureTmp(detailName, "", 44f, TextAlignmentOptions.Center);

        var detailDesc = Child(detail, "DetailDesc");
        SetRect(detailDesc, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -130f), new Vector2(500f, 280f));
        var descTmp = EnsureTmp(detailDesc, "", 30f, TextAlignmentOptions.TopLeft);
        descTmp.enableWordWrapping = true;

        EnsureButton(detail, "BtnPreviewFx", "試写",
            new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 40f), new Vector2(360f, 100f), 38f);

        var note = Child(screen, "FxNote");
        SetRect(note, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 60f), new Vector2(1500f, 60f));
        var noteTmp = EnsureTmp(note, "選んだ和了演出は、あなたがロン／ツモしたときに全員へ表示されます。", 30f, TextAlignmentOptions.Center);
        noteTmp.enableWordWrapping = true;
        EnsureTmp(title, "装飾品", 52f, TextAlignmentOptions.Center).text = "装飾品";
        string[] categories = { "和了エフェクト", "麻雀牌の背面\n<size=26>ローカル</size>", "麻雀卓の上面\n<size=26>ローカル</size>", "立直BGM" };
        for (int i = 0; i < 4; ++i)
            EnsureButton(screen, "BtnCosmeticCategory" + i, categories[i],
                new Vector2(.5f,.5f), new Vector2(.5f,.5f), new Vector2(-640f, 240f - i * 150f), new Vector2(460f,125f), 38f);
        SetRect(grid, new Vector2(.5f,.5f), new Vector2(.5f,.5f), new Vector2(.5f,.5f), new Vector2(300f,35f), new Vector2(960f,450f));
        layout.cellSize = new Vector2(216f,200f);
        layout.spacing = new Vector2(28f,28f);
        for (int i = 0; i < MaxCards; ++i)
        {
            var card = grid.Find("FxCard" + i);
            SetRect(card.Find("Swatch"), new Vector2(.5f,1f), new Vector2(.5f,1f), new Vector2(.5f,1f), new Vector2(0,-16), new Vector2(180,115));
            SetRect(card.Find("Name"), new Vector2(.5f,0), new Vector2(.5f,0), new Vector2(.5f,0), new Vector2(0,8), new Vector2(204,58));

            // カード名は中身の長さがカテゴリごとに違う（「Whistle in the Dark」「スレートブルー」など）。
            // 固定34ptだとカード幅216を軽く超えて左右へはみ出すので、折り返し＋自動縮小で枠に収める。
            // 収まらないぶんは点ではなく縮小で吸収する＝曲名・色名は途中で切れない。
            var nameTmp = card.Find("Name").GetComponent<TextMeshProUGUI>();
            if (nameTmp != null)
            {
                nameTmp.enableWordWrapping = true;
                nameTmp.overflowMode = TextOverflowModes.Truncate;
                nameTmp.enableAutoSizing = true;
                nameTmp.fontSizeMax = 34f;
                nameTmp.fontSizeMin = 18f;
                nameTmp.alignment = TextAlignmentOptions.Center;
            }
        }
        var categoryTitle = Child(screen, "CosmeticCategoryTitle");
        SetRect(categoryTitle, new Vector2(.5f,.5f), new Vector2(.5f,.5f), new Vector2(.5f,.5f), new Vector2(300f,330f), new Vector2(960,70));
        EnsureTmp(categoryTitle, "和了エフェクト", 40f, TextAlignmentOptions.Left);
        SetRect(detail, new Vector2(.5f,.5f), new Vector2(.5f,.5f), new Vector2(.5f,.5f), new Vector2(300,-310), new Vector2(960,180));
        SetRect(detailName, new Vector2(.5f,.5f), new Vector2(.5f,.5f), new Vector2(.5f,.5f), new Vector2(-150,45), new Vector2(580,55));
        SetRect(detailDesc, new Vector2(.5f,.5f), new Vector2(.5f,.5f), new Vector2(.5f,.5f), new Vector2(-150,-35), new Vector2(580,85));
        SetRect(detail.Find("BtnPreviewFx"), new Vector2(.5f,.5f), new Vector2(.5f,.5f), new Vector2(.5f,.5f), new Vector2(330,0), new Vector2(240,90));
        EnsureButton(detail, "BtnResetColor", "元に戻す",
            new Vector2(.5f,.5f), new Vector2(.5f,.5f), new Vector2(330,0), new Vector2(240,90), 32f);
        detail.Find("BtnResetColor").gameObject.SetActive(false);
        foreach (var text in screen.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            // ロック文言だけは黒い暗幕の上に乗るので白。StyleMainMenu.Apply も同じ例外を持つ。
            text.color = text.name == "LockLabel" ? StyleMainMenu.White : StyleMainMenu.Ink;
            text.raycastTarget = false;
        }
        foreach (var img in screen.GetComponentsInChildren<Image>(true))
        {
            if (img.GetComponent<Button>() != null)
            {
                img.color = StyleMainMenu.White;
                StyleMainMenu.Frame(img, 4f, 9f);
                StyleMainMenu.ButtonTint(img.GetComponent<Button>());
            }
            else if (img.name == "FxDetail") { img.color = StyleMainMenu.White; StyleMainMenu.Frame(img,3,8); }
            else if (img.name == "SelectedBadge") { img.color = StyleMainMenu.Yellow; StyleMainMenu.Frame(img,3,0); }
        }
    }

    // ============================================================
    // ワールド設定画面（雀魂式：左に項目タブ、右にそのページ）
    //
    //  ScreenWorld
    //   ├ BtnBack / Title
    //   ├ TabSound / TabPlay / TabVr / TabDisplay / TabYaku / TabStaff … 左のタブ（TabStaff は権限者だけ）
    //   ├ LineTabRail                                          … タブと右側の仕切り
    //   ├ PageSound   … VolumePanel（BuildVolumePanel が作る）
    //   ├ PagePlay    … 立直後のツモ切り / 押した直後の連打防止
    //   ├ PageVr      … 卓のボタンの押し方（レーザー／タッチ） / 牌を掴んで打牌（どちらも VR のみ）
    //   ├ PageDisplay … 表示言語 / 他人のチュンビーム
    //   ├ PageYaku    … 役の早見板（札はここ、中身は BuildYakuGuide が作る）
    //   └ PageStaff   … 卓の強制リセットへの入口
    //
    // 1行＝白い札（Row*）に「見出し（左）｜選択肢（右）」、札の下段に説明。
    // 出し入れは MainMenuController.RenderWorld が見ている人の端末だけで行う（同期なし）。
    // 以前は1画面に全部を詰めていて、空いた隙間へ設定を足すたびに見づらくなった（2026-10-05 改修）。
    // 卓に付いていた Jp/En ボタンは廃止し、切替口をここに集約してある。
    // ============================================================

    // 右側のページ（Canvas 中央基準）。左のタブ列は x -930..-560、札は x -500..900。
    // BuildYakuGuide / BuildVolumePanel もこの座標に合わせる。
    public const float WorldPaneX = 200f;
    public const float WorldRowW = 1400f;
    public const float WorldRowH = 220f;
    public const float WorldRowLeft = WorldPaneX - WorldRowW * 0.5f;
    public const float WorldRowRight = WorldPaneX + WorldRowW * 0.5f;
    public const float WorldPad = 30f;
    public const float WorldHeadW = 520f;
    public const float WorldChoiceGap = 20f;
    public const float WorldHeadDY = 50f;    // 札の中心から見出し・選択肢の段まで
    public const float WorldNoteDY = -62f;   // 札の中心から説明の段まで
    public static readonly float[] WorldRowY = { 250f, -30f, -310f };
    // 並び＝MainMenuController の SetWorldTabN / WORLD_TAB_* と同じ（スタッフは必ず最後）。
    public static readonly string[] WorldPageNames = { "PageSound", "PagePlay", "PageVr", "PageDisplay", "PageYaku", "PageStaff" };
    public static readonly string[] WorldTabNames = { "TabSound", "TabPlay", "TabVr", "TabDisplay", "TabYaku", "TabStaff" };
    static readonly string[] WorldTabLabels = { "サウンド", "対局", "VR操作", "表示・演出", "役の早見板", "スタッフ" };

    static void BuildScreenWorld(Transform root)
    {
        var screen = Screen(root, "ScreenWorld", false);

        EnsureButton(screen, "BtnBack", "もどる",
            new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(40f, -40f), new Vector2(180f, 90f), 32f);

        var title = Child(screen, "Title");
        SetRect(title, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0f, -50f), new Vector2(900f, 90f));
        EnsureTmp(title, "ワールド設定", 56f, TextAlignmentOptions.Center);

        // --- 左のタブ（「もどる」の下から縦に並べる。スタッフは権限者だけ実行時に出す）---
        var center = new Vector2(0.5f, 0.5f);
        for (int i = 0; i < WorldTabNames.Length; ++i)
        {
            EnsureButton(screen, WorldTabNames[i], WorldTabLabels[i],
                center, center, new Vector2(-745f, 300f - i * 135f), new Vector2(370f, 110f), 40f);
            var tab = screen.Find(WorldTabNames[i]);
            SetLabelSize(tab, 40f);
            StyleMainMenu.Accent(tab, StyleMainMenu.Purple);
        }
        screen.Find("TabStaff").gameObject.SetActive(false);

        // タブ列と右側の仕切り（Line* は StyleMainMenu が罫線色に塗る）。
        var rail = Child(screen, "LineTabRail");
        SetRect(rail, center, center, center, new Vector2(-530f, -60f), new Vector2(4f, 860f));
        EnsureImage(rail, StyleMainMenu.Rule).raycastTarget = false;

        // --- 右のページ（サウンドだけ開いた状態で焼く。実行時は RenderWorld が出し入れする）---
        var pages = new Transform[WorldPageNames.Length];
        for (int i = 0; i < pages.Length; ++i) { pages[i] = Screen(screen, WorldPageNames[i], i == 0); }

        // 旧レイアウト（ScreenWorld 直下に全部並べていた）の部品をページへ付け替える。
        // 作り直すと onClick の配線（ほかのビルダーが張った分も含む）を失うので移すだけ。
        MoveAllInto(screen, pages[0], new[] { "VolumePanel" });
        MoveAllInto(screen, pages[1], new[] { "TsumogiriHead", "BtnReachTsumogiriOn", "BtnReachTsumogiriOff", "TsumogiriNote",
            "PressGuardHead", "BtnPressGuard50", "BtnPressGuard100", "BtnPressGuard200", "BtnPressGuard300", "PressGuardNote" });
        MoveAllInto(screen, pages[3], new[] { "LangHead", "BtnLangJa", "BtnLangEn", "BtnLangKo", "BtnLangZhs", "BtnLangZht", "BtnLangAuto", "LangNote",
            "ChunHideHead", "BtnChunShowOthers", "BtnChunHideOthers", "ChunHideNote" });
        MoveAllInto(screen, pages[4], new[] { "YakuHead", "BtnYakuS", "BtnYakuM", "BtnYakuL", "BtnYakuHome", "YakuNote" });
        MoveAllInto(screen, pages[5], new[] { "BtnTableReset" });
        // 卓のボタンの押し方は 2026-10-08 に「対局」から「VR操作」へ移した（onClick の配線ごと移す）。
        MoveAllInto(pages[1], pages[2], new[] { "RowTouchInput", "TouchInputHead", "BtnTouchInputLaser", "BtnTouchInputTouch", "TouchInputNote" });

        // --- 対局 ---
        // 見出しと説明は RenderWorld が T() で毎回書く（ここの文言はエディタで見たとき用）。
        var play = pages[1];
        WorldRow(play, "RowTsumogiri", 0);
        WorldHead(play, "TsumogiriHead", "立直後のツモ切り", 0);
        WorldChoices(play, new[] { "BtnReachTsumogiriOn", "BtnReachTsumogiriOff" }, new[] { "ON", "OFF" }, 0, 230f);
        WorldNote(play, "TsumogiriNote", "立直すると自動でツモ切りになります。立直中も卓のボタンでOFFへ戻せます。", 0);

        WorldRow(play, "RowPressGuard", 1);
        WorldHead(play, "PressGuardHead", "押した直後の連打防止", 1);
        WorldChoices(play, new[] { "BtnPressGuard50", "BtnPressGuard100", "BtnPressGuard200", "BtnPressGuard300" },
            new[] { "0.05秒", "0.1秒", "0.2秒", "0.3秒" }, 1, 180f);
        WorldNote(play, "PressGuardNote", "卓のボタンを押した直後、次の押下を受け付けない時間。短いほど速く反応します。", 1);

        // --- VR操作（どちらも VR のみ。デスクトップでは RenderWorld が行ごと隠し、VrOnlyNote を出す）---
        var vr = pages[2];
        // 卓のボタンの押し方。説明は注意書きを兼ね、選択中の方式と指の骨の有無で RenderWorld が書き換える。
        WorldRow(vr, "RowTouchInput", 0);
        WorldHead(vr, "TouchInputHead", "卓のボタンの押し方", 0);
        WorldChoices(vr, new[] { "BtnTouchInputLaser", "BtnTouchInputTouch" }, new[] { "レーザー", "タッチ" }, 0, 230f);
        WorldNote(vr, "TouchInputNote", "レーザーで指して押します。「タッチ」にすると、鳴き・立直・和了などのボタンを指で奥へ押し込んで押せます（VRのみ）。", 0);

        // 牌を掴んで打牌（既定OFF）。実体は卓ごとの MahjongTileGrab（Build Tile Grab）。
        WorldRow(vr, "RowTileGrab", 1);
        WorldHead(vr, "TileGrabHead", "牌を掴んで打牌", 1);
        WorldChoices(vr, new[] { "BtnTileGrabOn", "BtnTileGrabOff" }, new[] { "ON", "OFF" }, 1, 230f);
        WorldNote(vr, "TileGrabNote", "自分の手番に牌をグリップで掴み、牌山まで出して離すと打牌します。北抜きはボタンの後に北を同じように出します。掴んでいる間は持ち時間が止まります（1手につき15秒まで）。", 1);

        // デスクトップの人向けの案内（行が全部隠れて空白になるため）。
        WorldNote(vr, "VrOnlyNote", "このページの設定は VR で遊ぶときだけ使えます。", 0);
        var vrOnly = vr.Find("VrOnlyNote");
        var mid = new Vector2(0.5f, 0.5f);
        SetRect(vrOnly, mid, mid, mid, new Vector2(WorldPaneX, WorldRowY[0]), new Vector2(WorldRowW - WorldPad * 2f, 90f));

        // --- 表示・演出 ---
        var disp = pages[3];
        WorldRow(disp, "RowLang", 0);
        WorldHead(disp, "LangHead", "表示言語", 0);
        // 言語ボタンは「その言語で」書く（雀魂・VRChat本体と同じ作法）。
        // 6つ並べると見出しの右に1つ120pxしか取れないので、この行だけ字を28ptに下げる（「简体中文」4文字が収まる大きさ）。
        WorldChoices(disp, new[] { "BtnLangJa", "BtnLangEn", "BtnLangKo", "BtnLangZhs", "BtnLangZht", "BtnLangAuto" },
            new[] { "日本語", "English", "한국어", "简体中文", "繁體中文", "自動" }, 0, 120f, 28f);
        WorldNote(disp, "LangNote", "選んだ言語はプロフィールに保存されます。「自動」に戻すと VRChat 本体の表示言語に合わせます。", 0);

        WorldRow(disp, "RowChun", 1);
        WorldHead(disp, "ChunHideHead", "他人のチュンビーム", 1);
        WorldChoices(disp, new[] { "BtnChunShowOthers", "BtnChunHideOthers" }, new[] { "表示", "非表示" }, 1, 230f);
        WorldNote(disp, "ChunHideNote", "中を捨てたときのチュンビームを全員ぶん表示します。", 1);

        // --- 役の早見板 ---
        // 札だけここで作る。見出し・ボタン・説明は BuildYakuGuide が同じ座標（World* 定数）で置く。
        WorldRow(pages[4], "RowYakuSize", 0);
        WorldRow(pages[4], "RowYakuHome", 1);

        // --- スタッフ（卓の強制リセットへの入口）---
        var staff = pages[5];
        WorldRow(staff, "RowStaff", 0);
        WorldHead(staff, "StaffHead", "卓の強制リセット", 0);
        WorldChoices(staff, new[] { "BtnTableReset" }, new[] { "開く" }, 0, 260f);
        WorldNote(staff, "StaffNote", "卓が詰まって進まないときに、卓を初期状態へ戻します。スタッフとインスタンスマスターだけが使えます。", 0);
        // 権限が無い人には出さない。出し入れは MainMenuController.RenderWorld が行う。
        staff.Find("BtnTableReset").gameObject.SetActive(false);

        // 札は奥（先頭の兄弟）へ。後から付け替えた部品より手前に来ると、見えてもクリックを奪う。
        // 並べ替えながら添え字で走ると要素を飛ばすので、先に札だけ拾ってから動かす。
        foreach (var page in pages)
        {
            var rows = new System.Collections.Generic.List<Transform>();
            foreach (Transform c in page) { if (c.name.StartsWith("Row")) { rows.Add(c); } }
            for (int i = rows.Count - 1; i >= 0; --i) { rows[i].SetAsFirstSibling(); }
        }

        // 旧「他の卓の声」の切替を掃除する（2026-10-04 撤去。卓ごとの声の振り分けをやめ、
        // 観戦ルーム（Carpet_01）の声だけ外へ漏らさない仕組みに変えた）。
        DestroyIfExists(screen, "OtherTableVoiceHead");
        DestroyIfExists(screen, "BtnOtherTableVoiceOn");
        DestroyIfExists(screen, "BtnOtherTableVoiceOff");

        // 旧「頭上プレートのプレビュー」の置き土産を掃除する（2026-09-09 撤去）。
        DestroyIfExists(screen, "NameplateHead");
        DestroyIfExists(screen, "BtnNameplatePreview");
    }

    static void MoveAllInto(Transform screen, Transform page, string[] names)
    {
        foreach (var name in names)
        {
            var t = screen.Find(name);
            if (t == null || page.Find(name) != null) { continue; }
            Undo.SetTransformParent(t, page, "Move World Setting Into Page");
        }
    }

    // 1行の白い札。
    static void WorldRow(Transform page, string name, int row)
    {
        var t = Child(page, name);
        var c = new Vector2(0.5f, 0.5f);
        SetRect(t, c, c, c, new Vector2(WorldPaneX, WorldRowY[row]), new Vector2(WorldRowW, WorldRowH));
        var image = EnsureImage(t, StyleMainMenu.White);
        image.color = StyleMainMenu.White;
        image.raycastTarget = false;
        StyleMainMenu.Frame(image, 3f, 8f);
    }

    // 札の左に置く見出し。英訳で長くなっても枠に収まるよう自動縮小させる。
    static void WorldHead(Transform page, string name, string text, int row)
    {
        var t = Child(page, name);
        var c = new Vector2(0.5f, 0.5f);
        SetRect(t, c, c, c, new Vector2(WorldRowLeft + WorldPad + WorldHeadW * 0.5f, WorldRowY[row] + WorldHeadDY),
            new Vector2(WorldHeadW, 80f));
        var tmp = EnsureTmp(t, text, 40f, TextAlignmentOptions.MidlineLeft);
        tmp.text = text;
        tmp.alignment = TextAlignmentOptions.MidlineLeft;
        tmp.enableWordWrapping = false;
        tmp.enableAutoSizing = true;
        tmp.fontSizeMax = 40f;
        tmp.fontSizeMin = 24f;
        EditorUtility.SetDirty(tmp);
    }

    // 札の下段の説明（札の幅いっぱい・2行まで）。
    static void WorldNote(Transform page, string name, string text, int row)
    {
        var t = Child(page, name);
        var c = new Vector2(0.5f, 0.5f);
        SetRect(t, c, c, c, new Vector2(WorldPaneX, WorldRowY[row] + WorldNoteDY),
            new Vector2(WorldRowW - WorldPad * 2f, 90f));
        var tmp = EnsureTmp(t, text, 26f, TextAlignmentOptions.TopLeft);
        tmp.text = text;
        tmp.fontSize = 26f;
        tmp.enableAutoSizing = false;
        tmp.alignment = TextAlignmentOptions.TopLeft;
        tmp.enableWordWrapping = true;
        EditorUtility.SetDirty(tmp);
    }

    // 札の右に右詰めで並べる選択肢。
    static void WorldChoices(Transform page, string[] names, string[] labels, int row, float w, float fontSize = 36f)
    {
        var c = new Vector2(0.5f, 0.5f);
        int n = names.Length;
        for (int i = 0; i < n; ++i)
        {
            float x = WorldRowRight - WorldPad - w * 0.5f - (n - 1 - i) * (w + WorldChoiceGap);
            EnsureButton(page, names[i], labels[i], c, c, new Vector2(x, WorldRowY[row] + WorldHeadDY), new Vector2(w, 96f), fontSize);
            SetLabelSize(page.Find(names[i]), fontSize);
            // 旧レイアウトから移したボタンは文言が古いままなので入れ直す（Wire Language はこの文字列を日本語として採る）。
            var label = page.Find(names[i] + "/Label").GetComponent<TextMeshProUGUI>();
            if (label.text != labels[i]) { label.text = labels[i]; EditorUtility.SetDirty(label); }
        }
    }

    // EnsureButton は新規作成時しか文字の大きさを入れないので、作り直さずに揃えたい分はここで。
    static void SetLabelSize(Transform button, float size)
    {
        var label = button != null ? button.Find("Label") : null;
        var tmp = label != null ? label.GetComponent<TextMeshProUGUI>() : null;
        if (tmp == null) { return; }
        tmp.fontSize = size;
        tmp.enableAutoSizing = false;
        tmp.enableWordWrapping = false;
        EditorUtility.SetDirty(tmp);
    }

    // 卓の強制リセット画面（スタッフ／インスタンスマスター専用）。
    //
    // カウンター裏のスタッフパネルと同じ機能を、見えない壁の外からも使えるようにするための画面。
    // スタッフ名簿は3名しか無く、全員不在のインスタンスで卓が詰まると誰も直せないが、
    // インスタンスマスターは必ず1人存在する。
    //
    // ScreenWorld は言語・役種一覧板・音量パネルで埋まっているので、押し込まず別画面にする。
    // フリー対局の卓（部屋）一覧。
    //
    // 部屋＝卓へ変えたので、フリー対局は必ずここで卓を選んでから始める。
    // 1枚のカード＝1卓で、状態（空き／設定中／募集中 n/m ＋代表名／満員／使用中）を出す。
    // カードそのものがボタン。押したときの意味は状態で変わる（作る／入る／理由を出す）。
    static void BuildScreenFreeList(Transform root)
    {
        var screen = Screen(root, "ScreenFreeList", false);

        EnsureButton(screen, "BtnBack", "もどる",
            new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(40f, -40f), new Vector2(180f, 90f), 32f);

        var title = Child(screen, "Title");
        SetRect(title, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0f, -50f), new Vector2(900f, 90f));
        EnsureTmp(title, "卓をえらぶ", 56f, TextAlignmentOptions.Center);

        // 2列×4行。カード 820x170、行間 180、列は ±430。
        // 上端はタイトルの下端(400)より下、下端は説明文(-450)より上に収める。
        for (int i = 0; i < 8; ++i)
        {
            float x = (i % 2 == 0) ? -430f : 430f;
            float y = 260f - (i / 2) * 180f;
            EnsureButton(screen, "FreeCard" + i, "",
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(x, y), new Vector2(820f, 170f), 30f);

            var card = screen.Find("FreeCard" + i);

            // カード内の「卓N」と状態行。EnsureButton が作る Label は使わない
            // （1行しか置けないので、見出しと状態を別の TMP に分ける）。
            var label = card.Find("Label");
            if (label != null) { label.gameObject.SetActive(false); }

            var name = Child(card, "Name");
            SetRect(name, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(30f, 0f), new Vector2(240f, 120f));
            EnsureTmp(name, "卓 " + (i + 1), 40f, TextAlignmentOptions.Left);

            var info = Child(card, "Info");
            SetRect(info, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(-30f, 0f), new Vector2(520f, 130f));
            var infoTmp = EnsureTmp(info, "", 28f, TextAlignmentOptions.Right);
            infoTmp.enableWordWrapping = false;
        }

        var note = Child(screen, "Note");
        SetRect(note, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
            new Vector2(0f, -450f), new Vector2(1600f, 120f));
        var noteTmp = EnsureTmp(note, "", 28f, TextAlignmentOptions.Center);
        noteTmp.enableWordWrapping = true;
    }

    static void BuildScreenTableReset(Transform root)
    {
        var screen = Screen(root, "ScreenTableReset", false);

        EnsureButton(screen, "BtnBack", "もどる",
            new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(40f, -40f), new Vector2(180f, 90f), 32f);

        var title = Child(screen, "Title");
        SetRect(title, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0f, -50f), new Vector2(900f, 90f));
        EnsureTmp(title, "卓の強制リセット", 56f, TextAlignmentOptions.Center);

        var note = Child(screen, "Note");
        SetRect(note, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
            new Vector2(0f, 305f), new Vector2(1600f, 170f));
        var noteTmp = EnsureTmp(note,
            "詰まって動かなくなった卓を、素の空卓へ戻します。",
            28f, TextAlignmentOptions.Center);
        noteTmp.enableWordWrapping = true;

        // 卓ボタンは 4列×2行。卓番号は卓インデックス+1（全体の前提）。
        for (int i = 0; i < 8; ++i)
        {
            float x = -660f + (i % 4) * 440f;
            float y = (i < 4) ? 130f : -50f;
            EnsureButton(screen, "BtnTable" + i, "卓 " + (i + 1),
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(x, y), new Vector2(420f, 150f), 34f);
        }

        var status = Child(screen, "Status");
        SetRect(status, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
            new Vector2(0f, -230f), new Vector2(1600f, 150f));
        var statusTmp = EnsureTmp(status, "", 30f, TextAlignmentOptions.Center);
        statusTmp.enableWordWrapping = true;

        EnsureButton(screen, "BtnExecute", "実行",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -400f), new Vector2(520f, 130f), 42f);
    }

    // 自画像の顔ガイドの寸法（撮影枠 420x630 / ビューファインダは内側8pxなので 404x614 の中の座標）。
    //  Y=195 … 既定の構図で目線が来る高さ。頭頂（耳・角を除く）は概ね +300 付近まで伸びる。
    //  180x200 … 耳の張り出しを含めた頭の見かけの大きさ（顔だけなら概ね 137x160）。
    const float SelfieFaceGuideY = 195f;
    const float SelfieFaceGuideW = 180f;
    const float SelfieFaceGuideH = 200f;

    // 顔ガイドの縁。四辺それぞれを細い板で描く（Image にアウトラインは無い）。
    static void FaceGuideEdge(Transform parent, string name, Vector2 anchor, Vector2 size)
    {
        var t = Child(parent, name);
        SetRect(t, anchor, anchor, anchor, Vector2.zero, size);
        var img = EnsureImage(t, new Color(1f, 1f, 1f, 0.55f));
        img.raycastTarget = false;
    }

    static void DestroyIfExists(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null) { Undo.DestroyObjectImmediate(t.gameObject); }
    }

    // 自画像の撮影画面。カード「自画像」を押すとここへ来て、3秒のセルフタイマーが走る。
    //  ・Viewfinder … 撮影カメラの liveRt をそのまま映す（カウントダウン中だけ表示）
    //  ・Preview    … 撮り終わった未確定の1枚（止めた liveRt）を映す（プレビュー中だけ表示）。photoRt は「決定」で焼く確定写真
    //  ・BtnRetake / BtnConfirm … プレビュー中だけ表示
    // 出し入れと文言は SelfiePortraitCamera / MainMenuController が実行時に行う。
    static void BuildScreenSelfie(Transform root)
    {
        var screen = Screen(root, "ScreenSelfie", false);

        EnsureButton(screen, "BtnBack", "もどる",
            new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(40f, -40f), new Vector2(180f, 90f), 32f);

        var title = Child(screen, "Title");
        SetRect(title, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0f, -50f), new Vector2(900f, 90f));
        EnsureTmp(title, "自画像をとる", 56f, TextAlignmentOptions.Center);

        // 撮影枠。写真の縦横比（512x768＝2:3）に合わせた 440x660。
        // 上はタイトル（下端 y=400）、下は案内文と重ならない高さに収めてある。
        var frame = Child(screen, "Frame");
        SetRect(frame, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
            new Vector2(0f, 30f), new Vector2(420f, 630f));
        EnsureImage(frame, new Color(0f, 0f, 0f, 0.55f));

        var viewfinder = Child(frame, "Viewfinder");
        StretchInside(viewfinder, 8f);
        var viewRaw = EnsureRawImage(viewfinder);
        viewRaw.color = Color.white;
        viewRaw.raycastTarget = false;

        var preview = Child(frame, "Preview");
        StretchInside(preview, 8f);
        var previewRaw = EnsureRawImage(preview);
        previewRaw.color = Color.white;
        previewRaw.raycastTarget = false;
        preview.gameObject.SetActive(false); // 初期はビューファインダ側

        // 顔の置き場所のガイド。
        // 既定の構図（距離1.4m／FOV35／注視点は頭の0.30m下）では目線が枠の中心ではなく
        // 上寄り（中心から +200px 付近）に来る。ここに何も出ていないと
        // 「枠の真ん中に顔を入れるもの」と勘違いされ、撮れた絵が下寄りになる。
        // そこで顔の位置へ薄暗い四角を1枚置き、ここへ顔を合わせてもらう。
        // 出し入れは SelfiePortraitCamera.ApplyStateToUi（構図合わせ＋カウントダウン中だけ）。
        var faceGuide = Child(frame, "FaceGuide");
        SetRect(faceGuide, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
            new Vector2(0f, SelfieFaceGuideY), new Vector2(SelfieFaceGuideW, SelfieFaceGuideH));
        var faceGuideImage = EnsureImage(faceGuide, new Color(0f, 0f, 0f, 0.22f));
        faceGuideImage.raycastTarget = false;
        // 四辺の細枠。薄暗いだけだと暗いアバターの上で境目が消えるので白い縁を足す。
        // 名前は Line*/Dot* を避ける（StyleMainMenu の一括塗りに拾われる）。
        FaceGuideEdge(faceGuide, "EdgeTop", new Vector2(0.5f, 1f), new Vector2(SelfieFaceGuideW, 3f));
        FaceGuideEdge(faceGuide, "EdgeBottom", new Vector2(0.5f, 0f), new Vector2(SelfieFaceGuideW, 3f));
        FaceGuideEdge(faceGuide, "EdgeLeft", new Vector2(0f, 0.5f), new Vector2(3f, SelfieFaceGuideH));
        FaceGuideEdge(faceGuide, "EdgeRight", new Vector2(1f, 0.5f), new Vector2(3f, SelfieFaceGuideH));

        // カウントダウンの数字は白なので、明るい被写体の上でも読めるよう暗い座布団を敷く。
        // （TMPのアウトラインはマテリアル差し替えが要るので、板を1枚置く方が安い）
        // この座布団は構図合わせ中には出さない。出していると枠の中心に暗い四角が居座り、
        // 顔ガイドと二重の的になって「真ん中に顔」の勘違いを生む。
        var countBack = Child(frame, "CountBack");
        SetRect(countBack, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
            new Vector2(0f, 0f), new Vector2(240f, 240f));
        var countBackImage = EnsureImage(countBack, new Color(0f, 0f, 0f, 0.45f));
        countBackImage.raycastTarget = false;

        var count = Child(frame, "CountLabel");
        SetRect(count, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
            new Vector2(0f, 0f), new Vector2(400f, 400f));
        // TMPのフォントサイズは300で頭打ちなので、これ以上大きくしたいときは
        // RectTransform のスケールで稼ぐこと（宣言演出のカットインと同じ制約）。
        EnsureTmp(count, "3", 260f, TextAlignmentOptions.Center);
        // CountBack より後ろに作られた場合でも数字が前に出るようにする。
        count.SetAsLastSibling();

        var info = Child(screen, "Info");
        // 案内文は2行になることがある（共有の注意書き）。高さに余裕を持たせる。
        SetRect(info, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
            new Vector2(0f, -360f), new Vector2(1160f, 96f));
        var infoTmp = EnsureTmp(info, "", 30f, TextAlignmentOptions.Center);
        infoTmp.fontSize = 30f;
        infoTmp.enableWordWrapping = true;

        EnsureButton(screen, "BtnRetake", "撮り直し",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-200f, -470f), new Vector2(320f, 96f), 40f);
        EnsureButton(screen, "BtnConfirm", "決定",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(200f, -470f), new Vector2(320f, 96f), 40f);

        // 「撮影」。構図合わせ中だけ出るので、撮り直し／決定と同じ行に重ねてよい
        //  （この画面はビューファインダとプレビューも同じ場所に重ねて出し分けている）。
        EnsureButton(screen, "BtnShoot", "撮影",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -470f), new Vector2(320f, 96f), 40f);

        // 構図調整。ケモノアバターは耳・角・盛り髪が既定の構図から上へはみ出して切れるので、
        // 撮る前に本人が上下とズームを合わせられるようにする。
        // 撮影枠は x=±210 なので、左右 x=±330 の列は枠に被らない（Canvas は 1920x1080）。
        var controls = Child(screen, "FrameControls");
        StretchInside(controls, 0f);   // Image を付けない＝クリックを吸わない入れ物

        EnsureButton(controls, "BtnFrameUp", "上へ",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-330f, 170f), new Vector2(200f, 96f), 34f);
        EnsureButton(controls, "BtnFrameDown", "下へ",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-330f, 55f), new Vector2(200f, 96f), 34f);
        EnsureButton(controls, "BtnZoomOut", "引く",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(330f, 170f), new Vector2(200f, 96f), 34f);
        EnsureButton(controls, "BtnZoomIn", "寄る",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(330f, 55f), new Vector2(200f, 96f), 34f);
        EnsureButton(controls, "BtnFrameReset", "もとに戻す",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-330f, -60f), new Vector2(200f, 96f), 28f);

        // 初期状態は構図合わせなので FrameControls と撮影は出しておく
        // （実行時は SelfiePortraitCamera.ApplyStateToUi が状態で出し分ける）。
        var retake = screen.Find("BtnRetake");
        if (retake != null) { retake.gameObject.SetActive(false); }
        var confirm = screen.Find("BtnConfirm");
        if (confirm != null) { confirm.gameObject.SetActive(false); }
    }

    // 親いっぱいに広げて内側へ inset だけ寄せる。
    static void StretchInside(Transform t, float inset)
    {
        var rt = (RectTransform)t;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(inset, inset);
        rt.offsetMax = new Vector2(-inset, -inset);
    }

    // 段位戦マッチングの待機画面（実装③）。文言は MainMenuController.RenderWaiting が書き換える。
    static void BuildScreenWaiting(Transform root)
    {
        var screen = Screen(root, "ScreenWaiting", false);

        var title = Child(screen, "WaitTitle");
        SetRect(title, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 160f), new Vector2(1400f, 110f));
        EnsureTmp(title, "対戦相手を待っています...", 64f, TextAlignmentOptions.Center);

        var info = Child(screen, "WaitInfo");
        SetRect(info, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 10f), new Vector2(1500f, 160f));
        var infoTmp = EnsureTmp(info, "", 40f, TextAlignmentOptions.Center);
        infoTmp.enableWordWrapping = true;

        // 顔ぶれ（待機中は先着順、成立後はメンバー）。行は四麻の4行分、三麻は4行目を「－」にする。
        // 内容は MainMenuController.RenderWaitingMembers が書き換える。
        var members = Child(screen, "WaitMemberList");
        SetRect(members, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -30f), new Vector2(900f, 400f));
        EnsureImage(members, new Color(1f, 1f, 1f, 1f));
        for (int i = 0; i < 4; ++i)
        {
            var row = Child(members, "WaitMember" + i);
            SetRect(row, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -22f - i * 92f), new Vector2(820f, 80f));
            EnsureTmp(row, "（募集中...）", 40f, TextAlignmentOptions.Center);
        }

        EnsureButton(screen, "BtnCancelWait", "キャンセル",
            new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 120f), new Vector2(420f, 120f), 40f);
    }

    // フリー対局戦の設定画面（代表のみ操作）。ラベルは MainMenuController が書き換える。
    static void BuildScreenFreeSetup(Transform root)
    {
        var screen = Screen(root, "ScreenFreeSetup", false);

        EnsureButton(screen, "BtnBack", "もどる",
            new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(40f, -40f), new Vector2(180f, 90f), 32f);

        var title = Child(screen, "Title");
        SetRect(title, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0f, -50f), new Vector2(900f, 90f));
        EnsureTmp(title, "フリー対局 設定", 56f, TextAlignmentOptions.Center);

        // 左列＝対局形式、右列＝特殊ルール。赤ドラ追加に合わせて5段へ詰める。
        // Title の下端が y=445、BtnFreeConfirm の上端が y=-420。
        // 5段を等間隔（ピッチ128・高さ100）で置くと上下に 28px ずつ空く。
        const float buttonW = 640f;
        const float buttonH = 100f;
        const float fontSize = 36f;
        float[] ys = { 256f, 128f, 0f, -128f, -256f };

        EnsureButton(screen, "BtnFreeMode", "モード：四麻",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-350f, ys[0]), new Vector2(buttonW, buttonH), fontSize);
        EnsureButton(screen, "BtnFreeTimer", "長考時間：8+30",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-350f, ys[1]), new Vector2(buttonW, buttonH), fontSize);
        EnsureButton(screen, "BtnFreeLength", "局数：半荘戦",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-350f, ys[2]), new Vector2(buttonW, buttonH), fontSize);
        EnsureButton(screen, "BtnFreeRedDora", "赤ドラ：3枚",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-350f, ys[3]), new Vector2(buttonW, buttonH), fontSize);
        EnsureButton(screen, "BtnFreeBust", "トビ：あり",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-350f, ys[4]), new Vector2(buttonW, buttonH), fontSize);

        EnsureButton(screen, "BtnFreeAotenjou", "青天井：なし",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(350f, ys[0]), new Vector2(buttonW, buttonH), fontSize);
        EnsureButton(screen, "BtnFreeLocalYaku", "ローカル役：なし",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(350f, ys[1]), new Vector2(buttonW, buttonH), fontSize);
        EnsureButton(screen, "BtnFreeOpenReach", "オープン立直：なし",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(350f, ys[2]), new Vector2(buttonW, buttonH), fontSize);
        EnsureButton(screen, "BtnFreeTsumoLoss", "ツモ損：あり",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(350f, ys[3]), new Vector2(buttonW, buttonH), fontSize);

        // 能力設定は仕様確定まで非表示で保持する。
        // 非表示のあいだ右列 ys[4] は空くので、CPUの強さをそこへ置く（座標が重なるのは承知の上）。
        EnsureButton(screen, "BtnFreeAbility", "能力：あり",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(350f, ys[4]), new Vector2(buttonW, buttonH), fontSize);
        var freeAbilityButton = screen.Find("BtnFreeAbility");
        if (freeAbilityButton != null) { freeAbilityButton.gameObject.SetActive(false); }

        EnsureButton(screen, "BtnFreeCpuLevel", "CPUの強さ：簡単",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(350f, ys[4]), new Vector2(buttonW, buttonH), fontSize);

        string[] staleTimerNames = { "ThinkNote", "BtnFreeTimer830", "BtnFreeTimer1560", "BtnFreeTimer60255" };
        for (int i = 0; i < staleTimerNames.Length; i++)
        {
            var staleTimer = screen.Find(staleTimerNames[i]);
            if (staleTimer != null) { Undo.DestroyObjectImmediate(staleTimer.gameObject); }
        }

        EnsureButton(screen, "BtnFreeConfirm", "決定（募集開始）",
            new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 60f), new Vector2(520f, 120f), 40f);

        // EnsureTmp はラベルを作るときしか fontSize を触らない。設定ボタンは
        // 増設のたびに既定値が変わってきたので、9個ぶんをここで揃え直す
        //（揃えないと後から足したボタンだけ文字が小さいまま残る）。
        string[] settingButtons =
        {
            "BtnFreeMode", "BtnFreeTimer", "BtnFreeLength", "BtnFreeRedDora", "BtnFreeBust",
            "BtnFreeAotenjou", "BtnFreeLocalYaku", "BtnFreeOpenReach", "BtnFreeTsumoLoss",
            "BtnFreeAbility", "BtnFreeCpuLevel",
        };
        for (int i = 0; i < settingButtons.Length; i++)
        {
            var label = screen.Find(settingButtons[i] + "/Label");
            if (label == null) { continue; }
            var labelTmp = label.GetComponent<TMPro.TextMeshProUGUI>();
            if (labelTmp != null) { labelTmp.fontSize = fontSize; }
        }
    }


    // フリー対局戦の部屋（募集中）画面。メンバー一覧と代表用の操作ボタン。
    static void BuildScreenFreeRoom(Transform root)
    {
        var screen = Screen(root, "ScreenFreeRoom", false);

        var title = Child(screen, "RoomTitle");
        SetRect(title, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -50f), new Vector2(1100f, 90f));
        EnsureTmp(title, "フリー対局 募集中", 56f, TextAlignmentOptions.Center);

        var info = Child(screen, "RoomInfo");
        SetRect(info, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -150f), new Vector2(1400f, 110f));
        var infoTmp = EnsureTmp(info, "", 34f, TextAlignmentOptions.Center);
        infoTmp.enableWordWrapping = true;

        // メンバー一覧（4行。三麻時は4行目「－」）。
        var list = Child(screen, "MemberList");
        SetRect(list, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-260f, -60f), new Vector2(760f, 480f));
        EnsureImage(list, new Color(0f, 0f, 0f, 0.25f));
        for (int i = 0; i < 4; ++i)
        {
            var row = Child(list, "Member" + i);
            SetRect(row, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -25f - i * 110f), new Vector2(700f, 90f));
            EnsureTmp(row, "（募集中...）", 40f, TextAlignmentOptions.Left);
        }

        // 代表用の操作（参加者には MainMenuController が非表示にする）。
        EnsureButton(screen, "BtnCpuAdd", "CPU追加",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(430f, 60f), new Vector2(420f, 110f), 36f);
        EnsureButton(screen, "BtnCpuRemove", "CPU削除",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(430f, -70f), new Vector2(420f, 110f), 36f);
        EnsureButton(screen, "BtnFreeStart", "開始",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(430f, -220f), new Vector2(420f, 130f), 44f);

        // ×（代表＝解散／参加者＝退出。ラベルはスクリプトが書き換え）。
        EnsureButton(screen, "BtnFreeClose", "退出",
            new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-40f, -40f), new Vector2(220f, 90f), 34f);
    }

    // フリー対局戦の入室確認画面（「（代表名）さんの部屋に入ります。よろしいですか？」）。
    static void BuildScreenFreeConfirm(Transform root)
    {
        var screen = Screen(root, "ScreenFreeConfirm", false);

        var text = Child(screen, "ConfirmText");
        SetRect(text, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 120f), new Vector2(1400f, 160f));
        var tmp = EnsureTmp(text, "部屋に入ります。よろしいですか？", 48f, TextAlignmentOptions.Center);
        tmp.enableWordWrapping = true;

        EnsureButton(screen, "BtnYes", "はい",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-240f, -120f), new Vector2(380f, 130f), 44f);
        EnsureButton(screen, "BtnNo", "いいえ",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(240f, -120f), new Vector2(380f, 130f), 44f);
    }

    // 対局中の案内画面。対局中の卓に座ったままメインメニューへ来た人にだけ出る。
    // 文言は MainMenuController.RenderInGame が言語に合わせて書き換える。
    static void BuildScreenInGame(Transform root)
    {
        var screen = Screen(root, "ScreenInGame", false);

        var title = Child(screen, "InGameTitle");
        SetRect(title, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -90f), new Vector2(1400f, 110f));
        EnsureTmp(title, "対局中", 64f, TextAlignmentOptions.Center);

        var text = Child(screen, "InGameText");
        SetRect(text, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0f, 60f), new Vector2(1500f, 500f));
        var tmp = EnsureTmp(text, "あなたは対局中です。", 44f, TextAlignmentOptions.Center);
        tmp.enableWordWrapping = true;

        EnsureButton(screen, "BtnAbandon", "途中退出する",
            new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 120f), new Vector2(560f, 130f), 44f);
    }

    // 途中退出の二段階確認。ペナルティの追記は MainMenuController が書き換える。
    static void BuildScreenAbandonConfirm(Transform root)
    {
        var screen = Screen(root, "ScreenAbandonConfirm", false);

        var text = Child(screen, "ConfirmText");
        SetRect(text, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0f, 120f), new Vector2(1400f, 300f));
        var tmp = EnsureTmp(text, "あなたは途中退出をします。よろしいですか？", 48f, TextAlignmentOptions.Center);
        tmp.enableWordWrapping = true;

        EnsureButton(screen, "BtnYes", "はい",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-240f, -160f), new Vector2(380f, 130f), 44f);
        EnsureButton(screen, "BtnNo", "いいえ",
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(240f, -160f), new Vector2(380f, 130f), 44f);
    }

    // ============================================================
    // 生成ヘルパ（同名があれば再利用＝重複生成防止）
    // ============================================================

    static Transform Screen(Transform root, string name, bool initialVisible)
    {
        var screen = Child(root, name);
        // 画面ルートは全面ストレッチ（Canvasが1920x1080）。
        var rt = (RectTransform)screen;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        screen.gameObject.SetActive(initialVisible);
        return screen;
    }

    static Transform Child(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null)
        {
            ++reused_;
            return t;
        }

        var go = new GameObject(name, typeof(RectTransform));
        Undo.RegisterCreatedObjectUndo(go, "Build Main Menu");
        go.transform.SetParent(parent, false);
        ++created_;
        return go.transform;
    }

    static void SetRect(Transform t, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        var rt = (RectTransform)t;
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        if (size.x > 0f || size.y > 0f)
        {
            rt.sizeDelta = size;
        }
    }

    static Image EnsureImage(Transform t, Color color)
    {
        var image = t.GetComponent<Image>();
        if (image == null)
        {
            image = Undo.AddComponent<Image>(t.gameObject);
            image.color = color;
        }
        return image;
    }

    static RawImage EnsureRawImage(Transform t)
    {
        var raw = t.GetComponent<RawImage>();
        if (raw == null)
        {
            raw = Undo.AddComponent<RawImage>(t.gameObject);
            raw.color = new Color(0.6f, 0.6f, 0.6f, 1f); // プレースホルダ
        }
        return raw;
    }

    static TextMeshProUGUI EnsureTmp(Transform t, string text, float size, TextAlignmentOptions alignment)
    {
        var tmp = t.GetComponent<TextMeshProUGUI>();
        if (tmp == null)
        {
            tmp = Undo.AddComponent<TextMeshProUGUI>(t.gameObject);
            tmp.text = text;
            tmp.fontSize = size;
            tmp.alignment = alignment;
            tmp.raycastTarget = false; // ラベルはレーザーを遮らない
        }

        // フォントだけは**既存の TMP にも毎回入れ直す**。
        // 文言やサイズは実行時に書き換わるので新規作成時しか触らないが、フォントを
        // 同じ扱いにすると事故が直せなくなる：日本語フォントアセットが読めない状態で
        // 一度 Build が走ると LiberationSans（TMPの既定）で TMP が作られ、以後どれだけ
        // Build し直しても直らず日本語が豆腐（□）のまま残る。実際に ScreenHome の
        // ラベル12個がその状態だった（2026-09-09）。
        if (font_ != null && tmp.font != font_)
        {
            tmp.font = font_;
            EditorUtility.SetDirty(tmp);
        }
        return tmp;
    }

    static void EnsureButton(Transform parent, string name, string label,
        Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size, float fontSize)
    {
        var t = Child(parent, name);
        SetRect(t, anchor, anchor, pivot, pos, size);
        var image = EnsureImage(t, new Color(0.2f, 0.25f, 0.4f, 0.95f));

        var button = t.GetComponent<Button>();
        if (button == null)
        {
            button = Undo.AddComponent<Button>(t.gameObject);
            button.targetGraphic = image;
            // onClick は WireMainMenu / 手動で割り当てるため空のまま。
        }

        var labelT = Child(t, "Label");
        SetRect(labelT, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        var rt = (RectTransform)labelT;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        EnsureTmp(labelT, label, fontSize, TextAlignmentOptions.Center);
    }

    // ============================================================
    // TMP 日本語フォントアセット（無ければ NotoSansJP-Bold から動的生成）
    // ============================================================

    static TMP_FontAsset GetOrCreateJpFontAsset()
    {
        var fa = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
        if (fa != null) { return fa; }

        var src = AssetDatabase.LoadAssetAtPath<Font>(FontSourcePath);
        if (src == null)
        {
            // ここで TMP の既定フォント（LiberationSans）へ落とすと、日本語を持たない
            // フォントで TMP が作られてしまい、後から Build し直しても直らない豆腐が
            // 残る。黙って劣化させず null を返して Build 自体を止める。
            Debug.LogError("[BuildMainMenu] 日本語フォントが用意できません: " + FontSourcePath
                           + "（TMPの既定フォントで作ると日本語が豆腐になり、後から直せません）");
            return null;
        }

        fa = TMP_FontAsset.CreateFontAsset(src);
        fa.name = "NotoSansJP-Bold SDF";
        AssetDatabase.CreateAsset(fa, FontAssetPath);

        // atlas と material はサブアセットとして保存しないと参照が失われる。
        fa.atlasTexture.name = fa.name + " Atlas";
        fa.material.name = fa.name + " Material";
        AssetDatabase.AddObjectToAsset(fa.atlasTexture, fa);
        AssetDatabase.AddObjectToAsset(fa.material, fa);
        AssetDatabase.SaveAssets();

        Debug.Log("[BuildMainMenu] TMPフォントアセットを生成: " + FontAssetPath +
                  "（Dynamic。Quest最適化時はStaticへの焼き込みを検討）");
        return fa;
    }

    // ============================================================
    // その他
    // ============================================================

    static void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform)
        {
            SetLayerRecursively(child.gameObject, layer);
        }
    }

    static string DumpHierarchy(Transform t, int depth)
    {
        var line = new string(' ', depth * 2) + t.name + "\n";
        foreach (Transform child in t)
        {
            line += DumpHierarchy(child, depth + 1);
        }
        return line;
    }
}
#endif
