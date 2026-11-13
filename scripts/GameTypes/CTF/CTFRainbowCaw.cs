using System;
using System.Collections.Generic;
using System.Linq;

using Assets;
using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_CTF
{
    /// <summary>
    /// Rainbow CAW kill effect (Travis 10-27): a player killed with a Rainbow CAW gets a rainbow drawn over the corpse, from one foot on the
    /// ground up and over to the other ("Rainbow Arc", display only, art hwcaw RcawArc). Every Rainbow CAW look counts (the classic one,
    /// Stream, Prism). Off with the arena's item skins (*skins off, league arenas).
    ///   Bullets report their explosions, so the last round each shooter landed is remembered; a death whose killer's last round was a
    ///   rainbow one within KILL_WINDOW_MS gets the arc (the victim's death and the shooter's hit come from two clients, either may arrive first).
    /// </summary>
    public static class RainbowCaw
    {
        public const string ARC = "Rainbow Arc";
        private static readonly string[] Rounds = { "Rainbow CAW-START", "Rainbow CAW-STREAM", "Rainbow CAW-PRISM", "Rainbow CAW-CLASSIC" };   // CLASSIC = the local test item
        private const int KILL_WINDOW_MS = 1500;
        private const int FX_RANGE = 1600;             // players this close see it (the screen is ~1000 px wide)
        private static readonly Dictionary<string, int> _lastRainbow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // shooter -> tick

        /// <summary>Player.Explosion: remember a rainbow round; any other weapon's round ends it (the kill was not the rainbow's)</summary>
        public static void OnExplosion(Player from, ItemInfo.Projectile wep)
        {
            if (from == null || wep == null)
                return;
            if (Rounds.Any(r => r.Equals(wep.name, StringComparison.OrdinalIgnoreCase)))
                _lastRainbow[from._alias] = Environment.TickCount;
            else
                _lastRainbow.Remove(from._alias);
        }

        /// <summary>Player.Death: the killer's last round was a rainbow one -> the arc over the body</summary>
        public static void OnDeath(Arena arena, Player victim, Player killer, bool fxOn)
        {
            int at;
            if (!fxOn || arena == null || victim == null || killer == null || killer == victim || !_lastRainbow.TryGetValue(killer._alias, out at))
                return;
            if (unchecked(Environment.TickCount - at) > KILL_WINDOW_MS)
                return;
            ItemInfo fx = AssetManager.Manager.getItemByName(ARC);
            if (fx == null)
                return;
            short x = victim._state.positionX, y = victim._state.positionY;
            var shot = new SC_Projectile { projectileID = (short)fx.id, playerID = (ushort)killer._id, posX = x, posY = y, posZ = 0, yaw = 0 };
            foreach (Player p in arena.getPlayersAndSpecInRange(x, y, FX_RANGE))
                p._client.sendReliable(shot);
        }
    }
}
