#if UNITY_EDITOR
// 入場時の注意書きパネル（EntryNotice）をシーンに生成・再生成するエディタスクリプト。
//
// ■ 何を作るか
//   EntryNotice                     … 拘束の基準点（anchor）。EntryNoticeGate を載せる。
//    ├ Canvas                       … World Space Canvas（暗幕＋パネル本体）
//    │   ├ Dim                      … 前方を軽く暗くする板
//    │   └ Panel
//    │       ├ JA_Body / EN_Body / KO_Body / ZHS_Body / ZHT_Body … 本文一式（言語ごとに丸ごと差し替える）
//    │       ├ BtnAgree / BtnLang   … 言語に依存しない共有ボタン
//    │       └ Footer               … 最終更新日
//    └ Cage                         … 不可視の壁（VRの実歩行で桟橋から落ちるのを防ぐ）
//
// ■ 配置の決め方
//   VRCSceneDescriptor の spawns[0] を読み、その 0.2m 前方を anchor にする。
//   スポーンを動かしたら本メニューを再実行すれば追従する。
//   桟橋（Sanbashi）の実測（2026-09-01）: 床 y≈-0.03 / x -17.0〜-11.2 / z -0.8〜2.2。
//   スポーン(-16.75,0,0.5)は背後0.25m・右1.3mで水面。壁の内寸はこれに収めてある。
//
// ■ 文言の正はこのファイル
//   実行のたびに TMP のテキストを上書きする。Inspector で直しても次回の Build で
//   戻るので、文言を変えるときはここを編集して再実行すること
//   （EnsureTmp が既存テキストを温存して「古いラベルが残る」事故を過去に踏んでいるため、
//    本ビルダーは常に上書きする方針にしている）。
//
// 実行: Tools > Kemono Mahjong > Build Entry Notice
//       Tools > Kemono Mahjong > Validate Entry Notice
using System.Collections.Generic;
using SuzuFactory.AozoraMahjong;
using TMPro;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using VRC.Udon;

public static class BuildEntryNotice
{
    const string RootName = "EntryNotice";
    const string FontAssetPath = "Assets/Mahjong/Fonts/NotoSansJP-Bold SDF.asset";
    const string GateScriptPath = "Assets/Mahjong/Sources/EntryNoticeGate.cs";
    const string ProgramAssetPath = "Assets/Mahjong/scene_UdonProgramSources/EntryNoticeGate Udon C# Program Asset.asset";

    // --- 配置（anchor ローカル。root は rotY=90 なので「ローカル +Z ＝ 世界 +X ＝ 前方」）---
    const float SpawnForwardOffset = 0.2f;   // スポーンから前方へこれだけ寄せた点を anchor にする
    const float PanelDistance = 1.25f;       // anchor からパネルまでの距離
    // パネル中心の高さ。目線1.6m・デスクトップの縦FOV60度（＝上下±30度）に
    // パネル全体が収まるように決めてある。下げすぎると入場ボタンが視野の外に落ちる。
    const float PanelHeight = 1.58f;

    static readonly Vector2 CanvasSize = new Vector2(2900f, 1900f); // 暗幕込みの大きさ（px＝mm）
    static readonly Vector2 PanelSize = new Vector2(2100f, 1280f);
    const float CanvasScale = 0.001f;

    // 不可視の壁の内寸（anchor ローカル）。桟橋からはみ出さない値。
    const float CageBack = -0.35f;   // 背後（-Z）
    const float CageFront = 1.45f;   // 前方（+Z）※パネル(1.25)より外側に置くこと
    const float CageSide = 1.00f;    // 左右（±X）
    const float CageThickness = 0.2f;
    const float CageHeight = 3.0f;
    const float CageBottom = -0.5f;

    // --- 色 ---
    static readonly Color DimColor = new Color(0f, 0f, 0f, 0.45f);
    // パネルは不透明。地の色が暗いので、わずかな透過でも明るい背景（陽の当たった木材）が
    // 幽霊のように透けて可読性を落とす。周囲の景色は Dim 側で残す。
    static readonly Color PanelColor = new Color(0.055f, 0.065f, 0.09f, 1f);
    static readonly Color NoticeBoxColor = new Color(0.45f, 0.16f, 0.10f, 0.55f);
    static readonly Color SectionBoxColor = new Color(1f, 1f, 1f, 0.05f);
    static readonly Color AgreeColor = new Color(0.16f, 0.42f, 0.28f, 0.98f);
    static readonly Color LangColor = new Color(0.20f, 0.24f, 0.34f, 0.95f);
    static readonly Color TextColor = new Color(0.96f, 0.96f, 0.98f, 1f);
    static readonly Color SubTextColor = new Color(0.72f, 0.76f, 0.85f, 1f);

    // ============================================================
    // 文言（ここが正。変更したら Build を再実行する）
    // ============================================================

    const string FooterText = "最終更新 2026-09-01 ／ Last updated 2026-09-01";

    const string JaTitle = "ご入場の前にお読みください";
    const string JaSubtitle = "超能力ケモノ麻雀 — オープンベータ";
    const string JaNoticeHead = "このワールドは開発中です（オープンベータ）";
    const string JaNoticeBody =
        "・不具合により対局が進行不能になったり、演出が正しく出ないことがあります\n" +
        "・段位・戦績データは、開発の都合で予告なくリセットされる場合があります\n" +
        "・仕様・ルール・キャラクターの能力は今後も変更されます\n" +
        "・不具合を見つけたら、再現手順を添えて製作者までご報告ください　→　x.com/Very_Chevy";
    const string JaTermsHead = "利用規約　Terms and Conditions";
    const string JaTermsBody =
        "1. 本ワールドは開発中の試験公開であり、動作の正確性・継続性を保証しません。利用は自己責任でお願いします。\n" +
        "2. 段位・戦績などのデータは、保存の保証をしません。予告なくリセット・仕様変更されることがあります。\n" +
        "3. 本ワールドに含まれるモデル・テクスチャ・音源・スクリプト等の無断転載、再配布、改変、他ワールドへの流用を禁止します。\n" +
        "4. 撮影・配信・動画投稿は許可します。\n" +
        "5. 利用にあたっては VRChat の利用規約およびコミュニティガイドラインを遵守してください。\n" +
        "6. 本規約は予告なく変更されることがあります。変更後も本ワールドを利用した場合、変更後の規約に同意したものとみなします。\n" +
        "7. 規約・ルールに違反した場合、インスタンスからの退出をお願いする、または以後の参加をお断りすることがあります。";
    const string JaRulesHead = "ワールド内ルール　Rules";
    const string JaRulesBody =
        "1. 通し・共謀の禁止 — 外部通話やジェスチャーで手牌・待ちを伝える行為は、対局の根幹を壊すため厳禁です。\n" +
        "2. 途中抜け・放置の禁止 — 対局を開始したら最後まで打ち切ってください。やむを得ない場合は一言お願いします。\n" +
        "3. 暴言・ハラスメント・差別的言動の禁止 — 煽り、晒し、しつこい指導も含みます。\n" +
        "4. 妨害行為の禁止 — 他プレイヤーの視界を塞ぐ、卓上に居座る、大音量を出す、カメラで対局を妨げる等。\n" +
        "5. 不正の禁止 — 改造クライアント、ワールドの解析・改変による有利化を禁止します。\n" +
        "6. 観戦時の配慮 — 対局中の卓に対して、手牌が見える位置からの助言・指摘はご遠慮ください。\n" +
        "7. 製作者・スタッフからの案内には従ってください。";
    const string JaAgreeNote = "同意いただけない場合は、VRChatメニューからインスタンスを退出してください。";

    const string EnTitle = "Please Read Before Entering";
    const string EnSubtitle = "Kemono Mahjong — Open Beta";
    const string EnNoticeHead = "This world is under development (Open Beta)";
    const string EnNoticeBody =
        "- Bugs may leave a game unfinishable, or make effects display incorrectly.\n" +
        "- Rank and match records may be reset without notice while development continues.\n" +
        "- Rules, specifications and character abilities are still subject to change.\n" +
        "- Found a bug? Please report it with steps to reproduce  ->  x.com/Very_Chevy";
    const string EnTermsHead = "Terms and Conditions";
    const string EnTermsBody =
        "1. This world is an in-development test release. Correct or continuous operation is not guaranteed. Use it at your own risk.\n" +
        "2. No guarantee is made that rank or match data will be kept. It may be reset or changed without notice.\n" +
        "3. Unauthorized reproduction, redistribution, modification, or reuse in other worlds of any model, texture, audio or script contained in this world is prohibited.\n" +
        "4. Screenshots, streaming and video uploads are permitted.\n" +
        "5. You must comply with the VRChat Terms of Service and Community Guidelines.\n" +
        "6. These terms may change without notice. If you keep using this world after a change, you are deemed to have accepted the updated terms.\n" +
        "7. If you break these terms or rules, you may be asked to leave the instance, or be refused entry in the future.";
    const string EnRulesHead = "Rules";
    const string EnRulesBody =
        "1. No collusion or signalling - Telling others about hands or waits through outside voice chat or gestures is strictly forbidden; it destroys the game.\n" +
        "2. No quitting or going AFK mid-game - Once a game starts, please play it to the end. If you must leave, say so first.\n" +
        "3. No abuse, harassment or discriminatory language - This includes taunting, public shaming and persistent unwanted coaching.\n" +
        "4. No disruptive behaviour - Blocking someone's view, sitting on the table, loud noise, or getting in the way with cameras.\n" +
        "5. No cheating - Modified clients, or gaining an advantage by analysing or altering the world, are prohibited.\n" +
        "6. Spectate considerately - Do not advise or comment on a game in progress from a position where you can see the hands.\n" +
        "7. Please follow instructions from the creator and staff.";
    const string EnAgreeNote = "If you do not agree, please leave the instance from your VRChat menu.";

    // 韓国語。確認できる話者がいないので、規約の意味は日本語・英語と1対1で揃えることを優先している。
    const string KoTitle = "입장 전에 꼭 읽어 주세요";
    const string KoSubtitle = "초능력 케모노 마작 — 오픈 베타";
    const string KoNoticeHead = "이 월드는 개발 중입니다 (오픈 베타)";
    const string KoNoticeBody =
        "・버그로 인해 대국을 진행할 수 없게 되거나 연출이 제대로 나오지 않을 수 있습니다\n" +
        "・단위·전적 데이터는 개발 사정에 따라 예고 없이 초기화될 수 있습니다\n" +
        "・사양·규칙·캐릭터의 능력은 앞으로도 변경됩니다\n" +
        "・버그를 발견하면 재현 방법과 함께 제작자에게 알려 주세요　→　x.com/Very_Chevy";
    const string KoTermsHead = "이용 약관　Terms and Conditions";
    const string KoTermsBody =
        "1. 이 월드는 개발 중인 시험 공개이며, 동작의 정확성·지속성을 보장하지 않습니다. 이용은 본인 책임하에 해 주세요.\n" +
        "2. 단위·전적 등의 데이터는 보존을 보장하지 않습니다. 예고 없이 초기화되거나 사양이 변경될 수 있습니다.\n" +
        "3. 이 월드에 포함된 모델·텍스처·음원·스크립트 등의 무단 전재, 재배포, 개변, 다른 월드로의 유용을 금지합니다.\n" +
        "4. 촬영·방송·동영상 게시는 허용합니다.\n" +
        "5. 이용 시 VRChat 이용 약관 및 커뮤니티 가이드라인을 준수해 주세요.\n" +
        "6. 이 약관은 예고 없이 변경될 수 있습니다. 변경 후에도 이 월드를 이용한 경우, 변경된 약관에 동의한 것으로 간주합니다.\n" +
        "7. 약관·규칙을 위반한 경우, 인스턴스에서 퇴장을 요청하거나 이후의 참가를 거절할 수 있습니다.";
    const string KoRulesHead = "월드 내 규칙　Rules";
    const string KoRulesBody =
        "1. 통모·담합 금지 — 외부 통화나 제스처로 손패·대기패를 알리는 행위는 대국의 근간을 무너뜨리므로 엄격히 금지합니다.\n" +
        "2. 도중 이탈·방치 금지 — 대국을 시작하면 끝까지 쳐 주세요. 부득이한 경우에는 한마디 남겨 주세요.\n" +
        "3. 폭언·괴롭힘·차별적 언행 금지 — 도발, 박제, 원치 않는 지도도 포함됩니다.\n" +
        "4. 방해 행위 금지 — 다른 플레이어의 시야를 가리기, 탁 위에 눌러앉기, 큰 소리 내기, 카메라로 대국을 방해하기 등.\n" +
        "5. 부정행위 금지 — 개조 클라이언트, 월드의 분석·개변으로 유리하게 만드는 행위를 금지합니다.\n" +
        "6. 관전 시 배려 — 대국 중인 탁에 대해 손패가 보이는 위치에서 조언·지적하는 것은 삼가 주세요.\n" +
        "7. 제작자·스태프의 안내에 따라 주세요.";
    const string KoAgreeNote = "동의하지 않으시는 경우, VRChat 메뉴에서 인스턴스를 나가 주세요.";


    // 中国語（簡体字・繁体字）。確認できる話者がいないので、韓国語と同じく日英と1対1で揃えることを優先。
    const string ZhsTitle = "进入前请务必阅读";
    const string ZhsSubtitle = "超能力兽人麻将 — 公开测试版";
    const string ZhsNoticeHead = "本世界仍在开发中（公开测试版）";
    const string ZhsNoticeBody =
        "・可能因故障导致对局无法进行，或演出无法正常显示\n" +
        "・段位・战绩数据可能因开发需要而在未经通知的情况下重置\n" +
        "・规格・规则・角色的能力今后仍会变更\n" +
        "・发现故障时，请附上重现步骤报告给制作者　→　x.com/Very_Chevy";
    const string ZhsTermsHead = "使用条款　Terms and Conditions";
    const string ZhsTermsBody =
        "1. 本世界为开发中的测试公开版本，不保证运作的正确性与持续性。使用风险请自行承担。\n" +
        "2. 段位・战绩等数据不保证保存，可能在未经通知的情况下重置或变更规格。\n" +
        "3. 禁止擅自转载、再发布、修改本世界包含的模型・贴图・音源・脚本等，或挪用于其他世界。\n" +
        "4. 允许拍摄・直播・上传视频。\n" +
        "5. 使用时请遵守 VRChat 的使用条款及社区准则。\n" +
        "6. 本条款可能在未经通知的情况下变更。变更后仍继续使用本世界者，视为同意变更后的条款。\n" +
        "7. 违反条款・规则时，可能会请您退出实例，或拒绝您日后参加。";
    const string ZhsRulesHead = "世界内规则　Rules";
    const string ZhsRulesBody =
        "1. 禁止通牌・串通 — 通过外部语音或手势传达手牌・听牌的行为会破坏对局根本，严格禁止。\n" +
        "2. 禁止中途离开・挂机 — 对局开始后请打到最后。不得已时请先说一声。\n" +
        "3. 禁止辱骂・骚扰・歧视性言行 — 包括挑衅、公开羞辱、纠缠不休的指导。\n" +
        "4. 禁止妨碍行为 — 遮挡其他玩家视线、坐在牌桌上、发出巨大音量、用相机妨碍对局等。\n" +
        "5. 禁止作弊 — 禁止使用修改客户端，或通过分析・修改世界获取优势。\n" +
        "6. 观战时请体谅他人 — 请勿在看得到手牌的位置对进行中的对局给予建议・指点。\n" +
        "7. 请遵从制作者・工作人员的指引。";
    const string ZhsAgreeNote = "如不同意，请从 VRChat 菜单退出实例。";

    const string ZhtTitle = "進入前請務必閱讀";
    const string ZhtSubtitle = "超能力獸人麻將 — 公開測試版";
    const string ZhtNoticeHead = "本世界仍在開發中（公開測試版）";
    const string ZhtNoticeBody =
        "・可能因故障導致對局無法進行，或演出無法正常顯示\n" +
        "・段位・戰績資料可能因開發需要而在未經通知的情況下重置\n" +
        "・規格・規則・角色的能力今後仍會變更\n" +
        "・發現故障時，請附上重現步驟回報給製作者　→　x.com/Very_Chevy";
    const string ZhtTermsHead = "使用條款　Terms and Conditions";
    const string ZhtTermsBody =
        "1. 本世界為開發中的測試公開版本，不保證運作的正確性與持續性。使用風險請自行承擔。\n" +
        "2. 段位・戰績等資料不保證保存，可能在未經通知的情況下重置或變更規格。\n" +
        "3. 禁止擅自轉載、再發布、修改本世界包含的模型・貼圖・音源・腳本等，或挪用於其他世界。\n" +
        "4. 允許拍攝・直播・上傳影片。\n" +
        "5. 使用時請遵守 VRChat 的使用條款及社群守則。\n" +
        "6. 本條款可能在未經通知的情況下變更。變更後仍繼續使用本世界者，視為同意變更後的條款。\n" +
        "7. 違反條款・規則時，可能會請您退出實例，或拒絕您日後參加。";
    const string ZhtRulesHead = "世界內規則　Rules";
    const string ZhtRulesBody =
        "1. 禁止通牌・串通 — 透過外部語音或手勢傳達手牌・聽牌的行為會破壞對局根本，嚴格禁止。\n" +
        "2. 禁止中途離開・掛機 — 對局開始後請打到最後。不得已時請先說一聲。\n" +
        "3. 禁止辱罵・騷擾・歧視性言行 — 包括挑釁、公開羞辱、糾纏不休的指導。\n" +
        "4. 禁止妨礙行為 — 遮擋其他玩家視線、坐在牌桌上、發出巨大音量、用相機妨礙對局等。\n" +
        "5. 禁止作弊 — 禁止使用修改用戶端，或透過分析・修改世界取得優勢。\n" +
        "6. 觀戰時請體諒他人 — 請勿在看得到手牌的位置對進行中的對局給予建議・指點。\n" +
        "7. 請遵從製作者・工作人員的指引。";
    const string ZhtAgreeNote = "如不同意，請從 VRChat 選單退出實例。";

    // 本文一式の作り分け。並びは KemonoLanguage.LANG_*（0=日本語 / 1=英語 / 2=韓国語 / 3=簡体字 / 4=繁体字）。
    static readonly string[] BodyNames = { "JA_Body", "EN_Body", "KO_Body", "ZHS_Body", "ZHT_Body" };
    static string L(int lang, string ja, string en, string ko, string zhs, string zht)
    {
        return lang == 4 ? zht : lang == 3 ? zhs : lang == 2 ? ko : lang == 1 ? en : ja;
    }

    static TMP_FontAsset font_;
    static int created_;
    static int reused_;

    // ============================================================
    // ビルド
    // ============================================================

    [MenuItem("Tools/Kemono Mahjong/Build Entry Notice")]
    public static void Run()
    {
        created_ = 0;
        reused_ = 0;
        font_ = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
        if (font_ == null)
        {
            Debug.LogWarning("[BuildEntryNotice] " + FontAssetPath + " が見つかりません。" +
                             "先に Tools > Kemono Mahjong > Build Main Menu を実行してください（日本語が豆腐になります）。");
        }

        // .cs を直接置いたスクリプトには U# プログラムアセットが存在しない。
        // 先に生成し、コンパイルが終わってからでないとコンポーネントを追加できない。
        if (EnsureProgramAsset())
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[BuildEntryNotice] U#プログラムアセットを生成しました。" +
                      "コンパイル完了後、もう一度 Build Entry Notice を実行してください。");
            return;
        }

        Vector3 anchorPos;
        Quaternion anchorRot;
        if (!ResolveAnchor(out anchorPos, out anchorRot)) { return; }

        var root = GameObject.Find(RootName);
        if (root == null)
        {
            root = new GameObject(RootName);
            Undo.RegisterCreatedObjectUndo(root, "Build Entry Notice");
            ++created_;
        }
        root.transform.SetPositionAndRotation(anchorPos, anchorRot);
        root.transform.localScale = Vector3.one;

        var canvasRoot = BuildCanvas(root.transform);
        var cageRoot = BuildCage(root.transform);

        // レイヤーは Default（VRChatのUIレーザーは Default で判定される）。
        SetLayerRecursively(root, 0);

        var gate = EnsureGate(root);
        if (gate != null)
        {
            WireGate(gate, canvasRoot, cageRoot);
        }

        EditorSceneManager.MarkSceneDirty(root.scene);
        Debug.Log("[BuildEntryNotice] 完了: 新規 " + created_ + " / 再利用 " + reused_ +
                  "\nanchor=" + anchorPos.ToString("F2") + " rot=" + anchorRot.eulerAngles.ToString("F0") +
                  "\nパネル中心=" + canvasRoot.transform.position.ToString("F2"));
        ReportGround(root.transform);
    }

    // スポーン地点（VRCSceneDescriptor.spawns[0]）の少し前方を anchor にする。
    static bool ResolveAnchor(out Vector3 pos, out Quaternion rot)
    {
        pos = new Vector3(-16.55f, 0f, 0.5f);
        rot = Quaternion.Euler(0f, 90f, 0f);

        var descriptor = Object.FindObjectOfType<VRC.SDK3.Components.VRCSceneDescriptor>();
        if (descriptor == null)
        {
            Debug.LogWarning("[BuildEntryNotice] VRCSceneDescriptor が見つかりません。既定座標で生成します。");
            return true;
        }

        var spawns = descriptor.spawns;
        if (spawns == null || spawns.Length == 0 || spawns[0] == null)
        {
            Debug.LogWarning("[BuildEntryNotice] spawns が空です。既定座標で生成します。");
            return true;
        }

        var spawn = spawns[0];
        rot = Quaternion.Euler(0f, spawn.rotation.eulerAngles.y, 0f);
        pos = spawn.position + (rot * Vector3.forward) * SpawnForwardOffset;
        return true;
    }

    // ============================================================
    // Canvas（暗幕＋パネル）
    // ============================================================

    static GameObject BuildCanvas(Transform root)
    {
        var canvasT = Node(root, "Canvas");
        var canvasGo = canvasT.gameObject;
        canvasT.localPosition = new Vector3(0f, PanelHeight, PanelDistance);
        // Canvas は forward の「裏側」から読める（待ち牌パネルで踏んだのと同じ罠）。
        // root の forward がプレイヤー→パネルの向きなので、Canvas は回さないのが正しい。
        // 180度回すと文字が鏡像になる。
        canvasT.localRotation = Quaternion.identity;
        canvasT.localScale = new Vector3(CanvasScale, CanvasScale, CanvasScale);
        ((RectTransform)canvasT).sizeDelta = CanvasSize;

        var canvas = canvasGo.GetComponent<Canvas>();
        if (canvas == null) { canvas = Undo.AddComponent<Canvas>(canvasGo); }
        canvas.renderMode = RenderMode.WorldSpace;

        if (canvasGo.GetComponent<CanvasScaler>() == null) { Undo.AddComponent<CanvasScaler>(canvasGo); }
        if (canvasGo.GetComponent<GraphicRaycaster>() == null) { Undo.AddComponent<GraphicRaycaster>(canvasGo); }
        if (canvasGo.GetComponent<VRC.SDK3.Components.VRCUiShape>() == null)
        {
            Undo.AddComponent<VRC.SDK3.Components.VRCUiShape>(canvasGo);
        }

        // ClientSim は「VRC_UiShape と同じ GameObject にあるコライダーの内側」でしか
        // UIクリックを通さない（ClientSimInputModule）。実機はコライダー無しでも押せるが、
        // それだと ClientSim で入場ボタンを検証できない（MainMenu はコライダー0のため
        // ClientSim では押せていない）。パネル大の BoxCollider を付けて両方で押せるようにする。
        // 承諾すると Canvas ごと非表示になるので、入場後に通行の邪魔をすることはない。
        var shapeBox = canvasGo.GetComponent<BoxCollider>();
        if (shapeBox == null) { shapeBox = Undo.AddComponent<BoxCollider>(canvasGo); }
        shapeBox.center = Vector3.zero;
        shapeBox.size = new Vector3(PanelSize.x, PanelSize.y, 1f);
        shapeBox.isTrigger = false;

        var dim = Node(canvasT, "Dim");
        Stretch(dim);
        Img(dim, DimColor, false);

        var panel = Node(canvasT, "Panel");
        Rect(panel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, PanelSize);
        Img(panel, PanelColor, true);

        BuildBody(panel, 0);
        BuildBody(panel, 1);
        BuildBody(panel, 2);
        BuildBody(panel, 3);
        BuildBody(panel, 4);
        BuildSharedControls(panel);

        return canvasGo;
    }

    // 言語ごとの本文一式。JA_Body / EN_Body / KO_Body のどれか1つだけを active にする。
    static void BuildBody(Transform panel, int lang)
    {
        var body = Node(panel, BodyNames[lang]);
        Stretch(body);
        body.gameObject.SetActive(lang == 0); // 初期表示は日本語（実行時にも Start で揃える）

        var title = Node(body, "Title");
        Rect(title, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -22f), new Vector2(1900f, 70f));
        Tmp(title, L(lang, JaTitle, EnTitle, KoTitle, ZhsTitle, ZhtTitle), 50f, TextAlignmentOptions.Center, TextColor, false);

        var subtitle = Node(body, "Subtitle");
        Rect(subtitle, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -94f), new Vector2(1900f, 38f));
        Tmp(subtitle, L(lang, JaSubtitle, EnSubtitle, KoSubtitle, ZhsSubtitle, ZhtSubtitle), 26f, TextAlignmentOptions.Center, SubTextColor, false);

        // 開発中のお知らせ（目立たせるため色付きの箱）
        var noticeBox = Node(body, "NoticeBox");
        Rect(noticeBox, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -138f), new Vector2(2020f, 226f));
        Img(noticeBox, NoticeBoxColor, false);
        var noticeHead = Node(noticeBox, "Head");
        Rect(noticeHead, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(26f, -12f), new Vector2(1960f, 40f));
        Tmp(noticeHead, L(lang, JaNoticeHead, EnNoticeHead, KoNoticeHead, ZhsNoticeHead, ZhtNoticeHead), 30f, TextAlignmentOptions.Left, TextColor, false);
        var noticeBody = Node(noticeBox, "Body");
        Rect(noticeBody, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(26f, -56f), new Vector2(1960f, 160f));
        Tmp(noticeBody, L(lang, JaNoticeBody, EnNoticeBody, KoNoticeBody, ZhsNoticeBody, ZhtNoticeBody), 25f, TextAlignmentOptions.TopLeft, TextColor, true);

        // 規約（左列）とルール（右列）
        BuildSection(body, "TermsBox", true,
            L(lang, JaTermsHead, EnTermsHead, KoTermsHead, ZhsTermsHead, ZhtTermsHead),
            L(lang, JaTermsBody, EnTermsBody, KoTermsBody, ZhsTermsBody, ZhtTermsBody));
        BuildSection(body, "RulesBox", false,
            L(lang, JaRulesHead, EnRulesHead, KoRulesHead, ZhsRulesHead, ZhtRulesHead),
            L(lang, JaRulesBody, EnRulesBody, KoRulesBody, ZhsRulesBody, ZhtRulesBody));

        var agreeNote = Node(body, "AgreeNote");
        Rect(agreeNote, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 184f), new Vector2(1900f, 40f));
        Tmp(agreeNote, L(lang, JaAgreeNote, EnAgreeNote, KoAgreeNote, ZhsAgreeNote, ZhtAgreeNote), 24f, TextAlignmentOptions.Center, SubTextColor, false);
    }

    static void BuildSection(Transform body, string name, bool leftColumn, string head, string text)
    {
        var box = Node(body, name);
        Rect(box,
            new Vector2(0.5f, 1f),
            leftColumn ? new Vector2(1f, 1f) : new Vector2(0f, 1f),
            new Vector2(leftColumn ? -20f : 20f, -372f),
            new Vector2(1000f, 668f));
        Img(box, SectionBoxColor, false);

        var headT = Node(box, "Head");
        Rect(headT, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, -12f), new Vector2(950f, 42f));
        Tmp(headT, head, 30f, TextAlignmentOptions.Left, TextColor, false);

        var bodyT = Node(box, "Body");
        Rect(bodyT, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, -58f), new Vector2(950f, 600f));
        Tmp(bodyT, text, 24f, TextAlignmentOptions.TopLeft, TextColor, true);
    }

    // 言語に依存しない共有パーツ（ボタン・フッタ）。JA/EN の切替対象にしない。
    static void BuildSharedControls(Transform panel)
    {
        Btn(panel, "BtnAgree", "同意して入場 / AGREE & PLAY", AgreeColor,
            new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 44f), new Vector2(760f, 118f), 40f);
        Btn(panel, "BtnLang", "English", LangColor,
            new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(40f, 44f), new Vector2(260f, 88f), 28f);

        var footer = Node(panel, "Footer");
        Rect(footer, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-40f, 60f), new Vector2(560f, 50f));
        Tmp(footer, FooterText, 22f, TextAlignmentOptions.Right, SubTextColor, false);
    }

    // ============================================================
    // 不可視の壁
    // ============================================================

    static GameObject BuildCage(Transform root)
    {
        var cage = Node3D(root, "Cage");
        cage.localPosition = Vector3.zero;
        cage.localRotation = Quaternion.identity;

        float cy = CageBottom + CageHeight * 0.5f;
        float midZ = (CageBack + CageFront) * 0.5f;
        float lenZ = CageFront - CageBack;
        float lenX = CageSide * 2f;
        float t = CageThickness;

        Wall(cage, "Wall_Back", new Vector3(0f, cy, CageBack - t * 0.5f), new Vector3(lenX + t * 2f, CageHeight, t));
        Wall(cage, "Wall_Front", new Vector3(0f, cy, CageFront + t * 0.5f), new Vector3(lenX + t * 2f, CageHeight, t));
        Wall(cage, "Wall_Right", new Vector3(CageSide + t * 0.5f, cy, midZ), new Vector3(t, CageHeight, lenZ));
        Wall(cage, "Wall_Left", new Vector3(-CageSide - t * 0.5f, cy, midZ), new Vector3(t, CageHeight, lenZ));

        // 生成直後は非表示。実行時に anchor へ引き寄せてから EntryNoticeGate が出す。
        cage.gameObject.SetActive(false);
        return cage.gameObject;
    }

    static void Wall(Transform parent, string name, Vector3 localPos, Vector3 size)
    {
        var t = Node3D(parent, name);
        t.localPosition = localPos;
        t.localRotation = Quaternion.identity;
        t.localScale = Vector3.one;

        var box = t.GetComponent<BoxCollider>();
        if (box == null) { box = Undo.AddComponent<BoxCollider>(t.gameObject); }
        box.center = Vector3.zero;
        box.size = size;
        box.isTrigger = false;
    }

    // ============================================================
    // EntryNoticeGate の設置と参照の割り当て
    // ============================================================

    // U# プログラムアセットが無ければ生成する。生成したら true（要コンパイル）。
    static bool EnsureProgramAsset()
    {
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(GateScriptPath);
        if (script == null)
        {
            Debug.LogError("[BuildEntryNotice] スクリプトが見つかりません: " + GateScriptPath);
            return false;
        }

        var type = script.GetClass();
        if (type != null && UdonSharpProgramAsset.GetProgramAssetForClass(type) != null) { return false; }
        if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(ProgramAssetPath) != null) { return false; }

        var programAsset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
        programAsset.sourceCsScript = script;
        AssetDatabase.CreateAsset(programAsset, ProgramAssetPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[BuildEntryNotice] U#プログラムアセットを生成: " + ProgramAssetPath);
        return true;
    }

    static EntryNoticeGate EnsureGate(GameObject root)
    {
        var gate = root.GetComponent<EntryNoticeGate>();
        if (gate != null)
        {
            var existing = UdonSharpEditorUtility.GetBackingUdonBehaviour(gate);
            // programSource が null の UdonBehaviour は、プログラムアセット未生成のまま
            // 追加された残骸。掴んだまま CopyProxyToUdon すると
            // 「Value cannot be null. Parameter name: key」で落ちるので作り直す。
            if (existing != null && existing.programSource != null) { return gate; }

            Debug.LogWarning("[BuildEntryNotice] EntryNoticeGate の UdonBehaviour が壊れているため作り直します。");
            Object.DestroyImmediate(gate);
            foreach (var ub in root.GetComponents<UdonBehaviour>())
            {
                if (ub.programSource == null) { Object.DestroyImmediate(ub); }
            }
        }

        var added = (EntryNoticeGate)UdonSharpUndo.AddComponent(root, typeof(EntryNoticeGate));
        if (added == null || UdonSharpEditorUtility.GetBackingUdonBehaviour(added) == null)
        {
            Debug.LogError("[BuildEntryNotice] EntryNoticeGate を追加できません（U#未コンパイル？）。コンパイル後に再実行してください。");
            if (added != null) { Object.DestroyImmediate(added); }
            return null;
        }
        return added;
    }

    static void WireGate(EntryNoticeGate gate, GameObject canvasRoot, GameObject cageRoot)
    {
        var panel = canvasRoot.transform.Find("Panel");
        if (panel == null)
        {
            Debug.LogError("[BuildEntryNotice] Panel が見つかりません。");
            return;
        }

        gate.canvasRoot = canvasRoot;
        gate.cageRoot = cageRoot;
        gate.japaneseBody = panel.Find("JA_Body").gameObject;
        gate.englishBody = panel.Find("EN_Body").gameObject;
        gate.koreanBody = panel.Find("KO_Body").gameObject;
        gate.chineseSimplifiedBody = panel.Find("ZHS_Body").gameObject;
        gate.chineseTraditionalBody = panel.Find("ZHT_Body").gameObject;
        gate.agreeLabel = panel.Find("BtnAgree/Label").GetComponent<TextMeshProUGUI>();
        gate.languageLabel = panel.Find("BtnLang/Label").GetComponent<TextMeshProUGUI>();
        gate.agreeLabelJa = "同意して入場 / AGREE & PLAY";
        gate.agreeLabelEn = "AGREE & PLAY / 同意して入場";
        gate.agreeLabelKo = "동의하고 입장 / AGREE & PLAY";
        gate.agreeLabelZhs = "同意并进入 / AGREE & PLAY";
        gate.agreeLabelZht = "同意並進入 / AGREE & PLAY";
        // 切替ボタンは 日本語→English→한국어→简体中文→繁體中文→日本語 と回る（ラベルは押すと切り替わる先）。
        gate.languageLabelJa = "English";
        gate.languageLabelEn = "한국어";
        gate.languageLabelKo = "简体中文";
        gate.languageLabelZhs = "繁體中文";
        gate.languageLabelZht = "日本語";
        gate.tetherRadius = 1.9f;
        gate.fallDepth = -1.5f;
        gate.initDelaySeconds = 0.25f;
        gate.pollIntervalSeconds = 0.25f;

        // U# は Inspector に見えている proxy の値を Udon 側へ写さないと実行時に反映されない。
        UdonSharpEditorUtility.CopyProxyToUdon(gate);

        var udon = UdonSharpEditorUtility.GetBackingUdonBehaviour(gate);
        if (udon == null)
        {
            Debug.LogError("[BuildEntryNotice] backing UdonBehaviour が取得できません。");
            return;
        }
        WireButton(panel, "BtnAgree", udon, "OnAgree");
        WireButton(panel, "BtnLang", udon, "OnToggleLanguage");
    }

    // onClick の既存 persistent listener を全消ししてから1件だけ登録（冪等）。
    static void WireButton(Transform panel, string name, UdonBehaviour udon, string eventName)
    {
        var t = panel.Find(name);
        if (t == null)
        {
            Debug.LogError("[BuildEntryNotice] ボタンが見つかりません: " + name);
            return;
        }
        var button = t.GetComponent<Button>();
        if (button == null)
        {
            Debug.LogError("[BuildEntryNotice] Button コンポーネントがありません: " + name);
            return;
        }
        while (button.onClick.GetPersistentEventCount() > 0)
        {
            UnityEventTools.RemovePersistentListener(button.onClick, 0);
        }
        UnityEventTools.AddStringPersistentListener(button.onClick, udon.SendCustomEvent, eventName);
        EditorUtility.SetDirty(button);
    }

    // ============================================================
    // 検証
    // ============================================================

    [MenuItem("Tools/Kemono Mahjong/Validate Entry Notice")]
    public static void Validate()
    {
        var root = GameObject.Find(RootName);
        if (root == null)
        {
            Debug.LogError("[ValidateEntryNotice] " + RootName + " がシーンにありません。先に Build Entry Notice を実行してください。");
            return;
        }

        var problems = new List<string>();
        var gate = root.GetComponent<EntryNoticeGate>();
        if (gate == null)
        {
            problems.Add("EntryNoticeGate が付いていない");
        }
        else
        {
            if (UdonSharpEditorUtility.GetBackingUdonBehaviour(gate) == null) { problems.Add("backing UdonBehaviour が無い"); }
            if (gate.canvasRoot == null) { problems.Add("canvasRoot 未割当"); }
            if (gate.cageRoot == null) { problems.Add("cageRoot 未割当"); }
            if (gate.japaneseBody == null) { problems.Add("japaneseBody 未割当"); }
            if (gate.englishBody == null) { problems.Add("englishBody 未割当"); }
            if (gate.koreanBody == null) { problems.Add("koreanBody 未割当"); }
            if (gate.chineseSimplifiedBody == null) { problems.Add("chineseSimplifiedBody 未割当"); }
            if (gate.chineseTraditionalBody == null) { problems.Add("chineseTraditionalBody 未割当"); }
            if (gate.agreeLabel == null) { problems.Add("agreeLabel 未割当"); }
            if (gate.languageLabel == null) { problems.Add("languageLabel 未割当"); }
            if (gate.tetherRadius <= MaxCageRadius())
            {
                problems.Add("tetherRadius(" + gate.tetherRadius.ToString("F2") + ") が壁の対角(" +
                             MaxCageRadius().ToString("F2") + ")以下＝壁際で永久にテレポートし続ける");
            }
        }

        var panel = root.transform.Find("Canvas/Panel");
        if (panel == null)
        {
            problems.Add("Canvas/Panel が無い");
        }
        else
        {
            CheckButton(panel, "BtnAgree", "OnAgree", problems);
            CheckButton(panel, "BtnLang", "OnToggleLanguage", problems);
        }

        problems.AddRange(GroundProblems(root.transform));

        if (problems.Count == 0)
        {
            Debug.Log("[ValidateEntryNotice] OK（参照・配線・足場すべて問題なし）");
        }
        else
        {
            Debug.LogError("[ValidateEntryNotice] 問題 " + problems.Count + " 件:\n - " + string.Join("\n - ", problems.ToArray()));
        }
    }

    static float MaxCageRadius()
    {
        float far = Mathf.Max(Mathf.Abs(CageBack), Mathf.Abs(CageFront));
        return Mathf.Sqrt(far * far + CageSide * CageSide);
    }

    static void CheckButton(Transform panel, string name, string eventName, List<string> problems)
    {
        var t = panel.Find(name);
        if (t == null) { problems.Add(name + " が無い"); return; }
        var button = t.GetComponent<Button>();
        if (button == null) { problems.Add(name + " に Button が無い"); return; }
        if (button.onClick.GetPersistentEventCount() != 1)
        {
            problems.Add(name + " の onClick が " + button.onClick.GetPersistentEventCount() + " 件（1件であるべき）");
            return;
        }
        if (button.onClick.GetPersistentMethodName(0) != "SendCustomEvent")
        {
            problems.Add(name + " の onClick が SendCustomEvent ではない");
        }
    }

    // 壁の内側の四隅に床があるか（桟橋からはみ出していないか）を実地で確認する。
    static List<string> GroundProblems(Transform root)
    {
        var problems = new List<string>();
        foreach (var p in CageCorners(root))
        {
            RaycastHit hit;
            if (!Physics.Raycast(p + Vector3.up * 3f, Vector3.down, out hit, 6f))
            {
                problems.Add("壁の内側 " + p.ToString("F2") + " の下に床が無い（VRの実歩行で落ちる）");
            }
        }
        return problems;
    }

    static void ReportGround(Transform root)
    {
        var problems = GroundProblems(root);
        if (problems.Count > 0)
        {
            Debug.LogWarning("[BuildEntryNotice] 足場の警告:\n - " + string.Join("\n - ", problems.ToArray()));
        }
    }

    static Vector3[] CageCorners(Transform root)
    {
        return new Vector3[]
        {
            root.TransformPoint(new Vector3(-CageSide, 0f, CageBack)),
            root.TransformPoint(new Vector3(CageSide, 0f, CageBack)),
            root.TransformPoint(new Vector3(-CageSide, 0f, CageFront)),
            root.TransformPoint(new Vector3(CageSide, 0f, CageFront)),
        };
    }

    // ============================================================
    // 生成ヘルパ（同名があれば再利用＝重複生成しない）
    // ============================================================

    static Transform Node(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null) { ++reused_; return t; }
        var go = new GameObject(name, typeof(RectTransform));
        Undo.RegisterCreatedObjectUndo(go, "Build Entry Notice");
        go.transform.SetParent(parent, false);
        ++created_;
        return go.transform;
    }

    static Transform Node3D(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null) { ++reused_; return t; }
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Build Entry Notice");
        go.transform.SetParent(parent, false);
        ++created_;
        return go.transform;
    }

    static void Rect(Transform t, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        var rt = (RectTransform)t;
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        rt.localRotation = Quaternion.identity;
        rt.localScale = Vector3.one;
    }

    static void Stretch(Transform t)
    {
        var rt = (RectTransform)t;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        rt.localRotation = Quaternion.identity;
        rt.localScale = Vector3.one;
    }

    static Image Img(Transform t, Color color, bool raycastTarget)
    {
        var image = t.GetComponent<Image>();
        if (image == null) { image = Undo.AddComponent<Image>(t.gameObject); }
        image.color = color;
        image.raycastTarget = raycastTarget;
        return image;
    }

    // 文言の正はこのファイル。既存があっても必ず上書きする。
    static TextMeshProUGUI Tmp(Transform t, string text, float size, TextAlignmentOptions alignment, Color color, bool wrap)
    {
        var tmp = t.GetComponent<TextMeshProUGUI>();
        if (tmp == null) { tmp = Undo.AddComponent<TextMeshProUGUI>(t.gameObject); }
        if (font_ != null) { tmp.font = font_; }
        tmp.text = text;
        tmp.fontSize = size;
        tmp.alignment = alignment;
        tmp.color = color;
        tmp.raycastTarget = false; // ラベルはレーザーを遮らない
        tmp.enableWordWrapping = wrap;
        tmp.overflowMode = TextOverflowModes.Overflow;
        tmp.lineSpacing = wrap ? 6f : 0f;
        tmp.paragraphSpacing = wrap ? 12f : 0f;
        EditorUtility.SetDirty(tmp);
        return tmp;
    }

    static void Btn(Transform parent, string name, string label, Color color,
        Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size, float fontSize)
    {
        var t = Node(parent, name);
        Rect(t, anchor, pivot, pos, size);
        var image = Img(t, color, true);

        var button = t.GetComponent<Button>();
        if (button == null) { button = Undo.AddComponent<Button>(t.gameObject); }
        button.targetGraphic = image;

        var labelT = Node(t, "Label");
        Stretch(labelT);
        Tmp(labelT, label, fontSize, TextAlignmentOptions.Center, TextColor, false);
    }

    static void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform)
        {
            SetLayerRecursively(child.gameObject, layer);
        }
    }
}
#endif
