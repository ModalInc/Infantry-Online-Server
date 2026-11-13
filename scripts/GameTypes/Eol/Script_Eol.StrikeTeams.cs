using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// Human-class roaming squads (local change 2026-11-14), built on the real PLAYER classes so they wear the same
    /// skins as human players. Part of the 80-bot roster (see Script_Eol.Roster.cs):
    ///   - "Storm Rangers Bots": human roamers, 5-8, normal skill mix;
    ///   - five mixed teams of 3-8 that make up the rest of the 80, each with its own skill (ace .. normal mix);
    ///   - "Ghost Reapers Bots" (botTeam8): an elite vehicle squad of 3 - Deathboard, Shimmerboard, Drop Pack.
    /// Every class is kept on the field (each new bot takes the class with the fewest bots out) and every class
    /// draws from its own weapon pools, so loadouts vary. Every bot carries the Suit SuperCharger; about a third
    /// carry grenades; some riflemen open with a clip of the Kamenev AP mk2. They use the roaming-squad brain
    /// (leader hunts / investigates, the rest move in formation, everyone fights what they see).
    /// </summary>
    public partial class Script_Eol
    {
        public const bool StrikeTeamsEnabled = true;
        const int StrikeRespawnDelayMs = 20000;

        // player classes (Pioneer Station .veh)
        const int ClsJumpTrooper = 10, ClsEngineer = 11, ClsInfantry = 12, ClsHeavy = 13, ClsInfiltrator = 14, ClsMedic = 15,
                  ClsBioChem = 100, ClsPartisan = 104, ClsMPAC = 110, ClsConscript = 111, ClsNeuroTAC = 112, ClsCombatTech = 113,
                  ClsSciOps = 115, ClsDropTrooper = 117, ClsSquadLeader = 123, ClsSergeant = 133, ClsCaptain = 134,
                  ClsChiefEngineer = 135, ClsMarine = 136, ClsFieldSurgeon = 137, ClsLieutenant = 138, ClsBlackOps = 150, ClsColonel = 151;
        static readonly int[] StrikeMemberClasses =
        {
            ClsInfantry, ClsHeavy, ClsJumpTrooper, ClsInfiltrator, ClsMedic, ClsEngineer, ClsBioChem, ClsPartisan, ClsMPAC,
            ClsConscript, ClsNeuroTAC, ClsCombatTech, ClsSciOps, ClsDropTrooper, ClsMarine, ClsFieldSurgeon, ClsChiefEngineer, ClsBlackOps,
        };
        static readonly int[] StrikeOfficerClasses = { ClsSquadLeader, ClsSergeant, ClsLieutenant, ClsCaptain, ClsColonel };
        /// <summary>Leader chassis for any roaming squad (pirate commander + human officers).</summary>
        static readonly HashSet<int> SquadLeaderVehIds = new HashSet<int> { EliteCommanderVehId, ClsSquadLeader, ClsSergeant, ClsCaptain, ClsLieutenant, ClsColonel };

        // elite vehicle squad (personal vehicles)
        const int VehDeathboard = 108, VehShimmerboard = 131, VehDropPack = 109;
        static readonly int[] VehicleSquadVehIds = { VehDeathboard, VehShimmerboard, VehDropPack };
        const int ItmDeathboardSlash = 1047, ItmVehicleShimmer = 174, ItmDropPackBolt = 3008;   // "DropPack Blaster" - its own twin bolts

        // items (Pioneer Station .itm)
        const int ItmSuperCharger = 14;
        const int ItmKamenevAPmk2 = 3003;
        const int ItmTitanAR = 1097, ItmA2GL = 3075;
        static readonly int[] Grenades = { 1011, 1011, 1435, 1012 };   // frag (most common), incendiary, EMP

        // weapon pools - each class mixes from these so no two squads look the same
        static readonly int[] PoolRifle = { 1097, 1096, 1003, 3094, 3025, 1024, 3103, 3038, 3016 };
        static readonly int[] PoolSmg = { 1001, 1109, 1305, 1002, 3104, 3051 };
        // (local 2026-11-14: no charge-up guns that must be fired standing still - MG94k, ACp-90, GC 10a, MC1,
        // IRL909 - bots never stop to shoot; no items without a usable projectile - Titan CCS, Havzeks Stroker)
        static readonly int[] PoolShotgun = { 3023, 1016, 3001, 1465 };
        static readonly int[] PoolCarbine = { 1002, 3104, 1001, 3094 };
        static readonly int[] PoolHeavy = { 1057, 3028, 3021, 1310, 3105, 3110, 1004 };
        static readonly int[] PoolPlasma = { 1044, 1104, 1007, 1188, 1311, 1006 };
        static readonly int[] PoolPlasmaSmg = { 1100, 109, 1006, 1343 };
        static readonly int[] PoolGauss = { 1031, 1038, 1141, 1114, 1533 };
        static readonly int[] PoolLaser = { 1103, 1157, 1607 };
        static readonly int[] PoolProjector = { 1146, 1126, 1000 };

        public Team botTeamStrike1;
        readonly List<Team> _mixedTeams = new List<Team>();
        readonly Dictionary<Team, int> _strikeTargetSize = new Dictionary<Team, int>();
        readonly Dictionary<Team, string> _strikeSkill = new Dictionary<Team, string>();
        readonly Dictionary<Team, int> _strikeNextSpawnTick = new Dictionary<Team, int>();
        int _tickStrikePoll;

        static readonly string[] MixedTeamNames = { "Nova Commandos Bots", "Rogue Jackals Bots", "Iron Wolves Bots", "Desert Vipers Bots", "Night Owls Bots" };

        void CreateStrikeTeams()
        {
            botTeamStrike1 = CreateAndRegisterBotTeam("Storm Rangers Bots");
            _mixedTeams.Clear();
            for (int i = 0; i < RosterMixedTeamCount && i < MixedTeamNames.Length; i++)
                _mixedTeams.Add(CreateAndRegisterBotTeam(MixedTeamNames[i]));
        }

        public bool IsStrikeTeam(Team t)
        {
            return t != null && (t == botTeamStrike1 || _mixedTeams.Contains(t));
        }

        /// <summary>The elite vehicle squad (boards / pack).</summary>
        public bool IsVehicleSquadTeam(Team t)
        {
            return t != null && t == botTeam8;
        }

        /// <summary>Pirate roamers, the vehicle squad and the human squads share the roaming-squad brain.</summary>
        public bool IsRoamingSquadTeam(Team t)
        {
            return IsEliteSquadTeam(t) || IsStrikeTeam(t);
        }

        public static bool IsSquadLeaderVehicle(int vehId)
        {
            return SquadLeaderVehIds.Contains(vehId);
        }

        void PollStrikeTeams(int now)
        {
            if (!StrikeTeamsEnabled || _arena == null || !_arena._bGameRunning || !EolHasArenaPresence())
                return;
            if (now - _tickStrikePoll < 1500)
                return;
            _tickStrikePoll = now;
            EnsureRoster();

            _strikeTargetSize[botTeamStrike1] = _rosterHumanRoamers;
            KeepStrikeTeamFilled(botTeamStrike1, now, false);
            if (botTeam8 != null)
            {
                _strikeTargetSize[botTeam8] = RosterVehicleSquadSize;
                _strikeSkill[botTeam8] = "ace";
                KeepStrikeTeamFilled(botTeam8, now, false);
            }

            // mixed teams top the field up to 80 (not past it while the assassins are out)
            int live = CountLiveRosterBots();
            for (int i = 0; i < _mixedTeams.Count; i++)
            {
                Team t = _mixedTeams[i];
                _strikeTargetSize[t] = _rosterMixedSize[i];
                _strikeSkill[t] = _rosterMixedSkill[i];
                if (live >= EolBotTotalTarget)
                    break;
                if (KeepStrikeTeamFilled(t, now, true))
                    live++;
            }
        }

        /// <summary>Spawns at most one bot for this team; true if one was added.</summary>
        bool KeepStrikeTeamFilled(Team team, int now, bool capped)
        {
            if (team == null)
                return false;
            if (roamBots != null && !roamBots.ContainsKey(team))
                roamBots.Add(team, 0);
            int size;
            if (!_strikeTargetSize.TryGetValue(team, out size) || size <= 0)
                return false;

            List<RoamingAttacker> members = EliteSquadMembers(team);
            if (members.Count >= size)
            {
                _strikeNextSpawnTick.Remove(team);
                return false;
            }
            int due;
            if (!_strikeNextSpawnTick.TryGetValue(team, out due))
            {
                due = members.Count == 0 ? now : now + StrikeRespawnDelayMs;
                _strikeNextSpawnTick[team] = due;
            }
            if (now - due < 0)
                return false;

            bool vehicleSquad = IsVehicleSquadTeam(team);
            int cls;
            if (vehicleSquad)
            {
                // one of each: Deathboard, Shimmerboard, Drop Pack
                cls = VehicleSquadVehIds[0];
                foreach (int v in VehicleSquadVehIds)
                    if (!members.Exists(b => b._type != null && b._type.Id == v)) { cls = v; break; }
            }
            else
            {
                bool hasLeader = members.Exists(b => b._type != null && IsSquadLeaderVehicle(b._type.Id));
                cls = hasLeader ? PickLeastFieldedClass(StrikeMemberClasses) : PickLeastFieldedClass(StrikeOfficerClasses);
            }

            Helpers.ObjectState at = null;
            RoamingAttacker leader = EliteSquadLeader(team);
            if (leader != null && !leader.InLiveCombat && InPlayArea(leader._state.positionX, leader._state.positionY) && !PlayerNear(leader._state.positionX, leader._state.positionY, EliteSpawnAwayFromPlayersRaw))
            {
                at = findOpenWarpBot(_arena, leader._state.positionX, leader._state.positionY, 200);
                if (at != null && (!InPlayArea(at.positionX, at.positionY, 120) || !IsOpenConnectedGround(at.positionX, at.positionY)))
                    at = null;
            }
            if (at == null)
                at = PickEliteSpawnPoint();
            if (at == null)
                return false;

            RoamingAttacker bot = _arena.newBot(typeof(RoamingAttacker), (ushort)cls, team, null, at, this, null) as RoamingAttacker;
            if (bot == null)
                return false;
            string skill;
            if (_strikeSkill.TryGetValue(team, out skill) && skill != null)
                bot.SkillModeOverride = skill;
            if (vehicleSquad)
                ApplyVehicleSquadLoadout(bot, cls);
            else
                ApplyHumanLoadout(bot, cls, team);
            if (_bots == null)
                _bots = new List<Bots.Bot>();
            _bots.Add(bot);
            roamBots[team]++;

            if (members.Count + 1 >= size)
                _strikeNextSpawnTick.Remove(team);
            else
                _strikeNextSpawnTick[team] = now + 600;
            return true;
        }

        /// <summary>The class with the fewest bots on the field (ties broken at random) - keeps every class out.</summary>
        int PickLeastFieldedClass(int[] classes)
        {
            Dictionary<int, int> count = new Dictionary<int, int>();
            foreach (int c in classes)
                count[c] = 0;
            foreach (Bots.Bot b in _arena.ActiveBots)
            {
                if (b == null || b.IsDead || b._type == null)
                    continue;
                int n;
                if (count.TryGetValue(b._type.Id, out n))
                    count[b._type.Id] = n + 1;
            }
            int best = classes[_arena._rand.Next(classes.Length)];
            int bestN = int.MaxValue;
            int start = _arena._rand.Next(classes.Length);
            for (int k = 0; k < classes.Length; k++)
            {
                int c = classes[(start + k) % classes.Length];
                if (count[c] < bestN) { bestN = count[c]; best = c; }
            }
            return best;
        }

        int CountTeamSplashGunners(Team team)
        {
            int n = 0;
            foreach (Bots.Bot b in _arena.ActiveBots)
            {
                if (b == null || b.IsDead || b._team != team)
                    continue;
                if (BotWeaponUtil.GetMinFireRangeRaw(b._weaponFarItem) > 0 || BotWeaponUtil.GetMinFireRangeRaw(b._weaponCloseItem) > 0)
                    n++;
            }
            return n;
        }

        ItemInfo Itm(int id) { return _arena._server._assets.getItemByID(id); }

        int Pick(int[] pool) { return pool[_arena._rand.Next(pool.Length)]; }

        /// <summary>Close + far weapons for a class, drawn from that class's pools.</summary>
        void PickClassWeapons(int cls, Team team, out int close, out int far)
        {
            Random r = _arena._rand;
            switch (cls)
            {
                case ClsHeavy:
                    close = Pick(PoolHeavy);
                    far = team != null && CountTeamSplashGunners(team) < MaxGrenadiersPerTeam && r.Next(2) == 0 ? ItmA2GL : Pick(PoolHeavy);
                    break;
                case ClsMPAC:
                    close = Pick(PoolHeavy);
                    far = Pick(PoolHeavy);
                    break;
                case ClsJumpTrooper:
                case ClsDropTrooper:
                    close = Pick(PoolSmg);
                    far = r.Next(2) == 0 ? Pick(PoolPlasma) : Pick(PoolCarbine);
                    break;
                case ClsInfiltrator:
                    // (no sniper rifles - bots fight at close range where they never hit; local 2026-11-14)
                    close = r.Next(2) == 0 ? Pick(PoolSmg) : Pick(PoolPlasmaSmg);
                    far = r.Next(2) == 0 ? Pick(PoolCarbine) : Pick(PoolShotgun);
                    break;
                case ClsMedic:
                case ClsFieldSurgeon:
                    close = Pick(PoolSmg);
                    far = Pick(PoolCarbine);
                    break;
                case ClsEngineer:
                case ClsChiefEngineer:
                case ClsCombatTech:
                case ClsPartisan:
                    close = Pick(PoolShotgun);
                    far = r.Next(2) == 0 ? Pick(PoolCarbine) : Pick(PoolRifle);
                    break;
                case ClsBioChem:
                    close = Pick(PoolProjector);
                    far = Pick(PoolPlasma);
                    break;
                case ClsNeuroTAC:
                    close = Pick(PoolGauss);
                    far = Pick(PoolPlasma);
                    break;
                case ClsSciOps:
                    close = Pick(PoolLaser);
                    far = r.Next(2) == 0 ? Pick(PoolLaser) : Pick(PoolGauss);
                    break;
                case ClsBlackOps:
                    close = r.Next(2) == 0 ? Pick(PoolPlasmaSmg) : Pick(PoolGauss);
                    far = r.Next(2) == 0 ? Pick(PoolPlasma) : Pick(PoolSmg);
                    break;
                case ClsMarine:
                    close = Pick(PoolShotgun);
                    far = r.Next(3) == 0 ? Pick(PoolHeavy) : Pick(PoolRifle);
                    break;
                case ClsSquadLeader:
                case ClsSergeant:
                case ClsLieutenant:
                case ClsCaptain:
                case ClsColonel:
                    close = Pick(PoolCarbine);
                    far = Pick(PoolRifle);
                    break;
                default:   // Infantry, Conscript
                    close = r.Next(3) == 0 ? Pick(PoolShotgun) : Pick(PoolSmg);
                    far = Pick(PoolRifle);
                    break;
            }
        }

        /// <summary>Equip close + far; falls back to the Titan AR if a pool item is missing or not a gun.</summary>
        void EquipCloseFar(Bots.Bot bot, int close, int far)
        {
            WeaponController wClose = bot._weaponClose ?? new WeaponController(bot._state, new WeaponController.WeaponSettings());
            WeaponController wFar = bot._weaponFar ?? new WeaponController(bot._state, new WeaponController.WeaponSettings());
            ItemInfo iClose = Itm(close), iFar = Itm(far), iBackup = Itm(ItmTitanAR);
            if (iClose == null || !wClose.equip(iClose))
                if (iBackup != null) wClose.equip(iBackup);
            if (iFar == null || !wFar.equip(iFar))
                if (iBackup != null) wFar.equip(iBackup);
            BotWeaponUtil.WireLiveCombatWeapons(bot, wClose, wFar, includeFarWeapon: true, seekRangeRaw: EolBotSelfAggroRange);
        }

        void SetUtility(Bots.Bot bot, int itemId)
        {
            if (bot.Loadout == null)
                return;
            int slot = 5;
            for (int s = 0; s < BotLoadout.SlotCount; s++)
                if (bot.Loadout.GetSlotKind(s) == BotLoadoutSlotKind.Empty) { slot = s; break; }
            bot.Loadout.SetSlot(slot, itemId);
            bot.Loadout.SetUtilityActive(slot, true);
        }

        /// <summary>
        /// Human-class bots are as tough as the pirate Roamers (local 2026-11-14 - they died far too fast on bare
        /// player-class armour): ignore the first 6 damage of every hit, then 10 % less, on every damage type.
        /// (The Roamers are also immune to bypass / energy; these are not.)
        /// </summary>
        public const int HumanBotArmorIgnore = 6000, HumanBotArmorPercent = 100, HumanBotBypassEnergyPercent = 500;
        public const short HumanBotHealth = 100;

        /// <summary>
        /// Same as a pirate Roamer: 100 HP, first 6 damage per hit ignored then 10 % less on kinetic / explosive /
        /// electronic / psionic. Roamers are immune to bypass + energy; human bots take half there (still killable).
        /// </summary>
        static void MakeAsToughAsPirates(Bots.Bot bot)
        {
            bot.ExtraArmorIgnore = HumanBotArmorIgnore;
            bot.ExtraArmorPercent = HumanBotArmorPercent;
            bot.ExtraArmorPercentBypassEnergy = HumanBotBypassEnergyPercent;
            if (bot._state != null && bot._state.health > 0 && bot._state.health < HumanBotHealth)
                bot._state.health = HumanBotHealth;   // 75 / 80 HP classes (Black Ops, Infiltrator...) up to 100
        }

        /// <summary>Boards slash (the Shimmerboard runs its shimmer field); the Drop Pack fires its plasma bolts.</summary>
        void ApplyVehicleSquadLoadout(Bots.Bot bot, int veh)
        {
            try
            {
                int gun = veh == VehDropPack ? ItmDropPackBolt : ItmDeathboardSlash;
                EquipCloseFar(bot, gun, gun);
                // the board / pack itself passes all damage to its rider (100 % reduction) - a bot IS the vehicle,
                // so it was invincible: take hits with the rider's armour (Jump Trooper), like a player would
                bot.ArmorSource = _arena._server._assets.getVehicleByID(ClsJumpTrooper);
                MakeAsToughAsPirates(bot);
                if (veh == VehShimmerboard)
                    SetUtility(bot, ItmVehicleShimmer);
                // a few grenades every life, thrown at mid range now and then (6-11 s apart)
                ItemInfo g = Itm(Grenades[_arena._rand.Next(Grenades.Length)]);
                WeaponController grenade = null;
                if (g != null)
                {
                    grenade = new WeaponController(bot._state, new WeaponController.WeaponSettings());
                    if (grenade.equip(g)) grenade.Owner = bot;
                    else grenade = null;
                }
                if (grenade != null)
                    EolBotBrain.SetExtraWeapons(bot, null, grenade, 2 + _arena._rand.Next(2));
            }
            catch (Exception ex)
            {
                Log.write(TLog.Warning, "[Eol] Vehicle squad loadout failed: " + ex.Message);
            }
        }

        /// <summary>Class weapons (close + far), SuperCharger, maybe grenades, maybe the Kamenev AP mk2 opener.</summary>
        void ApplyHumanLoadout(Bots.Bot bot, int cls, Team team)
        {
            try
            {
                Random r = _arena._rand;
                int close, far;
                PickClassWeapons(cls, team, out close, out far);
                EquipCloseFar(bot, close, far);
                MakeAsToughAsPirates(bot);

                // Suit SuperCharger on everyone (a free utility slot, switched on)
                SetUtility(bot, ItmSuperCharger);

                // ~35 % carry 2-3 grenades; ~40 % of riflemen open with a clip of the Kamenev AP mk2
                WeaponController grenade = null, opener = null;
                int grenades = 0;
                if (r.NextDouble() < 0.35)
                {
                    ItemInfo g = Itm(Grenades[r.Next(Grenades.Length)]);
                    if (g != null)
                    {
                        grenade = new WeaponController(bot._state, new WeaponController.WeaponSettings());
                        if (grenade.equip(g)) { grenade.Owner = bot; grenades = 2 + r.Next(2); }
                        else grenade = null;
                    }
                }
                bool rifleman = cls == ClsInfantry || cls == ClsMarine || cls == ClsConscript || cls == ClsJumpTrooper
                    || cls == ClsPartisan || cls == ClsBlackOps || SquadLeaderVehIds.Contains(cls);
                if (rifleman && r.NextDouble() < 0.4)
                {
                    ItemInfo k = Itm(ItmKamenevAPmk2);
                    if (k != null)
                    {
                        opener = new WeaponController(bot._state, new WeaponController.WeaponSettings());
                        if (opener.equip(k)) opener.Owner = bot;
                        else opener = null;
                    }
                }
                EolBotBrain.SetExtraWeapons(bot, opener, grenade, grenades);
            }
            catch (Exception ex)
            {
                Log.write(TLog.Warning, "[Eol] Strike team loadout failed: " + ex.Message);
            }
        }
    }
}
