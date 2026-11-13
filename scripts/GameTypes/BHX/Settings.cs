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
        public int s_maxHives = 2;
        public int s_hiveSpawnDelay = 1000;

        public const int s_hiveMinSpawnDist = 200;
        public const int s_hiveMaxSpawnDist = 30000;
        public const short s_hiveAreaRadius = 50;
        public const int s_hiveDensityRadius = 1000;
        public const int s_skrallMinimumDistance = 1500;
        //SupplyDrops
        public const int s_supply_openRadius = 150;
    }
}
