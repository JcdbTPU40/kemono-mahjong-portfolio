using UdonSharp;
using UnityEngine;

namespace SuzuFactory.AozoraMahjong
{
    /// <summary>
    /// ボタン1個ぶんの中継。onClick から Press() が呼ばれ、
    /// 実際のアニメーションは MenuButtonPressFx（Update 1本）へ渡す。
    ///
    /// このスクリプトは Update を持たない＝ボタンが何個増えても毎フレームの負荷は増えない。
    /// 配線（コンポーネント追加・onClick への追加・参照割り当て）は Wire Main Menu が全ボタンへ自動で行う。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class MenuButtonPress : UdonSharpBehaviour
    {
        [Tooltip("アニメーションの実行役。Wire Main Menu が割り当てる。")]
        public MenuButtonPressFx fx;

        [Tooltip("動かす対象。通常は自分の RectTransform。Wire Main Menu が割り当てる。")]
        public RectTransform target;

        public void Press()
        {
            if (fx == null || target == null) { return; }
            fx.Punch(target);
        }
    }
}
