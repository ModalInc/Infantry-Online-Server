using System;
using System.Collections.Generic;
using Assets;
using InfServer.Game;

namespace InfServer.Bots
{
    public enum BotLoadoutSlotKind
    {
        Empty,
        Weapon,
        Utility,
    }

    /// <summary>
    /// Manages a bot's six vehicle inventory slots (weapons + utilities).
    /// </summary>
    public sealed class BotLoadout
    {
        public const int SlotCount = 6;

        private readonly Bot _bot;
        private readonly int[] _itemIds = new int[SlotCount];
        private readonly BotLoadoutSlotKind[] _kinds = new BotLoadoutSlotKind[SlotCount];
        private readonly bool[] _active = new bool[SlotCount];

        public BotLoadout(Bot bot)
        {
            _bot = bot;
        }

        public BotLoadoutSlotKind GetSlotKind(int slot) => _kinds[slot];

        public int GetItemId(int slot) => _itemIds[slot];

        public bool IsActive(int slot) => _active[slot];

        /// <summary>
        /// Loads all six slots from the vehicle definition and activates utilities with AlwaysOn start mode.
        /// </summary>
        public void ConfigureFromVehicle()
        {
            if (_bot._type?.InventoryItems == null)
                return;

            for (int i = 0; i < SlotCount; i++)
            {
                int itemId = i < _bot._type.InventoryItems.Length ? _bot._type.InventoryItems[i] : 0;
                SetSlot(i, itemId, autoActivateUtility: true);
            }

            SyncActiveEquipBroadcast();
        }

        public void SetSlot(int slot, int itemId, bool autoActivateUtility = false)
        {
            if (slot < 0 || slot >= SlotCount)
                return;

            _itemIds[slot] = itemId;
            _kinds[slot] = ClassifyItem(itemId);
            _active[slot] = false;

            if (_kinds[slot] == BotLoadoutSlotKind.Utility && autoActivateUtility)
            {
                ItemInfo? item = AssetManager.Manager.getItemByID(itemId);
                if (item is ItemInfo.UtilityItem util &&
                    util.startMode == ItemInfo.UtilityItem.StartMode.AlwaysOn)
                {
                    _active[slot] = true;
                }
            }
        }

        public void SetUtilityActive(int slot, bool active)
        {
            if (slot < 0 || slot >= SlotCount || _kinds[slot] != BotLoadoutSlotKind.Utility)
                return;

            _active[slot] = active;
            _bot.RefreshUtilityEffects();
        }

        public void ActivateAllUtilities()
        {
            for (int i = 0; i < SlotCount; i++)
            {
                if (_kinds[i] == BotLoadoutSlotKind.Utility && _itemIds[i] != 0)
                    _active[i] = true;
            }

            _bot.RefreshUtilityEffects();
        }

        public IEnumerable<ItemInfo.UtilityItem> GetActiveUtilities()
        {
            for (int i = 0; i < SlotCount; i++)
            {
                if (!_active[i] || _kinds[i] != BotLoadoutSlotKind.Utility || _itemIds[i] == 0)
                    continue;

                if (AssetManager.Manager.getItemByID(_itemIds[i]) is ItemInfo.UtilityItem util)
                    yield return util;
            }
        }

        public ItemInfo? GetWeaponItem(int weaponSlot)
        {
            if (weaponSlot < 0 || weaponSlot >= SlotCount)
                return null;

            if (_kinds[weaponSlot] != BotLoadoutSlotKind.Weapon || _itemIds[weaponSlot] == 0)
                return null;

            return AssetManager.Manager.getItemByID(_itemIds[weaponSlot]);
        }

        public void SyncActiveEquipBroadcast()
        {
            // Lock matches Helpers.SnapshotActiveUtilities so network serialize
            // cannot observe a mid-Clear/Add mutation of the shared list.
            lock (_bot._activeEquip)
            {
                _bot._activeEquip.Clear();
                foreach (ItemInfo.UtilityItem util in GetActiveUtilities())
                    _bot._activeEquip.Add(util);
            }
        }

        private static BotLoadoutSlotKind ClassifyItem(int itemId)
        {
            if (itemId == 0)
                return BotLoadoutSlotKind.Empty;

            ItemInfo? item = AssetManager.Manager.getItemByID(itemId);
            if (item == null)
                return BotLoadoutSlotKind.Empty;

            if (item is ItemInfo.UtilityItem)
                return BotLoadoutSlotKind.Utility;

            if (item.itemType == ItemInfo.ItemType.Projectile ||
                item.itemType == ItemInfo.ItemType.MultiUse)
                return BotLoadoutSlotKind.Weapon;

            return BotLoadoutSlotKind.Empty;
        }
    }
}
