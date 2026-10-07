using InfServer.Bots;
using InfServer.Game;
using InfServer.Game.Modules.BotActions;
using InfServer.Protocol;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// Eol-specific objective board (HQ anchors, raid collective point, arena flags).
    /// </summary>
    public sealed class EolObjectives : IArenaObjectives
    {
        readonly Arena _arena;
        readonly Script_Eol _script;
        readonly ArenaDefaultObjectives _fallback;

        public EolObjectives(Arena arena, Script_Eol script)
        {
            _arena = arena;
            _script = script;
            _fallback = new ArenaDefaultObjectives(_arena);
        }

        public bool TryGetEnemyFlag(Team forTeam, out Helpers.ObjectState pos)
            => _fallback.TryGetEnemyFlag(forTeam, out pos);

        public bool TryGetFriendlyFlag(Team forTeam, out Helpers.ObjectState pos)
            => _fallback.TryGetFriendlyFlag(forTeam, out pos);

        public bool TryGetFlagCarrier(Team enemyOf, out Player carrier)
            => _fallback.TryGetFlagCarrier(enemyOf, out carrier);

        public bool TryGetFriendlyFlagCarrier(Team forTeam, out Player carrier)
            => _fallback.TryGetFriendlyFlagCarrier(forTeam, out carrier);

        public bool TryGetHill(out Helpers.ObjectState pos, out Team owner)
            => _fallback.TryGetHill(out pos, out owner);

        public bool TryGetPayload(out Helpers.ObjectState pos)
            => _fallback.TryGetPayload(out pos);

        public bool TryGetBombSite(out Helpers.ObjectState pos)
            => _fallback.TryGetBombSite(out pos);

        public bool TryGetControlPoint(out Helpers.ObjectState pos, out Team owner)
            => _fallback.TryGetControlPoint(out pos, out owner);

        public bool TryGetPatrolAnchor(Bot bot, out Helpers.ObjectState pos)
        {
            pos = null;
            if (bot == null || _script == null)
                return _fallback.TryGetPatrolAnchor(bot, out pos);

            // Alien spacecraft (crash ship) — pack leash for AlienHorde.
            if (bot is AlienHorde alien && alien.vHq != null && !alien.vHq.IsDead && alien.vHq._state != null)
            {
                pos = alien.vHq._state;
                return true;
            }

            // Defense / Captain HQ.
            Vehicle hq = _script.FindHqVehicleForTeam(bot._team);
            if (hq != null && !hq.IsDead && hq._state != null)
            {
                pos = hq._state;
                return true;
            }

            // Pirate raid: PDB / sector spawn anchor (also used when captain is the pack lead).
            if (_script.RaiderPdbAnchorValid
                && (bot is RoamingAttacker || bot is RoamingCaptain))
            {
                pos = new Helpers.ObjectState
                {
                    positionX = _script.RaiderPdbAnchorX,
                    positionY = _script.RaiderPdbAnchorY
                };
                return true;
            }

            return _fallback.TryGetPatrolAnchor(bot, out pos);
        }

        public bool TryGetDefendAnchor(Bot bot, out Helpers.ObjectState pos)
            => TryGetPatrolAnchor(bot, out pos);

        public bool TryGetSafeZone(Bot bot, out Helpers.ObjectState pos)
            => TryGetPatrolAnchor(bot, out pos);

        public bool TryGetLaneWaypoint(Bot bot, out Helpers.ObjectState pos)
            => TryGetEnemyFlag(bot?._team, out pos) || TryGetPatrolAnchor(bot, out pos);

        public bool TryGetRaidObjective(Bot bot, out Helpers.ObjectState pos)
        {
            pos = null;
            if (_script == null)
                return false;

            short x, y;
            if (!_script.TryGetRaiderCollectiveRoamPoint(out x, out y))
                return false;

            pos = new Helpers.ObjectState { positionX = x, positionY = y };
            return true;
        }
    }
}
