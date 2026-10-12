using TMPro;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Persistence;
using VRC.SDK3.Rendering;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

namespace SuzuFactory.AozoraMahjong
{
    // ============================================================
    // 自画像キャラの撮影ブース（完全ローカル・同期変数ゼロ）
    //
    // ■ 役割
    //   キャラ選択画面で「自画像」を選んだときに、その場で3秒のセルフタイマーを回し、
    //   ワールド内カメラで自分のアバターを**背景を切り抜いて**撮る。
    //   撮った絵はプレビューし、「撮り直し」「決定」を選ばせる。
    //   決定したら縮小版を PlayerData へ焼いて、他プレイヤーにも配る
    //   （配布と復元は SelfiePortraitStore が担当）。
    //
    // ■ 背景の切り抜き（Avatar Sticker Camera と同じ原理）
    //   シェーダーもマスクも要らない。カメラの設定だけで抜ける：
    //     ・clearFlags = SolidColor / backgroundColor = (0,0,0,0)  ＝ 背景は透明
    //     ・cullingMask = **MirrorReflection(レイヤー18) のみ**    ＝ 自分のアバターだけ
    //   **自分のアバターは PlayerLocal(10) ではなく MirrorReflection(18) で拾う。**
    //   10 を指定しても World カメラには写らない。ここを間違えると
    //   「風景だけ写って自分が居ない」写真になる（Avatar Sticker Camera の
    //   cullingMask が 262656 = (1<<9)|(1<<18) なのが実装上の根拠）。
    //   レイヤー9(Player) は他プレイヤーなので、自画像では**入れない**
    //   ＝他人が勝手に写り込むことも、他人を撮ることもできない。
    //
    // ■ 撮る仕組み（Udonで公開済みの外部呼び出しだけで組んである）
    //   ・Camera.targetTexture / enabled / cullingMask / clearFlags … Udon公開済み
    //   ・VRCGraphics.Blit(Texture, RenderTexture[, scale, offset])  … Udon公開済み
    //   ・VRCAsyncGPUReadback.Request + OnAsyncGpuReadbackComplete   … Udon公開済み
    //   カメラは liveRt へ毎フレーム描き続け（＝ビューファインダ）、
    //   シャッターでカメラを止める＝liveRt に最後の1枚が残る（これをプレビューに映す）。
    //   「決定」で初めて liveRt → photoRt へ焼く。photoRt は**確定済みの写真**で、自分の画面
    //   （GetPortrait／ストアの高解像度版）が参照しているので、撮り直しやキャンセルでは触らない
    //   （以前はシャッターで photoRt へ上書きしていたため、キャンセルしても自分にだけ新しい写真が出た）。
    //   続けて photoRt → shareRts[i] へ縮小し、AsyncGPUReadback で
    //   RGBA32 の生バイト列を取り出して PlayerData へ入れる。
    //   **PlayerData に入りきらなかったら一段小さい shareRts[i+1] で撮り直す**
    //   （実容量はワールドの他キーやプラットフォームで動くので固定値を当てない）。
    //
    // ■ 置き場所
    //   畳まれる画面の中に置くとUdonが止まるので、**MainMenu脇の常時アクティブな場所**に
    //   1個だけ置く（和了演出カタログ fxCatalog と同じ思想）。UIへの参照は
    //   畳まれた側を持っていてよい（非アクティブなオブジェクトのプロパティは書ける）。
    //
    // ■ 生成／配線
    //   Tools > Kemono Mahjong > Build Main Menu → Wire Main Menu で自動生成・自動割り当て。
    // ============================================================
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class SelfiePortraitCamera : UdonSharpBehaviour
    {
        // 状態。MainMenuController から GetState() で参照する。
        public const int STATE_IDLE = 0;       // カメラ停止
        public const int STATE_COUNTDOWN = 1;  // ビューファインダ表示＋カウントダウン
        public const int STATE_PREVIEW = 2;    // 撮影済み。撮り直し／決定待ち
        public const int STATE_SAVING = 3;     // 決定後。PlayerDataへ焼いている最中
        public const int STATE_FRAMING = 4;    // 構図合わせ。ライブビュー＋調整ボタン（カメラは頭に追従）

        // 構図（上下・ズーム）の保存先。int 1個だけなので PlayerData の容量はほぼ食わない。
        public const string KEY_FRAME = "kmj_selfie_frame";

        // 自分のアバターが居るレイヤー。PlayerLocal(10) ではないので注意。
        private const int LAYER_MIRROR_REFLECTION = 18;

        [Header("参照（Wire Main Menu で自動割り当て）")]
        [Tooltip("撮影用カメラ。targetTexture に liveRt を入れ、既定では enabled=false にしておく。")]
        public Camera captureCamera;
        [Tooltip("ビューファインダ用の RenderTexture（ARGB32）。カメラの出力先。")]
        public RenderTexture liveRt;
        [Tooltip("確定した1枚を焼き付ける RenderTexture（ARGB32）。自分の画面ではこれを使う。")]
        public RenderTexture photoRt;
        [Tooltip("他プレイヤーへ配る縮小版の RenderTexture を**大きい順**に並べたもの（ARGB32）。"
                 + "PlayerData に入りきらなかったら次の段で撮り直す。Wire Main Menu が生成する。")]
        public RenderTexture[] shareRts;
        [Tooltip("自画像の共有ストア。PlayerDataへの書き込みはここ経由。")]
        public SelfiePortraitStore store;
        [Tooltip("メインメニュー。撮影完了・保存完了の通知先・案内文の取得元。")]
        public MainMenuController menu;

        [Header("UI（ScreenSelfie の中。畳まれていても参照は保持してよい）")]
        [Tooltip("ビューファインダ（liveRt を映す RawImage）。")]
        public RawImage viewfinder;
        [Tooltip("撮影結果のプレビュー（photoRt を映す RawImage）。")]
        public RawImage previewImage;
        [Tooltip("カウントダウンの数字。")]
        public TextMeshProUGUI countdownLabel;
        [Tooltip("案内文（構えてください／この写真でよろしいですか）。")]
        public TextMeshProUGUI infoLabel;
        [Tooltip("「撮り直し」ボタンのルート。プレビュー中だけ出す。")]
        public GameObject btnRetake;
        [Tooltip("「決定」ボタンのルート。プレビュー中だけ出す。")]
        public GameObject btnConfirm;
        [Tooltip("「撮影」ボタンのルート。構図合わせ中だけ出す。")]
        public GameObject btnShoot;
        [Tooltip("構図調整ボタンの入れ物（上へ／下へ／寄る／引く／もとに戻す）。構図合わせ中だけ出す。")]
        public GameObject frameControls;
        [Tooltip("顔の位置ガイド（薄暗い四角）。構図合わせ／カウントダウン中だけ出す。")]
        public GameObject faceGuide;
        [Tooltip("カウントダウンの数字の座布団。数字と同じくカウントダウン中だけ出す。")]
        public GameObject countdownBack;

        [Header("音（任意）")]
        public AudioSource audioSource;
        [Tooltip("カウントダウンの1秒ごとの音。")]
        public AudioClip countdownClip;
        [Tooltip("シャッター音。")]
        public AudioClip shutterClip;

        [Header("撮影パラメータ")]
        [Tooltip("カウントダウンの秒数。")]
        public float countdownSeconds = 3.0f;
        [Tooltip("頭からカメラまでの水平距離[m]。")]
        public float captureDistance = 1.4f;
        [Tooltip("カメラの高さ（頭の高さからの相対[m]）。")]
        public float cameraHeightOffset = -0.10f;
        [Tooltip("カメラが狙う点の高さ（頭の高さからの相対[m]）。胸のあたりを狙うと収まりが良い。")]
        public float aimHeightOffset = -0.30f;
        [Tooltip("撮影カメラの画角。")]
        public float cameraFieldOfView = 35.0f;
        [Tooltip("撮影カメラの cullingMask。0なら MirrorReflection(18) だけを自動で入れる。")]
        public int cameraCullingMask = 0;

        [Header("構図調整（プレイヤーが撮影前に合わせる）")]
        // ケモノアバターは耳・角・盛り髪が既定の構図から上へはみ出して切れる。
        // そこで撮影前に本人が上下とズームを合わせられるようにしてある。
        [Tooltip("上下1段あたりの移動量[m]。カメラと注視点を同じだけ動かすので、"
                 + "あおり角は変わらない（構図がそのまま平行移動する）。")]
        public float frameStepMeters = 0.05f;
        [Tooltip("上下の下限段（負＝構図を下げる）。")]
        public int frameStepMin = -6;
        [Tooltip("上下の上限段（正＝構図を上げる＝頭の上を広く取る）。"
                 + "耳・角が切れるアバター用に多めに取ってある。")]
        public int frameStepMax = 8;
        [Tooltip("ズーム1段あたりの距離[m]。正の段で引く（カメラが遠ざかる＝広く写る）。")]
        public float zoomStepMeters = 0.20f;
        [Tooltip("ズームの下限段（負＝寄る）。")]
        public int zoomStepMin = -2;
        [Tooltip("ズームの上限段（正＝引く）。")]
        public int zoomStepMax = 5;

        [Header("共有")]
        [Tooltip("Readback の結果が上下反転していたら true にする（プラットフォーム差の逃げ）。")]
        public bool flipVertically = false;
        [Tooltip("保存の保険タイムアウト[秒]。Readbackが返らなくても画面を進める。")]
        public float saveTimeoutSeconds = 5.0f;

        // --- ローカル状態（同期しない） ---
        private int state_ = STATE_IDLE;
        private float countdownEndTime_ = 0.0f;
        private int lastShownCount_ = -1;
        private bool hasPhoto_ = false;   // photoRt に確定済みの写真がある
        private bool hasShot_ = false;    // シャッターを切ったが未確定（liveRt に止まっている）
        private bool initialized_ = false;

        private byte[] readbackBuffer_;
        private float saveDeadline_ = 0.0f;
        private bool saveSucceeded_ = false;
        // 共有サイズのラダーの現在段（0＝最大）。
        private int shareStep_ = 0;

        // --- 構図（本人が合わせた値。PlayerData に int 1個で残す） ---
        private int frameStep_ = 0;   // 正＝構図を上げる
        private int zoomStep_ = 0;    // 正＝引く
        private bool frameLoaded_ = false;

        // ラダーのi段目。範囲外・未割当なら null。
        private RenderTexture GetShareRt(int index)
        {
            if (shareRts == null || index < 0 || index >= shareRts.Length) { return null; }
            return shareRts[index];
        }

        // Readback の受け皿をそのRTのサイズに合わせる。
        private void EnsureBuffer(RenderTexture rt)
        {
            int need = rt.width * rt.height * 4;
            if (readbackBuffer_ == null || readbackBuffer_.Length != need)
            {
                readbackBuffer_ = new byte[need];
            }
        }

        void Start()
        {
            Initialize();
        }

        private void Initialize()
        {
            if (initialized_) { return; }
            initialized_ = true;

            if (captureCamera != null)
            {
                captureCamera.targetTexture = liveRt;
                captureCamera.fieldOfView = cameraFieldOfView;

                // 背景の切り抜き：透明で塗り、自分のアバターだけを描く。
                captureCamera.clearFlags = CameraClearFlags.SolidColor;
                captureCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
                captureCamera.cullingMask = (cameraCullingMask != 0)
                    ? cameraCullingMask
                    : (1 << LAYER_MIRROR_REFLECTION);

                captureCamera.enabled = false;
            }

            if (viewfinder != null)
            {
                viewfinder.texture = liveRt;
                viewfinder.color = Color.white;
                viewfinder.raycastTarget = false;
            }
            if (previewImage != null)
            {
                previewImage.texture = liveRt;   // 未確定の1枚（カメラを止めた liveRt）
                previewImage.color = Color.white;
                previewImage.raycastTarget = false;
            }

            // Readback バッファはラダーの先頭（最大）に合わせて確保しておく。
            // 段が下がったら EnsureBuffer が取り直す。
            var top = GetShareRt(0);
            if (top != null) { EnsureBuffer(top); }

            ApplyStateToUi();
        }

        // ============================================================
        // MainMenuController から呼ぶ入口
        // ============================================================

        // 撮影画面を開いた／「撮り直し」を押した。まず**構図合わせ**に入る。
        // カメラは頭へ追従し続けるので、鏡のように自分を見ながら上下・ズームを合わせられる。
        // 「撮影」を押すと BeginCountdown へ進み、そこでカメラ位置が固定される。
        public void BeginFraming()
        {
            Initialize();
            if (Networking.LocalPlayer == null) { return; }

            LoadFrame();

            hasShot_ = false;   // 撮り直し＝前の未確定の1枚は捨てる
            state_ = STATE_FRAMING;
            PlaceCamera();
            if (captureCamera != null) { captureCamera.enabled = true; }
            ApplyStateToUi();
        }

        // --- 構図調整ボタン（MainMenuController 経由で呼ばれる） ---
        // 押した値はその場でカメラへ効き、PlayerData に残るので次回も同じ構図から始まる。

        public void FrameUp() { AdjustFrame(1); }
        public void FrameDown() { AdjustFrame(-1); }
        public void ZoomIn() { AdjustZoom(-1); }   // 寄る＝カメラを近づける
        public void ZoomOut() { AdjustZoom(1); }   // 引く＝カメラを遠ざける

        public void FrameReset()
        {
            if (state_ != STATE_FRAMING) { return; }

            frameStep_ = 0;
            zoomStep_ = 0;
            SaveFrame();
            PlaceCamera();
            ApplyStateToUi();
        }

        private void AdjustFrame(int delta)
        {
            if (state_ != STATE_FRAMING) { return; }

            frameStep_ = Mathf.Clamp(frameStep_ + delta, frameStepMin, frameStepMax);
            SaveFrame();
            PlaceCamera();
            ApplyStateToUi();
        }

        private void AdjustZoom(int delta)
        {
            if (state_ != STATE_FRAMING) { return; }

            zoomStep_ = Mathf.Clamp(zoomStep_ + delta, zoomStepMin, zoomStepMax);
            SaveFrame();
            PlaceCamera();
            ApplyStateToUi();
        }

        // 案内文の末尾に出す現在値。**書式指定つきの ToString は使わない**
        // （Udon で通る保証が無いので、int と手組みの文字列で作る）。
        private string FrameReadout()
        {
            int cm = Mathf.RoundToInt(frameStepMeters * frameStep_ * 100f);
            string lift = (cm > 0 ? "+" : (cm < 0 ? "-" : "±")) + Mathf.Abs(cm) + "cm";

            float dist = captureDistance + zoomStepMeters * zoomStep_;
            if (dist < 0.3f) { dist = 0.3f; }
            int d10 = Mathf.RoundToInt(dist * 10f);

            return "(" + lift + " / " + (d10 / 10) + "." + (d10 % 10) + "m)";
        }

        // 構図は int 1個にまとめて PlayerData へ入れる。
        // 段は必ず -31..+31 に収まるので +32 して非負にしてから詰める
        // （Udon で負の値を持ち回さないための定石）。
        private void SaveFrame()
        {
            if (Networking.LocalPlayer == null) { return; }
            PlayerData.SetInt(KEY_FRAME, (frameStep_ + 32) * 64 + (zoomStep_ + 32));
        }

        private void LoadFrame()
        {
            if (frameLoaded_) { return; }

            var local = Networking.LocalPlayer;
            if (local == null) { return; }
            frameLoaded_ = true;

            if (!PlayerData.HasKey(local, KEY_FRAME)) { return; }
            int v = PlayerData.GetInt(local, KEY_FRAME);
            if (v <= 0) { return; }

            frameStep_ = Mathf.Clamp((v / 64) - 32, frameStepMin, frameStepMax);
            zoomStep_ = Mathf.Clamp((v % 64) - 32, zoomStepMin, zoomStepMax);
        }

        // 自分の PlayerData が届いた時点で読み直す（Start より後に来ることがある）。
        public override void OnPlayerRestored(VRCPlayerApi player)
        {
            var local = Networking.LocalPlayer;
            if (player == null || local == null || player.playerId != local.playerId) { return; }

            frameLoaded_ = false;
            LoadFrame();
        }

        // 「撮影」を押した。ここでカメラ位置を固定して3秒のセルフタイマーに入る。
        public void BeginCountdown()
        {
            Initialize();
            if (Networking.LocalPlayer == null) { return; }

            PlaceCamera();

            state_ = STATE_COUNTDOWN;
            countdownEndTime_ = Time.time + countdownSeconds;
            lastShownCount_ = -1;

            if (captureCamera != null) { captureCamera.enabled = true; }
            ApplyStateToUi();
        }

        // 撮影画面を閉じた（もどる）。カメラを止める。
        public void StopCamera()
        {
            hasShot_ = false;   // キャンセル＝未確定の1枚は捨てる（確定済みの photoRt はそのまま）
            state_ = STATE_IDLE;
            if (captureCamera != null) { captureCamera.enabled = false; }
            ApplyStateToUi();
        }

        public int GetState() { return state_; }

        public bool HasPhoto() { return hasPhoto_ && photoRt != null; }

        // シャッターを切った未確定の1枚があるか（「決定」を受け付けてよいか）。
        public bool HasPendingShot() { return hasShot_ && liveRt != null && photoRt != null; }

        // 自分の画面で使うポートレート。今回のセッションで撮っていれば高解像度の photoRt。
        // 撮っていなければ（再入場直後など）、PlayerData から復元した共有版へ落ちる。
        public Texture GetPortrait()
        {
            if (HasPhoto()) { return photoRt; }

            if (store != null)
            {
                var local = Networking.LocalPlayer;
                if (local != null) { return store.GetPortraitOf(local); }
            }
            return null;
        }

        // 自分がこのワールドに自画像を持っているか（今回撮った or 過去に保存した）。
        public bool HasAnyPortrait()
        {
            return GetPortrait() != null;
        }

        // 「決定」で呼ぶ。ラダーの先頭（最大サイズ）から順に試す。
        // 完了は OnAsyncGpuReadbackComplete。
        public void BeginSave()
        {
            Initialize();

            // ここで初めて未確定の1枚を確定写真へ焼く。
            if (HasPendingShot())
            {
                VRCGraphics.Blit(liveRt, photoRt);
                hasPhoto_ = true;
                hasShot_ = false;
            }

            if (!HasPhoto() || store == null || GetShareRt(0) == null)
            {
                // 共有できない構成でも、ローカルの写真は使えるので失敗扱いで先へ進める。
                FinishSave(false);
                return;
            }

            state_ = STATE_SAVING;
            saveSucceeded_ = false;
            shareStep_ = 0;
            ApplyStateToUi();

            RequestShareStep();
        }

        // ラダーの現在段で縮小して Readback を投げる。
        private void RequestShareStep()
        {
            var rt = GetShareRt(shareStep_);
            if (rt == null)
            {
                Debug.LogError("[SelfiePortraitCamera] 共有用RTのラダーを使い切りました。写真は共有されません。");
                FinishSave(false);
                return;
            }

            EnsureBuffer(rt);

            // 縮小（必要なら上下反転）。Blit のスケール -1 で反転できる。
            if (flipVertically)
            {
                VRCGraphics.Blit(photoRt, rt, new Vector2(1f, -1f), new Vector2(0f, 1f));
            }
            else
            {
                VRCGraphics.Blit(photoRt, rt);
            }

            saveDeadline_ = Time.time + saveTimeoutSeconds;
            VRCAsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, (IUdonEventReceiver)this);
        }

        public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request)
        {
            if (state_ != STATE_SAVING) { return; }

            var rt = GetShareRt(shareStep_);
            if (rt == null) { FinishSave(false); return; }

            if (request.hasError)
            {
                Debug.LogError("[SelfiePortraitCamera] Readback に失敗しました。写真は共有されません。");
                FinishSave(false);
                return;
            }

            if (readbackBuffer_ == null || !request.TryGetData(readbackBuffer_))
            {
                Debug.LogError("[SelfiePortraitCamera] Readback のデータを取り出せませんでした。");
                FinishSave(false);
                return;
            }

            if (store.SaveLocalPhoto(readbackBuffer_, rt.width, rt.height))
            {
                FinishSave(true);
                return;
            }

            // 入りきらなかった＝一段小さいサイズで撮り直す。
            // （PlayerData の実容量はワールドの他キーやプラットフォームで動くので、
            //   固定値を当てずにここで実測に合わせて落ちる）
            ++shareStep_;
            RequestShareStep();
        }

        private void FinishSave(bool ok)
        {
            // 決定した1枚の実物（photoRt＝既定 512x768）をストアへ預ける。
            // これで卓の表示（宣言カットイン／あがり画面／精算／終局結果／役満カットイン）でも
            // **自分の席だけ**は高解像度が出る。他プレイヤーには従来どおり共有版が配られる。
            // 共有に失敗（容量オーバー）していても自分の画面には出せるので ok は見ない。
            if (store != null && HasPhoto()) { store.SetLocalHighResPortrait(photoRt); }

            saveSucceeded_ = ok;
            state_ = STATE_IDLE;
            if (captureCamera != null) { captureCamera.enabled = false; }
            ApplyStateToUi();

            if (menu != null) { menu.OnSelfieSaved(ok); }
        }

        public bool GetLastSaveSucceeded() { return saveSucceeded_; }

        // ============================================================
        // 進行（撮影画面を開いている間だけ動く）
        // ============================================================
        void Update()
        {
            if (state_ == STATE_FRAMING)
            {
                // 構図合わせ中はカメラを頭へ追従させる＝鏡のように自分が映る。
                // （「撮影」でカウントダウンに入った時点で固定され、以降は動かない）
                PlaceCamera();
                return;
            }

            if (state_ == STATE_SAVING)
            {
                // Readback が返らないまま固まらないための保険。
                if (Time.time >= saveDeadline_)
                {
                    Debug.LogWarning("[SelfiePortraitCamera] Readback がタイムアウトしました。共有せずに進みます。");
                    FinishSave(false);
                }
                return;
            }

            if (state_ != STATE_COUNTDOWN) { return; }

            float remain = countdownEndTime_ - Time.time;
            if (remain <= 0.0f)
            {
                Capture();
                return;
            }

            // 3 → 2 → 1。秒が変わった瞬間だけ音を鳴らす。
            int shown = Mathf.CeilToInt(remain);
            if (shown != lastShownCount_)
            {
                lastShownCount_ = shown;
                if (countdownLabel != null) { countdownLabel.text = shown.ToString(); }
                PlayClip(countdownClip);
            }
        }

        // シャッター。カメラを止めて liveRt に今の絵を残す（プレビュー）。確定写真の photoRt へ焼くのは
        // 「決定」（BeginSave）のときだけ＝撮り直し・キャンセルで自分の確定写真は変わらない。
        private void Capture()
        {
            // photoRt へは焼かない（確定は「決定」で）。カメラを止めれば liveRt に今の絵が残る。
            if (liveRt != null && photoRt != null)
            {
                hasShot_ = true;
            }

            PlayClip(shutterClip);

            state_ = STATE_PREVIEW;
            if (captureCamera != null) { captureCamera.enabled = false; }
            ApplyStateToUi();

            if (menu != null) { menu.OnSelfieCaptured(); }
        }

        // カウントダウン開始時に1回だけ置く。以降は動かさないので、
        // 3秒のあいだにポーズを取ったり向きを変えたりできる（セルフタイマーと同じ）。
        private void PlaceCamera()
        {
            if (captureCamera == null) { return; }

            var player = Networking.LocalPlayer;
            if (player == null) { return; }

            var head = player.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
            Vector3 headPos = head.position;

            // 頭の向きの水平成分。真上・真下を向いていても破綻しないよう、
            // 潰れたときはプレイヤーの体の向きへ落とす。
            Vector3 forward = head.rotation * Vector3.forward;
            forward.y = 0.0f;
            if (forward.sqrMagnitude < 0.0001f)
            {
                forward = player.GetRotation() * Vector3.forward;
                forward.y = 0.0f;
            }
            if (forward.sqrMagnitude < 0.0001f)
            {
                forward = Vector3.forward;
            }
            forward = forward.normalized;

            // 本人が合わせた構図を効かせる。
            //  ・lift はカメラと注視点を同じだけ動かす＝あおり角を変えずに構図を平行移動する
            //  ・dist は寄り／引き。背景は透明なので引いても破綻しないが、
            //    共有版（96x144）では小さく写るぶん潰れる
            float lift = frameStepMeters * frameStep_;
            float dist = captureDistance + zoomStepMeters * zoomStep_;
            if (dist < 0.3f) { dist = 0.3f; }

            Vector3 camPos = headPos + forward * dist;
            camPos.y = headPos.y + cameraHeightOffset + lift;

            Vector3 aim = headPos;
            aim.y = headPos.y + aimHeightOffset + lift;

            Vector3 look = aim - camPos;
            if (look.sqrMagnitude < 0.0001f) { look = -forward; }
            look = look.normalized;

            captureCamera.transform.SetPositionAndRotation(
                camPos, Quaternion.LookRotation(look, Vector3.up));
            captureCamera.fieldOfView = cameraFieldOfView;
        }

        // 状態に応じてビューファインダ／プレビュー／ボタンを出し分ける。
        private void ApplyStateToUi()
        {
            bool framing = (state_ == STATE_FRAMING);
            bool counting = (state_ == STATE_COUNTDOWN);
            bool preview = (state_ == STATE_PREVIEW);
            bool saving = (state_ == STATE_SAVING);

            if (viewfinder != null) { viewfinder.gameObject.SetActive(framing || counting); }
            if (previewImage != null) { previewImage.gameObject.SetActive(preview || saving); }
            if (countdownLabel != null) { countdownLabel.gameObject.SetActive(counting); }
            if (btnRetake != null) { btnRetake.SetActive(preview); }
            if (btnConfirm != null) { btnConfirm.SetActive(preview); }
            if (btnShoot != null) { btnShoot.SetActive(framing); }
            if (frameControls != null) { frameControls.SetActive(framing); }
            // 顔ガイドは「これから撮る」間だけ。プレビュー／保存では邪魔なので畳む。
            if (faceGuide != null) { faceGuide.SetActive(framing || counting); }
            // 数字の座布団は数字と同じ寿命。構図合わせ中に枠の中心へ暗い四角を残すと、
            // 顔をそこへ合わせようとして撮れた絵が下寄りになる。
            if (countdownBack != null) { countdownBack.SetActive(counting); }

            if (infoLabel != null && menu != null)
            {
                if (framing) { infoLabel.text = menu.GetSelfieFramingText() + "  " + FrameReadout(); }
                else if (counting) { infoLabel.text = menu.GetSelfiePoseText(); }
                else if (preview) { infoLabel.text = menu.GetSelfieConfirmText(); }
                else if (saving) { infoLabel.text = menu.GetSelfieSavingText(); }
                else { infoLabel.text = ""; }
            }
        }

        private void PlayClip(AudioClip clip)
        {
            if (audioSource == null || clip == null) { return; }
            audioSource.PlayOneShot(clip);
        }
    }
}
