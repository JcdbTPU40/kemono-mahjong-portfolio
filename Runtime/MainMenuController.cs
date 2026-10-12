using TMPro;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    // メインメニュー制御（雀魂風・完全ローカル）
    //
    // ■ 役割
    //   MainMenu（World Space Canvas）配下の3画面
    //   （ScreenHome / ScreenProfile / ScreenChara）を切り替え、
    //   各クライアントが自分の PlayerData（PlayerStatsStore 経由）を
    //   読んで描画する。同期変数・RequestSerialization は一切使わない。
    //
    // ■ 前提
    //   - UI階層は Assets/Editor/BuildMainMenu.cs（Tools > Kemono Mahjong > Build Main Menu）で生成。
    //   - フィールド割り当てとボタン配線は Assets/Editor/WireMainMenu.cs
    //     （Tools > Kemono Mahjong > Wire Main Menu）で自動化。
    //   - 各ボタンは uGUI Button の onClick から UdonBehaviour.SendCustomEvent で
    //     ShowHome / ShowProfile / ShowChara / SelectChara0..2 / ToggleAbility を呼ぶ。
    //   - キャラ＝Vol(0)/Shine(1)/Tsumuntema(2)/自画像(3)の4体（旧4体キットは2026-07-15廃止）。
    //     自画像は鋭爪以上で解禁。選ぶと ScreenSelfie で3秒セルフタイマー撮影→プレビュー→決定。
    //     撮影は SelfiePortraitCamera、他プレイヤーへの配布は SelfiePortraitStore
    //     （どちらも MainMenu脇の常時アクティブな1個）が担当する。
    //     決定した写真は PlayerData へ焼かれるので、他プレイヤーにも見え、次の入場でも残る。
    //     「能力なし」はカードではなく能力スイッチ（kmj_ability_on）で表現。
    //   - キャラ選択時にボイスパック（kmj_voice_pack）も自動設定（charaVoicePacks → voiceSelector.SetPack）。
    //     PlayerData への書込／復元前保留は MahjongVoiceSelector が唯一担当する。
    //   - MainMenu脇の3Dプレビュー（charaPreviewModels）は自分の選択のみローカル表示。
    //
    // ■ 注意
    //   - Update() でのポーリングは行わない（描画は画面遷移時とコールバック時のみ）。
    //   - 一括非表示は子→親の順（各画面は子を常時アクティブのまま画面ルートのみ
    //     SetActive するため、この規約に抵触しない）。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class MainMenuController : UdonSharpBehaviour
    {
        [Header("参照（Wire Main Menu で自動割り当て）")]
        public PlayerStatsStore statsStore;
        [Tooltip("段位戦マッチング（実装③）。Wire Nameplates → Wire Matchmaking 実行後に自動割り当て。")]
        public MatchmakingManager matchmaking;
        [Tooltip("ボイスパック選択（kmj_voice_pack の唯一の書き手）。キャラ連動はここ経由。未割当なら連動しない。")]
        public MahjongVoiceSelector voiceSelector;

        [Header("画面ルート")]
        public GameObject screenHome;
        public GameObject screenProfile;
        public GameObject screenChara;
        public GameObject screenWaiting;
        public GameObject screenFreeSetup;
        public GameObject screenFreeRoom;
        public GameObject screenFreeConfirm;
        [Tooltip("ワールド設定（表示言語など）画面のルート。未割当ならワールド設定なし。")]
        public GameObject screenWorld;
        [Tooltip("装飾品（和了演出コスメ）画面のルート。未割当なら装飾品機能なし。")]
        public GameObject screenCosmetic;
        [Tooltip("自画像の撮影画面のルート。未割当なら自画像キャラは選べない。")]
        public GameObject screenSelfie;
        [Tooltip("対局中の案内画面のルート。未割当なら従来どおりホームのまま。")]
        public GameObject screenInGame;
        [Tooltip("途中退出の確認画面のルート。未割当なら途中退出できない。")]
        public GameObject screenAbandonConfirm;
        [Tooltip("卓の強制リセット画面のルート（スタッフ／インスタンスマスター専用）。")]
        public GameObject screenTableReset;
        [Tooltip("フリー対局の卓（部屋）一覧画面のルート。未割当ならフリー対局は卓おまかせになる。")]
        public GameObject screenFreeList;

        [Header("対局中の案内（ScreenInGame / ScreenAbandonConfirm）")]
        public TextMeshProUGUI inGameText;
        public TextMeshProUGUI abandonConfirmText;
        [Tooltip("途中退出ボタン。対局中だけ押せる。")]
        public GameObject btnAbandon;

        [Header("待機画面（ScreenWaiting）")]
        public TextMeshProUGUI waitingTitle;
        public TextMeshProUGUI waitingInfo;
        public GameObject btnCancelWait;
        [Tooltip("待機中・成立直後に顔ぶれを出す枠。未割当なら名前は出ない。")]
        public GameObject waitingMemberList;
        [Tooltip("顔ぶれの各行（四麻の4行分）。WaitMember0..3 を順に割り当てる。")]
        public TextMeshProUGUI[] waitingMemberTexts;

        [Header("ホーム（フリー対局ボタン・お知らせ）")]
        public Button btnFree;
        public TextMeshProUGUI btnFreeLabel;
        public TextMeshProUGUI homeInfo;

        [Header("段位戦ボタン（スタッフパネルの列開閉で表示/非表示）")]
        [Tooltip("四麻 半荘戦。BtnRanked4Han。Wire Main Menu が割り当てる。")]
        public GameObject btnRanked4Han;
        [Tooltip("四麻 東風戦。BtnRanked4Ton。")]
        public GameObject btnRanked4Ton;
        [Tooltip("三麻 半荘戦。BtnRanked3Han。")]
        public GameObject btnRanked3Han;
        [Tooltip("三麻 東風戦。BtnRanked3Ton。")]
        public GameObject btnRanked3Ton;

        [Header("待機状況（ボタン下段の行と枠の発光）")]
        [Tooltip("各ボタンの2行目。順番は 四麻半荘 / 四麻東風 / 三麻半荘 / 三麻東風 / フリー対局。")]
        public TextMeshProUGUI[] queueInfoTexts;
        [Tooltip("各ボタンの発光枠（Glow）。queueInfoTexts と同じ順・同じ数。")]
        public GameObject[] queueGlows;

        [Header("フリー対局 設定画面（ScreenFreeSetup）")]
        public TextMeshProUGUI freeModeLabel;
        public TextMeshProUGUI freeAbilityLabel;
        public TextMeshProUGUI freeLengthLabel;
        public TextMeshProUGUI freeTimerLabel;
        public TextMeshProUGUI freeRedDoraLabel;
        public TextMeshProUGUI freeAotenjouLabel;
        public TextMeshProUGUI freeLocalYakuLabel;
        public TextMeshProUGUI freeOpenReachLabel;
        public TextMeshProUGUI freeTsumoLossLabel;
        public TextMeshProUGUI freeBustLabel;
        public TextMeshProUGUI freeCpuLevelLabel;

        [Header("フリー対局 部屋画面（ScreenFreeRoom）")]
        public TextMeshProUGUI freeRoomTitle;
        public TextMeshProUGUI freeRoomInfo;
        public TextMeshProUGUI[] freeMemberTexts;
        public GameObject btnFreeCpuAdd;
        public GameObject btnFreeCpuRemove;
        public GameObject btnFreeStart;
        public TextMeshProUGUI freeCloseLabel;

        [Header("フリー対局 入室確認（ScreenFreeConfirm）")]
        public TextMeshProUGUI freeConfirmText;

        [Header("フリー対局 卓一覧（ScreenFreeList）")]
        // 部屋＝卓になったので、フリー対局は必ずここで卓を選んでから始める。
        // 卓番号は卓index+1（ワールド全体の前提）。
        [Tooltip("卓カードの見出し（index=卓番号-1）。「卓N」を書く。")]
        public TextMeshProUGUI[] freeListNames;
        [Tooltip("卓カードの状態行（index=卓番号-1）。空き／募集中／使用中を書く。")]
        public TextMeshProUGUI[] freeListInfos;
        [Tooltip("卓カードの背景 Image（index=卓番号-1）。使える卓だけ明るくする。")]
        public Image[] freeListImages;
        [Tooltip("卓一覧の見出し。")]
        public TextMeshProUGUI freeListTitle;
        [Tooltip("卓一覧の説明文／エラー表示。")]
        public TextMeshProUGUI freeListNote;
        [Tooltip("卓一覧の戻るボタンのラベル。")]
        public TextMeshProUGUI freeListBackLabel;

        // 一覧で選んだ卓（-1=未選択）。入室確認はこの卓の部屋について尋ねる。
        private int freeListTable_ = -1;
        // 一覧に出す一時メッセージ（押せない卓を押したときの理由）。
        private string freeListMessage_ = "";

        [Header("ヘッダー")]
        public TextMeshProUGUI headerName;
        public TextMeshProUGUI headerRank;
        public RawImage headerRankIcon;

        [Header("プロフィール")]
        [Header("Profile analytics")]
        public RectTransform[] profileTrendDots;
        public RectTransform[] profileTrendLines;
        public GameObject[] profileRankRows;
        public Image[] profileWinSlices;
        public TextMeshProUGUI[] profileWinLabels;
        public RectTransform[] profilePlaceBars;
        public TextMeshProUGUI[] profilePlaceLabels;
        public RectTransform[] profileRadar;
        public TextMeshProUGUI profileStyleText;
        public TextMeshProUGUI profileSampleText;
        public TextMeshProUGUI profileTrendText;
        public TextMeshProUGUI profileHighlightText;
        public TextMeshProUGUI profileHighlightEmpty;
        [Tooltip("ハイライトの通常/イベント切替の枠。イベントの記録が無い人には出さない。")]
        public GameObject profileHighlightScope;
        [Tooltip("切替「通常」ボタンの Image（選択中の色替え用）。")]
        public Image profileHighlightScopeNormalImage;
        [Tooltip("切替「イベント」ボタンの Image（選択中の色替え用）。")]
        public Image profileHighlightScopeEventImage;
        public RawImage[] profileHighlightTiles;
        public GameObject profileStyleHelp;
        public UnityEngine.UI.ScrollRect profileScroll;
        private int profileTrendWindow_ = 50;
        private float[] profileAtlasX_ = new float[] {5.5f,228.5f,456.5f,683.5f,910.5f,1137.5f,1365f,1592f,1819.5f,2042.5f};
        private float[] profileAtlasY_ = new float[] {73.5f,397.5f,726.5f,1056.5f,1386.5f,1716.5f,2041.5f};

        public TextMeshProUGUI profileName;
        public TextMeshProUGUI profileRank;
        public RawImage profileRankIcon;
        [Tooltip("大段位index 0～5（初獣／鋭爪／剛牙／獣威／幻獣／天獣）のアイコン。")]
        public Texture[] rankIcons;
        [Tooltip("戦績タブの背景Image（0=四麻段位戦 1=三麻段位戦 2=フリー対局）。選択中をハイライトする。")]
        public Image[] profileTabImages;
        [Tooltip("統計ブロックのタイトル（選択中カテゴリ名を表示）")]
        public TextMeshProUGUI statsSelTitle;
        public TextMeshProUGUI statsSelPlay;
        public TextMeshProUGUI statsSelWin;
        public TextMeshProUGUI statsSelAgari;
        public TextMeshProUGUI statsSelHoju;

        [Header("直近10戦グラフ（Dot0〜Dot9）")]
        public RectTransform[] recentDots;
        // 1位のドットの anchoredPosition.y。BuildMainMenu の罫線配置と合わせる。
        public float graphRank1Y = 120.0f;
        // 着順1つ下がるごとの下降量。
        public float graphRankStep = 80.0f;

        [Header("キャラ選択（index: 0=Vol 1=Shine 2=Tsumuntema 3=自画像）")]
        public GameObject[] charaSelectedBadges;
        public RawImage[] charaCardPortraits;
        public RawImage charaDetailPortrait;
        public TextMeshProUGUI charaDetailText;
        [Tooltip("キャラのポートレート（0=Vol / 1=Shine / 2=Tsumuntema）。Wire Main Menuで自動割り当て。自画像(3)は撮影した写真を実行時に差し込むので null のままでよい。")]
        public Texture[] charaPortraits;
        [Tooltip("ロック表示のルート（index=キャラID）。解禁前だけ出す。自画像以外は常に非表示。")]
        public GameObject[] charaLockOverlays;
        [Tooltip("ロック文言（index=キャラID）。解禁条件を書き換える。")]
        public TextMeshProUGUI[] charaLockLabels;

        [Header("自画像キャラ（撮影ブース）")]
        [Tooltip("撮影ブース。MainMenu脇の常時アクティブな1個を割り当てる（画面の中に置くとUdonが止まる）。")]
        public SelfiePortraitCamera selfieCamera;
        [Tooltip("自画像カードの、写真未撮影時に出すプレースホルダ画像。未割当なら空きスロット色で塗る。")]
        public Texture selfiePlaceholder;
        [Tooltip("写真未撮影・プレースホルダ未割当のときにポートレート枠を塗る色。真っ白な板を出さないための逃げ。")]
        public Color emptyPortraitColor = new Color(0.10f, 0.11f, 0.15f, 0.85f);
        [Tooltip("自画像カードの「写真をとる」案内。写真がある間は隠す。")]
        public GameObject selfieCardHint;

        [Header("ホーム画面の選択中キャラ")]
        [Tooltip("ScreenHome左側に表示する、現在選択中キャラのポートレート。")]
        public RawImage homeCharaPortrait;

        [Header("能力スイッチ（BtnAbilityToggle/Label）")]
        public TextMeshProUGUI abilityToggleLabel;

        [Header("キャラ3Dプレビュー（index: 0=Vol 1=Shine。選択中のみローカル表示）")]
        public GameObject[] charaPreviewModels;

        [Header("装飾品：和了演出（ScreenCosmetic）")]
        [Tooltip("コスメのカタログ兼試写用の MahjongAgariEffect。MainMenu 脇に置いた常時アクティブな1個を割り当てる（卓側のものはカリングで落ちるため使えない）。")]
        public MahjongAgariEffect fxCatalog;
        public Renderer[] cosmeticTileBackRenderers;
        public Renderer[] cosmeticTableTopRenderers;
        public Image[] cosmeticCategoryImages;
        public TextMeshProUGUI cosmeticCategoryTitle;
        public TextMeshProUGUI cosmeticNote;
        public GameObject cosmeticPreviewButton;
        public GameObject cosmeticResetButton;
        // 装飾品のカード枚数はカテゴリごとに独立させる。
        // 色は GetTileBackColor / GetTableTopColor の7色固定、立直BGMは「なし」を含めて3。
        // 伸びるのは和了エフェクト（fxCatalog）だけ。ここを共用すると、
        // コスメを1個増やしただけで色タブに8枚目が出て、
        // SetTileBackColor(8) が弾かれて「押しても無反応のカード」になる。
        public const int COSMETIC_COLOR_COUNT = 7;
        public const int COSMETIC_BGM_COUNT = 3;

        // ============================================================
        // 装飾品：立直BGMの試聴（ローカル。他プレイヤーには聞こえない）
        //   卸しの AudioSource はメインメニュー脇の1個。卓側の MahjongRiichiBgm は
        //   近接カリングで寝ているので試聴には使えない（和了エフェクトの fxCatalog と同じ理由）。
        // ============================================================
        [Header("装飾品：立直BGMの試聴（Build Riichi BGM が配線）")]
        [Tooltip("試聴用の AudioSource。MainMenu 脇の常時アクティブな1個を割り当てる。")]
        public AudioSource riichiBgmPreviewSource;
        [Tooltip("トラック番号で引く試聴クリップ。卓側 MahjongRiichiBgm.riichiBgm と同じ並び（[0]=なし）。")]
        public AudioClip[] riichiBgmPreviewClips;
        [Tooltip("riichiBgmPreviewClips と同じ並びの基準音量。卓側の riichiBgmGains と同値＝試聴と本番で音量が揃う。")]
        public float[] riichiBgmPreviewGains;
        [Tooltip("試聴の音量にBGMスライダーを効かせるための参照。Build Volume Panel が割り当てる。")]
        public VolumeSettingsPanel volumePanel;

        // 試聴中のトラックの基準ゲイン。Update が毎フレーム volume を書き戻す
        // ＝音量パネルのスライダーを動かしている最中でも試聴が追従する。
        private float riichiPreviewGain_;
        // 前フレームの再生状態。曲が自然に終わったときにボタンの文言を戻すために見る。
        private bool riichiPreviewPlaying_;

        // 立直BGMで「いま見ているカード」。解禁前は押しても装備できない＝
        // 装備中トラック（statsStore）とは別に持たないと、試聴も詳細欄も動かせない。
        private int riichiBgmFocus_;

        private int cosmeticCategory_ = 0;

        // いまのカテゴリで出すカード枚数。
        private int CosmeticCardCount()
        {
            if (cosmeticCategory_ == 3) { return COSMETIC_BGM_COUNT; }
            if (cosmeticCategory_ == 1 || cosmeticCategory_ == 2) { return COSMETIC_COLOR_COUNT; }
            return fxCatalog != null ? fxCatalog.GetVariantCount() : 0;
        }

        public void ShowCosmeticRiichiBgm()
        {
            cosmeticCategory_ = 3;
            // 詳細欄は「いま装備している曲」から始める（解禁前はなし＝0）。
            riichiBgmFocus_ = statsStore != null ? statsStore.GetRiichiBgm() : 0;
            RenderCosmetic();
        }
        public void ShowCosmeticAgari() { cosmeticCategory_ = 0; RenderCosmetic(); }
        public void ShowCosmeticTileBack() { cosmeticCategory_ = 1; RenderCosmetic(); }
        public void ShowCosmeticTableTop() { cosmeticCategory_ = 2; RenderCosmetic(); }

        public void ResetCosmeticColor()
        {
            if (statsStore == null || cosmeticCategory_ == 0) { return; }
            // 立直BGMのタブにこのボタンは出ない（「なし」カードが同じ働きをする）。
            // 隠れているだけで配線は生きているので、呼ばれても何もしないで帰る。
            if (cosmeticCategory_ == 3) { return; }
            if (cosmeticCategory_ == 1) { statsStore.SetTileBackColor(0); }
            else { statsStore.SetTableTopColor(0); }
            ApplyCosmeticColors();
            RenderCosmetic();
        }

        public Color GetTileBackColor(int id)
        {
            if (id == 0) return new Color(217f / 255f, 163f / 255f, 35f / 255f, 1f);
            if (id == 1) return new Color(35f / 255f, 122f / 255f, 87f / 255f, 1f);
            if (id == 2) return new Color(40f / 255f, 85f / 255f, 154f / 255f, 1f);
            if (id == 3) return new Color(200f / 255f, 75f / 255f, 67f / 255f, 1f);
            if (id == 4) return new Color(135f / 255f, 100f / 255f, 176f / 255f, 1f);
            if (id == 5) return new Color(220f / 255f, 145f / 255f, 173f / 255f, 1f);
            return new Color(53f / 255f, 59f / 255f, 69f / 255f, 1f);
        }

        public Color GetTableTopColor(int id)
        {
            if (id == 0) return new Color(53f / 255f, 94f / 255f, 75f / 255f, 1f);
            if (id == 1) return new Color(54f / 255f, 95f / 255f, 96f / 255f, 1f);
            if (id == 2) return new Color(56f / 255f, 78f / 255f, 104f / 255f, 1f);
            if (id == 3) return new Color(82f / 255f, 101f / 255f, 117f / 255f, 1f);
            if (id == 4) return new Color(98f / 255f, 102f / 255f, 75f / 255f, 1f);
            if (id == 5) return new Color(98f / 255f, 82f / 255f, 103f / 255f, 1f);
            return new Color(100f / 255f, 98f / 255f, 92f / 255f, 1f);
        }

        // 塗る先のマテリアルスロット。Wire（WireMainMenu.WireCosmetics）がこの前提で
        // レンダラーを集めているので、変えるときは Wire 側と対で直すこと。
        //   牌の背面 … sharedMaterials[0] == backs/green.mat のレンダラーだけを拾っている
        //   卓の上面 … 天板メッシュを2サブメッシュへ割り、スロット1をスロット0の複製にしている
        private const int TILE_BACK_MATERIAL_SLOT = 0;
        private const int TABLE_TOP_MATERIAL_SLOT = 1;

        private void ApplyCosmeticColors()
        {
            // fxCatalog（和了エフェクトのカタログ）はここでは使わない。
            // 以前は fxCatalog が null でも早期 return していたので、未割当のときに
            // 牌背・卓面の色だけが黙って復元されなくなっていた。
            if (statsStore == null) { return; }

            int back = statsStore.GetTileBackColor();
            int top = statsStore.GetTableTopColor();
            ApplyCosmeticColor(cosmeticTileBackRenderers, TILE_BACK_MATERIAL_SLOT, back,
                back > 0 ? GetTileBackColor(back - 1) : Color.white);
            ApplyCosmeticColor(cosmeticTableTopRenderers, TABLE_TOP_MATERIAL_SLOT, top,
                top > 0 ? GetTableTopColor(top - 1) : Color.white);
        }

        // materialSlot は「レンダラーの何番目のマテリアルへ塗るか」だけを意味する。
        // どのパレットから引くかは呼び出し側が決めて color で渡す。
        // （以前は slot 1つがマテリアル番号とパレット選択を兼ねていたため、
        //   牌背がスロット1へ移った瞬間に牌と卓の色が入れ替わる作りだった）
        private void ApplyCosmeticColor(Renderer[] renderers, int materialSlot, int saved, Color color)
        {
            // saved は 0=素の色 / 1..COSMETIC_COLOR_COUNT=パレットの色。
            if (renderers == null || saved < 0 || saved > COSMETIC_COLOR_COUNT) { return; }

            // 素の色に戻すときは MPB を「消す」。
            // 以前はマテリアル自身の _Color / _MainTex を読んで MPB に書き戻していたが、
            // それは何も変えない MPB を全レンダラーへ貼るだけで、色を設定していない人
            // （＝大多数）にも毎回そのコストがかかっていた。
            if (saved == 0)
            {
                for (int i = 0; i < renderers.Length; ++i)
                {
                    if (renderers[i] == null) { continue; }
                    renderers[i].SetPropertyBlock(null, materialSlot);
                }
                return;
            }

            // 全レンダラーで同じ内容なので、ブロックはループの外で1回だけ組む。
            // このコスメが書くのは materialSlot だけ。ドラ強調・暗転・鳴きの MPB は
            // すべて絵柄面（submesh 1）側に書くので、ここで読み戻す必要はない。
            var block = new MaterialPropertyBlock();
            block.SetColor("_Color", color);
            block.SetTexture("_MainTex", Texture2D.whiteTexture);
            for (int i = 0; i < renderers.Length; ++i)
            {
                var target = renderers[i];
                if (target == null) { continue; }
                target.SetPropertyBlock(block, materialSlot);
            }
        }
        [Tooltip("コスメカードのルート（カタログ数を超えたカードは自動で非表示）。")]
        public GameObject[] fxCardRoots;
        [Tooltip("カードの色見本 Image（コスメの主色で塗る）。")]
        public Image[] fxCardSwatches;
        [Tooltip("カードのコスメ名 Text。")]
        public TextMeshProUGUI[] fxCardNames;
        [Tooltip("選択中バッジ（カードごと）。")]
        public GameObject[] fxSelectedBadges;
        [Tooltip("カードのロック暗幕（FxCardN/LockOverlay）。いまは立直BGMの曲だけが解禁条件を持つ。")]
        public GameObject[] fxCardLockOverlays;
        [Tooltip("ロック暗幕の文言（FxCardN/LockOverlay/LockLabel）。")]
        public TextMeshProUGUI[] fxCardLockLabels;
        [Tooltip("右側の詳細：コスメ名。")]
        public TextMeshProUGUI fxDetailName;
        [Tooltip("右側の詳細：説明文。")]
        public TextMeshProUGUI fxDetailDesc;

        [Header("ワールド設定：表示言語（ScreenWorld）")]
        [Tooltip("表示言語マネージャ。ここから日本語／英語／自動を切り替える。")]
        public KemonoLanguage languageManager;
        [Tooltip("言語ボタンの背景 Image（0=日本語 / 1=English / 2=自動）。選択中をハイライトする。")]
        public Image[] langButtonImages;
        [Tooltip("言語設定の説明文。言語で書き換える。")]
        public TextMeshProUGUI worldLangNote;

        [Header("ワールド設定：立直後のツモ切り（ScreenWorld）")]
        // 立直したら自動でツモ切りになるのを好まない人向けのスイッチ。
        // 実体は PlayerData（PlayerStatsStore）で、卓の Mahjong が立直のたびに1回だけ読む。
        [Tooltip("ON/OFF ボタンの背景 Image（0=ON / 1=OFF）。選択中をハイライトする。")]
        public Image[] reachTsumogiriButtonImages;
        [Tooltip("見出し。言語で書き換える。")]
        public TextMeshProUGUI reachTsumogiriHead;
        [Tooltip("説明文。言語で書き換える。")]
        public TextMeshProUGUI reachTsumogiriNote;

        [Header("ワールド設定：押した直後の連打防止（ScreenWorld）")]
        // 卓のボタンを押した直後、次の押下を受け付けない時間。短いほど反応が速く、長いほど二度押しに強い。
        // 実体は PlayerData（PlayerStatsStore.KEY_PRESS_GUARD）で、MahjongPushButton が押下のたびに読む。
        [Tooltip("0.05/0.1/0.2/0.3 秒ボタンの背景 Image。選択中をハイライトする。")]
        public Image[] pressGuardButtonImages;
        [Tooltip("見出し。言語で書き換える。")]
        public TextMeshProUGUI pressGuardHead;
        [Tooltip("説明文。言語で書き換える。")]
        public TextMeshProUGUI pressGuardNote;

        // 選べる長さ（ミリ秒）。pressGuardButtonImages と同じ並び。
        private int[] pressGuardChoicesMs_ = { 50, 100, 200, 300 };

        [Header("ワールド設定：ボタンの押し方 レーザー／タッチ（ScreenWorld）")]
        // タッチ＝卓の対局ボタンを指で奥へ押し込んで押す（VRのみ）。牌の選択はレーザーのまま。
        // 実体は PlayerData（PlayerStatsStore.KEY_TOUCH_INPUT）で、卓ごとの MahjongTouchInput が読む。
        [Tooltip("レーザー/タッチ ボタンの背景 Image（0=レーザー / 1=タッチ）。選択中をハイライトする。")]
        public Image[] touchInputButtonImages;
        [Tooltip("見出し。言語で書き換える。")]
        public TextMeshProUGUI touchInputHead;
        [Tooltip("説明文（注意書き）。言語と選択中の方式・指の骨の有無で書き換える。")]
        public TextMeshProUGUI touchInputNote;
        [Tooltip("この行の部品（札・見出し・ボタン・説明）。デスクトップでは隠す。")]
        public GameObject[] touchInputRowParts;

        [Header("ワールド設定：牌を掴んで打牌（ScreenWorld/PageVr）")]
        // ON＝自分の手番に牌をグリップで掴み、卓の中央側へ出して離すと打牌（VRのみ）。既定はOFF。
        // 実体は PlayerData（PlayerStatsStore.KEY_TILE_GRAB）で、卓ごとの MahjongTileGrab が読む。
        [Tooltip("ON/OFF ボタンの背景 Image（0=ON / 1=OFF）。選択中をハイライトする。")]
        public Image[] tileGrabButtonImages;
        [Tooltip("見出し。言語で書き換える。")]
        public TextMeshProUGUI tileGrabHead;
        [Tooltip("説明文。言語で書き換える。")]
        public TextMeshProUGUI tileGrabNote;
        [Tooltip("この行の部品（札・見出し・ボタン・説明）。デスクトップでは隠す。")]
        public GameObject[] tileGrabRowParts;
        [Tooltip("VR操作ページの「VRのみ」の案内。デスクトップのときだけ出す。")]
        public TextMeshProUGUI vrOnlyNote;

        [Header("ワールド設定：他人のチュンビーム（ScreenWorld）")]
        // 他人のチュンビーム（カットインと声）を自分の画面で出さないスイッチ。自分のビームは常に出る。
        [Tooltip("表示/非表示ボタンの背景 Image（0=表示 / 1=非表示）。選択中をハイライトする。")]
        public Image[] chunBeamHideButtonImages;
        [Tooltip("見出し。言語で書き換える。")]
        public TextMeshProUGUI chunBeamHideHead;
        [Tooltip("説明文。言語で書き換える。")]
        public TextMeshProUGUI chunBeamHideNote;

        [Header("ワールド設定：タブ（ScreenWorld）")]
        // 雀魂式＝左に項目タブ、右にそのページ。出し入れは見ている人の端末だけ（同期なし）。
        [Tooltip("ページのルート（0=サウンド / 1=対局 / 2=VR操作 / 3=表示・演出 / 4=役の早見板 / 5=スタッフ）。")]
        public GameObject[] worldPages;
        [Tooltip("タブボタンの背景 Image（worldPages と同じ並び）。選択中をハイライトする。")]
        public Image[] worldTabImages;
        [Tooltip("スタッフのタブ。権限の無い人には出さない。")]
        public GameObject worldTabStaff;

        private const int WORLD_TAB_SOUND = 0;
        private const int WORLD_TAB_STAFF = 5;
        private int worldTab_ = WORLD_TAB_SOUND;

        [Header("キャラ選択：チュンビーム（ScreenChara）")]
        [Tooltip("ON/OFFボタンのルート。チュンビームを持たないキャラを選んでいる間は隠す。")]
        public GameObject chunBeamToggleRoot;
        [Tooltip("ON/OFFボタンのラベル。")]
        public TextMeshProUGUI chunBeamToggleLabel;
        [Tooltip("ボタン横の説明（解放条件など）。")]
        public TextMeshProUGUI chunBeamNote;

        [Header("ワールド設定：卓の強制リセット（ScreenTableReset）")]
        // スタッフ名簿は3名しか無く、全員不在のインスタンスで卓が詰まると誰も直せない。
        // マスターは必ず1人存在するので、ここだけマスターにも開けておく。
        // カウンター裏のスタッフパネルは見えない壁の内側にあるため、マスターはそちらへ行けない。
        [Tooltip("ワールド設定に出す「卓の強制リセット」への入口。権限が無い人には伸びない。")]
        public GameObject btnTableResetEntry;
        [Tooltip("入口ボタンのラベル。言語で書き換える。")]
        public TextMeshProUGUI btnTableResetEntryLabel;
        [Tooltip("強制リセット画面の見出し。")]
        public TextMeshProUGUI tableResetTitle;
        [Tooltip("強制リセット画面の説明文。")]
        public TextMeshProUGUI tableResetNote;
        [Tooltip("卓ボタンのラベル（index=卓番号-1）。卓番号と状態を書く。")]
        public TextMeshProUGUI[] tableResetLabels;
        [Tooltip("卓ボタンの背景 Image（index=卓番号-1）。選択中をハイライトする。")]
        public Image[] tableResetImages;
        [Tooltip("強制リセット画面の状態表示。")]
        public TextMeshProUGUI tableResetStatus;
        [Tooltip("実行ボタンのラベル。")]
        public TextMeshProUGUI tableResetExecuteLabel;
        [Tooltip("実行ボタンの背景 Image。武装中だけ赤くする。")]
        public Image tableResetExecuteImage;
        [Tooltip("戻るボタンのラベル。")]
        public TextMeshProUGUI tableResetBackLabel;

        // 選択中の卓（-1=未選択）と、その時刻。全部ローカル。
        private int tableResetArmed_ = -1;
        private int tableResetArmedAtMs_ = 0;
        private string tableResetStatus_ = "";
        // 武装の有効期限。長すぎると「さっき押した卓」が生き残って事故になる。
        private const int TABLE_RESET_ARM_TIMEOUT_MS = 15000;

        [Header("キャラ→ボイスパック連動（index=キャラID、-1=連動しない）")]
        [Tooltip("SoundsV2のAudioClip[]で各キャラが入っているパックIDを設定。キャラ選択時に kmj_voice_pack を自動設定する。")]
        public int[] charaVoicePacks = new int[] { -1, -1, -1, MahjongSoundsV2.VOICE_PACK_SILENT };

        // 能力説明（index = キャラID）。能力キットは改定中のため全キャラ「???（調整中）」。
        private string[] charaDescs_;

        // --- フリー対局のローカル状態（同期しない） ---
        private bool freeJoinPending_ = false;     // 入室要求の同期反映待ち
        private float freeJoinPendingUntil_ = 0f;  // 反映待ちの保険タイムアウト

        // --- 段位戦マッチングのローカル状態（同期しない） ---
        private float rankedWaitHoldUntil_ = 0f;      // 列に入る要求の往復猶予
        private float waitReopenSuppressUntil_ = 0f;  // キャンセル後の自己修復抑制
        private bool rankedWaitSeen_ = false;         // 列に入れたことを一度でも見たか
        // 最後に並んでいた段位戦の待機列。スタッフが列を停止して降ろされた時に
        // 「なぜホームへ戻ったか」を出すために使う（0=並んでいない）。
        private int rankedWaitLastStatus_ = 0;
        // 待機状況の行の前回値。毎秒TMPへ書き込むとQuestで無駄なメッシュ再構築が走るので、
        // 文字列が変わった時だけ触る。
        private string[] queueInfoShown_ = new string[5];
        private bool[] queueGlowShown_ = new bool[5];
        private bool queueCacheReady_ = false;
        private float homeInfoUntil_ = 0f;         // ホームの一時メッセージ消去時刻

        // --- 表示言語（純ローカル。KemonoLanguage が SetLanguage で押し込む） ---
        private int lang_ = KemonoLanguage.LANG_JA;

        // --- プロフィールの戦績タブ（純ローカル。0=四麻段位戦 1=三麻段位戦 2=フリー対局） ---
        private int profileTab_ = PlayerStatsStore.CAT_YONMA;
        // タブの色（通常＝EnsureButtonの生成色に合わせる／選択中＝ハイライト）。
        private Color tabNormalColor_ = new Color(1f, 0.996f, 0.976f, 1f);
        
    // 画面フェードで1枚ずつ色を塗る Graphic の上限。CanvasGroup が付いた枝は
    // 配下ごと alpha 1本で畳むので、分析パネルの数百個はここに乗らない。
    private const int SCREEN_GRAPHIC_MAX = 256;
    private const int SCREEN_GROUP_MAX = 8;
    private const float SCREEN_FADE_OUT_SECONDS = 0.13f;
    private const float SCREEN_INCOMING_DELAY_SECONDS = 0.06f;
    private const float SCREEN_FADE_IN_SECONDS = 0.18f;

    private GraphicRaycaster menuRaycaster_;
    private GameObject currentScreen_;
    private GameObject transitionFrom_;
    private GameObject transitionTo_;
    private bool screenTransitionInitialized_ = false;
    private bool screenTransitionActive_ = false;
    private bool screenIncomingStarted_ = false;
    private float screenTransitionTime_ = 0.0f;
    private Graphic[] screenFromGraphics_;
    private Graphic[] screenToGraphics_;
    private Color[] screenFromColors_;
    private Color[] screenToColors_;
    private int screenFromCount_ = 0;
    private int screenToCount_ = 0;
    private CanvasGroup[] screenFromGroups_;
    private CanvasGroup[] screenToGroups_;
    private int screenFromGroupCount_ = 0;
    private int screenToGroupCount_ = 0;
        private Color tabSelectedColor_ = new Color(1f, 0.843f, 0f, 1f);

        // ============================================================
        // 表示言語（KemonoLanguage から配られる。同期変数ゼロ・完全ローカル）
        //
        // 静的ラベル（ボタンの文字など BuildMainMenu が焼いたもの）は
        // MainMenu ルートの KemonoLocalizer が差し替える。ここが持つのは
        // 実行時に組み立てる動的文字列だけ。
        // ============================================================
        public void SetLanguage(int lang)
        {
            lang_ = (lang > 0 && lang < KemonoLanguage.NUM_LANG) ? lang : KemonoLanguage.LANG_JA;

            SetupCharaDescs();

            // 表示中の画面を描き直す（畳んである画面は開くときに描かれる）。
            RenderHeader();
            RenderHomeChara();
            // UpdateFreeButton は matchmaking を null チェックせずに触る（本来 UpdateFreeUi からしか呼ばれない）。
            if (matchmaking != null) { UpdateFreeButton(); }

            if (screenProfile != null && screenProfile.activeSelf) { RenderProfile(); }
            if (screenChara != null && screenChara.activeSelf) { RenderChara(); }
            if (screenCosmetic != null && screenCosmetic.activeSelf) { RenderCosmetic(); }
            if (screenWorld != null && screenWorld.activeSelf) { RenderWorld(); }
            if (screenWaiting != null && screenWaiting.activeSelf) { RenderWaiting(); }
            if (screenFreeSetup != null && screenFreeSetup.activeSelf) { RenderFreeSetup(); }
            if (screenFreeRoom != null && screenFreeRoom.activeSelf) { RenderFreeRoom(); }
            if (screenFreeConfirm != null && screenFreeConfirm.activeSelf) { RenderFreeConfirm(); }
            if (screenInGame != null && screenInGame.activeSelf) { RenderInGame(); }
            if (screenAbandonConfirm != null && screenAbandonConfirm.activeSelf) { RenderAbandonConfirm(); }
            if (screenTableReset != null && screenTableReset.activeSelf) { RenderTableReset(); }
            if (screenFreeList != null && screenFreeList.activeSelf) { RenderFreeList(); }
        }

        public int GetLanguage() { return lang_; }

        // 日本語／英語／韓国語／簡体字／繁体字の五択。分岐を毎回書かずに済ませるための短縮。
        private string T(string ja, string en, string ko, string zhs, string zht)
        {
            if (lang_ == KemonoLanguage.LANG_KO) { return ko; }
            if (lang_ == KemonoLanguage.LANG_ZHS) { return zhs; }
            if (lang_ == KemonoLanguage.LANG_ZHT) { return zht; }
            return (lang_ == KemonoLanguage.LANG_EN) ? en : ja;
        }

        // キャラ説明。内容は能力キット改定中のためプレースホルダ。
        private void SetupCharaDescs()
        {
            if (charaDescs_ == null || charaDescs_.Length != PlayerStatsStore.NUM_CHARAS)
            {
                charaDescs_ = new string[PlayerStatsStore.NUM_CHARAS];
            }
            var wip = T("???（調整中）", "??? (WIP)", "??? (조정 중)", "???（调整中）", "???（調整中）");
            for (int i = 0; i < charaDescs_.Length; ++i) { charaDescs_[i] = wip; }

            // 自画像だけは仕様が確定しているので、能力キットとは別に本文を出す。
            if (charaDescs_.Length > PlayerStatsStore.CHARA_SELFIE)
            {
                // 解像度の注意書きは実装どおりに書くこと。共有版は PlayerData の容量に
                // 合わせてラダー（96x144 / 80x120 / 64x96）を自動で下る。自分の画面だけは
                // 撮影用RT の実物（512x768）を返すが、それは RenderTexture なので
                // 同期にも PlayerData にも乗らない＝撮ったインスタンスを出ると消える。
                // 本文は DetailText（560x350・font24）で日英とも9行に収まる実測値。
                charaDescs_[PlayerStatsStore.CHARA_SELFIE] = T(
                    "自分のアバターを撮って、そのままキャラにします。ボイスはありません（役は順に表示されます）。\n"
                    + "写真は他のプレイヤーにも表示され、次にこのワールドへ入ったときも残ります。\n"
                    + "※共有される写真は容量の都合で粗くなります。自分の画面だけは撮った直後から高画質ですが、"
                    + "インスタンスを出ると粗い方に戻ります。毎回きれいに見たいときは、入るたびに撮り直してください。",
                    "Take a photo of your own avatar and use it as your character. It has no voice; yaku are shown one by one instead.\n"
                    + "The photo is shown to other players too, and it stays for your next visit.\n"
                    + "Note: the shared photo is downscaled to fit the save limit. Only your own screen shows it in full quality, "
                    + "and only until you leave this instance. Retake it in each instance to keep it sharp.",
                    "자신의 아바타를 촬영해서 그대로 캐릭터로 사용합니다. 보이스는 없으며, 역은 하나씩 표시됩니다.\n" + "사진은 다른 플레이어에게도 표시되며, 다음에 이 월드에 들어올 때도 남아 있습니다.\n" + "※공유용 사진은 저장 용량 때문에 화질이 낮아집니다. 자신의 화면에서만 촬영 직후 고화질로 보이며, " + "인스턴스를 나가면 낮은 화질로 돌아갑니다. 항상 선명하게 보이고 싶다면 들어올 때마다 다시 촬영해 주세요.", "拍下自己的虚拟形象，直接作为角色使用。没有语音（役会依次显示）。\n" + "照片也会显示给其他玩家，下次进入这个世界时依然保留。\n" + "※共享的照片因存储容量限制会变得粗糙。只有自己的画面在拍摄后立即显示高画质，" + "离开实例后会恢复为粗糙的版本。想一直看清楚的话，请每次进入时重新拍摄。", "拍下自己的虛擬形象，直接作為角色使用。沒有語音（役會依次顯示）。\n" + "照片也會顯示給其他玩家，下次進入這個世界時依然保留。\n" + "※共享的照片因儲存容量限制會變得粗糙。只有自己的畫面在拍攝後立即顯示高畫質，" + "離開實例後會恢復為粗糙的版本。想一直看清楚的話，請每次進入時重新拍攝。");
            }
        }

        void Start()
        {
            SetupCharaDescs();
            // 復元済み／保存が無い場合ぶんの1回。復元待ちは OnPlayerRestored が持つ。
            ApplyCosmeticColors();

            ApplyPortraits();
            RenderHeader();
            ApplyCharaPreview();
            RenderHomeChara();
            ShowHome();

            // 待機画面の更新ループ（1秒間隔・Updateポーリングはしない規約のため遅延イベント駆動。
            // 待機画面が非表示のときは即returnの軽量処理）。
            SendCustomEventDelayedSeconds(nameof(_WaitingTick), 1.0f);
        }

        // PlayerData 復元時にヘッダーを再描画（表示中の画面も最新化する）。
        // キャラ／能力の保留 flush は PlayerStatsStore。ボイスは MahjongVoiceSelector 側のみ。
        // 旧バージョンで kmj_chara と kmj_voice_pack が不一致になった保存データも、
        // 復元時に現在のキャラへ再整合する。Selector自身とのイベント順はpendingで吸収される。
        public override void OnPlayerRestored(VRCPlayerApi player)
        {
            if (player == null || !player.isLocal) { return; }

            if (statsStore != null)
            {
                ApplyCharaVoicePack(statsStore.GetChara(), false);
            }
            RenderHeader();
            ApplyCharaPreview();
            RenderHomeChara();

            // 復元済みキャラに対応するボイスパックを反映する。
            // キャラ選択時（SelectChara）だけの書き込みだと、キャラを選び直さない限り
            // kmj_voice_pack が古いまま（連動導入前に保存されたプロフィール等）になる。
            // MahjongVoiceSelector 側の復元前保留があるため、コールバック順序は問わない。
            if (statsStore != null)
            {
                ApplyCharaVoicePack(statsStore.GetChara(), false);
            }

            if (screenProfile != null && screenProfile.activeSelf)
            {
                RenderProfile();
            }
            if (screenChara != null && screenChara.activeSelf)
            {
                RenderChara();
            }
            // 保存済みの牌背／卓面の色をここで当てる。
            // 以前は Start から1秒ごとに自分を呼び直して復元を待っていたが、
            // 対象レンダラーが約2600本あり、待っている間ずっと毎秒ぶんの
            // GetPropertyBlock と sharedMaterials の確保を出していた。
            // 復元の通知はこのコールバックが持っているので、待ち合わせは不要。
            ApplyCosmeticColors();

            if (screenCosmetic != null && screenCosmetic.activeSelf)
            {
                RenderCosmetic();
            }
        }

        // ============================================================
        // 画面遷移（Button onClick → SendCustomEvent で呼ぶ）
        // ============================================================

        // 全画面ルートを畳む（画面ルート単位のSetActiveのみ。子は触らない）。
        private void HideAllScreens()
        {
            if (screenHome != null) { screenHome.SetActive(false); }
            if (screenProfile != null) { screenProfile.SetActive(false); }
            if (screenChara != null) { screenChara.SetActive(false); }
            if (screenWaiting != null) { screenWaiting.SetActive(false); }
            if (screenFreeSetup != null) { screenFreeSetup.SetActive(false); }
            if (screenFreeRoom != null) { screenFreeRoom.SetActive(false); }
            if (screenFreeConfirm != null) { screenFreeConfirm.SetActive(false); }
            if (screenCosmetic != null) { screenCosmetic.SetActive(false); }
            if (screenWorld != null) { screenWorld.SetActive(false); }
            if (screenSelfie != null) { screenSelfie.SetActive(false); }
            if (screenInGame != null) { screenInGame.SetActive(false); }
            if (screenAbandonConfirm != null) { screenAbandonConfirm.SetActive(false); }
            if (screenTableReset != null) { screenTableReset.SetActive(false); }
            if (screenFreeList != null) { screenFreeList.SetActive(false); }
        }

public void ShowHome()
{
    // 対局中（卓に座ったままメインメニューへ来た）はホームの代わりに案内画面を出す。
    // 途中退出の直後は席の申告が畳まれるまで数秒かかるので、その間は差し替えない。
    if (screenInGame != null && Time.time >= abandonSuppressUntil_ && IsSeatedInOngoingGame())
    {
        ShowInGame();
        return;
    }

    ShowHomeRaw();
}

// 差し替えを通さない素のホーム表示。案内メッセージ付きの遷移から使う。
private void ShowHomeRaw()
{
    RenderHeader();
    RenderHomeChara();
    UpdateRankedButtons();
    BeginScreenTransition(screenHome);
}

// 段位戦4列の表示/非表示。スタッフパネルが閉じた列はボタンごと消す。
// 「三麻 半荘戦だけのイベント」を立てやすくするための仕組み。
// 押せてしまっても Owner が要求を捨てるので、ここは見た目の整合だけを担う。
// フリー対局はレートが動かないので対象外＝常に出す。
private void UpdateRankedButtons()
{
    if (matchmaking == null) { return; }
    SetButtonActive(btnRanked4Han, matchmaking.IsRankedColumnEnabled(
        MatchmakingManager.STATUS_WAIT_YONMA));
    SetButtonActive(btnRanked4Ton, matchmaking.IsRankedColumnEnabled(
        MatchmakingManager.STATUS_WAIT_YONMA_TONPU));
    SetButtonActive(btnRanked3Han, matchmaking.IsRankedColumnEnabled(
        MatchmakingManager.STATUS_WAIT_SANMA));
    SetButtonActive(btnRanked3Ton, matchmaking.IsRankedColumnEnabled(
        MatchmakingManager.STATUS_WAIT_SANMA_TONPU));
    UpdateQueueBadges();
}

// 各ボタンの下段に待機状況を出し、待っている人がいる列の枠を光らせる。
// 読む値は全部 MatchmakingManager の同期状態なので、全員の画面で同じものが出る。
// 呼ばれるのは1秒Tickとホーム表示時だけ。Update は使わない。
private void UpdateQueueBadges()
{
    if (matchmaking == null || queueInfoTexts == null) { return; }

    for (int i = 0; i < 5; ++i)
    {
        int waiting;
        int guiding;
        bool preparing = false;

        if (i < 4)
        {
            int status = RankedStatusOfSlot(i);
            waiting = matchmaking.GetWaitingCount(status);
            guiding = matchmaking.GetGuidingCount(status);
        }
        else
        {
            // 部屋＝卓（マルチルーム）なのでワールド全体の募集人数を出す。
            // 自分の部屋（GetFreeState）を見ると、ホームにいる人はどこにも
            // 入っていないため必ず「0人待機中...」になってしまう。
            guiding = matchmaking.GetFreeGuidingCount();
            waiting = matchmaking.GetFreeRecruitingCountTotal();
            // 募集中の部屋が1つも無く、代表が設定中の部屋だけある間は「準備中...」。
            preparing = matchmaking.GetFreeRecruitingRoomCount() == 0
                        && matchmaking.GetFreeSetupRoomCount() > 0;
        }

        string line;
        if (guiding > 0)
        {
            line = T("卓へ案内中...", "Heading to a table...", "탁으로 안내 중...", "正在引导至牌桌...", "正在引導至牌桌...");
        }
        else if (preparing)
        {
            // フリー対局は代表が設定画面を開いている間があり、まだ誰も並べない。
            line = T("準備中...", "Setting up...", "준비 중...", "准备中...", "準備中...");
        }
        else
        {
            line = T(waiting + "人待機中...", waiting + " waiting...", waiting + "명 대기 중...", waiting + "人等待中...", waiting + "人等待中...");
        }

        // 待っている人がいる／案内中＝その列は今動いている、というサイン。
        bool glow = waiting > 0 || guiding > 0 || preparing;
        ApplyQueueBadge(i, line, glow);
    }
    queueCacheReady_ = true;
}

private int RankedStatusOfSlot(int i)
{
    if (i == 0) { return MatchmakingManager.STATUS_WAIT_YONMA; }
    if (i == 1) { return MatchmakingManager.STATUS_WAIT_YONMA_TONPU; }
    if (i == 2) { return MatchmakingManager.STATUS_WAIT_SANMA; }
    return MatchmakingManager.STATUS_WAIT_SANMA_TONPU;
}

private void ApplyQueueBadge(int i, string line, bool glow)
{
    if (queueInfoTexts != null && i < queueInfoTexts.Length && queueInfoTexts[i] != null)
    {
        if (!queueCacheReady_ || queueInfoShown_[i] != line)
        {
            queueInfoShown_[i] = line;
            queueInfoTexts[i].text = line;
        }
    }
    if (queueGlows != null && i < queueGlows.Length && queueGlows[i] != null)
    {
        if (!queueCacheReady_ || queueGlowShown_[i] != glow)
        {
            queueGlowShown_[i] = glow;
            queueGlows[i].SetActive(glow);
        }
    }
}

// SetActive は毎回呼ぶと Canvas のリビルドを誘発するので、変化した時だけ触る。
private void SetButtonActive(GameObject go, bool active)
{
    if (go == null) { return; }
    if (go.activeSelf != active) { go.SetActive(active); }
}

public void ShowProfile()
{
    RenderProfile();
    BeginScreenTransition(screenProfile);
}

        // 戦績タブ切替（BtnTab4 / BtnTab3 / BtnTabF の onClick から呼ぶ。純ローカル）。
        public void ShowProfileTab4() { profileTab_ = PlayerStatsStore.CAT_YONMA; RenderProfile(); }
        public void ShowProfileTab3() { profileTab_ = PlayerStatsStore.CAT_SANMA; RenderProfile(); }
        public void ShowProfileTabFree() { profileTab_ = PlayerStatsStore.CAT_FREE; RenderProfile(); }

public void ShowChara()
{
    RenderChara();
    BeginScreenTransition(screenChara);
}

        // 装飾品（和了演出コスメ）画面。BtnCosmetic / ScreenCosmetic の BtnBack から呼ぶ。
public void ShowCosmetic()
{
    RenderCosmetic();
    BeginScreenTransition(screenCosmetic);
}

        // ============================================================
        // ワールド設定（表示言語）
        //
        // 卓に付いていた言語ボタンは廃止し、切替口はここ1か所に集約した。
        // 押した内容は KemonoLanguage が PlayerData("kmj_lang") へ保存するので、
        // 次回入場・別インスタンスでも同じ言語で開く。
        // ============================================================
        public void ShowWorld()
        {
            // 開くたびに先頭（サウンド）から。前回のタブは覚えない。
            worldTab_ = WORLD_TAB_SOUND;
            RenderWorld();
            BeginScreenTransition(screenWorld);
        }

        // 卓の強制リセット画面の「もどる」。来たタブ（スタッフ）へ戻す。
        public void ShowWorldStaff()
        {
            worldTab_ = WORLD_TAB_STAFF;
            RenderWorld();
            BeginScreenTransition(screenWorld);
        }

        // 左のタブ（雀魂式）。0=サウンド / 1=対局 / 2=VR操作 / 3=表示・演出 / 4=役の早見板 / 5=スタッフ
        public void SetWorldTab0() { SelectWorldTab(0); }
        public void SetWorldTab1() { SelectWorldTab(1); }
        public void SetWorldTab2() { SelectWorldTab(2); }
        public void SetWorldTab3() { SelectWorldTab(3); }
        public void SetWorldTab4() { SelectWorldTab(4); }
        public void SetWorldTab5() { SelectWorldTab(5); }

        private void SelectWorldTab(int tab)
        {
            worldTab_ = tab;
            RenderWorld();
        }

        public void SetLanguageJapanese()
        {
            if (languageManager != null) { languageManager.SetLanguage(KemonoLanguage.LANG_JA); }
            RenderWorld();
        }

        public void SetLanguageEnglish()
        {
            if (languageManager != null) { languageManager.SetLanguage(KemonoLanguage.LANG_EN); }
            RenderWorld();
        }

        public void SetLanguageKorean()
        {
            if (languageManager != null) { languageManager.SetLanguage(KemonoLanguage.LANG_KO); }
            RenderWorld();
        }

        public void SetLanguageChineseSimplified()
        {
            if (languageManager != null) { languageManager.SetLanguage(KemonoLanguage.LANG_ZHS); }
            RenderWorld();
        }

        public void SetLanguageChineseTraditional()
        {
            if (languageManager != null) { languageManager.SetLanguage(KemonoLanguage.LANG_ZHT); }
            RenderWorld();
        }

        // 手動指定をやめて VRChat 本体の表示言語に従う。
        public void SetLanguageAuto()
        {
            if (languageManager != null) { languageManager.ResetToAuto(); }
            RenderWorld();
        }

        // 立直したらツモ切りを自動でONにするか。自分にだけ効くローカル設定。
        public void SetReachTsumogiriOn()
        {
            if (statsStore != null) { statsStore.SetReachAutoTsumogiri(true); }
            RenderWorld();
        }

        public void SetReachTsumogiriOff()
        {
            if (statsStore != null) { statsStore.SetReachAutoTsumogiri(false); }
            RenderWorld();
        }

        // 押した直後の連打防止の長さ。自分にだけ効くローカル設定。
        public void SetPressGuard50() { SetPressGuardMs(50); }
        public void SetPressGuard100() { SetPressGuardMs(100); }
        public void SetPressGuard200() { SetPressGuardMs(200); }
        public void SetPressGuard300() { SetPressGuardMs(300); }

        private void SetPressGuardMs(int ms)
        {
            if (statsStore != null) { statsStore.SetPressGuardMs(ms); }
            RenderWorld();
        }

        // 卓のボタンの押し方。自分にだけ効くローカル設定（卓は0.5秒以内に拾う）。
        public void SetTouchInputLaser()
        {
            if (statsStore != null) { statsStore.SetTouchInput(false); }
            RenderWorld();
        }

        public void SetTouchInputTouch()
        {
            if (statsStore != null) { statsStore.SetTouchInput(true); }
            RenderWorld();
        }

        // 牌を掴んで打牌する。自分にだけ効くローカル設定（卓は0.5秒以内に拾う）。
        public void SetTileGrabOn()
        {
            if (statsStore != null) { statsStore.SetTileGrab(true); }
            RenderWorld();
        }

        public void SetTileGrabOff()
        {
            if (statsStore != null) { statsStore.SetTileGrab(false); }
            RenderWorld();
        }

        // 他人のチュンビームを出す／出さない。自分にだけ効くローカル設定。
        public void SetChunBeamShowOthers()
        {
            if (statsStore != null) { statsStore.SetChunBeamHideOthers(false); }
            RenderWorld();
        }

        public void SetChunBeamHideOthers()
        {
            if (statsStore != null) { statsStore.SetChunBeamHideOthers(true); }
            RenderWorld();
        }

        private void RenderWorld()
        {
            if (langButtonImages != null)
            {
                // 0=日本語 / 1=English / 2=自動 / 3=한국어 / 4=简体中文 / 5=繁體中文
                // （韓国語・中国語は後から足したので自動の後ろ）。手動指定していなければ「自動」が選択中。
                int selected = 2;
                if (languageManager != null && languageManager.IsManual())
                {
                    selected = languageManager.IsKorean() ? 3
                        : languageManager.IsChineseSimplified() ? 4
                        : languageManager.IsChineseTraditional() ? 5
                        : languageManager.IsEnglish() ? 1 : 0;
                }

                for (int i = 0; i < langButtonImages.Length; ++i)
                {
                    if (langButtonImages[i] == null) { continue; }
                    langButtonImages[i].color = (i == selected) ? tabSelectedColor_ : tabNormalColor_;
                }
            }

            // 強制リセットは権限のある人にしか見せない（タブごと隠す）。
            // 表示を消すだけでは防壁にならないが、本当の検査は
            // MatchmakingManager の Owner 側（CanForceResetTables）がやる。
            bool staffAllowed = matchmaking != null && matchmaking.CanLocalPlayerForceResetTables();
            if (!staffAllowed && worldTab_ == WORLD_TAB_STAFF) { worldTab_ = WORLD_TAB_SOUND; }
            if (worldTabStaff != null && worldTabStaff.activeSelf != staffAllowed) { worldTabStaff.SetActive(staffAllowed); }
            if (btnTableResetEntry != null && btnTableResetEntry.activeSelf != staffAllowed) { btnTableResetEntry.SetActive(staffAllowed); }
            if (btnTableResetEntryLabel != null)
            {
                btnTableResetEntryLabel.text = T("開く", "Open", "열기", "打开", "打開");
            }

            // 左のタブ＝右に出すページ。選ばれていないページは畳む（同期なし・自分の画面だけ）。
            if (worldPages != null)
            {
                for (int i = 0; i < worldPages.Length; ++i)
                {
                    if (worldPages[i] == null) { continue; }
                    bool on = i == worldTab_;
                    if (worldPages[i].activeSelf != on) { worldPages[i].SetActive(on); }
                }
            }
            if (worldTabImages != null)
            {
                for (int i = 0; i < worldTabImages.Length; ++i)
                {
                    if (worldTabImages[i] == null) { continue; }
                    worldTabImages[i].color = (i == worldTab_) ? tabSelectedColor_ : tabNormalColor_;
                }
            }

            if (worldLangNote != null)
            {
                worldLangNote.text = T(
                    "選んだ言語はプロフィールに保存され、次回の入場でも引き継がれます。「自動」に戻すと VRChat 本体の表示言語に合わせます。",
                    "Your choice is saved to your profile and used next time. Auto follows your VRChat display language.",
                    "선택한 언어는 프로필에 저장되어 다음 입장 때도 유지됩니다. '자동'으로 되돌리면 VRChat 표시 언어를 따릅니다.", "所选语言会保存到个人资料，下次进入时也会沿用。改回「自动」则跟随 VRChat 的显示语言。", "所選語言會儲存到個人資料，下次進入時也會沿用。改回「自動」則跟隨 VRChat 的顯示語言。");
            }

            // 立直後のツモ切り。未設定はON＝従来どおり。
            bool reachTsumogiri = statsStore == null || statsStore.GetReachAutoTsumogiri();
            if (reachTsumogiriButtonImages != null)
            {
                int selected = reachTsumogiri ? 0 : 1;
                for (int i = 0; i < reachTsumogiriButtonImages.Length; ++i)
                {
                    if (reachTsumogiriButtonImages[i] == null) { continue; }
                    reachTsumogiriButtonImages[i].color = (i == selected) ? tabSelectedColor_ : tabNormalColor_;
                }
            }
            if (reachTsumogiriHead != null)
            {
                reachTsumogiriHead.text = T("立直後のツモ切り", "Auto tsumogiri on riichi", "리치 후 쯔모기리", "立直后自动摸切", "立直後自動摸切");
            }
            if (reachTsumogiriNote != null)
            {
                reachTsumogiriNote.text = reachTsumogiri
                    ? T("立直すると自動でツモ切りになります。立直中も卓のボタンでOFFへ戻せます。",
                        "Riichi turns on auto-discard. You can switch it off on the table anytime.",
                        "리치하면 자동으로 쯔모기리가 됩니다. 리치 중에도 탁의 버튼으로 끌 수 있습니다.", "立直后会自动摸切。立直中也可以用牌桌上的按钮关闭。", "立直後會自動摸切。立直中也可以用牌桌上的按鈕關閉。")
                    : T("立直してもツモ切りは自動になりません。卓のボタンで切り替えます。",
                        "Riichi leaves auto-discard off. Use the table button yourself.",
                        "리치해도 쯔모기리는 자동으로 켜지지 않습니다. 탁의 버튼으로 전환하세요.", "立直后不会自动摸切。请用牌桌上的按钮切换。", "立直後不會自動摸切。請用牌桌上的按鈕切換。");
            }

            // 押した直後の連打防止。未設定は 0.2 秒。
            int guardMs = statsStore != null ? statsStore.GetPressGuardMs() : PlayerStatsStore.PRESS_GUARD_DEFAULT_MS;
            if (pressGuardButtonImages != null)
            {
                for (int i = 0; i < pressGuardButtonImages.Length; ++i)
                {
                    if (pressGuardButtonImages[i] == null) { continue; }
                    bool sel = i < pressGuardChoicesMs_.Length && pressGuardChoicesMs_[i] == guardMs;
                    pressGuardButtonImages[i].color = sel ? tabSelectedColor_ : tabNormalColor_;
                }
            }
            if (pressGuardHead != null)
            {
                pressGuardHead.text = T("押した直後の連打防止", "Double-press guard", "연타 방지", "防止连按", "防止連按");
            }
            if (pressGuardNote != null)
            {
                pressGuardNote.text = T(
                    "卓のボタンを押した直後、次の押下を受け付けない時間。短いほど速く反応します。",
                    "Ignores a second press on the table for this long. Shorter feels snappier.",
                    "탁의 버튼을 누른 직후, 이 시간 동안은 다음 입력을 받지 않습니다. 짧을수록 반응이 빠릅니다.", "按下牌桌按钮后，在这段时间内不接受下一次按下。越短反应越快。", "按下牌桌按鈕後，在這段時間內不接受下一次按下。越短反應越快。");
            }

            // 卓のボタンの押し方。未設定はレーザー。タッチは VR だけ＝デスクトップでは行ごと隠す。
            var lpWorld = Networking.LocalPlayer;
            bool inVr = Utilities.IsValid(lpWorld) && lpWorld.IsUserInVR();
            if (touchInputRowParts != null)
            {
                for (int i = 0; i < touchInputRowParts.Length; ++i)
                {
                    if (touchInputRowParts[i] != null && touchInputRowParts[i].activeSelf != inVr) { touchInputRowParts[i].SetActive(inVr); }
                }
            }
            bool touch = statsStore != null && statsStore.GetTouchInput();
            if (touchInputButtonImages != null)
            {
                int selected = touch ? 1 : 0;
                for (int i = 0; i < touchInputButtonImages.Length; ++i)
                {
                    if (touchInputButtonImages[i] == null) { continue; }
                    touchInputButtonImages[i].color = (i == selected) ? tabSelectedColor_ : tabNormalColor_;
                }
            }
            if (touchInputHead != null)
            {
                touchInputHead.text = T("卓のボタンの押し方", "How to press table buttons", "탁 버튼 누르는 방법", "牌桌按钮的按法", "牌桌按鈕的按法");
            }
            if (touchInputNote != null)
            {
                if (!touch)
                {
                    touchInputNote.text = T(
                        "レーザーで指して押します。「タッチ」にすると、鳴き・立直・和了などのボタンを指で奥へ押し込んで押せます（VRのみ）。",
                        "Point with the laser and pull the trigger. With Touch, push call/riichi/win buttons in with your finger (VR only).",
                        "레이저로 가리키고 트리거를 당겨 누릅니다. '터치'로 하면 울기·리치·화료 등의 버튼을 손가락으로 밀어 넣어 누를 수 있습니다 (VR 전용).", "用激光指向并扣动扳机按下。改为「触摸」后，可以用手指把鸣牌・立直・和牌等按钮往里按（仅限 VR）。", "用雷射指向並扣動扳機按下。改為「觸摸」後，可以用手指把鳴牌・立直・和牌等按鈕往裡按（僅限 VR）。");
                }
                else
                {
                    // 指の骨が無いアバターは手の位置で代用している（MahjongTouchInput.GetFingertip と同じ判定）。
                    bool noFinger = Utilities.IsValid(lpWorld)
                        && lpWorld.GetBonePosition(HumanBodyBones.RightIndexDistal) == Vector3.zero;
                    string head = T(
                        "ボタンを指で奥へ押し込むと反応します（牌はレーザーのまま）。小さいアバター・腕が短いと上の段に届かないことがあります。",
                        "Push buttons in with a fingertip (tiles stay on laser). Small avatars may not reach the upper row. ",
                        "버튼을 손가락 끝으로 밀어 넣으면 반응합니다 (패는 레이저 그대로). 작은 아바타나 팔이 짧으면 위쪽 줄에 닿지 않을 수 있습니다. ", "用指尖把按钮往里按即可反应（牌仍用激光）。虚拟形象较小或手臂较短时，可能够不到上排。", "用指尖把按鈕往裡按即可反應（牌仍用雷射）。虛擬形象較小或手臂較短時，可能搆不到上排。");
                    string bone = noFinger
                        ? T("※今のアバターは指の骨が無いため、手の位置で判定しています（少しずれます）。",
                            "* This avatar has no finger bones, so your hand position is used (slightly off).",
                            "※현재 아바타는 손가락 뼈가 없어서 손 위치로 판정합니다 (약간 어긋납니다).", "※当前的虚拟形象没有手指骨骼，因此以手的位置判定（会稍有偏差）。", "※目前的虛擬形象沒有手指骨骼，因此以手的位置判定（會稍有偏差）。")
                        : T("指の骨が無いアバターでは、手の位置で判定します（少しずれます）。",
                            "Avatars without finger bones use the hand position instead (slightly off).",
                            "손가락 뼈가 없는 아바타는 손 위치로 판정합니다 (약간 어긋납니다).", "没有手指骨骼的虚拟形象以手的位置判定（会稍有偏差）。", "沒有手指骨骼的虛擬形象以手的位置判定（會稍有偏差）。");
                    touchInputNote.text = head + bone;
                }
            }

            // 牌を掴んで打牌。未設定はOFF。VR だけ＝デスクトップでは行ごと隠して「VRのみ」の案内を出す。
            if (tileGrabRowParts != null)
            {
                for (int i = 0; i < tileGrabRowParts.Length; ++i)
                {
                    if (tileGrabRowParts[i] != null && tileGrabRowParts[i].activeSelf != inVr) { tileGrabRowParts[i].SetActive(inVr); }
                }
            }
            if (vrOnlyNote != null)
            {
                if (vrOnlyNote.gameObject.activeSelf == inVr) { vrOnlyNote.gameObject.SetActive(!inVr); }
                vrOnlyNote.text = T("このページの設定は VR で遊ぶときだけ使えます。",
                    "These settings are only available when playing in VR.",
                    "이 페이지의 설정은 VR로 플레이할 때만 사용할 수 있습니다.", "本页的设置仅在使用 VR 游玩时可用。", "本頁的設定僅在使用 VR 遊玩時可用。");
            }
            bool tileGrab = statsStore != null && statsStore.GetTileGrab();
            if (tileGrabButtonImages != null)
            {
                int selected = tileGrab ? 0 : 1;
                for (int i = 0; i < tileGrabButtonImages.Length; ++i)
                {
                    if (tileGrabButtonImages[i] == null) { continue; }
                    tileGrabButtonImages[i].color = (i == selected) ? tabSelectedColor_ : tabNormalColor_;
                }
            }
            if (tileGrabHead != null)
            {
                tileGrabHead.text = T("牌を掴んで打牌", "Grab tiles to discard", "패를 잡아서 버리기", "抓牌打出", "抓牌打出");
            }
            if (tileGrabNote != null)
            {
                tileGrabNote.text = T(
                    "自分の手番に牌をグリップで掴み、牌山まで出して離すと打牌します。北抜きはボタンの後に北を同じように出します。掴んでいる間は持ち時間が止まります（1手につき15秒まで）。",
                    "On your turn, grip a tile and release it over the wall to discard. For Kita, press the button, then do the same with a North tile. The clock pauses while held (up to 15s per turn).",
                    "자신의 차례에 패를 그립으로 잡고, 패산 앞까지 가져가서 놓으면 버립니다. 북 빼기는 버튼을 누른 뒤 북을 같은 방법으로 내보내세요. 잡고 있는 동안은 제한 시간이 멈춥니다 (한 차례에 15초까지).", "轮到自己时用握键抓住牌，拿到牌山前松开即可打出。拔北时先按按钮，再用同样的方法打出北。抓着牌时思考时间会暂停（每手最多15秒）。", "輪到自己時用握鍵抓住牌，拿到牌山前鬆開即可打出。拔北時先按按鈕，再用同樣的方法打出北。抓著牌時思考時間會暫停（每手最多15秒）。");
            }

            // 他人のチュンビーム。未設定は表示。
            bool chunHide = statsStore != null && statsStore.GetChunBeamHideOthers();
            if (chunBeamHideButtonImages != null)
            {
                int selected = chunHide ? 1 : 0;
                for (int i = 0; i < chunBeamHideButtonImages.Length; ++i)
                {
                    if (chunBeamHideButtonImages[i] == null) { continue; }
                    chunBeamHideButtonImages[i].color = (i == selected) ? tabSelectedColor_ : tabNormalColor_;
                }
            }
            if (chunBeamHideHead != null)
            {
                chunBeamHideHead.text = T("他人のチュンビーム", "Others' Chun Beam", "다른 사람의 춘 빔", "他人的中光束", "他人的中光束");
            }
            if (chunBeamHideNote != null)
            {
                chunBeamHideNote.text = chunHide
                    ? T("他の人のチュンビームは画面にも声にも出ません。自分のビームは出ます。",
                        "Others' Chun Beam is hidden, voice included. Your own still plays.",
                        "다른 사람의 춘 빔은 화면에도 음성에도 나오지 않습니다. 자신의 빔은 나옵니다.", "他人的中光束不会显示画面，也不会播放语音。自己的光束照常出现。", "他人的中光束不會顯示畫面，也不會播放語音。自己的光束照常出現。")
                    : T("中を捨てたときのチュンビームを全員ぶん表示します。",
                        "Shows everyone's Chun Beam when they discard Red Dragon.",
                        "중을 버렸을 때의 춘 빔을 모든 사람 것까지 표시합니다.", "显示所有人打出中时的中光束。", "顯示所有人打出中時的中光束。");
            }
        }

        // ============================================================
        // 卓の強制リセット（スタッフ／インスタンスマスター）
        //
        // カウンター裏のスタッフパネルと同じことがここからもできる。
        // パネルは見えない壁の内側にあり、マスターはそこへ入れないため、
        // 「スタッフが一人もいないインスタンスで卓が詰まる」を抜けるのはこちらだけ。
        //
        // 二段構え（卓を選ぶ → 実行）。選択は15秒で自動的に失効する。
        // ============================================================
        public void ShowTableReset()
        {
            tableResetArmed_ = -1;
            tableResetStatus_ = "";
            RenderTableReset();
            BeginScreenTransition(screenTableReset);
        }

        // 卓ボタン。Udon は引数を取れないので卓の数だけ入口を並べる。
        public void ForceResetPick0() { ArmTableReset(0); }
        public void ForceResetPick1() { ArmTableReset(1); }
        public void ForceResetPick2() { ArmTableReset(2); }
        public void ForceResetPick3() { ArmTableReset(3); }
        public void ForceResetPick4() { ArmTableReset(4); }
        public void ForceResetPick5() { ArmTableReset(5); }
        public void ForceResetPick6() { ArmTableReset(6); }
        public void ForceResetPick7() { ArmTableReset(7); }

        private void ArmTableReset(int tableIndex)
        {
            if (matchmaking == null || !matchmaking.CanLocalPlayerForceResetTables()) { return; }
            if (tableIndex < 0 || tableIndex >= matchmaking.GetTableCount()) { return; }

            // マスター権限（スタッフ不在時の非常口）では、人が使っている卓は選ばせない。
            if (!matchmaking.CanLocalPlayerForceResetTableAt(tableIndex))
            {
                tableResetArmed_ = -1;
                tableResetStatus_ = TableResetDeniedMessage(tableIndex);
                RenderTableReset();
                return;
            }

            // 同じ卓をもう一度押したら選択解除。「選び直せない」は事故の元。
            if (tableResetArmed_ == tableIndex)
            {
                tableResetArmed_ = -1;
                tableResetStatus_ = T("選択を取り消しました。",
                                      "Selection cleared.",
                                      "선택을 취소했습니다.", "已取消选择。", "已取消選擇。");
                RenderTableReset();
                return;
            }

            tableResetArmed_ = tableIndex;
            tableResetArmedAtMs_ = Networking.GetServerTimeInMilliseconds();
            tableResetStatus_ = T(
                "卓 " + (tableIndex + 1) + " を強制リセットします。下の【実行】で確定します（15秒で取り消し）。",
                "Table " + (tableIndex + 1) + " will be force-reset. Press Execute below to confirm (cancels in 15s).",
                "탁 " + (tableIndex + 1) + "을(를) 강제 리셋합니다. 아래의 [실행]으로 확정합니다 (15초 후 자동 취소).", "将强制重置牌桌 " + (tableIndex + 1) + "。按下方的【执行】确定（15秒后自动取消）。", "將強制重置牌桌 " + (tableIndex + 1) + "。按下方的【執行】確定（15秒後自動取消）。");
            RenderTableReset();
        }

        public void ForceResetExecute()
        {
            if (matchmaking == null || !matchmaking.CanLocalPlayerForceResetTables()) { return; }

            if (tableResetArmed_ < 0)
            {
                tableResetStatus_ = T("先に、潰す卓を選んでください。",
                                      "Pick the table you want to reset first.",
                                      "먼저 리셋할 탁을 선택해 주세요.", "请先选择要重置的牌桌。", "請先選擇要重置的牌桌。");
                RenderTableReset();
                return;
            }

            // サーバー時刻は負値になり得るので、比較は必ず減算で行う（ラップアラウンド安全）。
            if (Networking.GetServerTimeInMilliseconds() - tableResetArmedAtMs_ > TABLE_RESET_ARM_TIMEOUT_MS)
            {
                tableResetArmed_ = -1;
                tableResetStatus_ = T("時間切れです。もう一度卓を選び直してください。",
                                      "Timed out. Pick the table again.",
                                      "시간이 초과되었습니다. 탁을 다시 선택해 주세요.", "已超时。请重新选择牌桌。", "已逾時。請重新選擇牌桌。");
                RenderTableReset();
                return;
            }

            int t = tableResetArmed_;
            // 選んでから実行までの間に対局が始まった卓は、ここでもう一度止める。
            if (!matchmaking.CanLocalPlayerForceResetTableAt(t))
            {
                tableResetArmed_ = -1;
                tableResetStatus_ = TableResetDeniedMessage(t);
                RenderTableReset();
                return;
            }
            matchmaking.RequestForceResetTable(t);

            tableResetArmed_ = -1;
            tableResetStatus_ = T(
                "卓 " + (t + 1) + " を強制リセットしました。数秒で空卓に戻ります。",
                "Table " + (t + 1) + " has been force-reset. It returns to an open table within a few seconds.",
                "탁 " + (t + 1) + "을(를) 강제 리셋했습니다. 몇 초 안에 빈 탁으로 돌아갑니다.", "已强制重置牌桌 " + (t + 1) + "。几秒内会恢复为空桌。", "已強制重置牌桌 " + (t + 1) + "。幾秒內會恢復為空桌。");
            RenderTableReset();
        }

        private void RenderTableReset()
        {
            if (tableResetTitle != null)
            {
                tableResetTitle.text = T("卓の強制リセット", "Force-reset a table", "탁 강제 리셋", "强制重置牌桌", "強制重置牌桌");
            }
            if (tableResetBackLabel != null)
            {
                tableResetBackLabel.text = T("もどる", "Back", "뒤로", "返回", "返回");
            }
            if (tableResetExecuteLabel != null)
            {
                tableResetExecuteLabel.text = T("実行", "Execute", "실행", "执行", "執行");
            }
            if (tableResetNote != null)
            {
                tableResetNote.text = T(
                    "詰まって動かなくなった卓を、素の空卓へ戻します。対局中の卓を選ぶとその対局は消えます。\n"
                    + "普通は放っておいても自動で戻ります（対局中の卓は応答が5分途絶えたら、10分空いたままの予約は自動解放）。\n"
                    + "それを待てないときだけ使ってください。",
                    "Returns a stuck table to an open table. If you pick a table with a game running, that game is lost.\n"
                    + "Normally you do not need this: a table whose owner has been silent for 5 minutes, or a reservation left empty for 10 minutes, is released automatically.\n"
                    + "Use this only when you cannot wait for that.",
                    "멈춰서 움직이지 않는 탁을 빈 탁으로 되돌립니다. 대국 중인 탁을 선택하면 그 대국은 사라집니다.\n" + "보통은 그대로 두어도 자동으로 돌아옵니다 (대국 중인 탁은 방장의 응답이 5분 끊기면, 10분 동안 빈 채로 남은 예약은 자동 해제).\n" + "기다릴 수 없을 때만 사용해 주세요.", "把卡住不动的牌桌恢复为空桌。选择正在对局的牌桌时，该对局会消失。\n" + "通常放着不管也会自动恢复（对局中的牌桌在主机5分钟无响应后、空置10分钟的预约会自动释放）。\n" + "请仅在等不及时使用。", "把卡住不動的牌桌恢復為空桌。選擇正在對局的牌桌時，該對局會消失。\n" + "通常放著不管也會自動恢復（對局中的牌桌在主機5分鐘無回應後、空置10分鐘的預約會自動釋放）。\n" + "請僅在等不及時使用。");
                if (matchmaking != null && !matchmaking.IsLocalFullForceResetter())
                {
                    tableResetNote.text += T(
                        "\n【マスター権限】対局中・着席中・部屋のある卓は選べません（スタッフのみ）。",
                        "\n[Instance master] Tables being played, being seated or holding a room are staff only.",
                        "\n[인스턴스 마스터] 대국 중·착석 중·방이 있는 탁은 선택할 수 없습니다 (스태프 전용).", "\n【房主权限】正在对局・有人就座・已有房间的牌桌无法选择（仅限工作人员）。", "\n【房主權限】正在對局・有人就座・已有房間的牌桌無法選擇（僅限工作人員）。");
                }
            }

            int now = Networking.GetServerTimeInMilliseconds();
            // 武装の時間切れは、描画のついでに見る（専用のTickを増やさない）。
            if (tableResetArmed_ >= 0 && now - tableResetArmedAtMs_ > TABLE_RESET_ARM_TIMEOUT_MS)
            {
                tableResetArmed_ = -1;
                tableResetStatus_ = T("時間切れです。もう一度卓を選び直してください。",
                                      "Timed out. Pick the table again.",
                                      "시간이 초과되었습니다. 탁을 다시 선택해 주세요.", "已超时。请重新选择牌桌。", "已逾時。請重新選擇牌桌。");
            }

            int tableCount = matchmaking != null ? matchmaking.GetTableCount() : 0;
            if (tableResetLabels != null)
            {
                for (int i = 0; i < tableResetLabels.Length; ++i)
                {
                    if (tableResetLabels[i] == null) { continue; }
                    if (i >= tableCount)
                    {
                        tableResetLabels[i].text = "";
                        continue;
                    }
                    tableResetLabels[i].text = T("卓 ", "Table ", "탁 ", "牌桌 ", "牌桌 ") + (i + 1) + "\n"
                        + DescribeTableForReset(i);
                }
            }
            if (tableResetImages != null)
            {
                for (int i = 0; i < tableResetImages.Length; ++i)
                {
                    if (tableResetImages[i] == null) { continue; }
                    tableResetImages[i].color = (i == tableResetArmed_) ? tabSelectedColor_ : tabNormalColor_;
                }
            }
            if (tableResetExecuteImage != null)
            {
                tableResetExecuteImage.color = (tableResetArmed_ >= 0) ? tabSelectedColor_ : tabNormalColor_;
            }

            if (tableResetStatus != null)
            {
                tableResetStatus.text = (tableResetStatus_ != null) ? tableResetStatus_ : "";
            }
        }

        // 卓ボタンの2行目。卓は寝ていて読めないので、
        // MatchmakingManager の同期状態だけから書けることを書く。
        private string DescribeTableForReset(int i)
        {
            if (matchmaking == null) { return ""; }
            if (matchmaking.IsForceResetPending(i)) { return T("リセット中", "resetting", "리셋 중", "重置中", "重置中"); }
            if (!matchmaking.IsTableIndexInUse(i)) { return T("空き", "open", "비어 있음", "空闲", "空閒"); }
            if (!matchmaking.CanLocalPlayerForceResetTableAt(i)) { return T("使用中（スタッフのみ）", "in use (staff only)", "사용 중 (스태프 전용)", "使用中（仅限工作人员）", "使用中（僅限工作人員）"); }
            return T("使用中", "in use", "사용 중", "使用中", "使用中");
        }

        private string TableResetDeniedMessage(int tableIndex)
        {
            return T(
                "卓 " + (tableIndex + 1) + " はいま使われています。マスター権限では進行中の卓はリセットできません。\n"
                + "止まった卓は、応答が5分途絶えると自動で空卓に戻ります。",
                "Table " + (tableIndex + 1) + " is in use. The instance master cannot reset a table that is being played.\n"
                + "A stuck table returns to open automatically after its owner has been silent for 5 minutes.",
                "탁 " + (tableIndex + 1) + "은(는) 지금 사용 중입니다. 인스턴스 마스터 권한으로는 진행 중인 탁을 리셋할 수 없습니다.\n" + "멈춘 탁은 방장의 응답이 5분 끊기면 자동으로 빈 탁으로 돌아갑니다.", "牌桌 " + (tableIndex + 1) + " 正在使用中。房主权限无法重置正在进行的牌桌。\n" + "卡住的牌桌在5分钟无响应后会自动恢复为空桌。", "牌桌 " + (tableIndex + 1) + " 正在使用中。房主權限無法重置正在進行的牌桌。\n" + "卡住的牌桌在5分鐘無回應後會自動恢復為空桌。");
        }

private void ShowWaiting()
{
    RenderWaiting();
    BeginScreenTransition(screenWaiting);
    EnsureWaitingFastTick();
}

        // ホームへ戻しつつ一時メッセージ（満員です等）を表示する。
        private void ShowHomeWithInfo(string message)
        {
            ShowHomeRaw();
            if (homeInfo != null)
            {
                homeInfo.text = message;
                homeInfoUntil_ = Time.time + 5f;
            }
        }

        // ============================================================
        // 対局中の案内 と 途中退出（ScreenInGame / ScreenAbandonConfirm）
        //
        //  ・対局中の卓に座ったままメインメニューへ来た人にだけ出す画面。
        //  ・「途中退出」を押すと二段階確認 → 席はCPU代打に引き継がれ、
        //    段位戦なら段位ポイント -100（天獣段位は天珠 -1）。
        //  ・フリー対局は減点しない。戦績は席がCPUへ移る時点で自分の記録経路から
        //    外れる（自分の席が entryCpu になると GetMySeat が -1 になり、
        //    結果保存もデイリーランキング加算も走らない）。
        // ============================================================

        // 途中退出の直後だけ案内画面への差し戻しを止める秒。
        // 申告 → 卓Owner が代打へ切替 → 席の申告が畳まれる、までに1〜2秒かかる。
        private float abandonSuppressUntil_ = 0f;
        private const float ABANDON_SUPPRESS_SECONDS = 10f;

        // 自分が対局中の卓に座っているか。判定材料は卓Owner が申告した同期席IDなので、
        // 卓が近接カリングで寝ていても正しく読める。
        private bool IsSeatedInOngoingGame()
        {
            return matchmaking != null && matchmaking.IsLocalPlayerSeatedInOngoingGame();
        }

        private void ShowInGame()
        {
            RenderInGame();
            BeginScreenTransition(screenInGame);
        }

        // 座っている卓がもう終局して結果表示中か（卓Owner の申告）。この間は途中退出させない。
        private bool IsSeatedGameFinished()
        {
            return matchmaking != null && matchmaking.IsLocalSeatedTableFinished();
        }

        private void RenderInGame()
        {
            bool finished = IsSeatedGameFinished();

            if (inGameText != null)
            {
                inGameText.text = finished
                    ? T("対局は終了しました。\n\n" +
                        "卓の結果画面で NEXT を押すか、しばらく待つと卓が空き、この画面も閉じます。",
                        "Your match has ended.\n\n" +
                        "Press NEXT on the result screen at the table, or wait a moment. " +
                        "The table will be freed and this screen will close.",
                        "대국이 종료되었습니다.\n\n" + "탁의 결과 화면에서 NEXT를 누르거나 잠시 기다리면 " + "탁이 비워지고 이 화면이 닫힙니다.", "对局已结束。\n\n" + "在牌桌的结果画面按 NEXT，或稍等片刻，" + "牌桌就会空出来，这个画面也会关闭。", "對局已結束。\n\n" + "在牌桌的結果畫面按 NEXT，或稍等片刻，" + "牌桌就會空出來，這個畫面也會關閉。")
                    : T("あなたは対局中です。\n\n" +
                        "途中退出をする場合は下のボタンを押してください。",
                        "You are in a match.\n\n" +
                        "To leave the match early, press the button below.",
                        "대국 중입니다.\n\n" + "중도 퇴장하려면 아래 버튼을 눌러 주세요.", "你正在对局中。\n\n" + "如需中途退出，请按下方的按钮。", "你正在對局中。\n\n" + "如需中途退出，請按下方的按鈕。");
            }

            if (btnAbandon != null) { btnAbandon.SetActive(!finished); }
        }

        // 「途中退出」ボタン → 確認画面へ。
        public void OnAbandonRequest()
        {
            if (!IsSeatedInOngoingGame())
            {
                ShowHomeRaw();
                return;
            }
            if (IsSeatedGameFinished())
            {
                ShowInGame();
                return;
            }

            RenderAbandonConfirm();
            BeginScreenTransition(screenAbandonConfirm);
        }

        private void RenderAbandonConfirm()
        {
            if (abandonConfirmText == null) { return; }

            string message = T("あなたは途中退出をします。よろしいですか？",
                               "You are about to leave this match. Are you sure?",
                               "중도 퇴장합니다. 괜찮으십니까?", "你将中途退出。确定吗？", "你將中途退出。確定嗎？");

            if (IsAbandonPenaltyTable())
            {
                bool tenju = statsStore != null && statsStore.IsTenjuRank();
                message += "\n" + (tenju
                    ? T("天珠が1つ減ります。", "You will lose 1 orb.", "천주가 1개 줄어듭니다.", "天珠将减少1个。", "天珠將減少1個。")
                    : T("段位ポイントが-100減ります。", "You will lose 100 rank points.", "단위 포인트가 100 줄어듭니다.", "段位分将减少100。", "段位分將減少100。"));
            }

            abandonConfirmText.text = message;
        }

        // 減点対象の卓か（段位戦のみ。フリー対局・手動卓は減点しない）。
        private bool IsAbandonPenaltyTable()
        {
            if (matchmaking == null) { return false; }
            return matchmaking.IsRankedTableIndex(matchmaking.GetLocalSeatedTable());
        }

        public void OnAbandonYes()
        {
            if (matchmaking == null) { ShowHomeRaw(); return; }

            // 確認画面を開いている間に終局した。対局は済んでいるので減点しない。
            if (IsSeatedGameFinished())
            {
                ShowInGame();
                return;
            }

            bool penalty = IsAbandonPenaltyTable();

            // 申告が通らない（席が既に無い等）ならペナルティも与えない。
            if (!matchmaking.RequestAbandonSeat())
            {
                ShowHomeWithInfo(T("途中退出できませんでした。もう一度お試しください。",
                                   "Could not leave the match. Please try again.",
                                   "중도 퇴장하지 못했습니다. 다시 시도해 주세요.", "无法中途退出。请再试一次。", "無法中途退出。請再試一次。"));
                return;
            }

            if (penalty && statsStore != null) { statsStore.ApplyAbandonPenalty(); }

            abandonSuppressUntil_ = Time.time + ABANDON_SUPPRESS_SECONDS;

            ShowHomeWithInfo(penalty
                ? T("途中退出しました。段位が減点されました。",
                    "You left the match. Your rank was penalized.",
                    "중도 퇴장했습니다. 단위가 감점되었습니다.", "已中途退出。段位已被扣分。", "已中途退出。段位已被扣分。")
                : T("途中退出しました。", "You left the match.", "중도 퇴장했습니다.", "已中途退出。", "已中途退出。"));
        }

        public void OnAbandonNo()
        {
            if (IsSeatedInOngoingGame()) { ShowInGame(); }
            else { ShowHomeRaw(); }
        }

        // 1秒Tickから呼ぶ。対局中に入った／終わったのを画面へ反映する。
        private void UpdateInGameScreen()
        {
            if (screenInGame == null) { return; }

            bool seated = IsSeatedInOngoingGame() && Time.time >= abandonSuppressUntil_;

            if (seated)
            {
                // ホームを見ている人だけ引き上げる。プロフィール等を開いている間は邪魔しない。
                if (screenHome != null && screenHome.activeSelf) { ShowInGame(); }
                // 開いたままの案内も描き直す（対局中 → 終局で文言と途中退出ボタンが切り替わる）。
                else if (screenInGame.activeSelf) { RenderInGame(); }
                // 確認画面を開いている間に終局したら、案内へ戻す（減点の入口を閉じる）。
                else if (screenAbandonConfirm != null && screenAbandonConfirm.activeSelf
                         && IsSeatedGameFinished()) { ShowInGame(); }
                return;
            }

            // 対局が終わった／席がCPUへ移った → 案内画面と確認画面は畳む。
            if (screenInGame.activeSelf
                || (screenAbandonConfirm != null && screenAbandonConfirm.activeSelf))
            {
                ShowHomeRaw();
            }
        }

        // ============================================================
        // 段位戦マッチング（実装③。BtnRanked4Han / BtnRanked4Ton /
        //                    BtnRanked3Han / BtnRanked3Ton / BtnCancelWait から呼ぶ）
        //
        // 2026-08-18：半荘固定をやめ、東風戦を追加した（人数×局数の4列）。
        // 待機列を混ぜないので、押したボタンの局数がそのまま卓のルールになる。
        // ============================================================

        public void OnRankedYonmaHanchan() { StartRankedWait(MatchmakingManager.STATUS_WAIT_YONMA); }
        public void OnRankedSanmaHanchan() { StartRankedWait(MatchmakingManager.STATUS_WAIT_SANMA); }
        public void OnRankedYonmaTonpu() { StartRankedWait(MatchmakingManager.STATUS_WAIT_YONMA_TONPU); }
        public void OnRankedSanmaTonpu() { StartRankedWait(MatchmakingManager.STATUS_WAIT_SANMA_TONPU); }

        // 旧配線（BtnRanked4 / BtnRanked3）が残っているシーン向けの互換エイリアス。
        // Wire Main Menu を再実行すれば上の4メソッドへ張り替わる。
        public void OnRankedYonma() { OnRankedYonmaHanchan(); }
        public void OnRankedSanma() { OnRankedSanmaHanchan(); }

        private void StartRankedWait(int status)
        {
            if (matchmaking == null) { return; }
            if (IsMatchmakingBusy()) { return; } // 二重押し・成立中の再要求を無視
            if (BlockedBySeatedGame()) { return; }

            // キャラ確定タイミング＝待機列に入った時点でロック（仕様A）。
            // 選択自体は PlayerData に保存済みなので、ここでは列に入るだけでよい。
            matchmaking.RequestSetLocalStatus(status);
            EnsureWaitingFastTick();
            // Owner の返事が届くまでの猶予。この間は待機画面をホームへ落とさない。
            rankedWaitHoldUntil_ = Time.time + 10f;
            rankedWaitSeen_ = false;
            rankedWaitLastStatus_ = 0;
            waitReopenSuppressUntil_ = 0f;
            ShowWaiting();
        }

        // キャンセル（待機中のみ有効。成立後はボタン自体を隠す＝RenderWaiting）。
        public void OnCancelWait()
        {
            if (matchmaking != null && matchmaking.GetLocalMatchTable() < 0)
            {
                matchmaking.SetIdle();
            }
            // 「降りる」要求も往復する。返事が来るまでは待機ステータスが残るので、
            // 自己修復（ReopenWaitingIfQueued）に待機画面へ引き戻されないよう押さえる。
            rankedWaitHoldUntil_ = 0f;
            waitReopenSuppressUntil_ = Time.time + 6f;
            ShowHome();
        }

        // 待機中・成立中・フリー部屋在室中か（キャラ選択ロック・二重押し防止に使う）。
        // フリー部屋在室もロック対象（仕様：待機中・部屋入室中はキャラ選択画面ロック）。
        // 卓に着席している間、マッチング系の要求は Manager Owner に弾かれる。
        // ところが呼び出し側は弾かれたことを知らずに画面だけ進めてしまう
        // （段位戦＝待機画面が一瞬出て Tick でホームへ／フリー入室＝5秒後に
        // 「満員です」という誤った理由が出る）。押した時点で止めて理由を出す。
        private bool BlockedBySeatedGame()
        {
            if (matchmaking == null) { return false; }
            if (!matchmaking.IsLocalPlayerSeatedInOngoingGame()) { return false; }
            ShowHomeWithInfo(T("卓に着席中です。席を立ってからもう一度どうぞ",
                               "You are seated at a table. Please stand up and try again.",
                               "탁에 착석 중입니다. 자리에서 일어난 뒤 다시 시도해 주세요.", "你正坐在牌桌上。请离开座位后再试。", "你正坐在牌桌上。請離開座位後再試。"));
            return true;
        }

        private bool IsMatchmakingBusy()
        {
            if (matchmaking == null) { return false; }
            if (matchmaking.GetLocalMatchTable() >= 0) { return true; }
            if (matchmaking.IsLocalFreeMember()) { return true; }
            return matchmaking.GetLocalStatus() != MatchmakingManager.STATUS_IDLE;
        }

        // ------------------------------------------------------------
        // 待機画面の高速Tick（待機列に並んでいる間＋成立中だけ回る）
        //
        // マッチングのカウントダウン音は matchReadyAtMs（同期済みサーバー時刻）から
        // 絶対時刻で予約して鳴らしている。一方この画面の数字は1秒Tickで描いていたので、
        // 「3・2・1」の音と画面の数字が最大1秒ズレていた（音が先に進み、画面が後から動く）。
        // 数字が変わる瞬間＝音が鳴る瞬間なので、0.1秒ごとに見て変化したときだけ描き直す。
        //
        // 描き直しは Canvas の再構築を伴うので、**値が変わったときだけ**呼ぶこと
        // （毎回 RenderWaiting すると 10Hz で Canvas を作り直すことになる）。
        // 成立後（カウントダウン中）は音と数字を合わせたいので細かく、
        // 並んで待っているだけの間は成立の瞬間を拾えれば十分なので粗く回す。
        private const float WAITING_FAST_TICK_SECONDS = 0.1f;
        private const float WAITING_QUEUE_TICK_SECONDS = 0.25f;
        private bool waitingFastTickActive_ = false;
        private int waitingFastShownTable_ = -999;
        private int waitingFastShownSec_ = -999;

        // 高速Tickを回すべき状態か。
        //  ・待機画面を開いている（数字を描き直す必要がある）
        //  ・成立済み（音が絶対時刻で鳴り始めている＝画面を待たせない）
        //  ・待機列に並んでいる（ホームを見ていても成立を素早く拾う）
        private bool WaitingFastTickNeeded()
        {
            if (matchmaking == null) { return false; }
            if (screenWaiting != null && screenWaiting.activeSelf) { return true; }
            if (matchmaking.GetLocalMatchTable() >= 0) { return true; }
            return matchmaking.IsRankedWaitStatus(matchmaking.GetLocalStatus());
        }

        private void EnsureWaitingFastTick()
        {
            if (waitingFastTickActive_) { return; }
            if (!WaitingFastTickNeeded()) { return; }
            waitingFastTickActive_ = true;
            // 直前の描画内容は分からないので、初回は必ず描き直させる。
            waitingFastShownTable_ = -999;
            waitingFastShownSec_ = -999;
            SendCustomEventDelayedSeconds(nameof(_WaitingFastTick), WAITING_FAST_TICK_SECONDS);
        }

        public void _WaitingFastTick()
        {
            if (!WaitingFastTickNeeded())
            {
                waitingFastTickActive_ = false;
                return;
            }

            int table = matchmaking.GetLocalMatchTable();

            if (screenWaiting != null && screenWaiting.activeSelf)
            {
                int sec = table >= 0 ? matchmaking.GetLocalMatchPrepareRemainSeconds() : -1;
                if (table != waitingFastShownTable_ || sec != waitingFastShownSec_)
                {
                    waitingFastShownTable_ = table;
                    waitingFastShownSec_ = sec;
                    RenderWaiting();
                }
            }
            else
            {
                // ホームを見ている間に成立した場合。1秒Tick待ちだと、音が「3・2・1」と
                // 進んでから画面がカウントダウンへ切り替わる（＝音だけ先に行く）。
                ReopenWaitingIfQueued();
            }

            SendCustomEventDelayedSeconds(nameof(_WaitingFastTick),
                table >= 0 ? WAITING_FAST_TICK_SECONDS : WAITING_QUEUE_TICK_SECONDS);
        }

        // メニューの定期更新（1秒Tick。表示中の画面だけ描画する軽量処理）。
        public void _WaitingTick()
        {
            if (screenWaiting != null && screenWaiting.activeSelf)
            {
                RenderWaiting();
            }
            else
            {
                ReopenWaitingIfQueued();
            }
            UpdateFreeUi();
            UpdateInGameScreen();
            UpdateRankedButtons();
            EnsureWaitingFastTick();
            SendCustomEventDelayedSeconds(nameof(_WaitingTick), 1.0f);
        }

        // 待機列に並んでいる／卓が決まっているのにホームを見ているなら待機画面へ戻す。
        // 要求の往復ズレや画面の取りこぼしで「自分が待機中だと分からない」状態へ
        // 落ちるのを1秒Tickで自己修復する。ホーム以外を見ているときは邪魔しない。
        private void ReopenWaitingIfQueued()
        {
            if (matchmaking == null || screenWaiting == null) { return; }
            if (screenHome == null || !screenHome.activeSelf) { return; }
            if (Time.time < waitReopenSuppressUntil_) { return; }

            // キャンセル直後は「降りる」要求の往復中で待機ステータスがまだ残る。戻さない。
            if (matchmaking.IsLocalStatusRequestPending()
                && !matchmaking.IsRankedWaitStatus(matchmaking.GetLocalDesiredStatus()))
            {
                return;
            }

            if (matchmaking.GetLocalMatchTable() < 0
                && !matchmaking.IsRankedWaitStatus(matchmaking.GetLocalStatus()))
            {
                return;
            }

            ShowWaiting();
        }

        private void RenderWaiting()
        {
            if (matchmaking == null)
            {
                ShowHome();
                return;
            }

            int matchTable = matchmaking.GetLocalMatchTable();
            if (matchTable >= 0)
            {
                rankedWaitSeen_ = true;
                RenderWaitingMatched(matchTable);
                return;
            }

            int status = matchmaking.GetLocalStatus();
            if (matchmaking.IsRankedWaitStatus(status))
            {
                rankedWaitSeen_ = true;
                rankedWaitLastStatus_ = status;
                RenderWaitingQueued(status);
                return;
            }

            // 列に入る要求は Manager Owner を往復する。返事が届く前の1秒Tickで
            // ホームへ落とすと、頭上は「待機中」なのにメニューはホームのままになり、
            // 自分が並んでいることが分からなくなる。往復中は待機画面のまま待つ。
            int desired = matchmaking.GetLocalDesiredStatus();
            if (matchmaking.IsRankedWaitStatus(desired)
                && (matchmaking.IsLocalStatusRequestPending()
                    || Time.time < rankedWaitHoldUntil_))
            {
                RenderWaitingQueued(desired);
                return;
            }

            // 待機でも成立でもない＝完了（着席済みで対局へ）またはタイムアウト脱落。ホームへ戻す。
            // 10秒待っても一度も列に入れなかった＝Owner に弾かれたか要求が落ちた。
            // 黙ってホームへ戻すと原因不明の「押しても何も起きない」になるので理由を出す。
            bool joinFailed = !rankedWaitSeen_ && rankedWaitHoldUntil_ > 0f;
            // 並べていたのに列から消えた＋その列が閉じている＝スタッフが停止した。
            // 押し間違いでも通信落ちでもないので、そう伝える（成立して卓へ行った人は
            // 上の matchTable 分岐で抜けているのでここへは来ない）。
            bool columnClosed = rankedWaitSeen_
                && matchmaking.IsRankedWaitStatus(rankedWaitLastStatus_)
                && !matchmaking.IsRankedColumnEnabled(rankedWaitLastStatus_);
            rankedWaitHoldUntil_ = 0f;
            rankedWaitLastStatus_ = 0;
            if (columnClosed)
            {
                ShowHomeWithInfo(T("この待機列は運営が停止しました。別の待機列をお試しください",
                                   "This queue was closed by staff. Please try another queue.",
                                   "이 대기열은 운영자가 정지했습니다. 다른 대기열을 이용해 주세요.", "此队列已被运营方停止。请尝试其他队列。", "此佇列已被營運方停止。請嘗試其他佇列。"));
                return;
            }
            if (joinFailed)
            {
                ShowHomeWithInfo(T("待機列に入れませんでした。もう一度お試しください",
                                   "Could not join the queue. Please try again.",
                                   "대기열에 들어가지 못했습니다. 다시 시도해 주세요.", "无法加入队列。请再试一次。", "無法加入佇列。請再試一次。"));
                return;
            }
            ShowHome();
        }

        // 待機中（列に並んでいる）。何人揃ったかと、一緒に待っている顔ぶれを見せる。
        private void RenderWaitingQueued(int status)
        {
            int mode = matchmaking.GetWaitModeOf(status);
            bool tonpu = matchmaking.GetWaitRuleLengthOf(status) == MatchmakingManager.RULE_LEN_TONPU;
            int waiting = matchmaking.GetWaitingCount(status);

            if (waitingTitle != null)
            {
                waitingTitle.text = T("対戦相手を待っています...", "Waiting for opponents...", "대전 상대를 기다리는 중...", "正在等待对手...", "正在等待對手...");
            }
            if (waitingInfo != null)
            {
                string info = T(
                    (mode == 3 ? "三麻 " : "四麻 ")
                        + (tonpu ? "東風戦" : "半荘戦")
                        + "（" + waiting + " / " + mode + " 人）",
                    (mode == 3 ? "3-Player " : "4-Player ")
                        + (tonpu ? "East Only" : "Half Game")
                        + " (" + waiting + " / " + mode + ")",
                    (mode == 3 ? "3인 " : "4인 ") + (tonpu ? "동풍전" : "반장전") + " (" + waiting + " / " + mode + "명)", (mode == 3 ? "三麻 " : "四麻 ") + (tonpu ? "东风战" : "半庄战") + "（" + waiting + " / " + mode + " 人）", (mode == 3 ? "三麻 " : "四麻 ") + (tonpu ? "東風戰" : "半莊戰") + "（" + waiting + " / " + mode + " 人）");
                // 列には卓の定員より多く並べる（全卓使用中など）。行に出ない分を数で補う。
                if (waiting > mode)
                {
                    info += T("\nほか " + (waiting - mode) + " 人が待機中",
                              "\n+" + (waiting - mode) + " more in the queue",
                              "\n그 외 " + (waiting - mode) + "명 대기 중", "\n另有 " + (waiting - mode) + " 人等待中", "\n另有 " + (waiting - mode) + " 人等待中");
                }
                if (matchmaking.IsWaitBlockedByFullTables(status))
                {
                    info += T("\n全卓使用中のため、空き卓を待っています",
                              "\nAll tables are in use. Waiting for one to free up.",
                              "\n모든 탁이 사용 중이라 빈 탁을 기다리고 있습니다", "\n所有牌桌都在使用中，正在等待空桌", "\n所有牌桌都在使用中，正在等待空桌");
                }
                waitingInfo.text = info;
            }

            RenderWaitingMembers(mode, -1, status);

            if (btnCancelWait != null) { btnCancelWait.SetActive(true); }
        }

        // 成立後。まず「プレイヤーが決まりました！」と顔ぶれを見せ、数秒数えてから卓へ案内する。
        // カウント中は足元の矢印も光柱も出ない（MatchmakingManager 側で押さえている）。
        private void RenderWaitingMatched(int matchTable)
        {
            int prepSec = matchmaking.GetLocalMatchPrepareRemainSeconds();
            bool countdown = prepSec > 0;

            if (waitingTitle != null)
            {
                waitingTitle.text = countdown
                    ? T("プレイヤーが決まりました！", "Players matched!", "플레이어가 모였습니다!", "玩家已匹配！", "玩家已配對！")
                    : T("対局卓が決まりました！", "Your table is ready!", "대국 탁이 정해졌습니다!", "对局牌桌已确定！", "對局牌桌已確定！");
            }

            if (waitingInfo != null)
            {
                if (countdown)
                {
                    waitingInfo.text = T(prepSec + " 秒で開始します！\n" + (matchTable + 1) + "番卓へご案内中です...",
                                         "Starting in " + prepSec + "...\nGuiding you to table " + (matchTable + 1) + "...",
                                         prepSec + "초 후 시작합니다!\n" + (matchTable + 1) + "번 탁으로 안내 중...", prepSec + " 秒后开始！\n正在引导至 " + (matchTable + 1) + " 号桌...", prepSec + " 秒後開始！\n正在引導至 " + (matchTable + 1) + " 號桌...");
                }
                else if (matchmaking.IsLocalMatchWaitingForTableSync())
                {
                    // 3秒を過ぎても卓Ownerの確認が届くまでは座れない。理由を出しておかないと
                    // 「卓に着いたのに ENTRY が出ない」がバグに見える。
                    waitingInfo.text = T(
                        "卓のプレイヤー・同期状態を確認しています…　そのままお待ちください",
                        "Checking the table's players and sync state… please wait.",
                        "탁의 플레이어와 동기화 상태를 확인하고 있습니다... 잠시만 기다려 주세요.", "正在确认牌桌的玩家与同步状态…　请稍候", "正在確認牌桌的玩家與同步狀態…　請稍候");
                }
                else
                {
                    var sec = matchmaking.GetLocalMatchRemainSeconds().ToString();
                    waitingInfo.text = T(
                        "足元の矢印をたどって、光の柱が立っている卓に着席してください（残り " + sec + " 秒）",
                        "Follow the arrows and sit at the table marked by the pillar of light ("
                            + sec + "s left)",
                        "발밑의 화살표를 따라가서 빛 기둥이 서 있는 탁에 앉아 주세요 (남은 시간 " + sec + "초)", "请沿着脚下的箭头，坐到光柱所在的牌桌（剩余 " + sec + " 秒）", "請沿著腳下的箭頭，坐到光柱所在的牌桌（剩餘 " + sec + " 秒）");
                }
            }

            if (!countdown && waitingInfo != null)
            {
                waitingInfo.text = T("<size=150%>" + (matchTable + 1) + "</size>番卓へお願いします！\n",
                    "Please go to table <size=150%>" + (matchTable + 1) + "</size>!\n",
                    "<size=150%>" + (matchTable + 1) + "</size>번 탁으로 가 주세요!\n", "请前往 <size=150%>" + (matchTable + 1) + "</size> 号桌！\n", "請前往 <size=150%>" + (matchTable + 1) + "</size> 號桌！\n") + waitingInfo.text;
            }

            RenderWaitingMembers(matchmaking.GetMatchModeOf(matchTable), matchTable,
                                 MatchmakingManager.STATUS_IDLE);

            // 成立後はキャンセル不可（仕様B）。
            if (btnCancelWait != null) { btnCancelWait.SetActive(false); }
        }

        // 顔ぶれの行。matchTable >= 0 なら成立メンバー、未満なら待機列の先頭から並べる。
        private void RenderWaitingMembers(int mode, int matchTable, int waitStatus)
        {
            if (waitingMemberList != null) { waitingMemberList.SetActive(mode > 0); }
            if (waitingMemberTexts == null || mode <= 0) { return; }

            var lp = Networking.LocalPlayer;
            int myId = (lp != null && Utilities.IsValid(lp)) ? lp.playerId : 0;

            // 成立済みマッチの空きスロットは「募集中」とは限らない。フリー対局で CPU を
            // 追加して開始した場合、matchPlayerIds には**人間だけが前詰めで**入り、
            // CPU席は 0 のまま残る。人数ぶんは CPU として表示する（部屋画面と同じ文言）。
            int humanCount = 0;
            int cpuCount = 0;
            bool cpuStrong = false;
            if (matchTable >= 0)
            {
                for (int k = 0; k < mode; ++k)
                {
                    if (matchmaking.GetMatchPlayerIdAt(matchTable, k) != 0) { ++humanCount; }
                }
                cpuCount = matchmaking.GetTableCpuCount(matchTable);
                // 強さは卓へ焼かれたルールから引く。部屋の設定値は成立時に消えている。
                cpuStrong = matchmaking.GetTableCpuStrongAt(matchTable);
            }

            for (int i = 0; i < waitingMemberTexts.Length; ++i)
            {
                var row = waitingMemberTexts[i];
                if (row == null) { continue; }

                if (i >= mode)
                {
                    row.text = "－"; // 三麻の4行目
                    continue;
                }

                int id = (matchTable >= 0)
                    ? matchmaking.GetMatchPlayerIdAt(matchTable, i)
                    : matchmaking.GetWaitingPlayerIdAt(waitStatus, i);
                if (id == 0)
                {
                    row.text = (i < humanCount + cpuCount)
                        ? (cpuStrong ? T("CPU(強い)", "CPU (Strong)", "CPU(강함)", "CPU(强)", "CPU(強)")
                                     : T("CPU(簡単)", "CPU (Easy)", "CPU(쉬움)", "CPU(简单)", "CPU(簡單)"))
                        : T("（募集中...）", "(Waiting...)", "(모집 중...)", "（招募中...）", "（招募中...）");
                    continue;
                }

                var p = VRCPlayerApi.GetPlayerById(id);
                string nm = (p != null && Utilities.IsValid(p)) ? p.displayName : "???";
                row.text = (id == myId) ? nm + T("（あなた）", " (You)", " (나)", "（你）", "（你）") : nm;
            }
        }

        // ============================================================
        // フリー対局戦（実装④。BtnFree ほか各ボタンから呼ぶ）
        // ============================================================

        public void OnFreeButton()
        {
            if (matchmaking == null) { return; }

            // 在室中なら自分の部屋へ戻るだけ（誤って二重部屋を作らない）。
            if (matchmaking.IsLocalFreeMember())
            {
                if (matchmaking.GetFreeState() == MatchmakingManager.FREE_SETUP
                    && matchmaking.IsLocalFreeHost())
                {
                    ShowFreeSetup();
                }
                else
                {
                    ShowFreeRoom();
                }
                return;
            }

            if (IsMatchmakingBusy()) { return; } // 段位戦の待機/成立中は不可
            if (BlockedBySeatedGame()) { return; }

            // 部屋＝卓。まず卓を選ばせる。
            // 一覧が用意されていない（Build Main Menu が古い）ときだけ、
            // 卓おまかせで従来どおり設定画面へ入る。
            if (screenFreeList == null)
            {
                matchmaking.RequestOpenFreeSetup();
                ShowFreeSetup();
                return;
            }
            ShowFreeList();
        }

        // --- 卓（部屋）一覧 ---

        // 卓一覧を開く（理由なし＝ホームや「いいえ」からの通常の入場）。
        private void ShowFreeList()
        {
            ShowFreeListWithMessage("");
        }

        // 卓一覧へ戻す（戻した理由つき）。
        //
        // 【注意】freeListMessage_ へ代入してから ShowFreeList() を呼んではいけない。
        // あちらは入口で freeListMessage_ を空にしてから描画するので、理由が黙って消える。
        // 2026-09-14 まで「満員です」「入室できませんでした」「フリー対局の設定を終了しました」の
        // 3つが実際に消えており、ユーザーには理由なしで一覧へ飛ばされるように見えていた。
        // 理由を出したいときは必ずこちらを通す（ShowHomeWithInfo と同じ作法）。
        private void ShowFreeListWithMessage(string message)
        {
            freeListTable_ = -1;
            freeListMessage_ = message;
            RenderFreeList();
            BeginScreenTransition(screenFreeList);
        }

        public void ShowFreeListScreen() { ShowFreeList(); }

        // 卓カード。Udon は引数を取れないので卓の数だけ入口を並べる。
        public void FreeListPick0() { PickFreeListTable(0); }
        public void FreeListPick1() { PickFreeListTable(1); }
        public void FreeListPick2() { PickFreeListTable(2); }
        public void FreeListPick3() { PickFreeListTable(3); }
        public void FreeListPick4() { PickFreeListTable(4); }
        public void FreeListPick5() { PickFreeListTable(5); }
        public void FreeListPick6() { PickFreeListTable(6); }
        public void FreeListPick7() { PickFreeListTable(7); }

        private void PickFreeListTable(int t)
        {
            if (matchmaking == null) { return; }
            if (t < 0 || t >= matchmaking.GetTableCount()) { return; }
            if (IsMatchmakingBusy() || BlockedBySeatedGame()) { return; }
            if (matchmaking.IsLocalFreeMember()) { return; }

            int st = matchmaking.GetFreeStateAt(t);
            if (st == MatchmakingManager.FREE_NONE)
            {
                // 前回この卓で打った面子なら、設定画面を飛ばして募集中から建て直す。
                if (matchmaking.CanLocalPlayerRematchFreeRoomAt(t))
                {
                    freeListTable_ = t;
                    matchmaking.RequestFreeRematchAt(t);
                    ShowFreeRoom();
                    return;
                }
                if (!matchmaking.CanOpenFreeRoomAt(t))
                {
                    freeListMessage_ = T("この卓はいま使われています。", "That table is in use right now.", "이 탁은 지금 사용 중입니다.", "这张牌桌正在使用中。", "這張牌桌正在使用中。");
                    RenderFreeList();
                    return;
                }
                // 競合はOwner検証で先着1名。負けた場合はTickの整合性チェックで戻される。
                freeListTable_ = t;
                matchmaking.RequestOpenFreeSetupAt(t);
                ShowFreeSetup();
                return;
            }

            if (st == MatchmakingManager.FREE_RECRUIT && !matchmaking.IsFreeRoomFullAt(t))
            {
                freeListTable_ = t;
                ShowFreeConfirm();
                return;
            }

            freeListMessage_ = (st == MatchmakingManager.FREE_SETUP)
                ? T("この卓はいま設定中です。", "That table is being set up.", "이 탁은 지금 설정 중입니다.", "这张牌桌正在设置中。", "這張牌桌正在設定中。")
                : T("この部屋は満員です。", "That room is full.", "이 방은 만원입니다.", "这个房间已满。", "這個房間已滿。");
            RenderFreeList();
        }

        private void RenderFreeList()
        {
            if (matchmaking == null) { return; }

            if (freeListTitle != null)
            {
                freeListTitle.text = T("卓をえらぶ", "Choose a table", "탁 선택", "选择牌桌", "選擇牌桌");
            }
            if (freeListBackLabel != null)
            {
                freeListBackLabel.text = T("もどる", "Back", "뒤로", "返回", "返回");
            }
            if (freeListNote != null)
            {
                freeListNote.text = (freeListMessage_ != null && freeListMessage_.Length > 0)
                    ? freeListMessage_
                    : T("空いている卓を選ぶと部屋を作れます。募集中の卓を選ぶとその部屋に入ります。",
                        "Pick an open table to start a room, or a recruiting table to join one.",
                        "빈 탁을 선택하면 방을 만들 수 있습니다. 모집 중인 탁을 선택하면 그 방에 들어갑니다.", "选择空桌即可创建房间。选择招募中的牌桌即可加入该房间。", "選擇空桌即可建立房間。選擇招募中的牌桌即可加入該房間。");
            }

            int count = matchmaking.GetTableCount();
            if (freeListNames == null) { return; }

            for (int i = 0; i < freeListNames.Length; ++i)
            {
                bool exists = i < count;
                if (freeListNames[i] != null)
                {
                    freeListNames[i].text = exists ? (T("卓 ", "Table ", "탁 ", "牌桌 ", "牌桌 ") + (i + 1)) : "";
                }

                string info = "";
                bool usable = false;
                if (exists)
                {
                    int st = matchmaking.GetFreeStateAt(i);
                    if (st == MatchmakingManager.FREE_RECRUIT)
                    {
                        int members = matchmaking.GetFreeMemberCountAt(i)
                            + matchmaking.GetFreeCpuCountAt(i);
                        int mode = matchmaking.GetFreeModeAt(i);
                        string host = matchmaking.GetFreeHostNameAt(i);
                        bool full = matchmaking.IsFreeRoomFullAt(i);
                        info = T("募集中  " + members + "/" + mode + "\n" + host,
                                 "Recruiting  " + members + "/" + mode + "\n" + host,
                                 "모집 중  " + members + "/" + mode + "\n" + host, "招募中  " + members + "/" + mode + "\n" + host, "招募中  " + members + "/" + mode + "\n" + host);
                        if (full) { info = T("満員  ", "Full  ", "만원  ", "已满  ", "已滿  ") + members + "/" + mode; }
                        usable = !full;
                    }
                    else if (st == MatchmakingManager.FREE_SETUP)
                    {
                        info = T("設定中", "Being set up", "설정 중", "设置中", "設定中");
                    }
                    else if (matchmaking.CanLocalPlayerRematchFreeRoomAt(i))
                    {
                        info = T("空き\nもう1局（前回の設定）", "Open\nRematch (same rules)", "비어 있음\n한 판 더 (이전 설정)", "空闲\n再来一局（沿用上次设置）", "空閒\n再來一局（沿用上次設定）");
                        usable = true;
                    }
                    else if (matchmaking.CanOpenFreeRoomAt(i))
                    {
                        info = T("空き", "Open", "비어 있음", "空闲", "空閒");
                        usable = true;
                    }
                    else
                    {
                        info = T("使用中", "In use", "사용 중", "使用中", "使用中");
                    }
                }

                if (freeListInfos != null && i < freeListInfos.Length && freeListInfos[i] != null)
                {
                    freeListInfos[i].text = info;
                }
                if (freeListImages != null && i < freeListImages.Length && freeListImages[i] != null)
                {
                    freeListImages[i].color = usable ? tabSelectedColor_ : tabNormalColor_;
                }
            }
        }


        // --- 設定画面（代表のみ） ---

        public void OnFreeToggleMode()
        {
            if (matchmaking == null) { return; }
            matchmaking.FreeToggleMode();
            RenderFreeSetup();
        }

        public void OnFreeToggleAbility()
        {
            if (matchmaking == null) { return; }
            matchmaking.FreeToggleAbility();
            RenderFreeSetup();
        }

        public void OnFreeCycleLength()
        {
            if (matchmaking == null) { return; }
            matchmaking.FreeCycleLength();
            RenderFreeSetup();
        }

        public void OnFreeCycleTimer()
        {
            if (matchmaking == null) { return; }
            matchmaking.FreeCycleTurnTimer();
            RenderFreeSetup();
        }

        public void OnFreeCycleRedDora()
        {
            if (matchmaking == null) { return; }
            matchmaking.FreeCycleRedDora();
            RenderFreeSetup();
        }

        // CPUの強さ（簡単／強い）。卓のCPUは全員この強さで打つ。
        public void OnFreeCycleCpuLevel()
        {
            if (matchmaking == null) { return; }
            matchmaking.FreeCycleCpuLevel();
            RenderFreeSetup();
        }

        public void OnFreeToggleAotenjou()
        {
            if (matchmaking == null) { return; }
            matchmaking.FreeToggleAotenjou();
            RenderFreeSetup();
        }

        public void OnFreeToggleLocalYaku()
        {
            if (matchmaking == null) { return; }
            matchmaking.FreeToggleLocalYaku();
            RenderFreeSetup();
        }

        public void OnFreeToggleOpenReach()
        {
            if (matchmaking == null) { return; }
            matchmaking.FreeToggleOpenReach();
            RenderFreeSetup();
        }

        public void OnFreeToggleTsumoLoss()
        {
            if (matchmaking == null) { return; }
            matchmaking.FreeToggleTsumoLoss();
            RenderFreeSetup();
        }

        public void OnFreeToggleBust()
        {
            if (matchmaking == null) { return; }
            matchmaking.FreeToggleBust();
            RenderFreeSetup();
        }

        public void OnFreeConfirmSetup()
        {
            if (matchmaking == null) { return; }
            matchmaking.FreeConfirmSetup();
            ShowFreeRoom();
        }

        // --- 部屋画面 ---

        public void OnFreeCpuAdd()
        {
            if (matchmaking == null) { return; }
            matchmaking.FreeAddCpu();
            RenderFreeRoom();
        }

        public void OnFreeCpuRemove()
        {
            if (matchmaking == null) { return; }
            matchmaking.FreeRemoveCpu();
            RenderFreeRoom();
        }

        public void OnFreeStart()
        {
            if (matchmaking == null) { return; }
            matchmaking.FreeStart();
            // 成立したらTickが待機画面（卓誘導）へ切り替える。
            // 部屋＝卓になったので「満卓で開始できない」は起きない（卓は部屋を開いた時点で確保済み）。
            RenderFreeRoom();
        }

        // ×ボタン（代表＝キャンセル/解散、参加者＝退出）。設定画面のBtnBackも兼用。
        public void OnFreeClose()
        {
            if (matchmaking != null)
            {
                if (matchmaking.IsLocalFreeHost()) { matchmaking.FreeCancelOrDisband(); }
                else { matchmaking.FreeLeaveRoom(); }
            }
            ShowHome();
        }

        // --- 入室確認画面 ---

        public void OnFreeJoinYes()
        {
            if (matchmaking == null) { ShowHome(); return; }
            if (BlockedBySeatedGame()) { return; }
            matchmaking.RequestJoinFreeRoomAt(freeListTable_);
            // 成否は同期反映後にTickで確認（勝ち＝部屋画面／負け＝満員です→一覧へ）。
            freeJoinPending_ = true;
            freeJoinPendingUntil_ = Time.time + 5f;
        }


        public void OnFreeJoinNo()
        {
            // 「いいえ」＝卓一覧へ戻る。他の卓を選び直せるので、
            // ホームまで戻していた頃の5秒カウントダウンはここでは掛けない。
            freeListTable_ = -1;
            if (screenFreeList != null) { ShowFreeList(); }
            else { ShowHome(); }
        }


        // --- フリー対局UIの定期更新（Tickから毎秒） ---

        private void UpdateFreeUi()
        {
            // ホームの一時メッセージの消去。
            if (homeInfo != null && homeInfo.text.Length > 0 && Time.time > homeInfoUntil_)
            {
                homeInfo.text = "";
            }

            if (matchmaking == null) { return; }

            // 入室要求の結果確認。
            if (freeJoinPending_)
            {
                if (matchmaking.IsLocalFreeMember())
                {
                    freeJoinPending_ = false;
                    ShowFreeRoom();
                }
                else if (matchmaking.GetFreeStateAt(freeListTable_) != MatchmakingManager.FREE_RECRUIT ||
                         matchmaking.IsFreeRoomFullAt(freeListTable_) ||
                         Time.time > freeJoinPendingUntil_)
                {
                    // 先着で負けた・部屋が消えた・同期が来ない→満員扱いで戻す（仕様D）。
                    freeJoinPending_ = false;
                    if (screenFreeList != null)
                    {
                        ShowFreeListWithMessage(T("満員です", "The room is full", "만원입니다", "房间已满", "房間已滿"));
                    }
                    else
                    {
                        ShowHomeWithInfo(T("満員です", "The room is full", "만원입니다", "房间已满", "房間已滿"));
                    }
                }
            }

            // 画面フェード中は退出元と移動先が短時間どちらもactiveになる。
            // activeSelfだけで整合性を判定すると、正常な募集開始・入室直後でも
            // 退出元を「終了／失敗」と誤認してホームへ戻してしまうため、遷移完了を待つ。
            if (screenTransitionActive_)
            {
                UpdateFreeButton();
                return;
            }

            // 表示中画面の整合性チェック＋再描画。
            if (screenFreeList != null && screenFreeList.activeSelf) { RenderFreeList(); }
            if (screenTableReset != null && screenTableReset.activeSelf) { RenderTableReset(); }

            if (screenFreeSetup != null && screenFreeSetup.activeSelf)
            {
                if (matchmaking.IsLocalFreeHost() && matchmaking.GetFreeState() == MatchmakingManager.FREE_SETUP)
                {
                    RenderFreeSetup();
                }
                else if (screenFreeList != null)
                {
                    // 競合負け・AFK自動解除など。卓一覧へ戻して選び直せるようにする。
                    ShowFreeListWithMessage(T("フリー対局の設定を終了しました", "Free match setup was ended", "프리 대국 설정이 종료되었습니다", "自由对局的设置已结束", "自由對局的設定已結束"));
                }
                else
                {
                    ShowHomeWithInfo(T("フリー対局の設定を終了しました", "Free match setup was ended", "프리 대국 설정이 종료되었습니다", "自由对局的设置已结束", "自由對局的設定已結束"));
                }
            }

            if (screenFreeRoom != null && screenFreeRoom.activeSelf)
            {
                if (matchmaking.GetLocalMatchTable() >= 0)
                {
                    ShowWaiting(); // 開始成立→卓誘導へ
                }
                else if (matchmaking.IsLocalFreeMember())
                {
                    RenderFreeRoom();
                }
                else
                {
                    ShowHomeWithInfo(T("部屋が解散されました", "The room was closed", "방이 해산되었습니다", "房间已解散", "房間已解散"));
                }
            }

            if (screenFreeConfirm != null && screenFreeConfirm.activeSelf && !freeJoinPending_)
            {
                // 確認中の部屋は「自分がいま見ている卓」の部屋。GetFreeState()（＝自分が
                // 入っている部屋）で見ると、まだ入室していないので必ず FREE_NONE になる。
                if (matchmaking.GetFreeStateAt(freeListTable_) == MatchmakingManager.FREE_RECRUIT
                    && !matchmaking.IsFreeRoomFullAt(freeListTable_))
                {
                    RenderFreeConfirm();
                }
                else if (screenFreeList != null)
                {
                    ShowFreeListWithMessage(T("入室できませんでした", "Could not join the room", "입장하지 못했습니다", "无法进入房间", "無法進入房間"));
                }
                else
                {
                    ShowHomeWithInfo(T("入室できませんでした", "Could not join the room", "입장하지 못했습니다", "无法进入房间", "無法進入房間"));
                }
            }

            UpdateFreeButton();
        }

        // ホームのフリー対局ボタン：他人の設定中・満員・段位戦中・クールダウン中はグレー（仕様D）。
        // ホームのフリー対局ボタン。部屋＝卓になってからは「卓一覧を開く」入口なので、
        // 他人の部屋の状態ではグレーにしない（押せない理由は一覧の各カードが説明する）。
        // 伏せるのは自分が段位戦で動けないときだけ。
        //
        // 旧・仕様Dの「いいえ」後5秒カウントダウンは 2026-09-14 に廃止。
        // 部屋＝卓になって「いいえ」の戻り先がホームから卓一覧へ変わり、
        // 別の卓を選び直すのが正常な操作になったため、待たせる理由が無くなった。
        // 連打は RequestJoinFreeRoomAt / Owner 検証が受け止める。
        private void UpdateFreeButton()
        {
            if (btnFree == null) { return; }

            btnFree.interactable = matchmaking.IsLocalFreeMember() || !IsMatchmakingBusy();

            if (btnFreeLabel != null)
            {
                // 2行目は初見向けの説明。見出しは48ptのまま残したいので、説明だけ
                // <size=55%> で落とす（下段の QueueInfo 行とは別枠・Label の中に収まる）。
                btnFreeLabel.text = T("フリー対局\n<size=55%>CPU戦・練習もできる</size>",
                                      "Free Match\n<size=55%>vs CPU / practice</size>",
                                      "프리 대국\n<size=55%>CPU전·연습도 가능</size>", "自由对局\n<size=55%>也可对战CPU・练习</size>", "自由對局\n<size=55%>也可對戰CPU・練習</size>");
            }
        }


        // --- フリー対局の画面表示・描画 ---

private void ShowFreeSetup()
{
    RenderFreeSetup();
    BeginScreenTransition(screenFreeSetup);
}

private void ShowFreeRoom()
{
    RenderFreeRoom();
    BeginScreenTransition(screenFreeRoom);
}

private void ShowFreeConfirm()
{
    RenderFreeConfirm();
    BeginScreenTransition(screenFreeConfirm);
}

        // 募集中の要約に出す赤ドラ表記。既定（3枚設定）のときは表記しない
        // ＝ほかの特殊ルールと同じで「既定から外れた分だけ」を並べる。
        // lang は KemonoLanguage.LANG_*（日英韓の文を並べて組み立てる呼び出し元が、それぞれの言語で呼ぶ）。
        private string FreeRedDoraSummary(int lang)
        {
            bool en = lang == KemonoLanguage.LANG_EN;
            bool ko = lang == KemonoLanguage.LANG_KO;
            bool zhs = lang == KemonoLanguage.LANG_ZHS;
            bool zht = lang == KemonoLanguage.LANG_ZHT;
            if (matchmaking == null) { return ""; }

            int redMode = matchmaking.GetFreeRedDoraMode();
            if (redMode == MatchmakingManager.RED_DORA_MODE_NONE)
            {
                return ko ? " / 적도라 없음" : zhs ? "／无赤宝牌" : zht ? "／無赤寶牌" : en ? " / No Red Dora" : "／赤ドラなし";
            }
            if (redMode == MatchmakingManager.RED_DORA_MODE_FOUR)
            {
                return ko ? " / 적도라 4장" : zhs ? "／赤宝牌4张" : zht ? "／赤寶牌4張" : en ? " / Red Dora 4" : "／赤ドラ4枚";
            }
            if (redMode == MatchmakingManager.RED_DORA_MODE_ALL)
            {
                return ko ? " / 전부 적" : (zhs || zht) ? "／全赤" : en ? " / All Red Fives" : "／全赤";
            }
            return "";
        }

        private string RuleLengthName(int len)
        {
            if (len == MatchmakingManager.RULE_LEN_TONPU) { return T("東風戦", "East Only", "동풍전", "东风战", "東風戰"); }
            if (len == MatchmakingManager.RULE_LEN_ONE) { return T("一局戦", "Single Hand", "1국전", "一局战", "一局戰"); }
            return T("半荘戦", "Half Game", "반장전", "半庄战", "半莊戰");
        }

        private string TurnTimerName(int mode)
        {
            if (mode == MatchmakingManager.TURN_TIMER_15_60) { return "15+60"; }
            if (mode == MatchmakingManager.TURN_TIMER_60_255) { return "60+255"; }
            return "8+30";
        }

        private void RenderFreeSetup()
        {
            if (matchmaking == null) { return; }

            if (freeModeLabel != null)
            {
                freeModeLabel.text = matchmaking.GetFreeMode() == 3
                    ? T("モード：三麻", "Mode: 3-Player", "모드: 3인", "模式：三麻", "模式：三麻")
                    : T("モード：四麻", "Mode: 4-Player", "모드: 4인", "模式：四麻", "模式：四麻");
            }
            if (freeAbilityLabel != null)
            {
                freeAbilityLabel.text = matchmaking.GetFreeAbilityOn()
                    ? T("能力：あり", "Abilities: On", "능력: 있음", "能力：有", "能力：有")
                    : T("能力：なし", "Abilities: Off", "능력: 없음", "能力：无", "能力：無");
            }
            if (freeLengthLabel != null)
            {
                freeLengthLabel.text = T("局数：", "Length: ", "국 수: ", "局数：", "局數：")
                    + RuleLengthName(matchmaking.GetFreeRuleLength());
            }

            if (freeTimerLabel != null)
            {
                freeTimerLabel.text = T("長考時間：", "Timer: ", "제한 시간: ", "思考时间：", "思考時間：")
                    + TurnTimerName(matchmaking.GetFreeTurnTimerMode());
            }
            if (freeRedDoraLabel != null)
            {
                int redMode = matchmaking.GetFreeRedDoraMode();
                if (redMode == MatchmakingManager.RED_DORA_MODE_NONE)
                {
                    freeRedDoraLabel.text = T("赤ドラ：なし", "Red Dora: None", "적도라: 없음", "赤宝牌：无", "赤寶牌：無");
                }
                else if (redMode == MatchmakingManager.RED_DORA_MODE_FOUR)
                {
                    freeRedDoraLabel.text = T("赤ドラ：4枚", "Red Dora: 4", "적도라: 4장", "赤宝牌：4张", "赤寶牌：4張");
                }
                else if (redMode == MatchmakingManager.RED_DORA_MODE_ALL)
                {
                    freeRedDoraLabel.text = T("赤ドラ：全赤", "Red Dora: All", "적도라: 전부 적", "赤宝牌：全赤", "赤寶牌：全赤");
                }
                else
                {
                    freeRedDoraLabel.text = matchmaking.GetFreeMode() == 3
                        ? T("赤ドラ：2枚（標準）", "Red Dora: 2 (Standard)", "적도라: 2장 (표준)", "赤宝牌：2张（标准）", "赤寶牌：2張（標準）")
                        : T("赤ドラ：3枚", "Red Dora: 3", "적도라: 3장", "赤宝牌：3张", "赤寶牌：3張");
                }
            }
            if (freeAotenjouLabel != null)
            {
                int aotenjouMode = matchmaking.GetFreeAotenjouMode();
                if (aotenjouMode == MatchmakingManager.AOTENJOU_MODE_CURRENT)
                {
                    freeAotenjouLabel.text = T("青天井：現行式（N倍満）", "No Limit: Current (Multiplier)", "무제한(아오텐조): 현행식 (N배 만관)", "青天井：现行式（N倍满）", "青天井：現行式（N倍滿）");
                }
                else if (aotenjouMode == MatchmakingManager.AOTENJOU_MODE_TRADITIONAL)
                {
                    freeAotenjouLabel.text = T("青天井：従来式（符×指数）", "No Limit: Traditional (Exponential)", "무제한(아오텐조): 종래식 (부×지수)", "青天井：传统式（符×指数）", "青天井：傳統式（符×指數）");
                }
                else
                {
                    freeAotenjouLabel.text = T("青天井：なし", "No Limit: Off", "무제한(아오텐조): 없음", "青天井：无", "青天井：無");
                }
            }
            if (freeLocalYakuLabel != null)
            {
                freeLocalYakuLabel.text = matchmaking.GetFreeLocalYaku()
                    ? T("ローカル役：あり", "Local Yaku: On", "로컬 역: 있음", "地方役：有", "地方役：有")
                    : T("ローカル役：なし", "Local Yaku: Off", "로컬 역: 없음", "地方役：无", "地方役：無");
            }
            if (freeOpenReachLabel != null)
            {
                freeOpenReachLabel.text = matchmaking.GetFreeOpenReach()
                    ? T("オープン立直：あり", "Open Riichi: On", "오픈 리치: 있음", "开立直：有", "開立直：有")
                    : T("オープン立直：なし", "Open Riichi: Off", "오픈 리치: 없음", "开立直：无", "開立直：無");
            }
            if (freeTsumoLossLabel != null)
            {
                // ツモ損は三麻でしか意味がないので、四麻のときはそう書いておく。
                freeTsumoLossLabel.text = matchmaking.GetFreeMode() == 3
                    ? (matchmaking.GetFreeTsumoLoss()
                        ? T("ツモ損：あり", "Tsumo Loss: On", "쯔모손: 있음", "自摸损：有", "自摸損：有")
                        : T("ツモ損：なし", "Tsumo Loss: Off", "쯔모손: 없음", "自摸损：无", "自摸損：無"))
                    : T("ツモ損：－（三麻のみ）", "Tsumo Loss: - (3P only)", "쯔모손: - (3인 전용)", "自摸损：－（仅限三麻）", "自摸損：－（僅限三麻）");
            }
            if (freeBustLabel != null)
            {
                freeBustLabel.text = matchmaking.GetFreeBust()
                    ? T("トビ：あり", "Bust: On", "토비: 있음", "击飞：有", "擊飛：有")
                    : T("トビ：なし", "Bust: Off", "토비: 없음", "击飞：无", "擊飛：無");
            }
            if (freeCpuLevelLabel != null)
            {
                freeCpuLevelLabel.text = matchmaking.GetFreeCpuStrong()
                    ? T("CPUの強さ：強い", "CPU Level: Strong", "CPU 난이도: 강함", "CPU强度：强", "CPU強度：強")
                    : T("CPUの強さ：簡単", "CPU Level: Easy", "CPU 난이도: 쉬움", "CPU强度：简单", "CPU強度：簡單");
            }
        }

        private void RenderFreeRoom()
        {
            if (matchmaking == null) { return; }

            int mode = matchmaking.GetFreeMode();
            int humans = matchmaking.GetFreeMemberCount();
            int cpu = matchmaking.GetFreeCpuCount();
            bool host = matchmaking.IsLocalFreeHost();

            if (freeRoomTitle != null)
            {
                freeRoomTitle.text = T(
                    "フリー対局 募集中（" + (humans + cpu) + "/" + mode + "）",
                    "Free Match — Recruiting (" + (humans + cpu) + "/" + mode + ")",
                    "프리 대국 모집 중 (" + (humans + cpu) + "/" + mode + ")", "自由对局 招募中（" + (humans + cpu) + "/" + mode + "）", "自由對局 招募中（" + (humans + cpu) + "/" + mode + "）");
            }

            if (freeRoomInfo != null)
            {
                string info = T(
                    (mode == 3 ? "三麻" : "四麻")
                        + "／" + RuleLengthName(matchmaking.GetFreeRuleLength())
                        + FreeRedDoraSummary(KemonoLanguage.LANG_JA)
                        + (matchmaking.GetFreeAotenjouMode() == MatchmakingManager.AOTENJOU_MODE_CURRENT
                            ? "／青天井（現行式）"
                            : (matchmaking.GetFreeAotenjouMode() == MatchmakingManager.AOTENJOU_MODE_TRADITIONAL
                                ? "／青天井（従来式）" : ""))
                        + (matchmaking.GetFreeLocalYaku() ? "／ローカル役" : "")
                        + (matchmaking.GetFreeOpenReach() ? "／オープン立直" : "")
                        + (mode == 3 && !matchmaking.GetFreeTsumoLoss() ? "／ツモ損なし" : "")
                        + (matchmaking.GetFreeBust() ? "" : "／トビなし")
                        + (matchmaking.GetFreeCpuStrong() ? "／CPU強い" : "")
                        + "／長考" + TurnTimerName(matchmaking.GetFreeTurnTimerMode()),
                    (mode == 3 ? "3-Player" : "4-Player")
                        + " / " + RuleLengthName(matchmaking.GetFreeRuleLength())
                        + FreeRedDoraSummary(KemonoLanguage.LANG_EN)
                        + (matchmaking.GetFreeAotenjouMode() == MatchmakingManager.AOTENJOU_MODE_CURRENT
                            ? " / No Limit (Current)"
                            : (matchmaking.GetFreeAotenjouMode() == MatchmakingManager.AOTENJOU_MODE_TRADITIONAL
                                ? " / No Limit (Traditional)" : ""))
                        + (matchmaking.GetFreeLocalYaku() ? " / Local Yaku" : "")
                        + (matchmaking.GetFreeOpenReach() ? " / Open Riichi" : "")
                        + (mode == 3 && !matchmaking.GetFreeTsumoLoss() ? " / No Tsumo Loss" : "")
                        + (matchmaking.GetFreeBust() ? "" : " / No Bust")
                        + (matchmaking.GetFreeCpuStrong() ? " / Strong CPU" : "")
                        + " / Timer " + TurnTimerName(matchmaking.GetFreeTurnTimerMode()),
                    (mode == 3 ? "3인" : "4인") + " / " + RuleLengthName(matchmaking.GetFreeRuleLength()) + FreeRedDoraSummary(KemonoLanguage.LANG_KO) + (matchmaking.GetFreeAotenjouMode() == MatchmakingManager.AOTENJOU_MODE_CURRENT ? " / 무제한 (현행식)" : (matchmaking.GetFreeAotenjouMode() == MatchmakingManager.AOTENJOU_MODE_TRADITIONAL ? " / 무제한 (종래식)" : "")) + (matchmaking.GetFreeLocalYaku() ? " / 로컬 역" : "") + (matchmaking.GetFreeOpenReach() ? " / 오픈 리치" : "") + (mode == 3 && !matchmaking.GetFreeTsumoLoss() ? " / 쯔모손 없음" : "") + (matchmaking.GetFreeBust() ? "" : " / 토비 없음") + (matchmaking.GetFreeCpuStrong() ? " / CPU 강함" : "") + " / 제한 시간 " + TurnTimerName(matchmaking.GetFreeTurnTimerMode()), (mode == 3 ? "三麻" : "四麻") + "／" + RuleLengthName(matchmaking.GetFreeRuleLength()) + FreeRedDoraSummary(KemonoLanguage.LANG_ZHS) + (matchmaking.GetFreeAotenjouMode() == MatchmakingManager.AOTENJOU_MODE_CURRENT ? "／青天井（现行式）" : (matchmaking.GetFreeAotenjouMode() == MatchmakingManager.AOTENJOU_MODE_TRADITIONAL ? "／青天井（传统式）" : "")) + (matchmaking.GetFreeLocalYaku() ? "／地方役" : "") + (matchmaking.GetFreeOpenReach() ? "／开立直" : "") + (mode == 3 && !matchmaking.GetFreeTsumoLoss() ? "／无自摸损" : "") + (matchmaking.GetFreeBust() ? "" : "／无击飞") + (matchmaking.GetFreeCpuStrong() ? "／CPU强" : "") + "／思考" + TurnTimerName(matchmaking.GetFreeTurnTimerMode()), (mode == 3 ? "三麻" : "四麻") + "／" + RuleLengthName(matchmaking.GetFreeRuleLength()) + FreeRedDoraSummary(KemonoLanguage.LANG_ZHT) + (matchmaking.GetFreeAotenjouMode() == MatchmakingManager.AOTENJOU_MODE_CURRENT ? "／青天井（現行式）" : (matchmaking.GetFreeAotenjouMode() == MatchmakingManager.AOTENJOU_MODE_TRADITIONAL ? "／青天井（傳統式）" : "")) + (matchmaking.GetFreeLocalYaku() ? "／地方役" : "") + (matchmaking.GetFreeOpenReach() ? "／開立直" : "") + (mode == 3 && !matchmaking.GetFreeTsumoLoss() ? "／無自摸損" : "") + (matchmaking.GetFreeBust() ? "" : "／無擊飛") + (matchmaking.GetFreeCpuStrong() ? "／CPU強" : "") + "／思考" + TurnTimerName(matchmaking.GetFreeTurnTimerMode()));
                // 【削除禁止の注意】ここで IsFreeStartBlockedByFullTables() を見てはいけない。
                // 部屋＝卓になった時点で、そのフラグの意味は「新しく部屋を作れる空卓が無い」に
                // 変わった。部屋の代表は開いた瞬間に自分の卓を押さえているので、全卓が埋まると
                // 必ず真になり、開始できるのに「開始できません」と出たうえで下の案内まで消える。
                // 同じ罠が MatchmakingManager.OnDeserialization の START 判定にもある。
                if (host)
                {
                    info += T("\n人数がそろったら「開始」を押してください",
                              "\nPress START once everyone is here.",
                              "\n인원이 모이면 '시작'을 눌러 주세요.", "\n人数凑齐后请按「开始」", "\n人數湊齊後請按「開始」");
                }
                freeRoomInfo.text = info;
            }

            if (freeMemberTexts != null)
            {
                for (int i = 0; i < freeMemberTexts.Length; ++i)
                {
                    if (freeMemberTexts[i] == null) { continue; }

                    string line;
                    if (i >= mode)
                    {
                        line = "－"; // 三麻の4行目
                    }
                    else if (i < humans)
                    {
                        int id = matchmaking.GetFreeMemberId(i);
                        var p = VRCPlayerApi.GetPlayerById(id);
                        string nm = (p != null && Utilities.IsValid(p)) ? p.displayName : "???";
                        line = (i == 0) ? nm + T("（代表）", " (Host)", " (방장)", "（房主）", "（房主）") : nm;
                    }
                    else if (i < humans + cpu)
                    {
                        line = matchmaking.GetFreeCpuStrong()
                            ? T("CPU(強い)", "CPU (Strong)", "CPU(강함)", "CPU(强)", "CPU(強)")
                            : T("CPU(簡単)", "CPU (Easy)", "CPU(쉬움)", "CPU(简单)", "CPU(簡單)");
                    }
                    else
                    {
                        line = T("（募集中...）", "(Recruiting...)", "(모집 중...)", "（招募中...）", "（招募中...）");
                    }
                    freeMemberTexts[i].text = line;
                }
            }

            if (btnFreeCpuAdd != null) { btnFreeCpuAdd.SetActive(host); }
            if (btnFreeCpuRemove != null) { btnFreeCpuRemove.SetActive(host); }
            if (btnFreeStart != null) { btnFreeStart.SetActive(host && humans + cpu == mode); }
            if (freeCloseLabel != null)
            {
                freeCloseLabel.text = host ? T("解散", "Close", "해산", "解散", "解散") : T("退出", "Leave", "퇴장", "退出", "退出");
            }
        }

        private void RenderFreeConfirm()
        {
            if (matchmaking == null) { return; }

            if (freeConfirmText != null)
            {
                var hostName = matchmaking.GetFreeHostNameAt(freeListTable_);
                string table = (freeListTable_ >= 0) ? (freeListTable_ + 1).ToString() : "?";
                freeConfirmText.text = T(
                    "卓 " + table + " の " + hostName + " さんの部屋に入ります。よろしいですか？",
                    "Join " + hostName + "'s room at table " + table + "?",
                    "탁 " + table + "의 " + hostName + " 님의 방에 들어갑니다. 괜찮으십니까?", "将进入牌桌 " + table + " 上 " + hostName + " 的房间。确定吗？", "將進入牌桌 " + table + " 上 " + hostName + " 的房間。確定嗎？");
            }
        }


        // ============================================================
        // キャラ選択（各カードの Button onClick から呼ぶ）
        // ============================================================

        public void SelectChara0() { SelectChara(0); }
        public void SelectChara1() { SelectChara(1); }
        public void SelectChara2() { SelectChara(2); }
        public void SelectChara3() { SelectChara(3); }

        public void SelectChara(int chara)
        {
            if (statsStore == null) { return; }
            if (IsMatchmakingBusy()) { return; } // 待機列に入った時点でキャラ選択ロック（仕様A）
            if (chara < 0 || chara >= PlayerStatsStore.NUM_CHARAS) { chara = PlayerStatsStore.CHARA_VOL; }

            // 自画像は「押した＝選択」ではなく、その場で撮影してから決定させる。
            if (chara == PlayerStatsStore.CHARA_SELFIE)
            {
                if (!IsSelfieAvailable()) { RenderChara(); return; }
                ShowSelfie();
                return;
            }

            statsStore.SetChara(chara);
            ApplyCharaVoicePack(chara, true);
            ApplyCharaPreview();
            RenderHomeChara();
            RenderChara();
        }

        // ============================================================
        // 自画像キャラ（ScreenSelfie）
        //
        //  ・カードを押す → 撮影画面へ遷移して**構図合わせ**（ライブビュー＋上下/ズーム調整）
        //  ・「撮影」で3秒カウントダウン（ここでカメラ位置が固定される）
        //  ・撮り終わるとプレビューになり「撮り直し」「決定」が出る
        //  ・決定でキャラを自画像へ確定し、キャラ選択画面へ戻る
        //
        //  写真は SelfiePortraitCamera が持つ RenderTexture そのもの＝同期しない。
        //  ワールドを出ると消えるが、キャラの選択ID自体は PlayerData に残るので、
        //  再入場時は「写真を撮る」プレースホルダから始まる。
        // ============================================================

        // 自画像が選べる状態か（撮影ブースが居て、段位が鋭爪以上）。
        private bool IsSelfieAvailable()
        {
            if (selfieCamera == null || screenSelfie == null) { return false; }
            return statsStore != null && statsStore.IsSelfieUnlocked();
        }

        // 撮影画面を開く。開いた時点では**構図合わせ**（ライブビュー）から始まる。
        // ケモノアバターは耳・角・盛り髪が既定の構図から切れるので、
        // いきなりカウントダウンを回さず、まず本人に合わせてもらう。
        public void ShowSelfie()
        {
            if (!IsSelfieAvailable()) { ShowChara(); return; }

            BeginScreenTransition(screenSelfie);
            selfieCamera.BeginFraming();
        }

        // 「撮り直し」。構図合わせからやり直す（構図を直してから撮れる）。
        public void SelfieRetake()
        {
            if (selfieCamera == null) { return; }
            selfieCamera.BeginFraming();
        }

        // 「撮影」。構図が決まったので3秒のセルフタイマーへ。
        public void SelfieShoot()
        {
            if (selfieCamera == null) { return; }
            selfieCamera.BeginCountdown();
        }

        // --- 構図調整ボタン（ScreenSelfie/FrameControls の onClick） ---
        // 実体は SelfiePortraitCamera 側。値は PlayerData に残るので次回も同じ構図で始まる。

        public void SelfieFrameUp()
        {
            if (selfieCamera == null) { return; }
            selfieCamera.FrameUp();
        }

        public void SelfieFrameDown()
        {
            if (selfieCamera == null) { return; }
            selfieCamera.FrameDown();
        }

        public void SelfieZoomIn()
        {
            if (selfieCamera == null) { return; }
            selfieCamera.ZoomIn();
        }

        public void SelfieZoomOut()
        {
            if (selfieCamera == null) { return; }
            selfieCamera.ZoomOut();
        }

        public void SelfieFrameReset()
        {
            if (selfieCamera == null) { return; }
            selfieCamera.FrameReset();
        }

        // 「決定」。まず共有用の縮小版を PlayerData へ焼きに行く。
        // 焼き上がり（または失敗）は OnSelfieSaved で戻ってくる。
        public void SelfieConfirm()
        {
            if (statsStore == null || selfieCamera == null) { return; }
            if (!selfieCamera.HasPendingShot()) { return; }

            selfieCamera.BeginSave();
        }

        // SelfiePortraitCamera から、共有用の保存が終わったときに呼ばれる。
        // ok=false（Readback失敗・ストア未割当）でも、ローカルの写真は使えるので
        // キャラの確定自体は行う。他プレイヤーに見えないだけ。
        public void OnSelfieSaved(bool ok)
        {
            if (statsStore == null) { return; }

            statsStore.SetChara(PlayerStatsStore.CHARA_SELFIE);
            ApplyCharaVoicePack(PlayerStatsStore.CHARA_SELFIE, false);
            ApplyCharaPreview();
            ApplyPortraits();
            RenderHomeChara();
            ShowChara();
        }

        // 「もどる」。撮影をやめてキャラ選択へ戻す（キャラは変更しない）。
        public void SelfieCancel()
        {
            if (selfieCamera != null) { selfieCamera.StopCamera(); }
            ShowChara();
        }

        // SelfiePortraitCamera から、シャッターを切った瞬間に呼ばれる。
        public void OnSelfieCaptured()
        {
            ApplyPortraits();
        }

        // 撮影画面の案内文（SelfiePortraitCamera が状態に応じて読みに来る）。
        // 構図合わせ中は、この後ろにカメラが現在値「(+15cm / 1.6m)」を足して出す。
        public string GetSelfieFramingText()
        {
            return T("耳や角が切れないよう構図を合わせて、「撮影」を押してください。",
                     "Adjust the framing so your ears and horns fit, then press Shoot.",
                     "귀나 뿔이 잘리지 않도록 구도를 맞추고 '촬영'을 눌러 주세요.", "调整构图，别让耳朵或角被裁掉，然后按「拍摄」。", "調整構圖，別讓耳朵或角被裁掉，然後按「拍攝」。");
        }

        public string GetSelfiePoseText()
        {
            return T("カメラの方を向いてポーズをとってください。",
                     "Face the camera and strike a pose.",
                     "카메라 쪽을 보고 포즈를 취해 주세요.", "请面向相机摆个姿势。", "請面向相機擺個姿勢。");
        }

        public string GetSelfieConfirmText()
        {
            // 「粗くなる」のはここで保存する共有版だけ。自分の画面はこの直後から高画質になる。
            return T("この写真でよろしいですか？\n決定すると他のプレイヤーにも表示されます（共有用は粗くなります）。",
                     "Use this photo?\nIt will also be shown to other players (the shared copy is downscaled).",
                     "이 사진으로 하시겠습니까?\n확정하면 다른 플레이어에게도 표시됩니다 (공유용은 화질이 낮아집니다).", "使用这张照片吗？\n确定后也会显示给其他玩家（共享版画质会变粗糙）。", "使用這張照片嗎？\n確定後也會顯示給其他玩家（共享版畫質會變粗糙）。");
        }

        public string GetSelfieSavingText()
        {
            return T("保存しています…", "Saving...", "저장 중...", "保存中…", "儲存中…");
        }

        // 能力スイッチ（BtnAbilityToggle の onClick から呼ぶ）。
        public void ToggleAbility()
        {
            if (statsStore == null) { return; }
            if (IsMatchmakingBusy()) { return; } // 待機列に入った時点で能力スイッチもロック（仕様A）

            statsStore.SetAbilityOn(!statsStore.GetAbilityOn());
            RenderChara();
        }

        // チュンビームのON/OFF（BtnChunBeamToggle の onClick から呼ぶ）。
        // 対象は選択中のキャラ。未解放なら何もしない（ボタンは解放条件の表示を兼ねる）。
        public void ToggleChunBeam()
        {
            if (statsStore == null) { return; }
            int chara = statsStore.GetChara();
            if (!statsStore.IsChunBeamUnlocked(chara)) { RenderChara(); return; }
            statsStore.SetChunBeamOn(chara, !statsStore.GetChunBeamOn(chara));
            RenderChara();
        }

        private void RenderChunBeamToggle(int chara)
        {
            bool has = statsStore != null && statsStore.IsChunBeamChara(chara);
            if (chunBeamToggleRoot != null && chunBeamToggleRoot.activeSelf != has)
            {
                chunBeamToggleRoot.SetActive(has);
            }
            if (chunBeamNote != null && chunBeamNote.gameObject.activeSelf != has)
            {
                chunBeamNote.gameObject.SetActive(has);
            }
            if (!has) { return; }

            bool unlocked = statsStore.IsChunBeamUnlocked(chara);
            bool on = unlocked && statsStore.GetChunBeamOn(chara);

            if (chunBeamToggleLabel != null)
            {
                chunBeamToggleLabel.text = !unlocked
                    ? T("チュンビーム：未解放", "Chun Beam: Locked", "춘 빔: 미해금", "中光束：未解锁", "中光束：未解鎖")
                    : (on ? T("チュンビーム：ON", "Chun Beam: ON", "춘 빔: ON", "中光束：ON", "中光束：ON")
                          : T("チュンビーム：OFF", "Chun Beam: OFF", "춘 빔: OFF", "中光束：OFF", "中光束：OFF"));
            }
            if (chunBeamNote != null)
            {
                string rival = statsStore.GetChunBeamRivalName(chara);
                chunBeamNote.text = !unlocked
                    ? T(rival + " より多い点数で\n終局すると解放",
                        "Finish a game with more\npoints than " + rival + " to unlock",
                        rival + "보다 높은 점수로\n종국하면 해금", "以高于 " + rival + " 的分数\n终局即可解锁", "以高於 " + rival + " 的分數\n終局即可解鎖")
                    : T("中を捨てたときに\nカットインが出ます",
                        "A cut-in plays when\nyou discard Red Dragon",
                        "중을 버렸을 때\n컷인이 나옵니다", "打出中时\n会出现特写演出", "打出中時\n會出現特寫演出");
            }
        }

        // ============================================================
        // 装飾品：和了演出コスメ（ScreenCosmetic）
        //  ・選択は PlayerData(kmj_agari_fx) に保存＝クロスインスタンス永続。
        //  ・卓側は和了の瞬間に「和了者の PlayerData」を読むので、同期変数は不要。
        //    対局中に変えても次の和了から反映される（キャラ選択と違いロック不要）。
        //  ・選ぶと MainMenu 脇の試写用エフェクト（fxCatalog）がローカル再生される。
        //    卓側の演出はカリングで落ちるので、試写は必ずこの1個を使う。
        // ============================================================
        public void SelectFx0() { SelectFx(0); }
        public void SelectFx1() { SelectFx(1); }
        public void SelectFx2() { SelectFx(2); }
        public void SelectFx3() { SelectFx(3); }
        public void SelectFx4() { SelectFx(4); }
        public void SelectFx5() { SelectFx(5); }
        public void SelectFx6() { SelectFx(6); }
        public void SelectFx7() { SelectFx(7); }

        public void SelectFx(int fxId)
        {
            if (statsStore == null) { return; }

            // 枠外のカードは黙って無視する。カード枚数はカテゴリごとに違うので、
            // 判定も CosmeticCardCount() で引く（和了エフェクトの枚数を色へ流用しない）。
            int count = CosmeticCardCount();
            if (count <= 0) { return; }
            if (fxId < 0 || fxId >= count) { return; }

            // 立直BGMはカードを押しただけでは鳴らさない。鳴らす口は詳細欄の「試聴」ボタン1つ
            //（曲は数十秒鳴りっぱなしになるので、選ぶたびに勝手に始まると選び比べができない）。
            // 別のカードへ移ったら鳴っている試聴は止める＝ボタンの「停止」は常にいま見ている曲を指す。
            //
            // 解禁（鋭爪★1）前は「聴けるが付けられない」。タブもカードも押せて試聴も動くが、
            // SetRiichiBgm は通さない＝装備中の曲は変わらない。「なし」はいつでも選べる。
            if (cosmeticCategory_ == 3)
            {
                riichiBgmFocus_ = fxId;
                statsStore.SetRiichiBgm(fxId);
                StopRiichiBgmPreview();
                RenderCosmetic();
                return;
            }

            if (cosmeticCategory_ == 1)
            {
                statsStore.SetTileBackColor(fxId + 1);
                ApplyCosmeticColors();
                RenderCosmetic();
                return;
            }
            if (cosmeticCategory_ == 2)
            {
                statsStore.SetTableTopColor(fxId + 1);
                ApplyCosmeticColors();
                RenderCosmetic();
                return;
            }
            statsStore.SetAgariFx(fxId);

            // 試写（ローカルのみ。他プレイヤーには見えない）
            fxCatalog.PlayHere(fxId);

            RenderCosmetic();
        }

        // 詳細欄の「試写／試聴」ボタン。いま選んでいるコスメをその場で再生する。
        // 立直BGM（カテゴリ3）は鳴っている時間が長いので、押すたびに再生／停止が入れ替わる。
        public void PreviewFx()
        {
            if (cosmeticCategory_ == 3)
            {
                if (IsRiichiBgmPreviewPlaying()) { StopRiichiBgmPreview(); }
                else { PlayRiichiBgmPreview(riichiBgmFocus_); }
                RenderCosmetic();
                return;
            }

            if (cosmeticCategory_ != 0) { return; }
            if (fxCatalog == null) { return; }

            int count = fxCatalog.GetVariantCount();
            if (count <= 0) { return; }

            int selected = statsStore != null ? statsStore.GetAgariFx() : 0;
            if (selected < 0 || selected >= count) { selected = 0; }

            fxCatalog.PlayHere(selected);
        }

        // ============================================================
        // 立直BGMの試聴（完全ローカル）
        //   ・鳴らすのはメニュー脇の1個だけ。卓の立直BGMとは無関係。
        //   ・loop は切る＝曲が終われば勝手に止まる（メニューを開けっ放しでも鳴り続けない）。
        //   ・音量は卓と同じ「トラック別の基準ゲイン × 音量パネルの bgmAmp」。
        //     Update が毎フレーム書き戻すので、試聴中にスライダーを動かしても追従する。
        // ============================================================
        public bool IsRiichiBgmPreviewPlaying()
        {
            return riichiBgmPreviewSource != null && riichiBgmPreviewSource.isPlaying;
        }

        private void PlayRiichiBgmPreview(int track)
        {
            if (riichiBgmPreviewSource == null) { return; }

            riichiBgmPreviewSource.Stop();
            riichiPreviewPlaying_ = false;

            // 「なし」は試聴するものが無い＝止めるだけ。
            if (track <= 0 || riichiBgmPreviewClips == null || track >= riichiBgmPreviewClips.Length) { return; }

            var clip = riichiBgmPreviewClips[track];
            if (clip == null) { return; }

            riichiPreviewGain_ = riichiBgmPreviewGains != null && track < riichiBgmPreviewGains.Length
                ? riichiBgmPreviewGains[track] : 0.2f;
            riichiBgmPreviewSource.clip = clip;
            riichiBgmPreviewSource.loop = false;
            riichiBgmPreviewSource.volume = riichiPreviewGain_ * (volumePanel != null ? volumePanel.bgmAmp : 1.0f);
            riichiBgmPreviewSource.Play();
            riichiPreviewPlaying_ = true;
        }

        private void StopRiichiBgmPreview()
        {
            if (riichiBgmPreviewSource != null) { riichiBgmPreviewSource.Stop(); }
            riichiPreviewPlaying_ = false;
        }

        // 試聴の音量追従と、曲が終わったときのボタン文言の戻し。Update から毎フレーム。
        private void UpdateRiichiBgmPreview()
        {
            if (riichiBgmPreviewSource == null) { return; }

            if (riichiBgmPreviewSource.isPlaying)
            {
                riichiBgmPreviewSource.volume = riichiPreviewGain_ * (volumePanel != null ? volumePanel.bgmAmp : 1.0f);
                riichiPreviewPlaying_ = true;
                return;
            }

            if (!riichiPreviewPlaying_) { return; }
            riichiPreviewPlaying_ = false;
            // 鳴り終わった＝ボタンが「停止」のまま残るので描き直す。
            if (screenCosmetic != null && screenCosmetic.activeSelf && cosmeticCategory_ == 3) { RenderCosmetic(); }
        }

        // キャラ選択と同時にボイスパックを自動設定する（卓のボイス切替ボタンは廃止）。
        // charaVoicePacks[chara] が負なら連動しない（未設定扱い）。
        // VOICE_PACK_KEY の書き手は MahjongVoiceSelector のみ（保留スロットもそちら1つ）。
        // voiceSelector 未割当時は連動しない（キャラ／能力の保存は statsStore 側で継続）。
        // preview=true でキャラの試聴ボイスをローカル再生する（キャラ選択のクリック時のみ）。
        // 復元時の自動連動から鳴らすと入場のたびに勝手に喋るので、そちらは false 固定。
        private void ApplyCharaVoicePack(int chara, bool preview)
        {
            if (voiceSelector == null)
            {
                Debug.LogError("[MainMenuController] voiceSelector が未割当のため、キャラ連動ボイスを保存できません。");
                return;
            }
            if (charaVoicePacks == null || chara < 0 || chara >= charaVoicePacks.Length) { return; }

            int packId = charaVoicePacks[chara];
            if (packId < 0) { return; }

            // 復元前/済みの分岐・保留は Selector 内。
            // preview=true は SelectPack（＝SetPack＋ローカル試聴）を通す。
            if (preview)
            {
                voiceSelector.SelectPack(packId);
            }
            else
            {
                voiceSelector.SetPack(packId);
            }
        }

        // 選択中キャラの3Dモデルだけをローカル表示する（他人の選択は反映しない）。
        private void ApplyCharaPreview()
        {
            if (charaPreviewModels == null) { return; }

            int selected = PlayerStatsStore.CHARA_VOL;
            if (statsStore != null) { selected = statsStore.GetChara(); }

            for (int i = 0; i < charaPreviewModels.Length; ++i)
            {
                if (charaPreviewModels[i] != null)
                {
                    charaPreviewModels[i].SetActive(i == selected);
                }
            }
        }

        // ホーム画面左側へ、自分が現在選択しているキャラ写真を表示する。
        // PlayerData復元前はPlayerStatsStoreの保留値、未設定時はVolを使う。
        private void RenderHomeChara()
        {
            if (homeCharaPortrait == null) { return; }

            int selected = PlayerStatsStore.CHARA_VOL;
            if (statsStore != null) { selected = statsStore.GetChara(); }
            if (selected < 0 || selected >= PlayerStatsStore.NUM_CHARAS)
            {
                selected = PlayerStatsStore.CHARA_VOL;
            }

            Texture portrait = GetCharaPortrait(selected);

            homeCharaPortrait.texture = portrait;
            homeCharaPortrait.color = Color.white;
            homeCharaPortrait.raycastTarget = false;
            homeCharaPortrait.gameObject.SetActive(portrait != null);
        }

        // ============================================================
        // 描画（遷移時・コールバック時のみ。Updateポーリング禁止）
        // ============================================================

        // PlayerStatsStoreの復元・段位結果反映後から呼ぶ公開更新口。
        // 同期変数やUpdateポーリングは持たず、既存の2つのTextだけをローカル描画する。
        public void RefreshRankDisplay()
        {
            string value = statsStore != null ? statsStore.GetRankDisplayText() : "段位 --";
            if (headerRank != null) { headerRank.text = value; }
            if (profileRank != null) { profileRank.text = value; }

            int majorRank = statsStore != null ? statsStore.GetMajorRankIndex() : -1;
            Texture icon = rankIcons != null && majorRank >= 0 && majorRank < rankIcons.Length
                         ? rankIcons[majorRank]
                         : null;
            SetRankIcon(headerRankIcon, icon);
            SetRankIcon(profileRankIcon, icon);
        }

        private void SetRankIcon(RawImage target, Texture icon)
        {
            if (target == null) { return; }
            target.texture = icon;
            bool show = icon != null;
            if (target.gameObject.activeSelf != show) { target.gameObject.SetActive(show); }
        }

        private void RenderHeader()
        {
            var lp = Networking.LocalPlayer;

            if (headerName != null)
            {
                headerName.text = lp != null ? lp.displayName : "";
            }

            if (headerRank != null)
            {
                RefreshRankDisplay();
            }
        }

        private void RenderProfile()
        {
            var lp = Networking.LocalPlayer;

            if (profileName != null)
            {
                profileName.text = lp != null ? lp.displayName : "";
            }

            if (profileRank != null)
            {
                RefreshRankDisplay();
            }

            // タブのハイライト（選択中を明るく）。
            if (profileTabImages != null)
            {
                for (int i = 0; i < profileTabImages.Length; ++i)
                {
                    if (profileTabImages[i] != null)
                    {
                        profileTabImages[i].color = (i == profileTab_) ? tabSelectedColor_ : tabNormalColor_;
                    }
                }
            }

            if (statsSelTitle != null) { statsSelTitle.text = CategoryName(profileTab_); }

            if (statsStore != null)
            {
                RenderStatsBlockCat(profileTab_);
            }

            RenderRecentGraph();
        }

        // 選択中カテゴリの統計ブロックを描画する。
        // 1位率は対局数、和了率・放銃率は総局数を分母にする。
        // v2導入前の累計回数は残すが総局数を復元できないため、率はv2期間だけで計算する。
        private void RenderStatsBlockCat(int cat)
        {
            int play = statsStore.GetPlayCountCat(cat);
            int win = statsStore.GetWinCountCat(cat);
            int handV2 = statsStore.GetHandCountV2Cat(cat);
            int agariV2 = statsStore.GetAgariCountV2Cat(cat);
            int hojuV2 = statsStore.GetHojuCountV2Cat(cat);

            if (statsSelPlay != null) { statsSelPlay.text = play.ToString(); }
            if (statsSelWin != null) { statsSelWin.text = FormatCountWithRate(win, win, play); }
            if (statsSelAgari != null) { statsSelAgari.text = FormatCountWithRate(agariV2, agariV2, handV2); }
            if (statsSelHoju != null) { statsSelHoju.text = FormatCountWithRate(hojuV2, hojuV2, handV2); }
        }

        // 戦績タブの表示名。
        private string CategoryName(int cat)
        {
            if (cat == PlayerStatsStore.CAT_SANMA) { return T("三麻 段位戦", "3-Player Ranked", "3인 단위전", "三麻 段位战", "三麻 段位戰"); }
            if (cat == PlayerStatsStore.CAT_FREE) { return T("フリー対局", "Free Match", "프리 대국", "自由对局", "自由對局"); }
            return T("四麻 段位戦", "4-Player Ranked", "4인 단위전", "四麻 段位战", "四麻 段位戰");
        }

        // "回数 (率%)" 形式。1位は全期間、和了/放銃は総局数を保存し始めたv2期間を表示する。
        private string FormatCountWithRate(int displayCount, int rateCount, int denominator)
        {
            var countStr = displayCount.ToString();
            if (denominator <= 0)
            {
                return countStr + " (--)";
            }

            // 小数1桁の通常の四捨五入。Mathf.Round は .5 を偶数側へ丸めるため、
            // 2/32=6.25% が 6.2% になるケースを避ける。
            var rate = Mathf.Floor((float)rateCount / denominator * 1000.0f + 0.5f) / 10.0f;
            return countStr + " (" + rate + "%)";
        }

        // 直近10戦（選択中タブのカテゴリ）：Dot0〜9 の縦位置を動かす。
        // 1位=最上段、以降 graphRankStep ずつ下がる。データが無い Dot は非表示。
        private void RenderRecentGraph()
        {
            RenderProfileAnalytics();
        }

        public void ProfileFewerGames()
        {
            profileTrendWindow_ = profileTrendWindow_ > 50 ? 50 : 10;
            RenderProfileAnalytics();
        }

        public void ProfileMoreGames()
        {
            profileTrendWindow_ = profileTrendWindow_ < 50 ? 50 : 100;
            RenderProfileAnalytics();
        }

        public void ProfileToggleStyleHelp()
        {
            if (profileStyleHelp != null) { profileStyleHelp.SetActive(!profileStyleHelp.activeSelf); }
        }

        // 率の丸めは右上の統計ブロック（FormatCountWithRate）と揃える。
        // 同じ分子・分母なのに「12.5%」と「12.50%」で見た目が割れるのを避ける。
        private string ProfilePercent(int count, int total)
        {
            if (total <= 0) { return "--"; }
            var rate = Mathf.Floor((float)count / total * 1000.0f + 0.5f) / 10.0f;
            return rate + "%";
        }

        // 役満の倍数表記。雀魂準拠で 1倍は「役満」、日本語の4倍以上は漢数字
        // （Mahjong.YakumanMultiplierLabel と揃える。ここだけ「1倍役満」にしない）。
        private string YakumanLabel(int count)
        {
            if (count <= 1) { return T("役満", "Yakuman", "역만", "役满", "役滿"); }
            if (lang_ == KemonoLanguage.LANG_EN) { return count + "x Yakuman"; }
            if (lang_ == KemonoLanguage.LANG_KO) { return count + "배 역만"; }

            // 日本語と中国語は漢数字（四〜九は簡体・繁体とも同じ字）。
            string n = count.ToString();
            if (count == 4) { n = "四"; }
            else if (count == 5) { n = "五"; }
            else if (count == 6) { n = "六"; }
            else if (count == 7) { n = "七"; }
            else if (count == 8) { n = "八"; }
            else if (count >= 9) { n = "九"; }
            if (lang_ == KemonoLanguage.LANG_ZHS) { return n + "倍役满"; }
            if (lang_ == KemonoLanguage.LANG_ZHT) { return n + "倍役滿"; }
            return n + "倍役満";
        }

        // ハイライトの表示枠（0=通常 / 1=イベント）。ローカルの見た目だけの状態。
        private int profileHighlightScope_ = 0;

        // 切替ボタン（BtnHighlightNormal / BtnHighlightEvent の onClick から呼ぶ）。
        public void ProfileHighlightNormal()
        {
            if (profileHighlightScope_ == 0) { return; }
            profileHighlightScope_ = 0;
            RenderProfileAnalytics();
        }

        public void ProfileHighlightEvent()
        {
            if (profileHighlightScope_ == 1) { return; }
            profileHighlightScope_ = 1;
            RenderProfileAnalytics();
        }

        private void RenderProfileAnalytics()
        {
            if (profileTrendDots == null || profileTrendDots.Length == 0 || statsStore == null) { return; }

            // イベントルールの段位戦で記録が出ている時だけ切替を出す。
            // 記録が無い人（＝ほとんどのプレイヤー）の画面は従来と同じ見た目のままにする。
            // フリー対局タブは分けていないので、ここも常に出ない（store 側が空を返す）。
            bool hasEvent = statsStore.HasProfileHighlightEvent(profileTab_);
            if (!hasEvent) { profileHighlightScope_ = 0; }
            if (profileHighlightScope != null && profileHighlightScope.activeSelf != hasEvent)
            {
                profileHighlightScope.SetActive(hasEvent);
            }
            if (profileHighlightScopeNormalImage != null)
            {
                profileHighlightScopeNormalImage.color =
                    profileHighlightScope_ == 0 ? tabSelectedColor_ : tabNormalColor_;
            }
            if (profileHighlightScopeEventImage != null)
            {
                profileHighlightScopeEventImage.color =
                    profileHighlightScope_ == 1 ? tabSelectedColor_ : tabNormalColor_;
            }

            string highlight = profileHighlightScope_ == 1
                ? statsStore.GetProfileHighlightEvent(profileTab_)
                : statsStore.GetProfileHighlight(profileTab_);

            RenderProfileData(statsStore.GetRecentPlacesCat(profileTab_), statsStore.GetProfileTotals(profileTab_),
                statsStore.ParseProfileNumbers(highlight), statsStore.IsRestored(),
                statsStore.GetLifetimeTotalsCat(profileTab_));
        }

        // places   : 直近の着順ログ（折れ線グラフ専用・最大100戦）
        // lifetime : 通算値 [0]=対局数 / [1..4]=1〜4位 / [5..7]=立直・副露・闇聴の和了数
        //            （順位グラフと和了グラフはこちら。totals はCSVの直近100戦分でスタイル専用）
        private void RenderProfileData(int[] places, int[] totals, int[] highlight, bool ready, int[] lifetime)
        {
            // 配線が1本でも欠けたらここで止める。null を触ると MainMenu の Udon ごと停止して
            // グラフだけでなくメニュー全体が死ぬ。
            if (places == null || totals == null || totals.Length < 12 || highlight == null) { return; }
            if (lifetime == null || lifetime.Length < 8) { return; }
            if (profileTrendDots == null || profileTrendLines == null ||
                profileRankRows == null || profileRankRows.Length < 4 ||
                profileWinSlices == null || profileWinSlices.Length < 3 ||
                profileWinLabels == null || profileWinLabels.Length < 3 ||
                profilePlaceBars == null || profilePlaceBars.Length < 4 ||
                profilePlaceLabels == null || profilePlaceLabels.Length < 4 ||
                profileRadar == null || profileRadar.Length < 4 ||
                profileHighlightTiles == null) { return; }

            int maxRank = profileTab_ == PlayerStatsStore.CAT_SANMA ? 3 : 4;
            int[] valid = new int[Mathf.Min(100, places.Length)];
            int n = 0;
            for (int i = Mathf.Max(0, places.Length - 100); i < places.Length; i++)
            {
                if (places[i] >= 1 && places[i] <= maxRank) { valid[n++] = places[i]; }
            }
            int shown = Mathf.Min(profileTrendWindow_, n);
            float stepY = 150f / (maxRank - 1);
            for (int i = 0; i < profileTrendDots.Length; i++)
            {
                bool visible = ready && i < shown;
                RectTransform dot = profileTrendDots[i];
                if (dot != null)
                {
                    dot.gameObject.SetActive(visible);
                    if (visible)
                    {
                        float x = shown <= 1 ? 0f : -740f + 1480f * i / (shown - 1);
                        dot.anchoredPosition = new Vector2(x, 75f - stepY * (valid[n - shown + i] - 1));
                    }
                }
                if (i < profileTrendLines.Length && profileTrendLines[i] != null)
                {
                    RectTransform line = profileTrendLines[i];
                    bool lineVisible = ready && i + 1 < shown;
                    line.gameObject.SetActive(lineVisible);
                    if (lineVisible)
                    {
                        float x0 = -740f + 1480f * i / (shown - 1);
                        float x1 = -740f + 1480f * (i + 1) / (shown - 1);
                        Vector2 a = new Vector2(x0, 75f - stepY * (valid[n - shown + i] - 1));
                        Vector2 b = new Vector2(x1, 75f - stepY * (valid[n - shown + i + 1] - 1));
                        Vector2 delta = b - a;
                        line.anchoredPosition = a;
                        line.sizeDelta = new Vector2(delta.magnitude, 4f);
                        line.localEulerAngles = new Vector3(0, 0, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
                    }
                }
            }
            for (int i = 0; i < profileRankRows.Length; i++)
            {
                if (profileRankRows[i] == null) { continue; }
                profileRankRows[i].SetActive(i < maxRank);
                if (i < maxRank)
                {
                    RectTransform row = (RectTransform)profileRankRows[i].transform;
                    row.anchoredPosition = new Vector2(0, 75f - stepY * i);
                }
            }
            if (profileTrendText != null)
            {
                profileTrendText.text = !ready ? T("データ復元中", "Restoring data", "데이터 복원 중", "数据恢复中", "資料恢復中") :
                    shown == 0 ? T("対戦記録なし", "No match records", "대전 기록 없음", "没有对战记录", "沒有對戰記錄") :
                    T("直近", "Last ", "최근 ", "最近", "最近") + profileTrendWindow_ + T("戦 / 記録 ", " games / Recorded ", "판 / 기록 ", "战 / 已记录 ", "戰 / 已記錄 ") + shown;
            }

            // スタイルの指標（攻・速・運）は直近100戦のCSVが分母。和了グラフとは母集団が違う。
            int wins = totals[2] + totals[3] + totals[4];
            // 和了グラフは通算。内訳を持つ前の和了は復元できないので、
            // 右上の「和了」（v2期間の総局数が分母）とは数字が一致しないことがある。
            int lifetimeWins = lifetime[5] + lifetime[6] + lifetime[7];
            int cumulative = 0;
            for (int i = 0; i < 3; i++)
            {
                cumulative += lifetime[5 + i];
                if (profileWinSlices[i] != null) { profileWinSlices[i].fillAmount = ready && lifetimeWins > 0 ? (float)cumulative / lifetimeWins : 0f; }
                if (profileWinLabels[i] != null) { profileWinLabels[i].text = ProfilePercent(lifetime[5 + i], ready ? lifetimeWins : 0); }
            }
            // 順位グラフは通算（右上の統計ブロックと同じ母集団）。
            // 直近ログは上の折れ線グラフだけが使うので、±ボタンでここの率は動かない。
            int lifetimeGames = lifetime[0];
            for (int i = 0; i < 4; i++)
            {
                if (profilePlaceBars[i] == null || profilePlaceLabels[i] == null) { continue; }
                int count = lifetime[i + 1];
                profilePlaceBars[i].parent.gameObject.SetActive(i < maxRank);
                profilePlaceBars[i].sizeDelta = new Vector2(ready && lifetimeGames > 0 ? 440f * count / lifetimeGames : 0f, 30f);
                profilePlaceLabels[i].text = ProfilePercent(count, ready ? lifetimeGames : 0);
                ((RectTransform)profilePlaceLabels[i].transform).anchoredPosition = new Vector2(116f + profilePlaceBars[i].sizeDelta.x, 0f);
            }

            // World-specific proxies, documented by the help button. No hidden Mahjong Soul formula.
            float attack = wins > 0 ? Mathf.Clamp(100f * totals[6] / wins / 12000f, 0, 100) : 0;
            float defense = totals[1] > 0 ? Mathf.Clamp(100f - 500f * totals[5] / totals[1], 0, 100) : 0;
            float speed = wins > 0 ? Mathf.Clamp(100f - 5f * totals[7] / wins, 0, 100) : 0;
            // 運：引きで乗った翻（ドラ・裏ドラ・赤ドラ・抜きドラ・一発）を和了数で割る。
            // 役満は1倍につき 8 翻分のボーナス。和了あたり平均 4 翻で 100。
            // 分母は totals[11]＝運の内訳を持つ行の和了数だけ（旧ログを混ぜると低く出る）。
            int luckWins = totals[11];
            float luck = luckWins > 0 ? Mathf.Clamp(25f * (totals[9] + 8f * totals[10]) / luckWins, 0, 100) : 0;
            float[] values = new float[] {luck, attack, speed, defense};
            for (int i = 0; i < 4; i++)
            {
                if (profileRadar[i] == null) { continue; }
                profileRadar[i].gameObject.SetActive(ready && totals[1] > 0);
                profileRadar[i].sizeDelta = new Vector2(values[i] * 1.25f, values[(i + 1) % 4] * 1.25f);
            }
            if (profileStyleText != null)
            {
                string a = ready && wins > 0 ? attack.ToString("F0") : "--";
                string d = ready && totals[1] > 0 ? defense.ToString("F0") : "--";
                string s = ready && wins > 0 ? speed.ToString("F0") : "--";
                string l = ready && luckWins > 0 ? luck.ToString("F0") : "--";
                profileStyleText.text = T("攻  ", "ATK  ", "공  ", "攻  ", "攻  ") + a + "\n" + T("防  ", "DEF  ", "방  ", "防  ", "防  ") + d +
                    "\n" + T("速  ", "SPD  ", "속  ", "速  ", "速  ") + s + "\n" + T("運  ", "LUK  ", "운  ", "运  ", "運  ") + l;
            }
            if (profileSampleText != null)
            {
                profileSampleText.text = !ready ? T("PlayerDataを復元しています", "Restoring PlayerData", "PlayerData를 복원하고 있습니다", "正在恢复 PlayerData", "正在恢復 PlayerData") :
                    T("順位：直近", "Placements: last ", "순위: 최근 ", "顺位：最近", "順位：最近") + n + T("戦 ｜ 和了・スタイル：導入後の直近", " games | Wins / style: ", "판 | 화료·스타일: 도입 후 최근 ", "战 ｜ 和牌・风格：导入后最近", "戰 ｜ 和牌・風格：導入後最近") +
                    totals[0] + T("戦・", " games / ", "판 · ", "战・", "戰・") + totals[1] + T("局・", " hands / ", "국 · ", "局・", "局・") + wins +
                    T("和了（最大100戦）", " wins (up to 100 games)", "화료 (최대 100판)", "和牌（最多100战）", "和牌（最多100戰）");
            }
            RenderProfileHighlight(highlight, ready);
        }

        private void RenderProfileHighlight(int[] data, bool ready)
        {
            if (data == null || profileHighlightTiles == null) { return; }
            bool exists = ready && data.Length >= 7 && data[0] > 0;
            for (int i = 0; i < profileHighlightTiles.Length; i++)
            {
                if (profileHighlightTiles[i] != null) { profileHighlightTiles[i].gameObject.SetActive(false); }
            }
            if (profileHighlightEmpty != null)
            {
                profileHighlightEmpty.gameObject.SetActive(!exists);
                profileHighlightEmpty.text = ready ? T("記録なし — 対局を完了すると最高打点の和了を保存します", "No record — complete a match to save your best winning hand", "기록 없음 — 대국을 완료하면 최고 점수의 화료를 저장합니다", "没有记录 — 完成对局后会保存最高打点的和牌", "沒有記錄 — 完成對局後會儲存最高打點的和牌") : T("データ復元中", "Restoring data", "데이터 복원 중", "数据恢复中", "資料恢復中");
            }
            if (profileHighlightText != null)
            {
                string rank = "";
                if (exists)
                {
                    rank = data[3] >= 1
                        ? YakumanLabel(data[3])
                        : data[1] + T("翻 ", " han ", "판 ", "番 ", "番 ") + data[2] + T("符", " fu", "부", "符", "符");
                }
                profileHighlightText.text = exists ? data[0] + T("点  ", " pts  ", "점  ", "点  ", "點  ") + rank : "";
            }
            if (!exists) { return; }
            float x = 0;
            int slot = 0;
            int closed = Mathf.Clamp(data[4], 0, 13);
            for (int i = 0; i < closed && 6 + i < data.Length; i++)
            {
                DrawProfileTile(slot++, data[6 + i], x);
                x += 68f;
            }
            x += 16f;
            DrawProfileTile(slot++, data[5], x);
            x += 86f;
            for (int i = 6 + closed; i < data.Length && slot < profileHighlightTiles.Length; i++)
            {
                if (data[i] == -2) { x += 20f; continue; }
                DrawProfileTile(slot++, data[i], x);
                x += 68f;
            }
        }

        private void DrawProfileTile(int slot, int code, float x)
        {
            if (slot < 0 || slot >= profileHighlightTiles.Length || profileHighlightTiles[slot] == null
                || code < 0 || code > 36) { return; }
            int col = 0;
            int row = 0;
            if (code < 9) { col = code; row = 5; }
            else if (code < 18) { col = code - 9; row = 3; }
            else if (code < 27) { col = code - 18; row = 4; }
            else if (code < 34) { col = code - 27; row = 2; }
            else if (code == 34) { col = 8; row = 2; }
            else if (code == 35) { col = 8; row = 1; }
            else { col = 7; row = 2; }
            RawImage tile = profileHighlightTiles[slot];
            tile.rectTransform.anchoredPosition = new Vector2(x, 0);
            tile.uvRect = new Rect((profileAtlasX_[col] + 2f) / 2048f,
                1f - (profileAtlasY_[row + 1] - 2f) / 2048f,
                (profileAtlasX_[col + 1] - profileAtlasX_[col] - 4f) / 2048f,
                (profileAtlasY_[row + 1] - profileAtlasY_[row] - 4f) / 2048f);
            tile.gameObject.SetActive(true);
        }

        // キャラ選択画面：選択中バッジと詳細（ポートレート＋能力説明）を更新する。
        private void RenderChara()
        {
            int selected = PlayerStatsStore.CHARA_VOL;
            if (statsStore != null)
            {
                selected = statsStore.GetChara();
            }
            if (selected < 0 || selected >= PlayerStatsStore.NUM_CHARAS) { selected = PlayerStatsStore.CHARA_VOL; }

            // 能力スイッチのラベル（能力キット改定中も スイッチ自体は仕様確定済み）。
            if (abilityToggleLabel != null)
            {
                bool on = statsStore != null && statsStore.GetAbilityOn();
                abilityToggleLabel.text = on
                    ? T("能力：ON", "Abilities: ON", "능력: ON", "能力：ON", "能力：ON")
                    : T("能力：OFF", "Abilities: OFF", "능력: OFF", "能力：OFF", "能力：OFF");
            }

            if (charaSelectedBadges != null)
            {
                for (int i = 0; i < charaSelectedBadges.Length; ++i)
                {
                    if (charaSelectedBadges[i] != null)
                    {
                        charaSelectedBadges[i].SetActive(i == selected);
                    }
                }
            }

            if (charaDetailText != null && charaDescs_ != null && selected < charaDescs_.Length)
            {
                charaDetailText.text = charaDescs_[selected];
            }

            if (charaDetailPortrait != null)
            {
                ApplyPortraitTo(charaDetailPortrait, GetCharaPortrait(selected));
            }

            RenderCharaLocks();
            RenderChunBeamToggle(selected);
        }

        // ロック表示。今のところ自画像だけが解禁条件を持つ。
        private void RenderCharaLocks()
        {
            bool selfieLocked = statsStore == null || !statsStore.IsSelfieUnlocked();

            string lockText = "";
            if (selfieLocked && statsStore != null)
            {
                string rank = statsStore.GetSelfieUnlockRankName();
                lockText = T(rank + " で解禁", "Unlocks at " + rank, rank + "에서 해금", "达到 " + rank + " 解锁", "達到 " + rank + " 解鎖");
            }

            if (charaLockOverlays != null)
            {
                for (int i = 0; i < charaLockOverlays.Length; ++i)
                {
                    if (charaLockOverlays[i] == null) { continue; }
                    bool locked = (i == PlayerStatsStore.CHARA_SELFIE) && selfieLocked;
                    charaLockOverlays[i].SetActive(locked);
                }
            }

            if (charaLockLabels != null)
            {
                for (int i = 0; i < charaLockLabels.Length; ++i)
                {
                    if (charaLockLabels[i] == null) { continue; }
                    charaLockLabels[i].text = (i == PlayerStatsStore.CHARA_SELFIE) ? lockText : "";
                }
            }

            UpdateSelfieCardHint();
        }

        // キャラIDからポートレートを解決する。
        // 自画像(3)だけは静的テクスチャを持たず、撮影ブースの RenderTexture を使う。
        // 未撮影ならプレースホルダ（無ければ null＝表示しない）。
        private Texture GetCharaPortrait(int chara)
        {
            if (chara == PlayerStatsStore.CHARA_SELFIE)
            {
                if (selfieCamera != null)
                {
                    // 今回のセッションで撮っていれば高解像度、
                    // 撮っていなければ PlayerData に残っている共有版へ落ちる。
                    var mine = selfieCamera.GetPortrait();
                    if (mine != null) { return mine; }
                }
                return selfiePlaceholder;
            }

            if (charaPortraits != null && chara >= 0 && chara < charaPortraits.Length)
            {
                return charaPortraits[chara];
            }
            return null;
        }

        // 装飾品画面の描画。カタログ（fxCatalog）の枚数ぶんだけカードを出し、
        // 余ったカードは非表示にする＝コスメを増やしてもUI側の再ビルドは不要。
        private string CosmeticColorName(int id)
        {
            if (cosmeticCategory_ == 2)
            {
                if (id == 0) return T("深緑", "Forest green", "짙은 녹색", "深绿", "深綠");
                if (id == 1) return T("青緑", "Teal", "청록색", "青绿", "青綠");
                if (id == 2) return T("藍色", "Indigo", "남색", "靛蓝", "靛藍");
                if (id == 3) return T("スレートブルー", "Slate blue", "슬레이트 블루", "石板蓝", "石板藍");
                if (id == 4) return T("オリーブ", "Olive", "올리브", "橄榄绿", "橄欖綠");
                if (id == 5) return T("葡萄色", "Grape", "포도색", "葡萄紫", "葡萄紫");
                return T("ウォームグレー", "Warm gray", "웜 그레이", "暖灰", "暖灰");
            }
            if (id == 0) return T("山吹色", "Yamabuki", "황금색", "山吹黄", "山吹黃");
            if (id == 1) return T("深緑", "Forest green", "짙은 녹색", "深绿", "深綠");
            if (id == 2) return T("紺青", "Deep blue", "감청색", "绀青", "紺青");
            if (id == 3) return T("朱赤", "Vermilion", "주홍색", "朱红", "朱紅");
            if (id == 4) return T("藤紫", "Wisteria", "등나무색", "藤紫", "藤紫");
            if (id == 5) return T("桜色", "Cherry blossom", "벚꽃색", "樱花色", "櫻花色");
            return T("墨色", "Sumi", "먹색", "墨色", "墨色");
        }

        private string RiichiBgmName(int track)
        {
            return track == 1 ? "Whistle in the Dark" : track == 2 ? "ROGLIK01" : T("なし", "None", "없음", "无", "無");
        }

        private void RenderCosmetic()
        {
            if (cosmeticCategoryTitle != null)
                cosmeticCategoryTitle.text = cosmeticCategory_ == 0 ? T("和了エフェクト", "Win effect", "화료 이펙트", "和牌特效", "和牌特效")
                    : cosmeticCategory_ == 1 ? T("麻雀牌の背面（ローカル）", "Tile backs (local)", "마작패 뒷면 (로컬)", "麻将牌背面（本地）", "麻將牌背面（本地）")
                    : cosmeticCategory_ == 3 ? T("立直BGM", "Riichi BGM", "리치 BGM", "立直BGM", "立直BGM") : T("麻雀卓の上面（ローカル）", "Table surface (local)", "마작 탁 윗면 (로컬)", "麻将桌桌面（本地）", "麻將桌桌面（本地）");
            if (cosmeticResetButton != null)
            {
                // 「元に戻す」が要るのは色の2タブだけ。色は 0＝青空麻雀v37.1の色が
                // どのカードにも無いので、このボタンが唯一の復帰手段になる。
                // 立直BGMは 0＝「なし」がカードとして並んでいる＝ボタンは同じことの重複。
                cosmeticResetButton.SetActive(cosmeticCategory_ == 1 || cosmeticCategory_ == 2);
                var resetLabel = cosmeticResetButton.GetComponentInChildren<TextMeshProUGUI>(true);
                if (resetLabel != null) resetLabel.text = T("元に戻す", "Reset", "되돌리기", "恢复默认", "恢復預設");
            }
            // 試写ボタンは和了エフェクトと立直BGMで出す（色の2タブには鳴らすものが無い）。
            // 文言はカテゴリで入れ替わるため T() で毎回書く＝BuildLanguage 側では DynamicPaths 扱い。
            if (cosmeticPreviewButton != null)
            {
                cosmeticPreviewButton.SetActive(cosmeticCategory_ == 0 || cosmeticCategory_ == 3);
                var previewLabel = cosmeticPreviewButton.GetComponentInChildren<TextMeshProUGUI>(true);
                if (previewLabel != null)
                {
                    previewLabel.text = cosmeticCategory_ != 3 ? T("試写", "Preview", "미리보기", "预览", "預覽")
                        : IsRiichiBgmPreviewPlaying() ? T("停止", "Stop", "정지", "停止", "停止") : T("試聴", "Play", "미리 듣기", "试听", "試聽");
                }
            }
            if (cosmeticNote != null) cosmeticNote.text = cosmeticCategory_ == 0
                ? T("選んだ和了演出は、あなたがロン／ツモしたときに全員へ表示されます。", "Your win effect is visible to everyone when you win.", "선택한 화료 연출은 내가 론/쯔모했을 때 모두에게 표시됩니다.", "所选的和牌演出会在你荣和／自摸时显示给所有人。", "所選的和牌演出會在你榮和／自摸時顯示給所有人。")
                : cosmeticCategory_ == 3 ? (statsStore != null && !statsStore.IsRiichiBgmUnlocked()
                    ? T("試聴は解禁前でもできます。", "You can listen before unlocking.", "미리 듣기는 해금 전에도 할 수 있습니다.", "解锁前也可以试听。", "解鎖前也可以試聽。")
                    : T("立直したとき、卓の範囲内の全員に再生されます。後続の立直で曲が切り替わります。", "Played to everyone near the table. The next riichi replaces the music.", "리치했을 때 탁 주변의 모든 사람에게 재생됩니다. 다음 리치로 곡이 바뀝니다.", "立直时会向牌桌附近的所有人播放。之后有人立直会切换曲目。", "立直時會向牌桌附近的所有人播放。之後有人立直會切換曲目。"))
                : T("色は自分にだけ反映されます。選択は保存され、次回入室時に復元されます。", "Colors are local to you. Your selection is saved for your next visit.", "색은 자신에게만 적용됩니다. 선택은 저장되어 다음 입장 때 복원됩니다.", "颜色只对你自己生效。所选内容会保存，下次进入时恢复。", "顏色只對你自己生效。所選內容會儲存，下次進入時恢復。");
            if (cosmeticCategoryImages != null)
                for (int i = 0; i < cosmeticCategoryImages.Length; ++i)
                    if (cosmeticCategoryImages[i] != null) cosmeticCategoryImages[i].color =
                        i == cosmeticCategory_ ? new Color(1f, 0.843f, 0f, 1f) : new Color(1f, 0.996f, 0.976f, 1f);
            int count = CosmeticCardCount();

            int selected = statsStore != null ? statsStore.GetAgariFx() : 0;
            if (statsStore != null && cosmeticCategory_ != 0)
            {
                int saved = cosmeticCategory_ == 1 ? statsStore.GetTileBackColor() : statsStore.GetTableTopColor();
                selected = saved > 0 ? saved - 1 : -1;
            }
            if (cosmeticCategory_ == 3) { selected = statsStore != null ? statsStore.GetRiichiBgm() : 0; }
            if (selected >= count || (selected < 0 && cosmeticCategory_ == 0)) { selected = 0; }

            int cards = fxCardRoots != null ? fxCardRoots.Length : 0;
            for (int i = 0; i < cards; ++i)
            {
                bool has = i < count;

                if (fxCardRoots[i] != null) { fxCardRoots[i].SetActive(has); }
                if (!has) { continue; }

                if (fxCardNames != null && i < fxCardNames.Length && fxCardNames[i] != null)
                {
                    fxCardNames[i].text = cosmeticCategory_ == 3 ? RiichiBgmName(i) : cosmeticCategory_ == 0 ? fxCatalog.GetVariantName(i) : CosmeticColorName(i);
                }

                if (fxCardSwatches != null && i < fxCardSwatches.Length && fxCardSwatches[i] != null)
                {
                    var c = cosmeticCategory_ == 2 ? GetTableTopColor(i)
                        : cosmeticCategory_ == 1 ? GetTileBackColor(i) : cosmeticCategory_ == 3 ? new Color(.15f, .5f, .5f) : fxCatalog.GetVariantMainColor(i);
                    fxCardSwatches[i].color = new Color(c.r, c.g, c.b, 1.0f);
                }
            }

            if (fxSelectedBadges != null)
            {
                for (int i = 0; i < fxSelectedBadges.Length; ++i)
                {
                    if (fxSelectedBadges[i] != null)
                    {
                        fxSelectedBadges[i].SetActive(i < count && i == selected);
                    }
                }
            }

            // ロック暗幕（いまは立直BGMの曲だけ）。カード0＝「なし」は常に選べるので暗幕を出さない。
            // 暗幕は raycastTarget を切ってあるので、押すこと自体は通る＝解禁前でも試聴はできる。
            bool bgmLocked = cosmeticCategory_ == 3 && (statsStore == null || !statsStore.IsRiichiBgmUnlocked());
            string bgmLockText = bgmLocked && statsStore != null
                ? T(statsStore.GetRiichiBgmUnlockRankName() + " で解禁", "Unlocks at " + statsStore.GetRiichiBgmUnlockRankName(), statsStore.GetRiichiBgmUnlockRankName() + "에서 해금", "达到 " + statsStore.GetRiichiBgmUnlockRankName() + " 解锁", "達到 " + statsStore.GetRiichiBgmUnlockRankName() + " 解鎖")
                : "";
            if (fxCardLockOverlays != null)
            {
                for (int i = 0; i < fxCardLockOverlays.Length; ++i)
                {
                    if (fxCardLockOverlays[i] == null) { continue; }
                    fxCardLockOverlays[i].SetActive(bgmLocked && i > 0 && i < count);
                }
            }
            if (fxCardLockLabels != null)
            {
                for (int i = 0; i < fxCardLockLabels.Length; ++i)
                {
                    if (fxCardLockLabels[i] == null) { continue; }
                    fxCardLockLabels[i].text = bgmLocked && i > 0 ? bgmLockText : "";
                }
            }

            // 立直BGMだけは「装備中（バッジ）」と「詳細欄に出すカード」が分かれる。
            // 解禁前はカードを押しても装備が動かないので、詳細欄は押したカードに追従させないと
            // 何を試聴しているのか分からなくなる。
            if (cosmeticCategory_ == 3 && (riichiBgmFocus_ < 0 || riichiBgmFocus_ >= count)) { riichiBgmFocus_ = selected; }
            int detailIndex = cosmeticCategory_ == 3 ? riichiBgmFocus_ : selected;

            if (fxDetailName != null)
            {
                fxDetailName.text = cosmeticCategory_ == 3 ? RiichiBgmName(detailIndex) : selected < 0 ? T("元の色", "Original", "원래 색", "原本的颜色", "原本的顏色") : count > 0 ? (cosmeticCategory_ == 0 ? fxCatalog.GetVariantName(selected) : CosmeticColorName(selected)) : "";
            }
            if (fxDetailDesc != null)
            {
                // 立直BGMの説明文は「なし」か作曲者の2通り。解禁条件はカードの暗幕が出すので重ねない。
                string bgmDesc = detailIndex == 0
                    ? T("立直BGMを再生しません。", "No riichi music.", "리치 BGM을 재생하지 않습니다.", "不播放立直BGM。", "不播放立直BGM。")
                    : T("作曲：Tsumuntema", "Composer: Tsumuntema", "작곡: Tsumuntema", "作曲：Tsumuntema", "作曲：Tsumuntema");

                fxDetailDesc.text = cosmeticCategory_ == 3 ? bgmDesc : selected < 0 ? T("青空麻雀v37.1の色", "Aozora Mahjong v37.1 color", "아오조라 마작 v37.1의 색", "青空麻雀v37.1的颜色", "青空麻雀v37.1的顏色") : count > 0
                    ? (cosmeticCategory_ == 0 ? fxCatalog.GetVariantDesc(selected) : T("選択した色", "Selected color", "선택한 색", "所选颜色", "所選顏色"))
                    : T("和了演出が設定されていません。", "No win effect is selected.", "화료 연출이 설정되어 있지 않습니다.", "未设置和牌演出。", "未設定和牌演出。");
            }
        }

        // カードのポートレートを反映（未割り当てはそのまま）。
        // 自画像カードは撮影の有無で中身が変わるので、撮影・決定のたびに呼び直す。
        private void ApplyPortraits()
        {
            if (charaCardPortraits == null) { return; }

            for (int i = 0; i < charaCardPortraits.Length; ++i)
            {
                if (charaCardPortraits[i] == null) { continue; }

                var tex = GetCharaPortrait(i);

                // 素材が一時的に見つからない既存キャラは、既に入っている絵を残す
                // （ここで潰すと Wire のやり直しまで空欄になる）。
                if (tex == null && i != PlayerStatsStore.CHARA_SELFIE) { continue; }

                ApplyPortraitTo(charaCardPortraits[i], tex);
            }

            UpdateSelfieCardHint();
        }

        // 「写真をとる」案内は、まだ撮っていなくて、かつ解禁済みのときだけ出す。
        // ロック中に出すと解禁文言と重なって両方読めなくなる。
        private void UpdateSelfieCardHint()
        {
            if (selfieCardHint == null) { return; }

            bool hasPhoto = selfieCamera != null && selfieCamera.HasAnyPortrait();
            bool unlocked = statsStore != null && statsStore.IsSelfieUnlocked();
            selfieCardHint.SetActive(unlocked && !hasPhoto);
        }

        // ポートレート枠へテクスチャを反映する。未撮影（null）のときは真っ白な板ではなく
        // 「空きスロット」として暗く塗る。
        private void ApplyPortraitTo(RawImage target, Texture tex)
        {
            target.texture = tex;
            target.color = (tex != null) ? Color.white : emptyPortraitColor;
            target.raycastTarget = false;
        }
    

// 画面フェード用に「1枚ずつ色を塗る Graphic」を集める。
//  ・非アクティブは描かれていないので触らない（塗ると Canvas が無駄に作り直される）
//  ・CanvasGroup が付いた枝は配下ごと除外し、CacheScreenGroups が alpha 1本で畳む
//    （プロフィール分析は Graphic が300個超あり、毎フレーム色を書くと Quest で刺さる）
private int CacheScreenItems(GameObject screen, Graphic[] graphics, Color[] colors)
{
    Graphic[] foundGraphics = screen.GetComponentsInChildren<Graphic>(true);
    Transform screenRoot = screen.transform;
    int count = 0;

    for (int i = 0; i < foundGraphics.Length && count < SCREEN_GRAPHIC_MAX; i++)
    {
        Graphic graphic = foundGraphics[i];
        if (graphic == null || !graphic.gameObject.activeInHierarchy) continue;
        if (IsUnderFadeGroup(graphic.transform, screenRoot)) continue;

        graphics[count] = graphic;
        colors[count] = graphic.color;
        count++;
    }

    return count;
}


// CanvasGroup.alpha は CanvasRenderer の alpha だけを触るので、Graphic.color と違って
// 頂点の作り直しが起きない＝まとめて畳むほど安い。入れ子の CanvasGroup は
// alpha が掛け算になるので外側だけを採る。
private int CacheScreenGroups(GameObject screen, CanvasGroup[] groups)
{
    CanvasGroup[] found = screen.GetComponentsInChildren<CanvasGroup>(true);
    Transform screenRoot = screen.transform;
    int count = 0;

    for (int i = 0; i < found.Length && count < SCREEN_GROUP_MAX; i++)
    {
        CanvasGroup group = found[i];
        if (group == null) continue;
        if (IsUnderFadeGroup(group.transform.parent, screenRoot)) continue;

        groups[count] = group;
        count++;
    }

    return count;
}


private bool IsUnderFadeGroup(Transform target, Transform screenRoot)
{
    for (Transform p = target; p != null; p = p.parent)
    {
        if (p.GetComponent<CanvasGroup>() != null) return true;
        if (p == screenRoot) break;
    }

    return false;
}


private void SetScreenGroupAlpha(CanvasGroup[] groups, int count, float alpha)
{
    for (int i = 0; i < count; i++)
    {
        CanvasGroup group = groups[i];
        if (group == null) continue;

        group.alpha = alpha;
    }
}


private void UpdateScreenTransition()
{
    bool outgoingFinished = transitionFrom_ == null || !transitionFrom_.activeSelf;

    if (!outgoingFinished)
    {
        if (screenTransitionTime_ < SCREEN_FADE_OUT_SECONDS)
        {
            AnimateOutgoingScreen(Mathf.Clamp01(screenTransitionTime_ / SCREEN_FADE_OUT_SECONDS));
        }
        else
        {
            RestoreScreenItems(screenFromGraphics_, screenFromColors_, screenFromCount_);
            SetScreenGroupAlpha(screenFromGroups_, screenFromGroupCount_, 1.0f);
            transitionFrom_.SetActive(false);
            outgoingFinished = true;
        }
    }

    if (screenTransitionTime_ >= SCREEN_INCOMING_DELAY_SECONDS && !screenIncomingStarted_)
    {
        transitionTo_.SetActive(true);
        screenToCount_ = CacheScreenItems(transitionTo_, screenToGraphics_, screenToColors_);
        screenToGroupCount_ = CacheScreenGroups(transitionTo_, screenToGroups_);
        PrepareIncomingScreen();
        screenIncomingStarted_ = true;
    }

    if (!screenIncomingStarted_) return;

    float incomingElapsed = screenTransitionTime_ - SCREEN_INCOMING_DELAY_SECONDS;
    AnimateIncomingScreen(incomingElapsed);

    if (outgoingFinished && incomingElapsed >= SCREEN_FADE_IN_SECONDS)
    {
        FinishScreenTransition();
    }
}


private void BeginScreenTransition(GameObject target)
{
    if (target == null) return;
    // 装飾品画面を離れたら試聴を止める（ホームへ戻っても鳴り続けない）。
    if (target != screenCosmetic) { StopRiichiBgmPreview(); }
    if (!screenTransitionInitialized_) InitializeScreenTransition();
    if (screenTransitionActive_) CompleteScreenTransitionImmediately();
    if (currentScreen_ == target && target.activeSelf) return;

    transitionFrom_ = currentScreen_;
    transitionTo_ = target;
    screenTransitionTime_ = 0.0f;
    screenIncomingStarted_ = false;
    screenFromCount_ = 0;
    screenToCount_ = 0;
    screenFromGroupCount_ = 0;
    screenToGroupCount_ = 0;

    if (transitionFrom_ != null && transitionFrom_.activeSelf)
    {
        screenFromCount_ = CacheScreenItems(transitionFrom_, screenFromGraphics_, screenFromColors_);
        screenFromGroupCount_ = CacheScreenGroups(transitionFrom_, screenFromGroups_);
    }
    else
    {
        screenTransitionTime_ = SCREEN_INCOMING_DELAY_SECONDS;
    }

    if (menuRaycaster_ != null) menuRaycaster_.enabled = false;
    screenTransitionActive_ = true;
}


private void Update()
{
    // 立直BGMの試聴は画面遷移と無関係に進むので、早期 return の前に置く。
    UpdateRiichiBgmPreview();

    if (!screenTransitionActive_) return;

    screenTransitionTime_ += Time.deltaTime;
    UpdateScreenTransition();
}


private void InitializeScreenTransition()
{
    if (screenTransitionInitialized_) return;

    screenFromGraphics_ = new Graphic[SCREEN_GRAPHIC_MAX];
    screenToGraphics_ = new Graphic[SCREEN_GRAPHIC_MAX];
    screenFromColors_ = new Color[SCREEN_GRAPHIC_MAX];
    screenToColors_ = new Color[SCREEN_GRAPHIC_MAX];
    screenFromGroups_ = new CanvasGroup[SCREEN_GROUP_MAX];
    screenToGroups_ = new CanvasGroup[SCREEN_GROUP_MAX];

    menuRaycaster_ = GetComponent<GraphicRaycaster>();
    currentScreen_ = null;
    screenTransitionInitialized_ = true;
    HideAllScreens();
}


private void CompleteScreenTransitionImmediately()
{
    RestoreScreenItems(screenFromGraphics_, screenFromColors_, screenFromCount_);
    SetScreenGroupAlpha(screenFromGroups_, screenFromGroupCount_, 1.0f);
    if (transitionFrom_ != null) transitionFrom_.SetActive(false);

    if (transitionTo_ != null)
    {
        transitionTo_.SetActive(true);

        if (screenIncomingStarted_)
        {
            RestoreScreenItems(screenToGraphics_, screenToColors_, screenToCount_);
            SetScreenGroupAlpha(screenToGroups_, screenToGroupCount_, 1.0f);
        }

        currentScreen_ = transitionTo_;
    }

    transitionFrom_ = null;
    transitionTo_ = null;
    screenTransitionActive_ = false;
    screenIncomingStarted_ = false;

    if (menuRaycaster_ != null) menuRaycaster_.enabled = true;
}


private void FinishScreenTransition()
{
    RestoreScreenItems(screenToGraphics_, screenToColors_, screenToCount_);
    SetScreenGroupAlpha(screenToGroups_, screenToGroupCount_, 1.0f);
    currentScreen_ = transitionTo_;
    transitionFrom_ = null;
    transitionTo_ = null;
    screenTransitionActive_ = false;
    screenIncomingStarted_ = false;

    if (menuRaycaster_ != null) menuRaycaster_.enabled = true;
}


private void RestoreScreenItems(Graphic[] graphics, Color[] colors, int count)
{
    for (int i = 0; i < count; i++)
    {
        Graphic graphic = graphics[i];
        if (graphic == null) continue;
        graphic.color = colors[i];
    }
}


private void AnimateIncomingScreen(float elapsed)
{
    float normalizedTime = Mathf.Clamp01(elapsed / SCREEN_FADE_IN_SECONDS);
    float eased = normalizedTime * normalizedTime * (3.0f - 2.0f * normalizedTime);
    SetScreenGraphicAlpha(screenToGraphics_, screenToColors_, screenToCount_, eased);
    SetScreenGroupAlpha(screenToGroups_, screenToGroupCount_, eased);
}


private void PrepareIncomingScreen()
{
    SetScreenGraphicAlpha(screenToGraphics_, screenToColors_, screenToCount_, 0.0f);
    SetScreenGroupAlpha(screenToGroups_, screenToGroupCount_, 0.0f);
}


private void AnimateOutgoingScreen(float normalizedTime)
{
    float eased = normalizedTime * normalizedTime * (3.0f - 2.0f * normalizedTime);
    SetScreenGraphicAlpha(screenFromGraphics_, screenFromColors_, screenFromCount_, 1.0f - eased);
    SetScreenGroupAlpha(screenFromGroups_, screenFromGroupCount_, 1.0f - eased);
}





private void SetScreenGraphicAlpha(Graphic[] graphics, Color[] colors, int count, float alphaMultiplier)
{
    for (int i = 0; i < count; i++)
    {
        Graphic graphic = graphics[i];
        if (graphic == null) continue;

        Color color = colors[i];
        color.a *= alphaMultiplier;
        graphic.color = color;
    }
}
}
}
