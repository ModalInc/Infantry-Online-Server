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
{
    public partial class AlienHorde : Bot
    {
        public float farDist = 1.4f;
        public float runDist = 0.28f;
        public float shortDist = 0.40f;
        public float patrolDist = 1.1f;
        public float retreatDist = 6.0f;
        public float fireDist = 1.6f;
        private const int c_MaxPath = 300;
        public const int c_DistanceLeeway = 500;

        public const int c_pathUpdateInterval = 22000;
        public const int c_playerMaxRangeEnemies = Script_Eol.EolBotCombatSeekRangeRaw;
        public const int c_playerRangeFire = Script_Eol.EolBotCombatFireRangeRaw;

        /// <summary>Alien-only: reuse hostile lists briefly (match global cadence so packs don't "forget" fights).</summary>
        public const int AlienHostileSenseReuseMs = 160;
        /// <summary>Keep fireAtEnemy queued briefly so sticky combat does not flap to patrol every poll.</summary>
        public const int AlienCombatFireBurstPolls = 20;
        /// <summary>Throttle full-vehicle scan for crashed ship anchor lookup.</summary>
        public const int AlienVhqRescanMs = 500;
    }
}
