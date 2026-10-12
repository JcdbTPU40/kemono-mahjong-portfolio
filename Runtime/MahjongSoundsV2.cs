using UdonSharp;
using UnityEngine;
using VRC.SDK3.Persistence;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    // 統合サウンドコンポーネント（旧 MahjongSoundPlayer + MahjongSoundsV2）
    //
    // ■ 役割
    //   1) 発声（立直/チー/ポン/カン/北抜き/ロン/ツモ/よろしく/役読み上げ）
    //      … 席別AudioSource(voiceSources[0..3])で再生。キュー式で順番に読む。
    //   2) SFX（自ターンチャイム/打牌音/システムアナウンス）
    //      … 専用 sfxSource で PlayOneShot。発声と別ソースなので発声を打ち切らない。
    //      （旧構成はチャイムと発声が同じAudioSourceを共有し、Stop()で発声が
    //        消える事故があった。構造的に解消。）
    //   3) BGM連携 … 卓ごとに MahjongRiichiBgm を1個持つ（2D・立直中だけ鳴る）。
    //      この卓で立直が起きているとき worldBgm.ClaimRiichi() で立直BGMを要求する
    //      （ローカル駆動＝後入室にも追従）。ワールドの常時BGMは廃止済み。
    //
    // ■ ボイスパック（キャラCV）
    //   全ての AudioClip[] は「パックID」インデックス。
    //     [0] = デフォルト（アナウンサー）… 必ず埋める
    //     [1..] = 追加キャラ（例: 元気な少女/元気な少年/冷淡/母性 …自由に拡張）
    //   パックIDは発声時に 席→seatPlayerId→PlayerData("kmj_voice_pack") で
    //   遅延解決する（新規同期変数ゼロ。後入室/席替え/CPU席=0に構造的に追従）。
    //   該当パックのクリップが無ければ要素0にフォールバックするため、
    //   キャラ側は8発声＋よろしく等だけ収録してあれば成立する。
    //
    // ■ ネットワーク
    //   Play*(seat) は「アクションした本人（卓Owner側）」からのみ呼ばれ、内部で
    //   SendCustomNetworkEvent(All) により全員へ配信される（従来どおり）。
    //   OnMyTurn / UpdateBgm はローカル専用。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class MahjongSoundsV2 : UdonSharpBehaviour
    {
        // PlayerData のキー（MahjongVoiceSelector が書き、ここが読む）
        public const string VOICE_PACK_KEY = "kmj_voice_pack";

        // 「ボイスなし」を表すパックID。
        // ・実在パックの添字と衝突しない十分大きい非負値にしてある
        //   （Udonで負のintを扱うと事故りやすいので番兵に負値は使わない）。
        // ・このパックのときは PickPackClip が null を返す＝**そもそもキューに積まれない**。
        //   クリップ長で進む発声キューに null を入れると停止するので、入口で弾くのが重要。
        // ・声が積まれないので GetAgariStepCount は0のまま。あがり画面は
        //   「音声が来ない環境」用の自動送りで役行を出す（間隔は voicelessStepSeconds）。
        public const int VOICE_PACK_SILENT = 100;

        // ドラ読み上げの種類
        public const int DORA_TYPE_OMOTE = 0; // 表ドラ
        public const int DORA_TYPE_URA = 1;   // 裏ドラ
        public const int DORA_TYPE_AKA = 2;   // 赤ドラ
        public const int DORA_TYPE_KITA = 3;  // 抜きドラ（北）

        // ================= AudioSource =================
        [Header("AudioSources（未設定なら子 AudioSource0..3 / SfxSource を自動検索）")]
        public AudioSource[] voiceSources;  // 席別発声用 4本
        public AudioSource sfxSource;       // チャイム/打牌音/システムアナウンス用
        // 打牌音の専用ソース。音量パネルで「打牌音」と「演出音」を別々に下げられるように
        // するため、SfxSource から分けてある。未割当なら sfxSource へフォールバックする。
        public AudioSource discardSource;
        // 持ち時間の警告音（3秒・5秒のカウントダウン）専用ソース。手番が終わったら
        // 途中で Stop する必要があるので、共用の SfxSource とは分けてある
        // （共用ソースを止めると同時に鳴っている役読み・アナウンスまで切れる）。
        // 未割当なら従来どおり sfxSource へ落とす（その場合は止められない）。
        public AudioSource warnSource;

        // キャラクターごとの録音レベル差を再生時に補正する（パックID順）。
        // 全276クリップの個別補正を重ね、70%時に同一ターゲットへ揃える。
        [Header("キャラクターボイス音量（0=Vol / 1=Shine / 2=Tsumuntema）")]
        public float[] voicePackVolumes = new float[] { 1.0f, 0.53f, 0.28f };

        [Header("ボイス個別ノーマライズ（Kemono Audio Normalizer が生成）")]
        public AudioClip[] voiceNormalizationClips;
        public float[] voiceNormalizationGains;
        [Tooltip("同じクリップを SfxSource から鳴らすときの倍率。システムアナウンス（対局開始・流局）は" +
                 "ボイス素材だが効果音として鳴るため、効果音の目標レベルに揃えた値を使う。")]
        public float[] voiceAnnounceScales;

        // CPU席（誰も座っていない席）の既定ボイスパック。席順 0..3。
        // 全席が同じ声だと誰が喋ったのか分からないので、席ごとに散らしてある。
        // 人が座っている席はここを見ず、その人の PlayerData を引く。
        [Header("CPU席の既定ボイス（席順0..3 / 0=Vol 1=Shine 2=Tsumuntema）")]
        public int[] cpuVoicePacks = new int[] { 0, 1, 2, 0 };

        // 連続読み上げ（役→役→ドラ→格）の間隔調整（秒）。
        // 負値=次のクリップを前倒しで開始して間を詰める（録音末尾の無音ぶんを相殺）。
        // 正値=逆に間を空ける。次クリップ開始時に前クリップはStopされるので、
        // 詰めすぎると語尾が切れる。-0.1〜-0.2あたりから実際に聞いて調整。
        [Header("読み上げ間隔調整（秒・負値で間を詰める）")]
        public float voiceGapAdjust = -0.1f;

        // 格読み（満貫〜役満）の直前に置く間（秒）。
        // あがり画面の演出「役読み上げ→点数表示→一拍→称号バナー＋格読み」を
        // 成立させるためのキュー内プリディレイ。0で従来どおり間なし。
        [Header("格読み前の間（秒・称号バナー演出用）")]
        public float rankVoicePreDelaySeconds = 0.8f;

        // 称号バナー（＋SFX）が出てから格読みボイスを鳴らすまでの間（秒）。
        // バナーの「ドーン」と声が同時だと声が潰れるため、先にバナー＋SFX、
        // 少し遅れて声、という段取りにする。0で従来どおり同時。
        // 実際の並びは 点数表示 →(rankVoicePreDelaySeconds)→ バナー＋SFX
        //              →(rankVoiceLeadSeconds)→ 格読みボイス。
        [Header("称号バナー→格読みボイスの間（秒）")]
        public float rankVoiceLeadSeconds = 0.5f;

        // ================= チュンビーム =================
        // キャラ×4段。並びは [キャラID*4 + 段-1]（0..3=Vol / 4..7=Shine）。
        // 素材は Assets/Mahjong/Sounds/{Vol|Shine} に ChunBeam_Shine_1〜4 または
        // chunbeam1x / 2x / 3x / final - Shine の名前で置けば
        // Tools > Kemono Mahjong > Wire Chun Beam Voices が割り当てる。
        // 空きは声なし（他キャラの声では代用しない）。
        // 宣言ボイスと違い**各クライアントがローカルで鳴らす**（打牌のエッジを全員が
        // 自分で見ているので、ネットワークで配ると二重になる）。発声キューにも積まない
        // （直後のポン・ロンの声を遅らせないため）。
        [Header("チュンビームのボイス（[キャラID*4 + 段-1]）")]
        public AudioClip[] chunBeamVoices = new AudioClip[8];

        // 席のボイス用ソースで鳴らし、鳴り終わるまでの秒数を返す。鳴らさなかったら0。
        // その席が発声キューで喋っている最中（鳴いた直後の「ポン！」など）は割り込まず、
        // 今の声が終わった直後に鳴るよう1本だけ予約する（以前はここで捨てていたため、
        // 鳴いてすぐ中を切ると帯だけ出て声が出なかった。2026-10-03）。
        // 待ちが CHUN_VOICE_MAX_DEFER を超える（和了の読み上げ中など）なら従来どおり鳴らさない。
        private const float CHUN_VOICE_MAX_DEFER = 1.5f;
        private AudioClip[] chunPendingClip_ = new AudioClip[4];
        private int[] chunPendingChara_ = new int[4];
        private float[] chunPendingUntil_ = new float[4];

        public float PlayChunBeamVoice(int seat, int chara, int level)
        {
            if (seat < 0 || seat >= 4 || level < 1 || chara < 0) { return 0.0f; }
            if (level > 4) { level = 4; }
            if (chunBeamVoices == null) { return 0.0f; }

            int index = chara * 4 + (level - 1);
            if (index < 0 || index >= chunBeamVoices.Length) { return 0.0f; }
            var clip = chunBeamVoices[index];
            if (clip == null) { return 0.0f; }

            Initialize();
            if (voiceSources == null || seat >= voiceSources.Length || voiceSources[seat] == null) { return 0.0f; }

            chunPendingClip_[seat] = null;
            if (isPlaying_ != null && seat < isPlaying_.Length && isPlaying_[seat])
            {
                float wait = EstimateVoiceBusySeconds(seat);
                if (wait < 0.0f || wait > CHUN_VOICE_MAX_DEFER) { return 0.0f; }

                chunPendingClip_[seat] = clip;
                chunPendingChara_[seat] = chara;
                chunPendingUntil_[seat] = Time.time + CHUN_VOICE_MAX_DEFER + 0.5f;
                return wait + clip.length;
            }

            StartChunBeamVoice(seat, clip, chara);
            return clip.length;
        }

        private void StartChunBeamVoice(int seat, AudioClip clip, int chara)
        {
            var source = voiceSources[seat];
            source.Stop();
            source.clip = clip;
            float userVoiceAmp = volumePanel != null ? volumePanel.voiceAmp : 1.0f;
            source.volume = GetVoicePackVolume(chara) * GetVoiceClipGain(clip) * userVoiceAmp;
            source.Play();
        }

        // その席の発声キューが空くまでの見込み秒数。和了の読み上げ（保留・格読みの間）を
        // 含むなら -1（待つ対象ではない）。
        private float EstimateVoiceBusySeconds(int seat)
        {
            float wait = nextAdvanceTime_[seat] - Time.time;
            if (wait < 0.0f) { wait = 0.0f; }

            AudioClip[] queue = audioQueues_[seat];
            bool[] tags = agariTagQueues_[seat];
            bool[] delays = rankDelayQueues_[seat];
            for (int i = 0; i < queue.Length; ++i)
            {
                if (tags != null && i < tags.Length && tags[i]) { return -1.0f; }
                if (delays != null && i < delays.Length && delays[i]) { return -1.0f; }
                if (queue[i] == null) { continue; }
                float step = queue[i].length + voiceGapAdjust;
                wait += step < 0.05f ? 0.05f : step;
            }
            return wait;
        }

        // 予約したチュンビームの声を、席の発声が終わった直後に鳴らす（Update から）。
        private void AdvanceChunBeamPending()
        {
            for (int i = 0; i < 4; ++i)
            {
                if (chunPendingClip_[i] == null) { continue; }
                if (Time.time > chunPendingUntil_[i]) { chunPendingClip_[i] = null; continue; }
                if (isPlaying_[i]) { continue; }

                var clip = chunPendingClip_[i];
                chunPendingClip_[i] = null;
                if (voiceSources == null || i >= voiceSources.Length || voiceSources[i] == null) { continue; }
                StartChunBeamVoice(i, clip, chunPendingChara_[i]);
            }
        }

        // チュンビームの声だけを止める（その中でロンされたとき）。宣言ボイスは止めない。
        public void StopChunBeamVoice(int seat)
        {
            if (seat < 0 || seat >= 4 || chunBeamVoices == null) { return; }
            chunPendingClip_[seat] = null;
            if (voiceSources == null || seat >= voiceSources.Length || voiceSources[seat] == null) { return; }

            var source = voiceSources[seat];
            if (!source.isPlaying || source.clip == null) { return; }
            for (int i = 0; i < chunBeamVoices.Length; ++i)
            {
                if (chunBeamVoices[i] != null && chunBeamVoices[i] == source.clip)
                {
                    source.Stop();
                    return;
                }
            }
        }

        // ================= VFX（システム音） =================
        [Header("VFX：システム音（パック無し）")]
        public AudioClip myTurn;            // 自分のターンを知らせる音
        public AudioClip discard;           // 打牌音
        public AudioClip haipai;            // 配牌音
        [Range(0.0f, 1.0f)] public float myTurnSfxVolume = 0.337f;
        [Range(0.0f, 1.0f)] public float discardSfxVolume = 0.697f;
        [Range(0.0f, 1.0f)] public float haipaiSfxVolume = 0.065f;

        // ---- 打牌まわりの追加SE（すべて discardSource ＝音量パネルの「打牌音」）----
        // 音量は Normalize Audio が実測RMSから入れ直す。手でInspectorを触っても巻き戻る。
        // 新しい素材を足すときは KemonoAudioNormalizer.IsTileDiscard() にも登録すること
        // （登録漏れは +4dB の下駄と基準音量の差で合計 +7.5dB 大きくなる）。
        [Header("打牌まわりの追加SE（打牌音スライダー）")]
        public AudioClip discardDora;       // ドラ牌（表・カンドラ・赤）を切ったとき
        public AudioClip nakiArrange;       // 鳴いた牌を持ってきて整理する音
        [Range(0.0f, 1.0f)] public float discardDoraSfxVolume = 0.697f;
        [Range(0.0f, 1.0f)] public float nakiArrangeVolume = 0.697f;

        // ---- 演出SE（sfxSource ＝音量パネルの「演出音」）----
        [Header("演出SE（演出音スライダー）")]
        public AudioClip reachStick;        // 立直棒を置く音
        public AudioClip thinkWarn;         // 思考時間が終わる3秒前
        public AudioClip thinkWarnLong;     // 長考（追加持ち時間）が終わる5秒前
        [Range(0.0f, 1.0f)] public float reachStickVolume = 0.12f;
        [Range(0.0f, 1.0f)] public float thinkWarnVolume = 0.12f;
        [Range(0.0f, 1.0f)] public float thinkWarnLongVolume = 0.12f;

        // ================= あがり画面SFX =================
        // 役読み上げ/点数表示/称号バナーに重ねる効果音。あがり画面(MahjongAgariScreen)が
        // 各クライアントで自分の画面の進行に合わせてローカル再生する（同期変数ゼロ・
        // ネットワークイベントも消費しない）。未設定なら無音＝従来動作。
        [Header("あがり画面SFX（役読み/点数/称号・ローカル再生）")]
        public AudioClip agariYakuRowSfx;   // 役行が1つ（同一ステップは1回）出るたび
        public AudioClip agariScoreSfx;     // 点数表示のドーン
        public AudioClip agariRankSfx;      // 満貫/跳満/倍満/三倍満/役満の称号バナー
        // 音量は和了演出SE（MahjongAgariEffect.seVolume=0.20）と同値。あちらと同じ素材を
        // 使う場面があるため、揃えておかないと同じ音が場面によって鳴り方が変わって聞こえる。
        [Range(0.0f, 1.0f)] public float agariYakuRowSfxVolume = 0.123f;
        [Range(0.0f, 1.0f)] public float agariScoreSfxVolume = 0.028f;
        [Range(0.0f, 1.0f)] public float agariRankSfxVolume = 0.022f;


        // ================= システムアナウンス =================
        // 特定の席に紐づかないため「聞く側が選んだキャラ」の声で鳴らす
        // （ローカルプレイヤーのPlayerDataでパック解決。未選択/未収録は[0]）。
        [Header("システムアナウンス（パック別・聞く側のキャラ声）")]
        public AudioClip[] taikyokuKaishi;  // 対局開始
        public AudioClip[] suufonrenda;     // 四風連打（流局アナウンス）
        public AudioClip[] suukansanra;     // 四槓散了（流局アナウンス）
        public AudioClip[] suuchariichi;    // 四家立直（流局アナウンス）

        // ================= BGM =================
        // 立直BGMは卓ごとの MahjongRiichiBgm が鳴らす。鳴る条件（近接）は向こうが持つ。
        [Header("BGM（この卓の MahjongRiichiBgm への参照）")]
        public MahjongRiichiBgm worldBgm;
        public AudioSource tableOperationSource;
        public AudioSource systemSource;
        public AudioSource announceSource;

        // 各自のローカル音量（音量設定パネル）。未割当なら等倍＝従来動作。
        // 発声は再生のたびにここを読む（AudioSource.volume を毎回上書きするため、
        // パネル側から .volume を書いても消えてしまう）。
        [Header("音量設定パネル（ローカル音量・Build Volume Panel が割当）")]
        public VolumeSettingsPanel volumePanel;

        [Header("和了ジングル（パック別・[0]必須）")]
        public AudioClip[] agariJingle;     // 和了時ジングル（和了者のパックで選択）
        [Range(0.0f, 1.0f)]
        public float agariJingleVolume = 0.16f;

        [Header("和了VFX（任意。指定時、和了で一定時間Active化）")]
        public GameObject winVfxObject;
        public float winVfxDuration = 3.0f;

        // ================= 発声：試合中 =================
        [Header("発声：試合中（パック別）")]
        public AudioClip[] riichi;          // 立直（宣言時）
        public AudioClip[] doubleRiichi;    // ダブル立直（宣言時）
        public AudioClip[] chi;             // チー
        public AudioClip[] pon;             // ポン
        public AudioClip[] kan;             // カン
        public AudioClip[] nukidora;        // 北抜き
        public AudioClip[] ron;             // ロン
        public AudioClip[] tsumo;           // ツモ（宣言時）

        // ================= 発声：局開始前 =================
        [Header("発声：局開始前（パック別）")]
        public AudioClip[] yoroshiku;       // よろしくお願いします

        // ================= 役読み上げ：通常役 =================
        [Header("役読み上げ（パック別・[0]=アナウンサー必須）")]
        public AudioClip[] chankan;         // 槍槓
        public AudioClip[] rinshankaihou;   // 嶺上開花
        public AudioClip[] haiteiRaoyue;    // 海底摸月
        public AudioClip[] houteiRaoyui;    // 河底撈魚
        public AudioClip[] ton;             // 東
        public AudioClip[] nan;             // 南
        public AudioClip[] sha;             // 西
        public AudioClip[] pey;             // 北
        public AudioClip[] chun;            // 中
        public AudioClip[] haku;            // 白
        public AudioClip[] hatsu;           // 発
        public AudioClip[] doubleTon;       // ダブル東
        public AudioClip[] doubleNan;       // ダブル南
        public AudioClip[] doubleSha;       // ダブル西
        public AudioClip[] doublePey;       // ダブル北
        public AudioClip[] tanyao;          // 断幺九
        public AudioClip[] iipeeikou;       // 一盃口
        public AudioClip[] pinfu;           // 平和
        public AudioClip[] chanta;          // 混全帯幺九
        public AudioClip[] ikkitsuukan;     // 一気通貫
        public AudioClip[] sanshokudoujun;  // 三色同順
        public AudioClip[] sanshokudoukou;  // 三色同刻
        public AudioClip[] sankantsu;       // 三槓子
        public AudioClip[] toitoi;          // 対々和
        public AudioClip[] sanankou;        // 三暗刻
        public AudioClip[] shousangen;      // 小三元
        public AudioClip[] honroutou;       // 混老頭
        public AudioClip[] chitoitsu;       // 七対子
        public AudioClip[] junchan;         // 純全帯幺九
        public AudioClip[] honitsu;         // 混一色
        public AudioClip[] ryanpeikou;      // 二盃口
        public AudioClip[] chinitsu;        // 清一色
        public AudioClip[] riichiAgari;     // 立直（上がり時読み上げ）
        public AudioClip[] doubleRiichiAgari; // ダブル立直（上がり時読み上げ）
        public AudioClip[] menzenTsumo;     // 門前清自摸和
        public AudioClip[] ippatsu;         // 一発

        // ================= 役読み上げ：役満・特殊 =================
        [Header("役読み上げ：役満・特殊（パック別）")]
        public AudioClip[] tenhou;          // 天和
        public AudioClip[] chiihou;         // 地和
        public AudioClip[] daisangen;       // 大三元
        public AudioClip[] suuankou;        // 四暗刻
        public AudioClip[] suuankouTankimachi; // 四暗刻単騎
        public AudioClip[] tsuisou;         // 字一色
        public AudioClip[] ryuuisou;        // 緑一色
        public AudioClip[] chinroutou;      // 清老頭
        public AudioClip[] kokushimusou;    // 国士無双
        public AudioClip[] kokushimusouJuusanmenmachi; // 国士無双十三面待ち
        public AudioClip[] daisushi;        // 大四喜
        public AudioClip[] shousushi;       // 小四喜
        public AudioClip[] suukantsu;       // 四槓子
        public AudioClip[] chuurenpoutou;   // 九蓮宝燈
        public AudioClip[] junseiChuurenpoutou; // 純正九蓮宝燈（エンジン側判定 実装済み：YAKU_JUNSEICHUREN）
        public AudioClip[] nagashimangan;   // 流し満貫

        // ================= 読み上げ：点数格 =================
        [Header("読み上げ：点数格（パック別）")]
        public AudioClip[] mangan;          // 満貫
        public AudioClip[] haneman;         // 跳満
        public AudioClip[] baiman;          // 倍満
        public AudioClip[] sanbaiman;       // 三倍満
        public AudioClip[] yakuman;         // 役満
        public AudioClip[] kazoeYakuman;    // 数え役満
        public AudioClip[] doubleYakuman;   // ダブル役満
        public AudioClip[] tripleYakuman;   // トリプル役満
        public AudioClip[] yakuman4Times;   // 四倍役満
        public AudioClip[] yakuman5Times;   // 五倍役満
        public AudioClip[] yakuman6Times;   // 六倍役満

        // ================= 読み上げ：流局系 =================
        [Header("読み上げ：流局系（パック別）")]
        public AudioClip[] tenpai;          // 聴牌
        public AudioClip[] noten;           // ノーテン
        public AudioClip[] kyuushukyuuhai;  // 九種九牌

        // ================= 読み上げ：ドラ枚数 =================
        // 表ドラ（抜きドラ合算）・裏ドラ・赤ドラすべて共通でこのクリップを使う。
        // （種類ごとに枚数は分けて読むが、音声は同一。例：表2+裏1 →「ドラ2」「ドラ1」）
        [Header("読み上げ：ドラ枚数（パック別。表/抜き/裏/赤 共通）")]
        public AudioClip[] dora1;
        public AudioClip[] dora2;
        public AudioClip[] dora3;
        public AudioClip[] dora4;
        public AudioClip[] dora5;
        public AudioClip[] dora6;
        public AudioClip[] dora7;
        public AudioClip[] dora8;
        public AudioClip[] dora9;
        public AudioClip[] dora10;
        public AudioClip[] dora11;
        public AudioClip[] dora12;
        public AudioClip[] dora13Plus;      // ドラ13以上

        // ================= 内部状態 =================
        private Mahjong mahjong_;                       // 席→playerId 解決用（Mahjong.Initializeが注入）
        private AudioClip[][] audioQueues_ = new AudioClip[4][];
        // audioQueues_ と同じ順序でパックIDを保持し、再生時に音量補正へ使う。
        private int[][] packQueues_ = new int[4][];
        // あがり読み上げタグ（audioQueues_ と同じ長さ・同じ順で維持する並行配列）。
        // true のクリップが「鳴り始めた」瞬間に agariStepsStarted_ を進め、
        // あがり画面パネル(MahjongAgariScreen)がポーリングして役行を1つずつ表示する。
        private bool[][] agariTagQueues_ = new bool[4][];
        // 席ごとの「開始済みあがり読み上げクリップ数」。ロン/ツモ宣言と局跨ぎClearで0に戻る。
        // ネットワークイベントは同一送信者から同一順で届くため、全クライアントで
        // 同じ順序のキューになり、この段数がそのまま表示ステップとして使える。
        private int[] agariStepsStarted_ = new int[4];
        // 席ごとの「ロン/ツモ宣言ボイスが鳴り終わる時刻」（Time.time基準・0＝不明）。
        // 和了の段取り（宣言ボイス → 手牌公開 → 牌エフェクト → あがり画面）で、
        // 手牌公開をボイスの実尺に合わせるために Mahjong.cs が読む。
        // ボイスパックごとにクリップ長が違うため、固定秒だと被る／間延びする。
        private float[] declareVoiceEndTime_ = new float[4];
        // 席ごとの「あがり読み上げを始めてよい時刻」（Time.time基準・0＝保留なし）。
        // 雀魂式の段取りでは役読みは あがり画面 と同時に始まる。宣言ボイスの直後に
        // 読み始めると、手牌公開・牌エフェクトの最中に役が読まれて段取りが崩れるため、
        // Mahjong.cs（各クライアント）が自分の画面が開く時刻をここへ渡して待たせる。
        private float[] agariReadoutHoldUntil_ = new float[4];

        // あがり読み上げの解禁時刻が届くまでの暫定の伏せ時間（秒）。
        // 読み上げを積むのは Owner の SendCustomNetworkEvent、解禁時刻を決めるのは
        // 各クライアントが同期を受け取ってから（Mahjong.PrepareAgariStages）なので、
        // 積む方が先に着くと解禁時刻ゼロのまま鳴り始めてしまう。積んだ時点で一旦
        // 伏せておき、直後に届く本来の時刻で上書きさせる。
        // 万一その同期が来なくても、この秒数で自動的に鳴り出して読み上げが消えない。
        private const float AGARI_READOUT_SAFETY_HOLD = 6.0f;

        // 伏せている間の見直し間隔（秒）。解禁時刻は後から前倒しされる
        // （暫定の伏せ → 本来の時刻）ので、その時点の解禁時刻をそのまま次回時刻に
        // すると前倒しに追従できず、暫定の秒数まで待ってしまう。
        private const float AGARI_READOUT_RECHECK_SECONDS = 0.05f;
        // 格読みタグ（audioQueues_ と並行維持）。true のクリップは再生直前に
        // rankVoicePreDelaySeconds の間を置いてから鳴らす（称号バナー演出用）。
        private bool[][] rankDelayQueues_ = new bool[4][];
        // 席ごとの「称号バナーを出す時刻」（Time.time基準・0＝予定なし）。
        // 格読みの前の間を消費した時点で確定し、あがり画面パネルがポーリングして
        // バナー＋SFXを出す。ボイスはこの時刻から rankVoiceLeadSeconds 後に鳴る。
        private float[] rankBannerCueTime_ = new float[4];
        private bool[] isPlaying_ = new bool[4];
        // 次クリップへ進める時刻（Time.time 基準）。SendCustomEventDelayedSeconds の予約は
        // ClearQueue で取り消せず、局跨ぎで旧コールバックが新しい発声を切る/二重化する事故を
        // 起こしていた。Update() ポーリング＋この時刻で駆動し、キューを空にすれば進まないので
        // 予約残存の問題自体が起きない（世代管理不要）。
        private float[] nextAdvanceTime_ = new float[4];
        private bool initialized_ = false;
        // voiceSources[i]==null の LogError を席ごとに1回だけ出す（毎フレーム禁止）。
        private bool[] voiceSourceNullLogged_ = new bool[4];
        // 和了 VFX の非表示時刻（Time.time）。負＝非表示待ちなし。
        // Delayed 複数予約の早消え（ダブロン）を避けるため Update ポーリングで消す（P1B-23）。
        private float winVfxHideAt_ = -1.0f;

        public void SetMahjong(Mahjong m)
        {
            mahjong_ = m;
        }

        // 旧API互換（旧Mahjong.csがSoundPlayerの4本を渡していた口）。voiceSourcesに取り込む。
        public void SetAudioSources(AudioSource[] audioSources)
        {
            voiceSources = audioSources;
        }

        private void Initialize()
        {
            if (initialized_) { return; }

            for (int i = 0; i < 4; i++)
            {
                audioQueues_[i] = new AudioClip[0];
                packQueues_[i] = new int[0];
                agariTagQueues_[i] = new bool[0];
                rankDelayQueues_[i] = new bool[0];
                agariStepsStarted_[i] = 0;
                declareVoiceEndTime_[i] = 0.0f;
                agariReadoutHoldUntil_[i] = 0.0f;
                rankBannerCueTime_[i] = 0.0f;
                isPlaying_[i] = false;
                chunPendingClip_[i] = null;
            }

            // 未配線／欠席があれば子オブジェクトから自動検索（[0] だけでなく 0..3 を見る）。
            bool needFind = voiceSources == null || voiceSources.Length < 4;
            if (!needFind)
            {
                for (int i = 0; i < 4; i++)
                {
                    if (voiceSources[i] == null) { needFind = true; break; }
                }
            }
            if (needFind)
            {
                var found = new AudioSource[4];
                for (int i = 0; i < 4; i++)
                {
                    var t = transform.Find("AudioSource" + i);
                    if (t != null) { found[i] = t.GetComponent<AudioSource>(); }
                }
                if (voiceSources == null || voiceSources.Length < 4)
                {
                    voiceSources = found;
                }
                else
                {
                    for (int i = 0; i < 4; i++)
                    {
                        if (voiceSources[i] == null && found[i] != null)
                        {
                            voiceSources[i] = found[i];
                        }
                    }
                }
            }

            for (int i = 0; i < 4; i++)
            {
                if (voiceSources == null || voiceSources.Length <= i || voiceSources[i] == null)
                {
                    LogVoiceSourceNullOnce(i);
                }
            }

            if (sfxSource == null)
            {
                var t = transform.Find("SfxSource");
                if (t != null) { sfxSource = t.GetComponent<AudioSource>(); }
            }

            initialized_ = true;
        }

        private void LogVoiceSourceNullOnce(int seat)
        {
            if (seat < 0 || seat >= 4) { return; }
            if (voiceSourceNullLogged_[seat]) { return; }
            voiceSourceNullLogged_[seat] = true;
            Debug.LogError("[MahjongSoundsV2] voiceSources[" + seat + "] が null です。発声できません。");
        }

        // ============================================================
        // ボイスパック解決
        // ============================================================

        // 席に座っているプレイヤーが選んだボイスパックIDを返す。
        // seatPlayerId(同期済) → VRCPlayerApi → PlayerData を毎回引く遅延解決。
        // 未設定/CPU席/退室済みは 0（デフォルト）。
        public int ResolveSeatPack(int seat)
        {
            if (mahjong_ == null) { return 0; }
            if (seat < 0 || seat >= 4) { return 0; }

            // CPU席・退室済みは席ごとの既定パック（人が座っている席だけ PlayerData を引く）。
            int playerId = mahjong_.GetSeatPlayerId(seat);
            if (playerId <= 0) { return ResolveCpuPack(seat); }

            var player = VRCPlayerApi.GetPlayerById(playerId);
            if (player == null || !player.IsValid()) { return ResolveCpuPack(seat); }

            int pack = 0;
            if (PlayerData.TryGetInt(player, VOICE_PACK_KEY, out pack))
            {
                return pack < 0 ? 0 : pack;
            }

            return 0;
        }

        // CPU席の既定パック。未設定・範囲外は 0（Vol）へ落とす。
        private int ResolveCpuPack(int seat)
        {
            if (cpuVoicePacks == null || seat < 0 || seat >= cpuVoicePacks.Length) { return 0; }

            int pack = cpuVoicePacks[seat];
            return pack < 0 ? 0 : pack;
        }

        // その席の人が「ボイスなし」を選んでいるか。
        // あがり画面はこれを見て、役の読み上げを声ではなく一定間隔で送る。
        public bool IsSeatVoiceless(int seat)
        {
            return ResolveSeatPack(seat) == VOICE_PACK_SILENT;
        }

        // ローカルプレイヤー自身が選んだボイスパックID（システムアナウンス用）。
        public int ResolveLocalPack()
        {
            var lp = Networking.LocalPlayer;
            if (lp == null || !lp.IsValid()) { return 0; }

            int pack = 0;
            if (PlayerData.TryGetInt(lp, VOICE_PACK_KEY, out pack))
            {
                return pack < 0 ? 0 : pack;
            }

            return 0;
        }

        // パック該当クリップ（無ければ[0]フォールバック）
        private AudioClip PickPackClip(AudioClip[] clips, int pack)
        {
            // ボイスなし。ここで null を返すと PlayVoiceWithPackTagged が積まずに帰る。
            if (pack == VOICE_PACK_SILENT) { return null; }

            if (clips == null || clips.Length == 0) { return null; }

            if (pack >= 0 && pack < clips.Length && clips[pack] != null)
            {
                return clips[pack];
            }

            return clips[0];
        }

        // ============================================================
        // 発声キュー（席別・順番に読む）
        // ============================================================

        // index = 席（0..3）。鳴らすAudioSourceは席、クリップは席のプレイヤーのパック。
        private void PlayVoice(AudioClip[] audioClips, int index)
        {
            PlayVoiceWithPackTagged(audioClips, index, ResolveSeatPack(index), false, false);
        }

        // 宣言ボイス（チー/ポン/カン/北抜き/立直）専用。同じ席が連続で宣言したとき、
        // 積んで待たせると発声が実際の操作からどんどん遅れる（北抜き連打で顕著）。
        // 前の宣言を捨てて即差し替える＝常に「今の操作」の声が鳴る。
        //
        // あがり読み上げが積まれている席は触らない。役読みは順番に読むこと自体が
        // 演出（あがり画面の行送りが GetAgariStepCount に追従する）なので、
        // ここで消すと画面の段階表示が止まる。
        private void PlayVoiceInterrupt(AudioClip[] audioClips, int index)
        {
            if (index < 0 || index >= 4) { return; }

            Initialize();

            if (!HasAgariTagQueued(index))
            {
                ClearVoiceQueueForInterrupt(index);
            }

            PlayVoiceWithPackTagged(audioClips, index, ResolveSeatPack(index), false, false);
        }

        // その席のキューにあがり読み上げクリップが1つでも積まれているか。
        private bool HasAgariTagQueued(int index)
        {
            bool[] tags = agariTagQueues_[index];
            if (tags == null) { return false; }
            for (int i = 0; i < tags.Length; ++i)
            {
                if (tags[i]) { return true; }
            }
            return false;
        }

        // 割り込み用のキュー破棄。ClearQueue と違い、あがり演出の状態
        // （agariStepsStarted_ / declareVoiceEndTime_ / agariReadoutHoldUntil_ /
        //   rankBannerCueTime_）には一切触らない。対局中の宣言で和了演出の
        // 時刻を巻き戻すと、あがり画面の段取りが崩れるため。
        private void ClearVoiceQueueForInterrupt(int index)
        {
            audioQueues_[index] = new AudioClip[0];
            packQueues_[index] = new int[0];
            agariTagQueues_[index] = new bool[0];
            rankDelayQueues_[index] = new bool[0];
            isPlaying_[index] = false;
            nextAdvanceTime_[index] = 0.0f;

            if (voiceSources != null && voiceSources.Length > index && voiceSources[index] != null)
            {
                voiceSources[index].Stop();
            }
        }

        // あがり読み上げ（役/ドラ）用。タグ付きで積み、再生開始時にステップを進める。
        private void PlayAgariVoice(AudioClip[] audioClips, int index)
        {
            PlayVoiceWithPackTagged(audioClips, index, ResolveSeatPack(index), true, false);
        }

        // 格読み（満貫〜役満）用。あがりタグに加え、再生直前に
        // rankVoicePreDelaySeconds の間を置く（点数表示→一拍→称号ドーンの演出）。
        private void PlayAgariRankVoice(AudioClip[] audioClips, int index)
        {
            PlayVoiceWithPackTagged(audioClips, index, ResolveSeatPack(index), true, true);
        }

        // パックIDを明示して鳴らす版（エントリー時など、seatPlayerIdがまだ
        // 同期されていないタイミングでは送信側で解決したパックを使う）。
        private float GetVoicePackVolume(int pack)
        {
            if (voicePackVolumes == null || pack < 0 || pack >= voicePackVolumes.Length)
            {
                return 1.0f;
            }

            float volume = voicePackVolumes[pack];
            if (volume < 0.0f) { return 0.0f; }
            if (volume > 1.0f) { return 1.0f; }
            return volume;
        }

        // クリップが正規化表の何番目か。無ければ -1。
        // 276本の線形探索だが、走るのは1クリップ鳴らすたびに1回だけ。
        private int FindVoiceClipIndex(AudioClip clip)
        {
            if (clip == null || voiceNormalizationClips == null) { return -1; }
            for (int i = 0; i < voiceNormalizationClips.Length; ++i)
            {
                if (voiceNormalizationClips[i] == clip) { return i; }
            }
            return -1;
        }

        // 席の発声に使うゲイン。表に無いクリップは従来どおりパック補正だけで鳴らす。
        private float GetVoiceClipGain(AudioClip clip)
        {
            int index = FindVoiceClipIndex(clip);
            if (index < 0 || voiceNormalizationGains == null || index >= voiceNormalizationGains.Length)
            {
                return 1.0f;
            }

            float gain = voiceNormalizationGains[index];
            if (gain < 0.001f) { return 0.001f; }
            if (gain > 1.0f) { return 1.0f; }
            return gain;
        }


        private void PlayVoiceWithPack(AudioClip[] audioClips, int index, int pack)
        {
            PlayVoiceWithPackTagged(audioClips, index, pack, false, false);
        }

        private void PlayVoiceWithPackTagged(AudioClip[] audioClips, int index, int pack, bool agariTag, bool rankDelay)
        {
            if (index < 0 || index >= 4) { return; }

            Initialize();

            var clip = PickPackClip(audioClips, pack);
            if (clip == null) { return; }

            AudioClip[] currentQueue = audioQueues_[index];
            int[] currentPacks = packQueues_[index];
            bool[] currentTags = agariTagQueues_[index];
            bool[] currentDelays = rankDelayQueues_[index];
            AudioClip[] newQueue = new AudioClip[currentQueue.Length + 1];
            int[] newPacks = new int[currentQueue.Length + 1];
            bool[] newTags = new bool[currentQueue.Length + 1];
            bool[] newDelays = new bool[currentQueue.Length + 1];

            for (int i = 0; i < currentQueue.Length; i++)
            {
                newQueue[i] = currentQueue[i];
                newPacks[i] = i < currentPacks.Length ? currentPacks[i] : 0;
                newTags[i] = i < currentTags.Length && currentTags[i];
                newDelays[i] = i < currentDelays.Length && currentDelays[i];
            }
            // この和了ぶんの最初のあがりクリップを積む時点で、暫定的に伏せておく。
            // ダブロン・トリロンの2人目以降は前に宣言ボイスが積まれていないため、
            // これが無いと「あがり画面より先に役読み」がそのまま出る。
            if (agariTag)
            {
                bool alreadyQueued = false;
                for (int i = 0; i < currentTags.Length; ++i)
                {
                    if (currentTags[i]) { alreadyQueued = true; break; }
                }

                // 既に解禁時刻が入っている（＝同期が先に着いた）ときは触らない。
                if (!alreadyQueued && agariReadoutHoldUntil_[index] <= Time.time)
                {
                    agariReadoutHoldUntil_[index] = Time.time + AGARI_READOUT_SAFETY_HOLD;
                }
            }

            newQueue[currentQueue.Length] = clip;
            newPacks[currentQueue.Length] = pack;
            newTags[currentQueue.Length] = agariTag;
            newDelays[currentQueue.Length] = rankDelay;
            audioQueues_[index] = newQueue;
            packQueues_[index] = newPacks;
            agariTagQueues_[index] = newTags;
            rankDelayQueues_[index] = newDelays;


            if (!isPlaying_[index])
            {
                PlayNextInQueue(index);
            }
        }

        private void PlayNextInQueue(int index)
        {
            // 未配線席の無音消化は while で回す（Udon にコールスタックが無く自己再帰禁止）。
            // 正常配線席は再生／ディレイ待ちで必ず return し、ループは1周のみ。
            while (true)
            {
                if (audioQueues_[index].Length == 0)
                {
                    isPlaying_[index] = false;
                    return;
                }

                // あがり読み上げの保留：あがり画面が出るまで役読みを始めない
                // （宣言ボイス → 手牌公開 → 牌エフェクト → あがり画面＋役読み）。
                // isPlaying_ を立てたまま時刻だけ先送りするので、格読みディレイと同じく
                // Update() のポーリングが再入して続きから鳴る（予約は使わない）。
                bool[] holdTags = agariTagQueues_[index];
                if (holdTags.Length > 0 && holdTags[0] && Time.time < agariReadoutHoldUntil_[index])
                {
                    isPlaying_[index] = true;

                    // 解禁時刻をそのまま次回時刻にしてはいけない。暫定の伏せを入れた直後に
                    // 本来の時刻へ前倒しされるので、固定すると前倒しに追従できず、
                    // 画面が出たあとに役読みが始まる（ダブロンの2人目以降で顕著）。
                    // 解禁までは小刻みに見直す。
                    float recheck = Time.time + AGARI_READOUT_RECHECK_SECONDS;
                    nextAdvanceTime_[index] =
                        recheck < agariReadoutHoldUntil_[index] ? recheck : agariReadoutHoldUntil_[index];
                    return;
                }

                // 格読みクリップの直前に一拍置く（点数表示→間→称号バナー＋格読み）。
                // フラグを消費して待ち時刻を設定し、次のUpdateポーリングで実際に鳴らす。
                // ClearQueueでキューごと消えるため予約残存の問題は起きない。
                bool[] delays = rankDelayQueues_[index];
                float rankPre = rankVoicePreDelaySeconds > 0.0f ? rankVoicePreDelaySeconds : 0.0f;
                float rankLead = rankVoiceLeadSeconds > 0.0f ? rankVoiceLeadSeconds : 0.0f;
                if (delays.Length > 0 && delays[0] && rankPre + rankLead > 0.001f)
                {
                    delays[0] = false;
                    isPlaying_[index] = true;
                    // バナー（＋SFX）はここから rankPre 後、声はさらに rankLead 後。
                    rankBannerCueTime_[index] = Time.time + rankPre;
                    nextAdvanceTime_[index] = Time.time + rankPre + rankLead;
                    return;
                }

                if (voiceSources != null && voiceSources.Length > index && voiceSources[index] != null)
                {
                    var audioSource = voiceSources[index];
                    var clip = audioQueues_[index][0];
                    int[] packs = packQueues_[index];
                    int pack = packs != null && packs.Length > 0 ? packs[0] : 0;

                    // あがり読み上げクリップの開始＝表示ステップを1進める
                    // （あがり画面パネルがポーリングして役行をフェードインさせる）。
                    bool[] tags = agariTagQueues_[index];
                    if (tags.Length > 0 && tags[0])
                    {
                        ++agariStepsStarted_[index];
                    }

                    audioSource.Stop();
                    audioSource.clip = clip;
                    // 各自の音量設定を掛ける（未割当＝1.0倍で従来どおり）。
                    float userVoiceAmp = volumePanel != null ? volumePanel.voiceAmp : 1.0f;
                    audioSource.volume = GetVoicePackVolume(pack) * GetVoiceClipGain(clip) * userVoiceAmp;
                    // 席つきで渡す＝立直BGMは「立直した本人の宣言ボイス」だけ曲を止めない。
                    if (worldBgm != null) { worldBgm.SuspendForVoiceBy(index); }
                    audioSource.Play();
                    isPlaying_[index] = true;

                    RemoveFromQueue(index);

                    // 次クリップの開始タイミング＝クリップ長＋調整値（負値で間を詰める）。
                    // 下限クランプで暴走（0秒連打）を防止。
                    var delay = clip.length + voiceGapAdjust;
                    if (delay < 0.05f) { delay = 0.05f; }

                    // 予約コールバックではなく Update() ポーリングで進める（局跨ぎの予約残存対策）。
                    nextAdvanceTime_[index] = Time.time + delay;
                    return;
                }

                // P1B-21: 未配線席。RemoveFromQueue で先頭を1つ消費してから続行（必ず有限回で空）。
                // 正常配線席はこの枝に入らない。
                LogVoiceSourceNullOnce(index);

                bool[] nullTags = agariTagQueues_[index];
                if (nullTags.Length > 0 && nullTags[0])
                {
                    ++agariStepsStarted_[index];
                }

                RemoveFromQueue(index);
                // continue → 先頭へ（キュー長は上で必ず減っている）
            }
        }

        private void Update()
        {
            // 発声キューの進行。isPlaying_ が false の席は進めない＝ClearQueue/ClearAllQueues で
            // キューを空にして isPlaying_=false にすれば、旧予約に相当するものは一切残らない。
            for (int i = 0; i < 4; i++)
            {
                if (isPlaying_[i] && Time.time >= nextAdvanceTime_[i])
                {
                    PlayNextInQueue(i);
                }
            }

            // 鳴いた直後の中など、声が重なって予約したチュンビームの声を鳴らす。
            AdvanceChunBeamPending();

            // 和了 VFX の非表示（ダブロンで延長された hideAt を1本のポーリングで扱う）。
            if (winVfxHideAt_ >= 0.0f && Time.time >= winVfxHideAt_)
            {
                winVfxHideAt_ = -1.0f;
                if (winVfxObject != null) { winVfxObject.SetActive(false); }
            }
        }

        private void RemoveFromQueue(int index)
        {
            if (audioQueues_[index].Length <= 1)
            {
                audioQueues_[index] = new AudioClip[0];
                packQueues_[index] = new int[0];
                agariTagQueues_[index] = new bool[0];
                rankDelayQueues_[index] = new bool[0];
                return;
            }

            AudioClip[] currentQueue = audioQueues_[index];
            int[] currentPacks = packQueues_[index];
            bool[] currentTags = agariTagQueues_[index];
            bool[] currentDelays = rankDelayQueues_[index];
            AudioClip[] newQueue = new AudioClip[currentQueue.Length - 1];
            int[] newPacks = new int[currentQueue.Length - 1];
            bool[] newTags = new bool[currentQueue.Length - 1];
            bool[] newDelays = new bool[currentQueue.Length - 1];

            for (int i = 1; i < currentQueue.Length; i++)
            {
                newQueue[i - 1] = currentQueue[i];
                newPacks[i - 1] = i < currentPacks.Length ? currentPacks[i] : 0;
                newTags[i - 1] = i < currentTags.Length && currentTags[i];
                newDelays[i - 1] = i < currentDelays.Length && currentDelays[i];
            }
            audioQueues_[index] = newQueue;
            packQueues_[index] = newPacks;
            agariTagQueues_[index] = newTags;
            rankDelayQueues_[index] = newDelays;
        }


        // （旧 OnAudioComplete0..3 は SendCustomEventDelayedSeconds 予約用だったが、
        //   Update() ポーリング化により不要になったため削除。）

        public void ClearQueue(int index)
        {
            Initialize();
            audioQueues_[index] = new AudioClip[0];
            packQueues_[index] = new int[0];
            agariTagQueues_[index] = new bool[0];
            rankDelayQueues_[index] = new bool[0];
            agariStepsStarted_[index] = 0;
            declareVoiceEndTime_[index] = 0.0f;
            agariReadoutHoldUntil_[index] = 0.0f;
            rankBannerCueTime_[index] = 0.0f;
            isPlaying_[index] = false;
            chunPendingClip_[index] = null;

            if (voiceSources != null && voiceSources.Length > index && voiceSources[index] != null)
            {
                voiceSources[index].Stop();
            }
        }

        public void ClearAllQueues()
        {
            Initialize();
            for (int i = 0; i < 4; i++)
            {
                audioQueues_[i] = new AudioClip[0];
                packQueues_[i] = new int[0];
                agariTagQueues_[i] = new bool[0];
                rankDelayQueues_[i] = new bool[0];
                agariStepsStarted_[i] = 0;
                declareVoiceEndTime_[i] = 0.0f;
                agariReadoutHoldUntil_[i] = 0.0f;
                rankBannerCueTime_[i] = 0.0f;
                isPlaying_[i] = false;
                chunPendingClip_[i] = null;
                if (voiceSources != null && voiceSources.Length > i && voiceSources[i] != null)
                {
                    voiceSources[i].Stop();
                }
            }
        }

        // あがり画面パネル用：この席で「鳴り始めた」あがり読み上げクリップの数。
        // ロン/ツモ宣言（読み上げの前に必ず入る）と局跨ぎClearで0に戻る。
        public int GetAgariStepCount(int seat)
        {
            if (seat < 0 || seat >= 4) { return 0; }
            return agariStepsStarted_[seat];
        }

        // あがり画面パネル用：この席の称号バナーを出す時刻（Time.time基準・0＝予定なし）。
        // 格読みの前の間に入った時点で確定する。格読みボイスはこの時刻から
        // rankVoiceLeadSeconds 後に鳴るため、パネルはこれを見てバナー＋SFXを先に出す。
        public float GetAgariRankCueTime(int seat)
        {
            if (seat < 0 || seat >= 4) { return 0.0f; }
            return rankBannerCueTime_[seat];
        }

        // 和了段取り用：この席のロン/ツモ宣言ボイスが鳴り終わる時刻（Time.time基準）。
        // 0＝不明（未再生・クリップ無し・局跨ぎClear後）。呼び側は固定秒へフォールバックする。
        public float GetDeclareVoiceEndTime(int seat)
        {
            if (seat < 0 || seat >= 4) { return 0.0f; }
            return declareVoiceEndTime_[seat];
        }

        // 和了段取り用：この席の発声キューが動いているか（再生中／保留待ち／次クリップ待ち）。
        // 最後のクリップが鳴り終わってキューが空になった時点で false になるので、
        // 卓のNEXTボタンを「読み上げが終わってから」出す条件に使える。
        public bool IsVoiceBusy(int seat)
        {
            if (seat < 0 || seat >= 4) { return false; }
            return isPlaying_[seat];
        }

        // 和了段取り用：この席のあがり読み上げを指定時刻まで待たせる（Time.time基準）。
        // 0以下で保留解除。各クライアントが自分のあがり画面の表示時刻を渡す＝完全ローカル。
        public void HoldAgariReadout(int seat, float untilTime)
        {
            if (seat < 0 || seat >= 4) { return; }
            Initialize();
            agariReadoutHoldUntil_[seat] = untilTime;
        }

        // 宣言ボイスを積んだ直後に呼ぶ。宣言はあがり読み上げの直前＝キューが空の状態で
        // 積まれるため、この時点の nextAdvanceTime_ がそのまま宣言クリップの終了時刻になる。
        // 鳴らなかった（未配線・クリップ無し）ときは現在時刻＝待たない。
        private void NoteDeclareVoice(int index)
        {
            if (index < 0 || index >= 4) { return; }
            declareVoiceEndTime_[index] = isPlaying_[index] ? nextAdvanceTime_[index] : Time.time;
        }

        // 局跨ぎで全クライアントの発声キューを止める。ClearAllQueues をローカルだけで呼ぶと
        // Owner のキューしか消えず、非Owner では旧クリップが鳴り続ける/次局と重なる。
        // これを全員へ配信する（Play*Local と同じ SendCustomNetworkEvent(All) 方式）。
        public void ClearAllQueuesGlobal()
        {
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(ClearAllQueues));
        }

        // ============================================================
        // SFX（発声とは別ソース。発声を打ち切らない）
        // ============================================================

        private AudioSource SelectSfxSource(AudioClip clip)
        {
            if (clip == haipai || clip == reachStick || clip == nakiArrange)
                return tableOperationSource != null ? tableOperationSource : sfxSource;
            if (clip == myTurn) { return systemSource != null ? systemSource : sfxSource; }
            return sfxSource;
        }

        // ---- 観戦モニターへの中継（効果音の記録）----
        // 効果音は PlayOneShot で鳴らすので、外から AudioSource を見ても何が鳴ったか分からない。
        // 鳴らした一撃をここに控え、観戦モニターが読んで自分の位置で鳴らし直す（ローカル・同期ゼロ）。
        // 音量は「一撃の倍率 × その AudioSource の volume」＝音量パネルのスライダーも乗った値。
        private const int SPEC_SFX_RING = 16;
        private AudioClip[] specSfxClip_ = new AudioClip[SPEC_SFX_RING];
        private float[] specSfxVol_ = new float[SPEC_SFX_RING];
        private bool[] specSfxDiscard_ = new bool[SPEC_SFX_RING];
        private int specSfxSerial_ = 0;

        private void RecordSpectatorSfx(AudioSource src, AudioClip clip, float volume, bool discard)
        {
            if (src == null || clip == null) { return; }
            int i = specSfxSerial_ % SPEC_SFX_RING;
            specSfxClip_[i] = clip;
            specSfxVol_[i] = volume * src.volume;
            specSfxDiscard_[i] = discard;
            // 読む側は差分だけ見る。上限で 0 へ戻ったら読む側が数え直す
            specSfxSerial_ = specSfxSerial_ >= 2000000000 ? 0 : specSfxSerial_ + 1;
        }

        // 観戦モニター用の読み口。serial は Spectate_SfxSerial() 未満・直近 SPEC_SFX_RING 件だけ有効
        public int Spectate_SfxSerial() { return specSfxSerial_; }
        public int Spectate_SfxRing() { return SPEC_SFX_RING; }
        public AudioClip Spectate_SfxClip(int serial) { return specSfxClip_[serial % SPEC_SFX_RING]; }
        public float Spectate_SfxVolume(int serial) { return specSfxVol_[serial % SPEC_SFX_RING]; }
        public bool Spectate_SfxIsDiscard(int serial) { return specSfxDiscard_[serial % SPEC_SFX_RING]; }

        private void PlaySfx(AudioClip clip)
        {
            if (clip == null) { return; }
            Initialize();
            var source = SelectSfxSource(clip);
            if (source == null) { return; }
            source.PlayOneShot(clip);
            RecordSpectatorSfx(source, clip, 1f, false);
        }

        private void PlaySfxAt(AudioClip clip, float volume)
        {
            if (clip == null) { return; }
            Initialize();
            var source = SelectSfxSource(clip);
            if (source == null) { return; }
            source.PlayOneShot(clip, volume);
            RecordSpectatorSfx(source, clip, volume, false);
        }

        // 打牌音（専用ソースから鳴らす）。音量パネルの「打牌音」スライダーが効く。
        //  dora ＝ ドラ牌を切った（Owner が判定して席番号の bit2 に載せてくる）。
        //  専用素材が無ければ通常の打牌音へ落とす＝未設定でも従来どおり鳴る。
        private void PlayDiscardSfx(bool dora)
        {
            bool useDora = dora && discardDora != null;
            var clip = useDora ? discardDora : discard;
            if (clip == null) { return; }
            Initialize();
            var src = discardSource != null ? discardSource : sfxSource;
            if (src == null) { return; }
            src.PlayOneShot(clip, useDora ? discardDoraSfxVolume : discardSfxVolume);
            RecordSpectatorSfx(src, clip, useDora ? discardDoraSfxVolume : discardSfxVolume, true);
        }

        // 自ターンチャイム（ローカル専用：Mahjong.Deserializeが自席判定して呼ぶ）
public void OnMyTurn(int index)
        {
            PlaySfxAt(myTurn, myTurnSfxVolume);
        }

        // 打牌音（Owner側から全員へ）
        public void OnDiscard(int index)
        {
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(OnDiscardLocal), index);
        }

[NetworkCallable]
        public void OnDiscardLocal(int index)
        {
            // index は席番号(0..3)。bit2(+4) が立っていたらドラ牌の打牌。
            PlayDiscardSfx(index >= 4);
        }

        // 打牌音（ローカル専用）。鳴きの晒し牌スライドのように、同期済みの状態から
        // 各クライアントが同時に自前で鳴らす演出で使う。ネットワークイベントを消費しない。
public void PlayDiscardLocal()
        {
            PlayDiscardSfx(false);
        }

        // 鳴いた牌を卓へ持ってきて並べる音（ローカル専用）。晒し牌が着地した瞬間に
        // 各クライアントが自前で鳴らす。素材未設定なら従来どおり打牌音へ落とす。
        public void PlayNakiArrangeLocal()
        {
            if (nakiArrange == null) { PlayDiscardSfx(false); return; }
            Initialize();
            var src = tableOperationSource != null ? tableOperationSource : sfxSource;
            if (src == null) { return; }
            src.PlayOneShot(nakiArrange, nakiArrangeVolume);
            RecordSpectatorSfx(src, nakiArrange, nakiArrangeVolume, false);
        }

        // 立直棒が卓に出た瞬間の音（ローカル専用）。
        public void PlayReachStickLocal() { PlaySfxAt(reachStick, reachStickVolume); }

        // 持ち時間の警告音（ローカル専用）。自席の残り時間表示が出ているときだけ鳴る。
        //
        // この2本は「残り時間を刻む音」で尺がそのまま3秒・5秒ある。共用の sfxSource で
        // 鳴らすと、牌を切って手番が終わっても最後まで鳴り続ける（共用ソースを Stop すると
        // 同時に鳴っている役読み・アナウンスまで切れる）。専用ソースから鳴らし、
        // 手番が終わったら StopThinkWarnLocal() で止める。
        public void PlayThinkWarnLocal() { PlayWarn(thinkWarn, thinkWarnVolume); }

        public void PlayThinkWarnLongLocal() { PlayWarn(thinkWarnLong, thinkWarnLongVolume); }

        // 手番が終わった（牌を切った・判断が終わった）ときに呼ぶ。専用ソースなので
        // 他の演出音・発声を巻き込まずに止められる。
        public void StopThinkWarnLocal()
        {
            if (warnSource == null) { return; }
            warnSource.Stop();
        }

        // 専用ソースが無いシーン（Normalize Audio 未実行）でも鳴ることを優先する。
        // ただしその経路では従来どおり途中で止められない。
        private void PlayWarn(AudioClip clip, float volume)
        {
            if (clip == null) { return; }
            Initialize();
            if (warnSource == null) { PlaySfxAt(clip, volume); return; }
            warnSource.Stop();
            warnSource.PlayOneShot(clip, volume);
        }

public void PlayHaipaiLocal()
        {
            PlaySfxAt(haipai, haipaiSfxVolume);
        }


        // あがり画面SFX（すべてローカル専用）。あがり画面の進行は各クライアントが
        // 同じ順序でローカル駆動しているため、ネットワークイベントは不要。
        public void PlayAgariYakuRowSfxLocal() { PlaySfxAt(agariYakuRowSfx, agariYakuRowSfxVolume); }

        public void PlayAgariScoreSfxLocal() { PlaySfxAt(agariScoreSfx, agariScoreSfxVolume); }

        public void PlayAgariRankSfxLocal() { PlaySfxAt(agariRankSfx, agariRankSfxVolume); }


        // システムアナウンス共通：聞く側のローカルパックの声でSFXソースから鳴らす
        private void PlayAnnounce(AudioClip[] clips)
        {
            // アナウンスはボイス素材だが SfxSource から鳴る＝効果音カテゴリ。
            // 効果音の目標に合わせた倍率を引く（表に無ければ従来どおり等倍）。
            var clip = PickPackClip(clips, ResolveLocalPack());
            if (clip == null) { return; }
            Initialize();
            var source = announceSource != null ? announceSource : sfxSource;
            if (source == null) { return; }
            int index = FindVoiceClipIndex(clip);
            float gain = index >= 0 && voiceAnnounceScales != null && index < voiceAnnounceScales.Length ? voiceAnnounceScales[index] : 1f;
            if (worldBgm != null) { worldBgm.SuspendForVoice(); }
            source.PlayOneShot(clip, gain);
            RecordSpectatorSfx(source, clip, gain, false);
        }

        // 対局開始（半荘の開始時のみ・アナウンス）
        public void PlayTaikyokuKaishi()
        {
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayTaikyokuKaishiLocal));
        }

        [NetworkCallable]
        public void PlayTaikyokuKaishiLocal()
        {
            PlayAnnounce(taikyokuKaishi);
        }

        // 四風連打
        public void PlaySuufonrenda()
        {
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlaySuufonrendaLocal));
        }

        [NetworkCallable]
        public void PlaySuufonrendaLocal()
        {
            PlayAnnounce(suufonrenda);
        }

        // 四槓散了
        public void PlaySuukansanra()
        {
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlaySuukansanraLocal));
        }

        [NetworkCallable]
        public void PlaySuukansanraLocal()
        {
            PlayAnnounce(suukansanra);
        }

        // 四家立直
        public void PlaySuuchariichi()
        {
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlaySuuchariichiLocal));
        }

        [NetworkCallable]
        public void PlaySuuchariichiLocal()
        {
            PlayAnnounce(suuchariichi);
        }

        // ============================================================
        // BGM（ローカル駆動：Mahjong.Deserialize から状態変化時に呼ばれる・冪等）
        //   mode: 2=自卓で立直中（かつ自分がこの卓にentry済み）→立直BGMを要求
        //         それ以外=要求解除（ワールド共通の通常BGMに戻る）
        //   riichiSeat=立直者の席（そのプレイヤーのパックで立直BGMを選択）
        // ============================================================
        public int ResolveRiichiBgm(int seat)
        {
            if (mahjong_ == null || seat < 0 || seat >= 4) { return 0; }
            var player = VRCPlayerApi.GetPlayerById(mahjong_.GetSeatPlayerId(seat));
            int track;
            if (!Utilities.IsValid(player) || !PlayerData.TryGetInt(player, "kmj_riichi_bgm", out track)) { return 0; }
            return Mathf.Clamp(track, 0, MahjongRiichiBgm.MAX_TRACK);
        }

        public bool IsVoiceBusy()
        {
            if (announceSource != null && announceSource.isPlaying) { return true; }
            for (int i = 0; i < 4; ++i)
            {
                if (isPlaying_[i]) { return true; }
                if (voiceSources != null && i < voiceSources.Length && voiceSources[i] != null && voiceSources[i].isPlaying) { return true; }
            }
            return false;
        }

        // 立直BGM用：指定席「以外」の声が鳴っているか。
        // 立直の宣言ボイスと立直BGMを重ねて鳴らすため（立直した本人の声だけ無視する）。
        // 他家の鳴き声やアナウンスは従来どおり曲を止めるので、重なるのは自分の宣言だけ。
        public bool IsVoiceBusyExcept(int seat)
        {
            if (announceSource != null && announceSource.isPlaying) { return true; }
            for (int i = 0; i < 4; ++i)
            {
                if (i == seat) { continue; }
                if (isPlaying_[i]) { return true; }
                if (voiceSources != null && i < voiceSources.Length && voiceSources[i] != null && voiceSources[i].isPlaying) { return true; }
            }
            return false;
        }

        // 立直BGM用：この席の声が実際に鳴っているか（キュー＋AudioSource の両方）。
        // IsVoiceBusy(int) はキューだけを見る＝NEXTボタンの解禁用なので、
        // 「宣言ボイスが鳴り終わったか」の判定にはこちらを使う。
        public bool IsSeatVoiceBusy(int seat)
        {
            if (seat < 0 || seat >= 4) { return false; }
            if (isPlaying_[seat]) { return true; }
            return voiceSources != null && seat < voiceSources.Length && voiceSources[seat] != null && voiceSources[seat].isPlaying;
        }

        public void UpdateBgm(int mode, int riichiSeat, int track)
        {
            if (worldBgm == null) { return; }
            if (mode == 2) { worldBgm.ClaimRiichi(this, riichiSeat, track); }
            else { worldBgm.ReleaseRiichi(this); }
        }

        // 和了ジングル＋VFX（Owner側から全員へ。seat=和了者）
        public void PlayAgariJingle(int seat)
        {
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayAgariJingleLocal), seat);
        }

        [NetworkCallable]
        public void PlayAgariJingleLocal(int seat)
        {
            PlaySfxAt(PickPackClip(agariJingle, ResolveSeatPack(seat)), agariJingleVolume);

            if (winVfxObject != null)
            {
                winVfxObject.SetActive(true);
                // ダブロン／トリロン：都度 hideAt を延長。Delayed 複数予約は使わない（P1B-23）。
                winVfxHideAt_ = Time.time + winVfxDuration;
            }
        }

        public void HideWinVfx()
        {
            winVfxHideAt_ = -1.0f;
            if (winVfxObject != null)
            {
                winVfxObject.SetActive(false);
            }
        }

        // ============================================================
        // 点数格の読み上げ
        //   numYakumanStack: 役満の重ね数（0=役満なし, 1=役満, 2=ダブル…6以上=六倍扱い）
        //   雀魂準拠：役満役があるときは格のみ、無く13翻以上なら数え役満。
        //   満貫未満は格を読まない（役とドラのみ）。
        // ============================================================
        public void PlayWinSound(int numYaku, int numFu, int numYakumanStack, int index)
        {
            if (numYakumanStack >= 1)
            {
                switch (numYakumanStack)
                {
                    case 1: PlayYakuman(index); break;
                    case 2: PlayDoubleYakuman(index); break;
                    case 3: PlayTripleYakuman(index); break;
                    case 4: Play4TimesYakuman(index); break;
                    case 5: Play5TimesYakuman(index); break;
                    default: Play6TimesYakuman(index); break;
                }
                return;
            }

            if (numYaku >= 13)
            {
                PlayKazoeYakuman(index);
                return;
            }

            if (numYaku >= 11)
            {
                PlaySanbaiman(index);
                return;
            }

            if (numYaku >= 8)
            {
                PlayBaiman(index);
                return;
            }

            if (numYaku >= 6)
            {
                PlayHaneman(index);
                return;
            }

            if (numYaku == 5 || (numYaku == 4 && numFu >= 40) || (numYaku == 3 && numFu >= 70))
            {
                PlayMangan(index);
                return;
            }

            // 満貫未満：格読みなし（符・翻の読み上げは廃止）
        }

        // ============================================================
        // 青天井モードの点数格。
        //   ・満貫〜三倍満は通常と同じ翻数の区切りで読む。
        //   ・4倍満以上（＝13翻以上）は重ね数で出し分けず、共通で「役満」1本。
        //     青天井では役満役も翻数として点数へ溶けるため、二倍役満・数え役満と
        //     いった格の区別自体が成立しない。
        // ============================================================
        public void PlayWinSoundAotenjou(int numYaku, int numFu, int index)
        {
            if (numYaku >= 13)
            {
                PlayYakuman(index);
                return;
            }

            if (numYaku >= 11)
            {
                PlaySanbaiman(index);
                return;
            }

            if (numYaku >= 8)
            {
                PlayBaiman(index);
                return;
            }

            if (numYaku >= 6)
            {
                PlayHaneman(index);
                return;
            }

            if (numYaku == 5 || (numYaku == 4 && numFu >= 40) || (numYaku == 3 && numFu >= 70))
            {
                PlayMangan(index);
                return;
            }

            // 満貫未満：格読みなし（通常モードと同じ）
        }

        // ============================================================
        // ドラ枚数読み上げ（type: DORA_TYPE_*, count: 枚数, index: 席）
        // 該当枚数のクリップが未設定ならこの種類は読まない。
        // ============================================================
        public void PlayDoraCount(int doraType, int count, int index)
        {
            if (count <= 0) { return; }
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayDoraCountLocal), doraType, count, index);
        }

        [NetworkCallable]
        public void PlayDoraCountLocal(int doraType, int count, int index)
        {
            PlayAgariVoice(GetDoraClips(doraType, count), index);
        }

        // 表/抜き/裏/赤 いずれも同じ「ドラN」クリップを使う（doraTypeは将来の分離用に残置）。
        private AudioClip[] GetDoraClips(int doraType, int count)
        {
            if (count <= 0) { return null; }

            switch (count)
            {
                case 1: return dora1;
                case 2: return dora2;
                case 3: return dora3;
                case 4: return dora4;
                case 5: return dora5;
                case 6: return dora6;
                case 7: return dora7;
                case 8: return dora8;
                case 9: return dora9;
                case 10: return dora10;
                case 11: return dora11;
                case 12: return dora12;
                default: return dora13Plus;
            }
        }

        // ============================================================
        // 発声・読み上げAPI（Owner側から呼ぶ → 全員へ配信）
        // ============================================================

        // --- 試合中 ---
        public void PlayRiichi(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayRiichiLocal), index); }
        [NetworkCallable] public void PlayRiichiLocal(int index) { PlayVoiceInterrupt(riichi, index); }

        public void PlayDoubleRiichi(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayDoubleRiichiLocal), index); }
        [NetworkCallable] public void PlayDoubleRiichiLocal(int index) { PlayVoiceInterrupt(doubleRiichi, index); }

        public void PlayChi(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayChiLocal), index); }
        [NetworkCallable] public void PlayChiLocal(int index) { PlayVoiceInterrupt(chi, index); }

        public void PlayPon(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayPonLocal), index); }
        [NetworkCallable] public void PlayPonLocal(int index) { PlayVoiceInterrupt(pon, index); }

        public void PlayKan(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayKanLocal), index); }
        [NetworkCallable] public void PlayKanLocal(int index) { PlayVoiceInterrupt(kan, index); }

        public void PlayNukidora(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayNukidoraLocal), index); }
        [NetworkCallable] public void PlayNukidoraLocal(int index) { PlayVoiceInterrupt(nukidora, index); }

        // ロン/ツモ宣言はあがり読み上げの直前に必ず入るため、ここで表示ステップを0に戻す
        // （新規同期なしで全クライアントのステップ基準が揃う）。
        public void PlayRon(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayRonLocal), index); }
        [NetworkCallable] public void PlayRonLocal(int index) { if (worldBgm != null) { worldBgm.EndRiichi(this); } Initialize(); if (index >= 0 && index < 4) { agariStepsStarted_[index] = 0; } PlayVoice(ron, index); NoteDeclareVoice(index); }

        public void PlayTsumo(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayTsumoLocal), index); }
        [NetworkCallable] public void PlayTsumoLocal(int index) { if (worldBgm != null) { worldBgm.EndRiichi(this); } Initialize(); if (index >= 0 && index < 4) { agariStepsStarted_[index] = 0; } PlayVoice(tsumo, index); NoteDeclareVoice(index); }

        // --- 局開始前 ---
        // エントリー直後は seatPlayerId の代入・同期がまだ済んでいないため、
        // 押した本人（＝送信側）が自分のパックを解決し、パックIDごと全員へ配信する。
        public void PlayYoroshiku(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayYoroshikuLocal), index, ResolveLocalPack()); }
        // 固定卓OwnerがENTRYを確定する経路。要求者ではなく同期済みseatPlayerIdからパックを解決する。
        public void PlayYoroshikuForSeat(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayYoroshikuLocal), index, ResolveSeatPack(index)); }
        // CPU席のエントリーは常にデフォルトボイス
        public void PlayYoroshikuCpu(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayYoroshikuLocal), index, 0); }
        [NetworkCallable] public void PlayYoroshikuLocal(int index, int pack) { PlayVoiceWithPack(yoroshiku, index, pack); }

        // --- 役読み上げ：状況役 ---
        public void PlayChankan(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayChankanLocal), index); }
        [NetworkCallable] public void PlayChankanLocal(int index) { PlayAgariVoice(chankan, index); }

        public void PlayRinshankaihou(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayRinshankaihouLocal), index); }
        [NetworkCallable] public void PlayRinshankaihouLocal(int index) { PlayAgariVoice(rinshankaihou, index); }

        public void PlayHaiteiRaoyue(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayHaiteiRaoyueLocal), index); }
        [NetworkCallable] public void PlayHaiteiRaoyueLocal(int index) { PlayAgariVoice(haiteiRaoyue, index); }

        public void PlayHouteiRaoyui(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayHouteiRaoyuiLocal), index); }
        [NetworkCallable] public void PlayHouteiRaoyuiLocal(int index) { PlayAgariVoice(houteiRaoyui, index); }

        // --- 役読み上げ：役牌（個別） ---
        public void PlayTon(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayTonLocal), index); }
        [NetworkCallable] public void PlayTonLocal(int index) { PlayAgariVoice(ton, index); }

        public void PlayNan(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayNanLocal), index); }
        [NetworkCallable] public void PlayNanLocal(int index) { PlayAgariVoice(nan, index); }

        public void PlaySha(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayShaLocal), index); }
        [NetworkCallable] public void PlayShaLocal(int index) { PlayAgariVoice(sha, index); }

        public void PlayPey(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayPeyLocal), index); }
        [NetworkCallable] public void PlayPeyLocal(int index) { PlayAgariVoice(pey, index); }

        public void PlayChun(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayChunLocal), index); }
        [NetworkCallable] public void PlayChunLocal(int index) { PlayAgariVoice(chun, index); }

        public void PlayHaku(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayHakuLocal), index); }
        [NetworkCallable] public void PlayHakuLocal(int index) { PlayAgariVoice(haku, index); }

        public void PlayHatsu(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayHatsuLocal), index); }
        [NetworkCallable] public void PlayHatsuLocal(int index) { PlayAgariVoice(hatsu, index); }

        public void PlayDoubleTon(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayDoubleTonLocal), index); }
        [NetworkCallable] public void PlayDoubleTonLocal(int index) { PlayAgariVoice(doubleTon, index); }

        public void PlayDoubleNan(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayDoubleNanLocal), index); }
        [NetworkCallable] public void PlayDoubleNanLocal(int index) { PlayAgariVoice(doubleNan, index); }

        public void PlayDoubleSha(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayDoubleShaLocal), index); }
        [NetworkCallable] public void PlayDoubleShaLocal(int index) { PlayAgariVoice(doubleSha, index); }

        public void PlayDoublePey(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayDoublePeyLocal), index); }
        [NetworkCallable] public void PlayDoublePeyLocal(int index) { PlayAgariVoice(doublePey, index); }

        // --- 役読み上げ：通常役 ---
        public void PlayTanyao(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayTanyaoLocal), index); }
        [NetworkCallable] public void PlayTanyaoLocal(int index) { PlayAgariVoice(tanyao, index); }

        public void PlayIipeeikou(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayIipeeikouLocal), index); }
        [NetworkCallable] public void PlayIipeeikouLocal(int index) { PlayAgariVoice(iipeeikou, index); }

        public void PlayPinfu(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayPinfuLocal), index); }
        [NetworkCallable] public void PlayPinfuLocal(int index) { PlayAgariVoice(pinfu, index); }

        public void PlayChanta(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayChantaLocal), index); }
        [NetworkCallable] public void PlayChantaLocal(int index) { PlayAgariVoice(chanta, index); }

        public void PlayIkkitsuukan(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayIkkitsuukanLocal), index); }
        [NetworkCallable] public void PlayIkkitsuukanLocal(int index) { PlayAgariVoice(ikkitsuukan, index); }

        public void PlaySanshokudoujun(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlaySanshokudoujunLocal), index); }
        [NetworkCallable] public void PlaySanshokudoujunLocal(int index) { PlayAgariVoice(sanshokudoujun, index); }

        public void PlaySanshokudoukou(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlaySanshokudoukouLocal), index); }
        [NetworkCallable] public void PlaySanshokudoukouLocal(int index) { PlayAgariVoice(sanshokudoukou, index); }

        public void PlaySankantsu(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlaySankantsuLocal), index); }
        [NetworkCallable] public void PlaySankantsuLocal(int index) { PlayAgariVoice(sankantsu, index); }

        public void PlayToitoi(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayToitoiLocal), index); }
        [NetworkCallable] public void PlayToitoiLocal(int index) { PlayAgariVoice(toitoi, index); }

        public void PlaySanankou(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlaySanankouLocal), index); }
        [NetworkCallable] public void PlaySanankouLocal(int index) { PlayAgariVoice(sanankou, index); }

        public void PlayShousangen(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayShousangenLocal), index); }
        [NetworkCallable] public void PlayShousangenLocal(int index) { PlayAgariVoice(shousangen, index); }

        public void PlayHonroutou(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayHonroutouLocal), index); }
        [NetworkCallable] public void PlayHonroutouLocal(int index) { PlayAgariVoice(honroutou, index); }

        public void PlayChitoitsu(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayChitoitsuLocal), index); }
        [NetworkCallable] public void PlayChitoitsuLocal(int index) { PlayAgariVoice(chitoitsu, index); }

        public void PlayJunchan(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayJunchanLocal), index); }
        [NetworkCallable] public void PlayJunchanLocal(int index) { PlayAgariVoice(junchan, index); }

        public void PlayHonitsu(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayHonitsuLocal), index); }
        [NetworkCallable] public void PlayHonitsuLocal(int index) { PlayAgariVoice(honitsu, index); }

        public void PlayRyanpeikou(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayRyanpeikouLocal), index); }
        [NetworkCallable] public void PlayRyanpeikouLocal(int index) { PlayAgariVoice(ryanpeikou, index); }

        public void PlayChinitsu(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayChinitsuLocal), index); }
        [NetworkCallable] public void PlayChinitsuLocal(int index) { PlayAgariVoice(chinitsu, index); }

        public void PlayRiichiAgari(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayRiichiAgariLocal), index); }
        [NetworkCallable] public void PlayRiichiAgariLocal(int index) { PlayAgariVoice(riichiAgari, index); }

        public void PlayDoubleRiichiAgari(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayDoubleRiichiAgariLocal), index); }
        [NetworkCallable] public void PlayDoubleRiichiAgariLocal(int index) { PlayAgariVoice(doubleRiichiAgari, index); }

        public void PlayMenzenTsumo(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayMenzenTsumoLocal), index); }
        [NetworkCallable] public void PlayMenzenTsumoLocal(int index) { PlayAgariVoice(menzenTsumo, index); }

        public void PlayIppatsu(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayIppatsuLocal), index); }
        [NetworkCallable] public void PlayIppatsuLocal(int index) { PlayAgariVoice(ippatsu, index); }

        // --- 役読み上げ：役満・特殊 ---
        public void PlayTenhou(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayTenhouLocal), index); }
        [NetworkCallable] public void PlayTenhouLocal(int index) { PlayAgariVoice(tenhou, index); }

        public void PlayChiihou(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayChiihouLocal), index); }
        [NetworkCallable] public void PlayChiihouLocal(int index) { PlayAgariVoice(chiihou, index); }

        public void PlayDaisangen(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayDaisangenLocal), index); }
        [NetworkCallable] public void PlayDaisangenLocal(int index) { PlayAgariVoice(daisangen, index); }

        public void PlaySuuankou(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlaySuuankouLocal), index); }
        [NetworkCallable] public void PlaySuuankouLocal(int index) { PlayAgariVoice(suuankou, index); }

        public void PlaySuuankouTankimachi(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlaySuuankouTankimachiLocal), index); }
        [NetworkCallable] public void PlaySuuankouTankimachiLocal(int index) { PlayAgariVoice(suuankouTankimachi, index); }

        public void PlayTsuisou(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayTsuisouLocal), index); }
        [NetworkCallable] public void PlayTsuisouLocal(int index) { PlayAgariVoice(tsuisou, index); }

        public void PlayRyuuisou(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayRyuuisouLocal), index); }
        [NetworkCallable] public void PlayRyuuisouLocal(int index) { PlayAgariVoice(ryuuisou, index); }

        public void PlayChinroutou(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayChinroutouLocal), index); }
        [NetworkCallable] public void PlayChinroutouLocal(int index) { PlayAgariVoice(chinroutou, index); }

        public void PlayKokushimusou(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayKokushimusouLocal), index); }
        [NetworkCallable] public void PlayKokushimusouLocal(int index) { PlayAgariVoice(kokushimusou, index); }

        public void PlayKokushimusouJuusanmenmachi(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayKokushimusouJuusanmenmachiLocal), index); }
        [NetworkCallable] public void PlayKokushimusouJuusanmenmachiLocal(int index) { PlayAgariVoice(kokushimusouJuusanmenmachi, index); }

        public void PlayDaisushi(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayDaisushiLocal), index); }
        [NetworkCallable] public void PlayDaisushiLocal(int index) { PlayAgariVoice(daisushi, index); }

        public void PlayShousushi(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayShousushiLocal), index); }
        [NetworkCallable] public void PlayShousushiLocal(int index) { PlayAgariVoice(shousushi, index); }

        public void PlaySuukantsu(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlaySuukantsuLocal), index); }
        [NetworkCallable] public void PlaySuukantsuLocal(int index) { PlayAgariVoice(suukantsu, index); }

        public void PlayChuurenpoutou(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayChuurenpoutouLocal), index); }
        [NetworkCallable] public void PlayChuurenpoutouLocal(int index) { PlayAgariVoice(chuurenpoutou, index); }

        public void PlayJunseiChuurenpoutou(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayJunseiChuurenpoutouLocal), index); }
        [NetworkCallable] public void PlayJunseiChuurenpoutouLocal(int index) { PlayAgariVoice(junseiChuurenpoutou, index); }

        public void PlayNagashimangan(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayNagashimanganLocal), index); }
        // 流し満貫はロン/ツモ宣言を経由しないため、ここでステップを0に戻してから積む。
        [NetworkCallable] public void PlayNagashimanganLocal(int index) { Initialize(); if (index >= 0 && index < 4) { agariStepsStarted_[index] = 0; } PlayAgariVoice(nagashimangan, index); }

        // --- 点数格 ---
        public void PlayMangan(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayManganLocal), index); }
        [NetworkCallable] public void PlayManganLocal(int index) { PlayAgariRankVoice(mangan, index); }

        public void PlayHaneman(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayHanemanLocal), index); }
        [NetworkCallable] public void PlayHanemanLocal(int index) { PlayAgariRankVoice(haneman, index); }

        public void PlayBaiman(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayBaimanLocal), index); }
        [NetworkCallable] public void PlayBaimanLocal(int index) { PlayAgariRankVoice(baiman, index); }

        public void PlaySanbaiman(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlaySanbaimanLocal), index); }
        [NetworkCallable] public void PlaySanbaimanLocal(int index) { PlayAgariRankVoice(sanbaiman, index); }

        public void PlayYakuman(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayYakumanLocal), index); }
        [NetworkCallable] public void PlayYakumanLocal(int index) { PlayAgariRankVoice(yakuman, index); }

        public void PlayKazoeYakuman(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayKazoeYakumanLocal), index); }
        [NetworkCallable] public void PlayKazoeYakumanLocal(int index) { PlayAgariRankVoice(kazoeYakuman, index); }

        public void PlayDoubleYakuman(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayDoubleYakumanLocal), index); }
        [NetworkCallable] public void PlayDoubleYakumanLocal(int index) { PlayAgariRankVoice(doubleYakuman, index); }

        public void PlayTripleYakuman(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayTripleYakumanLocal), index); }
        [NetworkCallable] public void PlayTripleYakumanLocal(int index) { PlayAgariRankVoice(tripleYakuman, index); }

        public void Play4TimesYakuman(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(Play4TimesYakumanLocal), index); }
        [NetworkCallable] public void Play4TimesYakumanLocal(int index) { PlayAgariRankVoice(yakuman4Times, index); }

        public void Play5TimesYakuman(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(Play5TimesYakumanLocal), index); }
        [NetworkCallable] public void Play5TimesYakumanLocal(int index) { PlayAgariRankVoice(yakuman5Times, index); }

        public void Play6TimesYakuman(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(Play6TimesYakumanLocal), index); }
        [NetworkCallable] public void Play6TimesYakumanLocal(int index) { PlayAgariRankVoice(yakuman6Times, index); }

        // --- 流局系 ---
        public void PlayTenpai(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayTenpaiLocal), index); }
        [NetworkCallable] public void PlayTenpaiLocal(int index) { PlayVoice(tenpai, index); }

        public void PlayNoten(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayNotenLocal), index); }
        [NetworkCallable] public void PlayNotenLocal(int index) { PlayVoice(noten, index); }

        public void PlayKyuushukyuuhai(int index) { SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayKyuushukyuuhaiLocal), index); }
        [NetworkCallable] public void PlayKyuushukyuuhaiLocal(int index) { PlayVoice(kyuushukyuuhai, index); }
    }
}
