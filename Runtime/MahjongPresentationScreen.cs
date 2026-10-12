using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

/// <summary>
/// 対局開始・和了カットイン・局間点数精算をまとめた純ローカル表示。
/// Mahjong 本体の同期済み状態を読むだけで、同期変数や得点ロジックは持たない。
/// World Space Canvas 前提。常時アクティブな本Behaviourから子パネルだけを開閉する。
/// </summary>
namespace SuzuFactory.AozoraMahjong
{
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class MahjongPresentationScreen : UdonSharpBehaviour
{
    [Header("Link")]
    public Mahjong mahjong;

    [Tooltip("自画像キャラの共有ストア（任意）。割り当てると、自画像を選んでいる席は"
             + "その人が撮った写真を立ち絵として出す。未割当なら既定立ち絵に落ちる。")]
    public SelfiePortraitStore selfieStore;

    [Header("Win Cut-in")]
    public GameObject cutinPanelRoot;
    public CanvasGroup cutinCanvasGroup;
    public RectTransform cutinBandRect;
    public RectTransform cutinMascotRect;
    public RawImage cutinMascotImage;

    [Header("Cutin: ダブロン・トリロンの同時表示スロット")]
    [Tooltip("和了者の立ち絵（最大3人）。[0] は上の cutinMascotRect と同じものを入れる。")]
    public RectTransform[] cutinMascotRects;
    public RawImage[] cutinMascotImages;

    [Tooltip("立ち絵ごとの名前ラベル。2人以上のときだけ使い、1人のときは cutinPlayerText を使う。")]
    public Text[] cutinNameTexts;

    [Tooltip("2人／3人のときの立ち絵の間隔（px）。")]
    public float cutinMultiSpacing2 = 470.0f;
    public float cutinMultiSpacing3 = 415.0f;

    [Tooltip("2人／3人のときの立ち絵の縮小率。横に並べるぶん小さくする。")]
    public float cutinMultiScale2 = 0.82f;
    public float cutinMultiScale3 = 0.70f;

    [Tooltip("複数人のときの名前ラベルの高さ（px）。帯の中に収まる値にする。")]
    public float cutinNameY = -150.0f;
    public Text cutinCallText;
    public Text cutinPlayerText;
    public Image cutinBandImage;
    public Image cutinAccentImage;
    public Texture[] charaMascotTextures;
    public Texture defaultMascotTexture;

    [Tooltip("斜め帯の展開時間。動画基準は約0.2秒。")]
    public float cutinEnterSeconds = 0.20f;
    [Tooltip("展開後の保持時間。動画基準は約0.85秒。")]
    public float cutinHoldSeconds = 0.85f;
    [Tooltip("斜め帯の退場時間。動画基準は約0.2秒。")]
    public float cutinExitSeconds = 0.20f;
    public float cutinMascotSlide = 330.0f;

    [Header("Match Intro / Hand Settlement")]
    public GameObject settlementPanelRoot;
    public CanvasGroup settlementCanvasGroup;
    public Text settlementHeaderText;
    public Text settlementSubText;
    public GameObject[] settlementCardRoots;
    public CanvasGroup[] settlementCardCanvasGroups;
    public RectTransform[] settlementCardRects;
    public RawImage[] settlementPortraitImages;
    public Text[] settlementNameTexts;
    public Text[] settlementBeforeTexts;
    public Text[] settlementDeltaTexts;
    public Text[] settlementAfterTexts;
    public Image[] settlementCardBackgrounds;
    public Image[] settlementAccentImages;

    [Header("Settlement Rank / Arrows")]
    [Tooltip("順位（1位/2位/…）。カードごとに1つ。")]
    public Text[] settlementRankTexts;

    [Tooltip("点棒の流れを示す矢印。払う席から受け取る席へ引く。最大3本。")]
    public RawImage[] settlementArrowImages;
    public RectTransform[] settlementArrowRects;

    [Tooltip("矢印の太さ（px）。")]
    public float settlementArrowThickness = 52.0f;

    [Tooltip("矢印の色。")]
    public Color arrowColor = new Color(1.0f, 1.0f, 1.0f, 0.95f);

    [Tooltip("矢印の両端をカードに食い込ませない余白（px）。")]
    public float settlementArrowMargin = 210.0f;

    [Header("Settlement SFX")]
    [Tooltip("点数がカウントアップしている間に鳴らす音。未割当なら無音。")]
    public AudioClip scoreCountSound;

    [Tooltip("勝者カットイン（キャラ立ち絵＋ロン/ツモ）が出る瞬間に鳴らす音。未割当なら無音。")]
    public AudioClip winCutinSound;

    [Range(0.0f, 1.0f)]
    public float winCutinVolume = 0.253f;

    [Tooltip("SE再生用 AudioSource（常時アクティブな親側に置く）。未割当ならSEなし。")]
    public AudioSource seSource;

    [Range(0.0f, 1.0f)]
    public float seVolume = 0.567f;

    [Header("Settlement Layout（自分が手前になるよう席で並べる）")]
    [Tooltip("自分の席のカード位置。")]
    public Vector2 slotSelf = new Vector2(0.0f, -330.0f);

    [Tooltip("下家（自分の右）のカード位置。")]
    public Vector2 slotRight = new Vector2(660.0f, 10.0f);

    [Tooltip("対面のカード位置。三麻では使わない。")]
    public Vector2 slotTop = new Vector2(0.0f, 330.0f);

    [Tooltip("上家（自分の左）のカード位置。")]
    public Vector2 slotLeft = new Vector2(-660.0f, 10.0f);

    [Tooltip("精算カード1枚の入場時間。")]
    public float settlementEnterSeconds = 0.28f;
    [Tooltip("カードごとの入場ずらし。")]
    public float settlementCardStagger = 0.08f;
    [Tooltip("得点カウント時間。既存点数板より少し長く、視認しやすくする。")]
    public float settlementCountSeconds = 1.40f;
    [Tooltip("得点カウント後の保持。")]
    public float settlementHoldSeconds = 1.20f;
    [Tooltip("精算画面の退場時間。")]
    public float settlementExitSeconds = 0.30f;
    [Tooltip("対局開始カードの保持時間。")]
    public float matchIntroHoldSeconds = 2.40f;
    public float settlementSlideDistance = 430.0f;
    public float settlementTextInterval = 0.05f;

    [Header("Palette")]
    public Color navyColor = new Color(0.035f, 0.075f, 0.14f, 0.97f);
    public Color navyGainColor = new Color(0.035f, 0.16f, 0.15f, 0.98f);
    public Color navyLossColor = new Color(0.18f, 0.055f, 0.09f, 0.98f);
    public Color goldColor = new Color(1.0f, 0.64f, 0.18f, 1.0f);
    public Color gainColor = new Color(0.38f, 1.0f, 0.58f, 1.0f);
    public Color lossColor = new Color(1.0f, 0.38f, 0.42f, 1.0f);
    public Color neutralColor = new Color(0.80f, 0.88f, 0.94f, 1.0f);

    private float cutinStartTime_ = -1.0f;
    private float cardsStartTime_ = -1.0f;
    private float nextTextDrawTime_ = 0.0f;
    private bool introMode_ = false;
    private bool settlementQueued_ = false;

    private bool viewerRotationFollowing_ = false;
    private bool matchIntroArmed_ = false;
    private bool matchIntroShown_ = false;
    private int activeCardCount_ = 0;

    private int[] beforePoints_ = new int[4];
    private int[] afterPoints_ = new int[4];
    private int[] charaIds_ = new int[4];
    // 席ごとのプレイヤーID（自画像の引き当てにだけ使う。0＝CPU/不明）。
    private int[] charaPlayerIds_ = new int[4];
    private string[] playerNames_ = new string[4];
    private Vector2[] cardHomePositions_ = new Vector2[4];

    // カード位置のスロット割当（0=手前 1=右 2=対面 3=左）の作業領域。
    private int[] slotOfSeat_ = new int[4];
    private bool[] slotTaken_ = new bool[4];

    // 出せない状態（あがり画面表示中など）でキューを抱え続けると、次局が
    // 始まってから開いてしまう。これを過ぎた精算は捨てる。
    private const float SETTLEMENT_QUEUE_TIMEOUT = 12.0f;
    private float settlementQueueTime_ = -1.0f;

    private bool countSoundPlayed_ = false;
    private int[] cardRanks_ = new int[4];
    private int activeArrowCount_ = 0;

    // 矢印を「払う側から受け取る側へ伸ばす」ために、始点と向きと全長を控える。
    private Vector2[] arrowFrom_ = new Vector2[4];
    private Vector2[] arrowDir_ = new Vector2[4];
    private float[] arrowLen_ = new float[4];

    private bool layoutCached_ = false;
    private Vector2 cutinBandHome_;
    private Vector2 cutinMascotHome_;
    private Vector3 cutinBandScale_ = Vector3.one;
    private Vector3 cutinMascotScale_ = Vector3.one;

    // この和了で実際に使うスロットの位置・大きさ（人数で変わる）。
    private int cutinSlotCount_ = 1;
    private Vector2[] cutinSlotHome_ = new Vector2[3];
    private Vector3[] cutinSlotScale_ = new Vector3[3];
    private int[] cutinCharaBuf_ = new int[3];
    private int[] cutinPlayerIdBuf_ = new int[3];
    private string[] cutinNameBuf_ = new string[3];

    private void Start()
    {
        CacheLayout();
        HideCutinNow();
        HideSettlementNow();
    }

    private void CacheLayout()
    {
        if (layoutCached_) { return; }

        if (cutinBandRect != null)
        {
            cutinBandHome_ = cutinBandRect.anchoredPosition;
            cutinBandScale_ = cutinBandRect.localScale;
        }
        if (cutinMascotRect != null)
        {
            cutinMascotHome_ = cutinMascotRect.anchoredPosition;
            cutinMascotScale_ = cutinMascotRect.localScale;
        }

        layoutCached_ = true;
    }

private void PlaceForLocalViewer()
    {
        UpdateLocalViewerYaw(true);
    }

private void UpdateLocalViewerYaw(bool snap)
    {
        VRCPlayerApi localPlayer = Networking.LocalPlayer;
        if (localPlayer == null || !Utilities.IsValid(localPlayer)) { return; }

        VRCPlayerApi.TrackingData head =
            localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);

        Vector3 forward = transform.position - head.position;
        forward.y = 0.0f;

        if (forward.sqrMagnitude < 0.0001f)
        {
            forward = head.rotation * Vector3.forward;
            forward.y = 0.0f;
        }

        if (forward.sqrMagnitude < 0.0001f) { return; }
        forward = forward.normalized;

        Quaternion targetRotation = Quaternion.LookRotation(forward, Vector3.up);

        if (snap)
        {
            transform.rotation = targetRotation;
            viewerRotationFollowing_ = false;
            return;
        }

        float yawDelta = Quaternion.Angle(transform.rotation, targetRotation);
        if (!viewerRotationFollowing_ && yawDelta > 5.0f)
        {
            viewerRotationFollowing_ = true;
        }

        if (!viewerRotationFollowing_) { return; }

        float rotationBlend = Mathf.Clamp01(Time.deltaTime * 8.0f);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, rotationBlend);

        if (yawDelta < 0.75f)
        {
            transform.rotation = targetRotation;
            viewerRotationFollowing_ = false;
        }
    }


private void FollowLocalViewer()
    {
        UpdateLocalViewerYaw(false);
    }



    /// <summary>
    /// ENTRY_COMPLETEDを実際に見たクライアントだけを武装し、最初の対局状態で一度だけ紹介を出す。
    /// 途中参加・卓カリング復帰ではENTRYを見ていないため暴発しない。
    /// </summary>
    public void ObserveMatchStart(bool entryCompleted)
    {
        if (entryCompleted)
        {
            matchIntroArmed_ = true;
            matchIntroShown_ = false;
            return;
        }

        if (!matchIntroArmed_ || matchIntroShown_ || mahjong == null) { return; }

        matchIntroShown_ = true;
        matchIntroArmed_ = false;
        PlayMatchIntro();
    }

    public void PlayMatchIntro()
    {
        if (mahjong == null || settlementPanelRoot == null) { return; }

        int count = mahjong.Presentation_PlayerCount();
        if (count < 3 || count > 4) { return; }

        activeCardCount_ = count;
        introMode_ = true;

        for (int i = 0; i < 4; ++i)
        {
            beforePoints_[i] = mahjong.Presentation_SeatPoints(i);
            afterPoints_[i] = beforePoints_[i];
            charaIds_[i] = mahjong.Presentation_SeatChara(i);
            charaPlayerIds_[i] = mahjong.Presentation_SeatPlayerId(i);
            playerNames_[i] = mahjong.Presentation_SeatName(i);
        }

        if (settlementHeaderText != null) { settlementHeaderText.text = "対局開始"; }
        if (settlementSubText != null) { settlementSubText.text = "MATCH START"; }

        SetupCards();
        BeginCards();
    }

    /// <summary>
    /// 同期済み得点の前後値を受け、各クライアントで表示だけを補間する。
    /// 配列は即コピーするため、呼び出し元の次の更新に影響されない。
    /// </summary>
public void ShowSettlement(int[] before, int[] after, string[] names, int[] charas, int playerCount)
{
    if (settlementPanelRoot == null || before == null || after == null) { return; }

    int count = playerCount;
    if (count < 3 || count > 4) { count = 4; }

    activeCardCount_ = count;
    introMode_ = false;

    for (int i = 0; i < 4; ++i)
    {
        beforePoints_[i] = i < before.Length ? before[i] : 0;
        afterPoints_[i] = i < after.Length ? after[i] : beforePoints_[i];
        playerNames_[i] = names != null && i < names.Length && names[i] != null ? names[i] : "";
        charaIds_[i] = charas != null && i < charas.Length ? charas[i] : 0;
        // 自画像の引き当て用。卓が居れば席のプレイヤーIDを引き直す。
        charaPlayerIds_[i] = mahjong != null ? mahjong.Presentation_SeatPlayerId(i) : 0;
    }

    if (settlementHeaderText != null) { settlementHeaderText.text = "精算"; }
    if (settlementSubText != null) { settlementSubText.text = "SCORE TRANSFER"; }

    settlementQueued_ = true;
    settlementQueueTime_ = Time.time;
    TryBeginSettlement();
}

private void TryBeginSettlement()
{
    if (!settlementQueued_) { return; }

    if (mahjong != null && !mahjong.Presentation_CanShowSettlement())
    {
        // 待たされすぎた精算は捨てる。次局の途中で唐突に開くほうが害が大きい。
        if (settlementQueueTime_ >= 0.0f
            && Time.time - settlementQueueTime_ > SETTLEMENT_QUEUE_TIMEOUT)
        {
            settlementQueued_ = false;
            settlementQueueTime_ = -1.0f;
        }
        return;
    }

    settlementQueued_ = false;
    settlementQueueTime_ = -1.0f;
    SetupCards();
    BeginCards();
}


// 観戦モニター用：精算パネルの点数が数え始める時刻（Time.time 基準の絶対時刻）。精算を出していなければ -1
public float Spectate_SettlementCountStartTime()
{
    if (introMode_ || cardsStartTime_ < 0.0f || settlementPanelRoot == null || !settlementPanelRoot.activeSelf) { return -1.0f; }
    return cardsStartTime_ + Spectate_SettlementCountLead();
}

// 精算パネルを出してから数え始めるまでの秒数（カードが入り終わるまで）
public float Spectate_SettlementCountLead()
{
    float enter = Mathf.Max(0.01f, settlementEnterSeconds);
    float stagger = Mathf.Max(0.0f, settlementCardStagger);
    int cards = activeCardCount_ >= 3 && activeCardCount_ <= 4 ? activeCardCount_ : 4;
    return enter + stagger * (cards - 1);
}

public bool IsSettlementVisible()
{
    return settlementQueued_
        || (cardsStartTime_ >= 0.0f
            && settlementPanelRoot != null
            && settlementPanelRoot.activeSelf);
}

    public float GetWinCutinDuration()
    {
        if (cutinPanelRoot == null || cutinBandRect == null) { return 0.0f; }
        return Mathf.Max(0.0f, cutinEnterSeconds)
             + Mathf.Max(0.0f, cutinHoldSeconds)
             + Mathf.Max(0.0f, cutinExitSeconds);
    }

    // 単独和了（ツモ・頭ハネ）用。中身は複数版に委譲する。
    public void PlayWinCutin(int charaId, bool isTsumo, string playerName)
    {
        PlayWinCutinAs(charaId, 0, isTsumo, playerName);
    }

    // 自画像キャラ対応版。playerId は和了者のプレイヤーID（0＝CPU/不明）。
    public void PlayWinCutinAs(int charaId, int playerId, bool isTsumo, string playerName)
    {
        cutinCharaBuf_[0] = charaId;
        cutinPlayerIdBuf_[0] = playerId;
        cutinNameBuf_[0] = playerName;
        PlayWinCutinMultiAs(cutinCharaBuf_, cutinPlayerIdBuf_, cutinNameBuf_, 1, isTsumo);
    }

    /// <summary>
    /// 勝者カットイン。ダブロン・トリロンは和了者を横並びで同時に出す。
    /// 1人のときは従来どおり（立ち絵ひとつ＋右に「ロン/ツモ」＋その下に名前）。
    /// </summary>
    public void PlayWinCutinMulti(int[] charaIds, string[] names, int count, bool isTsumo)
    {
        PlayWinCutinMultiAs(charaIds, null, names, count, isTsumo);
    }

    /// <summary>
    /// 勝者カットイン（自画像キャラ対応版）。
    /// playerIds は charaIds と同じ並びの和了者プレイヤーID。null なら従来動作。
    /// </summary>
    public void PlayWinCutinMultiAs(int[] charaIds, int[] playerIds, string[] names, int count, bool isTsumo)
    {
        cutinPlayerIds_ = playerIds;

        if (cutinPanelRoot == null || cutinBandRect == null) { return; }

        int n = count;
        if (n < 1) { n = 1; }
        if (n > 3) { n = 3; }

        // カットインの効果音。ここで鳴らせば呼び出し元が増えても必ず絵と揃う。
        if (seSource != null && winCutinSound != null)
        {
            seSource.PlayOneShot(winCutinSound, winCutinVolume);
        }

        CacheLayout();

        cutinSlotCount_ = n;

        // 人数に応じて間隔と縮小率を決め、立ち絵ひとつぶんの定位置を中心に左右へ振り分ける。
        float spacing = n >= 3 ? cutinMultiSpacing3 : (n == 2 ? cutinMultiSpacing2 : 0.0f);
        float scale = n >= 3 ? cutinMultiScale3 : (n == 2 ? cutinMultiScale2 : 1.0f);

        for (int i = 0; i < 3; ++i)
        {
            bool used = i < n;

            RectTransform rect = (cutinMascotRects != null && i < cutinMascotRects.Length) ? cutinMascotRects[i] : null;
            RawImage img = (cutinMascotImages != null && i < cutinMascotImages.Length) ? cutinMascotImages[i] : null;
            Text nameText = (cutinNameTexts != null && i < cutinNameTexts.Length) ? cutinNameTexts[i] : null;

            if (used)
            {
                float offset = (i - (n - 1) * 0.5f) * spacing;
                cutinSlotHome_[i] = cutinMascotHome_ + new Vector2(offset, 0.0f);
                cutinSlotScale_[i] = cutinMascotScale_ * scale;
            }

            if (img != null)
            {
                if (used)
                {
                    int pid = (cutinPlayerIds_ != null && i < cutinPlayerIds_.Length) ? cutinPlayerIds_[i] : 0;
                    img.texture = ResolveMascot(charaIds[i], pid);
                }
                img.gameObject.SetActive(used);
            }

            // 名前は2人以上のときだけ立ち絵の下に出す（1人なら従来の cutinPlayerText）。
            if (nameText != null)
            {
                bool showName = used && n >= 2;
                if (showName)
                {
                    nameText.text = names[i] == null ? "" : names[i];
                    nameText.rectTransform.anchoredPosition =
                        new Vector2(cutinSlotHome_[i].x, cutinNameY);
                }
                nameText.gameObject.SetActive(showName);
            }
        }

        if (cutinCallText != null)
        {
            cutinCallText.text = isTsumo ? "ツモ" : "ロン";
            cutinCallText.color = isTsumo
                ? new Color(0.40f, 0.92f, 1.0f, 1.0f)
                : new Color(1.0f, 0.30f, 0.34f, 1.0f);
            cutinCallText.gameObject.SetActive(true);
        }
        if (cutinPlayerText != null)
        {
            // 1人のときだけ従来位置に名前を出す。複数のときは立ち絵ごとのラベルに任せる。
            bool single = n == 1;
            if (single) { cutinPlayerText.text = names[0] == null ? "" : names[0]; }
            cutinPlayerText.gameObject.SetActive(single);
        }

        // 帯の色は先頭の和了者のキャラ色。複数人だと1色に決められないので、
        // 並びの左端（席順で最初の和了者）に合わせる。
        Color band = CharaColor(charaIds[0]);
        if (cutinBandImage != null) { cutinBandImage.color = band; }
        if (cutinAccentImage != null) { cutinAccentImage.color = goldColor; }

        cutinBandRect.localScale = new Vector3(cutinBandScale_.x * 0.04f, cutinBandScale_.y, cutinBandScale_.z);
        cutinBandRect.anchoredPosition = cutinBandHome_;

        for (int i = 0; i < cutinSlotCount_; ++i)
        {
            RectTransform rect = (cutinMascotRects != null && i < cutinMascotRects.Length) ? cutinMascotRects[i] : null;
            if (rect == null) { continue; }

            rect.localScale = cutinSlotScale_[i] * 0.96f;
            rect.anchoredPosition = cutinSlotHome_[i] + new Vector2(-cutinMascotSlide, -36.0f);
        }

        if (cutinCanvasGroup != null)
        {
            cutinCanvasGroup.alpha = 0.0f;
            cutinCanvasGroup.interactable = false;
            cutinCanvasGroup.blocksRaycasts = false;
        }

        PlaceForLocalViewer();
        cutinPanelRoot.transform.localPosition = Vector3.down * 0.20f;
        cutinPanelRoot.transform.localScale = Vector3.one * 0.7f;
        cutinPanelRoot.SetActive(true);
        cutinStartTime_ = Time.time;
        ApplyCutinFrame(0.0f);
    }

    public void HideCutinNow()
    {
        cutinStartTime_ = -1.0f;

        // 子→親の順。親を先に落とすと非アクティブUdon環境で子の状態が残ることがある。
        if (cutinMascotImages != null)
        {
            for (int i = 0; i < cutinMascotImages.Length; ++i)
            {
                if (cutinMascotImages[i] != null) { cutinMascotImages[i].gameObject.SetActive(false); }
            }
        }
        if (cutinNameTexts != null)
        {
            for (int i = 0; i < cutinNameTexts.Length; ++i)
            {
                if (cutinNameTexts[i] != null) { cutinNameTexts[i].gameObject.SetActive(false); }
            }
        }
        if (cutinMascotImage != null) { cutinMascotImage.gameObject.SetActive(false); }
        if (cutinCallText != null) { cutinCallText.gameObject.SetActive(false); }
        if (cutinPlayerText != null) { cutinPlayerText.gameObject.SetActive(false); }
        if (cutinCanvasGroup != null) { cutinCanvasGroup.alpha = 0.0f; }
        if (cutinPanelRoot != null) { cutinPanelRoot.SetActive(false); }
    }

public void HideSettlementNow()
{
    cardsStartTime_ = -1.0f;
    settlementQueued_ = false;
    settlementQueueTime_ = -1.0f;

    if (settlementCardRoots != null)
    {
        for (int i = 0; i < settlementCardRoots.Length; ++i)
        {
            if (settlementCardRoots[i] != null) { settlementCardRoots[i].SetActive(false); }
        }
    }
    if (settlementCanvasGroup != null) { settlementCanvasGroup.alpha = 0.0f; }
    if (settlementPanelRoot != null) { settlementPanelRoot.SetActive(false); }
}

    private void SetupCards()
    {
        ComputeRanks();
        LayoutSlots();

        for (int i = 0; i < 4; ++i)
        {
            bool active = i < activeCardCount_;
            if (settlementCardRoots != null && i < settlementCardRoots.Length && settlementCardRoots[i] != null)
            {
                settlementCardRoots[i].SetActive(active);
            }
            if (!active) { continue; }

            if (settlementNameTexts != null && i < settlementNameTexts.Length && settlementNameTexts[i] != null)
            {
                settlementNameTexts[i].text = playerNames_[i];
            }

            if (settlementRankTexts != null && i < settlementRankTexts.Length && settlementRankTexts[i] != null)
            {
                // 対局開始（intro）では順位が無いので風を出す。
                settlementRankTexts[i].text = introMode_
                    ? (mahjong != null ? mahjong.Presentation_SeatWind(i) : "")
                    : cardRanks_[i].ToString() + "位";
            }

            if (settlementPortraitImages != null && i < settlementPortraitImages.Length && settlementPortraitImages[i] != null)
            {
                settlementPortraitImages[i].texture = ResolveMascot(charaIds_[i], charaPlayerIds_[i]);
                // Original 700x1080 mascot into a 260x250 frame: keep the top and crop the lower area.
                settlementPortraitImages[i].uvRect = new Rect(0.0f, 0.3767806f, 1.0f, 0.6232194f);
            }

            int delta = afterPoints_[i] - beforePoints_[i];
            Color signColor = delta > 0 ? gainColor : (delta < 0 ? lossColor : neutralColor);

            if (settlementCardBackgrounds != null && i < settlementCardBackgrounds.Length && settlementCardBackgrounds[i] != null)
            {
                settlementCardBackgrounds[i].color = introMode_
                    ? navyColor
                    : (delta > 0 ? navyGainColor : (delta < 0 ? navyLossColor : navyColor));
            }
            if (settlementAccentImages != null && i < settlementAccentImages.Length && settlementAccentImages[i] != null)
            {
                settlementAccentImages[i].color = introMode_ ? goldColor : signColor;
            }

            if (settlementBeforeTexts != null && i < settlementBeforeTexts.Length && settlementBeforeTexts[i] != null)
            {
                settlementBeforeTexts[i].text = introMode_
                    ? mahjong.Presentation_SeatWind(i)
                    : beforePoints_[i].ToString();
            }
            if (settlementDeltaTexts != null && i < settlementDeltaTexts.Length && settlementDeltaTexts[i] != null)
            {
                settlementDeltaTexts[i].text = introMode_ ? "VS" : Signed(delta);
                settlementDeltaTexts[i].color = introMode_ ? goldColor : signColor;
            }
            if (settlementAfterTexts != null && i < settlementAfterTexts.Length && settlementAfterTexts[i] != null)
            {
                settlementAfterTexts[i].text = introMode_
                    ? afterPoints_[i].ToString()
                    : beforePoints_[i].ToString();
            }
        }

        SetupArrows();
    }

    /// <summary>
    /// 点数の高い順に順位を振る。同点なら起家に近い側が上位（麻雀の順位付け）。
    /// </summary>
    private void ComputeRanks()
    {
        for (int i = 0; i < 4; ++i)
        {
            if (i >= activeCardCount_) { cardRanks_[i] = 0; continue; }

            int better = 0;
            for (int j = 0; j < activeCardCount_; ++j)
            {
                if (j == i) { continue; }

                if (afterPoints_[j] > afterPoints_[i]) { ++better; continue; }
                if (afterPoints_[j] < afterPoints_[i]) { continue; }

                // 同点：起家からの並び順が小さいほうが上位。
                int oj = mahjong != null ? mahjong.Presentation_SeatOrderFromFirstLeader(j) : j;
                int oi = mahjong != null ? mahjong.Presentation_SeatOrderFromFirstLeader(i) : i;
                if (oj < oi) { ++better; }
            }
            cardRanks_[i] = better + 1;
        }
    }

    /// <summary>
    /// カードを席の位置へ並べる。
    ///
    /// 席番号の足し算で「右・対面・左」を決めると、三麻（席3が空く）で3人目を
    /// 左に置いてしまうなど実際の座り位置とずれる。ここでは席のワールド方向を
    /// 見る人の向きに投影して、実際に見えている方角へ割り当てる。
    ///
    /// 割り当ては「見る人から見た角度を90度刻みへ丸める」一対一。席は必ず90度
    /// 刻みに並ぶので、丸めた先は必ず全席ばらける。
    /// 内積のしきい値（toward&gt;0.5 で手前）で決めていた頃は、卓の角（席と席の
    /// 中間＝視線が30〜60度）から見ると隣り合う2席が同じスロットに入り、カードが
    /// 完全に重なって片方が見えず、その2席を結ぶ矢印も長さ0になって消えていた。
    /// 着席者は席の正面にいるので起きず、観戦者の立ち位置でだけ再現する不具合だった。
    /// </summary>
    private void LayoutSlots()
    {
        // 見る人の向き。ヘッドが取れないエディタ等では自席の方向で代用する。
        int localSeat = mahjong != null ? mahjong.Presentation_LocalSeat() : -1;
        if (localSeat < 0 || localSeat >= activeCardCount_) { localSeat = 0; }

        Vector3 selfDir = mahjong != null
            ? mahjong.Presentation_SeatDirection(localSeat)
            : Vector3.right;

        VRCPlayerApi localPlayer = Networking.LocalPlayer;
        if (localPlayer != null && Utilities.IsValid(localPlayer) && mahjong != null)
        {
            VRCPlayerApi.TrackingData head =
                localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);

            Vector3 fromTable = head.position - mahjong.transform.position;
            fromTable.y = 0.0f;
            if (fromTable.sqrMagnitude > 0.04f)   // 卓の真上に立っている等の異常値を弾く
            {
                selfDir = fromTable.normalized;
            }
        }

        // 見る人は卓の外から中心を向いている。右手の方向は Cross(up, forward)。
        Vector3 viewRight = Vector3.Cross(Vector3.up, -selfDir).normalized;

        // 各席が見る人から見て何度の方向にあるか（0=手前 +90=右 ±180=対面 -90=左）を
        // 90度刻みのスロットへ丸める。席は必ず90度刻みに並ぶので、丸めた時点で
        // どの視線角でも重複しない。万一重なった場合の保険として輪の次へ送る。
        for (int s = 0; s < 4; ++s) { slotTaken_[s] = false; }

        for (int i = 0; i < 4; ++i)
        {
            slotOfSeat_[i] = -1;

            if (i >= activeCardCount_) { continue; }

            Vector3 dir = mahjong != null ? mahjong.Presentation_SeatDirection(i) : selfDir;

            float toward = Vector3.Dot(dir, selfDir);   // +1=自分 -1=対面
            float side = Vector3.Dot(dir, viewRight);   // +1=右 -1=左
            float angle = Mathf.Atan2(side, toward) * Mathf.Rad2Deg;

            // floor(x + 0.5) の丸めは -0.5 も +0.5 も同じ向きへ倒れるため、
            // 視線が席と席のちょうど中間（卓の角）でも席同士がぶつからない。
            int slot = (int)Mathf.Floor(angle / 90.0f + 0.5f);
            slot = ((slot % 4) + 4) % 4;

            for (int k = 0; k < 4; ++k)
            {
                int s = (slot + k) % 4;
                if (slotTaken_[s]) { continue; }

                slotTaken_[s] = true;
                slotOfSeat_[i] = s;
                break;
            }
        }

        for (int i = 0; i < activeCardCount_; ++i)
        {
            cardHomePositions_[i] = SlotPosition(slotOfSeat_[i]);
        }
    }

    /// <summary>スロット番号のカード位置。未割当（-1）は手前へ落とす。</summary>
    private Vector2 SlotPosition(int slot)
    {
        if (slot == 1) { return slotRight; }
        if (slot == 2) { return slotTop; }
        if (slot == 3) { return slotLeft; }
        return slotSelf;
    }

    /// <summary>
    /// 点棒の流れを矢印で引く。払う席（delta&lt;0）から受け取る席（delta&gt;0）へ、
    /// <b>全ての組み合わせ</b>を引く。intro では引かない。
    ///   ・ロン     … 1本（放銃者 → 和了者）
    ///   ・ツモ     … 3本（他家3人 → 和了者）
    ///   ・ダブロン … 2本 ／ トリロン … 3本（放銃者 → 和了者それぞれ）
    ///   ・流局     … 最大4本（ノーテン2人 → テンパイ2人）
    ///
    /// 受け取り側を「いちばん増えた1席」に決め打つと、ダブロン・トリロンの
    /// 2人目以降に矢印が出ず、点棒だけ動いて線が無い絵になる（それが不具合だった）。
    /// </summary>
    private void SetupArrows()
    {
        activeArrowCount_ = 0;
        if (settlementArrowRects == null) { return; }

        // 引ける本数は「用意されている矢印」と「諸元を控える配列」の小さいほう。
        int cap = settlementArrowRects.Length;
        if (cap > arrowFrom_.Length) { cap = arrowFrom_.Length; }

        if (!introMode_)
        {
            for (int p = 0; p < activeCardCount_ && activeArrowCount_ < cap; ++p)
            {
                if (afterPoints_[p] - beforePoints_[p] >= 0) { continue; }   // 払う席だけ

                for (int r = 0; r < activeCardCount_ && activeArrowCount_ < cap; ++r)
                {
                    if (r == p) { continue; }
                    if (afterPoints_[r] - beforePoints_[r] <= 0) { continue; }   // 受け取る席だけ

                    // 引けなかった本（長さ0＝同じ位置）は数えない。数えてしまうと
                    // ApplyCardsFrame が前局の諸元で描き、古い矢印が残って見える。
                    if (PlaceArrow(activeArrowCount_, cardHomePositions_[p], cardHomePositions_[r]))
                    {
                        ++activeArrowCount_;
                    }
                }
            }
        }

        // 余った矢印は畳む。
        for (int a = 0; a < settlementArrowRects.Length; ++a)
        {
            bool on = a < activeArrowCount_;
            if (settlementArrowRects[a] != null)
            {
                settlementArrowRects[a].gameObject.SetActive(on);
            }
        }
    }

    /// <summary>
    /// 矢印1本を from→to に伸ばす。長さぶん幅を持たせ、角度で回す。
    /// 引けたら true。<b>false のときは諸元を書いていない</b>ので、呼び側は数に含めないこと。
    /// </summary>
    private bool PlaceArrow(int index, Vector2 from, Vector2 to)
    {
        var rect = settlementArrowRects[index];
        if (rect == null) { return false; }

        Vector2 delta = to - from;
        float length = delta.magnitude;
        if (length < 1.0f) { return false; }

        // 両端がカードに食い込まないよう内側へ詰める。
        float margin = Mathf.Min(settlementArrowMargin, length * 0.4f);
        Vector2 dir = delta / length;
        Vector2 a = from + dir * margin;
        Vector2 b = to - dir * margin;

        // 諸元だけ控える。実際の長さは ApplyCardsFrame が時間で伸ばす。
        arrowFrom_[index] = a;
        arrowDir_[index] = dir;
        arrowLen_[index] = (b - a).magnitude;

        rect.localRotation = Quaternion.Euler(0.0f, 0.0f, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);
        rect.sizeDelta = new Vector2(0.0f, settlementArrowThickness);
        rect.anchoredPosition = a;

        if (settlementArrowImages != null && index < settlementArrowImages.Length
            && settlementArrowImages[index] != null)
        {
            settlementArrowImages[index].color =
                new Color(arrowColor.r, arrowColor.g, arrowColor.b, 0.0f);
        }

        return true;
    }

private void BeginCards()
{
    if (settlementCanvasGroup != null)
    {
        settlementCanvasGroup.alpha = 0.0f;
        settlementCanvasGroup.interactable = false;
        settlementCanvasGroup.blocksRaycasts = false;
    }

    for (int i = 0; i < activeCardCount_; ++i)
    {
        if (settlementCardCanvasGroups != null && i < settlementCardCanvasGroups.Length && settlementCardCanvasGroups[i] != null)
        {
            settlementCardCanvasGroups[i].alpha = 0.0f;
        }
        if (settlementCardRects != null && i < settlementCardRects.Length && settlementCardRects[i] != null)
        {
            float direction = (i & 1) == 0 ? -1.0f : 1.0f;
            settlementCardRects[i].anchoredPosition =
                cardHomePositions_[i] + new Vector2(0.0f, direction * settlementSlideDistance);
            settlementCardRects[i].localScale = Vector3.one * 0.92f;
        }
    }

    PlaceForLocalViewer();
    settlementPanelRoot.transform.localPosition = Vector3.down * 0.20f;
        settlementPanelRoot.transform.localScale = Vector3.one * 0.7f;
        settlementPanelRoot.SetActive(true);
    cardsStartTime_ = Time.time;
    nextTextDrawTime_ = 0.0f;
    countSoundPlayed_ = false;
    ApplyCardsFrame(0.0f);
}

private void Update()
    {
        if (settlementQueued_)
        {
            TryBeginSettlement();
        }

        if (cutinStartTime_ >= 0.0f || cardsStartTime_ >= 0.0f)
        {
            FollowLocalViewer();
        }

        if (cutinStartTime_ >= 0.0f)
        {
            ApplyCutinFrame(Time.time - cutinStartTime_);
        }

        if (cardsStartTime_ >= 0.0f)
        {
            ApplyCardsFrame(Time.time - cardsStartTime_);
        }
    }

    private void ApplyCutinFrame(float elapsed)
    {
        float enter = Mathf.Max(0.01f, cutinEnterSeconds);
        float hold = Mathf.Max(0.0f, cutinHoldSeconds);
        float exit = Mathf.Max(0.01f, cutinExitSeconds);
        float total = enter + hold + exit;

        if (elapsed >= total)
        {
            HideCutinNow();
            return;
        }

        float alpha;
        float bandX;
        float mascotX;

        if (elapsed < enter)
        {
            float e = EaseOutCubic(elapsed / enter);
            alpha = e;
            bandX = 0.04f + 0.96f * e;
            mascotX = -cutinMascotSlide * (1.0f - e);
        }
        else if (elapsed < enter + hold)
        {
            alpha = 1.0f;
            bandX = 1.0f;
            mascotX = 0.0f;
        }
        else
        {
            float q = EaseInCubic((elapsed - enter - hold) / exit);
            alpha = 1.0f - q;
            bandX = 1.0f - 0.96f * q;
            mascotX = cutinMascotSlide * 0.55f * q;
        }

        if (cutinCanvasGroup != null) { cutinCanvasGroup.alpha = Mathf.Clamp01(alpha); }

        if (cutinBandRect != null)
        {
            cutinBandRect.anchoredPosition = cutinBandHome_;
            cutinBandRect.localScale =
                new Vector3(cutinBandScale_.x * Mathf.Max(0.04f, bandX), cutinBandScale_.y, cutinBandScale_.z);
        }

        float pop = elapsed < enter ? 0.96f + 0.04f * EaseOutBack(elapsed / enter) : 1.0f;

        for (int i = 0; i < cutinSlotCount_; ++i)
        {
            RectTransform rect = (cutinMascotRects != null && i < cutinMascotRects.Length) ? cutinMascotRects[i] : null;
            if (rect == null) { continue; }

            rect.anchoredPosition = cutinSlotHome_[i] + new Vector2(mascotX, -18.0f * (1.0f - alpha));
            rect.localScale = cutinSlotScale_[i] * pop;
        }
    }

    private void ApplyCardsFrame(float elapsed)
    {
        float enter = Mathf.Max(0.01f, settlementEnterSeconds);
        float stagger = Mathf.Max(0.0f, settlementCardStagger);
        float lastEnterEnd = enter + stagger * Mathf.Max(0, activeCardCount_ - 1);
        float count = introMode_ ? 0.0f : Mathf.Max(0.01f, settlementCountSeconds);
        float countEnd = lastEnterEnd + count;
        float hold = introMode_ ? Mathf.Max(0.0f, matchIntroHoldSeconds) : Mathf.Max(0.0f, settlementHoldSeconds);
        float holdEnd = countEnd + hold;
        float exit = Mathf.Max(0.01f, settlementExitSeconds);
        float total = holdEnd + exit;

        if (elapsed >= total)
        {
            bool notifyMahjong = !introMode_;
            HideSettlementNow();
            if (notifyMahjong && mahjong != null) { mahjong.Presentation_RefreshView(); mahjong.Presentation_OnSettlementFinished(); }
            return;
        }

        float rootAlpha = elapsed < enter ? EaseOutCubic(elapsed / enter) : 1.0f;
        if (elapsed > holdEnd)
        {
            rootAlpha = 1.0f - EaseInCubic((elapsed - holdEnd) / exit);
        }
        if (settlementCanvasGroup != null) { settlementCanvasGroup.alpha = Mathf.Clamp01(rootAlpha); }

        for (int i = 0; i < activeCardCount_; ++i)
        {
            float local = Mathf.Clamp01((elapsed - stagger * i) / enter);
            float e = EaseOutBack(local);

            // 入場方向は「外側から自分の席の位置へ」。席ごとに置き場所が違うので、
            // 中心から見た向きへ外へ逃がしてから戻す（横一列だった頃の上下振り分けの名残を廃止）。
            Vector2 outward = cardHomePositions_[i];
            outward = outward.sqrMagnitude > 1.0f ? outward.normalized : Vector2.down;

            if (settlementCardCanvasGroups != null && i < settlementCardCanvasGroups.Length && settlementCardCanvasGroups[i] != null)
            {
                settlementCardCanvasGroups[i].alpha = local;
            }
            if (settlementCardRects != null && i < settlementCardRects.Length && settlementCardRects[i] != null)
            {
                settlementCardRects[i].anchoredPosition =
                    cardHomePositions_[i] + outward * (settlementSlideDistance * (1.0f - e));
                settlementCardRects[i].localScale = Vector3.one * (0.92f + 0.08f * e);
            }
        }

        // 点数のカウントが始まる瞬間に1回だけ鳴らす。音の長さと
        // settlementCountSeconds を揃えてあるので、鳴り終わりとカウント終わりが一致する。
        if (!introMode_ && !countSoundPlayed_ && elapsed >= lastEnterEnd)
        {
            countSoundPlayed_ = true;
            if (seSource != null && scoreCountSound != null)
            {
                seSource.PlayOneShot(scoreCountSound, seVolume);
            }
        }

        // 矢印はカードが出そろってから、払う側から受け取る側へ伸びる。
        // 点数のカウントアップと同じ尺に収め、伸び切ったら保持中は出しっぱなし。
        if (activeArrowCount_ > 0 && settlementArrowRects != null)
        {
            float grow = Mathf.Clamp01((elapsed - lastEnterEnd) / Mathf.Max(0.01f, count * 0.45f));
            float eased = EaseOutCubic(grow);

            for (int a = 0; a < activeArrowCount_ && a < settlementArrowRects.Length; ++a)
            {
                if (settlementArrowRects[a] == null) { continue; }

                float len = arrowLen_[a] * eased;
                settlementArrowRects[a].sizeDelta = new Vector2(len, settlementArrowThickness);
                // pivot は中央なので、伸びるぶん中心を先へ送る。
                settlementArrowRects[a].anchoredPosition = arrowFrom_[a] + arrowDir_[a] * (len * 0.5f);

                if (settlementArrowImages != null && a < settlementArrowImages.Length
                    && settlementArrowImages[a] != null)
                {
                    // 伸び始めの一瞬だけ薄く入れる（いきなり出ると硬い）。
                    float alpha = Mathf.Clamp01(grow / 0.25f);
                    settlementArrowImages[a].color = new Color(
                        arrowColor.r, arrowColor.g, arrowColor.b, arrowColor.a * alpha);
                }
            }
        }

        if (!introMode_ && Time.time >= nextTextDrawTime_)
        {
            nextTextDrawTime_ = Time.time + Mathf.Max(0.02f, settlementTextInterval);
            float p = Mathf.Clamp01((elapsed - lastEnterEnd) / count);
            float eased = EaseOutCubic(p);

            for (int i = 0; i < activeCardCount_; ++i)
            {
                if (settlementAfterTexts == null || i >= settlementAfterTexts.Length || settlementAfterTexts[i] == null) { continue; }
                float value = beforePoints_[i] + (afterPoints_[i] - beforePoints_[i]) * eased;
                int shown = Mathf.RoundToInt(value / 100.0f) * 100;
                settlementAfterTexts[i].text = shown.ToString();
            }
        }
    }

    // PlayWinCutinMultiAs で渡された和了者のプレイヤーID列（自画像の引き当て用）。
    private int[] cutinPlayerIds_;

    private Texture ResolveMascot(int charaId)
    {
        return ResolveMascot(charaId, 0);
    }

    // 自画像キャラ（CHARA_SELFIE）を選んでいる席は、その人が PlayerData へ焼いた
    // 写真を立ち絵として使う。写真が無い／ストア未割当なら従来の立ち絵へ落ちる。
    private Texture ResolveMascot(int charaId, int playerId)
    {
        if (selfieStore != null
            && charaId == PlayerStatsStore.CHARA_SELFIE
            && playerId > 0)
        {
            var player = VRCPlayerApi.GetPlayerById(playerId);
            if (player != null && player.IsValid())
            {
                var selfie = selfieStore.GetPortraitOf(player);
                if (selfie != null) { return selfie; }
            }
        }

        if (charaMascotTextures != null
            && charaId >= 0
            && charaId < charaMascotTextures.Length
            && charaMascotTextures[charaId] != null)
        {
            return charaMascotTextures[charaId];
        }
        return defaultMascotTexture;
    }

    private Color CharaColor(int charaId)
    {
        if (charaId == 1) { return new Color(0.34f, 0.24f, 0.06f, 0.98f); }
        if (charaId == 2) { return new Color(0.20f, 0.07f, 0.28f, 0.98f); }
        return new Color(0.025f, 0.16f, 0.22f, 0.98f);
    }

    private string Signed(int value)
    {
        if (value > 0) { return "+" + value; }
        return value.ToString();
    }

    private float EaseOutCubic(float t)
    {
        t = Mathf.Clamp01(t);
        float inv = 1.0f - t;
        return 1.0f - inv * inv * inv;
    }

    private float EaseInCubic(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * t;
    }

    private float EaseOutBack(float t)
    {
        t = Mathf.Clamp01(t);
        float x = t - 1.0f;
        return 1.0f + 1.70158f * x * x * x + 0.70158f * x * x;
    }
}
}
