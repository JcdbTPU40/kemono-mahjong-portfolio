using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    // ボイスパック選択ボタン（Collider付きオブジェクトに付ける）
    //
    // ■ 使い方（キューブ1個＝キャラ1体）
    //   1. Cube等（Collider必須・IsTrigger不要）を置く。
    //   2. この .cs をキューブへ**ドラッグ**してアタッチ
    //      （Add Componentの名前検索はProgram Source=Noneになるため不可）。
    //   3. Inspector:
    //      - selector : ワールドに1個置いた MahjongVoiceSelector
    //      - packId   : 0=デフォルト（アナウンサー）, 1=Shine, 2以降=追加キャラ
    //      - UdonBehaviour の Interaction Text にキャラ名（例:「Shine」）を設定。
    //   4. プレイヤーがInteractすると自分のボイスパックが切り替わり（PlayerData保存）、
    //      selector側にpreview設定があればローカルで試聴が鳴る。
    //
    //   キャラ追加時はキューブを複製して packId を変えるだけ。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class MahjongVoicePackButton : UdonSharpBehaviour
    {
        public MahjongVoiceSelector selector;
        public int packId = 0;

        public override void Interact()
        {
            if (selector == null) { return; }
            selector.SelectPack(packId);
        }
    }
}
