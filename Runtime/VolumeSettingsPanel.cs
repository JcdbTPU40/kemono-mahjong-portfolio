using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using VRC.SDK3.Persistence;
using VRC.SDKBase;
using VRC.Udon;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    // 音量設定パネル（ワールド内の音を各自で下げられるようにする）
    //
    // ■ 役割
    //   AssetCredits の隣に置く World Space パネル。BGM / 効果音 / ボイス / 環境音 の
    //   4カテゴリを 0〜100%（10%刻み）で調整する。**完全ローカル**＝押した本人にしか
    //   効かない。音量は個人の事情なので、他人の聞こえ方を変えてはいけない
    //   （PerformanceModeController と同じ思想）。
    //
    // ■ 効かせ方（同期変数ゼロ・Update ポーリングもゼロ）
    //   1) BGM / 効果音 / 環境音 … AudioSource.volume を直接掛ける。
    //      効果音は全て PlayOneShot(clip, volumeScale) で鳴っており、実際の音量は
    //      AudioSource.volume × volumeScale。よって .volume を触るだけで全SEに効く。
    //      基準値（100%のときの音量）は Start でシーンの値を控える＝
    //      SFX音量基準（役割別3段）を壊さずに、その上から倍率だけを掛ける。
    //   2) ボイス … MahjongSoundsV2 が発声のたびに voiceSources[i].volume を
    //      GetVoicePackVolume(pack) で上書きするため、.volume を先に書いても消される。
    //      そこで倍率を voiceAmp に置き、向こうから毎回読ませる（MahjongSoundsV2 の
    //      volumePanel 参照。未割当なら等倍＝従来動作にフォールバック）。
    //      卓は近接カリングで SetActive(false) になるが、フィールドの読み書きは
    //      非アクティブでも通る（SendCustomEvent と違って落ちない）ので卓を選ばない。
    //
    // ■ 永続化
    //   PlayerData("kmj_vol_bgm" / "kmj_vol_se" / "kmj_vol_voice" / "kmj_vol_amb")
    //   に 0〜10 の段で保存。復元は OnPlayerRestored 後なので、それより前の操作は
    //   保留して復元後に流す（PerformanceModeController / MahjongVoiceSelector と同じ作法）。
    //   ※本コンポーネントは常時アクティブな MainMenu/VolumeSettings に置く。
    //     UI の ScreenWorld は入場時に非アクティブで、そこに置くと OnPlayerRestored
    //     （入場時1回きり）を取りこぼし、保存も復元も一度も走らなかった（2026-09-27）。
    //
    // ■ カーブ
    //   振幅 = (段/10)^2。等倍リニアだと 50% にしてもほとんど下がって聞こえないため、
    //   50%＝シーンで調整・測定した通常音量（0dB）。0段は完全ミュート、100%は+3.5dB。
    //   下げ側は 1段 -2.8dB（10%＝-11.2dB）。上げ側の幅は分類ごとに違う
    //   （効果音 +11.2 / ボイス +9.0 / 環境音 +3.5dB）＝素材の実力どおり。
    //
    // ■ 使い方
    //   Tools > Kemono Mahjong > Build Volume Panel   … 生成＋対象の自動収集＋配線
    //   Tools > Kemono Mahjong > Validate Volume Panel … 参照欠けの検査
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class VolumeSettingsPanel : UdonSharpBehaviour
    {
        [Header("表示言語（Wire Language で自動割り当て）")]
        [Tooltip("値ラベル（ミュート／標準）の言語。未割当なら日本語のまま。")]
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
        private bool Zhs() { return Lang() == KemonoLanguage.LANG_ZHS; }
        private bool Zht() { return Lang() == KemonoLanguage.LANG_ZHT; }
        
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
            UpdateView();
        }
        
        // PlayerData キー（値は 0〜10 の段）
        public const string KEY_BGM = "kmj_vol_bgm";
        public const string KEY_DISCARD = "kmj_vol_dahai";
        public const string KEY_FX = "kmj_vol_fx";
        public const string KEY_VOICE = "kmj_vol_voice";
        public const string KEY_AMB = "kmj_vol_amb";

        public const int CAT_BGM = 0;
        public const int CAT_DISCARD = 1;  // 打牌音（常時鳴る・素材の上限に張り付いている）
        public const int CAT_FX = 2;       // 演出音（打牌音以外の効果音）
        public const int CAT_VOICE = 3;
        public const int CAT_AMB = 4;
        public const int CAT_TABLE = 5;
        public const int CAT_WARN = 6;
        public const int CAT_UI = 7;
        public const int CAT_SYSTEM = 8;
        public const int CAT_COUNT = 9;

        public const int MAX_STEP = 10;   // 10段＝10%刻み
        public const int NORMAL_STEP = 5; // 50%＝通常音量（0dB）
        public const int SEGMENTS = 10;   // バーのセグメント数（段と同数）

        [Header("対象 AudioSource（Build Volume Panel が自動収集）")]
        [Tooltip("ワールドBGM。未設置なら空＝BGM行は自動で隠れる。")]
        public AudioSource[] bgmSources;
        [Tooltip("打牌音の専用ソース（DiscardSource）。常時鳴るので独立して下げられるようにする。")]
        public AudioSource[] discardSources;
        [Tooltip("演出音。卓の SfxSource・各演出の seSource。")]
        public AudioSource[] fxSources;
        [Tooltip("ボイス系のうち、コードが volume を書かないもの（マッチング成立ボイス等）。")]
        public AudioSource[] voiceSources;
        [Tooltip("環境音（EnvironmentAudio の風・水）。")]
        public AudioSource[] ambienceSources;
        public AudioSource[] tableSources;
        public AudioSource[] warningSources;
        public AudioSource[] uiSources;
        public AudioSource[] systemSources;
        [HideInInspector] public float bgmAmp = 1f;

        [Header("UI（Build Volume Panel が自動配線）")]
        [Tooltip("カテゴリ行。対象が0本のカテゴリは Start で非表示にする。")]
        public GameObject[] rowObjects;
        [Tooltip("バーのセグメント。cat * SEGMENTS + i の順に詰める（4×10）。")]
        public Image[] segmentImages;
        [Tooltip("「100%」などの数値ラベル（4個）。")]
        public TextMeshProUGUI[] valueLabels;

        [Header("試聴（効果音・ボイスは鳴っていないと変化が分からないため）")]
        public AudioSource previewSource;
        public AudioClip discardPreviewClip;
        public AudioClip fxPreviewClip;
        public AudioClip voicePreviewClip;

        [Header("バーの色")]
        public Color segmentOnColor = new Color(0.32f, 0.78f, 0.92f, 1f);
        public Color segmentOffColor = new Color(1f, 1f, 1f, 0.12f);
        public Color segmentMuteColor = new Color(0.85f, 0.33f, 0.30f, 1f);
        [Tooltip("「50% 標準」などの数値ラベルの色（ミュート時は segmentMuteColor）。")]
        public Color valueTextColor = Color.white;

        // ボイスの倍率。MahjongSoundsV2 が発声のたびにここを読む（公開必須）。
        [HideInInspector] public float voiceAmp = 1.0f;

        // 現在の段（0〜10）
        private int[] steps_;
        // 50%のときの音量＝シーンで設定・測定した基準値
        private float[] baseDiscard_;
        private float[] baseFx_;
        private float[] baseVoice_;
        private float[] baseAmb_;
        private float[] baseTable_;
        private float[] baseWarning_;
        private float[] baseUi_;
        private float[] baseSystem_;

        private bool restored_ = false;   // 自分の PlayerData が復元済みか
        private bool pendingSave_ = false; // 復元前に操作した＝復元後に保存し直す

        void Start()
        {
            EnsureInit();
        }

        // Start と OnPlayerRestored のどちらが先に来ても段の配列と基準音量をそろえる。
        private void EnsureInit()
        {
            if (steps_ != null) { return; }

            steps_ = new int[CAT_COUNT];
            for (int i = 0; i < CAT_COUNT; ++i) { steps_[i] = NORMAL_STEP; }

            baseDiscard_ = CaptureBase(discardSources);
            baseFx_ = CaptureBase(fxSources);
            baseVoice_ = CaptureBase(voiceSources);
            baseAmb_ = CaptureBase(ambienceSources);
            baseTable_ = CaptureBase(tableSources);
            baseWarning_ = CaptureBase(warningSources);
            baseUi_ = CaptureBase(uiSources);
            baseSystem_ = CaptureBase(systemSources);

            HideEmptyRows();
            Apply();
            UpdateView();
        }

        // ============================================================
        // ボタン（Button.onClick から SendCustomEvent で呼ばれる）
        // ============================================================

        public void OnBgmDown() { Nudge(CAT_BGM, -1); }
        public void OnBgmUp() { Nudge(CAT_BGM, 1); }
        public void OnDiscardDown() { Nudge(CAT_DISCARD, -1); }
        public void OnDiscardUp() { Nudge(CAT_DISCARD, 1); }
        public void OnFxDown() { Nudge(CAT_FX, -1); }
        public void OnFxUp() { Nudge(CAT_FX, 1); }
        public void OnVoiceDown() { Nudge(CAT_VOICE, -1); }
        public void OnVoiceUp() { Nudge(CAT_VOICE, 1); }
        public void OnTableDown() { Nudge(CAT_TABLE, -1); }
        public void OnTableUp() { Nudge(CAT_TABLE, 1); }
        public void OnWarnDown() { Nudge(CAT_WARN, -1); }
        public void OnWarnUp() { Nudge(CAT_WARN, 1); }
        public void OnUiDown() { Nudge(CAT_UI, -1); }
        public void OnUiUp() { Nudge(CAT_UI, 1); }
        public void OnSystemDown() { Nudge(CAT_SYSTEM, -1); }
        public void OnSystemUp() { Nudge(CAT_SYSTEM, 1); }
        public void OnAmbDown() { Nudge(CAT_AMB, -1); }
        public void OnAmbUp() { Nudge(CAT_AMB, 1); }

        // 全カテゴリを 100% に戻す。
public void OnResetAll()
        {
            for (int i = 0; i < CAT_COUNT; ++i) { steps_[i] = NORMAL_STEP; }
            Apply();
            UpdateView();
            Save();
        }

        private void Nudge(int cat, int delta)
        {
            if (steps_ == null) { return; }

            int next = steps_[cat] + delta;
            if (next < 0) { next = 0; }
            if (next > MAX_STEP) { next = MAX_STEP; }
            if (next == steps_[cat]) { return; }

            steps_[cat] = next;
            Apply();
            UpdateView();
            Save();
            Preview(cat);
        }

        // ============================================================
        // 反映
        // ============================================================

        private void Apply()
        {
            float ampVoice = StepToAmp(CAT_VOICE, steps_[CAT_VOICE]);

            // BGM（立直BGM）の AudioSource.volume はここでは書かない。
            // MahjongRiichiBgm.Update が毎フレーム「トラック別ゲイン x bgmAmp」で上書きするので、
            // ここで書いても捨てられるうえ、音量の権威が2か所に分かれて事故る。
            // bgmSources は「BGM行を出すかどうか」の判定にだけ使う（HideEmptyRows）。
            ApplyToArray(discardSources, baseDiscard_, StepToAmp(CAT_DISCARD, steps_[CAT_DISCARD]));
            ApplyToArray(fxSources, baseFx_, StepToAmp(CAT_FX, steps_[CAT_FX]));
            ApplyToArray(voiceSources, baseVoice_, ampVoice);
            ApplyToArray(ambienceSources, baseAmb_, StepToAmp(CAT_AMB, steps_[CAT_AMB]));

            // 卓の発声は MahjongSoundsV2 が再生直前にここを読む。
            voiceAmp = ampVoice;
            bgmAmp = StepToAmp(CAT_BGM, steps_[CAT_BGM]);
            ApplyToArray(tableSources, baseTable_, StepToAmp(CAT_TABLE, steps_[CAT_TABLE]));
            ApplyToArray(warningSources, baseWarning_, StepToAmp(CAT_WARN, steps_[CAT_WARN]));
            ApplyToArray(uiSources, baseUi_, StepToAmp(CAT_UI, steps_[CAT_UI]));
            ApplyToArray(systemSources, baseSystem_, StepToAmp(CAT_SYSTEM, steps_[CAT_SYSTEM]));
        }

        private void ApplyToArray(AudioSource[] targets, float[] baseVolumes, float amp)
        {
            if (targets == null || baseVolumes == null) { return; }

            int n = targets.Length;
            if (baseVolumes.Length < n) { n = baseVolumes.Length; }

            for (int i = 0; i < n; ++i)
            {
                var src = targets[i];
                if (src == null) { continue; }
                // 基準音量は 0.707 に置いてあるので 100%(+3dB) でも 1.0 に収まる。
                // 環境音の風だけは目標レベル優先で 0.707 を超えるため、ここで頭打ちにする。
                float v = baseVolumes[i] * amp;
                if (v > 1.0f) { v = 1.0f; }
                src.volume = v;
            }
        }

        private float[] CaptureBase(AudioSource[] targets)
        {
            if (targets == null) { return new float[0]; }

            var result = new float[targets.Length];
            for (int i = 0; i < targets.Length; ++i)
            {
                result[i] = targets[i] != null ? targets[i].volume : 0.0f;
            }
            return result;
        }


        // 分類ごとの上げ幅（dB）。素材の実力で決まる上限そのもの。
        //   打牌音 +0.0（素材の上限まで出し切っているので上げ余地なし＝下げ方向だけ）
        //   演出音 +3.5（次の律速は配牌音。上限 +5.0dB まで）
        //   ボイス +9.0（Volパックの大きいクリップが律速）
        //   環境音 +3.5（2Dの風は基準音量がそのまま効くため）
        // BGM（立直BGM）は素材が控えめなので上げ方向を許す。
        private float UpRangeDb(int cat)
        {
            if (cat == CAT_VOICE) { return 9.0f; }
            if (cat == CAT_BGM) { return 6f; }
            if (cat == CAT_FX || cat >= CAT_TABLE) { return 3.5f; }
            if (cat == CAT_AMB) { return 3.5f; }
            return 0.0f;   // 打牌音＝すでに天井（下げ方向だけ効く）
        }

        // 段 → 振幅。50%（段5）が 0dB＝通常音量。
        //   下げ側は1段 -2.8dB（10% = -11.2dB）、上げ側は分類の上げ幅を5段で割る。
        //   打牌音なら 10%=-14.0dB / 50%=-2.8dB（60%以上は50%と同じ）。
        //   演出音なら 10%=-10.0dB / 50%=+1.2dB / 100%=+4.7dB。
        // 上限が素材で決まるのは、AudioSource.volume が 1.0 でクランプされ、
        // VRCSpatialAudioSource.Gain も上限24dBを使い切っているため。
        private float StepToAmp(int cat, int step)
        {
            if (step <= 0) { return 0.0f; }
            if (step == NORMAL_STEP) { return 1.0f; }

            float db;
            if (step < NORMAL_STEP)
            {
                db = (step - NORMAL_STEP) * 2.8f;
            }
            else
            {
                db = (step - NORMAL_STEP) * (UpRangeDb(cat) / (float)(MAX_STEP - NORMAL_STEP));
            }
            return Mathf.Pow(10.0f, db / 20.0f);
        }

        // 変更したカテゴリの音を1回鳴らす（効果音・ボイスのみ）。
        // 鳴っていない音は下げても変化が分からず、押し続けて0にしてしまうため。
        private void Preview(int cat)
        {
            if (previewSource == null) { return; }

            AudioClip clip = null;
            float amp = 0.0f;

            if (cat == CAT_DISCARD) { clip = discardPreviewClip; amp = StepToAmp(CAT_DISCARD, steps_[CAT_DISCARD]); }
            else if (cat == CAT_FX) { clip = fxPreviewClip; amp = StepToAmp(CAT_FX, steps_[CAT_FX]); }
            else if (cat == CAT_VOICE) { clip = voicePreviewClip; amp = StepToAmp(CAT_VOICE, steps_[CAT_VOICE]); }

            if (clip == null || amp <= 0.0f) { return; }

            previewSource.Stop();
            previewSource.PlayOneShot(clip, amp);
        }

        // ============================================================
        // 表示
        // ============================================================

        private void UpdateView()
        {
            // Start より先に呼ばれることがある（KemonoLanguage は Start で言語を配るが、
            // Start の実行順は保証されない）。段の配列ができる前に触ると Udon が止まる。
            if (steps_ == null || steps_.Length < CAT_COUNT) { return; }

            if (segmentImages != null)
            {
                for (int cat = 0; cat < CAT_COUNT; ++cat)
                {
                    int step = steps_[cat];
                    for (int i = 0; i < SEGMENTS; ++i)
                    {
                        int index = cat * SEGMENTS + i;
                        if (index >= segmentImages.Length) { break; }
                        var image = segmentImages[index];
                        if (image == null) { continue; }
                        image.color = i < step ? segmentOnColor : segmentOffColor;
                    }
                }
            }

            if (valueLabels != null)
            {
                for (int cat = 0; cat < CAT_COUNT; ++cat)
                {
                    if (cat >= valueLabels.Length) { break; }
                    var label = valueLabels[cat];
                    if (label == null) { continue; }

                    int step = steps_[cat];
                    if (step <= 0)
                    {
                        label.text = En() ? "Mute" : Ko() ? "음소거" : Zhs() ? "静音" : Zht() ? "靜音" : "ミュート";
                        label.color = segmentMuteColor;
                    }
                    else if (step == NORMAL_STEP)
                    {
                        // 50% が制作側の基準。どこが「普通」なのか分かるように明示する。
                        label.text = En() ? "50%  Default" : Ko() ? "50%  표준" : Zhs() ? "50% 标准" : Zht() ? "50% 標準" : "50% 標準";
                        label.color = valueTextColor;
                    }
                    else
                    {
                        label.text = (step * 10).ToString() + "%";
                        label.color = valueTextColor;
                    }
                }
            }
        }

        // 対象が1本も無いカテゴリの行は隠す（BGM未設置のうちは BGM 行を出さない）。
        private void HideEmptyRows()
        {
            if (rowObjects == null) { return; }

            for (int cat = 0; cat < CAT_COUNT; ++cat)
            {
                if (cat >= rowObjects.Length) { break; }
                var row = rowObjects[cat];
                if (row == null) { continue; }

                // ボイス行は卓の発声（MahjongSoundsV2 が voiceAmp を読む）が本体で、
                // voiceSources には現れない。ここは常に出す。
                bool has = cat == CAT_VOICE || CountAlive(GetSources(cat)) > 0;
                if (row.activeSelf != has) { row.SetActive(has); }
            }
        }

        private AudioSource[] GetSources(int cat)
        {
            if (cat == CAT_BGM) { return bgmSources; }
            if (cat == CAT_DISCARD) { return discardSources; }
            if (cat == CAT_FX) { return fxSources; }
            if (cat == CAT_AMB) { return ambienceSources; }
            if (cat == CAT_TABLE) { return tableSources; }
            if (cat == CAT_WARN) { return warningSources; }
            if (cat == CAT_UI) { return uiSources; }
            if (cat == CAT_SYSTEM) { return systemSources; }
            return voiceSources;
        }

        private int CountAlive(AudioSource[] targets)
        {
            if (targets == null) { return 0; }
            int count = 0;
            for (int i = 0; i < targets.Length; ++i)
            {
                if (targets[i] != null) { ++count; }
            }
            return count;
        }

        // ============================================================
        // 永続化
        // ============================================================

        public override void OnPlayerRestored(VRCPlayerApi player)
        {
            if (player == null || !player.isLocal) { return; }

            EnsureInit();
            restored_ = true;

            if (pendingSave_)
            {
                // 復元前に操作していた＝そちらが本人の最新の意思。保存し直す。
                pendingSave_ = false;
                Save();
                return;
            }

            steps_[CAT_BGM] = LoadStep(player, KEY_BGM);
            steps_[CAT_DISCARD] = LoadStep(player, KEY_DISCARD);
            steps_[CAT_FX] = LoadStep(player, KEY_FX);
            steps_[CAT_VOICE] = LoadStep(player, KEY_VOICE);
            steps_[CAT_AMB] = LoadStep(player, KEY_AMB);
            steps_[CAT_TABLE] = LoadSplitStep(player, "kmj_vol_table");
            steps_[CAT_WARN] = LoadSplitStep(player, "kmj_vol_warn");
            steps_[CAT_UI] = LoadSplitStep(player, "kmj_vol_ui");
            steps_[CAT_SYSTEM] = LoadSplitStep(player, "kmj_vol_system");

            Apply();
            UpdateView();
        }

        private int LoadSplitStep(VRCPlayerApi player, string key)
        {
            int value;
            return PlayerData.TryGetInt(player, key, out value) ? Mathf.Clamp(value, 0, MAX_STEP) : LoadStep(player, KEY_FX);
        }

        private int LoadStep(VRCPlayerApi player, string key)
        {
            int v;
            if (!PlayerData.TryGetInt(player, key, out v)) { return NORMAL_STEP; }
            if (v < 0) { return 0; }
            if (v > MAX_STEP) { return MAX_STEP; }
            return v;
        }

        private void Save()
        {
            if (!restored_)
            {
                pendingSave_ = true;
                return;
            }

            PlayerData.SetInt(KEY_BGM, steps_[CAT_BGM]);
            PlayerData.SetInt(KEY_DISCARD, steps_[CAT_DISCARD]);
            PlayerData.SetInt(KEY_FX, steps_[CAT_FX]);
            PlayerData.SetInt(KEY_VOICE, steps_[CAT_VOICE]);
            PlayerData.SetInt(KEY_AMB, steps_[CAT_AMB]);
            PlayerData.SetInt("kmj_vol_table", steps_[CAT_TABLE]);
            PlayerData.SetInt("kmj_vol_warn", steps_[CAT_WARN]);
            PlayerData.SetInt("kmj_vol_ui", steps_[CAT_UI]);
            PlayerData.SetInt("kmj_vol_system", steps_[CAT_SYSTEM]);
        }
    }
}
