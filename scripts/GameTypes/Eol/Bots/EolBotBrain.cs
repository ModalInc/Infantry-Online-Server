using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;
using Axiom.Math;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// A bot's personality (local change 2026-11-13) - rolled once when the bot is first seen, kept for its whole life,
    /// so each bot plays consistently but no two play alike. Goal: bots you can't tell from players.
    /// </summary>
    internal sealed class EolBotPersona
    {
        /// <summary>Fights to the death (true) or retreats when hurt (false).</summary>
        public bool Fighter;
        /// <summary>Survivors retreat at or below this share of max health…</summary>
        public double FleeHp;
        /// <summary>…and rejoin the fight once healed back to this share.</summary>
        public double RejoinHp;
        /// <summary>Team player: sees a teammate getting hurt → goes after the attacker. Lone wolves don't.</summary>
        public bool TeamPlayer;
        /// <summary>Turns on whoever hurts it (most players do; a few tunnel-vision their target).</summary>
        public bool Retaliates;
        /// <summary>Delay before reacting to a new target with movement (human reaction time).</summary>
        public int ReactMs;
        /// <summary>Minimum time on a target before switching to another (no flip-flopping).</summary>
        public int CommitMs;
        /// <summary>Preferred fighting distance as a share of its weapon range.</summary>
        public double RangeFrac;
        /// <summary>Strafe direction holds this long before it changes (random within the range).</summary>
        public int FlipMinMs, FlipMaxMs;
        /// <summary>How hard it strafes (0.6 lazy … 1.0 constant juking).</summary>
        public float StrafePower;
        /// <summary>How readily it runs after a target that backs off (0.5 … 1.0).</summary>
        public double ChaseEagerness;
        /// <summary>How far it will go to help a teammate (raw px).</summary>
        public int HelpRange;
        /// <summary>Bloodthirsty: when idle, roams out hunting for people to kill instead of patrolling.</summary>
        public bool Hunter;
        /// <summary>Retreats backpedalling and shooting (good players) rather than turning tail.</summary>
        public bool Backpedals;
        /// <summary>How long it keeps trying to get round cover to a target it lost sight of before giving up.</summary>
        public int PatienceMs;

        public static EolBotPersona Roll(Random r, bool alien)
        {
            EolBotPersona p = new EolBotPersona();
            // aliens: pack animals, mostly fearless; humans: more mixed
            p.Fighter = alien || r.NextDouble() < 0.35;          // aliens: bloodthirsty animals - never retreat
            p.FleeHp = 0.20 + r.NextDouble() * 0.10;            // 20-30 %
            p.RejoinHp = 0.68 + r.NextDouble() * 0.17;          // 68-85 %
            p.TeamPlayer = r.NextDouble() < (alien ? 0.75 : 0.55);
            p.Retaliates = r.NextDouble() < 0.80;
            p.ReactMs = 180 + r.Next(470);                      // 0.18-0.65 s
            p.CommitMs = 2500 + r.Next(3500);                   // 2.5-6 s
            p.RangeFrac = 0.45 + r.NextDouble() * 0.40;         // 45-85 % of weapon range
            p.FlipMinMs = 800 + r.Next(700);
            p.FlipMaxMs = p.FlipMinMs + 1200 + r.Next(1800);
            p.StrafePower = (float)(0.60 + r.NextDouble() * 0.40);
            p.ChaseEagerness = 0.5 + r.NextDouble() * 0.5;
            p.HelpRange = 700 + r.Next(700);
            p.Hunter = alien && r.NextDouble() < 0.35;          // the rest guard their ship (no ship = all hunt)
            if (alien)
            {   // wild animal: never still, darting side to side on a jittery rhythm
                p.StrafePower = (float)(0.85 + r.NextDouble() * 0.15);
                p.FlipMinMs = 350 + r.Next(350);
                p.FlipMaxMs = p.FlipMinMs + 500 + r.Next(700);
                p.ChaseEagerness = 0.8 + r.NextDouble() * 0.2;
            }
            p.PatienceMs = 1800 + r.Next(2200);
            return p;
        }

        public override string ToString()
        {
            return string.Format("{0}{1}, {2}, react {3}ms, commit {4}ms, range {5:P0}, strafe {6:F2}{7}{8}",
                Fighter ? "fighter" : "survivor", Fighter ? "" : string.Format(" (flees <{0:P0}, back at {1:P0}, {2})", FleeHp, RejoinHp,
                    Backpedals ? "backpedals" : "turns and runs"),
                TeamPlayer ? "team player" : "lone wolf", ReactMs, CommitMs, RangeFrac, StrafePower,
                Hunter ? ", hunter" : "", "");
        }
    }

    /// <summary>
    /// Player-like combat for every Eol bot (human and alien), local change 2026-11-13:
    ///  - locked on: always turns to face (lead-aim) its target
    ///  - strafes / drifts around the target at its preferred range, flipping direction on its own rhythm (and at once
    ///    when a wall stops it); steps in when too far, backs off when too close
    ///  - chases straight when the target runs; path-chase (the old ChaseTargetWithoutLos) only when out of sight
    ///  - personality: fighter vs survivor (retreat low, heal once safe, come back), team player (protects hurt
    ///    teammates by targeting their attacker) vs lone wolf, retaliation, reaction delay, target commitment
    /// Drives through BotDrive's body-relative combat drive (forward / strafe / rotate), so no world-axis mirroring.
    /// Firing stays with the existing combat code (it runs before this each poll).
    /// </summary>
    internal static class EolBotBrain
    {
        public const bool Enabled = true;
        /// <summary>Announce each bot's personality in the server window when it first fights (for tuning).</summary>
        public const bool LogPersonas = true;

        const int SafeDistanceRaw = 1500;       // retreating bot counts as safe this far from its threat…
        const int SafeForMs = 4000;             // …for this long, then starts healing
        const double HealPerSecond = 0.04;      // share of max HP per second while safe
        const int AllyScanMs = 300;
        const int AllyHurtWindowMs = 2500;
        const int SelfHurtWindowMs = 1500;
        const int ProtectHoldMs = 4500;

        sealed class Mind
        {
            public EolBotPersona P;
            public Random R;
            public float StrafeSign = 1f;
            public int NextFlip;
            public object Committed;
            public int CommitUntil;
            public int ReactUntil;
            public double LastDist;
            public int LastDistTick;
            public short StuckX, StuckY;
            public int StuckTick;
            public bool Retreating;
            public object Threat;
            public int SafeSince;
            public int LastHealTick;
            public int NextAllyScan;
            public object Protect;
            public int ProtectUntil;
            public bool Logged;
            // lost-sight chase watchdog
            public short NoLosX, NoLosY;
            public int NoLosTick;
            // idle hunting / roaming
            public object HuntTarget;
            public int HuntPickTick;
            public short HuntGoalX, HuntGoalY;
            public Helpers.ObjectState RoamDest;
            public int RoamPickTick;
            public short IdleX, IdleY;
            public int IdleTick;
            public int OddsTick;
            public object Turret;
            public int TurretPickTick;
            public int SearchArrivedTick;
            public int IdleStareTick;
            public short IdleStareX, IdleStareY;
            public readonly Dictionary<object, int> IgnoreUntil = new Dictionary<object, int>();
            // progress tracking (MakingProgress)
            public int ProgTick;
            public double ProgBest;
            public short ProgGoalX, ProgGoalY, ProgX, ProgY;
        }

        const int IgnoreAfterGiveUpMs = 8000;
        const int IgnoreUnlessWithinRaw = 500;

        /// <summary>Engine hook: skip a target this bot recently gave up on, unless it has come close.</summary>
        static bool IsTargetIgnored(Bot self, object target, double dist)
        {
            Mind m;
            if (self == null || target == null || !Minds.TryGetValue(self, out m) || m.IgnoreUntil.Count == 0)
                return false;
            int until;
            if (!m.IgnoreUntil.TryGetValue(target, out until))
                return false;
            if (Environment.TickCount - until >= 0)
            {
                m.IgnoreUntil.Remove(target);
                return false;
            }
            return dist > IgnoreUnlessWithinRaw;
        }

        static readonly ConditionalWeakTable<Bot, Mind> Minds = new ConditionalWeakTable<Bot, Mind>();
        static readonly List<WeakReference<Bot>> Known = new List<WeakReference<Bot>>();

        static Mind Get(Bot b)
        {
            Mind m;
            if (Minds.TryGetValue(b, out m))
                return m;
            m = new Mind();
            m.R = new Random(unchecked(Environment.TickCount * 31 + b._id * 7919));
            m.P = EolBotPersona.Roll(m.R, b is AlienHorde);
            // better aimers (skill tier) are the ones who backpedal-and-shoot: ~15 % of rookies .. ~95 % of aces
            double bpChance = 0.5;
            try { if (b.Skill != null) bpChance = b.Skill.TierPick(0.15, 0.95); } catch { }
            // aliens never backpedal (too slow) - they turn tail and run like an animal
            m.P.Backpedals = !(b is AlienHorde) && m.R.NextDouble() < bpChance;
            if (b.SkillModeOverride == "ace" || b.SkillModeOverride == "ultra")
            {   // elite: sharp, disciplined, sticks together. Assassins ("ultra") and elite aliens never retreat.
                bool alienElite = b is AlienHorde;
                bool assassin = b.SkillModeOverride == "ultra";
                m.P.Fighter = assassin || alienElite || m.R.NextDouble() < 0.6;
                m.P.TeamPlayer = true;
                m.P.Retaliates = !assassin;          // assassins stay on the contract
                m.P.Backpedals = !alienElite;
                if (alienElite)
                    m.P.Hunter = true;
                m.P.ReactMs = 140 + m.R.Next(160);
                m.P.StrafePower = (float)(0.85 + m.R.NextDouble() * 0.15);
                m.P.PatienceMs = 3500 + m.R.Next(2500);
                m.P.HelpRange = 1400;
                if (assassin)
                {
                    m.P.ReactMs = 90 + m.R.Next(80);
                    m.P.PatienceMs = 9000;           // relentless: keeps working round cover to the mark
                    m.P.StrafePower = 1f;
                }
            }
            m.StrafeSign = m.R.Next(2) == 0 ? -1f : 1f;
            Minds.Add(b, m);
            lock (Known) Known.Add(new WeakReference<Bot>(b));
            return m;
        }

        /// <summary>The bot's personality (rolled on first use).</summary>
        public static EolBotPersona PersonaOf(Bot b) { return b == null ? null : Get(b).P; }

        static int MaxHp(Bot b) { return b?._type != null && b._type.Hitpoints > 0 ? b._type.Hitpoints : 1; }
        static double HpFrac(Bot b) { return b?._state == null ? 1 : Math.Max(0, (double)b._state.health / MaxHp(b)); }

        static Helpers.ObjectState StateOf(object who)
        {
            if (who is Player p) return p._state;
            if (who is Vehicle v) return v._state;
            return null;
        }

        /// <summary>A live, hostile, fightable target for <paramref name="self"/> (turrets excluded - bots don't target them).</summary>
        static bool IsHostileTarget(Bot self, Script_Eol script, object who)
        {
            if (who is Player p)
                return !p.IsDead && !p.IsSpectator && p._team != null && p._team != self._team;
            if (who is Bot b)
                return !b.IsDead && !b.bCondemned && script != null && script.ShouldEolBotTeamsFight(self._team, b._team);
            return false;
        }

        static void SuggestTarget(Bot self, object who, ref Player playerTarget)
        {
            if (who is Player p)
            {
                playerTarget = p;
                self.Actions?.SuggestPlayerTarget(p);
            }
            else if (who is Bot b)
            {
                playerTarget = null;
                self.Actions?.SuggestBotTarget(b);
                EolBotSenseUtil.SetStickyHostileBot(self._id, b);
            }
        }

        /// <summary>
        /// Before the combat module picks a target: protect a hurt teammate (team players), hit back at our own
        /// attacker (retaliators), otherwise stick with the committed target until its commit time runs out.
        /// </summary>
        public static void BeforeCombat(Bot self, Script_Eol script, ref Player playerTarget, int now)
        {
            if (!Enabled || self == null || self._arena == null || self._state == null || self._team == null)
                return;
            try
            {
                Mind m = Get(self);
                EolBotPersona P = m.P;

                // team player: scan nearby teammates for one taking damage
                if (P.TeamPlayer && now >= m.NextAllyScan)
                {
                    m.NextAllyScan = now + AllyScanMs;
                    object best = null; int bestTick = 0;
                    List<Vehicle> allies = self._arena.getVehiclesInRange(self._state.positionX, self._state.positionY, P.HelpRange,
                        delegate (Vehicle v) { return v is Bot && v != self && v._team == self._team && !v.IsDead; });
                    foreach (Vehicle v in allies)
                    {
                        Bot ally = (Bot)v;
                        if (ally.LastHurtBy == null || now - ally.LastHurtTick > AllyHurtWindowMs || ally.LastHurtTick <= bestTick)
                            continue;
                        if (!IsHostileTarget(self, script, ally.LastHurtBy))
                            continue;
                        Helpers.ObjectState s = StateOf(ally.LastHurtBy);
                        if (s == null || !Helpers.isInRange(Script_Eol.EolBotCombatSeekRangeRaw, self._state, s))
                            continue;
                        best = ally.LastHurtBy; bestTick = ally.LastHurtTick;
                    }
                    if (best != null)
                    {
                        if (!ReferenceEquals(best, m.Protect) && LogPersonas)
                            Log.write(TLog.Normal, "[bot brain] {0} (team player) protects a teammate - targeting its attacker", self._id);
                        m.Protect = best;
                        m.ProtectUntil = now + ProtectHoldMs;
                    }
                }
                if (m.Protect != null && (now > m.ProtectUntil || !IsHostileTarget(self, script, m.Protect)))
                    m.Protect = null;

                object want = m.Protect;

                // retaliation: someone else is shooting ME
                if (want == null && P.Retaliates && self.LastHurtBy != null && now - self.LastHurtTick <= SelfHurtWindowMs
                    && IsHostileTarget(self, script, self.LastHurtBy)
                    && !ReferenceEquals(self.LastHurtBy, m.Committed))
                    want = self.LastHurtBy;

                if (want != null)
                {
                    if (want is Player wp)
                        EolBotPerception.Reveal(self._state, wp);   // knows where the shooter is
                    m.Committed = want;
                    m.CommitUntil = now + P.CommitMs;
                    SuggestTarget(self, want, ref playerTarget);
                    return;
                }

                // commitment: keep the current target until its time is up (if it's still fightable)
                if (m.Committed != null && now < m.CommitUntil && IsHostileTarget(self, script, m.Committed))
                    SuggestTarget(self, m.Committed, ref playerTarget);
            }
            catch (Exception ex)
            {
                Log.write(TLog.Warning, "[bot brain] BeforeCombat: " + ex.Message);
            }
        }

        /// <summary>
        /// Combat movement for this poll (replaces the plain ChaseTargetWithoutLos call). <paramref name="target"/> is
        /// the Player or Bot being fought, <paramref name="dest"/> its state. Returns false when the chase is over.
        /// </summary>
        public static bool CombatMove(Bot self, Script_Eol script, int now, object target, Helpers.ObjectState dest,
            Func<InfantryVehicle, Vector3> fallback)
        {
            if (!Enabled || self == null || dest == null || self._arena == null || self._state == null)
                return EolBotCombatUtil.ChaseTargetWithoutLos(self, now, dest, fallback);
            try
            {
                Mind m = Get(self);
                EolBotPersona P = m.P;
                if (!m.Logged && LogPersonas)
                {
                    m.Logged = true;
                    Log.write(TLog.Normal, "[bot brain] {0} {1}: {2}", self._id, self._type?.Name, P);
                }

                // a new target: commit to it and take a human moment to react
                if (target != null && !ReferenceEquals(target, m.Committed))
                {
                    if (m.Committed == null || now >= m.CommitUntil || !IsHostileTarget(self, script, m.Committed))
                    {
                        m.Committed = target;
                        m.CommitUntil = now + P.CommitMs;
                        m.ReactUntil = now + P.ReactMs;
                        m.LastDistTick = 0;
                    }
                }

                // survivors: retreat when low, until healed - or when hurt and badly outnumbered (live to fight on)
                double hp = HpFrac(self);
                if (!P.Fighter && !m.Retreating && hp < 0.6 && now - m.OddsTick >= 1000)
                {
                    m.OddsTick = now;
                    int foes = 0, friends = 0;
                    foreach (Vehicle v in self._arena.getVehiclesInRange(self._state.positionX, self._state.positionY, 800,
                        delegate (Vehicle v) { return v is Bot && v != self && !v.IsDead; }))
                    {
                        if (v._team == self._team) friends++;
                        else if (IsHostileTarget(self, script, v)) foes++;
                    }
                    foreach (Player p in self._arena.getPlayersInRange(self._state.positionX, self._state.positionY, 800))
                        if (IsHostileTarget(self, script, p) && EolBotPerception.Knows(self._state, p)) foes++;
                    if (foes >= friends + 3)
                    {
                        m.Retreating = true;
                        m.Threat = target;
                        m.SafeSince = 0;
                        if (LogPersonas)
                            Log.write(TLog.Normal, "[bot brain] {0} is outnumbered ({1} vs {2}) and pulling back", self._id, foes, friends + 1);
                    }
                }
                if (!P.Fighter)
                {
                    if (!m.Retreating && hp <= P.FleeHp)
                    {
                        m.Retreating = true;
                        m.Threat = target;
                        m.SafeSince = 0;
                        if (LogPersonas)
                            Log.write(TLog.Normal, "[bot brain] {0} is hurt ({1:P0}) and retreating", self._id, hp);
                    }
                    if (m.Retreating)
                    {
                        if (hp >= P.RejoinHp)
                        {
                            m.Retreating = false;
                            if (LogPersonas)
                                Log.write(TLog.Normal, "[bot brain] {0} recovered ({1:P0}) - back in the fight", self._id, hp);
                        }
                        else
                        {
                            Retreat(self, m, dest, target, now);
                            return true;
                        }
                    }
                }

                SteeringController steering = self._movement as SteeringController;
                if (steering == null)
                    return EolBotCombatUtil.ChaseTargetWithoutLos(self, now, dest, fallback);

                // a turret is the one shooting me right now: take it out first
                if (self.LastHurtBy is Computer && now - self.LastHurtTick < 2500 && TryEngageTurret(self, script, now))
                    return true;

                double dist = Helpers.distanceTo(self._state, dest);

                // Safety net: never stand still staring at a target out of reach. If it's been out of range and we
                // haven't moved in 2.5 s, drop it and get back to roaming / patrolling.
                {
                    int reachNow = EolBotCombatUtil.GetCombatRangeRaw(self._weapon != null && self._weapon.ItemID != 0
                        ? AssetManager.Manager?.getItemByID(self._weapon.ItemID) : self._weaponCloseItem);
                    bool outOfReach = reachNow > 0 && dist > Math.Max(reachNow * 1.5, 320);
                    if (!outOfReach)
                        m.IdleStareTick = 0;
                    else if (m.IdleStareTick == 0)
                    {
                        m.IdleStareTick = now; m.IdleStareX = self._state.positionX; m.IdleStareY = self._state.positionY;
                    }
                    else if (now - m.IdleStareTick > 2500)
                    {
                        int sdx = self._state.positionX - m.IdleStareX, sdy = self._state.positionY - m.IdleStareY;
                        if (sdx * sdx + sdy * sdy < 40 * 40)
                        {
                            m.IdleStareTick = 0;
                            GiveUp(self, m, target);
                            return false;
                        }
                        m.IdleStareTick = now; m.IdleStareX = self._state.positionX; m.IdleStareY = self._state.positionY;
                    }
                }
                // can shoot it from here (shot LOS over low walls; maser carriers through walls)
                bool los = BotCombat.CanDirectEngage(self, dest);

                int range = EolBotCombatUtil.GetCombatRangeRaw(self._weaponCloseItem);
                if (range <= 0) range = EolBotCombatUtil.GetCombatRangeRaw(self._weaponFarItem);
                if (range <= 0) range = 300;
                // aliens fight at their spit/plasma range when they have one, not on top of you
                if (self is AlienHorde && self._weaponFarItem != null)
                    range = Math.Max(range, EolBotCombatUtil.GetCombatRangeRaw(self._weaponFarItem));
                range = Math.Max(140, Math.Min(700, range));
                double pref = Math.Max(150, range * P.RangeFrac);   // never run into the target
                // splash-only bots (grenade launcher etc.) keep outside their minimum fire distance
                int minFire = BotWeaponUtil.GetMinFireRangeRaw(self._weaponCloseItem);
                int minFar = self._weaponFarItem != null ? BotWeaponUtil.GetMinFireRangeRaw(self._weaponFarItem) : minFire;
                int needMin = Math.Min(minFire, minFar);
                if (needMin > 0)
                    pref = Math.Max(pref, needMin * 1.3);

                // is the target opening the distance (running)?
                double openRate = 0;
                if (m.LastDistTick != 0 && now - m.LastDistTick >= 60 && now - m.LastDistTick <= 1200)
                    openRate = (dist - m.LastDist) * 1000.0 / (now - m.LastDistTick);
                if (m.LastDistTick == 0 || now - m.LastDistTick >= 60)
                {
                    m.LastDist = dist;
                    m.LastDistTick = now;
                }
                bool targetRunning = openRate > 45 * (1.5 - P.ChaseEagerness);

                // out of sight: path round the cover (never stand at a wall aiming). If it isn't getting
                // anywhere within its patience, give up on that target like a player would.
                if (!los)
                {
                    // Lost sight of a player: go where we last SAW them (a little ahead in the way they were
                    // running) to get eyes on them again - not to wherever they really are now. Reached that spot
                    // and they're not there: give up like a person would.
                    Helpers.ObjectState chaseDest = dest;
                    if (target is Player tp && !EolBotPerception.SeesNow(self._state, tp)
                        && EolBotPerception.TryGetLastSighting(self._state, tp, out EolBotPerception.Sighting seen))
                    {
                        int sx = seen.X + (int)Math.Max(-250, Math.Min(250, seen.VelX * 0.08));
                        int sy = seen.Y + (int)Math.Max(-250, Math.Min(250, seen.VelY * 0.056));
                        sx = Math.Max(16, Math.Min(short.MaxValue - 16, sx));
                        sy = Math.Max(16, Math.Min(short.MaxValue - 16, sy));
                        if (!EolHqPatrol.IsOpenForBot(self, (short)sx, (short)sy)) { sx = seen.X; sy = seen.Y; }
                        chaseDest = new Helpers.ObjectState { positionX = (short)sx, positionY = (short)sy };
                        if (Helpers.distanceTo(self._state, chaseDest) < 90)
                        {
                            if (m.SearchArrivedTick == 0)
                                m.SearchArrivedTick = now;
                            else if (now - m.SearchArrivedTick > 1200)
                            {
                                m.SearchArrivedTick = 0;
                                GiveUp(self, m, target);
                                return false;
                            }
                        }
                        else
                            m.SearchArrivedTick = 0;
                    }

                    // Real progress only: getting closer to where it's going, or actually travelling a found route.
                    // (Sliding along a red wall toward someone on the other side is NOT progress - that used to
                    // count as "moving", so bots ground against walls forever.) No progress within its patience:
                    // give up and go back to roaming / patrol.
                    if (!MakingProgress(self, m, chaseDest, now, P.PatienceMs))
                    {
                        GiveUp(self, m, target);
                        return false;
                    }
                    if (!EolBotCombatUtil.ChaseTargetWithoutLos(self, now, chaseDest, fallback))
                    {
                        GiveUp(self, m, target);
                        return false;
                    }
                    return true;
                }
                m.NoLosTick = 0;
                m.SearchArrivedTick = 0;

                // far away (but visible): path-chase, shooting when the gun reaches
                if (dist > range * 1.8)
                {
                    bool chasing = EolBotCombatUtil.ChaseTargetWithoutLos(self, now, dest, fallback);
                    AimFire(self, dest, target, dist, Helpers.computeLeadFireAngle(self._state, dest, 10));
                    return chasing;
                }

                // can shoot over it (low physics) but can't walk through it: if it needs to close in, take the
                // route round - still facing and shooting - instead of pushing into the obstacle
                bool walkClear = EolHqPatrol.TileLineClear(self._arena, self._state.positionX, self._state.positionY,
                    dest.positionX, dest.positionY);
                if (!walkClear && dist > pref * 1.15)
                {
                    if (!MakingProgress(self, m, dest, now, P.PatienceMs + 1500))
                    {
                        GiveUp(self, m, target);
                        return false;
                    }
                    Helpers.ObjectState goal = new Helpers.ObjectState { positionX = dest.positionX, positionY = dest.positionY };
                    self.ApplyNavigationApproach(goal, EolHqPatrol.MakeLineClearPendingSeek(self, () => goal), new BotNavigateOptions
                    {
                        RequeueMinMs = 1200,
                        PathStartIndex = 0,
                        Priority = PathPriority.Combat,
                    });
                    AimFire(self, dest, target, dist, Helpers.computeLeadFireAngle(self._state, dest, 10));
                    return true;
                }

                // strafe rhythm, plus flip at once if strafing has us stuck on something
                if (m.NextFlip == 0 || now >= m.NextFlip)
                {
                    if (m.NextFlip != 0)
                        m.StrafeSign = -m.StrafeSign;
                    m.NextFlip = now + m.R.Next(P.FlipMinMs, P.FlipMaxMs);
                }
                if (m.StuckTick == 0 || now - m.StuckTick >= 450)
                {
                    if (m.StuckTick != 0)
                    {
                        int dx = self._state.positionX - m.StuckX, dy = self._state.positionY - m.StuckY;
                        if (dx * dx + dy * dy < 36)
                        {
                            m.StrafeSign = -m.StrafeSign;
                            m.NextFlip = now + m.R.Next(P.FlipMinMs, P.FlipMaxMs);
                        }
                    }
                    m.StuckX = self._state.positionX; m.StuckY = self._state.positionY; m.StuckTick = now;
                }

                float forward, strafe;
                if (dist > range * 1.05 || (targetRunning && dist > pref * 1.2))
                {   // chase: straight at them, a little weave
                    forward = 1f;
                    strafe = 0.18f * m.StrafeSign;
                }
                else
                {   // fight: drift around them at the preferred range
                    if (dist > pref * 1.15) forward = 0.55f;
                    else if (dist < pref * 0.70) forward = needMin > 0 && dist < needMin ? -1f : -0.55f;
                    else forward = (float)Math.Max(-0.3, Math.Min(0.3, (dist - pref) / pref * 0.8));
                    strafe = P.StrafePower * m.StrafeSign;
                }

                DriveAimFire(self, steering, dest, target, dist, forward, strafe);
                return true;
            }
            catch (Exception ex)
            {
                Log.write(TLog.Warning, "[bot brain] CombatMove: " + ex.Message);
                return EolBotCombatUtil.ChaseTargetWithoutLos(self, now, dest, fallback);
            }
        }

        /// <summary>
        /// Move (body-relative) while staying locked on: the body turns onto the gun's real aim angle (so aim and
        /// body agree and shots go off), and against players it pulls the trigger every time the gun is ready -
        /// unloading like a player. Against bots the existing bot-vs-bot fire path does the shooting.
        /// </summary>
        static void DriveAimFire(Bot self, SteeringController steering, Helpers.ObjectState dest, object target,
            double dist, float forward, float strafe)
        {
            int aimYaw = Helpers.computeLeadFireAngle(self._state, dest, 10);
            double aimErr = Helpers.calculateDifferenceInAngles(self._state.yaw, aimYaw);
            float rotate = (float)Math.Max(-1.0, Math.Min(1.0, aimErr / 35.0));
            BotDrive.ApplyCombatDrivePublic(self, steering, forward, strafe, rotate);   // new drive request this tick
            AimFire(self, dest, target, dist, aimYaw);
        }

        /// <summary>Lock the aim onto the gun's aim point and fire whenever the gun is ready (players).</summary>
        static void AimFire(Bot self, Helpers.ObjectState dest, object target, double dist, int aimYaw)
        {
            WeaponController gun = null;
            try { gun = InfServer.Game.Modules.BotActions.BotActionHelpers.ResolveEngageWeapon(self, dest, true, dist); }
            catch { }
            // a wall in the way but we carry a maser: use the maser, it goes through
            WeaponController maser = BotWeaponUtil.WallPiercingWeapon(self);
            if (maser != null && !ReferenceEquals(gun, maser)
                && !EolBotTargetUtil.TileLineClearForWeaponFire(self._arena, self._state.positionX, self._state.positionY, dest.positionX, dest.positionY))
                gun = BotWeaponUtil.CommitEngageWeapon(self, maser);
            if (gun != null)
                self._weapon = gun;

            // Keep the aim on the drive request (SetDrive replaced it), so the body tracks the gun's aim point
            // and the dodge layer knows we are fighting (it no longer makes us stand still).
            int gunAim = gun != null && gun.Projectile != null ? gun.getAimAngle(dest) : aimYaw;
            self.SetAim(gunAim, true);

            if (gun == null)
            {
                _fNoGun++;
                return;
            }
            // out of the gun's reach: aim, don't waste shots
            int reach = BotWeaponUtil.GetCombatRangeRaw(gun.ItemID != 0 ? AssetManager.Manager?.getItemByID(gun.ItemID) : null);
            if (reach > 0 && dist > Math.Max(reach * 1.5, 320))
            {
                _fOutOfReach++;
                return;
            }
            // Unload: fire every time the gun is ready and on target. (Hits on enemy BOTS are resolved for every
            // bot shot in OnBotShotFired below, whichever code path fired it.)
            if (!gun.ableToFire()) { _fCooldown++; return; }
            // Only weapons that must be fired standing still (charge-up / prefire) make the bot plant: stop,
            // fire, then it keeps moving next tick. Everything else fires on the move.
            int plantMs = 0;
            if (gun.RequiresStationaryFire)
            {
                plantMs = gun.StationaryFireLockMs;
                self.SetDrive(0f, 0f, 0f);
                self.SetAim(gun.Projectile != null ? gun.getAimAngle(dest) : aimYaw, true);
            }
            if (BotCombat.TryAimAndFire(self, gun, dest, requireLineOfSight: true, freezeMs: plantMs))
                _fFired++;
            else
                _fNotAimed++;

            int now = Environment.TickCount;
            if (LogPersonas && now - _tickFireLog > 15000)
            {
                _tickFireLog = now;
                Log.write(TLog.Normal, "[bot brain] fire: {0} shots, held - {1} cooldown, {2} aiming/blocked, {3} out of reach, {4} no gun",
                    _fFired, _fCooldown, _fNotAimed, _fOutOfReach, _fNoGun);
            }
        }

        static int _fFired, _fCooldown, _fNotAimed, _fOutOfReach, _fNoGun, _tickFireLog;

        static readonly Random HitRng = new Random();
        static int _hitsLanded, _shotsAtBots, _tickHitLog;

        /// <summary>
        /// A bot fired at something. If that something is an enemy bot, the shot lands with the shooter's accuracy
        /// (skill tier, falling off with distance) and does the weapon's real damage through the target's armour.
        /// </summary>
        static void OnBotShotFired(Bot shooter, WeaponController gun, Helpers.ObjectState targetState)
        {
            if (shooter == null || gun?.Projectile == null || targetState == null)
                return;
            Bot victim = EolBotPerception.BotAt(targetState);
            Vehicle hitVeh = victim;
            if (victim == null && shooter._arena != null)
            {   // a turret? (their state objects aren't registered - match by reference nearby)
                foreach (Vehicle v in shooter._arena.getVehiclesInRange(targetState.positionX, targetState.positionY, 8))
                    if (v is Computer && ReferenceEquals(v._state, targetState) && !v.IsDead && v._team != shooter._team)
                    { hitVeh = v; break; }
            }
            if (hitVeh == null || hitVeh == shooter || hitVeh.IsDead || hitVeh._team == shooter._team)
                return;
            if (victim != null && victim.bCondemned)
                return;
            double dist = Helpers.distanceTo(shooter._state, targetState);
            int reach = BotWeaponUtil.GetCombatRangeRaw(gun.ItemID != 0 ? AssetManager.Manager?.getItemByID(gun.ItemID) : null);
            if (reach > 0 && dist > Math.Max(reach * 1.5, 320))
                return;

            _shotsAtBots++;
            double acc = 0.6;
            try { if (shooter.Skill != null) acc = shooter.Skill.TierPick(0.45, 0.88); } catch { }
            acc *= Math.Max(0.4, 1.0 - dist / 1800.0);
            if (HitRng.NextDouble() >= acc)
                return;

            _hitsLanded++;
            if (victim != null)
            {
                victim.NoteHurtBy(shooter);
                shooter._arena?.NoteBotDamagedByBot(victim, shooter);
            }
            if (hitVeh is Computer turretHit)
                // turrets: their own damage path (health update to clients + proper destroy when it hits 0)
                turretHit.applyExplosion(null, turretHit._state.positionX, turretHit._state.positionY, gun.Projectile);
            else
                hitVeh.applyBotHit(gun.Projectile);

            int now = Environment.TickCount;
            if (LogPersonas && now - _tickHitLog > 15000)
            {
                _tickHitLog = now;
                Log.write(TLog.Normal, "[bot brain] bot-vs-bot: {0} hits from {1} shots so far (e.g. {2} hit {3}, now {4} hp)",
                    _hitsLanded, _shotsAtBots, shooter._id, victim._id, victim._state.health);
            }
        }

        // ---------------------------------------------------------------- turrets
        const int TurretSightRaw = 900;

        /// <summary>A hostile armed turret this bot can see (nearest first), or null.</summary>
        static Computer FindHostileTurret(Bot self, Script_Eol script, int range)
        {
            Computer best = null; double bestD = double.MaxValue;
            foreach (Vehicle v in self._arena.getVehiclesInRange(self._state.positionX, self._state.positionY, range,
                delegate (Vehicle x) { return x is Computer c && c._primaryGun != null && !x.IsDead && x._team != self._team; }))
            {
                Computer c = (Computer)v;
                if (IsTargetIgnored(self, c, Helpers.distanceTo(self._state, c._state)))
                    continue;   // couldn't reach it recently
                if (c._team != null && c._team._name != null && c._team._name.StartsWith("Bot", StringComparison.OrdinalIgnoreCase)
                    && script != null && !script.ShouldEolBotTeamsFight(self._team, c._team))
                    continue;   // allied bot turret
                if (!BotCombat.SightLineClear(self._arena, self._state.positionX, self._state.positionY, c._state.positionX, c._state.positionY))
                    continue;
                double d = Helpers.distanceTo(self._state, c._state);
                if (d < bestD) { bestD = d; best = c; }
            }
            return best;
        }

        /// <summary>
        /// Attack a hostile turret it can see (bots don't just stand there and get shot): close to weapon range,
        /// strafe, shoot - hits resolve server-side like bot-vs-bot. Call when idle (patrol / hunt), or from
        /// combat when a turret is the one hurting it. Returns true when it is busy with a turret.
        /// </summary>
        public static bool TryEngageTurret(Bot self, Script_Eol script, int now)
        {
            if (!Enabled || self?._arena == null || self._state == null || self.IsDead)
                return false;
            try
            {
                Mind m = Get(self);
                if (m.Retreating)
                    return false;
                Computer turret = m.Turret as Computer;
                if (turret == null || turret.IsDead || now - m.TurretPickTick > 1500)
                {
                    m.TurretPickTick = now;
                    turret = FindHostileTurret(self, script, TurretSightRaw);
                    m.Turret = turret;
                }
                if (turret == null)
                    return false;
                SteeringController steering = self._movement as SteeringController;
                if (steering == null)
                    return false;

                double dist = Helpers.distanceTo(self._state, turret._state);
                int range = EolBotCombatUtil.GetCombatRangeRaw(self._weaponCloseItem);
                int rFar = self._weaponFarItem != null ? EolBotCombatUtil.GetCombatRangeRaw(self._weaponFarItem) : 0;
                range = Math.Max(140, Math.Min(700, Math.Max(range, rFar)));
                double pref = Math.Max(150, range * 0.7);
                bool shotClear = EolBotTargetUtil.TileLineClearForWeaponFire(self._arena, self._state.positionX, self._state.positionY,
                    turret._state.positionX, turret._state.positionY);

                if (!shotClear || dist > range * 1.05)
                {   // get into a firing spot: path toward it, aiming/firing when possible - unless it can't get there
                    if (!MakingProgress(self, m, turret._state, now, 4000))
                    {
                        m.IgnoreUntil[turret] = now + 12000;
                        m.Turret = null;
                        ResetProgress(m);
                        self.Navigation?.Clear();
                        return false;
                    }
                    Helpers.ObjectState goal = new Helpers.ObjectState { positionX = turret._state.positionX, positionY = turret._state.positionY };
                    self.ApplyNavigationApproach(goal, EolHqPatrol.MakeLineClearPendingSeek(self, () => goal), new BotNavigateOptions
                    {
                        RequeueMinMs = 1500,
                        PathStartIndex = 0,
                        Priority = PathPriority.Combat,
                    });
                    if (shotClear)
                        AimFire(self, turret._state, turret, dist, Helpers.computeLeadFireAngle(self._state, turret._state, 10));
                    return true;
                }

                if (m.NextFlip == 0 || now >= m.NextFlip)
                {
                    if (m.NextFlip != 0) m.StrafeSign = -m.StrafeSign;
                    m.NextFlip = now + m.R.Next(m.P.FlipMinMs, m.P.FlipMaxMs);
                }
                float forward = dist > pref * 1.15 ? 0.55f : (dist < pref * 0.7 ? -0.55f : 0f);
                DriveAimFire(self, steering, turret._state, turret, dist, forward, m.P.StrafePower * m.StrafeSign);
                return true;
            }
            catch (Exception ex)
            {
                Log.write(TLog.Warning, "[bot brain] TryEngageTurret: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Progress toward <paramref name="goal"/>: true while the bot has got at least 48 px closer, or travelled
        /// 96 px along a real navigation route, within the last <paramref name="patienceMs"/>. Shared by every
        /// chase / hunt so no bot grinds against a wall toward a target it can't reach.
        /// </summary>
        static bool MakingProgress(Bot self, Mind m, Helpers.ObjectState goal, int now, int patienceMs)
        {
            double d = Helpers.distanceTo(self._state, goal);
            int gx = goal.positionX - m.ProgGoalX, gy = goal.positionY - m.ProgGoalY;
            bool newGoal = m.ProgTick == 0 || gx * gx + gy * gy > 300 * 300;   // a genuinely different goal: restart
            if (newGoal)
            {
                m.ProgTick = now; m.ProgBest = d;
                m.ProgGoalX = goal.positionX; m.ProgGoalY = goal.positionY;
                m.ProgX = self._state.positionX; m.ProgY = self._state.positionY;
                return true;
            }
            int mx = self._state.positionX - m.ProgX, my = self._state.positionY - m.ProgY;
            bool closer = d < m.ProgBest - 48;
            bool onRoute = self.Navigation != null && self.Navigation.HasPath && mx * mx + my * my >= 96 * 96;
            if (closer || onRoute)
            {
                m.ProgTick = now; m.ProgBest = Math.Min(m.ProgBest, d);
                m.ProgX = self._state.positionX; m.ProgY = self._state.positionY;
                return true;
            }
            return now - m.ProgTick < patienceMs;
        }

        static void ResetProgress(Mind m) { m.ProgTick = 0; }

        /// <summary>Stop chasing a target it can't reach / see: forget it, drop the lock, back to its own business.</summary>
        static void GiveUp(Bot self, Mind m, object target)
        {
            if (target is Player p)
                EolBotPerception.Forget(self._state, p);
            else if (target is Bot)
                EolBotSenseUtil.SetStickyHostileBot(self._id, null);
            if (ReferenceEquals(m.Committed, target))
            {
                m.Committed = null;
                m.CommitUntil = 0;
            }
            if (ReferenceEquals(m.HuntTarget, target))
                m.HuntTarget = null;
            if (target != null)
                m.IgnoreUntil[target] = Environment.TickCount + IgnoreAfterGiveUpMs;
            ResetProgress(m);
            m.NoLosTick = 0;
            try { self.Actions?.Clear(); } catch { }
            self.Navigation?.Clear();
            if (LogPersonas)
                Log.write(TLog.Normal, "[bot brain] {0} lost its target behind cover - giving up the chase", self._id);
        }

        /// <summary>A bot of this persona hunts when idle (aliens: some; any alien whose ship is gone: all).</summary>
        public static bool IsHunter(Bot self, bool hasHome)
        {
            if (!Enabled || self == null)
                return false;
            return !hasHome || Get(self).P.Hunter;
        }

        /// <summary>
        /// Idle behaviour for bloodthirsty bots: track the nearest enemy it can find (players first) and path to
        /// them - round walls, never through them; when nobody is around, roam to fresh open areas. Once it gets
        /// sight of someone, the normal combat code takes over. <paramref name="home"/> (optional) keeps roaming
        /// loosely around a home point.
        /// </summary>
        public static void Hunt(Bot self, Script_Eol script, int now, Helpers.ObjectState home)
        {
            if (self?._arena == null || self._state == null)
                return;
            if (TryEngageTurret(self, script, now))
                return;
            try
            {
                Mind m = Get(self);
                Arena arena = self._arena;

                // pick / keep a quarry (hunters "track" by radar, but path to it - no aiming through walls)
                if (now - m.HuntPickTick >= 1000 || m.HuntTarget == null || !IsHostileTarget(self, script, m.HuntTarget))
                {
                    m.HuntPickTick = now;
                    object best = null; double bestD = double.MaxValue;
                    foreach (Player p in arena.getPlayersInRange(self._state.positionX, self._state.positionY, HuntRadarRaw))
                    {
                        if (p == null || p.IsDead || p.IsSpectator || p._team == self._team || p._state == null)
                            continue;
                        if (arena.getTerrain(p._state.positionX, p._state.positionY).safety)
                            continue;
                        if (EolBotSenseUtil.PlayerHasActiveCloak(p))
                            continue;
                        double d = Helpers.distanceTo(self._state, p._state);
                        if (IsTargetIgnored(self, p, d))
                            continue;   // gave up reaching them recently
                        if (ReferenceEquals(p, m.HuntTarget)) d *= 0.7;   // keep the same quarry
                        if (d < bestD) { bestD = d; best = p; }
                    }
                    if (best == null)
                    {
                        foreach (Vehicle v in arena.getVehiclesInRange(self._state.positionX, self._state.positionY, HuntRadarRaw / 2,
                            delegate (Vehicle v) { return v is Bot && v != self && !v.IsDead; }))
                        {
                            if (!IsHostileTarget(self, script, v))
                                continue;
                            double d = Helpers.distanceTo(self._state, v._state);
                            if (IsTargetIgnored(self, v, d))
                                continue;
                            if (d < bestD) { bestD = d; best = v; }
                        }
                    }
                    m.HuntTarget = best;
                }

                Helpers.ObjectState goal = StateOf(m.HuntTarget);
                bool hunting = goal != null;
                if (!hunting)
                {
                    bool needPick = m.RoamDest == null || now - m.RoamPickTick > 25000
                        || Helpers.distanceTo(self._state, m.RoamDest) < 200;
                    if (needPick)
                    {
                        m.RoamDest = PickRoamPoint(self, m, home);
                        m.RoamPickTick = now;
                        self.Navigation?.Clear();
                    }
                    goal = m.RoamDest;
                }
                if (goal == null)
                    return;

                // Can't actually get to the quarry (e.g. it's on the other side of a red wall and no route comes):
                // drop it for a while and roam instead of grinding toward it.
                if (hunting && !MakingProgress(self, m, goal, now, 4000))
                {
                    m.IgnoreUntil[m.HuntTarget] = now + 12000;
                    m.HuntTarget = null;
                    ResetProgress(m);
                    self.Navigation?.Clear();
                    m.RoamDest = PickRoamPoint(self, m, home);
                    m.RoamPickTick = now;
                    goal = m.RoamDest;
                    hunting = false;
                    if (goal == null)
                        return;
                    if (LogPersonas)
                        Log.write(TLog.Normal, "[bot brain] {0} can't reach its quarry - roaming instead", self._id);
                }

                // stuck watchdog: no real progress in 3 s -> new roam point / fresh route
                if (m.IdleTick == 0 || now - m.IdleTick >= 3000)
                {
                    if (m.IdleTick != 0)
                    {
                        int dx = self._state.positionX - m.IdleX, dy = self._state.positionY - m.IdleY;
                        if (dx * dx + dy * dy < 80 * 80)
                        {
                            if (!hunting) { m.RoamDest = PickRoamPoint(self, m, home); goal = m.RoamDest ?? goal; m.RoamPickTick = now; }
                            self.Navigation?.Clear();
                        }
                    }
                    m.IdleTick = now; m.IdleX = self._state.positionX; m.IdleY = self._state.positionY;
                }

                Helpers.ObjectState dest = new Helpers.ObjectState { positionX = goal.positionX, positionY = goal.positionY };
                int gx = dest.positionX - m.HuntGoalX, gy = dest.positionY - m.HuntGoalY;
                bool force = gx * gx + gy * gy > 400 * 400;
                if (force) { m.HuntGoalX = dest.positionX; m.HuntGoalY = dest.positionY; }
                self.ApplyNavigationApproach(dest, EolHqPatrol.MakeLineClearPendingSeek(self, () => dest), new BotNavigateOptions
                {
                    RequeueMinMs = hunting ? 1500 : 2500,
                    PathStartIndex = 0,
                    ForceRequeue = force,
                    Priority = hunting ? PathPriority.Combat : PathPriority.Idle,
                });
            }
            catch (Exception ex)
            {
                Log.write(TLog.Warning, "[bot brain] Hunt: " + ex.Message);
            }
        }

        const int HuntRadarRaw = 5000;

        /// <summary>An open spot 900-2600 px away (around home if given), clear of walls on every side.</summary>
        static Helpers.ObjectState PickRoamPoint(Bot self, Mind m, Helpers.ObjectState home)
        {
            short cx = home != null ? home.positionX : self._state.positionX;
            short cy = home != null ? home.positionY : self._state.positionY;
            for (int i = 0; i < 60; i++)
            {
                double a = m.R.NextDouble() * Math.PI * 2;
                int rad = 900 + m.R.Next(1700);
                int x = cx + (int)(Math.Cos(a) * rad), y = cy + (int)(Math.Sin(a) * rad);
                if (x < 64 || y < 64 || x > short.MaxValue - 64 || y > short.MaxValue - 64)
                    continue;
                short sx = (short)x, sy = (short)y;
                if (!EolHqPatrol.IsOpenForBot(self, sx, sy))
                    continue;
                if (Helpers.distanceTo(self._state.positionX, self._state.positionY, sx, sy) < 600)
                    continue;
                return new Helpers.ObjectState { positionX = sx, positionY = sy };
            }
            return null;
        }

        /// <summary>
        /// Get out of the fight. Good players backpedal - facing the threat and shooting while they open the
        /// distance - while it's close and in sight; weaker ones (and anyone with a wall behind them) turn and run.
        /// </summary>
        static void Retreat(Bot self, Mind m, Helpers.ObjectState threat, object threatObj, int now)
        {
            SteeringController steering = self._movement as SteeringController;
            double tdist = Helpers.distanceTo(self._state, threat);
            if (m.P.Backpedals && steering != null && tdist < 700
                && EolBotTargetUtil.TileLineClearForWeaponFire(self._arena, self._state.positionX, self._state.positionY,
                    threat.positionX, threat.positionY))
            {
                // room behind? (body faces the threat, so "behind" = away from it)
                int awayYaw = Helpers.computeLeadFireAngle(threat, self._state, 10);
                int room = BotWallAvoidance.ProbeOpen(self, self._state.positionX, self._state.positionY, awayYaw, 96);
                if (room >= 64)
                {
                    if (m.NextFlip == 0 || now >= m.NextFlip)
                    {
                        if (m.NextFlip != 0) m.StrafeSign = -m.StrafeSign;
                        m.NextFlip = now + m.R.Next(m.P.FlipMinMs, m.P.FlipMaxMs);
                    }
                    DriveAimFire(self, steering, threat, threatObj, tdist, -1f, 0.35f * m.StrafeSign);
                    return;
                }
            }

            double dx = self._state.positionX - threat.positionX;
            double dy = self._state.positionY - threat.positionY;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) { dx = 1; dy = 0; len = 1; }
            dx /= len; dy /= len;

            // pick an away direction with open ground (straight away, then ±35°, ±70°)
            double[] tries = { 0, 0.61, -0.61, 1.22, -1.22 };
            short fx = self._state.positionX, fy = self._state.positionY;
            foreach (double a in tries)
            {
                double cx = dx * Math.Cos(a) - dy * Math.Sin(a), cy = dx * Math.Sin(a) + dy * Math.Cos(a);
                short tx = (short)Math.Max(16, Math.Min(short.MaxValue - 16, self._state.positionX + cx * 600));
                short ty = (short)Math.Max(16, Math.Min(short.MaxValue - 16, self._state.positionY + cy * 600));
                if (EolBotTargetUtil.TileLineClearForWeaponFire(self._arena, self._state.positionX, self._state.positionY, tx, ty))
                { fx = tx; fy = ty; break; }
            }
            Helpers.ObjectState flee = new Helpers.ObjectState { positionX = fx, positionY = fy };
            self.ApplyNavigationApproach(flee, v => v.SteerForSeek(flee.position()), new BotNavigateOptions
            {
                RequeueMinMs = 1200,
                PathStartIndex = 0,
                Priority = PathPriority.Combat,
            });
        }

        /// <summary>
        /// Called every few hundred ms from the gametype: retreating bots that are safe heal gradually; bots that
        /// healed enough stop retreating (they rejoin the next time they see a fight).
        /// </summary>
        static bool _loggedPhysics;
        static bool _loggedWeapons;

        /// <summary>One-time startup line per bot vehicle: weapons, blast radius, minimum fire range (for tuning).</summary>
        static void LogBotWeapons(Arena arena)
        {
            _loggedWeapons = true;
            try
            {
                int[] ids = { 140, 141, 143, 156, 158, 437, 142, 144, 145, 146, 147, 157 };
                foreach (int id in ids)
                {
                    VehInfo.Car car = arena._server._assets.getVehicleByID(id) as VehInfo.Car;
                    if (car?.InventoryItems == null)
                        continue;
                    var sb = new System.Text.StringBuilder();
                    for (int s = 0; s < 2 && s < car.InventoryItems.Length; s++)
                    {
                        ItemInfo it = car.InventoryItems[s] != 0 ? arena._server._assets.getItemByID(car.InventoryItems[s]) : null;
                        if (it == null) continue;
                        ItemInfo.Projectile pr = BotWeaponUtil.ResolveProjectile(it);
                        sb.AppendFormat(" | slot{0} {1}: range {2}, blast {3}, min fire {4}", s, it.name,
                            BotWeaponUtil.GetCombatRangeRaw(it), pr != null ? BotWeaponUtil.MaxDamageRadiusRaw(pr) : 0,
                            BotWeaponUtil.GetMinFireRangeRaw(it));
                    }
                    Log.write(TLog.Normal, "[bot brain] veh {0} {1} (fire height {2}){3}", id, car.Name, car.FireHeight, sb);
                }
            }
            catch (Exception ex) { Log.write(TLog.Warning, "[bot brain] weapon log: " + ex.Message); }
        }

        public static void PollAll(int now, Arena arena)
        {
            // (re)attach the bot-vs-bot hit resolver - replaced, not added, so a script reload can't double it
            BotCombat.BotShotFired = OnBotShotFired;
            InfServer.Game.Modules.BotActions.BotActionHelpers.IgnoreTarget = IsTargetIgnored;
            if (!_loggedWeapons && arena?._server?._assets != null)
                LogBotWeapons(arena);
            EolBotPerception.InstallEngineHook();
            if (arena != null)
            {
                foreach (Bot ab in arena.ActiveBots)
                    if (ab != null && !ab.IsDead)
                    {
                        EolBotPerception.Register(ab);
                        if (ab._type != null && ab._type.FireHeight > BotCombat.DefaultFireHeight)
                            BotCombat.DefaultFireHeight = ab._type.FireHeight;
                    }
                if (!_loggedPhysics && BotCombat.DefaultFireHeight > 0)
                {
                    _loggedPhysics = true;
                    try
                    {
                        var lvl = arena._server._assets.Level;
                        var sb = new System.Text.StringBuilder();
                        for (int i = 0; i < lvl.PhysicsHigh.Length && i < 16; i++)
                            sb.AppendFormat(" {0}:{1}-{2}", i, lvl.PhysicsLow[i], lvl.PhysicsHigh[i]);
                        Log.write(TLog.Normal, "[bot brain] shots fly over physics lower than fire height {0}; physics low-high:{1}",
                            BotCombat.DefaultFireHeight, sb);
                    }
                    catch { }
                }
            }
            if (!Enabled)
                return;
            List<WeakReference<Bot>> list;
            lock (Known) { Known.RemoveAll(w => { Bot x; return !w.TryGetTarget(out x) || x.IsDead || x.bCondemned; }); list = new List<WeakReference<Bot>>(Known); }
            foreach (WeakReference<Bot> w in list)
            {
                Bot b; Mind m;
                if (!w.TryGetTarget(out b) || !Minds.TryGetValue(b, out m) || !m.Retreating || b._state == null)
                    continue;
                Helpers.ObjectState t = StateOf(m.Threat);
                bool safe = t == null || (m.Threat is Player tp && (tp.IsDead || tp.IsSpectator)) || (m.Threat is Vehicle tv && tv.IsDead)
                    || !Helpers.isInRange(SafeDistanceRaw, b._state, t);
                if (!safe) { m.SafeSince = 0; continue; }
                if (m.SafeSince == 0) { m.SafeSince = now; m.LastHealTick = now; continue; }
                if (now - m.SafeSince < SafeForMs) { m.LastHealTick = now; continue; }
                int dt = now - m.LastHealTick;
                if (dt < 250) continue;
                m.LastHealTick = now;
                int max = MaxHp(b);
                int add = (int)Math.Ceiling(max * HealPerSecond * dt / 1000.0);
                b._state.health = (short)Math.Min(max, b._state.health + add);
                if (HpFrac(b) >= m.P.RejoinHp)
                {
                    m.Retreating = false;
                    if (LogPersonas)
                        Log.write(TLog.Normal, "[bot brain] {0} healed up ({1:P0}) - ready to fight again", b._id, HpFrac(b));
                }
            }
        }
    }
}
