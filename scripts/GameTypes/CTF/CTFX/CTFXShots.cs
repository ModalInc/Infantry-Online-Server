// CTF:X server-side bots - PROJECTILE FLIGHT. Every client draws a bot's shot from the bot's routed update: it leaves the
// barrel along the routed yaw at the item's muzzle velocity and bursts where it touches a body, a wall or the floor, or when
// its aliveTime runs out. No client applies damage to a bot, so the server flies the same projectile here and lands the
// damage, knockback and stun at that moment and place. (Travis 10-11: bots took damage before the shots landed - the old
// code applied every bot-vs-bot hit at the instant of firing, which the client knockback made plain to see.)
//
// Client rules ported (cfs-studio projectileSimulation.ts + WeaponTestStrip.tsx, retail RE):
//  - 1 tick = 10 ms. muzzleVelocity / 1000 = px per tick in WORLD space; a position's map y moves 7/10 of the world y
//    velocity. Yaw Y heads (sin(1.5 Y deg), -cos(1.5 Y deg)) in world space (0 = north, 60 = east).
//  - The shot leaves BarrelLength ahead of the body at FireHeight + startHeightAdjust. Rotation spread (low..high rotation
//    angle, integer degrees, wrap-aware) is an offset from the heading; MultiUse children add their deltaAngle and start at
//    deltaX (right) / deltaY (forward). The fire angle (low..high, -1000 = 0) tips the shot up: horizontal speed x cos,
//    vertical x sin. inheritedSpeed (per mille) adds the firer's velocity (< 0: only its part along the shot).
//  - Per tick: age; past |aliveTime| it is gone (aliveTime < 0 bursts there); gravity (1 = the level default -25) moves z and
//    the floor bursts it or bounces it (floorBounce*); horizontal friction; move; a blocked end or x-corner tile bursts it
//    (bounceCount -1 passes walls); then the body test.
//  - Body test: the shot point inside a body's TriggerRadius, y difference un-squished (x 10/7), z inside the body's
//    LowZ..HighZ band (a degenerate band = no z gate). A shot is inert for its first inactiveTime ticks. proximityRadius > 0
//    acquires a body within TriggerRadius + proximityRadius and bursts at closest approach (under 12: at once).
//  - Burst: distance = max(0, |dx, dy x 10/7| - TriggerRadius); the touched body takes it at 0. explodeItem chains spawn
//    at the burst (3 deep). Damage follows the item's damageMode against the firer's team; the firer itself is spared.
// Not modelled: wall bounces (a bounce bursts at the wall), tile heights (grenades do not ride wall tops), homing. Pellet
// spread is rolled here, not from the retail spread token, so single pellets can differ from what a client draws.

using System;
using System.Collections.Generic;

using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_CTF
{
    /// <summary>One trigger pull of a bot: how many of its damaging projectiles there are, how many are still flying, how many
    /// touched a body. Every projectile flies and lands on its own; the volley only counts them.</summary>
    internal sealed class CTFXVolley
    {
        public int Item, Leaves, Live, Hits;
        public double Range;                // firer to its target at the trigger pull (px), for the logged checks
    }

    /// <summary>One flying projectile leaf (a Projectile item, or one child of a MultiUse) fired by a bot.</summary>
    internal sealed class CTFXShot
    {
        public CTFXVolley Volley;           // the trigger pull it belongs to (null for explodeItem chain children)
        public bool Touched;                // it burst on (or next to, proximity) a body
        public bool FromTurret;             // fired by a turret at a bot (CTFXTurrets.Fight), or a chain child of such a shot
        public int RotSpeed;                // current rotational speed (thousandths of a yaw unit per tick), see Tick
        public CTFXBot Owner;           // the firer (may die or respawn while the shot flies; the shot still lands)
        public Team Team;               // the firer's team at fire time
        public ItemInfo.Projectile P;
        public double X, Y, Z;          // map px (y as on the map) and height
        public double Vx, Vy, Vz;       // WORLD px per tick (the map y moves 7/10 of Vy)
        public int T;                   // Environment.TickCount this shot is simulated up to
        public int Age, Life, Bounces, Depth, WallBounces;
        public bool Inactive, GravOff;
        public CTFXBot Prox;            // proximity target being tracked to its closest approach
        public double ProxPrev;
        public int Born;                // fire time and point, for the logged flight checks
        public double Sx, Sy;
    }

    internal sealed class CTFXShots
    {
        const double IsoY = 0.7;
        const int MaxLive = 4000, MaxDepth = 3, MaxNest = 4, LevelGravity = -25;

        readonly CTFXBotManager _mgr;
        readonly Random _rng;
        readonly List<CTFXShot> _live = new List<CTFXShot>(512);
        readonly List<CTFXBot> _hit = new List<CTFXBot>(16);
        internal int Fired, Direct, Splashed, Walls, Floors, Expired, Dropped, TeamHits, Logged, TurretHits;

        internal CTFXShots(CTFXBotManager mgr)
        {
            _mgr = mgr;
            _rng = mgr.Rng;
        }

        internal int Live { get { return _live.Count; } }

        /// <summary>
        /// Travis 10-14 night: mines "should adhere to the projectile limitations like max alive at any given time and the category code
        /// max alive (if bullet mine has 5 laid, it doesn't allow for 1 AP mine)". The item's maxLivePerPlayer / PerTeam / PerLevel count
        /// every live shot of the same maxLiveCategoryCode (or of the same item when the code is 0), as the client counts them.
        /// </summary>
        internal bool MayLay(CTFXBot owner, int itemId)
        {
            ItemInfo.Projectile p = AssetManager.Manager.getItemByID(itemId) as ItemInfo.Projectile;
            if (p == null) return true;
            int cat = p.maxLiveCategoryCode;
            int mine = 0, team = 0, all = 0;
            foreach (CTFXShot s in _live)
            {
                if (s.Depth != 0 || s.P == null) continue;
                bool same = cat != 0 ? s.P.maxLiveCategoryCode == cat : s.P.id == p.id;
                if (!same) continue;
                all++;
                if (s.Team == owner._team) team++;
                if (s.Owner == owner) mine++;
            }
            if ((p.maxLivePerPlayer > 0 && mine >= p.maxLivePerPlayer) || (p.maxLivePerTeam > 0 && team >= p.maxLivePerTeam) || (p.maxLivePerLevel > 0 && all >= p.maxLivePerLevel))
            { CapRefusals++; return false; }
            return true;
        }
        internal int CapRefusals, WallBounceCount;

        /// <summary>The mines this bot has lying in the world right now (MiniTP's mine rule keeps two or three out and lays another when one goes off).</summary>
        internal int LiveMinesOf(CTFXBot owner)
        {
            int n = 0;
            foreach (CTFXShot s in _live)
                if (s.Depth == 0 && s.Owner == owner && s.P != null && CTFXBot.IsMine(s.P.id)) n++;
            return n;
        }

        internal void Clear() { _live.Clear(); }

        /// <summary>A bot's routed shot: <paramref name="itemId"/> leaves the barrel of <paramref name="b"/> as of its current state.</summary>
        internal void Fire(CTFXBot b, int itemId, double range)
        {
            ItemInfo it = AssetManager.Manager.getItemByID(itemId);
            if (it == null) return;
            double h = b._state.yaw * 1.5 * Math.PI / 180.0;
            double bl = b._type.BarrelLength;
            double x = b._state.positionX + Math.Sin(h) * bl, y = b._state.positionY - Math.Cos(h) * bl * IsoY;
            _volley = new CTFXVolley();
            _volley.Item = itemId; _volley.Range = range;
            Spawn(it, b, b._team, x, y, b._type.FireHeight, h, b._state.velocityX / 1000.0, b._state.velocityY / 1000.0, 0.0,
                0, 0, Environment.TickCount);
            _volley = null;
        }

        CTFXVolley _volley;                 // the trigger pull being spawned (Fire); chain children belong to none
        bool _turret;                       // the shot being spawned comes from a turret (FireFrom)

        /// <summary>A turret's routed shot (CTFXTurrets.Fight): its gun leaves the barrel along fire angle <paramref name="angle"/>.</summary>
        internal void FireFrom(Computer c, Team team, int itemId, byte angle)
        {
            ItemInfo it = AssetManager.Manager.getItemByID(itemId);
            if (it == null) return;
            double h = angle * 1.5 * Math.PI / 180.0, bl = c._type.BarrelLength;
            double x = c._state.positionX + Math.Sin(h) * bl, y = c._state.positionY - Math.Cos(h) * bl * IsoY;
            _turret = true;
            Spawn(it, null, team, x, y, c._type.FireHeight, h, 0.0, 0.0, 0.0, 0, 0, Environment.TickCount);
            _turret = false;
        }

        void Spawn(ItemInfo it, CTFXBot owner, Team team, double x, double y, double z, double h, double pvx, double pvy, double pvz,
            int depth, int nest, int now)
        {
            ItemInfo.Projectile p = it as ItemInfo.Projectile;
            if (p != null) { if (!Cosmetic(p)) Launch(p, owner, team, x, y, z, h, pvx, pvy, pvz, depth, now); return; }
            ItemInfo.MultiUse mu = it as ItemInfo.MultiUse;
            if (mu == null || mu.childItems == null || nest >= MaxNest) return;
            double fx = Math.Sin(h), fy = -Math.Cos(h), rx = -fy, ry = fx;     // forward and right, world space
            foreach (ItemInfo.MultiUse.ChildItem c in mu.childItems)
            {
                if (c.id <= 0) break;                                           // retail stops at the first empty slot
                ItemInfo ci = AssetManager.Manager.getItemByID(c.id);
                if (ci == null) continue;
                double cx = x + c.deltaY * fx + c.deltaX * rx, cy = y + (c.deltaY * fy + c.deltaX * ry) * IsoY;
                Spawn(ci, owner, team, cx, cy, z + c.deltaZ, h + c.deltaAngle * Math.PI / 180.0, pvx, pvy, pvz, depth, nest + 1, now);
            }
        }

        void Launch(ItemInfo.Projectile p, CTFXBot owner, Team team, double x, double y, double z, double h, double pvx, double pvy,
            double pvz, int depth, int now)
        {
            if (_live.Count >= MaxLive) { Dropped++; return; }
            double rot = h + Roll(p.lowRotationAngle, p.highRotationAngle, true) * Math.PI / 180.0;
            int lf = p.lowFireAngle == -1000 ? 0 : p.lowFireAngle, hf = p.highFireAngle == -1000 ? 0 : p.highFireAngle;
            double fire = Roll(lf, hf, false) * Math.PI / 180.0;
            double s = p.muzzleVelocity / 1000.0, dx = Math.Sin(rot), dy = -Math.Cos(rot);
            double vx = s * dx * Math.Cos(fire), vy = s * dy * Math.Cos(fire), vz = s * Math.Sin(fire);
            if (p.inheritedSpeed > 0) { double k = p.inheritedSpeed / 1000.0; vx += pvx * k; vy += pvy * k; }
            else if (p.inheritedSpeed < 0)
            {
                double k = -p.inheritedSpeed / 1000.0, dot = pvx * dx + pvy * dy;
                vx += dx * dot * k; vy += dy * dot * k;
            }
            if (p.inheritZSpeed != 0) vz += pvz * Math.Abs(p.inheritZSpeed) / 1000.0;
            CTFXShot sh = new CTFXShot();
            sh.Owner = owner; sh.Team = team; sh.P = p;
            sh.X = x; sh.Y = y; sh.Z = z + p.startHeightAdjust;
            sh.Vx = vx; sh.Vy = vy; sh.Vz = vz;
            sh.T = now; sh.Born = now; sh.Sx = x; sh.Sy = y;
            sh.Life = Math.Abs(p.aliveTime);
            sh.Bounces = Math.Abs(p.floorBounceCount);
            sh.Inactive = p.inactiveTime != 0;
            sh.Depth = depth;
            sh.RotSpeed = p.rotationalSpeed;
            sh.FromTurret = _turret;
            if (_volley != null) { sh.Volley = _volley; _volley.Leaves++; _volley.Live++; }
            _live.Add(sh);
            Fired++;
        }

        static readonly Dictionary<int, bool> _cosmetic = new Dictionary<int, bool>();

        /// <summary>
        /// A leaf that can do nothing to a body: no damage of any modelled type, no anti-effect, no knockback, no explode chain. The
        /// shell casing a rifle ejects sideways is one; it is drawn by the clients and not flown here.
        /// </summary>
        bool Cosmetic(ItemInfo.Projectile p)
        {
            bool c;
            if (_cosmetic.TryGetValue(p.id, out c)) return c;
            c = p.kineticDamageInner <= 0 && p.kineticDamageOuter <= 0 && p.explosiveDamageInner <= 0 && p.explosiveDamageOuter <= 0
                && p.electronicDamageInner <= 0 && p.electronicDamageOuter <= 0 && p.psionicDamageInner <= 0 && p.psionicDamageOuter <= 0
                && p.bypassDamageInner <= 0 && p.bypassDamageOuter <= 0 && p.energyDamageInner <= 0 && p.energyDamageOuter <= 0
                && (p.antiEffectsRadius <= 0 || (p.antiEffectsFire <= 0 && p.antiEffectsRotate <= 0 && p.antiEffectsThrust <= 0 && p.antiEffectsRecharge <= 0))
                && p.explodeItem <= 0 && !_mgr.Knock.ContainsKey(p.id);
            _cosmetic[p.id] = c;
            return c;
        }

        /// <summary>Integer degrees in [lo, hi); a rotation range with hi below lo wraps through 0 (358..2 = a 4 degree cone).</summary>
        int Roll(int lo, int hi, bool wrap)
        {
            if (wrap && hi < lo) hi += 360;
            return hi > lo ? _rng.Next(lo, hi) : lo;
        }

        /// <summary>Fly every live shot up to <paramref name="now"/> in 10 ms ticks.</summary>
        internal void Step(int now)
        {
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                if (i >= _live.Count) continue;
                CTFXShot s = _live[i];
                bool alive = true;
                int n = 0;
                while (alive && now - (s.T + 10) >= 0)
                {
                    s.T += 10;
                    alive = Tick(s);
                    if (++n >= 60 && alive) { s.T = now; break; }       // a stalled server never replays more than 0.6 s
                }
                if (!alive)
                {
                    int last = _live.Count - 1;
                    _live[i] = _live[last];
                    _live.RemoveAt(last);
                    EndLeaf(s);
                }
            }
        }

        // ---- volley tally: per multi-projectile weapon, how many of a trigger pull's projectiles touched a body
        readonly Dictionary<int, int[]> _tally = new Dictionary<int, int[]>();     // item -> volleys with none / some / all touching
        readonly Dictionary<int, int> _tallyLeaves = new Dictionary<int, int>();
        int _volleyLogged;

        void EndLeaf(CTFXShot s)
        {
            CTFXVolley v = s.Volley;
            if (v == null) return;
            if (s.Touched) v.Hits++;
            if (--v.Live > 0 || v.Leaves < 2) return;
            int[] t;
            if (!_tally.TryGetValue(v.Item, out t)) { t = new int[3]; _tally[v.Item] = t; }
            t[v.Hits == 0 ? 0 : (v.Hits < v.Leaves ? 1 : 2)]++;
            _tallyLeaves[v.Item] = v.Leaves;
            if (_volleyLogged < 8 && v.Range > 0)
            {
                _volleyLogged++;
                _mgr.WarnOnce("volley check #" + _volleyLogged + ": item " + v.Item + " fired " + v.Leaves + " projectiles at a target " + (int)v.Range
                    + " px away: " + v.Hits + " touched a body, " + (v.Leaves - v.Hits) + " did not");
            }
        }

        /// <summary>Stats: the multi-projectile weapons' volleys by how many of their projectiles touched a body.</summary>
        internal string VolleyTally()
        {
            if (_tally.Count == 0) return "";
            List<KeyValuePair<int, int[]>> l = new List<KeyValuePair<int, int[]>>(_tally);
            l.Sort(delegate(KeyValuePair<int, int[]> a, KeyValuePair<int, int[]> b) { return (b.Value[0] + b.Value[1] + b.Value[2]).CompareTo(a.Value[0] + a.Value[1] + a.Value[2]); });
            System.Text.StringBuilder sb = new System.Text.StringBuilder(" | volleys (none/some/all of their projectiles touched a body):");
            for (int i = 0; i < l.Count && i < 5; i++)
                sb.Append(' ').Append(l[i].Key).Append('x').Append(_tallyLeaves[l[i].Key]).Append(' ')
                  .Append(l[i].Value[0]).Append('/').Append(l[i].Value[1]).Append('/').Append(l[i].Value[2]);
            return sb.ToString();
        }

        bool Tick(CTFXShot s)
        {
            ItemInfo.Projectile p = s.P;
            s.Age++;
            if (s.Inactive && p.inactiveTime > 0 && s.Age >= p.inactiveTime) s.Inactive = false;
            if (s.Age > s.Life && s.Prox == null)
            {
                Expired++;
                if (p.aliveTime < 0) Burst(s, null);
                return false;
            }
            // rotational steering (retail 0x460303, before the move): between rotationalStartTime and rotationalEndTime the velocity
            // turns by the current rotational speed (thousandths of a yaw unit of 1.5 deg per tick), which then changes by the
            // acceleration and stops at zero instead of reversing. The micro missiles swing out and back this way.
            if (p.rotationalEndTime > 0 && s.Age >= p.rotationalStartTime && s.Age < p.rotationalEndTime)
            {
                if (s.RotSpeed != 0)
                {
                    double a = s.RotSpeed / 1000.0 * 1.5 * Math.PI / 180.0, c = Math.Cos(a), sn = Math.Sin(a);
                    double rvx = s.Vx * c - s.Vy * sn, rvy = s.Vx * sn + s.Vy * c;
                    s.Vx = rvx; s.Vy = rvy;
                }
                if (p.rotationalAcceleration != 0)
                {
                    int prev = s.RotSpeed;
                    s.RotSpeed += p.rotationalAcceleration;
                    if (prev != 0 && Math.Sign(s.RotSpeed) != Math.Sign(prev)) s.RotSpeed = 0;
                }
            }
            int g = p.gravityAcceleration == 1 ? LevelGravity : p.gravityAcceleration;
            if (g != 0 && !s.GravOff)
            {
                s.Vz += g / 1000.0;
                s.Z += s.Vz;
                if (s.Z < 0)
                {
                    s.Z = -s.Z;
                    s.Vz = -s.Vz * p.floorBounceVerticalSpeed / 1000.0;
                    s.Vx = s.Vx * p.floorBounceHorizontalSpeed / 1000.0;
                    s.Vy = s.Vy * p.floorBounceHorizontalSpeed / 1000.0;
                    if (s.Bounces <= 0 && p.floorBounceCount >= 0) { Floors++; Burst(s, null); return false; }
                    if (s.Bounces <= 0) { s.Vx = 0; s.Vy = 0; s.Vz = 0; s.GravOff = true; }
                    s.Bounces--;
                    if (s.Inactive && p.inactiveTime == -1) s.Inactive = false;
                }
            }
            if (p.horizontalFriction != 10000)
            {
                double f = p.horizontalFriction / 10000.0;
                s.Vx *= f; s.Vy *= f;
            }
            double nx = s.X + s.Vx, ny = s.Y + s.Vy * IsoY;
            if (p.bounceCount > 0 && s.WallBounces < p.bounceCount && (_mgr.BlockedPx((int)nx, (int)s.Y) || _mgr.BlockedPx((int)s.X, (int)ny)))
            {   // 10-15: a wall bounce (the Plasma Cannon bounces twice): reflect off the blocked axis and carry on (Travis: "they bounce off
                // walls ... surprising bank shots or get around well defended chokes")
                bool bx = _mgr.BlockedPx((int)nx, (int)s.Y), by = _mgr.BlockedPx((int)s.X, (int)ny);
                if (bx) s.Vx = -s.Vx;
                if (by || !bx) s.Vy = -s.Vy;
                s.WallBounces++;
                WallBounceCount++;
                return true;
            }
            if (p.bounceCount != -1 && (_mgr.BlockedPx((int)nx, (int)s.Y) || _mgr.BlockedPx((int)nx, (int)ny)))
            {
                Walls++;
                Burst(s, null);                                     // at the last open point
                return false;
            }
            s.X = nx; s.Y = ny;
            if (s.Inactive) return true;
            return !Contact(s);
        }

        bool Target(CTFXBot t, CTFXShot s)
        {
            return t != null && t != s.Owner && !t.IsDead && !t.Released && t._team != s.Team;
        }

        static bool InBand(double z, CTFXBot t)
        {
            int lo = t._type.LowZ, hi = t._type.HighZ;
            return hi <= lo || (z >= lo && z <= hi);
        }

        /// <summary>The body test after a move. True = the shot burst. (Bursts happen after the body loop: a burst walks the bodies too.)</summary>
        bool Contact(CTFXShot s)
        {
            double prox = s.P.proximityRadius;
            if (s.Prox != null)
            {
                CTFXBot t = s.Prox;
                if (!Target(t, s)) s.Prox = null;
                else
                {
                    double e = Edge(s, t);
                    if (e <= 0 || e > s.ProxPrev) { s.Touched = true; Burst(s, null); return true; }     // closest approach passed
                    s.ProxPrev = e;
                    return false;
                }
            }
            CTFXBot hit = null, near = null;
            double nearE = 0;
            foreach (CTFXBot t in _mgr.LiveBots)
            {
                if (!Target(t, s)) continue;
                double dx = s.X - t._state.positionX, dy = (s.Y - t._state.positionY) / IsoY;
                double r = t._type.TriggerRadius, d2 = dx * dx + dy * dy;
                bool band = InBand(s.Z, t);
                if (band && d2 < r * r) { hit = t; break; }
                if (prox > 0 && near == null && d2 < (r + prox) * (r + prox))
                {
                    double e = Math.Max(0.0, Math.Sqrt(d2) - r);
                    double dz = band ? 0.0 : (s.Z < t._type.LowZ ? t._type.LowZ - s.Z : s.Z - t._type.HighZ);
                    e = Math.Sqrt(e * e + dz * dz);
                    if (e <= prox) { near = t; nearE = e; }
                }
            }
            if (hit != null) { Direct++; s.Touched = true; Burst(s, hit); return true; }
            if (near == null)
                foreach (Computer c in _mgr.Turrets.Live)
                {
                    if (!Body(c, s)) continue;
                    double dx = s.X - c._state.positionX, dy = (s.Y - c._state.positionY) / IsoY, r = c._type.TriggerRadius;
                    int lo = c._type.LowZ, hi = c._type.HighZ;
                    if (dx * dx + dy * dy < r * r && (hi <= lo || (s.Z >= lo && s.Z <= hi))) { Direct++; TurretHits++; s.Touched = true; Burst(s, null); return true; }
                }
            if (near != null)
            {
                if (prox < 12) { s.Touched = true; Burst(s, null); return true; }
                s.Prox = near; s.ProxPrev = nearE;                              // track it to the closest approach
            }
            return false;
        }

        /// <summary>A computer the shot can touch: not the firer's team, not neutral, destroyable or armed, alive.</summary>
        static bool Body(Computer c, CTFXShot s)
        {
            return !c.IsDead && c._team != s.Team && c._team != null && c._team._id != -1 && (c._type.Destroyable != 0 || c._primaryGun != null);
        }

        static double Edge(CTFXShot s, CTFXBot t)
        {
            double dx = s.X - t._state.positionX, dy = (s.Y - t._state.positionY) / IsoY;
            return Math.Sqrt(dx * dx + dy * dy) - t._type.TriggerRadius;
        }

        /// <summary>The burst: damage, knockback and anti-effects on every bot in reach, then the explodeItem chain.</summary>
        internal List<CTFXShot> LiveShots { get { return _live; } }
        internal int Repelled;                  // shots shoved by a repulsor burst

        /// <summary>A burst with explosionRecoilWeapon (Repulsor Charge 10000 over 272 px) shoves every other live shot within its recoil
        /// radius straight away from it (radius > 0: full force inside; < 0: falling off to the edge); 10000 = 4 px per tick.</summary>
        void Repel(CTFXShot s)
        {
            ItemInfo.Projectile p = s.P;
            int r = Math.Abs(p.explosionRecoilRadius);
            if (p.explosionRecoilWeapon == 0 || r <= 0) return;
            foreach (CTFXShot o in _live)
            {
                if (o == s || o.Life <= 0) continue;
                double dx = o.X - s.X, dy = (o.Y - s.Y) / IsoY, d = Math.Sqrt(dx * dx + dy * dy);
                if (d > r || d < 0.5) continue;
                double f = Math.Abs(p.explosionRecoilWeapon) / 2500.0 * (p.explosionRecoilRadius < 0 ? 1.0 - d / r : 1.0);
                o.Vx += dx / d * f; o.Vy += dy / d * f;
                Repelled++;
            }
        }

        /// <summary>Knockback calibration: humans this burst pushes (their own clients apply it) are watched for 1.5 s (CTFXBots.WatchHumanKnock).</summary>
        void WatchHumans(CTFXShot s, ItemInfo.Projectile p)
        {
            CTFXBotManager.KnockInfo k;
            if (_mgr.KbLogged >= 150 || !_mgr.Knock.TryGetValue(p.id, out k)) return;
            int r = Math.Abs(k.Radius);
            if (r <= 0) return;
            foreach (Player h in _mgr.Arena.PlayersIngame)
            {
                if (h == null || h.IsDead || h._state == null || !Affects(p.damageMode, s.Team, h._team)) continue;
                double dx = h._state.positionX - s.X, dy = h._state.positionY - s.Y, d = Math.Sqrt(dx * dx + dy * dy);
                if (d > r) continue;
                double falloff = k.Radius > 0 ? 1.0 : Math.Max(0.0, 1.0 - d / r);
                _mgr.WatchHumanKnock(h, p.id, (int)d, k.Force * falloff * (10000.0 / 1500.0) * _mgr.KnockScale / 100.0, k.IgnoreMs);
            }
        }

        void Burst(CTFXShot s, CTFXBot direct)
        {
            ItemInfo.Projectile p = s.P;
            Repel(s);
            int reach = Math.Max(Helpers.getMaxBlastRadius(p), Math.Max(Math.Abs(p.explosionRecoilRadius), p.antiEffectsRadius));
            _hit.Clear();
            foreach (CTFXBot t in _mgr.LiveBots)
            {
                if (t == s.Owner || t.IsDead || t.Released) continue;
                if (t != direct && reach <= 0) continue;
                if (!Affects(p.damageMode, s.Team, t._team)) continue;
                if (t == direct || Edge(s, t) <= reach) _hit.Add(t);
            }
            bool owner = s.Owner != null && !s.Owner.Released;
            for (int i = 0; i < _hit.Count; i++)
            {
                CTFXBot t = _hit[i];
                double edge = t == direct ? 0.0 : Math.Max(0.0, Edge(s, t));
                int hp = t._state.health;
                t.HitBy = s.Owner;
                t.TakeBlast(edge, p, null, t == direct);
                if (t._state.health < hp || t.IsDead)
                {   // kill credit (and bounty) goes to the last hit that took health: a beamer draining energy or a Tranq stun after the
                    // killer's shot must not steal it (Travis 10-13: the hunt pack serves the kill to its killer)
                    if (owner) t.LastHitBy = s.Owner;
                    if (owner && s.Owner != null && s.Owner.Cloaked) _mgr.CloakedHit(t, Environment.TickCount);   // hit by something it could not see
                    t.LastHitItem = s.Volley != null ? s.Volley.Item : 0;
                    if (owner && s.Volley != null && _mgr.Usage != null) _mgr.Usage.Hit(s.Volley.Item);
                    t.LastHitByTurret = s.FromTurret;
                    if (t != direct) Splashed++;
                    if (t._team == s.Team) TeamHits++;
                    if (s.FromTurret && t.IsDead) _mgr.Turrets.Kills++;
                }
                if (!t.IsDead)
                {
                    t.Knockback(p.id, (int)s.X, (int)s.Y, s.Vx, s.Vy * IsoY);
                    double dx = t._state.positionX - s.X, dy = t._state.positionY - s.Y;
                    t.ApplyAnti(p, t == direct ? 0.0 : Math.Sqrt(dx * dx + dy * dy));      // the touched body: a hit on it
                }
            }
            WatchHumans(s, p);
            int blast = Helpers.getMaxBlastRadius(p);
            if (blast > 0)
                foreach (Computer c in _mgr.Turrets.Live)
                {
                    if (!Body(c, s) || !Affects(p.damageMode, s.Team, c._team)) continue;
                    double dx = s.X - c._state.positionX, dy = (s.Y - c._state.positionY) / IsoY, lim = blast + c._type.TriggerRadius;
                    if (dx * dx + dy * dy > lim * lim) continue;
                    try { c.applyExplosion(null, (int)s.X, (int)s.Y, p); } catch (Exception) { }     // engine damage, death and update
                }
            if (direct != null)
            {
                _mgr.AddHotSpot((int)s.X, (int)s.Y);
                if (Logged < 5)
                {
                    Logged++;
                    double fx = s.X - s.Sx, fy = (s.Y - s.Sy) / IsoY;
                    _mgr.WarnOnce("shot check #" + Logged + ": item " + p.id + " flew " + (int)Math.Sqrt(fx * fx + fy * fy) + " px (world) in "
                        + (s.T - s.Born) + " ms at " + (p.muzzleVelocity / 1000.0).ToString("F1") + " px/tick, burst on a "
                        + direct.Style + " bot " + (direct.IsDead ? "(killed)" : "(" + direct._state.health + " hp left)"));
                }
            }
            if (p.explodeItem > 0 && s.Depth < MaxDepth)
            {
                ItemInfo ei = AssetManager.Manager.getItemByID(p.explodeItem);
                if (ei != null)
                {
                    double h = Math.Atan2(s.Vx, -s.Vy);                 // travel heading, same convention as the yaw
                    _turret = s.FromTurret;
                    Spawn(ei, s.Owner, s.Team, s.X, s.Y, s.Z, h, s.Vx, s.Vy, s.Vz, s.Depth + 1, 0, s.T);
                    _turret = false;
                }
            }
        }

        /// <summary>damageMode: 0 / 1 = everyone, 2 = enemies only, 3 / 4 = the firer's team only.</summary>
        static bool Affects(int mode, Team firer, Team target)
        {
            if (mode == 2) return target != firer;
            if (mode == 3 || mode == 4) return target == firer;
            return true;
        }

        /// <summary>Attach log: how each kit weapon flies (leaves, speed, how far it gets, splash, damage mode) and the bodies' hit shapes.</summary>
        internal static string Report(IEnumerable<int> weapons, IEnumerable<int> bodies)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder("flight:");
            foreach (int id in weapons)
            {
                ItemInfo it = AssetManager.Manager.getItemByID(id);
                List<ItemInfo.Projectile> leaves = new List<ItemInfo.Projectile>();
                Leaves(it, leaves, 0);
                if (leaves.Count == 0) { sb.Append(' ').Append(id).Append("=no projectile"); continue; }
                ItemInfo.Projectile p = leaves[0];
                double s = p.muzzleVelocity / 1000.0;
                int g = p.gravityAcceleration == 1 ? LevelGravity : p.gravityAcceleration;
                sb.Append(' ').Append(id).Append('=').Append(leaves.Count).Append('x').Append(s.ToString("F1")).Append("px/t");
                if (g != 0) sb.Append(" lob ").Append(LobLand(id)).Append("px");
                else sb.Append(" reach ").Append((int)(s * Math.Abs(p.aliveTime))).Append("px");
                sb.Append(" blast ").Append(Helpers.getMaxBlastRadius(p)).Append(" mode ").Append(p.damageMode);
                if (p.proximityRadius > 0) sb.Append(" prox ").Append(p.proximityRadius);
                if (p.explodeItem > 0) sb.Append(" chain ").Append(p.explodeItem);
            }
            sb.Append(" | bodies (trigger r, z band, fire height, barrel):");
            foreach (int id in bodies)
            {
                VehInfo v = AssetManager.Manager.getVehicleByID(id);
                if (v == null) continue;
                sb.Append(' ').Append(id).Append('=').Append(v.TriggerRadius).Append('/').Append(v.LowZ).Append("..").Append(v.HighZ)
                  .Append('/').Append(v.FireHeight).Append('/').Append(v.BarrelLength);
            }
            return sb.ToString();
        }

        static readonly Dictionary<int, int> _land = new Dictionary<int, int>();

        /// <summary>
        /// World px from the firer to where item <paramref name="id"/> comes down (first leaf, middle fire angle, fired from the bot
        /// bodies' FireHeight 34 at rest), 0 when it is not a lobbed weapon. The landing spot is fixed: a lob cannot be aimed shorter.
        /// </summary>
        internal static int LobLand(int id)
        {
            int d;
            if (_land.TryGetValue(id, out d)) return d;
            d = 0;
            List<ItemInfo.Projectile> leaves = new List<ItemInfo.Projectile>();
            Leaves(AssetManager.Manager.getItemByID(id), leaves, 0);
            if (leaves.Count > 0)
            {
                ItemInfo.Projectile p = leaves[0];
                int g = p.gravityAcceleration == 1 ? LevelGravity : p.gravityAcceleration;
                if (g != 0)
                {
                    int lf = p.lowFireAngle == -1000 ? 0 : p.lowFireAngle, hf = p.highFireAngle == -1000 ? 0 : p.highFireAngle;
                    double s = p.muzzleVelocity / 1000.0, a = (lf + hf) / 2.0 * Math.PI / 180.0;
                    double vz = s * Math.Sin(a), vh = s * Math.Cos(a), z = 34 + p.startHeightAdjust, run = 0;
                    int life = Math.Max(1, Math.Abs(p.aliveTime));
                    for (int t = 0; t < life && t < 3000; t++)
                    {
                        vz += g / 1000.0; z += vz;
                        if (z < 0) break;
                        vh *= p.horizontalFriction / 10000.0;
                        run += vh;
                    }
                    d = Math.Max(1, (int)run);
                }
            }
            _land[id] = d;
            return d;
        }

        static void Leaves(ItemInfo it, List<ItemInfo.Projectile> into, int nest)
        {
            if (it == null || nest >= MaxNest) return;
            ItemInfo.Projectile p = it as ItemInfo.Projectile;
            if (p != null) { into.Add(p); return; }
            ItemInfo.MultiUse mu = it as ItemInfo.MultiUse;
            if (mu == null || mu.childItems == null) return;
            foreach (ItemInfo.MultiUse.ChildItem c in mu.childItems)
            {
                if (c.id <= 0) break;
                Leaves(AssetManager.Manager.getItemByID(c.id), into, nest + 1);
            }
        }
    }
}
/* PARSER GUARD - keep last */
