using SuzuFactory.AozoraMahjong;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

/// <summary>
/// 観戦ルームの箱（SpectatorRoomArea）を Scene ビューでいじれるようにする（Editor専用・ビルドに入らない）。
/// 箱そのものの表示は TableVoiceRouterGizmo（緑）。こちらは編集用の面ハンドルと外枠だけ。
/// ・常時：一度入った人が出たとみなされる外枠＝exitMargin ぶん広い箱（点線）を描く
/// ・SpectatorRoomArea を選択中：箱の各面のハンドルをドラッグして範囲を直接調整できる（Undo 可）
/// 箱は Transform のローカル ±0.5 の立方体＝position が中心・localScale が大きさ（TableVoiceRouter.InRoom と同じ解釈）。
/// Build Table Voice は手で調整した箱をそのまま使う。Carpet_01 の大きさへ戻すときは Reset Spectator Room Area。
/// </summary>
[InitializeOnLoad]
public static class SpectatorRoomAreaHandles
{
    const string RoomAreaName = "SpectatorRoomArea";
    static readonly Color RoomColor = new Color(0.25f, 0.85f, 1f, 1f);
    static readonly Color MarginColor = new Color(0.25f, 0.85f, 1f, 0.45f);
    static readonly BoxBoundsHandle handle_ = new BoxBoundsHandle();

    static SpectatorRoomAreaHandles()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
        SceneView.duringSceneGui += OnSceneGUI;
    }

    static void OnSceneGUI(SceneView view)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) { return; }
        var router = Object.FindObjectOfType<TableVoiceRouter>(true);
        Transform area = router != null ? router.roomArea : null;
        if (area == null) { return; }
        float margin = router.exitMargin;

        Vector3 size = area.localScale;
        Vector3 center = area.position;
        var rot = Quaternion.Euler(0f, area.eulerAngles.y, 0f);   // InRoom は回転も見るが、通常は回さない

        using (new Handles.DrawingScope(Matrix4x4.TRS(center, rot, Vector3.one)))
        {
            Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;   // 柱や葉の裏でも見えるように
            // 外枠（exitMargin）
            Handles.color = MarginColor;
            DrawDottedBox(Vector3.zero, size + Vector3.one * (margin * 2f));

            bool selected = Selection.activeTransform == area;
            Handles.color = RoomColor;
            if (!selected) { return; }   // 箱そのものは TableVoiceRouterGizmo（緑）が常時描く

            // 選択中：面ハンドルで編集
            handle_.center = Vector3.zero;
            handle_.size = size;
            handle_.wireframeColor = RoomColor;
            handle_.handleColor = RoomColor;
            EditorGUI.BeginChangeCheck();
            handle_.DrawHandle();
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(area, "Edit Spectator Room Area");
                area.position = center + rot * handle_.center;
                Vector3 s = handle_.size;
                area.localScale = new Vector3(Mathf.Max(1f, s.x), Mathf.Max(1f, s.y), Mathf.Max(1f, s.z));
                BuildSpectatorRoomBoundary.RebuildMesh(area);   // 床の防音マークの帯も追従させる
            }
            Handles.Label(new Vector3(0f, size.y * 0.5f + 0.2f, 0f),
                "観戦ルーム  " + size.x.ToString("F2") + " × " + size.y.ToString("F2") + " × " + size.z.ToString("F2")
                + " m\n下端 Y=" + (center.y - size.y * 0.5f).ToString("F2") + " / 上端 Y=" + (center.y + size.y * 0.5f).ToString("F2")
                + "\n点線＝出たとみなす外枠（+" + margin.ToString("F2") + "m）");
        }
    }

    static void DrawDottedBox(Vector3 c, Vector3 s)
    {
        Vector3 h = s * 0.5f;
        var p = new Vector3[8];
        for (int i = 0; i < 8; ++i)
        {
            p[i] = c + new Vector3((i & 1) != 0 ? h.x : -h.x, (i & 2) != 0 ? h.y : -h.y, (i & 4) != 0 ? h.z : -h.z);
        }
        int[,] e = { { 0, 1 }, { 2, 3 }, { 4, 5 }, { 6, 7 }, { 0, 2 }, { 1, 3 }, { 4, 6 }, { 5, 7 }, { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 } };
        for (int i = 0; i < 12; ++i) { Handles.DrawDottedLine(p[e[i, 0]], p[e[i, 1]], 4f); }
    }
}
