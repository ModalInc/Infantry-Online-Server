using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// Bounty Hunting Bots (BHB) on <see cref="botTeam5"/>.
    /// While a 30k hunt is active, keep a 20-bot squad (1 captain + 19 grunts) alive
    /// and hunting the marked player (infinite respawns while the hunt lasts).
    /// When the mark switches, retarget the new hunted player; tear down only when the hunt ends.
    /// </summary>
    public partial class Script_Eol
    {
        public const int BountyHunterCaptainVehId = 142;
        /// <summary>Total live BHB on <see cref="botTeam5"/> (captain + grunts).</summary>
        public const int BountyHunterSquadSize = 20;
        /// <summary>Concurrent grunt cap — squad size minus the captain.</summary>
        public const int BountyHunterMaxConcurrentGrunts = BountyHunterSquadSize - 1;
        /// <summary>Spawn this many grunts immediately when a hunt arms (fill to cap).</summary>
        public const int BountyHunterOpeningBatch = BountyHunterMaxConcurrentGrunts;
        public const int BountyHunterGruntSpawnIntervalMs = 900;
        public const int BountyHunterCaptainRespawnMs = 4000;
        /// <summary>Directional shield projectile — fired when non–bounty-team hits are rejected.</summary>
        public const int BountyHunterForceFieldItemId = 3164;
        /// <summary>Min ms between shield FX per bot (avoids reliable spam on multi-pellet weapons).</summary>
        public const int BountyHunterForceFieldCooldownMs = 180;

        bool _bountyHunterWaveActive;
        int _tickNextBountyHunterGruntSpawn;
        int _tickNextBountyHunterCaptainSpawn;
        short _bountyHunterLastKnownX;
        short _bountyHunterLastKnownY;
        bool _bountyHunterHasLastKnown;
        int _bountyHunterMarkPlayerId;
        readonly Dictionary<int, int> _bountyHunterForceFieldNextTick = new Dictionary<int, int>();

        public bool BountyHunterSquadOperational
        {
            get { return _bountyHunterWaveActive && IsBountyHuntActive; }
        }

        public bool IsBountyHunterBotTeam(Team t)
        {
            return t != null && t == botTeam5;
        }

        public int GetBountyHunterAliveCount()
        {
            if (_arena == null || botTeam5 == null)
                return 0;
            int n = 0;
            foreach (Vehicle v in _arena.Vehicles)
            {
                if (v == null || v.IsDead || v._team != botTeam5)
                    continue;
                if (v is RoamingCaptain || v is RoamingAttacker)
                    n++;
            }
            return n;
        }

        int CountLiveBountyHunterGrunts()
        {
            if (_arena == null || botTeam5 == null)
                return 0;
            int n = 0;
            foreach (Vehicle v in _arena.Vehicles)
            {
                if (v is RoamingAttacker && !v.IsDead && v._team == botTeam5)
                    n++;
            }
            return n;
        }

        /// <summary>Mark, or anyone on the mark's team — BHB may engage these only.</summary>
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
            // Bounty mark + their teammates still deal damage.
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
                // Face the incoming shot so the directional shield rejects toward the attacker.
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
            // Dropship coords must not become the chase pin or a spawn anchor.
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
            if (_eol != null && _eol.IsInPioneerDropshipSafeZone(spawn.positionX, spawn.positionY))
            {
                spawn = null;
                return false;
            }
            _bountyHunterSectorSpawnX = spawn.positionX;
            _bountyHunterSectorSpawnY = spawn.positionY;
            _bountyHunterSectorSpawnValid = true;
            return true;
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

            if (sameTarget && FindLiveBountyHunterCaptain() != null)
                return;

            if (_bountyHunterWaveActive)
            {
                // New mark — keep squad, announce retarget, refresh last-known.
                int bong = _config != null ? _config.flag.victoryWarningBong : 28;
                string retarget = IsBountyHunterMarkInField()
                    ? "now hunting " + mark._alias
                    : "holding for " + mark._alias + " to leave the dropship";
                _arena.sendArenaMessage("&BHB: Retargeting — " + retarget + ".", bong);
                EnsureBountyHunterCaptain(now);
                return;
            }

            ArmBountyHunterWave(mark, now);
        }

        /// <summary>Called when the hunt ends with no next mark (or match clear).</summary>
        public void OnBountyHunterHuntEnded(int now, bool announce)
        {
            TeardownBountyHunterSquad(now, announce);
        }

        void ArmBountyHunterWave(InfServer.Game.Player mark, int now)
        {
            if (mark == null || _arena == null)
                return;
            if (botTeam5 == null)
            {
                Log.write(TLog.Error, "[Eol] BHB: botTeam5 is null — cannot deploy hunters.");
                return;
            }
            if (_rand == null)
                _rand = new Random(Environment.TickCount);

            // Always an open sector. Never the dropship, even if the mark is still there.
            Helpers.ObjectState spawn;
            if (!TryFindBountyHunterSectorSpawn(out spawn) || spawn == null)
            {
                Log.write(TLog.Warning, "[Eol] BHB: no open-sector spawn for hunt on {0}.", mark._alias);
                return;
            }

            if (IsBountyHunterMarkInField())
                NoteBountyHunterMarkSighting(mark);
            _bountyHunterWaveActive = true;
            _bountyHunterMarkPlayerId = mark._id;
            _tickNextBountyHunterGruntSpawn = now;
            _tickNextBountyHunterCaptainSpawn = now;

            if (_bots == null)
                _bots = new List<Bot>();

            if (!roamingCaptianBots.Contains(botTeam5))
                roamingCaptianBots.Add(botTeam5);
            if (!capRoamBots.ContainsKey(botTeam5))
                capRoamBots.Add(botTeam5, 0);
            if (!roamBots.ContainsKey(botTeam5))
                roamBots.Add(botTeam5, 0);
            else
                roamBots[botTeam5] = 0;

            RoamingCaptain captain = SpawnBountyHunterCaptainAt(spawn, now);
            int bong = _config != null ? _config.flag.victoryWarningBong : 28;
            string wait = IsBountyHunterMarkInField()
                ? "hunting " + mark._alias
                : "waiting for " + mark._alias + " to leave the dropship";
            _arena.sendArenaMessage(string.Format(
                "&BHB: Bounty Hunting Bots deployed ({0} on {1}) — {2}.",
                BountyHunterSquadSize,
                botTeam5._name ?? "Bot - BHB",
                wait), bong);

            // Fill the squad immediately (captain + opening grunts → 20).
            int open = BountyHunterOpeningBatch;
            for (int i = 0; i < open; i++)
            {
                if (CountLiveBountyHunterGrunts() >= BountyHunterMaxConcurrentGrunts)
                    break;
                TrySpawnOneBountyHunterGrunt(now, captain);
            }

            Log.write(TLog.Normal, "[Eol] BHB wave armed for {0} (alive={1}/{2}).",
                mark._alias, GetBountyHunterAliveCount(), BountyHunterSquadSize);
            InvalidateEolHudTickerCache();
        }

        RoamingCaptain SpawnBountyHunterCaptainAt(Helpers.ObjectState spawn, int now)
        {
            if (spawn == null || botTeam5 == null || _arena == null)
                return null;

            RoamingCaptain captain = _arena.newBot(
                typeof(RoamingCaptain), (ushort)BountyHunterCaptainVehId, botTeam5, null, spawn, this) as RoamingCaptain;
            if (captain == null)
                return null;

            EnableBotDropshipRespawn(captain, 0);
            if (_bots != null && !_bots.Contains(captain))
                _bots.Add(captain);
            _tickNextBountyHunterCaptainSpawn = now + BountyHunterCaptainRespawnMs;
            return captain;
        }

        void EnsureBountyHunterCaptain(int now)
        {
            if (!_bountyHunterWaveActive || !IsBountyHuntActive)
                return;
            if (FindLiveBountyHunterCaptain() != null)
                return;
            if (_tickNextBountyHunterCaptainSpawn != 0 && now < _tickNextBountyHunterCaptainSpawn)
                return;

            InfServer.Game.Player mark = _bountyHuntMarked;
            Helpers.ObjectState spawn;
            if (!TryFindBountyHunterSectorSpawn(out spawn) || spawn == null)
                return;

            RoamingCaptain cap = SpawnBountyHunterCaptainAt(spawn, now);
            if (cap != null && mark != null)
            {
                _arena.sendArenaMessage(string.Format(
                    "&BHB: Replacement captain hunting {0}.", mark._alias), 1);
            }
        }

        void TrySpawnOneBountyHunterGrunt(int now, RoamingCaptain nearCaptain)
        {
            if (!_bountyHunterWaveActive || botTeam5 == null || _arena == null)
                return;
            if (CountLiveBountyHunterGrunts() >= BountyHunterMaxConcurrentGrunts)
                return;

            Helpers.ObjectState openPoint = null;
            RoamingCaptain liveCap = nearCaptain != null && !nearCaptain.IsDead
                ? nearCaptain
                : FindLiveBountyHunterCaptain();
            if (liveCap != null && !liveCap.IsDead
                && (_eol == null || !_eol.IsInPioneerDropshipSafeZone(liveCap._state.positionX, liveCap._state.positionY)))
            {
                openPoint = FindOpenRaiderSpawnWithPeerGap(liveCap._state.positionX, liveCap._state.positionY);
            }
            if (openPoint == null)
                TryFindBountyHunterSectorSpawn(out openPoint);
            if (openPoint == null)
            {
                _tickNextBountyHunterGruntSpawn = now + 900;
                return;
            }

            int id = 144;
            switch ((_rand ?? new Random()).Next(1, 6))
            {
                case 1: id = 144; break;
                case 2: id = 145; break;
                case 3: id = 146; break;
                case 4: id = 147; break;
                case 5: id = 157; break;
            }

            if (_bots == null)
                _bots = new List<Bot>();

            RoamingAttacker grunt = _arena.newBot(
                typeof(RoamingAttacker), (ushort)id, botTeam5, null, openPoint, this, null) as RoamingAttacker;
            if (grunt == null)
            {
                _tickNextBountyHunterGruntSpawn = now + 900;
                return;
            }

            EnableBotDropshipRespawn(grunt, 0);
            if (!roamBots.ContainsKey(botTeam5))
                roamBots.Add(botTeam5, 0);
            roamBots[botTeam5] = CountLiveBountyHunterGrunts();
            if (!_bots.Contains(grunt))
                _bots.Add(grunt);
            _tickNextBountyHunterGruntSpawn = now + BountyHunterGruntSpawnIntervalMs;
        }

        RoamingCaptain FindLiveBountyHunterCaptain()
        {
            if (_arena == null || botTeam5 == null)
                return null;
            foreach (Vehicle v in _arena.Vehicles)
            {
                RoamingCaptain c = v as RoamingCaptain;
                if (c != null && !c.IsDead && c._team == botTeam5)
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
                TeardownBountyHunterSquad(now, announce: false);
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
                        "&BHB: Retargeting — now hunting {0}.", mark._alias), 1);
                }
            }

            EnsureBountyHunterCaptain(now);

            // Keep the squad topped up to 20 (1 captain + 19 grunts) for the whole hunt.
            int grunts = CountLiveBountyHunterGrunts();
            if (grunts < BountyHunterMaxConcurrentGrunts && now >= _tickNextBountyHunterGruntSpawn)
            {
                // Spawn a few per poll so a wiped pack rebuilds quickly without one-tick floods.
                int budget = Math.Min(3, BountyHunterMaxConcurrentGrunts - grunts);
                RoamingCaptain cap = FindLiveBountyHunterCaptain();
                for (int i = 0; i < budget; i++)
                    TrySpawnOneBountyHunterGrunt(now, cap);
            }

            if (roamBots != null && botTeam5 != null)
                roamBots[botTeam5] = CountLiveBountyHunterGrunts();
        }

        void TeardownBountyHunterSquad(int now, bool announce)
        {
            if (_arena != null && botTeam5 != null)
            {
                List<Vehicle> wipe = new List<Vehicle>();
                foreach (Vehicle v in _arena.Vehicles)
                {
                    if (v == null || v.IsDead)
                        continue;
                    if (v._team != botTeam5)
                        continue;
                    if (v is RoamingCaptain || v is RoamingAttacker)
                        wipe.Add(v);
                }
                for (int i = 0; i < wipe.Count; i++)
                    EnqueueVehicleDespawn(wipe[i]);
            }

            if (roamBots != null && botTeam5 != null && roamBots.ContainsKey(botTeam5))
                roamBots[botTeam5] = 0;
            if (roamingCaptianBots != null && botTeam5 != null)
                roamingCaptianBots.Remove(botTeam5);
            if (capRoamBots != null && botTeam5 != null)
                capRoamBots.Remove(botTeam5);

            _bountyHunterWaveActive = false;
            _bountyHunterHasLastKnown = false;
            _bountyHunterSectorSpawnValid = false;
            _bountyHunterMarkPlayerId = 0;
            _bountyHunterForceFieldNextTick.Clear();

            if (announce && _arena != null)
                _arena.sendArenaMessage("&BHB: Bounty Hunting Bots withdrawn.", 1);

            InvalidateEolHudTickerCache();
            Log.write(TLog.Normal, "[Eol] BHB squad torn down.");
        }
    }
}
