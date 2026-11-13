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
{ 
    static public class Configuration
    {
        public enum BotType
        {
            Arachnomorph = 203,
            Brute = 202,
            Electromorph = 201,
            Rampager = 204,
            Warrior = 200,
            Scientist = 105,
            Queen = 106,
            Arachnid = 207
        }
    }
}
