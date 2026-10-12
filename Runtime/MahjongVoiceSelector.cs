using UdonSharp;
using UnityEngine;
using VRC.SDK3.Persistence;
using VRC.SDKBase;
using VRC.Udon;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    // ボイスパック（キャラCV）選択コンポーネント
    //
    // ■ 役割
    //   プレイヤーが「どのキャラのボイスを使うか」を選ぶUI用。
    //   - 選択は PlayerData に保存（クロスインスタンス永続・全クライアントが読める）。
    //   - kmj_voice_pack（VOICE_PACK_KEY）の**唯一の書き手**。
    //     MainMenu のキャラ連動も SetPack 経由（保留スロットは本クラスの1つだけ）。
    //   - 実際の発声は MahjongSoundsV2 が発声時に 席→playerId→PlayerData を
    //     遅延解決して鳴らすため、同期変数・NetworkEventは一切不要。
    //   - ワールドに1個置けば全卓に効く（卓ごとに置く必要なし）。
    //
    // ■ 使い方
    //   1. ロビー等の World Space Canvas 配下にボタンを並べる。
    //   2. 各ボタン（MahjongPushButton等のInteract）から SelectPack0..SelectPack9 を呼ぶ。
    //      packId は MahjongSoundsV2 の AudioClip[] のインデックスと一致させる。
    //      （0=デフォルト/アナウンサー, 1..=追加キャラ。例: 1=元気な少女, 2=元気な少年, 3=冷淡, 4=母性）
    //   3. previewSource と previewClips[packId] を設定すると、選択時に
    //      ローカルだけで試聴が鳴る（他人には聞こえない）。
    //   4. MainMenuController.voiceSelector に本コンポーネントを割り当て、
    //      キャラ連動は SetPack（試聴なし）を呼ぶ。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class MahjongVoiceSelector : UdonSharpBehaviour
    {
        [Header("試聴（任意）：選択時にローカル再生")]
        public AudioSource previewSource;
        public AudioClip[] previewClips; // index = packId

        // 自分の PlayerData が復元済みかどうか。復元前の書き込みは消える恐れがあるためガード。
        private bool restored_ = false;

        // VOICE_PACK_KEY 用の唯一の保留スロット。OnPlayerRestored で flush する。
        private bool pendingPackSet_ = false;
        private int pendingPackId_ = 0;

        public override void OnPlayerRestored(VRCPlayerApi player)
        {
            if (player == null || !player.isLocal) { return; }

            restored_ = true;
            if (pendingPackSet_)
            {
                pendingPackSet_ = false;
                PlayerData.SetInt(MahjongSoundsV2.VOICE_PACK_KEY, pendingPackId_);
            }
        }

        // --- UIボタン（Interact）から呼ぶエントリ ---
        public void SelectPack0() { SelectPack(0); }
        public void SelectPack1() { SelectPack(1); }
        public void SelectPack2() { SelectPack(2); }
        public void SelectPack3() { SelectPack(3); }
        public void SelectPack4() { SelectPack(4); }
        public void SelectPack5() { SelectPack(5); }
        public void SelectPack6() { SelectPack(6); }
        public void SelectPack7() { SelectPack(7); }
        public void SelectPack8() { SelectPack(8); }
        public void SelectPack9() { SelectPack(9); }

        // UI からの選択（試聴あり）。書き込み本体は SetPack。
        public void SelectPack(int packId)
        {
            if (packId < 0) { packId = 0; }

            SetPack(packId);

            // 試聴（ローカルのみ）
            if (previewSource != null && previewClips != null &&
                packId < previewClips.Length && previewClips[packId] != null)
            {
                previewSource.Stop();
                previewSource.clip = previewClips[packId];
                previewSource.Play();
            }
        }

        // PlayerData / 復元前保留のみ（試聴なし）。MainMenu のキャラ連動から呼ぶ。
        // 復元済みなら即 SetInt、未復元なら pending を上書き（最後の呼び出しが勝つ）。
        public void SetPack(int packId)
        {
            if (packId < 0) { packId = 0; }

            if (!restored_)
            {
                pendingPackId_ = packId;
                pendingPackSet_ = true;
                return;
            }

            PlayerData.SetInt(MahjongSoundsV2.VOICE_PACK_KEY, packId);
        }

        // 現在の自分の選択（UIハイライト用など）。未設定は0。保留中は保留値。
        public int GetMyPack()
        {
            if (pendingPackSet_)
            {
                return pendingPackId_ < 0 ? 0 : pendingPackId_;
            }

            int v = 0;
            var lp = Networking.LocalPlayer;
            if (lp != null && PlayerData.TryGetInt(lp, MahjongSoundsV2.VOICE_PACK_KEY, out v))
            {
                return v < 0 ? 0 : v;
            }
            return 0;
        }
    }
}
