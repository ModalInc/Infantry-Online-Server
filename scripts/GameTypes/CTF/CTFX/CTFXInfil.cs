// CTF:X server-side bots - INFILTRATORS (Travis 10-14 night):
//  1. "Infiltrators should sneak past bases and try to get deep safely for a safe attach for a summoning class so teams can safely
//     group and push in from different parts of a base, at opportunistic spots (as there are a lot of side rooms and corridors that
//     sometimes are safer than concentrated areas)". An Infiltrator sent for an enemy-held flag first sneaks to the quietest route
//     node 250-900 px from that flag (fewest enemies, away from other infiltrators' spots, so pushes come from different sides);
//     once it has stood there 1.5 s with no enemy within 300 px, a summoning teammate (SL, Sergeant, Captain, Medic) who is not
//     fighting warps onto it and summons 2-4 more in; then the infiltrator goes on for the flag. The spot getting hot = a new spot
//     (once); 45 s without a quiet moment = it goes for the flag as it is.
//  2. "sometimes infiltrators will snipe the corridor at top brown base (just below the very top base, inside the area where there
//     are two middle splits)": an Infiltrator of a brown team that does not hold top brown now and then (15 % of its goal picks, at
//     most 2 at once) perches at an end of that hall corridor (y 1461-1474, under the top block, above the two middle blocks) for
//     40-90 s with a long engage range: its Pulse Rifle down the corridor.
/* PARSER GUARD: keep a block comment after the last line comment */

using System;
using System.Collections.Generic;

using InfServer.Game;

namespace InfServer.Script.GameType_CTF
{
    internal sealed partial class CTFXBot
    {
        // ---- the top-brown corridor perch (tiles): its west end looking east, its east end looking west, the middle
        static readonly int[][] CorridorPerches = new int[][] { new int[] { 713, 1467 }, new int[] { 797, 1467 }, new int[] { 714, 1470 }, new int[] { 796, 1470 }, new int[] { 756, 1470 } };
        int _perchUntil, _perchX, _perchY, _engageSaved = -1;
        internal bool Perching(int now) { return _perchUntil != 0 && now - _perchUntil < 0; }

        bool CorridorGoal(int now, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            if (Perching(now)) { tx = _perchX; ty = _perchY; return true; }
            if (_perchUntil != 0) { _perchUntil = 0; if (_engageSaved >= 0) { _engage = _engageSaved; _engageSaved = -1; } }
            if (_class != SK_INFIL || Group == null || Group.Quad != "brown" || _carrying || Slot == null) return false;
            if (Mgr.Flags != null && Mgr.Flags.HoldsFocus(_team)) return false;                   // the holders defend inside
            if (Mgr.Flags != null && Mgr.Flags.HasChase(Slot)) return false;
            if (_rng.NextDouble() >= 0.15 || Mgr.Perchers(now) >= 2) return false;
            int[] p = CorridorPerches[_rng.Next(CorridorPerches.Length)];
            int ox, oy;
            if (!Mgr.NearestOpen(p[0], p[1], 2, out ox, out oy)) return false;
            _perchX = ox; _perchY = oy; _perchUntil = now + 40000 + _rng.Next(50001);
            _engageSaved = _engage; _engage = Math.Max(_engage, 700);                          // the Pulse Rifle down the corridor
            Mgr.CorridorPerches++;
            tx = ox; ty = oy;
            return true;
        }
    }

    internal sealed partial class CTFXBotManager
    {
        internal int CorridorPerches;
        internal int Perchers(int now)
        {
            int n = 0;
            foreach (CTFXBot b in LiveBots) if (!b.IsDead && b.Perching(now)) n++;
            return n;
        }
    }

    internal sealed partial class CTFXFlags
    {
        sealed class InfilRun
        {
            public Arena.FlagState Flag;
            public int Tx, Ty, Phase, Since, QuietSince, Moves;      // Phase 0 = sneaking to the spot, 1 = done
        }
        readonly Dictionary<CTFXSlot, InfilRun> _infil = new Dictionary<CTFXSlot, InfilRun>();
        readonly Dictionary<CTFXSlot, int> _infilNext = new Dictionary<CTFXSlot, int>();
        internal int InfilRuns, InfilAttaches, InfilSummons, InfilGaveUp, InfilMoved;

        internal void Unchase(CTFXSlot s) { if (s != null) { _chase.Remove(s); _chaseUntil.Remove(s); } }

        internal bool HasChase(CTFXSlot s) { return s != null && _chase.ContainsKey(s); }

        /// <summary>The infiltrator's sneak spot, while it has one (Flags.Goal asks first).</summary>
        bool InfilGoal(CTFXBot b, Arena.FlagState fs, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            InfilRun r;
            if (_infil.TryGetValue(b.Slot, out r))
            {
                if (r.Flag != fs || r.Phase != 0) return false;
                tx = r.Tx; ty = r.Ty; return true;
            }
            if (b.ClassId != CTFXBot.SK_INFIL || fs.carrier != null || fs.team == null || fs.team == b._team) return false;
            int now = Environment.TickCount, next;
            if (_infilNext.TryGetValue(b.Slot, out next) && now - next < 0) return false;
            int x, y;
            if (!SafeSpot(b, fs, out x, out y)) return false;
            r = new InfilRun(); r.Flag = fs; r.Tx = x; r.Ty = y; r.Since = now;
            _infil[b.Slot] = r;
            _infilNext[b.Slot] = now + 60000;
            InfilRuns++;
            if (InfilRuns <= 12) _m.Note("infiltrator " + b.Slot.Name + " (" + b._team._name + ") sneaking to " + x + "," + y + " for " + Name(fs) + " (held by " + fs.team._name + ")");
            tx = x; ty = y;
            return true;
        }

        /// <summary>The quietest route node 250-900 px from the flag: fewest enemies near, away from the other infiltrators' spots.</summary>
        bool SafeSpot(CTFXBot b, Arena.FlagState fs, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            CTFXBaseDef bd = _m.BaseAt(fs.posX, fs.posY);
            if (bd != null && _m.AttachCorner(b, bd, out tx, out ty)) return true;     // a corner behind cover (CTFXBases.txt)
            CTFXRouteBook rb = _m.Routes;
            if (rb == null || rb.N == 0) return false;
            double bs = double.MaxValue;
            int best = -1;
            for (int n = 0; n < rb.N; n++)
            {
                int x = rb.X[n], y = rb.Y[n];
                double d = Dist(x, y, fs.posX, fs.posY);
                if (d < 250 || d > 900) continue;
                double sc = EnemiesNear(b._team, x, y, 400.0) * 3.0 + Math.Abs(d - 550) / 300.0 + _m.Rng.NextDouble() * 1.5;
                foreach (InfilRun o in _infil.Values) if (o.Phase == 0 && Dist(x, y, o.Tx * 16 + 8, o.Ty * 16 + 8) < 400) sc += 4.0;   // another side
                if (sc < bs) { bs = sc; best = n; }
            }
            if (best < 0) return false;
            return _m.NearestOpen(rb.X[best] >> 4, rb.Y[best] >> 4, 2, out tx, out ty);
        }

        /// <summary>Enemy bots within 700 px of the flag, and how many of them are fighting someone right now.</summary>
        void DefendersBusy(Team us, Arena.FlagState fs, out int busy, out int defs)
        {
            busy = 0; defs = 0;
            foreach (CTFXBot o in _m.LiveBots)
            {
                if (o.IsDead || o.InDropship || o._team == us) continue;
                if (Dist(o._state.positionX, o._state.positionY, fs.posX, fs.posY) > 700) continue;
                defs++;
                if (o.HasTarget) busy++;
            }
        }
        internal int InfilWaits, InfilOpportune;

        int EnemiesNear(Team us, int x, int y, double r)
        {
            int n = 0;
            double r2 = r * r;
            foreach (CTFXBot o in _m.LiveBots)
            {
                if (o.IsDead || o.InDropship || o._team == us) continue;
                double dx = o._state.positionX - x, dy = o._state.positionY - y;
                if (dx * dx + dy * dy <= r2) n++;
            }
            foreach (Player p in _m.Arena.PlayersIngame)
            {
                if (p.IsDead || p._team == us || p._state == null) continue;
                double dx = p._state.positionX - x, dy = p._state.positionY - y;
                if (dx * dx + dy * dy <= r2) n++;
            }
            return n;
        }

        /// <summary>Every second: the infiltrators on their spots; a quiet moment = the attach.</summary>
        void InfilStep(int now)
        {
            List<CTFXSlot> done = null;
            foreach (KeyValuePair<CTFXSlot, InfilRun> kv in _infil)
            {
                InfilRun r = kv.Value;
                CTFXBot b = _m.GetBot(kv.Key.BotId);
                Arena.FlagState cf;
                if (b == null || b.IsDead || r.Phase != 0 || !_chase.TryGetValue(kv.Key, out cf) || cf != r.Flag || r.Flag.carrier != null)
                { (done ?? (done = new List<CTFXSlot>())).Add(kv.Key); continue; }
                if (now - r.Since > 70000) { r.Phase = 1; InfilGaveUp++; b.ForceGoal(); continue; }
                int px = b._state.positionX, py = b._state.positionY;
                if (Dist(px, py, r.Tx * 16 + 8, r.Ty * 16 + 8) > 64) continue;
                int foes = EnemiesNear(b._team, px, py, 300.0);
                if (foes > 0)
                {
                    r.QuietSince = 0;
                    if (r.Moves == 0 && foes >= 2)
                    {   // the spot got hot: one move to another side
                        int x, y;
                        if (SafeSpot(b, r.Flag, out x, out y)) { r.Tx = x; r.Ty = y; r.Moves++; InfilMoved++; b.ForceGoal(); }
                    }
                    continue;
                }
                if (r.QuietSince == 0) { r.QuietSince = now | 1; continue; }
                if (now - r.QuietSince < 1500) continue;
                // Travis 10-14 night: a deep infiltrator perches hidden "waiting for other attackers to pick each other off, distract each
                // other, in order to look for the best opportunity to sneak by and macro to their teammates to storm". It calls the attach
                // when most defenders near the flag are busy fighting someone else, or after 20 s quiet on its perch
                int busy, defs;
                DefendersBusy(b._team, r.Flag, out busy, out defs);
                bool distracted = defs == 0 || busy * 2 >= defs;
                if (!distracted && now - r.QuietSince < 20000) { InfilWaits++; continue; }
                if (distracted && defs > 0) InfilOpportune++;
                r.Phase = 1;
                Attach(b, r.Flag, now);
                b.ForceGoal();
            }
            if (done != null) foreach (CTFXSlot s in done) _infil.Remove(s);
        }

        /// <summary>A summoning teammate warps onto the infiltrator and summons 2-4 more in.</summary>
        void Attach(CTFXBot infil, Arena.FlagState fs, int now)
        {
            CTFXBot lead = null;
            double bd = double.MaxValue;
            foreach (CTFXBot o in _m.LiveBots)
            {
                if (o == infil || o.IsDead || o.InDropship || o.Released || o._team != infil._team || o.HasTarget || !_m.Squads.CarriesSummoner(o) || Carrying(o)) continue;
                if (o.Slot == null || o.Slot.Style == CTFXStyle.Post || (o.Slot.Intent != null && o.Slot.Intent.Act == CTFXAct.Anchor)) continue;   // anchors stay home
                double d = Dist(o._state.positionX, o._state.positionY, infil._state.positionX, infil._state.positionY);
                if (d < 500) continue;
                double sc = d * (o.ClassId == CTFXBot.SK_MEDIC ? 1.5 : 1.0);                      // a leader class first
                if (sc < bd) { bd = sc; lead = o; }
            }
            if (lead == null || !_m.Squads.TeleportTo(lead, infil, now, "infil attach")) return;
            InfilAttaches++;
            _m.Chat.Say(infil, "safe attach " + _m.Chat.PlaceOf(infil), 0.6, false);
            if (!_chase.ContainsKey(lead.Slot)) { _chase[lead.Slot] = fs; _chaseUntil[lead.Slot] = now + 90000; Sent++; }
            int want = 2 + _m.Rng.Next(3), got = 0;
            List<KeyValuePair<double, CTFXBot>> c = new List<KeyValuePair<double, CTFXBot>>();
            foreach (CTFXBot o in _m.LiveBots)
            {
                if (o == infil || o == lead || o.IsDead || o.InDropship || o.Released || o._team != infil._team || o.HasTarget || o.Slot == null || Carrying(o)) continue;
                Arena.FlagState of;
                bool same = _chase.TryGetValue(o.Slot, out of) && of == fs;
                if (!same && (_chase.ContainsKey(o.Slot) || !FreeForFlag(o.Slot, o))) continue;
                double d = Dist(o._state.positionX, o._state.positionY, infil._state.positionX, infil._state.positionY);
                if (d < 600) continue;
                c.Add(new KeyValuePair<double, CTFXBot>(same ? -d : d, o));
            }
            c.Sort(delegate(KeyValuePair<double, CTFXBot> x, KeyValuePair<double, CTFXBot> y) { return x.Key.CompareTo(y.Key); });
            foreach (KeyValuePair<double, CTFXBot> kv in c)
            {
                if (got >= want) break;
                CTFXBot o = kv.Value;
                bool ok = lead.ClassId == CTFXBot.SK_MEDIC ? _m.Squads.MedicSummonBot(lead, o, now) : _m.Squads.CanSummon(lead, now) && _m.Squads.Summon(lead, o, now);
                if (!ok) break;
                if (!_chase.ContainsKey(o.Slot)) { _chase[o.Slot] = fs; _chaseUntil[o.Slot] = now + 90000; Sent++; }
                o.ForceGoal();
                got++; InfilSummons++;
            }
            if (InfilAttaches <= 12) _m.Note("infiltrator " + infil.Slot.Name + " attach at " + (infil._state.positionX >> 4) + "," + (infil._state.positionY >> 4) + ": " + lead.Slot.Name + " warped in, summoned " + got);
        }


        // ---- the flag courier (Travis 10-17): "1 team (at least the elite flagging focused ones) beelines for neutral flags at the start, RUNS to
        // where the engineers are (or attaches to them if they are closer to the optimal place), places the flag, then sweeps for infiltrators
        // looking to storm early or attacks, or seeks other flags and calls for attaches. Hyper vigilant about the flagging objective."
        readonly HashSet<CTFXBot> _courierWarped = new HashSet<CTFXBot>();
        internal int CourierRuns, CourierWarps, CourierSweeps;

        /// <summary>Every second: a courier with no flag business takes the nearest flag its team does not hold (neutral ones first).</summary>
        void CourierAssign(int now, List<Arena.FlagState> flags)
        {
            foreach (CTFXBotGroup g in _m.Groups)
            {
                if (g.Team == null) continue;
                foreach (CTFXSlot s in g.Slots)
                {
                    if (!s.Courier || _chase.ContainsKey(s)) continue;
                    CTFXBot b = _m.GetBot(s.BotId);
                    if (b == null || b.IsDead || b.InDropship || Carrying(b)) continue;
                    if (now - b.CourierPauseUntil < 0) continue;                          // sweeping for an infiltrator first
                    Arena.FlagState best = null;
                    double bd = double.MaxValue;
                    foreach (Arena.FlagState fs in flags)
                    {
                        if (fs.carrier != null || fs.team == g.Team || _m.MilkedFor(fs, g.Team)) continue;
                        double d = Dist(b._state.positionX, b._state.positionY, fs.posX, fs.posY);
                        if (fs.team == null) d *= 0.5;                                    // neutral flags first
                        else d += 600.0;                                                  // an enemy-held one after that (the infiltrator sneaks in)
                        if (d < bd) { bd = d; best = fs; }
                    }
                    if (best == null) continue;
                    _chase[s] = best; _chaseUntil[s] = now + 90000; Sent++; CourierRuns++;
                    b.ForceGoal();
                }
            }
        }

        /// <summary>A courier carrying a flag far from home with an engineer of its team standing at the base: it warps onto the engineer (once per carry).</summary>
        void CourierWarp(CTFXBot c, int now, int hx, int hy)
        {
            if (c.Slot == null || !c.Slot.Courier || c.Group == null || _courierWarped.Contains(c)) return;
            if (Dist(c._state.positionX, c._state.positionY, hx, hy) < 1200.0) return;
            CTFXBot eng = null;
            double bd = 900.0;
            foreach (CTFXSlot s in c.Group.Slots)
            {
                if (s.Style != CTFXStyle.Engineer) continue;
                CTFXBot e = _m.GetBot(s.BotId);
                if (e == null || e.IsDead || e.InDropship) continue;
                double d = Dist(e._state.positionX, e._state.positionY, hx, hy);
                if (d < bd) { bd = d; eng = e; }
            }
            if (eng == null) return;
            _courierWarped.Add(c);
            if (_m.Squads.TeleportTo(c, eng, now, "flag home")) CourierWarps++;
        }

        /// <summary>A courier's own goal when it has no flag in hand: an enemy infiltrator near its base first (it is hunted down), else null.</summary>
        bool CourierSweep(CTFXBot b, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            int hx, hy;
            if (b.Slot == null || !b.Slot.Courier || !HomePx(b, out hx, out hy)) return false;
            CTFXBot hit = null;
            double bd = 1000.0;
            foreach (CTFXBot o in _m.LiveBots)
            {
                if (o.IsDead || o.InDropship || o._team == b._team || o.ClassId != CTFXBot.SK_INFIL) continue;
                double d = Dist(o._state.positionX, o._state.positionY, hx, hy);
                if (d < bd) { bd = d; hit = o; }
            }
            if (hit == null) return false;
            _m.NearestOpenPx(hit._state.positionX, hit._state.positionY, out tx, out ty);
            b.CourierPauseUntil = Environment.TickCount + 4000; CourierSweeps++;
            return true;
        }

        internal string InfilText()
        {
            return " | infiltrators: runs " + InfilRuns + ", safe attaches " + InfilAttaches + " (summoned in " + InfilSummons + "), spot moves " + InfilMoved + ", gave up " + InfilGaveUp
                + ", opportune attaches (defenders busy) " + InfilOpportune + ", waits on the perch " + InfilWaits + ", corridor perches " + _m.CorridorPerches + ", couriers: runs " + CourierRuns + ", warps to engineers " + CourierWarps + ", sweeps " + CourierSweeps;
        }
    }
}
/* PARSER GUARD - keep last */
