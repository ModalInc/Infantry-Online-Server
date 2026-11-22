using System;
using System.Collections.Generic;

using InfServer.Game;
using InfServer.Protocol;
using Assets;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-22, owner: some bots spawn in lava - if they do, they die from the lava) lava is terrain 15 (health -333 a tick,
    /// deadly to players, done by their own client). The engine never applies terrain health to bots, so a bot standing in
    /// lava walked on unhurt. Now a bot on lava for LavaKillMs dies (no killer - nobody gets the kill).
    /// </summary>
    public partial class Script_Eol
    {
        const int LavaTerrainId = 15;
        const int LavaKillMs = 1000;
        const int LavaPollMs = 250;

        readonly Dictionary<Bots.Bot, int> _botOnLavaSince = new Dictionary<Bots.Bot, int>();
        int _tickLavaPoll;

        void PollLavaBots(int now)
        {
            if (_arena == null || now - _tickLavaPoll < LavaPollMs)
                return;
            _tickLavaPoll = now;

            List<Bots.Bot> dead = null;
            foreach (Bots.Bot b in _arena.ActiveBots)
            {
                if (b == null || b.IsDead || b.bCondemned || b._state == null)
                    continue;
                if (_arena.getTerrainID(b._state.positionX, b._state.positionY) != LavaTerrainId)
                {
                    _botOnLavaSince.Remove(b);
                    continue;
                }
                int since;
                if (!_botOnLavaSince.TryGetValue(b, out since))
                {
                    _botOnLavaSince[b] = now;
                    continue;
                }
                if (now - since >= LavaKillMs)
                {
                    if (dead == null) dead = new List<Bots.Bot>();
                    dead.Add(b);
                }
            }

            if (dead != null)
                foreach (Bots.Bot b in dead)
                {
                    _botOnLavaSince.Remove(b);
                    try { b.kill(null, 0); }
                    catch (Exception e) { Log.write(TLog.Warning, "[Eol] Lava kill failed for a bot: {0}", e.Message); }
                }

            // forget bots that are gone
            if (_botOnLavaSince.Count > 0)
            {
                List<Bots.Bot> gone = null;
                foreach (Bots.Bot b in _botOnLavaSince.Keys)
                    if (b == null || b.IsDead || b.bCondemned)
                        (gone ?? (gone = new List<Bots.Bot>())).Add(b);
                if (gone != null)
                    foreach (Bots.Bot b in gone)
                        _botOnLavaSince.Remove(b);
            }
        }
    }
}
