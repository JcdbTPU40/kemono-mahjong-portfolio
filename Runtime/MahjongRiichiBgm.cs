using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    // 立直BGM（卓ごとに1個・卓の SoundPlayer 配下に置く）
    //
    // ■ 何をするか
    //   立直が宣言されている間だけ、その卓の近くにいるプレイヤーへ立直BGMを鳴らす。
    //   曲は「宣言した本人が選んだトラック」＝ PlayerData("kmj_riichi_bgm")。
    //   卓Owner が席とトラック番号を1バイトに詰めて同期し（Mahjong.latestRiichiBgm_）、
    //   各クライアントの Mahjong.UpdateBgmFromState() が Claim / Release を呼ぶ。
    //
    // ■ 宣言ボイスとは重ねて鳴らす
    //   曲は「立直！」の声と同時に立ち上げる（claimLeadSeconds = 0）。
    //   ボイスで曲を止めるダッキングは残すが、立直直後だけは
    //   「立直した席の声」を無視する（UpdateVoiceOverlap）。
    //   他家の鳴き声・アナウンスはこの間も従来どおり曲を止める。
    //
    // ■ ワールド常時BGMはここには無い
    //   以前は通常BGMをワールド全体で常時ループしていたが廃止した。
    //   ワールドのBGMは VizVid の動画プレイヤーが持つ。
    //
    // ■ 音量の権威はこのスクリプト1本
    //   AudioSource.volume は Update が毎フレーム
    //   「トラック別の基準ゲイン × 音量パネルの bgmAmp」で上書きする。
    //   Inspector の volume も VolumeSettingsPanel の ApplyToArray も効かないので、
    //   音量を変えるときは riichiBgmGains（ビルダーが焼く）を触ること。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class MahjongRiichiBgm : UdonSharpBehaviour
    {
        public AudioSource bgmSource;

        [Tooltip("トラック番号で引く立直BGM。[0] は「なし」＝必ず空にする。")]
        public AudioClip[] riichiBgm;

        [Tooltip("riichiBgm と同じ並びの基準音量（50%＝0dB のときの値）。実測LUFSからの逆算。\n曲を足したら必ずこちらも足すこと（Build Riichi Bgm が焼く）。")]
        public float[] riichiBgmGains;

        public TableProximityCuller culler;
        public MahjongSoundsV2 sounds;
        public VolumeSettingsPanel volumePanel;

        // 曲を足すときに触る箇所を1か所へ寄せるための上限。
        // 同期側（Mahjong.latestRiichiBgm_）は seat を下位3bit、track を上位5bitに置くので
        // track は 31 まで載る。ここを上げるときは PlayerStatsStore / MahjongSoundsV2 の
        // クランプと MainMenuController.COSMETIC_BGM_COUNT も揃えること。
        public const int MAX_TRACK = 31;

        // 範囲外になってから実際に止めるまでの猶予（秒）。詳細は Update のコメント。
        private const float OUT_OF_RANGE_GRACE = 0.15f;

        [Tooltip("立直が確定してから曲を出すまでの間（秒）。0＝宣言ボイスと同時に鳴り始める。")]
        public float claimLeadSeconds = 0.0f;

        // 宣言ボイスと重ねる区間の上限（秒）。本来は「本人の声が鳴り終わったら」抜けるが、
        // ボイスなしのパックや、ボイスのネットワークイベントが落ちたときに
        // 永久に重ねたまま（＝他家の声しか曲を止めない）にならないよう時間でも抜ける。
        // 立直の宣言ボイスは長くても数秒。
        private const float OVERLAP_MAX_SECONDS = 5.0f;

        // ボイス（立直・鳴き・ロン/ツモ・アナウンス）が途切れてから曲を戻すまでの間（秒）。
        // ボイスが鳴っている間は毎フレーム押し延ばされるので、実質「最後の声の N 秒後」になる。
        private const float VOICE_TAIL_SECONDS = 0.1f;

        private int currentSeat_ = -1;
        private int currentTrack_;
        private bool inRange_;
        private bool paused_;
        private bool ended_;
        private float waitUntil_;
        private float outSince_ = -1f;
        private bool voiceOverlap_;
        private bool overlapVoiceSeen_;
        private float overlapUntil_;

        public void ClaimRiichi(MahjongSoundsV2 owner, int seat, int track)
        {
            if (owner != sounds || bgmSource == null) { return; }
            track = Mathf.Clamp(track, 0, MAX_TRACK);
            if (currentSeat_ == seat && currentTrack_ == track) { return; }
            bgmSource.Stop();
            currentSeat_ = seat;
            currentTrack_ = track;
            ended_ = false;
            paused_ = false;
            waitUntil_ = Time.time + Mathf.Max(0f, claimLeadSeconds);
            // ここから宣言ボイスと重ねて鳴らす区間に入る（詳細は UpdateVoiceOverlap）。
            voiceOverlap_ = true;
            overlapVoiceSeen_ = false;
            overlapUntil_ = Time.time + OVERLAP_MAX_SECONDS;
            bgmSource.clip = track > 0 && riichiBgm != null && track < riichiBgm.Length ? riichiBgm[track] : null;
            // loop はシーン設定に頼らずここで立てる。false のまま鳴らすと、下の
            // 「止まっていたら Play する」に拾われて1周ごとに頭から鳴り直す
            // ＝無音にはならず「たまに切れる」という気づきにくい壊れ方をする。
            bgmSource.loop = true;
        }

        public void ReleaseRiichi(MahjongSoundsV2 owner)
        {
            if (owner != sounds) { return; }
            StopNow();
            currentSeat_ = -1;
            currentTrack_ = 0;
        }

        // 和了が通った瞬間に BGM を切る口（MahjongSoundsV2 の ron / tsumo から呼ぶ）。
        // Claim / Release と同じく呼び出し元が自分の卓の SoundsV2 かを確かめる。
        // いまは MahjongRiichiBgm が卓ごとに1個なので他卓から届くことは無いが、
        // 3つのうち1つだけ検証が無いと、読む側が「意図的な例外か事故か」を判断できない。
        public void EndRiichi(MahjongSoundsV2 owner)
        {
            if (owner != sounds) { return; }
            StopNow();
        }

        private void StopNow()
        {
            ended_ = true;
            paused_ = false;
            outSince_ = -1f;
            voiceOverlap_ = false;
            if (bgmSource != null) { bgmSource.Stop(); }
        }

        // 立直の宣言ボイスと曲を重ねる区間の管理。
        // ClaimRiichi の直後は「立直した席の声」を無視して鳴らす＝宣言と同時に曲が立ち上がる。
        // 本人の声が鳴り始めて（overlapVoiceSeen_）鳴り終わったら通常のダッキングへ戻す。
        // ボイスなし・ボイスのイベントが届かなかった場合に備え、OVERLAP_MAX_SECONDS で必ず抜ける。
        private void UpdateVoiceOverlap()
        {
            if (!voiceOverlap_) { return; }
            if (Time.time >= overlapUntil_) { voiceOverlap_ = false; return; }
            if (sounds != null && sounds.IsSeatVoiceBusy(currentSeat_)) { overlapVoiceSeen_ = true; return; }
            if (overlapVoiceSeen_) { voiceOverlap_ = false; }
        }

        // 発声の開始時に呼ばれる版（seat＝鳴らした席）。立直の宣言ボイスと曲を重ねている間は、
        // 立直した本人の声では止めない。これが無いと Update 側で無視しても、
        // クリップ開始の一撃で Pause されて曲の頭が 0.1 秒途切れる。
        public void SuspendForVoiceBy(int seat)
        {
            if (voiceOverlap_ && seat == currentSeat_) { return; }
            SuspendForVoice();
        }

        public void SuspendForVoice()
        {
            waitUntil_ = Time.time + VOICE_TAIL_SECONDS;
            if (bgmSource != null && bgmSource.isPlaying)
            {
                bgmSource.Pause();
                paused_ = true;
            }
        }

        // トラック別の基準音量。表に無ければ等倍にせず、鳴らしすぎを避けて控えめに寄せる。
        private float ResolveGain()
        {
            if (riichiBgmGains != null && currentTrack_ >= 0 && currentTrack_ < riichiBgmGains.Length)
            {
                return riichiBgmGains[currentTrack_];
            }
            return 0.2f;
        }

        // ---- 観戦モニター用の読み口 ----
        // 卓の曲は近い人にしか鳴らない（範囲外は Pause）ので、観戦モニターは「本来なら鳴っている曲」を
        // ここから読んで自分の位置で同じ曲を鳴らす。範囲の判定とは無関係に答える。
        public AudioClip Spectate_ActiveClip()
        {
            if (ended_ || currentTrack_ == 0 || bgmSource == null) { return null; }
            return bgmSource.clip;
        }

        public float Spectate_Volume()
        {
            return ResolveGain() * (volumePanel != null ? volumePanel.bgmAmp : 1f);
        }

        // 宣言ボイスの間は曲を下げる（卓と同じ規則。立直直後は立直した席の声とは重ねる）
        public bool Spectate_IsDucked()
        {
            if (sounds == null) { return false; }
            UpdateVoiceOverlap();
            return voiceOverlap_ ? sounds.IsVoiceBusyExcept(currentSeat_) : sounds.IsVoiceBusy();
        }

        private void OnDisable()
        {
            if (bgmSource != null) { bgmSource.Stop(); }
            paused_ = false;
            inRange_ = false;
            outSince_ = -1f;
        }

        private void Update()
        {
            if (bgmSource == null) { return; }

            // 立直が終わった／曲が無い＝完全に止める（頭出しし直してよい）。
            if (ended_ || currentTrack_ == 0 || bgmSource.clip == null)
            {
                if (bgmSource.isPlaying || paused_) { bgmSource.Stop(); }
                paused_ = false;
                outSince_ = -1f;
                return;
            }

            inRange_ = culler != null && culler.IsAudioInRange();
            if (!inRange_)
            {
                // 範囲外は Stop ではなく Pause。Stop にすると境界を出入りするたび
                // 曲が頭から鳴り直す（判定に余白が無くなったぶん、ここで受ける）。
                //
                // 距離の余白の代わりに「0.15秒続けて範囲外なら止める」という時間の余白を置く。
                // 立ち止まったときのトラッキング揺れ（数cm）で1フレームごとに
                // Pause/UnPause が交互に走るのを防ぐのが目的。
                // 距離ではなく時間なので、隣卓との重複は復活しない：
                // 卓間 4.59m・判定 1.8m なら、卓Aの縁から卓Bの縁まで 0.99m ＝ 走っても約0.25秒あり、
                // 0.15秒の猶予では次の卓が鳴り始める前に前の卓が止まる。
                if (outSince_ < 0f) { outSince_ = Time.time; }
                if (Time.time - outSince_ >= OUT_OF_RANGE_GRACE && bgmSource.isPlaying)
                {
                    bgmSource.Pause();
                    paused_ = true;
                }
                return;
            }
            outSince_ = -1f;

            // 宣言ボイスと重ねる区間かどうかを先に更新する。
            // 重ねている間は「立直した席の声」だけを無視＝他家の声やアナウンスは従来どおり曲を止める。
            UpdateVoiceOverlap();
            bool voiceBusy = sounds != null && (voiceOverlap_ ? sounds.IsVoiceBusyExcept(currentSeat_) : sounds.IsVoiceBusy());
            if (voiceBusy) { SuspendForVoice(); return; }
            if (Time.time < waitUntil_) { return; }
            bgmSource.volume = ResolveGain() * (volumePanel != null ? volumePanel.bgmAmp : 1f);
            if (paused_) { bgmSource.UnPause(); paused_ = false; }
            else if (!bgmSource.isPlaying) { bgmSource.Play(); }
        }
    }
}
