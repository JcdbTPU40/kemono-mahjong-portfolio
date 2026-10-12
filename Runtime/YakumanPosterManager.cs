using TMPro;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    //  役満ポスター（インスタンス内へ一斉掲示）
    // ------------------------------------------------------------
    //  誰かが役満で和了すると、ワールド中の広告パネル（AdPanel）が一斉に
    //  その和了のポスターへ差し替わり、一定時間ののち広告へ戻る。
    //
    //  ポスターの中身（参照デザインどおり）:
    //    上   … 称号（役満／二倍役満／…／数え役満）
    //    右   … 役名（複合時はレア度がいちばん高い役を1つだけ。数え役満は空）
    //    下   … プレイヤー名（白帯）
    //    背景 … 和了者の使用キャラ（自画像キャラならその人が撮った写真）
    //
    //  導入（YakumanIntro）は真の役満だけ。数え役満は卓側もカットインを出さないので、
    //  パネルだけで牌が舞うと何の演出か分からなくなる＝最初からポスターを貼る。
    //
    //  ■ なぜ RenderTexture 1枚に描いて配るのか
    //    ポスターの中身は毎回変わる＝焼いたテクスチャでは出せない。かといって
    //    パネル23枚それぞれに Canvas を持たせると、掲示中ずっと23枚ぶんの
    //    Canvas とバッチが積み上がる（Quest基準では論外）。
    //    そこで「どこにも見えない場所に置いた Canvas をカメラ1台で撮り、
    //    その RenderTexture を全パネルへ配る」形にした。パネル側は既存の
    //    テクスチャ差し替え（MaterialPropertyBlock＋クロスフェード）に
    //    そのまま乗るので、パネルの負荷は普段の広告と1ミリも変わらない。
    //    カメラも撮り終えたら止める（掲示中ずっと回しっぱなしにはしない）。
    //
    //  ■ 同期（同期変数は7つ・イベント1回きり）
    //    卓Ownerが「役満が出た」をここのOwnerへ投げ、Ownerが同期変数へ焼いて配る。
    //    各クライアントは同期変数から自分で絵を組み立てる＝画像も文字列も飛ばない。
    //    掲示の開始時刻はサーバー時刻で持つので、
    //      ・全員が同時に貼られ、同時に剥がれる
    //      ・途中入室した人も「残り時間ぶん」だけ見える（貼り直しは起きない）
    //    言語も各クライアントが自分の設定で組み立てる（publisher の言語に揃わない）。
    //
    //  ■ 使い方
    //    Tools > Kemono Mahjong > Build Yakuman Poster     … 一式を生成
    //    Tools > Kemono Mahjong > Wire Yakuman Poster      … 卓・広告パネル・自画像を配線
    //    Tools > Kemono Mahjong > Validate Yakuman Poster  … 参照欠けの点検
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class YakumanPosterManager : UdonSharpBehaviour
    {
        // ------------------------------------------------------------
        // 役満の役ID。Mahjong.cs の private const と同じ値をここへ写している
        // （あちらは private なので参照できない。値を変えるときは両方直すこと）。
        // ------------------------------------------------------------
        public const int YAKU_SUKANTSU = 15;
        public const int YAKU_SUANKO = 16;
        public const int YAKU_SUANKO_TANKI = 17;
        public const int YAKU_DAISANGEN = 27;
        public const int YAKU_TSUISO = 29;
        public const int YAKU_KOKUSHIMUSO = 32;
        public const int YAKU_KOKUSHIMUSO13 = 33;
        public const int YAKU_TENHO = 37;
        public const int YAKU_CHIHO = 38;
        public const int YAKU_RYUISO = 40;
        public const int YAKU_DAISUSHI = 41;
        public const int YAKU_SHOSUSHI = 42;
        public const int YAKU_CHURENPOHTOU = 43;
        public const int YAKU_CHINROUTOU = 44;
        public const int YAKU_JUNSEICHUREN = 57;

        // 数え役満（ドラ込み13飜以上・役満役なし）のマーカー。役IDは持たないので 0 を使う
        // （0 は門前清自摸和＝役満ではないので、本物の役満IDとぶつからない）。
        // 数え役満のポスターは「称号＝数え役満／役名は空／導入は流さない」。
        public const int YAKU_KAZOE = 0;

        [Header("必須参照（Build / Wire が入れる）")]
        [Tooltip("ポスターを撮るカメラ。普段は enabled=false。撮る瞬間だけ回す。")]
        public Camera posterCamera;

        [Tooltip("カメラの出力先。これがそのまま全パネルへ貼られる。")]
        public RenderTexture posterTexture;

        [Tooltip("ポスターを貼る先。未割当ならこの機能は何もしない。")]
        public AdPanelManager adPanels;

        [Header("導入（YakumanIntro）")]
        [Tooltip("スタジオに置いた役満カットインの複製。ポスターの前にステージ映像を流す担当。\n"
            + "卓のカットインはカリングで寝るのでパネルへは流せない＝ワールド側に1本だけ持つ。")]
        public MahjongYakumanCutin introCutin;

        [Tooltip("導入の出力先。introCutin の StageCam がここへ撮る。")]
        public RenderTexture introTexture;

        [Tooltip("導入を何秒流してからポスターへ切り替えるか。Wire がカットインの実尺を入れる。\n"
            + "0以下にすると導入を飛ばして最初からポスターを貼る。")]
        public float introSeconds = 3.0f;

        [Header("ポスターの中身")]
        [Tooltip("称号（役満／二倍役満…）")]
        public TextMeshProUGUI titleText;

        [Tooltip("役名。日本語は1文字ずつ縦に積む。")]
        public TextMeshProUGUI yakuText;

        [Tooltip("役名の枠。英語のときだけ90度倒して縦組みの代わりにする。")]
        public RectTransform yakuRect;

        [Tooltip("プレイヤー名")]
        public TextMeshProUGUI nameText;

        [Tooltip("和了者のキャラ絵を出す面")]
        public RawImage portraitImage;
        public RectTransform portraitRect;

        [Tooltip("ポスター1枚ぶんの枠（キャラ絵の拡大率の基準）。Canvas のルート。")]
        public RectTransform posterRect;

        [Header("キャラ絵")]
        [Tooltip("キャラIDに対応する立ち絵。PresentationScreen と同じ配列を Wire が入れる。")]
        public Texture[] charaTextures;

        [Tooltip("キャラ絵が引けないときの絵。")]
        public Texture defaultCharaTexture;

        [Tooltip("キャラ絵の「元画像の」縦横比（幅÷高さ）。charaTextures と同じ並び。Wire が入れる。\n"
            + "立ち絵はPOTへ丸めて取り込まれている（700x1080→512x1024）ので、テクスチャの実寸から\n"
            + "比を求めると縦に伸びる。0以下ならテクスチャの実寸を使う。")]
        public float[] charaAspects;

        [Tooltip("defaultCharaTexture の元画像の縦横比。0以下ならテクスチャの実寸を使う。")]
        public float defaultCharaAspect = 0.0f;

        [Tooltip("自画像キャラの共有ストア。自画像を選んでいる人はその写真を出す。")]
        public SelfiePortraitStore selfieStore;

        [Tooltip("申告を受け付ける卓（Wire Yakuman Poster が全卓を入れる）。"
            + "受信側で「送り主がその卓の Owner で、和了者がその卓の和了者か」を確かめるのに使う。空なら一切貼らない。")]
        public Mahjong[] tables;

        [Header("言語")]
        [Tooltip("表示言語マネージャ。未割当なら日本語で出す。")]
        public KemonoLanguage languageManager;

        [Header("掲示の調整")]
        [Tooltip("1回の役満を何秒間貼っておくか。")]
        public float displaySeconds = 90.0f;

        [Tooltip("同じ人・同じ卓の連投で貼り替えが暴れないよう、これより短い間隔の申告は捨てる（秒）。")]
        public float minIntervalSeconds = 5.0f;

        [Tooltip("ポスターの縦幅に対するキャラ絵の寄せ方。0=上端合わせ（顔が切れない）／1=下端合わせ。")]
        [Range(0.0f, 1.0f)]
        public float portraitAnchorY = 0.0f;

        [Tooltip("キャラ絵を上からこれだけ下げる（px）。称号の帯にキャラの頭がかぶらないようにする。")]
        public float portraitTopMargin = 118.0f;

        [Header("撮影（普段は触らない）")]
        [Tooltip("1回の撮影でカメラを回しておく秒数。数フレーム回れば足りる。")]
        public float renderBurstSeconds = 0.15f;

        [Tooltip("掲示中に撮り直す間隔（秒）。RenderTexture の内容はアプリの中断等で"
            + "失われることがあるので、保険で撮り直す。0以下で撮り直さない。")]
        public float rerenderIntervalSeconds = 10.0f;

        // ------------------------------------------------------------
        // 同期変数。Owner だけが書き、他は OnDeserialization で読む。
        // ------------------------------------------------------------
        [UdonSynced] private int syncSeq = 0;          // 掲示の世代。0＝まだ1回も出ていない
        [UdonSynced] private int syncYakuId = 0;
        [UdonSynced] private int syncHan = 0;
        [UdonSynced] private int syncChara = 0;
        [UdonSynced] private int syncWinnerId = 0;
        [UdonSynced] private string syncWinnerName = "";
        [UdonSynced] private int syncStartMs = 0;      // サーバー時刻（ms）

        // 導入で飛ばす牌。6bitずつ詰めてある（0=無し / 1以上は牌コード+1）。
        //   syncTilesA … [0]=和了牌 [1..4]=彗星0〜3
        //   syncTilesB … [0..1]=彗星4〜5
        [UdonSynced] private int syncTilesA = 0;
        [UdonSynced] private int syncTilesB = 0;

        private const int PHASE_NONE = 0;
        private const int PHASE_INTRO = 1;
        private const int PHASE_POSTER = 2;

        private int shownSeq_ = 0;
        private bool showing_ = false;
        private int phase_ = PHASE_NONE;
        private float introEndAt_ = 0.0f;
        private float hideAt_ = 0.0f;
        private float renderOffAt_ = 0.0f;
        private float nextRenderAt_ = 0.0f;
        private int lastAcceptMs_ = 0;
        private bool hasLastAccept_ = false;

        // 和了の局面の同期より先に着いた申告を、卓ごとに確かめ直すまで預かる（Owner ローカル）。
        // 卓ごと＝ある卓の確かめられない申告（偽物・誰も立ち会えない卓）が、別の卓の本物の申告を締め出さない。
        private const int PEND_MAX = 16;
        private const float PENDING_VERIFY_SECONDS = 10.0f;
        private const float PENDING_RETRY_INTERVAL = 0.5f;
        // 立会いの依頼を出し直す間隔。立会人の端末に和了の局面の同期が届くまで数回頼む（確かめ直しの期限内）。
        private const float WITNESS_QUERY_INTERVAL = 1.5f;
        // 同じ卓の同じ局（卓の局の通し番号 handSeq）で一度掲示したら、その局の申告は受けない
        // （ダブロン役満の2人目で貼り替わらない＝先着優先）。次の局の役満は時間に関係なく受ける。
        // 通し番号は 1〜255 で一周するので、一周ぶんより十分短いこの秒数を過ぎた記録は使わない。
        private const float SAME_HAND_RECORD_SECONDS = 600.0f;
        private bool[] pendActive_ = new bool[PEND_MAX];
        private int[] pendCaller_ = new int[PEND_MAX];
        private int[] pendYaku_ = new int[PEND_MAX];
        private int[] pendHan_ = new int[PEND_MAX];
        private int[] pendChara_ = new int[PEND_MAX];
        private int[] pendWinner_ = new int[PEND_MAX];
        private int[] pendHand_ = new int[PEND_MAX];
        private int[] pendTilesA_ = new int[PEND_MAX];
        private int[] pendTilesB_ = new int[PEND_MAX];
        private float[] pendUntil_ = new float[PEND_MAX];
        private int[] pendNonce_ = new int[PEND_MAX];      // 立会いの返事を取り違えない通し番号
        private float[] pendNextQueryAt_ = new float[PEND_MAX];
        private float[] tableAcceptedAt_ = new float[PEND_MAX];
        private bool[] tableAccepted_ = new bool[PEND_MAX];
        private int[] tableAcceptedHand_ = new int[PEND_MAX];
        private float[] pendStartedAt_ = new float[PEND_MAX];
        // 手を読めたのに申告と合わなかった（卓・申告者）＝偽の申告。しばらく同じ組の申告は預からない＝偽の申告の
        // 繰り返しで、ポスター Owner の端末に遠くの卓を起こし続けさせない／全員への立会い依頼を撒かせない。
        // 読めないまま期限切れになっただけ（同期・起こすのが遅い）の本物は止めない。
        private const float FAILED_REPORT_COOLDOWN_SECONDS = 60.0f;
        private float[] failCooldownUntil_ = new float[PEND_MAX];
        private int[] failCaller_ = new int[PEND_MAX];
        // 「読めたのに合わない」を最初に見た時刻（0＝見ていない）。起こした直後の卓は前の局の同期が残っている
        // ことがあるので、1秒続いたときだけ偽の申告と判断する。
        private float[] pendMismatchSince_ = new float[PEND_MAX];
        // 確かめ直しの間、この端末で卓が起きていて局面を読めた合計秒数と、その間に和了者の和了を一度でも見たか。
        // 読めたのに一度も和了が出ないまま期限切れ＝偽の申告（本物の和了の局面は読み上げ・NEXT待ちで数秒以上続く）。
        // 読めないまま終わっただけの本物は止めない。
        private float[] pendReadableSeconds_ = new float[PEND_MAX];
        private bool[] pendSawAgari_ = new bool[PEND_MAX];
        private const float READABLE_FAKE_SECONDS = 3.0f;
        private const float MISMATCH_CONFIRM_SECONDS = 1.0f;
        // ダブロンで、和了者の手がまだ表示順を待っている間は預かりを延ばす。その上限（申告からの秒数）。
        // 偽の申告で卓を長く起こさせないよう短めに抑える（表示1人ぶんの尺に足りる程度）。
        private const float RON_REVIEW_MAX_WAIT_SECONDS = 20.0f;
        private int nonceSeq_ = 0;
        // 確かめ直しの繰り返し（_RetryPendingYakuman）が動いているか。全卓で1本に保つ。
        private bool retryRunning_ = false;
        private int lang_ = KemonoLanguage.LANG_JA;

        // いま出しているキャラ絵の縦横比。0以下＝テクスチャの実寸から求める
        // （自画像は素直な比で焼かれているのでそちら）。ResolvePortrait が入れる。
        private float portraitAspect_ = 0.0f;

        // 導入のカットインへ渡す手牌バッファ（毎回作らない）。
        private int[] introClosed_ = new int[6];
        private int[] introOpenCodes_ = new int[0];
        private bool[] introOpenFlags_ = new bool[0];

        void Start()
        {
            if (posterCamera != null)
            {
                if (posterTexture != null) { posterCamera.targetTexture = posterTexture; }
                posterCamera.enabled = false;
            }
            PullLanguage();

            // 入室時点で既に誰かの役満が貼られていることがある（同期変数は入室時に届く）。
            // OnDeserialization より先に Start が走る場合に備えて、ここでも見に行く。
            ApplyState();
        }

        // ------------------------------------------------------------
        // 卓からの申告
        // ------------------------------------------------------------

        /// <summary>
        /// 役満が出たことを卓（Owner）から申告する。誰が投げても最終的な採否は
        /// このビヘイビアの Owner が決める＝掲示は必ず1本に揃う。
        /// </summary>
        public void ReportYakuman(int yakuId, int totalHan, int chara, int winnerPlayerId,
            string winnerName, int tilesA, int tilesB, int handSeq)
        {
            if (adPanels == null) { return; }
            if (!IsPosterYaku(yakuId)) { return; }
            if (winnerPlayerId <= 0) { return; }   // CPUの和了では貼らない
            if (winnerName == null) { winnerName = ""; }

            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(NetApplyYakuman),
                yakuId, totalHan, chara, winnerPlayerId, winnerName, tilesA, tilesB, handSeq);
        }

        [NetworkCallable]
        public void NetApplyYakuman(int yakuId, int totalHan, int chara, int winnerPlayerId,
            string winnerName, int tilesA, int tilesB, int handSeq)
        {
            if (!Networking.IsOwner(gameObject)) { return; }
            if (!IsPosterYaku(yakuId)) { return; }
            if (winnerPlayerId <= 0) { return; }
            if (handSeq <= 0) { return; }

            // 送り主の資格確認。誰でもイベントは撃てるので、送信側の卓Ownerチェックだけでは足りない。
            //  ① 送り主がその卓の Owner で、和了者がその卓の席に居る（席は配牌前から同期済み＝競合しない）
            //  ② その卓がいま和了の局面で、和了者がその人で、手の中身が申告どおりの役満（役ID・翻数）
            // ②が未達なら捨てずに数秒待って確かめ直す（卓の同期より申告が先に着くことがある。申告は一度きり）。
            var caller = NetworkCalling.CallingPlayer;
            if (caller == null || !Utilities.IsValid(caller)) { return; }
            int t = FindReportTable(caller.playerId, winnerPlayerId);
            if (t < 0 || t >= PEND_MAX) { return; }

            // 同じ卓の同じ局で掲示済みなら受けない（ダブロン役満の2人目で貼り替わらない＝先着優先）。
            // 局の番号は申告者の言い値だが、下の確かめで卓の同期の番号と照合する（別の局と偽って2人目を通せない）。
            if (IsSameHandAccepted(t, handSeq)) { return; }

            if (IsAgariConfirmed(t, winnerPlayerId, yakuId, totalHan, handSeq))
            {
                AcceptYakuman(t, yakuId, totalHan, chara, winnerPlayerId, tilesA, tilesB, handSeq);
                return;
            }
            // 預かり枠は卓ごとに1件。同じ卓で確かめ直し中なら後着は捨てる（先着優先）。
            if (pendActive_[t] && Time.time <= pendUntil_[t]) { return; }
            // 少し前に確かめが取れずに終わった同じ組（卓・申告者）は預からない（その場で確かめられる申告は上で通る）。
            if (failCaller_[t] == caller.playerId && Time.time < failCooldownUntil_[t]) { return; }

            nonceSeq_ = nonceSeq_ >= 2147480000 ? 1 : nonceSeq_ + 1;
            pendActive_[t] = true;
            pendCaller_[t] = caller.playerId;
            pendYaku_[t] = yakuId;
            pendHan_[t] = totalHan;
            pendChara_[t] = chara;
            pendWinner_[t] = winnerPlayerId;
            pendHand_[t] = handSeq;
            pendTilesA_[t] = tilesA;
            pendTilesB_[t] = tilesB;
            pendNonce_[t] = nonceSeq_;
            pendNextQueryAt_[t] = 0.0f;   // 寝ている卓なら最初の確かめ直しで立会いを頼む
            pendStartedAt_[t] = Time.time;
            // 着いた時点で手を読めて合わないなら、その時刻から1秒の猶予を数える（表示が戻った直後の古い同期かも
            // しれない）。1秒続けば確かめ直しの中で打ち切って同じ組を止める。卓は起きているので起こす負荷は無い。
            pendMismatchSince_[t] = IsShowingAgariHere(t, winnerPlayerId) ? Time.time : 0.0f;
            pendReadableSeconds_[t] = 0.0f;
            pendSawAgari_[t] = false;
            pendUntil_[t] = Time.time + PENDING_VERIFY_SECONDS;
            if (!retryRunning_)
            {
                retryRunning_ = true;
                SendCustomEventDelayedSeconds(nameof(_RetryPendingYakuman), PENDING_RETRY_INTERVAL);
            }
        }

        // 和了の局面の同期が届くのを待って確かめ直す（全卓ぶんを1本の繰り返しで見る）。
        // この端末で卓が寝ているときは、①近接カリングに卓を起こさせて自分で読む（IsKeepingTableAwake）と
        // ②卓が起きている同卓者への立会いの依頼、の両方を走らせる（人間が少ない卓は②が成り立たない）。
        public void _RetryPendingYakuman()
        {
            bool owner = Networking.IsOwner(gameObject);
            bool any = false;
            for (int t = 0; t < PEND_MAX; ++t)
            {
                if (!pendActive_[t]) { continue; }
                if (!owner || tables == null || t >= tables.Length) { pendActive_[t] = false; continue; }
                if (Time.time > pendUntil_[t])
                {
                    // 確かめが取れないまま期限切れ。卓を十分読めたのに和了者の和了が一度も出なかった＝偽の申告なら
                    // 同じ組を止める（遠くの卓を起こさせ続ける繰り返しを断つ）。読めないまま終わった本物は止めない。
                    pendActive_[t] = false;
                    if (!pendSawAgari_[t] && pendReadableSeconds_[t] >= READABLE_FAKE_SECONDS)
                    {
                        failCaller_[t] = pendCaller_[t];
                        failCooldownUntil_[t] = Time.time + FAILED_REPORT_COOLDOWN_SECONDS;
                    }
                    continue;
                }

                var tb = tables[t];
                VRCPlayerApi tableOwner = null;
                if (tb != null) { tableOwner = Networking.GetOwner(tb.gameObject); }
                if (tableOwner == null || !Utilities.IsValid(tableOwner) || tableOwner.playerId != pendCaller_[t])
                {
                    pendActive_[t] = false;
                    continue;
                }

                if (IsAgariConfirmed(t, pendWinner_[t], pendYaku_[t], pendHan_[t], pendHand_[t]))
                {
                    pendActive_[t] = false;
                    AcceptYakuman(t, pendYaku_[t], pendHan_[t], pendChara_[t], pendWinner_[t], pendTilesA_[t], pendTilesB_[t], pendHand_[t]);
                    continue;
                }
                // 読めた時間と、和了者の和了（表示待ちのダブロンを含む）が出たかを記録する（期限切れの判定に使う）。
                if (tb.gameObject.activeInHierarchy)
                {
                    pendReadableSeconds_[t] += PENDING_RETRY_INTERVAL;
                    if (tb.Poster_IsShowingAgariOf(pendWinner_[t]) || tb.Poster_IsRonWinnerAwaitingReview(pendWinner_[t]))
                    {
                        pendSawAgari_[t] = true;
                    }
                }

                // その人の和了の手を読めたのに合わない＝偽の申告。1秒続いたら（起こした直後の古い同期ではない）
                // 待っても変わらないので打ち切って止める。
                if (IsShowingAgariHere(t, pendWinner_[t]))
                {
                    if (pendMismatchSince_[t] <= 0.0f) { pendMismatchSince_[t] = Time.time; }
                    else if (Time.time - pendMismatchSince_[t] >= MISMATCH_CONFIRM_SECONDS)
                    {
                        pendActive_[t] = false;
                        failCaller_[t] = pendCaller_[t];
                        failCooldownUntil_[t] = Time.time + FAILED_REPORT_COOLDOWN_SECONDS;
                        continue;
                    }
                }
                else { pendMismatchSince_[t] = 0.0f; }

                // ダブロン・トリロン：和了者は確かにロンしているが、手（handState）がまだ前の和了者の表示中。
                // 表示順が回ってくるまで預かりを延ばす（上限あり）。手の中身はその人の番でしか確かめられない。
                if (tb.gameObject.activeInHierarchy && tb.Poster_IsRonWinnerAwaitingReview(pendWinner_[t]))
                {
                    float cap = pendStartedAt_[t] + RON_REVIEW_MAX_WAIT_SECONDS;
                    float extended = Time.time + PENDING_RETRY_INTERVAL * 4.0f;
                    if (extended > cap) { extended = cap; }
                    if (extended > pendUntil_[t]) { pendUntil_[t] = extended; }
                }

                any = true;
                if (!tb.gameObject.activeInHierarchy && Time.time >= pendNextQueryAt_[t])
                {
                    pendNextQueryAt_[t] = Time.time + WITNESS_QUERY_INTERVAL;
                    SendCustomNetworkEvent(NetworkEventTarget.All, nameof(NetYakumanWitnessQuery),
                        t, pendWinner_[t], pendCaller_[t], pendNonce_[t], pendYaku_[t], pendHan_[t], pendHand_[t]);
                }
            }

            if (!any) { retryRunning_ = false; return; }
            SendCustomEventDelayedSeconds(nameof(_RetryPendingYakuman), PENDING_RETRY_INTERVAL);
        }

        // 近接カリングが「この卓を起こしておくか」を聞きに来る（TableProximityCuller._PollDistance）。
        // ポスターの Owner が、寝ている卓の申告を確かめ直している間だけ true＝卓の同期を読めるようにする。
        public bool IsKeepingTableAwake(Mahjong tb)
        {
            if (tb == null || tables == null) { return false; }
            if (!retryRunning_ || !Networking.IsOwner(gameObject)) { return false; }
            for (int t = 0; t < tables.Length && t < PEND_MAX; ++t)
            {
                if (tables[t] != tb) { continue; }
                if (!pendActive_[t] || Time.time > pendUntil_[t]) { return false; }
                // 立会人（同卓者）が居ればそちらで済むので、最初の依頼の返事を待ってから起こす。
                return Time.time - pendStartedAt_[t] >= WITNESS_QUERY_INTERVAL;
            }
            return false;
        }

        // 立会いの依頼（ポスターの Owner → 全員）。この端末で卓が起きていて、その対局の同卓者で、申告者でも
        // 和了者でもなく、自分の手元の同期で申告どおりの役満の和了が見えたら Owner へ裏書きを返す。
        [NetworkCallable]
        public void NetYakumanWitnessQuery(int t, int winnerPlayerId, int reporterId, int nonce, int yakuId, int totalHan, int handSeq)
        {
            var caller = NetworkCalling.CallingPlayer;
            var owner = Networking.GetOwner(gameObject);
            if (caller == null || owner == null || !Utilities.IsValid(caller) || caller.playerId != owner.playerId) { return; }
            var lp = Networking.LocalPlayer;
            if (lp == null || !Utilities.IsValid(lp)) { return; }
            if (lp.playerId == reporterId || lp.playerId == winnerPlayerId) { return; }
            if (tables == null || t < 0 || t >= tables.Length) { return; }
            var tb = tables[t];
            if (tb == null || !tb.gameObject.activeInHierarchy) { return; }
            if (!IsSeatedAtReportTable(t, lp.playerId)) { return; }
            if (tb.Poster_GetHandSeq() != handSeq) { return; }
            if (!tb.Poster_VerifyYakumanBy(winnerPlayerId, yakuId, totalHan)) { return; }

            // 依頼の出し直しにも毎回答える（返事が落ちても次の依頼で届く）。二重の掲示は Owner 側の照合で防ぐ。
            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(NetYakumanWitnessConfirm), t, winnerPlayerId, nonce);
        }

        // 立会いの裏書き（立会人 → ポスターの Owner）。いま確かめ直している申告そのもので、
        // 裏書きした人がその対局の同卓者（申告者・和了者以外）なら掲示する。
        [NetworkCallable]
        public void NetYakumanWitnessConfirm(int t, int winnerPlayerId, int nonce)
        {
            if (!Networking.IsOwner(gameObject)) { return; }
            if (t < 0 || t >= PEND_MAX || !pendActive_[t] || Time.time > pendUntil_[t]) { return; }
            if (winnerPlayerId != pendWinner_[t] || nonce != pendNonce_[t]) { return; }
            var caller = NetworkCalling.CallingPlayer;
            if (caller == null || !Utilities.IsValid(caller)) { return; }
            if (caller.playerId == pendCaller_[t] || caller.playerId == pendWinner_[t]) { return; }
            // 依頼は全員に届く（番号も見える）ので、卓と無関係の協力者でも返事だけは送れてしまう。
            // 裏書きできるのはその対局の同卓者だけ（Manager の同期の席申告＝寝ている卓でも読める）。
            if (!IsSeatedAtReportTable(t, caller.playerId)) { return; }

            pendActive_[t] = false;
            AcceptYakuman(t, pendYaku_[t], pendHan_[t], pendChara_[t], pendWinner_[t], pendTilesA_[t], pendTilesB_[t], pendHand_[t]);
        }

        private bool IsSameHandAccepted(int t, int handSeq)
        {
            if (t < 0 || t >= PEND_MAX || !tableAccepted_[t]) { return false; }
            if (Time.time - tableAcceptedAt_[t] >= SAME_HAND_RECORD_SECONDS) { return false; }
            return tableAcceptedHand_[t] == handSeq;
        }

        private void AcceptYakuman(int t, int yakuId, int totalHan, int chara, int winnerPlayerId, int tilesA, int tilesB, int handSeq)
        {
            // 同じ局の2人目が別経路（確かめ直し・立会い）から遅れて着いても貼り替えない。
            if (IsSameHandAccepted(t, handSeq)) { return; }
            // 同じ卓の確かめ直し中の申告（ダブロン役満のもう1人など）はここで畳み、しばらく同じ卓の申告を受けない
            // （残すと後から確認が取れた時点で貼り替わる）。
            if (t >= 0 && t < PEND_MAX) { pendActive_[t] = false; }

            // 名前は申告の文字列を信じず、和了者本人の表示名を引き直す。
            var winner = VRCPlayerApi.GetPlayerById(winnerPlayerId);
            if (winner == null || !Utilities.IsValid(winner)) { return; }
            string winnerName = winner.displayName;

            int nowMs = Networking.GetServerTimeInMilliseconds();

            // 連投防止。ダブロン役満のように同じ瞬間に2件来ることがあるので、
            // 先に着いた1件だけ通す（後着は捨てる＝貼り替えのちらつきが出ない）。
            if (hasLastAccept_)
            {
                int gapMs = nowMs - lastAcceptMs_;
                if (gapMs >= 0 && gapMs < (int)(minIntervalSeconds * 1000.0f)) { return; }
            }
            lastAcceptMs_ = nowMs;
            hasLastAccept_ = true;
            // 実際に掲示したときだけ、同じ卓のその局の受付を止める（連投防止で捨てた申告では止めない）。
            if (t >= 0 && t < PEND_MAX)
            {
                tableAccepted_[t] = true;
                tableAcceptedAt_[t] = Time.time;
                tableAcceptedHand_[t] = handSeq;
            }

            syncSeq = syncSeq + 1;
            if (syncSeq < 0) { syncSeq = 1; }   // 万一の折り返し（負値は使わない）
            syncYakuId = yakuId;
            syncHan = totalHan;
            syncChara = chara;
            syncWinnerId = winnerPlayerId;
            syncWinnerName = winnerName == null ? "" : winnerName;
            syncTilesA = tilesA;
            syncTilesB = tilesB;
            syncStartMs = nowMs;

            RequestSerialization();
            ApplyState();   // Owner には OnDeserialization が来ない
        }

        public override void OnDeserialization()
        {
            ApplyState();
        }

        // 送り主が Owner で、和了者がその席に居る卓の添え字（無ければ -1）。
        // 1人が複数卓の Owner を兼ねることもあるので全卓を見る。
        //
        // 寝ている卓（近接カリングで非アクティブ）のメソッドは呼ばない：非アクティブな Udon への
        // 呼び出しは届かないことがある。寝ている卓は、常時アクティブな MatchmakingManager の
        // 同期（使用中＋席申告）で見る。卓の matchmaking はフィールド読みなので寝ていても取れる。
        private int FindReportTable(int callerId, int winnerPlayerId)
        {
            if (tables == null) { return -1; }
            for (int i = 0; i < tables.Length; ++i)
            {
                var tb = tables[i];
                if (tb == null) { continue; }
                var owner = Networking.GetOwner(tb.gameObject);
                if (owner == null || !Utilities.IsValid(owner) || owner.playerId != callerId) { continue; }

                // 起きている卓は実物の席を見る。ただし表示復帰の直後はローカルの同期がまだ古いことがあるので、
                // 常時アクティブな Manager の席申告でも認める（どちらかで座っていれば可）。
                if (tb.gameObject.activeInHierarchy && tb.IsPlayerSeatedAtTable(winnerPlayerId)) { return i; }
                if (IsSeatedAtReportTable(i, winnerPlayerId)) { return i; }
            }
            return -1;
        }

        // playerId が卓 tables[i] で対局中の席に居るか（Manager の同期の席申告）。
        private bool IsSeatedAtReportTable(int i, int playerId)
        {
            if (tables == null || i < 0 || i >= tables.Length || playerId <= 0) { return false; }
            var tb = tables[i];
            if (tb == null) { return false; }
            var mm = tb.matchmaking;
            if (mm == null) { return false; }
            int t = mm.GetTableIndexOf(tb);
            if (t < 0 || !mm.IsTableInUse(tb)) { return false; }
            for (int s = 0; s < 4; ++s)
            {
                if (mm.GetTableSeatIdAt(t, s) == playerId) { return true; }
            }
            return false;
        }

        // この端末で、その卓がいま和了の局面で、和了者が winnerPlayerId で、手の中身が申告どおりの役満
        // （役ID・翻数／数え役満）だと確かめられるか。寝ている卓は局面を読めない（同期が止まっている）ので false
        // ＝卓を起こして読み直すか、立会い（NetYakumanWitnessQuery）に回す。
        // この端末で卓が起きていて、いま handState に載っているのがその人の和了の手か（読める状態か）。
        private bool IsShowingAgariHere(int t, int winnerPlayerId)
        {
            if (tables == null || t < 0 || t >= tables.Length) { return false; }
            var tb = tables[t];
            if (tb == null || !tb.gameObject.activeInHierarchy) { return false; }
            return tb.Poster_IsShowingAgariOf(winnerPlayerId);
        }

        private bool IsAgariConfirmed(int t, int winnerPlayerId, int yakuId, int totalHan, int handSeq)
        {
            if (tables == null || t < 0 || t >= tables.Length) { return false; }
            var tb = tables[t];
            if (tb == null || !tb.gameObject.activeInHierarchy) { return false; }
            if (tb.Poster_GetHandSeq() != handSeq) { return false; }
            return tb.Poster_VerifyYakumanBy(winnerPlayerId, yakuId, totalHan);
        }

        // ------------------------------------------------------------
        // 掲示（導入 → ポスター）
        // ------------------------------------------------------------

        private void ApplyState()
        {
            if (syncSeq == 0 || syncSeq == shownSeq_) { return; }
            if (adPanels == null) { return; }

            shownSeq_ = syncSeq;

            float elapsed = ElapsedSeconds(syncStartMs);
            // 数え役満は導入を流さない（卓側もカットインを出していないので、
            // パネルだけで牌が舞うと何の演出か分からなくなる）。
            float intro = IsKazoe() ? 0.0f : ResolveIntroSeconds();

            // 全体の尺＝導入＋掲示。途中入室した人はその残りだけ見る（頭から流し直さない）。
            float remain = intro + displaySeconds - elapsed;
            if (remain <= 0.5f)
            {
                if (showing_) { Hide(); }
                return;
            }

            BuildPoster();
            showing_ = true;
            hideAt_ = Time.time + remain;

            // 導入を流すのは「和了とほぼ同時に受け取った人」だけ。数秒遅れて届いた人にも
            // 頭から流すと、卓の演出が終わったあとにパネルだけで導入が走ることになる。
            bool playIntro = intro > 0.01f && elapsed < 0.5f;

            if (playIntro) { StartIntro(intro); }
            else { StartPoster(); }
        }

        private float ResolveIntroSeconds()
        {
            if (introCutin == null || introTexture == null) { return 0.0f; }
            return Mathf.Max(0.0f, introSeconds);
        }

        // 導入（YakumanIntro）。スタジオに置いたカットインの複製を回し、その
        // ステージ映像をパネルへ流す。卓のカットインはカリングで寝るので使えない。
        private void StartIntro(float intro)
        {
            phase_ = PHASE_INTRO;
            introEndAt_ = Time.time + intro;

            introCutin.SetWinnerPlayerId(syncWinnerId);

            // 彗星として飛ぶ牌。詰めてある順に取り出す（空きは飛ばす）。
            int n = 0;
            for (int i = 0; i < introClosed_.Length; ++i)
            {
                int code = i < 4
                    ? DecodeTile(syncTilesA, i + 1)
                    : DecodeTile(syncTilesB, i - 4);
                if (code < 0) { continue; }
                introClosed_[n] = code;
                ++n;
            }

            introCutin.SetHandData(
                introClosed_, n, DecodeTile(syncTilesA, 0),
                introOpenCodes_, introOpenFlags_, introOpenFlags_, 0, syncChara);

            introCutin.Play(syncYakuId, YakuName(syncYakuId), RankLabel(syncHan));

            // 貼るのは2フレーム後。今フレームの RenderTexture はまだ前回の絵なので、
            // 先に貼ると一瞬それが見える。
            SendCustomEventDelayedFrames(nameof(_ShowIntroPanels), 2);
        }

        public void _ShowIntroPanels()
        {
            if (!showing_ || phase_ != PHASE_INTRO) { return; }
            if (adPanels == null || introTexture == null) { return; }
            adPanels.ShowPosterOverride(introTexture);
        }

        private void StartPoster()
        {
            // 導入の残り（役名の書き出し以降）は誰も見ないので畳む。
            if (phase_ == PHASE_INTRO && introCutin != null) { introCutin.HideImmediate(); }

            phase_ = PHASE_POSTER;

            if (posterCamera != null)
            {
                posterCamera.enabled = true;
                renderOffAt_ = Time.time + Mathf.Max(0.05f, renderBurstSeconds);
                nextRenderAt_ = rerenderIntervalSeconds > 0.0f
                    ? Time.time + rerenderIntervalSeconds
                    : 0.0f;
            }

            SendCustomEventDelayedFrames(nameof(_ShowPosterPanels), 2);
        }

        public void _ShowPosterPanels()
        {
            if (!showing_ || phase_ != PHASE_POSTER) { return; }
            if (adPanels == null || posterTexture == null) { return; }
            adPanels.ShowPosterOverride(posterTexture);
        }

        private void Hide()
        {
            showing_ = false;
            phase_ = PHASE_NONE;
            if (posterCamera != null) { posterCamera.enabled = false; }
            if (adPanels != null) { adPanels.ClearPosterOverride(); }
        }

        void Update()
        {
            if (!showing_) { return; }

            float now = Time.time;

            if (phase_ == PHASE_INTRO)
            {
                // 導入が終わったらポスターへ渡す。パネル側は広告と同じクロスフェードで溶ける。
                // 導入中はカットインの StageCam が回っているので、ポスターのカメラは回さない。
                if (now >= introEndAt_) { StartPoster(); }
                return;
            }

            if (posterCamera != null)
            {
                if (posterCamera.enabled)
                {
                    if (now >= renderOffAt_) { posterCamera.enabled = false; }
                }
                else if (nextRenderAt_ > 0.0f && now >= nextRenderAt_)
                {
                    // 保険の撮り直し。数フレームだけ回す。
                    posterCamera.enabled = true;
                    renderOffAt_ = now + Mathf.Max(0.05f, renderBurstSeconds);
                    nextRenderAt_ = now + rerenderIntervalSeconds;
                }
            }

            if (now >= hideAt_) { Hide(); }
        }

        // 6bit詰めの牌コードを1つ取り出す（0=無し／1以上は牌コード+1）。
        private int DecodeTile(int pack, int slot)
        {
            int v = (pack >> (6 * slot)) & 63;
            return v - 1;
        }

        // ------------------------------------------------------------
        // ポスターを組む
        // ------------------------------------------------------------

        private void BuildPoster()
        {
            PullLanguage();

            if (titleText != null)
            {
                titleText.text = IsKazoe()
                    ? (lang_ == KemonoLanguage.LANG_JA ? "数え役満"
                        : lang_ == KemonoLanguage.LANG_KO ? "헤아림 역만"
                        : lang_ == KemonoLanguage.LANG_ZHS ? "累计役满"
                        : lang_ == KemonoLanguage.LANG_ZHT ? "累計役滿" : "KAZOE YAKUMAN")
                    : RankLabel(syncHan);
            }

            if (yakuText != null)
            {
                string name = YakuName(syncYakuId);
                // 韓国語（ハングル）・中国語は日本語と同じく1文字ずつ積んでも読める。
                bool vertical = lang_ != KemonoLanguage.LANG_EN;
                yakuText.text = vertical ? Stack(name) : name;
                if (yakuRect != null)
                {
                    // 英語（ローマ字）は1文字ずつ積むと読めないので、枠ごと倒して縦に流す。
                    yakuRect.localRotation = vertical
                        ? Quaternion.identity
                        : Quaternion.Euler(0.0f, 0.0f, -90.0f);
                }
            }

            if (nameText != null) { nameText.text = syncWinnerName; }

            ApplyPortrait(ResolvePortrait(syncChara, syncWinnerId));
        }

        // キャラ絵はポスターの短辺に合わせて「はみ出す方」で拡大する（cover）。
        // 縦横比の違う写真でも余白が出ない。顔が切れないよう既定は上端合わせ。
        private void ApplyPortrait(Texture tex)
        {
            if (portraitImage == null) { return; }

            portraitImage.texture = tex;
            portraitImage.enabled = tex != null;
            if (tex == null || portraitRect == null || posterRect == null) { return; }

            float pw = posterRect.rect.width;
            // 上端は称号のぶん空ける。参照デザインどおり「黒地の上に称号、その下からキャラ」。
            float ph = posterRect.rect.height - portraitTopMargin;
            if (pw <= 0.0f || ph <= 0.0f) { return; }

            // 縦横比は「元画像の比」が正（テクスチャはPOTへ丸められていることがある）。
            // 分かっていないときだけテクスチャの実寸から求める。
            float aspect = portraitAspect_;
            if (aspect <= 0.0f)
            {
                float tw = tex.width;
                float th = tex.height;
                if (tw <= 0.0f || th <= 0.0f) { return; }
                aspect = tw / th;
            }
            if (aspect <= 0.0f) { return; }

            float w = pw;
            float h = pw / aspect;
            if (h < ph)
            {
                h = ph;
                w = ph * aspect;
            }

            portraitRect.anchorMin = new Vector2(0.5f, 1.0f);
            portraitRect.anchorMax = new Vector2(0.5f, 1.0f);
            portraitRect.pivot = new Vector2(0.5f, 1.0f);
            portraitRect.sizeDelta = new Vector2(w, h);
            portraitRect.anchoredPosition = new Vector2(
                0.0f, -portraitTopMargin + (h - ph) * portraitAnchorY);
        }

        // 自画像キャラ（CHARA_SELFIE）を選んでいる人は、その人が PlayerData へ焼いた
        // 写真を使う。写真が無い／退出済みなら既定のキャラ絵へ落ちる。
        private Texture ResolvePortrait(int chara, int playerId)
        {
            // 自画像は撮ったままの比で焼かれている＝テクスチャの実寸が正しい。
            portraitAspect_ = 0.0f;

            if (selfieStore != null && chara == PlayerStatsStore.CHARA_SELFIE && playerId > 0)
            {
                var player = VRCPlayerApi.GetPlayerById(playerId);
                if (player != null && player.IsValid())
                {
                    var selfie = selfieStore.GetPortraitOf(player);
                    if (selfie != null) { return selfie; }
                }
            }

            if (charaTextures != null && chara >= 0 && chara < charaTextures.Length
                && charaTextures[chara] != null)
            {
                if (charaAspects != null && chara < charaAspects.Length)
                {
                    portraitAspect_ = charaAspects[chara];
                }
                return charaTextures[chara];
            }

            portraitAspect_ = defaultCharaAspect;
            return defaultCharaTexture;
        }

        // ------------------------------------------------------------
        // 文言
        // ------------------------------------------------------------

        // 1文字ずつ改行して縦組みにする。TextMeshPro には縦書きが無いので、
        // 行を積むことで代用する（漢字だけなので回転は要らない）。
        private string Stack(string s)
        {
            if (s == null || s.Length <= 1) { return s; }

            string result = s.Substring(0, 1);
            for (int i = 1; i < s.Length; ++i)
            {
                result = result + "\n" + s.Substring(i, 1);
            }
            return result;
        }

        // 称号。あがり画面・カットインと同じ言い回しに揃える。
        private string RankLabel(int totalHan)
        {
            bool ja = lang_ == KemonoLanguage.LANG_JA;
            bool ko = lang_ == KemonoLanguage.LANG_KO;
            bool zhs = lang_ == KemonoLanguage.LANG_ZHS;
            bool zht = lang_ == KemonoLanguage.LANG_ZHT;

            if (totalHan >= 13 * 4)
            {
                int n = totalHan / 13;
                return ja
                    ? (MultiplierJa(n) + "倍役満")
                    : ko ? (n.ToString() + "배 역만")
                    : zhs ? (MultiplierJa(n) + "倍役满")
                    : zht ? (MultiplierJa(n) + "倍役滿")
                    : (n.ToString() + "-COUNT YAKUMAN");
            }
            if (totalHan >= 13 * 3) { return ja ? "三倍役満" : ko ? "트리플 역만" : zhs ? "三倍役满" : zht ? "三倍役滿" : "TRIPLE YAKUMAN"; }
            if (totalHan >= 13 * 2) { return ja ? "二倍役満" : ko ? "더블 역만" : zhs ? "两倍役满" : zht ? "兩倍役滿" : "DOUBLE YAKUMAN"; }
            return ja ? "役満" : ko ? "역만" : zhs ? "役满" : zht ? "役滿" : "YAKUMAN";
        }

        private string MultiplierJa(int n)
        {
            if (n == 4) { return "四"; }
            if (n == 5) { return "五"; }
            if (n == 6) { return "六"; }
            return n.ToString();
        }

        private string YakuName(int yakuId)
        {
            bool ja = lang_ == KemonoLanguage.LANG_JA;
            if (lang_ == KemonoLanguage.LANG_KO) { return YakuNameKo(yakuId); }
            if (lang_ == KemonoLanguage.LANG_ZHS) { return YakuNameZh(yakuId, false); }
            if (lang_ == KemonoLanguage.LANG_ZHT) { return YakuNameZh(yakuId, true); }

            if (yakuId == YAKU_KOKUSHIMUSO) { return ja ? "国士無双" : "KOKUSHIMUSOU"; }
            if (yakuId == YAKU_KOKUSHIMUSO13) { return ja ? "国士無双十三面待ち" : "KOKUSHIMUSOU 13"; }
            if (yakuId == YAKU_SUANKO) { return ja ? "四暗刻" : "SUUANKOU"; }
            if (yakuId == YAKU_SUANKO_TANKI) { return ja ? "四暗刻単騎" : "SUUANKOU TANKI"; }
            if (yakuId == YAKU_DAISANGEN) { return ja ? "大三元" : "DAISANGEN"; }
            if (yakuId == YAKU_SHOSUSHI) { return ja ? "小四喜" : "SHOUSUUSHI"; }
            if (yakuId == YAKU_DAISUSHI) { return ja ? "大四喜" : "DAISUUSHI"; }
            if (yakuId == YAKU_TSUISO) { return ja ? "字一色" : "TSUUIISOU"; }
            if (yakuId == YAKU_RYUISO) { return ja ? "緑一色" : "RYUUIISOU"; }
            if (yakuId == YAKU_CHINROUTOU) { return ja ? "清老頭" : "CHINROUTOU"; }
            if (yakuId == YAKU_CHURENPOHTOU) { return ja ? "九蓮宝燈" : "CHUUREN"; }
            if (yakuId == YAKU_JUNSEICHUREN) { return ja ? "純正九蓮宝燈" : "JUNSEI CHUUREN"; }
            if (yakuId == YAKU_SUKANTSU) { return ja ? "四槓子" : "SUUKANTSU"; }
            if (yakuId == YAKU_TENHO) { return ja ? "天和" : "TENHOU"; }
            if (yakuId == YAKU_CHIHO) { return ja ? "地和" : "CHIIHOU"; }
            return "";
        }

        // 韓国語の役名。雀魂の韓国語版の読み（漢字語＋日本語の音写）に揃える。
        private string YakuNameKo(int yakuId)
        {
            if (yakuId == YAKU_KOKUSHIMUSO) { return "국사무쌍"; }
            if (yakuId == YAKU_KOKUSHIMUSO13) { return "국사무쌍 13면 대기"; }
            if (yakuId == YAKU_SUANKO) { return "스안커"; }
            if (yakuId == YAKU_SUANKO_TANKI) { return "스안커 단기"; }
            if (yakuId == YAKU_DAISANGEN) { return "대삼원"; }
            if (yakuId == YAKU_SHOSUSHI) { return "소사희"; }
            if (yakuId == YAKU_DAISUSHI) { return "대사희"; }
            if (yakuId == YAKU_TSUISO) { return "자일색"; }
            if (yakuId == YAKU_RYUISO) { return "녹일색"; }
            if (yakuId == YAKU_CHINROUTOU) { return "청노두"; }
            if (yakuId == YAKU_CHURENPOHTOU) { return "구련보등"; }
            if (yakuId == YAKU_JUNSEICHUREN) { return "순정구련보등"; }
            if (yakuId == YAKU_SUKANTSU) { return "스깡쯔"; }
            if (yakuId == YAKU_TENHO) { return "천화"; }
            if (yakuId == YAKU_CHIHO) { return "지화"; }
            return "";
        }

        // 中国語の役名（雀魂の中国語版の呼び方）。traditional=true で繁体字。
        private string YakuNameZh(int yakuId, bool traditional)
        {
            if (yakuId == YAKU_KOKUSHIMUSO) { return traditional ? "國士無雙" : "国士无双"; }
            if (yakuId == YAKU_KOKUSHIMUSO13) { return traditional ? "國士無雙十三面" : "国士无双十三面"; }
            if (yakuId == YAKU_SUANKO) { return "四暗刻"; }
            if (yakuId == YAKU_SUANKO_TANKI) { return traditional ? "四暗刻單騎" : "四暗刻单骑"; }
            if (yakuId == YAKU_DAISANGEN) { return "大三元"; }
            if (yakuId == YAKU_SHOSUSHI) { return "小四喜"; }
            if (yakuId == YAKU_DAISUSHI) { return "大四喜"; }
            if (yakuId == YAKU_TSUISO) { return "字一色"; }
            if (yakuId == YAKU_RYUISO) { return traditional ? "綠一色" : "绿一色"; }
            if (yakuId == YAKU_CHINROUTOU) { return traditional ? "清老頭" : "清老头"; }
            if (yakuId == YAKU_CHURENPOHTOU) { return traditional ? "九蓮寶燈" : "九莲宝灯"; }
            if (yakuId == YAKU_JUNSEICHUREN) { return traditional ? "純正九蓮寶燈" : "纯正九莲宝灯"; }
            if (yakuId == YAKU_SUKANTSU) { return traditional ? "四槓子" : "四杠子"; }
            if (yakuId == YAKU_TENHO) { return "天和"; }
            if (yakuId == YAKU_CHIHO) { return "地和"; }
            return "";
        }

        private bool IsYakumanYaku(int yakuId)
        {
            return YakuName(yakuId).Length > 0;
        }

        // ポスターに出す和了か（真の役満＋数え役満）。
        private bool IsPosterYaku(int yakuId)
        {
            return yakuId == YAKU_KAZOE || IsYakumanYaku(yakuId);
        }

        private bool IsKazoe()
        {
            return syncYakuId == YAKU_KAZOE;
        }

        // ------------------------------------------------------------
        // 言語（KemonoLanguage の extraTargets から叩かれる）
        // ------------------------------------------------------------

        public void ApplyLanguageJa() { SetLanguage(KemonoLanguage.LANG_JA); }
        public void ApplyLanguageEn() { SetLanguage(KemonoLanguage.LANG_EN); }
        public void ApplyLanguageKo() { SetLanguage(KemonoLanguage.LANG_KO); }
        public void ApplyLanguageZhs() { SetLanguage(KemonoLanguage.LANG_ZHS); }
        public void ApplyLanguageZht() { SetLanguage(KemonoLanguage.LANG_ZHT); }

        private void SetLanguage(int lang)
        {
            if (lang_ == lang) { return; }
            lang_ = lang;

            // 掲示中なら組み直して撮り直す（言語を切り替えた人だけの絵が変わる）。
            if (showing_)
            {
                BuildPoster();
                if (posterCamera != null)
                {
                    posterCamera.enabled = true;
                    renderOffAt_ = Time.time + Mathf.Max(0.05f, renderBurstSeconds);
                }
            }
        }

        private void PullLanguage()
        {
            if (languageManager == null) { return; }
            lang_ = languageManager.GetLanguage();
        }

        // ------------------------------------------------------------
        // 時刻
        // ------------------------------------------------------------

        // サーバー時刻(int ms)は約24.8日で折り返して負に転じる。折り返しをまたいだ
        // 差はここで畳んで「0〜掲示時間」の範囲へ収める（負の int は uint 化しない）。
        private float ElapsedSeconds(int startMs)
        {
            int nowMs = Networking.GetServerTimeInMilliseconds();
            int diff = nowMs - startMs;
            if (diff < 0) { return 0.0f; }
            return diff * 0.001f;
        }
    }
}
