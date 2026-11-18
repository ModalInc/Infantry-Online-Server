using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// BASE ENGINEERS REBUILD (2026-11-19, owner: "lets have it so the engy in the team re builds turrets when they fall in the
    /// same spot on the chosen layout for that time ... the same 3 second delay with animation that squad team members engy
    /// do to replace turrets"). Every turret a bot base places is remembered as a slot (its kind and spot - the layout picked
    /// for that base). When one falls and the base's engineer is alive, the engineer walks to the spot, works there for 3 s
    /// with its repair kit going (the squad engineers' build), and the same turret comes back on the same spot. One at a
    /// time; a new base (a new HQ) starts a new layout.
    /// </summary>
    public partial class Script_Eol
    {
        const int BaseRebuildWorkMs = 3000;          // (the squad engineer's build time)
        const int BaseRebuildArriveRaw = 48;
        const int BaseRebuildTravelTimeoutMs = 60000;
        const int BaseRebuildRetryMs = 30000;        // couldn't get there: that slot waits this long

        sealed class TurretSlot
        {
            public int VehId;
            public short X, Y;
            public Vehicle Current;
            public int RetryAt;
        }

        sealed class RebuildJob
        {
            public TurretSlot Slot;
            public int Started, ArrivedAt;
        }

        readonly Dictionary<Team, List<TurretSlot>> _baseTurretSlots = new Dictionary<Team, List<TurretSlot>>();
        readonly Dictionary<Bot, RebuildJob> _baseRebuildJobs = new Dictionary<Bot, RebuildJob>();
        bool _placingRebuild;

        /// <summary>From RegisterDefenseBaseVehicle: a bot base's HQ starts a new layout; each of its turrets is a slot.</summary>
        void RecordBaseLayoutPiece(Team team, Vehicle v)
        {
            if (_placingRebuild || team == null || v?._type == null || v._state == null)
                return;
            if (IsHqVehId(v._type.Id))
            {
                _baseTurretSlots[team] = new List<TurretSlot>();
                foreach (Bot b in new List<Bot>(_baseRebuildJobs.Keys))
                    if (b._team == team) _baseRebuildJobs.Remove(b);
                return;
            }
            Computer c = v as Computer;
            if (c == null || c._primaryGun == null)
                return;   // (turrets only - not walls)
            List<TurretSlot> list;
            if (!_baseTurretSlots.TryGetValue(team, out list))
                _baseTurretSlots[team] = list = new List<TurretSlot>();
            list.Add(new TurretSlot { VehId = v._type.Id, X = v._state.positionX, Y = v._state.positionY, Current = v });
        }

        static bool SlotDown(TurretSlot s)
        {
            return s.Current == null || s.Current.IsDead || s.Current.bCondemned;
        }

        /// <summary>
        /// First in a base engineer's poll: true = it is on a rebuild (walking to a fallen turret's spot, or working there).
        /// </summary>
        public bool PollBaseEngineerRebuild(Engineer e, int now)
        {
            if (e == null || e.IsDead || e.bCondemned || e._team == null || e._state == null || IsDeploying(e))
                return false;
            List<TurretSlot> slots;
            if (!TeamHasLiveHqVehicle(e._team) || !_baseTurretSlots.TryGetValue(e._team, out slots) || slots.Count == 0)
            {
                _baseRebuildJobs.Remove(e);
                return false;
            }
            RebuildJob job;
            if (!_baseRebuildJobs.TryGetValue(e, out job) || !SlotDown(job.Slot))
            {   // the nearest fallen turret of the layout (not one another engineer is already on)
                job = null;
                TurretSlot best = null; double bestD = double.MaxValue;
                foreach (TurretSlot s in slots)
                {
                    if (!SlotDown(s) || now - s.RetryAt < 0)
                        continue;
                    bool taken = false;
                    foreach (KeyValuePair<Bot, RebuildJob> kv in _baseRebuildJobs)
                        if (!ReferenceEquals(kv.Key, e) && ReferenceEquals(kv.Value.Slot, s)) { taken = true; break; }
                    if (taken)
                        continue;
                    double d = Helpers.distanceTo(e._state.positionX, e._state.positionY, s.X, s.Y);
                    if (d < bestD) { bestD = d; best = s; }
                }
                if (best == null)
                {
                    _baseRebuildJobs.Remove(e);
                    return false;
                }
                _baseRebuildJobs[e] = job = new RebuildJob { Slot = best, Started = now };
            }

            TurretSlot slot = job.Slot;
            Helpers.ObjectState spot = new Helpers.ObjectState { positionX = slot.X, positionY = slot.Y };
            if (job.ArrivedAt == 0)
            {
                if (now - job.Started > BaseRebuildTravelTimeoutMs)
                {
                    slot.RetryAt = NonZeroTick(now + BaseRebuildRetryMs);
                    _baseRebuildJobs.Remove(e);
                    return false;
                }
                double dist = Helpers.distanceTo(e._state, spot);
                if (dist > BaseRebuildArriveRaw)
                {
                    if (dist < 260 && WideLineClear(e._state.positionX, e._state.positionY, slot.X, slot.Y))
                        SteerStraight(e, slot.X, slot.Y);
                    else
                        e.ApplyNavigationApproach(spot, EolHqPatrol.MakeLineClearPendingSeek(e, () => spot), new BotNavigateOptions
                        {
                            RequeueMinMs = 900,
                            PathStartIndex = 0,
                            Priority = PathPriority.Combat,
                        });
                    return true;
                }
                job.ArrivedAt = NonZeroTick(now);
                try { e.Navigation?.Clear(); } catch { }
            }
            e.HoldPosition();
            if (now - job.ArrivedAt < BaseRebuildWorkMs)
            {   // (3 s on the spot, the repair kit going)
                EngineerToolFx(e, null, now);
                return true;
            }
            _baseRebuildJobs.Remove(e);
            VehInfo vi = _arena._server._assets.getVehicleByID(slot.VehId);
            if (vi == null)
                return false;
            Helpers.ObjectState at = new Helpers.ObjectState { positionX = slot.X, positionY = slot.Y };
            Vehicle built;
            _placingRebuild = true;
            try
            {
                built = _arena.newVehicle(vi, e._team, null, at);
                RegisterDefenseBaseVehicle(e._team, built);   // (wiped with the base, like the first one)
            }
            finally { _placingRebuild = false; }
            if (built == null)
            {
                slot.RetryAt = NonZeroTick(now + BaseRebuildRetryMs);
                return false;
            }
            slot.Current = built;
            Log.write(TLog.Normal, "[Eol] {0}: engineer rebuilt a {1} at {2},{3}.", e._team._name, vi.Name, slot.X / 16, slot.Y / 16);
            return false;
        }
    }
}
