#if UNITY_EDITOR
// 役種一覧パネル（YakuGuidePanel）の生成・配線・検証。
//
//   Tools > Kemono Mahjong > Build Yaku Guide     … 生成＋配線（何度でも再実行できる）
//   Tools > Kemono Mahjong > Validate Yaku Guide  … 参照欠損・データ不整合の洗い出し
//
// ■ 何を作るか
//   YakuGuidePanel                     … 3Dルート（Rigidbody + VRCPickup + 握り用 BoxCollider）
//    ├ Handle                          … 握り棒（見た目。当たり判定はルート側の BoxCollider）
//    ├ Home                            … 「元に戻す」の帰り先（ルートと同じ位置に置く空オブジェクト）
//    └ Canvas                          … World Space 768x432（VRCUiShape + トリガーBox + Raycaster）
//        └ Panel
//            ├ Side                    … 役種一覧／チュートリアル／点数計算
//            ├ Head                    … 見出し・ページ送り・ローカル役トグル
//            ├ Tabs                    … 一翻〜流局の9タブ
//            ├ List                    … Entry0..3（役名／但し書き／説明／牌）
//            ├ Tutorial                … Block0..7 ＋ ページ送り（1画面1ページの読み物）
//            ├ Score                   … 点数早見表（子／親）＋符計算早見表
//            └ Notice                  … 準備中（未実装セクションの受け皿）
//
// ■ ローカル表示・持ち運び
//   VRCObjectSync を付けない＝掴んで動かしても他人には見えない（各自が自分の1枚を
//   持ち歩ける）。Rigidbody は isKinematic で、落としてもその場に留まる。
//   握りはルートの BoxCollider（Canvas の下）だけで、Canvas 側はトリガーなので
//   プレイヤーを押さないし、UI クリックと掴みが同じ場所で競合しない。
//
// ■ 牌の絵
//   卓と同じ tiles.png を RawImage.uvRect で切り出す。牌1枚ぶんの矩形は
//   KemonoMahjong.prefab の牌メッシュ（絵柄面 submesh）の UV から実測して焼く。
//   手打ちの座標表を持たないので、アトラスを差し替えても再ビルドで追随する。
//
// ■ 文言の正
//   下の YakuTable がこのパネルの正。Mahjong.cs の判定（翻数・食い下がり・
//   ローカル役の成立条件）と 1:1 で対応させてある。片方だけ直すと嘘になる。
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

public static class BuildYakuGuide
{
    const string RootName = "YakuGuidePanel";
    const string ScriptPath = "Assets/Mahjong/Sources/MahjongYakuGuide.cs";
    const string ProgramAssetPath = "Assets/Mahjong/scene_UdonProgramSources/MahjongYakuGuide Udon C# Program Asset.asset";
    const string FontAssetPath = "Assets/Mahjong/Fonts/NotoSansJP-Bold SDF.asset";
    const string TilePrefabPath = "Assets/Mahjong/Prefabas/KemonoMahjong.prefab";
    const string TileTexturePath = "Assets/Mahjong/Textures/tiles.png";

    // --- 置き場所 ---
    // 置き場所。シーンで置き直したらこの定数も合わせること
    // （Build のたびにここへ戻すので、定数を直さないと次のビルドで巻き戻る）。
    // スタッフ専用エリアの壁は z=0.96 で、そこから「奥（−Z）」が立入禁止。
    // プレイヤーは z>0.96 側に立つ。
    static readonly Vector3 HomePosition = new Vector3(6.50f, 2.00f, 1.00f);
    // MainMenu と同じ向き。rotY=180 の Canvas は世界 +Z 側から読める（実測）。
    static readonly Vector3 HomeEuler = new Vector3(0f, 180f, 0f);

    // --- 大きさ ---
    // VRChat の Pickup 層。Player 層との衝突が無視されるので、持ち物が壁にならない。
    const int LayerPickup = 13;

    static readonly Vector2 CanvasSize = new Vector2(768f, 432f);
    const float CanvasScale = 0.0007f;   // 0.538m x 0.302m
    // 画面のまわりに出す縁（掴みしろ）と、筐体の厚み。
    const float Bezel = 0.012f;
    const float BodyDepth = 0.018f;

    // --- レイアウト（Canvasピクセル・左上原点）---
    const float SideW = 126f;
    const float RightX = 138f;
    const float RightW = 620f;
    const float HeadH = 26f;
    const float TabY = 40f;
    const float TabH = 30f;
    const float TabW = 66f;
    const float TabStep = 69f;
    const float ListY = 78f;
    const float ListH = 346f;          // Viewport の高さ（4行ぶんと少し）
    const float ViewportW = 606f;      // スクロールバーのぶん右を空ける
    const float EntryH = 84f;
    const float EntryStep = 86f;
    const float ScrollbarW = 10f;
    const float TileH = 34f;
    const float TileAreaW = 590f;

    // ページ1の牌の下に出す読み方の色（白地に載るのでどれも深め）。字牌は Ink。
    static readonly Color RubyMan = new Color32(192, 57, 43, 255);
    static readonly Color RubyPin = new Color32(31, 95, 169, 255);
    static readonly Color RubySou = new Color32(30, 122, 79, 255);

    // プレビュー用の読み方（実行時は MahjongYakuGuide.RubyTextFor / RubyColorFor が同じことをする）。
    // 牌ID は TileOrder 順：0..26 が数牌（M/P/S の交互）、27..29 が赤五、30..36 が字牌。
    static string RubyTextJa(int id)
    {
        if (id >= 0 && id < 27) { return "" + (id / 3 + 1); }
        if (id >= 27 && id <= 29) { return "5"; }
        switch (id)
        {
            case 30: return "トン";
            case 31: return "ナン";
            case 32: return "シャー";
            case 33: return "ペー";
            case 34: return "ハク";
            case 35: return "ハツ";
            case 36: return "チュン";
        }
        return "";
    }

    static Color RubyColor(int id)
    {
        if (id >= 0 && id < 27)
        {
            int suit = id % 3;
            if (suit == 0) { return RubyMan; }
            if (suit == 1) { return RubyPin; }
            return RubySou;
        }
        if (id == 27) { return RubyMan; }
        if (id == 28) { return RubyPin; }
        if (id == 29) { return RubySou; }
        return StyleMainMenu.Ink;
    }

    // --- チュートリアル（1画面＝1ページ・右下の矢印で送る）---
    // 翻数タブを出さないので、タブの位置から下を丸ごと使える。
    static readonly float TutH = CanvasSize.y - TabY - 8f;   // 384
    // 矢印を右下に置くぶん、本文は右に 60px の余白を空ける。
    const float TutAreaW = 560f;
    const float TutNavX = 568f;
    const float TutNavW = 44f;
    const float TutNavH = 30f;

    // KoreanText の範囲名（同じ英語が別の画面で別の意味になる語を分ける）。
    const string Scope = "YakuGuide";

    static readonly string[] TabJa = { "一翻", "二翻", "三翻", "六翻", "満貫", "倍満", "役満", "ダブル役満", "流局" };
    static readonly string[] TabEn = { "1 Han", "2 Han", "3 Han", "6 Han", "Mangan", "Baiman", "Yakuman", "Double", "Draw" };
    static readonly string[] SectionJa = { "役種一覧", "チュートリアル", "点数計算" };
    static readonly string[] SectionEn = { "Yaku List", "Tutorial", "Scoring" };

    static TMP_FontAsset font_;
    static int created_;
    static int reused_;

    // ============================================================
    // ビルド
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Build Yaku Guide")]
    public static void Run()
    {
        if (EditorApplication.isPlaying)
        {
            Debug.LogError("[BuildYakuGuide] 再生中は実行できません。Play を止めてから実行してください。");
            return;
        }

        created_ = 0;
        reused_ = 0;
        font_ = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
        if (font_ == null)
        {
            Debug.LogWarning("[BuildYakuGuide] " + FontAssetPath + " が見つかりません（日本語が豆腐になります）。");
        }

        if (EnsureProgramAsset())
        {
            UdonSharpProgramAsset.CompileAllCsPrograms();
            Debug.Log("[BuildYakuGuide] U#プログラムアセットを生成しました。" +
                      "コンパイル完了後、もう一度 Build Yaku Guide を実行してください。");
            return;
        }

        var rects = MeasureTileRects();
        if (rects == null) { return; }

        var root = BuildRig();
        if (root == null) { return; }

        var guide = EnsureComponent(root);
        if (guide == null) { return; }

        // 伏せ牌の色は牌背コスメの既定色と同じものを使う。literal を2か所に置くと必ずずれるので、
        // MainMenuController のパレットを正として毎回焼き直す。
        var menu = Object.FindObjectOfType<SuzuFactory.AozoraMahjong.MainMenuController>(true);
        if (menu != null) { guide.tileBackColor = menu.GetTileBackColor(0); }

        Wire(guide, root, rects);
        BuildWorldSettings(guide);

        EditorSceneManager.MarkSceneDirty(root.scene);
        Debug.Log("[BuildYakuGuide] 完了: 新規 " + created_ + " / 再利用 " + reused_ +
                  "\n役 " + YakuTable.Length + " 件 / チュートリアル " + TutorialTable.Length +
                  " ページ / 牌 " + rects.Count + " 種 / 大きさ " +
                  (CanvasSize.x * CanvasScale).ToString("F3") + " x " + (CanvasSize.y * CanvasScale).ToString("F3") + " m" +
                  "\nホーム " + HomePosition.ToString("F2"));
    }

    // ============================================================
    // リグ生成
    // ============================================================
    static GameObject BuildRig()
    {
        var existing = GameObject.Find(RootName);
        Transform rootT;
        if (existing != null) { rootT = existing.transform; ++reused_; }
        else
        {
            var go = new GameObject(RootName);
            Undo.RegisterCreatedObjectUndo(go, "Build Yaku Guide");
            rootT = go.transform;
            ++created_;
        }
        // 置き場所は毎回ここへ戻す。Build は「配置のリセット」も兼ねる
        // （実行時に持ち出された位置はローカルなので、シーンには残らない）。
        rootT.position = HomePosition;
        rootT.rotation = Quaternion.Euler(HomeEuler);
        rootT.localScale = Vector3.one;

        var rootGo = rootT.gameObject;

        // --- 帰り先。ルートを掴んで動かしても動かないよう、シーン直下に別で置く ---
        var home = Node3D(null, RootName + "_Home");
        home.position = HomePosition;
        home.rotation = Quaternion.Euler(HomeEuler);

        // --- 持ち運び（同期しない＝各自のローカル）---
        var rb = rootGo.GetComponent<Rigidbody>();
        if (rb == null) { rb = Undo.AddComponent<Rigidbody>(rootGo); }
        rb.isKinematic = true;   // 落としてもその場に留まる（床まで転がっていかない）
        rb.useGravity = false;

        // 掴み判定はパネル本体そのもの（タブレットの筐体）。
        // 画面の外へ Bezel ぶんはみ出させてあるので、正面から狙っても
        // 画面側の当たり判定（トリガー）に邪魔されずに縁を掴める。
        // 奥行きは画面より「裏側」だけに置く（前に出すと UI のレイを遮る）。
        var grip = rootGo.GetComponent<BoxCollider>();
        if (grip == null) { grip = Undo.AddComponent<BoxCollider>(rootGo); }
        grip.center = new Vector3(0f, 0f, BodyDepth * 0.5f + 0.002f);
        grip.size = new Vector3(
            CanvasSize.x * CanvasScale + Bezel * 2f,
            CanvasSize.y * CanvasScale + Bezel * 2f,
            BodyDepth);
        grip.isTrigger = false;

        var pickup = rootGo.GetComponent<VRC.SDK3.Components.VRCPickup>();
        if (pickup == null) { pickup = Undo.AddComponent<VRC.SDK3.Components.VRCPickup>(rootGo); }
        pickup.pickupable = true;
        pickup.allowManipulationWhenEquipped = false;
        pickup.orientation = VRC.SDKBase.VRC_Pickup.PickupOrientation.Any;
        pickup.proximity = 2.5f;

        // --- 筐体の見た目 ---
        BuildBody(rootT);

        // --- Canvas ---
        BuildCanvas(rootT);

        // 掴む側は Pickup(13)。Default のままだと当たり判定がプレイヤーを押しのける
        //（Pickup 層だけが Player 層との衝突を無視する設定になっている）。
        // Canvas 配下は他のパネルと同じ Default に戻す＝UI レイの扱いを揃える。
        SetLayerRecursively(rootGo, LayerPickup);
        var canvasGo = rootT.Find("Canvas");
        if (canvasGo != null) { SetLayerRecursively(canvasGo.gameObject, 0); }
        return rootGo;
    }

    // タブレットの筐体（画面のうしろの板）。当たり判定はルート側の BoxCollider が持つので、
    // ここは見た目だけ。画面より一回り大きくして、縁（ベゼル）を掴みしろにする。
    static void BuildBody(Transform rootT)
    {
        // 旧・握り棒が残っていたら消す。
        var oldHandle = rootT.Find("Handle");
        if (oldHandle != null) { Undo.DestroyObjectImmediate(oldHandle.gameObject); }

        var t = rootT.Find("Body");
        if (t == null)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Body";
            Undo.RegisterCreatedObjectUndo(go, "Build Yaku Guide");
            go.transform.SetParent(rootT, false);
            ++created_;
            t = go.transform;
        }
        else { ++reused_; }

        // 当たり判定を二重に持つと掴みが不安定になる。判定はルートの1つだけ。
        var col = t.GetComponent<Collider>();
        if (col != null) { Undo.DestroyObjectImmediate(col); }

        t.localPosition = new Vector3(0f, 0f, BodyDepth * 0.5f + 0.002f);
        t.localRotation = Quaternion.identity;
        t.localScale = new Vector3(
            CanvasSize.x * CanvasScale + Bezel * 2f,
            CanvasSize.y * CanvasScale + Bezel * 2f,
            BodyDepth);

        var mr = t.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.sharedMaterial = EnsureHandleMaterial();
        }
    }

    static Material EnsureHandleMaterial()
    {
        const string path = "Assets/Mahjong/Materials/YakuGuideHandle.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat != null) { return mat; }

        // Quest 基準なので Unlit 固定。影も光も要らない棒。
        var shader = Shader.Find("Unlit/Color");
        if (shader == null) { shader = Shader.Find("Unlit/Texture"); }
        mat = new Material(shader);
        mat.color = StyleMainMenu.Ink;
        AssetDatabase.CreateAsset(mat, path);
        AssetDatabase.SaveAssets();
        return mat;
    }

    static void BuildCanvas(Transform rootT)
    {
        var canvasT = Node(rootT, "Canvas");
        canvasT.localPosition = Vector3.zero;
        canvasT.localRotation = Quaternion.identity;
        canvasT.localScale = new Vector3(CanvasScale, CanvasScale, CanvasScale);
        ((RectTransform)canvasT).sizeDelta = CanvasSize;

        var go = canvasT.gameObject;
        var canvas = go.GetComponent<Canvas>();
        if (canvas == null) { canvas = Undo.AddComponent<Canvas>(go); }
        canvas.renderMode = RenderMode.WorldSpace;

        if (go.GetComponent<CanvasScaler>() == null) { Undo.AddComponent<CanvasScaler>(go); }
        if (go.GetComponent<GraphicRaycaster>() == null) { Undo.AddComponent<GraphicRaycaster>(go); }
        if (go.GetComponent<VRC.SDK3.Components.VRCUiShape>() == null)
        {
            Undo.AddComponent<VRC.SDK3.Components.VRCUiShape>(go);
        }

        // ClientSim は VRCUiShape と同じ GameObject のコライダー内でしかクリックを通さない。
        // トリガーにしておかないと、持ち歩くパネルが自分を押しのける壁になる。
        var box = go.GetComponent<BoxCollider>();
        if (box == null) { box = Undo.AddComponent<BoxCollider>(go); }
        box.center = Vector3.zero;
        box.size = new Vector3(CanvasSize.x, CanvasSize.y, 1f);
        box.isTrigger = true;

        var panel = Node(canvasT, "Panel");
        Stretch(panel);
        var panelImg = Img(panel, StyleMainMenu.Paper, true);
        StyleMainMenu.Frame(panelImg, 4f, 0f);

        BuildSide(panel);
        BuildHead(panel);
        BuildTabs(panel);
        BuildList(panel);
        BuildNotice(panel);
        BuildTutorial(panel);
        BuildScore(panel);
    }

    static void BuildSide(Transform panel)
    {
        var side = Node(panel, "Side");
        TopLeft(side, 0f, 0f, SideW, CanvasSize.y);
        Img(side, StyleMainMenu.White, false);

        var brand = Node(side, "Brand");
        TopLeft(brand, 10f, 8f, 106f, 34f);
        var brandTmp = Tmp(brand, "麻雀集会所", 14f, TextAlignmentOptions.TopLeft, StyleMainMenu.Muted, true);
        brandTmp.lineSpacing = -12f;

        var rule = Node(side, "LineTop");
        TopLeft(rule, 10f, 46f, 106f, 2f);
        Img(rule, StyleMainMenu.Rule, false);

        for (int i = 0; i < SectionJa.Length; ++i)
        {
            var b = Btn(side, "BtnSec" + i, SectionJa[i], 9f, 56f + i * 48f, 108f, 42f, 15f);
            EdgeAccent(b, i == 0 ? StyleMainMenu.Teal : StyleMainMenu.Rule);
            // 「チュートリアル」は7文字あって枠に入らない。英語の "Tutorial" も同様なので自動で縮める。
            AutoShrink(b.Find("Label").GetComponent<TextMeshProUGUI>(), 15f, 9f);
        }

        var hint = Node(side, "Hint");
        TopLeft(hint, 10f, 386f, 106f, 36f);
        var hintTmp = Tmp(hint, "つかむと持ち運べます", 10f, TextAlignmentOptions.BottomLeft, StyleMainMenu.Muted, true);
        hintTmp.lineSpacing = -10f;

        // 「元に戻す」ボタンは廃止（要望）。U# 側の OnReturnHome は逃がし道として残してある。
        var oldHome = side.Find("BtnHome");
        if (oldHome != null) { Undo.DestroyObjectImmediate(oldHome.gameObject); }
    }

    static void BuildHead(Transform panel)
    {
        var head = Node(panel, "Head");
        TopLeft(head, RightX, 8f, RightW, HeadH);

        var title = Node(head, "Title");
        TopLeft(title, 0f, 0f, 210f, HeadH);
        Tmp(title, "役種一覧", 19f, TextAlignmentOptions.Left, StyleMainMenu.Ink, false);

        // スクロールはドラッグ／ホイールが主。▲▼ は「つまみにくい」環境むけの1行送り。
        Btn(head, "BtnUp", "▲", 262f, 0f, 26f, HeadH, 13f);
        var count = Node(head, "CountLabel");
        TopLeft(count, 292f, 0f, 90f, HeadH);
        Tmp(count, "全 0 件", 14f, TextAlignmentOptions.Center, StyleMainMenu.Muted, false);
        Btn(head, "BtnDown", "▼", 386f, 0f, 26f, HeadH, 13f);

        // 旧ページ送りの残骸を掃除する（幽霊オブジェクトを残さない）。
        foreach (var stale in new string[] { "BtnPrev", "BtnNext", "PageLabel" })
        {
            var old = head.Find(stale);
            if (old != null) { Undo.DestroyObjectImmediate(old.gameObject); }
        }

        var local = Btn(head, "BtnLocal", "ローカル役 なし", 418f, 0f, 202f, HeadH, 14f);
        EdgeAccent(local, StyleMainMenu.Pink);
    }

    static void BuildTabs(Transform panel)
    {
        var tabs = Node(panel, "Tabs");
        TopLeft(tabs, RightX, TabY, RightW, TabH);

        for (int i = 0; i < TabJa.Length; ++i)
        {
            var t = Btn(tabs, "Tab" + i, TabJa[i], i * TabStep, 0f, TabW, TabH, 13f);
            var label = t.Find("Label").GetComponent<TextMeshProUGUI>();
            // 「ダブル役満」だけ字数が多い。枠いっぱいに広げると罫線に触れるので、
            // ラベルを左右3pxずつ内側に取ってから自動で縮める。
            var lrt = (RectTransform)label.transform;
            lrt.offsetMin = new Vector2(3f, 0f);
            lrt.offsetMax = new Vector2(-3f, 0f);
            AutoShrink(label, 13f, 8f);
        }
    }

    static void BuildList(Transform panel)
    {
        // 旧・非スクロール版（List 直下に Entry）が残っていたら作り直す。
        var stale = panel.Find("List/Entry0");
        if (stale != null) { Undo.DestroyObjectImmediate(panel.Find("List").gameObject); }

        var list = Node(panel, "List");
        TopLeft(list, RightX, ListY, RightW, ListH);

        var viewport = Node(list, "Viewport");
        TopLeft(viewport, 0f, 0f, ViewportW, ListH);
        // ドラッグを拾う下地。役の行はどれも raycastTarget=false なので、
        // ここに Graphic が無いと ScrollRect がドラッグを受け取れない（触っても動かない）。
        Img(viewport, StyleMainMenu.Paper, true);
        // Mask ではなく RectMask2D。矩形で切るだけなので Quest では圧倒的に安い。
        if (viewport.GetComponent<RectMask2D>() == null) { Undo.AddComponent<RectMask2D>(viewport.gameObject); }

        var content = Node(viewport, "Content");
        TopLeft(content, 0f, 0f, ViewportW, ListH);

        var scroll = list.GetComponent<ScrollRect>();
        if (scroll == null) { scroll = Undo.AddComponent<ScrollRect>(list.gameObject); }
        scroll.viewport = (RectTransform)viewport;
        scroll.content = (RectTransform)content;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 40f;
        scroll.inertia = true;
        scroll.decelerationRate = 0.10f;

        // スクロールバー。触れる幅が要るので溝ごとレイキャスト対象にする。
        var track = Node(list, "Scrollbar");
        TopLeft(track, ViewportW + 4f, 0f, ScrollbarW, ListH);
        Img(track, new Color32(226, 222, 206, 255), true);
        var handle = Node(track, "Handle");
        Stretch(handle);
        var handleImg = Img(handle, StyleMainMenu.Teal, true);
        var bar = track.GetComponent<Scrollbar>();
        if (bar == null) { bar = Undo.AddComponent<Scrollbar>(track.gameObject); }
        bar.handleRect = (RectTransform)handle;
        bar.targetGraphic = handleImg;
        bar.direction = Scrollbar.Direction.BottomToTop;
        scroll.verticalScrollbar = bar;
        scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;

        // 枠は SLOT_COUNT 個だけ。中身はスクロール位置に応じて U# が差し替える。
        for (int i = 0; i < MahjongYakuGuide.SLOT_COUNT; ++i)
        {
            var entry = Node(content, "Entry" + i);
            TopLeft(entry, 0f, i * EntryStep, ViewportW, EntryH);

            var headerBar = Node(entry, "HeaderBar");
            TopLeft(headerBar, 0f, 0f, ViewportW, 23f);
            var barImg = Img(headerBar, StyleMainMenu.White, false);
            StyleMainMenu.Frame(barImg, 2f, 0f);

            var name = Node(headerBar, "Name");
            TopLeft(name, 8f, 1f, 370f, 21f);
            Tmp(name, "役名", 15f, TextAlignmentOptions.Left, StyleMainMenu.Ink, false);

            var badge = Node(headerBar, "Badge");
            TopLeft(badge, 316f, 2f, 282f, 20f);
            Tmp(badge, "", 12f, TextAlignmentOptions.Right, StyleMainMenu.Teal, false);

            var desc = Node(entry, "Desc");
            TopLeft(desc, 6f, 25f, 594f, 22f);
            var descTmp = Tmp(desc, "説明", 12f, TextAlignmentOptions.TopLeft, StyleMainMenu.Muted, true);
            descTmp.lineSpacing = -14f;
            descTmp.overflowMode = TextOverflowModes.Truncate;

            var tileRow = Node(entry, "Tiles");
            TopLeft(tileRow, 6f, 48f, TileAreaW, TileH);

            for (int k = 0; k < MahjongYakuGuide.MAX_TILES; ++k)
            {
                var tile = Node(tileRow, "Tile" + k);
                TopLeft(tile, 0f, 0f, TileH * 0.6689f, TileH);
                var raw = tile.GetComponent<RawImage>();
                if (raw == null) { raw = Undo.AddComponent<RawImage>(tile.gameObject); }
                raw.raycastTarget = false;
                raw.color = Color.white;
                TileBorder(raw);
                tile.gameObject.SetActive(false);
            }
        }

        // 「0件」の案内はスクロールの外（切り取られない位置）に置く。
        var empty = Node(panel, "EmptyLabel");
        TopLeft(empty, RightX, ListY + 110f, RightW, 80f);
        Tmp(empty, "", 14f, TextAlignmentOptions.Center, StyleMainMenu.Muted, true);
        empty.gameObject.SetActive(false);
    }

    // チュートリアルの枠。ページの中身（ブロックの位置・文言・牌）は U# が
    // TutorialTable から流し込むので、ここでは「箱を TUT_BLOCKS 個」作るだけ。
    static void BuildTutorial(Transform panel)
    {
        var root = Node(panel, "Tutorial");
        TopLeft(root, RightX, TabY, RightW, TutH);

        for (int i = 0; i < MahjongYakuGuide.TUT_BLOCKS; ++i)
        {
            var block = Node(root, "Block" + i);
            TopLeft(block, 0f, 0f, 200f, 100f);   // 実際の矩形は実行時に置き直す
            var bg = Img(block, StyleMainMenu.White, false);
            StyleMainMenu.Frame(bg, 2f, 0f);

            // 見出しチップ。幅は中身に合わせて uGUI に決めさせる
            // （「萬子」と「捨て牌振り聴」で必要な幅が違う。日英で字幅も変わるので、
            //   数字を表に持たせるより ContentSizeFitter に任せる方が壊れない）。
            var head = Node(block, "Head");
            TopLeft(head, 12f, 8f, 120f, MahjongYakuGuide.TUT_HEAD_H);
            Img(head, StyleMainMenu.Yellow, false);
            var layout = head.GetComponent<HorizontalLayoutGroup>();
            if (layout == null) { layout = Undo.AddComponent<HorizontalLayoutGroup>(head.gameObject); }
            layout.padding = new RectOffset(10, 10, 0, 0);
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;
            var fitter = head.GetComponent<ContentSizeFitter>();
            if (fitter == null) { fitter = Undo.AddComponent<ContentSizeFitter>(head.gameObject); }
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;

            var headLabel = Node(head, "Label");
            TopLeft(headLabel, 0f, 0f, 100f, MahjongYakuGuide.TUT_HEAD_H);
            Tmp(headLabel, "見出し", 13f, TextAlignmentOptions.Center, StyleMainMenu.Ink, false);

            var body = Node(block, "Body");
            TopLeft(body, 12f, 40f, 176f, 40f);
            var bodyTmp = Tmp(body, "本文", 12f, TextAlignmentOptions.TopLeft, StyleMainMenu.Muted, true);
            bodyTmp.lineSpacing = -6f;
            bodyTmp.overflowMode = TextOverflowModes.Overflow;

            var tiles = Node(block, "Tiles");
            TopLeft(tiles, 12f, 60f, 176f, TileH);

            for (int k = 0; k < MahjongYakuGuide.MAX_TILES; ++k)
            {
                var tile = Node(tiles, "Tile" + k);
                TopLeft(tile, 0f, 0f, TileH * 0.6689f, TileH);
                var raw = tile.GetComponent<RawImage>();
                if (raw == null) { raw = Undo.AddComponent<RawImage>(tile.gameObject); }
                raw.raycastTarget = false;
                raw.color = Color.white;
                TileBorder(raw);

                // 読み方のルビ（ページ1だけ使う）。牌の子にして下辺にアンカーするので、
                // 実行時に牌の大きさ・位置が変わっても自動で追従する（位置計算を U# に持たせない）。
                // 左右に 8px ずつはみ出せるのは「シャー」「South」が牌幅（29.4px）より広いため。
                var ruby = Node(tile, "Ruby");
                var rubyRt = (RectTransform)ruby;
                rubyRt.anchorMin = new Vector2(0f, 0f);
                rubyRt.anchorMax = new Vector2(1f, 0f);
                rubyRt.pivot = new Vector2(0.5f, 1f);
                rubyRt.anchoredPosition = new Vector2(0f, -2f);
                rubyRt.sizeDelta = new Vector2(16f, 15f);
                rubyRt.localRotation = Quaternion.identity;
                rubyRt.localScale = Vector3.one;
                Tmp(ruby, "", 10f, TextAlignmentOptions.Top, StyleMainMenu.Ink, false);
                ruby.gameObject.SetActive(false);

                tile.gameObject.SetActive(false);
            }
        }

        // ページ送り。雀魂と同じ右下の縦積み（▲／N/M／▼）。
        // Head の中に置かないのは、BuildHead が BtnPrev/BtnNext/PageLabel という名前の
        // 「旧ページ送りの残骸」を毎回消しているため（同名だと消される）。
        Btn(root, "BtnPagePrev", "▲", TutNavX, 262f, TutNavW, TutNavH, 15f);
        var page = Node(root, "PageLabel");
        TopLeft(page, TutNavX - 8f, 296f, TutNavW + 16f, 26f);
        Tmp(page, "1 / 1", 14f, TextAlignmentOptions.Center, StyleMainMenu.Muted, false);
        Btn(root, "BtnPageNext", "▼", TutNavX, 326f, TutNavW, TutNavH, 15f);

        root.gameObject.SetActive(false);
    }

    static void BuildNotice(Transform panel)
    {
        var notice = Node(panel, "Notice");
        TopLeft(notice, RightX, TabY, RightW, CanvasSize.y - TabY - 8f);

        var label = Node(notice, "NoticeLabel");
        Stretch(label);
        Tmp(label, "準備中です。", 20f, TextAlignmentOptions.Center, StyleMainMenu.Muted, true);
        notice.gameObject.SetActive(false);
    }

    // ============================================================
    // 点数計算（点数早見表・符計算早見表）
    //
    // ■ 数字はここで計算して焼く
    //   表の値は Mahjong.cs の CalculatePoint / Ceil100 と同じ式で出す（下の Sc* 関数）。
    //   手打ちの表を持たないので、点数まわりの実装を直したらビルドし直すだけで表も追従する。
    //   実行時は一切計算しない＝Quest で表を開いてもフレームを落とさない。
    //
    // ■ 「—」のマス
    //   起こり得ない組み合わせは空欄ではなく「—」で潰す（雀魂と同じ）。
    //     ・20符 … 平和ツモ専用。ロンでは 20符 にならない
    //     ・25符 … 七対子専用。2翻確定なので1翻は無い。ツモは門前清自摸和が付いて3翻以上
    //     ・110符1翻ツモ … 110符を作る面子・槓の構成では1翻に収まらない
    //
    // ■ 満貫のマス
    //   符×2^(翻+2) が 2000 以上になると満貫で頭打ち（切り上げ満貫は無い）。
    //   該当するのは「4翻の40符以上」と「3翻の70符以上」の階段状の領域で、
    //   雀魂と同じく1枚の「満貫」ブロックにまとめる。
    // ============================================================
    static readonly string[] ScoreTabJa = { "点数早見表（子）", "点数早見表（親）", "符計算早見表" };
    static readonly string[] ScoreTabEn = { "Scores (Non-dealer)", "Scores (Dealer)", "Fu Table" };

    static readonly int[] ScFuRows = { 20, 25, 30, 40, 50, 60, 70, 80, 90, 100, 110 };

    const float ScTabH = 28f;
    const float ScTabW = 196f;
    const float ScTabStep = 202f;
    const float ScPageY = 34f;
    static readonly float ScPageH = TutH - ScPageY;   // 350

    const float ScFuW = 52f;
    const float ScHanW = 103f;
    const float ScHeadH = 24f;
    const float ScRowH = 26f;
    const float ScGridW = ScFuW + 4f * ScHanW;        // 464
    const float ScGridH = ScHeadH + 11f * ScRowH;     // 310
    const float ScSideX = 474f;
    const float ScSideW = 146f;
    const float ScSideH = 57f;
    const float ScSideStep = 63f;
    const float ScTitleH = 22f;

    static readonly Color ScHeadBg = StyleMainMenu.Ink;
    static readonly Color ScRowA = StyleMainMenu.White;
    static readonly Color ScRowB = new Color32(243, 239, 223, 255);
    static readonly Color ScFuBg = new Color32(236, 231, 211, 255);
    static readonly Color ScManganBg = new Color32(228, 246, 246, 255);

    // 日英で文字が変わるラベルだけを集める。数字のマスは登録しない
    // （実行時に書き換えるラベルが減るほど、言語を切り替えたときの組み直しが軽い）。
    static readonly List<TextMeshProUGUI> scoreTexts_ = new List<TextMeshProUGUI>();
    static readonly List<string> scoreJa_ = new List<string>();
    static readonly List<string> scoreEn_ = new List<string>();

    // --- Mahjong.cs と同じ計算 ---
    static int ScCeil100(int x) { return x % 100 == 0 ? x : (x / 100 + 1) * 100; }

    /// <summary>子のロン総取り点（丸め前）。CalculatePoint の非役満経路そのまま。</summary>
    static int ScChildRaw(int fu, int han)
    {
        if (han >= 13) { return 32000; }
        if (han >= 11) { return 24000; }
        if (han >= 8) { return 16000; }
        if (han >= 6) { return 12000; }
        if (han == 5) { return 8000; }
        return Mathf.Min(8000, fu * 4 * (1 << (han + 2)));
    }

    /// <summary>ロンでもらう点数（親は1.5倍・100点単位へ切り上げ）。</summary>
    static int ScTotal(int fu, int han, bool oya)
    {
        int p = ScChildRaw(fu, han);
        if (oya) { p = p * 3 / 2; }
        return ScCeil100(p);
    }

    /// <summary>ツモで一家あたりが払う点数。子は「子・親」の2つ。</summary>
    static string ScTsumoText(int fu, int han, bool oya)
    {
        int p = ScTotal(fu, han, oya);
        if (oya) { return ScCeil100(p / 3).ToString(); }
        return ScCeil100(p / 4) + "・" + ScCeil100(p / 2);
    }

    /// <summary>満貫で頭打ちになるマス（＝満貫ブロックへ吸収される）。</summary>
    static bool ScIsMangan(int fu, int han) { return fu * (1 << (han + 2)) >= 2000; }

    static void BuildScore(Transform panel)
    {
        scoreTexts_.Clear();
        scoreJa_.Clear();
        scoreEn_.Clear();

        var root = Node(panel, "Score");
        TopLeft(root, RightX, TabY, RightW, TutH);

        for (int i = 0; i < ScoreTabJa.Length; ++i)
        {
            var t = Btn(root, "ScoreTab" + i, ScoreTabJa[i], 10f + i * ScTabStep, 0f, ScTabW, ScTabH, 14f);
            // 「点数早見表（子）」も英語の "Scores (Non-dealer)" も枠より長い。自動で縮める。
            AutoShrink(t.Find("Label").GetComponent<TextMeshProUGUI>(), 14f, 9f);
        }

        BuildScoreTable(root, "PageKo", false);
        BuildScoreTable(root, "PageOya", true);
        BuildFuTable(root);

        root.gameObject.SetActive(false);
    }

    // ------------------------------------------------------------
    // 点数早見表（子／親）
    // ------------------------------------------------------------
    static void BuildScoreTable(Transform root, string name, bool oya)
    {
        var page = Node(root, name);
        TopLeft(page, 0f, ScPageY, RightW, ScPageH);

        var grid = ScNode(page, "Grid", 0f, 0f, ScGridW, ScGridH);
        var gridImg = Img(grid, StyleMainMenu.White, false);
        StyleMainMenu.Frame(gridImg, 2f, 0f);

        // 地：符の列を一段濃くして、横に目で追えるようにする。
        ScRect(grid, "FuBg", 0f, ScHeadH, ScFuW, ScGridH - ScHeadH, ScFuBg);
        for (int r = 0; r < ScFuRows.Length; ++r)
        {
            ScRect(grid, "RowBg" + r, ScFuW, ScHeadH + r * ScRowH, ScGridW - ScFuW, ScRowH,
                   (r % 2) == 0 ? ScRowA : ScRowB);
        }

        // 見出し帯
        ScRect(grid, "HeadBg", 0f, 0f, ScGridW, ScHeadH, ScHeadBg);
        ScLabel(grid, "HeadFu", 0f, 0f, ScFuW, ScHeadH, "符", "Fu", 12f,
                StyleMainMenu.Yellow, false, TextAlignmentOptions.Center);
        for (int c = 0; c < 4; ++c)
        {
            ScLabel(grid, "HeadHan" + c, ScFuW + c * ScHanW, 0f, ScHanW, ScHeadH,
                    (c + 1) + "翻", (c + 1) + " Han", 12f,
                    StyleMainMenu.Yellow, false, TextAlignmentOptions.Center);
        }

        // 罫線
        for (int c = 0; c <= 3; ++c)
        {
            ScRect(grid, "VLine" + c, ScFuW + c * ScHanW, ScHeadH, 1f, ScGridH - ScHeadH, StyleMainMenu.Rule);
        }
        for (int r = 1; r < ScFuRows.Length; ++r)
        {
            ScRect(grid, "HLine" + r, 0f, ScHeadH + r * ScRowH, ScGridW, 1f, StyleMainMenu.Rule);
        }

        // 符の見出しとマス
        for (int r = 0; r < ScFuRows.Length; ++r)
        {
            int fu = ScFuRows[r];
            float y = ScHeadH + r * ScRowH;

            ScLabel(grid, "Fu" + r, 0f, y, ScFuW, ScRowH, fu.ToString(), null, 12f,
                    StyleMainMenu.Ink, false, TextAlignmentOptions.Center);

            for (int c = 0; c < 4; ++c)
            {
                int han = c + 1;
                var cell = ScNode(grid, "Cell" + r + "_" + c, ScFuW + c * ScHanW, y, ScHanW, ScRowH);
                bool mangan = ScIsMangan(fu, han);
                cell.gameObject.SetActive(!mangan);
                if (mangan) { continue; }

                // 20符はロンで出ない（平和ツモ専用）。25符1翻・20符1翻は形として存在しない。
                bool ronDash = fu == 20 || (han == 1 && fu == 25);
                // 25符は七対子＝ツモなら門前清自摸和が付いて3翻以上になる。
                bool tsumoDash = (han == 1 && (fu == 20 || fu == 25 || fu == 110))
                                 || (fu == 25 && han <= 2);

                ScLabel(cell, "Ron", 0f, 1f, ScHanW, 15f,
                        ronDash ? "—" : ScTotal(fu, han, oya).ToString(), null, 13.5f,
                        StyleMainMenu.Ink, false, TextAlignmentOptions.Center);
                ScLabel(cell, "Tsumo", 0f, 15f, ScHanW, 11f,
                        tsumoDash ? "—" : ScTsumoText(fu, han, oya), null, 9f,
                        StyleMainMenu.Muted, false, TextAlignmentOptions.Center);
            }
        }

        // 満貫ブロック（階段状の領域を2枚の矩形で塗る）。
        int firstMangan4 = 0;   // 4翻で満貫になる最初の行
        int firstMangan3 = 0;   // 3翻で満貫になる最初の行
        for (int r = 0; r < ScFuRows.Length; ++r)
        {
            if (ScIsMangan(ScFuRows[r], 4)) { firstMangan4 = r; break; }
        }
        for (int r = 0; r < ScFuRows.Length; ++r)
        {
            if (ScIsMangan(ScFuRows[r], 3)) { firstMangan3 = r; break; }
        }

        float topA = ScHeadH + firstMangan4 * ScRowH;
        ScRect(grid, "ManganTop", ScFuW + 3f * ScHanW, topA, ScHanW,
               ScHeadH + firstMangan3 * ScRowH - topA, ScManganBg);

        float topB = ScHeadH + firstMangan3 * ScRowH;
        var block = ScNode(grid, "ManganBlock", ScFuW + 2f * ScHanW, topB, ScHanW * 2f, ScGridH - topB);
        Img(block, ScManganBg, false);
        float bh = ScGridH - topB;
        ScLabel(block, "Name", 0f, bh * 0.5f - 34f, ScHanW * 2f, 18f, "満貫", "Mangan", 13f,
                StyleMainMenu.Pink, false, TextAlignmentOptions.Center);
        ScLabel(block, "Value", 0f, bh * 0.5f - 14f, ScHanW * 2f, 22f,
                ScTotal(30, 5, oya).ToString(), null, 16f,
                StyleMainMenu.Ink, false, TextAlignmentOptions.Center);
        ScLabel(block, "Sub", 0f, bh * 0.5f + 10f, ScHanW * 2f, 14f,
                ScTsumoText(30, 5, oya), null, 10f,
                StyleMainMenu.Muted, false, TextAlignmentOptions.Center);

        // 5翻以上（満貫〜役満）。13翻以上は数え役満＝32000点固定（真の役満と同額）。
        string[] rankJa = { "満貫", "跳満", "倍満", "三倍満", "役満" };
        string[] rankEn = { "Mangan", "Haneman", "Baiman", "Sanbaiman", "Yakuman" };
        string[] headJa = { "5翻", "6〜7翻", "8〜10翻", "11〜12翻", "13翻以上" };
        string[] headEn = { "5 Han", "6-7 Han", "8-10 Han", "11-12 Han", "13+ Han" };
        int[] rankHan = { 5, 6, 8, 11, 13 };

        for (int k = 0; k < rankHan.Length; ++k)
        {
            var box = ScNode(page, "Rank" + k, ScSideX, k * ScSideStep, ScSideW, ScSideH);
            var boxImg = Img(box, StyleMainMenu.White, false);
            StyleMainMenu.Frame(boxImg, 2f, 0f);

            ScRect(box, "HeadBg", 0f, 0f, ScSideW, 15f, ScHeadBg);
            ScLabel(box.Find("HeadBg"), "Label", 0f, 0f, ScSideW, 15f, headJa[k], headEn[k], 10f,
                    StyleMainMenu.Yellow, false, TextAlignmentOptions.Center);
            ScLabel(box, "Name", 0f, 16f, ScSideW, 15f, rankJa[k], rankEn[k], 12.5f,
                    StyleMainMenu.Pink, false, TextAlignmentOptions.Center);
            ScLabel(box, "Value", 0f, 29f, ScSideW, 18f,
                    ScTotal(30, rankHan[k], oya).ToString(), null, 15f,
                    StyleMainMenu.Ink, false, TextAlignmentOptions.Center);
            ScLabel(box, "Sub", 0f, 45f, ScSideW, 12f,
                    ScTsumoText(30, rankHan[k], oya), null, 9.5f,
                    StyleMainMenu.Muted, false, TextAlignmentOptions.Center);
        }

        // 読み方。三麻はツモの払い手が1人減る（ツモ損あり＝既定）。
        string noteJa = oya
            ? "各マスの上段はロン和了で放銃者からもらう点数、下段はツモ和了で【子】がそれぞれ払う点数。"
            : "各マスの上段はロン和了で放銃者からもらう点数、下段はツモ和了で【子・親】がそれぞれ払う点数。";
        noteJa += "\n三人打ちは払う子が1人少ないぶんツモの受取が減る（ツモ損あり）。フリー対局で「ツモ損なし」にすると不足分が補われる。";
        string noteEn = oya
            ? "Top line: what the discarder pays on a ron. Bottom line: what each non-dealer pays on a tsumo."
            : "Top line: what the discarder pays on a ron. Bottom line: what each [non-dealer / dealer] pays on a tsumo.";
        noteEn += "\nIn 3-player there is one fewer non-dealer, so a tsumo pays less unless \"No tsumo loss\" is on in a free match.";

        var note = ScLabel(page, "Note", 0f, ScGridH + 4f, RightW, ScPageH - ScGridH - 4f,
                           noteJa, noteEn, 9.5f,
                           StyleMainMenu.Muted, true, TextAlignmentOptions.TopLeft);
        note.lineSpacing = -10f;

        page.gameObject.SetActive(false);
    }

    // ------------------------------------------------------------
    // 符計算早見表
    //
    // 数字は Mahjong.cs の符計算（CheckHand 内）と同じ。門前ロンの +10 は
    // 実装では「基本符 20 のかわりに 30 から始める」形になっているが、意味は同じ。
    // ------------------------------------------------------------
    static void BuildFuTable(Transform root)
    {
        var page = Node(root, "PageFu");
        TopLeft(page, 0f, ScPageY, RightW, ScPageH);

        const float colY = 0f;
        const float colH = 248f;

        // --- 副底 ---
        var basis = ScBox(page, "Basis", 0f, colY, 72f, colH, "副底", "Base");
        ScLabel(basis, "Cap", 0f, 96f, 72f, 16f, "基本符", "Base fu", 10f,
                StyleMainMenu.Muted, false, TextAlignmentOptions.Center);
        ScLabel(basis, "Value", 0f, 112f, 72f, 28f, "+20", null, 18f,
                StyleMainMenu.Ink, false, TextAlignmentOptions.Center);

        ScLabel(page, "Plus0", 72f, colY, 18f, colH, "＋", null, 16f,
                StyleMainMenu.Muted, false, TextAlignmentOptions.Center);

        // --- 面子 ---
        var sets = ScBox(page, "Sets", 90f, colY, 210f, colH, "面子（メンツ）", "Sets");
        ScLabel(sets, "ColA", 70f, 26f, 70f, 13f, "中張牌", "Simples", 10f,
                StyleMainMenu.Teal, false, TextAlignmentOptions.Center);
        ScLabel(sets, "ColB", 140f, 26f, 70f, 13f, "幺九牌", "Terminals", 10f,
                StyleMainMenu.Teal, false, TextAlignmentOptions.Center);

        // 見本の牌（中張＝五筒／幺九＝一萬）。uvRect は Wire で焼く。
        MakeFuTile(sets, "TileSimple", 70f + (70f - 24f * 0.6689f) * 0.5f, 40f, 24f);
        MakeFuTile(sets, "TileTerminal", 140f + (70f - 24f * 0.6689f) * 0.5f, 40f, 24f);

        ScRect(sets, "VLineA", 70f, ScTitleH, 1f, colH - ScTitleH, StyleMainMenu.Rule);
        ScRect(sets, "VLineB", 140f, ScTitleH, 1f, colH - ScTitleH, StyleMainMenu.Rule);
        ScRect(sets, "HLineHead", 0f, 66f, 210f, 1f, StyleMainMenu.Rule);

        string[] setJa = { "明刻", "暗刻", "明槓", "暗槓" };
        string[] setEn = { "Open triplet", "Closed triplet", "Open kan", "Closed kan" };
        int[] setSimple = { 2, 4, 8, 16 };
        int[] setTerminal = { 4, 8, 16, 32 };

        for (int i = 0; i < setJa.Length; ++i)
        {
            float y = 68f + i * 44f;
            if (i > 0) { ScRect(sets, "HLine" + i, 0f, y, 210f, 1f, StyleMainMenu.Rule); }
            ScLabel(sets, "SetName" + i, 4f, y, 62f, 44f, setJa[i], setEn[i], 12f,
                    StyleMainMenu.Ink, true, TextAlignmentOptions.Center);
            ScLabel(sets, "SetSimple" + i, 70f, y, 70f, 44f, "+" + setSimple[i], null, 14f,
                    StyleMainMenu.Ink, false, TextAlignmentOptions.Center);
            ScLabel(sets, "SetTerminal" + i, 140f, y, 70f, 44f, "+" + setTerminal[i], null, 14f,
                    StyleMainMenu.Ink, false, TextAlignmentOptions.Center);
        }

        ScLabel(page, "Plus1", 300f, colY, 18f, colH, "＋", null, 16f,
                StyleMainMenu.Muted, false, TextAlignmentOptions.Center);

        // --- 雀頭 ---
        var pair = ScBox(page, "Pair", 318f, colY, 90f, colH, "雀頭（アタマ）", "Pair");
        string[] pairJa = { "自風牌 +2", "場風牌 +2", "連風牌 +4", "三元牌 +2" };
        string[] pairEn = { "Seat wind +2", "Round wind +2", "Double wind +4", "Dragons +2" };
        for (int i = 0; i < pairJa.Length; ++i)
        {
            ScLabel(pair, "Pair" + i, 4f, 40f + i * 46f, 82f, 44f, pairJa[i], pairEn[i], 11.5f,
                    StyleMainMenu.Ink, true, TextAlignmentOptions.Center);
        }

        ScLabel(page, "Plus2", 408f, colY, 18f, colH, "＋", null, 16f,
                StyleMainMenu.Muted, false, TextAlignmentOptions.Center);

        // --- 待ちの形 ---
        var wait = ScBox(page, "Wait", 426f, colY, 90f, colH, "待ちの形", "Wait");
        string[] waitJa = { "単騎待ち +2", "嵌張待ち +2", "辺張待ち +2" };
        string[] waitEn = { "Pair wait +2", "Closed wait +2", "Edge wait +2" };
        for (int i = 0; i < waitJa.Length; ++i)
        {
            ScLabel(wait, "Wait" + i, 4f, 40f + i * 46f, 82f, 44f, waitJa[i], waitEn[i], 11.5f,
                    StyleMainMenu.Ink, true, TextAlignmentOptions.Center);
        }
        var waitNote = ScLabel(wait, "Note", 4f, 182f, 82f, 60f,
                               "両面待ち・双碰待ちは\n0符（加算なし）", "Two-sided and dual-pair\nwaits give 0 fu.", 9f,
                               StyleMainMenu.Muted, true, TextAlignmentOptions.Top);
        waitNote.lineSpacing = -10f;

        ScLabel(page, "Plus3", 516f, colY, 18f, colH, "＋", null, 16f,
                StyleMainMenu.Muted, false, TextAlignmentOptions.Center);

        // --- アガリ方 ---
        var win = ScBox(page, "Win", 534f, colY, 86f, colH, "アガリ方", "Win type");
        ScLabel(win, "Tsumo", 4f, 40f, 78f, 34f, "ツモ +2", "Tsumo +2", 12f,
                StyleMainMenu.Ink, true, TextAlignmentOptions.Center);
        var tsumoNote = ScLabel(win, "TsumoNote", 4f, 74f, 78f, 44f,
                                "平和が\nつかないとき", "Unless pinfu\napplies.", 9.5f,
                                StyleMainMenu.Muted, true, TextAlignmentOptions.Top);
        tsumoNote.lineSpacing = -10f;
        ScLabel(win, "Ron", 4f, 130f, 78f, 34f, "門前ロン +10", "Closed ron +10", 12f,
                StyleMainMenu.Ink, true, TextAlignmentOptions.Center);
        var ronNote = ScLabel(win, "RonNote", 4f, 164f, 78f, 44f,
                              "鳴いていない\n手でロンしたとき", "Only if the hand\nis closed.", 9.5f,
                              StyleMainMenu.Muted, true, TextAlignmentOptions.Top);
        ronNote.lineSpacing = -10f;

        // --- 切り上げと例外 ---
        ScLabel(page, "RoundUp", 0f, 254f, RightW, 18f,
                "合計した符は1の位を切り上げて最終結果とする（例：合計32符 → 40符）",
                "Round the total up to the next 10 fu (e.g. 32 fu becomes 40 fu).", 11f,
                StyleMainMenu.Ink, false, TextAlignmentOptions.Center);

        var exBox = ScNode(page, "Exceptions", 0f, 276f, RightW, 74f);
        var exImg = Img(exBox, StyleMainMenu.White, false);
        StyleMainMenu.Frame(exImg, 2f, 0f);

        string[] exJa =
        {
            "例外1：平和でツモ和了したときは一律20符（門前ロンの平和は30符）。",
            "例外2：七対子は一律25符。",
            "例外3：例外1・2を除き、最終結果が30符未満の場合は一律30符（喰い平和など）。",
        };
        string[] exEn =
        {
            "Exception 1: A pinfu tsumo is always 20 fu (a closed pinfu ron is 30 fu).",
            "Exception 2: Seven pairs is always 25 fu.",
            "Exception 3: Otherwise, anything below 30 fu counts as 30 fu (e.g. an open pinfu-shaped hand).",
        };
        for (int i = 0; i < exJa.Length; ++i)
        {
            ScLabel(exBox, "Ex" + i, 10f, 6f + i * 21f, RightW - 20f, 20f, exJa[i], exEn[i], 10.5f,
                    StyleMainMenu.Muted, false, TextAlignmentOptions.Left);
        }

        page.gameObject.SetActive(false);
    }

    static void MakeFuTile(Transform parent, string name, float x, float y, float h)
    {
        var t = ScNode(parent, name, x, y, h * 0.6689f, h);
        var raw = t.GetComponent<RawImage>();
        if (raw == null) { raw = Undo.AddComponent<RawImage>(t.gameObject); }
        raw.raycastTarget = false;
        raw.color = Color.white;
        TileBorder(raw);
    }

    // ------------------------------------------------------------
    // 点数計算の生成ヘルパ
    // ------------------------------------------------------------
    static Transform ScNode(Transform parent, string name, float x, float y, float w, float h)
    {
        var t = Node(parent, name);
        TopLeft(t, x, y, w, h);
        return t;
    }

    static void ScRect(Transform parent, string name, float x, float y, float w, float h, Color color)
    {
        var t = ScNode(parent, name, x, y, w, h);
        Img(t, color, false);
    }

    static Transform ScBox(Transform parent, string name, float x, float y, float w, float h,
                           string titleJa, string titleEn)
    {
        var box = ScNode(parent, name, x, y, w, h);
        var img = Img(box, StyleMainMenu.White, false);
        StyleMainMenu.Frame(img, 2f, 0f);

        ScRect(box, "TitleBar", 0f, 0f, w, ScTitleH, ScHeadBg);
        var label = ScLabel(box.Find("TitleBar"), "Label", 0f, 0f, w, ScTitleH, titleJa, titleEn, 11f,
                            StyleMainMenu.Yellow, false, TextAlignmentOptions.Center);
        AutoShrink(label, 11f, 7f);
        return box;
    }

    static TextMeshProUGUI ScLabel(Transform parent, string name, float x, float y, float w, float h,
                                   string ja, string en, float size, Color color, bool wrap,
                                   TextAlignmentOptions align)
    {
        var t = ScNode(parent, name, x, y, w, h);
        var tmp = Tmp(t, ja, size, align, color, wrap);
        if (!string.IsNullOrEmpty(en) && en != ja)
        {
            scoreTexts_.Add(tmp);
            scoreJa_.Add(ja);
            scoreEn_.Add(en);
        }
        return tmp;
    }

    // ------------------------------------------------------------
    // 点数計算：参照の配線
    // ------------------------------------------------------------
    static void WireScore(MahjongYakuGuide guide, Transform panel, Dictionary<string, Rect> rects)
    {
        var root = panel.Find("Score");
        if (root == null) { Debug.LogError("[BuildYakuGuide] Score が見つかりません。"); return; }

        guide.scoreRoot = root.gameObject;
        guide.scorePages = new GameObject[]
        {
            root.Find("PageKo").gameObject,
            root.Find("PageOya").gameObject,
            root.Find("PageFu").gameObject,
        };

        var tabImages = new Image[ScoreTabJa.Length];
        var tabLabels = new TextMeshProUGUI[ScoreTabJa.Length];
        for (int i = 0; i < ScoreTabJa.Length; ++i)
        {
            var t = root.Find("ScoreTab" + i);
            tabImages[i] = t.GetComponent<Image>();
            tabLabels[i] = t.Find("Label").GetComponent<TextMeshProUGUI>();
        }
        guide.scoreTabImages = tabImages;
        guide.scoreTabLabels = tabLabels;
        guide.scoreTabNameJa = ScoreTabJa;
        guide.scoreTabNameEn = ScoreTabEn;
        guide.scoreTabNameKo = KoreanText.KoArray(ScoreTabEn, null, Scope);
        guide.scoreTabNameZhs = ChineseText.ZhArray(ScoreTabEn, false, null, Scope);
        guide.scoreTabNameZht = ChineseText.ZhArray(ScoreTabEn, true, null, Scope);

        guide.scoreTexts = scoreTexts_.ToArray();
        guide.scoreTextJa = scoreJa_.ToArray();
        guide.scoreTextEn = scoreEn_.ToArray();
        guide.scoreTextKo = KoreanText.KoArray(guide.scoreTextEn, null, Scope);
        guide.scoreTextZhs = ChineseText.ZhArray(guide.scoreTextEn, false, null, Scope);
        guide.scoreTextZht = ChineseText.ZhArray(guide.scoreTextEn, true, null, Scope);

        // 符計算の見本牌。牌の矩形は一覧と同じ実測値を使う。
        var tex = AssetDatabase.LoadAssetAtPath<Texture>(TileTexturePath);
        WireFuTile(root.Find("PageFu/Sets/TileSimple"), tex, rects, "P5");
        WireFuTile(root.Find("PageFu/Sets/TileTerminal"), tex, rects, "M1");

        // 小タブの初期状態は「点数早見表（子）」。Score 自体は畳んであるので画面には出ない
        // （エディタで見た目を確かめるときは Preview Yaku Guide Score を使う）。
        guide.scorePages[0].SetActive(true);
        guide.scorePages[1].SetActive(false);
        guide.scorePages[2].SetActive(false);
        tabImages[0].color = StyleMainMenu.Yellow;
        for (int i = 1; i < tabImages.Length; ++i) { tabImages[i].color = StyleMainMenu.White; }
    }

    static void WireFuTile(Transform t, Texture tex, Dictionary<string, Rect> rects, string tile)
    {
        if (t == null) { Debug.LogError("[BuildYakuGuide] 符計算の見本牌がありません: " + tile); return; }
        var raw = t.GetComponent<RawImage>();
        if (raw == null) { return; }
        Rect r;
        if (!rects.TryGetValue(tile, out r)) { Debug.LogError("[BuildYakuGuide] 牌の実測がありません: " + tile); return; }
        raw.texture = tex;
        raw.uvRect = r;
        EditorUtility.SetDirty(raw);
    }

    // エディタで点数計算の見た目を確かめる（押すたびに次のページへ送る）。
    // 実行時は U# の Refresh() が同じ出し分けをするので、ここで焼いた状態に意味は無い。
    [MenuItem("Tools/Kemono Mahjong/Preview Yaku Guide Score")]
    public static void PreviewScoreMenu()
    {
        var rootGo = GameObject.Find(RootName);
        if (rootGo == null) { Debug.LogError("[BuildYakuGuide] 先に Build Yaku Guide を実行してください。"); return; }
        var guide = rootGo.GetComponent<MahjongYakuGuide>();
        if (guide == null || guide.scoreRoot == null || guide.scorePages == null || guide.scorePages.Length == 0)
        {
            Debug.LogError("[BuildYakuGuide] 点数計算が未生成です。Build Yaku Guide を実行してください。");
            return;
        }

        int page = EditorPrefs.GetInt("KemonoMahjong.YakuGuideScorePreviewPage", -1) + 1;
        if (page >= guide.scorePages.Length) { page = 0; }
        EditorPrefs.SetInt("KemonoMahjong.YakuGuideScorePreviewPage", page);

        if (guide.listRoot != null) { guide.listRoot.SetActive(false); }
        if (guide.tutorialRoot != null) { guide.tutorialRoot.SetActive(false); }
        if (guide.noticeRoot != null) { guide.noticeRoot.SetActive(false); }
        if (guide.emptyLabel != null) { guide.emptyLabel.gameObject.SetActive(false); }
        if (guide.listOnlyObjects != null)
        {
            foreach (var go in guide.listOnlyObjects) { if (go != null) { go.SetActive(false); } }
        }
        guide.scoreRoot.SetActive(true);

        for (int i = 0; i < guide.scorePages.Length; ++i)
        {
            if (guide.scorePages[i] != null) { guide.scorePages[i].SetActive(i == page); }
            if (guide.scoreTabImages != null && i < guide.scoreTabImages.Length && guide.scoreTabImages[i] != null)
            {
                guide.scoreTabImages[i].color = i == page ? StyleMainMenu.Yellow : StyleMainMenu.White;
            }
        }
        for (int i = 0; i < guide.sectionImages.Length; ++i)
        {
            guide.sectionImages[i].color = i == MahjongYakuGuide.SECTION_SCORE
                ? StyleMainMenu.Yellow : StyleMainMenu.White;
        }
        if (guide.titleLabel != null) { guide.titleLabel.text = SectionJa[MahjongYakuGuide.SECTION_SCORE]; }

        EditorUtility.SetDirty(guide);
        EditorSceneManager.MarkSceneDirty(guide.gameObject.scene);
        Debug.Log("[BuildYakuGuide] 点数計算 " + ScoreTabJa[page] + " を表示しました。もう一度押すと次のページへ進みます。");
    }

    // 雀魂の点数早見表と突き合わせる。実装式（CalculatePoint）を触ったときに、
    // 表だけが静かにズレるのを防ぐための固定値チェック。
    static void CheckScore(List<string> problems, int fu, int han, bool oya, int ron, string tsumo)
    {
        string at = fu + "符" + han + "翻" + (oya ? "（親）" : "（子）");
        if (ron > 0 && ScTotal(fu, han, oya) != ron)
        {
            problems.Add("点数表がズレている " + at + " ロン " + ScTotal(fu, han, oya) + " ≠ " + ron);
        }
        if (tsumo != null && ScTsumoText(fu, han, oya) != tsumo)
        {
            problems.Add("点数表がズレている " + at + " ツモ " + ScTsumoText(fu, han, oya) + " ≠ " + tsumo);
        }
    }

    // ============================================================
    // MainMenu > ワールド設定 に置く操作（元に戻す・大きさ）
    //
    // MainMenuController には触らない。ボタンの onClick を
    // MahjongYakuGuide の UdonBehaviour へ直接向けるだけで済む。
    // ラベルの英訳は BuildLanguage.EnglishByPath 側に登録してある
    //（登録しないと Validate Language が「未翻訳」で落ちる）。
    // ============================================================
    static void BuildWorldSettings(MahjongYakuGuide guide)
    {
        var menu = GameObject.Find("MainMenu");
        if (menu == null)
        {
            Debug.LogWarning("[BuildYakuGuide] MainMenu がシーンにありません。ワールド設定の操作は作れません。");
            return;
        }
        // ワールド設定の「役の早見板」ページ。札（RowYakuSize / RowYakuHome）は BuildMainMenu が作るので、
        // ここはその上に見出し・ボタン・説明を同じ座標（BuildMainMenu.World* ）で置く。
        var screen = menu.transform.Find("ScreenWorld/PageYaku");
        if (screen == null)
        {
            Debug.LogWarning("[BuildYakuGuide] ScreenWorld/PageYaku がありません。先に Build Main Menu を実行してください。");
            return;
        }

        float headX = BuildMainMenu.WorldRowLeft + BuildMainMenu.WorldPad + BuildMainMenu.WorldHeadW * 0.5f;
        float noteW = BuildMainMenu.WorldRowW - BuildMainMenu.WorldPad * 2f;
        float y0 = BuildMainMenu.WorldRowY[0];
        float y1 = BuildMainMenu.WorldRowY[1];
        float choiceY0 = y0 + BuildMainMenu.WorldHeadDY;
        float choiceY1 = y1 + BuildMainMenu.WorldHeadDY;
        float right = BuildMainMenu.WorldRowRight - BuildMainMenu.WorldPad;
        float gap = BuildMainMenu.WorldChoiceGap;

        // 1行目：板の大きさ（小・中・大を右詰め）
        var head = Node(screen, "YakuHead");
        MenuRect(head, headX, choiceY0, BuildMainMenu.WorldHeadW, 80f);
        MenuTmp(head, "板の大きさ", 40f, TextAlignmentOptions.MidlineLeft);

        const float sizeW = 200f;
        MenuBtn(screen, "BtnYakuS", "小", right - sizeW * 0.5f - 2f * (sizeW + gap), choiceY0, sizeW, 96f, 36f);
        MenuBtn(screen, "BtnYakuM", "中", right - sizeW * 0.5f - (sizeW + gap), choiceY0, sizeW, 96f, 36f);
        MenuBtn(screen, "BtnYakuL", "大", right - sizeW * 0.5f, choiceY0, sizeW, 96f, 36f);

        var note = Node(screen, "YakuNote");
        MenuRect(note, BuildMainMenu.WorldPaneX, y0 + BuildMainMenu.WorldNoteDY, noteW, 90f);
        var noteTmp = MenuTmp(note, "受付にある役種一覧の板の大きさです。あなたにだけ効きます。",
            26f, TextAlignmentOptions.TopLeft);
        noteTmp.enableWordWrapping = true;

        // 2行目：置き場所（元に戻す）
        var homeHead = Node(screen, "YakuHomeHead");
        MenuRect(homeHead, headX, choiceY1, BuildMainMenu.WorldHeadW, 80f);
        MenuTmp(homeHead, "置き場所", 40f, TextAlignmentOptions.MidlineLeft);

        const float homeW = 300f;
        MenuBtn(screen, "BtnYakuHome", "元に戻す", right - homeW * 0.5f, choiceY1, homeW, 96f, 36f);

        var homeNote = Node(screen, "YakuHomeNote");
        MenuRect(homeNote, BuildMainMenu.WorldPaneX, y1 + BuildMainMenu.WorldNoteDY, noteW, 90f);
        var homeNoteTmp = MenuTmp(homeNote, "「元に戻す」を押すと、持ち出した板が受付の定位置へ戻ります。",
            26f, TextAlignmentOptions.TopLeft);
        homeNoteTmp.enableWordWrapping = true;

        // --- 参照と配線 ---
        var images = new Image[MahjongYakuGuide.SIZE_COUNT];
        images[0] = screen.Find("BtnYakuS").GetComponent<Image>();
        images[1] = screen.Find("BtnYakuM").GetComponent<Image>();
        images[2] = screen.Find("BtnYakuL").GetComponent<Image>();
        guide.sizeButtonImages = images;
        guide.sizeScales = new float[] { 1f, 1.35f, 1.8f };
        guide.sizeOnColor = StyleMainMenu.Yellow;
        guide.sizeOffColor = StyleMainMenu.White;
        EditorUtility.SetDirty(guide);
        UdonSharpEditorUtility.CopyProxyToUdon(guide);

        var udon = UdonSharpEditorUtility.GetBackingUdonBehaviour(guide);
        MenuWire(screen, "BtnYakuS", udon, "OnSizeSmall");
        MenuWire(screen, "BtnYakuM", udon, "OnSizeMedium");
        MenuWire(screen, "BtnYakuL", udon, "OnSizeLarge");
        MenuWire(screen, "BtnYakuHome", udon, "OnReturnHome");

        // 生成直後の見た目を既定（小）にしておく。実行時は ApplySize が塗り直す。
        images[0].color = StyleMainMenu.Yellow;
        images[1].color = StyleMainMenu.White;
        images[2].color = StyleMainMenu.White;

        EditorSceneManager.MarkSceneDirty(menu.scene);
    }

    // MainMenu の Canvas は 1920x1080・中央基準。こちらの左上基準ヘルパとは別物なので分けてある。
    static void MenuRect(Transform t, float x, float y, float w, float h)
    {
        var rt = (RectTransform)t;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(w, h);
        rt.localRotation = Quaternion.identity;
        rt.localScale = Vector3.one;
    }

    static TextMeshProUGUI MenuTmp(Transform t, string text, float size, TextAlignmentOptions align)
    {
        var tmp = t.GetComponent<TextMeshProUGUI>();
        if (tmp == null) { tmp = Undo.AddComponent<TextMeshProUGUI>(t.gameObject); }
        if (font_ != null) { tmp.font = font_; }
        tmp.text = text;
        tmp.fontSize = size;
        tmp.alignment = align;
        tmp.color = StyleMainMenu.Ink;
        tmp.raycastTarget = false;
        EditorUtility.SetDirty(tmp);
        return tmp;
    }

    static void MenuBtn(Transform parent, string name, string label,
        float x, float y, float w, float h, float fontSize)
    {
        var t = Node(parent, name);
        MenuRect(t, x, y, w, h);
        var image = Img(t, StyleMainMenu.White, true);
        StyleMainMenu.Frame(image, 4f, 9f);

        var button = t.GetComponent<Button>();
        if (button == null) { button = Undo.AddComponent<Button>(t.gameObject); }
        button.targetGraphic = image;
        StyleMainMenu.ButtonTint(button);

        var labelT = Node(t, "Label");
        Stretch(labelT);
        MenuTmp(labelT, label, fontSize, TextAlignmentOptions.Center);
    }

    // MainMenu のボタンには WireMainMenu がクリック演出（MenuButtonPress）の
    // リスナも足している。全消ししてしまうと演出が死ぬので、自分の分だけ差し替える。
    static void MenuWire(Transform parent, string name, UdonBehaviour udon, string eventName)
    {
        var t = parent.Find(name);
        if (t == null) { Debug.LogError("[BuildYakuGuide] ボタンが見つかりません: " + name); return; }
        var button = t.GetComponent<Button>();
        if (button == null) { Debug.LogError("[BuildYakuGuide] Button がありません: " + name); return; }

        for (int i = button.onClick.GetPersistentEventCount() - 1; i >= 0; --i)
        {
            if (button.onClick.GetPersistentTarget(i) == (Object)udon)
            {
                UnityEventTools.RemovePersistentListener(button.onClick, i);
            }
        }
        UnityEventTools.AddStringPersistentListener(button.onClick, udon.SendCustomEvent, eventName);
        EditorUtility.SetDirty(button);
    }

    // ============================================================
    // 牌アトラスの実測
    // ============================================================
    // 牌メッシュの絵柄面（submesh 1）の先頭クアッドがそのまま絵柄の矩形。
    // 座標を手打ちで持つと、アトラスを差し替えたときに黙ってズレる。
    static Dictionary<string, Rect> MeasureTileRects()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(TilePrefabPath);
        if (prefab == null)
        {
            Debug.LogError("[BuildYakuGuide] 卓 Prefab が見つかりません: " + TilePrefabPath);
            return null;
        }

        Transform tiles0 = null;
        foreach (var t in prefab.GetComponentsInChildren<Transform>(true))
        {
            if (t.name == "Tiles0") { tiles0 = t; break; }
        }
        if (tiles0 == null)
        {
            Debug.LogError("[BuildYakuGuide] Prefab に Tiles0 がありません。");
            return null;
        }

        var map = new Dictionary<string, Rect>();
        foreach (Transform c in tiles0)
        {
            var mf = c.GetComponent<MeshFilter>();
            var mr = c.GetComponent<MeshRenderer>();
            if (mf == null || mf.sharedMesh == null || mr == null) { continue; }

            int face = (mr.sharedMaterials != null && mr.sharedMaterials.Length >= 2) ? 1 : 0;
            var mesh = mf.sharedMesh;
            var uvs = mesh.uv;
            var tris = mesh.GetTriangles(face);
            if (tris.Length < 6) { continue; }

            float minU = 9f, minV = 9f, maxU = -9f, maxV = -9f;
            for (int i = 0; i < 6; ++i)
            {
                var uv = uvs[tris[i]];
                if (uv.x < minU) { minU = uv.x; }
                if (uv.x > maxU) { maxU = uv.x; }
                if (uv.y < minV) { minV = uv.y; }
                if (uv.y > maxV) { maxV = uv.y; }
            }
            map[c.name] = new Rect(minU, minV, maxU - minU, maxV - minV);
        }

        if (map.Count < 34)
        {
            Debug.LogError("[BuildYakuGuide] 牌の実測が " + map.Count + " 種しかありません（34種以上必要）。");
            return null;
        }
        return map;
    }

    // ============================================================
    // U# コンポーネント
    // ============================================================
    static bool EnsureProgramAsset()
    {
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(ScriptPath);
        if (script == null)
        {
            Debug.LogError("[BuildYakuGuide] スクリプトが見つかりません: " + ScriptPath);
            return false;
        }

        var type = script.GetClass();
        if (type != null && UdonSharpProgramAsset.GetProgramAssetForClass(type) != null) { return false; }
        if (AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(ProgramAssetPath) != null) { return false; }

        var programAsset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
        programAsset.sourceCsScript = script;
        AssetDatabase.CreateAsset(programAsset, ProgramAssetPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[BuildYakuGuide] U#プログラムアセットを生成: " + ProgramAssetPath);
        return true;
    }

    static MahjongYakuGuide EnsureComponent(GameObject go)
    {
        var guide = go.GetComponent<MahjongYakuGuide>();
        if (guide != null)
        {
            var existing = UdonSharpEditorUtility.GetBackingUdonBehaviour(guide);
            if (existing != null && existing.programSource != null) { return guide; }

            Debug.LogWarning("[BuildYakuGuide] MahjongYakuGuide の UdonBehaviour が壊れているため作り直します。");
            Object.DestroyImmediate(guide);
            foreach (var ub in go.GetComponents<UdonBehaviour>())
            {
                if (ub.programSource == null) { Object.DestroyImmediate(ub); }
            }
        }

        var added = (MahjongYakuGuide)UdonSharpUndo.AddComponent(go, typeof(MahjongYakuGuide));
        if (added == null || UdonSharpEditorUtility.GetBackingUdonBehaviour(added) == null)
        {
            Debug.LogError("[BuildYakuGuide] MahjongYakuGuide を追加できません（U#未コンパイル？）。コンパイル後に再実行してください。");
            if (added != null) { Object.DestroyImmediate(added); }
            return null;
        }
        return added;
    }

    // ============================================================
    // 配線
    // ============================================================
    static void Wire(MahjongYakuGuide guide, GameObject rootGo, Dictionary<string, Rect> rects)
    {
        var rootT = rootGo.transform;
        var panel = rootT.Find("Canvas/Panel");

        // --- 牌テーブル ---
        var order = TileOrder;
        var u = new float[order.Length];
        var v = new float[order.Length];
        var w = new float[order.Length];
        var h = new float[order.Length];
        var missing = new List<string>();
        for (int i = 0; i < order.Length; ++i)
        {
            Rect r;
            if (!rects.TryGetValue(order[i], out r)) { missing.Add(order[i]); continue; }
            u[i] = r.x; v[i] = r.y; w[i] = r.width; h[i] = r.height;
        }
        if (missing.Count > 0)
        {
            Debug.LogError("[BuildYakuGuide] 牌の実測に失敗: " + string.Join(", ", missing));
            return;
        }

        guide.tileTexture = AssetDatabase.LoadAssetAtPath<Texture>(TileTexturePath);
        guide.tileU = u;
        guide.tileV = v;
        guide.tileW = w;
        guide.tileH = h;

        // --- 役データ ---
        int n = YakuTable.Length;
        var tab = new int[n];
        var local = new bool[n];
        var nameJa = new string[n];
        var nameEn = new string[n];
        var badgeJa = new string[n];
        var badgeEn = new string[n];
        var descJa = new string[n];
        var descEn = new string[n];
        // 韓国語は KoreanText（英→韓の対訳表）から引く。表に無い欄は空＝実行時は英語を出す。
        var nameKo = new string[n];
        var badgeKo = new string[n];
        var descKo = new string[n];
        // 中国語は ChineseText（英→中）。簡体字と繁体字の2列を作る。
        var nameZhs = new string[n];
        var badgeZhs = new string[n];
        var descZhs = new string[n];
        var nameZht = new string[n];
        var badgeZht = new string[n];
        var descZht = new string[n];
        var planStart = new int[n];
        var planCount = new int[n];
        var plan = new List<int>();

        for (int i = 0; i < n; ++i)
        {
            var e = YakuTable[i];
            tab[i] = e.tab;
            local[i] = e.local;
            nameJa[i] = e.local ? "ローカル役：" + e.nameJa : e.nameJa;
            nameEn[i] = e.local ? "Local: " + e.nameEn : e.nameEn;
            badgeJa[i] = e.badgeJa;
            badgeEn[i] = e.badgeEn;
            descJa[i] = e.descJa;
            descEn[i] = e.descEn;
            var koName = KoreanText.Ko(e.nameEn, Scope);
            nameKo[i] = koName == null ? "" : (e.local ? "로컬 역: " + koName : koName);
            badgeKo[i] = KoreanText.Ko(e.badgeEn, Scope) ?? "";
            descKo[i] = KoreanText.Ko(e.descEn, Scope) ?? "";
            var zhsName = ChineseText.Zh(e.nameEn, false, Scope);
            var zhtName = ChineseText.Zh(e.nameEn, true, Scope);
            nameZhs[i] = zhsName == null ? "" : (e.local ? "地方役：" + zhsName : zhsName);
            nameZht[i] = zhtName == null ? "" : (e.local ? "地方役：" + zhtName : zhtName);
            badgeZhs[i] = ChineseText.Zh(e.badgeEn, false, Scope) ?? "";
            badgeZht[i] = ChineseText.Zh(e.badgeEn, true, Scope) ?? "";
            descZhs[i] = ChineseText.Zh(e.descEn, false, Scope) ?? "";
            descZht[i] = ChineseText.Zh(e.descEn, true, Scope) ?? "";

            planStart[i] = plan.Count;
            int added = ParsePattern(e.pattern, plan, e.nameJa);
            planCount[i] = added;
        }

        guide.yakuTab = tab;
        guide.yakuLocal = local;
        guide.yakuNameJa = nameJa;
        guide.yakuNameEn = nameEn;
        guide.yakuBadgeJa = badgeJa;
        guide.yakuBadgeEn = badgeEn;
        guide.yakuDescJa = descJa;
        guide.yakuDescEn = descEn;
        guide.yakuNameKo = nameKo;
        guide.yakuBadgeKo = badgeKo;
        guide.yakuDescKo = descKo;
        guide.yakuNameZhs = nameZhs;
        guide.yakuBadgeZhs = badgeZhs;
        guide.yakuDescZhs = descZhs;
        guide.yakuNameZht = nameZht;
        guide.yakuBadgeZht = badgeZht;
        guide.yakuDescZht = descZht;
        guide.tilePlan = plan.ToArray();
        guide.tilePlanStart = planStart;
        guide.tilePlanCount = planCount;

        // --- UI 参照 ---
        var tabs = panel.Find("Tabs");
        var tabImages = new Image[TabJa.Length];
        var tabLabels = new TextMeshProUGUI[TabJa.Length];
        for (int i = 0; i < TabJa.Length; ++i)
        {
            var t = tabs.Find("Tab" + i);
            tabImages[i] = t.GetComponent<Image>();
            tabLabels[i] = t.Find("Label").GetComponent<TextMeshProUGUI>();
        }
        guide.tabImages = tabImages;
        guide.tabLabels = tabLabels;
        guide.tabNameJa = TabJa;
        guide.tabNameEn = TabEn;
        guide.tabNameKo = KoreanText.KoArray(TabEn, null, Scope);
        guide.tabNameZhs = ChineseText.ZhArray(TabEn, false, null, Scope);
        guide.tabNameZht = ChineseText.ZhArray(TabEn, true, null, Scope);

        var side = panel.Find("Side");
        var secImages = new Image[SectionJa.Length];
        var secLabels = new TextMeshProUGUI[SectionJa.Length];
        for (int i = 0; i < SectionJa.Length; ++i)
        {
            var t = side.Find("BtnSec" + i);
            secImages[i] = t.GetComponent<Image>();
            secLabels[i] = t.Find("Label").GetComponent<TextMeshProUGUI>();
        }
        guide.sectionImages = secImages;
        guide.sectionLabels = secLabels;
        guide.sectionNameJa = SectionJa;
        guide.sectionNameEn = SectionEn;
        guide.sectionNameKo = KoreanText.KoArray(SectionEn, null, Scope);
        guide.sectionNameZhs = ChineseText.ZhArray(SectionEn, false, null, Scope);
        guide.sectionNameZht = ChineseText.ZhArray(SectionEn, true, null, Scope);

        var list = panel.Find("List");
        var content = list.Find("Viewport/Content");
        int slots = MahjongYakuGuide.SLOT_COUNT;
        var entryRoots = new GameObject[slots];
        var entryRects = new RectTransform[slots];
        var entryNames = new TextMeshProUGUI[slots];
        var entryBadges = new TextMeshProUGUI[slots];
        var entryDescs = new TextMeshProUGUI[slots];
        var entryTileRows = new GameObject[slots];
        var entryTiles = new RawImage[slots * MahjongYakuGuide.MAX_TILES];
        for (int i = 0; i < slots; ++i)
        {
            var e = content.Find("Entry" + i);
            entryRoots[i] = e.gameObject;
            entryRects[i] = (RectTransform)e;
            entryNames[i] = e.Find("HeaderBar/Name").GetComponent<TextMeshProUGUI>();
            entryBadges[i] = e.Find("HeaderBar/Badge").GetComponent<TextMeshProUGUI>();
            entryDescs[i] = e.Find("Desc").GetComponent<TextMeshProUGUI>();
            var row = e.Find("Tiles");
            entryTileRows[i] = row.gameObject;
            for (int k = 0; k < MahjongYakuGuide.MAX_TILES; ++k)
            {
                entryTiles[i * MahjongYakuGuide.MAX_TILES + k] = row.Find("Tile" + k).GetComponent<RawImage>();
            }
        }
        guide.listRoot = list.gameObject;
        guide.scrollRect = list.GetComponent<ScrollRect>();
        guide.contentRect = (RectTransform)content;
        guide.entryRoots = entryRoots;
        guide.entryRects = entryRects;
        guide.entryNames = entryNames;
        guide.entryBadges = entryBadges;
        guide.entryDescs = entryDescs;
        guide.entryTileRows = entryTileRows;
        guide.entryTiles = entryTiles;
        guide.emptyLabel = panel.Find("EmptyLabel").GetComponent<TextMeshProUGUI>();

        var head = panel.Find("Head");
        guide.titleLabel = head.Find("Title").GetComponent<TextMeshProUGUI>();
        guide.countLabel = head.Find("CountLabel").GetComponent<TextMeshProUGUI>();
        guide.localLabel = head.Find("BtnLocal/Label").GetComponent<TextMeshProUGUI>();
        guide.localImage = head.Find("BtnLocal").GetComponent<Image>();
        guide.brandLabel = side.Find("Brand").GetComponent<TextMeshProUGUI>();
        guide.hintLabel = side.Find("Hint").GetComponent<TextMeshProUGUI>();

        var notice = panel.Find("Notice");
        guide.noticeRoot = notice.gameObject;
        guide.noticeLabel = notice.Find("NoticeLabel").GetComponent<TextMeshProUGUI>();

        WireTutorial(guide, panel, head, tabs);
        WireScore(guide, panel, rects);

        guide.entryStep = EntryStep;
        guide.viewportHeight = ListH;
        guide.tileHeight = TileH;
        guide.tileGap = 7f;
        guide.tileAreaWidth = TileAreaW;

        guide.tabOnColor = StyleMainMenu.Yellow;
        guide.tabOffColor = StyleMainMenu.White;
        guide.tabDisabledColor = new Color32(214, 210, 196, 255);
        guide.toggleOnColor = StyleMainMenu.Teal;
        guide.toggleOffColor = StyleMainMenu.White;
        guide.toggleOnlyColor = StyleMainMenu.Purple;

        // --- 持ち運び ---
        guide.pickupRoot = rootT;
        guide.pickup = rootGo.GetComponent<VRC.SDK3.Components.VRCPickup>();
        var home = GameObject.Find(RootName + "_Home");
        guide.homeAnchor = home != null ? home.transform : null;

        // --- 言語 ---
        var langGo = GameObject.Find("LanguageManager");
        var lang = langGo != null ? langGo.GetComponentInChildren<KemonoLanguage>(true) : null;
        if (lang == null) { lang = Object.FindObjectOfType<KemonoLanguage>(true); }
        guide.languageManager = lang;

        EditorUtility.SetDirty(guide);
        UdonSharpEditorUtility.CopyProxyToUdon(guide);

        // --- ボタン ---
        var udon = UdonSharpEditorUtility.GetBackingUdonBehaviour(guide);
        if (udon == null)
        {
            Debug.LogError("[BuildYakuGuide] backing UdonBehaviour が取得できません。");
            return;
        }
        for (int i = 0; i < TabJa.Length; ++i) { WireButton(tabs, "Tab" + i, udon, "OnTab" + i); }
        WireButton(side, "BtnSec0", udon, "OnSectionYaku");
        WireButton(side, "BtnSec1", udon, "OnSectionTutorial");
        WireButton(side, "BtnSec2", udon, "OnSectionScore");
        WireButton(head, "BtnUp", udon, "OnScrollUp");
        WireButton(head, "BtnDown", udon, "OnScrollDown");
        WireButton(head, "BtnLocal", udon, "OnToggleLocal");
        var tutRoot = panel.Find("Tutorial");
        WireButton(tutRoot, "BtnPagePrev", udon, "OnPagePrev");
        WireButton(tutRoot, "BtnPageNext", udon, "OnPageNext");
        var scoreRoot = panel.Find("Score");
        for (int i = 0; i < ScoreTabJa.Length; ++i)
        {
            WireButton(scoreRoot, "ScoreTab" + i, udon, "OnScoreTab" + i);
        }

        // --- 言語マネージャの配布先に登録（ApplyLanguageJa/En が飛んでくる）---
        if (lang != null)
        {
            var targets = new List<UdonSharpBehaviour>();
            if (lang.extraTargets != null)
            {
                foreach (var b in lang.extraTargets)
                {
                    if (b != null && b != (UdonSharpBehaviour)guide) { targets.Add(b); }
                }
            }
            targets.Add(guide);
            lang.extraTargets = targets.ToArray();
            EditorUtility.SetDirty(lang);
            UdonSharpEditorUtility.CopyProxyToUdon(lang);
        }
        else
        {
            Debug.LogWarning("[BuildYakuGuide] KemonoLanguage が見つかりません。英語切替が届きません。");
        }

        PreviewFirstPage(guide);
    }

    // 生成直後の見た目を「一翻タブの先頭」にしておく。
    // 実行時は Refresh() が上書きするが、
    //   ・エディタのシーンビューで実物のレイアウトを確認できる
    //   ・万一 Udon の Start が遅れても「役名／説明」のダミーが出ない
    // の2つのために、ビルド時点で見えている行ぶんだけ静的に流し込む。
    static void PreviewFirstPage(MahjongYakuGuide guide)
    {
        int total = 0;
        for (int i = 0; i < YakuTable.Length; ++i) { if (YakuTable[i].tab == 0 && !YakuTable[i].local) { ++total; } }

        int slot = 0;
        for (int i = 0; i < YakuTable.Length && slot < MahjongYakuGuide.SLOT_COUNT; ++i)
        {
            var e = YakuTable[i];
            if (e.tab != 0 || e.local) { continue; }

            guide.entryRoots[slot].SetActive(true);
            guide.entryRects[slot].anchoredPosition = new Vector2(0f, -slot * EntryStep);
            guide.entryNames[slot].text = e.nameJa;
            guide.entryBadges[slot].text = e.badgeJa;
            guide.entryDescs[slot].text = e.descJa;

            int start = guide.tilePlanStart[i];
            int count = guide.tilePlanCount[i];
            float aspect = guide.tileW[0] / guide.tileH[0];
            float w = TileH * aspect;

            int tiles = 0, gaps = 0;
            for (int k = 0; k < count; ++k)
            {
                if (guide.tilePlan[start + k] == -1) { ++gaps; } else { ++tiles; }
            }
            float need = tiles * w + gaps * 7f;
            float scale = (need > TileAreaW && need > 0f) ? TileAreaW / need : 1f;

            float x = 0f;
            int drawn = 0;
            for (int k = 0; k < count; ++k)
            {
                int id = guide.tilePlan[start + k];
                if (id == -1) { x += 7f * scale; continue; }
                if (drawn >= MahjongYakuGuide.MAX_TILES) { break; }

                var img = guide.entryTiles[slot * MahjongYakuGuide.MAX_TILES + drawn];
                ++drawn;
                img.gameObject.SetActive(true);
                img.texture = id == -2 ? Texture2D.whiteTexture : guide.tileTexture;
                img.color = id == -2 ? new Color32(217, 163, 35, 255) : Color.white;
                img.uvRect = id == -2 ? new Rect(0f, 0f, 1f, 1f)
                    : new Rect(guide.tileU[id], guide.tileV[id], guide.tileW[id], guide.tileH[id]);
                var rt = img.rectTransform;
                rt.sizeDelta = new Vector2(w * scale, TileH * scale);
                rt.anchoredPosition = new Vector2(x, 0f);
                x += w * scale;
                EditorUtility.SetDirty(img);
            }
            for (int k = drawn; k < MahjongYakuGuide.MAX_TILES; ++k)
            {
                guide.entryTiles[slot * MahjongYakuGuide.MAX_TILES + k].gameObject.SetActive(false);
            }

            ++slot;
        }

        guide.contentRect.sizeDelta = new Vector2(guide.contentRect.sizeDelta.x, Mathf.Max(ListH, total * EntryStep));
        guide.contentRect.anchoredPosition = new Vector2(0f, 0f);
        guide.countLabel.text = "全 " + total + " 件";
        guide.localLabel.text = "ローカル役 なし";
        guide.localImage.color = StyleMainMenu.White;
        guide.tabImages[0].color = StyleMainMenu.Yellow;
        guide.sectionImages[0].color = StyleMainMenu.Yellow;
        guide.brandLabel.text = "麻雀集会所";
        guide.hintLabel.text = "つかむと持ち運べます";

        // Preview でチュートリアル・点数計算を出したまま Build しても、一覧の見た目に戻す。
        if (guide.listRoot != null) { guide.listRoot.SetActive(true); }
        if (guide.noticeRoot != null) { guide.noticeRoot.SetActive(false); }
        if (guide.tutorialRoot != null) { guide.tutorialRoot.SetActive(false); }
        if (guide.scoreRoot != null) { guide.scoreRoot.SetActive(false); }
        if (guide.listOnlyObjects != null)
        {
            foreach (var go in guide.listOnlyObjects) { if (go != null) { go.SetActive(true); } }
        }
    }

    // ============================================================
    // チュートリアル：表 → U# のデータ配列 ＋ UI 参照
    // ============================================================
    static void WireTutorial(MahjongYakuGuide guide, Transform panel, Transform head, Transform tabs)
    {
        var root = panel.Find("Tutorial");

        // --- データ（ページ順にブロックを平らに並べる）---
        int numPages = TutorialTable.Length;
        var titleJa = new string[numPages];
        var titleEn = new string[numPages];
        var pageStart = new int[numPages];
        var pageCount = new int[numPages];

        var bx = new List<float>();
        var by = new List<float>();
        var bw = new List<float>();
        var bh = new List<float>();
        var headJa = new List<string>();
        var headEn = new List<string>();
        var bodyJa = new List<string>();
        var bodyEn = new List<string>();
        var tileStart = new List<int>();
        var tileCount = new List<int>();
        var blockTileH = new List<float>();
        var blockRuby = new List<bool>();
        var plan = new List<int>();

        for (int p = 0; p < numPages; ++p)
        {
            var page = TutorialTable[p];
            titleJa[p] = page.titleJa;
            titleEn[p] = page.titleEn;
            pageStart[p] = bx.Count;
            pageCount[p] = page.blocks.Length;

            if (page.blocks.Length > MahjongYakuGuide.TUT_BLOCKS)
            {
                Debug.LogError("[BuildYakuGuide] ページ " + (p + 1) + " のブロックが多すぎます（" +
                               page.blocks.Length + " > " + MahjongYakuGuide.TUT_BLOCKS + "）: " + page.titleJa);
            }

            for (int b = 0; b < page.blocks.Length; ++b)
            {
                var blk = page.blocks[b];
                bx.Add(blk.x); by.Add(blk.y); bw.Add(blk.w); bh.Add(blk.h);
                headJa.Add(blk.headJa); headEn.Add(blk.headEn);
                bodyJa.Add(blk.bodyJa); bodyEn.Add(blk.bodyEn);
                blockTileH.Add(blk.tileH);
                blockRuby.Add(blk.ruby);
                tileStart.Add(plan.Count);
                tileCount.Add(ParsePattern(blk.pattern, plan, page.titleJa));
            }
        }

        guide.tutPageTitleJa = titleJa;
        guide.tutPageTitleEn = titleEn;
        guide.tutPageTitleKo = KoreanText.KoArray(titleEn, null, Scope);
        guide.tutPageTitleZhs = ChineseText.ZhArray(titleEn, false, null, Scope);
        guide.tutPageTitleZht = ChineseText.ZhArray(titleEn, true, null, Scope);
        guide.tutPageStart = pageStart;
        guide.tutPageCount = pageCount;
        guide.tutBlockX = bx.ToArray();
        guide.tutBlockY = by.ToArray();
        guide.tutBlockW = bw.ToArray();
        guide.tutBlockH = bh.ToArray();
        guide.tutBlockHeadJa = headJa.ToArray();
        guide.tutBlockHeadEn = headEn.ToArray();
        guide.tutBlockHeadKo = KoreanText.KoArray(guide.tutBlockHeadEn, null, Scope);
        guide.tutBlockHeadZhs = ChineseText.ZhArray(guide.tutBlockHeadEn, false, null, Scope);
        guide.tutBlockHeadZht = ChineseText.ZhArray(guide.tutBlockHeadEn, true, null, Scope);
        guide.tutBlockBodyJa = bodyJa.ToArray();
        guide.tutBlockBodyEn = bodyEn.ToArray();
        guide.tutBlockBodyKo = KoreanText.KoArray(guide.tutBlockBodyEn, null, Scope);
        guide.tutBlockBodyZhs = ChineseText.ZhArray(guide.tutBlockBodyEn, false, null, Scope);
        guide.tutBlockBodyZht = ChineseText.ZhArray(guide.tutBlockBodyEn, true, null, Scope);
        guide.tutBlockTileH = blockTileH.ToArray();
        guide.tutTilePlan = plan.ToArray();
        guide.tutTileStart = tileStart.ToArray();
        guide.tutTileCount = tileCount.ToArray();
        guide.tutBlockRuby = blockRuby.ToArray();

        // --- UI 参照 ---
        int slots = MahjongYakuGuide.TUT_BLOCKS;
        var blockRoots = new GameObject[slots];
        var blockRects = new RectTransform[slots];
        var headRoots = new GameObject[slots];
        var headLabels = new TextMeshProUGUI[slots];
        var bodyRects = new RectTransform[slots];
        var bodyLabels = new TextMeshProUGUI[slots];
        var tileRows = new RectTransform[slots];
        var tiles = new RawImage[slots * MahjongYakuGuide.MAX_TILES];
        var tileRuby = new TextMeshProUGUI[slots * MahjongYakuGuide.MAX_TILES];

        for (int i = 0; i < slots; ++i)
        {
            var block = root.Find("Block" + i);
            blockRoots[i] = block.gameObject;
            blockRects[i] = (RectTransform)block;
            headRoots[i] = block.Find("Head").gameObject;
            headLabels[i] = block.Find("Head/Label").GetComponent<TextMeshProUGUI>();
            bodyRects[i] = (RectTransform)block.Find("Body");
            bodyLabels[i] = block.Find("Body").GetComponent<TextMeshProUGUI>();
            tileRows[i] = (RectTransform)block.Find("Tiles");
            for (int k = 0; k < MahjongYakuGuide.MAX_TILES; ++k)
            {
                tiles[i * MahjongYakuGuide.MAX_TILES + k] =
                    block.Find("Tiles/Tile" + k).GetComponent<RawImage>();
                tileRuby[i * MahjongYakuGuide.MAX_TILES + k] =
                    block.Find("Tiles/Tile" + k + "/Ruby").GetComponent<TextMeshProUGUI>();
            }
        }

        guide.tutorialRoot = root.gameObject;
        guide.tutBlockRoots = blockRoots;
        guide.tutBlockRects = blockRects;
        guide.tutHeadRoots = headRoots;
        guide.tutHeadLabels = headLabels;
        guide.tutBodyRects = bodyRects;
        guide.tutBodyLabels = bodyLabels;
        guide.tutTileRows = tileRows;
        guide.tutTiles = tiles;
        guide.tutTileRuby = tileRuby;

        // ルビの色。U# のヒープは既定値を変えても更新されないので、Wire で毎回入れ直す。
        guide.rubyColorMan = RubyMan;
        guide.rubyColorPin = RubyPin;
        guide.rubyColorSou = RubySou;
        guide.rubyColorHonor = StyleMainMenu.Ink;
        guide.tutPrevButton = root.Find("BtnPagePrev").gameObject;
        guide.tutNextButton = root.Find("BtnPageNext").gameObject;
        guide.tutPageLabel = root.Find("PageLabel").GetComponent<TextMeshProUGUI>();

        // 一覧のときだけ出すもの。読み物の画面に翻数タブが残らないようにする。
        guide.listOnlyObjects = new GameObject[]
        {
            tabs.gameObject,
            head.Find("BtnUp").gameObject,
            head.Find("BtnDown").gameObject,
            head.Find("CountLabel").gameObject,
            head.Find("BtnLocal").gameObject,
        };
    }

    // エディタでチュートリアルの見た目を確かめる（押すたびに次のページへ送る）。
    // 実行時は U# の Refresh() が同じ計算で上書きするので、ここで焼いた値に意味は無い。
    // Build Yaku Guide を実行すると一覧の表示に戻る。
    [MenuItem("Tools/Kemono Mahjong/Preview Yaku Guide Tutorial")]
    public static void PreviewTutorialMenu()
    {
        var root = GameObject.Find(RootName);
        if (root == null) { Debug.LogError("[BuildYakuGuide] 先に Build Yaku Guide を実行してください。"); return; }
        var guide = root.GetComponent<MahjongYakuGuide>();
        if (guide == null || guide.tutorialRoot == null)
        {
            Debug.LogError("[BuildYakuGuide] チュートリアルが未生成です。Build Yaku Guide を実行してください。");
            return;
        }

        int pages = guide.tutPageTitleJa != null ? guide.tutPageTitleJa.Length : 0;
        if (pages == 0) { Debug.LogError("[BuildYakuGuide] チュートリアルのページがありません。"); return; }

        int page = EditorPrefs.GetInt("KemonoMahjong.YakuGuideTutorialPreviewPage", -1) + 1;
        if (page >= pages) { page = 0; }
        EditorPrefs.SetInt("KemonoMahjong.YakuGuideTutorialPreviewPage", page);

        PreviewTutorialPage(guide, page);
        Debug.Log("[BuildYakuGuide] チュートリアル " + (page + 1) + " / " + pages +
                  "「" + guide.tutPageTitleJa[page] + "」を表示しました。もう一度押すと次のページへ進みます。");
    }

    static void PreviewTutorialPage(MahjongYakuGuide guide, int page)
    {
        if (guide.listRoot != null) { guide.listRoot.SetActive(false); }
        if (guide.noticeRoot != null) { guide.noticeRoot.SetActive(false); }
        if (guide.scoreRoot != null) { guide.scoreRoot.SetActive(false); }
        if (guide.emptyLabel != null) { guide.emptyLabel.gameObject.SetActive(false); }
        if (guide.listOnlyObjects != null)
        {
            foreach (var go in guide.listOnlyObjects) { if (go != null) { go.SetActive(false); } }
        }
        guide.tutorialRoot.SetActive(true);

        for (int i = 0; i < guide.sectionImages.Length; ++i)
        {
            guide.sectionImages[i].color = i == MahjongYakuGuide.SECTION_TUTORIAL
                ? StyleMainMenu.Yellow : StyleMainMenu.White;
        }

        guide.titleLabel.text = guide.tutPageTitleJa[page];
        guide.tutPageLabel.text = (page + 1) + " / " + guide.tutPageTitleJa.Length;
        guide.tutPrevButton.SetActive(page > 0);
        guide.tutNextButton.SetActive(page < guide.tutPageTitleJa.Length - 1);

        int start = guide.tutPageStart[page];
        int count = guide.tutPageCount[page];
        float aspect = guide.tileW[0] / guide.tileH[0];

        for (int b = 0; b < MahjongYakuGuide.TUT_BLOCKS; ++b)
        {
            bool has = b < count;
            guide.tutBlockRoots[b].SetActive(has);
            if (!has) { continue; }

            int idx = start + b;
            float w = guide.tutBlockW[idx];
            float h = guide.tutBlockH[idx];
            guide.tutBlockRects[b].anchoredPosition = new Vector2(guide.tutBlockX[idx], -guide.tutBlockY[idx]);
            guide.tutBlockRects[b].sizeDelta = new Vector2(w, h);

            string headText = guide.tutBlockHeadJa[idx];
            string bodyText = guide.tutBlockBodyJa[idx];
            bool hasHead = !string.IsNullOrEmpty(headText);
            bool hasBody = !string.IsNullOrEmpty(bodyText);
            int tStart = guide.tutTileStart[idx];
            int tCount = guide.tutTileCount[idx];
            bool hasTiles = tCount > 0;
            bool ruby = guide.tutBlockRuby != null && idx < guide.tutBlockRuby.Length && guide.tutBlockRuby[idx];

            guide.tutHeadRoots[b].SetActive(hasHead);
            if (hasHead) { guide.tutHeadLabels[b].text = headText; }

            float th = guide.tutBlockTileH[idx] > 0f ? guide.tutBlockTileH[idx] : TileH;
            float top = hasHead ? MahjongYakuGuide.TUT_HEAD_H + 12f : 10f;
            float tileY = h - th - 10f;
            if (!hasBody && hasTiles) { tileY = top + (h - top - 10f - th) * 0.5f; }
            if (ruby && hasTiles) { tileY -= 6f; }

            float bodyH = (hasTiles ? tileY - 6f : h - 8f) - top;
            if (bodyH < 12f) { bodyH = 12f; }
            guide.tutBodyRects[b].anchoredPosition = new Vector2(12f, -top);
            guide.tutBodyRects[b].sizeDelta = new Vector2(w - 24f, bodyH);
            guide.tutBodyLabels[b].gameObject.SetActive(hasBody);
            guide.tutBodyLabels[b].text = bodyText;

            guide.tutTileRows[b].gameObject.SetActive(hasTiles);
            guide.tutTileRows[b].anchoredPosition = new Vector2(12f, -tileY);
            guide.tutTileRows[b].sizeDelta = new Vector2(w - 24f, th);

            float areaW = w - 24f;
            int numTiles = 0, numGaps = 0;
            for (int k = 0; k < tCount; ++k)
            {
                if (guide.tutTilePlan[tStart + k] < 0) { ++numGaps; } else { ++numTiles; }
            }
            float tw = th * aspect;
            float need = numTiles * tw + numGaps * 7f;
            float scale = (need > areaW && need > 0f) ? areaW / need : 1f;

            float x = 0f;
            int drawn = 0;
            for (int k = 0; k < tCount; ++k)
            {
                int id = guide.tutTilePlan[tStart + k];
                if (id < 0) { x += 7f * scale; continue; }
                if (drawn >= MahjongYakuGuide.MAX_TILES) { break; }

                var img = guide.tutTiles[b * MahjongYakuGuide.MAX_TILES + drawn];
                ++drawn;
                img.gameObject.SetActive(true);
                img.texture = guide.tileTexture;
                img.uvRect = new Rect(guide.tileU[id], guide.tileV[id], guide.tileW[id], guide.tileH[id]);
                img.rectTransform.sizeDelta = new Vector2(tw * scale, th * scale);
                img.rectTransform.anchoredPosition = new Vector2(x, 0f);
                x += tw * scale;

                // プレビューは日本語表示なので、ルビも日本語側を出す。
                var rb = img.transform.Find("Ruby");
                if (rb != null)
                {
                    rb.gameObject.SetActive(ruby);
                    if (ruby)
                    {
                        var rbTmp = rb.GetComponent<TextMeshProUGUI>();
                        rbTmp.text = RubyTextJa(id);
                        rbTmp.color = RubyColor(id);
                        EditorUtility.SetDirty(rbTmp);
                    }
                }
                EditorUtility.SetDirty(img);
            }
            for (int k = drawn; k < MahjongYakuGuide.MAX_TILES; ++k)
            {
                guide.tutTiles[b * MahjongYakuGuide.MAX_TILES + k].gameObject.SetActive(false);
            }
        }

        EditorUtility.SetDirty(guide);
        EditorSceneManager.MarkSceneDirty(guide.gameObject.scene);
    }

    static void WireButton(Transform parent, string name, UdonBehaviour udon, string eventName)
    {
        var t = parent.Find(name);
        if (t == null) { Debug.LogError("[BuildYakuGuide] ボタンが見つかりません: " + name); return; }
        var button = t.GetComponent<Button>();
        if (button == null) { Debug.LogError("[BuildYakuGuide] Button がありません: " + name); return; }
        while (button.onClick.GetPersistentEventCount() > 0)
        {
            UnityEventTools.RemovePersistentListener(button.onClick, 0);
        }
        UnityEventTools.AddStringPersistentListener(button.onClick, udon.SendCustomEvent, eventName);
        EditorUtility.SetDirty(button);
    }

    // ============================================================
    // 牌並びの解釈
    // ============================================================
    // 記法：数字の並び＋色（m=萬 p=筒 s=索 z=字）。0 は赤五。空白は面子の区切り。
    //   例) "123m 456p 789s 111z 22z"  z は 1東 2南 3西 4北 5白 6發 7中
    static readonly string[] TileOrder =
    {
        "M1","P1","S1","M2","P2","S2","M3","P3","S3","M4","P4","S4","M5","P5","S5",
        "M6","P6","S6","M7","P7","S7","M8","P8","S8","M9","P9","S9",
        "M5R","P5R","S5R","Ton","Nan","Sha","Pey","Haku","Hatsu","Chun",
    };

    static int IndexOfTile(string name)
    {
        for (int i = 0; i < TileOrder.Length; ++i) { if (TileOrder[i] == name) { return i; } }
        return -1;
    }

    static int ParsePattern(string pattern, List<int> plan, string owner)
    {
        int added = 0;
        if (string.IsNullOrEmpty(pattern)) { return 0; }

        var digits = new List<char>();
        for (int i = 0; i < pattern.Length; ++i)
        {
            char c = pattern[i];
            if (c == 'x') { plan.Add(-2); ++added; continue; }
            if (c >= '0' && c <= '9') { digits.Add(c); continue; }

            if (c == ' ')
            {
                if (digits.Count > 0)
                {
                    Debug.LogError("[BuildYakuGuide] 色の指定が無い数字があります: " + owner + " / " + pattern);
                    digits.Clear();
                }
                plan.Add(-1);
                ++added;
                continue;
            }

            string suit =
                c == 'm' ? "M" :
                c == 'p' ? "P" :
                c == 's' ? "S" :
                c == 'z' ? "Z" : null;

            if (suit == null)
            {
                Debug.LogError("[BuildYakuGuide] 知らない記号 '" + c + "': " + owner + " / " + pattern);
                digits.Clear();
                continue;
            }

            foreach (var d in digits)
            {
                string tile;
                if (suit == "Z")
                {
                    switch (d)
                    {
                        case '1': tile = "Ton"; break;
                        case '2': tile = "Nan"; break;
                        case '3': tile = "Sha"; break;
                        case '4': tile = "Pey"; break;
                        case '5': tile = "Haku"; break;
                        case '6': tile = "Hatsu"; break;
                        case '7': tile = "Chun"; break;
                        default: tile = null; break;
                    }
                }
                else
                {
                    tile = d == '0' ? suit + "5R" : suit + d;
                }

                int id = tile != null ? IndexOfTile(tile) : -1;
                if (id < 0)
                {
                    Debug.LogError("[BuildYakuGuide] 牌が引けません '" + d + suit + "': " + owner + " / " + pattern);
                    continue;
                }
                plan.Add(id);
                ++added;
            }
            digits.Clear();
        }

        if (digits.Count > 0)
        {
            Debug.LogError("[BuildYakuGuide] 末尾に色の指定が無い数字があります: " + owner + " / " + pattern);
        }
        return added;
    }

    // ============================================================
    // 検証
    // ============================================================
    [MenuItem("Tools/Kemono Mahjong/Validate Yaku Guide")]
    public static void Validate()
    {
        var root = GameObject.Find(RootName);
        if (root == null)
        {
            Debug.LogError("[ValidateYakuGuide] " + RootName + " がシーンにありません。先に Build Yaku Guide を実行してください。");
            return;
        }

        var problems = new List<string>();
        var scrollHandle = root.transform.Find("Canvas/Panel/List/Scrollbar/Handle") as RectTransform;
        if (scrollHandle == null) { problems.Add("スクロールバーの Handle が無い"); }
        else if (scrollHandle.sizeDelta != Vector2.zero || scrollHandle.anchoredPosition != Vector2.zero)
        { problems.Add("スクロールバーの Handle に余分な寸法・位置オフセットがある"); }
        var guide = root.GetComponent<MahjongYakuGuide>();
        if (guide == null) { problems.Add("MahjongYakuGuide が付いていない"); }
        else
        {
            if (UdonSharpEditorUtility.GetBackingUdonBehaviour(guide) == null) { problems.Add("backing UdonBehaviour が無い"); }

            int n = guide.yakuTab != null ? guide.yakuTab.Length : 0;
            if (n == 0) { problems.Add("役データが空"); }
            if (n != YakuTable.Length) { problems.Add("役データが " + n + " 件（表は " + YakuTable.Length + " 件）＝Build を再実行"); }
            SameLength(problems, "yakuLocal", guide.yakuLocal != null ? guide.yakuLocal.Length : -1, n);
            SameLength(problems, "yakuNameJa", Len(guide.yakuNameJa), n);
            SameLength(problems, "yakuNameEn", Len(guide.yakuNameEn), n);
            SameLength(problems, "yakuBadgeJa", Len(guide.yakuBadgeJa), n);
            SameLength(problems, "yakuBadgeEn", Len(guide.yakuBadgeEn), n);
            SameLength(problems, "yakuDescJa", Len(guide.yakuDescJa), n);
            SameLength(problems, "yakuDescEn", Len(guide.yakuDescEn), n);
            SameLength(problems, "tilePlanStart", guide.tilePlanStart != null ? guide.tilePlanStart.Length : -1, n);
            SameLength(problems, "tilePlanCount", guide.tilePlanCount != null ? guide.tilePlanCount.Length : -1, n);

            if (guide.tileTexture == null) { problems.Add("tileTexture が未割当"); }
            if (guide.tileU == null || guide.tileU.Length != TileOrder.Length)
            {
                problems.Add("牌テーブルが " + TileOrder.Length + " 件でない");
            }

            // 牌並びの範囲チェック（範囲外の ID は実行時に配列外で止まる）。
            if (guide.tilePlan != null && guide.tilePlanStart != null && guide.tilePlanCount != null)
            {
                for (int i = 0; i < n && i < guide.tilePlanStart.Length; ++i)
                {
                    int s = guide.tilePlanStart[i];
                    int c = guide.tilePlanCount[i];
                    if (s < 0 || s + c > guide.tilePlan.Length) { problems.Add("tilePlan の範囲が壊れている（役 " + i + "）"); break; }
                    // 区切り（-1）は RawImage を消費しないので、牌の枚数だけを上限と突き合わせる。
                    int tiles = 0;
                    for (int k = 0; k < c; ++k)
                    {
                        int id = guide.tilePlan[s + k];
                        if (id == -1) { continue; }
                        ++tiles;
                        if (id < -2 || id >= TileOrder.Length) { problems.Add("牌IDが範囲外: " + guide.yakuNameJa[i]); break; }
                    }
                    if (tiles > MahjongYakuGuide.MAX_TILES)
                    {
                        problems.Add("牌が " + tiles + " 枚（上限 " + MahjongYakuGuide.MAX_TILES + "）: " + guide.yakuNameJa[i]);
                    }
                }
            }

            // 各タブに1件以上あるか（空タブは押しても何も出ない＝データ書き忘れの検出）。
            var count = new int[TabJa.Length];
            for (int i = 0; i < n; ++i)
            {
                int t = guide.yakuTab[i];
                if (t < 0 || t >= TabJa.Length) { problems.Add("タブ番号が範囲外: " + guide.yakuNameJa[i]); continue; }
                ++count[t];
            }
            for (int t = 0; t < TabJa.Length; ++t)
            {
                if (count[t] == 0) { problems.Add("タブ「" + TabJa[t] + "」が0件"); }
            }

            // 参照
            if (Len(guide.tabImages) != TabJa.Length) { problems.Add("tabImages が " + TabJa.Length + " 個でない"); }
            if (Len(guide.sectionImages) != SectionJa.Length) { problems.Add("sectionImages が " + SectionJa.Length + " 個でない"); }
            if (Len(guide.entryRoots) != MahjongYakuGuide.SLOT_COUNT) { problems.Add("entryRoots が " + MahjongYakuGuide.SLOT_COUNT + " 個でない"); }
            if (Len(guide.entryRects) != MahjongYakuGuide.SLOT_COUNT) { problems.Add("entryRects が " + MahjongYakuGuide.SLOT_COUNT + " 個でない"); }
            int wantTiles = MahjongYakuGuide.SLOT_COUNT * MahjongYakuGuide.MAX_TILES;
            if (Len(guide.entryTiles) != wantTiles) { problems.Add("entryTiles が " + wantTiles + " 個でない"); }
            else
            {
                for (int i = 0; i < guide.entryTiles.Length; ++i)
                {
                    if (guide.entryTiles[i] == null) { problems.Add("entryTiles[" + i + "] が null"); break; }
                }
            }
            if (guide.countLabel == null) { problems.Add("countLabel が未割当"); }
            if (guide.scrollRect == null) { problems.Add("scrollRect が未割当（スクロールしない）"); }
            if (guide.contentRect == null) { problems.Add("contentRect が未割当（スクロールしない）"); }
            if (guide.entryStep <= 0f) { problems.Add("entryStep が0以下"); }
            if (guide.viewportHeight <= 0f) { problems.Add("viewportHeight が0以下"); }
            if (guide.localLabel == null) { problems.Add("localLabel が未割当"); }
            if (guide.listRoot == null) { problems.Add("listRoot が未割当"); }
            if (guide.noticeRoot == null) { problems.Add("noticeRoot が未割当"); }
            if (guide.brandLabel == null) { problems.Add("brandLabel が未割当"); }
            if (guide.homeAnchor == null) { problems.Add("homeAnchor が未割当（OnReturnHome が効かない）"); }
            if (guide.pickup == null) { problems.Add("pickup が未割当"); }
            if (guide.languageManager == null) { problems.Add("languageManager が未割当（英語に切り替わらない）"); }

            // Udonヒープと proxy の突き合わせ（Prefab 保存などで巻き戻ると実行時だけ壊れる）。
            var ub = UdonSharpEditorUtility.GetBackingUdonBehaviour(guide);
            if (ub != null && ub.publicVariables != null)
            {
                object heapTab;
                if (ub.publicVariables.TryGetVariableValue("yakuTab", out heapTab))
                {
                    var arr = heapTab as int[];
                    if (arr == null || arr.Length != n) { problems.Add("Udonヒープの yakuTab が proxy と食い違う＝Wire をやり直す"); }
                }
            }
        }

        // 持ち運び
        var rb = root.GetComponent<Rigidbody>();
        if (rb == null) { problems.Add("Rigidbody が無い（掴めない）"); }
        else if (!rb.isKinematic) { problems.Add("Rigidbody が isKinematic でない（落とすと転がる）"); }
        if (root.GetComponent<VRC.SDK3.Components.VRCPickup>() == null) { problems.Add("VRCPickup が無い"); }
        var grip = root.GetComponent<BoxCollider>();
        if (grip == null) { problems.Add("掴み用 BoxCollider が無い"); }
        else
        {
            if (grip.isTrigger) { problems.Add("掴み用 BoxCollider がトリガー（掴めない）"); }
            // 画面より前に出ていると UI のレイを遮ってボタンが押せなくなる。必ず裏側だけに置く。
            if (grip.center.z - grip.size.z * 0.5f < 0.0005f)
            {
                problems.Add("掴み判定が画面より前にはみ出している（ボタンが押せなくなる）");
            }
            // 正面から確実に掴める「縁」が要る。画面と同じ大きさだと、
            // 画面のトリガーに邪魔されて正面から掴めない環境がありうる。
            float wantW = CanvasSize.x * CanvasScale + Bezel * 2f;
            if (grip.size.x < wantW - 0.001f) { problems.Add("掴み判定に縁（ベゼル）のはみ出しが無い"); }
        }
        if (root.GetComponent<VRC.SDK3.Components.VRCObjectSync>() != null)
        {
            problems.Add("VRCObjectSync が付いている（ローカル表示にならず全員で共有される）");
        }
        if (root.layer != LayerPickup)
        {
            problems.Add("ルートが Pickup 層でない（当たり判定がプレイヤーを押しのける）");
        }
        var body = root.transform.Find("Body");
        if (body == null) { problems.Add("Body（筐体の板）が無い"); }
        else
        {
            if (body.gameObject.layer != LayerPickup) { problems.Add("Body が Pickup 層でない"); }
            if (body.GetComponent<Collider>() != null) { problems.Add("Body に当たり判定が残っている（掴みが二重になる）"); }
        }
        if (root.transform.Find("Handle") != null) { problems.Add("旧「握り棒」Handle が残っている"); }

        // Canvas
        var canvas = root.transform.Find("Canvas");
        if (canvas == null) { problems.Add("Canvas が無い"); }
        else
        {
            var go = canvas.gameObject;
            if (go.GetComponent<VRC.SDK3.Components.VRCUiShape>() == null) { problems.Add("VRCUiShape が無い"); }
            if (go.GetComponent<GraphicRaycaster>() == null) { problems.Add("GraphicRaycaster が無い"); }
            var box = go.GetComponent<BoxCollider>();
            if (box == null) { problems.Add("Canvas に BoxCollider が無い（ClientSimで押せない）"); }
            else if (!box.isTrigger) { problems.Add("Canvas の BoxCollider がトリガーでない（持つと自分を押しのける）"); }

            var c = go.GetComponent<Canvas>();
            if (c == null || c.renderMode != RenderMode.WorldSpace) { problems.Add("World Space Canvas でない"); }
            var rt = canvas as RectTransform;
            if (rt != null && (rt.sizeDelta.x != CanvasSize.x || rt.sizeDelta.y != CanvasSize.y))
            {
                problems.Add("Canvas の大きさが " + CanvasSize.x + "x" + CanvasSize.y + " でない（" + rt.sizeDelta + "）");
            }
        }

        // ボタン
        var panel = root.transform.Find("Canvas/Panel");
        if (panel == null) { problems.Add("Panel が無い"); }
        else
        {
            var tabs = panel.Find("Tabs");
            for (int i = 0; i < TabJa.Length; ++i) { CheckButton(tabs, "Tab" + i, problems); }
            var side = panel.Find("Side");
            for (int i = 0; i < SectionJa.Length; ++i) { CheckButton(side, "BtnSec" + i, problems); }
            var list = panel.Find("List");
            if (list == null) { problems.Add("List が無い"); }
            else
            {
                var sr = list.GetComponent<ScrollRect>();
                if (sr == null) { problems.Add("List に ScrollRect が無い"); }
                else
                {
                    if (sr.viewport == null || sr.content == null) { problems.Add("ScrollRect の viewport/content が未割当"); }
                    if (sr.horizontal) { problems.Add("ScrollRect が横スクロール有効（縦だけにする）"); }
                    if (sr.verticalScrollbar == null) { problems.Add("縦スクロールバーが未割当"); }
                }
                if (list.Find("Viewport") == null || list.Find("Viewport").GetComponent<RectMask2D>() == null)
                {
                    problems.Add("Viewport / RectMask2D が無い（枠外にはみ出す）");
                }
                var content = list.Find("Viewport/Content") as RectTransform;
                if (content == null) { problems.Add("Content が無い"); }
                else
                {
                    if (content.pivot.y != 1f || content.anchorMin.y != 1f)
                    {
                        problems.Add("Content が上基準でない（スクロール量の計算がずれる）");
                    }
                    if (content.childCount != MahjongYakuGuide.SLOT_COUNT)
                    {
                        problems.Add("Content の Entry が " + content.childCount + " 個（" + MahjongYakuGuide.SLOT_COUNT + " 個であるべき）");
                    }
                }
            }

            var head = panel.Find("Head");
            CheckButton(head, "BtnUp", problems);
            CheckButton(head, "BtnDown", problems);
            CheckButton(head, "BtnLocal", problems);
        }

        // ワールド設定の操作
        var menu = GameObject.Find("MainMenu");
        var screen = menu != null ? menu.transform.Find("ScreenWorld/PageYaku") : null;
        if (screen == null)
        {
            problems.Add("MainMenu/ScreenWorld/PageYaku が無い（大きさ・元に戻すの操作を置けない）");
        }
        else
        {
            string[] names = { "BtnYakuS", "BtnYakuM", "BtnYakuL", "BtnYakuHome" };
            string[] events = { "OnSizeSmall", "OnSizeMedium", "OnSizeLarge", "OnReturnHome" };
            var udon = guide != null ? UdonSharpEditorUtility.GetBackingUdonBehaviour(guide) : null;
            for (int i = 0; i < names.Length; ++i)
            {
                var t = screen.Find(names[i]);
                if (t == null) { problems.Add("ScreenWorld/PageYaku/" + names[i] + " が無い"); continue; }
                var b = t.GetComponent<Button>();
                if (b == null) { problems.Add(names[i] + " に Button が無い"); continue; }

                bool wired = false;
                for (int k = 0; k < b.onClick.GetPersistentEventCount(); ++k)
                {
                    if (b.onClick.GetPersistentTarget(k) == (Object)udon
                        && b.onClick.GetPersistentMethodName(k) == "SendCustomEvent") { wired = true; break; }
                }
                if (!wired) { problems.Add(names[i] + " が MahjongYakuGuide の " + events[i] + " へ配線されていない"); }
            }
            string[] labels = { "YakuHead", "YakuNote", "YakuHomeHead", "YakuHomeNote" };
            foreach (var label in labels)
            {
                if (screen.Find(label) == null) { problems.Add("ScreenWorld/PageYaku/" + label + " が無い"); }
            }
        }

        if (guide != null)
        {
            if (Len(guide.sizeButtonImages) != MahjongYakuGuide.SIZE_COUNT)
            {
                problems.Add("sizeButtonImages が " + MahjongYakuGuide.SIZE_COUNT + " 個でない");
            }
            if (Len(guide.sizeScales) != MahjongYakuGuide.SIZE_COUNT)
            {
                problems.Add("sizeScales が " + MahjongYakuGuide.SIZE_COUNT + " 個でない");
            }
            else if (guide.sizeScales[0] != 1f)
            {
                problems.Add("小の倍率が 1.0 でない（小＝設計どおりの大きさが既定）");
            }
        }

        // 置き場所がシーンとビルダーの定数でズレていないか
        if ((root.transform.position - HomePosition).sqrMagnitude > 0.0001f)
        {
            problems.Add("シーンの位置 " + root.transform.position.ToString("F2")
                + " が BuildYakuGuide.HomePosition " + HomePosition.ToString("F2")
                + " と違う（次の Build で巻き戻る）");
        }

        // --- チュートリアル ---
        if (guide != null)
        {
            int pages = Len(guide.tutPageTitleJa);
            if (pages != TutorialTable.Length)
            {
                problems.Add("チュートリアルが " + pages + " ページ（表は " + TutorialTable.Length + " ページ）＝Build を再実行");
            }
            SameLength(problems, "tutPageTitleEn", Len(guide.tutPageTitleEn), pages);
            SameLength(problems, "tutPageStart", Len(guide.tutPageStart), pages);
            SameLength(problems, "tutPageCount", Len(guide.tutPageCount), pages);

            int blocks = Len(guide.tutBlockX);
            SameLength(problems, "tutBlockY", Len(guide.tutBlockY), blocks);
            SameLength(problems, "tutBlockW", Len(guide.tutBlockW), blocks);
            SameLength(problems, "tutBlockH", Len(guide.tutBlockH), blocks);
            SameLength(problems, "tutBlockHeadJa", Len(guide.tutBlockHeadJa), blocks);
            SameLength(problems, "tutBlockHeadEn", Len(guide.tutBlockHeadEn), blocks);
            SameLength(problems, "tutBlockBodyJa", Len(guide.tutBlockBodyJa), blocks);
            SameLength(problems, "tutBlockBodyEn", Len(guide.tutBlockBodyEn), blocks);
            SameLength(problems, "tutBlockTileH", Len(guide.tutBlockTileH), blocks);
            SameLength(problems, "tutTileStart", Len(guide.tutTileStart), blocks);
            SameLength(problems, "tutTileCount", Len(guide.tutTileCount), blocks);
            SameLength(problems, "tutBlockRoots", Len(guide.tutBlockRoots), MahjongYakuGuide.TUT_BLOCKS);
            SameLength(problems, "tutTiles", Len(guide.tutTiles),
                       MahjongYakuGuide.TUT_BLOCKS * MahjongYakuGuide.MAX_TILES);
            if (guide.tutorialRoot == null) { problems.Add("tutorialRoot が未割当"); }
            if (guide.tutPrevButton == null || guide.tutNextButton == null) { problems.Add("ページ送りボタンが未割当"); }
            if (guide.tutPageLabel == null) { problems.Add("ページ番号ラベルが未割当"); }
            SameLength(problems, "listOnlyObjects", Len(guide.listOnlyObjects), 5);

            // 箱が表示領域からはみ出していないか（矢印の下に潜り込むと読めない）。
            for (int i = 0; i < blocks; ++i)
            {
                float right = guide.tutBlockX[i] + guide.tutBlockW[i];
                float bottom = guide.tutBlockY[i] + guide.tutBlockH[i];
                if (right > TutAreaW + 0.01f) { problems.Add("ブロック " + i + " が右にはみ出す（右端 " + right + " > " + TutAreaW + "）"); }
                if (bottom > TutH + 0.01f) { problems.Add("ブロック " + i + " が下にはみ出す（下端 " + bottom + " > " + TutH + "）"); }
            }

            // 空ページ（矢印を押しても何も出ない）を弾く。
            for (int p = 0; p < TutorialTable.Length; ++p)
            {
                if (TutorialTable[p].blocks.Length == 0) { problems.Add("ページ " + (p + 1) + " が空"); }
            }

            // 本文が箱に収まるかを実測する。スクロールしない作りなので、
            // あふれた本文は下の牌や隣の箱に重なる（TMP は Overflow のまま描いてしまう）。
            // 実際に画面へ出しているラベルに measure させるので、フォント・字送りの設定ごと反映される。
            if (guide.tutBodyLabels != null && guide.tutBodyLabels.Length > 0 && guide.tutBodyLabels[0] != null)
            {
                var ruler = guide.tutBodyLabels[0];
                for (int p = 0; p < TutorialTable.Length; ++p)
                {
                    var page = TutorialTable[p];
                    for (int b = 0; b < page.blocks.Length; ++b)
                    {
                        var blk = page.blocks[b];
                        if (string.IsNullOrEmpty(blk.bodyJa)) { continue; }

                        bool hasHead = !string.IsNullOrEmpty(blk.headJa);
                        bool hasTiles = !string.IsNullOrEmpty(blk.pattern);
                        float th = blk.tileH > 0f ? blk.tileH : TileH;
                        float top = hasHead ? MahjongYakuGuide.TUT_HEAD_H + 12f : 10f;
                        float tileY = blk.h - th - 10f;
                        float boxH = (hasTiles ? tileY - 6f : blk.h - 8f) - top;
                        float boxW = blk.w - 24f;

                        float needJa = ruler.GetPreferredValues(blk.bodyJa, boxW, 0f).y;
                        float needEn = string.IsNullOrEmpty(blk.bodyEn)
                            ? 0f : ruler.GetPreferredValues(blk.bodyEn, boxW, 0f).y;
                        float need = Mathf.Max(needJa, needEn);

                        if (need > boxH + 1f)
                        {
                            problems.Add("ページ " + (p + 1) + "「" + page.titleJa + "」のブロック " + (b + 1) +
                                         " の本文が箱に入らない（必要 " + need.ToString("F0") +
                                         "px > 枠 " + boxH.ToString("F0") + "px）＝箱を高くするか文を短くする");
                        }
                    }
                }
            }
        }

        // --- 点数計算 ---
        if (guide != null)
        {
            if (guide.scoreRoot == null) { problems.Add("scoreRoot が未割当"); }
            SameLength(problems, "scorePages", Len(guide.scorePages), 3);
            SameLength(problems, "scoreTabImages", Len(guide.scoreTabImages), ScoreTabJa.Length);
            SameLength(problems, "scoreTabLabels", Len(guide.scoreTabLabels), ScoreTabJa.Length);
            SameLength(problems, "scoreTabNameJa", Len(guide.scoreTabNameJa), ScoreTabJa.Length);
            SameLength(problems, "scoreTabNameEn", Len(guide.scoreTabNameEn), ScoreTabJa.Length);

            int numScoreText = Len(guide.scoreTexts);
            if (numScoreText <= 0) { problems.Add("scoreTexts が空（Build を再実行）"); }
            SameLength(problems, "scoreTextJa", Len(guide.scoreTextJa), numScoreText);
            SameLength(problems, "scoreTextEn", Len(guide.scoreTextEn), numScoreText);

            if (guide.scoreTexts != null)
            {
                for (int i = 0; i < guide.scoreTexts.Length; ++i)
                {
                    if (guide.scoreTexts[i] == null) { problems.Add("scoreTexts[" + i + "] が空"); break; }
                }
            }
            if (guide.scorePages != null)
            {
                for (int i = 0; i < guide.scorePages.Length; ++i)
                {
                    if (guide.scorePages[i] == null) { problems.Add("scorePages[" + i + "] が空"); break; }
                }
            }

            var scorePanel = root.transform.Find("Canvas/Panel/Score");
            if (scorePanel == null) { problems.Add("Score が無い"); }
            else
            {
                for (int i = 0; i < ScoreTabJa.Length; ++i) { CheckButton(scorePanel, "ScoreTab" + i, problems); }
            }

            // 点数表の実測値（雀魂の早見表と一致すること）。
            CheckScore(problems, 30, 1, false, 1000, "300・500");
            CheckScore(problems, 30, 3, false, 3900, "1000・2000");
            CheckScore(problems, 20, 2, false, 0, "400・700");
            CheckScore(problems, 25, 4, false, 6400, "1600・3200");
            CheckScore(problems, 70, 2, false, 4500, "1200・2300");
            CheckScore(problems, 110, 2, false, 7100, "1800・3600");
            CheckScore(problems, 30, 5, false, 8000, "2000・4000");
            CheckScore(problems, 30, 13, false, 32000, "8000・16000");
            CheckScore(problems, 30, 3, true, 5800, "2000");
            CheckScore(problems, 90, 2, true, 8700, "2900");
            CheckScore(problems, 110, 2, true, 10600, "3600");
            CheckScore(problems, 30, 5, true, 12000, "4000");
            CheckScore(problems, 30, 13, true, 48000, "16000");

            // 満貫ブロックの階段（4翻は40符から・3翻は70符から）。
            if (ScIsMangan(30, 4) || !ScIsMangan(40, 4)) { problems.Add("満貫ブロック（4翻）の位置がおかしい"); }
            if (ScIsMangan(60, 3) || !ScIsMangan(70, 3)) { problems.Add("満貫ブロック（3翻）の位置がおかしい"); }
        }

        // 言語マネージャに登録されているか
        var lang = Object.FindObjectOfType<KemonoLanguage>(true);
        if (lang != null && guide != null)
        {
            bool found = false;
            if (lang.extraTargets != null)
            {
                foreach (var b in lang.extraTargets) { if (b == (UdonSharpBehaviour)guide) { found = true; break; } }
            }
            if (!found) { problems.Add("KemonoLanguage.extraTargets に登録されていない（英語に切り替わらない）"); }
        }

        // 韓国語の欠け（英語が出るだけで壊れはしないので警告止まり）。
        if (guide != null)
        {
            var koMissing = new List<string>();
            KoMissing(koMissing, guide.yakuNameEn, guide.yakuNameKo);
            KoMissing(koMissing, guide.yakuBadgeEn, guide.yakuBadgeKo);
            KoMissing(koMissing, guide.yakuDescEn, guide.yakuDescKo);
            KoMissing(koMissing, guide.tabNameEn, guide.tabNameKo);
            KoMissing(koMissing, guide.sectionNameEn, guide.sectionNameKo);
            KoMissing(koMissing, guide.scoreTabNameEn, guide.scoreTabNameKo);
            KoMissing(koMissing, guide.scoreTextEn, guide.scoreTextKo);
            KoMissing(koMissing, guide.tutPageTitleEn, guide.tutPageTitleKo);
            KoMissing(koMissing, guide.tutBlockHeadEn, guide.tutBlockHeadKo);
            KoMissing(koMissing, guide.tutBlockBodyEn, guide.tutBlockBodyKo);
            if (koMissing.Count > 0)
            {
                Debug.LogWarning("[ValidateYakuGuide] 韓国語なし " + koMissing.Count + " 件（英語で出る。KoreanText に足して Build を再実行）:\n - "
                                 + string.Join("\n - ", koMissing));
            }

            // 中国語の欠け（簡体字・繁体字は同じ表の2列なので、両方を数える）。
            var zhMissing = new List<string>();
            KoMissing(zhMissing, guide.yakuNameEn, guide.yakuNameZhs);
            KoMissing(zhMissing, guide.yakuBadgeEn, guide.yakuBadgeZhs);
            KoMissing(zhMissing, guide.yakuDescEn, guide.yakuDescZhs);
            KoMissing(zhMissing, guide.tabNameEn, guide.tabNameZhs);
            KoMissing(zhMissing, guide.sectionNameEn, guide.sectionNameZhs);
            KoMissing(zhMissing, guide.scoreTabNameEn, guide.scoreTabNameZhs);
            KoMissing(zhMissing, guide.scoreTextEn, guide.scoreTextZhs);
            KoMissing(zhMissing, guide.tutPageTitleEn, guide.tutPageTitleZhs);
            KoMissing(zhMissing, guide.tutBlockHeadEn, guide.tutBlockHeadZhs);
            KoMissing(zhMissing, guide.tutBlockBodyEn, guide.tutBlockBodyZhs);
            KoMissing(zhMissing, guide.yakuNameEn, guide.yakuNameZht);
            KoMissing(zhMissing, guide.yakuDescEn, guide.yakuDescZht);
            KoMissing(zhMissing, guide.tutBlockBodyEn, guide.tutBlockBodyZht);
            if (zhMissing.Count > 0)
            {
                Debug.LogWarning("[ValidateYakuGuide] 中国語なし " + zhMissing.Count + " 件（英語で出る。ChineseText に足して Build を再実行）:\n - "
                                 + string.Join("\n - ", zhMissing));
            }
        }

        if (problems.Count == 0)
        {
            int localCount = 0;
            for (int i = 0; i < YakuTable.Length; ++i) { if (YakuTable[i].local) { ++localCount; } }
            int tutBlocks = 0;
            for (int i = 0; i < TutorialTable.Length; ++i) { tutBlocks += TutorialTable[i].blocks.Length; }
            Debug.Log("[ValidateYakuGuide] OK: 役 " + YakuTable.Length + " 件（うちローカル役 " + localCount + " 件）／" +
                      "タブ " + TabJa.Length + " ／牌 " + TileOrder.Length + " 種／" +
                      "チュートリアル " + TutorialTable.Length + " ページ（ブロック " + tutBlocks + " 個）");
        }
        else
        {
            Debug.LogError("[ValidateYakuGuide] 問題 " + problems.Count + " 件:\n - " + string.Join("\n - ", problems));
        }
    }

    static int Len(System.Array a) { return a == null ? -1 : a.Length; }

    // 英語があるのに韓国語が空（または配列ごと無い）欄を集める。
    static void KoMissing(List<string> missing, string[] en, string[] ko)
    {
        if (en == null) { return; }
        for (int i = 0; i < en.Length; ++i)
        {
            if (string.IsNullOrEmpty(en[i])) { continue; }
            if (ko == null || i >= ko.Length || string.IsNullOrEmpty(ko[i])) { missing.Add(en[i]); }
        }
    }

    static void SameLength(List<string> problems, string name, int got, int want)
    {
        if (got != want) { problems.Add(name + " が " + got + " 件（" + want + " 件であるべき）"); }
    }

    static void CheckButton(Transform parent, string name, List<string> problems)
    {
        if (parent == null) { problems.Add(name + " の親が無い"); return; }
        var t = parent.Find(name);
        if (t == null) { problems.Add(name + " が無い"); return; }
        var button = t.GetComponent<Button>();
        if (button == null) { problems.Add(name + " に Button が無い"); return; }
        if (button.onClick.GetPersistentEventCount() != 1)
        {
            problems.Add(name + " の onClick が " + button.onClick.GetPersistentEventCount() + " 件");
            return;
        }
        if (button.onClick.GetPersistentMethodName(0) != "SendCustomEvent")
        {
            problems.Add(name + " の onClick が SendCustomEvent でない");
        }
    }

    // ============================================================
    // 生成ヘルパ
    // ============================================================
    static Transform Node(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null) { ++reused_; return t; }
        var go = new GameObject(name, typeof(RectTransform));
        Undo.RegisterCreatedObjectUndo(go, "Build Yaku Guide");
        go.transform.SetParent(parent, false);
        ++created_;
        return go.transform;
    }

    static Transform Node3D(Transform parent, string name)
    {
        Transform t = parent != null ? parent.Find(name) : null;
        if (t == null && parent == null)
        {
            var found = GameObject.Find(name);
            if (found != null) { t = found.transform; }
        }
        if (t != null) { ++reused_; return t; }

        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Build Yaku Guide");
        if (parent != null) { go.transform.SetParent(parent, false); }
        ++created_;
        return go.transform;
    }

    // 左上原点でのレイアウト。Canvas を 768x432 で固定しているので、
    // どの要素も「左から x、上から y」で読めるほうが図面と突き合わせやすい。
    static void TopLeft(Transform t, float x, float y, float w, float h)
    {
        var rt = (RectTransform)t;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
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
        image.sprite = null;
        image.color = color;
        image.raycastTarget = raycastTarget;
        return image;
    }

    static TextMeshProUGUI Tmp(Transform t, string text, float size, TextAlignmentOptions alignment, Color color, bool wrap)
    {
        var tmp = t.GetComponent<TextMeshProUGUI>();
        if (tmp == null) { tmp = Undo.AddComponent<TextMeshProUGUI>(t.gameObject); }
        if (font_ != null) { tmp.font = font_; }
        tmp.text = text;
        tmp.enableAutoSizing = false;
        tmp.fontSize = size;
        tmp.alignment = alignment;
        tmp.color = color;
        tmp.raycastTarget = false;
        tmp.enableWordWrapping = wrap;
        tmp.overflowMode = TextOverflowModes.Overflow;
        EditorUtility.SetDirty(tmp);
        return tmp;
    }

    static Transform Btn(Transform parent, string name, string label, float x, float y, float w, float h, float fontSize)
    {
        var t = Node(parent, name);
        TopLeft(t, x, y, w, h);
        var image = Img(t, StyleMainMenu.White, true);
        StyleMainMenu.Frame(image, 3f, 0f);

        var button = t.GetComponent<Button>();
        if (button == null) { button = Undo.AddComponent<Button>(t.gameObject); }
        button.targetGraphic = image;
        StyleMainMenu.ButtonTint(button);

        var labelT = Node(t, "Label");
        Stretch(labelT);
        Tmp(labelT, label, fontSize, TextAlignmentOptions.Center, StyleMainMenu.Ink, false);
        return t;
    }

    // 牌1枚ごとの縁取り。牌の絵はアトラスの「面」だけを切り出していて枠線が入らないため、
    // 白（模様なしの真っ白な牌）を3枚並べると1枚の白い長方形にしか見えない。
    // Outline は元の絵を4方向にずらして墨色で描き直すので、
    // 不透明なテクスチャに対しては 1px の枠線として働く（追加オブジェクトは0個）。
    static void TileBorder(RawImage raw)
    {
        var outline = raw.GetComponent<Outline>();
        if (outline == null) { outline = Undo.AddComponent<Outline>(raw.gameObject); }
        outline.effectColor = StyleMainMenu.Ink;
        outline.effectDistance = new Vector2(1.1f, 1.1f);
        outline.useGraphicAlpha = true;
        EditorUtility.SetDirty(outline);
    }

    // TMP の自動縮小。overflow のままだと「はみ出してよい」と解釈されて縮まないので、
    // Truncate に落としてから範囲を与える。
    static void AutoShrink(TextMeshProUGUI text, float max, float min)
    {
        if (text == null) { return; }
        text.overflowMode = TextOverflowModes.Truncate;
        text.enableWordWrapping = false;
        text.enableAutoSizing = true;
        text.fontSizeMax = max;
        text.fontSizeMin = min;
        text.fontSize = max;
        EditorUtility.SetDirty(text);
    }

    // 差し色は MainMenu と同じ考えだが、こちらのボタンは 42px 前後と小さい。
    // MainMenu 流の「左上に横棒」を置くと中央寄せのラベルに重なるので、
    // 左端の縦ストライプにして、ラベル側は同じ幅だけ内側へ寄せる。
    static void EdgeAccent(Transform button, Color color)
    {
        var t = button.Find("NeoAccent");
        if (t == null)
        {
            var go = new GameObject("NeoAccent", typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(go, "Build Yaku Guide");
            go.transform.SetParent(button, false);
            t = go.transform;
            ++created_;
        }
        else { ++reused_; }

        var rt = (RectTransform)t;
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 0.5f);
        rt.anchoredPosition = new Vector2(4f, 0f);
        rt.sizeDelta = new Vector2(5f, -10f);
        var img = t.GetComponent<Image>();
        if (img == null) { img = Undo.AddComponent<Image>(t.gameObject); }
        img.sprite = null;
        img.color = color;
        img.raycastTarget = false;

        // ラベルをストライプのぶん内側へ。中央寄せが左へ寄らないよう両側から詰める。
        var label = (RectTransform)button.Find("Label");
        label.offsetMin = new Vector2(13f, 0f);
        label.offsetMax = new Vector2(-13f, 0f);
    }

    static void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform) { SetLayerRecursively(child.gameObject, layer); }
    }

    // ============================================================
    // チュートリアルの表（このパネルの正）
    //
    // 1ページ＝1画面。スクロールしないので、**必ず 560 x 384 に収まる**ように
    // ブロックの矩形を置く（Validate がはみ出しを弾く）。
    // ブロックは「見出しチップ（任意）＋本文（任意）＋牌の並び（任意）」の箱。
    //   TB(x, y, 幅, 高さ, 見出し日, 見出し英, 本文日, 本文英, 牌姿)
    // 牌姿の記法は役の表と同じ（"123m 456p" / 0 は赤五 / z は 1東2南3西4北5白6發7中）。
    //
    // 文言は本ワールドの実装に合わせること（雀魂のルールとは違う箇所がある）。
    //   ・役は標準51件（ローカル役62件はフリー対局のトグル時のみ）
    //   ・三人打ちは 2〜8萬 を除いた108枚。赤五も筒・索の2枚だけになる
    //   ・立直は門前・聴牌・山の残りが人数分以上・持ち点1000以上
    // ============================================================
    struct TutBlock
    {
        public float x, y, w, h;
        public string headJa, headEn, bodyJa, bodyEn, pattern;
        public float tileH;      // 0 = 既定（34px）
        public bool ruby;        // true = 牌の下に読み方を出す（ページ1の牌の見本だけ）
    }

    struct TutPage
    {
        public string titleJa, titleEn;
        public TutBlock[] blocks;
    }

    static TutBlock TB(float x, float y, float w, float h,
                       string headJa, string headEn, string bodyJa, string bodyEn, string pattern,
                       float tileH = 0f, bool ruby = false)
    {
        var b = new TutBlock();
        b.x = x; b.y = y; b.w = w; b.h = h;
        b.headJa = headJa; b.headEn = headEn;
        b.bodyJa = bodyJa; b.bodyEn = bodyEn;
        b.pattern = pattern;
        b.tileH = tileH;
        b.ruby = ruby;
        return b;
    }

    static TutPage TP(string titleJa, string titleEn, params TutBlock[] blocks)
    {
        var p = new TutPage();
        p.titleJa = titleJa; p.titleEn = titleEn;
        p.blocks = blocks;
        return p;
    }

    static readonly TutPage[] TutorialTable =
    {
        // ==================== 1. 牌の種類 ====================
        TP("麻雀教室・牌の種類", "Mahjong Basics - The Tiles",
            TB(0f, 6f, 316f, 110f, "萬子（マンズ）", "Characters",
               "", "", "123456789m", 44f, true),
            TB(0f, 124f, 316f, 110f, "筒子（ピンズ）", "Circles",
               "", "", "123456789p", 44f, true),
            TB(0f, 242f, 316f, 110f, "索子（ソウズ）", "Bamboo",
               "", "", "123456789s", 44f, true),
            TB(328f, 6f, 232f, 110f, "風牌（フォンパイ）", "Winds",
               "", "", "1z 2z 3z 4z", 44f, true),
            TB(328f, 124f, 232f, 110f, "三元牌（サンゲンパイ）", "Dragons",
               "", "", "5z 6z 7z", 44f, true),
            TB(328f, 242f, 232f, 110f, "牌の数", "How many",
               "四人打ちは34種を4枚ずつ＝136枚。三人打ちは萬子の2〜8を抜いた108枚で打つ。",
               "Four-player: 34 kinds x4 = 136 tiles. Three-player drops characters 2-8: 108 tiles.",
               "")),

        // ==================== 2. 手牌と和了の形 ====================
        TP("麻雀教室・手牌と和了の形", "Mahjong Basics - Your Hand",
            TB(0f, 6f, 560f, 132f, "手牌", "Your hand",
               "配られる手牌は13枚。自分の番はツモって14枚にし、1枚捨てて13枚に戻す。",
               "You are dealt 13 tiles. On your turn you draw one, then discard one to go back to 13.",
               "345m 77p 234s 678s 11z", 40f),
            TB(0f, 150f, 176f, 110f, "順子（シュンツ）", "Run / Sequence",
               "", "", "123m", 40f),
            TB(192f, 150f, 176f, 110f, "刻子（コーツ）", "Triplet",
               "", "", "555p", 40f),
            TB(384f, 150f, 176f, 110f, "雀頭（アタマ）", "Pair",
               "", "", "22z", 40f),
            TB(0f, 272f, 560f, 106f, "和了（アガリ）", "Winning",
               "手牌13枚にツモ牌か他家の捨て牌を足した14枚が「4面子1雀頭」になれば和了。",
               "Win when your 13 tiles plus the drawn or discarded tile form four sets and one pair.",
               "123m 555p 234s 678s 22z")),

        // ==================== 3. 特別な和了形 ====================
        TP("麻雀教室・特別な和了形", "Mahjong Basics - Two Special Hands",
            TB(0f, 6f, 560f, 132f, "七対子（チートイツ）", "Seven Pairs",
               "対子7つでも和了できる。門前のみ・二翻。",
               "Seven different pairs also win. Closed hand only, 2 han.",
               "11m 44m 99m 33p 77p 22s 77z", 40f),
            TB(0f, 150f, 560f, 132f, "国士無双（コクシムソウ）", "Thirteen Orphans",
               "1と9と字牌を1枚ずつ集め、どれか1枚が対子。門前のみ・役満。",
               "One of each terminal and honour, with one of them paired. Closed only, yakuman.",
               "19m 19p 19s 1234567z 1z", 40f),
            TB(0f, 294f, 560f, 84f, "", "",
               "この2つだけは「4面子1雀頭」にならない特別な形。ほかの役はすべて4面子1雀頭で考える。",
               "These two are the only hands that do not follow the four-sets-and-a-pair shape.",
               "")),

        // ==================== 4. 局の進み方 ====================
        TP("麻雀教室・局の進み方", "Mahjong Basics - Flow of a Game",
            TB(0f, 6f, 272f, 110f, "親と子", "Dealer",
               "親は1人で、残りは子。親は和了ると続けて親ができ、点数も1.5倍になる。",
               "One player is the dealer. The dealer scores 1.5x and stays dealer after a win.",
               ""),
            TB(288f, 6f, 272f, 110f, "半荘と東風", "Match length",
               "段位戦は半荘（東場と南場）か東風（東場だけ）を選ぶ。フリー対局は一局戦もできる。",
               "Ranked play offers half games (East and South) or East-only games. Free play adds single hands.",
               ""),
            TB(0f, 126f, 272f, 110f, "手番", "Your turn",
               "自分の番はツモって1枚捨てる。捨てた牌は自分の河に並んでいく。",
               "Draw one tile and discard one. Your discards line up in front of you.",
               ""),
            TB(288f, 126f, 272f, 110f, "流局", "Exhaustive draw",
               "山が尽きたら流局。聴牌していれば受け取り、していなければ支払いになる。",
               "If the wall runs out, players in tenpai receive points and the rest pay.",
               ""),
            TB(0f, 246f, 560f, 110f, "順位", "Placement",
               "終わったときの持ち点で順位が決まる。段位戦は順位に応じて段位ポイントが増減する。",
               "Final scores decide placement. In ranked play your placement moves your rank points.",
               "")),

        // ==================== 5. 役とは ====================
        TP("麻雀教室・役とは", "Mahjong Basics - What is a Yaku",
            TB(0f, 6f, 560f, 100f, "役が要る", "You need a yaku",
               "形がそろっても「役」が1つも無いと和了できない。ドラは役ではないので、ドラだけでは和了れない。",
               "A finished shape is not enough: with no yaku you cannot win. Dora are not yaku.",
               ""),
            TB(0f, 118f, 560f, 100f, "このワールドの役", "Yaku in this world",
               "標準で51種。フリー対局で「ローカル役あり」を選ぶと、さらに62種が加わる。",
               "51 standard yaku. Free play with local yaku enabled adds 62 more.",
               ""),
            TB(0f, 230f, 560f, 112f, "まずはこの3つ", "Start with three",
               "立直・断幺九・役牌。この3つを覚えれば、たいていの手は和了れる。役の一覧は左の「役種一覧」でいつでも引ける。",
               "Riichi, Tanyao and Yakuhai will carry most hands. The Yaku List tab on the left has them all.",
               "")),

        // ==================== 6. 断幺九 ====================
        TP("麻雀教室・断幺九", "Mahjong Basics - Tanyao",
            TB(0f, 6f, 560f, 132f, "幺九牌（ヤオチューハイ）", "Terminals and honours",
               "数牌の1と9、風牌、三元牌をまとめて幺九牌と呼ぶ。",
               "The 1s and 9s, the winds and the dragons are called terminals and honours.",
               "19m 19p 19s 1234567z", 40f),
            TB(0f, 150f, 560f, 132f, "断幺九（タンヤオ）", "Tanyao",
               "幺九牌を1枚も使わずに和了すると成立する。一翻。鳴いても成立する。",
               "Win using only 2-8 tiles. Worth 1 han, and it still counts when your hand is open.",
               "234m 567m 345p 678s 22s", 40f),
            TB(0f, 294f, 560f, 84f, "", "",
               "初心者の味方。迷ったら2〜8の牌で手を作ると和了りやすい。",
               "The friendliest yaku: when in doubt, build your hand out of 2-8 tiles.",
               "")),

        // ==================== 7. 役牌 ====================
        TP("麻雀教室・役牌", "Mahjong Basics - Yakuhai",
            TB(0f, 6f, 560f, 96f, "役牌（ヤクハイ）", "Yakuhai",
               "決まった字牌を3枚そろえると、それだけで一翻の役になる。場風牌・自風牌・三元牌の3種類。",
               "A triplet of the right honour tile is a yaku by itself: round wind, seat wind, or a dragon.",
               ""),
            TB(0f, 114f, 272f, 132f, "場風牌", "Round wind",
               "東場なら東、南場なら南。",
               "East in the East round, South in the South round.",
               "111z", 40f),
            TB(288f, 114f, 272f, 132f, "自風牌", "Seat wind",
               "自分の席の風。東家は東、南家は南。",
               "The wind of your seat: East for the dealer, then South, West, North.",
               "222z", 40f),
            TB(0f, 258f, 560f, 120f, "三元牌", "Dragons",
               "白・發・中はどれも役牌。鳴いても一翻になる。",
               "White, Green and Red are always yakuhai, open or closed.",
               "555z 666z 777z", 40f)),

        // ==================== 8. 立直 ====================
        TP("麻雀教室・立直", "Mahjong Basics - Riichi",
            TB(0f, 6f, 560f, 132f, "聴牌（テンパイ）", "Tenpai",
               "あと1枚で和了できる形を聴牌という。この手は三筒か六筒で和了できる。",
               "Tenpai means you are one tile away. This hand waits on the 3 or 6 of circles.",
               "345m 678m 45p 345s 77z", 40f),
            TB(0f, 150f, 560f, 96f, "立直の条件", "Requirements",
               "門前（チー・ポン・大明カンをしていない）で聴牌、持ち点1000以上、山の残りが人数分以上。1000点を供託して宣言する。",
               "Closed hand, tenpai, at least 1000 points, and enough tiles left to draw again. You pay a 1000-point stick (riichi stick).",
               ""),
            TB(0f, 258f, 272f, 120f, "立直したら", "After declaring",
               "和了るまでツモ切り。ただし待ちの変わらないカンと、三人打ちのツモ北は抜ける。",
               "You must discard what you draw, except for a kan that keeps your wait, or a drawn North in 3-player.",
               ""),
            TB(288f, 258f, 272f, 120f, "立直の得", "What you gain",
               "一発と裏ドラが乗る。フリー対局ではオープン立直も選べる。",
               "You gain ippatsu and the under-dora (ura-dora). Free play can also enable Open Riichi.",
               "")),

        // ==================== 9. 鳴き ====================
        TP("麻雀教室・鳴き", "Mahjong Basics - Calling Tiles",
            TB(0f, 6f, 560f, 96f, "鳴き（副露）", "Calls",
               "他家の捨て牌を使って面子を作れる。手は早くなるが、門前が崩れて立直できなくなる。",
               "You may take a discard to complete a set. It speeds up your hand but you can no longer riichi.",
               ""),
            TB(0f, 114f, 176f, 132f, "チー", "Chi",
               "上家の捨て牌で順子。",
               "A run, from the player on your left.",
               "123m", 36f),
            TB(192f, 114f, 176f, 132f, "ポン", "Pon",
               "誰の捨て牌でも刻子。",
               "A triplet, from anyone.",
               "555p", 36f),
            TB(384f, 114f, 176f, 132f, "カン", "Kan",
               "4枚そろえる。嶺上牌をツモる。",
               "All four. You draw a replacement tile.",
               "7777s", 36f),
            TB(0f, 258f, 560f, 120f, "暗カン", "Closed kan",
               "手牌だけで4枚そろえたカンは暗カン。門前のままなので、立直していてもできる（待ちが変わらないときだけ）。",
               "A kan made entirely from your own hand keeps the hand closed, and is allowed after riichi if your wait does not change.",
               "")),

        // ==================== 10. 振り聴（捨て牌） ====================
        TP("麻雀教室・振り聴", "Mahjong Basics - Furiten",
            TB(0f, 6f, 560f, 96f, "振り聴（フリテン）", "Furiten",
               "自分の待ち牌が自分の捨て牌にあると振り聴。ロンできず、ツモでしか和了れない。",
               "If any of your winning tiles is in your own discards you are furiten: you can only win by self-draw.",
               ""),
            TB(0f, 114f, 560f, 132f, "自分の河", "Your discards",
               "待ちが三筒と六筒でも、河に三筒があれば振り聴。六筒で出和了することもできない。",
               "Waiting on 3 and 6 of circles, a 3 in your discards puts you in furiten for both.",
               "9m 1z 3p 5s 2z", 40f),
            TB(0f, 258f, 560f, 120f, "解けない", "It does not clear",
               "捨て牌による振り聴は、その牌が河にある限り続く。待ちを変えれば解ける。",
               "This kind of furiten lasts as long as the tile sits in your discards. It only clears once your winning tile changes.",
               "")),

        // ==================== 11. 振り聴（見逃し） ====================
        TP("麻雀教室・見逃しの振り聴", "Mahjong Basics - Missed Wins",
            TB(0f, 6f, 272f, 116f, "同巡内振り聴", "Temporary furiten",
               "聴牌後に他家の和了牌を見逃すと一時的に振り聴になる。自分が1枚捨てるまで続く。",
               "Passing on a winning discard makes you furiten until you discard once yourself.",
               ""),
            TB(288f, 6f, 272f, 116f, "立直後の振り聴", "Riichi furiten",
               "立直後に和了牌を見逃すと、その局のあいだ振り聴のままになる。取り消せない。",
               "Passing on a winning tile after riichi keeps you furiten for the rest of the hand.",
               ""),
            TB(0f, 134f, 560f, 100f, "画面の出かた", "Where it shows",
               "自分が振り聴のときは、待ち牌パネルの上と手牌の左端に「振り聴」と出る。",
               "When you are furiten the wait panel and the left edge of your hand both say so.",
               ""),
            TB(0f, 246f, 560f, 100f, "", "",
               "見逃しは高い手を狙うときだけ。基本は和了れるときに和了るのが安全。",
               "Only pass on a win when you are going for something bigger. Otherwise, take it.",
               "")),

        // ==================== 12. ドラ ====================
        TP("麻雀教室・ドラ", "Mahjong Basics - Dora",
            TB(0f, 6f, 560f, 124f, "ドラ", "Dora",
               "王牌に開いている表示牌の「次の牌」がドラ。1枚持つごとに一翻ぶん点数が上がる。",
               "The tile after the indicator is dora. Every dora you hold adds one han of value.",
               "5m 6m", 40f),
            TB(0f, 142f, 272f, 124f, "赤五", "Red fives",
               "五萬・五筒・五索の赤い牌。持っているだけでドラ1枚ぶん。",
               "The red five of each suit counts as one dora on its own.",
               "0m 0p 0s", 36f),
            TB(288f, 142f, 272f, 124f, "裏ドラ・抜きドラ", "More dora",
               "立直して和了ると裏ドラがめくれる。三人打ちの北抜きもドラになる。",
               "Winning after riichi reveals ura-dora. A set-aside North also counts.",
               ""),
            TB(0f, 278f, 560f, 100f, "", "",
               "ドラは役ではない。役が無い手は、ドラが何枚あっても和了できない。",
               "Dora are not yaku. Without a yaku you cannot win, no matter how many dora you hold.",
               "")),

        // ==================== 13. 三人打ち ====================
        TP("麻雀教室・三人打ち", "Mahjong Basics - Three-Player",
            TB(0f, 6f, 560f, 132f, "三人打ちの牌", "The tiles",
               "萬子の2〜8を使わない。牌は108枚で、赤五は筒子と索子の2枚。",
               "Characters 2-8 are removed, leaving 108 tiles and only two red fives.",
               "19m", 40f),
            TB(0f, 150f, 272f, 132f, "北は抜きドラ", "North is a bonus",
               "北は自風にならない。ツモった北は抜いてドラ1枚ぶんにできる。",
               "North is nobody's seat wind. Draw one and you may set it aside as a dora.",
               "4z", 40f),
            TB(288f, 150f, 272f, 132f, "席の風", "Seat winds",
               "自風は東・南・西の3つだけになる。",
               "Only East, South and West are seat winds.",
               "123z", 40f),
            TB(0f, 294f, 560f, 84f, "", "",
               "段位戦は四人打ちと三人打ちで待機列が分かれている。段位は共通。",
               "Ranked play queues four-player and three-player separately, but your rank is shared.",
               "")),

        // ==================== 14. 終わり ====================
        TP("麻雀教室・おわりに", "Mahjong Basics - You Are Ready",
            TB(0f, 6f, 560f, 110f, "ここまでで打てる", "That is enough to play",
               "牌の種類・和了の形・役・立直・鳴き・振り聴が分かれば、あとは打ちながら覚えられる。",
               "Tiles, shapes, yaku, riichi, calls and furiten are all you need. The rest comes from playing.",
               ""),
            TB(0f, 128f, 272f, 110f, "段位戦", "Ranked",
               "半荘か東風で対戦する。成績で段位が上下する。",
               "Half games or East-only games. Your results move your rank.",
               ""),
            TB(288f, 128f, 272f, 110f, "フリー対局", "Free play",
               "CPUとも打てる。ローカル役や青天井などのルールも選べる。",
               "Play with CPUs, and switch on rules like local yaku or no-limit scoring.",
               ""),
            TB(0f, 250f, 560f, 128f, "困ったら", "If you get stuck",
               "役の一覧は左の「役種一覧」で引ける。この板は掴んで持ち運べるので、卓の横に置いたまま打ってもよい。",
               "The Yaku List tab has every yaku. You can carry this panel with you and keep it beside your table.",
               "")),
    };

    // ============================================================
    // 役データ（このパネルの正）
    // ============================================================
    struct Entry
    {
        public int tab;
        public bool local;
        public string nameJa, nameEn, badgeJa, badgeEn, descJa, descEn, pattern;
    }

    static Entry E(int tab, bool local, string nameJa, string nameEn,
                   string badgeJa, string badgeEn, string descJa, string descEn, string pattern)
    {
        var e = new Entry();
        e.tab = tab; e.local = local;
        e.nameJa = nameJa; e.nameEn = nameEn;
        e.badgeJa = badgeJa; e.badgeEn = badgeEn;
        e.descJa = descJa; e.descEn = descEn;
        e.pattern = pattern;
        return e;
    }

    const string MenzenJa = "門前清の場合のみ";
    const string MenzenEn = "Closed hand only";
    const string Kui1Ja = "食い下がり一翻";
    const string Kui1En = "1 han when open";
    const string Kui2Ja = "食い下がり二翻";
    const string Kui2En = "2 han when open";
    const string Kui5Ja = "食い下がり五翻";
    const string Kui5En = "5 han when open";
    const string OpenReachJa = "オープン立直ルール時";
    const string OpenReachEn = "Open Riichi rule only";
    const string DrawJa = "流局";
    const string DrawEn = "Abortive draw";

    static readonly Entry[] YakuTable =
    {
        // ==================== 一翻 ====================
        E(0, false, "立直（リーチ）", "Riichi", MenzenJa, MenzenEn,
          "門前清で聴牌していて持ち点が1000以上あると宣言でき、1000点を供託して立直後に和了すると成立する",
          "Declare while closed and tenpai with at least 1000 points, paying a 1000-point stick. Scores if you win after it.",
          "111m 234p 678p 456s 99s"),
        E(0, false, "断幺九（タンヤオ）", "Tanyao", "", "",
          "幺九牌（一九字牌）を使わずに和了した場合に成立する",
          "Win with a hand that uses no terminals and no honor tiles.",
          "222m 345p 678p 456s 88s"),
        E(0, false, "門前清自摸和（ツモ）", "Menzen Tsumo", MenzenJa, MenzenEn,
          "門前かつツモ和了した場合に成立する",
          "Win by self-draw with a closed hand.",
          "111m 234p 678p 456s 99s"),
        E(0, false, "一発（イッパツ）", "Ippatsu", MenzenJa, MenzenEn,
          "立直の宣言後、次の自分のツモ番までに和了した場合に成立する（間に鳴きが入ると消える）",
          "Win within one go-around after declaring riichi. Any call by anyone cancels it.",
          "111m 234p 678p 456s 99s"),
        E(0, false, "平和（ピンフ）", "Pinfu", MenzenJa, MenzenEn,
          "門前で、4面子すべてが順子・雀頭が役牌でない・両面待ちの3条件を満たすと成立する",
          "Closed hand of four runs, a non-value pair, and a two-sided wait.",
          "234m 345p 456p 678s 99s"),
        E(0, false, "一盃口（イーペーコー）", "Iipeikou", MenzenJa, MenzenEn,
          "同じ色で同じ数字の順子が2組あった場合に成立する",
          "Two identical runs in the same suit, in a closed hand.",
          "234m 234m xxx xxx xx"),
        E(0, false, "槍槓（チャンカン）", "Chankan", "", "",
          "他家の加槓の牌でロン和了した場合に成立する（三麻の北抜きも搶槓の対象）",
          "Rob the tile another player adds to a pon to make a kan. In 3-player, a discarded North also counts.",
          "111m 234p 678p 456s 99s"),
        E(0, false, "嶺上開花（リンシャンカイホウ）", "Rinshan Kaihou", "", "",
          "槓をした後に引いた嶺上牌でツモ和了した場合に成立する",
          "Win on the replacement tile drawn after calling a kan.",
          "1111m 234p 567p 789s 99s"),
        E(0, false, "海底摸月（ハイテイ）", "Haitei", "", "",
          "牌山の最後の1枚を引いてツモ和了した場合に成立する",
          "Win by self-draw on the very last tile of the live wall.",
          "111m 234p 678p 456s 99s"),
        E(0, false, "河底撈魚（ホウテイ）", "Houtei", "", "",
          "その局の最後の捨て牌でロン和了した場合に成立する",
          "Win by ron on the final discard of the hand.",
          "111m 234p 678p 456s 99s"),
        E(0, false, "役牌：白（ハク）", "Yakuhai: Haku", "", "",
          "白を刻子または槓子にして和了した場合に成立する",
          "Win with a triplet or kan of White Dragon.",
          "555z xxx xxx xxx xx"),
        E(0, false, "役牌：發（ハツ）", "Yakuhai: Hatsu", "", "",
          "發を刻子または槓子にして和了した場合に成立する",
          "Win with a triplet or kan of Green Dragon.",
          "666z xxx xxx xxx xx"),
        E(0, false, "役牌：中（チュン）", "Yakuhai: Chun", "", "",
          "中を刻子または槓子にして和了した場合に成立する",
          "Win with a triplet or kan of Red Dragon.",
          "777z xxx xxx xxx xx"),
        E(0, false, "役牌：自風牌（ジカゼ）", "Yakuhai: Seat Wind", "", "",
          "自分の席の風牌を刻子または槓子にして和了した場合に成立する",
          "Win with a triplet or kan of your own seat wind.",
          "222z xxx xxx xxx xx"),
        E(0, false, "役牌：場風牌（バカゼ）", "Yakuhai: Round Wind", "", "",
          "その場の風牌を刻子または槓子にして和了した場合に成立する（自風と重なれば合計二翻）",
          "Win with a triplet or kan of the round wind. Stacks with the seat wind for 2 han.",
          "111z xxx xxx xxx xx"),
        E(0, true, "燕返し（ツバメガエシ）", "Tsubame-gaeshi", "", "",
          "他家の立直宣言牌でロン和了した場合に成立する",
          "Ron on the very tile another player discarded to declare riichi.",
          "111m 234p 678p 456s 99s"),
        E(0, true, "槓振り（カンブリ）", "Kanburi", "", "",
          "他家が槓をした直後の打牌でロン和了した場合に成立する",
          "Ron on the discard that immediately follows another player's kan.",
          "111m 234p 678p 456s 99s"),
        E(0, true, "十二落抬（シーアルラオタイ）", "Shiiaru Raotai", "", "",
          "4面子すべてを副露した裸単騎でロン和了した場合に成立する",
          "Ron on a single-tile wait after melding all four sets.",
          "111m 234p 678p 456s 99s"),
        E(0, true, "五心通貫（ウーシンツウカン）", "Uushin Tsuukan", "", "",
          "一気通貫を、同じ色の5の嵌張待ちで和了した場合に成立する",
          "Complete a pure straight on a closed wait for the 5 of that suit.",
          "123m 456m 789m 234p 55s"),
        E(0, true, "三元七対子（サンゲンチートイツ）", "Sangen Chiitoi", "", "",
          "七対子のうち、白・發・中の対子をすべて含む場合に一翻を上乗せする",
          "Adds 1 han when a Seven Pairs hand contains all three dragon pairs.",
          "xx xx xx xx 55z 66z 77z"),
        E(0, true, "四喜七対子（スーシーチートイツ）", "Suushii Chiitoi", "", "",
          "七対子のうち、東・南・西・北の対子をすべて含む場合に一翻を上乗せする",
          "Adds 1 han when a Seven Pairs hand contains all four wind pairs.",
          "xx xx xx 11z 22z 33z 44z"),
        E(0, true, "オープン立直（オープンリーチ）", "Open Riichi", OpenReachJa, OpenReachEn,
          "立直の宣言時に手牌を全員へ公開すると、立直の一翻に一翻を上乗せする",
          "Reveal your hand when declaring riichi to add 1 han on top of riichi.",
          "111m 234p 678p 456s 99s"),

        // ==================== 二翻 ====================
        E(1, false, "ダブル立直（ダブリー）", "Double Riichi", MenzenJa, MenzenEn,
          "誰の鳴きも入らないうちに、第一打で立直を宣言した場合に成立する",
          "Declare riichi on your first discard with no calls having interrupted the first go-around.",
          "111m 234p 678p 456s 99s"),
        E(1, false, "七対子（チートイツ）", "Chiitoitsu", MenzenJa, MenzenEn,
          "対子7組で和了した場合に成立する（同じ牌4枚は2組と数えない）",
          "Win with seven distinct pairs. Four of a kind does not count as two pairs.",
          "11m 44m 22p 66p 33s 88s 55z"),
        E(1, false, "三色同順（サンショクドウジュン）", "Sanshoku Doujun", Kui1Ja, Kui1En,
          "萬子・筒子・索子で同じ数字の順子をそろえた場合に成立する",
          "The same run in all three suits.",
          "234m 234p 234s xxx xx"),
        E(1, false, "一気通貫（イッキツウカン）", "Ittsuu", Kui1Ja, Kui1En,
          "同じ色で123・456・789の順子をそろえた場合に成立する",
          "123, 456 and 789 all in the same suit.",
          "123m 456m 789m xxx xx"),
        E(1, false, "混全帯幺九（チャンタ）", "Chanta", Kui1Ja, Kui1En,
          "4面子1雀頭のすべてに幺九牌（一九字牌）が関わっていて、字牌を1つ以上含む場合に成立する",
          "Every set and the pair contains a terminal or honor, with at least one honor.",
          "111m 789p 123s 999s 22z"),
        E(1, false, "対々和（トイトイ）", "Toitoi", "", "",
          "4面子すべてを刻子または槓子でそろえた場合に成立する",
          "All four sets are triplets or kans.",
          "111m 444p 888s 666m xx"),
        E(1, false, "三暗刻（サンアンコウ）", "San Ankou", "", "",
          "暗刻または暗槓を3つそろえた場合に成立する（ロンで完成した刻子は明刻扱い）",
          "Three concealed triplets or kans. A triplet completed by ron counts as open.",
          "111m 444p 888s xxx xx"),
        E(1, false, "三色同刻（サンショクドウコウ）", "Sanshoku Doukou", "", "",
          "萬子・筒子・索子で同じ数字の刻子をそろえた場合に成立する",
          "The same numbered triplet in all three suits.",
          "555m 555p 555s xxx xx"),
        E(1, false, "三槓子（サンカンツ）", "San Kantsu", "", "",
          "1人で槓を3回して和了した場合に成立する",
          "Call three kans yourself and win.",
          "1111m 4444p 8888s xxx xx"),
        E(1, false, "混老頭（ホンロウトウ）", "Honroutou", "", "",
          "幺九牌（一九字牌）だけで和了した場合に成立する",
          "The hand contains only terminals and honors.",
          "11m 99m 11p 99s 11z 33z 55z"),
        E(1, false, "小三元（ショウサンゲン）", "Shousangen", "", "",
          "三元牌のうち2種類を刻子、残る1種類を雀頭にした場合に成立する",
          "Two dragon triplets plus a pair of the third dragon.",
          "555z 666z 77z xxx xxx"),
        E(1, true, "五門斉（ウーメンチー）", "Uumenchii", "", "",
          "萬子・筒子・索子・風牌・三元牌の5種類をすべて使って和了した場合に成立する",
          "Use all five families: characters, circles, bamboo, winds and dragons.",
          "123m 456p 789s 111z 55z"),
        E(1, true, "三連刻（サンレンコウ）", "San Renkou", "", "",
          "同じ色で数字が連続する刻子を3つそろえた場合に成立する",
          "Three triplets of consecutive numbers in one suit.",
          "222m 333m 444m xxx xx"),
        E(1, true, "客風三刻（オタカゼサンコー）", "Otakaze Sankou", "", "",
          "自風でも場風でもない風牌の刻子を3つそろえた場合に成立する",
          "Three triplets of winds that are neither your seat wind nor the round wind.",
          "222z 333z 444z xxx xx"),
        E(1, true, "三風刻（サンプウコー）", "Sanpuukou", "", "",
          "風牌4種類のうち3種類を刻子にした場合に成立する（雀頭が風牌なら小四喜）",
          "Triplets of three of the four winds. With a wind pair it becomes Shousuushii instead.",
          "111z 222z 333z xxx xx"),
        E(1, true, "二同刻（リャンドーコー）", "Ryandoukou", "", "",
          "2種類の数牌で同じ数字の刻子をそろえた場合に成立する（3色そろえば三色同刻）",
          "The same numbered triplet in two suits. Three suits makes Sanshoku Doukou instead.",
          "333m 333p xxx xxx xx"),
        E(1, true, "三色通貫（サンショクツウカン）", "Sanshoku Tsuukan", Kui1Ja, Kui1En,
          "123・456・789を3色に1つずつ分けてそろえた場合に成立する",
          "123, 456 and 789 spread one to each suit.",
          "123m 456p 789s xxx xx"),
        E(1, true, "三色連刻（サンショクレンコウ）", "Sanshoku Renkou", "", "",
          "連続する3つの数字の刻子を、3色に1つずつ分けてそろえた場合に成立する",
          "Three consecutive numbered triplets, one in each suit.",
          "222m 333p 444s xxx xx"),
        E(1, true, "鏡同和（キャンドンホー）", "Kyandonhou", "", "",
          "2種類の数牌で同じ順子をそろえた場合に成立する（3色そろえば三色同順）",
          "The same run in two suits. Three suits makes Sanshoku Doujun instead.",
          "345m 345p xxx xxx xx"),
        E(1, true, "小タテ（ショータテ）", "Shoutate", "", "",
          "同じ数字の刻子を2つそろえ、残る1色の同じ数字を雀頭にした場合に成立する",
          "Two triplets of the same number plus a pair of that number in the remaining suit.",
          "333m 333p 33s xxx xxx"),
        E(1, true, "連開花（レンカイホウ）", "Renkaihou", "", "",
          "打牌を挟まずに2回以上続けて槓をし、その嶺上牌でツモ和了した場合に成立する",
          "Win on the replacement tile after two or more kans called back to back.",
          "1111m 2222p 345s 678s 99m"),
        E(1, true, "頂三刻（チョウサンコウ）", "Chousankou", "", "",
          "同じ色の1・5・9の刻子をそろえた場合に成立する",
          "Triplets of 1, 5 and 9 in the same suit.",
          "111m 555m 999m xxx xx"),
        E(1, true, "筋牌刻（スジパイコー）", "Sujipaikou", "", "",
          "同じ色で1-4-7・2-5-8・3-6-9のいずれかの刻子を3つそろえた場合に成立する",
          "Three triplets on one suji line (1-4-7, 2-5-8 or 3-6-9) in the same suit.",
          "111m 444m 777m xxx xx"),
        E(1, true, "跳牌刻（チョウパイコー）", "Choupaikou", "", "",
          "同じ色で1つ飛ばしの数字の刻子を3つそろえた場合に成立する",
          "Three triplets two numbers apart in the same suit.",
          "222m 444m 666m xxx xx"),

        // ==================== 三翻 ====================
        E(2, false, "二盃口（リャンペーコー）", "Ryanpeikou", MenzenJa, MenzenEn,
          "一盃口が2つあった場合に成立する",
          "Two sets of Iipeikou in one closed hand.",
          "234m 234m 567p 567p xx"),
        E(2, false, "純全帯幺九（ジュンチャン）", "Junchan", Kui2Ja, Kui2En,
          "4面子1雀頭のすべてに老頭牌（一九牌）が関わっていた場合に成立する",
          "Every set and the pair contains a terminal, with no honors at all.",
          "111m 789m 123p 999s 11s"),
        E(2, false, "混一色（ホンイツ）", "Honitsu", Kui2Ja, Kui2En,
          "萬子・索子・筒子のどれか一種と、字牌だけを使って和了した場合に成立する",
          "One suit plus honors only.",
          "111m 234m 567m 789m 55z"),
        E(2, true, "一色三順（イッショクサンジュン）", "Isshoku Sanjun", Kui2Ja, Kui2En,
          "同じ色で同じ数字の順子を3組そろえた場合に成立する（一盃口・二盃口は取り下げる）",
          "Three identical runs in one suit. Replaces Iipeikou / Ryanpeikou.",
          "234m 234m 234m xxx xx"),
        E(2, true, "清盃口（チンペーコー）", "Chinpeekou", MenzenJa, MenzenEn,
          "二盃口の2組が、色ちがいで同じ数字だった場合に成立する（二盃口は取り下げる）",
          "Ryanpeikou where the two doubled runs share the same numbers in different suits.",
          "234m 234m 234p 234p xx"),
        E(2, true, "単竜（タンロン）", "Tanron", MenzenJa, MenzenEn,
          "三色通貫を単騎待ちで和了した場合に成立する",
          "Complete Sanshoku Tsuukan on a single-tile wait, closed.",
          "123m 456p 789s 234m 99s"),
        E(2, true, "断紅和（タンフォンホー）", "Tanfonhou", Kui2Ja, Kui2En,
          "赤色をいっさい含まない牌（索子の2/3/4/6/8・筒子の2/4/8・白・發）だけで和了した場合に成立する",
          "Use only tiles with no red ink: 2/3/4/6/8 of bamboo, 2/4/8 of circles, White and Green.",
          "234s 888s 222p 444p 55z"),

        // ==================== 六翻 ====================
        E(3, false, "清一色（チンイツ）", "Chinitsu", Kui5Ja, Kui5En,
          "萬子・索子・筒子のどれか一種だけを使って和了した場合に成立する",
          "The whole hand is a single suit, with no honors.",
          "111m 234m 567m 888m 99m"),

        // ==================== 満貫 ====================
        E(4, false, "流し満貫（ナガシマンガン）", "Nagashi Mangan", DrawJa, DrawEn,
          "流局時に、自分の捨て牌がすべて幺九牌で他家に1枚もポン・チー・カンをされなかった場合に成立する",
          "At an exhaustive draw, every one of your discards is a terminal or honor and none was called.",
          "91m1122z7z2333z111p1s6z9m"),
        E(4, true, "人和（レンホウ）", "Renhou", "", "",
          "子が、自分の第一ツモより前にロン和了した場合に成立する（鳴き・槓・北抜きが入ると不成立）",
          "A non-dealer wins by ron before drawing their first tile. Any call breaks it.",
          "111m 234p 678p 456s 99s"),
        E(4, true, "真似満（マネマン）", "Maneman", "", "",
          "子が親の捨て牌を第一打からそっくり同じ順序で真似たまま和了した場合に成立する",
          "A non-dealer mirrors the dealer's discards in the same order from the first discard, then wins.",
          "111m 234p 678p 456s 99s"),
        E(4, true, "一筒摸月（イーピンモーユエ）", "Ii-pin Mou-yue", "", "",
          "海底で引いたツモ和了牌が一筒だった場合に成立する",
          "The last tile of the wall, drawn for the win, is the 1 of circles.",
          "222m 456p 234s 678s 11p"),
        E(4, true, "九筒撈魚（チューピンラオユイ）", "Chuu-pin Rao-yu", "", "",
          "河底で打牌された九筒でロン和了した場合に成立する",
          "Ron on a 9 of circles discarded as the final tile of the hand.",
          "222m 456p 234s 678s 99p"),
        E(4, true, "五筒開花（ウーピンカイホウ）", "Uu-pin Kaihou", "", "",
          "嶺上牌で引いたツモ和了牌が五筒だった場合に成立する",
          "The replacement tile after a kan is the 5 of circles, and it wins.",
          "222m 456p 234s 678s 55p"),
        E(4, true, "二索槍槓（リャンゾーチャンカン）", "Ryanzou Chankan", "", "",
          "搶槓で和了した牌が二索だった場合に成立する",
          "Rob a kan of the 2 of bamboo.",
          "222m 456p 234s 678s 22s"),
        E(4, true, "金鶏独立（キンケイドクリツ）", "Kinkei Dokuritsu", "", "",
          "4面子すべてを副露した裸単騎で、一索を和了牌にした場合に成立する（暗槓を含むと不成立）",
          "All four sets melded, waiting alone on the 1 of bamboo. A concealed kan breaks it.",
          "111z 222z 333z 555z 1s"),
        E(4, true, "独釣寒江雪（トウチャオハンチャンシュエ）", "Touchao Hanchanshue", "", "",
          "4面子すべてを副露した裸単騎で、白を和了牌にした場合に成立する（暗槓を含むと不成立）",
          "All four sets melded, waiting alone on the White Dragon. A concealed kan breaks it.",
          "111z 222z 333z 444z 5z"),
        E(4, true, "双竜争珠（シャンロンチョンチュ）", "Shanron Chonchu", "", "",
          "雀頭を筒子にし、萬子と索子で同じ数字の順子をそれぞれ2組そろえた場合に成立する",
          "A circles pair, plus two identical runs in characters and two in bamboo, all on the same numbers.",
          "234m 234m 234s 234s 55p"),

        // ==================== 倍満 ====================
        E(5, true, "小車輪（ショウシャリン）", "Shou Sharin", "", "",
          "筒子の1〜7を2枚ずつ、または3〜9を2枚ずつそろえた場合に成立する",
          "Pairs of 1-7, or of 3-9, all in circles.",
          "11223344556677p"),
        E(5, true, "小竹林（ショウチクリン）", "Shou Chikurin", "", "",
          "索子の1〜7を2枚ずつ、または3〜9を2枚ずつそろえた場合に成立する",
          "Pairs of 1-7, or of 3-9, all in bamboo.",
          "11223344556677s"),
        E(5, true, "小数隣（ショウスウリン）", "Shou Suurin", "", "",
          "萬子の1〜7を2枚ずつ、または3〜9を2枚ずつそろえた場合に成立する",
          "Pairs of 1-7, or of 3-9, all in characters.",
          "11223344556677m"),

        // ==================== 役満 ====================
        E(6, false, "天和（テンホー）", "Tenhou", MenzenJa, MenzenEn,
          "親が配牌の時点で和了形だった場合に成立する",
          "The dealer's starting hand is already complete.",
          "111m 234p 678p 456s 99s"),
        E(6, false, "地和（チーホー）", "Chiihou", MenzenJa, MenzenEn,
          "子が第一ツモで和了した場合に成立する（それまでに誰の鳴きも入っていないこと）",
          "A non-dealer wins on their first draw with no calls before it.",
          "111m 234p 678p 456s 99s"),
        E(6, false, "大三元（ダイサンゲン）", "Daisangen", "", "",
          "白・發・中をすべて刻子または槓子にした場合に成立する",
          "Triplets or kans of all three dragons.",
          "555z 666z 777z xxx xx"),
        E(6, false, "四暗刻（スーアンコウ）", "Suu Ankou", MenzenJa, MenzenEn,
          "暗刻または暗槓を4つそろえた場合に成立する（ロンで4つ目が完成した場合は三暗刻）",
          "Four concealed triplets. A fourth completed by ron scores only San Ankou.",
          "111m 444m 888p 999s xx"),
        E(6, false, "字一色（ツーイーソー）", "Tsuuiisou", "", "",
          "字牌だけで和了した場合に成立する",
          "The whole hand is honor tiles.",
          "111z 222z 555z 777z 33z"),
        E(6, false, "緑一色（リューイーソー）", "Ryuuiisou", "", "",
          "索子の2・3・4・6・8と發だけで和了した場合に成立する（發がなくてもよい）",
          "Only green tiles: 2, 3, 4, 6, 8 of bamboo and the Green Dragon. The Green Dragon is optional.",
          "234s 666s 888s 222s 66z"),
        E(6, false, "清老頭（チンロウトウ）", "Chinroutou", "", "",
          "老頭牌（一九牌）だけで和了した場合に成立する",
          "The whole hand is terminals only.",
          "111m 999m 111p 999s 99p"),
        E(6, false, "国士無双（コクシムソウ）", "Kokushi Musou", MenzenJa, MenzenEn,
          "幺九牌13種をすべて1枚ずつ集め、そのうち1種類が対子になった場合に成立する",
          "All thirteen terminals and honors, one of them paired.",
          "119m19p19s1234567z"),
        E(6, false, "小四喜（ショウスーシー）", "Shousuushii", "", "",
          "風牌4種のうち3種を刻子、残る1種を雀頭にした場合に成立する",
          "Triplets of three winds plus a pair of the fourth.",
          "111z 222z 333z xxx 44z"),
        E(6, false, "四槓子（スーカンツ）", "Suu Kantsu", "", "",
          "1人で槓を4回して和了した場合に成立する",
          "Call all four kans yourself and win.",
          "1111m 4444p 8888s 7777z xx"),
        E(6, false, "九蓮宝燈（チューレンポートー）", "Chuuren Poutou", MenzenJa, MenzenEn,
          "同じ色の一・九を各3枚、二〜八を各1枚含む14枚で和了すると成立する（例は右端の九萬で和了）",
          "A 14-tile hand in one suit containing 1112345678999. In this example, win on the final 9m.",
          "1112334567899m 9m"),
        E(6, true, "一色四順（イッショクヨンジュン）", "Isshoku Yonjun", "", "",
          "同じ色で同じ数字の順子を4組そろえた場合に成立する",
          "Four identical runs in one suit.",
          "234m 234m 234m 234m xx"),
        E(6, true, "四連刻（スーレンコウ）", "Suu Renkou", "", "",
          "同じ色で数字が連続する刻子を4つそろえた場合に成立する",
          "Four triplets of consecutive numbers in one suit.",
          "222m 333m 444m 555m xx"),
        E(6, true, "大車輪（ダイシャリン）", "Dai Sharin", "", "",
          "筒子の2〜8を2枚ずつそろえた場合に成立する",
          "Pairs of 2 through 8, all in circles.",
          "22334455667788p"),
        E(6, true, "大竹林（ダイチクリン）", "Dai Chikurin", "", "",
          "索子の2〜8を2枚ずつそろえた場合に成立する",
          "Pairs of 2 through 8, all in bamboo.",
          "22334455667788s"),
        E(6, true, "大数隣（ダイスウリン）", "Dai Suurin", "", "",
          "萬子の2〜8を2枚ずつそろえた場合に成立する",
          "Pairs of 2 through 8, all in characters.",
          "22334455667788m"),
        E(6, true, "紅孔雀（ベニクジャク）", "Beni Kujaku", "", "",
          "索子の1・5・7・9と中だけで和了した場合に成立する",
          "Only 1, 5, 7, 9 of bamboo and the Red Dragon.",
          "111s 555s 777s 999s 77z"),
        E(6, true, "黒一色（ヘイイーソー）", "Hei Iisou", "", "",
          "筒子の2・4・8と風牌だけで和了した場合に成立する",
          "Only 2, 4, 8 of circles and wind tiles.",
          "222p 444p 888p 111z 22z"),
        E(6, true, "百万石（ヒャクマンゴク）", "Hyakumangoku", "", "",
          "萬子だけで和了し、数字の合計が100以上になった場合に成立する",
          "A characters-only hand whose digits add up to 100 or more.",
          "999m 888m 666m 567m 77m"),
        E(6, true, "四字刻（スーツーコー）", "Suutsuukou", "", "",
          "字牌の刻子を4つそろえ、雀頭を数牌にした場合に成立する（雀頭も字牌なら字一色）",
          "Four honor triplets with a numbered pair. An honor pair makes it Tsuuiisou instead.",
          "111z 222z 555z 777z 99m"),
        E(6, true, "八連荘（パーレンチャン）", "Paarenchan", "", "",
          "同じ人が8回続けて和了した、その8回目に成立する（手牌の中身は問わない）",
          "The eighth consecutive win by the same player, whatever the hand.",
          "111m 234p 678p 456s 99s"),
        E(6, true, "破回八連荘（ポーホイパーレンチャン）", "Poohoi Paarenchan", "", "",
          "他家の八連荘を、その直前で止めた和了に成立する（手牌の中身は問わない）",
          "The win that stops someone else's Paarenchan on its final step.",
          "111m 234p 678p 456s 99s"),
        E(6, true, "十三不塔（シーサンプートー）", "Shiisan Puutou", MenzenJa, MenzenEn,
          "配牌または第一ツモの時点で、対子1組のほかに面子も搭子も無い場合に成立する",
          "At the first draw, the hand holds exactly one pair and no other partial sets.",
          "147m258p369s135z77z"),
        E(6, true, "十三無靠（シーサンウーシー）", "Shiisan Wuushii", MenzenJa, MenzenEn,
          "配牌または第一ツモの時点で、対子も含めてまったくつながりが無い場合に成立する",
          "At the first draw, no pair and no partial set anywhere in the hand.",
          "147m258p369s12356z"),
        E(6, true, "七星無靠（チーシンウーシー）", "Chiishin Wuushii", MenzenJa, MenzenEn,
          "十三無靠のうち、字牌7種をすべて含む場合に成立する",
          "Shiisan Wuukou that also contains all seven honor tiles.",
          "147m258p3s1234567z"),
        E(6, true, "頭槓和（トウカンホー）", "Toukanhou", "", "",
          "第一巡目に槓をして、その嶺上牌で和了した場合に成立する",
          "Call a kan during the first go-around and win on the replacement tile.",
          "1111m 234p 567p 789s 99s"),
        E(6, true, "東北新幹線（トウホクシンカンセン）", "Touhoku Shinkansen", "", "",
          "東と北を刻子と雀頭にし、残る3面子で一気通貫をそろえた場合に成立する",
          "East and North as a triplet and a pair, with a pure straight in the remaining sets.",
          "123p 456p 789p 111z 44z"),
        E(6, true, "東北自動車道（トウホクジドウシャドウ）", "Touhoku Jidoushadou", "", "",
          "二筒・四筒・六筒・東・北の5種類だけで対々和をそろえた場合に成立する",
          "An all-triplets hand built only from 2, 4, 6 of circles plus East and North.",
          "222p 444p 666p 111z 44z"),
        E(6, true, "青ノ洞門（アオノドウモン）", "Ao no Doumon", "", "",
          "風牌1種類・筒子の2/4/8・發だけで対々和をそろえた場合に成立する",
          "An all-triplets hand from a single wind, 2/4/8 of circles and the Green Dragon.",
          "222p 444p 888p 111z 66z"),
        E(6, true, "金門橋（キンモンキョウ）", "Kinmonkyou", "", "",
          "同じ色で123・345・567・789の4順子をそろえた場合に成立する",
          "123, 345, 567 and 789 all in the same suit.",
          "123m 345m 567m 789m xx"),
        E(6, true, "紅一点（コウイッテン）", "Kou Itten", "", "",
          "緑一色の發を中に置き替えた形（索子の2・3・4・6・8と中）で和了した場合に成立する",
          "Ryuuiisou with the Green Dragon swapped for the Red one.",
          "234s 234s 666s 888s 77z"),
        E(6, true, "緑一色輪（リューイーソーリン）", "Ryuuiisou Rin", "", "",
          "緑一色の雀頭を五索に置き替えた形で和了した場合に成立する（緑一色は取り下げる）",
          "Ryuuiisou whose pair is the 5 of bamboo. Replaces Ryuuiisou.",
          "234s 234s 666s 888s 55s"),
        E(6, true, "風花雪月（フウカセツゲツ）", "Fuuka Setsugetsu", "", "",
          "自風または場風の風牌・五筒・白・一筒をすべて刻子にした場合に成立する",
          "Triplets of a value wind, the 5 of circles, White Dragon and the 1 of circles.",
          "111z 555p 555z 111p xx"),
        E(6, true, "花鳥風月（カチョウフウゲツ）", "Kachou Fuugetsu", "", "",
          "自風または場風の風牌・五筒・一索・一筒をすべて刻子にした場合に成立する",
          "Triplets of a value wind, the 5 of circles, 1 of bamboo and the 1 of circles.",
          "111z 555p 111s 111p xx"),
        E(6, true, "南北戦争（ナンボクセンソウ）", "Nanboku Sensou", MenzenJa, MenzenEn,
          "南3枚・北3枚に加え、ある色の1・1・6・8と別の色の1・5・6・8をそろえた場合に成立する",
          "Three South, three North, plus 1-1-6-8 in one suit and 1-5-6-8 in another.",
          "222z 444z 1168m 1568p"),
        E(6, true, "オープン立直放銃（オープンリーチホウジュウ）", "Dealing into Open Riichi", OpenReachJa, OpenReachEn,
          "オープン立直の和了牌を放銃した場合、放銃者が役満を払う（放銃者自身が立直していれば免除）",
          "Deal the winning tile into an Open Riichi and you pay a yakuman. Exempt if you had declared riichi too.",
          "111m 234p 678p 456s 99s"),

        // ==================== ダブル役満 ====================
        E(7, false, "四暗刻単騎（スーアンコウタンキ）", "Suu Ankou Tanki", MenzenJa, MenzenEn,
          "四暗刻を単騎待ちで和了した場合に成立する",
          "Suu Ankou completed on a single-tile wait.",
          "111m 444m 888p 999s 22s"),
        E(7, false, "国士無双十三面待ち（コクシムソウジュウサンメン）", "Kokushi 13-Wait", MenzenJa, MenzenEn,
          "幺九牌13種を1枚ずつそろえた状態から和了した場合に成立する",
          "Kokushi Musou waiting on all thirteen tiles.",
          "19m19p19s1234567z 1m"),
        E(7, false, "純正九蓮宝燈（ジュンセイチューレンポートー）", "Junsei Chuuren", MenzenJa, MenzenEn,
          "九蓮宝燈を九面待ちの状態から和了した場合に成立する",
          "Chuuren Poutou waiting on all nine tiles.",
          "1112345678999m 5m"),
        E(7, false, "大四喜（ダイスーシー）", "Daisuushii", "", "",
          "風牌4種をすべて刻子または槓子にした場合に成立する",
          "Triplets or kans of all four winds.",
          "111z 222z 333z 444z xx"),
        E(7, true, "大七星（ダイチーシン）", "Dai Chiishin", MenzenJa, MenzenEn,
          "字牌7種をすべて対子にした七対子で和了した場合に成立する（字一色は取り下げる）",
          "Seven Pairs made of all seven honor tiles. Replaces Tsuuiisou.",
          "11z 22z 33z 44z 55z 66z 77z"),

        // ==================== 流局 ====================
        E(8, false, "荒牌平局（流局）", "Exhaustive Draw", DrawJa, DrawEn,
          "牌山を引き切っても誰も和了しなかった場合、聴牌者と不聴者のあいだで点数をやりとりして流局とする",
          "Nobody wins before the wall runs out. Tenpai players collect from those who are not.",
          ""),
        E(8, false, "四風連打（スーフーレンダ）", "Four Winds Draw", DrawJa, DrawEn,
          "誰の鳴き（ポン・チー・カン）も入らないで全プレイヤーが最初の打牌で同じ風牌を捨てた場合、流局とする",
          "All players discard the same wind on their first turn with no calls in between.",
          "4z 4z 4z 4z"),
        E(8, false, "四槓散了（スーカンナガレ）", "Four Kans Draw", DrawJa, DrawEn,
          "複数のプレイヤーにより合計で4回の槓が行われた場合、流局とする（1人で4回なら四槓子）",
          "Four kans called by two or more different players. Four by one player is Suu Kantsu instead.",
          "1111m 1111p 5555p 6666s"),
        E(8, false, "九種九牌（キュウシュキュウハイ）", "Nine Terminals Draw", DrawJa, DrawEn,
          "誰の鳴きも入らないで、親の配牌または子の第1ツモの時点で手牌の中に幺九牌が9種類以上あった場合、倒牌して流局とすることができる",
          "On your first uninterrupted turn, declare a draw if your hand holds nine or more kinds of terminals and honors.",
          "19m19p19s1234567z"),
        E(8, false, "四家立直（スーチャリーチ）", "Four Riichi Draw", DrawJa, DrawEn,
          "4人全員が立直を宣言し、4人目の宣言牌が通った場合、流局とする",
          "All four players declare riichi and the fourth declaration tile passes safely.",
          ""),
    };
}
#endif
