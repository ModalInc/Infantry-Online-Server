using System;
using System.Collections.Generic;
using System.Linq;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

using Assets;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// BASE RAIDS (2026-11-19, owner: "if a human player builds a HQ, and they start getting the roamer flags in the base. i
    /// wanted the roamer bots running around to start attacking that base to kill the HQ and to grab the flags and put
    /// somewhere ... if the team holds all roamers then more roamer teams come ... 1 roamer less come, 2 more, 3 more, 4 even
    /// more ... it doesn't really matter where they ... drop them, somewhere else so the players have to get them again ...
    /// add a point of interest to the roamers to rotate in").
    ///   - A player team with a live HQ that holds Eol Roamer flags is raided: every 10 s the roaming squads (pirate roamers,
    ///     Ghost Reapers, the human squads, the pirate raiders while they're out - never a squad with a player on it) are
    ///     shared out: 1 roamer flag held = 1 squad comes, 2 = 2, 3 = 3, all 4 = 5. Nearest squads first; fewer flags,
    ///     the extra squads go back to roaming. No HQ or no roamer flag: the raid is over.
    ///   - A raiding squad marches on the base (out of its usual territory) - its leader to a roamer flag the team holds
    ///     (near the squad) or the HQ, the rest in formation - fights what it finds there and shoots the HQ (bots treat
    ///     an HQ like a turret), except while one of the team's roamer flags is close: that first.
    ///   - A raider that reaches one of the team's roamer flags lying on the ground takes it: the flag goes to the bot's
    ///     team and moves with the bot, which runs (no fighting) to a spot 3,000+ px from the base and drops it there.
    ///     Killed on the way, it drops where it fell; a player who touches the flag takes it back as usual.
    ///   - The raided base is also a point of interest for every roaming bot (more weight with more flags held).
    /// </summary>
    public partial class Script_Eol
    {
        /// <summary>Raiding squads for 0 / 1 / 2 / 3 / 4 roamer flags held.</summary>
        static readonly int[] RaidSquadsForFlags = { 0, 1, 2, 3, 5 };
        const int RaidPollMs = 10000;
        const int RaidStealRangeRaw = 120;
        const int RaidDropMinFromBaseRaw = 3000;
        const int RaidStolenMaxMs = 150000;   // a thief that can't get there in 2.5 min drops it where it is
        const int RaidAfterHqMs = 300000;     // the HQ down: the raid goes on for the flags up to 5 min more
        const int RaidFlagFirstRaw = 700;     // a roamer flag this close: a raider goes for it before shooting the HQ

        sealed class BaseRaid
        {
            public Team Target;
            public Vehicle Hq;                 // null / dead once it's down
            public Helpers.ObjectState Base;   // where the base is (the HQ's spot)
            public int Flags, HqLostAt;
            public readonly List<Team> Squads = new List<Team>();
        }

        sealed class StolenFlag
        {
            public Arena.FlagState Fs;
            public Bot Carrier;
            public Team From;
            public Helpers.ObjectState Drop;
            public int Since, LastSend;
            public short SentX, SentY;
        }

        readonly Dictionary<Team, BaseRaid> _raids = new Dictionary<Team, BaseRaid>();     // raided team -> raid
        readonly Dictionary<Team, BaseRaid> _raidOf = new Dictionary<Team, BaseRaid>();    // raiding squad team -> raid
        readonly Dictionary<Arena.FlagState, StolenFlag> _stolen = new Dictionary<Arena.FlagState, StolenFlag>();
        int _tickRaids, _tickStolen;

        static bool IsRoamerFlag(Arena.FlagState fs)
        {
            return fs?.flag != null && (fs.flag.GeneralData.Name ?? "").IndexOf("Eol Roamer", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>The Eol Roamer flags <paramref name="team"/> holds right now (in play).</summary>
        int RoamerFlagsHeld(Team team)
        {
            int n = 0;
            if (_arena?._flags == null || team == null)
                return 0;
            foreach (Arena.FlagState fs in _arena._flags.Values)
                if (IsRoamerFlag(fs) && fs.team == team && FlagInPlay(fs))
                    n++;
            return n;
        }

        /// <summary>The roaming squads that could be sent (no player on them, a leader out in the field).</summary>
        IEnumerable<Team> RaidCandidateSquads()
        {
            List<Team> all = new List<Team> { botTeam6, botTeam8, botTeamStrike1 };
            all.AddRange(_mixedTeams);
            all.Add(botTeam4); all.Add(botTeamRaid2);
            foreach (Team t in all.Distinct())
            {
                if (t == null || !IsRoamingSquadTeam(t) || IsAssassinTeam(t) || SquadHumans(t) > 0)
                    continue;
                RoamingAttacker lead = EliteSquadLeader(t);
                if (lead == null || lead._state == null || IsDeploying(lead))
                    continue;
                yield return t;
            }
        }

        void PollBaseRaids(int now)
        {
            if (_arena == null || !_arena._bGameRunning)
                return;
            if (now - _tickStolen >= 250)
            {
                _tickStolen = now;
                PollRaidSteals(now);
                PollStolenFlags(now);
            }
            if (_tickRaids != 0 && now - _tickRaids < RaidPollMs)
                return;
            _tickRaids = now;

            // the player teams with a base and roamer flags
            HashSet<Team> raided = new HashSet<Team>();
            foreach (Team t in _arena.Teams)
            {
                if (t == null || IsManagedEolBotFactionTeam(t) || t.ActivePlayerCount == 0)
                    continue;
                int k = RoamerFlagsHeld(t);
                if (k <= 0)
                    continue;
                Vehicle hq = FindHqVehicleForTeam(t);
                bool hqUp = hq != null && !hq.IsDead && hq._state != null;
                BaseRaid r;
                _raids.TryGetValue(t, out r);
                if (!hqUp)
                {   // no base: no new raid; one under way goes on for the flags a while
                    if (r == null)
                        continue;
                    if (r.HqLostAt == 0) r.HqLostAt = now;
                    if (now - r.HqLostAt > RaidAfterHqMs)
                        continue;
                }
                raided.Add(t);
                if (r == null)
                    _raids[t] = r = new BaseRaid { Target = t };
                if (hqUp) { r.Hq = hq; r.HqLostAt = 0; r.Base = new Helpers.ObjectState { positionX = hq._state.positionX, positionY = hq._state.positionY }; }
                else r.Hq = null;
                int before = r.Squads.Count, flagsBefore = r.Flags;
                r.Flags = k;
                int want = RaidSquadsForFlags[Math.Min(k, RaidSquadsForFlags.Length - 1)];

                // squads that can't go on (a player joined it, it's wiped out): released
                for (int i = r.Squads.Count - 1; i >= 0; i--)
                {
                    Team s = r.Squads[i];
                    if (SquadHumans(s) > 0 || EliteSquadLeader(s) == null)
                    { r.Squads.RemoveAt(i); _raidOf.Remove(s); }
                }
                while (r.Squads.Count > want)
                {
                    Team s = r.Squads[r.Squads.Count - 1];
                    r.Squads.RemoveAt(r.Squads.Count - 1); _raidOf.Remove(s);
                }
                if (r.Squads.Count < want)
                {   // the nearest free squads
                    foreach (Team s in RaidCandidateSquads().Where(s => !_raidOf.ContainsKey(s))
                        .OrderBy(s => Helpers.distanceTo(EliteSquadLeader(s)._state, r.Base)).ToList())
                    {
                        if (r.Squads.Count >= want)
                            break;
                        r.Squads.Add(s); _raidOf[s] = r;
                    }
                }
                if (r.Squads.Count > before || (before == 0 && r.Squads.Count > 0))
                {
                    string where = Helpers.posToLetterCoord(r.Base.positionX, r.Base.positionY);
                    t.sendArenaMessage(string.Format("&Raid - {0} roamer squad{1} coming for your base at {2} and your {3} roamer flag{4}!",
                        r.Squads.Count, r.Squads.Count == 1 ? " is" : "s are", where, k, k == 1 ? "" : "s"), _config.flag.periodicBong);
                    Log.write(TLog.Normal, "[Eol] Base raid on {0} ({1} roamer flags): {2} squads ({3}).", t._name, k, r.Squads.Count,
                        string.Join(", ", r.Squads.Select(s => s._name)));
                }
                else if (k != flagsBefore)
                    Log.write(TLog.Normal, "[Eol] Base raid on {0}: {1} roamer flags, {2} squads.", t._name, k, r.Squads.Count);
            }
            // raids that are over (HQ down, no roamer flags left, the team gone)
            foreach (Team t in _raids.Keys.Where(t => !raided.Contains(t)).ToList())
            {
                foreach (Team s in _raids[t].Squads) _raidOf.Remove(s);
                _raids.Remove(t);
                Log.write(TLog.Normal, "[Eol] Base raid on {0} over.", t._name);
            }
        }

        BaseRaid RaidOf(Bot b)
        {
            BaseRaid r;
            return b?._team != null && _raidOf.TryGetValue(b._team, out r) && r.Base != null ? r : null;
        }

        /// <summary>The raided team's roamer flags a raider can take: on the ground, not carried, not already stolen.</summary>
        IEnumerable<Arena.FlagState> StealableFlags(BaseRaid r)
        {
            if (_arena?._flags == null)
                yield break;
            foreach (Arena.FlagState fs in _arena._flags.Values)
                if (IsRoamerFlag(fs) && fs.team == r.Target && fs.carrier == null && FlagInPlay(fs) && !_stolen.ContainsKey(fs))
                    yield return fs;
        }

        /// <summary>The nearest of the raided team's roamer flags on the ground; null = none.</summary>
        Arena.FlagState NearestStealable(BaseRaid r, Bot b, out double dist)
        {
            Arena.FlagState best = null; dist = double.MaxValue;
            foreach (Arena.FlagState fs in StealableFlags(r))
            {
                double d = Helpers.distanceTo(b._state.positionX, b._state.positionY, fs.posX, fs.posY);
                if (d < dist) { dist = d; best = fs; }
            }
            return best;
        }

        /// <summary>A raiding squad's goal (the leader marches on it): a roamer flag of the team's near the squad (or any, once
        /// the HQ is down), else the HQ; null = not raiding.</summary>
        public Helpers.ObjectState RaidGoalFor(Bot b)
        {
            BaseRaid r = RaidOf(b);
            if (r == null || b._state == null)
                return null;
            double d;
            Arena.FlagState fs = NearestStealable(r, b, out d);
            bool hqUp = r.Hq != null && !r.Hq.IsDead && r.Hq._state != null;
            if (fs != null && (!hqUp || d <= 1800 || d < Helpers.distanceTo(b._state, r.Hq._state)))
                return new Helpers.ObjectState { positionX = fs.posX, positionY = fs.posY };
            return hqUp ? new Helpers.ObjectState { positionX = r.Hq._state.positionX, positionY = r.Hq._state.positionY } : null;
        }

        /// <summary>The HQ this bot's squad is raiding (bots shoot HQs like turrets), while one of the team's
        /// roamer flags is close: leave the HQ for now - the flag first.</summary>
        public bool RaidFlagBeforeHq(Bot b, Vehicle v)
        {
            BaseRaid r = RaidOf(b);
            if (r == null || !ReferenceEquals(r.Hq, v) || b._state == null)
                return false;
            double d;
            return NearestStealable(r, b, out d) != null && d <= RaidFlagFirstRaw;
        }

        /// <summary>A raiding bot: no territory limit.</summary>
        public bool IsRaidingBot(Bot b) { return RaidOf(b) != null || StolenFlagDropFor(b) != null; }

        /// <summary>Where this bot is taking a stolen flag; null = it carries none.</summary>
        public Helpers.ObjectState StolenFlagDropFor(Bot b)
        {
            if (b == null || _stolen.Count == 0)
                return null;
            foreach (StolenFlag s in _stolen.Values)
                if (ReferenceEquals(s.Carrier, b))
                    return s.Drop;
            return null;
        }

        /// <summary>Raiders next to one of the team's roamer flags on the ground take it.</summary>
        void PollRaidSteals(int now)
        {
            if (_raidOf.Count == 0 || _arena == null)
                return;
            foreach (Bot b in _arena.ActiveBots)
            {
                if (b == null || b.IsDead || b.bCondemned || b._state == null || IsDeploying(b))
                    continue;
                BaseRaid r = RaidOf(b);
                if (r == null || StolenFlagDropFor(b) != null)
                    continue;
                foreach (Arena.FlagState fs in StealableFlags(r).ToList())
                {
                    if (Helpers.distanceTo(b._state.positionX, b._state.positionY, fs.posX, fs.posY) > RaidStealRangeRaw)
                        continue;
                    if (!_arena.flagClaimByBot(b, fs, true))
                        continue;
                    Helpers.ObjectState drop = PickRaidDropSpot(r.Base);
                    _stolen[fs] = new StolenFlag { Fs = fs, Carrier = b, From = r.Target, Drop = drop, Since = now, LastSend = now, SentX = fs.posX, SentY = fs.posY };
                    string name = InfServer.Game.Modules.BotNameModule.GetDisplayName(b);
                    r.Target.sendArenaMessage(string.Format("&Raid - {0} grabbed your {1}! Stop them!", name, fs.flag.GeneralData.Name), _config.flag.periodicBong);
                    Log.write(TLog.Normal, "[Eol] Base raid: {0} ({1}) stole {2} from {3}, taking it to {4}.", name, b._team._name,
                        fs.flag.GeneralData.Name, r.Target._name, Helpers.posToLetterCoord(drop.positionX, drop.positionY));
                    break;
                }
            }
        }

        /// <summary>Stolen flags move with their thief; at the drop spot (or the thief down, or too long) they're left there.</summary>
        void PollStolenFlags(int now)
        {
            if (_stolen.Count == 0)
                return;
            foreach (StolenFlag s in _stolen.Values.ToList())
            {
                Arena.FlagState fs = s.Fs;
                if (fs == null || !fs.bActive || fs.carrier != null)
                {   // a player took it back (or it reset)
                    _stolen.Remove(fs);
                    continue;
                }
                Bot b = s.Carrier;
                string where;
                if (b == null || b.IsDead || b.bCondemned || b._state == null)
                {   // the thief is down: the flag lies where it fell
                    _stolen.Remove(fs);
                    SendFlagState(fs);
                    where = Helpers.posToLetterCoord(fs.posX, fs.posY);
                    s.From?.sendArenaMessage(string.Format("&Raid - the thief is down: your {0} lies at {1}.", fs.flag.GeneralData.Name, where), 0);
                    continue;
                }
                if (fs.team != b._team)
                {   // someone else claimed it meanwhile
                    _stolen.Remove(fs);
                    continue;
                }
                fs.posX = b._state.positionX; fs.posY = b._state.positionY;
                bool there = Helpers.distanceTo(b._state, s.Drop) <= 160, tooLong = now - s.Since > RaidStolenMaxMs;
                if (there || tooLong)
                {
                    if (there) { fs.posX = s.Drop.positionX; fs.posY = s.Drop.positionY; }
                    _stolen.Remove(fs);
                    SendFlagState(fs);
                    where = Helpers.posToLetterCoord(fs.posX, fs.posY);
                    s.From?.sendArenaMessage(string.Format("&Raid - your {0} was dumped at {1}. Go get it back!", fs.flag.GeneralData.Name, where), 0);
                    Log.write(TLog.Normal, "[Eol] Base raid: {0} dropped at {1}.", fs.flag.GeneralData.Name, where);
                    continue;
                }
                // (the moving flag: sent every 0.7 s when it has moved)
                int dx = fs.posX - s.SentX, dy = fs.posY - s.SentY;
                if (now - s.LastSend >= 700 && dx * dx + dy * dy > 16 * 16)
                {
                    s.LastSend = now; s.SentX = fs.posX; s.SentY = fs.posY;
                    SendFlagState(fs);
                }
            }
        }

        void SendFlagState(Arena.FlagState fs)
        {
            fs.oldPosX = fs.posX; fs.oldPosY = fs.posY;
            Helpers.Object_Flags(_arena.Players, fs);
        }

        /// <summary>Open ground in play, 3,000+ px from the raided base (near another base, flag or point - anywhere else).</summary>
        Helpers.ObjectState PickRaidDropSpot(Helpers.ObjectState hq)
        {
            Random r = _rand ?? (_rand = new Random(Environment.TickCount));
            List<Helpers.ObjectState> spots = new List<Helpers.ObjectState>();
            if (_arena._flags != null)
                foreach (Arena.FlagState f in _arena._flags.Values)
                    if (f != null && f.bActive && f.carrier == null)
                        spots.Add(new Helpers.ObjectState { positionX = f.posX, positionY = f.posY });
            foreach (Vehicle v in _arena.Vehicles)
                if (v != null && !v.IsDead && v._type != null && IsHqVehId(v._type.Id) && v._state != null)
                    spots.Add(v._state);
            for (int tries = 0; tries < 40 && spots.Count > 0; tries++)
            {
                Helpers.ObjectState c = spots[r.Next(spots.Count)];
                double a = r.NextDouble() * 2 * Math.PI; int off = 150 + r.Next(350);
                short x = (short)Math.Max(64, Math.Min(short.MaxValue - 64, c.positionX + Math.Cos(a) * off));
                short y = (short)Math.Max(64, Math.Min(short.MaxValue - 64, c.positionY + Math.Sin(a) * off));
                if (Helpers.distanceTo(x, y, hq.positionX, hq.positionY) < RaidDropMinFromBaseRaw || NearNewPlayerBuffer(x, y)
                    || (_eol != null && _eol.IsInPioneerDropshipSafeZone(x, y)))
                    continue;
                Helpers.ObjectState at = OpenSpotNear(x, y, 120);
                if (at != null && Helpers.distanceTo(at.positionX, at.positionY, hq.positionX, hq.positionY) >= RaidDropMinFromBaseRaw)
                    return at;
            }
            // nothing suitable: straight away from the base, as far as the play area goes
            for (int dist = 4000; dist >= RaidDropMinFromBaseRaw; dist -= 250)
                for (int k = 0; k < 8; k++)
                {
                    double a = r.NextDouble() * 2 * Math.PI;
                    short x = (short)Math.Max(64, Math.Min(short.MaxValue - 64, hq.positionX + Math.Cos(a) * dist));
                    short y = (short)Math.Max(64, Math.Min(short.MaxValue - 64, hq.positionY + Math.Sin(a) * dist));
                    if (NearNewPlayerBuffer(x, y))
                        continue;
                    Helpers.ObjectState at = OpenSpotNear(x, y, 160);
                    if (at != null)
                        return at;
                }
            return new Helpers.ObjectState { positionX = hq.positionX, positionY = hq.positionY };
        }

        /// <summary>The raided bases as extra points of interest for every roaming bot (twice per roamer flag held).</summary>
        List<KeyValuePair<short, short>> RaidPointsOfInterest()
        {
            List<KeyValuePair<short, short>> list = new List<KeyValuePair<short, short>>();
            foreach (BaseRaid r in _raids.Values)
                if (r.Base != null)
                    for (int i = 0; i < r.Flags * 2; i++)
                        list.Add(new KeyValuePair<short, short>(r.Base.positionX, r.Base.positionY));
            return list;
        }
    }
}
