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
    public partial class BasicDefense : Bot
    {
        public float farDist = 1.2f;                        //Close immediately — combat bands own real stand
        public float runDist = 0.28f;                       //Almost never peel
        public float shortDist = 0.40f;                     //Knife-fight envelope
        public float patrolDist = 1.1f;                     //The distance from our patrol points where we turn around and pursue the other point
        public float retreatDist = 6.0f;                     //The distance from our patrol points where we turn around and pursue the other point
        public float fireDist = 1.4f;                       //Shoot while hugging
        private const int c_MaxPath = 300;
        public const int c_DistanceLeeway = 500;

        public const int c_pathUpdateInterval = 22000;      //The amount of ticks before a bot will renew it's path
        public const int c_playerMaxRangeEnemies = Script_Eol.EolBotCombatSeekRangeRaw;
        public const int c_playerRangeFire = Script_Eol.EolBotCombatFireRangeDefenseRaw;
        /// <summary>Legacy absolute HP threshold — backpedal now uses 20% of vehicle Hitpoints.</summary>
        public const int DefenseBackpedalHealth = 12;
    }
}
