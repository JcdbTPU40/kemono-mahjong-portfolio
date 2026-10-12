#if UNITY_EDITOR
// ターン制限時間（雀魂準拠 5+20秒）の残り秒表示を全卓へ生成する。
//   Tools > Kemono Mahjong > Build Turn Timers … 全卓に TurnTimer/TurnTimerText を冪等生成
//   Tools > Kemono Mahjong > Remove Turn Timers … 生成物を撤去（表示だけ消す。自動打牌は動き続ける）
//
// 配線は不要。Mahjong.cs が卓直下の "TurnTimer" を名前で拾う（見つからなければ表示しないだけ）。
//
// 【表示位置の決め方】シーン/プレハブ上で TurnTimer に置いた localPosition が
// 「席0基準のホーム位置」になる。実行時はそれを席ぶんY軸回転させて手番の席へ出すので、
// Unity上で見ながら1箇所調整すれば全席に反映される。スケールも触らないのでそのまま効く。
// Build を再実行しても既存の TurnTimer の Transform は上書きしない（調整結果を守るため）。
//
// 構造は既存の Status と同じ「親Transformをビルボード回転 → 子のWorld Space Canvasが描く」。
//   TurnTimer                    … Mahjong.cs が毎フレーム位置と向きを設定する空Transform
//     TurnTimerCanvas            … World Space Canvas（Graphic を持たない＝描画順の親にする）
//       TurnTimerText            … Text（残り秒の数字）
//       TurnTimerBonusText       … Text（持ち時間 +N。0 のときは非表示）
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using SuzuFactory.AozoraMahjong;

public static class BuildTurnTimer
{
    const string RootName = "TurnTimer";
    const string FontPath = "Assets/Mahjong/Fonts/NotoSansJP-Bold.ttf";
    const int WalkthroughLayer = 17; // コライダー誤当たり防止（他のUI生成と同じ知見）

    // Status と同じ 1024px キャンバス。scale 0.00032552 で実寸約 0.333m。
    // 数字だけなので 256px に絞り、実寸 0.12m 相当（VRで卓を挟んでも読める大きさ）。
    const float CanvasScale = 0.00046875f; // 256px * この値 = 0.12m
    static readonly Vector2 CanvasSize = new Vector2(512f, 256f);
    const int TimerFontSize = 150;

    // 新規生成時のホーム位置（席0基準・卓ローカル座標）。Unity上で調整した値を正とするため、
    // すでに TurnTimer がある卓では使わない（＝Build を再実行しても位置は動かない）。
    static readonly Vector3 DefaultHomePosition = new Vector3(0.3639f, 0.0573f, 0.2058f);

    [MenuItem("Tools/Kemono Mahjong/Build Turn Timers")]
    public static void Build()
    {
        var font = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
        if (font == null)
        {
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            Debug.LogWarning("[BuildTurnTimer] フォントが見つかりません（" + FontPath + "）。LegacyRuntime で生成します。");
        }

        var tables = Object.FindObjectsOfType<Mahjong>(true);
        System.Array.Sort(tables, (a, b) => string.CompareOrdinal(GetPath(a.transform), GetPath(b.transform)));
        if (tables.Length == 0)
        {
            Debug.LogError("[BuildTurnTimer] シーンに Mahjong（卓）が見つかりません。");
            return;
        }

        foreach (var table in tables)
        {
            BuildOne(table, font);
        }

        EditorSceneManager.MarkSceneDirty(tables[0].gameObject.scene);
        Debug.Log("[BuildTurnTimer] 完了：" + tables.Length + " 卓にターン制限時間の表示を生成/更新しました。配線は不要です（卓が名前で拾います）。");
    }

    [MenuItem("Tools/Kemono Mahjong/Remove Turn Timers")]
    public static void Remove()
    {
        var tables = Object.FindObjectsOfType<Mahjong>(true);
        int removed = 0;

        foreach (var table in tables)
        {
            var t = table.transform.Find(RootName);
            if (t != null)
            {
                Undo.DestroyObjectImmediate(t.gameObject);
                ++removed;
            }
        }

        if (tables.Length != 0) { EditorSceneManager.MarkSceneDirty(tables[0].gameObject.scene); }
        Debug.Log("[BuildTurnTimer] 表示を撤去：" + removed + " 卓。※自動打牌（時間切れ代行）は表示と独立に動き続けます。");
    }

    static void BuildOne(Mahjong table, Font font)
    {
        // 親は空Transform。ここに置かれた localPosition が「席0基準のホーム位置」になり、
        // 実行時は Mahjong.cs がそれを席ぶんY軸回転させて手番の席へ出す。
        //
        // 【重要】既存の TurnTimer の Transform は触らない。Unity上で見ながら位置・大きさを
        // 詰めた結果を、Build の再実行で巻き戻さないため（新規生成時だけ初期値を入れる）。
        bool isNew = table.transform.Find(RootName) == null;
        var root = EnsureChild(table.transform, RootName);
        if (isNew)
        {
            root.transform.localPosition = DefaultHomePosition;
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = new Vector3(0.25f, 0.25f, 0.25f);
        }
        // 初期状態は非表示（対局が始まるまで出ない）。実行時に Mahjong.cs が切り替える。
        root.SetActive(false);

        // Status/StatusText と同じ向き付け（親をヨー回転させたとき正面を向く -90°）。
        var canvasGo = EnsureUiChild(root.transform, "TurnTimerCanvas");
        var canvas = canvasGo.GetComponent<Canvas>();
        if (canvas == null) { canvas = canvasGo.AddComponent<Canvas>(); }
        canvas.renderMode = RenderMode.WorldSpace;

        // GraphicRaycaster は付けない（表示専用・操作しない）。
        var raycaster = canvasGo.GetComponent<GraphicRaycaster>();
        if (raycaster != null) { Object.DestroyImmediate(raycaster); }

        var rt = canvasGo.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = CanvasSize;
        rt.localPosition = Vector3.zero;
        rt.localRotation = Quaternion.Euler(0f, -90f, 0f);
        rt.localScale = new Vector3(CanvasScale, CanvasScale, CanvasScale);

        // 既存の TurnTimerGauge は撤去する（リング表示は使わない）。
        var g = canvasGo.transform.Find("TurnTimerGauge");
        if (g != null) { Undo.DestroyObjectImmediate(g.gameObject); }

        // 数字。
        var textGo = EnsureUiChild(canvasGo.transform, "TurnTimerText");
        var text = textGo.GetComponent<Text>();
        if (text == null) { text = textGo.AddComponent<Text>(); }
        text.font = font;
        text.fontSize = TimerFontSize;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = table.turnTimerBaseColor;
        text.raycastTarget = false;
        text.supportRichText = false;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.text = "8";
        var trtMain = textGo.GetComponent<RectTransform>();
        trtMain.anchorMin = new Vector2(0.5f, 0.5f);
        trtMain.anchorMax = new Vector2(0.5f, 0.5f);
        trtMain.pivot = new Vector2(0.5f, 0.5f);
        trtMain.anchoredPosition = new Vector2(-80f, 0f);
        trtMain.sizeDelta = new Vector2(220f, 256f);
        textGo.transform.SetAsLastSibling();

        // 長考残り（持ち時間）。0 のときは実行時に非表示。
        var bonusGo = EnsureUiChild(canvasGo.transform, "TurnTimerBonusText");
        var bonus = bonusGo.GetComponent<Text>();
        if (bonus == null) { bonus = bonusGo.AddComponent<Text>(); }
        bonus.font = font;
        bonus.fontSize = 120;
        bonus.alignment = TextAnchor.MiddleLeft;
        bonus.color = table.turnTimerBonusColor;
        bonus.raycastTarget = false;
        bonus.supportRichText = false;
        bonus.horizontalOverflow = HorizontalWrapMode.Overflow;
        bonus.verticalOverflow = VerticalWrapMode.Overflow;
        bonus.text = "+30";
        bonusGo.transform.SetAsLastSibling();
        var brt = bonusGo.GetComponent<RectTransform>();
        brt.anchorMin = new Vector2(0.5f, 0.5f);
        brt.anchorMax = new Vector2(0.5f, 0.5f);
        brt.pivot = new Vector2(0f, 0.5f);
        brt.anchoredPosition = new Vector2(20f, 0f);
        brt.sizeDelta = new Vector2(260f, 256f);
        bonusGo.SetActive(false);

        // タイマーで public フィールドを増やしたので、プロキシの値を backing UdonBehaviour へ
        // 押し込む（U#はフィールド増減後にこれをやらないと実行時に既定値のままになる）。
        UdonSharpEditorUtility.CopyProxyToUdon(table);
        EditorUtility.SetDirty(table);

        SetLayerRecursively(root, WalkthroughLayer);
    }

    // ============================================================
    // 共通ヘルパ（BuildScoreBoard と同等）
    // ============================================================
    static GameObject EnsureChild(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null) { return t.gameObject; }
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Create " + name);
        go.transform.SetParent(parent, false);
        return go;
    }

    static GameObject EnsureUiChild(Transform parent, string name)
    {
        var t = parent.Find(name);
        if (t != null) { return t.gameObject; }
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Create " + name);
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();
        return go;
    }

    static void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform c in go.transform) { SetLayerRecursively(c.gameObject, layer); }
    }

    static string GetPath(Transform t)
    {
        var path = t.name;
        while (t.parent != null)
        {
            t = t.parent;
            path = t.name + "/" + path;
        }
        return path;
    }
}
#endif
