using UdonSharp;
using UnityEngine;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    // 広告パネル（A4）1枚ぶんの表示器
    //
    // ■ 役割
    //   posters に入れたテクスチャを、AdPanelManager から渡される「時間帯番号(currentStep)」に
    //   従って差し替えるだけの受け身のコンポーネント。時刻の計算・巡回の駆動は
    //   すべて AdPanelManager が持つ（Update を持つのはワールドで1個だけにするため）。
    //
    // ■ 表示順（shuffle）
    //   既定はランダム順。ただし UnityEngine.Random は使わない。
    //   クライアントごとに違う絵が出てしまい、広告として成立しないため。
    //   代わりに「時間帯番号から決まる決定的シャッフル」を使う：
    //
    //     cycle = floor(step / 枚数)   … 何巡目か
    //     pos   = step - cycle * 枚数  … その巡の何番目か
    //     bag   = Fisher-Yates(seed = hash(cycle, startOffset))
    //     index = bag[pos]
    //
    //   これで (a) 全員が同じ絵を見る (b) 1巡の中で全部が必ず1回ずつ出る
    //   (c) 巡ごとに並びが変わる、が同時に成立する。乱数の種は時刻由来なので同期も要らない。
    //   純粋な毎回ランダム（同じ絵が2回続き得る）ではなく袋引き方式にしているのは、
    //   枚数が少ないと同じ広告が2分3分と居座って見えるため。
    //
    // ■ なぜ MaterialPropertyBlock なのか
    //   renderer.material を触るとパネルごとにマテリアルの実体が増える（＝Quest でメモリと
    //   バッチが崩れる）。MPB ならマテリアル1個を全パネルで共有したまま、そのレンダラーの
    //   _MainTex だけを差し替えられる。ドラ光沢（Mahjong.cs）と同じ作法。
    //
    // ■ テクスチャの持ち方の注意（Quest）
    //   posters に入れたテクスチャはワールドに常駐する。同じポスターを複数のパネルで
    //   使い回す場合、メモリはアセット1つぶんしか増えない（参照が増えるだけ）。
    //   逆に「パネルごとに別の絵」を大量に置くとそのまま VRAM に効くので、
    //   長辺1024px・ASTC/DXT 圧縮を守ること（Tools > Kemono Mahjong > Setup Ad Poster Textures）。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class AdPanel : UdonSharpBehaviour
    {
        [Tooltip("ここに A4 のポスター画像を入れる。1枚だけなら固定表示になる。")]
        public Texture[] posters;

        [Tooltip("ON=ランダム順（1巡で全部を1回ずつ出し、巡ごとに並びが変わる）／OFF=配列の上から順番通り。")]
        public bool shuffle = true;

        [Tooltip("このパネル固有の番号。ランダム順では乱数の種、順番通りでは開始位置のズラしになる。\n" +
                 "同じ画像セットのパネルを並べるときは 0,1,2… と変える（同じ値だと全部同じ絵になる）。\n" +
                 "Tools > Kemono Mahjong > Reseed Ad Panels で一括採番できる。")]
        public int startOffset = 0;

        [Tooltip("絵を貼る面のレンダラー（プレハブの Poster）。")]
        public MeshRenderer posterRenderer;

        [Tooltip("posterRenderer のマテリアルスロット番号。プレハブ既定は 0。")]
        public int materialSlot = 0;

        [Tooltip("差し替えるテクスチャプロパティ名。Unlit/Texture 系は _MainTex。")]
        public string textureProperty = "_MainTex";

        // Manager が書き込む「いま何番目の時間帯か」。手で触らない。
        [HideInInspector] public int currentStep = 0;

        // クロスフェードの尺（秒）。Manager が Start で全パネルに配るので、値の正は Manager 側。
        // 0 にするとフェードなし（即切り替え）になる。
        [HideInInspector] public float fadeSeconds = 0.6f;

        private MaterialPropertyBlock mpb_;
        private int shownIndex_ = -1;
        private Texture prevTex_;   // クロスフェードの元になる「今出ている絵」

        // 役満ポスター等の割り込み。これが入っている間は巡回を止めて1枚を出し続ける
        // （Manager の Tick は毎秒来るので、ここで止めないと1秒後に広告へ戻る）。
        private Texture overrideTex_;

        // シャッフルの袋。巡が変わった時だけ引き直す（切り替え間隔×枚数に1回・枚数ぶんのループ）。
        private int[] bag_;
        private int bagCycle_ = 0;
        private bool hasBag_ = false;

        void Start()
        {
            // Manager が居ない・まだ回っていない場合でも、とりあえず1枚出しておく
            // （真っさらなマテリアルのまま放置すると「壊れている」ように見えるため）。
            ApplyPoster();
        }

        // Manager から呼ばれる。currentStep を書き換えた直後に叩く前提。
        public void _Refresh()
        {
            ApplyPoster();
        }

        // 割り込みの絵を貼る（役満ポスター）。Manager から全パネル一斉に呼ばれる。
        // 広告と同じクロスフェードに乗るので、貼り替えの見え方は普段の巡回と同じ。
        public void _ShowOverride(Texture tex)
        {
            if (tex == null) { return; }
            overrideTex_ = tex;
            ApplyTexture(tex);
        }

        // 割り込みを解除して広告へ戻す。
        public void _ClearOverride()
        {
            if (overrideTex_ == null) { return; }
            overrideTex_ = null;
            shownIndex_ = -1;   // 「同じ番号だから何もしない」を通さず必ず貼り直す
            ApplyPoster();
        }

        private void ApplyPoster()
        {
            if (overrideTex_ != null) { return; }
            if (posterRenderer == null) { return; }
            if (posters == null || posters.Length == 0) { return; }

            int n = posters.Length;
            int index = shuffle ? ShuffledIndex(n) : SequentialIndex(n);

            Texture tex = posters[index];
            if (tex == null)
            {
                // 空スロットは飛ばして次の有効な絵を探す（配列に穴が空いていても止まらない）。
                for (int k = 1; k < n; k++)
                {
                    int j = (index + k) % n;
                    if (posters[j] != null) { index = j; tex = posters[j]; break; }
                }
                if (tex == null) { return; }
            }

            if (index == shownIndex_) { return; }

            ApplyTexture(tex);
            shownIndex_ = index;
        }

        // 実際にレンダラーへ貼る。広告の巡回も割り込みもここを通る＝
        // クロスフェードの作法を1か所に閉じる。
        private void ApplyTexture(Texture tex)
        {
            if (posterRenderer == null || tex == null) { return; }
            if (mpb_ == null) { mpb_ = new MaterialPropertyBlock(); }

            // フェードの進行はシェーダーが _Time.y から自分で解く。
            // ここでは「いつ始めたか」を 1回書き込むだけ＝フェード中の CPU コストはゼロ。
            // _Time.y は Time.timeSinceLevelLoad と同じ値なので、この時刻をそのまま渡せる。
            bool canFade = (prevTex_ != null) && (fadeSeconds > 0.01f);
            if (canFade)
            {
                mpb_.SetTexture("_PrevTex", prevTex_);
                mpb_.SetFloat("_FadeStart", Time.timeSinceLevelLoad);
                mpb_.SetFloat("_FadeDuration", fadeSeconds);
            }
            else
            {
                // 初回は溶け元が無い。前の絵に新しい絵を入れて、十分に古い時刻を入れて
                // 「フェード完了済み」の状態から始める（白から溶ける事故を防ぐ）。
                mpb_.SetTexture("_PrevTex", tex);
                mpb_.SetFloat("_FadeStart", -1000f);
                mpb_.SetFloat("_FadeDuration", 0.6f);
            }
            mpb_.SetTexture(textureProperty, tex);
            posterRenderer.SetPropertyBlock(mpb_, materialSlot);
            prevTex_ = tex;
        }

        // ------------------------------------------------------------
        // 順番通り
        // ------------------------------------------------------------
        private int SequentialIndex(int n)
        {
            int index = (currentStep + startOffset) % n;
            // サーバー時刻は int のミリ秒なので負になり得る。剰余も負になるのでここで畳む。
            if (index < 0) { index += n; }
            return index;
        }

        // ------------------------------------------------------------
        // ランダム順（決定的シャッフル）
        // ------------------------------------------------------------
        private int ShuffledIndex(int n)
        {
            // 1枚は固定。2枚は「連続しない並び」が交互の1通りしか無い＝シャッフルする余地がないので
            // 順番通りに落とす（下の入れ替え規則は n>=3 を前提にしている）。
            if (n <= 2) { return SequentialIndex(n); }

            int cycle = FloorDiv(currentStep, n);
            int pos = currentStep - cycle * n;   // FloorDiv なので 0..n-1 が保証される

            if (!hasBag_ || bag_ == null || bag_.Length != n || bagCycle_ != cycle)
            {
                if (bag_ == null || bag_.Length != n) { bag_ = new int[n]; }
                BuildBag(bag_, n, cycle);
                bagCycle_ = cycle;
                hasBag_ = true;
            }
            return bag_[pos];
        }

        // seed から Fisher-Yates で袋を作る。全クライアントで同じ結果になることが要件。
        private void BuildBag(int[] bag, int n, int cycle)
        {
            for (int i = 0; i < n; i++) { bag[i] = i; }

            int rnd = Hash(cycle, startOffset);
            for (int i = n - 1; i > 0; i--)
            {
                rnd = rnd * 1103515245 + 12345;               // 古典的なLCG。オーバーフローは折り返す
                int j = ((rnd >> 16) & 0x7FFF) % (i + 1);     // 上位ビットを使う（下位は周期が短い）
                int t = bag[i]; bag[i] = bag[j]; bag[j] = t;
            }

            // 巡の変わり目で同じ絵が2回続くのを避ける。
            // 入れ替え先は「末尾」ではなく「2番目」でなければならない。
            // 末尾を絶対に触らなければ actual(c)[n-1] == raw(c)[n-1] が常に成立し、
            // 次の巡が参照する「前の巡の最後」が厳密に正しくなる（遡る必要が消える）。
            // 末尾と入れ替えると前の巡の最後がずれ、実測で連続重複が残った（2026-09-03 の検算）。
            // 全要素は相異なので bag[1] != bag[0] == prevLast が保証される。
            int prevLast = LastOfRawBag(n, cycle - 1);
            if (bag[0] == prevLast)
            {
                int t = bag[0]; bag[0] = bag[1]; bag[1] = t;
            }
        }

        // 指定の巡の「素の袋」の最後の要素だけを求める（入れ替え規則は適用しない）。
        private int LastOfRawBag(int n, int cycle)
        {
            // 袋を作り直すのは「切り替え間隔×枚数」に1回（既定なら5枚なら5分に1回）なので、
            // この程度の再計算はコストにならない。
            int[] tmp = new int[n];
            for (int i = 0; i < n; i++) { tmp[i] = i; }

            int rnd = Hash(cycle, startOffset);
            for (int i = n - 1; i > 0; i--)
            {
                rnd = rnd * 1103515245 + 12345;
                int j = ((rnd >> 16) & 0x7FFF) % (i + 1);
                int t = tmp[i]; tmp[i] = tmp[j]; tmp[j] = t;
            }
            return tmp[n - 1];
        }

        // 巡番号とパネル固有番号から種を作る。負値も素直に混ざるように XOR とシフトで撹拌する。
        // 最後に 0x7FFFFFFF で畳んで必ず非負にする（Udon は負の int を uint 化すると停止するので
        // uint 化は使わない）。
        private int Hash(int a, int b)
        {
            int h = a * 73856093;
            h = h ^ (b * 19349663);
            h = h ^ (h >> 13);
            h = h * 1274126177;
            h = h ^ (h >> 16);
            return h & 0x7FFFFFFF;
        }

        // 切り捨て方向を必ず下向きにする整数除算（b > 0 前提）。
        // C# の / は 0 方向へ丸めるので、サーバー時刻が負の間だけ巡がズレる。
        private int FloorDiv(int a, int b)
        {
            int q = a / b;
            if ((a % b) != 0 && a < 0) { q--; }
            return q;
        }
    }
}
