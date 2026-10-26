using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// Bots with <see cref="Bot.bRespawn"/> linger as a corpse at the death site, then revive
    /// directly at their spawn anchor (live captain, HQ, or raid spaceship / PDB).
    /// </summary>
    public partial class Script_Eol
    {
        /// <summary>Corpse linger at the death site before revive at the spawn anchor.</summary>
        public const int BotCorpseLingerMs = 10000;
        /// <summary>Legacy alias — same as <see cref="BotCorpseLingerMs"/>.</summary>
        public const int BotDropshipRespawnDelayMs = BotCorpseLingerMs;
        /// <summary>
        /// Default deathmatch lives for bots that use anchor respawn.
        /// <c>0</c> = infinite; <c>N &gt; 0</c> = leave after N deaths.
        /// Prefer the faction constants below at spawn.
        /// </summary>
        public const int BotDeathmatchMaxDeaths = 0;
        /// <summary>Base / defense bots — unlimited lives.</summary>
        public const int BotMaxDeathsBase = 0;
        /// <summary>Pirate raiders — leave after this many deaths.</summary>
        public const int BotMaxDeathsRaider = 4;
        /// <summary>Alien horde — leave after this many deaths.</summary>
        public const int BotMaxDeathsAlien = 4;
        /// <summary>Pioneer Station (map search origin / legacy recall coords).</summary>
        public const short BotDropshipRecallX = 27008;
        public const short BotDropshipRecallY = 3360;
        public const int BotDropshipRecallWarpRadius = 300;

        struct BotAnchorRespawnState
        {
            public int TickRespawnAt;
            /// <summary>Hard cancel if still pending past this (anchor blocked / findOpenWarp fails).</summary>
            public int TickDeadline;
            public bool IsAlien;
        }

        readonly Dictionary<int, BotAnchorRespawnState> _botDropshipRespawnById = new Dictionary<int, BotAnchorRespawnState>();

        void ClearBotDropshipRespawnState()
        {
            _botDropshipRespawnById.Clear();
        }

        /// <summary>
        /// Opt into corpse linger + anchor revive, and set deathmatch lives.
        /// Pass <paramref name="maxDeaths"/> to override <see cref="BotDeathmatchMaxDeaths"/> for this bot.
        /// </summary>
        void EnableBotDropshipRespawn(Bots.Bot bot, int maxDeaths = -1)
        {
            if (bot == null)
                return;
            bot.bRespawn = true;
            bot.MaxDeaths = maxDeaths >= 0 ? maxDeaths : BotDeathmatchMaxDeaths;
        }

        /// <summary>True when this bot may linger and revive at its spawn anchor.</summary>
        bool CanBotUseDropshipRespawn(Bots.Bot bot)
        {
            if (bot == null || bot._team == null || !bot.bRespawn)
                return false;
            // Deathmatch limit reached — leave the game (no further revive).
            if (bot.HasExhaustedDeaths)
                return false;
            if (bot is BasicDefense || bot is Captain || bot is Engineer)
                return IsDefenseBotTeam(bot._team)
                    && EolBaseBotsEnabled
                    && TeamHasLiveHqVehicle(bot._team);
            if (bot is RoamingAttacker || bot is RoamingCaptain)
            {
                if (IsBountyHunterBotTeam(bot._team))
                    return BountyHunterSquadOperational;
                return IsRoamBotTeam(bot._team) && PirateRaidHordeOperational;
            }
            if (bot is AlienHorde)
                return IsAlienBotTeam(bot._team) && _alienWaveActive && AlienCrashShipAlive();
            return false;
        }

        bool AlienCrashShipAlive()
        {
            if (_arena == null || botTeam7 == null)
                return false;
            foreach (Vehicle v in _arena.Vehicles)
            {
                if (v == null || v.IsDead || v._type == null)
                    continue;
                if (v._type.Id == AlienCrashVehicleId && v._team == botTeam7)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Keep the corpse at the death site and schedule revive at the spawn anchor
        /// after <see cref="BotCorpseLingerMs"/>.
        /// </summary>
        public void BeginBotDropshipRespawn(Bots.Bot bot, int now)
        {
            if (bot == null || _arena == null)
                return;

            bot.bDropshipRespawnPending = true;
            BotAnchorRespawnState st;
            st.TickRespawnAt = now + BotCorpseLingerMs;
            // Extra grace for findOpenWarp retries, then cancel so slots are not held forever.
            st.TickDeadline = now + BotCorpseLingerMs + 8000;
            st.IsAlien = bot is AlienHorde;
            _botDropshipRespawnById[bot._id] = st;
            // No Pioneer warp — corpse stays where they died until the linger ends.
        }

        void PollBotDropshipRespawns(int now)
        {
            if (_botDropshipRespawnById.Count == 0)
                return;

            List<int> remove = null;
            List<int> keys = new List<int>(_botDropshipRespawnById.Keys);

            for (int i = 0; i < keys.Count; i++)
            {
                int botId = keys[i];
                BotAnchorRespawnState st;
                if (!_botDropshipRespawnById.TryGetValue(botId, out st))
                    continue;

                Bots.Bot bot = FindTrackedBotById(botId);
                if (bot == null || bot.bCondemned || !bot.bDropshipRespawnPending)
                {
                    // Orphan dict entry — free alien slot if this was an alien linger.
                    if (bot == null && st.IsAlien)
                        FreeAlienWaveSlot();
                    if (remove == null)
                        remove = new List<int>();
                    remove.Add(botId);
                    continue;
                }

                if (!CanBotUseDropshipRespawn(bot))
                {
                    FinalizeBotDropshipRespawnCancelled(bot);
                    if (remove == null)
                        remove = new List<int>();
                    remove.Add(botId);
                    continue;
                }

                if (now < st.TickRespawnAt)
                    continue;

                Helpers.ObjectState spawn = TryFindAnchorRespawnPoint(bot);
                if (spawn == null)
                    spawn = ForceAnchorRespawnFallback(bot);

                if (spawn == null || now >= st.TickDeadline)
                {
                    // Never leave corpses pending forever — that blocks captain/alien slots.
                    FinalizeBotDropshipRespawnCancelled(bot);
                    if (remove == null)
                        remove = new List<int>();
                    remove.Add(botId);
                    continue;
                }

                if (CompleteBotDropshipRespawn(bot, spawn))
                {
                    if (remove == null)
                        remove = new List<int>();
                    remove.Add(botId);
                }
            }

            if (remove == null)
                return;
            for (int i = 0; i < remove.Count; i++)
                _botDropshipRespawnById.Remove(remove[i]);
        }

        /// <summary>
        /// Spawn near the bot's live anchor: defense captain / HQ, raid spaceship / PDB, or alien crash ship.
        /// </summary>
        Helpers.ObjectState TryFindAnchorRespawnPoint(Bots.Bot bot)
        {
            if (bot == null || _arena == null)
                return null;

            // Bounty hunters revive in an open sector, never the Pioneer dropship or a raid pin.
            if (IsBountyHunterBotTeam(bot._team))
            {
                Helpers.ObjectState sector;
                if (TryFindBountyHunterSectorSpawn(out sector) && sector != null)
                    return sector;
                return null;
            }

            short anchorX;
            short anchorY;

            // Aliens revive at the crash-ship / wave anchor.
            if (bot is AlienHorde)
            {
                UpdateAlienHordeAnchorFromShip();
                anchorX = AlienHordeAnchorX;
                anchorY = AlienHordeAnchorY;
                if (anchorX == 0 && anchorY == 0)
                    return null;
                return findOpenWarpBot(_arena, anchorX, anchorY, 140, 36)
                    ?? findOpenWarpBot(_arena, anchorX, anchorY, 280, 56)
                    ?? new Helpers.ObjectState { positionX = anchorX, positionY = anchorY };
            }

            bool defense = bot is BasicDefense || bot is Captain || bot is Engineer || IsDefenseBotTeam(bot._team);

            // Captains revive at HQ (not beside their own corpse).
            if (bot is Captain)
            {
                Vehicle hq = FindHqVehicleForTeam(bot._team);
                if (hq == null || hq.IsDead)
                    return null;
                Helpers.ObjectState hqSpawn = FindDefenseCaptainSpawnNearHq(hq._state.positionX, hq._state.positionY);
                if (hqSpawn != null)
                    return hqSpawn;
                return findOpenWarpBot(_arena, hq._state.positionX, hq._state.positionY, 200, 36)
                    ?? findOpenWarpBot(_arena, hq._state.positionX, hq._state.positionY, 360, 56)
                    ?? new Helpers.ObjectState { positionX = hq._state.positionX, positionY = hq._state.positionY };
            }

            // Roaming captains revive at the raid spawn pad (sector entry), not the flag pin.
            if (bot is RoamingCaptain)
            {
                if (RaiderPdbAnchorValid)
                {
                    anchorX = RaiderPdbAnchorX;
                    anchorY = RaiderPdbAnchorY;
                }
                else if (RaiderCollectiveRoamValid)
                {
                    anchorX = RaiderCollectiveRoamX;
                    anchorY = RaiderCollectiveRoamY;
                }
                else
                    return null;

                return FindOpenRaiderSpawnWithPeerGap(anchorX, anchorY)
                    ?? new Helpers.ObjectState { positionX = anchorX, positionY = anchorY };
            }

            Bots.Bot captain = FindLiveCaptainForTeam(bot._team, defense);
            if (captain != null && !captain.IsDead)
            {
                // Grunts / other roamers: always revive on the captain.
                anchorX = captain._state.positionX;
                anchorY = captain._state.positionY;
                return FindOpenRaiderSpawnWithPeerGap(anchorX, anchorY)
                    ?? new Helpers.ObjectState { positionX = anchorX, positionY = anchorY };
            }
            else if (defense)
            {
                Vehicle hq = FindHqVehicleForTeam(bot._team);
                if (hq == null || hq.IsDead)
                    return null;
                anchorX = hq._state.positionX;
                anchorY = hq._state.positionY;
                Helpers.ObjectState hqSpawn = FindDefenseCaptainSpawnNearHq(anchorX, anchorY);
                if (hqSpawn != null)
                    return hqSpawn;
            }
            else if (IsRoamBotTeam(bot._team))
            {
                // No live captain — fall back to spawn pad / collective pin.
                if (RaiderPdbAnchorValid)
                {
                    anchorX = RaiderPdbAnchorX;
                    anchorY = RaiderPdbAnchorY;
                }
                else if (RaiderCollectiveRoamValid)
                {
                    anchorX = RaiderCollectiveRoamX;
                    anchorY = RaiderCollectiveRoamY;
                }
                else
                    return null;

                return FindOpenRaiderSpawnWithPeerGap(anchorX, anchorY)
                    ?? new Helpers.ObjectState { positionX = anchorX, positionY = anchorY };
            }
            else
                return null;

            Helpers.ObjectState open = findOpenWarpBot(_arena, anchorX, anchorY, 200, 36);
            if (open == null)
                open = findOpenWarpBot(_arena, anchorX, anchorY, 360, 56);
            if (open == null)
                open = findOpenWarpBot(_arena, anchorX, anchorY, 520, 72);
            if (open == null)
                open = new Helpers.ObjectState { positionX = anchorX, positionY = anchorY };
            return open;
        }

        /// <summary>Open tile near anchor with at least <see cref="RaiderBotPeerMinGapRaw"/> from other raiders.</summary>
        Helpers.ObjectState FindOpenRaiderSpawnWithPeerGap(short anchorX, short anchorY)
        {
            if (_arena == null)
                return null;

            int[] radii = { 80, 140, 220, 360, 520 };
            for (int r = 0; r < radii.Length; r++)
            {
                Helpers.ObjectState pt = findOpenWarpBot(_arena, anchorX, anchorY, radii[r], 28);
                if (pt == null)
                    continue;
                if (IsRaiderSpawnClearOfPeers(pt.positionX, pt.positionY, RaiderBotPeerMinGapRaw))
                    return pt;
            }

            // Last try: accept any open tile even if tight.
            return findOpenWarpBot(_arena, anchorX, anchorY, 360, 56);
        }

        bool IsRaiderSpawnClearOfPeers(short x, short y, int minGapRaw)
        {
            if (_bots == null || minGapRaw <= 0)
                return true;

            long gapSq = (long)minGapRaw * minGapRaw;
            foreach (Bots.Bot b in _bots)
            {
                if (b == null || b.IsDead || b._team != botTeam4)
                    continue;
                if (!(b is RoamingCaptain) && !(b is RoamingAttacker))
                    continue;
                long dx = (long)b._state.positionX - x;
                long dy = (long)b._state.positionY - y;
                if (dx * dx + dy * dy < gapSq)
                    return false;
            }
            return true;
        }

        /// <summary>Last-resort revive coords when open-warp search fails completely.</summary>
        Helpers.ObjectState ForceAnchorRespawnFallback(Bots.Bot bot)
        {
            if (bot == null)
                return null;

            if (bot is AlienHorde)
            {
                UpdateAlienHordeAnchorFromShip();
                if (AlienHordeAnchorX == 0 && AlienHordeAnchorY == 0)
                    return null;
                return new Helpers.ObjectState { positionX = AlienHordeAnchorX, positionY = AlienHordeAnchorY };
            }

            if (bot is Captain || bot is BasicDefense || bot is Engineer || IsDefenseBotTeam(bot._team))
            {
                Vehicle hq = FindHqVehicleForTeam(bot._team);
                if (hq == null || hq.IsDead)
                    return null;
                return new Helpers.ObjectState { positionX = hq._state.positionX, positionY = hq._state.positionY };
            }

            if (bot is RoamingCaptain || bot is RoamingAttacker || IsRoamBotTeam(bot._team))
            {
                Bots.Bot liveCap = FindLiveCaptainForTeam(bot._team, false);
                if (liveCap != null && !liveCap.IsDead && !(bot is RoamingCaptain))
                    return new Helpers.ObjectState
                    {
                        positionX = liveCap._state.positionX,
                        positionY = liveCap._state.positionY
                    };
                if (RaiderPdbAnchorValid)
                    return new Helpers.ObjectState { positionX = RaiderPdbAnchorX, positionY = RaiderPdbAnchorY };
                if (RaiderCollectiveRoamValid)
                    return new Helpers.ObjectState { positionX = RaiderCollectiveRoamX, positionY = RaiderCollectiveRoamY };
            }

            return null;
        }

        /// <summary>Legacy name — prefers live captain, else HQ / raid anchor.</summary>
        Helpers.ObjectState TryFindCaptainRespawnPoint(Bots.Bot bot)
        {
            return TryFindAnchorRespawnPoint(bot);
        }

        Bots.Bot FindLiveCaptainForTeam(Team team, bool defenseCaptain)
        {
            if (team == null || _bots == null)
                return null;

            foreach (Bots.Bot b in _bots)
            {
                if (b == null || b.IsDead || b._team != team)
                    continue;
                if (defenseCaptain && b is Captain)
                    return b;
                if (!defenseCaptain && b is RoamingCaptain)
                    return b;
            }

            if (defenseCaptain || _arena == null)
                return null;

            // Fallback: roam captain vehicle id scan if not in script _bots yet.
            List<Vehicle> caps = _arena.getVehiclesInRange(
                BotDropshipRecallX, BotDropshipRecallY, 32000,
                delegate (Vehicle v)
                {
                    return v != null && v._type != null
                        && v._type.Id == _roamCaptains
                        && v._team == team
                        && !v.IsDead
                        && v is Bots.Bot;
                });
            if (caps == null || caps.Count == 0)
                return null;
            return caps[0] as Bots.Bot;
        }

        bool CompleteBotDropshipRespawn(Bots.Bot bot, Helpers.ObjectState spawn)
        {
            if (bot == null || spawn == null || _arena == null)
                return false;

            bot.bDropshipRespawnPending = false;
            bot.bCondemned = false;
            bot.assignDefaultState();
            bot._tickDead = 0;
            bot._itemUseID = 0;

            try
            {
                bot.Navigation?.Clear();
                bot.HoldPosition();
            }
            catch { /* revive race */ }

            if (bot._movement != null)
                bot._movement.bEnabled = true;

            EolBotCombatUtil.ForgetBot(bot._id);
            EolBotSenseUtil.ForgetBot(bot._id);
            ClearBotActivePlayerTarget(bot);

            short deathX = bot._state.positionX;
            short deathY = bot._state.positionY;

            putVehicleAtSpawnLocation(bot, spawn);
            bot.update(false);

            // Re-list if death dropped them while the team was under max.
            _arena.EnsureBotOnNamelist(bot);

            // Death graphic removed the sprite. Forget awareness and re-send the hull
            // to anyone who saw the death or is at the revive point. Far clients pick
            // it up on the next in-range update during the reattach window.
            int reattachUntil = Environment.TickCount + 5000;
            if (reattachUntil == 0)
                reattachUntil = 1;
            bot.RequestHullReattach(reattachUntil);
            bot.ClearClientsAwareOfBot();

            List<Player> repair = _arena.GetBotLifecycleAudience(deathX, deathY);
            List<Player> atSpawn = _arena.GetBotLifecycleAudience(bot._state.positionX, bot._state.positionY);
            if (atSpawn != null)
            {
                if (repair == null)
                    repair = atSpawn;
                else
                {
                    for (int i = 0; i < atSpawn.Count; i++)
                    {
                        Player p = atSpawn[i];
                        if (p != null && !repair.Contains(p))
                            repair.Add(p);
                    }
                }
            }
            if (repair != null && repair.Count > 0)
                _arena.ReattachBotHull(bot, repair);

            return true;
        }

        void FinalizeBotDropshipRespawnCancelled(Bots.Bot bot)
        {
            if (bot == null)
                return;

            bool wasRaiderCaptain = bot is RoamingCaptain && !IsBountyHunterBotTeam(bot._team);

            bot.bDropshipRespawnPending = false;
            _botDropshipRespawnById.Remove(bot._id);

            FreeAlienWaveSlot(bot);

            if (_bots != null)
                _bots.Remove(bot);
            ForgetScriptBotBookkeeping(bot);
            _arena?.RemoveBotFromNamelist(bot);

            bot.bCondemned = true;
            try
            {
                bot.destroy(true);
            }
            catch { /* teardown race */ }

            if (wasRaiderCaptain)
                SyncRaiderCaptainCountFromOccupying();
        }
    }
}
