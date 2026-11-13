using System;
using System.Collections.Generic;

using Assets;
using InfServer.Bots;
using InfServer.Protocol;

namespace InfServer.Game
{
    /// <summary>
    /// Arena-wide bot sense data: shots in flight (for dodging) and the per-human set of bots that
    /// get the full-rate pose stream.
    /// </summary>
    public abstract partial class Arena
    {
        sealed class TrackedShot
        {
            public Team Team;
            public object Owner;
            public double X, Y;     // world px (map y / 0.7)
            public double Vx, Vy;   // world px per 10 ms tick
            public int Born, Until;
            public double Reach;    // body this close to the shot's line is hit (blast + body)
        }

        const int MaxTrackedShots = 160;
        const double InboundRangeRaw = 520.0;
        readonly List<TrackedShot> _trackedShots = new List<TrackedShot>(MaxTrackedShots);
        readonly Dictionary<int, double> _shotReachByItem = new Dictionary<int, double>();

        /// <summary>
        /// Record a projectile fired at (x, y) facing yaw so bots on other teams can dodge it.
        /// Called for player, bot and turret fire; no-op when dodging is off or there are no bots.
        /// </summary>
        /// <summary>Gunfire "noise" for curious bots (team, owner, x, y) - local 2026-11-14; set by the gametype.</summary>
        public static Action<Team, object, short, short> GunfireNoise;

        public void NoteProjectileFired(Team team, object owner, short x, short y, byte yaw, int itemId)
        {
            try { GunfireNoise?.Invoke(team, owner, x, y); }
            catch { /* script hook must never break routing */ }

            if (!BotFeatures.Dodge || itemId <= 0 || _bots.Count == 0)
                return;

            ItemInfo it = _server?._assets?.getItemByID(itemId);
            bool multi = false;
            if (it is ItemInfo.MultiUse mu && mu.childItems != null && mu.childItems.Count > 0)
            {
                multi = mu.childItems.Count > 1;
                it = _server._assets.getItemByID(mu.childItems[0].id);
            }
            if (!(it is ItemInfo.Projectile p) || p.muzzleVelocity <= 0)
                return;

            double reach;
            lock (_shotReachByItem)
            {
                if (!_shotReachByItem.TryGetValue(p.id, out reach))
                {
                    int blast = 0;
                    try { blast = Helpers.getMaxBlastRadius(p); } catch (Exception) { }
                    reach = blast + 14.0;
                    _shotReachByItem[p.id] = reach;
                }
            }

            double r = yaw * 1.5 * Math.PI / 180.0;
            double v = Math.Max(1.0, p.muzzleVelocity / 1000.0);
            int now = Environment.TickCount;
            TrackedShot s = new TrackedShot
            {
                Team = team,
                Owner = owner,
                X = x,
                Y = y / 0.7,
                Vx = Math.Sin(r) * v,
                Vy = -Math.Cos(r) * v,
                Born = now,
                Until = now + (int)Math.Min(1500.0, Math.Max(150.0, Math.Abs(p.aliveTime) * 10.0)),
                Reach = reach + (multi ? 12.0 : 0.0),
            };

            lock (_trackedShots)
            {
                if (_trackedShots.Count >= MaxTrackedShots)
                    _trackedShots.RemoveAt(0);
                _trackedShots.Add(s);
            }
        }

        /// <summary>
        /// Enemy shots near <paramref name="bot"/>: 6 doubles each (x, y relative to the bot in world px,
        /// vx, vy per tick, reach, ticks left). Returns how many were written.
        /// </summary>
        internal int CollectInboundShots(Bot bot, int now, double[] buf, int max)
        {
            int n = 0;
            if (bot?._state == null || _trackedShots.Count == 0)
                return 0;

            double bx = bot._state.positionX;
            double by = bot._state.positionY / 0.7;
            double lim2 = InboundRangeRaw * InboundRangeRaw;
            lock (_trackedShots)
            for (int i = _trackedShots.Count - 1; i >= 0; i--)
            {
                TrackedShot s = _trackedShots[i];
                if (now - s.Until > 0)
                {
                    _trackedShots.RemoveAt(i);
                    continue;
                }
                if (n >= max || ReferenceEquals(s.Owner, bot))
                    continue;
                if (s.Team != null && s.Team == bot._team)
                    continue;

                double t0 = (now - s.Born) / 10.0;
                double sx = s.X + s.Vx * t0 - bx;
                double sy = s.Y + s.Vy * t0 - by;
                if (sx * sx + sy * sy > lim2)
                    continue;

                int o = n * 6;
                buf[o] = sx;
                buf[o + 1] = sy;
                buf[o + 2] = s.Vx;
                buf[o + 3] = s.Vy;
                buf[o + 4] = s.Reach;
                buf[o + 5] = (s.Until - now) / 10.0;
                n++;
            }
            return n;
        }

        readonly HashSet<ushort> _fastRoutedBots = new HashSet<ushort>();
        readonly List<KeyValuePair<long, ushort>> _fastRouteScratch = new List<KeyValuePair<long, ushort>>(64);
        int _tickFastRouteRefresh;
        const int FastRouteRefreshMs = 100;

        /// <summary>True when this bot is among some human's nearest <see cref="BotFeatures.RouteNearCap"/> bots.</summary>
        internal bool IsBotFastRouted(ushort botId, int now)
        {
            if (BotFeatures.RouteNearCap <= 0)
                return true;
            if (_tickFastRouteRefresh == 0 || now - _tickFastRouteRefresh >= FastRouteRefreshMs || now - _tickFastRouteRefresh < 0)
                RefreshFastRoutedBots(now);
            return _fastRoutedBots.Contains(botId);
        }

        void RefreshFastRoutedBots(int now)
        {
            _tickFastRouteRefresh = now == 0 ? 1 : now;
            _fastRoutedBots.Clear();
            int cap = BotFeatures.RouteNearCap;
            long range = GetBotHighRateViewRange();
            long r2 = range * range;

            foreach (Player p in Players)
            {
                Helpers.ObjectState v = p?._state;
                if (v == null)
                    continue;
                _fastRouteScratch.Clear();
                foreach (Bot b in _bots)
                {
                    if (b == null || b.IsDead || b._state == null)
                        continue;
                    long dx = b._state.positionX - v.positionX;
                    long dy = b._state.positionY - v.positionY;
                    long d = dx * dx + dy * dy;
                    if (d <= r2)
                        _fastRouteScratch.Add(new KeyValuePair<long, ushort>(d, b._id));
                }
                if (_fastRouteScratch.Count > cap)
                    _fastRouteScratch.Sort((a, c) => a.Key.CompareTo(c.Key));
                for (int i = 0; i < _fastRouteScratch.Count && i < cap; i++)
                    _fastRoutedBots.Add(_fastRouteScratch[i].Value);
            }
        }
    }
}
