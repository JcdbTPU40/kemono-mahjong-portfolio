using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    // 広告パネルの巡回コントローラ（ワールドに1個だけ）
    //
    // ■ 役割
    //   サーバー時刻から「いま何番目の時間帯か」を計算し、配下の AdPanel 全部に配る。
    //   切り替わった瞬間だけ配るので、平常時のコストは float 比較1回ぶん。
    //
    // ■ なぜ Manager に集約するのか（Quest）
    //   Udon の Update はビヘイビア1個ごとに固定の呼び出しコストが乗る。広告パネルを
    //   30枚 50枚と置く前提なので、パネル側に Update を持たせると枚数ぶん丸ごと積み上がる。
    //   時刻の計算は全パネルで同じ＝1回で済むため、Update はここ1個に閉じる。
    //
    // ■ 同期変数はゼロ
    //   index = floor(サーバー時刻 / 間隔) をどのクライアントも同じ式で解くので、
    //   ネットワーク送信なしで全員の表示が一致する。遅れて入ってきた人も即座に正しい絵になる
    //   （Manual同期のように「オーナーが送るまで真っ白」にならない）。
    //
    // ■ サーバー時刻の性質
    //   GetServerTimeInMilliseconds() は int なので約24.8日で一周して負に転じる。
    //   その瞬間だけ巡回が1回ぶん飛ぶが、負の剰余は AdPanel 側で畳んでいるので停止はしない。
    //   （Udon は負の int を uint 化すると止まるため、uint 化はどこでも行わない。）
    //
    // ■ 使い方
    //   1) シーンに空オブジェクト "AdPanels" を作り、この AdPanelManager を付ける
    //      （Tools > Kemono Mahjong > Build Ad Panels が自動で作る）
    //   2) AdPanel.prefab をその子として好きなだけ置く
    //   3) 各パネルの Inspector の posters に A4 画像を入れる
    //   配線作業は不要。Start 時に子から自動収集する。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class AdPanelManager : UdonSharpBehaviour
    {
        [Tooltip("何秒ごとに次のポスターへ送るか。既定は1分＝60秒。")]
        public float intervalSeconds = 60f;

        [Tooltip("切り替えのクロスフェードの尺（秒）。0 にするとフェードなしの即切り替え。\n" +
                 "進行はシェーダーが時刻から自分で解くので、長くしても毎フレームの負荷は増えない。")]
        public float fadeSeconds = 0.6f;

        [Tooltip("空なら Start 時に自分の子から AdPanel を自動収集する。手で並べたい時だけ埋める。")]
        public AdPanel[] panels;

        [Tooltip("何秒ごとに時刻を見に行くか。切り替え判定の粒度。1秒で十分。")]
        public float checkIntervalSeconds = 1f;

        private AdPanel[] panels_;
        private int intervalMs_ = 60000;
        private int step_ = 0;
        private bool hasStep_ = false;   // step_ に番兵値を使わない（0 も正規の値なので）
        private float nextCheck_ = 0f;

        void Start()
        {
            if (intervalSeconds < 1f) { intervalSeconds = 1f; }
            intervalMs_ = (int)(intervalSeconds * 1000f);
            if (intervalMs_ < 1000) { intervalMs_ = 1000; }
            if (checkIntervalSeconds < 0.1f) { checkIntervalSeconds = 0.1f; }

            panels_ = panels;
            if (panels_ == null || panels_.Length == 0)
            {
                // 非アクティブのパネルも拾う（カリングで寝ていても、起きた時に正しい絵にするため）。
                panels_ = GetComponentsInChildren<AdPanel>(true);
            }

            // フェードの尺はここが正。全パネルに配る（パネルごとに Inspector で直させないため）。
            // 切り替え間隔の半分を超える尺は、前のフェードが終わる前に次が始まって破綻するので丸める。
            if (fadeSeconds < 0f) { fadeSeconds = 0f; }
            if (fadeSeconds > intervalSeconds * 0.5f) { fadeSeconds = intervalSeconds * 0.5f; }
            if (panels_ == null) { panels_ = new AdPanel[0]; }
            for (int i = 0; i < panels_.Length; i++)
            {
                AdPanel p = panels_[i];
                if (p != null) { p.fadeSeconds = fadeSeconds; }
            }

            Tick();
        }

        void Update()
        {
            if (Time.time < nextCheck_) { return; }
            nextCheck_ = Time.time + checkIntervalSeconds;
            Tick();
        }

        // ------------------------------------------------------------
        // 割り込み掲示（役満ポスター）
        //
        // 全パネルを同じ1枚で塗り潰し、解除で広告の巡回へ戻す。
        // 巡回そのものは止めない（時間帯番号はサーバー時刻から解く値なので、
        // 止めても意味が無い）。パネル側が割り込み中の Tick を無視する。
        // ------------------------------------------------------------

        public void ShowPosterOverride(Texture tex)
        {
            if (panels_ == null || tex == null) { return; }
            for (int i = 0; i < panels_.Length; i++)
            {
                AdPanel p = panels_[i];
                if (p == null) { continue; }
                p._ShowOverride(tex);
            }
        }

        public void ClearPosterOverride()
        {
            if (panels_ == null) { return; }
            for (int i = 0; i < panels_.Length; i++)
            {
                AdPanel p = panels_[i];
                if (p == null) { continue; }
                p._ClearOverride();
            }
        }

        private void Tick()
        {
            if (panels_ == null || panels_.Length == 0) { return; }

            int nowMs = Networking.GetServerTimeInMilliseconds();
            int step = nowMs / intervalMs_;   // 負でも 0 方向へ切り捨て。負の剰余は AdPanel 側で畳む。

            if (hasStep_ && step == step_) { return; }
            step_ = step;
            hasStep_ = true;

            for (int i = 0; i < panels_.Length; i++)
            {
                AdPanel p = panels_[i];
                if (p == null) { continue; }
                p.currentStep = step;
                p._Refresh();
            }
        }
    }
}
