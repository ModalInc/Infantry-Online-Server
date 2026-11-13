// CTF:X server-side bots - THE REPULSOR FIELD (Travis 10-17: "I never see any bots try to use their repulsor field (they use the repulsor charge
// though). The field is a repulsion explosion at the centre of their body: thrown + knockback. The Infiltrator has the Enhanced RFG, which knocks
// back projectiles AND vehicles - great for storming or getting in deep - but it should be held for when they are truly under threat, or to protect
// the bubble of area their team storms from, so storms last longer. Elite defender teams use it too.")
//
// Every class that may carry a field (Repulsor Field 1074; the Infiltrator's Enhanced RFG 1053) has it in its kit as a defensive tool, never as a
// weapon. RfgStep fires it (a burst at the body) in four cases only:
//   A. desperate: under 40 % health with an enemy within 130 px, or two shots about to land on it
//   B. a blast about to land on it (a grenade, a rocket): the burst shoves the shell off (CTFXShots.Repel does the shoving for any burst with
//      explosionRecoilWeapon) - when it is hurt below 80 %, or it is the stormer / an elite defender
//   C. the storm bubble: a bot that blinked in on its squad's storm, near the landing, with two enemies on it or three shots coming
//   D. an elite defender (veteran or ace post / guard of a base end) near its flag room, with two enemies on it or three close
// One burst per 7-10 s per bot; a squad's bubble gets at most one every 2.5 s, so the storm is not blown on the first shot.
/* PARSER GUARD: keep a block comment after the last line comment */

using System;
using System.Collections.Generic;

using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_CTF
{
    internal sealed partial class CTFXBot
    {
        internal const int RepulsorField = 1074, EnhancedRfg = 1053, ImprovedRfg = 1226;
        int _rfgNext, _rfgAt;

        /// <summary>The field this class may carry (0 = none): the Infiltrator's Enhanced RFG, the standard Repulsor Field for the rest.</summary>
        internal static int RfgFor(int cls)
        {
            if (CTFXSkillLogic.Allowed(EnhancedRfg, cls)) return EnhancedRfg;
            if (CTFXSkillLogic.Allowed(ImprovedRfg, cls)) return ImprovedRfg;
            return CTFXSkillLogic.Allowed(RepulsorField, cls) ? RepulsorField : 0;
        }

        static bool IsDefTool(int id) { return id == Repulsor || id == RepulsorField || id == EnhancedRfg || id == ImprovedRfg; }

        bool RfgStep(int now, int px, int py)
        {
            if (now - _rfgNext < 0 || IsDead || InDropship || now - _antiFireUntil < 0 || _kit == null) return false;
            int id = RfgFor(_class);
            if (id == 0) return false;
            int ri = KitIndex(id);
            if (ri < 0 || !CanShoot(now, ri) || EnergyCost(id) > _energy) return false;
            double hpF = (double)_state.health / Math.Max(1, _type.Hitpoints);
            // enemies close
            int close = 0, near = 0;
            foreach (CTFXBot o in Mgr.LiveBots)
            {
                if (o == this || o.IsDead || o.InDropship || o._team == _team) continue;
                double dx = o._state.positionX - px, dy = o._state.positionY - py, d2 = dx * dx + dy * dy;
                if (d2 <= 130.0 * 130.0) close++;
                if (d2 <= 230.0 * 230.0) near++;
            }
            foreach (Player p in _arena.PlayersIngame)
            {
                if (p.IsDead || p._team == _team || p._state == null) continue;
                double dx = p._state.positionX - px, dy = p._state.positionY - py, d2 = dx * dx + dy * dy;
                if (d2 <= 130.0 * 130.0) close++;
                if (d2 <= 230.0 * 230.0) near++;
            }
            // shots about to land: closest approach inside their reach within 0.5 s
            int n = Mgr.Inbound(this, now, _inbBuf, 8);      // Travis 11-08: always looked at now: a haywire / EMP shell comes at a bot at full health with nobody close
            int landing = 0, blasts = 0, drainBlasts = 0;
            for (int j = 0; j < n; j++)
            {
                int o = j * 6;
                double vv = _inbBuf[o + 2] * _inbBuf[o + 2] + _inbBuf[o + 3] * _inbBuf[o + 3];
                if (vv < 1e-6) continue;
                double t = -(_inbBuf[o] * _inbBuf[o + 2] + _inbBuf[o + 1] * _inbBuf[o + 3]) / vv;
                if (t < 2.0 || t > 50.0 || t > _inbBuf[o + 5]) continue;
                double mx = _inbBuf[o] + _inbBuf[o + 2] * t, my = _inbBuf[o + 1] + _inbBuf[o + 3] * t;
                double miss = Math.Sqrt(mx * mx + my * my);
                if (miss > _inbBuf[o + 4]) continue;
                landing++;
                if (_inbBuf[o + 4] >= 50.0 && t <= 30.0 && miss <= _inbBuf[o + 4] * 0.8) { blasts++; if (j < 8 && _inbDrain[j]) drainBlasts++; }
            }
            string why = null;
            bool elite = Human != null && (Human.Tier == "veteran" || Human.Tier == "ace");
            if (hpF < 0.40 && (close >= 1 || landing >= 2)) why = "desperate";
            else if (drainBlasts >= 1) why = "blast";      // Travis 11-08: "they don't repulse haywire grenades but do repulse frag grenades": a shell that takes no HP but strips the energy is shoved off at any health
            else if (blasts >= 1 && (hpF < 0.80 || Style == CTFXStyle.Stormer || (elite && Slot != null && (Slot.Style == CTFXStyle.Post || Slot.Style == CTFXStyle.Guard)))) why = "blast";
            else if (StormBubble(now, close, landing)) why = "bubble";
            else if (elite && Slot != null && (Slot.Style == CTFXStyle.Post || Slot.Style == CTFXStyle.Guard) && NearOwnFlagRoom(px, py) && (close >= 2 || (near >= 3 && landing >= 1))) why = "defence";
            if (why == null) return false;
            if (!FireNow(now, ri, _state.yaw, 40.0)) return false;
            _rfgNext = now + 7000 + _rng.Next(3001);
            _rfgAt = now;
            Mgr.RfgUses++;
            if (why == "bubble") Mgr.RfgBubble++; else if (why == "defence") Mgr.RfgDefence++; else if (why == "blast") Mgr.RfgBlast++; else Mgr.RfgDesperate++;
            return true;
        }

        /// <summary>A bot that blinked in on its squad's storm (or its stormer), within 300 px of the landing, with the squad's bubble under attack.</summary>
        bool StormBubble(int now, int close, int landing)
        {
            CTFXStorm st = Group != null ? Group.Storm : null;
            if (st == null || (st.Phase != CTFXStorm.Storm && st.Phase != CTFXStorm.Hold)) return false;
            bool member = Style == CTFXStyle.Stormer || st.Joined.Contains(_id);
            if (!member) return false;
            double dx = _state.positionX - st.StormX, dy = _state.positionY - st.StormY;
            if (dx * dx + dy * dy > 300.0 * 300.0) return false;
            if (!(close >= 2 || landing >= 3 || (close >= 1 && landing >= 1))) return false;
            int last;
            if (Mgr.RfgGroupAt.TryGetValue(Group, out last) && now - last < 2500) return false;      // one burst at a time for the squad
            Mgr.RfgGroupAt[Group] = now;
            return true;
        }

        /// <summary>Within 550 px of the flag room its base end keeps its flags in (an elite defender's ground).</summary>
        bool NearOwnFlagRoom(int px, int py)
        {
            int hx, hy;
            if (Group == null || Group.HomeEnd == null || Mgr.Flags == null || !Mgr.Flags.EndHomePx(Group.HomeEnd, out hx, out hy)) return false;
            double dx = px - hx, dy = py - hy;
            return dx * dx + dy * dy <= 550.0 * 550.0;
        }
    }

    internal sealed partial class CTFXBotManager
    {
        internal int RfgUses, RfgBubble, RfgDefence, RfgBlast, RfgDesperate;
        internal readonly Dictionary<CTFXBotGroup, int> RfgGroupAt = new Dictionary<CTFXBotGroup, int>();
        internal string RfgText() { return " | repulsor fields: " + RfgUses + " (desperate " + RfgDesperate + ", blast " + RfgBlast + ", storm bubble " + RfgBubble + ", defence " + RfgDefence + ")"; }
    }
}
/* PARSER GUARD - keep last */
