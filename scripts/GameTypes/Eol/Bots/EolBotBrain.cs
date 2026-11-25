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
        /// <summary>0..1: how far it hears fights and how readily it goes to look (aliens and elites: high).</summary>
        public double Curiosity;
        /// <summary>How long it keeps trying to get round cover to a target it lost sight of before giving up.</summary>
        public int PatienceMs;

        public static EolBotPersona Roll(Random r, bool alien)
        {
            EolBotPersona p = new EolBotPersona();
            // aliens: pack animals, mostly fearless; humans: more mixed
            // (2026-11-18, owner: "remove the flight or fight ... they should always engage if they see someone") every bot
            // is a fighter - nobody pulls back when hurt or outnumbered (was: aliens + 35 %)
            p.Fighter = true;
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
            p.Hunter = alien;                                    // aliens have no base: they all range out hunting
            if (alien)
            {   // wild animal: never still, darting side to side on a jittery rhythm
                p.StrafePower = (float)(0.85 + r.NextDouble() * 0.15);
                p.FlipMinMs = 350 + r.Next(350);
                p.FlipMaxMs = p.FlipMinMs + 500 + r.Next(700);
                p.ChaseEagerness = 0.8 + r.NextDouble() * 0.2;
            }
            p.PatienceMs = 1800 + r.Next(2200);
            p.Curiosity = alien ? 0.7 + r.NextDouble() * 0.3 : 0.3 + r.NextDouble() * 0.7;
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
            public int SeenEscapeTick;
            public short IdleX, IdleY;
            public int IdleTick;
            public int OddsTick;
            public object Turret;
            public int TurretPickTick;
            // (2026-11-15) turret fights: is it losing health while we strafe it; backing off after giving up
            public int TurretStrafeSince, TurretWatchHp;
            public object TurretWatched;
            public short LeaveX, LeaveY;
            public int LeaveUntil;
            public int SearchArrivedTick;
            /// <summary>When this bot started trading shots across a wall it can't walk through (0 = not).</summary>
            public int WallStandoffTick;
            /// <summary>Pushing round the wall (through the opening) until this tick.</summary>
            public int FlankUntil;
            public int IdleStareTick;
            public short IdleStareX, IdleStareY;
            public readonly Dictionary<object, int> IgnoreUntil = new Dictionary<object, int>();
            // new player buffer turn-backs (BufferGuard)
            public int BufferPulls, LastBufferPull, BufferRetreatUntil;
            // progress tracking (MakingProgress)
            public int ProgTick;
            public double ProgBest;
            public short ProgGoalX, ProgGoalY, ProgX, ProgY;
            // curiosity
            public short InvestX, InvestY;
            public int InvestUntil, InvestPickTick;
            public short InvestFailX, InvestFailY;
            public int InvestFailUntil;
            // areas searched recently (packed x<<16|y), for logical sweeping
            public readonly Queue<long> Searched = new Queue<long>();
            // extra weapons (human-class bots)
            public WeaponController Opener, Grenade;
            public object OpenerTarget;
            public int OpenerShotsLeft, GrenadesLeft, NextGrenade;
            // (2026-11-17) the third gun (melee / laser / sonic / shotgun) and its burst window
            public WeaponController Misc;
            public object DrainTarget; public int DrainUntil;   // (2026-11-18) the sidearm's energy-drain opener
            public int MixNext, MixUntil;                       // (2026-11-18) a misc-gun burst now and then
            public WeaponController Melee; public bool EnergySaving;   // (2026-11-18) low energy: free guns / melee
            public bool PreferMelee;   // (2026-11-26) the Pirate Commander swings his Chain Sword whenever someone is in reach
            public int MiscFrom, MiscUntil;
            // (2026-11-17) stand-still guns: charge-up end, end of the planted burst, earliest next commit
            public int PlantFireFrom, PlantUntil, NextPlant;
            public WeaponController PlantGun;
        }

        /// <summary>The bot's misc gun (third slot): used while the main reloads and in short bursts now and then.</summary>
        /// <summary>(2026-11-18) the melee weapon a bot falls back on while its energy recharges.</summary>
        public static void SetMeleeWeapon(Bot b, WeaponController melee)
        {
            if (b != null) Get(b).Melee = melee;
        }

        /// <summary>(2026-11-26) a melee misc gun is used whenever the target is in reach (not only as a last resort).</summary>
        public static void SetPreferMelee(Bot b, bool on)
        {
            if (b != null) Get(b).PreferMelee = on;
        }

        public static void SetMiscWeapon(Bot b, WeaponController misc)
        {
            if (b == null)
                return;
            Mind m = Get(b);
            m.Misc = misc;
            m.MiscFrom = Environment.TickCount + 6000 + m.R.Next(6000);
            m.MiscUntil = 0;
        }

        /// <summary>Can <paramref name="w"/> hit something <paramref name="dist"/> away (inside its reach, past its minimum)?</summary>
        const int MeleeReachRaw = 56;
        /// <summary>The zone's melee items (knife, axe, swords, fists, stunner) - their rounds have a small speed, so the
        /// engine's "no muzzle velocity" test missed them and bots ran at players swinging from far away.</summary>
        static readonly HashSet<int> MeleeItemIds = new HashSet<int> { 1123, 1013, 1080, 1075, 1076, 1174, 1173, 1065, 1047 };

        static bool IsMeleeGun(WeaponController w)
        {
            if (w == null) return false;
            if (MeleeItemIds.Contains(w.ItemID) || BotCombat.IsMeleeWeapon(w)) return true;
            ItemInfo.Projectile p = w.Projectile;
            return p != null && p.muzzleVelocity > 0 && p.aliveTime > 0 && (long)p.muzzleVelocity * p.aliveTime / 10000L < 96;
        }

        static bool GunReaches(WeaponController w, double dist)
        {
            if (w == null || w.ItemID == 0)
                return false;
            int reach = BotWeaponUtil.GetCombatRangeRaw(AssetManager.Manager?.getItemByID(w.ItemID));
            int min = BotWeaponUtil.GetMinFireRangeRaw(w);
            if (IsMeleeGun(w))   // (2026-11-17: a melee weapon reaches arm's length only, never "anything")
                return dist <= Math.Max(MeleeReachRaw, reach > 0 ? Math.Min(reach, 96) : 0);
            return (reach <= 0 || dist <= BotCombat.MaxFireDistance(reach, false)) && (min <= 0 || dist >= min);
        }

        /// <summary>
        /// (2026-11-17, owner: "they should rotate around in the fight with the main being the hardest hitting") the main
        /// gun (far slot) whenever it reaches and isn't reloading; the misc gun in a 2-3 s burst every 8-14 s and while the
        /// main reloads; the sidearm (close slot) when neither can fire. null = keep the engine's pick.
        /// </summary>
        /// <summary>A gun whose round does energy damage (plasma pistols / SMGs): it drains the target's energy.</summary>
        static bool IsDrainGun(WeaponController w)
        {
            ItemInfo.Projectile p = w?.Projectile;
            return p != null && p.energyDamageInner > 0;
        }

        const int SideDrainMs = 1200;          // the sidearm's opening burst on a new target
        // (2026-11-18, owner: "the bots were caught in some weird freeze and stutter and they werent shooting") holding fire
        // through 1.5 s main reloads froze them - the sidearm covers any reload of 0.6 s or more again
        const int SideOnReloadMinMs = 600;

        static WeaponController RotateGuns(Bot self, Mind m, double dist, int now, object target = null)
        {
            WeaponController main = self._weaponFar, side = self._weaponClose, misc = m.Misc;
            if (main == null && misc == null)
                return null;
            if (self.IsPlanted(now))
            {   // (planted for a stand-still gun: keep blasting with it)
                foreach (WeaponController w in new[] { main, side, misc })
                    if (w != null && w.ItemID == self.PlantedItemId) return w;
            }
            // (2026-11-18, owner: "if cloak classes are using up all their energy, they should move to a melee to let their
            // energy regain, and then back again to the guns ... not spamming something with low energy and only firing low
            // rate") below 20 % energy a bot saves it until it is back to 60 %: a gun that costs no energy, else its melee
            // weapon (it closes in with it), never the energy guns at a trickle
            if (self.PlayerEnergy)
            {
                double ef = self.EnergyFraction;
                if (ef < 0.2) m.EnergySaving = true;
                else if (ef >= 0.6) m.EnergySaving = false;
                if (m.EnergySaving)
                {
                    foreach (WeaponController w in new[] { main, side, misc })
                        if (w != null && Bot.WeaponEnergyCost(w) == 0 && GunReaches(w, dist) && !w.IsReloading)
                            return w;
                    // (2026-11-18, owner: "an infil tried to melee me from far away ... any class ... should not melee from far
                    // away") the melee weapon only within striking distance; farther off it keeps its guns
                    if (m.Melee != null && dist <= MeleeReachRaw + 24)
                        return m.Melee;
                }
            }
            bool miscOk = misc != null && GunReaches(misc, dist) && !misc.IsReloading;
            // (2026-11-17, owner: "melee ... only if they are close and they cannot fire their other weapons") a melee misc
            // is the last resort: in reach (close) and only when neither the main nor the sidearm can fire
            if (misc != null && IsMeleeGun(misc))
            {
                if (m.PreferMelee && miscOk)
                    return misc;   // (2026-11-26: in reach - GunReaches keeps a melee to arm's length)
                bool mainCan = main != null && GunReaches(main, dist) && !main.IsReloading;
                bool sideCan = side != null && !ReferenceEquals(side, misc) && GunReaches(side, dist) && !side.IsReloading;
                if (mainCan) return main;
                if (sideCan) return side;
                return miscOk ? misc : null;
            }
            // (2026-11-18, owner: "they should only be using a pistol type weapon for energy drain, and then they swap to a
            // harder hitting weapon" / "the pistol for energy drain can either be a plasma type or a pistol ammo type weapon
            // ... then go into their main weapon and sometimes the misc weapon to mix it up a bit") the sidearm opens on each
            // new target for SideDrainMs, then the main gun - with a 1.5-2.5 s misc burst every 8-14 s; on a main reload the
            // misc gun, the sidearm only if the reload is long; the sidearm also when the main can't reach (close quarters)
            bool sideOk = side != null && !ReferenceEquals(side, main) && GunReaches(side, dist) && !side.IsReloading;
            bool mainReach = main != null && GunReaches(main, dist);
            if (target != null && !ReferenceEquals(target, m.DrainTarget))
            {
                m.DrainTarget = target;
                m.DrainUntil = sideOk ? now + SideDrainMs : 0;
                m.MixNext = now + SideDrainMs + 4000 + m.R.Next(4000);   // (first misc burst a little into the fight)
            }
            if (sideOk && m.DrainUntil != 0 && now - m.DrainUntil < 0)
                return side;
            if (miscOk && m.MixUntil != 0 && now - m.MixUntil < 0)
                return misc;
            if (miscOk && m.MixNext != 0 && now - m.MixNext >= 0)
            {
                m.MixUntil = now + 1500 + m.R.Next(1000);
                m.MixNext = now + 8000 + m.R.Next(6000);
                return misc;
            }
            if (mainReach && !main.IsReloading)
                return main;
            if (mainReach && main.IsReloading)
            {
                if (miscOk) return misc;
                if (sideOk && main.ReloadMsLeft >= SideOnReloadMinMs) return side;
                return main;   // (a short reload: wait for the harder hitter)
            }
            if (sideOk)
                return side;
            if (miscOk)
                return misc;
            return main;
        }

        /// <summary>
        /// Give a bot an opener gun (fired for one clip at each new human target - e.g. Kamenev AP mk2 to strip the
        /// shield) and/or a few grenades thrown at mid range on a cooldown.
        /// </summary>
        public static void SetExtraWeapons(Bot b, WeaponController opener, WeaponController grenade, int grenades)
        {
            if (b == null)
                return;
            Mind m = Get(b);
            m.Opener = opener;
            m.Grenade = grenade;
            m.GrenadesLeft = grenade != null ? grenades : 0;
            m.OpenerTarget = null;
        }

        static int ClipOf(WeaponController w)
        {
            ItemInfo.Projectile p = w?.Projectile;
            return p != null && p.ammoCapacity > 0 ? p.ammoCapacity : 10;
        }

        const int IgnoreAfterGiveUpMs = 8000;
        const int IgnoreUnlessWithinRaw = 500;

        /// <summary>Engine hook: skip a target this bot recently gave up on, unless it has come close.</summary>
        static bool IsTargetIgnored(Bot self, object target, double dist)
        {
            if (self == null || target == null)
                return false;
            // never a target in (or at the edge of) the new player buffer
            if (BufferEdge != null)
            {
                Helpers.ObjectState ts = StateOf(target);
                if (ts != null && BufferEdge(ts.positionX, ts.positionY) < BufferTargetMarginRaw)
                    return true;
            }
            Mind m;
            if (!Minds.TryGetValue(self, out m))
                return false;
            // turning back from the buffer: nothing but point-blank threats until it is clear
            if (m.BufferRetreatUntil != 0 && Environment.TickCount - m.BufferRetreatUntil < 0 && dist > 250)
                return true;
            if (m.IgnoreUntil.Count == 0)
                return false;
            int until;
            if (!m.IgnoreUntil.TryGetValue(target, out until))
                return false;
            if (Environment.TickCount - until >= 0)
            {
                m.IgnoreUntil.Remove(target);
                return false;
            }
            // (2026-11-15) a given-up target stays ignored at any distance - unless it is close AND the bot can actually
            // shoot it from where it stands. Turrets always stay ignored. (The plain "unless within 500" picked the same
            // unreachable target / unbeatable turret straight back: the 4-7 s approach-fail-strafe loop repeated forever.)
            if (target is Computer)
                return true;
            if (dist > IgnoreUnlessWithinRaw)
                return true;
            Helpers.ObjectState tst = StateOf(target);
            return tst == null || !BotCombat.CanDirectEngage(self, tst);
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
                bool assassin = b.SkillModeOverride == "ultra" && !(b._type != null && Script_Eol.IsHarrierCrewVeh(b._type.Id));   // (2026-11-26: the Harriers aim like assassins, fight like elites)
                m.P.Fighter = true;   // (2026-11-18, owner: no flight or fight - always engage)
                m.P.TeamPlayer = true;
                m.P.Retaliates = !assassin;          // assassins stay on the contract
                m.P.Backpedals = !alienElite;
                if (alienElite)
                    m.P.Hunter = true;
                m.P.ReactMs = 140 + m.R.Next(160);
                m.P.StrafePower = (float)(0.85 + m.R.NextDouble() * 0.15);
                m.P.PatienceMs = 3500 + m.R.Next(2500);
                m.P.HelpRange = 1400;
                m.P.Curiosity = Math.Max(m.P.Curiosity, 0.85);
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

                // new player buffer: at its edge (or with a target in it) - let go and roam away (BufferGuard)
                if (BufferGuard(self, m, now)
                    || (playerTarget != null && BufferEdge != null && BufferEdge(playerTarget._state.positionX, playerTarget._state.positionY) < BufferTargetMarginRaw))
                {
                    playerTarget = null;
                    return;
                }

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
                        // only an attacker it can SEE itself (no shared wall-hack knowledge)
                        if (ally.LastHurtBy is Player ap ? !EolBotPerception.SeesNow(self._state, ap)
                            : !(ally.LastHurtBy is Vehicle av && BotCombat.CanDirectEngage(self, av._state)))
                            continue;
                        Helpers.ObjectState s = StateOf(ally.LastHurtBy);
                        if (s == null || !Helpers.isInRange(Script_Eol.EolBotCombatSeekRangeRaw, self._state, s))
                            continue;
                        best = ally.LastHurtBy; bestTick = ally.LastHurtTick;
                    }
                    if (best != null)
                    {
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
            // The caller's pursuit steer only chases PLAYER targets (it returns zero for a bot target), so two bots
            // that spotted each other far apart just stood there in limbo. If that steer gives nothing, seek the
            // target ourselves (wall-aware, EolWallMemory) - bots run at each other to fight (local fix 2026-11-14).
            if (dest != null && self != null)
            {
                Func<InfantryVehicle, Vector3> given = fallback;
                Func<InfantryVehicle, Vector3> seek = EolHqPatrol.MakeLineClearPendingSeek(self, () => dest);
                fallback = v =>
                {
                    Vector3 r = Vector3.Zero;
                    try { if (given != null) r = given(v); } catch { }
                    if (target is Bot || r.x * r.x + r.y * r.y < 1e-6)
                        return seek(v);
                    return r;
                };
            }
            if (!Enabled || self == null || dest == null || self._arena == null || self._state == null)
                return EolBotCombatUtil.ChaseTargetWithoutLos(self, now, dest, fallback);
            try
            {
                Mind m = Get(self);
                EolBotPersona P = m.P;
                // just had to back out of a corner / wall: a target it can't shoot from here is what walled it
                // in - drop it and go somewhere else instead of walking straight back into the corner
                if (self.LastWallEscapeTick != 0 && self.LastWallEscapeTick != m.SeenEscapeTick)
                {
                    m.SeenEscapeTick = self.LastWallEscapeTick;
                    m.RoamDest = null;
                    if (target != null && !BotCombat.CanDirectEngage(self, dest))
                    {
                        GiveUp(self, m, target);
                        return false;
                    }
                }
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
                // melee only (board slash, bites - reach under 140): it has to get in close to land a hit
                bool meleeOnly = range < 140
                    && (self._weaponFarItem == null || EolBotCombatUtil.GetCombatRangeRaw(self._weaponFarItem) < 140);
                range = meleeOnly ? Math.Max(40, range) : Math.Max(140, Math.Min(700, range));
                double pref = meleeOnly ? Math.Max(18, range * 0.5) : Math.Max(150, range * P.RangeFrac);   // never run into the target (guns)
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
                    else if (target is Player && !EolBotPerception.SeesNow(self._state, (Player)target))
                    {   // never seen where they are: nothing to go on - no tracking through walls
                        GiveUp(self, m, target);
                        return false;
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

                // can shoot over it (low physics) but can't walk through it?
                bool walkClear = EolHqPatrol.TileLineClear(self._arena, self._state.positionX, self._state.positionY,
                    dest.positionX, dest.positionY, self._type);

                // EVERY approach (far chase, straight chase, route round) must actually be closing the gap; a bot
                // that isn't getting closer / travelling a route within its patience drops the target and goes
                // back to its own business - no grinding on walls, no standing off out of range.
                if (dist > range * 1.05 && !MakingProgress(self, m, dest, now, P.PatienceMs + 1000))
                {
                    GiveUp(self, m, target);
                    return false;
                }

                // far away (but visible): path-chase, shooting when the gun reaches. When the straight line is
                // blocked, the stop-gap steering follows open ground (never seeks straight into the wall).
                if (dist > range * 1.8)
                {
                    Func<InfantryVehicle, Vector3> far = walkClear ? fallback : EolHqPatrol.MakeLineClearPendingSeek(self, () => dest);
                    bool chasing = EolBotCombatUtil.ChaseTargetWithoutLos(self, now, dest, far);
                    AimFire(self, dest, target, dist, Helpers.computeLeadFireAngle(self._state, dest, 10));
                    return chasing;
                }
                // (local 2026-11-14) a standoff across a low wall (aliens outside a base vs bots inside, both
                // strafing up and down in one spot): after a few seconds, push round through the opening like a
                // player would - still shooting on the way - instead of trading shots there forever
                if (walkClear)
                {
                    m.WallStandoffTick = 0;
                    m.FlankUntil = 0;
                }
                else if (m.FlankUntil == 0 || now - m.FlankUntil >= 0)
                {
                    if (m.FlankUntil != 0)
                    {   // a push just ended: start counting again
                        m.FlankUntil = 0;
                        m.WallStandoffTick = now;
                    }
                    else if (m.WallStandoffTick == 0)
                        m.WallStandoffTick = now;
                    else if (now - m.WallStandoffTick > 3000 + m.R.Next(3000))
                    {
                        m.FlankUntil = now + 7000;
                        m.WallStandoffTick = 0;
                        ResetProgress(m);
                    }
                }
                bool flanking = !walkClear && m.FlankUntil != 0 && now - m.FlankUntil < 0;

                if (!walkClear && (dist > pref * 1.15 || flanking))
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
                // (2026-11-18, owner: "if i move away half a coordinate from a team, they just look at me and strafe in spot
                // ... they should be following me to engage") the gun in hand out of reach = close in like a chase, and
                // past the preferred range push in hard (it used to drift at half power while strafing at full)
                int handReach = EolBotCombatUtil.GetCombatRangeRaw(self._weapon != null && self._weapon.ItemID != 0
                    ? AssetManager.Manager?.getItemByID(self._weapon.ItemID) : null);
                bool handShort = handReach > 0 && dist > handReach * 0.9;
                if (dist > range * 1.05 || handShort || (targetRunning && dist > pref * 1.2))
                {   // chase: straight at them, a little weave
                    forward = 1f;
                    strafe = 0.18f * m.StrafeSign;
                }
                else
                {   // fight: drift around them at the preferred range
                    bool tooFar = dist > pref * 1.15;
                    if (tooFar) forward = 0.85f;
                    else if (dist < pref * 0.70) forward = needMin > 0 && dist < needMin ? -1f : -0.55f;
                    else forward = (float)Math.Max(-0.3, Math.Min(0.3, (dist - pref) / pref * 0.8));
                    strafe = P.StrafePower * m.StrafeSign * (tooFar ? 0.5f : 1f);
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
            else
            {   // (2026-11-17) sidearm / main / misc rotation (the maser through a wall still comes first)
                WeaponController rot = RotateGuns(self, Get(self), dist, Environment.TickCount, target);
                if (rot != null && !ReferenceEquals(rot, gun))
                    gun = BotWeaponUtil.CommitEngageWeapon(self, rot) ?? rot;
            }
            if (gun != null)
                self._weapon = gun;

            // Keep the aim on the drive request (SetDrive replaced it), so the body tracks the gun's aim point
            // and the dodge layer knows we are fighting (it no longer makes us stand still).
            int gunAim = gun != null && gun.Projectile != null ? gun.getAimAngle(dest) : aimYaw;
            self.SetAim(gunAim, true);

            Mind xm = Get(self);
            int xnow = Environment.TickCount;

            // grenade: mid range, clear (low-wall) line, on its own cooldown, a few per life
            if (xm.Grenade != null && xm.GrenadesLeft > 0 && xnow - xm.NextGrenade >= 0 && xm.Grenade.ableToFire())
            {
                int gReach = BotWeaponUtil.GetCombatRangeRaw(xm.Grenade.ItemID != 0 ? AssetManager.Manager?.getItemByID(xm.Grenade.ItemID) : null);
                int gMin = BotWeaponUtil.GetMinFireRangeRaw(xm.Grenade);
                if (gReach > 0 && dist <= gReach && dist >= Math.Max(100, gMin)
                    && BotCombat.TryAimAndFire(self, xm.Grenade, dest, requireLineOfSight: true, freezeMs: 0))
                {
                    xm.GrenadesLeft--;
                    xm.NextGrenade = xnow + 6000 + xm.R.Next(5000);
                    _fFired++;
                    return;
                }
            }

            // opener: one clip of it at every new human target (strip the shield), then the normal guns
            if (xm.Opener != null && target is Player)
            {
                if (!ReferenceEquals(xm.OpenerTarget, target))
                {
                    xm.OpenerTarget = target;
                    xm.OpenerShotsLeft = ClipOf(xm.Opener);
                }
                if (xm.OpenerShotsLeft > 0)
                {
                    int oReach = BotWeaponUtil.GetCombatRangeRaw(xm.Opener.ItemID != 0 ? AssetManager.Manager?.getItemByID(xm.Opener.ItemID) : null);
                    // only when it can actually fire it from here - otherwise the normal guns keep shooting
                    // (local fix 2026-11-14: it held fire waiting on an opener it was too far to use)
                    if ((oReach <= 0 || dist <= BotCombat.MaxFireDistance(oReach, BotCombat.IsMeleeWeapon(xm.Opener)))
                        && self.CanFireWeaponNow(xm.Opener))
                    {
                        if (xm.Opener.ableToFire()
                            && BotCombat.TryAimAndFire(self, xm.Opener, dest, requireLineOfSight: true, freezeMs: 0))
                        {
                            xm.OpenerShotsLeft--;
                            _fFired++;
                            return;
                        }
                        if (!xm.Opener.ableToFire() && !xm.Opener.IsReloading)
                            return;   // between opener shots
                    }
                }
            }

            if (gun == null)
            {
                _fNoGun++;
                return;
            }
            // Stay on the gun it is already shooting while that one is ready and in reach - swapping costs the
            // weapon's switch delay (fireDelayOther, 1-3 s on most guns). Swap only when the current one is
            // reloading / out of reach (fire on reload, like a player).
            WeaponController last = self.LastFiredWeapon;
            if (last != null && !ReferenceEquals(gun, last) && !self.CanFireWeaponNow(gun)
                && (ReferenceEquals(last, self._weaponClose) || ReferenceEquals(last, self._weaponFar) || ReferenceEquals(last, xm.Misc)) && last.ableToFire())
            {
                int lReach = BotWeaponUtil.GetCombatRangeRaw(last.ItemID != 0 ? AssetManager.Manager?.getItemByID(last.ItemID) : null);
                int lMin = BotWeaponUtil.GetMinFireRangeRaw(last);
                if ((lReach <= 0 || dist <= BotCombat.MaxFireDistance(lReach, BotCombat.IsMeleeWeapon(last))) && (lMin <= 0 || dist >= lMin))
                {
                    gun = last;
                    self._weapon = gun;
                }
            }
            // out of the gun's reach: aim, don't waste shots
            int reach = BotWeaponUtil.GetCombatRangeRaw(gun.ItemID != 0 ? AssetManager.Manager?.getItemByID(gun.ItemID) : null);
            if (reach > 0 && dist > BotCombat.MaxFireDistance(reach, BotCombat.IsMeleeWeapon(gun)))
            {
                _fOutOfReach++;
                return;
            }
            // (2026-11-17, owner: "weapons that require stand still the bots respect it and shoot. i.e cannons. so they have
            // to stand still and start blasting and then if they want to dodge they can dodge ... they have to commit to
            // blasting to get damage off") a gun whose charge-up is cancelled by moving: plant, wait out the charge-up, blast
            // for 1.5-2.5 s without moving, then 1-2.5 s free to dodge (a non-planting gun may shoot meanwhile), repeat
            // (the engine plants / charges / bursts it on every fire path - Bot.StationaryFireGate); between bursts a gun
            // that doesn't need planting may shoot instead, never the stand-still one on the move
            if (gun.RequiresStationaryFire && self.InStationaryCooldown(Environment.TickCount))
            {
                WeaponController alt = null;
                foreach (WeaponController w in new[] { self._weaponClose, self._weaponFar, xm.Misc })
                    if (w != null && !ReferenceEquals(w, gun) && !w.RequiresStationaryFire && !IsMeleeGun(w) && GunReaches(w, dist) && w.ableToFire()) { alt = w; break; }
                if (alt == null)
                    return;
                gun = alt;
                self._weapon = gun;
            }

            // Unload: fire every time the gun is ready and on target. (Hits on enemy BOTS are resolved for every
            // bot shot in OnBotShotFired below, whichever code path fired it.)
            if (!gun.ableToFire()) { _fCooldown++; return; }
            // Every bot fires on the move - strafing while it aims, never stopping to turn and shoot (local
            // 2026-11-14; the old charge-up "plant" made bots stand still and only rotate).
            if (BotCombat.TryAimAndFire(self, gun, dest, requireLineOfSight: true, freezeMs: 0))
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
            if (reach > 0 && dist > BotCombat.MaxFireDistance(reach, BotCombat.IsMeleeWeapon(gun)))
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
            // (2026-11-15) turrets too: applyExplosion ignores a direct-fire round (blast radius 0), so bots' bullets
            // never hurt a turret and squads strafed in front of one forever. applyBotHit does the listed damage and
            // destroys it at 0; _sendUpdate pushes the new health to clients.
            hitVeh.applyBotHit(gun.Projectile);
            if (hitVeh is Computer turretHit)
                turretHit._sendUpdate = true;

            int now = Environment.TickCount;
            if (LogPersonas && now - _tickHitLog > 15000)
            {
                _tickHitLog = now;
                Log.write(TLog.Normal, "[bot brain] bot-vs-bot: {0} hits from {1} shots so far (e.g. {2} hit {3}, now {4} hp)",
                    _hitsLanded, _shotsAtBots, shooter._id, victim._id, victim._state.health);
            }
        }

        // ---------------------------------------------------------------- curiosity
        struct Noise { public short X, Y; public int Tick; public Team Team; }
        static readonly List<Noise> Noises = new List<Noise>(64);
        const int NoiseLifeMs = 5000;

        /// <summary>Something loud happened here (gunfire, a death): curious bots nearby go and have a look.</summary>
        public static void NoteNoise(Team team, short x, short y)
        {
            int now = Environment.TickCount;
            lock (Noises)
            {
                // merge with a recent noise close by (a firefight is one place, not 200 events)
                for (int i = 0; i < Noises.Count; i++)
                {
                    Noise n = Noises[i];
                    int dx = n.X - x, dy = n.Y - y;
                    if (dx * dx + dy * dy < 250 * 250 && now - n.Tick < NoiseLifeMs)
                    {
                        n.Tick = now; n.X = x; n.Y = y; n.Team = team;
                        Noises[i] = n;
                        return;
                    }
                }
                if (Noises.Count >= 64)
                    Noises.RemoveAt(0);
                Noises.Add(new Noise { X = x, Y = y, Tick = now, Team = team });
            }
        }

        static void OnGunfireNoise(Team team, object owner, short x, short y)
        {
            NoteNoise(team, x, y);
        }

        /// <summary>
        /// Natural curiosity when not fighting: head for recent gunfire / deaths within earshot (how far depends
        /// on the bot) to find enemies - like a player hearing a fight. Returns true while investigating.
        /// Base guards don't call this (they hold the base).
        /// </summary>
        public static bool TryInvestigate(Bot self, Script_Eol script, int now, Helpers.ObjectState home = null, int homeRadius = 0)
        {
            if (!Enabled || self?._arena == null || self._state == null || self.IsDead)
                return false;
            Mind m = Get(self);
            if (m.Retreating)
                return false;
            int earshot = 900 + (int)(900 * m.P.Curiosity);

            // keep going to the current spot until reached / stale
            if (m.InvestUntil != 0 && now - m.InvestUntil < 0)
            {
                Helpers.ObjectState spot = new Helpers.ObjectState { positionX = m.InvestX, positionY = m.InvestY };
                bool arrived = Helpers.distanceTo(self._state, spot) < 160;
                if (arrived || !MakingProgress(self, m, spot, now, 4000))
                {
                    if (!arrived)
                    {   // (2026-11-15) couldn't get there: don't pick the same spot again straight away
                        m.InvestFailX = m.InvestX; m.InvestFailY = m.InvestY; m.InvestFailUntil = now + 12000;
                    }
                    m.InvestUntil = 0;
                    ResetProgress(m);
                    return false;
                }
                GoTo(self, spot, PathPriority.Combat);
                return true;
            }

            if (now - m.InvestPickTick < 700)
                return false;
            m.InvestPickTick = now;
            Noise best = default(Noise); double bestD = double.MaxValue; bool found = false;
            lock (Noises)
            {
                foreach (Noise n in Noises)
                {
                    if (now - n.Tick > NoiseLifeMs)
                        continue;
                    // (2026-11-15) not its own side's gunfire (its base turrets firing drew the whole base team into
                    // a wall), and not a spot it just failed to reach
                    if (n.Team != null && n.Team == self._team)
                        continue;
                    if (m.InvestFailUntil != 0 && now - m.InvestFailUntil < 0
                        && Helpers.distanceTo(n.X, n.Y, m.InvestFailX, m.InvestFailY) < 400)
                        continue;
                    if (!InArea(n.X, n.Y) || !InTerritory(self, n.X, n.Y))
                        continue;   // (not outside its territory, never into the new player buffer)
                    double d = Helpers.distanceTo(self._state.positionX, self._state.positionY, n.X, n.Y);
                    if (home != null && Helpers.distanceTo(home.positionX, home.positionY, n.X, n.Y) > homeRadius)
                        continue;   // (2026-11-17) a base-team bot: only fighting round its own base
                    if (d < 200 || d > earshot)
                        continue;
                    if (d < bestD) { bestD = d; best = n; found = true; }
                }
            }
            if (!found || m.R.NextDouble() > 0.35 + 0.6 * m.P.Curiosity)
                return false;

            m.InvestX = best.X; m.InvestY = best.Y;
            m.InvestUntil = now + 9000;
            ResetProgress(m);
            GoTo(self, new Helpers.ObjectState { positionX = best.X, positionY = best.Y }, PathPriority.Combat);
            return true;
        }

        public static void GoTo(Bot self, Helpers.ObjectState spot, PathPriority pri)
        {
            Helpers.ObjectState dest = new Helpers.ObjectState { positionX = spot.positionX, positionY = spot.positionY };
            self.ApplyNavigationApproach(dest, EolHqPatrol.MakeLineClearPendingSeek(self, () => dest), new BotNavigateOptions
            {
                RequeueMinMs = 1500,
                PathStartIndex = 0,
                Priority = pri,
            });
        }

        // ---------------------------------------------------------------- turrets
        const int TurretSightRaw = 900;
        const int TurretNoDamageGiveUpMs = 3500;   // strafing a turret that never loses health: give up
        const int TurretGiveUpMs = 30000;          // and leave it alone this long
        const int TurretLeaveRaw = 1000;           // backing off to at least this far from it

        /// <summary>
        /// (2026-11-15) Stop fighting a turret it can't beat: ignored for TurretGiveUpMs at ANY distance (the normal
        /// "ignored unless close" exception picked the same turret again at once), and the bot backs off out of its
        /// range (Mind.LeaveX/Y, used by CombatMove / roam while LeaveUntil runs).
        /// </summary>
        static void GiveUpTurret(Bot self, Mind m, Computer turret, int now)
        {
            m.IgnoreUntil[turret] = now + TurretGiveUpMs;
            m.Turret = null;
            m.TurretStrafeSince = 0;
            ResetProgress(m);
            self.Navigation?.Clear();
            // a point away from the turret, out of its reach
            double dx = self._state.positionX - turret._state.positionX, dy = self._state.positionY - turret._state.positionY;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) { dx = 1; dy = 0; len = 1; }
            int lx = turret._state.positionX + (int)(dx / len * TurretLeaveRaw), ly = turret._state.positionY + (int)(dy / len * TurretLeaveRaw);
            if (InArea((short)lx, (short)ly))
            {
                m.LeaveX = (short)lx; m.LeaveY = (short)ly; m.LeaveUntil = now + 6000;
                GoTo(self, new Helpers.ObjectState { positionX = m.LeaveX, positionY = m.LeaveY }, PathPriority.Combat);
            }
        }

        /// <summary>Still backing off from a turret it gave up on: keep heading out (true while busy).</summary>
        public static bool TryLeaveTurret(Bot self, int now)
        {
            if (!Enabled || self?._state == null || self.IsDead)
                return false;
            Mind m = Get(self);
            if (m.LeaveUntil == 0 || now - m.LeaveUntil >= 0)
                return false;
            Helpers.ObjectState spot = new Helpers.ObjectState { positionX = m.LeaveX, positionY = m.LeaveY };
            if (Helpers.distanceTo(self._state, spot) < 160)
            {
                m.LeaveUntil = 0;
                return false;
            }
            GoTo(self, spot, PathPriority.Combat);
            return true;
        }

        /// <summary>A hostile armed turret this bot can see (nearest first), or null.</summary>
        static Computer FindHostileTurret(Bot self, Script_Eol script, int range)
        {
            Computer best = null; double bestD = double.MaxValue;
            foreach (Vehicle v in self._arena.getVehiclesInRange(self._state.positionX, self._state.positionY, range,
                delegate (Vehicle x) { return x is Computer c && (c._primaryGun != null || (script != null && x._type != null && script.IsHqVehId(x._type.Id))) && !x.IsDead && x._team != self._team && !(script != null && script.RaidFlagBeforeHq(self, x)); }))   // (2026-11-19: HQs too - they have no gun now)   // (2026-11-19: a raider next to the team's roamer flag goes for the flag, not the HQ)
            {
                Computer c = (Computer)v;
                if (IsTargetIgnored(self, c, Helpers.distanceTo(self._state, c._state)))
                    continue;   // couldn't reach it recently
                if (c._team != null && script != null && script.IsEolBotTeam(c._team)
                    && !script.ShouldEolBotTeamsFight(self._team, c._team))
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
                // backing off from a turret it gave up on: keep going until out of its range
                if (TryLeaveTurret(self, now))
                    return true;
                Computer turret = m.Turret as Computer;
                if (turret == null || turret.IsDead || now - m.TurretPickTick > 1500)
                {
                    m.TurretPickTick = now;
                    turret = FindHostileTurret(self, script, TurretSightRaw);
                    m.Turret = turret;
                }
                if (turret == null)
                {
                    // still being shot by a turret it gave up on: get out of its range again (not stand in its fire)
                    Computer hurt = self.LastHurtBy as Computer;
                    if (hurt != null && !hurt.IsDead && now - self.LastHurtTick < 2000
                        && Helpers.distanceTo(self._state, hurt._state) < TurretLeaveRaw - 100)
                    {
                        GiveUpTurret(self, m, hurt, now);
                        return m.LeaveUntil != 0;
                    }
                    return false;
                }
                SteeringController steering = self._movement as SteeringController;
                if (steering == null)
                    return false;

                double dist = Helpers.distanceTo(self._state, turret._state);
                int range = EolBotCombatUtil.GetCombatRangeRaw(self._weaponCloseItem);
                int rFar = self._weaponFarItem != null ? EolBotCombatUtil.GetCombatRangeRaw(self._weaponFarItem) : 0;
                range = Math.Max(140, Math.Min(700, Math.Max(range, rFar)));
                double pref = Math.Max(150, range * 0.7);
                // (2026-11-15) the same fire line the gun itself uses (the tile test used another fire height, so a
                // bot was told "clear", strafed, and its shots were silently refused)
                bool shotClear = BotCombat.CanDirectEngage(self, turret._state);

                if (!shotClear || dist > range * 1.05)
                {   // get into a firing spot: path toward it, aiming/firing when possible - unless it can't get there
                    m.TurretStrafeSince = 0;
                    if (!MakingProgress(self, m, turret._state, now, 4000))
                    {
                        GiveUpTurret(self, m, turret, now);
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

                // (2026-11-15) strafing in front of it: if the turret takes no damage for a while (out of reach, armour,
                // a wall the shots don't clear) stop - give up on it and leave its range instead of dancing in its fire
                if (m.TurretStrafeSince == 0 || m.TurretWatched != turret)
                {
                    m.TurretStrafeSince = now; m.TurretWatched = turret; m.TurretWatchHp = turret._state.health;
                }
                else if (turret._state.health < m.TurretWatchHp)
                {
                    m.TurretStrafeSince = now; m.TurretWatchHp = turret._state.health;
                }
                else if (now - m.TurretStrafeSince > TurretNoDamageGiveUpMs)
                {
                    GiveUpTurret(self, m, turret, now);
                    return false;
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
            // Only a bot that was really chasing this target drops its route; one that merely had it picked
            // (never seen, never chased) keeps walking its patrol / roam - no stop-and-replan stutter.
            bool wasChasing = ReferenceEquals(m.Committed, target) || ReferenceEquals(m.HuntTarget, target) || m.ProgTick != 0;
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
            if (wasChasing)
            {
                try { self.Actions?.Clear(); } catch { }
                self.Navigation?.Clear();
            }
            _giveUps++;
            int now = Environment.TickCount;
            if (LogPersonas && now - _tickGiveUpLog > 30000)
            {   // one summary line every 30 s instead of a line per give-up (the log was 15k lines of this)
                _tickGiveUpLog = now;
                Log.write(TLog.Normal, "[bot brain] {0} give-ups (lost behind cover / unreachable) so far", _giveUps);
            }
        }

        static int _giveUps, _tickGiveUpLog;

        /// <summary>
        /// A bot that has stood still without firing too long: drop whatever it was locked on (and ignore it for a
        /// while) so it goes and does something else. Returns true if it had a target.
        /// </summary>
        public static bool DropStaleTarget(Bot self)
        {
            if (self == null)
                return false;
            Mind m;
            if (!Minds.TryGetValue(self, out m) || m == null)
                return false;
            object t = m.Committed ?? m.HuntTarget;
            m.RoamDest = null;
            if (t == null)
                return false;
            GiveUp(self, m, t);
            return true;
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

                // new player buffer: turning back - just head for the spot away from it (BufferGuard)
                if (BufferGuard(self, m, now))
                {
                    if (m.RoamDest == null)
                        m.RoamDest = PickAwayFromBuffer(self, m, BufferAwayBaseRaw);
                    if (m.RoamDest != null)
                    {
                        Helpers.ObjectState away = m.RoamDest;
                        self.ApplyNavigationApproach(away, EolHqPatrol.MakeLineClearPendingSeek(self, () => away), new BotNavigateOptions
                        {
                            RequeueMinMs = 2000,
                            PathStartIndex = 0,
                            Priority = PathPriority.Idle,
                        });
                    }
                    return;
                }

                // backed out of a corner: whatever it was walking to walled it in - pick a new destination
                if (self.LastWallEscapeTick != 0 && self.LastWallEscapeTick != m.SeenEscapeTick)
                {
                    m.SeenEscapeTick = self.LastWallEscapeTick;
                    if (m.HuntTarget != null)
                        m.IgnoreUntil[m.HuntTarget] = now + 12000;
                    m.HuntTarget = null;
                    m.RoamDest = null;
                    ResetProgress(m);
                }

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
                        if (!InArea(p._state.positionX, p._state.positionY))
                            continue;   // don't chase people into the toxic zone
                        if (NearBuffer(p._state.positionX, p._state.positionY, BufferTargetMarginRaw))
                            continue;   // ...or into the new player buffer
                        // no radar: only someone it has actually seen / been shot by (local 2026-11-14)
                        if (!EolBotPerception.Knows(self._state, p))
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
                            if (d > EolBotPerception.SightRaw || !BotCombat.CanDirectEngage(self, v._state))
                                continue;   // only bots it can see
                            if (IsTargetIgnored(self, v, d))
                                continue;
                            if (d < bestD) { bestD = d; best = v; }
                        }
                    }
                    m.HuntTarget = best;
                }

                Helpers.ObjectState goal = StateOf(m.HuntTarget);
                // out of sight: go where it was LAST SEEN, never to its live position
                if (m.HuntTarget is Player hp && goal != null && !EolBotPerception.SeesNow(self._state, hp))
                {
                    EolBotPerception.Sighting hs;
                    goal = EolBotPerception.TryGetLastSighting(self._state, hp, out hs)
                        ? new Helpers.ObjectState { positionX = hs.X, positionY = hs.Y }
                        : null;
                    if (goal == null)
                        m.HuntTarget = null;
                }
                // Territory leash: never chase into the new player buffer; a quarry well outside the bot's own
                // territory (NW) is let go - the bot keeps to its patch (it still fights whatever comes to it)
                if (goal != null && m.HuntTarget != null
                    && (IsNoGo(goal.positionX, goal.positionY)
                        || (!InTerritory(self, goal.positionX, goal.positionY) && Helpers.distanceTo(self._state, goal) > 900)))
                {
                    m.IgnoreUntil[m.HuntTarget] = now + 8000;
                    m.HuntTarget = null;
                    goal = null;
                }
                bool hunting = goal != null;
                if (!hunting && TryInvestigate(self, script, now))
                    return;   // nobody tracked: go and look where the fighting is
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
                    _giveUps++;   // counted in the 30 s give-up summary (was one log line each)
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

        /// <summary>The granted (non-toxic) play area test, set by the gametype; null = anywhere.</summary>
        public static Func<short, short, bool> PlayArea;

        static bool InArea(short x, short y) { return PlayArea == null || PlayArea(x, y); }

        /// <summary>
        /// Territories (2026-11-15, Script_Eol.Territories.cs): may this bot go to (x,y)? (its own zone in NW, never the
        /// new player buffer). TerritoryPoint: a random spot in its zone when nothing nearby qualifies. NoGo: the buffer
        /// (no bot chases or investigates in there). All null = no limits.
        /// </summary>
        public static Func<Bot, short, short, bool> Territory;
        public static Func<Bot, Random, Helpers.ObjectState> TerritoryPoint;
        /// <summary>Distance (px) from a point to the new player buffer's edge - negative inside; null = no buffer.</summary>
        public static Func<short, short, double> BufferEdge;

        static bool InTerritory(Bot b, short x, short y) { return Territory == null || Territory(b, x, y); }
        static bool IsNoGo(short x, short y) { return BufferEdge != null && BufferEdge(x, y) < 0; }
        static bool NearBuffer(short x, short y, int margin) { return BufferEdge != null && BufferEdge(x, y) < margin; }

        // ---- the new player buffer (2026-11-15): a bot that comes within BufferTurnBackRaw of it drops its target and
        // roams away; pulled back again and again (players luring it), it goes further out each time and stays away
        // longer, so it never ends up circling the edge. Players inside the buffer are never targeted.
        const int BufferTurnBackRaw = 450;        // this close to the edge -> turn back
        const int BufferAwayBaseRaw = 900;        // first turn-back: roam at least this far beyond the edge
        const int BufferAwayStepRaw = 800;        // ...this much further for every recent pull (up to 4)
        const int BufferPullMemoryMs = 90000;     // pulls this recent count
        const int BufferTargetMarginRaw = 150;    // a target this close to (or inside) the buffer is never engaged

        /// <summary>Turn the bot away if it's at the buffer. True while it is heading away (no hunting / engaging).</summary>
        /// <summary>(2026-11-17) set by the script: a bot its player summoned into the new player zone (it stays round them there).</summary>
        public static Func<Bot, bool> HeldBySummon;

        static bool BufferGuard(Bot self, Mind m, int now)
        {
            if (BufferEdge == null || self?._state == null)
                return false;
            if (HeldBySummon != null && HeldBySummon(self))
                return false;   // (summoned to its player in the new player zone - not turned away)
            if (NearBuffer(self._state.positionX, self._state.positionY, BufferTurnBackRaw)
                && (m.BufferRetreatUntil == 0 || now - m.BufferRetreatUntil >= 0 || now - m.LastBufferPull > 4000))
            {
                // one more pull (a fresh streak after BufferPullMemoryMs quiet)
                m.BufferPulls = (m.LastBufferPull != 0 && now - m.LastBufferPull < BufferPullMemoryMs) ? Math.Min(4, m.BufferPulls + 1) : 1;
                m.LastBufferPull = now == 0 ? 1 : now;
                if (m.HuntTarget != null)
                    m.IgnoreUntil[m.HuntTarget] = now + 8000 + 4000 * m.BufferPulls;
                m.HuntTarget = null;
                m.InvestUntil = 0;
                int away = BufferAwayBaseRaw + BufferAwayStepRaw * (m.BufferPulls - 1);
                m.RoamDest = PickAwayFromBuffer(self, m, away);
                m.RoamPickTick = now;
                m.BufferRetreatUntil = now + 6000 + 4000 * m.BufferPulls;
                if (m.BufferRetreatUntil == 0) m.BufferRetreatUntil = 1;
                ResetProgress(m);
                self.Navigation?.Clear();
            }
            return m.BufferRetreatUntil != 0 && now - m.BufferRetreatUntil < 0;
        }

        /// <summary>An open spot in the bot's territory at least awayRaw beyond the buffer's edge.</summary>
        static Helpers.ObjectState PickAwayFromBuffer(Bot self, Mind m, int awayRaw)
        {
            Helpers.ObjectState best = null;
            double bestEdge = -1;
            for (int i = 0; i < 80; i++)
            {
                double a = m.R.NextDouble() * Math.PI * 2;
                int rad = awayRaw + m.R.Next(1400);
                int x = self._state.positionX + (int)(Math.Cos(a) * rad), y = self._state.positionY + (int)(Math.Sin(a) * rad);
                if (x < 64 || y < 64 || x > short.MaxValue - 64 || y > short.MaxValue - 64)
                    continue;
                short sx = (short)x, sy = (short)y;
                double edge = BufferEdge(sx, sy);
                if (edge < awayRaw || !InArea(sx, sy) || !InTerritory(self, sx, sy) || !EolHqPatrol.IsOpenForBot(self, sx, sy))
                    continue;
                if (edge > bestEdge) { bestEdge = edge; best = new Helpers.ObjectState { positionX = sx, positionY = sy }; }
                if (bestEdge >= awayRaw + 600)
                    break;
            }
            if (best == null && TerritoryPoint != null)
            {   // nothing that far nearby: anywhere in its territory well clear of the buffer
                for (int i = 0; i < 6 && best == null; i++)
                {
                    Helpers.ObjectState p = TerritoryPoint(self, m.R);
                    if (p != null && BufferEdge(p.positionX, p.positionY) >= BufferAwayBaseRaw)
                        best = p;
                }
            }
            return best;
        }

        /// <summary>Engine hook: where a bot that has idled 4 s should go (inside the open sectors). Engineers stay.</summary>
        static Helpers.ObjectState IdleRoamFor(Bot b)
        {
            if (b == null || b is Engineer || b._state == null)
                return null;
            Helpers.ObjectState p = PickRoamPoint(b, Get(b), null);
            return p != null && InArea(p.positionX, p.positionY) && InTerritory(b, p.positionX, p.positionY) ? p : null;
        }

        static Func<InfantryVehicle, Vector3> IdleRoamSteerFor(Bot b, Helpers.ObjectState d)
        {
            return EolHqPatrol.MakeLineClearPendingSeek(b, () => d);
        }

        /// <summary>An open spot 900-2600 px away (around home if given), clear of walls on every side.</summary>
        // ---- points of interest (local 2026-11-14): bases (HQs + their turrets), flags, crash ships - where real
        // players go. Refreshed every 5 s, merged onto a 400 px grid so a base counts once.
        static readonly List<KeyValuePair<short, short>> _poi = new List<KeyValuePair<short, short>>();
        static int _tickPoi;
        /// <summary>(2026-11-19) extra points of interest from the gametype (raided player bases, weighted) - added as they are.</summary>
        public static Func<List<KeyValuePair<short, short>>> ExtraPointsOfInterest;

        static void RefreshPointsOfInterest(Arena arena, int now)
        {
            if (arena == null || (_tickPoi != 0 && now - _tickPoi < 5000))
                return;
            _tickPoi = now;
            _poi.Clear();
            HashSet<int> cells = new HashSet<int>();
            Action<short, short> add = (x, y) =>
            {
                int cell = (x / 400) * 1000 + (y / 400);
                if (cells.Add(cell))
                    _poi.Add(new KeyValuePair<short, short>(x, y));
            };
            try
            {
                if (arena._flags != null)
                    foreach (Arena.FlagState f in arena._flags.Values)
                        if (f != null && f.bActive)
                            add(f.posX, f.posY);
                foreach (Vehicle v in arena.Vehicles)
                {
                    if (v == null || v.IsDead || v._type == null || v._state == null)
                        continue;
                    string n = v._type.Name ?? "";
                    bool hq = n.IndexOf("HQ", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Headquarters", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool turret = v is Computer && n.IndexOf("Turret", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool ship = n.IndexOf("Spacecraft", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (hq || turret || ship)
                        add(v._state.positionX, v._state.positionY);
                }
                List<KeyValuePair<short, short>> extra = ExtraPointsOfInterest?.Invoke();
                if (extra != null)
                    _poi.AddRange(extra);
            }
            catch { }
        }

        /// <summary>A point of interest (or the ground between two) this bot hasn't searched lately; null = none.</summary>
        static Helpers.ObjectState PickPointOfInterest(Bot self, Mind m)
        {
            RefreshPointsOfInterest(self._arena, Environment.TickCount);
            if (_poi.Count == 0)
                return null;
            for (int tries = 0; tries < 12; tries++)
            {
                KeyValuePair<short, short> a = _poi[m.R.Next(_poi.Count)];
                int x = a.Key, y = a.Value;
                if (_poi.Count > 1 && m.R.NextDouble() < 0.35)
                {   // the in-between places: halfway to another point of interest
                    KeyValuePair<short, short> b = _poi[m.R.Next(_poi.Count)];
                    x = (x + b.Key) / 2; y = (y + b.Value) / 2;
                }
                // somewhere around it, on open ground (not on the turret / HQ itself)
                double ang = m.R.NextDouble() * Math.PI * 2;
                int off = 120 + m.R.Next(260);
                short sx = (short)Math.Max(64, Math.Min(short.MaxValue - 64, x + Math.Cos(ang) * off));
                short sy = (short)Math.Max(64, Math.Min(short.MaxValue - 64, y + Math.Sin(ang) * off));
                if (!EolHqPatrol.IsOpenForBot(self, sx, sy) || !InArea(sx, sy) || !InTerritory(self, sx, sy))
                    continue;
                if (Helpers.distanceTo(self._state.positionX, self._state.positionY, sx, sy) < 500)
                    continue;
                bool recent = false;
                foreach (long v in m.Searched)
                    if (Helpers.distanceTo(sx, sy, (short)(v >> 16), (short)(v & 0xFFFF)) < 450) { recent = true; break; }
                if (recent)
                    continue;
                if (NearGivenUpTurret(m, sx, sy))
                    continue;   // (2026-11-15) not back into the fire of a turret it just gave up on
                return new Helpers.ObjectState { positionX = sx, positionY = sy };
            }
            return null;
        }

        /// <summary>Within reach of a live turret this bot gave up on (still ignored)?</summary>
        static bool NearGivenUpTurret(Mind m, short x, short y)
        {
            if (m.IgnoreUntil.Count == 0)
                return false;
            int now = Environment.TickCount;
            foreach (KeyValuePair<object, int> kv in m.IgnoreUntil)
            {
                Computer c = kv.Key as Computer;
                if (c == null || c.IsDead || c._state == null || now - kv.Value >= 0)
                    continue;
                if (Helpers.distanceTo(x, y, c._state.positionX, c._state.positionY) < TurretLeaveRaw)
                    return true;
            }
            return false;
        }

        static Helpers.ObjectState PickRoamPoint(Bot self, Mind m, Helpers.ObjectState home)
        {
            // Half the time head for a point of interest - a base, a flag, a crash ship, or the ground between
            // two - like a player looking for a fight; otherwise sweep new open ground.
            if (m.R.NextDouble() < 0.5)
            {
                Helpers.ObjectState poi = PickPointOfInterest(self, m);
                if (poi != null)
                {
                    m.Searched.Enqueue(((long)(ushort)poi.positionX << 16) | (ushort)poi.positionY);
                    while (m.Searched.Count > 8)
                        m.Searched.Dequeue();
                    return poi;
                }
            }
            // Logical searching: of a dozen valid spots, go to the one farthest from everywhere this bot searched
            // recently (and from its squad-mates' current search spots), so it sweeps new ground instead of
            // circling the same patch.
            short cx = home != null ? home.positionX : self._state.positionX;
            short cy = home != null ? home.positionY : self._state.positionY;
            Helpers.ObjectState best = null;
            double bestScore = -1;
            for (int i = 0, found = 0; i < 90 && found < 12; i++)
            {
                double a = m.R.NextDouble() * Math.PI * 2;
                int rad = 900 + m.R.Next(1700);
                int x = cx + (int)(Math.Cos(a) * rad), y = cy + (int)(Math.Sin(a) * rad);
                if (x < 64 || y < 64 || x > short.MaxValue - 64 || y > short.MaxValue - 64)
                    continue;
                short sx = (short)x, sy = (short)y;
                if (!EolHqPatrol.IsOpenForBot(self, sx, sy))
                    continue;
                if (!InArea(sx, sy))
                    continue;   // never roam into the toxic zone
                if (!InTerritory(self, sx, sy))
                    continue;   // its own territory (NW) and never the new player buffer
                if (Helpers.distanceTo(self._state.positionX, self._state.positionY, sx, sy) < 600)
                    continue;
                found++;
                double novelty = 3000;
                foreach (long v in m.Searched)
                {
                    double d = Helpers.distanceTo(sx, sy, (short)(v >> 16), (short)(v & 0xFFFF));
                    if (d < novelty) novelty = d;
                }
                double score = novelty + m.R.NextDouble() * 300;
                if (score > bestScore) { bestScore = score; best = new Helpers.ObjectState { positionX = sx, positionY = sy }; }
            }
            if (best != null)
            {
                m.Searched.Enqueue(((long)(ushort)best.positionX << 16) | (ushort)best.positionY);
                while (m.Searched.Count > 8)
                    m.Searched.Dequeue();
            }
            if (best == null)
                best = PickPointOfInterest(self, m);   // never left with nowhere to go
            if (best == null && TerritoryPoint != null)
                best = TerritoryPoint(self, m.R);      // out of its territory with none nearby: head into it
            return best;
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
            Bot.IdleRoamTarget = IdleRoamFor;          // nobody stands around: idle bots roam to points of interest
            Bot.IdleRoamSteer = IdleRoamSteerFor;
            InfServer.Game.Modules.BotActions.BotActionHelpers.IgnoreTarget = IsTargetIgnored;
            Arena.GunfireNoise = OnGunfireNoise;
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
