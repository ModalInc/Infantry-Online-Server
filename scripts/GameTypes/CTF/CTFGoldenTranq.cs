using System;
using System.Collections.Generic;
using System.Linq;

using Assets;
using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_CTF
{
    /// <summary>
    /// posty's Golden Tranq (Travis, 2026-10-26: a championship reward, Mischief's "golden dart, sleep, sheep" idea). The skin itself is the item
    /// "Golden Tranq" (CTFSkins.cs catalogue, champion-flagged); this adds its three moments, all with art in hwcaw.blo:
    ///   tranq hit  every enemy a Golden Tranq stuns (antiEffectsRadius, the same test as CTFStunLock.cs) gets gold "Z z Z" rising over their head
    ///              ("Golden Zzz", a soft sleep sound); the stunned player sees it too
    ///   kill       a player who dies within SHEEP_WINDOW_MS of a Golden Tranq stun, whoever finishes them, lies down as a sleeping sheep that bursts
    ///              into gold wool ("Golden Sheep", a baa). The sheep (v2, 10-27) lies exactly on the corpse: its sprite has the corpse's 64 heading
    ///              rows, so it is sent with the VICTIM's heading at ground level and the client picks the row that matches the body
    ///   beacon     an owner who jumps back to their Teleport Beacon gets "HELLO THERE" in kill-text letters over where they land (not where
    ///              they place it: the server only hears the placing, the jump back is seen as the owner's position landing on the beacon)
    /// Only where champion items and item skins are both on (fxOn: off in league arenas, on Sundays, *skins off, *champItems off).
    /// Static, keyed by alias, so every arena shares it.
    /// </summary>
    public static class GoldenTranq
    {
        public const string SKIN = "Golden Tranq", ZZZ = "Golden Zzz", SHEEP = "Golden Sheep";
        private static readonly string[] Owners = { "posty", "Axidus" };   // the same owners as the CTFSkins.cs set (Axidus = staff test)
        private const int TELEPORT_BEACON = 5001;
        private const int SHEEP_WINDOW_MS = 5000;      // Mischief: a kill within 5 s of the tranq counts, so a teammate's finish does not eat it
        private const int FX_RANGE = 1600;             // the kill-text audience
        private const int STUN_MARGIN = 8;             // px on top of antiEffectsRadius, as CTFStunLock
        private const int BEACON_LIFE_MS = 60000;      // how long a placed beacon can be jumped back to when the item has no portalTime (5001: 60 s)
        private const int BEACON_SLACK_MS = 2000;      // the client's own beacon clock started a little before the server heard the placing
        private const int HELLO_LAND_PX = 112;         // landed this close to a beacon spot (5001's accuracyRadius is 64) ...
        private const int HELLO_FROM_PX = 256;         // ... straight from at least this far away in one step: the jump back, not a run
        private static readonly Dictionary<string, int> _stunnedAt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // victim -> tick
        private static readonly Dictionary<string, Beacon> _beacons = new Dictionary<string, Beacon>(StringComparer.OrdinalIgnoreCase);   // owner -> placed beacon

        /// <summary>An owner's placed Teleport Beacon: where it was used, until when it can be jumped back to, and where the owner was last seen.</summary>
        private sealed class Beacon
        {
            public int Expires, LastX, LastY;
            public readonly List<int[]> Spots = new List<int[]>();
        }

        public static bool IsOwner(Player p)
        {
            return p != null && Owners.Any(o => o.Equals(p._alias, StringComparison.OrdinalIgnoreCase));
        }

        /// <param name="yaw">the heading to draw the effect with (0-239); -1 = the sender's own (Golden Zzz). The sheep passes the victim's.</param>
        private static void Show(Arena arena, string itemName, Player by, short x, short y, short z, int yaw = -1)
        {
            ItemInfo fx = AssetManager.Manager.getItemByName(itemName);
            if (fx == null || by == null)
                return;
            var shot = new SC_Projectile { projectileID = (short)fx.id, playerID = (ushort)by._id, posX = x, posY = y, posZ = z, yaw = yaw >= 0 ? (byte)yaw : by._state.yaw };
            foreach (Player p in arena.getPlayersAndSpecInRange(x, y, FX_RANGE))
                p._client.sendReliable(shot);
        }

        /// <summary>Player.Explosion: a Golden Tranq blast puts the Zzz over every enemy it stuns and starts their sheep window.</summary>
        public static void OnExplosion(Arena arena, Player from, ItemInfo.Projectile wep, short x, short y, bool fxOn)
        {
            if (!fxOn || from == null || from._team == null || wep == null || !SKIN.Equals(wep.name, StringComparison.OrdinalIgnoreCase))
                return;
            List<Player> near = arena.getPlayersInRange(x, y, wep.antiEffectsRadius + STUN_MARGIN);
            if (near == null)
                return;
            int now = Environment.TickCount;
            foreach (Player p in near)
            {
                if (p == null || p == from || p.IsSpectator || p.IsDead || p._team == null || p._team == from._team)
                    continue;
                _stunnedAt[p._alias] = now;
                Show(arena, ZZZ, from, p._state.positionX, p._state.positionY, p._state.positionZ);
            }
        }

        /// <summary>Player.Death: died within the window of a Golden Tranq stun = the sheep, whoever the killer.</summary>
        public static void OnDeath(Arena arena, Player victim, Player killer, bool fxOn)
        {
            int at;
            if (victim == null || !_stunnedAt.TryGetValue(victim._alias, out at))
                return;
            _stunnedAt.Remove(victim._alias);
            if (fxOn && unchecked(Environment.TickCount - at) <= SHEEP_WINDOW_MS)
                Show(arena, SHEEP, killer ?? victim, victim._state.positionX, victim._state.positionY, 0, victim._state.yaw);   // on the corpse: its heading, ground level
        }

        /// <summary>Player.WarpItem: an owner places a Teleport Beacon (a Portal warp item). The server hears only the placing; the jump back
        /// happens in the client and arrives as a position jump, which Poll greets. A second use while one is live is kept as another spot, so
        /// the landing is found whether the client treats a re-press as the jump back or as a new beacon.</summary>
        public static void OnWarpItem(Player p, ItemInfo item, bool fxOn)
        {
            if (!fxOn || item == null || item.id != TELEPORT_BEACON || !IsOwner(p))
                return;
            int now = Environment.TickCount, x = p._state.positionX, y = p._state.positionY;
            var warp = item as ItemInfo.WarpItem;
            Beacon b;
            if (!_beacons.TryGetValue(p._alias, out b) || unchecked(now - b.Expires) > 0)
                _beacons[p._alias] = b = new Beacon { LastX = x, LastY = y };
            if (b.Spots.Count >= 3)
                b.Spots.RemoveAt(0);
            b.Spots.Add(new[] { x, y });
            b.Expires = now + (warp != null && warp.portalTime > 0 ? warp.portalTime * 10 : BEACON_LIFE_MS) + BEACON_SLACK_MS;   // portalTime is in 1/100 s
        }

        /// <summary>From the arena poll: an owner whose position lands on one of their beacon spots straight from HELLO_FROM_PX or more away
        /// has just jumped back to it - "HELLO THERE" over where they land, once. Beacons are forgotten on death, spec or when they run out.</summary>
        public static void Poll(Arena arena, bool fxOn)
        {
            if (_beacons.Count == 0)
                return;
            int now = Environment.TickCount;
            foreach (var kv in _beacons.ToList())
            {
                Beacon b = kv.Value;
                bool old = unchecked(now - b.Expires) > 0;
                Player p = arena.Players.FirstOrDefault(pl => pl._alias.Equals(kv.Key, StringComparison.OrdinalIgnoreCase));
                if (p == null)
                {
                    if (old) _beacons.Remove(kv.Key);   // left, or in another arena whose poll watches them
                    continue;
                }
                if (old || !fxOn || p.IsDead || p.IsSpectator)
                {
                    _beacons.Remove(kv.Key);
                    continue;
                }
                int x = p._state.positionX, y = p._state.positionY, fromX = b.LastX, fromY = b.LastY;
                if (x == fromX && y == fromY)
                    continue;
                b.LastX = x;
                b.LastY = y;
                if (!b.Spots.Any(s => Dist2(x, y, s) <= (long)HELLO_LAND_PX * HELLO_LAND_PX && Dist2(fromX, fromY, s) >= (long)HELLO_FROM_PX * HELLO_FROM_PX))
                    continue;
                _beacons.Remove(kv.Key);
                ExplosionHelper.ShowPhrase(arena, p, "HELLO THERE", (short)x, (short)(y - 28), p._state.positionZ);
            }
        }

        private static long Dist2(int x, int y, int[] spot)
        {
            long dx = x - spot[0], dy = y - spot[1];
            return dx * dx + dy * dy;
        }
    }
}
