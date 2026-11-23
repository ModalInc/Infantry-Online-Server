using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-17, owner: "choose what class you want in the team of 6. but have the must have classes medic, engy and
    /// command ... change squad jumptrooper, sciops and it will sub out the two classes for those") players on a human
    /// squad pick the squad's other classes in team chat: "change squad jumptrooper, sciops".
    ///   - the medic, the Chief Engineer and the commander always stay; free places = 6 - players - those 3
    ///     (1 player: 2, 2 players: 1);
    ///   - no class twice in one pick (owner: "you can't pick the same class twice");
    ///   - every player on the squad must be in the dropship; "change squad" itself brings the bots that stay to the
    ///     player there (owner: "if i type squad change, that summons the squad to me in the dropship instead of @");
    ///   - once every 10 minutes per squad (owner: "to avoid overpower").
    /// Bots of other fighting classes are sent off and the picked classes come in a few seconds apart. The pick lasts
    /// while players are on the squad; an empty squad goes back to random classes.
    /// </summary>
    public partial class Script_Eol
    {
        const int SquadCoreCount = 3;                     // commander, medic, Chief Engineer
        const int SquadChangeCooldownMs = 10 * 60 * 1000;
        const int SquadSwapSpawnMs = 2500;                // picked classes come in this far apart (not the usual 20 s)

        readonly Dictionary<Team, List<int>> _squadChosen = new Dictionary<Team, List<int>>();
        readonly Dictionary<Team, int> _squadChangeNext = new Dictionary<Team, int>();
        readonly Dictionary<Team, int> _squadSwapLeft = new Dictionary<Team, int>();

        const string SquadClassList = "Infantry, Heavy Weapons, Marine, Jump Trooper, Drop Trooper, Infiltrator, BioChem Trooper, MPAC, NeuroTAC, SciOps, BlackOps";

        /// <summary>(owner: "make sure they type full name of class how it is in f11") the F11 names only - capitals and
        /// spaces don't matter ("Jump Trooper" = "jumptrooper"), no short forms.</summary>
        static readonly Dictionary<string, int> SquadClassNames = new Dictionary<string, int>
        {
            { "jumptrooper", ClsJumpTrooper }, { "infantry", ClsInfantry }, { "heavyweapons", ClsHeavy }, { "marine", ClsMarine },
            { "infiltrator", ClsInfiltrator }, { "biochem", ClsBioChem }, { "mpac", ClsMPAC }, { "neurotac", ClsNeuroTAC },
            { "sciops", ClsSciOps }, { "blackops", ClsBlackOps },
            // (2026-11-23, owner: "Drop Trooper can't be found when you change squad") Drop Trooper, and BioChem by its full F11 name
            { "droptrooper", ClsDropTrooper }, { "biochemtrooper", ClsBioChem },
        };
        /// <summary>The core classes, so "change squad medic" gets a helpful answer.</summary>
        static readonly HashSet<string> SquadCoreNames = new HashSet<string>
        { "combatmedic", "medic", "fieldsurgeon", "chiefengineer", "combatengineer", "engineer", "squadleader", "sergeant", "lieutenant", "captain", "commander" };

        static string SquadClassDisplay(int cls)
        {
            switch (cls)
            {
                case ClsJumpTrooper: return "Jump Trooper";
                case ClsDropTrooper: return "Drop Trooper";
                case ClsInfantry: return "Infantry";
                case ClsHeavy: return "Heavy Weapons";
                case ClsMarine: return "Marine";
                case ClsInfiltrator: return "Infiltrator";
                case ClsBioChem: return "BioChem Trooper";
                case ClsMPAC: return "MPAC";
                case ClsNeuroTAC: return "NeuroTAC";
                case ClsSciOps: return "SciOps";
                case ClsBlackOps: return "BlackOps";
                default: return "?";
            }
        }

        /// <summary>"change squad ..." typed in team chat (lower case, trimmed)?</summary>
        static bool IsChangeSquadText(string t)
        {
            return t.StartsWith("change squad") || t.StartsWith("squad change");
        }

        /// <summary>The class names after "change squad": comma separated, or space separated (two-word names joined).</summary>
        static List<string> SplitClassNames(string rest, out List<string> unknown)
        {
            unknown = new List<string>();
            List<string> found = new List<string>();
            Func<string, string> norm = x => x.Replace(" ", "").Replace("-", "").Replace(".", "").Trim();
            Func<string, bool> known = n => SquadClassNames.ContainsKey(n) || SquadCoreNames.Contains(n);
            if (rest.Contains(",") || rest.Contains("/") || rest.Contains("&"))
            {
                foreach (string part in rest.Split(new[] { ',', '/', '&' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string n = norm(part.Replace(" and ", " "));
                    if (n.Length == 0) continue;
                    if (known(n)) found.Add(n); else unknown.Add(part.Trim());
                }
                return found;
            }
            string[] w = rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < w.Length; i++)
            {
                if (w[i] == "and") continue;
                string two = i + 1 < w.Length ? norm(w[i] + w[i + 1]) : null;
                if (two != null && known(two)) { found.Add(two); i++; continue; }
                string one = norm(w[i]);
                if (known(one)) found.Add(one); else unknown.Add(w[i]);
            }
            return found;
        }

        /// <summary>Free places for picked classes: 6 - players - the 3 core roles.</summary>
        int SquadPickSlots(Team team)
        {
            return Math.Max(0, SquadMaxWithPlayers - SquadHumans(team) - SquadCoreCount);
        }

        /// <summary>The picked classes still in force for this squad (null = random). Cleared once no players are on it.</summary>
        List<int> SquadChosenFor(Team team)
        {
            List<int> c;
            if (team == null || !_squadChosen.TryGetValue(team, out c))
                return null;
            if (SquadHumans(team) == 0)
            {
                _squadChosen.Remove(team); _squadSwapLeft.Remove(team);
                return null;
            }
            return c;
        }

        bool IsCoreSquadRole(Bots.Bot b)
        {
            if (b == null || b._type == null) return false;
            int id = b._type.Id;
            return IsSquadLeaderVehicle(id) || id == ClsMedic || id == ClsFieldSurgeon || id == ClsChiefEngineer || id == ClsEngineer;
        }

        /// <summary>The squad's PLAYERS not in the dropship (the bots are brought in by "change squad" itself).</summary>
        List<string> SquadPlayersOutsideDropship(Team team)
        {
            List<string> outside = new List<string>();
            if (_eol == null)
                return outside;
            foreach (InfServer.Game.Player p in _arena.PlayersIngame)
                if (p != null && p._team == team && !p.IsSpectator && p._state != null
                    && (p.IsDead || !_eol.IsInPioneerDropshipSafeZone(p._state.positionX, p._state.positionY)))
                    outside.Add(p._alias);
            return outside;
        }

        /// <summary>(2026-11-17, owner: "if i type squad change, that summons the squad to me in the dropship instead of @")
        /// the bots that stay come to the player in the dropship at once and wait there with them (like "@").</summary>
        int SummonSquadIntoDropship(Team team, InfServer.Game.Player player, List<RoamingAttacker> staying, int now)
        {
            int moved = 0;
            foreach (RoamingAttacker b in staying)
            {
                if (b == null || b.IsDead || b.bCondemned || b._state == null)
                    continue;
                if (!_eol.IsInPioneerDropshipSafeZone(b._state.positionX, b._state.positionY))
                {
                    Helpers.ObjectState at = SummonSpotInSafe(player._state);
                    _deployStartTick.Remove(b);
                    WarpBot(b, at);
                    b.DamageImmuneUntilTick = NonZeroTick(now + 1500);
                    moved++;
                }
                _shipSummoned[b] = player;   // (waits round the player until they leave the dropship, then walks out)
            }
            return moved;
        }

        /// <summary>Arena thread: a player typed "change squad ...".</summary>
        void HandleChangeSquad(InfServer.Game.Player player, string text)
        {
            if (player == null || player.IsSpectator || _arena == null)
                return;
            Team team = player._team;
            if (!IsJoinableSquadTeam(team))
                return;
            if (!IsStrikeTeam(team))
            {
                player.sendMessage(-1, "Squad: this squad's classes are fixed - only the human squads can change theirs.");
                return;
            }
            int now = Environment.TickCount;
            string t = text.Trim().ToLowerInvariant();
            string rest = t.Substring("change squad".Length).Trim().TrimStart(':', '-').Trim();
            int slots = SquadPickSlots(team);
            RoamingAttacker lead = EliteSquadLeader(team);
            string leaderName = lead != null ? lead._alias : team._name;
            if (rest.Length == 0)
            {   // (bare "change squad" in the dropship: the squad comes to you there - pick the classes once they're in)
                if (_eol != null && player._state != null && !player.IsDead && _eol.IsInPioneerDropshipSafeZone(player._state.positionX, player._state.positionY))
                {
                    int comingIn = SummonSquadIntoDropship(team, player, EliteSquadMembers(team), now);
                    if (comingIn > 0)
                        Helpers.Player_RouteChatRaw(team, leaderName, "Coming to you in the dropship", 0, Helpers.Chat_Type.Team);
                }
                player.sendMessage(0, String.Format("&Squad: in the dropship say e.g. \"change squad Jump Trooper, SciOps\" - {0} free place{1}, no class twice. Classes: {2}",
                    slots, slots == 1 ? "" : "s", SquadClassList));
                return;
            }
            int next;
            if (_squadChangeNext.TryGetValue(team, out next) && now - next < 0)
            {
                int s = (next - now + 999) / 1000;
                player.sendMessage(-1, String.Format("Squad: the squad can change again in {0}:{1:00}.", s / 60, s % 60));
                return;
            }
            if (_eol == null || player._state == null || !_eol.IsInPioneerDropshipSafeZone(player._state.positionX, player._state.positionY))
            {
                player.sendMessage(-1, "Squad: go to the dropship to change squad - the bots come to you there.");
                return;
            }
            List<string> outside = SquadPlayersOutsideDropship(team);
            if (outside.Count > 0)
            {
                player.sendMessage(-1, String.Format("Squad: every player on the squad has to be in the dropship to change. Still out: {0}",
                    string.Join(", ", outside.Count > 6 ? outside.GetRange(0, 6) : outside)));
                return;
            }
            if (slots == 0)
            {
                player.sendMessage(-1, "Squad: no free places - with this many players it's just the medic, engineer and commander.");
                return;
            }
            List<string> unknown;
            List<string> names = SplitClassNames(rest, out unknown);
            List<int> picks = new List<int>();
            bool askedCore = false, twice = false;
            foreach (string n in names)
            {
                int cls;
                if (!SquadClassNames.TryGetValue(n, out cls)) { askedCore = true; continue; }
                if (picks.Contains(cls)) twice = true; else picks.Add(cls);
            }
            if (twice)
            {
                player.sendMessage(-1, "Squad: you can't pick the same class twice - pick different classes.");
                return;
            }
            if (picks.Count == 0)
            {
                player.sendMessage(-1, (askedCore ? "Squad: the medic, engineer and commander are always in the squad - pick from: "
                    : "Squad: type the class name as it is in F11 - pick from: ") + SquadClassList);
                return;
            }
            if (unknown.Count > 0)
            {
                player.sendMessage(-1, "Squad: no class called " + string.Join(", ", unknown) + " - type the name as it is in F11: " + SquadClassList);
                return;
            }
            if (picks.Count > slots)
            {
                player.sendMessage(-1, String.Format("Squad: only {0} free place{1} with {2} player{3} - pick {0}.",
                    slots, slots == 1 ? "" : "s", SquadHumans(team), SquadHumans(team) == 1 ? "" : "s"));
                return;
            }
            _squadChosen[team] = picks;
            _squadChangeNext[team] = now + SquadChangeCooldownMs;

            // send off the fighters that weren't picked (one of each picked class already here stays; the core roles stay)
            List<RoamingAttacker> members = EliteSquadMembers(team);
            List<int> kept = new List<int>();
            List<RoamingAttacker> staying = new List<RoamingAttacker>();
            int sent = 0;
            foreach (RoamingAttacker m in members)
            {
                if (m == null || m.IsDead || m._type == null)
                    continue;
                if (IsCoreSquadRole(m) || picks.Contains(m._type.Id) && !kept.Contains(m._type.Id))
                {
                    if (!IsCoreSquadRole(m)) kept.Add(m._type.Id);
                    staying.Add(m);
                    continue;
                }
                EnqueueVehicleDespawn(m, force: true);
                if (_bots != null) _bots.Remove(m);
                sent++;
            }
            int brought = SummonSquadIntoDropship(team, player, staying, now);
            if (roamBots != null)
                roamBots[team] = EliteSquadMembers(team).Count;
            int coming = picks.Count - kept.Count;
            _squadSwapLeft[team] = coming;
            _strikeNextSpawnTick[team] = now + SquadSwapSpawnMs;

            List<string> shown = picks.ConvertAll(SquadClassDisplay);
            Helpers.Player_RouteChatRaw(team, leaderName,
                String.Format("Copy - squad is medic, engineer, command + {0}{1}", string.Join(", ", shown),
                    coming > 0 ? " - on their way" : ""), 0, Helpers.Chat_Type.Team);
            Log.write(TLog.Normal, "[Eol] {0} changed {1} to {2} ({3} sent off, {4} brought into the dropship).", player._alias, team._name, string.Join(", ", shown), sent, brought);
        }

        /// <summary>The next picked class the squad doesn't have yet (0 = none picked / all in).</summary>
        int NextChosenClass(Team team, List<RoamingAttacker> members)
        {
            List<int> picks = SquadChosenFor(team);
            if (picks == null)
                return 0;
            foreach (int c in picks)
                if (!members.Exists(m => m != null && !m.IsDead && m._type != null && m._type.Id == c))
                    return c;
            return 0;
        }

        /// <summary>Delay before this squad's next bot: quick while picked classes are still coming in.</summary>
        int SquadRefillDelay(Team team)
        {
            int left;
            return team != null && _squadSwapLeft.TryGetValue(team, out left) && left > 0 && SquadChosenFor(team) != null
                ? SquadSwapSpawnMs : StrikeRespawnDelayMs;
        }

        void NoteSquadSwapSpawned(Team team)
        {
            int left;
            if (team != null && _squadSwapLeft.TryGetValue(team, out left) && left > 0)
                _squadSwapLeft[team] = left - 1;
        }

        /// <summary>A picked class stays longer than a random fighter when players take places (SquadKeepRank).</summary>
        bool IsChosenClass(Bots.Bot b)
        {
            List<int> picks = b != null ? SquadChosenFor(b._team) : null;
            return picks != null && b._type != null && picks.Contains(b._type.Id);
        }
    }
}
