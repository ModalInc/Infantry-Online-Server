using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// Human-class strike teams (local change 2026-11-14): three roaming teams of 4-8 bots built on the real PLAYER
    /// classes (so they wear the same skins as human players) - Infantry, Heavy Weapons, Jump Trooper, Infiltrator,
    /// Combat Medic, Marine, Conscript, led by an officer (Squad Leader / Sergeant). Every bot carries the Suit
    /// SuperCharger; about a third carry grenades; each gets sensible, hard-hitting class weapons, and some open a
    /// fight with one clip of the Kamenev AP mk2 before switching to their rifle. They use the elite-squad brain
    /// (leader hunts / investigates, the rest move in formation, everyone fights what they see) with a normal skill mix.
    /// </summary>
    public partial class Script_Eol
    {
        public const bool StrikeTeamsEnabled = true;
        const int StrikeTeamMinSize = 4, StrikeTeamMaxSize = 8;
        const int StrikeRespawnDelayMs = 20000;

        // player classes (Pioneer Station .veh)
        const int ClsJumpTrooper = 10, ClsInfantry = 12, ClsHeavy = 13, ClsInfiltrator = 14, ClsMedic = 15,
                  ClsConscript = 111, ClsSquadLeader = 123, ClsSergeant = 133, ClsMarine = 136;
        static readonly int[] StrikeMemberClasses = { ClsInfantry, ClsInfantry, ClsMarine, ClsHeavy, ClsJumpTrooper, ClsInfiltrator, ClsMedic, ClsConscript };
        static readonly int[] StrikeOfficerClasses = { ClsSquadLeader, ClsSergeant };
        /// <summary>Leader chassis for any roaming squad (elite commander + strike officers).</summary>
        static readonly HashSet<int> SquadLeaderVehIds = new HashSet<int> { EliteCommanderVehId, ClsSquadLeader, ClsSergeant, 134, 138 };

        // items (Pioneer Station .itm)
        const int ItmSuperCharger = 14;
        const int ItmKamenevAPmk2 = 3003;
        const int ItmTitanAR = 1097, ItmMaklovAR = 1096, ItmKuchlerLMG = 1057, ItmMaklovLMG = 3028, ItmSteinerSMG = 1001,
                  ItmTitanCarbine = 1002, ItmKuchlerCarbine = 3104, ItmMaklovSniper = 1056, ItmA2GL = 3075, ItmLAW = 3071;
        static readonly int[] Grenades = { 1011, 1011, 1435, 1012 };   // frag (most common), incendiary, EMP

        public Team botTeamStrike1, botTeamStrike2, botTeamStrike3;
        readonly Dictionary<Team, int> _strikeTargetSize = new Dictionary<Team, int>();
        readonly Dictionary<Team, int> _strikeNextSpawnTick = new Dictionary<Team, int>();
        int _tickStrikePoll;

        void CreateStrikeTeams()
        {
            botTeamStrike1 = CreateAndRegisterBotTeam("Bot - Strike Team Alpha");
            botTeamStrike2 = CreateAndRegisterBotTeam("Bot - Strike Team Bravo");
            botTeamStrike3 = CreateAndRegisterBotTeam("Bot - Strike Team Charlie");
        }

        public bool IsStrikeTeam(Team t)
        {
            return t != null && (t == botTeamStrike1 || t == botTeamStrike2 || t == botTeamStrike3);
        }

        /// <summary>Elite squads and strike teams share the roaming-squad brain.</summary>
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
            KeepStrikeTeamFilled(botTeamStrike1, now);
            KeepStrikeTeamFilled(botTeamStrike2, now);
            KeepStrikeTeamFilled(botTeamStrike3, now);
        }

        void KeepStrikeTeamFilled(Team team, int now)
        {
            if (team == null)
                return;
            if (roamBots != null && !roamBots.ContainsKey(team))
                roamBots.Add(team, 0);
            int size;
            if (!_strikeTargetSize.TryGetValue(team, out size))
            {
                size = _arena._rand.Next(StrikeTeamMinSize, StrikeTeamMaxSize + 1);
                _strikeTargetSize[team] = size;
            }

            List<RoamingAttacker> members = EliteSquadMembers(team);
            if (members.Count >= size)
            {
                _strikeNextSpawnTick.Remove(team);
                return;
            }
            int due;
            if (!_strikeNextSpawnTick.TryGetValue(team, out due))
            {
                due = members.Count == 0 ? now : now + StrikeRespawnDelayMs;
                _strikeNextSpawnTick[team] = due;
            }
            if (now - due < 0)
                return;

            bool hasLeader = members.Exists(b => b._type != null && IsSquadLeaderVehicle(b._type.Id));
            int cls = hasLeader
                ? StrikeMemberClasses[_arena._rand.Next(StrikeMemberClasses.Length)]
                : StrikeOfficerClasses[_arena._rand.Next(StrikeOfficerClasses.Length)];
            // splash-gun heavies count against the per-team grenadier cap
            if (cls == ClsHeavy && CountTeamSplashGunners(team) >= MaxGrenadiersPerTeam)
                cls = ClsInfantry;

            Helpers.ObjectState at = null;
            RoamingAttacker leader = EliteSquadLeader(team);
            if (leader != null && !leader.InLiveCombat && !PlayerNear(leader._state.positionX, leader._state.positionY, EliteSpawnAwayFromPlayersRaw))
                at = findOpenWarpBot(_arena, leader._state.positionX, leader._state.positionY, 200);
            if (at == null)
                at = PickEliteSpawnPoint();
            if (at == null)
                return;

            RoamingAttacker bot = _arena.newBot(typeof(RoamingAttacker), (ushort)cls, team, null, at, this, null) as RoamingAttacker;
            if (bot == null)
                return;
            ApplyHumanLoadout(bot, cls, team);
            if (_bots == null)
                _bots = new List<Bots.Bot>();
            _bots.Add(bot);
            roamBots[team]++;

            if (members.Count + 1 >= size)
            {
                _strikeNextSpawnTick.Remove(team);
                _strikeTargetSize.Remove(team);   // re-roll the size for the next time it rebuilds
            }
            else
                _strikeNextSpawnTick[team] = now + 600;
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

        /// <summary>Class weapons (close + far), SuperCharger, maybe grenades, maybe the Kamenev AP mk2 opener.</summary>
        void ApplyHumanLoadout(Bots.Bot bot, int cls, Team team)
        {
            try
            {
                Random r = _arena._rand;
                int close, far;
                switch (cls)
                {
                    case ClsHeavy:
                        close = r.Next(2) == 0 ? ItmKuchlerLMG : ItmMaklovLMG;
                        far = CountTeamSplashGunners(team) < MaxGrenadiersPerTeam ? (r.Next(2) == 0 ? ItmA2GL : ItmLAW) : ItmTitanAR;
                        break;
                    case ClsJumpTrooper:
                        close = ItmSteinerSMG;
                        far = r.Next(2) == 0 ? ItmTitanCarbine : ItmKuchlerCarbine;
                        break;
                    case ClsInfiltrator:
                        close = ItmSteinerSMG;
                        far = ItmMaklovSniper;
                        break;
                    case ClsMedic:
                        close = ItmSteinerSMG;
                        far = ItmTitanCarbine;
                        break;
                    case ClsSquadLeader:
                    case ClsSergeant:
                        close = ItmTitanCarbine;
                        far = ItmTitanAR;
                        break;
                    default:   // Infantry, Marine, Conscript
                        close = r.Next(3) == 0 ? ItmKuchlerCarbine : ItmSteinerSMG;
                        far = r.Next(2) == 0 ? ItmTitanAR : ItmMaklovAR;
                        break;
                }

                WeaponController wClose = bot._weaponClose ?? new WeaponController(bot._state, new WeaponController.WeaponSettings());
                WeaponController wFar = bot._weaponFar ?? new WeaponController(bot._state, new WeaponController.WeaponSettings());
                ItemInfo iClose = Itm(close), iFar = Itm(far);
                if (iClose != null) wClose.equip(iClose);
                if (iFar != null) wFar.equip(iFar);
                BotWeaponUtil.WireLiveCombatWeapons(bot, wClose, wFar, includeFarWeapon: true, seekRangeRaw: EolBotSelfAggroRange);

                // Suit SuperCharger on everyone (a free utility slot, switched on)
                if (bot.Loadout != null)
                {
                    int slot = 5;
                    for (int s = 0; s < BotLoadout.SlotCount; s++)
                        if (bot.Loadout.GetSlotKind(s) == BotLoadoutSlotKind.Empty) { slot = s; break; }
                    bot.Loadout.SetSlot(slot, ItmSuperCharger);
                    bot.Loadout.SetUtilityActive(slot, true);
                }

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
                bool rifleman = cls == ClsInfantry || cls == ClsMarine || cls == ClsConscript || cls == ClsSquadLeader || cls == ClsSergeant || cls == ClsJumpTrooper;
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
