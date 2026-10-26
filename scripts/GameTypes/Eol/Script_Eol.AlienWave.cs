using System;
using System.Collections.Generic;
using System.Linq;

using InfServer.Logic;
using InfServer.Game;
using InfServer.Protocol;
using InfServer.Scripting;
using InfServer.Bots;

using Assets;

namespace InfServer.Script.GameType_Eol
{
    public partial class Script_Eol
    {
        public const int AlienCrashVehicleId = 163;
        public const int AlienCrashExplosionProjectileId = 1143;
        /// <summary>Terrain type ID that must not host the crashed spacecraft (e.g. building footprint / interior).</summary>
        public const int AlienCrashForbiddenTerrainTypeId = 5;
        /// <summary>Hostile players/bots farther than this from the crash anchor are ignored for alien seek (wider than alien leash hard radius so aliens can hunt near the wreck).</summary>
        public const int AlienHordeEngageRadiusFromCrash = 900;
        /// <summary>Hostiles within this range of the alien bot always count as threats (chasing away from the wreck still keeps aggro).</summary>
        public const int AlienSelfAggroRange = EolBotSelfAggroRange;
        /// <summary>Total AlienHorde spawns queued for one invasion wave (three batches of <see cref="AlienWaveMaxConcurrentAlive"/>).</summary>
        public const int AlienWaveTotalSpawns = 60;
        /// <summary>Never more than this many AlienHorde bots alive at once during a wave.</summary>
        public const int AlienWaveMaxConcurrentAlive = 20;
        /// <summary>Number of spawn waves (each up to <see cref="AlienWaveMaxConcurrentAlive"/> concurrent; total <see cref="AlienWaveTotalSpawns"/>).</summary>
        public const int AlienWaveSpawnBatchCount = 3;
        /// <summary>Delay between each <see cref="AlienHorde"/> spawn during a wave — random 12–22 seconds (inclusive).</summary>
        public const int AlienSpawnIntervalMinMs = 12 * 1000;
        public const int AlienSpawnIntervalMaxMs = 22 * 1000;
        public const int AlienWaveDelayMinMs = 30 * 60 * 1000;
        public const int AlienWaveDelayMaxMs = 60 * 60 * 1000;

        int _tickNextAlienWave;
        bool _alienWaveActive;
        List<ushort> _alienSpawnQueue;
        int _tickNextAlienSpawn;
        int _aliensSpawnedThisWave;
        /// <summary>Successful AlienHorde bot spawns this wave (HUD progress vs <see cref="AlienWaveTotalSpawns"/>).</summary>
        int _aliensSuccessfulSpawnForHud;
        int _aliensAliveThisWave;
        /// <summary>Wave quota left on the HUD (starts at <see cref="AlienWaveTotalSpawns"/>; −1 per permanent alien death).</summary>
        int _aliensRemainingThisWave;
        /// <summary>After each full concurrent batch is spawned (e.g. 10th and 20th when <see cref="AlienWaveMaxConcurrentAlive"/> is 10), brief pause before next batch can spawn (reinforcement cadence).</summary>
        int _tickAlienInterBatchGateUntil;
        short _alienCrashPosX;
        short _alienCrashPosY;
        int _tickLastAlienAnchorScan;
        const int AlienAnchorScanMinMs = 900;

        /// <summary>Horde rally point for alien bots (crashed ship while present, else last crash site).</summary>
        public short AlienHordeAnchorX { get; private set; }
        public short AlienHordeAnchorY { get; private set; }

        public bool AlienWaveActive { get { return _alienWaveActive; } }

        /// <summary>True while a pirate raid wave is armed (anchor / grunts / captain).</summary>
        public bool IsPirateRaidLive()
        {
            return _pirateRaidWaveActive;
        }

        /// <summary>True until the alien wave has spawned its quota and every alien is dead.</summary>
        public bool IsAlienInvasionLive()
        {
            return _alienWaveActive;
        }

        public bool IsAlienBotTeam(Team t)
        {
            return t != null && t == botTeam7;
        }

        public bool IsRoamBotTeam(Team t)
        {
            return t != null && t == botTeam4;
        }

        public bool IsDefenseBotTeam(Team t)
        {
            if (t == null)
                return false;
            return t == botTeam1 || t == botTeam2 || t == botTeam3;
        }

        bool IsManagedEolBotFactionTeam(Team t)
        {
            return IsDefenseBotTeam(t) || IsRoamBotTeam(t) || IsAlienBotTeam(t) || IsBountyHunterBotTeam(t);
        }

        /// <summary>Whether two script bot teams should damage each other (not same faction).</summary>
        public bool ShouldEolBotTeamsFight(Team self, Team other)
        {
            if (self == null || other == null || self == other)
                return false;
            if (!IsManagedEolBotFactionTeam(self) || !IsManagedEolBotFactionTeam(other))
                return false;
            if (IsAlienBotTeam(self) && IsAlienBotTeam(other))
                return false;
            if (IsRoamBotTeam(self) && IsRoamBotTeam(other))
                return false;
            if (IsDefenseBotTeam(self) && IsDefenseBotTeam(other))
                return false;
            if (IsBountyHunterBotTeam(self) && IsBountyHunterBotTeam(other))
                return false;
            return true;
        }

        void ResetAlienWaveState()
        {
            if (_arena != null)
            {
                foreach (Vehicle v in _arena.Vehicles.Where((Vehicle x) => x._type.Id == AlienCrashVehicleId && x._team == botTeam7).ToList())
                    EnqueueVehicleDespawn(v);
            }
            _alienWaveActive = false;
            _alienSpawnQueue = null;
            _tickNextAlienSpawn = 0;
            _aliensSpawnedThisWave = 0;
            _aliensSuccessfulSpawnForHud = 0;
            _aliensAliveThisWave = 0;
            _aliensRemainingThisWave = 0;
            _tickNextAlienWave = 0;
            _tickLastAlienAnchorScan = 0;
            _tickAlienInterBatchGateUntil = 0;
        }

        void ScheduleNextAlienWave(int now)
        {
            if (!EolAlienWavesEnabled)
            {
                _tickNextAlienWave = 0;
                return;
            }
            if (_rand == null)
                _rand = new Random(Environment.TickCount);
            _tickNextAlienWave = now + _rand.Next(AlienWaveDelayMinMs, AlienWaveDelayMaxMs + 1);
        }

        /// <summary>Ends an active invasion immediately: kills alien bots, removes crash ship, clears queue. Optionally arms the random next-wave timer when <see cref="Script_Eol.EolAlienWavesEnabled"/>.</summary>
        public void AbortAlienInvasionNow(int now, bool armNextRandomWaveIfSystemEnabled)
        {
            if (_arena == null)
                return;

            if (_alienWaveActive)
                EnqueueExplosionRoute(AlienCrashExplosionProjectileId, _alienCrashPosX, _alienCrashPosY, 0, 0, 0);

            foreach (Vehicle v in _arena.Vehicles.Where((Vehicle x) => x._type.Id == AlienCrashVehicleId && x._team == botTeam7).ToList())
                EnqueueVehicleDespawn(v);

            if (_bots != null)
            {
                foreach (Bots.Bot b in _bots.ToList())
                {
                    if (b == null || !(b is AlienHorde))
                        continue;
                    if (b._team != botTeam7)
                        continue;
                    EnqueueVehicleDespawn(b);
                }
            }

            _alienWaveActive = false;
            _alienSpawnQueue = null;
            _aliensSpawnedThisWave = 0;
            _aliensSuccessfulSpawnForHud = 0;
            _aliensAliveThisWave = 0;
            _aliensRemainingThisWave = 0;
            _tickLastAlienAnchorScan = 0;
            _tickAlienInterBatchGateUntil = 0;
            _tickNextAlienSpawn = 0;

            if (armNextRandomWaveIfSystemEnabled && EolAlienWavesEnabled)
                ScheduleNextAlienWave(now);
            else
                _tickNextAlienWave = 0;
        }

        List<ushort> BuildAlienSpawnIdList()
        {
            if (_rand == null)
                _rand = new Random(Environment.TickCount);

            var list = new List<ushort>(AlienWaveTotalSpawns);
            list.Add(200);
            ushort[] pool = { 149, 153, 148, 152, 154, 155 };
            for (int i = 0; i < AlienWaveTotalSpawns - 1; i++)
                list.Add(pool[_rand.Next(pool.Length)]);

            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = _rand.Next(i + 1);
                ushort tmp = list[i];
                list[i] = list[j];
                list[j] = tmp;
            }
            return list;
        }

        Helpers.ObjectState TryFindAlienCrashOpenPoint()
        {
            _eol.EnsureActiveSectorsReadyForPortal();
            if (_eol._activeSectors == null || _eol._activeSectors.Count == 0)
                return null;

            if (_rand == null)
                _rand = new Random(Environment.TickCount);

            const int attempts = 280;
            int[] radii = { 120, 180, 240, 320 };
            // Only fightable non-safety tiles in open sectors — no safety fallback pass.
            for (int a = 0; a < attempts; a++)
            {
                string sec = _eol._activeSectors[_rand.Next(_eol._activeSectors.Count)];
                short minX;
                short maxX;
                short minY;
                short maxY;
                if (!_eol.TryGetSectorBounds(sec, out minX, out maxX, out minY, out maxY))
                    continue;

                Helpers.ObjectState coarse = findOpenWarpInAxisAlignedRect(_arena, minX, maxX, minY, maxY, 24);
                if (coarse == null || !_eol.IsInAnyActiveSector(coarse.positionX, coarse.positionY))
                    continue;

                Helpers.ObjectState open = null;
                foreach (int rad in radii)
                {
                    open = findOpenWarpBot(_arena, coarse.positionX, coarse.positionY, rad);
                    if (open != null)
                        break;
                }
                if (open == null)
                    continue;
                if (!_eol.IsInAnyActiveSector(open.positionX, open.positionY))
                    continue;
                if (_arena.getTile(open.positionX, open.positionY).Blocked)
                    continue;
                if (_arena.getTerrainID(open.positionX, open.positionY) == AlienCrashForbiddenTerrainTypeId)
                    continue;
                if (_arena.getTerrain(open.positionX, open.positionY).safety)
                    continue;
                return open;
            }
            return null;
        }

        /// <param name="modForced">If true, a failed spawn does not push back the automatic random timer (mod will see an error instead).</param>
        void StartAlienCrashWave(int now, bool modForced = false)
        {
            if (!EolAlienWavesEnabled && !modForced)
                return;
            if (_activeThreat != EolThreatMode.Aliens && !modForced)
            {
                // Threat rotation owns scheduling — ignore legacy timer fires.
                return;
            }
            if (IsPirateRaidLive() && _activeThreat != EolThreatMode.Aliens)
            {
                ScheduleNextAlienWave(now);
                Log.write(TLog.Normal, "[Eol] Alien wave deferred — pirate raid is still active; timer reset.");
                return;
            }

            Helpers.ObjectState site = TryFindAlienCrashOpenPoint();
            if (site == null)
            {
                Log.write(TLog.Warning, "[Eol] Alien wave: no crash site tile{0}.", modForced ? " (mod)" : "; retry in 2 min.");
                if (!modForced)
                    _tickNextAlienWave = now + 120000;
                return;
            }

            _alienCrashPosX = site.positionX;
            _alienCrashPosY = site.positionY;
            AlienHordeAnchorX = site.positionX;
            AlienHordeAnchorY = site.positionY;

            createSDU(AlienCrashVehicleId, 0, 0, botTeam7, site);

            if (!_arena.Vehicles.Any((Vehicle v) => v._type.Id == AlienCrashVehicleId && v._team == botTeam7))
                Log.write(TLog.Warning, "[Eol] Alien wave: crash vehicle {0} not visible immediately after createVehicle (may appear next tick); continuing wave at ({1},{2}).", AlienCrashVehicleId, site.positionX, site.positionY);

            _arena.sendArenaMessage(
                "&WARNING A spacecraft has crashed in the district, carrying unidentified alien specimens. All personnel: stay clear of the impact site extreme risk to life.",
                _config.flag.victoryWarningBong);

            _alienSpawnQueue = BuildAlienSpawnIdList();
            _aliensSpawnedThisWave = 0;
            _aliensSuccessfulSpawnForHud = 0;
            _aliensAliveThisWave = 0;
            _aliensRemainingThisWave = AlienWaveTotalSpawns;
            _alienWaveActive = true;
            _tickAlienInterBatchGateUntil = 0;
            // Opening batch: fill to concurrent max immediately, then drip-spawn the rest.
            for (int i = 0; i < AlienWaveMaxConcurrentAlive; i++)
            {
                if (_aliensAliveThisWave >= AlienWaveMaxConcurrentAlive)
                    break;
                if (_aliensSpawnedThisWave >= AlienWaveTotalSpawns)
                    break;
                if (_alienSpawnQueue == null || _alienSpawnQueue.Count == 0)
                    break;
                SpawnOneAlienFromQueue(now);
            }
            _tickNextAlienSpawn = now + (_rand != null ? _rand.Next(AlienSpawnIntervalMinMs, AlienSpawnIntervalMaxMs + 1) : AlienSpawnIntervalMinMs);
            _tickLastAlienAnchorScan = 0;
            Log.write(TLog.Normal, "[Eol] Alien invasion started at ({0},{1}){2}.", _alienCrashPosX, _alienCrashPosY, modForced ? " [mod]" : "");
        }

        void SpawnOneAlienFromQueue(int now)
        {
            if (_alienSpawnQueue == null || _alienSpawnQueue.Count == 0)
                return;
            if (_aliensAliveThisWave >= AlienWaveMaxConcurrentAlive)
                return;
            if (_aliensSpawnedThisWave >= AlienWaveTotalSpawns)
                return;

            ushort vid = _alienSpawnQueue[0];
            _alienSpawnQueue.RemoveAt(0);

            Helpers.ObjectState openPoint = findOpenWarpBot(_arena, AlienHordeAnchorX, AlienHordeAnchorY, 140);
            if (openPoint == null)
                openPoint = new Helpers.ObjectState { positionX = AlienHordeAnchorX, positionY = AlienHordeAnchorY };

            if (_bots == null)
                _bots = new List<Bots.Bot>();

            AlienHorde bot = _arena.newBot(typeof(AlienHorde), vid, botTeam7, null, openPoint, this, null) as AlienHorde;
            if (bot != null)
            {
                EnableBotDropshipRespawn(bot, BotMaxDeathsAlien);
                if (botCount.ContainsKey(botTeam7))
                    botCount[botTeam7]++;
                else
                    botCount.Add(botTeam7, 0);
                _bots.Add(bot);
                _aliensAliveThisWave++;
                _aliensSuccessfulSpawnForHud++;
            }

            _aliensSpawnedThisWave++;
            if ((_aliensSpawnedThisWave == AlienWaveMaxConcurrentAlive || _aliensSpawnedThisWave == AlienWaveMaxConcurrentAlive * 2)
                && _aliensSpawnedThisWave < AlienWaveTotalSpawns
                && _alienSpawnQueue.Count > 0)
            {
                if (_rand == null)
                    _rand = new Random(Environment.TickCount);
                _tickAlienInterBatchGateUntil = now + _rand.Next(6500, 12001);
            }

            _tickNextAlienSpawn = now + (_rand != null ? _rand.Next(AlienSpawnIntervalMinMs, AlienSpawnIntervalMaxMs + 1) : AlienSpawnIntervalMinMs);
        }

        void UpdateAlienHordeAnchorFromShip()
        {
            foreach (Vehicle v in _arena.Vehicles)
            {
                if (v._type.Id == AlienCrashVehicleId && v._team == botTeam7)
                {
                    AlienHordeAnchorX = v._state.positionX;
                    AlienHordeAnchorY = v._state.positionY;
                    return;
                }
            }
        }

        void FinishAlienWaveCleanup(int now)
        {
            if (_activeThreat == EolThreatMode.Aliens)
            {
                CompleteThreatMode(EolThreatMode.Aliens);
                return;
            }

            AbortAlienInvasionNow(now, true);
            if (_tickNextAlienWave != 0)
            {
                int remMs = _tickNextAlienWave - now;
                if (remMs < 0)
                    remMs = 0;
                _arena.sendArenaMessage(
                    "&The alien crash site has been neutralized. Next invasion in " + FormatEolHudCountdownCoarse(remMs) + ".",
                    _config.flag.periodicBong);
            }
            else
                _arena.sendArenaMessage("&The alien crash site has been neutralized. Wreckage destroyed.", _config.flag.periodicBong);
            Log.write(TLog.Normal, "[Eol] Alien wave cleared; next_allowed_tick={0}.", _tickNextAlienWave);
        }

        /// <summary>Mod *startaliens — force-switches threat rotation to aliens.</summary>
        public string TryModForceStartAlienWave()
        {
            if (_arena == null || !_arena._bGameRunning || _bbetweengames)
                return "A match must be running.";
            if (_eol == null || !_eol.MatchSectorPlayReady)
                return "Sectors are not open yet — start or wait for the match layout.";
            if (!EolAlienWavesEnabled)
                return "Alien waves are disabled — use *enablealiens first.";
            if (_activeThreat == EolThreatMode.Aliens && _alienWaveActive)
                return "An alien wave is already in progress.";

            StartThreatMode(EolThreatMode.Aliens, announce: true);
            if (_activeThreat != EolThreatMode.Aliens || !_alienWaveActive)
                return "Could not find a crash site — ensure active sectors are open and try again.";
            return null;
        }

        /// <summary>Mod *stopaliens — ends active invasion and clears crash ship; re-arms random timer only if <see cref="Script_Eol.EolAlienWavesEnabled"/>.</summary>
        public string TryModStopAlienWave()
        {
            if (_arena == null)
                return "Arena not ready.";
            if (_activeThreat == EolThreatMode.Aliens)
            {
                CompleteThreatMode(EolThreatMode.Aliens);
                return null;
            }
            int now = Environment.TickCount;
            bool hadWave = _alienWaveActive;
            AbortAlienInvasionNow(now, false);
            if (hadWave)
                _arena.sendArenaMessage("&Alien invasion stopped by a moderator.", _config.flag.periodicBong);
            return null;
        }

        /// <summary>Mod *disablealiens — blocks automatic waves and *startaliens; clears any active invasion.</summary>
        public string TryModDisableAlienWaves()
        {
            // Disable first so CompleteThreatMode cannot re-pick Aliens.
            EolAlienWavesEnabled = false;
            if (_activeThreat == EolThreatMode.Aliens)
                CompleteThreatMode(EolThreatMode.Aliens);
            int now = Environment.TickCount;
            if (_arena != null && _arena._bGameRunning && !_bbetweengames && _alienWaveActive)
                AbortAlienInvasionNow(now, false);
            else
                _tickNextAlienWave = 0;
            return null;
        }

        /// <summary>Mod *enablealiens — allows Aliens in threat rotation again.</summary>
        public string TryModEnableAlienWaves()
        {
            EolAlienWavesEnabled = true;
            if (_arena != null && _arena._bGameRunning && !_bbetweengames && _eol != null
                && _eol.MatchSectorPlayReady && _activeThreat == EolThreatMode.None)
                BeginInitialThreatRotation();
            return null;
        }

        void PollAlienCrashEvent(int now)
        {
            if (!_arena._bGameRunning || _bbetweengames)
                return;
            if (_eol == null || !_eol.MatchSectorPlayReady)
                return;

            if (!EolAlienWavesEnabled || _activeThreat != EolThreatMode.Aliens)
            {
                if (_alienWaveActive && _activeThreat != EolThreatMode.Aliens)
                    AbortAlienInvasionNow(now, false);
                else if (!EolAlienWavesEnabled && _alienWaveActive)
                    AbortAlienInvasionNow(now, false);
                return;
            }

            if (_alienWaveActive)
            {
                if (_tickLastAlienAnchorScan == 0 || now - _tickLastAlienAnchorScan >= AlienAnchorScanMinMs)
                {
                    _tickLastAlienAnchorScan = now;
                    UpdateAlienHordeAnchorFromShip();
                }

                if (_tickAlienInterBatchGateUntil != 0 && now >= _tickAlienInterBatchGateUntil)
                    _tickAlienInterBatchGateUntil = 0;

                if (_alienSpawnQueue != null && _alienSpawnQueue.Count > 0 && now >= _tickNextAlienSpawn
                    && _aliensAliveThisWave < AlienWaveMaxConcurrentAlive
                    && _aliensSpawnedThisWave < AlienWaveTotalSpawns
                    && (_tickAlienInterBatchGateUntil == 0 || now >= _tickAlienInterBatchGateUntil))
                    SpawnOneAlienFromQueue(now);

                if ((_alienSpawnQueue == null || _alienSpawnQueue.Count == 0)
                    && _aliensSpawnedThisWave >= AlienWaveTotalSpawns
                    && _aliensAliveThisWave <= 0)
                    FinishAlienWaveCleanup(now);

                return;
            }

            // Threat mode is Aliens but wave not live yet — start (or retry after failed site).
            if (now - _tickThreatModeStarted < 2000)
                return;
            StartAlienCrashWave(now, modForced: true);
        }

        internal void OnAlienHordeBotDeath(Bots.Bot dead, InfServer.Game.Player killer)
        {
            if (dead == null || !(dead is AlienHorde))
                return;

            // Visible death FX — alien chassis often vanish too quickly without a corpse timer.
            // Do not fire GG/EZ taunt projectiles here — those are kill-only (see QueueBotKillTauntExplosion).
            if (_arena != null)
                Helpers.Vehicle_RouteDeath(_arena.Players, killer, dead, null);

            if (!_alienWaveActive)
                return;

            // Wave slot is freed only on permanent leave (botDeath / cancel), not while lingering to revive.
        }

        void FreeAlienWaveSlot(Bots.Bot bot)
        {
            if (bot == null || !(bot is AlienHorde))
                return;
            FreeAlienWaveSlot();
        }

        void FreeAlienWaveSlot()
        {
            if (!_alienWaveActive)
                return;
            _aliensAliveThisWave--;
            if (_aliensAliveThisWave < 0)
                _aliensAliveThisWave = 0;
            if (_aliensRemainingThisWave > 0)
                _aliensRemainingThisWave--;
        }
    }
}
