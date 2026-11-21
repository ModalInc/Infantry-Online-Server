using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-17, owner: "bots are getting stuck at 146,411 make sure they do not get stuck here") narrow gaps on
    /// the map where bots wedge themselves (146,411: a 3-tile slot between two cliff hills). Any bot in one that
    /// hasn't made 16 px of progress for 2 s is moved to the nearest open ground outside it. (The swarm also keeps
    /// its own instant rule for 145,410 - Script_Eol.AlienSwarm.cs.)
    /// </summary>
    public partial class Script_Eol
    {
        /// <summary>Choke spots in tiles, and how far round each counts as "in it".</summary>
        static short[][] BotChokeTiles { get { return EolMap.BotChokeTiles; } }
        const int BotChokeRadius = 5 * 16;
        const int BotChokeStuckMs = 2000;
        int _tickChokeRescue;
        readonly Dictionary<Bot, StuckSample> _chokeStuck = new Dictionary<Bot, StuckSample>();

        void PollChokeRescue(int now)
        {
            if (_arena == null || now - _tickChokeRescue < 400)
                return;
            _tickChokeRescue = now;
            List<Bot> seen = new List<Bot>();
            foreach (Bot b in _arena.ActiveBots)
            {
                if (b == null || b.IsDead || b.bCondemned || b._state == null)
                    continue;
                short[] spot = null;
                foreach (short[] c in BotChokeTiles)
                    if (Helpers.distanceTo(b._state.positionX, b._state.positionY, TileRaw(c[0]), TileRaw(c[1])) <= BotChokeRadius)
                    { spot = c; break; }
                if (spot == null)
                    continue;
                seen.Add(b);
                StuckSample s;
                if (!_chokeStuck.TryGetValue(b, out s)
                    || Helpers.distanceTo(b._state.positionX, b._state.positionY, s.X, s.Y) >= 16)
                {
                    _chokeStuck[b] = new StuckSample { X = b._state.positionX, Y = b._state.positionY, Since = now };
                    continue;
                }
                if (now - s.Since < BotChokeStuckMs)
                    continue;
                Helpers.ObjectState out_ = NearestOpenOutside(TileRaw(spot[0]), TileRaw(spot[1]), BotChokeRadius + 32);
                _chokeStuck.Remove(b);
                if (out_ == null)
                    continue;
                WarpBot(b, out_);
                Log.write(TLog.Normal, "[Eol] {0} stuck in the choke at {1},{2} - moved to {3},{4}.", b._alias, spot[0], spot[1],
                    out_.positionX / 16, out_.positionY / 16);
            }
            // forget bots that left the chokes
            List<Bot> gone = new List<Bot>();
            foreach (Bot b in _chokeStuck.Keys)
                if (!seen.Contains(b))
                    gone.Add(b);
            foreach (Bot b in gone)
                _chokeStuck.Remove(b);
        }

        /// <summary>The nearest open, in-play spot at least <paramref name="minDist"/> from (x,y) with room round it.</summary>
        Helpers.ObjectState NearestOpenOutside(int x, int y, int minDist)
        {
            for (int r = minDist; r <= minDist + 320; r += 16)
                for (int k = 0; k < 24; k++)
                {
                    double a = k * Math.PI / 12;
                    short px = (short)(x + Math.Cos(a) * r), py = (short)(y + Math.Sin(a) * r);
                    if (px < 32 || py < 32 || !InPlayArea(px, py, 0) || InNoBotSpawnBuilding(px, py))
                        continue;
                    bool open = true;
                    for (int oy = -16; oy <= 16 && open; oy += 16)
                        for (int ox = -16; ox <= 16 && open; ox += 16)
                            if (_arena.getTile((short)(px + ox), (short)(py + oy)).Blocked)
                                open = false;
                    if (open)
                        return new Helpers.ObjectState { positionX = px, positionY = py };
                }
            return null;
        }
    }
}
