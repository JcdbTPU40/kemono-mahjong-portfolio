using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Persistence;
using VRC.SDKBase;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    //  カメラパネル（TOP CAMERA の操作盤）
    // ------------------------------------------------------------
    //  自席の左手前・天板より下に、自動化パネル（MahjongAutoPanel）と同じ見た目の横長タブ（カメラ＋▼）を置き、
    //  押すと下へ縦に展開して次の4行を並べる（上から。行の位置は BuildCameraPanel が焼く）。
    //  天板より上だと左端の牌を指すレイに吸われる（2026-10-05 自動化パネルと揃えて下げた）。
    //  自分に新しい決断が来たら Mahjong.cs が Collapse() で畳む。
    //  並びは上から（よく使うものほどタブの近く）：
    //    表示   … カメラ（板＋撮影）のON/OFF
    //    大きさ … − ／ ＋ で板の大きさを 60／80／100／130／160%
    //    透明度 … − ／ ＋ で 0〜80% を20%刻み（板が消え切らないよう上限80%）
    //    視点   … 自分／下家／対面／上家（三麻は自分／下家／上家）。選ぶとカメラも開く
    //
    //  ・純ローカル。同期変数なし。自席の本人の画面にだけ出る。
    //  ・カメラの実体（板・撮影カメラ・向きの計算）は各席の MahjongTopCameraViewButton が持つ。
    //    板は本人の席のボタンの位置に出し、撮影の向きだけ選んだ席の背後へ回す。
    //  ・映すのは河と中央表示（手牌は画角の外）。副露は各家の側の端に小窓で足す（MahjongTopCameraViewButton）。
    //    他家の手牌は本人の端末では伏せて描かれているので、他家の視点にしても表は見えない。
    //  ・透明度と大きさは PlayerData に保存して次回も引き継ぐ（同期ゼロ）。
    //  ・カメラのON/OFFと板の位置（掴んで動かせる Pickup）も PlayerData に保存する（2026-10-05）。
    //    終局で消えてもONの記録は残し、次の対局でパネルが出たら同じ位置に開き直す。
    //    本人がOFFを押したときは位置の記録を捨てる＝OFF→ONで既定の位置に戻せる（位置リセット）。
    //    位置は「自分の席から見た相対位置」で持つので、席や卓が変わっても自分から見て同じ場所に出る。
    //  ・押下イベントは「_」始まり＝ネットワークイベントとしては呼べない（他人に押されない）。
    //  ・スクリプトの載るルートは常時アクティブ。表示ON/OFFは panelRoot だけで行う。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class MahjongCameraPanel : UdonSharpBehaviour
    {
        public const int VIEW_SELF = 0;
        public const int VIEW_SHIMOCHA = 1;
        public const int VIEW_TOIMEN = 2;
        public const int VIEW_KAMICHA = 3;

        private const int TRANSPARENCY_STEP = 20;

        // カメラのON/OFFと板の位置・向き（自分の席の座標）の保存先。本人の端末だけで効く。
        public const string ON_KEY = "kmj_topcam_on";
        public const string POS_KEY = "kmj_topcam_pos";
        public const string ROT_KEY = "kmj_topcam_rot";
        public const string POS_VALID_KEY = "kmj_topcam_posok";   // false＝位置の記録なし（PlayerData は消せないので旗で持つ）
        // 保存した位置が卓の中心からこれより遠ければ使わない（どこかへ放り投げた板を拾えなくなるため）。
        private const float BOARD_MAX_DISTANCE = 2.0f;
        private const int TRANSPARENCY_MAX = 80;

        public GameObject panelRoot;

        // ---- たたんだタブ ----
        public Text tabTitle;
        public Image tabLamp;              // カメラが開いている間だけ緑
        public Text tabArrow;              // ▲（展開中は ▼）

        // ---- 展開した一覧 ----
        public GameObject listRoot;
        public Transform[] viewHits;       // 視点の4区画の当たり判定（VIEW_* の順）
        public RectTransform[] viewRects;  // 同じ区画の絵
        public Image[] viewFills;
        public Text[] viewTexts;
        public Text alphaText;
        public Text sizeText;
        public Image showLamp;
        public Text showText;

        // 各席の TOP CAMERA ボタン（添え字＝席番号）。
        public MahjongTopCameraViewButton[] viewButtons;

        public float pixelsPerMeter = 4000.0f;
        public float width = 0.12f;        // m
        public float segmentGap = 0.002f;  // m（区画どうしの隙間）

        public Color onColor = new Color(0.30f, 0.92f, 0.40f, 1.0f);
        public Color offLampColor = new Color(0.22f, 0.27f, 0.42f, 1.0f);
        public Color onTextColor = new Color(1.0f, 1.0f, 1.0f, 1.0f);
        public Color offTextColor = new Color(0.62f, 0.68f, 0.82f, 1.0f);
        public Color segOnFill = new Color(0.16f, 0.42f, 0.22f, 0.98f);
        public Color segOffFill = new Color(0.09f, 0.13f, 0.27f, 0.96f);

        private bool expanded_;
        private int seat_ = -1;
        private bool autoOpenPending_;     // 対局が始まってパネルが出た直後だけ立つ（ONの記録なら開く）
        private bool sanma_;
        private int lang_;
        private int view_ = VIEW_SELF;
        private int transparency_ = 0;     // ％（0＝不透明）
        private bool transparencyLoaded_;
        private int[] sizeSteps = { 60, 80, 100, 130, 160 };
        private bool[] meldHas_;
        private float[] meldLeft_;
        private int sizeIndex_ = 2;        // sizeSteps の添え字（100%）
        private bool sizeLoaded_;

        private string[] jaViews = { "自分", "下家", "対面", "上家" };
        private string[] enViews = { "Self", "Right", "Front", "Left" };
        private string[] koViews = { "나", "하가", "대면", "상가" };
        private string[] zhsViews = { "自己", "下家", "对家", "上家" };
        private string[] zhtViews = { "自己", "下家", "對家", "上家" };

        // 終局・席を立ったとき（Mahjong.cs の UpdateCameraPanel が対局外で呼ぶ）。
        // パネルが消えるとカメラを閉じる手段が無くなる（卓のキューブは撤去済み）ので、開いていれば一緒に閉じる。
        // 次の対局は自分の視点から始める。
        public void Hide()
        {
            expanded_ = false;
            if (seat_ >= 0)
            {
                // ONの記録はそのまま（次の対局で開き直す）。板の位置だけ控えてから閉じる。
                if (IsCameraOpen()) { SaveBoardPose(); }
                CloseCamera();
                seat_ = -1;
            }
            if (panelRoot != null && panelRoot.activeSelf) { panelRoot.SetActive(false); }
        }

        // 一覧を畳む（タブとカメラはそのまま）。次の Show で描き直される。
        public void Collapse()
        {
            if (!expanded_) { return; }
            expanded_ = false;
            Render();
        }

        // meldHas / meldLeft ＝ 各家の副露の有無と左端（Mahjong.cs が UpdateView のたびに渡す）。
        public void Show(Vector3 pos, Quaternion rot, int seat, bool sanma, int lang, bool[] meldHas, float[] meldLeft)
        {
            if (panelRoot == null) { return; }

            transform.position = pos;
            transform.rotation = rot;

            // 席に着いていない状態（終局・初回）からパネルが出た＝新しい対局。
            // 透明度・大きさは全卓共通の保存値なので、新しい対局のたびに読み直す
            // （別の卓で変えた値を、この卓が覚えていた古い値で上書きしないため）。
            if (seat_ < 0)
            {
                autoOpenPending_ = true;
                transparencyLoaded_ = false;
                sizeLoaded_ = false;
            }

            if (seat != seat_ || sanma != sanma_)
            {
                // 席や人数が変わったら、開いたままの別席のカメラを閉じて自分の視点へ戻す。
                if (seat_ >= 0 && seat_ != seat) { CloseCamera(); }
                view_ = VIEW_SELF;
            }
            seat_ = seat;
            sanma_ = sanma;
            lang_ = lang;
            meldHas_ = meldHas;
            meldLeft_ = meldLeft;
            LoadTransparency();

            var own = OwnButton();
            if (own != null && own.IsOpen()) { own.SetMelds(meldHas_, meldLeft_); }
            LoadSize();

            if (!panelRoot.activeSelf) { panelRoot.SetActive(true); }

            // 前の対局でカメラをONのまま終えていたら、同じ位置に開き直す（視点は自分から）。
            if (autoOpenPending_)
            {
                autoOpenPending_ = false;
                if (LoadOn() && !IsCameraOpen())
                {
                    view_ = VIEW_SELF;
                    OpenCamera();
                }
            }

            Render();
        }

        private void Render()
        {
            // 韓国語は英語と同じ字詰め（字の大きさ）で出す。文言だけ ko で差し替える。
            // 中国語は日本語と同じく短い漢字なので、日本語の字の大きさのまま出す。
            bool zhs = lang_ == 3;
            bool zht = lang_ == 4;
            bool en = lang_ == 1 || lang_ == 2;
            bool ko = lang_ == 2;
            bool open = IsCameraOpen();

            // ---- タブ ----
            if (tabTitle != null)
            {
                tabTitle.text = ko ? "카메라" : zhs ? "相机" : zht ? "相機" : en ? "CAMERA" : "カメラ";
                tabTitle.color = open ? onTextColor : offTextColor;
            }
            if (tabLamp != null) { tabLamp.color = open ? onColor : offLampColor; }
            if (tabArrow != null) { tabArrow.text = expanded_ ? "▲" : "▼"; }   // 下へ開く

            // ---- 一覧 ----
            if (listRoot != null && listRoot.activeSelf != expanded_) { listRoot.SetActive(expanded_); }
            if (!expanded_) { return; }

            LayoutViewSegments(en, open);

            if (alphaText != null)
            {
                alphaText.text = (ko ? "투명도 " : (zhs || zht) ? "透明度 " : en ? "Transparency " : "透明度 ") + transparency_ + "%";
                alphaText.fontSize = en ? 36 : 52;
            }
            if (sizeText != null)
            {
                sizeText.text = (ko ? "크기 " : (zhs || zht) ? "大小 " : en ? "Size " : "大きさ ") + sizeSteps[sizeIndex_] + "%";
                sizeText.fontSize = en ? 40 : 44;   // 「大きさ 100%」は透明度より1文字長い
            }
            if (showLamp != null) { showLamp.color = open ? onColor : offLampColor; }
            if (showText != null)
            {
                showText.text = ko ? "카메라 표시" : zhs ? "显示相机" : zht ? "顯示相機" : en ? "Show View" : "カメラ表示";
                showText.color = open ? onTextColor : offTextColor;
            }
        }

        // 視点の区画を左から並べる。三麻は対面を伏せて3等分。
        private void LayoutViewSegments(bool en, bool open)
        {
            if (viewHits == null) { return; }

            int count = sanma_ ? 3 : 4;
            float seg = width / count;
            float baseSeg = width / 4.0f;
            float ppm = pixelsPerMeter;
            int slot = 0;

            for (int i = 0; i < viewHits.Length; ++i)
            {
                bool visible = !(sanma_ && i == VIEW_TOIMEN);
                var hit = viewHits[i];
                RectTransform rect = null;
                if (viewRects != null && i < viewRects.Length) { rect = viewRects[i]; }

                if (hit != null && hit.gameObject.activeSelf != visible) { hit.gameObject.SetActive(visible); }
                if (rect != null && rect.gameObject.activeSelf != visible) { rect.gameObject.SetActive(visible); }
                if (!visible) { continue; }

                float x = -width * 0.5f + seg * (slot + 0.5f);
                ++slot;

                if (hit != null)
                {
                    var p = hit.localPosition;
                    hit.localPosition = new Vector3(x, p.y, p.z);
                    hit.localScale = new Vector3(seg / baseSeg, 1.0f, 1.0f);
                }
                if (rect != null)
                {
                    rect.anchoredPosition = new Vector2(x * ppm, rect.anchoredPosition.y);
                    rect.sizeDelta = new Vector2((seg - segmentGap) * ppm, rect.sizeDelta.y);
                }

                bool selected = open && view_ == i;
                if (viewFills != null && i < viewFills.Length && viewFills[i] != null)
                {
                    viewFills[i].color = selected ? segOnFill : segOffFill;
                }
                if (viewTexts != null && i < viewTexts.Length && viewTexts[i] != null)
                {
                    viewTexts[i].text = lang_ == 2 ? koViews[i] : lang_ == 3 ? zhsViews[i]
                        : lang_ == 4 ? zhtViews[i] : en ? enViews[i] : jaViews[i];
                    viewTexts[i].fontSize = en ? 36 : 48;   // 英語は区画（約1cm×3cm）に収まる大きさへ
                    viewTexts[i].color = selected ? onColor : (view_ == i ? onTextColor : offTextColor);
                }
            }
        }

        // ---- カメラの実体（席のボタン）----
        private MahjongTopCameraViewButton OwnButton()
        {
            if (viewButtons == null || seat_ < 0 || seat_ >= viewButtons.Length) { return null; }
            return viewButtons[seat_];
        }

        private bool IsCameraOpen()
        {
            var b = OwnButton();
            return b != null && b.IsOpen();
        }

        // 選んでいる視点の席番号。三麻は手番順（%3）で下家・上家を数える。
        private int TargetSeat()
        {
            int n = sanma_ ? 3 : 4;
            int rel = view_;
            if (sanma_ && rel == VIEW_KAMICHA) { rel = 2; }
            if (sanma_ && rel == VIEW_TOIMEN) { rel = 0; }
            return (seat_ + rel) % n;
        }

        private void OpenCamera()
        {
            var b = OwnButton();
            if (b == null) { return; }
            b.SetMelds(meldHas_, meldLeft_);
            if (b.IsOpen()) { b.AimFromSeat(TargetSeat()); }
            else
            {
                b.OpenFromSeat(TargetSeat());
                RestoreBoardPose(b);
            }
            // Open は PlayerData から読み直す（復元前は既定値）ので、パネルの値で上書きする。
            b.SetAlphaPercent(100 - transparency_);
            b.SetSizePercent(sizeSteps[sizeIndex_]);
        }

        private void CloseCamera()
        {
            var b = OwnButton();
            if (b != null) { b.Close(); }
        }

        // ---- ON/OFF と板の位置（本人の PlayerData）----
        private bool LoadOn()
        {
            var lp = Networking.LocalPlayer;
            bool v;
            if (Utilities.IsValid(lp) && PlayerData.TryGetBool(lp, ON_KEY, out v)) { return v; }
            return false;
        }

        private void SaveOn(bool on)
        {
            PlayerData.SetBool(ON_KEY, on);
        }

        // 自分の席の座標（卓の中心が原点・席の向きで回した座標系）。
        private Quaternion SeatRotation()
        {
            var table = transform.parent;
            var r = table != null ? table.rotation : Quaternion.identity;
            return r * Quaternion.Euler(0.0f, seat_ * -90.0f, 0.0f);
        }

        private Vector3 TableCenter()
        {
            var table = transform.parent;
            return table != null ? table.position : transform.position;
        }

        private void SaveBoardPose()
        {
            var b = OwnButton();
            if (b == null || b.view == null || seat_ < 0) { return; }
            var inv = Quaternion.Inverse(SeatRotation());
            var t = b.view.transform;
            PlayerData.SetVector3(POS_KEY, inv * (t.position - TableCenter()));
            PlayerData.SetQuaternion(ROT_KEY, inv * t.rotation);
            PlayerData.SetBool(POS_VALID_KEY, true);
        }

        // 位置の記録を捨てる（本人がOFFを押したとき）。次に開くと OpenFromSeat の既定の位置に出る。
        private void ClearBoardPose()
        {
            PlayerData.SetBool(POS_VALID_KEY, false);
        }

        // 保存した位置があればそこへ置く（無い・遠すぎるときは OpenFromSeat の既定の位置のまま）。
        private void RestoreBoardPose(MahjongTopCameraViewButton b)
        {
            if (b == null || b.view == null || seat_ < 0) { return; }
            var lp = Networking.LocalPlayer;
            if (!Utilities.IsValid(lp)) { return; }
            bool valid;
            if (!PlayerData.TryGetBool(lp, POS_VALID_KEY, out valid) || !valid) { return; }
            Vector3 p;
            Quaternion q;
            if (!PlayerData.TryGetVector3(lp, POS_KEY, out p)) { return; }
            if (!PlayerData.TryGetQuaternion(lp, ROT_KEY, out q)) { return; }
            if (p.magnitude > BOARD_MAX_DISTANCE) { return; }
            var r = SeatRotation();
            b.view.transform.position = TableCenter() + r * p;
            b.view.transform.rotation = r * q;
        }

        // ---- 透明度（本人の PlayerData）----
        private void LoadTransparency()
        {
            if (transparencyLoaded_) { return; }

            var lp = Networking.LocalPlayer;
            int v;
            if (Utilities.IsValid(lp) && PlayerData.TryGetInt(lp, MahjongTopCameraViewButton.ALPHA_KEY, out v))
            {
                transparency_ = ClampTransparency(100 - v);
                transparencyLoaded_ = true;
            }
        }

        private int ClampTransparency(int t)
        {
            if (t < 0) { t = 0; }
            if (t > TRANSPARENCY_MAX) { t = TRANSPARENCY_MAX; }
            return (t / TRANSPARENCY_STEP) * TRANSPARENCY_STEP;
        }

        private void SetTransparency(int t)
        {
            transparency_ = ClampTransparency(t);
            transparencyLoaded_ = true;
            PlayerData.SetInt(MahjongTopCameraViewButton.ALPHA_KEY, 100 - transparency_);

            var b = OwnButton();
            if (b != null) { b.SetAlphaPercent(100 - transparency_); }
        }

        // ---- 大きさ（本人の PlayerData）----
        private void LoadSize()
        {
            if (sizeLoaded_) { return; }

            var lp = Networking.LocalPlayer;
            int v;
            if (Utilities.IsValid(lp) && PlayerData.TryGetInt(lp, MahjongTopCameraViewButton.SIZE_KEY, out v))
            {
                sizeIndex_ = NearestSizeIndex(v);
                sizeLoaded_ = true;
            }
        }

        private int NearestSizeIndex(int percent)
        {
            int best = 0;
            for (int i = 1; i < sizeSteps.Length; ++i)
            {
                if (Mathf.Abs(sizeSteps[i] - percent) < Mathf.Abs(sizeSteps[best] - percent)) { best = i; }
            }
            return best;
        }

        private void SetSizeIndex(int index)
        {
            if (index < 0) { index = 0; }
            if (index >= sizeSteps.Length) { index = sizeSteps.Length - 1; }
            sizeIndex_ = index;
            sizeLoaded_ = true;
            PlayerData.SetInt(MahjongTopCameraViewButton.SIZE_KEY, sizeSteps[sizeIndex_]);

            var b = OwnButton();
            if (b != null) { b.SetSizePercent(sizeSteps[sizeIndex_]); }
        }

        // ---- 押下（MahjongPushButton: local=true / disableOnPush=false）----
        public void _CamTab()
        {
            expanded_ = !expanded_;
            Render();
        }

        public void _CamShow()
        {
            if (IsCameraOpen())
            {
                // 本人のOFF＝位置もリセット（次のONは既定の位置）。終局で消えるとき（Hide）は位置を残す。
                ClearBoardPose();
                CloseCamera();
                SaveOn(false);
            }
            else
            {
                OpenCamera();
                SaveOn(true);
            }
            Render();
        }

        public void _CamView0() { SelectView(VIEW_SELF); }
        public void _CamView1() { SelectView(VIEW_SHIMOCHA); }
        public void _CamView2() { SelectView(VIEW_TOIMEN); }
        public void _CamView3() { SelectView(VIEW_KAMICHA); }

        private void SelectView(int v)
        {
            if (sanma_ && v == VIEW_TOIMEN) { return; }
            view_ = v;
            OpenCamera();
            SaveOn(true);
            Render();
        }

        public void _CamAlphaDown()
        {
            SetTransparency(transparency_ - TRANSPARENCY_STEP);
            Render();
        }

        public void _CamAlphaUp()
        {
            SetTransparency(transparency_ + TRANSPARENCY_STEP);
            Render();
        }

        public void _CamSizeDown()
        {
            SetSizeIndex(sizeIndex_ - 1);
            Render();
        }

        public void _CamSizeUp()
        {
            SetSizeIndex(sizeIndex_ + 1);
            Render();
        }
    }
}
