// CTF:X defence lines, footholds and resupply (Travis 10-13: "no cheating, organically").
//
// LINES: every base end gets three lines built from the route book: inner (the flag room, about 15 tiles from the flag), mid
//   (about 40) and outer (about 70), one point per route that comes into the end, each watching outward along its route. The team
//   holds one line. Attackers at or past it outnumbering the defenders there (or two defenders dying at it in 20 s) for 3 s: it
//   falls back a line. Quiet at and ahead of it for 10 s: it pushes back up. Frontliners hold the held line; heavies and snipers
//   one line deeper (cover and suppressive fire down the line); engineers and medics the inner line, the hard room by the flag.
// SUPPRESSION: an anchor or defender with no target, an enemy it cannot see within 650 px and its line watch point in sight
//   within 350 px of that enemy: bullets down the watch point, where the attackers have to come through.
// FOOTHOLD: an attacking team with raiders inside the brown building keeps one of them as its foothold: it holds the ground already
//   taken (just outside the defenders line), backs off at 60 % health instead of trading, and summons teammates in when its class
//   can. While it lives its team raids brown more and lands on it (RejoinStep warps onto the teammate nearest the objective).
// RESUPPLY: a bot short of ammo (under 25 %) picks up a pile of its ammo within 450 px; else a nearby teammate with spare ammo walks
//   over and drops a real pile (Ammo MG etc., humans can grab it too); dry with no pile coming it uses DropShip Recall (item 46),
//   the ship store refills it, and a medic or leader near where it left summons it back. The old 5 s refill on the spot is only a
//   25 s fallback now.
using System;
using System.Collections.Generic;
using System.Text;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_CTF
{
    internal sealed partial class CTFXCommand
    {
        sealed class LinePt { public int X, Y, WX, WY; public string Route; }

        sealed class LineSet
        {
            public readonly List<LinePt>[] L = new List<LinePt>[] { new List<LinePt>(), new List<LinePt>(), new List<LinePt>() };
            public int Held, PressSince, QuietSince, Falls, Pushes, HeldMsSum;
            public readonly int[] HeldMs = new int[3];
            public int HeldAt;
            public readonly List<int[]> Deaths = new List<int[]>();          // tick, x, y
        }

        sealed class Foothold { public CTFXSlot Seat; public string End; public int Since, SummonAt; }

        static readonly int[] LineRadius = { 1120, 640, 240 };                // px from the flag: outer, mid, inner
        static readonly string[] LineName = { "outer", "mid", "inner" };

        readonly Dictionary<CTFXTeamCmd, LineSet> _lineSets = new Dictionary<CTFXTeamCmd, LineSet>();

        /// <summary>The line team t holds now: 0 outer, 1 mid, 2 inner (0 when it has no lines).</summary>
        int LineHeldOf(CTFXTeamCmd t) { LineSet ls; return _lineSets.TryGetValue(t, out ls) ? ls.Held : 0; }
        readonly Dictionary<CTFXTeamCmd, Foothold> _foot = new Dictionary<CTFXTeamCmd, Foothold>();
        readonly Dictionary<CTFXSlot, LinePt> _seatPt = new Dictionary<CTFXSlot, LinePt>();
        readonly Dictionary<CTFXSlot, int[]> _alive = new Dictionary<CTFXSlot, int[]>();      // alive flag, last x, last y
        readonly Dictionary<CTFXSlot, int[]> _giver = new Dictionary<CTFXSlot, int[]>();      // needy seat -> giver bot id, deadline
        readonly Dictionary<CTFXSlot, int[]> _recallBack = new Dictionary<CTFXSlot, int[]>(); // recalled seat -> x, y
        int _linesAt, _footAt, _ammoAt;

        internal int SummonAsks, SummonWaitMs, DryOn, Suppressions, FieldRebuys, AmmoPickups, AmmoDrops, Recalls, SummonBacks, WalkBacks;
        internal int FootStarted, FootEnded, FootSummons;
        long _footMs;

        void ClearLines()
        {
            _lineSets.Clear(); _foot.Clear(); _seatPt.Clear(); _alive.Clear(); _giver.Clear(); _recallBack.Clear();
        }

        static double Dist(int ax, int ay, int bx, int by) { double dx = ax - bx, dy = ay - by; return Math.Sqrt(dx * dx + dy * dy); }

        // ================================================================ build
        void BuildLines(CTFXTeamCmd t)
        {
            CTFXRouteBook rb = _m.Routes;
            int fx, fy;
            if (rb == null || !_m.RouteNode(t.End + ":patrol", 0, out fx, out fy)) return;
            int Fx = fx * 16 + 8, Fy = fy * 16 + 8;
            LineSet ls = new LineSet();
            bool shaped = BuildBaseLines(t, ls);                                         // CTFXBases.txt (CTFXBase.cs)
            if (!shaped) foreach (KeyValuePair<string, int[]> kv in rb.Routes)
            {
                string name = kv.Key;
                if (name.IndexOf(t.End, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (name.EndsWith(":patrol", StringComparison.OrdinalIgnoreCase) || name.IndexOf(":spawn", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                int[] nodes = kv.Value;
                if (nodes == null || nodes.Length < 2) continue;
                int n = nodes.Length;
                bool rev = Dist(rb.X[nodes[n - 1]], rb.Y[nodes[n - 1]], Fx, Fy) < Dist(rb.X[nodes[0]], rb.Y[nodes[0]], Fx, Fy);
                int[] o = new int[n];
                for (int k = 0; k < n; k++) o[k] = rev ? nodes[n - 1 - k] : nodes[k];
                if (Dist(rb.X[o[0]], rb.Y[o[0]], Fx, Fy) > 1300) continue;                  // never near this flag
                for (int li = 0; li < 3; li++)
                {
                    int r = LineRadius[li], k0 = -1, kw = n - 1;
                    for (int k = 0; k < n; k++) if (Dist(rb.X[o[k]], rb.Y[o[k]], Fx, Fy) >= r) { k0 = k; break; }
                    if (k0 < 0 || k0 == 0 && li == 2) continue;                              // the route starts outside the inner ring
                    for (int k = k0; k < n; k++) if (Dist(rb.X[o[k]], rb.Y[o[k]], Fx, Fy) >= r + 400) { kw = k; break; }
                    AddLinePt(ls.L[li], rb.X[o[k0]], rb.Y[o[k0]], rb.X[o[kw]], rb.Y[o[kw]], name);
                }
            }
            if (ls.L[2].Count == 0) AddLinePt(ls.L[2], Fx, Fy, Fx, Fy - 300, "flag");
            _lineSets[t] = ls;
            StringBuilder sb = new StringBuilder("[CTFXBots] lines " + t.End + ":");
            for (int li = 0; li < 3; li++)
            {
                sb.Append(' ').Append(LineName[li]);
                foreach (LinePt p in ls.L[li]) sb.Append(' ').Append(p.X >> 4).Append(',').Append(p.Y >> 4);
                sb.Append(';');
            }
            _m.Log(sb.ToString());
        }

        void AddLinePt(List<LinePt> l, int x, int y, int wx, int wy, string route)
        {
            foreach (LinePt p in l) if (Dist(p.X, p.Y, x, y) < 128) return;
            int ox, oy;
            if (_m.NearestOpen(x >> 4, y >> 4, 4, out ox, out oy)) { x = ox * 16 + 8; y = oy * 16 + 8; }
            LinePt q = new LinePt();
            q.X = x; q.Y = y; q.WX = wx; q.WY = wy; q.Route = route;
            l.Add(q);
        }

        // ================================================================ who stands where
        int EnemiesNear(Team us, int x, int y, double r)
        {
            int n = 0;
            double r2 = r * r;
            foreach (CTFXBot o in _m.LiveBots)
            {
                if (o.IsDead || o.Released || o.InDropship || o._team == us) continue;
                double dx = o._state.positionX - x, dy = o._state.positionY - y;
                if (dx * dx + dy * dy <= r2) n++;
            }
            foreach (Player p in _m.Arena.PlayersIngame)
            {
                if (p.IsDead || p._team == us) continue;
                double dx = p._state.positionX - x, dy = p._state.positionY - y;
                if (dx * dx + dy * dy <= r2) n++;
            }
            return n;
        }

        int MatesAt(Team us, int x, int y, double r)
        {
            int n = 0;
            double r2 = r * r;
            foreach (CTFXBot o in _m.LiveBots)
            {
                if (o.IsDead || o.Released || o.InDropship || o._team != us) continue;
                double dx = o._state.positionX - x, dy = o._state.positionY - y;
                if (dx * dx + dy * dy <= r2) n++;
            }
            return n;
        }

        int LineEnemies(Team us, LineSet ls, int from, int to)
        {
            int n = 0;
            for (int li = Math.Max(0, from); li <= Math.Min(2, to); li++) foreach (LinePt p in ls.L[li]) n += EnemiesNear(us, p.X, p.Y, 300.0);
            return n;
        }

        // ================================================================ the held line moves
        void LinesStep(int now)
        {
            if (now - _linesAt < 1000) return;
            _linesAt = now;
            foreach (CTFXTeamCmd t in Teams)
            {
                LineSet ls;
                if (!_lineSets.TryGetValue(t, out ls)) continue;
                foreach (CTFXSlot s in t.Seats)
                {   // deaths of its own seats, where they fell
                    CTFXBot b = _m.GetBot(s.BotId);
                    int[] a;
                    if (!_alive.TryGetValue(s, out a)) { a = new int[3]; _alive[s] = a; }
                    bool alive = b != null && !b.IsDead && !b.Released && !b.InDropship;
                    if (a[0] == 1 && (b == null || b.IsDead)) ls.Deaths.Add(new int[] { now, a[1], a[2] });
                    a[0] = alive ? 1 : 0;
                    if (alive) { a[1] = b._state.positionX; a[2] = b._state.positionY; }
                }
                ls.Deaths.RemoveAll(d => now - d[0] > 20000);
                int held = ls.Held, deathsNear = 0, mine = 0;
                foreach (LinePt p in ls.L[held])
                {
                    mine += MatesAt(t.Team, p.X, p.Y, 300.0);
                    foreach (int[] d in ls.Deaths) if (Dist(d[1], d[2], p.X, p.Y) <= 350) deathsNear++;
                }
                int at = LineEnemies(t.Team, ls, held, 2), ahead = held > 0 ? LineEnemies(t.Team, ls, held - 1, held - 1) : 0;
                ls.HeldMs[held] += 1000;
                if ((at >= 2 && at > mine) || deathsNear >= 2)
                {
                    if (ls.PressSince == 0) ls.PressSince = now;
                    if (now - ls.PressSince >= 3000 && held < 2) { ls.Held++; ls.Falls++; ls.PressSince = 0; ls.Deaths.Clear(); LineMoved(t, ls, true); }
                }
                else ls.PressSince = 0;
                if (ls.Held > 0 && at == 0 && ahead == 0)
                {
                    if (ls.QuietSince == 0) ls.QuietSince = now;
                    if (now - ls.QuietSince >= 10000) { ls.Held--; ls.Pushes++; ls.QuietSince = 0; LineMoved(t, ls, false); }
                }
                else ls.QuietSince = 0;
            }
        }

        void LineMoved(CTFXTeamCmd t, LineSet ls, bool back)
        {
            CTFXBot caller = null;
            foreach (CTFXSlot s in t.Seats)
            {
                CTFXBot b = _m.GetBot(s.BotId);
                if (b == null || b.IsDead || b.Released || (s.Intent.Act != CTFXAct.Anchor && s.Intent.Act != CTFXAct.Defend)) continue;
                b.ForceGoal();
                if (caller == null) caller = b;
            }
            if (caller != null && _m.Chat != null)
                _m.Chat.Say(caller, back ? (ls.Held == 2 ? "fall back to the flag room" : "fall back, hold the next choke") : "push up, they backed off", 0.6, false);
        }

        /// <summary>The line spot of a defender by its class: frontline at the held line, heavies and snipers one deeper, engineers
        /// and medics on the inner line. With an alarm the point nearest the threat, else the busiest, else its own by seat.</summary>
        bool LineGoal(CTFXTeamCmd t, CTFXSlot s, CTFXBot b, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            LineSet ls;
            if (b == null || !_lineSets.TryGetValue(t, out ls)) return false;
            int line = ls.Held, cls = b.ClassId;
            if (cls == CTFXBot.SK_ENGY || cls == CTFXBot.SK_MEDIC) line = 2;
            else if (cls == CTFXBot.SK_HEAVY || s.Style == CTFXStyle.Sniper) line = Math.Min(2, ls.Held + 1);
            while (ls.L[line].Count == 0 && line < 2) line++;
            List<LinePt> l = ls.L[line];
            if (l.Count == 0) return false;
            LinePt best = null;
            if (t.Alarm)
            {
                double bd = double.MaxValue;
                foreach (LinePt p in l) { double d = Dist(p.X, p.Y, t.ThreatX, t.ThreatY); if (d < bd) { bd = d; best = p; } }
            }
            else
            {
                int bn = 0;
                foreach (LinePt p in l) { int e = EnemiesNear(t.Team, p.WX, p.WY, 700.0); if (e > bn) { bn = e; best = p; } }
                if (best == null) best = l[(int)((uint)s.GetHashCode() % (uint)l.Count)];
            }
            _seatPt[s] = best;
            // a step back from the point toward the flag side (cover), a little spread
            double wx = best.WX - best.X, wy = best.WY - best.Y, wd = Math.Max(1.0, Math.Sqrt(wx * wx + wy * wy));
            int back = line == ls.Held ? 24 : 48;
            _m.NearestOpenPx(best.X - (int)(wx / wd * back) + _m.Rng.Next(-40, 41), best.Y - (int)(wy / wd * back) + _m.Rng.Next(-32, 33), out tx, out ty);
            return true;
        }

        /// <summary>Facing at a line spot: down its watch point (for a foothold: at the defenders line), sweeping a little.</summary>
        bool LineYaw(CTFXBot b, int now, out float yaw)
        {
            yaw = 0f;
            LinePt p;
            if (b.Slot == null || !_seatPt.TryGetValue(b.Slot, out p)) return false;
            CTFXAct a = b.Slot.Intent.Act;
            if (a != CTFXAct.Anchor && a != CTFXAct.Defend && !b.Slot.Intent.Foothold) return false;
            double dx = p.WX - b._state.positionX, dy = p.WY - b._state.positionY;
            if (dx * dx + dy * dy < 32 * 32) return false;
            double period = 3000.0 + (b.Slot.Human != null ? b.Slot.Human.Seed % 4000 : 0);
            yaw = CTFXBot.WrapYaw(CTFXBot.YawTo(dx, dy) + (float)(18.0 * Math.Sin(now / period * 2.0 * Math.PI)));
            return true;
        }

        /// <summary>Suppressive fire for an idle line holder: an unseen enemy within 650 px, the watch point in sight and within
        /// 350 px of that enemy: the point to shoot at.</summary>
        internal bool Suppress(CTFXBot b, int now, out int x, out int y)
        {
            x = 0; y = 0;
            LinePt p;
            if (b.Slot == null || b.Slot.Intent == null || !_seatPt.TryGetValue(b.Slot, out p)) return false;
            CTFXAct a = b.Slot.Intent.Act;
            if (a != CTFXAct.Anchor && a != CTFXAct.Defend && !b.Slot.Intent.Foothold) return false;
            bool heavy = b.ClassId == CTFXBot.SK_HEAVY || b.Style == CTFXStyle.Sniper;
            if (!heavy && _m.Rng.NextDouble() > 0.35) return false;
            int bx = b._state.positionX, by = b._state.positionY;
            if (Dist(bx, by, p.WX, p.WY) > 900 || !_m.Los(bx, by, p.WX, p.WY)) return false;
            foreach (CTFXBot o in _m.LiveBots)
            {
                if (o.IsDead || o.Released || o.InDropship || o._team == b._team) continue;
                int ox = o._state.positionX, oy = o._state.positionY;
                if (Dist(bx, by, ox, oy) > 650 || Dist(ox, oy, p.WX, p.WY) > 350 || _m.Los(bx, by, ox, oy)) continue;
                x = p.WX + _m.Rng.Next(-24, 25); y = p.WY + _m.Rng.Next(-24, 25);
                return true;
            }
            foreach (Player o in _m.Arena.PlayersIngame)
            {
                if (o.IsDead || o._team == b._team) continue;
                int ox = o._state.positionX, oy = o._state.positionY;
                if (Dist(bx, by, ox, oy) > 650 || Dist(ox, oy, p.WX, p.WY) > 350 || _m.Los(bx, by, ox, oy)) continue;
                x = p.WX + _m.Rng.Next(-24, 25); y = p.WY + _m.Rng.Next(-24, 25);
                return true;
            }
            return false;
        }

        // ================================================================ footholds in brown
        internal bool HasFoothold(CTFXTeamCmd t, out string end)
        {
            Foothold f;
            end = null;
            if (!_foot.TryGetValue(t, out f)) return false;
            end = f.End;
            return true;
        }

        void FootholdStep(int now)
        {
            if (_m.FocusEnd == null || now - _footAt < 1000) return;
            _footAt = now;
            foreach (CTFXTeamCmd t in Teams)
            {
                if (t.Quad == "brown") continue;
                Foothold f;
                _foot.TryGetValue(t, out f);
                if (f != null)
                {
                    CTFXBot fb = _m.GetBot(f.Seat.BotId);
                    // a seat moved off this team (make room, a clan recruit: CTFXClan.MoveSeat clears its intent) ends its foothold too;
                    // its null intent threw here every second and the foothold was never dropped (stage soak 10-24, 9 poll errors)
                    if (fb == null || fb.IsDead || fb.Released || fb.InDropship || !_m.InQuad("brown", fb) || _m.Flags.Carrying(fb)
                        || f.Seat.Intent == null || !t.Seats.Contains(f.Seat))
                    {
                        _footMs += now - f.Since; FootEnded++;
                        if (f.Seat.Intent != null) { f.Seat.Intent.Foothold = false; f.Seat.Intent.Until = now; }
                        _seatPt.Remove(f.Seat);
                        if (fb != null) fb.ForceGoal();
                        _foot.Remove(t);
                        f = null;
                    }
                }
                if (f == null)
                {
                    CTFXSlot pick = null;
                    double ps = 0;
                    foreach (CTFXSlot s in t.Seats)
                    {
                        CTFXBot b = _m.GetBot(s.BotId);
                        if (b == null || b.IsDead || b.Released || b.InDropship || s.Intent.Act != CTFXAct.Raid || !_m.InQuad("brown", b) || _m.Flags.Carrying(b)) continue;
                        int c = b.ClassId;
                        double sc = b._state.health / (double)Math.Max(1, (int)b._type.Hitpoints)
                            + (c == CTFXBot.SK_HEAVY || c == CTFXBot.SK_ENGY || c == CTFXBot.SK_MEDIC ? 1.0 : 0.0) + (s.Style == CTFXStyle.Guard ? 0.5 : 0.0);
                        if (sc > ps) { ps = sc; pick = s; }
                    }
                    if (pick == null) continue;
                    f = new Foothold();
                    f.Seat = pick; f.End = pick.Intent.RaidEnd ?? _m.FocusEnd; f.Since = now; f.SummonAt = now + 8000;
                    pick.Intent.Foothold = true; pick.Intent.Until = now + 3600000;
                    _foot[t] = f;
                    FootStarted++;
                    CTFXBot pb = _m.GetBot(pick.BotId);
                    pb.ForceGoal();
                    if (_m.Chat != null) _m.Chat.Say(pb, "holding " + f.End.Replace('-', ' ') + ", come in on me", 0.7, false);
                    continue;
                }
                if (now - f.SummonAt >= 0)
                {   // a medic or a leader pulls in one teammate on its way here, far off and not in a fight
                    f.SummonAt = now + 15000 + _m.Rng.Next(10001);
                    CTFXBot fb = _m.GetBot(f.Seat.BotId);
                    bool medic = fb.ClassId == CTFXBot.SK_MEDIC;
                    if (!medic && !_m.Squads.CanSummon(fb, now)) continue;
                    foreach (CTFXSlot s in t.Seats)
                    {
                        CTFXBot b = _m.GetBot(s.BotId);
                        if (b == null || b == fb || b.IsDead || b.Released || b.InDropship || s.Intent.Act != CTFXAct.Raid || b.HasTarget || b.RecentlyHit(now, 4000)) continue;
                        if (Dist(b._state.positionX, b._state.positionY, fb._state.positionX, fb._state.positionY) < 900) continue;
                        if (medic ? _m.Squads.MedicSummonBot(fb, b, now) : _m.Squads.Summon(fb, b, now)) { FootSummons++; b.ForceGoal(); }
                        break;
                    }
                }
            }
        }

        /// <summary>The foothold spot: the ground already taken, just outside the defenders held line, facing it.</summary>
        bool FootholdGoal(CTFXSlot s, CTFXBot b, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            string end = s.Intent.RaidEnd ?? _m.FocusEnd;
            LineSet ls = null;
            foreach (CTFXTeamCmd o in Teams) if (o.End == end && _lineSets.TryGetValue(o, out ls)) break;
            if (ls == null) return false;
            List<LinePt> l = ls.L[Math.Max(0, ls.Held - 1)];
            if (l.Count == 0) return false;
            LinePt best = null;
            double bd = double.MaxValue;
            foreach (LinePt p in l) { double d = Dist(p.X, p.Y, b._state.positionX, b._state.positionY); if (d < bd) { bd = d; best = p; } }
            // held at the outer line already: it stays outside it, on the watch point side; else on the line taken
            int sx = ls.Held == 0 ? best.WX : best.X, sy = ls.Held == 0 ? best.WY : best.Y;
            LinePt me = new LinePt();
            me.X = sx; me.Y = sy; me.WX = best.X; me.WY = best.Y; me.Route = best.Route;
            if (ls.Held == 0) { me.WX = best.X; me.WY = best.Y; }
            else
            {   // facing the next line in
                List<LinePt> inner = ls.L[ls.Held];
                if (inner.Count > 0) { LinePt q = inner[0]; me.WX = q.X; me.WY = q.Y; }
            }
            _seatPt[s] = me;
            _m.NearestOpenPx(sx + _m.Rng.Next(-48, 49), sy + _m.Rng.Next(-40, 41), out tx, out ty);
            return true;
        }

        /// <summary>A hurt foothold backs off a short way (away from what it faces), not home.</summary>
        internal bool FootRetreat(CTFXBot b, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            LinePt p;
            if (b.Slot == null || b.Slot.Intent == null || !b.Slot.Intent.Foothold || !_seatPt.TryGetValue(b.Slot, out p)) return false;
            double dx = b._state.positionX - p.WX, dy = b._state.positionY - p.WY, d = Math.Max(1.0, Math.Sqrt(dx * dx + dy * dy));
            _m.NearestOpenPx(b._state.positionX + (int)(dx / d * 220), b._state.positionY + (int)(dy / d * 220), out tx, out ty);
            return true;
        }

        // ================================================================ resupply
        void AmmoStep(int now)
        {
            if (!_m.Ammo || now - _ammoAt < 1000) return;
            _ammoAt = now;
            foreach (CTFXTeamCmd t in Teams)
                foreach (CTFXSlot s in t.Seats)
                {
                    CTFXBot b = _m.GetBot(s.BotId);
                    if (b == null || b.IsDead || b.Released) { _giver.Remove(s); continue; }
                    if (b.InDropship) { SummonBack(s, b, now); continue; }
                    _recallBack.Remove(s);
                    int aid, start;
                    double frac;
                    if (!b.AmmoNeed(out aid, out frac, out start) || frac >= 0.25) { _giver.Remove(s); continue; }
                    if (b.HasTarget && !b.Rebuying) continue;                         // still shooting: later
                    if (PickupStep(b, aid)) { _giver.Remove(s); continue; }
                    if (GiverStep(t, s, b, aid, start, now)) continue;
                    if ((b.Rebuying || frac < 0.10) && b.Recall(now)) { Recalls++; _recallBack[s] = new int[] { _alive.ContainsKey(s) ? _alive[s][1] : 0, _alive.ContainsKey(s) ? _alive[s][2] : 0 }; }
                }
        }

        /// <summary>A pile of its ammo within 450 px: walk to it, take it within 28 px.</summary>
        bool PickupStep(CTFXBot b, int aid)
        {
            Arena.ItemDrop best = null;
            double bd = 450.0;
            foreach (Arena.ItemDrop d in _m.Arena._items.Values)
            {
                if (d.item == null || d.item.id != aid || d.quantity <= 0) continue;
                double dd = Dist(d.positionX, d.positionY, b._state.positionX, b._state.positionY);
                if (dd < bd) { bd = dd; best = d; }
            }
            if (best == null) return false;
            if (bd <= 28.0)
            {
                _m.Arena._items.Remove(best.id);
                Helpers.Object_ItemDropUpdate(_m.Arena.Players, best.id, 0);
                b.AddAmmo(aid, best.quantity);
                AmmoPickups++;
                b.ForceGoal();
                return true;
            }
            b.GoTo(best.positionX >> 4, best.positionY >> 4);
            return true;
        }

        /// <summary>A teammate within 800 px with spare ammo walks over and drops a pile (60 % of a full stock) beside it.</summary>
        bool GiverStep(CTFXTeamCmd t, CTFXSlot s, CTFXBot b, int aid, int start, int now)
        {
            int[] g;
            CTFXBot gb = null;
            if (_giver.TryGetValue(s, out g))
            {
                gb = _m.GetBot((ushort)g[0]);
                if (gb == null || gb.IsDead || gb.InDropship || now - g[1] >= 0 || gb.SpareDrops <= 0) { _giver.Remove(s); gb = null; }
            }
            if (gb == null)
            {
                double bd = 800.0;
                foreach (CTFXBot o in _m.LiveBots)
                {
                    if (o == b || o.IsDead || o.Released || o.InDropship || o._team != b._team || o.SpareDrops <= 0 || o.HasTarget || _m.Flags.Carrying(o)) continue;
                    if (o.ClassId == CTFXBot.SK_HEAVY) continue;
                    double d = Dist(o._state.positionX, o._state.positionY, b._state.positionX, b._state.positionY);
                    if (d < bd) { bd = d; gb = o; }
                }
                if (gb == null) return false;
                _giver[s] = new int[] { gb._id, now + 15000 };
                if (_m.Chat != null) _m.Chat.Say(b, "need ammo", 0.5, false);
            }
            if (Dist(gb._state.positionX, gb._state.positionY, b._state.positionX, b._state.positionY) > 90)
            {
                gb.GoTo(b._state.positionX >> 4, b._state.positionY >> 4);
                return true;
            }
            ItemInfo it = AssetManager.Manager.getItemByID(aid);
            if (it == null) { _giver.Remove(s); return false; }
            Arena.ItemDrop drop = _m.Arena.itemSpawn(it, (ushort)Math.Max(10, start * 3 / 5), (short)b._state.positionX, (short)b._state.positionY, (short)16, null);
            gb.SpareDrops--;
            gb.ForceGoal();
            _giver.Remove(s);
            if (drop != null) AmmoDrops++;
            return true;
        }

        /// <summary>
        /// Recalled (Travis 10-14): the ship store restocks it, heals it and fills its energy at once (BackToShip); 2-4 s later, geared up,
        /// it asks its team for a summon and waits up to 25 s in the ship. Any teammate that can summon (a medic, a leader class) and is
        /// not mid-fight answers, the one nearest where it left first; nobody answers = it walks out. A solo player never asks.
        /// </summary>
        void SummonBack(CTFXSlot s, CTFXBot b, int now)
        {
            int[] r;
            if (!_recallBack.TryGetValue(s, out r)) return;
            if (b.SoloPlayer) { _recallBack.Remove(s); return; }
            if (now - b.RecallAt < 2000 + (s.BotId % 3) * 700) return;              // buying, healed, energy full
            if (r.Length < 3 || r[2] == 0)
            {
                if (r.Length < 3) { r = new int[] { r[0], r[1], 0 }; _recallBack[s] = r; }
                r[2] = 1;
                SummonAsks++;
                if (_m.Chat != null) _m.Chat.Say(b, _m.Rng.NextDouble() < 0.5 ? "sum" : "summon pls", 1.0, false);
            }
            if (now - b.RecallAt > 25000) { _recallBack.Remove(s); WalkBacks++; b.LeaveShipSoon(now); return; }
            CTFXBot best = null;
            double bd = double.MaxValue;
            foreach (CTFXBot o in _m.LiveBots)
            {
                if (o == b || o.IsDead || o.Released || o.InDropship || o._team != b._team || o.HasTarget || _m.Flags.Carrying(o)) continue;
                bool can = o.ClassId == CTFXBot.SK_MEDIC ? now - o.SummonNextAt >= 0 : _m.Squads.CanSummon(o, now);
                if (!can) continue;
                double d = (r[0] == 0 && r[1] == 0) ? 0 : Dist(o._state.positionX, o._state.positionY, r[0], r[1]);
                if (d < bd) { bd = d; best = o; }
            }
            if (best == null) return;
            bool ok = best.ClassId == CTFXBot.SK_MEDIC ? _m.Squads.MedicSummonBot(best, b, now) : _m.Squads.Summon(best, b, now);
            if (!ok) return;
            b.InDropship = false;
            SummonBacks++;
            SummonWaitMs += now - b.RecallAt;
            _recallBack.Remove(s);
            b.ForceGoal();
            if (_m.Chat != null) _m.Chat.Say(best, "got you", 0.4, true);
        }

        internal string LinesText()
        {
            StringBuilder sb = new StringBuilder(" | lines held (falls/pushes):");
            foreach (CTFXTeamCmd t in Teams)
            {
                LineSet ls;
                if (!_lineSets.TryGetValue(t, out ls)) continue;
                int tot = Math.Max(1, ls.HeldMs[0] + ls.HeldMs[1] + ls.HeldMs[2]);
                sb.Append(' ').Append(t.End).Append(' ').Append(LineName[ls.Held]).Append(" (").Append(ls.Falls).Append('/').Append(ls.Pushes)
                  .Append(", outer ").Append(ls.HeldMs[0] * 100 / tot).Append("% mid ").Append(ls.HeldMs[1] * 100 / tot).Append("% inner ").Append(ls.HeldMs[2] * 100 / tot).Append("%)");
            }
            long live = _footMs;
            int now = Environment.TickCount;
            foreach (Foothold f in _foot.Values) live += now - f.Since;
            sb.Append(" | footholds: ").Append(_foot.Count).Append(" now, started ").Append(FootStarted).Append(", avg ")
              .Append(FootStarted > 0 ? (live / 1000 / FootStarted) + " s" : "-").Append(", summons in ").Append(FootSummons);
            sb.Append(" | suppressive volleys ").Append(Suppressions);
            sb.Append(" | ammo: pickups ").Append(AmmoPickups).Append(", piles dropped ").Append(AmmoDrops).Append(", recalls ").Append(Recalls)
              .Append(" (asked ").Append(SummonAsks).Append(", summoned back ").Append(SummonBacks).Append(" avg ").Append(SummonBacks > 0 ? SummonWaitMs / SummonBacks / 1000 : 0).Append(" s").Append(", walked ").Append(WalkBacks).Append("), dry and carrying on ").Append(DryOn);
            return sb.ToString();
        }
    }
}
