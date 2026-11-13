// CTF:X server-side bots - MAKE ROOM (Travis + Galaxy 10-22: "allow players to change to any team, keep the team size limits, and for each
// player who joins ONE bot leaves and goes to a team that needs a player") and ?bot <name> (Galaxy: "Jett 45 the medic on Titan doesn't
// stay home" - a bot has to be findable by name, and the same name has to be the same player every boot).
//  - Each bot team's size is its line's seat count (CTFXSlot.HomeTeam; the elite clan's moves count where the seat went).
//  - A human on a bot team (?team, ?t, the F-key join, the event's own team pick - nothing tells the script, so every 2.5 s the humans' teams
//    are compared with the last look) displaces ONE seat of that team per human: a bot of the human's class if there is one (a player
//    taking the medic role replaces the medic bot), else the lowest-value seat; never a flag carrier or a bot attached to a human, and the
//    team's last medic / summoner only if nothing else is left. The seat goes to the team most below its own size (its row changes team
//    the way the clan's recruits do: SetRowTeam, never a dropped row), else it is BENCHED: its body waits, idle, in the dropship.
//  - When the human leaves the team, changes class (a re-pick) or the knob goes off, the seat comes back. A displacement waits 2 s of
//    unchanged humans, a return 8 s: a human flipping teams does not shuffle bots about.
//  - A team with more humans than seats sends the newcomer back to spec ("<team> is full (N)").
//  - makeRoom=on|off in CTFXBots.txt (default on); *ctfxbots reload + *ctfxbots on applies it.
/* PARSER GUARD: keep a block comment after the last line comment */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

using InfServer.Game;

using Assets;

namespace InfServer.Script.GameType_CTF
{
    internal sealed partial class CTFXBotManager
    {
        internal int RoomJoins, RoomMoved, RoomBenched, RoomReturned, RoomRefused, RoomMismatch;   // stats line
        int _roomAt;
        long _roomMaxTicks;                                                                 // the slowest RoomWork so far (stats)
        readonly Dictionary<Player, Team> _roomTeamOf = new Dictionary<Player, Team>();   // each human's team at the last look
        readonly Dictionary<Player, int> _roomSince = new Dictionary<Player, int>();      // when he took it (ticks)
        readonly Dictionary<Team, string> _roomSig = new Dictionary<Team, string>();      // a team's humans and their classes at the last look
        readonly Dictionary<Team, int> _roomSigAt = new Dictionary<Team, int>();          // when that last changed

        /// <summary>Every seat's callsign in roster order (Start): the name, and so the seeded personality, is the same each boot.</summary>
        void AssignNames()
        {
            _nameNext = 0;
            foreach (CTFXBotGroup g in _groups)
                foreach (CTFXSlot s in g.Slots)
                {
                    if (s.Retired) continue;
                    NameFor(s);
                    if (s.Human == null)
                    {   // rolled now so ?bot has a profile before the seat's first spawn; Setup keeps it
                        s.Human = CTFXHuman.Roll(Rng, s.Name);
                        if (s.Style == CTFXStyle.Bountier) s.Human.MakeElite(new Random(s.Human.Seed ^ 0x5bd1e995));
                    }
                }
        }

        void RoomReset()
        {
            RoomClear();
            foreach (CTFXBotGroup g in _groups)
                foreach (CTFXSlot s in g.Slots) { s.HomeTeam = g.Team; s.Benched = false; s.RoomHome = null; }
            // the roster as it starts (two boots print the same lines): seat = name style[/class set by mustHave or the line] tier react comply
            foreach (CTFXBotGroup g in _groups)
            {
                if (g.Team == null) continue;
                StringBuilder sb = new StringBuilder("[CTFXRoster] " + g.Team._name + ":");
                foreach (CTFXSlot s in g.Slots)
                {
                    if (s.Retired) continue;
                    sb.Append(' ').Append(s.Name).Append(' ').Append(s.Style.ToString().ToLowerInvariant());
                    if (s.ForceClass != 0) sb.Append('/').Append(CTFXBot.ShortClass(s.ForceClass));
                    if (s.Human != null) sb.Append(' ').Append(s.Human.Tier[0]).Append(s.Human.ReactMean).Append('/').Append((int)Math.Round(s.Human.Comply * 100)).Append(',');
                }
                Out(sb.ToString());
            }
        }

        void RoomClear() { _roomTeamOf.Clear(); _roomSince.Clear(); _roomSig.Clear(); _roomSigAt.Clear(); }

        internal void RoomStep(int now)
        {
            if (now - _roomAt < 2500) return;
            _roomAt = now;
            if (!_on || _groups.Count == 0 || _cfg == null) return;
            long t0 = Stopwatch.GetTimestamp();
            try { RoomWork(now); } catch (Exception ex) { LogErr("room", ex); }
            long dt = Stopwatch.GetTimestamp() - t0;
            if (dt > _roomMaxTicks) _roomMaxTicks = dt;
        }

        static int HumanClass(Player p)
        {
            SkillInfo sk = CTFClass.Resolve(p);
            return sk != null ? sk.SkillId : 0;
        }

        int SeatClass(CTFXSlot s)
        {
            CTFXBot b = GetBot(s.BotId);
            return b != null ? b.ClassId : s.ForceClass;
        }

        static bool SummonClass(int cls) { return cls == 7 || cls == 11 || cls == 12 || cls == CTFXBot.SK_MEDIC; }   // Squad Leader, Sergeant, Captain, Medic

        /// <summary>What a seat is worth its team: its tier plus what its style holds (a post or an engineer is the base; a roamer is a spare).</summary>
        static int SeatValue(CTFXSlot s)
        {
            int v = 1;
            if (s.Human != null) v = s.Human.Tier == "ace" ? 3 : s.Human.Tier == "veteran" ? 2 : s.Human.Tier == "regular" ? 1 : 0;
            switch (s.Style)
            {
                case CTFXStyle.Post: case CTFXStyle.Engineer: v += 3; break;
                case CTFXStyle.Guard: case CTFXStyle.Stormer: v += 2; break;
                case CTFXStyle.Assault: case CTFXStyle.Sniper: case CTFXStyle.Drain: case CTFXStyle.Bountier: v += 1; break;
            }
            if (s.Courier) v += 1;
            return v;
        }

        bool RoomCarrying(CTFXSlot s)
        {
            CTFXBot b = GetBot(s.BotId);
            return b != null && Flags != null && Flags.Carrying(b);
        }

        /// <summary>May this seat be sent away? Not while it carries a flag; not while it is attached to a human (an answered call, his summon).</summary>
        bool RoomEligible(CTFXSlot s)
        {
            if (s.Retired) return false;
            if (s.Benched || s.RoomHome != null) return true;     // already away: it stays in the pick (only a flag held stops its return)
            return !RoomCarrying(s) && s.AttachTo == null;
        }

        double RoomScore(CTFXSlot s, int hc, List<CTFXSlot> homes, HashSet<CTFXSlot> used)
        {
            int cls = SeatClass(s);
            double sc = 0;
            if (hc != 0 && cls == hc) sc += 1000;                  // the human takes this role: its bot makes room
            else
            {   // the team's last medic / summoner stays unless the human replaces that role
                bool otherMed = false, otherSum = false;
                foreach (CTFXSlot o in homes)
                {
                    if (o == s || used.Contains(o)) continue;
                    int oc = SeatClass(o);
                    if (oc == CTFXBot.SK_MEDIC) otherMed = true;
                    if (SummonClass(oc)) otherSum = true;
                }
                if (cls == CTFXBot.SK_MEDIC && !otherMed) sc -= 600;
                if (SummonClass(cls) && !otherSum) sc -= 500;
            }
            sc -= SeatValue(s) * 10;
            if (s.Benched || s.RoomHome != null) sc += 200;        // already away: keep it so (no churn; only a class match outranks it)
            CTFXBot b = GetBot(s.BotId);
            if (b == null || b.IsDead || b.InDropship) sc += 5;    // cheapest to lose
            return sc;
        }

        /// <summary>One seat per human, the best match first; pairs of (seat, the human it makes room for).</summary>
        List<KeyValuePair<CTFXSlot, Player>> RoomPick(List<CTFXSlot> homes, List<Player> hs)
        {
            List<KeyValuePair<CTFXSlot, Player>> res = new List<KeyValuePair<CTFXSlot, Player>>();
            HashSet<CTFXSlot> used = new HashSet<CTFXSlot>();
            foreach (Player h in hs)
            {
                if (res.Count >= homes.Count) break;
                int hc = HumanClass(h);
                CTFXSlot best = null;
                double bs = double.MinValue;
                foreach (CTFXSlot s in homes)
                {
                    if (used.Contains(s) || !RoomEligible(s)) continue;
                    double sc = RoomScore(s, hc, homes, used);
                    if (sc > bs) { bs = sc; best = s; }
                }
                if (best == null) break;
                used.Add(best);
                res.Add(new KeyValuePair<CTFXSlot, Player>(best, h));
            }
            return res;
        }

        /// <summary>The group of team u a seat joins (its biggest ordinary one), or null.</summary>
        CTFXBotGroup RoomGroupOf(Team u)
        {
            CTFXBotGroup best = null;
            foreach (CTFXBotGroup g in _groups)
            {
                if (g.Team != u) continue;
                if (best == null || (best.Outside || best.Ambush || best.DropPack) && !(g.Outside || g.Ambush || g.DropPack) || g.Slots.Count > best.Slots.Count && !(g.Outside || g.Ambush || g.DropPack)) best = g;
            }
            return best;
        }

        /// <summary>The seat leaves its body and group for another team's group; its player-list row changes team as the clan's recruits' do.</summary>
        void RoomRegroup(CTFXSlot s, CTFXBotGroup to, int now)
        {
            CTFXBotGroup from = s.Group;
            CTFXBot b = GetBot(s.BotId);
            if (b != null) { ElectNewLeader(b); RemoveQuietly(b); }      // the same id and row come back with the next body
            from.Slots.Remove(s);
            to.Slots.Add(s);
            s.Group = to;
            s.Index = to.Slots.Count - 1;
            if (Cmdr != null) Cmdr.MoveSeat(s, to);
            SetRowTeam(s, to.Team);
            QueueSpawn(s, now + 600 + Rng.Next(700));
        }

        static void Bump(Dictionary<Team, int> d, Team t, int by) { int c; d.TryGetValue(t, out c); d[t] = c + by; }

        string RowTeamText(CTFXSlot s)
        {
            return "group " + (s.Group != null && s.Group.Team != null ? s.Group.Team._name : "?") + ", row " + (s.Entry != null && s.Entry._team != null ? s.Entry._team._name : "-");
        }

        void RoomDisplace(CTFXSlot s, Team home, Player human, Dictionary<Team, List<CTFXSlot>> seats, Dictionary<Team, int> present, int now)
        {
            int cls = SeatClass(s);
            // the team most below its own size that no human has pushed over it
            Team dest = null;
            int bestDef = 0;
            foreach (KeyValuePair<Team, List<CTFXSlot>> kv in seats)
            {
                if (kv.Key == home) continue;
                int p;
                present.TryGetValue(kv.Key, out p);
                int def = kv.Value.Count - p;
                if (def > bestDef && RoomGroupOf(kv.Key) != null) { bestDef = def; dest = kv.Key; }
            }
            string who = (s.Name ?? "a bot") + (cls > 0 ? " (" + CTFXBot.ClassName(cls) + ")" : "");
            if (dest != null)
            {
                s.RoomHome = s.Group;
                s.Benched = false;
                RoomRegroup(s, RoomGroupOf(dest), now);
                Bump(present, home, -1); Bump(present, dest, 1);
                RoomMoved++;
                Out("[CTFXRoom] " + who + " left " + home._name + " for " + dest._name + " (" + human._alias + " joined as class " + HumanClass(human) + ", seat class " + cls + ") | " + RowTeamText(s));
                human.sendMessage(0, who + " left " + home._name + " to make room for you and went to " + dest._name);
            }
            else
            {
                s.RoomHome = s.Group;
                s.Benched = true;
                CTFXBot b = GetBot(s.BotId);
                if (b != null) { ElectNewLeader(b); RemoveQuietly(b); }  // its next body spawns in the ship and idles there (CTFXBot.Think, Slot.Benched)
                QueueSpawn(s, now + 600 + Rng.Next(700));
                Bump(present, home, -1);
                RoomBenched++;
                Out("[CTFXRoom] " + who + " benched on " + home._name + " (" + human._alias + " joined as class " + HumanClass(human) + ", seat class " + cls + ") | " + RowTeamText(s));
                human.sendMessage(0, who + " left " + home._name + " to make room for you");
            }
        }

        /// <summary>A displaced seat comes home. False = it stays (a flag in its hands, or its new team needs it more).</summary>
        bool RoomReturn(CTFXSlot s, Team home, Dictionary<Team, List<CTFXSlot>> seats, Dictionary<Team, int> present, int now)
        {
            if (RoomCarrying(s)) return false;
            if (s.Benched)
            {
                int ph0;
                present.TryGetValue(home, out ph0);
                if (ph0 >= seats[home].Count) return false;        // guests from other teams fill its place: it stays benched until they go home
                s.Benched = false; s.RoomHome = null;
                CTFXBot b = GetBot(s.BotId);
                if (b != null && b.InDropship) b.LeaveShipSoon(now);
                Bump(present, home, 1);
                RoomReturned++;
                Out("[CTFXRoom] " + (s.Name ?? "a bot") + " back on " + home._name + " from the bench | " + RowTeamText(s));
                return true;
            }
            CTFXBotGroup back = s.RoomHome;
            if (back == null || back.Team == null || !_groups.Contains(back)) { s.RoomHome = null; return true; }
            Team cur = s.Group.Team;
            int pc, ph;
            present.TryGetValue(cur, out pc); present.TryGetValue(home, out ph);
            int nCur = seats.ContainsKey(cur) ? seats[cur].Count : 0;
            int defCurAfter = Math.Max(0, nCur - (pc - 1)), defHome = seats[home].Count - ph;
            if (defCurAfter > defHome) return false;               // where it is now needs it more than home does
            RoomRegroup(s, back, now);
            s.RoomHome = null;
            Bump(present, cur, -1); Bump(present, home, 1);
            RoomReturned++;
            Out("[CTFXRoom] " + (s.Name ?? "a bot") + " back on " + home._name + " from " + cur._name + " | " + RowTeamText(s));
            return true;
        }

        void RoomWork(int now)
        {
            bool on = _cfg.MakeRoom;
            Dictionary<Team, List<CTFXSlot>> seats = new Dictionary<Team, List<CTFXSlot>>();     // by home team
            Dictionary<Team, int> present = new Dictionary<Team, int>();                         // bots on the team now (benched ones not counted) + humans
            foreach (CTFXBotGroup g in _groups)
                foreach (CTFXSlot s in g.Slots)
                {
                    if (s.Retired) continue;
                    if (s.HomeTeam != null)
                    {
                        List<CTFXSlot> l;
                        if (!seats.TryGetValue(s.HomeTeam, out l)) { l = new List<CTFXSlot>(); seats[s.HomeTeam] = l; }
                        l.Add(s);
                    }
                    if (g.Team != null && !s.Benched)
                    {
                        int c;
                        present.TryGetValue(g.Team, out c);
                        present[g.Team] = c + 1;
                    }
                }

            // the humans on bot teams, and who just arrived
            Dictionary<Team, List<Player>> hum = new Dictionary<Team, List<Player>>();
            HashSet<Player> here = new HashSet<Player>();
            foreach (Player p in _arena.Players)
            {
                if (p == null || p._team == null || p.IsSpectator || !p._bIngame || p._team.IsSpec) continue;
                here.Add(p);
                Team prev;
                bool known = seats.ContainsKey(p._team);
                if (!_roomTeamOf.TryGetValue(p, out prev) || prev != p._team)
                {
                    _roomTeamOf[p] = p._team; _roomSince[p] = now;
                    if (known) RoomJoins++;
                }
                if (!known) continue;
                List<Player> hl;
                if (!hum.TryGetValue(p._team, out hl)) { hl = new List<Player>(); hum[p._team] = hl; }
                hl.Add(p);
            }
            List<Player> gone = new List<Player>();
            foreach (Player p in _roomTeamOf.Keys) if (!here.Contains(p)) gone.Add(p);
            foreach (Player p in gone) { _roomTeamOf.Remove(p); _roomSince.Remove(p); }

            // more humans than seats: the newest go back to spec
            foreach (KeyValuePair<Team, List<CTFXSlot>> kv in seats)
            {
                List<Player> hs;
                if (!hum.TryGetValue(kv.Key, out hs)) continue;
                hs.Sort(delegate(Player a, Player b) { return _roomSince[a].CompareTo(_roomSince[b]); });
                while (on && hs.Count > kv.Value.Count)
                {
                    Player p = hs[hs.Count - 1];
                    hs.RemoveAt(hs.Count - 1);
                    RoomRefused++;
                    try
                    {
                        p.sendMessage(-1, kv.Key._name + " is full (" + kv.Value.Count + ")");
                        p.spec("spec");
                    }
                    catch (Exception ex) { LogErr("roomRefuse", ex); }
                    _roomTeamOf.Remove(p); _roomSince.Remove(p);
                    Out("[CTFXRoom] " + p._alias + " sent back to spec: " + kv.Key._name + " is full (" + kv.Value.Count + ")");
                }
                int pc;
                present.TryGetValue(kv.Key, out pc);
                present[kv.Key] = pc + hs.Count;
            }
            // teams that only bots are on (a seat's own team has no entry above when it has no humans) already hold their bot count

            foreach (KeyValuePair<Team, List<CTFXSlot>> kv in seats)
            {
                Team T = kv.Key;
                List<CTFXSlot> homes = kv.Value;
                List<Player> hs;
                if (!hum.TryGetValue(T, out hs)) hs = new List<Player>();
                StringBuilder sg = new StringBuilder();
                foreach (Player p in hs) sg.Append(p._alias).Append(':').Append(HumanClass(p)).Append(',');
                string sig = sg.ToString();
                string last;
                if (!_roomSig.TryGetValue(T, out last) || last != sig) { _roomSig[T] = sig; _roomSigAt[T] = now; }
                int stable = now - _roomSigAt[T];

                List<CTFXSlot> disp = new List<CTFXSlot>();
                foreach (CTFXSlot s in homes) if (s.Benched || s.RoomHome != null) disp.Add(s);
                List<KeyValuePair<CTFXSlot, Player>> ideal = on ? RoomPick(homes, hs) : new List<KeyValuePair<CTFXSlot, Player>>();
                HashSet<CTFXSlot> idealSet = new HashSet<CTFXSlot>();
                foreach (KeyValuePair<CTFXSlot, Player> kp in ideal) idealSet.Add(kp.Key);

                bool full = !on || stable >= 8000;          // returns (and re-picks) wait for 8 s of the same humans; a first displacement for 2 s
                int allow = full ? ideal.Count : (stable >= 2000 ? Math.Max(0, ideal.Count - disp.Count) : 0);
                foreach (KeyValuePair<CTFXSlot, Player> kp in ideal)
                {
                    if (allow <= 0) break;
                    if (kp.Key.Benched || kp.Key.RoomHome != null) continue;
                    RoomDisplace(kp.Key, T, kp.Value, seats, present, now);
                    allow--;
                }
                if (full)      // after the displacements: a re-pick (the human changed class) sends the new seat out first, then lets the old one back
                    foreach (CTFXSlot s in disp)
                        if (!idealSet.Contains(s)) RoomReturn(s, T, seats, present, now);
            }

            // the old invincibility bug watch: a live body whose team is not its row's / its group's
            foreach (CTFXBotGroup g in _groups)
                foreach (CTFXSlot s in g.Slots)
                {
                    CTFXBot b = GetBot(s.BotId);
                    if (b == null || b._team == null || s.Retired) continue;
                    if ((s.Entry != null && s.Entry._team != b._team) || (g.Team != null && g.Team != b._team))
                    {
                        RoomMismatch++;
                        if (RoomMismatch <= 8) Out("[CTFXRoom] TEAM MISMATCH " + s.Name + ": body " + b._team._name + " | " + RowTeamText(s));
                    }
                    if (s.Benched && !b.InDropship && !b.IsDead)
                    {   // something walked a benched body out of the ship: a new body goes back in
                        ElectNewLeader(b); RemoveQuietly(b); QueueSpawn(s, now + 600);
                        Out("[CTFXRoom] " + s.Name + " was benched but out of the ship: respawned in it");
                    }
                }
        }

        internal string RoomText()
        {
            int benched = 0, away = 0;
            foreach (CTFXBotGroup g in _groups)
                foreach (CTFXSlot s in g.Slots)
                {
                    if (s.Retired) continue;
                    if (s.Benched) benched++;
                    else if (s.RoomHome != null) away++;
                }
            return " | room " + (_cfg != null && _cfg.MakeRoom ? "on" : "off") + ": joins " + RoomJoins + ", bots moved " + RoomMoved + ", benched " + RoomBenched
                + " (now " + benched + " benched, " + away + " away), returned " + RoomReturned + ", refusals " + RoomRefused + ", team mismatches " + RoomMismatch + ", slowest step " + (_roomMaxTicks * 1000.0 / Stopwatch.Frequency).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " ms";
        }

        // ================================================================ ?bot <name>
        /// <summary>?bot &lt;name&gt;: exact name, else names that start with it, else names that contain it. One match = its card, several = a list.</summary>
        internal bool BotInfo(Player p, string payload)
        {
            try
            {
                string q = (payload ?? "").Trim();
                while (q.IndexOf("  ", StringComparison.Ordinal) >= 0) q = q.Replace("  ", " ");
                q = q.Trim('{', '}').Trim();                                   // ?bot {Jett 45} as it shows on the list (nameWrap=)
                if (!_on || _groups.Count == 0) { p.sendMessage(0, "The CTF:X bots are not running right now."); return true; }
                if (q.Length == 0) { p.sendMessage(0, "?bot <name> looks a bot up, e.g. ?bot Jett 45 (a part of the name works)."); return true; }
                List<CTFXSlot> exact = new List<CTFXSlot>(), starts = new List<CTFXSlot>(), has = new List<CTFXSlot>();
                foreach (CTFXBotGroup g in _groups)
                    foreach (CTFXSlot s in g.Slots)
                    {
                        if (s.Retired || s.Name == null) continue;
                        if (s.Name.Equals(q, StringComparison.OrdinalIgnoreCase)) exact.Add(s);
                        else if (s.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) starts.Add(s);
                        else if (s.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) has.Add(s);
                    }
                List<CTFXSlot> m = exact.Count > 0 ? exact : starts.Count > 0 ? starts : has;
                if (m.Count == 0) { p.sendMessage(0, "No bot named '" + q + "'."); return true; }
                if (m.Count > 1)
                {
                    StringBuilder sb = new StringBuilder();
                    for (int i = 0; i < m.Count && i < 8; i++) { if (i > 0) sb.Append(", "); sb.Append(m[i].Name); }
                    p.sendMessage(0, m.Count + " bots match '" + q + "': " + sb + (m.Count > 8 ? ", ..." : "") + " - type more of the name.");
                    return true;
                }
                BotCard(p, m[0]);
            }
            catch (Exception ex) { LogErr("botinfo", ex); }
            return true;
        }

        void BotCard(Player p, CTFXSlot s)
        {
            CTFXBot b = GetBot(s.BotId);
            int cls = SeatClass(s);
            string team = s.Group != null && s.Group.Team != null ? s.Group.Team._name : "no team";
            string away = s.Benched ? " (benched: a player took its seat)" : s.RoomHome != null && s.RoomHome.Team != null ? " (from " + s.RoomHome.Team._name + ")" : "";
            CTFXHuman h = s.Human;
            string line1 = s.Name + " - " + team + away + " " + (cls > 0 ? CTFXBot.ClassName(cls) : "class unknown") + ", " + s.Style.ToString().ToLowerInvariant();
            if (h != null) line1 += " | " + h.Tier + " (react " + h.ReactMean + " ms, follows calls " + (int)Math.Round(h.Comply * 100) + "%)";
            p.sendMessage(0, line1);
            string now;
            if (s.Benched) now = "benched in the dropship";
            else if (b == null) now = "waiting to spawn";
            else if (b.IsDead) now = "dead";
            else if (b.InDropship) now = "in the dropship";
            else
            {
                string act = s.Intent != null && s.Intent.Act != CTFXAct.None ? s.Intent.Act.ToString().ToLowerInvariant() : (b.Why ?? "-");
                if (s.Intent != null && s.Intent.Act == CTFXAct.Raid && s.Intent.RaidEnd != null) act += " " + s.Intent.RaidEnd.Replace('-', ' ');
                string place = PlaceName(b._state.positionX, b._state.positionY);
                now = act + (place != null && place != act ? ", " + place : "") + (RoomCarrying(s) ? ", carrying a flag" : "");
            }
            p.sendMessage(0, "now: " + now + " | " + s.Kills + " kills " + s.Deaths + " deaths, bounty " + s.Bounty + (s.Courier ? " | flag courier" : ""));
        }
    }
}
/* PARSER GUARD - keep last */
