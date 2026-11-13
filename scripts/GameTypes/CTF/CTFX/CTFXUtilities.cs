// CTF:X server-side bots - THE THREE UTILITIES (Travis 10-14: "all bots should have 3 utility items btw, the main armor, the generator
// armor toggled on (or off to regen energy) and the energy choice (SSC, or any of the cells, depending on their build preference of
// faster energy regen or higher energy pool)").
//
// The item slots (ctfpl.itm maxSlottedCategoryCode): 10 = armour (Carapace, CMP6, Drop, CMP4, Kevlite ... and the cloaks, so a
// cloaked bot wears no armour), 20 = generator (PF -100 recharge, IDF -100, EMP -50), 30 = energy (SSC +100 recharge, Light / Medium /
// Heavy PowerCell +120 / +190 / +250 capacity). Each item must be legal for the class (its skill logic).
// The armour and the energy item are always on. The generator is switched on for a fight (target or a recent hit, energy 30 %+) and
// off to recharge (energy under 20 %, or 3-8 s without a fight; a rookie is slower to think of it).
// Every active utility is an armour layer on the bot's body, in order after the vehicle's own, exactly as the client stacks them
// (cfs-studio damageCalculation.buildArmorStack): per damage type, amount = (amount - ignore) x (1 - percent / 1000).
/* PARSER GUARD: keep a block comment after the last line comment */

using System;
using System.Collections.Generic;

using InfServer.Game;

using Assets;

namespace InfServer.Script.GameType_CTF
{
    internal sealed partial class CTFXBot
    {
        internal const int Carapace = 62, Cmp6 = 7, Cmp4 = 20, Kevlite = 6, DropArmor = 32, Cmp6Jump = 83, ShockArmor = 18, Ceramax = 3;
        internal const int PfGen = 25, IdfGen = 45, EmpGen = 16, Ssc = 14, CellL = 40, CellM = 41, CellH = 42;
        ItemInfo.UtilityItem _armorU, _genU, _cellU;
        bool _genOn;
        int _genCheckAt, _lastFightAt;

        static ItemInfo.UtilityItem Util(int id) { return AssetManager.Manager.getItemByID(id) as ItemInfo.UtilityItem; }

        ItemInfo.UtilityItem FirstLegal(int[] ids, double skip)
        {
            ItemInfo.UtilityItem first = null;
            foreach (int id in ids)
            {
                if (!CTFXSkillLogic.Allowed(id, _class)) continue;
                ItemInfo.UtilityItem u = Util(id);
                if (u == null) continue;
                if (first == null) { first = u; if (_rng.NextDouble() >= skip) return u; continue; }
                return u;                                                                // the second choice (skip chance)
            }
            return first;
        }

        /// <summary>The first armour whose skill logic names this class's number (a bare N, not %N); skip = chance of the second.</summary>
        ItemInfo.UtilityItem FirstNamed(int[] ids, double skip)
        {
            List<int> ok = new List<int>();
            foreach (int id in ids)
            {
                ItemInfo.UtilityItem u = Util(id);
                if (u == null || !CTFXSkillLogic.Allowed(id, _class)) continue;
                if (System.Text.RegularExpressions.Regex.IsMatch(u.skillLogic ?? "", @"(?<![%@#\-\d])" + _class + @"(?!\d)")) ok.Add(id);
            }
            if (ok.Count == 0) return null;
            return Util(ok.Count > 1 && _rng.NextDouble() < skip ? ok[1] : ok[0]);
        }

        /// <summary>A new body: its armour, generator and energy item (UtilSpawn runs after the cloak / TD are put on).</summary>
        void UtilSpawn(int now)
        {
            _armorU = null; _genU = null; _cellU = null; _genOn = false;
            _genCheckAt = now + 500; _lastFightAt = 0;
            _genFightRolled = false; _genForgot = false; _genKeepUntil = 0;
            if (_activeEquip == null || !Mgr.Utilities) return;
            CTFXShop.Build fromB = Mgr.Mini != null ? CTFXShop.Of(_kitId) : null;
            if (fromB != null && (fromB.ArmorId != 0 || fromB.GenId != 0 || fromB.CellId != 0))
            {   // MiniTP (Travis 11-08: "armour / generator / cell from the build"): a seat wears what its own ?buy string buys - not a roll by class. The only flaw left is the tier one: a life
                // that never bought the generator (GenOwn), and the cloak (if the build has one) takes the armour slot
                if (fromB.ArmorId != 0 && _cloak == null) _armorU = Util(fromB.ArmorId);
                if (fromB.GenId != 0 && Mgr.Mini.Owns(Mgr.Mini.GenOwn(Human != null ? Human.Tier : null))) _genU = Util(fromB.GenId);
                if (fromB.CellId != 0) _cellU = Util(fromB.CellId);
                if (_armorU != null && !_activeEquip.Contains(_armorU)) _activeEquip.Add(_armorU);
                if (_cellU != null && !_activeEquip.Contains(_cellU)) _activeEquip.Add(_cellU);
                if (_armorU != null) Mgr.UtilArmour++;
                if (_genU != null) Mgr.UtilGen++;
                if (_cellU != null) { if (_cellU.id == Ssc) Mgr.UtilSsc++; else Mgr.UtilCell++; }
                Mgr.Mini.NoteBuildUtils(this, _armorU, _genU, _cellU);
                return;
            }
            // Travis 10-14 eve: "Heavy weapons doesn't qualify for those armors, they wear Kevlite Combat Armor". The item logic's %3 (any
            // suit) lets every class through, so armour goes by the class numbers each armour names: Carapace 3 / 11 / 12, CMP6 3 / 7 / 11 /
            // 12, Kevlite 3 / 5 / 6 / 7 / 11 / 12, Shock 6, Drop 2; the Heavy wears Kevlite
            int[] armour = _class == SK_HEAVY ? new int[] { Kevlite }
                : _class == SK_JT ? new int[] { DropArmor, Cmp6Jump }
                : new int[] { Cmp6, Kevlite, Carapace, ShockArmor };
            if (_cloak == null) _armorU = FirstNamed(armour, 0.30);                     // a cloak takes the armour slot
            double g = _rng.NextDouble();
            _genU = FirstLegal(g < 0.70 ? new int[] { PfGen, IdfGen, EmpGen } : g < 0.90 ? new int[] { IdfGen, PfGen } : new int[] { EmpGen, PfGen }, 0.0);
            // the energy choice is the seat's build preference: regen (SSC) or pool (a cell); heavies and snipers lean to the pool
            int seed = Human != null ? Human.Seed : _rng.Next(1000);
            double poolLean = (_class == SK_HEAVY || Style == CTFXStyle.Sniper || Style == CTFXStyle.Post) ? 0.6 : 0.35;
            bool pool = (seed % 100) / 100.0 < poolLean;
            _cellU = FirstLegal(pool ? new int[] { (seed & 1) == 0 ? CellH : CellM, CellH, Ssc } : new int[] { Ssc, CellH }, 0.0);
            // the brown front line (Travis 10-14): a brown team's front fighters wear the tank set: Carapace (the Heavy: Kevlite),
            // PF Generator, Suit SuperCharger
            bool front = Group != null && Group.Quad == "brown" && (_class == SK_HEAVY || ((_class == SK_INF || _class == SK_SGT || _class == SK_CAPT)
                && (Style == CTFXStyle.Guard || Style == CTFXStyle.Assault || Style == CTFXStyle.Post)));
            if (front)
            {
                ItemInfo.UtilityItem c = FirstNamed(_class == SK_HEAVY ? new int[] { Kevlite } : new int[] { Carapace, Cmp6 }, 0.0),
                    p = CTFXSkillLogic.Allowed(PfGen, _class) ? Util(PfGen) : null, s = CTFXSkillLogic.Allowed(Ssc, _class) ? Util(Ssc) : null;
                if (c != null && _cloak == null) _armorU = c;
                if (p != null) _genU = p;
                if (s != null) _cellU = s;
                Mgr.FrontLiners++;
            }
            if (Mgr.Mini != null && _genU != null && !Mgr.Mini.Owns(Mgr.Mini.GenOwn(Human != null ? Human.Tier : null))) _genU = null;      // MiniTP: some lives never bought the generator
            if (_armorU != null && !_activeEquip.Contains(_armorU)) _activeEquip.Add(_armorU);
            if (_cellU != null && !_activeEquip.Contains(_cellU)) _activeEquip.Add(_cellU);
            if (_armorU != null) Mgr.UtilArmour++;
            if (_genU != null) Mgr.UtilGen++;
            if (_cellU != null) { if (_cellU.id == Ssc) Mgr.UtilSsc++; else Mgr.UtilCell++; }
        }

        /// <summary>The generator switch (every 250 ms from the heal tick).</summary>
        void UtilStep(int now)
        {
            if (_genU == null || _activeEquip == null) return;
            if (IsDead || InDropship) { if (_genOn) GenSet(false); return; }
            bool fighting = HasTarget || (_hitAt != 0 && now - _hitAt < 3000);
            if (fighting) _lastFightAt = now;
            double en = _energyMax > 0 ? _energy / _energyMax : 1.0;
            if (_genOn && en < 0.20) { GenSet(false); Mgr.GenOffLow++; _genCheckAt = now + 1500; return; }
            if (now - _genCheckAt < 0) return;
            _genCheckAt = now + 400 + _rng.Next(400);
            if (!_genOn && fighting && en >= 0.30)
            {   // a rookie is slower to think of it
                if (Mgr.Mini != null && !_genFightRolled) { _genFightRolled = true; _genForgot = _rng.NextDouble() < Mgr.Mini.ForgetOn(Human != null ? Human.Tier : null); }   // MiniTP: it forgets it altogether this fight
                if (!_genForgot && _rng.NextDouble() < TierPick(0.45, 0.95)) { GenSet(true); Mgr.GenOn++; }
            }
            else if (_genOn && !fighting && now - _lastFightAt > (int)(3000 + (1.0 - TierPick(0.0, 1.0)) * 5000))
            {
                if (Mgr.Mini != null)
                {   // MiniTP: sometimes it forgets to switch the generator off, and pays in energy regen until the under-20 % rule cuts it
                    if (_genKeepUntil == 0) _genKeepUntil = _rng.NextDouble() < Mgr.Mini.ForgetOff(Human != null ? Human.Tier : null) ? now + 10000 + _rng.Next(25000) : 1;
                    if (_genKeepUntil != 1 && now - _genKeepUntil < 0) return;
                }
                _genKeepUntil = 0; GenSet(false); Mgr.GenOffRegen++;
            }
            if (!fighting) _genFightRolled = false;
        }

        void GenSet(bool on)
        {
            _genOn = on;
            if (on && !_activeEquip.Contains(_genU)) _activeEquip.Add(_genU);
            else if (!on) _activeEquip.Remove(_genU);
        }

        /// <summary>One damage type through every active utility's layer, in the order worn (thousandths in and out).</summary>
        double UtilArmour(double v, int type)
        {
            if (_activeEquip == null || v <= 0) return v;
            foreach (ItemInfo.UtilityItem u in _activeEquip)
            {
                if (u == null) continue;
                int ig, pc;
                switch (type)
                {
                    case 0: ig = u.kineticIgnore; pc = u.kineticPercent; break;
                    case 1: ig = u.explosiveIgnore; pc = u.explosivePercent; break;
                    case 2: ig = u.electronicIgnore; pc = u.electronicPercent; break;
                    case 3: ig = u.psionicIgnore; pc = u.psionicPercent; break;
                    case 4: ig = u.bypassIgnore; pc = u.bypassPercent; break;
                    default: ig = u.energyIgnore; pc = u.energyPercent; break;
                }
                if (ig == 0 && pc == 0) continue;
                v = (v - ig) * (1.0 - pc / 1000.0);
                if (v <= 0) return 0.0;
            }
            return v;
        }

        internal string UtilName()
        {
            return (_armorU != null ? _armorU.name.Trim('"', ' ') : "-") + " / " + (_genU != null ? _genU.name.Trim('"', ' ') + (_genOn ? " ON" : " off") : "-")
                + " / " + (_cellU != null ? _cellU.name.Trim('"', ' ') : "-");
        }
    }

    internal sealed partial class CTFXBotManager
    {
        internal int FrontLiners, UtilArmour, UtilGen, UtilSsc, UtilCell, GenOn, GenOffLow, GenOffRegen;

        internal string UtilText()
        {
            return " | utilities: armour " + UtilArmour + ", generator " + UtilGen + " (on " + GenOn + ", off at low energy " + GenOffLow + ", off to recharge " + GenOffRegen
                + "), SSC " + UtilSsc + " / cells " + UtilCell + ", brown front-line tank sets " + FrontLiners;
        }
    }
}
/* PARSER GUARD - keep last */
