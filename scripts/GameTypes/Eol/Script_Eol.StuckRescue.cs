using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;
using Assets;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-17, owner: "bots getting stuck around 422,59 ... make sure no bot can get stuck ever it will always find
    /// its way out. or teleports to a team member if stuck for unusual amount of time") the map-wide safety net behind
    /// the bots' own steering nudge (EolBotPhysicsUtil) and the known-choke rule (Script_Eol.ChokeRescue.cs).
    /// Every bot's position is sampled; one that hasn't moved 48 px in
    ///   StuckWallMs while pressed against terrain (a blocked tile within 1.5 tiles), or
    ///   StuckAnyMs anywhere (roaming bots only - base guards may stand still on purpose)
    /// is rescued: a roaming bot goes to its nearest team mate out in the field (player first, then bot); with none,
    /// or for a base guard, to the nearest open connected ground a few tiles away.
    /// </summary>
    public partial class Script_Eol
    {
        const int StuckWallMs = 8000;
        const int StuckWallFightMs = 20000;

        /// <summary>A live enemy player / bot within r (raw) of this bot.</summary>
        bool EnemyNear(Bot b, int r)
        {
            foreach (InfServer.Game.Player p in _arena.getPlayersInRange(b._state.positionX, b._state.positionY, r))
                if (p != null && !p.IsDead && !p.IsSpectator && p._team != b._team)
                    return true;
            foreach (Vehicle v in _arena.getVehiclesInRange(b._state.positionX, b._state.positionY, r,
                delegate (Vehicle x) { return x is Bot && x != b && !x.IsDead && x._team != b._team; }))
                return true;
            return false;
        }

        /// <summary>The bot fired in the last 3 s.</summary>
        static bool Fighting(Bot b, int now) { return b.LastShotTick != 0 && now - b.LastShotTick < 3000; }
        const int StuckAnyMs = 30000;
        const int StuckMoveRaw = 48;
        const int StuckRescueCooldownMs = 10000;
        int _tickStuckRescue;
        readonly Dictionary<Bot, StuckSample> _mapStuck = new Dictionary<Bot, StuckSample>();
        readonly Dictionary<Bot, int> _mapStuckRescued = new Dictionary<Bot, int>();

        void PollStuckRescue(int now)
        {
            if (_arena == null || now - _tickStuckRescue < 1000)
                return;
            _tickStuckRescue = now;
            HashSet<Bot> seen = new HashSet<Bot>();
            foreach (Bot b in _arena.ActiveBots)
            {
                if (b == null || b.IsDead || b.bCondemned || b._state == null || b._team == null)
                    continue;
                seen.Add(b);
                short x = b._state.positionX, y = b._state.positionY;
                if (PollVehicleIndoors(b, x, y, now))
                    continue;
                StuckSample s;
                if (!_mapStuck.TryGetValue(b, out s) || Helpers.distanceTo(x, y, s.X, s.Y) >= StuckMoveRaw
                    || IsDeploying(b) || SwarmBugDeploying(b) || ShipSummonHold(b) != null
                    || _eol != null && _eol.IsInPioneerDropshipSafeZone(x, y))
                {   // moving (or in the dropship / its walk out, which have their own rules): new sample
                    _mapStuck[b] = new StuckSample { X = x, Y = y, Since = now };
                    continue;
                }
                bool roamer = b is RoamingAttacker || b is RoamingCaptain;
                bool baseBot = b is BasicDefense || b is Captain;
                int still = now - s.Since;
                // (a bot that shot in the last 3 s is fighting - maybe from cover: give it longer before moving it)
                // (2026-11-18) in a fight (firing, or an enemy close by - it may be lining up or reloading behind cover): longer
                int wallMs = Fighting(b, now) || EnemyNear(b, 700) ? StuckWallFightMs : StuckWallMs;
                bool wedged = still >= wallMs && TerrainTouching(x, y, 24);
                Helpers.ObjectState hqState = baseBot ? FindHqVehicleForTeam(b._team)?._state : null;
                bool lostFromBase = hqState != null && still >= StuckAnyMs && Helpers.distanceTo(b._state, hqState) > EolHqPatrol.HomeLeashRaw;
                if (!wedged && !(roamer && still >= StuckAnyMs) && !lostFromBase)
                    continue;
                int last;
                if (_mapStuckRescued.TryGetValue(b, out last) && now - last < StuckRescueCooldownMs)
                    continue;

                Helpers.ObjectState to = null; string toName = null;
                if (roamer)
                    to = MateSpotFor(b, out toName);
                else if (lostFromBase)
                {   // a base bot that can't find its way home: back beside its HQ
                    to = OpenSpotNear(hqState.positionX, hqState.positionY, 300);
                    toName = "its base";
                }
                if (to == null)
                {
                    to = NearestOpenOutside(x, y, 64);
                    if (to != null && !IsOpenConnectedGround(to.positionX, to.positionY))
                        to = null;
                    toName = "open ground";
                }
                _mapStuck[b] = new StuckSample { X = x, Y = y, Since = now };
                if (to == null)
                    continue;
                _mapStuckRescued[b] = now;
                WarpBot(b, to);
                b.DamageImmuneUntilTick = NonZeroTick(now + 1000);
                Log.write(TLog.Normal, "[Eol] {0} stuck at {1},{2} for {3} s - moved to {4} ({5},{6}).", b._alias, x / 16, y / 16,
                    still / 1000, toName, to.positionX / 16, to.positionY / 16);
            }
            // forget bots that are gone
            List<Bot> gone = null;
            foreach (Bot b in _mapStuck.Keys)
                if (!seen.Contains(b)) (gone ??= new List<Bot>()).Add(b);
            if (gone != null)
                foreach (Bot b in gone) { _mapStuck.Remove(b); _mapStuckRescued.Remove(b); _vehIndoor.Remove(b); _vehIndoorSince.Remove(b); }
        }

        // ---------- vehicle squad indoors (2026-11-17, owner: "the vehicle team often get stuck indoors. please help them
        // so they find their way out") a board / Drop Pack bot of the vehicle squad on building floor (terrain 36 / 39)
        // that hasn't moved 96 px in VehIndoorStillMs, or has been indoors VehIndoorMaxMs at all, goes outside: to a
        // team mate who is outdoors, else to the nearest open outdoor ground.
        const int VehIndoorStillMs = 6000;
        const int VehIndoorStillFightMs = 15000;
        const int VehIndoorMaxMs = 20000;
        // (2026-11-17, owner: "some bots also get stuck in the d5 building ... find their way out to head to objectives")
        // the same rule for every roaming foot bot: out after 6 s without progress, or 45 s indoors at all
        const int FootIndoorMaxMs = 45000;
        readonly Dictionary<Bot, StuckSample> _vehIndoor = new Dictionary<Bot, StuckSample>();
        readonly Dictionary<Bot, int> _vehIndoorSince = new Dictionary<Bot, int>();

        /// <summary>True when it handled this bot (indoors, being watched or just moved out).</summary>
        bool PollVehicleIndoors(Bot b, short x, short y, int now)
        {
            bool veh = IsVehicleSquadTeam(b._team), roamer = b is RoamingAttacker || b is RoamingCaptain;
            if (!(veh || roamer) || !IsIndoors(x, y) || IsDeploying(b) || ShipSummonHold(b) != null
                || _eol != null && _eol.IsInPioneerDropshipSafeZone(x, y))
            {
                _vehIndoor.Remove(b); _vehIndoorSince.Remove(b);
                return false;
            }
            int since;
            if (!_vehIndoorSince.TryGetValue(b, out since)) { _vehIndoorSince[b] = since = now; }
            StuckSample s;
            if (!_vehIndoor.TryGetValue(b, out s) || Helpers.distanceTo(x, y, s.X, s.Y) >= 96)
                _vehIndoor[b] = s = new StuckSample { X = x, Y = y, Since = now };
            bool fighting = Fighting(b, now);
            if (fighting && !veh)
                _vehIndoorSince[b] = since = Math.Max(since, now - FootIndoorMaxMs / 2);   // (a fight inside doesn't run the 45 s out)
            int stillMs = fighting ? VehIndoorStillFightMs : VehIndoorStillMs;
            if (now - s.Since < stillMs && now - since < (veh ? VehIndoorMaxMs : FootIndoorMaxMs))
                return true;
            Helpers.ObjectState to = null; string toName = "outdoors";
            foreach (InfServer.Game.Player p in _arena.PlayersIngame)
                if (p != null && !p.IsDead && !p.IsSpectator && p._team == b._team && p._state != null && !IsIndoors(p._state.positionX, p._state.positionY)
                    && !(_eol != null && _eol.IsInPioneerDropshipSafeZone(p._state.positionX, p._state.positionY)) && InPlayArea(p._state.positionX, p._state.positionY, 0))
                { to = OpenSpotNear(p._state.positionX, p._state.positionY, 140); toName = p._alias; if (to != null) break; }
            if (to == null)
                foreach (Bot o in _arena.ActiveBots)
                    if (o != null && o != b && !o.IsDead && !o.bCondemned && o._team == b._team && o._state != null && !IsIndoors(o._state.positionX, o._state.positionY)
                        && !(_eol != null && _eol.IsInPioneerDropshipSafeZone(o._state.positionX, o._state.positionY)) && InPlayArea(o._state.positionX, o._state.positionY, 0))
                    { to = OpenSpotNear(o._state.positionX, o._state.positionY, 140); toName = o._alias; if (to != null) break; }
            if (to != null && IsIndoors(to.positionX, to.positionY))
                to = null;
            if (to == null)
            { to = NearestOutdoors(x, y); toName = "outdoors"; }
            _vehIndoor.Remove(b); _vehIndoorSince.Remove(b);
            if (to == null)
                return true;
            WarpBot(b, to);
            b.DamageImmuneUntilTick = NonZeroTick(now + 1000);
            _mapStuck[b] = new StuckSample { X = to.positionX, Y = to.positionY, Since = now };
            Log.write(TLog.Normal, "[Eol] {0} stuck indoors at {1},{2} - moved to {3} ({4},{5}).", b._alias, x / 16, y / 16,
                toName, to.positionX / 16, to.positionY / 16);
            return true;
        }

        /// <summary>Building floor (terrain lookup 36 / 39) under this spot.</summary>
        bool IsIndoors(int x, int y)
        {
            LvlInfo lvl = _arena?._server?._assets?.Level;
            if (lvl == null) return false;
            int tx = x / 16, ty = y / 16;
            if (tx < 0 || ty < 0 || tx >= lvl.Width || ty >= lvl.Height) return false;
            int t = lvl.Tiles[ty * lvl.Width + tx].Unknown0 & 0x7F;
            return t == 36 || t == 39;
        }

        /// <summary>The nearest open, connected, in-play outdoor spot (rings out to ~60 tiles).</summary>
        Helpers.ObjectState NearestOutdoors(int x, int y)
        {
            for (int r = 48; r <= 960; r += 24)
                for (int k = 0; k < 32; k++)
                {
                    double a = k * Math.PI / 16;
                    short px = (short)(x + Math.Cos(a) * r), py = (short)(y + Math.Sin(a) * r);
                    if (px < 32 || py < 32 || IsIndoors(px, py) || !InPlayArea(px, py, 0) || InNoBotSpawnBuilding(px, py) || !AreaOpen(px, py, 24))
                        continue;
                    if (!IsOpenConnectedGround(px, py))
                        continue;
                    return new Helpers.ObjectState { positionX = px, positionY = py };
                }
            return null;
        }

        /// <summary>A blocked tile within r (raw) of (x,y): the bot is up against terrain.</summary>
        bool TerrainTouching(int x, int y, int r)
        {
            for (int oy = -r; oy <= r; oy += 8)
                for (int ox = -r; ox <= r; ox += 8)
                    if (_arena.getTile((short)(x + ox), (short)(y + oy)).Blocked)
                        return true;
            return false;
        }

        /// <summary>Open ground beside the stuck bot's nearest team mate out in the field (a player first, then a bot that's moving).</summary>
        Helpers.ObjectState MateSpotFor(Bot b, out string name)
        {
            name = null;
            Helpers.ObjectState best = null; double bestD = double.MaxValue; bool bestPlayer = false;
            foreach (InfServer.Game.Player p in _arena.PlayersIngame)
            {
                if (p == null || p.IsDead || p.IsSpectator || p._team != b._team || p._state == null)
                    continue;
                if (_eol != null && _eol.IsInPioneerDropshipSafeZone(p._state.positionX, p._state.positionY) || !InPlayArea(p._state.positionX, p._state.positionY, 0))
                    continue;
                double d = Helpers.distanceTo(b._state, p._state);
                if (d < bestD) { bestD = d; best = p._state; name = p._alias; bestPlayer = true; }
            }
            if (!bestPlayer)
                foreach (Bot o in _arena.ActiveBots)
                {
                    if (o == null || o == b || o.IsDead || o.bCondemned || o._team != b._team || o._state == null)
                        continue;
                    StuckSample os;
                    if (_mapStuck.TryGetValue(o, out os) && Environment.TickCount - os.Since > 4000)
                        continue;   // (not one that's stuck too)
                    if (_eol != null && _eol.IsInPioneerDropshipSafeZone(o._state.positionX, o._state.positionY) || !InPlayArea(o._state.positionX, o._state.positionY, 0))
                        continue;
                    double d = Helpers.distanceTo(b._state, o._state);
                    if (d > 160 && d < bestD) { bestD = d; best = o._state; name = o._alias; }
                }
            if (best == null)
                return null;
            return OpenSpotNear(best.positionX, best.positionY, 140);
        }
    }
}
