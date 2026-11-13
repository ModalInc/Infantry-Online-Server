// MiniTP bots (CTF:X engine, MiniTP profile) - POINTS, SEAT TABLE, TRAVERSAL and the basic attack / defend GOALS.
// Spec: MiniTPBots\MINITP-BOT-META-SPEC.md (sections 2, 2a, 3, 14), plan: MiniTPBots\PHASE1-PLAN.md.
//
// Runs only when the manager's profile is MiniTP (event MiniTP + MiniTP/MiniTPBots.txt enabled). Everything here is reached through
// CTFXBotManager.Mini, which is null in the CTF:X profile, so the CTF:X zone never executes a line of it.
//
// How a MiniTP bot lives (phase 1):
//   Ship    spawns in its team's dropship room (Titan 654,518 north room / Collective 648,565 south room), waits 3-8 s (gear-up).
//   Defender: "yellow home portal" -> lands at its own base's home structure (A7 Collective, D7 Titan) -> Defend (its role anchor).
//   Attacker: dropship exit portal -> FIELD landing -> walks the route book to the ENEMY base's entry portal in the field -> lands in the
//             enemy base (landA / landB) -> Raid: the BOX anchor (turret gauntlet), then the mouth, then the flag room.
//   Bots cannot trigger LIO portals (they are not players), so every hop is a scripted Teleported() followed by a short warp grace.
// The route book (MiniTP/MiniTPRoutes.txt) is area-based (A7 D7 FIELD DST DSC); goals are always picked inside the bot's own area.
/* PARSER GUARD: keep a block comment after the last line comment */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_CTF
{
    /// <summary>One bot's MiniTP state (CTFXBot.Mini).</summary>
    internal sealed class MiniBot
    {
        public const int Ship = 0, Field = 3, Raid = 4, Defend = 5;
        public int Phase = Ship, PhaseAt, WaitUntil;
        public int Side;            // 0 = Titan, 1 = Collective
        public bool Attack;
        public string Role;         // SL MED ENG DHVY DINF OHVY OINF JT INFIL
        public int Variant;         // which of a role's two anchors / entry portals
        public int Stage, StageUntil;
        public int PortalX, PortalY;   // Field: the entry portal tile it walks to
        public int Hops;
        public int Idx, Dir = 1, Top = 0, NextTopAt, Tour = -1;     // Defend: the sweep on the choke ladder (level now, direction, how far out it pushes, when that changes)
        public bool Hunting;        // its goal is an enemy inside its area (short pauses)
        public bool Said;           // it has made its first callout this life
        public int WideAt;          // when WideNow was last eased (ms): the ease is by time, not per poll
        public int Sentries;                // Sentry items an engineer carries (it builds a sentry by using one: the maker's own ammo is item 23)
        public int BuildIdx = -1;           // Engineer: the BaseTurrets row it is walking to / standing at (BuildStart, above, is since when it has stood still there)
        public int[] SpotCool;              // Engineer: per BaseTurrets row, no (re)build before this tick
        public bool SeatAttack;     // what its seat table row says (an all-in side attacks whatever the seat says; this is restored afterwards)
        public string EngTask;      // the Engineer's current subroutine (repair / patrol / restock): said in team chat when it changes (Travis 11-08)
        public int EngSayAt;        // the earliest it may announce again
        public double WideNow, Smooth = 0.5;   // fluid movement: the sideways swing it is at now (px), how long a look-ahead it takes (0..1)
        public int WideSign = 1;    // which side it swings to
        public int RelocUntil, RelocX, RelocY;   // an anti-warped Squad Leader walks out of the disruptor's range until then (tile)
        public bool WideLikely;     // it is one of the bots that takes a wide approach to a contact
        public bool Hold;           // its goal is a firing position it holds (long pauses)
        public int CensusX, CensusY; // where it was at the last census (stillness measure)
        public int StandX, StandY, StandAt, StandTur, WaveAt, WaveGoAt, SuppX, SuppY, SuppUntil;       // turret standoff spot (OHVY), wave staging at the entry portal
        public object ShooterKey, ShooterLast;           // the enemy it is going after because he is shooting a turret
        public int RestockAt;               // an engineer out of repair charges: when it may recall to restock again
        public int BlockedSince;            // since when its keys were down and it went nowhere with no target (UnstickCheck)
        public int FootX, FootY, FootStage = -1;   // Raid: the cover spot this raider claimed for the stage it is in (Foothold); FootStage = stage key, -1 = none
        public double Bearing;              // Raid: its own side of an enemy it hunts (radians off the straight line), fixed per bot (Flank)
        public int Spot = -1, BuildStart;   // green portal builder: which of its side's two spots it walks to / stands on, since when
        public double Aggr = 1.0;   // how fast it pushes out while the base is quiet (0.7 cautious .. 1.5 bold), rolled per bot
        public Player Chasing;               // the enemy flag carrier it is going after (carrier-first, Travis 11-08)
        public int CarrierPoll, ChaseRefresh;   // carrier-first: when it looks for a carrier again / refreshed its goal toward the one it chases
        public int MineAt, MinesLaid, MineFails;   // the mine rule (MineStep): next try, mines laid this life, consecutive failed tries
        public bool WaveFree;               // 11-11: the wave released it - it walks from the staging spot onto the portal (before: it stood ON the portal while it waited)
        public int PortalCool;              // 11-11: no portal touch before this tick (just landed from a warp)
        public bool Rotated;                // 11-11: a defender sent out to attack because its base was quiet for 30 s (back to its seat when the base is hit again / at its next body)
        public Arena.FlagState GrabFlag;    // 11-11: the free enemy flag it is going for
        public int CarryAskAt, CarrySaidAt; // 11-11: a flag carrier away from home: next summon try / next 'S me home' line
        public int Dig, DigAt, DigStart, DigNext, Cells, BoardAt, EngTso, DropX, DropY;   // 11-11: the engineer's mining trip (CTFXMiniMine): step, since, trip start, next trip, Light PowerCells, board entry time, Tsolvy carried, its drop spot
        public int PileSeenAt; public bool PileGo;                                        // 11-11: the medic saw a Tsolvy pile (reaction delay by tier) / goes for it
    }

    /// <summary>One seat of a MiniTP team's table.</summary>
    internal struct MiniSeat
    {
        public bool Attack;
        public string Cls, Build, Role;
        public MiniSeat(bool attack, string cls, string build, string role) { Attack = attack; Cls = cls; Build = build; Role = role; }
    }

    internal sealed partial class CTFXMini
    {
        readonly CTFXBotManager _m;
        readonly Dictionary<string, int[]> _pt = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);   // "A7:flag1" -> tile x, y
        internal string LoadNote = "points: not loaded";

        // side 0 = Titan Militia (base D7, ship room DST, exits E5-E8, attacks A7), side 1 = Collective (base A7, ship DSC, exits E1-E4, attacks D7)
        static readonly string[] ShipArea = { "DST", "DSC" };
        static readonly string[] HomeArea = { "D7", "A7" };
        static readonly string[] FoeArea = { "A7", "D7" };
        static readonly int[][] Exits = { new int[] { 5, 6, 7, 8 }, new int[] { 1, 2, 3, 4 } };
        // the field portals that lead into the ENEMY base: Titan walks to "toA7a/b", Collective to "toD7a/b"
        static readonly string[][] EntryPortals = { new string[] { "toA7a", "toA7b" }, new string[] { "toD7a", "toD7b" } };
        // landings inside a base after an entry portal (the base's landA / landB)
        static readonly string[] Landing = { "landA", "landB" };

        internal int Warps, Stuck, Deaths, CrossArea, Unstuck;
        const int UnstickMs = 25000;      // a Defend / Raid bot blocked this long is put back by the scripted hop (Pub 11-08: V0rtex sat 26 min in the A7 exit chamber)
        int _unstuckLogged;

        // ---- the flaws by skill tier (rookie, regular, veteran, ace): what a life OWNS, and how often it forgets to switch a toggled item on / off
        static int TierIdx(string t)
        {
            switch ((t ?? "regular").ToLowerInvariant()) { case "rookie": return 0; case "veteran": return 2; case "ace": return 3; }
            return 1;
        }
        static readonly double[] GenOwnT = { 0.55, 0.78, 0.92, 0.97 }, EsOwnT = { 0.15, 0.35, 0.55, 0.75 }, TdOwnT = { 0.12, 0.32, 0.58, 0.85 },
            ForgetOnT = { 0.30, 0.18, 0.08, 0.03 }, ForgetOffT = { 0.40, 0.25, 0.12, 0.04 };
        internal double GenOwn(string tier) { return GenOwnT[TierIdx(tier)]; }
        internal double EsOwn(string tier) { return EsOwnT[TierIdx(tier)]; }
        internal double TdOwn(string tier) { return TdOwnT[TierIdx(tier)]; }
        internal double ForgetOn(string tier) { return ForgetOnT[TierIdx(tier)]; }
        internal double ForgetOff(string tier) { return ForgetOffT[TierIdx(tier)]; }
        internal bool Owns(double p) { return _m.Rng.NextDouble() < p; }

        /// <summary>An enemy of the bot inside its area within <paramref name="px"/> (used by the TD switch).</summary>
        internal bool FoeNear(CTFXBot b, double px)
        {
            int fx, fy;
            return NearestFoe(b, AreaOf(b._state.positionX >> 4, b._state.positionY >> 4), px, out fx, out fy);
        }

        internal int[] SwingDrops = new int[5];       // answers that never came: gone, hunting, wrong phase, no summoner, retried
        internal int GraceMs = 1000;

        internal CTFXMini(CTFXBotManager m) { _m = m; }

        // ---------------------------------------------------------------- points (MiniTP/MiniTPPoints.txt)
        internal void LoadPoints()
        {
            _pt.Clear();
            string path = Path.Combine("scripts", Path.Combine("GameTypes", Path.Combine("CTF", Path.Combine("MiniTP", "MiniTPPoints.txt"))));
            try
            {
                if (!File.Exists(path)) { LoadNote = "points: no " + path; return; }
                foreach (string raw in File.ReadAllLines(path))
                {
                    string l = raw.Trim();
                    if (!l.StartsWith("point=", StringComparison.Ordinal)) continue;
                    string[] f = l.Substring(6).Split(',');
                    int x, y;
                    if (f.Length < 4 || !int.TryParse(f[2].Trim(), out x) || !int.TryParse(f[3].Trim(), out y)) continue;
                    _pt[f[0].Trim() + ":" + f[1].Trim()] = new int[] { x, y };
                }
                LoadNote = "points: " + _pt.Count + " from MiniTPPoints.txt";
            }
            catch (Exception ex) { LoadNote = "points: " + ex.Message; }
        }

        internal bool Pt(string area, string name, out int tx, out int ty)
        {
            int[] p;
            if (_pt.TryGetValue(area + ":" + name, out p)) { tx = p[0]; ty = p[1]; return true; }
            tx = 0; ty = 0;
            return false;
        }

        internal bool Have { get { return _pt.Count > 0; } }

        /// <summary>The synthetic "home zone" of a side's groups (CTFXBotGroup.Home must not be null): the base's flag room.</summary>
        internal CTFXZone ZoneOf(int side)
        {
            int x, y;
            if (!Pt(HomeArea[side], "home", out x, out y)) { x = side == 0 ? 1077 : 976; y = side == 0 ? 628 : 649; }
            return new CTFXZone(HomeArea[side].ToLowerInvariant(), 30, new int[] { x }, new int[] { y });
        }

        // ---------------------------------------------------------------- the seat table (spec 2, 2a)
        // Defense group: Medic + Engineer always, a defensive heavy (dhvy) and defensive infantry (dinf). Attack group: the Squad Leader
        // ALWAYS, then heavies (ohvy), offensive infantry (oinf), and a rolled mix of JT / Infiltrator. Heavy cap 3 per team.
        internal List<MiniSeat> Roster(int per, Random rng)
        {
            List<MiniSeat> r = new List<MiniSeat>();
            // ---- defense
            r.Add(new MiniSeat(false, "Med", "med", "MED"));
            r.Add(new MiniSeat(false, "Eng", "eng", "ENG"));
            if (per >= 10) r.Add(new MiniSeat(false, "Hvy", "dhvy", "DHVY"));
            if (per >= 7 && per < 10) r.Add(new MiniSeat(false, "Hvy", "dhvy", "DHVY"));
            if (per >= 14) { r.Add(new MiniSeat(false, "Inf", "dinf", "DINF")); /* one Medic per team (Travis 11-08): the second defender medic seat is a fourth defensive infantry */ r.Add(new MiniSeat(false, "Inf", "dinf", "DINF")); r.Add(new MiniSeat(false, "Inf", "dinfcaw", "DINF")); r.Add(new MiniSeat(false, "Inf", "dinfcara", "DINF")); }
            else if (per >= 10) { r.Add(new MiniSeat(false, "Inf", "dinf", "DINF")); r.Add(new MiniSeat(false, "Inf", "dinfcaw", "DINF")); }
            // ---- attack (the SL is always here)
            r.Add(new MiniSeat(true, "SL", "slBR", "SL"));
            if (per >= 14)
            {
                r.Add(new MiniSeat(true, "Hvy", "ohvy", "OHVY")); r.Add(new MiniSeat(true, "Hvy", "ohvy2", "OHVY"));
                r.Add(new MiniSeat(true, "JT", "jt", "JT")); r.Add(new MiniSeat(true, "Infil", "infil", "INFIL"));
                r.Add(new MiniSeat(true, "Inf", "oinf", "OINF")); r.Add(Fighter("OINF"));      // one Medic per team (Travis 11-08): the attack medic seat of 11-08 is a second offensive infantry again the raiders (it heals whoever is hurt within 500 px)
            }
            else if (per >= 10)
            {   // four fighters beside the SL: a rolled spread (spec 2a table), heavy cap respected (one dhvy already)
                int spread = rng.Next(6);
                string[] f = spread == 0 ? new[] { "OINF", "OINF", "OINF", "OHVY" }
                    : spread == 1 ? new[] { "OINF", "OINF", "OHVY", "JT" }
                    : spread == 2 ? new[] { "OINF", "OINF", "JT", "INFIL" }
                    : spread == 3 ? new[] { "OINF", "OINF", "INFIL", "OHVY" }
                    : spread == 4 ? new[] { "OINF", "OINF", "OINF", "OINF" }
                    : new[] { "OINF", "OINF", "OINF", "INFIL" };
                foreach (string role in f) r.Add(Fighter(role));
            }
            else if (per >= 7)
            {
                r.Add(Fighter("OINF")); r.Add(Fighter("OINF"));
                string[] x = { "OHVY", "JT", "INFIL" };
                r.Add(Fighter(x[rng.Next(3)]));
            }
            else
            {
                r.Add(Fighter("OHVY")); r.Add(Fighter("OINF"));
            }
            return r;
        }

        static MiniSeat Fighter(string role)
        {
            switch (role)
            {
                case "OHVY": return new MiniSeat(true, "Hvy", "ohvy", "OHVY");
                case "JT": return new MiniSeat(true, "JT", "jt", "JT");
                case "INFIL": return new MiniSeat(true, "Infil", "infil", "INFIL");
                default: return new MiniSeat(true, "Inf", "oinf", "OINF");
            }
        }

        // ---------------------------------------------------------------- spawn / spawned
        internal static int SideOf(CTFXBotGroup g) { return g.Kind == CTFXKind.Titan ? 0 : 1; }

        /// <summary>The team's dropship spawn rect (the DST / DSC "spawn" point +- 1 tile).</summary>
        internal void SpawnTile(CTFXBotGroup g, out int tx, out int ty)
        {
            int side = SideOf(g);
            int cx, cy;
            if (!Pt(ShipArea[side], "spawn", out cx, out cy)) { cx = side == 0 ? 654 : 648; cy = side == 0 ? 519 : 565; }
            tx = cx; ty = cy;
            for (int i = 0; i < 12; i++)
            {
                int x = cx + _m.Rng.Next(-1, 2), y = cy + _m.Rng.Next(-1, 2);
                if (_m.BodyFits(x * 16 + 8, y * 16 + 8)) { tx = x; ty = y; return; }
            }
        }

        /// <summary>A new body came up (first spawn or a respawn): it starts in its dropship room.</summary>
        internal void Spawned(CTFXBot b, int now)
        {
            CTFXSlot s = b.Slot;
            MiniBot mb = new MiniBot();
            mb.Side = SideOf(s.Group);
            mb.SeatAttack = s.Attack;
            mb.Attack = s.Attack || _allIn == mb.Side;           // a side that is all in (the other team holds every flag) sends every new body to attack (Travis 11-08)
            mb.Role = s.Role ?? "OINF";
            mb.Variant = s.StyleIndex & 1;
            mb.Phase = MiniBot.Ship;
            mb.PhaseAt = now;
            mb.WaitUntil = now + 2000 + _m.Rng.Next(2500);
            mb.NextTopAt = now + 6000;
            mb.Aggr = 0.7 + _m.Rng.NextDouble() * 0.8;
            mb.Smooth = _m.Rng.NextDouble(); mb.WideSign = _m.Rng.Next(2) == 0 ? -1 : 1; mb.WideLikely = _m.Rng.NextDouble() < 0.5;
            mb.Bearing = (_m.Rng.NextDouble() - 0.5) * 2.2;
            mb.Sentries = mb.Role == "ENG" ? 1 : 0;
            mb.SpotCool = new int[12];
            for (int ci = 0; ci < mb.SpotCool.Length; ci++) mb.SpotCool[ci] = now + 20000;      // the event has just built them: no rebuild before the live list can show them
            b.Mini = mb;
            b.GraceUntil = now + GraceMs;
            b.MarkX = b._state.positionX; b.MarkY = b._state.positionY;      // the dropship spot it starts on is its recall mark (CTFXBot.Recall: the engineer restocks there; MarkX was only set in the CTF:X ship, so no MiniTP bot could ever recall)
        }

        // ---------------------------------------------------------------- any warp (CTFXBot.Teleported): keep the phase in step with where the bot landed
        internal void Warped(CTFXBot b, int now)
        {
            MiniBot mb = b.Mini;
            if (mb == null) return;
            b.GraceUntil = now + GraceMs;                                   // spec 8: a bot takes no damage right after any warp (summon, teleport, portal)
            mb.WaveAt = 0; mb.WaveGoAt = 0; mb.WaveFree = false; mb.PortalCool = now + 1500; mb.GrabFlag = null;
            int a = AreaOf(b._state.positionX >> 4, b._state.positionY >> 4);
            if (a != 3 && a != 4 && b.InDropship) b.InDropship = false;       // a recall (BackToShip) marks the body "in the dropship"; leaving the ship room by any hop ends it (nobody fights, heals or is a target while it stands)
            int home = HomeAreaIdx(mb.Side), foe = home == 0 ? 1 : 0;
            if (a == home) { if (mb.Phase != MiniBot.Defend) { mb.Phase = MiniBot.Defend; mb.PhaseAt = now; mb.Hunting = false; } }
            else if (a == foe)
            {
                if (mb.Phase != MiniBot.Raid)
                {
                    mb.Phase = MiniBot.Raid; mb.PhaseAt = now; mb.FootStage = -1;
                    mb.Stage = mb.Role == "JT" || mb.Role == "INFIL" ? 1 : 0;
                    mb.StageUntil = now + 12000 + _m.Rng.Next(18000);
                }
            }
            else if (a == 2)
            {   // the field: an attacker walks on to the enemy base's entry portal, a defender goes home through the ship hop
                int px, py;
                if (mb.Attack && mb.Phase != MiniBot.Field && Pt("FIELD", EntryPortals[mb.Side][mb.Variant & 1], out px, out py)) { mb.PortalX = px; mb.PortalY = py; mb.Phase = MiniBot.Field; mb.PhaseAt = now; }
                else if (!mb.Attack && mb.Dig == 0) { mb.Phase = MiniBot.Ship; mb.PhaseAt = now; mb.WaitUntil = now + 500; }
                else if (mb.Dig > 0) { mb.Phase = MiniBot.Field; mb.PhaseAt = now; }      // 11-11: mining in the field
            }
            else if (a == 3 || a == 4) { if (mb.Phase != MiniBot.Ship) { mb.Phase = MiniBot.Ship; mb.PhaseAt = now; mb.WaitUntil = now + 1500 + _m.Rng.Next(2000); } }
        }

        // ---------------------------------------------------------------- per poll: the hops
        int _stepAt;

        int _censusAt, _failLogged;

        int _crossLogged;

        /// <summary>A goal in another area was dropped (CTFXBot.PlanRoute). The first few are logged: who, where, and what it was doing.</summary>
        internal void CrossDropped(CTFXBot b, int px, int py, int gx, int gy, string why)
        {
            CrossArea++;
            if (_crossLogged >= 25) return;
            _crossLogged++;
            MiniBot mb = b.Mini;
            _m.MiniLog("[MiniTP] cross-area goal dropped #" + _crossLogged + ": " + (mb != null ? mb.Role + " phase " + mb.Phase : "?") + " style " + b.Style + " from " + (px >> 4) + "," + (py >> 4)
                + " to " + (gx >> 4) + "," + (gy >> 4) + " why='" + why + "' target " + (b.HasTarget ? "yes" : "no"));
        }

        /// <summary>A bot's route plan failed (CTFXBot.PlanRoute): the first few are logged with where it was and where it wanted to go.</summary>
        internal void PlanFailed(CTFXBot b, int px, int py, int gx, int gy, int from, int to)
        {
            if (_failLogged >= 20) return;
            _failLogged++;
            MiniBot mb = b.Mini;
            _m.MiniLog("[MiniTP] plan failed #" + _failLogged + ": " + (mb != null ? mb.Role + " phase " + mb.Phase : "?") + " from " + (px >> 4) + "," + (py >> 4)
                + " to " + (gx >> 4) + "," + (gy >> 4) + " | nodes " + from + " -> " + to);
        }

        /// <summary>One log line every 10 s: bots per side and phase, and a few positions (the way to check a run without a client).</summary>
        void Census(int now)
        {
            int[,] n = new int[2, 6];
            int[] still = new int[6], seen = new int[6], hist = new int[8];
            int[] uf = new int[6];
            int hunting = 0, holding = 0, cloakedNow = 0;
            int dead = 0, total = 0;
            StringBuilder sample = new StringBuilder();
            StringBuilder engs = new StringBuilder();
            int shown = 0;
            foreach (CTFXBot b in _m.LiveBots)
            {
                MiniBot mb = b.Mini;
                if (mb == null) continue;
                total++;
                if (mb.Role == "ENG")
                    engs.Append(' ').Append(mb.Side == 0 ? "T" : "C").Append(':').Append(b.IsDead ? "dead" : mb.Phase == 0 ? "ship" : mb.Phase == 3 ? "field" : mb.Phase == 4 ? "raid" : "def")
                        .Append(" oxide ").Append(b.KitCharges).Append(" sentries ").Append(mb.Sentries).Append(" job ").Append(mb.BuildIdx).Append(" task ").Append(mb.EngTask ?? "-")
                        .Append('@').Append(b._state.positionX >> 4).Append(',').Append(b._state.positionY >> 4);
                if (b.IsDead) { dead++; continue; }
                n[mb.Side, mb.Phase]++;
                {   // stillness: moved less than 3 tiles in the 10 s since the last census
                    int sx = b._state.positionX, sy = b._state.positionY;
                    int dx = sx - mb.CensusX, dy = sy - mb.CensusY;
                    if (mb.CensusX != 0 && dx * dx + dy * dy < 48 * 48) still[mb.Phase]++;
                    if (mb.CensusX != 0) seen[mb.Phase]++;
                    mb.CensusX = sx; mb.CensusY = sy;
                    if (mb.Phase == MiniBot.Defend && b.Mini.Role != "MED" && b.Mini.Role != "ENG") hist[Math.Max(0, Math.Min(7, mb.Idx))]++;
                    int flags = b.UtilFlags();
                    for (int bit = 0; bit < 6; bit++) if ((flags & (1 << bit)) != 0) uf[bit]++;
                    if (mb.Hunting) hunting++;
                    if (mb.Hold) holding++;
                    if (b.Cloaked) cloakedNow++;
                }
                if (shown < 8 && (shown < 4 || mb.Phase == MiniBot.Raid))
                {
                    shown++;
                    sample.Append(' ').Append(b.Slot != null ? b.Slot.Name : "?").Append('/').Append(mb.Role).Append(':').Append(mb.Phase == 0 ? "ship" : mb.Phase == 3 ? "field" : mb.Phase == 4 ? "raid" : "def")
                          .Append('@').Append(b._state.positionX >> 4).Append(',').Append(b._state.positionY >> 4);
                }
            }
            _m.MiniLog("[MiniTP] bots " + total + " (dead " + dead + ") | Titan ship/field/raid/def " + n[0, 0] + "/" + n[0, 3] + "/" + n[0, 4] + "/" + n[0, 5]
                + " | Collective " + n[1, 0] + "/" + n[1, 3] + "/" + n[1, 4] + "/" + n[1, 5] + " | engineer: rebuilt " + Rebuilt + ", sentries dropped/taken " + SentriesDropped + "/" + SentriesTaken + ", minerals taken " + MineralsTaken + " [" + engs.ToString().Trim() + "] | tactics: suppress fired/tried " + Suppressions + "/" + SuppressTried + ", waves held/released/timed out " + WaveHolds + "/" + WaveReleases + "/" + WaveTimeouts + ", standoff spots/none " + StandoffSpots + "/" + StandoffNone + ", turret shooters flagged/chased " + ShootersFlagged + "/" + ShooterChases + ", bot shots/hits on turrets " + _m.Turrets.ShotsAtTurrets + "/" + _m.Shots.TurretHits + " | carriers now " + _carriers.Count + " seen " + CarrierSeen + " dropped/dead " + CarrierDropped + " chases " + CarrierChases + " calls " + CarrierCalls + " | watch turns " + WatchTurns + " holders facing the watch point (cumulative) " + FaceOk + "/" + FaceN + " | mines laid/tried/blocked " + MinesLaid + "/" + MineTried + "/" + MineBlocked + " | cloak on/off " + CloakOns + "/" + CloakOffs + " now cloaked " + cloakedNow + " | all-in " + (_allIn < 0 ? "off" : _allIn == 0 ? "Titan" : "Collective") + " hops " + AllInHops + " flag touches " + FlagTouches + " | 11-11: portal walk-ins " + PortalWalkIns + " touches " + PortalTouches + " (no exit " + PortalNoExit + ") quiet rotations " + QuietRotations + " (" + QuietSent + " sent, " + QuietRecalled + " back) flag grabs " + GrabGoals + "/" + FlagTouches + " rebuilds waiting for energy " + RebuildNoEnergy + " " + FlagCensus() + " | carry: picks " + CarryPicks + " from humans " + CarryFromHumans + " summoned " + CarrySummons + " home " + CarryHome + " timeouts " + CarryTimeouts + " lost " + CarryLost + " pinned " + CarryPinned + " asks " + CarryAsks + CarryCensus() + " | mining: trips " + DigTrips + " boards " + DigBoards + " walked " + DigWalks + " hide spawns " + DigSpawns + " Oxide " + DigTox + " Tsolvy " + DigTso + " summoned " + DigSummoned + " recalled " + DigRecalled + " delivered " + DigDelivered + " re-picked " + DigRepicked + " died " + DigDeaths + DigCensus() + " | warps " + Warps + " stuck " + Stuck + " unstuck " + Unstuck + " crossArea " + CrossArea + " fill out/back " + _m.FillOut + "/" + _m.FillBack
                + " | STILL (10 s, <3 tiles) field " + still[3] + "/" + seen[3] + " raid " + still[4] + "/" + seen[4] + " defend " + still[5] + "/" + seen[5]
                + " | utils gen own/on " + uf[0] + "/" + uf[1] + " es carry/on " + uf[2] + "/" + uf[3] + " td own/on " + uf[4] + "/" + uf[5]
                + " | green portals built " + PortalsBuilt + " taken " + GreenTaken
                + " | swing drops gone/hunt/phase/nosl " + SwingDrops[0] + "/" + SwingDrops[1] + "/" + SwingDrops[2] + "/" + SwingDrops[3]
                + " | swings asked " + SwingAsks + " out " + SwingOut + " home " + SwingHome + " failed " + SwingFailed + " | contacts " + ContactsPosted + " wide steers " + WideSteers + " | hunting " + hunting + " holding " + holding + " | defender ladder levels " + hist[0] + "/" + hist[1] + "/" + hist[2] + "/" + hist[3] + "/" + hist[4] + "/" + hist[5] + "/" + hist[6]
                + " |" + sample);
        }

        internal void Poll(int now)
        {
            if (now - _stepAt < 250) return;
            _stepAt = now;
            PostContacts(now);
            AllInStep(now);
            EngCallStep(now);
            SwingStep(now);
            PortalStep(now);
            BuilderTick(now);
            try { PortalTouchStep(now); } catch (Exception ex) { _m.WarnOnce("[MiniTP] portal touch: " + ex.Message); }
            try { QuietStep(now); } catch (Exception ex) { _m.WarnOnce("[MiniTP] quiet base: " + ex.Message); }
            try { DigStep(now); } catch (Exception ex) { _m.WarnOnce("[MiniTP] mining: " + ex.Message); }      // 11-11: the engineer's mining trip + the medic's Tsolvy pickup delay
            try { CarryStep(now); CarryHomeStep(now); } catch (Exception ex) { _m.WarnOnce("[MiniTP] flag carry: " + ex.Message); }      // 11-11: bots CARRY flags (pickup replaces the touch-flip)
            if (now - _censusAt >= 10000) { _censusAt = now; try { Census(now); } catch (Exception ex) { _m.WarnOnce("[MiniTP] census: " + ex.Message); } }
            try { TurretWatch(now); } catch (Exception ex) { _m.WarnOnce("[MiniTP] turret watch: " + ex.Message); }
            try { RefreshCarriers(now); } catch (Exception ex) { _m.WarnOnce("[MiniTP] carriers: " + ex.Message); _carriers.Clear(); }
            foreach (CTFXBot b in _m.LiveBots)
            {
                MiniBot mb = b.Mini;
                if (mb == null || b.IsDead || b.Released) continue;
                try
                {
                    if (mb.Phase == MiniBot.Field) CarrierStep(b, mb, now);          // 11-11: a field walker goes for a carrier in the field too (Bridge3 lies there)
                    if (mb.Phase == MiniBot.Defend || mb.Phase == MiniBot.Raid)
                    {
                        CarrierStep(b, mb, now); MineStep(b, mb, now);
                        if (mb.Hold && !b.HasTarget && b.Still && mb.Role != "MED" && mb.Role != "ENG")
                        {   // watch-yaw check: a holder standing still with nothing to shoot should be facing its watch point (within 35 degrees)
                            int cwx, cwy;
                            if (WatchPoint(b, mb, now, true, out cwx, out cwy)) { FaceN++; if (b.FaceErrorDeg(cwx, cwy) <= 35.0) FaceOk++; }
                        }
                    }
                    if (mb.Phase == MiniBot.Defend || mb.Phase == MiniBot.Raid) UnstickCheck(b, mb, now); else mb.BlockedSince = 0;
                    if (mb.Role == "ENG" && mb.Phase == MiniBot.Defend) EngTick(b, mb, now);
                    if (mb.Role == "MED" && mb.Phase == MiniBot.Defend) MedTick(b, mb, now);
                    switch (mb.Phase)
                    {
                        case MiniBot.Ship:
                            if (mb.Dig > 0 && mb.Dig < 7) break;                            // 11-11: a mining engineer leaves the ship by its own step (CTFXMiniMine)
                            if (now - mb.WaitUntil >= 0)
                            {
                                if (mb.Attack) { if (!TryGreen(b, mb, now)) LeaveShip(b, mb, now); } else GoHome(b, mb, now);
                            }
                            break;
                        case MiniBot.Field:
                            {
                                if (mb.Dig > 0) break;                                           // 11-11: a mining engineer is not walking to the enemy portal
                                // 11-11 (Travis: "they're STANDING ON the portal ... if they ever come in contact with a portal it needs to follow 1:1 what players do"): the wave
                                // stages at the chamber's switch, a few tiles off the portals; a released bot walks onto its portal and goes through the moment it reaches it
                                int dx = b._state.positionX - (mb.PortalX * 16 + 8), dy = b._state.positionY - (mb.PortalY * 16 + 8);
                                bool onPortal = dx * dx + dy * dy <= (mb.WaveFree ? 60 * 60 : 36 * 36);    // released: it walks onto it (a bot stops ~45 px short of a goal); else only a real touch
                                int sx, sy; StageTile(mb, out sx, out sy);
                                int ex = b._state.positionX - (sx * 16 + 8), ey = b._state.positionY - (sy * 16 + 8);
                                bool staged = ex * ex + ey * ey <= 64 * 64;
                                bool stuck = now - mb.PhaseAt > 150000;
                                if (stuck) Stuck++;
                                if (onPortal || stuck) { mb.WaveAt = 0; mb.WaveFree = false; PortalWalkIns++; EnterFoe(b, mb, now); break; }
                                if (mb.WaveFree) break;                                                // released: on its way onto the portal
                                if ((staged || dx * dx + dy * dy <= 64 * 64) && !WaveHold(b, mb, now)) { mb.WaveFree = true; b.ForceGoal(); }   // waits at the staging spot until the wave goes
                                break;
                            }
                    }
                }
                catch (Exception ex) { _m.WarnOnce("[MiniTP] poll: " + ex.GetType().Name + " " + ex.Message); }
            }
        }

        /// <summary>Bots cannot trigger LIO portals / warp fields, and the switch that opens an exit chamber's doors sits outside it: a bot that walked into such a pocket
        /// (the A7 / D7 "exit chamber" with its exit portal) keeps its keys down against a closed door for good. Any Defend / Raid bot that is blocked (keys down, going nowhere,
        /// no target) for UnstickMs is put back by the same scripted hop its phase uses: a defender at its home structure, a raider at the enemy base's landing.</summary>
        void UnstickCheck(CTFXBot b, MiniBot mb, int now)
        {
            if (b.HasTarget || b.Moving || b.Why != "move") { mb.BlockedSince = 0; return; }
            if (mb.BlockedSince == 0) { mb.BlockedSince = now; return; }
            if (now - mb.BlockedSince < UnstickMs) return;
            mb.BlockedSince = 0;
            int x, y;
            bool def = mb.Phase == MiniBot.Defend;
            if (!Pt(def ? HomeArea[mb.Side] : FoeArea[mb.Side], def ? "home" : Landing[mb.Variant & 1], out x, out y)) return;
            int fx = b._state.positionX >> 4, fy = b._state.positionY >> 4;
            Land(b, x, y, def ? 3 : 1, now);
            mb.Hunting = false; mb.Hold = false;
            Unstuck++;
            if (_unstuckLogged < 40)
            {
                _unstuckLogged++;
                _m.MiniLog("[MiniTP] unstuck #" + _unstuckLogged + ": " + (b.Slot != null ? b.Slot.Name : "?") + "/" + mb.Role + " " + (def ? "defend" : "raid") + " blocked " + (UnstickMs / 1000) + " s at " + fx + "," + fy + " -> " + x + "," + y);
            }
        }

        /// <summary>Teleport to a tile of an area (a random open spot within r tiles, else the tile itself) and open the warp grace.</summary>
        bool Land(CTFXBot b, int tx, int ty, int r, int now)
        {
            int lx = tx * 16 + 8, ly = ty * 16 + 8;
            for (int i = 0; i < 24; i++)
            {
                int x = (tx + _m.Rng.Next(-r, r + 1)) * 16 + 8, y = (ty + _m.Rng.Next(-r, r + 1)) * 16 + 8;
                if (_m.BodyFits(x, y)) { lx = x; ly = y; break; }
            }
            b.Teleported(lx, ly, now);
            b.GraceUntil = now + GraceMs;
            Warps++;
            return true;
        }

        void GoHome(CTFXBot b, MiniBot mb, int now)
        {   // the yellow home portal of the dropship: arrives at the team's home structure
            int x, y;
            if (!Pt(HomeArea[mb.Side], "home", out x, out y)) return;
            Land(b, x, y, 2, now);
            mb.Phase = MiniBot.Defend; mb.PhaseAt = now; mb.Hops++;
            MaybeDropSentry(b, mb, now);
        }

        void LeaveShip(CTFXBot b, MiniBot mb, int now)
        {   // an exit portal of the team's ship room: lands in the field
            int e = Exits[mb.Side][_m.Rng.Next(4)];
            int x, y;
            if (!Pt("FIELD", "exit" + e, out x, out y)) return;
            Land(b, x, y, 4, now);
            int px, py;
            string[] ps = EntryPortals[mb.Side];
            if (!Pt("FIELD", ps[mb.Variant & 1], out px, out py)) { GoHome(b, mb, now); return; }
            mb.PortalX = px; mb.PortalY = py;
            mb.Phase = MiniBot.Field; mb.PhaseAt = now; mb.Hops++;
        }

        void EnterFoe(CTFXBot b, MiniBot mb, int now)
        {   // the field portal into the ENEMY base: lands at that base's landA / landB
            int x, y;
            if (!Pt(FoeArea[mb.Side], Landing[mb.Variant & 1], out x, out y)) return;
            Land(b, x, y, 1, now);
            mb.Phase = MiniBot.Raid; mb.PhaseAt = now; mb.Hops++;
            Talk(b, 0.6, 24, "| Attacking Enemy [{c}] |", "At Enemy", "@Nme");
            mb.Stage = mb.Role == "JT" || mb.Role == "INFIL" ? 1 : 0;
            mb.StageUntil = now + 25000 + _m.Rng.Next(35000);
        }

        // ---------------------------------------------------------------- areas (the route book's sealed boxes), tile windows with 2 tiles of margin
        // 0 A7 (Collective base), 1 D7 (Titan base), 2 FIELD, 3 DST (Titan ship room), 4 DSC (Collective ship room); -1 = outside all of them
        internal static int AreaOf(int tx, int ty)
        {
            if (tx >= 956 && tx <= 1033 && ty >= 610 && ty <= 698) return 0;
            if (tx >= 1064 && tx <= 1143 && ty >= 568 && ty <= 651) return 1;
            if (tx >= 1048 && tx <= 1253 && ty >= 673 && ty <= 896) return 2;
            if (tx >= 608 && tx <= 696 && ty >= 496 && ty <= 531) return 3;
            if (tx >= 608 && tx <= 696 && ty >= 544 && ty <= 580) return 4;
            return -1;
        }

        /// <summary>True when the goal is in the bot's own area (a bot outside every area is not judged).</summary>
        internal bool SameArea(int px, int py, int gx, int gy)
        {
            int a = AreaOf(px >> 4, py >> 4), b = AreaOf(gx >> 4, gy >> 4);
            return a < 0 || a == b;                    // a bot inside an area never plans to a tile outside every area (a big-map fallback tile)
        }

        /// <summary>A hurt bot falls back, always inside the area it is in: the enemy base's rear anchor, the home anchor, or its field landing.</summary>
        internal bool Retreat(CTFXBot b, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            MiniBot mb = b.Mini;
            if (mb == null) return false;
            switch (mb.Phase)
            {
                case MiniBot.Raid: return Anchor(FoeArea[mb.Side], "SL", 3, out tx, out ty);       // the far back of the gauntlet
                case MiniBot.Defend: return Anchor(HomeArea[mb.Side], mb.Role == "MED" ? "MED" : "home", 3, out tx, out ty);
                case MiniBot.Field:
                    if (mb.Dig > 0 && DigSpot(mb, out tx, out ty)) return true;      // 11-11: a hurt miner keeps going for the minerals (it asks for the summon from there)
                    return Anchor("FIELD", "exit" + Exits[mb.Side][_m.Rng.Next(4)], 4, out tx, out ty);
            }
            tx = b._state.positionX >> 4; ty = b._state.positionY >> 4;                            // in the ship: nowhere to go
            return true;
        }

        // ================================================================ suppressive fire (Travis 11-08: "do the suppressive fire and wave gathering next")
        // A holder with nothing in sight that knows an enemy is close (within 650 px, out of its sight) and within 350 px of a neighbouring choke point fires bullets down that choke
        // (CTFXBot.SuppressFire), the way CTFXLines.Suppress does for the CTF:X lines. Defenders: the ladder levels beside the one they hold; raiders: the points of the gauntlet.
        internal int Suppressions, SuppressTried;

        internal bool Suppress(CTFXBot b, int now, out int x, out int y)
        {
            x = 0; y = 0;
            MiniBot mb = b.Mini;
            if (mb == null || !mb.Hold || (mb.Phase != MiniBot.Defend && mb.Phase != MiniBot.Raid)) return false;
            if (b.ClassId != CTFXBot.SK_HEAVY && _m.Rng.NextDouble() > 0.35) return false;          // heavies suppress; the others now and then
            int bx = b._state.positionX, by = b._state.positionY, area = AreaOf(bx >> 4, by >> 4);
            if (mb.SuppUntil != 0 && now - mb.SuppUntil < 0 && _m.Los(bx, by, mb.SuppX, mb.SuppY)) { x = mb.SuppX; y = mb.SuppY; return true; }      // the same spot for a couple of seconds: the turn to it takes a moment (rotate keys)
            List<int[]> watch = new List<int[]>();
            if (mb.Phase == MiniBot.Defend)
            {
                List<int[]> lad = _ladder[mb.Side];
                if (lad == null || lad.Count == 0) return false;
                int i = Math.Max(0, Math.Min(lad.Count - 1, mb.Idx));
                for (int d = -1; d <= 1; d++) { int k = i + d; if (k >= 0 && k < lad.Count) watch.Add(new int[] { lad[k][0] * 16 + 8, lad[k][1] * 16 + 8 }); }
            }
            else
            {
                string foe = FoeArea[mb.Side];
                string[] names = { foe == "A7" ? "mouth" : "chanBot", "SL", "BOX" };
                for (int k = 0; k < names.Length; k++) { int px, py; if (Pt(foe, names[k], out px, out py)) watch.Add(new int[] { px * 16 + 8, py * 16 + 8 }); }
            }
            foreach (int[] w in watch)
            {
                double wd = Math.Sqrt((double)(w[0] - bx) * (w[0] - bx) + (double)(w[1] - by) * (w[1] - by));
                if (wd < 120.0 || wd > 900.0 || !_m.Los(bx, by, w[0], w[1])) continue;
                if (UnseenFoeNear(b, area, bx, by, w[0], w[1]))
                {
                    x = w[0] + _m.Rng.Next(-24, 25); y = w[1] + _m.Rng.Next(-24, 25);
                    mb.SuppX = x; mb.SuppY = y; mb.SuppUntil = now + 2500;
                    return true;
                }
            }
            return false;
        }

        /// <summary>An enemy of b in its area within 650 px of it, out of its sight, and within 350 px of the point (wx, wy).</summary>
        bool UnseenFoeNear(CTFXBot b, int area, int bx, int by, int wx, int wy)
        {
            foreach (CTFXBot o in _m.LiveBots)
            {
                if (o._team == b._team || o.IsDead || o.Released) continue;
                int ox = o._state.positionX, oy = o._state.positionY;
                if (AreaOf(ox >> 4, oy >> 4) != area) continue;
                if (Dist2(bx, by, ox, oy) > 650.0 * 650.0 || Dist2(ox, oy, wx, wy) > 350.0 * 350.0 || _m.Los(bx, by, ox, oy)) continue;
                return true;
            }
            foreach (Player p in _m.Arena.PlayersIngame)
            {
                if (p == null || p._team == b._team || p.IsDead || p._state == null) continue;
                int ox = p._state.positionX, oy = p._state.positionY;
                if (AreaOf(ox >> 4, oy >> 4) != area) continue;
                if (Dist2(bx, by, ox, oy) > 650.0 * 650.0 || Dist2(ox, oy, wx, wy) > 350.0 * 350.0 || _m.Los(bx, by, ox, oy)) continue;
                return true;
            }
            return false;
        }

        static double Dist2(int ax, int ay, int bx, int by) { double dx = ax - bx, dy = ay - by; return dx * dx + dy * dy; }

        // ================================================================ waves: attackers stage at the enemy base's entry portal and go in together
        // (the field walkers used to enter one by one and die one by one: "lone raiders die"). A bot that reaches the portal waits until WaveMin of its side are at it (160 px), or
        // until nobody else is coming, or WaveMaxMs have passed; one release sends everybody there in together, a moment apart. A side that is ALL IN never waits.
        const int WaveMin = 3, WaveMaxMs = 20000;
        readonly int[] _waveGo = new int[2];
        internal int WaveHolds, WaveReleases, WaveTimeouts;

        bool WaveHold(CTFXBot b, MiniBot mb, int now)
        {
            if (_allIn == mb.Side) return false;
            if (mb.WaveAt == 0) { mb.WaveAt = now | 1; WaveHolds++; }
            if (mb.WaveGoAt != 0) { if (now - mb.WaveGoAt < 0) return true; mb.WaveGoAt = 0; return false; }            // released: goes in at its own moment
            int px = mb.PortalX * 16 + 8, py = mb.PortalY * 16 + 8;
            if (now - _waveGo[mb.Side] < 0) { mb.WaveGoAt = now + 1 + _m.Rng.Next(900); return true; }
            int here = 0, coming = 0;
            foreach (CTFXBot o in _m.LiveBots)
            {
                MiniBot mo = o.Mini;
                if (mo == null || mo.Side != mb.Side || o.IsDead || o.Released || !mo.Attack || mo.Role == "ENG") continue;
                if (mo.Phase == MiniBot.Field)
                {
                    if (Dist2(o._state.positionX, o._state.positionY, px, py) <= 160.0 * 160.0) here++; else coming++;
                }
                else if (mo.Phase == MiniBot.Ship) coming++;
            }
            bool timeout = now - mb.WaveAt >= WaveMaxMs;
            if (here >= WaveMin || here + coming < WaveMin || timeout)
            {
                _waveGo[mb.Side] = now + 2500;                                                  // everybody at the portal now goes in
                mb.WaveGoAt = now + 1 + _m.Rng.Next(900);
                WaveReleases++; if (timeout && here < WaveMin) WaveTimeouts++;
                return true;
            }
            return true;
        }

        // ================================================================ turrets: raiders hit them, from the safest angle; defenders go for whoever is hitting theirs
        internal int StandoffSpots, StandoffNone, ShootersFlagged, ShooterChases;
        int _standoffLogged;
        readonly Dictionary<Computer, short> _turHp = new Dictionary<Computer, short>();
        readonly Dictionary<object, int> _shooters = new Dictionary<object, int>();      // Player / CTFXBot that hurt a turret of the other team -> until
        int _turWatchAt, _turPruneAt;

        /// <summary>A raider that goes for the enemy's turrets: in the gauntlet, with a gun that works on them, and not the infiltrator, squad leader or medic.</summary>
        internal bool TurretHunter(CTFXBot b)
        {
            MiniBot mb = b.Mini;
            if (mb == null || mb.Phase != MiniBot.Raid) return false;
            return mb.Role == "OHVY" || mb.Role == "OINF" || mb.Role == "JT";
        }

        /// <summary>How near a turret counts against another target (a factor on the squared distance): the offensive heavy takes it as half as far, the others as 30 % nearer.</summary>
        internal double TurretFactor(CTFXBot b) { return b.Mini != null && b.Mini.Role == "OHVY" ? 0.25 : 0.49; }

        /// <summary>Could turret c hit a body of the given weight standing at (x, y)? The engine's own tests (Computer.isValidTarget): FireRadius, weight band, firing arc, line of sight.</summary>
        bool TurretCanHit(Computer c, int x, int y, int weight)
        {
            double dx = x - c._state.positionX, dy = y - c._state.positionY;
            if (Math.Sqrt(dx * dx + dy * dy) > c._type.FireRadius) return false;
            if (weight < c._type.TrackingWeightLow || weight > c._type.TrackingWeightHigh) return false;
            double ang = Helpers.calcDegreesBetweenPoints(x, y, c._state.positionX, c._state.positionY);
            if (c._type.AngleStart > 0 && (short)ang < c._type.AngleStart) return false;
            if (c._type.AngleLength > 0 && c._type.AngleLength < 360 && (short)ang > c._type.AngleStart + c._type.AngleLength) return false;
            if (c._type.ObeyLos != 0 && !_m.Los(c._state.positionX, c._state.positionY, x, y)) return false;
            return true;
        }

        int Exposure(CTFXBot b, int team0, int x, int y)
        {
            int n = 0, w = b._type.Weight;
            foreach (Computer c in _m.Turrets.Live)
            {
                if (c == null || c.IsDead || c._team == null || c._team == b._team || c._team._id == -1 || c._primaryGun == null) continue;
                if (TurretCanHit(c, x, y, w)) n++;
            }
            return n;
        }

        /// <summary>The heavy's firing spot against the nearest enemy turret it can hurt: in sight of the turret, inside its screen (a player must see what he shoots) and its gun's reach, and
        /// out of the reach, arc and sight of as many turrets as it can find (the "safest angle"); where it stands already when that is as safe as any. False = none (the BOX hold).</summary>
        bool TurretStandoff(CTFXBot b, MiniBot mb, int now, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            if (!b.MiniCanHurtTurret) return false;
            int bx = b._state.positionX, by = b._state.positionY, area = AreaOf(bx >> 4, by >> 4);
            // the spot it chose is kept while its turret stands (re-chosen every 6 s)
            if (mb.StandAt != 0 && now - mb.StandAt < 6000)
            {
                bool alive = false;
                foreach (Computer c0 in _m.Turrets.Live) if (c0 != null && !c0.IsDead && c0._id == mb.StandTur) { alive = true; break; }
                if (alive) { tx = mb.StandX; ty = mb.StandY; return true; }
            }
            int reach = Math.Min(900, b.MiniReach);
            if (reach < 250) return NoStand(b, "reach only " + reach + " px");
            // up to four of the nearest enemy turrets with a gun in this area, each looked at from every spot around it; the best (turret, spot) pair wins
            List<Computer> near = new List<Computer>();
            for (int pick = 0; pick < 4; pick++)
            {
                Computer nc = null; double nd = 1100.0 * 1100.0;
                foreach (Computer c in _m.Turrets.Live)
                {
                    if (c == null || c.IsDead || c._team == null || c._team == b._team || c._team._id == -1 || c._primaryGun == null || near.Contains(c)) continue;
                    if (AreaOf(c._state.positionX >> 4, c._state.positionY >> 4) != area) continue;
                    double d = Dist2(bx, by, c._state.positionX, c._state.positionY);
                    if (d < nd) { nd = d; nc = c; }
                }
                if (nc == null) break;
                near.Add(nc);
            }
            if (near.Count == 0) return NoStand(b, "no enemy turret with a gun within 1100 px in this area");
            int curExp = Exposure(b, 0, bx, by);
            Computer tur = null; int cx = 0, cy = 0, bestX = 0, bestY = 0, bestExp = int.MaxValue; double bestScore = double.MaxValue;
            foreach (Computer t in near)
            {
                int tx0 = t._state.positionX, ty0 = t._state.positionY;
                for (int r = 120; r <= Math.Min(reach - 30, 520); r += 60)
                    for (int a2 = 0; a2 < 24; a2++)
                    {
                        double ang = a2 * Math.PI / 12.0;
                        int x = tx0 + (int)Math.Round(Math.Cos(ang) * r), y = ty0 + (int)Math.Round(Math.Sin(ang) * r);
                        if (Math.Abs(x - tx0) > 380 || Math.Abs(y - ty0) > 290) continue;                    // it has to see what it shoots (InView: 420 x 320 px around a player)
                        if (AreaOf(x >> 4, y >> 4) != area || !_m.BodyFits(x, y) || !_m.Los(x, y, tx0, ty0)) continue;
                        double wb = Math.Sqrt(Dist2(bx, by, x, y));
                        if (wb > 1100.0) continue;                                                           // the route planner walks it there
                        int ex = Exposure(b, 0, x, y);
                        double score = ex * 1000.0 + wb * 0.3 - r * 0.8;                                     // fewest turrets on it first, then as far from its target as the screen allows
                        if (score < bestScore) { bestScore = score; bestExp = ex; bestX = x; bestY = y; tur = t; cx = tx0; cy = ty0; }
                    }
            }
            // already as safe as any spot: it stays and shoots from where it stands (it sees the turret and the turret is in reach)
            if (tur != null && _m.Los(bx, by, cx, cy) && Math.Abs(bx - cx) <= 380 && Math.Abs(by - cy) <= 290 && Dist2(bx, by, cx, cy) <= (double)reach * reach && curExp <= bestExp) { bestX = bx; bestY = by; bestExp = curExp; }
            else if (tur != null && bestExp > Math.Max(curExp + 1, 3)) return NoStand(b, "every firing spot is worse than where it stands (exposed to " + curExp + " turret(s) now, best spot " + bestExp + ")");
            if (tur == null) return NoStand(b, "no free spot in sight of any of the " + near.Count + " nearest turrets (reach " + reach + ")");
            if (_standoffLogged < 8)
            {
                _standoffLogged++;
                _m.MiniLog("[MiniTP] standoff #" + _standoffLogged + ": " + (b.Slot != null ? b.Slot.Name : "?") + " vs " + tur._type.Name + " at " + (cx >> 4) + "," + (cy >> 4) + " (fire radius " + tur._type.FireRadius + ", reach " + reach + ", arc " + tur._type.AngleStart + "+" + tur._type.AngleLength
                    + "): spot " + (bestX >> 4) + "," + (bestY >> 4) + ", " + (int)Math.Sqrt(Dist2(bestX, bestY, cx, cy)) + " px off, exposed to " + bestExp + " turret(s) (now " + curExp + "), picked from " + near.Count);
            }
            mb.StandX = bestX >> 4; mb.StandY = bestY >> 4; mb.StandAt = now | 1; mb.StandTur = tur._id;
            StandoffSpots++;
            tx = mb.StandX; ty = mb.StandY;
            return true;
        }

        bool NoStand(CTFXBot b, string why)
        {
            StandoffNone++;
            if (_standoffNoneLogged < 8) { _standoffNoneLogged++; _m.MiniLog("[MiniTP] standoff none #" + _standoffNoneLogged + ": " + (b.Slot != null ? b.Slot.Name : "?") + " " + why); }
            return false;
        }
        int _standoffNoneLogged;

        static int SideOfTeam(Team t) { return t != null && t._name != null && t._name.Contains("Titan") ? 0 : 1; }

        /// <summary>Every 0.4 s: a turret whose health fell was shot by someone; the nearest enemies in its sight (within 1000 px) are flagged as turret shooters for 8 s.</summary>
        void TurretWatch(int now)
        {
            if (now - _turWatchAt < 400) return;
            _turWatchAt = now;
            foreach (Computer c in _m.Turrets.Live)
            {
                if (c == null || c._team == null || c._primaryGun == null) continue;
                short hp = c._state.health, prev;
                if (!_turHp.TryGetValue(c, out prev)) { _turHp[c] = hp; continue; }
                _turHp[c] = hp;
                if (c.IsDead || hp >= prev) continue;
                int cx = c._state.positionX, cy = c._state.positionY;
                object a1 = null, a2 = null; double d1 = 1000.0 * 1000.0, d2 = d1;
                foreach (CTFXBot o in _m.LiveBots)
                {
                    if (o._team == c._team || o.IsDead || o.Released) continue;
                    double d = Dist2(o._state.positionX, o._state.positionY, cx, cy);
                    if (d >= d2 || !_m.Los(o._state.positionX, o._state.positionY, cx, cy)) continue;
                    if (d < d1) { a2 = a1; d2 = d1; a1 = o; d1 = d; } else { a2 = o; d2 = d; }
                }
                foreach (Player p in _m.Arena.PlayersIngame)
                {
                    if (p == null || p._team == c._team || p.IsDead || p._state == null) continue;
                    double d = Dist2(p._state.positionX, p._state.positionY, cx, cy);
                    if (d >= d2 || !_m.Los(p._state.positionX, p._state.positionY, cx, cy)) continue;
                    if (d < d1) { a2 = a1; d2 = d1; a1 = p; d1 = d; } else { a2 = p; d2 = d; }
                }
                object[] hit = { a1, a2 };
                foreach (object a in hit)
                {
                    if (a == null) continue;
                    if (!_shooters.ContainsKey(a) || now - _shooters[a] > 0) ShootersFlagged++;
                    _shooters[a] = now + 8000;
                }
            }
            if (now - _turPruneAt >= 10000)
            {
                _turPruneAt = now;
                List<object> gone = new List<object>();
                foreach (KeyValuePair<object, int> kv in _shooters) if (now - kv.Value >= 0) gone.Add(kv.Key);
                foreach (object k in gone) _shooters.Remove(k);
                List<Computer> dead = new List<Computer>();
                foreach (Computer k in _turHp.Keys) if (k == null || k.IsDead) dead.Add(k);
                foreach (Computer k in dead) _turHp.Remove(k);
            }
        }

        bool IsFlagged(CTFXBot viewer, Player p, CTFXBot o)
        {
            object key = p != null ? (object)p : (object)o;
            if (key == null) return false;
            int until;
            if (!_shooters.TryGetValue(key, out until) || Environment.TickCount - until >= 0) return false;
            Team t = p != null ? p._team : o._team;
            return t != null && t != viewer._team;
        }

        /// <summary>A factor on a candidate's squared distance (CTFXBot.ScanTargets): whoever is shooting our turrets is rated 25 % nearer than he is. A threat nearer than that still wins.</summary>
        internal double ShooterFactor(CTFXBot b, Player p, CTFXBot o) { return IsFlagged(b, p, o) ? 0.6 : 1.0; }

        internal bool IsShooterTarget(CTFXBot b, Player p, CTFXBot o) { return (p != null && CarrierFactor(p) < 1.0) || ((p != null || o != null) && IsFlagged(b, p, o)); }      // a flag carrier is knocked off balance like a turret shooter

        /// <summary>A sweeper goes for the nearest flagged shooter that stands in its base (at most three bodies on one), and fights whatever else it meets on the way.</summary>
        bool ShooterGoal(CTFXBot b, MiniBot mb, int now, int homeArea, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            object best = null; double bd = 1500.0 * 1500.0; int sx = 0, sy = 0;
            foreach (KeyValuePair<object, int> kv in _shooters)
            {
                if (now - kv.Value >= 0) continue;
                Player p = kv.Key as Player; CTFXBot o = kv.Key as CTFXBot;
                int x, y;
                if (p != null) { if (p.IsDead || p._state == null || p._team == null || p._team == b._team) continue; x = p._state.positionX; y = p._state.positionY; }
                else if (o != null) { if (o.IsDead || o.Released || o._team == b._team) continue; x = o._state.positionX; y = o._state.positionY; }
                else continue;
                if (AreaOf(x >> 4, y >> 4) != homeArea) continue;
                double d = Dist2(b._state.positionX, b._state.positionY, x, y);
                if (d < bd) { bd = d; best = kv.Key; sx = x; sy = y; }
            }
            if (best == null) return false;
            int onIt = 0;
            foreach (CTFXBot m in _m.LiveBots) if (m != b && m._team == b._team && !m.IsDead && m.Mini != null && m.Mini.ShooterKey == best) onIt++;
            if (onIt >= 3) return false;
            mb.ShooterKey = best; mb.Hunting = true;
            Around(sx >> 4, sy >> 4, 2, out tx, out ty);
            if (mb.ShooterLast != best) { mb.ShooterLast = best; ShooterChases++; }
            return true;
        }

        // ---------------------------------------------------------------- the odds in MiniTP (Travis 11-08: "start with the human-enemy counting and the help calls")
        /// <summary>A bot that must NOT back off when outnumbered (it still calls for help): the true anchor holding its base, and every body of a side that is ALL IN (they have to reach the flags).</summary>
        internal bool OddsStay(CTFXBot b)
        {
            MiniBot mb = b.Mini;
            if (mb == null) return false;
            if (_allIn == mb.Side && mb.Phase != MiniBot.Ship) return true;
            return IsAnchor(mb) && mb.Phase == MiniBot.Defend;
        }

        /// <summary>May bot o answer bot b's help call: in b's area, out of the ship, and not the engineer / medic (they have their jobs), the true anchor or an all-in body.</summary>
        internal bool RescueOk(CTFXBot o, CTFXBot b)
        {
            MiniBot mo = o.Mini, mbb = b.Mini;
            if (mo == null || mbb == null || mo.Phase == MiniBot.Ship || mo.Role == "ENG" || mo.Role == "MED" || IsAnchor(mo) || _allIn == mo.Side) return false;
            int ao = AreaOf(o._state.positionX >> 4, o._state.positionY >> 4), ab = AreaOf(b._state.positionX >> 4, b._state.positionY >> 4);
            return ao >= 0 && ao == ab;
        }

        /// <summary>The help call, in the players' own macros: the home base says it is under attack, a raider or field walker asks for help with its coordinates.</summary>
        internal void HelpCall(CTFXBot b, int foes)
        {
            MiniBot mb = b.Mini;
            if (mb == null) return;
            if (mb.Phase == MiniBot.Defend) Talk(b, 0.7, 28, "| Defending [{c}] | Status: Under Attack |", "%28Under Attack", "Under Attack    --[{c}]--", "outnumbered at home [{c}], need help");
            else Talk(b, 0.7, 28, "Need help [{c}]", "Outnumbered [{c}] " + foes + " of them", "%28Under Attack [{c}]", "help, " + foes + " on me [{c}]");
        }

        // ---------------------------------------------------------------- CARRIER FIRST (Travis 11-08: "Carrier first target priority is important, the game is capture the flag after all")
        // The flags are script-owned in MiniTP and only HUMANS carry them; the CTF:X carrier rules key on the CTFXFlags tracker, which is off here. So the carriers are read straight from the
        // arena's flag states (fs.carrier). 1. Every bot rates an enemy carrier as far nearer than he is (CarrierFactor, in the target pick: only a threat right on top of the bot outranks him,
        // and a carrier is seen even while the bot is travelling). 2. Defenders and raiders in his AREA break off and go for him (CarrierGoal): the true anchor only inside 650 px, the offensive
        // heavy and the squad leader inside 900 px, the engineer and the medic never. 3. They favour stun / knock-back guns on him (IsShooterTarget). 4. One bot calls "Flag carrier" in team chat.
        // A carrier that leaves the area through a portal leaves the chasers behind (they go back to their jobs).
        sealed class CarrierRec { public Player P; public CTFXBot B; public Arena.FlagState Fs; public int Side;      // B: a bot carrier (P is its row; the row has no position)
            public int X { get { return B != null ? B._state.positionX : P._state.positionX; } } public int Y { get { return B != null ? B._state.positionY : P._state.positionY; } } }
        readonly List<CarrierRec> _carriers = new List<CarrierRec>();
        readonly Dictionary<Player, int> _carrierCalled = new Dictionary<Player, int>();
        internal int FaceN, FaceOk, CarrierSeen, CarrierChases, CarrierCalls, CarrierDropped, CloakOns, CloakOffs, MinesLaid, MineTried, MineBlocked, WatchTurns;
        int _lastCarrierN;

        void RefreshCarriers(int now)
        {
            _carriers.Clear();
            foreach (Arena.FlagState fs in _m.Arena._flags.Values)
            {
                Player c = fs.carrier;
                CTFXBot cb = fs.bActive ? CarrierBot(fs) : null;                                  // 11-11: a bot carrier is chased like a human one
                if (cb != null) { if (cb.IsDead || cb._team == null) continue; CarrierRec rb = new CarrierRec(); rb.P = c; rb.B = cb; rb.Fs = fs; rb.Side = SideOfTeam(cb._team); _carriers.Add(rb); continue; }
                if (!fs.bActive || c == null || c._arena != _m.Arena || c.IsDead || c._state == null || c._team == null || c._team.IsSpec) continue;
                CarrierRec r = new CarrierRec();
                r.P = c; r.Fs = fs; r.Side = SideOfTeam(c._team);
                _carriers.Add(r);
            }
            if (_carriers.Count > _lastCarrierN) CarrierSeen += _carriers.Count - _lastCarrierN;
            if (_carriers.Count < _lastCarrierN) CarrierDropped += _lastCarrierN - _carriers.Count;
            _lastCarrierN = _carriers.Count;
            if (_carrierCalled.Count > 0 && _carriers.Count == 0) _carrierCalled.Clear();
        }

        /// <summary>The factor on a candidate's score: an enemy human carrying a flag counts as about three times nearer (the score is the squared distance, so 0.12).</summary>
        internal double CarrierFactor(Player p)
        {
            for (int i = 0; i < _carriers.Count; i++) if (_carriers[i].P == p) return 0.12;
            return 1.0;
        }

        /// <summary>The carrier this bot should go for: an enemy of its side, alive, in its own area, inside the reach its job allows.</summary>
        CarrierRec CarrierTarget(CTFXBot b, MiniBot mb)
        {
            if (_carriers.Count == 0 || mb.Role == "MED" || mb.Role == "ENG" || mb.Spot >= 0 || (mb.Phase != MiniBot.Defend && mb.Phase != MiniBot.Raid && mb.Phase != MiniBot.Field)) return null;       // (a green portal builder finishes its portal)
            int px = b._state.positionX, py = b._state.positionY;
            int myArea = AreaOf(px >> 4, py >> 4);
            double reach = IsAnchor(mb) ? 650.0 : (mb.Role == "OHVY" || mb.Role == "SL") ? 900.0 : 1900.0;
            CarrierRec best = null; double bd = double.MaxValue;
            foreach (CarrierRec r in _carriers)
            {
                if (r.Side == mb.Side) continue;                                          // our own team's carrier
                int cx = r.X, cy = r.Y;
                if (AreaOf(cx >> 4, cy >> 4) != myArea) continue;
                double dx = cx - px, dy = cy - py, d = dx * dx + dy * dy;
                if (d > reach * reach || d >= bd) continue;
                bd = d; best = r;
            }
            return best;
        }

        /// <summary>The goal of a bot that goes for a carrier: his tile (a raider comes on its own bearing and stops short with a line, a defender simply closes in).</summary>
        bool CarrierGoal(CTFXBot b, MiniBot mb, int now, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            CarrierRec r = CarrierTarget(b, mb);
            if (r == null) { mb.Chasing = null; return false; }
            int cx = r.X >> 4, cy = r.Y >> 4;
            if (mb.Phase == MiniBot.Raid) Flank(b, mb, cx, cy, out tx, out ty); else Around(cx, cy, 1, out tx, out ty);
            mb.Hunting = true; mb.Hold = false;
            if (mb.Chasing != r.P)
            {
                mb.Chasing = r.P; CarrierChases++;
                int last;
                if (!_carrierCalled.TryGetValue(r.P, out last) || now - last > 20000)
                {   // one bot says it (the team chat gap keeps the rest quiet): his tile is the call
                    _carrierCalled[r.P] = now | 1;
                    CarrierCalls++;
                    string at = Coord(cx, cy);
                    Talk(b, 1.0, 26, "Flag carrier [" + at + "]", "%26 Flag carrier " + at + " - kill him", "Carrier [" + at + "] get him");
                }
            }
            return true;
        }

        /// <summary>Per bot, every poll: a bot that should be going for a carrier but is not (or has lost him) gets a fresh goal at once, then again every 2 s while it chases.</summary>
        void CarrierStep(CTFXBot b, MiniBot mb, int now)
        {
            if (_carriers.Count == 0 && mb.Chasing == null) return;
            if (now - mb.CarrierPoll < 0) return;
            mb.CarrierPoll = now + 500;
            CarrierRec r = CarrierTarget(b, mb);
            if (r == null)
            {
                if (mb.Chasing != null) { mb.Chasing = null; mb.Hunting = false; b.ForceGoal(); }
                return;
            }
            if (mb.Chasing != r.P || now - mb.ChaseRefresh >= 0) { mb.ChaseRefresh = now + 2000; b.ForceGoal(); }
        }

        // ---------------------------------------------------------------- WATCH-YAW (Travis 11-08): a holder faces where the enemy will come from
        // CTF:X anchors face the choke's watch point and sweep +-20 units at their own pace (CTFXCommand.HoldYaw); MiniTP holders only glanced randomly +-50 units, so one that stood facing the
        // wrong way had to turn (with the rotate keys) before it could shoot the first man through the choke. Now: a fresh contact in the area first (a mate saw an enemy there), else the
        // next ladder level OUT from where a defender stands, else - for a raider that holds (the heavy, the squad leader, a foothold) - the enemy flag room. Walking sweepers still glance about.
        bool LadderOut(MiniBot mb, int px, int py, out int wx, out int wy)
        {
            wx = 0; wy = 0;
            List<int[]> lad = _ladder[mb.Side];
            if (lad == null || lad.Count == 0) return false;
            int idx = Math.Max(0, Math.Min(lad.Count - 1, mb.Idx));
            for (int k = idx + 1; k < lad.Count; k++)
            {   // the next level out that is a real distance away (a level that sits on the bot gives no direction)
                int lx = lad[k][0] * 16 + 8, ly = lad[k][1] * 16 + 8;
                double dx = lx - px, dy = ly - py;
                if (dx * dx + dy * dy >= 64.0 * 64.0) { wx = lx; wy = ly; return true; }
            }
            return false;
        }

        bool WatchPoint(CTFXBot b, MiniBot mb, int now, bool useContacts, out int wx, out int wy)
        {
            wx = 0; wy = 0;
            int px = b._state.positionX, py = b._state.positionY;
            int area = AreaOf(px >> 4, py >> 4);
            if (useContacts)
            {
                Contact bc = null; double bd = double.MaxValue;
                foreach (Contact c in _contacts[mb.Side])
                {
                    if (now - c.At > 6000 || AreaOf(c.X >> 4, c.Y >> 4) != area) continue;
                    double dx = c.X - px, dy = c.Y - py, d = dx * dx + dy * dy;
                    if (d < 140.0 * 140.0 || d > 1100.0 * 1100.0 || d >= bd) continue;
                    bd = d; bc = c;
                }
                if (bc != null) { wx = bc.X; wy = bc.Y; return true; }
            }
            if (mb.Phase == MiniBot.Defend) return LadderOut(mb, px, py, out wx, out wy);
            int hx, hy;
            if (!Pt(FoeArea[mb.Side], "home", out hx, out hy)) return false;
            wx = hx * 16 + 8; wy = hy * 16 + 8;
            return true;
        }

        /// <summary>CTFXBot.Nav asks for the facing of a bot that is standing at its goal; false = it keeps its random glances.</summary>
        internal bool WatchYaw(CTFXBot b, int now, out float yaw)
        {
            yaw = 0f;
            MiniBot mb = b.Mini;
            if (mb == null || mb.Role == "MED" || mb.Role == "ENG" || mb.Role == "INFIL" || (mb.Phase != MiniBot.Defend && mb.Phase != MiniBot.Raid)) return false;
            bool holder = mb.Hold || IsAnchor(mb) || (mb.Phase == MiniBot.Raid && (mb.Role == "OHVY" || mb.Role == "SL"));
            int wx, wy;
            if (!holder && !FreshContact(b, mb, now)) return false;           // a walking sweeper only turns to a fresh contact; a holder always has somewhere to look
            if (!WatchPoint(b, mb, now, true, out wx, out wy)) return false;
            int period = 3000 + (b.Slot != null && b.Slot.Human != null ? Math.Abs(b.Slot.Human.Seed) % 4000 : 0);
            yaw = b.WatchToward(wx, wy, now, period, 20f);
            WatchTurns++;
            return true;
        }

        bool FreshContact(CTFXBot b, MiniBot mb, int now)
        {
            int px = b._state.positionX, py = b._state.positionY, area = AreaOf(px >> 4, py >> 4);
            foreach (Contact c in _contacts[mb.Side])
            {
                if (now - c.At > 6000 || AreaOf(c.X >> 4, c.Y >> 4) != area) continue;
                double dx = c.X - px, dy = c.Y - py, d = dx * dx + dy * dy;
                if (d >= 140.0 * 140.0 && d <= 1100.0 * 1100.0) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- MINES (Travis 11-08): the builds carry them (bullet / plasma / AP / EMP / grapeshot); CTFXShop keeps them at the end of the kit
        // A holder with a mine in its kit, standing still with nothing in sight, lays one toward the way the enemy comes (a defender: the next ladder level out; a raider that holds: back toward its
        // own landing, so whoever chases it walks onto it) and keeps two (a raider) or three (a defender) lying; one that went off is replaced, within a budget per life. The item's live
        // caps (CTFXShots.MayLay) apply, and a mine whose damage mode would also hurt the layer's own team is never laid (CTFXBot.MineSafe). The engineer and medic do not lay.
        void MineStep(CTFXBot b, MiniBot mb, int now)
        {
            if (now - mb.MineAt < 0) return;
            if (mb.Role == "MED" || mb.Role == "ENG" || mb.Role == "INFIL" || b.InDropship || b.HasTarget) return;
            bool def = mb.Phase == MiniBot.Defend;
            if (!def && mb.Phase != MiniBot.Raid) return;
            if (!(mb.Hold || IsAnchor(mb))) return;                            // only a bot standing at a post lays
            if (!b.MiniHasMine) return;
            int cap = def ? 3 : 2;
            if (mb.MinesLaid >= cap + (def ? 3 : 1) || b.MiniLiveMines >= cap) { mb.MineAt = now + 4000; return; }
            if (!b.Still || b.RecentlyHit(now, 6000)) { mb.MineAt = now + 700; return; }
            int px = b._state.positionX, py = b._state.positionY, area = AreaOf(px >> 4, py >> 4), fx, fy;
            if (NearestFoe(b, area, def ? 900.0 : 500.0, out fx, out fy)) { mb.MineAt = now + 1500; MineBlocked++; return; }      // an enemy near: that is a fight, not a time to lay
            int wx = 0, wy = 0;
            if (def) { if (!LadderOut(mb, px, py, out wx, out wy)) { mb.MineAt = now + 5000; return; } }
            else
            {
                int lx, ly;
                if (!Pt(FoeArea[mb.Side], Landing[mb.Variant & 1], out lx, out ly)) { mb.MineAt = now + 5000; return; }
                wx = lx * 16 + 8; wy = ly * 16 + 8;
            }
            float yaw = CTFXBot.YawToPublic(wx - px, wy - py);
            MineTried++;
            if (b.MiniLayMine(now, yaw))
            {
                mb.MinesLaid++; MinesLaid++; mb.MineFails = 0;
                mb.MineAt = now + 7000 + _m.Rng.Next(9000);
            }
            else
            {   // turning to it, or refused: a few quick retries, then a long rest
                mb.MineFails++;
                mb.MineAt = now + (mb.MineFails >= 10 ? 25000 : 500);
                if (mb.MineFails >= 10) mb.MineFails = 0;
            }
        }

        // ---------------------------------------------------------------- the armour / generator / cell a build buys (Travis 11-08): logged once per build the first time a life wears it
        readonly HashSet<string> _utilKits = new HashSet<string>();
        int _utilLogged;
        internal void NoteBuildUtils(CTFXBot b, ItemInfo.UtilityItem armor, ItemInfo.UtilityItem gen, ItemInfo.UtilityItem cell)
        {
            if (_utilLogged >= 30 || b.Slot == null || b.Slot.BuildName == null || !_utilKits.Add(b.Slot.BuildName)) return;
            _utilLogged++;
            CTFXShop.Build sb = CTFXShop.ByName(b.Slot.BuildName);
            if (sb == null) return;
            StringBuilder mines = new StringBuilder();
            foreach (int id in sb.Mines)
            {
                ItemInfo mi = AssetManager.Manager.getItemByID(id);
                int cnt; sb.Ammo.TryGetValue(id, out cnt);
                ItemInfo.Projectile mp = mi as ItemInfo.Projectile;
                mines.Append(mines.Length > 0 ? ", " : "").Append(mi != null ? mi.name : "#" + id).Append(" x").Append(cnt).Append(mp != null ? " (damage mode " + mp.damageMode + (mp.damageMode == 2 ? "" : " - never laid") + ")" : "");
            }
            _m.MiniLog("[MiniTP] build " + sb.Name + " (" + sb.ClassName + "): armour " + UName(sb.ArmorId) + ", generator " + UName(sb.GenId) + ", cell " + UName(sb.CellId) + ", cloak " + UName(sb.CloakId) + ", mines [" + mines + "]");
        }

        static string UName(int id)
        {
            if (id == 0) return "-";
            ItemInfo it = AssetManager.Manager.getItemByID(id);
            return it != null ? it.name.Trim() : "#" + id;
        }

        /// <summary>The up-close finishers: the rifle grenades (Maklov RG 2, Titan Arms RG 2mv, Kuchler RG 249) and the Micro Missile Launcher.</summary>
        internal bool IsFinisher(int id) { return id == 1106 || id == 1107 || id == 1082 || id == 3009; }

        // ---------------------------------------------------------------- goals (CTFXBot.ChooseGoal calls this first)
        internal bool Goal(CTFXBot b, int now, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            MiniBot mb = b.Mini;
            if (mb == null) return false;
            if (mb.Dig > 0 && DigGoal(b, mb, now, out tx, out ty)) return true;                  // 11-11: an engineer on a mining trip
            if (mb.Phase != MiniBot.Ship && CarryGoal(b, mb, now, out tx, out ty)) return true;      // 11-11: a flag carrier takes it home (summon / flag spot)
            switch (mb.Phase)
            {
                case MiniBot.Ship:
                    tx = b._state.positionX >> 4; ty = b._state.positionY >> 4;      // idle where it stands
                    return true;
                case MiniBot.Field:
                    if (CarrierGoal(b, mb, now, out tx, out ty)) return true;           // 11-11: an enemy carrier in the field first
                    if (mb.WaveFree) { tx = mb.PortalX; ty = mb.PortalY; } else StageTile(mb, out tx, out ty);    // 11-11: wait off the portal, step onto it when released
                    return true;
                case MiniBot.Defend:
                    if (DefendGoal(b, mb, now, out tx, out ty)) return true;
                    break;
                case MiniBot.Raid:
                    if (RaidGoal(b, mb, now, out tx, out ty)) return true;
                    break;
            }
            // a missing anchor must never hand the bot to the CTF:X goal logic (it picks big-map tiles): hold where it stands
            tx = b._state.positionX >> 4; ty = b._state.positionY >> 4;
            return true;
        }

        /// <summary>A random open tile within r of (x, y).</summary>
        void Around(int x, int y, int r, out int tx, out int ty)
        {
            tx = x; ty = y;
            for (int i = 0; i < 10; i++)
            {
                int ax = x + _m.Rng.Next(-r, r + 1), ay = y + _m.Rng.Next(-r, r + 1);
                if (_m.BodyFits(ax * 16 + 8, ay * 16 + 8)) { tx = ax; ty = ay; return; }
            }
        }

        bool Anchor(string area, string name, int r, out int tx, out int ty)
        {
            int x, y;
            tx = 0; ty = 0;
            if (!Pt(area, name, out x, out y)) return false;
            Around(x, y, r, out tx, out ty);
            return true;
        }

        // ---------------------------------------------------------------- fluid movement (Travis 11-07: "too linear in their movement, stilted. A little more
        // fluid, still mostly the most direct path but sometimes wider sweep paths if they are about to meet enemies defending a choke they have been
        // alerted to by the first person or bot that rounds the corner and reveals the fog of war")
        //  1. LOOK-AHEAD: instead of aiming at the next waypoint (a straight leg and a sharp turn at every node), a bot aims at a CARROT point further
        //     along its own route (about 100-150 px ahead, the farthest one it can walk to in a straight line with the body clear), so corners round off.
        //  2. CONTACTS: a bot that has an enemy in sight posts where he is for its team (the first one round the corner "reveals the fog of war").
        //  3. WIDE APPROACH: a teammate in the same area whose way leads toward a fresh contact it cannot see yet swings its carrot sideways (3-7 tiles,
        //     eased in and out, never into a wall) so the group arrives from a spread of angles instead of down one line. Not every bot does it.
        sealed class Contact { public int X, Y, At; }
        readonly List<Contact>[] _contacts = { new List<Contact>(), new List<Contact>() };      // by side
        internal int ContactsPosted, WideSteers;

        void PostContact(int side, int fx, int fy, int now)
        {
            List<Contact> l = _contacts[side];
            foreach (Contact c in l)
                if (Math.Abs(c.X - fx) + Math.Abs(c.Y - fy) < 200) { c.X = fx; c.Y = fy; c.At = now; return; }
            if (l.Count >= 8) l.RemoveAt(0);
            Contact n = new Contact(); n.X = fx; n.Y = fy; n.At = now;
            l.Add(n);
            ContactsPosted++;
        }

        void PostContacts(int now)
        {
            foreach (CTFXBot b in _m.LiveBots)
            {
                MiniBot mb = b.Mini;
                if (mb == null || b.IsDead || b.Released || !b.HasTarget) continue;
                if (mb.Phase != MiniBot.Raid && mb.Phase != MiniBot.Defend && mb.Phase != MiniBot.Field) continue;
                int area = AreaOf(b._state.positionX >> 4, b._state.positionY >> 4);
                int fx, fy;
                if (NearestFoe(b, area, 520.0, out fx, out fy)) PostContact(mb.Side, fx, fy, now);
            }
        }

        /// <summary>How far to swing the carrot sideways right now (px, signed; eased so the turn is never abrupt).</summary>
        double WideOffset(CTFXBot b, MiniBot mb, int now, int px, int py, int tx, int ty)
        {
            double target = 0.0;
            if (mb.WideLikely && !b.HasTarget)
            {
                List<Contact> l = _contacts[mb.Side];
                for (int i = l.Count - 1; i >= 0; i--)
                {
                    Contact c = l[i];
                    if (now - c.At > 14000) { l.RemoveAt(i); continue; }
                    double dx = c.X - px, dy = c.Y - py, d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < 260.0 || d > 1500.0) continue;                                   // already on top of it / too far to matter
                    if (AreaOf(c.X >> 4, c.Y >> 4) != AreaOf(px >> 4, py >> 4)) continue;
                    double a1 = Math.Atan2(ty - py, tx - px), a2 = Math.Atan2(dy, dx), diff = Math.Abs(a1 - a2);
                    if (diff > Math.PI) diff = 2.0 * Math.PI - diff;
                    if (diff > 0.95) continue;                                                // its way does not lead at the contact
                    double fade = d < 600.0 ? (d - 260.0) / 340.0 : (d > 1100.0 ? (1500.0 - d) / 400.0 : 1.0);
                    target = mb.WideSign * (24.0 + 24.0 * mb.Smooth) * Math.Max(0.0, Math.Min(1.0, fade));      // 1.5-3 tiles: the halls are narrow
                    break;
                }
            }
            int dtw = Math.Max(1, Math.Min(250, now - mb.WideAt)); mb.WideAt = now;       // 11-08: by time (about 0.45 s to cover most of a swing); the old fixed 0.14 per poll took about a tenth of a second at the arena's 15 ms polls
            mb.WideNow += (target - mb.WideNow) * (1.0 - Math.Exp(-dtw / 450.0));
            if (Math.Abs(mb.WideNow) < 1.0) mb.WideNow = 0.0;
            return mb.WideNow;
        }

        /// <summary>A straight walk with the body clear: the centre line and two lines <paramref name="half"/> px either side must all be open (WalkLine only checks 6 px,
        /// which let a rounded corner clip a wall: Travis 11-07 run, stuck events 10 -> 211).</summary>
        bool WalkWide(int x0, int y0, int x1, int y1, int half)
        {
            double dx = x1 - x0, dy = y1 - y0, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1.0) return !_m.BlockedPx(x0, y0);
            double nx = -dy / len * half, ny = dx / len * half;
            int steps = (int)(len / 6.0) + 1;
            for (int s = 0; s <= steps; s++)
            {
                double t = (double)s / steps, x = x0 + dx * t, y = y0 + dy * t;
                if (_m.BlockedPx((int)x, (int)y) || _m.BlockedPx((int)(x + nx), (int)(y + ny)) || _m.BlockedPx((int)(x - nx), (int)(y - ny))) return false;
            }
            return true;
        }

        /// <summary>CTFXBot.RouteMove calls this with the lane-adjusted waypoint it would steer at. Returns how many waypoints the bot has already passed (it advances
        /// that many); the carrot replaces tx, ty when one is clear.</summary>
        internal int Steer(CTFXBot b, int now, int px, int py, int gx, int gy, ref int tx, ref int ty)
        {
            MiniBot mb = b.Mini;
            CTFXRouteBook rb = _m.Routes;
            List<int> rp = b.MiniRoute;
            int i = b.MiniRouteIdx;
            if (mb == null || rb == null || rp == null || i >= rp.Count) return 0;
            // waypoints it is already abreast of: within ~6 tiles of one with a clear straight walk to the next (never around a door)
            int adv = 0;
            while (i + adv + 1 < rp.Count)
            {
                int cur = rp[i + adv], nxt = rp[i + adv + 1];
                if (rb.DoorBetween(cur, nxt) != 0 || (i + adv > 0 && rb.DoorBetween(rp[i + adv - 1], cur) != 0)) break;
                double dcx = rb.X[cur] - px, dcy = rb.Y[cur] - py;
                if (dcx * dcx + dcy * dcy > 96.0 * 96.0) break;
                if (!WalkWide(px, py, rb.X[nxt], rb.Y[nxt], 10)) break;
                adv++;
            }
            int first = i + adv;
            // the polyline ahead: the next waypoint, then raw waypoints (stop at a door: it is crossed exactly), then the goal
            List<int[]> v = new List<int[]>(6);
            if (adv > 0) v.Add(new int[] { rb.X[rp[first]], rb.Y[rp[first]] }); else v.Add(new int[] { tx, ty });
            bool open = true;
            for (int k = first + 1; k < rp.Count && v.Count < 5; k++)
            {
                if (rb.DoorBetween(rp[k - 1], rp[k]) != 0) { open = false; break; }
                v.Add(new int[] { rb.X[rp[k]], rb.Y[rp[k]] });
            }
            if (open && v.Count < 5 && !(gx == v[v.Count - 1][0] && gy == v[v.Count - 1][1])) v.Add(new int[] { gx, gy });
            double carrot = 80.0 + 40.0 * mb.Smooth;
            double total = 0.0;
            double[] seg = new double[v.Count];
            double lx = px, ly = py;
            for (int k = 0; k < v.Count; k++) { double dx = v[k][0] - lx, dy = v[k][1] - ly; seg[k] = Math.Sqrt(dx * dx + dy * dy); total += seg[k]; lx = v[k][0]; ly = v[k][1]; }
            double reach = Math.Min(carrot, total);
            int bx = v[0][0], by = v[0][1];
            bool got = false;
            if (reach >= 24.0)
                for (int step = 0; step < 4 && !got; step++)
                {
                    double want = reach * (1.0 - 0.25 * step);
                    if (want < 24.0) break;
                    double run = 0.0, ax = px, ay = py, cx = bx, cy = by;
                    for (int k = 0; k < v.Count; k++)
                    {
                        if (run + seg[k] >= want || k == v.Count - 1)
                        {
                            double t = seg[k] > 0 ? Math.Max(0.0, Math.Min(1.0, (want - run) / seg[k])) : 1.0;
                            cx = ax + (v[k][0] - ax) * t; cy = ay + (v[k][1] - ay) * t;
                            break;
                        }
                        run += seg[k]; ax = v[k][0]; ay = v[k][1];
                    }
                    if (_m.BodyFits((int)cx, (int)cy) && WalkWide(px, py, (int)cx, (int)cy, 10)) { bx = (int)cx; by = (int)cy; got = true; }
                }
            if (!got)
            {
                if (adv > 0) { tx = v[0][0]; ty = v[0][1]; }       // no carrot: just the next waypoint
                return adv;
            }
            // the wide approach: swing the carrot a little to one side when a contact lies ahead (halve it, then drop it, where a wall is in the way)
            double w = WideOffset(b, mb, now, px, py, bx, by);
            if (Math.Abs(w) >= 1.0)
            {
                double dx = bx - px, dy = by - py, dl = Math.Sqrt(dx * dx + dy * dy);
                if (dl > 8.0)
                {
                    double nx = -dy / dl, ny = dx / dl;
                    for (int k = 0; k < 3; k++)
                    {
                        double f = w * (1.0 - 0.5 * k);
                        int sx = (int)(bx + nx * f), sy = (int)(by + ny * f);
                        if (_m.BodyFits(sx, sy) && WalkWide(px, py, sx, sy, 12) && WalkWide(sx, sy, bx, by, 10)) { bx = sx; by = sy; WideSteers++; break; }
                        if (k == 2) { mb.WideSign = -mb.WideSign; mb.WideNow *= 0.5; }      // blocked this side: try the other next time
                    }
                }
            }
            tx = bx; ty = by;
            return adv;
        }

        // ---------------------------------------------------------------- swings (phase 2; spec 3: "when a team's home base is quiet its defenders SWING to the
        // enemy base by asking for an XXXXX in team chat, one X per bot that wants to go; the attack group's SL answers by summoning them, one summon per X up
        // to its charges and energy. The reverse also holds: a base under real pressure pulls swingers home, the medic answers S / SSS")
        // Both sides use the Squads primitives (SL Summon: 3 charges, 100 energy, anti-warp at both ends; Medic Teleport Summoner: 150 energy, 20 s), so the
        // summons cost what they cost a player. The summoned bot lands beside its summoner and CTFXMini.Warped puts it in the right phase.
        sealed class SwingAsk { public CTFXBot Bot; public int Due; public bool Home; }
        readonly List<SwingAsk> _asks = new List<SwingAsk>();
        readonly int[] _swingNext = new int[2], _homeCallNext = new int[2];
        int _swingAt;
        internal int SwingAsks, SwingOut, SwingHome, SwingFailed;

        void SwingStep(int now)
        {
            if (now - _swingAt < 2000) return;
            _swingAt = now;
            // 1. answers that are due
            for (int i = 0; i < _asks.Count; )
            {
                SwingAsk a = _asks[i];
                if (now - a.Due < 0) { i++; continue; }
                _asks.RemoveAt(i);
                CTFXBot b = a.Bot;
                if (b == null || b.IsDead || b.Released || b.Mini == null) { SwingDrops[0]++; continue; }
                if (b.Mini.Hunting) { SwingDrops[1]++; continue; }
                if (a.Home ? b.Mini.Phase != MiniBot.Raid : b.Mini.Phase != MiniBot.Defend) { SwingDrops[2]++; continue; }     // it has moved on (died, was summoned already)
                try { if (a.Home) AnswerHome(b, now); else AnswerOut(b, now); }
                catch (Exception ex) { _m.WarnOnce("[MiniTP] swing answer: " + ex.Message); }
            }
            // 2. new asks
            for (int side = 0; side < 2; side++)
            {
                try { WantOut(side, now); WantHome(side, now); }
                catch (Exception ex) { _m.WarnOnce("[MiniTP] swing ask: " + ex.Message); }
            }
        }

        CTFXBot Summoner(int side, bool medic, int now)
        {
            foreach (CTFXBot o in _m.LiveBots)
            {
                MiniBot m = o.Mini;
                if (m == null || m.Side != side || o.IsDead || o.Released) continue;
                if (medic) { if (m.Role == "MED" && m.Phase == MiniBot.Defend && now - o.SummonNextAt >= 0) return o; }
                else if (m.Role == "SL" && m.Phase == MiniBot.Raid && _m.Squads.CanSummon(o, now)) return o;
            }
            return null;
        }

        int Foes(int homeAreaIdx)
        {
            int n = 0;
            foreach (CTFXBot o in _m.LiveBots)
            {
                if (o.Mini == null || o.IsDead || o.Released) continue;
                if (AreaOf(o._state.positionX >> 4, o._state.positionY >> 4) == homeAreaIdx && o.Mini.Side != (homeAreaIdx == 1 ? 0 : 1)) n++;     // an enemy of the base's owner
            }
            foreach (Player p in _m.Arena.PlayersIngame)
                if (!p.IsDead && p._state != null && AreaOf(p._state.positionX >> 4, p._state.positionY >> 4) == homeAreaIdx) n++;                  // (a human of either team counts; close enough)
            return n;
        }

        /// <summary>The base is quiet, a Squad Leader is up in the enemy base with a charge: spare defenders ask for the swing.</summary>
        void WantOut(int side, int now)
        {
            if (now - _swingNext[side] < 0) return;
            if (_intruderAt[side] != 0 && now - _intruderAt[side] < 25000) return;                       // the base was attacked a moment ago
            CTFXBot sl = Summoner(side, false, now);
            if (sl == null) return;
            List<CTFXBot> spare = new List<CTFXBot>();
            int home = 0;
            foreach (CTFXBot o in _m.LiveBots)
            {
                MiniBot m = o.Mini;
                if (m == null || m.Side != side || o.IsDead || o.Released || m.Phase != MiniBot.Defend) continue;
                home++;
                if (m.Role == "DINF" && !m.Hunting && !IsPending(o)) spare.Add(o);
            }
            int keep = 3;                                                                                // the anchor, the Medic and the Engineer stay
            int k = Math.Min(Math.Min(sl.SummonCharges <= 0 ? 1 : sl.SummonCharges, spare.Count), home - keep);
            if (k <= 0) return;
            k = Math.Min(k, 3);
            _swingNext[side] = now + 35000 + _m.Rng.Next(30000);
            // the asker types one X per bot that wants to go (CTF:X chat macro %21 "XXXXX")
            CTFXBot caller = spare[_m.Rng.Next(spare.Count)];
            string xs = new string('X', Math.Max(2, k));
            _m.Chat.Say(caller, xs, 1.0, true, 21);
            for (int i = 0; i < k; i++)
            {
                SwingAsk a = new SwingAsk(); a.Bot = spare[i]; a.Home = false; a.Due = now + 1500 + 1800 * i + _m.Rng.Next(1200);
                _asks.Add(a);
            }
            SwingAsks += k;
        }

        /// <summary>Real pressure on the home base (three or more enemies inside): swingers are asked home (SSSSS), the home Medic answers.</summary>
        void WantHome(int side, int now)
        {
            if (now - _homeCallNext[side] < 0) return;
            int homeArea = HomeAreaIdx(side);
            if (Foes(homeArea) < 3) return;
            CTFXBot medic = Summoner(side, true, now);
            if (medic == null) return;
            List<CTFXBot> swingers = new List<CTFXBot>();
            CTFXBot caller = null;
            foreach (CTFXBot o in _m.LiveBots)
            {
                MiniBot m = o.Mini;
                if (m == null || m.Side != side || o.IsDead || o.Released) continue;
                if (m.Phase == MiniBot.Raid && !m.Attack && !IsPending(o)) swingers.Add(o);
                if (m.Phase == MiniBot.Defend && m.Role != "MED") caller = o;
            }
            if (swingers.Count == 0 || caller == null) return;
            _homeCallNext[side] = now + 20000 + _m.Rng.Next(10000);
            int k = Math.Min(swingers.Count, 2);
            _m.Chat.Say(caller, new string('S', Math.Max(3, k + 2)), 1.0, true, 27);
            for (int i = 0; i < k; i++)
            {
                SwingAsk a = new SwingAsk(); a.Bot = swingers[i]; a.Home = true; a.Due = now + 1200 + 1500 * i + _m.Rng.Next(900);
                _asks.Add(a);
            }
            SwingAsks += k;
        }

        bool IsPending(CTFXBot b) { foreach (SwingAsk a in _asks) if (a.Bot == b) return true; return false; }

        int _whyLogged;

        /// <summary>Why a bot-to-bot summon was refused (the same checks as CTFXSquads.Summon, without spending anything).</summary>
        string SummonWhy(CTFXBot leader, CTFXBot b, int now)
        {
            if (b.IsDead || leader.IsDead) return "dead";
            if (b.Stunned(now)) return "target stunned";
            string a = _m.AntiWarpedAt(leader._state.positionX, leader._state.positionY, leader._team, leader);
            if (a != null) return "summoner anti-warped by " + a;
            a = _m.AntiWarpedAt(b._state.positionX, b._state.positionY, b._team, b);
            if (a != null) return "target anti-warped by " + a;
            return "energy (SL has " + (int)leader.EnergyNow + ") or charges " + leader.SummonCharges;
        }

        void AnswerOut(CTFXBot b, int now)
        {
            int side = b.Mini.Side;
            CTFXBot sl = Summoner(side, false, now);
            string who = _m.RowName(b.Slot);
            if (sl == null) { SwingDrops[3]++; return; }                                                  // nobody to answer
            if (_m.Squads.Summon(sl, b, now))
            {
                SwingOut++;
                _m.Chat.Say(sl, "Acknowledged --[" + who + "]--", 0.85, true, 22);
            }
            else
            {
                SwingFailed++;
                string why0 = SummonWhy(sl, b, now);
                if (why0.StartsWith("summoner anti-warped"))
                {   // a disruptor holds the SL (the spec: it is TD-able): the players' macro, then out of its range, then the retry
                    _m.Chat.Say(sl, "I am TD'd but safe (please wait) --[" + who + "]--", 0.9, true, 22);
                    RelocateFromTd(sl, now);
                }
                if (_whyLogged < 12) { _whyLogged++; _m.MiniLog("[MiniTP] swing summon refused #" + _whyLogged + ": SL at " + (sl._state.positionX >> 4) + "," + (sl._state.positionY >> 4) + " -> " + who + " at " + (b._state.positionX >> 4) + "," + (b._state.positionY >> 4) + ": " + SummonWhy(sl, b, now)); }
                _m.Chat.Say(sl, _m.Rng.Next(2) == 0 ? "Loading, summon in queue --[" + who + "]--" : "Loading...", 0.7, true, 22);
                SwingAsk a = new SwingAsk(); a.Bot = b; a.Home = false; a.Due = now + 6000;             // one retry
                if (!_retried.Contains(b._id)) { _retried.Add(b._id); _asks.Add(a); }
            }
        }

        /// <summary>Walk the SL to the anchor farthest from the disruptor that holds it (at least ~540 px away when one exists).</summary>
        void RelocateFromTd(CTFXBot sl, int now)
        {
            MiniBot mb = sl.Mini;
            if (mb == null) return;
            int sx, sy;
            if (!_m.AntiWarpSource(sl._state.positionX, sl._state.positionY, sl._team, out sx, out sy, sl)) return;
            string foe = FoeArea[mb.Side];
            string[] names = { "SL", "TB", "gaunt", "DHVY", "REP1", "REP2", "DINF1", "DINF2", "ENG", "MED" };
            double best = -1.0; int bx = 0, by = 0;
            foreach (string n in names)
            {
                int tx, ty;
                if (!Pt(foe, n, out tx, out ty)) continue;
                double d = Math.Sqrt(Math.Pow(tx * 16 + 8 - sx, 2) + Math.Pow(ty * 16 + 8 - sy, 2));
                if (d > best) { best = d; bx = tx; by = ty; }
            }
            if (best < 0.0) return;
            Around(bx, by, 2, out mb.RelocX, out mb.RelocY);
            mb.RelocUntil = now + 9000;
        }

        void AnswerHome(CTFXBot b, int now)
        {
            CTFXBot medic = Summoner(b.Mini.Side, true, now);
            string who = _m.RowName(b.Slot);
            if (medic == null) return;
            if (_m.Squads.MedicSummonBot(medic, b, now))
            {
                SwingHome++;
                _m.Chat.Say(medic, "Acknowledged --[" + who + "]--", 0.85, true, 22);
            }
            else SwingFailed++;
        }

        readonly HashSet<ushort> _retried = new HashSet<ushort>();

        // ---------------------------------------------------------------- the choke ladder (Travis 11-07: "defenders are not sweeping their path and
        // then pushing out to defend the base at greater choke stages and levels, they shouldn't remain afk in their base")
        // Each base has a LADDER: points along the enemy's real approach path (the route book's landing -> flag route, walked backwards), level 0
        // = the flag room, higher levels = further out toward the landing. Defenders SWEEP up and down it (short pauses) and PUSH OUT a level
        // every so often while the base is quiet, falling back a level now and then; any enemy inside the base is hunted down.
        readonly List<int[]>[] _ladder = new List<int[]>[2];          // by side (the side's HOME base): tiles, level 0 first
        readonly int[] _intruderAt = new int[2];                       // by side: the last time an enemy was seen inside that side's base
        static readonly double[] LadderAt = { 0.0, 0.07, 0.16, 0.27, 0.40, 0.55, 0.72 };      // fractions of the path length, from the flag room outward

        internal int LadderLen(int side) { return _ladder[side] == null ? 0 : _ladder[side].Count; }

        internal void BuildLadders()
        {
            for (int side = 0; side < 2; side++)
            {
                _ladder[side] = null;
                try
                {
                    int[] r = null;
                    string area = HomeArea[side];
                    if (_m.Routes == null || (!_m.Routes.Routes.TryGetValue(area + ":landA>flag1", out r) && !_m.Routes.Routes.TryGetValue(area + ":landA>home", out r)) || r == null || r.Length < 2) continue;
                    // polyline in tiles, flag room first
                    List<double[]> pl = new List<double[]>();
                    for (int i = r.Length - 1; i >= 0; i--) pl.Add(new double[] { _m.Routes.X[r[i]] / 16.0, _m.Routes.Y[r[i]] / 16.0 });
                    double total = 0.0;
                    for (int i = 1; i < pl.Count; i++) total += Math.Sqrt(Math.Pow(pl[i][0] - pl[i - 1][0], 2) + Math.Pow(pl[i][1] - pl[i - 1][1], 2));
                    if (total < 10.0) continue;
                    List<int[]> lad = new List<int[]>();
                    foreach (double f in LadderAt)
                    {
                        double want = f * total, run = 0.0;
                        double px = pl[0][0], py = pl[0][1];
                        for (int i = 1; i < pl.Count; i++)
                        {
                            double seg = Math.Sqrt(Math.Pow(pl[i][0] - pl[i - 1][0], 2) + Math.Pow(pl[i][1] - pl[i - 1][1], 2));
                            if (run + seg >= want || i == pl.Count - 1)
                            {
                                double t = seg > 0 ? Math.Max(0.0, Math.Min(1.0, (want - run) / seg)) : 0.0;
                                px = pl[i - 1][0] + (pl[i][0] - pl[i - 1][0]) * t; py = pl[i - 1][1] + (pl[i][1] - pl[i - 1][1]) * t;
                                break;
                            }
                            run += seg;
                        }
                        int tx, ty;
                        Around((int)Math.Round(px), (int)Math.Round(py), 1, out tx, out ty);
                        lad.Add(new int[] { tx, ty });
                    }
                    _ladder[side] = lad;
                }
                catch (Exception ex) { _m.WarnOnce("[MiniTP] ladder: " + ex.Message); }
            }
            _m.MiniLog("[MiniTP] choke ladders: " + LadderText(0) + " | " + LadderText(1));
        }

        string LadderText(int side)
        {
            if (_ladder[side] == null) return HomeArea[side] + " none";
            StringBuilder sb = new StringBuilder(HomeArea[side] + ":");
            foreach (int[] p in _ladder[side]) sb.Append(' ').Append(p[0]).Append(',').Append(p[1]);
            return sb.ToString();
        }

        // ---------------------------------------------------------------- foes in an area
        /// <summary>The nearest living enemy (bot or human) inside area <paramref name="area"/> within <paramref name="maxPx"/> of the bot.</summary>
        bool NearestFoe(CTFXBot b, int area, double maxPx, out int fx, out int fy)
        {
            fx = 0; fy = 0;
            double best = maxPx * maxPx;
            bool found = false;
            foreach (CTFXBot o in _m.LiveBots)
            {
                if (o._team == b._team || o.IsDead || o.Released) continue;
                if (AreaOf(o._state.positionX >> 4, o._state.positionY >> 4) != area) continue;
                double dx = o._state.positionX - b._state.positionX, dy = o._state.positionY - b._state.positionY, d = dx * dx + dy * dy;
                if (d < best) { best = d; fx = o._state.positionX; fy = o._state.positionY; found = true; }
            }
            foreach (Player p in _m.Arena.PlayersIngame)
            {
                if (p._team == b._team || p.IsDead || p._state == null) continue;
                if (AreaOf(p._state.positionX >> 4, p._state.positionY >> 4) != area) continue;
                double dx = p._state.positionX - b._state.positionX, dy = p._state.positionY - b._state.positionY, d = dx * dx + dy * dy;
                if (d < best) { best = d; fx = p._state.positionX; fy = p._state.positionY; found = true; }
            }
            return found;
        }

        /// <summary>The living teammate (not a Medic) furthest out in the bot's area, for a Medic to stay with.</summary>
        bool Escort(CTFXBot b, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            CTFXBot pick = null;
            int bestIdx = -1;
            foreach (CTFXBot o in _m.LiveBots)
            {
                if (o == b || o._team != b._team || o.IsDead || o.Released || o.Mini == null || o.Mini.Role == "MED") continue;
                if (o.Mini.Phase != b.Mini.Phase) continue;
                if (AreaOf(o._state.positionX >> 4, o._state.positionY >> 4) != AreaOf(b._state.positionX >> 4, b._state.positionY >> 4)) continue;
                int score = o.Mini.Idx + (o.HasTarget ? 3 : 0);
                if (score > bestIdx) { bestIdx = score; pick = o; }
            }
            if (pick == null) return false;
            Around(pick._state.positionX >> 4, pick._state.positionY >> 4, 3, out tx, out ty);
            return true;
        }

        // ---------------------------------------------------------------- pauses (CTFXBot.PauseMs calls this)
        /// <summary>How long a bot stands at a goal: short while sweeping (it keeps moving), long only at a firing position it holds.</summary>
        internal int PauseMs(CTFXBot b)
        {
            MiniBot mb = b.Mini;
            if (mb == null) return 1500;
            if (mb.Hunting) return 150 + _m.Rng.Next(300);
            if (mb.Hold) return (mb.Role == "DHVY" ? 4000 : 2500) + _m.Rng.Next(2500);       // the anchor stands its choke
            return 600 + _m.Rng.Next(900);
        }

        // ---------------------------------------------------------------- the sweep tour (Travis 11-07: "they need to sweep the FULL base, with a
        // dedicated anchor that stays mostly back")
        // Besides the ladder, each home base has a TOUR: points spread over every hall and room of the base (farthest-point sampling over the
        // route book's waypoints inside the base, at least ~7 tiles apart), ordered as a loop. Sweepers walk the loop (1-2 points at a time, a
        // random direction), so the whole base gets walked, and a share of the legs go to the main choke and hold it.
        readonly List<int[]>[] _sweep = new List<int[]>[2];
        readonly int[] _mainChoke = { 3, 2 };      // ladder level of the most important choke, by side: D7 (Titan) the channel foot, A7 (Collective) the mouth

        internal static int HomeAreaOf(int side) { return HomeAreaIdx(side); }

        /// <summary>A summoner refused because an enemy Teleport Disruptor holds it walks out of the disruptor's range (the SL's relocation), then the ask is retried.</summary>
        internal void RelocForSummon(CTFXBot b, int now) { RelocateFromTd(b, now); }

        /// <summary>A summoner that is in the wrong base for a human's ask hops there first (CTFXChat.MiniSummon): toEnemy = into the enemy base, else home.</summary>
        internal void Recruit(CTFXBot b, bool toEnemy, int now)
        {
            MiniBot mb = b.Mini;
            if (mb == null || b.IsDead) return;
            int a = AreaOf(b._state.positionX >> 4, b._state.positionY >> 4);
            int home = HomeAreaIdx(mb.Side), foe = home == 0 ? 1 : 0;
            if (toEnemy) { if (a != foe) EnterFoe(b, mb, now); }
            else if (a != home) GoHome(b, mb, now);
        }

        static int HomeAreaIdx(int side) { return side == 0 ? 1 : 0; }      // Titan's home is D7 (area 1), Collective's A7 (area 0)

        internal void BuildSweeps()
        {
            for (int side = 0; side < 2; side++)
            {
                _sweep[side] = null;
                try
                {
                    if (_m.Routes == null) continue;
                    int area = HomeAreaIdx(side);
                    List<int[]> cand = new List<int[]>();
                    HashSet<long> seen = new HashSet<long>();
                    for (int n = 0; n < _m.Routes.N; n++)
                    {
                        if (_m.Routes.IsGrid(n)) continue;
                        int tx = _m.Routes.X[n] >> 4, ty = _m.Routes.Y[n] >> 4;
                        if (AreaOf(tx, ty) != area) continue;
                        if (seen.Add(((long)tx << 20) | (uint)ty)) cand.Add(new int[] { tx, ty });
                    }
                    if (cand.Count < 4) continue;
                    {   // not the exit chamber: its doors shut behind a bot and the only way out is a portal bots cannot take (Pub 11-08)
                        int exx, exy;
                        if (Pt(HomeArea[side], "exitPortal", out exx, out exy))
                        {
                            List<int[]> keep = new List<int[]>();
                            foreach (int[] c in cand) if ((c[0] - exx) * (c[0] - exx) + (c[1] - exy) * (c[1] - exy) > 10 * 10) keep.Add(c);
                            if (keep.Count >= 4) cand = keep;
                        }
                    }
                    int hx, hy;
                    if (!Pt(HomeArea[side], "home", out hx, out hy)) { hx = cand[0][0]; hy = cand[0][1]; }
                    List<int[]> chosen = new List<int[]>();
                    chosen.Add(new int[] { hx, hy });
                    while (chosen.Count < 20)
                    {
                        int[] best = null; double bd = -1.0;
                        foreach (int[] c in cand)
                        {
                            double md = double.MaxValue;
                            foreach (int[] k in chosen) { double d = Math.Pow(c[0] - k[0], 2) + Math.Pow(c[1] - k[1], 2); if (d < md) md = d; }
                            if (md > bd) { bd = md; best = c; }
                        }
                        if (best == null || bd < 7.0 * 7.0) break;
                        chosen.Add(best);
                    }
                    // a loop: nearest neighbour from the flag room
                    List<int[]> tour = new List<int[]>();
                    List<int[]> left = new List<int[]>(chosen);
                    int[] cur = left[0]; tour.Add(cur); left.RemoveAt(0);
                    while (left.Count > 0)
                    {
                        int bi = 0; double bd2 = double.MaxValue;
                        for (int i = 0; i < left.Count; i++) { double d = Math.Pow(left[i][0] - cur[0], 2) + Math.Pow(left[i][1] - cur[1], 2); if (d < bd2) { bd2 = d; bi = i; } }
                        cur = left[bi]; tour.Add(cur); left.RemoveAt(bi);
                    }
                    _sweep[side] = tour;
                }
                catch (Exception ex) { _m.WarnOnce("[MiniTP] sweep: " + ex.Message); }
            }
            _m.MiniLog("[MiniTP] sweep tours: " + (_sweep[0] != null ? _sweep[0].Count : 0) + " points in D7, " + (_sweep[1] != null ? _sweep[1].Count : 0) + " in A7 (main chokes: ladder " + _mainChoke[0] + " / " + _mainChoke[1] + ")");
        }

        /// <summary>The ladder level nearest to a tile (0 = the flag room).</summary>
        int LadderIdxAt(int side, int tx, int ty)
        {
            List<int[]> lad = _ladder[side];
            if (lad == null || lad.Count == 0) return 0;
            int bi = 0; double bd = double.MaxValue;
            for (int i = 0; i < lad.Count; i++) { double d = Math.Pow(lad[i][0] - tx, 2) + Math.Pow(lad[i][1] - ty, 2); if (d < bd) { bd = d; bi = i; } }
            return bi;
        }

        // ---------------------------------------------------------------- places and callouts (team chat: the CTF:X chat module's Say, caps in MiniTPBots.txt)
        /// <summary>Where a tile is, as a player would say it: the nearest named point of its area.</summary>
        internal string PlaceName(int tx, int ty)
        {
            int a = AreaOf(tx, ty);
            if (a == 2) return "the field";
            if (a == 3 || a == 4) return "the ship";
            if (a < 0) return "base";
            string area = a == 0 ? "A7" : "D7";
            string best = "base";
            double bd = double.MaxValue;
            foreach (KeyValuePair<string, int[]> kv in _pt)
            {
                if (!kv.Key.StartsWith(area + ":", StringComparison.OrdinalIgnoreCase)) continue;
                string w = WordFor(kv.Key.Substring(area.Length + 1), area);
                if (w == null) continue;
                double d = Math.Pow(kv.Value[0] - tx, 2) + Math.Pow(kv.Value[1] - ty, 2);
                if (d < bd) { bd = d; best = w; }
            }
            return best;
        }

        static string WordFor(string point, string area)
        {
            switch (point)
            {
                case "home": case "flag1": case "flag2": case "MED": return "the flag room";
                case "mouth": return "the mouth";
                case "chanTop": case "chanBot": return "the channel";
                case "gaunt": case "BOX": case "eastEntry": return "the box";
                case "REP1": case "REP2": case "DHVY": case "ENG": return "the turrets";
                case "DINF1": case "DINF2": return area == "A7" ? "the north hall" : "the gallery";
                case "SL": case "TB": return "the back hall";
                case "landA": case "landB": return "the landing";
            }
            return null;
        }

        /// <summary>The Engineer says what its subroutine is (once per change of task, then quiet for 20 s).</summary>
        void EngSay(CTFXBot b, MiniBot mb, int now, string task, params string[] lines)
        {
            if (mb.EngTask == task) return;
            mb.EngTask = task;
            if (now - mb.EngSayAt < 0) return;
            mb.EngSayAt = now + 20000;
            TalkNow(b, 1.0, 25, lines);
        }

        /// <summary>Like Talk, but not held back by the team's shared chat gap (an Engineer's announcement of what it is about to do must be heard: Travis 11-08); the bot's own gap still applies.</summary>
        void TalkNow(CTFXBot b, double chance, byte bong, params string[] lines)
        {
            try
            {
                if (_m.Chat == null || !_m.Chat.On) return;
                string t = lines[_m.Rng.Next(lines.Length)];
                int tx = b._state.positionX >> 4, ty = b._state.positionY >> 4;
                t = t.Replace("{c}", Coord(tx, ty)).Replace("{p}", PlaceName(tx, ty));
                _m.Chat.Say(b, t, chance, true, bong);
            }
            catch (Exception) { }
        }

        /// <summary>Team chat from a bot (the CTF:X chat module: only the humans on its team hear it; caps keep it quiet).</summary>
        void Talk(CTFXBot b, double chance, byte bong, params string[] lines)
        {
            try
            {
                if (_m.Chat == null || !_m.Chat.On) return;
                string t = lines[_m.Rng.Next(lines.Length)];
                int tx = b._state.positionX >> 4, ty = b._state.positionY >> 4;
                t = t.Replace("{c}", Coord(tx, ty)).Replace("{p}", PlaceName(tx, ty));
                _m.Chat.Say(b, t, chance, false, bong);
            }
            catch (Exception) { }
        }

        /// <summary>The map grid square a tile is in, as the client's %coord macro prints it: 80 x 80 tiles, a letter by column and a number by row
        /// (the Collective base is M9, the Titan base N8).</summary>
        internal static string Coord(int tx, int ty)
        {
            return ((char)('A' + Math.Max(0, Math.Min(25, tx / 80)))).ToString() + (Math.Max(0, ty) / 80 + 1);
        }

        // ---------------------------------------------------------------- defense
        static bool IsAnchor(MiniBot mb) { return mb.Role == "DHVY"; }       // the defensive heavy is the team's true anchor

        /// <summary>Mean ladder level of the other sweepers in the same base (the anchor trails the front line).</summary>
        double OthersLevel(CTFXBot b, MiniBot mb)
        {
            int n = 0, sum = 0;
            foreach (CTFXBot o in _m.LiveBots)
            {
                if (o == b || o._team != b._team || o.IsDead || o.Released || o.Mini == null) continue;
                if (o.Mini.Phase != MiniBot.Defend || o.Mini.Role == "DHVY" || o.Mini.Role == "MED" || o.Mini.Role == "ENG") continue;
                sum += o.Mini.Idx; n++;
            }
            return n == 0 ? 0.0 : (double)sum / n;
        }

        bool AnchorGoal(CTFXBot b, MiniBot mb, int now, List<int[]> lad, out int tx, out int ty)
        {
            // it holds the main choke and stays there (long holds, a small re-position now and then); it only moves UP when the rest of the
            // team has pushed out past it (it trails the front line by about a level, never leads it) and now and then a step more on a whim
            int choke = Math.Min(_mainChoke[mb.Side], lad.Count - 1);
            if (now - mb.NextTopAt >= 0)
            {
                mb.NextTopAt = now + 9000 + _m.Rng.Next(9000);
                double others = OthersLevel(b, mb);
                int want = choke;
                if (others > choke + 1.0) want = choke + (int)Math.Min(2.0, Math.Floor(others - choke - 0.5));
                if (_m.Rng.Next(8) == 0) want++;                                   // the occasional push of its own
                mb.Top = Math.Max(0, Math.Min(lad.Count - 1, want));
            }
            int lvl = Math.Max(0, Math.Min(lad.Count - 1, mb.Top <= 0 ? choke : mb.Top));
            mb.Idx = lvl;
            mb.Hold = true;
            Around(lad[lvl][0], lad[lvl][1], 1, out tx, out ty);
            if (!mb.Said) { mb.Said = true; Talk(b, 0.9, 25, "| Defending [{c}] | Status: Safe |", "@Home Safe {c}", "@ Home"); }
            return true;
        }

        bool DefendGoal(CTFXBot b, MiniBot mb, int now, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            mb.Hunting = false; mb.Hold = false;
            if (_allIn == mb.Side && AllInGoal(b, mb, now, out tx, out ty)) return true;      // a flag lies at home and the other team holds it: take it
            if (CarrierGoal(b, mb, now, out tx, out ty)) return true;                         // Travis 11-08: carrier first - an enemy carrying a flag in this base is the target (not the engineer / medic; the anchor only when he is close)
            if (GrabGoal(b, mb, now, out tx, out ty)) return true;                            // 11-11: a flag of the other team lying free in this base: take it back
            int bx = b._state.positionX >> 4, by = b._state.positionY >> 4;
            int homeArea = AreaOf(bx, by);
            List<int[]> lad = _ladder[mb.Side];
            bool anchor = IsAnchor(mb);
            if (lad != null && lad.Count > 0) mb.Idx = LadderIdxAt(mb.Side, bx, by);
            // an enemy inside the base: the sweepers go for it, the anchor only when it is close to its choke; the Medic and Engineer stay with the group
            mb.ShooterKey = null;
            if (mb.Role != "MED" && mb.Role != "ENG" && !anchor && ShooterGoal(b, mb, now, homeArea, out tx, out ty)) return true;       // Travis 11-08: whoever is shooting our turrets
            if (mb.Role != "MED" && mb.Role != "ENG")
            {
                int fx, fy;
                if (NearestFoe(b, homeArea, anchor ? 650.0 : 1600.0, out fx, out fy))
                {
                    bool fresh = _intruderAt[mb.Side] == 0 || now - _intruderAt[mb.Side] > 20000;
                    _intruderAt[mb.Side] = now | 1;                                   // the base is under attack: the push-out starts over
                    if (fresh)
                    {   // the players' macros: %26 Enemy Approaching / %19 Contact @ Home / %28 Under Attack (once he is close)
                        double fd = Math.Sqrt(Math.Pow(fx - b._state.positionX, 2) + Math.Pow(fy - b._state.positionY, 2));
                        if (fd > 500.0) Talk(b, 0.9, 26, "%26 Enemy Approaching", "At Home enemy sighted", "Contact @ Home");
                        else Talk(b, 0.9, 28, "| Defending [{c}] | Status: Under Attack |", "%28Under Attack", "Under Attack    --[{c}]--");
                    }
                    Around(fx >> 4, fy >> 4, 1, out tx, out ty); mb.Hunting = true; return true;
                }
            }
            if (lad == null || lad.Count == 0)
            {   // no ladder (route book missing): the role anchors
                string name = mb.Role == "MED" ? "MED" : mb.Role == "ENG" ? "ENG" : mb.Role == "DHVY" ? "DHVY" : (mb.Variant == 0 ? "DINF1" : "DINF2");
                return Anchor(HomeArea[mb.Side], name, 3, out tx, out ty);
            }
            if (anchor) return AnchorGoal(b, mb, now, lad, out tx, out ty);
            if (mb.Role == "MED")
            {   // Tsolvy somebody dropped in the base: the medic fetches it while it carries less than 120 (it uses 1 a medikit, 5 a deluxe)
                int tsx, tsy;
                if (MedPileReady(b, mb) && FindPile(b, homeArea, 2005, out tsx, out tsy)) { tx = tsx >> 4; ty = tsy >> 4; return true; }      // 11-11: after its reaction delay (tier), up to 150
            }
            if (mb.Role == "MED" && Escort(b, out tx, out ty)) return true;                  // a Medic stays with the group
            if (mb.Role == "ENG")
            {   // Pub night 11-07: "the engineer gets stuck in a loop, trying to repair a turret without having what it needs": out of repair-kit charges it recalls to the dropship to restock
                // (the ship hop brings it home again); if the recall is not possible it waits at its post instead of walking from turret to turret with nothing to repair with
                int needTox;
                int engJob = EngNextJob(b, mb, now, out needTox);
                int pox, poy;
                if (b.KitCharges >= 0 && b.KitCharges < 90 && FindPile(b, homeArea, 2009, out pox, out poy))
                {   // Titanium Oxide somebody dropped in the base: fetched before it recalls for more
                    EngSay(b, mb, now, "tox", "Getting the dropped Oxide", "@ Fetching Titanium Oxide {c}", "| Engineer | fetching Oxide |");
                    tx = pox >> 4; ty = poy >> 4;
                    return true;
                }
                if (b.KitCharges >= 0 && b.KitCharges < Math.Max(5, needTox))      // too little Titanium Oxide for the next job (a repair 5, an MG 25, a plasma 15, a rocket 50)
                {
                    if (now - mb.RestockAt >= 0)
                    {
                        mb.RestockAt = now + 30000;
                        if (b.Recall(now)) { _m.MiniLog("[MiniTP] engineer " + _m.RowName(b.Slot) + " is out of repair charges: recalled to restock"); mb.EngTask = "restock"; mb.EngSayAt = now + 20000; TalkNow(b, 1.0, 25, "Restocking, back soon", "@ out of minerals, restocking", "| Engineer | Going to restock [{c}] |"); }
                    }
                    return Anchor(HomeArea[mb.Side], "ENG", 2, out tx, out ty);
                }
                // (Travis 11-08, the engineer rework) 1. a spare sentry a defender dropped for it; 2. a base turret that attackers took down is REBUILT (the event builds the base turrets once,
                // at the game start); both are said in team chat. The rebuild is carried out by EngTick when it stands still on the spot.
                int psx, psy;
                if (mb.Sentries < 2 && FindSentryPile(b, homeArea, out psx, out psy))
                {
                    EngSay(b, mb, now, "fetch", "Picking up the spare sentry", "@ Getting the dropped sentry {c}", "| Engineer | Fetching a sentry |");
                    tx = psx >> 4; ty = psy >> 4;
                    mb.BuildIdx = -1;
                    return true;
                }
                if (engJob >= 0)
                {
                    object[] engSpot = BaseTurrets[engJob];
                    EngSay(b, mb, now, "rebuild", "Rebuilding the " + ((string)engSpot[1]).ToLowerInvariant() + " turret [{c}]", "@ Turret down, rebuilding {c}", "| Engineer | Rebuilding " + (string)engSpot[1] + " [{c}] |");
                    mb.BuildIdx = engJob;
                    tx = (int)engSpot[2]; ty = (int)engSpot[3];
                    return true;
                }
                mb.BuildIdx = -1;
                // the Engineer repairs: the most damaged friendly turret in its base first (CTFXTurrets.RepairNear heals what is within ~120 px)
                // (Travis 11-08: "have the engineer communicate in chat all of its subroutines": it says what it is about to do whenever the task changes)
                Computer hurt = null; double worst = 0.8;
                foreach (Computer c in _m.Turrets.Live)
                {
                    if (c.IsDead || c._team != b._team || c._type.Id == 402) continue;                // not a sentry (11-08: those are rebuilt, not repaired)
                    if (AreaOf(c._state.positionX >> 4, c._state.positionY >> 4) != homeArea) continue;
                    double f = (double)c._state.health / Math.Max(1, c._type.Hitpoints);
                    if (f < worst) { worst = f; hurt = c; }
                }
                if (hurt != null) { EngSay(b, mb, now, "repair", "Repairing a turret @ {c}", "@ Repairing turret --[{c}]--", "| Engineer | Repairing [{c}] |"); Around(hurt._state.positionX >> 4, hurt._state.positionY >> 4, 2, out tx, out ty); return true; }
                // nothing hurt: it works the turret line, the first levels of the ladder
                EngSay(b, mb, now, "patrol", "Checking the turrets", "@ Turrets look fine {c}", "| Engineer | Checking turrets |");
                int el = _m.Rng.Next(Math.Min(4, lad.Count));
                Around(lad[el][0], lad[el][1], 2, out tx, out ty);
                return true;
            }
            // a sweeper: the loop through the whole base, with a good share of the legs spent holding the main choke
            List<int[]> tour = _sweep[mb.Side];
            int choke = Math.Min(_mainChoke[mb.Side], lad.Count - 1);
            if (_m.Rng.NextDouble() < 0.28)
            {
                int cl = Math.Max(0, Math.Min(lad.Count - 1, choke + _m.Rng.Next(-1, 2)));
                Around(lad[cl][0], lad[cl][1], 2, out tx, out ty);
                mb.Hold = true;
                return true;
            }
            if (tour == null || tour.Count == 0)
            {
                int l = _m.Rng.Next(lad.Count);
                Around(lad[l][0], lad[l][1], 2, out tx, out ty);
                return true;
            }
            if (mb.Tour < 0) { mb.Tour = _m.Rng.Next(tour.Count); mb.Dir = _m.Rng.Next(2) == 0 ? 1 : -1; }
            if (_m.Rng.Next(6) == 0) mb.Dir = -mb.Dir;
            mb.Tour = ((mb.Tour + mb.Dir * (1 + _m.Rng.Next(2))) % tour.Count + tour.Count) % tour.Count;
            int[] p = tour[mb.Tour];
            Around(p[0], p[1], 1, out tx, out ty);
            if (_m.Rng.Next(4) == 0) mb.Hold = true;
            if (_m.Rng.Next(14) == 0) Talk(b, 0.5, 25, "@Home Safe {c}", "| Defending [{c}] | Status: Safe |");
            return true;
        }

        // ---------------------------------------------------------------- green portals (Travis 11-07: "not proactive about activating the green portal areas",
        // "not taking the enemy portals in the dropship, nor creating them")
        // The enemy portal is a Hide spawner INSIDE the enemy base (spec 1): Titan builds at the two A7 hides, Collective at the two D7 hides: the one near the
        // landing first (veh 413, the safer walk), then the deep one beside the flag room (veh 436). Humans warping in are sent to the 436 one when it is up. The engine only fires a hide for a real player standing there, so a bot stands on the spot for 2.5 s and the
        // script builds the same vehicle (CTF.cs MiniTPBuildGreenPortal). Attackers in the ship then take the portal about two times in three (the field walk is the other way in).
        static readonly int[][][] PortalSpots = {
            new int[][] { new int[] { 501, 999, 664 }, new int[] { 513, 998, 637 } },          // Titan builds in A7: hide id, tile x, tile y - the one near the landing (veh 413) first, the deep one (veh 436) second
            new int[][] { new int[] { 514, 1096, 576 }, new int[] { 515, 1136, 613 } } };      // Collective builds in D7
        int _portalAt;
        readonly int[] _portalNext = new int[2];
        internal int PortalsBuilt;

        /// <summary>True when a portal vehicle (413 / 436) stands within a hide's box.</summary>
        bool PortalUp(int[] sp)
        {
            int cx = sp[1] * 16 + 8, cy = sp[2] * 16 + 8;
            foreach (Vehicle v in _m.Arena.Vehicles)
            {
                if (v == null || v._type == null || (v._type.Id != 413 && v._type.Id != 436) || v.IsDead) continue;       // a destroyed portal is not up
                if (Math.Abs(v._state.positionX - cx) <= 56 && Math.Abs(v._state.positionY - cy) <= 56) return true;
            }
            return false;
        }

        /// <summary>The position of this side's enemy portal: the nearer-the-flags one first. False = none is up.</summary>
        bool GreenPortalPos(int side, out int px, out int py)
        {   // the players' own rule (CTF.cs MiniTPEnemyPortal): alive, spawned by the hide, in this team's enemy base, the one nearer the flags first
            px = 0; py = 0;
            Vehicle v = _m.EnemyPortal(side);
            if (v == null) return false;
            px = v._state.positionX; py = v._state.positionY;
            return true;
        }

        /// <summary>An attacker in the ship takes the team's enemy portal when one is up (about two times in three).</summary>
        bool TryGreen(CTFXBot b, MiniBot mb, int now)
        {
            if (_m.Rng.NextDouble() > 0.66) return false;
            int px, py;
            if (!GreenPortalPos(mb.Side, out px, out py)) return false;
            Land(b, px >> 4, py >> 4, 1, now);
            mb.Phase = MiniBot.Raid; mb.PhaseAt = now; mb.Hops++;
            mb.Stage = mb.Role == "JT" || mb.Role == "INFIL" ? 1 : 0;
            mb.StageUntil = now + 12000 + _m.Rng.Next(18000);
            Talk(b, 0.6, 24, "| Attacking Enemy [{c}] |", "At Enemy", "@Nme");
            GreenTaken++;
            return true;
        }
        internal int GreenTaken;

        /// <summary>Every 2 s: for each side, make sure one raider is on its way to build the enemy portal that is missing.</summary>
        void PortalStep(int now)
        {
            if (now - _portalAt < 2000) return;
            _portalAt = now;
            for (int side = 0; side < 2; side++)
            {
                int want = -1;
                for (int i = 0; i < 2 && want < 0; i++)
                    if (!PortalUp(PortalSpots[side][i])) want = i;
                CTFXBot builder = null;
                foreach (CTFXBot b in _m.LiveBots)
                {
                    MiniBot mb = b.Mini;
                    if (mb == null || mb.Side != side || mb.Spot < 0) continue;
                    if (b.IsDead || b.Released || mb.Phase != MiniBot.Raid) { _m.MiniLog("[MiniTP] portal builder " + _m.RowName(b.Slot) + " " + (b.IsDead ? "died" : "left the base") + " before the portal was up"); mb.Spot = -1; mb.BuildStart = 0; continue; }
                    builder = b;
                }
                if (want < 0) { if (builder != null) { builder.Mini.Spot = -1; builder.Mini.BuildStart = 0; } continue; }
                if (builder != null) { builder.Mini.Spot = want; continue; }
                if (now - _portalNext[side] < 0) continue;
                int foe = HomeAreaIdx(side) == 0 ? 1 : 0;
                int[] sp = PortalSpots[side][want];
                // a lone raider landing in the enemy base dies in seconds (rig 11-07: 12 builders, 0 portals): the builder goes with company - at least 2 of its team
                // are already in the enemy base - and only while no enemy bot is near the spot
                int company = 0;
                foreach (CTFXBot b in _m.LiveBots)
                    if (b.Mini != null && b.Mini.Side == side && !b.IsDead && !b.Released && b.Mini.Phase == MiniBot.Raid && AreaOf(b._state.positionX >> 4, b._state.positionY >> 4) == foe) company++;
                if (company < 2) continue;
                bool covered = false;
                foreach (CTFXBot o in _m.LiveBots)
                {
                    if (o.Mini == null || o.Mini.Side == side || o.IsDead || o.Released) continue;
                    double ex = o._state.positionX - (sp[1] * 16 + 8), ey = o._state.positionY - (sp[2] * 16 + 8);
                    if (ex * ex + ey * ey <= 300.0 * 300.0) { covered = true; break; }
                }
                if (covered) continue;
                CTFXBot best = null; double bd = double.MaxValue;
                foreach (CTFXBot b in _m.LiveBots)
                {
                    MiniBot mb = b.Mini;
                    if (mb == null || mb.Side != side || b.IsDead || b.Released || mb.Phase != MiniBot.Raid) continue;
                    if (mb.Role == "SL" || mb.Role == "OHVY" || mb.Role == "DHVY" || mb.Role == "MED" || mb.Role == "ENG") continue;
                    if (AreaOf(b._state.positionX >> 4, b._state.positionY >> 4) != foe) continue;
                    double dx = b._state.positionX - (sp[1] * 16 + 8), dy = b._state.positionY - (sp[2] * 16 + 8), d = dx * dx + dy * dy;
                    if (d < bd) { bd = d; best = b; }
                }
                if (best == null) continue;
                best.Mini.Spot = want; best.Mini.BuildStart = 0;
                _portalNext[side] = now + 3000;
                {   // the walk across the enemy base is what kills a builder (rig 11-07): like every bot entry into a base it hops - straight onto the spot, with the usual warp grace
                    double hx = best._state.positionX - (sp[1] * 16 + 8), hy = best._state.positionY - (sp[2] * 16 + 8);
                    if (hx * hx + hy * hy > 400.0 * 400.0) Land(best, sp[1], sp[2], 1, now);
                }
                _m.MiniLog("[MiniTP] portal builder: " + _m.RowName(best.Slot) + " (" + best.Mini.Role + ") goes to hide " + sp[0] + " at " + sp[1] + "," + sp[2] + " for " + (side == 0 ? "Titan" : "Collective"));
            }
        }

        /// <summary>The builder's goal: the hide's spot; standing on it for 2.5 s builds the portal. An enemy within 420 px is fought first.</summary>
        bool PortalGoal(CTFXBot b, MiniBot mb, int now, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            int[] sp = PortalSpots[mb.Side][mb.Spot];
            int fx, fy;
            if (NearestFoe(b, AreaOf(b._state.positionX >> 4, b._state.positionY >> 4), 420.0, out fx, out fy))
            {
                Around(fx >> 4, fy >> 4, 1, out tx, out ty); mb.Hunting = true; mb.BuildStart = 0;
                return true;
            }
            tx = sp[1]; ty = sp[2];
            mb.Hold = false;
            return true;
        }

        /// <summary>Every poll tick (not only when a goal is picked): a builder on its spot for 1.5 s builds the portal.</summary>
        void BuilderTick(int now)
        {
            foreach (CTFXBot b in _m.LiveBots)
            {
                MiniBot mb = b.Mini;
                if (mb == null || mb.Spot < 0 || b.IsDead || b.Released || mb.Phase != MiniBot.Raid) continue;
                int[] sp = PortalSpots[mb.Side][mb.Spot];
                double dx = b._state.positionX - (sp[1] * 16 + 8), dy = b._state.positionY - (sp[2] * 16 + 8);
                if (dx * dx + dy * dy > 26.0 * 26.0) { mb.BuildStart = 0; continue; }
                mb.Hold = true;
                if (mb.BuildStart == 0) { mb.BuildStart = now; _m.MiniLog("[MiniTP] portal builder " + _m.RowName(b.Slot) + " is on the spot at hide " + sp[0]); continue; }
                if (now - mb.BuildStart < 1500) continue;
                bool ok = _m.BuildGreenPortal(sp[0]);
                _m.MiniLog("[MiniTP] green portal " + (ok ? "BUILT" : "not built (one stands there already)") + " by " + _m.RowName(b.Slot) + " at hide " + sp[0]);
                if (ok) { PortalsBuilt++; Talk(b, 0.9, 24, "| Green Portal Up [{c}] |", "Portal Up", "@ Portal Up [{c}]"); }
                mb.Spot = -1; mb.BuildStart = 0; mb.Hold = false;
                _portalNext[mb.Side] = now + 4000;
            }
        }

        // ---------------------------------------------------------------- raid
        static readonly string[] RoamPoints = { "flag1", "flag2", "home", "REP1", "REP2", "DHVY", "ENG", "MED", "DINF1", "DINF2" };

        // ---------------------------------------------------------------- the engineer rework (Travis 11-08: "building turrets that have been taken down by attackers should be a thing the engineer
        // is capable of doing (rebuilding turrets)", "defenders carrying an extra sentry to drop when they arrive at base, so the bot engineer can pick it up")
        // The event builds each base's turrets ONCE at a game start (CTF.cs BuildMiniTPTurrets: side, kind, tile x, y); nothing replaced one the attackers destroyed. The engineer now does:
        // it walks to the empty spot, stands still for the maker's prefire (1.5 s, movement cancels it), pays what the maker item costs (the data: AutoGun MG 25 Titanium Oxide, Plasma 15,
        // Rocket 50; a Sentry uses up one carried Sentry item) and builds it the way the event did. Sentry items come from the defenders: on arriving at the base some of them drop a spare
        // beside the engineer's post (a visible pile), and the engineer picks it up. It does not repair sentries with Oxide any more (they are rebuilt), and it only rebuilds a spot that
        // has no enemy within 600 px.
        static readonly object[][] BaseTurrets = new object[][] {
            new object[] { 0, "MG", 1094, 627 }, new object[] { 0, "MG", 1077, 636 }, new object[] { 0, "Rocket", 1089, 635 }, new object[] { 0, "Plasma", 1076, 639 }, new object[] { 0, "Sentry", 1081, 630 }, new object[] { 0, "Sentry", 1101, 634 },
            new object[] { 1, "MG", 977, 632 }, new object[] { 1, "MG", 977, 622 }, new object[] { 1, "Rocket", 991, 628 }, new object[] { 1, "Plasma", 976, 639 }, new object[] { 1, "Sentry", 1002, 629 }, new object[] { 1, "Sentry", 985, 638 } };
        internal int Rebuilt, SentriesDropped, SentriesTaken;
        int _engCallAt, _engLogAt;
        readonly int[] _engCallNext = new int[2];

        static int VehOf(string kind) { switch (kind) { case "MG": return 429; case "Rocket": return 428; case "Plasma": return 427; case "Sentry": return 402; } return 0; }
        static int ToxOf(string kind) { switch (kind) { case "MG": return 25; case "Rocket": return 50; case "Plasma": return 15; } return 0; }

        /// <summary>A live turret of this kind stands within 3 tiles of the spot.</summary>
        bool TurretStands(string kind, int tx, int ty)
        {
            int vid = VehOf(kind);
            foreach (Computer c in _m.Turrets.Live)
            {
                if (c.IsDead || c._type == null || c._type.Id != vid) continue;
                double dx = c._state.positionX - tx * 16, dy = c._state.positionY - ty * 16;
                if (dx * dx + dy * dy <= 48.0 * 48.0) return true;
            }
            return false;
        }

        bool EnemyNear(CTFXBot b, int px, int py, double r)
        {
            double r2 = r * r;
            foreach (CTFXBot o in _m.LiveBots)
            {
                if (o.IsDead || o.InDropship || o._team == b._team) continue;
                double dx = o._state.positionX - px, dy = o._state.positionY - py;
                if (dx * dx + dy * dy < r2) return true;
            }
            foreach (Player p in _m.Arena.PlayersIngame)
            {
                if (p == null || p.IsDead || p._state == null || p._team == b._team) continue;
                double dx = p._state.positionX - px, dy = p._state.positionY - py;
                if (dx * dx + dy * dy < r2) return true;
            }
            return false;
        }

        /// <summary>The nearest base turret spot of the engineer's side that stands empty and that it can pay for now (-1 = none); needTox = what that rebuild costs in Oxide.</summary>
        int EngNextJob(CTFXBot b, MiniBot mb, int now, out int needTox)
        {
            needTox = 0;
            int best = -1;
            double bd = double.MaxValue;
            for (int i = 0; i < BaseTurrets.Length; i++)
            {
                object[] t = BaseTurrets[i];
                if ((int)t[0] != mb.Side) continue;
                string kind = (string)t[1];
                int tx = (int)t[2], ty = (int)t[3];
                if (mb.SpotCool != null && i < mb.SpotCool.Length && now - mb.SpotCool[i] < 0) continue;
                if (TurretStands(kind, tx, ty)) continue;
                if (kind == "Sentry" && mb.Sentries < 1) continue;                                  // no spare sentry: it waits for a pile
                if (EnemyNear(b, tx * 16 + 8, ty * 16 + 8, 320.0))                                      // not under fire (an enemy within 20 tiles of the spot)
                {
                    if (now - _engLogAt >= 0) { _engLogAt = now + 20000; _m.MiniLog("[MiniTP] engineer " + _m.RowName(b.Slot) + ": the " + kind + " spot at " + tx + "," + ty + " is empty but an enemy is within 20 tiles of it"); }
                    continue;
                }
                double dx = tx * 16 - b._state.positionX, dy = ty * 16 - b._state.positionY, d = dx * dx + dy * dy;
                if (d < bd) { bd = d; best = i; needTox = ToxOf(kind); }
            }
            return best;
        }

        bool FindSentryPile(CTFXBot b, int homeArea, out int sx, out int sy) { return FindPile(b, homeArea, 23, out sx, out sy); }

        /// <summary>The nearest pile of item <paramref name="itemId"/> (a spare sentry a defender dropped, minerals somebody dropped) in the home area; px.</summary>
        bool FindPile(CTFXBot b, int homeArea, int itemId, out int sx, out int sy)
        {
            sx = 0; sy = 0;
            double bd = double.MaxValue;
            bool found = false;
            foreach (Arena.ItemDrop d in _m.Arena._items.Values)
            {
                if (d.item == null || d.item.id != itemId || d.quantity <= 0) continue;
                if (AreaOf(d.positionX >> 4, d.positionY >> 4) != homeArea) continue;
                double dx = d.positionX - b._state.positionX, dy = d.positionY - b._state.positionY, dd = dx * dx + dy * dy;
                if (dd < bd) { bd = dd; sx = d.positionX; sy = d.positionY; found = true; }
            }
            return found;
        }

        /// <summary>A defender that has just arrived at its base drops a spare sentry beside the engineer's post (some of them, never more than two piles at once).</summary>
        void MaybeDropSentry(CTFXBot b, MiniBot mb, int now)
        {
            if (mb.Role == "ENG" || mb.Role == "MED" || _m.Rng.Next(100) >= 45) return;
            int ex, ey;
            if (!Pt(HomeArea[mb.Side], "ENG", out ex, out ey)) return;
            int home = HomeAreaIdx(mb.Side), piles = 0;
            foreach (Arena.ItemDrop d in _m.Arena._items.Values)
                if (d.item != null && d.item.id == 23 && d.quantity > 0 && AreaOf(d.positionX >> 4, d.positionY >> 4) == home) piles += d.quantity;
            if (piles >= 2) return;
            ItemInfo it = AssetManager.Manager.getItemByID(23);
            if (it == null) return;
            Arena.ItemDrop drop = _m.Arena.itemSpawn(it, 1, (short)((ex + _m.Rng.Next(-2, 3)) * 16 + 8), (short)((ey + _m.Rng.Next(-1, 2)) * 16 + 8), (short)16, null);
            if (drop == null) return;
            SentriesDropped++;
            Talk(b, 0.6, 25, "Spare sentry down for the engineer [{c}]", "@ sentry dropped for the engineer", "| Defending [{c}] | spare sentry down |");
        }

        /// <summary>Travis 11-08: "if someone drops minerals (titanium oxide, tsolvy crystals) the bot that uses them (tso for meds, tox for engs)" takes them. A pile within 28 px is picked up
        /// into the carried stock (Oxide to 100 for an engineer, Tsolvy to 150 for a medic); what does not fit goes back on the ground where it lay.</summary>
        void TakeMineral(CTFXBot b, MiniBot mb, int now, int itemId, string label, bool engineer)
        {
            Arena.ItemDrop got = null;
            foreach (Arena.ItemDrop d in _m.Arena._items.Values)
            {
                if (d.item == null || d.item.id != itemId || d.quantity <= 0) continue;
                double dx = d.positionX - b._state.positionX, dy = d.positionY - b._state.positionY;
                if (dx * dx + dy * dy <= 28.0 * 28.0) { got = d; break; }
            }
            if (got == null) return;
            int qty = got.quantity;
            int took = engineer ? b.AddTox(qty) : b.AddTso(qty);
            if (took <= 0) return;
            short gx = got.positionX, gy = got.positionY;
            ItemInfo it = got.item;
            _m.Arena._items.Remove(got.id);
            Helpers.Object_ItemDropUpdate(_m.Arena.Players, got.id, 0);
            if (took < qty) _m.Arena.itemSpawn(it, (ushort)(qty - took), gx, gy, (short)16, null);       // the rest stays for somebody else
            MineralsTaken += took;
            b.ForceGoal();
            int have = engineer ? b.KitCharges : b.TsoUnits;
            _m.MiniLog("[MiniTP] " + (engineer ? "engineer " : "medic ") + _m.RowName(b.Slot) + " picked up " + took + " " + label + " (" + have + " carried)");
            if (engineer) EngSay(b, mb, now, "minerals", "Picked up " + took + " Titanium Oxide, " + have + " on me", "@ Got the Oxide, " + have + " carried", "| Engineer | Titanium Oxide picked up |");
            else TalkNow(b, 1.0, 25, "Picked up " + took + " Tsolvy, " + have + " on me", "@ Got the Tsolvy, " + have + " carried", "| Medic | Tsolvy picked up |");
        }

        internal int MineralsTaken;

        /// <summary>Per poll for the Medic: pick up Tsolvy Crystals it stands on.</summary>
        void MedTick(CTFXBot b, MiniBot mb, int now)
        {
            if (b.IsDead || b.TsoUnits < 0 || b.TsoUnits >= 150) return;
            TakeMineral(b, mb, now, 2005, "Tsolvy", false);
        }

        /// <summary>Per poll for the Engineer: pick up a sentry it stands on, and carry out the rebuild it is standing at.</summary>
        void EngTick(CTFXBot b, MiniBot mb, int now)
        {
            if (b.IsDead) { mb.BuildStart = 0; return; }
            if (mb.Sentries < 2)
            {
                Arena.ItemDrop got = null;
                foreach (Arena.ItemDrop d in _m.Arena._items.Values)
                {
                    if (d.item == null || d.item.id != 23 || d.quantity <= 0) continue;
                    double dx = d.positionX - b._state.positionX, dy = d.positionY - b._state.positionY;
                    if (dx * dx + dy * dy <= 28.0 * 28.0) { got = d; break; }
                }
                if (got != null)
                {
                    _m.Arena._items.Remove(got.id);
                    Helpers.Object_ItemDropUpdate(_m.Arena.Players, got.id, 0);
                    mb.Sentries += got.quantity;
                    SentriesTaken++;
                    b.ForceGoal();
                    EngSay(b, mb, now, "fetched", "Got the sentry, " + mb.Sentries + " in hand", "@ Sentry picked up", "| Engineer | sentry picked up |");
                }
            }
            if (b.KitCharges >= 0 && b.KitCharges < 100) TakeMineral(b, mb, now, 2009, "Titanium Oxide", true);
            if (mb.BuildIdx < 0 || mb.BuildIdx >= BaseTurrets.Length) { mb.BuildStart = 0; return; }
            object[] t = BaseTurrets[mb.BuildIdx];
            string kind = (string)t[1];
            int tx = (int)t[2], ty = (int)t[3];
            double ex2 = b._state.positionX - (tx * 16 + 8), ey2 = b._state.positionY - (ty * 16 + 8);
            if (ex2 * ex2 + ey2 * ey2 > 40.0 * 40.0 || !b.Still || b.HasTarget) { mb.BuildStart = 0; return; }
            if (mb.BuildStart == 0) { mb.BuildStart = now | 1; return; }
            if (now - mb.BuildStart < 1500) return;                                                  // the maker's prefire (150 = 1.5 s), standing still
            int idx = mb.BuildIdx;
            mb.BuildStart = 0; mb.BuildIdx = -1;
            mb.SpotCool[idx] = now + 10000;
            if (TurretStands(kind, tx, ty)) return;
            int cost = ToxOf(kind);
            if (kind == "Sentry" ? mb.Sentries < 1 : b.KitCharges < cost) return;
            int maker = MakerItemOf(kind);
            if (maker > 0 && !b.CanPay(maker)) { RebuildNoEnergy++; return; }                        // 11-11: the maker's energy, as a player pays it (it was free)
            if (!_m.BuildTurret(kind, tx, ty, mb.Side)) return;
            if (maker > 0) b.SpendEnergy(maker);
            if (kind == "Sentry") mb.Sentries--; else b.SpendTox(cost);
            Rebuilt++;
            _m.MiniLog("[MiniTP] engineer " + _m.RowName(b.Slot) + " rebuilt the " + kind + " turret at " + tx + "," + ty + " (" + (kind == "Sentry" ? "a sentry used, " + mb.Sentries + " left" : cost + " Oxide, " + b.KitCharges + " left") + ")");
            TalkNow(b, 1.0, 25, kind + " turret rebuilt [{c}]", "@ " + kind + " turret back up {c}", "| Engineer | " + kind + " turret rebuilt |");
        }

        /// <summary>The Medic tells an Engineer that is away (restocking in the ship) to get home when the base is about to be hit: a contact posted inside the home area in the last 4 s.</summary>
        void EngCallStep(int now)
        {
            if (now - _engCallAt < 2000) return;
            _engCallAt = now;
            for (int side = 0; side < 2; side++)
            {
                if (now - _engCallNext[side] < 0) continue;
                bool threat = false;
                foreach (Contact c in _contacts[side])
                    if (now - c.At < 4000 && AreaOf(c.X >> 4, c.Y >> 4) == HomeAreaIdx(side)) { threat = true; break; }
                if (!threat) continue;
                CTFXBot eng = null, med = null;
                foreach (CTFXBot b in _m.LiveBots)
                {
                    if (b.Mini == null || b.IsDead || b.Released || b.Mini.Side != side) continue;
                    if (b.Mini.Role == "ENG") eng = b; else if (b.Mini.Role == "MED") med = b;
                }
                if (eng == null || med == null) continue;
                if (!eng.InDropship && AreaOf(eng._state.positionX >> 4, eng._state.positionY >> 4) == HomeAreaIdx(side)) continue;
                if (eng.Mini.Dig >= 4 || (eng.Mini.Dig > 0 && RigMine)) continue;                                                  // 11-11: an engineer already out on its mining board finishes the trip (the medic summons it home)
                _engCallNext[side] = now + 25000;
                TalkNow(med, 1.0, 24, "ENG - get home! The turrets are about to take damage [{c}]", "@ ENG home now, they are coming", "| Defending [{c}] | ENG to base |");
                Recruit(eng, false, now);
            }
        }

        // ---------------------------------------------------------------- ALL IN against the victory countdown (Travis 11-08: "the defense is still staying home when the opposing team has ALL the
        // flags. THERE IS ZERO reason for defenders to stay at the original home base at that point, they should all be attacking the base to abort the victory condition")
        // The script's rule: one team owning every active flag starts the countdown, ANY flag changing owner aborts it (Script_CTF.OnFlagChange). MiniTP bots never played flags, so a side
        // that was losing 4-0 on flags just sat at home. Now, while the countdown runs, EVERY bot of the side that does not hold the flags attacks: defenders hop to the enemy base (unless a
        // flag lies at home, then they take it there), new bodies go out as attackers, nobody holds a post, and the goal is the nearest flag the other side owns. A bot standing on such a
        // flag takes it (the same state change a player's pickup makes, without carrying), the script is told, and the countdown aborts. No timing games: the whole side rushes at once.
        internal int _allIn = -1;                                     // the side that is all in (-1 = none)
        internal int AllInHops, FlagTouches;

        void AllInStep(int now)
        {
            Team holder; int msLeft;
            int side = -1;
            if (_m.VictoryClock(out holder, out msLeft) && holder != null && holder._name != null)
                side = holder._name.StartsWith("Titan", StringComparison.Ordinal) ? 1 : 0;     // the side that does NOT hold the flags
            if (side != _allIn)
            {
                int was = _allIn;
                _allIn = side;
                if (side >= 0)
                {
                    bool flagAtHome = false;
                    foreach (Arena.FlagState fs in _m.Arena._flags.Values)
                        if (fs.bActive && fs.team != null && fs.team._name != null && fs.team._name.StartsWith("Titan", StringComparison.Ordinal) != (side == 0) && AreaOf(fs.posX >> 4, fs.posY >> 4) == HomeAreaIdx(side)) flagAtHome = true;
                    int n = 0;
                    CTFXBot talker = null;
                    foreach (CTFXBot b in _m.LiveBots)
                    {
                        MiniBot mb = b.Mini;
                        if (mb == null || b.IsDead || b.Released || mb.Side != side) continue;
                        mb.Attack = true; mb.Hold = false; mb.Hunting = false; mb.FootStage = -1;
                        b.ForceGoal();
                        n++;
                        if (talker == null || mb.Role == "SL") talker = b;
                        if (mb.Phase == MiniBot.Defend && !flagAtHome) { EnterFoe(b, mb, now); AllInHops++; }
                    }
                    _m.MiniLog("[MiniTP] VICTORY COUNTDOWN: the other team holds every flag, " + (msLeft / 1000) + " s left - " + (side == 0 ? "Titan" : "Collective") + " goes ALL IN (" + n + " bots" + (flagAtHome ? ", a flag lies at home: defenders take it there" : "") + ")");
                    if (talker != null) Talk(talker, 1.0, 24, "ALL IN - break their flags!", "| Attacking Enemy [{c}] | ALL IN", "@ ALL IN - abort the flags");
                }
                else if (was >= 0)
                {
                    foreach (CTFXBot b in _m.LiveBots) { MiniBot mb = b.Mini; if (mb != null && mb.Side == was) { mb.Attack = mb.SeatAttack; b.ForceGoal(); } }
                    _m.MiniLog("[MiniTP] victory countdown over: back to the seat table");
                }
            }
            if (_allIn < 0) return;
            // the touch: a bot of the all-in side on a flag the other side holds takes it
            foreach (CTFXBot b in _m.LiveBots)
            {
                MiniBot mb = b.Mini;
                if (mb == null || b.IsDead || b.Released || b.InDropship || mb.Side != _allIn || b.Slot == null) continue;
                foreach (Arena.FlagState fs in _m.Arena._flags.Values)
                {
                    if (!fs.bActive || fs.carrier != null || fs.team == null || fs.team == b._team) continue;
                    double dx = b._state.positionX - fs.posX, dy = b._state.positionY - fs.posY;
                    if (dx * dx + dy * dy > 40.0 * 40.0) continue;
                    TouchFlag(b, fs, now);
                    return;
                }
            }
        }

        void TouchFlag(CTFXBot b, Arena.FlagState fs, int now)
        {   // what a player's pickup does to the flag's state (CTFXFlags.Pickup), without the carrying: the flag turns to the toucher's team where it lies, the clients are told, and the script's rule runs (it aborts the countdown)
            fs.oldTeam = fs.team;
            fs.carrier = null;
            fs.team = fs.flag.FlagData.IsFlagOwnedWhenDropped ? b._team : null;
            fs.lastOperation = now;
            Helpers.Object_Flags(_m.Arena.Players, fs);
            FlagTouches++;
            _m.MiniLog("[MiniTP] FLAG TOUCH: " + _m.RowName(b.Slot) + " (" + b.Mini.Role + ") took the flag at " + (fs.posX >> 4) + "," + (fs.posY >> 4) + (_allIn >= 0 ? " - the victory countdown is aborted" : ""));
            _m.FlagChanged(fs);
            if (_allIn >= 0) Talk(b, 1.0, 24, "Flag taken - victory aborted!", "@ Flag taken, countdown aborted");
            else Talk(b, 0.7, 24, "Got the flag [" + Coord(fs.posX >> 4, fs.posY >> 4) + "]", "flag ours", "took their flag");
        }

        /// <summary>The goal of a bot of the all-in side: the nearest flag the other side owns in the bot's own area (goals never cross areas; a defender hops out first unless the flag is at home).</summary>
        bool AllInGoal(CTFXBot b, MiniBot mb, int now, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            if (_allIn != mb.Side) return false;
            int myArea = AreaOf(b._state.positionX >> 4, b._state.positionY >> 4);
            double bd = double.MaxValue;
            bool found = false;
            foreach (Arena.FlagState fs in _m.Arena._flags.Values)
            {
                if (!fs.bActive || fs.team == null || fs.team == b._team) continue;
                double fx = fs.carrier != null && fs.carrier._state != null ? fs.carrier._state.positionX : fs.posX;
                double fy = fs.carrier != null && fs.carrier._state != null ? fs.carrier._state.positionY : fs.posY;
                if (AreaOf((int)fx >> 4, (int)fy >> 4) != myArea) continue;
                double dx = fx - b._state.positionX, dy = fy - b._state.positionY, d = dx * dx + dy * dy;
                if (d < bd) { bd = d; tx = (int)fx >> 4; ty = (int)fy >> 4; found = true; }
            }
            if (found) { mb.Hold = false; mb.Hunting = false; }
            return found;
        }

        // ---------------------------------------------------------------- footholds (Travis 11-08, with a screenshot of six raiders in one blob round a corner and the open ground beside them
        // empty: "when pushing chokes, the bots should protect that area and seek out coverage of all protective chokes to establish a foothold in that part of the base, they're
        // congregating and clustering too much and die too easily as a result")
        // A stage goal used to be "a random tile within 3-5 of the same anchor" for everybody, on the same timers: one blob. Now every raider CLAIMS its own cover spot near the stage anchor,
        // chosen among the route-book nodes (so it is reachable) within a radius of it: the one farthest from the claims and bodies of its mates (a spot a mate already holds counts
        // fully, a mate standing there counts a little less), with a small pull toward the anchor so the group still holds the choke between them. The claim lasts the stage.
        readonly Dictionary<string, List<int[]>> _footCand = new Dictionary<string, List<int[]>>();

        List<int[]> FootCandidates(string area, int ax, int ay, int r)
        {
            string key = area + ":" + ax + "," + ay + "," + r;
            List<int[]> list;
            if (_footCand.TryGetValue(key, out list)) return list;
            list = new List<int[]>();
            int anchorArea = AreaOf(ax, ay);
            try
            {
                if (_m.Routes != null)
                    for (int n = 0; n < _m.Routes.N; n++)
                    {
                        if (_m.Routes.IsGrid(n)) continue;
                        int tx = _m.Routes.X[n] >> 4, ty = _m.Routes.Y[n] >> 4;
                        if ((tx - ax) * (tx - ax) + (ty - ay) * (ty - ay) > r * r || AreaOf(tx, ty) != anchorArea) continue;
                        if (!_m.BodyFits(tx * 16 + 8, ty * 16 + 8)) continue;
                        list.Add(new int[] { tx, ty });
                    }
                if (list.Count < 4)
                    for (int dy = -r; dy <= r; dy += 3)
                        for (int dx = -r; dx <= r; dx += 3)
                        {   // a thin route book here: open tiles on a 3-tile lattice that see the anchor (the same room)
                            int tx = ax + dx, ty = ay + dy;
                            if (dx * dx + dy * dy > r * r || AreaOf(tx, ty) != anchorArea || !_m.BodyFits(tx * 16 + 8, ty * 16 + 8)) continue;
                            if (!_m.Los(ax * 16 + 8, ay * 16 + 8, tx * 16 + 8, ty * 16 + 8)) continue;
                            list.Add(new int[] { tx, ty });
                        }
            }
            catch (Exception ex) { _m.WarnOnce("[MiniTP] foothold candidates: " + ex.Message); }
            list.Add(new int[] { ax, ay });
            _footCand[key] = list;
            return list;
        }

        /// <summary>The raider's own cover spot for the stage it is in; false = no such point (the caller falls back to the old random tile round the anchor).</summary>
        bool Foothold(CTFXBot b, MiniBot mb, string area, string point, int stage, int r, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            int ax, ay;
            if (!Pt(area, point, out ax, out ay)) return false;
            int key = stage * 16 + 1;
            if (mb.FootStage == key && mb.FootX != 0) { tx = mb.FootX; ty = mb.FootY; return true; }
            List<int[]> cand = FootCandidates(area, ax, ay, r);
            int bx = ax, by = ay;
            double bs = -1e9;
            int px = b._state.positionX >> 4, py = b._state.positionY >> 4;
            foreach (int[] c in cand)
            {
                double near = 12.0;                                       // tiles to the closest mate's claim or body
                foreach (CTFXBot o in _m.LiveBots)
                {
                    if (o == b || o.IsDead || o.Mini == null || o.Mini.Side != mb.Side || o.Mini.Phase != MiniBot.Raid) continue;
                    MiniBot om = o.Mini;
                    double dx, dy, d;
                    if (om.FootStage >= 0 && om.FootX != 0) { dx = c[0] - om.FootX; dy = c[1] - om.FootY; d = Math.Sqrt(dx * dx + dy * dy); if (d < near) near = d; }
                    dx = c[0] - (o._state.positionX >> 4); dy = c[1] - (o._state.positionY >> 4); d = Math.Sqrt(dx * dx + dy * dy) * 1.15;
                    if (d < near) near = d;
                }
                double toAnchor = Math.Sqrt((c[0] - ax) * (c[0] - ax) + (c[1] - ay) * (c[1] - ay));
                double toMe = Math.Sqrt((c[0] - px) * (c[0] - px) + (c[1] - py) * (c[1] - py));
                double score = Math.Min(near, 9.0) - 0.15 * toAnchor - 0.04 * toMe + _m.Rng.NextDouble() * 1.5;
                if (score > bs) { bs = score; bx = c[0]; by = c[1]; }
            }
            mb.FootStage = key; mb.FootX = bx; mb.FootY = by;
            tx = bx; ty = by;
            return true;
        }

        /// <summary>A raider that hunts an enemy in the base comes at him on its OWN bearing and stops 6-9 tiles short with a line on him, not onto his tile with everybody else.</summary>
        void Flank(CTFXBot b, MiniBot mb, int fx, int fy, out int tx, out int ty)
        {
            int bx = b._state.positionX >> 4, by = b._state.positionY >> 4;
            double dir = Math.Atan2(by - fy, bx - fx);                   // from the foe toward the bot
            for (int k = 0; k < 8; k++)
            {
                double ang = dir + mb.Bearing * (k < 4 ? 1.0 : 0.45);
                double dist = (k < 4 ? 8.5 : 6.0) - (k & 3) * 1.2 + _m.Rng.NextDouble() * 1.5;
                int ax = fx + (int)Math.Round(Math.Cos(ang) * dist), ay = fy + (int)Math.Round(Math.Sin(ang) * dist);
                if (AreaOf(ax, ay) != AreaOf(fx, fy) || !_m.BodyFits(ax * 16 + 8, ay * 16 + 8)) continue;
                if (!_m.Los(ax * 16 + 8, ay * 16 + 8, fx * 16 + 8, fy * 16 + 8)) continue;
                tx = ax; ty = ay;
                return;
            }
            Around(fx, fy, 1, out tx, out ty);
        }

        bool RaidGoal(CTFXBot b, MiniBot mb, int now, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            mb.Hunting = false; mb.Hold = false;
            if (_allIn == mb.Side && AllInGoal(b, mb, now, out tx, out ty)) return true;      // the other team holds every flag: go and take one
            if (CarrierGoal(b, mb, now, out tx, out ty)) return true;
            if (GrabGoal(b, mb, now, out tx, out ty)) return true;                            // 11-11 (Travis: "bots don't prioritise capturing the flag when it's available"): a free enemy flag here is taken                         // Travis 11-08: carrier first - the enemy carrying a flag here (the heavy and the SL only when he is within 900 px)
            string foe = FoeArea[mb.Side];
            int foeArea = AreaOf(b._state.positionX >> 4, b._state.positionY >> 4);
            // heavies hold the BOX (the turret gauntlet) and the SL stays at its far anchor; they stand to fire, so these are the holds
            if (mb.Role == "OHVY")
            {
                mb.Hold = true; if (!mb.Said) { mb.Said = true; Talk(b, 0.8, 24, "At Enemy", "| Attacking Enemy [{c}] |"); }
                if (TurretStandoff(b, mb, now, out tx, out ty)) return true;                    // Travis 11-08: shoots the turrets from the safest angle it can find, and keeps firing
                return Anchor(foe, "BOX", 4, out tx, out ty);
            }
            if (mb.Role == "SL" && now - mb.RelocUntil < 0) { mb.Hold = false; tx = mb.RelocX; ty = mb.RelocY; return true; }      // out of the enemy disruptor's range first
            if (mb.Role == "SL") { mb.Hold = true; if (!mb.Said) { mb.Said = true; Talk(b, 0.9, 24, "@%24 Enemy -- Taking Summons --[{c}]--".Replace("%24", ""), "@ Enemy -- Taking Summons --[{c}]--"); } return Anchor(foe, "SL", 3, out tx, out ty); }
            if (mb.Role == "MED" && Escort(b, out tx, out ty)) { mb.Hunting = false; return true; }      // an attack Medic stays with the raiders (the heal reaches 500 px)
            if (mb.Spot >= 0 && PortalGoal(b, mb, now, out tx, out ty)) return true;          // the green portal builder
            // everyone else: an enemy in the base is hunted
            int fx, fy;
            if (NearestFoe(b, foeArea, 1400.0, out fx, out fy)) { Flank(b, mb, fx >> 4, fy >> 4, out tx, out ty); mb.Hunting = true; return true; }
            if (now - mb.StageUntil >= 0 && mb.Stage < 2)
            {
                mb.Stage++;
                mb.StageUntil = now + 12000 + _m.Rng.Next(18000);
            }
            if (mb.Stage == 0) { mb.Hold = _m.Rng.Next(3) == 0; if (Foothold(b, mb, foe, "BOX", 0, 12, out tx, out ty)) return true; return Anchor(foe, "BOX", 5, out tx, out ty); }
            if (mb.Stage == 1) { string mp = foe == "A7" ? "mouth" : "chanBot"; if (Foothold(b, mb, foe, mp, 1, 9, out tx, out ty)) return true; return Anchor(foe, mp, 3, out tx, out ty); }      // A7: the flag-room mouth; D7: the foot of the channel
            // the base is theirs: sweep it (flags, turret line, anchors) instead of standing in the flag room
            return Anchor(foe, RoamPoints[_m.Rng.Next(RoamPoints.Length)], 3, out tx, out ty);
        }
    }
}
/* PARSER GUARD - keep last */
