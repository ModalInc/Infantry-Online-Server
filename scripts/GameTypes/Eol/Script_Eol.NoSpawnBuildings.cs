using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-17, owner: "make sure no bots can spawn in the building 361,348 .... they get stuck. make the spawn
    /// outside if they are spawning there") buildings no bot may spawn in. Each is the building's floor - the
    /// connected tiles of the building-floor terrain (lookup 36 / 39) from a seed tile - read from the map at start.
    ///   - findOpenWarpBot / findOpenRoamBot never pick a spot inside one (a search centred inside moves outside);
    ///   - a bot that still appears inside one (new, revived or teleported) is moved at once to the nearest open
    ///     ground outside it.
    /// </summary>
    public partial class Script_Eol
    {
        /// <summary>Seed tiles (on the building's floor) of the buildings bots must not spawn in.</summary>
        static readonly short[][] NoBotSpawnBuildingSeeds = { new short[] { 361, 348 } };

        bool[] _noSpawnTiles;
        int _noSpawnW, _noSpawnH;
        bool _noSpawnBuilt;

        void BuildNoSpawnBuildings()
        {
            _noSpawnBuilt = true;
            LvlInfo lvl = _arena != null ? _arena._server._assets.Level : null;
            if (lvl == null)
                return;
            int W = lvl.Width, H = lvl.Height;
            bool[] inB = new bool[W * H];
            Func<int, bool> floor = i => { int t = lvl.Tiles[i].Unknown0 & 0x7F; return t == 36 || t == 39; };
            int total = 0;
            foreach (short[] s in NoBotSpawnBuildingSeeds)
            {
                int start = s[1] * W + s[0];
                if (start < 0 || start >= W * H || !floor(start) || inB[start])
                    continue;
                Queue<int> q = new Queue<int>();
                q.Enqueue(start);
                inB[start] = true;
                while (q.Count > 0)
                {
                    int i = q.Dequeue(); total++;
                    int x = i % W, y = i / W;
                    if (x > 0 && !inB[i - 1] && floor(i - 1)) { inB[i - 1] = true; q.Enqueue(i - 1); }
                    if (x < W - 1 && !inB[i + 1] && floor(i + 1)) { inB[i + 1] = true; q.Enqueue(i + 1); }
                    if (y > 0 && !inB[i - W] && floor(i - W)) { inB[i - W] = true; q.Enqueue(i - W); }
                    if (y < H - 1 && !inB[i + W] && floor(i + W)) { inB[i + W] = true; q.Enqueue(i + W); }
                }
            }
            _noSpawnTiles = inB;
            _noSpawnW = W;
            _noSpawnH = H;
            Log.write(TLog.Normal, "[Eol] no-bot-spawn buildings: {0} tiles", total);
        }

        /// <summary>(2026-11-21, owner: "remove all bot friendly zones"; "there is a base right in F5 outside? it should be on the
        /// flag") false = no building is closed to bots: bases go right at their flag (F5's is inside the 361,348 building).</summary>
        public const bool NoBotSpawnBuildingsOn = false;

        /// <summary>True when this spot (raw coords) is inside a building no bot may spawn in.</summary>
        public bool InNoBotSpawnBuilding(int x, int y)
        {
            if (!NoBotSpawnBuildingsOn)
                return false;
            if (!_noSpawnBuilt)
                BuildNoSpawnBuildings();
            if (_noSpawnTiles == null)
                return false;
            int tx = x / 16, ty = y / 16;
            if (tx < 0 || ty < 0 || tx >= _noSpawnW || ty >= _noSpawnH)
                return false;
            return _noSpawnTiles[ty * _noSpawnW + tx];
        }

        /// <summary>The nearest open ground outside the no-spawn building (rings out from the spot), or null.</summary>
        Helpers.ObjectState NearestOutsideNoSpawn(int x, int y)
        {
            for (int r = 32; r <= 2400; r += 32)
                for (int k = 0; k < 24; k++)
                {
                    double a = k * Math.PI / 12;
                    short px = (short)(x + Math.Cos(a) * r), py = (short)(y + Math.Sin(a) * r);
                    if (px < 16 || py < 16)
                        continue;
                    if (InNoBotSpawnBuilding(px, py) || _arena.getTile(px, py).Blocked || NearNewPlayerBuffer(px, py)
                        || !InPlayArea(px, py, 0))
                        continue;
                    return new Helpers.ObjectState { positionX = px, positionY = py };
                }
            return null;
        }

        /// <summary>The nearest spot outside the no-spawn building with room for a base (open ground all round), or null.</summary>
        Helpers.ObjectState HqSpotOutsideNoSpawn(int x, int y)
        {
            for (int r = 64; r <= 3200; r += 32)
                for (int k = 0; k < 32; k++)
                {
                    double a = k * Math.PI / 16;
                    int px = (int)(x + Math.Cos(a) * r), py = (int)(y + Math.Sin(a) * r);
                    if (px < 64 || py < 64 || InNoBotSpawnBuilding(px, py) || NearNewPlayerBuffer(px, py) || !InPlayArea((short)px, (short)py, 0))
                        continue;
                    // the whole base footprint off the building and on open ground
                    if (InNoBotSpawnBuilding(px - 160, py) || InNoBotSpawnBuilding(px + 160, py) || InNoBotSpawnBuilding(px, py - 160) || InNoBotSpawnBuilding(px, py + 160))
                        continue;
                    if (!AreaOpen(px, py, ObjectiveHqClearRaw))
                        continue;
                    return new Helpers.ObjectState { positionX = (short)px, positionY = (short)py };
                }
            return null;
        }

        readonly HashSet<Bot> _noSpawnSeenAlive = new HashSet<Bot>();
        int _tickNoSpawnSweep;

        /// <summary>
        /// Every 250 ms: a bot that has just come alive (spawned, revived) or that was teleported inside a no-spawn
        /// building goes straight to open ground outside it. Bots that walk in later (chasing someone) are left alone.
        /// </summary>
        void PollNoSpawnBuildings(int now)
        {
            if (_arena == null || now - _tickNoSpawnSweep < 250)
                return;
            _tickNoSpawnSweep = now;
            if (!_noSpawnBuilt)
                BuildNoSpawnBuildings();
            if (_noSpawnTiles == null)
                return;
            List<Bot> alive = new List<Bot>();
            foreach (Bot b in _arena.ActiveBots)
            {
                if (b == null || b.bCondemned || b.IsDead || b._state == null)
                    continue;
                alive.Add(b);
                if (_noSpawnSeenAlive.Contains(b))
                    continue;   // (already checked when it came alive)
                if (IsDeploying(b) || _swarmDeploying.ContainsKey(b))
                    continue;   // (still in the dropship / bug cave: checked once it lands)
                _noSpawnSeenAlive.Add(b);
                if (!InNoBotSpawnBuilding(b._state.positionX, b._state.positionY))
                    continue;
                Helpers.ObjectState outside = NearestOutsideNoSpawn(b._state.positionX, b._state.positionY);
                if (outside == null)
                    continue;
                WarpBot(b, outside);
                Log.write(TLog.Normal, "[Eol] {0} spawned inside a no-spawn building ({1},{2}) - moved outside to {3},{4}.",
                    b._alias, b._state.positionX / 16, b._state.positionY / 16, outside.positionX / 16, outside.positionY / 16);
            }
            // dead / gone bots leave the set (a revive counts as coming alive again)
            _noSpawnSeenAlive.IntersectWith(alive);
        }

        /// <summary>A teleport (squad @, deploy landing...) into a no-spawn building lands outside it instead.</summary>
        Helpers.ObjectState OutsideNoSpawnBuilding(Helpers.ObjectState at)
        {
            if (at == null || !InNoBotSpawnBuilding(at.positionX, at.positionY))
                return at;
            return NearestOutsideNoSpawn(at.positionX, at.positionY) ?? at;
        }
    }
}
