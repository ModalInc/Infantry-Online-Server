// CTF:X server-side bots - WEAPON COMBOS (Travis 10-14: "bots should be more sensitive to understanding when they've drained someone
// (like firing Beamer's), they only need to land a few shots before they switch to another item like stunner tranq or a damaging
// weapon to finish the person off. shotgun several times > rifle grenade is also another very common combo. Also if teammates are
// draining for each other, someone can be more designated to be a finisher so they should play off each other better").
//
// SelectWeapon adds ComboBonus to each kit item's score:
//   - a beamer while the target still has energy (50 %+) opens the fight; once the target is under 30 % energy the beamer is done
//     (the drained target's shield is gone: every hit lands full) and a damaging or stunning item finishes
//   - a teammate already beaming the same target: this one is the finisher (no beamer of its own, damage first)
//   - 2-4 shotgun shots on one target, then the rifle grenade (when the target stands where it lands)
// ComboShot runs after every shot and asks for an immediate re-pick when a combo step is due (past the switch locks).
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
        int _comboTid = -1, _sgShots, _sgWant, _beamShots;
        bool _comboPick;

        static bool IsBeamer(int id) { return id >= 1163 && id <= 1166; }
        static bool IsStunItem(int id) { return id == 1113 || id == 1195 || id == 1185 || id == 1073 || id == 1064 || id == 1012; }
        static bool IsRifleGrenade(int id) { return id == 1106 || id == 1107; }

        static readonly Dictionary<int, bool> _rgAny = new Dictionary<int, bool>();
        /// <summary>Any rifle grenade: the two combo ids, the Kuchler 1082 and every item named "... RG ..." (champion / skin variants).</summary>
        static bool IsAnyRg(int id)
        {
            lock (_rgAny)
            {
                bool v;
                if (_rgAny.TryGetValue(id, out v)) return v;
                ItemInfo it = AssetManager.Manager.getItemByID(id);
                string n = it != null && it.name != null ? it.name : "";
                v = id == 1106 || id == 1107 || id == 1082 || n.IndexOf(" RG ", StringComparison.Ordinal) >= 0 || n.EndsWith(" RG", StringComparison.Ordinal) || n.StartsWith("RG ", StringComparison.Ordinal);
                _rgAny[id] = v;
                return v;
            }
        }

        /// <summary>Travis 11-08: "they are sometimes just shooting rifle grenade which will do no damage unless the target is drained, so the bot should understand to wait
        /// until the target is drained, either from their own source of draining weapon or a teammate's". A rifle grenade at a man (bot or player) whose energy shield is still
        /// up (30 % or more) is held back, whoever drains him (own beamer, a mate's, anything) opens it. Turrets have no shield: unaffected.</summary>
        bool RgWaits(int id)
        {
            if (!IsAnyRg(id) || _tgtC != null || (_tgtP == null && _tgtB == null)) return false;
            return TargetEnergyFrac() >= 0.30;
        }

        int ComboTargetId() { return _tgtP != null ? (int)_tgtP._id : _tgtB != null ? 100000 + (int)_tgtB._id : -1; }

        /// <summary>The target's energy, 0..1; -1 = unknown (a turret, no target).</summary>
        double TargetEnergyFrac()
        {
            if (_tgtB != null) return _tgtB.EnergyMax > 0 ? _tgtB.Energy / _tgtB.EnergyMax : -1;
            if (_tgtP != null && _tgtP._state != null)
            {
                double max = _tgtP._baseVehicle != null && _tgtP._baseVehicle._type != null && _tgtP._baseVehicle._type.EnergyMax > 0 ? _tgtP._baseVehicle._type.EnergyMax : 400;
                return Math.Max(0.0, Math.Min(1.0, _tgtP._state.energy / max));
            }
            return -1;
        }

        /// <summary>A teammate bot already beaming this same target (then this one finishes).</summary>
        bool MateDraining()
        {
            if (_tgtP == null && _tgtB == null) return false;
            foreach (CTFXBot o in Mgr.LiveBots)
            {
                if (o == this || o.IsDead || o._team != _team || o._kit == null || o._curWep < 0 || o._curWep >= o._kit.Length) continue;
                if (o._tgtP != _tgtP || o._tgtB != _tgtB) continue;
                if (IsBeamer(o._kit[o._curWep].Id)) return true;
            }
            return false;
        }

        // the Zap Glove rotation (Travis 10-14 night): Zap Glove > PowerFist > Zap Glove > (no knife / axe in ctfpl.itm) > a GR to finish
        // the item data (ctfpl.itm, 10-14 night): Zap (stun: no thrust 3.5 s, other items locked 1 s) > PowerFist (200 energy) > PB g111 x3
        // (180 energy each: the shield gone, full hits) > Zap again as the stun runs out > PB / point-blank EMP / GR790 to finish
        static readonly int[] ZapRotation = new int[] { 1195, 1185, 1196, 1196, 1196, 1195 };
        int _zapSeq;
        internal void ZapSeqStart() { _zapSeq = 0; _comboPick = true; }
        internal int ZapStep { get { return _zapSeq; } }
        internal string FireBlock = "-";

        /// <summary>Cycle 8: a zapper lying in wait has the glove out and its eye on the victim already, so the spring is at once: no
        /// reaction delay, no settle (cycle 7: every strike ended 160-800 px behind a victim that walked on during the reaction).</summary>
        internal void StrikeNow(int now, Player p, CTFXBot o)
        {
            _pendP = null; _pendB = null; _pendC = null; _pendAt = 0;
            Commit(now, p, o, null);
            _firstShotAt = 0;
            _aimBias = 0;
            _nextScan = now + 2000;
            ZapReady(now);
        }

        /// <summary>
        /// "TD and out" (Travis 10-14 night): "kill the sources of teleport disrupt (sentries, medics, captains, sergeants, sciops, or any
        /// class that is actively TD'ing a person holding a flag)". Is this enemy an anti-warp source within its reach of a teammate
        /// carrying a flag? (Turret sentries are already the turret-fight's job.)
        /// </summary>
        internal bool TdOnOurCarrier(Player p, CTFXBot b)
        {
            int tx, ty, reach;
            if (b != null) { if (!b.HasTd || b.IsDead) return false; tx = b._state.positionX; ty = b._state.positionY; reach = 512; }
            else if (p != null && p.activeUtilities != null)
            {
                reach = 0;
                foreach (ItemInfo.UtilityItem u in p.activeUtilities) if (u != null && u.antiWarpDistance > reach) reach = u.antiWarpDistance;
                if (reach == 0) return false;
                tx = p._state.positionX; ty = p._state.positionY;
            }
            else return false;
            foreach (CTFXBot o in Mgr.LiveBots)
            {
                if (o._team != _team || o.IsDead || !Mgr.Flags.Carrying(o)) continue;
                double dx = o._state.positionX - tx, dy = o._state.positionY - ty;
                if (dx * dx + dy * dy <= (double)reach * reach) return true;
            }
            return false;
        }

        // ---- the elite Heavy's Demo Pack (Travis 10-14 night): "an elite heavy weapons will use demo pack in retreat path tight chokes in a
        // way that it's hard for the enemy to space by, or as a 'get off me' maneuver if someone is trying to close the gap with shotgun and
        // the heavy needs to gain space and distance and energy using energizer + that". A Heavy backing off with an enemy within 260 px,
        // or hurt with one closing inside 130 px: it drops the pack at its feet (5 s fuse), pops an energizer if it has one, and breaks
        // away from the enemy. Skill decides how often it thinks of it (rookie 15 %, ace 80 %); one pack per 25 s.
        internal const int DemoPack = 1062;
        int _demoNext;

        void DemoStep(int now, int px, int py)
        {
            if (_class != SK_HEAVY || now - _demoNext < 0 || IsDead || InDropship || KitIndex(DemoPack) < 0) return;
            Helpers.ObjectState ts = _tgtP != null ? _tgtP._state : _tgtB != null ? _tgtB._state : null;
            if (ts == null) return;
            double dx = ts.positionX - px, dy = ts.positionY - py, d = Math.Sqrt(dx * dx + dy * dy);
            bool retreat = _retreating && d < 260.0;
            bool closer = d < 130.0 && _state.health < 0.6 * _type.Hitpoints;
            if (!retreat && !closer) return;
            _demoNext = now + 3000;
            if (_rng.NextDouble() >= TierPick(0.15, 0.80)) return;
            if (!LayMine(now, DemoPack, YawTo(dx, dy))) return;
            _demoNext = now + 25000;
            Mgr.DemoPacks++;
            if (_energizers > 0)
            {
                ItemInfo.RepairItem ez = Repair(15);
                if (ez != null) { _energy = Math.Min(_energyMax, _energy + ez.repairAmount); _energizers--; Mgr.Energizes++; }
            }
            BreakAway(now, ts.positionX, ts.positionY, 420);
        }

        /// <summary>Right before a storm a player tops up: an energizer when energy is under 90 % (Travis 10-14: "energizers mainly").</summary>
        internal void PreStorm(int now)
        {
            if (_energizers <= 0 || _energyMax <= 0 || _energy >= 0.9 * _energyMax) return;
            ItemInfo.RepairItem ez = Repair(15);
            if (ez == null) return;
            _energy = Math.Min(_energyMax, _energy + ez.repairAmount); _energizers--; Mgr.Energizes++; Mgr.PreStormEnergizers++;
        }

        /// <summary>
        /// Weapons against turrets (10-15, from the item data: turretdps/REPORT.md): the MML (one volley kills an MG / sentry / plasma
        /// turret) and the AC (37.7 DPS, no energy) are best; frag / WP next (grenades already +45); Haywire switches a turret's fire off
        /// for 4 s, so it goes first; bullet guns do NOTHING to the MG and rocket turrets (kinetic under their armour ignore).
        /// </summary>
        // Guns that do (almost) nothing to a turret under its armour (ctfpl.itm turret DPS table): bullets, shotguns, beamers, melee, gas.
        // Haywire (silences a turret) and grenades / launchers / mines are NOT here.
        static readonly HashSet<int> NoTurretDamage = new HashSet<int> {
            3000, 1005, 3021, 1067, 1096, 3057, 3038, 1003, 1007, 1097, 1024, 3025, 1057, 3028, 1044, 1104, 3016, 1203, 1204, 1081, 1038,
            1177, 1113, 1205, 1225, 3010, 1065, 1118, 3023, 1016, 3001, 1111, 1163, 1164, 1165, 1166, 1195, 1196, 1198, 1202, 3050, 1056 };
        static bool TurretUseless(int id) { return NoTurretDamage.Contains(id); }

        /// <summary>Travis 10-17: "a lot of bots try to shoot turrets with weapons that don't damage them." A bot only engages a turret when its kit holds a gun that hurts it.</summary>
        bool CanHurtTurret()
        {
            if (_kit == null) return false;
            for (int i = 0; i < _kit.Length; i++)
            {
                if ((_badMask & (1 << i)) != 0 || Dry(i) || TurretUseless(_kit[i].Id) || IsDefTool(_kit[i].Id)) continue;
                int id = _kit[i].Id;
                if (IsMine(id)) continue;
                if (EnergyCost(id) > _energy) continue;
                return true;
            }
            return false;
        }

        static double TurretBias(int id)
        {
            switch (id)
            {
                case 3009: return 70.0;                                   // MML
                case 3031: return 60.0;                                   // AC
                case 1064: case 1193: return 90.0;                        // Haywire: their fire off first
                case 1096: case 3038: case 3028: case 1044: case 1109: case 3023: case 3001: case 1004: case 1056: case 1031: return -80.0;   // bullets
            }
            return 0.0;
        }

        /// <summary>No other enemy within 200 px of the target (a single man to pick off).</summary>
        bool TargetHasCompany()
        {
            Helpers.ObjectState ts = _tgtP != null ? _tgtP._state : _tgtB != null ? _tgtB._state : null;
            if (ts == null) return true;
            foreach (CTFXBot o in Mgr.LiveBots)
            {
                if (o.IsDead || o._team == _team || o == _tgtB) continue;
                double dx = o._state.positionX - ts.positionX, dy = o._state.positionY - ts.positionY;
                if (dx * dx + dy * dy < 200.0 * 200.0) return true;
            }
            return false;
        }

        /// <summary>The Zap Glove in hand (while it waits on the victim's path).</summary>
        internal void ZapReady(int now)
        {
            int i = KitIndex(1195);
            if (i < 0 || i == _curWep) return;
            if (EquipIdx(i)) { _curWep = i; _fmPhase = FM_FREE; _lastWepSwitch = now; }
        }
        internal int WeaponId { get { return _kit != null && _curWep >= 0 && _curWep < _kit.Length ? _kit[_curWep].Id : 0; } }
        static bool IsGr(int id) { return id == 1031 || id == 1114 || id == 1196 || id == 1012; }   // the finishers

        /// <summary>Cycle 7: the Zap Glove and PowerFist only swing within their reach (the kit's Hi + 10 px); cycle 6 logged strikes that
        /// "finished" the rotation 100-700 px from a victim at 95-100 % health.</summary>
        bool MeleeReach(int i, double dist)
        {
            int id = _kit[i].Id;
            if (id != 1195 && id != 1185 && id != 1196) return true;
            return dist <= _kit[i].Hi + 30;                                   // the glove's shot flies ~25 px + 35 px proximity
        }

        double ComboBonus(int i, bool mateDrains, double ef)
        {
            int id = _kit[i].Id;
            double s = 0;
            if (Mgr.Bounty != null && Mgr.Bounty.Striking(this))
            {
                if (_zapSeq < ZapRotation.Length) s += id == ZapRotation[_zapSeq] ? 400.0 : IsGr(id) ? -200.0 : 0.0;
                else s += IsGr(id) ? 400.0 : (id == 1195 || id == 1185) ? -300.0 : 0.0;   // the melee did not do it: the GR finishes
                return s;
            }
            s += DrainRuleBonus(id);                                                 // 10-17: energy-only weapons, 3 shots then the HP weapon (CTFXDrainRule.cs)
            if (ef >= 0)
            {
                if (IsBeamer(id))
                {
                    if (ef < 0.30) s -= 250.0;                                           // drained: the beamer's job is done
                    else if (mateDrains && Style != CTFXStyle.Drain) s -= 120.0;        // a mate drains it: this one finishes
                    else if (ef >= 0.50) s += 35.0;                                      // open with the drain
                }
                else if (ef < 0.30 || mateDrains)
                    s += IsStunItem(id) ? (id == 1113 ? 10.0 : 25.0) : 40.0;           // finish it: damage first, a stun to hold it
            }
            // the RPG (Travis 10-14 night): "preferred against drained single target man vehicles": fast, knockback, a one-shot when the
            // target's energy (its shield) is low and the shrapnel lands
            if (id == 1083 && _tgtC == null && ef >= 0 && ef < 0.35 && !TargetHasCompany()) s += 70.0;
            if (IsGrenade(id) && Mgr.Flags != null && Mgr.Flags.ChasingEnemy(this) && _wdist <= 320.0) s += 45.0;   // grenade hell in the flag room
            // Travis 10-17: infantry should mostly use the SHOTGUN (it kills nearly every class, Carapace aside); the assault rifle is the
            // finisher, and the rifle grenade follows a shotgun drain. Inside 330 px the shotgun leads, the rifle only finishes a drained or hurt target.
            if (_tgtC == null && (_tgtP != null || _tgtB != null) && _wdist <= 330.0 && HasShotgun())
            {
                bool low = ef >= 0 && ef < 0.30;
                if (_kit[i].Shotgun) s += 55.0 - Math.Max(0.0, _wdist - 160.0) * 0.12;
                else if (IsAssaultRifle(id)) s += low ? 25.0 : -45.0;
                else if (IsRifleGrenade(id) && low) s += 60.0;
            }
            if (_sgWant > 0 && _sgShots >= _sgWant)
            {
                if (IsRifleGrenade(id))
                {
                    int land = CTFXShots.LobLand(id);
                    if (land == 0 || Math.Abs(_wdist - land) <= 150.0) s += 90.0;         // it stands where the grenade lands
                }
                else if (_kit[i].Shotgun) s -= 40.0;
            }
            return s;
        }

        /// <summary>After each shot: count the combo steps, and ask for a re-pick when one is due.</summary>
        void ComboShot(int now, int i)
        {
            int tid = ComboTargetId();
            if (tid != _comboTid) { _comboTid = tid; _sgShots = 0; _beamShots = 0; _sgWant = 2 + _rng.Next(3); _drainStreak = 0; }
            int id = _kit[i].Id;
            DrainRuleShot(now, id);
            if (Mgr.Bounty != null && Mgr.Bounty.Striking(this) && _zapSeq < ZapRotation.Length && id == ZapRotation[_zapSeq])
            { _zapSeq++; _comboPick = true; Mgr.ZapSteps++; }
            if (_kit[i].Shotgun)
            {
                _sgShots++;
                if (_sgShots == _sgWant && HasRifleGrenade()) { _comboPick = true; Mgr.CombosShotgun++; }
            }
            else if (IsRifleGrenade(id)) { _sgShots = 0; _sgWant = 2 + _rng.Next(3); }
            if (IsBeamer(id))
            {
                _beamShots++;
                double ef = TargetEnergyFrac();
                if (ef >= 0 && ef < 0.30) { _comboPick = true; Mgr.CombosDrained++; }
            }
        }

        static bool IsAssaultRifle(int id) { return id == 1096 || id == 3038 || id == 1003 || id == 1097 || id == 1024 || id == 3025 || id == 1004 || id == 3057; }

        bool HasShotgun()
        {
            for (int k = 0; k < _kit.Length; k++) if (_kit[k].Shotgun && !Dry(k) && (_badMask & (1 << k)) == 0 && EnergyCost(_kit[k].Id) <= _energy) return true;
            return false;
        }

        bool HasRifleGrenade()
        {
            for (int k = 0; k < _kit.Length; k++) if (IsRifleGrenade(_kit[k].Id) && !Dry(k) && (_badMask & (1 << k)) == 0) return true;
            return false;
        }
    }

    internal sealed partial class CTFXBotManager
    {
        internal int EliteRecalls;
        internal int TdAndOut, DemoPacks, PreStormEnergizers, AfkSpells, EsSweeps, GapSwitches;

        /// <summary>A respawning roamer / guard / assault seat of a base team switches to Engineer when its team has no living engineer
        /// and its base is missing turrets (a player fills what the team lacks); 1 in 3 such respawns.</summary>
        internal bool FillsEngineerGap(CTFXSlot slot)
        {
            if (slot == null || slot.Group == null || slot.Group.HomeEnd == null || slot.Group.Team == null) return false;
            if (slot.Style != CTFXStyle.Roam && slot.Style != CTFXStyle.Guard && slot.Style != CTFXStyle.Assault) return false;
            foreach (CTFXBot o in LiveBots) if (!o.IsDead && o._team == slot.Group.Team && o.ClassId == CTFXBot.SK_ENGY) return false;
            bool missing = false;
            foreach (CTFXTurretSpot t in Turrets.Spots) if (t.End == slot.Group.HomeEnd && t.TurretId == 0) { missing = true; break; }
            if (!missing || Rng.NextDouble() > 0.33) return false;
            GapSwitches++;
            return true;
        }
        internal int CombosDrained, CombosShotgun, Finishers, ZapSteps, MeleeReacts, MeleeReactTries;

        internal string CombosText()
        {
            return " | combos: drained then switched " + CombosDrained + ", shotgun > rifle grenade " + CombosShotgun + ", finisher picks (a mate drained) " + Finishers + ", TD-and-out target picks " + TdAndOut + ", demo packs dropped " + DemoPacks + ", energizers before a storm " + PreStormEnergizers + ", wall bounces " + Shots.WallBounceCount + ", AFK spells " + AfkSpells + ", ES sweeps at home " + EsSweeps + ", class switches to fill an engineer gap " + GapSwitches + ", big-bounty recalls " + EliteRecalls + ", TD corner spots " + (Cmdr != null ? Cmdr.TdCorners : 0) + ", calm anchors at the outer choke " + (Cmdr != null ? Cmdr.CalmAnchors : 0) + (Cmdr != null ? Cmdr.SweepText() : "") + ", staggered clips " + StaggerClips + " (breaks " + StaggerBreaks + ")" + ", attack positions taken " + Staged + " (crowded skipped " + StageCrowded + "), hall pass-through scans " + HallPassThrough + BrawlText() + ", human hits on bots " + HumanHits + " (closer in the last 400 ms than now " + HumanHitsLagged + ")" + ", captain anchors " + (Cmdr != null ? Cmdr.CaptainAnchors : 0) + ", zap rotation steps " + ZapSteps + ", victims fighting back (energizer / repulsor) " + MeleeReacts + " of " + MeleeReactTries;
        }
    }
}
/* PARSER GUARD - keep last */
