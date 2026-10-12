using UdonSharp;
using UnityEngine;
using VRC.SDK3.Persistence;
using VRC.SDKBase;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    // プレイヤー戦績・設定の PlayerData 読み書きラッパ
    //
    // ■ 役割
    //   選択キャラ・段位・四麻/三麻の対局実績・直近10戦の順位を
    //   VRChat PlayerData（クロスインスタンス永続）に保存する。
    //   - 書き込みは本人のクライアントからのみ（PlayerDataの仕様）。
    //   - 読み込みは誰のクライアントからでも可（プロフィール閲覧用に
    //     VRCPlayerApi 指定版の Get を用意）。
    //   - 同期変数・RequestSerialization は一切使わない（完全ローカル）。
    //   - ワールドに1個置けば全卓・全UIから共用できる。
    //
    // ■ 参考実装
    //   MahjongVoiceSelector / MahjongSoundsV2 の VOICE_PACK_KEY パターン。
    //   復元前の書き込みは消える恐れがあるため OnPlayerRestored でガードする。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class PlayerStatsStore : UdonSharpBehaviour
    {
        [Header("表示言語（Wire Language で自動割り当て）")]
        [Tooltip("段位名の言語。未割当なら日本語のまま。")]
        public KemonoLanguage languageManager;
        // 0=日本語 / 1=英語 / 2=韓国語 / 3=簡体字 / 4=繁体字。-1＝未解決（初回参照時に languageManager から取りに行く）。
        // Start の実行順は保証されないので、push を待たずに遅延で解決する。
        private int lang_ = -1;
        
        private int Lang()
        {
            if (lang_ < 0) { lang_ = (languageManager != null) ? languageManager.GetLanguage() : KemonoLanguage.LANG_JA; }
            return lang_;
        }
        
        private bool En() { return Lang() == KemonoLanguage.LANG_EN; }
        private bool Ko() { return Lang() == KemonoLanguage.LANG_KO; }
        private bool Zhs() { return Lang() == KemonoLanguage.LANG_ZHS; }
        private bool Zht() { return Lang() == KemonoLanguage.LANG_ZHT; }
        
        // KemonoLanguage から SendCustomEvent で呼ばれる配布口。
        public void ApplyLanguageJa() { SetLang(KemonoLanguage.LANG_JA); }
        public void ApplyLanguageEn() { SetLang(KemonoLanguage.LANG_EN); }
        public void ApplyLanguageKo() { SetLang(KemonoLanguage.LANG_KO); }
        public void ApplyLanguageZhs() { SetLang(KemonoLanguage.LANG_ZHS); }
        public void ApplyLanguageZht() { SetLang(KemonoLanguage.LANG_ZHT); }
        
        private void SetLang(int lang)
        {
            if (lang_ == lang) { return; }
            lang_ = lang;
        }
        
        // 段位名の英訳。獣のはしごを保ったまま段位らしく登っていく並びにしてある。
        // 変えたくなったらここ1か所だけを直せば、メニュー・ネームプレート・結果画面・
        // ランキングの全部に効く（段位名の生成はこのクラスに集約してある）。
        private const string RANK_EN_0 = "Cub";             // 初獣
        private const string RANK_EN_1 = "Keen Claw";       // 鋭爪
        private const string RANK_EN_2 = "Iron Fang";       // 剛牙
        private const string RANK_EN_3 = "Dire Beast";      // 獣威
        private const string RANK_EN_4 = "Phantom Beast";   // 幻獣
        private const string RANK_EN_5 = "Celestial Beast"; // 天獣
        private const string TENJU_EN = "Celestial Orb";    // 天珠

        // 段位名の韓国語。雀魂の韓国語版が段位を漢字語（작사・작걸…）で読むのに合わせて、
        // 日本語の漢字をそのまま韓国語の音で読む。
        private const string RANK_KO_0 = "초수";   // 初獣
        private const string RANK_KO_1 = "예조";   // 鋭爪
        private const string RANK_KO_2 = "강아";   // 剛牙
        private const string RANK_KO_3 = "수위";   // 獣威
        private const string RANK_KO_4 = "환수";   // 幻獣
        private const string RANK_KO_5 = "천수";   // 天獣
        private const string TENJU_KO = "천주";    // 天珠

        // 段位名の中国語。漢字はそのまま、字形だけ簡体字／繁体字に合わせる。
        private const string RANK_ZHS_0 = "初兽";
        private const string RANK_ZHS_1 = "锐爪";
        private const string RANK_ZHS_2 = "刚牙";
        private const string RANK_ZHS_3 = "兽威";
        private const string RANK_ZHS_4 = "幻兽";
        private const string RANK_ZHS_5 = "天兽";
        private const string RANK_ZHT_0 = "初獸";
        private const string RANK_ZHT_1 = "銳爪";
        private const string RANK_ZHT_2 = "剛牙";
        private const string RANK_ZHT_3 = "獸威";
        private const string RANK_ZHT_4 = "幻獸";
        private const string RANK_ZHT_5 = "天獸";

        // --- PlayerData キー ---
        public const string KEY_CHARA = "kmj_chara";         // int 選択キャラ（未設定=0 Vol）
        public const string KEY_CHARA_SCHEMA = "kmj_chara_schema"; // int キャラID体系の版（旧ID 2との衝突回避）
        public const string KEY_ABILITY_ON = "kmj_ability_on"; // int 能力スイッチ 0=OFF/1=ON（未設定=0）
        public const string KEY_AGARI_FX = "kmj_agari_fx";   // int 和了演出コスメID（未設定=0 既定演出）
        public const string KEY_RANK_PT = "kmj_rank_pt";     // int 段位ポイント（未設定=0）
        public const string KEY_RANK_LV = "kmj_rank_lv";     // int 段位レベル（未設定=0）
        public const string KEY_TENJU_ORB_X10 = "kmj_tenju_orb_x10"; // int 天珠（10倍整数。10.0=100）
        public const string KEY_RANK_SCHEMA = "kmj_rank_schema";     // int 段位保存形式の版
        public const string KEY_RANK_ADMIN_REV = "kmj_rank_admin_rev"; // int 運営補正の通し番号
        public const string KEY_RANK_ADMIN_BY = "kmj_rank_admin_by";   // string 最後に補正を依頼した運営表示名
        public const string KEY_ST4_PLAY = "kmj_st4_play";   // int 四麻段位戦 対局数
        public const string KEY_ST4_WIN = "kmj_st4_win";     // int 四麻段位戦 1位数
        public const string KEY_ST4_AGARI = "kmj_st4_agari"; // int 四麻段位戦 和了数
        public const string KEY_ST4_HOJU = "kmj_st4_hoju";   // int 四麻段位戦 放銃数
        public const string KEY_ST3_PLAY = "kmj_st3_play";   // int 三麻段位戦 対局数
        public const string KEY_ST3_WIN = "kmj_st3_win";     // int 三麻段位戦 1位数
        public const string KEY_ST3_AGARI = "kmj_st3_agari"; // int 三麻段位戦 和了数
        public const string KEY_ST3_HOJU = "kmj_st3_hoju";   // int 三麻段位戦 放銃数
        public const string KEY_RECENT = "kmj_recent";       // string 四麻段位戦 直近10戦の順位CSV（新しいものが末尾）
        public const string KEY_STF_PLAY = "kmj_stf_play";   // int フリー対局 対局数
        public const string KEY_STF_WIN = "kmj_stf_win";     // int フリー対局 1位数
        public const string KEY_STF_AGARI = "kmj_stf_agari"; // int フリー対局 和了数
        public const string KEY_STF_HOJU = "kmj_stf_hoju";   // int フリー対局 放銃数
        public const string KEY_RECENT3 = "kmj_recent3";     // string 三麻段位戦 直近10戦CSV
        public const string KEY_RECENTF = "kmj_recentf";     // string フリー対局 直近10戦CSV

        // 段位戦（＝段位が動いた対局）だけの通算対局数。2026-09-14 追加。
        // KEY_ST4_PLAY / KEY_ST3_PLAY は「フリー部屋でない対局」を全部数えるので、
        // 手動着席・CPU混じり・未登録卓・デバッグ局まで段位戦として積まれていた。
        // 段位ランキングの「通算」列はこちらを見る。加算は ApplyRankedGameResultNow の
        // 1か所だけ＝段位が動かない対局は構造的に入らない（新設なので0から数え直し）。
        public const string KEY_RANKED_PLAY = "kmj_rk_play"; // int 段位戦の通算対局数

        // 和了率・放銃率の分母は対局数ではなく総局数。
        // 旧データには総局数が無いため、既存の累計回数とは別に v2 期間の局数・和了数・放銃数を持つ。
        // これにより既存戦績を消さず、分子だけ旧データを含んで率が壊れることも防ぐ。
        public const string KEY_ST4_HAND_V2 = "kmj_st4_hand_v2";
        public const string KEY_ST4_AGARI_V2 = "kmj_st4_agari_v2";
        public const string KEY_ST4_HOJU_V2 = "kmj_st4_hoju_v2";
        public const string KEY_ST3_HAND_V2 = "kmj_st3_hand_v2";
        public const string KEY_ST3_AGARI_V2 = "kmj_st3_agari_v2";
        public const string KEY_ST3_HOJU_V2 = "kmj_st3_hoju_v2";
        public const string KEY_STF_HAND_V2 = "kmj_stf_hand_v2";
        public const string KEY_STF_AGARI_V2 = "kmj_stf_agari_v2";
        public const string KEY_STF_HOJU_V2 = "kmj_stf_hoju_v2";
        // 通算の着順内訳（2026-09-08 追加）。順位グラフは直近ログではなくここを見る。
        // 1位数は既存の KEY_*_WIN、分母は KEY_*_PLAY をそのまま使う（右上の統計ブロックと
        // 必ず同じ値になる）ので、2〜4位だけを新しく持つ。三麻の p4 は書かれない。
        public const string KEY_ST4_P2 = "kmj_st4_p2";
        public const string KEY_ST4_P3 = "kmj_st4_p3";
        public const string KEY_ST4_P4 = "kmj_st4_p4";
        public const string KEY_ST3_P2 = "kmj_st3_p2";
        public const string KEY_ST3_P3 = "kmj_st3_p3";
        public const string KEY_ST3_P4 = "kmj_st3_p4";
        public const string KEY_STF_P2 = "kmj_stf_p2";
        public const string KEY_STF_P3 = "kmj_stf_p3";
        public const string KEY_STF_P4 = "kmj_stf_p4";
        // 通算の和了種別（2026-09-08 追加）。wr=立直 / wo=副露 / wd=闇聴。
        // 和了グラフは直近100戦のCSVではなくここを見る（スタイルの指標だけはCSVのまま）。
        public const string KEY_ST4_WR = "kmj_st4_wr";
        public const string KEY_ST4_WO = "kmj_st4_wo";
        public const string KEY_ST4_WD = "kmj_st4_wd";
        public const string KEY_ST3_WR = "kmj_st3_wr";
        public const string KEY_ST3_WO = "kmj_st3_wo";
        public const string KEY_ST3_WD = "kmj_st3_wd";
        public const string KEY_STF_WR = "kmj_stf_wr";
        public const string KEY_STF_WO = "kmj_stf_wo";
        public const string KEY_STF_WD = "kmj_stf_wd";
        // --- デイリーランキング（インスタンス内・2026-09-03追加） ---
        // 「その日の段位戦スコア」だけを持つ。日付が変わったら加算時に0から数え直すので、
        // 前日の値が残っていても読み側で日付が合わなければ無視される（JST5時リセット）。
        public const string KEY_DAY_INDEX = "kmj_day_index"; // int 集計日（JSTのyyyyMMdd）
        public const string KEY_DAY_SCORE = "kmj_day_score"; // int その日の累計スコア
        public const string KEY_DAY_GAMES = "kmj_day_games"; // int その日の段位戦の対局数
        public const string KEY_DAY_TOP = "kmj_day_top";     // int その日の1位数
        // 「本日の最高和了」ランキング（2026-09-08追加）。その日に自分が和了った中で
        // いちばん打点の高い1回だけを持つ。翻・符・役満の重ねは表示用の飾りで、
        // 並べ替えの主キーはあくまで打点（KEY_DAY_BEST）。日付は KEY_DAY_INDEX を共用する。
        public const string KEY_DAY_BEST = "kmj_day_best";     // int その日の最高打点（受取点）
        public const string KEY_DAY_BEST_HAN = "kmj_day_bhan"; // int その和了の翻数
        public const string KEY_DAY_BEST_FU = "kmj_day_bfu";   // int その和了の符
        public const string KEY_DAY_BEST_YM = "kmj_day_bym";   // int 役満の重ね数（0=非役満）


        // --- 戦績カテゴリ（プロフィールの戦績タブと対応。2026-07-17追加） ---
        // フリー対局は専用カテゴリに記録する（段位・経験値には影響しない）。
        // KEY_RECENT（無印）は既存データ互換のため四麻段位戦用として継続使用。
        // 段位戦の東風戦（2026-08-18追加）は半荘と同じカテゴリへ合算する。
        // 段位そのものが半荘・東風で共通である以上、戦績も分けない（タブ肥大の回避）。
        public const int CAT_YONMA = 0; // 四麻段位戦
        public const int CAT_SANMA = 1; // 三麻段位戦
        public const int CAT_FREE = 2;  // フリー対局
        public const int NUM_CATS = 3;

        // --- キャラID ---
        // 旧4体キット（キツネ0/タヌキ1/オオカミ2/クマ3/能力なし4）は2026-07-15に廃止。
        // 現行は 0=Vol / 1=Shine / 2=Tsumuntema / 3=自画像。旧データのID 2・3と衝突するため、
        // KEY_CHARA_SCHEMA が現行版でないID 2以上はVolとして扱う。
        // 「能力なし」はキャラIDではなく KEY_ABILITY_ON（能力スイッチ）で表現する。
        //
        // CHARA_SELFIE（自画像）は他キャラと性質が違う：
        //   ・立ち絵はワールド内カメラで撮ったRenderTexture＝**撮った本人のクライアントにしか存在しない**。
        //     VRChatはテクスチャを同期できないので、他プレイヤーの画面（宣言カットイン等）では
        //     MahjongCallEffect.ResolvePortrait の既定立ち絵へ自動フォールバックする。
        //   ・PlayerDataに画像は保存できないので、写真はインスタンスを跨いで残らない（選択IDだけ残る）。
        //   ・専用ボイスは持たない（charaVoicePacks[3] = -1 ＝ 直前のパックを維持）。
        public const int CHARA_VOL = 0;   // デフォルト
        public const int CHARA_SHINE = 1;
        public const int CHARA_TSUMUNTEMA = 2;
        public const int CHARA_SELFIE = 3;
        public const int NUM_CHARAS = 4;
        private const int CHARA_SCHEMA_CURRENT = 1;

        // 自画像キャラの解禁段位（大段位index）。1＝鋭爪。
        public const int CHARA_SELFIE_UNLOCK_MAJOR_RANK = 1;

        // 立直BGM（装飾品）の解禁段位。Lv3＝鋭爪★1（大段位1の最初のLv）。
        // 大段位indexではなくLvで持つ＝★いくつで開けるかを後から動かせる。
        public const int RIICHI_BGM_UNLOCK_LV = 3;

        // --- 段位 ---
        public const int RANK_MIN_LV = 0;
        public const int RANK_MAX_LV = 34;
        public const int RANK_TENJU_FIRST_LV = 15;
        public const int RANK_TENJU_LAST_LV = 34;
        public const int RANK_SCHEMA_CURRENT = 1;
        public const int TENJU_ORB_INITIAL_X10 = 100;
        public const int TENJU_ORB_MAX_X10 = 200;

        // CalculateRankedHanchanResult の返却配列index。
        // 結果処理時だけ1回生成する配列で、Update/LateUpdateからは呼ばない。
        public const int RANK_RESULT_LV = 0;
        public const int RANK_RESULT_PT = 1;
        public const int RANK_RESULT_ORB_X10 = 2;
        public const int RANK_RESULT_DELTA = 3;
        public const int RANK_RESULT_SIZE = 4;

        private const int RECENT_MAX = 100;

        // ============================================================
        // デバッグ（テスト用。**リリース前に必ず OFF へ戻すこと**）
        //
        // 段位そのものは一切いじらない。「解禁されているか」の判定だけを
        // 素通しにする。ON のままビルドすると誰でも自画像を選べてしまうので、
        // Validate Rank System が ON を見つけたら警告を出す。
        // ============================================================
        [Header("デバッグ（テスト用・リリース前に必ずOFF）")]
        [Tooltip("ONにすると段位に関係なく自画像キャラを選べる。段位・戦績は一切変更しない。"
                 + "リリース前に必ずOFFへ戻すこと（Validate Rank System が警告を出す）。")]
        public bool debugUnlockSelfie = false;

        [Tooltip("ONにすると段位に関係なく立直BGMを装備できる。段位・戦績は一切変更しない。"
                 + "リリース前に必ずOFFへ戻すこと（Validate Rank System が警告を出す）。")]
        public bool debugUnlockRiichiBgm = false;

        // 自分の PlayerData が復元済みかどうか。復元前の書き込みは消える恐れがあるためガード。
        private bool restored_ = false;

        // 復元前に選ばれたキャラ／能力。OnPlayerRestored で PlayerData へ flush する。
        private bool pendingCharaSet_ = false;
        private int pendingChara_ = CHARA_VOL;
        private bool pendingAbilitySet_ = false;
        private bool pendingAbilityOn_ = false;
        private int pendingTileBackColor_ = 0;
        private bool pendingTileBackColorSet_ = false;
        private bool pendingTableTopColorSet_ = false;
        private int pendingTableTopColor_ = 0;
        public int GetTileBackColor()
        {
            return pendingTileBackColorSet_ ? pendingTileBackColor_ : GetIntLocal("kmj_tile_back_color", 0);
        }
        public int GetTableTopColor()
        {
            return pendingTableTopColorSet_ ? pendingTableTopColor_ : GetIntLocal("kmj_table_top_color", 0);
        }
        public void SetTileBackColor(int value)
        {
            if (value < 0 || value > 7) { return; }
            if (!restored_) { pendingTileBackColor_ = value; pendingTileBackColorSet_ = true; return; }
            PlayerData.SetInt("kmj_tile_back_color", value);
        }
        public void SetTableTopColor(int value)
        {
            if (value < 0 || value > 7) { return; }
            if (!restored_) { pendingTableTopColor_ = value; pendingTableTopColorSet_ = true; return; }
            PlayerData.SetInt("kmj_table_top_color", value);
        }

        private bool pendingAgariFxSet_ = false;
        private int pendingAgariFx_ = 0;

        // 立直後のツモ切り自動ON（純ローカル設定）。
        // 0=未設定 / 1=ON / 2=OFF。既定を 0 にしておくと、既存プレイヤーは
        // これまでどおり「立直したら自動でツモ切り」のまま開く。
        public const string KEY_REACH_TSUMOGIRI = "kmj_reach_tg";
        private bool pendingReachTsumogiriSet_ = false;
        private int pendingReachTsumogiri_ = 0;

        // 立直したときにツモ切りを自動でONにするか。未設定＝ON（従来どおり）。
        public bool GetReachAutoTsumogiri()
        {
            int v = pendingReachTsumogiriSet_
                ? pendingReachTsumogiri_
                : GetIntLocal(KEY_REACH_TSUMOGIRI, 0);
            return v != 2;
        }

        public void SetReachAutoTsumogiri(bool on)
        {
            int v = on ? 1 : 2;
            if (!restored_) { pendingReachTsumogiri_ = v; pendingReachTsumogiriSet_ = true; return; }
            PlayerData.SetInt(KEY_REACH_TSUMOGIRI, v);
        }

        // 押した直後の連打防止（卓のボタンの入力の保護・純ローカル設定）。値はミリ秒。
        // 0=未設定＝PRESS_GUARD_DEFAULT_MS。MahjongPushButton が押下のたびに PlayerData から直接読む。
        public const string KEY_PRESS_GUARD = "kmj_press_guard";
        public const int PRESS_GUARD_DEFAULT_MS = 200;
        private bool pendingPressGuardSet_ = false;
        private int pendingPressGuard_ = 0;

        public int GetPressGuardMs()
        {
            int v = pendingPressGuardSet_
                ? pendingPressGuard_
                : GetIntLocal(KEY_PRESS_GUARD, 0);
            return v > 0 ? v : PRESS_GUARD_DEFAULT_MS;
        }

        public void SetPressGuardMs(int ms)
        {
            if (ms <= 0) { ms = PRESS_GUARD_DEFAULT_MS; }
            if (!restored_) { pendingPressGuard_ = ms; pendingPressGuardSet_ = true; return; }
            PlayerData.SetInt(KEY_PRESS_GUARD, ms);
        }

        // 卓のボタンの押し方（純ローカル設定）。0=未設定＝レーザー / 1=レーザー / 2=タッチ（指で押し込む・VRのみ）。
        // 卓ごとの MahjongTouchInput が0.5秒ごとに PlayerData から直接読む。
        public const string KEY_TOUCH_INPUT = "kmj_touch";
        public const int TOUCH_INPUT_LASER = 1;
        public const int TOUCH_INPUT_TOUCH = 2;
        private bool pendingTouchInputSet_ = false;
        private int pendingTouchInput_ = 0;

        public bool GetTouchInput()
        {
            int v = pendingTouchInputSet_
                ? pendingTouchInput_
                : GetIntLocal(KEY_TOUCH_INPUT, 0);
            return v == TOUCH_INPUT_TOUCH;
        }

        public void SetTouchInput(bool touch)
        {
            int v = touch ? TOUCH_INPUT_TOUCH : TOUCH_INPUT_LASER;
            if (!restored_) { pendingTouchInput_ = v; pendingTouchInputSet_ = true; return; }
            PlayerData.SetInt(KEY_TOUCH_INPUT, v);
        }

        // 牌を掴んで打牌する（純ローカル設定・VRのみ）。0=未設定＝OFF / 1=OFF / 2=ON。
        // 卓ごとの MahjongTileGrab が0.5秒ごとに PlayerData から直接読む。
        public const string KEY_TILE_GRAB = "kmj_tile_grab";
        public const int TILE_GRAB_OFF = 1;
        public const int TILE_GRAB_ON = 2;
        private bool pendingTileGrabSet_ = false;
        private int pendingTileGrab_ = 0;

        public bool GetTileGrab()
        {
            int v = pendingTileGrabSet_
                ? pendingTileGrab_
                : GetIntLocal(KEY_TILE_GRAB, 0);
            return v == TILE_GRAB_ON;
        }

        public void SetTileGrab(bool on)
        {
            int v = on ? TILE_GRAB_ON : TILE_GRAB_OFF;
            if (!restored_) { pendingTileGrab_ = v; pendingTileGrabSet_ = true; return; }
            PlayerData.SetInt(KEY_TILE_GRAB, v);
        }


        // ============================================================
        // チュンビーム（中を捨てたときのカットイン・キャラごとの装飾）
        //
        //   解放条件：同じ卓の「ライバル」（chunBeamRivalNames）より多い点数で終局する。
        //   ライバル1人につき解放されるキャラが1体決まっている（chunBeamRivalCharas）。
        //   ライバル本人は自分に勝てないので、自キャラは最初から解放済みとして扱う。
        //
        //   すべて PlayerData（同期変数ゼロ）。卓では捨てた人の PlayerData を各自が読む。
        //   ビット番号＝キャラID（bit0=Vol / bit1=Shine）。
        // ============================================================
        public const string KEY_CHUN_UNLOCK = "kmj_chun_unlock"; // int 解放済みビット
        // ON/OFF は「OFFにしたキャラ」のビットで持つ＝未設定(0)が「解放したら即ON」になる。
        public const string KEY_CHUN_OFF = "kmj_chun_off";       // int OFFにしたキャラのビット
        public const string KEY_CHUN_HIDE = "kmj_chun_hide";     // int 1=他人のチュンビームを出さない（画面も声も）

        [Header("チュンビームの解放条件（ライバルの表示名 → 解放されるキャラID）")]
        [Tooltip("この人より多い点数で終局すると、同じ添字の chunBeamRivalCharas のキャラが解放される。"
                 + "VRChatの表示名は他人と重複しない。改名されたらここを直す。")]
        public string[] chunBeamRivalNames = new string[] { "<STAFF_NAME_2>", "<RIVAL_NAME>" };
        [Tooltip("chunBeamRivalNames と同じ並び。0=Vol / 1=Shine")]
        public int[] chunBeamRivalCharas = new int[] { CHARA_VOL, CHARA_SHINE };

        [Tooltip("ONにすると誰でもチュンビームが解放済みになる（テスト用）。リリース前に必ずOFF。")]
        public bool debugUnlockChunBeam = false;

        private int pendingChunUnlockBits_ = 0;
        private bool pendingChunOffSet_ = false;
        private int pendingChunOff_ = 0;
        private bool pendingChunHideSet_ = false;
        private bool pendingChunHide_ = false;

        // チュンビームを持つキャラか（いまは Vol と Shine だけ）。
        public bool IsChunBeamChara(int chara)
        {
            return chara == CHARA_VOL || chara == CHARA_SHINE;
        }

        // その表示名がライバルなら、解放されるキャラIDを返す。ライバルでなければ -1。
        public int FindChunBeamRivalChara(string displayName)
        {
            if (string.IsNullOrEmpty(displayName)) { return -1; }
            if (chunBeamRivalNames == null || chunBeamRivalCharas == null) { return -1; }
            int n = Mathf.Min(chunBeamRivalNames.Length, chunBeamRivalCharas.Length);
            for (int i = 0; i < n; ++i)
            {
                if (chunBeamRivalNames[i] == displayName) { return chunBeamRivalCharas[i]; }
            }
            return -1;
        }

        // そのキャラを解放するライバルの表示名（ロック表示用）。無ければ空文字。
        public string GetChunBeamRivalName(int chara)
        {
            if (chunBeamRivalNames == null || chunBeamRivalCharas == null) { return ""; }
            int n = Mathf.Min(chunBeamRivalNames.Length, chunBeamRivalCharas.Length);
            for (int i = 0; i < n; ++i)
            {
                if (chunBeamRivalCharas[i] == chara && chunBeamRivalNames[i] != null)
                {
                    return chunBeamRivalNames[i];
                }
            }
            return "";
        }

        private int ChunBit(int chara)
        {
            if (chara < 0 || chara >= NUM_CHARAS) { return 0; }
            return 1 << chara;
        }

        // 解放済みビット＋ライバル本人の自キャラ（保存値は書き換えない）。
        private bool IsChunBeamUnlockedFor(VRCPlayerApi player, int bits, int chara)
        {
            if (!IsChunBeamChara(chara)) { return false; }
            if (debugUnlockChunBeam) { return true; }
            if ((bits & ChunBit(chara)) != 0) { return true; }
            if (player != null && player.IsValid() && FindChunBeamRivalChara(player.displayName) == chara)
            {
                return true;
            }
            return false;
        }

        // 自分がそのキャラのチュンビームを解放済みか。
        public bool IsChunBeamUnlocked(int chara)
        {
            int bits = GetIntLocal(KEY_CHUN_UNLOCK, 0) | pendingChunUnlockBits_;
            return IsChunBeamUnlockedFor(Networking.LocalPlayer, bits, chara);
        }

        // 自分のそのキャラのチュンビームが ON か（未解放なら false）。
        public bool GetChunBeamOn(int chara)
        {
            if (!IsChunBeamUnlocked(chara)) { return false; }
            int off = pendingChunOffSet_ ? pendingChunOff_ : GetIntLocal(KEY_CHUN_OFF, 0);
            return (off & ChunBit(chara)) == 0;
        }

        public void SetChunBeamOn(int chara, bool on)
        {
            if (!IsChunBeamUnlocked(chara)) { return; }
            int off = pendingChunOffSet_ ? pendingChunOff_ : GetIntLocal(KEY_CHUN_OFF, 0);
            off = on ? (off & ~ChunBit(chara)) : (off | ChunBit(chara));
            if (!restored_) { pendingChunOff_ = off; pendingChunOffSet_ = true; return; }
            PlayerData.SetInt(KEY_CHUN_OFF, off);
        }

        // 解放する。新しく解放したときだけ true（結果画面のお知らせに使う）。
        public bool UnlockChunBeam(int chara)
        {
            if (!IsChunBeamChara(chara)) { return false; }
            int bit = ChunBit(chara);
            int saved = GetIntLocal(KEY_CHUN_UNLOCK, 0);
            if (((saved | pendingChunUnlockBits_) & bit) != 0) { return false; }
            if (!restored_) { pendingChunUnlockBits_ |= bit; return true; }
            PlayerData.SetInt(KEY_CHUN_UNLOCK, saved | bit);
            return true;
        }

        // 他人のチュンビームを出さないか（自分専用の設定）。
        public bool GetChunBeamHideOthers()
        {
            if (pendingChunHideSet_) { return pendingChunHide_; }
            return GetIntLocal(KEY_CHUN_HIDE, 0) != 0;
        }

        public void SetChunBeamHideOthers(bool hide)
        {
            if (!restored_) { pendingChunHide_ = hide; pendingChunHideSet_ = true; return; }
            PlayerData.SetInt(KEY_CHUN_HIDE, hide ? 1 : 0);
        }

        // 卓で使う：その人が中を捨てたら、チュンビームを出すべきか
        // （選んでいるキャラがチュンビーム持ち・解放済み・ON）。
        // 「他人のを出さない」設定は見る側の都合なので、ここでは見ない。
        public bool IsChunBeamActiveOf(VRCPlayerApi player)
        {
            if (player == null || !player.IsValid()) { return false; }
            if (player.isLocal)
            {
                return GetChunBeamOn(GetChara());
            }
            int chara = GetCharaOf(player);
            int bits = GetIntOf(player, KEY_CHUN_UNLOCK, 0);
            if (!IsChunBeamUnlockedFor(player, bits, chara)) { return false; }
            return (GetIntOf(player, KEY_CHUN_OFF, 0) & ChunBit(chara)) == 0;
        }

        // テスト用：自分の解放状態とON/OFFを消す（Editor の execute_code / Udon の SendCustomEvent から）。
        public void _DebugClearChunBeam()
        {
            pendingChunUnlockBits_ = 0;
            pendingChunOffSet_ = false;
            if (!restored_) { return; }
            PlayerData.SetInt(KEY_CHUN_UNLOCK, 0);
            PlayerData.SetInt(KEY_CHUN_OFF, 0);
        }

        // PlayerData復元前に半荘RESULTへ到達した場合の段位結果（最大1件）。
        // matchToken + rankResultAccepted_ で同じRESULTの二重受付を防ぐ。
        private bool pendingRankResult_ = false;
        private bool pendingRankIsSanma_ = false;
        private bool pendingRankIsTonpu_ = false;
        private int pendingRankPlace_ = 0;
        private int pendingRankFinalPoints_ = 0;
        private int pendingRankStartLv_ = 0;
        private int pendingRankStartValue_ = 0;
        private bool pendingRankAllTenju_ = false;
        private bool pendingRankStartReady_ = false;
        private int rankedMatchToken_ = 0;
        private bool rankResultAccepted_ = false;

        public override void OnPlayerRestored(VRCPlayerApi player)
        {
            if (player == null || !player.isLocal) { return; }

            restored_ = true;
            SanitizeAndPersistRankData();
            SeedPlaceCountsFromLog();
            SeedWinTypeCountsFromLog();
            FlushPendingWrites();
            FlushPendingRankResult();
            NotifyRankDisplayChanged();
        }

        // 保留中のキャラ／能力を PlayerData へ書き、フラグを落とす（保留無しなら何もしない）。
        // 通算の着順内訳（2〜4位）を後から追加したので、既存セーブには過去分が無い。
        // ただし「直近ログの件数＝通算対局数」なら全対局がログに残っている＝内訳を正確に復元できる。
        // ログから溢れた対局がある場合は推測で埋めず、0 から積み直す。
        private void SeedPlaceCountsFromLog()
        {
            for (int cat = 0; cat < NUM_CATS; ++cat)
            {
                int play = GetPlayCountCat(cat);
                if (play <= 0) { continue; }
                if (GetIntLocal(PlaceKeyCat(cat, 2), 0) > 0 ||
                    GetIntLocal(PlaceKeyCat(cat, 3), 0) > 0 ||
                    GetIntLocal(PlaceKeyCat(cat, 4), 0) > 0) { continue; }

                int[] places = GetRecentPlacesCat(cat);
                if (places.Length != play) { continue; }

                var counts = new int[5];
                bool usable = true;
                for (int i = 0; i < places.Length; ++i)
                {
                    int p = places[i];
                    if (p < 1 || p > 4) { usable = false; break; }
                    counts[p]++;
                }
                if (!usable) { continue; }
                if (counts[1] != GetWinCountCat(cat)) { continue; }
                if (counts[2] + counts[3] + counts[4] <= 0) { continue; }

                for (int place = 2; place <= 4; ++place)
                {
                    if (counts[place] > 0) { PlayerData.SetInt(PlaceKeyCat(cat, place), counts[place]); }
                }
            }
        }

        // 通算の和了種別も後から追加した。詳細CSVが全対局分そろっていて、
        // 和了数カウンタとも一致するときだけ、そこから正確に復元する。
        private void SeedWinTypeCountsFromLog()
        {
            for (int cat = 0; cat < NUM_CATS; ++cat)
            {
                if (GetIntLocal(WinTypeKeyCat(cat, 0), 0) > 0 ||
                    GetIntLocal(WinTypeKeyCat(cat, 1), 0) > 0 ||
                    GetIntLocal(WinTypeKeyCat(cat, 2), 0) > 0) { continue; }

                int play = GetPlayCountCat(cat);
                if (play <= 0) { continue; }

                int[] totals = GetProfileTotals(cat);
                if (totals[0] != play) { continue; }              // CSV から溢れている＝復元不可
                int wins = totals[2] + totals[3] + totals[4];
                if (wins <= 0) { continue; }
                if (wins != GetAgariCountCat(cat)) { continue; }  // 旧データ混在＝復元不可

                for (int type = 0; type < 3; ++type)
                {
                    int count = totals[2 + type];
                    if (count > 0) { PlayerData.SetInt(WinTypeKeyCat(cat, type), count); }
                }
            }
        }

        private int pendingRiichiBgm_;
        private bool pendingRiichiBgmSet_;
        public int GetRiichiBgm()
        {
            return Mathf.Clamp(pendingRiichiBgmSet_ ? pendingRiichiBgm_ : GetIntLocal("kmj_riichi_bgm", 0), 0, MahjongRiichiBgm.MAX_TRACK);
        }
        public void SetRiichiBgm(int track)
        {
            track = Mathf.Clamp(track, 0, MahjongRiichiBgm.MAX_TRACK);
            // 解禁前は曲を装備できない。0（なし）へ戻すのはいつでも通す。
            // 保存済みの値は消さない＝降格しても再昇格でそのまま戻る。
            if (track > 0 && !IsRiichiBgmUnlocked()) { return; }
            if (!restored_) { pendingRiichiBgm_ = track; pendingRiichiBgmSet_ = true; return; }
            PlayerData.SetInt("kmj_riichi_bgm", track);
        }

        private void FlushPendingWrites()
        {
            if (pendingRiichiBgmSet_)
            {
                PlayerData.SetInt("kmj_riichi_bgm", pendingRiichiBgm_);
                pendingRiichiBgmSet_ = false;
            }
            if (pendingTileBackColorSet_)
            {
                PlayerData.SetInt("kmj_tile_back_color", pendingTileBackColor_);
                pendingTileBackColorSet_ = false;
            }
            if (pendingTableTopColorSet_)
            {
                PlayerData.SetInt("kmj_table_top_color", pendingTableTopColor_);
                pendingTableTopColorSet_ = false;
            }
            if (pendingCharaSet_)
            {
                pendingCharaSet_ = false;
                PlayerData.SetInt(KEY_CHARA, SanitizeChara(pendingChara_));
                PlayerData.SetInt(KEY_CHARA_SCHEMA, CHARA_SCHEMA_CURRENT);
            }
            if (pendingAbilitySet_)
            {
                pendingAbilitySet_ = false;
                PlayerData.SetInt(KEY_ABILITY_ON, pendingAbilityOn_ ? 1 : 0);
            }
            if (pendingAgariFxSet_)
            {
                pendingAgariFxSet_ = false;
                PlayerData.SetInt(KEY_AGARI_FX, SanitizeAgariFx(pendingAgariFx_));
            }
            if (pendingReachTsumogiriSet_)
            {
                pendingReachTsumogiriSet_ = false;
                PlayerData.SetInt(KEY_REACH_TSUMOGIRI, pendingReachTsumogiri_);
            }
            if (pendingPressGuardSet_)
            {
                pendingPressGuardSet_ = false;
                PlayerData.SetInt(KEY_PRESS_GUARD, pendingPressGuard_);
            }
            if (pendingTouchInputSet_)
            {
                pendingTouchInputSet_ = false;
                PlayerData.SetInt(KEY_TOUCH_INPUT, pendingTouchInput_);
            }
            if (pendingTileGrabSet_)
            {
                pendingTileGrabSet_ = false;
                PlayerData.SetInt(KEY_TILE_GRAB, pendingTileGrab_);
            }
            if (pendingChunUnlockBits_ != 0)
            {
                PlayerData.SetInt(KEY_CHUN_UNLOCK, GetIntLocal(KEY_CHUN_UNLOCK, 0) | pendingChunUnlockBits_);
                pendingChunUnlockBits_ = 0;
            }
            if (pendingChunOffSet_)
            {
                pendingChunOffSet_ = false;
                PlayerData.SetInt(KEY_CHUN_OFF, pendingChunOff_);
            }
            if (pendingChunHideSet_)
            {
                pendingChunHideSet_ = false;
                PlayerData.SetInt(KEY_CHUN_HIDE, pendingChunHide_ ? 1 : 0);
            }
        }

        // 復元済みか（UI側が書き込み可否の表示に使える）。
        public bool IsRestored()
        {
            return restored_;
        }

        // ============================================================
        // 内部：共通の読み書きヘルパ
        // ============================================================

        private int GetIntLocal(string key, int defaultValue)
        {
            var lp = Networking.LocalPlayer;
            if (lp == null) { return defaultValue; }

            int v;
            if (PlayerData.TryGetInt(lp, key, out v))
            {
                return v;
            }
            return defaultValue;
        }

        private int GetIntOf(VRCPlayerApi player, string key, int defaultValue)
        {
            if (player == null || !player.IsValid()) { return defaultValue; }

            int v;
            if (PlayerData.TryGetInt(player, key, out v))
            {
                return v;
            }
            return defaultValue;
        }

        // 段位データだけは、欠損と値0を区別する必要があるためTryGetを直接使う。
        private bool TryGetIntLocal(string key, out int value)
        {
            value = 0;
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return false; }
            return PlayerData.TryGetInt(lp, key, out value);
        }

        // 復元完了時に段位保存値を安全域へ正規化する。
        // schema未設定でも旧kmj_rank_lv/ptが存在すれば値を引き継ぎ、新規だけLv0/0ptにする。
        private void SanitizeAndPersistRankData()
        {
            int rawLv;
            bool hasLv = TryGetIntLocal(KEY_RANK_LV, out rawLv);
            int lv = hasLv ? SanitizeRankLv(rawLv) : RANK_MIN_LV;

            int rawPt;
            bool hasPt = TryGetIntLocal(KEY_RANK_PT, out rawPt);
            int pt = 0;
            if (lv < RANK_TENJU_FIRST_LV)
            {
                // lvが無い新規/片側破損データは初獣★1・0ptへ。旧値継承はlvが存在する時だけ。
                int inheritedPt = hasLv && hasPt ? rawPt : (hasLv ? GetInitialRankPtForLv(lv) : 0);
                pt = SanitizeRankPtForLv(lv, inheritedPt);
            }

            int rawOrb;
            bool hasOrb = TryGetIntLocal(KEY_TENJU_ORB_X10, out rawOrb);
            int orb = SanitizeTenjuOrbX10(hasOrb ? rawOrb : TENJU_ORB_INITIAL_X10);

            int rawSchema;
            bool hasSchema = TryGetIntLocal(KEY_RANK_SCHEMA, out rawSchema);

            if (!hasLv || rawLv != lv) { PlayerData.SetInt(KEY_RANK_LV, lv); }
            if (!hasPt || rawPt != pt) { PlayerData.SetInt(KEY_RANK_PT, pt); }
            if (!hasOrb || rawOrb != orb) { PlayerData.SetInt(KEY_TENJU_ORB_X10, orb); }
            if (!hasSchema || rawSchema != RANK_SCHEMA_CURRENT)
            {
                PlayerData.SetInt(KEY_RANK_SCHEMA, RANK_SCHEMA_CURRENT);
            }
        }

        private string GetStringLocal(string key, string defaultValue)
        {
            var lp = Networking.LocalPlayer;
            if (lp == null) { return defaultValue; }

            string v;
            if (PlayerData.TryGetString(lp, key, out v))
            {
                return v;
            }
            return defaultValue;
        }

        private string GetStringOf(VRCPlayerApi player, string key, string defaultValue)
        {
            if (player == null || !player.IsValid()) { return defaultValue; }

            string v;
            if (PlayerData.TryGetString(player, key, out v))
            {
                return v;
            }
            return defaultValue;
        }

        // ============================================================
        // 段位計算（PlayerData非依存。Editor検証も必ずこの実装を呼ぶ）
        // ============================================================

        public int SanitizeRankLv(int lv)
        {
            if (lv < RANK_MIN_LV) { return RANK_MIN_LV; }
            if (lv > RANK_MAX_LV) { return RANK_MAX_LV; }
            return lv;
        }

        public int SanitizeTenjuOrbX10(int orbX10)
        {
            if (orbX10 < 0) { return 0; }
            if (orbX10 > TENJU_ORB_MAX_X10) { return TENJU_ORB_MAX_X10; }
            return orbX10;
        }

        public int GetInitialRankPtForLv(int lv)
        {
            lv = SanitizeRankLv(lv);
            switch (lv)
            {
                case 0: return 0;
                case 1: return 40;
                case 2: return 100;
                case 3: return 300;
                case 4: return 400;
                case 5: return 500;
                case 6: return 600;
                case 7: return 700;
                case 8: return 1000;
                case 9: return 1400;
                case 10: return 1600;
                case 11: return 1800;
                case 12: return 2000;
                case 13: return 3000;
                case 14: return 4500;
            }
            return 0; // 天獣では段位ptを使わない
        }

        public int GetPromotionRankPtForLv(int lv)
        {
            lv = SanitizeRankLv(lv);
            switch (lv)
            {
                case 0: return 20;
                case 1: return 80;
                case 2: return 200;
                case 3: return 600;
                case 4: return 800;
                case 5: return 1000;
                case 6: return 1200;
                case 7: return 1400;
                case 8: return 2000;
                case 9: return 2800;
                case 10: return 3200;
                case 11: return 3600;
                case 12: return 4000;
                case 13: return 6000;
                case 14: return 9000;
            }
            return 0;
        }

        public int SanitizeRankPtForLv(int lv, int pt)
        {
            lv = SanitizeRankLv(lv);
            if (lv >= RANK_TENJU_FIRST_LV) { return 0; }
            if (pt < 0) { return 0; }
            int max = GetPromotionRankPtForLv(lv);
            if (pt > max) { return max; }
            return pt;
        }

        public bool CanDemoteFromRankLv(int lv)
        {
            lv = SanitizeRankLv(lv);
            return lv >= 4 && lv < RANK_TENJU_FIRST_LV;
        }

        // 大段位index。頭上アイコンは内部Lvではなく0～5のこの値だけを参照する。
        public int GetMajorRankIndexForLv(int lv)
        {
            lv = SanitizeRankLv(lv);
            if (lv >= RANK_TENJU_FIRST_LV) { return 5; }
            return lv / 3;
        }

        // ★表記を将来「紋」へ差し替える場合は、この1メソッドだけを変更する。
        public string GetRankDisplayNameForLv(int lv)
        {
            lv = SanitizeRankLv(lv);
            bool en = En();
            bool ko = Ko();
            bool zhs = Zhs();
            bool zht = Zht();

            if (lv >= RANK_TENJU_FIRST_LV)
            {
                return (ko ? RANK_KO_5 : zhs ? RANK_ZHS_5 : zht ? RANK_ZHT_5 : en ? RANK_EN_5 : "天獣")
                    + " Lv." + (lv - RANK_TENJU_FIRST_LV + 1).ToString();
            }

            int majorIndex = GetMajorRankIndexForLv(lv);
            string major = ko ? RANK_KO_0 : zhs ? RANK_ZHS_0 : zht ? RANK_ZHT_0 : en ? RANK_EN_0 : "初獣";
            if (majorIndex == 1) { major = ko ? RANK_KO_1 : zhs ? RANK_ZHS_1 : zht ? RANK_ZHT_1 : en ? RANK_EN_1 : "鋭爪"; }
            else if (majorIndex == 2) { major = ko ? RANK_KO_2 : zhs ? RANK_ZHS_2 : zht ? RANK_ZHT_2 : en ? RANK_EN_2 : "剛牙"; }
            else if (majorIndex == 3) { major = ko ? RANK_KO_3 : zhs ? RANK_ZHS_3 : zht ? RANK_ZHT_3 : en ? RANK_EN_3 : "獣威"; }
            else if (majorIndex == 4) { major = ko ? RANK_KO_4 : zhs ? RANK_ZHS_4 : zht ? RANK_ZHT_4 : en ? RANK_EN_4 : "幻獣"; }
            return major + "★" + ((lv % 3) + 1).ToString();
        }

        private string FormatOrbX10(int orbX10)
        {
            orbX10 = SanitizeTenjuOrbX10(orbX10);
            return (orbX10 / 10).ToString() + "." + (orbX10 % 10).ToString();
        }

        // 最終持ち点の素点。1000点未満の端数を符号に関係なく絶対値方向へ切り上げる。
        // C#の負数除算の丸め方向には依存しない。
        public int CalculateBaseRankPoint(bool isSanma, int finalPoints)
        {
            int startPoints = isSanma ? 35000 : 25000;
            int diff = finalPoints - startPoints;
            if (diff == 0) { return 0; }

            if (diff > 0)
            {
                int value = diff / 1000;
                if (diff % 1000 != 0) { ++value; }
                return value;
            }

            int magnitude = -diff;
            int negativeValue = magnitude / 1000;
            if (magnitude % 1000 != 0) { ++negativeValue; }
            return -negativeValue;
        }

        // 対局開始時の内部Lvに対応する固定卓（銅/銀/金/玉/王座相当）の順位点。
        public int GetPlacementRankPoint(bool isSanma, int startRankLv, int place)
        {
            int lv = SanitizeRankLv(startRankLv);
            if (lv >= RANK_TENJU_FIRST_LV) { return 0; }

            if (isSanma)
            {
                if (place < 1 || place > 3) { return 0; }
                if (place == 2) { return 0; }
                if (place == 1)
                {
                    if (lv <= 2) { return 45; }
                    if (lv <= 5) { return 75; }
                    if (lv <= 8) { return 120; }
                    if (lv <= 11) { return 175; }
                    return 255;
                }

                if (lv <= 2) { return -15; }
                if (lv == 3) { return -35; }
                if (lv == 4) { return -55; }
                if (lv == 5) { return -75; }
                if (lv == 6) { return -95; }
                if (lv == 7) { return -115; }
                if (lv == 8) { return -135; }
                if (lv == 9) { return -180; }
                if (lv == 10) { return -205; }
                if (lv == 11) { return -230; }
                if (lv == 12) { return -255; }
                if (lv == 13) { return -280; }
                return -305;
            }

            if (place < 1 || place > 4) { return 0; }
            if (place == 3) { return -5; }
            if (place == 1)
            {
                if (lv <= 2) { return 35; }
                if (lv <= 5) { return 55; }
                if (lv <= 8) { return 95; }
                if (lv <= 11) { return 125; }
                return 135;
            }
            if (place == 2)
            {
                if (lv <= 2) { return 15; }
                if (lv <= 5) { return 25; }
                if (lv <= 8) { return 45; }
                if (lv <= 11) { return 60; }
                return 65;
            }

            if (lv <= 2) { return -15; }
            if (lv == 3) { return -35; }
            if (lv == 4) { return -55; }
            if (lv == 5) { return -75; }
            if (lv == 6) { return -95; }
            if (lv == 7) { return -115; }
            if (lv == 8) { return -135; }
            if (lv == 9) { return -180; }
            if (lv == 10) { return -195; }
            if (lv == 11) { return -210; }
            if (lv == 12) { return -225; }
            if (lv == 13) { return -240; }
            return -255;
        }

        public int GetTenjuDeltaX10(bool isSanma, int place, bool allTenju)
        {
            int delta = 0;
            if (isSanma)
            {
                if (place == 1) { delta = 5; }
                else if (place == 3) { delta = -5; }
            }
            else
            {
                if (place == 1) { delta = 5; }
                else if (place == 2) { delta = 2; }
                else if (place == 3) { delta = -2; }
                else if (place == 4) { delta = -5; }
            }
            return allTenju ? delta * 2 : delta;
        }

        // ============================================================
        // 東風戦の段位変動（2026-08-18 追加）
        //
        // 東風戦は所要時間が半荘のおよそ半分なので、順位点・天珠を半荘と同額にすると
        // 「東風だけ回すのが最効率」となり半荘が過疎化する。雀魂に倣って半分にする。
        //   ・素点（CalculateBaseRankPoint）は圧縮しない＝雀魂と同じ。
        //   ・端数は必ず絶対値方向へ「切り捨て」る。切り上げにすると半荘の50%超になり、
        //     半分の所要時間で半分超のPtが得られてしまい、圧縮した意味がなくなる。
        //   ・獲得側・失点側とも同じ規則なので、リスクとリターンの比は半荘と変わらない。
        // ============================================================

        // 絶対値方向へ切り捨てて半分にする。負数は (uint) 化を挟まない（Udon停止対策）。
        private int HalveTowardZero(int value)
        {
            if (value >= 0) { return value / 2; }
            return -((-value) / 2);
        }

        // 局数ルールを含む順位点。isTonpu=false なら従来の半荘値をそのまま返す。
        public int GetPlacementRankPointFor(bool isSanma, bool isTonpu, int startRankLv, int place)
        {
            int pt = GetPlacementRankPoint(isSanma, startRankLv, place);
            return isTonpu ? HalveTowardZero(pt) : pt;
        }

        // 局数ルールを含む天珠変動。全員天獣2倍は半分にした後へ掛ける
        // （先に2倍して半分にすると東風の圧縮が消えるため、順序を固定する）。
        public int GetTenjuDeltaX10For(bool isSanma, bool isTonpu, int place, bool allTenju)
        {
            if (!isTonpu) { return GetTenjuDeltaX10(isSanma, place, allTenju); }
            int delta = HalveTowardZero(GetTenjuDeltaX10(isSanma, place, false));
            return allTenju ? delta * 2 : delta;
        }

        // 半荘の結果計算（既存呼び出し互換のラッパ）。
        public int[] CalculateRankedHanchanResult(bool isSanma, int place, int finalPoints,
                                                  int startRankLv, int startValue, bool allTenju)
        {
            return CalculateRankedGameResult(isSanma, false, place, finalPoints,
                                             startRankLv, startValue, allTenju);
        }

        // 返却: [内部Lv, 通常pt, 天珠x10, 今回増減]。
        // startValue は通常段位ならpt、天獣なら天珠x10。開始時Lvを途中の昇段後に再利用しない。
        // isTonpu=true（東風戦）は順位点・天珠だけを半分にする（素点は圧縮しない）。
        public int[] CalculateRankedGameResult(bool isSanma, bool isTonpu, int place, int finalPoints,
                                               int startRankLv, int startValue, bool allTenju)
        {
            int[] result = new int[RANK_RESULT_SIZE];
            int lv = SanitizeRankLv(startRankLv);

            if (lv >= RANK_TENJU_FIRST_LV)
            {
                int orb = SanitizeTenjuOrbX10(startValue);
                int delta = GetTenjuDeltaX10For(isSanma, isTonpu, place, allTenju);
                int nextOrb = orb + delta;

                if (nextOrb >= TENJU_ORB_MAX_X10)
                {
                    if (lv < RANK_TENJU_LAST_LV)
                    {
                        ++lv;
                        nextOrb = TENJU_ORB_INITIAL_X10;
                    }
                    else
                    {
                        nextOrb = TENJU_ORB_MAX_X10;
                    }
                }
                else if (nextOrb < 0)
                {
                    if (lv > RANK_TENJU_FIRST_LV)
                    {
                        --lv;
                        nextOrb = TENJU_ORB_INITIAL_X10;
                    }
                    else
                    {
                        lv = 14;
                        result[RANK_RESULT_PT] = GetInitialRankPtForLv(14);
                        nextOrb = TENJU_ORB_INITIAL_X10;
                    }
                }

                result[RANK_RESULT_LV] = lv;
                result[RANK_RESULT_ORB_X10] = nextOrb;
                result[RANK_RESULT_DELTA] = delta;
                return result;
            }

            int pt = SanitizeRankPtForLv(lv, startValue);
            int normalDelta = GetPlacementRankPointFor(isSanma, isTonpu, lv, place)
                              + CalculateBaseRankPoint(isSanma, finalPoints);
            int nextPt = pt + normalDelta;

            if (nextPt >= GetPromotionRankPtForLv(lv))
            {
                // 通常段位内の昇段は、新段位の昇段点50%へ余剰ptを加える。
                // 大きな増加で複数段階を越えた場合も、同じ規則を順に適用する。
                while (lv < RANK_TENJU_FIRST_LV && nextPt >= GetPromotionRankPtForLv(lv))
                {
                    int overflowPt = nextPt - GetPromotionRankPtForLv(lv);
                    if (lv == 14)
                    {
                        // 通常ptから天珠へ単位が変わるため、既存仕様どおり天珠10.0から開始する。
                        lv = RANK_TENJU_FIRST_LV;
                        nextPt = 0;
                        result[RANK_RESULT_ORB_X10] = TENJU_ORB_INITIAL_X10;
                        break;
                    }

                    ++lv;
                    nextPt = GetInitialRankPtForLv(lv) + overflowPt;
                    result[RANK_RESULT_ORB_X10] = TENJU_ORB_INITIAL_X10;
                }
            }
            else if (nextPt < 0)
            {
                if (CanDemoteFromRankLv(lv))
                {
                    --lv;
                    nextPt = GetInitialRankPtForLv(lv);
                }
                else
                {
                    nextPt = 0;
                }
            }

            result[RANK_RESULT_LV] = lv;
            result[RANK_RESULT_PT] = nextPt;
            if (result[RANK_RESULT_ORB_X10] == 0)
            {
                result[RANK_RESULT_ORB_X10] = TENJU_ORB_INITIAL_X10;
            }
            result[RANK_RESULT_DELTA] = normalDelta;
            return result;
        }

        // 統計キーの解決（カテゴリ別）。bool 版APIは段位戦2カテゴリへの互換ラッパとして残す。
        private int CatOf(bool isSanma) { return isSanma ? CAT_SANMA : CAT_YONMA; }
        private string PlayKeyCat(int cat) { return cat == CAT_SANMA ? KEY_ST3_PLAY : (cat == CAT_FREE ? KEY_STF_PLAY : KEY_ST4_PLAY); }
        private string WinKeyCat(int cat) { return cat == CAT_SANMA ? KEY_ST3_WIN : (cat == CAT_FREE ? KEY_STF_WIN : KEY_ST4_WIN); }
        private string AgariKeyCat(int cat) { return cat == CAT_SANMA ? KEY_ST3_AGARI : (cat == CAT_FREE ? KEY_STF_AGARI : KEY_ST4_AGARI); }
        private string HojuKeyCat(int cat) { return cat == CAT_SANMA ? KEY_ST3_HOJU : (cat == CAT_FREE ? KEY_STF_HOJU : KEY_ST4_HOJU); }
        private string RecentKeyCat(int cat) { return cat == CAT_SANMA ? KEY_RECENT3 : (cat == CAT_FREE ? KEY_RECENTF : KEY_RECENT); }
        private string HandV2KeyCat(int cat) { return cat == CAT_SANMA ? KEY_ST3_HAND_V2 : (cat == CAT_FREE ? KEY_STF_HAND_V2 : KEY_ST4_HAND_V2); }
        private string AgariV2KeyCat(int cat) { return cat == CAT_SANMA ? KEY_ST3_AGARI_V2 : (cat == CAT_FREE ? KEY_STF_AGARI_V2 : KEY_ST4_AGARI_V2); }
        private string HojuV2KeyCat(int cat) { return cat == CAT_SANMA ? KEY_ST3_HOJU_V2 : (cat == CAT_FREE ? KEY_STF_HOJU_V2 : KEY_ST4_HOJU_V2); }
        // 通算の着順内訳は 2〜4位だけ（1位は WinKeyCat を使い回す）。
        private string PlaceKeyCat(int cat, int place)
        {
            if (place == 2) { return cat == CAT_SANMA ? KEY_ST3_P2 : (cat == CAT_FREE ? KEY_STF_P2 : KEY_ST4_P2); }
            if (place == 3) { return cat == CAT_SANMA ? KEY_ST3_P3 : (cat == CAT_FREE ? KEY_STF_P3 : KEY_ST4_P3); }
            return cat == CAT_SANMA ? KEY_ST3_P4 : (cat == CAT_FREE ? KEY_STF_P4 : KEY_ST4_P4);
        }
        // 通算の和了種別。type: 0=立直 / 1=副露 / 2=闇聴（counters[2..4] と同じ並び）。
        private string WinTypeKeyCat(int cat, int type)
        {
            if (type == 0) { return cat == CAT_SANMA ? KEY_ST3_WR : (cat == CAT_FREE ? KEY_STF_WR : KEY_ST4_WR); }
            if (type == 1) { return cat == CAT_SANMA ? KEY_ST3_WO : (cat == CAT_FREE ? KEY_STF_WO : KEY_ST4_WO); }
            return cat == CAT_SANMA ? KEY_ST3_WD : (cat == CAT_FREE ? KEY_STF_WD : KEY_ST4_WD);
        }
        private string PlayKey(bool isSanma) { return PlayKeyCat(CatOf(isSanma)); }
        private string WinKey(bool isSanma) { return WinKeyCat(CatOf(isSanma)); }
        private string AgariKey(bool isSanma) { return AgariKeyCat(CatOf(isSanma)); }
        private string HojuKey(bool isSanma) { return HojuKeyCat(CatOf(isSanma)); }

        // ============================================================
        // Get（ローカルプレイヤー用）
        // ============================================================

        // 保留中はローカル値を返す（復元前でも UI が選択を反映できるようにする）。
        public int GetChara()
        {
            if (pendingCharaSet_) { return SanitizeChara(pendingChara_); }
            int chara = GetIntLocal(KEY_CHARA, CHARA_VOL);
            int schema = GetIntLocal(KEY_CHARA_SCHEMA, 0);
            return SanitizeStoredChara(chara, schema);
        }

        public bool GetAbilityOn()
        {
            if (pendingAbilitySet_) { return pendingAbilityOn_; }
            return GetIntLocal(KEY_ABILITY_ON, 0) != 0;
        }

        // 和了演出コスメID。未設定・不正値は0（既定演出）。保留中は保留値。
        public int GetAgariFx()
        {
            if (pendingAgariFxSet_) { return SanitizeAgariFx(pendingAgariFx_); }
            return SanitizeAgariFx(GetIntLocal(KEY_AGARI_FX, 0));
        }

        public int GetRankLv()
        {
            return SanitizeRankLv(GetIntLocal(KEY_RANK_LV, RANK_MIN_LV));
        }

        public int GetRankPt()
        {
            int lv = GetRankLv();
            return SanitizeRankPtForLv(lv, GetIntLocal(KEY_RANK_PT, GetInitialRankPtForLv(lv)));
        }

        public int GetTenjuOrbX10()
        {
            return SanitizeTenjuOrbX10(GetIntLocal(KEY_TENJU_ORB_X10, TENJU_ORB_INITIAL_X10));
        }

        public int GetMajorRankIndex() { return GetMajorRankIndexForLv(GetRankLv()); }
        public string GetRankDisplayName() { return GetRankDisplayNameForLv(GetRankLv()); }
        public int GetCurrentPromotionRankPt() { return GetPromotionRankPtForLv(GetRankLv()); }
        public int GetCurrentRankValue()
        {
            return GetRankLv() >= RANK_TENJU_FIRST_LV ? GetTenjuOrbX10() : GetRankPt();
        }
        public bool IsLocalPlayerDataRestored() { return restored_; }

        // MainMenuのHeader/Profileは必ずこの1か所で生成した同じ文字列を使う。
        public string GetRankDisplayText()
        {
            int lv = GetRankLv();
            return GetRankDisplayTextForValues(lv, GetRankPt(), GetTenjuOrbX10());
        }

        // Editor検証でも同じ表示生成を使えるPlayerData非依存版。
        public string GetRankDisplayTextForValues(int lv, int pt, int orbX10)
        {
            lv = SanitizeRankLv(lv);
            string name = GetRankDisplayNameForLv(lv);
            if (lv >= RANK_TENJU_FIRST_LV)
            {
                return name + (Ko() ? "  " + TENJU_KO + " " : En() ? "  " + TENJU_EN + " " : "  天珠 ")  // 天珠は中国語も同じ字
                       + FormatOrbX10(orbX10) + " / 20.0";
            }
            return name + "  " + SanitizeRankPtForLv(lv, pt).ToString() + " / "
                   + GetPromotionRankPtForLv(lv).ToString() + " pt";
        }
        public int GetPlayCount(bool isSanma) { return GetIntLocal(PlayKey(isSanma), 0); }
        public int GetWinCount(bool isSanma) { return GetIntLocal(WinKey(isSanma), 0); }
        public int GetAgariCount(bool isSanma) { return GetIntLocal(AgariKey(isSanma), 0); }
        public int GetHojuCount(bool isSanma) { return GetIntLocal(HojuKey(isSanma), 0); }
        public string GetRecent() { return GetStringLocal(KEY_RECENT, ""); }

        // カテゴリ指定版（プロフィールの戦績タブ用。cat = CAT_YONMA / CAT_SANMA / CAT_FREE）。
        public int GetPlayCountCat(int cat) { return GetIntLocal(PlayKeyCat(cat), 0); }
        public int GetWinCountCat(int cat) { return GetIntLocal(WinKeyCat(cat), 0); }
        public int GetAgariCountCat(int cat) { return GetIntLocal(AgariKeyCat(cat), 0); }
        public int GetHojuCountCat(int cat) { return GetIntLocal(HojuKeyCat(cat), 0); }
        public string GetRecentCat(int cat) { return GetStringLocal(RecentKeyCat(cat), ""); }
        public int GetHandCountV2Cat(int cat) { return GetIntLocal(HandV2KeyCat(cat), 0); }
        public int GetAgariCountV2Cat(int cat) { return GetIntLocal(AgariV2KeyCat(cat), 0); }
        public int GetHojuCountV2Cat(int cat) { return GetIntLocal(HojuV2KeyCat(cat), 0); }
        /// <summary>
        /// プロフィールの通算値。
        /// [0]=対局数 / [1]〜[4]=1〜4位の回数 / [5]〜[7]=立直・副露・闇聴の和了数。
        /// 1位と分母は右上の統計ブロックと同じカウンタなので、両方の1位率は常に一致する。
        /// 内訳を持つ前の対局は復元できないため、[1]〜[4]の合計が [0] を下回ることがある。
        /// </summary>
        public int[] GetLifetimeTotalsCat(int cat)
        {
            var totals = new int[8];
            if (cat < 0 || cat >= NUM_CATS) { return totals; }
            totals[0] = GetPlayCountCat(cat);
            totals[1] = GetWinCountCat(cat);
            for (int place = 2; place <= 4; ++place) { totals[place] = GetIntLocal(PlaceKeyCat(cat, place), 0); }
            for (int type = 0; type < 3; ++type) { totals[5 + type] = GetIntLocal(WinTypeKeyCat(cat, type), 0); }
            return totals;
        }


        // ============================================================
        // Get（他プレイヤー指定版：プロフィール閲覧用の将来拡張）
        // ============================================================

        public int GetCharaOf(VRCPlayerApi player)
        {
            int chara = GetIntOf(player, KEY_CHARA, CHARA_VOL);
            int schema = GetIntOf(player, KEY_CHARA_SCHEMA, 0);
            return SanitizeStoredChara(chara, schema);
        }
        public bool GetAbilityOnOf(VRCPlayerApi player) { return GetIntOf(player, KEY_ABILITY_ON, 0) != 0; }

        // 和了演出コスメID（他プレイヤー版）。卓側が「和了者のコスメ」を解決するのに使う。
        // PlayerData は全クライアントから読めるため、同期変数を増やさずに全員の表示が一致する。
        public int GetAgariFxOf(VRCPlayerApi player) { return SanitizeAgariFx(GetIntOf(player, KEY_AGARI_FX, 0)); }

        public int GetRankLvOf(VRCPlayerApi player)
        {
            return SanitizeRankLv(GetIntOf(player, KEY_RANK_LV, RANK_MIN_LV));
        }
        public int GetRankPtOf(VRCPlayerApi player)
        {
            int lv = GetRankLvOf(player);
            return SanitizeRankPtForLv(lv, GetIntOf(player, KEY_RANK_PT, GetInitialRankPtForLv(lv)));
        }
        public int GetTenjuOrbX10Of(VRCPlayerApi player)
        {
            return SanitizeTenjuOrbX10(GetIntOf(player, KEY_TENJU_ORB_X10, TENJU_ORB_INITIAL_X10));
        }
        public int GetMajorRankIndexOf(VRCPlayerApi player)
        {
            return GetMajorRankIndexForLv(GetRankLvOf(player));
        }

        // 全員天獣2倍判定専用。欠損・無効・範囲外をclampして天獣扱いせず、必ずfalseへ倒す。
        public bool IsSafelyReadableTenjuPlayer(VRCPlayerApi player)
        {
            if (player == null || !Utilities.IsValid(player)) { return false; }
            int rawLv;
            if (!PlayerData.TryGetInt(player, KEY_RANK_LV, out rawLv)) { return false; }
            return rawLv >= RANK_TENJU_FIRST_LV && rawLv <= RANK_TENJU_LAST_LV;
        }

        // 段位ランキングの掲載可否。PlayerData未復元の人は読めず「初獣★1 0pt」に
        // 見えてしまうため、キーの有無で弾く（復元後は必ずKEY_RANK_LVが書かれている）。
        public bool HasRankDataOf(VRCPlayerApi player)
        {
            if (player == null || !Utilities.IsValid(player)) { return false; }
            int rawLv;
            return PlayerData.TryGetInt(player, KEY_RANK_LV, out rawLv);
        }
        public int GetPlayCountOf(VRCPlayerApi player, bool isSanma) { return GetIntOf(player, PlayKey(isSanma), 0); }
        public int GetWinCountOf(VRCPlayerApi player, bool isSanma) { return GetIntOf(player, WinKey(isSanma), 0); }
        public int GetAgariCountOf(VRCPlayerApi player, bool isSanma) { return GetIntOf(player, AgariKey(isSanma), 0); }
        public int GetHojuCountOf(VRCPlayerApi player, bool isSanma) { return GetIntOf(player, HojuKey(isSanma), 0); }
        public string GetRecentOf(VRCPlayerApi player) { return GetStringOf(player, KEY_RECENT, ""); }

        // 段位戦だけの通算対局数（段位ランキングの「通算」列）。
        public int GetRankedPlayCount() { return GetIntLocal(KEY_RANKED_PLAY, 0); }
        public int GetRankedPlayCountOf(VRCPlayerApi player) { return GetIntOf(player, KEY_RANKED_PLAY, 0); }

        // ============================================================
        // デイリーランキング（インスタンス内・累計スコア／JST5時リセット）
        //
        // 加算は ApplyRankedGameResultNow の1か所だけ。そこは「段位が動く対局」しか
        // 通らないので、フリー対局・デバッグ局・観戦は除外処理を書かなくても入らない。
        // 計算式は PlayerData 非依存（Editor検証からも同じ実装を呼べる）。
        // ============================================================

        // 集計日の境目（JSTの時）。深夜の対局を前日に数えるため朝5時で日付を切り替える
        // （昼夜スイッチ DayNightSwitch の「朝に昼へ戻す」時刻と揃えてある）。
        public const int DAY_BOUNDARY_HOUR_JST = 5;

        // 集計日を yyyyMMdd の整数で返す（2026-09-03 → 20260903）。JST の朝5時で切り替わる
        // ＝10/4 の 2時は 20261003。TimeSpan演算を避けて桁で持つ＝ログでもそのまま読める。
        public int GetJstDayIndex()
        {
            System.DateTime jst = Networking.GetNetworkDateTime().AddHours(9.0 - DAY_BOUNDARY_HOUR_JST);
            return jst.Year * 10000 + jst.Month * 100 + jst.Day;
        }

        public string GetJstDateText()
        {
            int day = GetJstDayIndex();
            return (day / 10000).ToString() + "/" + ((day / 100) % 100).ToString() + "/" + (day % 100).ToString();
        }

        // 順位点（デイリー専用）。段位ptの順位点は段位が上がるほど大きく、
        // ランキング表に使うと上位段位が有利すぎるため、ここでは段位に依らない固定値にする。
        // 東風戦は半荘の半分（素点は圧縮しない）＝段位側の東風ルールと揃える。
        public int GetDailyUma(bool isSanma, bool isTonpu, int place)
        {
            int uma = 0;
            if (isSanma)
            {
                if (place == 1) { uma = 30; }
                else if (place == 3) { uma = -30; }
            }
            else
            {
                if (place == 1) { uma = 30; }
                else if (place == 2) { uma = 10; }
                else if (place == 3) { uma = -10; }
                else if (place == 4) { uma = -30; }
            }
            return isTonpu ? HalveTowardZero(uma) : uma;
        }

        // 1対局分のデイリースコア＝素点（1000点＝1）＋順位点。
        public int CalculateDailyScore(bool isSanma, bool isTonpu, int place, int finalPoints)
        {
            int maxPlace = isSanma ? 3 : 4;
            if (place < 1 || place > maxPlace) { return 0; }
            return CalculateBaseRankPoint(isSanma, finalPoints) + GetDailyUma(isSanma, isTonpu, place);
        }

        // 自分の分だけを書く（PlayerDataの仕様）。日付が変わっていたら0から数え直す。
        private void AddDailyRankingResult(bool isSanma, bool isTonpu, int place, int finalPoints)
        {
            if (!restored_) { return; }

            int today = GetJstDayIndex();
            bool sameDay = GetIntLocal(KEY_DAY_INDEX, 0) == today;
            int score = sameDay ? GetIntLocal(KEY_DAY_SCORE, 0) : 0;
            int games = sameDay ? GetIntLocal(KEY_DAY_GAMES, 0) : 0;
            int tops = sameDay ? GetIntLocal(KEY_DAY_TOP, 0) : 0;

            score += CalculateDailyScore(isSanma, isTonpu, place, finalPoints);
            ++games;
            if (place == 1) { ++tops; }

            PlayerData.SetInt(KEY_DAY_INDEX, today);
            PlayerData.SetInt(KEY_DAY_SCORE, score);
            PlayerData.SetInt(KEY_DAY_GAMES, games);
            PlayerData.SetInt(KEY_DAY_TOP, tops);

            // 最高和了。RecordProfileResult が直前に置いた候補（その対局の最高打点の和了）を
            // ここで初めて確定させる＝段位が動く対局しか通らないので、フリー対局・デバッグ局・
            // 未登録卓は自動的に除外される（デイリースコアと同じ理屈）。
            int best = sameDay ? GetIntLocal(KEY_DAY_BEST, 0) : 0;
            if (pendingBestPoints_ > best)
            {
                PlayerData.SetInt(KEY_DAY_BEST, pendingBestPoints_);
                PlayerData.SetInt(KEY_DAY_BEST_HAN, pendingBestHan_);
                PlayerData.SetInt(KEY_DAY_BEST_FU, pendingBestFu_);
                PlayerData.SetInt(KEY_DAY_BEST_YM, pendingBestYakuman_);
            }
            else if (!sameDay)
            {
                // 日が変わって候補も無い＝前日の記録を持ち越さないよう0で潰す。
                PlayerData.SetInt(KEY_DAY_BEST, 0);
                PlayerData.SetInt(KEY_DAY_BEST_HAN, 0);
                PlayerData.SetInt(KEY_DAY_BEST_FU, 0);
                PlayerData.SetInt(KEY_DAY_BEST_YM, 0);
            }
            ClearDailyBestCandidate();
        }

        // --- 最高和了の候補（対局終了時に RecordProfileResult が置く） ---
        // 「1和了ぶんのデータ」は Mahjong.cs が既に profileHighlight_ として作っているので、
        // それを流用する（Mahjong.cs は無改修）。PlayerData へ書くのは AddDailyRankingResult だけ。
        private int pendingBestPoints_;
        private int pendingBestHan_;
        private int pendingBestFu_;
        private int pendingBestYakuman_;

        private void ClearDailyBestCandidate()
        {
            pendingBestPoints_ = 0;
            pendingBestHan_ = 0;
            pendingBestFu_ = 0;
            pendingBestYakuman_ = 0;
        }

        // 対局終了ごとに必ず呼ぶ（フリー対局でも呼ぶ）。毎回0で潰してから入れ直すので、
        // 「段位戦にならなかった対局の候補」が次の段位戦へ持ち越されることはない。
        private void StashDailyBestCandidate(int cat, string highlight)
        {
            ClearDailyBestCandidate();
            if (cat == CAT_FREE) { return; }

            // highlight の並びは Mahjong.cs 側と同じ [打点, 翻, 符, 役満の重ね, 面前枚数, 和了牌, ...]。
            int[] values = ParseProfileNumbers(highlight);
            if (values.Length < 7 || values[0] <= 0) { return; }
            pendingBestPoints_ = values[0];
            pendingBestHan_ = values[1] > 0 ? values[1] : 0;
            pendingBestFu_ = values[2] > 0 ? values[2] : 0;
            pendingBestYakuman_ = values[3] > 0 ? values[3] : 0;
        }

        // 読み取り（他人の分も読める）。日付照合はランキング表側で1回だけ行う。
        public int GetDailyDayIndexOf(VRCPlayerApi player) { return GetIntOf(player, KEY_DAY_INDEX, 0); }
        public int GetDailyScoreOf(VRCPlayerApi player) { return GetIntOf(player, KEY_DAY_SCORE, 0); }
        public int GetDailyGamesOf(VRCPlayerApi player) { return GetIntOf(player, KEY_DAY_GAMES, 0); }
        public int GetDailyTopOf(VRCPlayerApi player) { return GetIntOf(player, KEY_DAY_TOP, 0); }
        public int GetDailyBestPointsOf(VRCPlayerApi player) { return GetIntOf(player, KEY_DAY_BEST, 0); }
        public int GetDailyBestHanOf(VRCPlayerApi player) { return GetIntOf(player, KEY_DAY_BEST_HAN, 0); }
        public int GetDailyBestFuOf(VRCPlayerApi player) { return GetIntOf(player, KEY_DAY_BEST_FU, 0); }
        public int GetDailyBestYakumanOf(VRCPlayerApi player) { return GetIntOf(player, KEY_DAY_BEST_YM, 0); }

        public int GetDailyDayIndex() { return GetIntLocal(KEY_DAY_INDEX, 0); }
        public int GetDailyScore() { return GetDailyDayIndex() == GetJstDayIndex() ? GetIntLocal(KEY_DAY_SCORE, 0) : 0; }
        public int GetDailyGames() { return GetDailyDayIndex() == GetJstDayIndex() ? GetIntLocal(KEY_DAY_GAMES, 0) : 0; }
        public int GetDailyTop() { return GetDailyDayIndex() == GetJstDayIndex() ? GetIntLocal(KEY_DAY_TOP, 0) : 0; }

        // ============================================================
        // Set（ローカルのみ。復元前のキャラ／能力は保留→OnPlayerRestored で flush）
        // ============================================================

        public void SetChara(int chara)
        {
            chara = SanitizeChara(chara);
            if (!restored_)
            {
                pendingChara_ = chara;
                pendingCharaSet_ = true;
                return;
            }
            PlayerData.SetInt(KEY_CHARA, chara);
            PlayerData.SetInt(KEY_CHARA_SCHEMA, CHARA_SCHEMA_CURRENT);
        }

        public void SetAbilityOn(bool on)
        {
            if (!restored_)
            {
                pendingAbilityOn_ = on;
                pendingAbilitySet_ = true;
                return;
            }
            PlayerData.SetInt(KEY_ABILITY_ON, on ? 1 : 0);
        }

        // 和了演出コスメの選択。上限はここでは持たない（コスメ数は
        // MahjongAgariEffect 側のカタログが正で、卓側が範囲外を0へ丸める）。
        // 保存値そのものを潰さないことで、コスメ追加前の環境で選んだIDが
        // コスメ実装後にそのまま復活する。
        public void SetAgariFx(int fxId)
        {
            fxId = SanitizeAgariFx(fxId);
            if (!restored_)
            {
                pendingAgariFx_ = fxId;
                pendingAgariFxSet_ = true;
                return;
            }
            PlayerData.SetInt(KEY_AGARI_FX, fxId);
        }

        // 負値だけ弾く（Udonで負のintを(uint)化すると停止するため、番兵は0）。
        private int SanitizeAgariFx(int fxId)
        {
            return fxId < 0 ? 0 : fxId;
        }

        // 現行範囲外のキャラIDを CHARA_VOL に丸める。
        private int SanitizeChara(int chara)
        {
            if (chara < 0 || chara >= NUM_CHARAS) { return CHARA_VOL; }
            return chara;
        }

        // スキーマ導入前のID 2・3は旧オオカミ／旧クマの保存値なので、新キャラへ自動変換しない。
        private int SanitizeStoredChara(int chara, int schema)
        {
            if (chara >= CHARA_TSUMUNTEMA && schema != CHARA_SCHEMA_CURRENT) { return CHARA_VOL; }
            return SanitizeChara(chara);
        }

        // 自画像キャラが解禁されているか（鋭爪以上）。ロック中はカードを押しても選べない。
        public bool IsSelfieUnlocked()
        {
            // テスト用の素通し。カード選択・ロック暗幕・案内の出し分けは
            // すべてこの1メソッドを見ているので、ここだけで全部が外れる。
            if (debugUnlockSelfie) { return true; }

            return GetMajorRankIndex() >= CHARA_SELFIE_UNLOCK_MAJOR_RANK;
        }

        // 解禁に必要な段位の表示名（ロック文言に使う）。Lvは各大段位の先頭。
        public string GetSelfieUnlockRankName()
        {
            return GetRankDisplayNameForLv(CHARA_SELFIE_UNLOCK_MAJOR_RANK * 3);
        }

        // 立直BGMの曲が装備できるか（鋭爪★1以上）。
        // ロックするのは「曲を装備する」ことだけ＝装飾品のタブも試聴も解禁前から使える。
        // 判定するのは自分の段位のみ。他人の曲が鳴るかは本人のクライアントが
        // 装備した時点で通した結果（PlayerData）なので、卓側では段位を見ない
        //（見てしまうと降格した人の曲だけが無音になり、画面と音が食い違う）。
        public bool IsRiichiBgmUnlocked()
        {
            if (debugUnlockRiichiBgm) { return true; }
            return GetRankLv() >= RIICHI_BGM_UNLOCK_LV;
        }

        // 解禁に必要な段位の表示名（例：鋭爪★1 / Keen Claw★1）。
        public string GetRiichiBgmUnlockRankName()
        {
            return GetRankDisplayNameForLv(RIICHI_BGM_UNLOCK_LV);
        }

        public void SetRankPt(int pt)
        {
            if (!restored_) { return; }
            int lv = GetRankLv();
            PlayerData.SetInt(KEY_RANK_PT, SanitizeRankPtForLv(lv, pt));
            PlayerData.SetInt(KEY_RANK_SCHEMA, RANK_SCHEMA_CURRENT);
            NotifyRankDisplayChanged();
        }

        public void SetRankLv(int lv)
        {
            if (!restored_) { return; }
            lv = SanitizeRankLv(lv);
            PlayerData.SetInt(KEY_RANK_LV, lv);
            PlayerData.SetInt(KEY_RANK_PT,
                              SanitizeRankPtForLv(lv, GetIntLocal(KEY_RANK_PT, GetInitialRankPtForLv(lv))));
            PlayerData.SetInt(KEY_TENJU_ORB_X10,
                              SanitizeTenjuOrbX10(GetIntLocal(KEY_TENJU_ORB_X10, TENJU_ORB_INITIAL_X10)));
            PlayerData.SetInt(KEY_RANK_SCHEMA, RANK_SCHEMA_CURRENT);
            NotifyRankDisplayChanged();
        }

        // ===== 途中退出のペナルティ（段位戦のみ・フリー対局では呼ばない）=====
        // 通常段位 … 段位ポイントを ABANDON_PENALTY_PT 減らす。0未満なら既存の降段規則
        //             （CanDemoteFromRankLv → 1段下の初期pt）に合わせる。降段できない
        //             段位は0止まり。
        // 天獣段位 … 天珠を1つ（x10 で ABANDON_PENALTY_ORB_X10）減らす。0未満なら
        //             1段下げて初期天珠、天獣最下位なら Lv14 へ落とす。
        // どちらも CalculateRankedGameResult と同じ規則を踏襲すること（別規則にすると
        // 「対局で下がるときと途中退出で下がるときで結果が違う」になる）。
        public const int ABANDON_PENALTY_PT = 100;
        public const int ABANDON_PENALTY_ORB_X10 = 10;

        public bool ApplyAbandonPenalty()
        {
            if (!restored_) { return false; }

            int lv = SanitizeRankLv(GetRankLv());
            int pt = SanitizeRankPtForLv(lv, GetRankPt());
            int orb = SanitizeTenjuOrbX10(GetTenjuOrbX10());

            if (lv >= RANK_TENJU_FIRST_LV)
            {
                orb -= ABANDON_PENALTY_ORB_X10;
                if (orb < 0)
                {
                    if (lv > RANK_TENJU_FIRST_LV)
                    {
                        --lv;
                        orb = TENJU_ORB_INITIAL_X10;
                    }
                    else
                    {
                        lv = 14;
                        pt = GetInitialRankPtForLv(14);
                        orb = TENJU_ORB_INITIAL_X10;
                    }
                }
            }
            else
            {
                pt -= ABANDON_PENALTY_PT;
                if (pt < 0)
                {
                    if (CanDemoteFromRankLv(lv))
                    {
                        --lv;
                        pt = GetInitialRankPtForLv(lv);
                    }
                    else
                    {
                        pt = 0;
                    }
                }
            }

            lv = SanitizeRankLv(lv);
            PlayerData.SetInt(KEY_RANK_LV, lv);
            PlayerData.SetInt(KEY_RANK_PT, SanitizeRankPtForLv(lv, pt));
            PlayerData.SetInt(KEY_TENJU_ORB_X10, SanitizeTenjuOrbX10(orb));
            PlayerData.SetInt(KEY_RANK_SCHEMA, RANK_SCHEMA_CURRENT);

            NotifyRankDisplayChanged();
            return true;
        }

        // 途中退出でこれから失う量の表示用（確認ダイアログに出す）。
        // 天獣段位は天珠、それ以外は段位ポイント。
        public bool IsTenjuRank()
        {
            return SanitizeRankLv(GetRankLv()) >= RANK_TENJU_FIRST_LV;
        }

        public void SetTenjuOrbX10(int orbX10)
        {
            if (!restored_) { return; }
            PlayerData.SetInt(KEY_TENJU_ORB_X10, SanitizeTenjuOrbX10(orbX10));
            PlayerData.SetInt(KEY_RANK_SCHEMA, RANK_SCHEMA_CURRENT);
            NotifyRankDisplayChanged();
        }

        // 運営モニターから対象本人のローカルクライアントだけが呼ぶ一括補正。
        // 復元前は一切書かず、段位4キーと監査情報を同じ呼び出し内で確定してから1回だけUIへ通知する。
        public bool ApplyAdminRankCorrection(int lv, int pt, int orbX10, string operatorName)
        {
            if (!restored_) { return false; }

            lv = SanitizeRankLv(lv);
            pt = SanitizeRankPtForLv(lv, pt);
            orbX10 = SanitizeTenjuOrbX10(orbX10);
            if (operatorName == null) { operatorName = ""; }

            PlayerData.SetInt(KEY_RANK_LV, lv);
            PlayerData.SetInt(KEY_RANK_PT, pt);
            PlayerData.SetInt(KEY_TENJU_ORB_X10, orbX10);
            PlayerData.SetInt(KEY_RANK_SCHEMA, RANK_SCHEMA_CURRENT);

            int revision = GetIntLocal(KEY_RANK_ADMIN_REV, 0);
            if (revision <= 0 || revision >= 2147483647) { revision = 1; }
            else { ++revision; }
            PlayerData.SetInt(KEY_RANK_ADMIN_REV, revision);
            PlayerData.SetString(KEY_RANK_ADMIN_BY, operatorName);

            NotifyRankDisplayChanged();
            return true;
        }

        // 新しい段位戦半荘をローカルで識別するtoken。Mahjongの同期payloadには載せない。
        public int BeginRankedHanchan()
        {
            if (rankedMatchToken_ >= 2147483647) { rankedMatchToken_ = 1; }
            else
            {
                ++rankedMatchToken_;
                if (rankedMatchToken_ <= 0) { rankedMatchToken_ = 1; }
            }
            rankResultAccepted_ = false;
            return rankedMatchToken_;
        }

        // 半荘の結果反映（既存呼び出し互換のラッパ）。
        public bool ApplyRankedHanchanResult(bool isSanma, int place, int finalPoints,
                                             int startRankLv, int startValue, bool allTenju,
                                             int matchToken, bool startReady)
        {
            return ApplyRankedGameResult(isSanma, false, place, finalPoints,
                                         startRankLv, startValue, allTenju, matchToken, startReady);
        }

        // 同じ対局のRESULTはtokenごとに1回だけ受け付ける。復元前なら最大1件を保留する。
        // isTonpu=true（東風戦）は順位点・天珠が半分になる。
        public bool ApplyRankedGameResult(bool isSanma, bool isTonpu, int place, int finalPoints,
                                          int startRankLv, int startValue, bool allTenju,
                                          int matchToken, bool startReady)
        {
            int maxPlace = isSanma ? 3 : 4;
            if (place < 1 || place > maxPlace) { return false; }
            if (matchToken <= 0 || matchToken != rankedMatchToken_ || rankResultAccepted_)
            {
                return false;
            }

            // PlayerData書き込みより先に受付済みにし、再同期中の再入を防ぐ。
            rankResultAccepted_ = true;
            if (!restored_)
            {
                if (pendingRankResult_) { return false; }
                pendingRankResult_ = true;
                pendingRankIsSanma_ = isSanma;
                pendingRankIsTonpu_ = isTonpu;
                pendingRankPlace_ = place;
                pendingRankFinalPoints_ = finalPoints;
                pendingRankStartLv_ = startRankLv;
                pendingRankStartValue_ = startValue;
                pendingRankAllTenju_ = allTenju;
                pendingRankStartReady_ = startReady;
                return true;
            }

            ApplyRankedGameResultNow(isSanma, isTonpu, place, finalPoints,
                                     startRankLv, startValue, allTenju, startReady);
            return true;
        }

        private void FlushPendingRankResult()
        {
            if (!pendingRankResult_) { return; }

            // 適用前にフラグを落とす。途中でイベントが再入しても二重適用しない。
            bool isSanma = pendingRankIsSanma_;
            bool isTonpu = pendingRankIsTonpu_;
            int place = pendingRankPlace_;
            int finalPoints = pendingRankFinalPoints_;
            int startLv = pendingRankStartLv_;
            int startValue = pendingRankStartValue_;
            bool allTenju = pendingRankAllTenju_;
            bool startReady = pendingRankStartReady_;
            pendingRankResult_ = false;

            ApplyRankedGameResultNow(isSanma, isTonpu, place, finalPoints,
                                     startLv, startValue, allTenju, startReady);
        }

        private void ApplyRankedGameResultNow(bool isSanma, bool isTonpu, int place, int finalPoints,
                                              int startRankLv, int startValue, bool allTenju,
                                              bool startReady)
        {
            if (!restored_) { return; }

            // ENTRY時にPlayerDataが未復元だった場合だけ、復元された対局前保存値を使う。
            if (!startReady)
            {
                startRankLv = GetRankLv();
                startValue = startRankLv >= RANK_TENJU_FIRST_LV ? GetTenjuOrbX10() : GetRankPt();
            }

            int[] result = CalculateRankedGameResult(isSanma, isTonpu, place, finalPoints,
                                                     startRankLv, startValue, allTenju);
            if (result == null || result.Length < RANK_RESULT_SIZE) { return; }

            int nextLv = SanitizeRankLv(result[RANK_RESULT_LV]);
            int nextPt = SanitizeRankPtForLv(nextLv, result[RANK_RESULT_PT]);
            int nextOrb = SanitizeTenjuOrbX10(result[RANK_RESULT_ORB_X10]);

            PlayerData.SetInt(KEY_RANK_LV, nextLv);
            PlayerData.SetInt(KEY_RANK_PT, nextPt);
            PlayerData.SetInt(KEY_TENJU_ORB_X10, nextOrb);
            PlayerData.SetInt(KEY_RANK_SCHEMA, RANK_SCHEMA_CURRENT);

            // 段位戦の通算対局数もここで1対局1回だけ加算する（デイリーと同じ理由で、
            // 段位が動かない対局はこのメソッドまで来ない＝除外処理を書かなくてよい）。
            PlayerData.SetInt(KEY_RANKED_PLAY, GetRankedPlayCount() + 1);

            // インスタンス内ランキング（デイリー）もここで1対局1回だけ加算する。
            // 段位が動かない対局（フリー・デバッグ局・未登録卓）はこのメソッドまで来ない。
            AddDailyRankingResult(isSanma, isTonpu, place, finalPoints);

            NotifyRankDisplayChanged();
        }

        // MainMenuと同じGameObject上の既存Controllerだけをイベント時に更新する。
        // Updateポーリングや新しいシーン参照は増やさない。
        private void NotifyRankDisplayChanged()
        {
            var menu = GetComponent<MainMenuController>();
            if (menu != null) { menu.RefreshRankDisplay(); }
        }

        public void SetPlayCount(bool isSanma, int value)
        {
            if (!restored_) { return; }
            PlayerData.SetInt(PlayKey(isSanma), value);
        }

        public void SetWinCount(bool isSanma, int value)
        {
            if (!restored_) { return; }
            PlayerData.SetInt(WinKey(isSanma), value);
        }

        public void SetAgariCount(bool isSanma, int value)
        {
            if (!restored_) { return; }
            PlayerData.SetInt(AgariKey(isSanma), value);
        }

        public void SetHojuCount(bool isSanma, int value)
        {
            if (!restored_) { return; }
            PlayerData.SetInt(HojuKey(isSanma), value);
        }

        public void SetRecent(string csv)
        {
            if (!restored_) { return; }
            if (csv == null) { csv = ""; }
            PlayerData.SetString(KEY_RECENT, csv);
        }

        // ============================================================
        // 統合メソッド
        // ============================================================

        // 半荘1回分の結果を反映する（段位戦用の互換ラッパ。isSanma=三麻なら true）。
        public void AddGameResult(bool isSanma, int place, int agariCount, int hojuCount)
        {
            AddGameResultCat(CatOf(isSanma), place, agariCount, hojuCount);
        }

        // 半荘1回分の結果をカテゴリ指定で反映する。
        //   cat        : CAT_YONMA / CAT_SANMA / CAT_FREE
        //   place      : 最終着順（1〜4）
        //   agariCount : この半荘での和了回数
        //   hojuCount  : この半荘での放銃回数
        // play/win/agari/hoju を加算し、カテゴリ別の直近10戦CSVの末尾に着順を追加する。
        public void AddGameResultCat(int cat, int place, int agariCount, int hojuCount)
        {
            AddGameResultCatWithHands(cat, place, agariCount, hojuCount, 0);
        }

        // 総局数を含む現行版。累計回数は従来キーへ加算し、率の分子・分母は
        // v2キーだけで同じ計測期間をそろえる。
        // Profile v3: last 100 completed games. Each row is place + nine counters.
        // Separate keys keep legacy placement history and v2 totals intact.
        private string ProfileKey(int cat) { return "kmj_profile3_" + cat; }
        private string HighlightKey(int cat) { return "kmj_highlight3_" + cat; }

        // イベントルールの段位戦で出た最高打点は別枠に持つ。
        //
        // 【なぜ分けるのか】
        //   赤ドラ全赤・ローカル役ありの卓は打点が出やすい。標準ルールの記録と同じ枠へ
        //   入れると、一度イベントで出した打点で枠が埋まり、以後の通常戦の記録が
        //   ずっと更新されなくなる（比べられる相手が違う記録になる）。
        //
        // 【フリー対局も分ける】（2026-09-09〜）
        //   青天井などの特殊ルールの一発が通常枠を永久に占領するため、フリー対局も
        //   特殊ルールの部屋（IsEventRuleGameTable）で打った和了はイベント枠へ入れる。
        //   保存（RecordProfileResult）と取得（GetProfileHighlightEvent）は必ず同じ振り分けにすること
        //   （以前は取得側だけ CAT_FREE を空扱いにしていて、保存した記録が見えなかった）。
        //
        // 【容量】
        //   SelfiePortraitStore が戦績用に 16,384 バイトを予約している。増えるのは
        //   四麻・三麻・フリーの3本ぶん（1件あたり数十バイト）なので、予約の中に収まる。
        private string HighlightEventKey(int cat) { return "kmj_highlightev3_" + cat; }

        public string GetProfileHighlight(int cat) { return GetStringLocal(HighlightKey(cat), ""); }

        public string GetProfileHighlightEvent(int cat)
        {
            return GetStringLocal(HighlightEventKey(cat), "");
        }

        // イベント枠に記録があるか。プロフィールの切替ボタンの出し入れに使う。
        public bool HasProfileHighlightEvent(int cat)
        {
            int[] values = ParseProfileNumbers(GetProfileHighlightEvent(cat));
            return values.Length >= 7 && values[0] > 0;
        }

        public int[] ParseProfileNumbers(string csv)
        {
            if (string.IsNullOrEmpty(csv)) { return new int[0]; }
            string[] parts = csv.Split(',');
            if (parts.Length > 128) { return new int[0]; }
            int[] values = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                int value;
                if (!int.TryParse(parts[i], out value)) { return new int[0]; }
                values[i] = value;
            }
            return values;
        }

        // [games, hands, riichi wins, open wins, dama wins, deal-ins, win points, win turns,
        //  tsumo wins, luck han, yakuman count, wins that carry luck data]
        // 行は「順位 + counters」の CSV。運の2項目を足す前に書いた行は 10 列しか無いので
        // 長さを固定で見ない（固定で見ると過去100戦が丸ごと落ちる）。
        // 運の分母だけは別枠[11]に積む：旧行の和了を分母に入れると
        // ログが一周するまで運だけが不当に低く出る。
        public int[] GetProfileTotals(int cat)
        {
            int[] totals = new int[12];
            string csv = GetStringLocal(ProfileKey(cat), "");
            if (string.IsNullOrEmpty(csv)) { return totals; }
            string[] rows = csv.Split(';');
            int start = Mathf.Max(0, rows.Length - 100);
            for (int i = start; i < rows.Length; i++)
            {
                int[] row = ParseProfileNumbers(rows[i]);
                if ((row.Length != 10 && row.Length != 12) ||
                    row[0] < 1 || row[0] > row[1] || row[1] < 3 || row[1] > 4) { continue; }
                totals[0]++;
                for (int j = 2; j < row.Length; j++) { totals[j - 1] += Mathf.Max(0, row[j]); }
                if (row.Length == 12)
                {
                    totals[11] += Mathf.Max(0, row[3]) + Mathf.Max(0, row[4]) + Mathf.Max(0, row[5]);
                }
            }
            return totals;
        }

        // eventRules … 標準ルールから動いた設定（イベントの段位戦・特殊ルールの
        //               フリー対局）で打った対局か。true ならハイライトはイベント枠へ入れる。
        public void RecordProfileResult(int cat, int place, int[] counters, string highlight, bool eventRules)
        {
            if (!restored_ || cat < 0 || cat >= NUM_CATS || place < 1 || place > 4) { return; }

            // 「本日の最高和了」の候補を先に置く。実際に PlayerData へ書くかどうかは
            // AddDailyRankingResult（＝段位が動く対局だけが通る）が決める。
            StashDailyBestCandidate(cat, highlight);

            // 局を観測できていない対局（counters が揃わない）でも、順位と対局数だけは
            // 従来どおり残す。ここで丸ごと return すると戦績から1戦黙って消える。
            if (counters == null || counters.Length != 11 ||
                counters[0] < 3 || counters[0] > 4 || place > counters[0])
            {
                AddGameResultCatWithHands(cat, place, 0, 0, 0);
                return;
            }

            int wins = counters[2] + counters[3] + counters[4];
            AddGameResultCatWithHands(cat, place, wins, counters[5], counters[1]);
            // 通算の和了種別。和了グラフはここを分子・分母に使う。
            // counters が揃わない対局（上の早期 return 側）は内訳が無いので積まない。
            for (int type = 0; type < 3; ++type)
            {
                int add = Mathf.Max(0, counters[2 + type]);
                if (add <= 0) { continue; }
                string winTypeKey = WinTypeKeyCat(cat, type);
                PlayerData.SetInt(winTypeKey, GetIntLocal(winTypeKey, 0) + add);
            }
            string old = GetStringLocal(ProfileKey(cat), "");
            string[] rows = string.IsNullOrEmpty(old) ? new string[0] : old.Split(';');
            string csv = "";
            for (int i = Mathf.Max(0, rows.Length - 99); i < rows.Length; i++)
            {
                if (csv.Length > 0) { csv += ";"; }
                csv += rows[i];
            }
            if (csv.Length > 0) { csv += ";"; }
            csv += place.ToString();
            for (int i = 0; i < counters.Length; i++) { csv += "," + Mathf.Max(0, counters[i]); }
            PlayerData.SetString(ProfileKey(cat), csv);

            // 特殊ルールで打った和了は別枠へ。フリー対局（CAT_FREE）も分ける：
            // 青天井の一発が通常枠を永久に占領してしまうため。
            bool toEvent = eventRules;
            string key = toEvent ? HighlightEventKey(cat) : HighlightKey(cat);
            int[] candidate = ParseProfileNumbers(highlight);
            int[] previous = ParseProfileNumbers(GetStringLocal(key, ""));
            if (candidate.Length >= 7 && candidate[0] > 0 &&
                (previous.Length < 7 || candidate[0] > previous[0]))
            {
                PlayerData.SetString(key, highlight);
            }
        }

        public void AddGameResultCatWithHands(int cat, int place, int agariCount, int hojuCount, int handCount)
        {
            if (!restored_) { return; }
            if (cat < 0 || cat >= NUM_CATS) { return; }
            if (place < 1 || place > 4) { return; }
            if (agariCount < 0) { agariCount = 0; }
            if (hojuCount < 0) { hojuCount = 0; }
            if (handCount < 0) { handCount = 0; }

            PlayerData.SetInt(PlayKeyCat(cat), GetPlayCountCat(cat) + 1);
            if (place == 1)
            {
                PlayerData.SetInt(WinKeyCat(cat), GetWinCountCat(cat) + 1);
            }
            else
            {
                // 通算の着順内訳。順位グラフはここを分子に使う。
                string placeKey = PlaceKeyCat(cat, place);
                PlayerData.SetInt(placeKey, GetIntLocal(placeKey, 0) + 1);
            }
            PlayerData.SetInt(AgariKeyCat(cat), GetAgariCountCat(cat) + agariCount);
            PlayerData.SetInt(HojuKeyCat(cat), GetHojuCountCat(cat) + hojuCount);

            if (handCount > 0)
            {
                PlayerData.SetInt(HandV2KeyCat(cat), GetHandCountV2Cat(cat) + handCount);
                PlayerData.SetInt(AgariV2KeyCat(cat), GetAgariCountV2Cat(cat) + agariCount);
                PlayerData.SetInt(HojuV2KeyCat(cat), GetHojuCountV2Cat(cat) + hojuCount);
            }

            // 直近10戦（カテゴリ別）：末尾に追加し、10件を超えた分は先頭から捨てる。
            var old = GetRecentPlacesCat(cat);
            int total = old.Length + 1;
            int start = total > RECENT_MAX ? total - RECENT_MAX : 0;

            string csv = "";
            for (int i = start; i < old.Length; ++i)
            {
                if (csv.Length > 0) { csv += ","; }
                csv += old[i].ToString();
            }
            if (csv.Length > 0) { csv += ","; }
            csv += place.ToString();

            PlayerData.SetString(RecentKeyCat(cat), csv);
        }

        // 直近10戦（四麻段位戦）。既存呼び出し互換のためのラッパ。
        public int[] GetRecentPlaces()
        {
            return GetRecentPlacesCat(CAT_YONMA);
        }

        // カテゴリ別の直近CSVをパースして着順配列（最大10要素・古い順）を返す。
        // パースに失敗した要素はスキップする。
        public int[] GetRecentPlacesCat(int cat)
        {
            string csv = GetRecentCat(cat);
            if (csv == null || csv.Length == 0) { return new int[0]; }

            string[] parts = csv.Split(',');
            int[] tmp = new int[parts.Length];
            int count = 0;

            for (int i = 0; i < parts.Length; ++i)
            {
                int v;
                if (int.TryParse(parts[i], out v))
                {
                    tmp[count] = v;
                    ++count;
                }
            }

            int start = count > RECENT_MAX ? count - RECENT_MAX : 0;
            int len = count - start;
            var result = new int[len];
            for (int i = 0; i < len; ++i)
            {
                result[i] = tmp[start + i];
            }
            return result;
        }
    }
}
