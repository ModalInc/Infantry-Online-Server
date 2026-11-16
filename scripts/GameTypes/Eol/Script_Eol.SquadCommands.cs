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
        const int SquadToMeCooldownMs = 30000;    // @ / @ home: once every 30 s
        const int SquadHealCallMs = 20000;        // H: the medics stay on you this long (or until you're full)
        const int SquadHealWarpBeyond = 900;      // H: a medic further away than this suit-teleports to you
        const int SquadFollowMs = 10 * 60 * 1000; // follow: up to 10 min

        readonly Dictionary<InfServer.Game.Player, int> _squadToMeNext = new Dictionary<InfServer.Game.Player, int>();

        struct PendingSummon
        {
            public InfServer.Game.Player Player;
            public int Order;
            public int Due;
        }
        readonly List<PendingSummon> _pendingSummons = new List<PendingSummon>();

        class SquadCall { public InfServer.Game.Player Player; public int Until; }
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
            return p;
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

        const int OrderHome = 1, OrderEnemy = 2, OrderToMe = 3, OrderDefendHere = 4, OrderHeal = 5, OrderFollow = 6, OrderStop = 7;
        /// <summary>"@ home": the squad holds the spot it was called to this long (then back to attacking bases).</summary>
        const int SquadDefendMs = 5 * 60 * 1000;
        const int SquadDefendRadius = 350;

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
            if (order == OrderStop)
            {
                bool was = _squadFollow.Remove(team) | _squadDefend.Remove(team);
                player.sendMessage(0, was ? "&Squad: back to attacking enemy bases." : "&Squad: we're already on the attack.");
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
                _squadHealCall[team] = new SquadCall { Player = player, Until = now + SquadHealCallMs };
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
                if (!inField)
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
                    player.sendMessage(0, i == 0 ? "&Squad: we're with you - defending this spot for 5 min."
                        : String.Format("&Squad: {0} suit-teleporting to you in 2 s - defending this spot for 5 min.", i));
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
            if (SummonTarget(team, order) == null)
            {
                player.sendMessage(-1, "Squad: nobody out in the field to summon you.");
                return;
            }
            _pendingSummons.RemoveAll(s => s.Player == player);
            _pendingSummons.Add(new PendingSummon { Player = player, Order = order, Due = now + SquadSummonDelayMs });
            _squadOrderNext[player] = now + SquadOrderCooldownMs;
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
                RoamingAttacker to = SummonTarget(player._team, s.Order);
                if (to == null)
                {
                    player.sendMessage(-1, "Squad: nobody out in the field to summon you.");
                    _squadOrderNext.Remove(player);
                    continue;
                }
                Helpers.ObjectState at = OpenSpotNear(to._state.positionX, to._state.positionY, 140);
                if (at == null)
                {
                    player.sendMessage(-1, "Squad: no room there right now - try again.");
                    _squadOrderNext.Remove(player);
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
        void QueueSquadGreeting(Team team, int now)
        {
            if (team != null)
                _squadGreetDue[team] = now + 1500;
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
                Helpers.Player_RouteChatRaw(t, name, "S for S", 0, Helpers.Chat_Type.Team);
                Helpers.Player_RouteChatRaw(t, name, "Support ready! @ nme = we come attack with you, @ home = we come defend with you, S / X = summon you", 0, Helpers.Chat_Type.Team);
                Helpers.Player_RouteChatRaw(t, name, "H = medic heals you, follow = we follow you, stop = back on the attack", 0, Helpers.Chat_Type.Team);
            }
        }

        /// <summary>The queued "@" teleports, one bot at a time, onto open ground around the player.</summary>
        void PollSquadCommands(int now)
        {
            EnsureSquadChatHook();
            PollSquadGreetings(now);
            PollPendingSummons(now);
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
                if (!InPlayArea(p._state.positionX, p._state.positionY, 0))
                    continue;
                Helpers.ObjectState at = OpenSpotNear(p._state.positionX, p._state.positionY, 160);
                if (at == null)
                    continue;
                _deployStartTick.Remove(b);   // (one still in the dropship comes straight out)
                WarpBot(b, at);
                b.DamageImmuneUntilTick = NonZeroTick(now + 1500);
            }
        }
    }
}
