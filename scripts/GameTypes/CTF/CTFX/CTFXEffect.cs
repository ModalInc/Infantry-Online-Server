// CTF:X / MiniTP server-side bots - WHAT WORKS AGAINST WHAT (Travis 11-08: "they shouldn't be attacking the green structures with weapons that don't damage them, as it has kinetic
// protection, so things like smg don't work on it ... the more elegant solution is for it to just understand what weapons work against what, as a shared brain, so it doesn't have to
// calculate in the run time of the script").
//
// One table for every bot: HpPerHit(weapon item, target vehicle type) = the HP one direct hit takes off that vehicle, worked out ONCE per pair from the item and vehicle data with the
// engine's own formula (Vehicle.applyExplosionDamage: per damage type, (gross - armor.SelfIgnore) x (1 - armor.SelfReduction / 1000), summed, applied only when above zero, in
// thousandths of HP). Energy damage is not applied to computers by the engine, so it does not count. The result is cached for the life of the script; a bot only does a dictionary lookup.
// Used for structures (a computer with no gun: the green portals 413 / 436, generators ...). Gun turrets keep CTFXBot's measured NoTurretDamage list (haywire silences a turret without
// taking HP, so a pure HP table would wrongly rule it out there).
/* PARSER GUARD: keep a block comment after the last line comment */

using System;
using System.Collections.Generic;
using System.Text;

using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_CTF
{
    internal static class CTFXEffect
    {
        static readonly Dictionary<long, double> _hp = new Dictionary<long, double>();
        static readonly HashSet<int> _reported = new HashSet<int>();

        /// <summary>HP one direct hit of weapon item <paramref name="weaponId"/> takes off vehicle type <paramref name="vehId"/>; -1 = unknown (not a projectile, no vehicle data).</summary>
        internal static double HpPerHit(int weaponId, int vehId)
        {
            long key = ((long)weaponId << 20) | (uint)vehId;
            lock (_hp)
            {
                double v;
                if (_hp.TryGetValue(key, out v)) return v;
                try { v = Compute(weaponId, vehId); } catch (Exception) { v = -1; }
                _hp[key] = v;
                return v;
            }
        }

        /// <summary>True when the weapon hurts the vehicle (a hit takes at least 1 HP off), or when nothing is known about it (never blocks on a guess).</summary>
        internal static bool Works(int weaponId, int vehId)
        {
            double v = HpPerHit(weaponId, vehId);
            return v < 0 || v >= 1.0;
        }

        static double Stage(int radius, int inner, int outer, ArmorValues a)
        {
            if (radius < 1 || a == null) return 0.0;
            double fraction = 1.0 - 1.0 / radius;                                    // a direct hit: radius clamped to 1 by the engine
            double gross = (inner - outer) * fraction + outer;
            return (gross - a.SelfIgnore) * (1.0 - a.SelfReduction / 1000.0);
        }

        static double OneProjectile(ItemInfo.Projectile p, VehInfo veh)
        {
            double total = 0.0;
            for (int depth = 0; depth < 3 && p != null; depth++)
            {   // the shot itself, then the burst child it explodes into
                double net = Stage(p.kineticDamageRadius, p.kineticDamageInner, p.kineticDamageOuter, veh.Armors[0])
                           + Stage(p.explosiveDamageRadius, p.explosiveDamageInner, p.explosiveDamageOuter, veh.Armors[1])
                           + Stage(p.electronicDamageRadius, p.electronicDamageInner, p.electronicDamageOuter, veh.Armors[2])
                           + Stage(p.psionicDamageRadius, p.psionicDamageInner, p.psionicDamageOuter, veh.Armors[3])
                           + Stage(p.bypassDamageRadius, p.bypassDamageInner, p.bypassDamageOuter, veh.Armors[4]);
                if (net > 0) total += net;
                p = AssetManager.Manager.getItemByID(p.explodeItem) as ItemInfo.Projectile;
            }
            return total;
        }

        static double Compute(int weaponId, int vehId)
        {
            VehInfo veh = AssetManager.Manager.getVehicleByID(vehId);
            if (veh == null || veh.Armors == null || veh.Armors.Length < 5) return -1;
            ItemInfo it = AssetManager.Manager.getItemByID(weaponId);
            if (it == null) return -1;
            double total = 0.0;
            ItemInfo.MultiUse mu = it as ItemInfo.MultiUse;
            if (mu != null && mu.childItems != null && mu.childItems.Count > 0)
            {   // a multi-use item fires its children (a shotgun's pellets are several of them)
                foreach (var ch in mu.childItems)
                {
                    ItemInfo.Projectile cp = AssetManager.Manager.getItemByID(ch.id) as ItemInfo.Projectile;
                    if (cp != null) total += OneProjectile(cp, veh);
                }
                return total / 1000.0;
            }
            ItemInfo.Projectile p = it as ItemInfo.Projectile;
            if (p == null) return -1;
            return OneProjectile(p, veh) / 1000.0;
        }

        static readonly int[] ReportWeapons = { 1109, 1096, 3038, 3028, 1003, 1030, 306, 1044, 1038, 1011, 1176, 1064, 1012, 1106, 1107, 1082, 1083, 3009, 1163, 1195 };

        /// <summary>One log line per vehicle type, the first time a bot asks about it: which of the common weapons work (HP per hit) and which do nothing.</summary>
        internal static string ReportOnce(int vehId)
        {
            lock (_hp) { if (!_reported.Add(vehId)) return null; }
            VehInfo veh = AssetManager.Manager.getVehicleByID(vehId);
            StringBuilder ok = new StringBuilder(), no = new StringBuilder();
            foreach (int w in ReportWeapons)
            {
                ItemInfo it = AssetManager.Manager.getItemByID(w);
                if (it == null) continue;
                double hp = HpPerHit(w, vehId);
                string nm = (it.name ?? ("#" + w)) + "(" + w + ")";
                if (hp < 0) continue;
                if (hp >= 1.0) ok.Append(ok.Length == 0 ? "" : ", ").Append(nm).Append(' ').Append(hp.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
                else no.Append(no.Length == 0 ? "" : ", ").Append(nm);
            }
            return "[CTFXEffect] vehicle " + vehId + (veh != null ? " (" + veh.Name + ")" : "") + " takes HP/hit from: " + (ok.Length == 0 ? "nothing in the list" : ok.ToString()) + " | no damage from: " + (no.Length == 0 ? "-" : no.ToString());
        }
    }

    internal sealed partial class CTFXBot
    {
        /// <summary>A structure (a computer with no gun: the green portals, a generator ...): does anything in this kit hurt it? From the shared table, so no per-shot maths.</summary>
        bool CanHurtStructure(Computer c)
        {
            if (_kit == null || c == null || c._type == null) return false;
            int vid = c._type.Id;
            if (c._type.Destroyable != 0) { string rep = CTFXEffect.ReportOnce(vid); if (rep != null) Mgr.MiniLog(rep); }
            for (int i = 0; i < _kit.Length; i++)
            {
                if ((_badMask & (1 << i)) != 0 || Dry(i) || IsDefTool(_kit[i].Id)) continue;
                int id = _kit[i].Id;
                if (IsMine(id)) continue;
                if (EnergyCost(id) > _energy) continue;
                if (CTFXEffect.Works(id, vid)) return true;
            }
            return false;
        }
    }
}
/* PARSER GUARD - keep last */
