using UnityEditor;
using UnityEngine;
using UdonSharpEditor;
using SuzuFactory.AozoraMahjong;

/// <summary>
/// 対局中に使うボタン（PlayerUI 配下）の当たり判定を揃える。
///
/// VRChat の Interact は「手とコライダーの距離 &lt; proximity」で反応し、
/// VRの手の届く範囲はさらに 0.4m × アバタースケール が上限になる。
/// 元は proximity=0.1 だったため、手を10cm以内まで近づける必要があり、
/// 小さいアバターでは 0.4×スケール も縮んで二重に届かなくなっていた → proximity=0.25。
///
/// 判定の箱は雀魂風の帯の絵（JA_Skin）に合わせる（2026-10-05）。
///  ・縦（ローカルz）：帯の絵と同じ高さ・同じ位置。以前は縦6cm＋奥行き5cmの箱で、
///    絵より大きく、絵の外を押しても反応していた。
///  ・奥行き（ローカルy＝帯の面の法線）：帯の面の前後 Depth だけ。
///  ・横（ローカルx）：右詰めの列に並ぶボタンは 9cm。間隔12cm（Mahjong.ACTION_SLOT_PITCH）との差＝
///    隙間3cmを緩衝帯にする。帯の絵（約11cm）より狭いので、帯の端を押しても反応しない。
///    卓の手前の縁にあるスキップ・立直取消は列に並ばないので横幅を変えない。
/// </summary>
public static class TuneButtonReach
{
    const string PrefabPath = "Assets/Mahjong/Prefabas/KemonoMahjong.prefab";
    const string UiPath = "PlayerUIWrapper/PlayerUI";

    // 手とコライダーの距離のしきい値（m）。VRでは 0.4×アバタースケール が上限なので、
    // これ以上大きくしてもVRの体感は変わらない（デスクトップの射程だけ伸びる）。
    const float Proximity = 0.25f;

    // 帯の面の前後に持たせる奥行き（m）。帯は厚みゼロの板なので、判定には少しだけ厚みを付ける。
    const float Depth = 0.01f;

    // 右詰めの列に並ぶボタンの横幅（m・ローカルx）。列の間隔12cmとの差が隙間になる。Mahjong.ACTION_BUTTON_WIDTH も同じ値にすること。
    const float RowWidth = 0.09f;

    // 列に並ばない（卓の手前の縁に固定の）ボタン。横幅は触らない。
    static bool IsRowButton(string name)
    {
        return name != "NoButton" && name != "ReachCancelButton";
    }

    [MenuItem("Tools/Kemono Mahjong/Tune Button Reach (Prefab)")]
    public static void Apply()
    {
        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (asset == null)
        {
            Debug.LogError("[TuneButtonReach] プレハブが見つかりません: " + PrefabPath);
            return;
        }

        int changed = Tune(asset, true);
        if (changed > 0)
        {
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
        }
        Debug.Log("[TuneButtonReach] プレハブの " + changed + " 個を調整しました。");
    }

    [MenuItem("Tools/Kemono Mahjong/Validate Button Reach")]
    public static void Validate()
    {
        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        int bad = asset != null ? Tune(asset, false) : -1;

        int sceneBad = 0, tables = 0;
        foreach (var m in Object.FindObjectsOfType<Mahjong>(true))
        {
            ++tables;
            sceneBad += Tune(m.gameObject, false);
        }

        if (bad == 0 && sceneBad == 0)
        {
            Debug.Log("[TuneButtonReach] OK：プレハブ・シーン" + tables + "卓とも既定値です。");
        }
        else
        {
            Debug.LogWarning("[TuneButtonReach] 未適用 プレハブ=" + bad + " / シーン=" + sceneBad
                + "（Tune Button Reach (Prefab) を実行してください）");
        }
    }

    // 帯の絵（JA_Skin）の範囲をボタンのローカル座標で返す。無ければ false。
    static bool TryGetArtBounds(Transform button, out Vector3 min, out Vector3 max)
    {
        min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        max = -min;
        foreach (var r in button.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (r.name != "JA_Skin") { continue; }
            var mf = r.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) { continue; }
            var bb = mf.sharedMesh.bounds;
            for (int i = 0; i < 8; ++i)
            {
                var c = new Vector3((i & 1) == 0 ? bb.min.x : bb.max.x,
                                    (i & 2) == 0 ? bb.min.y : bb.max.y,
                                    (i & 4) == 0 ? bb.min.z : bb.max.z);
                var p = button.InverseTransformPoint(r.transform.TransformPoint(c));
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            return true;
        }
        return false;
    }

    static bool Differs(float a, float b)
    {
        return Mathf.Abs(a - b) > 0.0001f;
    }

    /// <summary>apply=false なら数えるだけ。戻り値は「既定値と違ったボタン数」。</summary>
    static int Tune(GameObject root, bool apply)
    {
        var ui = root.transform.Find(UiPath);
        if (ui == null) { return 0; }

        int n = 0;
        foreach (var b in ui.GetComponentsInChildren<MahjongPushButton>(true))
        {
            var col = b.GetComponent<BoxCollider>();
            var ub = UdonSharpEditorUtility.GetBackingUdonBehaviour(b);
            if (col == null || ub == null) { continue; }

            var scale = b.transform.lossyScale;
            if (Mathf.Abs(scale.x) < 0.0001f || Mathf.Abs(scale.y) < 0.0001f) { continue; }

            var wantSize = col.size;
            var wantCenter = col.center;

            // 縦と奥行きは帯の絵に合わせる（絵が無いボタンは触らない）。
            Vector3 artMin, artMax;
            if (TryGetArtBounds(b.transform, out artMin, out artMax))
            {
                wantSize.z = artMax.z - artMin.z;
                wantCenter.z = (artMin.z + artMax.z) * 0.5f;
                wantSize.y = Depth / Mathf.Abs(scale.y);
                wantCenter.y = (artMin.y + artMax.y) * 0.5f;
            }

            // 横幅は列に並ぶボタンだけ狭める。
            if (IsRowButton(b.name))
            {
                wantSize.x = RowWidth / Mathf.Abs(scale.x);
                wantCenter.x = 0.0f;
            }

            bool needCol = Differs(col.size.x, wantSize.x) || Differs(col.size.y, wantSize.y) || Differs(col.size.z, wantSize.z)
                || Differs(col.center.x, wantCenter.x) || Differs(col.center.y, wantCenter.y) || Differs(col.center.z, wantCenter.z);
            bool needProx = Mathf.Abs(ub.proximity - Proximity) > 0.001f;
            if (!needCol && !needProx) { continue; }

            ++n;
            if (!apply) { continue; }

            if (needCol)
            {
                col.size = wantSize;
                col.center = wantCenter;
                EditorUtility.SetDirty(col);
            }
            if (needProx)
            {
                ub.proximity = Proximity;
                EditorUtility.SetDirty(ub);
            }
        }
        return n;
    }
}
