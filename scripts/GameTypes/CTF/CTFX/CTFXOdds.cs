// CTF:X server-side bots - THE ODDS (Travis 10-14 night: "they shouldn't necessarily rush into a fight that they are heavily
// disadvantaged most of the time, should be comparable numbers or slight deviations, if a bot is massively outnumbered, they'd
// probably retreat or call for help, or get summoned, or get bailed out by a teammate with a rep charge, stun, summon, heal, etc.").
//
// Every 0.5 s a bot counts the enemies it can see within 450 px against its side there (itself included). Outnumbered = 2+ more
// of them AND a ratio past its nerve (rookie 1.6, regular 1.8, veteran 2.2, ace 2.6). Most then back off toward the nearest
// teammates out of the crowd (or toward home) and call for help; some do not (the flaw: rookies often rush in anyway, aces rarely).
// Posts and anchors hold their spot but still call. A help call (at most every 12 s per bot): a summoner teammate who is not
// fighting may summon it out (hurt, or 3:1); free teammates within 1300 px come over; one with a Repulsor close by throws it into
// the crowd; it says so in team chat. Bountiers, masers and flag carriers keep their own logic.
/* PARSER GUARD: keep a block comment after the last line comment */

using System;
using System.Collections.Generic;

using InfServer.Game;

namespace InfServer.Script.GameType_CTF
{
    internal sealed partial class CTFXBot
    {
        int _oddsAt, _helpAt, _nerveUntil;
        bool _nerveIgnore;
        internal int OddsFoes, OddsMates;
        internal int OddsFx, OddsFy;                                  // the enemy crowd's centre

        internal int MaxHp { get { return _type.Hitpoints; } }

        /// <summary>MiniTP: the human enemies (not cloaked, alive, in this bot's own area) within r px. The bots' own foes are counted by their callers; the players are the rest of the enemy.</summary>
        int HumanFoesNear(double r)
        {
            int n = 0; double r2 = r * r, px = _state.positionX, py = _state.positionY;
            foreach (Player p in _arena.PlayersIngame)
            {
                if (p == null || p.IsDead || p._state == null || p._team == null || p._team == _team || p._team.IsSpec) continue;
                double dx = p._state.positionX - px, dy = p._state.positionY - py;
                if (dx * dx + dy * dy >= r2) continue;
                if (Mini != null && !Mgr.Mini.SameArea(_state.positionX, _state.positionY, p._state.positionX, p._state.positionY)) continue;
                if (PlayerCloak(p) == 0 || EsOn) n++;
            }
            return n;
        }

        /// <summary>A teammate's bail-out: its Repulsor thrown into the crowd at (x, y), if it carries one and may fire it now.</summary>
        internal bool TryRepulseAt(int now, int x, int y)
        {
            if (IsDead || InDropship || KitIndex(Repulsor) < 0 || now - _panicNext < 0) return false;
            return GuardRepulse(now, x, y);
        }

        void OddsStep(int now, int px, int py)
        {
            if (now - _oddsAt < 500) return;
            _oddsAt = now;
            OddsFoes = 0; OddsMates = 1;
            if (IsDead || InDropship || Style == CTFXStyle.Bountier || Style == CTFXStyle.Maser || _carrying) return;
            long sx = 0, sy = 0;
            const double R2 = 450.0 * 450.0;
            foreach (CTFXBot o in Mgr.LiveBots)
            {
                if (o == this || o.IsDead || o.InDropship || o.Released) continue;
                double dx = o._state.positionX - px, dy = o._state.positionY - py;
                if (dx * dx + dy * dy > R2) continue;
                if (Mini != null && !Mgr.Mini.SameArea(px, py, o._state.positionX, o._state.positionY)) continue;      // MiniTP: another area (a base behind a wall) is not this fight
                if (o._team == _team) OddsMates++;
                else if (!o.Cloaked || EsOn) { OddsFoes++; sx += o._state.positionX; sy += o._state.positionY; }
            }
            foreach (Player p in _arena.PlayersIngame)
            {
                if (p == null || p.IsDead || p._state == null) continue;
                double dx = p._state.positionX - px, dy = p._state.positionY - py;
                if (dx * dx + dy * dy > R2) continue;
                if (Mini != null && !Mgr.Mini.SameArea(px, py, p._state.positionX, p._state.positionY)) continue;
                if (p._team == _team) OddsMates++;
                else if (PlayerCloak(p) == 0 || EsOn) { OddsFoes++; sx += p._state.positionX; sy += p._state.positionY; }
            }
            if (OddsFoes == 0) return;
            OddsFx = (int)(sx / OddsFoes); OddsFy = (int)(sy / OddsFoes);
            double nerve = TierPick(1.6, 2.6);
            bool outnumbered = OddsFoes - OddsMates >= 2 && OddsFoes >= nerve * OddsMates;
            if (!outnumbered) return;
            Mgr.OddsOut++;
            if (now - _nerveUntil >= 0)
            {   // decided once per spell (4 s): the flaw, a rookie often rushes in anyway, an ace rarely
                _nerveIgnore = _rng.NextDouble() < TierPick(0.30, 0.03);
                _nerveUntil = now + 4000;
                if (_nerveIgnore) Mgr.OddsIgnored++;
            }
            bool holder = Style == CTFXStyle.Post || (Mgr.Cmdr != null && (Mgr.Cmdr.ActOf(this) == CTFXAct.Anchor || Mgr.Cmdr.ActOf(this) == CTFXAct.Defend)) || (Mini != null && Mgr.Mini.OddsStay(this));
            if (now - _helpAt >= 0) { _helpAt = now + 12000; Mgr.OddsHelp(this, now); }
            if (_nerveIgnore || holder || _retreating) return;
            int sfx, sfy;
            if (Mgr.Flags != null && Mgr.Flags.ChasingEnemy(this) && Mgr.Flags.Regroup(this, now, out sfx, out sfy))
            {   // a flag runner stages back from the flag and calls the team in, then pushes again (cycle 3)
                BreakAway(now, sfx, sfy, 600);
                Mgr.OddsRetreats++;
                return;
            }
            // back off: toward the nearest teammate outside the crowd (450-1500 px from here, on the far side), else away from it
            CTFXBot best = null;
            double bd = double.MaxValue;
            foreach (CTFXBot o in Mgr.LiveBots)
            {
                if (o == this || o.IsDead || o.InDropship || o._team != _team) continue;
                double dx = o._state.positionX - px, dy = o._state.positionY - py, d = Math.Sqrt(dx * dx + dy * dy);
                if (d < 450 || d > 1500) continue;
                if (Mini != null && !Mgr.Mini.SameArea(px, py, o._state.positionX, o._state.positionY)) continue;      // MiniTP: a goal in another area is dropped
                double fx = OddsFx - px, fy = OddsFy - py;
                if (dx * fx + dy * fy > 0) continue;                                    // not through the crowd
                if (d < bd) { bd = d; best = o; }
            }
            int mtx, mty;
            if (best != null) RetreatTo(now, best._state.positionX >> 4, best._state.positionY >> 4, 3000 + _rng.Next(2001));
            else if (Mini != null && Mgr.Mini.Retreat(this, out mtx, out mty)) RetreatTo(now, mtx, mty, 3000 + _rng.Next(2001));       // MiniTP: its area's fall-back anchor (the rear of the gauntlet, the home anchor)
            else BreakAway(now, OddsFx, OddsFy, 500);
            Mgr.OddsRetreats++;
        }
    }

    internal sealed partial class CTFXBotManager
    {
        internal int OddsOut, OddsIgnored, OddsRetreats, OddsCalls, OddsSummonOuts, OddsRescuers, OddsRepulses, HelpOffObjective;

        /// <summary>b is outnumbered and calls for help: a summon out, teammates coming over, a repulsor into the crowd.</summary>
        internal void OddsHelp(CTFXBot b, int now)
        {
            bool mini = b.Mini != null;
            if (mini ? b.Mini.Phase == MiniBot.Ship : !AtObjective(b._state.positionX, b._state.positionY)) { HelpOffObjective++; return; }   // 10-15 night: help is for the bases (MiniTP: anywhere but the ship room)
            OddsCalls++;
            if (mini) Mini.HelpCall(b, b.OddsFoes);
            else Chat.Say(b, Chat.Pick("help " + Chat.PlaceOf(b), "need help " + Chat.PlaceOf(b), "outnumbered " + Chat.PlaceOf(b), "inc " + b.OddsFoes + " " + Chat.PlaceOf(b)), 0.5, false);
            bool hurt = b._state.health < 0.6 * b.MaxHp;
            bool summoned = false;
            if (!mini && (hurt || b.OddsFoes >= 3 * b.OddsMates))       // MiniTP has its own summon swings (CTFXMini); the CTF:X summoner pull is not used there
            {   // a summoner not in a fight pulls it out (a player at a safe spot does this for a teammate in trouble)
                foreach (CTFXBot o in LiveBots)
                {
                    if (o == b || o.IsDead || o.InDropship || o._team != b._team || o.HasTarget || !Squads.CarriesSummoner(o)) continue;
                    double dx = o._state.positionX - b._state.positionX, dy = o._state.positionY - b._state.positionY;
                    if (dx * dx + dy * dy < 700.0 * 700.0) continue;                    // it would land in the same fight
                    if (Rng.NextDouble() > 0.6) continue;
                    bool ok = o.ClassId == CTFXBot.SK_MEDIC ? Squads.MedicSummonBot(o, b, now) : Squads.CanSummon(o, now) && Squads.Summon(o, b, now);
                    if (ok) { OddsSummonOuts++; summoned = true; Chat.Say(o, Chat.Pick("sum " + b.Slot.Name, "got you " + b.Slot.Name), 0.3, false); break; }
                }
            }
            if (summoned) return;
            if (InStaleBrawl(b._state.positionX, b._state.positionY)) { StaleRescueSkips++; return; }
            int want = Math.Min(4, Math.Max(1, b.OddsFoes - b.OddsMates + 1)), sent = 0;
            foreach (CTFXBot o in LiveBots)
            {
                if (sent >= want) break;
                if (o == b || o.IsDead || o.InDropship || o._team != b._team || o.Slot == null || o.HasTarget || o.Style == CTFXStyle.Post) continue;
                if (o.Style == CTFXStyle.Bountier || o.Style == CTFXStyle.Maser || (Flags != null && Flags.Carrying(o))) continue;
                double dx = o._state.positionX - b._state.positionX, dy = o._state.positionY - b._state.positionY, d2 = dx * dx + dy * dy;
                if (d2 > 1300.0 * 1300.0) continue;
                if (mini && !Mini.RescueOk(o, b)) continue;      // MiniTP: same area, not the engineer / medic / true anchor, not in the ship, not all-in
                if (d2 < 260.0 * 260.0 && o.TryRepulseAt(now, b.OddsFx, b.OddsFy)) { OddsRepulses++; sent++; continue; }   // the rep charge bail-out
                CTFXHuman h = o.Slot.Human;
                if (Rng.NextDouble() > 0.45 + 0.45 * (h != null ? h.Comply : 0.6)) continue;
                o.GoTo(b._state.positionX >> 4, b._state.positionY >> 4);
                OddsRescuers++; sent++;
            }
        }

        internal string OddsText()
        {
            return " | odds: outnumbered checks " + OddsOut + " (rushed in anyway " + OddsIgnored + ", backed off " + OddsRetreats + "), help calls " + OddsCalls
                + " (summoned out " + OddsSummonOuts + ", rescuers sent " + OddsRescuers + ", repulsor bail-outs " + OddsRepulses + "), not called away from the bases " + HelpOffObjective;
        }
    }
}
/* PARSER GUARD - keep last */
