// CTF:X server-side bots - DODGING (Travis 10-17: "bots don't strafe or react to bullets the way elite players do ... they should be able to
// asymmetrically dodge one person's bullets while fighting another ... unless they're doing a mad dash and can reasonably escape").
//
// The strafe of combat was a metronome, blind to what was flying at the bot. Now every bot with enemy shots flying near it (the human's
// bullets, seen in his item uses; every bot and turret shot in CTFXShots) weighs its candidate moves: it simulates each heading for the next
// 0.7 s against each shot's straight line and scores how far the shots would pass from its body (a blast counts with its radius). It takes
// the heading that leaves the least penetration, with a price for leaving its own plan (its strafe, or its run: a flag carrier, a traveller
// or a retreat pays three times as much, so a dash only swerves when the swerve is cheap). The seat's level decides how often and how well:
// a shot needs the seat's reaction time to be seen, and a rookie notices and picks worse than an ace (TierPick). Several shooters are
// handled by the same sum, which is what makes one man's bullets dodgeable while the bot shoots at another.
/* PARSER GUARD: keep a block comment after the last line comment */

using System;
using System.Collections.Generic;

using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_CTF
{
    internal sealed class CTFXInb
    {
        public Team Team;
        public double X, Y, Vx, Vy;         // world px (y as the map y / 0.7) and world px per 10 ms tick
        public int Born, Until;
        public double Reach;                // a body this close to the shot's line is hit (blast + body)
        public bool Drain;                  // an energy-only shell (haywire, EMP): no HP, but it strips the energy
    }

    internal sealed partial class CTFXBotManager
    {
        readonly List<CTFXInb> _inb = new List<CTFXInb>();
        readonly Dictionary<int, double> _blast = new Dictionary<int, double>();
        internal int Dodges, DodgeEvals, DodgeShotsSeen;
        internal bool Dodge = true;                // dodge=0 would switch it off (not wired to the config yet)
        internal string DodgeText() { return " | dodge: swerves " + Dodges + " of " + DodgeEvals + " evals, stand-and-fight polls " + StandAndFight + ", turret chips " + ChipTurrets; }

        double BlastOf(ItemInfo.Projectile p)
        {
            double r;
            lock (_blast)
            {
                if (_blast.TryGetValue(p.id, out r)) return r;
                try { r = Helpers.getMaxBlastRadius(p); } catch (Exception) { r = 0; }
                _blast[p.id] = r;
            }
            return r;
        }

        /// <summary>Arena thread: a human used item itemId at (x, y) facing yaw (any projectile, not only blasts).</summary>
        internal void NoteInbound(Team team, int x, int y, byte yaw, int itemId)
        {
            ItemInfo it = AssetManager.Manager.getItemByID(itemId);
            ItemInfo.MultiUse mu = it as ItemInfo.MultiUse;
            bool multi = false;
            if (mu != null && mu.childItems != null && mu.childItems.Count > 0) { multi = mu.childItems.Count > 1; it = AssetManager.Manager.getItemByID(mu.childItems[0].id); }
            ItemInfo.Projectile p = it as ItemInfo.Projectile;
            if (p == null || p.muzzleVelocity <= 0) return;
            double r = yaw * 1.5 * Math.PI / 180.0, v = Math.Max(1.0, p.muzzleVelocity / 1000.0);
            int now = Environment.TickCount;
            CTFXInb s = new CTFXInb();
            s.Team = team; s.X = x; s.Y = y / 0.7; s.Vx = Math.Sin(r) * v; s.Vy = -Math.Cos(r) * v;
            s.Born = now; s.Until = now + (int)Math.Min(1500.0, Math.Max(150.0, Math.Abs(p.aliveTime) * 10.0));
            s.Reach = BlastOf(p) + 14.0 + (multi ? 12.0 : 0.0);
            s.Drain = CTFXBot.IsDrainOnly(p.id);
            lock (_inb) { if (_inb.Count < 160) _inb.Add(s); }
        }

        /// <summary>The enemy shots near b now: 6 numbers each (world x, y relative to b, vx, vy per tick, reach, ticks left). Returns how many.</summary>
        internal int Inbound(CTFXBot b, int now, double[] buf, int max)
        {
            int n = 0;
            double bx = b._state.positionX, by = b._state.positionY / 0.7, lim2 = 520.0 * 520.0;
            lock (_inb)
            {
                for (int i = _inb.Count - 1; i >= 0; i--)
                {
                    CTFXInb s = _inb[i];
                    if (now - s.Until > 0) { _inb.RemoveAt(i); continue; }
                    if (s.Team == b._team || n >= max) continue;
                    double t0 = (now - s.Born) / 10.0, sx = s.X + s.Vx * t0 - bx, sy = s.Y + s.Vy * t0 - by;
                    if (sx * sx + sy * sy > lim2) continue;
                    int o = n * 6; buf[o] = sx; buf[o + 1] = sy; buf[o + 2] = s.Vx; buf[o + 3] = s.Vy; buf[o + 4] = s.Reach; buf[o + 5] = (s.Until - now) / 10.0;
                    if (n < 8) b._inbDrain[n] = s.Drain;
                    n++;
                }
            }
            List<CTFXShot> live = Shots.LiveShots;
            for (int i = 0; i < live.Count && n < max; i++)
            {
                CTFXShot sh = live[i];
                if (sh.Team == b._team || sh.Inactive || sh.P == null || sh.Owner == b) continue;
                double sx = sh.X - bx, sy = sh.Y / 0.7 - by;
                if (sx * sx + sy * sy > lim2) continue;
                int o = n * 6; buf[o] = sx; buf[o + 1] = sy; buf[o + 2] = sh.Vx; buf[o + 3] = sh.Vy; buf[o + 4] = BlastOf(sh.P) + 14.0;
                if (n < 8) b._inbDrain[n] = CTFXBot.IsDrainOnly(sh.P.id);
                buf[o + 5] = sh.Life > 0 ? Math.Max(1, Math.Min(70, sh.Life - sh.Age)) : 70;
                n++;
            }
            return n;
        }
    }

    internal sealed partial class CTFXBot
    {
        readonly double[] _inbBuf = new double[8 * 6];
        int _dodgeNext, _dodgeUntil;
        float _dodgeYaw;

        /// <summary>The penetration of the shots into a body that sets off along heading yaw (-1000 = it stands), over the next 0.7 s.</summary>
        double DodgeCost(double[] s, int n, float yaw, int reactTicks)
        {
            double dx = 0, dy = 0;
            if (yaw > -999f) { double r = yaw * 1.5 * Math.PI / 180.0; dx = Math.Sin(r) * 2.4; dy = -Math.Cos(r) * 2.4; }
            double cost = 0;
            for (int j = 0; j < n; j++)
            {
                int o = j * 6;
                double life = s[o + 5];
                for (int t = Math.Max(3, reactTicks); t <= 70 && t <= life; t += 4)
                {
                    int m = t < 25 ? t : 25;                                  // the body is at speed after 0.25 s
                    double rx = s[o] + s[o + 2] * t - dx * m, ry = s[o + 1] + s[o + 3] * t - dy * m;
                    double d = Math.Sqrt(rx * rx + ry * ry), pen = s[o + 4] - d;
                    if (pen > 0) cost += pen;
                }
            }
            return cost;
        }

        /// <summary>
        /// Called by Think before Drive. Weighs standing and the headings around the planned move against the shots flying at the body, and
        /// swerves (or stops, or steps off the strafe line) when that clearly helps. dash: a flag run, a traveller or a retreat (the swerve pays more).
        /// </summary>
        void DodgeAdjust(int now, int px, int py, bool dash, bool firing, ref bool move, ref float moveYaw)
        {
            if (!Mgr.Dodge || InDropship || now - _antiThrustUntil < 0) return;
            if (now - _dodgeUntil < 0) { move = true; moveYaw = _dodgeYaw; return; }          // finishing the last step
            if (now - _dodgeNext < 0) return;
            _dodgeNext = now + 70;
            int n = Mgr.Inbound(this, now, _inbBuf, 8);
            if (n == 0) return;
            Mgr.DodgeEvals++;
            double react = (Human != null ? Human.ReactMean : 260) * 0.5 / 10.0;            // ticks: seeing a bullet is quicker than answering a target
            int reactTicks = (int)react;
            double baseCost = DodgeCost(_inbBuf, n, move ? moveYaw : -1000f, reactTicks);
            if (baseCost < 6.0) return;                                                      // nothing is going to touch it
            if (_rng.NextDouble() > TierPick(0.30, 0.90)) return;                            // did not notice this time (rookies miss most)
            double lambda = dash ? 3.0 : 0.4, noise = TierPick(14.0, 0.0);
            float best = -1000f; double bestScore = baseCost;
            if (!dash && !firing) { double c0 = DodgeCost(_inbBuf, n, -1000f, reactTicks); if (c0 < bestScore) bestScore = c0; }
            float planned = move ? moveYaw : _state.yaw;
            float[] offs = dash ? new float[] { 0f, 20f, -20f, 40f, -40f, 60f, -60f } : new float[] { 0f, 30f, -30f, 60f, -60f, 90f, -90f, 120f };
            foreach (float off in offs)
            {
                float y = WrapYaw(planned + off);
                if (ProbeOpen(px, py, y, 32) < 32) continue;
                double c = DodgeCost(_inbBuf, n, y, reactTicks) + lambda * Math.Abs(off) * 0.5 + (noise > 0 ? CTFXHuman.Gauss(_rng, 0.0, noise) : 0.0);
                if (c < bestScore - 4.0) { bestScore = c; best = y; }
            }
            if (best < -999f) { if (!dash && !firing && bestScore < baseCost - 4.0) { move = false; _dodgeUntil = 0; Mgr.Dodges++; } return; }   // the best answer is to stand still
            if (bestScore > baseCost * 0.8) return;                                          // not clearly better than its plan
            move = true; moveYaw = best; _dodgeYaw = best; _dodgeUntil = now + 160;
            Mgr.Dodges++;
        }
    }
}
/* PARSER GUARD - keep last */
