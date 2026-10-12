#if UNITY_EDITOR
// MainMenuController / PlayerStatsStore のフィールド自動割り当てと
// uGUI Button onClick → UdonBehaviour.SendCustomEvent の配線を行う。
// BuildMainMenu で骨格を生成した後に実行する。2回実行しても安全（冪等）。
// 実行: メニュー Tools > Kemono Mahjong > Wire Main Menu
using TMPro;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using SuzuFactory.AozoraMahjong;
using VRC.Udon;

public static class WireMainMenu
{
    const string ProgramAssetDir = "Assets/Mahjong/scene_UdonProgramSources";
    // BuildMainMenu が作るコスメカードの枚数（実際に出す枚数はカタログ数で実行時に決まる）。
    // 卓の強制リセット画面の卓ボタン数。MatchmakingManager.MAX_TABLES と揃える。
    const int TableResetButtons = 8;
    // フリー対局の卓一覧のカード数。卓の数と揃える。
    const int FreeListCards = 8;
    const int CosmeticCards = 8;
    static readonly string[] RankIconPaths =
    {
        "Assets/Mahjong/Textures/UI/rank/初獣.png",
        "Assets/Mahjong/Textures/UI/rank/鋭爪.png",
        "Assets/Mahjong/Textures/UI/rank/剛牙.png",
        "Assets/Mahjong/Textures/UI/rank/獣威.png",
        "Assets/Mahjong/Textures/UI/rank/幻獣.png",
        "Assets/Mahjong/Textures/UI/rank/天獣.png"
    };
    static readonly string[] CharaPortraitPaths =
    {
        "Assets/Mahjong/Textures/UI/Characters/vol_mascot_pic.png",
        "Assets/Mahjong/Textures/UI/Characters/Shine_mascot_pic.png",
        "Assets/Mahjong/Textures/UI/Characters/Tsumuntema_mascot_pic.png"
    };

    [MenuItem("Tools/Kemono Mahjong/Wire Agari Mascots")]
    public static void WireAgariMascotsMenu()
    {
        var portraits = LoadCharaPortraits();
        int wired = 0;
        foreach (var mahjong in Object.FindObjectsOfType<Mahjong>(true))
        {
            wired += WireAgariMascot(mahjong, portraits);
        }

        if (wired == 0)
        {
            Debug.LogWarning("[WireAgariMascots] No AgariScreen/MascotImage pair was found in the current editing context.");
            return;
        }

        Debug.Log("[WireAgariMascots] Wired " + wired + " AgariScreen mascot image(s). Save the current scene or prefab stage to persist the changes.");
    }

    [MenuItem("Tools/Kemono Mahjong/Wire Main Menu")]
    public static void Run()
    {
        var mainMenu = GameObject.Find("MainMenu");
        if (mainMenu == null)
        {
            Debug.LogError("[WireMainMenu] シーンに 'MainMenu' が見つかりません。先に Build Main Menu を実行してください。");
            return;
        }

        var root = mainMenu.transform;
        if (root.Find("ScreenHome") == null)
        {
            Debug.LogError("[WireMainMenu] ScreenHome がありません。先に Build Main Menu を実行してください。");
            return;
        }

        // --- U# プログラムアセット（.cs 直接作成のスクリプトには存在しないため先に生成） ---
        bool createdProgramAsset = false;
        createdProgramAsset |= EnsureProgramAsset(
            "Assets/Mahjong/Sources/PlayerStatsStore.cs",
            ProgramAssetDir + "/PlayerStatsStore Udon C# Program Asset.asset");
        createdProgramAsset |= EnsureProgramAsset(
            "Assets/Mahjong/Sources/MainMenuController.cs",
            ProgramAssetDir + "/MainMenuController Udon C# Program Asset.asset");
        createdProgramAsset |= EnsureProgramAsset(
            "Assets/Mahjong/Sources/MahjongVoiceSelector.cs",
            ProgramAssetDir + "/MahjongVoiceSelector Udon C# Program Asset.asset");
        createdProgramAsset |= EnsureProgramAsset(
            "Assets/Mahjong/Sources/SelfiePortraitCamera.cs",
            ProgramAssetDir + "/SelfiePortraitCamera Udon C# Program Asset.asset");
        createdProgramAsset |= EnsureProgramAsset(
            "Assets/Mahjong/Sources/SelfiePortraitStore.cs",
            ProgramAssetDir + "/SelfiePortraitStore Udon C# Program Asset.asset");
        createdProgramAsset |= EnsureProgramAsset(
            "Assets/Mahjong/Sources/MenuButtonPressFx.cs",
            ProgramAssetDir + "/MenuButtonPressFx Udon C# Program Asset.asset");
        createdProgramAsset |= EnsureProgramAsset(
            "Assets/Mahjong/Sources/MenuButtonPress.cs",
            ProgramAssetDir + "/MenuButtonPress Udon C# Program Asset.asset");

        if (createdProgramAsset)
        {
            // 生成直後はU#コンパイルが必要。コンパイル完了後にもう一度このメニューを実行する。
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[WireMainMenu] U#プログラムアセットを生成しました。コンパイル完了後、もう一度 Wire Main Menu を実行してください。");
            return;
        }

        // --- PlayerStatsStore（シーンに無ければ MainMenu に追加。壊れた残骸は再生成） ---
        var store = (PlayerStatsStore)EnsureUSharpComponent(mainMenu, typeof(PlayerStatsStore), true);
        if (store == null)
        {
            Debug.LogError("[WireMainMenu] PlayerStatsStore を追加できませんでした。");
            return;
        }

        // --- MainMenuController（MainMenu に無ければ追加） ---
        var controller = (MainMenuController)EnsureUSharpComponent(mainMenu, typeof(MainMenuController), false);
        if (controller == null)
        {
            Debug.LogError("[WireMainMenu] MainMenuController を追加できませんでした。");
            return;
        }

        // --- ボイス選択（PlayerData復元待ちを持つため、卓内ではなく常時有効なMainMenuに1個置く） ---
        var voiceSelector = (MahjongVoiceSelector)EnsureUSharpComponent(mainMenu, typeof(MahjongVoiceSelector), false);
        if (voiceSelector == null)
        {
            Debug.LogError("[WireMainMenu] MahjongVoiceSelector をMainMenuへ追加できませんでした。");
            return;
        }

        // --- フィールド割り当て ---
        controller.statsStore = store;
        controller.voiceSelector = voiceSelector;
        controller.screenHome = Find(root, "ScreenHome").gameObject;
        controller.screenProfile = Find(root, "ScreenProfile").gameObject;
        controller.screenChara = Find(root, "ScreenChara").gameObject;

        // 自画像の撮影画面（Build Main Menu 再実行で生成される）。
        var screenSelfie = Find(root, "ScreenSelfie");
        controller.screenSelfie = screenSelfie != null ? screenSelfie.gameObject : null;

        // 待機画面（実装③。Build Main Menu 再実行で生成される）。
        var screenWaiting = Find(root, "ScreenWaiting");
        controller.screenWaiting = screenWaiting != null ? screenWaiting.gameObject : null;
        controller.waitingTitle = FindTmp(root, "ScreenWaiting/WaitTitle");
        controller.waitingInfo = FindTmp(root, "ScreenWaiting/WaitInfo");
        var cancelWait = Find(root, "ScreenWaiting/BtnCancelWait");
        controller.btnCancelWait = cancelWait != null ? cancelWait.gameObject : null;

        // 待機画面の顔ぶれ（待機中は先着順、成立後はメンバー）。
        var waitMemberList = Find(root, "ScreenWaiting/WaitMemberList");
        controller.waitingMemberList = waitMemberList != null ? waitMemberList.gameObject : null;
        var waitMemberTexts = new TextMeshProUGUI[4];
        for (int i = 0; i < waitMemberTexts.Length; ++i)
        {
            waitMemberTexts[i] = FindTmp(root, "ScreenWaiting/WaitMemberList/WaitMember" + i);
        }
        controller.waitingMemberTexts = waitMemberTexts;

        // 段位戦マッチング（実装③。Wire Nameplates 実行後にシーンに存在する）。
        var matchmaking = Object.FindObjectOfType<MatchmakingManager>(true);
        if (matchmaking == null)
        {
            Debug.LogWarning("[WireMainMenu] MatchmakingManager が見つかりません。先に Build/Wire Nameplates を実行してください（段位戦・フリー対局ボタンは動作しません）。");
        }
        controller.matchmaking = matchmaking;

        // フリー対局戦（実装④）。
        var btnFreeT = Find(root, "ScreenHome/BtnFree");
        controller.btnFree = btnFreeT != null ? btnFreeT.GetComponent<Button>() : null;
        controller.btnFreeLabel = FindTmp(root, "ScreenHome/BtnFree/Label");
        controller.homeInfo = FindTmp(root, "ScreenHome/HomeInfo");

        // 段位戦ボタン（スタッフパネルの列開閉で SetActive する）。
        // Button ではなく GameObject を持つ＝閉じた列はボタンごと消える。
        controller.btnRanked4Han = FindGo(root, "ScreenHome/BtnRanked4Han");
        controller.btnRanked4Ton = FindGo(root, "ScreenHome/BtnRanked4Ton");
        controller.btnRanked3Han = FindGo(root, "ScreenHome/BtnRanked3Han");
        controller.btnRanked3Ton = FindGo(root, "ScreenHome/BtnRanked3Ton");

        // 待機状況の行と発光枠。並びは 四麻半荘 / 四麻東風 / 三麻半荘 / 三麻東風 / フリー対局。
        // 実体は StyleMainMenu が Build のたびに作り直すので、ここは拾うだけ。
        string[] homeQueueButtons =
        {
            "BtnRanked4Han", "BtnRanked4Ton", "BtnRanked3Han", "BtnRanked3Ton", "BtnFree"
        };
        var queueInfos = new TextMeshProUGUI[homeQueueButtons.Length];
        var queueGlows = new GameObject[homeQueueButtons.Length];
        for (int qi = 0; qi < homeQueueButtons.Length; ++qi)
        {
            queueInfos[qi] = FindTmp(root, "ScreenHome/" + homeQueueButtons[qi] + "/QueueInfo");
            queueGlows[qi] = FindGo(root, "ScreenHome/" + homeQueueButtons[qi] + "/Glow");
        }
        controller.queueInfoTexts = queueInfos;
        controller.queueGlows = queueGlows;

        var screenFreeSetup = Find(root, "ScreenFreeSetup");
        controller.screenFreeSetup = screenFreeSetup != null ? screenFreeSetup.gameObject : null;
        controller.freeModeLabel = FindTmp(root, "ScreenFreeSetup/BtnFreeMode/Label");
        controller.freeAbilityLabel = FindTmp(root, "ScreenFreeSetup/BtnFreeAbility/Label");
        controller.freeLengthLabel = FindTmp(root, "ScreenFreeSetup/BtnFreeLength/Label");
        controller.freeTimerLabel = FindTmp(root, "ScreenFreeSetup/BtnFreeTimer/Label");
        controller.freeRedDoraLabel = FindTmp(root, "ScreenFreeSetup/BtnFreeRedDora/Label");
        controller.freeCpuLevelLabel = FindTmp(root, "ScreenFreeSetup/BtnFreeCpuLevel/Label");
        controller.freeAotenjouLabel = FindTmp(root, "ScreenFreeSetup/BtnFreeAotenjou/Label");
        controller.freeLocalYakuLabel = FindTmp(root, "ScreenFreeSetup/BtnFreeLocalYaku/Label");
        controller.freeOpenReachLabel = FindTmp(root, "ScreenFreeSetup/BtnFreeOpenReach/Label");
        controller.freeTsumoLossLabel = FindTmp(root, "ScreenFreeSetup/BtnFreeTsumoLoss/Label");
        controller.freeBustLabel = FindTmp(root, "ScreenFreeSetup/BtnFreeBust/Label");

        var screenFreeRoom = Find(root, "ScreenFreeRoom");
        controller.screenFreeRoom = screenFreeRoom != null ? screenFreeRoom.gameObject : null;
        controller.freeRoomTitle = FindTmp(root, "ScreenFreeRoom/RoomTitle");
        controller.freeRoomInfo = FindTmp(root, "ScreenFreeRoom/RoomInfo");
        var memberTexts = new TextMeshProUGUI[4];
        for (int i = 0; i < 4; ++i)
        {
            memberTexts[i] = FindTmp(root, "ScreenFreeRoom/MemberList/Member" + i);
        }
        controller.freeMemberTexts = memberTexts;
        var cpuAdd = Find(root, "ScreenFreeRoom/BtnCpuAdd");
        controller.btnFreeCpuAdd = cpuAdd != null ? cpuAdd.gameObject : null;
        var cpuRemove = Find(root, "ScreenFreeRoom/BtnCpuRemove");
        controller.btnFreeCpuRemove = cpuRemove != null ? cpuRemove.gameObject : null;
        var freeStart = Find(root, "ScreenFreeRoom/BtnFreeStart");
        controller.btnFreeStart = freeStart != null ? freeStart.gameObject : null;
        controller.freeCloseLabel = FindTmp(root, "ScreenFreeRoom/BtnFreeClose/Label");

        var screenFreeConfirm = Find(root, "ScreenFreeConfirm");
        controller.screenFreeConfirm = screenFreeConfirm != null ? screenFreeConfirm.gameObject : null;
        controller.freeConfirmText = FindTmp(root, "ScreenFreeConfirm/ConfirmText");

        // --- フリー対局の卓（部屋）一覧 ---
        var screenFreeList = Find(root, "ScreenFreeList");
        controller.screenFreeList = screenFreeList != null ? screenFreeList.gameObject : null;
        if (screenFreeList == null)
        {
            Debug.LogWarning("[WireMainMenu] ScreenFreeList がありません。Build Main Menu を再実行してください"
                + "（フリー対局は卓おまかせのままになります）。");
        }
        else
        {
            controller.freeListTitle = FindTmp(root, "ScreenFreeList/Title");
            controller.freeListNote = FindTmp(root, "ScreenFreeList/Note");
            controller.freeListBackLabel = FindTmp(root, "ScreenFreeList/BtnBack/Label");

            var listNames = new TextMeshProUGUI[FreeListCards];
            var listInfos = new TextMeshProUGUI[FreeListCards];
            var listImages = new Image[FreeListCards];
            for (int i = 0; i < FreeListCards; ++i)
            {
                listNames[i] = FindTmp(root, "ScreenFreeList/FreeCard" + i + "/Name");
                listInfos[i] = FindTmp(root, "ScreenFreeList/FreeCard" + i + "/Info");
                listImages[i] = FindImage(root, "ScreenFreeList/FreeCard" + i);
            }
            controller.freeListNames = listNames;
            controller.freeListInfos = listInfos;
            controller.freeListImages = listImages;
        }

        // --- 対局中の案内 と 途中退出（Build Main Menu で生成済みの前提） ---
        var screenInGame = Find(root, "ScreenInGame");
        controller.screenInGame = screenInGame != null ? screenInGame.gameObject : null;
        controller.inGameText = FindTmp(root, "ScreenInGame/InGameText");
        var btnAbandon = Find(root, "ScreenInGame/BtnAbandon");
        controller.btnAbandon = btnAbandon != null ? btnAbandon.gameObject : null;

        var screenAbandonConfirm = Find(root, "ScreenAbandonConfirm");
        controller.screenAbandonConfirm = screenAbandonConfirm != null ? screenAbandonConfirm.gameObject : null;
        controller.abandonConfirmText = FindTmp(root, "ScreenAbandonConfirm/ConfirmText");

        // --- 装飾品（和了演出コスメ）。Build Main Menu で ScreenCosmetic が生成済みの前提 ---
        var screenCosmetic = Find(root, "ScreenCosmetic");
        // ワールド設定（雀魂式：左に項目タブ、右にそのページ）。KemonoLanguage 本体の割当ては Wire Language が行う。
        var screenWorld = root.Find("ScreenWorld");
        controller.screenWorld = screenWorld != null ? screenWorld.gameObject : null;

        // ページとタブは BuildMainMenu.WorldPageNames / WorldTabNames と同じ並び。
        int worldTabCount = BuildMainMenu.WorldPageNames.Length;
        var worldPages = new GameObject[worldTabCount];
        var worldTabImages = new UnityEngine.UI.Image[worldTabCount];
        for (int i = 0; i < worldTabCount; ++i)
        {
            worldPages[i] = FindGo(root, "ScreenWorld/" + BuildMainMenu.WorldPageNames[i]);
            worldTabImages[i] = FindImage(root, "ScreenWorld/" + BuildMainMenu.WorldTabNames[i]);
        }
        controller.worldPages = worldPages;
        controller.worldTabImages = worldTabImages;
        controller.worldTabStaff = FindGo(root, "ScreenWorld/TabStaff");

        // 並びは MainMenuController.RenderWorld の selected と対応（韓国語は後から足したので末尾）。
        var langImages = new UnityEngine.UI.Image[6];
        langImages[0] = FindImage(root, "ScreenWorld/PageDisplay/BtnLangJa");
        langImages[1] = FindImage(root, "ScreenWorld/PageDisplay/BtnLangEn");
        langImages[2] = FindImage(root, "ScreenWorld/PageDisplay/BtnLangAuto");
        langImages[3] = FindImage(root, "ScreenWorld/PageDisplay/BtnLangKo");
        langImages[4] = FindImage(root, "ScreenWorld/PageDisplay/BtnLangZhs");
        langImages[5] = FindImage(root, "ScreenWorld/PageDisplay/BtnLangZht");
        controller.langButtonImages = langImages;
        controller.worldLangNote = FindTmp(root, "ScreenWorld/PageDisplay/LangNote");

        // 立直後のツモ切り（純ローカル設定）。見出し・説明文は RenderWorld が T() で書く。
        var reachTsumogiriImages = new UnityEngine.UI.Image[2];
        reachTsumogiriImages[0] = FindImage(root, "ScreenWorld/PagePlay/BtnReachTsumogiriOn");
        reachTsumogiriImages[1] = FindImage(root, "ScreenWorld/PagePlay/BtnReachTsumogiriOff");
        controller.reachTsumogiriButtonImages = reachTsumogiriImages;
        controller.reachTsumogiriHead = FindTmp(root, "ScreenWorld/PagePlay/TsumogiriHead");
        controller.reachTsumogiriNote = FindTmp(root, "ScreenWorld/PagePlay/TsumogiriNote");

        // 押した直後の連打防止（純ローカル設定）。0.05 / 0.1 / 0.2 / 0.3 秒の順。
        var pressGuardImages = new UnityEngine.UI.Image[4];
        pressGuardImages[0] = FindImage(root, "ScreenWorld/PagePlay/BtnPressGuard50");
        pressGuardImages[1] = FindImage(root, "ScreenWorld/PagePlay/BtnPressGuard100");
        pressGuardImages[2] = FindImage(root, "ScreenWorld/PagePlay/BtnPressGuard200");
        pressGuardImages[3] = FindImage(root, "ScreenWorld/PagePlay/BtnPressGuard300");
        controller.pressGuardButtonImages = pressGuardImages;
        controller.pressGuardHead = FindTmp(root, "ScreenWorld/PagePlay/PressGuardHead");
        controller.pressGuardNote = FindTmp(root, "ScreenWorld/PagePlay/PressGuardNote");

        // 卓のボタンの押し方（純ローカル設定）。0=レーザー / 1=タッチ。行の部品はデスクトップで隠す。
        var touchImages = new UnityEngine.UI.Image[2];
        touchImages[0] = FindImage(root, "ScreenWorld/PageVr/BtnTouchInputLaser");
        touchImages[1] = FindImage(root, "ScreenWorld/PageVr/BtnTouchInputTouch");
        controller.touchInputButtonImages = touchImages;
        controller.touchInputHead = FindTmp(root, "ScreenWorld/PageVr/TouchInputHead");
        controller.touchInputNote = FindTmp(root, "ScreenWorld/PageVr/TouchInputNote");
        controller.touchInputRowParts = new GameObject[]
        {
            FindGo(root, "ScreenWorld/PageVr/RowTouchInput"),
            FindGo(root, "ScreenWorld/PageVr/TouchInputHead"),
            FindGo(root, "ScreenWorld/PageVr/BtnTouchInputLaser"),
            FindGo(root, "ScreenWorld/PageVr/BtnTouchInputTouch"),
            FindGo(root, "ScreenWorld/PageVr/TouchInputNote"),
        };

        // 牌を掴んで打牌（純ローカル設定・VRのみ）。0=ON / 1=OFF。行の部品はデスクトップで隠す。
        var tileGrabImages = new UnityEngine.UI.Image[2];
        tileGrabImages[0] = FindImage(root, "ScreenWorld/PageVr/BtnTileGrabOn");
        tileGrabImages[1] = FindImage(root, "ScreenWorld/PageVr/BtnTileGrabOff");
        controller.tileGrabButtonImages = tileGrabImages;
        controller.tileGrabHead = FindTmp(root, "ScreenWorld/PageVr/TileGrabHead");
        controller.tileGrabNote = FindTmp(root, "ScreenWorld/PageVr/TileGrabNote");
        controller.tileGrabRowParts = new GameObject[]
        {
            FindGo(root, "ScreenWorld/PageVr/RowTileGrab"),
            FindGo(root, "ScreenWorld/PageVr/TileGrabHead"),
            FindGo(root, "ScreenWorld/PageVr/BtnTileGrabOn"),
            FindGo(root, "ScreenWorld/PageVr/BtnTileGrabOff"),
            FindGo(root, "ScreenWorld/PageVr/TileGrabNote"),
        };
        controller.vrOnlyNote = FindTmp(root, "ScreenWorld/PageVr/VrOnlyNote");

        // 他人のチュンビーム（純ローカル設定）。0=表示 / 1=非表示。
        var chunHideImages = new UnityEngine.UI.Image[2];
        chunHideImages[0] = FindImage(root, "ScreenWorld/PageDisplay/BtnChunShowOthers");
        chunHideImages[1] = FindImage(root, "ScreenWorld/PageDisplay/BtnChunHideOthers");
        controller.chunBeamHideButtonImages = chunHideImages;
        controller.chunBeamHideHead = FindTmp(root, "ScreenWorld/PageDisplay/ChunHideHead");
        controller.chunBeamHideNote = FindTmp(root, "ScreenWorld/PageDisplay/ChunHideNote");

        // 卓の強制リセット（スタッフ／インスタンスマスター専用）。
        // 入口は「スタッフ」ページに、本体は別画面。出し入れは実行時に
        // MainMenuController.RenderWorld が権限を見て行うので、ここでは参照だけ渡す。
        controller.btnTableResetEntry = FindGo(root, "ScreenWorld/PageStaff/BtnTableReset");
        controller.btnTableResetEntryLabel = FindTmp(root, "ScreenWorld/PageStaff/BtnTableReset/Label");

        var screenTableReset = root.Find("ScreenTableReset");
        controller.screenTableReset = screenTableReset != null ? screenTableReset.gameObject : null;
        if (screenTableReset == null)
        {
            Debug.LogWarning("[WireMainMenu] ScreenTableReset がありません。Build Main Menu を再実行してください"
                + "（卓の強制リセットはスタッフパネルからしか使えません）。");
        }
        else
        {
            controller.tableResetTitle = FindTmp(root, "ScreenTableReset/Title");
            controller.tableResetNote = FindTmp(root, "ScreenTableReset/Note");
            controller.tableResetStatus = FindTmp(root, "ScreenTableReset/Status");
            controller.tableResetBackLabel = FindTmp(root, "ScreenTableReset/BtnBack/Label");
            controller.tableResetExecuteLabel = FindTmp(root, "ScreenTableReset/BtnExecute/Label");
            controller.tableResetExecuteImage = FindImage(root, "ScreenTableReset/BtnExecute");

            var resetLabels = new TextMeshProUGUI[TableResetButtons];
            var resetImages = new Image[TableResetButtons];
            for (int i = 0; i < TableResetButtons; ++i)
            {
                resetLabels[i] = FindTmp(root, "ScreenTableReset/BtnTable" + i + "/Label");
                resetImages[i] = FindImage(root, "ScreenTableReset/BtnTable" + i);
            }
            controller.tableResetLabels = resetLabels;
            controller.tableResetImages = resetImages;
        }

        controller.screenCosmetic = screenCosmetic != null ? screenCosmetic.gameObject : null;
        if (screenCosmetic == null)
        {
            Debug.LogWarning("[WireMainMenu] ScreenCosmetic がありません。Build Main Menu を再実行してください（装飾品画面は動作しません）。");
        }
        else
        {
            var cardRoots = new GameObject[CosmeticCards];
            var cardSwatches = new Image[CosmeticCards];
            var cardNames = new TextMeshProUGUI[CosmeticCards];
            var cardBadges = new GameObject[CosmeticCards];
            var cardLocks = new GameObject[CosmeticCards];
            var cardLockLabels = new TextMeshProUGUI[CosmeticCards];

            for (int i = 0; i < CosmeticCards; ++i)
            {
                var card = Find(root, "ScreenCosmetic/FxGrid/FxCard" + i);
                cardRoots[i] = card != null ? card.gameObject : null;

                var swatch = Find(root, "ScreenCosmetic/FxGrid/FxCard" + i + "/Swatch");
                cardSwatches[i] = swatch != null ? swatch.GetComponent<Image>() : null;

                cardNames[i] = FindTmp(root, "ScreenCosmetic/FxGrid/FxCard" + i + "/Name");

                var badge = Find(root, "ScreenCosmetic/FxGrid/FxCard" + i + "/SelectedBadge");
                cardBadges[i] = badge != null ? badge.gameObject : null;

                // 解禁条件つきコスメ（いまは立直BGMの曲）のロック暗幕。
                var lockOverlay = Find(root, "ScreenCosmetic/FxGrid/FxCard" + i + "/LockOverlay");
                cardLocks[i] = lockOverlay != null ? lockOverlay.gameObject : null;
                cardLockLabels[i] = FindTmp(root, "ScreenCosmetic/FxGrid/FxCard" + i + "/LockOverlay/LockLabel");
            }

            controller.fxCardRoots = cardRoots;
            controller.fxCardSwatches = cardSwatches;
            controller.fxCardNames = cardNames;
            controller.fxSelectedBadges = cardBadges;
            controller.fxCardLockOverlays = cardLocks;
            controller.fxCardLockLabels = cardLockLabels;
            controller.fxDetailName = FindTmp(root, "ScreenCosmetic/FxDetail/DetailName");
            controller.fxDetailDesc = FindTmp(root, "ScreenCosmetic/FxDetail/DetailDesc");
        }

        // 試写用の和了演出（MainMenu の隣に置く常時アクティブな1個）。
        // Build/Wire Agari Effects が生成・配線する。卓側の演出は卓カリングで
        // 落ちるため、カタログ参照／試写には必ずこちらを使う。
        var fxPreviewGo = FindSceneObject("AgariEffectPreview");
        var fxCatalog = fxPreviewGo != null ? fxPreviewGo.GetComponent<MahjongAgariEffect>() : null;
        if (fxCatalog == null)
        {
            Debug.LogWarning("[WireMainMenu] AgariEffectPreview が見つかりません。Tools > Kemono Mahjong > Build Agari Effects → Wire Agari Effects を実行してください（装飾品画面のカード名・試写が出ません）。");
        }
        else
        {
            controller.fxCatalog = fxCatalog;
        }

        controller.headerName = FindTmp(root, "ScreenHome/HeaderBar/PlayerName");
        controller.headerRank = FindTmp(root, "ScreenHome/HeaderBar/RankBadge");
        var headerRankIcon = Find(root, "ScreenHome/HeaderBar/PlayerIcon/RankIcon");
        controller.headerRankIcon = headerRankIcon != null ? headerRankIcon.GetComponent<RawImage>() : null;

        controller.profileName = FindTmp(root, "ScreenProfile/ProfileName");
        controller.profileRank = FindTmp(root, "ScreenProfile/ProfileRank");
        var profileRankIcon = Find(root, "ScreenProfile/ProfileRankIcon");
        controller.profileRankIcon = profileRankIcon != null ? profileRankIcon.GetComponent<RawImage>() : null;

        var rankIcons = new Texture[RankIconPaths.Length];
        for (int i = 0; i < RankIconPaths.Length; ++i)
        {
            rankIcons[i] = AssetDatabase.LoadAssetAtPath<Texture2D>(RankIconPaths[i]);
            if (rankIcons[i] == null)
            {
                Debug.LogWarning("[WireMainMenu] 段位アイコンが見つかりません: " + RankIconPaths[i]);
            }
        }
        controller.rankIcons = rankIcons;
        // Start前の白いプレースホルダ表示を避ける。ランタイムではPlayerDataの大段位で即時更新される。
        Texture defaultRankIcon = rankIcons.Length > 0 ? rankIcons[0] : null;
        if (controller.headerRankIcon != null) { controller.headerRankIcon.texture = defaultRankIcon; }
        if (controller.profileRankIcon != null) { controller.profileRankIcon.texture = defaultRankIcon; }

        // 戦績タブ（2026-07-17：四麻/三麻2ブロック並列→タブ式へ変更）。
        var tabImages = new Image[PlayerStatsStore.NUM_CATS];
        string[] tabNames = { "BtnTab4", "BtnTab3", "BtnTabF" };
        for (int i = 0; i < tabNames.Length; ++i)
        {
            var tab = Find(root, "ScreenProfile/" + tabNames[i]);
            tabImages[i] = tab != null ? tab.GetComponent<Image>() : null;
        }
        controller.profileTabImages = tabImages;
        controller.statsSelTitle = FindTmp(root, "ScreenProfile/StatsBlockSel/Title");
        controller.statsSelPlay = FindTmp(root, "ScreenProfile/StatsBlockSel/RowPlay/Value");
        controller.statsSelWin = FindTmp(root, "ScreenProfile/StatsBlockSel/RowWin/Value");
        controller.statsSelAgari = FindTmp(root, "ScreenProfile/StatsBlockSel/RowAgari/Value");
        controller.statsSelHoju = FindTmp(root, "ScreenProfile/StatsBlockSel/RowHoju/Value");

        var dots = new RectTransform[10];  // 直近10戦グラフ
        for (int i = 0; i < 10; ++i)
        {
            var dot = Find(root, "ScreenProfile/RecentGraph/Dot" + i);
            dots[i] = dot != null ? (RectTransform)dot : null;
        }
        controller.recentDots = dots;
        controller.graphRank1Y = 120f;  // RecentGraph の Line1 の y と一致させる
        controller.graphRankStep = 80f; // 罫線の間隔と一致させる

        const int numCharas = PlayerStatsStore.NUM_CHARAS; // 4 (Vol/Shine/Tsumuntema/自画像)
        var badges = new GameObject[numCharas];
        var cardPortraits = new RawImage[numCharas];
        var lockOverlays = new GameObject[numCharas];
        var lockLabels = new TextMeshProUGUI[numCharas];
        for (int i = 0; i < numCharas; ++i)
        {
            var badge = Find(root, "ScreenChara/CharaGrid/CharaCard" + i + "/SelectedBadge");
            badges[i] = badge != null ? badge.gameObject : null;

            var portrait = Find(root, "ScreenChara/CharaGrid/CharaCard" + i + "/Portrait");
            cardPortraits[i] = portrait != null ? portrait.GetComponent<RawImage>() : null;

            var lockRoot = Find(root, "ScreenChara/CharaGrid/CharaCard" + i + "/LockOverlay");
            lockOverlays[i] = lockRoot != null ? lockRoot.gameObject : null;
            lockLabels[i] = FindTmp(root, "ScreenChara/CharaGrid/CharaCard" + i + "/LockOverlay/LockLabel");
        }
        controller.charaSelectedBadges = badges;
        controller.charaCardPortraits = cardPortraits;
        controller.charaLockOverlays = lockOverlays;
        controller.charaLockLabels = lockLabels;

        var selfieHint = Find(root, "ScreenChara/CharaGrid/CharaCard"
                              + PlayerStatsStore.CHARA_SELFIE + "/Hint");
        controller.selfieCardHint = selfieHint != null ? selfieHint.gameObject : null;

        var detailPortrait = Find(root, "ScreenChara/CharaDetail/DetailPortrait");
        controller.charaDetailPortrait = detailPortrait != null ? detailPortrait.GetComponent<RawImage>() : null;
        controller.charaDetailText = FindTmp(root, "ScreenChara/CharaDetail/DetailText");

        // 能力スイッチのラベル。
        controller.abilityToggleLabel = FindTmp(root, "ScreenChara/BtnAbilityToggle/Label");
        controller.chunBeamToggleRoot = FindGo(root, "ScreenChara/BtnChunBeamToggle");
        controller.chunBeamToggleLabel = FindTmp(root, "ScreenChara/BtnChunBeamToggle/Label");
        controller.chunBeamNote = FindTmp(root, "ScreenChara/ChunBeamNote");

        // キャラID順（0=Vol / 1=Shine / 2=Tsumuntema）でポートレートを割り当てる。
        // 素材が一時的に見つからない場合は、既存のInspector割り当てを保持する。
        var oldPortraits = controller.charaPortraits;
        var portraits = new Texture[numCharas];
        for (int i = 0; i < numCharas; ++i)
        {
            if (i < CharaPortraitPaths.Length)
            {
                portraits[i] = AssetDatabase.LoadAssetAtPath<Texture2D>(CharaPortraitPaths[i]);
            }
            if (portraits[i] == null && oldPortraits != null && i < oldPortraits.Length)
            {
                portraits[i] = oldPortraits[i];
            }
            // 自画像キャラは静的な立ち絵を持たない（実行時に撮った RenderTexture を差し込む）。
            if (portraits[i] == null && i != PlayerStatsStore.CHARA_SELFIE)
            {
                string missingPath = i < CharaPortraitPaths.Length ? CharaPortraitPaths[i] : "(path未定義)";
                Debug.LogWarning("[WireMainMenu] キャラポートレートが見つかりません: " + missingPath);
            }
            if (cardPortraits[i] != null && portraits[i] != null)
            {
                cardPortraits[i].texture = portraits[i];
                cardPortraits[i].color = Color.white;
                cardPortraits[i].raycastTarget = false;
            }
        }
        controller.charaPortraits = portraits;

        // 自画像カードは静的な絵を持たない。エディタで真っ白な板に見えないよう空きスロット色で塗る
        // （実行時は MainMenuController.ApplyPortraits が撮った写真で上書きする）。
        if (cardPortraits.Length > PlayerStatsStore.CHARA_SELFIE &&
            cardPortraits[PlayerStatsStore.CHARA_SELFIE] != null)
        {
            cardPortraits[PlayerStatsStore.CHARA_SELFIE].texture = null;
            cardPortraits[PlayerStatsStore.CHARA_SELFIE].color = controller.emptyPortraitColor;
            cardPortraits[PlayerStatsStore.CHARA_SELFIE].raycastTarget = false;
        }

        // キャラ→ボイスパックの対応表。長さがキャラ数とズレていると自画像で範囲外になるので
        // ここで必ず合わせる（既存の値は保つ。増えた分は -1 ＝ 連動しない）。
        {
            var packs = controller.charaVoicePacks;
            if (packs == null || packs.Length != numCharas)
            {
                var resized = new int[numCharas];
                for (int i = 0; i < numCharas; ++i)
                {
                    resized[i] = (packs != null && i < packs.Length) ? packs[i] : -1;
                }
                controller.charaVoicePacks = resized;
            }
            // 自画像は専用ボイスを持たない。**直前のパックを維持するのではなく無音**にする
            // （声で役を送れないぶん、あがり画面が voicelessStepSeconds 間隔で順に出す）。
            controller.charaVoicePacks[PlayerStatsStore.CHARA_SELFIE] =
                MahjongSoundsV2.VOICE_PACK_SILENT;
        }

        // --- 自画像：共有ストア（PlayerDataの読み書き窓口。MainMenuに常駐） ---
        var selfieStore = (SelfiePortraitStore)EnsureUSharpComponent(mainMenu, typeof(SelfiePortraitStore), true);
        if (selfieStore == null)
        {
            Debug.LogError("[WireMainMenu] SelfiePortraitStore を追加できませんでした。");
            return;
        }
        UdonSharpEditorUtility.CopyProxyToUdon(selfieStore);
        EditorUtility.SetDirty(selfieStore);

        // --- 自画像の撮影ブース（MainMenu脇の常時アクティブなリグ） ---
        controller.selfieCamera = EnsureSelfieRig(mainMenu, controller, root, selfieStore);

        var homeMascot = Find(root, "ScreenHome/SelectedMascot");
        controller.homeCharaPortrait = homeMascot != null ? homeMascot.GetComponent<RawImage>() : null;
        if (controller.homeCharaPortrait != null)
        {
            controller.homeCharaPortrait.texture = portraits.Length > 0 ? portraits[0] : null;
            controller.homeCharaPortrait.color = Color.white;
            controller.homeCharaPortrait.raycastTarget = false;
            controller.homeCharaPortrait.uvRect = new Rect(0f, 380f / 1080f, 1f, 700f / 1080f);
        }

        // Edit Modeでも左の詳細欄が空にならないよう、初期表示はデフォルトキャラのVolにする。
        if (controller.charaDetailPortrait != null && portraits.Length > 0 && portraits[0] != null)
        {
            controller.charaDetailPortrait.texture = portraits[0];
            controller.charaDetailPortrait.color = Color.white;
            controller.charaDetailPortrait.raycastTarget = false;
        }

        // キャラ→ボイスパック連動（既存の設定値は保持。未設定は -1 = 連動しない）。
        if (controller.charaVoicePacks == null || controller.charaVoicePacks.Length != numCharas)
        {
            var packs = new int[numCharas];
            for (int i = 0; i < numCharas; ++i) { packs[i] = -1; }
            var oldPacks = controller.charaVoicePacks;
            if (oldPacks != null)
            {
                int copyCount = Mathf.Min(oldPacks.Length, packs.Length);
                for (int i = 0; i < copyCount; ++i) { packs[i] = oldPacks[i]; }
            }
            controller.charaVoicePacks = packs;
            Debug.LogWarning("[WireMainMenu] 追加キャラのcharaVoicePacksは -1 です。専用音声が揃ったらパックIDを設定してください。");
        }

        // ボイスパック書き込み役（kmj_voice_pack の唯一の書き手）。
        // 卓Prefab内の VoicePackSelector は近接カリングで丸ごと SetActive(false) になり、
        // 無効なGameObject上の UdonBehaviour はイベントを受けない（＝SetPack も
        // OnPlayerRestored も走らない）。MainMenu 直下に常時アクティブな1個を持たせる。
        var selector = (MahjongVoiceSelector)EnsureUSharpComponent(mainMenu, typeof(MahjongVoiceSelector), false);
        if (selector == null)
        {
            Debug.LogError("[WireMainMenu] MahjongVoiceSelector を追加できませんでした。キャラ選択→ボイス連動は無効のままです。");
        }
        else
        {
            UdonSharpEditorUtility.CopyProxyToUdon(selector);
            EditorUtility.SetDirty(selector);
        }
        controller.voiceSelector = selector;

        // キャラ選択の試聴用 AudioSource（ローカル再生・2D）。
        // previewClips は素材側で入っているのに鳴らす口が無かったため、ここで必ず作って配線する。
        // 卓の発声とは別系統（同期しない・他人には聞こえない）。
        if (selector != null)
        {
            var previewTf = mainMenu.transform.Find("CharaVoicePreview");
            GameObject previewGo;
            if (previewTf == null)
            {
                previewGo = new GameObject("CharaVoicePreview");
                previewGo.transform.SetParent(mainMenu.transform, false);
                Undo.RegisterCreatedObjectUndo(previewGo, "Create CharaVoicePreview");
            }
            else
            {
                previewGo = previewTf.gameObject;
            }

            var previewSrc = previewGo.GetComponent<AudioSource>();
            if (previewSrc == null) { previewSrc = previewGo.AddComponent<AudioSource>(); }
            previewSrc.playOnAwake = false;
            previewSrc.loop = false;
            previewSrc.spatialBlend = 0.0f;   // 2D＝メニューを見ている本人にだけ等音量で鳴る
            previewSrc.volume = CHARA_PREVIEW_VOLUME;
            previewSrc.dopplerLevel = 0.0f;
            previewSrc.clip = null;
            EditorUtility.SetDirty(previewSrc);

            selector.previewSource = previewSrc;
            UdonSharpEditorUtility.CopyProxyToUdon(selector);
            EditorUtility.SetDirty(selector);
        }

        // キャラ3Dプレビュー（Move CharacterSelector To MainMenu 実行後に存在する）。
        // プレビューRootは選択状態によって非アクティブになり得るため、
        // activeInHierarchy限定のGameObject.Findは使わない。
        var previewRoot = FindSceneObject("MainMenuCharaPreview");
        var previews = new GameObject[numCharas];
        if (previewRoot != null)
        {
            string[] previewNames = { "Vol", "Shine", "Tsumuntema" };
            for (int i = 0; i < previewNames.Length; ++i)
            {
                var preview = previewRoot.transform.Find(previewNames[i]);
                previews[i] = preview != null ? preview.gameObject : null;
            }
        }
        else
        {
            Debug.LogWarning("[WireMainMenu] MainMenuCharaPreview が見つかりません。先に Tools > Kemono Mahjong > Move CharacterSelector To MainMenu を実行してください。");
        }
        controller.charaPreviewModels = previews;

        UdonSharpEditorUtility.CopyProxyToUdon(voiceSelector);
        EditorUtility.SetDirty(voiceSelector);
        ProfileAnalyticsBuilder.Wire(mainMenu);
        WireCosmetics(controller);
        UdonSharpEditorUtility.CopyProxyToUdon(controller);
        EditorUtility.SetDirty(controller);

        // --- ボタン配線（onClick → UdonBehaviour.SendCustomEvent） ---
        var udon = UdonSharpEditorUtility.GetBackingUdonBehaviour(controller);
        if (udon == null)
        {
            Debug.LogError("[WireMainMenu] MainMenuController の UdonBehaviour が見つかりません。");
            return;
        }

        int wired = 0;
        wired += WireButton(root, "ScreenHome/BtnProfile", udon, "ShowProfile");
        wired += WireButton(root, "ScreenHome/BtnChara", udon, "ShowChara");
        wired += WireButton(root, "ScreenProfile/BtnBack", udon, "ShowHome");

        // 戦績タブ（四麻段位戦/三麻段位戦/フリー対局）。
        wired += WireButton(root, "ScreenProfile/BtnTab4", udon, "ShowProfileTab4");
        wired += WireButton(root, "ScreenProfile/BtnTab3", udon, "ShowProfileTab3");
        wired += WireButton(root, "ScreenProfile/BtnTabF", udon, "ShowProfileTabFree");
        wired += WireButton(root, "ScreenChara/BtnBack", udon, "ShowHome");
        for (int i = 0; i < numCharas; ++i)
        {
            wired += WireButton(root, "ScreenChara/CharaGrid/CharaCard" + i, udon, "SelectChara" + i);
        }
        wired += WireButton(root, "ScreenChara/BtnAbilityToggle", udon, "ToggleAbility");
        wired += WireButton(root, "ScreenChara/BtnChunBeamToggle", udon, "ToggleChunBeam");

        // 自画像の撮影画面。
        wired += WireButton(root, "ScreenSelfie/BtnBack", udon, "SelfieCancel");
        wired += WireButton(root, "ScreenSelfie/BtnRetake", udon, "SelfieRetake");
        wired += WireButton(root, "ScreenSelfie/BtnConfirm", udon, "SelfieConfirm");
        wired += WireButton(root, "ScreenSelfie/BtnShoot", udon, "SelfieShoot");
        wired += WireButton(root, "ScreenSelfie/FrameControls/BtnFrameUp", udon, "SelfieFrameUp");
        wired += WireButton(root, "ScreenSelfie/FrameControls/BtnFrameDown", udon, "SelfieFrameDown");
        wired += WireButton(root, "ScreenSelfie/FrameControls/BtnZoomIn", udon, "SelfieZoomIn");
        wired += WireButton(root, "ScreenSelfie/FrameControls/BtnZoomOut", udon, "SelfieZoomOut");
        wired += WireButton(root, "ScreenSelfie/FrameControls/BtnFrameReset", udon, "SelfieFrameReset");

        // 装飾品（和了演出コスメ）
        wired += WireButton(root, "ScreenHome/BtnCosmetic", udon, "ShowCosmetic");

        // 卓の強制リセット。入口は権限が無いと非表示なので、配線は常に行ってよい。
        wired += WireButton(root, "ScreenFreeList/BtnBack", udon, "ShowHome");
        for (int i = 0; i < FreeListCards; ++i)
        {
            wired += WireButton(root, "ScreenFreeList/FreeCard" + i, udon, "FreeListPick" + i);
        }

        wired += WireButton(root, "ScreenWorld/PageStaff/BtnTableReset", udon, "ShowTableReset");
        // 「もどる」は来たタブ（スタッフ）へ戻す。ShowWorld だと先頭のサウンドに戻ってしまう。
        wired += WireButton(root, "ScreenTableReset/BtnBack", udon, "ShowWorldStaff");
        wired += WireButton(root, "ScreenTableReset/BtnExecute", udon, "ForceResetExecute");
        for (int i = 0; i < TableResetButtons; ++i)
        {
            wired += WireButton(root, "ScreenTableReset/BtnTable" + i, udon, "ForceResetPick" + i);
        }
        wired += WireButton(root, "ScreenCosmetic/BtnBack", udon, "ShowHome");
        for (int i = 0; i < CosmeticCards; ++i)
        {
            wired += WireButton(root, "ScreenCosmetic/FxGrid/FxCard" + i, udon, "SelectFx" + i);
        }
        wired += WireButton(root, "ScreenCosmetic/FxDetail/BtnPreviewFx", udon, "PreviewFx");

        // ワールド設定（表示言語）
        wired += WireButton(root, "ScreenHome/BtnWorld", udon, "ShowWorld");
        wired += WireButton(root, "ScreenWorld/BtnBack", udon, "ShowHome");
        for (int i = 0; i < BuildMainMenu.WorldTabNames.Length; ++i)
        {
            wired += WireButton(root, "ScreenWorld/" + BuildMainMenu.WorldTabNames[i], udon, "SetWorldTab" + i);
        }
        wired += WireButton(root, "ScreenWorld/PageDisplay/BtnLangJa", udon, "SetLanguageJapanese");
        wired += WireButton(root, "ScreenWorld/PageDisplay/BtnLangEn", udon, "SetLanguageEnglish");
        wired += WireButton(root, "ScreenWorld/PageDisplay/BtnLangKo", udon, "SetLanguageKorean");
        wired += WireButton(root, "ScreenWorld/PageDisplay/BtnLangZhs", udon, "SetLanguageChineseSimplified");
        wired += WireButton(root, "ScreenWorld/PageDisplay/BtnLangZht", udon, "SetLanguageChineseTraditional");
        wired += WireButton(root, "ScreenWorld/PageDisplay/BtnLangAuto", udon, "SetLanguageAuto");
        wired += WireButton(root, "ScreenWorld/PagePlay/BtnReachTsumogiriOn", udon, "SetReachTsumogiriOn");
        wired += WireButton(root, "ScreenWorld/PagePlay/BtnReachTsumogiriOff", udon, "SetReachTsumogiriOff");
        wired += WireButton(root, "ScreenWorld/PageDisplay/BtnChunShowOthers", udon, "SetChunBeamShowOthers");
        wired += WireButton(root, "ScreenWorld/PageDisplay/BtnChunHideOthers", udon, "SetChunBeamHideOthers");
        wired += WireButton(root, "ScreenWorld/PagePlay/BtnPressGuard50", udon, "SetPressGuard50");
        wired += WireButton(root, "ScreenWorld/PagePlay/BtnPressGuard100", udon, "SetPressGuard100");
        wired += WireButton(root, "ScreenWorld/PagePlay/BtnPressGuard200", udon, "SetPressGuard200");
        wired += WireButton(root, "ScreenWorld/PagePlay/BtnPressGuard300", udon, "SetPressGuard300");
        wired += WireButton(root, "ScreenWorld/PageVr/BtnTouchInputLaser", udon, "SetTouchInputLaser");
        wired += WireButton(root, "ScreenWorld/PageVr/BtnTouchInputTouch", udon, "SetTouchInputTouch");
        wired += WireButton(root, "ScreenWorld/PageVr/BtnTileGrabOn", udon, "SetTileGrabOn");
        wired += WireButton(root, "ScreenWorld/PageVr/BtnTileGrabOff", udon, "SetTileGrabOff");


        // 段位戦ボタン（実装③。2026-08-18 東風戦追加で人数×局数の4ボタンへ）。
        wired += WireButton(root, "ScreenHome/BtnRanked4Han", udon, "OnRankedYonmaHanchan");
        wired += WireButton(root, "ScreenHome/BtnRanked4Ton", udon, "OnRankedYonmaTonpu");
        wired += WireButton(root, "ScreenHome/BtnRanked3Han", udon, "OnRankedSanmaHanchan");
        wired += WireButton(root, "ScreenHome/BtnRanked3Ton", udon, "OnRankedSanmaTonpu");
        wired += WireButton(root, "ScreenWaiting/BtnCancelWait", udon, "OnCancelWait");

        // フリー対局戦ボタン（実装④）。
        wired += WireButton(root, "ScreenHome/BtnFree", udon, "OnFreeButton");
        wired += WireButton(root, "ScreenFreeSetup/BtnBack", udon, "OnFreeClose");
        wired += WireButton(root, "ScreenFreeSetup/BtnFreeMode", udon, "OnFreeToggleMode");
        wired += WireButton(root, "ScreenFreeSetup/BtnFreeAbility", udon, "OnFreeToggleAbility");
        wired += WireButton(root, "ScreenFreeSetup/BtnFreeLength", udon, "OnFreeCycleLength");
        wired += WireButton(root, "ScreenFreeSetup/BtnFreeTimer", udon, "OnFreeCycleTimer");
        wired += WireButton(root, "ScreenFreeSetup/BtnFreeRedDora", udon, "OnFreeCycleRedDora");
        wired += WireButton(root, "ScreenFreeSetup/BtnFreeCpuLevel", udon, "OnFreeCycleCpuLevel");
        wired += WireButton(root, "ScreenFreeSetup/BtnFreeAotenjou", udon, "OnFreeToggleAotenjou");
        wired += WireButton(root, "ScreenFreeSetup/BtnFreeLocalYaku", udon, "OnFreeToggleLocalYaku");
        wired += WireButton(root, "ScreenFreeSetup/BtnFreeOpenReach", udon, "OnFreeToggleOpenReach");
        wired += WireButton(root, "ScreenFreeSetup/BtnFreeTsumoLoss", udon, "OnFreeToggleTsumoLoss");
        wired += WireButton(root, "ScreenFreeSetup/BtnFreeBust", udon, "OnFreeToggleBust");
        wired += WireButton(root, "ScreenFreeSetup/BtnFreeConfirm", udon, "OnFreeConfirmSetup");
        wired += WireButton(root, "ScreenFreeRoom/BtnCpuAdd", udon, "OnFreeCpuAdd");
        wired += WireButton(root, "ScreenFreeRoom/BtnCpuRemove", udon, "OnFreeCpuRemove");
        wired += WireButton(root, "ScreenFreeRoom/BtnFreeStart", udon, "OnFreeStart");
        wired += WireButton(root, "ScreenFreeRoom/BtnFreeClose", udon, "OnFreeClose");
        wired += WireButton(root, "ScreenFreeConfirm/BtnYes", udon, "OnFreeJoinYes");
        wired += WireButton(root, "ScreenFreeConfirm/BtnNo", udon, "OnFreeJoinNo");
        wired += WireButton(root, "ScreenInGame/BtnAbandon", udon, "OnAbandonRequest");
        wired += WireButton(root, "ScreenAbandonConfirm/BtnYes", udon, "OnAbandonYes");
        wired += WireButton(root, "ScreenAbandonConfirm/BtnNo", udon, "OnAbandonNo");

        // --- 卓（Mahjong）への PlayerStatsStore と和了画面マスコットの割り当て ---
        int tables = 0;
        int agariScreens = 0;
        int selfieWired = 0;
        foreach (var mahjong in Object.FindObjectsOfType<Mahjong>(true))
        {
            if (mahjong.statsStore != store)
            {
                mahjong.statsStore = store;
                UdonSharpEditorUtility.CopyProxyToUdon(mahjong);
                EditorUtility.SetDirty(mahjong);
            }

            var agariScreen = mahjong.agariScreen;
            if (agariScreen != null)
            {
                Transform mascotTransform = agariScreen.panelRoot != null
                    ? agariScreen.panelRoot.transform.Find("Canvas/Content/MascotImage")
                    : null;
                var mascotRaw = mascotTransform != null ? mascotTransform.GetComponent<RawImage>() : null;
                if (mascotRaw == null)
                {
                    Debug.LogWarning("[WireMainMenu] AgariScreen/MascotImage のRawImageが見つかりません: " + mahjong.name);
                }
                else
                {
                    agariScreen.mascotImage = mascotRaw;
                    agariScreen.charaMascotTextures = portraits;
                    agariScreen.defaultMascotTexture = portraits.Length > 0 ? portraits[0] : null;
                    mascotRaw.texture = agariScreen.defaultMascotTexture;
                    mascotRaw.color = Color.white;
                    mascotRaw.raycastTarget = false;
                    ApplyAgariMascotTopCrop(mascotRaw);
                    UdonSharpEditorUtility.CopyProxyToUdon(agariScreen);
                    EditorUtility.SetDirty(agariScreen);
                    EditorUtility.SetDirty(mascotRaw);
                    ++agariScreens;
                }
            }
            // --- 自画像キャラの写真を卓の表示にも出す ---
            // ストアは MainMenu 常駐なので、卓がカリングで落ちていても参照は生きている。
            selfieWired += AssignSelfieStore(mahjong, selfieStore);

            ++tables;
        }

        // ボタンを押したときのアニメーション。onClick を全部張り終えたこの位置で足す。
        WireButtonPressFx(mainMenu);

        EditorSceneManager.MarkSceneDirty(mainMenu.scene);

        Debug.Log("[WireMainMenu] 完了: ボタン配線 " + wired + " 件、卓への statsStore 割り当て " + tables +
                  " 卓、AgariScreenマスコット配線 " + agariScreens + " 件、自画像ストア配線 " + selfieWired +
                  " 件、voiceSelector " +
                  (controller.voiceSelector != null ? "割当済" : "未割当") + "。");
    }

    // ============================================================
    // ボタンのクリックアニメーション（押すと沈んで戻る）
    //   全ボタンに MenuButtonPress（Update を持たない中継）を付け、onClick へ
    //   「Press」を追加する。実際に動かすのは MainMenu 上の MenuButtonPressFx 1個。
    //
    //   WireButton は onClick を毎回全消ししてから張り直すので、この処理は
    //   全ボタンの配線が終わったあとに回す必要がある（先に足すと消される）。
    // ============================================================
    static void WireButtonPressFx(GameObject mainMenu, Transform buttonRoot = null)
    {
        var fx = (MenuButtonPressFx)EnsureUSharpComponent(mainMenu, typeof(MenuButtonPressFx), false);
        if (fx == null)
        {
            Debug.LogWarning("[WireMainMenu] MenuButtonPressFx を用意できませんでした。クリックアニメは無効です。");
            return;
        }
        UdonSharpEditorUtility.CopyProxyToUdon(fx);

        int buttons = 0;
        int added = 0;
        foreach (var button in (buttonRoot != null ? buttonRoot : mainMenu.transform).GetComponentsInChildren<Button>(true))
        {
            var relay = (MenuButtonPress)EnsureUSharpComponent(button.gameObject, typeof(MenuButtonPress), false);
            if (relay == null) { continue; }

            relay.fx = fx;
            relay.target = button.GetComponent<RectTransform>();
            UdonSharpEditorUtility.CopyProxyToUdon(relay);
            ++buttons;

            var relayUdon = UdonSharpEditorUtility.GetBackingUdonBehaviour(relay);
            if (relayUdon == null) { continue; }

            // Wire の再実行で二重登録しない（WireButton に消されていなければそのまま）。
            bool alreadyWired = false;
            for (int i = 0; i < button.onClick.GetPersistentEventCount(); ++i)
            {
                if (button.onClick.GetPersistentTarget(i) == relayUdon) { alreadyWired = true; break; }
            }
            if (alreadyWired) { continue; }

            UnityEventTools.AddStringPersistentListener(button.onClick, relayUdon.SendCustomEvent, "Press");
            ++added;
        }

        Debug.Log("[WireMainMenu] クリックアニメ: ボタン " + buttons + " 個に中継を配置（onClick 追加 "
                  + added + " 件）。");
    }

    // ============================================================
    // 自画像ストアを卓の表示コンポーネントへ配る
    //
    //  自画像を選んでいる席は、その人が PlayerData へ焼いた写真を
    //  立ち絵／キャラ絵として出す。ここを配線しないと、他プレイヤーには
    //  既定の立ち絵しか見えない（＝写真が同期していないように見える）。
    // ============================================================
    static int AssignSelfieStore(Mahjong mahjong, SelfiePortraitStore store)
    {
        int wired = 0;

        if (mahjong.agariScreen != null && mahjong.agariScreen.selfieStore != store)
        {
            mahjong.agariScreen.selfieStore = store;
            UdonSharpEditorUtility.CopyProxyToUdon(mahjong.agariScreen);
            EditorUtility.SetDirty(mahjong.agariScreen);
            ++wired;
        }

        if (mahjong.presentationScreen != null && mahjong.presentationScreen.selfieStore != store)
        {
            mahjong.presentationScreen.selfieStore = store;
            UdonSharpEditorUtility.CopyProxyToUdon(mahjong.presentationScreen);
            EditorUtility.SetDirty(mahjong.presentationScreen);
            ++wired;
        }

        if (mahjong.resultScreen != null && mahjong.resultScreen.selfieStore != store)
        {
            mahjong.resultScreen.selfieStore = store;
            UdonSharpEditorUtility.CopyProxyToUdon(mahjong.resultScreen);
            EditorUtility.SetDirty(mahjong.resultScreen);
            ++wired;
        }

        if (mahjong.yakumanCutin != null && mahjong.yakumanCutin.selfieStore != store)
        {
            mahjong.yakumanCutin.selfieStore = store;
            UdonSharpEditorUtility.CopyProxyToUdon(mahjong.yakumanCutin);
            EditorUtility.SetDirty(mahjong.yakumanCutin);
            ++wired;
        }

        // 宣言カットインは席ごとに4本ある（ダブロン同時表示のため）。
        foreach (var fx in mahjong.GetComponentsInChildren<MahjongCallEffect>(true))
        {
            if (fx == null || fx.selfieStore == store) { continue; }
            fx.selfieStore = store;
            UdonSharpEditorUtility.CopyProxyToUdon(fx);
            EditorUtility.SetDirty(fx);
            ++wired;
        }

        return wired;
    }

    // U# スクリプトに対応する UdonSharpProgramAsset が無ければ生成する。生成したら true。
    static Texture[] LoadCharaPortraits()
    {
        var portraits = new Texture[CharaPortraitPaths.Length];
        for (int i = 0; i < CharaPortraitPaths.Length; ++i)
        {
            portraits[i] = AssetDatabase.LoadAssetAtPath<Texture2D>(CharaPortraitPaths[i]);
            if (portraits[i] == null)
            {
                Debug.LogWarning("[WireAgariMascots] Character portrait was not found: " + CharaPortraitPaths[i]);
            }
        }
        return portraits;
    }

    static void ApplyAgariMascotTopCrop(RawImage mascotRaw)
    {
        const float sourceHeight = 1080f;
        const float cropSize = 700f;

        var mascotRect = mascotRaw.rectTransform;
        float topEdge = mascotRect.anchoredPosition.y +
            (1f - mascotRect.pivot.y) * mascotRect.sizeDelta.y;
        mascotRect.sizeDelta = new Vector2(cropSize, cropSize);
        mascotRect.anchoredPosition = new Vector2(
            mascotRect.anchoredPosition.x,
            topEdge - (1f - mascotRect.pivot.y) * cropSize);

        mascotRaw.uvRect = new Rect(
            0f,
            (sourceHeight - cropSize) / sourceHeight,
            1f,
            cropSize / sourceHeight);
    }

    static int WireAgariMascot(Mahjong mahjong, Texture[] portraits)
    {
        if (mahjong == null || mahjong.agariScreen == null) { return 0; }

        var agariScreen = mahjong.agariScreen;
        Transform mascotTransform = agariScreen.panelRoot != null
            ? agariScreen.panelRoot.transform.Find("Canvas/Content/MascotImage")
            : null;
        var mascotRaw = mascotTransform != null ? mascotTransform.GetComponent<RawImage>() : null;
        if (mascotRaw == null)
        {
            Debug.LogWarning("[WireAgariMascots] RawImage was not found at AgariScreen/PanelRoot/Canvas/Content/MascotImage: " + mahjong.name);
            return 0;
        }

        Undo.RecordObjects(new Object[] { agariScreen, mascotRaw }, "Wire Agari Mascot Images");
        agariScreen.mascotImage = mascotRaw;
        agariScreen.charaMascotTextures = portraits;
        agariScreen.defaultMascotTexture = portraits != null && portraits.Length > 0 ? portraits[0] : null;
        mascotRaw.texture = agariScreen.defaultMascotTexture;
        mascotRaw.color = Color.white;
        mascotRaw.raycastTarget = false;
        ApplyAgariMascotTopCrop(mascotRaw);
        UdonSharpEditorUtility.CopyProxyToUdon(agariScreen);
        EditorUtility.SetDirty(agariScreen);
        EditorUtility.SetDirty(mascotRaw);
        if (mahjong.gameObject.scene.IsValid())
        {
            EditorSceneManager.MarkSceneDirty(mahjong.gameObject.scene);
        }
        return 1;
    }

    // キャラ試聴ボイスの音量。Wire のたびにこの値へ戻す（Inspector 手直しは残らない）。
    const float CHARA_PREVIEW_VOLUME = 0.7f;

    static bool EnsureProgramAsset(string scriptPath, string assetPath)
    {
        // 既にどこかにプログラムアセットがあれば何もしない。
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
        if (script == null)
        {
            Debug.LogError("[WireMainMenu] スクリプトが見つかりません: " + scriptPath);
            return false;
        }

        var type = script.GetClass();
        if (type != null && UdonSharpProgramAsset.GetProgramAssetForClass(type) != null)
        {
            return false;
        }

        if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(assetPath) != null)
        {
            return false;
        }

        var programAsset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
        programAsset.sourceCsScript = script;
        AssetDatabase.CreateAsset(programAsset, assetPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[WireMainMenu] U#プログラムアセットを生成: " + assetPath);
        return true;
    }

    // U# コンポーネントを取得（無ければ追加）。プログラムアセット未生成のまま追加されて
    // 壊れた残骸（backing UdonBehaviour なし）が居たら作り直す。
    static UdonSharpBehaviour EnsureUSharpComponent(GameObject go, System.Type type, bool searchScene)
    {
        UdonSharpBehaviour proxy = null;
        if (searchScene)
        {
            proxy = (UdonSharpBehaviour)Object.FindObjectOfType(type, true);
        }
        else
        {
            proxy = (UdonSharpBehaviour)go.GetComponent(type);
        }

        if (proxy != null)
        {
            if (UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy) != null)
            {
                return proxy;
            }

            // 壊れた残骸：proxy と、プログラム未割り当ての UdonBehaviour を撤去して作り直す。
            Debug.LogWarning("[WireMainMenu] " + type.Name + " の backing UdonBehaviour が無いため作り直します。");
            var host = proxy.gameObject;
            Object.DestroyImmediate(proxy);
            foreach (var ub in host.GetComponents<UdonBehaviour>())
            {
                if (ub.programSource == null)
                {
                    Object.DestroyImmediate(ub);
                }
            }
        }

        var added = (UdonSharpBehaviour)UdonSharpUndo.AddComponent(go, type);
        if (added != null && UdonSharpEditorUtility.GetBackingUdonBehaviour(added) == null)
        {
            Debug.LogError("[WireMainMenu] " + type.Name + " の追加に失敗（プログラムアセット未コンパイル？）。U#コンパイル後に再実行してください。");
            Object.DestroyImmediate(added);
            return null;
        }

        Debug.Log("[WireMainMenu] " + type.Name + " を " + go.name + " に追加しました。");
        return added;
    }

    // Transform ではなく GameObject が欲しい場面用。見つからなければ null。
    static GameObject FindGo(Transform root, string path)
    {
        var t = Find(root, path);
        return t != null ? t.gameObject : null;
    }

    static Transform Find(Transform root, string path)
    {
        var t = root.Find(path);
        if (t == null)
        {
            Debug.LogWarning("[WireMainMenu] 見つかりません: " + path);
        }
        return t;
    }

    // 非アクティブを含むScene上のGameObjectを名前で探す。Prefabアセットは除外する。
    static GameObject FindSceneObject(string name)
    {
        var transforms = Object.FindObjectsOfType<Transform>(true);
        for (int i = 0; i < transforms.Length; ++i)
        {
            var candidate = transforms[i];
            if (candidate != null && candidate.name == name && candidate.gameObject.scene.IsValid())
            {
                return candidate.gameObject;
            }
        }
        return null;
    }

    static Image FindImage(Transform root, string path)
    {
        var t = Find(root, path);
        return t != null ? t.GetComponent<Image>() : null;
    }

    static TextMeshProUGUI FindTmp(Transform root, string path)
    {
        var t = Find(root, path);
        return t != null ? t.GetComponent<TextMeshProUGUI>() : null;
    }

    // onClick の既存 persistent listener を全消ししてから SendCustomEvent を1件だけ登録（冪等）。
    // ============================================================
    // 自画像の撮影ブース（SelfieCameraRig）
    //
    //  MainMenu の兄弟として常時アクティブなリグを1個だけ置く。
    //  撮影画面は畳まれる＝中にUdonを置くと止まるので、必ず外に出すこと。
    //  カメラは既定で enabled=false。撮影中だけ有効になり liveRt へ描く。
    // ============================================================
    const string SelfieRtDir = "Assets/Mahjong/Textures/UI/Selfie";
    const string SelfieLiveRtPath = SelfieRtDir + "/SelfieLive.renderTexture";
    const string SelfiePhotoRtPath = SelfieRtDir + "/SelfiePhoto.renderTexture";
    // 共有サイズのラダー（大きい順）。PlayerData に入らなければ次の段へ落ちる。
    //
    // **2026-09-03 実測**：生 153,600 バイトを入れたら使用量 210,651 / 上限 102,400。
    // 153,600 x 4/3 = 204,800 に他キー約 5,851 を足すと 210,651 でぴったり一致するので、
    // byte[] は base64 相当（4/3倍）で格納され、公式ドキュメントの言う圧縮は効いていない。
    // → 生サイズの上限はおよそ (102,400 - 5,851) x 3/4 ≒ 72,000 バイト。
    //   先頭の 96x144 = 55,296 バイト（生）で使用量は上限の約78%。
    // この値は他のPlayerDataキーの量やプラットフォームで動くので、固定値は当てず
    // 入らなければ下の段へ落ちる。段を足したいときはここへ書き足すだけでよい。
    static readonly int[] SelfieShareLadderW = { 96, 80, 64 };
    static readonly int[] SelfieShareLadderH = { 144, 120, 96 };

    // 旧・単一の共有RT（ラダー化する前のもの）。残っていたら片付ける。
    const string SelfieShareLegacyRtPath = SelfieRtDir + "/SelfieShare.renderTexture";
    static string SelfieShareRtPath(int index)
    {
        return SelfieRtDir + "/SelfieShare" + index + ".renderTexture";
    }
    // 自分の画面で見る用（高解像度）。
    const int SelfieRtWidth = 512;
    const int SelfieRtHeight = 768;

    // 自分のアバターが居るレイヤー。**PlayerLocal(10) ではない。**
    // World カメラから自分を写すには MirrorReflection(18) を入れる
    // （Avatar Sticker Camera の cullingMask 262656 = (1<<9)|(1<<18) が根拠）。
    const int LayerMirrorReflection = 18;

    static SelfiePortraitCamera EnsureSelfieRig(GameObject mainMenu, MainMenuController controller,
                                                Transform root, SelfiePortraitStore selfieStore)
    {
        // 背景を透明で抜くのでアルファ必須（ARGB32）。
        var liveRt = EnsureRenderTexture(SelfieLiveRtPath, SelfieRtWidth, SelfieRtHeight, 24);
        var photoRt = EnsureRenderTexture(SelfiePhotoRtPath, SelfieRtWidth, SelfieRtHeight, 0);
        // 他プレイヤーへ配る縮小版のラダー（大きい順）。
        var shareRts = new RenderTexture[SelfieShareLadderW.Length];
        for (int i = 0; i < SelfieShareLadderW.Length; ++i)
        {
            shareRts[i] = EnsureRenderTexture(SelfieShareRtPath(i),
                SelfieShareLadderW[i], SelfieShareLadderH[i], 0);
        }

        // ラダー化する前の単一RTが残っていたら消す（参照はもう無い）。
        if (AssetDatabase.LoadAssetAtPath<RenderTexture>(SelfieShareLegacyRtPath) != null)
        {
            AssetDatabase.DeleteAsset(SelfieShareLegacyRtPath);
            Debug.Log("[WireMainMenu] 旧・単一の共有RTを削除: " + SelfieShareLegacyRtPath);
        }

        // リグ本体（MainMenu の兄弟。畳まれない場所）。
        GameObject rig = null;
        var menuParent = mainMenu.transform.parent;
        if (menuParent != null)
        {
            var found = menuParent.Find("SelfieCameraRig");
            if (found != null) { rig = found.gameObject; }
        }
        else
        {
            rig = GameObject.Find("SelfieCameraRig");
        }

        if (rig == null)
        {
            rig = new GameObject("SelfieCameraRig");
            Undo.RegisterCreatedObjectUndo(rig, "Wire Main Menu");
            if (menuParent != null)
            {
                rig.transform.SetParent(menuParent, false);
            }
            rig.transform.position = mainMenu.transform.position;
            Debug.Log("[WireMainMenu] SelfieCameraRig を生成しました。");
        }
        rig.SetActive(true);

        // カメラ（子）。
        var camTransform = rig.transform.Find("SelfieCamera");
        GameObject camGo;
        if (camTransform != null)
        {
            camGo = camTransform.gameObject;
        }
        else
        {
            camGo = new GameObject("SelfieCamera");
            Undo.RegisterCreatedObjectUndo(camGo, "Wire Main Menu");
            camGo.transform.SetParent(rig.transform, false);
        }
        camGo.SetActive(true);

        var cam = camGo.GetComponent<Camera>();
        if (cam == null) { cam = Undo.AddComponent<Camera>(camGo); }
        // 背景の切り抜き：透明で塗り、自分のアバターだけを描く
        // （Avatar Sticker Camera と同じ原理。シェーダーもマスクも要らない）。
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
        cam.cullingMask = SelfieCullingMask();
        cam.fieldOfView = 35f;
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 120f;   // 背景は要らないので遠景は切る（Quest対策）
        cam.depth = -50f;          // 画面には出さない（targetTexture 付き）
        cam.allowHDR = false;
        cam.allowMSAA = false;
        cam.targetTexture = liveRt;
        cam.enabled = false;       // 撮影中だけ SelfiePortraitCamera が有効化する

        // AudioListener が付くと VRChat で警告になるので必ず外す。
        var listener = camGo.GetComponent<AudioListener>();
        if (listener != null) { Object.DestroyImmediate(listener); }

        var audio = rig.GetComponent<AudioSource>();
        if (audio == null) { audio = Undo.AddComponent<AudioSource>(rig); }
        audio.playOnAwake = false;
        audio.spatialBlend = 0f;   // UIの操作音なので2D
        audio.loop = false;

        var selfie = (SelfiePortraitCamera)EnsureUSharpComponent(rig, typeof(SelfiePortraitCamera), false);
        if (selfie == null)
        {
            Debug.LogError("[WireMainMenu] SelfiePortraitCamera を追加できませんでした。");
            return null;
        }

        selfie.captureCamera = cam;
        selfie.liveRt = liveRt;
        selfie.photoRt = photoRt;
        selfie.shareRts = shareRts;
        selfie.store = selfieStore;
        selfie.menu = controller;
        selfie.audioSource = audio;
        selfie.cameraCullingMask = SelfieCullingMask();

        // 撮影画面のUI（畳まれていても参照は保持してよい）。
        var viewfinder = Find(root, "ScreenSelfie/Frame/Viewfinder");
        selfie.viewfinder = viewfinder != null ? viewfinder.GetComponent<RawImage>() : null;
        var preview = Find(root, "ScreenSelfie/Frame/Preview");
        selfie.previewImage = preview != null ? preview.GetComponent<RawImage>() : null;
        selfie.countdownLabel = FindTmp(root, "ScreenSelfie/Frame/CountLabel");
        selfie.infoLabel = FindTmp(root, "ScreenSelfie/Info");
        var btnRetake = Find(root, "ScreenSelfie/BtnRetake");
        selfie.btnRetake = btnRetake != null ? btnRetake.gameObject : null;
        var btnConfirm = Find(root, "ScreenSelfie/BtnConfirm");
        selfie.btnConfirm = btnConfirm != null ? btnConfirm.gameObject : null;
        var btnShoot = Find(root, "ScreenSelfie/BtnShoot");
        selfie.btnShoot = btnShoot != null ? btnShoot.gameObject : null;
        var frameControls = Find(root, "ScreenSelfie/FrameControls");
        selfie.frameControls = frameControls != null ? frameControls.gameObject : null;
        var faceGuide = Find(root, "ScreenSelfie/Frame/FaceGuide");
        selfie.faceGuide = faceGuide != null ? faceGuide.gameObject : null;
        var countBack = Find(root, "ScreenSelfie/Frame/CountBack");
        selfie.countdownBack = countBack != null ? countBack.gameObject : null;

        // 実行時にも Initialize が入れ直すが、エディタで白板に見えないようここでも入れておく。
        if (selfie.viewfinder != null)
        {
            selfie.viewfinder.texture = liveRt;
            selfie.viewfinder.color = Color.white;
            selfie.viewfinder.raycastTarget = false;
        }
        if (selfie.previewImage != null)
        {
            selfie.previewImage.texture = liveRt;   // プレビューは未確定の1枚（止めた liveRt）。photoRt は確定写真
            selfie.previewImage.color = Color.white;
            selfie.previewImage.raycastTarget = false;
        }

        // ビルダーが決めた見た目の確定値は毎回代入する
        // （U#のシリアライズ済み値は既定値を変えても更新されないため）。
        selfie.countdownSeconds = 3.0f;
        selfie.captureDistance = 1.4f;
        selfie.cameraHeightOffset = -0.10f;
        selfie.aimHeightOffset = -0.30f;
        selfie.cameraFieldOfView = 35.0f;

        // 構図調整の刻みと範囲。
        //  上下 … 0.05m 刻みで -0.30m 〜 +0.40m。既定の構図はフレーム上端が目線+0.14m しか
        //         無く、耳・角・盛り髪が切れるので、上げ幅を下げ幅より多く取ってある。
        //  ズーム… 0.20m 刻みで 1.0m 〜 2.4m（既定 1.4m）。
        selfie.frameStepMeters = 0.05f;
        selfie.frameStepMin = -6;
        selfie.frameStepMax = 8;
        selfie.zoomStepMeters = 0.20f;
        selfie.zoomStepMin = -2;
        selfie.zoomStepMax = 5;

        UdonSharpEditorUtility.CopyProxyToUdon(selfie);
        EditorUtility.SetDirty(selfie);
        return selfie;
    }

    // 撮影カメラの cullingMask。
    // **自分のアバターだけを描く＝MirrorReflection(18) の1枚だけ。**
    // ・PlayerLocal(10) では World カメラに自分は写らない（ここを間違えると空の写真になる）
    // ・Player(9) は他プレイヤー。自画像には入れない
    //   ＝他人が写り込むことも、他人を撮ることもできない
    // ・背景（Default/Environment 等）も入れない＝自動で背景が抜ける
    static int SelfieCullingMask()
    {
        return 1 << LayerMirrorReflection;
    }

    // 背景を透明で抜くので ARGB32（アルファ付き）で作る。
    // 既にあるアセットもサイズ・形式が違えば直す（旧版が残っていると透過しない）。
    static RenderTexture EnsureRenderTexture(string path, int width, int height, int depthBits)
    {
        var existing = AssetDatabase.LoadAssetAtPath<RenderTexture>(path);
        if (existing != null)
        {
            bool changedRt = false;
            if (existing.width != width || existing.height != height
                || existing.format != RenderTextureFormat.ARGB32
                || existing.depth != depthBits)
            {
                existing.Release();
                existing.width = width;
                existing.height = height;
                existing.format = RenderTextureFormat.ARGB32;
                existing.depth = depthBits;
                changedRt = true;
            }
            if (existing.antiAliasing != 1) { existing.antiAliasing = 1; changedRt = true; }
            if (existing.useMipMap) { existing.useMipMap = false; changedRt = true; }
            if (changedRt)
            {
                existing.Create();
                EditorUtility.SetDirty(existing);
                AssetDatabase.SaveAssets();
                Debug.Log("[WireMainMenu] RenderTexture を作り直し: " + path
                          + " (" + width + "x" + height + " ARGB32 depth" + depthBits + ")");
            }
            return existing;
        }

        if (!AssetDatabase.IsValidFolder(SelfieRtDir))
        {
            var parent = System.IO.Path.GetDirectoryName(SelfieRtDir).Replace("\\", "/");
            AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(SelfieRtDir));
        }

        var rt = new RenderTexture(width, height, depthBits, RenderTextureFormat.ARGB32)
        {
            name = System.IO.Path.GetFileNameWithoutExtension(path),
            antiAliasing = 1,
            useMipMap = false,
            autoGenerateMips = false,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        AssetDatabase.CreateAsset(rt, path);
        AssetDatabase.SaveAssets();
        Debug.Log("[WireMainMenu] RenderTexture を生成: " + path
                  + " (" + width + "x" + height + " ARGB32 depth" + depthBits + ")");
        return AssetDatabase.LoadAssetAtPath<RenderTexture>(path);
    }


    // ScreenCosmetic のカテゴリボタン（index = MainMenuController の cosmeticCategory_）。
    static readonly string[] CosmeticCategoryEvents =
        { "ShowCosmeticAgari", "ShowCosmeticTileBack", "ShowCosmeticTableTop", "ShowCosmeticRiichiBgm" };

    // 必須ノードの解決。1つでも欠けていたら、卓を書き換える前に落とすために使う。
    static T CosmeticRequire<T>(Transform parent, string path) where T : Component
    {
        var t = parent.Find(path);
        if (t == null)
        {
            throw new System.InvalidOperationException(
                "[WireCosmetics] ノードがありません: " + parent.name + "/" + path + "（Build Cosmetic Customization を先に実行）");
        }
        var c = t.GetComponent<T>();
        if (c == null)
        {
            throw new System.InvalidOperationException(
                "[WireCosmetics] " + typeof(T).Name + " が付いていません: " + parent.name + "/" + path);
        }
        return c;
    }

    // 卓の天板メッシュを「上面」と「それ以外」の2サブメッシュへ割り直したコピーを作る。
    // 上面だけ MaterialPropertyBlock で色を差し替えたいので分割が要る。
    //
    // 注意（Quest）：サブメッシュが1つ増える＝卓あたり +1 ドローコール。色を設定していない
    // プレイヤーにも常時かかるコストなので、卓数を増やすときはここを思い出すこと。
    //
    // 注意（保守）：分割は Table.fbx の UV レイアウト頼み。上面のUVが u<TableTopUvMax の
    // 帯に固まっている、という実測に依存している。FBXを差し替えたら閾値の再確認が要る
    // （どちらかが空なら下で throw するので、黙って崩れることはない）。
    // 生成物 Assets/Mahjong/Models/TableTopColors.asset は必ずコミットに含めること。
    // 含め忘れると別環境で別GUIDのメッシュが再生成され、シーンの参照が壊れる。
    const float TableTopUvMax = .06f;

    static Mesh BuildTableTopSplitMesh(Mesh source, string meshPath)
    {
        if (source == null) { throw new System.InvalidOperationException("[WireCosmetics] Table の sharedMesh がありません。"); }

        var other = new System.Collections.Generic.List<int>();
        var surface = new System.Collections.Generic.List<int>();
        var uv = source.uv;
        var triangles = source.triangles;
        for (int t = 0; t < triangles.Length; t += 3)
        {
            bool top = uv[triangles[t]].x < TableTopUvMax
                && uv[triangles[t + 1]].x < TableTopUvMax
                && uv[triangles[t + 2]].x < TableTopUvMax;
            var list = top ? surface : other;
            list.Add(triangles[t]); list.Add(triangles[t + 1]); list.Add(triangles[t + 2]);
        }
        if (surface.Count == 0 || other.Count == 0)
        {
            throw new System.InvalidOperationException(
                "[WireCosmetics] 天板の上面UV領域が見つかりません（u<" + TableTopUvMax + " の帯）。Table.fbx を差し替えた場合は TableTopUvMax を測り直してください。");
        }

        var split = Object.Instantiate(source);
        split.name = "TableTopColors";
        split.subMeshCount = 2;
        split.SetTriangles(other, 0);
        split.SetTriangles(surface, 1);
        if (!AssetDatabase.IsValidFolder("Assets/Mahjong/Models")) { AssetDatabase.CreateFolder("Assets/Mahjong", "Models"); }
        AssetDatabase.CreateAsset(split, meshPath);
        return split;
    }

    // ScreenCosmetic（装飾品画面）の配線。
    //
    // ■ 順序の約束
    //   このメソッドは途中で卓の共有メッシュとマテリアル配列を書き換える。
    //   参照が欠けていて中途で落ちると「卓は改変済み・配線は途中」の半端な状態が残るので、
    //   §1 で必要なものを全部そろえてから §2 の書き換えに入る。順序を入れ替えないこと。
    public static void WireCosmetics(MainMenuController controller)
    {
        if (controller == null) { return; }
        var root = controller.transform;
        var screen = root.Find("ScreenCosmetic");
        if (screen == null || screen.Find("BtnCosmeticCategory0") == null) { return; }

        // ================= §1 検証：ここでは何も書き換えない =================
        var udon = UdonSharpEditorUtility.GetBackingUdonBehaviour(controller);
        if (udon == null) { throw new System.InvalidOperationException("[WireCosmetics] MainMenuController の UdonBehaviour が取れません。"); }
        if (controller.fxCatalog == null) { throw new System.InvalidOperationException("[WireCosmetics] fxCatalog が未割当です。"); }
        if (controller.fxDetailName == null || controller.fxDetailDesc == null) { throw new System.InvalidOperationException("[WireCosmetics] fxDetailName / fxDetailDesc が未割当です。"); }

        var categoryImages = new Image[4];
        for (int i = 0; i < 4; ++i) { categoryImages[i] = CosmeticRequire<Image>(screen, "BtnCosmeticCategory" + i); }
        var categoryTitle = CosmeticRequire<TextMeshProUGUI>(screen, "CosmeticCategoryTitle");
        var note = CosmeticRequire<TextMeshProUGUI>(screen, "FxNote");
        var previewButton = CosmeticRequire<Transform>(screen, "FxDetail/BtnPreviewFx");
        var resetButton = CosmeticRequire<Transform>(screen, "FxDetail/BtnResetColor");
        var grid = CosmeticRequire<Transform>(screen, "FxGrid");

        // カードは「全カテゴリの最大枚数」ぶん配線しておく。表示の出し入れは実行時の
        // RenderCosmetic が持つ。和了エフェクトの枚数だけで回すと、色7枚・立直BGM3枚の
        // カードが未配線のまま残り、押しても無反応になる。
        int fxCount = controller.fxCatalog.GetVariantCount();
        int wireCount = Mathf.Max(fxCount,
            Mathf.Max(MainMenuController.COSMETIC_COLOR_COUNT, MainMenuController.COSMETIC_BGM_COUNT));
        if (wireCount > grid.childCount)
        {
            throw new System.InvalidOperationException(
                "[WireCosmetics] カードが足りません：必要 " + wireCount + " 枚 / FxGrid は " + grid.childCount +
                " 枚。BuildMainMenu の MaxCards と MainMenuController の SelectFxN を増やしてください。");
        }
        for (int i = 0; i < wireCount; ++i)
        {
            var probe = grid.GetChild(i);
            CosmeticRequire<TextMeshProUGUI>(probe, "Name");
            CosmeticRequire<Image>(probe, "Swatch");
            CosmeticRequire<Transform>(probe, "SelectedBadge");
            // 解禁条件つきコスメ（立直BGMの曲）のロック暗幕。参照を配るのは Wire Main Menu 側。
            CosmeticRequire<Transform>(probe, "LockOverlay");
            CosmeticRequire<TextMeshProUGUI>(probe, "LockOverlay/LockLabel");
        }

        // 対訳は BuildLanguage.EnglishByPath がパスで引くので、そのパスに
        // 実体があることだけここで確かめる（登録自体は Wire Language の仕事）。
        CosmeticRequire<TextMeshProUGUI>(screen, "Title");
        for (int i = 0; i < 4; ++i) { CosmeticRequire<TextMeshProUGUI>(screen, "BtnCosmeticCategory" + i + "/Label"); }

        var backMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Mahjong/Materials/backs/green.mat");
        if (backMaterial == null) { throw new System.InvalidOperationException("[WireCosmetics] Assets/Mahjong/Materials/backs/green.mat が見つかりません。"); }

        // 卓の天板レンダラーを先に集めて、マテリアル構成まで検証しておく。
        // スロット1は「上面だけを塗るためのスロット0の複製」でなければならない
        // （別マテリアルが入っている卓を tops に入れると、色が違う面に乗る）。
        var tableTops = new System.Collections.Generic.List<MeshRenderer>();
        foreach (var table in Object.FindObjectsOfType<Mahjong>(true))
        {
            foreach (var renderer in table.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (renderer.name != "Table" || renderer.transform.parent == null || renderer.transform.parent.name != "Table") { continue; }
                if (renderer.GetComponent<MeshFilter>() == null)
                {
                    throw new System.InvalidOperationException("[WireCosmetics] " + table.name + " の Table に MeshFilter がありません。");
                }
                var m = renderer.sharedMaterials;
                if (m.Length != 1 && !(m.Length >= 2 && m[1] == m[0]))
                {
                    throw new System.InvalidOperationException(
                        "[WireCosmetics] " + table.name + " の Table のマテリアル構成が想定外です（" + m.Length +
                        " 枚）。スロット1はスロット0の複製である必要があります。");
                }
                tableTops.Add(renderer);
            }
        }

        // ================= §2 ここから書き換え =================
        controller.cosmeticCategoryImages = categoryImages;
        for (int i = 0; i < 4; ++i) { WireButton(root, "ScreenCosmetic/BtnCosmeticCategory" + i, udon, CosmeticCategoryEvents[i]); }
        controller.cosmeticCategoryTitle = categoryTitle;
        controller.cosmeticNote = note;
        controller.cosmeticPreviewButton = previewButton.gameObject;
        controller.cosmeticResetButton = resetButton.gameObject;
        WireButton(root, "ScreenCosmetic/FxDetail/BtnResetColor", udon, "ResetCosmeticColor");

        var backs = new System.Collections.Generic.List<Renderer>();
        var tops = new System.Collections.Generic.List<Renderer>();
        const string meshPath = "Assets/Mahjong/Models/TableTopColors.asset";
        var split = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
        foreach (var table in Object.FindObjectsOfType<Mahjong>(true))
        {
            foreach (var renderer in table.GetComponentsInChildren<MeshRenderer>(true))
            {
                var materials = renderer.sharedMaterials;
                if (materials.Length >= 2 && materials[0] == backMaterial) { backs.Add(renderer); }
            }
        }
        foreach (var renderer in tableTops)
        {
            var filter = renderer.GetComponent<MeshFilter>();
            if (split == null) { split = BuildTableTopSplitMesh(filter.sharedMesh, meshPath); }
            Undo.RecordObject(filter, "Wire Table Surface Colors");
            Undo.RecordObject(renderer, "Wire Table Surface Colors");
            filter.sharedMesh = split;
            var materials = renderer.sharedMaterials;
            if (materials.Length == 1) { renderer.sharedMaterials = new Material[] { materials[0], materials[0] }; }
            PrefabUtility.RecordPrefabInstancePropertyModifications(filter);
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            EditorUtility.SetDirty(filter);
            EditorUtility.SetDirty(renderer);
            tops.Add(renderer);
        }
        Transform loliHeightPreview = null;
        foreach (var candidate in Object.FindObjectsOfType<Transform>(true))
        {
            if (candidate.parent == null && candidate.name == "LoliHeightPreview")
            {
                loliHeightPreview = candidate;
                break;
            }
        }
        if (loliHeightPreview != null)
        {
            foreach (var renderer in loliHeightPreview.GetComponentsInChildren<MeshRenderer>(true))
            {
                var materials = renderer.sharedMaterials;
                if (materials.Length >= 2 && materials[0] == backMaterial) { backs.Add(renderer); }
                if (renderer.name == "TableSurface" && materials.Length >= 2) { tops.Add(renderer); }
            }
        }
        controller.cosmeticTileBackRenderers = backs.ToArray();
        controller.cosmeticTableTopRenderers = tops.ToArray();
        for (int i = 0; i < grid.childCount; ++i)
        {
            var card = grid.GetChild(i);
            // 初期表示は和了エフェクト（カテゴリ0）。他カテゴリの枚数は実行時に描き直される。
            card.gameObject.SetActive(i < fxCount);
            if (i >= wireCount) { continue; }
            card.Find("Name").GetComponent<TextMeshProUGUI>().text = i < fxCount ? controller.fxCatalog.variantNames[i] : "";
            card.Find("Swatch").GetComponent<Image>().color = i < fxCount ? controller.fxCatalog.variantMainColors[i] : Color.white;
            card.Find("SelectedBadge").gameObject.SetActive(i == 0);
            WireButton(root, "ScreenCosmetic/FxGrid/" + card.name, udon, "SelectFx" + i);
        }
        controller.fxDetailName.text = controller.fxCatalog.variantNames[0];
        controller.fxDetailDesc.text = controller.fxCatalog.variantDescs[0];
        // 対訳はここでは入れない。正は BuildLanguage.EnglishByPath の1か所で、
        // KemonoLocalizer への登録は Wire Language が一括でやる。
        // （以前はここから直接 tmpTexts へ足していたため、対訳件数の照合が
        //   常に合わず Validate Language が落ちていた。英文も 2 か所に分かれていた）
        // このメニューを回したあとは Wire Language を必ず実行すること。
        controller.cosmeticCategoryImages[0].color = StyleMainMenu.Yellow;
        UdonSharpEditorUtility.CopyProxyToUdon(controller);
        if (controller.statsStore != null) UdonSharpEditorUtility.CopyProxyToUdon(controller.statsStore);
        EditorUtility.SetDirty(controller);
        WireButtonPressFx(root.gameObject, screen);
        Debug.Log("[WireCosmetics] backs=" + backs.Count + " tops=" + tops.Count
            + " / カード配線=" + wireCount + "枚（和了エフェクト " + fxCount
            + " / 色 " + MainMenuController.COSMETIC_COLOR_COUNT
            + " / 立直BGM " + MainMenuController.COSMETIC_BGM_COUNT + "）");
    }

    static int WireButton(Transform root, string path, UdonBehaviour udon, string eventName)
    {
        var t = Find(root, path);
        if (t == null) { return 0; }

        var button = t.GetComponent<Button>();
        if (button == null)
        {
            Debug.LogWarning("[WireMainMenu] Button がありません: " + path);
            return 0;
        }

        while (button.onClick.GetPersistentEventCount() > 0)
        {
            UnityEventTools.RemovePersistentListener(button.onClick, 0);
        }

        UnityEventTools.AddStringPersistentListener(button.onClick, udon.SendCustomEvent, eventName);
        EditorUtility.SetDirty(button);
        return 1;
    }
}
#endif
