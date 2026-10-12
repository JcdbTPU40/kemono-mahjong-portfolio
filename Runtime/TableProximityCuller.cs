using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

namespace SuzuFactory.AozoraMahjong
{
    /// <summary>
    /// 卓の近接カリング。ローカルプレイヤーが範囲に入ったら卓ルートを表示し、
    /// 範囲外なら非表示にして描画・CPU負荷を丸ごと削る。
    ///
    /// 【重要な前提】
    /// - このコンポーネントは「卓ルート(tableRoot)」とは別の、常時アクティブな
    ///   GameObject に付けること。自分自身を非アクティブにすると Enter / poll を
    ///   二度と受け取れず復帰不能になる。
    /// - tableRoot は Mahjong(Manual同期) を含む卓一式。SetActive で丸ごとトグルする。
    /// - ネットワーク同期は一切いじらない(このコンポーネントは BehaviourSyncMode.None)。
    ///   卓ルート再表示時、VRChat が最後の同期状態を再適用し OnDeserialization が
    ///   呼ばれることに依存する(=要テスト項目)。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class TableProximityCuller : UdonSharpBehaviour
    {
        [Tooltip("表示/非表示を切り替える卓のルート。Mahjong 等を含む一式。\nこのカリング用オブジェクト自身や、その親を指定しないこと。")]
        public GameObject tableRoot;

        [Tooltip("表示する距離(m)。このオブジェクトの位置を卓の中心に置き、トリガーColliderのサイズと概ね合わせる。")]
        public float showDistance = 1.8f;

        [Tooltip("非表示に戻すまでの追加マージン(m)。表示<->非表示のバタつき(チャタリング)防止のヒステリシス。")]
        public float hideMargin = 3.0f;

        [Tooltip("保険の距離チェック間隔(秒)。トリガーのExit発火漏れ・スポーン時のEnter未発火を補正する。")]
        public float pollInterval = 1.0f;

        [Tooltip("ワールド入場時に非表示から始める(遠い卓の初期ロード負荷を下げる)。近ければ最初のポーリングで自動表示される。")]
        public bool startHidden = true;

        // --- 内部状態 ---
        private VRCPlayerApi localPlayer_;
        private bool isShown_ = true;
        private bool inTrigger_ = false;
        private bool ready_ = false;
        // 「自分がOwnerになった使用中の卓」を起こすために使う。参照は Start で自動解決するので
        // Inspector の配線は増えない（Mahjong 側の matchmaking を借りる）。
        private Mahjong table_;
        private MatchmakingManager matchmaking_;
        // 観戦モニター（matchmaking 側の配線を借りる）。この端末が見ている配信卓は距離外でも起こす。
        private SpectatorMonitor spectator_;
        // 役満ポスター（卓の配線を借りる）。ポスターの Owner が寝ている卓の申告を確かめ直す数秒だけ起こす。
        private YakumanPosterManager poster_;

        void Start()
        {
            localPlayer_ = Networking.LocalPlayer;

            // 自己参照ガード: tableRoot を誤って自分自身/親に設定すると復帰不能になる。
            if (tableRoot == null)
            {
                Debug.LogError("[TableProximityCuller] tableRoot が未設定です。カリングを無効化します。");
                return;
            }
            if (tableRoot == this.gameObject)
            {
                Debug.LogError("[TableProximityCuller] tableRoot にこのオブジェクト自身が設定されています。別の卓ルートを指定してください。無効化します。");
                return;
            }

            ready_ = true;

            // 使用中判定に使う参照を1度だけ解決する。GetComponent は非アクティブな
            // GameObject でも引ける。matchmaking は Mahjong 側の配線をそのまま借りるので、
            // このコンポーネントに新しい Inspector 項目は増やさない。
            table_ = tableRoot.GetComponent<Mahjong>();
            if (table_ != null) { matchmaking_ = table_.matchmaking; }

            // 初期状態
            if (startHidden)
            {
                isShown_ = false;
                tableRoot.SetActive(false);
            }
            else
            {
                isShown_ = tableRoot.activeSelf;
            }

            // 距離ポーリング開始(トリガーの補正 & 初期判定)。
            SendCustomEventDelayedSeconds(nameof(_PollDistance), pollInterval);
        }

        public override void OnPlayerTriggerEnter(VRCPlayerApi player)
        {
            if (!ready_) return;
            if (player == null || !player.isLocal) return;

            inTrigger_ = true;
            SetShown(true); // 入場は即時反映
        }

        public override void OnPlayerTriggerExit(VRCPlayerApi player)
        {
            if (!ready_) return;
            if (player == null || !player.isLocal) return;

            // Exit は VRChat で誤発火/発火漏れがあるため即消ししない。
            // フラグだけ下ろし、実際の非表示は距離ポーリングでヒステリシス付きで確定する。
            inTrigger_ = false;
        }

        /// <summary>
        /// 低頻度の距離チェック。Exit発火漏れ・スポーン時のEnter未発火を補正し、
        /// Owner保護とヒステリシスをかけて最終的な表示状態を決める。
        /// </summary>
        public void _PollDistance()
        {
            if (!ready_) return;

            // 観戦モニターは Start では引かない。Udon の初期化順は不定で、Manager がまだ初期化前だと
            // null が読めてしまう（ClientSim で 8台とも空だった）。解決するまでポーリングのたびに引き直す。
            if (spectator_ == null && matchmaking_ != null) { spectator_ = matchmaking_.spectatorMonitor; }
            if (poster_ == null && table_ != null) { poster_ = table_.yakumanPoster; }

            if (localPlayer_ == null || !Utilities.IsValid(localPlayer_))
            {
                localPlayer_ = Networking.LocalPlayer;
            }

            if (localPlayer_ != null && Utilities.IsValid(localPlayer_))
            {
                float d = Vector3.Distance(localPlayer_.GetPosition(), transform.position);

                // Exit の発火漏れ補正：トリガー（≒showDistance）からヒステリシス分も離れているのに
                // 「中にいる」ままなら、Exit を取りこぼしたとみなして下ろす。下ろさないと遠方でも
                // 卓が起きたまま・IsAudioInRange も真のまま残る。Owner/割当/観戦の保持は下の判定が別に見る。
                if (inTrigger_ && d > showDistance + hideMargin) { inTrigger_ = false; }

                if (!isShown_)
                {
                    // 非表示 -> 表示:
                    //   ・近い／トリガー内
                    //   ・使用中の卓のOwner（離脱後のOwner移譲でも同期権威を起こす）
                    //   ・マッチ成立でこの卓へ割り当てられた本人（3秒の間に卓をロードする）
                    bool ownerWake = table_ != null && matchmaking_ != null
                                     && matchmaking_.IsTableInUse(table_)
                                     && Networking.IsOwner(localPlayer_, tableRoot);
                    bool assignedWake = table_ != null && matchmaking_ != null
                                        && matchmaking_.IsLocalPlayerAssignedToTable(table_);

                    bool spectateWake = spectator_ != null && table_ != null
                                        && spectator_.IsKeepingTableAwake(table_);
                    // 役満ポスターの確かめ直し（人間が少なく立会人の居ない卓でも、自分で同期を読めるように）。
                    bool posterWake = poster_ != null && table_ != null && poster_.IsKeepingTableAwake(table_);

                    if (inTrigger_ || d <= showDistance || ownerWake || assignedWake || spectateWake || posterWake)
                    {
                        SetShown(true);
                    }
                }
                else
                {
                    // 使用中の卓で同期Ownerなら距離外でも維持する。空卓のOwnerは寝かせてよい。
                    bool ownerHold = table_ != null && matchmaking_ != null
                                     && matchmaking_.IsTableInUse(table_)
                                     && Networking.IsOwner(localPlayer_, tableRoot);
                    // 割当メンバーは表示側の assignedWake と対にしないと、卓へ歩いている間
                    // 「起こす → 遠いので寝かす」を pollInterval ごとに繰り返す（1Hzのチャタリング）。
                    // 卓一式の SetActive がその都度走り、LocalMatchDuties の activeInHierarchy も
                    // 半分の時間で落ちる。起こす条件と維持する条件は必ず揃える。
                    bool assignedHold = table_ != null && matchmaking_ != null
                                        && matchmaking_.IsLocalPlayerAssignedToTable(table_);
                    // 観戦の配信卓も起こす条件と保つ条件をそろえる（上の assignedHold と同じ理由）。
                    bool spectateHold = spectator_ != null && table_ != null
                                        && spectator_.IsKeepingTableAwake(table_);
                    bool posterHold = poster_ != null && table_ != null && poster_.IsKeepingTableAwake(table_);
                    if (!inTrigger_ && !ownerHold && !assignedHold && !spectateHold && !posterHold
                        && d > showDistance + hideMargin)
                    {
                        SetShown(false);
                    }
                }
            }

            SendCustomEventDelayedSeconds(nameof(_PollDistance), pollInterval);
        }

        /// <summary>
        /// 立直BGMを鳴らしてよい範囲か。表示のカリングとは別判定で、
        /// <b>ヒステリシス（hideMargin）を使わない純粋な内外判定</b>にしてある。
        ///
        /// 【なぜ hideMargin を使わないか】
        /// 表示は「バタつくと卓一式の SetActive が走る」ので離脱に余白が要るが、
        /// 音に同じ余白を使うと離脱が showDistance+hideMargin = 4.8m まで伸びる。
        /// 卓間距離は最小 4.59m しかないため、隣の卓へ歩くと前の卓のBGMが鳴ったまま
        /// 次の卓が鳴り始め、2卓ぶんが 2D フルボリュームで重なる。
        /// ソファのラウンジ（動画プレイヤーの前）も卓1/2/4/5 の 4.8m 圏に入るので、
        /// 卓から移動してきた人の耳で立直BGMが動画に被さったまま止まらなくなる。
        ///
        /// showDistance(1.8m) だけなら、ラウンジの最短距離 3.14m に対しても余裕があり、
        /// 卓間 4.59m に対しても 2 卓が同時に鳴らないことが幾何学的に保証される。
        /// この保証は「卓どうしを showDistance の2倍より近づけない」ことが前提。
        /// Validate Riichi BGM が卓間距離を検査する。
        /// </summary>
        public bool IsAudioInRange()
        {
            var player = Networking.LocalPlayer;
            if (!Utilities.IsValid(player)) { return false; }
            float distance = Vector3.Distance(player.GetPosition(), transform.position);
            return inTrigger_ || distance <= showDistance;
        }

        private void SetShown(bool shown)
        {
            if (tableRoot == null) return;
            if (isShown_ == shown && tableRoot.activeSelf == shown) return;

            isShown_ = shown;
            tableRoot.SetActive(shown);
        }
    }
}
