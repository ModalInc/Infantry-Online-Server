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

namespace InfServer.Script.GameType_BHX
{   // Script Class
    /// Provides the interface between the script and bot
    ///////////////////////////////////////////////////////
    public partial class Scientist : Bot
    {
        public float pursueDist = 0.6f;
        public float shortDist = 0.4f;                     //The distance from the player where we keep our distance
        public float minMeleeDist = 1.1f;                        //The distance from the player where we cut him down!
        public float minRangedAttack = 2.5f;                  //The distance from the player where we blast him with SG!
        public float patrolDist = 0.2f;                     //The distance from our patrol points where we turn around and pursue the other point
        public float fireDist = 5.8f;                      //The distance from the player where we use our rifle
        private const int c_MaxPath = 350;
        public const int c_DistanceLeeway = 500;

        public const int c_pathUpdateInterval = 4000;      //The amount of ticks before a bot will renew it's path
        public const int c_MaxRespawnDist = 1500;           //The maximum distance bots can be spawned from the players
        public const int c_playerMaxRangeEnemies = 2100;    //The max range the bot will consider enemies a threat
        public const int c_playerRangeFire = 1000;          //The range the bot will consider firing at enemies
        public int c_maxEnergy = 250;                 //Our max/starting Energy
        public const int c_energyRechargeRate = 100;        //Our recharge rate 
    }
}
