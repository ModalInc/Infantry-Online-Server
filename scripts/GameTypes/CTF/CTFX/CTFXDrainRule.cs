// CTF:X server-side bots - THE ENERGY-ONLY RULE (Travis 10-17: "use of beamers even when alone, but they're meant to be supportive energy drain,
// they're no use in 1v1 combat unless used sparingly (3 shots then switch to a weapon that deals hp damage) - the universal rule for weapons that
// only do energy damage: energy damage is only good to drain the shield; with no follow-up weapon to damage and kill, there was no point").
//  - An item is energy-only when its damage is all energy (the energy type, or a type routed to energy) and none lands as HP; worked out from the
//    item data, once per item, so every beamer and any future drain weapon follows it.
//  - Three shots in a row of energy-only weapons at one target (CTFXBot.ComboShot) and a bot that carries a weapon with HP damage is barred from
//    them for 8 s: the follow-up kills the shield-down target. A bot with nothing else to shoot (or a drain pack with its pack finisher) is free.
/* PARSER GUARD: keep a block comment after the last line comment */

using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_CTF
{
    internal sealed partial class CTFXBot
    {
        static readonly Dictionary<int, int> _dmgKind = new Dictionary<int, int>();   // item -> 0 no damage, 1 HP, 2 energy only, 3 both
        int _drainStreak, _drainBanUntil;

        static int DmgKind(int id)
        {
            lock (_dmgKind)
            {
                int k;
                if (_dmgKind.TryGetValue(id, out k)) return k;
                ItemInfo it = AssetManager.Manager.getItemByID(id);
                ItemInfo.MultiUse mu = it as ItemInfo.MultiUse;
                if (mu != null && mu.childItems != null && mu.childItems.Count > 0) { ItemInfo c = AssetManager.Manager.getItemByID(mu.childItems[0].id); if (c != null) it = c; }
                ItemInfo.Projectile p = it as ItemInfo.Projectile;
                bool hp = false, en = false;
                for (int depth = 0; depth < 3 && p != null; depth++)
                {
                    int[] inner = new int[] { p.kineticDamageInner, p.explosiveDamageInner, p.electronicDamageInner, p.psionicDamageInner, p.bypassDamageInner, p.energyDamageInner };
                    int[] outer = new int[] { p.kineticDamageOuter, p.explosiveDamageOuter, p.electronicDamageOuter, p.psionicDamageOuter, p.bypassDamageOuter, p.energyDamageOuter };
                    int[] mode = new int[] { p.kineticDamageMode, p.explosiveDamageMode, p.electronicDamageMode, p.psionicDamageMode, p.bypassDamageMode, p.energyDamageMode };
                    for (int t = 0; t < 6; t++)
                    {
                        if (inner[t] <= 0 && outer[t] <= 0) continue;
                        if (t == 5 || mode[t] == 2) en = true; else hp = true;
                    }
                    p = AssetManager.Manager.getItemByID(p.explodeItem) as ItemInfo.Projectile;     // a burst child can carry the damage
                }
                k = (hp ? 1 : 0) | (en ? 2 : 0);
                _dmgKind[id] = k;
                return k;
            }
        }

        static bool IsEnergyOnly(int id) { return DmgKind(id) == 2; }

        /// <summary>It carries a usable weapon whose damage lands as HP (the follow-up that makes draining worth it).</summary>
        bool HasHpFinisher()
        {
            if (_kit == null) return false;
            for (int k = 0; k < _kit.Length; k++)
                if ((_badMask & (1 << k)) == 0 && !Dry(k) && !IsDefTool(_kit[k].Id) && (DmgKind(_kit[k].Id) & 1) != 0 && EnergyCost(_kit[k].Id) >= 0) return true;
            return false;
        }

        /// <summary>From ComboShot: count the energy-only shots at this target; the third bars them for 8 s.</summary>
        void DrainRuleShot(int now, int id)
        {
            int k = DmgKind(id);
            if (k == 2)
            {
                _drainStreak++;
                if (_drainStreak >= 3 && HasHpFinisher() && !(Style == CTFXStyle.Drain && Mgr.Bounty != null && Mgr.Bounty.PackFinisher(Group) != null))
                {
                    _drainBanUntil = now + 8000; _drainStreak = 0; _comboPick = true; Mgr.DrainCuts++;
                }
            }
            else if ((k & 1) != 0) _drainStreak = 0;
        }

        /// <summary>From ComboBonus: an energy-only weapon is out of the running while the bar is on (a bot with a finisher).</summary>
        double DrainRuleBonus(int id)
        {
            if (Environment.TickCount - _drainBanUntil < 0 && IsEnergyOnly(id) && HasHpFinisher()) return -600.0;
            return 0.0;
        }
    }

    internal sealed partial class CTFXBotManager
    {
        internal int DrainCuts;      // times a bot stopped draining after 3 shots to switch to its HP weapon
    }
}
/* PARSER GUARD - keep last */
