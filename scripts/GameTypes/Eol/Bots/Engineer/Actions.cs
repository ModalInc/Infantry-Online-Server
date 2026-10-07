using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_Eol
{
    public partial class Engineer : Bot
    {
        bool IsHealableBaseVehicle(Vehicle v)
        {
            if (v == null || v.IsDead || v.bCondemned || v._type == null)
                return false;
            if (v._team != _team)
                return false;
            if (v._type.Type == VehInfo.Types.Spectator)
                return false;
            if (ReferenceEquals(v, this))
                return false;
            // Skip corpses / zero-HP chassis.
            if (v._type.Hitpoints <= 0)
                return false;
            return true;
        }

        bool NeedsRepair(Vehicle v)
        {
            if (!IsHealableBaseVehicle(v))
                return false;
            return v._state.health < v._type.Hitpoints;
        }

        int GetRepairRangeRaw()
        {
            if (_repairItem == null)
                return OnTopFallbackRangeRaw;
            int d = _repairItem.repairDistance;
            if (d == 0)
                return OnTopFallbackRangeRaw;
            return Math.Abs(d);
        }

        bool InRepairRange(Vehicle target)
        {
            if (target == null || _repairItem == null)
                return false;
            int range = GetRepairRangeRaw();
            return Helpers.isInRange(range, _state, target._state);
        }

        /// <summary>Most-damaged healable base vehicle, or null if everything is full HP.</summary>
        Vehicle PickDamagedRepairTarget()
        {
            if (_baseScript == null)
                return null;

            List<Vehicle> baseVehs = _baseScript.GetDefenseBaseVehiclesForTeam(_team);
            if (baseVehs == null || baseVehs.Count == 0)
                return null;

            Vehicle worst = null;
            int worstMissing = 0;
            foreach (Vehicle v in baseVehs)
            {
                if (!NeedsRepair(v))
                    continue;
                int missing = v._type.Hitpoints - v._state.health;
                if (missing > worstMissing)
                {
                    worstMissing = missing;
                    worst = v;
                }
            }
            return worst;
        }

        /// <summary>
        /// Turrets / base layout vehicles to walk between while healthy.
        /// Prefers non-HQ stops so the eng stays on the gun line; falls back to HQ alone.
        /// Ordered by angle around the HQ for a stable loop.
        /// </summary>
        List<Vehicle> BuildTurretCircuitStops(Vehicle hq)
        {
            if (_baseScript == null)
                return null;

            List<Vehicle> baseVehs = _baseScript.GetDefenseBaseVehiclesForTeam(_team);
            if (baseVehs == null || baseVehs.Count == 0)
                return null;

            int hqId = _baseScript._hqVehId;
            List<Vehicle> stops = new List<Vehicle>(baseVehs.Count);
            foreach (Vehicle v in baseVehs)
            {
                if (!IsHealableBaseVehicle(v))
                    continue;
                if (v._type.Id == hqId)
                    continue;
                stops.Add(v);
            }

            if (stops.Count == 0)
            {
                if (hq != null && IsHealableBaseVehicle(hq))
                    stops.Add(hq);
                return stops.Count > 0 ? stops : null;
            }

            // Stable circuit: sort by angle around HQ (or average of stops).
            short cx = hq != null ? hq._state.positionX : stops[0]._state.positionX;
            short cy = hq != null ? hq._state.positionY : stops[0]._state.positionY;
            stops.Sort((a, b) =>
            {
                double aa = Math.Atan2(a._state.positionY - cy, a._state.positionX - cx);
                double bb = Math.Atan2(b._state.positionY - cy, b._state.positionX - cx);
                return aa.CompareTo(bb);
            });
            return stops;
        }

        void ClearRepairApproach()
        {
            _nextRet = null;
            _tickPrefireStarted = 0;
        }

        void ClearCircuitApproach()
        {
            _circuitTarget = null;
            _tickCircuitArrived = 0;
        }

        /// <summary>Walk turret-to-turret when healthy; peel off to repair damaged ones.</summary>
        void RunRepairLoop(int now)
        {
            if (_repairItem == null)
            {
                HoldPosition();
                return;
            }

            Vehicle hq = _baseScript != null ? _baseScript.FindHqVehicleForTeam(_team) : null;
            if (hq == null)
            {
                HoldPosition();
                return;
            }

            if (_nextRet == null || !IsHealableBaseVehicle(_nextRet) || !NeedsRepair(_nextRet)
                || now - _tickLastRetarget >= RetargetIntervalMs)
            {
                Vehicle damaged = PickDamagedRepairTarget();
                if (damaged != _nextRet)
                {
                    if (damaged != null)
                    {
                        // Leave the healthy circuit for a repair job.
                        ClearCircuitApproach();
                        Navigation?.Clear();
                    }
                    _nextRet = damaged;
                    _tickPrefireStarted = 0;
                    if (damaged != null)
                        Navigation?.Clear();
                }
                _tickLastRetarget = now;
            }

            // Nothing to fix — stay close and circuit the turrets.
            if (_nextRet == null || !NeedsRepair(_nextRet))
            {
                ClearRepairApproach();
                TickTurretCircuit(now, hq);
                return;
            }

            ClearCircuitApproach();

            bool inRange = InRepairRange(_nextRet);
            if (!inRange)
            {
                _tickPrefireStarted = 0;
                if (_tickLastApproachPath == 0 || now - _tickLastApproachPath >= ApproachPathRequeueMs)
                {
                    _tickLastApproachPath = now;
                    ApplyNavigationApproach(_nextRet._state, steerForFollowTurret);
                }
                else
                    FollowNavigationPath(steerForFollowTurret);
                return;
            }

            // On the damaged turret — stop to fire (required when movement cancels prefire).
            HoldPosition();

            if (!AbleToFireRepair(now))
                return;

            int prefireMs = Math.Max(0, _repairItem.prefireDelay * 10);
            if (_repairItem.movementCancelsPrefire && prefireMs > 0)
            {
                if (_tickPrefireStarted == 0)
                {
                    _tickPrefireStarted = now;
                    return;
                }
                if (now - _tickPrefireStarted < prefireMs)
                    return;
            }

            FireRepairAt(_nextRet, now);
        }

        /// <summary>Move between base turrets and linger briefly at each stop.</summary>
        void TickTurretCircuit(int now, Vehicle hq)
        {
            List<Vehicle> stops = BuildTurretCircuitStops(hq);
            if (stops == null || stops.Count == 0)
            {
                ClearCircuitApproach();
                HoldPosition();
                return;
            }

            // Advance past dead / despawned stops.
            int guard = stops.Count;
            while (guard-- > 0)
            {
                if (_circuitIndex < 0 || _circuitIndex >= stops.Count)
                    _circuitIndex = 0;
                Vehicle candidate = stops[_circuitIndex];
                if (candidate != null && IsHealableBaseVehicle(candidate))
                    break;
                _circuitIndex = (_circuitIndex + 1) % stops.Count;
                _tickCircuitArrived = 0;
            }

            if (_circuitIndex < 0 || _circuitIndex >= stops.Count)
                _circuitIndex = 0;

            Vehicle stop = stops[_circuitIndex];
            if (stop == null || !IsHealableBaseVehicle(stop))
            {
                ClearCircuitApproach();
                HoldPosition();
                return;
            }

            _circuitTarget = stop;

            if (Helpers.isInRange(CircuitArriveRangeRaw, _state, stop._state))
            {
                HoldPosition();
                if (_tickCircuitArrived == 0)
                    _tickCircuitArrived = now;
                if (now - _tickCircuitArrived >= CircuitDwellMs)
                {
                    _circuitIndex = (_circuitIndex + 1) % stops.Count;
                    _tickCircuitArrived = 0;
                    Navigation?.Clear();
                }
                return;
            }

            _tickCircuitArrived = 0;
            if (_tickLastCircuitPath == 0 || now - _tickLastCircuitPath >= CircuitPathRequeueMs)
            {
                _tickLastCircuitPath = now;
                ApplyNavigationApproach(stop._state, steerForCircuit);
            }
            else
                FollowNavigationPath(steerForCircuit);
        }

        bool AbleToFireRepair(int now)
        {
            if (_repairItem == null)
                return false;
            if (_tickShotTime != 0 && now - _tickShotTime < _fireDelayMs)
                return false;
            if (_tickReloadUntil != 0 && now < _tickReloadUntil)
                return false;
            if (_ammoCapacity > 0 && _ammoRemaining <= 0)
                return false;

            int energyCost = _repairItem.energyCostTerrain0;
            if (energyCost > 0 && _state.energy < energyCost)
                return false;

            return true;
        }

        void MarkRepairFired(int now)
        {
            _tickShotTime = now;
            _tickPrefireStarted = 0;

            if (_repairItem == null)
                return;

            int energyCost = _repairItem.energyCostTerrain0;
            if (energyCost > 0)
            {
                int next = _state.energy - energyCost;
                _state.energy = (short)(next < 0 ? 0 : next);
            }

            if (_ammoCapacity != 0)
            {
                _ammoRemaining -= Math.Max(1, _ammoPerShot);
                if (_ammoRemaining <= 0)
                {
                    _tickReloadUntil = now + _reloadDelayMs;
                    _ammoRemaining = _ammoCapacity;
                }
            }
        }

        /// <summary>Apply the repair item to one or more vehicles using the item's repairType / distance rules.</summary>
        void FireRepairAt(Vehicle primary, int now)
        {
            if (_repairItem == null || primary == null)
                return;

            // Only vehicle repairs for this eng.
            if (_repairItem.repairType != 1 && _repairItem.repairType != 0)
            {
                // Still allow type 0 if someone misconfigured — but eng is vehicle-focused.
            }

            if (_repairItem.repairDistance > 0)
            {
                if (!InRepairRange(primary))
                    return;
                ApplyVehicleHeal(primary);
            }
            else if (_repairItem.repairDistance < 0)
            {
                int area = -_repairItem.repairDistance;
                List<Vehicle> nearby = _arena.getVehiclesInRange(_state.positionX, _state.positionY, area);
                if (nearby == null)
                    return;
                foreach (Vehicle v in nearby)
                {
                    if (!IsHealableBaseVehicle(v))
                        continue;
                    if (!NeedsRepair(v) && !ReferenceEquals(v, primary))
                        continue;
                    ApplyVehicleHeal(v);
                }
            }
            else
            {
                // Distance 0 — treat as on-top individual repair of primary.
                if (!InRepairRange(primary))
                    return;
                ApplyVehicleHeal(primary);
            }

            _itemUseID = (short)_repairItem.id;
            MarkRepairFired(now);
        }

        void ApplyVehicleHeal(Vehicle target)
        {
            if (target == null || _repairItem == null)
                return;

            // Same formula as Vehicle.heal — avoid null Player for unoccupied computers.
            float percentage = (float)_repairItem.repairPercentage / 100;
            if (_repairItem.repairPercentage > 100)
                percentage = (float)_repairItem.repairPercentage / 1000;

            int repairAmount = _repairItem.repairAmount;
            int next = (int)(target._state.health + (percentage * target._state.health) + repairAmount);
            if (next > target._type.Hitpoints)
                next = target._type.Hitpoints;
            if (next < 0)
                next = 0;
            target._state.health = (short)next;

            Computer computer = target as Computer;
            if (computer != null)
                computer._sendUpdate = true;
        }
    }
}
