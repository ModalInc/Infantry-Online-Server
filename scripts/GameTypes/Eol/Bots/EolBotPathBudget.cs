using InfServer.Game;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// Soft caps on async pathfinder use so idle/patrol/unstick work does not starve combat
    /// chase routes (main source of multi-second "frozen bot" stalls online).
    /// </summary>
    internal static class EolBotPathBudget
    {
        /// <summary>Above this, skip new idle/roam/flag path requests (steer fallback instead).</summary>
        public const int IdleQueueSoftCap = 24;

        /// <summary>Above this, combat force-repaths slow down and may defer.</summary>
        public const int CombatQueueSoftCap = 40;

        /// <summary>
        /// At/above this, refuse new path enqueues entirely.
        /// Must stay below Pathfinder work-queue soft-drop (~160) / capacity (200 for NavMesh).
        /// </summary>
        public const int HardQueueCap = 56;

        public static int QueueCount(Arena arena)
        {
            return arena?.BotPathQueueCount ?? 0;
        }

        public static bool AllowIdlePath(Arena arena)
        {
            return QueueCount(arena) < IdleQueueSoftCap;
        }

        public static bool AllowCombatPath(Arena arena)
        {
            return QueueCount(arena) < HardQueueCap;
        }

        public static bool AllowUnstickPath(Arena arena)
        {
            return QueueCount(arena) < CombatQueueSoftCap;
        }

        /// <summary>
        /// Force-repath interval while LOS is broken. Staggers by bot id and stretches when the queue is hot.
        /// </summary>
        public static int CombatLosRepathIntervalMs(Arena arena, int botId)
        {
            int baseMs = EolBotCombatUtil.LosLostRepathIntervalMs;
            int stagger = (botId & 7) * 110;
            int q = QueueCount(arena);
            if (q >= CombatQueueSoftCap)
                return baseMs + stagger + 1600;
            if (q >= IdleQueueSoftCap)
                return baseMs + stagger + 800;
            return baseMs + stagger;
        }
    }
}
