using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;


using InfServer.Game;
using InfServer.Protocol;
using InfServer.Scripting;
using InfServer.Bots;

using Assets;
using Axiom.Math;
using Bnoerj.AI.Steering;

namespace InfServer.Script.GameType_Eol
{   // Script Class
    /// Provides the interface between the script and bot
    ///////////////////////////////////////////////////////
    public partial class RoamingCaptain : Bot
    {
        public float farDist = 1.4f;
        public float runDist = 0.28f;
        public float shortDist = 0.40f;
        public float patrolDist = 1.1f;                     //The distance from our patrol points where we turn around and pursue the other point
        public float retreatDist = 6.1f;                     //The distance from our patrol points where we turn around and pursue the other point
        public float fireDist = 1.6f;                      //Hug and dump shots
        private const int c_MaxPath = 350;
        public const int c_DistanceLeeway = 500;

        public const int c_pathUpdateInterval = 22000;      //The amount of ticks before a bot will renew it's path
        public const int c_MaxRespawnDist = 1500;           //The maximum distance bots can be spawned from the players
        public const int c_playerMaxRangeEnemies = Script_Eol.EolBotCombatSeekRangeRaw;
        public const int c_playerRangeFire = Script_Eol.EolBotCombatFireRangeRaw;
    }
}
