using UdonSharp;
using UnityEngine;
using VRC.SDK3.Persistence;
using VRC.SDKBase;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    // 自画像の共有ストア（PlayerData の byte[] 経由・同期変数ゼロ）
    //
    // ■ 役割
    //   撮影ブース（SelfiePortraitCamera）が焼いた写真を PlayerData へ入れ、
    //   全クライアントがそれを Texture2D へ復元してキャッシュする。
    //   「あるプレイヤーの自画像テクスチャをくれ」の唯一の窓口。
    //
    // ■ なぜ PlayerData なのか（同期変数チャンク送りではなく）
    //   ・PlayerData は **他人の分も読める**（GetBytes(player, key)）＝他プレイヤーに見せられる
    //   ・**インスタンスを跨いで永続する**＝次に入場しても写真が残る
    //   ・**後から来た人にも自動で届く**（OnPlayerRestored / OnPlayerDataUpdated）
    //   ・Udon の同期帯域（約11KB/s）を1バイトも使わない＝対局の同期を邪魔しない
    //   同期変数で byte[] をチャンク送りする方式に比べ、チャンク管理・Owner移譲・
    //   後乗り再送のどれも要らない。
    //
    // ■ 容量（ここが唯一の制約。**実測で決めること**）
    //   PlayerData は 1ワールドあたり100KB/人。
    //   公式ドキュメントには「圧縮して格納する／圧縮が効けば300KB相当まで入る」とあるが、
    //   **実測ではその圧縮は効いていない。むしろ生より膨らむ。**
    //     2026-09-03 の計測：生 153,600 バイトを入れたら使用量 210,651 / 上限 102,400。
    //     153,600 x 4/3 = 204,800 に他キー約 5,851 を足すと 210,651 でぴったり一致するので、
    //     byte[] は base64 相当（4/3倍）で格納されていると見るのが妥当。
    //   つまり **生サイズの上限はおおよそ (102,400 - 他キー) x 3/4 ≒ 72,000 バイト**。
    //
    //   この値はワールドが持つ他のPlayerDataキーの量で動くし、実機とClientSimでも
    //   違う可能性がある。そこで固定値を当てるのをやめ、**入らなければ一段小さいサイズで
    //   撮り直すラダー方式**にした（SelfiePortraitCamera.shareRts が大きい順に並ぶ）。
    //   SaveLocalPhoto は入りきらなかったら false を返し、呼び出し側が次の段へ落ちる。
    //
    //   参考：Pocca の同期版は 200x163 が上限だが、あちらは同期変数を4096バイトずつ
    //   2秒間隔で送るので1枚に約64秒かかる。こちらは1回の書き込みで済む。
    //
    // ■ 保存形式
    //   KEY_DATA … RGBA32 の生バイト列そのもの（Texture2D.LoadRawTextureData に直接食わせる）
    //   KEY_FMT  … int。(version<<16) | (width<<8) | height
    //   ヘッダをバイト列に混ぜないのは、混ぜると復元時に配列コピーのループが要るため
    //   （Udon の per-pixel ループは Quest で高い）。分けておけばループはゼロ。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class SelfiePortraitStore : UdonSharpBehaviour
    {
        public const string KEY_DATA = "kmj_selfie";     // byte[] RGBA32 生データ
        public const string KEY_FMT = "kmj_selfie_fmt";  // int (version<<16)|(w<<8)|h

        public const int FORMAT_VERSION = 1;

        // 共有サイズは固定値を持たない。WireMainMenu が作る RenderTexture のラダー
        // （SelfiePortraitCamera.shareRts、大きい順）が正で、幅・高さは KEY_FMT に入る。
        // **どの段も 255 以下であること**（KEY_FMT に1バイトずつ詰めているため）。

        // 使用量がこの割合を超えたら Console に警告を出す（実機で詰めるための目安）。
        private const float USAGE_WARN_RATIO = 0.8f;

        // 戦績・プロフィール分析（kmj_profile3_* / kmj_highlight3_* / kmj_highlightev3_* /
        // kmj_recent*）は
        // 対局のたびに太る。撮影時に上限ぎりぎりまで使うと、後の対局で戦績の保存が落ちる。
        // 100戦×3カテゴリの最悪値（見積り約11KB）に余裕を足した分を先に差し引く。
        // ラダー最上段 96x144（生 55,296 → 格納約 73,728）＋この予約でも 100KB に収まる。
        private const int RESERVED_FOR_STATS_BYTES = 16384;

        [Header("キャッシュ")]
        [Tooltip("同時にテクスチャを保持する人数。8卓×4席＝32席ぶん。")]
        public int cacheCapacity = 32;

        [Header("デバッグ")]
        [Tooltip("復元の成否をConsoleへ出す。実機で写真が出ないときに使う。")]
        public bool verboseLog = false;

        [Header("ローカル高解像度")]
        [Tooltip("自分の写真だけは、撮影したセッションのあいだ 撮影用RT（既定 512x768）の実物を返す。"
                 + "PlayerData を通さない＝容量の制約を受けない。OFF にすると自分も共有版（96x144 など）になる。")]
        public bool useLocalHighRes = true;

        // --- キャッシュ（純ローカル） ---
        private int[] cachePlayerIds_;
        private Texture2D[] cacheTextures_;
        private int[] cacheUsedAt_;    // 単純なLRU用の通し番号
        private bool[] cacheHasPhoto_; // 「読みに行ったが写真が無い」も覚える（毎フレーム引き直さない）
        private int cacheClock_ = 0;
        private bool initialized_ = false;

        // 自分だけが見る高解像度版（撮影ブースが「決定」時に渡す photoRt の実物）。
        // RenderTexture なので同期にも PlayerData にも乗らない＝撮ったセッション限りで消える。
        private Texture localHighRes_ = null;
        private int localHighResPlayerId_ = 0;

        void Start()
        {
            Initialize();
        }

        private void Initialize()
        {
            if (initialized_) { return; }
            initialized_ = true;

            if (cacheCapacity < 4) { cacheCapacity = 4; }
            cachePlayerIds_ = new int[cacheCapacity];
            cacheTextures_ = new Texture2D[cacheCapacity];
            cacheUsedAt_ = new int[cacheCapacity];
            cacheHasPhoto_ = new bool[cacheCapacity];
            for (int i = 0; i < cacheCapacity; ++i) { cachePlayerIds_[i] = 0; }
        }

        // ============================================================
        // 書き込み（撮影ブースから呼ぶ。自分の分しか書けない）
        // ============================================================

        // 撮った写真の生バイト列を PlayerData へ保存する。
        // bytes は RGBA32 で width*height*4 バイトちょうどであること。
        //
        // 戻り値 false ＝ **容量に入りきらなかった**。呼び出し側（撮影ブース）は
        // 一段小さいサイズで撮り直して再度ここへ来ること。
        // 引数が壊れている場合も false を返すが、そちらは Console にエラーを出す。
        public bool SaveLocalPhoto(byte[] bytes, int width, int height)
        {
            Initialize();

            if (bytes == null || width <= 0 || height <= 0 || width > 255 || height > 255
                || bytes.Length != width * height * 4)
            {
                Debug.LogError("[SelfiePortraitStore] 保存の引数が不正です: " + width + "x" + height
                               + " / bytes=" + (bytes == null ? -1 : bytes.Length));
                return false;
            }

            PlayerData.SetBytes(KEY_DATA, bytes);
            PlayerData.SetInt(KEY_FMT, (FORMAT_VERSION << 16) | (width << 8) | height);

            // 自分のキャッシュは即座に捨てる（次の要求で新しい写真から作り直す）。
            var local = Networking.LocalPlayer;
            if (local != null) { Invalidate(local.playerId); }

            int usage = GetUsageBytes();
            int limit = GetLimitBytes();

            // 入りきらなかった場合、VRChat は保存せずエラーを出す。
            // ここで気付いて一段小さいサイズへ落とす（呼び出し側がやる）。
            // 判定は上限そのものではなく、戦績の伸びしろを引いた実効枠で行う。
            int budget = limit > 0 ? limit - RESERVED_FOR_STATS_BYTES : 0;
            if (budget > 0 && usage > budget)
            {
                Debug.LogWarning("[SelfiePortraitStore] " + width + "x" + height
                                 + "（生 " + bytes.Length + " bytes）は PlayerData に入りませんでした（"
                                 + usage + " / " + budget + " bytes＝上限 " + limit
                                 + " − 戦績予約 " + RESERVED_FOR_STATS_BYTES + "）。一段小さいサイズで撮り直します。");
                return false;
            }


            Debug.Log("[SelfiePortraitStore] 保存: " + width + "x" + height
                      + " / 生 " + bytes.Length + " bytes"
                      + " / PlayerData実使用量 " + usage + " / " + limit + " bytes");

            if (limit > 0 && usage > (int)(limit * USAGE_WARN_RATIO))
            {
                Debug.LogWarning("[SelfiePortraitStore] PlayerData の使用量が上限の"
                                 + Mathf.RoundToInt(USAGE_WARN_RATIO * 100f) + "%を超えました（"
                                 + usage + " / " + limit + " bytes）。"
                                 + "ラダーの先頭サイズを下げることを検討してください。");
            }
            return true;
        }

        // 自分の写真を消す（キャラを他へ変えても写真は残す仕様なので、
        // いまは使っていない。将来「写真を削除」を付けるときの口）。
        public void ClearLocalPhoto()
        {
            PlayerData.SetInt(KEY_FMT, 0);
            localHighRes_ = null;
            localHighResPlayerId_ = 0;
            var local = Networking.LocalPlayer;
            if (local != null) { Invalidate(local.playerId); }
        }

        // 撮影ブースが「決定」のときに渡してくる、自分だけが見る高解像度版（photoRt の実物）。
        // 共有版（PlayerData）は容量の都合で 96x144 まで落ちるが、それは**他人へ配るための絵**。
        // 自分の画面に出す絵は同期もPlayerDataも通らないので、落とす理由がない。
        // 保存の成否に関わらず預ける（容量で共有に失敗しても自分の画面には出せる）。
        public void SetLocalHighResPortrait(Texture tex)
        {
            Initialize();

            var local = Networking.LocalPlayer;
            if (local == null || tex == null)
            {
                localHighRes_ = null;
                localHighResPlayerId_ = 0;
                return;
            }

            localHighRes_ = tex;
            localHighResPlayerId_ = local.playerId;

            // 低解像度版がキャッシュに残っていたら捨てる（次の要求で高解像度に切り替わる）。
            Invalidate(localHighResPlayerId_);
        }

        // 自分の PlayerData 使用量[バイト]。上限は GetLimitBytes。
        public int GetUsageBytes()
        {
            var local = Networking.LocalPlayer;
            if (local == null) { return 0; }
            return Networking.GetPlayerDataStorageUsage(local);
        }

        public int GetLimitBytes()
        {
            return Networking.GetPlayerDataStorageLimit();
        }

        // ============================================================
        // 読み出し（全クライアントが誰の分でも読める）
        // ============================================================

        // そのプレイヤーが自画像を保存しているか。
        public bool HasPortrait(VRCPlayerApi player)
        {
            return GetPortraitOf(player) != null;
        }

        // そのプレイヤーの自画像テクスチャ。無ければ null。
        // 復元は初回だけで、以降はキャッシュを返す。
        public Texture GetPortraitOf(VRCPlayerApi player)
        {
            Initialize();

            if (player == null || !player.IsValid()) { return null; }

            int id = player.playerId;

            // 自分の分は、撮ったセッションのあいだ高解像度の実物を返す。
            // （PlayerData 経由の共有版は他プレイヤーへ配るためのもので、自分は通す必要がない）
            if (useLocalHighRes && localHighRes_ != null && id == localHighResPlayerId_)
            {
                return localHighRes_;
            }

            int slot = FindSlot(id);
            if (slot >= 0)
            {
                cacheUsedAt_[slot] = ++cacheClock_;
                return cacheHasPhoto_[slot] ? cacheTextures_[slot] : null;
            }

            var tex = Decode(player);
            Store(id, tex);
            return tex;
        }

        // PlayerData から Texture2D を復元する。ループはゼロ
        // （LoadRawTextureData に生バイト列をそのまま食わせる）。
        private Texture2D Decode(VRCPlayerApi player)
        {
            if (!PlayerData.HasKey(player, KEY_FMT)) { return null; }
            if (!PlayerData.HasKey(player, KEY_DATA)) { return null; }

            int fmt = PlayerData.GetInt(player, KEY_FMT);
            if (fmt <= 0) { return null; }

            int version = (fmt >> 16) & 0xFF;
            int width = (fmt >> 8) & 0xFF;
            int height = fmt & 0xFF;
            if (version != FORMAT_VERSION || width <= 0 || height <= 0) { return null; }

            byte[] bytes = PlayerData.GetBytes(player, KEY_DATA);
            if (bytes == null || bytes.Length != width * height * 4)
            {
                if (verboseLog)
                {
                    Debug.LogWarning("[SelfiePortraitStore] サイズ不一致: fmt=" + width + "x" + height
                                     + " bytes=" + (bytes == null ? -1 : bytes.Length));
                }
                return null;
            }

            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.LoadRawTextureData(bytes);
            tex.Apply(false, false);

            if (verboseLog)
            {
                Debug.Log("[SelfiePortraitStore] 復元: " + player.displayName + " " + width + "x" + height);
            }
            return tex;
        }

        // ============================================================
        // キャッシュ管理
        // ============================================================

        private int FindSlot(int playerId)
        {
            for (int i = 0; i < cacheCapacity; ++i)
            {
                if (cachePlayerIds_[i] == playerId) { return i; }
            }
            return -1;
        }

        private void Store(int playerId, Texture2D tex)
        {
            int slot = -1;

            // 空きスロット優先
            for (int i = 0; i < cacheCapacity; ++i)
            {
                if (cachePlayerIds_[i] == 0) { slot = i; break; }
            }

            // 無ければ最も長く使われていないものを追い出す
            if (slot < 0)
            {
                int oldest = cacheUsedAt_[0];
                slot = 0;
                for (int i = 1; i < cacheCapacity; ++i)
                {
                    if (cacheUsedAt_[i] < oldest) { oldest = cacheUsedAt_[i]; slot = i; }
                }
                FreeSlot(slot);
            }

            cachePlayerIds_[slot] = playerId;
            cacheTextures_[slot] = tex;
            cacheHasPhoto_[slot] = (tex != null);
            cacheUsedAt_[slot] = ++cacheClock_;
        }

        private void FreeSlot(int slot)
        {
            if (cacheTextures_[slot] != null)
            {
                // 実行時に作った Texture2D はここで捨てないと積み上がる。
                Object.Destroy(cacheTextures_[slot]);
            }
            cacheTextures_[slot] = null;
            cachePlayerIds_[slot] = 0;
            cacheHasPhoto_[slot] = false;
        }

        // そのプレイヤーのキャッシュを捨てる（次の要求で作り直す）。
        public void Invalidate(int playerId)
        {
            Initialize();
            int slot = FindSlot(playerId);
            if (slot >= 0) { FreeSlot(slot); }
        }

        // ============================================================
        // VRChat からの通知
        // ============================================================

        // 誰かの PlayerData が更新された＝写真が差し替わった可能性がある。
        public override void OnPlayerDataUpdated(VRCPlayerApi player, PlayerData.Info[] infos)
        {
            if (player == null || infos == null) { return; }

            for (int i = 0; i < infos.Length; ++i)
            {
                if (infos[i].Key == KEY_DATA || infos[i].Key == KEY_FMT)
                {
                    Invalidate(player.playerId);
                    return;
                }
            }
        }

        // 退室したら解放する（残しておくと VRAM を握ったままになる）。
        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            if (player == null) { return; }
            Invalidate(player.playerId);
        }

        // 100KB を超えた＝保存できていない。
        // 通常は SaveLocalPhoto の戻り値で気付いて一段小さいサイズへ落ちるので、
        // ここまで来るのはラダーの最小段でも入らなかったとき（＝他キーが太りすぎ）。
        public override void OnPlayerDataStorageExceeded(VRCPlayerApi player)
        {
            Debug.LogError("[SelfiePortraitStore] PlayerData の容量上限を超えました（" + GetUsageBytes()
                           + " / " + GetLimitBytes() + " bytes）。自画像が保存されていません。"
                           + "ラダー（WireMainMenu の SelfieShareLadder）にさらに小さい段を足すか、"
                           + "他のPlayerDataキーを整理してください。");
        }

        public override void OnPlayerDataStorageWarning(VRCPlayerApi player)
        {
            Debug.LogWarning("[SelfiePortraitStore] PlayerData の容量が上限に近づいています（"
                             + GetUsageBytes() + " / " + GetLimitBytes() + " bytes）。");
        }
    }
}
