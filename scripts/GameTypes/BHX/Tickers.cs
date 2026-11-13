using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

using InfServer.Logic;
using InfServer.Game;
using InfServer.Scripting;
using InfServer.Bots;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_BHX
{   // Script Class
    /// Provides the interface between the script and arena
    ///////////////////////////////////////////////////////
    public partial class GameType_BHX : Scripts.IScript
    {
        public int _lastTickerUpdate;
        public int _scientistsRescued;

        public void updateTickers(int now)
        {
            if (now - _lastTickerUpdate < 1000)
                return;

            _lastTickerUpdate = now;

            int totalObj = _objectives.Count;
            int rescued = 0;

            foreach (Objective obj in _objectives)
                if (obj.bObjectiveRescued)
                    rescued++;

            _scientistsRescued = rescued;

            _arena.setTicker(0, 3, 0, String.Format("{0}/{1} Scientists Rescued | {2} Skrall Active", rescued, totalObj, _botHandler._skrall.Count));

            //Personal Scores
            _arena.setTicker(0, 2, 0, delegate (Player p)
            {
                //Update their ticker
                if (_savedPlayerStats.ContainsKey(p._alias))
                    return string.Format("HP={0}          Personal Score: Kills={1} - Deaths={2}",
                        p._state.health,
                        _savedPlayerStats[p._alias].kills,
                        _savedPlayerStats[p._alias].deaths);
                return "";
            });

            _arena.setTicker(0, 4, 0, String.Format("[Debug] Skrall Count={0}", _botHandler._skrall.Count));
        }
    }
}
