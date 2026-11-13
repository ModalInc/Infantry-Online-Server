// CTF:X server-side bots - THE SWEPT BASE (Travis 10-15 test):
//  1. "the heavys stand way way too still, they dont even subtly fan out, point in the possible threatening direction": the five
//     posts shared two tiles (three heavies on one tile). Each post now has its own spot fanned 2-3 tiles round its turret room
//     tile, shuffles a tile about every 2.5-5.5 s, and faces the nearest enemy within 1000 px (else out of the room, away from
//     the flag) with a slow sweep, instead of turning circles.
//  2. "if an area is swept fully and confident, then elite bots shouldn't be sitting in a group together clustered unless the
//     engineer is building something or the captain is anchoring deeply in the flag room to summon a flagger, but otherwise they
//     should be guarding the outdoor area": the focus base is swept after 10 s with no enemy inside and no alarm. Then every
//     post but the flag room's keeper and every anchor but the Captain spreads to the outdoor guard spots north of top brown
//     (the plaza above the base wall and its two gates), the least-held spot first, at least 90 px from a teammate, facing out
//     down the canyon it covers. The first enemy back inside ends the sweep: they fall back to their posts and chokes.
/* PARSER GUARD: keep a block comment after the last line comment */

using System;
using System.Collections.Generic;

using InfServer.Game;

namespace InfServer.Script.GameType_CTF
{
    internal sealed partial class CTFXCommand
    {
        // ---- top brown's outdoor guard spots (tiles) and the point each watches: west gate, plaza west, the middle canyon, plaza east, the east canyon (checked on the tile map)
        static readonly int[][] OuterSrc = new int[][]
        {
            new int[] { 692, 1398, 700, 1360 }, new int[] { 722, 1398, 730, 1350 }, new int[] { 757, 1390, 757, 1350 },
            new int[] { 795, 1400, 823, 1350 }, new int[] { 823, 1390, 823, 1340 },
        };
        int[][] _outer;
        readonly Dictionary<ushort, int> _outerOf = new Dictionary<ushort, int>();   // bot id -> its outdoor spot
        readonly Dictionary<CTFXTeamCmd, int> _calmSince = new Dictionary<CTFXTeamCmd, int>();
        internal int SweptSpells, OuterSends, PostShuffles, PostRounds;

        /// <summary>The choke nearest the team's flag corner (its flag room), -1 when unknown.</summary>
        int FlagRoomChoke(CTFXTeamCmd t)
        {
            int fx, fy;
            if (_m.Flags == null || !_m.Flags.EndHomePx(t.End, out fx, out fy)) return -1;
            return NearestChoke(t, fx, fy);
        }

        void OuterInit()
        {
            if (_outer != null) return;
            _outer = new int[OuterSrc.Length][];
            for (int i = 0; i < OuterSrc.Length; i++)
            {
                int ox, oy;
                if (!_m.NearestOpen(OuterSrc[i][0], OuterSrc[i][1], 6, out ox, out oy)) { ox = OuterSrc[i][0]; oy = OuterSrc[i][1]; }
                _outer[i] = new int[] { ox, oy, OuterSrc[i][2] * 16 + 8, OuterSrc[i][3] * 16 + 8 };
            }
        }

        /// <summary>Per commander step: the focus base is swept after 10 s calm (no enemy in it, no alarm).</summary>
        void SweepStep(CTFXTeamCmd t, int now)
        {
            if (t.End != _m.FocusEnd) return;
            int since;
            bool calm = !t.Alarm && t.Threat == 0;
            if (!calm) { if (_calmSince.Remove(t)) ClearOuter(t); return; }
            if (!_calmSince.TryGetValue(t, out since)) { _calmSince[t] = now; return; }
            if (since != 0 && now - since >= 10000) { _calmSince[t] = 0; _sweptAt[t] = now; SweptSpells++; return; }
            int at;
            if (since == 0 && _sweptAt.TryGetValue(t, out at) && now - at >= 45000)
            {   // the push is over: back to the posts and chokes; calm again only after 60 s more (since lies 60 s ahead)
                _calmSince[t] = now + 60000; _sweptAt.Remove(t); ClearOuter(t); SweepReturns++;
            }
        }
        readonly Dictionary<CTFXTeamCmd, int> _sweptAt = new Dictionary<CTFXTeamCmd, int>();
        internal int SweepReturns;

        internal bool Swept(CTFXTeamCmd t)
        {
            int since;
            return t != null && t.End == _m.FocusEnd && _calmSince.TryGetValue(t, out since) && since == 0;
        }

        void ClearOuter(CTFXTeamCmd t)
        {
            List<ushort> gone = new List<ushort>();
            foreach (KeyValuePair<ushort, int> kv in _outerOf)
            {
                CTFXBot b = _m.GetBot(kv.Key);
                if (b == null || b.Slot == null || TeamOf(b.Slot) == t) gone.Add(kv.Key);
            }
            foreach (ushort id in gone) _outerOf.Remove(id);
        }

        /// <summary>The flag room's keeper: the post whose tile lies nearest the focus flag corner (lowest post number of those).</summary>
        bool Keeper(CTFXBot b)
        {
            if (b.Slot == null || b.Slot.Style != CTFXStyle.Post || _m.Flags == null) return false;
            int fx, fy;
            if (!_m.Flags.EndHomePx(_m.FocusEnd, out fx, out fy)) return b.Slot.PostIndex == 0;
            int best = -1; double bd = double.MaxValue;
            for (int i = 0; i < 2; i++)
            {
                int tx, ty;
                _m.PostTile(i, out tx, out ty);
                double d = Math.Pow(tx * 16 + 8 - fx, 2) + Math.Pow(ty * 16 + 8 - fy, 2);
                if (d < bd) { bd = d; best = i; }
            }
            return b.Slot.PostIndex == best;
        }

        /// <summary>A spot at the swept base's outdoor line for b: the one it holds, else the least-held one; its stand tile 90+ px
        /// clear of teammates.</summary>
        internal bool OuterGoal(CTFXBot b, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            CTFXTeamCmd t = b.Slot != null ? TeamOf(b.Slot) : null;
            if (!Swept(t)) return false;
            if (b.ClassId == CTFXBot.SK_CAPT && b.Slot.Intent != null && b.Slot.Intent.Act == CTFXAct.Anchor) return false;   // deep in the flag room
            if (Keeper(b)) return false;
            OuterInit();
            int k;
            if (!_outerOf.TryGetValue(b._id, out k))
            {
                int[] held = new int[_outer.Length];
                foreach (KeyValuePair<ushort, int> kv in _outerOf) if (kv.Value >= 0 && kv.Value < held.Length) held[kv.Value]++;
                k = -1; double bs = double.MaxValue;
                for (int i = 0; i < _outer.Length; i++)
                {
                    double sc = held[i] * 10000.0 + _m.Rng.NextDouble();
                    if (sc < bs) { bs = sc; k = i; }
                }
                _outerOf[b._id] = k;
                OuterSends++;
            }
            int[] o = _outer[k];
            int cx = o[0] * 16 + 8, cy = o[1] * 16 + 8;
            for (int i = 0; i < 8; i++)
            {
                int x = cx + _m.Rng.Next(-56, 57), y = cy + _m.Rng.Next(-40, 41);
                if (!_m.BodyFits(x, y) || NearestMatePx(b, x, y) < 90.0) continue;
                tx = x >> 4; ty = y >> 4;
                return true;
            }
            tx = o[0]; ty = o[1];
            return true;
        }

        /// <summary>At its outdoor spot: facing out down the canyon it covers, sweeping +-25 units.</summary>
        internal bool OuterYaw(CTFXBot b, int now, out float yaw)
        {
            yaw = 0f;
            int k;
            if (_outer == null || !_outerOf.TryGetValue(b._id, out k) || !Swept(b.Slot != null ? TeamOf(b.Slot) : null)) return false;
            int[] o = _outer[k];
            double dx = o[0] * 16 + 8 - b._state.positionX, dy = o[1] * 16 + 8 - b._state.positionY;
            if (dx * dx + dy * dy > 220 * 220) return false;                                   // still on its way
            float e;
            if (NearestEnemyYaw(b, 1000.0, out e)) { yaw = CTFXBot.WrapYaw(e + (float)(8.0 * Math.Sin(now / 900.0))); return true; }
            double period = 3500.0 + (b._id % 7) * 600.0;
            yaw = CTFXBot.WrapYaw(CTFXBot.YawTo(o[2] - b._state.positionX, o[3] - b._state.positionY) + (float)(25.0 * Math.Sin(now / period * 2.0 * Math.PI)));
            return true;
        }

        internal int TurretWatches;

        /// <summary>
        /// Travis 10-17: "defenders - at least 1 AC heavy should always stand by the outer turrets guarding them (not the inner turrets in the
        /// flag room, unless the defenders are pushed all the way to that lane)". The first two posts of a base (the flag-room keeper aside) stand 2-4 tiles behind a live OUTER
        /// turret of its end (the outer half of the end's spots by distance from the flag room), the one nearest the attackers with the alarm up.
        /// Pushed back to the flag room (the threat inside 450 px of it) it falls through to the usual post duty.
        /// </summary>
        bool TurretWatchGoal(CTFXBot b, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            if (b.Slot == null || b.Slot.StyleIndex > 1 || Keeper(b) || b.Group == null || b.Group.HomeEnd == null || _m.Flags == null) return false;
            int hx, hy;
            if (!_m.Flags.EndHomePx(b.Group.HomeEnd, out hx, out hy)) return false;
            CTFXTeamCmd pt = TeamOf(b.Slot);
            double tX = pt != null && pt.Alarm ? pt.ThreatX : hx, tY = pt != null && pt.Alarm ? pt.ThreatY : hy;
            if (pt != null && pt.Alarm && (tX - hx) * (tX - hx) + (tY - hy) * (tY - hy) < 450.0 * 450.0) return false;     // pushed all the way in
            List<CTFXTurretSpot> live = new List<CTFXTurretSpot>();
            foreach (CTFXTurretSpot t in _m.Turrets.Spots) if (t.End == b.Group.HomeEnd && t.TurretId != 0) live.Add(t);
            if (live.Count == 0) return false;
            live.Sort(delegate (CTFXTurretSpot x, CTFXTurretSpot y)
            {
                double dx = x.Tx * 16 - hx, dy = x.Ty * 16 - hy, ex = y.Tx * 16 - hx, ey = y.Ty * 16 - hy;
                return (ex * ex + ey * ey).CompareTo(dx * dx + dy * dy);          // farthest from the flag room first
            });
            int outer = Math.Max(1, (live.Count + 1) / 2);
            CTFXTurretSpot pick = null;
            double best = double.MaxValue;
            for (int i = 0; i < outer; i++)
            {
                double dx = live[i].Tx * 16 - tX, dy = live[i].Ty * 16 - tY, d2 = dx * dx + dy * dy;
                if (d2 < best) { best = d2; pick = live[i]; }
            }
            if (pick == null) return false;
            double vx = hx - (pick.Tx * 16 + 8), vy = hy - (pick.Ty * 16 + 8), vl = Math.Max(1.0, Math.Sqrt(vx * vx + vy * vy));
            for (int k = 3; k >= 1; k--)
            {   // 2-4 tiles behind it (toward the flag room), the first that fits and sees the turret
                int x = pick.Tx * 16 + 8 + (int)(vx / vl * 16 * (k + 1)), y = pick.Ty * 16 + 8 + (int)(vy / vl * 16 * (k + 1));
                if (!_m.BodyFits(x, y) || !_m.Los(x, y, pick.Tx * 16 + 8, pick.Ty * 16 + 8)) continue;
                tx = x >> 4; ty = y >> 4;
                TurretWatches++;
                return true;
            }
            return false;
        }

        /// <summary>
        /// A post's next stand tile: the outdoor line when the base is swept (not the keeper), else its own fanned spot round its
        /// turret room tile (2-3 tiles out, a different angle per post sharing the tile) plus a one-tile shuffle.
        /// </summary>
        internal bool PostGoal(CTFXBot b, int now, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            if (b.Slot == null) return false;
            if (OuterGoal(b, out tx, out ty)) return true;
            if (TurretWatchGoal(b, out tx, out ty)) return true;
            // one keeper holds the flag room; every other post walks the base's chokes in turn (25-40 s at each, a phase of its own),
            // the flag room's choke left to the keeper, so the heavies are a line on the ways in; with the alarm up, the choke nearest
            // the attackers
            CTFXTeamCmd pt = TeamOf(b.Slot);
            if (pt != null && pt.Chokes.Count > 0 && !Keeper(b))
            {
                int room = FlagRoomChoke(pt);
                int k;
                if (pt.Alarm) k = NearestChoke(pt, pt.ThreatX, pt.ThreatY);
                else
                {
                    int per = 25000 + (b._id % 4) * 5000;
                    k = (int)(((uint)b.Slot.PostIndex + (uint)(now + b._id * 3301) / (uint)per) % (uint)pt.Chokes.Count);
                    if (k == room && pt.Chokes.Count > 1) k = (k + 1) % pt.Chokes.Count;
                }
                if (k >= 0 && k < pt.Chokes.Count && AnchorGoalAt(pt.Chokes[k], 72, out tx, out ty)) { PostRounds++; return true; }
            }
            int pi = b.Slot.PostIndex, bx, by;
            _m.PostTile(pi, out bx, out by);
            int share = pi / 2;                                                                     // 0, 1, 2 on the same tile
            double ang = share * 2.1 + (pi % 2) * 0.7, r = share == 0 ? 0.0 : 36.0;
            for (int i = 0; i < 8; i++)
            {
                int x = bx * 16 + 8 + (int)(Math.Cos(ang) * r) + _m.Rng.Next(-16, 17), y = by * 16 + 8 + (int)(Math.Sin(ang) * r * 0.8) + _m.Rng.Next(-16, 17);
                if (!_m.BodyFits(x, y) || !_m.Los(x, y, bx * 16 + 8, by * 16 + 8)) { ang += 0.9; continue; }
                tx = x >> 4; ty = y >> 4;
                PostShuffles++;
                return true;
            }
            tx = bx; ty = by;
            return true;
        }

        /// <summary>A post with no target: its outdoor watch, else the nearest enemy within 1000 px, else out of its room (away from the
        /// flag), each with a slow sweep of its own.</summary>
        internal bool PostYaw(CTFXBot b, int now, out float yaw)
        {
            if (OuterYaw(b, now, out yaw)) return true;
            float e;
            if (NearestEnemyYaw(b, 1000.0, out e)) { yaw = CTFXBot.WrapYaw(e + (float)(10.0 * Math.Sin(now / 1100.0))); return true; }
            int fx, fy;
            double period = 4000.0 + (b._id % 5) * 700.0, sw = 30.0 * Math.Sin(now / period * 2.0 * Math.PI);
            if (_m.Flags != null && _m.Flags.EndHomePx(_m.FocusEnd, out fx, out fy))
            {
                double dx = b._state.positionX - fx, dy = b._state.positionY - fy;
                if (dx * dx + dy * dy > 24 * 24) { yaw = CTFXBot.WrapYaw(CTFXBot.YawTo(dx, dy) + (float)sw); return true; }
            }
            yaw = CTFXBot.WrapYaw((b._id % 4) * 60 + (float)sw);
            return true;
        }

        bool NearestEnemyYaw(CTFXBot b, double r, out float yaw)
        {
            yaw = 0f;
            int px = b._state.positionX, py = b._state.positionY, ex = 0, ey = 0;
            double bd = r * r;
            bool got = false;
            foreach (CTFXBot o in _m.LiveBots)
            {
                if (o.IsDead || o.Released || o.InDropship || o._team == b._team || o.Cloaked) continue;
                double d = Math.Pow(o._state.positionX - px, 2) + Math.Pow(o._state.positionY - py, 2);
                if (d < bd) { bd = d; ex = o._state.positionX; ey = o._state.positionY; got = true; }
            }
            foreach (Player p in _m.Arena.PlayersIngame)
            {
                if (p.IsDead || p.IsSpectator || p._team == b._team || p._state == null) continue;
                double d = Math.Pow(p._state.positionX - px, 2) + Math.Pow(p._state.positionY - py, 2);
                if (d < bd) { bd = d; ex = p._state.positionX; ey = p._state.positionY; got = true; }
            }
            if (got) yaw = CTFXBot.YawTo(ex - px, ey - py);
            return got;
        }

        double NearestMatePx(CTFXBot me, int x, int y)
        {
            double bd = double.MaxValue;
            foreach (CTFXBot o in _m.LiveBots)
            {
                if (o == me || o.IsDead || o.Released || o._team != me._team) continue;
                double d = Math.Pow(o._state.positionX - x, 2) + Math.Pow(o._state.positionY - y, 2);
                if (d < bd) bd = d;
            }
            return Math.Sqrt(bd);
        }

        internal string SweepText()
        {
            return ", swept spells " + SweptSpells + " (outdoor sends " + OuterSends + ", now out " + _outerOf.Count + ", back inside after the push " + SweepReturns + "), post shuffles " + PostShuffles + ", post rounds on the chokes " + PostRounds;
        }
    }
}
