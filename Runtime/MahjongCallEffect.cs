using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    //  宣言演出（雀魂スタイル）
    // ------------------------------------------------------------
    //  見た目は宣言の種類で2通り。雀魂の使い分けに合わせてある。
    //   (A) 帯カットイン … チー/ポン/カン/リーチ/北抜き
    //       黒い斜めの帯が横に開き、右からキャラ立ち絵が入り、左下に筆文字。
    //   (B) 紅雲カットイン … ロン/ツモ
    //       紅い流雲がひと塊で飛び込み、その上に金グラデの筆文字が乗る。
    //       出る直前に予兆が入る＝同じ筆文字が拡大（既定2倍）から縮みつつフェードインし、
    //       終わりで一気に等倍の本体へ切り替わる。予兆と本体は色もテクスチャも同じで、
    //       切り替えで動くのはスケールだけ。
    //       筆文字だけは UI Text ではなく焼いたテクスチャを使う。単色の Text では
    //       参照映像の縦グラデ（上=濃橙／下=クリーム）とクリームの縁が出せない。
    //
    //  設計思想（MahjongReachDiscardEffect / MahjongAgariEffect と同じ）:
    //   ・純ローカル表示。同期変数を一切持たない（NoVariableSync）。
    //     「誰が何を宣言したか」は Mahjong.cs 側の同期済み状態（numEatedSets /
    //     eatedSetsTypes / reachIndex / numKita）から各クライアントが自前で
    //     エッジ検知するので、全員が同じ入力で Play() を叩く＝追加同期ゼロ。
    //   ・パーティクルは使わず World Space Canvas 上の Text / RawImage を
    //     RectTransform のスケール／anchoredPosition／color.a だけで動かす（Quest基準）。
    //   ・スクリプトが載るルートは常時アクティブ。表示ON/OFFは子の fxRoot だけで行う
    //     （非アクティブな UdonBehaviour は外部から叩けない）。
    //   ・立ち絵は和了者コスメと同じ流儀で「キャラIDでテクスチャを引く」だけ。
    //     どのキャラかは Mahjong 側の ResolveSeatChara が同期済み情報から決めるので、
    //     ここでも追加同期は要らない。
    //
    //  表示位置:
    //   宣言した席の手牌の中央（Mahjong.cs が実際の牌 Transform から平均して渡す）。
    //   卓の中心ではなく手牌の上に出るので、誰が宣言したのかが位置で分かる。
    //
    //  重なりについて:
    //   ダブロン・トリロンでは複数席が同時に宣言する。1リグ＝1本の演出なので
    //   後勝ちで上書きされる（雀魂も同時表示はしない）。位置だけ最後の宣言者に寄る。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class MahjongCallEffect : UdonSharpBehaviour
    {
        // 宣言の種類。Mahjong.cs から int で渡される（enum は Udon で扱いが面倒なので定数）。
        public const int CALL_CHII = 0;
        public const int CALL_PON = 1;
        public const int CALL_KAN = 2;
        public const int CALL_RIICHI = 3;
        public const int CALL_RON = 4;
        public const int CALL_TSUMO = 5;
        public const int CALL_KITA = 6;
        public const int CALL_COUNT = 7;
        // チュンビーム。宣言ではないので CALL_COUNT の外。PlayChunBeam からだけ使う。
        public const int CALL_CHUN = 7;

        [Header("必須参照")]
        [Tooltip("演出全体のルート。非アクティブにして置いておく（演出中だけONになる）。")]
        public GameObject fxRoot;

        [Tooltip("Canvas のルート。演出中は毎フレーム自分の方へ水平ビルボードする。")]
        public Transform billboardRoot;

        [Header("(A) 帯カットイン ― チー/ポン/カン/リーチ/北抜き")]
        [Tooltip("帯カットインのパーツをまとめた親。ロン/ツモでは丸ごと畳む。")]
        public GameObject bandGroup;

        [Tooltip("黒い斜めの帯。横に開いて閉じる。")]
        public RawImage bandImage;
        public RectTransform bandRect;

        [Tooltip("帯の下辺に走る宣言色のライン。無くても成立する。")]
        public RawImage bandEdgeImage;
        public RectTransform bandEdgeRect;

        [Tooltip("キャラ立ち絵。右から入る。")]
        public RawImage portraitImage;
        public RectTransform portraitRect;

        [Tooltip("キャラIDで引く立ち絵。並びは Mahjong 側の ResolveSeatChara と揃えること。")]
        public Texture[] charaPortraits;

        [Tooltip("キャラ未選択・範囲外のときに使う立ち絵。未割当なら立ち絵なしで帯だけ出る。")]
        public Texture defaultPortrait;

        [Tooltip("自画像キャラの共有ストア（任意）。割り当てると、自画像を選んでいる席は"
                 + "その人が撮った写真を立ち絵として出す。未割当なら既定立ち絵に落ちる。")]
        public SelfiePortraitStore selfieStore;

        [Header("(B) 墨しぶき ― ロン/ツモ")]
        [Tooltip("墨しぶきのパーツをまとめた親。鳴き系では丸ごと畳む。")]
        public GameObject inkGroup;

        [Tooltip("墨しぶき。文字の背後で不規則に広がる。")]
        public RawImage inkImage;
        public RectTransform inkRect;

        [Tooltip("横に走る筆の帯。文字より先に走って軌跡を作る。")]
        public RawImage streakImage;
        public RectTransform streakRect;

        [Tooltip("ちぎれ雲。雲本体から左へ流れて消える。0枚でも成立する。")]
        public RawImage[] dropImages;
        public RectTransform[] dropRects;

        [Tooltip("焼いた筆文字（金グラデ＋クリームのフチ）。ロン/ツモで差し替える。")]
        public RawImage glyphImage;
        public RectTransform glyphRect;

        [Tooltip("予兆の筆文字。glyph と同じテクスチャを大きく出して縮めながら見せる。"
                 + "位置・傾き・定位置スケールは glyph 側の値を共用する。")]
        public RawImage ghostImage;
        public RectTransform ghostRect;

        [Tooltip("「ロン」の焼き文字。")]
        public Texture ronGlyph;

        [Tooltip("「ツモ」の焼き文字。")]
        public Texture tsumoGlyph;

        [Tooltip("雲本体の濃さ。参照映像は卓の柄がうっすら透ける程度。")]
        [Range(0.0f, 1.0f)]
        public float cloudAlpha = 0.88f;

        [Tooltip("後ろに敷く霞の濃さ。雲の輪郭の外へ紅がにじんで見える。")]
        [Range(0.0f, 1.0f)]
        public float hazeAlpha = 0.30f;

        [Tooltip("ちぎれ雲の濃さ。")]
        [Range(0.0f, 1.0f)]
        public float puffAlpha = 0.80f;

        [Tooltip("霞が流れる距離（px）。")]
        public float hazeDriftDistance = 54.0f;

        [Tooltip("ちぎれ雲が流れる距離（px）。左＝筆を振り抜いた逆向きへ散る。")]
        public float puffDistance = 150.0f;

        [Tooltip("焼き文字の大きさ倍率。")]
        public float glyphScale = 1.0f;

        [Tooltip("飛び出し時の焼き文字の初期スケール倍率。1=拡大しない（予兆が大きさの芝居を担う）。")]
        public float glyphPunchScale = 1.0f;

        [Tooltip("飛び出し時に焼き文字が下から上がってくる距離（px）。0=動かさない。")]
        public float glyphRiseDistance = 0.0f;

        [Tooltip("予兆の筆文字の色。既定は白＝焼いたテクスチャをそのまま出す"
                 + "（本体と同じ絵になるので、切り替わりで大きさ以外は動かない）。")]
        public Color ghostColor = new Color(0.05f, 0.015f, 0.03f, 0.40f);

        [Tooltip("予兆の筆文字の大きさ倍率。本体よりずっと大きくする。")]
        public float ghostScale = 2.45f;

        [Header("筆文字（両モード共通）")]
        [Tooltip("筆文字（前面）。白フチは Outline コンポーネントが担当する。")]
        public Text callText;
        public RectTransform callTextRect;

        [Tooltip("筆文字の影（背面）。前面より少しずらして暗色で敷き、背景に負けないようにする。")]
        public Text callShadowText;
        public RectTransform callShadowRect;

        [Tooltip("帯カットイン時の文字の基準位置（左下）。")]
        public Vector2 bandTextHome = new Vector2(-330.0f, -190.0f);

        [Tooltip("墨しぶき時の文字の基準位置（中央）。")]
        public Vector2 inkTextHome = new Vector2(0.0f, 0.0f);

        // Unity の UI Text はフォントサイズを 300 で頭打ちにする（それ以上を書いても
        // シリアライズ時に丸められる）。大きく出したいぶんは RectTransform のスケールで稼ぐ。
        [Tooltip("帯カットイン時の文字の大きさ倍率。左下に置くぶん墨しぶきより小さくする。"
               + "小さくしすぎると筆線が細くなり、白フチに食われて字が白く見える。")]
        public float bandTextScale = 0.88f;

        [Tooltip("墨しぶき時の文字の大きさ倍率。中央に大きく出す。")]
        public float inkTextScale = 1.0f;

        [Tooltip("筆文字の影のずらし量（px）。")]
        public Vector2 shadowOffset = new Vector2(7.0f, -7.0f);

        [Header("タイミング（秒）")]
        [Tooltip("開き切るまでの時間。雀魂基準は約0.16秒。")]
        public float enterSeconds = 0.16f;

        [Tooltip("開き切ってからの保持時間。")]
        public float holdSeconds = 0.62f;

        [Tooltip("閉じるまでの時間。")]
        public float exitSeconds = 0.26f;

        [Header("タイミング（紅雲・秒）")]
        [Tooltip("予兆の巨大筆文字がフェードインしている時間。切れると本体が通常サイズで出る。")]
        public float ghostSeconds = 0.10f;

        [Tooltip("雲と筆文字が出るまでの時間。ほぼ一瞬。")]
        public float cloudEnterSeconds = 0.07f;

        [Tooltip("出し切ってからの保持時間。参照映像はこの間ほぼ静止する。")]
        public float cloudHoldSeconds = 0.60f;

        [Tooltip("消えるまでの時間。参照映像は一気に切れる。")]
        public float cloudExitSeconds = 0.11f;

        [Header("動きの量")]
        [Tooltip("立ち絵が右から入ってくる距離（Canvas ローカル px）。")]
        public float portraitSlideDistance = 190.0f;

        [Tooltip("筆文字が横から入ってくる距離（px）。帯モードは左から、墨モードは右から入る。")]
        public float textSlideDistance = 240.0f;

        [Tooltip("飛び出し時の文字の初期スケール倍率。")]
        public float textPunchScale = 1.55f;

        [Tooltip("退場時に文字がふわっと持ち上がる距離（px）。")]
        public float textDriftDistance = 30.0f;

        [Tooltip("文字の傾き（度）。雀魂の筆文字は軽く右上がり。")]
        public float textTiltDegrees = -6.0f;

        [Tooltip("墨しぶきの最終スケール倍率。")]
        public float inkScale = 1.0f;

        [Tooltip("墨の粒が飛ぶ距離（px）。")]
        public float dropDistance = 190.0f;

        [Header("配置")]
        [Tooltip("手牌の中央から持ち上げる高さ（m）。牌に被らせないぶん浮かせる。")]
        public float heightOffset = 0.10f;

        [Tooltip("手牌の中央から卓の外側（席の手前）へずらす距離（m）。0で真上。")]
        public float outwardOffset = 0.0f;

        [Header("色（宣言ごと）")]
        public Color chiiColor = new Color(0.40f, 0.95f, 0.52f, 1.0f);
        public Color ponColor = new Color(1.00f, 0.68f, 0.22f, 1.0f);
        public Color kanColor = new Color(0.80f, 0.56f, 1.00f, 1.0f);
        public Color riichiColor = new Color(0.55f, 0.86f, 1.00f, 1.0f);
        public Color ronColor = new Color(1.00f, 0.26f, 0.28f, 1.0f);
        public Color tsumoColor = new Color(1.00f, 0.84f, 0.32f, 1.0f);

        [Tooltip("北抜きの色。参照デザインに合わせた濃いピンク。")]
        public Color kitaColor = new Color(1.00f, 0.28f, 0.62f, 1.0f);

        [Tooltip("帯の色。ほぼ黒。")]
        public Color bandColor = new Color(0.045f, 0.035f, 0.06f, 0.93f);

        [Tooltip("筆文字の影の色。宣言色に関わらず共通の暗色。")]
        public Color shadowColor = new Color(0.04f, 0.02f, 0.06f, 0.85f);

        [Tooltip("墨しぶきの色を宣言色でどれだけ染めるか。0=黒い墨のまま／1=宣言色そのもの。"
               + "上げすぎると筆文字と同系色になって字が墨に埋もれる。")]
        [Range(0.0f, 1.0f)]
        public float inkTint = 0.34f;

        // ============================================================
        //  チュンビーム（中を捨てたときの帯カットイン。麻雀ファイトガールのオマージュ）
        //  帯カットインの上で、立ち絵の両目からビームが左へ走る。局内の枚数で段が上がる：
        //   1=チュンビーム / 2=ダブル（太く・2連射）/ 3=トリプル（金・帯が揺れる）
        //   4=ファイナル（白飛び・大きい文字・長め）
        //  部品は Build Chun Beam (Prefab) が立ち絵の子 ChunGroup（Flash／Glow／Core／Flare）に作る。
        //  配線はせず名前で引く（見つからなければビーム無しで帯だけ出る）。
        //  立ち絵の子にしてあるのは、既存の Build Call Effects が Canvas と BandGroup の
        //  知らない子を消すため（Portrait の子は触らない）。白飛び（Flash）も同じ理由でここ。
        // ============================================================
        [Header("チュンビーム")]
        [Tooltip("目の位置（元画像に対する割合。x=左から / y=上から）。キャラIDごとに 左目x,左目y,右目x,右目y の4つ。"
                 + "0=Vol / 1=Shine。")]
        public float[] chunEyes = new float[] { 0.460f, 0.225f, 0.597f, 0.215f, 0.379f, 0.197f, 0.503f, 0.188f };

        [Tooltip("ビームの長さ（Canvas px）。目から左へ伸びる。")]
        public float chunBeamLength = 1180.0f;

        [Tooltip("ビームの太さ（px）。段ごとに倍率が掛かる。")]
        public float chunBeamWidth = 46.0f;

        [Tooltip("ビームの傾き（度）。左へ行くほど下がる。")]
        public float chunBeamAngle = 9.0f;

        [Tooltip("左右のビームの開き（度）。0だと2本が重なって1本に見える。")]
        public float chunBeamSpread = 3.5f;

        [Tooltip("帯が開いてからビームが出るまで（秒）。立ち絵が入りきるのを待つ。")]
        public float chunBeamDelay = 0.20f;

        [Tooltip("ビームが伸びきるまで（秒）。")]
        public float chunBeamGrowSeconds = 0.10f;

        [Tooltip("段ごとの保持時間（秒）。ボイスが長ければそちらに合わせて伸びる。")]
        public float[] chunHoldSeconds = new float[] { 1.00f, 1.10f, 1.30f, 1.70f };

        [Tooltip("段ごとのビームの太さ倍率。")]
        public float[] chunWidthScales = new float[] { 1.0f, 1.45f, 1.75f, 2.3f };

        [Tooltip("段ごとの色（ビームのにじみ・文字・帯のライン）。")]
        public Color[] chunColors = new Color[]
        {
            new Color(1.00f, 0.18f, 0.22f, 1.0f),
            new Color(1.00f, 0.36f, 0.12f, 1.0f),
            new Color(1.00f, 0.80f, 0.22f, 1.0f),
            new Color(1.00f, 0.92f, 0.55f, 1.0f),
        };

        [Tooltip("帯カットイン時の文字の大きさ倍率（チュンビーム）。長いので宣言より小さい。")]
        public float chunTextScale = 0.40f;

        [Tooltip("ファイナルの文字の大きさ倍率。")]
        public float chunFinalTextScale = 0.48f;

        [Tooltip("チュンビームの文字の基準位置。")]
        public Vector2 chunTextHome = new Vector2(-300.0f, -170.0f);

        [Tooltip("トリプル以上で帯が揺れる幅（Canvas px）。")]
        public float chunShakePixels = 10.0f;

        [Tooltip("ファイナルの白飛びの濃さ。")]
        [Range(0.0f, 1.0f)]
        public float chunFlashAlpha = 0.85f;

        // SEは持たない（2026-09-01 に削除）。
        // 宣言そのものは MahjongSoundsV2 がキャラボイスで鳴らす。帯が開く音／墨が飛ぶ音は
        // 「鳴らさない」と決めた枠なので、空のクリップ欄と使われない AudioSource を残さない。
        // （ロン/ツモの風切り音も、あがり画面側の演出が担当する＝ここで鳴らすと二重になる。）

        // ---- 再生中のローカル状態（同期しない）----
        private float startTime_ = -1.0f;   // <0 = 非表示
        private bool bandMode_ = true;      // true=帯カットイン／false=墨しぶき
        private Color activeColor_ = Color.white;
        private Vector2 textHome_ = Vector2.zero;
        private float[] dropAngles_;   // 水平からの仰角（度）。上下どちらにも振る。
        private float[] dropSides_;    // +1=右へ／-1=左へ。仰角と独立させて左右対称に散らす。
        private float[] dropSpeeds_;

        private bool layoutCached_ = false;
        private Vector3 glyphHomeScale_ = Vector3.one;
        private Vector3 ghostHomeScale_ = Vector3.one;
        private Vector2 glyphHome_ = Vector2.zero;
        private Vector2 ghostHome_ = Vector2.zero;
        private Vector2 streakHome_ = Vector2.zero;
        private Vector2[] dropHomes_;
        private Vector3[] dropHomeScales_;
        private float cloudR_ = 1.0f;
        private float cloudG_ = 1.0f;
        private float cloudB_ = 1.0f;
        private Vector3 textHomeScale_ = Vector3.one;
        private Vector3 shadowHomeScale_ = Vector3.one;
        private Vector3 bandHomeScale_ = Vector3.one;
        private Vector3 bandEdgeHomeScale_ = Vector3.one;
        private Vector3 portraitHomeScale_ = Vector3.one;
        private Vector3 inkHomeScale_ = Vector3.one;
        private Vector3 streakHomeScale_ = Vector3.one;
        private Vector2 portraitHome_ = Vector2.zero;

        // ---- チュンビームの状態（同期しない）----
        private int chunLevel_ = 0;          // 0=チュンビームではない / 1..4=段
        private float chunHold_ = -1.0f;     // 帯の保持時間の上書き（<0=既定の holdSeconds）
        private float chunTextScaleNow_ = 0.40f;
        private bool chunResolved_ = false;
        private GameObject chunGroup_;
        private RectTransform[] chunGlowRects_;
        private RawImage[] chunGlowImages_;
        private RectTransform[] chunCoreRects_;
        private RawImage[] chunCoreImages_;
        private RectTransform[] chunFlareRects_;
        private RawImage[] chunFlareImages_;
        private RawImage chunFlash_;
        private RectTransform canvasRect_;
        private Vector3 canvasHome_ = Vector3.zero;

        void Start()
        {
            CacheLayout();

            // 墨の粒は左右へ低く散らす（筆を振り抜いた飛沫のイメージ）。
            int n = dropRects != null ? dropRects.Length : 0;
            dropAngles_ = new float[n];
            dropSides_ = new float[n];
            dropSpeeds_ = new float[n];
            for (int i = 0; i < n; ++i)
            {
                // 左右交互（side）に、仰角は上下交互（elevation）。掛け合わせて四方に散る。
                dropSides_[i] = (i % 2 == 0) ? 1.0f : -1.0f;

                float elevation = ((i / 2) + 1) * 16.0f;
                if (elevation > 34.0f) { elevation = 34.0f; }
                dropAngles_[i] = ((i / 2) % 2 == 0) ? elevation : -elevation;

                dropSpeeds_[i] = 0.72f + 0.14f * (i % 3);
            }

            HideNow();
        }

        private void CacheLayout()
        {
            if (layoutCached_) { return; }

            if (callTextRect != null) { textHomeScale_ = callTextRect.localScale; }
            if (callShadowRect != null) { shadowHomeScale_ = callShadowRect.localScale; }
            if (bandRect != null) { bandHomeScale_ = bandRect.localScale; }
            if (bandEdgeRect != null) { bandEdgeHomeScale_ = bandEdgeRect.localScale; }
            if (portraitRect != null)
            {
                portraitHomeScale_ = portraitRect.localScale;
                portraitHome_ = portraitRect.anchoredPosition;
            }
            if (inkRect != null) { inkHomeScale_ = inkRect.localScale; }
            if (streakRect != null)
            {
                streakHomeScale_ = streakRect.localScale;
                streakHome_ = streakRect.anchoredPosition;
            }
            if (glyphRect != null)
            {
                glyphHomeScale_ = glyphRect.localScale;
                glyphHome_ = glyphRect.anchoredPosition;
            }
            if (ghostRect != null)
            {
                ghostHomeScale_ = ghostRect.localScale;
                ghostHome_ = ghostRect.anchoredPosition;
            }

            // ちぎれ雲の定位置はビルダーが置いた位置がそのまま正（レイアウトはビルダーの仕事）。
            int dn = dropRects != null ? dropRects.Length : 0;
            dropHomes_ = new Vector2[dn];
            dropHomeScales_ = new Vector3[dn];
            for (int i = 0; i < dn; ++i)
            {
                if (dropRects[i] == null) { continue; }
                dropHomes_[i] = dropRects[i].anchoredPosition;
                dropHomeScales_[i] = dropRects[i].localScale;
            }

            layoutCached_ = true;
        }

        private void HideNow()
        {
            startTime_ = -1.0f;
            ResetChunParts();
            if (fxRoot != null) { fxRoot.SetActive(false); }
        }

        /// <summary>チュンビームを再生中なら打ち切る（その中でロンされたとき）。宣言演出は止めない。</summary>
        public void CancelChunBeam()
        {
            if (chunLevel_ > 0 && startTime_ >= 0.0f) { HideNow(); }
        }

        /// <summary>チュンビームを再生中か。</summary>
        public bool IsChunBeamPlaying()
        {
            return chunLevel_ > 0 && startTime_ >= 0.0f;
        }

        /// <summary>
        /// 今出ている演出が終わる時刻（Time.time 基準）。何も出ていなければ 0。
        /// 自動北抜き・自動ツモが、直前のカンの宣言を上書きしないよう待つのに使う。
        /// </summary>
        public float GetEndTime()
        {
            if (startTime_ < 0.0f) { return 0.0f; }
            return startTime_ + GetDuration();
        }

        /// <summary>演出全体の長さ（秒）。Mahjong.cs 側で後続演出の待ち時間に使う。</summary>
        public float GetDuration()
        {
            return DurationOf(bandMode_);
        }

        /// <summary>帯カットインと紅雲では尺が違う。帯＝開いて閉じる／紅雲＝出して切る。</summary>
        private float DurationOf(bool band)
        {
            if (band)
            {
                return Mathf.Max(0.0f, enterSeconds)
                     + Mathf.Max(0.0f, CurrentHoldSeconds())
                     + Mathf.Max(0.0f, exitSeconds);
            }

            return Mathf.Max(0.0f, ghostSeconds)
                 + Mathf.Max(0.0f, cloudEnterSeconds)
                 + Mathf.Max(0.0f, cloudHoldSeconds)
                 + Mathf.Max(0.0f, cloudExitSeconds);
        }

        /// <summary>
        /// 宣言演出を出す。
        /// worldPos は宣言した席の手牌の中央（Mahjong.cs が実際の牌から平均して渡す）。
        /// charaId は立ち絵を引くためのキャラID（範囲外なら defaultPortrait）。
        /// </summary>
        public void Play(int callType, Vector3 worldPos, int charaId)
        {
            PlayAs(callType, worldPos, charaId, 0);
        }

        /// <summary>
        /// 宣言演出を出す（自画像キャラ対応版）。
        /// playerId は宣言した席のプレイヤーID。自画像を選んでいる人の写真を引くのに使う。
        /// 0 なら従来どおりキャラIDだけで立ち絵を引く（CPU席など）。
        /// </summary>
        public void PlayAs(int callType, Vector3 worldPos, int charaId, int playerId)
        {
            if (callType < 0 || callType >= CALL_COUNT) { return; }
            // 宣言がチュンビームを上書きする場合は、チュンの部品を先に畳む。
            ResetChunParts();
            chunLevel_ = 0;
            chunHold_ = -1.0f;
            PlayInternal(callType, worldPos, charaId, playerId);
        }

        /// <summary>
        /// チュンビームを出す。level は局内で何枚目の中か（1..4、5以上は4）。
        /// voiceSeconds はボイスの長さ（0=声なし）。声が長ければ帯の保持を伸ばして合わせる。
        /// </summary>
        public void PlayChunBeam(Vector3 worldPos, int charaId, int playerId, int level, float voiceSeconds)
        {
            if (level < 1) { return; }
            if (level > 4) { level = 4; }

            chunLevel_ = level;
            float hold = (chunHoldSeconds != null && chunHoldSeconds.Length >= level)
                ? chunHoldSeconds[level - 1]
                : holdSeconds;
            // 声が終わるまで帯を開けておく（帯が開く時間ぶんは声が先に始まっている）。
            float voiceHold = voiceSeconds - Mathf.Max(0.0f, enterSeconds) + 0.10f;
            if (voiceHold > hold) { hold = voiceHold; }
            chunHold_ = hold;
            chunTextScaleNow_ = level >= 4 ? chunFinalTextScale : chunTextScale;

            PlayInternal(CALL_CHUN, worldPos, charaId, playerId);
            SetupChun(charaId);
            Apply(0.0f);
        }

        private float CurrentHoldSeconds()
        {
            return chunHold_ >= 0.0f ? chunHold_ : holdSeconds;
        }

        private void PlayInternal(int callType, Vector3 worldPos, int charaId, int playerId)
        {
            portraitPlayerId_ = playerId;

            if (fxRoot == null) { return; }

            CacheLayout();

            // ロン/ツモだけ墨しぶき。それ以外は帯カットイン（雀魂の使い分け）。
            bandMode_ = !(callType == CALL_RON || callType == CALL_TSUMO);

            activeColor_ = ResolveColor(callType);
            textHome_ = bandMode_ ? (chunLevel_ > 0 ? chunTextHome : bandTextHome) : inkTextHome;

            string glyph = ResolveGlyph(callType);

            if (callText != null)
            {
                callText.text = glyph;
                callText.color = new Color(activeColor_.r, activeColor_.g, activeColor_.b, 0.0f);
            }
            if (callShadowText != null)
            {
                callShadowText.text = glyph;
                callShadowText.color = new Color(shadowColor.r, shadowColor.g, shadowColor.b, 0.0f);
            }

            if (bandGroup != null) { bandGroup.SetActive(bandMode_); }
            if (inkGroup != null) { inkGroup.SetActive(!bandMode_); }

            // 紅雲モードの筆文字は焼いたテクスチャなので、UI Text 側は畳む。
            // （両方出すと同じ字が二重に重なる。）
            if (callText != null) { callText.gameObject.SetActive(bandMode_); }
            if (callShadowText != null) { callShadowText.gameObject.SetActive(bandMode_); }

            if (bandMode_)
            {
                SetupBand(charaId);
            }
            else
            {
                Texture glyphTex = (callType == CALL_TSUMO) ? tsumoGlyph : ronGlyph;
                if (glyphImage != null) { glyphImage.texture = glyphTex; }
                if (ghostImage != null) { ghostImage.texture = glyphTex; }
                SetupInk();
            }

            transform.position = worldPos + Vector3.up * heightOffset;

            fxRoot.SetActive(true);
            startTime_ = Time.time;

            Apply(0.0f);
        }

        // PlayAs で渡された宣言者のプレイヤーID（自画像の引き当てにだけ使う）。
        private int portraitPlayerId_ = 0;

        private void SetupBand(int charaId)
        {
            if (bandImage != null)
            {
                bandImage.color = new Color(bandColor.r, bandColor.g, bandColor.b, 0.0f);
            }
            if (bandEdgeImage != null)
            {
                bandEdgeImage.color = new Color(activeColor_.r, activeColor_.g, activeColor_.b, 0.0f);
            }
            if (portraitImage != null)
            {
                Texture portrait = ResolvePortrait(charaId);
                portraitImage.texture = portrait;
                portraitImage.color = new Color(1.0f, 1.0f, 1.0f, 0.0f);
                portraitImage.gameObject.SetActive(portrait != null);
            }
        }

        private void SetupInk()
        {
            // 雲は宣言色をそのまま乗せる（ロン=紅／ツモ=金）。
            // 以前は黒い墨を宣言色で染めていたが、参照映像は「色つきの流雲」なので
            // 黒を混ぜない。濃淡はテクスチャ側の RGB（0.78〜1.0のムラ）が作る。
            cloudR_ = activeColor_.r;
            cloudG_ = activeColor_.g;
            cloudB_ = activeColor_.b;

            if (inkImage != null) { inkImage.color = new Color(cloudR_, cloudG_, cloudB_, 0.0f); }
            if (streakImage != null)
            {
                // 霞は既定で切ってある。同じ雲を薄く重ねると輪郭が二重に見えて濁るだけで、
                // 参照映像にもそんな層は無い。透明でも描画コールは出るので丸ごと畳む。
                streakImage.gameObject.SetActive(hazeAlpha > 0.001f);
                streakImage.color = new Color(cloudR_, cloudG_, cloudB_, 0.0f);
            }

            int n = dropImages != null ? dropImages.Length : 0;
            for (int i = 0; i < n; ++i)
            {
                if (dropImages[i] == null) { continue; }
                dropImages[i].color = new Color(cloudR_, cloudG_, cloudB_, 0.0f);
            }

            // 焼き文字は色を持たない（グラデはテクスチャに入っている）。白で素通し。
            if (glyphImage != null) { glyphImage.color = new Color(1.0f, 1.0f, 1.0f, 0.0f); }
            if (ghostImage != null)
            {
                ghostImage.color = new Color(ghostColor.r, ghostColor.g, ghostColor.b, 0.0f);
            }
        }

        void Update()
        {
            if (startTime_ < 0.0f) { return; }

            float elapsed = Time.time - startTime_;
            float total = GetDuration();

            if (elapsed >= total)
            {
                HideNow();
                return;
            }

            FaceLocalViewer();
            Apply(elapsed);
        }

        /// <summary>Canvas をローカルプレイヤーの方へ水平だけ向ける（見上げ・見下ろしはしない）。</summary>
        private void FaceLocalViewer()
        {
            if (billboardRoot == null) { return; }

            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null || !Utilities.IsValid(localPlayer)) { return; }

            VRCPlayerApi.TrackingData head =
                localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);

            Vector3 forward = billboardRoot.position - head.position;
            forward.y = 0.0f;
            if (forward.sqrMagnitude < 0.0001f) { return; }

            billboardRoot.rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
        }

        /// <summary>経過時間から各パーツの姿勢と透明度を作る。</summary>
        private void Apply(float elapsed)
        {
            if (!bandMode_) { ApplyCloudMode(elapsed); return; }

            float enter = Mathf.Max(0.0001f, enterSeconds);
            float hold = Mathf.Max(0.0f, CurrentHoldSeconds());
            float exit = Mathf.Max(0.0001f, exitSeconds);

            float enterIn = Mathf.Clamp01(elapsed / enter);
            float enterEased = 1.0f - (1.0f - enterIn) * (1.0f - enterIn) * (1.0f - enterIn); // easeOutCubic

            float exitStart = enter + hold;
            float fadeOut = elapsed <= exitStart ? 0.0f : Mathf.Clamp01((elapsed - exitStart) / exit);
            float alpha = 1.0f - fadeOut;

            ApplyBand(enterEased, fadeOut, alpha);
            ApplyText(enterIn, enterEased, fadeOut, alpha);
            if (chunLevel_ > 0) { ApplyChun(elapsed, alpha); }
        }

        // ============================================================
        //  チュンビームの部品
        // ============================================================

        private void ResolveChun()
        {
            if (chunResolved_) { return; }
            chunResolved_ = true;

            chunGlowRects_ = new RectTransform[2];
            chunGlowImages_ = new RawImage[2];
            chunCoreRects_ = new RectTransform[2];
            chunCoreImages_ = new RawImage[2];
            chunFlareRects_ = new RectTransform[2];
            chunFlareImages_ = new RawImage[2];

            if (portraitRect != null)
            {
                Transform group = portraitRect.Find("ChunGroup");
                if (group != null)
                {
                    chunGroup_ = group.gameObject;
                    Transform flash = group.Find("Flash");
                    if (flash != null) { chunFlash_ = flash.GetComponent<RawImage>(); }
                    for (int i = 0; i < 2; ++i)
                    {
                        Transform g = group.Find("Glow" + i);
                        if (g != null)
                        {
                            chunGlowRects_[i] = g.GetComponent<RectTransform>();
                            chunGlowImages_[i] = g.GetComponent<RawImage>();
                        }
                        Transform c = group.Find("Core" + i);
                        if (c != null)
                        {
                            chunCoreRects_[i] = c.GetComponent<RectTransform>();
                            chunCoreImages_[i] = c.GetComponent<RawImage>();
                        }
                        Transform f = group.Find("Flare" + i);
                        if (f != null)
                        {
                            chunFlareRects_[i] = f.GetComponent<RectTransform>();
                            chunFlareImages_[i] = f.GetComponent<RawImage>();
                        }
                    }
                }
            }

            if (billboardRoot != null)
            {
                Transform canvas = billboardRoot.Find("Canvas");
                if (canvas != null)
                {
                    canvasRect_ = canvas.GetComponent<RectTransform>();
                    if (canvasRect_ != null) { canvasHome_ = canvasRect_.localPosition; }
                }
            }
        }

        // チュンの部品を畳み、揺らした Canvas を元の位置へ戻す。
        private void ResetChunParts()
        {
            if (!chunResolved_) { return; }
            if (chunGroup_ != null && chunGroup_.activeSelf) { chunGroup_.SetActive(false); }
            if (chunFlash_ != null && chunFlash_.gameObject.activeSelf) { chunFlash_.gameObject.SetActive(false); }
            if (canvasRect_ != null) { canvasRect_.localPosition = canvasHome_; }
        }

        // 目の位置へビームの根元を置く（立ち絵の矩形内の座標。中心原点）。
        private void SetupChun(int charaId)
        {
            ResolveChun();

            bool hasPortrait = portraitImage != null && portraitImage.gameObject.activeSelf;
            if (chunGroup_ != null) { chunGroup_.SetActive(hasPortrait); }
            if (chunFlash_ != null) { chunFlash_.gameObject.SetActive(chunLevel_ >= 4); }
            if (!hasPortrait || portraitRect == null) { return; }

            int baseIndex = (chunEyes != null && charaId >= 0 && (charaId * 4 + 3) < chunEyes.Length) ? charaId * 4 : 0;
            Rect uv = portraitImage.uvRect;
            Vector2 size = portraitRect.sizeDelta;
            Vector2 eyeSum = Vector2.zero;

            for (int i = 0; i < 2; ++i)
            {
                float ex = 0.5f;
                float ey = 0.25f;
                if (chunEyes != null && baseIndex + i * 2 + 1 < chunEyes.Length)
                {
                    ex = chunEyes[baseIndex + i * 2];
                    ey = chunEyes[baseIndex + i * 2 + 1];
                }
                // 元画像の上からの割合 → テクスチャ座標 → 表示している uvRect の中の位置。
                float u = uv.width > 0.0001f ? (ex - uv.x) / uv.width : 0.5f;
                float v = uv.height > 0.0001f ? ((1.0f - ey) - uv.y) / uv.height : 0.5f;
                Vector2 eye = new Vector2((u - 0.5f) * size.x, (v - 0.5f) * size.y);
                eyeSum += eye;

                // 左目（i=0）は少し下向き、右目は少し上向き＝2本がV字に開く。
                float angle = chunBeamAngle + (i == 0 ? chunBeamSpread : -chunBeamSpread);
                if (chunGlowRects_[i] != null)
                {
                    chunGlowRects_[i].anchoredPosition = eye;
                    chunGlowRects_[i].localRotation = Quaternion.Euler(0.0f, 0.0f, angle);
                }
                if (chunCoreRects_[i] != null)
                {
                    chunCoreRects_[i].anchoredPosition = eye;
                    chunCoreRects_[i].localRotation = Quaternion.Euler(0.0f, 0.0f, angle);
                }
                if (chunFlareRects_[i] != null) { chunFlareRects_[i].anchoredPosition = eye; }
            }

            // ファイナルの白飛びは両目の間から広がる。
            if (chunFlash_ != null) { chunFlash_.rectTransform.anchoredPosition = eyeSum * 0.5f; }
        }

        // ビームの経過。帯の開閉（alpha）に合わせて消える。
        private void ApplyChun(float elapsed, float alpha)
        {
            if (!chunResolved_) { return; }

            Color col = ResolveColor(CALL_CHUN);
            float widthScale = (chunWidthScales != null && chunWidthScales.Length >= chunLevel_)
                ? chunWidthScales[chunLevel_ - 1]
                : 1.0f;

            float t = elapsed - chunBeamDelay;
            float grow = t <= 0.0f ? 0.0f : Mathf.Clamp01(t / Mathf.Max(0.0001f, chunBeamGrowSeconds));
            float growEased = 1.0f - (1.0f - grow) * (1.0f - grow);
            float on = t <= 0.0f ? 0.0f : 1.0f;

            // 撃った瞬間に太く光り、すぐ落ち着く。ダブル以上は少し遅れて2発目。
            float shot = 0.0f;
            if (t > 0.0f) { shot += Mathf.Exp(-t / 0.09f); }
            if (chunLevel_ >= 2 && t > 0.36f) { shot += Mathf.Exp(-(t - 0.36f) / 0.09f); }
            float flicker = 1.0f + 0.10f * Mathf.Sin(elapsed * 47.0f);
            float width = chunBeamWidth * widthScale * (1.0f + 0.8f * shot) * flicker;
            float length = chunBeamLength * growEased;
            float a = on * alpha;

            for (int i = 0; i < 2; ++i)
            {
                if (chunGlowRects_[i] != null) { chunGlowRects_[i].sizeDelta = new Vector2(length, width); }
                if (chunGlowImages_[i] != null) { chunGlowImages_[i].color = new Color(col.r, col.g, col.b, 0.90f * a); }
                if (chunCoreRects_[i] != null) { chunCoreRects_[i].sizeDelta = new Vector2(length, width * 0.34f); }
                if (chunCoreImages_[i] != null) { chunCoreImages_[i].color = new Color(1.0f, 1.0f, 1.0f, a); }
                if (chunFlareRects_[i] != null)
                {
                    float fs = width * 2.6f * (1.0f + 0.5f * shot);
                    chunFlareRects_[i].sizeDelta = new Vector2(fs, fs);
                }
                if (chunFlareImages_[i] != null)
                {
                    chunFlareImages_[i].color = new Color(
                        Mathf.Lerp(col.r, 1.0f, 0.6f), Mathf.Lerp(col.g, 1.0f, 0.6f), Mathf.Lerp(col.b, 1.0f, 0.6f), a);
                }
            }

            // トリプル以上：ビームが出ている間、帯ごと揺らす（撃った直後ほど強い）。
            if (canvasRect_ != null)
            {
                if (chunLevel_ >= 3 && on > 0.0f && alpha > 0.0f)
                {
                    float amp = chunShakePixels * (0.35f + 0.65f * Mathf.Clamp01(shot)) * alpha;
                    float s = canvasRect_.localScale.x;
                    canvasRect_.localPosition = canvasHome_ + new Vector3(
                        Mathf.Sin(elapsed * 83.0f) * amp * s,
                        Mathf.Cos(elapsed * 71.0f) * amp * s,
                        0.0f);
                }
                else
                {
                    canvasRect_.localPosition = canvasHome_;
                }
            }

            // ファイナル：撃った瞬間に白飛び。
            if (chunFlash_ != null && chunLevel_ >= 4)
            {
                float f = t <= 0.0f ? 0.0f : Mathf.Clamp01(1.0f - t / 0.40f);
                chunFlash_.color = new Color(1.0f, 0.97f, 0.86f, chunFlashAlpha * f * f);
            }
        }

        private void ApplyBand(float enterEased, float fadeOut, float alpha)
        {
            // 帯は中央から左右へ開き、退場で一気に閉じる（閉じは開きより速い）。
            float openX = enterEased * (1.0f - fadeOut * fadeOut);
            if (openX < 0.02f) { openX = 0.02f; }

            if (bandRect != null)
            {
                bandRect.localScale = new Vector3(
                    bandHomeScale_.x * openX,
                    bandHomeScale_.y,
                    bandHomeScale_.z);
            }
            if (bandImage != null)
            {
                bandImage.color = new Color(bandColor.r, bandColor.g, bandColor.b, bandColor.a * enterEased);
            }

            if (bandEdgeRect != null)
            {
                bandEdgeRect.localScale = new Vector3(
                    bandEdgeHomeScale_.x * openX,
                    bandEdgeHomeScale_.y,
                    bandEdgeHomeScale_.z);
            }
            if (bandEdgeImage != null)
            {
                bandEdgeImage.color = new Color(
                    activeColor_.r, activeColor_.g, activeColor_.b, enterEased * alpha);
            }

            // 立ち絵は帯がある程度開いてから右スライドで入る。
            if (portraitRect != null)
            {
                float pIn = Mathf.Clamp01((enterEased - 0.30f) / 0.70f);
                float pEased = 1.0f - (1.0f - pIn) * (1.0f - pIn) * (1.0f - pIn);

                float slide = portraitSlideDistance * (1.0f - pEased) + portraitSlideDistance * 0.55f * fadeOut;
                portraitRect.anchoredPosition = portraitHome_ + new Vector2(slide, 0.0f);
                portraitRect.localScale = portraitHomeScale_ * Mathf.Lerp(1.05f, 1.0f, pEased);

                if (portraitImage != null)
                {
                    portraitImage.color = new Color(1.0f, 1.0f, 1.0f, pEased * alpha);
                }
            }
        }

        // 紅雲カットイン（ロン/ツモ）。参照映像の段取りをそのまま時間軸にしてある。
        //   0 .. ghost                     予兆。筆文字が拡大から縮みつつ出る（雲と本体は畳んだまま）
        //   ghost .. +enter                雲と筆文字がほぼ一瞬で出る（わずかにオーバーシュート）
        //   +hold                          ほぼ静止。雲だけがじわり広がり、ちぎれ雲が流れる
        //   +exit                          すぱっと消す（帯モードのように持ち上げない）
        private void ApplyCloudMode(float elapsed)
        {
            float ghost = Mathf.Max(0.0f, ghostSeconds);
            float enter = Mathf.Max(0.0001f, cloudEnterSeconds);
            float hold = Mathf.Max(0.0f, cloudHoldSeconds);
            float exit = Mathf.Max(0.0001f, cloudExitSeconds);

            if (ghostImage != null && ghostRect != null)
            {
                float gt = ghost > 0.0001f ? Mathf.Clamp01(elapsed / ghost) : 1.0f;

                // フェードインの尺(0.12秒)は ghostSeconds と**わざと連動させていない**。
                // 予兆は「濃くなり切ってから、縮みだけで見せる」ので、尺を延ばしても
                // 濃くなる速さは変えない。ghostSeconds を触るときはここも見ること
                // （0.12 より短くすると予兆が濃くなり切る前に本体へ切り替わる）。
                float ga = elapsed < ghost ? Mathf.Clamp01(elapsed / 0.12f) : 0.0f;

                float curveT = gt;
                if (gt > 0.0f && gt < 1.0f)
                {
                    float lower = 0.0f;
                    float upper = 1.0f;
                    for (int i = 0; i < 16; ++i)
                    {
                        curveT = (lower + upper) * 0.5f;
                        float inverseT = 1.0f - curveT;
                        float curveX = 3.0f * inverseT * inverseT * curveT * 0.722f
                            + 3.0f * inverseT * curveT * curveT + curveT * curveT * curveT;
                        if (curveX < gt) { lower = curveT; }
                        else { upper = curveT; }
                    }
                    curveT = (lower + upper) * 0.5f;
                }
                float inverseCurveT = 1.0f - curveT;
                float curveY = 3.0f * inverseCurveT * inverseCurveT * curveT * -0.003f
                    + 3.0f * inverseCurveT * curveT * curveT * 0.284f + curveT * curveT * curveT;

                ghostRect.anchoredPosition = glyphHome_;
                ghostRect.localScale = glyphHomeScale_ * Mathf.LerpUnclamped(ghostScale, glyphScale, curveY);
                ghostRect.localRotation = Quaternion.Euler(0.0f, 0.0f, textTiltDegrees);
                ghostImage.color = new Color(
                    ghostColor.r, ghostColor.g, ghostColor.b, ghostColor.a * ga);
            }

            // 本体は予兆が終わってから動き出す。
            float body = elapsed - ghost;
            if (body < 0.0f) { body = 0.0f; }

            float inT = Mathf.Clamp01(body / enter);
            float eased = 1.0f - (1.0f - inT) * (1.0f - inT) * (1.0f - inT); // easeOutCubic

            float exitStart = enter + hold;
            float outT = body <= exitStart ? 0.0f : Mathf.Clamp01((body - exitStart) / exit);
            float alpha = (1.0f - outT) * eased;

            // 保持中のゆっくりした広がり（静止して見えるが完全には止めない）。
            float spread = Mathf.Clamp01((body - enter) / Mathf.Max(0.0001f, hold + exit));

            // ---- 後ろの霞：雲より一回り大きく薄く。右へ流れる ----
            if (streakRect != null && streakImage != null)
            {
                float hs = Mathf.Lerp(0.62f, 1.0f, eased) * (1.0f + 0.10f * spread);
                streakRect.localScale = new Vector3(
                    streakHomeScale_.x * hs, streakHomeScale_.y * hs, streakHomeScale_.z);
                streakRect.anchoredPosition =
                    streakHome_ + new Vector2(hazeDriftDistance * spread, 0.0f);
                streakImage.color = new Color(cloudR_, cloudG_, cloudB_, hazeAlpha * alpha);
            }

            // ---- 雲本体：横方向へ一気に開く（筆を振り抜いた向き）----
            if (inkRect != null && inkImage != null)
            {
                float sx = Mathf.Lerp(0.72f, 1.0f, eased) * (1.0f + 0.045f * spread);
                float sy = Mathf.Lerp(0.88f, 1.0f, eased) * (1.0f + 0.025f * spread);
                inkRect.localScale = new Vector3(
                    inkHomeScale_.x * sx * inkScale,
                    inkHomeScale_.y * sy * inkScale,
                    inkHomeScale_.z);
                inkImage.color = new Color(cloudR_, cloudG_, cloudB_, cloudAlpha * alpha);
            }

            // ---- ちぎれ雲：定位置から左へ流れて痩せながら消える ----
            int n = dropRects != null ? dropRects.Length : 0;
            for (int i = 0; i < n; ++i)
            {
                if (dropRects[i] == null) { continue; }

                // Start が dropRects.Length で4本まとめて確保するので実行時に食い違わないが、
                // Udon は添字外を踏むと以後そのビヘイビアが丸ごと止まる。和了のたびに通る
                // ループなので、1本だけ見て他を素通しにはしない。
                if (dropHomes_ == null || i >= dropHomes_.Length) { continue; }
                if (dropHomeScales_ == null || i >= dropHomeScales_.Length) { continue; }
                if (dropAngles_ == null || i >= dropAngles_.Length) { continue; }
                if (dropSpeeds_ == null || i >= dropSpeeds_.Length) { continue; }

                float life = Mathf.Clamp01(body / Mathf.Max(0.0001f, enter + hold + exit));
                float drift = 1.0f - (1.0f - life) * (1.0f - life); // easeOutQuad（失速する）

                float rad = dropAngles_[i] * Mathf.Deg2Rad;
                float dist = puffDistance * dropSpeeds_[i] * drift;

                dropRects[i].anchoredPosition = dropHomes_[i]
                    + new Vector2(-dist, Mathf.Sin(rad) * dist * 0.30f);

                float shrink = Mathf.Lerp(1.0f, 0.52f, drift);
                dropRects[i].localScale = new Vector3(
                    dropHomeScales_[i].x * shrink,
                    dropHomeScales_[i].y * shrink,
                    dropHomeScales_[i].z);

                if (dropImages != null && i < dropImages.Length && dropImages[i] != null)
                {
                    dropImages[i].color = new Color(
                        cloudR_, cloudG_, cloudB_, puffAlpha * (1.0f - life * 0.8f) * alpha);
                }
            }

            // ---- 焼き文字：予兆から引き継いで「一気に通常サイズ」で出る ----
            // 既定では拡大も傾き変化もさせない（glyphPunchScale=1 / glyphRiseDistance=0）。
            // 大きさの芝居は予兆側が全部やっているので、ここで重ねると二段モーションになる。
            if (glyphRect != null && glyphImage != null)
            {
                float punch = Mathf.Lerp(glyphPunchScale, 1.0f, eased) * glyphScale;
                glyphRect.localScale = glyphHomeScale_ * punch;
                glyphRect.anchoredPosition =
                    glyphHome_ + new Vector2(0.0f, -glyphRiseDistance * (1.0f - eased));
                glyphRect.localRotation = Quaternion.Euler(0.0f, 0.0f, textTiltDegrees);
                glyphImage.color = new Color(1.0f, 1.0f, 1.0f, elapsed < ghost ? 0.0f : 1.0f - outT);
            }
        }

        private void ApplyText(float enterIn, float enterEased, float fadeOut, float alpha)
        {
            // 帯モードは左下に置くので左から、墨モードは中央なので右から入る。
            float dir = bandMode_ ? -1.0f : 1.0f;
            float scaleMul = bandMode_ ? (chunLevel_ > 0 ? chunTextScaleNow_ : bandTextScale) : inkTextScale;

            float textScale = Mathf.Lerp(textPunchScale, 1.0f, enterEased) * scaleMul;
            float slideX = dir * textSlideDistance * (1.0f - enterEased);
            float driftY = textDriftDistance * fadeOut;
            float tilt = Mathf.Lerp(textTiltDegrees * 2.2f, textTiltDegrees, enterEased);

            if (callShadowRect != null)
            {
                callShadowRect.anchoredPosition = textHome_ + shadowOffset + new Vector2(slideX, driftY);
                callShadowRect.localScale = shadowHomeScale_ * textScale;
                callShadowRect.localRotation = Quaternion.Euler(0.0f, 0.0f, tilt);
            }
            if (callShadowText != null)
            {
                callShadowText.color = new Color(
                    shadowColor.r, shadowColor.g, shadowColor.b,
                    shadowColor.a * enterIn * alpha);
            }

            if (callTextRect != null)
            {
                callTextRect.anchoredPosition = textHome_ + new Vector2(slideX, driftY);
                callTextRect.localScale = textHomeScale_ * textScale;
                callTextRect.localRotation = Quaternion.Euler(0.0f, 0.0f, tilt);
            }
            if (callText != null)
            {
                callText.color = new Color(
                    activeColor_.r, activeColor_.g, activeColor_.b,
                    enterIn * alpha);
            }
        }

        // 自画像キャラ（CHARA_SELFIE）を選んでいる席は、その人が PlayerData へ焼いた
        // 写真を立ち絵として使う。写真が無い／ストア未割当なら従来の立ち絵へ落ちる。
        private Texture ResolveSelfiePortrait(int charaId, int playerId)
        {
            if (selfieStore == null) { return null; }
            if (charaId != PlayerStatsStore.CHARA_SELFIE) { return null; }
            if (playerId <= 0) { return null; }

            var player = VRCPlayerApi.GetPlayerById(playerId);
            if (player == null || !player.IsValid()) { return null; }

            return selfieStore.GetPortraitOf(player);
        }

        private Texture ResolvePortrait(int charaId)
        {
            var selfie = ResolveSelfiePortrait(charaId, portraitPlayerId_);
            if (selfie != null) { return selfie; }

            if (charaPortraits != null
                && charaId >= 0
                && charaId < charaPortraits.Length
                && charaPortraits[charaId] != null)
            {
                return charaPortraits[charaId];
            }
            return defaultPortrait;
        }

        private string ResolveGlyph(int callType)
        {
            if (callType == CALL_CHII) { return "チー"; }
            if (callType == CALL_PON) { return "ポン"; }
            if (callType == CALL_KAN) { return "カン"; }
            if (callType == CALL_RIICHI) { return "リーチ"; }
            if (callType == CALL_RON) { return "ロン"; }
            if (callType == CALL_TSUMO) { return "ツモ"; }
            if (callType == CALL_CHUN)
            {
                if (chunLevel_ >= 4) { return "ファイナル\nチュンビーム！"; }
                if (chunLevel_ == 3) { return "トリプル\nチュンビーム！"; }
                if (chunLevel_ == 2) { return "ダブル\nチュンビーム！"; }
                return "チュンビーム！";
            }
            return "北抜き";
        }

        private Color ResolveColor(int callType)
        {
            if (callType == CALL_CHII) { return chiiColor; }
            if (callType == CALL_PON) { return ponColor; }
            if (callType == CALL_KAN) { return kanColor; }
            if (callType == CALL_RIICHI) { return riichiColor; }
            if (callType == CALL_RON) { return ronColor; }
            if (callType == CALL_TSUMO) { return tsumoColor; }
            if (callType == CALL_CHUN)
            {
                int i = chunLevel_ - 1;
                if (chunColors != null && i >= 0 && i < chunColors.Length) { return chunColors[i]; }
                return ronColor;
            }
            return kitaColor;
        }
    }
}
