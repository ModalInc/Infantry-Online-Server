using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Threading;

using InfServer.Network;
using InfServer.Protocol;
using InfServer.Logic;
using InfServer.Bots;

using Assets;

namespace InfServer.Game
{
    // Arena Class
    /// Represents a single arena in the server
    ///////////////////////////////////////////////////////
    public abstract partial class Arena : IChatTarget
    {	// Member variables
        ///////////////////////////////////////////////////
        protected ObjTracker<Bot> _bots;				//The vehicles belonging to the arena, indexed by id
        private List<Bot> _condemnedBots;				//Bots to be deleted
        /// <summary>
        /// Reused snapshot for <see cref="pollBots"/> so captain/grunt <c>newBot</c> adds
        /// during <c>bot.poll()</c> cannot invalidate the live tracker enumerator.
        /// </summary>
        private readonly List<Bot> _botPollScratch = new List<Bot>(256);
        public int _botsInArena;                        //How many bots currently in the arena

        /// <summary>Bot vehicle ids currently advertised on the team-list UI (authoritative membership).</summary>
        readonly HashSet<ushort> _botNamelistAdvertisedIds = new HashSet<ushort>();
        /// <summary>Per-client soft-mimic ids already sent (avoids re-Enter spam on unspec / refresh).</summary>
        readonly Dictionary<int, HashSet<ushort>> _botNamelistKnownByPlayerId = new Dictionary<int, HashSet<ushort>>();
        int _tickNextBotNamelistReconcile;
        const int BotNamelistReconcileIntervalMs = 2500;

        /// <summary>
        /// Deferred soft-mimic namelist sync after unspec — avoid burying SC_PlayerWarp
        /// under a Leave/Enter flood on the same tick.
        /// </summary>
        readonly Dictionary<Player, int> _pendingBotNamelistSync = new Dictionary<Player, int>();
        const int BotNamelistSyncDeferMs = 400;

        /// <summary>Staggered join-time soft-mimic roster so late joiners do not get a packet flood.</summary>
        sealed class BotNamelistJoinStream
        {
            public List<Bot> Bots;
            public int Index;
            public int TickNextBatch;
        }

        readonly Dictionary<Player, BotNamelistJoinStream> _botNamelistJoinStreams =
            new Dictionary<Player, BotNamelistJoinStream>();
        /// <summary>How many soft-mimic Enter+Vehicles to send per poll batch on join.</summary>
        const int BotNamelistJoinBatchSize = 6;
        /// <summary>Ms between join batches.</summary>
        const int BotNamelistJoinBatchIntervalMs = 40;
        /// <summary>Ms after SC_EnterArena before the first join batch.</summary>
        const int BotNamelistJoinStartDeferMs = 200;

        /// <summary>Active bots in this arena (for server modules).</summary>
        public IEnumerable<Bot> ActiveBots => _bots;

        /// <summary>
        /// Live bot count for zone/arena pop: excludes condemned hulls waiting for tracker cleanup.
        /// Dropship-pending corpses still count (they occupy a roster slot).
        /// Preferred over <see cref="_botsInArena"/> which can drift.
        /// </summary>
        public int BotCount
        {
            get
            {
                if (_bots == null)
                    return 0;
                int n = 0;
                foreach (Bot b in _bots)
                {
                    if (b == null || b.bCondemned)
                        continue;
                    n++;
                }
                return n;
            }
        }

        /// <summary>Force <see cref="_botsInArena"/> to match <see cref="BotCount"/>.</summary>
        void SyncBotsInArenaCount()
        {
            _botsInArena = BotCount;
        }

        /// <summary>
        /// Bots eligible for score charts (named / namelist-visible).
        /// </summary>
        public List<Bot> GetScoreChartBots()
        {
            List<Bot> result = new List<Bot>();
            if (_bots == null)
                return result;

            HashSet<string> seenAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Bot bot in _bots)
            {
                if (bot == null || string.IsNullOrEmpty(bot._alias))
                    continue;
                if (!ShouldBotAppearOnNamelist(bot))
                    continue;
                if (!seenAliases.Add(bot._alias))
                    continue;
                result.Add(bot);
            }
            return result;
        }

        /// <summary>Zero bot K/D/points for a new game (keeps last-game snapshot for charts).</summary>
        public void ClearBotGameStats()
        {
            if (_bots == null)
                return;
            foreach (Bot bot in _bots)
            {
                if (bot != null)
                    bot.ClearGameStats();
            }
        }

        ///////////////////////////////////////////////////
        // Member Functions
        ///////////////////////////////////////////////////
        /// <summary>
        /// Players/specs who may already see a bot (motion uses up to far radar).
        /// Spawn/death vehicle packets go only here — not arena-wide reliable.
        /// </summary>
        public List<Player> GetBotLifecycleAudience(int posX, int posY)
        {
            int range = routeRadarRangeFar > 0
                ? routeRadarRangeFar
                : (routeRadarRange > 0 ? routeRadarRange : routeRange);
            if (range <= 0)
                range = 1000;
            return getPlayersAndSpecInRange(posX, posY, range);
        }

        /// <summary>
        /// Takes care of all bots in the arena
        /// </summary>
        private void pollBots()
        {	//Handle each bot
            FlushBotPathCallbacks();
            PollDeferredBotNamelistSync();
            PollBotNamelistJoinStreams();

            // Snapshot into a reused buffer (AddRange → CopyTo). Avoids LINQ ToList() allocs
            // while remaining safe when bot.poll() spawns via newBot → _bots.Add.
            _botPollScratch.Clear();
            int botCount = _bots.Count;
            if (_botPollScratch.Capacity < botCount)
                _botPollScratch.Capacity = botCount;
            _botPollScratch.AddRange(_bots);

            foreach (Bot bot in _botPollScratch)
            {	//Does it need to send an update?
                if (bot.poll())
                {	//Prepare and send an update packet for the vehicle
                    bot._state.updateNumber++;

                    Helpers.Update_RouteBot(bot);

                    //Update the bot's state
                    if (!bot.bCondemned)
                    {
                        _vehicles.updateObjState(bot, bot._state);
                        _bots.updateObjState(bot, bot._state);
                    }

                    //Only shoot once!
                    bot._itemUseID = 0;
                }

                if (bot.bCondemned)
                    _condemnedBots.Add(bot);
            }

            foreach (Bot bot in _condemnedBots)
            {
                if (bot == null)
                    continue;
                // Always run destroy so client VehicleDestroy + vehicle tracker cleanup happen
                // even when a script only flipped bCondemned (Vehicle.destroy used to no-op).
                try { bot.destroy(true); }
                catch { /* teardown race */ }
                _bots.Remove(bot);
            }
            _condemnedBots.Clear();
            SyncBotsInArenaCount();

            // Keep aliases unique and membership aligned — avoid blasting leave/enter every tick
            // (clients can stack duplicate rows if PlayerLeave is ignored).
            if (BotNamelistEnabled)
            {
                int now = Environment.TickCount;
                if (_tickNextBotNamelistReconcile == 0 || now - _tickNextBotNamelistReconcile >= BotNamelistReconcileIntervalMs)
                {
                    _tickNextBotNamelistReconcile = now;
                    ReconcileBotNamelist(resyncAllClients: false);
                }
            }
        }

        /// <summary>
        /// Creates and adds a new vehicle to the arena
        /// </summary>
        public Bot newBot(Type botType, ushort type, Helpers.ObjectState state, params object[] args)
        {	//Redirect
            return newBot(botType, _server._assets.getVehicleByID(type), null, null, state, args);
        }

        /// <summary>
        /// Creates and adds a new vehicle to the arena
        /// </summary>
        public Bot newBot(Type botType, VehInfo type, Helpers.ObjectState state, params object[] args)
        {	//Redirect
            return newBot(botType, type, null, null, state, args);
        }

        /// <summary>
        /// Creates and adds a new vehicle to the arena
        /// </summary>
        public Bot newBot(Type botType, ushort type, Team team, Player owner, Helpers.ObjectState state, params object[] args)
        {	//Redirect
            return newBot(botType, _server._assets.getVehicleByID(type), team, owner, state, args);
        }


        /// <summary>
        /// Creates and adds a new bot vehicle to the arena
        /// </summary>
        public Bot newBot(Type botType, VehInfo type, Team team, Player owner, Helpers.ObjectState state, params object[] args)
        {	//Is this bot type compatible?
            if (!typeof(Bot).IsAssignableFrom(botType))
            {
                Log.write(TLog.Error, "Unable to infer bot type from type '{0}'.", botType);
                return null;
            }

            //Sanity checks
            VehInfo.Car carType = type as VehInfo.Car;
            if (carType == null)
            {
                Log.write(TLog.Error, "Bots can only be created using Car vehicles.");
                return null;
            }

            //Too many vehicles?
            if (_vehicles.Count == maxVehicles)
            {
                Log.write(TLog.Warning, "Vehicle list full.");
                return null;
            }

            // Soft player-mimic: client player-linked hulls need id < 5000 (player table)
            // / id < 5001 (vehicle create). IDs >= 5001 are pure vehicles and hop.
            // Share 1..4999 with players — take free slots from the high end so we
            // rarely collide with ZonePlayers (which allocates from low IDs up).
            const ushort BotMimicIdMin = 1;
            const ushort BotMimicIdMax = 4999; // inclusive; client player lookup is id < 5000

            if (_lastBotMimicKey < BotMimicIdMin || _lastBotMimicKey > BotMimicIdMax)
                _lastBotMimicKey = BotMimicIdMax;

            ushort vk = 0;
            bool found = false;
            int span = BotMimicIdMax - BotMimicIdMin + 1;
            // Walk downward from last key (wrap within range).
            ushort start = _lastBotMimicKey;
            for (int i = 0; i < span; ++i)
            {
                int id = start - i;
                while (id < BotMimicIdMin)
                    id += span;
                vk = (ushort)id;
                if (_vehicles.getObjByID(vk) != null)
                    continue;
                if (getPlayerById(vk) != null)
                    continue;
                found = true;
                break;
            }

            if (!found)
            {
                Log.write(TLog.Warning, "Player-mimic bot ID space full (1..{0}).", BotMimicIdMax);
                return null;
            }

            // Next search continues just below this id.
            if (vk <= BotMimicIdMin)
                _lastBotMimicKey = BotMimicIdMax;
            else
                _lastBotMimicKey = (ushort)(vk - 1);

            //Create our vehicle class		
            Bot newBot;
            Helpers.ObjectState newState = new Helpers.ObjectState();

            if (state != null)
            {
                newState.positionX = state.positionX;
                newState.positionY = state.positionY;
                newState.positionZ = state.positionZ;
                newState.yaw = state.yaw;

                if (args != null && args.Length > 0)
                {	//Put together a new argument list
                    object[] arguments = new object[3 + args.Length];

                    arguments[0] = carType;
                    arguments[1] = newState;
                    arguments[2] = this;

                    Array.Copy(args, 0, arguments, 3, args.Length);

                    newBot = (Bot)Activator.CreateInstance(botType, arguments);
                }
                else
                    newBot = (Bot)Activator.CreateInstance(botType, carType, newState, this);
            }
            else
            {
                if (args != null && args.Length > 0)
                {	//Put together a new argument list
                    object[] arguments = new object[2 + args.Length];

                    arguments[0] = carType;
                    arguments[1] = this;

                    Array.Copy(args, 0, arguments, 2, args.Length);

                    newBot = (Bot)Activator.CreateInstance(botType, arguments);
                }
                else
                    newBot = (Bot)Activator.CreateInstance(botType, carType, this);
            }

            if (newBot == null)
            {
                Log.write(TLog.Error, "Error while instancing bot type '{0}'", botType);
                return null;
            }

            newBot._arena = this;
            newBot._id = vk;

            newBot._team = team;
            newBot._creator = owner;
            newBot._tickCreation = Environment.TickCount;

            newBot.assignDefaultState();

            // botnames on: every bot type (alien, defense, raider, …) must have a team +
            // alias and appear on the team list. No type filter.
            if (BotNamelistEnabled)
            {
                if (newBot._team == null)
                    newBot._team = EnsureBotNamelistFallbackTeam();

                if (string.IsNullOrEmpty(newBot._alias) && BotNames != null)
                    newBot._alias = BotNames.Acquire(newBot, name => IsBotAliasTakenByAnother(name, newBot));

                if (string.IsNullOrEmpty(newBot._alias))
                    newBot._alias = "~Bot" + newBot._id;
            }
            else if (string.IsNullOrEmpty(newBot._alias) && BotNames != null)
            {
                newBot._alias = BotNames.Acquire(newBot, name => IsBotAliasTakenByAnother(name, newBot));
            }

            // Soft-mimic needs a PlayerEnter alias so the client builds an inhabited hull.
            if (string.IsNullOrEmpty(newBot._alias))
                newBot._alias = "~Bot" + newBot._id;

            //This uses the new ID automatically
            _vehicles.Add(newBot);
            _bots.Add(newBot);
            SyncBotsInArenaCount();

            // Repair any accidental same-alias collisions among live bots.
            EnsureUniqueBotAliases();

            // Soft player-mimic: PlayerEnter first (creates inhabited hull on client),
            // then Vehicles with inhabitant == bot id (same as human base vehicle).
            List<Player> spawnAudience = GetBotLifecycleAudience(
                newBot._state.positionX, newBot._state.positionY);

            // Soft-mimic: always arena-wide PlayerEnter (same packet as team list). Nearby-only
            // enter left far clients with Vehicles / Leave / death and no inhabited hull — client crash.
            broadcastBotNamelistEntry(newBot);

            if (spawnAudience != null && spawnAudience.Count > 0)
                ReattachBotHull(newBot, spawnAudience);

            return newBot;
        }

        /// <summary>
        /// Fallback public team for namelist bots spawned without a script team.
        /// </summary>
        Team EnsureBotNamelistFallbackTeam()
        {
            const string name = "Bot - Unassigned";
            if (_teams != null && _teams.TryGetValue(name.ToLowerInvariant(), out Team existing) && existing != null)
                return existing;

            Team t = new Team(this, _server);
            t._name = name;
            t._id = (short)(_teams != null ? _teams.Count : 0);
            t._password = "jojotheClown";
            t._owner = null;
            t._isPrivate = false;
            createTeam(t);
            return t;
        }

        /// <summary>
        /// Bots that still occupy a roster slot: alive, or waiting on dropship respawn.
        /// Exhausted deathmatch lives never stay on the team list.
        /// </summary>
        public static bool ShouldBotAppearOnNamelist(Bot bot)
        {
            if (bot == null || bot.bCondemned)
                return false;
            // Final life spent — name must leave even if a corpse still routes briefly.
            if (bot.HasExhaustedDeaths)
                return false;
            if (!bot.IsDead)
                return true;
            return bot.bDropshipRespawnPending;
        }

        /// <summary>True if another roster bot already uses this display alias.</summary>
        bool IsBotAliasTakenByAnother(string name, Bot except)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            foreach (Bot bot in _bots)
            {
                if (bot == null || bot == except || string.IsNullOrEmpty(bot._alias))
                    continue;
                if (!ShouldBotAppearOnNamelist(bot))
                    continue;
                if (string.Equals(bot._alias, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Force every roster bot to a unique alias so the team list never shows the same name twice.
        /// </summary>
        void EnsureUniqueBotAliases()
        {
            if (BotNames == null)
                return;

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<Bot> collisions = null;

            foreach (Bot bot in _bots)
            {
                if (bot == null || !ShouldBotAppearOnNamelist(bot))
                    continue;
                if (string.IsNullOrEmpty(bot._alias))
                {
                    bot._alias = BotNames.Acquire(bot, name => IsBotAliasTakenByAnother(name, bot));
                    if (string.IsNullOrEmpty(bot._alias))
                        continue;
                }

                if (seen.Add(bot._alias))
                    continue;

                if (collisions == null)
                    collisions = new List<Bot>();
                collisions.Add(bot);
            }

            if (collisions == null)
                return;

            foreach (Bot bot in collisions)
            {
                string oldAlias = bot._alias;
                BotNames.Release(bot);
                string next = BotNames.Acquire(bot, name => IsBotAliasTakenByAnother(name, bot) || seen.Contains(name));
                if (string.IsNullOrEmpty(next))
                    next = "~Bot" + bot._id;
                bot._alias = next;
                seen.Add(bot._alias);

                broadcastBotNamelistLeave(bot);
                broadcastBotNamelistEntry(bot);

                Log.write(TLog.Warning,
                    "Bot alias collision repaired: '{0}' -> '{1}' (botId {2})",
                    oldAlias, bot._alias, bot._id);
            }
        }

        /// <summary>Roster bots for the team list - one entry per unique alias and id.</summary>
        List<Bot> BuildUniqueNamelistBots()
        {
            EnsureUniqueBotAliases();

            List<Bot> named = new List<Bot>();
            HashSet<string> aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<ushort> ids = new HashSet<ushort>();

            foreach (Bot bot in _bots)
            {
                if (!ShouldBotAppearOnNamelist(bot))
                    continue;
                if (string.IsNullOrEmpty(bot._alias) && BotNames != null)
                    bot._alias = BotNames.Acquire(bot, name => IsBotAliasTakenByAnother(name, bot));
                if (string.IsNullOrEmpty(bot._alias))
                    continue;
                if (!ids.Add(bot._id))
                    continue;
                if (!aliases.Add(bot._alias))
                    continue;
                named.Add(bot);
            }
            return named;
        }

        /// <summary>
        /// Align aliases + advertised membership. Optionally push one shared snapshot to all clients.
        /// </summary>
        public void ReconcileBotNamelist(bool resyncAllClients)
        {
            // Soft-mimic enter/leave must stay consistent even if the XML flag is off.
            if (BotNames == null)
                return;

            List<Bot> shouldList = BuildUniqueNamelistBots();
            HashSet<ushort> shouldIds = new HashSet<ushort>();
            foreach (Bot bot in shouldList)
                shouldIds.Add(bot._id);

            if (_botNamelistAdvertisedIds.Count > 0)
            {
                List<ushort> stale = null;
                foreach (ushort id in _botNamelistAdvertisedIds)
                {
                    if (shouldIds.Contains(id))
                        continue;
                    if (stale == null)
                        stale = new List<ushort>();
                    stale.Add(id);
                }
                if (stale != null)
                {
                    foreach (ushort id in stale)
                    {
                        Bot live = null;
                        foreach (Bot b in _bots)
                        {
                            if (b != null && b._id == id)
                            {
                                live = b;
                                break;
                            }
                        }
                        if (live != null && !ShouldBotAppearOnNamelist(live))
                            RemoveBotFromNamelist(live);
                        else
                        {
                            broadcastBotNamelistLeaveById(id);
                            _botNamelistAdvertisedIds.Remove(id);
                        }
                    }
                }
            }

            foreach (Bot bot in shouldList)
            {
                if (!_botNamelistAdvertisedIds.Contains(bot._id))
                    broadcastBotNamelistEntry(bot);
            }

            foreach (Bot bot in _bots)
            {
                if (bot == null || ShouldBotAppearOnNamelist(bot))
                    continue;
                if (!string.IsNullOrEmpty(bot._alias))
                    RemoveBotFromNamelist(bot);
            }

            if (resyncAllClients)
                ResyncBotNamelistToAllPlayers();
        }

        /// <summary>One leave-enter snapshot of unique names to every connected client.</summary>
        public void ResyncBotNamelistToAllPlayers()
        {
            if (BotNames == null)
                return;

            List<Bot> named = BuildUniqueNamelistBots();

            HashSet<ushort> clearIds = new HashSet<ushort>(_botNamelistAdvertisedIds);
            foreach (Bot bot in _bots)
            {
                if (bot != null)
                    clearIds.Add(bot._id);
            }

            foreach (ushort id in clearIds)
                broadcastBotNamelistLeaveById(id);

            _botNamelistAdvertisedIds.Clear();

            if (named.Count == 0)
                return;

            // One bot per packet per client — avoids stock client id↔name corruption.
            foreach (Player p in Players)
            {
                if (p?._client == null)
                    continue;
                foreach (Bot bot in named)
                    SendBotNamelistEntryTo(p, bot);
            }

            foreach (Bot bot in named)
                _botNamelistAdvertisedIds.Add(bot._id);
        }

        /// <summary>True when this client was already sent a PlayerEnter for the bot.</summary>
        public bool IsBotNamelistKnownBy(Player player, ushort botId)
        {
            if (player == null || botId == 0)
                return false;
            HashSet<ushort> known;
            if (!_botNamelistKnownByPlayerId.TryGetValue(player._id, out known) || known == null)
                return false;
            return known.Contains(botId);
        }

        /// <summary>
        /// Re-send the inhabited vehicle create. Does not Leave/Enter.
        /// Marks the client aware only when the reliable was actually queued.
        /// </summary>
        public void ReattachBotHull(Bot bot, IEnumerable<Player> audience)
        {
            if (bot == null || audience == null)
                return;

            foreach (Player player in audience)
            {
                if (player?._client == null || player._arena != this)
                    continue;

                SC_Vehicles vehicles = new SC_Vehicles();
                vehicles.singleUpdate = bot;
                if (player._client.sendReliable(vehicles))
                    bot.MarkClientAwareOfBot(player);
            }
        }

        /// <summary>True when this bot id is currently on the team-list namelist.</summary>
        public bool IsBotNamelistAdvertised(ushort botId)
        {
            // Track soft-mimic PlayerEnter even when the botnames XML flag is off.
            return botId != 0 && _botNamelistAdvertisedIds.Contains(botId);
        }

        /// <summary>
        /// Sends a bot team-list entry to all connected clients once (no re-enter if already listed).
        /// </summary>
        void broadcastBotNamelistEntry(Bot bot)
        {
            if (bot == null)
                return;
            if (string.IsNullOrEmpty(bot._alias))
                bot._alias = "Bot " + bot._id;

            if (_botNamelistAdvertisedIds.Contains(bot._id))
                return;

            broadcastBotNamelistLeaveWireId(bot._id);

            SC_PlayerEnter packet = new SC_PlayerEnter();
            packet.bots = new[] { bot };

            foreach (Player p in Players)
            {
                if (p?._client == null)
                    continue;
                p._client.sendReliable(packet);
                HashSet<ushort> known;
                if (!_botNamelistKnownByPlayerId.TryGetValue(p._id, out known) || known == null)
                {
                    known = new HashSet<ushort>();
                    _botNamelistKnownByPlayerId[p._id] = known;
                }
                known.Add(bot._id);
            }

            _botNamelistAdvertisedIds.Add(bot._id);
        }

        void broadcastBotNamelistLeave(Bot bot)
        {
            if (bot == null)
                return;
            broadcastBotNamelistLeaveWireId(bot._id);
            _botNamelistAdvertisedIds.Remove(bot._id);
        }

        void broadcastBotNamelistLeaveById(ushort botVehicleId)
        {
            broadcastBotNamelistLeaveWireId(botVehicleId);
            _botNamelistAdvertisedIds.Remove(botVehicleId);
        }

        void broadcastBotNamelistLeaveWireId(ushort wireId)
        {
            if (wireId == 0)
                return;

            SC_PlayerLeave leavePacket = new SC_PlayerLeave();
            leavePacket.playerID = wireId;

            foreach (Player p in Players)
            {
                if (p?._client != null)
                    p._client.sendReliable(leavePacket);
            }
        }

        /// <summary>
        /// Queue soft-mimic namelist sync for a player after a short delay so exit-spectator
        /// warp is not buried under Leave/Enter traffic on the same tick.
        /// </summary>
        public void QueueBotNamelistSync(Player player)
        {
            if (player?._client == null || BotNames == null)
                return;
            // Join stream already owns this client — don't pile a full dump on top.
            if (_botNamelistJoinStreams.ContainsKey(player))
                return;
            int due = Environment.TickCount;
            if (due == 0) due = 1;
            due += BotNamelistSyncDeferMs;
            _pendingBotNamelistSync[player] = due;
        }

        /// <summary>
        /// After SC_EnterArena: stream soft-mimic PlayerEnter + Vehicles in small batches.
        /// Prevents client crashes when joining a persistent arena with many bots.
        /// </summary>
        public void QueueBotNamelistJoinStream(Player player)
        {
            if (player?._client == null || BotNames == null)
                return;

            _pendingBotNamelistSync.Remove(player);

            List<Bot> named = BuildUniqueNamelistBots();
            if (named == null || named.Count == 0)
                return;

            int due = Environment.TickCount;
            if (due == 0) due = 1;
            due += BotNamelistJoinStartDeferMs;

            _botNamelistJoinStreams[player] = new BotNamelistJoinStream
            {
                Bots = named,
                Index = 0,
                TickNextBatch = due,
            };
        }

        void PollBotNamelistJoinStreams()
        {
            if (_botNamelistJoinStreams.Count == 0)
                return;

            int now = Environment.TickCount;
            List<Player> finished = null;
            foreach (KeyValuePair<Player, BotNamelistJoinStream> kv in _botNamelistJoinStreams)
            {
                Player player = kv.Key;
                BotNamelistJoinStream stream = kv.Value;
                if (player == null || player._client == null || player._arena != this || stream == null)
                {
                    if (finished == null) finished = new List<Player>();
                    finished.Add(player);
                    continue;
                }
                if ((now - stream.TickNextBatch) < 0)
                    continue;

                int sent = 0;
                while (stream.Index < stream.Bots.Count && sent < BotNamelistJoinBatchSize)
                {
                    Bot bot = stream.Bots[stream.Index++];
                    if (bot == null || !ShouldBotAppearOnNamelist(bot))
                        continue;

                    SendBotNamelistEntryTo(player, bot, leaveFirst: false);
                    SC_Vehicles vehicles = new SC_Vehicles();
                    vehicles.singleUpdate = bot;
                    if (player._client.sendReliable(vehicles))
                        bot.MarkClientAwareOfBot(player);
                    sent++;
                }

                if (stream.Index >= stream.Bots.Count)
                {
                    if (finished == null) finished = new List<Player>();
                    finished.Add(player);
                }
                else
                {
                    stream.TickNextBatch = now + BotNamelistJoinBatchIntervalMs;
                    if (stream.TickNextBatch == 0)
                        stream.TickNextBatch = 1;
                }
            }

            if (finished == null)
                return;
            for (int i = 0; i < finished.Count; i++)
                _botNamelistJoinStreams.Remove(finished[i]);
        }

        void PollDeferredBotNamelistSync()
        {
            if (_pendingBotNamelistSync.Count == 0)
                return;

            int now = Environment.TickCount;
            List<Player> due = null;
            foreach (KeyValuePair<Player, int> kv in _pendingBotNamelistSync)
            {
                if (kv.Key == null || kv.Key._client == null || kv.Key._arena != this)
                {
                    if (due == null) due = new List<Player>();
                    due.Add(kv.Key);
                    continue;
                }
                // Not yet due (signed tick delta).
                if ((now - kv.Value) < 0)
                    continue;
                if (due == null) due = new List<Player>();
                due.Add(kv.Key);
            }

            if (due == null)
                return;

            for (int i = 0; i < due.Count; i++)
            {
                Player p = due[i];
                _pendingBotNamelistSync.Remove(p);
                if (p != null && p._client != null && p._arena == this
                    && !_botNamelistJoinStreams.ContainsKey(p))
                    syncBotNamelistTo(p);
            }
        }

        /// <summary>
        /// Sends the unique roster namelist to a single player (unspec refresh).
        /// Only enters bots this client does not already know — avoids re-Enter spam.
        /// </summary>
        public void syncBotNamelistTo(Player player, bool asInitialRoster = false)
        {
            // Soft-mimic needs PlayerEnter on join even when the botnames XML flag is off.
            if (player?._client == null || BotNames == null)
                return;

            // Cancel any pending deferred sync — we are doing it now.
            _pendingBotNamelistSync.Remove(player);
            // Prefer the join stream if one is already running.
            if (_botNamelistJoinStreams.ContainsKey(player))
                return;

            List<Bot> named = BuildUniqueNamelistBots();
            HashSet<ushort> known;
            if (!_botNamelistKnownByPlayerId.TryGetValue(player._id, out known) || known == null)
            {
                known = new HashSet<ushort>();
                _botNamelistKnownByPlayerId[player._id] = known;
            }

            // Drop stale ids this client still thinks are on the list.
            List<ushort> stale = null;
            foreach (ushort id in known)
            {
                bool stillNamed = false;
                for (int i = 0; i < named.Count; i++)
                {
                    if (named[i] != null && named[i]._id == id)
                    {
                        stillNamed = true;
                        break;
                    }
                }
                if (!stillNamed)
                {
                    if (stale == null) stale = new List<ushort>();
                    stale.Add(id);
                }
            }
            if (stale != null)
            {
                for (int i = 0; i < stale.Count; i++)
                {
                    ushort id = stale[i];
                    SC_PlayerLeave leavePacket = new SC_PlayerLeave();
                    leavePacket.playerID = id;
                    player._client.sendReliable(leavePacket);
                    known.Remove(id);
                }
            }

            if (named.Count == 0)
                return;

            // If this looks like a full first-time sync (nothing known yet), stream instead of dumping.
            if (known.Count == 0 && named.Count > BotNamelistJoinBatchSize)
            {
                QueueBotNamelistJoinStream(player);
                return;
            }

            foreach (Bot bot in named)
            {
                if (bot == null)
                    continue;

                bool needEnter = asInitialRoster || !known.Contains(bot._id);
                if (needEnter)
                {
                    SendBotNamelistEntryTo(player, bot, leaveFirst: known.Contains(bot._id));
                    known.Add(bot._id);
                }

                SC_Vehicles vehicles = new SC_Vehicles();
                vehicles.singleUpdate = bot;
                if (player._client.sendReliable(vehicles))
                    bot.MarkClientAwareOfBot(player);
                _botNamelistAdvertisedIds.Add(bot._id);
            }
        }

        /// <summary>PlayerLeave for one bot to one client (does not touch global advertised set).</summary>
        public void SendBotNamelistLeaveTo(Player player, Bot bot)
        {
            if (player?._client == null || bot == null)
                return;
            SC_PlayerLeave leavePacket = new SC_PlayerLeave();
            leavePacket.playerID = bot._id;
            player._client.sendReliable(leavePacket);
        }

        /// <summary>Single-bot PlayerEnter to one client.</summary>
        /// <param name="leaveFirst">When true, send Leave before Enter (refresh / dedupe). Fresh join streams skip leave.</param>
        public void SendBotNamelistEntryTo(Player player, Bot bot, bool leaveFirst = true)
        {
            if (player?._client == null || bot == null)
                return;
            if (string.IsNullOrEmpty(bot._alias))
                bot._alias = "Bot " + bot._id;

            if (leaveFirst)
                SendBotNamelistLeaveTo(player, bot);

            SC_PlayerEnter enterPacket = new SC_PlayerEnter();
            enterPacket.bots = new[] { bot };
            player._client.sendReliable(enterPacket);
            _botNamelistAdvertisedIds.Add(bot._id);
            HashSet<ushort> known;
            if (!_botNamelistKnownByPlayerId.TryGetValue(player._id, out known) || known == null)
            {
                known = new HashSet<ushort>();
                _botNamelistKnownByPlayerId[player._id] = known;
            }
            known.Add(bot._id);
        }

        /// <summary>
        /// Re-push one bot's namelist row to a single client so id↔alias matches the server.
        /// </summary>
        public void RefreshBotNamelistEntryFor(Player player, Bot bot)
        {
            SendBotNamelistEntryTo(player, bot);
        }

        /// <summary>Debug string of advertised bot vehicleId=alias.</summary>
        public string FormatBotNamelistRoster()
        {
            if (_botNamelistAdvertisedIds.Count == 0)
                return "(none)";

            StringBuilder sb = new StringBuilder();
            bool first = true;
            foreach (ushort id in _botNamelistAdvertisedIds.OrderBy(x => x))
            {
                Bot bot = _bots.getObjByID(id);
                if (bot == null)
                    bot = _vehicles.getObjByID(id) as Bot;

                if (!first)
                    sb.Append(", ");
                first = false;
                if (bot != null)
                {
                    sb.Append(bot._id);
                    sb.Append('=');
                    sb.Append(bot._alias ?? "?");
                }
                else
                {
                    sb.Append(id);
                    sb.Append("=missing");
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Removes a bot from the team-list UI and frees its name. Safe to call more than once.
        /// Always sends SC_PlayerLeave so player-style motion entities are cleared even when
        /// botnames UI is off.
        /// </summary>
        public void RemoveBotFromNamelist(Bot bot)
        {
            if (bot == null)
                return;

            // Player-style pose uses bot._id as a player entity; always leave it.
            broadcastBotNamelistLeaveWireId(bot._id);
            _botNamelistAdvertisedIds.Remove(bot._id);

            // Free the ~Bot pool slot, but never leave _alias empty — late killfeed / enter
            // paths crash clients on blank PlayerEnter names.
            if (!string.IsNullOrEmpty(bot._alias))
                BotNames?.Release(bot);
            bot._alias = "Bot " + bot._id;
        }

        /// <summary>
        /// Ensure a roster bot has a unique alias and appears on the team list once.
        /// All bot types are eligible when botnames is on — no alien/human filter.
        /// </summary>
        public void EnsureBotOnNamelist(Bot bot)
        {
            if (bot == null || BotNames == null || !ShouldBotAppearOnNamelist(bot))
                return;

            if (bot._team == null)
                bot._team = EnsureBotNamelistFallbackTeam();

            EnsureUniqueBotAliases();

            if (string.IsNullOrEmpty(bot._alias))
                bot._alias = BotNames.Acquire(bot, name => IsBotAliasTakenByAnother(name, bot));
            if (string.IsNullOrEmpty(bot._alias))
                bot._alias = "~Bot" + bot._id;

            broadcastBotNamelistEntry(bot);
        }

        /// <summary>
        /// Handles the loss of a bot
        /// </summary>
        public void lostBot(Bot bot)
        {	//Mark it for deletion and drop from the live tracker immediately
            // so zone pop (BotCount) cannot accumulate orphans until the next poll.
            if (bot == null)
                return;

            RemoveBotFromNamelist(bot);
            ForgetBotDamagerBookkeeping(bot);
            bot.bCondemned = true;
            if (_bots != null)
                _bots.Remove(bot);
            SyncBotsInArenaCount();
        }

        /// <summary>
        /// Determines whether there is a bot antiwarping the player in the specified area
        /// </summary>
        public Bot checkBotAntiwarp(Player player)
        {	//Get the list of bots in the area
            IEnumerable<Bot> candidates = _bots.getObjsInRange(player._state.positionX, player._state.positionY, 1000);

            foreach (Bot candidate in candidates)
            {	//Any anti-warp utils?
                // Snapshot under the same lock SyncActiveEquipBroadcast uses.
                List<ItemInfo.UtilityItem> equip;
                lock (candidate._activeEquip)
                    equip = new List<ItemInfo.UtilityItem>(candidate._activeEquip);

                if (!equip.Any(util => util.antiWarpDistance != -1))
                    continue;

                //Ignore your own team
                if (candidate._team == player._team)
                    continue;

                //Is it within the distance?
                int dist = (int)(player._state.position().Distance(candidate._state.position()) * 100);

                if (equip.Any(util => util.antiWarpDistance >= dist))
                    return candidate;
            }

            return null;
        }
    }
}