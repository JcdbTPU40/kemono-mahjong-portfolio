#if UNITY_EDITOR
// 入口（大きい木の手前）に置く特大のグループ勧誘パネル。
//   Tools > Kemono Mahjong > Build Group Ad Panel    … 生成＋見た目の適用（冪等）
//   Tools > Kemono Mahjong > Validate Group Ad Panel … 設定漏れの検査（変更なし）
//
// 【広告パネル（AdPanels）とは別物】
//   AdPanelManager は配下の AdPanel を全部まとめて巡回させ、役満が出ると
//   ShowPosterOverride で1枚に塗り潰す。ここへぶら下げるとグループ広告が
//   役満ポスターに乗っ取られるので、**シーンルートに単独で置く**。
//   1枚しか貼らない＝巡回もしないので、そもそもマネージャは要らない。
//
// 【クリックでグループページ】
//   MimyLab の GroupPageOpener（VRC.Economy.Store.OpenGroupPage）を使う。
//   これは外部ブラウザではなく VRChat クライアント内のグループページなので Quest でも動く。
//   プレハブは確認ダイアログ付きの uGUI 版だが、ワールド側の確認は挟まず一発で開く
//   （2026-09-09 決定）。Canvas/Image の onClick を Window.SetActive から
//   UdonBehaviour.SendCustomEvent("OpenPage") へ張り替え、Window ごと消す。
//
// 【置き場所を実測で決めている理由】
//   木の幹は上へ行くほど反り返っていて（y0.4 で z+0.11、y2.4 で z-1.57）、
//   平らなパネルを貼るとめり込む。幹には貼らず、桟橋から歩いてくる導線の脇へ立てる。
//
// 【対局中は押せなくする（GroupAdPanelGuard）】
//   UI レーザーは遠くまで届くので、卓で牌やボタンを押すつもりのトリガーがこの特大ポスターに当たり、
//   対局中にグループページが開くことがあった。ルートに GroupAdPanelGuard を付け、対局中
//   （MatchmakingManager.IsLocalPlayerSeatedInOngoingGame）だけ Canvas の BoxCollider と押せる Image を止める。
using System;
using TMPro;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using MimyLab;
using SuzuFactory.AozoraMahjong;

public static class BuildGroupAdPanel
{
    private const string RootName = "GroupAdPanel";
    private const string PrefabPath = "Assets/MimyLab/GroupPageOpener/Prefabs/GroupPageOpener_uGUI.prefab";
    private const string FontAssetPath = "Assets/Mahjong/Fonts/NotoSansJP-Bold SDF.asset";
    private const string AdPanelsRootName = "AdPanels";

    // ポスターの絵。**Sprite ではなく Texture のまま RawImage で貼る。**
    // この png は広告パネル23枚の巡回でも Texture として使われているので、
    // Sprite へインポートし直すとそちらが一斉に壊れる。
    private const string PosterTexturePath = "Assets/Mahjong/AdPanels/Posters/雀kemogroupposter.png";

    // 公式グループ。パネルに印刷して公開する情報なので、シーンだけでなくここにも持たせて
    // 作り直しても同じIDが入るようにする（Inspector で上書きすればそちらが優先）。
    private const string DefaultGroupId = "grp_00000000-0000-0000-0000-000000000000";

    // 桟橋から大きい木へ向かう飛び石の北側。地面 y=0.02・板の体積に草木も灯籠も無いことを
    // レンダラー境界で実測して選んだ場所（コライダーだけで判定すると、当たり判定を持たない
    // 植栽を見落として絵の前に草が生える）。下端が地面に接する高さにしてある。
    private static readonly Vector3 DefaultPosition = new Vector3(-7.00f, 1.08f, 2.20f);
    // 桟橋から東へ歩いてくる人に正対する向き（World Space Canvas は forward の裏側から読む）。
    private static readonly Vector3 DefaultEuler = new Vector3(0f, 20f, 0f);

    // プレハブ既定の Canvas サイズ。1448 x 2048 @ scale 0.001 ＝ 1.45 x 2.05 m。
    // A判の比（1 : √2）そのままなので、A0（0.84 x 1.19m）より一回り大きい「特大」になる。
    private const float PanelWidth = 1448f;
    private const float PanelHeight = 2048f;
    // ポスターを貼る面の内側の余白。0 にしてある理由：
    // 画像は 724x1024（1 : √2）で、Canvas も 1448x2048（同じ比）。余白を取ると内側だけ比が
    // ずれて絵が伸びる（26px 入れると 0.699 になり約1%縦長になる）。
    // 額縁はポスターの絵自体が持っているので、こちらで重ねる必要もない。
    private const float FrameInset = 0f;

    private static readonly Color Gold = new Color32(198, 158, 62, 255);
    private static readonly Color Ink = new Color32(18, 16, 14, 255);
    private static readonly Color Paper = new Color32(247, 244, 230, 255);

    private static TMP_FontAsset font_;

    [MenuItem("Tools/Kemono Mahjong/Build Group Ad Panel")]
    public static void Run()
    {
        font_ = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
        if (font_ == null)
        {
            Debug.LogError("[BuildGroupAdPanel] 日本語TMPフォントが見つかりません: " + FontAssetPath);
            return;
        }

        GameObject root = FindSceneObject(RootName);
        if (root == null)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
            {
                Debug.LogError("[BuildGroupAdPanel] プレハブが見つかりません: " + PrefabPath);
                return;
            }
            root = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            Undo.RegisterCreatedObjectUndo(root, "Build Group Ad Panel");
            root.name = RootName;
            root.transform.SetParent(null, false);
            root.transform.position = DefaultPosition;
            root.transform.rotation = Quaternion.Euler(DefaultEuler);
            Debug.Log("[BuildGroupAdPanel] " + RootName + " を生成しました（大きい木の手前・導線の脇）。");
        }
        // 既存なら位置・回転には触れない＝Sceneビューで自由に置ける。

        // AdPanels の子にいると役満ポスターに塗り潰される。見つけたら引き上げる。
        if (root.transform.parent != null && IsUnderAdPanels(root.transform))
        {
            Undo.SetTransformParent(root.transform, null, "Detach Group Ad Panel");
            Debug.LogWarning("[BuildGroupAdPanel] AdPanels の下にあったのでシーンルートへ移しました"
                             + "（役満ポスターの一斉掲示に巻き込まれるため）。");
        }

        var canvasT = root.transform.Find("Canvas");
        if (canvasT == null)
        {
            Debug.LogError("[BuildGroupAdPanel] Canvas が見つかりません。プレハブの構造が変わっています。");
            return;
        }

        BuildPosterFace(canvasT);
        RemoveConfirmWindow(canvasT);

        var opener = root.GetComponent<GroupPageOpener>();
        if (opener == null)
        {
            Debug.LogError("[BuildGroupAdPanel] GroupPageOpener が付いていません。");
            return;
        }
        // _groupId は private [SerializeField] なので SerializedObject 経由で読み書きする。
        // 空のときだけ既定値を入れる＝Inspector で別のIDに変えてあれば尊重する。
        string groupId = ReadGroupId(opener);
        if (string.IsNullOrEmpty(groupId))
        {
            WriteGroupId(opener, DefaultGroupId);
            Debug.Log("[BuildGroupAdPanel] _groupId に既定のグループIDを入れました: " + DefaultGroupId);
        }
        UdonSharpEditorUtility.CopyProxyToUdon(opener);
        EditorUtility.SetDirty(opener);

        WireGuard(root, canvasT);

        EditorSceneManager.MarkSceneDirty(root.scene);
        Debug.Log("[BuildGroupAdPanel] 完了：1.45 x 2.05 m（A判比）。絵を差し替えるときは "
                  + PosterTexturePath + " を置き換えるか、"
                  + RootName + "/Canvas/Poster の RawImage に Texture を入れてください（Sprite にはしないこと）。");
    }

    [MenuItem("Tools/Kemono Mahjong/Validate Group Ad Panel")]
    public static void Validate()
    {
        int errors = 0;
        GameObject root = FindSceneObject(RootName);
        if (root == null)
        {
            Debug.LogError("[ValidateGroupAdPanel] " + RootName + " がシーンにありません。");
            return;
        }

        if (IsUnderAdPanels(root.transform))
        {
            Debug.LogError("[ValidateGroupAdPanel] AdPanels の下にあります。役満ポスターに塗り潰されます。", root);
            ++errors;
        }

        var opener = root.GetComponent<GroupPageOpener>();
        if (opener == null) { Debug.LogError("[ValidateGroupAdPanel] GroupPageOpener がありません。"); ++errors; }
        else
        {
            string id = ReadGroupId(opener);
            if (string.IsNullOrEmpty(id))
            {
                Debug.LogError("[ValidateGroupAdPanel] _groupId が空です。押しても何も起きません。", root);
                ++errors;
            }
            else if (!id.StartsWith("grp_"))
            {
                Debug.LogWarning("[ValidateGroupAdPanel] _groupId が grp_ で始まっていません: " + id);
            }
            if (UdonSharpEditorUtility.GetBackingUdonBehaviour(opener) == null)
            {
                Debug.LogError("[ValidateGroupAdPanel] backing UdonBehaviour がありません。");
                ++errors;
            }
        }

        var canvasT = root.transform.Find("Canvas");
        if (canvasT == null) { Debug.LogError("[ValidateGroupAdPanel] Canvas がありません。"); ++errors; }
        else
        {
            if (canvasT.GetComponent<BoxCollider>() == null)
            {
                Debug.LogError("[ValidateGroupAdPanel] Canvas に BoxCollider がありません（ポインタが当たりません）。");
                ++errors;
            }
            // 確認ダイアログは廃止した（押したら即開く）。残っていたら Build のやり直し。
            var window = canvasT.Find("Window");
            if (window != null)
            {
                Debug.LogError("[ValidateGroupAdPanel] 確認ダイアログ（Window）が残っています。"
                               + "Build Group Ad Panel を実行してください。", window);
                ++errors;
            }

            // ポスターの onClick が OpenPage を直接叩いているか。
            var imageT = canvasT.Find("Image");
            var button = imageT != null ? imageT.GetComponent<Button>() : null;
            if (button == null)
            {
                Debug.LogError("[ValidateGroupAdPanel] Canvas/Image に Button がありません。");
                ++errors;
            }
            else
            {
                bool opens = false;
                for (int i = 0; i < button.onClick.GetPersistentEventCount(); ++i)
                {
                    if (button.onClick.GetPersistentMethodName(i) == "SendCustomEvent"
                        && button.onClick.GetPersistentTarget(i) is VRC.Udon.UdonBehaviour)
                    {
                        opens = true;
                    }
                }
                if (!opens)
                {
                    Debug.LogError("[ValidateGroupAdPanel] ポスターの onClick が OpenPage を叩いていません"
                                   + "（押しても何も起きないか、消したダイアログを開こうとします）。", button);
                    ++errors;
                }
            }
            var posterT = canvasT.Find("Poster");
            var raw = posterT != null ? posterT.GetComponent<RawImage>() : null;
            if (raw == null)
            {
                Debug.LogError("[ValidateGroupAdPanel] Poster に RawImage がありません。");
                ++errors;
            }
            else if (raw.texture == null)
            {
                Debug.LogWarning("[ValidateGroupAdPanel] ポスターの絵が未設定です（暫定の文字組みが出ます）。");
            }

            // ポスターの絵とテキストがクリックを食っていないか（食うとボタンが押せない）。
            foreach (var g in canvasT.GetComponentsInChildren<Graphic>(true))
            {
                if (g.transform == canvasT.Find("Image")) { continue; }
                if (g.raycastTarget)
                {
                    Debug.LogError("[ValidateGroupAdPanel] " + g.name + " の Raycast Target が ON です"
                                   + "（ポスターがクリックを食ってグループページが開きません）。", g);
                    ++errors;
                }
            }
        }

        // 対局中に押せなくするガード。
        var guard = root.GetComponent<GroupAdPanelGuard>();
        if (guard == null)
        {
            Debug.LogError("[ValidateGroupAdPanel] GroupAdPanelGuard がありません（対局中にも押せてしまいます）。"
                           + "Build Group Ad Panel を実行してください。", root);
            ++errors;
        }
        else
        {
            if (guard.matchmaking == null) { Debug.LogError("[ValidateGroupAdPanel] GroupAdPanelGuard.matchmaking が未設定です。", root); ++errors; }
            if (guard.panelCollider == null) { Debug.LogError("[ValidateGroupAdPanel] GroupAdPanelGuard.panelCollider が未設定です。", root); ++errors; }
            if (guard.panelImage == null) { Debug.LogError("[ValidateGroupAdPanel] GroupAdPanelGuard.panelImage が未設定です。", root); ++errors; }
            if (UdonSharpEditorUtility.GetBackingUdonBehaviour(guard) == null)
            {
                Debug.LogError("[ValidateGroupAdPanel] GroupAdPanelGuard の backing UdonBehaviour がありません。", root);
                ++errors;
            }
        }

        // 立って読める高さ・手が届く位置か。VRChat の UI は近づかないと押せない。
        float bottom = root.transform.position.y - (PanelHeight * 0.5f * 0.001f);
        if (bottom > 1.2f)
        {
            Debug.LogWarning("[ValidateGroupAdPanel] パネルの下端が y=" + bottom.ToString("F2")
                             + " と高めです。押すには近づく必要があるので、低めに置いてください。");
        }

        if (errors == 0) { Debug.Log("[ValidateGroupAdPanel] OK：設定に欠けはありません。"); }
        else { Debug.LogError("[ValidateGroupAdPanel] エラー " + errors + " 件。"); }
    }

    // ============================================================
    // 見た目
    // ============================================================

    private static void BuildPosterFace(Transform canvasT)
    {
        // クリックの当たり判定はプレハブの Image（Button付き）が持っている。
        // ポスター（RawImage）は raycastTarget を切ってあるので、絵で覆っても
        // クリックはこの下の Image に届く＝パネルのどこを押しても確認ダイアログが出る。
        var face = canvasT.Find("Image");
        if (face != null)
        {
            var img = face.GetComponent<Image>();
            img.sprite = null;
            // ポスターが全面を覆うので見えないが、絵が未設定のときの下地になる。
            img.color = Ink;
            img.raycastTarget = true;  // ここだけが押せる
            EditorUtility.SetDirty(img);
        }

        // 紙面。RawImage なので Texture をそのまま貼れる（Sprite へ変換しない＝広告巡回を壊さない）。
        var poster = Child(canvasT, "Poster");
        Stretch(poster, FrameInset);
        var legacyPosterImg = poster.GetComponent<Image>();
        if (legacyPosterImg != null) { UnityEngine.Object.DestroyImmediate(legacyPosterImg, true); }
        var posterRaw = poster.GetComponent<RawImage>();
        if (posterRaw == null) { posterRaw = Undo.AddComponent<RawImage>(poster.gameObject); }
        var posterTex = AssetDatabase.LoadAssetAtPath<Texture>(PosterTexturePath);
        if (posterTex == null)
        {
            Debug.LogWarning("[BuildGroupAdPanel] ポスター画像が見つかりません: " + PosterTexturePath
                             + "（暫定の文字組みのまま表示します）。");
        }
        posterRaw.texture = posterTex;
        posterRaw.color = posterTex != null ? Color.white : Ink;
        posterRaw.raycastTarget = false;
        poster.SetSiblingIndex(1);
        EditorUtility.SetDirty(posterRaw);

        // 絵が入るまでのつなぎの文字組み。絵が入ったら重なるので丸ごと隠す。
        var content = Child(canvasT, "Content");
        Stretch(content, FrameInset);
        content.SetSiblingIndex(2);
        var contentRect = content.GetComponent<Image>();
        if (contentRect != null) { UnityEngine.Object.DestroyImmediate(contentRect, true); }
        content.gameObject.SetActive(posterTex == null);

        Text(content, "Head", "公式グループ", 78f, new Vector2(0f, -120f), new Vector2(1200f, 110f), Gold);
        Text(content, "Title", "超能力ケモノ麻雀", 122f, new Vector2(0f, -250f), new Vector2(1300f, 320f), Paper);
        Text(content, "Body",
            "超能力 × ケモノ × 麻雀。\n" +
            "雀魂準拠の段位戦（三麻／四麻・半荘／東風）。\n" +
            "イベント告知・対戦募集はグループで。",
            52f, new Vector2(0f, -620f), new Vector2(1240f, 420f), Paper);

        // 押せることを明示する帯。VRChatのUIは「押せる物」に見えないと誰も触らない。
        var cta = Child(content, "Cta");
        SetCenterRect(cta, new Vector2(0f, -1560f), new Vector2(1180f, 210f));
        var ctaImg = cta.GetComponent<Image>();
        if (ctaImg == null) { ctaImg = Undo.AddComponent<Image>(cta.gameObject); }
        ctaImg.sprite = null;
        ctaImg.color = Gold;
        ctaImg.raycastTarget = false;
        Text(cta, "Label", "クリックでグループページへ\nClick to open the group page", 46f,
            Vector2.zero, new Vector2(1140f, 190f), Ink);
    }

    // プレハブの確認ダイアログは英語のみ。日本語話者が主なので併記へ書き換える
    // （KemonoLocalizer に載せるほどの量ではないため、ここで焼く）。
    // 確認ダイアログを外し、ポスターを押したら即グループページを開くようにする。
    //
    // プレハブ既定の配線は Canvas/Image(Button) → Window.SetActive(true) で、
    // 実際に開くのは Window/Button_Open の SendCustomEvent("OpenPage") だった。
    // ここでは Image の onClick を GroupPageOpener の UdonBehaviour へ直接張り替え、
    // Window を丸ごと削除する。冪等：既に張り替え済みなら何もしない。
    //
    // 注意：VRChat クライアント側が Store.OpenGroupPage で独自の確認を出すかどうかは
    // SDK の DLL 内なので分からない。出る場合はそれが残る（ワールド側では消せない）。
    private static void RemoveConfirmWindow(Transform canvasT)
    {
        var imageT = canvasT.Find("Image");
        var button = imageT != null ? imageT.GetComponent<Button>() : null;
        if (button == null)
        {
            Debug.LogError("[BuildGroupAdPanel] Canvas/Image に Button がありません。プレハブの構造が変わっています。");
            return;
        }

        var opener = canvasT.parent != null ? canvasT.parent.GetComponent<GroupPageOpener>() : null;
        var backing = opener != null ? UdonSharpEditorUtility.GetBackingUdonBehaviour(opener) : null;
        if (backing == null)
        {
            Debug.LogError("[BuildGroupAdPanel] GroupPageOpener の backing UdonBehaviour が取れません。");
            return;
        }

        // onClick を「UdonBehaviour.SendCustomEvent(\"OpenPage\")」1本だけにする。
        var so = new SerializedObject(button);
        var calls = so.FindProperty("m_OnClick.m_PersistentCalls.m_Calls");
        calls.ClearArray();
        calls.InsertArrayElementAtIndex(0);
        var call = calls.GetArrayElementAtIndex(0);
        call.FindPropertyRelative("m_Target").objectReferenceValue = backing;
        call.FindPropertyRelative("m_TargetAssemblyTypeName").stringValue =
            typeof(VRC.Udon.UdonBehaviour).AssemblyQualifiedName;
        call.FindPropertyRelative("m_MethodName").stringValue = "SendCustomEvent";
        call.FindPropertyRelative("m_Mode").enumValueIndex = 5;   // PersistentListenerMode.String
        call.FindPropertyRelative("m_CallState").enumValueIndex = 2; // RuntimeOnly
        var args = call.FindPropertyRelative("m_Arguments");
        args.FindPropertyRelative("m_StringArgument").stringValue = "OpenPage";
        args.FindPropertyRelative("m_ObjectArgument").objectReferenceValue = null;
        args.FindPropertyRelative("m_ObjectArgumentAssemblyTypeName").stringValue =
            typeof(UnityEngine.Object).AssemblyQualifiedName;
        args.FindPropertyRelative("m_IntArgument").intValue = 0;
        args.FindPropertyRelative("m_FloatArgument").floatValue = 0f;
        args.FindPropertyRelative("m_BoolArgument").boolValue = false;
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(button);

        var window = canvasT.Find("Window");
        if (window != null)
        {
            Undo.DestroyObjectImmediate(window.gameObject);
            Debug.Log("[BuildGroupAdPanel] 確認ダイアログ（Window）を削除し、押したら即グループページを開くようにしました。");
        }
    }

    // ============================================================
    // 対局中ガード
    // ============================================================

    // ルートに GroupAdPanelGuard を付けて配線する（冪等）。
    private static void WireGuard(GameObject root, Transform canvasT)
    {
        var guard = root.GetComponent<GroupAdPanelGuard>();
        if (guard == null)
        {
            guard = UdonSharpUndo.AddComponent<GroupAdPanelGuard>(root);
            Debug.Log("[BuildGroupAdPanel] GroupAdPanelGuard を付けました（対局中はポスターを押せなくする）。");
        }

        var mm = UnityEngine.Object.FindObjectOfType<MatchmakingManager>(true);
        if (mm == null)
        {
            Debug.LogError("[BuildGroupAdPanel] MatchmakingManager がシーンにありません。対局中ガードが効きません。");
        }
        guard.matchmaking = mm;
        guard.panelCollider = canvasT.GetComponent<BoxCollider>();
        var imageT = canvasT.Find("Image");
        guard.panelImage = imageT != null ? imageT.GetComponent<Image>() : null;

        UdonSharpEditorUtility.CopyProxyToUdon(guard);
        EditorUtility.SetDirty(guard);
    }

    // ============================================================
    // ヘルパー
    // ============================================================

    private static string ReadGroupId(GroupPageOpener opener)
    {
        var so = new SerializedObject(opener);
        var prop = so.FindProperty("_groupId");
        return prop != null ? prop.stringValue : null;
    }

    private static void WriteGroupId(GroupPageOpener opener, string value)
    {
        var so = new SerializedObject(opener);
        var prop = so.FindProperty("_groupId");
        if (prop == null) { return; }
        prop.stringValue = value;
        so.ApplyModifiedProperties();
    }

    private static bool IsUnderAdPanels(Transform t)
    {
        for (var p = t; p != null; p = p.parent)
        {
            if (p.name == AdPanelsRootName) { return true; }
        }
        return false;
    }

    private static Transform Child(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null) { return t; }
        var go = new GameObject(name, typeof(RectTransform));
        Undo.RegisterCreatedObjectUndo(go, "Build Group Ad Panel");
        go.layer = parent.gameObject.layer;
        go.transform.SetParent(parent, false);
        return go.transform;
    }

    private static void Stretch(Transform t, float inset)
    {
        var rt = (RectTransform)t;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(inset, inset);
        rt.offsetMax = new Vector2(-inset, -inset);
        rt.localRotation = Quaternion.identity;
        rt.localScale = Vector3.one;
        rt.localPosition = new Vector3(rt.localPosition.x, rt.localPosition.y, 0f);
    }

    // 中央基準。上端からの距離で置きたいので y は負の値で渡す。
    private static void SetCenterRect(Transform t, Vector2 pos, Vector2 size)
    {
        var rt = (RectTransform)t;
        rt.anchorMin = new Vector2(0.5f, 1f);
        rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        rt.localRotation = Quaternion.identity;
        rt.localScale = Vector3.one;
        rt.localPosition = new Vector3(rt.localPosition.x, rt.localPosition.y, 0f);
    }

    private static TextMeshProUGUI Text(Transform parent, string name, string value, float size,
        Vector2 pos, Vector2 rectSize, Color color)
    {
        var t = Child(parent, name);
        // Cta の中身だけは親いっぱいに置きたいので、pos がゼロなら中央合わせにする。
        if (pos == Vector2.zero && parent.name == "Cta")
        {
            var rt = (RectTransform)t;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = rectSize;
            rt.localRotation = Quaternion.identity;
            rt.localScale = Vector3.one;
        }
        else
        {
            SetCenterRect(t, pos, rectSize);
        }
        var tmp = t.GetComponent<TextMeshProUGUI>();
        if (tmp == null) { tmp = Undo.AddComponent<TextMeshProUGUI>(t.gameObject); }
        tmp.font = font_;
        tmp.text = value;
        tmp.fontSize = size;
        tmp.enableAutoSizing = true;
        tmp.fontSizeMax = size;
        tmp.fontSizeMin = size * 0.6f;
        tmp.color = color;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = true;
        tmp.raycastTarget = false;
        EditorUtility.SetDirty(tmp);
        return tmp;
    }

    private static GameObject FindSceneObject(string name)
    {
        var transforms = UnityEngine.Object.FindObjectsOfType<Transform>(true);
        for (int i = 0; i < transforms.Length; ++i)
        {
            var candidate = transforms[i];
            if (candidate != null && candidate.name == name && candidate.gameObject.scene.IsValid())
            {
                return candidate.gameObject;
            }
        }
        return null;
    }
}
#endif
