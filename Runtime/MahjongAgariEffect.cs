using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    //  和了演出（ロン／ツモの牌に出るエフェクト）＝ 雀魂スタイル
    // ------------------------------------------------------------
    //  設計思想（開発ナレッジ準拠）:
    //   ・純ローカル表示。同期変数を一切持たない（NoVariableSync）。
    //     「誰が・どの牌で和了ったか」は Mahjong.cs が既に全クライアントへ
    //     同期しているため、各クライアントが UpdateAgariScreen のフックから
    //     同じ入力で Play() を叩けば全員同じ演出になる（追加同期ゼロ）。
    //   ・どの演出を出すかは「和了者が PlayerData に保存したコスメID」で決まる。
    //     PlayerData は全クライアントから読めるので、これも同期変数不要
    //     （ボイスパック kmj_voice_pack と同じ遅延解決パターン）。
    //   ・視覚は Vefects Zap VFX（ParticleSystem）を variantExtraRoots に載せて再生する。Canvas リグは Zap 未割当時の逃げ道
    //     RectTransform スケール／anchoredPosition／color.a だけで動かす
    //     （Quest基準。あがり画面・役満バナーと同方針）。
    //   ・スクリプト自身は「常時アクティブな親」に置くこと。表示のON/OFFは
    //     fxRoot（子）の SetActive で行う（非アクティブなUdonBehaviourは
    //     外部から叩けないため）。
    //   ・一括非表示は子→親の順（HideNow は子＝extraRoot を先に落とす）。
    // ------------------------------------------------------------
    //  演出の構成（コスメで色／テクスチャだけ差し替える共有リグ）:
    //    billboardRoot（プレイヤーの方を向く World Space Canvas）
    //      + Glow      … 牌の背後に残る柔らかい光
    //      + Pillar    … 牌から立ち上がる光の柱（雀魂の「柱」）
    //      + Flash     … 着弾の白閃光
    //      + Spark0..N … 放射状に散る粒
    //    groundRoot（卓面に寝かせた World Space Canvas）
    //      + Ring      … 牌を中心に広がる衝撃波リング
    //  さらに variantExtraRoots[id] に凝ったアートを入れれば、そのコスメの
    //  ときだけ追加で SetActive される（将来の追加コスメ用の逃げ道）。
    // ------------------------------------------------------------
    //  タイミング（雀魂式の段取り。実際に進めるのは Mahjong.cs の和了段取り）:
    //    ロン/ツモ宣言ボイス
    //      →(declareSeconds ／ waitDeclareVoice=ON ならボイス実尺+declarePadSeconds)
    //    手牌公開
    //      →(revealSeconds)
    //    牌のエフェクト（Play）
    //      →(leadSeconds)
    //    あがり画面（MahjongAgariScreen）
    //    ダブロン／トリロンの2人目以降は手牌が既に開いているので nextWinnerSeconds。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class MahjongAgariEffect : UdonSharpBehaviour
    {
        [Header("表示言語（Wire Language で自動割り当て）")]
        [Tooltip("コスメ名・説明の言語。未割当なら日本語のまま。")]
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
        
        [Header("必須参照")]
        [Tooltip("演出全体のルート。非アクティブにして置いておく（演出中だけONになる）。")]
        public GameObject fxRoot;

        [Tooltip("縦向きレイヤーのルート（World Space Canvas）。演出中は毎フレーム自分の方へ水平ビルボードする。")]
        public Transform billboardRoot;

        [Tooltip("卓面レイヤーのルート（World Space Canvas を寝かせたもの）。リングを卓に貼り付ける。")]
        public Transform groundRoot;

        [Header("共有リグ（無いパーツは未割当でOK）")]
        [Tooltip("着弾の白閃光。一番手前・一瞬だけ。")]
        public RawImage flashImage;
        public RectTransform flashRect;

        [Tooltip("牌から立ち上がる光の柱。ピボットを下端（0,0）にしておくと下から伸びる。")]
        public RawImage pillarImage;
        public RectTransform pillarRect;

        [Tooltip("卓面を走る衝撃波リング（groundRoot 配下）。")]
        public RawImage ringImage;
        public RectTransform ringRect;

        [Tooltip("牌の背後に残る柔らかい光。演出中ずっと出ている。")]
        public RawImage glowImage;
        public RectTransform glowRect;

        [Tooltip("放射状に散る粒。0枚でも成立する。")]
        public RawImage[] sparkImages;
        public RectTransform[] sparkRects;

        [Header("コスメ（和了演出の種類）")]
        [Tooltip("コスメ名。index が PlayerData(kmj_agari_fx) に保存されるID。0番は必ず全員が使える既定演出にすること。")]
        // index は BuildAgariEffect.ZapPrefabPaths の並びと 1:1（0=琥珀 1=黒 2=蒼 3=紅 4=翠 5=紫 6=白）。
        // 名前・色を変えるときは必ず向こうの並びと突き合わせること（ズレるとカードの色見本と実物が食い違う）。
        public string[] variantNames = new string[]
        {
            "金雷", "黒雷", "蒼雷", "紅雷", "翠雷", "紫雷", "白雷"
        };

        [Tooltip("コスメの説明文（メインメニューの装飾品画面に出す。任意）。")]
        public string[] variantDescs = new string[]
        {
            "既定の和了演出。黄金の雷が牌を撃ち抜く。",
            "漆黒の雷が牌を呑み込む。",
            "蒼い雷が牌を撃ち抜く。",
            "紅い雷が牌を焼き貫く。",
            "翠の雷が牌を弾けさせる。",
            "紫電が牌を撃ち抜く。",
            "白銀の雷が牌を貫く。"
        };

        [Tooltip("コスメ名の英語。variantNames と同じ長さ・同じ順にする。表の正は BuildAgariEffect。")]
        public string[] variantNamesEn = new string[]
        {
            "Gold Bolt", "Black Bolt", "Azure Bolt", "Crimson Bolt",
            "Jade Bolt", "Violet Bolt", "Silver Bolt"
        };

        [Tooltip("コスメ説明の英語。variantDescs と同じ長さ・同じ順にする。")]
        public string[] variantDescsEn = new string[]
        {
            "The default win effect. A golden bolt tears through the tile.",
            "A pitch-black bolt swallows the tile whole.",
            "An azure bolt punches through the tile.",
            "A crimson bolt sears its way through the tile.",
            "A jade bolt bursts the tile apart.",
            "A violet bolt strikes clean through the tile.",
            "A silver bolt runs the tile through."
        };

        [Tooltip("コスメ名の韓国語。空の欄は英語を出す。")]
        public string[] variantNamesKo = new string[]
        {
            "금뢰", "흑뢰", "창뢰", "홍뢰", "취뢰", "자뢰", "백뢰"
        };

        [Tooltip("コスメ説明の韓国語。空の欄は英語を出す。")]
        public string[] variantDescsKo = new string[]
        {
            "기본 화료 연출. 황금 번개가 패를 꿰뚫는다.",
            "칠흑의 번개가 패를 집어삼킨다.",
            "푸른 번개가 패를 꿰뚫는다.",
            "붉은 번개가 패를 태워 관통한다.",
            "비취색 번개가 패를 터뜨린다.",
            "보랏빛 번개가 패를 꿰뚫는다.",
            "은빛 번개가 패를 관통한다."
        };

        [Tooltip("コスメ名の簡体字。空の欄は英語を出す。")]
        public string[] variantNamesZhs = new string[]
        {
            "金雷", "黑雷", "苍雷", "红雷", "翠雷", "紫雷", "白雷"
        };

        [Tooltip("コスメ説明の簡体字。空の欄は英語を出す。")]
        public string[] variantDescsZhs = new string[]
        {
            "默认的和了演出。金色雷电击穿牌面。",
            "漆黑的雷电将牌吞没。",
            "苍蓝的雷电击穿牌面。",
            "赤红的雷电灼穿牌面。",
            "翠绿的雷电让牌迸裂。",
            "紫电击穿牌面。",
            "白银的雷电贯穿牌面。"
        };

        [Tooltip("コスメ名の繁体字。空の欄は英語を出す。")]
        public string[] variantNamesZht = new string[]
        {
            "金雷", "黑雷", "蒼雷", "紅雷", "翠雷", "紫雷", "白雷"
        };

        [Tooltip("コスメ説明の繁体字。空の欄は英語を出す。")]
        public string[] variantDescsZht = new string[]
        {
            "預設的和了演出。金色雷電擊穿牌面。",
            "漆黑的雷電將牌吞沒。",
            "蒼藍的雷電擊穿牌面。",
            "赤紅的雷電灼穿牌面。",
            "翠綠的雷電讓牌迸裂。",
            "紫電擊穿牌面。",
            "白銀的雷電貫穿牌面。"
        };

        // 色は各 Zap プレハブの LUT テクスチャ（T_VFX_Zap_LUT_0x）を実測した色相を、
        // カードの色見本で潰れない明度まで持ち上げた値。黒だけは真っ黒だと見えないので灰寄り。
        [Tooltip("主色（装飾品カードの色見本／Zap未割当時の柱・閃光・粒）。variantNames と同じ長さ・同じ順にする。")]
        public Color[] variantMainColors = new Color[]
        {
            new Color(1.00f, 0.62f, 0.12f, 1.0f),
            new Color(0.22f, 0.20f, 0.26f, 1.0f),
            new Color(0.18f, 0.52f, 1.00f, 1.0f),
            new Color(1.00f, 0.20f, 0.24f, 1.0f),
            new Color(0.20f, 1.00f, 0.38f, 1.0f),
            new Color(0.42f, 0.24f, 1.00f, 1.0f),
            new Color(0.92f, 0.96f, 1.00f, 1.0f)
        };

        [Tooltip("副色（リング・背後の光）。variantNames と同じ長さ・同じ順にする。")]
        public Color[] variantSubColors = new Color[]
        {
            new Color(1.00f, 0.88f, 0.55f, 1.0f),
            new Color(0.62f, 0.58f, 0.70f, 1.0f),
            new Color(0.72f, 0.90f, 1.00f, 1.0f),
            new Color(1.00f, 0.72f, 0.72f, 1.0f),
            new Color(0.78f, 1.00f, 0.85f, 1.0f),
            new Color(0.80f, 0.74f, 1.00f, 1.0f),
            new Color(1.00f, 1.00f, 1.00f, 1.0f)
        };

        [Tooltip("コスメ別の光柱テクスチャ（任意）。null のスロットは既定テクスチャのまま。")]
        public Texture[] variantPillarTextures;

        [Tooltip("コスメ専用の追加アート（任意）。そのコスメの演出中だけ SetActive される。null でよい。")]
        public GameObject[] variantExtraRoots;

        [Tooltip("コスメ別のSE（任意）。全員のクライアントでローカル再生されるので、結果的に全員に聞こえる。")]
        public AudioClip[] variantSounds;

        [Tooltip("コスメ別SEが未設定のときに使う共通SE（和了演出の標準音）。")]
        public AudioClip defaultSound;

        [Tooltip("手牌公開の瞬間に鳴らすSE（宣言ボイスのあと、牌のエフェクトの前）。未割当なら無音。")]
        public AudioClip handOpenSound;


        [Tooltip("SE再生用 AudioSource（常時アクティブな親側に置く）。未割当ならSEなし。")]
        public AudioSource seSource;

        [Tooltip("牌エフェクトのSE（defaultSound / variantSounds）の音量。")]
        [Range(0.0f, 1.0f)]
        public float seVolume = 0.0885f;

        // 手牌公開の「ドン」と牌エフェクトの「爆発」は素の音圧が約8dB違うため、
        // つまみを1つにすると必ずどちらかが埋もれる。別々に持たせて聴感を揃える。
        [Tooltip("手牌公開SE（handOpenSound）の音量。")]
        [Range(0.0f, 1.0f)]
        public float handOpenVolume = 0.0885f;


        [Header("タイミング（雀魂式の段取り）")]
        //  ロン/ツモ宣言ボイス →(declare)→ 手牌公開 →(reveal)→ 牌のエフェクト →(lead)→ あがり画面
        //  ここの3値が段取りの全て。0 に寄せるほど従来（全部同時）に近づく。

        [Tooltip("宣言ボイスの実尺に合わせて手牌公開を待つ。ボイスパックごとにクリップ長が違うため既定ON。")]
        public bool waitDeclareVoice = true;

        [Tooltip("宣言ボイス → 手牌公開 の秒数（waitDeclareVoice=OFF、またはボイス長が取れないときの固定値）。")]
        public float declareSeconds = 0.70f;

        [Tooltip("宣言ボイスが鳴り終わってから手牌を公開するまでの余韻（秒）。waitDeclareVoice=ON のときだけ効く。")]
        public float declarePadSeconds = 0.15f;

        [Tooltip("宣言ボイス待ちの下限（秒）。ボイス無し・極短クリップでも最低これだけは間を置く。")]
        public float declareMinSeconds = 0.35f;

        [Tooltip("宣言ボイス待ちの上限（秒）。長尺ボイスで和了が間延びするのを防ぐ保険。")]
        public float declareMaxSeconds = 1.60f;

        [Tooltip("手牌公開 → 牌のエフェクト の秒数。手が開いたのを見せてから演出に入る。")]
        public float revealSeconds = 0.45f;

        [Tooltip("ダブロン／トリロンで表示が次の和了者へ切り替わってから、その人の演出が出るまでの秒数（手牌は公開済みなので短くてよい）。")]
        public float nextWinnerSeconds = 0.35f;

        [Tooltip("演出開始から あがり画面 が出るまでの秒数。「牌の演出 → 画面」の間はこれで決まる。")]
        public float leadSeconds = 1.5f;

        [Tooltip("演出全体の長さ（秒）。あがり画面と重なって出続ける想定。")]
        public float duration = 2.0f;

        [Tooltip("末尾のフェードアウト時間（秒・duration の内数）。")]
        public float fadeOutSeconds = 0.25f;

        [Header("大きさ（メートル基準の見た目調整）")]
        [Tooltip("牌の位置から演出中心を持ち上げる高さ（m）。牌の厚みぶん浮かせる。")]
        public float centerHeightOffset = 0.02f;

        [Tooltip("リングを卓面から浮かせる高さ（m）。Zファイト回避。")]
        public float ringHeightOffset = 0.004f;

        [Tooltip("光柱の最終スケール倍率。")]
        public float pillarScale = 1.0f;

        [Tooltip("リングの最終スケール倍率。")]
        public float ringScale = 1.0f;

        [Tooltip("粒が飛ぶ距離（Canvas ローカル px）。")]
        public float sparkDistance = 220.0f;

        // ---- 再生中のローカル状態（同期しない）----
        private float startTime_ = -1.0f;   // <0 = 非表示（float の番兵。負intの(uint)化問題とは無関係）
        private Color mainColor_ = Color.white;
        private Color subColor_ = Color.white;
        private float[] sparkAngles_;       // 粒の飛ぶ向き（度）。Start で1回だけ配る
        private Texture defaultPillarTex_;  // コスメ別テクスチャから戻すための既定値
        private bool playingZap_;           // true = Zap パーティクル中（Canvas リグは出さない）


        // 演出の時間配分（秒）。duration を変えても各パーツの立ち上がりは保つ。
        private const float FLASH_SECONDS = 0.30f;
        private const float PILLAR_RISE_SECONDS = 0.14f;
        private const float RING_SECONDS = 0.85f;
        private const float SPARK_SECONDS = 0.75f;

void Start()
        {
            if (pillarImage != null)
            {
                defaultPillarTex_ = pillarImage.texture;
            }

            int n = sparkRects != null ? sparkRects.Length : 0;
            sparkAngles_ = new float[n];
            for (int i = 0; i < n; ++i)
            {
                int div = n > 1 ? n - 1 : 1;
                sparkAngles_[i] = -60.0f + 300.0f * ((float)i / (float)div);
            }

            HideExtraRoots();
            if (fxRoot != null && startTime_ < 0.0f)
            {
                fxRoot.SetActive(false);
            }
        }

        // ============================================================
        //  外部API
        // ============================================================

        // コスメ数（メインメニューの装飾品画面がカード枚数を知るために使う）。
        public int GetVariantCount()
        {
            return variantNames != null ? variantNames.Length : 0;
        }

        public string GetVariantName(int id)
        {
            return PickVariant(variantNames, variantNamesEn, variantNamesKo, variantNamesZhs, variantNamesZht, id);
        }

        public string GetVariantDesc(int id)
        {
            return PickVariant(variantDescs, variantDescsEn, variantDescsKo, variantDescsZhs, variantDescsZht, id);
        }

        // その言語の欄があればそれ、無ければ英語（日本語以外）、最後に日本語。
        private string PickVariant(string[] ja, string[] en, string[] ko, string[] zhs, string[] zht, int id)
        {
            int lang = Lang();
            string[] own = lang == KemonoLanguage.LANG_KO ? ko
                : lang == KemonoLanguage.LANG_ZHS ? zhs
                : lang == KemonoLanguage.LANG_ZHT ? zht : null;
            if (own != null && id >= 0 && id < own.Length && own[id] != null && own[id].Length > 0)
            {
                return own[id];
            }
            if (lang != KemonoLanguage.LANG_JA && en != null && id >= 0 && id < en.Length
                && en[id] != null && en[id].Length > 0)
            {
                return en[id];
            }
            if (ja == null || id < 0 || id >= ja.Length) { return ""; }
            return ja[id];
        }

        public Color GetVariantMainColor(int id)
        {
            if (variantMainColors == null || id < 0 || id >= variantMainColors.Length) { return Color.white; }
            return variantMainColors[id];
        }

        public Color GetVariantSubColor(int id)
        {
            if (variantSubColors == null || id < 0 || id >= variantSubColors.Length) { return Color.white; }
            return variantSubColors[id];
        }

        // あがり画面をどれだけ遅らせるか（Mahjong.cs が読んで画面へ渡す）。
        public float GetLeadSeconds()
        {
            return leadSeconds < 0.0f ? 0.0f : leadSeconds;
        }

        // ---- 段取りの秒数（Mahjong.cs が読む。負値はすべて0へ丸める）----
        public bool GetWaitDeclareVoice() { return waitDeclareVoice; }
        public float GetDeclareSeconds() { return declareSeconds < 0.0f ? 0.0f : declareSeconds; }
        public float GetDeclarePadSeconds() { return declarePadSeconds < 0.0f ? 0.0f : declarePadSeconds; }
        public float GetDeclareMinSeconds() { return declareMinSeconds < 0.0f ? 0.0f : declareMinSeconds; }

        // 上限は下限を下回らせない（Inspectorで逆転させても段取りが壊れないように）。
        public float GetDeclareMaxSeconds()
        {
            float lo = GetDeclareMinSeconds();
            float hi = declareMaxSeconds < 0.0f ? 0.0f : declareMaxSeconds;
            return hi < lo ? lo : hi;
        }

        public float GetRevealSeconds() { return revealSeconds < 0.0f ? 0.0f : revealSeconds; }
        public float GetNextWinnerSeconds() { return nextWinnerSeconds < 0.0f ? 0.0f : nextWinnerSeconds; }

        // 和了演出の再生。tileWorldPos = 和了牌のワールド位置。
        // variantId は和了者の PlayerData(kmj_agari_fx)。範囲外は 0 に丸める。
public void Play(int variantId, Vector3 tileWorldPos)
        {
            if (fxRoot == null) { return; }

            int count = GetVariantCount();
            if (count <= 0) { return; }
            if (variantId < 0 || variantId >= count) { variantId = 0; }

            HideExtraRoots();
            StopAllZaps();

            mainColor_ = GetVariantMainColor(variantId);
            subColor_ = GetVariantSubColor(variantId);

            var center = tileWorldPos + new Vector3(0.0f, centerHeightOffset, 0.0f);
            fxRoot.transform.position = center;

            if (billboardRoot != null)
            {
                billboardRoot.position = center;
            }

            if (groundRoot != null)
            {
                groundRoot.position = new Vector3(tileWorldPos.x, tileWorldPos.y + ringHeightOffset, tileWorldPos.z);
                groundRoot.rotation = Quaternion.Euler(-90.0f, 0.0f, 0.0f);
            }

            if (pillarImage != null)
            {
                Texture tex = defaultPillarTex_;
                if (variantPillarTextures != null && variantId < variantPillarTextures.Length
                    && variantPillarTextures[variantId] != null)
                {
                    tex = variantPillarTextures[variantId];
                }
                pillarImage.texture = tex;
            }

            GameObject extra = null;
            if (variantExtraRoots != null && variantId < variantExtraRoots.Length)
            {
                extra = variantExtraRoots[variantId];
            }

            // 選んだコスメに Zap が紐づいていないとき（配線漏れ・配列が短い等）は、
            // 廃止した Canvas リグ（光柱）を出すと「前の演出が復活した」としか見えないので、
            // 0番の Zap へ逃がす。Canvas リグは Zap が1つも無いリグの最後の逃げ道にだけ残す。
            if (extra == null && variantExtraRoots != null && variantExtraRoots.Length > 0)
            {
                extra = variantExtraRoots[0];
            }

            playingZap_ = extra != null;
            if (extra != null)
            {
                extra.SetActive(true);
            }
            SetSharedRigVisible(!playingZap_);

            startTime_ = Time.time;
            if (!playingZap_)
            {
                ApplyFrame(0.0f);
                FaceLocalPlayer();
            }

            fxRoot.SetActive(true);

            if (playingZap_)
            {
                RestartZap(extra);
            }

            if (seSource != null)
            {
                AudioClip clip = defaultSound;
                if (variantSounds != null && variantId < variantSounds.Length
                    && variantSounds[variantId] != null)
                {
                    clip = variantSounds[variantId];
                }

                if (clip != null) { seSource.PlayOneShot(clip, seVolume); }
            }
        }

        // 自分の位置でそのまま再生する（メインメニューの試写用）。
        public void PlayHere(int variantId)
        {
            Play(variantId, transform.position);
        }

        // 手牌公開の瞬間に鳴らすSE（Mahjong.cs の和了段取りから叩く）。
        // 演出本体（Play）とは独立。fxRoot が消えていても鳴る。
        public void PlayHandOpenSound()
        {
            if (seSource == null || handOpenSound == null) { return; }

            seSource.PlayOneShot(handOpenSound, handOpenVolume);
        }


        // 局のリセット等で即座に消したいとき用。
public void HideNow()
        {
            startTime_ = -1.0f;
            playingZap_ = false;

            StopAllZaps();
            HideExtraRoots();

            if (fxRoot != null)
            {
                fxRoot.SetActive(false);
            }
        }

        private void HideExtraRoots()
        {
            if (variantExtraRoots == null) { return; }
            for (int i = 0; i < variantExtraRoots.Length; ++i)
            {
                if (variantExtraRoots[i] != null)
                {
                    variantExtraRoots[i].SetActive(false);
                }
            }
        }

private void SetSharedRigVisible(bool visible)
        {
            if (billboardRoot != null)
            {
                billboardRoot.gameObject.SetActive(visible);
            }
            if (groundRoot != null)
            {
                groundRoot.gameObject.SetActive(visible);
            }
        }

        private void RestartZap(GameObject go)
        {
            if (go == null) { return; }
            var ps = go.GetComponent<ParticleSystem>();
            if (ps == null) { return; }
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ps.Play(true);
        }

        private void StopAllZaps()
        {
            if (variantExtraRoots == null) { return; }
            for (int i = 0; i < variantExtraRoots.Length; ++i)
            {
                if (variantExtraRoots[i] == null) { continue; }
                var ps = variantExtraRoots[i].GetComponent<ParticleSystem>();
                if (ps != null)
                {
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                }
            }
        }


        // ============================================================
        //  アニメーション（RectTransform と color.a のみ）
        // ============================================================

void Update()
        {
            if (startTime_ < 0.0f)
            {
                return;
            }

            float t = Time.time - startTime_;

            if (t >= duration)
            {
                HideNow();
                return;
            }

            if (playingZap_)
            {
                return;
            }

            ApplyFrame(t);
            FaceLocalPlayer();
        }

        private void ApplyFrame(float t)
        {
            // 末尾フェード（全パーツ共通の掛け算）
            float tail = 1.0f;
            float fadeStart = duration - fadeOutSeconds;
            if (fadeOutSeconds > 0.001f && t > fadeStart)
            {
                tail = Mathf.Clamp01(1.0f - (t - fadeStart) / fadeOutSeconds);
            }

            // ---- 閃光：一瞬で膨らんで消える ----
            if (flashImage != null)
            {
                float f = Mathf.Clamp01(t / FLASH_SECONDS);
                float ease = 1.0f - (1.0f - f) * (1.0f - f);        // easeOutQuad
                if (flashRect != null)
                {
                    float s = Mathf.Lerp(0.35f, 2.60f, ease);
                    flashRect.localScale = new Vector3(s, s, 1.0f);
                }
                SetAlpha(flashImage, Color.white, (1.0f - f) * tail);
            }

            // ---- 光柱：素早く立ち上がり、わずかに揺らぎながら残る ----
            if (pillarImage != null)
            {
                float rise = Mathf.Clamp01(t / PILLAR_RISE_SECONDS);
                float ease = 1.0f - (1.0f - rise) * (1.0f - rise) * (1.0f - rise); // easeOutCubic
                if (pillarRect != null)
                {
                    float sway = 1.0f + 0.06f * Mathf.Sin(t * 9.0f);
                    float sy = Mathf.Lerp(0.05f, pillarScale, ease);
                    float sx = Mathf.Lerp(1.8f, pillarScale * sway, ease); // 最初は太く、細く絞る
                    pillarRect.localScale = new Vector3(sx, sy, 1.0f);
                }
                SetAlpha(pillarImage, mainColor_, Mathf.Clamp01(t / 0.06f) * tail);
            }

            // ---- リング：卓面を走って薄れる ----
            if (ringImage != null)
            {
                float r = Mathf.Clamp01(t / RING_SECONDS);
                float ease = 1.0f - (1.0f - r) * (1.0f - r) * (1.0f - r);
                if (ringRect != null)
                {
                    float s = Mathf.Lerp(0.15f, 2.40f * ringScale, ease);
                    ringRect.localScale = new Vector3(s, s, 1.0f);
                }
                SetAlpha(ringImage, subColor_, (1.0f - r) * 0.9f * tail);
            }

            // ---- 背後の光：演出中ずっと出ている ----
            if (glowImage != null)
            {
                if (glowRect != null)
                {
                    float s = Mathf.Lerp(0.80f, 1.15f, Mathf.Clamp01(t / 0.35f));
                    glowRect.localScale = new Vector3(s, s, 1.0f);
                }
                SetAlpha(glowImage, subColor_, Mathf.Clamp01(t / 0.10f) * 0.85f * tail);
            }

            // ---- 粒：放射状に飛んで薄れる ----
            int n = sparkRects != null ? sparkRects.Length : 0;
            for (int i = 0; i < n; ++i)
            {
                var rect = sparkRects[i];
                if (rect == null) { continue; }

                float p = Mathf.Clamp01(t / SPARK_SECONDS);
                float ease = 1.0f - (1.0f - p) * (1.0f - p) * (1.0f - p);
                float deg = (sparkAngles_ != null && i < sparkAngles_.Length) ? sparkAngles_[i] : 0.0f;
                float rad = deg * Mathf.Deg2Rad;
                float dist = sparkDistance * ease;

                rect.anchoredPosition = new Vector2(Mathf.Cos(rad) * dist, Mathf.Sin(rad) * dist);
                float s = Mathf.Lerp(1.0f, 0.25f, ease);
                rect.localScale = new Vector3(s, s, 1.0f);

                if (sparkImages != null && i < sparkImages.Length && sparkImages[i] != null)
                {
                    SetAlpha(sparkImages[i], mainColor_, (1.0f - p) * tail);
                }
            }
        }

        // 色は「コスメの色 × このフレームのアルファ」。Graphic.color はインスタンス
        // プロパティなのでマテリアル複製は発生しない（Quest配慮）。
        private void SetAlpha(RawImage image, Color baseColor, float alpha)
        {
            // 既存UI（あがり画面）と同じく、構造体のフィールド書き換えではなく
            // new Color で組み直す。
            image.color = new Color(baseColor.r, baseColor.g, baseColor.b, Mathf.Clamp01(alpha));
        }

        // 縦レイヤーだけ自分の方へ水平ビルボード（卓面のリングは回さない）。
        private void FaceLocalPlayer()
        {
            if (billboardRoot == null) { return; }

            var lp = Networking.LocalPlayer;
            if (lp == null) { return; }

            var away = billboardRoot.position - lp.GetPosition();
            away.y = 0.0f;
            if (away.sqrMagnitude > 0.0001f)
            {
                billboardRoot.rotation = Quaternion.LookRotation(away);
            }
        }
    }
}
