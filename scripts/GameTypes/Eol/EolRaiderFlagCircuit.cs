using System;
using System.Collections.Generic;
using System.Text;

using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>Pirate raid objective: random active-sector flags, refreshed when sectors change.</summary>
    public partial class Script_Eol
    {
        struct RaiderSectorFlag
        {
            public short X;
            public short Y;
        }

        readonly List<RaiderSectorFlag> _raiderSectorFlags = new List<RaiderSectorFlag>();
        string _raiderSectorFlagsSignature = "";

        public bool RaiderSectorFlagsValid { get { return _raiderSectorFlags.Count > 0; } }

        /// <summary>Alias for legacy checks.</summary>
        public bool RaiderFlagCircuitValid { get { return RaiderSectorFlagsValid; } }

        void ClearRaiderFlagCircuit()
        {
            _raiderSectorFlags.Clear();
            _raiderSectorFlagsSignature = "";
        }

        bool FlagInActiveOpenSector(Arena.FlagState fs)
        {
            if (fs == null || !fs.bActive || _eol == null)
                return false;
            return _eol.IsInAnyActiveSector(fs.posX, fs.posY);
        }

        static string BuildRaiderSectorFlagsSignature(EolBoundaries eol, int flagCount)
        {
            if (eol?._activeSectors == null || eol._activeSectors.Count == 0)
                return "0|" + flagCount;

            var sb = new StringBuilder();
            sb.Append(eol._activeSectors.Count);
            foreach (string s in eol._activeSectors)
                sb.Append('|').Append(s);
            sb.Append('|').Append(flagCount);
            return sb.ToString();
        }

        /// <summary>Rebuild the list of active flags in currently open sectors.</summary>
        public void RebuildRaiderSectorFlags()
        {
            _raiderSectorFlags.Clear();

            if (_arena == null || _arena._flags == null || _eol == null)
            {
                _raiderSectorFlagsSignature = "";
                return;
            }

            _eol.EnsureActiveSectorsReadyForPortal();

            foreach (Arena.FlagState fs in _arena._flags.Values)
            {
                if (!FlagInActiveOpenSector(fs))
                    continue;

                _raiderSectorFlags.Add(new RaiderSectorFlag { X = fs.posX, Y = fs.posY });
            }

            _raiderSectorFlagsSignature = BuildRaiderSectorFlagsSignature(_eol, _raiderSectorFlags.Count);
        }

        void MaybeRefreshRaiderSectorFlags()
        {
            if (_eol == null)
                return;

            string sig = BuildRaiderSectorFlagsSignature(_eol, _raiderSectorFlags.Count);
            if (sig != _raiderSectorFlagsSignature)
                RebuildRaiderSectorFlags();
        }

        void OnRaiderCollectiveObjectiveChanged(int now)
        {
            RaiderCollectiveRoamRevision++;
            RaiderPdbAnchorX = RaiderCollectiveRoamX;
            RaiderPdbAnchorY = RaiderCollectiveRoamY;
            RaiderPdbAnchorValid = true;
            _tickRaiderObjectiveMarchUntil = now + RaiderObjectiveMarchHoldMs;
            _tickRaiderHordeAtObjective = 0;
            _raiderHordeAtObjectiveX = RaiderCollectiveRoamX;
            _raiderHordeAtObjectiveY = RaiderCollectiveRoamY;
        }

        void SetRaiderCollectiveObjective(short flagX, short flagY, int now)
        {
            RaiderCollectiveRoamX = flagX;
            RaiderCollectiveRoamY = flagY;
            RaiderCollectiveRoamValid = true;
            RaiderCaptainCircuitLastFlagX = flagX;
            RaiderCaptainCircuitLastFlagY = flagY;
            OnRaiderCollectiveObjectiveChanged(now);
        }

        /// <summary>
        /// Pick the next flag objective:
        /// 1) Unowned / enemy flags in the current sector (random).
        /// 2) Else unowned / enemy in any active sector.
        /// 3) Else (sector fully owned / nothing left to contest) any flag ≥ <see cref="RaiderFlagRepickMinSeparationRaw"/> away.
        /// </summary>
        bool PickNextRaiderFlagObjective(short excludeX, short excludeY, int now)
        {
            MaybeRefreshRaiderSectorFlags();

            if (_arena == null || _arena._flags == null || _eol == null)
            {
                RaiderCollectiveRoamValid = false;
                return false;
            }

            _eol.EnsureActiveSectorsReadyForPortal();

            if (_rand == null)
                _rand = new Random(Environment.TickCount);

            string currentSector = TryGetActiveSectorNameAt(excludeX, excludeY);

            List<Arena.FlagState> captureSameSector = new List<Arena.FlagState>();
            List<Arena.FlagState> captureAnySector = new List<Arena.FlagState>();
            List<Arena.FlagState> ownedOrAnyFar = new List<Arena.FlagState>();

            long minSepSq = (long)RaiderFlagRepickMinSeparationRaw * RaiderFlagRepickMinSeparationRaw;

            foreach (Arena.FlagState fs in _arena._flags.Values)
            {
                if (fs == null || !fs.bActive)
                    continue;
                if (!_eol.IsInAnyActiveSector(fs.posX, fs.posY))
                    continue;
                if (fs.posX == excludeX && fs.posY == excludeY)
                    continue;

                long dx = (long)fs.posX - excludeX;
                long dy = (long)fs.posY - excludeY;
                long distSq = dx * dx + dy * dy;
                bool farEnough = distSq >= minSepSq;

                bool ours = fs.team == botTeam4;
                bool sameSector = currentSector != null
                    && string.Equals(TryGetActiveSectorNameAt(fs.posX, fs.posY), currentSector, StringComparison.OrdinalIgnoreCase);

                if (!ours)
                {
                    captureAnySector.Add(fs);
                    if (sameSector)
                        captureSameSector.Add(fs);
                }

                // Fully-owned sector fallback: any other flag at least 300 raw away.
                if (farEnough)
                    ownedOrAnyFar.Add(fs);
            }

            List<Arena.FlagState> pool;
            if (captureSameSector.Count > 0)
                pool = captureSameSector;
            else if (captureAnySector.Count > 0)
                pool = captureAnySector;
            else if (ownedOrAnyFar.Count > 0)
                pool = ownedOrAnyFar;
            else
                return false;

            Arena.FlagState pick = pool[_rand.Next(pool.Count)];
            if (pick.posX == excludeX && pick.posY == excludeY)
                return false;

            SetRaiderCollectiveObjective(pick.posX, pick.posY, now);
            return true;
        }

        /// <summary>Active-sector name containing (x,y), or null.</summary>
        string TryGetActiveSectorNameAt(short x, short y)
        {
            if (_eol == null || _eol._activeSectors == null)
                return null;

            foreach (string s in _eol._activeSectors)
            {
                if (s == null)
                    continue;
                short minX, maxX, minY, maxY;
                if (!_eol.TryGetSectorBounds(s, out minX, out maxX, out minY, out maxY))
                    continue;
                if (x >= minX && x <= maxX && y >= minY && y <= maxY)
                    return s;
            }
            return null;
        }

        /// <summary>True when within arrival distance of the current flag objective.</summary>
        public bool IsNearCurrentRaiderFlagObjective(short px, short py, int arrivalRaw = -1)
        {
            if (!RaiderCollectiveRoamValid)
                return false;
            if (arrivalRaw < 0)
                arrivalRaw = RaiderCaptainObjectiveArrivalRaw;

            long arrSq = (long)arrivalRaw * arrivalRaw;
            long dx = (long)px - RaiderCollectiveRoamX;
            long dy = (long)py - RaiderCollectiveRoamY;
            return dx * dx + dy * dy <= arrSq;
        }

        /// <summary>Alias for captain arrival checks.</summary>
        public bool IsNearCurrentRaiderCircuitObjective(short px, short py, int arrivalRaw = -1)
        {
            return IsNearCurrentRaiderFlagObjective(px, py, arrivalRaw);
        }

        int CountAliveRaiders()
        {
            if (_bots == null)
                return 0;

            int count = 0;
            foreach (InfServer.Bots.Bot b in _bots)
            {
                if (b == null || b.IsDead || b._team != botTeam4)
                    continue;
                if (!(b is RoamingCaptain) && !(b is RoamingAttacker))
                    continue;
                count++;
            }
            return count;
        }

        /// <summary>Any live raider near the pin is enough to claim/advance — do not wait for a full pack majority.</summary>
        int GetRaiderAdvancePackRequirement()
        {
            if (RaiderMinRaidersNearObjectiveToAdvance > 0)
                return RaiderMinRaidersNearObjectiveToAdvance;
            return 1;
        }

        int CountRaidersNearCurrentObjective(int arrivalRaw)
        {
            if (_bots == null || !RaiderCollectiveRoamValid)
                return 0;

            int count = 0;
            foreach (InfServer.Bots.Bot b in _bots)
            {
                if (b == null || b.IsDead || b._team != botTeam4)
                    continue;
                if (!(b is RoamingCaptain) && !(b is RoamingAttacker))
                    continue;
                if (!IsNearCurrentRaiderFlagObjective(b._state.positionX, b._state.positionY, arrivalRaw))
                    continue;
                count++;
            }

            return count;
        }

        Arena.FlagState GetCurrentRaiderObjectiveFlag()
        {
            if (!RaiderCollectiveRoamValid)
                return null;
            // Match claim range so "already ours" sees the same flag TryRaiderClaim finds.
            return FindActiveFlagNear(RaiderCollectiveRoamX, RaiderCollectiveRoamY, RaiderHordeGroupArrivalRaw);
        }

        bool IsCurrentRaiderObjectiveAlreadyOurs()
        {
            Arena.FlagState fs = GetCurrentRaiderObjectiveFlag();
            return fs != null && fs.team == botTeam4;
        }

        RoamingCaptain FindLiveRaiderCaptain()
        {
            RoamingCaptain fromScript = FindOccupyingRaiderCaptain(liveOnly: true, scriptListOnly: true);
            if (fromScript != null)
                return fromScript;
            // Script list can desync after dropship / despawn races — trust the arena tracker.
            return FindOccupyingRaiderCaptain(liveOnly: true, scriptListOnly: false);
        }

        /// <summary>
        /// Live or dropship-pending pirate captains on <see cref="botTeam4"/>.
        /// Pending corpses still occupy the captain slot until revive or cancel.
        /// </summary>
        int CountOccupyingRaiderCaptains()
        {
            int n = 0;
            // Prefer arena vehicles so we never miss a captain that dropped off script _bots.
            if (_arena != null)
            {
                foreach (Vehicle v in _arena.Vehicles)
                {
                    RoamingCaptain cap = v as RoamingCaptain;
                    if (cap == null || cap._team != botTeam4)
                        continue;
                    if (cap.bCondemned && !cap.bDropshipRespawnPending)
                        continue;
                    if (cap.IsDead && !cap.bDropshipRespawnPending)
                        continue;
                    n++;
                }
                return n;
            }

            if (_bots == null)
                return 0;
            foreach (InfServer.Bots.Bot b in _bots)
            {
                if (b == null || b._team != botTeam4 || !(b is RoamingCaptain))
                    continue;
                if (b.bCondemned && !b.bDropshipRespawnPending)
                    continue;
                if (b.IsDead && !b.bDropshipRespawnPending)
                    continue;
                n++;
            }
            return n;
        }

        RoamingCaptain FindOccupyingRaiderCaptain(bool liveOnly, bool scriptListOnly)
        {
            if (scriptListOnly)
            {
                if (_bots == null)
                    return null;
                foreach (InfServer.Bots.Bot b in _bots)
                {
                    RoamingCaptain cap = b as RoamingCaptain;
                    if (cap == null || cap._team != botTeam4)
                        continue;
                    if (liveOnly)
                    {
                        if (cap.IsDead || cap.bCondemned)
                            continue;
                    }
                    else
                    {
                        if (cap.bCondemned && !cap.bDropshipRespawnPending)
                            continue;
                        if (cap.IsDead && !cap.bDropshipRespawnPending)
                            continue;
                    }
                    return cap;
                }
                return null;
            }

            if (_arena == null)
                return null;
            foreach (Vehicle v in _arena.Vehicles)
            {
                RoamingCaptain cap = v as RoamingCaptain;
                if (cap == null || cap._team != botTeam4)
                    continue;
                if (liveOnly)
                {
                    if (cap.IsDead || cap.bCondemned)
                        continue;
                }
                else
                {
                    if (cap.bCondemned && !cap.bDropshipRespawnPending)
                        continue;
                    if (cap.IsDead && !cap.bDropshipRespawnPending)
                        continue;
                }
                return cap;
            }
            return null;
        }

        /// <summary>Resync sticky <see cref="_currentRoamCaptains"/> from live/pending captains.</summary>
        void SyncRaiderCaptainCountFromOccupying()
        {
            _currentRoamCaptains = CountOccupyingRaiderCaptains();
            // Keep script _bots in sync with any arena captain that was missing from the list.
            RoamingCaptain live = FindOccupyingRaiderCaptain(liveOnly: false, scriptListOnly: false);
            if (live != null && _bots != null && !_bots.Contains(live))
                _bots.Add(live);
        }

        /// <summary>Captain must be on the flag to advance (grunts only follow / claim).</summary>
        bool IsRaiderCaptainNearCurrentObjective()
        {
            RoamingCaptain cap = FindLiveRaiderCaptain();
            if (cap == null)
                return false;
            return IsNearCurrentRaiderFlagObjective(cap._state.positionX, cap._state.positionY, RaiderHordeGroupArrivalRaw);
        }

        /// <summary>Captain at the pin (or flag already ours) unlocks claim + next flag.</summary>
        bool IsRaiderPackReadyToAdvanceObjective()
        {
            if (!_pirateRaidWaveActive || !RaiderCollectiveRoamValid)
                return false;
            if (IsCurrentRaiderObjectiveAlreadyOurs())
                return true;
            return IsRaiderCaptainNearCurrentObjective();
        }

        /// <summary>Obsolete name — any-raider gate.</summary>
        bool IsAnyRaiderAtCurrentFlagObjective()
        {
            return IsRaiderPackReadyToAdvanceObjective();
        }

        /// <summary>
        /// Advance off the excluded pin to another flag (unowned preferred, then owned).
        /// Never re-pins the excluded coords; lone-flag case roam-hops away.
        /// </summary>
        bool TryAdvanceRaiderObjectiveFrom(short excludeX, short excludeY, int now)
        {
            if (PickNextRaiderFlagObjective(excludeX, excludeY, now))
                return true;
            if (ForceAnyRaiderFlagObjective(excludeX, excludeY, now))
                return true;

            MaybeRefreshRaiderSectorFlags();
            CaptainPickRaiderCollectiveRoamPointLegacy(excludeX, excludeY);
            return RaiderCollectiveRoamValid
                && (RaiderCollectiveRoamX != excludeX || RaiderCollectiveRoamY != excludeY);
        }

        /// <summary>Once per poll — claim on arrival, then move to another sector flag (owned OK if not current).</summary>
        public void PollRaiderFlagObjectiveAdvance(int now)
        {
            if (!_pirateRaidWaveActive || !RaiderCollectiveRoamValid)
            {
                _tickRaiderHordeAtObjective = 0;
                return;
            }

            if (_raiderSectorFlags.Count == 0)
                MaybeRefreshRaiderSectorFlags();
            if (_raiderSectorFlags.Count == 0)
            {
                _tickRaiderHordeAtObjective = 0;
                return;
            }

            // March hold after a switch: do not re-pick while still leaving the last objective.
            if (IsRaiderHordeMarchingToObjective(now))
            {
                _tickRaiderHordeAtObjective = 0;
                return;
            }

            // Already ours — leave immediately for another target (owned OK if not this pin).
            if (IsCurrentRaiderObjectiveAlreadyOurs())
            {
                if (TryAdvanceRaiderObjectiveFrom(RaiderCollectiveRoamX, RaiderCollectiveRoamY, now))
                    _tickRaiderHordeAtObjective = 0;
                return;
            }

            if (!IsRaiderPackReadyToAdvanceObjective())
            {
                _tickRaiderHordeAtObjective = 0;
                return;
            }

            if (_tickRaiderHordeAtObjective == 0)
            {
                _tickRaiderHordeAtObjective = now;
                TryRaiderClaimCurrentFlagObjective();
                return;
            }

            // Keep trying to claim while dwelling (pickup delay / stragglers).
            TryRaiderClaimCurrentFlagObjective();

            if (now - _tickRaiderHordeAtObjective < RaiderObjectiveArrivalDwellMs)
                return;

            if (!TryAdvanceRaiderObjectiveFrom(RaiderCollectiveRoamX, RaiderCollectiveRoamY, now))
                return;

            _tickRaiderHordeAtObjective = 0;
        }

        /// <summary>
        /// Last-resort: any other active-sector flag pin from the cached list.
        /// Never re-selects the excluded (current) pin — returns false if that is the only option.
        /// </summary>
        bool ForceAnyRaiderFlagObjective(short excludeX, short excludeY, int now)
        {
            MaybeRefreshRaiderSectorFlags();
            if (_raiderSectorFlags.Count == 0)
                return false;

            if (_rand == null)
                _rand = new Random(Environment.TickCount);

            List<RaiderSectorFlag> candidates = new List<RaiderSectorFlag>(_raiderSectorFlags.Count);
            for (int i = 0; i < _raiderSectorFlags.Count; i++)
            {
                RaiderSectorFlag pick = _raiderSectorFlags[i];
                if (pick.X == excludeX && pick.Y == excludeY)
                    continue;
                candidates.Add(pick);
            }

            if (candidates.Count == 0)
                return false;

            RaiderSectorFlag chosen = candidates[_rand.Next(candidates.Count)];
            SetRaiderCollectiveObjective(chosen.X, chosen.Y, now);
            return true;
        }

        /// <summary>Captain alone must not advance — pack poll owns flag switches when the captain arrives.</summary>
        public bool TryAdvanceRaiderFlagObjectiveIfArrived(short captainX, short captainY)
        {
            // Shared poll advances when the captain is near; this keeps the API for captain actions.
            return false;
        }

        /// <summary>
        /// Warp a grunt to the captain when farther than <see cref="RaiderGruntSummonBehindCaptainRaw"/>.
        /// </summary>
        public bool TrySummonRaiderGruntToCaptain(RoamingAttacker grunt, Vehicle captain, int now)
        {
            if (grunt == null || captain == null || captain.IsDead || _arena == null)
                return false;
            if (grunt.IsDead || grunt.bDropshipRespawnPending || grunt.bCondemned)
                return false;
            // Fighting bots stay in the scrap — do not yank them mid-fight.
            InfServer.Game.Player fightTarget = grunt.ActivePlayerTarget;
            if (fightTarget != null && !fightTarget.IsDead)
                return false;

            long dx = (long)grunt._state.positionX - captain._state.positionX;
            long dy = (long)grunt._state.positionY - captain._state.positionY;
            long summonSq = (long)RaiderGruntSummonBehindCaptainRaw * RaiderGruntSummonBehindCaptainRaw;
            if (dx * dx + dy * dy <= summonSq)
                return false;

            Helpers.ObjectState near = FindOpenRaiderSpawnWithPeerGap(
                captain._state.positionX, captain._state.positionY);
            if (near == null)
            {
                near = findOpenWarpBot(
                    _arena, captain._state.positionX, captain._state.positionY, 180, 24);
            }
            if (near == null)
                near = findOpenWarpBot(_arena, captain._state.positionX, captain._state.positionY, 320, 36);
            if (near == null)
            {
                near = new Helpers.ObjectState
                {
                    positionX = captain._state.positionX,
                    positionY = captain._state.positionY
                };
            }

            putVehicleAtSpawnLocation(grunt, near);
            grunt.ClearActivePlayerTarget();
            try
            {
                grunt.Navigation?.Clear();
                grunt.HoldPosition();
                grunt._targetPoint = null;
            }
            catch { /* ignore */ }

            List<Player> audience = _arena.GetBotLifecycleAudience(near.positionX, near.positionY);
            if (audience != null && audience.Count > 0)
            {
                Helpers.Object_Vehicles(audience, grunt);
                grunt.MarkClientsAwareOfBot(audience);
            }
            return true;
        }

        /// <summary>Alias for roaming captain poll.</summary>
        public bool TryAdvanceRaiderFlagCircuitIfCaptainArrived(short captainX, short captainY)
        {
            return TryAdvanceRaiderFlagObjectiveIfArrived(captainX, captainY);
        }

        /// <summary>Arm the first random flag objective when a raid starts.</summary>
        public void CaptainInitRaiderFlagObjective(short fromX, short fromY)
        {
            int now = Environment.TickCount;
            RebuildRaiderSectorFlags();
            _tickRaiderHordeAtObjective = 0;
            if (!PickNextRaiderFlagObjective(short.MinValue, short.MinValue, now))
                CaptainPickRaiderCollectiveRoamPointLegacy(fromX, fromY);
        }

        /// <summary>Alias for raid start.</summary>
        public void CaptainInitRaiderFlagCircuit(short fromX, short fromY)
        {
            CaptainInitRaiderFlagObjective(fromX, fromY);
        }

        /// <summary>Force the next random flag (manual advance).</summary>
        public void CaptainAdvanceRaiderFlagObjective()
        {
            TryAdvanceRaiderObjectiveFrom(RaiderCollectiveRoamX, RaiderCollectiveRoamY, Environment.TickCount);
        }

        public void CaptainAdvanceRaiderFlagCircuit()
        {
            CaptainAdvanceRaiderFlagObjective();
        }

        /// <summary>Called when sector layout changes — move basebots and refresh raid flags.</summary>
        public void OnEolActiveSectorsChanged()
        {
            RelocateDefenseBasesForActiveSectors();

            if (!_pirateRaidWaveActive)
                return;

            short prevX = RaiderCollectiveRoamX;
            short prevY = RaiderCollectiveRoamY;
            bool hadObjective = RaiderCollectiveRoamValid;

            RebuildRaiderSectorFlags();

            if (_raiderSectorFlags.Count == 0)
            {
                RaiderCollectiveRoamValid = false;
                return;
            }

            if (!hadObjective)
                return;

            bool stillValid = false;
            for (int i = 0; i < _raiderSectorFlags.Count; i++)
            {
                RaiderSectorFlag f = _raiderSectorFlags[i];
                if (f.X == prevX && f.Y == prevY)
                {
                    stillValid = true;
                    break;
                }
            }

            if (!stillValid)
            {
                if (!PickNextRaiderFlagObjective(prevX, prevY, Environment.TickCount))
                    PickNextRaiderFlagObjective(short.MinValue, short.MinValue, Environment.TickCount);
            }
        }

        /// <summary>Legacy random flag pick when no sector flags exist.</summary>
        void CaptainPickRaiderCollectiveRoamPointLegacy(short fromX, short fromY)
        {
            Helpers.ObjectState wp = TryPickRaiderRaidRoamWaypoint(fromX, fromY, RaiderRoamWaypointMinSpacing,
                RaiderCaptainCircuitLastFlagX, RaiderCaptainCircuitLastFlagY,
                out bool fromFlag, out short fbX, out short fbY);
            if (wp != null && fromFlag)
            {
                RaiderCaptainCircuitLastFlagX = fbX;
                RaiderCaptainCircuitLastFlagY = fbY;
            }

            if (wp == null)
                wp = TryPickRaiderRoamWaypointFarFrom(fromX, fromY, RaiderRoamWaypointMinSpacing);
            if (wp == null && RaiderPdbAnchorValid)
                wp = findOpenRoamBot(_arena, RaiderPdbAnchorX, RaiderPdbAnchorY, 900);
            if (wp == null)
                return;

            RaiderCollectiveRoamX = wp.positionX;
            RaiderCollectiveRoamY = wp.positionY;
            RaiderCollectiveRoamValid = true;
            OnRaiderCollectiveObjectiveChanged(Environment.TickCount);
        }
    }
}
