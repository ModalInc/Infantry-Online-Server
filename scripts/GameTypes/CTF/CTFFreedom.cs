using System;
using System.Collections.Generic;
using System.Linq;

using Assets;
using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_CTF
{
    /// <summary>
    /// Freedom kill reward (Travis 10-27, the Freedom skin set - CTFSkins.cs): a player killed with a Freedom item (the Freedom AR, the Freedom
    /// RG or its holder) gets a bald eagle that swoops in, plants a flag in the body and flies off; the flag waves, then dissolves into
    /// white stars ("Freedom Flag", display only, art freedom.blo FrdFlag, 2.4 s). Off with the arena's item skins (*skins off, league arenas).
    ///   The shooter's client reports where each round went off (Player.Explosion) and the victim's client reports the death (Player.Death);
    ///   either may arrive first, so both orders are matched within a short window. Explosions of what the Freedom items spawn themselves
    ///   (the RG's shrapnel and damage-event rounds, the AR's casing) don't end a Freedom streak; any other weapon's explosion does.
    ///   One flag per death, sent to players and spectators within FX_RANGE (one packet each).
    /// </summary>
    public static class Freedom
    {
        public const string FLAG = "Freedom Flag";
        private static readonly string[] Shots = { "Freedom AR- Main", "Freedom RG", "Freedom RG Holder" };
        private static readonly string[] Guns = { "Freedom AR" };
        private const int KILL_WINDOW_MS = 1500;   // a Freedom round this recent from the killer counts
        private const int LATE_HIT_MS = 600;       // a death reported before the killer's round: wait this long for it
        private const int FX_RANGE = 1600;          // players this close see it (the screen is ~1000 px wide)

        private static HashSet<int> _shots, _spawned;
        private static readonly Dictionary<string, int> _lastShot = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // shooter -> tick
        private class Pending { public Arena Arena; public short X, Y; public int Tick; public ushort Killer; }
        private static readonly Dictionary<string, Pending> _pending = new Dictionary<string, Pending>(StringComparer.OrdinalIgnoreCase);   // killer -> a death waiting

        /// <summary>the Freedom projectiles, and everything they or the Freedom guns spawn (explodeItem trees, multi-use children)</summary>
        private static void Resolve()
        {
            if (_shots != null) return;
            var shots = new HashSet<int>(); var spawned = new HashSet<int>();
            Action<int, int> walk = null;
            walk = (id, depth) =>
            {
                if (id <= 0 || depth > 4 || !spawned.Add(id)) return;
                ItemInfo it = AssetManager.Manager.getItemByID(id);
                var pr = it as ItemInfo.Projectile; var mu = it as ItemInfo.MultiUse;
                if (pr != null) walk(pr.explodeItem, depth + 1);
                if (mu != null) foreach (var ch in mu.childItems) walk(ch.id, depth + 1);
            };
            foreach (string n in Shots)
            {
                var pr = AssetManager.Manager.getItemByName(n) as ItemInfo.Projectile;
                if (pr == null) continue;
                shots.Add(pr.id); walk(pr.explodeItem, 1);
            }
            foreach (string n in Guns)
            {
                var mu = AssetManager.Manager.getItemByName(n) as ItemInfo.MultiUse;
                if (mu != null) foreach (var ch in mu.childItems) walk(ch.id, 1);
            }
            spawned.ExceptWith(shots);
            _spawned = spawned; _shots = shots;
        }

        /// <summary>Player.Explosion: remember a Freedom round (and pay out a death that came first); another weapon's round ends it</summary>
        public static void OnExplosion(Player from, ItemInfo.Projectile wep)
        {
            if (from == null || wep == null)
                return;
            Resolve();
            if (_shots.Contains(wep.id))
            {
                int now = Environment.TickCount;
                _lastShot[from._alias] = now;
                Pending p;
                if (_pending.TryGetValue(from._alias, out p))
                {
                    _pending.Remove(from._alias);
                    if (unchecked(now - p.Tick) <= LATE_HIT_MS)
                        Show(p.Arena, p.X, p.Y, p.Killer);
                }
            }
            else if (!_spawned.Contains(wep.id))
            {
                _lastShot.Remove(from._alias);
                _pending.Remove(from._alias);
            }
        }

        /// <summary>Player.Death: the killer's last round was a Freedom one -> the eagle and the flag over the body (or wait briefly for that round)</summary>
        public static void OnDeath(Arena arena, Player victim, Player killer, bool fxOn)
        {
            if (!fxOn || arena == null || victim == null || killer == null || killer == victim)
                return;
            int at, now = Environment.TickCount;
            short x = victim._state.positionX, y = victim._state.positionY;
            if (_lastShot.TryGetValue(killer._alias, out at) && unchecked(now - at) <= KILL_WINDOW_MS)
                Show(arena, x, y, (ushort)killer._id);
            else
                _pending[killer._alias] = new Pending { Arena = arena, X = x, Y = y, Tick = now, Killer = (ushort)killer._id };
        }

        private static void Show(Arena arena, short x, short y, ushort killerId)
        {
            ItemInfo fx = AssetManager.Manager.getItemByName(FLAG);
            if (fx == null || arena == null)
                return;
            var shot = new SC_Projectile { projectileID = (short)fx.id, playerID = killerId, posX = x, posY = y, posZ = 0, yaw = 0 };
            foreach (Player p in arena.getPlayersAndSpecInRange(x, y, FX_RANGE))
                p._client.sendReliable(shot);
        }
    }
}
