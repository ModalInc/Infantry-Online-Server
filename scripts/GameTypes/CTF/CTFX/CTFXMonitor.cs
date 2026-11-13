// CTF:X server-side bots - the MONITOR (Travis 10-12: "should instrument and monitor it and see where we can do recursive self
// improvement"; "packet health as something that's instrumented and carefully considered" before any live test).
//
// Once a second every living bot is sampled: moving, fighting, or standing idle and WHY (its brain's last reason: pause at a
// goal, post, perch, anchor, door, build, formation, hide, no goal ...), where it stands relative to its own base (home end,
// hall, enemy end, another end, out), its activity (CTFXCommand). Idle bots of one team within 128 px of each other form an
// idle cluster: the "congregating and stopping" Travis saw. Streaks: how long each seat has stood idle without a break.
// Network: per connected client the packets the server sent it per second (Client._packetsSent deltas, peak and mean) and its
// 20 s average send rate; and how many bot state updates went out per second (each reaches every client in route range).
//
// Each stats window: one JSON line to logs/ctfxmon.jsonl (scratchpad monitor_report.py turns the file into a scorecard), a
// short summary on the stats line, and the window's measures to CTFXCommand.Tune (the self-tuning step).
/* PARSER GUARD: keep a block comment after the last line comment */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

using InfServer.Bots;
using InfServer.Game;

namespace InfServer.Script.GameType_CTF
{
    internal sealed class CTFXMonitor
    {
        sealed class TeamAcc
        {
            public string Name;
            public int Samples, Idle, Fight, Moving, FreeSamples, IdleFree, DefendSamples, DefendFight;
            public readonly Dictionary<string, int> Why = new Dictionary<string, int>(StringComparer.Ordinal);
            public readonly Dictionary<string, int> Area = new Dictionary<string, int>(StringComparer.Ordinal);
            public readonly int[] Act = new int[10];
            public readonly int[] HomeAct = new int[10];    // samples at home, by activity: who keeps the base full
            public int Hold;                                // idle by design: post, anchor, perch, hide, squad, build
            public int ClusterMax, ClusterSum, ClusterN, ClusterX, ClusterY;
        }

        sealed class NetAcc
        {
            public string Alias;
            public ulong LastSent;
            public long Packets;
            public int Samples, PeakPps, LastBps;
        }

        readonly CTFXBotManager _m;
        int _lastSample, _winStart;
        readonly Dictionary<string, TeamAcc> _teams = new Dictionary<string, TeamAcc>(StringComparer.Ordinal);
        readonly Dictionary<CTFXSlot, int> _idleSince = new Dictionary<CTFXSlot, int>();
        readonly Dictionary<CTFXSlot, string> _idleWhy = new Dictionary<CTFXSlot, string>();
        readonly Dictionary<Player, NetAcc> _net = new Dictionary<Player, NetAcc>();
        long _routes0;
        int _routePeak, _lastRoutes;
        string _last = "";
        int _turretsLost0;
        static readonly string FilePath = Path.Combine("logs", "ctfxmon.jsonl");
        readonly List<CTFXBot> _idle = new List<CTFXBot>(64);
        readonly HashSet<CTFXSlot> _seen = new HashSet<CTFXSlot>();
        // the whole map: bots per area (8 ends, 4 halls, outside areas A-H, out) summed per sample, averaged per window
        readonly SortedDictionary<string, int> _map = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int _mapTicks;
        // where bots walk into things: 4-tile cells of "blocked" samples, with the goal the last one had (dead ends to fix)
        readonly Dictionary<int, int> _blocked = new Dictionary<int, int>();
        readonly Dictionary<int, string> _blockedGoal = new Dictionary<int, string>();

        internal CTFXMonitor(CTFXBotManager m) { _m = m; }

        internal void Reset(int now)
        {
            _teams.Clear(); _idleSince.Clear(); _idleWhy.Clear(); _net.Clear();
            _winStart = now; _lastSample = now; _routes0 = _m.BotRoutes; _routePeak = 0; _lastRoutes = 0;
            _turretsLost0 = _m.Turrets.Lost;
        }

        TeamAcc Acc(string name)
        {
            TeamAcc a;
            if (!_teams.TryGetValue(name, out a)) { a = new TeamAcc(); a.Name = name; _teams[name] = a; }
            return a;
        }

        static void Inc(Dictionary<string, int> d, string k)
        {
            int n;
            d.TryGetValue(k, out n);
            d[k] = n + 1;
        }

        // ================================================================ per second
        internal void Sample(int now)
        {
            if (now - _lastSample < 1000) return;
            _lastSample = now;
            _idle.Clear();
            _seen.Clear();
            foreach (CTFXBot b in _m.LiveBots)
            {
                if (b.IsDead || b.Released || b._team == null || b.Slot == null) continue;
                _seen.Add(b.Slot);
                string where = b.InDropship ? "ship" : _m.AreaAt(b._state.positionX, b._state.positionY);
                if (where == "out") { char oa = _m.Dropship.OutsideArea(b._state.positionX, b._state.positionY); if (oa != ' ') where = "outside-" + oa; }
                if (where.StartsWith("outside-", StringComparison.Ordinal))
                {   // who is out there: brawlers (the outside activity or packs), others fighting there, others passing through
                    CTFXAct oact = _m.Cmdr.ActOf(b);
                    string who = oact == CTFXAct.Outside || (b.Group != null && b.Group.Outside) ? "out:brawl" : b.HasTarget ? "out:fight" : "out:pass";
                    int wc;
                    _map.TryGetValue(who, out wc);
                    _map[who] = wc + 1;
                }
                int mc;
                _map.TryGetValue(where, out mc);
                _map[where] = mc + 1;
                TeamAcc a = Acc(b._team._name);
                a.Samples++;
                CTFXAct act = _m.Cmdr.ActOf(b);
                a.Act[(int)act]++;
                bool free = CTFXCommand.Free(act);
                if (free) a.FreeSamples++;
                if (act == CTFXAct.Defend) a.DefendSamples++;
                string rel = RelArea(b);
                Inc(a.Area, rel);
                if (rel == "home") a.HomeAct[(int)act]++;
                if (b.HasTarget)
                {
                    a.Fight++;
                    if (act == CTFXAct.Defend) a.DefendFight++;
                    _m.Cmdr.AddContact(b._state.positionX, b._state.positionY, now, 1.0);
                    _idleSince.Remove(b.Slot);
                }
                else if (b.Moving)
                {
                    a.Moving++;
                    _idleSince.Remove(b.Slot);
                }
                else
                {
                    a.Idle++;
                    string why = b.Why == "move" ? "blocked" : b.Why ?? "-";       // keys down yet not moving: walking into something
                    // a free bot idling for real (gearing up in the dropship after a respawn is part of the game, not idling)
                    if (free && why != "gearup") a.IdleFree++;
                    if (why == "blocked")
                    {
                        int k = (b._state.positionX >> 6) * 10000 + (b._state.positionY >> 6);
                        int c;
                        _blocked.TryGetValue(k, out c);
                        _blocked[k] = c + 1;
                        _blockedGoal[k] = b.Style + "/" + _m.Cmdr.ActOf(b) + " goal " + b.GoalText;
                    }
                    Inc(a.Why, why);
                    if (why == "post" || why == "anchor" || why == "perch" || why == "hide" || why == "squad" || why == "build" || why == "gearup") a.Hold++;
                    if (!_idleSince.ContainsKey(b.Slot)) _idleSince[b.Slot] = now;
                    _idleWhy[b.Slot] = why;
                    _idle.Add(b);
                }
            }
            _mapTicks++;
            if (_idleSince.Count > 0)
            {   // a dead seat's streak ends with its body
                List<CTFXSlot> gone = null;
                foreach (CTFXSlot s in _idleSince.Keys) if (!_seen.Contains(s)) { if (gone == null) gone = new List<CTFXSlot>(); gone.Add(s); }
                if (gone != null) foreach (CTFXSlot s in gone) { _idleSince.Remove(s); _idleWhy.Remove(s); }
            }
            Clusters();
            NetSample(now);
            if (now - _lastSnap >= 15000) { _lastSnap = now; Snapshot(); }
        }

        int _lastSnap;
        static readonly string SnapPath = Path.Combine("logs", "ctfxpos.jsonl");

        static string J(string s) { return s == null ? "" : s.Replace("\\", "/").Replace("\"", "'"); }

        /// <summary>Where everyone is (Travis 10-15: judge the bots from the map, not only counters): logs/ctfxpos.jsonl.</summary>
        void Snapshot()
        {
            try
            {
                StringBuilder js = new StringBuilder(8192);
                js.Append("{\"t\":\"").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("\",\"b\":[");
                bool first = true;
                foreach (CTFXBot b in _m.LiveBots)
                {
                    if (b.IsDead || b.Released || b._team == null || b.Slot == null) continue;
                    if (!first) js.Append(',');
                    first = false;
                    string area = b.InDropship ? "ship" : _m.AreaAt(b._state.positionX, b._state.positionY);
                    js.Append('[').Append(b._state.positionX >> 4).Append(',').Append(b._state.positionY >> 4).Append(",\"").Append(J(b._team._name))
                      .Append("\",\"").Append(b.Style).Append("\",\"").Append(_m.Cmdr.ActOf(b)).Append("\",\"").Append(area).Append("\",")
                      .Append(b.HasTarget ? 1 : 0).Append(',').Append(b.Slot.Bounty).Append(',').Append(_m.Flags != null && _m.Flags.Carrying(b) ? 1 : 0)
                      .Append(",\"").Append(b.Slot.Human != null ? b.Slot.Human.Tier : "").Append("\",\"").Append(J(b.Slot.Name)).Append("\"]");
                }
                js.Append("],\"f\":[");
                first = true;
                if (_m.Flags != null)
                    foreach (Arena.FlagState fs in _m.Flags.All())
                    {
                        if (!first) js.Append(',');
                        first = false;
                        js.Append('[').Append(fs.posX >> 4).Append(',').Append(fs.posY >> 4).Append(",\"").Append(fs.team != null ? J(fs.team._name) : "")
                          .Append("\",").Append(fs.carrier != null ? 1 : 0).Append(']');
                    }
                js.Append("],\"u\":[");      // 10-17 soak telemetry: live turrets per team and area, and every seat's lifetime deaths
                first = true;
                Dictionary<string, int> tur = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (Computer c in _m.Turrets.Live)
                {
                    if (c.IsDead || c._team == null || c._team._id < 0 || c._primaryGun == null) continue;
                    string k = J(c._team._name) + "|" + _m.AreaAt(c._state.positionX, c._state.positionY);
                    int n0; tur.TryGetValue(k, out n0); tur[k] = n0 + 1;
                }
                foreach (KeyValuePair<string, int> kv in tur)
                {
                    if (!first) js.Append(',');
                    first = false;
                    string[] ta = kv.Key.Split('|');
                    js.Append("[\"").Append(ta[0]).Append("\",\"").Append(ta[1]).Append("\",").Append(kv.Value).Append(']');
                }
                int deaths = 0;
                foreach (CTFXBot b in _m.LiveBots) if (b.Slot != null) deaths += b.Slot.Deaths;
                js.Append("],\"d\":").Append(deaths).Append(",\"h\":[");
                first = true;
                foreach (Player p in _m.Arena.PlayersIngame)
                {
                    if (p == null || p._state == null || _m.SlotOfRow(p._id) != null) continue;
                    if (!first) js.Append(',');
                    first = false;
                    js.Append('[').Append(p._state.positionX >> 4).Append(',').Append(p._state.positionY >> 4).Append(",\"").Append(J(p._alias))
                      .Append("\",\"").Append(p._team != null ? J(p._team._name) : "").Append("\"]");
                }
                js.Append("]}");
                File.AppendAllText(SnapPath, js.ToString() + Environment.NewLine);
            }
            catch (Exception) { }
        }

        /// <summary>Idle bots of one team chained within 128 px: the biggest chain per team, where it stands.</summary>
        void Clusters()
        {
            int n = _idle.Count;
            if (n == 0) return;
            int[] comp = new int[n];
            for (int i = 0; i < n; i++) comp[i] = -1;
            int c = 0;
            Dictionary<string, int> biggest = new Dictionary<string, int>(StringComparer.Ordinal);
            Stack<int> st = new Stack<int>();
            for (int i = 0; i < n; i++)
            {
                if (comp[i] >= 0) continue;
                int size = 0;
                long sx = 0, sy = 0;
                comp[i] = c; st.Push(i);
                while (st.Count > 0)
                {
                    int k = st.Pop();
                    size++; sx += _idle[k]._state.positionX; sy += _idle[k]._state.positionY;
                    for (int j = 0; j < n; j++)
                    {
                        if (comp[j] >= 0 || _idle[j]._team != _idle[k]._team) continue;
                        int dx = _idle[j]._state.positionX - _idle[k]._state.positionX, dy = _idle[j]._state.positionY - _idle[k]._state.positionY;
                        if (dx * dx + dy * dy <= 128 * 128) { comp[j] = c; st.Push(j); }
                    }
                }
                TeamAcc a = Acc(_idle[i]._team._name);
                int prev;
                biggest.TryGetValue(a.Name, out prev);
                if (size > prev) biggest[a.Name] = size;
                if (size > a.ClusterMax) { a.ClusterMax = size; a.ClusterX = (int)(sx / size) >> 4; a.ClusterY = (int)(sy / size) >> 4; }
                c++;
            }
            foreach (KeyValuePair<string, int> kv in biggest) { TeamAcc a = Acc(kv.Key); a.ClusterSum += kv.Value; a.ClusterN++; }
        }

        /// <summary>Where a bot stands relative to its own base: home, hall, enemy, end (another), out.</summary>
        string RelArea(CTFXBot b)
        {
            if (b.InDropship) return "ship";
            string a = _m.AreaAt(b._state.positionX, b._state.positionY);
            CTFXBotGroup g = b.Group;
            if (g == null) return a;
            if (g.HomeEnd != null)
            {
                if (a == g.HomeEnd) return "home";
                if (a == _m.OtherEnd(g.HomeEnd)) return "enemy";
            }
            else if (g.CoreOf != null && a == g.CoreOf + "-core") return "home";
            if (a.EndsWith("-core", StringComparison.Ordinal)) return "hall";
            return a == "out" ? "out" : "end";
        }

        void NetSample(int now)
        {
            long routes = _m.BotRoutes;
            int perSec = (int)(routes - _routes0) - _lastRoutes;
            _lastRoutes = (int)(routes - _routes0);
            if (perSec > _routePeak) _routePeak = perSec;
            foreach (Player p in _m.Arena.Players)
            {
                if (p == null || p._client == null) continue;
                NetAcc a;
                ulong sent = p._client._packetsSent;
                if (!_net.TryGetValue(p, out a)) { a = new NetAcc(); a.Alias = p._alias; a.LastSent = sent; _net[p] = a; continue; }
                int pps = (int)(sent - a.LastSent);
                a.LastSent = sent;
                a.Packets += pps; a.Samples++;
                if (pps > a.PeakPps) a.PeakPps = pps;
                try { a.LastBps = p._client._stats.SendSpeed; } catch (Exception) { }
            }
        }

        // ================================================================ each stats window
        /// <summary>The window's JSON line, the self-tuning step, and the stats-line summary. Clears the window.</summary>
        internal string Window(int now)
        {
            int ms = Math.Max(1, now - _winStart);
            StringBuilder js = new StringBuilder(2048);
            StringBuilder sum = new StringBuilder(" | monitor");
            js.Append("{\"t\":\"").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("\",\"ms\":").Append(ms).Append(",\"teams\":{");
            int idleFreeAll = 0, freeAll = 0, clusterWorst = 0, defS = 0, defF = 0;
            double topShare = 0;
            bool first = true;
            foreach (TeamAcc a in _teams.Values)
            {
                if (a.Samples == 0) continue;
                if (!first) js.Append(',');
                first = false;
                double idle = a.Idle / (double)a.Samples, fight = a.Fight / (double)a.Samples;
                double idleFree = a.FreeSamples > 0 ? a.IdleFree / (double)a.FreeSamples : 0.0;
                js.Append(Q(a.Name)).Append(":{\"samples\":").Append(a.Samples).Append(",\"idle\":").Append(R(idle)).Append(",\"fight\":").Append(R(fight))
                  .Append(",\"moving\":").Append(R(a.Moving / (double)a.Samples)).Append(",\"idleFree\":").Append(R(idleFree))
                  .Append(",\"why\":").Append(Obj(a.Why)).Append(",\"area\":").Append(Obj(a.Area)).Append(",\"act\":{");
                string[] names = Enum.GetNames(typeof(CTFXAct));
                bool f2 = true;
                int freeActs = 0, topAct = 0;
                for (int i = 0; i < a.Act.Length; i++)
                {
                    if (a.Act[i] == 0) continue;
                    if (!f2) js.Append(',');
                    f2 = false;
                    js.Append(Q(names[i].ToLowerInvariant())).Append(':').Append(a.Act[i]);
                    if (CTFXCommand.Free((CTFXAct)i)) { freeActs += a.Act[i]; if (a.Act[i] > topAct) topAct = a.Act[i]; }
                }
                js.Append("},\"home\":{");
                bool f4 = true;
                for (int i = 0; i < a.HomeAct.Length; i++)
                {
                    if (a.HomeAct[i] == 0) continue;
                    if (!f4) js.Append(',');
                    f4 = false;
                    js.Append(Q(names[i].ToLowerInvariant())).Append(':').Append(a.HomeAct[i]);
                }
                js.Append("},\"hold\":").Append(R(a.Hold / (double)a.Samples));
                js.Append(",\"clusterMax\":").Append(a.ClusterMax).Append(",\"clusterAt\":\"").Append(a.ClusterX).Append(',').Append(a.ClusterY).Append('"')
                  .Append(",\"clusterAvg\":").Append(R(a.ClusterN > 0 ? a.ClusterSum / (double)a.ClusterN : 0)).Append('}');
                if (_m.Cmdr.Manages(a.Name))
                {
                    idleFreeAll += a.IdleFree; freeAll += a.FreeSamples;
                    if (a.ClusterMax > clusterWorst) clusterWorst = a.ClusterMax;
                    if (freeActs >= 4 * Math.Max(1, ms / 1000)) topShare = Math.Max(topShare, topAct / (double)freeActs);   // 4+ free bots on average
                    defS += a.DefendSamples; defF += a.DefendFight;
                }
                sum.Append(" [").Append(Short(a.Name)).Append(" idle ").Append(Pct(idle)).Append(" fight ").Append(Pct(fight))
                   .Append(" free-idle ").Append(Pct(idleFree)).Append(" cluster ").Append(a.ClusterMax);
                if (a.ClusterMax >= 3) sum.Append('@').Append(a.ClusterX).Append(',').Append(a.ClusterY);
                sum.Append(" home ").Append(Pct(Share(a.Area, "home", a.Samples))).Append(" top ").Append(TopWhy(a.Why)).Append(']');
            }
            js.Append("},\"map\":{");
            bool fm = true;
            StringBuilder mapSb = new StringBuilder();
            foreach (KeyValuePair<string, int> kv in _map)
            {
                double avg = _mapTicks > 0 ? kv.Value / (double)_mapTicks : 0.0;
                if (!fm) js.Append(',');
                fm = false;
                js.Append(Q(kv.Key)).Append(':').Append(R(avg));
                mapSb.Append(mapSb.Length == 0 ? "" : " ").Append(kv.Key).Append(' ').Append(avg.ToString("F1", CultureInfo.InvariantCulture));
            }
            sum.Append(" | map avg ").Append(mapSb.ToString());
            _map.Clear(); _mapTicks = 0;
            js.Append("},\"long\":[");
            List<KeyValuePair<CTFXSlot, int>> streaks = new List<KeyValuePair<CTFXSlot, int>>(_idleSince);
            streaks.Sort(delegate(KeyValuePair<CTFXSlot, int> x, KeyValuePair<CTFXSlot, int> y) { return x.Value.CompareTo(y.Value); });
            StringBuilder longs = new StringBuilder();
            for (int i = 0; i < streaks.Count && i < 4; i++)
            {
                CTFXSlot s = streaks[i].Key;
                int secs = (now - streaks[i].Value) / 1000;
                if (secs < 8) break;
                CTFXBot b = _m.GetBot(s.BotId);
                string why;
                _idleWhy.TryGetValue(s, out why);
                string at = b != null ? (b._state.positionX >> 4) + "," + (b._state.positionY >> 4) : "-";
                if (i > 0) js.Append(',');
                js.Append("{\"name\":").Append(Q(s.Name ?? "?")).Append(",\"style\":\"").Append(s.Style).Append("\",\"act\":\"")
                  .Append(s.Intent != null ? s.Intent.Act.ToString() : "-").Append("\",\"s\":").Append(secs).Append(",\"why\":").Append(Q(why ?? "-"))
                  .Append(",\"at\":\"").Append(at).Append("\"}");
                longs.Append(longs.Length == 0 ? "" : ", ").Append(s.Name).Append(' ').Append(secs).Append("s ").Append(why).Append(" @").Append(at);
            }
            js.Append("],\"base\":[");
            int alarmMs = 0;
            for (int i = 0; i < _m.Cmdr.Teams.Count; i++)
            {
                CTFXTeamCmd t = _m.Cmdr.Teams[i];
                if (i > 0) js.Append(',');
                js.Append("{\"team\":").Append(Q(t.Name)).Append(",\"end\":").Append(Q(t.End)).Append(",\"alarms\":").Append(t.Alarms).Append(",\"alarmMs\":").Append(t.AlarmMs)
                  .Append(",\"threatPeak\":").Append(t.ThreatPeak).Append(",\"heard\":").Append(t.Heard).Append(",\"joined\":").Append(t.Joined)
                  .Append(",\"ignored\":").Append(t.Ignored).Append(",\"teleports\":").Append(t.Teleports).Append(",\"respMs\":").Append(t.RespN > 0 ? (t.RespSum / t.RespN).ToString(CultureInfo.InvariantCulture) : "null")
                  .Append(",\"anchorAsks\":").Append(t.AnchorAsks).Append(",\"anchorYes\":").Append(t.AnchorYes).Append(",\"handoffs\":").Append(t.Handoffs).Append('}');
                alarmMs = Math.Max(alarmMs, t.AlarmMs);
                t.Alarms = 0; t.AlarmMs = 0; t.ThreatPeak = 0; t.Heard = 0; t.Joined = 0; t.Ignored = 0; t.Teleports = 0; t.RespSum = 0; t.RespN = 0;
                t.AnchorAsks = 0; t.AnchorYes = 0; t.AnchorNo = 0; t.Handoffs = 0;
            }
            // network
            long routes = _m.BotRoutes - _routes0;
            double routesPerSec = routes * 1000.0 / ms;
            js.Append("],\"net\":{\"botRoutesPerSec\":").Append(R(routesPerSec)).Append(",\"botRoutesPeak\":").Append(_routePeak).Append(",\"clients\":[");
            sum.Append(" | net bot updates ").Append(routesPerSec.ToString("F0", CultureInfo.InvariantCulture)).Append("/s (peak ").Append(_routePeak).Append(')');
            bool f3 = true;
            foreach (NetAcc a in _net.Values)
            {
                if (a.Samples == 0) continue;
                if (!f3) js.Append(',');
                f3 = false;
                double mean = a.Packets / (double)a.Samples;
                js.Append("{\"alias\":").Append(Q(a.Alias)).Append(",\"pps\":").Append(R(mean)).Append(",\"ppsPeak\":").Append(a.PeakPps).Append(",\"bps\":").Append(a.LastBps).Append('}');
                sum.Append(", ").Append(a.Alias).Append(' ').Append(mean.ToString("F0", CultureInfo.InvariantCulture)).Append(" pkt/s (peak ").Append(a.PeakPps).Append(") ")
                   .Append((a.LastBps / 1024.0).ToString("F1", CultureInfo.InvariantCulture)).Append(" kB/s");
            }
            // the worst dead ends this window
            List<KeyValuePair<int, int>> bl = new List<KeyValuePair<int, int>>(_blocked);
            bl.Sort(delegate(KeyValuePair<int, int> x, KeyValuePair<int, int> y) { return y.Value.CompareTo(x.Value); });
            StringBuilder bjs = new StringBuilder(",\"blockedAt\":[");
            StringBuilder bs = new StringBuilder();
            for (int i = 0; i < bl.Count && i < 5; i++)
            {
                int tx = bl[i].Key / 10000 * 4 + 2, ty = bl[i].Key % 10000 * 4 + 2;
                string g;
                _blockedGoal.TryGetValue(bl[i].Key, out g);
                if (i > 0) bjs.Append(',');
                bjs.Append("{\"at\":\"").Append(tx).Append(',').Append(ty).Append("\",\"n\":").Append(bl[i].Value).Append(",\"last\":").Append(Q(g ?? "-")).Append('}');
                if (bl[i].Value >= 5) bs.Append(bs.Length == 0 ? "" : ", ").Append(tx).Append(',').Append(ty).Append(' ').Append(bl[i].Value).Append("s");
            }
            bjs.Append(']');
            if (bs.Length > 0) sum.Append(" | blocked at ").Append(bs.ToString());
            _blocked.Clear(); _blockedGoal.Clear();
            int lost = _m.Turrets.Lost - _turretsLost0;
            double idleFreeShare = freeAll > 0 ? idleFreeAll / (double)freeAll : 0.0;
            double defendFight = defS > 0 ? defF / (double)defS : -1.0;
            js.Append("]}").Append(bjs.ToString()).Append(",\"tune\":{\"idleFree\":").Append(R(idleFreeShare)).Append(",\"clusterWorst\":").Append(clusterWorst).Append(",\"topShare\":").Append(R(topShare))
              .Append(",\"turretsLost\":").Append(lost).Append(",\"defendFight\":").Append(R(defendFight)).Append(",\"knobs\":").Append(Q(_m.Cmdr.Knobs())).Append("}}");
            try { File.AppendAllText(FilePath, js.ToString() + "\n"); } catch (Exception) { }
            _m.Cmdr.Tune(idleFreeShare, clusterWorst, topShare, alarmMs, lost, defendFight, ms);
            if (longs.Length > 0) sum.Append(" | idle longest: ").Append(longs.ToString());
            // window reset (idle streaks carry on: they are the seats' own)
            _teams.Clear(); _net.Clear();
            foreach (Player p in _m.Arena.Players)
            {
                if (p == null || p._client == null) continue;
                NetAcc a = new NetAcc(); a.Alias = p._alias; a.LastSent = p._client._packetsSent; _net[p] = a;
            }
            _winStart = now; _routes0 = _m.BotRoutes; _routePeak = 0; _lastRoutes = 0; _turretsLost0 = _m.Turrets.Lost;
            _last = sum.ToString();
            return _last;
        }

        // ================================================================ helpers
        static double Share(Dictionary<string, int> d, string k, int total)
        {
            int n;
            return total > 0 && d.TryGetValue(k, out n) ? n / (double)total : 0.0;
        }

        static string TopWhy(Dictionary<string, int> d)
        {
            string best = "-";
            int bn = 0;
            foreach (KeyValuePair<string, int> kv in d) if (kv.Value > bn) { bn = kv.Value; best = kv.Key; }
            return best;
        }

        static string Short(string team)
        {
            if (team == null) return "?";
            return team.Length > 14 ? team.Substring(0, 14) : team;
        }

        static string Obj(Dictionary<string, int> d)
        {
            StringBuilder sb = new StringBuilder("{");
            bool f = true;
            foreach (KeyValuePair<string, int> kv in d)
            {
                if (!f) sb.Append(',');
                f = false;
                sb.Append(Q(kv.Key)).Append(':').Append(kv.Value);
            }
            return sb.Append('}').ToString();
        }

        static string Q(string s)
        {
            StringBuilder sb = new StringBuilder("\"");
            foreach (char ch in s ?? "")
            {
                if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
                else if (ch < ' ') sb.Append(' ');
                else sb.Append(ch);
            }
            return sb.Append('"').ToString();
        }

        static string R(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }
        static string Pct(double v) { return ((int)Math.Round(v * 100)).ToString(CultureInfo.InvariantCulture) + "%"; }
    }
}
/* PARSER GUARD - keep last */
