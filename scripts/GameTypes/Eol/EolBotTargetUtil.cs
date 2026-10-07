using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>Closest hostile Eol bot vehicle in range and player-vs-bot target arbitration.</summary>
    internal static class EolBotTargetUtil
    {
        /// <summary>True if every tile along the segment is open (no <see cref="LvlInfo.Tile.Blocked"/>). Used before <c>shotFired</c> so bots do not shoot through walls.</summary>
        public static bool TileLineClearForWeaponFire(Arena arena, short fromX, short fromY, short toX, short toY)
        {
            if (arena == null)
                return false;
            return Helpers.calcBresenhemsPredicate(arena, fromX, fromY, toX, toY,
                delegate (LvlInfo.Tile t) { return !t.Blocked; });
        }

        const int HostileBotScanRange = Script_Eol.EolBotCombatSeekRangeRaw;
        const int MaxLosCandidates = 3;
        /// <summary>Matches bot Settings c_playerRangeFire — human in this range wins over hostile bots.</summary>
        const int PlayerPriorityWeaponRange = Script_Eol.EolBotCombatFireRangeRaw;

        static bool HostileBotEligible(Bot self, Script_Eol script, Vehicle v)
        {
            if (v == null || !(v is Bot) || v.IsDead)
                return false;
            if (!script.ShouldEolBotTeamsFight(self._team, v._team))
                return false;

            if (self is AlienHorde && script.AlienWaveActive)
            {
                if (!Helpers.isInRange(Script_Eol.EolBotSelfAggroRange, self._state, v._state))
                {
                    long adx = v._state.positionX - script.AlienHordeAnchorX;
                    long ady = v._state.positionY - script.AlienHordeAnchorY;
                    long r = Script_Eol.AlienHordeEngageRadiusFromCrash;
                    if (adx * adx + ady * ady > r * r)
                        return false;
                }
            }
            else if (!Helpers.isInRange(Script_Eol.EolBotSelfAggroRange, self._state, v._state))
                return false;
            return true;
        }

        static bool EvalBotLos(Bot self, Vehicle v)
        {
            return Helpers.calcBresenhemsPredicate(
                self._arena,
                self._state.positionX,
                self._state.positionY,
                v._state.positionX,
                v._state.positionY,
                delegate (LvlInfo.Tile t)
                {
                    short high = self._arena._server._assets.Level.PhysicsHigh[t.Physics];
                    if (self._type.FireHeight > high)
                        return true;
                    return !t.Blocked;
                });
        }

        public static Vehicle GetTargetEnemyBotVehicle(Bot self, Script_Eol script, ref bool bInSight)
        {
            bInSight = false;
            if (self == null || script == null || self._arena == null || self._team == null)
                return null;

            // Sticky: refresh LOS only.
            Vehicle sticky = EolBotSenseUtil.GetStickyHostileBot(self._id);
            if (sticky != null
                && Helpers.isInRange(HostileBotScanRange, self._state, sticky._state)
                && HostileBotEligible(self, script, sticky))
            {
                bInSight = EvalBotLos(self, sticky);
                return sticky;
            }

            Vehicle callPrefer = null;
            InfServer.Game.Player callP;
            Vehicle callV;
            if (script.TryGetCombatCallAssistTarget(self._team, self, Environment.TickCount, out callP, out callV, false)
                && callV != null && !callV.IsDead)
                callPrefer = callV;

            if (callPrefer != null && HostileBotEligible(self, script, callPrefer))
            {
                bInSight = EvalBotLos(self, callPrefer);
                EolBotSenseUtil.SetStickyHostileBot(self._id, callPrefer);
                return callPrefer;
            }

            List<Vehicle> bots = self._arena.getVehiclesInRange(
                self._state.positionX,
                self._state.positionY,
                HostileBotScanRange,
                delegate (Vehicle v) { return v is Bot; });

            double[] topDist = { double.MaxValue, double.MaxValue, double.MaxValue };
            Vehicle[] topV = new Vehicle[MaxLosCandidates];

            foreach (Vehicle v in bots)
            {
                if (!HostileBotEligible(self, script, v))
                    continue;

                double dist = Helpers.distanceSquaredTo(self._state, v._state);
                for (int k = 0; k < MaxLosCandidates; k++)
                {
                    if (dist >= topDist[k])
                        continue;
                    for (int m = MaxLosCandidates - 1; m > k; m--)
                    {
                        topDist[m] = topDist[m - 1];
                        topV[m] = topV[m - 1];
                    }
                    topDist[k] = dist;
                    topV[k] = v;
                    break;
                }
            }

            Vehicle best = null;
            double bestDist = double.MaxValue;
            bool bestLos = false;
            for (int k = 0; k < MaxLosCandidates; k++)
            {
                Vehicle v = topV[k];
                if (v == null)
                    break;
                bool los = EvalBotLos(self, v);
                double dist = topDist[k];
                if (best == null || (!bestLos && los) || (los == bestLos && dist < bestDist))
                {
                    best = v;
                    bestDist = dist;
                    bestLos = los;
                }
            }

            bInSight = bestLos;
            EolBotSenseUtil.SetStickyHostileBot(self._id, best);
            return best;
        }

        /// <param name="preferPlayerInWeaponRange">When true, any human inside weapon range beats a closer hostile bot.</param>
        /// <returns>false if neither target exists.</returns>
        public static bool PickPlayerOrVehicle(
            Bot self,
            InfServer.Game.Player p,
            Vehicle v,
            bool pClear,
            bool vClear,
            out bool pursuePlayer,
            bool preferPlayerInWeaponRange = false)
        {
            pursuePlayer = true;
            if (p == null && v == null)
                return false;
            if (preferPlayerInWeaponRange && p != null && Helpers.isInRange(PlayerPriorityWeaponRange, self._state, p._state))
            {
                pursuePlayer = true;
                return true;
            }
            if (v == null)
            {
                pursuePlayer = true;
                return true;
            }
            if (p == null)
            {
                pursuePlayer = false;
                return true;
            }
            if (pClear && !vClear)
            {
                pursuePlayer = true;
                return true;
            }
            if (!pClear && vClear)
            {
                pursuePlayer = false;
                return true;
            }
            double dp = Helpers.distanceSquaredTo(self._state, p._state);
            double dv = Helpers.distanceSquaredTo(self._state, v._state);
            pursuePlayer = dp <= dv;
            return true;
        }
    }
}
