#if UNITY_EDITOR
// オープン立直ボタンを卓Prefabに生成する。
//
// 置き場所は立直ボタンと同じスロット（PlayerUI/ReachButton の localPosition）。
// 立直ボタンは自分の手番(DRAWED)、オープン立直ボタンは立直宣言後(SELECT_REACH)にしか
// 出ないので、卓上ボタンの「zスロットに排他ボタンを重ねる」規約どおり位置を共有できる。
//
// ラベルは3Dメッシュ（MeshFilter）で焼かれていて文字を差し替えられないため、
// 複製元の JA_/EN_ ラベルは両方消し、鳴きボタンの赤版と同じく PlayerUIWrapper 側の
// uGUI Text（"OpenReachText"）に Mahjong.cs が毎フレーム書き込む。
//
// あわせて、他家・観戦者に見せる公開待ち牌の牌プール（OpenReachTiles0..3）も用意する。
// 牌モデルは種類ごとに面が焼き込まれているので、席ごとに実体が要る。
//
// 2回実行しても安全（冪等：既にあれば位置・id・テキスト位置を更新するだけ）。
// 実行: メニュー Tools > Kemono Mahjong > Build Open Reach Button
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using SuzuFactory.AozoraMahjong;

public static class BuildOpenReachButton
{
    const string PrefabPath = "Assets/Mahjong/Prefabas/KemonoMahjong.prefab";

    // uGUI Text の既定フォント（LegacyRuntime＝Arial）は日本語グリフを持たない。
    // 卓の他の日本語表示はすべてこのフォントなので合わせる。
    const string JpFontPath = "Assets/Mahjong/Fonts/NotoSansJP-Bold.ttf";

    // 公開待ち牌の牌プール。牌モデルは種類ごとに面が焼き込まれているので、
    // 席ごとに実体が必要（同じ種類が2席の待ちに同時に入り得る）。
    // SampleTiles0（自席の聴牌表示用・種類ごとに1個）を席数ぶん複製して使う。
    const string WaitTilesPrefix = "OpenReachTiles";

    // 旧・文字ラベル版。牌モデルへ置き換えたので見つけたら消す。
    const string LegacyWaitRootName = "OpenReachWait";

    // ラベルをボタン面から浮かせる量[m]。元の焼き込みラベルと同じ位置になる値。
    const float LabelSurfaceOffset = 0.011f;

    [MenuItem("Tools/Kemono Mahjong/Build Open Reach Button")]
    public static void Run()
    {
        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null)
        {
            Debug.LogError("[BuildOpenReachButton] Prefab を読み込めません: " + PrefabPath);
            return;
        }

        try
        {
            var wrapper = root.transform.Find("PlayerUIWrapper");
            var playerUI = wrapper != null ? wrapper.Find("PlayerUI") : null;
            if (playerUI == null)
            {
                Debug.LogError("[BuildOpenReachButton] PlayerUIWrapper/PlayerUI が見つかりません。");
                return;
            }

            var jpFont = AssetDatabase.LoadAssetAtPath<Font>(JpFontPath);
            if (jpFont == null)
            {
                Debug.LogWarning("[BuildOpenReachButton] 日本語フォントが見つかりません: " + JpFontPath);
            }

            var reachButton = playerUI.Find("ReachButton");
        var openReachSlot = playerUI.Find("TsumoButton");
            var refText = wrapper.Find("ChiPrevText");

            if (reachButton == null || openReachSlot == null || refText == null)
        {
            Debug.LogError("[BuildOpenReachButton] Required button/text not found.");
            return;
        }


        // ReachButtonの見た目を複製し、ツモボタンのスロットへ分離配置する。
            var t = playerUI.Find("OpenReachButton");

            if (t == null)
            {
                var go = (GameObject)Object.Instantiate(reachButton.gameObject, playerUI);
                go.name = "OpenReachButton";
                t = go.transform;
            }

        t.localPosition = openReachSlot.localPosition;
        t.localRotation = reachButton.localRotation;
        t.localScale = reachButton.localScale;

            var push = t.GetComponent<MahjongPushButton>();
            if (push != null)
            {
                push.id = "OnOpenReachButton";
                push.local = false;
                push.target = VRC.Udon.Common.Interfaces.NetworkEventTarget.Owner;
        // 宣言種別は一度の押下で確定するため、通常の操作ボタンと同様に押下後は閉じる。
        push.disableOnPush = true;

                UdonSharpEditorUtility.CopyProxyToUdon(push);
                EditorUtility.SetDirty(push);
            }
            else
            {
                Debug.LogWarning("[BuildOpenReachButton] MahjongPushButton がありません。");
            }

            // 焼かれた「リーチ」ラベルは使わない（文字を差し替えられないため）。
            // ただし SetActive(false) では消えない：Mahjong.SwitchLanguage は卓を全走査して
            // 名前が JA_/EN_ で始まるオブジェクトを実行時に問答無用でアクティブへ戻す
            // （ActivateLanguage）。伏せておいても入場時の言語適用で「リーチ」が復活し、
            // uGUI ラベルの「オープン立直」と重なって読めなくなる。
            // 位置だけ控えて、オブジェクトごと削除する。
            // ラベル位置はボタンの Transform から決める。
            // 削除済みの焼き込みラベルを基準にすると、2回目以降のビルドで
            // 「見つからない＝ボタンの原点」へ落ちてプレートの内側に潜り、
            // 文字が見えなくなる（＝ボタンが真っ白になる）。
            // 面はボタンの -up 側を向いているので、その方向へ板の厚みぶん押し出す。
            var bakedLabels = new System.Collections.Generic.List<GameObject>();

            foreach (var child in t.GetComponentsInChildren<Transform>(true))
            {
                if (child == t) { continue; }
                if (!child.name.StartsWith("EN_") && !child.name.StartsWith("JA_")) { continue; }

                bakedLabels.Add(child.gameObject);
            }

            for (int i = 0; i < bakedLabels.Count; ++i)
            {
                Object.DestroyImmediate(bakedLabels[i]);
            }

            Debug.Log("[BuildOpenReachButton] 焼き込みラベルを削除: " + bakedLabels.Count + " 件"
                + "（SwitchLanguage が JA_/EN_ を実行時に復活させるため、非表示では足りない）");

            // 表示制御は Mahjong.cs 側が毎フレーム行う。ここでは伏せておく。
            t.gameObject.SetActive(false);

            // --- ラベル（uGUI Text） ---
            var textTf = wrapper.Find("OpenReachText");

            if (textTf == null)
            {
                var go = (GameObject)Object.Instantiate(refText.gameObject, wrapper);
                go.name = "OpenReachText";
                textTf = go.transform;
            }

            // ChiPrevButton→ChiPrevText の相対関係を保ったまま、ボタン位置へ合わせる。
            // テキスト側の Canvas は x/z が反転した座標系（BuildAkaMeldButtons と同じ）。
            // 焼き込み文字の位置をアンカーにして、可変uGUIラベルをボタン中央へ重ねる。
            // 参照テキストとボタンが別階層でも、ワールド座標基準なのでずれない。
            textTf.position = t.position - t.up * LabelSurfaceOffset;
            textTf.rotation = refText.rotation;
            textTf.localScale = refText.localScale;

            var text = textTf.GetComponent<Text>();
            if (text != null)
            {
                text.supportRichText = true;
                text.text = "";
                if (jpFont != null) { text.font = jpFont; }
                text.alignment = TextAnchor.MiddleCenter;
                text.fontStyle = FontStyle.Bold;
                text.fontSize = 180;
                text.resizeTextForBestFit = true;
                text.resizeTextMinSize = 48;
                text.resizeTextMaxSize = 180;
                // Overflow のままだと bestFit が横幅を見ないので、長い表記がボタンから
                // はみ出す。Wrap にして縦横とも収まるサイズまで自動で縮ませる。
                text.horizontalOverflow = HorizontalWrapMode.Wrap;
                text.verticalOverflow = VerticalWrapMode.Truncate;
                text.color = new Color(0.62f, 0.08f, 0.035f, 1.0f);
                text.raycastTarget = false;

                var openRect = textTf as RectTransform;
                if (openRect != null)
                {
                    openRect.sizeDelta = new Vector2(900.0f, 280.0f);
                }

                var outline = textTf.GetComponent<Outline>();
                if (outline == null) { outline = textTf.gameObject.AddComponent<Outline>(); }
                outline.effectColor = new Color(1.0f, 0.86f, 0.62f, 0.95f);
                outline.effectDistance = new Vector2(2.0f, -2.0f);

                EditorUtility.SetDirty(text);
            }
            else
            {
                Debug.LogWarning("[BuildOpenReachButton] Text コンポーネントがありません: OpenReachText");
            }

            // --- 他家・観戦者向けの公開待ち牌（牌モデル・席ごとに独立） ---
            // 牌は種類ごとに面が焼き込まれていて実行時に差し替えられないので、
            // 同じ種類が複数席の待ちに入っても困らないよう席ごとにプールを持つ。
            // 元は SampleTiles0（自席の聴牌表示用・34種×1個）。
            var tilesSet = root.transform.Find("TilesSet");
            var sampleTiles = tilesSet != null ? tilesSet.Find("SampleTiles0") : null;

            if (tilesSet == null || sampleTiles == null)
            {
                Debug.LogError("[BuildOpenReachButton] TilesSet/SampleTiles0 が見つかりません。");
                return;
            }

            int madeTiles = 0;

            for (int seat = 0; seat < 4; ++seat)
            {
                string groupName = WaitTilesPrefix + seat;
                var group = tilesSet.Find(groupName);

                if (group == null)
                {
                    var groupGo = (GameObject)Object.Instantiate(sampleTiles.gameObject, tilesSet);
                    groupGo.name = groupName;
                    group = groupGo.transform;
                    madeTiles += group.childCount;
                }

                group.localPosition = sampleTiles.localPosition;
                group.localRotation = sampleTiles.localRotation;
                group.localScale = sampleTiles.localScale;
                group.gameObject.SetActive(true);

                // 表示制御は Mahjong.cs 側が毎フレーム行う。ここでは伏せておく。
                foreach (Transform tile in group)
                {
                    tile.gameObject.SetActive(false);
                }
            }

            // 旧・文字ラベル版の撤去（牌モデルへ置き換えたため）。
            var legacyWaitRoot = root.transform.Find(LegacyWaitRootName);
            if (legacyWaitRoot != null)
            {
                Debug.Log("[BuildOpenReachButton] 旧・文字ラベル版を削除: " + LegacyWaitRootName);
                Object.DestroyImmediate(legacyWaitRoot.gameObject);
            }

            for (int i = wrapper.childCount - 1; i >= 0; --i)
            {
                var legacy = wrapper.GetChild(i);
                if (!legacy.name.StartsWith("OpenReachWaitText")) { continue; }

                Debug.Log("[BuildOpenReachButton] 旧・文字ラベルを削除: " + legacy.name);
                Object.DestroyImmediate(legacy.gameObject);
            }

            Debug.Log("[BuildOpenReachButton] 公開待ち牌プール: "
                + WaitTilesPrefix + "0..3 を用意（新規生成 " + madeTiles + " 牌）。");

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log("[BuildOpenReachButton] 完了: OpenReachButton / OpenReachText を用意しました"
                + "（pos=" + t.localPosition.ToString("F3") + " id=OnOpenReachButton）。"
                + "Prefab を保存＝シーンの全卓に反映。");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }
}
#endif
