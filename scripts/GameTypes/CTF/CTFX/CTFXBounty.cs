// CTF:X server-side bots - BOUNTIERS (Travis 10-13: "a subset of the population (10-15) who are purely either part of a bounty
// pack (helping stun or drain for a killer bountier, who they protect) or solo bountiers, who use clever tricks like teleport
// beacons set in the dropship and only take the safest kills").
//
// Style "bountier" (CTFXBots.txt bountier:N, home=outside so no base commander owns it). Its class decides the trade:
//   ENGINEER  the trap: walks to a busy spot, builds a sentry, lays an AP mine and a bullet mine at the foot of it (tucked under
//             the turret head), then beacons back to the dropship (Teleport Beacon) and waits; every cycle it goes out again,
//             re-lays the mines (they live 150 s) and rebuilds a lost sentry. A target it meets on the way gets the Engy Beamer.
//   SCIOPS    the pick: cloaked, it watches a fight from 300-450 px; a weakened enemy (under half health, hit in the last 3 s,
//   INFIL     with at most one of its mates near it) is the strike: in close with Zap Glove (stun) and PowerFist (SciOps) or
//             the Disruptor and pulse rifle (Infiltrator); after the kill, 4 s, or when hurt, it blinks back to the mark it left
//             in the dropship (Blink Generator) and waits there before the next sortie.
//   PACK      a bountier in a group with drain bots is that pack killer: the drainers follow it and drain what it shoots
//             (CTFXBot FollowTarget / ScanTargets), it takes only the safe kills like a solo one.
// Every bountier engages only its chosen victim or whoever shoots it. The mark is where it stood in the dropship as it left.
/* PARSER GUARD: keep a block comment after the last line comment */

using System;
using System.Collections.Generic;
using System.Text;

using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_CTF
{
    internal sealed class CTFXBounty
    {
        internal const int Beacon = 5001, Blink = 19, SentryMaker = 23, SentryVeh = 402;
        internal const int ApMine = 1014, BulletMine = 1029, PlasmaMine = 1020;

        sealed class St
        {
            public int Phase;                       // 0 out (trap: walking to the spot / pick: watching), 1 laying / striking
            public int Tx, Ty;                      // trap spot or watch spot (px)
            public CTFXTurretSpot Sentry;           // the engineer sentry spot
            public int Laid, LayAt, StrikeUntil, EscapeTry;
            public int Gx, Gy;                      // the open tile it actually walks to (px)
            public double LastD;                    // the victim's last distance while waiting (its closest pass)
            public int ReGoalAt;
            public int ProtectTry;
            public int LookAt;                      // ambush: next look for a victim                  // last bounty-protect look (Protect)                    // pack: the next goal refresh while there is a victim (victims move)
            public Player VictimP;
            public CTFXBot VictimB;
        }

        readonly CTFXBotManager _m;
        readonly Dictionary<CTFXSlot, St> _st = new Dictionary<CTFXSlot, St>();
        int _lastStep;
        // stats
        internal int Traps, MinesLaid, Strikes, Escapes, EscapeFails, Kills;
        internal int BreakAways;
        internal readonly int[] EscapeWhy = new int[5];          // no mark, class gate, cooldown, anti-warp, energy
        internal int VictimByKiller, VictimByDrainer, VictimByOther, VictimByHuman;

        /// <summary>A bot died: if it was a pack victim, who landed the kill (the killer, a drainer, anyone else).</summary>
        internal void NoteKill(CTFXBot victim, CTFXBot killerBot, Player killerHuman)
        {
            foreach (KeyValuePair<CTFXSlot, St> kv in _st)
            {
                if (kv.Value.VictimB != victim) continue;
                CTFXBot k = _m.GetBot(kv.Key.BotId);
                if (killerHuman != null) VictimByHuman++;
                else if (killerBot == k) VictimByKiller++;
                else if (killerBot != null && killerBot.Style == CTFXStyle.Drain && k != null && killerBot.Group == k.Group) VictimByDrainer++;
                else VictimByOther++;
                return;
            }
        }

        internal CTFXBounty(CTFXBotManager m) { _m = m; }

        internal void Clear() { _st.Clear(); Traps = MinesLaid = Strikes = Escapes = EscapeFails = Kills = 0; Array.Clear(EscapeWhy, 0, 5); BreakAways = 0; Protects = PackSummons = 0; Ambushes = Sprung = AmbushKills = AmbushMissed = AmbushLogs = 0; }

        static bool Is(CTFXBot b) { return b != null && b.Style == CTFXStyle.Bountier && b.Slot != null; }

        St Of(CTFXBot b)
        {
            St s;
            if (!_st.TryGetValue(b.Slot, out s)) { s = new St(); _st[b.Slot] = s; }
            return s;
        }

        internal static bool Trapper(CTFXBot b) { return b.ClassId == CTFXBot.SK_ENGY; }
        internal static bool Picker(CTFXBot b) { return (b.ClassId == CTFXBot.SK_SCIOPS || b.ClassId == CTFXBot.SK_INFIL) && !Ambusher(b); }

        /// <summary>The Zap Glove ambusher (zapper:N): a cloaked SciOps on its own in the landing areas.</summary>
        /// <summary>A sprung Zap Glove ambusher (the melee rotation runs, CTFXCombos.cs).</summary>
        internal bool Striking(CTFXBot b)
        {
            St s;
            return Ambusher(b) && b.Slot != null && _st.TryGetValue(b.Slot, out s) && s.Phase == 2;
        }

        internal static bool Ambusher(CTFXBot b) { return b.Group != null && b.Group.Ambush && b.ClassId == CTFXBot.SK_SCIOPS; }

        /// <summary>A pack drainer while its killer has a victim: it runs the victim down (CTFXBot asks before following the killer).</summary>
        internal bool DrainerGoal(CTFXBot d, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            CTFXBot k = PackFinisher(d.Group);
            if (k == null || !Killer(k)) return false;
            St s = Of(k);
            if (s.VictimP == null && s.VictimB == null) return false;
            Helpers.ObjectState vs = s.VictimP != null ? s.VictimP._state : s.VictimB._state;
            _m.NearestOpenPx(vs.positionX + _m.Rng.Next(-60, 61), vs.positionY + _m.Rng.Next(-45, 46), out tx, out ty);
            return true;
        }

        /// <summary>The pack victim of a drainer group (null when none): drainers aim at it.</summary>
        internal bool PackVictim(CTFXBot d, out Player p, out CTFXBot o)
        {
            p = null; o = null;
            CTFXBot k = PackFinisher(d.Group);
            if (k == null || !Killer(k)) return false;
            St s = Of(k);
            p = s.VictimP; o = s.VictimB;
            return p != null || o != null;
        }

        static bool InPack(CTFXBot b)
        {
            if (b == null || b.Style != CTFXStyle.Bountier || b.Group == null || b.Slot == null) return false;
            foreach (CTFXSlot s in b.Group.Slots) if (s.Style == CTFXStyle.Drain) return true;
            return false;
        }

        /// <summary>The killer of a hunt pack: its first bountier seat.</summary>
        internal bool Killer(CTFXBot b) { return InPack(b) && b.Slot.StyleIndex == 0; }

        /// <summary>A runner of a hunt pack (Travis 10-13): a cloaked Infiltrator that runs past the victim and repulses it back into
        /// the pack, and that the killer warps onto to get there.</summary>
        internal bool Runner(CTFXBot b) { return InPack(b) && b.Slot.StyleIndex >= 1; }

        /// <summary>The solo sniper (an Infantry bountier with no pack).</summary>
        internal bool Sniper(CTFXBot b) { return (b.ClassId == CTFXBot.SK_INF || (b.ClassId == CTFXBot.SK_JT && b.Group != null && b.Group.DropPack)) && !InPack(b); }

        /// <summary>
        /// A hunt pack killer holds its fire until the drainers have softened the victim (stunned, drained, or under 55 % health), then
        /// takes the kill (Travis 10-13: "chasing down, stunning, draining, and serving up the kill on a silver platter").
        /// </summary>
        internal bool HoldFire(CTFXBot b, int now)
        {
            return false;                               // Travis 10-13: the killer does not hold back; the drainers only drain and stun
#pragma warning disable 0162
            if (!Is(b) || !Killer(b)) return false;
            St s = Of(b);
            if (s.VictimB != null) return !s.VictimB.Softened(now);
            if (s.VictimP != null)
            {
                Player p = s.VictimP;
                int maxHp = p._baseVehicle != null ? p._baseVehicle._type.Hitpoints : 60;
                int maxEn = p._baseVehicle != null && p._baseVehicle._type.EnergyMax > 0 ? p._baseVehicle._type.EnergyMax : 400;
                return !(p._state.health < 0.55 * maxHp || p._state.energy < 0.35 * maxEn);
            }
            return false;
#pragma warning restore 0162
        }

        /// <summary>The pack killer of a group (its bountier), if alive and out of the ship.</summary>
        internal CTFXBot PackFinisher(CTFXBotGroup g)
        {
            if (g == null) return null;
            foreach (CTFXSlot s in g.Slots)
            {
                if (s.Style != CTFXStyle.Bountier || s.StyleIndex != 0) continue;
                CTFXBot b = _m.GetBot(s.BotId);
                if (b != null && !b.IsDead && !b.InDropship) return b;
            }
            return null;
        }

        // ================================================================ who it may shoot
        /// <summary>A bountier fights only its chosen victim (ScanTargets lets one that shoots it through as well).</summary>
        internal bool MayEngage(CTFXBot b, Player p, CTFXBot o)
        {
            if (Trapper(b)) return true;                                       // the engineer drains whoever comes near its trap
            if (Ambusher(b))
            {   // only its victim, and only once it is on top of the trap (one that shoots it gets through in ScanTargets)
                St a = Of(b);
                bool isV = (p != null && p == a.VictimP) || (o != null && o == a.VictimB);
                if (!isV) return false;
                Helpers.ObjectState vs = p != null ? p._state : o._state;
                double dx = vs.positionX - b._state.positionX, dy = vs.positionY - b._state.positionY;
                return a.Phase == 2 || dx * dx + dy * dy <= 80.0 * 80.0;
            }
            if (Killer(b) || Runner(b))
            {
                St k = Of(Runner(b) ? PackFinisher(b.Group) ?? b : b);
                return (p != null && p == k.VictimP) || (o != null && o == k.VictimB);
            }
            St s = Of(b);
            if (s.Phase != 1) return Safe(b, p, o);
            return (p != null && p == s.VictimP) || (o != null && o == s.VictimB);
        }

        /// <summary>The safest kill: under half health, hit in the last 3 s (in a fight), at most one of its mates within 250 px.</summary>
        bool Safe(CTFXBot me, Player p, CTFXBot o)
        {
            int hp, max, x, y;
            Team t;
            bool fighting;
            if (p != null)
            {
                hp = p._state.health; max = p._baseVehicle != null ? p._baseVehicle._type.Hitpoints : 60;
                x = p._state.positionX; y = p._state.positionY; t = p._team; fighting = true;   // a player: health alone tells it
            }
            else if (o != null)
            {
                hp = o._state.health; max = o._type.Hitpoints; x = o._state.positionX; y = o._state.positionY; t = o._team;
                fighting = o.RecentlyHit(Environment.TickCount, 3000);
            }
            else return false;
            if (max <= 0 || hp > 0.5 * max || !fighting) return false;
            int mates = 0;
            foreach (CTFXBot q in _m.LiveBots)
            {
                if (q == o || q.IsDead || q._team != t) continue;
                double dx = q._state.positionX - x, dy = q._state.positionY - y;
                if (dx * dx + dy * dy <= 250.0 * 250.0) mates++;
            }
            foreach (Player q in _m.Arena.PlayersIngame)
            {
                if (q == p || q.IsDead || q._team != t) continue;
                double dx = q._state.positionX - x, dy = q._state.positionY - y;
                if (dx * dx + dy * dy <= 250.0 * 250.0) mates++;
            }
            return mates <= 1;
        }

        // ================================================================ goals (CTFXBot.ChooseGoal asks first)
        internal bool Goal(CTFXBot b, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            if (!Is(b) || b.InDropship) return false;
            St s = Of(b);
            if (Trapper(b))
            {
                if (s.Tx == 0 && !PickTrapSpot(b, s)) return false;
                _m.NearestOpenPx(s.Tx, s.Ty, out tx, out ty);
                s.Gx = tx * 16 + 8; s.Gy = ty * 16 + 8;                      // tiles -> px
                return true;
            }
            if (Ambusher(b))
            {
                if (s.Phase == 2 && (s.VictimP != null || s.VictimB != null))
                {   // sprung (Travis 10-14 night: "walking past an easy target ... didn't lead their zap path ... try for a backstab"): where
                    // the victim will be (its heading, half the gap ahead), then 30 px BEHIND it, so the zap lands on its back
                    Helpers.ObjectState vs = s.VictimP != null ? s.VictimP._state : s.VictimB._state;
                    double vx = vs.velocityX, vy = vs.velocityY, v = Math.Sqrt(vx * vx + vy * vy);
                    double gap = Dist(b, vs.positionX, vs.positionY), hx, hy;
                    if (v >= 200) { hx = vx / v; hy = vy / v; }
                    else { float fx, fy; CTFXBot.YawVec(vs.yaw, out fx, out fy); hx = fx; hy = fy; }
                    double lead = v >= 200 ? Math.Min(160.0, gap * 0.5) : 0.0;
                    double back = v >= 200 ? 0.0 : 30.0;                              // the backstab once it stands (stunned)
                    int ax = vs.positionX + (int)(hx * (lead - back)), ay = vs.positionY + (int)(hy * (lead - back));
                    _m.NearestOpenPx(ax, ay, out tx, out ty);
                    return true;
                }
                if (s.Phase == 1) { tx = s.Gx >> 4; ty = s.Gy >> 4; return true; }      // the spot on its path: there, it stands still
                return _m.Dropship.OutsideGoal(b, 0.3, out tx, out ty);               // drifting near the landing strips, cloaked
            }
            if (Killer(b))
            {   // the pack hunts the landing areas; with a victim the killer hangs back about 300 px on its own side (the drainers
                // close in), out of the brawl, its battle rifle in range (10-13 soak: walking straight at the victim, it died first)
                if (s.VictimP != null || s.VictimB != null)
                {   // in on the victim, battle rifle range
                    Helpers.ObjectState vs = s.VictimP != null ? s.VictimP._state : s.VictimB._state;
                    _m.NearestOpenPx(vs.positionX + _m.Rng.Next(-90, 91), vs.positionY + _m.Rng.Next(-70, 71), out tx, out ty);
                    return true;
                }
                return _m.Dropship.OutsideGoal(b, 0.5, out tx, out ty);
            }
            if (Runner(b))
            {
                CTFXBot k = PackFinisher(b.Group);
                if (k == null) return _m.Dropship.OutsideGoal(b, 0.5, out tx, out ty);
                St ks = Of(k);
                if (ks.VictimP != null || ks.VictimB != null)
                {   // past the victim: the point 120 px beyond it, on the side away from the killer
                    Helpers.ObjectState vs = ks.VictimP != null ? ks.VictimP._state : ks.VictimB._state;
                    double dx = vs.positionX - k._state.positionX, dy = vs.positionY - k._state.positionY, d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < 1) { dx = 1; d = 1; }
                    _m.NearestOpenPx(vs.positionX + (int)(dx / d * 120), vs.positionY + (int)(dy / d * 120), out tx, out ty);
                    return true;
                }
                _m.NearestOpenPx(k._state.positionX + _m.Rng.Next(-150, 151), k._state.positionY + _m.Rng.Next(-110, 111), out tx, out ty);
                return true;
            }
            if (Picker(b) || Sniper(b))
            {
                if (s.Phase == 1 && !Sniper(b) && (s.VictimP != null || s.VictimB != null))
                {
                    Helpers.ObjectState vs = s.VictimP != null ? s.VictimP._state : s.VictimB._state;
                    _m.NearestOpenPx(vs.positionX, vs.positionY, out tx, out ty);
                    return true;
                }
                int hx, hy;
                if (!_m.GetHotSpot(Environment.TickCount, out hx, out hy)) return false;
                // a watch spot 300-450 px off the fight (a sniper 500-700 px), on a random side
                double a = _m.Rng.NextDouble() * Math.PI * 2, r = Sniper(b) ? 500 + _m.Rng.Next(201) : 300 + _m.Rng.Next(151);
                _m.NearestOpenPx(hx + (int)(Math.Cos(a) * r), hy + (int)(Math.Sin(a) * r), out tx, out ty);
                return true;
            }
            return false;
        }

        bool PickTrapSpot(CTFXBot b, St s)
        {
            int hx, hy;
            if (!_m.GetHotSpot(Environment.TickCount, out hx, out hy))
            {   // nothing hot yet: a hall
                string[] q = new string[] { "brown", "blue", "green", "purple" };
                if (!_m.CoreHub(q[_m.Rng.Next(q.Length)], out hx, out hy)) return false;
                hx = hx * 16 + 8; hy = hy * 16 + 8;
            }
            int tx, ty;
            _m.NearestOpenPx(hx + _m.Rng.Next(-120, 121), hy + _m.Rng.Next(-90, 91), out tx, out ty);
            s.Tx = tx; s.Ty = ty; s.Phase = 0; s.Laid = 0;
            return true;
        }

        // ================================================================ step (manager poll, 250 ms)
        internal void Step(int now)
        {
            if (now - _lastStep < 250) return;
            _lastStep = now;
            foreach (CTFXBot b in _m.LiveBots)
            {
                if (!Is(b) || b.IsDead || b.Released) continue;
                if (b.InDropship) { if (Killer(b)) PackSummon(b, now); continue; }
                St s = Of(b);
                bool hurt = b._state.health < 0.55 * b._type.Hitpoints;
                if (Protect(b, s, now)) continue;
                if (Trapper(b)) TrapStep(b, s, now, hurt);
                else if (Killer(b)) PackStep(b, s, now, hurt);
                else if (Runner(b)) RunnerStep(b, now);
                else if (Ambusher(b)) AmbushStep(b, s, now, hurt);
                else if (Picker(b) || Sniper(b)) PickStep(b, s, now, hurt);
            }
        }

        void TrapStep(CTFXBot b, St s, int now, bool hurt)
        {
            if (hurt && b.HasTarget) { Escape(b, now); return; }
            if (s.Tx == 0 || s.Gx == 0) return;
            double dx = s.Gx - b._state.positionX, dy = s.Gy - b._state.positionY;
            if (dx * dx + dy * dy > 72.0 * 72.0) return;                         // still on its way to the open tile by the spot
            if (s.Phase == 0)
            {   // there: the sentry first (a new spot each trip unless the old one still stands right here)
                if (s.Sentry == null || s.Sentry.TurretId == 0 || _m.Turrets.TurretGone(s.Sentry))
                {
                    s.Sentry = new CTFXTurretSpot();
                    s.Sentry.End = "trap"; s.Sentry.Set = b.Slot.Name; s.Sentry.Role = "sentry";
                    s.Sentry.Tx = s.Gx >> 4; s.Sentry.Ty = s.Gy >> 4; s.Sentry.Maker = SentryMaker; s.Sentry.VehType = SentryVeh;
                    if (_m.Turrets.TryBuild(b, s.Sentry, now)) Traps++;
                }
                s.Phase = 1; s.Laid = 0; s.LayAt = now + 600;
                return;
            }
            if (now - s.LayAt < 0) return;
            // the mines at the foot of the sentry: it faces the sentry spot and drops them there (a mine stays where it leaves the barrel)
            int item = s.Laid == 0 ? ApMine : s.Laid == 1 ? BulletMine : PlasmaMine;
            double ex = s.Gx - b._state.positionX, ey = s.Gy - b._state.positionY;
            if (ex * ex + ey * ey < 1.0) ex = 1.0;                               // standing on it: drop them where it faces
            if (b.LayMine(now, item, CTFXBot.YawToPublic(ex, ey))) { MinesLaid++; s.Laid++; }
            else s.Laid++;                                                       // not in its kit / not now: the next one
            s.LayAt = now + 700;
            if (s.Laid >= 3)
            {   // trap set: back to the dropship to wait for the mine kill; the next trip lays fresh mines
                s.EscapeTry = 0;
                Escape(b, now);
                s.Phase = 0; s.Laid = 0;
                if (_m.Rng.NextDouble() < 0.4) s.Tx = 0;                         // now and then a new spot
            }
        }

        /// <summary>The hunt pack: the nearest enemy within 600 px of the killer that is not in a crowd (at most 2 of its mates within
        /// 250 px) is the pack victim; the drainers follow the killer and drain / stun it (CTFXBot FollowTarget / ScanTargets).</summary>
        void PackStep(CTFXBot b, St s, int now, bool hurt)
        {
            bool vDead = s.VictimP != null ? (s.VictimP.IsDead || s.VictimP._arena != _m.Arena) : s.VictimB != null && (s.VictimB.IsDead || s.VictimB.Released);
            if (vDead) { Kills++; s.VictimP = null; s.VictimB = null; b.ForceGoal(); }
            if (s.VictimP != null || s.VictimB != null)
            {
                Helpers.ObjectState vs = s.VictimP != null ? s.VictimP._state : s.VictimB._state;
                double dx = vs.positionX - b._state.positionX, dy = vs.positionY - b._state.positionY;
                if (dx * dx + dy * dy > 1400.0 * 1400.0 || now - s.StrikeUntil >= 0) { s.VictimP = null; s.VictimB = null; b.ForceGoal(); return; }
                if (now - s.ReGoalAt >= 0) { s.ReGoalAt = now + 1500; b.ForceGoal(); }
                Deliver(b, vs, now);
                return;
            }
            // out to 1300 px (the pack runs or warps there), and only one nobody else is fighting: third parties took 3 of every 4
            // pack kills when the victim was picked out of a brawl (Travis 10-13)
            Player bp = null; CTFXBot bb = null;
            double bd = 1300.0 * 1300.0;
            foreach (Player p in _m.Arena.PlayersIngame)
            {
                if (p.IsDead || p._team == b._team) continue;
                double dx = p._state.positionX - b._state.positionX, dy = p._state.positionY - b._state.positionY, d = (dx * dx + dy * dy) * Worth(p.Bounty);
                if (d < bd && Crowd(p._team, p._state.positionX, p._state.positionY, p, null) <= 2
                    && ThirdParties(p._team, b._team, p._state.positionX, p._state.positionY) == 0) { bd = d; bp = p; bb = null; }
            }
            foreach (CTFXBot o in _m.LiveBots)
            {
                if (o.IsDead || o.Released || o.InDropship || o._team == b._team) continue;
                double dx = o._state.positionX - b._state.positionX, dy = o._state.positionY - b._state.positionY, d = (dx * dx + dy * dy) * Worth(CTFXBotManager.BountyOf(o));
                if (d < bd && Crowd(o._team, o._state.positionX, o._state.positionY, null, o) <= 2
                    && ThirdParties(o._team, b._team, o._state.positionX, o._state.positionY) == 0) { bd = d; bb = o; bp = null; }
            }
            if (bp == null && bb == null) return;
            s.VictimP = bp; s.VictimB = bb; s.StrikeUntil = now + 25000;
            Strikes++;
            b.ForceGoal();
        }

        internal int Deliveries, Summoned2, RunnerRepulses, RunnerStuns, Protects, PackSummons;

        /// <summary>
        /// Keeping the bounty (Travis 10-14: the 30k players are the ones who do not die). Once it is worth something (300+), a hurt
        /// bountier (under half) with no medic of its team within 400 px and no enemy right on it (120 px) leaves the fight the way a
        /// player does: Blink / Beacon back to its ship mark, or DropShip Recall; the ship store heals and refills it.
        /// </summary>
        bool Protect(CTFXBot b, St s, int now)
        {
            if (b.Slot.Bounty < 60 || _m.Flags.Carrying(b)) return false;
            double hpf = (double)b._state.health / Math.Max(1, (int)b._type.Hitpoints);
            bool clear = hpf < 0.7 && _m.AntiWarpedAt(b._state.positionX, b._state.positionY, b._team, b) == null;   // a clean window: take it
            if (hpf >= 0.5 && !clear) return false;
            if (now - s.ProtectTry < 3000) return false;
            s.ProtectTry = now;
            CTFXBot medic = _m.NearestMedic(b, 400.0);
            if (medic != null) return false;                                     // the pack medic heals it where it stands
            foreach (CTFXBot o in _m.LiveBots)
                if (!o.IsDead && !o.InDropship && o._team != b._team && Math.Abs(o._state.positionX - b._state.positionX) + Math.Abs(o._state.positionY - b._state.positionY) < 120) return false;
            int before = Escapes;
            if (Trapper(b) || Picker(b)) Escape(b, now);
            if (Escapes == before && !b.Recall(now)) return false;
            s.Phase = 0; s.VictimP = null; s.VictimB = null;
            Protects++;
            return true;
        }

        /// <summary>A pack killer that recalled: once geared up (3-5 s) a drainer of its pack (a medic) summons it back to the pack.</summary>
        void PackSummon(CTFXBot k, int now)
        {
            if (k.RecallAt == 0 || now - k.RecallAt < 3000 + (k._id % 3) * 1000 || now - k.RecallAt > 30000) return;
            foreach (CTFXSlot sl in k.Group.Slots)
            {
                CTFXBot o = _m.GetBot(sl.BotId);
                if (o == null || o == k || o.IsDead || o.InDropship || o.HasTarget || o.ClassId != CTFXBot.SK_MEDIC) continue;
                if (_m.Squads.MedicSummonBot(o, k, now)) { k.InDropship = false; k.RecallAt = 0; PackSummons++; k.ForceGoal(); return; }
            }
        }

        /// <summary>Getting the killer to the victim (Travis 10-13): far from it (600+ px), it Suit Teleports onto the runner when the
        /// runner is beside the victim; else a drainer beside the victim summons it (Medic Teleport Summoner).</summary>
        void Deliver(CTFXBot k, Helpers.ObjectState vs, int now)
        {
            double kx = vs.positionX - k._state.positionX, ky = vs.positionY - k._state.positionY;
            if (kx * kx + ky * ky < 450.0 * 450.0) return;
            CTFXBot near = null;
            foreach (CTFXSlot s in k.Group.Slots)
            {
                CTFXBot o = _m.GetBot(s.BotId);
                if (o == null || o == k || o.IsDead || o.InDropship) continue;
                double dx = vs.positionX - o._state.positionX, dy = vs.positionY - o._state.positionY;
                if (dx * dx + dy * dy > 260.0 * 260.0) continue;
                if (Runner(o)) { if (_m.Squads.TeleportTo(k, o, now, "deliver")) { Deliveries++; return; } }
                else if (near == null && o.Style == CTFXStyle.Drain) near = o;
            }
            if (near != null && _m.Squads.MedicSummonBot(near, k, now)) Summoned2++;
        }

        /// <summary>The runner: on the far side of the victim within 150 px, it throws its repulsor at it (the burst shoves the victim
        /// back toward the killer); within 60-260 px its Disruptor stuns it.</summary>
        void RunnerStep(CTFXBot r, int now)
        {
            CTFXBot k = PackFinisher(r.Group);
            if (k == null) return;
            St ks = Of(k);
            if (ks.VictimP == null && ks.VictimB == null) return;
            Helpers.ObjectState vs = ks.VictimP != null ? ks.VictimP._state : ks.VictimB._state;
            St rs = Of(r);
            if (now - rs.ReGoalAt >= 0) { rs.ReGoalAt = now + 1000; r.ForceGoal(); }   // its goal was the killer's side until now
            double rx = r._state.positionX - vs.positionX, ry = r._state.positionY - vs.positionY, d = Math.Sqrt(rx * rx + ry * ry);
            double kx = k._state.positionX - vs.positionX, ky = k._state.positionY - vs.positionY;
            bool farSide = rx * kx + ry * ky < 0;                                // the runner and the killer on opposite sides of it
            if (d <= 170.0 && farSide && r.CanRepulse(now) && r.GuardRepulse(now, vs.positionX, vs.positionY)) { RunnerRepulses++; return; }
            if (r.RunnerDisrupt(now, vs.positionX, vs.positionY)) RunnerStuns++;
        }

        /// <summary>Fighters of neither the victim's team nor the pack's within 320 px of (x, y).</summary>
        /// <summary>A bounty hunter goes for the fat bounties (10 % of the victim's bounty goes to the killer): a 3k bounty counts as
        /// half as far away, a 10k one about a quarter (squared distances).</summary>
        static double Worth(int bounty) { double f = 1.0 / (1.0 + Math.Max(0, bounty) / 3000.0); return f * f; }

        int ThirdParties(Team victim, Team pack, int x, int y)
        {
            int n = 0;
            foreach (CTFXBot q in _m.LiveBots)
            {
                if (q.IsDead || q.Released || q.InDropship || q._team == victim || q._team == pack) continue;
                double dx = q._state.positionX - x, dy = q._state.positionY - y;
                if (dx * dx + dy * dy <= 320.0 * 320.0) n++;
            }
            foreach (Player p in _m.Arena.PlayersIngame)
            {
                if (p.IsDead || p._team == victim || p._team == pack) continue;
                double dx = p._state.positionX - x, dy = p._state.positionY - y;
                if (dx * dx + dy * dy <= 320.0 * 320.0) n++;
            }
            return n;
        }

        int Crowd(Team t, int x, int y, Player notP, CTFXBot notB)
        {
            int n = 0;
            foreach (CTFXBot q in _m.LiveBots)
            {
                if (q == notB || q.IsDead || q._team != t) continue;
                double dx = q._state.positionX - x, dy = q._state.positionY - y;
                if (dx * dx + dy * dy <= 250.0 * 250.0) n++;
            }
            foreach (Player q in _m.Arena.PlayersIngame)
            {
                if (q == notP || q.IsDead || q._team != t) continue;
                double dx = q._state.positionX - x, dy = q._state.positionY - y;
                if (dx * dx + dy * dy <= 250.0 * 250.0) n++;
            }
            return n;
        }

        void PickStep(CTFXBot b, St s, int now, bool hurt)
        {
            if (Sniper(b))
            {   // the sniper: never in close; anyone within 300 px and it moves on (a new watch spot), hurt and it backs off
                foreach (CTFXBot o in _m.LiveBots)
                {
                    if (o.IsDead || o._team == b._team) continue;
                    double dx = o._state.positionX - b._state.positionX, dy = o._state.positionY - b._state.positionY;
                    if (dx * dx + dy * dy <= 300.0 * 300.0) { b.ForceGoal(); break; }
                }
            }
            if (s.Phase == 1)
            {
                bool vDead = s.VictimP != null ? (s.VictimP.IsDead || s.VictimP._arena != _m.Arena) : (s.VictimB == null || s.VictimB.IsDead || s.VictimB.Released);
                if (vDead || hurt || now - s.StrikeUntil >= 0)
                {
                    if (vDead) Kills++;
                    s.Phase = 0; s.VictimP = null; s.VictimB = null;
                    Escape(b, now);                                              // combo landed or not: out, as Travis described
                }
                return;
            }
            if (hurt && b.HasTarget) { Escape(b, now); return; }
            // a victim: the safest kill within 500 px
            Player bp = null; CTFXBot bb = null;
            double bd = 500.0 * 500.0;
            foreach (Player p in _m.Arena.PlayersIngame)
            {
                if (p.IsDead || p._team == b._team) continue;
                double dx = p._state.positionX - b._state.positionX, dy = p._state.positionY - b._state.positionY, d = (dx * dx + dy * dy) * Worth(p.Bounty);
                if (d < bd && Safe(b, p, null)) { bd = d; bp = p; bb = null; }
            }
            foreach (CTFXBot o in _m.LiveBots)
            {
                if (o.IsDead || o.Released || o.InDropship || o._team == b._team) continue;
                double dx = o._state.positionX - b._state.positionX, dy = o._state.positionY - b._state.positionY, d = (dx * dx + dy * dy) * Worth(CTFXBotManager.BountyOf(o));
                if (d < bd && Safe(b, null, o)) { bd = d; bb = o; bp = null; }
            }
            if (bp == null && bb == null) return;
            s.Phase = 1; s.VictimP = bp; s.VictimB = bb; s.StrikeUntil = now + 6000;
            Strikes++;
            b.ForceGoal();
        }

        // ================================================================ the Zap Glove ambush (Travis 10-14: cloaked SciOps, melee
        // combos, solo, picking off stragglers in the outside spawns; it sets a trap by waiting in someone's predicted walk path)
        //   0 looking   a lone enemy on the move within 700 px (nobody of its team within 250 px, nobody else fighting there): the point
        //               200-320 px ahead of it on its heading, if the ambusher gets there first
        //   1 waiting   walks there and stands still, cloaked; gives up when the victim turns away (its heading no longer passes within
        //               90 px of the spot), gets company, or 14 s pass
        //   2 sprung    the victim within 110 px: zap, then the fist; 5 s. A kill or a miss: hurt = blink out, else the next trap
        internal int Ambushes, Sprung, AmbushKills, AmbushMissed, AmbushLogs;

        void AmbushStep(CTFXBot b, St s, int now, bool hurt)
        {
            bool vGone = s.VictimP != null ? (s.VictimP.IsDead || s.VictimP._arena != _m.Arena) : (s.VictimB != null && (s.VictimB.IsDead || s.VictimB.Released || s.VictimB.InDropship));
            if (s.Phase == 2)
            {
                bool vDead = s.VictimP != null ? s.VictimP.IsDead : s.VictimB != null && s.VictimB.IsDead;
                if (vGone || hurt || now - s.StrikeUntil >= 0)
                {
                    if (vDead) { Kills++; AmbushKills++; } else AmbushMissed++;
                    if (AmbushLogs++ < 20)
                    {   // why a strike ended (cycle 5: 24 sprung, 0 kills)
                        Helpers.ObjectState es = s.VictimP != null ? s.VictimP._state : s.VictimB != null ? s.VictimB._state : null;
                        int vhp = s.VictimB != null ? s.VictimB._state.health * 100 / Math.Max(1, s.VictimB.MaxHp) : s.VictimP != null ? s.VictimP._state.health : -1;
                        _m.Note("zap strike by " + b.Slot.Name + " ended: " + (vDead ? "KILL" : vGone ? "victim gone" : hurt ? "zapper hurt (hp " + b._state.health * 100 / Math.Max(1, b.MaxHp) + "%)" : "7 s out")
                            + ", victim hp " + vhp + "%, rotation step " + b.ZapStep + ", gap " + (es != null ? (int)Dist(b, es.positionX, es.positionY) : -1) + " px, weapon " + b.WeaponId + ", energy " + (int)b.Energy + "/" + (int)b.EnergyMax + ", fire block " + b.FireBlock + ", " + b.Why);
                    }
                    s.Phase = 0; s.VictimP = null; s.VictimB = null; s.LookAt = now + 2000;
                    if (hurt || (vDead && _m.Rng.NextDouble() < 0.7)) Escape(b, now);   // Travis 10-14: the beacon back to safety after the kill
                    b.ForceGoal();
                    return;
                }
                if (now - s.ReGoalAt >= 0) { s.ReGoalAt = now + 300; b.ForceGoal(); }   // keep leading it
                return;
            }
            if (hurt && b.HasTarget) { Escape(b, now); return; }
            if (s.Phase == 1)
            {
                if (vGone) { s.Phase = 0; s.VictimP = null; s.VictimB = null; b.ForceGoal(); return; }
                Helpers.ObjectState vs = s.VictimP != null ? s.VictimP._state : s.VictimB._state;
                double dx = vs.positionX - b._state.positionX, dy = vs.positionY - b._state.positionY;
                // cycle 9: spring when it walks INTO reach (55 px), or at its closest pass (receding, under 100 px): chasing a walking
                // target from behind at the same speed never closed the 45 px glove reach (cycles 5-8: 0 kills in 98 strikes)
                double dnow = Math.Sqrt(dx * dx + dy * dy);
                bool closing = s.LastD == 0 || dnow < s.LastD;                       // cycle 12: only as it comes on (83 sprung on the pass, 0 kills)
                s.LastD = dnow;
                if (dnow <= 100.0 && closing)                                         // cycle 13: the glove's prefire + aim take ~0.4 s: spring early
                {
                    s.Phase = 2; s.StrikeUntil = now + 7000; Sprung++; Strikes++; s.ReGoalAt = now + 300;
                    b.ZapSeqStart();
                    b.StrikeNow(now, s.VictimP, s.VictimB);
                    b.ForceGoal();
                    return;
                }
                if (now - s.ReGoalAt >= 0)
                {   // once a second: still coming this way, still alone?
                    s.ReGoalAt = now + 1000;
                    Team vt = s.VictimP != null ? s.VictimP._team : s.VictimB._team;
                    bool alone = Crowd(vt, vs.positionX, vs.positionY, s.VictimP, s.VictimB) == 0;
                    if (now - s.StrikeUntil >= 0 || !alone || !Passes(vs, s.Gx, s.Gy, 48.0)) { s.Phase = 0; s.VictimP = null; s.VictimB = null; b.ForceGoal(); }
                }
                return;
            }
            if (now - s.LookAt < 0) return;
            s.LookAt = now + 700;
            Player bp = null; CTFXBot bb = null;
            int gx = 0, gy = 0;
            double bd = 700.0;
            foreach (Player p in _m.Arena.PlayersIngame)
            {
                if (p.IsDead || p._team == b._team) continue;
                int px, py;
                double d = Dist(b, p._state.positionX, p._state.positionY);
                if (d < bd && Crowd(p._team, p._state.positionX, p._state.positionY, p, null) == 0 && ThirdParties(p._team, b._team, p._state.positionX, p._state.positionY) == 0
                    && Ahead(b, p._state, out px, out py)) { bd = d; bp = p; bb = null; gx = px; gy = py; }
            }
            foreach (CTFXBot o in _m.LiveBots)
            {
                if (o.IsDead || o.Released || o.InDropship || o._team == b._team || o.HasTarget) continue;
                int px, py;
                double d = Dist(b, o._state.positionX, o._state.positionY);
                if (d < bd && Crowd(o._team, o._state.positionX, o._state.positionY, null, o) == 0 && ThirdParties(o._team, b._team, o._state.positionX, o._state.positionY) == 0
                    && Ahead(b, o._state, out px, out py)) { bd = d; bb = o; bp = null; gx = px; gy = py; }
            }
            if (bp == null && bb == null) return;
            s.Phase = 1; s.VictimP = bp; s.VictimB = bb; s.Gx = gx; s.Gy = gy; s.StrikeUntil = now + 14000; s.ReGoalAt = now + 1500;
            b.ZapReady(now);
            s.LastD = 0;
            Ambushes++;
            b.ForceGoal();
        }

        static double Dist(CTFXBot b, int x, int y)
        {
            double dx = x - b._state.positionX, dy = y - b._state.positionY;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>The trap spot: 200-320 px ahead of a moving target on its heading, on an open tile in a straight walk from it, that the
        /// ambusher reaches first (its way there under 80 % of the target's).</summary>
        bool Ahead(CTFXBot b, Helpers.ObjectState vs, out int px, out int py)
        {
            px = 0; py = 0;
            double vx = vs.velocityX, vy = vs.velocityY, v = Math.Sqrt(vx * vx + vy * vy);
            if (v < 400) return false;                                           // standing / barely moving: no path to read
            double lead = 200 + _m.Rng.Next(121);
            int tx, ty;
            _m.NearestOpenPx(vs.positionX + (int)(vx / v * lead), vs.positionY + (int)(vy / v * lead), out tx, out ty);
            px = tx * 16 + 8; py = ty * 16 + 8;
            if (!_m.WalkLine(vs.positionX, vs.positionY, px, py)) return false;  // a wall between: it will not come that way
            double mine = Dist(b, px, py), theirs = Math.Sqrt(Math.Pow(px - vs.positionX, 2) + Math.Pow(py - vs.positionY, 2));
            return mine < theirs * 0.8;
        }

        /// <summary>Still heading for (x, y): the spot lies ahead of it and its heading passes within r px.</summary>
        static bool Passes(Helpers.ObjectState vs, int x, int y, double r)
        {
            double vx = vs.velocityX, vy = vs.velocityY, v = Math.Sqrt(vx * vx + vy * vy);
            double ex = x - vs.positionX, ey = y - vs.positionY;
            if (v < 200) return ex * ex + ey * ey < 250.0 * 250.0;              // paused near it: keep waiting
            double along = (ex * vx + ey * vy) / v;
            if (along < -40) return false;                                       // walked past / turned back
            double off = Math.Abs(ex * vy - ey * vx) / v;
            return off <= r;
        }

        /// <summary>
        /// Where to run when an enemy disruptor holds it (10-14 diagnosis: a straight-line point 700 px off sat behind a wall and the bot
        /// stalled in place): a route node 250-900 px away, scored by its distance to the nearest enemy, a bonus for lying along the
        /// away-from-the-crowd direction (vx, vy); -1 = none.
        /// </summary>
        int SafeNode(CTFXBot b, double vx, double vy)
        {
            CTFXRouteBook rb = _m.Routes;
            if (rb == null || rb.N == 0) return -1;
            int x = b._state.positionX, y = b._state.positionY, best = -1;
            double vl = Math.Sqrt(vx * vx + vy * vy), bs = double.MinValue;
            for (int n = 0; n < rb.N; n++)
            {
                double dx = rb.X[n] - x, dy = rb.Y[n] - y, d = Math.Sqrt(dx * dx + dy * dy);
                if (d < 250 || d > 900) continue;
                double near = 900;
                foreach (CTFXBot o in _m.LiveBots)
                {
                    if (o.IsDead || o.InDropship || o._team == b._team) continue;
                    double ex = rb.X[n] - o._state.positionX, ey = rb.Y[n] - o._state.positionY, e = Math.Sqrt(ex * ex + ey * ey);
                    if (e < near) near = e;
                }
                double sc = near + (vl > 0 ? 200.0 * (dx * vx + dy * vy) / (d * vl) : 0) - 0.15 * d;
                if (sc > bs) { bs = sc; best = n; }
            }
            return best;
        }

        /// <summary>Back to the mark in the dropship: Teleport Beacon (engineer) or Blink Generator (SciOps, Infiltrator), its energy
        /// and the anti-warp rules; then it waits there before the next sortie. No such item: it runs on.</summary>
        internal void Escape(CTFXBot b, int now)
        {
            St st = Of(b);
            if (now - st.EscapeTry < 3000) return;                               // one try every 3 s, not every step
            st.EscapeTry = now;
            int item = Trapper(b) ? Beacon : Blink;
            if (!CTFXSkillLogic.Allowed(item, b.ClassId)) item = 46;             // no beacon / blink for its class: DropShip Recall
            int why = b.MarkX == 0 ? 0 : !CTFXSkillLogic.Allowed(item, b.ClassId) ? 1 : now - b.WarpReadyAt < 0 ? 2
                : _m.AntiWarpedAt(b._state.positionX, b._state.positionY, b._team, b) != null ? 3 : !b.SpendEnergy(item) ? 4 : -1;
            if (why == 3)
            {   // an enemy anti-warp covers it: run straight out of the field (512 px) and try again in a second
                // away from the whole crowd, not one source (every enemy carries a 512 px disruptor): the sum of the directions from
                // each enemy within 800 px, nearer ones weighing more
                int x = b._state.positionX, y = b._state.positionY;
                if (EscapeWhy[3] < 12)
                {   // what holds it (first dozen): the source, how far, how many enemies within 512 px
                    int ax, ay, n512 = 0;
                    _m.AntiWarpSource(x, y, b._team, out ax, out ay, b);
                    foreach (CTFXBot o in _m.LiveBots)
                        if (!o.IsDead && !o.InDropship && o._team != b._team && Math.Abs(o._state.positionX - x) + Math.Abs(o._state.positionY - y) < 512) n512++;
                    _m.Note("escape blocked #" + (EscapeWhy[3] + 1) + ": " + CTFXBot.ShortClass(b.ClassId) + " at " + (x >> 4) + "," + (y >> 4) + " by " + _m.AntiWarpedAt(x, y, b._team, b)
                        + " at " + (ax >> 4) + "," + (ay >> 4) + " (" + (int)Math.Sqrt((ax - x) * (ax - x) + (ay - y) * (ay - y)) + " px), enemies within 512: " + n512
                        + (b.Retreating ? ", running" : "") + ", hp " + b._state.health);
                }
                double vx = 0, vy = 0;
                foreach (CTFXBot o in _m.LiveBots)
                {
                    if (o.IsDead || o.InDropship || o._team == b._team) continue;
                    double dx = x - o._state.positionX, dy = y - o._state.positionY, d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < 1 || d > 800) continue;
                    vx += dx / (d * d); vy += dy / (d * d);
                }
                foreach (Player q in _m.Arena.PlayersIngame)
                {
                    if (q.IsDead || q._team == b._team) continue;
                    double dx = x - q._state.positionX, dy = y - q._state.positionY, d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < 1 || d > 800) continue;
                    vx += dx / (d * d); vy += dy / (d * d);
                }
                int sx, sy;
                double vl = Math.Sqrt(vx * vx + vy * vy);
                int node = SafeNode(b, vx, vy);
                if (node >= 0) { b.RetreatTo(now, _m.Routes.X[node] >> 4, _m.Routes.Y[node] >> 4, 5000); BreakAways++; }
                else if (vl > 0) { b.BreakAway(now, x - (int)(vx / vl * 100), y - (int)(vy / vl * 100), 700); BreakAways++; }
                else if (_m.AntiWarpSource(x, y, b._team, out sx, out sy, b)) { b.BreakAway(now, sx, sy, 640); BreakAways++; }
                st.EscapeTry = now - 2000;
            }
            if (why >= 0)
            {
                EscapeFails++; EscapeWhy[why]++;
                if (why == 1 || why == 0) _m.WarnOnce("escape: " + CTFXBot.ShortClass(b.ClassId) + " bountier cannot use item " + item + (why == 0 ? " (no ship mark)" : " (class gate)"));
                return;
            }
            b.WarpReadyAt = now + 15000;
            if (item == 46) { if (b.Recall(now)) Escapes++; else EscapeFails++; return; }
            b.Teleported(b.MarkX, b.MarkY, now);
            b.BackToShip(now, 15000 + _m.Rng.Next(25001));                     // 15-40 s in the ship, healed, then out again
            Escapes++;
        }

        internal string Summary()
        {
            if (_st.Count == 0) return "";
            StringBuilder sb = new StringBuilder();
            foreach (CTFXBot b in _m.LiveBots)
            {
                if (!Is(b)) continue;
                St s;
                _st.TryGetValue(b.Slot, out s);
                double d = s != null && s.Gx != 0 ? Math.Sqrt(Math.Pow(s.Gx - b._state.positionX, 2) + Math.Pow(s.Gy - b._state.positionY, 2)) : -1;
                sb.Append(' ').Append(b.Slot.Name).Append('/').Append(Killer(b) ? "KILLER" : CTFXBot.ShortClass(b.ClassId)).Append(" bty ").Append(b.Slot.Bounty)
                  .Append(b.IsDead ? " dead" : b.InDropship ? " ship" : "")
                  .Append(s != null && (s.VictimP != null || s.VictimB != null) ? " hunting" : "")
                  .Append(" ph").Append(s != null ? s.Phase : -1).Append(" d").Append((int)d).Append(" ").Append(b.Why).Append(';');
            }
            return " | bountiers:" + sb + " pack victims killed by: killer " + VictimByKiller + ", drainer " + VictimByDrainer + ", others " + VictimByOther
                + ", humans " + VictimByHuman + "; deliveries: onto the runner " + Deliveries + ", summoned in " + Summoned2 + ", runner repulses " + RunnerRepulses + ", runner stuns " + RunnerStuns + "; zap ambushes " + Ambushes + " (sprung " + Sprung + ", kills " + AmbushKills + ", missed " + AmbushMissed + ")" + "; bounty kept: hurt exits " + Protects + ", pack summons " + PackSummons
                + " | traps " + Traps + " (mines " + MinesLaid + ", refused by live caps " + _m.Shots.CapRefusals + "), strikes " + Strikes + " (kills " + Kills + "), back to the ship "
                + Escapes + " (could not " + EscapeFails + ": no mark " + EscapeWhy[0] + ", class " + EscapeWhy[1] + ", cooldown " + EscapeWhy[2] + ", anti-warp " + EscapeWhy[3] + ", energy " + EscapeWhy[4] + "; ran out of anti-warp " + BreakAways + ")";
        }
    }
}
/* PARSER GUARD - keep last */
