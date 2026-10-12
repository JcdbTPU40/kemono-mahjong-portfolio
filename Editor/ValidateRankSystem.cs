#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using SuzuFactory.AozoraMahjong;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 段位計算のproduction APIと、現在シーンの最小配線を非破壊で検証する。
/// 計算式はここへ再実装せず、期待値だけを固定してPlayerStatsStoreを直接呼ぶ。
/// </summary>
public static class ValidateRankSystem
{
    private static int errors_;
    private static int warnings_;
    private static int passed_;

    [MenuItem("Tools/Kemono Mahjong/Validate Rank System")]
    public static void RunFromMenu()
    {
        RunValidation();
    }

    public static bool RunValidation()
    {
        errors_ = 0;
        warnings_ = 0;
        passed_ = 0;

        var stores = UnityEngine.Object.FindObjectsOfType<PlayerStatsStore>(true);
        if (stores == null || stores.Length == 0)
        {
            Fail("PlayerStatsStoreがシーンに存在する", 0, 1);
            LogSummary();
            return false;
        }

        var store = stores[0];
        ValidateTables(store);
        ValidateBasePoints(store);
        ValidateRankTransitions(store);
        ValidateTenju(store);
        ValidateTonpu(store);
        ValidateSanitizationAndLabels(store);
        ValidatePendingIdempotence();
        ValidateSceneWiring(store);
        ValidateRankAdminMonitor(store);
        ValidateTakeoverClassification();
        ValidateSourceVersion();

        LogSummary();
        return errors_ == 0;
    }

    private static void ValidateTables(PlayerStatsStore store)
    {
        int[] initial = { 0, 40, 100, 300, 400, 500, 600, 700, 1000, 1400, 1600, 1800, 2000, 3000, 4500 };
        int[] promotion = { 20, 80, 200, 600, 800, 1000, 1200, 1400, 2000, 2800, 3200, 3600, 4000, 6000, 9000 };
        int[,] yonma =
        {
            { 35, 15, -5, -15 }, { 35, 15, -5, -15 }, { 35, 15, -5, -15 },
            { 55, 25, -5, -35 }, { 55, 25, -5, -55 }, { 55, 25, -5, -75 },
            { 95, 45, -5, -95 }, { 95, 45, -5, -115 }, { 95, 45, -5, -135 },
            { 125, 60, -5, -180 }, { 125, 60, -5, -195 }, { 125, 60, -5, -210 },
            { 135, 65, -5, -225 }, { 135, 65, -5, -240 }, { 135, 65, -5, -255 }
        };
        int[,] sanma =
        {
            { 45, 0, -15 }, { 45, 0, -15 }, { 45, 0, -15 },
            { 75, 0, -35 }, { 75, 0, -55 }, { 75, 0, -75 },
            { 120, 0, -95 }, { 120, 0, -115 }, { 120, 0, -135 },
            { 175, 0, -180 }, { 175, 0, -205 }, { 175, 0, -230 },
            { 255, 0, -255 }, { 255, 0, -280 }, { 255, 0, -305 }
        };

        for (int lv = 0; lv < 15; ++lv)
        {
            Eq("初期点 Lv" + lv, store.GetInitialRankPtForLv(lv), initial[lv]);
            Eq("昇段点 Lv" + lv, store.GetPromotionRankPtForLv(lv), promotion[lv]);
            if (lv > 0)
            {
                Eq("昇段後は昇段点の50% Lv" + lv,
                   store.GetInitialRankPtForLv(lv) * 2, store.GetPromotionRankPtForLv(lv));
            }
            for (int place = 1; place <= 4; ++place)
            {
                Eq("四麻順位点 Lv" + lv + " place" + place,
                   store.GetPlacementRankPoint(false, lv, place), yonma[lv, place - 1]);
            }
            for (int place = 1; place <= 3; ++place)
            {
                Eq("三麻順位点 Lv" + lv + " place" + place,
                   store.GetPlacementRankPoint(true, lv, place), sanma[lv, place - 1]);
            }
        }
    }

    private static void ValidateBasePoints(PlayerStatsStore store)
    {
        Eq("四麻 +15200", store.CalculateBaseRankPoint(false, 40200), 16);
        Eq("四麻 +500", store.CalculateBaseRankPoint(false, 25500), 1);
        Eq("四麻 0", store.CalculateBaseRankPoint(false, 25000), 0);
        Eq("四麻 -500", store.CalculateBaseRankPoint(false, 24500), -1);
        Eq("四麻 -12700", store.CalculateBaseRankPoint(false, 12300), -13);
        Eq("三麻 +7500", store.CalculateBaseRankPoint(true, 42500), 8);
    }

    private static void ValidateRankTransitions(PlayerStatsStore store)
    {
        // 新規プレイヤー相当。
        Eq("新規Lv", store.SanitizeRankLv(0), 0);
        Eq("新規pt", store.SanitizeRankPtForLv(0, 0), 0);
        Eq("新規昇段点", store.GetPromotionRankPtForLv(0), 20);

        int[] r = store.CalculateRankedHanchanResult(false, 1, 35000, 0, 0, false);
        Result("四麻初獣1位", r, 1, 65, 100, 45);

        r = store.CalculateRankedHanchanResult(false, 1, 40000, 0, 0, false);
        Result("初獣1の0ptから+50は初獣2の70pt", r, 1, 70, 100, 50);

        r = store.CalculateRankedHanchanResult(false, 1, 90000, 0, 0, false);
        Result("余剰ptで複数昇段", r, 2, 140, 100, 100);

        r = store.CalculateRankedHanchanResult(true, 1, 42500, 6, 600, false);
        Result("三麻剛牙1位", r, 6, 728, 100, 128);

        r = store.CalculateRankedHanchanResult(false, 4, 12300, 10, 1600, false);
        Result("四麻獣威2四位", r, 10, 1392, 100, -208);

        r = store.CalculateRankedHanchanResult(true, 2, 35500, 6, 600, false);
        Result("三麻2位は素点のみ", r, 6, 601, 100, 1);

        r = store.CalculateRankedHanchanResult(false, 4, 25000, 4, 55, false);
        Result("0ちょうどは降段しない", r, 4, 0, 100, -55);

        r = store.CalculateRankedHanchanResult(false, 4, 25000, 4, 54, false);
        Result("0未満は前段位初期点", r, 3, 300, 100, -55);

        r = store.CalculateRankedHanchanResult(false, 4, 25000, 0, 0, false);
        Result("初獣は0未満にならない", r, 0, 0, 100, -15);
        r = store.CalculateRankedHanchanResult(false, 4, 25000, 3, 0, false);
        Result("鋭爪1は0未満にならない", r, 3, 0, 100, -35);

        r = store.CalculateRankedHanchanResult(false, 1, 25000, 14, 8999, false);
        Result("幻獣3から天獣1", r, 15, 0, 100, 135);
    }

    private static void ValidateTenju(PlayerStatsStore store)
    {
        int[] r = store.CalculateRankedHanchanResult(false, 4, 0, 15, 1, false);
        Result("天獣1から幻獣3", r, 14, 4500, 100, -5);

        r = store.CalculateRankedHanchanResult(false, 4, 0, 15, 5, false);
        Result("天珠0ちょうどは下降しない", r, 15, 0, 0, -5);

        r = store.CalculateRankedHanchanResult(false, 1, 0, 34, 198, false);
        Result("天獣20上限", r, 34, 0, 200, 5);

        r = store.CalculateRankedHanchanResult(false, 1, 0, 15, 100, true);
        Result("四麻全員天獣2倍", r, 15, 0, 110, 10);
        r = store.CalculateRankedHanchanResult(true, 1, 0, 15, 100, true);
        Result("三麻全員天獣2倍", r, 15, 0, 110, 10);
        r = store.CalculateRankedHanchanResult(true, 1, 0, 15, 100, false);
        Result("天獣以外混在は等倍", r, 15, 0, 105, 5);

        Eq("四麻天獣2位", store.GetTenjuDeltaX10(false, 2, false), 2);
        Eq("四麻天獣3位", store.GetTenjuDeltaX10(false, 3, false), -2);
        Eq("三麻天獣2位", store.GetTenjuDeltaX10(true, 2, false), 0);
    }

    // 東風戦（2026-08-18追加）：順位点・天珠が半荘の半分になることを検証する。
    // 素点は圧縮しない。端数は必ず絶対値方向へ切り捨てる（半荘の50%を超えさせない）。
    private static void ValidateTonpu(PlayerStatsStore store)
    {
        // 順位点：代表的なLvで実値を固定する。
        int[] yonmaLv0Tonpu = { 17, 7, -2, -7 };    // 半荘 { 35, 15, -5, -15 }
        int[] yonmaLv14Tonpu = { 67, 32, -2, -127 }; // 半荘 { 135, 65, -5, -255 }
        int[] sanmaLv0Tonpu = { 22, 0, -7 };         // 半荘 { 45, 0, -15 }
        int[] sanmaLv14Tonpu = { 127, 0, -152 };     // 半荘 { 255, 0, -305 }

        for (int place = 1; place <= 4; ++place)
        {
            Eq("四麻東風順位点 Lv0 place" + place,
               store.GetPlacementRankPointFor(false, true, 0, place), yonmaLv0Tonpu[place - 1]);
            Eq("四麻東風順位点 Lv14 place" + place,
               store.GetPlacementRankPointFor(false, true, 14, place), yonmaLv14Tonpu[place - 1]);
        }
        for (int place = 1; place <= 3; ++place)
        {
            Eq("三麻東風順位点 Lv0 place" + place,
               store.GetPlacementRankPointFor(true, true, 0, place), sanmaLv0Tonpu[place - 1]);
            Eq("三麻東風順位点 Lv14 place" + place,
               store.GetPlacementRankPointFor(true, true, 14, place), sanmaLv14Tonpu[place - 1]);
        }

        // 全Lv・全着順で「東風の絶対値 ≦ 半荘の絶対値の半分」を保証する。
        // ここが破れると所要時間半分で獲得半分超＝東風の一択化が起きる。
        for (int lv = 0; lv < 15; ++lv)
        {
            for (int place = 1; place <= 4; ++place)
            {
                int han = store.GetPlacementRankPoint(false, lv, place);
                int ton = store.GetPlacementRankPointFor(false, true, lv, place);
                True("四麻東風は半荘の半分以下 Lv" + lv + " place" + place,
                     Math.Abs(ton) * 2 <= Math.Abs(han) && Math.Sign(ton) == Math.Sign(han));
            }
            for (int place = 1; place <= 3; ++place)
            {
                int han = store.GetPlacementRankPoint(true, lv, place);
                int ton = store.GetPlacementRankPointFor(true, true, lv, place);
                True("三麻東風は半荘の半分以下 Lv" + lv + " place" + place,
                     Math.Abs(ton) * 2 <= Math.Abs(han) && Math.Sign(ton) == Math.Sign(han));
            }
        }

        // isTonpu=false は従来値そのまま（既存半荘の挙動を変えない）。
        for (int lv = 0; lv < 15; ++lv)
        {
            for (int place = 1; place <= 4; ++place)
            {
                Eq("半荘は非圧縮 Lv" + lv + " place" + place,
                   store.GetPlacementRankPointFor(false, false, lv, place),
                   store.GetPlacementRankPoint(false, lv, place));
            }
        }

        // 天珠も半分。全員天獣2倍は「半分にしてから」掛ける。
        Eq("四麻東風天珠1位", store.GetTenjuDeltaX10For(false, true, 1, false), 2);
        Eq("四麻東風天珠2位", store.GetTenjuDeltaX10For(false, true, 2, false), 1);
        Eq("四麻東風天珠3位", store.GetTenjuDeltaX10For(false, true, 3, false), -1);
        Eq("四麻東風天珠4位", store.GetTenjuDeltaX10For(false, true, 4, false), -2);
        Eq("三麻東風天珠1位", store.GetTenjuDeltaX10For(true, true, 1, false), 2);
        Eq("三麻東風天珠2位", store.GetTenjuDeltaX10For(true, true, 2, false), 0);
        Eq("三麻東風天珠3位", store.GetTenjuDeltaX10For(true, true, 3, false), -2);
        Eq("四麻東風天珠1位・全員天獣", store.GetTenjuDeltaX10For(false, true, 1, true), 4);
        Eq("半荘天珠は非圧縮", store.GetTenjuDeltaX10For(false, false, 1, true),
           store.GetTenjuDeltaX10(false, 1, true));

        // 結果計算の通し。半荘の「四麻初獣1位」(lv1/65pt/+45) と同条件で比較する。
        int[] r = store.CalculateRankedGameResult(false, true, 1, 35000, 0, 0, false);
        Result("四麻東風初獣1位（順位点17+素点10）", r, 1, 47, 100, 27);

        r = store.CalculateRankedGameResult(false, true, 4, 12300, 10, 1600, false);
        Result("四麻東風獣威2四位（順位点-97+素点-13）", r, 10, 1490, 100, -110);

        r = store.CalculateRankedGameResult(false, true, 1, 0, 15, 100, false);
        Result("天獣は東風で+0.2", r, 15, 0, 102, 2);
        r = store.CalculateRankedGameResult(false, true, 1, 0, 15, 100, true);
        Result("天獣全員天獣は東風で+0.4", r, 15, 0, 104, 4);

        // 旧APIラッパは isTonpu=false と等価（既存呼び出しの互換性）。
        int[] wrapper = store.CalculateRankedHanchanResult(false, 1, 35000, 0, 0, false);
        int[] explicitHan = store.CalculateRankedGameResult(false, false, 1, 35000, 0, 0, false);
        True("旧CalculateRankedHanchanResultは半荘と等価",
             wrapper[PlayerStatsStore.RANK_RESULT_LV] == explicitHan[PlayerStatsStore.RANK_RESULT_LV] &&
             wrapper[PlayerStatsStore.RANK_RESULT_PT] == explicitHan[PlayerStatsStore.RANK_RESULT_PT] &&
             wrapper[PlayerStatsStore.RANK_RESULT_DELTA] == explicitHan[PlayerStatsStore.RANK_RESULT_DELTA]);
    }

    private static void ValidateSanitizationAndLabels(PlayerStatsStore store)
    {
        Eq("rankLv下限", store.SanitizeRankLv(-999), 0);
        Eq("rankLv上限", store.SanitizeRankLv(999), 34);
        Eq("pt下限", store.SanitizeRankPtForLv(10, -999), 0);
        Eq("pt上限", store.SanitizeRankPtForLv(10, 99999), 3200);
        Eq("天珠下限", store.SanitizeTenjuOrbX10(-999), 0);
        Eq("天珠上限", store.SanitizeTenjuOrbX10(999), 200);

        string[] majorNames = { "初獣", "鋭爪", "剛牙", "獣威", "幻獣" };
        for (int lv = 0; lv < 15; ++lv)
        {
            string expected = majorNames[lv / 3] + "★" + ((lv % 3) + 1);
            Eq("段位名 Lv" + lv, store.GetRankDisplayNameForLv(lv), expected);
            Eq("大段位index Lv" + lv, store.GetMajorRankIndexForLv(lv), lv / 3);
        }
        for (int lv = 15; lv <= 34; ++lv)
        {
            Eq("天獣名 Lv" + lv, store.GetRankDisplayNameForLv(lv), "天獣 Lv." + (lv - 14));
            Eq("天獣大段位index Lv" + lv, store.GetMajorRankIndexForLv(lv), 5);
        }

        Eq("通常表示例", store.GetRankDisplayTextForValues(10, 1600, 0), "獣威★2  1600 / 3200 pt");
        Eq("天獣表示例", store.GetRankDisplayTextForValues(18, 0, 125), "天獣 Lv.4  天珠 12.5 / 20.0");
    }

    private static void ValidatePendingIdempotence()
    {
        var go = new GameObject("__RankValidationStore");
        try
        {
            var store = go.AddComponent<PlayerStatsStore>();
            int token = store.BeginRankedHanchan();
            True("段位戦tokenは正数", token > 0);
            True("復元前RESULTをpending受付",
                 store.ApplyRankedHanchanResult(false, 1, 35000, 0, 0, false, token, true));
            False("同じRESULTの二重受付を拒否",
                  store.ApplyRankedHanchanResult(false, 1, 35000, 0, 0, false, token, true));

            var pending = typeof(PlayerStatsStore).GetField("pendingRankResult_",
                BindingFlags.Instance | BindingFlags.NonPublic);
            True("pending結果が1件保持される", pending != null && (bool)pending.GetValue(store));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    private static void ValidateSceneWiring(PlayerStatsStore store)
    {
        var menus = UnityEngine.Object.FindObjectsOfType<MainMenuController>(true);
        var managers = UnityEngine.Object.FindObjectsOfType<MatchmakingManager>(true);
        var nameplates = UnityEngine.Object.FindObjectsOfType<OverheadNameplateManager>(true);

        Eq("PlayerStatsStore数", UnityEngine.Object.FindObjectsOfType<PlayerStatsStore>(true).Length, 1);
        Eq("MainMenuController数", menus.Length, 1);
        Eq("MatchmakingManager数", managers.Length, 1);
        Eq("OverheadNameplateManager数", nameplates.Length, 1);

        // --- テスト用の解禁素通しが ON のまま残っていないか ---
        // 段位を無視して自画像が選べてしまうので、リリース前に必ず気付けるようにする。
        if (store.debugUnlockSelfie)
        {
            Warn("PlayerStatsStore.debugUnlockSelfie が ON です（段位に関係なく自画像を選べる状態）。"
                 + "テストが終わったら OFF へ戻してください。");
        }
        if (store.debugUnlockRiichiBgm)
        {
            Warn("PlayerStatsStore.debugUnlockRiichiBgm が ON です（段位に関係なく立直BGMを装備できる状態）。"
                 + "テストが終わったら OFF へ戻してください。");
        }

        // 立直BGMの解禁段位。鋭爪★1＝Lv3（大段位1の先頭）から動いていないこと。
        // 表示名（GetRankDisplayNameForLv）はここでは呼ばない：言語解決が Editor では
        // NRE を投げるため、検査そのものが途中で止まる。
        Eq("立直BGMの解禁Lvは鋭爪★1（Lv3）", PlayerStatsStore.RIICHI_BGM_UNLOCK_LV, 3);
        Eq("解禁Lvの大段位は鋭爪（index 1）", store.GetMajorRankIndexForLv(PlayerStatsStore.RIICHI_BGM_UNLOCK_LV), 1);
        Eq("解禁Lvは鋭爪の★1（Lv%3==0）", PlayerStatsStore.RIICHI_BGM_UNLOCK_LV % 3, 0);

        // Inspector の値と Udon 側のヒープを突き合わせる。
        // U# はプロキシと UdonBehaviour のヒープが別物で、コピーは Play 入り／ビルド時に走る。
        // したがって「編集しただけでまだコピーされていない」状態は**正常**なので、
        // 危険な向きだけをエラーにする：
        //   heap=ON かつ proxy=OFF … Inspectorでは切ったつもりなのに**実機で効く**＝危険
        //   heap=OFF かつ proxy=ON … まだコピーされていないだけ＝Play/ビルドで入る（情報）
        {
            var udon = UdonSharpEditorUtility.GetBackingUdonBehaviour(store);
            if (udon != null)
            {
                bool heapValue = false;
                // publicVariables は既定値と同じ変数を持たないので、未登録＝既定値(false)。
                if (udon.publicVariables != null)
                {
                    object raw;
                    if (udon.publicVariables.TryGetVariableValue("debugUnlockSelfie", out raw) && raw is bool)
                    {
                        heapValue = (bool)raw;
                    }
                }

                False("debugUnlockSelfie がUdon側だけONになっていない（Inspectorで切ったのに実機で効く事故）",
                      heapValue && !store.debugUnlockSelfie);

                bool bgmHeapValue = false;
                if (udon.publicVariables != null)
                {
                    object rawBgm;
                    if (udon.publicVariables.TryGetVariableValue("debugUnlockRiichiBgm", out rawBgm) && rawBgm is bool)
                    {
                        bgmHeapValue = (bool)rawBgm;
                    }
                }
                False("debugUnlockRiichiBgm がUdon側だけONになっていない（Inspectorで切ったのに実機で効く事故）",
                      bgmHeapValue && !store.debugUnlockRiichiBgm);

                if (!heapValue && store.debugUnlockSelfie)
                {
                    Warn("debugUnlockSelfie は Inspector で ON ですが、まだ Udon 側へコピーされていません"
                         + "（Play に入るか、ビルドすると反映されます）。");
                }
            }
        }

        if (menus.Length == 1)
        {
            True("MainMenu.statsStore", menus[0].statsStore == store);
            True("MainMenu.voiceSelector", menus[0].voiceSelector != null);
            Eq("MainMenu常駐MahjongVoiceSelector数",
               menus[0].GetComponents<MahjongVoiceSelector>().Length, 1);
            if (menus[0].voiceSelector != null)
            {
                True("MainMenu.voiceSelectorはMainMenu自身に常駐",
                     menus[0].voiceSelector.gameObject == menus[0].gameObject);
            }
            Eq("MainMenu.charaVoicePacksはキャラ数と一致",
               menus[0].charaVoicePacks == null ? -1 : menus[0].charaVoicePacks.Length,
               PlayerStatsStore.NUM_CHARAS);
            if (menus[0].charaVoicePacks != null &&
                menus[0].charaVoicePacks.Length == PlayerStatsStore.NUM_CHARAS)
            {
                Eq("MainMenu.Volボイスパック", menus[0].charaVoicePacks[PlayerStatsStore.CHARA_VOL], 0);
                Eq("MainMenu.Shineボイスパック", menus[0].charaVoicePacks[PlayerStatsStore.CHARA_SHINE], 1);
            }
            True("MainMenu.headerRank", menus[0].headerRank != null);
            True("MainMenu.headerRankIcon", menus[0].headerRankIcon != null);
            True("MainMenu.profileRank", menus[0].profileRank != null);
            True("MainMenu.profileRankIcon", menus[0].profileRankIcon != null);
            Eq("MainMenu.rankIconsは大段位6枠",
               menus[0].rankIcons == null ? -1 : menus[0].rankIcons.Length, 6);
            if (menus[0].rankIcons != null)
            {
                for (int i = 0; i < menus[0].rankIcons.Length; ++i)
                {
                    True("MainMenu.rankIcons[" + i + "]割当", menus[0].rankIcons[i] != null);
                }
            }
            True("MainMenu.matchmaking", menus[0].matchmaking != null);

            // --- 自画像キャラ（撮影ブース） ---
            True("MainMenu.screenSelfie", menus[0].screenSelfie != null);
            Eq("MainMenu.charaLockOverlaysはキャラ数と一致",
               menus[0].charaLockOverlays == null ? -1 : menus[0].charaLockOverlays.Length,
               PlayerStatsStore.NUM_CHARAS);
            Eq("MainMenu.charaLockLabelsはキャラ数と一致",
               menus[0].charaLockLabels == null ? -1 : menus[0].charaLockLabels.Length,
               PlayerStatsStore.NUM_CHARAS);
            if (menus[0].charaVoicePacks != null &&
                menus[0].charaVoicePacks.Length == PlayerStatsStore.NUM_CHARAS)
            {
                Eq("MainMenu.自画像は無音パック",
                   menus[0].charaVoicePacks[PlayerStatsStore.CHARA_SELFIE],
                   MahjongSoundsV2.VOICE_PACK_SILENT);
            }

            var selfie = menus[0].selfieCamera;
            True("MainMenu.selfieCamera", selfie != null);
            if (selfie != null)
            {
                // 畳まれる画面の中に置くとUdonが止まる＝必ず常時アクティブな場所に居ること。
                True("selfieCameraは常時アクティブ", selfie.gameObject.activeInHierarchy);
                True("selfieCamera.menu", selfie.menu == menus[0]);
                True("selfieCamera.captureCamera", selfie.captureCamera != null);
                True("selfieCamera.liveRt", selfie.liveRt != null);
                True("selfieCamera.photoRt", selfie.photoRt != null);
                True("selfieCamera.store", selfie.store != null);

                // 共有サイズのラダー。大きい順に並び、どの段も 255 以下・ARGB32 であること。
                int ladder = selfie.shareRts == null ? 0 : selfie.shareRts.Length;
                True("selfieCamera.shareRts が1段以上ある", ladder >= 1);
                int prevPixels = int.MaxValue;
                for (int li = 0; li < ladder; ++li)
                {
                    var rt = selfie.shareRts[li];
                    True("shareRts[" + li + "] 割当", rt != null);
                    if (rt == null) { continue; }

                    True("shareRts[" + li + "] はアルファ付き(ARGB32)",
                         rt.format == UnityEngine.RenderTextureFormat.ARGB32);
                    // KEY_FMT に幅・高さを1バイトずつ詰めているので255まで。
                    True("shareRts[" + li + "] は255以下", rt.width <= 255 && rt.height <= 255);

                    int pixels = rt.width * rt.height;
                    True("shareRts[" + li + "] は前段より小さい（大きい順）", pixels < prevPixels);
                    prevPixels = pixels;
                }
                if (selfie.liveRt != null)
                {
                    True("ビューファインダRTはアルファ付き(ARGB32)",
                         selfie.liveRt.format == UnityEngine.RenderTextureFormat.ARGB32);
                }
                if (selfie.photoRt != null)
                {
                    True("写真RTはアルファ付き(ARGB32)",
                         selfie.photoRt.format == UnityEngine.RenderTextureFormat.ARGB32);
                }
                True("selfieCamera.viewfinder", selfie.viewfinder != null);
                True("selfieCamera.previewImage", selfie.previewImage != null);
                True("selfieCamera.countdownLabel", selfie.countdownLabel != null);
                True("selfieCamera.infoLabel", selfie.infoLabel != null);
                True("selfieCamera.btnRetake", selfie.btnRetake != null);
                True("selfieCamera.btnConfirm", selfie.btnConfirm != null);
                // 顔ガイドが無いと「枠の真ん中に顔」と勘違いされて絵が下寄りになる。
                True("selfieCamera.faceGuide", selfie.faceGuide != null);
                True("selfieCamera.countdownBack", selfie.countdownBack != null);

                if (selfie.captureCamera != null)
                {
                    // 自分のアバターは **MirrorReflection(18)** に居る。PlayerLocal(10) では
                    // World カメラに写らない。ここを外すと「自分が居ない空の写真」になる。
                    True("撮影カメラはMirrorReflection(18)を写す",
                         (selfie.captureCamera.cullingMask & (1 << 18)) != 0);
                    // 他プレイヤー(9)や背景は入れない＝自動で背景が抜ける・他人を撮れない。
                    False("撮影カメラは他プレイヤー(9)を写さない",
                          (selfie.captureCamera.cullingMask & (1 << 9)) != 0);
                    False("撮影カメラは背景(Default)を写さない",
                          (selfie.captureCamera.cullingMask & 1) != 0);
                    Eq("撮影カメラのclearFlagsはSolidColor",
                       (int)selfie.captureCamera.clearFlags, (int)UnityEngine.CameraClearFlags.SolidColor);
                    Eq("撮影カメラの背景は透明(alpha 0)",
                       Mathf.RoundToInt(selfie.captureCamera.backgroundColor.a * 255f), 0);
                    True("撮影カメラの出力先はliveRt",
                         selfie.captureCamera.targetTexture == selfie.liveRt);
                    False("撮影カメラは既定で停止している", selfie.captureCamera.enabled);
                    True("撮影カメラにAudioListenerが付いていない",
                         selfie.captureCamera.GetComponent<AudioListener>() == null);
                }
            }
        }

        if (nameplates.Length == 1)
        {
            True("Nameplate.statsStore", nameplates[0].statsStore == store);
            True("Nameplate.matchmaking", nameplates[0].matchmaking != null);
            Eq("Nameplate.rankIconsは大段位6枠",
               nameplates[0].rankIcons == null ? -1 : nameplates[0].rankIcons.Length, 6);
            if (nameplates[0].rankIcons != null)
            {
                for (int i = 0; i < nameplates[0].rankIcons.Length; ++i)
                {
                    True("Nameplate.rankIcons[" + i + "]割当", nameplates[0].rankIcons[i] != null);
                }
            }
        }

        if (managers.Length != 1) { return; }
        var mm = managers[0];
        int tableCount = mm.tables == null ? 0 : mm.tables.Length;
        if (tableCount != 8)
        {
            Warn("現在シーンは" + tableCount + "卓（仕様の8卓に未達）。存在する卓のみ配線検証した");
        }

        for (int i = 0; i < tableCount; ++i)
        {
            var table = mm.tables[i];
            True("table[" + i + "]存在", table != null);
            if (table == null) { continue; }
            True("table[" + i + "].statsStore", table.statsStore == store);
            True("table[" + i + "].matchmaking", table.matchmaking == mm);

            // 自画像キャラの写真を卓の表示にも出すための配線。
            // 抜けていると「自分にだけ写真が見えて他人には見えない」状態になる。
            if (table.agariScreen != null)
            {
                True("table[" + i + "].agariScreen.selfieStore", table.agariScreen.selfieStore != null);
            }
            if (table.presentationScreen != null)
            {
                True("table[" + i + "].presentationScreen.selfieStore", table.presentationScreen.selfieStore != null);
            }
            if (table.resultScreen != null)
            {
                True("table[" + i + "].resultScreen.selfieStore", table.resultScreen.selfieStore != null);
            }
            if (table.yakumanCutin != null)
            {
                True("table[" + i + "].yakumanCutin.selfieStore", table.yakumanCutin.selfieStore != null);
            }
            int fxMissing = 0;
            foreach (var fx in table.GetComponentsInChildren<MahjongCallEffect>(true))
            {
                if (fx != null && fx.selfieStore == null) { ++fxMissing; }
            }
            Eq("table[" + i + "].callEffect.selfieStore未割当数", fxMissing, 0);
        }

        var maxTablesField = typeof(MatchmakingManager).GetField("MAX_TABLES",
            BindingFlags.Static | BindingFlags.NonPublic);
        Eq("rank mask実装上限", maxTablesField == null ? -1 : (int)maxTablesField.GetRawConstantValue(), 8);
        ValidateRankMaskIsolation(mm, tableCount);
        False("未登録卓は段位戦ではない", mm.IsRankedGameTable(null));

        for (int i = 0; i < tableCount; ++i)
        {
            if (mm.tables[i] != null)
            {
                ValidateTieRankOrder(mm.tables[i]);
                break;
            }
        }
    }

    private static void ValidateRankAdminMonitor(PlayerStatsStore store)
    {
        var monitors = UnityEngine.Object.FindObjectsOfType<RankAdminMonitor>(true);
        Eq("RankAdminMonitor数", monitors.Length, 1);
        if (monitors.Length != 1) { return; }

        var monitor = monitors[0];
        True("RankAdminMonitor.statsStore", monitor.statsStore == store);
        True("RankAdminMonitor.adminPanel", monitor.adminPanel != null);
        True("RankAdminMonitor.confirmationPanel", monitor.confirmationPanel != null);
        True("RankAdminMonitor.targetText", monitor.targetText != null);
        True("RankAdminMonitor.currentRankText", monitor.currentRankText != null);
        True("RankAdminMonitor.draftRankText", monitor.draftRankText != null);
        True("RankAdminMonitor.rankPointInput", monitor.rankPointInput != null);
        True("RankAdminMonitor.tenjuOrbX10Input", monitor.tenjuOrbX10Input != null);
        True("RankAdminMonitor.adminStatusText", monitor.adminStatusText != null);
        True("RankAdminMonitor.confirmationText", monitor.confirmationText != null);
        True("RankAdminMonitor.confirmationStatusText", monitor.confirmationStatusText != null);

        // 実機はUiShapeだけでも押せるが、ClientSimはコライダーを要求する。MainUIと同じ組に揃える。
        AssertUiShapeCanvas("RankAdminMonitor", monitor.gameObject);

        // ConfirmationPanelは別Canvas（RankConfirmMonitor）へ分離済み。
        // 同じCanvasの子のまま位置を動かすと平面から外れ、描画だけ残って触れなくなる。
        var confirmRoot = monitor.confirmationPanel;
        True("confirmationPanelが別Canvasルート（RankConfirmMonitor）",
             confirmRoot != null && confirmRoot.name == "RankConfirmMonitor");
        if (confirmRoot != null)
        {
            True("RankConfirmMonitorがRankAdminMonitorの子でない",
                 !confirmRoot.transform.IsChildOf(monitor.transform));
            AssertUiShapeCanvas("RankConfirmMonitor", confirmRoot);
            True("RankAdminMonitor直下に旧ConfirmationPanelが残っていない",
                 monitor.transform.Find("ConfirmationPanel") == null);
        }

        // パネルは自分のCanvas平面上にいないとレーザーが当たらない（実際に踏んだ）。
        var panels = new GameObject[]
        {
            monitor.adminPanel,
            confirmRoot != null && confirmRoot.transform.Find("ConfirmationPanel") != null
                ? confirmRoot.transform.Find("ConfirmationPanel").gameObject : null,
        };
        foreach (var panel in panels)
        {
            if (panel == null) { continue; }
            var prt = (RectTransform)panel.transform;
            True(panel.name + "がCanvas平面上（localPosition.z=0）", Mathf.Abs(prt.localPosition.z) < 0.01f);
            True(panel.name + "が未回転（localRotation=identity）",
                 Quaternion.Angle(prt.localRotation, Quaternion.identity) < 0.1f);
            True(panel.name + "のlocalScaleが1", (prt.localScale - Vector3.one).sqrMagnitude < 0.0001f);
        }
        var backingUdon = monitor.GetComponent<VRC.Udon.UdonBehaviour>();
        True("RankAdminMonitorの実UdonBehaviourはManual同期",
             backingUdon != null && backingUdon.SyncMethod == VRC.SDKBase.Networking.SyncType.Manual);
        Eq("RankAdminMonitorオブジェクト名", monitor.gameObject.name, "RankAdminMonitor");

        True("権限: <STAFF_NAME_1>", monitor.IsAuthorizedDisplayName("<STAFF_NAME_1>"));
        True("権限: <STAFF_NAME_2>", monitor.IsAuthorizedDisplayName("<STAFF_NAME_2>"));
        False("権限外表示名を拒否", monitor.IsAuthorizedDisplayName("一般プレイヤー"));
        False("前後空白を完全一致で拒否", monitor.IsAuthorizedDisplayName(" <STAFF_NAME_1>"));
        False("似たスタッフ名を拒否", monitor.IsAuthorizedDisplayName("ヴォルl1"));

        string storePath = Path.Combine(Application.dataPath, "Mahjong/Sources/PlayerStatsStore.cs");
        string storeSource = File.Exists(storePath) ? File.ReadAllText(storePath) : "";
        True("運営補正の一括保存API", storeSource.Contains("ApplyAdminRankCorrection"));
        True("運営補正revision監査キー", storeSource.Contains("kmj_rank_admin_rev"));
        True("運営補正operator監査キー", storeSource.Contains("kmj_rank_admin_by"));

        string monitorPath = Path.Combine(Application.dataPath, "Mahjong/Sources/RankAdminMonitor.cs");
        string monitorSource = File.Exists(monitorPath) ? File.ReadAllText(monitorPath) : "";
        True("対象本人の確認UIを実装", monitorSource.Contains("AcceptCorrection") && monitorSource.Contains("RejectCorrection"));
        True("補正依頼はManual同期", monitorSource.Contains("BehaviourSyncMode.Manual"));
        True("補正対象のplayerIdと表示名を照合",
             monitorSource.Contains("requestTargetPlayerId_") && monitorSource.Contains("requestTargetName_"));
        True("依頼Ownerを権限者として再検証",
             monitorSource.Contains("IsRequestSenderTrusted") && monitorSource.Contains("IsPendingRequestStillCurrent"));
    }

    // World Space CanvasのUIを実機で押せる最低限の組を検査する。
    private static void AssertUiShapeCanvas(string label, GameObject go)
    {
        var canvas = go.GetComponent<Canvas>();
        True(label + "はWorld Space Canvas", canvas != null && canvas.renderMode == RenderMode.WorldSpace);
        True(label + "にGraphicRaycaster", go.GetComponent<UnityEngine.UI.GraphicRaycaster>() != null);
        True(label + "にVRCUiShape", go.GetComponent<VRC.SDK3.Components.VRCUiShape>() != null);
        var shapeCollider = go.GetComponent<BoxCollider>();
        True(label + "にBoxCollider", shapeCollider != null);
        if (shapeCollider != null)
        {
            var rect = ((RectTransform)go.transform).rect;
            True(label + "のBoxColliderがCanvasサイズと一致",
                 Mathf.Abs(shapeCollider.size.x - rect.width) < 1f &&
                 Mathf.Abs(shapeCollider.size.y - rect.height) < 1f);
        }
    }

    private static void ValidateRankMaskIsolation(MatchmakingManager mm, int tableCount)
    {
        var rankedMaskField = typeof(MatchmakingManager).GetField("rankedTableMask",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var freeMaskField = typeof(MatchmakingManager).GetField("freeTableMask",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var invalidMaskField = typeof(MatchmakingManager).GetField("invalidRankedTableMask",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var countField = typeof(MatchmakingManager).GetField("numTables_",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (rankedMaskField == null || freeMaskField == null || invalidMaskField == null ||
            countField == null || mm.tables == null)
        {
            Fail("卓分類mask reflection", 0, 1);
            return;
        }

        int oldRankedMask = (int)rankedMaskField.GetValue(mm);
        int oldFreeMask = (int)freeMaskField.GetValue(mm);
        int oldInvalidMask = (int)invalidMaskField.GetValue(mm);
        int oldCount = (int)countField.GetValue(mm);
        try
        {
            int count = tableCount > 8 ? 8 : tableCount;
            countField.SetValue(mm, count);
            for (int bit = 0; bit < count; ++bit)
            {
                rankedMaskField.SetValue(mm, 1 << bit);
                freeMaskField.SetValue(mm, 0);
                invalidMaskField.SetValue(mm, 0);
                for (int table = 0; table < count; ++table)
                {
                    bool expected = table == bit;
                    bool actual = mm.IsRankedGameTable(mm.tables[table]);
                    if (actual == expected) { ++passed_; }
                    else { Fail("段位戦bit混線 bit=" + bit + " table=" + table, actual ? 1 : 0, expected ? 1 : 0); }
                    False("段位戦bitでfreeにならない bit=" + bit + " table=" + table,
                          mm.IsFreeGameTable(mm.tables[table]));
                    False("段位戦bitでinvalidにならない bit=" + bit + " table=" + table,
                          mm.IsRankedGameInvalidatedTable(mm.tables[table]));
                }

                rankedMaskField.SetValue(mm, 0);
                freeMaskField.SetValue(mm, 1 << bit);
                invalidMaskField.SetValue(mm, 0);
                for (int table = 0; table < count; ++table)
                {
                    bool expected = table == bit;
                    bool actual = mm.IsFreeGameTable(mm.tables[table]);
                    if (actual == expected) { ++passed_; }
                    else { Fail("フリーbit混線 bit=" + bit + " table=" + table, actual ? 1 : 0, expected ? 1 : 0); }
                    False("フリーbitでrankedにならない bit=" + bit + " table=" + table,
                          mm.IsRankedGameTable(mm.tables[table]));
                }

                rankedMaskField.SetValue(mm, 0);
                freeMaskField.SetValue(mm, 0);
                invalidMaskField.SetValue(mm, 1 << bit);
                for (int table = 0; table < count; ++table)
                {
                    bool expected = table == bit;
                    bool actual = mm.IsRankedGameInvalidatedTable(mm.tables[table]);
                    if (actual == expected) { ++passed_; }
                    else { Fail("段位無効bit混線 bit=" + bit + " table=" + table, actual ? 1 : 0, expected ? 1 : 0); }
                    False("無効bitだけでrankedにならない bit=" + bit + " table=" + table,
                          mm.IsRankedGameTable(mm.tables[table]));
                }
            }
        }
        finally
        {
            rankedMaskField.SetValue(mm, oldRankedMask);
            freeMaskField.SetValue(mm, oldFreeMask);
            invalidMaskField.SetValue(mm, oldInvalidMask);
            countField.SetValue(mm, oldCount);
        }
    }

    private static void ValidateTieRankOrder(Mahjong table)
    {
        int[] oldPoints = table.points;
        int oldNumPlayers = table.numPlayers;
        int oldFirstLeader = table.firstLeader;
        try
        {
            table.points = new[] { 25000, 25000, 25000, 25000 };
            table.numPlayers = 4;
            table.firstLeader = 2;
            Eq("同点順位 起家seat2", table.Hanchan_GetRank(2), 1);
            Eq("同点順位 起家次seat3", table.Hanchan_GetRank(3), 2);
            Eq("同点順位 起家次seat0", table.Hanchan_GetRank(0), 3);
            Eq("同点順位 起家次seat1", table.Hanchan_GetRank(1), 4);
        }
        finally
        {
            table.points = oldPoints;
            table.numPlayers = oldNumPlayers;
            table.firstLeader = oldFirstLeader;
        }
    }

    private static void ValidateSourceVersion()
    {
        string path = Path.Combine(Application.dataPath, "Mahjong/Sources/Mahjong.cs");
        string source = File.Exists(path) ? File.ReadAllText(path) : "";
        // 段位の値は卓の同期ペイロードへ載せない（PlayerData のみ）。version はその番人。
        //
        // これは「わざと落ちる」検査。payload version が動いたら必ずここが赤くなるので、
        // **増えた分が段位と無関係だと確認してから**番号を上げること（黙って上げると番人の意味が消える）。
        // 確認済みの内訳：
        //   16 … 終局結果の画面送り（resultStep_）
        //   17 … ローカル役の状態（カン振り／連荘／連開包）
        //   18 … オープン立直（openReach マスク＋pending）
        //   19 … 立直BGM（latestRiichiBgm_ ＝ 席+1 と本人が選んだトラック番号の1バイト）
        //   20 … 勝者カットインに載せる和了者の席ビット（ronManganMask_）
        //   21 … ロン確認中の鳴き予約（予約席+1・鳴きの種類・見送り席ビット）
        //   22 … 局の通し番号（handSeq_。受け手が新しい局を検知して自動化トグルを落とす）
        // いずれも段位の値は載っていない（曲の選択は PlayerData 側で、ここに載るのは番号だけ）。
        True("Mahjong Serialize versionを把握（現行22）", source.Contains("int version = 22;"));
        False("Mahjong Serializeへrank keyを追加していない",
              source.Contains("KEY_TENJU_ORB_X10") || source.Contains("KEY_RANK_SCHEMA"));
        True("ENTRYで段位開始値をラッチ", source.Contains("LatchRankedHanchanStart();"));
        True("RESULTから段位結果APIへ接続", source.Contains("statsStore.ApplyRankedGameResult("));
        True("東風判定はENTRYでラッチ", source.Contains("statsRankedTonpu_ ="));
        True("東風判定を段位結果へ渡す",
             source.Contains("ApplyRankedGameResult(numPlayers == 3, statsRankedTonpu_"));
        True("RESULTから段位戦後片付けへ接続", source.Contains("matchmaking.NotifyRankedGameFinished(this)"));
        True("forced wallは段位対象外", source.Contains("matchmaking.NotifyRankedGameInvalidated(this)"));
        True("離脱席CPU代打を実装", source.Contains("ActivateRankedCpuTakeoverForSeat"));
        True("CPU代打は5秒待機", source.Contains("RANKED_TAKEOVER_ACTION_DELAY_MS = 5000"));
        True("ENTRYは卓Owner宛てNetworkCallable",
              source.Contains("nameof(OnHumanEntryRequest)")
              && source.Contains("public void OnHumanEntryRequest("));
        True("CPU ENTRYは卓Owner宛てNetworkCallable",
              source.Contains("nameof(OnCpuEntryRequest)")
              && source.Contains("public void OnCpuEntryRequest("));
        True("STARTは卓Owner宛てNetworkCallable",
              source.Contains("nameof(OnStartRequest)")
              && source.Contains("public void OnStartRequest("));
        True("卓要求はCallingPlayerで送信者を検証",
              source.Contains("NetworkCalling.CallingPlayer")
              && source.Contains("ApplyHumanEntryRequestAuthoritative")
              && source.Contains("ApplyStartRequestAuthoritative"));
        True("固定卓OwnerのENTRY音声はseatPlayerIdからパック解決",
              source.Contains("soundsV2.PlayYoroshikuForSeat(seat)"));
        True("RESYNCも卓Owner要求制",
              source.Contains("nameof(OnResyncRequest)")
              && source.Contains("ApplyResyncAuthoritative"));
        True("Owner移譲後に開始前の不在席を再検証",
              source.Contains("_CheckPregameEntriesAfterOwnership"));

        string matchmakingPath = Path.Combine(Application.dataPath, "Mahjong/Sources/MatchmakingManager.cs");
        string matchmakingSource = File.Exists(matchmakingPath) ? File.ReadAllText(matchmakingPath) : "";

        True("Managerに同期matchId", matchmakingSource.Contains("[UdonSynced] private int[] matchId"));
        True("Managerに同期stateRevision", matchmakingSource.Contains("[UdonSynced] private int stateRevision"));
        True("Managerに送信者別requestSeq", matchmakingSource.Contains("managerRequestSeqs")
             && matchmakingSource.Contains("AcceptManagerRequestSequence"));
        True("段位戦成立はmatchId発行後に一括commit",
             matchmakingSource.Contains("IssueMatchId(tableIdx)")
             && matchmakingSource.Contains("ValidateNewMatchMembers")
             && matchmakingSource.Contains("CommitManagerState();"));
        True("Manager要求は中央Owner宛てNetworkCallable",
             matchmakingSource.Contains("NetApplyStatusRequest")
             && matchmakingSource.Contains("NetApplySeatRequest")
             && matchmakingSource.Contains("NetApplyFreeActionRequest"));
        False("Manager同期状態の書込でOwnerを奪わない",
              matchmakingSource.Contains("Networking.SetOwner"));

        int firstManagerSerialize = matchmakingSource.IndexOf("RequestSerialization();");
        int lastManagerSerialize = matchmakingSource.LastIndexOf("RequestSerialization();");
        True("ManagerのRequestSerializationはCommitManagerStateだけ",
             firstManagerSerialize >= 0 && firstManagerSerialize == lastManagerSerialize);

        int entryButtonsStart = source.IndexOf("public void _OnEntryButton0");
        int entryRequestStart = entryButtonsStart < 0 ? -1
            : source.IndexOf("private void OnEntry(int seat)", entryButtonsStart);
        string entryButtons = entryButtonsStart >= 0 && entryRequestStart > entryButtonsStart
            ? source.Substring(entryButtonsStart, entryRequestStart - entryButtonsStart) : "";
        False("人間ENTRYボタンは卓Ownerを奪わない", entryButtons.Contains("TakeOwnership"));

        int cpuButtonsStart = source.IndexOf("public void _OnEntryCpuButton0");
        int cpuRequestStart = cpuButtonsStart < 0 ? -1
            : source.IndexOf("private void OnEntryCpu(int seat)", cpuButtonsStart);
        string cpuButtons = cpuButtonsStart >= 0 && cpuRequestStart > cpuButtonsStart
            ? source.Substring(cpuButtonsStart, cpuRequestStart - cpuButtonsStart) : "";
        False("CPU ENTRYボタンは卓Ownerを奪わない", cpuButtons.Contains("TakeOwnership"));

        // 引数付き SendCustomNetworkEvent の受け側は [NetworkCallable] が無いと実行されない。
        // 属性が消えても C# としては合法＝コンパイルも validate_script も通ってしまい、
        // 実機で「要求を送っているのに永久に届かない」形でしか表面化しない。
        // 実際に NetApplyTablePrepared / NetApplyAssignedTableUnusable で踏んだので機械で見張る。
        string[] tableCallables = new string[] {
            "OnStartRequest", "OnHumanEntryRequest", "OnCpuEntryRequest",
            "OnEntryAccepted", "OnResyncRequest",
            "OnPickTileRequest", "OnNoButtonRequest", "OnTurnTimerSeen",
        };
        foreach (string callable in tableCallables)
        {
            True("Mahjong." + callable + " に [NetworkCallable]",
                 HasNetworkCallable(source, callable));
        }

        string[] managerCallables = new string[] {
            "NetApplyStatusRequest", "NetApplySeatRequest", "NetApplyTableBusyReport",
            "NetApplyAssignedTableUnusable", "NetApplyTablePrepared",
            "NetApplyTableLifecycleRequest", "NetApplyFreeActionRequest",
        };
        foreach (string callable in managerCallables)
        {
            True("Matchmaking." + callable + " に [NetworkCallable]",
                 HasNetworkCallable(matchmakingSource, callable));
        }

        True("卓Ownerが空卓復帰を申告できる",
             matchmakingSource.Contains("public void ReportTableIdle(Mahjong tb)")
             && matchmakingSource.Contains("TABLE_ACTION_TABLE_IDLE")
             && source.Contains("AdvanceTableIdleReport"));
        True("使用中判定に開始前ENTRYを含む",
             source.Contains("gameState != GAMESTATE_INITIAL || HasAnyActiveSeatEntry()"));
        True("要求シーケンススロットを掃除する",
             matchmakingSource.Contains("private bool SweepManagerRequests()"));

        // 卓の着席者は同期側で持つ。seatPlayerId は [NonSerialized] ＝ 寝ている卓では
        // null か古い値なので、メインメニュー側にいる Manager Owner からは読めない。
        True("卓の着席者IDを同期で持つ",
             matchmakingSource.Contains("[UdonSynced] private int[] tableSeatIds"));
        True("対局中判定は同期済み着席者IDを見る",
             matchmakingSource.Contains("private int SeatStateOf(int id)")
             && matchmakingSource.Contains("if (tableSeatIds[t * 4 + s] == id) { seated = true; break; }"));
        True("卓Ownerが席の主をbusy報告に載せる",
             matchmakingSource.Contains("public void ReportTableBusy(Mahjong tb, bool busy, bool finished, bool started, int seat0")
             && source.Contains("private bool RefreshSeatReport()")
             && source.Contains("private int SeatReportIdOf(int seat)"));

        // 弾かれる要求で画面だけ進めない（待機画面の空振り／「満員です」の誤表示を防ぐ）。
        string mainMenuPath = Path.Combine(Application.dataPath, "Mahjong/Sources/MainMenuController.cs");
        string mainMenuSource = File.Exists(mainMenuPath) ? File.ReadAllText(mainMenuPath) : "";
        True("着席中のマッチング要求はメニューで理由を出す",
             matchmakingSource.Contains("public bool IsLocalPlayerSeatedInOngoingGame()")
             && mainMenuSource.Contains("private bool BlockedBySeatedGame()"));
        True("段位戦・フリー両方の入口で着席中を弾く",
             mainMenuSource.Split(new string[] { "if (BlockedBySeatedGame()) { return; }" },
                                  System.StringSplitOptions.None).Length - 1 == 3);


        True("卓busy報告はOwner切替競合に最大3回再送",
             source.Contains("tableBusyReportRetryLeft_ = 3;"));
        True("卓busy報告は中央Owner宛てNetworkCallable",
             matchmakingSource.Contains("nameof(NetApplyTableBusyReport)"));
        True("卓busy報告は送信者を卓Owner検証",
             matchmakingSource.Contains("NetworkCalling.CallingPlayer")
             && matchmakingSource.Contains("tableOwner.playerId != caller.playerId"));
        True("卓準備完了を同期bitで管理",
             matchmakingSource.Contains("[UdonSynced] private int matchPreparedMask = 0;"));
        True("卓準備完了は中央Owner宛て申告",
             matchmakingSource.Contains("nameof(NetApplyTablePrepared)"));
        True("卓準備未確認に安全タイムアウト",
             matchmakingSource.Contains("PREPARE_CONFIRM_TIMEOUT_MS = 10000"));
        False("非同期ローカル準備ラッチを廃止",
              matchmakingSource.Contains("prepVerifiedMask_"));

        int busyStart = matchmakingSource.IndexOf("public void ReportTableBusy");
        int busyEnd = busyStart < 0 ? -1
            : matchmakingSource.IndexOf("[NetworkCallable]", busyStart);
        string busyMethod = busyStart >= 0 && busyEnd > busyStart
            ? matchmakingSource.Substring(busyStart, busyEnd - busyStart) : "";
        False("ReportTableBusyはManager Ownerを奪わない",
              busyMethod.Contains("Networking.SetOwner"));

        int unusableStart = matchmakingSource.IndexOf("public void ReportAssignedTableUnusable");
        int unusableEnd = unusableStart < 0 ? -1
            : matchmakingSource.IndexOf("[NetworkCallable]", unusableStart);
        string unusableMethod = unusableStart >= 0 && unusableEnd > unusableStart
            ? matchmakingSource.Substring(unusableStart, unusableEnd - unusableStart) : "";
        False("ReportAssignedTableUnusableはManager Ownerを奪わない",
              unusableMethod.Contains("Networking.SetOwner"));

        string cullerPath = Path.Combine(Application.dataPath, "Mahjong/Sources/TableProximityCuller.cs");
        string cullerSource = File.Exists(cullerPath) ? File.ReadAllText(cullerPath) : "";
        True("割当本人が非表示卓を準備中に起こす",
             cullerSource.Contains("IsLocalPlayerAssignedToTable(table_)"));
        True("空卓Ownerは遠距離でカリング可能",
             cullerSource.Contains("matchmaking_.IsTableInUse(table_)")
             && cullerSource.Contains("bool ownerHold"));
        True("Matchmakingに固定ENTRY検証API", matchmakingSource.Contains("CanLocalPlayerEnterTableSeat"));
    }

    private static void ValidateTakeoverClassification()
    {
        var go = new GameObject("__RankTakeoverValidationTable");
        try
        {
            var table = go.AddComponent<Mahjong>();
            table.numPlayers = 4;
            table.seatPlayerId = new[] { 0, 123, 0, 0 };

            var cpuField = typeof(Mahjong).GetField("entryCpu",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var method = typeof(Mahjong).GetMethod("IsRankedTakeoverCpuSeat",
                BindingFlags.Instance | BindingFlags.NonPublic);
            True("CPU代打classification reflection", cpuField != null && method != null);
            if (cpuField == null || method == null) { return; }

            cpuField.SetValue(table, new[] { true, true, false, false });
            False("通常CPUは代打扱いしない", (bool)method.Invoke(table, new object[] { 0 }));
            True("元playerId保持CPUは代打扱い", (bool)method.Invoke(table, new object[] { 1 }));
            False("人間席は代打扱いしない", (bool)method.Invoke(table, new object[] { 2 }));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    private static void Result(string name, int[] result, int lv, int pt, int orb, int delta)
    {
        if (result == null || result.Length < PlayerStatsStore.RANK_RESULT_SIZE)
        {
            Fail(name + " result length", result == null ? -1 : result.Length, PlayerStatsStore.RANK_RESULT_SIZE);
            return;
        }
        Eq(name + " lv", result[PlayerStatsStore.RANK_RESULT_LV], lv);
        Eq(name + " pt", result[PlayerStatsStore.RANK_RESULT_PT], pt);
        Eq(name + " orb", result[PlayerStatsStore.RANK_RESULT_ORB_X10], orb);
        Eq(name + " delta", result[PlayerStatsStore.RANK_RESULT_DELTA], delta);
    }

    private static void Eq(string name, int actual, int expected)
    {
        if (actual == expected) { ++passed_; }
        else { Fail(name, actual, expected); }
    }

    private static void Eq(string name, string actual, string expected)
    {
        if (actual == expected) { ++passed_; }
        else
        {
            ++errors_;
            Debug.LogError("[ValidateRankSystem] FAIL " + name + " actual='" + actual + "' expected='" + expected + "'");
        }
    }

    // public void <name>( の直前が [NetworkCallable] で終わっているか。
    // 宣言の手前の非空白だけを見るので、インデント崩れには反応しない。
    private static bool HasNetworkCallable(string src, string methodName)
    {
        int at = src.IndexOf("public void " + methodName + "(");
        if (at < 0) { return false; }
        return src.Substring(0, at).TrimEnd().EndsWith("[NetworkCallable]");
    }

    private static void True(string name, bool value)
    {
        if (value) { ++passed_; }
        else { Fail(name, 0, 1); }
    }

    private static void False(string name, bool value)
    {
        if (!value) { ++passed_; }
        else { Fail(name, 1, 0); }
    }

    private static void Fail(string name, int actual, int expected)
    {
        ++errors_;
        Debug.LogError("[ValidateRankSystem] FAIL " + name + " actual=" + actual + " expected=" + expected);
    }

    private static void Warn(string message)
    {
        ++warnings_;
        Debug.LogWarning("[ValidateRankSystem] WARN " + message);
    }

    private static void LogSummary()
    {
        string summary = "[ValidateRankSystem] errors=" + errors_ + " warnings=" + warnings_ + " passed=" + passed_;
        if (errors_ == 0) { Debug.Log(summary); }
        else { Debug.LogError(summary); }
    }
}
#endif
