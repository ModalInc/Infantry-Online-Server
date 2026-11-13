// MiniTP profile - the manager / bot halves that need the partial classes' private members (groups, init, per-bot state).
// The brain itself is MiniTP\CTFXMini.cs. Reached only when the manager's profile is MiniTP (Mini != null).
/* PARSER GUARD: keep a block comment after the last line comment */

using System;
using System.Collections.Generic;

using InfServer.Game;

using Assets;

namespace InfServer.Script.GameType_CTF
{
    internal sealed partial class CTFXBot
    {
        internal MiniBot Mini;        // MiniTP state (null in the CTF:X profile)
        internal bool Still { get { return IsStill(); } }                                      // under 16 px/s: an engineer's build waits for it (the maker's prefire is cancelled by movement)
        internal int AddTox(int n) { if (_kitCharges < 0) return 0; int take = Math.Max(0, Math.Min(100 - _kitCharges, n)); _kitCharges += take; return take; }      // Titanium Oxide picked up (an engineer carries up to 100)
        internal int AddTso(int n) { if (_medCharges < 0) return 0; int take = Math.Max(0, Math.Min(150 - _medCharges, n)); _medCharges += take; return take; }      // Tsolvy picked up (a medic carries up to 150)
        internal int TsoUnits { get { return _medCharges; } }
        internal bool SpendTox(int n) { if (_kitCharges < n) return false; _kitCharges -= n; return true; }     // Titan Oxide units (the engineer carries up to 100)
        internal int MiniReach { get { return UsableReach(); } }                     // its longest usable weapon reach (px)
        internal bool MiniCanHurtTurret { get { return CanHurtTurret(); } }           // the kit holds a gun that hurts a turret
        double ShooterF(Player p, CTFXBot o) { return Mini != null ? Mgr.Mini.ShooterFactor(this, p, o) : 1.0; }
        static readonly Dictionary<int, bool> _stunKnock = new Dictionary<int, bool>();
        /// <summary>The item stuns (move / turn / fire lock inside an anti-effects radius) or knocks back (an entry in the knockback table).</summary>
        bool StunsOrKnocks(int id)
        {
            bool r;
            if (_stunKnock.TryGetValue(id, out r)) return r;
            ItemInfo.Projectile pr = AssetManager.Manager.getItemByID(id) as ItemInfo.Projectile;
            r = Mgr.Knock.ContainsKey(id) || (pr != null && pr.antiEffectsRadius > 0 && (pr.antiEffectsThrust > 0 || pr.antiEffectsRotate > 0 || pr.antiEffectsFire > 0));
            _stunKnock[id] = r;
            return r;
        }
        // ---- mines (Travis 11-08): a build's mines ride at the end of the kit; the weapon pick never fires them, the MiniTP mine rule lays them
        static readonly Dictionary<int, bool> _mineIds = new Dictionary<int, bool>();
        internal static bool IsMine(int id)
        {
            if (id == CTFXBounty.ApMine || id == CTFXBounty.BulletMine || id == CTFXBounty.PlasmaMine || id == DemoPack) return true;
            lock (_mineIds)
            {
                bool r;
                if (_mineIds.TryGetValue(id, out r)) return r;
                ItemInfo.Projectile p = AssetManager.Manager.getItemByID(id) as ItemInfo.Projectile;
                r = p != null && p.name != null && p.name.IndexOf("mine", StringComparison.OrdinalIgnoreCase) >= 0;
                _mineIds[id] = r;
                return r;
            }
        }

        /// <summary>A mine can be laid by this seat: its build holds one (not dry, not refused by the body) whose damage mode spares the layer's own team (mode 2 = enemies only).</summary>
        internal bool MiniHasMine
        {
            get
            {
                if (_kit == null) return false;
                for (int i = 0; i < _kit.Length; i++) if (IsMine(_kit[i].Id) && _kit[i].Id != DemoPack && (_badMask & (1 << i)) == 0 && !Dry(i) && MineSafe(_kit[i].Id)) return true;
                return false;
            }
        }

        /// <summary>Mines whose damage mode hurts the layer's own team too (mode 0 / 1 = everyone: the bullet, plasma and AP mines of this zone) are laid only when this is on. Off: only enemy-only mines (mode 2: EMP, grapeshot) are laid, so a defender's mine never drops a teammate or a human of its own team.</summary>
        internal static bool MineFriendly = false;

        static bool MineSafe(int id)
        {
            ItemInfo.Projectile p = AssetManager.Manager.getItemByID(id) as ItemInfo.Projectile;
            return p != null && (p.damageMode == 2 || MineFriendly);
        }

        int _mineRot;
        /// <summary>Lay the next mine of the kit along yaw (the mine stays where it leaves the barrel). False = nothing to lay / still turning / a cap or energy refused it.</summary>
        internal bool MiniLayMine(int now, float yaw)
        {
            if (_kit == null || IsDead || InDropship || Stunned(now)) return false;
            int n = _kit.Length;
            for (int k = 0; k < n; k++)
            {
                int i = (_mineRot + k) % n, id = _kit[i].Id;
                if (!IsMine(id) || id == DemoPack || !MineSafe(id)) continue;
                if (LayMine(now, id, yaw)) { _mineRot = i + 1; return true; }
            }
            return false;
        }

        /// <summary>Mines of this bot still lying in the world (the shot sim keeps them until they go off or expire).</summary>
        internal int MiniLiveMines { get { return Mgr.Shots.LiveMinesOf(this); } }

        // ---- the infiltrator's cloak (Travis 11-08): on while it sneaks through the enemy base, off the moment it fights; it pays its energy drain while it is on (the active gear's recharge)
        bool _cloakOn;
        int _cloakCheckAt, _cloakQuietSince;
        internal bool CloakOn { get { return _cloakOn; } }
        internal bool CloakOwned { get { return _cloak != null; } }

        void CloakSet(bool on)
        {
            if (_cloak == null || _activeEquip == null) return;
            _cloakOn = on;
            if (on && !_activeEquip.Contains(_cloak)) _activeEquip.Add(_cloak);
            else if (!on) _activeEquip.Remove(_cloak);
        }

        /// <summary>MiniTP cloak: sneaking = in the enemy base with nothing on its hands. A shot fired, a hit taken, a target or low energy drop it; it comes back after a quiet spell. Tier flaws:
        /// a rookie forgets to put it on (ForgetOn), and sometimes leaves it on while it fights.</summary>
        void CloakStep(int now)
        {
            if (_cloak == null || Mgr.Mini == null) return;
            if (IsDead || InDropship) { if (_cloakOn) CloakSet(false); return; }
            if (now - _cloakCheckAt < 0) return;
            _cloakCheckAt = now + 300 + _rng.Next(300);
            MiniBot mb = Mini;
            string tier = Human != null ? Human.Tier : null;
            double en = _energyMax > 0 ? _energy / _energyMax : 1.0;
            bool sneaking = mb != null && mb.Phase == MiniBot.Raid;
            bool fighting = HasTarget || (_hitAt != 0 && now - _hitAt < 3500) || (_lastShotTick != 0 && now - _lastShotTick < 2500);
            if (_cloakOn)
            {
                if (en < 0.20 || !sneaking) { CloakSet(false); _cloakQuietSince = 0; Mgr.Mini.CloakOffs++; return; }
                if (fighting && _rng.NextDouble() >= Mgr.Mini.ForgetOff(tier)) { CloakSet(false); _cloakQuietSince = now + 3500 + _rng.Next(3000); Mgr.Mini.CloakOffs++; }      // a flaw: sometimes it keeps it on through the fight
            }
            else if (sneaking && !fighting && en >= 0.45)
            {
                if (_cloakQuietSince == 0) _cloakQuietSince = now + 1500;
                if (now - _cloakQuietSince < 0) return;
                if (_rng.NextDouble() < Mgr.Mini.ForgetOn(tier)) { _cloakQuietSince = now + 6000 + _rng.Next(8000); return; }       // forgot to switch it on this time
                CloakSet(true); _cloakQuietSince = 0; Mgr.Mini.CloakOns++;
            }
        }

        /// <summary>MiniTP: the carrier-first factor on a candidate's score (CTFXMini.CarrierFactor): an enemy human holding a flag is rated far nearer than he is.</summary>
        double CarrierF(Player p) { return Mini != null && p != null ? Mgr.Mini.CarrierFactor(p) : 1.0; }

        /// <summary>MiniTP holder facing: toward (wx, wy) with the anchors' slow sweep of +-amp yaw units at its own pace (CTFXCommand.HoldYaw did the same for CTF:X anchors).</summary>
        internal double FaceErrorDeg(int wx, int wy) { return Math.Abs(YawDelta(YawTo(wx - _state.positionX, wy - _state.positionY), _state.yaw)) * 1.5; }

        internal float WatchToward(int wx, int wy, int now, int periodMs, float amp)
        {
            double dx = wx - _state.positionX, dy = wy - _state.positionY;
            return WrapYaw(YawTo(dx, dy) + (float)(amp * Math.Sin(now / (double)periodMs * 2.0 * Math.PI)));
        }

        internal int KitCharges { get { return _kitCharges; } }      // repair-kit charges left this life (engineer); -1 = not stocked yet
        internal int GraceUntil;      // warp grace: no damage until this tick (set after every scripted warp, CTFXMini)

        // ---- toggled utilities with human flaws (Travis 11-07): a Teleport Disruptor / generator that is ON costs energy regen every second; players forget to turn
        // them on when it matters and forget to turn them off afterwards, and not everyone owns them - all by skill tier (CTFXMini.Owns / TdOwn / GenOwn / EsOwn / ForgetOn / ForgetOff)
        bool _tdOn, _tdFight, _tdForgot, _genFightRolled, _genForgot;
        int _tdCheckAt, _tdQuietSince, _genKeepUntil;
        internal double EnergyNow { get { return _energy; } }
        internal bool TdActive { get { return HasTd && (Mgr.Mini == null || _tdOn); } }
        internal int UtilFlags()
        {   // bit 1 generator owned, 2 on, 4 sensors carried, 8 on, 16 TD owned, 32 on
            int f = 0;
            if (_genU != null) f |= 1;
            if (_genOn) f |= 2;
            if (_esCarry) f |= 4;
            if (_esOn) f |= 8;
            if (HasTd) f |= 16;
            if (_tdOn) f |= 32;
            return f;
        }

        void TdSet(bool on)
        {
            ItemInfo.UtilityItem u = TdItem();
            if (u == null || _activeEquip == null) return;
            _tdOn = on;
            if (on && !_activeEquip.Contains(u)) _activeEquip.Add(u);
            else if (!on) _activeEquip.Remove(u);
        }

        /// <summary>MiniTP: the disruptor is switched on when the bot thinks of it (an enemy near, a fight, a hit) and off again when it is quiet - both with a chance of
        /// forgetting, so it sometimes sits on and drains energy, and sometimes is not on when the enemy warps.</summary>
        void TdStep(int now)
        {
            if (!HasTd || IsDead || InDropship) { if (_tdOn) TdSet(false); return; }
            if (now - _tdCheckAt < 0) return;
            _tdCheckAt = now + 400 + _rng.Next(400);
            string tier = Human != null ? Human.Tier : null;
            double en = _energyMax > 0 ? _energy / _energyMax : 1.0;
            bool threat = HasTarget || (_hitAt != 0 && now - _hitAt < 4000) || Mgr.Mini.FoeNear(this, 650.0);
            if (_tdOn)
            {
                if (en < 0.22) { TdSet(false); return; }                                    // it cannot carry the drain any longer
                if (threat) { _tdQuietSince = 0; return; }
                if (_tdQuietSince == 0) _tdQuietSince = now + 4000 + _rng.Next(6000);
                if (now - _tdQuietSince < 0) return;
                if (_rng.NextDouble() < Mgr.Mini.ForgetOff(tier)) { _tdQuietSince = now + 8000 + _rng.Next(20000); return; }   // forgot to switch it off: it keeps draining a while
                TdSet(false); _tdQuietSince = 0;
            }
            else if (threat && en >= 0.35)
            {
                if (!_tdFight) { _tdFight = true; _tdForgot = _rng.NextDouble() < Mgr.Mini.ForgetOn(tier); }     // decided once per scare: it forgets it this time or not
                if (!_tdForgot && _rng.NextDouble() < TierPick(0.5, 0.95)) TdSet(true);
            }
            else if (!threat) _tdFight = false;
        }
        internal List<int> MiniRoute { get { return _rp; } }      // the planned route (route-book node ids), read by CTFXMini.Steer
        internal int MiniRouteIdx { get { return _rpi; } }        // the waypoint it is heading for
    }

    internal sealed partial class CTFXBotManager
    {
        internal CTFXMini Mini;       // the MiniTP brain; null in the CTF:X profile

        internal void MiniLog(string line) { Out(line); }
        internal bool BuildGreenPortal(int hideId) { return _script.MiniTPBuildGreenPortal(hideId); }
        internal Vehicle EnemyPortal(int side) { return _script.MiniTPEnemyPortal(side == 1); }       // the SAME alive / own-region / nearer-the-flags rule the players' portal uses (side 0 Titan, 1 Collective)
        internal bool VictoryClock(out Team holder, out int msLeft) { return _script.MiniVictoryClock(out holder, out msLeft); }
        internal void FlagChanged(Arena.FlagState fs) { _script.MiniFlagChanged(fs); }
        internal bool BuildTurret(string kind, int tx, int ty, int side) { return _script.MiniBuildTurret(kind, tx, ty, side); }

        // ---------------------------------------------------------------- fill-to-N: humans take seats from the bots
        // A team plays N seats; every human on a team stands one bot down (the lowest-value seat first: surplus infantry, then JT / Infiltrator /
        // heavies, the Engineer, the Medic, and the Squad Leader last), and a bot comes back when he leaves (the most important seat first).
        int _fillAt, _miniPer = 14;
        internal int FillOut, FillBack;

        static int RemovalRank(string role)
        {
            switch (role)
            {
                case "OINF": return 0; case "DINF": return 1; case "JT": return 2; case "INFIL": return 3;
                case "OHVY": return 4; case "DHVY": return 5; case "ENG": return 6; case "MED": return 7; case "SL": return 8;
            }
            return 0;
        }

        void MiniFillStep(int now)
        {
            if (Mini == null || !_on || now - _fillAt < 2500) return;
            _fillAt = now;
            for (int side = 0; side < 2; side++)
            {
                Team team = ResolveTeam(side == 0 ? "Titan" : "Collective");
                if (team == null) continue;
                int humans = 0;
                foreach (Player p in _arena.PlayersIngame) if (p._team == team) humans++;
                int want = Math.Max(0, _miniPer - humans);
                List<CTFXSlot> mine = new List<CTFXSlot>();
                foreach (CTFXBotGroup g in _groups)
                    if (g.Team == team) foreach (CTFXSlot s in g.Slots) if (!s.Retired || s.MiniOut) mine.Add(s);
                int live = 0;
                foreach (CTFXSlot s in mine) if (!s.MiniOut) live++;
                while (live > want)
                {
                    CTFXSlot pick = null; int best = int.MaxValue;
                    foreach (CTFXSlot s in mine) { if (s.MiniOut) continue; int r = RemovalRank(s.Role); if (r < best) { best = r; pick = s; } }
                    if (pick == null) break;
                    MiniBench(pick); live--; FillOut++;
                }
                while (live < want)
                {
                    CTFXSlot pick = null; int best = -1;
                    foreach (CTFXSlot s in mine) { if (!s.MiniOut) continue; int r = RemovalRank(s.Role); if (r > best) { best = r; pick = s; } }
                    if (pick == null) break;
                    pick.MiniOut = false; pick.Retired = false;
                    QueueSpawn(pick, now + 500 + Rng.Next(2000)); live++; FillBack++;
                }
            }
        }

        void MiniBench(CTFXSlot s)
        {
            s.MiniOut = true; s.Retired = true;
            CTFXBot b = GetBot(s.BotId);
            if (b != null) { _bots.Remove(b._id); try { b.destroy(false); } catch (Exception) { } b.Release(); }
            DropEntry(s); DropGhost(s);
            s.BotId = 0; s.FixedId = 0; s.Queued = false;
        }

        void MiniInit()
        {
            Mini = new CTFXMini(this);
            Mini.LoadPoints();
            Mini.BuildLadders();
            Mini.BuildSweeps();
            try
            {
                int ticks = _arena._server._zoneConfig.vehicle.warpDamageIgnoreTime;      // the zone's own value; ticks of 10 ms (Phase 0, spec 8)
                if (ticks > 0) Mini.GraceMs = Math.Min(3000, ticks * 10);
            }
            catch (Exception) { }
            Out("[CTFXBots] MiniTP profile | " + Mini.LoadNote + " | warp grace " + Mini.GraceMs + " ms | per side " + _cfg.PerSide);
        }

        /// <summary>MiniTP: two teams (Titan Militia / Collective), each an attack group and a defense group from the seat table.
        /// <paramref name="target"/> (*ctfxbots on N) means N bots PER SIDE here; 0 = perside from MiniTPBots.txt.</summary>
        void BuildMiniGroups(int target)
        {
            _groups.Clear();
            _postCounter = 0;
            SecondaryEnd = null; TertiaryEnd = null;
            int per = target > 0 ? Math.Max(2, Math.Min(14, target)) : _cfg.PerSide;
            _miniPer = per;
            int seats = 0;
            for (int side = 0; side < 2; side++)
            {
                Team team = ResolveTeam(side == 0 ? "Titan" : "Collective");
                List<MiniSeat> table = Mini.Roster(per, Rng);
                for (int pass = 0; pass < 2; pass++)
                {
                    bool attack = pass == 1;
                    CTFXBotGroup g = new CTFXBotGroup();
                    g.Kind = side == 0 ? CTFXKind.Titan : CTFXKind.Collective;
                    g.Team = team;
                    g.Home = Mini.ZoneOf(side);
                    g.Attackers = false;        // the CTF:X meaning (spawns in the big map's landing areas) does not apply; a seat's side is slot.Attack
                    foreach (MiniSeat st in table)
                    {
                        if (st.Attack != attack) continue;
                        AddSlots(g, attack ? CTFXStyle.Roam : CTFXStyle.Guard, 1);
                        CTFXSlot s = g.Slots[g.Slots.Count - 1];
                        s.ForceClass = CTFXBot.ClassByShort(st.Cls);
                        s.BuildName = st.Build; s.Role = st.Role; s.Attack = attack;
                        seats++;
                    }
                    if (g.Slots.Count == 0) continue;
                    if (g.Team == null)
                    {
                        foreach (CTFXSlot s in g.Slots) s.Retired = true;
                        Out("[CTFXBots] MiniTP: no team for " + (side == 0 ? "Titan Militia" : "Collective") + " (" + g.Slots.Count + " bots skipped)");
                    }
                    _groups.Add(g);
                }
            }
            Out("[CTFXBots] MiniTP teams: " + seats + " seats, " + per + " per side");
        }
    }
}
