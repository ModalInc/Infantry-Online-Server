using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-17, owner) team-chat build orders for a squad's Chief Engineer:
    ///   "mg here", "plasma here", "rocket ap here", "rocket here", "sentry here", "hq here"
    /// The engineer runs to the spot the player stood on when giving the order, works there 3 s and places that
    /// turret / HQ for the team - the player's own build, as if they had used the kit (its creator, its kills).
    /// The team's computer limits are checked exactly as for a player's kit (per player, team total, per type and the
    /// two area limits), when ordered and again when placing. One build order every 30 seconds per player.
    /// </summary>
    public partial class Script_Eol
    {
        const int OrderBuildBase = 100;
        /// <summary>The words, and the build item each one stands for (its VehicleMaker gives the vehicle).</summary>
        static readonly string[] BuildWords = { "mg", "plasma", "rocket ap", "rocket", "sentry", "hq" };
        static readonly int[] BuildItemIds = { 10, 9, 1704, 11, 23, 183 };
        static readonly string[] BuildTitles = { "MG turret", "Plasma turret", "Rocket AP turret", "Rocket turret", "Sentry", "HQ" };
        const int BuildCooldownMs = 30 * 1000;   // (2026-11-17, owner: 30 s, was 2 min)
        const int BuildWorkMs = 3000;
        const int BuildTravelTimeoutMs = 60000;
        const int BuildArriveRadius = 48;

        class BuildJob
        {
            public InfServer.Game.Player Player;
            public Team Team;
            public int Kind;
            public short X, Y;
            public int Started, ArrivedAt;
            public Vehicle Target;   // a dismantle job: the turret / HQ to take down (null = a build)
        }

        /// <summary>A dismantle order: the nearest team computer of that kind to the player, taken down by the engineer.</summary>
        void HandleRemoveOrder(InfServer.Game.Player player, int kind)
        {
            Team team = player._team;
            int now = Environment.TickCount, next;
            if (_removeNext.TryGetValue(player, out next) && now - next < 0)
            {
                player.sendMessage(-1, String.Format("Squad: wait {0} s.", (next - now + 999) / 1000));
                return;
            }
            VehInfo veh = BuildVehicle(kind);
            if (veh == null)
            {
                player.sendMessage(-1, "Squad: there's no such thing to dismantle here.");
                return;
            }
            Vehicle target = null;
            foreach (Vehicle v in _arena.getVehiclesInRange(player._state.positionX, player._state.positionY, RemoveSearchRadius))
            {
                if (!(v is Computer) || v.IsDead || v.bCondemned || v._team != team || v._type == null || v._type.Id != veh.Id)
                    continue;
                bool taken = false;
                foreach (BuildJob j in _buildJobs.Values)
                    if (j.Target == v) { taken = true; break; }
                if (taken)
                    continue;
                if (target == null || Helpers.distanceTo(v._state, player._state) < Helpers.distanceTo(target._state, player._state))
                    target = v;
            }
            if (target == null)
            {
                player.sendMessage(-1, String.Format("Squad: no team {0} near you to dismantle.", BuildTitles[kind]));
                return;
            }
            Bot eng = null;
            foreach (RoamingAttacker b in EliteSquadMembers(team))
                if (IsChiefEngineerBot(b) && !b.IsDead && !b.bCondemned && !_buildJobs.ContainsKey(b))
                {
                    if (eng == null || Helpers.distanceTo(b._state, target._state) < Helpers.distanceTo(eng._state, target._state))
                        eng = b;
                }
            if (eng == null)
            {
                player.sendMessage(-1, "Squad: no engineer free right now.");
                return;
            }
            _buildJobs[eng] = new BuildJob { Player = player, Team = team, Kind = kind, X = target._state.positionX, Y = target._state.positionY, Started = now, Target = target };
            _removeNext[player] = now + RemoveCooldownMs;
            player.sendMessage(0, String.Format("&Squad: {0} is on the way to dismantle the {1}.",
                InfServer.Game.Modules.BotNameModule.GetDisplayName(eng), BuildTitles[kind]));
            SquadSay(eng, Pick3("taking it down", "on it", "dismantling"));
        }
        readonly Dictionary<Bot, BuildJob> _buildJobs = new Dictionary<Bot, BuildJob>();
        readonly Dictionary<InfServer.Game.Player, int> _buildNext = new Dictionary<InfServer.Game.Player, int>();

        // (2026-11-17, owner: "if you say remove mg ... the engy goes and dismantles it") "remove mg" ... "remove hq"
        const int OrderRemoveBase = 200;
        const int RemoveCooldownMs = 15000;     // a dismantle order every 15 s per player
        const int RemoveSearchRadius = 1500;    // the nearest one of the team's within this of the player
        readonly Dictionary<InfServer.Game.Player, int> _removeNext = new Dictionary<InfServer.Game.Player, int>();

        static int KindOfWord(string what)
        {
            what = what.Trim();
            if (what == "rap" || what == "rocketap" || what == "rocket-ap") what = "rocket ap";
            if (what == "mg turret") what = "mg";
            if (what.EndsWith(" turret")) what = what.Substring(0, what.Length - 7).Trim();
            for (int i = 0; i < BuildWords.Length; i++)
                if (what == BuildWords[i])
                    return i;
            return -1;
        }

        /// <summary>"mg here" ... "hq here" -> OrderBuildBase + kind; "remove mg" ... -> OrderRemoveBase + kind; else 0.</summary>
        static int ParseBuildOrder(string t)
        {
            if (t.StartsWith("remove ") || t.StartsWith("dismantle "))
            {
                int k = KindOfWord(t.Substring(t.IndexOf(' ') + 1));
                return k >= 0 ? OrderRemoveBase + k : 0;
            }
            if (!t.EndsWith(" here"))
                return 0;
            string what = t.Substring(0, t.Length - 5).Trim();
            if (what == "rap" || what == "rocketap" || what == "rocket-ap") what = "rocket ap";
            if (what == "mg turret") what = "mg";
            for (int i = 0; i < BuildWords.Length; i++)
                if (what == BuildWords[i])
                    return OrderBuildBase + i;
            return 0;
        }

        static bool IsChiefEngineerBot(Bot b)
        {
            return b != null && b._type != null && (b._type.Id == ClsChiefEngineer || b._type.Id == ClsEngineer);
        }

        /// <summary>
        /// The same checks a player's kit gets (ScriptArena.handlePlayerMakeVehicle): null = it may be built here,
        /// else the reason it can't.
        /// </summary>
        string BuildLimitReason(InfServer.Game.Player player, VehInfo vehinfo, short x, short y)
        {
            VehInfo.Computer newComp = vehinfo as VehInfo.Computer;
            if (newComp == null)
                return "that can't be built";
            int playerTotal = 0, totalAmount = 0, totalType = 0, densityAmount = 0, densityType = 0;
            foreach (Vehicle v in _arena.Vehicles)
            {
                if (v == null || v.IsDead || v._type == null)
                    continue;
                if (v._type.Id == vehinfo.Id && v._creator != null && v._creator._alias == player._alias)
                    playerTotal++;
                Computer comp = v as Computer;
                if (comp == null || comp._team != player._team)
                    continue;
                totalAmount++;
                if (comp._type.Name == newComp.Name)
                    totalType++;
            }
            foreach (Vehicle v in _arena.getVehiclesInRange(x, y, newComp.DensityRadius))
            {
                Computer comp = v as Computer;
                if (comp == null || comp.IsDead || comp._team != player._team)
                    continue;
                densityAmount++;
                if (comp._type.Name == newComp.Name)
                    densityType++;
            }
            if (newComp.MaxTypeByPlayerRegardlessOfTeam != -1 && playerTotal >= newComp.MaxTypeByPlayerRegardlessOfTeam)
                return "you have the maximum allowed of this type";
            if (newComp.FrequencyMaxActive != -1 && totalAmount >= newComp.FrequencyMaxActive)
                return "your team already has the maximum allowed computer vehicles";
            if (newComp.FrequencyMaxType != -1 && totalType >= newComp.FrequencyMaxType)
                return "your team already has the maximum allowed of this type";
            if (newComp.FrequencyDensityMaxActive != -1 && densityAmount >= newComp.FrequencyDensityMaxActive)
                return "your team already has the maximum allowed computer vehicles in this area";
            if (newComp.FrequencyDensityMaxType != -1 && densityType >= newComp.FrequencyDensityMaxType)
                return "your team already has the maximum allowed of this type in this area";
            return null;
        }

        VehInfo BuildVehicle(int kind)
        {
            ItemInfo.VehicleMaker maker = _arena._server._assets.getItemByID(BuildItemIds[kind]) as ItemInfo.VehicleMaker;
            return maker != null ? _arena._server._assets.getVehicleByID(maker.vehicleID) : null;
        }

        /// <summary>A build order from team chat (HandleSquadOrder hands it here).</summary>
        void HandleBuildOrder(InfServer.Game.Player player, int kind)
        {
            Team team = player._team;
            int now = Environment.TickCount, next;
            // a job whose engineer died meanwhile is over: the player may order again
            List<Bot> stale = new List<Bot>();
            foreach (KeyValuePair<Bot, BuildJob> kv in _buildJobs)
                if (kv.Key == null || kv.Key.IsDead || kv.Key.bCondemned)
                    stale.Add(kv.Key);
            foreach (Bot sb in stale)
            {
                if (_buildJobs[sb].Player == player)
                    _buildNext.Remove(player);
                _buildJobs.Remove(sb);
            }
            if (_buildNext.TryGetValue(player, out next) && now - next < 0)
            {
                int s = (next - now + 999) / 1000;
                player.sendMessage(-1, String.Format("Squad: the engineer can build again in {0}:{1:00}.", s / 60, s % 60));
                return;
            }
            short x = player._state.positionX, y = player._state.positionY;
            if (_eol != null && _eol.IsInPioneerDropshipSafeZone(x, y) || !InPlayArea(x, y, 0) || _arena.getTile(x, y).Blocked)
            {
                player.sendMessage(-1, "Squad: can't build there - stand on open ground out in the field.");
                return;
            }
            VehInfo veh = BuildVehicle(kind);
            if (veh == null)
            {
                player.sendMessage(-1, "Squad: that can't be built in this zone.");
                return;
            }
            string why = BuildLimitReason(player, veh, x, y);
            if (why != null)
            {
                player.sendMessage(-1, "Squad: " + why + ".");
                return;
            }
            Bot eng = null;
            foreach (RoamingAttacker b in EliteSquadMembers(team))
                if (IsChiefEngineerBot(b) && !b.IsDead && !b.bCondemned && !_buildJobs.ContainsKey(b))
                {
                    if (eng == null || Helpers.distanceTo(b._state, player._state) < Helpers.distanceTo(eng._state, player._state))
                        eng = b;
                }
            if (eng == null)
            {
                player.sendMessage(-1, "Squad: no engineer free right now.");
                return;
            }
            _buildJobs[eng] = new BuildJob { Player = player, Team = team, Kind = kind, X = x, Y = y, Started = now };
            _buildNext[player] = now + BuildCooldownMs;
            player.sendMessage(0, String.Format("&Squad: {0} is on the way to build a {1} here.",
                InfServer.Game.Modules.BotNameModule.GetDisplayName(eng), BuildTitles[kind]));
            SquadSay(eng, Pick3("on it", "building it", "omw, cover me"));
        }

        // ---------- energy (2026-11-17, owner: "engys should have that energizer thing ... similar to how a medic
        // heals. make sure its not all the time so its not over powered and spammy") ----------

        /// <summary>The zone's own engineer tool: Broadcast Charger, Light (+100 energy to everyone within 128 px).</summary>
        const int ItmBroadcastChargerLight = 105;
        const int EngineerChargeRadius = 128;
        const double EngineerChargeBelow = 0.40;     // only when a team mate in reach is under 40 % energy
        const int EngineerChargeCooldownMs = 20000;  // and at most once every 20 s per engineer
        int _tickEngCharge;
        readonly Dictionary<Bot, int> _engChargeNext = new Dictionary<Bot, int>();

        static int MaxEnergyOf(Vehicle v)
        {
            return v != null && v._type != null && v._type.EnergyMax > 0 ? v._type.EnergyMax : 0;
        }

        /// <summary>
        /// Every 500 ms: each squad Chief Engineer whose charger is ready and who has a low-energy team mate (player
        /// or bot) within reach charges everyone round it once - players get it from the client exactly as from a
        /// player's Broadcast Charger, bots get the +100 straight.
        /// </summary>
        void PollEngineerCharge(int now)
        {
            if (_arena == null || now - _tickEngCharge < 500)
                return;
            _tickEngCharge = now;
            ItemInfo.RepairItem charger = _arena._server._assets.getItemByID(ItmBroadcastChargerLight) as ItemInfo.RepairItem;
            if (charger == null)
                return;
            int amount = charger.repairAmount > 0 ? charger.repairAmount : 100;
            foreach (Bot e in _arena.ActiveBots)
            {
                if (e == null || e.IsDead || e.bCondemned || !IsChiefEngineerBot(e) || !IsRoamingSquadTeam(e._team) || IsDeploying(e))
                    continue;
                int due;
                if (_engChargeNext.TryGetValue(e, out due) && now - due < 0)
                    continue;
                bool someoneLow = false;
                List<InfServer.Game.Player> players = new List<InfServer.Game.Player>();
                foreach (InfServer.Game.Player p in e._team.ActivePlayers)
                {
                    if (p == null || p.IsDead || p._state == null || p._client == null || Helpers.distanceTo(p._state, e._state) > EngineerChargeRadius)
                        continue;
                    players.Add(p);
                    int max = MaxEnergyOf(p.ActiveVehicle);
                    if (max > 0 && p._state.energy < max * EngineerChargeBelow)
                        someoneLow = true;
                }
                List<Bot> bots = new List<Bot>();
                foreach (Bot b in _arena.ActiveBots)
                {
                    if (b == null || b.IsDead || b.bCondemned || b._team != e._team || Helpers.distanceTo(b._state, e._state) > EngineerChargeRadius)
                        continue;
                    bots.Add(b);
                    int max = MaxEnergyOf(b);
                    if (max > 0 && b._state.energy < max * EngineerChargeBelow)
                        someoneLow = true;
                }
                if (!someoneLow)
                    continue;
                _engChargeNext[e] = now + EngineerChargeCooldownMs;
                foreach (InfServer.Game.Player p in players)
                {
                    SC_ItemUsed used = new SC_ItemUsed();
                    used.userPlayer = e._id;
                    used.targetVehicle = p._id;
                    used.itemID = (short)charger.id;
                    used.posX = e._state.positionX;
                    used.posY = e._state.positionY;
                    p._client.sendReliable(used);
                }
                foreach (Bot b in bots)
                {
                    int max = MaxEnergyOf(b);
                    if (max > 0)
                        b._state.energy = (short)Math.Min(max, b._state.energy + amount);
                }
                if (players.Count > 0 && _arena._rand.Next(3) == 0)
                    SquadSay(e, Pick3("energy up", "charged you", "power's back, go"));
            }
        }

        // ---------- the wrench (2026-11-17, owner: "add animation for the build wrench thing ... the dismantle
        // animation") ----------

        /// <summary>The engineer's tool: Active Repair Kit, Light - its use effect plays at the engineer / the turret.</summary>
        const int ItmEngineerWrench = 104;
        const int EngineerToolFxMs = 1000;
        readonly Dictionary<Bot, int> _engToolFxNext = new Dictionary<Bot, int>();

        /// <summary>Once a second: everyone near sees the engineer use its repair kit (on the turret it works on, or on
        /// the spot it's building). A look only - nothing is healed by it.</summary>
        void EngineerToolFx(Bot eng, Vehicle target, int now)
        {
            int due;
            if (_engToolFxNext.TryGetValue(eng, out due) && now - due < 0)
                return;
            _engToolFxNext[eng] = now + EngineerToolFxMs;
            ItemInfo wrench = _arena._server._assets.getItemByID(ItmEngineerWrench);
            if (wrench == null)
                return;
            SC_ItemUsed used = new SC_ItemUsed();
            used.userPlayer = eng._id;
            used.targetVehicle = target != null ? target._id : eng._id;
            used.itemID = (short)wrench.id;
            used.posX = target != null ? target._state.positionX : eng._state.positionX;
            used.posY = target != null ? target._state.positionY : eng._state.positionY;
            foreach (InfServer.Game.Player p in _arena.getPlayersAndSpecInRange(eng._state.positionX, eng._state.positionY, 1000))
                if (p?._client != null && (target == null || p._id != target._id))
                    p._client.sendReliable(used);
        }

        // ---------- repairs (2026-11-17, owner: "they should also repair the turrets if they are low") ----------

        const double EngineerRepairBelow = 0.75;   // a team turret / HQ under 75 % is worth fixing
        const int EngineerRepairSeek = 700;        // looks this far for one
        const int EngineerRepairReach = 80;        // works on it from this close
        const int EngineerRepairPulseMs = 1500;
        readonly Dictionary<Bot, int> _engRepairNext = new Dictionary<Bot, int>();

        /// <summary>
        /// A squad engineer out of a fight: true = it is walking to / repairing the most damaged team computer
        /// (turret, HQ...) near it - 10 % of its hitpoints every 1.5 s until it's full.
        /// </summary>
        public bool PollEngineerRepair(RoamingAttacker b, int now)
        {
            if (b == null || b._team == null || !IsChiefEngineerBot(b) || IsDeploying(b) || b.IsDead)
                return false;
            Computer best = null;
            double bestNeed = 0;
            foreach (Vehicle v in _arena.getVehiclesInRange(b._state.positionX, b._state.positionY, EngineerRepairSeek))
            {
                Computer c = v as Computer;
                if (c == null || c.IsDead || c.bCondemned || c._team != b._team || c._type == null || c._type.Hitpoints <= 0)
                    continue;
                if (IsSwarmNest(c))
                    continue;
                double need = 1.0 - (double)c._state.health / c._type.Hitpoints;
                if (need > 1.0 - EngineerRepairBelow && need > bestNeed)
                {
                    bestNeed = need;
                    best = c;
                }
            }
            if (best == null)
                return false;
            Helpers.ObjectState at = new Helpers.ObjectState { positionX = best._state.positionX, positionY = best._state.positionY };
            if (Helpers.distanceTo(b._state, at) > EngineerRepairReach)
            {
                b.ApplyNavigationApproach(at, EolHqPatrol.MakeLineClearPendingSeek(b, () => at), new BotNavigateOptions
                {
                    RequeueMinMs = 1200,
                    PathStartIndex = 0,
                    Priority = PathPriority.Idle,
                });
                return true;
            }
            b.HoldPosition();
            int due;
            if (_engRepairNext.TryGetValue(b, out due) && now - due < 0)
                return true;
            _engRepairNext[b] = now + EngineerRepairPulseMs;
            EngineerToolFx(b, best, now);   // (the wrench on the turret)
            int max = best._type.Hitpoints;
            best._state.health = (short)Math.Min(max, best._state.health + Math.Max(5, max / 10));
            best._sendUpdate = true;
            Helpers.Update_RouteComputerHealth(best);   // (everyone near sees the bar go up)
            if (best._state.health >= max)
                SquadSay(b, Pick3("turret's fixed", "repaired", "good as new"));
            return true;
        }

        /// <summary>
        /// First in a squad engineer's poll: true = it is on a build job this poll (running to the spot, or working
        /// there). It drops everything else for the job, like a medic called with H.
        /// </summary>
        public bool PollEngineerBuild(RoamingAttacker b, int now)
        {
            BuildJob job;
            if (b == null || !_buildJobs.TryGetValue(b, out job))
                return false;
            InfServer.Game.Player p = job.Player;
            if (b.IsDead || b.bCondemned || b._team != job.Team || p == null || p._team != job.Team)
            {
                _buildJobs.Remove(b);
                if (p != null && p._team == job.Team && !b.bCondemned)
                    p.sendMessage(-1, "Squad: your engineer went down - the build is off.");
                if (p != null && job.Target == null)
                    _buildNext.Remove(p);   // (nothing was built: they may order again)
                return false;
            }
            if (now - job.Started > BuildTravelTimeoutMs && job.ArrivedAt == 0)
            {
                _buildJobs.Remove(b);
                if (job.Target == null)
                    _buildNext.Remove(p);
                p.sendMessage(-1, "Squad: the engineer couldn't get there - the order is off.");
                return false;
            }
            if (job.Target != null && (job.Target.IsDead || job.Target.bCondemned || job.Target._team != job.Team))
            {
                _buildJobs.Remove(b);
                p.sendMessage(-1, "Squad: it's already gone.");
                return false;
            }
            Helpers.ObjectState spot = new Helpers.ObjectState { positionX = job.X, positionY = job.Y };
            // (2026-11-17, owner: "he took his time going back and forth ... he should stand still and 3 seconds then
            // it builds") once there it stays there: arrival is latched, it holds still and works - no more re-pathing
            // round the spot (which restarted the 3 s); on the way it drives straight in when it has a clear line
            if (job.ArrivedAt == 0)
            {
                double dist = Helpers.distanceTo(b._state, spot);
                if (dist > (job.Target != null ? EngineerRepairReach : BuildArriveRadius))
                {
                    if (dist < 260 && WideLineClear(b._state.positionX, b._state.positionY, job.X, job.Y))
                        SteerStraight(b, job.X, job.Y);
                    else
                        b.ApplyNavigationApproach(spot, EolHqPatrol.MakeLineClearPendingSeek(b, () => spot), new BotNavigateOptions
                        {
                            RequeueMinMs = 900,
                            PathStartIndex = 0,
                            Priority = PathPriority.Combat,
                        });
                    return true;
                }
                job.ArrivedAt = NonZeroTick(now);
                try { b.Navigation?.Clear(); } catch { }
            }
            b.HoldPosition();
            if (now - job.ArrivedAt < BuildWorkMs)
            {   // (3 s of work on the spot, the wrench going - on the turret when dismantling)
                EngineerToolFx(b, job.Target, now);
                return true;
            }
            _buildJobs.Remove(b);
            if (job.Target != null)
            {   // a dismantle: the turret / HQ is taken down quietly (no kill, no drop) and its place is free again
                Vehicle t = job.Target;
                if (t.IsDead || t.bCondemned || t._team != job.Team)
                {
                    p.sendMessage(-1, "Squad: it's already gone.");
                    return false;
                }
                t.destroy(true);
                p.sendMessage(0, String.Format("&Squad: {0} dismantled.", BuildTitles[job.Kind]));
                SquadSay(b, Pick3("it's down", "dismantled", "packed up"));
                Log.write(TLog.Normal, "[Eol] {0} dismantled a {1} for {2}.", b._alias, t._type != null ? t._type.Name : "?", p._alias);
                return false;
            }
            VehInfo veh = BuildVehicle(job.Kind);
            string why = veh == null ? "that can't be built in this zone" : BuildLimitReason(p, veh, job.X, job.Y);
            if (why != null)
            {
                p.sendMessage(-1, "Squad: build stopped - " + why + ".");
                _buildNext.Remove(p);
                return false;
            }
            Vehicle built = _arena.newVehicle(veh, job.Team, p, spot);
            if (built == null)
            {
                p.sendMessage(-1, "Squad: the build failed - try another spot.");
                _buildNext.Remove(p);
                return false;
            }
            p.sendMessage(0, String.Format("&Squad: {0} built.", BuildTitles[job.Kind]));
            SquadSay(b, Pick3("done", "built, it's up", "there you go"));
            Log.write(TLog.Normal, "[Eol] {0} built a {1} for {2} at {3},{4}.", b._alias, veh.Name, p._alias, job.X / 16, job.Y / 16);
            return false;
        }
    }
}
