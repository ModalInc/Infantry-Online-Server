using System;
using System.Collections.Generic;
using System.Linq;

using InfServer.Game;
using InfServer.Protocol;
using InfServer.Bots;

using Assets;

namespace InfServer.Script.GameType_Eol
{
    public partial class AlienHorde : Bot
    {
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

        protected InfServer.Game.Player getTargetPlayer(ref bool bInSight)
        {
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
                null,
                useFireHeightLos: true,
                ref bInSight,
                this,
                _baseScript);
        }
    }
}
