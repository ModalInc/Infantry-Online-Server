using System;
using System.Text.RegularExpressions;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>Server-side flag capture for script bots (no <see cref="CS_PlayerFlag"/> / player carrier).</summary>
    public partial class Script_Eol
    {
        static readonly Regex FlagSkillAtReq = new Regex(@"@(\d+)", RegexOptions.Compiled);
        static readonly Regex FlagSkillHashReq = new Regex(@"#(\d+)", RegexOptions.Compiled);

        /// <summary>Human player teams only — bot factions hold flags visually but never earn periodic payouts.</summary>
        bool TeamQualifiesForFlagRewards(Team team)
        {
            // (2026-11-18, owner: "if you are on a bot team and have flags, there is no rewards ... so the real human
            // players get the rewards") a bot team with players on it is paid like any team - only its players get it
            // (the payout loops over ActivePlayers); a bot team with nobody on it still gets nothing
            return team != null && (!IsManagedEolBotFactionTeam(team) || team.ActivePlayerCount > 0);
        }

        /// <summary>
        /// Raise bot exp/points to satisfy this flag's SkillLogic @ / # gates (from flaginfo).
        /// Class (%) and attribute (-) gates still use the vehicle class / skills.
        /// </summary>
        static void EnsureBotMeetsFlagExpPointsGates(Bot bot, Arena.FlagState fs)
        {
            if (bot == null || fs?.flag == null)
                return;

            string skill = fs.flag.FlagData.SkillLogic;
            if (string.IsNullOrWhiteSpace(skill) || skill == "\"\"")
                return;

            int needExp = 0;
            foreach (Match m in FlagSkillAtReq.Matches(skill))
            {
                int v;
                if (int.TryParse(m.Groups[1].Value, out v) && v > needExp)
                    needExp = v;
            }
            int needPts = 0;
            foreach (Match m in FlagSkillHashReq.Matches(skill))
            {
                int v;
                if (int.TryParse(m.Groups[1].Value, out v) && v > needPts)
                    needPts = v;
            }

            if (needExp > bot._experience)
                bot._experience = needExp;
            if (needPts > bot._killPoints)
                bot._killPoints = needPts;
        }

        /// <summary>
        /// Active on-map flag belonging to <paramref name="team"/> — same rules as the client team list
        /// (bActive + team id), not only object reference.
        /// </summary>
        /// <summary>
        /// (2026-11-18, owner: "make sure the amount of flags they have is accurately captured in the team, yesterday i
        /// saw ... 10 ... flags held, but it was not the case") a flag counts for its team only while it's in play: up and
        /// lying in an open sector, or carried by a live player / bot that is in one. Flags left behind in a sector that
        /// closed (radiation) kept their owner and were counted - and shown in the team list - forever.
        /// </summary>
        bool FlagInPlay(Arena.FlagState fs)
        {
            if (fs == null || !fs.bActive)
                return false;
            if (_eol == null)
                return true;
            if (fs.carrier != null)
                return !fs.carrier.IsDead && fs.carrier._state != null && _eol.IsInAnyActiveSector(fs.carrier._state.positionX, fs.carrier._state.positionY);
            return _eol.IsInAnyActiveSector(fs.posX, fs.posY);
        }

        int _tickOffSectorFlags;

        /// <summary>Every 15 s: an owned flag lying outside the open sectors goes back to no owner, and every client is
        /// told - so the team list's flag count (the client counts the flags' owners) is right too.</summary>
        void PollOffSectorFlags(int now)
        {
            if (_arena == null || _arena._flags == null || _eol == null || now - _tickOffSectorFlags < 15000)
                return;
            _tickOffSectorFlags = now;
            int cleared = 0;
            foreach (Arena.FlagState fs in _arena._flags.Values)
            {
                if (fs == null || !fs.bActive || fs.team == null || fs.carrier != null)
                    continue;
                if (_eol.IsInAnyActiveSector(fs.posX, fs.posY))
                    continue;
                fs.team = null;
                Helpers.Object_Flags(_arena.Players, fs);
                cleared++;
            }
            if (cleared > 0)
                Log.write(TLog.Normal, "[Eol] {0} flag(s) in closed sectors lost their owner (not in play).", cleared);
        }

        static bool TeamOwnsArenaFlag(Team team, Arena.FlagState fs)
        {
            if (team == null || fs == null || !fs.bActive || fs.team == null)
                return false;
            if (fs.team == team)
                return true;
            return fs.team._id == team._id;
        }

        Arena.FlagState FindActiveFlagNear(short x, short y, int rangeRaw)
        {
            if (_arena == null || _arena._flags == null)
                return null;

            long rangeSq = (long)rangeRaw * rangeRaw;
            Arena.FlagState best = null;
            long bestDsq = long.MaxValue;

            foreach (Arena.FlagState fs in _arena._flags.Values)
            {
                if (fs == null || !fs.bActive)
                    continue;

                long dx = (long)fs.posX - x;
                long dy = (long)fs.posY - y;
                long dsq = dx * dx + dy * dy;
                if (dsq > rangeSq || dsq >= bestDsq)
                    continue;

                best = fs;
                bestDsq = dsq;
            }

            return best;
        }

        /// <summary>
        /// Assign <paramref name="fs"/> to <paramref name="bot"/>._team via Arena claim path
        /// (SkillLogic / points / exp / class / pickup delay / range — same gates as players).
        /// </summary>
        public bool TryBotClaimFlag(Bot bot, Arena.FlagState fs, bool force = false)
        {
            if (_arena == null || bot == null || bot.IsDead || fs == null || !fs.bActive || bot._team == null)
                return false;
            if (!IsManagedEolBotFactionTeam(bot._team))
                return false;
            if (fs.team == bot._team)
                return false;

            // Static (FlagCarriable==0) and holdable flags both claim in place for bots —
            // own-on-carry / own-on-drop from flaginfo still gates via Arena.flagClaimByBot.
            if (!force)
                EnsureBotMeetsFlagExpPointsGates(bot, fs);

            return _arena.flagClaimByBot(bot, fs, force);
        }

        /// <summary>Live raider captain near the current collective objective claims that flag if not already owned.</summary>
        void TryRaiderClaimCurrentFlagObjective()
        {
            if (!_pirateRaidWaveActive || !RaiderCollectiveRoamValid || _bots == null)
                return;

            Arena.FlagState fs = FindActiveFlagNear(RaiderCollectiveRoamX, RaiderCollectiveRoamY, RaiderHordeGroupArrivalRaw);
            if (fs == null)
                return;
            if (fs.team == botTeam4)
                return;

            foreach (Bot b in _bots)
            {
                if (b == null || b.IsDead || b._team != botTeam4)
                    continue;
                // Captains only — roamers / grunts must not capture.
                if (!(b is RoamingCaptain))
                    continue;
                if (!TryBotClaimFlag(b, fs))
                    continue;

                _arena.sendArenaMessage(
                    string.Format("&Pirate raiders have captured a flag at {0}.",
                        Helpers.posToLetterCoord(fs.posX, fs.posY)),
                    _config.flag.periodicBong);
                return;
            }
        }

        const short BotTauntProjDefenseRaiderA = 1260;
        const short BotTauntProjDefenseRaiderB = 1261;
        const short BotTauntProjAlienA = 1262;
        const short BotTauntProjAlienB = 1263;

        /// <summary>GG/EZ-style taunt at the victim's death position when a bot gets a kill (player or bot).</summary>
        /// <summary>Local 2026-11-14: off - real players don't drop GG/EZ grenades on you; bots shouldn't either.</summary>
        public const bool BotKillTauntsEnabled = false;

        void QueueBotKillTauntExplosion(Bot killerBot, short posX, short posY)
        {
            if (!BotKillTauntsEnabled || _arena == null || killerBot == null)
                return;

            short projId;
            if (killerBot is AlienHorde || IsAlienBotTeam(killerBot._team))
                projId = (_rand.Next(0, 2) == 0) ? BotTauntProjAlienA : BotTauntProjAlienB;
            else if (killerBot is BasicDefense || killerBot is Captain || killerBot is Engineer
                || killerBot is RoamingAttacker || killerBot is RoamingCaptain
                || IsDefenseBotTeam(killerBot._team) || IsRoamBotTeam(killerBot._team))
                projId = (_rand.Next(0, 2) == 0) ? BotTauntProjDefenseRaiderA : BotTauntProjDefenseRaiderB;
            else
                // Any other bot kill still taunts (soft-mimic ids / unknown teams).
                projId = (_rand.Next(0, 2) == 0) ? BotTauntProjDefenseRaiderA : BotTauntProjDefenseRaiderB;

            EnqueueExplosionRoute(projId, posX, posY, 0, 0, (ushort)killerBot._id);
        }

        /// <summary>GG/EZ taunt when a bot kills a player — uses death packet coords when available.</summary>
        void QueueBotKillTauntExplosion(Bot killerBot, InfServer.Game.Player victim, CS_VehicleDeath deathUpdate)
        {
            if (killerBot == null || victim == null)
                return;
            if (victim._team != null && killerBot._team != null && victim._team == killerBot._team)
                return;

            short posX;
            short posY;
            if (deathUpdate != null)
            {
                posX = deathUpdate.positionX;
                posY = deathUpdate.positionY;
            }
            else
            {
                posX = victim._state.positionX;
                posY = victim._state.positionY;
            }

            QueueBotKillTauntExplosion(killerBot, posX, posY);
        }
    }
}
