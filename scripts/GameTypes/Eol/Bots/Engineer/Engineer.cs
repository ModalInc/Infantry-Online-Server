using System;

using InfServer;
using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// Non-combat defense engineer — circuits base turrets and fires its
    /// slot-0 repair item when something needs healing.
    /// </summary>
    public partial class Engineer : Bot
    {
        public Script_Eol _baseScript;
        protected SteeringController steering;
        float _seperation;

        ItemInfo.RepairItem _repairItem;
        Vehicle _nextRet;
        /// <summary>Current healthy-circuit stop (turret / base vehicle).</summary>
        Vehicle _circuitTarget;

        int _circuitIndex;
        int _tickCircuitArrived;
        int _tickLastCircuitPath;

        int _tickLastRetarget;
        int _tickLastApproachPath;
        int _tickShotTime;
        int _tickReloadUntil;
        int _tickPrefireStarted;

        int _fireDelayMs;
        int _reloadDelayMs;
        int _ammoCapacity;
        int _ammoPerShot;
        int _ammoRemaining;

        public Engineer(VehInfo.Car type, Helpers.ObjectState state, Arena arena, Script_Eol baseScript)
            : base(type, state, arena, new SteeringController(type, state, arena))
        {
            _seperation = (float)new Random().NextDouble();
            steering = _movement as SteeringController;
            _baseScript = baseScript;
            MovementMode = BotMovementMode.Api;

            EquipRepairFromInventory(type);
        }

        void EquipRepairFromInventory(VehInfo.Car type)
        {
            if (type?.InventoryItems == null || type.InventoryItems.Length == 0 || type.InventoryItems[0] == 0)
            {
                Log.write(TLog.Warning, "[Eol] Engineer veh {0} has no InventoryItems[0] repair item.", type?.Id);
                return;
            }

            ItemInfo item = AssetManager.Manager.getItemByID(type.InventoryItems[0]);
            _repairItem = item as ItemInfo.RepairItem;
            if (_repairItem == null)
            {
                Log.write(TLog.Warning, "[Eol] Engineer InventoryItems[0]={0} is not a RepairItem.", type.InventoryItems[0]);
                return;
            }

            _fireDelayMs = Math.Max(0, _repairItem.fireDelay * 10);
            _reloadDelayMs = Math.Max(0, _repairItem.reloadDelayNormal * 10);
            _ammoCapacity = Math.Max(0, _repairItem.ammoCapacity);
            _ammoPerShot = Math.Max(1, _repairItem.ammoUsedPerShot);
            _ammoRemaining = _ammoCapacity > 0 ? _ammoCapacity : 0;
        }

        public override bool poll()
        {
            if (_baseScript != null && _baseScript.IsVehicleDespawnQueued(this))
            {
                HoldPosition();
                return false;
            }

            if (IsDead)
            {
                HoldPosition();
                if (bDropshipRespawnPending)
                    return base.poll();

                if (_baseScript != null)
                    _baseScript.OnDefenseEngineerLost(this);
                bCondemned = true;
                return base.poll();
            }

            Vehicle hq = _baseScript != null ? _baseScript.FindHqVehicleForTeam(_team) : null;
            if (hq == null)
            {
                HoldPosition();
                return base.poll();
            }

            int now = Environment.TickCount;
            _itemUseID = 0;
            RunRepairLoop(now);
            return base.poll();
        }
    }
}
