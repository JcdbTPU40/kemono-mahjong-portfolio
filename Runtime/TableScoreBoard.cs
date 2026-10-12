using UdonSharp;
using UnityEngine;
using UnityEngine.UI;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    //  卓エッジ点数板（自動卓スタイル・俯瞰マップ型）
    // ------------------------------------------------------------
    //  卓の縁の下に取り付ける傾斜パネル（4面）。卓を上から見た配置の
    //  まま各家の点数を並べる：手前=自席／右=下家／対面=対面／左=上家。
    //  中央に局・本場・供託（立直棒）。空席・三麻seat3は「-----」。
    //
    //  設計思想（開発ナレッジ準拠）:
    //   ・純ローカル描画。同期変数ゼロ（NoVariableSync）。
    //     points / round / leader / honba / kyotaku / reachIndex 等は
    //     Mahjong.cs が既に全クライアントで同一値を持つ（イベント同期＋
    //     各自シミュレーション）。UpdateView 末尾から Refresh() を呼ばれ、
    //     全員が同じ入力から同じ表示を組み立てる。追加同期負荷ゼロ。
    //     Late Joiner も Deserialize→UpdateView の既存経路で自動復帰。
    //   ・UpdateView は高頻度で呼ばれるため、シグネチャ比較で無変化なら
    //     即 return。文字列生成と Text 書き換えは変化時のみ（Quest基準）。
    //   ・UIはWorld Space Canvas必須（Screen Space Overlayは描画されない）。
    //     アニメなし・uGUI Textのみ。レイヤーはWalkthrough推奨（当たり判定回避）。
    //   ・注意：U#の他Behaviourメソッド呼び出しは非アクティブなObjectに
    //     届かない。本スクリプトのGameObjectは常時アクティブに保ち、
    //     表示ON/OFFは子の boardRoot で行うこと。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class TableScoreBoard : UdonSharpBehaviour
    {
        [Tooltip("同じ卓の Mahjong 本体")]
        public Mahjong mahjong;

        [Tooltip("表示ルート（対局前は非表示にする）。本スクリプトのGameObjectとは別の子にすること")]
        public GameObject boardRoot;

        // ---- 面共通情報（要素0..3 = 席0..3側の面） ----
        [Tooltip("局ラベルText（例「東1局」）。要素0..3 = 席0..3側の面")]
        public Text[] roundTexts;

        public RawImage[] doraImages;
        public Texture doraAtlas;
        public Rect[] doraUvRects;
        [Tooltip("本場Text（例「0本場」）。要素0..3 = 席0..3側の面")]
        public Text[] honbaTexts;

        [Tooltip("供託（立直棒本数）Text（例「×0」）。要素0..3 = 席0..3側の面")]
        public Text[] kyotakuTexts;

        [Tooltip("供託の立直棒アイコン（RawImage）。要素0..3 = 席0..3側の面。0本のときは隠す")]
        public RawImage[] kyotakuSticks;

        // 供託テキストのX座標。棒を出すと右へ寄せて (棒)×N の並びにする。
        // BuildScoreBoard.cs の KyotakuStick / KyotakuText の配置と対になっている値。
        // 片方だけ変えると絵と文字が重なるので、必ず両方そろえて直すこと。
        private const float KYOTAKU_TEXT_X_ALONE = 158.0f;      // 0本：元の中央位置
        private const float KYOTAKU_TEXT_X_WITH_STICK = 180.0f; // 1本以上：棒の右隣

        [Tooltip("局数ルールText（例「半荘戦」）。要素0..3 = 席0..3側の面。パネル左下の小さい表示")]
        public Text[] lengthTexts;

        [Tooltip("残り牌数Text（任意）。要素0..3 = 席0..3側の面。未割当なら表示なし＝ツモ毎の再描画も発生しない")]
        public Text[] wallTexts;

        // ---- 点数スロット（16要素 = 面×4 + スロット） ----
        // スロット順：0=手前(その面に正対する席)／1=右(下家)／2=奥(対面)／3=左(上家)
        // index = 面 * 4 + スロット。スロットの席 = (面 + スロット) % 4（物理席方向=%4）。
        [Tooltip("点数Text 16要素。index=面*4+スロット（0手前/1右/2奥/3左）")]
        public Text[] scoreTexts;

        [Tooltip("順位・トップ差の小Text 16要素。index同上")]
        public Text[] subTexts;

        [Tooltip("プレイヤー名Text 16要素（任意）。未割当（空配列）なら名前表示なし")]
        public Text[] nameTexts;

        [Tooltip("位置ラベルText 16要素（自分/下家/対面/上家）。index=面*4+スロット。言語で自動切替。未割当なら表示なし")]
        public Text[] posTexts;

        [Tooltip("プレイヤー名の最大表示文字数（超過分は切り詰め）")]
        public int maxNameLength = 10;

        [Tooltip("空席・三麻seat3の点数表示")]
        public string emptySeatText = "-----";

        // 0 は「未描画」の番兵（負値センチネルは使わない・番兵0運用）。
        private int lastSignature_ = 0;
        private bool visible_ = false;

        // シーン上で BoardRoot がアクティブのまま置かれていても、対局前は
        // 必ず非表示から始める（visible_ 初期値と実状態のズレ防止）。
        // OnEnable が先に走って既に描画済み（visible_==true）なら消さない。
        private void Start()
        {
            if (!visible_)
            {
                SetVisible(false);
            }
        }

        // 卓カリング再表示時の復帰。直後に Mahjong 側の UpdateView でも
        // Refresh されるのが通常経路だが、再表示後に卓イベントが来るまで
        // UpdateView が呼ばれないケースに備えてここでも再描画する。
        private void OnEnable()
        {
            lastSignature_ = 0;
            if (mahjong != null && mahjong.points != null)
            {
                Refresh();
            }
        }

        // Mahjong.UpdateView 末尾から毎回呼ばれる（純ローカル）。
        public void Refresh()
        {
            if (mahjong == null || scoreTexts == null) { return; }
            if (mahjong.points == null) { return; } // Mahjong 初期化前

            // 対局前（INITIAL/ENTRY）は非表示。
            if (mahjong.ScoreBoard_IsIdle())
            {
                if (visible_) { SetVisible(false); }
                lastSignature_ = 0;
                return;
            }

            int sig = ComputeSignature();
            if (sig == lastSignature_ && visible_) { return; }
            lastSignature_ = sig;

            if (!visible_) { SetVisible(true); }

            RenderAll();
        }

        private void SetVisible(bool value)
        {
            visible_ = value;
            if (boardRoot != null) { boardRoot.SetActive(value); }
        }

        // 表示に影響する同期済み状態のみを畳み込む。名前の遅延解決は
        // seatPlayerId の変化（＝同期到着）で拾う。
        private int ComputeSignature()
        {
            int sig = 17;
            int n = mahjong.numPlayers;
            for (int i = 0; i < n; ++i)
            {
                // 表示値を使う。これによりカウント中は毎回シグネチャが変わって再描画され、
                // カウント終了後は自動的に静止する（別途アニメ用のフラグは要らない）。
                sig = sig * 31 + mahjong.ScoreBoard_DisplayPoints(i);
                // フラッシュ状態も含める。カウント終了後に色だけ戻す描画が要るため、
                // 点数だけでは色が戻らず固まってしまう。
                sig = sig * 31 + mahjong.ScoreBoard_PointFlash(i);
                sig = sig * 31 + mahjong.seatPlayerId[i];
                sig = sig * 2 + (mahjong.ScoreBoard_IsReached(i) ? 1 : 0);
            }
            sig = sig * 31 + mahjong.round;
            sig = sig * 31 + mahjong.leader;
            sig = sig * 31 + mahjong.honba;
            sig = sig * 31 + mahjong.kyotaku;
            sig = sig * 31 + mahjong.numPlayers;
            sig = sig * 31 + mahjong.ScoreBoard_LanguageInt();
            sig = sig * 31 + mahjong.ScoreBoard_LengthInt();
            // 残り牌数は表示するときだけシグネチャに含める（ツモ毎の再描画を
            // 使わない構成に波及させない）。
            if (wallTexts != null && wallTexts.Length > 0)
            {
                sig = sig * 31 + mahjong.GetWallRemain();
            }
            for (int i = 0; i < 5; ++i) { sig = sig * 41 + mahjong.ScoreBoard_DoraIndicator(i); }
            if (sig == 0) { sig = 1; } // 番兵0と衝突しないよう回避
            return sig;
        }

        // 点数アニメ中も、順位・トップ差・表示点数を同じスナップショットから計算する。
        // 同点時は本体の半荘順位と同じく、起家に近い席を上位にする。
        private bool IsHigherDisplayRank(int a, int b)
        {
            int pointA = mahjong.ScoreBoard_DisplayPoints(a);
            int pointB = mahjong.ScoreBoard_DisplayPoints(b);
            if (pointA != pointB) { return pointA > pointB; }

            int n = mahjong.numPlayers;
            int first = mahjong.Hanchan_GetFirstLeader();
            int orderA = (a - first + n) % n;
            int orderB = (b - first + n) % n;
            return orderA < orderB;
        }

        private int GetDisplayRank(int seat)
        {
            if (seat < 0 || seat >= mahjong.numPlayers) { return 0; }
            int rank = 1;
            for (int i = 0; i < mahjong.numPlayers; ++i)
            {
                if (i != seat && IsHigherDisplayRank(i, seat)) { ++rank; }
            }
            return rank;
        }

        private int GetDisplaySeatByRank(int rank)
        {
            if (rank < 1 || rank > mahjong.numPlayers) { return -1; }
            for (int seat = 0; seat < mahjong.numPlayers; ++seat)
            {
                if (GetDisplayRank(seat) == rank) { return seat; }
            }
            return -1;
        }

        private void RenderAll()
        {
            if (doraImages != null && doraUvRects != null)
            {
                for (int i = 0; i < doraImages.Length; ++i)
                {
                    var img = doraImages[i];
                    if (img == null) { continue; }
                    int code = mahjong.ScoreBoard_DoraIndicator(i % 5);
                    bool open = code > 0 && code <= doraUvRects.Length;
                    img.texture = open ? doraAtlas : Texture2D.whiteTexture;
                    img.uvRect = open ? doraUvRects[code - 1] : new Rect(0f, 0f, 1f, 1f);
                    img.color = open ? Color.white : new Color(0.93f, 0.55f, 0.12f, 1f);
                }
            }
            bool ja = mahjong.ScoreBoard_LanguageInt() == 0;
            bool ko = mahjong.ScoreBoard_LanguageInt() == 2;
            bool zhs = mahjong.ScoreBoard_LanguageInt() == 3;
            bool zht = mahjong.ScoreBoard_LanguageInt() == 4;
            int n = mahjong.numPlayers;

            // ---- 面共通の素材を先に作る --------------------------------
            string roundLabel = mahjong.ScoreBoard_RoundLabel();
            string honbaLabel = ja ? (mahjong.honba + "本場") : ko ? (mahjong.honba + "본장")
                : zhs ? (mahjong.honba + "本场") : zht ? (mahjong.honba + "本場") : ("Honba x" + mahjong.honba);
            string kyotakuLabel = "×" + (mahjong.kyotaku / 1000); // 立直棒本数
            string lengthLabel = mahjong.ScoreBoard_LengthLabel();

            // 位置ラベル（スロット固定：0=自分/1=下家/2=対面/3=上家）。言語で切替。
            // face に依らずスロット番号だけで決まる（面 face のスロット slot に座る席が
            // 自分から見て下家/対面/上家の関係になるよう Build 側が配置している）。
            string[] posLabel = ja
                ? new string[] { "自分", "下家", "対面", "上家" }
                : ko
                ? new string[] { "나", "하가", "대면", "상가" }
                : zhs
                ? new string[] { "自己", "下家", "对家", "上家" }
                : zht
                ? new string[] { "自己", "下家", "對家", "上家" }
                : new string[] { "You", "Right", "Across", "Left" };

            // 席ごとの表示素材（点数・順位/トップ差・名前・立直）。
            // 着順は表示点数から算出し、同点時だけ本体と同じ起家順で決める。
            int topSeat = GetDisplaySeatByRank(1);
            int secondSeat = GetDisplaySeatByRank(2);
            string[] seatScore = new string[4];
            string[] seatSub = new string[4];
            string[] seatName = new string[4];

            for (int seat = 0; seat < 4; ++seat)
            {
                if (seat >= n)
                {
                    // 三麻 seat3 など物理的に存在しない席。
                    seatScore[seat] = emptySeatText;
                    seatSub[seat] = "";
                    seatName[seat] = "";
                    continue;
                }

                // 点数変動アニメ中は途中値を出す（各席のTextと同じ数字で動かすため）。
                string score = "" + mahjong.ScoreBoard_DisplayPoints(seat);

                // 変動直後は増減で色を変える。立直の赤より一時的にこちらを優先する
                // （フラッシュが終われば立直色に戻る）。
                int flash = mahjong.ScoreBoard_PointFlash(seat);
                if (flash != 0)
                {
                    score = "<color=#" + mahjong.ScoreBoard_PointFlashHex(seat) + ">" + score + "</color>";
                }
                else if (mahjong.ScoreBoard_IsReached(seat))
                {
                    score = "<color=#FF6A6A>" + score + "</color>";
                }
                seatScore[seat] = score;

                // トップは2位との差(+)、それ以外はトップとの差(-)。
                int rank = GetDisplayRank(seat);
                int diff;
                // 差も表示値で出す。点数がカウント中なのに差だけ即座に確定値へ飛ぶと
                // 数字が食い違って見えるため。
                if (rank == 1)
                {
                    diff = secondSeat >= 0
                        ? (mahjong.ScoreBoard_DisplayPoints(seat) - mahjong.ScoreBoard_DisplayPoints(secondSeat))
                        : 0;
                }
                else
                {
                    diff = mahjong.ScoreBoard_DisplayPoints(seat) - mahjong.ScoreBoard_DisplayPoints(topSeat);
                }
                seatSub[seat] = rank + (ja || zhs || zht ? "位 " : ko ? "위 " : ". ")
                    + (diff >= 0 ? "+" : "") + diff;

                string name = mahjong.ScoreBoard_SeatLabel(seat);
                if (name.Length > maxNameLength) { name = name.Substring(0, maxNameLength); }
                seatName[seat] = name;
            }

            // ---- 面ごとに描画 ------------------------------------------
            // 面 face のスロット slot（0手前/1右/2奥/3左）に座る席 = (face + slot) % 4。
            // 下家は物理的に右隣（物理席方向=%4）。三麻でも面の向きは変えない。
            for (int face = 0; face < 4; ++face)
            {
                SetText(roundTexts, face, roundLabel);
                SetText(honbaTexts, face, honbaLabel);
                SetText(kyotakuTexts, face, kyotakuLabel);
                SetKyotakuStick(face, mahjong.kyotaku >= 1000);
                SetText(lengthTexts, face, lengthLabel);
                if (wallTexts != null && wallTexts.Length > 0)
                {
                    SetText(wallTexts, face, (ja ? "残" : ko ? "남은 패 " : zhs ? "余" : zht ? "餘" : "Wall ") + mahjong.GetWallRemain());
                }

                for (int slot = 0; slot < 4; ++slot)
                {
                    int seat = (face + slot) % 4;
                    int idx = face * 4 + slot;
                    SetText(scoreTexts, idx, seatScore[seat]);
                    SetText(subTexts, idx, seatSub[seat]);
                    SetText(nameTexts, idx, seatName[seat]);
                    SetText(posTexts, idx, posLabel[slot]);
                }
            }
        }

        // 供託の立直棒アイコン。0本なら絵を隠して「×0」を元の中央位置へ戻し、
        // 1本以上なら絵を出してテキストを右へ寄せる＝ (立直棒)×N の並びになる。
        // kyotaku はシグネチャに入っているので、この関数は本数が変わったフレームしか走らない。
        private void SetKyotakuStick(int face, bool show)
        {
            if (kyotakuSticks != null && face >= 0 && face < kyotakuSticks.Length)
            {
                var icon = kyotakuSticks[face];
                if (icon != null && icon.gameObject.activeSelf != show)
                {
                    icon.gameObject.SetActive(show);
                }
            }

            if (kyotakuTexts == null || face < 0 || face >= kyotakuTexts.Length) { return; }
            var text = kyotakuTexts[face];
            if (text == null) { return; }

            var rt = text.rectTransform;
            float x = show ? KYOTAKU_TEXT_X_WITH_STICK : KYOTAKU_TEXT_X_ALONE;
            var pos = rt.anchoredPosition;
            if (pos.x != x)
            {
                rt.anchoredPosition = new Vector2(x, pos.y);
            }
        }

        // 配列未割当・要素null・長さ不足をすべて許容する安全な書き込み。
        private void SetText(Text[] texts, int index, string value)
        {
            if (texts == null || index < 0 || index >= texts.Length) { return; }
            var t = texts[index];
            if (t == null) { return; }
            t.text = value;
        }
    }
}
