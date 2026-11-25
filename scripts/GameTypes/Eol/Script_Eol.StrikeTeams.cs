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
    /// skins as human players. Part of the 60-bot roster (see Script_Eol.Roster.cs):
    ///   - 'Storm Rangers Bots': human roamers, 5-8, normal skill mix;
    ///   - three mixed teams of 3-8 that make up the rest of the 60, each with its own skill (ace .. normal mix);
    ///   - 'Ghost Reapers Bots' (botTeam8): an elite vehicle squad of 3 - Deathboard, Shimmerboard, Drop Pack.
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
            ClsInfantry, ClsHeavy, ClsJumpTrooper, ClsInfiltrator, ClsMedic, ClsEngineer, ClsBioChem, ClsMPAC,   // Partisan / Paramedic (Paramilitary) removed 2026-11-14
            ClsNeuroTAC, ClsSciOps,   // (2026-11-17, owner: 'have no conscripts' - only the F11 classes) Combat Tech removed (local 2026-11-14) ClsDropTrooper, ClsMarine, ClsFieldSurgeon, ClsChiefEngineer, ClsBlackOps,
        };
        // Colonel (151) and Grand Marshal aren't ready yet (local 2026-11-14) - not fielded
        static readonly int[] StrikeOfficerClasses = { ClsSquadLeader, ClsSergeant, ClsLieutenant, ClsCaptain };
        /// <summary>Leader chassis for any roaming squad (pirate commander + human officers).</summary>
        static readonly HashSet<int> SquadLeaderVehIds = new HashSet<int> { EliteCommanderVehId, ClsSquadLeader, ClsSergeant, ClsCaptain, ClsLieutenant, ClsColonel };

        // elite vehicle squad (personal vehicles)
        const int VehDeathboard = 108, VehShimmerboard = 131, VehDropPack = 109;
        static readonly int[] VehicleSquadVehIds = { VehDeathboard, VehShimmerboard, VehDropPack, VehHoverboard };   // (+ the board medic, 2026-11-17)
        const int ItmDeathboardSlash = 1047, ItmVehicleShimmer = 174, ItmDropPackBolt = 3008;   // 'DropPack Blaster' - its own twin bolts

        // items (Pioneer Station .itm)
        const int ItmSuperCharger = 14;
        const int ItmKamenevAPmk2 = 3003;
        const int ItmTitanAR = 1097, ItmKuchlerCAW = 1004;
        static readonly int[] Grenades = { 1011, 1011, 1435, 1012 };   // frag (most common), incendiary, EMP

        // weapon pools - each class mixes from these so no two squads look the same
        static readonly int[] PoolRifle = { 1097, 1096, 1003, 3094, 3025, 1024, 3103, 3038, 3016 };
        static readonly int[] PoolSmg = { 1001, 1109, 1305, 1002, 3104, 3051 };
        // (local 2026-11-14: no charge-up guns that must be fired standing still - MG94k, ACp-90, GC 10a, MC1,
        // IRL909 - bots never stop to shoot; no items without a usable projectile - Titan CCS, Havzeks Stroker)
        static readonly int[] PoolShotgun = { 3023, 1016, 3001, 1465 };
        static readonly int[] PoolCarbine = { 1002, 3104, 1001, 3094 };
        static readonly int[] PoolHeavy = { 1057, 3028, 3021, 1310, 3105, 3110, 1004, 1061, 3031, 3095, 3102 };   // (2026-11-17: + the autocannons AC 2000k, AC mk2, ACp-90, LPC-4 - they plant to fire)
        static readonly int[] PoolPlasma = { 1044, 1104, 1007, 1188, 1311 };   // (2026-11-18: Maklov PP08 pistol out - pistols are sidearms only)
        static readonly int[] PoolPlasmaSmg = { 1100, 109, 1006, 1343 };
        static readonly int[] PoolGauss = { 1031, 1038, 1141, 1114, 1533 };
        static readonly int[] PoolLaser = { 1103, 1157, 1607 };
        static readonly int[] PoolProjector = { 1146, 1126, 1000 };
        /// <summary>(2026-11-17, owner: 'blackops sometimes have proton sphere') Black Ops energy weapon (skill 13 = BlackOps).</summary>
        const int ItmProtonSphere = 1623;
        /// <summary>Stealth utilities: Improved Cloak (Infiltrator), Blackout Field (Black Ops suit), Cloaking Unit (SciOps).</summary>
        const int ItmImprovedCloak = 43, ItmBlackoutField = 79, ItmCloakingUnit = 5;
        static readonly int[] PoolProton = { ItmProtonSphere };

        public Team botTeamStrike1;
        readonly List<Team> _mixedTeams = new List<Team>();
        readonly Dictionary<Team, int> _strikeTargetSize = new Dictionary<Team, int>();
        readonly Dictionary<Team, string> _strikeSkill = new Dictionary<Team, string>();
        readonly Dictionary<Team, int> _strikeNextSpawnTick = new Dictionary<Team, int>();
        int _tickStrikePoll;

        // (2026-11-24, owner: "put the bots in the normal teams ... the rest the normal teams please starting with the standard
        // ones ... if they play sector C there is more teams, have them use the same teams going down the numbers") the squads
        // ARE the classic public factions now - new players land on a team that already has bots
        static readonly string[] MixedTeamNames = { "Collective Military", "Europan Mercs", "Morgan Raiders", "Tethys Consortium", "United Moons",
            "Phobos Militarius", "Callisto Irregulars", "Ganymede Rangers" };   // (the last 3: Sector C only; StarFire Corps = the vehicle squad; Low Life Thugs = the Harrier crews)

        void CreateStrikeTeams()
        {
            botTeamStrike1 = SquadTeam("Titan Militia");
            _mixedTeams.Clear();
            for (int i = 0; i < RosterMixedTeamCount && i < MixedTeamNames.Length; i++)
                _mixedTeams.Add(SquadTeam(MixedTeamNames[i]));
        }

        /// <summary>
        /// (2026-11-16e, owner: 'join without pressing enter') a joinable squad uses the zone .cfg's own team of that
        /// name (TeamInfo8-13 renamed to the squads): the client treats .cfg teams as public and joins them with one
        /// click, while a team the script makes up is 'private' to it and asks for a password first. Not in the
        /// .cfg (old zone files): made up as before.
        /// </summary>
        Team SquadTeam(string name)
        {
            Team t = _arena.getTeamByName(name);
            if (t == null)
                return CreateAndRegisterBotTeam(name);
            t._isPrivate = false;
            t._password = "";
            t._owner = null;
            return t;
        }

        public bool IsStrikeTeam(Team t)
        {
            return t != null && (t == botTeamStrike1 || _mixedTeams.Contains(t));
        }

        /// <summary>The elite vehicle squad (boards / pack).</summary>
        /// <summary>(2026-11-24) the vehicle squad's captain while it is on its pack.</summary>
        readonly HashSet<Bots.Bot> _vehSquadCaptains = new HashSet<Bots.Bot>();
        public bool IsVehicleSquadCaptain(Bots.Bot b)
        {
            if (b == null)
                return false;
            lock (_vehSquadCaptains)
            {
                if (!_vehSquadCaptains.Contains(b))
                    return false;
                if (b.IsDead || b.bCondemned)
                {
                    _vehSquadCaptains.Remove(b);
                    return false;
                }
                return true;
            }
        }

        public bool IsVehicleSquadTeam(Team t)
        {
            return t != null && t == botTeam8;
        }

        /// <summary>Pirate roamers, the vehicle squad and the human squads share the roaming-squad brain.</summary>
        public bool IsRoamingSquadTeam(Team t)
        {
            return IsEliteSquadTeam(t) || IsStrikeTeam(t) || IsPirateRaidTeam(t);
        }

        /// <summary>(2026-11-16b, owner) roaming squads are 6-8 - players and bots together (was 5).</summary>
        public const int SquadMaxSize = 6;   // (2026-11-16c, owner: 5-6 man teams)

        /// <summary>
        /// (2026-11-16b, owner: 'a real team') every human squad fields these first, in this order - a commander
        /// (one of the officer classes), medic, heavy weapons, marine, infantry, engineer - then fighters of any
        /// other class for the 7th / 8th place.
        /// </summary>
        // (2026-11-17, owner: 'medic, command and engy always there, the other 3 a class at random ... so it feels much
        // more alive') the three fixed roles; the rest of the squad is drawn from SquadRandomClasses, no class twice
        static readonly int[] SquadCoreRoles = { -1, ClsMedic, ClsChiefEngineer };
        static readonly int[] SquadRandomClasses =
        {
            ClsInfantry, ClsHeavy, ClsMarine, ClsJumpTrooper, ClsInfiltrator, ClsBioChem, ClsMPAC,
            ClsNeuroTAC, ClsSciOps, ClsBlackOps, ClsDropTrooper,   // (2026-11-23 + Drop Trooper) (2026-11-17: no Conscripts, no Colonel / Partisan / Combat Tech - not on F11)
        };

        bool MemberHasRole(Bots.Bot b, int role)
        {
            if (b == null || b._type == null)
                return false;
            return role == -1 ? IsSquadLeaderVehicle(b._type.Id) : b._type.Id == role;
        }

        /// <summary>The class the squad needs next: the first core role nobody has, else an extra fighter.</summary>
        int NextSquadClass(List<RoamingAttacker> members, Team team = null)
        {
            foreach (int role in SquadCoreRoles)
                if (!members.Exists(m => MemberHasRole(m, role)))
                    return role == -1 ? PickLeastFieldedClass(StrikeOfficerClasses) : role;
            // (2026-11-17) the classes the players picked ('change squad ...', Script_Eol.SquadClasses.cs) come first
            int chosen = NextChosenClass(team, members);
            if (chosen != 0)
                return chosen;
            // a random fighting class the squad doesn't have yet (any, if it has them all)
            List<int> fresh = new List<int>();
            foreach (int c in SquadRandomClasses)
                if (!members.Exists(m => m._type != null && m._type.Id == c))
                    fresh.Add(c);
            return fresh.Count > 0 ? fresh[_arena._rand.Next(fresh.Count)] : SquadRandomClasses[_arena._rand.Next(SquadRandomClasses.Length)];
        }

        /// <summary>
        /// (2026-11-16f, owner) who stays when players take the places: the medic stays longest, then the commander,
        /// then the engineer; the fighting classes go first (extras, infantry, marine, heavy).
        /// </summary>
        int SquadKeepRank(Bots.Bot b)
        {
            if (b == null || b._type == null)
                return 0;
            int id = b._type.Id;
            if (id == ClsMedic || id == ClsFieldSurgeon) return 100;
            if (IsSquadLeaderVehicle(id)) return 90;
            if (id == ClsEngineer || id == ClsChiefEngineer) return 80;
            if (IsChosenClass(b)) return 50;   // (2026-11-17) a class the players picked goes after the random fighters
            if (id == ClsHeavy) return 30;
            if (id == ClsMarine) return 20;
            if (id == ClsInfantry) return 10;
            return 0;   // the other fighters
        }

        /// <summary>(2026-11-16f, owner) a squad with players on it is 5 at most - players and bots together.</summary>
        public const int SquadMaxWithPlayers = 6;   // (2026-11-17, owner: teams of 6, players and bots together)

        /// <summary>Roaming squads players may join (pirate elite squad, vehicle squad, human squads).</summary>
        public bool IsJoinableSquadTeam(Team t)
        {
            return t != null && IsRoamingSquadTeam(t) && !IsAssassinTeam(t) && !IsPirateRaidTeam(t) && !IsHarrierCrewTeam(t);   // (2026-11-26: nobody joins the Harrier crews)
        }

        /// <summary>Players on a squad right now (not spectating).</summary>
        public int SquadHumans(Team t)
        {
            return t == null ? 0 : t.ActivePlayerCount;
        }

        /// <summary>Bots this squad may hold: its roster size (8 at most) minus its players.</summary>
        int SquadBotRoom(Team t, int rosterSize)
        {
            int humans = SquadHumans(t);
            int cap = Math.Min(rosterSize, SquadMaxSize);
            if (humans > 0)
                cap = Math.Min(cap, SquadMaxWithPlayers);   // (players on it: 5 in all)
            return Math.Max(0, cap - humans);
        }

        /// <summary>
        /// Bots on a squad: out in the field (or walking in through the dropship), plus - with
        /// <paramref name='countPending'/> - dead ones waiting to come back through the dropship.
        /// </summary>
        public int SquadStrength(Team t, bool countPending)
        {
            int n = EliteSquadMembers(t).Count;
            if (countPending && _arena != null)
                foreach (Bots.Bot b in _arena.ActiveBots)
                    if (b != null && b._team == t && b.IsDead && b.bDropshipRespawnPending && !b.bCondemned)
                        n++;
            return n;
        }

        /// <summary>The squad's full strength (players + bots): its roster size, 6 at most.</summary>
        int SquadTargetSize(Team t)
        {
            int size;
            if (t != null && _strikeTargetSize.TryGetValue(t, out size) && size > 0)
                size = Math.Min(size, SquadMaxSize);
            else if (t != null && t == botTeam6)
                size = Math.Min(RosterPirateRoamers, SquadMaxSize);
            else
                size = SquadMaxSize;
            // players on it: 5 in all
            if (SquadHumans(t) > 0)
                size = Math.Min(size, SquadMaxWithPlayers);
            return size;
        }

        /// <summary>
        /// Kick bots out of a squad until bots + players (+ <paramref name='joining'/>) fit in its full strength
        /// (2026-11-16b: was always the cap, so a squad of 6 plus a player stayed at 7). Never the leader while
        /// another bot can go.
        /// </summary>
        public void TrimSquadForHumans(Team team, int joining)
        {
            if (!IsJoinableSquadTeam(team) || _arena == null)
                return;
            int humans = SquadHumans(team) + joining;
            int full = SquadTargetSize(team);
            if (humans > 0)
                full = Math.Min(full, SquadMaxWithPlayers);   // (the joining player counts already)
            int room = Math.Max(0, full - humans);
            List<RoamingAttacker> members = EliteSquadMembers(team);
            int safety = 10;
            while (members.Count > room && safety-- > 0)
            {
                // the least needed member goes: fighters first, then the engineer, the commander - the medic last
                RoamingAttacker go = null;
                foreach (RoamingAttacker m in members)
                    if (go == null || SquadKeepRank(m) < SquadKeepRank(go))
                        go = m;
                members.Remove(go);
                EnqueueVehicleDespawn(go, force: true);
                if (_bots != null)
                    _bots.Remove(go);
            }
            if (roamBots != null)
                roamBots[team] = EliteSquadMembers(team).Count;
        }

        /// <summary>The nearest live bot-base HQ that is not on <paramref name='self'/>'s team (null = none).</summary>
        public Helpers.ObjectState NearestEnemyBaseHq(Bots.Bot self)
        {
            if (self == null || _arena == null || self._state == null)
                return null;
            Vehicle best = null;
            double bestD = double.MaxValue;
            foreach (Vehicle v in _arena.Vehicles)
            {
                if (v == null || v._type == null || v.IsDead || v.bCondemned || !IsHqVehId(v._type.Id))
                    continue;
                if (v._team == null || v._team == self._team || !IsDefenseBotTeam(v._team))
                    continue;
                double d = Helpers.distanceTo(self._state, v._state);
                if (d < bestD) { bestD = d; best = v; }
            }
            return best == null ? null : new Helpers.ObjectState { positionX = best._state.positionX, positionY = best._state.positionY };
        }

        public static bool IsSquadLeaderVehicle(int vehId)
        {
            return SquadLeaderVehIds.Contains(vehId);
        }

        void PollStrikeTeams(int now)
        {
            PollPendingFoot(now);   // (2026-11-18) vehicle-squad riders on foot after their vehicle went down
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

            // mixed teams top the field up to 60 (not past it while the assassins are out)
            s_sectorCOpen = _eol != null && _eol._activeSectors != null && _eol._activeSectors.Contains("Sector C");
            int live = CountLiveRosterBots();
            for (int i = 0; i < _mixedTeams.Count; i++)
            {
                Team t = _mixedTeams[i];
                _strikeTargetSize[t] = i < MixedTeamsActive ? _rosterMixedSize[i] : 0;   // (the Sector C squads: 0 while C is shut)
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
            size = SquadBotRoom(team, size);   // 6 max, players take bot places
            if (members.Count > size)
                TrimSquadForHumans(team, 0);
            // (2026-11-16d) dead bots coming back through the dropship still hold their place: no extra bot
            int strength = SquadStrength(team, countPending: true);
            if (strength >= size)
            {
                _strikeNextSpawnTick.Remove(team);
                return false;
            }
            int due;
            if (!_strikeNextSpawnTick.TryGetValue(team, out due))
            {
                due = strength == 0 ? now : now + SquadRefillDelay(team);
                _strikeNextSpawnTick[team] = due;
            }
            if (now - due < 0)
                return false;

            bool vehicleSquad = IsVehicleSquadTeam(team);
            bool pickedClass = false;
            int cls;
            bool captainPack = false;
            if (vehicleSquad)
            {
                // (2026-11-24, owner: "give them a captain on a pack") first of all the squad's captain, on a Drop Pack
                // (back on foot as a Captain once the pack goes down - Script_Eol.VehicleSquadFoot.cs)
                if (!members.Exists(b => IsVehicleSquadCaptain(b) || (b._type != null && b._type.Id == ClsCaptain)))
                {
                    cls = VehDropPack;
                    captainPack = true;
                }
                else
                {
                    // one of each: Deathboard, Shimmerboard, Drop Pack - and (2026-11-17, owner) a 4th: a SciOps on a Flight Pack
                    cls = VehFlightPack;
                    foreach (int v in VehicleSquadVehIds)
                        if (!members.Exists(b => b._type != null && b._type.Id == v && !IsVehicleSquadCaptain(b))) { cls = v; break; }
                }
            }
            else
            {
                cls = NextSquadClass(members, team);   // commander, medic, engineer, then the picked classes, then extras
                pickedClass = cls != 0 && cls == NextChosenClass(team, members);
            }

            // (2026-11-16) in the dropship, then through the portal (Script_Eol.Deploy.cs); fallback: the field
            if (DropshipBusy)
                return false;   // (2026-11-16e) a few walking through already - wait a moment (no crowd, no choppiness)
            bool viaDropship;
            Helpers.ObjectState at = SquadSpawnPoint(() =>
            {
                Helpers.ObjectState f = null;
                RoamingAttacker leader = EliteSquadLeader(team);
                if (leader != null && !leader.InLiveCombat && InPlayArea(leader._state.positionX, leader._state.positionY) && !PlayerNear(leader._state.positionX, leader._state.positionY, EliteSpawnAwayFromPlayersRaw))
                {
                    f = findOpenWarpBot(_arena, leader._state.positionX, leader._state.positionY, 200);
                    if (f != null && (!InPlayArea(f.positionX, f.positionY, 120) || !IsOpenConnectedGround(f.positionX, f.positionY)))
                        f = null;
                }
                return f ?? PickEliteSpawnPoint();
            }, out viaDropship);
            if (at == null)
                return false;

            RoamingAttacker bot = !CanSpawnBotOnTeam(team) ? null
                : _arena.newBot(typeof(RoamingAttacker), (ushort)cls, team, null, at, this, null) as RoamingAttacker;
            if (bot == null)
                return false;
            string skill;
            if (_strikeSkill.TryGetValue(team, out skill) && skill != null)
                bot.SkillModeOverride = skill;
            if (vehicleSquad && cls == VehHoverboard)
                ApplyBoardMedicLoadout(bot);   // (2026-11-17) the Ghost Reapers' board medic
            else if (vehicleSquad)
            {
                ApplyVehicleSquadLoadout(bot, cls, sciOpsPack: cls == VehFlightPack);
                if (captainPack)
                {   // the captain: leads the squad, takes hits with a Captain's armour
                    lock (_vehSquadCaptains) _vehSquadCaptains.Add(bot);
                    bot.ArmorSource = _arena._server._assets.getVehicleByID(ClsCaptain);
                    ApplyPlayerArmor(bot, ClsCaptain, useClassTable: true);
                }
            }
            else
                ApplyHumanLoadout(bot, cls, team);
            if (viaDropship)
                BeginDeploy(bot, now);
            if (!vehicleSquad)
                EnableBotDropshipRespawn(bot, 0);   // (2026-11-16d) dies -> corpse -> back through the dropship, same bot
            // (2026-11-18) vehicle squad: losing the vehicle puts its rider on foot (Script_Eol.VehicleSquadFoot.cs)
            if (_bots == null)
                _bots = new List<Bots.Bot>();
            _bots.Add(bot);
            roamBots[team]++;
            if (pickedClass)
                NoteSquadSwapSpawned(team);   // (2026-11-17) one more picked class in (Script_Eol.SquadClasses.cs)

            if (strength + 1 >= size)
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
                    // (2026-11-16, owner: one-shot from range) no A2 Grenade Launcher - its shell is the vehicle Harrier GL
                    // round; heavies carry the close-range Kuchler A6 CAW instead
                    far = r.Next(2) == 0 ? ItmKuchlerCAW : Pick(PoolHeavy);
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
                    far = r.NextDouble() < 0.35 ? ItmProtonSphere : r.Next(2) == 0 ? Pick(PoolPlasma) : Pick(PoolSmg);
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
            // (2026-11-16, owner) only guns the class may really use, and a pistol / SMG in the mix
            EnforceClassWeapons(cls, ref close, ref far);
        }

        /// <summary>(2026-11-17) sidearm (close slot), main (far slot) and misc gun for a class - see PickThreeWeapons.</summary>
        void PickClassWeapons(int cls, Team team, out int side, out int main, out int misc)
        {
            int close, far;
            PickClassWeapons(cls, team, out close, out far);
            if (cls == ClsBlackOps && far == ItmProtonSphere)
            {   // the Proton Sphere stays their main when they roll it (owner, part 11)
                int s0, m0;
                PickThreeWeapons(cls, 0, out s0, out m0, out misc);
                side = s0; main = ItmProtonSphere; return;
            }
            PickThreeWeapons(cls, far, out side, out main, out misc);
            if (main == 0) { side = close; main = far; }
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
            if (bot._peakHealth < HumanBotHealth)
                bot._peakHealth = HumanBotHealth;   // (2026-11-17) its full bar for the team's health display
        }

        /// <summary>Boards slash (the Shimmerboard runs its shimmer field); the Drop Pack fires its plasma bolts.</summary>
        /// <summary>(2026-11-17, owner: 'sciops is a flightpack') the vehicle squad's 4th: a SciOps on a Flight Pack (106) - the pink Proton Beamer and the Plasma Bomb.</summary>
        const int ItmProtonBeamer = 1125, ItmPlasmaBomb = 120, VehFlightPack = 106;
        const int ItmCombatSword = 1080;
        /// <summary>Energy guns for the Flight Pack SciOps's third slot: Proton Accelerator, Particle Accelerators, WaveTech MR2 / MR1, FusionCorp IRLSMG.</summary>
        static readonly int[] SciOpsEnergyGuns = { 1478, 1101, 1055, 1103, 1157, 1607 };

        /// <summary>A random SciOps-legal energy gun (by the item's own skill logic); 0 = none.</summary>
        int PickSciOpsEnergyGun()
        {
            VehInfo veh = _arena._server._assets.getVehicleByID(ClsSciOps);
            List<int> skills = ClassSkillIds(ClsSciOps), ok = new List<int>();
            foreach (int id in SciOpsEnergyGuns)
            {
                ItemInfo it = Itm(id);
                if (it != null && it.id != ItmProtonBeamer && (it is ItemInfo.Projectile || it is ItemInfo.MultiUse)
                    && (veh == null || skills.Count == 0 || ClassMayUse(it.skillLogic, veh.ClassId, skills)))
                    ok.Add(id);
            }
            return ok.Count == 0 ? 0 : ok[_arena._rand.Next(ok.Count)];
        }

        void ApplyVehicleSquadLoadout(Bots.Bot bot, int veh, bool sciOpsPack = false)
        {
            if (bot != null && veh == VehShimmerboard)
                bot.KeepCloakOn = true;   // (2026-11-22, owner: the shimmer board bot leaves its shimmer on)
            try
            {
                int gun = veh == VehDropPack ? ItmDropPackBolt : ItmDeathboardSlash;
                if (sciOpsPack)
                {   // (2026-11-17, owner: 'the pink gun he has now as a main gun, and secondary is plasma bomb, and third maybe
                    // some other cool gun in energy') main = Proton Beamer (far slot), secondary = Plasma Bomb (close slot, also
                    // thrown on its timer below), third = a SciOps-legal energy gun in the misc rotation
                    EquipCloseFar(bot, ItmPlasmaBomb, ItmProtonBeamer);
                    int eg = PickSciOpsEnergyGun();
                    ItemInfo ei = eg != 0 ? Itm(eg) : null;
                    WeaponController we = new WeaponController(bot._state, new WeaponController.WeaponSettings());
                    if (ei != null && we.equip(ei)) { we.Owner = bot; EolBotBrain.SetMiscWeapon(bot, we); }
                }
                else
                    EquipCloseFar(bot, gun, gun);
                // the board / pack itself passes all damage to its rider (100 % reduction) - a bot IS the vehicle,
                // so it was invincible: take hits with the rider's armour (Jump Trooper / SciOps), like a player would
                bot.ArmorSource = _arena._server._assets.getVehicleByID(sciOpsPack ? ClsSciOps : ClsJumpTrooper);
                if (veh == VehShimmerboard)
                    SetUtility(bot, ItmVehicleShimmer);
                // (2026-11-17) the rider's player armour + energy (replaces the old bot armour)
                ApplyPlayerArmor(bot, sciOpsPack ? ClsSciOps : ClsJumpTrooper, useClassTable: true);
                // a few grenades every life, thrown at mid range now and then (6-11 s apart)
                ItemInfo g = Itm(sciOpsPack ? ItmPlasmaBomb : Grenades[_arena._rand.Next(Grenades.Length)]);
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
                int close, far, misc;
                PickClassWeapons(cls, team, out close, out far, out misc);
                EquipCloseFar(bot, close, far);   // close slot = sidearm, far slot = main
                if (misc != 0)
                {
                    ItemInfo mi = Itm(misc);
                    WeaponController wm = new WeaponController(bot._state, new WeaponController.WeaponSettings());
                    if (mi != null && wm.equip(mi)) { wm.Owner = bot; EolBotBrain.SetMiscWeapon(bot, wm); }
                }
                // (2026-11-18) the cloak classes carry a Combat Sword for when their energy runs low (EolBotBrain.RotateGuns)
                if (cls == ClsInfiltrator || cls == ClsBlackOps)
                {
                    ItemInfo ki = Itm(ItmCombatSword);
                    WeaponController wk = new WeaponController(bot._state, new WeaponController.WeaponSettings());
                    if (ki != null && wk.equip(ki)) { wk.Owner = bot; EolBotBrain.SetMeleeWeapon(bot, wk); }
                }

                // Suit SuperCharger on everyone (a free utility slot, switched on)
                SetUtility(bot, ItmSuperCharger);

                // (2026-11-17, owner) stealth classes: Infiltrators always run the Improved Cloak, Black Ops the
                // Blackout Field (the Black Ops suit - seen only within 20 px), and some SciOps a Cloaking Unit. Firing
                // drops the cloak for 3 s, like a player's (engine: Helpers.BotWireUtilities)
                if (cls == ClsInfiltrator)
                    SetUtility(bot, ItmImprovedCloak);
                else if (cls == ClsBlackOps)
                    SetUtility(bot, ItmBlackoutField);
                else if (cls == ClsSciOps && r.NextDouble() < 0.4)
                    SetUtility(bot, ItmCloakingUnit);

                // (2026-11-17, owner) a real player's armour set for the class + player energy - replaces the old bot armour
                ApplyPlayerArmor(bot, cls, useClassTable: bot._type == null || bot._type.Id != cls);

                // ~35 % carry 2-3 grenades; ~40 % of riflemen open with a clip of the Kamenev AP mk2
                WeaponController grenade = null, opener = null;
                int grenades = 0;
                int gid = r.NextDouble() < 0.35 ? PickClassGrenade(bot) : 0;   // (class-legal grenades only)
                if (gid != 0)
                {
                    ItemInfo g = Itm(gid);
                    if (g != null)
                    {
                        grenade = new WeaponController(bot._state, new WeaponController.WeaponSettings());
                        if (grenade.equip(g)) { grenade.Owner = bot; grenades = 2 + r.Next(2); }
                        else grenade = null;
                    }
                }
                bool rifleman = cls == ClsInfantry || cls == ClsMarine || cls == ClsConscript || cls == ClsJumpTrooper
                    || cls == ClsPartisan || cls == ClsBlackOps || SquadLeaderVehIds.Contains(cls);
                if (rifleman && r.NextDouble() < 0.4 && ClassMayCarry(bot, ItmKamenevAPmk2))
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
