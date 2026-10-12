using TMPro;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace SuzuFactory.AozoraMahjong
{
    // 席の名札（Mリーグの卓の手前に置かれた名前のプレート）。
    //   ・手牌の真ん中の手前のフェルトに寝かせて置く＝観戦の手元カメラに手牌と一緒に名前が写る。
    //   ・左の小窓はその人の段位紋章（初獣〜天獣・PlayerData の段位から各自が引く）。CPU 席は空。
    //   ・座っている人にも見える実物の板（観戦カメラ専用にはしない）。
    //   ・同期変数ゼロ：名前は卓の同期済みの席情報（entry / seatPlayerId）から各自が引く。
    //     色はその人が選んだ和了演出（雷）の色＝PlayerData(kmj_agari_fx)。装飾品カードの色見本（主色）そのまま。
    //   ・卓ごと近接カリングで寝るので、Update は起きている卓だけで 0.5 秒に1回・変わった時だけ書き換える。
    // 骨格と配線は Tools > Kemono Mahjong > Build Seat Name Plates（冪等）。
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class MahjongSeatNamePlates : UdonSharpBehaviour
    {
        public Mahjong table;
        public GameObject[] plates;          // 席0..3
        public TextMeshPro[] nameTexts;
        public Renderer[] rankRenderers;     // 左の小窓の段位紋章（MPB で _MainTex を差し替える）
        public Texture[] rankEmblems;        // 段位の大分類 0..5（初獣・鋭爪・剛牙・獣威・幻獣・天獣）
        public Renderer[] baseRenderers;     // 板の地（席の人の色を MPB で塗る）
        public float refreshInterval = 0.5f;
        public Color darkTextColor = new Color(0.10f, 0.10f, 0.12f, 1f);   // 明るい地（白雷など）の文字
        public float darkTextLuminance = 0.65f;                              // 地の明るさがこれ以上なら墨色の文字
        public Color cpuColor = new Color(0.30f, 0.32f, 0.36f, 1f);

        private string[] shownName_ = new string[4];
        private int[] shownRank_ = new int[4];
        private int[] shownKey_ = new int[4];
        private bool[] shownOn_ = new bool[4];
        private float next_;
        private MaterialPropertyBlock mpb_;

        private void Start()
        {
            for (int s = 0; s < 4; ++s)
            {
                shownName_[s] = null;
                shownRank_[s] = int.MinValue;
                shownKey_[s] = int.MinValue;
                shownOn_[s] = true;
            }
            Refresh();
        }

        private void OnEnable()
        {
            // カリングから起きた直後は待たずに描き直す
            next_ = 0f;
        }

        private void Update()
        {
            if (Time.time < next_) { return; }
            next_ = Time.time + refreshInterval;
            Refresh();
        }

        private void Refresh()
        {
            if (table == null || plates == null) { return; }
            int n = table.Presentation_PlayerCount();
            bool[] entry = table.entry;
            for (int s = 0; s < 4 && s < plates.Length; ++s)
            {
                bool on = entry != null && s < entry.Length && entry[s] && s < n;
                if (on != shownOn_[s])
                {
                    shownOn_[s] = on;
                    if (plates[s] != null) { plates[s].SetActive(on); }
                }
                if (!on) { continue; }

                string name = PlateName(s);
                if (name != shownName_[s])
                {
                    shownName_[s] = name;
                    if (nameTexts != null && s < nameTexts.Length && nameTexts[s] != null) { nameTexts[s].text = name; }
                }

                int pid = table.Presentation_SeatPlayerId(s);
                int rank = pid > 0 ? SeatRank(pid) : -1;
                if (rank != shownRank_[s])
                {
                    shownRank_[s] = rank;
                    ApplyRank(s, rank);
                }

                int fx = pid > 0 ? SeatFx(pid) : -1;
                int key = pid * 64 + fx;
                if (key != shownKey_[s])
                {
                    shownKey_[s] = key;
                    ApplyColor(s, fx);
                }
            }
        }

        // 卓の席ラベルは CPU を「東家(CPU)」と返すので、名札は家を外して「CPU」「CPU代打」にする
        private string PlateName(int s)
        {
            string label = table.Presentation_SeatName(s);
            if (label == null) { return ""; }
            int idx = label.IndexOf("(CPU");
            if (idx >= 0)
            {
                string cpu = label.Substring(idx + 1);
                if (cpu.EndsWith(")")) { cpu = cpu.Substring(0, cpu.Length - 1); }
                return cpu;
            }
            return label;
        }

        // 段位の大分類（0..5）。退室済み・読めない時は -1＝紋章なし
        private int SeatRank(int pid)
        {
            if (table.statsStore == null) { return -1; }
            VRCPlayerApi p = VRCPlayerApi.GetPlayerById(pid);
            if (p == null || !Utilities.IsValid(p)) { return -1; }
            return table.statsStore.GetMajorRankIndexOf(p);
        }

        private void ApplyRank(int s, int rank)
        {
            if (rankRenderers == null || s >= rankRenderers.Length || rankRenderers[s] == null) { return; }
            Renderer r = rankRenderers[s];
            bool show = rank >= 0 && rankEmblems != null && rank < rankEmblems.Length && rankEmblems[rank] != null;
            if (r.enabled != show) { r.enabled = show; }
            if (!show) { return; }
            if (mpb_ == null) { mpb_ = new MaterialPropertyBlock(); }
            r.GetPropertyBlock(mpb_);
            mpb_.SetTexture("_MainTex", rankEmblems[rank]);
            r.SetPropertyBlock(mpb_);
        }

        private int SeatFx(int pid)
        {
            if (table.statsStore == null) { return 0; }
            VRCPlayerApi p = VRCPlayerApi.GetPlayerById(pid);
            if (p == null || !Utilities.IsValid(p)) { return 0; }
            return table.statsStore.GetAgariFxOf(p);
        }

        // 板の色：その人が選んだ雷の主色（装飾品カードの色見本）をそのまま塗る（2026-10-05 ユーザー指示で
        // 帯と同じ「0.85倍・黒雷は持ち上げ」をやめた）。CPU・退室済みは灰。白雷のような明るい地は文字を墨色にする
        private void ApplyColor(int s, int fx)
        {
            if (baseRenderers == null || s >= baseRenderers.Length || baseRenderers[s] == null) { return; }
            Color c = cpuColor;
            if (fx >= 0 && table.agariEffect != null)
            {
                c = table.agariEffect.GetVariantMainColor(fx);
                c.a = 1f;
            }
            if (mpb_ == null) { mpb_ = new MaterialPropertyBlock(); }
            baseRenderers[s].GetPropertyBlock(mpb_);
            mpb_.SetColor("_Color", c);
            baseRenderers[s].SetPropertyBlock(mpb_);

            float lum = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
            if (nameTexts != null && s < nameTexts.Length && nameTexts[s] != null)
            {
                nameTexts[s].color = lum >= darkTextLuminance ? darkTextColor : Color.white;
            }
        }
    }
}
