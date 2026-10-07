using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Bots
{
	// WeaponController Class
	/// Facilitates the use of weapon items for a bot — same fire/reload timing fields as players.
	///////////////////////////////////////////////////////
	public class WeaponController
	{	// Member variables
		///////////////////////////////////////////////////
		public bool bEquipped;							//Are we equipped with a weapon?

		private Helpers.ObjectState _state;				//The state of the vehicle we represent
		private WeaponSettings _settings;				//The controller settings

		private ItemInfo _type;							//The type of item weapon we're using
		private ItemInfo _primaryItem;					//The main item we're using
		private ItemInfo.Projectile _primaryProjectile;	//The projectile we're paying attention to

		private int _fireDelay;							//Ms between shots (item fireDelay)
		private int _fireDelayOther;					//Ms after weapon switch (item fireDelayOther)
		private int _entryFireDelay;					//Ms before first shot after equip
		private int _reloadTime;						//Full-clip reload ms (reloadDelayNormal)
		private int _reloadTimePartial;					//Mid-clip reload ms
		private int _reloadTimeAsync;					//Async full reload fallback
		private int _reloadTimeAsyncPartial;			//Async partial reload fallback
		private int _ammoType;							//Ammo settings
		private int _ammoCount;							//Ammo consumed per shot
		private int _ammoCapacity;						//Clip size

		private int _tickNextFireAllowed;				//Earliest tick we may fire again
		private int _tickReloadDone;					//Reload complete tick (0 = not reloading)
		private int _ammoRemaining;						//Ammo remaining in clip

		//Properties
		public ItemInfo.Projectile Projectile
		{
			get
			{
				return _primaryProjectile;
			}
		}

		public ItemInfo EquippedItem
		{
			get { return _primaryItem; }
		}

		public short ItemID
		{
			get
			{
				return (short)_type.id;
			}
		}

		public int AmmoUse
		{
			get
			{
				return _ammoCount;
			}
		}

		public int AmmoRemaining
		{
			get { return _ammoRemaining; }
		}

		/// <summary>Minimum ms between shots (from item fireDelay).</summary>
		public int FireDelayMs
		{
			get { return _fireDelay; }
		}

		/// <summary>True while a clip reload timer is active.</summary>
		public bool IsReloading
		{
			get { return _tickReloadDone != 0 && Environment.TickCount < _tickReloadDone; }
		}

		/// <summary>
		/// True when this item's prefire sequence is cancelled by movement.
		/// Weapons without this flag may fire while the bot continues driving.
		/// </summary>
		public bool MovementCancelsPrefire
		{
			get
			{
				if (_primaryItem is ItemInfo.Projectile projectile)
					return projectile.movementCancelsPrefire;
				if (_primaryItem is ItemInfo.MultiUse multiUse)
					return multiUse.movementCancelsPrefire;
				return false;
			}
		}

		/// <summary>Configured prefire delay converted from 10 ms ticks.</summary>
		public int PrefireDelayMs
		{
			get
			{
				if (_primaryItem is ItemInfo.Projectile projectile)
					return Math.Max(0, projectile.prefireDelay * 10);
				if (_primaryItem is ItemInfo.MultiUse multiUse)
					return Math.Max(0, multiUse.prefireDelay * 10);
				return 0;
			}
		}

		/// <summary>Whether this weapon must be stationary for its prefire to succeed.</summary>
		public bool RequiresStationaryFire
		{
			get { return MovementCancelsPrefire && PrefireDelayMs > 0; }
		}

		/// <summary>Movement lock used after committing a stationary shot.</summary>
		public int StationaryFireLockMs
		{
			get { return Math.Max(80, Math.Min(350, PrefireDelayMs)); }
		}

		///////////////////////////////////////////////////
		// Member Classes
		///////////////////////////////////////////////////
		/// <summary>
		/// Weapon controller settings class
		/// </summary>
		public class WeaponSettings
		{
			public byte aimFuzziness;			//The leeway we give when aiming at a target

			//Constructor
			public WeaponSettings()
			{
				aimFuzziness = 2;
			}
		}

		///////////////////////////////////////////////////
		// Member Functions
		///////////////////////////////////////////////////
		/// <summary>
		/// Generic Constructor
		/// </summary>
		public WeaponController(Helpers.ObjectState state, WeaponSettings settings)
		{
			_state = state;
			_settings = settings;

			bEquipped = false;
		}

		/// <summary>
		/// Changes the WeaponController settings
		/// </summary>
		public void setSettings(WeaponSettings settings)
		{
			_settings = settings;
		}

		static int ItemTicksToMs(int ticks)
		{
			return Math.Max(0, ticks * 10);
		}

		/// <summary>
		/// Equips the weapon controller with a new item (loads player-style timing fields).
		/// </summary>
		public bool equip(ItemInfo item)
		{
			_type = item;

			AssetManager am = Helpers._server._assets;

			_primaryItem = item;
			_primaryItem.getAmmoType(out _ammoType, out _ammoCount, out _ammoCapacity);
			_ammoRemaining = _ammoCapacity;

			_fireDelay = 0;
			_fireDelayOther = 0;
			_entryFireDelay = 0;
			_reloadTime = 0;
			_reloadTimePartial = 0;
			_reloadTimeAsync = 0;
			_reloadTimeAsyncPartial = 0;
			_primaryProjectile = null;

			if (_primaryItem is ItemInfo.MultiUse)
			{
				ItemInfo.MultiUse mug = (ItemInfo.MultiUse)_primaryItem;
				_fireDelay = ItemTicksToMs(mug.fireDelay);
				_fireDelayOther = ItemTicksToMs(mug.fireDelayOther);
				_entryFireDelay = ItemTicksToMs(mug.entryFireDelay);
				_reloadTime = ItemTicksToMs(mug.reloadDelayNormal);
				_reloadTimePartial = ItemTicksToMs(mug.reloadDelayPartial);
				_reloadTimeAsync = ItemTicksToMs(mug.reloadDelayAsynchronous);
				_reloadTimeAsyncPartial = ItemTicksToMs(mug.reloadDelayAsynchronousPartial);

				if (mug.childItems != null && mug.childItems.Count > 0)
					_primaryProjectile = am.getItemByID(mug.childItems[0].id) as ItemInfo.Projectile;
			}
			else if (_primaryItem is ItemInfo.Projectile)
			{
				_primaryProjectile = (ItemInfo.Projectile)_primaryItem;
				_fireDelay = ItemTicksToMs(_primaryProjectile.fireDelay);
				_fireDelayOther = ItemTicksToMs(_primaryProjectile.fireDelayOther);
				_entryFireDelay = ItemTicksToMs(_primaryProjectile.entryFireDelay);
				_reloadTime = ItemTicksToMs(_primaryProjectile.reloadDelayNormal);
				_reloadTimePartial = ItemTicksToMs(_primaryProjectile.reloadDelayPartial);
				// Assets typo: Asyncronous (one 'h')
				_reloadTimeAsync = ItemTicksToMs(_primaryProjectile.reloadDelayAsyncronous);
				_reloadTimeAsyncPartial = ItemTicksToMs(_primaryProjectile.reloadDelayAsynchronousPartial);
			}
			else
			{
				Log.write(TLog.Warning, "Weapon controller given invalid equip item {0}", item);
				return false;
			}

			if (_reloadTime <= 0 && _reloadTimeAsync > 0)
				_reloadTime = _reloadTimeAsync;
			if (_reloadTimePartial <= 0 && _reloadTimeAsyncPartial > 0)
				_reloadTimePartial = _reloadTimeAsyncPartial;
			if (_fireDelayOther <= 0)
				_fireDelayOther = _fireDelay;

			int now = Environment.TickCount;
			_tickReloadDone = 0;
			_tickNextFireAllowed = now + _entryFireDelay;

			bEquipped = (_primaryProjectile != null);
			return bEquipped;
		}

		/// <summary>
		/// Determines whether the bot is currently able to fire it's weapon
		/// </summary>
		public bool ableToFire()
		{
			if (!bEquipped)
				return false;

			int now = Environment.TickCount;
			if (_tickReloadDone != 0 && now < _tickReloadDone)
				return false;
			if (now < _tickNextFireAllowed)
				return false;

			return true;
		}

		/// <summary>
		/// Apply fireDelayOther after switching to this weapon (player switch cadence).
		/// </summary>
		public void NotifyWeaponSwitched()
		{
			int now = Environment.TickCount;
			int delay = _fireDelayOther > 0 ? _fireDelayOther : _fireDelay;
			int next = now + delay;
			if (next > _tickNextFireAllowed)
				_tickNextFireAllowed = next;
		}

		/// <summary>
		/// Begin a mid-clip (partial) reload using item reloadDelayPartial.
		/// </summary>
		public void BeginPartialReload()
		{
			int now = Environment.TickCount;
			int ms = _reloadTimePartial > 0 ? _reloadTimePartial : _reloadTime;
			if (ms <= 0)
				return;
			_tickReloadDone = now + ms;
			_ammoRemaining = _ammoCapacity;
			if (_tickReloadDone > _tickNextFireAllowed)
				_tickNextFireAllowed = _tickReloadDone;
		}

		/// <summary>
		/// Notifies the controller that the bot has fired a shot
		/// </summary>
		public void shotFired()
		{
			int now = Environment.TickCount;
			_tickNextFireAllowed = now + _fireDelay;

			if (_ammoCapacity != 0 && --_ammoRemaining <= 0)
			{
				int reloadMs = _reloadTime > 0 ? _reloadTime : _reloadTimeAsync;
				if (reloadMs <= 0)
					reloadMs = _fireDelay;
				_tickReloadDone = now + reloadMs;
				_ammoRemaining = _ammoCapacity;
				if (_tickReloadDone > _tickNextFireAllowed)
					_tickNextFireAllowed = _tickReloadDone;
			}
			else
			{
				_tickReloadDone = 0;
			}
		}

		/// <summary>
		/// Retrieves the appropriate angle to aim at to hit the target
		/// </summary>
		public bool isAimed(int aimAngle)
		{
			int angleDiffHi = aimAngle - _state.yaw;
			int angleDiffLo = (angleDiffHi < 0) ? 240 - Math.Abs(angleDiffHi) : -(240 - Math.Abs(angleDiffHi));

			int angleDiff;

			if (Math.Abs(angleDiffHi) < Math.Abs(angleDiffLo))
				angleDiff = angleDiffHi;
			else
				angleDiff = angleDiffLo;

			return (Math.Abs(angleDiff) <= (_settings.aimFuzziness / 2));
		}

		/// <summary>
		/// Retrieves the appropriate angle to aim at to hit the target
		/// </summary>
		public int getAimAngle(Helpers.ObjectState target)
		{
			return Helpers.computeLeadFireAngle(_state, target, _primaryProjectile.muzzleVelocity / 1000);
		}

		/// <summary>
		/// Determines whether the bot needs to aim to clockwise, anticlockwise or is ontarget
		/// </summary>
		public int testAim(Helpers.ObjectState target, out bool bAimed)
		{
			int fireAngle = Helpers.computeLeadFireAngle(_state, target, _primaryProjectile.muzzleVelocity / 1000);

			int angleDiffHi = fireAngle - _state.yaw;
			int angleDiffLo = (angleDiffHi < 0) ? 240 - Math.Abs(angleDiffHi) : -(240 - Math.Abs(angleDiffHi));

			int angleDiff;

			if (Math.Abs(angleDiffHi) < Math.Abs(angleDiffLo))
				angleDiff = angleDiffHi;
			else
				angleDiff = angleDiffLo;

			bAimed = (Math.Abs(angleDiff) <= (_settings.aimFuzziness / 2));

			if (angleDiff > 0)
				return 1;
			else
				return -1;
		}
	}
}
