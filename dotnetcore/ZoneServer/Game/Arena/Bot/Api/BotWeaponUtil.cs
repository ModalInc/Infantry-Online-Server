using System;
using Assets;
using InfServer.Game;

namespace InfServer.Bots
{
    /// <summary>
    /// Shared bot weapon plumbing: inventory equip, combat hit range, near/far ready swap.
    /// Game scripts keep mode-specific tuning (range scale, alien lure, etc.).
    /// </summary>
    public static class BotWeaponUtil
    {
        /// <summary>Melee / bite / stomp band (raw). Below this → fight on top of the target.</summary>
        public const int MeleeCombatRangeRaw = 140;

        /// <summary>
        /// Vehicle inventory: slot 0 = short/close, slot 1 = far.
        /// </summary>
        public static void ResolveWeaponItems(
            VehInfo.Car type,
            out ItemInfo itemClose,
            out ItemInfo itemFar,
            bool includeFarSlot = true)
        {
            itemClose = null;
            itemFar = null;
            if (type?.InventoryItems == null)
                return;

            if (type.InventoryItems.Length > 0 && type.InventoryItems[0] != 0)
                itemClose = AssetManager.Manager?.getItemByID(type.InventoryItems[0]);

            if (includeFarSlot && type.InventoryItems.Length > 1 && type.InventoryItems[1] != 0)
                itemFar = AssetManager.Manager?.getItemByID(type.InventoryItems[1]);
        }

        /// <summary>Equip close (slot 0) and optionally far (slot 1) from the vehicle inventory.</summary>
        public static void EquipFromVehicleInventory(
            VehInfo.Car type,
            WeaponController weaponClose,
            WeaponController weaponFar,
            bool includeFarSlot = true)
        {
            ResolveWeaponItems(type, out ItemInfo itemClose, out ItemInfo itemFar, includeFarSlot);
            if (itemClose != null && weaponClose != null)
                weaponClose.equip(itemClose);
            if (includeFarSlot && itemFar != null && weaponFar != null)
                weaponFar.equip(itemFar);
        }

        /// <summary>
        /// Wire close/far controllers into the live engage fire path.
        /// Also refreshes utility armor/movement from <see cref="BotLoadout"/>.
        /// </summary>
        public static void WireLiveCombatWeapons(
            Bot bot,
            WeaponController weaponClose,
            WeaponController weaponFar,
            bool includeFarWeapon = true,
            int seekRangeRaw = 0)
        {
            if (bot == null)
                return;

            ItemInfo itemClose = null;
            ItemInfo itemFar = null;
            if (weaponClose != null && weaponClose.ItemID != 0)
                itemClose = AssetManager.Manager?.getItemByID(weaponClose.ItemID);
            if (includeFarWeapon && weaponFar != null && weaponFar.ItemID != 0)
                itemFar = AssetManager.Manager?.getItemByID(weaponFar.ItemID);

            bot._weaponClose = weaponClose ?? bot._weaponClose ?? bot._weapon;
            bot._weapon = bot._weaponClose ?? bot._weapon;
            bot._weaponFar = includeFarWeapon ? weaponFar : null;
            bot._weaponCloseItem = itemClose;
            bot._weaponFarItem = includeFarWeapon ? itemFar : null;
            bot._combatWeaponLocked = bot._weaponClose ?? bot._weapon;

            if (seekRangeRaw > 0)
                bot.Actions?.SetSeekRange(seekRangeRaw);

            // Armor / utility movement come from BotLoadout (ConfigureFromVehicle on spawn).
            bot.RefreshUtilityEffects();
        }

        /// <summary>Equip inventory weapons then wire them for engage.</summary>
        public static void EquipAndWire(
            Bot bot,
            WeaponController weaponClose,
            WeaponController weaponFar,
            bool includeFarWeapon = true,
            int seekRangeRaw = 0)
        {
            if (bot?._type is VehInfo.Car car)
                EquipFromVehicleInventory(car, weaponClose, weaponFar, includeFarWeapon);
            WireLiveCombatWeapons(bot, weaponClose, weaponFar, includeFarWeapon, seekRangeRaw);
        }

        /// <summary>Network packet broadcast range — not combat hit range.</summary>
        public static int GetRouteRangeTicks(ItemInfo item)
        {
            if (item == null)
                return 0;
            return item.getRouteRange(out int r) ? r : 0;
        }

        /// <summary>
        /// Approximate hit range in raw map units from projectile travel + damage radius.
        /// Do not use item routeRange — that is client update broadcast distance.
        /// </summary>
        public static int GetCombatRangeRaw(ItemInfo item)
        {
            ItemInfo.Projectile proj = ResolveProjectile(item);
            if (proj == null)
                return 0;

            int blast = MaxDamageRadiusRaw(proj);
            long travel = 0;
            if (proj.muzzleVelocity > 0 && proj.aliveTime > 0)
                travel = ((long)proj.muzzleVelocity * (long)proj.aliveTime) / 10000L;

            int range = travel < MeleeCombatRangeRaw
                ? (blast > 0 ? Math.Max(blast, 48) : 72)
                : (int)Math.Max(travel, blast);

            if (range < 32) range = 32;
            if (range > 3200) range = 3200;
            return range;
        }

        public static int MaxWeaponRangeRaw(ItemInfo primary, ItemInfo secondary)
        {
            int a = GetCombatRangeRaw(primary);
            int b = GetCombatRangeRaw(secondary);
            int m = Math.Max(a, b);
            return m > 0 ? m : 1200;
        }

        public static ItemInfo.Projectile ResolveProjectile(ItemInfo item)
        {
            if (item == null)
                return null;
            if (item is ItemInfo.Projectile projectile)
                return projectile;

            if (item.relativeID != 0)
            {
                ItemInfo.Projectile viaRel = AssetManager.Manager?.getItemByID(item.relativeID) as ItemInfo.Projectile;
                if (viaRel != null)
                    return viaRel;
            }

            ItemInfo.MultiUse multi = item as ItemInfo.MultiUse;
            if (multi?.childItems == null)
                return null;

            for (int i = 0; i < multi.childItems.Count; i++)
            {
                int childId = multi.childItems[i].id;
                if (childId == 0)
                    continue;
                ItemInfo.Projectile child = ResolveProjectile(AssetManager.Manager?.getItemByID(childId));
                if (child != null)
                    return child;
            }
            return null;
        }

        public static int MaxDamageRadiusRaw(ItemInfo.Projectile proj)
        {
            if (proj == null)
                return 0;
            int m = proj.proximityRadius;
            if (proj.kineticDamageRadius > m) m = proj.kineticDamageRadius;
            if (proj.explosiveDamageRadius > m) m = proj.explosiveDamageRadius;
            if (proj.electronicDamageRadius > m) m = proj.electronicDamageRadius;
            if (proj.psionicDamageRadius > m) m = proj.psionicDamageRadius;
            if (proj.bypassDamageRadius > m) m = proj.bypassDamageRadius;
            if (proj.energyDamageRadius > m) m = proj.energyDamageRadius;
            if (proj.damageEventRadius > m) m = proj.damageEventRadius;
            return m;
        }

        /// <summary>
        /// Near when inside close hit range; far when only far reaches.
        /// Both in range → prefer ready close, else ready far (item fireDelay/reload).
        /// </summary>
        public static WeaponController PickNearFarByRangeAndReady(
            WeaponController close,
            WeaponController far,
            int closeR,
            int farR,
            double distanceRaw,
            WeaponController locked)
        {
            bool closeInRange = close != null && distanceRaw <= closeR;
            bool farInRange = far != null && distanceRaw <= farR;

            if (!closeInRange && !farInRange)
                return far ?? close;

            if (closeInRange && !farInRange)
                return close;
            if (farInRange && !closeInRange)
                return far;

            bool closeReady = close.ableToFire();
            bool farReady = far.ableToFire();

            if (closeReady && !farReady)
                return close;
            if (farReady && !closeReady)
                return far;
            if (closeReady && farReady)
            {
                // Mix attacks: mostly close, poke far on a short cycle when both ready.
                int tick = Environment.TickCount;
                if (((tick / 1400) & 1) != 0)
                    return far ?? close;
                return close;
            }

            if (locked == close || locked == far)
                return locked;
            return close ?? far;
        }

        /// <summary>Lock the engage gun and apply fireDelayOther on real switches.</summary>
        public static WeaponController CommitEngageWeapon(Bot bot, WeaponController desired)
        {
            if (desired == null)
                return null;
            if (bot == null)
                return desired;

            WeaponController locked = bot._combatWeaponLocked;
            if (desired == locked)
                return locked;

            if (locked != null)
                desired.NotifyWeaponSwitched();
            bot._combatWeaponLocked = desired;
            return desired;
        }
    }
}
