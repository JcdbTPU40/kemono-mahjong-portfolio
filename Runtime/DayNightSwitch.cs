using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

/// <summary>
/// 昼夜切り替え（Skybox / Ambient / Fog / Directional Light / GameObject表示 を同期切替）。
/// Night_switch に付けて使う。
///
/// ■ 切り替えは1分かけたアニメーション（夕焼け → 夜空）
///   昼→夜：前半30秒で昼の空を夕焼け色にしながら暗くし、真っ暗になった所で星空へ入れ替え、
///          後半30秒で星・月・夜空の背景を0から元の明るさまで出す。夜→昼はその逆再生（朝焼け）。
///   雲海・水面など昼用／夜用の入れ替えは、暗くなりきった中間点で行う（半透明のフェードは Quest で重い）。
///   空の2枚はシェーダーが別物（Cubemap と UniStar）なので混ぜずに「暗く→入れ替え→明るく」にしている。
///
/// ■ 同期は「目標（isNight）」と「開始時刻（サーバー時刻ms）」だけ
///   各自が「開始から何ms経ったか」で同じ場面を描く＝途中で入った人も同じ空になる。
///   切り替え中にもう一度押されたら、今の暗さから逆向きに進む（開始時刻をずらして表す）。
///   毎フレーム描くのは切り替え中の1分だけ。終わったら Update は1秒に1回の時刻確認だけになる。
///
/// ■ 自動切り替え（JST 0時に夜・朝5時に昼）
///   境目を越えた瞬間に Owner が1回だけ切り替える。「最後に自動で切り替えた境目」を同期して持つので、
///   Owner が入れ替わっても二重に切り替えたり漏らしたりしない。手で押しても次の境目までは触らない。
///   インスタンスを開いた時点の状態は時刻どおり（アニメーションなし）。
///
/// ■ 空のマテリアルは切り替え専用のコピー（daySkyRuntime / nightSkyRuntime）を動かす
///   元のマテリアルを直接書き換えると、Editor で Play を途中で止めたときに暗い値がアセットに残る。
///   元の値（daySkybox / nightSkybox から読む）を起動のたびにコピーへ入れ直すので、コピーが汚れても戻る。
///
/// ★ 焼いたライトマップ(LightingData / Lightmap-0)は実行時に切り替えできない。
///   静的ジオメトリは「昼」か「夜」どちらか一方でベイクし、もう一方は
///   ここで設定するリアルタイムの Ambient / Light / Skybox で寄せること。
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class DayNightSwitch : UdonSharpBehaviour
{
    [Header("初期状態 (true = 夜)。自動切り替えOFFのときだけ使う")]
    [SerializeField] bool startAtNight = false;

    [Header("Skybox マテリアル（元の値の読み出し元。アセットは書き換えない）")]
    [SerializeField] Material daySkybox;    // 例: Skybox_for_quest
    [SerializeField] Material nightSkybox;  // 例: UniStarSky

    [Header("時間帯で表示を切り替える GameObject（切り替えの中間点で入れ替える）")]
    [SerializeField] GameObject[] dayObjects;   // 昼だけ表示 (例: CloudSea)
    [SerializeField] GameObject[] nightObjects; // 夜だけ表示 (例: 星空ドーム, 月)

    [Header("メインの太陽/月ライト (任意)")]
    [SerializeField] Light directionalLight;
    [SerializeField] Color dayLightColor = Color.white;
    [SerializeField] float dayLightIntensity = 1f;
    [SerializeField] Color nightLightColor = new Color(0.40f, 0.45f, 0.70f);
    [SerializeField] float nightLightIntensity = 0.25f;

    [Header("環境光 Ambient (Flat) を制御する")]
    [SerializeField] bool controlAmbient = true;
    [SerializeField] Color dayAmbient = new Color(0.55f, 0.55f, 0.55f);
    [SerializeField] Color nightAmbient = new Color(0.10f, 0.10f, 0.16f);

    [Header("Fog を制御する (任意)")]
    [SerializeField] bool controlFog = false;
    [SerializeField] Color dayFog = new Color(0.75f, 0.80f, 0.85f);
    [SerializeField] Color nightFog = new Color(0.02f, 0.02f, 0.06f);

    [Header("自動切り替え（日本時間）")]
    [SerializeField] bool autoSchedule = true;
    [SerializeField] int nightStartHourJst = 0;  // この時刻に夜へ
    [SerializeField] int nightEndHourJst = 5;    // この時刻に昼へ（ランキングの集計日の境目と揃える）

    [Header("切り替えアニメーション")]
    [SerializeField] float transitionSeconds = 60f;
    [SerializeField] Material daySkyRuntime;     // 実際に空へ貼って動かすコピー（未設定なら daySkybox を直接動かす）
    [SerializeField] Material nightSkyRuntime;   // 同上（夜）
    [SerializeField] Color sunsetTint = new Color(1.45f, 0.80f, 0.50f); // 昼の空 _Tint への掛け率（夕焼け）
    [SerializeField] Color duskTint = new Color(0.30f, 0.35f, 0.65f);   // 同（暮れきった色）
    [SerializeField] float duskExposure = 0.12f;  // 暮れきったときの _Exposure（元の値に対する倍率）

    [Header("スイッチ本体の表示（押した瞬間に切り替える）")]
    [SerializeField] GameObject[] switchOnObjects;  // 例: Night_on

    // 同期変数。Owner が変更 → 全員 OnDeserialization で反映（遅参加者も自動で合う）
    [UdonSynced] bool isNight;          // 目標（いま向かっている側）
    [UdonSynced] int transStartMs;      // 切り替えを始めたサーバー時刻(ms)。0＝まだ初期化されていない
    [UdonSynced] int autoKey;           // 最後に自動で切り替えた境目（集計日*2+夜なら1）。0＝未初期化

    Material daySky_;
    Material nightSky_;
    bool hasDayProps_;
    Color dayTintBase_;
    float dayExposureBase_;
    bool hasNightProps_;
    float nightBrightBase_;
    Color nightTopBase_;
    Color nightBottomBase_;
    Color nightMoonBase_;
    Color nightBloomBase_;

    int appliedSide_ = -1;      // 0=昼の空・昼の物 / 1=夜
    int appliedSwitch_ = -1;    // スイッチ表示 0/1
    bool animating_;
    bool ready_;
    float nextPoll_;

    void Start()
    {
        SetupMaterials();

        if (controlAmbient)
        {
            // ※ AmbientMode enum が Udon で通らない環境ならこの1行だけコメントアウト
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        }

        // インスタンスを開いた人（Owner）が時刻どおりの状態で始める。非Ownerは OnDeserialization で合う。
        if (Networking.IsOwner(gameObject) && autoKey == 0)
        {
            System.DateTime jst = GetJst();
            isNight = autoSchedule ? IsNightWindow(jst) : startAtNight;
            autoKey = autoSchedule ? GetWindowKey(jst) : 1;
            int now = Networking.GetServerTimeInMilliseconds();
            SetStart(now - GetDurationMs());   // 切り替え済み＝アニメーションなし
            RequestSerialization();
        }

        ready_ = true;
        animating_ = true;
        UpdateVisual(Networking.GetServerTimeInMilliseconds());
    }

    public override void Interact()
    {
        if (!ready_) { return; }
        if (!Networking.IsOwner(gameObject))
            Networking.SetOwner(Networking.LocalPlayer, gameObject);

        BeginTransition(!isNight);
        RequestSerialization(); // 全員へ配信
    }

    public override void OnDeserialization()
    {
        animating_ = true;      // 描き直しは Update が受け持つ（途中の場面から描ける）
    }

    void Update()
    {
        if (!ready_) { return; }

        if (Time.time >= nextPoll_)
        {
            nextPoll_ = Time.time + 1.0f;
            PollSchedule();
        }

        if (!animating_) { return; }
        UpdateVisual(Networking.GetServerTimeInMilliseconds());
    }

    // ============================================================
    // 自動切り替え（Owner のみ・1秒に1回）
    // ============================================================
    void PollSchedule()
    {
        if (!autoSchedule) { return; }
        if (!Networking.IsOwner(gameObject)) { return; }
        if (autoKey == 0) { return; }   // まだ初期化前（Start 前の同期待ち）

        System.DateTime jst = GetJst();
        int key = GetWindowKey(jst);
        if (key == autoKey) { return; }

        // 境目を越えた。手で既にその側にしてあれば記録だけ更新する。
        autoKey = key;
        bool night = IsNightWindow(jst);
        if (night != isNight) { BeginTransition(night); }
        RequestSerialization();
    }

    System.DateTime GetJst()
    {
        return Networking.GetNetworkDateTime().AddHours(9.0);
    }

    bool IsNightWindow(System.DateTime jst)
    {
        int h = jst.Hour;
        return h >= nightStartHourJst && h < nightEndHourJst;
    }

    // 境目ごとに一意な数。朝の境目（nightEndHourJst）で日付が変わる集計日 × 2 ＋ 夜なら1。
    // 0時〜5時の夜は前日の集計日に属する＝「前日の昼 → 前日の夜 → 当日の昼」と単調に増える。
    int GetWindowKey(System.DateTime jst)
    {
        System.DateTime biz = jst.AddHours(-nightEndHourJst);
        int day = biz.Year * 10000 + biz.Month * 100 + biz.Day;
        return day * 2 + (IsNightWindow(jst) ? 1 : 0);
    }

    // ============================================================
    // 切り替え（Owner のみ）
    // ============================================================
    // 目標を night に向け、今の見た目から続けて進むように開始時刻をずらす。
    void BeginTransition(bool night)
    {
        int now = Networking.GetServerTimeInMilliseconds();
        float n = GetNightAmount(now);
        isNight = night;

        // 新しい目標へ向けて「もう進んだ分」。昼→夜なら今の夜らしさ n、夜→昼なら 1-n。
        float progressed = night ? n : 1.0f - n;
        SetStart(now - Mathf.RoundToInt(progressed * GetDurationMs()));

        animating_ = true;
        UpdateVisual(now);
    }

    void SetStart(int ms)
    {
        transStartMs = ms == 0 ? 1 : ms;   // 0 は「未初期化」の番兵
    }

    int GetDurationMs()
    {
        return transitionSeconds > 0.0f ? Mathf.RoundToInt(transitionSeconds * 1000.0f) : 0;
    }

    // 0＝昼 … 1＝夜。サーバー時刻の差だけで決まる＝全員が同じ値になる。
    float GetNightAmount(int now)
    {
        float p = 1.0f;
        int dur = GetDurationMs();
        if (transStartMs != 0 && dur > 0)
        {
            int elapsed = now - transStartMs;   // int の差＝サーバー時刻が負へ回っても正しい
            if (elapsed <= 0) { p = 0.0f; }
            else if (elapsed < dur) { p = (float)elapsed / (float)dur; }
        }
        return isNight ? p : 1.0f - p;
    }

    // ============================================================
    // 見た目
    // ============================================================
    void UpdateVisual(int now)
    {
        float n = GetNightAmount(now);
        ApplyNightAmount(n);

        // 目標に着いたら止める（以降は1秒ごとの時刻確認だけ）。
        if (isNight ? n >= 1.0f : n <= 0.0f) { animating_ = false; }
    }

    void ApplyNightAmount(float n)
    {
        // --- スイッチ本体：押した瞬間に目標の側へ ---
        int sw = isNight ? 1 : 0;
        if (sw != appliedSwitch_)
        {
            appliedSwitch_ = sw;
            if (switchOnObjects != null)
                for (int i = 0; i < switchOnObjects.Length; i++)
                    if (switchOnObjects[i] != null) switchOnObjects[i].SetActive(isNight);
        }

        // --- 中間点で空と昼用/夜用の物を入れ替える ---
        int side = n >= 0.5f ? 1 : 0;
        if (side != appliedSide_)
        {
            appliedSide_ = side;
            Material sky = side == 1 ? nightSky_ : daySky_;
            if (sky != null) RenderSettings.skybox = sky;

            int j;
            if (dayObjects != null)
                for (j = 0; j < dayObjects.Length; j++)
                    if (dayObjects[j] != null) dayObjects[j].SetActive(side == 0);
            if (nightObjects != null)
                for (j = 0; j < nightObjects.Length; j++)
                    if (nightObjects[j] != null) nightObjects[j].SetActive(side == 1);
        }

        // --- 空 ---
        if (side == 0)
        {
            // 昼の空：前半は夕焼け色へ、後半はそのまま暮れて暗くなる（夜→昼では朝焼けとして逆に進む）。
            if (daySky_ != null && hasDayProps_)
            {
                float u = Mathf.Clamp01(n * 2.0f);
                Color mul = u < 0.5f
                    ? Color.Lerp(Color.white, sunsetTint, u * 2.0f)
                    : Color.Lerp(sunsetTint, duskTint, (u - 0.5f) * 2.0f);
                daySky_.SetColor("_Tint", new Color(dayTintBase_.r * mul.r, dayTintBase_.g * mul.g,
                                                    dayTintBase_.b * mul.b, dayTintBase_.a));
                daySky_.SetFloat("_Exposure", Mathf.Lerp(dayExposureBase_, dayExposureBase_ * duskExposure, Smooth(u)));
            }
        }
        else
        {
            // 夜空：星・月・背景を0から元の明るさまで出す。
            if (nightSky_ != null && hasNightProps_)
            {
                float v = Smooth(Mathf.Clamp01((n - 0.5f) * 2.0f));
                nightSky_.SetFloat("_Brightness", nightBrightBase_ * v);
                nightSky_.SetColor("_BackGroundColorTop", Scale(nightTopBase_, v));
                nightSky_.SetColor("_BackGroundColorBottom", Scale(nightBottomBase_, v));
                nightSky_.SetColor("_MoonColor", Scale(nightMoonBase_, v));
                nightSky_.SetColor("_BloomColor", Scale(nightBloomBase_, v));
            }
        }

        // --- 環境光・フォグ・ライト：1分かけてなめらかに ---
        float s = Smooth(n);
        if (controlAmbient) { RenderSettings.ambientLight = Color.Lerp(dayAmbient, nightAmbient, s); }
        if (controlFog)
        {
            RenderSettings.fog = true;
            RenderSettings.fogColor = Color.Lerp(dayFog, nightFog, s);
        }
        if (directionalLight != null)
        {
            directionalLight.color = Color.Lerp(dayLightColor, nightLightColor, s);
            directionalLight.intensity = Mathf.Lerp(dayLightIntensity, nightLightIntensity, s);
        }

        // 注: DynamicGI.UpdateEnvironment() は Udon 非対応のため呼べない。
        // Skybox 差し替えの見た目は RenderSettings.skybox 代入だけで反映される。
    }

    float Smooth(float t)
    {
        return t * t * (3.0f - 2.0f * t);
    }

    Color Scale(Color c, float k)
    {
        return new Color(c.r * k, c.g * k, c.b * k, c.a);
    }

    // 元のマテリアルから基準値を読み、動かす用のコピーへ入れ直す（前回の Play の途中値を消す）。
    void SetupMaterials()
    {
        daySky_ = daySkyRuntime != null ? daySkyRuntime : daySkybox;
        nightSky_ = nightSkyRuntime != null ? nightSkyRuntime : nightSkybox;

        if (daySkybox != null && daySkybox.HasProperty("_Tint") && daySkybox.HasProperty("_Exposure"))
        {
            hasDayProps_ = true;
            dayTintBase_ = daySkybox.GetColor("_Tint");
            dayExposureBase_ = daySkybox.GetFloat("_Exposure");
            if (daySky_ != null)
            {
                daySky_.SetColor("_Tint", dayTintBase_);
                daySky_.SetFloat("_Exposure", dayExposureBase_);
            }
        }

        if (nightSkybox != null && nightSkybox.HasProperty("_Brightness")
            && nightSkybox.HasProperty("_BackGroundColorTop") && nightSkybox.HasProperty("_BackGroundColorBottom")
            && nightSkybox.HasProperty("_MoonColor") && nightSkybox.HasProperty("_BloomColor"))
        {
            hasNightProps_ = true;
            nightBrightBase_ = nightSkybox.GetFloat("_Brightness");
            nightTopBase_ = nightSkybox.GetColor("_BackGroundColorTop");
            nightBottomBase_ = nightSkybox.GetColor("_BackGroundColorBottom");
            nightMoonBase_ = nightSkybox.GetColor("_MoonColor");
            nightBloomBase_ = nightSkybox.GetColor("_BloomColor");
            if (nightSky_ != null)
            {
                nightSky_.SetFloat("_Brightness", nightBrightBase_);
                nightSky_.SetColor("_BackGroundColorTop", nightTopBase_);
                nightSky_.SetColor("_BackGroundColorBottom", nightBottomBase_);
                nightSky_.SetColor("_MoonColor", nightMoonBase_);
                nightSky_.SetColor("_BloomColor", nightBloomBase_);
            }
        }
    }
}
