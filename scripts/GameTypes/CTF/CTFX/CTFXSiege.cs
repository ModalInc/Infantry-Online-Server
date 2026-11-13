// CTF:X server-side bots - THE SIEGE AND THE MASER TEAMS (Travis 10-14): "The maser situation is more of a meme team, usually they'd sit
// at the safety of OUTSIDE of a large building, but on the corner where flags and actions are happening as action gets more and more
// concentrated at the brown base and the defending team gets further and further back in a tug of war scenario, maser teams would crop
// up only usually under those conditions where bots could team up and have fun, not meant to be efficient."
//
// The siege: the focus base's defenders have fallen back to their mid or inner line (CTFXLines.cs) with 4+ enemies within 800 px of
// their flag, for 15 s running. It lifts after 30 s with the line back out, or the attackers gone (under 3).
// Maser seats (maser:N) idle in the dropship while there is no siege. When one starts they come out (landing beside brown), walk to
// a spot OUTSIDE the building on the corner nearest the flag, inside maser reach of it, and burst through the wall at whoever is
// there (CTFXBot._wallRange targeting). When the siege lifts they recall to the ship and idle again.
/* PARSER GUARD: keep a block comment after the last line comment */

using System;
using System.Collections.Generic;
using System.Text;

using InfServer.Game;

namespace InfServer.Script.GameType_CTF
{
    internal sealed partial class CTFXCommand
    {
        internal bool SiegeOn;
        internal string SiegeEnd;
        int _siegeFoes, _siegeHeld, _siegeFoesPeak, _siegeAt, _siegeSince, _siegeCalm, _siegeFx, _siegeFy;
        readonly List<int[]> _maserSpots = new List<int[]>();                  // px, outside the building, near the flag
        internal int Sieges, SiegeMs, MaserOuts, MaserRecalls;

        void SiegeStep(int now)
        {
            if (now - _siegeAt < 1000) return;
            _siegeAt = now;
            CTFXTeamCmd t = null;
            foreach (CTFXTeamCmd o in Teams) if (o.End == _m.FocusEnd) { t = o; break; }
            LineSet ls;
            if (t == null || !_lineSets.TryGetValue(t, out ls)) return;
            int fx, fy;
            if (!_m.RouteNode(t.End + ":patrol", 0, out fx, out fy)) return;
            fx = fx * 16 + 8; fy = fy * 16 + 8;
            int foes = EnemiesNear(t.Team, fx, fy, 800.0);
            bool pressed = ls.Held >= 1 && foes >= 4;
            _siegeFoes = foes; _siegeHeld = ls.Held; if (foes > _siegeFoesPeak) _siegeFoesPeak = foes;
            if (!SiegeOn)
            {
                if (!pressed) { _siegeSince = 0; return; }
                if (_siegeSince == 0) { _siegeSince = now | 1; return; }
                if (now - _siegeSince < 15000) return;
                SiegeOn = true; SiegeEnd = t.End; _siegeFx = fx; _siegeFy = fy; _siegeCalm = 0; _siegeSince = now;
                Sieges++;
                MaserSpots(fx, fy);
                _m.Note("siege #" + Sieges + " on " + t.End + ": defenders back to the " + LineName[ls.Held] + " line, " + foes + " attackers near the flag; "
                    + _maserSpots.Count + " maser spots outside");
                return;
            }
            bool calm = ls.Held == 0 || foes < 3;
            if (!calm) { _siegeCalm = 0; return; }
            if (_siegeCalm == 0) { _siegeCalm = now | 1; return; }
            if (now - _siegeCalm < 30000) return;
            SiegeOn = false;
            SiegeMs += now - _siegeSince;
            _siegeSince = 0;
            _m.Note("siege on " + SiegeEnd + " lifted after " + (now - _siegeCalm) / 1000 + " s of calm");
        }

        /// <summary>Open tiles outside every base end and hall, 120-280 px from the flag, nearest first (the corner by the flag room).</summary>
        void MaserSpots(int fx, int fy)
        {
            _maserSpots.Clear();
            List<int[]> c = new List<int[]>();
            for (int r = 120; r <= 280; r += 32)
                for (int a = 0; a < 36; a++)
                {
                    int x = fx + (int)(Math.Cos(a * Math.PI / 18) * r), y = fy + (int)(Math.Sin(a * Math.PI / 18) * r);
                    int ox, oy;
                    if (!_m.NearestOpen(x >> 4, y >> 4, 1, out ox, out oy)) continue;
                    int px = ox * 16 + 8, py = oy * 16 + 8;
                    if (_m.AreaAt(px, py) != "out") continue;
                    c.Add(new int[] { px, py, r });
                }
            c.Sort((p, q) => p[2].CompareTo(q[2]));
            foreach (int[] p in c)
            {
                bool near = false;
                foreach (int[] q in _maserSpots) if (Dist(p[0], p[1], q[0], q[1]) < 40) { near = true; break; }
                if (!near) _maserSpots.Add(p);
                if (_maserSpots.Count >= 8) break;
            }
        }

        /// <summary>A maser bot's spot: the nearest of the spots to the flag, spread a little by its place in the team.</summary>
        internal bool MaserGoal(CTFXBot b, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            if (!On || !SiegeOn || _maserSpots.Count == 0) return false;
            int k = b.Slot != null ? b.Slot.StyleIndex : 0;
            int[] p = _maserSpots[Math.Min(_maserSpots.Count - 1, k % Math.Min(3, _maserSpots.Count))];
            _m.NearestOpenPx(p[0] + _m.Rng.Next(-24, 25), p[1] + _m.Rng.Next(-24, 25), out tx, out ty);
            return true;
        }

        /// <summary>A maser seat stays in the ship while nothing is under siege (Travis: they crop up only under those conditions).</summary>
        internal bool MaserIdle(CTFXBot b) { return On && b.Style == CTFXStyle.Maser && !SiegeOn; }

        /// <summary>The siege lifted: the maser bots out there recall to the ship (once every 2 s at most).</summary>
        int _maserHomeAt;
        void MaserStep(int now)
        {
            if (SiegeOn || now - _maserHomeAt < 2000) return;
            _maserHomeAt = now;
            foreach (CTFXBot b in _m.LiveBots)
            {
                if (b.Style != CTFXStyle.Maser || b.IsDead || b.Released || b.InDropship || b.HasTarget) continue;
                if (b.Recall(now)) { MaserRecalls++; return; }
            }
        }

        internal string SiegeText()
        {
            return " | sieges " + Sieges + (SiegeOn ? " (ON at " + SiegeEnd + ")" : "") + ", " + (SiegeMs + (SiegeOn ? Environment.TickCount - _siegeSince : 0)) / 1000
                + " s under siege (now: " + LineName[_siegeHeld] + " line, " + _siegeFoes + " near the flag, peak " + _siegeFoesPeak + "), maser outs " + MaserOuts + ", maser recalls " + MaserRecalls;
        }
    }
}
/* PARSER GUARD - keep last */
