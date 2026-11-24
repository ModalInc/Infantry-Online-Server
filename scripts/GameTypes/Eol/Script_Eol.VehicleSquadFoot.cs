using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-18, owner: "for ghost reapers, ideally they should be actually bots on vehicles, not the vehicles them selves...
    /// and if the vehicle dies they are on foot untill they die. and when they respawn they make their vehicle again")
    /// A bot IS its vehicle in this engine, so the vehicle squad plays it out:
    ///   - its board / pack destroyed: no kill reward for that - the same rider (same name) carries on on foot in its class on
    ///     the spot (Deathboard, Shimmerboard, Drop Pack: Jump Trooper; Flight Pack: SciOps; the board medic: Combat Medic);
    ///   - the rider on foot killed: a normal kill - it's gone for good, and the squad's refill brings a new vehicle out of
    ///     the dropship (the board bots no longer revive through the dropship themselves).
    /// </summary>
    public partial class Script_Eol
    {
        class PendingFoot { public Team Team; public short X, Y; public int Cls; public string Skill; public Bots.Bot Rider; public int Bounty; }
        readonly List<PendingFoot> _pendingFoot = new List<PendingFoot>();

        /// <summary>A vehicle-squad bot that is still on its board / pack (not one already on foot).</summary>
        bool IsVehicleStageBot(Bots.Bot b)
        {
            if (b == null || b._type == null || !(b is RoamingAttacker) || !IsVehicleSquadTeam(b._team))
                return false;
            int id = b._type.Id;
            if (id == VehFlightPack) return true;
            foreach (int v in VehicleSquadVehIds) if (v == id) return true;
            return false;
        }

        static int FootClassFor(int vehId)
        {
            if (vehId == VehFlightPack) return ClsSciOps;
            if (vehId == VehHoverboard) return ClsMedic;
            return ClsJumpTrooper;
        }

        /// <summary>(2026-11-26) the body a vehicle-squad bot gets off as: a Captain for the squad's captain, else its class.</summary>
        VehInfo.Car FootBodyFor(int vehId, bool captain)
        {
            return _arena._server._assets.getVehicleByID(captain ? ClsCaptain : FootClassFor(vehId)) as VehInfo.Car;
        }

        /// <summary>
        /// (2026-11-26, the other dev's mount code - Bots/RoamingAttacker/Mount.cs) the board / pack of a riding squad bot is
        /// destroyed: the SAME bot gets off on the spot - body back, full health, its class's guns and armour, no kill reward
        /// for the board, no killfeed line, no new bot. On foot it dies normally and the squad's refill sends a new vehicle.
        /// True = handled (the bot lives on).
        /// </summary>
        public bool OnBoardDestroyed(RoamingAttacker bot, InfServer.Game.Player killer)
        {
            if (bot == null || !bot.IsMounted || bot._state == null || _arena == null)
                return false;
            int boardId = bot._type != null ? bot._type.Id : 0;
            bool captain;
            lock (_vehSquadCaptains) captain = _vehSquadCaptains.Remove(bot);
            int cls = captain ? ClsCaptain : FootClassFor(boardId);
            bot.Dismount();   // body first (the order rule)
            bot._state.health = (short)Math.Min(short.MaxValue, Math.Max(1, bot._type != null ? bot._type.Hitpoints : 100));
            ApplyHumanLoadout(bot, cls, bot._team);
            if (captain)
            {   // still the squad's captain, with a Captain's armour
                bot.ArmorSource = _arena._server._assets.getVehicleByID(ClsCaptain);
                ApplyPlayerArmor(bot, ClsCaptain, useClassTable: true);
            }
            bot.bRespawn = false;   // (dies on foot = gone; the squad sends a new vehicle)
            bot.DamageImmuneUntilTick = NonZeroTick(Environment.TickCount + 800);
            Log.write(TLog.Normal, "[Eol] squad bot {0}: board {1} destroyed{2} - on foot as {3}",
                InfServer.Game.Modules.BotNameModule.GetDisplayName(bot), boardId, killer != null ? " by " + killer._alias : "", cls);
            return true;
        }

        /// <summary>Bot.Death: a vehicle-squad bot lost its vehicle - queue its rider on foot. True = it was one (no kill reward).
        /// (2026-11-26: only a fallback now - riding bots get off through OnBoardDestroyed and never die on their board.)</summary>
        bool OnVehicleSquadVehicleLost(Bots.Bot dead)
        {
            if (!IsVehicleStageBot(dead) || dead._state == null)
                return false;
            string skill;
            _strikeSkill.TryGetValue(dead._team, out skill);
            // (2026-11-24) the captain on its pack carries on on foot as a Captain (still the squad leader)
            bool captain;
            lock (_vehSquadCaptains) captain = _vehSquadCaptains.Remove(dead);
            _pendingFoot.Add(new PendingFoot { Team = dead._team, X = dead._state.positionX, Y = dead._state.positionY, Cls = captain ? ClsCaptain : FootClassFor(dead._type.Id), Skill = skill, Rider = dead, Bounty = BotBountyOf(dead) });
            return true;
        }

        /// <summary>The riders on foot, spawned on the spot their vehicle went down.</summary>
        void PollPendingFoot(int now)
        {
            if (_pendingFoot.Count == 0 || _arena == null)
                return;
            List<PendingFoot> todo = new List<PendingFoot>(_pendingFoot);
            _pendingFoot.Clear();
            foreach (PendingFoot f in todo)
            {
                if (f.Team == null || !CanSpawnBotOnTeam(f.Team))
                    continue;
                Helpers.ObjectState at = findOpenWarpBot(_arena, f.X, f.Y, 48)
                    ?? new Helpers.ObjectState { positionX = f.X, positionY = f.Y };
                // (owner: "it should be the same person" / "i still saw a random bot0494 name ... have it so its hidden") the
                // vehicle bot leaves the team list for good and the rider is created with its name - no other name shows
                _arena.NextBotAlias = _arena.HandOverBotAlias(f.Rider);
                RoamingAttacker bot = _arena.newBot(typeof(RoamingAttacker), (ushort)f.Cls, f.Team, null, at, this, null) as RoamingAttacker;
                _arena.NextBotAlias = null;
                if (bot == null)
                    continue;
                if (f.Skill != null)
                    bot.SkillModeOverride = f.Skill;
                lock (_botBounty) _botBounty[bot] = f.Bounty;   // (and the same bounty)
                bot.DisplayBounty = f.Bounty;
                ApplyHumanLoadout(bot, f.Cls, f.Team);
                bot.bRespawn = false;   // (dies on foot = gone; the squad sends a new vehicle)
                bot.DamageImmuneUntilTick = NonZeroTick(now + 800);
                if (_bots == null)
                    _bots = new List<Bots.Bot>();
                _bots.Add(bot);
                if (roamBots != null)
                    roamBots[f.Team] = EliteSquadMembers(f.Team).Count;
            }
        }
    }
}
