using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;
using Bnoerj.AI.Steering;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// Shared cheap sensing helpers: distance-first player pick, sticky hostile bots,
    /// and TTL-cached separation peer lists.
    /// </summary>
    internal static class EolBotSenseUtil
    {
        public const int MaxLosCandidates = 3;
        public const int SepCacheTtlMs = 160;
        /// <summary>Cloaked players are visible to bots only within this raw distance.</summary>
        public const int CloakRevealRaw = 200;

        static readonly Dictionary<int, Vehicle> StickyHostileBot = new Dictionary<int, Vehicle>();
        static readonly Dictionary<int, SepCacheEntry> SepCaches = new Dictionary<int, SepCacheEntry>();

        sealed class SepCacheEntry
        {
            public int Tick;
            public int RangeKey;
            public readonly List<IVehicle> Peers = new List<IVehicle>(16);
        }

        /// <summary>Drop sticky / sep cache for a despawned bot.</summary>
        public static void ForgetBot(int botId)
        {
            StickyHostileBot.Remove(botId);
            SepCaches.Remove(botId);
        }

        public static void SetStickyHostileBot(int botId, Vehicle v)
        {
            if (botId == 0)
                return;
            if (v == null || v.IsDead)
                StickyHostileBot.Remove(botId);
            else
                StickyHostileBot[botId] = v;
        }

        public static Vehicle GetStickyHostileBot(int botId)
        {
            Vehicle v;
            if (!StickyHostileBot.TryGetValue(botId, out v))
                return null;
            if (v == null || v.IsDead)
            {
                StickyHostileBot.Remove(botId);
                return null;
            }
            return v;
        }

        /// <summary>
        /// Build or reuse a separation peer list for <paramref name="botId"/>.
        /// <paramref name="rangeKey"/> should encode radius (and optionally bot-type) so mismatched caches miss.
        /// </summary>
        public static List<IVehicle> GetSepPeers(
            int botId,
            int now,
            int rangeKey,
            int maxPeers,
            Func<List<IVehicle>> buildFresh)
        {
            SepCacheEntry entry;
            if (!SepCaches.TryGetValue(botId, out entry) || entry == null)
            {
                entry = new SepCacheEntry();
                SepCaches[botId] = entry;
            }

            if (entry.Tick != 0
                && now - entry.Tick < SepCacheTtlMs
                && entry.RangeKey == rangeKey
                && entry.Peers.Count > 0)
            {
                return entry.Peers;
            }

            entry.Peers.Clear();
            List<IVehicle> built = buildFresh != null ? buildFresh() : null;
            if (built != null)
            {
                int n = Math.Min(maxPeers, built.Count);
                for (int i = 0; i < n; i++)
                {
                    if (built[i] != null)
                        entry.Peers.Add(built[i]);
                }
            }
            entry.Tick = now;
            entry.RangeKey = rangeKey;
            return entry.Peers;
        }

        /// <summary>True when the player has an active utility with a cloak distance set.</summary>
        public static bool PlayerHasActiveCloak(InfServer.Game.Player p)
        {
            if (p?.activeUtilities == null)
                return false;
            for (int i = 0; i < p.activeUtilities.Count; i++)
            {
                ItemInfo.UtilityItem util = p.activeUtilities[i];
                if (util != null && util.cloakDistance != -1)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Cloaked players are hidden beyond <see cref="CloakRevealRaw"/>; within that range bots see them.
        /// </summary>
        public static bool BotCanSeePlayer(Helpers.ObjectState from, InfServer.Game.Player p)
        {
            if (from == null || p == null)
                return false;
            if (!PlayerHasActiveCloak(p))
                return true;
            return Helpers.isInRange(CloakRevealRaw, from, p._state);
        }

        /// <summary>True when a live hostile player is close enough that patrol/raid-march must stop.</summary>
        public static bool HostilePlayerWithin(
            Arena arena,
            Helpers.ObjectState from,
            Team team,
            int rangeRaw)
        {
            if (arena == null || from == null || team == null || rangeRaw <= 0)
                return false;
            foreach (InfServer.Game.Player p in arena.getPlayersInRange(from.positionX, from.positionY, rangeRaw))
            {
                if (p == null || p.IsDead || p._team == team)
                    continue;
                if (!BotCanSeePlayer(from, p))
                    continue;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Patrol, march, and roam idle should yield to combat when any hostile player is in range
        /// or this bot still has a live target in range.
        /// </summary>
        public static bool ShouldBreakIdleForCombat(
            Arena arena,
            Helpers.ObjectState from,
            Team team,
            int engageRangeRaw,
            InfServer.Game.Player activeTarget,
            Func<bool> extraHostileNearby = null)
        {
            if (extraHostileNearby != null && extraHostileNearby())
                return true;
            if (HostilePlayerWithin(arena, from, team, engageRangeRaw))
                return true;
            if (activeTarget != null && !activeTarget.IsDead && activeTarget._team != team
                && Helpers.isInRange(engageRangeRaw, from, activeTarget._state)
                && BotCanSeePlayer(from, activeTarget))
                return true;
            return false;
        }

        /// <summary>
        /// Distance-first hostile player pick: gather candidates without LOS, then Bresenham only
        /// the closest <see cref="MaxLosCandidates"/> (plus combat-call prefer).
        /// </summary>
        public static InfServer.Game.Player PickHostilePlayerDistanceFirst(
            Arena arena,
            Helpers.ObjectState from,
            Team team,
            VehInfo fireHeightType,
            int maxRange,
            InfServer.Game.Player sticky,
            InfServer.Game.Player callPrefer,
            Func<InfServer.Game.Player, bool> accept,
            bool useFireHeightLos,
            ref bool bInSight,
            Bot selfBot = null,
            Script_Eol engageCapScript = null)
        {
            bInSight = false;
            if (arena == null || from == null || team == null)
                return null;

            if (sticky != null && !sticky.IsDead && sticky._team != team
                && Helpers.isInRange(maxRange, from, sticky._state)
                && !arena.getTerrain(sticky._state.positionX, sticky._state.positionY).safety
                && BotCanSeePlayer(from, sticky)
                && (accept == null || accept(sticky)))
            {
                bInSight = EvalPlayerLos(arena, from, sticky._state, fireHeightType, useFireHeightLos);
                return sticky;
            }

            // (distSq, player) — keep a tiny top-K without sorting the whole list.
            double[] topDist = { double.MaxValue, double.MaxValue, double.MaxValue };
            InfServer.Game.Player[] topP = new InfServer.Game.Player[MaxLosCandidates];
            InfServer.Game.Player callHit = null;

            foreach (InfServer.Game.Player p in arena.getPlayersInRange(from.positionX, from.positionY, maxRange))
            {
                if (p == null || p.IsDead || p._team == team)
                    continue;
                if (arena.getTerrain(p._state.positionX, p._state.positionY).safety)
                    continue;
                if (!BotCanSeePlayer(from, p))
                    continue;
                if (accept != null && !accept(p))
                    continue;
                if (engageCapScript != null && selfBot != null && !engageCapScript.CanBotEngagePlayer(selfBot, p))
                    continue;

                if (callPrefer != null && p == callPrefer)
                {
                    callHit = p;
                    continue; // LOS separately
                }

                double dist = Helpers.distanceSquaredTo(from, p._state);
                // Insert into top-K
                for (int k = 0; k < MaxLosCandidates; k++)
                {
                    if (dist >= topDist[k])
                        continue;
                    for (int m = MaxLosCandidates - 1; m > k; m--)
                    {
                        topDist[m] = topDist[m - 1];
                        topP[m] = topP[m - 1];
                    }
                    topDist[k] = dist;
                    topP[k] = p;
                    break;
                }
            }

            if (callHit != null)
            {
                bInSight = EvalPlayerLos(arena, from, callHit._state, fireHeightType, useFireHeightLos);
                return callHit;
            }

            InfServer.Game.Player best = null;
            double bestDist = double.MaxValue;
            bool bestLos = false;

            for (int k = 0; k < MaxLosCandidates; k++)
            {
                InfServer.Game.Player p = topP[k];
                if (p == null)
                    break;
                bool los = EvalPlayerLos(arena, from, p._state, fireHeightType, useFireHeightLos);
                double dist = topDist[k];
                if (best == null || (!bestLos && los) || (los == bestLos && dist < bestDist))
                {
                    best = p;
                    bestDist = dist;
                    bestLos = los;
                }
            }

            bInSight = bestLos;
            return best;
        }

        static bool EvalPlayerLos(
            Arena arena,
            Helpers.ObjectState from,
            Helpers.ObjectState to,
            VehInfo fireHeightType,
            bool useFireHeightLos)
        {
            if (useFireHeightLos && fireHeightType != null)
            {
                return Helpers.calcBresenhemsPredicate(arena,
                    from.positionX, from.positionY, to.positionX, to.positionY,
                    delegate (LvlInfo.Tile t)
                    {
                        short high = arena._server._assets.Level.PhysicsHigh[t.Physics];
                        if (fireHeightType.FireHeight > high)
                            return true;
                        return !t.Blocked;
                    });
            }
            return Helpers.calcBresenhemsPredicate(arena,
                from.positionX, from.positionY, to.positionX, to.positionY,
                delegate (LvlInfo.Tile t) { return !t.Blocked; });
        }
    }
}
