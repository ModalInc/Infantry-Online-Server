// CTF:X server-side bots - THE POCKET MEDIC (overnight cycle 4, 10-14 night). Travis wants an elite few to reach 30k bounty and hold
// it without cheating; in cycles 1-3 the peak was 600-1050 because nobody kept the killers alive. A player's answer is the pocket
// medic: one medic glued to the team's best killer all game. A Medic (not a hunt-pack drainer or bountier) adopts the living
// teammate with the highest bounty (150+), or failing that a Titan AC post, within 2500 px that no other medic already pockets,
// and stays 90-170 px behind it (away from the enemy it faces), re-checking every 8 s. Its area heal (HealStep) does the rest. The
// patron, hurt, backs toward its medic rather than home (MedicGoal already prefers a medic within 900 px).
/* PARSER GUARD: keep a block comment after the last line comment */

using System;
using System.Collections.Generic;

namespace InfServer.Script.GameType_CTF
{
    internal sealed partial class CTFXBot
    {
        CTFXBot _patron;
        internal bool PitMedic;
        int _patronAt;

        internal bool Pocketing { get { return _patron != null && !_patron.IsDead; } }

        bool PocketGoal(int now, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            if (_class != SK_MEDIC || Style == CTFXStyle.Drain || Style == CTFXStyle.Bountier || Group == null || _carrying) return false;
            if (_patron != null && (_patron.IsDead || _patron.InDropship || _patron.Released)) { Mgr.Unpocket(_patron, this); _patron = null; }
            if (now - _patronAt >= 0)
            {
                _patronAt = now + 8000;
                CTFXBot best = null;
                double bs = 0;
                foreach (CTFXBot o in Mgr.LiveBots)
                {
                    if (o == this || o.IsDead || o.InDropship || o._team != _team || o.Slot == null || o._class == SK_MEDIC) continue;
                    CTFXBot taken = Mgr.PocketOf(o);
                    if (taken != null && taken != this) continue;
                    double dx = o._state.positionX - _state.positionX, dy = o._state.positionY - _state.positionY;
                    if (dx * dx + dy * dy > 2500.0 * 2500.0) continue;
                    int bty = o.Slot.Bounty;
                    double sc = bty >= 150 ? bty : (o.Style == CTFXStyle.Post && o.Group != null && o.Group.Kind == CTFXKind.Titan ? 100 : 0);
                    if (sc > bs) { bs = sc; best = o; }
                }
                if (best != _patron)
                {
                    if (_patron != null) Mgr.Unpocket(_patron, this);
                    _patron = best;
                    if (best != null) { Mgr.Pocket(best, this); Mgr.PocketPairs++; }
                }
            }
            if (_patron == null)
            {   // Travis 10-15: the medic pit stop - with no killer to pocket, it holds a safe back room at home (the flag room) and the
                // hurt rotate back to it (MedicGoal reaches 1500 px for a pit medic)
                bool otherPit = false;
                foreach (CTFXBot o in Mgr.LiveBots) if (o != this && !o.IsDead && o._team == _team && o.PitMedic) { otherPit = true; break; }
                if (!otherPit && Group != null && Group.HomeEnd != null && Mgr.RouteNode(Group.HomeEnd + ":patrol", 0, out tx, out ty))
                {
                    int ox, oy;
                    if (Mgr.NearestOpen(tx + _rng.Next(-3, 4), ty + _rng.Next(-3, 4), 3, out ox, out oy)) { tx = ox; ty = oy; }
                    PitMedic = true;
                    return true;
                }
                PitMedic = false;
                return false;
            }
            PitMedic = false;
            // behind it: away from the enemy crowd it faces (or its target), 90-170 px
            double ax = 0, ay = 0;
            if (_patron.OddsFoes > 0) { ax = _patron._state.positionX - _patron.OddsFx; ay = _patron._state.positionY - _patron.OddsFy; }
            else { float fx, fy; YawVec(_patron._state.yaw, out fx, out fy); ax = -fx; ay = -fy; }
            double al = Math.Sqrt(ax * ax + ay * ay);
            if (al < 0.01) { ax = 0; ay = 1; al = 1; }
            double r = 90 + _rng.Next(81), sp = (_rng.NextDouble() - 0.5) * 1.2;
            double ux = ax / al, uy = ay / al, vx = ux * Math.Cos(sp) - uy * Math.Sin(sp), vy = ux * Math.Sin(sp) + uy * Math.Cos(sp);
            Mgr.NearestOpenPx(_patron._state.positionX + (int)(vx * r), _patron._state.positionY + (int)(vy * r), out tx, out ty);
            return true;
        }
    }

    internal sealed partial class CTFXBotManager
    {
        readonly Dictionary<CTFXBot, CTFXBot> _pocket = new Dictionary<CTFXBot, CTFXBot>();   // patron -> its medic
        internal int PocketPairs, PitStops;

        /// <summary>A pit medic of b's team within r px.</summary>
        internal CTFXBot PitMedicFor(CTFXBot b, double r)
        {
            foreach (CTFXBot o in LiveBots)
            {
                if (o == b || o.IsDead || o._team != b._team || !o.PitMedic) continue;
                double dx = o._state.positionX - b._state.positionX, dy = o._state.positionY - b._state.positionY;
                if (dx * dx + dy * dy <= r * r) return o;
            }
            return null;
        }

        internal CTFXBot PocketOf(CTFXBot patron) { CTFXBot m; return patron != null && _pocket.TryGetValue(patron, out m) && !m.IsDead ? m : null; }
        internal void Pocket(CTFXBot patron, CTFXBot medic) { _pocket[patron] = medic; }
        internal void Unpocket(CTFXBot patron, CTFXBot medic) { CTFXBot m; if (patron != null && _pocket.TryGetValue(patron, out m) && m == medic) _pocket.Remove(patron); }

        internal string EliteText()
        {
            int now = 0;
            foreach (CTFXBot b in LiveBots) if (b.Pocketing) now++;
            int pit = 0;
            foreach (CTFXBot b in LiveBots) if (b.PitMedic && !b.IsDead) pit++;
            return " | pocket medics: now " + now + ", pairings " + PocketPairs + ", pit medics now " + pit + ", pit stops " + PitStops;
        }
    }
}
/* PARSER GUARD - keep last */
