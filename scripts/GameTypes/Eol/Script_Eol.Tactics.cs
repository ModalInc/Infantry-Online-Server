using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-16b, owner: "merge the CTF:X bot code so they score high") the CTF:X ideas Eol lacked, rebuilt on
    /// Eol's own bot code:
    ///   - ADAPTIVE SKILL per player (CTF:X MiniSkill): each player's form against bots (kills of bots vs deaths
    ///     to bots, 3 min half-life) makes the bots aiming at them up to 25 % sharper (a farmer) or gentler (a
    ///     newcomer) - reaction, settle and every aim error (engine hook Bot.SkillVsTarget).
    ///   - REAL MEDICS (CTF:X pocket medic + Pub support loops): squad medics (Combat Medic, Field Surgeon,
    ///     BioChem) heal hurt squad mates - bots and players - with the zone's own Area Heal, the way a player
    ///     medic does (the player's client gets the heal exactly as from a human medic), and between fights they
    ///     stay close to whoever needs them, or 90-170 px behind a player on their squad.
    ///   - REGROUP (CTF:X odds / nerve): a squad down to under half its strength and out of a fight holds and waits
    ///     for its reinforcements (they come through the dropship portal) instead of feeding in one by one.
    ///   - SQUAD VOICE: short team-chat replies from a squad bot to its players (orders, heals), like a teammate.
    /// </summary>
    public partial class Script_Eol
    {
        // ---------- adaptive skill ----------

        class PlayerForm
        {
            public double Kills, Deaths;   // decayed counts
            public int Tick;
        }
        readonly Dictionary<InfServer.Game.Player, PlayerForm> _form = new Dictionary<InfServer.Game.Player, PlayerForm>();
        const double FormHalfLifeMs = 180000;
        const double AdaptiveRange = 0.25;
        Func<Bot, Helpers.ObjectState, double> _skillVsFn;

        PlayerForm FormOf(InfServer.Game.Player p, int now)
        {
            PlayerForm f;
            if (!_form.TryGetValue(p, out f))
                _form[p] = f = new PlayerForm { Tick = now };
            double k = Math.Pow(0.5, (now - f.Tick) / FormHalfLifeMs);
            f.Kills *= k;
            f.Deaths *= k;
            f.Tick = now;
            return f;
        }

        void NoteFormBotKill(InfServer.Game.Player killer)
        {
            if (killer != null)
                FormOf(killer, Environment.TickCount).Kills += 1;
        }

        void NoteFormBotDeath(InfServer.Game.Player victim)
        {
            if (victim != null)
                FormOf(victim, Environment.TickCount).Deaths += 1;
        }

        /// <summary>1 + 0.25 x form, form = log2(kills / deaths) / 2 clamped to +-1 (CTF:X's curve).</summary>
        double SkillFactorAgainst(Bot bot, Helpers.ObjectState target)
        {
            if (_arena == null || target == null)
                return 1.0;
            foreach (InfServer.Game.Player p in _arena.PlayersIngame)
            {
                if (p == null || !ReferenceEquals(p._state, target))
                    continue;
                PlayerForm f = FormOf(p, Environment.TickCount);
                double form = Math.Log((f.Kills + 1.0) / (f.Deaths + 1.0), 2) / 2.0;
                form = Math.Max(-1.0, Math.Min(1.0, form));
                return 1.0 + AdaptiveRange * form;
            }
            return 1.0;
        }

        void WireTacticsHooks()
        {
            if (_skillVsFn == null)
                _skillVsFn = SkillFactorAgainst;
            Bot.SkillVsTarget = _skillVsFn;   // (re-set every poll: survives script reloads)
            // (2026-11-17, owner: "no unknowns") a death with no known killer still gets a name in the killfeed
            if (_killerNameBot == null)
            {
                _killerNameBot = b => b != null && b._state != null ? NoKillerName(b._state.positionX, b._state.positionY) : null;
                _killerNamePlayer = p => p != null && p._state != null ? NoKillerName(p._state.positionX, p._state.positionY) : null;
            }
            Arena.KillerNameFallback = _killerNameBot;
            Arena.KillerNameFallbackPlayer = _killerNamePlayer;
        }

        Func<Bot, string> _killerNameBot;
        Func<InfServer.Game.Player, string> _killerNamePlayer;

        /// <summary>What most likely killed someone at (x,y) when nobody is on record: the Alien Queen close by,
        /// radiation outside the open sectors; null = the engine's own fallback.</summary>
        string NoKillerName(short x, short y)
        {
            string nestName = SwarmNestNameNear(x, y, 700);   // (Alien Queen or Alien King)
            if (nestName != null)
                return nestName;
            if (_eol != null && !_eol.IsInAnyActiveSector(x, y))
                return "Radiation";
            return null;
        }

        // ---------- medics ----------

        static readonly HashSet<int> MedicClasses = new HashSet<int> { ClsMedic, ClsFieldSurgeon, ClsBioChem };
        const int ItmAreaHeal = 432;
        const int MedicHealRadius = 96;          // Area Heal's own radius
        const int MedicSeekRadius = 520;         // goes to a hurt mate this far away
        const int MedicPulseMs = 1500;
        const double MedicHurtBelow = 0.8;       // heals anyone under 80 %
        int _tickMedics;
        readonly Dictionary<Bot, int> _medicNextPulse = new Dictionary<Bot, int>();

        public bool IsSquadMedic(Bot b)
        {
            // (2026-11-17: medics heal on every human / pirate bot team, not only the roaming squads)
            return b != null && b._type != null && (MedicClasses.Contains(b._type.Id) && !IsAlienBotTeam(b._team) && !(b is AlienHorde) || IsBoardMedic(b));
        }

        static int MaxHealthOf(Vehicle v)
        {
            if (v == null || v._type == null)
                return 100;
            return Math.Max(1, v._type.Hitpoints);   // (2026-11-17: bots keep their real class HP now - no lift to 100)
        }

        static int PlayerMaxHealth(InfServer.Game.Player p)
        {
            Vehicle v = p != null ? p.ActiveVehicle : null;
            return v != null && v._type != null && v._type.Hitpoints > 0 ? v._type.Hitpoints : 100;
        }

        /// <summary>A heal pulse every 1.5 s from each squad medic that has a hurt mate in reach.</summary>
        void PollMedics(int now)
        {
            if (_arena == null || now - _tickMedics < 250)
                return;
            _tickMedics = now;
            ItemInfo.RepairItem heal = _arena._server._assets.getItemByID(ItmAreaHeal) as ItemInfo.RepairItem;
            foreach (Bot m in _arena.ActiveBots)
            {
                if (m == null || m.IsDead || m.bCondemned || !IsSquadMedic(m) || IsDeploying(m))
                    continue;
                int due;
                if (_medicNextPulse.TryGetValue(m, out due) && now - due < 0)
                    continue;
                int healed = 0;
                // squad bots
                foreach (Bot b in _arena.ActiveBots)
                {
                    if (b == null || b == m || b.IsDead || b.bCondemned || b._team != m._team)
                        continue;
                    int max = MaxHealthOf(b);
                    if (b._state.health >= max * MedicHurtBelow || Helpers.distanceTo(b._state, m._state) > MedicHealRadius)
                        continue;
                    b._state.health = (short)Math.Min(max, b._state.health + Math.Max(8, max / 5));
                    healed++;
                }
                // players on the squad: their client heals them, as from a player medic
                if (heal != null && m._team != null)
                {
                    InfServer.Game.Player caller = SquadHealCaller(m._team, now);
                    foreach (InfServer.Game.Player p in m._team.ActivePlayers)
                    {
                        if (p == null || p.IsDead || p._state == null || p._client == null)
                            continue;
                        // (2026-11-17) whoever called H is healed right up to full, everyone else under 80 %
                        double below = p == caller ? 1.0 : MedicHurtBelow;
                        if (p._state.health >= PlayerMaxHealth(p) * below || Helpers.distanceTo(p._state, m._state) > MedicHealRadius)
                            continue;
                        SC_ItemUsed used = new SC_ItemUsed();
                        used.userPlayer = m._id;
                        used.targetVehicle = p._id;
                        used.itemID = (short)heal.id;
                        used.posX = m._state.positionX;
                        used.posY = m._state.positionY;
                        p._client.sendReliable(used);
                        healed++;
                        // (2026-11-17) an H call is a few heals, not a fountain: 3, then the medic is back to normal
                        SquadCall hc;
                        if (p == caller && _squadHealCall.TryGetValue(m._team, out hc) && --hc.HealsLeft <= 0)
                        {
                            _squadHealCall.Remove(m._team);
                            caller = null;
                        }
                        if (_arena._rand.Next(4) == 0)
                            SquadSay(m, Pick3("patched you up", "got you", "heal up, stay close"));
                    }
                }
                if (healed > 0)
                    _medicNextPulse[m] = now + MedicPulseMs;
            }
            if (_medicNextPulse.Count > 64)
            {
                List<Bot> gone = new List<Bot>();
                foreach (Bot b in _medicNextPulse.Keys)
                    if (b == null || b.bCondemned)
                        gone.Add(b);
                foreach (Bot b in gone)
                    _medicNextPulse.Remove(b);
            }
        }

        /// <summary>
        /// Where a squad medic goes when it isn't fighting: to the most hurt mate in reach, else beside a player on
        /// its squad (pocket medic). Null = carry on with the squad.
        /// </summary>
        public Helpers.ObjectState MedicMoveTarget(Bot m)
        {
            if (!IsSquadMedic(m) || IsDeploying(m) || m._team == null)
                return null;
            // (2026-11-17, owner: "H should get healers to heal the person") a player who called H comes first
            InfServer.Game.Player caller = SquadHealCaller(m._team, Environment.TickCount);
            if (caller != null)
                return Helpers.distanceTo(caller._state, m._state) > 50
                    ? new Helpers.ObjectState { positionX = caller._state.positionX, positionY = caller._state.positionY } : null;
            Helpers.ObjectState best = null;
            double bestNeed = 0;
            foreach (Bot b in _arena.ActiveBots)
            {
                if (b == null || b == m || b.IsDead || b.bCondemned || b._team != m._team || IsDeploying(b))
                    continue;
                int max = MaxHealthOf(b);
                double need = 1.0 - (double)b._state.health / max;
                if (need < 1.0 - MedicHurtBelow || Helpers.distanceTo(b._state, m._state) > MedicSeekRadius)
                    continue;
                if (need > bestNeed) { bestNeed = need; best = b._state; }
            }
            InfServer.Game.Player pocket = null;
            foreach (InfServer.Game.Player p in m._team.ActivePlayers)
            {
                if (p == null || p.IsDead || p._state == null || !InPlayArea(p._state.positionX, p._state.positionY, 0))
                    continue;
                double need = 1.0 - (double)p._state.health / PlayerMaxHealth(p);
                double d = Helpers.distanceTo(p._state, m._state);
                if (need >= 1.0 - MedicHurtBelow && d <= MedicSeekRadius && need + 0.1 > bestNeed)
                {   // (players first when as hurt)
                    bestNeed = need + 0.1;
                    best = p._state;
                }
                if (pocket == null && d < 1400)
                    pocket = p;
            }
            if (best != null)
                return Helpers.distanceTo(best, m._state) > 60 ? new Helpers.ObjectState { positionX = best.positionX, positionY = best.positionY } : null;
            if (pocket != null && Helpers.distanceTo(pocket._state, m._state) > 170)
            {   // 90-170 px behind the player
                double yaw = pocket._state.yaw * 1.5 * Math.PI / 180.0;
                short sx = (short)(pocket._state.positionX - Math.Sin(yaw) * 120);
                short sy = (short)(pocket._state.positionY + Math.Cos(yaw) * 120);
                return new Helpers.ObjectState { positionX = sx, positionY = sy };
            }
            return null;
        }

        // ---------- regroup ----------

        /// <summary>
        /// True when this squad's leader should hold and wait for reinforcements: under half its bots out, nobody
        /// on it is a player, and not waited 45 s yet.
        /// </summary>
        readonly Dictionary<Team, int> _regroupSince = new Dictionary<Team, int>();
        public bool SquadShouldRegroup(RoamingAttacker leader, int now)
        {
            Team t = leader != null ? leader._team : null;
            if (t == null || SquadHumans(t) > 0 || IsPirateRaidTeam(t))
                return false;
            int want;
            if (!_strikeTargetSize.TryGetValue(t, out want))
                want = IsHarrierCrewTeam(t) ? RosterPirateRoamers : 0;
            if (want <= 2)
                return false;
            int outNow = 0;
            foreach (RoamingAttacker b in EliteSquadMembers(t))
                if (!IsDeploying(b))
                    outNow++;
            if (outNow * 2 >= want)
            {
                _regroupSince.Remove(t);
                return false;
            }
            int since;
            if (!_regroupSince.TryGetValue(t, out since))
                _regroupSince[t] = since = now;
            return now - since < 45000;
        }

        // ---------- squad voice ----------

        readonly Dictionary<Team, int> _squadSayNext = new Dictionary<Team, int>();

        /// <summary>A short team-chat line from a squad bot to its players (at most one every 6 s per squad).</summary>
        public void SquadSay(Bot from, string text)
        {
            if (from == null || from._team == null || string.IsNullOrEmpty(text) || from._team.ActivePlayerCount == 0)
                return;
            int now = Environment.TickCount, next;
            if (_squadSayNext.TryGetValue(from._team, out next) && now - next < 0)
                return;
            _squadSayNext[from._team] = now + 6000;
            Helpers.Player_RouteChatRaw(from._team, from._alias, text, 0, Helpers.Chat_Type.Team);
        }

        string Pick3(string a, string b, string c)
        {
            int r = _arena._rand.Next(3);
            return r == 0 ? a : r == 1 ? b : c;
        }
    }
}
