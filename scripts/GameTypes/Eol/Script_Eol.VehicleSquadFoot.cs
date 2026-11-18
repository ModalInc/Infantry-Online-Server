using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

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

        /// <summary>Bot.Death: a vehicle-squad bot lost its vehicle - queue its rider on foot. True = it was one (no kill reward).</summary>
        bool OnVehicleSquadVehicleLost(Bots.Bot dead)
        {
            if (!IsVehicleStageBot(dead) || dead._state == null)
                return false;
            string skill;
            _strikeSkill.TryGetValue(dead._team, out skill);
            _pendingFoot.Add(new PendingFoot { Team = dead._team, X = dead._state.positionX, Y = dead._state.positionY, Cls = FootClassFor(dead._type.Id), Skill = skill, Rider = dead, Bounty = BotBountyOf(dead) });
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
