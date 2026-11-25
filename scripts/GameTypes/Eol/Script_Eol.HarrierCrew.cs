using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-26, owner: "make another team of 4. These guys have a chief engy and a gunner for both vehicles [the Harrier
    /// GV-s and the Harrier GV-s A2]. the harrier themselves should be able to fly over green physics and they should have
    /// really good aim ... put them on low life thugs" / "people cannot join them" / "if their vehicle dies, they are on foot
    /// (similar to how the vehicle team works)" / "the gunners can be a command class. Like Squad Lead and sarge" / "but 2
    /// chief engys" / "if they die, they spawn in the dropship, and when they come out chief and the command both get
    /// somewhere and then the chief engy makes the harrier, and they both get in")
    ///
    /// Low Life Thugs (botTeam6, bot-only): two crews of 2.
    ///   - Crew 1: Chief Engineer + Squad Leader, Harrier GV-s (Kamenev SCV mk2 + LAW Launcher).
    ///   - Crew 2: Chief Engineer + Sergeant, Harrier GV-s A2 (Flak Shell Launcher + A2 Grenade Launcher).
    /// A crew starts in the dropship; once both are out (they land together), the Chief Engineer builds the Harrier on the
    /// spot (HarrierBuildMs) and both get in: one Harrier bot under the Chief's name, the gunner's guns, "ultra" aim (the
    /// assassins' - the best there is). A bot IS its vehicle here, so the gunner rides inside it.
    /// Harrier destroyed: both get out on the spot (same names) and fight on foot; each one killed comes back through the
    /// dropship, and when both have been round and are out again they build a new Harrier.
    /// The Harriers' hover height (LowZ 17) is over green physics (16): bots fly over it like the boards do (BotCombat).
    /// </summary>
    public partial class Script_Eol
    {
        public const bool HarrierCrewEnabled = true;
        const ushort VehHarrierGvs = 121, VehHarrierGvsA2 = 126;
        const int HarrierBuildMs = 4000;
        const int HarrierCrewSize = 4;
        const int ItmKamenevScv = 3070, ItmLawLauncher = 3071, ItmFlakShell = 3074, ItmA2GrenadeLauncher = 3075;

        sealed class HarrierCrew
        {
            public ushort HarrierVeh;
            public int GunnerCls;
            public int CloseGun, FarGun;
            public RoamingAttacker Harrier, Chief, Gunner;
            public string GunnerAlias;
            public bool ChiefCycled, GunnerCycled;   // been through the dropship since they got out of the Harrier
            public Helpers.ObjectState BuildSpot;
            public int BuildDoneTick, NextSpawnTick;
        }

        readonly List<HarrierCrew> _harrierCrews = new List<HarrierCrew>
        {
            new HarrierCrew { HarrierVeh = VehHarrierGvs, GunnerCls = ClsSquadLeader, CloseGun = ItmKamenevScv, FarGun = ItmLawLauncher },
            new HarrierCrew { HarrierVeh = VehHarrierGvsA2, GunnerCls = ClsSergeant, CloseGun = ItmFlakShell, FarGun = ItmA2GrenadeLauncher },
        };

        sealed class PendingHarrierFoot { public HarrierCrew Crew; public short X, Y; public string ChiefAlias; public int Bounty; }
        readonly List<PendingHarrierFoot> _pendingHarrierFoot = new List<PendingHarrierFoot>();
        int _tickHarrierPoll;

        /// <summary>The Harrier crews' team (Low Life Thugs).</summary>
        public bool IsHarrierCrewTeam(Team t) { return t != null && HarrierCrewEnabled && t == botTeam6; }

        /// <summary>A Harrier (with its crew inside).</summary>
        public static bool IsHarrierVeh(int vehId) { return vehId == VehHarrierGvs || vehId == VehHarrierGvsA2; }

        static bool BotGone(Bots.Bot b)
        {
            return b == null || (b.bCondemned && !b.bDropshipRespawnPending) || (b.IsDead && !b.bDropshipRespawnPending);
        }

        void PollHarrierCrew(int now)
        {
            if (!HarrierCrewEnabled || botTeam6 == null || _arena == null || !_arena._bGameRunning || !EolHasArenaPresence())
                return;
            PollPendingHarrierFoot(now);
            if (now - _tickHarrierPoll < 500)
                return;
            _tickHarrierPoll = now;
            if (roamBots != null && !roamBots.ContainsKey(botTeam6))
                roamBots.Add(botTeam6, 0);

            foreach (HarrierCrew c in _harrierCrews)
            {
                if (c.Harrier != null)
                {
                    if (!BotGone(c.Harrier))
                        continue;
                    c.Harrier = null;   // (gone without the death hook - despawned, sector change): the crew starts again
                }
                if (_pendingHarrierFoot.Exists(p => p.Crew == c))
                    continue;
                if (BotGone(c.Chief)) c.Chief = null;
                if (BotGone(c.Gunner)) c.Gunner = null;
                // been round the dropship (dead and coming back, or walking out)
                if (c.Chief != null && (c.Chief.IsDead || IsDeploying(c.Chief))) c.ChiefCycled = true;
                if (c.Gunner != null && (c.Gunner.IsDead || IsDeploying(c.Gunner))) c.GunnerCycled = true;

                // nobody left: both in the dropship again
                if (c.Chief == null && c.Gunner == null)
                {
                    if (now - c.NextSpawnTick >= 0)
                        SpawnHarrierCrewInDropship(c, now);
                    continue;
                }
                // one of them gone for good (respawn refused): the other one comes back too, so they start together
                if (c.Chief == null || c.Gunner == null)
                {
                    RoamingAttacker left = c.Chief ?? c.Gunner;
                    if (!left.IsDead && !IsDeploying(left))
                    {   // a new partner in the dropship
                        if (now - c.NextSpawnTick >= 0)
                            SpawnHarrierCrewInDropship(c, now);
                    }
                    continue;
                }
                // both out, both been round: build the Harrier and get in
                bool ready = c.ChiefCycled && c.GunnerCycled && !c.Chief.IsDead && !c.Gunner.IsDead
                    && !IsDeploying(c.Chief) && !IsDeploying(c.Gunner);
                if (!ready)
                {
                    c.BuildDoneTick = 0;
                    continue;
                }
                if (c.BuildDoneTick == 0)
                {
                    c.BuildDoneTick = NonZeroTick(now + HarrierBuildMs);
                    Log.write(TLog.Normal, "[Eol] Low Life Thugs: {0} builds a {1}.", c.Chief._alias, HarrierName(c));
                    continue;
                }
                if (now - c.BuildDoneTick >= 0)
                    MountHarrier(c, now);
            }
        }

        string HarrierName(HarrierCrew c)
        {
            VehInfo v = _arena._server._assets.getVehicleByID(c.HarrierVeh);
            return v != null ? v.Name : "Harrier";
        }

        /// <summary>The crew (or the missing one) in the dropship; they land together (HarrierCrewLandingFor).</summary>
        void SpawnHarrierCrewInDropship(HarrierCrew c, int now)
        {
            if (DropshipBusy)
                return;
            c.BuildSpot = null;
            c.BuildDoneTick = 0;
            if (c.Chief == null)
            {
                c.Chief = NewHarrierCrewBot(ClsChiefEngineer, null, null, now);
                c.ChiefCycled = true;
            }
            if (c.Gunner == null)
            {
                c.Gunner = NewHarrierCrewBot(c.GunnerCls, null, null, now);
                c.GunnerCycled = true;
            }
            c.NextSpawnTick = now + 5000;   // (one try every 5 s while the dropship is busy)
        }

        /// <summary>A crew member on foot: in the dropship (at == null) or on the spot. Ace, its class's guns and armour,
        /// back through the dropship when killed.</summary>
        RoamingAttacker NewHarrierCrewBot(int cls, Helpers.ObjectState at, string alias, int now)
        {
            if (!CanSpawnBotOnTeam(botTeam6))
                return null;
            bool viaDropship = at == null;
            if (viaDropship)
                at = PickDropshipSpawn() ?? PickEliteSpawnPoint();
            if (at == null)
                return null;
            _arena.NextBotAlias = alias;
            RoamingAttacker bot = _arena.newBot(typeof(RoamingAttacker), (ushort)cls, botTeam6, null, at, this, null) as RoamingAttacker;
            _arena.NextBotAlias = null;
            if (bot == null)
                return null;
            bot.SkillModeOverride = "ace";
            ApplyHumanLoadout(bot, cls, botTeam6);
            EnableBotDropshipRespawn(bot, 0);   // (killed: back through the dropship, same bot)
            if (viaDropship && _eol != null && _eol.IsInPioneerDropshipSafeZone(at.positionX, at.positionY))
                BeginDeploy(bot, now);
            else
                bot.DamageImmuneUntilTick = NonZeroTick(now + 800);
            if (_bots == null)
                _bots = new List<Bots.Bot>();
            _bots.Add(bot);
            if (roamBots != null && roamBots.ContainsKey(botTeam6))
                roamBots[botTeam6]++;
            return bot;
        }

        /// <summary>
        /// Out of the dropship portal: both crew members land at the same spot (the first one out picks it - open ground away
        /// from players), where the Chief Engineer builds their Harrier. Null = not a Harrier crew member.
        /// </summary>
        public Helpers.ObjectState HarrierCrewLandingFor(Bots.Bot b)
        {
            if (b == null || !IsHarrierCrewTeam(b._team))
                return null;
            foreach (HarrierCrew c in _harrierCrews)
            {
                if (!ReferenceEquals(c.Chief, b) && !ReferenceEquals(c.Gunner, b))
                    continue;
                RoamingAttacker other = ReferenceEquals(c.Chief, b) ? c.Gunner : c.Chief;
                // the partner is out already, not fighting: land beside it
                if (other != null && !other.IsDead && !IsDeploying(other) && other._state != null && !other.InLiveCombat)
                {
                    Helpers.ObjectState beside = OpenSpotNear(other._state.positionX, other._state.positionY, 160);
                    if (beside != null)
                        return beside;
                }
                if (c.BuildSpot == null)
                    c.BuildSpot = PickHarrierBuildSpot();
                return c.BuildSpot == null ? null : (OpenSpotNear(c.BuildSpot.positionX, c.BuildSpot.positionY, 160) ?? c.BuildSpot);
            }
            return null;
        }

        /// <summary>Open ground room enough for a Harrier, in play, away from players.</summary>
        Helpers.ObjectState PickHarrierBuildSpot()
        {
            for (int i = 0; i < 8; i++)
            {
                Helpers.ObjectState s = PickEliteSpawnPoint();
                if (s == null)
                    continue;
                Helpers.ObjectState roomy = findOpenWarpBot(_arena, s.positionX, s.positionY, 160, 56);
                if (roomy != null && InPlayArea(roomy.positionX, roomy.positionY, 120))
                    return roomy;
            }
            return PickEliteSpawnPoint();
        }

        /// <summary>The Chief Engineer has built the Harrier: both get in - one Harrier bot under the Chief's name.</summary>
        void MountHarrier(HarrierCrew c, int now)
        {
            RoamingAttacker chief = c.Chief, gunner = c.Gunner;
            Helpers.ObjectState at = findOpenWarpBot(_arena, chief._state.positionX, chief._state.positionY, 200, 56)
                ?? new Helpers.ObjectState { positionX = chief._state.positionX, positionY = chief._state.positionY };
            int bounty = BotBountyOf(chief);
            string chiefAlias = _arena.HandOverBotAlias(chief);
            c.GunnerAlias = _arena.HandOverBotAlias(gunner) ?? c.GunnerAlias;
            EnqueueVehicleDespawn(chief, force: true);
            EnqueueVehicleDespawn(gunner, force: true);
            c.Chief = c.Gunner = null;
            c.ChiefCycled = c.GunnerCycled = false;
            c.BuildDoneTick = 0;
            c.BuildSpot = null;

            _arena.NextBotAlias = chiefAlias;
            RoamingAttacker h = _arena.newBot(typeof(RoamingAttacker), c.HarrierVeh, botTeam6, null, at, this, null) as RoamingAttacker;
            _arena.NextBotAlias = null;
            if (h == null)
            {
                Log.write(TLog.Warning, "[Eol] Low Life Thugs: {0} could not be made - the crew starts again.", HarrierName(c));
                c.NextSpawnTick = now + 5000;
                return;
            }
            h.SkillModeOverride = "ultra";   // (owner: "really good aim" - the assassins' tier, the best there is)
            EquipCloseFar(h, c.CloseGun, c.FarGun);
            h.bRespawn = false;   // (destroyed: the crew gets out - OnHarrierLost)
            h.DamageImmuneUntilTick = NonZeroTick(now + 1500);
            lock (_botBounty) _botBounty[h] = bounty;
            h.DisplayBounty = bounty;
            if (_bots == null)
                _bots = new List<Bots.Bot>();
            _bots.Add(h);
            c.Harrier = h;
            Log.write(TLog.Normal, "[Eol] Low Life Thugs: {0} and {1} got in their {2} at {3}.", chiefAlias, c.GunnerAlias,
                HarrierName(c), Helpers.posToLetterCoord(at.positionX, at.positionY));
        }

        /// <summary>
        /// Bot.Death: a Harrier destroyed - its crew gets out on the spot (no kill reward for the vehicle, no death for the
        /// crew). True = it was one.
        /// </summary>
        bool OnHarrierLost(Bots.Bot dead)
        {
            if (dead == null || !IsHarrierCrewTeam(dead._team) || dead._type == null || !IsHarrierVeh(dead._type.Id))
                return false;
            HarrierCrew c = _harrierCrews.Find(x => ReferenceEquals(x.Harrier, dead));
            if (c == null)
                return false;
            c.Harrier = null;
            short x0 = dead._state != null ? dead._state.positionX : (short)0, y0 = dead._state != null ? dead._state.positionY : (short)0;
            _pendingHarrierFoot.Add(new PendingHarrierFoot { Crew = c, X = x0, Y = y0, ChiefAlias = _arena.HandOverBotAlias(dead), Bounty = BotBountyOf(dead) });
            Log.write(TLog.Normal, "[Eol] Low Life Thugs: {0} destroyed - the crew is on foot.", HarrierName(c));
            return true;
        }

        void PollPendingHarrierFoot(int now)
        {
            if (_pendingHarrierFoot.Count == 0)
                return;
            List<PendingHarrierFoot> todo = new List<PendingHarrierFoot>(_pendingHarrierFoot);
            _pendingHarrierFoot.Clear();
            foreach (PendingHarrierFoot f in todo)
            {
                HarrierCrew c = f.Crew;
                Helpers.ObjectState a = findOpenWarpBot(_arena, f.X, f.Y, 64) ?? new Helpers.ObjectState { positionX = f.X, positionY = f.Y };
                Helpers.ObjectState b = findOpenWarpBot(_arena, f.X, f.Y, 96) ?? a;
                c.Chief = NewHarrierCrewBot(ClsChiefEngineer, a, f.ChiefAlias, now);
                c.Gunner = NewHarrierCrewBot(c.GunnerCls, b, c.GunnerAlias, now);
                c.ChiefCycled = c.GunnerCycled = false;   // (they get back in only after the dropship)
                c.BuildDoneTick = 0;
                if (c.Chief != null)
                {
                    lock (_botBounty) _botBounty[c.Chief] = f.Bounty;
                    c.Chief.DisplayBounty = f.Bounty;
                }
                c.NextSpawnTick = now + 5000;
            }
        }
    }
}
