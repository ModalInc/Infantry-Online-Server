using System;
using System.Collections.Generic;
using System.Linq;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_Eol
{
    public partial class Script_Eol
    {
        /// <summary>
        /// Rotating field threats. Base defense bots stay up independently;
        /// only Raiders and Aliens alternate.
        /// </summary>
        public enum EolThreatMode
        {
            None = 0,
            /// <summary>Legacy — base bots are no longer part of rotation.</summary>
            BaseBots,
            Raiders,
            Aliens,
        }

        EolThreatMode _activeThreat = EolThreatMode.None;
        EolThreatMode _lastCompletedThreat = EolThreatMode.None;
        bool _threatRotateInProgress;
        int _tickThreatModeStarted;

        public EolThreatMode ActiveThreat { get { return _activeThreat; } }

        bool IsThreatModeEnabled(EolThreatMode mode)
        {
            switch (mode)
            {
                case EolThreatMode.Raiders:
                    return EolPirateRaidsEnabled;
                case EolThreatMode.Aliens:
                    return EolAlienWavesEnabled;
                case EolThreatMode.BaseBots:
                    // Base bots run continuously — not in the rotate pool.
                    return false;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Raiders / Aliens only. When <paramref name="exclude"/> is set, that mode is
        /// omitted so a completed threat does not immediately repeat — unless it is the only
        /// enabled mode (see <see cref="PickRandomThreatMode"/>).
        /// </summary>
        List<EolThreatMode> GetEnabledThreatModes(EolThreatMode exclude)
        {
            var list = new List<EolThreatMode>(2);
            if (exclude != EolThreatMode.Raiders && IsThreatModeEnabled(EolThreatMode.Raiders))
                list.Add(EolThreatMode.Raiders);
            if (exclude != EolThreatMode.Aliens && IsThreatModeEnabled(EolThreatMode.Aliens))
                list.Add(EolThreatMode.Aliens);
            return list;
        }

        /// <summary>
        /// Pick next threat from enabled systems only.
        /// Prefers a different mode than <paramref name="exclude"/>; if nothing else is enabled,
        /// repeats the only remaining enabled mode (e.g. Base-only → Base again).
        /// </summary>
        EolThreatMode PickRandomThreatMode(EolThreatMode exclude)
        {
            List<EolThreatMode> opts = GetEnabledThreatModes(exclude);
            // Only one system left (or exclude wiped the list) — allow that mode to run again.
            if (opts.Count == 0)
                opts = GetEnabledThreatModes(EolThreatMode.None);
            if (opts.Count == 0)
                return EolThreatMode.None;
            if (_rand == null)
                _rand = new Random(Environment.TickCount);
            return opts[_rand.Next(opts.Count)];
        }

        string ThreatModeDisplayName(EolThreatMode mode)
        {
            switch (mode)
            {
                case EolThreatMode.BaseBots: return "Base bots";
                case EolThreatMode.Raiders: return "Pirate raiders";
                case EolThreatMode.Aliens: return "Alien invasion";
                default: return "None";
            }
        }

        /// <summary>Match start / idle — pick and start a random enabled threat.</summary>
        void BeginInitialThreatRotation()
        {
            _activeThreat = EolThreatMode.None;
            _lastCompletedThreat = EolThreatMode.None;
            _tickNextAlienWave = 0;
            _tickNextPirateRaidAllowed = 0;

            EolThreatMode first = PickRandomThreatMode(EolThreatMode.None);
            if (first == EolThreatMode.None)
            {
                Log.write(TLog.Normal, "[Eol] Threat rotation: no enabled modes — idle.");
                PushEolActivityTicker(force: true);
                return;
            }
            StartThreatMode(first, announce: true);
        }

        /// <summary>Force-switch (mods) or start a mode. Tears down whatever is active first.</summary>
        public void StartThreatMode(EolThreatMode mode, bool announce)
        {
            if (_arena == null)
                return;

            if (mode != EolThreatMode.None && !IsThreatModeEnabled(mode))
            {
                Log.write(TLog.Warning, "[Eol] Threat rotation: cannot start {0} (disabled).", mode);
                return;
            }

            int now = Environment.TickCount;
            _threatRotateInProgress = true;
            try
            {
                if (_activeThreat != EolThreatMode.None && _activeThreat != mode)
                    TeardownThreatMode(_activeThreat, now, announceArena: false);

                _activeThreat = mode;
                _tickThreatModeStarted = now;

                if (mode == EolThreatMode.None)
                {
                    PushEolActivityTicker(force: true);
                    return;
                }

                switch (mode)
                {
                    case EolThreatMode.BaseBots:
                        // No longer used for rotation — base bots stay up via EolBaseBotsEnabled.
                        break;

                    case EolThreatMode.Raiders:
                        _tickNextPirateRaidAllowed = now;
                        // Clear leftover alien state so raider arm is never soft-blocked.
                        if (IsAlienInvasionLive())
                            AbortAlienInvasionNow(now, armNextRandomWaveIfSystemEnabled: false);
                        if (!TryPlaceRaiderPdbHordeInActiveSectors(bypassRaidCooldown: true))
                        {
                            Log.write(TLog.Warning, "[Eol] Threat rotation: raider arm failed — will retry via spawnRoam.");
                        }
                        else
                        {
                            _tickLastRoamingCaptain = 0;
                            spawnRoam(now);
                        }
                        if (announce && !_pirateRaidWaveActive)
                            _arena.sendArenaMessage("&Threat: Pirate raiders are mobilizing.", 28);
                        break;

                    case EolThreatMode.Aliens:
                        _tickNextAlienWave = now;
                        StartAlienCrashWave(now, modForced: true);
                        if (announce && !_alienWaveActive)
                            _arena.sendArenaMessage("&Threat: Alien crash inbound — searching for a site.", _config.flag.victoryWarningBong);
                        break;
                }

                Log.write(TLog.Normal, "[Eol] Threat mode started: {0}.", mode);
                PushEolActivityTicker(force: true);
            }
            finally
            {
                _threatRotateInProgress = false;
            }
        }

        /// <summary>Active mode finished — teardown leftovers and rotate to a different enabled mode.</summary>
        public void CompleteThreatMode(EolThreatMode finished)
        {
            if (_threatRotateInProgress)
                return;
            if (finished == EolThreatMode.None)
                return;
            if (_activeThreat != finished)
                return;

            int now = Environment.TickCount;
            EolThreatMode next = EolThreatMode.None;
            _threatRotateInProgress = true;
            try
            {
                TeardownThreatMode(finished, now, announceArena: false);
                _lastCompletedThreat = finished;
                _activeThreat = EolThreatMode.None;

                next = PickRandomThreatMode(finished);
                if (next == EolThreatMode.None)
                {
                    if (_arena != null)
                        _arena.sendArenaMessage("&Threat cleared. No other bot systems are enabled.", 1);
                    PushEolActivityTicker(force: true);
                    Log.write(TLog.Normal, "[Eol] Threat {0} complete — no rotation targets.", finished);
                    return;
                }

                if (_arena != null)
                {
                    _arena.sendArenaMessage(
                        "&" + ThreatModeDisplayName(finished) + " defeated. Next threat: " + ThreatModeDisplayName(next) + ".",
                        _config.flag.periodicBong);
                }
                Log.write(TLog.Normal, "[Eol] Threat {0} complete → rotating to {1}.", finished, next);
            }
            finally
            {
                _threatRotateInProgress = false;
            }

            if (next != EolThreatMode.None)
                StartThreatMode(next, announce: false);
        }

        void TeardownThreatMode(EolThreatMode mode, int now, bool announceArena)
        {
            switch (mode)
            {
                case EolThreatMode.BaseBots:
                    // Base bots persist — never wipe them on threat rotate.
                    break;
                case EolThreatMode.Raiders:
                    if (_pirateRaidWaveActive || PirateRaidHordeOperational || _currentRoamCaptains > 0)
                        ModAbortPirateRaidWave(now, sendFinishedLine: false, armCooldownForNextRaid: false);
                    _tickNextPirateRaidAllowed = 0;
                    if (announceArena)
                        _arena.sendArenaMessage("&Pirate raiders withdrawn.", 1);
                    break;
                case EolThreatMode.Aliens:
                    if (_alienWaveActive)
                        AbortAlienInvasionNow(now, armNextRandomWaveIfSystemEnabled: false);
                    _tickNextAlienWave = 0;
                    if (announceArena)
                        _arena.sendArenaMessage("&Alien invasion withdrawn.", 1);
                    break;
            }
        }

        /// <summary>Despawn all defense HQs, captains, grunts, and tracked base vehicles.</summary>
        void TeardownAllDefenseBases(int now)
        {
            if (_arena != null)
            {
                foreach (Vehicle v in _arena.Vehicles.ToList())
                {
                    if (v == null || v._type == null || v.IsDead)
                        continue;
                    if (v._type.Id == _hqVehId && v._team != null && IsDefenseBotTeam(v._team))
                        EnqueueVehicleDespawn(v);
                }
            }

            ModClearBaseDefenseBots();

            if (_defenseBaseVehicles != null)
            {
                foreach (KeyValuePair<Team, List<Vehicle>> kv in _defenseBaseVehicles.ToList())
                {
                    if (kv.Value == null)
                        continue;
                    for (int i = 0; i < kv.Value.Count; i++)
                    {
                        Vehicle bv = kv.Value[i];
                        if (bv != null && !bv.IsDead)
                            EnqueueVehicleDespawn(bv);
                    }
                }
                ClearAllDefenseBaseVehicleLists();
            }

            _tickLastEngineer = now;
            _tickDefenseCaptainRespawnEligible = now + 60000;
        }

        void ClearThreatRotationState()
        {
            int now = Environment.TickCount;
            if (_activeThreat != EolThreatMode.None)
                TeardownThreatMode(_activeThreat, now, announceArena: false);
            _activeThreat = EolThreatMode.None;
            _lastCompletedThreat = EolThreatMode.None;
            _tickNextAlienWave = 0;
            _tickNextPirateRaidAllowed = 0;
        }
    }
}
