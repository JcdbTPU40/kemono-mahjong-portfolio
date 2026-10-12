#if UNITY_EDITOR
// 表示言語の自動切替（KemonoLanguage / KemonoLocalizer）の設置・配線・検査。
//
//  Tools > Kemono Mahjong > Build Language     … LanguageManager と Localizer を設置して配線まで行う
//  Tools > Kemono Mahjong > Wire Language      … 配線だけやり直す（卓を増やした後など）
//  Tools > Kemono Mahjong > Validate Language  … 参照欠け・未翻訳ラベルの検査
//
// ■ 日本語文言はここに持たない
//   静的ラベルの日本語は BuildMainMenu が焼いた「現在の表示文字列」をそのまま採取する。
//   ここが持つのは英訳だけ（EnglishByPath）。二重管理を避けるための決めごとで、
//   日本語を直したいときは BuildMainMenu 側を直して Build Main Menu → Wire Language の順に回す。
//
// ■ 動的ラベルは触らない
//   MainMenuController が実行時に書き換えるラベル（残り秒数・プレイヤー名・戦績値など）は
//   DynamicPaths に列挙してある。Localizer に載せると実行時の表示を潰すので必ず除外する。
using System.Collections.Generic;
using System.Linq;
using SuzuFactory.AozoraMahjong;
using TMPro;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class BuildLanguage
{
    const string ManagerName = "LanguageManager";

    // MainMenu からの相対パス → 英訳。
    // ここに無い TMP ラベルは「未翻訳」として Validate が報告する（DynamicPaths を除く）。
    static readonly Dictionary<string, string> EnglishByPath = new Dictionary<string, string>
    {
        // --- ホーム ---
        { "ScreenHome/HeaderBar/NeoBrand", "Welcome to the Mahjong Hall!" },
        { "ScreenHome/NeoRankedTitle", "RANKED MATCH" },
        { "ScreenHome/BtnRanked4Han/Label", "4-Player\nHalf Game" },
        { "ScreenHome/BtnRanked4Ton/Label", "4-Player\nEast Only" },
        { "ScreenHome/BtnRanked3Han/Label", "3-Player\nHalf Game" },
        { "ScreenHome/BtnRanked3Ton/Label", "3-Player\nEast Only" },
        { "ScreenHome/BtnProfile/Label", "Profile" },
        { "ScreenHome/BtnChara/Label", "Characters" },
        { "ScreenHome/BtnCosmetic/Label", "Cosmetics" },
        { "ScreenHome/BtnWorld/Label", "Settings" },

        // --- ワールド設定（雀魂式：左に項目タブ、右にそのページ） ---
        { "ScreenWorld/BtnBack/Label", "Back" },
        { "ScreenWorld/Title", "World Settings" },
        { "ScreenWorld/TabSound/Label", "Sound" },
        { "ScreenWorld/TabPlay/Label", "Gameplay" },
        { "ScreenWorld/TabVr/Label", "VR Controls" },
        { "ScreenWorld/TabDisplay/Label", "Display" },
        { "ScreenWorld/TabYaku/Label", "Yaku Board" },
        { "ScreenWorld/TabStaff/Label", "Staff" },
        { "ScreenWorld/PageDisplay/LangHead", "Display language" },
        { "ScreenWorld/PageDisplay/BtnLangAuto/Label", "Auto" },
        { "ScreenWorld/PageDisplay/BtnChunShowOthers/Label", "Show" },
        { "ScreenWorld/PageDisplay/BtnChunHideOthers/Label", "Hide" },
        { "ScreenWorld/PagePlay/BtnPressGuard50/Label", "0.05s" },
        { "ScreenWorld/PagePlay/BtnPressGuard100/Label", "0.1s" },
        { "ScreenWorld/PagePlay/BtnPressGuard200/Label", "0.2s" },
        { "ScreenWorld/PagePlay/BtnPressGuard300/Label", "0.3s" },
        { "ScreenWorld/PageVr/BtnTouchInputLaser/Label", "Laser" },
        { "ScreenWorld/PageVr/BtnTouchInputTouch/Label", "Touch" },
        { "ScreenWorld/PageStaff/StaffHead", "Force-reset a table" },
        { "ScreenWorld/PageStaff/StaffNote", "Resets a stuck table to its initial state. Only staff and the instance master can use this." },

        // 役種一覧パネル（持ち運べる早見板）の操作。実体は MahjongYakuGuide が持つ。
        { "ScreenWorld/PageYaku/YakuHead", "Board size" },
        { "ScreenWorld/PageYaku/BtnYakuS/Label", "S" },
        { "ScreenWorld/PageYaku/BtnYakuM/Label", "M" },
        { "ScreenWorld/PageYaku/BtnYakuL/Label", "L" },
        { "ScreenWorld/PageYaku/YakuNote", "Size of the yaku guide board at the reception desk. Local to you." },
        { "ScreenWorld/PageYaku/YakuHomeHead", "Position" },
        { "ScreenWorld/PageYaku/BtnYakuHome/Label", "Send it back" },
        { "ScreenWorld/PageYaku/YakuHomeNote", "\"Send it back\" returns the board to its stand at the reception desk." },

        // --- プロフィール ---
        { "ScreenProfile/BtnBack/Label", "Back" },
        { "ScreenProfile/BtnTab4/Label", "4-Player Ranked" },
        { "ScreenProfile/BtnTab3/Label", "3-Player Ranked" },
        { "ScreenProfile/BtnTabF/Label", "Free Match" },
        { "ScreenProfile/StatsBlockSel/RowPlay/Label", "Games" },
        { "ScreenProfile/StatsBlockSel/RowWin/Label", "1st place" },
        { "ScreenProfile/StatsBlockSel/RowAgari/Label", "Wins" },
        { "ScreenProfile/StatsBlockSel/RowHoju/Label", "Deal-ins" },

        // --- プロフィール分析（ProfileAnalyticsBuilder が生成） ---
        { "ScreenProfile/AnalyticsScroll/Viewport/Content/HighlightTitle", "Highlight" },
        { "ScreenProfile/AnalyticsScroll/Viewport/Content/HighlightScope/BtnHighlightNormal/Label", "Standard" },
        { "ScreenProfile/AnalyticsScroll/Viewport/Content/HighlightScope/BtnHighlightEvent/Label", "Event" },
        { "ScreenProfile/AnalyticsScroll/Viewport/Content/HighlightNote", "Your best-scoring win since this update / recorded from completed matches" },
        { "ScreenProfile/AnalyticsScroll/Viewport/Content/StyleTitle", "Style" },
        { "ScreenProfile/AnalyticsScroll/Viewport/Content/Attack", "ATK" },
        { "ScreenProfile/AnalyticsScroll/Viewport/Content/Defense", "DEF" },
        { "ScreenProfile/AnalyticsScroll/Viewport/Content/Speed", "SPD" },
        { "ScreenProfile/AnalyticsScroll/Viewport/Content/Luck", "LUK" },
        { "ScreenProfile/AnalyticsScroll/Viewport/Content/StyleDescription", "A rough guide to your play style\n\nATK: average points when you win\nDEF: how rarely you deal in\nSPD: discards taken to win\nLUK: han gained from dora, ippatsu and yakuman" },
        { "ScreenProfile/AnalyticsScroll/Viewport/Content/StyleHelp/Formula", "World-specific metrics (not the Mahjong Soul formula)\nATK = average win points / 12,000 x 100\nDEF = 100 - deal-in rate x 500\nSPD = 100 - average discards per win x 5\nLUK = (dora + ura + red + kita + ippatsu han + yakuman x 8) / wins x 25\nEach value is 0-100. Items with no win data show --.\nThese four use your last 100 games (the placement and win-type graphs are lifetime).\nLUK counts only wins recorded after this update." },
        { "ScreenProfile/AnalyticsScroll/Viewport/Content/TrendTitle", "Match History" },
        { "ScreenProfile/AnalyticsScroll/Viewport/Content/Trend/Rank0/Label", "1st" },
        { "ScreenProfile/AnalyticsScroll/Viewport/Content/Trend/Rank1/Label", "2nd" },
        { "ScreenProfile/AnalyticsScroll/Viewport/Content/Trend/Rank2/Label", "3rd" },
        { "ScreenProfile/AnalyticsScroll/Viewport/Content/Trend/Rank3/Label", "4th" },
        { "ScreenProfile/AnalyticsScroll/Viewport/Content/WinTitle", "Wins by Type" },
        { "ScreenProfile/AnalyticsScroll/Viewport/Content/WinName0", "● Riichi" },
        { "ScreenProfile/AnalyticsScroll/Viewport/Content/WinName1", "● Open" },
        { "ScreenProfile/AnalyticsScroll/Viewport/Content/WinName2", "● Damaten" },
        { "ScreenProfile/AnalyticsScroll/Viewport/Content/PlaceTitle", "Placements" },
        { "ScreenProfile/AnalyticsScroll/Viewport/Content/Place0/Label", "1st" },
        { "ScreenProfile/AnalyticsScroll/Viewport/Content/Place1/Label", "2nd" },
        { "ScreenProfile/AnalyticsScroll/Viewport/Content/Place2/Label", "3rd" },
        { "ScreenProfile/AnalyticsScroll/Viewport/Content/Place3/Label", "4th" },

        // --- キャラ選択 ---
        { "ScreenChara/BtnBack/Label", "Back" },
        { "ScreenChara/CharaGrid/CharaCard3/Name", "Selfie" },
        { "ScreenChara/CharaGrid/CharaCard3/Hint", "Take a\nphoto" },

        // --- 自画像の撮影 ---
        { "ScreenSelfie/BtnBack/Label", "Back" },
        { "ScreenSelfie/Title", "Take Your Selfie" },
        { "ScreenSelfie/BtnRetake/Label", "Retake" },
        { "ScreenSelfie/BtnConfirm/Label", "Confirm" },
        { "ScreenSelfie/BtnShoot/Label", "Shoot" },
        { "ScreenSelfie/FrameControls/BtnFrameUp/Label", "Up" },
        { "ScreenSelfie/FrameControls/BtnFrameDown/Label", "Down" },
        { "ScreenSelfie/FrameControls/BtnZoomIn/Label", "Closer" },
        { "ScreenSelfie/FrameControls/BtnZoomOut/Label", "Wider" },
        { "ScreenSelfie/FrameControls/BtnFrameReset/Label", "Reset" },

        // --- 装飾品 ---
        // カテゴリボタンの英文はここが正。WireMainMenu.WireCosmetics 側では登録しないこと。
        // （2か所に置くと、Wire Language を後に流したときだけ古い方へ戻る）
        { "ScreenCosmetic/BtnBack/Label", "Back" },
        { "ScreenCosmetic/Title", "Customization" },
        { "ScreenCosmetic/BtnCosmeticCategory0/Label", "Win effect" },
        { "ScreenCosmetic/BtnCosmeticCategory1/Label", "Tile backs\n<size=26>Local</size>" },
        { "ScreenCosmetic/BtnCosmeticCategory2/Label", "Table surface\n<size=26>Local</size>" },
        { "ScreenCosmetic/BtnCosmeticCategory3/Label", "Riichi BGM" },

        // --- 待機 ---
        { "ScreenWaiting/BtnCancelWait/Label", "Cancel" },

        // --- フリー対局 設定 ---
        { "ScreenFreeSetup/BtnBack/Label", "Back" },
        { "ScreenFreeSetup/Title", "Free Match Setup" },
        { "ScreenFreeSetup/BtnFreeConfirm/Label", "Confirm (start recruiting)" },

        // --- フリー対局 部屋 ---
        { "ScreenFreeRoom/BtnCpuAdd/Label", "Add CPU" },
        { "ScreenFreeRoom/BtnCpuRemove/Label", "Remove CPU" },
        { "ScreenFreeRoom/BtnFreeStart/Label", "Start" },

        // --- 入室確認 ---
        { "ScreenFreeConfirm/BtnYes/Label", "Yes" },
        { "ScreenFreeConfirm/BtnNo/Label", "No" },
        { "ScreenInGame/InGameTitle", "In a Match" },
        { "ScreenInGame/BtnAbandon/Label", "Leave Match" },
        { "ScreenAbandonConfirm/BtnYes/Label", "Yes" },
        { "ScreenAbandonConfirm/BtnNo/Label", "No" },
    };

    // MainMenuController が実行時に書き換えるラベル（Localizer に載せてはいけない）。
    static readonly HashSet<string> DynamicPaths = new HashSet<string>
    {
        "ScreenHome/HeaderBar/RankBadge",
        "ScreenHome/HeaderBar/PlayerName",
        "ScreenHome/HomeInfo",
        "ScreenHome/BtnFree/Label",
        "ScreenProfile/ProfileName",
        // ホームの待機状況（「N人待機中... / 卓へ案内中...」）は
        // MainMenuController が T() で毎秒組み立てる＝Localizer には載せない。
        "ScreenHome/BtnRanked4Han/QueueInfo",
        "ScreenHome/BtnRanked4Ton/QueueInfo",
        "ScreenHome/BtnRanked3Han/QueueInfo",
        "ScreenHome/BtnRanked3Ton/QueueInfo",
        "ScreenHome/BtnFree/QueueInfo",
        "ScreenProfile/ProfileRank",
        "ScreenProfile/StatsBlockSel/Title",
        "ScreenProfile/StatsBlockSel/RowPlay/Value",
        "ScreenProfile/StatsBlockSel/RowWin/Value",
        "ScreenProfile/StatsBlockSel/RowAgari/Value",
        "ScreenProfile/StatsBlockSel/RowHoju/Value",
        // 分析パネルの値は MainMenuController が T() で組み立てる（Localizer に載せない）。
        "ScreenProfile/AnalyticsScroll/Viewport/Content/HighlightValue",
        "ScreenProfile/AnalyticsScroll/Viewport/Content/HighlightEmpty",
        "ScreenProfile/AnalyticsScroll/Viewport/Content/StyleValues",
        "ScreenProfile/AnalyticsScroll/Viewport/Content/TrendValue",
        "ScreenProfile/AnalyticsScroll/Viewport/Content/Sample",
        "ScreenProfile/AnalyticsScroll/Viewport/Content/WinValue0",
        "ScreenProfile/AnalyticsScroll/Viewport/Content/WinValue1",
        "ScreenProfile/AnalyticsScroll/Viewport/Content/WinValue2",
        "ScreenProfile/AnalyticsScroll/Viewport/Content/Place0/Value",
        "ScreenProfile/AnalyticsScroll/Viewport/Content/Place1/Value",
        "ScreenProfile/AnalyticsScroll/Viewport/Content/Place2/Value",
        "ScreenProfile/AnalyticsScroll/Viewport/Content/Place3/Value",
        "ScreenChara/CharaDetail/DetailText",
        "ScreenChara/BtnAbilityToggle/Label",
        // チュンビーム。ラベルも説明も RenderChunBeamToggle が T() で毎回書く。
        "ScreenChara/BtnChunBeamToggle/Label",
        "ScreenChara/ChunBeamNote",
        "ScreenChara/CharaGrid/CharaCard0/LockOverlay/LockLabel",
        "ScreenChara/CharaGrid/CharaCard1/LockOverlay/LockLabel",
        "ScreenChara/CharaGrid/CharaCard2/LockOverlay/LockLabel",
        "ScreenChara/CharaGrid/CharaCard3/LockOverlay/LockLabel",
        "ScreenSelfie/Info",
        "ScreenSelfie/Frame/CountLabel",
        "ScreenCosmetic/FxDetail/DetailName",
        "ScreenCosmetic/FxDetail/DetailDesc",
        // 装飾品はカテゴリで中身が入れ替わる＝RenderCosmetic が T() で毎回書き換える。
        // Localizer に載せると、言語切替のたびに別カテゴリの文言へ戻る。
        "ScreenCosmetic/CosmeticCategoryTitle",
        "ScreenCosmetic/FxNote",
        "ScreenCosmetic/FxDetail/BtnResetColor/Label",
        // 試写ボタンは和了エフェクト＝「試写」、立直BGM＝「試聴／停止」と中身が変わる。
        "ScreenCosmetic/FxDetail/BtnPreviewFx/Label",
        // カードのロック文言（立直BGMの解禁条件）。RenderCosmetic が T() で毎回書く。
        "ScreenCosmetic/FxGrid/FxCard0/LockOverlay/LockLabel",
        "ScreenCosmetic/FxGrid/FxCard1/LockOverlay/LockLabel",
        "ScreenCosmetic/FxGrid/FxCard2/LockOverlay/LockLabel",
        "ScreenCosmetic/FxGrid/FxCard3/LockOverlay/LockLabel",
        "ScreenCosmetic/FxGrid/FxCard4/LockOverlay/LockLabel",
        "ScreenCosmetic/FxGrid/FxCard5/LockOverlay/LockLabel",
        "ScreenCosmetic/FxGrid/FxCard6/LockOverlay/LockLabel",
        "ScreenCosmetic/FxGrid/FxCard7/LockOverlay/LockLabel",
        // カード名。RenderCosmetic がカテゴリ（和了演出／色／立直BGM）ごとに日英で毎回書く
        // （和了演出の英語名は MahjongAgariEffect.variantNamesEn）。英訳表に足すと切替で潰れる。
        "ScreenCosmetic/FxGrid/FxCard0/Name",
        "ScreenCosmetic/FxGrid/FxCard1/Name",
        "ScreenCosmetic/FxGrid/FxCard2/Name",
        "ScreenCosmetic/FxGrid/FxCard3/Name",
        "ScreenCosmetic/FxGrid/FxCard4/Name",
        "ScreenCosmetic/FxGrid/FxCard5/Name",
        "ScreenCosmetic/FxGrid/FxCard6/Name",
        "ScreenCosmetic/FxGrid/FxCard7/Name",
        "ScreenWorld/PageDisplay/LangNote",
        // 立直後のツモ切り。見出しも説明文も RenderWorld が T() で毎回書く。
        "ScreenWorld/PagePlay/TsumogiriHead",
        "ScreenWorld/PagePlay/TsumogiriNote",
        // 押した直後の連打防止。見出しも説明文も RenderWorld が T() で毎回書く。
        "ScreenWorld/PagePlay/PressGuardHead",
        "ScreenWorld/PagePlay/PressGuardNote",
        // 卓のボタンの押し方。見出しも説明文（注意書き）も RenderWorld が T() で毎回書く。
        "ScreenWorld/PageVr/TouchInputHead",
        "ScreenWorld/PageVr/TouchInputNote",
        // 牌を掴んで打牌・VRのみの案内。見出しも説明文も RenderWorld が T() で毎回書く。
        "ScreenWorld/PageVr/TileGrabHead",
        "ScreenWorld/PageVr/TileGrabNote",
        "ScreenWorld/PageVr/VrOnlyNote",
        // 他人のチュンビーム。見出しも説明文も RenderWorld が T() で毎回書く。
        "ScreenWorld/PageDisplay/ChunHideHead",
        "ScreenWorld/PageDisplay/ChunHideNote",
        "ScreenWaiting/WaitTitle",
        "ScreenWaiting/WaitInfo",
        "ScreenWaiting/WaitMemberList/WaitMember0",
        "ScreenWaiting/WaitMemberList/WaitMember1",
        "ScreenWaiting/WaitMemberList/WaitMember2",
        "ScreenWaiting/WaitMemberList/WaitMember3",
        "ScreenFreeSetup/BtnFreeMode/Label",
        "ScreenFreeSetup/BtnFreeAbility/Label",
        "ScreenFreeSetup/BtnFreeLength/Label",
        "ScreenFreeSetup/BtnFreeTimer/Label",
        "ScreenFreeSetup/BtnFreeRedDora/Label",
        "ScreenFreeSetup/BtnFreeCpuLevel/Label",
        "ScreenFreeSetup/BtnFreeAotenjou/Label",
        "ScreenFreeSetup/BtnFreeLocalYaku/Label",
        "ScreenFreeSetup/BtnFreeOpenReach/Label",
        "ScreenFreeSetup/BtnFreeTsumoLoss/Label",
        "ScreenFreeSetup/BtnFreeBust/Label",
        "ScreenFreeRoom/RoomTitle",
        "ScreenFreeRoom/RoomInfo",
        "ScreenFreeRoom/BtnFreeClose/Label",
        "ScreenFreeRoom/MemberList/Member0",
        "ScreenFreeRoom/MemberList/Member1",
        "ScreenFreeRoom/MemberList/Member2",
        "ScreenFreeRoom/MemberList/Member3",
        "ScreenFreeConfirm/ConfirmText",
        // 対局中の案内・途中退出の確認文は MainMenuController が言語ごとに書き換える。
        "ScreenInGame/InGameText",
        "ScreenAbandonConfirm/ConfirmText",
        // 卓の強制リセット（スタッフ／インスタンスマスター）。
        // 見出しも卓ボタンも状態行も RenderTableReset が T() で毎回書く。
        // フリー対局の卓一覧。見出しもカードも RenderFreeList が T() で毎回書く。
        "ScreenFreeList/Title",
        "ScreenFreeList/Note",
        "ScreenFreeList/BtnBack/Label",
        "ScreenFreeList/FreeCard0/Name",
        "ScreenFreeList/FreeCard0/Info",
        "ScreenFreeList/FreeCard1/Name",
        "ScreenFreeList/FreeCard1/Info",
        "ScreenFreeList/FreeCard2/Name",
        "ScreenFreeList/FreeCard2/Info",
        "ScreenFreeList/FreeCard3/Name",
        "ScreenFreeList/FreeCard3/Info",
        "ScreenFreeList/FreeCard4/Name",
        "ScreenFreeList/FreeCard4/Info",
        "ScreenFreeList/FreeCard5/Name",
        "ScreenFreeList/FreeCard5/Info",
        "ScreenFreeList/FreeCard6/Name",
        "ScreenFreeList/FreeCard6/Info",
        "ScreenFreeList/FreeCard7/Name",
        "ScreenFreeList/FreeCard7/Info",
        "ScreenWorld/PageStaff/BtnTableReset/Label",
        "ScreenTableReset/Title",
        "ScreenTableReset/Note",
        "ScreenTableReset/Status",
        "ScreenTableReset/BtnBack/Label",
        "ScreenTableReset/BtnExecute/Label",
        "ScreenTableReset/BtnTable0/Label",
        "ScreenTableReset/BtnTable1/Label",
        "ScreenTableReset/BtnTable2/Label",
        "ScreenTableReset/BtnTable3/Label",
        "ScreenTableReset/BtnTable4/Label",
        "ScreenTableReset/BtnTable5/Label",
        "ScreenTableReset/BtnTable6/Label",
        "ScreenTableReset/BtnTable7/Label",
    };

    // 固有名詞・記号など、翻訳しないもの（Validate を黙らせる）。
    static readonly HashSet<string> NoTranslationPaths = new HashSet<string>
    {
        "ScreenChara/CharaGrid/CharaCard0/Name",
        "ScreenChara/CharaGrid/CharaCard1/Name",
        "ScreenChara/CharaGrid/CharaCard2/Name",
        // 言語ボタンは「その言語で」書いてある（雀魂・VRChat本体と同じ作法）。
        "ScreenWorld/PageDisplay/BtnLangJa/Label",
        "ScreenWorld/PageDisplay/BtnLangEn/Label",
        "ScreenWorld/PageDisplay/BtnLangKo/Label",
        "ScreenWorld/PageDisplay/BtnLangZhs/Label",
        "ScreenWorld/PageDisplay/BtnLangZht/Label",
        // ON / OFF は日英で同じ綴り。
        "ScreenWorld/PagePlay/BtnReachTsumogiriOn/Label",
        "ScreenWorld/PagePlay/BtnReachTsumogiriOff/Label",
    };

    // ============================================================
    // メインメニュー以外のパネル
    //   キー = シーン上のルート GameObject 名 → （ルートからの相対パス → 英訳）
    //   日本語は Wire 実行時の表示文字列をそのまま採取するので、ここには英訳だけ書く。
    // ============================================================
    static readonly Dictionary<string, Dictionary<string, string>> ExtraPanels =
        new Dictionary<string, Dictionary<string, string>>
    {
        // --- デイリーランキング（スコア版） ---
        { "InstanceRankingPanel", new Dictionary<string, string>
            {
                { "Panel/Title", "DAILY RANKING" },
                { "Panel/Subtitle", "Today's ranked score" },
                { "Panel/Header/HPlace", "#" },
                { "Panel/Header/HName", "Player" },
                { "Panel/Header/HRank", "Rank" },
                { "Panel/Header/HScore", "Score" },
                { "Panel/Header/HGames", "Games" },
                { "Panel/EmptyText",
                  "No ranked games recorded today yet.\nPlay a ranked match (3P or 4P) and you will appear here." },
                { "Panel/BtnPrev/Label", "◀ Prev" },
                { "Panel/BtnNext/Label", "Next ▶" },
                { "Panel/BtnRefresh/Label", "REFRESH" },
                { "Panel/Footer",
                  "Score = raw points (1000 pts = 1) + placement bonus (1st +30 / 2nd +10 / 3rd -10 / 4th -30; "
                  + "3-player is +30/0/-30; East Only counts half).\n"
                  + "Free matches are not counted. Resets at 5:00 AM JST. Players who leave drop off the board." },
            }
        },

        // --- 段位ランキング ---
        { "InstanceRankPanel", new Dictionary<string, string>
            {
                { "Panel/Title", "RANK STANDINGS" },
                { "Panel/Subtitle", "Everyone currently in this instance, ordered by rank" },
                { "Panel/DateText", "Your rank is shared across every mode and only moves in ranked matches" },
                { "Panel/Header/HPlace", "#" },
                { "Panel/Header/HName", "Player" },
                { "Panel/Header/HRank", "Rank" },
                { "Panel/Header/HScore", "Points" },
                { "Panel/Header/HGames", "Total" },
                { "Panel/EmptyText",
                  "No players with readable rank data yet.\nRank data loads a few seconds after someone joins." },
                { "Panel/BtnPrev/Label", "◀ Prev" },
                { "Panel/BtnNext/Label", "Next ▶" },
                { "Panel/BtnRefresh/Label", "REFRESH" },
                { "Panel/Footer",
                  "Points show your position inside your current rank (current pt / pt needed to promote). "
                  + "Celestial Beast shows its Celestial Orb value instead.\n"
                  + "Total counts ranked games only (games that moved your rank); the count restarted on 2026-09-14. "
                  + "Players whose data has not loaded yet are not listed." },
            }
        },

        // --- 本日の最高和了ランキング ---
        { "InstanceBestAgariPanel", new Dictionary<string, string>
            {
                { "Panel/Title", "BEST HAND TODAY" },
                { "Panel/Subtitle", "Highest single win in today's ranked matches" },
                { "Panel/Header/HPlace", "#" },
                { "Panel/Header/HName", "Player" },
                { "Panel/Header/HRank", "Rank" },
                { "Panel/Header/HScore", "Points" },
                { "Panel/Header/HGames", "Hand" },
                { "Panel/EmptyText",
                  "No winning hands recorded today yet.\nWin a hand in a ranked match (3P or 4P) and you will appear here." },
                { "Panel/BtnPrev/Label", "\u25C0 Prev" },
                { "Panel/BtnNext/Label", "Next \u25B6" },
                { "Panel/BtnRefresh/Label", "REFRESH" },
                { "Panel/Footer",
                  "Points are what you actually received for one winning hand (riichi sticks and honba included). "
                  + "Ranked matches only. Resets at 5:00 AM JST." },
            }
        },

        // --- 音量設定パネル ---
        // 既に「日本語 / ENGLISH」の併記になっている短いラベルも、言語が決まるなら
        // 片方だけ出したほうが読みやすいので差し替える。
        { "VolumePanel", new Dictionary<string, string>
            {
                { "Panel/Title", "SOUND VOLUME" },
                { "Panel/Subtitle", "Local to you — your settings are kept for next time" },
                { "Panel/Footer", "To turn everything down at once, use the World volume in your VRChat menu." },
                { "Panel/BtnReset/Label", "RESET" },
                { "Panel/Row0/Label", "Riichi BGM" },
                { "Panel/Row1/Label", "Tiles" },
                { "Panel/Row2/Label", "Effects" },
                { "Panel/Row3/Label", "Voice" },
                { "Panel/Row4/Label", "Ambience" },
                // 行の並び順は BuildVolumePanel.RowOrder が決める。
                // ここの番号は RowKeys の並び（＝Row の GameObject 名）と対。
                { "Panel/Row5/Label", "Table" },
                { "Panel/Row6/Label", "Timer" },
                { "Panel/Row7/Label", "UI" },
                { "Panel/Row8/Label", "Notices" },
            }
        },
    };

    // 上のパネルのうち、スクリプトが実行時に書き換えるラベル（Localizer に載せない）。
    // 末尾が "/" のものは前方一致（表の行セルのようにまとめて除外したいとき）。
    static readonly Dictionary<string, HashSet<string>> ExtraPanelDynamic =
        new Dictionary<string, HashSet<string>>
    {
        // Panel/Table/ 配下は順位・名前・段位・スコア・対局数の全セルを毎回書き換える。
        { "InstanceRankingPanel", new HashSet<string>
            { "Panel/DateText", "Panel/SelfText", "Panel/PageText", "Panel/Table/" } },
        { "InstanceRankPanel", new HashSet<string>
            { "Panel/SelfText", "Panel/PageText", "Panel/Table/" } },
        { "InstanceBestAgariPanel", new HashSet<string>
            { "Panel/DateText", "Panel/SelfText", "Panel/PageText", "Panel/Table/" } },
        { "VolumePanel", new HashSet<string>
            { "Panel/Row0/Value", "Panel/Row1/Value", "Panel/Row2/Value",
              "Panel/Row3/Value", "Panel/Row4/Value", "Panel/Row5/Value",
              "Panel/Row6/Value", "Panel/Row7/Value", "Panel/Row8/Value" } },
    };

    // ============================================================
    // Build
    // ============================================================

    [MenuItem("Tools/Kemono Mahjong/Build Language")]
    public static void Build()
    {
        var manager = EnsureManager();
        if (manager == null) { return; }

        var mainMenu = Object.FindObjectOfType<MainMenuController>(true);
        if (mainMenu != null)
        {
            EnsureLocalizerOn(mainMenu.gameObject);
        }
        else
        {
            Debug.LogWarning("[BuildLanguage] MainMenuController が見つかりません。メインメニューの翻訳はスキップします。");
        }

        foreach (var rootName in ExtraPanels.Keys)
        {
            var root = FindRoot(rootName);
            if (root == null)
            {
                Debug.LogWarning("[BuildLanguage] '" + rootName + "' がシーンに見つかりません。この画面の翻訳はスキップします。");
                continue;
            }
            EnsureLocalizerOn(root);
        }

        Wire();
    }

    static KemonoLanguage EnsureManager()
    {
        var existing = Object.FindObjectOfType<KemonoLanguage>(true);
        if (existing != null) { return existing; }

        var go = GameObject.Find(ManagerName);
        if (go == null)
        {
            go = new GameObject(ManagerName);
            Undo.RegisterCreatedObjectUndo(go, "Create LanguageManager");
        }

        var added = (KemonoLanguage)UdonSharpUndo.AddComponent(go, typeof(KemonoLanguage));
        if (added == null)
        {
            Debug.LogError("[BuildLanguage] KemonoLanguage の追加に失敗しました。");
            return null;
        }
        Debug.Log("[BuildLanguage] LanguageManager を作成しました。");
        return added;
    }

    static KemonoLocalizer EnsureLocalizerOn(GameObject go)
    {
        var existing = go.GetComponent<KemonoLocalizer>();
        if (existing != null) { return existing; }
        return (KemonoLocalizer)UdonSharpUndo.AddComponent(go, typeof(KemonoLocalizer));
    }

    // ============================================================
    // Wire
    // ============================================================

    [MenuItem("Tools/Kemono Mahjong/Wire Language")]
    public static void Wire()
    {
        var manager = Object.FindObjectOfType<KemonoLanguage>(true);
        if (manager == null)
        {
            Debug.LogError("[WireLanguage] KemonoLanguage がシーンにありません。先に Build Language を実行してください。");
            return;
        }

        // --- 卓 ---
        var tables = Object.FindObjectsOfType<Mahjong>(true)
            .OrderBy(m => m.gameObject.name, System.StringComparer.Ordinal)
            .ToArray();
        manager.tables = tables;

        foreach (var t in tables)
        {
            if (t.languageManager == manager) { continue; }
            t.languageManager = manager;
            UdonSharpEditorUtility.CopyProxyToUdon(t);
            EditorUtility.SetDirty(t);
        }

        // --- メインメニュー ---
        var mainMenu = Object.FindObjectOfType<MainMenuController>(true);
        manager.mainMenu = mainMenu;

        int labelCount = 0;
        koMissing_.Clear();
        zhMissing_.Clear();
        if (mainMenu != null)
        {
            // ワールド設定画面の言語ボタンがここを叩く（卓の Jp/En ボタンは廃止済み）。
            if (mainMenu.languageManager != manager)
            {
                mainMenu.languageManager = manager;
                UdonSharpEditorUtility.CopyProxyToUdon(mainMenu);
                EditorUtility.SetDirty(mainMenu);
            }

            var localizer = EnsureLocalizerOn(mainMenu.gameObject);
            if (localizer != null)
            {
                labelCount = FillLocalizer(localizer, mainMenu.transform, EnglishByPath);
                localizer.languageManager = manager;
                UdonSharpEditorUtility.CopyProxyToUdon(localizer);
                EditorUtility.SetDirty(localizer);
            }
        }

        // --- メインメニュー以外のパネル（ランキング2枚・音量設定） ---
        int extraPanels = 0;
        foreach (var pair in ExtraPanels)
        {
            var root = FindRoot(pair.Key);
            if (root == null) { continue; }

            var localizer = EnsureLocalizerOn(root);
            if (localizer == null) { continue; }

            labelCount += FillLocalizer(localizer, root.transform, pair.Value);
            localizer.languageManager = manager;
            UdonSharpEditorUtility.CopyProxyToUdon(localizer);
            EditorUtility.SetDirty(localizer);
            ++extraPanels;
        }

        // --- 段位名（ほかの画面が引用するので専用の枠） ---
        var statsStore = Object.FindObjectOfType<PlayerStatsStore>(true);
        manager.statsStore = statsStore;
        if (statsStore != null && statsStore.languageManager != manager)
        {
            statsStore.languageManager = manager;
            UdonSharpEditorUtility.CopyProxyToUdon(statsStore);
            EditorUtility.SetDirty(statsStore);
        }

        // --- ApplyLanguageJa / ApplyLanguageEn を持つその他の受け手 ---
        var extras = new List<UdonSharpBehaviour>();
        extras.AddRange(Object.FindObjectsOfType<InstanceRankingBoard>(true));
        extras.AddRange(Object.FindObjectsOfType<VolumeSettingsPanel>(true));
        extras.AddRange(Object.FindObjectsOfType<OverheadNameplateManager>(true));
        extras.AddRange(Object.FindObjectsOfType<MahjongAgariEffect>(true));
        extras.AddRange(Object.FindObjectsOfType<MahjongYakuGuide>(true));

        var extraTargets = extras
            .Where(b => b != null)
            .OrderBy(b => Path(b.transform), System.StringComparer.Ordinal)
            .ToArray();
        manager.extraTargets = extraTargets;

        foreach (var b in extraTargets)
        {
            if (!AssignLanguageManager(b, manager)) { continue; }
            UdonSharpEditorUtility.CopyProxyToUdon(b);
            EditorUtility.SetDirty(b);
        }

        // --- 入場注意書き（日英の本文は向こうが持っている。初期言語だけ渡す） ---
        var entryNotice = Object.FindObjectOfType<EntryNoticeGate>(true);
        manager.entryNotice = entryNotice;
        if (entryNotice != null && entryNotice.languageManager != manager)
        {
            entryNotice.languageManager = manager;
            UdonSharpEditorUtility.CopyProxyToUdon(entryNotice);
            EditorUtility.SetDirty(entryNotice);
        }

        // --- Localizer 一覧（メインメニュー以外の対訳表もここで拾う） ---
        var localizers = Object.FindObjectsOfType<KemonoLocalizer>(true)
            .OrderBy(l => Path(l.transform), System.StringComparer.Ordinal)
            .ToArray();
        manager.localizers = localizers;

        foreach (var l in localizers)
        {
            if (l.languageManager == manager) { continue; }
            l.languageManager = manager;
            UdonSharpEditorUtility.CopyProxyToUdon(l);
            EditorUtility.SetDirty(l);
        }

        UdonSharpEditorUtility.CopyProxyToUdon(manager);
        EditorUtility.SetDirty(manager);
        EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);

        Debug.Log("[WireLanguage] 完了: 卓 " + tables.Length
                  + " / メインメニュー " + (mainMenu != null ? "OK" : "なし")
                  + " / 入場注意書き " + (entryNotice != null ? "OK" : "なし")
                  + " / 段位名 " + (statsStore != null ? "OK" : "なし")
                  + " / その他パネル " + extraPanels
                  + " / その他の受け手 " + extraTargets.Length
                  + " / Localizer " + localizers.Length
                  + " / 静的ラベル " + labelCount + " 件");
        if (koMissing_.Count > 0)
        {
            // 韓国語が無い行は英語で出る（壊れはしない）。KoreanText に足して Wire し直す。
            Debug.LogWarning("[WireLanguage] 韓国語なし " + koMissing_.Count + " 件: "
                             + string.Join(" / ", koMissing_.Distinct().ToArray()));
        }
        if (zhMissing_.Count > 0)
        {
            // 中国語が無い行は英語で出る（壊れはしない）。ChineseText に足して Wire し直す。
            Debug.LogWarning("[WireLanguage] 中国語なし " + zhMissing_.Count + " 件: "
                             + string.Join(" / ", zhMissing_.Distinct().ToArray()));
        }
    }

    // シーンルート直下から名前で探す（畳まれていても拾えるよう全走査する）。
    static GameObject FindRoot(string name)
    {
        foreach (var t in Object.FindObjectsOfType<Transform>(true))
        {
            if (t.name == name && t.gameObject.scene.IsValid()) { return t.gameObject; }
        }
        return null;
    }

    // 型ごとに languageManager フィールドへ代入する。既に同じなら false を返す。
    static bool AssignLanguageManager(UdonSharpBehaviour b, KemonoLanguage manager)
    {
        var board = b as InstanceRankingBoard;
        if (board != null)
        {
            if (board.languageManager == manager) { return false; }
            board.languageManager = manager;
            return true;
        }

        var volume = b as VolumeSettingsPanel;
        if (volume != null)
        {
            if (volume.languageManager == manager) { return false; }
            volume.languageManager = manager;
            return true;
        }

        var plates = b as OverheadNameplateManager;
        if (plates != null)
        {
            if (plates.languageManager == manager) { return false; }
            plates.languageManager = manager;
            return true;
        }

        var fx = b as MahjongAgariEffect;
        if (fx != null)
        {
            if (fx.languageManager == manager) { return false; }
            fx.languageManager = manager;
            return true;
        }

        var yaku = b as MahjongYakuGuide;
        if (yaku != null)
        {
            if (yaku.languageManager == manager) { return false; }
            yaku.languageManager = manager;
            return true;
        }

        return false;
    }

    // パネル配下の静的ラベルを対訳表へ焼く。
    // 日本語＝いま表示されている文字列、英語＝引数の対訳表。
    // Wire 中に見つかった韓国語の欠け（Wire の最後にまとめて警告する）。
    static readonly List<string> koMissing_ = new List<string>();
    // 同じく中国語の欠け（簡体・繁体は同じ表の2列なので欠けも同じ。簡体字側で数える）。
    static readonly List<string> zhMissing_ = new List<string>();

    static int FillLocalizer(KemonoLocalizer localizer, Transform panelRoot,
        Dictionary<string, string> englishByPath)
    {
        var texts = new List<TextMeshProUGUI>();
        var ja = new List<string>();
        var en = new List<string>();

        foreach (var tmp in panelRoot.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            var path = RelativePath(panelRoot, tmp.transform);
            string english;
            if (!englishByPath.TryGetValue(path, out english)) { continue; }

            // Wire を英語表示のまま回してしまった場合に日本語を失わないための保険。
            // いまの表示が英訳と同じなら、既存の対訳表に入っている日本語を使い続ける。
            var japanese = tmp.text;
            if (japanese == english)
            {
                japanese = PreviousJapanese(localizer, tmp) ?? japanese;
            }

            texts.Add(tmp);
            ja.Add(japanese);
            en.Add(english);
        }

        localizer.tmpTexts = texts.ToArray();
        localizer.tmpJa = ja.ToArray();
        localizer.tmpEn = en.ToArray();
        // 韓国語は英→韓の対訳表（KoreanText）から引く。無い行は空＝実行時は英語を出す。
        localizer.tmpKo = KoreanText.KoArray(localizer.tmpEn, koMissing_);
        // 中国語は英→中の対訳表（ChineseText）から引く。簡体字と繁体字は同じ行の2列。
        localizer.tmpZhs = ChineseText.ZhArray(localizer.tmpEn, false, zhMissing_);
        localizer.tmpZht = ChineseText.ZhArray(localizer.tmpEn, true, null);
        return texts.Count;
    }

    static string PreviousJapanese(KemonoLocalizer localizer, TextMeshProUGUI target)
    {
        if (localizer.tmpTexts == null || localizer.tmpJa == null) { return null; }
        for (int i = 0; i < localizer.tmpTexts.Length && i < localizer.tmpJa.Length; ++i)
        {
            if (localizer.tmpTexts[i] == target) { return localizer.tmpJa[i]; }
        }
        return null;
    }

    // ============================================================
    // Validate
    // ============================================================

    [MenuItem("Tools/Kemono Mahjong/Validate Language")]
    public static void Validate()
    {
        int errors = 0;
        int warnings = 0;

        var manager = Object.FindObjectOfType<KemonoLanguage>(true);
        if (manager == null)
        {
            Debug.LogError("[ValidateLanguage] KemonoLanguage がシーンにありません。");
            return;
        }

        var tables = Object.FindObjectsOfType<Mahjong>(true);
        if (manager.tables == null || manager.tables.Length != tables.Length)
        {
            Debug.LogError("[ValidateLanguage] tables の数が卓の数と違います（manager="
                           + (manager.tables == null ? 0 : manager.tables.Length)
                           + " / scene=" + tables.Length + "）。Wire Language を実行してください。");
            ++errors;
        }

        foreach (var t in tables)
        {
            if (t.languageManager == null)
            {
                Debug.LogError("[ValidateLanguage] languageManager 未割当: " + Path(t.transform), t);
                ++errors;
            }
        }

        var gate = Object.FindObjectOfType<EntryNoticeGate>(true);
        if (gate != null && (gate.languageManager != manager || manager.entryNotice != gate))
        {
            Debug.LogError("[ValidateLanguage] 入場注意書きが未配線です。Wire Language を実行してください。", gate);
            ++errors;
        }

        var mainMenu = Object.FindObjectOfType<MainMenuController>(true);
        if (mainMenu == null)
        {
            Debug.LogWarning("[ValidateLanguage] MainMenuController が見つかりません。");
            ++warnings;
        }
        else
        {
            if (manager.mainMenu != mainMenu)
            {
                Debug.LogError("[ValidateLanguage] manager.mainMenu が未割当です。Wire Language を実行してください。");
                ++errors;
            }

            if (mainMenu.languageManager != manager)
            {
                Debug.LogError("[ValidateLanguage] MainMenuController.languageManager が未割当です。"
                               + "ワールド設定の言語ボタンが効きません。Wire Language を実行してください。", mainMenu);
                ++errors;
            }

            if (mainMenu.screenWorld == null)
            {
                Debug.LogError("[ValidateLanguage] ScreenWorld が未割当です。"
                               + "Build Main Menu → Wire Main Menu を実行してください。", mainMenu);
                ++errors;
            }

            var localizer = mainMenu.GetComponent<KemonoLocalizer>();
            if (localizer == null)
            {
                Debug.LogError("[ValidateLanguage] MainMenu に KemonoLocalizer がありません。Build Language を実行してください。", mainMenu);
                ++errors;
            }
            else
            {
                int n = localizer.tmpTexts == null ? 0 : localizer.tmpTexts.Length;
                if (localizer.tmpJa == null || localizer.tmpJa.Length != n ||
                    localizer.tmpEn == null || localizer.tmpEn.Length != n)
                {
                    Debug.LogError("[ValidateLanguage] Localizer の配列長が揃っていません。Wire Language を実行してください。", localizer);
                    ++errors;
                }
                // ExtraPanels と同じ件数照合。ここが無かったせいで、分析パネルを増やした後に
                // Wire Language を回し忘れても Validate が素通りしていた（プロフィールが日本語のまま）。
                int want = EnglishByPath.Keys.Count(k => mainMenu.transform.Find(k) != null);
                if (n != want)
                {
                    Debug.LogError("[ValidateLanguage] MainMenu の対訳件数が合いません（"
                                   + n + " / 期待 " + want + "）。Wire Language を実行してください。", localizer);
                    ++errors;
                }
                for (int i = 0; i < n; ++i)
                {
                    if (localizer.tmpTexts[i] == null)
                    {
                        Debug.LogError("[ValidateLanguage] Localizer[" + i + "] の対象が失われています。", localizer);
                        ++errors;
                    }
                }
            }

            // 未翻訳ラベルの洗い出し。
            foreach (var tmp in mainMenu.transform.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                // ExtraPanels のパネルは MainMenu の子に置かれていても、自分の
                // KemonoLocalizer が対訳を持つ（音量設定＝ScreenWorld/PageSound/VolumePanel）。
                // ここで数えると同じラベルを二重に見て未翻訳扱いになる。
                if (HasNestedLocalizer(mainMenu.transform, tmp.transform)) { continue; }

                var path = RelativePath(mainMenu.transform, tmp.transform);
                if (EnglishByPath.ContainsKey(path)) { continue; }
                if (DynamicPaths.Contains(path)) { continue; }
                if (NoTranslationPaths.Contains(path)) { continue; }
                if (!HasJapanese(tmp.text)) { continue; }

                Debug.LogWarning("[ValidateLanguage] 未翻訳のラベル: " + path
                                 + " 「" + tmp.text + "」（BuildLanguage.EnglishByPath に追加してください）", tmp);
                ++warnings;
            }

            // 対訳表にあるのにシーンに無いパス（BuildMainMenu の改名に取り残された分）。
            foreach (var key in EnglishByPath.Keys)
            {
                if (mainMenu.transform.Find(key) == null)
                {
                    Debug.LogWarning("[ValidateLanguage] EnglishByPath のパスがシーンにありません: " + key);
                    ++warnings;
                }
            }
        }

        // --- メインメニュー以外のパネル ---
        foreach (var pair in ExtraPanels)
        {
            var root = FindRoot(pair.Key);
            if (root == null)
            {
                Debug.LogWarning("[ValidateLanguage] '" + pair.Key + "' がシーンにありません。");
                ++warnings;
                continue;
            }

            var localizer = root.GetComponent<KemonoLocalizer>();
            if (localizer == null || localizer.languageManager != manager)
            {
                Debug.LogError("[ValidateLanguage] '" + pair.Key + "' の KemonoLocalizer が未設置/未配線です。"
                               + "Build Language を実行してください。", root);
                ++errors;
                continue;
            }

            int want = pair.Value.Keys.Count(k => root.transform.Find(k) != null);
            int have = localizer.tmpTexts == null ? 0 : localizer.tmpTexts.Length;
            if (have != want)
            {
                Debug.LogError("[ValidateLanguage] '" + pair.Key + "' の対訳件数が合いません（"
                               + have + " / 期待 " + want + "）。Wire Language を実行してください。", root);
                ++errors;
            }

            foreach (var key in pair.Value.Keys)
            {
                if (root.transform.Find(key) == null)
                {
                    Debug.LogWarning("[ValidateLanguage] " + pair.Key + " のパスがシーンにありません: " + key);
                    ++warnings;
                }
            }

            HashSet<string> dyn;
            if (!ExtraPanelDynamic.TryGetValue(pair.Key, out dyn)) { dyn = new HashSet<string>(); }

            foreach (var tmp in root.transform.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                var path = RelativePath(root.transform, tmp.transform);
                if (pair.Value.ContainsKey(path)) { continue; }
                if (IsDynamic(dyn, path)) { continue; }
                if (!HasJapanese(tmp.text)) { continue; }

                Debug.LogWarning("[ValidateLanguage] 未翻訳のラベル: " + pair.Key + "/" + path
                                 + " 「" + tmp.text + "」", tmp);
                ++warnings;
            }
        }

        // --- 段位名・その他の受け手 ---
        var store = Object.FindObjectOfType<PlayerStatsStore>(true);
        if (store != null && (manager.statsStore != store || store.languageManager != manager))
        {
            Debug.LogError("[ValidateLanguage] PlayerStatsStore が未配線です。Wire Language を実行してください。", store);
            ++errors;
        }

        int extraWant = Object.FindObjectsOfType<InstanceRankingBoard>(true).Length
                      + Object.FindObjectsOfType<VolumeSettingsPanel>(true).Length
                      + Object.FindObjectsOfType<OverheadNameplateManager>(true).Length
                      + Object.FindObjectsOfType<MahjongAgariEffect>(true).Length
                      + Object.FindObjectsOfType<MahjongYakuGuide>(true).Length;
        int extraHave = manager.extraTargets == null ? 0 : manager.extraTargets.Length;
        if (extraHave != extraWant)
        {
            Debug.LogError("[ValidateLanguage] extraTargets の数が合いません（" + extraHave
                           + " / 期待 " + extraWant + "）。Wire Language を実行してください。");
            ++errors;
        }

        if (errors == 0)
        {
            Debug.Log("[ValidateLanguage] OK（警告 " + warnings + " 件）");
        }
        else
        {
            Debug.LogError("[ValidateLanguage] エラー " + errors + " 件 / 警告 " + warnings + " 件");
        }
    }

    // ============================================================
    // ヘルパ
    // ============================================================

    // panelRoot と target の間に、target 側の面倒を見る別の KemonoLocalizer があるか。
    // panelRoot 自身に付いているものは数えない。
    static bool HasNestedLocalizer(Transform panelRoot, Transform target)
    {
        for (var t = target; t != null && t != panelRoot; t = t.parent)
        {
            if (t.GetComponent<KemonoLocalizer>() != null) { return true; }
        }
        return false;
    }

    // 動的ラベルか。末尾 "/" の項目は前方一致で扱う。
    static bool IsDynamic(HashSet<string> dyn, string path)
    {
        if (dyn.Contains(path)) { return true; }
        foreach (var d in dyn)
        {
            if (d.EndsWith("/") && path.StartsWith(d)) { return true; }
        }
        return false;
    }

    static bool HasJapanese(string s)
    {
        if (string.IsNullOrEmpty(s)) { return false; }
        foreach (var c in s)
        {
            if ((c >= 0x3040 && c <= 0x30FF) || (c >= 0x4E00 && c <= 0x9FFF) ||
                (c >= 0xFF00 && c <= 0xFF60))
            {
                return true;
            }
        }
        return false;
    }

    static string RelativePath(Transform root, Transform t)
    {
        var parts = new List<string>();
        while (t != null && t != root)
        {
            parts.Add(t.name);
            t = t.parent;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    static string Path(Transform t)
    {
        var s = t.name;
        while (t.parent != null)
        {
            t = t.parent;
            s = t.name + "/" + s;
        }
        return s;
    }
}
#endif
