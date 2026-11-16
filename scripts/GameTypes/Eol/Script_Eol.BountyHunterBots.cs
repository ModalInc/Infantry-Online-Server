using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// The ASSASSINS (was "Bounty Hunting Bots", local rework 2026-11-13): while a 30k hunt is active, two
    /// 3-man assassin cells ("Bot - Assassins" on <see cref="botTeam5"/> and "Bot - Assassins 2" on
    /// <see cref="botTeam9"/>) - 1 captain + 2 operatives each, ultra-elite aim - hunt the marked player
    /// (infinite respawns while the hunt lasts). They only engage the mark and the mark's team, and shrug off
    /// hits from anyone else. When the mark switches they retarget; they withdraw when the hunt ends.
    /// </summary>
    public partial class Script_Eol
    {
        /// <summary>Cell leader: Black Ops (local 2026-11-14 - assassins wear the dark player skins, was Captain Roamer).</summary>
        public const int BountyHunterCaptainVehId = 150;
        /// <summary>Operatives: Black Ops, Infiltrator, BioChem, NeuroTAC, DropTrooper (Black Ops twice = most common).</summary>
        static readonly int[] AssassinOperativeClasses = { 150, 150, 14, 100, 112, 117 };
        /// <summary>No speed bonus: assassins move at their vehicle's own speeds (2026-11-15 - the +35% read as a strafe cheat).</summary>
        public const int AssassinAgilityBonusPercent = 0;
        /// <summary>Live assassins per cell (captain + operatives).</summary>
        public const int BountyHunterSquadSize = 3;
        /// <summary>Operatives per cell - cell size minus the captain.</summary>
        public const int BountyHunterMaxConcurrentGrunts = BountyHunterSquadSize - 1;
        public const int BountyHunterOpeningBatch = BountyHunterMaxConcurrentGrunts;
        public const int BountyHunterGruntSpawnIntervalMs = 4000;
        public const int BountyHunterCaptainRespawnMs = 6000;
        /// <summary>Skill tier for assassins (beyond ace - see BotSkillProfile "ultra").</summary>
        public const string AssassinSkillMode = "ultra";
        /// <summary>Directional shield projectile — fired when non–bounty-team hits are rejected.</summary>
        public const int BountyHunterForceFieldItemId = 3164;
        /// <summary>Min ms between shield FX per bot (avoids reliable spam on multi-pellet weapons).</summary>
        public const int BountyHunterForceFieldCooldownMs = 180;

        bool _bountyHunterWaveActive;
        readonly Dictionary<Team, int> _tickNextBountyHunterGruntSpawn = new Dictionary<Team, int>();
        readonly Dictionary<Team, int> _tickNextBountyHunterCaptainSpawn = new Dictionary<Team, int>();
        short _bountyHunterLastKnownX;
        short _bountyHunterLastKnownY;
        bool _bountyHunterHasLastKnown;
        int _bountyHunterMarkPlayerId;
        readonly Dictionary<int, int> _bountyHunterForceFieldNextTick = new Dictionary<int, int>();

        /// <summary>Both assassin cells.</summary>
        IEnumerable<Team> AssassinTeams()
        {
            if (botTeam5 != null) yield return botTeam5;
            if (botTeam9 != null) yield return botTeam9;
        }

        public bool BountyHunterSquadOperational
        {
            get { return _bountyHunterWaveActive && IsBountyHuntActive; }
        }

        /// <summary>True while assassins are on a contract (hunting the mark, shielded from others).</summary>
        public bool IsBountyHunterBotTeam(Team t)
        {
            return !_assassinsReleased && IsAssassinTeam(t);
        }

        /// <summary>Either assassin cell's team, contract or not.</summary>
        public bool IsAssassinTeam(Team t)
        {
            return t != null && (t == botTeam5 || t == botTeam9);
        }

        /// <summary>
        /// Contract over (the 30ker died / hunt ended): surviving assassins go rogue - no shield, fight anyone they
        /// meet until they die - and nobody respawns until the next 30k hunt.
        /// </summary>
        bool _assassinsReleased;
        public bool IsReleasedAssassinTeam(Team t)
        {
            return _assassinsReleased && IsAssassinTeam(t);
        }

        void ReleaseAssassins(bool announce)
        {
            int alive = 0;
            if (_arena != null)
            {
                foreach (Vehicle v in _arena.Vehicles)
                {
                    if (v is Bot b && !b.IsDead && IsAssassinTeam(b._team))
                    {
                        b.bRespawn = false;           // no revive after this
                        alive++;
                    }
                }
            }
            _assassinsReleased = true;
            _bountyHunterWaveActive = false;
            _bountyHunterHasLastKnown = false;
            _bountyHunterMarkPlayerId = 0;
            _bountyHunterForceFieldNextTick.Clear();
            _tickNextBountyHunterGruntSpawn.Clear();
            _tickNextBountyHunterCaptainSpawn.Clear();
            if (announce && _arena != null && alive > 0)
                _arena.sendArenaMessage(string.Format("&Assassins: contract over — {0} survivor{1} gone rogue.", alive, alive == 1 ? "" : "s"), 1);
            InvalidateEolHudTickerCache();
            Log.write(TLog.Normal, "[Eol] Assassins released ({0} alive, no respawns until the next hunt).", alive);
        }

        public int GetBountyHunterAliveCount()
        {
            if (_arena == null)
                return 0;
            int n = 0;
            foreach (Vehicle v in _arena.Vehicles)
            {
                if (v == null || v.IsDead || !IsBountyHunterBotTeam(v._team))
                    continue;
                if (v is RoamingCaptain || v is RoamingAttacker)
                    n++;
            }
            return n;
        }

        int CountLiveBountyHunterGrunts(Team team)
        {
            if (_arena == null || team == null)
                return 0;
            int n = 0;
            foreach (Vehicle v in _arena.Vehicles)
            {
                if (v is RoamingAttacker && !v.IsDead && v._team == team)
                    n++;
            }
            return n;
        }

        /// <summary>Mark, or anyone on the mark's team — assassins may engage these only.</summary>
        public bool IsBountyHunterEngageTarget(InfServer.Game.Player p)
        {
            InfServer.Game.Player mark = _bountyHuntMarked;
            if (p == null || p.IsDead || mark == null || mark.IsDead)
                return false;
            if (p == mark)
                return true;
            return p._team != null && mark._team != null && p._team == mark._team;
        }

        /// <summary>
        /// True when this hit should deal no damage (attacker is not the hunted player / their team).
        /// Fires directional shield projectile <see cref="BountyHunterForceFieldItemId"/> toward the attacker.
        /// </summary>
        public bool TryRejectBountyHunterHit(Bot bot, InfServer.Game.Player attacker)
        {
            if (bot == null || !IsBountyHunterBotTeam(bot._team))
                return false;
            if (attacker == null)
                return false;
            if (IsBountyHunterEngageTarget(attacker))
                return false;

            FireBountyHunterForceField(bot, attacker);
            return true;
        }

        /// <summary>Legacy name — prefer <see cref="TryRejectBountyHunterHit"/>.</summary>
        public bool ShouldIgnoreBountyHunterDamage(Bot bot, InfServer.Game.Player attacker)
        {
            return TryRejectBountyHunterHit(bot, attacker);
        }

        void FireBountyHunterForceField(Bot bot, InfServer.Game.Player attacker)
        {
            if (_arena == null || bot == null)
                return;

            int now = Environment.TickCount;
            int botId = bot._id;
            int nextAllowed;
            if (_bountyHunterForceFieldNextTick.TryGetValue(botId, out nextAllowed)
                && now < nextAllowed)
                return;
            _bountyHunterForceFieldNextTick[botId] = now + BountyHunterForceFieldCooldownMs;

            short x = bot._state.positionX;
            short y = bot._state.positionY;
            short z = bot._state.positionZ;
            byte yaw = bot._state.yaw;

            if (attacker != null && attacker._state != null)
            {
                int dx = attacker._state.positionX - x;
                int dy = attacker._state.positionY - y;
                if (dx != 0 || dy != 0)
                {
                    double ang = Math.Atan2(dx, dy);
                    yaw = (byte)(((int)Math.Round(ang * (128.0 / Math.PI))) & 0xFF);
                }
            }

            Helpers.Player_RouteExplosion(
                _arena.Players,
                (short)BountyHunterForceFieldItemId,
                x, y, z,
                yaw,
                bot._id);
        }

        short _bountyHunterSectorSpawnX;
        short _bountyHunterSectorSpawnY;
        bool _bountyHunterSectorSpawnValid;

        public void NoteBountyHunterMarkSighting(InfServer.Game.Player mark)
        {
            if (mark == null || mark.IsDead || mark.IsSpectator)
                return;
            if (IsBountyHuntInDropship(mark))
                return;
            _bountyHunterLastKnownX = mark._state.positionX;
            _bountyHunterLastKnownY = mark._state.positionY;
            _bountyHunterHasLastKnown = true;
        }

        /// <summary>Open tile in an active sector — never the Pioneer dropship.</summary>
        bool TryFindBountyHunterSectorSpawn(out Helpers.ObjectState spawn)
        {
            spawn = null;
            if (!TryFindOpenRaiderAnchorInActiveSectors(out spawn) || spawn == null)
            {
                if (!_bountyHunterSectorSpawnValid)
                    return false;
                spawn = FindOpenRaiderSpawnWithPeerGap(_bountyHunterSectorSpawnX, _bountyHunterSectorSpawnY)
                    ?? findOpenWarpBot(_arena, _bountyHunterSectorSpawnX, _bountyHunterSectorSpawnY, 400);
            }
            if (spawn == null)
                return false;
            if (!IsOpenConnectedGround(spawn.positionX, spawn.positionY))
            {   // sealed building / physics pocket: use a spread-out open spot instead (local 2026-11-14)
                spawn = PickEliteSpawnPoint();
                if (spawn == null)
                    return false;
            }
            if (_eol != null && _eol.IsInPioneerDropshipSafeZone(spawn.positionX, spawn.positionY))
            {
                spawn = null;
                return false;
            }
            // (2026-11-16, owner: assassins spawn and die straight away) never right on top of the mark's team -
            // they were coming in inside the 30ker's group. Out of their sight first, then they move in.
            if (MarkTeamNear(spawn.positionX, spawn.positionY, AssassinSpawnClearOfMarkRaw))
            {
                Helpers.ObjectState away = null;
                for (int i = 0; i < 6 && away == null; i++)
                {
                    Helpers.ObjectState p = PickEliteSpawnPoint();
                    if (p != null && !MarkTeamNear(p.positionX, p.positionY, AssassinSpawnClearOfMarkRaw))
                        away = p;
                }
                if (away == null)
                {
                    spawn = null;
                    return false;   // try again next poll
                }
                spawn = away;
            }
            _bountyHunterSectorSpawnX = spawn.positionX;
            _bountyHunterSectorSpawnY = spawn.positionY;
            _bountyHunterSectorSpawnValid = true;
            return true;
        }

        /// <summary>Assassins come in at least this far from the mark and the mark's team (a screen and a half).</summary>
        const int AssassinSpawnClearOfMarkRaw = 700;

        bool MarkTeamNear(short x, short y, int range)
        {
            if (_arena == null)
                return false;
            foreach (InfServer.Game.Player p in _arena.getPlayersInRange(x, y, range))
                if (p != null && !p.IsDead && !p.IsSpectator && IsBountyHunterEngageTarget(p))
                    return true;
            return false;
        }

        public bool TryGetBountyHunterLastKnown(out short x, out short y)
        {
            x = _bountyHunterLastKnownX;
            y = _bountyHunterLastKnownY;
            return _bountyHunterHasLastKnown;
        }

        /// <summary>Called when a hunt starts or advances to a new mark.</summary>
        public void OnBountyHunterHuntStarted(InfServer.Game.Player mark, int now)
        {
            if (mark == null || _arena == null)
                return;

            bool sameTarget = _bountyHunterWaveActive && _bountyHunterMarkPlayerId == mark._id;
            _bountyHunterMarkPlayerId = mark._id;
            if (IsBountyHunterMarkInField())
                NoteBountyHunterMarkSighting(mark);

            if (sameTarget)
                return;

            if (_bountyHunterWaveActive)
            {
                int bong = _config != null ? _config.flag.victoryWarningBong : 28;
                string retarget = IsBountyHunterMarkInField()
                    ? "new contract on " + mark._alias
                    : "waiting for " + mark._alias + " to leave the dropship";
                _arena.sendArenaMessage("&Assassins: " + retarget + ".", bong);
                foreach (Team t in AssassinTeams())
                    EnsureBountyHunterCaptain(now, t);
                return;
            }

            ArmBountyHunterWave(mark, now);
        }

        /// <summary>Called when the hunt ends with no next mark (or match clear).</summary>
        public void OnBountyHunterHuntEnded(int now, bool announce)
        {
            // survivors fight on until they die; no respawns until the next 30ker
            ReleaseAssassins(announce);
        }

        void ArmBountyHunterWave(InfServer.Game.Player mark, int now)
        {
            if (mark == null || _arena == null)
                return;
            if (botTeam5 == null)
            {
                Log.write(TLog.Error, "[Eol] Assassins: botTeam5 is null — cannot deploy.");
                return;
            }
            if (_rand == null)
                _rand = new Random(Environment.TickCount);

            // a new contract: any rogue survivors rejoin as assassins (shielded again, respawning)
            _assassinsReleased = false;
            foreach (Vehicle v in _arena.Vehicles)
                if (v is Bot rb && !rb.IsDead && IsAssassinTeam(rb._team))
                    rb.bRespawn = true;

            if (IsBountyHunterMarkInField())
                NoteBountyHunterMarkSighting(mark);
            _bountyHunterWaveActive = true;
            _bountyHunterMarkPlayerId = mark._id;

            if (_bots == null)
                _bots = new List<Bot>();

            int deployed = 0;
            foreach (Team team in AssassinTeams())
            {
                // each cell inserts at its own open-sector spot (never the dropship)
                Helpers.ObjectState spawn;
                if (!TryFindBountyHunterSectorSpawn(out spawn) || spawn == null)
                {
                    Log.write(TLog.Warning, "[Eol] Assassins: no open-sector spawn for {0} on {1}.", team._name, mark._alias);
                    continue;
                }
                if (!roamingCaptianBots.Contains(team))
                    roamingCaptianBots.Add(team);
                if (!capRoamBots.ContainsKey(team))
                    capRoamBots.Add(team, 0);
                roamBots[team] = 0;
                _tickNextBountyHunterGruntSpawn[team] = now;
                _tickNextBountyHunterCaptainSpawn[team] = now;

                RoamingCaptain captain = SpawnBountyHunterCaptainAt(spawn, now, team);
                for (int i = 0; i < BountyHunterOpeningBatch; i++)
                {
                    if (CountLiveBountyHunterGrunts(team) >= BountyHunterMaxConcurrentGrunts)
                        break;
                    TrySpawnOneBountyHunterGrunt(now, captain, team, ignoreInterval: true);
                }
                deployed++;
            }

            int bong = _config != null ? _config.flag.victoryWarningBong : 28;
            string wait = IsBountyHunterMarkInField()
                ? "the contract is on " + mark._alias
                : "waiting for " + mark._alias + " to leave the dropship";
            _arena.sendArenaMessage(string.Format(
                "&Assassins deployed: {0} cells of {1} — {2}.", deployed, BountyHunterSquadSize, wait), 0);   // (silent: the hunt start sounded)

            Log.write(TLog.Normal, "[Eol] Assassins armed for {0} (alive={1}).", mark._alias, GetBountyHunterAliveCount());
            InvalidateEolHudTickerCache();
        }

        /// <summary>Ultra-elite: beyond-ace aim and the brain's assassin persona.</summary>
        void MakeAssassin(Bot b)
        {
            if (b == null)
                return;
            b.SkillModeOverride = AssassinSkillMode;
            if (b._type != null)
                ApplyHumanLoadout(b, b._type.Id, b._team);   // class weapons, SuperCharger, grenades, opener
            ApplyAssassinWeapons(b);
            b.AgilityBonusPercent = AssassinAgilityBonusPercent;
            EnableBotDropshipRespawn(b, 0);
            if (_bots != null && !_bots.Contains(b))
                _bots.Add(b);
        }

        /// <summary>
        /// (2026-11-16, owner: assassins too weak) top-shelf guns instead of the class pools: close - Kamenev K3
        /// Striker, Kuchler 201a PSMG (2), Kamenev p3 Needler; far - Steiner CG60 Chaingun, Unittech AR2012,
        /// Maklov AR mk 606, Kamenev PCAR, Kuchler GR790. Always 3 grenades and the Kamenev AP mk2 opener.
        /// </summary>
        static readonly int[] AssassinClose = { 1465, 1343, 1533 };
        static readonly int[] AssassinFar = { 3105, 3094, 1096, 1188, 1031 };

        void ApplyAssassinWeapons(Bot b)
        {
            try
            {
                // the best guns the operative's class may carry (2026-11-16: class-legal only)
                int close = Pick(AssassinClose), far = Pick(AssassinFar);
                HashSet<int> legal = b._type != null ? ClassLegalWeapons(b._type.Id) : null;
                if (legal != null && legal.Count > 0)
                {
                    int c = PickLegal(AssassinClose, legal), f = PickLegal(AssassinFar, legal);
                    close = c != 0 ? c : close;
                    far = f != 0 ? f : far;
                    EnforceClassWeapons(b._type.Id, ref close, ref far, mix: false);
                }
                EquipCloseFar(b, close, far);
                WeaponController grenade = null, opener = null;
                int gid = PickClassGrenade(b);
                ItemInfo g = gid != 0 ? Itm(gid) : null;
                if (g != null)
                {
                    grenade = new WeaponController(b._state, new WeaponController.WeaponSettings());
                    if (grenade.equip(g)) grenade.Owner = b; else grenade = null;
                }
                ItemInfo k = ClassMayCarry(b, ItmKamenevAPmk2) ? Itm(ItmKamenevAPmk2) : null;
                if (k != null)
                {
                    opener = new WeaponController(b._state, new WeaponController.WeaponSettings());
                    if (opener.equip(k)) opener.Owner = b; else opener = null;
                }
                EolBotBrain.SetExtraWeapons(b, opener, grenade, grenade != null ? 3 : 0);
            }
            catch (Exception ex)
            {
                Log.write(TLog.Warning, "[Eol] Assassin weapons failed: " + ex.Message);
            }
        }

        RoamingCaptain SpawnBountyHunterCaptainAt(Helpers.ObjectState spawn, int now, Team team)
        {
            if (spawn == null || team == null || _arena == null)
                return null;

            RoamingCaptain captain = !CanSpawnBotOnTeam(team) ? null : _arena.newBot(
                typeof(RoamingCaptain), (ushort)BountyHunterCaptainVehId, team, null, spawn, this) as RoamingCaptain;
            if (captain == null)
                return null;

            MakeAssassin(captain);
            _tickNextBountyHunterCaptainSpawn[team] = now + BountyHunterCaptainRespawnMs;
            return captain;
        }

        void EnsureBountyHunterCaptain(int now, Team team)
        {
            if (!_bountyHunterWaveActive || !IsBountyHuntActive || team == null)
                return;
            if (FindLiveBountyHunterCaptain(team) != null)
                return;
            int due;
            if (_tickNextBountyHunterCaptainSpawn.TryGetValue(team, out due) && due != 0 && now < due)
                return;

            Helpers.ObjectState spawn;
            if (!TryFindBountyHunterSectorSpawn(out spawn) || spawn == null)
                return;
            SpawnBountyHunterCaptainAt(spawn, now, team);
        }

        void TrySpawnOneBountyHunterGrunt(int now, RoamingCaptain nearCaptain, Team team, bool ignoreInterval = false)
        {
            if (!_bountyHunterWaveActive || team == null || _arena == null)
                return;
            if (CountLiveBountyHunterGrunts(team) >= BountyHunterMaxConcurrentGrunts)
                return;
            int due;
            if (!ignoreInterval && _tickNextBountyHunterGruntSpawn.TryGetValue(team, out due) && now < due)
                return;

            Helpers.ObjectState openPoint = null;
            RoamingCaptain liveCap = nearCaptain != null && !nearCaptain.IsDead
                ? nearCaptain
                : FindLiveBountyHunterCaptain(team);
            if (liveCap != null && !liveCap.IsDead
                && (_eol == null || !_eol.IsInPioneerDropshipSafeZone(liveCap._state.positionX, liveCap._state.positionY))
                && !MarkTeamNear(liveCap._state.positionX, liveCap._state.positionY, AssassinSpawnClearOfMarkRaw))   // (not into the fight)
            {
                openPoint = FindOpenRaiderSpawnWithPeerGap(liveCap._state.positionX, liveCap._state.positionY);
            }
            if (openPoint == null)
                TryFindBountyHunterSectorSpawn(out openPoint);
            if (openPoint == null)
            {
                _tickNextBountyHunterGruntSpawn[team] = now + 900;
                return;
            }

            int id = AssassinOperativeClasses[(_rand ?? new Random()).Next(AssassinOperativeClasses.Length)];

            if (_bots == null)
                _bots = new List<Bot>();

            RoamingAttacker grunt = !CanSpawnBotOnTeam(team) ? null : _arena.newBot(
                typeof(RoamingAttacker), (ushort)id, team, null, openPoint, this, null) as RoamingAttacker;
            if (grunt == null)
            {
                _tickNextBountyHunterGruntSpawn[team] = now + 900;
                return;
            }

            MakeAssassin(grunt);
            if (!roamBots.ContainsKey(team))
                roamBots.Add(team, 0);
            roamBots[team] = CountLiveBountyHunterGrunts(team);
            _tickNextBountyHunterGruntSpawn[team] = now + BountyHunterGruntSpawnIntervalMs;
        }

        RoamingCaptain FindLiveBountyHunterCaptain(Team team)
        {
            if (_arena == null || team == null)
                return null;
            foreach (Vehicle v in _arena.Vehicles)
            {
                RoamingCaptain c = v as RoamingCaptain;
                if (c != null && !c.IsDead && c._team == team)
                    return c;
            }
            return null;
        }

        /// <summary>Any live assassin captain (either cell).</summary>
        RoamingCaptain FindLiveBountyHunterCaptain()
        {
            foreach (Team t in AssassinTeams())
            {
                RoamingCaptain c = FindLiveBountyHunterCaptain(t);
                if (c != null)
                    return c;
            }
            return null;
        }

        void PollBountyHunterSquad(int now)
        {
            if (!_bountyHunterWaveActive)
                return;

            if (!IsBountyHuntActive)
            {
                ReleaseAssassins(announce: true);
                return;
            }

            InfServer.Game.Player mark = _bountyHuntMarked;
            if (IsBountyHunterMarkInField())
            {
                NoteBountyHunterMarkSighting(mark);
                if (_bountyHunterMarkPlayerId != mark._id)
                {
                    _bountyHunterMarkPlayerId = mark._id;
                    _arena.sendArenaMessage(string.Format(
                        "&Assassins: new contract on {0}.", mark._alias), 1);
                }
            }

            foreach (Team team in AssassinTeams())
            {
                EnsureBountyHunterCaptain(now, team);
                if (CountLiveBountyHunterGrunts(team) < BountyHunterMaxConcurrentGrunts)
                    TrySpawnOneBountyHunterGrunt(now, FindLiveBountyHunterCaptain(team), team);
                if (roamBots != null)
                    roamBots[team] = CountLiveBountyHunterGrunts(team);
            }
        }

        void TeardownBountyHunterSquad(int now, bool announce)
        {
            if (_arena != null)
            {
                List<Vehicle> wipe = new List<Vehicle>();
                foreach (Vehicle v in _arena.Vehicles)
                {
                    if (v == null || v.IsDead)
                        continue;
                    if (!IsBountyHunterBotTeam(v._team))
                        continue;
                    if (v is RoamingCaptain || v is RoamingAttacker)
                        wipe.Add(v);
                }
                for (int i = 0; i < wipe.Count; i++)
                    EnqueueVehicleDespawn(wipe[i]);
            }

            foreach (Team team in AssassinTeams())
            {
                if (roamBots != null && roamBots.ContainsKey(team))
                    roamBots[team] = 0;
                if (roamingCaptianBots != null)
                    roamingCaptianBots.Remove(team);
                if (capRoamBots != null)
                    capRoamBots.Remove(team);
            }

            _bountyHunterWaveActive = false;
            _bountyHunterHasLastKnown = false;
            _bountyHunterSectorSpawnValid = false;
            _bountyHunterMarkPlayerId = 0;
            _bountyHunterForceFieldNextTick.Clear();
            _tickNextBountyHunterGruntSpawn.Clear();
            _tickNextBountyHunterCaptainSpawn.Clear();

            if (announce && _arena != null)
                _arena.sendArenaMessage("&Assassins withdrawn.", 1);

            InvalidateEolHudTickerCache();
            Log.write(TLog.Normal, "[Eol] Assassins torn down.");
        }
    }
}
