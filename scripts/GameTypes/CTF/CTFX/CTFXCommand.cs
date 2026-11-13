// CTF:X server-side bots - the TEAM COMMANDER and each bot's own INTENT (Travis 10-12: "only a few defenders at different choke
// points are needed when things are passive ... 1 or 2 anchors for each base while the rest of the bots on the map attempt to
// fight traffic inside or assault other bases"; "a lot more wandering autonomy for individual players even on teams, they
// work way too well together").
//
// Light touch. The commander of a base team (a group whose home is a base end that has an enemy end) only:
//   ANCHORS  keeps `anchors` bots holding the base's choke points (the end's mouth, its turret rooms; posts count). It ASKS one
//            bot near home at a time; the bot says yes by its taste and compliance (CTFXHuman), holds for a few minutes, gets
//            bored and hands off once someone else is home to take over.
//   ALARM    `alarmAt` or more enemies (bots and players) in the base raise an alarm. Each free teammate hears it after its own
//            delay (it has to notice the call) and decides by compliance and distance whether to go; some ignore it. No more go
//            than ceil(enemies x defendRatio) - anchors, so a lone raider is the anchors' and the turrets' problem.
// Everything else is the bot's own choice (CTFXIntent), re-picked when it gets bored of it and at each respawn:
//   skirmish  fight the traffic in the building's middle hall       raid   push into an enemy end (its own pick, own objective)
//   wander    anywhere in and around the building, long legs      hunt   where the fighting is (contact heat, 20 s half-life)
//   outside   brawl out in the neutral landing areas (A-H, the mountain sides between the buildings): only some like it, and
//             no more than `outsideShare` of the bots do it at once (Travis 10-12: "handfuls of bots fighting out in the neutral
//             spawns ... but most should have a variety of objectives")
// A raid picks its end when it starts: the other end of its own building more often than not, else any other base (nearer likelier).
// REJOIN (10-12 census: with every spawn random, 39 of 82 bots were walking the outside and half the bases stood empty): a bot that
// lands from the dropship far from its objective (its choke, its base, its hall, its raid target) usually takes Suit Teleport to
// the teammate nearest that objective, as a player does (energy, anti-warp and cooldown as TeleportTo): onto one already there, or
// a hop part of the way; it looks again every 12-20 s while still far. Wanderers, hunters and outside brawlers stay where they land.
// Every group whose home is a base end is run here: Titan, Collective and the private base teams (all 8 strongholds, 10-12).
// weighted by its style, its personal tastes, and what its teammates already do (herd aversion: the more of the team on one
// activity or one spot, the less it appeals). Storm squads (CTFXSquads) and engineers with a turret to build are left alone.
//
// SELF-TUNING: each stats window CTFXMonitor hands its measures to Tune(): the idle share of the free bots, idle clusters,
// how lopsided the activity mix is, how the base defence went. Each knob moves one bounded step toward its target; every
// change is logged and kept in CTFX/CTFXTune.txt, which the next start reads (tune=0 ignores it; *ctfxbots tune reset).
/* PARSER GUARD: keep a block comment after the last line comment */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_CTF
{
    internal enum CTFXAct { None, Anchor, Defend, Skirmish, Raid, Wander, Hunt, Build, Storm, Outside }

    /// <summary>A seat's current activity, kept on the slot across lives.</summary>
    internal sealed class CTFXIntent
    {
        public CTFXAct Act, Last;
        public int Since, Until;            // ticks
        public int Choke = -1;              // anchor: index into its team's chokes
        public int DeclineUntil;            // turned an anchor ask down: not asked again before this
        public int IgnoreUntil;             // ignored an alarm: not called again before this
        public bool Responded;              // defend: reached the fight (response time counted)
        public int JoinedAt;                // defend: when it decided to go
        public string RaidEnd;              // raid: the end it goes for this time
        public bool Foothold;               // raid: its team foothold in brown (CTFXLines.cs), kept until it dies or leaves
        public int Picks;
    }

    /// <summary>A choke point of a base end: where an anchor holds, and the point it watches.</summary>
    internal sealed class CTFXChoke
    {
        public string Name;
        public int Tx, Ty;                  // tile
        public int WatchX, WatchY;          // px
    }

    /// <summary>One base team: its ends, chokes, seats and alarm.</summary>
    internal sealed class CTFXTeamCmd
    {
        public Team Team;
        public string End, Enemy, Quad;
        public readonly List<CTFXChoke> Chokes = new List<CTFXChoke>();
        public readonly List<CTFXSlot> Seats = new List<CTFXSlot>();
        public bool Alarm;
        public int AlarmSince, ClearSince, RaisedAt, CallAt, AskAt, ShortSince;
        public int Threat, ThreatX, ThreatY;
        public int LastThreatAt;            // the last tick an enemy stood in its base (a skirmish push follows a sweep)
        public readonly List<KeyValuePair<CTFXSlot, int>> Hearing = new List<KeyValuePair<CTFXSlot, int>>();   // seat, tick it notices
        // a teammate's call for home (CTFXCommand.HumanAlarm): the alarm holds until HumanUntil with nobody in sight; Desperate = %28
        public int HumanUntil;
        public bool Desperate, HumanAcked;
        public int HumanCalls, HumanJoined;
        // per stats window (CTFXMonitor reads and clears)
        public int Alarms, Heard, Joined, Ignored, AlarmMs, ThreatPeak, AnchorAsks, AnchorYes, AnchorNo, Handoffs, Teleports;
        public long RespSum;
        public int RespN;
        public string Name { get { return Team != null ? Team._name : "?"; } }
    }

    internal sealed partial class CTFXCommand
    {
        readonly CTFXBotManager _m;
        internal readonly List<CTFXTeamCmd> Teams = new List<CTFXTeamCmd>();
        int _lastStep, _setupAt;
        // knobs (CTFXBots.txt), the last four self-tuned
        internal bool On = true, TuneOn = true;
        internal int Anchors = 2, AlarmAt = 2;
        internal double DefendRatio = 1.5, PauseScale = 1.0, HerdK = 1.2, ConfigDefend = 1.5;
        internal double OutsideShare = 0.15;
        internal double BrownWander = 0.35;   // share of the other teams' wanders that go through the brown building
        internal int BrownWanders, BrownSent, BrownPulls, BrownTarget = 30;
        int _brownAt = -100000, _brownN, _brownPullAt = -100000;
        long _brownSum, _brownSamples, _brownOwnIn, _brownOwnOut, _brownOwnShip, _brownOwnDead;
        readonly Dictionary<string, int> _brownOwnAct = new Dictionary<string, int>();

        /// <summary>Bots in the brown building (both ends, the hall and the routes between), counted once a second.</summary>
        internal int BrownCount(int now)
        {
            if (now - _brownAt < 1000) return _brownN;
            _brownAt = now; _brownN = 0;
            foreach (CTFXBot o in _m.LiveBots)
            {
                bool own = o.Group != null && o.Group.Quad == "brown" && o.Slot != null && o.Slot.Intent != null;
                if (o.IsDead || o.Released) { if (own) _brownOwnDead++; continue; }
                if (o.InDropship) { if (own) _brownOwnShip++; continue; }
                bool inb = _m.InQuad("brown", o);
                if (inb) _brownN++;
                if (own) { if (inb) _brownOwnIn++; else { _brownOwnOut++; string k = o.Slot.Intent.Act.ToString(); int c; _brownOwnAct.TryGetValue(k, out c); _brownOwnAct[k] = c + 1; } }
            }
            _brownSum += _brownN; _brownSamples++;
            return _brownN;
        }

        /// <summary>Brown below its quota and this team is not a brown team (Travis 10-13: the biggest hotspot at all times).</summary>
        bool BrownShort(CTFXTeamCmd t, int now)
        {
            return _m.FocusEnd != null && t.Quad != "brown" && BrownTarget > 0 && BrownCount(now) < BrownTarget;
        }

        string ActText() { string r = ""; foreach (KeyValuePair<string, int> kv in _brownOwnAct) r += (r.Length > 0 ? " " : "") + kv.Key + ":" + kv.Value; return r; }

        string Avg(long v) { return _brownSamples > 0 ? (v / (double)_brownSamples).ToString("0.0") : "-"; }

        internal string BrownText()
        {
            return " | brown building: now " + _brownN + ", avg " + (_brownSamples > 0 ? (_brownSum / (double)_brownSamples).ToString("0.0") : "-")
                + " (quota " + BrownTarget + "); brown teams in " + Avg(_brownOwnIn) + " out " + Avg(_brownOwnOut) + " ship " + Avg(_brownOwnShip)
                + " dead " + Avg(_brownOwnDead) + " (out doing " + ActText() + ")"
                + "; raids sent " + BrownSent + ", pulled off other acts " + BrownPulls + ", wanders through " + BrownWanders;
        }  // at most this share of the commander's bots brawl outside at once
        internal int Picks, Rejoins, Hops, RejoinWalks;
        internal readonly List<string> TuneLog = new List<string>();
        readonly Dictionary<CTFXSlot, int> _hopAt = new Dictionary<CTFXSlot, int>();    // seat -> next look at warping toward its objective
        readonly HashSet<CTFXSlot> _fresh = new HashSet<CTFXSlot>();                    // seats whose next look is their first after landing
        // beside the config, not in logs/ (the server rotates logs/ away at every start)
        static readonly string TunePath = Path.Combine("scripts", Path.Combine("GameTypes", Path.Combine("CTF", Path.Combine("CTFX", "CTFXTune.txt"))));

        // contact heat: where the fighting is, cells of 10 tiles; { heat, tick, last x, last y }
        readonly Dictionary<int, double[]> _heat = new Dictionary<int, double[]>();
        readonly List<CTFXSlot> _cand = new List<CTFXSlot>();

        internal CTFXCommand(CTFXBotManager m) { _m = m; }

        // ================================================================ setup
        internal void Clear()
        {
            ClearLines();
            Teams.Clear(); _heat.Clear(); Picks = 0; Rejoins = 0; Hops = 0; RejoinWalks = 0; _hopAt.Clear();
        }

        /// <summary>A command per base team (its groups' seats pooled), the chokes of its end. Called at each start.</summary>
        internal void Setup(List<CTFXBotGroup> groups, int now)
        {
            Clear();
            _setupAt = now;
            if (!On || _m.Routes == null) return;
            if (TuneOn) LoadTune();
            foreach (CTFXBotGroup g in groups)
            {
                if (g.HomeEnd == null || g.Team == null) continue;
                string enemy = _m.OtherEnd(g.HomeEnd);
                if (enemy == null) continue;
                CTFXTeamCmd t = null;
                foreach (CTFXTeamCmd o in Teams) if (o.Team == g.Team && o.End == g.HomeEnd) t = o;
                if (t == null)
                {
                    t = new CTFXTeamCmd();
                    t.Team = g.Team; t.End = g.HomeEnd; t.Enemy = enemy; t.Quad = g.Quad;
                    BuildChokes(t);
                    BuildLines(t);
                    Teams.Add(t);
                }
                foreach (CTFXSlot s in g.Slots) { s.Intent = new CTFXIntent(); t.Seats.Add(s); }
            }
            StringBuilder sb = new StringBuilder("[CTFXBots] command: ");
            if (Teams.Count == 0) sb.Append("no base teams");
            foreach (CTFXTeamCmd t in Teams)
            {
                sb.Append(t.Name).Append(" (").Append(t.End).Append(", ").Append(t.Seats.Count).Append(" seats, chokes");
                foreach (CTFXChoke c in t.Chokes) sb.Append(' ').Append(c.Name).Append(' ').Append(c.Tx).Append(',').Append(c.Ty);
                sb.Append(") ");
            }
            sb.Append("| anchors ").Append(Anchors).Append(", alarm at ").Append(AlarmAt).Append(" enemies, ").Append(Knobs());
            _m.Log(sb.ToString());
        }

        /// <summary>The end's mouth (first node of its raid route out), one turret room per turret set (the set's centre), else its
        /// flag room. The mouth watches down the raid route; a room watches the mouth.</summary>
        void BuildChokes(CTFXTeamCmd t)
        {
            int mx, my, wx, wy;
            string raid = t.End + ">" + t.Enemy + ":raid";
            bool mouth = _m.RouteNode(raid, 0, out mx, out my);
            if (mouth)
            {
                if (!_m.RouteNode(raid, 2, out wx, out wy)) { wx = mx; wy = my; }
                AddChoke(t, "mouth", mx, my, wx * 16 + 8, wy * 16 + 8);
            }
            List<string> sets = new List<string>();
            foreach (CTFXTurretSpot s in _m.Turrets.Spots) if (s.End == t.End && !sets.Contains(s.Set)) sets.Add(s.Set);
            foreach (string set in sets)
            {
                int n = 0, sx = 0, sy = 0;
                foreach (CTFXTurretSpot s in _m.Turrets.Spots) if (s.End == t.End && s.Set == set) { n++; sx += s.Tx; sy += s.Ty; }
                AddChoke(t, set + "-room", sx / n, sy / n, mouth ? mx * 16 + 8 : sx / n * 16 + 8, mouth ? my * 16 + 8 : sy / n * 16 + 8);
            }
            int fx, fy;
            if (sets.Count == 0 && _m.RouteNode(t.End + ":patrol", 0, out fx, out fy))
                AddChoke(t, "flag-room", fx, fy, mouth ? mx * 16 + 8 : fx * 16 + 8, mouth ? my * 16 + 8 : fy * 16 + 8);
        }

        void AddChoke(CTFXTeamCmd t, string name, int tx, int ty, int wx, int wy)
        {
            int ox, oy;
            if (_m.NearestOpen(tx, ty, 6, out ox, out oy)) { tx = ox; ty = oy; }
            CTFXChoke c = new CTFXChoke();
            c.Name = name; c.Tx = tx; c.Ty = ty; c.WatchX = wx; c.WatchY = wy;
            t.Chokes.Add(c);
        }

        // ================================================================ who is managed
        CTFXTeamCmd TeamOf(CTFXSlot s)
        {
            if (s == null || s.Intent == null) return null;
            foreach (CTFXTeamCmd t in Teams) if (t.Seats.Contains(s)) return t;
            return null;
        }

        internal static bool Free(CTFXAct a) { return a == CTFXAct.Skirmish || a == CTFXAct.Raid || a == CTFXAct.Wander || a == CTFXAct.Hunt || a == CTFXAct.Outside; }

        /// <summary>The commander picks this bot's goals (its activity is not a storm, a build or nothing).</summary>
        internal bool Manages(CTFXBot b)
        {
            if (!On || b == null || b.Slot == null || b.Slot.Intent == null) return false;
            CTFXAct a = b.Slot.Intent.Act;
            return a != CTFXAct.None && a != CTFXAct.Build && a != CTFXAct.Storm && TeamOf(b.Slot) != null;
        }

        /// <summary>A base team the commander runs (by team name).</summary>
        internal bool Manages(string team)
        {
            if (!On) return false;
            foreach (CTFXTeamCmd t in Teams) if (t.Name == team) return true;
            return false;
        }

        internal CTFXAct ActOf(CTFXBot b) { return b != null && b.Slot != null && b.Slot.Intent != null ? b.Slot.Intent.Act : CTFXAct.None; }

        /// <summary>Activities the commander does not choose: the storm cycle (unless the member sits this one out) and building.</summary>
        CTFXAct Fixed(CTFXSlot s, CTFXBot b, int now)
        {
            CTFXStorm st = s.Group.Storm;
            // the stormer always runs its cycle; a member only from the storm on (until then it does its normal things, Travis 10-14)
            if (st != null && s.Style == CTFXStyle.Stormer) return CTFXAct.Storm;
            if (st != null && s.Style == CTFXStyle.Assault && !st.SitOut.Contains(s) && b != null && st.Joined.Contains(b._id)
                && (st.Phase == CTFXStorm.Storm || st.Phase == CTFXStorm.Hold || (st.Phase == CTFXStorm.Rest && b != null && _m.InsideEnd(st.Enemy, b)))) return CTFXAct.Storm;
            if (s.Style == CTFXStyle.Engineer && _m.Turrets.JobWaiting(b, now)) return CTFXAct.Build;
            return CTFXAct.None;
        }

        // ================================================================ per poll
        internal void Step(int now)
        {
            if (!On || now - _lastStep < 500) return;
            _lastStep = now;
            RejoinStep(now);
            foreach (CTFXTeamCmd t in Teams)
            {
                Threat(t, now);
                AlarmStep(t, now);
                SweepStep(t, now);
                AnchorStep(t, now);
                foreach (CTFXSlot s in t.Seats) Intent(t, s, now);
            }
            BrownCount(now);                                                    // sampled for the report
            LinesStep(now);
            FootholdStep(now);
            AmmoStep(now);
            SiegeStep(now);
            MaserStep(now);
        }

        /// <summary>The newest body of a seat is up: a fresh pick, except a habit (half the time) or a needed anchor goes on.</summary>
        internal void Spawned(CTFXSlot s, CTFXBot b, int now)
        {
            CTFXTeamCmd t = TeamOf(s);
            if (t == null || !On) return;
            CTFXIntent it = s.Intent;
            if (it.Act == CTFXAct.Anchor && s.Style != CTFXStyle.Post && AnchorsAlive(t, s) < AnchorsFor(t)) return;
            if (it.Act == CTFXAct.Defend && t.Alarm) return;
            if (Free(it.Act) && _m.Rng.NextDouble() < 0.5) { it.Until = now + Human(b).BoredMs(_m.Rng); return; }
            Pick(t, s, b, now);
        }

        // ---------------------------------------------------------------- rejoin after the dropship
        /// <summary>The bot just came out of a MapEnter portal: it decides in 1.5-4 s whether to warp toward its objective.</summary>
        internal void Landed(CTFXBot b, int now)
        {
            if (!On || b == null || b.Slot == null) return;
            _hopAt[b.Slot] = now + 1000 + _m.Rng.Next(1501);
            _fresh.Add(b.Slot);                                                 // the first look right after landing is the decisive one
        }

        /// <summary>
        /// While a bot is far (1500+ px) from its objective and not fighting, every 12-20 s it may take Suit Teleport to the teammate
        /// nearest that objective, if that one is 1200+ px nearer than itself: onto a teammate already there, or a hop part of the way
        /// (10-12 soak: waiting for a teammate AT the objective never fired while the bases were empty). Most do (0.6 + 0.35 x
        /// compliance); with nobody nearer, or by choice, it walks and looks again later.
        /// </summary>
        void RejoinStep(int now)
        {
            if (_hopAt.Count == 0) return;
            List<CTFXSlot> due = null;
            foreach (KeyValuePair<CTFXSlot, int> kv in _hopAt) if (now - kv.Value >= 0) { if (due == null) due = new List<CTFXSlot>(); due.Add(kv.Key); }
            if (due == null) return;
            foreach (CTFXSlot s in due)
            {
                CTFXBot b = _m.GetBot(s.BotId);
                int ox, oy;
                if (b == null || b.IsDead || b.Released || b.InDropship || !Objective(s, b, out ox, out oy)) { _hopAt.Remove(s); continue; }
                double d0 = Math.Sqrt((double)(ox - b._state.positionX) * (ox - b._state.positionX) + (double)(oy - b._state.positionY) * (oy - b._state.positionY));
                if (d0 < 1500.0) { _hopAt.Remove(s); continue; }                                        // close enough to walk
                _hopAt[s] = now + 12000 + _m.Rng.Next(8001);
                if (b.HasTarget) continue;                                                              // fighting where it is: later
                CTFXBot to = null;
                double best = d0 - 1200.0;
                foreach (CTFXBot o in _m.LiveBots)
                {
                    if (o == b || o.IsDead || o.Released || o.InDropship || o._team != b._team) continue;
                    double ex = ox - o._state.positionX, ey = oy - o._state.positionY, dm = Math.Sqrt(ex * ex + ey * ey);
                    if (dm < best && _m.InStaleBrawl(o._state.positionX, o._state.positionY)) { _m.StaleRejoinSkips++; continue; }
                    if (dm < best) { best = dm; to = o; }
                }
                CTFXHuman h = Human(b);
                bool fresh = _fresh.Remove(s);
                if (to == null || _m.Rng.NextDouble() > (fresh ? 0.75 + 0.2 * h.Comply : 0.6 + 0.35 * h.Comply)) { RejoinWalks++; continue; }
                if (_m.Squads.TeleportTo(b, to, now, best < 700.0 ? "rejoin" : "hop")) { if (best < 700.0) Rejoins++; else Hops++; }
            }
        }

        /// <summary>Objective for the stale-brawl tracker (CTFXBrawl.cs).</summary>
        internal bool ObjectiveOf(CTFXBot b, out int x, out int y)
        {
            x = 0; y = 0;
            return b != null && b.Slot != null && Objective(b.Slot, b, out x, out y);
        }

        /// <summary>Where the bot is headed for: its choke, its fight, its base, its hall, its raid target; false = no single place.</summary>
        bool Objective(CTFXSlot s, CTFXBot b, out int x, out int y)
        {
            x = 0; y = 0;
            int tx, ty;
            CTFXBotGroup g = s.Group;
            CTFXTeamCmd t = TeamOf(s);
            CTFXAct a = s.Intent != null ? s.Intent.Act : CTFXAct.None;
            if (t != null)
            {
                switch (a)
                {
                    case CTFXAct.Anchor:
                        if (s.Style == CTFXStyle.Post) { _m.PostTile(s.PostIndex, out tx, out ty); x = tx * 16 + 8; y = ty * 16 + 8; return true; }
                        if (s.Intent.Choke >= 0 && s.Intent.Choke < t.Chokes.Count) { x = t.Chokes[s.Intent.Choke].Tx * 16 + 8; y = t.Chokes[s.Intent.Choke].Ty * 16 + 8; return true; }
                        break;
                    case CTFXAct.Defend:
                        if (t.Alarm) { x = t.ThreatX; y = t.ThreatY; return true; }
                        break;
                    case CTFXAct.Raid:
                        if (_m.RouteNode((s.Intent.RaidEnd ?? t.Enemy) + ":patrol", 0, out tx, out ty)) { x = tx * 16 + 8; y = ty * 16 + 8; return true; }
                        break;
                    case CTFXAct.Skirmish:
                        if (_m.CoreHub(t.Quad, out tx, out ty)) { x = tx * 16 + 8; y = ty * 16 + 8; return true; }
                        break;
                    case CTFXAct.Build:
                    case CTFXAct.Storm:
                        if (t.Chokes.Count > 0) { x = t.Chokes[0].Tx * 16 + 8; y = t.Chokes[0].Ty * 16 + 8; return true; }
                        break;
                }
                return false;                                                                         // wander / hunt / outside: anywhere
            }
            if (g != null && g.CoreOf != null && _m.RouteNode(_m.TopEnd(g.CoreOf) + ":patrol", 0, out tx, out ty)) { x = tx * 16 + 8; y = ty * 16 + 8; return true; }   // 10-15 night: the end, not the hall
            if (g != null && g.CoreOf != null && _m.CoreHub(g.CoreOf, out tx, out ty)) { x = tx * 16 + 8; y = ty * 16 + 8; return true; }
            return false;
        }

        // ---------------------------------------------------------------- threat + alarm
        bool InBase(CTFXTeamCmd t, int x, int y)
        {
            if (_m.InsideEndPx(t.End, x, y)) return true;
            foreach (Computer c in _m.Turrets.Live)
            {
                if (c._team != t.Team || c.IsDead) continue;
                int dx = c._state.positionX - x, dy = c._state.positionY - y;
                if (dx * dx + dy * dy <= 300 * 300) return true;
            }
            return false;
        }

        void Threat(CTFXTeamCmd t, int now)
        {
            int n = 0;
            long sx = 0, sy = 0;
            foreach (CTFXBot o in _m.LiveBots)
            {
                if (o.IsDead || o.Released || o._team == t.Team || o.Cloaked) continue;
                if (!InBase(t, o._state.positionX, o._state.positionY)) continue;
                n++; sx += o._state.positionX; sy += o._state.positionY;
            }
            foreach (Player p in _m.Arena.PlayersIngame)
            {
                if (p.IsDead || p.IsSpectator || p._team == t.Team || p._state == null) continue;
                if (!InBase(t, p._state.positionX, p._state.positionY)) continue;
                n++; sx += p._state.positionX; sy += p._state.positionY;
            }
            t.Threat = n;
            if (n > 0) t.LastThreatAt = now;
            if (n > t.ThreatPeak) t.ThreatPeak = n;
            if (n > 0) { t.ThreatX = (int)(sx / n); t.ThreatY = (int)(sy / n); }
        }

        void AlarmStep(CTFXTeamCmd t, int now)
        {
            if (!t.Alarm)
            {
                if (t.Threat >= AlarmAt)
                {
                    if (t.RaisedAt == 0) t.RaisedAt = now | 1;
                    else if (now - t.RaisedAt >= 1200)
                    {   // held for over a second: the call goes out
                        t.Alarm = true; t.AlarmSince = now; t.ClearSince = 0; t.Alarms++; t.CallAt = now;
                        CTFXBot caller = NearestMateTo(t, t.ThreatX, t.ThreatY);
                        if (caller != null) _m.Chat.Alarm(caller, t.End, t.Threat);
                    }
                }
                else t.RaisedAt = 0;
            }
            else
            {
                t.AlarmMs += 500;
                if (t.HumanUntil != 0 && now - t.HumanUntil >= 0) { t.HumanUntil = 0; t.Desperate = false; }   // the call has run out
                if (t.Threat == 0 && t.HumanUntil == 0)
                {
                    if (t.ClearSince == 0) t.ClearSince = now | 1;
                    else if (now - t.ClearSince >= 6000) EndAlarm(t, now);
                }
                else t.ClearSince = 0;
            }
            if (t.Alarm && now - t.CallAt >= 0) Call(t, now);
            Hear(t, now);
        }

        int Needed(CTFXTeamCmd t)
        {
            int defenders = 0;
            foreach (CTFXSlot s in t.Seats)
            {
                CTFXBot b = _m.GetBot(s.BotId);
                if (b != null && !b.IsDead && s.Intent.Act == CTFXAct.Defend) defenders++;
            }
            // never more than half the living team (10-12 stress run: an overrun base turned 9 of 19 into a trickle of defenders)
            int alive = 0;
            foreach (CTFXSlot s in t.Seats) { CTFXBot b = _m.GetBot(s.BotId); if (b != null && !b.IsDead) alive++; }
            int want = Math.Min((int)Math.Ceiling(t.Threat * DefendRatio), alive / 2);
            // a teammate's call: %28 asks for three quarters of the team at home, %26 for a few to look (they believe him)
            if (t.HumanUntil != 0)
                want = Math.Max(want, t.Desperate ? (alive * 3 + 3) / 4 : Math.Min(3, alive / 2));
            return want - AnchorsAlive(t, null) - defenders;
        }

        /// <summary>Every 3 s while the alarm lasts and the base is short: the nearest free teammates who were not called lately
        /// get the call (twice as many as needed: some will not come); each notices it after its own delay, more when far.</summary>
        void Call(CTFXTeamCmd t, int now)
        {
            t.CallAt = now + 3000;
            int need = Needed(t) - t.Hearing.Count;
            if (need <= 0) return;
            _cand.Clear();
            foreach (CTFXSlot s in t.Seats)
            {
                CTFXBot b = _m.GetBot(s.BotId);
                if (b == null || b.IsDead || b.Released || !Free(s.Intent.Act) || (!t.Desperate && now - s.Intent.IgnoreUntil < 0)) continue;
                if (t.Desperate && _m.Flags != null && _m.Flags.Carrying(b)) continue;     // a flag carrier keeps running
                bool hearing = false;
                foreach (KeyValuePair<CTFXSlot, int> h in t.Hearing) if (h.Key == s) hearing = true;
                if (!hearing) _cand.Add(s);
            }
            _cand.Sort(delegate(CTFXSlot a, CTFXSlot b) { return DistTo(a, t.ThreatX, t.ThreatY).CompareTo(DistTo(b, t.ThreatX, t.ThreatY)); });
            for (int i = 0; i < _cand.Count && i < need * 2; i++)
            {
                CTFXBot b = _m.GetBot(_cand[i].BotId);
                double d = DistTo(_cand[i], t.ThreatX, t.ThreatY);
                double late = Human(b).AlarmMs(_m.Rng) * (1.0 + d / 3000.0) * (t.Desperate ? 0.5 : 1.0);   // a desperate call is heard sooner
                t.Hearing.Add(new KeyValuePair<CTFXSlot, int>(_cand[i], now + (int)late));
            }
        }

        void Hear(CTFXTeamCmd t, int now)
        {
            for (int i = 0; i < t.Hearing.Count; )
            {
                if (now - t.Hearing[i].Value < 0) { i++; continue; }
                CTFXSlot s = t.Hearing[i].Key;
                t.Hearing.RemoveAt(i);
                CTFXBot b = _m.GetBot(s.BotId);
                if (!t.Alarm || b == null || b.IsDead || b.Released || !Free(s.Intent.Act)) continue;
                t.Heard++;
                if (Needed(t) <= 0) continue;                                   // others are on it
                CTFXHuman h = Human(b);
                double d = DistTo(s, t.ThreatX, t.ThreatY);
                double p = h.Comply * (d < 900 ? 1.2 : d < 1800 ? 0.9 : 0.6);
                if (b.HasTarget) p *= 0.5;                                       // busy with its own fight
                if (s.Intent.Act == CTFXAct.Raid && _m.InsideEndPx(t.Enemy, b._state.positionX, b._state.positionY)) p *= t.Desperate ? 0.8 : 0.4;
                if (t.Desperate) p = Math.Min(0.97, Math.Max(b.HasTarget ? 0.6 : 0.8, p * 1.6 + 0.2));   // "save home": nearly everyone turns around
                if (_m.Rng.NextDouble() >= p)
                {
                    s.Intent.IgnoreUntil = now + 20000 + _m.Rng.Next(20001);
                    t.Ignored++;
                    continue;
                }
                SetAct(s, b, CTFXAct.Defend, now, now + 120000);
                s.Intent.Responded = false;
                s.Intent.JoinedAt = now;
                t.Joined++;
                if (t.HumanUntil != 0)
                {
                    t.HumanJoined++;
                    if (!t.HumanAcked && _m.Chat != null) { t.HumanAcked = true; _m.Chat.HomeReply(b, t.Desperate); }
                }
                // far away: most take the teleport to whoever is nearest the fight (as a player warps to a teammate); nearly all on %28,
                // to a teammate anywhere near home (a call from away puts the alarm mid-base, between the anchors)
                if (d > 800 && _m.Rng.NextDouble() < (t.Desperate ? 0.95 : 0.75))
                {
                    CTFXBot to = NearestMate(b, t.ThreatX, t.ThreatY, t.Desperate ? 1500.0 : 500.0);
                    if (to != null && _m.Squads.TeleportTo(b, to, now, "alarm")) t.Teleports++;
                }
            }
        }

        // ---------------------------------------------------------------- a teammate's call for home
        /// <summary>
        /// Travis 10-25: "bots should respond more desperately to %28 macros (We are under attack!) as a call for desperation to save
        /// home, where as %26 macros just say Enemy Sighted, which is a lighter warning". The team's base alarm goes up at once where the
        /// caller stands (at home's chokes when he calls %28 from away; %26 from away is not about the base) and holds 45 s (%28) / 15 s
        /// (%26) with nobody the bots can see - a human sees a cloaked infiltrator or a stormer behind a door. %28 calls three quarters
        /// of the living team, sooner, ignores who skipped an alarm lately, pulls raiders out of the enemy end (never a flag carrier)
        /// and sends nearly all far ones by teleport; %26 brings up to three to look. False = nothing to do (no base team, or %26 away).
        /// </summary>
        internal bool HumanAlarm(Player from, bool desperate, int now)
        {
            if (!On || from == null || from._team == null || from._state == null) return false;
            int fx = from._state.positionX, fy = from._state.positionY;
            CTFXTeamCmd t = null;
            foreach (CTFXTeamCmd o in Teams)
                if (o.Team == from._team && (t == null || InBase(o, fx, fy))) t = o;
            if (t == null) return false;
            bool home = InBase(t, fx, fy);
            int x = fx, y = fy;
            if (!home)
            {
                if (!desperate || t.Chokes.Count == 0) return false;
                long sx = 0, sy = 0;
                foreach (CTFXChoke c in t.Chokes) { sx += c.WatchX; sy += c.WatchY; }
                x = (int)(sx / t.Chokes.Count); y = (int)(sy / t.Chokes.Count);
            }
            t.HumanCalls++;
            int hold = now + (desperate ? 45000 : 15000);
            if (t.HumanUntil == 0 || hold - t.HumanUntil > 0) t.HumanUntil = hold | 1;
            if (desperate) t.Desperate = true;
            t.HumanAcked = false;
            if (t.Threat == 0) { t.ThreatX = x; t.ThreatY = y; }
            if (!t.Alarm) { t.Alarm = true; t.AlarmSince = now; t.Alarms++; }
            t.ClearSince = 0;
            t.CallAt = now;                                                      // the call goes out on the next step
            return true;
        }

        void EndAlarm(CTFXTeamCmd t, int now)
        {
            t.Alarm = false; t.RaisedAt = 0; t.ClearSince = 0;
            t.HumanUntil = 0; t.Desperate = false;
            t.Hearing.Clear();
            foreach (CTFXSlot s in t.Seats)                                      // the defenders linger a little, then pick again
                if (s.Intent.Act == CTFXAct.Defend) s.Intent.Until = now + 2000 + _m.Rng.Next(5001);
        }

        // ---------------------------------------------------------------- anchors
        /// <summary>Living anchors (posts always are), <paramref name="except"/> not counted.</summary>
        int AnchorsAlive(CTFXTeamCmd t, CTFXSlot except)
        {
            int n = 0;
            foreach (CTFXSlot s in t.Seats)
            {
                if (s == except || s.Intent.Act != CTFXAct.Anchor) continue;
                CTFXBot b = _m.GetBot(s.BotId);
                if (b != null && !b.IsDead) n++;
            }
            return n;
        }

        bool NearHome(CTFXTeamCmd t, CTFXBot b)
        {
            if (_m.InsideEndPx(t.End, b._state.positionX, b._state.positionY)) return true;
            foreach (CTFXChoke c in t.Chokes)
            {
                double dx = c.Tx * 16 + 8 - b._state.positionX, dy = c.Ty * 16 + 8 - b._state.positionY;
                if (dx * dx + dy * dy <= 500.0 * 500.0) return true;
            }
            return false;
        }

        /// <summary>Anchors a team keeps home: the knob (2), but half the team for the holders of the focus base (Travis 10-14: top brown is
        /// the fortress; its elite team holds it, the 10-14 soak had 3 of 18 home and 13 out skirmishing / raiding).</summary>
        int AnchorsFor(CTFXTeamCmd t)
        {
            if (_m.FocusEnd == null || t.End != _m.FocusEnd) return Anchors;
            return Math.Max(Anchors, (t.Seats.Count + 1) / 2);
        }

        /// <summary>Travis 10-14 night: "The anchor for the brown base (and any base with a flag) should always be a captain, who have
        /// high reaction to summon requests". The team's Captain anchors the flag room for as long as the base is the focus base or
        /// holds a flag (a new body goes back to it).</summary>
        void CaptainStep(CTFXTeamCmd t, int now)
        {
            if (_m.Flags == null) return;
            int fx, fy;
            if (!_m.RouteNode(t.End + ":patrol", 0, out fx, out fy)) return;
            int hx, hy;
            bool flagHome = _m.Flags.HeldNear(t.Team, fx * 16 + 8, fy * 16 + 8, 900.0, out hx, out hy);
            if (!flagHome && t.End != _m.FocusEnd) return;
            foreach (CTFXSlot s in t.Seats)
            {
                CTFXBot b = _m.GetBot(s.BotId);
                if (b == null || b.IsDead || b.Released || b.ClassId != CTFXBot.SK_CAPT) continue;
                if (s.Intent.Act == CTFXAct.Anchor) { if (s.Intent.Until - now < 60000) s.Intent.Until = now + 600000; return; }
                if (b.InDropship) return;
                SetAct(s, b, CTFXAct.Anchor, now, now + 600000);
                s.Intent.Choke = NearestChoke(t, fx * 16 + 8, fy * 16 + 8);
                CaptainAnchors++;
                _m.Chat.Anchor(b, t.End);
                return;
            }
        }
        internal int CaptainAnchors, TdCorners, CalmAnchors;

        void AnchorStep(CTFXTeamCmd t, int now)
        {
            CaptainStep(t, now);
            int alive = AnchorsAlive(t, null);
            if (alive > AnchorsFor(t))
            {   // one too many (a post or the old anchor came back): the newest stand-in goes back to its own things
                CTFXSlot newest = null;
                foreach (CTFXSlot s in t.Seats)
                {
                    CTFXBot b = _m.GetBot(s.BotId);
                    if (s.Style == CTFXStyle.Post || s.Intent.Act != CTFXAct.Anchor || b == null || b.IsDead) continue;
                    if (b.ClassId == CTFXBot.SK_CAPT && s.Intent.Until - now > 60000) continue;   // the Captain anchor stays (cycle 19: 546 re-sets)
                    if (newest == null || s.Intent.Since - newest.Intent.Since > 0) newest = s;
                }
                if (newest != null && now - newest.Intent.Until < 0) { newest.Intent.Until = now; t.Handoffs++; }
                return;
            }
            if (alive >= AnchorsFor(t)) { t.ShortSince = 0; return; }
            if (t.ShortSince == 0) t.ShortSince = now | 1;
            if (t.Chokes.Count == 0 || now - t.AskAt < 0) return;
            t.AskAt = now + 4000;
            // short for 15 s with nobody home (bots out of the dropship land outside): anyone may be asked, the nearer the likelier
            bool anyone = now - t.ShortSince > 15000;
            CTFXSlot best = null;
            double bs = 0;
            foreach (CTFXSlot s in t.Seats)
            {
                CTFXBot b = _m.GetBot(s.BotId);
                if (b == null || b.IsDead || b.Released || b.HasTarget || !Free(s.Intent.Act) || now - s.Intent.DeclineUntil < 0) continue;
                bool home = NearHome(t, b);
                if (!home && !anyone) continue;
                CTFXHuman h = Human(b);
                double sc = StyleTaste(s.Style, CTFXAct.Anchor) * h.Taste[(int)CTFXAct.Anchor] * (0.5 + h.Comply) * (0.8 + _m.Rng.NextDouble() * 0.4);
                if (!home && t.Chokes.Count > 0) sc /= 1.0 + DistTo(s, t.Chokes[0].Tx * 16 + 8, t.Chokes[0].Ty * 16 + 8) / 1500.0;
                else sc *= 1.5;
                if (sc > bs) { bs = sc; best = s; }
            }
            if (best == null) return;
            t.AnchorAsks++;
            CTFXBot bb = _m.GetBot(best.BotId);
            CTFXHuman hh = Human(bb);
            double p = Math.Min(0.95, 0.25 + 0.7 * hh.Comply * Math.Min(1.0, StyleTaste(best.Style, CTFXAct.Anchor) + 0.3));
            if (_m.Rng.NextDouble() >= p) { best.Intent.DeclineUntil = now + 45000; t.AnchorNo++; return; }
            t.AnchorYes++;
            SetAct(best, bb, CTFXAct.Anchor, now, now + (int)(hh.BoredMs(_m.Rng) * (2.0 + _m.Rng.NextDouble())));
            best.Intent.Choke = FreeChoke(t, best);
            _m.Chat.Anchor(bb, t.End);
        }

        /// <summary>The living bot of team t nearest (x, y) (the one who sees the intruders first and calls them).</summary>
        CTFXBot NearestMateTo(CTFXTeamCmd t, int x, int y)
        {
            CTFXBot best = null;
            double bd = double.MaxValue;
            foreach (CTFXBot b in _m.LiveBots)
            {
                if (b.IsDead || b.Released || b.InDropship || b.Slot == null || TeamOf(b.Slot) != t) continue;
                double dx = b._state.positionX - x, dy = b._state.positionY - y, d = dx * dx + dy * dy;
                if (d < bd) { bd = d; best = b; }
            }
            return best;
        }

        /// <summary>The choke with the fewest anchors on it (a post covers the one nearest its post); ties at random.</summary>
        int FreeChoke(CTFXTeamCmd t, CTFXSlot me)
        {
            int[] n = new int[t.Chokes.Count];
            foreach (CTFXSlot s in t.Seats)
            {
                if (s == me || s.Intent.Act != CTFXAct.Anchor) continue;
                CTFXBot b = _m.GetBot(s.BotId);
                if (b == null || b.IsDead) continue;
                int c = s.Style == CTFXStyle.Post ? NearestChoke(t, b._state.positionX, b._state.positionY) : s.Intent.Choke;
                if (c >= 0 && c < n.Length) n[c]++;
            }
            int best = 0, bn = int.MaxValue, ties = 0;
            for (int i = 0; i < n.Length; i++)
            {
                if (n[i] < bn) { bn = n[i]; best = i; ties = 1; }
                else if (n[i] == bn && _m.Rng.Next(++ties) == 0) best = i;
            }
            return best;
        }

        int NearestChoke(CTFXTeamCmd t, int x, int y)
        {
            int best = -1;
            double bd = double.MaxValue;
            for (int i = 0; i < t.Chokes.Count; i++)
            {
                double dx = t.Chokes[i].Tx * 16 + 8 - x, dy = t.Chokes[i].Ty * 16 + 8 - y, d = dx * dx + dy * dy;
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        // ---------------------------------------------------------------- intent
        void Intent(CTFXTeamCmd t, CTFXSlot s, int now)
        {
            CTFXBot b = _m.GetBot(s.BotId);
            if (b == null || b.IsDead || b.Released) return;
            CTFXIntent it = s.Intent;
            if (it.Foothold) return;                                            // the team foothold holds (FootholdStep ends it)
            CTFXAct fx = Fixed(s, b, now);
            if (fx != CTFXAct.None)
            {
                if (it.Act != fx) { it.Last = it.Act; it.Act = fx; it.Since = now; b.ForceGoal(); }
                return;
            }
            if (s.Style == CTFXStyle.Post)
            {   // a post is an anchor for good: its configured turret-room post
                if (it.Act != CTFXAct.Anchor) { it.Act = CTFXAct.Anchor; it.Since = now; it.Choke = NearestChoke(t, b._state.positionX, b._state.positionY); }
                return;
            }
            if (it.Act == CTFXAct.None || it.Act == CTFXAct.Build || it.Act == CTFXAct.Storm) { Pick(t, s, b, now); return; }
            if (it.Act == CTFXAct.Defend && !it.Responded && t.Alarm && DistTo(s, t.ThreatX, t.ThreatY) <= 320.0)
            {
                it.Responded = true;
                t.RespSum += now - it.JoinedAt; t.RespN++;                      // from deciding to go to being at the fight
            }
            // brown short of its quota: one free bot of another team at a time (every 400 ms) drops what it does and heads there
            if (now - it.Until < 0 && BrownShort(t, now) && now - it.Since > 12000 && now - _brownPullAt >= 400
                && (it.Act == CTFXAct.Wander || it.Act == CTFXAct.Skirmish || it.Act == CTFXAct.Hunt || it.Act == CTFXAct.Outside
                    || (it.Act == CTFXAct.Raid && it.RaidEnd != _m.FocusEnd && it.RaidEnd != _m.OtherEnd(_m.FocusEnd))))
            { _brownPullAt = now; BrownPulls++; Pick(t, s, b, now); return; }
            if (now - it.Until < 0) return;
            if (it.Act == CTFXAct.Defend && t.Alarm) { it.Until = now + 15000; return; }
            if (it.Act == CTFXAct.Anchor && AnchorsAlive(t, s) < AnchorsFor(t))
            {   // bored of holding: hands off only when someone else is home to take over
                bool other = false;
                foreach (CTFXSlot o in t.Seats)
                {
                    CTFXBot ob = _m.GetBot(o.BotId);
                    if (o != s && ob != null && !ob.IsDead && Free(o.Intent.Act) && NearHome(t, ob)) { other = true; break; }
                }
                if (!other) { it.Until = now + 45000; return; }
                t.Handoffs++;
            }
            Pick(t, s, b, now);
        }

        void SetAct(CTFXSlot s, CTFXBot b, CTFXAct a, int now, int until)
        {
            CTFXIntent it = s.Intent;
            if (it.Act != a) it.Last = it.Act;
            it.Act = a; it.Since = now; it.Until = until;
            if (b != null) b.ForceGoal();
        }

        /// <summary>Its own pick among skirmish / raid / wander / hunt: style x personal taste x herd aversion (+ habit).</summary>
        void Pick(CTFXTeamCmd t, CTFXSlot s, CTFXBot b, int now)
        {
            CTFXHuman h = Human(b);
            if (BrownShort(t, now) && _m.Rng.NextDouble() < 0.85)
            {   // the brown quota: a raid on top brown (70 %) or its bottom end, held a while longer than a normal pick
                SetAct(s, b, CTFXAct.Raid, now, now + h.BoredMs(_m.Rng) * 3 / 2);
                string other = _m.OtherEnd(_m.FocusEnd);
                s.Intent.RaidEnd = other != null && _m.Rng.NextDouble() < 0.3 ? other : _m.FocusEnd;
                s.Intent.Picks++; Picks++; BrownSent++;
                return;
            }
            int[] doing = new int[10];
            int free = 0, outside = 0, managed = 0;
            foreach (CTFXTeamCmd o2 in Teams)
                foreach (CTFXSlot o in o2.Seats)
                {
                    CTFXBot ob = _m.GetBot(o.BotId);
                    if (ob == null || ob.IsDead) continue;
                    managed++;
                    if (o.Intent.Act == CTFXAct.Outside) outside++;
                }
            foreach (CTFXSlot o in t.Seats)
            {
                if (o == s) continue;
                CTFXBot ob = _m.GetBot(o.BotId);
                if (ob == null || ob.IsDead || !Free(o.Intent.Act)) continue;
                doing[(int)o.Intent.Act]++; free++;
            }
            int hx, hy;
            bool hot = HotCell(b, out hx, out hy);
            CTFXAct[] acts = new CTFXAct[] { CTFXAct.Skirmish, CTFXAct.Raid, CTFXAct.Wander, CTFXAct.Hunt, CTFXAct.Outside };
            double outsideRoom = managed > 0 ? Math.Max(0.0, 1.0 - (outside / (double)managed) / Math.Max(0.01, OutsideShare)) : 1.0;
            double[] w = new double[acts.Length];
            double sum = 0;
            for (int i = 0; i < acts.Length; i++)
            {
                CTFXAct a = acts[i];
                double share = free > 0 ? doing[(int)a] / (double)free : 0.0;
                bool brownTeam = _m.FocusEnd != null && t.Quad == "brown";
                // a raid on top brown is not put off by the crowd (Travis 10-13: brown is the biggest hotspot at all times)
                bool focusRaid = a == CTFXAct.Raid && _m.FocusEnd != null && t.End != _m.FocusEnd;
                double x = StyleTaste(s.Style, a) * h.Taste[(int)a] / (focusRaid ? 1.0 : 1.0 + HerdK * share * 2.0);
                if (a == CTFXAct.Hunt && !hot) x *= 0.3;
                if (focusRaid) x *= 2.5;                                        // the pull of top brown
                string fe;
                if (a == CTFXAct.Raid && HasFoothold(t, out fe)) x *= 2.5;      // a teammate is holding ground in brown: go help
                if (_m.FocusEnd != null && t.End == _m.FocusEnd && a != CTFXAct.Skirmish && a != CTFXAct.Raid) x *= 0.35;   // its holders stay home
                if (_m.FocusEnd != null && t.End == _m.FocusEnd && a == CTFXAct.Raid) x *= 0.3;     // the fortress team mostly holds (10-14)
                if (brownTeam && (a == CTFXAct.Outside || a == CTFXAct.Hunt)) x *= 0.1;   // the brown teams fight for brown, not the fields
                if (a == CTFXAct.Outside) x *= _m.Dropship.HasOutside ? outsideRoom : 0.0;
                if (a == s.Intent.Act || a == s.Intent.Last) x *= 1.3;          // habit
                if (a == CTFXAct.Skirmish) x *= PushOut(t, now) ? 1.5 : 0.15;   // the hall fight follows a sweep, else the flags
                w[i] = x; sum += x;
            }
            double u = _m.Rng.NextDouble() * sum;
            CTFXAct pick = acts[acts.Length - 1];
            for (int i = 0; i < acts.Length; i++) { u -= w[i]; if (u <= 0) { pick = acts[i]; break; } }
            SetAct(s, b, pick, now, now + h.BoredMs(_m.Rng));
            if (pick == CTFXAct.Skirmish) { s.Intent.Until = Math.Min(s.Intent.Until, now + 45000); if (PushOut(t, now)) PushOuts++; }   // a push, not a new home
            if (pick == CTFXAct.Raid) s.Intent.RaidEnd = RaidEnd(t, b);
            s.Intent.Picks++; Picks++;
        }

        internal int PushOuts;

        /// <summary>Its base was just cleared: attackers inside within the last 60 s, none now, no alarm.</summary>
        static bool PushOut(CTFXTeamCmd t, int now)
        {
            return !t.Alarm && t.Threat == 0 && t.LastThreatAt != 0 && now - t.LastThreatAt < 60000;
        }

        /// <summary>The end a raid goes for: its own building's other end 55 % of the time, else another base, the nearer the likelier.</summary>
        string RaidEnd(CTFXTeamCmd t, CTFXBot b)
        {
            // the focus base (top brown, Travis 10-13: always the busiest base on the map, everyone vies to set up there)
            if (_m.FocusEnd != null && t.Quad == "brown") return t.Enemy;
            string fe;
            if (HasFoothold(t, out fe) && _m.Rng.NextDouble() < 0.75) return fe;     // in on the foothold      // the brown teams: always each other (titan top vs collective bottom)
            if (_m.FocusEnd != null && t.End != _m.FocusEnd && _m.Rng.NextDouble() < _m.FocusShare) return _m.FocusEnd;
            if (_m.Rng.NextDouble() < 0.70 || _m.EndIds.Count < 3) return t.Enemy;
            double sum = 0;
            List<KeyValuePair<string, double>> w = new List<KeyValuePair<string, double>>();
            foreach (string e in _m.EndIds)
            {
                if (e == t.End || e == t.Enemy) continue;
                int ex, ey;
                if (!_m.RouteNode(e + ":patrol", 0, out ex, out ey)) continue;
                double dx = ex * 16 + 8 - b._state.positionX, dy = ey * 16 + 8 - b._state.positionY;
                double x = 1.0 / (1.0 + Math.Sqrt(dx * dx + dy * dy) / 2500.0);
                w.Add(new KeyValuePair<string, double>(e, x)); sum += x;
            }
            double u = _m.Rng.NextDouble() * sum;
            foreach (KeyValuePair<string, double> kv in w) { u -= kv.Value; if (u <= 0) return kv.Key; }
            return t.Enemy;
        }

        /// <summary>How much a style likes an activity (before the seat's own taste).</summary>
        static double StyleTaste(CTFXStyle st, CTFXAct a)
        {
            //                                   anchor skirm  raid   wander hunt   outside
            double[] v;
            switch (st)
            {
                case CTFXStyle.Guard: v = new double[] { 0.9, 0.40, 0.10, 0.30, 0.20, 0.05 }; break;
                case CTFXStyle.Assault: v = new double[] { 0.1, 0.25, 0.50, 0.10, 0.15, 0.10 }; break;
                case CTFXStyle.Sniper: v = new double[] { 0.8, 0.55, 0.10, 0.25, 0.10, 0.15 }; break;
                case CTFXStyle.Drain: v = new double[] { 0.2, 0.20, 0.10, 0.40, 0.30, 0.40 }; break;
                case CTFXStyle.Adhd: v = new double[] { 0.1, 0.15, 0.20, 0.25, 0.40, 0.30 }; break;
                case CTFXStyle.Stormer: v = new double[] { 0.1, 0.20, 0.40, 0.20, 0.20, 0.05 }; break;
                case CTFXStyle.Engineer: v = new double[] { 0.7, 0.35, 0.10, 0.30, 0.25, 0.05 }; break;
                case CTFXStyle.Post: v = new double[] { 1.0, 0.10, 0.05, 0.05, 0.05, 0.0 }; break;
                default: v = new double[] { 0.4, 0.30, 0.25, 0.25, 0.20, 0.20 }; break;    // roam
            }
            switch (a)
            {
                case CTFXAct.Anchor: return v[0];
                case CTFXAct.Skirmish: return v[1];
                case CTFXAct.Raid: return v[2];
                case CTFXAct.Wander: return v[3];
                case CTFXAct.Hunt: return v[4];
                case CTFXAct.Outside: return v[5];
            }
            return 0.0;
        }

        // ================================================================ goals (CTFXBot.ChooseGoal asks first)
        /// <summary>The next goal tile for a managed bot; false = not managed (its style's own logic runs).</summary>
        internal bool Goal(CTFXBot b, int now, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            if (!Manages(b)) return false;
            CTFXSlot s = b.Slot;
            CTFXTeamCmd t = TeamOf(s);
            if (s.Intent.Foothold && FootholdGoal(s, b, out tx, out ty)) return true;
            switch (s.Intent.Act)
            {
                case CTFXAct.Anchor:
                    if (s.Style == CTFXStyle.Post) return false;                 // the post's own tile and leash
                    if (BaseSweepGoal(t, b, now, out tx, out ty)) return true;   // just cleared: one sweep pass, inside out (CTFXBase.cs)
                    if (OuterGoal(b, out tx, out ty)) return true;               // the base is swept: out to the outdoor line (CTFXSweep)
                    return AnchorGoal(t, s, out tx, out ty);
                case CTFXAct.Defend:
                    if (t.Alarm && LineGoal(t, s, b, out tx, out ty)) return true;   // to its line, by the threat
                    if (t.Alarm) { _m.NearestOpenPx(t.ThreatX + _m.Rng.Next(-48, 49), t.ThreatY + _m.Rng.Next(-48, 49), out tx, out ty); return true; }
                    if (OuterGoal(b, out tx, out ty)) return true;
                    return t.Chokes.Count > 0 && AnchorGoalAt(t.Chokes[0], 80, out tx, out ty);
                case CTFXAct.Skirmish: return SkirmishGoal(t, b, out tx, out ty);
                case CTFXAct.Raid: return RaidGoal(t, b, out tx, out ty);
                case CTFXAct.Hunt:
                    if (HotCell(b, out tx, out ty)) return true;
                    return WanderGoal(t, b, out tx, out ty);
                case CTFXAct.Outside:
                    if (_m.Dropship.OutsideGoal(b, HerdK, out tx, out ty)) return true;
                    return WanderGoal(t, b, out tx, out ty);
                default: return WanderGoal(t, b, out tx, out ty);
            }
        }

        bool AnchorGoal(CTFXTeamCmd t, CTFXSlot s, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            if (t.Chokes.Count == 0) return false;
            if (s.Intent.Choke < 0 || s.Intent.Choke >= t.Chokes.Count) s.Intent.Choke = FreeChoke(t, s);
            CTFXChoke c = t.Chokes[s.Intent.Choke];
            // now and then (8 % of the shuffles) it checks another choke for a moment
            if (t.Chokes.Count > 1 && _m.Rng.NextDouble() < 0.08) c = t.Chokes[_m.Rng.Next(t.Chokes.Count)];
            // a flag the team holds at home: half the anchor shuffles stand over it (CTFXFlags)
            int fx, fy;
            // Travis 10-15: with no attackers in the later parts of the base, anchors guard the outer (secondary turret) choke, not the flag
            // room; they fall back over the flag only with the alarm up or the line pushed to mid / inner
            CTFXBot capt = _m.GetBot(s.BotId);
            bool calm = !t.Alarm && LineHeldOf(t) == 0 && !(capt != null && capt.ClassId == CTFXBot.SK_CAPT);   // the Captain stays in the flag room
            if (calm && t.Chokes.Count > 1 && _m.Rng.NextDouble() < 0.75)
            {
                CTFXChoke far = null; double fd = -1;
                int fx0, fy0;
                bool held = _m.Flags.HeldNear(t.Team, c.Tx * 16 + 8, c.Ty * 16 + 8, 1600.0, out fx0, out fy0);
                foreach (CTFXChoke o in t.Chokes)
                {
                    double dd = held ? Math.Sqrt(Math.Pow(o.Tx * 16 + 8 - fx0, 2) + Math.Pow(o.Ty * 16 + 8 - fy0, 2)) : _m.Rng.NextDouble();
                    if (dd > fd) { fd = dd; far = o; }
                }
                if (far != null) c = far;
                CTFXBot cb = _m.GetBot(s.BotId);
                if (cb != null && cb.HasTd && CornerGoal(c, out tx, out ty)) { TdCorners++; return true; }
                CalmAnchors++;
                return AnchorGoalAt(c, 72, out tx, out ty);
            }
            if (!calm && _m.Flags.HeldNear(t.Team, c.Tx * 16 + 8, c.Ty * 16 + 8, 1600.0, out fx, out fy) && _m.Rng.NextDouble() < 0.5)
            {
                _m.NearestOpenPx(fx + _m.Rng.Next(-64, 65), fy + _m.Rng.Next(-48, 49), out tx, out ty);
                return true;
            }
            CTFXBot ab = _m.GetBot(s.BotId);
            if (ab != null && ab.HasTd && _m.Rng.NextDouble() < 0.7 && CornerGoal(c, out tx, out ty)) { TdCorners++; return true; }   // cycle 19: before the line spot
            if (LineGoal(t, s, ab, out tx, out ty)) return true;   // its line spot (CTFXLines.cs)
            return AnchorGoalAt(c, 72, out tx, out ty);
        }

        /// <summary>
        /// Travis 10-14 night: anchors carrying a Teleport Disruptor "hug corners to maximize their disruption range and force the attackers
        /// further back". The open tile within 112 px of the choke, in sight of it, with the most walls around it (a corner), out of the lane.
        /// </summary>
        bool CornerGoal(CTFXChoke c, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            int cx = c.Tx * 16 + 8, cy = c.Ty * 16 + 8, best = -1;
            for (int i = 0; i < 14; i++)
            {
                int x = cx + _m.Rng.Next(-112, 113), y = cy + _m.Rng.Next(-112, 113);
                if (!_m.BodyFits(x, y) || !_m.Los(x, y, cx, cy)) continue;
                int walls = 0;
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) if ((dx != 0 || dy != 0) && _m.BlockedPx(x + dx * 16, y + dy * 16)) walls++;
                if (walls > best) { best = walls; tx = x >> 4; ty = y >> 4; }
            }
            return best >= 3;
        }

        /// <summary>An open spot within <paramref name="r"/> px of the choke that sees it (6 tries, else the choke itself).</summary>
        bool AnchorGoalAt(CTFXChoke c, int r, out int tx, out int ty)
        {
            int cx = c.Tx * 16 + 8, cy = c.Ty * 16 + 8;
            for (int i = 0; i < 6; i++)
            {
                int x = cx + _m.Rng.Next(-r, r + 1), y = cy + _m.Rng.Next(-r, r + 1);
                if (_m.BodyFits(x, y) && _m.Los(x, y, cx, cy)) { tx = x >> 4; ty = y >> 4; return true; }
            }
            tx = c.Tx; ty = c.Ty;
            return true;
        }

        /// <summary>The way an anchor looks while it holds: at the choke's watch point, sweeping +-20 units at its own pace.</summary>
        internal bool HoldYaw(CTFXBot b, int now, out float yaw)
        {
            yaw = 0f;
            if (Manages(b) && OuterYaw(b, now, out yaw)) return true;                            // out on the swept base's outdoor line
            if (Manages(b) && b.Slot.Style != CTFXStyle.Post && LineYaw(b, now, out yaw)) return true;
            if (!Manages(b) || b.Slot.Intent.Act != CTFXAct.Anchor || b.Slot.Style == CTFXStyle.Post) return false;
            CTFXTeamCmd t = TeamOf(b.Slot);
            int ci = b.Slot.Intent.Choke;
            if (t == null || ci < 0 || ci >= t.Chokes.Count) return false;
            CTFXChoke c = t.Chokes[ci];
            double dx = c.WatchX - b._state.positionX, dy = c.WatchY - b._state.positionY;
            if (dx * dx + dy * dy < 48 * 48) { dx = c.Tx * 16 + 8 - b._state.positionX; dy = c.Ty * 16 + 8 - b._state.positionY; }
            if (dx * dx + dy * dy < 16 * 16) return false;
            double period = 3000.0 + (b.Slot.Human != null ? b.Slot.Human.Seed % 4000 : 0);
            yaw = CTFXBot.WrapYaw(CTFXBot.YawTo(dx, dy) + (float)(20.0 * Math.Sin(now / period * 2.0 * Math.PI)));
            return true;
        }

        /// <summary>A hall node: busy spots (contact heat) appeal, spots teammates already stand at or head for do not.</summary>
        bool SkirmishGoal(CTFXTeamCmd t, CTFXBot b, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            // 10-15 J21: in brown the skirmish is the objective - the focus base's holders hold its outdoor line / chokes, everyone else
            // takes an attack position on it - not the hall's busiest crossing
            if (_m.FocusEnd != null && t.Quad == "brown")
            {
                if (t.End == _m.FocusEnd)
                {
                    if (OuterGoal(b, out tx, out ty)) return true;
                    if (t.Chokes.Count > 0) return AnchorGoalAt(t.Chokes[_m.Rng.Next(t.Chokes.Count)], 96, out tx, out ty);
                }
                else if (_m.StagingGoal(_m.FocusEnd, out tx, out ty)) return true;
            }
            List<int> core = _m.CoreNodes(t.Quad);
            if (core == null || core.Count == 0) return WanderGoal(t, b, out tx, out ty);
            int best = -1;
            double bs = double.MinValue;
            for (int i = 0; i < 7; i++)
            {
                int n = core[_m.Rng.Next(core.Count)];
                int x = _m.Routes.X[n], y = _m.Routes.Y[n];
                double ex = x - b._state.positionX, ey = y - b._state.positionY;
                if (ex * ex + ey * ey < 120 * 120) continue;                     // not where it already stands
                // contact heat used to appeal (1 + heat / 2), which fed the crowd it measured; now a crowd of any team repels
                double sc = 1.0 / (1.0 + HerdK * MatesNear(b, x, y, 200.0)) / (1.0 + 0.4 * _m.CrowdAt(x, y, 200.0)) * (0.7 + _m.Rng.NextDouble() * 0.6);
                if (sc > bs) { bs = sc; best = n; }
            }
            if (best < 0) return WanderGoal(t, b, out tx, out ty);
            tx = _m.Routes.X[best] >> 4; ty = _m.Routes.Y[best] >> 4;
            return true;
        }

        /// <summary>Its own objective in the enemy end, a new one at each arrival: a turret room with a live turret, a patrol
        /// spot, a spawn room, or the mouth.</summary>
        bool RaidGoal(CTFXTeamCmd t, CTFXBot b, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            string enemy = b.Slot.Intent.RaidEnd ?? t.Enemy;
            CTFXBaseDef bd = _m.BaseOf(enemy);
            if (bd != null && _m.Rng.NextDouble() < 0.85 && _m.AttackGoal(b, bd, bd.FX * 16 + 8, bd.FY * 16 + 8, out tx, out ty)) return true;   // CTFXBase.cs
            double u = _m.Rng.NextDouble();
            if (u < 0.35)
            {
                List<CTFXTurretSpot> live = new List<CTFXTurretSpot>();
                foreach (CTFXTurretSpot s in _m.Turrets.Spots) if (s.End == enemy && s.TurretId != 0) live.Add(s);
                if (live.Count > 0)
                {
                    CTFXTurretSpot s = live[_m.Rng.Next(live.Count)];
                    int ox, oy;
                    if (_m.NearestOpen(s.Tx + _m.Rng.Next(-5, 6), s.Ty + _m.Rng.Next(-5, 6), 6, out ox, out oy)) { tx = ox; ty = oy; return true; }
                }
            }
            if (u < 0.65 && _m.RandomRouteNode(enemy + ":patrol", out tx, out ty)) return true;
            if (u < 0.9 && _m.RandomSpawnNode(enemy, out tx, out ty)) return true;
            string other = _m.OtherEnd(enemy);
            return (other != null && _m.RouteNode(enemy + ">" + other + ":raid", 0, out tx, out ty)) || _m.RandomRouteNode(enemy + ":patrol", out tx, out ty);
        }

        /// <summary>Any route node in or around the building (its box + 30 tiles), at least 25 tiles from where it stands.</summary>
        bool WanderGoal(CTFXTeamCmd t, CTFXBot b, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            // a share of the other teams' wanders drift through the brown building (Travis 10-13: the biggest hotspot at all times)
            bool toBrown = _m.FocusEnd != null && t.Quad != "brown" && _m.Rng.NextDouble() < BrownWander;
            int[] box = _m.QuadBox(toBrown ? "brown" : t.Quad);
            CTFXRouteBook rb = _m.Routes;
            if (box == null || rb == null || rb.N == 0) return false;
            int best = -1;
            double bs = double.MinValue;
            for (int i = 0; i < 24; i++)
            {
                int n = _m.Rng.Next(rb.N);
                if (rb.IsGrid(n)) continue;                                                   // grid points are for walking, not goals
                int x = rb.X[n], y = rb.Y[n];
                int pad = toBrown ? 64 : 480;
                if (x < box[0] - pad || x > box[2] + pad || y < box[1] - pad || y > box[3] + pad) continue;
                double ex = x - b._state.positionX, ey = y - b._state.positionY;
                if (ex * ex + ey * ey < 400 * 400) continue;
                double sc = _m.Rng.NextDouble() / (1.0 + HerdK * MatesNear(b, x, y, 240.0));
                if (sc > bs) { bs = sc; best = n; }
            }
            if (best >= 0 && toBrown) BrownWanders++;
            if (best < 0) return false;
            tx = rb.X[best] >> 4; ty = rb.Y[best] >> 4;
            return true;
        }

        /// <summary>Teammates standing within <paramref name="r"/> px of (x, y), or heading for a goal there.</summary>
        int MatesNear(CTFXBot me, int x, int y, double r)
        {
            int n = 0;
            double r2 = r * r;
            foreach (CTFXBot o in _m.LiveBots)
            {
                if (o == me || o.IsDead || o.Released || o._team != me._team) continue;
                double dx = o._state.positionX - x, dy = o._state.positionY - y;
                int gx, gy;
                if (dx * dx + dy * dy <= r2) n++;
                else if (o.GoalTile(out gx, out gy))
                {
                    dx = gx * 16 + 8 - x; dy = gy * 16 + 8 - y;
                    if (dx * dx + dy * dy <= r2) n++;
                }
            }
            return n;
        }

        // ---------------------------------------------------------------- contact heat
        /// <summary>A fight at (x, y): CTFXMonitor adds one per bot in a fight per second; a death adds 3.</summary>
        internal void AddContact(int x, int y, int now, double w)
        {
            int k = (x / 160) * 1000 + (y / 160);
            double[] c;
            if (!_heat.TryGetValue(k, out c)) { c = new double[4]; _heat[k] = c; }
            c[0] = Decayed(c, now) + w; c[1] = now; c[2] = x; c[3] = y;
        }

        static double Decayed(double[] c, int now)
        {
            return c[0] <= 0 ? 0.0 : c[0] * Math.Pow(0.5, (now - (int)c[1]) / 20000.0);
        }

        double HeatAt(int x, int y)
        {
            double[] c;
            return _heat.TryGetValue((x / 160) * 1000 + (y / 160), out c) ? Decayed(c, Environment.TickCount) : 0.0;
        }

        /// <summary>One of the 3 hottest cells within 2500 px (weighted by heat, a teammate crowd there counts against it).</summary>
        bool HotCell(CTFXBot b, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            int now = Environment.TickCount;
            double[] best = new double[3];
            double[][] cell = new double[3][];
            List<int> stale = null;
            foreach (KeyValuePair<int, double[]> kv in _heat)
            {
                double h = Decayed(kv.Value, now);
                if (h < 0.5) { if (stale == null) stale = new List<int>(); stale.Add(kv.Key); continue; }
                double dx = kv.Value[2] - b._state.positionX, dy = kv.Value[3] - b._state.positionY;
                if (dx * dx + dy * dy > 1800.0 * 1800.0) continue;
                h /= 1.0 + HerdK * 0.5 * MatesNear(b, (int)kv.Value[2], (int)kv.Value[3], 240.0);
                for (int i = 0; i < 3; i++)
                    if (h > best[i])
                    {
                        for (int j = 2; j > i; j--) { best[j] = best[j - 1]; cell[j] = cell[j - 1]; }
                        best[i] = h; cell[i] = kv.Value;
                        break;
                    }
            }
            if (stale != null) foreach (int k in stale) _heat.Remove(k);
            double sum = best[0] + best[1] + best[2];
            if (sum <= 0) return false;
            double u = _m.Rng.NextDouble() * sum;
            int pick = 0;
            for (int i = 0; i < 3; i++) { u -= best[i]; if (u <= 0) { pick = i; break; } }
            if (cell[pick] == null) pick = 0;
            _m.NearestOpenPx((int)cell[pick][2] + _m.Rng.Next(-40, 41), (int)cell[pick][3] + _m.Rng.Next(-40, 41), out tx, out ty);
            return true;
        }

        /// <summary>The pause at a goal by activity (ms, x the seat's restlessness x pauseScale); -1 = not managed.</summary>
        internal int PauseMs(CTFXBot b)
        {
            if (!Manages(b)) return -1;
            Random r = _m.Rng;
            int ms;
            switch (b.Slot.Intent.Act)
            {
                case CTFXAct.Anchor: ms = 2500 + r.Next(4501); break;
                case CTFXAct.Defend: ms = 200 + r.Next(701); break;
                case CTFXAct.Skirmish: ms = 400 + r.Next(1801); break;
                case CTFXAct.Raid: ms = 200 + r.Next(1001); break;
                case CTFXAct.Hunt: ms = 200 + r.Next(701); break;
                case CTFXAct.Outside: ms = 400 + r.Next(1601); break;
                default: ms = r.NextDouble() < 0.1 ? 2500 + r.Next(2501) : 300 + r.Next(1501); break;   // wander: now and then a look around
            }
            if (b.Style == CTFXStyle.Sniper && b.Slot.Intent.Act != CTFXAct.Anchor) ms *= 3;      // a sniper settles into a perch
            return (int)(ms * Human(b).Restless * PauseScale);
        }

        // ================================================================ self-tuning
        /// <summary>One bounded step per knob toward its target, from the window's measures. Logged and saved.</summary>
        internal void Tune(double idleFree, int idleCluster, double topShare, int alarmMs, int turretsLost, double defendFight, int windowMs)
        {
            // not in the first 90 s after a start: every bot is just out of its spawn room and the engineers are building
            if (!TuneOn || Teams.Count == 0 || Environment.TickCount - _setupAt < 90000) return;
            List<string> ch = new List<string>();
            double ps = PauseScale, hk = HerdK, dr = DefendRatio;
            if (idleFree > 0.15) PauseScale = Math.Max(0.4, PauseScale * 0.9);
            else if (idleFree < 0.05) PauseScale = Math.Min(1.6, PauseScale * 1.05);
            if (idleCluster >= 4 || topShare > 0.55) HerdK = Math.Min(1.5, HerdK + 0.15);   // capped: crowd aversion also spreads bots out of brown
            else if (idleCluster <= 2 && topShare < 0.4) HerdK = Math.Max(0.5, HerdK - 0.05);
            if (alarmMs > windowMs * 0.3 && turretsLost >= 2) DefendRatio = Math.Min(2.5, DefendRatio + 0.2);
            else if (alarmMs > 0 && defendFight >= 0 && defendFight < 0.2) DefendRatio = Math.Max(1.0, DefendRatio - 0.1);
            if (PauseScale != ps) ch.Add("pauseScale " + F(ps) + ">" + F(PauseScale) + " (free bots idle " + Pct(idleFree) + ")");
            if (HerdK != hk) ch.Add("herdK " + F(hk) + ">" + F(HerdK) + " (idle cluster " + idleCluster + ", top activity " + Pct(topShare) + ")");
            if (DefendRatio != dr) ch.Add("defendRatio " + F(dr) + ">" + F(DefendRatio) + " (alarm " + alarmMs / 1000 + " s, turrets lost " + turretsLost
                + ", defenders fighting " + (defendFight < 0 ? "-" : Pct(defendFight)) + ")");
            if (ch.Count == 0) return;
            string line = "tune: " + string.Join("; ", ch.ToArray());
            TuneLog.Add(DateTime.Now.ToString("HH:mm:ss") + " " + line);
            if (TuneLog.Count > 20) TuneLog.RemoveAt(0);
            _m.Log("[CTFXBots] " + line);
            SaveTune();
        }

        internal string Knobs()
        {
            return "pauseScale " + F(PauseScale) + ", herdK " + F(HerdK) + ", defendRatio " + F(DefendRatio) + (TuneOn ? " (self-tuning)" : " (fixed)");
        }

        void SaveTune()
        {
            try
            {
                File.WriteAllText(TunePath, "// CTF:X bot knobs the self-tuning settled on (CTFXCommand.Tune). Read at each start; delete to go back to CTFXBots.txt.\r\n"
                    + "pauseScale=" + F(PauseScale) + "\r\nherdK=" + F(HerdK) + "\r\ndefendRatio=" + F(DefendRatio) + "\r\n");
            }
            catch (Exception) { }
        }

        void LoadTune()
        {
            try
            {
                if (!File.Exists(TunePath)) return;
                foreach (string raw in File.ReadAllLines(TunePath))
                {
                    int eq = raw.IndexOf('=');
                    if (raw.StartsWith("//") || eq <= 0) continue;
                    double v;
                    if (!double.TryParse(raw.Substring(eq + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) continue;
                    switch (raw.Substring(0, eq).Trim().ToLowerInvariant())
                    {
                        case "pausescale": PauseScale = Math.Max(0.4, Math.Min(1.6, v)); break;
                        case "herdk": HerdK = Math.Max(0.5, Math.Min(3.0, v)); break;
                        case "defendratio": DefendRatio = Math.Max(1.0, Math.Min(2.5, v)); break;
                    }
                }
                _m.Log("[CTFXBots] tune: read " + TunePath + ": " + Knobs());
            }
            catch (Exception) { }
        }

        internal void ResetTune(double pause, double herd, double defend)
        {
            PauseScale = pause; HerdK = herd; DefendRatio = defend;
            try { if (File.Exists(TunePath)) File.Delete(TunePath); } catch (Exception) { }
        }

        // ================================================================ stats
        /// <summary>Stats line: per base team the activity mix, its alarm and anchor counters (cleared by CTFXMonitor).</summary>
        internal string Summary()
        {
            if (!On || Teams.Count == 0) return "";
            StringBuilder sb = new StringBuilder(" | command");
            foreach (CTFXTeamCmd t in Teams)
            {
                int[] n = new int[10];
                foreach (CTFXSlot s in t.Seats)
                {
                    CTFXBot b = _m.GetBot(s.BotId);
                    if (b != null && !b.IsDead) n[(int)s.Intent.Act]++;
                }
                sb.Append(" [").Append(t.Name).Append(": anchor ").Append(n[1]).Append(" defend ").Append(n[2]).Append(" skirmish ").Append(n[3])
                  .Append(" raid ").Append(n[4]).Append(" wander ").Append(n[5]).Append(" hunt ").Append(n[6]).Append(" outside ").Append(n[9]).Append(" build ").Append(n[7]).Append(" storm ").Append(n[8])
                  .Append(" | alarm ").Append(t.Alarm ? "ON " + t.Threat + (t.HumanUntil != 0 ? (t.Desperate ? " (called: under attack)" : " (called: sighted)") : "") : "off")
                  .Append(", teammate calls ").Append(t.HumanCalls).Append(" joined ").Append(t.HumanJoined)
                  .Append(", alarms ").Append(t.Alarms).Append(", heard/joined/ignored ")
                  .Append(t.Heard).Append('/').Append(t.Joined).Append('/').Append(t.Ignored).Append(", teleports ").Append(t.Teleports)
                  .Append(", response ").Append(t.RespN > 0 ? (t.RespSum / t.RespN / 100 / 10.0).ToString("F1", CultureInfo.InvariantCulture) + " s" : "-")
                  .Append(", anchor asks yes/no ").Append(t.AnchorYes).Append('/').Append(t.AnchorNo).Append(", handoffs ").Append(t.Handoffs).Append(']');
            }
            sb.Append(" picks ").Append(Picks).Append(", rejoin teleports ").Append(Rejoins).Append(" + hops ").Append(Hops).Append(" (walked ").Append(RejoinWalks).Append("), ").Append(Knobs());
            return sb.ToString();
        }

        // ================================================================ helpers
        CTFXHuman Human(CTFXBot b)
        {
            if (b != null && b.Slot != null && b.Slot.Human != null) return b.Slot.Human;
            if (b != null && b.Slot != null) { b.Slot.Human = CTFXHuman.Roll(_m.Rng, _m.NameFor(b.Slot)); return b.Slot.Human; }
            return CTFXHuman.Roll(_m.Rng);
        }

        double DistTo(CTFXSlot s, int x, int y)
        {
            CTFXBot b = _m.GetBot(s.BotId);
            if (b == null) return double.MaxValue;
            double dx = b._state.positionX - x, dy = b._state.positionY - y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        CTFXBot NearestMate(CTFXBot me, int x, int y, double within)
        {
            CTFXBot best = null;
            double bd = within * within;
            foreach (CTFXBot o in _m.LiveBots)
            {
                if (o == me || o.IsDead || o.Released || o._team != me._team || o.InDropship) continue;
                double dx = o._state.positionX - x, dy = o._state.positionY - y, d = dx * dx + dy * dy;
                if (d < bd) { bd = d; best = o; }
            }
            return best;
        }

        static string F(double v) { return v.ToString("0.00", CultureInfo.InvariantCulture); }
        static string Pct(double v) { return ((int)Math.Round(v * 100)).ToString(CultureInfo.InvariantCulture) + "%"; }
    }
}
/* PARSER GUARD - keep last */
