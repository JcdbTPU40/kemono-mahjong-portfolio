using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;

// ============================================================
// テスト卓（エディタ専用・Play 中だけ）：VRChat を起動せずに、観戦モニターと卓を本番に近い形で確かめる。
//
// ・ClientSim のリモートプレイヤーを「人間の客」として卓の席に座らせる（本物の VRCPlayerApi＝ID・頭の骨がある）。
//   観戦モニター・卓とも、本番と同じ判定でその席を人間として扱う（名前・顔のショット・NEXT 待ち・役の読み上げ）。
// ・客の操作は、本物の端末と同じ「席＋署名（sig）」の要求を卓の Owner へ送る（OnPickTileRequest／OnAutoActionRequest／
//   OnNoButtonRequest／OnReachButton）。署名は同期値だけで作られる Mahjong.GetTurnTimerSignature と同じ式で計算する。
//   打ち方は自動化パネルを使う人の真似：和了れれば和了・聴牌なら立直・それ以外はツモ切り・鳴きは見送る。
// ・NEXT は卓が出すのと同じ合図（和了は読み上げ後の agariNextReady_）が開いてから、人間らしい間を置いて押す。
// ・自分（ローカルプレイヤー）は観戦ルームへ移す＝観戦モニターは本番どおりルームに入ったら観戦を始める。
//
// 本番と違うところ：客のアバターは ClientSim のロボット（座れない）・打ち方が単純・1台なので通信の遅れが無い・
// 卓は MainMenu を通さず手動で開始する（観戦モニターが卓を選べるよう MatchmakingManager の使用中マスクを立てる）。
// 本番のコードは書き換えない（読み・イベント送り・ヒープの値の変更は Play 中のこの卓だけ）。
// ============================================================
public class SpectatorTestTable : EditorWindow
{
    const int ROLE_GUEST = 0;
    const int ROLE_CPU = 1;
    static readonly string[] RoleNames = { "人間の客", "CPU" };

    // Mahjong.cs の gameState（private const の写し）
    const int GS_INITIAL = 0, GS_ENTRY_COMPLETED = 1, GS_DRAWED = 2, GS_CONFIRM_RON = 3, GS_CONFIRM_KAN = 4,
              GS_CONFIRM_PON = 5, GS_CONFIRM_CHI = 6, GS_EATED = 7, GS_TSUMO = 13, GS_RON = 14,
              GS_SELECT_KAN = 15, GS_SELECT_REACH = 16, GS_CONFIRM_RON_CHANKAN = 17, GS_RESULT = 18;
    const int AUTO_ACTION_TSUMO = 0, AUTO_ACTION_RON = 1;

    // 設定（ウィンドウ）
    static int tableIndex = 0;
    static int[] roles = { ROLE_GUEST, ROLE_GUEST, ROLE_CPU, ROLE_CPU };
    static bool moveToRoom = true;
    static int ruleLength = 0;        // MatchmakingManager.RULE_LEN_*（0=半荘 1=東風 2=一局）
    static readonly string[] RuleNames = { "半荘戦", "東風戦", "一局戦" };
    static bool presetFirstHand = true;
    static bool[] tenpaiSeats = { true, true, false, false };
    static float humanDelayMin = 0.8f, humanDelayMax = 1.8f;
    static float resultHold = 6f;     // 終局の順位表で NEXT を押すまで（秒）

    // 実行中の状態
    static bool running;
    static int phase;                 // 0=客を出す 1=席を埋める 2=開始 3=運転
    static float phaseAt, lastTry;
    static UdonBehaviour table, matchmaking, monitor;
    static readonly int[] guestIds = new int[4];
    static readonly List<VRCPlayerApi> guests = new List<VRCPlayerApi>();
    static string status = "停止中";
    static int lastGs = -1;
    static bool firstLeaderFixed;
    static float firstLeaderUntil = -1f;
    static readonly System.Random rng = new System.Random();

    class SeatState { public int sig; public float seenAt; public float delay; public int step; public int pickIdx; public float nextActAt; }
    static readonly SeatState[] seatStates = { new SeatState(), new SeatState(), new SeatState(), new SeatState() };
    static readonly float[] nextGateAt = { -1f, -1f, -1f, -1f };
    static int nextGateGs = -1;
    static readonly List<string> log = new List<string>();

    [MenuItem("Tools/Kemono Mahjong/Test Table (Play)")]
    static void Open() { GetWindow<SpectatorTestTable>("テスト卓"); }

    void OnGUI()
    {
        EditorGUILayout.HelpBox("Play 中に「開始」。ClientSim のリモートプレイヤーを人間の客として座らせ、本番と同じ要求で打たせます。自分は観戦ルームへ移動します。", MessageType.Info);
        using (new EditorGUI.DisabledScope(running))
        {
            tableIndex = EditorGUILayout.IntSlider("卓（0＝卓1）", tableIndex, 0, 7);
            for (int s = 0; s < 4; ++s)
            {
                EditorGUILayout.BeginHorizontal();
                roles[s] = EditorGUILayout.Popup("席" + s, roles[s], RoleNames);
                EditorGUILayout.EndHorizontal();
            }
            ruleLength = EditorGUILayout.Popup("局数", ruleLength, RuleNames);
            moveToRoom = EditorGUILayout.Toggle("自分を観戦ルームへ", moveToRoom);
            presetFirstHand = EditorGUILayout.Toggle("1局目を仕込む（聴牌）", presetFirstHand);
            if (presetFirstHand)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PrefixLabel("配牌で聴牌する席");
                for (int s = 0; s < 4; ++s) { tenpaiSeats[s] = GUILayout.Toggle(tenpaiSeats[s], s.ToString(), "Button"); }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel("客の考える時間（秒）");
            humanDelayMin = EditorGUILayout.FloatField(humanDelayMin);
            humanDelayMax = EditorGUILayout.FloatField(humanDelayMax);
            EditorGUILayout.EndHorizontal();
            resultHold = EditorGUILayout.FloatField("終局表示を見る秒数", resultHold);
        }

        EditorGUILayout.Space();
        using (new EditorGUI.DisabledScope(!Application.isPlaying))
        {
            EditorGUILayout.BeginHorizontal();
            if (!running && GUILayout.Button("開始")) { Begin(); }
            if (running && GUILayout.Button("停止")) { Stop("停止しました"); }
            if (GUILayout.Button("次の局を仕込む（聴牌）")) { ArmPreset(); }
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.LabelField("状態", status);
        EditorGUILayout.LabelField("記録（新しい順）");
        for (int i = log.Count - 1; i >= 0 && i >= log.Count - 12; --i) { EditorGUILayout.LabelField(log[i], EditorStyles.miniLabel); }
    }

    void OnInspectorUpdate() { Repaint(); }

    static void Log(string s)
    {
        log.Add(Time.time.ToString("F1") + "  " + s);
        if (log.Count > 200) { log.RemoveAt(0); }
    }

    // ------------------------------------------------------------
    static void Begin()
    {
        if (!Application.isPlaying) { return; }
        table = matchmaking = monitor = null;
        foreach (var ub in Resources.FindObjectsOfTypeAll<UdonBehaviour>())
        {
            if (ub.gameObject.scene.name == null || ub.programSource == null) { continue; }
            string pn = ub.programSource.name;
            if (pn.StartsWith("SpectatorMonitor")) { monitor = ub; }
            if (pn.StartsWith("MatchmakingManager")) { matchmaking = ub; }
        }
        if (matchmaking == null) { status = "MatchmakingManager が見つからない"; return; }
        var tables = matchmaking.GetProgramVariable("tables") as System.Array;
        if (tables == null || tableIndex >= tables.Length) { status = "卓が見つからない"; return; }
        table = tables.GetValue(tableIndex) as UdonBehaviour;
        if (table == null) { status = "卓が見つからない"; return; }
        if ((int)table.GetProgramVariable("gameState") != GS_INITIAL) { status = "卓が対局中（リセットしてから）"; return; }

        // 近接カリングを止めて卓を起こす（観戦モニターは自分の端末で配信卓を起こすが、テストでは常に起こしておく）
        foreach (var ub in Resources.FindObjectsOfTypeAll<UdonBehaviour>())
        {
            if (ub.gameObject.scene.name != null && ub.programSource != null && ub.programSource.name.Contains("Culler")) { ub.enabled = false; }
        }
        for (var t = table.transform; t != null; t = t.parent) { if (!t.gameObject.activeSelf) { t.gameObject.SetActive(true); } }
        matchmaking.SetProgramVariable("allowManualTableEntry", true);
        // 局数ルール（本番はフリー対局の部屋設定が書く卓ごとの配列）
        var lens = matchmaking.GetProgramVariable("tableRuleLength") as int[];
        if (lens != null && tableIndex < lens.Length) { lens[tableIndex] = ruleLength; }

        guests.Clear();
        for (int s = 0; s < 4; ++s) { guestIds[s] = 0; seatStates[s] = new SeatState(); nextGateAt[s] = -1f; }
        log.Clear();
        running = true;
        phase = 0;
        phaseAt = Time.time;
        lastTry = -10f;
        lastGs = -1;
        firstLeaderFixed = false;
        firstLeaderUntil = -1f;
        status = "準備中";
        EditorApplication.update -= Tick;
        EditorApplication.update += Tick;
        Log("開始");
    }

    static void Stop(string why)
    {
        running = false;
        EditorApplication.update -= Tick;
        status = why;
        Log(why);
    }

    // 次の配牌に「チュンビーム連鎖」を仕込み、聴牌させない席は両面の片方を山の奥の風牌と入れ替えて崩す
    static void ArmPreset()
    {
        if (table == null) { status = "先に開始してください"; return; }
        // 親は卓の今の親のまま（別の席を親にすると「東一局」が東N局に化ける）。仕込みの手牌は親から順に配られる
        int ld = (int)table.GetProgramVariable("leader");
        if (ld < 0 || ld > 3) { ld = 0; }
        table.SendCustomEvent("DebugSetSeat" + ld);
        table.SendCustomEvent("DebugPresetChunChain");
        var wall = table.GetProgramVariable("debugWall_") as int[];
        if (wall == null) { Log("仕込み失敗（debugWall_ が空）"); return; }
        int len = wall.Length, np = 4, search = 20;
        for (int r = 0; r < 4; ++r)
        {
            if (tenpaiSeats[(ld + r) % 4]) { continue; }
            int hi = len - 1 - (r + np * 12);
            for (int i = search; i < len - 13 * np - 10; ++i)
            {
                int id = wall[i];
                int num = (id >> 2) & 15;
                if ((id >> 6) == 3 && num >= 1 && num <= 3) { wall[i] = wall[hi]; wall[hi] = id; search = i + 1; break; }
            }
        }
        table.SetProgramVariable("debugWall_", wall);
        Log("次の配牌を仕込んだ（聴牌：" + SeatsText(tenpaiSeats) + "）");
    }

    static string SeatsText(bool[] f)
    {
        var l = new List<string>();
        for (int s = 0; s < 4; ++s) { if (f[s]) { l.Add("席" + s); } }
        return l.Count > 0 ? string.Join("・", l) : "なし";
    }

    // ------------------------------------------------------------
    static void Tick()
    {
        if (!running) { return; }
        if (!Application.isPlaying || table == null) { Stop("Play が止まった"); return; }

        int gs = (int)table.GetProgramVariable("gameState");
        var entry = table.GetProgramVariable("entry") as bool[];
        var cpu = table.GetProgramVariable("entryCpu") as bool[];
        var spid = table.GetProgramVariable("seatPlayerId") as int[];
        if (entry == null || cpu == null || spid == null) { return; }

        if (phase == 0)
        {
            if (Time.time - phaseAt < 1f) { return; }
            SpawnGuests();
            if (moveToRoom) { EnterSpectatorRoom(); }
            phase = 1;
            return;
        }
        if (phase == 1)
        {
            // CPU で4席を埋めてから、客の席を人間（entryCpu=false・seatPlayerId=客）へ書き換える
            if (!(entry[0] && entry[1] && entry[2] && entry[3]))
            {
                if (Time.time - lastTry > 2f)
                {
                    lastTry = Time.time;
                    for (int i = 0; i < 4; ++i) { if (!entry[i]) { table.SendCustomEvent("_OnEntryCpuButton" + i); } }
                }
                return;
            }
            for (int s = 0; s < 4; ++s)
            {
                if (roles[s] == ROLE_GUEST && guestIds[s] > 0) { cpu[s] = false; spid[s] = guestIds[s]; }
            }
            // 客の要求は Owner（この端末）から送るので、卓の送り主照合を Play 中のこの卓だけ緩める。
            table.SetProgramVariable("testHarnessTrustOwner_", true);
            if (presetFirstHand) { ArmPreset(); }
            Log("着席：" + SeatRolesText());
            phase = 2;
            lastTry = -10f;
            return;
        }
        if (phase == 2)
        {
            if (gs == GS_INITIAL || gs == GS_ENTRY_COMPLETED)
            {
                if (Time.time - lastTry > 2f) { lastTry = Time.time; table.SendCustomEvent("OnStartButton"); }
                return;
            }
            // 1局目を仕込んだときは、仕込みが決めた親を起家にもする（仕込みは leader だけ上書きするので、
            // そのままだと起家との差で「東N局」になる）
            // 卓は自分の同期メッセージを 1 フレーム遅れで読み戻す（Deserialize）ので、一度書いただけでは戻される。
            // 配牌の間（最大 4 秒）は毎フレームそろえ続け、次の Serialize に載せて定着させる
            if (presetFirstHand && !firstLeaderFixed) { firstLeaderFixed = true; firstLeaderUntil = Time.time + 4f; }
            // 観戦モニターが卓を選べるように（本番は MainMenu の開始で Manager が立てる）
            matchmaking.SetProgramVariable("busyTableMask", (int)matchmaking.GetProgramVariable("busyTableMask") | (1 << tableIndex));
            if (monitor != null)
            {
                // 観戦ルームへ移ったときは、モニターの入室判定（0.25秒ごと）が観戦を始めるのを待つ（最大 3 秒）
                if (moveToRoom && !(bool)monitor.GetProgramVariable("spectating_") && Time.time - phaseAt < 4f) { return; }
                if (!(bool)monitor.GetProgramVariable("spectating_"))
                {
                    // 観戦ルームに入れなかったとき（移動しない設定など）は、切替ボタンを押したのと同じにする
                    monitor.SetProgramVariable("voiceRouter", null);
                    monitor.SendCustomEvent("OnToggleMode");
                }
                monitor.SendCustomEvent("OnSelectTable" + tableIndex);
            }
            Log("対局開始");
            phase = 3;
        }

        // 局数ルール：Manager はフリー対局の部屋でない卓を定期的に半荘へ戻す（ClearFreeTableFlags）ので、毎フレーム入れ直す
        var lens = matchmaking.GetProgramVariable("tableRuleLength") as int[];
        if (lens != null && tableIndex < lens.Length && lens[tableIndex] != ruleLength) { lens[tableIndex] = ruleLength; }
        if (Time.time < firstLeaderUntil)
        {
            int ld = (int)table.GetProgramVariable("leader");
            if ((int)table.GetProgramVariable("firstLeader") != ld) { table.SetProgramVariable("firstLeader", ld); }
        }
        if (gs != lastGs) { lastGs = gs; status = "対局中 gameState=" + gs; }
        DriveGuests(gs);
        DriveNext(gs);
        if (gs == GS_INITIAL && phase == 3) { Stop("卓がリセットされた（終局）"); }
    }

    static string SeatRolesText()
    {
        var l = new List<string>();
        for (int s = 0; s < 4; ++s) { l.Add("席" + s + "=" + (roles[s] == ROLE_GUEST ? "客(id" + guestIds[s] + ")" : "CPU")); }
        return string.Join(" ", l);
    }

    // ClientSim のリモートプレイヤーを出し、卓の席の外側に卓の中心を向けて立たせる
    static void SpawnGuests()
    {
        var before = new HashSet<int>();
        foreach (var p in AllPlayers()) { before.Add(p.playerId); }
        int need = 0;
        for (int s = 0; s < 4; ++s) { if (roles[s] == ROLE_GUEST) { ++need; } }
        for (int i = 0; i < need; ++i) { VRC.SDK3.ClientSim.ClientSimMain.SpawnRemotePlayer("TestGuest" + (i + 1)); }
        var fresh = new List<VRCPlayerApi>();
        foreach (var p in AllPlayers()) { if (!before.Contains(p.playerId) && !p.isLocal) { fresh.Add(p); } }

        var tt = table.transform;
        int k = 0;
        for (int s = 0; s < 4; ++s)
        {
            if (roles[s] != ROLE_GUEST || k >= fresh.Count) { continue; }
            var p = fresh[k++];
            guestIds[s] = p.playerId;
            guests.Add(p);
            Vector3 d = tt.rotation * (Quaternion.Euler(0f, s * -90f, 0f) * Vector3.right);
            var go = p.gameObject;
            go.transform.position = tt.position + d * 0.75f;
            go.transform.rotation = Quaternion.LookRotation(-d, Vector3.up);
        }
        Log("客を出した：" + fresh.Count + "人");
    }

    static List<VRCPlayerApi> AllPlayers()
    {
        var arr = new VRCPlayerApi[VRCPlayerApi.GetPlayerCount()];
        VRCPlayerApi.GetPlayers(arr);
        var l = new List<VRCPlayerApi>();
        foreach (var p in arr) { if (p != null) { l.Add(p); } }
        return l;
    }

    // 入場の注意書きに同意し、観戦ルームの中央からモニターを向く
    static void EnterSpectatorRoom()
    {
        foreach (var ub in Resources.FindObjectsOfTypeAll<UdonBehaviour>())
        {
            if (ub.gameObject.scene.name != null && ub.programSource != null && ub.programSource.name.StartsWith("EntryNoticeGate")) { ub.SendCustomEvent("OnAgree"); }
        }
        var lp = Networking.LocalPlayer;
        if (lp == null || monitor == null) { return; }
        var router = monitor.GetProgramVariable("voiceRouter") as UdonBehaviour;
        var area = router != null ? router.GetProgramVariable("roomArea") as Transform : null;
        var screen = monitor.transform.Find("Monitor");
        if (area == null || screen == null) { Log("観戦ルームが見つからない（切替ボタンで観戦する）"); return; }
        Vector3 pos = area.position;
        if (Physics.Raycast(pos + Vector3.up * 1.0f, Vector3.down, out var hit, 5f)) { pos = hit.point; }
        Vector3 look = Vector3.ProjectOnPlane(screen.position - pos, Vector3.up);
        lp.TeleportTo(pos, look.sqrMagnitude > 0.01f ? Quaternion.LookRotation(look) : Quaternion.identity);
        Log("観戦ルームへ移動");
    }

    // ------------------------------------------------------------
    // 客の操作：本物の端末と同じ要求を、人間らしい間を置いて送る
    static void DriveGuests(int gs)
    {
        int actor = -1;
        if (gs == GS_DRAWED || gs == GS_EATED || gs == GS_SELECT_KAN || gs == GS_SELECT_REACH) { actor = (int)table.GetProgramVariable("turn"); }
        else if (gs == GS_CONFIRM_RON || gs == GS_CONFIRM_KAN || gs == GS_CONFIRM_PON || gs == GS_CONFIRM_CHI || gs == GS_CONFIRM_RON_CHANKAN) { actor = (int)table.GetProgramVariable("eatTurn"); }
        if (actor < 0 || actor > 3 || roles[actor] != ROLE_GUEST || guestIds[actor] <= 0) { return; }

        int sig = TurnTimerSignature(gs, actor);
        var st = seatStates[actor];
        float now = Time.time;
        if (sig != st.sig)
        {
            st.sig = sig;
            st.seenAt = now;
            st.step = 0;
            st.pickIdx = -1;
            bool confirm = gs != GS_DRAWED && gs != GS_SELECT_REACH && gs != GS_EATED;
            float lo = confirm ? humanDelayMin * 0.6f : humanDelayMin;
            float hi = confirm ? humanDelayMax * 0.6f : humanDelayMax;
            st.delay = lo + (float)rng.NextDouble() * Mathf.Max(0f, hi - lo);
            st.nextActAt = now + st.delay;
        }
        if (now < st.nextActAt) { return; }
        st.nextActAt = now + 0.3f;   // 卓が受けなかった時の次の手まで

        int[] numHand = table.GetProgramVariable("numHandTiles") as int[];
        int[] numDiscarded = table.GetProgramVariable("numDiscardedTiles") as int[];
        int[] reach = table.GetProgramVariable("reachIndex") as int[];
        int n = numHand != null ? numHand[actor] : 14;
        int disc = numDiscarded != null ? numDiscarded[actor] : 0;

        switch (gs)
        {
            case GS_DRAWED:
                if (st.step == 0) { Send("OnAutoActionRequest", actor, sig, AUTO_ACTION_TSUMO); st.step = 1; return; }
                if (st.step == 1)
                {
                    st.step = 2;
                    if (reach != null && reach[actor] == -1) { table.SendCustomNetworkEvent(NetworkEventTarget.Owner, "OnReachButton"); return; }
                }
                Pick(actor, sig, n, disc, st);   // ツモ切り（受けられなければ右から順に試す）
                return;
            case GS_SELECT_REACH:
            case GS_EATED:
                Pick(actor, sig, n, disc, st);
                return;
            case GS_CONFIRM_RON:
            case GS_CONFIRM_RON_CHANKAN:
                if (st.step == 0) { Send("OnAutoActionRequest", actor, sig, AUTO_ACTION_RON); st.step = 1; return; }
                Send("OnNoButtonRequest", actor, sig);
                return;
            default:
                // 鳴き（ポン・チー・カン）と槓の選択は見送る
                Send("OnNoButtonRequest", actor, sig);
                return;
        }
    }

    static void Pick(int seat, int sig, int n, int disc, SeatState st)
    {
        if (st.pickIdx < 0) { st.pickIdx = n - 1; }
        if (st.pickIdx < 0) { return; }
        table.SendCustomNetworkEvent(NetworkEventTarget.Owner, "OnPickTileRequest", seat, st.pickIdx, sig, disc);
        st.pickIdx--;
        if (st.pickIdx < 0) { st.pickIdx = n - 1; }
    }

    static void Send(string ev, int seat, int sig, int kind)
    {
        table.SendCustomNetworkEvent(NetworkEventTarget.Owner, ev, seat, sig, kind);
    }

    static void Send(string ev, int seat, int sig)
    {
        table.SendCustomNetworkEvent(NetworkEventTarget.Owner, ev, seat, sig);
    }

    // Mahjong.GetTurnTimerSignature と同じ式（同期値だけで作られる＝本物の端末も同じ値を出す）
    static int TurnTimerSignature(int gs, int actor)
    {
        unchecked
        {
            int sig = gs;
            sig = sig * 5 + actor;
            sig = sig * 37 + (int)table.GetProgramVariable("round");
            sig = sig * 41 + (int)table.GetProgramVariable("honba");
            sig = sig * 17 + (int)table.GetProgramVariable("numKan");
            sig = sig * 13 + (int)table.GetProgramVariable("numKitaTotal");
            var nd = table.GetProgramVariable("numDiscardedTiles") as int[];
            var ne = table.GetProgramVariable("numEatedSets") as int[];
            var nh = table.GetProgramVariable("numHandTiles") as int[];
            for (int i = 0; i < 4; ++i)
            {
                sig = sig * 79 + nd[i];
                sig = sig * 7 + ne[i];
            }
            sig = sig * 19 + nh[actor];
            if (sig == 0) { sig = 1; }
            return sig;
        }
    }

    // ------------------------------------------------------------
    // NEXT：卓が NEXT を出すのと同じ合図が開いてから、人間らしい間を置いて押す（客の席だけ。CPU 席は卓が数えない）
    static void DriveNext(int gs)
    {
        bool endState = (gs >= 8 && gs <= GS_RON) || gs == GS_RESULT;
        // 画面が変わったら（和了→終局の順位表など）押す予定を取り直す（前の画面の「押した後 3 秒」を持ち越さない）
        if (!endState || gs != nextGateGs) { for (int s = 0; s < 4; ++s) { nextGateAt[s] = -1f; } nextGateGs = gs; }
        if (!endState) { return; }
        bool allowed = gs == GS_RESULT || (gs != GS_TSUMO && gs != GS_RON) || (bool)table.GetProgramVariable("agariNextReady_");
        var ready = table.GetProgramVariable("nextReady") as bool[];
        if (ready == null) { return; }
        float now = Time.time;
        for (int s = 0; s < 4; ++s)
        {
            if (roles[s] != ROLE_GUEST || !allowed || ready[s]) { nextGateAt[s] = -1f; continue; }
            if (nextGateAt[s] < 0f)
            {
                float wait = gs == GS_RESULT ? resultHold : humanDelayMin + (float)rng.NextDouble() * Mathf.Max(0f, humanDelayMax - humanDelayMin);
                nextGateAt[s] = now + wait + 0.3f * s;
                continue;
            }
            if (now >= nextGateAt[s])
            {
                table.SendCustomEvent("OnNextButton" + s);
                nextGateAt[s] = now + 3f;
                Log("NEXT 席" + s + "（gameState=" + gs + "）");
            }
        }
    }
}
