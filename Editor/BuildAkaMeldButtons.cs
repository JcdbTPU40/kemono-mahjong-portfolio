#if UNITY_EDITOR
// 赤ドラ選択用の鳴きボタン（チー3種＋ポンの「赤を使う」版）を卓Prefabに生成する。
// 既存の鳴きボタン列（x=-0.42 の下段）はチー3種＋パスで4スロット埋まっているため、
// 赤版はツモ/ロン/立直と同じ上段（x=-0.30, y=0.08）に、対応する通常ボタンの真上へ置く。
// 2回実行しても安全（冪等：既にあれば位置・id・テキストを更新するだけ）。
// 実行: メニュー Tools > Kemono Mahjong > Build Aka Meld Buttons
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using SuzuFactory.AozoraMahjong;

public static class BuildAkaMeldButtons
{
    const string PrefabPath = "Assets/Mahjong/Prefabas/KemonoMahjong.prefab";

    // 赤版ボタンを置く段（ツモ/ロン/立直と同じ高さ・距離）。z は対応する通常ボタンに合わせる。
    const float RedRowX = -0.30f;
    const float RedRowY = 0.08f;

    // 牌表示テキストの配置基準。ChiPrevButton と ChiPrevText の相対関係を全テキストに使い回す。
    static Transform refButton_;
    static Transform refText_;

    [MenuItem("Tools/Kemono Mahjong/Build Aka Meld Buttons")]
    public static void Run()
    {
        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null)
        {
            Debug.LogError("[BuildAkaMeldButtons] Prefab を読み込めません: " + PrefabPath);
            return;
        }

        try
        {
            var wrapper = root.transform.Find("PlayerUIWrapper");
            var playerUI = wrapper != null ? wrapper.Find("PlayerUI") : null;
            if (playerUI == null)
            {
                Debug.LogError("[BuildAkaMeldButtons] PlayerUIWrapper/PlayerUI が見つかりません。");
                return;
            }

            refButton_ = playerUI.Find("ChiPrevButton");
            refText_ = wrapper.Find("ChiPrevText");
            var srcPonButton = playerUI.Find("PonButton");

            if (refButton_ == null || refText_ == null || srcPonButton == null)
            {
                Debug.LogError("[BuildAkaMeldButtons] 複製元（ChiPrevButton / ChiPrevText / PonButton）が見つかりません。");
                return;
            }

            int made = 0;

            // チーの赤版3種：複製元は ChiPrevButton（「チー」ラベルのメッシュを流用）。
            made += EnsureRedButton(playerUI, wrapper, refButton_, "ChiPrevButton", "ChiPrevRedButton", "ChiPrevRedText", "OnChiPrevRedButton");
            made += EnsureRedButton(playerUI, wrapper, refButton_, "ChiCenterButton", "ChiCenterRedButton", "ChiCenterRedText", "OnChiCenterRedButton");
            made += EnsureRedButton(playerUI, wrapper, refButton_, "ChiNextButton", "ChiNextRedButton", "ChiNextRedText", "OnChiNextRedButton");

            // ポンの赤版：複製元は PonButton（「ポン」ラベル）。
            made += EnsureRedButton(playerUI, wrapper, srcPonButton, "PonButton", "PonRedButton", "PonRedText", "OnPonRedButton");

            // ラベルは3Dメッシュで文字を差し替えられないため、通常ポン側にも牌表示テキストを足して
            // 赤あり/なしを見分けられるようにする（Mahjong.cs 側は選択肢が2つあるときだけ表示する）。
            var ponButton = playerUI.Find("PonButton");
            if (ponButton != null)
            {
                EnsureText(wrapper, ponButton.localPosition, "PonText");
                ++made;
            }

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log("[BuildAkaMeldButtons] 完了: 生成/更新 " + made + " 件。Prefab を保存しました（シーンの全卓に反映）。");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
            refButton_ = null;
            refText_ = null;
        }
    }

    // 赤版ボタン＋その牌表示テキストを1組そろえる。位置は通常ボタンの真上（上段）。
    static int EnsureRedButton(Transform playerUI, Transform wrapper, Transform srcButton,
        string normalButtonName, string newButtonName, string newTextName, string eventId)
    {
        var normalButton = playerUI.Find(normalButtonName);
        if (normalButton == null)
        {
            Debug.LogWarning("[BuildAkaMeldButtons] 通常ボタンが見つかりません: " + normalButtonName);
            return 0;
        }

        var pos = new Vector3(RedRowX, RedRowY, normalButton.localPosition.z);

        EnsureButton(playerUI, srcButton, newButtonName, pos, eventId);
        EnsureText(wrapper, pos, newTextName);

        return 1;
    }

    static void EnsureButton(Transform playerUI, Transform src, string name, Vector3 localPos, string eventId)
    {
        var t = playerUI.Find(name);

        if (t == null)
        {
            var go = (GameObject)Object.Instantiate(src.gameObject, playerUI);
            go.name = name;
            t = go.transform;
        }

        t.localPosition = localPos;
        t.localRotation = src.localRotation;
        t.localScale = src.localScale;

        // 押下時に飛ばすイベント名を差し替える（U#はproxyに書いてUdon側へコピーする）。
        var push = t.GetComponent<MahjongPushButton>();
        if (push != null)
        {
            push.id = eventId;
            UdonSharpEditorUtility.CopyProxyToUdon(push);
            EditorUtility.SetDirty(push);
        }
        else
        {
            Debug.LogWarning("[BuildAkaMeldButtons] MahjongPushButton がありません: " + name);
        }

        // 表示制御は Mahjong.cs 側が毎フレーム行う。ここでは伏せておく。
        // JA_/EN_ の二重表示（実機ラベル重なりの既知地雷）を避けるため EN_ は落とす。
        t.gameObject.SetActive(false);

        foreach (var child in t.GetComponentsInChildren<Transform>(true))
        {
            if (child == t) { continue; }
            if (child.name.StartsWith("EN_")) { child.gameObject.SetActive(false); }
            else if (child.name.StartsWith("JA_")) { child.gameObject.SetActive(true); }
        }
    }

    // ChiPrevButton→ChiPrevText の相対関係を保ったまま、targetButtonPos に対応する位置へテキストを置く。
    // テキスト側の Canvas は x/z が反転した座標系なので、ボタン側の差分に符号を合わせて反映する。
    static void EnsureText(Transform wrapper, Vector3 targetButtonPos, string name)
    {
        var t = wrapper.Find(name);

        if (t == null)
        {
            var go = (GameObject)Object.Instantiate(refText_.gameObject, wrapper);
            go.name = name;
            t = go.transform;
        }

        var delta = targetButtonPos - refButton_.localPosition;
        var rect = t as RectTransform;
        var refRect = refText_ as RectTransform;

        if (rect != null && refRect != null)
        {
            rect.anchoredPosition = refRect.anchoredPosition + new Vector2(-delta.x, delta.y);
            rect.sizeDelta = refRect.sizeDelta;
            var lp = rect.localPosition;
            rect.localPosition = new Vector3(lp.x, lp.y, refText_.localPosition.z - delta.z);
        }
        else
        {
            t.localPosition = new Vector3(
                refText_.localPosition.x - delta.x,
                refText_.localPosition.y + delta.y,
                refText_.localPosition.z - delta.z);
        }

        t.localRotation = refText_.localRotation;
        t.localScale = refText_.localScale;

        // 赤字表示（<color=...>）に必要。既定でONだが複製元が変わっても壊れないよう明示する。
        var text = t.GetComponent<Text>();
        if (text != null)
        {
            text.supportRichText = true;
            text.text = "";
            EditorUtility.SetDirty(text);
        }
        else
        {
            Debug.LogWarning("[BuildAkaMeldButtons] Text コンポーネントがありません: " + name);
        }
    }
}
#endif
