// CTF:X server-side bots - TURRETS (Travis 10-11): engineer bots build each base end's turret sets inside the zone's computer
// limits; turrets fight bots; bots fight turrets. LOCAL ONLY, like the other CTFXBot files.
//
// Sets come from CTFXBots.txt turret=<end>,<set>,<role>,<x>,<y> lines (made by scratchpad turrets/place.mts: per set 2 MG,
// 1 rocket, 2 plasma, 1 sentry, forward to back by walking distance from the base's hall, the build order replayed through the
// limits). An engineer slot of a group takes its end's sets in order (the first engineer the first set ...), walks to the
// next missing turret, holds still for the maker's prefire, and the turret appears on its tile. A lost turret is rebuilt after
// 20 s. Roles: mg = AutoGun MG 299 -> Auto Turret-MG 400, rocket = 301 -> 401, plasma = 300 -> 700, sentry = Sentry 23 -> 402.
//
// Limits are the engine's own (ScriptArena.handlePlayerMakeVehicle) from the turret's ctfpl.veh row, counted over EVERY computer
// of the team in the arena, players' turrets included: per builder and type (MaxTypeByPlayerRegardlessOfTeam), team total
// (FrequencyMaxActive) and per type (FrequencyMaxType), and around the new turret within DensityRadius
// (FrequencyDensityMaxActive / FrequencyDensityMaxType). A refused build waits 15 s and tries again; the reason is logged.
//
// Engine turrets only ever target players (Computer.getClosestValidTarget). Travis 10-21: "same target swapping logic as human to
// human, which is the closest target". Every 50 ms each turret weighs its nearest valid player (the engine's own pick and tests,
// EngineTarget: an enemy within fire radius, in the weight band and angle, in sight when ObeyLos) against its nearest enemy bot
// inside its fire radius (NearestBot: line of sight when ObeyLos; a cloaked stormer only up close) and shoots the NEARER one.
// The engine's shot timers are its own, but a vehicle's anti-fire / anti-rotate ticks are public: while a bot is nearer than the
// player, the script holds the engine off for 120 ms at a time (refreshed each tick), keeps the turret's aim on the bot, and fires
// the engine's lead angle through the engine's own route (Update_RouteComputer: every client draws the shot) on the gun's own
// timing; the projectile then flies in CTFXShots, where turrets are bodies too. When the player is nearer the hold lapses and the
// engine fires at him, not before the script's own next shot was due (no free extra shot on a switch). An anti-fire value the
// script did not write (an EMP hit) is honoured. (Before 10-21 any enemy player in TRACKING range idled the turret against bots.)

using System;
using System.Collections.Generic;

using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_CTF
{
    /// <summary>One planned turret of a base end's set.</summary>
    internal sealed class CTFXTurretSpot
    {
        public string End, Set, Role;
        public int Tx, Ty;
        public int Maker, VehType;
        public ushort TurretId;              // the live turret on this spot, 0 = none
        public int ReadyAt;                  // no (re)build before this tick
        public bool EverBuilt;               // a turret stood here before (the next one is a rebuild)
    }

    internal sealed class CTFXTurrets
    {
        const int RebuildMs = 20000, RefusedMs = 15000;

        readonly CTFXBotManager _m;
        internal readonly List<CTFXTurretSpot> Spots = new List<CTFXTurretSpot>();
        internal readonly List<Computer> Live = new List<Computer>();         // every live computer in the arena (flight sim bodies)
        readonly Dictionary<ushort, CTFXSlot> _builtBy = new Dictionary<ushort, CTFXSlot>();
        readonly Dictionary<ushort, int[]> _gun = new Dictionary<ushort, int[]>();   // turret id -> { next shot tick, rounds left in clip }
        int _lastFight;
        internal int Built, Refused, Lost, ShotsAtBots, Kills, ShotsAtTurrets;
        internal bool SelfTest;                // turretSelfTest=1: 60 s after a start one standing turret is knocked down (rebuild check)
        int _selfTestAt, _testAt;
        CTFXTurretSpot _testSpot;

        static readonly Dictionary<string, int[]> Roles = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "mg", new int[] { 299, 400 } }, { "rocket", new int[] { 301, 401 } }, { "plasma", new int[] { 300, 700 } }, { "sentry", new int[] { 23, 402 } }
        };

        internal CTFXTurrets(CTFXBotManager m) { _m = m; }

        /// <summary>The configured spots (turret= lines: end, set, role, tile x, tile y).</summary>
        internal void Load(List<string[]> lines)
        {
            Spots.Clear();
            foreach (string[] f in lines)
            {
                int[] r;
                int x, y;
                if (f.Length < 5 || !Roles.TryGetValue(f[2].Trim(), out r) || !int.TryParse(f[3].Trim(), out x) || !int.TryParse(f[4].Trim(), out y)) continue;
                CTFXTurretSpot s = new CTFXTurretSpot();
                s.End = f[0].Trim().ToLowerInvariant(); s.Set = f[1].Trim().ToLowerInvariant(); s.Role = f[2].Trim().ToLowerInvariant();
                s.Tx = x; s.Ty = y; s.Maker = r[0]; s.VehType = r[1];
                Spots.Add(s);
            }
        }

        /// <summary>The biggest damage radius (px) of a grenade's blast, following its explode items.</summary>
        static double BlastRadius(int itemId)
        {
            double best = 0;
            for (int depth = 0; depth < 3 && itemId > 0; depth++)
            {
                ItemInfo.Projectile p = AssetManager.Manager.getItemByID(itemId) as ItemInfo.Projectile;
                if (p == null) break;
                best = Math.Max(best, Math.Max(Math.Max(p.kineticDamageRadius, p.explosiveDamageRadius), Math.Max(p.electronicDamageRadius, p.psionicDamageRadius)));
                itemId = p.explodeItem;
            }
            return best;
        }

        /// <summary>
        /// 10-17 (Travis: Quill 96's turrets stood so close that one grenade hurt them all): no two turrets of one end stand within a
        /// frag grenade's blast of each other. A spot too close to an earlier one moves to the nearest open tile that clears every
        /// kept spot (within 260 px of where it was), or is dropped; the sets keep their order, so the forward turrets still come first.
        /// </summary>
        internal void Spread()
        {
            double sep = Math.Max(120.0, Math.Min(260.0, BlastRadius(1011) + 24.0));
            int moved = 0, dropped = 0;
            List<CTFXTurretSpot> kept = new List<CTFXTurretSpot>();
            foreach (CTFXTurretSpot s in Spots)
            {
                if (Clear(kept, s.End, s.Tx * 16 + 8, s.Ty * 16 + 8, sep)) { kept.Add(s); continue; }
                bool placed = false;
                for (double r = sep; r <= sep + 100.0 && !placed; r += 16.0)
                    for (int a = 0; a < 16 && !placed; a++)
                    {
                        double ang = a * Math.PI / 8.0;
                        int px = s.Tx * 16 + 8 + (int)(Math.Cos(ang) * r), py = s.Ty * 16 + 8 + (int)(Math.Sin(ang) * r);
                        if (_m.BlockedPx(px, py) || _m.BlockedPx(px + 16, py) || _m.BlockedPx(px - 16, py) || _m.BlockedPx(px, py + 16) || _m.BlockedPx(px, py - 16)) continue;
                        if (!Clear(kept, s.End, px, py, sep)) continue;
                        s.Tx = px >> 4; s.Ty = py >> 4; placed = true;
                    }
                if (placed) { kept.Add(s); moved++; } else dropped++;
            }
            if (moved + dropped > 0) { Spots.Clear(); Spots.AddRange(kept); }
            _m.Note("turret spread: spots " + (kept.Count + dropped) + ", at least " + (int)sep + " px apart per end; moved " + moved + ", dropped " + dropped);
        }

        static bool Clear(List<CTFXTurretSpot> kept, string end, int px, int py, double sep)
        {
            foreach (CTFXTurretSpot k in kept)
            {
                if (k.End != end) continue;
                double dx = k.Tx * 16 + 8 - px, dy = k.Ty * 16 + 8 - py;
                if (dx * dx + dy * dy < sep * sep) return false;
            }
            return true;
        }

        internal string Summary()
        {
            Dictionary<string, int> n = new Dictionary<string, int>();
            foreach (CTFXTurretSpot s in Spots) { int c; n.TryGetValue(s.End + "/" + s.Set, out c); n[s.End + "/" + s.Set] = c + 1; }
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, int> kv in n) parts.Add(kv.Key + " " + kv.Value);
            return "turret sets: " + (parts.Count == 0 ? "none (no turret= lines)" : string.Join(", ", parts.ToArray()));
        }

        /// <summary>Forget every turret (bots stopped): the ones bots built are taken down.</summary>
        internal void Clear()
        {
            foreach (ushort id in new List<ushort>(_builtBy.Keys))
            {
                Computer c = Find(id);
                if (c != null && !c.IsDead) { try { c.destroy(true); } catch (Exception) { } }
            }
            _builtBy.Clear(); _gun.Clear(); Live.Clear(); _holdFire.Clear(); _holdRot.Clear(); _mode.Clear();
            Switches = 0; HeldTicks = 0; EngineTurns = 0; _switchNotes = 0;
            foreach (CTFXTurretSpot s in Spots) { s.TurretId = 0; s.ReadyAt = 0; }
            Built = 0; Refused = 0; Lost = 0; ShotsAtBots = 0; Kills = 0; ShotsAtTurrets = 0;
            _selfTestAt = Environment.TickCount + 60000;
        }

        // ---- turret guards (Travis 10-13: bots by their turrets repulse grenades and other splash fire that would hurt them).
        // Threats: live bot shots (CTFXShots) and human shots seen in their player updates (an item use with a blast radius,
        // straight-line from where it left), each checked for a closest approach to a turret within 100 px + its blast in the next
        // 1.5 s. The nearest teammate within 350 px that carries a Repulsor Charge throws it at the turret: the burst shoves the
        // shots off course (CTFXShots.Repel here; each client applies the same burst to the shots it simulates itself).
        sealed class Threat { public Team Team; public double X, Y, Vx, Vy; public int Until, Blast; }
        readonly List<Threat> _threats = new List<Threat>();
        int _lastGuard;
        internal int Guards, HumanThreats;

        /// <summary>Arena thread: a human used item id at (x, y) facing yaw: a threat when it is a projectile with a blast.</summary>
        internal void NoteHumanShot(Team team, int x, int y, byte yaw, int itemId)
        {
            ItemInfo it = AssetManager.Manager.getItemByID(itemId);
            ItemInfo.MultiUse mu = it as ItemInfo.MultiUse;
            if (mu != null && mu.childItems != null && mu.childItems.Count > 0) it = AssetManager.Manager.getItemByID(mu.childItems[0].id);
            ItemInfo.Projectile p = it as ItemInfo.Projectile;
            if (p == null) return;
            int blast = Helpers.getMaxBlastRadius(p);
            if (blast <= 0) return;
            double r = yaw * 1.5 * Math.PI / 180.0, v = Math.Max(1.0, p.muzzleVelocity / 1000.0);
            Threat t = new Threat();
            t.Team = team; t.X = x; t.Y = y; t.Vx = Math.Sin(r) * v; t.Vy = -Math.Cos(r) * v * 0.7; t.Blast = blast;
            t.Until = Environment.TickCount + 2500;
            if (_threats.Count < 200) _threats.Add(t);
            HumanThreats++;
        }

        internal void GuardStep(int now)
        {
            if (now - _lastGuard < 250 || Live.Count == 0) return;
            _lastGuard = now;
            _threats.RemoveAll(delegate(Threat t) { return now - t.Until >= 0; });
            List<Threat> all = new List<Threat>(_threats);
            foreach (CTFXShot s in _m.Shots.LiveShots)
            {
                if (s.Life <= 0 || s.P == null || s.Team == null) continue;
                int blast = Helpers.getMaxBlastRadius(s.P);
                if (blast <= 0) continue;
                Threat t = new Threat();
                t.Team = s.Team; t.X = s.X; t.Y = s.Y; t.Vx = s.Vx; t.Vy = s.Vy * 0.7; t.Blast = blast;
                all.Add(t);
            }
            if (all.Count == 0) return;
            foreach (Computer c in Live)
            {
                if (c.IsDead || c._team == null) continue;
                double cx = c._state.positionX, cy = c._state.positionY;
                Threat hit = null;
                foreach (Threat t in all)
                {
                    if (t.Team == c._team) continue;
                    // closest approach of the straight line within the next 150 ticks (1.5 s)
                    double rx = cx - t.X, ry = cy - t.Y, vv = t.Vx * t.Vx + t.Vy * t.Vy;
                    double k = vv > 0.0001 ? Math.Max(0.0, Math.Min(150.0, (rx * t.Vx + ry * t.Vy) / vv)) : 0.0;
                    double ex = t.X + t.Vx * k - cx, ey = t.Y + t.Vy * k - cy;
                    if (ex * ex + ey * ey <= Math.Pow(100 + t.Blast, 2)) { hit = t; break; }
                }
                if (hit == null) continue;
                CTFXBot best = null;
                double bd = 350.0 * 350.0;
                foreach (CTFXBot b in _m.LiveBots)
                {
                    if (b.IsDead || b.Released || b.InDropship || b._team != c._team) continue;
                    double dx = b._state.positionX - cx, dy = b._state.positionY - cy, d = dx * dx + dy * dy;
                    if (d < bd && b.CanRepulse(now)) { bd = d; best = b; }
                }
                if (best != null && best.GuardRepulse(now, (int)cx, (int)cy)) Guards++;
            }
        }

        /// <summary>The engineer kit: the most damaged live turret of its team within the kit range (120 px) gets the repair.</summary>
        internal bool RepairNear(CTFXBot b, ItemInfo.RepairItem kit)
        {
            int r = kit.repairDistance > 0 ? kit.repairDistance : 120;
            Computer best = null;
            double worst = 0.8;                                                   // 11-08: only a turret that is really hurt is worth 5 Titanium Oxide
            foreach (Computer c in Live)
            {
                if (c.IsDead || c._team != b._team) continue;
                if (c._type.Id == 402) continue;                                   // 11-08 (Travis): "not heal sentries with titanium oxide": a sentry is a free resource, it is rebuilt not repaired
                double dx = c._state.positionX - b._state.positionX, dy = c._state.positionY - b._state.positionY;
                if (dx * dx + dy * dy > (double)r * r) continue;
                double f = (double)c._state.health / Math.Max(1, c._type.Hitpoints);
                if (f < worst) { worst = f; best = c; }
            }
            if (best == null || !b.SpendEnergy(kit.id)) return false;
            best._state.health = (short)Math.Min(best._type.Hitpoints, best._state.health + kit.repairAmount);
            return true;
        }

        /// <summary>The turret of this spot is gone (never built, destroyed, or not in the arena any more).</summary>
        internal bool TurretGone(CTFXTurretSpot s)
        {
            if (s == null || s.TurretId == 0) return true;
            Computer c = Find(s.TurretId);
            return c == null || c.IsDead;
        }

        Computer Find(ushort id)
        {
            foreach (Computer c in Live) if (c._id == id) return c;
            foreach (Vehicle v in _m.Arena.Vehicles) if (v._id == id) return v as Computer;
            return null;
        }

        // ================================================================ per poll
        /// <summary>Every live computer, and the spots whose turret died (rebuild after 20 s).</summary>
        internal void Refresh(int now)
        {
            Live.Clear();
            foreach (Vehicle v in _m.Arena.Vehicles)
            {
                Computer c = v as Computer;
                if (c != null && !c.IsDead) Live.Add(c);
            }
            if (SelfTest && _selfTestAt != 0 && now - _selfTestAt >= 0)
            {
                _selfTestAt = 0;
                foreach (CTFXTurretSpot s in Spots)
                {
                    if (s.TurretId == 0) continue;
                    Computer c = Find(s.TurretId);
                    if (c == null) continue;
                    _m.Note("turret self-test: knocked down the " + s.Role + " of " + s.End + " " + s.Set + " at " + s.Tx + "," + s.Ty + "; its engineer should rebuild it in about 20 s");
                    _testSpot = s; _testAt = now;
                    try { c.kill(null); } catch (Exception) { }
                    break;
                }
            }
            foreach (CTFXTurretSpot s in Spots)
            {
                if (s.TurretId == 0) continue;
                bool alive = false;
                foreach (Computer c in Live) if (c._id == s.TurretId) { alive = true; break; }
                if (alive) continue;
                _builtBy.Remove(s.TurretId); _gun.Remove(s.TurretId); _holdFire.Remove(s.TurretId); _holdRot.Remove(s.TurretId); _mode.Remove(s.TurretId);
                s.TurretId = 0; s.ReadyAt = now + (s.End == "brown-top" ? 12000 : RebuildMs);
                Lost++;
            }
        }

        // ================================================================ engineers
        /// <summary>The engineer's sets: its end's sets in config order; engineer k takes set k (one engineer takes them all).</summary>
        List<string> SetsOf(CTFXBot b)
        {
            List<string> sets = new List<string>();
            string end = b.Group != null ? b.Group.HomeEnd : null;
            if (end == null) return sets;
            foreach (CTFXTurretSpot s in Spots) if (s.End == end && !sets.Contains(s.Set)) sets.Add(s.Set);
            int engineers = 0;
            foreach (CTFXSlot o in b.Group.Slots) if (o.Style == CTFXStyle.Engineer) engineers++;
            if (engineers <= 1 || sets.Count <= 1 || b.Slot == null) return sets;
            List<string> mine = new List<string>();
            for (int i = 0; i < sets.Count; i++) if (i % engineers == b.Slot.StyleIndex % engineers) mine.Add(sets[i]);
            return mine;
        }

        /// <summary>
        /// The engineer's next job: the first missing turret of its sets that may be built now (null = all standing). Its goal is
        /// that spot; with nothing to build it keeps near its first set.
        /// </summary>
        internal CTFXTurretSpot NextJob(CTFXBot b, int now, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            List<string> sets = SetsOf(b);
            CTFXTurretSpot guard = null;
            foreach (CTFXTurretSpot s in Spots)
            {
                if (s.End != (b.Group != null ? b.Group.HomeEnd : null) || !sets.Contains(s.Set)) continue;
                if (guard == null) guard = s;
                if (s.TurretId != 0 || now - s.ReadyAt < 0) continue;
                tx = s.Tx; ty = s.Ty;
                return s;
            }
            if (guard != null) { tx = guard.Tx + _m.Rng.Next(-3, 4); ty = guard.Ty + _m.Rng.Next(-3, 4); }
            return null;
        }

        internal bool HasSets(CTFXBot b) { return SetsOf(b).Count > 0; }

        /// <summary>The engineer has a turret of its sets to (re)build right now (CTFXCommand leaves it to the job).</summary>
        internal bool JobWaiting(CTFXBot b, int now)
        {
            int tx, ty;
            return HasSets(b) && NextJob(b, now, out tx, out ty) != null;
        }

        /// <summary>The engineer finished its build at spot s: the engine's limit checks, then the turret appears on its tile.</summary>
        internal bool TryBuild(CTFXBot b, CTFXTurretSpot s, int now)
        {
            if (s.TurretId != 0) return false;
            VehInfo.Computer info = AssetManager.Manager.getVehicleByID(s.VehType) as VehInfo.Computer;
            if (info == null) { _m.Note("turret type " + s.VehType + " is not a computer vehicle"); s.ReadyAt = now + 60000; return false; }
            int x = s.Tx * 16 + 8, y = s.Ty * 16 + 8;
            string why = Refusal(b, info, x, y);
            if (why != null)
            {
                Refused++;
                s.ReadyAt = now + RefusedMs;
                _m.Note("turret refused (" + s.End + " " + s.Set + " " + s.Role + "): " + why);
                return false;
            }
            Helpers.ObjectState st = new Helpers.ObjectState();
            st.positionX = (short)x; st.positionY = (short)y;
            st.yaw = (byte)_m.Rng.Next(240);
            Vehicle v = null;
            try { v = _m.Arena.newVehicle(info, b._team, null, st); } catch (Exception ex) { _m.Note("turret build failed: " + ex.Message); }
            if (v == null) { s.ReadyAt = now + RefusedMs; return false; }
            bool rebuilt = s.EverBuilt;
            s.EverBuilt = true;
            s.TurretId = v._id;
            if (b.Slot != null) _builtBy[v._id] = b.Slot;
            _m.Chat.TurretUp(b, s.End, s.Role, rebuilt);
            if (s == _testSpot) { _m.Note("turret self-test: the " + s.Role + " of " + s.End + " " + s.Set + " was rebuilt " + ((now - _testAt) / 1000) + " s after it went down"); _testSpot = null; }
            Built++;
            if (Built <= 3) _m.Note("turret #" + Built + ": a " + b._team._name + " engineer built a " + s.Role + " in " + s.End + " " + s.Set + " at " + s.Tx + "," + s.Ty);
            return true;
        }

        /// <summary>handlePlayerMakeVehicle's checks for a turret of info at (x, y) px built by bot b (null = allowed).</summary>
        string Refusal(CTFXBot b, VehInfo.Computer info, int x, int y)
        {
            int mine = 0, team = 0, teamType = 0, dens = 0, densType = 0;
            long r2 = (long)info.DensityRadius * info.DensityRadius;
            foreach (Vehicle v in _m.Arena.Vehicles)
            {
                Computer c = v as Computer;
                if (c == null || c.IsDead) continue;
                CTFXSlot by;
                if (c._type.Id == info.Id && b.Slot != null && _builtBy.TryGetValue(c._id, out by) && by == b.Slot) mine++;
                if (c._team != b._team) continue;
                team++;
                bool same = c._type.Name == info.Name;
                if (same) teamType++;
                long dx = c._state.positionX - x, dy = c._state.positionY - y;
                if (dx * dx + dy * dy <= r2) { dens++; if (same) densType++; }
            }
            if (info.MaxTypeByPlayerRegardlessOfTeam != -1 && mine >= info.MaxTypeByPlayerRegardlessOfTeam) return "its engineer already owns " + mine + " (max " + info.MaxTypeByPlayerRegardlessOfTeam + ")";
            if (info.FrequencyMaxActive != -1 && team >= info.FrequencyMaxActive) return "the team has " + team + " computers (max " + info.FrequencyMaxActive + ")";
            if (info.FrequencyMaxType != -1 && teamType >= info.FrequencyMaxType) return "the team has " + teamType + " of this type (max " + info.FrequencyMaxType + ")";
            if (info.FrequencyDensityMaxActive != -1 && dens >= info.FrequencyDensityMaxActive) return dens + " team computers within " + info.DensityRadius + " px (max " + info.FrequencyDensityMaxActive + ")";
            if (info.FrequencyDensityMaxType != -1 && densType >= info.FrequencyDensityMaxType) return densType + " of this type within " + info.DensityRadius + " px (max " + info.FrequencyDensityMaxType + ")";
            return null;
        }

        // ================================================================ turrets fight bots (the nearer target, bot or player)
        const int HoldMs = 120;                                                     // > the 50 ms Fight tick: the hold never gaps
        readonly Dictionary<ushort, int> _holdFire = new Dictionary<ushort, int>(); // turret id -> the _tickAntiFire value the script wrote
        readonly Dictionary<ushort, int> _holdRot = new Dictionary<ushort, int>();  // turret id -> the _tickAntiRotate value the script wrote
        readonly Dictionary<ushort, byte> _mode = new Dictionary<ushort, byte>();   // turret id -> its last target kind (1 bot, 2 player)
        internal int Switches, HeldTicks, EngineTurns;                              // stats: target switches, ticks the engine was held, ticks it had the nearer player
        int _switchNotes;

        internal void Fight(int now)
        {
            if (now - _lastFight < 50) return;
            _lastFight = now;
            foreach (Computer c in Live)
            {
                if (c._primaryGun == null || c._primaryProjectile == null || c._team == null || c._team._id == -1) continue;
                if (c._state.health < c._type.HitpointsRequiredToOperate) continue;
                int mine;
                bool ownHold = _holdFire.TryGetValue(c._id, out mine) && c._tickAntiFire == mine;
                if (!ownHold && now - c._tickAntiFire < 0) continue;              // a real anti-fire hit (EMP ...): the gun is out
                int[] g;
                if (!_gun.TryGetValue(c._id, out g)) { g = new int[] { now, Clip(c) }; _gun[c._id] = g; }
                int delay, reload;
                Timing(c, out delay, out reload);
                double pd2, bd2;
                Player p = EngineTarget(c, out pd2);
                CTFXBot t = NearestBot(c, out bd2);
                if (t == null || (p != null && pd2 <= bd2))
                {   // no bot, or the player is the nearer: the engine's turn (its own aim, timing and shot)
                    if (ownHold) Release(c, g, now);
                    if (p != null)
                    {
                        EngineTurns++;
                        if (g[0] - (now + delay) < 0) g[0] = now + delay;           // it may have just fired: no free script shot on a switch back
                        TrackSwitch(c, 2, p, pd2, t, bd2);
                    }
                    continue;
                }
                // the bot is the nearer, or no player is a valid target
                if (p != null)
                {   // hold the engine off its player; refreshed every tick while the bot stays nearer
                    int until = now + HoldMs, rmine;
                    c._tickAntiFire = until; _holdFire[c._id] = until;
                    if (now - c._tickAntiRotate >= 0 || (_holdRot.TryGetValue(c._id, out rmine) && c._tickAntiRotate == rmine))
                    { c._tickAntiRotate = until; _holdRot[c._id] = until; }
                    HeldTicks++;
                }
                TrackSwitch(c, 1, p, pd2, t, bd2);
                byte angle = Helpers.computeLeadFireAngle(c._state, t._state, c._primaryProjectile.muzzleVelocity / 1000.0);
                c._state.fireAngle = angle;                                         // the aim stays on the bot between shots too
                if (now - g[0] < 0) continue;
                c._shouldFire = true;
                c._state.updateNumber++;
                Helpers.Update_RouteComputer(c);                                    // the clients draw the shot (and it clears the flag)
                _m.Shots.FireFrom(c, c._team, c._primaryGun.id, angle);
                ShotsAtBots++;
                t.TurretThreat = c; t.TurretThreatAt = now;                         // the bot answers it (CTFXBot.ScanTargets)
                // the gun's own timing: the fire delay between shots, the reload after a clip
                if (g[1] > 0 && --g[1] <= 0) { g[1] = Clip(c); g[0] = now + Math.Max(delay, reload); }
                else g[0] = now + delay;
            }
        }

        /// <summary>The script's hold ends (the player is now the nearer): the engine's anti-fire goes to the script's own next shot time
        /// when that is later - so the switch gives no free extra shot - else off; its anti-rotate goes off if the value is the script's.</summary>
        void Release(Computer c, int[] g, int now)
        {
            if (g[0] - now > 0) { c._tickAntiFire = g[0]; _holdFire[c._id] = g[0]; }
            else { c._tickAntiFire = now - 1; _holdFire.Remove(c._id); }
            int rmine;
            if (_holdRot.TryGetValue(c._id, out rmine) && c._tickAntiRotate == rmine) { c._tickAntiRotate = now - 1; _holdRot.Remove(c._id); }
        }

        /// <summary>Counts a turret's switch between a bot and a player (the first 20 are logged with both distances).</summary>
        void TrackSwitch(Computer c, byte mode, Player p, double pd2, CTFXBot t, double bd2)
        {
            byte last;
            if (_mode.TryGetValue(c._id, out last) && last == mode) return;
            _mode[c._id] = mode;
            if (last == 0) return;                                                  // its first pick, not a switch
            Switches++;
            if (_switchNotes++ >= 20) return;
            string bot = t != null ? (t.Slot != null && t.Slot.Name != null ? t.Slot.Name : "a bot") + " at " + (int)Math.Sqrt(bd2) + " px" : "no bot in range";
            string pl = p != null ? p._alias + " at " + (int)Math.Sqrt(pd2) + " px" : "no valid player";
            _m.Note("turret switch: " + c._type.Name.Trim((char)34, ' ') + " #" + c._id + " -> " + (mode == 1 ? "bot " + bot + " (player: " + pl + ")" : "player " + pl + " (bot: " + bot + ")"));
        }

        static int Clip(Computer c)
        {
            int t, n, cap;
            return c._primaryGun.getAmmoType(out t, out n, out cap) ? cap : 0;
        }

        static void Timing(Computer c, out int delay, out int reload)
        {
            ItemInfo.MultiUse mu = c._primaryGun as ItemInfo.MultiUse;
            if (mu != null) { delay = mu.fireDelay * 10; reload = mu.reloadDelayNormal * 10; }
            else { delay = c._primaryProjectile.fireDelay * 10; reload = c._primaryProjectile.reloadDelayNormal * 10; }
            if (delay < 100) delay = 100;
        }

        /// <summary>The engine's own turret target: Computer.getClosestValidTarget / isValidTarget, the same tests (an enemy, alive, not
        /// spectating, within FireRadius, inside TrackingWeightLow..High, inside AngleStart/AngleLength, not occluded when ObeyLos) -
        /// the nearest such player and his squared distance; null (and MaxValue) when there is none.</summary>
        Player EngineTarget(Computer c, out double d2)
        {
            d2 = double.MaxValue;
            Player best = null;
            List<Player> ps = _m.Arena.getPlayersInRange(c._state.positionX, c._state.positionY, c._type.TrackingRadius);
            if (ps == null) return null;
            foreach (Player p in ps)
            {
                if (p == null || p.IsSpectator || p.IsDead || p._team == c._team || p.ActiveVehicle == null) continue;
                double dx = p._state.positionX - c._state.positionX, dy = p._state.positionY - c._state.positionY, dd = dx * dx + dy * dy;
                if (dd >= d2 || Math.Sqrt(dd) > c._type.FireRadius) continue;
                int w = p.ActiveVehicle._type.Weight;
                if (w < c._type.TrackingWeightLow || w > c._type.TrackingWeightHigh) continue;
                double ang = Helpers.calcDegreesBetweenPoints(p._state.positionX, p._state.positionY, c._state.positionX, c._state.positionY);
                if (c._type.AngleStart > 0 && (short)ang < c._type.AngleStart) continue;
                if (c._type.AngleLength > 0 && c._type.AngleLength < 360 && (short)ang > c._type.AngleStart + c._type.AngleLength) continue;
                if (c._type.ObeyLos != 0 && Occluded(c, p)) continue;
                d2 = dd; best = p;
            }
            return best;
        }

        /// <summary>The engine's Computer.IsPlayerOccluded, the same vision-tile rule along the Bresenham line.</summary>
        bool Occluded(Computer c, Player p)
        {
            int tileSize = 8, tileDistance = 0;                                     // 8 = no vision tile on the line
            List<LvlInfo.Tile> tiles = Helpers.calcBresenhems(_m.Arena, c._state.positionX, c._state.positionY, p._state.positionX, p._state.positionY);
            for (int i = 0; i < tiles.Count; i++)
                if (tiles[i].Vision > 0 && tiles[i].Vision <= tileSize) { tileSize = tiles[i].Vision; tileDistance = i; }
            return !(tileSize == 8 || tiles.Count < Math.Floor(tileSize * 1.8) + tileDistance);
        }

        CTFXBot NearestBot(Computer c, out double bestD2)
        {
            bestD2 = double.MaxValue;
            CTFXBot best = null;
            double bd = (double)c._type.FireRadius * c._type.FireRadius;
            foreach (CTFXBot b in _m.LiveBots)
            {
                if (b.IsDead || b.Released || b._team == c._team) continue;
                int w = b._type.Weight;
                if (w < c._type.TrackingWeightLow || w > c._type.TrackingWeightHigh) continue;
                double dx = b._state.positionX - c._state.positionX, dy = b._state.positionY - c._state.positionY, d2 = dx * dx + dy * dy;
                if (d2 >= bd) continue;
                if (b.Cloaked && d2 > (double)b.CloakRange * b.CloakRange) continue;
                if (c._type.ObeyLos != 0 && !_m.Los(c._state.positionX, c._state.positionY, b._state.positionX, b._state.positionY)) continue;
                bd = d2; best = b;
            }
            if (best != null) bestD2 = bd;
            return best;
        }

        /// <summary>Stats: what stands, what was built, refused, lost, and the turrets' war on bots.</summary>
        internal string Stats()
        {
            if (Spots.Count == 0) return "";
            int up = 0;
            foreach (CTFXTurretSpot s in Spots) if (s.TurretId != 0) up++;
            return " | turrets up " + up + "/" + Spots.Count + " (built " + Built + ", refused " + Refused + ", lost " + Lost + ", shots at bots " + ShotsAtBots
                + ", bot kills " + Kills + ", bot shots at turrets " + ShotsAtTurrets + ", hit by bots " + _m.Shots.TurretHits
                + "; nearest-target switches " + Switches + ", engine held off a farther player " + HeldTicks + " ticks, player-nearer ticks " + EngineTurns + ")";
        }
    }
}
/* PARSER GUARD - keep last */
