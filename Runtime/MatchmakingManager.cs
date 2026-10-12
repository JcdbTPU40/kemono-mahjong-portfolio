using System;
using UdonSharp;
using UnityEngine;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    // マッチング状態の集約（インスタンス共有・Manual同期）
    //
    // ■ 役割（実装③時点）
    //   1) 頭上ステータス（四麻/三麻待機中・プレイヤー募集中）の同期保持（②から）。
    //      OverheadNameplateManager がこれを読み頭上表示する。
    //   2) 段位戦キュー本体（③。2026-08-18に東風戦を追加し4列化）：
    //      - 待機列は「四麻半荘／四麻東風／三麻半荘／三麻東風」の4本。列は混ぜない。
    //      - 待機列は先着順（slotSeq）。四麻=4人／三麻=3人ちょうどで成立。CPU補充なし。
    //      - 卓割当＝空卓を tables 配列順で優先。全卓使用中は待機継続＋満卓フラグ。
    //      - 成立時：当事者のみローカルで「お待たせしました」ボイス＋卓誘導ビーコン表示。
    //      - 着席タイムアウト60秒：期限までに全員着席しなければ成立解除→
    //        着席していたメンバーだけ待機列へ戻す（未着席者は列から外れる）。
    //      - 待機中/成立中のインスタンス退出は自動除外（OnPlayerLeft＋Sweep）。
    //   フリー対局戦（部屋・CPU追加・設定画面）は実装④。
    //
    // ■ 設計思想（docs/マッチング・フリー対局戦_確定仕様.md B・E）
    //   - 状態は本Behaviour 1個（Manual同期）に集約。表示側（メニュー/ネームプレート）は
    //     完全ローカルでこれを読むだけ。
    //   - VRChatはサーバー権威なし。各プレイヤーはNetworkCallableで要求だけを送る。
    //     同期配列の書込・成立裁定・卓割当は固定Manager Ownerが検証後に一括更新する。
    //   - requestSeqで重複・遅延要求を拒否し、stateRevisionで同期スナップショットを識別する。
    //   - Manager Owner離脱時は OnOwnershipTransferred → Sweep で状態復元。
    //
    // ■ 卓カリングとの整合（重要）
    //   TableProximityCuller により遠い卓は GameObject ごと非アクティブになる。
    //   非アクティブな UdonBehaviour へのメソッド呼び出し（SendCustomEvent経由）は実行されない。
    //   そのため：
    //   - Owner の空卓判定は Mahjong の public フィールド（gameState / entry）を直接読む
    //     （フィールド読みは非アクティブでも可。ただしカリング中は同期が止まり古い値の
    //     可能性がある＝誤割当はあり得る。60秒タイムアウトで自動復旧する。要テスト）。
    //   - 卓への操作（SetNumPlayers / _OnResync）は卓に接近している当事者クライアントが行う
    //     （接近＝カリング解除済み＝メソッドが届く＆同期が新しい）。
    //   - 着席検知も本人が近接時に seatPlayerId（public フィールド）を読んで自己申告する。
    //
    // ■ Udon地雷メモ
    //   - 負のintを(uint)化するとUdon停止。空きスロットの番兵は playerId=0
    //     （VRChatのplayerIdは1始まり）。卓インデックスの-1はローカル変数のみ。
    //   - サーバー時刻は GetServerTimeInMilliseconds（int・全クライアント共通）。
    //     ラップ対策で期限判定は差分 (deadline - now < 0) で行う。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class MatchmakingManager : UdonSharpBehaviour
    {
        // --- ステータスコード ---
        // 段位戦の待機は「人数（四麻/三麻）×局数（半荘/東風）」の4列に完全分割する。
        // 既存の1・2は半荘の値として据え置き（保存データも同期状態も持たないが、
        // 旧ビルドのクライアントが混在した瞬間に四麻半荘へ落ちる方が安全）。
        public const int STATUS_IDLE = 0;                // 表示なし
        public const int STATUS_WAIT_YONMA = 1;          // 四麻 半荘戦 待機中
        public const int STATUS_WAIT_SANMA = 2;          // 三麻 半荘戦 待機中
        public const int STATUS_FREE_RECRUIT = 3;        // プレイヤー募集中（フリー対局戦＝④）
        public const int STATUS_WAIT_YONMA_TONPU = 4;    // 四麻 東風戦 待機中
        public const int STATUS_WAIT_SANMA_TONPU = 5;    // 三麻 東風戦 待機中

        // 段位戦の待機列の開閉ビット（スタッフパネルが操作する）。
        // 「三麻 半荘戦だけのイベント」のように列を絞るための仕組みで、
        // 立っている列だけが MainMenu に出て、待機要求も受け付ける。
        // フリー対局はレートが動かないので対象外（列としても持たない）。
        public const int COLUMN_BIT_YONMA_HAN = 1;
        public const int COLUMN_BIT_YONMA_TON = 2;
        public const int COLUMN_BIT_SANMA_HAN = 4;
        public const int COLUMN_BIT_SANMA_TON = 8;
        public const int COLUMN_MASK_ALL = 15;

        // 段位戦のイベントで開放してよいルール（スタッフパネル）。
        //
        // 【なぜ赤ドラや全赤を段位戦へ入れてよいのか】
        //   段位pt = 素点(持ち点差/1000) + 順位点。素点は卓の中でゼロサム（持ち点の総和は
        //   保存される）なので、打点が膨らんでも卓全体で配られるptの総量は増えない。
        //   増えるのは分散だけで、期待値では誰も得をしない。順位点はルールに依存しない。
        //
        // 【青天井と一局戦だけは絶対に入れない】
        //   ・青天井は1億点クランプ＝素点pt換算で10万pt。標準の1半荘は±50pt程度なので桁が
        //     3〜4つ違い、1回の和了で初獣から幻獣まで昇段が連鎖する（段位が壊れる）。
        //   ・一局戦は所要時間1/8で順位点が同額になり、東風を半分にした設計が崩れる。
        //   このマスクに AOTENJOU 系のビットは含めない。加えて、そもそもスタッフパネルに
        //   青天井の同期変数を持たせていない＝入れる場所が無い（二重の歯止め）。
        //   局数も同様で、段位戦の局数は待機列（半荘/東風）から来るだけで触れない。
        public const int EVENT_ALLOWED_MASK = RULE_FLAG_LOCAL_YAKU | RULE_FLAG_OPEN_REACH
            | RULE_FLAG_NO_TSUMO_LOSS | RULE_FLAG_NO_BUST | RULE_FLAG_RED_DORA_MASK;

        // インスタンス最大人数（最大50人運用。安全側に確保）。
        private const int MAX_TRACKED = 50;
        // 卓の最大数（同期配列は固定長のためコンパイル時に確保）。
        private const int MAX_TABLES = 8;
        // 着席タイムアウト（ms）。
        private const int SEAT_TIMEOUT_MS = 60000;
        // 成立から着席受付を開くまでの準備時間（ms）。
        // この3秒で「卓が起きて同期が届く」「人数モード(三麻/四麻)が合う」
        // 「メンバーが実在する」を揃えてから座らせる。0にすると従来動作（準備なし）。

        // 3秒待機後も卓Ownerの準備確認が届かない場合の安全タイムアウト。
        private const int PREPARE_CONFIRM_TIMEOUT_MS = 10000;
        private const int PREPARE_REPORT_RETRY_MS = 1000;
        private const int MATCH_PREPARE_MS = 3000;
        // 準備中〜未着席の当事者だけが回す高速Tick（秒）。
        // 1秒Tickのままだと卓に着いてから人数モードが合うまで最大1秒かかり、
        // その間ENTRYが伏せられたままに見える＝「押せない」バグに見える。
        private const float PREPARE_TICK_SECONDS = 0.25f;
        // フリー戦：全員着席（ClearMatch COMPLETED）後〜ENTRY_COMPLETED 前の保護期限。
        // 期限切れ＝参加せず放置 → CleanupFinishedFreeTables が freeTableMask を解放してよい。
        // 猶予が長すぎる場合の実害は放置卓の再利用が遅れることだけ。
        // 短すぎる場合はフリー戦が段位カテゴリに記録されランクPtが汚染され、
        // しかもエラーもログも出ず誰も気づかない。
        // 失敗コストが非対称なので長い側に倒す。
        private const int FREE_ENTRY_GRACE_MS = 300000;
        // 自浄タイムアウト。Manager Owner が「卓に触れずに」詰まりを見つけて片付けるための尺。
        //
        // なぜ要るか：SweepBusyTables も CleanupFinishedFreeTables も
        // tb.gameObject.activeInHierarchy を要求する＝Manager Owner の手元で卓が
        // アクティブであることが前提になっている。Owner は普段 MainMenu にいて卓は
        // 近接カリングで寝ているので、この2つは事実上走らない。結果、分類bitや busy が
        // 立ちっぱなしの卓はスタッフを呼ぶまで永久に使えない（2026-09-13 の永久フリーズ）。
        //
        // 下の2つは同期済みの状態とサーバー時刻だけで判定するので、卓が寝ていても動く。
        // 卓の実体を戻す経路は従来どおり tableForceResetMask を卓Owner が消化する。

        // 卓Owner のハートビートが途絶えたとみなすまで（ms）。
        // 卓側は Mahjong.TABLE_BUSY_HEARTBEAT_MS（60秒）ごとに再申告するので、5拍ぶんの無音。
        // 対局中の卓を潰す唯一の条件なので、経過時間ではなく無音だけを根拠にする
        // （半荘は1時間を超えることがあり、尺で測ると健全な対局を潰す）。
        private const int TABLE_HEARTBEAT_STALE_MS = 300000;
        // 誰も座っていないのに分類bitだけ残っている卓を解放するまで（ms）。
        // 正規の猶予 FREE_ENTRY_GRACE_MS（5分）より必ず長くすること。倍の10分を取る。
        private const int TABLE_FLAG_STALE_MS = 600000;        // 強制リセットの旗そのものを諦めるまで（ms）。
        //
        // 旗を落とせるのは卓Owner の完了申告（TABLE_ACTION_FORCE_RESET_DONE）だけで、
        // 申告が構造的に届かない状況（tables[] に穴がある／卓の Initialize が失敗する等）に
        // 落ちると、その卓は IsTableFree=false のままインスタンス終了まで戻らない。
        // それはこの自浄機構が潰そうとしている永久フリーズそのものなので、最後の保険を置く。
        //
        // 卓Owner は 500ms ごとにポーリングし 3秒ごとに再申告する＝応答できるなら数秒で済む。
        // 3分は「応答できない」と断じるのに十分な余裕。落とす条件に busy と matchMode を
        // 足してあるので、旗の消化中に卓が生き返った場合（＝busy を再申告してきた場合）は
        // ここで解放されない。
        private const int TABLE_FORCE_RESET_STALE_MS = 180000;

        // 満卓フラグのビット（段位戦は待機列と1対1）。
        private const int FULL_BIT_YONMA = 1;
        private const int FULL_BIT_SANMA = 2;
        private const int FULL_BIT_FREE = 4;
        private const int FULL_BIT_YONMA_TONPU = 8;
        private const int FULL_BIT_SANMA_TONPU = 16;

        // --- 局数ルール（フリー対局戦・仕様D／段位戦は半荘・東風の2種） ---
        // 0=半荘 をデフォルトにする（同期配列のゼロ初期値＝従来動作の半荘、が安全）。
        public const int RULE_LEN_HANCHAN = 0;
        public const int RULE_LEN_TONPU = 1;
        public const int RULE_LEN_ONE = 2;

        // フリー対局の長考時間プリセット。追加時間は卓同期の1バイト上限に合わせて最大255秒。
        public const int TURN_TIMER_8_30 = 0;
        public const int TURN_TIMER_15_60 = 1;
        public const int TURN_TIMER_60_255 = 2;

        // 卓ごとの特殊ルール（bit フラグ）。0=標準＝従来動作。
        // 段位戦は常に0（ここに乗せるのはフリー対局戦だけ）。
        public const int RULE_FLAG_NONE = 0;
        public const int RULE_FLAG_AOTENJOU = 1;              // 青天井・現行式（N倍満）
        public const int RULE_FLAG_LOCAL_YAKU = 2;            // ローカル役あり
        public const int RULE_FLAG_OPEN_REACH = 4;            // オープン立直あり
        // ビットが立っている＝既定（従来動作）から外す、という向きで揃える。
        public const int RULE_FLAG_NO_TSUMO_LOSS = 8;         // ツモ損なし（三麻のみ効く）
        public const int RULE_FLAG_NO_BUST = 16;              // トビなし
        public const int RULE_FLAG_AOTENJOU_TRADITIONAL = 32; // 青天井・従来式（符×指数）
        // 赤ドラ設定は既定（3枚設定）を0とし、既存卓との互換性を保つ。
        // 残り3プリセットをbit6-7で表す。
        public const int RULE_FLAG_RED_DORA_NONE = 64;
        public const int RULE_FLAG_RED_DORA_FOUR = 128;
        public const int RULE_FLAG_RED_DORA_ALL = 192;
        public const int RULE_FLAG_RED_DORA_MASK = 192;
        // CPUの強さ。ビットが立っていない＝「簡単」＝従来のCPU（既存卓との互換）。
        public const int RULE_FLAG_CPU_STRONG = 256;

        public const int RED_DORA_MODE_NONE = 0;
        public const int RED_DORA_MODE_THREE = 1;
        public const int RED_DORA_MODE_FOUR = 2;
        public const int RED_DORA_MODE_ALL = 3;

        public const int AOTENJOU_MODE_NONE = 0;
        public const int AOTENJOU_MODE_CURRENT = 1;
        public const int AOTENJOU_MODE_TRADITIONAL = 2;

        // --- フリー部屋の状態（インスタンスで同時1部屋だけ・仕様D） ---
        public const int FREE_NONE = 0;     // 部屋なし
        public const int FREE_SETUP = 1;    // 代表が設定画面を開いている（他人のボタンはグレー）
        public const int FREE_RECRUIT = 2;  // 募集中（参加可）

        // フリー部屋の設定中AFK解除（無操作60秒・仕様D）。
        private const int FREE_AFK_TIMEOUT_MS = 60000;
        // 募集中のまま放置された部屋を畳むまで（ms）。設定中の60秒と違い、
        // 募集中は「人が来るのを待っている」正常な状態なので長く取る。
        // 部屋は卓を予約したままなので、放置されると卓が1つ死ぬ＝無期限にはできない。
        private const int FREE_RECRUIT_AFK_TIMEOUT_MS = 900000;
        // 成立解除理由（matchClearReason）。
        private const int CLEAR_NONE = 0;
        private const int CLEAR_COMPLETED = 1; // 全員着席で正常完了
        private const int CLEAR_DISSOLVED = 2;

        // Manager Ownerへ送る要求種別。要求側は同期状態を直接変更しない。
        private const int FREE_ACTION_OPEN = 1;
        private const int FREE_ACTION_TOGGLE_MODE = 2;
        private const int FREE_ACTION_TOGGLE_ABILITY = 3;
        private const int FREE_ACTION_CYCLE_LENGTH = 4;
        private const int FREE_ACTION_CONFIRM = 5;
        private const int FREE_ACTION_ADD_CPU = 6;
        private const int FREE_ACTION_REMOVE_CPU = 7;
        private const int FREE_ACTION_CANCEL = 8;
        private const int FREE_ACTION_JOIN = 9;
        private const int FREE_ACTION_LEAVE = 10;
        private const int FREE_ACTION_START = 11;
        private const int FREE_ACTION_TOGGLE_AOTENJOU = 12;
        private const int FREE_ACTION_TOGGLE_LOCALYAKU = 13;
        private const int FREE_ACTION_TOGGLE_OPENREACH = 14;
        private const int FREE_ACTION_TOGGLE_TSUMOLOSS = 15;
        private const int FREE_ACTION_TOGGLE_BUST = 16;
        private const int FREE_ACTION_CYCLE_TIMER = 17;
        private const int FREE_ACTION_CYCLE_RED_DORA = 18;
        // ここを増やしたら ApplyFreeActionRequestAuthoritative の範囲チェックの上限も上げる。
        private const int FREE_ACTION_CYCLE_CPU_LEVEL = 19;
        // 前回と同じ設定・同じ顔ぶれで部屋を建て直す（設定画面を飛ばして募集中から始まる）。
        private const int FREE_ACTION_REMATCH = 20;

        private const int TABLE_ACTION_FREE_FINISHED = 1;
        private const int TABLE_ACTION_RANKED_FINISHED = 2;
        private const int TABLE_ACTION_RANKED_INVALIDATED = 3;
        private const int TABLE_ACTION_RANKED_ABORTED = 4;
        private const int TABLE_ACTION_FREE_CPU_COMPLETE = 5;
        private const int TABLE_ACTION_TABLE_IDLE = 6;
        // スタッフの強制リセットを卓Ownerが実行し終えた申告。
        private const int TABLE_ACTION_FORCE_RESET_DONE = 7;

        // --- 待機列（並列配列：slotPlayerId[i]==0 は空きスロット） ---
        [UdonSynced] private int[] slotPlayerId = new int[MAX_TRACKED];
        [UdonSynced] private int[] slotStatus = new int[MAX_TRACKED];
        // 先着順の単調増加シーケンス（成立時は小さい順に選ぶ）。
        [UdonSynced] private int[] slotSeq = new int[MAX_TRACKED];
        [UdonSynced] private int seqCounter = 0;
        // 要求の重複・遅延到着をOwner移譲後も拒否するための送信者別シーケンス。
        [UdonSynced] private int[] managerRequestPlayerIds = new int[MAX_TRACKED];
        [UdonSynced] private int[] managerRequestSeqs = new int[MAX_TRACKED];
        // 全同期更新の単調増加番号。UI/ログで同一スナップショットを識別する。
        [UdonSynced] private int stateRevision = 0;

        // --- 成立中マッチ（卓ごと。matchMode[t]==0 が「マッチなし」） ---
        [UdonSynced] private int[] matchMode = new int[MAX_TABLES];              // 0=なし / 3=三麻 / 4=四麻
        [UdonSynced] private int[] matchId = new int[MAX_TABLES];                // 成立世代（0=なし）
        [UdonSynced] private int nextMatchId = 0;
        [UdonSynced] private int[] matchDeadlineMs = new int[MAX_TABLES];        // 着席期限（サーバー時刻ms）
        // 着席受付が開くサーバー時刻ms。これを過ぎるまでが準備フェーズ（ENTRY不可）。

        // 3秒経過に加え、割当卓のOwnerが初期状態・人数同期を確認した卓だけ着席を開く。
        [UdonSynced] private int matchPreparedMask = 0;
        [UdonSynced] private int[] matchReadyAtMs = new int[MAX_TABLES];
        [UdonSynced] private int[] matchPlayerIds = new int[MAX_TABLES * 4];     // メンバー（空き=0）
        [UdonSynced] private bool[] matchSeated = new bool[MAX_TABLES * 4];      // 本人申告の着席フラグ
        // 直前のマッチがどう終わったか（完了/解除）。解除時のみ着席済みメンバーが卓をOnResyncする。
        [UdonSynced] private int[] matchClearReason = new int[MAX_TABLES];
        // 満卓で成立保留中のモード（bit0=四麻 bit1=三麻 bit2=フリー開始）。メニューの「全卓使用中」表示用。
        [UdonSynced] private int tablesFullMask = 0;
        // 対局中の卓（手動で始めた卓を含む）。
        //
        // 卓の gameState は Update 内の Deserialize で復号しているため、カリングで寝ている
        // 卓の gameState は「寝る直前の値」で止まる。マッチングのOwnerは普段メインメニュー側に
        // いるので全卓が寝て見え、手動で始まった対局に気づけず「対局中の卓を空卓と誤認して
        // 割り当てる」ことがあった（段位戦・フリー戦は各Maskで除外済みなので手動卓だけの穴）。
        // 卓の近くにいるクライアント（＝卓のOwner）が遷移のたびにここへ書き、
        // 常時アクティブなこのマネージャ上に「起きている事実」を残す。
        [UdonSynced] private int busyTableMask = 0;
        // 卓Owner 申告の「対局が実際に始まっている（配牌以降）」。busy と組で読む（busy が落ちた卓では無意味）。
        // busy は開始前の着席でも立つので、成立中のメンバー離脱を「開始前の退出＝解散」と
        // 「対局中の退出＝卓がCPU代打で続行＝成立完了」に分けるのに使う。
        [UdonSynced] private int startedTableMask = 0;
        // 成立中にメンバーがインスタンスを抜けた卓（matchPlayerIds の該当席は0にされている）。
        // 同期する＝Owner が替わっても「0は抜けた人」と分かり、残った人だけで成立完了にしない。ClearMatch で落とす。
        [UdonSynced] private int matchLostMask = 0;
        // 開始前の離脱を見つけた時刻（Owner ローカル）。開始の申告が届くのを少し待ってから解散する。
        private int[] lostSinceMs_ = new int[MAX_TABLES];
        // 配牌直後の離脱で、卓Owner の「開始済み」申告（再送1秒間隔）がまだ届いていないことがある。その猶予。
        private const int LOST_START_GRACE_MS = 5000;
        // TryFormMatch の作業用（スロットごとの「開始前の席が残っている」判定）。毎回 new しない。
        private bool[] slotHeld_ = new bool[MAX_TRACKED];
        // 使用中の卓のうち、終局の結果表示中（Mahjong の RESULT）のもの。卓Owner が busy と一緒に申告する。
        // MainMenu が「対局はもう終わった」と見分けて、途中退出（段位の減点）を出さないために使う。
        // busy が落ちた卓では意味を持たない（読む側は必ず busy と組み合わせる）。
        [UdonSynced] private int finishedTableMask = 0;
        // 卓ごとの「人間が座っている席の playerId」（空席・CPU・CPU代打は 0）。
        // 卓Owner が busy 報告と一緒に申告する。
        //
        // Mahjong.seatPlayerId は [NonSerialized] で LastInitialize でしか実体化せず、
        // 値も Update 内の Deserialize でしか更新されない＝カリングで寝ている卓では
        // null か「寝る直前の値」。Manager Owner は普段メインメニュー側にいて全卓が
        // 寝て見えるため、「いま誰がどの卓で打っているか」を知る手段はこの同期値しかない。
        [UdonSynced] private int[] tableSeatIds = new int[MAX_TABLES * 4];

        // 途中退出の申告（卓ごとに席のビット）。MainMenu で「途中退出」を押した本人が
        // Manager Owner へ申告し、卓Owner がポーリングして CPU 代打へ切り替える。
        // 本人の卓は近接カリングで寝ているので卓へ直接イベントを送れない＝ここを中継にする。
        // 卓が空席へ戻ったら ClearSeatReport と同じタイミングで畳む。
        [UdonSynced] private int[] tableAbandonMask = new int[MAX_TABLES];

        // 観戦モニターの役満通知（卓Owner の申告）。卓は寝ていると外から手牌を読めないので、
        // Owner が判定した「4席の役満確定／チャンス」をここへ置いて全員へ配る。
        //  Alert[t] = 4席のレベル(2bit×4: 0なし/1チャンス/2確定)｜親<<8｜三麻<<10｜局ID<<11
        //  Yaku[t]  = 4席の役ID(7bit×4)。0 は数え役満の印
        // 卓が空く／使われ始めるたびに 0 へ戻す（前の対局の値で通知しないため）。
        [UdonSynced] private int[] tableYakumanAlert = new int[MAX_TABLES];
        [UdonSynced] private int[] tableYakumanYaku = new int[MAX_TABLES];

        // スタッフパネルからの「卓の強制リセット」要求（bit=卓index）。
        // Manager 側の卓状態はここを立てる時点で畳んであるが、卓の実体（gameState・entry・
        // 手牌）を戻せるのは卓Ownerだけなので、消化は卓側のポーリングに任せる。
        // 途中退出申告と同じ中継方式（スタッフは卓から遠く、卓は寝ている）。
        [UdonSynced] private int tableForceResetMask = 0;
        [UdonSynced] private int[] tableForceResetGeneration = new int[MAX_TABLES];

        // 強制リセットの理由と実行者（卓index別）。卓の上と全員のログに出すために同期する。
        // 従来は「スタッフの要求で〜」という1行が卓Ownerのログに出るだけで、スタッフ操作・
        // マスター操作・自浄のどれか、どの卓かも分からなかった（2026-09-26 に卓が消えた件で詰んだ）。
        // 下位4bit=理由（FORCE_RESET_REASON_*）／それより上=実行者の playerId（自浄は0）。
        [UdonSynced] private int[] tableForceResetInfo = new int[MAX_TABLES];
        [UdonSynced] private int[] tableForceResetAtMs = new int[MAX_TABLES];
        public const int FORCE_RESET_REASON_NONE = 0;
        public const int FORCE_RESET_REASON_STAFF = 1;    // 名簿のスタッフ
        public const int FORCE_RESET_REASON_MASTER = 2;   // スタッフ不在時のインスタンスマスター
        public const int FORCE_RESET_REASON_SILENT = 3;   // 自浄：卓Ownerの応答が5分途絶
        public const int FORCE_RESET_REASON_STALE = 4;    // 自浄：空卓に予約だけ10分残留
        // 卓の上に理由を出しておく時間（ms）。
        public const int FORCE_RESET_NOTICE_MS = 180000;
        // 観測済みの世代（ローカル）。変化を見て全員のログへ1行出すため。
        private int[] seenForceResetGeneration_;
        private VRCPlayerApi[] staffScanBuf_;

        // --- 自浄タイムアウト用。Manager Owner のローカル観測なので同期しない ---
        // 読むのは Owner 自身だけなので、同期させても送信量を食うだけで誰の得にもならない。
        // Owner が移譲されると 0（未観測）に戻るが、0 は「今この瞬間に観測した」として
        // 撒き直すので、引き継いだ直後に健全な卓を潰すことはない。
        [System.NonSerialized] private int[] tableHeartbeatMs_ = new int[MAX_TABLES];
        [System.NonSerialized] private int[] tableStaleSinceMs_ = new int[MAX_TABLES];        // 強制リセットの旗を立てた時刻（Manager Owner のローカル観測）。0=未観測。
        [System.NonSerialized] private int[] tableForceResetSinceMs_ = new int[MAX_TABLES];

        // --- フリー部屋（部屋＝卓。卓ごとに1部屋・代表=freeMemberIds[t*4]） ---
        //
        // 2026-09-13 に「ワールドで同時1部屋」から卓ごとへ広げた。
        // 部屋を開いた時点でその卓を予約する（freeTableMask を立てる）ので、
        // 段位戦の空卓割当（IsTableFree）とは何も足さずに排他になる。
        //
        // ルール設定は専用の同期変数を持たない。既存の卓ごと配列
        // （tableRuleLength / tableRuleFlags / tableTurnTimerMode / tableAbilityOn / tableCpuCount）へ
        // 設定中から直接書き、成立時はそのまま使う。
        // 旧15変数を素直に8倍すると +600バイト/送信になるが、この形なら +約190バイトで済む。
        // 代わりに「設定画面の表示」は tableRuleFlags からビットを解いて作る（下の Get...At 群）。
        // --- 卓に直接座れるか（③の封鎖スイッチ） ---
        //
        // false＝どの戦種にも登録されていない「通常卓」には座れない＝着席は MainMenu 一本。
        // true ＝従来どおり卓の ENTRY / CPU から直接始められる。
        //
        // ベータで「導線が重い」と分かったら、ここを true に戻すだけで旧動作へ帰れる。
        // 同期はしない（全クライアントで同じ値＝Inspector の設定がそのまま効く）。
        // スタッフ／インスタンスマスターはこの値に関わらず手動で座れる
        // （CPU相手に1人で検証する手段が消えると、機能追加のたびに人集めになる）。
        [Header("卓に直接座れるか（false＝着席はMainMenu経由のみ）")]
        [Tooltip("ONにすると卓のENTRY/CPUボタンが誰にでも出る（2026-09-13より前の動作）。")]
        public bool allowManualTableEntry = false;
        [Tooltip("スタッフ名簿の人だけは封鎖中でも卓に直接座れる（CPU相手の単独検証用）。"
            + "封鎖後の見え方をスタッフ自身が確かめたいときはOFFにする。")]
        public bool allowStaffManualTableEntry = true;

        [UdonSynced] private int[] freeState = new int[MAX_TABLES];
        [UdonSynced] private int[] freeMode = new int[MAX_TABLES];          // 3=三麻 / 4=四麻（0=部屋なし）
        [UdonSynced] private int[] freeMemberIds = new int[MAX_TABLES * 4]; // 人間メンバー（[t*4]=代表・空き=0）
        [UdonSynced] private int[] freeLastActionMs = new int[MAX_TABLES];  // AFK判定用（操作で更新）

        // 「同じ面子でもう1局」用のスナップショット。成立の瞬間に焼く。
        // ClearMatch が matchPlayerIds をゼロにするので、成立後は顔ぶれを復元する術がない。
        // 設定は packed に詰めて、卓あたり int 2本＋メンバー4本に収める。
        //   packed: mode(0-3bit) / ruleLength(4-7) / turnTimer(8-11) / cpuCount(12-15)
        //           / abilityOn(16) / valid(20)
        [UdonSynced] private int[] freeRematchPacked = new int[MAX_TABLES];
        [UdonSynced] private int[] freeRematchFlags = new int[MAX_TABLES];        // tableRuleFlags の写し
        [UdonSynced] private int[] freeRematchMembers = new int[MAX_TABLES * 4];  // 人間メンバー（空き=0）

        // --- 卓ごとの対局ルール（マッチ成立時に設定。卓の Mahjong が参照する） ---
        // フリー戦としてプレイ中の卓（bitmask）。対局が終わり卓が空へ戻ったのを
        // 「ローカルでアクティブな卓のフレッシュな値」で確認できた時にOwnerが消す。
        [UdonSynced] private int freeTableMask = 0;
        // MainMenuの通常マッチングから成立した段位戦卓（最大8卓のbitmask）。
        // matchModeは全員着席後に0へ戻るため、RESULTまで残るこの値を段位変動の正とする。
        [UdonSynced] private int rankedTableMask = 0;
        // 段位戦成立後にforced wallが使われた卓。非アクティブ中のnetwork event取りこぼしに備え、
        // RESULTまで同期状態として保持する（通常の段位戦bitは結果通知のため残す）。
        [UdonSynced] private int invalidRankedTableMask = 0;
        [UdonSynced] private int[] tableRuleLength = new int[MAX_TABLES]; // ゼロ初期値=半荘
        // 卓ごとの特殊ルール（RULE_FLAG_*のbit和）。ゼロ初期値=標準。
        // tableRuleLength と同じ寿命で、成立時に書いて後片付けで0へ戻す。
        [UdonSynced] private int[] tableRuleFlags = new int[MAX_TABLES];
        // フリー対局成立時の長考時間プリセット。0=8+30（段位戦・手動卓の既定値）。
        [UdonSynced] private int[] tableTurnTimerMode = new int[MAX_TABLES];
        [UdonSynced] private bool[] tableAbilityOn = new bool[MAX_TABLES];
        // フリー戦のCPU自動着席（代表が卓に接近した時に空席へCPUエントリーする）。
        [UdonSynced] private int[] tableCpuCount = new int[MAX_TABLES];
        [UdonSynced] private int[] tableCpuHostId = new int[MAX_TABLES];

        // 開いている段位戦の待機列（COLUMN_BIT_* のbit和）。既定は全開。
        // private なので publicVariables には載らず、ここの初期値がそのままヒープに入る
        // （Wire での代入は不要）。インスタンス内でのみ有効＝再訪時は全開に戻る。
        [UdonSynced] private int enabledColumnMask = COLUMN_MASK_ALL;

        // 段位戦のイベントルール（スタッフパネル）。フリー対局側と同じ意味・同じ既定値で持つ。
        // 青天井と局数は意図的に持たない（EVENT_ALLOWED_MASK のコメント参照）。
        [UdonSynced] private int eventRedDoraMode = RED_DORA_MODE_THREE;
        [UdonSynced] private bool eventLocalYaku = false;
        [UdonSynced] private bool eventOpenReach = false;
        [UdonSynced] private bool eventTsumoLoss = true;   // true = ツモ損あり（既定）
        [UdonSynced] private bool eventBust = true;        // true = トビあり（既定）
        [UdonSynced] private bool eventAbilityOn = true;
        [UdonSynced] private int eventTurnTimerMode = TURN_TIMER_8_30;

        // 頭上のスタッフバナー（ネームプレートの「スタッフ」章）を世界で出すか。
        // 章を描くのは各クライアントのローカル処理なので、ここは「今日は出す／隠す」を
        // 全員で1つに揃えるためのスイッチにすぎない。
        // **既定は非表示**。案内が要るときだけスタッフパネルから出す運用にする。
        // private なので publicVariables には載らず、この初期値がそのままヒープに入る
        // （Wire での代入は不要）。インスタンス内でのみ有効＝立て直せば非表示に戻る。
        [UdonSynced] private bool staffBadgeVisible = false;

        // 観戦ルームの防音（ルームの声を外へ漏らさない）を効かせるか。スタッフパネルの「表示」ページで切り替える。
        // 声の振り分けは各クライアントの TableVoiceRouter がローカルに行うので、ここは全員で揃えるスイッチだけ。
        // **既定は ON**。private なのでこの初期値がそのままヒープに入る。インスタンス内でのみ有効＝立て直せば ON に戻る。
        [UdonSynced] private bool soundproofOn = true;

        // ============================================================
        // 空卓を埋める順番（スタッフパネル）
        //
        // 8卓ぶんの「卓インデックス」を3bitずつ1つのintへ詰める。
        //   slot k の卓 = (tableOrderPacked >> (k*3)) & 7
        // 配列を同期すると要素ごとに差分が乗るので、順番のような小さな状態は
        // int 1本のほうが安い（24bit＝正の範囲。Udonは負のintのuint化で停止する）。
        // 既定は 0,1,2,...,7 ＝ tables 配列の並び順そのまま＝卓(1)→(8)。
        // tables の並びは Wire Matchmaking が卓名の番号順に固定するので、
        // 「卓インデックス i」の卓番号は i+1 で読める。
        // ============================================================
        public const int TABLE_ORDER_DEFAULT =
            (0 << 0) | (1 << 3) | (2 << 6) | (3 << 9)
            | (4 << 12) | (5 << 15) | (6 << 18) | (7 << 21);

        [UdonSynced] private int tableOrderPacked = TABLE_ORDER_DEFAULT;

        [Header("参照（Wire Matchmaking で自動割り当て）")]
        [Tooltip("段位戦マッチング対象の卓（最大8）。Wire Matchmaking が指定優先順で割り当てる。")]
        public Mahjong[] tables;

        // VVMW の位置の観戦モニター。卓のカリングがここから引いて「配信中の卓」を起こす
        // （カリング8台へ個別に配線しないため。BuildSpectatorMonitor が入れる）。
        public SpectatorMonitor spectatorMonitor;
        [Tooltip("卓誘導ビーコン（光柱）。tables と同数・同順。当事者のみローカル表示。")]
        public GameObject[] tableBeacons;
        [Tooltip("卓誘導の矢印（足元から自分の卓まで床に並ぶ）。未設定なら光柱だけになる。")]
        public MatchGuideArrow guideArrow;
        [Tooltip("メインメニューの位置。卓へ来たのに何もできない人をここへ誘導する。未割当なら誘導しない。")]
        public Transform mainMenuGuideTarget;
        [Tooltip("卓に「来た」とみなす距離(m)。この中に入ると案内の矢印が出る。")]
        public float tableApproachDistance = 3.0f;
        [Tooltip("成立ボイスの再生元（2D・ローカル再生）。")]
        public AudioSource matchVoiceSource;
        [Tooltip("マッチングSEの再生元（2D・ローカル再生）。音量パネルの「演出音」側。" +
                 "成立ボイス(matchVoiceSource)はボイス側なので、分けて持つ。")]
        public AudioSource matchSfxSource;
        [Tooltip("スタッフ判定の表示名allowlist。段位戦の列開閉をOwner側で検証するのに使う。未設定なら誰も列を操作できない。")]
        public StaffOnlyArea staffArea;

        [Header("成立ボイス（「お待たせしました」wav 48kHz mono・Vorbis。未設定なら鳴らさない）")]
        public AudioClip matchVoiceClip;

        [Header("マッチングSE（未設定なら鳴らさない）")]
        [Tooltip("準備フェーズの1秒ごとの拍（3・2・1）。")]
        public AudioClip matchCountdownClip;
        [Tooltip("カウントダウンが終わって卓が決まったお知らせ。")]
        public AudioClip matchReadyClip;
        [Range(0.0f, 1.0f)] public float matchCountdownVolume = 0.12f;
        [Range(0.0f, 1.0f)] public float matchReadyVolume = 0.12f;

        [Header("表示調整")]
        [Tooltip("卓に「接近した」とみなす距離(m)。ビーコン消灯・着席検知・卓モード設定に使う。TableProximityCuller.showDistance と揃える。")]
        public float beaconHideDistance = 1.8f;

        // --- ローカル状態（同期しない） ---
        private VRCPlayerApi localPlayer_;
        private int numTables_;
        // 本人が最後に要求した状態。同期反映を観測するまで同じrequestSeqで再送する。
        private int nextLocalRequestSeq_ = 0;
        private int desiredStatus_ = STATUS_IDLE;
        private int desiredStatusRequestSeq_ = 0;
        private int desiredRetryLeft_ = 0;
        // 着席申告の保留（卓＋matchIdで同一成立だけを受け付ける）。
        private const int DESIRED_RETRY_MAX = 3;
        private int desiredSeatTable_ = -1;
        private int desiredSeatMatchId_ = 0;
        private int desiredSeatRequestSeq_ = 0;
        private int desiredSeatRetryLeft_ = 0;
        // フリー部屋オープン／開始の保留（同期増やさずローカルのみ）。
        private const int FREE_DESIRE_NONE = 0;
        private const int FREE_DESIRE_OPEN = 1;
        private const int FREE_DESIRE_START = 2;
        private int desiredFreeAction_ = FREE_DESIRE_NONE;
        private int desiredFreeRequestSeq_ = 0;
        private int desiredFreeRetryLeft_ = 0;
        // 要求中の部屋の卓index。OPEN/START はリトライがあるので、どの卓に対する要求かを覚えておく。
        private int desiredFreeTable_ = -1;
        // 成立検知（ボイス1回再生・解除時の卓掃除アシスト用）。
        private bool wasInMatch_ = false;
        // カウントダウンSEの予約先。予約後に成立が解除された場合、遅延イベントは
        // 止められないので発火側で世代（matchId）を照合して黙らせる。
        private int countdownTable_ = -1;
        private int countdownMatchId_ = 0;
        private int lastMatchTable_ = -1;      // ローカル変数のみ（同期しない）
        private bool lastLocalSeated_ = false;
        // PickFreeTable 用の再利用バッファ（フィールド初期化子で確保。Start 前到達でも null にしない）。
        private int[] freshTableBuf_ = new int[MAX_TABLES];
        private int[] staleTableBuf_ = new int[MAX_TABLES];
        // 卓準備完了／使用不可のOwner宛て申告を、同期反映まで1秒間隔で再送する。
        private int preparedReportTable_ = -1;
        private int preparedReportMatchId_ = 0;
        private int preparedReportSentAtMs_ = 0;
        private int unusableReportTable_ = -1;
        private int unusableReportMatchId_ = 0;
        private int unusableReportSentAtMs_ = 0;
        // 高速Tickの二重起動よけ。
        private bool prepTickActive_ = false;

        void Start()
        {
            localPlayer_ = Networking.LocalPlayer;

            numTables_ = tables != null ? tables.Length : 0;
            if (numTables_ > MAX_TABLES)
            {
                Debug.LogError("[MatchmakingManager] tables が " + MAX_TABLES + " 卓を超えています。超過分は無視します。");
                numTables_ = MAX_TABLES;
            }

            // 自浄タイムアウトの観測配列。[NonSerialized] はシリアライズ経路に乗らないので、
            // 初期化子だけに頼らずここで必ず確保する。null のままだとスイープが
            // ガードに引っかかって黙って止まる（落ちないので誰も気づけない）。
            if (freeState == null || freeState.Length != MAX_TABLES) { freeState = new int[MAX_TABLES]; }
            if (freeMode == null || freeMode.Length != MAX_TABLES) { freeMode = new int[MAX_TABLES]; }
            if (freeLastActionMs == null || freeLastActionMs.Length != MAX_TABLES)
            {
                freeLastActionMs = new int[MAX_TABLES];
            }
            if (freeMemberIds == null || freeMemberIds.Length != MAX_TABLES * 4)
            {
                freeMemberIds = new int[MAX_TABLES * 4];
            }
            if (freeRematchPacked == null || freeRematchPacked.Length != MAX_TABLES)
            {
                freeRematchPacked = new int[MAX_TABLES];
            }
            if (freeRematchFlags == null || freeRematchFlags.Length != MAX_TABLES)
            {
                freeRematchFlags = new int[MAX_TABLES];
            }
            if (freeRematchMembers == null || freeRematchMembers.Length != MAX_TABLES * 4)
            {
                freeRematchMembers = new int[MAX_TABLES * 4];
            }

            if (tableHeartbeatMs_ == null || tableHeartbeatMs_.Length != MAX_TABLES)
            {
                tableHeartbeatMs_ = new int[MAX_TABLES];
            }
            if (tableStaleSinceMs_ == null || tableStaleSinceMs_.Length != MAX_TABLES)
            {
                tableStaleSinceMs_ = new int[MAX_TABLES];
            }
            if (tableForceResetSinceMs_ == null || tableForceResetSinceMs_.Length != MAX_TABLES)
            {
                tableForceResetSinceMs_ = new int[MAX_TABLES];
            }

            if (tableBeacons != null)
            {
                for (int i = 0; i < tableBeacons.Length; ++i)
                {
                    if (tableBeacons[i] != null) { tableBeacons[i].SetActive(false); }
                }
            }

            SendCustomEventDelayedSeconds(nameof(_Tick), 1.0f);
        }

        // ============================================================
        private int NextLocalRequestSeq()
        {
            if (nextLocalRequestSeq_ >= 2000000000) { nextLocalRequestSeq_ = 1; }
            else { nextLocalRequestSeq_ = nextLocalRequestSeq_ + 1; }
            return nextLocalRequestSeq_;
        }

        private bool AcceptManagerRequestSequence(int playerId, int requestSeq)
        {
            if (playerId <= 0 || requestSeq <= 0) { return false; }
            int free = -1;
            for (int i = 0; i < MAX_TRACKED; ++i)
            {
                if (managerRequestPlayerIds[i] == playerId)
                {
                    if (requestSeq <= managerRequestSeqs[i]) { return false; }
                    managerRequestSeqs[i] = requestSeq;
                    return true;
                }
                if (free < 0 && managerRequestPlayerIds[i] == 0) { free = i; }
            }
            if (free < 0)
            {
                // 満杯：離脱者の残骸を一度だけ掃除して空きを作り直す。
                // ここで false を返すと待機列登録も着席申告も黙って無視される。
                if (SweepManagerRequests())
                {
                    for (int i = 0; i < MAX_TRACKED; ++i)
                    {
                        if (managerRequestPlayerIds[i] != 0) { continue; }
                        managerRequestPlayerIds[i] = playerId;
                        managerRequestSeqs[i] = requestSeq;
                        return true;
                    }
                }
                Debug.LogWarning("[MatchmakingManager] request sequence slot が満杯です。");
                return false;
            }
            managerRequestPlayerIds[free] = playerId;
            managerRequestSeqs[free] = requestSeq;
            return true;
        }

        private bool ClearManagerRequestSequence(int playerId)
        {
            if (playerId <= 0) { return false; }
            for (int i = 0; i < MAX_TRACKED; ++i)
            {
                if (managerRequestPlayerIds[i] != playerId) { continue; }
                managerRequestPlayerIds[i] = 0;
                managerRequestSeqs[i] = 0;
                return true;
            }
            return false;
        }

        private void CommitManagerState()
        {
            if (stateRevision >= 2000000000) { stateRevision = 1; }
            else { stateRevision = stateRevision + 1; }
            RequestSerialization();
        }

        private int IssueMatchId(int tableIndex)
        {
            if (nextMatchId >= 2000000000) { nextMatchId = 1; }
            else { nextMatchId = nextMatchId + 1; }
            matchId[tableIndex] = nextMatchId;
            return nextMatchId;
        }

        private int GetStatusOfId(int playerId)
        {
            if (playerId <= 0) { return STATUS_IDLE; }
            for (int i = 0; i < MAX_TRACKED; ++i)
            {
                if (slotPlayerId[i] == playerId) { return slotStatus[i]; }
            }
            return STATUS_IDLE;
        }

        // どの卓の部屋でもよいので、その人がフリー部屋に属しているか。
        private bool IsIdFreeMember(int playerId)
        {
            return FindFreeRoomOfId(playerId) >= 0;
        }

        // その人が入っている部屋の卓index（-1=どこにも居ない）。
        // 1人が同時に2部屋へ入ることは Owner 側の検証で防いでいるので、最初に見つけた1つでよい。
        private int FindFreeRoomOfId(int playerId)
        {
            if (playerId <= 0 || freeState == null || freeMemberIds == null) { return -1; }
            for (int t = 0; t < numTables_; ++t)
            {
                if (freeState[t] == FREE_NONE) { continue; }
                for (int i = 0; i < 4; ++i)
                {
                    if (freeMemberIds[t * 4 + i] == playerId) { return t; }
                }
            }
            return -1;
        }

        // その人が代表をしている部屋の卓index（-1=代表ではない）。
        private int FindFreeHostRoomOfId(int playerId)
        {
            if (playerId <= 0 || freeState == null || freeMemberIds == null) { return -1; }
            for (int t = 0; t < numTables_; ++t)
            {
                if (freeState[t] == FREE_NONE) { continue; }
                if (freeMemberIds[t * 4] == playerId) { return t; }
            }
            return -1;
        }

        public int GetStateRevision() { return stateRevision; }

        public int GetMatchIdForTable(Mahjong tb)
        {
            int t = IndexOfTable(tb);
            if (t < 0 || matchMode[t] == 0) { return 0; }
            return matchId[t];
        }

        public bool IsTableMatchActive(Mahjong tb)
        {
            int t = IndexOfTable(tb);
            return t >= 0 && matchMode[t] != 0 && matchId[t] != 0;
        }

        // 読み取り（誰でも・毎フレーム呼ばれる想定なので軽量に）
        // ============================================================

        // 指定プレイヤーの現在ステータス。未登録なら STATUS_IDLE。
        public int GetStatusOf(VRCPlayerApi player)
        {
            if (player == null || !Utilities.IsValid(player)) { return STATUS_IDLE; }
            int id = player.playerId;
            for (int i = 0; i < MAX_TRACKED; ++i)
            {
                if (slotPlayerId[i] == id) { return slotStatus[i]; }
            }
            return STATUS_IDLE;
        }

        // ローカルプレイヤーの現在ステータス（UI表示のトグル判定などに）。
        public int GetLocalStatus()
        {
            return GetStatusOf(Networking.LocalPlayer);
        }

        // ステータス要求（列に入る／降りる）がまだ同期に載っていないか。
        // 要求は Manager Owner を往復するので、押した直後は GetLocalStatus() がまだ
        // 変わらない。メニュー側がこの間を「列に入っていない」と誤読してホームへ
        // 戻すと、頭上だけ待機中でメニューはホーム、という食い違いになる。
        public bool IsLocalStatusRequestPending()
        {
            return desiredRetryLeft_ > 0 && GetLocalStatus() != desiredStatus_;
        }

        // 直近に要求したステータス（往復中の待機画面の表示に使う）。
        public int GetLocalDesiredStatus()
        {
            return desiredStatus_;
        }

        // ローカルプレイヤーが成立中マッチのメンバーなら卓インデックス、いなければ -1。
        public int GetLocalMatchTable()
        {
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return -1; }
            int id = lp.playerId;
            for (int t = 0; t < numTables_; ++t)
            {
                if (matchMode[t] == 0) { continue; }
                for (int k = 0; k < matchMode[t]; ++k)
                {
                    if (matchPlayerIds[t * 4 + k] == id) { return t; }
                }
            }
            return -1;
        }

        // 成立中マッチの着席残り秒（メニューのカウントダウン用）。マッチ外なら 0。
        public int GetLocalMatchRemainSeconds()
        {
            int t = GetLocalMatchTable();
            if (t < 0) { return 0; }
            int remainMs = matchDeadlineMs[t] - Networking.GetServerTimeInMilliseconds();
            if (remainMs < 0) { return 0; }
            return remainMs / 1000;
        }

        // 成立直後の準備フェーズ中か（卓の同期・人数モード・メンバー実在を確認している間）。
        // 準備中は着席を受け付けない＝ENTRYボタンも出ない。
        public bool IsMatchPreparing(int t)
        {
            if (t < 0 || t >= numTables_) { return false; }
            if (matchMode[t] == 0) { return false; }
            int bit = 1 << t;
            // 最低3秒のロード待ちと、卓Ownerによる状態・人数同期の確認を両方要求する。
            return matchReadyAtMs[t] - Networking.GetServerTimeInMilliseconds() > 0
                   || (matchPreparedMask & bit) == 0;
        }

        public bool IsMatchPrepared(int t)
        {
            if (t < 0 || t >= numTables_ || matchMode[t] == 0) { return false; }
            return (matchPreparedMask & (1 << t)) != 0;
        }

        public bool IsLocalPlayerAssignedToTable(Mahjong tb)
        {
            int t = IndexOfTable(tb);
            if (t < 0 || matchMode[t] == 0) { return false; }
            var lp = localPlayer_;
            if (lp == null || !Utilities.IsValid(lp)) { lp = Networking.LocalPlayer; }
            if (lp == null || !Utilities.IsValid(lp)) { return false; }
            for (int k = 0; k < matchMode[t]; ++k)
            {
                if (matchPlayerIds[t * 4 + k] == lp.playerId) { return true; }
            }
            return false;
        }

        public bool IsLocalMatchWaitingForTableSync()
        {
            int t = GetLocalMatchTable();
            if (t < 0 || !IsMatchPreparing(t)) { return false; }
            return matchReadyAtMs[t] - Networking.GetServerTimeInMilliseconds() <= 0
                   && !IsMatchPrepared(t);
        }

        public bool IsLocalMatchPreparing()
        {
            return IsMatchPreparing(GetLocalMatchTable());
        }

        // 準備の残り秒（切り上げ）。準備中でなければ 0。
        public int GetLocalMatchPrepareRemainSeconds()
        {
            int t = GetLocalMatchTable();
            if (t < 0) { return 0; }
            int remainMs = matchReadyAtMs[t] - Networking.GetServerTimeInMilliseconds();
            if (remainMs <= 0) { return 0; }
            return (remainMs + 999) / 1000;
        }

        // 成立直後の3秒カウントダウン中か（卓Ownerの確認待ちは含まない）。
        // カウントダウン中は卓へ誘導しない＝矢印も光柱も出さない。
        public bool IsMatchCountdownRunning(int t)
        {
            if (t < 0 || t >= numTables_ || matchMode[t] == 0) { return false; }
            return matchReadyAtMs[t] - Networking.GetServerTimeInMilliseconds() > 0;
        }

        // 成立中マッチの人数（3/4）。マッチが無ければ 0。
        public int GetMatchModeOf(int t)
        {
            if (t < 0 || t >= numTables_) { return 0; }
            return matchMode[t];
        }

        // 成立中マッチの k 人目の playerId（空き・範囲外は 0）。
        public int GetMatchPlayerIdAt(int t, int k)
        {
            if (t < 0 || t >= numTables_ || k < 0 || k >= 4) { return 0; }
            return matchPlayerIds[t * 4 + k];
        }

        // 成立中マッチに含まれる CPU の数（フリー対局のみ。段位戦は常に 0）。
        // 成立時に matchPlayerIds は人間だけを前詰めで入れ、CPU席は 0 のまま残る。
        // 待機画面の顔ぶれで「（募集中...）」と区別するために必要。
        public int GetTableCpuCount(int t)
        {
            if (t < 0 || t >= numTables_) { return 0; }
            return tableCpuCount[t];
        }

        // 成立中マッチのCPUが「強い」設定か（待機・成立画面の顔ぶれ表示用）。
        // 部屋側の freeCpuStrong は成立と同時に ClearFreeRoom がリセットするので読めない。
        // 成立時に卓へ焼いた tableRuleFlags（同期済み）が正。
        public bool GetTableCpuStrongAt(int t)
        {
            if (t < 0 || t >= numTables_) { return false; }
            return (tableRuleFlags[t] & RULE_FLAG_CPU_STRONG) != 0;
        }

        // 指定の待機列に並んでいる人数（待機画面の「N / M 人」表示用）。
        public int GetWaitingCount(int status)
        {
            if (!IsRankedWaitStatus(status)) { return 0; }
            int n = 0;
            for (int i = 0; i < MAX_TRACKED; ++i)
            {
                if (slotPlayerId[i] != 0 && slotStatus[i] == status) { ++n; }
            }
            return n;
        }

        // 「卓へ案内中」の人数。成立した人は待機列（slot）から抜けるので GetWaitingCount には出ない。
        // 卓側に残っている matchMode / tableRuleLength から列を逆算して数える＝同期変数は増やさない。
        //
        // 全員が着席し終えると ClearMatch が matchMode を 0 に戻すので、そこで自動的に 0 へ落ちる。
        public int GetGuidingCount(int status)
        {
            if (!IsRankedWaitStatus(status)) { return 0; }
            int n = 0;
            for (int t = 0; t < numTables_; ++t)
            {
                if (matchMode[t] == 0) { continue; }
                if ((rankedTableMask & (1 << t)) == 0) { continue; }
                if (GetRankedWaitStatus(matchMode[t], tableRuleLength[t]) != status) { continue; }
                for (int k = 0; k < 4; ++k)
                {
                    if (matchPlayerIds[t * 4 + k] > 0) { ++n; }
                }
            }
            return n;
        }

        // フリー対局で卓へ案内中の人数。段位戦と違い列が1本しかないので status を取らない。
        public int GetFreeGuidingCount()
        {
            int n = 0;
            for (int t = 0; t < numTables_; ++t)
            {
                if (matchMode[t] == 0) { continue; }
                if ((freeTableMask & (1 << t)) == 0) { continue; }
                for (int k = 0; k < 4; ++k)
                {
                    if (matchPlayerIds[t * 4 + k] > 0) { ++n; }
                }
            }
            return n;
        }

        // ホームのフリー対局バッジ用の集計。部屋＝卓（マルチルーム）になったので、
        // 「自分の部屋」ではなくワールド全体の募集状況を数える。
        // ホームを見ている人はどの部屋にも入っていない＝自分の部屋を見ると必ず0になる。
        public int GetFreeRecruitingCountTotal()
        {
            if (freeState == null) { return 0; }
            int n = 0;
            for (int t = 0; t < numTables_; ++t)
            {
                if (freeState[t] != FREE_RECRUIT) { continue; }
                n += GetFreeMemberCountAt(t);
            }
            return n;
        }

        // 募集中（参加できる）部屋の数。
        public int GetFreeRecruitingRoomCount()
        {
            if (freeState == null) { return 0; }
            int n = 0;
            for (int t = 0; t < numTables_; ++t)
            {
                if (freeState[t] == FREE_RECRUIT) { ++n; }
            }
            return n;
        }

        // 代表が設定画面を開いている（まだ誰も並べない）部屋の数。
        public int GetFreeSetupRoomCount()
        {
            if (freeState == null) { return 0; }
            int n = 0;
            for (int t = 0; t < numTables_; ++t)
            {
                if (freeState[t] == FREE_SETUP) { ++n; }
            }
            return n;
        }

        // 指定の待機列の index 番目（先着順）の playerId。いなければ 0。
        // 成立時の選抜も seq の小さい順なので、ここに並ぶ順＝実際に当たる順になる。
        public int GetWaitingPlayerIdAt(int status, int index)
        {
            if (index < 0 || !IsRankedWaitStatus(status)) { return 0; }
            for (int i = 0; i < MAX_TRACKED; ++i)
            {
                if (slotPlayerId[i] == 0 || slotStatus[i] != status) { continue; }

                // 自分より先に並んだ人数＝先着順の順位。seq が同値ならスロット順で決める。
                int rank = 0;
                for (int j = 0; j < MAX_TRACKED; ++j)
                {
                    if (j == i || slotPlayerId[j] == 0 || slotStatus[j] != status) { continue; }
                    if (slotSeq[j] < slotSeq[i] || (slotSeq[j] == slotSeq[i] && j < i)) { ++rank; }
                }
                if (rank == index) { return slotPlayerId[i]; }
            }
            return 0;
        }

        // 指定モードの待機が「全卓使用中」で保留されているか（メニューの満卓表示用）。
        public bool IsWaitBlockedByFullTables(int status)
        {
            int bit = RankedFullBitOf(status);
            if (bit == 0) { return false; }
            return (tablesFullMask & bit) != 0;
        }

        // ============================================================
        // 段位戦の待機ステータス ⇔（人数・局数）の相互変換
        //   待機列は「四麻半荘 / 四麻東風 / 三麻半荘 / 三麻東風」の4本。
        //   ステータス1個に人数と局数の両方を持たせ、同期配列を増やさない
        //   （MAX_TRACKED=50 の並列配列を1本足すと帯域が無駄に増えるため）。
        // ============================================================

        public bool IsRankedWaitStatus(int status)
        {
            return status == STATUS_WAIT_YONMA || status == STATUS_WAIT_SANMA ||
                   status == STATUS_WAIT_YONMA_TONPU || status == STATUS_WAIT_SANMA_TONPU;
        }

        // 待機ステータス→卓人数（4/3）。段位戦の待機でなければ 0。
        public int GetWaitModeOf(int status)
        {
            if (status == STATUS_WAIT_YONMA || status == STATUS_WAIT_YONMA_TONPU) { return 4; }
            if (status == STATUS_WAIT_SANMA || status == STATUS_WAIT_SANMA_TONPU) { return 3; }
            return 0;
        }

        // 待機ステータス→局数ルール。段位戦の待機でなければ半荘（安全側）。
        public int GetWaitRuleLengthOf(int status)
        {
            if (status == STATUS_WAIT_YONMA_TONPU || status == STATUS_WAIT_SANMA_TONPU)
            {
                return RULE_LEN_TONPU;
            }
            return RULE_LEN_HANCHAN;
        }

        // （人数・局数）→待機ステータス。想定外の組み合わせは 0（IDLE）を返す。
        public int GetRankedWaitStatus(int mode, int ruleLength)
        {
            if (ruleLength == RULE_LEN_TONPU)
            {
                if (mode == 4) { return STATUS_WAIT_YONMA_TONPU; }
                if (mode == 3) { return STATUS_WAIT_SANMA_TONPU; }
                return STATUS_IDLE;
            }
            if (mode == 4) { return STATUS_WAIT_YONMA; }
            if (mode == 3) { return STATUS_WAIT_SANMA; }
            return STATUS_IDLE;
        }

        // ============================================================
        // 段位戦の待機列の開閉（スタッフパネル）
        //   ・UIを隠すだけでは改造クライアントが要求を直接投げられるので、
        //     ApplyStatusRequestAuthoritative でも同じ判定を通す（二重ロック）。
        //   ・成立済みのマッチには触らない。閉じるのは「これから並ぶ人」と
        //     「いま並んでいる人」だけで、対局中の卓は最後まで進む。
        // ============================================================

        public int GetEnabledColumnMask() { return enabledColumnMask; }

        // 待機ステータス→列ビット。段位戦の待機でなければ 0。
        public int GetRankedColumnBit(int status)
        {
            if (status == STATUS_WAIT_YONMA) { return COLUMN_BIT_YONMA_HAN; }
            if (status == STATUS_WAIT_YONMA_TONPU) { return COLUMN_BIT_YONMA_TON; }
            if (status == STATUS_WAIT_SANMA) { return COLUMN_BIT_SANMA_HAN; }
            if (status == STATUS_WAIT_SANMA_TONPU) { return COLUMN_BIT_SANMA_TON; }
            return 0;
        }

        // その待機列が開いているか。段位戦の待機ステータス以外は常に false。
        public bool IsRankedColumnEnabled(int status)
        {
            int bit = GetRankedColumnBit(status);
            if (bit == 0) { return false; }
            return (enabledColumnMask & bit) != 0;
        }

        // 表示名allowlistによるスタッフ判定。allowlistの実体は StaffOnlyArea に一本化する
        // （スタッフ追加のたびに複数箇所を直さないため）。
        // これは誤操作防止であって改造クライアントに対する防壁ではない。
        public bool IsStaffPlayer(VRCPlayerApi player)
        {
            if (player == null || !Utilities.IsValid(player)) { return false; }
            if (staffArea == null) { return false; }
            if (staffArea.allowInstanceMaster && player.isMaster) { return true; }
            return staffArea.IsStaffName(player.displayName);
        }

        public bool IsLocalStaff() { return IsStaffPlayer(Networking.LocalPlayer); }

        // 強制リセットだけは、スタッフ名簿に無くてもインスタンスマスターに許す。
        //
        // 名簿は3名しかいない。全員が不在のインスタンスで卓が詰まると、今は誰も直せない。
        // インスタンスマスターは必ず1人存在する（抜ければ次の人へ自動で移る）ので、
        // 「直せる人が居ない」状態が構造的に起きなくなる。
        // 広げるのはこの1機能だけ。イベントルール・卓順・章の表示は名簿のままにする
        // （壊れても巻き戻せる設定と違い、卓のリセットは"詰みからの脱出"にしか使えない）。
        //
        // 【2026-09-27 絞った】グループのパブリックではマスター＝一番古くからいる一般プレイヤー。
        // ワールド設定を開いた人に「卓の強制リセット」が見え、2手で対局中の卓を消せていた。
        //  ・スタッフが1人でもいるインスタンスでは、マスターには入口ごと出さない。
        //  ・マスターが潰せるのは「進行中でない卓」だけ（CanForceResetTableAt）。
        //    対局中の卓が本当に止まった場合は、自浄（応答5分途絶）が拾う。
        public bool CanForceResetTables(VRCPlayerApi player)
        {
            if (player == null || !Utilities.IsValid(player)) { return false; }
            if (IsStaffPlayer(player)) { return true; }
            if (!player.isMaster) { return false; }
            return !IsAnyStaffPresent();
        }

        // この卓を強制リセットしてよいか。最終判定は Owner 側（ApplyForceResetTableAuthoritative）。
        public bool CanForceResetTableAt(VRCPlayerApi player, int t)
        {
            if (!CanForceResetTables(player)) { return false; }
            if (t < 0 || t >= numTables_) { return false; }
            if (IsStaffPlayer(player)) { return true; }
            return !IsTableInLiveUse(t);
        }

        public bool CanLocalPlayerForceResetTableAt(int t)
        {
            return CanForceResetTableAt(Networking.LocalPlayer, t);
        }

        // 名簿のスタッフか（マスター権限の人には制限つきの画面を出すための区別）。
        public bool IsLocalFullForceResetter() { return IsLocalStaff(); }

        // 人が実際に使っている卓か：対局中・着席誘導中・フリーの部屋が開いている。
        // 分類bitだけが残った空卓（予約の残骸）や強制リセット待ちは含めない＝詰みとして潰してよい。
        private bool IsTableInLiveUse(int t)
        {
            if ((busyTableMask & (1 << t)) != 0) { return true; }
            if (matchMode[t] != 0) { return true; }
            return freeState[t] != FREE_NONE;
        }

        // 名簿のスタッフがインスタンスにいるか。
        private bool IsAnyStaffPresent()
        {
            if (staffArea == null) { return false; }
            int n = VRCPlayerApi.GetPlayerCount();
            if (n <= 0) { return false; }
            if (staffScanBuf_ == null || staffScanBuf_.Length < n)
            {
                staffScanBuf_ = new VRCPlayerApi[n < 16 ? 16 : n * 2];
            }
            VRCPlayerApi.GetPlayers(staffScanBuf_);
            for (int i = 0; i < n; ++i)
            {
                var p = staffScanBuf_[i];
                if (p == null || !Utilities.IsValid(p)) { continue; }
                if (staffArea.IsStaffName(p.displayName)) { return true; }
            }
            return false;
        }

        // 封鎖中でも卓に直接座れる人か。
        //
        // 【注意】ここで CanForceResetTables を流用してはいけない。あちらは
        // インスタンスマスターも通す＝1人で入れば必ず自分がマスターなので、
        // ソロでは封鎖がまったく効かなくなる（2026-09-13にその形で一度出して踏んだ）。
        // 強制リセットは「詰みからの脱出」なので必ず誰か1人は実行できる必要があるが、
        // 卓に直接座るのは普通のゲームプレイのバイパスなので、名簿のスタッフだけに限る。
        private bool CanManuallyUseTable(int playerId)
        {
            if (allowManualTableEntry) { return true; }
            if (!allowStaffManualTableEntry) { return false; }
            if (playerId <= 0) { return false; }
            return IsStaffPlayer(VRCPlayerApi.GetPlayerById(playerId));
        }

        public bool CanLocalPlayerForceResetTables()
        {
            return CanForceResetTables(Networking.LocalPlayer);
        }

        // ============================================================
        // 段位戦のイベントルール（スタッフパネル）
        // ============================================================

        public int GetEventRedDoraMode() { return eventRedDoraMode; }
        public bool GetEventLocalYaku() { return eventLocalYaku; }
        public bool GetEventOpenReach() { return eventOpenReach; }
        public bool GetEventTsumoLoss() { return eventTsumoLoss; }
        public bool GetEventBust() { return eventBust; }
        public bool GetEventAbilityOn() { return eventAbilityOn; }
        public int GetEventTurnTimerMode() { return eventTurnTimerMode; }

        // 頭上のスタッフバナーを出すか。OverheadNameplateManager が毎データ更新で読む。
        public bool GetStaffBadgeVisible() { return staffBadgeVisible; }
        public bool GetSoundproofOn() { return soundproofOn; }

        // ============================================================
        // 空卓を埋める順番
        // ============================================================

        public int GetTableOrderPacked() { return tableOrderPacked; }

        // 並びの k 番目に来る卓インデックス。
        public int GetTableAtOrder(int slot)
        {
            if (slot < 0 || slot >= MAX_TABLES) { return -1; }
            return (tableOrderPacked >> (slot * 3)) & 7;
        }

        // 卓インデックスが並びの何番目にいるか（見つからなければ -1）。
        public int GetOrderSlotOfTable(int tableIndex)
        {
            for (int k = 0; k < MAX_TABLES; ++k)
            {
                if (GetTableAtOrder(k) == tableIndex) { return k; }
            }
            return -1;
        }

        // 0..7 がちょうど1回ずつ入っているか。壊れた値を掴むと同じ卓へ2組流し込む。
        public bool IsValidTableOrder(int packed)
        {
            if (packed < 0) { return false; }
            int seen = 0;
            for (int k = 0; k < MAX_TABLES; ++k)
            {
                int v = (packed >> (k * 3)) & 7;
                int bit = 1 << v;
                if ((seen & bit) != 0) { return false; }
                seen |= bit;
            }
            return seen == 255;
        }

        // 並びの slot a と slot b を入れ替えた packed を返す（送信側で作る）。
        public int SwapTableOrder(int packed, int a, int b)
        {
            if (a < 0 || a >= MAX_TABLES || b < 0 || b >= MAX_TABLES || a == b) { return packed; }
            int va = (packed >> (a * 3)) & 7;
            int vb = (packed >> (b * 3)) & 7;
            packed = packed & ~(7 << (a * 3));
            packed = packed | (vb << (a * 3));
            packed = packed & ~(7 << (b * 3));
            packed = packed | (va << (b * 3));
            return packed;
        }

        // 段位戦の卓へ渡すルールビット。フリー対局の組み立てと同じ規則で作る。
        // 最後に許可マスクを掛けているのは、将来ここへ別のビットを足したときに
        // 青天井が段位戦へ紛れ込まないようにするため（フィールドが無いので現状は素通り）。
        public int GetEventRuleFlags()
        {
            int flags = (eventLocalYaku ? RULE_FLAG_LOCAL_YAKU : 0)
                | (eventOpenReach ? RULE_FLAG_OPEN_REACH : 0)
                | (eventTsumoLoss ? 0 : RULE_FLAG_NO_TSUMO_LOSS)
                | (eventBust ? 0 : RULE_FLAG_NO_BUST)
                | (eventRedDoraMode == RED_DORA_MODE_NONE ? RULE_FLAG_RED_DORA_NONE : 0)
                | (eventRedDoraMode == RED_DORA_MODE_FOUR ? RULE_FLAG_RED_DORA_FOUR : 0)
                | (eventRedDoraMode == RED_DORA_MODE_ALL ? RULE_FLAG_RED_DORA_ALL : 0);
            return flags & EVENT_ALLOWED_MASK;
        }

        // 標準ルールから1つでも動いているか。プロフィールのハイライトを
        // 「通常」と「イベント」で分ける判定にも、この結果（＝卓のruleFlagsが非ゼロ）を使う。
        public bool IsEventRulesActive()
        {
            return GetEventRuleFlags() != RULE_FLAG_NONE
                || eventTurnTimerMode != TURN_TIMER_8_30
                || !eventAbilityOn;
        }

        // 待機ステータス→満卓ビット。段位戦の待機でなければ 0。
        private int RankedFullBitOf(int status)
        {
            if (status == STATUS_WAIT_YONMA) { return FULL_BIT_YONMA; }
            if (status == STATUS_WAIT_SANMA) { return FULL_BIT_SANMA; }
            if (status == STATUS_WAIT_YONMA_TONPU) { return FULL_BIT_YONMA_TONPU; }
            if (status == STATUS_WAIT_SANMA_TONPU) { return FULL_BIT_SANMA_TONPU; }
            return 0;
        }

        // ============================================================
        // 書き込み（要求者は送信のみ。固定Manager Ownerが検証して一括更新）
        // ============================================================

        // ローカルプレイヤーのステータスを要求する。status==STATUS_IDLE で登録解除。
        // 成立中（マッチメンバー）は変更不可（キャンセルは待機中のみ＝仕様B）。
        public void RequestSetLocalStatus(int status)
        {
            if (GetLocalMatchTable() >= 0) { return; }
            if (status != STATUS_IDLE && IsLocalFreeMember()) { return; }
            if (status != STATUS_IDLE)
            {
                var me = Networking.LocalPlayer;
                if (me != null && Utilities.IsValid(me) &&
                    IsPlayerSeatedInOngoingGame(me.playerId))
                {
                    return;
                }
            }
            if (status != STATUS_IDLE && status != STATUS_FREE_RECRUIT && !IsRankedWaitStatus(status))
            {
                return;
            }
            // 閉じている列へは並べない（本命の判定はOwner側。ここは押し心地のための早期弾き）。
            if (IsRankedWaitStatus(status) && !IsRankedColumnEnabled(status)) { return; }

            desiredStatus_ = status;
            desiredStatusRequestSeq_ = NextLocalRequestSeq();
            desiredRetryLeft_ = DESIRED_RETRY_MAX;
            ApplyLocalStatus();
        }

        // uGUI Button の onClick → SendCustomEvent 用の引数なしラッパ。
        public void SetWaitYonma() { RequestSetLocalStatus(STATUS_WAIT_YONMA); }
        public void SetWaitSanma() { RequestSetLocalStatus(STATUS_WAIT_SANMA); }
        public void SetWaitYonmaTonpu() { RequestSetLocalStatus(STATUS_WAIT_YONMA_TONPU); }
        public void SetWaitSanmaTonpu() { RequestSetLocalStatus(STATUS_WAIT_SANMA_TONPU); }
        public void SetFreeRecruit() { RequestSetLocalStatus(STATUS_FREE_RECRUIT); }
        public void SetIdle() { RequestSetLocalStatus(STATUS_IDLE); }

        // desiredStatus_ を同期状態へ反映する（初回要求とリトライの共通処理）。
        private void ApplyLocalStatus()
        {
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return; }
            if (desiredStatusRequestSeq_ <= 0) { desiredStatusRequestSeq_ = NextLocalRequestSeq(); }

            if (Networking.IsOwner(gameObject))
            {
                ApplyStatusRequestAuthoritative(lp.playerId, desiredStatus_, desiredStatusRequestSeq_);
                return;
            }

            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(NetApplyStatusRequest),
                desiredStatus_, desiredStatusRequestSeq_);
        }

        [NetworkCallable]
        public void NetApplyStatusRequest(int status, int requestSeq)
        {
            if (!Networking.IsOwner(gameObject)) { return; }
            var caller = NetworkCalling.CallingPlayer;
            if (caller == null || !Utilities.IsValid(caller)) { return; }
            ApplyStatusRequestAuthoritative(caller.playerId, status, requestSeq);
        }

        private void ApplyStatusRequestAuthoritative(int playerId, int status, int requestSeq)
        {
            if (!Networking.IsOwner(gameObject)) { return; }
            if (status != STATUS_IDLE && status != STATUS_FREE_RECRUIT && !IsRankedWaitStatus(status))
            {
                return;
            }
            if (!AcceptManagerRequestSequence(playerId, requestSeq)) { return; }

            bool allowed = !IsIdInAnyMatch(playerId);
            if (status == STATUS_FREE_RECRUIT)
            {
                int hostRoom = FindFreeHostRoomOfId(playerId);
                allowed = allowed && hostRoom >= 0 && freeState[hostRoom] == FREE_RECRUIT;
            }
            else if (status != STATUS_IDLE)
            {
                // ここへ来る status は段位戦の待機のみ（IDLE と FREE_RECRUIT は上で分岐済み）。
                // 閉じた列への要求は、UIを外して直接投げられても Owner が捨てる。
                allowed = allowed && !IsIdFreeMember(playerId)
                    && !IsPlayerSeatedInOngoingGame(playerId)
                    && IsRankedColumnEnabled(status);
            }

            bool changed = false;
            if (allowed)
            {
                changed = WriteStatus(playerId, status);
                changed = TryFormMatchAll() || changed;
            }

            CommitManagerState();
            if (localPlayer_ != null && Utilities.IsValid(localPlayer_)
                && localPlayer_.playerId == playerId)
            {
                desiredRetryLeft_ = 0;
            }
            if (changed) { UpdateLocalMatchView(); }
        }

        // slot配列を更新。実際に変化したら true。
        private bool WriteStatus(int id, int status)
        {
            if (id <= 0) { return false; }

            int found = -1;
            int free = -1;
            for (int i = 0; i < MAX_TRACKED; ++i)
            {
                if (slotPlayerId[i] == id) { found = i; break; }
                if (free < 0 && slotPlayerId[i] == 0) { free = i; }
            }

            if (status == STATUS_IDLE)
            {
                if (found >= 0)
                {
                    ClearSlot(found);
                    return true;
                }
                return false;
            }

            if (found >= 0)
            {
                if (slotStatus[found] == status) { return false; }
                slotStatus[found] = status;
                // モード変更は並び直し（四麻⇄三麻の乗り換えで先頭割込みさせない）。
                seqCounter = seqCounter + 1;
                slotSeq[found] = seqCounter;
                return true;
            }

            if (free >= 0)
            {
                slotPlayerId[free] = id;
                slotStatus[free] = status;
                seqCounter = seqCounter + 1;
                slotSeq[free] = seqCounter;
                return true;
            }

            Debug.LogWarning("[MatchmakingManager] status slot が満杯です。");
            return false;
        }

        private void ClearSlot(int i)
        {
            slotPlayerId[i] = 0;
            slotStatus[i] = STATUS_IDLE;
            slotSeq[i] = 0;
        }

        // ============================================================
        // 列の開閉要求（スタッフパネル → Owner）
        // ============================================================

        // スタッフパネルから呼ぶ。mask は COLUMN_BIT_* のbit和。
        public void RequestSetEnabledColumns(int mask)
        {
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return; }
            if (!IsStaffPlayer(lp)) { return; }

            mask = mask & COLUMN_MASK_ALL;
            int requestSeq = NextLocalRequestSeq();
            if (Networking.IsOwner(gameObject))
            {
                ApplyEnabledColumnsAuthoritative(lp.playerId, lp, mask, requestSeq);
                return;
            }
            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(NetApplyEnabledColumns),
                mask, requestSeq);
        }

        [NetworkCallable]
        public void NetApplyEnabledColumns(int mask, int requestSeq)
        {
            if (!Networking.IsOwner(gameObject)) { return; }
            var caller = NetworkCalling.CallingPlayer;
            if (caller == null || !Utilities.IsValid(caller)) { return; }
            ApplyEnabledColumnsAuthoritative(caller.playerId, caller, mask, requestSeq);
        }

        private void ApplyEnabledColumnsAuthoritative(
            int playerId, VRCPlayerApi player, int mask, int requestSeq)
        {
            if (!Networking.IsOwner(gameObject)) { return; }
            // 最終防衛線。送信側の IsStaffPlayer はUIを外せば抜けられる。
            if (!IsStaffPlayer(player)) { return; }
            if (!AcceptManagerRequestSequence(playerId, requestSeq)) { return; }

            mask = mask & COLUMN_MASK_ALL;
            if (mask == enabledColumnMask) { return; }
            enabledColumnMask = mask;

            // 閉じた列に並んでいた人を降ろす。成立済み（slotから抜けている）には触らない。
            DrainClosedColumns();
            CommitManagerState();
            UpdateLocalMatchView();
            Debug.Log("[MatchmakingManager] 待機列の開閉を更新: mask=" + mask
                + " by=" + (player != null ? player.displayName : "?"));
        }

        // ============================================================
        // イベントルールの変更要求（スタッフパネル → Owner）
        //
        // 引数は1つのintへ詰める。[NetworkCallable] の引数を増やすより、
        // 既存の「1要求 = (値, requestSeq)」の形に揃えたほうが取り違えが起きない。
        //   bit0-1 : 赤ドラ(0..3)  bit2 : ローカル役  bit3 : オープン立直
        //   bit4   : ツモ損あり    bit5 : トビあり    bit6 : 能力あり
        //   bit7-8 : 長考時間(0..2)
        // 負のintを作らない（Udonは負のintのuint化で停止する）。
        // ============================================================

        public int PackEventRules(int redDoraMode, bool localYaku, bool openReach,
            bool tsumoLoss, bool bust, bool abilityOn, int turnTimerMode)
        {
            if (redDoraMode < 0 || redDoraMode > 3) { redDoraMode = RED_DORA_MODE_THREE; }
            if (turnTimerMode < 0 || turnTimerMode > 2) { turnTimerMode = TURN_TIMER_8_30; }
            return redDoraMode
                | (localYaku ? 4 : 0)
                | (openReach ? 8 : 0)
                | (tsumoLoss ? 16 : 0)
                | (bust ? 32 : 0)
                | (abilityOn ? 64 : 0)
                | (turnTimerMode << 7);
        }

        // スタッフパネルから呼ぶ。packed は PackEventRules で作る。
        public void RequestSetEventRules(int packed)
        {
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return; }
            if (!IsStaffPlayer(lp)) { return; }

            int requestSeq = NextLocalRequestSeq();
            if (Networking.IsOwner(gameObject))
            {
                ApplyEventRulesAuthoritative(lp.playerId, lp, packed, requestSeq);
                return;
            }
            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(NetApplyEventRules),
                packed, requestSeq);
        }

        [NetworkCallable]
        public void NetApplyEventRules(int packed, int requestSeq)
        {
            if (!Networking.IsOwner(gameObject)) { return; }
            var caller = NetworkCalling.CallingPlayer;
            if (caller == null || !Utilities.IsValid(caller)) { return; }
            ApplyEventRulesAuthoritative(caller.playerId, caller, packed, requestSeq);
        }

        private void ApplyEventRulesAuthoritative(
            int playerId, VRCPlayerApi player, int packed, int requestSeq)
        {
            if (!Networking.IsOwner(gameObject)) { return; }
            // 最終防衛線。送信側の IsStaffPlayer はUIを外せば抜けられる。
            if (!IsStaffPlayer(player)) { return; }
            if (!AcceptManagerRequestSequence(playerId, requestSeq)) { return; }
            if (packed < 0) { return; }

            int redDora = packed & 3;
            int timer = (packed >> 7) & 3;
            if (timer > 2) { timer = TURN_TIMER_8_30; }

            eventRedDoraMode = redDora;
            eventLocalYaku = (packed & 4) != 0;
            eventOpenReach = (packed & 8) != 0;
            eventTsumoLoss = (packed & 16) != 0;
            eventBust = (packed & 32) != 0;
            eventAbilityOn = (packed & 64) != 0;
            eventTurnTimerMode = timer;

            CommitManagerState();
            Debug.Log("[MatchmakingManager] 段位戦のイベントルールを更新: flags=" + GetEventRuleFlags()
                + " timer=" + eventTurnTimerMode + " ability=" + eventAbilityOn
                + " by=" + (player != null ? player.displayName : "?"));
        }

        // ============================================================
        // 頭上スタッフバナーの表示切替要求（スタッフパネル → Owner）
        //
        // 列の開閉・イベントルールと同じ「要求を送る／Ownerが検証して適用」の形に揃える。
        // [NetworkCallable] の引数は既存に合わせて int（1=出す / 0=隠す）。
        // ============================================================

        public void RequestSetStaffBadgeVisible(bool visible)
        {
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return; }
            if (!IsStaffPlayer(lp)) { return; }

            int flag = visible ? 1 : 0;
            int requestSeq = NextLocalRequestSeq();
            if (Networking.IsOwner(gameObject))
            {
                ApplyStaffBadgeVisibleAuthoritative(lp.playerId, lp, flag, requestSeq);
                return;
            }
            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(NetApplyStaffBadgeVisible),
                flag, requestSeq);
        }

        [NetworkCallable]
        public void NetApplyStaffBadgeVisible(int flag, int requestSeq)
        {
            if (!Networking.IsOwner(gameObject)) { return; }
            var caller = NetworkCalling.CallingPlayer;
            if (caller == null || !Utilities.IsValid(caller)) { return; }
            ApplyStaffBadgeVisibleAuthoritative(caller.playerId, caller, flag, requestSeq);
        }

        private void ApplyStaffBadgeVisibleAuthoritative(
            int playerId, VRCPlayerApi player, int flag, int requestSeq)
        {
            if (!Networking.IsOwner(gameObject)) { return; }
            // 最終防衛線。送信側の IsStaffPlayer はUIを外せば抜けられる。
            if (!IsStaffPlayer(player)) { return; }
            if (!AcceptManagerRequestSequence(playerId, requestSeq)) { return; }

            bool next = flag != 0;
            if (next == staffBadgeVisible) { return; }
            staffBadgeVisible = next;

            CommitManagerState();
            Debug.Log("[MatchmakingManager] 頭上スタッフバナーを"
                + (next ? "表示" : "非表示") + "にしました by="
                + (player != null ? player.displayName : "?"));
        }

        // ============================================================
        // 観戦ルームの防音 ON/OFF 要求（スタッフパネル → Owner）
        // 頭上スタッフバナーと同じ形（要求を送る／Owner が検証して適用）。
        // ============================================================

        public void RequestSetSoundproof(bool on)
        {
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return; }
            if (!IsStaffPlayer(lp)) { return; }

            int flag = on ? 1 : 0;
            int requestSeq = NextLocalRequestSeq();
            if (Networking.IsOwner(gameObject))
            {
                ApplySoundproofAuthoritative(lp.playerId, lp, flag, requestSeq);
                return;
            }
            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(NetApplySoundproof),
                flag, requestSeq);
        }

        [NetworkCallable]
        public void NetApplySoundproof(int flag, int requestSeq)
        {
            if (!Networking.IsOwner(gameObject)) { return; }
            var caller = NetworkCalling.CallingPlayer;
            if (caller == null || !Utilities.IsValid(caller)) { return; }
            ApplySoundproofAuthoritative(caller.playerId, caller, flag, requestSeq);
        }

        private void ApplySoundproofAuthoritative(
            int playerId, VRCPlayerApi player, int flag, int requestSeq)
        {
            if (!Networking.IsOwner(gameObject)) { return; }
            // 最終防衛線。送信側の IsStaffPlayer はUIを外せば抜けられる。
            if (!IsStaffPlayer(player)) { return; }
            if (!AcceptManagerRequestSequence(playerId, requestSeq)) { return; }

            bool next = flag != 0;
            if (next == soundproofOn) { return; }
            soundproofOn = next;

            CommitManagerState();
            Debug.Log("[MatchmakingManager] 観戦ルームの防音を"
                + (next ? "ON" : "OFF") + "にしました by="
                + (player != null ? player.displayName : "?"));
        }

        // ============================================================
        // 空卓を埋める順番の変更要求（スタッフパネル → Owner）
        // ============================================================

        public void RequestSetTableOrder(int packed)
        {
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return; }
            if (!IsStaffPlayer(lp)) { return; }
            if (!IsValidTableOrder(packed)) { return; }

            int requestSeq = NextLocalRequestSeq();
            if (Networking.IsOwner(gameObject))
            {
                ApplyTableOrderAuthoritative(lp.playerId, lp, packed, requestSeq);
                return;
            }
            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(NetApplyTableOrder),
                packed, requestSeq);
        }

        [NetworkCallable]
        public void NetApplyTableOrder(int packed, int requestSeq)
        {
            if (!Networking.IsOwner(gameObject)) { return; }
            var caller = NetworkCalling.CallingPlayer;
            if (caller == null || !Utilities.IsValid(caller)) { return; }
            ApplyTableOrderAuthoritative(caller.playerId, caller, packed, requestSeq);
        }

        private void ApplyTableOrderAuthoritative(
            int playerId, VRCPlayerApi player, int packed, int requestSeq)
        {
            if (!Networking.IsOwner(gameObject)) { return; }
            // 最終防衛線。送信側の IsStaffPlayer はUIを外せば抜けられる。
            if (!IsStaffPlayer(player)) { return; }
            if (!AcceptManagerRequestSequence(playerId, requestSeq)) { return; }
            // 順列として壊れている値は捨てる（同じ卓が2度出ると2組を同じ卓へ流し込む）。
            if (!IsValidTableOrder(packed)) { return; }
            if (packed == tableOrderPacked) { return; }

            tableOrderPacked = packed;
            // すでに割り当て済みの卓は動かさない（対局が始まっている）。
            // 次に空卓を探すときからこの順で埋まる。
            CommitManagerState();
            Debug.Log("[MatchmakingManager] 空卓の割当順を更新: packed=" + packed
                + " by=" + (player != null ? player.displayName : "?"));
        }

        // 閉じた列の待機者を IDLE へ落とす。Owner専用。
        private void DrainClosedColumns()
        {
            for (int i = 0; i < MAX_TRACKED; ++i)
            {
                if (slotPlayerId[i] == 0) { continue; }
                int st = slotStatus[i];
                if (!IsRankedWaitStatus(st)) { continue; }
                if (IsRankedColumnEnabled(st)) { continue; }
                ClearSlot(i);
            }
        }

        // ============================================================
        // 成立判定（Ownerのみ。書込直後とTickの両方から呼ばれる）
        // ============================================================

        private bool TryFormMatchAll()
        {
            if (!Networking.IsOwner(gameObject)) { return false; }
            // 待機列の掃除はここでは毎秒行わない。VRCPlayerApi.GetPlayerById は extern 呼び出しで、
            // 待機人数ぶん毎秒回すと最大50人×4列＝200回/秒になる（Questで無視できない）。
            // 実際に成立する直前だけ掃除する＝TryFormMatch の中（SweepWaitStatus）。
            // 人数不足・空卓なしで終わる通常時は extern を1回も増やさない。
            bool changed = false;
            // 4列を独立に成立させる。半荘を先に見るのは、東風が後発で人数が薄い間に
            // 空卓を先取りして半荘側を待たせ続けないため（空卓不足時の優先順位）。
            changed = TryFormMatch(4, RULE_LEN_HANCHAN) || changed;
            changed = TryFormMatch(3, RULE_LEN_HANCHAN) || changed;
            changed = TryFormMatch(4, RULE_LEN_TONPU) || changed;
            changed = TryFormMatch(3, RULE_LEN_TONPU) || changed;
            return changed;
        }

        // 指定モード（人数×局数）の成立を可能な限り行う。状態が変化したら true。
        private bool TryFormMatch(int mode, int ruleLength)
        {
            int waitStatus = GetRankedWaitStatus(mode, ruleLength);
            int fullBit = RankedFullBitOf(waitStatus);
            if (waitStatus == STATUS_IDLE || fullBit == 0) { return false; }
            bool changed = false;

            while (true)
            {
                int waiters = 0;
                for (int i = 0; i < MAX_TRACKED; ++i)
                {
                    if (slotPlayerId[i] != 0 && slotStatus[i] == waitStatus) { ++waiters; }
                }

                if (waiters < mode)
                {
                    if ((tablesFullMask & fullBit) != 0) { tablesFullMask &= ~fullBit; changed = true; }
                    break;
                }

                int tableIdx = PickFreeTable();
                if (tableIdx < 0)
                {
                    if ((tablesFullMask & fullBit) == 0) { tablesFullMask |= fullBit; changed = true; }
                    break;
                }

                if (SweepWaitStatus(waitStatus))
                {
                    changed = true;
                    continue;
                }

                // 開始前の席が残っている人は数えない。足りなければ席が空くまで待つ。
                // 判定（全卓の走査）は1スロット1回だけ。下の候補選びもこの結果を使う。
                int eligible = 0;
                for (int i = 0; i < MAX_TRACKED; ++i)
                {
                    slotHeld_[i] = false;
                    if (slotPlayerId[i] == 0 || slotStatus[i] != waitStatus) { continue; }
                    slotHeld_[i] = SeatStateOf(slotPlayerId[i]) == SEAT_STATE_PRESTART;
                    if (!slotHeld_[i]) { ++eligible; }
                }
                if (eligible < mode)
                {
                    // 空き卓はある（PickFreeTable が返した）ので「満卓」ではない。旗を立てたままにしない。
                    if ((tablesFullMask & fullBit) != 0) { tablesFullMask &= ~fullBit; changed = true; }
                    break;
                }

                for (int k = 0; k < 4; ++k)
                {
                    matchPlayerIds[tableIdx * 4 + k] = 0;
                    matchSeated[tableIdx * 4 + k] = false;
                }

                for (int k = 0; k < mode; ++k)
                {
                    int best = -1;
                    int bestSeq = 0;
                    for (int i = 0; i < MAX_TRACKED; ++i)
                    {
                        if (slotPlayerId[i] == 0 || slotStatus[i] != waitStatus) { continue; }
                        if (slotHeld_[i]) { continue; }
                        if (best < 0 || slotSeq[i] < bestSeq)
                        {
                            best = i;
                            bestSeq = slotSeq[i];
                        }
                    }
                    if (best < 0) { break; }
                    matchPlayerIds[tableIdx * 4 + k] = slotPlayerId[best];
                    ClearSlot(best);
                }

                if (!ValidateNewMatchMembers(tableIdx, mode))
                {
                    Debug.LogWarning("[MatchmakingManager] 成立候補を破棄: 重複/不在メンバー table=" + tableIdx);
                    for (int k = 0; k < mode; ++k)
                    {
                        int id = matchPlayerIds[tableIdx * 4 + k];
                        if (id > 0) { WriteStatus(id, waitStatus); }
                        matchPlayerIds[tableIdx * 4 + k] = 0;
                        matchSeated[tableIdx * 4 + k] = false;
                    }
                    changed = true;
                    continue;
                }

                matchMode[tableIdx] = mode;
                int formedMatchId = IssueMatchId(tableIdx);
                int formedAtMs = Networking.GetServerTimeInMilliseconds();
                matchReadyAtMs[tableIdx] = formedAtMs + MATCH_PREPARE_MS;
                matchDeadlineMs[tableIdx] = formedAtMs + MATCH_PREPARE_MS + SEAT_TIMEOUT_MS;
                matchPreparedMask &= ~(1 << tableIdx);
                matchClearReason[tableIdx] = CLEAR_NONE;
                tableRuleLength[tableIdx] = ruleLength;
                // 局数は待機列（半荘/東風）が正。ここでイベント設定に上書きさせない
                // ＝一局戦が段位戦へ入る経路をそもそも作らない。
                tableRuleFlags[tableIdx] = GetEventRuleFlags();
                tableTurnTimerMode[tableIdx] = eventTurnTimerMode;
                tableAbilityOn[tableIdx] = eventAbilityOn;
                freeTableMask &= ~(1 << tableIdx);
                rankedTableMask |= 1 << tableIdx;
                invalidRankedTableMask &= ~(1 << tableIdx);
                tableCpuCount[tableIdx] = 0;
                tableCpuHostId[tableIdx] = 0;
                // 前の対局の途中退出申告を新しい成立へ持ち込まない（席番号は使い回し）。
                tableAbandonMask[tableIdx] = 0;
                if ((tablesFullMask & fullBit) != 0) { tablesFullMask &= ~fullBit; }
                changed = true;

                Debug.Log("[MatchmakingManager] 成立: matchId=" + formedMatchId
                    + " revision=" + (stateRevision + 1)
                    + " mode=" + mode
                    + " len=" + (ruleLength == RULE_LEN_TONPU ? "東風" : "半荘")
                    + " table=" + tableIdx
                    + " members=" + BuildMatchMemberLog(tableIdx, mode));
            }

            return changed;
        }

        private bool ValidateNewMatchMembers(int tableIndex, int mode)
        {
            if (mode != 3 && mode != 4) { return false; }
            for (int k = 0; k < mode; ++k)
            {
                int id = matchPlayerIds[tableIndex * 4 + k];
                if (id <= 0) { return false; }
                var p = VRCPlayerApi.GetPlayerById(id);
                if (p == null || !Utilities.IsValid(p)) { return false; }
                for (int j = 0; j < k; ++j)
                {
                    if (matchPlayerIds[tableIndex * 4 + j] == id) { return false; }
                }
                for (int t = 0; t < numTables_; ++t)
                {
                    if (t == tableIndex || matchMode[t] == 0) { continue; }
                    for (int s = 0; s < matchMode[t]; ++s)
                    {
                        if (matchPlayerIds[t * 4 + s] == id) { return false; }
                    }
                }
            }
            return true;
        }

        private string BuildMatchMemberLog(int tableIndex, int mode)
        {
            string result = "";
            for (int k = 0; k < mode; ++k)
            {
                if (k > 0) { result += ","; }
                result += matchPlayerIds[tableIndex * 4 + k].ToString();
            }
            return result;
        }

        // 指定した待機列だけを掃除する。成立の直前にしか呼ばない＝ extern（GetPlayerById）は
        // 「実際にマッチが組まれるとき、その列の待機者ぶん」だけ。通常時のコストはゼロ。
        // 何か消したら true（呼び側は人数を数え直す）。
        private bool SweepWaitStatus(int waitStatus)
        {
            bool changed = false;
            for (int i = 0; i < MAX_TRACKED; ++i)
            {
                if (slotPlayerId[i] == 0 || slotStatus[i] != waitStatus) { continue; }
                if (IsQueueSlotUsable(i)) { continue; }
                ClearSlot(i);
                changed = true;
            }
            return changed;
        }

        // 待機スロットを成立に使ってよいか。
        //   ・プレイヤーが実在する（離脱直後の幽霊IDを弾く）
        //   ・まだどの卓のマッチにも入っていない（二重割当を弾く）
        private bool IsQueueSlotUsable(int i)
        {
            int id = slotPlayerId[i];
            if (id == 0) { return false; }
            var p = VRCPlayerApi.GetPlayerById(id);
            if (p == null || !Utilities.IsValid(p)) { return false; }
            if (IsIdInAnyMatch(id)) { return false; }
            // 対局中の人は列に居ても成立に使わない（誤って戻された残骸・対局中の押下）。
            // 使えない＝呼び側の SweepWaitStatus が列から落とすので「対局中なのに待機中」も消える。
            // 開始前の席が残っているだけの人は落とさない（成立解除で戻した直後。候補からは TryFormMatch が外す）。
            return !IsPlayerInStartedGame(id);
        }

        // 指定IDが「いまどこかの卓で対局中」か。
        //
        // 対局中の人を待機列へ戻したり、別卓のマッチへ引っ張り込んだりしないための番人。
        // 判定材料は2つ：
        //   ・卓がローカルでアクティブ＝gameState が新しい（近くの卓）
        //   ・busyTableMask＝卓Ownerが遷移時に申告した「対局中」（寝ている卓でも残る）
        // どちらの場合も、席の主は同期済みの seatPlayerId で確かめる。
        // 呼ぶのは解散時と成立直前だけ（毎フレーム・毎秒の経路には置かない）。
        // ローカルプレイヤーがいまどこかの卓に着席しているか（メニューの事前チェック用）。
        // 要求が Manager Owner に弾かれる条件を、押す前に画面側でも見られるようにする。
        public bool IsLocalPlayerSeatedInOngoingGame()
        {
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return false; }
            return IsPlayerSeatedInOngoingGame(lp.playerId);
        }

        private bool IsPlayerSeatedInOngoingGame(int id)
        {
            return SeatStateOf(id) != SEAT_STATE_NONE;
        }

        // 実際に始まっている対局（配牌以降）の席に居るか。開始前の着席は含めない。
        private bool IsPlayerInStartedGame(int id)
        {
            return SeatStateOf(id) == SEAT_STATE_STARTED;
        }

        private const int SEAT_STATE_NONE = 0;
        private const int SEAT_STATE_PRESTART = 1;   // 開始前の席にだけ名前が残っている（成立解除の直後など）
        private const int SEAT_STATE_STARTED = 2;    // 配牌以降の対局の席に居る

        // その人の席の状態を全卓1回の走査で返す。始まっている対局の席が見つかれば即 STARTED。
        // 開始前の席にだけ居る人は、待機列からは落とさず成立の候補から外す（TryFormMatch）。
        private int SeatStateOf(int id)
        {
            if (id <= 0) { return SEAT_STATE_NONE; }

            int state = SEAT_STATE_NONE;
            for (int t = 0; t < numTables_; ++t)
            {
                bool seated = false;
                // ① 卓Owner が申告した同期済みの席。寝ている卓でも読めるので、
                //    メインメニュー側にいる Manager Owner でもここだけは正しく効く。
                if ((busyTableMask & (1 << t)) != 0)
                {
                    for (int s = 0; s < 4; ++s)
                    {
                        if (tableSeatIds[t * 4 + s] == id) { seated = true; break; }
                    }
                }

                // ② 申告が届く前でも、卓がローカルでアクティブなら実物を直接読める（保険）。
                if (!seated)
                {
                    var tb = tables[t];
                    if (tb == null || !tb.gameObject.activeInHierarchy) { continue; }
                    if (tb.gameState == Mahjong.GAMESTATE_INITIAL && !HasAnyEntry(tb)) { continue; }

                    // CPU代打席は seatPlayerId を残したまま人が抜けた席なので、生のIDで見ると
                    // 途中退出した本人が永久に「対局中」のままになる（卓Ownerは自分の卓を
                    // 起こし続けるので必ずここを通る）。卓側の判定を使って人間の席だけ数える。
                    seated = tb.IsPlayerSeatedAtTable(id);
                }
                if (!seated) { continue; }

                if (IsTableGameStarted(t)) { return SEAT_STATE_STARTED; }
                state = SEAT_STATE_PRESTART;
            }
            return state;
        }

        // 指定IDが既にどこかの卓のマッチメンバーになっているか。
        private bool IsIdInAnyMatch(int id)
        {
            if (id == 0) { return false; }
            for (int t = 0; t < numTables_; ++t)
            {
                if (matchMode[t] == 0) { continue; }
                for (int k = 0; k < matchMode[t]; ++k)
                {
                    if (matchPlayerIds[t * 4 + k] == id) { return true; }
                }
            }
            return false;
        }

        // 空卓を1卓選ぶ。走査はスタッフパネルで決めた割当順（既定は卓(1)→(8)）。
        // ローカルでアクティブ（カリング解除中＝同期が新しい）卓を優先。
        // 空卓＝GAMESTATE_INITIAL かつ entry なし かつ マッチ未割当。
        private int PickFreeTable()
        {
            int nFresh = 0;
            int nStale = 0;
            for (int k = 0; k < MAX_TABLES; ++k)
            {
                int i = GetTableAtOrder(k);
                // 卓が8卓未満のときは、並びに載っている使えないindexを飛ばす。
                if (i < 0 || i >= numTables_) { continue; }
                if (!IsTableFree(i)) { continue; }

                var tb = tables[i];
                if (tb.gameObject.activeInHierarchy) { freshTableBuf_[nFresh] = i; ++nFresh; }
                else { staleTableBuf_[nStale] = i; ++nStale; }
            }

            if (nFresh > 0) { return freshTableBuf_[0]; }
            // カリング中の卓は gameState が古い可能性が残るが、対局中の卓は busyTableMask で
            // 除外済み。取りこぼした場合も、現地に着いた当事者の申告（ReportAssignedTableUnusable）
            // が即座に並び直させる＝60秒待たされない。
            if (nStale > 0) { return staleTableBuf_[0]; }
            return -1;
        }

        // 卓が空いているか。空卓割当の判定そのもので、スタッフパネルの卓順ページと
        // 卓上の番号表示（赤／緑）も同じものを見る＝「使えない卓」の定義を1か所に持つ。
        //
        // numTables_ は Start で決まるので、Start 前に外から呼ばれても答えられるよう
        // ここでは tables.Length を境界に使う。
        public bool IsTableFree(int i)
        {
            if (tables == null || i < 0 || i >= tables.Length) { return false; }
            if (i >= MAX_TABLES) { return false; }
            if (matchMode[i] != 0) { return false; }
            // スタッフの強制リセット待ち。卓の実体が戻る前に次のマッチを流し込まない。
            if ((tableForceResetMask & (1 << i)) != 0) { return false; }
            // 終局通知または安全な後片付けが済むまでは、旧対局の分類bitが残る卓を再利用しない。
            if (((freeTableMask | rankedTableMask | invalidRankedTableMask) & (1 << i)) != 0) { return false; }
            // 卓が寝ていて gameState が古くても、使用中の報告だけは常時アクティブな
            // こちら側に残っている。手動で始まった対局はこれでしか気づけない。
            if ((busyTableMask & (1 << i)) != 0) { return false; }
            var tb = tables[i];
            if (tb == null) { return false; }
            // 卓の実物（gameState / entry）は、この端末で卓が起きている時だけ見る。
            //
            // 寝ている卓は Deserialize が止まり、最後に見た時点の値で凍っている。
            // Manager Owner はふだん MainMenu 側にいるので、一度対局中の卓を見てから離れると
            // その卓は Owner の手元で永久に「対局中」になり、フリー部屋の作成も再戦も
            // 段位戦の割当も黙って拒否されていた（本人の画面では空き＝緑なので原因が見えない。
            // 2026-09-26 の強制リセット後に露見）。寝ている卓の状況は、卓Owner が申告する
            // 同期マスク（busyTableMask＋60秒ハートビート）が正。
            if (tb.gameObject.activeInHierarchy)
            {
                if (tb.gameState != Mahjong.GAMESTATE_INITIAL) { return false; }
                if (HasAnyEntry(tb)) { return false; }
            }
            return true;
        }

        // 使用中の卓のビット和。1秒ごとに引く用途向けに1回でまとめて返す。
        public int GetBusyTableMaskForView()
        {
            int mask = 0;
            int n = (tables != null) ? tables.Length : 0;
            if (n > MAX_TABLES) { n = MAX_TABLES; }
            for (int i = 0; i < n; ++i)
            {
                if (!IsTableFree(i)) { mask |= 1 << i; }
            }
            return mask;
        }

        // ローカルプレイヤーがこの卓に着席しているか（卓の seatPlayerId は同期済み public）。
        private bool IsLocalPlayerSeatedAt(Mahjong tb)
        {
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return false; }
            var sp = tb.seatPlayerId;
            if (sp == null) { return false; }
            for (int s = 0; s < 4 && s < sp.Length; ++s)
            {
                if (sp[s] == lp.playerId) { return true; }
            }
            return false;
        }

        // 有効席に誰か entry があるか。走査は卓の numPlayers 未満のみ
        // （三麻で seat3 に残骸 entry が残っても空卓誤認しない。残骸掃除自体は Mahjong.SetNumPlayers＝M9）。
        private bool HasAnyEntry(Mahjong tb)
        {
            var e = tb.entry;
            if (e == null) { return false; } // 未初期化卓＝誰も触っていない＝entryなし

            int n = tb.numPlayers;
            if (n < 1) { n = 1; }
            if (n > 4) { n = 4; }
            if (n > e.Length) { n = e.Length; }

            for (int i = 0; i < n; ++i)
            {
                if (e[i]) { return true; }
            }
            return false;
        }

        // ============================================================
        // 着席の自己申告（当事者本人が近接時に呼ぶ）→ 全員着席で完了
        // ============================================================

        private void ClearDesiredSeat()
        {
            desiredSeatTable_ = -1;
            desiredSeatMatchId_ = 0;
            desiredSeatRequestSeq_ = 0;
            desiredSeatRetryLeft_ = 0;
        }

        // 保留中の着席要求が、いまの成立と同一か（解散・別マッチなら破棄）。
        private bool IsDesiredSeatStillValid()
        {
            int t = desiredSeatTable_;
            if (t < 0 || t >= numTables_) { return false; }
            if (matchMode[t] == 0 || matchId[t] == 0) { return false; }
            if (matchId[t] != desiredSeatMatchId_) { return false; }

            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return false; }
            int id = lp.playerId;
            for (int i = 0; i < matchMode[t]; ++i)
            {
                if (matchPlayerIds[t * 4 + i] == id) { return true; }
            }
            return false;
        }

        // ローカルに既に着席フラグが立っているか（冪等・成功検知）。
        private bool IsLocalDesiredSeatApplied()
        {
            if (!IsDesiredSeatStillValid()) { return false; }
            int t = desiredSeatTable_;
            var lp = Networking.LocalPlayer;
            for (int i = 0; i < matchMode[t]; ++i)
            {
                if (matchPlayerIds[t * 4 + i] == lp.playerId)
                {
                    return matchSeated[t * 4 + i];
                }
            }
            return false;
        }

        private void RequestLocalSeated(int t)
        {
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return; }
            if (t < 0 || t >= numTables_ || matchMode[t] == 0 || matchId[t] == 0) { return; }

            desiredSeatTable_ = t;
            desiredSeatMatchId_ = matchId[t];
            desiredSeatRequestSeq_ = NextLocalRequestSeq();
            desiredSeatRetryLeft_ = DESIRED_RETRY_MAX;
            ApplyLocalSeated();
        }

        // desiredSeat* を同期状態へ反映（初回要求とリトライの共通処理。desiredStatus_ と同型）。
        private void ApplyLocalSeated()
        {
            if (desiredSeatTable_ < 0) { return; }
            if (!IsDesiredSeatStillValid())
            {
                ClearDesiredSeat();
                return;
            }

            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return; }
            int t = desiredSeatTable_;

            if (Networking.IsOwner(gameObject))
            {
                ApplySeatRequestAuthoritative(lp.playerId, t, desiredSeatMatchId_, desiredSeatRequestSeq_);
                return;
            }

            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(NetApplySeatRequest),
                t, desiredSeatMatchId_, desiredSeatRequestSeq_);
        }

        // ============================================================
        [NetworkCallable]
        public void NetApplySeatRequest(int t, int requestedMatchId, int requestSeq)
        {
            if (!Networking.IsOwner(gameObject)) { return; }
            var caller = NetworkCalling.CallingPlayer;
            if (caller == null || !Utilities.IsValid(caller)) { return; }
            ApplySeatRequestAuthoritative(caller.playerId, t, requestedMatchId, requestSeq);
        }

        private void ApplySeatRequestAuthoritative(int playerId, int t, int requestedMatchId, int requestSeq)
        {
            if (!Networking.IsOwner(gameObject)) { return; }
            if (t < 0 || t >= numTables_ || matchMode[t] == 0) { return; }
            if (requestedMatchId <= 0 || matchId[t] != requestedMatchId) { return; }
            if (!AcceptManagerRequestSequence(playerId, requestSeq)) { return; }

            int k = -1;
            for (int i = 0; i < matchMode[t]; ++i)
            {
                if (matchPlayerIds[t * 4 + i] == playerId) { k = i; break; }
            }

            if (k >= 0 && !matchSeated[t * 4 + k])
            {
                matchSeated[t * 4 + k] = true;
                bool all = true;
                for (int i = 0; i < matchMode[t]; ++i)
                {
                    if (matchPlayerIds[t * 4 + i] == 0) { continue; }
                    if (!matchSeated[t * 4 + i]) { all = false; break; }
                }
                // 抜けた人が居る成立（matchLostMask）は、残った人がそろっても完了にしない。
                // 0 を読み飛ばすので「残り全員着席」に見えるが、開始前の離脱＝解散して残った人を再待機へ戻す
                // （開始後なら SweepMatches → TryCompleteSeatedMatch が成立完了にする）。
                if (all && (matchLostMask & (1 << t)) == 0)
                {
                    ClearMatch(t, CLEAR_COMPLETED);
                    Debug.Log("[MatchmakingManager] 全員着席・成立完了: matchId="
                        + requestedMatchId + " table=" + t);
                }
            }

            CommitManagerState();
            if (localPlayer_ != null && Utilities.IsValid(localPlayer_)
                && localPlayer_.playerId == playerId)
            {
                ClearDesiredSeat();
            }
            UpdateLocalMatchView();
        }

        // 解除・掃除（Ownerのみが同期配列を触る）
        // ============================================================

        // 準備フェーズが明けた瞬間に、卓が本当に使える状態かを Owner が1回だけ確認する。
        //
        // PickFreeTable はカリングで寝ている卓（＝同期が古い）も候補にするため、
        // 「対局中なのに空卓に見えた」まま成立することがある。従来はそれを 60 秒の着席
        // タイムアウトでしか回収できず、当事者は他人の対局中の卓の前で1分待たされていた。
        // 準備の3秒のあいだに誰かが近づいて卓が起きていれば、ここで最新の状態が読めるので
        // 即座に解散して並び直せる。
        //
        // 卓が非アクティブなら判定を保留し、準備確認の安全タイムアウトまで再試行する。
        // 戻り値＝同期配列を触ったか。
        private bool VerifyPreparedMatch(int t, int now)
        {
            int bit = 1 << t;
            if ((matchPreparedMask & bit) != 0) { return false; }
            if (matchReadyAtMs[t] - now > 0) { return false; }

            // 着席が始まった旧クライアント混在時は、進行中マッチを誤って解散しない。
            for (int k = 0; k < matchMode[t]; ++k)
            {
                if (matchSeated[t * 4 + k]) { return false; }
            }

            var tb = tables[t];
            if (tb != null && tb.gameObject.activeInHierarchy
                && tb.gameState != Mahjong.GAMESTATE_INITIAL)
            {
                busyTableMask |= bit;
                Debug.LogWarning("[MatchmakingManager] 準備確認で卓が使用中だったため成立を解除: table=" + t);
                DissolveMatch(t, false);
                return true;
            }

            // 非アクティブ／同期未完了を成功扱いにせず再確認する。永久待機はさせない。
            if (now - matchReadyAtMs[t] > PREPARE_CONFIRM_TIMEOUT_MS)
            {
                // 申告が届いていないだけで着席・開始まで進んでいるなら解散しない。
                if (TryCompleteSeatedMatch(t)) { return true; }
                Debug.LogWarning("[MatchmakingManager] 卓の準備確認がタイムアウトしたため成立を解除: table=" + t);
                DissolveMatch(t, false);
                return true;
            }
            return false;
        }

        // 成立メンバー全員が本当に卓へ着席しているか（卓の seatPlayerId は同期済み public）。
        // 離脱で掃除済みのスロット（id=0）は飛ばす。
        private bool AllMatchMembersSeatedAt(int t, Mahjong tb)
        {
            var sp = tb.seatPlayerId;
            if (sp == null) { return false; }

            int mode = matchMode[t];
            if (mode != 3 && mode != 4) { return false; }

            bool any = false;
            for (int k = 0; k < mode; ++k)
            {
                int id = matchPlayerIds[t * 4 + k];
                if (id == 0) { continue; }

                bool seated = false;
                for (int s = 0; s < 4; ++s)
                {
                    if (sp[s] == id) { seated = true; break; }
                }
                if (!seated) { return false; }
                any = true;
            }
            return any;
        }


        // 卓 t で対局が実際に始まっているか（配牌以降）。卓Owner の同期申告（busy＋started）が正。
        // 申告が届く前の保険として、卓がローカルで起きていれば実物の gameState も見る。
        private bool IsTableGameStarted(int t)
        {
            int bit = 1 << t;
            if ((busyTableMask & bit) != 0 && (startedTableMask & bit) != 0) { return true; }
            var tb = tables[t];
            return tb != null && tb.gameObject.activeInHierarchy
                && tb.gameState != Mahjong.GAMESTATE_INITIAL
                && tb.gameState != Mahjong.GAMESTATE_ENTRY_COMPLETED;
        }


        // 成立メンバー全員が同期の申告で着席済みか（卓の実体は読まない＝卓が Manager Owner の手元で
        // 寝ていても判定できる）。卓Owner の席申告（tableSeatIds）は必ず見る。acceptSelfReport なら
        // 本人の着席申告（matchSeated）でも認める（片方の申告だけが落ちた成立を拾う）。
        // 1人でも確認できないメンバーが居れば false（＝開始前の1人着席を成立完了にしない）。
        // 成立メンバー（抜けた人の0は除く）の誰か1人でも、本人の着席申告か卓Owner の席申告で
        // この卓に座ったと確かめられるか。「開始済み」の対局がこの成立のものかを見分けるのに使う。
        private bool AnyMatchMemberSeatedByReport(int t)
        {
            int mode = matchMode[t];
            for (int k = 0; k < mode; ++k)
            {
                int id = matchPlayerIds[t * 4 + k];
                if (id == 0) { continue; }
                if (matchSeated[t * 4 + k]) { return true; }
                for (int s = 0; s < 4; ++s)
                {
                    if (tableSeatIds[t * 4 + s] == id) { return true; }
                }
            }
            return false;
        }

        private bool AllMatchMembersSeatedByReport(int t, bool acceptSelfReport)
        {
            int mode = matchMode[t];
            if (mode != 3 && mode != 4) { return false; }

            bool any = false;
            for (int k = 0; k < mode; ++k)
            {
                int id = matchPlayerIds[t * 4 + k];
                if (id == 0) { continue; }

                bool seated = acceptSelfReport && matchSeated[t * 4 + k];
                for (int s = 0; s < 4 && !seated; ++s)
                {
                    if (tableSeatIds[t * 4 + s] == id) { seated = true; }
                }
                if (!seated) { return false; }
                any = true;
            }
            return any;
        }

        // 着席の自己申告（matchSeated）の取りこぼしを回収する。
        //
        // 申告は Owner 書込の競合で落ちることがあり、それだけを根拠に「誰も来なかった」と
        // 裁定すると、実際には進行中の対局を解散してしまう。段位戦では DissolveMatch が
        // invalidRankedTableMask を立てるため、対局は普通に最後まで進むのに終局時の
        // ApplyRankedGameResult だけが黙って捨てられる（PT欄も出ない）。
        //
        // 卓の実体で「もう全員座っている／対局中」と確認できたときは、解散ではなく
        // 成立完了（ClearMatch COMPLETED）として畳む。分類bitは残るので段位戦のまま終局する。
        // 確認できないとき（本当に誰も来ない）は従来どおり呼び側が解散する。
        private bool TryCompleteSeatedMatch(int t)
        {
            if (t < 0 || t >= numTables_ || matchMode[t] == 0) { return false; }

            var tb = tables[t];

            // ① 卓がローカルでアクティブ＝最新の seatPlayerId が読める。全員着席なら確定。
            bool seatedHere = tb != null && tb.gameObject.activeInHierarchy
                              && AllMatchMembersSeatedAt(t, tb);

            // ② 卓が寝ている（Manager Owner が遠い）ときは、同期で残る手掛かりだけで判定する。
            //    busy は開始前の1人着席でも立つ（AdvanceTableBusyReport）ので、「busy＋誰か1人の申告」
            //    では、1人だけ座った成立を完了扱いにして残りの人を締め出していた。
            //    メンバー全員を、自己申告か卓Owner の席申告のどちらかで確認できたときだけ認める。
            bool busyByUs = (busyTableMask & (1 << t)) != 0 && AllMatchMembersSeatedByReport(t, true);

            // ④ 卓が実際に始まっている（配牌以降）。成立後にこの卓で始まった対局＝この成立の対局。
            //    メンバーが抜けた後でも、卓は CPU 代打で続いているので解散しない。
            // ただし別の対局（使用中の申告が遅れて誤割当された手動対局など）と取り違えないよう、
            // 成立メンバーの誰かが、この卓に座った（本人の着席申告 or 卓Owner の席申告）と確かめられること。
            bool startedHere = IsTableGameStarted(t) && AnyMatchMemberSeatedByReport(t);

            // ③ 卓Owner の席申告（同期）で全員そろっている。②と違い「1人でも申告」ではなく
            //    全員を要求するので、着席自己申告が丸ごと落ちた成立もここで正しく畳める。
            bool seatedByReport = AllMatchMembersSeatedByReport(t, false);

            // 開始前にメンバーが抜けていたら（matchPlayerIds が0にされている）、①〜③は残った人だけで
            // そろって見えてしまう＝1人だけ座った成立を完了にしていた。開始前は④以外で完了にしない。
            // matchLostMask＝この成立のメンバーがインスタンスを抜けて matchPlayerIds が0にされた（SweepMatches・同期済み）。
            if ((matchLostMask & (1 << t)) != 0) { seatedHere = false; busyByUs = false; seatedByReport = false; }
            if (!seatedHere && !busyByUs && !seatedByReport && !startedHere) { return false; }

            ClearMatch(t, CLEAR_COMPLETED);
            Debug.LogWarning("[MatchmakingManager] 着席申告の取りこぼしを成立完了として回収: table=" + t);
            return true;
        }

        // マッチを解除する。requeueSeatedOnly=true（着席タイムアウト）なら着席済みメンバーだけ
        // 待機列へ戻す（仕様B：着席しない人がいたら成立解除→残りは待機列へ）。
        // false（メンバー離脱）なら残存メンバー全員を待機列へ戻す。
        private void DissolveMatch(int t, bool requeueSeatedOnly)
        {
            int mode = matchMode[t];
            if (mode == 0) { return; }
            bool isFree = (freeTableMask & (1 << t)) != 0;
            bool isRanked = (rankedTableMask & (1 << t)) != 0;
            // 元の列（人数×局数）へ戻す。ClearRankedTableResultFlags が
            // tableRuleLength を半荘へ戻す前に読むこと。
            int waitStatus = GetRankedWaitStatus(mode, tableRuleLength[t]);

            for (int k = 0; k < mode; ++k)
            {
                int id = matchPlayerIds[t * 4 + k];
                if (id == 0) { continue; }
                var p = VRCPlayerApi.GetPlayerById(id);
                if (p == null || !Utilities.IsValid(p)) { continue; }
                // フリー戦の解除は再キューしない（部屋は開始時に解散済み。メンバーはメニューへ戻る）。
                // 対局中の人は戻さない。卓では普通に打っているのにメニュー・ネームプレートが
                // 「待機中」になり、そのまま別卓へ再マッチされ得る（実プレイで出た異変）。
                // 待機列へ戻す資格は通常の待機登録（ApplyStatusRequestAuthoritative）と同じ：
                // フリー部屋のメンバーでない・列が開いている・どこかで実際に対局中でない。
                // 開始前の席（この成立のために座った席）が残っていても戻してよい。席が空くまでは
                // 成立の候補から外す（TryFormMatch）＝同期済みの列に載るので Owner が替わっても消えない。
                if (!isFree && waitStatus != STATUS_IDLE &&
                    (!requeueSeatedOnly || matchSeated[t * 4 + k]) &&
                    !IsPlayerInStartedGame(id) && !IsIdFreeMember(id) && IsRankedColumnEnabled(waitStatus))
                {
                    WriteStatus(id, waitStatus);
                }
            }

            ClearMatch(t, CLEAR_DISSOLVED);
            if (isFree)
            {
                // 対局は始まらないので卓ルールの残骸も即時クリア（着席誘導中の解散＝放置解放経路）。
                ClearFreeTableFlags(t);
            }
            if (isRanked)
            {
                // 着席自己申告の競合で、卓側が既にENTRYをラッチした後にタイムアウト判定される
                // 可能性も保守的に扱う。分類bitは即時に落とし、無効bitはINITIAL空卓まで残す。
                invalidRankedTableMask |= 1 << t;
                ClearRankedTableResultFlags(t);
            }
            Debug.Log("[MatchmakingManager] 成立解除: table=" + t + (isFree ? " (フリー)" : ""));
        }

        private void ClearMatch(int t, int reason)
        {
            matchLostMask &= ~(1 << t);
            lostSinceMs_[t] = 0;
            matchMode[t] = 0;
            matchId[t] = 0;
            matchClearReason[t] = reason;
            matchReadyAtMs[t] = 0;
            matchPreparedMask &= ~(1 << t);
            if (reason == CLEAR_COMPLETED &&
                ((freeTableMask | rankedTableMask) & (1 << t)) != 0)
            {
                matchDeadlineMs[t] = Networking.GetServerTimeInMilliseconds() + FREE_ENTRY_GRACE_MS;
            }
            else
            {
                matchDeadlineMs[t] = 0;
            }
            for (int k = 0; k < 4; ++k)
            {
                matchPlayerIds[t * 4 + k] = 0;
                matchSeated[t * 4 + k] = false;
            }
        }

        // フリー戦卓フラグ・ルール残骸の共通クリア（Mask / 理由 / 期限をセットで落とす）。
        private void ClearFreeTableFlags(int t)
        {
            freeTableMask &= ~(1 << t);
            tableRuleLength[t] = RULE_LEN_HANCHAN;
            tableRuleFlags[t] = RULE_FLAG_NONE;
            tableTurnTimerMode[t] = TURN_TIMER_8_30;
            tableAbilityOn[t] = false;
            tableCpuCount[t] = 0;
            tableCpuHostId[t] = 0;
            if (matchClearReason[t] == CLEAR_COMPLETED)
            {
                matchClearReason[t] = CLEAR_NONE;
            }
            matchDeadlineMs[t] = 0;
        }

        // 段位戦分類bit・無効bit・着席完了保護をまとめて冪等に落とす。
        // 成立解除、ENTRY猶予切れ、RESULT後にINITIALへ戻った安全な掃除で使う。
        private void ClearRankedTableFlags(int t)
        {
            rankedTableMask &= ~(1 << t);
            invalidRankedTableMask &= ~(1 << t);
            tableRuleLength[t] = RULE_LEN_HANCHAN;
            tableRuleFlags[t] = RULE_FLAG_NONE;
            tableTurnTimerMode[t] = TURN_TIMER_8_30;
            tableAbilityOn[t] = false;
            tableCpuCount[t] = 0;
            tableCpuHostId[t] = 0;
            if (matchClearReason[t] == CLEAR_COMPLETED)
            {
                matchClearReason[t] = CLEAR_NONE;
            }
            matchDeadlineMs[t] = 0;
        }

        // RESULT通知では段位戦分類だけを落とす。無効bitは、forced wall通知や開始前成立解除を
        // まだ受けていないクライアントが遅れてRESULTを処理しても段位を書かないため、
        // 卓がINITIALかつ空席へ戻ったことをCleanupFinishedFreeTablesが確認するまで保持する。
        private void ClearRankedTableResultFlags(int t)
        {
            rankedTableMask &= ~(1 << t);
            tableRuleLength[t] = RULE_LEN_HANCHAN;
            tableRuleFlags[t] = RULE_FLAG_NONE;
            tableTurnTimerMode[t] = TURN_TIMER_8_30;
            tableAbilityOn[t] = false;
            tableCpuCount[t] = 0;
            tableCpuHostId[t] = 0;
            if (matchClearReason[t] == CLEAR_COMPLETED)
            {
                matchClearReason[t] = CLEAR_NONE;
            }
            matchDeadlineMs[t] = 0;
        }

        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            if (player == null) { return; }
            if (!Networking.IsOwner(gameObject)) { return; }

            bool changed = ClearManagerRequestSequence(player.playerId);
            changed = SweepQueue() || changed;
            changed = SweepMatches() || changed;
            changed = SweepFreeRoom() || changed;
            changed = TryFormMatchAll() || changed;
            if (changed) { CommitManagerState(); }
        }

        public override void OnOwnershipTransferred(VRCPlayerApi player)
        {
            // 新Ownerが掃除役を引き継ぐ（旧Owner離脱時の状態復元＝仕様E）。
            if (player == null || !player.isLocal) { return; }
            SweepStaleSlots();
        }

        // 無効プレイヤーのスロット/マッチを掃除して成立を再試行。Ownerでのみ有効。
        public void SweepStaleSlots()
        {
            if (!Networking.IsOwner(gameObject)) { return; }

            // 要求シーケンスのスロットもここで掃く。OnPlayerLeft の
            // ClearManagerRequestSequence は「そのとき Owner だったクライアント」でしか
            // 走らないため、Manager Owner 自身が抜けたときに離脱者のスロットが残る。
            bool changed = SweepManagerRequests();
            changed = SweepQueue() || changed;
            changed = SweepMatches() || changed;
            changed = SweepFreeRoom() || changed;
            changed = TryFormMatchAll() || changed;
            if (changed) { CommitManagerState(); }
        }

        // 使用中フラグの取りこぼしを回収する。
        //
        // ReportTableBusy は「卓のOwnerが遷移を見たとき」に1回だけ送る。対局中に全員が
        // 離脱すると誰も INITIAL への復帰を報告できず、bit が立ったまま残って卓が
        // 永久に選ばれなくなる。Owner が卓を直接見られるとき（起きている卓）だけ、
        // 実際の状態に合わせて畳み直す。寝ている卓には触らない（古い値しか読めない）。
        // 卓Owner が申告した席の主を反映する。負のIDは弾く（Udonで (uint) 化すると停止する）。
        private bool ApplySeatReport(int t, int seat0, int seat1, int seat2, int seat3)
        {
            bool changed = false;
            for (int s = 0; s < 4; ++s)
            {
                int id = s == 0 ? seat0 : (s == 1 ? seat1 : (s == 2 ? seat2 : seat3));
                if (id < 0) { id = 0; }
                if (tableSeatIds[t * 4 + s] == id) { continue; }
                tableSeatIds[t * 4 + s] = id;
                // 途中退出の申告は「その席にその人が座っていた間」だけのもの。席の主が
                // 変わった（代打へ切替済み／空席へ戻った／別人が着席）時点で必ず落とす。
                //
                // これを落とさないと申告は次の対局まで生き残り、その席へ座った無関係な人が
                // 開局直後に AdvanceAbandonTakeover でCPU代打へすり替えられる。従来の唯一の
                // クリア経路 ClearSeatReport は SweepBusyTables からしか呼ばれず、あれは
                // Manager Owner の手元で卓がアクティブであることを要求する＝Owner は普段
                // メインメニューにいて全卓が寝ているため、事実上一度も走らなかった。
                if ((tableAbandonMask[t] & (1 << s)) != 0)
                {
                    tableAbandonMask[t] &= ~(1 << s);
                }
                changed = true;
            }
            return changed;
        }

        private bool ClearSeatReport(int t)
        {
            bool changed = false;
            for (int s = 0; s < 4; ++s)
            {
                if (tableSeatIds[t * 4 + s] == 0) { continue; }
                tableSeatIds[t * 4 + s] = 0;
                changed = true;
            }
            // 途中退出の申告も同じ寿命（卓が空いたら消す）。残すと次の対局で
            // 座った人がいきなり代打にされる。
            if (tableAbandonMask[t] != 0)
            {
                tableAbandonMask[t] = 0;
                changed = true;
            }
            // 卓Owner 申告の「開始済み／終局表示中」も同じ寿命。busy と組で読む約束だが、Manager が
            // 自分で busy を立てる経路（準備確認・当事者の使用不可申告）で前の対局の bit が生き返らないよう、
            // 卓が空いた時点で一緒に消す。
            int bit = 1 << t;
            if (((startedTableMask | finishedTableMask) & bit) != 0)
            {
                startedTableMask &= ~bit;
                finishedTableMask &= ~bit;
                changed = true;
            }
            return changed;
        }

        private bool SweepBusyTables()
        {
            if (busyTableMask == 0) { return false; }

            bool changed = false;
            for (int t = 0; t < numTables_; ++t)
            {
                int bit = 1 << t;
                if ((busyTableMask & bit) == 0) { continue; }

                var tb = tables[t];
                if (tb == null || !tb.gameObject.activeInHierarchy) { continue; }
                if (tb.gameState != Mahjong.GAMESTATE_INITIAL || HasAnyEntry(tb)) { continue; }

                busyTableMask &= ~bit;
                ClearSeatReport(t);
                changed = true;
            }
            return changed;
        }

        // 離脱済みプレイヤーの要求シーケンススロットを解放する。
        // MAX_TRACKED 個たまると AcceptManagerRequestSequence が全部 false を返し、
        // 待機列登録・着席申告・フリー部屋操作が全員分黙って無視される。
        private bool SweepManagerRequests()
        {
            bool changed = false;
            for (int i = 0; i < MAX_TRACKED; ++i)
            {
                int id = managerRequestPlayerIds[i];
                if (id == 0) { continue; }
                var p = VRCPlayerApi.GetPlayerById(id);
                if (p != null && Utilities.IsValid(p)) { continue; }
                managerRequestPlayerIds[i] = 0;
                managerRequestSeqs[i] = 0;
                changed = true;
            }
            return changed;
        }

        private bool SweepQueue()
        {
            bool changed = false;
            for (int i = 0; i < MAX_TRACKED; ++i)
            {
                int id = slotPlayerId[i];
                if (id == 0) { continue; }
                var p = VRCPlayerApi.GetPlayerById(id);
                if (p == null || !Utilities.IsValid(p))
                {
                    ClearSlot(i);
                    changed = true;
                }
            }
            return changed;
        }

        // マッチ中のメンバーが離脱していたら、そのマッチを解除して残存メンバー全員を待機列へ戻す。
        private bool SweepMatches()
        {
            bool changed = false;
            for (int t = 0; t < numTables_; ++t)
            {
                if (matchMode[t] == 0) { continue; }

                bool memberLost = false;
                for (int k = 0; k < matchMode[t]; ++k)
                {
                    int id = matchPlayerIds[t * 4 + k];
                    if (id == 0) { continue; }
                    var p = VRCPlayerApi.GetPlayerById(id);
                    if (p == null || !Utilities.IsValid(p))
                    {
                        matchPlayerIds[t * 4 + k] = 0;
                        memberLost = true;
                    }
                }
                if (memberLost)
                {
                    matchLostMask |= 1 << t;
                    changed = true;
                }
                if ((matchLostMask & (1 << t)) == 0) { continue; }

                // 既に始まっている対局は解散しない（離脱席は卓側がCPU代打で引き継ぐ＝段位戦のまま）。
                if (TryCompleteSeatedMatch(t)) { changed = true; continue; }

                // 開始前の離脱に見える。ただし卓に誰か座っている（busy）間は、開始の申告が
                // まだ届いていないだけかもしれないので少し待つ（進行中の段位戦を解散して無効にしないため）。
                int now = Networking.GetServerTimeInMilliseconds();
                if (lostSinceMs_[t] == 0) { lostSinceMs_[t] = now == 0 ? 1 : now; }
                if ((busyTableMask & (1 << t)) != 0 && now - lostSinceMs_[t] < LOST_START_GRACE_MS) { continue; }

                DissolveMatch(t, false);
                changed = true;
            }
            return changed;
        }

        // ============================================================
        // フリー対局戦（実装④・仕様D）
        //   部屋はインスタンスで同時1つ。代表=freeMemberIds[0]。
        //   代表の離脱＝解散。設定中は無操作60秒でAFK解除。
        //   開始＝既存のマッチ機構（卓割当・ボイス・ビーコン・着席・60秒タイムアウト）へ流し込む。
        // ============================================================

        // --- 読み取り（メニュー描画用） ---

        // --- 卓ごとのアクセサ（部屋一覧の描画用。誰の部屋でも読める） ---

        public int GetFreeStateAt(int t)
        {
            if (t < 0 || t >= numTables_ || freeState == null) { return FREE_NONE; }
            return freeState[t];
        }

        public int GetFreeModeAt(int t)
        {
            if (t < 0 || t >= numTables_ || freeMode == null) { return 0; }
            return freeMode[t];
        }

        // ルール系は専用の同期変数を持たず tableRuleFlags のビットから解く。
        // 立て方は ApplyFreeActionRequestAuthoritative 側と1対1で対応させること
        // （片方だけ直すと「押しても表示が変わらない」になる）。
        public bool GetFreeAbilityOnAt(int t)
        {
            if (t < 0 || t >= numTables_) { return false; }
            return tableAbilityOn[t];
        }

        public int GetFreeRuleLengthAt(int t)
        {
            if (t < 0 || t >= numTables_) { return RULE_LEN_HANCHAN; }
            return tableRuleLength[t];
        }

        public int GetFreeTurnTimerModeAt(int t)
        {
            if (t < 0 || t >= numTables_) { return TURN_TIMER_8_30; }
            return tableTurnTimerMode[t];
        }

        public int GetFreeAotenjouModeAt(int t)
        {
            if (t < 0 || t >= numTables_) { return AOTENJOU_MODE_NONE; }
            int f = tableRuleFlags[t];
            if ((f & RULE_FLAG_AOTENJOU_TRADITIONAL) != 0) { return AOTENJOU_MODE_TRADITIONAL; }
            if ((f & RULE_FLAG_AOTENJOU) != 0) { return AOTENJOU_MODE_CURRENT; }
            return AOTENJOU_MODE_NONE;
        }

        public bool GetFreeLocalYakuAt(int t)
        {
            if (t < 0 || t >= numTables_) { return false; }
            return (tableRuleFlags[t] & RULE_FLAG_LOCAL_YAKU) != 0;
        }

        public bool GetFreeOpenReachAt(int t)
        {
            if (t < 0 || t >= numTables_) { return false; }
            return (tableRuleFlags[t] & RULE_FLAG_OPEN_REACH) != 0;
        }

        // 表示は「ツモ損あり/なし」、ビットは「ツモ損なし」で立つ＝極性が逆。
        public bool GetFreeTsumoLossAt(int t)
        {
            if (t < 0 || t >= numTables_) { return true; }
            return (tableRuleFlags[t] & RULE_FLAG_NO_TSUMO_LOSS) == 0;
        }

        public bool GetFreeBustAt(int t)
        {
            if (t < 0 || t >= numTables_) { return true; }
            return (tableRuleFlags[t] & RULE_FLAG_NO_BUST) == 0;
        }

        // 赤ドラは独立したビットではなく2ビットのフィールド（ALL = NONE|FOUR = 192）。
        // ビットごとに見ると ALL が NONE に化けるので、必ずマスクした値で比較する。
        public int GetFreeRedDoraModeAt(int t)
        {
            if (t < 0 || t >= numTables_) { return RED_DORA_MODE_THREE; }
            int v = tableRuleFlags[t] & RULE_FLAG_RED_DORA_MASK;
            if (v == RULE_FLAG_RED_DORA_NONE) { return RED_DORA_MODE_NONE; }
            if (v == RULE_FLAG_RED_DORA_FOUR) { return RED_DORA_MODE_FOUR; }
            if (v == RULE_FLAG_RED_DORA_ALL) { return RED_DORA_MODE_ALL; }
            return RED_DORA_MODE_THREE;
        }

        public int GetFreeCpuCountAt(int t)
        {
            if (t < 0 || t >= numTables_) { return 0; }
            return tableCpuCount[t];
        }

        public bool GetFreeCpuStrongAt(int t)
        {
            if (t < 0 || t >= numTables_) { return false; }
            return (tableRuleFlags[t] & RULE_FLAG_CPU_STRONG) != 0;
        }

        public int GetFreeHostIdAt(int t)
        {
            if (t < 0 || t >= numTables_ || freeState == null) { return 0; }
            if (freeState[t] == FREE_NONE) { return 0; }
            return freeMemberIds[t * 4];
        }

        public string GetFreeHostNameAt(int t)
        {
            int id = GetFreeHostIdAt(t);
            if (id <= 0) { return ""; }
            var p = VRCPlayerApi.GetPlayerById(id);
            if (p == null || !Utilities.IsValid(p)) { return ""; }
            return p.displayName;
        }

        public int GetFreeMemberCountAt(int t)
        {
            if (t < 0 || t >= numTables_ || freeState == null) { return 0; }
            if (freeState[t] == FREE_NONE) { return 0; }
            int n = 0;
            for (int i = 0; i < 4; ++i)
            {
                if (freeMemberIds[t * 4 + i] != 0) { ++n; }
            }
            return n;
        }

        public int GetFreeMemberIdAt(int t, int i)
        {
            if (t < 0 || t >= numTables_ || i < 0 || i >= 4 || freeState == null) { return 0; }
            if (freeState[t] == FREE_NONE) { return 0; }
            return freeMemberIds[t * 4 + i];
        }

        // 人間＋CPUで満員か（満員の部屋は一覧で「参加」を伏せる）。
        public bool IsFreeRoomFullAt(int t)
        {
            if (t < 0 || t >= numTables_ || freeState == null) { return false; }
            if (freeState[t] != FREE_RECRUIT) { return false; }
            return GetFreeMemberCountAt(t) + tableCpuCount[t] >= freeMode[t];
        }

        // その卓に新しく部屋を開けるか（一覧の「部屋を作る」を出す条件）。
        public bool CanOpenFreeRoomAt(int t)
        {
            if (t < 0 || t >= numTables_ || freeState == null) { return false; }
            if (freeState[t] != FREE_NONE) { return false; }
            return IsTableFree(t);
        }

        // --- ローカルプレイヤーの部屋（設定画面・部屋画面はこちらを見る） ---

        // 自分が入っている部屋の卓index（-1=どこにも居ない）。
        public int GetLocalFreeRoomTable()
        {
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return -1; }
            return FindFreeRoomOfId(lp.playerId);
        }

        public int GetFreeState() { return GetFreeStateAt(GetLocalFreeRoomTable()); }
        public int GetFreeMode() { return GetFreeModeAt(GetLocalFreeRoomTable()); }
        public bool GetFreeAbilityOn() { return GetFreeAbilityOnAt(GetLocalFreeRoomTable()); }
        public int GetFreeRuleLength() { return GetFreeRuleLengthAt(GetLocalFreeRoomTable()); }
        public int GetFreeTurnTimerMode() { return GetFreeTurnTimerModeAt(GetLocalFreeRoomTable()); }
        public int GetFreeRedDoraMode() { return GetFreeRedDoraModeAt(GetLocalFreeRoomTable()); }
        public bool GetFreeAotenjou() { return GetFreeAotenjouMode() != AOTENJOU_MODE_NONE; }
        public int GetFreeAotenjouMode() { return GetFreeAotenjouModeAt(GetLocalFreeRoomTable()); }
        public bool GetFreeLocalYaku() { return GetFreeLocalYakuAt(GetLocalFreeRoomTable()); }
        public bool GetFreeOpenReach() { return GetFreeOpenReachAt(GetLocalFreeRoomTable()); }
        public bool GetFreeTsumoLoss() { return GetFreeTsumoLossAt(GetLocalFreeRoomTable()); }
        public bool GetFreeBust() { return GetFreeBustAt(GetLocalFreeRoomTable()); }
        public int GetFreeCpuCount() { return GetFreeCpuCountAt(GetLocalFreeRoomTable()); }
        public bool GetFreeCpuStrong() { return GetFreeCpuStrongAt(GetLocalFreeRoomTable()); }
        public int GetFreeHostId() { return GetFreeHostIdAt(GetLocalFreeRoomTable()); }

        public string GetFreeHostName() { return GetFreeHostNameAt(GetLocalFreeRoomTable()); }

        public int GetFreeMemberCount() { return GetFreeMemberCountAt(GetLocalFreeRoomTable()); }

        // i番目の人間メンバーのplayerId（0=空き）。i=0 は代表。
        public int GetFreeMemberId(int i) { return GetFreeMemberIdAt(GetLocalFreeRoomTable(), i); }

        public bool IsLocalFreeHost()
        {
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return false; }
            return FindFreeHostRoomOfId(lp.playerId) >= 0;
        }

        public bool IsLocalFreeMember() { return GetLocalFreeRoomTable() >= 0; }

        // 指定の卓の部屋で自分が代表か。保留要求のリトライ判定に使う。
        public bool IsLocalFreeHostOfTable(int t)
        {
            if (t < 0 || t >= numTables_ || freeState == null) { return false; }
            if (freeState[t] == FREE_NONE) { return false; }
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return false; }
            return freeMemberIds[t * 4] == lp.playerId;
        }

        // 人間＋CPUで満員か（満員時は他人のボタンをグレーアウト＝仕様D）。
        public bool IsFreeRoomFull() { return IsFreeRoomFullAt(GetLocalFreeRoomTable()); }

        // 部屋を作れる空卓が無いか（ホーム画面の「満卓」表示用）。
        // 部屋＝卓になったので、詰まるのは開始時ではなく「部屋を作るとき」になった。
        public bool IsFreeStartBlockedByFullTables()
        {
            return (tablesFullMask & FULL_BIT_FREE) != 0;
        }

        // --- 卓側（Mahjong）が参照する対局ルールアクセサ ---

        private int IndexOfTable(Mahjong tb)
        {
            if (tb == null || tables == null) { return -1; }
            for (int i = 0; i < numTables_; ++i)
            {
                if (tables[i] == tb) { return i; }
            }
            return -1;
        }

        // 卓の局数ルール（半荘/東風/一局）。未登録卓は半荘（従来動作）。
        public int GetRuleLengthFor(Mahjong tb)
        {
            int i = IndexOfTable(tb);
            if (i < 0) { return RULE_LEN_HANCHAN; }
            return tableRuleLength[i];
        }

        // フリー対局中だけ卓側タイマーを上書きする。-1 は段位戦・手動卓＝Inspector設定を使う。
        public int GetTurnTimerBaseSecondsFor(Mahjong tb)
        {
            int i = IndexOfTable(tb);
            if (i < 0 || (freeTableMask & (1 << i)) == 0) { return -1; }
            if (tableTurnTimerMode[i] == TURN_TIMER_15_60) { return 15; }
            if (tableTurnTimerMode[i] == TURN_TIMER_60_255) { return 60; }
            return 8;
        }

        public int GetTurnTimerBonusSecondsFor(Mahjong tb)
        {
            int i = IndexOfTable(tb);
            if (i < 0 || (freeTableMask & (1 << i)) == 0) { return -1; }
            if (tableTurnTimerMode[i] == TURN_TIMER_15_60) { return 60; }
            if (tableTurnTimerMode[i] == TURN_TIMER_60_255) { return 255; }
            return 30;
        }

        // 卓の特殊ルール（RULE_FLAG_*のbit和）。未登録卓・段位戦は0＝標準。
        // 卓（Mahjong）は対局中これを毎フレーム参照できる（IndexOfTable は卓数ぶんの線形探索だけ）。
        public int GetRuleFlagsFor(Mahjong tb)
        {
            int i = IndexOfTable(tb);
            if (i < 0) { return RULE_FLAG_NONE; }
            return tableRuleFlags[i];
        }

        // この卓が「標準ルールから1つでも動いた設定」で打たれているか。
        // プロフィールのハイライトを「通常」と「イベント」へ振り分けるのに使う。
        //
        // ruleFlags だけでは足りない：長考プリセットと能力のオン/オフは別フィールドで、
        // スタッフ設定側の IsEventRulesActive() はそれも数えている。合わせないと
        // 「イベント開催中なのにハイライトは通常枠」という食い違いが出る。
        // 未登録の手動卓は tableAbilityOn が既定 false なので、登録卓だけ見る。
        public bool IsEventRuleGameTable(Mahjong tb)
        {
            int i = IndexOfTable(tb);
            if (i < 0) { return false; }
            int bit = 1 << i;
            if ((rankedTableMask & bit) == 0 && (freeTableMask & bit) == 0) { return false; }
            return tableRuleFlags[i] != RULE_FLAG_NONE
                || tableTurnTimerMode[i] != TURN_TIMER_8_30
                || !tableAbilityOn[i];
        }

        // 卓がフリー対局戦としてプレイ中か（戦績除外の判定に使う）。
        // 卓の使用状況の報告。卓のOwner（＝必ず卓の近くにいて状態が新しい）が、
        // INITIAL ⇔ 対局中 の遷移のたびに1回だけ呼ぶ。
        //
        // 着席誘導中（matchMode!=0）の卓は成立側が主役なので触らない。ここで使用中に
        // されると PickFreeTable の対象から外れるだけで実害は無いが、意味が二重になる。
        public void ReportTableBusy(Mahjong tb, bool busy, bool finished, bool started, int seat0, int seat1, int seat2, int seat3)
        {
            int t = IndexOfTable(tb);
            if (t < 0) { return; }
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return; }
            if (!Networking.IsOwner(lp, tb.gameObject)) { return; }

            // MatchmakingManagerのOwnerは奪わない。同期配列の書込者を常に中央Owner一人へ固定する。
            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(NetApplyTableBusyReport),
                t, busy, finished, started, seat0, seat1, seat2, seat3);
        }

        // 観戦モニターの役満通知の申告。卓Owner が 4席の判定が変わった時だけ呼ぶ（Mahjong.AdvanceSpectatorYakumanReport）。
        public void ReportSpectatorYakuman(Mahjong tb, int alert, int yaku)
        {
            int t = IndexOfTable(tb);
            if (t < 0) { return; }
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return; }
            if (!Networking.IsOwner(lp, tb.gameObject)) { return; }
            if (alert < 0 || yaku < 0) { return; }
            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(NetApplySpectatorYakuman), t, alert, yaku);
        }

        [NetworkCallable]
        public void NetApplySpectatorYakuman(int t, int alert, int yaku)
        {
            if (!Networking.IsOwner(gameObject)) { return; }
            if (t < 0 || t >= numTables_) { return; }
            if (alert < 0 || yaku < 0) { return; }
            var caller = NetworkCalling.CallingPlayer;
            var tb = tables[t];
            if (caller == null || !Utilities.IsValid(caller) || tb == null) { return; }
            var tableOwner = Networking.GetOwner(tb.gameObject);
            if (tableOwner == null || !Utilities.IsValid(tableOwner)
                || tableOwner.playerId != caller.playerId) { return; }
            // 対局中の卓だけ（空いた卓の遅れて届いた申告で通知しない）
            if ((busyTableMask & (1 << t)) == 0) { return; }
            if (tableYakumanAlert[t] == alert && tableYakumanYaku[t] == yaku) { return; }
            tableYakumanAlert[t] = alert;
            tableYakumanYaku[t] = yaku;
            CommitManagerState();
        }

        // 観戦モニター用の読み口（同期値だけで答える＝寝ている卓でも読める）。
        public int GetTableYakumanAlert(int t)
        {
            if (t < 0 || t >= numTables_) { return 0; }
            return tableYakumanAlert[t];
        }

        public int GetTableYakumanYaku(int t)
        {
            if (t < 0 || t >= numTables_) { return 0; }
            return tableYakumanYaku[t];
        }

        // ============================================================
        // 途中退出（対局中の自主離脱 → CPU 代打）
        // ============================================================

        // ローカルプレイヤーが今どの卓の何席に座っているか。座っていなければ -1。
        // 判定材料は卓Owner が申告した同期席ID＝寝ている卓でも読める（MainMenu から使う）。
        public int GetLocalSeatedTable()
        {
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return -1; }
            for (int t = 0; t < numTables_; ++t)
            {
                if ((busyTableMask & (1 << t)) == 0) { continue; }
                for (int s = 0; s < 4; ++s)
                {
                    if (tableSeatIds[t * 4 + s] == lp.playerId) { return t; }
                }
            }
            return -1;
        }

        // ローカルプレイヤーの座っている卓が、もう終局して結果表示中か。
        // MainMenu が「対局中」の案内を「対局は終わった」へ切り替え、途中退出を出さないために使う。
        public bool IsLocalSeatedTableFinished()
        {
            int t = GetLocalSeatedTable();
            if (t < 0) { return false; }
            return (finishedTableMask & (1 << t)) != 0;
        }

        public int GetLocalSeatedSeat()
        {
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return -1; }
            for (int t = 0; t < numTables_; ++t)
            {
                if ((busyTableMask & (1 << t)) == 0) { continue; }
                for (int s = 0; s < 4; ++s)
                {
                    if (tableSeatIds[t * 4 + s] == lp.playerId) { return s; }
                }
            }
            return -1;
        }

        // その卓が段位戦か（卓インデックス版）。段位ポイントの減点判定に使う。
        public bool IsRankedTableIndex(int t)
        {
            if (t < 0 || t >= numTables_) { return false; }
            return (rankedTableMask & (1 << t)) != 0;
        }

        // その卓がフリー対局か（卓インデックス版）。
        public bool IsFreeTableIndex(int t)
        {
            if (t < 0 || t >= numTables_) { return false; }
            return (freeTableMask & (1 << t)) != 0;
        }

        // 途中退出の申告。押した本人のクライアントから呼ぶ（卓は寝ていてよい）。
        public bool RequestAbandonSeat()
        {
            int t = GetLocalSeatedTable();
            int seat = GetLocalSeatedSeat();
            if (t < 0 || seat < 0) { return false; }
            // 終局後は途中退出にならない（呼び側は false を見て段位の減点もしない）。
            if ((finishedTableMask & (1 << t)) != 0) { return false; }

            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(NetApplyAbandonSeat), t, seat);
            return true;
        }

        // Manager Owner 側の受理。申告者が本当にその席の主かを同期席IDで検証する。
        [NetworkCallable]
        public void NetApplyAbandonSeat(int t, int seat)
        {
            if (!Networking.IsOwner(gameObject)) { return; }
            if (t < 0 || t >= numTables_ || seat < 0 || seat >= 4) { return; }

            var caller = NetworkCalling.CallingPlayer;
            if (caller == null || !Utilities.IsValid(caller)) { return; }
            if (tableSeatIds[t * 4 + seat] != caller.playerId) { return; }
            // 終局後（結果表示中）の申告は受けない。卓はまもなく空に戻るので代打の意味が無い。
            if ((finishedTableMask & (1 << t)) != 0) { return; }

            int bit = 1 << seat;
            if ((tableAbandonMask[t] & bit) != 0) { return; }
            tableAbandonMask[t] |= bit;
            CommitManagerState();
        }

        // 強制リセット待ちの卓か（卓index指定）。UI・空卓判定・カリングの起床条件で使う。
        public bool IsForceResetPending(int t)
        {
            if (t < 0 || t >= MAX_TABLES) { return false; }
            return (tableForceResetMask & (1 << t)) != 0;
        }

        // 登録済みの卓の数。MainMenu の強制リセット画面がボタンの出し分けに使う。
        public int GetTableCount() { return numTables_; }

        // 卓の実体 → 登録index（-1=未登録）。案内リングなど、卓を持っている側から引くため。
        public int GetTableIndexOf(Mahjong tb) { return IndexOfTable(tb); }

        // 観戦モニターの自動選択用。卓Ownerが申告した同期マスクだけで答える＝寝ている卓でも読める。
        public bool IsTableBusyIndex(int t)
        {
            if (t < 0 || t >= numTables_) { return false; }
            return (busyTableMask & (1 << t)) != 0;
        }

        public bool IsTableFinishedIndex(int t)
        {
            if (t < 0 || t >= numTables_) { return false; }
            return (finishedTableMask & (1 << t)) != 0;
        }

        // 卓 t の席 s に座っているプレイヤーID（空席・CPUは0）。
        public int GetTableSeatIdAt(int t, int s)
        {
            if (t < 0 || t >= numTables_ || s < 0 || s >= 4) { return 0; }
            return tableSeatIds[t * 4 + s];
        }

        // 卓が使われているか（卓インデックス版）。
        // MainMenu 側は卓の実体を読めない（近接カリングで寝ていて gameState が古い）ので、
        // 同期済みのマスクだけで答えられるこちらを使う。
        public bool IsTableIndexInUse(int t)
        {
            if (t < 0 || t >= numTables_) { return false; }
            if (matchMode[t] != 0) { return true; }
            if ((tableForceResetMask & (1 << t)) != 0) { return true; }
            return ((busyTableMask | freeTableMask | rankedTableMask | invalidRankedTableMask)
                    & (1 << t)) != 0;
        }

        public int GetForceResetGeneration(Mahjong tb)
        {
            int t = IndexOfTable(tb);
            if (t < 0 || (tableForceResetMask & (1 << t)) == 0) { return 0; }
            return tableForceResetGeneration[t];
        }

        // スタッフパネルからの要求。押した本人のクライアントから呼ぶ（卓は寝ていてよい）。
        //
        // 二段階確認はUI側（モード切替→卓選択→実行）で行う。ここへ来た時点で
        // 「本当に潰してよい」と判断済みとして扱うが、権限だけは Owner 側でもう一度見る。
        public void RequestForceResetTable(int tableIndex)
        {
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return; }
            if (tableIndex < 0 || tableIndex >= numTables_) { return; }
            if (!CanForceResetTableAt(lp, tableIndex)) { return; }

            int requestSeq = NextLocalRequestSeq();
            if (Networking.IsOwner(gameObject))
            {
                ApplyForceResetTableAuthoritative(lp.playerId, lp, tableIndex, requestSeq);
                return;
            }
            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(NetApplyForceResetTable),
                tableIndex, requestSeq);
        }

        [NetworkCallable]
        public void NetApplyForceResetTable(int tableIndex, int requestSeq)
        {
            if (!Networking.IsOwner(gameObject)) { return; }
            var caller = NetworkCalling.CallingPlayer;
            if (caller == null || !Utilities.IsValid(caller)) { return; }
            ApplyForceResetTableAuthoritative(caller.playerId, caller, tableIndex, requestSeq);
        }

        // Manager 側の後始末を全部やってから、卓Ownerへ「実体も戻せ」の旗を立てる。
        //
        // 順番が大事：busyTableMask を先に落として旗を立てないと、IsTableFree が一瞬 true に
        // なって旗の消化前に次のマッチが割り当たる。IsTableFree / IsTableInUse は
        // tableForceResetMask を見るので、旗が立っている間は空卓にもならず、卓Ownerの
        // カリングも起きたまま（ownerWake）になる。
        private void ApplyForceResetTableAuthoritative(
            int playerId, VRCPlayerApi player, int t, int requestSeq)
        {
            if (!Networking.IsOwner(gameObject)) { return; }
            // 最終防衛線。送信側の判定はUIを外せば抜けられる。
            if (t < 0 || t >= numTables_) { return; }
            if (!CanForceResetTableAt(player, t)) { return; }
            if (!AcceptManagerRequestSequence(playerId, requestSeq)) { return; }

            ForceResetTableState(t,
                IsStaffPlayer(player) ? FORCE_RESET_REASON_STAFF : FORCE_RESET_REASON_MASTER,
                playerId);

            CommitManagerState();
            Debug.LogWarning("[MatchmakingManager] 卓の強制リセットを要求: table=" + t
                + " by=" + (player != null ? player.displayName : "?"));
        }

        // 卓を「素の空卓」へ戻す Manager 側の後片付け＋卓Ownerへの実体リセット要求。
        // スタッフ操作（ApplyForceResetTableAuthoritative）と自浄（SweepStaleTableFlags）の共通処理。
        // CommitManagerState は呼び出し側でまとめて行う。
        private void ForceResetTableState(int t, int reason, int requesterId)
        {
            // 世代を進める前に「今の世代」を観測済みにしておく。こうしないと、入室後に一度も
            // Deserialize を受けていない Owner（最初からの Owner）では、この1件が初期化の
            // 取り込みに紛れてログに出ない。
            EnsureSeenForceResetGenerations();
            // 着席誘導中なら成立を解除してメンバーを待機列へ戻す（卓へ行っても意味が無くなるため）。
            if (matchMode[t] != 0) { DissolveMatch(t, false); }

            // 分類bit・卓ルール・CPU指示・無効bitを全部落とす。DissolveMatch が立てた
            // invalidRankedTableMask もここで消える（強制リセット後の卓は「素の空卓」）。
            ClearFreeRoomState(t);
            ClearRankedTableFlags(t);
            ClearFreeTableFlags(t);
            busyTableMask &= ~(1 << t);
            ClearSeatReport(t);          // 席の主 + 途中退出申告
            matchClearReason[t] = CLEAR_NONE;
            matchDeadlineMs[t] = 0;

            tableForceResetMask |= 1 << t;
            tableForceResetGeneration[t] = tableForceResetGeneration[t] == int.MaxValue
                ? 1 : tableForceResetGeneration[t] + 1;

            if (requesterId < 0) { requesterId = 0; }
            tableForceResetInfo[t] = (reason & 15) | (requesterId << 4);
            tableForceResetAtMs[t] = Networking.GetServerTimeInMilliseconds();

            // Owner は OnDeserialization を受けないので、ここで自分のログにも出す。
            LogObservedForceResets();
        }

        // ------------------------------------------------------------
        // 強制リセットの理由（卓の上の表示・全員のログ）
        // ------------------------------------------------------------

        public int GetForceResetReasonAt(int t)
        {
            if (t < 0 || t >= MAX_TABLES || tableForceResetInfo == null) { return FORCE_RESET_REASON_NONE; }
            return tableForceResetInfo[t] & 15;
        }

        public int GetForceResetAtMs(int t)
        {
            if (t < 0 || t >= MAX_TABLES || tableForceResetAtMs == null) { return 0; }
            return tableForceResetAtMs[t];
        }

        // 「スタッフ（名前）」「自動復旧（応答途絶）」など。卓の上とログの共通表記。
        // lang は KemonoLanguage.LANG_*（ログは日本語で残すので LANG_JA を渡す）。
        public string DescribeForceResetAt(int t, int lang)
        {
            if (t < 0 || t >= MAX_TABLES || tableForceResetInfo == null) { return ""; }
            int reason = GetForceResetReasonAt(t);
            int requesterId = tableForceResetInfo[t] >> 4;
            string who = "";
            if (requesterId > 0)
            {
                var p = VRCPlayerApi.GetPlayerById(requesterId);
                who = (p != null && Utilities.IsValid(p))
                    ? p.displayName
                    : L(lang, "退出済みのプレイヤー", "a player who has left", "퇴장한 플레이어",
                        "已离开的玩家", "已離開的玩家");
            }

            if (reason == FORCE_RESET_REASON_STAFF)
            {
                return L(lang, "スタッフ操作（" + who + "）", "by staff (" + who + ")", "스태프 조작 (" + who + ")",
                    "工作人员操作（" + who + "）", "工作人員操作（" + who + "）");
            }
            if (reason == FORCE_RESET_REASON_MASTER)
            {
                return L(lang, "インスタンスマスター操作（" + who + "）", "by the instance master (" + who + ")",
                    "인스턴스 마스터 조작 (" + who + ")",
                    "房主操作（" + who + "）", "房主操作（" + who + "）");
            }
            if (reason == FORCE_RESET_REASON_SILENT)
            {
                return L(lang, "自動復旧：卓の応答が5分途絶えたため", "auto-recovery: the table owner stopped responding",
                    "자동 복구: 탁의 응답이 5분간 끊겼기 때문",
                    "自动恢复：牌桌已5分钟无响应", "自動恢復：牌桌已5分鐘無回應");
            }
            if (reason == FORCE_RESET_REASON_STALE)
            {
                return L(lang, "自動復旧：予約だけ残った空卓のため", "auto-recovery: an empty reservation was left",
                    "자동 복구: 예약만 남은 빈 탁이었기 때문",
                    "自动恢复：只剩预约的空桌", "自動恢復：只剩預約的空桌");
            }
            return L(lang, "不明", "unknown", "알 수 없음", "未知", "未知");
        }

        // 日・英・韓・簡・繁の5つから lang（KemonoLanguage.LANG_*）の1つを選ぶ。
        private string L(int lang, string ja, string en, string ko, string zhs, string zht)
        {
            if (lang == KemonoLanguage.LANG_EN) { return en; }
            if (lang == KemonoLanguage.LANG_KO) { return ko; }
            if (lang == KemonoLanguage.LANG_ZHS) { return zhs; }
            if (lang == KemonoLanguage.LANG_ZHT) { return zht; }
            return ja;
        }

        // 卓の上に出す一文。直近 FORCE_RESET_NOTICE_MS 以内のリセットだけ返す（無ければ空）。
        public string DescribeRecentForceResetFor(Mahjong tb, int lang)
        {
            int t = IndexOfTable(tb);
            if (t < 0) { return ""; }
            if (GetForceResetReasonAt(t) == FORCE_RESET_REASON_NONE) { return ""; }
            int at = tableForceResetAtMs[t];
            if (at == 0) { return ""; }
            // サーバー時刻は負値になり得るので、比較は必ず減算で行う（ラップアラウンド安全）。
            int age = Networking.GetServerTimeInMilliseconds() - at;
            if (age < 0 || age > FORCE_RESET_NOTICE_MS) { return ""; }
            string why = DescribeForceResetAt(t, lang);
            return L(lang,
                "卓がリセットされました\n（" + why + "）",
                "This table was reset\n(" + why + ")",
                "탁이 리셋되었습니다\n(" + why + ")",
                "牌桌已重置\n（" + why + "）",
                "牌桌已重置\n（" + why + "）");
        }

        // 最初の観測は「既にあった分」として取り込むだけ（入室直後にログを撒かない）。
        private void EnsureSeenForceResetGenerations()
        {
            if (seenForceResetGeneration_ != null) { return; }
            seenForceResetGeneration_ = new int[MAX_TABLES];
            for (int t = 0; t < MAX_TABLES; ++t)
            {
                seenForceResetGeneration_[t] = tableForceResetGeneration[t];
            }
        }

        // 世代が進んだ卓を全員のログへ1行ずつ出す。発火元を後から追えるようにするため。
        private void LogObservedForceResets()
        {
            if (seenForceResetGeneration_ == null)
            {
                EnsureSeenForceResetGenerations();
                return;
            }
            for (int t = 0; t < numTables_; ++t)
            {
                int gen = tableForceResetGeneration[t];
                if (gen == seenForceResetGeneration_[t]) { continue; }
                seenForceResetGeneration_[t] = gen;
                Debug.LogWarning("[Matchmaking] 卓" + (t + 1) + " を強制リセット：" + DescribeForceResetAt(t, KemonoLanguage.LANG_JA)
                    + " generation=" + gen);
            }
        }

        // 卓Owner からの生存報告。Owner ローカルの観測なので同期しない。
        private void TouchTableHeartbeat(int t)
        {
            if (t < 0 || t >= MAX_TABLES || tableHeartbeatMs_ == null) { return; }
            tableHeartbeatMs_[t] = Networking.GetServerTimeInMilliseconds();
        }

        // 卓に触れずに「詰まった卓」を見つけて自分で片付ける。
        //
        // 既存の SweepBusyTables / CleanupFinishedFreeTables は卓がアクティブな時しか
        // 動けない。ここは同期状態とサーバー時刻だけで判断するので、卓が寝ていても走る。
        // 進行中の対局を誤って潰さないよう、拾うのは次の2つだけ。
        //   (1) busy が立っているのに卓Owner のハートビートが5分止まっている
        //       ＝卓Owner が居ないか、卓が起きられない。もう誰も進められない。
        //   (2) busy が立っていないのに分類bitだけが10分残っている
        //       ＝誰も座っていないのに卓が予約済みのまま。正規の猟予(5分)の倍。
        //
        // 着席誘導中（matchMode!=0）は SEAT_TIMEOUT_MS 側の担当なので触らない。
        private bool SweepStaleTableFlags(int now)
        {
            if (tableHeartbeatMs_ == null || tableStaleSinceMs_ == null
                || tableForceResetSinceMs_ == null || freeState == null) { return false; }

            bool changed = false;
            for (int t = 0; t < numTables_; ++t)
            {
                int bit = 1 << t;
                bool occupied = ((freeTableMask | rankedTableMask | invalidRankedTableMask
                                  | busyTableMask) & bit) != 0;

                // 強制リセット待ちの卓は、旗自体の失効だけを見る（下の2本の判定はかけない）。
                if ((tableForceResetMask & bit) != 0)
                {
                    tableStaleSinceMs_[t] = 0;
                    changed = ExpireStuckForceReset(t, now) || changed;
                    continue;
                }
                tableForceResetSinceMs_[t] = 0;

                // 空卓・着席誘導中は対象外。計測を止めて戻す。
                if (!occupied || matchMode[t] != 0 || freeState[t] != FREE_NONE)
                {
                    // 部屋が開いている卓の寿命は CheckFreeAfk が持つ（設定中60秒・募集中15分）。
                    // ここで拾うと、募集中の部屋が10分で卓ごと潰される。
                    tableStaleSinceMs_[t] = 0;
                    continue;
                }

                // Owner を引き継いだ直後はハートビートの履歴が無い。ここで撒き直さないと
                // 「5分間だれも報告していない」に見えて、健全な卓まで潰してしまう。
                if (tableHeartbeatMs_[t] == 0) { tableHeartbeatMs_[t] = now; }

                if ((busyTableMask & bit) != 0)
                {
                    // 対局中とされている卓。尺は半荘で1時間を超えることがあるので、
                    // 経過時間ではなくハートビートの途絶だけを根拠にする。
                    tableStaleSinceMs_[t] = 0;
                    if (now - tableHeartbeatMs_[t] < TABLE_HEARTBEAT_STALE_MS) { continue; }

                    Debug.LogWarning("[MatchmakingManager] 自浄：卓Ownerの応答が途絶えたので強制リセット"
                        + " table=" + t + " silentMs=" + (now - tableHeartbeatMs_[t]));
                    ForceResetTableState(t, FORCE_RESET_REASON_SILENT, 0);
                    // 旗を消化するまでの間に同じ判定を連発させない。
                    tableHeartbeatMs_[t] = now;
                    changed = true;
                    continue;
                }

                // 誰も座っていないのに予約だけ残っている卓。
                if (tableStaleSinceMs_[t] == 0) { tableStaleSinceMs_[t] = now; continue; }
                if (now - tableStaleSinceMs_[t] < TABLE_FLAG_STALE_MS) { continue; }

                Debug.LogWarning("[MatchmakingManager] 自浄：空卓に予約bitが残っていたので解放"
                    + " table=" + t + " staleMs=" + (now - tableStaleSinceMs_[t]));
                ForceResetTableState(t, FORCE_RESET_REASON_STALE, 0);
                tableStaleSinceMs_[t] = 0;
                changed = true;
            }
            return changed;
        }

        // 卓Owner の完了申告が永久に届かない強制リセットの旗を、最後の保険として落とす。
        //
        // 旗を立てた時点で Manager 側の状態（分類bit・ルール・busy・席の主）は ForceResetTableState が
        // 全部畳んであり、旗が立っている間は IsTableFree も false なので他から汚れ直さない。
        // つまりここでは bit を落とすだけで「素の空卓」に戻る。
        //
        // busy / matchMode が立っている間は落とさない：それは卓Owner が生きていて
        // （旗を消化する前に）対局中を再申告してきたケースで、ここで解放すると
        // 生きている対局の上へ次のマッチを流し込むことになる。その場合は計測を巻き直して待つ。
        private bool ExpireStuckForceReset(int t, int now)
        {
            if ((busyTableMask & (1 << t)) != 0 || matchMode[t] != 0)
            {
                tableForceResetSinceMs_[t] = 0;
                return false;
            }

            // Owner を引き継いだ直後は観測履歴が無い。ここで撒き直さないと
            // 「すでに3分経っている」に見えて、消化中の旗を先走って落としてしまう。
            if (tableForceResetSinceMs_[t] == 0) { tableForceResetSinceMs_[t] = now; return false; }

            // サーバー時刻は負値になり得るので、比較は必ず減算で行う（ラップアラウンド安全）。
            if (now - tableForceResetSinceMs_[t] < TABLE_FORCE_RESET_STALE_MS) { return false; }

            Debug.LogWarning("[MatchmakingManager] 自浄：強制リセットの完了申告が届かないので旗を落として空卓へ戻す"
                + " table=" + t + " pendingMs=" + (now - tableForceResetSinceMs_[t]));
            tableForceResetMask &= ~(1 << t);
            tableForceResetSinceMs_[t] = 0;
            return true;
        }

        // 卓Owner が「強制リセットを実行し終えた」と申告する。
        public void ReportForceResetDone(Mahjong tb, int generation)
        {
            int t = IndexOfTable(tb);
            if (t < 0) { return; }
            if ((tableForceResetMask & (1 << t)) == 0) { return; }
            if (generation <= 0 || generation != tableForceResetGeneration[t]) { return; }
            SendTableLifecycleRequest(TABLE_ACTION_FORCE_RESET_DONE, t, generation);
        }

        // 卓Owner が読む：この卓で途中退出を申告した席のビット。
        public int GetTableAbandonMask(Mahjong tb)
        {
            int t = IndexOfTable(tb);
            if (t < 0) { return 0; }
            return tableAbandonMask[t];
        }

        [NetworkCallable]
        public void NetApplyTableBusyReport(int t, bool busy, bool finished, bool started, int seat0, int seat1, int seat2, int seat3)
        {
            if (!Networking.IsOwner(gameObject)) { return; }
            if (t < 0 || t >= numTables_) { return; }
            var caller = NetworkCalling.CallingPlayer;
            var tb = tables[t];
            if (caller == null || !Utilities.IsValid(caller) || tb == null) { return; }
            var tableOwner = Networking.GetOwner(tb.gameObject);
            if (tableOwner == null || !Utilities.IsValid(tableOwner)
                || tableOwner.playerId != caller.playerId) { return; }

            // 自浄タイムアウト用の生存確認。卓Owner は状態が変わらなくても
            // TABLE_BUSY_HEARTBEAT_MS ごとに再申告してくるので、ここが止まったら
            // 「卓Owner が居ない／卓が寝たまま」と判断できる。
            // 必ず早期 return より前で押すこと（変化の無い申告こそがハートビートなので、
            // changed 判定の後ろに置くと一度も押されない）。
            TouchTableHeartbeat(t);

            int bit = 1 << t;
            bool current = (busyTableMask & bit) != 0;
            // 席の顔ぶれだけが変わる更新もあるので、busy の一致では早期 return しない。
            bool changed = current != busy;
            bool finishedNow = (finishedTableMask & bit) != 0;
            if (finishedNow != finished)
            {
                if (finished) { finishedTableMask |= bit; }
                else { finishedTableMask &= ~bit; }
                changed = true;
            }
            bool startedNow = (startedTableMask & bit) != 0;
            if (startedNow != started)
            {
                if (started) { startedTableMask |= bit; }
                else { startedTableMask &= ~bit; }
                changed = true;
            }
            changed = ApplySeatReport(t, seat0, seat1, seat2, seat3) || changed;
            if (!changed) { return; }
            if (current != busy)
            {
                tableYakumanAlert[t] = 0;
                tableYakumanYaku[t] = 0;
            }
            if (busy) { busyTableMask |= bit; }
            else { busyTableMask &= ~bit; }
            // 対局が始まった＝ENTRY以降で戦績はラッチ済み。着席完了の猶予は用済み。
            // 従来この解放は CleanupFinishedFreeTables が行っていたが、あれは Manager Owner の
            // 手元で卓がアクティブであることを要求する＝MainMenu にいる Owner ではまず成立しない。
            // 卓Owner の報告で落とす。
            if (busy && matchClearReason[t] == CLEAR_COMPLETED)
            {
                matchClearReason[t] = CLEAR_NONE;
                matchDeadlineMs[t] = 0;
            }
            CommitManagerState();
        }

        // 割り当てられた卓が実際には使えなかった（対局中だった）と、現地にいる当事者が申告する。
        // Owner は普段メインメニュー側にいて卓が寝て見えるため、この確認だけは現地の人しかできない。
        // 従来はこれが無く、60秒の着席タイムアウトを待つしかなかった。
        public void ReportAssignedTableUnusable(Mahjong tb)
        {
            int t = IndexOfTable(tb);
            if (t < 0 || matchMode[t] == 0 || matchId[t] == 0) { return; }

            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return; }
            bool member = false;
            for (int k = 0; k < matchMode[t]; ++k)
            {
                if (matchPlayerIds[t * 4 + k] == lp.playerId) { member = true; break; }
            }
            if (!member) { return; }
            for (int k = 0; k < matchMode[t]; ++k)
            {
                if (matchSeated[t * 4 + k]) { return; }
            }

            int currentMatchId = matchId[t];
            int now = Networking.GetServerTimeInMilliseconds();
            if (unusableReportTable_ == t && unusableReportMatchId_ == currentMatchId
                && now - unusableReportSentAtMs_ < PREPARE_REPORT_RETRY_MS) { return; }
            unusableReportTable_ = t;
            unusableReportMatchId_ = currentMatchId;
            unusableReportSentAtMs_ = now;
            SendCustomNetworkEvent(NetworkEventTarget.Owner,
                nameof(NetApplyAssignedTableUnusable), t, currentMatchId);
        }

        [NetworkCallable]
        public void NetApplyAssignedTableUnusable(int t, int requestedMatchId)
        {
            if (!Networking.IsOwner(gameObject)) { return; }
            if (t < 0 || t >= numTables_ || matchMode[t] == 0) { return; }
            if (requestedMatchId <= 0 || matchId[t] != requestedMatchId) { return; }
            var caller = NetworkCalling.CallingPlayer;
            if (caller == null || !Utilities.IsValid(caller)) { return; }

            bool member = false;
            for (int k = 0; k < matchMode[t]; ++k)
            {
                if (matchPlayerIds[t * 4 + k] == caller.playerId) { member = true; break; }
            }
            if (!member) { return; }
            for (int k = 0; k < matchMode[t]; ++k)
            {
                if (matchSeated[t * 4 + k]) { return; }
            }

            busyTableMask |= 1 << t;
            Debug.LogWarning("[MatchmakingManager] 当事者の申告で成立を解除: matchId="
                + requestedMatchId + " table=" + t);
            DissolveMatch(t, false);
            CommitManagerState();
        }

        [NetworkCallable]
        public void NetApplyTablePrepared(int t, int requestedMatchId)
        {
            if (!Networking.IsOwner(gameObject)) { return; }
            if (t < 0 || t >= numTables_ || matchMode[t] == 0) { return; }
            if (requestedMatchId <= 0 || matchId[t] != requestedMatchId) { return; }
            if (matchReadyAtMs[t] - Networking.GetServerTimeInMilliseconds() > 0) { return; }
            var caller = NetworkCalling.CallingPlayer;
            var tb = tables[t];
            if (caller == null || !Utilities.IsValid(caller) || tb == null) { return; }
            if (matchPlayerIds[t * 4] != caller.playerId) { return; }
            var tableOwner = Networking.GetOwner(tb.gameObject);
            if (tableOwner == null || !Utilities.IsValid(tableOwner)
                || tableOwner.playerId != caller.playerId) { return; }

            int bit = 1 << t;
            if ((matchPreparedMask & bit) != 0) { return; }
            matchPreparedMask |= bit;
            CommitManagerState();
        }

        public void ReportTablePrepared(Mahjong tb)
        {
            int t = IndexOfTable(tb);
            if (t < 0 || matchMode[t] == 0 || matchId[t] == 0 || IsMatchPrepared(t)) { return; }
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return; }
            if (matchPlayerIds[t * 4] != lp.playerId) { return; }
            if (!Networking.IsOwner(lp, tb.gameObject)) { return; }

            int currentMatchId = matchId[t];
            int now = Networking.GetServerTimeInMilliseconds();
            if (preparedReportTable_ == t && preparedReportMatchId_ == currentMatchId
                && now - preparedReportSentAtMs_ < PREPARE_REPORT_RETRY_MS) { return; }
            preparedReportTable_ = t;
            preparedReportMatchId_ = currentMatchId;
            preparedReportSentAtMs_ = now;
            SendCustomNetworkEvent(NetworkEventTarget.Owner,
                nameof(NetApplyTablePrepared), t, currentMatchId);
        }

        // この卓がいま使われているか（対局中、または着席誘導中）。
        //
        // 卓の gameState はカリングで寝ている間 Deserialize が止まって更新されないため、
        // 遠くのクライアントからは当てにできない。こちらは常時アクティブなので、
        // busyTableMask と matchMode だけが「寝ている卓の状況」を知る手段になる。
        // TableProximityCuller が「自分がOwnerになった使用中の卓」を起こすのに使う。
        public bool IsTableInUse(Mahjong tb)
        {
            int t = IndexOfTable(tb);
            if (t < 0) { return false; }
            if (matchMode[t] != 0) { return true; }
            // 強制リセット待ちも「使用中」。TableProximityCuller の ownerWake がこれを見るので、
            // 卓Ownerの卓が寝たまま旗を消化できない、という詰みを防ぐ。
            if ((tableForceResetMask & (1 << t)) != 0) { return true; }
            return (busyTableMask & (1 << t)) != 0;
        }

        public bool IsFreeGameTable(Mahjong tb)
        {
            int i = IndexOfTable(tb);
            if (i < 0) { return false; }
            return (freeTableMask & (1 << i)) != 0;
        }

        // 卓がMainMenuの三麻/四麻段位戦マッチングから成立した対局か。
        // 未登録卓は必ずfalse。matchModeが着席完了で0になった後もRESULTまで有効。
        public bool IsRankedGameTable(Mahjong tb)
        {
            int i = IndexOfTable(tb);
            if (i < 0) { return false; }
            return (rankedTableMask & (1 << i)) != 0;
        }

        // 段位戦のENTRYは、マッチング配列上の同じseat indexへ割り当てられた本人だけ許可する。
        // 表示側だけでなくOnEntry側からも呼び、古い表示や直接イベントによる横入りを拒否する。
        // フリー／手動／未登録卓は従来どおり任意の空席へ入れる。
        public bool CanLocalPlayerEnterTableSeat(Mahjong tb, int seat)
        {
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return false; }
            return CanPlayerEnterTableSeat(tb, seat, lp.playerId, GetMatchIdForTable(tb));
        }

        public bool CanPlayerEnterTableSeat(Mahjong tb, int seat, int playerId, int requestedMatchId)
        {
            if (tb == null || seat < 0 || seat >= 4 || playerId <= 0) { return false; }

            int t = IndexOfTable(tb);
            if (t < 0) { return requestedMatchId == 0; }

            int bit = 1 << t;
            int mode = matchMode[t];
            if (mode == 3 || mode == 4)
            {
                if (matchId[t] == 0 || requestedMatchId != matchId[t]) { return false; }
                if (IsMatchPreparing(t) || tb.numPlayers != mode || seat >= mode) { return false; }

                if ((rankedTableMask & bit) != 0)
                {
                    return matchPlayerIds[t * 4 + seat] == playerId;
                }

                if ((freeTableMask & bit) != 0)
                {
                    for (int k = 0; k < mode; ++k)
                    {
                        if (matchPlayerIds[t * 4 + k] == playerId) { return true; }
                    }
                }
                return false;
            }

            // matchMode==0 かつ戦種bitが残る卓は、全員着席済み／進行中。再ENTRYを拒否する。
            if (requestedMatchId != 0) { return false; }
            if ((rankedTableMask & bit) != 0 || (freeTableMask & bit) != 0
                || (invalidRankedTableMask & bit) != 0) { return false; }

            // ここまで来た卓＝どの戦種にも登録されていない「通常卓」。
            // 封鎖中はここを閉じる＝「対局は必ず MainMenu から始まる」が構造的に成り立ち、
            // 卓の状態は必ず Manager に登録される。スタッフだけは検証用に通す。
            return CanManuallyUseTable(playerId);
        }

        public bool CanPlayerRequestCpuEntry(Mahjong tb, int playerId, int requestedMatchId)
        {
            if (tb == null || playerId <= 0) { return false; }

            int t = IndexOfTable(tb);
            if (t < 0) { return requestedMatchId == 0; }

            int bit = 1 << t;
            int mode = matchMode[t];
            if (mode == 3 || mode == 4)
            {
                if ((freeTableMask & bit) == 0 || matchId[t] == 0
                    || requestedMatchId != matchId[t]) { return false; }
                if (IsMatchPreparing(t) || tb.numPlayers != mode) { return false; }
                return tableCpuCount[t] > 0 && tableCpuHostId[t] == playerId;
            }

            if (requestedMatchId != 0) { return false; }
            if ((rankedTableMask & bit) != 0 || (invalidRankedTableMask & bit) != 0) { return false; }
            if ((freeTableMask & bit) != 0)
            {
                return tableCpuCount[t] > 0 && tableCpuHostId[t] == playerId;
            }
            return CanManuallyUseTable(playerId);
        }

        // 卓のCPUボタンを出してよいか（表示側）。実際の受付は上の Owner 検証が決める。
        public bool CanLocalPlayerRequestCpuEntry(Mahjong tb)
        {
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return false; }
            return CanPlayerRequestCpuEntry(tb, lp.playerId, GetMatchIdForTable(tb));
        }

        // forced wallなどにより、この段位戦半荘が無効化済みか。
        public bool IsRankedGameInvalidatedTable(Mahjong tb)
        {
            int i = IndexOfTable(tb);
            if (i < 0) { return false; }
            return (invalidRankedTableMask & (1 << i)) != 0;
        }

        // 卓全体の能力あり/なし（フリー戦設定。能力システム実装後に卓側から参照する）。
        public bool GetTableAbilityOn(Mahjong tb)
        {
            int i = IndexOfTable(tb);
            if (i < 0) { return true; }
            return tableAbilityOn[i];
        }

        // 卓側から「フリー対局戦が終局した（GAMESTATE_RESULT到達）」の通知を受け、
        // 該当卓のフリー戦フラグ・ルール残骸を即時クリアする（仕様D：戦績除外は当該1戦のみ）。
        //
        // 背景：従来この掃除は CleanupFinishedFreeTables（MatchmakingのOwnerが卓に近接して
        // いる時の1秒Tick）任せだったため、終局直後に同じ卓で通常対局を始めると freeTableMask が
        // 残ったまま ENTRY_COMPLETED を通過し、その通常対局が誤って戦績除外されるエッジがあった。
        // 対局者（＝卓に近接・終局遷移を最初に知るクライアント）が RESULT 遷移の検知時点で
        // 呼ぶことで、次のエントリーが始まる前に必ず掃除が済む。
        //
        // ・局数ルール（tableRuleLength）は IsGameSet が終局を裁定した後なので消してよい。
        // ・全対局者が同時に呼んでも冪等（dirty でなければ Owner 奪取もしない）。
        // ・次マッチ割当済み（matchMode!=0）の卓には触らない（TryFormMatch の設定を守る）。
        // ・着席完了（ClearMatch COMPLETED）後〜ENTRY_COMPLETED 前は gameState==INITIAL。
        //   ここで消すと statsFreeGame_ 未ラッチで段位汚染（P1B-05）。INITIAL では拒否。
        // ・同窓の自動掃除穴は CleanupFinishedFreeTables 側で matchClearReason により塞ぐ（P1B-08）。
        // ・中断（RESULTを経ないRESYNC等）の残骸は CleanupFinishedFreeTables が拾う。
        public void NotifyFreeGameFinished(Mahjong tb)
        {
            int t = IndexOfTable(tb);
            if (t < 0 || matchMode[t] != 0) { return; }
            if (tb.gameState == Mahjong.GAMESTATE_INITIAL) { return; }

            bool dirty = (freeTableMask & (1 << t)) != 0
                || tableRuleLength[t] != RULE_LEN_HANCHAN
                || tableRuleFlags[t] != RULE_FLAG_NONE
                || tableCpuCount[t] != 0
                || tableCpuHostId[t] != 0;
            if (!dirty) { return; }
            SendTableLifecycleRequest(TABLE_ACTION_FREE_FINISHED, t);
        }

        // 卓側のGAMESTATE_RESULTから呼ぶ段位戦分類bitの冪等な終了通知。
        // Hanchan_IsFinishedでRESULT前の誤クリアを拒否する。無効bitだけは遅延RESULTの
        // 保護としてINITIAL空卓まで保持し、次の対局を割り当てる前にcleanupで落とす。
        public void NotifyRankedGameFinished(Mahjong tb)
        {
            int t = IndexOfTable(tb);
            if (t < 0 || matchMode[t] != 0) { return; }
            if (((rankedTableMask | invalidRankedTableMask) & (1 << t)) == 0) { return; }
            if (!tb.Hanchan_IsFinished()) { return; }
            SendTableLifecycleRequest(TABLE_ACTION_RANKED_FINISHED, t);
        }

        // forced wallなどのデバッグ機能が段位戦半荘へ使われた時点で、同期済み無効bitを立てる。
        // ranked bitはRESULT時の冪等後片付けまで残す。
        public void NotifyRankedGameInvalidated(Mahjong tb)
        {
            int t = IndexOfTable(tb);
            if (t < 0 || (rankedTableMask & (1 << t)) == 0) { return; }
            SendTableLifecycleRequest(TABLE_ACTION_RANKED_INVALIDATED, t);
        }

        // 全員着席後でも対局開始前に着席者が離脱した段位戦は、不成立として分類bitを落とす。
        // 対局開始後はMahjong側がCPU代打へ切り替えるため、このAPIを呼ばない。
        // 無効bitは遅延状態を見たクライアントの保存防止に使い、INITIAL空卓まで保持する。
        public void NotifyRankedGameAborted(Mahjong tb)
        {
            int t = IndexOfTable(tb);
            if (t < 0) { return; }
            int bit = 1 << t;
            if (((rankedTableMask | invalidRankedTableMask) & bit) == 0) { return; }
            SendTableLifecycleRequest(TABLE_ACTION_RANKED_ABORTED, t);
        }

        // 卓が「INITIAL かつ空席」へ戻ったのに分類bit・卓ルールの残骸が残っているか。
        // 卓Owner が ReportTableIdle を送るかどうかの判断に使う（配列読みだけ＝extern なし）。
        public bool IsTableClassificationDirty(Mahjong tb)
        {
            int t = IndexOfTable(tb);
            if (t < 0 || matchMode[t] != 0) { return false; }
            // freeState は同期配列。ここは非Ownerの卓からも毎フレーム呼ばれるので、
            // 他のアクセサと同じ粒度で null を見る（Udon は例外で止まると復帰しない）。
            if (freeState != null && freeState[t] != FREE_NONE) { return false; }
            return ((freeTableMask | rankedTableMask | invalidRankedTableMask) & (1 << t)) != 0
                || tableRuleLength[t] != RULE_LEN_HANCHAN
                || tableRuleFlags[t] != RULE_FLAG_NONE
                || tableCpuCount[t] != 0
                || tableCpuHostId[t] != 0;
        }

        // 卓Ownerが「対局が終わって空卓へ戻った」と申告する。
        // Manager Owner のローカル視界に依存せず後片付けを完了させるための経路。
        public void ReportTableIdle(Mahjong tb)
        {
            int t = IndexOfTable(tb);
            if (t < 0 || matchMode[t] != 0) { return; }
            if (freeState != null && freeState[t] != FREE_NONE) { return; }
            SendTableLifecycleRequest(TABLE_ACTION_TABLE_IDLE, t);
        }

        private void SendTableLifecycleRequest(int action, int tableIndex, int forceResetGeneration = 0)
        {
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return; }
            int requestSeq = NextLocalRequestSeq();
            if (Networking.IsOwner(gameObject))
            {
                ApplyTableLifecycleRequestAuthoritative(lp.playerId, action, tableIndex, requestSeq, forceResetGeneration);
                return;
            }
            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(NetApplyTableLifecycleRequest),
                action, tableIndex, requestSeq, forceResetGeneration);
        }

        [NetworkCallable]
        public void NetApplyTableLifecycleRequest(int action, int tableIndex, int requestSeq, int forceResetGeneration)
        {
            if (!Networking.IsOwner(gameObject)) { return; }
            var caller = NetworkCalling.CallingPlayer;
            if (caller == null || !Utilities.IsValid(caller)) { return; }
            ApplyTableLifecycleRequestAuthoritative(caller.playerId, action, tableIndex, requestSeq, forceResetGeneration);
        }

        private void ApplyTableLifecycleRequestAuthoritative(
            int playerId, int action, int tableIndex, int requestSeq, int forceResetGeneration)
        {
            if (!Networking.IsOwner(gameObject)) { return; }
            if (tableIndex < 0 || tableIndex >= numTables_) { return; }
            var tb = tables[tableIndex];
            if (tb == null) { return; }

            var tableOwner = Networking.GetOwner(tb.gameObject);
            if (tableOwner == null || !Utilities.IsValid(tableOwner)
                || tableOwner.playerId != playerId) { return; }
            if (!AcceptManagerRequestSequence(playerId, requestSeq)) { return; }

            int bit = 1 << tableIndex;
            bool changed = false;
            if (action == TABLE_ACTION_FREE_FINISHED)
            {
                // 対局が終わって卓が空いた直後に、誰かが同じ卓へ新しい部屋を開くことがある。
                // そこへ遅れて届いた終了通知を通すと、開いたばかりの部屋のルールが素に戻る
                // （freeState は消えないので「設定だけ勝手に戻る」という分かりにくい形になる）。
                if (freeState[tableIndex] != FREE_NONE) { return; }

                bool dirty = matchMode[tableIndex] == 0
                    && ((freeTableMask & bit) != 0
                        || tableRuleLength[tableIndex] != RULE_LEN_HANCHAN
                        || tableRuleFlags[tableIndex] != RULE_FLAG_NONE
                        || tableCpuCount[tableIndex] != 0
                        || tableCpuHostId[tableIndex] != 0);
                if (dirty)
                {
                    ClearFreeTableFlags(tableIndex);
                    changed = true;
                }
            }
            else if (action == TABLE_ACTION_RANKED_FINISHED)
            {
                if (matchMode[tableIndex] == 0
                    && ((rankedTableMask | invalidRankedTableMask) & bit) != 0)
                {
                    ClearRankedTableResultFlags(tableIndex);
                    changed = true;
                }
            }
            else if (action == TABLE_ACTION_RANKED_INVALIDATED)
            {
                if ((rankedTableMask & bit) != 0 && (invalidRankedTableMask & bit) == 0)
                {
                    invalidRankedTableMask |= bit;
                    changed = true;
                }
            }
            else if (action == TABLE_ACTION_RANKED_ABORTED)
            {
                if (((rankedTableMask | invalidRankedTableMask) & bit) != 0)
                {
                    invalidRankedTableMask |= bit;
                    ClearRankedTableResultFlags(tableIndex);
                    changed = true;
                }
            }
            else if (action == TABLE_ACTION_FREE_CPU_COMPLETE)
            {
                if (tableCpuHostId[tableIndex] == playerId
                    && (tableCpuCount[tableIndex] != 0 || tableCpuHostId[tableIndex] != 0))
                {
                    tableCpuCount[tableIndex] = 0;
                    tableCpuHostId[tableIndex] = 0;
                    changed = true;
                }
            }
            else if (action == TABLE_ACTION_FORCE_RESET_DONE)
            {
                if ((tableForceResetMask & bit) != 0 && forceResetGeneration > 0
                    && forceResetGeneration == tableForceResetGeneration[tableIndex])
                {
                    tableForceResetMask &= ~bit;
                    // 卓Owner が「空の INITIAL へ戻した」と申告した瞬間に、使用中・席の主も確定させる。
                    //
                    // 要求時にも落としているが、要求〜完了のあいだに卓Owner が交代すると、
                    // 新Owner の AdvanceTableBusyReport が対局中の状態を初回申告して busy が
                    // 立ち直る。そのあとリセット後の busy=false 申告まで落ちると、旗が下りても
                    // 卓が空かない（SweepBusyTables は Manager Owner の手元で卓がアクティブで
                    // あることを要求する＝MainMenu 常駐の Owner ではまず走らない）。
                    // ここは呼び元が卓Owner だと検証済みで、内容も推測ではなく実体の報告なので
                    // 「強制リセット＝卓は空」を1つの事実として確定してよい。
                    busyTableMask &= ~bit;
                    ClearSeatReport(tableIndex);
                    changed = true;
                }
            }
            else if (action == TABLE_ACTION_TABLE_IDLE)
            {
                // 卓Ownerが「INITIAL かつ空席へ戻った」と申告した。
                //
                // 分類bitの後片付けは従来 CleanupFinishedFreeTables だけが行っていたが、
                // あれは Manager Owner のローカルで卓がアクティブであることを要求する。
                // Owner は普段メインメニュー側にいて全卓が寝て見えるため、解散で立った
                // invalidRankedTableMask が永久に残り、その卓は PickFreeTable からも
                // 手動ENTRY（CanPlayerEnterTableSeat）からも外れて二度と使えなくなる。
                if (matchMode[tableIndex] == 0 && freeState[tableIndex] == FREE_NONE
                    && !IsSeatCompletionGraceActive(tableIndex))
                {
                    bool wasFree = (freeTableMask & bit) != 0;
                    bool wasRanked = ((rankedTableMask | invalidRankedTableMask) & bit) != 0;
                    bool dirty = wasFree || wasRanked
                        || tableRuleLength[tableIndex] != RULE_LEN_HANCHAN
                        || tableRuleFlags[tableIndex] != RULE_FLAG_NONE
                        || tableCpuCount[tableIndex] != 0
                        || tableCpuHostId[tableIndex] != 0;
                    if (dirty)
                    {
                        if (wasFree || !wasRanked) { ClearFreeTableFlags(tableIndex); }
                        if (wasRanked) { ClearRankedTableFlags(tableIndex); }
                        changed = true;
                    }
                }
            }
            else
            {
                return;
            }

            // 変化が無いのに commit すると stateRevision だけ進んで同期配列一式を再送する。
            // lifecycle 要求は毎回新しい requestSeq を使う＝リトライのために seq を永続化する
            // 必要が無いので、ここは落としてよい。
            if (!changed) { return; }
            CommitManagerState();
            Debug.Log("[MatchmakingManager] 卓Owner報告を反映: action=" + action
                + " table=" + tableIndex + " revision=" + stateRevision);
        }

        // 全員着席（ClearMatch COMPLETED）〜ENTRY_COMPLETED 前の保護期間中か。
        // この窓で分類bitを消すと statsFreeGame_ 未ラッチのままフリー戦が段位へ混ざる（P1B-05）。
        private bool IsSeatCompletionGraceActive(int t)
        {
            if (matchClearReason[t] != CLEAR_COMPLETED) { return false; }
            if (((freeTableMask | rankedTableMask | invalidRankedTableMask) & (1 << t)) == 0) { return false; }
            if (matchDeadlineMs[t] == 0) { return false; }
            return matchDeadlineMs[t] - Networking.GetServerTimeInMilliseconds() >= 0;
        }

        // --- 部屋の操作 ---

        private void ClearDesiredFree()
        {
            desiredFreeAction_ = FREE_DESIRE_NONE;
            desiredFreeRequestSeq_ = 0;
            desiredFreeRetryLeft_ = 0;
            desiredFreeTable_ = -1;
        }

        // フリー対局ボタン→指定した卓に部屋を作る（代表になる）。
        // 同時押しはOwner検証で先着1名（負けた側は次のOnDeserializationで代表でなくなり、メニュー側が弾く）。
        //
        // 部屋＝卓なので、ここで卓を1つ押さえる。押さえた瞬間から IsTableFree が false になり、
        // 段位戦のマッチングもその卓を選ばなくなる。
        public void RequestOpenFreeSetupAt(int tableIndex)
        {
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return; }
            if (tableIndex < 0 || tableIndex >= numTables_) { return; }
            if (GetLocalFreeRoomTable() >= 0) { return; }
            if (GetLocalStatus() != STATUS_IDLE || GetLocalMatchTable() >= 0) { return; }

            desiredFreeAction_ = FREE_DESIRE_OPEN;
            desiredFreeTable_ = tableIndex;
            desiredFreeRequestSeq_ = NextLocalRequestSeq();
            desiredFreeRetryLeft_ = DESIRED_RETRY_MAX;
            ApplyOpenFreeSetup();
        }

        // 卓おまかせ（空いている先頭の卓）。卓を選ばせない導線から呼ぶ。
        public void RequestOpenFreeSetup()
        {
            for (int t = 0; t < numTables_; ++t)
            {
                if (!CanOpenFreeRoomAt(t)) { continue; }
                RequestOpenFreeSetupAt(t);
                return;
            }
        }

        // desiredFree OPEN を同期へ反映（初回＋リトライ共通）。
        private void ApplyOpenFreeSetup()
        {
            if (desiredFreeAction_ != FREE_DESIRE_OPEN) { return; }
            SendFreeActionRequest(FREE_ACTION_OPEN, desiredFreeTable_, desiredFreeRequestSeq_);
        }

        // tableIndex は OPEN と JOIN でだけ使う（まだ部屋に居ないので自分では解決できない）。
        // それ以外のアクションは Owner 側が「要求者が入っている部屋」を引き直す＝
        // クライアントから卓番号を偽られても他人の部屋には触れない。
        private void SendFreeActionRequest(int action, int tableIndex, int requestSeq)
        {
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp) || requestSeq <= 0) { return; }
            if (Networking.IsOwner(gameObject))
            {
                ApplyFreeActionRequestAuthoritative(lp.playerId, action, tableIndex, requestSeq);
                return;
            }
            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(NetApplyFreeActionRequest),
                action, tableIndex, requestSeq);
        }

        // 自分の部屋に対する操作（設定の変更など）。卓は Owner 側で引き直すので -1 でよい。
        private void SendFreeActionRequest(int action, int requestSeq)
        {
            SendFreeActionRequest(action, -1, requestSeq);
        }

        [NetworkCallable]
        public void NetApplyFreeActionRequest(int action, int tableIndex, int requestSeq)
        {
            if (!Networking.IsOwner(gameObject)) { return; }
            var caller = NetworkCalling.CallingPlayer;
            if (caller == null || !Utilities.IsValid(caller)) { return; }
            ApplyFreeActionRequestAuthoritative(caller.playerId, action, tableIndex, requestSeq);
        }

        private void ApplyFreeActionRequestAuthoritative(
            int playerId, int action, int tableIndex, int requestSeq)
        {
            if (!Networking.IsOwner(gameObject)) { return; }
            // 【注意】FREE_ACTION_* を増やしたら、この上限も必ず一緒に上げること。
            // 上げ忘れると新しいボタンだけ黙って無反応になる（2026-09-11にCPUの強さで踏んだ）。
            if (action < FREE_ACTION_OPEN || action > FREE_ACTION_REMATCH) { return; }
            if (!AcceptManagerRequestSequence(playerId, requestSeq)) { return; }

            // どの部屋に対する操作かを決める。
            //  ・OPEN / JOIN … 要求者はまだ部屋に居ないので、要求された卓を使う（下で妥当性を見る）
            //  ・それ以外    … 要求者が入っている部屋を引き直す（卓番号を偽られても他人の部屋に触れない）
            int t = (action == FREE_ACTION_OPEN || action == FREE_ACTION_JOIN
                     || action == FREE_ACTION_REMATCH)
                ? tableIndex
                : FindFreeRoomOfId(playerId);
            if (t < 0 || t >= numTables_) { return; }

            bool host = freeState[t] != FREE_NONE && freeMemberIds[t * 4] == playerId;
            bool setupByHost = host && freeState[t] == FREE_SETUP;

            bool changed = false;
            if (action == FREE_ACTION_OPEN)
            {
                // 卓が空いていること（段位戦・他の部屋・対局中でないこと）まで見る。
                if (freeState[t] == FREE_NONE && IsTableFree(t)
                    && GetStatusOfId(playerId) == STATUS_IDLE
                    && !IsIdFreeMember(playerId)
                    && !IsIdInAnyMatch(playerId) && !IsPlayerSeatedInOngoingGame(playerId))
                {
                    freeState[t] = FREE_SETUP;
                    freeMode[t] = 4;
                    for (int i = 0; i < 4; ++i) { freeMemberIds[t * 4 + i] = 0; }
                    freeMemberIds[t * 4] = playerId;

                    // ルールの初期値は卓の配列へ直接書く。ClearFreeTableFlags の「素の値」と
                    // 揃っているのは能力ありだけが例外（既定は能力あり）。
                    tableRuleLength[t] = RULE_LEN_HANCHAN;
                    tableRuleFlags[t] = RULE_FLAG_NONE;
                    tableTurnTimerMode[t] = TURN_TIMER_8_30;
                    tableAbilityOn[t] = true;
                    tableCpuCount[t] = 0;
                    tableCpuHostId[t] = 0;

                    // 部屋を開いた時点で卓を予約する。これで段位戦の割当と排他になる。
                    freeTableMask |= 1 << t;
                    TouchFree(t);
                    changed = true;
                }
            }
            else if (action == FREE_ACTION_TOGGLE_MODE)
            {
                if (setupByHost)
                {
                    freeMode[t] = freeMode[t] == 4 ? 3 : 4;
                    if (tableCpuCount[t] > freeMode[t] - 1) { tableCpuCount[t] = freeMode[t] - 1; }
                    TouchFree(t);
                    changed = true;
                }
            }
            else if (action == FREE_ACTION_TOGGLE_ABILITY)
            {
                if (setupByHost)
                {
                    tableAbilityOn[t] = !tableAbilityOn[t];
                    TouchFree(t);
                    changed = true;
                }
            }
            else if (action == FREE_ACTION_CYCLE_LENGTH)
            {
                if (setupByHost)
                {
                    int v = tableRuleLength[t] + 1;
                    if (v > RULE_LEN_ONE) { v = RULE_LEN_HANCHAN; }
                    tableRuleLength[t] = v;
                    TouchFree(t);
                    changed = true;
                }
            }
            else if (action == FREE_ACTION_CYCLE_TIMER)
            {
                if (setupByHost)
                {
                    int v = tableTurnTimerMode[t] + 1;
                    if (v > TURN_TIMER_60_255) { v = TURN_TIMER_8_30; }
                    tableTurnTimerMode[t] = v;
                    TouchFree(t);
                    changed = true;
                }
            }
            else if (action == FREE_ACTION_CYCLE_RED_DORA)
            {
                if (setupByHost)
                {
                    int v = GetFreeRedDoraModeAt(t) + 1;
                    if (v > RED_DORA_MODE_ALL) { v = RED_DORA_MODE_NONE; }
                    SetFreeRedDoraFlags(t, v);
                    TouchFree(t);
                    changed = true;
                }
            }
            else if (action == FREE_ACTION_CYCLE_CPU_LEVEL)
            {
                if (setupByHost)
                {
                    ToggleRuleFlag(t, RULE_FLAG_CPU_STRONG);
                    TouchFree(t);
                    changed = true;
                }
            }
            else if (action == FREE_ACTION_TOGGLE_AOTENJOU)
            {
                if (setupByHost)
                {
                    int v = GetFreeAotenjouModeAt(t) + 1;
                    if (v > AOTENJOU_MODE_TRADITIONAL) { v = AOTENJOU_MODE_NONE; }
                    SetFreeAotenjouFlags(t, v);
                    TouchFree(t);
                    changed = true;
                }
            }
            else if (action == FREE_ACTION_TOGGLE_LOCALYAKU)
            {
                if (setupByHost)
                {
                    ToggleRuleFlag(t, RULE_FLAG_LOCAL_YAKU);
                    TouchFree(t);
                    changed = true;
                }
            }
            else if (action == FREE_ACTION_TOGGLE_OPENREACH)
            {
                if (setupByHost)
                {
                    ToggleRuleFlag(t, RULE_FLAG_OPEN_REACH);
                    TouchFree(t);
                    changed = true;
                }
            }
            else if (action == FREE_ACTION_TOGGLE_TSUMOLOSS)
            {
                if (setupByHost)
                {
                    ToggleRuleFlag(t, RULE_FLAG_NO_TSUMO_LOSS);
                    TouchFree(t);
                    changed = true;
                }
            }
            else if (action == FREE_ACTION_TOGGLE_BUST)
            {
                if (setupByHost)
                {
                    ToggleRuleFlag(t, RULE_FLAG_NO_BUST);
                    TouchFree(t);
                    changed = true;
                }
            }
            else if (action == FREE_ACTION_CONFIRM)
            {
                if (setupByHost)
                {
                    freeState[t] = FREE_RECRUIT;
                    WriteStatus(playerId, STATUS_FREE_RECRUIT);
                    TouchFree(t);
                    changed = true;
                }
            }
            else if (action == FREE_ACTION_ADD_CPU)
            {
                if (host && GetFreeMemberCountAt(t) + tableCpuCount[t] < freeMode[t])
                {
                    tableCpuCount[t] = tableCpuCount[t] + 1;
                    TouchFree(t);
                    changed = true;
                }
            }
            else if (action == FREE_ACTION_REMOVE_CPU)
            {
                if (host && tableCpuCount[t] > 0)
                {
                    tableCpuCount[t] = tableCpuCount[t] - 1;
                    TouchFree(t);
                    changed = true;
                }
            }
            else if (action == FREE_ACTION_CANCEL)
            {
                if (host)
                {
                    ClearFreeRoom(t);
                    changed = true;
                }
            }
            else if (action == FREE_ACTION_JOIN)
            {
                if (freeState[t] == FREE_RECRUIT && !IsIdFreeMember(playerId)
                    && !IsFreeRoomFullAt(t) && GetStatusOfId(playerId) == STATUS_IDLE
                    && !IsIdInAnyMatch(playerId) && !IsPlayerSeatedInOngoingGame(playerId))
                {
                    for (int i = 1; i < 4; ++i)
                    {
                        if (freeMemberIds[t * 4 + i] != 0) { continue; }
                        freeMemberIds[t * 4 + i] = playerId;
                        TouchFree(t);
                        changed = true;
                        break;
                    }
                }
            }
            else if (action == FREE_ACTION_LEAVE)
            {
                if (freeState[t] != FREE_NONE && !host)
                {
                    for (int i = 1; i < 4; ++i)
                    {
                        if (freeMemberIds[t * 4 + i] != playerId) { continue; }
                        freeMemberIds[t * 4 + i] = 0;
                        changed = true;
                    }
                    if (changed)
                    {
                        CompactFreeMembers(t);
                        TouchFree(t);
                    }
                }
            }
            else if (action == FREE_ACTION_START)
            {
                changed = ApplyFreeStartAuthoritative(playerId, t);
            }
            else if (action == FREE_ACTION_REMATCH)
            {
                changed = ApplyFreeRematchAuthoritative(playerId, t);
            }

            CommitManagerState();
            if (localPlayer_ != null && Utilities.IsValid(localPlayer_)
                && localPlayer_.playerId == playerId)
            {
                if (action == FREE_ACTION_OPEN && IsLocalFreeHost()) { ClearDesiredFree(); }
                if (action == FREE_ACTION_START && GetLocalMatchTable() >= 0) { ClearDesiredFree(); }
            }
            if (changed) { UpdateLocalMatchView(); }
        }

        // 成立した対局の設定と人間メンバーを保存する（「同じ面子でもう1局」用）。
        private void SnapshotFreeRematch(int t, int mode)
        {
            int packed = (mode & 15)
                | ((tableRuleLength[t] & 15) << 4)
                | ((tableTurnTimerMode[t] & 15) << 8)
                | ((tableCpuCount[t] & 15) << 12)
                | ((tableAbilityOn[t] ? 1 : 0) << 16)
                | (1 << 20);
            freeRematchPacked[t] = packed;
            freeRematchFlags[t] = tableRuleFlags[t];
            for (int i = 0; i < 4; ++i)
            {
                freeRematchMembers[t * 4 + i] = freeMemberIds[t * 4 + i];
            }
        }

        // その人が、その卓の「前回の顔ぶれ」に入っていたか。
        private bool IsIdFreeRematchMember(int t, int playerId)
        {
            if (t < 0 || t >= numTables_ || playerId <= 0) { return false; }
            if ((freeRematchPacked[t] & (1 << 20)) == 0) { return false; }
            for (int i = 0; i < 4; ++i)
            {
                if (freeRematchMembers[t * 4 + i] == playerId) { return true; }
            }
            return false;
        }

        // 同じ卓・同じ設定で部屋を建て直す。設定は決まっているので設定画面を飛ばし、
        // いきなり募集中から始める（前回の同卓者は自動で部屋に戻る）。
        private bool ApplyFreeRematchAuthoritative(int playerId, int t)
        {
            if (!IsIdFreeRematchMember(t, playerId)) { return false; }
            if (freeState[t] != FREE_NONE || matchMode[t] != 0 || !IsTableFree(t)) { return false; }
            if (GetStatusOfId(playerId) != STATUS_IDLE
                || IsIdFreeMember(playerId) || IsIdInAnyMatch(playerId)
                || IsPlayerSeatedInOngoingGame(playerId)) { return false; }

            int packed = freeRematchPacked[t];
            int mode = packed & 15;
            if (mode != 3 && mode != 4) { return false; }

            // 設定を卓へ戻す。
            freeMode[t] = mode;
            tableRuleLength[t] = (packed >> 4) & 15;
            tableTurnTimerMode[t] = (packed >> 8) & 15;
            tableCpuCount[t] = (packed >> 12) & 15;
            tableAbilityOn[t] = ((packed >> 16) & 1) != 0;
            tableRuleFlags[t] = freeRematchFlags[t];
            tableCpuHostId[t] = 0;

            // 要求者だけを代表にする。
            //
            // 【重要】前回の顔ぶれをここで自動的に引っ張ってはいけない（招待制）。
            // 入れられた側は何も押していないのに IsLocalFreeMember() が true になり、
            // ApplyStatusRequestAuthoritative の !IsIdFreeMember 判定で段位戦の待機列に
            // 並べなくなる。メニュー側も IsMatchmakingBusy() でボタンを伏せるだけなので、
            // 本人には理由が見えず、募集中AFKの15分が切れるまで拘束される。
            //
            // 部屋はこの後 FREE_RECRUIT になるので、前回の顔ぶれは卓一覧に
            // 「募集中」として見える。自分で押して入る＝合意が必ず介在する。
            for (int i = 0; i < 4; ++i) { freeMemberIds[t * 4 + i] = 0; }
            freeMemberIds[t * 4] = playerId;

            // 代表一人だけで始まるので、CPU数は入るだけにクランプする
            //（足りない分は人が入るか、代表が部屋画面でCPUを足す）。
            if (tableCpuCount[t] > mode - 1) { tableCpuCount[t] = mode - 1; }
            if (tableCpuCount[t] < 0) { tableCpuCount[t] = 0; }

            freeState[t] = FREE_RECRUIT;
            freeTableMask |= 1 << t;
            WriteStatus(playerId, STATUS_FREE_RECRUIT);
            TouchFree(t);

            // 使ったスナップショットは畳む（一覧に「もう1局」が出続けない）。
            freeRematchPacked[t] = 0;

            Debug.Log("[MatchmakingManager] フリー再戦の部屋を建て直し（代表のみ・前回の顔ぶれは自分で入る）: table=" + t
                + " mode=" + mode + " cpu=" + tableCpuCount[t]);
            return true;
        }

        // tableRuleFlags の1ビットを反転する（設定画面のトグル用）。
        private void ToggleRuleFlag(int t, int flag)
        {
            if ((tableRuleFlags[t] & flag) != 0) { tableRuleFlags[t] &= ~flag; }
            else { tableRuleFlags[t] |= flag; }
        }

        // 赤ドラは4値なので、3つのビットを一度に組み替える（既定の「3枚」はビット無し）。
        private void SetFreeRedDoraFlags(int t, int mode)
        {
            int f = tableRuleFlags[t] & ~RULE_FLAG_RED_DORA_MASK;
            if (mode == RED_DORA_MODE_NONE) { f |= RULE_FLAG_RED_DORA_NONE; }
            else if (mode == RED_DORA_MODE_FOUR) { f |= RULE_FLAG_RED_DORA_FOUR; }
            else if (mode == RED_DORA_MODE_ALL) { f |= RULE_FLAG_RED_DORA_ALL; }
            tableRuleFlags[t] = f;
        }

        // 青天井も3値。現行式／従来式の両方を同時に立てない。
        private void SetFreeAotenjouFlags(int t, int mode)
        {
            int f = tableRuleFlags[t] & ~(RULE_FLAG_AOTENJOU | RULE_FLAG_AOTENJOU_TRADITIONAL);
            if (mode == AOTENJOU_MODE_CURRENT) { f |= RULE_FLAG_AOTENJOU; }
            else if (mode == AOTENJOU_MODE_TRADITIONAL) { f |= RULE_FLAG_AOTENJOU_TRADITIONAL; }
            tableRuleFlags[t] = f;
        }

        private bool ValidateFreeStartMembers(int t)
        {
            int count = 0;
            for (int i = 0; i < 4; ++i)
            {
                int id = freeMemberIds[t * 4 + i];
                if (id == 0) { continue; }
                var p = VRCPlayerApi.GetPlayerById(id);
                if (p == null || !Utilities.IsValid(p)) { return false; }
                if (IsIdInAnyMatch(id) || IsPlayerSeatedInOngoingGame(id)) { return false; }
                for (int j = 0; j < i; ++j)
                {
                    if (freeMemberIds[t * 4 + j] == id) { return false; }
                }
                count = count + 1;
            }
            return count > 0;
        }

        // 部屋＝卓なので、成立しても卓は動かない（開いたときに押さえた卓をそのまま使う）。
        // 旧実装はここで空卓を探していたが、いまは探す必要も「満卓で開始できない」状態も無い。
        private bool ApplyFreeStartAuthoritative(int hostId, int t)
        {
            if (freeState[t] != FREE_RECRUIT || freeMemberIds[t * 4] != hostId) { return false; }
            int humans = GetFreeMemberCountAt(t);
            if (humans + tableCpuCount[t] != freeMode[t]) { return false; }
            if (!ValidateFreeStartMembers(t))
            {
                return SweepFreeRoomAt(t);
            }

            int formedMode = freeMode[t];
            int formedCpuCount = tableCpuCount[t];

            int slot = 0;
            for (int i = 0; i < 4; ++i)
            {
                if (freeMemberIds[t * 4 + i] == 0) { continue; }
                matchPlayerIds[t * 4 + slot] = freeMemberIds[t * 4 + i];
                matchSeated[t * 4 + slot] = false;
                slot = slot + 1;
            }
            for (; slot < 4; ++slot)
            {
                matchPlayerIds[t * 4 + slot] = 0;
                matchSeated[t * 4 + slot] = false;
            }

            matchMode[t] = formedMode;
            int formedMatchId = IssueMatchId(t);
            int formedAtMs = Networking.GetServerTimeInMilliseconds();
            matchReadyAtMs[t] = formedAtMs + MATCH_PREPARE_MS;
            matchDeadlineMs[t] = formedAtMs + MATCH_PREPARE_MS + SEAT_TIMEOUT_MS;
            matchPreparedMask &= ~(1 << t);
            matchClearReason[t] = CLEAR_NONE;
            // ルール（tableRuleLength / tableRuleFlags / tableTurnTimerMode / tableAbilityOn）は
            // 設定中から卓の配列に入っているので、ここで書き直すものは無い。
            rankedTableMask &= ~(1 << t);
            invalidRankedTableMask &= ~(1 << t);
            freeTableMask |= 1 << t;   // 開いた時点で立っているが、冪等に念を押す
            tableCpuHostId[t] = formedCpuCount > 0 ? hostId : 0;

            // 「もう1局」用に、この対局の設定と顔ぶれを焼いておく。
            // ClearFreeRoomState / ClearMatch より前でなければならない（どちらも顔ぶれを消す）。
            SnapshotFreeRematch(t, formedMode);

            // 卓の予約（freeTableMask とルール）は残したまま、部屋の状態だけ畳む。
            ClearFreeRoomState(t);

            Debug.Log("[MatchmakingManager] フリー対局成立: matchId=" + formedMatchId
                + " revision=" + (stateRevision + 1)
                + " table=" + t + " mode=" + formedMode
                + " members=" + BuildMatchMemberLog(t, formedMode));
            return true;
        }

        // 設定画面の操作（代表のみ・FREE_SETUP中のみ）。uGUI Button から SendCustomEvent で呼ぶ。
        public void FreeToggleMode()
        {
            if (!CanEditFreeSetup()) { return; }
            SendFreeActionRequest(FREE_ACTION_TOGGLE_MODE, NextLocalRequestSeq());
        }

        public void FreeToggleAbility()
        {
            if (!CanEditFreeSetup()) { return; }
            SendFreeActionRequest(FREE_ACTION_TOGGLE_ABILITY, NextLocalRequestSeq());
        }

        public void FreeCycleLength()
        {
            if (!CanEditFreeSetup()) { return; }
            SendFreeActionRequest(FREE_ACTION_CYCLE_LENGTH, NextLocalRequestSeq());
        }

        public void FreeCycleTurnTimer()
        {
            if (!CanEditFreeSetup()) { return; }
            SendFreeActionRequest(FREE_ACTION_CYCLE_TIMER, NextLocalRequestSeq());
        }
        public void FreeCycleRedDora()
        {
            if (!CanEditFreeSetup()) { return; }
            SendFreeActionRequest(FREE_ACTION_CYCLE_RED_DORA, NextLocalRequestSeq());
        }

        // CPUの強さ（簡単／強い）。卓単位の設定で、その卓のCPUは全員この強さで打つ。
        // 段位戦の途中退出によるCPU代打は卓のルールに関係なく常に「強い」（Mahjong側で決める）。
        public void FreeCycleCpuLevel()
        {
            if (!CanEditFreeSetup()) { return; }
            SendFreeActionRequest(FREE_ACTION_CYCLE_CPU_LEVEL, NextLocalRequestSeq());
        }

        public void FreeToggleAotenjou()
        {
            if (!CanEditFreeSetup()) { return; }
            SendFreeActionRequest(FREE_ACTION_TOGGLE_AOTENJOU, NextLocalRequestSeq());
        }

        public void FreeToggleLocalYaku()
        {
            if (!CanEditFreeSetup()) { return; }
            SendFreeActionRequest(FREE_ACTION_TOGGLE_LOCALYAKU, NextLocalRequestSeq());
        }

        public void FreeToggleOpenReach()
        {
            if (!CanEditFreeSetup()) { return; }
            SendFreeActionRequest(FREE_ACTION_TOGGLE_OPENREACH, NextLocalRequestSeq());
        }

        public void FreeToggleTsumoLoss()
        {
            if (!CanEditFreeSetup()) { return; }
            SendFreeActionRequest(FREE_ACTION_TOGGLE_TSUMOLOSS, NextLocalRequestSeq());
        }

        public void FreeToggleBust()
        {
            if (!CanEditFreeSetup()) { return; }
            SendFreeActionRequest(FREE_ACTION_TOGGLE_BUST, NextLocalRequestSeq());
        }

        // 設定決定→募集開始。代表の頭上に「プレイヤー募集中」を立てる（仕様C）。
        public void FreeConfirmSetup()
        {
            if (!CanEditFreeSetup()) { return; }
            SendFreeActionRequest(FREE_ACTION_CONFIRM, NextLocalRequestSeq());
        }

        // CPU の追加/削除（代表のみ。設定中・募集中とも可）。
        public void FreeAddCpu()
        {
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return; }
            int t = FindFreeHostRoomOfId(lp.playerId);
            if (t < 0) { return; }
            if (GetFreeMemberCountAt(t) + tableCpuCount[t] >= freeMode[t]) { return; }
            SendFreeActionRequest(FREE_ACTION_ADD_CPU, NextLocalRequestSeq());
        }

        public void FreeRemoveCpu()
        {
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return; }
            int t = FindFreeHostRoomOfId(lp.playerId);
            if (t < 0 || tableCpuCount[t] <= 0) { return; }
            SendFreeActionRequest(FREE_ACTION_REMOVE_CPU, NextLocalRequestSeq());
        }

        // 代表：設定キャンセル/部屋解散。参加者はメニューへ戻る（メニュー側Tickが検知）。
        public void FreeCancelOrDisband()
        {
            if (!IsLocalFreeHost()) { return; }
            SendFreeActionRequest(FREE_ACTION_CANCEL, NextLocalRequestSeq());
        }

        // 参加者：入室要求（確認画面の「はい」）。Owner検証で先着1名。
        // 成否はメニュー側が IsLocalFreeMember() で確認する（負け＝満員です表示）。
        public void RequestJoinFreeRoomAt(int tableIndex)
        {
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return; }
            if (tableIndex < 0 || tableIndex >= numTables_) { return; }
            if (freeState[tableIndex] != FREE_RECRUIT) { return; }
            if (GetLocalFreeRoomTable() >= 0 || IsFreeRoomFullAt(tableIndex)) { return; }
            if (GetLocalStatus() != STATUS_IDLE || GetLocalMatchTable() >= 0) { return; }
            SendFreeActionRequest(FREE_ACTION_JOIN, tableIndex, NextLocalRequestSeq());
        }

        // 「同じ面子でもう1局」が押せる卓か（卓一覧のカードがこれを見る）。
        public bool CanLocalPlayerRematchFreeRoomAt(int t)
        {
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return false; }
            if (!IsIdFreeRematchMember(t, lp.playerId)) { return false; }
            if (freeState[t] != FREE_NONE || !IsTableFree(t)) { return false; }
            return GetLocalFreeRoomTable() < 0 && GetLocalStatus() == STATUS_IDLE
                && GetLocalMatchTable() < 0;
        }

        // 同じ卓・同じ設定・同じ顔ぶれで部屋を建て直す。
        public void RequestFreeRematchAt(int tableIndex)
        {
            if (!CanLocalPlayerRematchFreeRoomAt(tableIndex)) { return; }
            SendFreeActionRequest(FREE_ACTION_REMATCH, tableIndex, NextLocalRequestSeq());
        }

        // 卓を選ばせない導線から呼ぶ（募集中の先頭の部屋へ入る）。
        public void RequestJoinFreeRoom()
        {
            for (int t = 0; t < numTables_; ++t)
            {
                if (freeState[t] != FREE_RECRUIT || IsFreeRoomFullAt(t)) { continue; }
                RequestJoinFreeRoomAt(t);
                return;
            }
        }

        // 参加者：自主退出（×ボタン。代表は FreeCancelOrDisband）。
        public void FreeLeaveRoom()
        {
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return; }
            if (GetLocalFreeRoomTable() < 0 || IsLocalFreeHost()) { return; }
            SendFreeActionRequest(FREE_ACTION_LEAVE, NextLocalRequestSeq());
        }

        // 代表：対局開始。人間＋CPU＝ちょうど人数で、開いたときに押さえた卓へ流し込む
        // （代表1人＋全CPUも可＝仕様D）。卓は最初から確保済みなので「満卓で開始できない」は起きない。
        public void FreeStart()
        {
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return; }
            int t = FindFreeHostRoomOfId(lp.playerId);
            if (t < 0 || freeState[t] != FREE_RECRUIT) { return; }
            if (GetFreeMemberCountAt(t) + tableCpuCount[t] != freeMode[t]) { return; }

            desiredFreeAction_ = FREE_DESIRE_START;
            desiredFreeTable_ = t;
            desiredFreeRequestSeq_ = NextLocalRequestSeq();
            desiredFreeRetryLeft_ = DESIRED_RETRY_MAX;
            ApplyFreeStart();
        }

        // desiredFree START を同期へ反映（初回＋リトライ共通）。
        private void ApplyFreeStart()
        {
            if (desiredFreeAction_ != FREE_DESIRE_START) { return; }
            SendFreeActionRequest(FREE_ACTION_START, desiredFreeTable_, desiredFreeRequestSeq_);
        }

        // --- 部屋の内部処理 ---

        // 自分が代表で設定中の部屋の卓index（-1=編集できない）。
        private int GetEditableFreeSetupTable()
        {
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return -1; }
            int t = FindFreeHostRoomOfId(lp.playerId);
            if (t < 0 || freeState[t] != FREE_SETUP) { return -1; }
            return t;
        }

        private bool CanEditFreeSetup()
        {
            return GetEditableFreeSetupTable() >= 0;
        }

        private void TouchFree(int t)
        {
            if (t < 0 || t >= numTables_) { return; }
            freeLastActionMs[t] = Networking.GetServerTimeInMilliseconds();
        }

        // 部屋の状態だけを消す（卓の予約とルールは残す）。成立時はこちら。
        private void ClearFreeRoomState(int t)
        {
            if (t < 0 || t >= numTables_) { return; }
            int hostId = freeMemberIds[t * 4];
            if (hostId != 0)
            {
                // 代表の頭上「プレイヤー募集中」を下ろす。
                for (int i = 0; i < MAX_TRACKED; ++i)
                {
                    if (slotPlayerId[i] == hostId && slotStatus[i] == STATUS_FREE_RECRUIT)
                    {
                        ClearSlot(i);
                    }
                }
            }
            freeState[t] = FREE_NONE;
            freeMode[t] = 0;
            freeLastActionMs[t] = 0;
            for (int i = 0; i < 4; ++i) { freeMemberIds[t * 4 + i] = 0; }
        }

        // 部屋を消して卓も解放する（解散・AFK・代表の離脱）。
        private void ClearFreeRoom(int t)
        {
            if (t < 0 || t >= numTables_) { return; }
            ClearFreeRoomState(t);
            ClearFreeTableFlags(t);
        }

        // 空きスロットを詰める（[t*4]=代表は不変）。
        private void CompactFreeMembers(int t)
        {
            int dst = 1;
            for (int i = 1; i < 4; ++i)
            {
                int id = freeMemberIds[t * 4 + i];
                freeMemberIds[t * 4 + i] = 0;
                if (id != 0)
                {
                    freeMemberIds[t * 4 + dst] = id;
                    ++dst;
                }
            }
        }

        // 離脱者の掃除（Ownerのみ）。代表の離脱＝その部屋は解散（仕様D）。
        private bool SweepFreeRoomAt(int t)
        {
            if (freeState[t] == FREE_NONE) { return false; }

            int hostId = freeMemberIds[t * 4];
            var host = hostId > 0 ? VRCPlayerApi.GetPlayerById(hostId) : null;
            if (host == null || !Utilities.IsValid(host))
            {
                ClearFreeRoom(t);
                return true;
            }

            bool changed = false;
            for (int i = 1; i < 4; ++i)
            {
                int id = freeMemberIds[t * 4 + i];
                if (id == 0) { continue; }
                var p = VRCPlayerApi.GetPlayerById(id);
                if (p == null || !Utilities.IsValid(p))
                {
                    freeMemberIds[t * 4 + i] = 0;
                    changed = true;
                }
            }
            if (changed) { CompactFreeMembers(t); }
            return changed;
        }

        private bool SweepFreeRoom()
        {
            bool changed = false;
            for (int t = 0; t < numTables_; ++t)
            {
                changed = SweepFreeRoomAt(t) || changed;
            }
            return changed;
        }

        // 設定中の無操作60秒でAFK解除（仕様D）。Ownerのみ。
        // 無操作での自動解除（Ownerのみ）。
        //  ・設定中 … 60秒。開いたまま離席されると卓が1つ死ぬので短い。
        //  ・募集中 … 15分。人が来るのを待っているのは正常な状態なので長く取るが、
        //             こちらも卓を予約したままなので無期限にはしない。
        private bool CheckFreeAfk(int nowMs)
        {
            bool changed = false;
            for (int t = 0; t < numTables_; ++t)
            {
                int state = freeState[t];
                if (state == FREE_NONE) { continue; }
                int limit = state == FREE_SETUP ? FREE_AFK_TIMEOUT_MS : FREE_RECRUIT_AFK_TIMEOUT_MS;
                if (nowMs - freeLastActionMs[t] < limit) { continue; }
                ClearFreeRoom(t);
                Debug.Log("[MatchmakingManager] フリー部屋を無操作で自動解除: table=" + t
                    + " state=" + state + " limitMs=" + limit);
                changed = true;
            }
            return changed;
        }

        // 「部屋を作れる空卓が無い」を毎Tick引き直す。
        // 旧実装は開始時に立てて解散時に落としていたが、部屋＝卓になると
        // 「他人の対局が終わって卓が空いた」でも状態が変わる＝イベント側では追えない。
        private bool RefreshFreeFullFlag()
        {
            bool full = true;
            for (int t = 0; t < numTables_; ++t)
            {
                if (CanOpenFreeRoomAt(t)) { full = false; break; }
            }
            bool current = (tablesFullMask & FULL_BIT_FREE) != 0;
            if (current == full) { return false; }
            if (full) { tablesFullMask |= FULL_BIT_FREE; }
            else { tablesFullMask &= ~FULL_BIT_FREE; }
            return true;
        }

        // フリー戦が終わって空に戻った卓の後始末（Ownerのみ）。
        // 誤クリア防止のため「ローカルでアクティブな卓のフレッシュな値」で確認できた時だけ消す。
        // （Ownerが遠くカリング中の卓は古い値の可能性があるため触らない＝次にOwnerが近づいた時か
        //   新しいマッチ成立の上書きで解消される）
        //
        // P1B-08: 着席完了（matchClearReason==COMPLETED）〜ENTRY 前は freeTableMask を消さない。
        // entry[] は参加ボタン前に全 false になり得るためガードに使わない（補助の HasAnyEntry のみ残す）。
        // ENTRY 通過（gameState!=INITIAL）で COMPLETED を落とし、その後の INITIAL 空卓掃除を可能にする。
        // 参加せず放置は FREE_ENTRY_GRACE_MS 経過後に解放する。
        private bool CleanupFinishedFreeTables()
        {
            bool changed = false;
            int now = Networking.GetServerTimeInMilliseconds();

            for (int t = 0; t < numTables_; ++t)
            {
                bool dirty = ((freeTableMask | rankedTableMask | invalidRankedTableMask) & (1 << t)) != 0 ||
                             tableRuleLength[t] != RULE_LEN_HANCHAN ||
                             tableRuleFlags[t] != RULE_FLAG_NONE ||
                             tableCpuCount[t] != 0;
                if (!dirty) { continue; }
                if (matchMode[t] != 0) { continue; } // 着席誘導中は触らない
                // 部屋が開いている卓は「予約中」であって残骸ではない。
                // ここで掃除すると、設定中/募集中の部屋が卓ごと消える。
                if (freeState[t] != FREE_NONE) { continue; }

                var tb = tables[t];
                if (tb == null) { continue; }
                if (!tb.gameObject.activeInHierarchy) { continue; }

                // フリー戦・着席完了済み：ENTRY 前保護 / ENTRY 通過で保護解除 / 猶予切れで放置解放。
                bool graceExpired = false;
                if (((freeTableMask | rankedTableMask | invalidRankedTableMask) & (1 << t)) != 0 &&
                    matchClearReason[t] == CLEAR_COMPLETED)
                {
                    if (tb.gameState != Mahjong.GAMESTATE_INITIAL)
                    {
                        // ENTRY_COMPLETED 以降＝statsFreeGame_ ラッチ済み。mask は RESULT まで残す。
                        matchClearReason[t] = CLEAR_NONE;
                        matchDeadlineMs[t] = 0;
                        changed = true;
                        continue;
                    }
                    // まだ INITIAL：猶予内はスキップ（窓を塞ぐ）。期限切れは下のクリアへ進む。
                    if (matchDeadlineMs[t] == 0 || matchDeadlineMs[t] - now >= 0)
                    {
                        continue;
                    }
                    graceExpired = true;
                }

                if (tb.gameState != Mahjong.GAMESTATE_INITIAL) { continue; }
                // 補助：誰か着席操作済みなら触らない（P1B-07 どおり numPlayers 範囲）。
                // 主ガードは上の COMPLETED＋猶予。entry 全 false の窓は主ガードが担う。
                if (HasAnyEntry(tb)) { continue; }

                // 猶予切れクリアは ClearFreeTableFlags 後に freeTableMask / CLEAR_COMPLETED が落ちるため
                // 次 Tick 以降はこの分岐に再入しない（1回限り。errorLogged_ 不要）。
                if (graceExpired)
                {
                    int elapsedMs = FREE_ENTRY_GRACE_MS - (matchDeadlineMs[t] - now);
                    Debug.Log("[Matchmaking] free entry grace expired: table=" + t + " elapsedMs=" + elapsedMs);
                }

                bool wasFree = (freeTableMask & (1 << t)) != 0;
                bool wasRanked = (rankedTableMask & (1 << t)) != 0;
                bool wasInvalidRanked = (invalidRankedTableMask & (1 << t)) != 0;
                if (wasFree || (!wasRanked && !wasInvalidRanked)) { ClearFreeTableFlags(t); }
                if (wasRanked || wasInvalidRanked) { ClearRankedTableFlags(t); }
                changed = true;
            }
            return changed;
        }

        // フリー戦のCPU自動着席（代表のローカル処理）：
        // 割当卓に接近したら、空席へ tableCpuCount 分の CPU をエントリーする。
        // 人間＋CPU＝ちょうど人数のため、どの空席を使っても人間の席は必ず残る。
        private void LocalFreeCpuDuties()
        {
            if (localPlayer_ == null || !Utilities.IsValid(localPlayer_)) { return; }
            int myId = localPlayer_.playerId;

            for (int t = 0; t < numTables_; ++t)
            {
                if (tableCpuCount[t] <= 0 || tableCpuHostId[t] != myId) { continue; }

                var tb = tables[t];
                if (tb == null || !tb.gameObject.activeInHierarchy) { continue; }
                if (Vector3.Distance(localPlayer_.GetPosition(), tb.transform.position) > beaconHideDistance) { continue; }
                if (tb.gameState != Mahjong.GAMESTATE_INITIAL) { continue; }

                var e = tb.entry;
                var sp = tb.seatPlayerId;
                if (e == null || sp == null) { continue; }

                int cpuEntries = 0;
                int emptySeat = -1;
                int maxSeat = tb.numPlayers;
                for (int s = 0; s < 4 && s < maxSeat; ++s)
                {
                    if (e[s])
                    {
                        if (sp[s] == 0) { ++cpuEntries; }
                    }
                    else if (emptySeat < 0)
                    {
                        emptySeat = s;
                    }
                }

                if (cpuEntries >= tableCpuCount[t])
                {
                    // 指示の消去もManager Ownerへ要求する。ローカルは同期配列を直接変更しない。
                    SendTableLifecycleRequest(TABLE_ACTION_FREE_CPU_COMPLETE, t);
                    continue;
                }

                if (emptySeat < 0) { continue; }
                if (emptySeat == 0) { tb._OnEntryCpuButton0(); }
                else if (emptySeat == 1) { tb._OnEntryCpuButton1(); }
                else if (emptySeat == 2) { tb._OnEntryCpuButton2(); }
                else { tb._OnEntryCpuButton3(); }
            }
        }

        // ============================================================
        // 定期処理（全クライアント1秒Tick。Owner裁定＋ローカル表示の両方を担う）
        // ============================================================

        public void _Tick()
        {
            // --- Owner裁定：タイムアウト・掃除・成立再試行（空卓が空くのはイベントで拾えないため） ---
            if (Networking.IsOwner(gameObject))
            {
                bool changed = false;
                int now = Networking.GetServerTimeInMilliseconds();
                for (int t = 0; t < numTables_; ++t)
                {
                    if (matchMode[t] == 0) { continue; }
                    if (matchDeadlineMs[t] - now < 0) // ラップ安全な差分比較
                    {
                        // 着席の自己申告が1つ落ちただけで、卓ではもう対局が始まっていることがある。
                        // そのまま解散すると進行中の段位戦に無効bitが立ち、終局しても段位が
                        // 一切変動しない（＝「マッチングは成功したのに段位が反映されない」）。
                        if (!TryCompleteSeatedMatch(t)) { DissolveMatch(t, true); }
                        changed = true;
                        continue;
                    }
                    // 準備が明けた瞬間に、卓が本当に使えるかを1回だけ確かめる。
                    changed = VerifyPreparedMatch(t, now) || changed;
                }
                changed = SweepBusyTables() || changed;
                changed = SweepMatches() || changed;
                changed = SweepFreeRoom() || changed;
                changed = CheckFreeAfk(now) || changed;
                changed = CleanupFinishedFreeTables() || changed;
                // 上の2つは卓がアクティブな時しか動けない。それでも残った詰まりを
                // 同期状態だけで拾う。これが最後の砦（スタッフを呼ばなくても戻る）。
                changed = SweepStaleTableFlags(now) || changed;
                changed = RefreshFreeFullFlag() || changed;
                changed = TryFormMatchAll() || changed;
                if (changed) { CommitManagerState(); }
            }

            // --- ローカル：着席検知・卓モード設定・ビーコン・成立/解除の後処理 ---
            UpdateLocalMatchView();
            LocalMatchDuties();
            LocalFreeCpuDuties();
            EnsurePrepareTick();

            SendCustomEventDelayedSeconds(nameof(_Tick), 1.0f);
        }

        // 成立してから自分が着席するまでの間だけ、当事者が回す高速Tick。
        // 1秒Tickのままだと卓に着いてから人数モードが揃うまで最大1秒かかり、
        // その間 ENTRY ボタンが伏せられたままに見える（＝押せないバグに見える）。
        // 着席したら自分で止まるので、常時の負荷は増えない。
        // 成立の瞬間に、準備フェーズの拍とお知らせを絶対時刻から予約する。
        //  残り3秒 → 拍 / 残り2秒 → 拍 / 残り1秒 → 拍 / 残り0秒 → お知らせ。
        //  同期の遅れで残りが3秒未満の状態で成立を観測した場合は、間に合う拍だけ鳴らす。
        private void ScheduleMatchCountdown(int t)
        {
            if (matchSfxSource == null) { return; }
            if (t < 0 || t >= numTables_) { return; }

            countdownTable_ = t;
            countdownMatchId_ = matchId[t];

            float remain = (matchReadyAtMs[t] - Networking.GetServerTimeInMilliseconds()) * 0.001f;

            if (matchCountdownClip != null)
            {
                for (int n = 3; n >= 1; --n)
                {
                    // 拍は画面の数字（切り上げ：残り2.7秒なら「3」）と揃える。
                    // 成立の観測は同期の往復の分だけ必ず遅れるので、at<0 をそのまま
                    // 捨てると「3」の拍が毎回消えて2回しか鳴らない（準備は3秒しか無い）。
                    // その数字がまだ画面に出ている間（遅れ1秒未満）は即鳴らす。
                    float at = remain - n;
                    if (at <= -1.0f) { continue; } // その数字はもう過ぎている
                    if (at < 0.0f) { at = 0.0f; }
                    SendCustomEventDelayedSeconds(nameof(_MatchCountdownBeep), at);
                }
            }

            if (matchReadyClip != null)
            {
                SendCustomEventDelayedSeconds(nameof(_MatchReadyChime), remain > 0.0f ? remain : 0.0f);
            }
        }

        // 予約したときの成立がまだ生きているか。解散後に遅延イベントだけ届いて
        // 誰もいない卓のカウントダウンが鳴るのを防ぐ。
        private bool IsCountdownStillValid()
        {
            if (countdownTable_ < 0 || countdownTable_ >= numTables_) { return false; }
            if (matchId[countdownTable_] != countdownMatchId_) { return false; }
            return GetLocalMatchTable() == countdownTable_;
        }

        public void _MatchCountdownBeep()
        {
            if (matchSfxSource == null || matchCountdownClip == null) { return; }
            if (!IsCountdownStillValid()) { return; }
            matchSfxSource.PlayOneShot(matchCountdownClip, matchCountdownVolume);
        }

        public void _MatchReadyChime()
        {
            if (matchSfxSource == null || matchReadyClip == null) { return; }
            if (!IsCountdownStillValid()) { return; }
            matchSfxSource.PlayOneShot(matchReadyClip, matchReadyVolume);
        }

        private void EnsurePrepareTick()
        {
            if (prepTickActive_) { return; }
            if (!NeedsPrepareTick()) { return; }
            prepTickActive_ = true;
            SendCustomEventDelayedSeconds(nameof(_PrepareTick), PREPARE_TICK_SECONDS);
        }

        public void _PrepareTick()
        {
            if (!NeedsPrepareTick())
            {
                prepTickActive_ = false;
                return;
            }
            LocalMatchDuties();
            SendCustomEventDelayedSeconds(nameof(_PrepareTick), PREPARE_TICK_SECONDS);
        }

        // 自分が成立中マッチの当事者で、まだ着席していないか。
        private bool NeedsPrepareTick()
        {
            if (localPlayer_ == null || !Utilities.IsValid(localPlayer_)) { return false; }
            int t = GetLocalMatchTable();
            if (t < 0) { return false; }
            for (int k = 0; k < matchMode[t]; ++k)
            {
                if (matchPlayerIds[t * 4 + k] == localPlayer_.playerId)
                {
                    return !matchSeated[t * 4 + k];
                }
            }
            return false;
        }

        // 成立中マッチの当事者としてローカルで行う仕事：
        //   1) 割当卓を起こして3秒待ち、先頭メンバーが状態・人数同期を確認する
        //   2) 準備完了後、自分の着席を検知して自己申告する
        private void LocalMatchDuties()
        {
            if (localPlayer_ == null || !Utilities.IsValid(localPlayer_))
            {
                localPlayer_ = Networking.LocalPlayer;
                if (localPlayer_ == null) { return; }
            }

            int t = GetLocalMatchTable();
            if (t < 0) { return; }
            var tb = tables[t];
            if (tb == null || !tb.gameObject.activeInHierarchy) { return; }

            int myK = -1;
            for (int i = 0; i < matchMode[t]; ++i)
            {
                if (matchPlayerIds[t * 4 + i] == localPlayer_.playerId) { myK = i; break; }
            }
            if (myK < 0) { return; }

            if (!IsMatchPrepared(t))
            {
                // 最初の3秒は卓を起こしてDeserializeを待つ。古いローカル値では判定しない。
                if (matchReadyAtMs[t] - Networking.GetServerTimeInMilliseconds() > 0) { return; }

                // 対局中またはENTRY残存なら別の卓。中央Ownerへ申告して全員を並び直す。
                if (tb.gameState != Mahjong.GAMESTATE_INITIAL || HasAnyEntry(tb))
                {
                    ReportAssignedTableUnusable(tb);
                    return;
                }

                // 成立メンバー先頭の1人だけが卓Ownerになり、人数モードを同期する。
                if (myK == 0)
                {
                    if (!Networking.IsOwner(localPlayer_, tb.gameObject)
                        || tb.numPlayers != matchMode[t])
                    {
                        tb.SetNumPlayers(matchMode[t]);
                        return;
                    }
                    ReportTablePrepared(tb);
                }
                return;
            }

            // 準備完了後に別対局が見えた場合も同じ安全経路へ戻す。
            if (tb.gameState != Mahjong.GAMESTATE_INITIAL && !IsLocalPlayerSeatedAt(tb))
            {
                ReportAssignedTableUnusable(tb);
                return;
            }

            if (!matchSeated[t * 4 + myK])
            {
                var sp = tb.seatPlayerId;
                if (sp != null)
                {
                    for (int s = 0; s < 4; ++s)
                    {
                        if (sp[s] == localPlayer_.playerId)
                        {
                            RequestLocalSeated(t);
                            break;
                        }
                    }
                }
            }
        }

        public override void OnDeserialization()
        {
            LogObservedForceResets();

            if (desiredRetryLeft_ > 0)
            {
                if (GetLocalMatchTable() >= 0 || GetLocalStatus() == desiredStatus_)
                {
                    desiredRetryLeft_ = 0;
                }
                else
                {
                    desiredRetryLeft_ = desiredRetryLeft_ - 1;
                    ApplyLocalStatus();
                    if (desiredRetryLeft_ == 0 && GetLocalStatus() != desiredStatus_)
                    {
                        Debug.LogWarning("[Matchmaking] status request retry exhausted: status="
                            + desiredStatus_ + " requestSeq=" + desiredStatusRequestSeq_);
                    }
                }
            }

            if (desiredSeatRetryLeft_ > 0 && desiredSeatTable_ >= 0)
            {
                if (!IsDesiredSeatStillValid())
                {
                    ClearDesiredSeat();
                }
                else if (IsLocalDesiredSeatApplied())
                {
                    ClearDesiredSeat();
                }
                else
                {
                    desiredSeatRetryLeft_ = desiredSeatRetryLeft_ - 1;
                    ApplyLocalSeated();
                    if (desiredSeatRetryLeft_ == 0 && desiredSeatTable_ >= 0
                        && IsDesiredSeatStillValid() && !IsLocalDesiredSeatApplied())
                    {
                        Debug.LogWarning("[Matchmaking] seat request retry exhausted: table="
                            + desiredSeatTable_ + " matchId=" + desiredSeatMatchId_);
                        ClearDesiredSeat();
                    }
                }
            }

            if (desiredFreeRetryLeft_ > 0 && desiredFreeAction_ != FREE_DESIRE_NONE)
            {
                // 判定は必ず「要求した卓」で行う。部屋が卓ごとになったので、
                // 「どこかの部屋の代表か」で見ると別の卓の部屋に引っかかって誤判定する。
                int wantT = desiredFreeTable_;
                bool haveRoom = IsLocalFreeHostOfTable(wantT);
                int wantState = GetFreeStateAt(wantT);

                if (desiredFreeAction_ == FREE_DESIRE_OPEN)
                {
                    if (haveRoom && wantState == FREE_SETUP)
                    {
                        ClearDesiredFree();
                    }
                    else if (wantState != FREE_NONE)
                    {
                        // その卓に他人の部屋が立った＝先を越された。
                        ClearDesiredFree();
                    }
                    else
                    {
                        desiredFreeRetryLeft_ = desiredFreeRetryLeft_ - 1;
                        ApplyOpenFreeSetup();
                        if (desiredFreeRetryLeft_ == 0 && desiredFreeAction_ == FREE_DESIRE_OPEN
                            && GetFreeStateAt(wantT) == FREE_NONE)
                        {
                            Debug.LogWarning("[Matchmaking] free OPEN retry exhausted: table=" + wantT);
                            ClearDesiredFree();
                        }
                    }
                }
                else if (desiredFreeAction_ == FREE_DESIRE_START)
                {
                    // 【注意】ここで IsFreeStartBlockedByFullTables() を見てはいけない。
                    // そのフラグは「部屋を作れる空卓が無い」になった（部屋＝卓）。開始する側は
                    // 自分の卓を既に押さえているので、満卓のときこそ真になり、必ず開始が捨てられる。
                    if (GetLocalMatchTable() >= 0)
                    {
                        ClearDesiredFree();
                    }
                    else if (!haveRoom || wantState != FREE_RECRUIT)
                    {
                        ClearDesiredFree();
                    }
                    else
                    {
                        desiredFreeRetryLeft_ = desiredFreeRetryLeft_ - 1;
                        ApplyFreeStart();
                        if (desiredFreeRetryLeft_ == 0 && desiredFreeAction_ == FREE_DESIRE_START
                            && GetLocalMatchTable() < 0 && IsLocalFreeHostOfTable(wantT)
                            && GetFreeStateAt(wantT) == FREE_RECRUIT)
                        {
                            Debug.LogWarning("[Matchmaking] free START retry exhausted: table=" + wantT);
                            ClearDesiredFree();
                        }
                    }
                }
            }

            UpdateLocalMatchView();
        }

        // 成立/解除のローカル後処理：成立ボイス（1回）・ビーコン表示・解除時の卓掃除アシスト。
        private void UpdateLocalMatchView()
        {
            if (localPlayer_ == null || !Utilities.IsValid(localPlayer_))
            {
                localPlayer_ = Networking.LocalPlayer;
                if (localPlayer_ == null) { return; }
            }

            int t = GetLocalMatchTable();
            bool inMatch = t >= 0;

            if (inMatch)
            {
                if (!wasInMatch_)
                {
                    // 成立の瞬間：当事者のみローカルでボイス再生（仕様B）。
                    if (matchVoiceSource != null && matchVoiceClip != null)
                    {
                        matchVoiceSource.PlayOneShot(matchVoiceClip);
                    }
                    // 待機要求は成立で用済み（リトライが列へ再追加しないように）。
                    desiredStatus_ = STATUS_IDLE;
                    desiredRetryLeft_ = 0;
                    // カウントダウン(3・2・1)→お知らせ。matchReadyAtMs は同期済みの
                    // サーバー時刻なので、残り時間から逆算すれば4人の耳で揃う。
                    // 0.25秒の _PrepareTick から鳴らすと拍が最大250msぶれるため、
                    // ここで絶対時刻から予約する（フレーム精度）。
                    ScheduleMatchCountdown(t);
                    // 準備の高速Tickは成立の瞬間から回す（次の1秒Tickを待たない）。
                    EnsurePrepareTick();
                }
                lastMatchTable_ = t;
                int myK = -1;
                for (int i = 0; i < matchMode[t]; ++i)
                {
                    if (matchPlayerIds[t * 4 + i] == localPlayer_.playerId) { myK = i; break; }
                }
                lastLocalSeated_ = myK >= 0 && matchSeated[t * 4 + myK];
                // Owner 経路は OnDeserialization リトライに入らないため、着席成功をここで確定。
                if (desiredSeatTable_ == t && lastLocalSeated_) { ClearDesiredSeat(); }
                // フリー開始：Owner なら成立観測で保留解除（非Owner は Deserialize 待ち）。
                if (desiredFreeAction_ == FREE_DESIRE_START && Networking.IsOwner(gameObject))
                {
                    ClearDesiredFree();
                }
            }
            else if (wasInMatch_)
            {
                // マッチが消えた：完了（全員着席）なら何もしない。解除なら着席済みだった本人が
                // 卓のentry残骸を掃除する（近接＝アクティブなのでメソッドが届く）。
                int lt = lastMatchTable_;
                if (lt >= 0 && lt < numTables_ && lastLocalSeated_ &&
                    matchClearReason[lt] == CLEAR_DISSOLVED)
                {
                    var tb = tables[lt];
                    if (tb != null && tb.gameObject.activeInHierarchy &&
                        tb.gameState == Mahjong.GAMESTATE_INITIAL)
                    {
                        tb._OnResync();
                    }
                }
                lastMatchTable_ = -1;
                lastLocalSeated_ = false;
                // 解散後に着席リトライが matchSeated を立て直さないよう保留を捨てる。
                ClearDesiredSeat();
            }
            wasInMatch_ = inMatch;

            // フリーオープン成功（Owner 経路用）。
            if (desiredFreeAction_ == FREE_DESIRE_OPEN
                && IsLocalFreeHostOfTable(desiredFreeTable_)
                && GetFreeStateAt(desiredFreeTable_) == FREE_SETUP
                && Networking.IsOwner(gameObject))
            {
                ClearDesiredFree();
            }
            // フリー開始保留：マッチ外かつ部屋が無効なら破棄（Deserialize 前のローカル成功は inMatch）。
            if (desiredFreeAction_ == FREE_DESIRE_START && !inMatch &&
                (!IsLocalFreeHostOfTable(desiredFreeTable_)
                 || GetFreeStateAt(desiredFreeTable_) != FREE_RECRUIT))
            {
                ClearDesiredFree();
            }

            UpdateBeacons(t);
            UpdateGuideArrow(t);
        }

        // 卓誘導の矢印：自分が当事者の卓だけ、足元からそこまで矢羽根を並べる。
        // 光柱は「どこにあるか」しか言えないが、8卓並ぶとどれが自分の柱か分かりにくい。
        // 実際の並べ替えは MatchGuideArrow が毎フレーム行う（行き先だけここで渡す）。
        private void UpdateGuideArrow(int myMatchTable)
        {
            if (guideArrow == null) { return; }

            // 成立直後の3秒カウントダウン中は誰も卓へ向かわせない（矢印を出さない）。
            if (myMatchTable < 0 || myMatchTable >= numTables_
                || matchMode[myMatchTable] == 0 || tables[myMatchTable] == null
                || IsMatchCountdownRunning(myMatchTable))
            {
                // 卓へ来たのに何もできない人だけ、逆向きに MainMenu へ送る。
                guideArrow.SetTarget(ResolveMainMenuGuideTarget());
                return;
            }

            guideArrow.SetTarget(ResolveGuideTarget(myMatchTable, tables[myMatchTable]));
        }

        // 「卓の近くにいる／その卓は使えない／自分は何の待機もしていない」の3つが揃ったときだけ、
        // MainMenu を指す。揃わなければ null（＝矢印は消える）。
        //
        // 卓の ENTRY を封鎖すると、卓へ来た人の目の前には押せるものが何も無くなる。
        // 輪っかの案内は「メインメニューで操作してください」と言えるが、
        // メインメニューがどこかは言えない。そこをこの矢印が埋める。
        private Transform ResolveMainMenuGuideTarget()
        {
            if (mainMenuGuideTarget == null) { return null; }
            if (localPlayer_ == null || !Utilities.IsValid(localPlayer_)) { return null; }

            // 待機列・部屋・対局中の人は案内しない（その人には次にやることがある）。
            if (GetLocalStatus() != STATUS_IDLE) { return null; }
            if (GetLocalFreeRoomTable() >= 0) { return null; }
            if (IsPlayerSeatedInOngoingGame(localPlayer_.playerId)) { return null; }
            // 卓に直接座れる人（スタッフ／封鎖を戻した状態）には案内しない。
            if (allowManualTableEntry) { return null; }

            Vector3 me = localPlayer_.GetPosition();
            float near = tableApproachDistance > 0.5f ? tableApproachDistance : 0.5f;
            float nearSq = near * near;
            for (int t = 0; t < numTables_; ++t)
            {
                var tb = tables[t];
                if (tb == null) { continue; }
                // 使用中の卓は「近づいたら誘導」の対象にしない。観戦しに来ただけの人を
                // メインメニューへ追い返すことになる。
                if (IsTableIndexInUse(t)) { continue; }
                // 自分が座れる卓なら案内は要らない（スタッフ判定はここに入る）。
                if (CanLocalPlayerEnterTableSeat(tb, 0)) { continue; }

                Vector3 d = tb.transform.position - me;
                d.y = 0f; // 多層ワールドなので高さは見ない（卓誘導の矢印と同じ理由）
                if (d.sqrMagnitude <= nearSq) { return mainMenuGuideTarget; }
            }
            return null;
        }

        // 誘導の的。
        //
        //   段位戦 … 座る席まで決まっている（CanLocalPlayerEnterTableSeat が
        //            matchPlayerIds[t*4+seat] を見て他席を拒否する）ので、
        //            その席の ENTRY ボタンをそのまま指す。卓の中心ではなく
        //            「自分が押すボタン」を指すので、着いてから迷わない。
        //   フリー戦 … 席は早い者勝ちで決まっていないので卓そのものを指す。
        //
        // 卓がカリングで非アクティブでも Find と position は使える（遠くから誘導するため必須）。
        private Transform ResolveGuideTarget(int t, Mahjong tb)
        {
            if (!IsRankedGameTable(tb)) { return tb.transform; }
            if (localPlayer_ == null || !Utilities.IsValid(localPlayer_)) { return tb.transform; }

            int seat = -1;
            int mode = matchMode[t];
            if (mode > 4) { mode = 4; }
            for (int k = 0; k < mode; ++k)
            {
                if (matchPlayerIds[t * 4 + k] == localPlayer_.playerId) { seat = k; break; }
            }
            if (seat < 0) { return tb.transform; }

            var btn = tb.transform.Find("Table/EntryButton" + seat.ToString());
            if (btn == null) { return tb.transform; }

            return btn;
        }

        // 卓誘導ビーコン：自分が当事者の卓だけ、接近するまで表示（仕様B：全員着席まで／接近で消える）。
        private void UpdateBeacons(int myMatchTable)
        {
            if (tableBeacons == null) { return; }

            for (int i = 0; i < numTables_ && i < tableBeacons.Length; ++i)
            {
                var beacon = tableBeacons[i];
                if (beacon == null) { continue; }

                bool show = false;
                // カウントダウン中は光柱も出さない（先にメニューで顔ぶれを見せる）。
                if (i == myMatchTable && matchMode[i] != 0 && tables[i] != null
                    && !IsMatchCountdownRunning(i))
                {
                    float dist = Vector3.Distance(localPlayer_.GetPosition(), tables[i].transform.position);
                    show = dist > beaconHideDistance;
                }

                if (beacon.activeSelf != show) { beacon.SetActive(show); }
            }
        }
    }
}
