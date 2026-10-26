using System;
using System.Collections.Generic;
using System.Linq;

using InfServer.Game;
using InfServer.Protocol;
using InfServer.Bots;

using Assets;

namespace InfServer.Script.GameType_Eol
{   // Script Class
    /// Provides the interface between the script and bot
    ///////////////////////////////////////////////////////
    public partial class RoamingCaptain : Bot
    {
        /// <summary>
        /// Checks to see if we're too far from the team
        /// </summary>
        protected bool checkTeamDistance()
        {
            if (_team == null)
                return false;

            //sanity check
            if (_team.ActivePlayerCount == 0)
                return true;

            int minDist = int.MaxValue;

            //finds minimum distance from any one of the activeplayers
            foreach (InfServer.Game.Player player in _team.ActivePlayers)
            {
                //uses the max{dx,dy} metric for distance estimate
                int dist = Math.Max(Math.Abs(_state.positionX - player._state.positionX), Math.Abs(_state.positionY - player._state.positionY));

                if (dist < minDist)
                    minDist = dist;
            }

            return minDist < c_MaxRespawnDist + c_DistanceLeeway;
        }

        protected List<InfServer.Game.Player> enemiesInRange()
        {
            return _arena.getPlayersInRange(_state.positionX, _state.positionY, c_playerMaxRangeEnemies)
                .FindAll((InfServer.Game.Player p) => p._team != _team && !p.IsDead && EolBotSenseUtil.BotCanSeePlayer(_state, p));
        }

        protected List<InfServer.Game.Player> friendliesInRange()
        {
            return _arena.getPlayersInRange(_state.positionX, _state.positionY, c_playerMaxRangeEnemies)
                .FindAll((InfServer.Game.Player p) => p._team == _team && !p.IsDead);
        }

        protected List<Vehicle> getFriendlyBotsInRange()
        {
            return _arena.getVehiclesInRange(_state.positionX, _state.positionY, Script_Eol.EolBotSelfAggroRange,
                delegate (Vehicle v)
                {
                    return (v is Bot) && v._team == _team;
                });
        }

        protected List<Vehicle> getEnemyBotsInRange()
        {
            Script_Eol script = _baseScript;
            return _arena.getVehiclesInRange(_state.positionX, _state.positionY, Script_Eol.EolBotSelfAggroRange,
                delegate (Vehicle v)
                {
                    Bot enemy = v as Bot;
                    if (enemy == null || script == null || !script.ShouldEolBotTeamsFight(_team, enemy._team))
                        return false;
                    // No wall-hug: only sense enemy bots with clear weapon LOS.
                    return BotCombat.CanDirectEngage(this, enemy._state);
                });
        }

        /// <summary>When raiding, hostiles within <see cref="Script_Eol.EolBotSelfAggroRange"/> of this bot are fought.</summary>
        protected bool PlayerNearRaiderObjectiveEngage(InfServer.Game.Player p)
        {
            if (_baseScript != null && _baseScript.IsBountyHunterBotTeam(_team))
                return _baseScript.IsBountyHunterEngageTarget(p)
                    && Helpers.isInRange(Script_Eol.EolBotSelfAggroRange, _state, p._state);
            return BotHostileInEngageRange(p);
        }

        protected bool BotNearRaiderObjectiveEngage(Vehicle v)
        {
            if (_baseScript != null && _baseScript.IsBountyHunterBotTeam(_team))
                return false;
            return BotHostileVehicleInEngageRange(v);
        }

        protected bool BotHostileInEngageRange(InfServer.Game.Player p)
        {
            if (p == null)
                return false;
            if (_baseScript != null && _baseScript.IsBountyHunterBotTeam(_team))
                return _baseScript.IsBountyHunterEngageTarget(p)
                    && Helpers.isInRange(Script_Eol.EolBotSelfAggroRange, _state, p._state);
            if (_target == p && !p.IsDead
                && Helpers.isInRange(Script_Eol.EolBotSelfAggroRange, _state, p._state))
                return true;
            return Helpers.isInRange(Script_Eol.EolBotSelfAggroRange, _state, p._state);
        }

        protected bool BotHostileVehicleInEngageRange(Vehicle v)
        {
            if (v == null)
                return false;
            if (_baseScript != null && _baseScript.IsBountyHunterBotTeam(_team))
                return false;
            if (!Helpers.isInRange(Script_Eol.EolBotSelfAggroRange, _state, v._state))
                return false;
            Bot enemy = v as Bot;
            if (enemy == null)
                return false;
            return BotCombat.CanDirectEngage(this, enemy._state);
        }

        /// <summary>Keep fighting during flag march when hostiles are nearby or we already have a live target in range.</summary>
        protected bool BotShouldEngageInsteadOfIdle(int now)
        {
            return EolBotSenseUtil.ShouldBreakIdleForCombat(
                _arena, _state, _team, Script_Eol.EolBotSelfAggroRange, _target,
                () => HostilesNearbyCached(now));
        }


        /// <summary>
        /// Obtains a suitable target player
        /// </summary>
        protected InfServer.Game.Player getTargetPlayer(ref bool bInSight)
        {
            if (_baseScript != null && _baseScript.IsBountyHunterBotTeam(_team))
            {
                if (!_baseScript.IsBountyHunterMarkInField())
                {
                    bInSight = false;
                    return null;
                }
                InfServer.Game.Player mark = _baseScript.BountyHuntMarked;
                if (mark != null && !mark.IsDead && !mark.IsSpectator)
                {
                    _baseScript.NoteBountyHunterMarkSighting(mark);
                    bInSight = EolBotSenseUtil.BotCanSeePlayer(_state, mark)
                        && Helpers.isInRange(c_playerMaxRangeEnemies, _state, mark._state);
                    return mark;
                }
                bInSight = false;
                return null;
            }

            InfServer.Game.Player callPrefer = null;
            if (_baseScript != null)
            {
                InfServer.Game.Player callP;
                Vehicle callV;
                if (_baseScript.TryGetCombatCallAssistTarget(_team, this, Environment.TickCount, out callP, out callV, false)
                    && callP != null && !callP.IsDead)
                    callPrefer = callP;
            }

            return EolBotSenseUtil.PickHostilePlayerDistanceFirst(
                _arena, _state, _team, _type, c_playerMaxRangeEnemies, _target, callPrefer,
                delegate (InfServer.Game.Player p)
                {
                    return PlayerNearRaiderObjectiveEngage(p);
                },
                useFireHeightLos: false,
                ref bInSight,
                this,
                _baseScript);
        }

        public override void applyExplosion(Player attacker, int dmgX, int dmgY, ItemInfo.Projectile wep)
        {
            if (_baseScript != null && _baseScript.TryRejectBountyHunterHit(this, attacker))
                return;
            base.applyExplosion(attacker, dmgX, dmgY, wep);
        }
    }
}
