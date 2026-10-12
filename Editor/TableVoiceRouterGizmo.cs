// 観戦ルームの範囲（TableVoiceRouter.roomArea の箱）を Scene ビューに緑の半透明の箱で描く。
// 何も選んでいなくても常に見える。箱（SpectatorRoomArea）を選ぶと濃くなり、名前と大きさが出る。
// 範囲の調整は SpectatorRoomArea を選んで、移動（W）・拡大（R）・回転（E）ツールで行う。
using SuzuFactory.AozoraMahjong;
using UnityEditor;
using UnityEngine;

public static class TableVoiceRouterGizmo
{
    static readonly Color Fill = new Color(0.2f, 0.9f, 0.4f, 0.10f);
    static readonly Color FillSelected = new Color(0.2f, 0.9f, 0.4f, 0.22f);
    static readonly Color Wire = new Color(0.2f, 1.0f, 0.4f, 0.8f);

    [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.Pickable)]
    static void Draw(TableVoiceRouter router, GizmoType type)
    {
        var area = router.roomArea;
        if (area == null) { return; }
        bool selected = Selection.activeTransform == area || Selection.activeTransform == router.transform;

        var old = Gizmos.matrix;
        Gizmos.matrix = area.localToWorldMatrix;
        Gizmos.color = selected ? FillSelected : Fill;
        Gizmos.DrawCube(Vector3.zero, Vector3.one);
        Gizmos.color = Wire;
        Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
        Gizmos.matrix = old;

        // 壁や床に隠れても分かるように、床の四角と縁は前面に描く
        var oldZ = Handles.zTest;
        Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
        Vector3[] floor =
        {
            area.TransformPoint(new Vector3(-0.5f, -0.5f, -0.5f)),
            area.TransformPoint(new Vector3(0.5f, -0.5f, -0.5f)),
            area.TransformPoint(new Vector3(0.5f, -0.5f, 0.5f)),
            area.TransformPoint(new Vector3(-0.5f, -0.5f, 0.5f)),
        };
        Handles.DrawSolidRectangleWithOutline(floor, selected ? FillSelected : Fill, Wire);
        Handles.color = Wire;
        float thick = selected ? 3f : 1.5f;
        for (int i = 0; i < 4; ++i)
        {
            Vector3 a = new Vector3(i == 1 || i == 2 ? 0.5f : -0.5f, 0f, i >= 2 ? 0.5f : -0.5f);
            Vector3 b = new Vector3(i == 0 || i == 1 ? 0.5f : -0.5f, 0f, i == 0 || i == 3 ? -0.5f : 0.5f);
            // 床と天井の辺、縦の辺
            Handles.DrawLine(area.TransformPoint(a + Vector3.down * 0.5f), area.TransformPoint(b + Vector3.down * 0.5f), thick);
            Handles.DrawLine(area.TransformPoint(a + Vector3.up * 0.5f), area.TransformPoint(b + Vector3.up * 0.5f), thick);
            Handles.DrawLine(area.TransformPoint(a + Vector3.down * 0.5f), area.TransformPoint(a + Vector3.up * 0.5f), thick);
        }
        Handles.zTest = oldZ;

        if (selected)
        {
            Vector3 top = area.TransformPoint(new Vector3(0f, 0.5f, 0f));
            Vector3 s = area.lossyScale;
            Handles.Label(top, "観戦ルーム  " + Mathf.Abs(s.x).ToString("F2") + " x " + Mathf.Abs(s.z).ToString("F2")
                               + " m（高さ " + Mathf.Abs(s.y).ToString("F2") + " m）");
        }
    }
}
