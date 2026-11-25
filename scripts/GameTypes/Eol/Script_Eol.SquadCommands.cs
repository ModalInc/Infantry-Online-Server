using System;
using System.Collections.Generic;

using InfServer.Bots;
using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// (2026-11-16b, owner) Team-chat orders for a player who joined a bot squad - the usual Infantry calls:
    ///   S / SSS   summon me home: you are suit-teleported to your squad (beside its leader);
    ///   X / XXX   summon me to the enemy: you go to the squad bot nearest the fight (or nearest an enemy base);
    ///   @ (or "@ me")  the squad's bots suit-teleport to you, one after the other.
    ///   H         the squad's medics come to you and heal you (far ones suit-teleport).
    ///   follow    the squad loosely follows you (a good angle on a base); "stop" = back to attacking bases.
    /// Heard from team chat only (like CTF:X), 10 s apart per player, never into a closed sector.
    /// (2026-11-17, owner) S / X summon you after 3 s; @ brings the bots after 2 s and only every 30 s.
    /// </summary>
    public partial class Script_Eol
    {
        const int SquadOrderCooldownMs = 10000;
        const int SquadSummonDelayMs = 3000;      // S / X: you go after 3 s
        const int SquadToMeDelayMs = 2000;        // @: the first bot arrives after 2 s
        const int SquadToMeCooldownMs = 45000;    // @ / @ home: once every 45 s (2026-11-24, owner: was 30 s)
        const int SquadSummonMeCooldownMs = 45000; // S / X: you can be summoned by the squad once every 45 s (2026-11-24, owner)
        const int SquadHealCallMs = 20000;        // H: the medics stay on you this long (or until you're full)
        const int SquadHealWarpBeyond = 900;      // H: a medic further away than this suit-teleports to you
        const int SquadFollowMs = 10 * 60 * 1000; // follow: up to 10 min

        readonly Dictionary<InfServer.Game.Player, int> _squadToMeNext = new Dictionary<InfServer.Game.Player, int>();
        readonly Dictionary<InfServer.Game.Player, int> _squadSummonMeNext = new Dictionary<InfServer.Game.Player, int>();

        /// <summary>(2026-11-24, owner: "if anyone is under a teleport disrupt they cant be summoned by bots") within an enemy
        /// teleport disruptor (a player's, a bot's or a turret's anti-warp).</summary>
        bool UnderTeleportDisrupt(InfServer.Game.Player p)
        {
            ScriptArena sa = _arena as ScriptArena;
            return p != null && sa != null && sa.isTeleportDisrupted(p);
        }

        struct PendingSummon
        {
            public InfServer.Game.Player Player;
            public int Order;
            public int Due;
        }
        readonly List<PendingSummon> _pendingSummons = new List<PendingSummon>();

        class SquadCall { public InfServer.Game.Player Player; public int Until; public int HealsLeft; public short LastX, LastY; public bool HasLast; }
        /// <summary>(2026-11-17, owner: "not overpowered ... only a few") an H call gives the caller this many heals.</summary>
        public const int SquadHealCallHeals = 3;
        readonly Dictionary<Team, SquadCall> _squadHealCall = new Dictionary<Team, SquadCall>();
        readonly Dictionary<Team, SquadCall> _squadFollow = new Dictionary<Team, SquadCall>();

        /// <summary>The player this squad's medics were called to with H (null = none, or done).</summary>
        public InfServer.Game.Player SquadHealCaller(Team t, int now)
        {
            SquadCall c;
            if (t == null || !_squadHealCall.TryGetValue(t, out c))
                return null;
            InfServer.Game.Player p = c.Player;
            if (now - c.Until >= 0 || p == null || p.IsDead || p.IsSpectator || p._team != t || p._state == null
                || p._state.health >= PlayerMaxHealth(p))
            {
                _squadHealCall.Remove(t);
                return null;
            }
            return p;
        }

        /// <summary>The player this squad follows ("follow"), or null.</summary>
        public InfServer.Game.Player SquadFollowTarget(Team t, int now)
        {
            SquadCall c;
            if (t == null || !_squadFollow.TryGetValue(t, out c))
                return null;
            InfServer.Game.Player p = c.Player;
            if (now - c.Until >= 0 || p == null || p._team != t || p.IsSpectator)
            {
                _squadFollow.Remove(t);
                return null;
            }
            // (dead or back in the dropship: the bots carry on round where they are until the player is out again)
            if (p.IsDead || p._state == null || !InPlayArea(p._state.positionX, p._state.positionY, 0)
                || _eol != null && _eol.IsInPioneerDropshipSafeZone(p._state.positionX, p._state.positionY))
                return null;
            c.LastX = p._state.positionX; c.LastY = p._state.positionY; c.HasLast = true;
            return p;
        }

        /// <summary>
        /// (2026-11-24, owner: "follow is basically YOU are the area that the bots follow ... instead of forgetting about the
        /// follow and going off elsewhere") the follow order is still on but the player is dead / in the dropship for now:
        /// the squad waits round the player's last spot instead of marching off on its own objectives.
        /// </summary>
        public Helpers.ObjectState SquadFollowWaitSpot(Team t, int now)
        {
            SquadCall c;
            if (t == null || !_squadFollow.TryGetValue(t, out c) || !c.HasLast)
                return null;
            if (now - c.Until >= 0 || c.Player == null || c.Player._team != t || c.Player.IsSpectator)
                return null;
            return new Helpers.ObjectState { positionX = c.LastX, positionY = c.LastY };
        }
        static Script_Eol _chatScript;
        static bool _chatHooked;

        readonly Dictionary<InfServer.Game.Player, int> _squadOrderNext = new Dictionary<InfServer.Game.Player, int>();

        struct PendingSuitWarp
        {
            public Bots.Bot Bot;
            public InfServer.Game.Player To;
            public int Due;
        }
        readonly List<PendingSuitWarp> _pendingSuitWarps = new List<PendingSuitWarp>();

        /// <summary>Hook team chat once (called from poll, so it survives a script reload).</summary>
        void EnsureSquadChatHook()
        {
            _chatScript = this;
            if (_chatHooked)
                return;
            _chatHooked = true;
            CS_Chat.Handlers += OnSquadChat;
        }

        /// <summary>Network thread: only picks out team chat of squad players and hands it to the arena thread.</summary>
        static void OnSquadChat(CS_Chat pkt, InfServer.Game.Player player)
        {
            try
            {
                Script_Eol s = _chatScript;
                if (s == null || pkt == null || player == null || pkt.chatType != Helpers.Chat_Type.Team)
                    return;
                if (s._arena == null || player._arena != s._arena)
                    return;
                string low = pkt.message != null ? pkt.message.Trim().ToLowerInvariant() : "";
                if (IsChangeSquadText(low))
                {   // (2026-11-17) "change squad Jump Trooper, SciOps" (Script_Eol.SquadClasses.cs)
                    string text = pkt.message;
                    player._arena.handleEvent(delegate (Arena a)
                    {
                        try { if (_chatScript == s) s.HandleChangeSquad(player, text); }
                        catch (Exception ex) { Log.write(TLog.Warning, "[Eol] change squad: " + ex.Message); }
                    });
                    return;
                }
                int order = ParseSquadOrder(pkt.message);
                if (order == 0)
                    return;
                player._arena.handleEvent(delegate (Arena a)
                {
                    try { if (_chatScript == s) s.HandleSquadOrder(player, order); }
                    catch (Exception ex) { Log.write(TLog.Warning, "[Eol] squad order: " + ex.Message); }
                });
            }
            catch (Exception) { }
        }

        const int OrderHome = 1, OrderEnemy = 2, OrderToMe = 3, OrderDefendHere = 4, OrderHeal = 5, OrderFollow = 6, OrderStop = 7, OrderHelp = 8;
        /// <summary>"@ home": the squad holds the spot it was called to this long (then back to attacking bases).
        /// (2026-11-24, owner: "@home ... should stay at home in the area you summoned them to defend") 5 -> 10 min.</summary>
        const int SquadDefendMs = 10 * 60 * 1000;

        class SquadDefend { public short X, Y; public int Until; }
        readonly Dictionary<Team, SquadDefend> _squadDefend = new Dictionary<Team, SquadDefend>();

        /// <summary>The spot this squad was told to defend ("@ home"), or null.</summary>
        public Helpers.ObjectState SquadDefendPoint(Team t, int now)
        {
            SquadDefend d;
            if (t == null || !_squadDefend.TryGetValue(t, out d))
                return null;
            if (now - d.Until >= 0 || SquadHumans(t) == 0)
            {
                _squadDefend.Remove(t);
                return null;
            }
            return new Helpers.ObjectState { positionX = d.X, positionY = d.Y };
        }

        /// <summary>S.. = home, X.. = enemy, @ / "@ me" = bots to me; anything else = 0.</summary>
        static int ParseSquadOrder(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg))
                return 0;
            string t = msg.Trim().ToLowerInvariant();
            if (t.StartsWith("@"))
            {
                string rest = t.Substring(1).Trim();
                if (rest == "home" || rest == "base" || rest == "def" || rest == "defend")
                    return OrderDefendHere;
                return rest.Length == 0 || rest == "me" || rest == "nme" || rest == "pls" ? OrderToMe : 0;
            }
            if (OnlyLetter(t, 's') || t == "sum home" || t == "summon home" || t == "s me")
                return OrderHome;
            if (OnlyLetter(t, 'x') || t == "sum nme" || t == "summon nme" || t == "sum to nme" || t == "x me")
                return OrderEnemy;
            if (OnlyLetter(t, 'h') || t == "heal" || t == "heal me" || t == "h me" || t == "h pls" || t == "medic")
                return OrderHeal;
            if (t == "follow" || t == "follow me" || t == "fm" || t == "f me")
                return OrderFollow;
            if (t == "stop" || t == "unfollow" || t == "stop follow" || t == "stop following")
                return OrderStop;
            // (2026-11-17, owner: "if someone types help in team chat, the bots say the message again")
            if (t == "help" || t == "commands" || t == "cmds" || t == "?" || t == "help pls")
                return OrderHelp;
            int build = ParseBuildOrder(t);   // (2026-11-17) "mg here" ... "hq here" for the squad's Chief Engineer
            if (build != 0)
                return build;
            return 0;
        }

        static bool OnlyLetter(string t, char c)
        {
            int n = 0;
            foreach (char ch in t)
            {
                if (ch == ' ')
                    continue;
                if (ch != c)
                    return false;
                n++;
            }
            return n > 0 && n <= 12;
        }

        void HandleSquadOrder(InfServer.Game.Player player, int order)
        {
            if (player == null || player.IsDead || player.IsSpectator || _arena == null)
                return;
            Team team = player._team;
            if (!IsJoinableSquadTeam(team))
                return;
            int now = Environment.TickCount;
            int next;

            // stop / follow need no cooldown (they cost nothing)
            if (order >= OrderRemoveBase)
            {   // (2026-11-17) "remove mg" ... : the engineer dismantles the nearest one
                HandleRemoveOrder(player, order - OrderRemoveBase);
                return;
            }
            if (order >= OrderBuildBase)
            {   // (2026-11-17) engineer build orders have their own 2-minute timer (Script_Eol.EngineerOrders.cs)
                HandleBuildOrder(player, order - OrderBuildBase);
                return;
            }
            if (order == OrderHelp)
            {   // the squad leader repeats how the squad can support you (once per 10 s per team - it's 4 lines)
                int hn;
                if (_squadHelpNext.TryGetValue(team, out hn) && now - hn < 0)
                    return;
                _squadHelpNext[team] = now + 10000;
                QueueSquadGreeting(team, now, helpOnly: true);
                return;
            }
            if (order == OrderStop)
            {
                bool was = _squadFollow.Remove(team) | _squadDefend.Remove(team);
                player.sendMessage(0, was ? "&Squad: stopped following - back to our objectives." : "&Squad: we're already on our objectives.");
                return;
            }
            if (order == OrderFollow)
            {
                if (EliteSquadMembers(team).Count == 0)
                {
                    player.sendMessage(-1, "Squad: nobody left to follow you.");
                    return;
                }
                _squadFollow[team] = new SquadCall { Player = player, Until = now + SquadFollowMs };
                _squadDefend.Remove(team);
                player.sendMessage(0, "&Squad: following you (10 min). Say stop to send us back on the attack.");
                RoamingAttacker l = EliteSquadLeader(team);
                if (l != null)
                    SquadSay(l, Pick3("on you", "right behind you", "lead the way"));
                return;
            }

            if (_squadOrderNext.TryGetValue(player, out next) && now - next < 0)
            {
                player.sendMessage(-1, String.Format("Squad: wait {0} s.", (next - now + 999) / 1000));
                return;
            }

            bool inField = !(_eol != null && _eol.IsInPioneerDropshipSafeZone(player._state.positionX, player._state.positionY))
                && InPlayArea(player._state.positionX, player._state.positionY, 0);
            // (2026-11-17, owner: "i meant the g6 zone ... that new player friendly zone ... the green area") "@" works from
            // the new player zone too: the bots come to you there and stay round you until you leave it
            bool onSafe = InSummonSafeZone(player._state.positionX, player._state.positionY);

            if (order == OrderHeal)
            {
                if (!inField)
                {
                    player.sendMessage(-1, "Squad: get out into the field first.");
                    return;
                }
                List<RoamingAttacker> medics = EliteSquadMembers(team).FindAll(b => IsSquadMedic(b) && !b.IsDead);
                if (medics.Count == 0)
                {
                    player.sendMessage(-1, "Squad: no medic in the squad right now.");
                    return;
                }
                _squadHealCall[team] = new SquadCall { Player = player, Until = now + SquadHealCallMs, HealsLeft = SquadHealCallHeals };
                foreach (RoamingAttacker m in medics)
                    _medicNextPulse.Remove(m);   // (2026-11-17: heals as soon as it's there - no waiting out its last pulse)
                int warped = 0;
                foreach (RoamingAttacker m in medics)
                    if (IsDeploying(m) || Helpers.distanceTo(m._state, player._state) > SquadHealWarpBeyond)
                        _pendingSuitWarps.Add(new PendingSuitWarp { Bot = m, To = player, Due = now + SquadToMeDelayMs + warped++ * 450 });
                _squadOrderNext[player] = now + SquadOrderCooldownMs;
                player.sendMessage(0, String.Format("&Squad: {0} on the way to heal you.", medics.Count == 1 ? "medic" : medics.Count + " medics"));
                SquadSay(medics[0], Pick3("medic coming", "hold on, healing you", "omw, stay alive"));
                return;
            }

            if (order == OrderToMe || order == OrderDefendHere)
            {
                // (2026-11-17, owner: "allow team members to summon bots into the safe zone") "@" also works from the
                // dropship - the bots wait round you there and walk out through the portal when you leave
                if (!inField && !onSafe && order == OrderDefendHere)
                {
                    player.sendMessage(-1, "Squad: get out into the field first.");
                    return;
                }
                int tnext;
                if (_squadToMeNext.TryGetValue(player, out tnext) && now - tnext < 0)
                {
                    player.sendMessage(-1, String.Format("Squad: we can come to you again in {0} s.", (tnext - now + 999) / 1000));
                    return;
                }
                List<RoamingAttacker> all = EliteSquadMembers(team);
                if (all.Count == 0)
                {
                    player.sendMessage(-1, "Squad: nobody left to send.");
                    return;
                }
                int i = 0;
                foreach (RoamingAttacker b in all)
                {
                    if (Helpers.distanceTo(b._state, player._state) < 300)
                        continue;   // already with you
                    // (2026-11-17, owner: "2 second delay for bots to teleport to you")
                    _pendingSuitWarps.Add(new PendingSuitWarp { Bot = b, To = player, Due = now + SquadToMeDelayMs + i * 450 + _arena._rand.Next(300) });
                    i++;
                }
                _squadOrderNext[player] = now + SquadOrderCooldownMs;
                _squadToMeNext[player] = now + SquadToMeCooldownMs;   // (owner: "only every 30 seconds to avoid spam")
                _squadFollow.Remove(team);
                if (order == OrderDefendHere)
                {   // hold this spot: fight round it instead of marching on enemy bases
                    _squadDefend[team] = new SquadDefend { X = player._state.positionX, Y = player._state.positionY, Until = now + SquadDefendMs };
                    player.sendMessage(0, i == 0 ? "&Squad: we're with you - guarding this area for 10 min. Say stop to send us back on the attack."
                        : String.Format("&Squad: {0} suit-teleporting to you in 2 s - guarding this area for 10 min.", i));
                    SquadSay(all[0], Pick3("holding here", "we got the base", "defending, nothing gets through"));
                    return;
                }
                _squadDefend.Remove(team);   // (a plain @: back on the move with you)
                player.sendMessage(0, i == 0 ? "&Squad: we're with you - attacking with you." : String.Format("&Squad: {0} suit-teleporting to you in 2 s - attacking with you.", i));
                if (i > 0)
                    SquadSay(all[0], Pick3("omw", "coming to you", "on you in a sec"));
                return;
            }

            // S / X: (2026-11-17, owner: "a 3 second delay for someone to get summoned by a bot, not instant")
            int snext;
            if (_squadSummonMeNext.TryGetValue(player, out snext) && now - snext < 0)
            {
                player.sendMessage(-1, String.Format("Squad: we can summon you again in {0} s.", (snext - now + 999) / 1000));
                return;
            }
            if (UnderTeleportDisrupt(player))
            {
                player.sendMessage(-1, "Squad: you're inside a teleport disruptor - we can't summon you.");
                return;
            }
            if (CarriesNoSummonFlag(player))
            {   // (2026-11-26) a flag goes nowhere except by HQ recall
                player.sendMessage(-1, "Squad: you can't be summoned while you carry a flag.");
                return;
            }
            if (SummonTarget(team, order) == null)
            {
                player.sendMessage(-1, "Squad: nobody out in the field to summon you.");
                return;
            }
            _pendingSummons.RemoveAll(s => s.Player == player);
            _pendingSummons.Add(new PendingSummon { Player = player, Order = order, Due = now + SquadSummonDelayMs });
            _squadOrderNext[player] = now + SquadOrderCooldownMs;
            _squadSummonMeNext[player] = now + SquadSummonMeCooldownMs;
            player.sendMessage(0, "&Squad: summoning you in 3 s...");
        }

        /// <summary>Who an S (the leader) or X (the bot nearest the fight / an enemy base) summons you to.</summary>
        RoamingAttacker SummonTarget(Team team, int order)
        {
            List<RoamingAttacker> squad = EliteSquadMembers(team).FindAll(b => !b.IsDead && !IsDeploying(b) && InPlayArea(b._state.positionX, b._state.positionY, 0));
            RoamingAttacker to = null;
            if (order == OrderHome)
            {
                to = EliteSquadLeader(team);
                if (to == null || IsDeploying(to) || !squad.Contains(to))
                    to = squad.Count > 0 ? squad[0] : null;
            }
            else
            {
                double best = double.MaxValue;
                foreach (RoamingAttacker b in squad)
                {
                    double score;
                    if (b.InLiveCombat)
                        score = 0;
                    else
                    {
                        Helpers.ObjectState hq = NearestEnemyBaseHq(b);
                        score = hq != null ? Helpers.distanceTo(b._state, hq) : 1e6;
                    }
                    if (score < best) { best = score; to = b; }
                }
            }
            return to;
        }

        /// <summary>The S / X summons whose 3 s are up: to wherever that bot is NOW.</summary>
        void PollPendingSummons(int now)
        {
            for (int i = _pendingSummons.Count - 1; i >= 0; i--)
            {
                PendingSummon s = _pendingSummons[i];
                if (now - s.Due < 0)
                    continue;
                _pendingSummons.RemoveAt(i);
                InfServer.Game.Player player = s.Player;
                if (player == null || player.IsDead || player.IsSpectator || player._arena != _arena || !IsJoinableSquadTeam(player._team))
                    continue;
                if (CarriesNoSummonFlag(player))
                {   // (2026-11-18, owner) no teleporting away with the Citizens / Elite Unit / Mercenaries Standard
                    player.sendMessage(-1, "Squad: you can't be summoned while you carry a flag.");
                    _squadOrderNext.Remove(player);
                    _squadSummonMeNext.Remove(player);
                    continue;
                }
                if (UnderTeleportDisrupt(player))
                {   // (2026-11-24) walked into a teleport disruptor during the 3 s: no summon (and no cooldown spent)
                    player.sendMessage(-1, "Squad: you're inside a teleport disruptor - we can't summon you.");
                    _squadOrderNext.Remove(player);
                    _squadSummonMeNext.Remove(player);
                    continue;
                }
                RoamingAttacker to = SummonTarget(player._team, s.Order);
                if (to == null)
                {
                    player.sendMessage(-1, "Squad: nobody out in the field to summon you.");
                    _squadOrderNext.Remove(player);
                    _squadSummonMeNext.Remove(player);
                    continue;
                }
                Helpers.ObjectState at = OpenSpotNear(to._state.positionX, to._state.positionY, 140);
                if (at == null)
                {
                    player.sendMessage(-1, "Squad: no room there right now - try again.");
                    _squadOrderNext.Remove(player);
                    _squadSummonMeNext.Remove(player);
                    continue;
                }
                warp(player, at);
                SquadSay(to, s.Order == OrderHome ? Pick3("sum'd", "got you, regroup on me", "here") : Pick3("sum'd, they're right here", "in the fight, careful", "go go"));
                player.sendMessage(0, String.Format("&Squad: summoned to {0}.", InfServer.Game.Modules.BotNameModule.GetDisplayName(to)));
            }
        }

        // ---------- greeting (2026-11-16f, owner) ----------

        readonly Dictionary<Team, int> _squadGreetDue = new Dictionary<Team, int>();

        /// <summary>A player joined: a moment later (once they're on the team) the squad leader greets them.</summary>
        readonly HashSet<Team> _squadGreetHelpOnly = new HashSet<Team>();
        readonly Dictionary<Team, int> _squadHelpNext = new Dictionary<Team, int>();

        void QueueSquadGreeting(Team team, int now, bool helpOnly = false)
        {
            if (team == null)
                return;
            if (helpOnly && _squadGreetDue.ContainsKey(team) && !_squadGreetHelpOnly.Contains(team))
                return;   // (a full greeting is already on its way - it has the help lines)
            _squadGreetDue[team] = now + (helpOnly ? 600 : 1500);
            if (helpOnly) _squadGreetHelpOnly.Add(team); else _squadGreetHelpOnly.Remove(team);
        }

        /// <summary>Team chat from the squad leader: "S for S" then "Support ready! Say @ nme or @ home".</summary>
        void PollSquadGreetings(int now)
        {
            if (_squadGreetDue.Count == 0)
                return;
            List<Team> due = null;
            foreach (KeyValuePair<Team, int> kv in _squadGreetDue)
                if (now - kv.Value >= 0)
                    (due ?? (due = new List<Team>())).Add(kv.Key);
            if (due == null)
                return;
            foreach (Team t in due)
            {
                _squadGreetDue.Remove(t);
                if (t == null || t.ActivePlayerCount == 0)
                    continue;
                Bots.Bot from = EliteSquadLeader(t);
                if (from == null)
                    foreach (Bots.Bot b in _arena.ActiveBots)
                        if (b != null && b._team == t && !b.IsDead && !b.bCondemned) { from = b; break; }
                string name = from != null ? from._alias : t._name;
                bool helpOnly = _squadGreetHelpOnly.Remove(t);
                if (!helpOnly)
                    Helpers.Player_RouteChatRaw(t, name, "S for S", 0, Helpers.Chat_Type.Team);
                Helpers.Player_RouteChatRaw(t, name, "Support ready! @ nme = we come attack with you, @ home = we come defend with you", 0, Helpers.Chat_Type.Team);
                Helpers.Player_RouteChatRaw(t, name, "H = medic heals you, follow = we follow you, stop = back on the attack", 0, Helpers.Chat_Type.Team);
                Helpers.Player_RouteChatRaw(t, name, "Engineer: mg / plasma / rocket / rocket ap / sentry / hq here = I build it (every 30 s), remove mg etc = I dismantle it", 0, Helpers.Chat_Type.Team);
                if (IsStrikeTeam(t))   // (2026-11-17, owner) pick the squad's other classes
                    Helpers.Player_RouteChatRaw(t, name, "Want other classes? In the dropship say: change squad Jump Trooper, SciOps - we come to you (F11 names, once every 10 min)", 0, Helpers.Chat_Type.Team);
                // (2026-11-17, owner) the last line: how to see this again
                Helpers.Player_RouteChatRaw(t, name, "Say help and we'll tell you again how we can support you", 0, Helpers.Chat_Type.Team);
            }
        }

        // ---------- bots summoned into the dropship (2026-11-17) ----------
        readonly Dictionary<Bots.Bot, InfServer.Game.Player> _shipSummoned = new Dictionary<Bots.Bot, InfServer.Game.Player>();

        /// <summary>Open ground round a player in the dropship / safe zone, in clear sight of them (never across a wall).</summary>
        Helpers.ObjectState SummonSpotInSafe(Helpers.ObjectState p)
        {
            Random r = _rand ?? (_rand = new Random(Environment.TickCount));
            for (int i = 0; i < 24; i++)
            {
                double a = r.NextDouble() * 2 * Math.PI; int rad = 48 + r.Next(i < 12 ? 72 : 112);
                short x = (short)(p.positionX + Math.Cos(a) * rad), y = (short)(p.positionY + Math.Sin(a) * rad);
                if (x < 32 || y < 32 || _arena.getTile(x, y).Blocked || InNoBotSpawnBuilding(x, y))
                    continue;
                if (LineClear(x, y, p.positionX, p.positionY))
                    return new Helpers.ObjectState { positionX = x, positionY = y };
            }
            return new Helpers.ObjectState { positionX = p.positionX, positionY = p.positionY };   // (right on them)
        }

        /// <summary>The new player zone (the green spawn-zone buffer, at / near its edge) - bots normally keep out of it.</summary>
        bool InSummonSafeZone(short x, short y)
        {
            return NearNewPlayerBuffer(x, y);
        }

        /// <summary>(2026-11-19) the bot is in the dropship (a safe zone: no fighting in there).</summary>
        public bool BotInDropshipSafeZone(Bots.Bot b)
        {
            return b != null && b._state != null && _eol != null && _eol.IsInPioneerDropshipSafeZone(b._state.positionX, b._state.positionY);
        }

        /// <summary>A bot summoned into the dropship: the player's spot while they're still in there (it waits round them), else null.</summary>
        public Helpers.ObjectState ShipSummonHold(Bots.Bot b)
        {
            InfServer.Game.Player p;
            if (b == null || !_shipSummoned.TryGetValue(b, out p) || p == null || p.IsDead || p.IsSpectator || p._arena != _arena
                || p._state == null || p._team != b._team)
                return null;
            if (!(_eol != null && _eol.IsInPioneerDropshipSafeZone(p._state.positionX, p._state.positionY))
                && !InSummonSafeZone(p._state.positionX, p._state.positionY))
                return null;
            return p._state;
        }

        /// <summary>The player left the dropship (or died / left): the bots summoned in there walk out through the portal.</summary>
        void PollShipSummoned(int now)
        {
            if (_shipSummoned.Count == 0)
                return;
            List<Bots.Bot> done = null;
            foreach (KeyValuePair<Bots.Bot, InfServer.Game.Player> kv in _shipSummoned)
            {
                Bots.Bot b = kv.Key;
                if (b == null || b.IsDead || b.bCondemned) { (done ??= new List<Bots.Bot>()).Add(b); continue; }
                if (ShipSummonHold(b) != null)
                    continue;
                if (_eol != null && b._state != null && _eol.IsInPioneerDropshipSafeZone(b._state.positionX, b._state.positionY) && !IsDeploying(b))
                    BeginDeploy(b, now);
                (done ??= new List<Bots.Bot>()).Add(b);
            }
            if (done != null) foreach (Bots.Bot b in done) _shipSummoned.Remove(b);
        }

        /// <summary>The queued "@" teleports, one bot at a time, onto open ground around the player.</summary>
        void PollSquadCommands(int now)
        {
            EnsureSquadChatHook();
            PollSquadGreetings(now);
            PollPendingSummons(now);
            PollBoardMedicSummons(now);   // (2026-11-17) the Ghost Reapers' board medic's summon kit
            PollShipSummoned(now);        // (2026-11-17) bots summoned into the dropship walk out when their player does
            if (_pendingSuitWarps.Count == 0)
                return;
            for (int i = _pendingSuitWarps.Count - 1; i >= 0; i--)
            {
                PendingSuitWarp w = _pendingSuitWarps[i];
                if (now - w.Due < 0)
                    continue;
                _pendingSuitWarps.RemoveAt(i);
                InfServer.Game.Player p = w.To;
                Bots.Bot b = w.Bot;
                if (b == null || b.IsDead || b.bCondemned || p == null || p.IsDead || p.IsSpectator || p._team != b._team)
                    continue;
                // (2026-11-26, owner: "no one should be able to get summoned by anyone ... if they have a TD on them" / "team mates
                // can't teleport to you if YOU're in a teleport disrupt. that makes no sense ... its only for summons the TD") a bot
                // inside an enemy disruptor can't suit-teleport out of it (it comes on foot); teleporting TO a player inside one is fine
                if (_arena.isTeleportDisruptedAt(b._state.positionX, b._state.positionY, b._team))
                    continue;
                bool ship = _eol != null && _eol.IsInPioneerDropshipSafeZone(p._state.positionX, p._state.positionY);
                bool safe = !ship && InSummonSafeZone(p._state.positionX, p._state.positionY);
                if (!ship && !safe && !InPlayArea(p._state.positionX, p._state.positionY, 0))
                    continue;
                Helpers.ObjectState at = ship || safe ? SummonSpotInSafe(p._state) : OpenSpotNear(p._state.positionX, p._state.positionY, 160);
                if (at == null)
                    continue;
                _deployStartTick.Remove(b);   // (one still in the dropship comes straight out)
                WarpBot(b, at);
                b.DamageImmuneUntilTick = NonZeroTick(now + 1500);
                if (ship || safe) _shipSummoned[b] = p; else _shipSummoned.Remove(b);
            }
        }
    }
}
