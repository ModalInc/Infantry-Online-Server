// CTF:X server-side bots - TEAM CHAT (Travis 10-13: "the talking would be nice at least for team macros, not overly spammy, just the
// occasional defender saying they are at home anchoring, or an attacker storming ... mostly quiet"; "if they could TAKE summon requests
// from teamchats, or attach to a player that says they are attacking ... not with instantaneous reaction and not everybody is paying
// attention or wants to, just like players").
//
// SAYING: a bot speaks in team chat under its callsign, only to the humans on its team (no human on the team: nothing is sent at all).
//   Callouts: an anchor taking its choke, a base alarm (one defender calls the count), a storm squad going in, an engineer putting a
//   turret up. Each is a chance, not a rule, and two caps keep the chat quiet: a team says one callout per chatTeamGap (45 s) and a
//   bot one per chatBotGap (3 min). Replies (summoning you, coming to you) skip the team gap, never the bot gap / 3.
// LISTENING (TEAM chat of a human, his team macros included, heard by the bots on his team; Travis 10-21: public chat and public
// macros are not heard - a public "summon" no longer pulls him):
//   summon request ("sum", "summ", "summon", "sum me"): every bot of the team that carries a summoner (Squad Leader, Sergeant,
//     Captain) and notices (attention by compliance, less in a fight) answers after its own delay (1-5 s); the first one ready pulls
//     the human to itself with its SL Summon (charges, energy and anti-warp at both ends, as a player summons).
//   home call (Travis 10-25): the sound macro %28 "We are under attack!" (underAttackBongs, or "under attack", "help home", "inc home",
//     "def home") is a desperate call to save home; %26 "Enemy Sighted" (sightedBongs, "enemy sighted", "contact home") a lighter
//     warning, heard only when the caller is at home. Both raise the team's base alarm where he stands (CTFXCommand.HumanAlarm) - a
//     human sees what the bots cannot (a cloaked infiltrator, a stormer behind a door). They are checked before the attack call: the
//     macro's text has the word "attack" in it, and a %28 used to send bots out to attack with the caller.
//   attack call (the sound macro %24, attackBongs, or "attacking", "atk", "push", "storming", "going in"): free bots of the team
//     (not anchors, defenders, posts, engineers at work, storm squads) each decide whether they heard it and want to go, by
//     compliance, their taste for raids and distance; at most a third of them and 4 in all. After their own alarm delay each attaches:
//     Suit Teleport onto the caller when far (as a player attaches), then fights around him for 1-2.5 minutes, re-attaching if he
//     warps away. A caller who summons a bot with his own summoner gets it attached the same way.
/* PARSER GUARD: keep a block comment after the last line comment */

using System;
using System.Collections.Generic;
using System.Text;

using InfServer.Game;
using InfServer.Protocol;

namespace InfServer.Script.GameType_CTF
{
    internal sealed class CTFXChat
    {
        readonly CTFXBotManager _m;
        internal bool On = true, Listen = true;
        internal int TeamGapMs = 45000, BotGapMs = 180000;
        internal readonly List<int> AttackBongs = new List<int>(new int[] { 24 });
        internal readonly List<int> SummonBongs = new List<int>(new int[] { 27 });
        internal readonly List<int> EnemySummonBongs = new List<int>(), HomeSummonBongs = new List<int>();
        internal readonly List<int> UnderAttackBongs = new List<int>(new int[] { 28 });   // %28 "We are under attack!"
        internal readonly List<int> SightedBongs = new List<int>(new int[] { 26 });       // %26 "Enemy Sighted"
        internal int HomeCalls, HomeSightings, HomeCallsHeard;
        const int SumAny = 0, SumEnemy = 1, SumHome = 2;
        internal int SumEnemyAsks, SumHomeAsks, Macros;

        internal static void Bongs(string csv, List<int> into)
        {
            into.Clear();
            foreach (string s in (csv ?? "").Split(','))
            {
                int n;
                if (int.TryParse(s.Trim(), out n) && n > 0) into.Add(n);
            }
        }

        static bool AllOf(string msg, char c)
        {   // "X", "xxx", "SSSSSS": one letter repeated, nothing else
            string t = msg.Trim();
            if (t.Length == 0) return false;
            foreach (char ch in t) if (char.ToLowerInvariant(ch) != c) return false;
            return true;
        }

        static bool BongIn(List<int> bongs, string msg, byte bong)
        {
            if (bongs.Contains(bong)) return true;
            foreach (int b in bongs) if (msg.IndexOf("%" + b, StringComparison.Ordinal) >= 0) return true;
            return false;
        }
        readonly Dictionary<Team, int> _teamNext = new Dictionary<Team, int>();
        readonly Dictionary<CTFXSlot, int> _botNext = new Dictionary<CTFXSlot, int>();
        readonly List<Pending> _pending = new List<Pending>();
        int _lastStep;
        // stats
        internal int SumNone, SumHeard, SumLost;
        internal int Said, Unheard, Heard, SumAsks, Summoned, SumFailed, Calls, Attached, Reattached, ItemSummons, FlagCalls;

        /// <summary>Where a bot stands, as a player names it in chat.</summary>
        internal string PlaceOf(CTFXBot b)
        {
            string w = Place(_m.AreaAt(b._state.positionX, b._state.positionY));
            return w == "out" ? "outside" : w;
        }
        readonly List<string> _recent = new List<string>();

        sealed class Pending
        {
            public CTFXSlot Slot;
            public Player From;
            public int Due;
            public bool Summon;            // false = attack call
            public bool Flag;              // an attack call near a flag: the bot joins that flag's chase
            public int Ask;                // which request (one summon answers it)
            public int Kind;               // SumAny / SumEnemy / SumHome
            public int Retry;              // MiniTP: how often this summoner was anti-warped and tried again after walking out of the disruptor
        }
        int _ask;
        readonly HashSet<int> _answered = new HashSet<int>();

        static readonly string[] SumWords = new string[] { "sum", "summ", "summon", "summons", "sumon", "summ me", "sum me" };
        static readonly string[] AttackWords = new string[] { "attacking", "atk", "attack", "push", "pushing", "storming", "going in", "goin in" };
        static readonly string[] UnderAttackWords = new string[] { "under attack", "help home", "home help", "inc home", "def home", "defend home", "save home", "help base" };
        static readonly string[] SightedWords = new string[] { "enemy sighted", "contact home", "enemy home", "nme home" };

        internal CTFXChat(CTFXBotManager m) { _m = m; }

        internal void Clear()
        {
            SumNone = SumHeard = SumLost = 0;
            _teamNext.Clear(); _botNext.Clear(); _pending.Clear(); _answered.Clear(); _recent.Clear();
            Said = Unheard = Heard = SumAsks = Summoned = SumFailed = Calls = Attached = Reattached = ItemSummons = 0;
        }

        // ================================================================ saying
        static string Place(string end)
        {
            return string.IsNullOrEmpty(end) ? "base" : end.Replace('-', ' ');
        }

        internal string Pick(params string[] lines) { return lines[_m.Rng.Next(lines.Length)]; }

        /// <summary>Team chat from bot b, if the dice and both caps allow. True = sent.</summary>
        internal bool Say(CTFXBot b, string text, double chance, bool reply, byte bong = 0)
        {
            if (!On || b == null || b.Slot == null || b.Slot.Name == null || b._team == null || b.IsDead) return false;
            if (_m.Rng.NextDouble() >= chance) return false;
            int now = Environment.TickCount, t;
            if (!reply && _teamNext.TryGetValue(b._team, out t) && now - t < 0) return false;
            if (_botNext.TryGetValue(b.Slot, out t) && now - t < (reply ? -BotGapMs * 2 / 3 : 0)) return false;
            SC_Chat c = null;
            int n = 0;
            foreach (Player p in _m.Arena.PlayersIngame)
            {
                if (p == null || p._team != b._team) continue;
                if (c == null)
                {
                    c = new SC_Chat();
                    c.chatType = Helpers.Chat_Type.Team; c.from = _m.RowName(b.Slot); c.message = text; c.bong = bong;      // bong = the macro's sound (%21 summon, %24 at enemy, %25 at home, %28 under attack ...)
                }
                p._client.sendReliable(c);
                n++;
            }
            if (n == 0) { Unheard++; return false; }                        // nobody on the team to hear it: nothing is sent
            if (!reply) _teamNext[b._team] = now + TeamGapMs;
            _botNext[b.Slot] = now + BotGapMs;
            Said++;
            _recent.Add(DateTime.Now.ToString("HH:mm:ss") + " " + b.Slot.Name + ": " + text);
            if (_recent.Count > 6) _recent.RemoveAt(0);
            _m.Log("[CTFXBots] chat " + b._team._name + " " + b.Slot.Name + ": " + text + (bong > 0 ? "  [sound %" + bong + "]" : ""));
            return true;
        }

        internal void Anchor(CTFXBot b, string end)
        {
            Say(b, Pick("anchoring " + Place(end), "got " + Place(end) + " choke", "holding " + Place(end), "anchor " + Place(end)), 0.4, false);
        }

        internal void Alarm(CTFXBot b, string end, int n)
        {
            Say(b, Pick("inc " + Place(end) + " x" + n, n + " in " + Place(end), "help " + Place(end) + " (" + n + ")", "they are in " + Place(end)), 0.6, false);
        }

        internal void Storming(CTFXBot b, string end)
        {
            Say(b, Pick("storming " + Place(end), "going in " + Place(end), "port in " + Place(end) + " now"), 0.5, false);
        }

        internal void TurretUp(CTFXBot b, string end, string role, bool rebuilt)
        {
            Say(b, rebuilt ? Pick(role + " back up " + Place(end), "rebuilt " + role) : Pick(role + " up " + Place(end), "setting up " + Place(end), "turrets going up " + Place(end)),
                rebuilt ? 0.15 : 0.12, false);
        }

        // ================================================================ listening
        static bool HasWord(string msg, string[] words)
        {
            string m = " " + msg.ToLowerInvariant() + " ";
            foreach (string w in words)
            {
                int i = m.IndexOf(w, StringComparison.Ordinal);
                while (i >= 0)
                {
                    bool l = i == 0 || !char.IsLetter(m[i - 1]), r = i + w.Length >= m.Length || !char.IsLetter(m[i + w.Length]);
                    if (l && r) return true;
                    i = m.IndexOf(w, i + 1, StringComparison.Ordinal);
                }
            }
            return false;
        }

        /// <summary>Arena thread: a human said something the bots of his team can hear.</summary>
        internal void Hear(Player from, string msg, byte bong)
        {
            if (!On || !Listen || from == null || from.IsSpectator || from._team == null || msg == null) return;
            Heard++;
            int now = Environment.TickCount;
            if (_m.Mini != null) { MiniHear(from, msg, bong, now); return; }      // MiniTP: its own reading of the team chat (below)
            // summons (Travis 10-13): X / XXX = summon me to the enemy, S / SSS = summon me home, "sum ..." or the summon sound = anyone
            string lw = msg.ToLowerInvariant();
            if (AllOf(msg, 'x') || BongIn(EnemySummonBongs, msg, bong) || lw.Contains("summon to enemy") || lw.Contains("sum nme") || lw.Contains("summon nme") || lw.Contains("sum to nme"))
            { SummonAsk(from, now, SumEnemy); return; }
            if (AllOf(msg, 's') || BongIn(HomeSummonBongs, msg, bong)) { SummonAsk(from, now, SumHome); return; }
            if (HasWord(msg, SumWords) || BongIn(SummonBongs, msg, bong))
            {   // Travis 10-14 eve: "Summon Home (ssss)" went unanswered: the words say which summon
                string lm = msg.ToLowerInvariant();
                SummonAsk(from, now, lm.Contains("home") || lm.Contains("ssss") ? SumHome : lm.Contains("enemy") || lm.Contains("xxx") ? SumEnemy : SumAny);
                return;
            }
            if (_m.Mini != null) return;                                   // MiniTP: summons only for now (the home / attack calls come with the swing logic)
            // home calls before the attack call: "We are under attack!" has the word "attack" in it
            if (BongIn(UnderAttackBongs, msg, bong) || HasWord(msg, UnderAttackWords))
            { HomeCalls++; if (_m.Cmdr != null && _m.Cmdr.HumanAlarm(from, true, now)) HomeCallsHeard++; return; }
            if (BongIn(SightedBongs, msg, bong) || HasWord(msg, SightedWords))
            { HomeSightings++; if (_m.Cmdr != null && _m.Cmdr.HumanAlarm(from, false, now)) HomeCallsHeard++; return; }
            if (BongIn(AttackBongs, msg, bong) || HasWord(msg, AttackWords)) AttackCall(from, now);
        }

        /// <summary>The first bot that turns for home after a teammate's call says so (a reply: it skips the team's chat gap).</summary>
        internal void HomeReply(CTFXBot b, bool desperate)
        {
            Say(b, desperate ? Pick("omw home", "coming home", "porting home", "on my way back", "holding on, coming") : Pick("checking it", "on it", "watching home", "looking"),
                desperate ? 0.8 : 0.45, true);
        }

        // ================================================================ MiniTP: what a human's TEAM chat means to the bots
        // Pub night 11-07 (Travis + the chat logs): "they only answer to X not XXX", "no ignores - it should happen in TP", "they dont suit-teleport to a player
        // sending @ nme / the %24 macro". So: every spelling of the ask counts (X, XXX, x x x, [XXX], "x me"), the answer is never rolled away, it comes
        // within a second or two, a summoner that is in the wrong base hops there first (a player would warp over and then summon), and the attack call
        // (%24 / "Attacking Enemy") pulls attackers to the caller with the Suit Teleport.
        readonly Dictionary<string, int> _miniAskAt = new Dictionary<string, int>(), _miniAttackAt = new Dictionary<string, int>();

        /// <summary>Lower case; letters, digits, % and @ only; everything else is one space ("[XXX]" -> "xxx", "| Attacking Enemy [N8] |" -> "attacking enemy n8").</summary>
        static string Norm(string msg)
        {
            StringBuilder sb = new StringBuilder();
            bool sp = true;
            foreach (char ch in (msg ?? "").ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(ch) || ch == '%' || ch == '@') { sb.Append(ch); sp = false; }
                else if (!sp) { sb.Append(' '); sp = true; }
            }
            return sb.ToString().Trim();
        }

        /// <summary>Only the letter c, any number of times, spaces allowed ("x", "xxx", "x x x", "xXxXx").</summary>
        static bool Letters(string t, char c)
        {
            int n = 0;
            foreach (char ch in t) { if (ch == ' ') continue; if (ch != c) return false; n++; }
            return n > 0;
        }

        /// <summary>X / XXX / "x me" / "xxx pls" (c = 'x'), S / SSS / "s me" (c = 's').</summary>
        static bool AskLike(string t, char c)
        {
            if (Letters(t, c)) return true;
            string[] w = t.Split(' ');
            if (w.Length < 2 || !Letters(w[0], c)) return false;
            for (int i = 1; i < w.Length; i++)
                if (w[i] != "me" && w[i] != "pls" && w[i] != "plz" && w[i] != "please" && w[i] != "now" && w[i] != "asap" && w[i] != "sl" && w[i] != "med" && w[i] != "bot") return false;
            return true;
        }

        static bool Has(string t, params string[] phrases)
        {
            foreach (string p in phrases) if (t.IndexOf(p, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        void MiniHear(Player from, string msg, byte bong, int now)
        {
            string t = Norm(msg);
            bool home = AskLike(t, 's') || BongIn(HomeSummonBongs, msg, bong) || Has(t, "summon home", "sum home", "summon me home", "sum me home", "take home", "take me home", "home sum");
            bool enemy = AskLike(t, 'x') || BongIn(EnemySummonBongs, msg, bong)
                || Has(t, "summon to enemy", "sum to enemy", "summon nme", "sum nme", "sum to nme", "to enemy", "to nme", "nme portal", "enemy portal", "green portal", "take me to enemy");
            if (home && !enemy) { MiniSummon(from, SumHome, now); return; }
            if (enemy) { MiniSummon(from, SumEnemy, now); return; }
            if (BongIn(AttackBongs, msg, bong) || Has(t, "attacking enemy", "attack enemy", "attacking nme", "at enemy", "@ enemy", "@ nme", "@nme", "@enemy", "attach to attack", "etfc")) { MiniAttack(from, now); return; }
            if (HasWord(msg, SumWords) || BongIn(SummonBongs, msg, bong)) MiniSummon(from, SumAny, now);
        }

        /// <summary>Lower = better: already in the base the ask needs (Raid for the enemy, Defend for home), the right class (SL to the enemy, medic home), not mid-fight.</summary>
        int MiniRank(CTFXBot b, int kind)
        {
            MiniBot mb = b.Mini;
            int r = 0;
            bool place = kind == SumAny || (kind == SumEnemy ? mb.Phase == MiniBot.Raid : mb.Phase == MiniBot.Defend);
            if (!place) r += 10;
            bool leader = b.ClassId != CTFXBot.SK_MEDIC;
            if (kind == SumEnemy ? !leader : leader) r += 3;
            if (b.HasTarget) r += 2;
            if (_m.AntiWarpedAt(b._state.positionX, b._state.positionY, b._team, b) != null) r += 5;      // standing in an enemy disruptor: the others first
            return r;
        }

        void MiniSummon(Player from, int kind, int now)
        {
            SumAsks++;
            if (kind == SumEnemy) SumEnemyAsks++; else if (kind == SumHome) SumHomeAsks++;
            if (from.IsDead) return;
            int last;
            if (_miniAskAt.TryGetValue(from._alias, out last) && now - last < 2500) return;      // a second "XXX" a moment later is the same ask
            _miniAskAt[from._alias] = now;
            int ask = ++_ask;
            List<CTFXBot> cands = new List<CTFXBot>();
            CTFXBot anyBot = null;
            foreach (CTFXBot b in _m.LiveBots)
            {
                if (b.IsDead || b.Released || b._team != from._team || b.Slot == null || b.Mini == null) continue;
                anyBot = b;
                if (b.InDropship || !_m.Squads.CarriesSummoner(b)) continue;
                cands.Add(b);
            }
            _m.MiniLog("[MiniTP] ask '" + (kind == SumEnemy ? "enemy" : kind == SumHome ? "home" : "any") + "' from " + from._alias + ": " + cands.Count + " summoner(s) up on his team");
            if (cands.Count == 0)
            {
                SumNone++;
                if (anyBot != null) Say(anyBot, Pick("no summoner up", "sl / med down, sorry", "cant sum, no sl"), 0.9, true);
                return;
            }
            cands.Sort(delegate(CTFXBot a, CTFXBot b2) { return MiniRank(a, kind).CompareTo(MiniRank(b2, kind)); });
            int n = 0;
            for (int i = 0; i < cands.Count && n < 3; i++)
            {
                CTFXBot b = cands[i];
                int due = now + 300 + _m.Rng.Next(500) + n * 900;
                bool inPlace = kind == SumAny || (kind == SumEnemy ? b.Mini.Phase == MiniBot.Raid : b.Mini.Phase == MiniBot.Defend);
                if (!inPlace) { _m.Mini.Recruit(b, kind == SumEnemy, now); due += 1100; }     // it hops to the right base first, then summons
                Pending q = new Pending();
                q.Slot = b.Slot; q.From = from; q.Summon = true; q.Ask = ask; q.Kind = kind; q.Due = due;
                _pending.Add(q);
                n++;
                if (inPlace && n >= 2) break;                                                 // two summoners already in place are backup enough
            }
            SumHeard++;
        }

        void MiniAttack(Player from, int now)
        {
            int last;
            if (_miniAttackAt.TryGetValue(from._alias, out last) && now - last < 6000) return;
            _miniAttackAt[from._alias] = now;
            if (from.IsDead) return;
            Calls++;
            int ca = CTFXMini.AreaOf(from._state.positionX >> 4, from._state.positionY >> 4);
            List<CTFXBot> c = new List<CTFXBot>(), dfs = new List<CTFXBot>();
            int homeDef = 0;
            foreach (CTFXBot b in _m.LiveBots)
            {
                if (b.IsDead || b.Released || b.InDropship || b._team != from._team || b.Slot == null || b.Mini == null) continue;
                MiniBot mb = b.Mini;
                int home = CTFXMini.HomeAreaOf(mb.Side), foe = home == 0 ? 1 : 0;
                if (mb.Phase == MiniBot.Defend) homeDef++;
                if (ca != foe && ca != 2) continue;                                           // only a call from the enemy base or the field is an assault to join (not from home or the ship)
                if (mb.Role == "SL" || mb.Role == "DHVY" || mb.Role == "ENG" || mb.Role == "MED") continue;   // the summoner stays back, the anchor, the engineer and the one medic keep their jobs
                if (!mb.Attack) { if (mb.Phase == MiniBot.Defend && !b.HasTarget) dfs.Add(b); continue; }   // Travis 11-08: the base's other defenders can attach too, when they are not in a fight
                c.Add(b);
            }
            int give = Math.Max(0, Math.Min(dfs.Count, homeDef - 3));                         // but three (anchor, engineer + one) always stay home
            for (int i = 0; i < give; i++)
            {
                int j = i + _m.Rng.Next(dfs.Count - i);
                CTFXBot dt = dfs[j]; dfs[j] = dfs[i]; dfs[i] = dt;
                c.Add(dfs[i]);
            }
            int cap = Math.Min(c.Count, Math.Max(3, Math.Min(6, (c.Count * 3 + 3) / 4)));      // 11-11 (Travis: "are bots properly attaching ...?"): 3/4 of the eligible bots come (was half, 2-5)
            for (int i = 0; i < c.Count && i < cap; i++)
            {
                int j = i + _m.Rng.Next(c.Count - i);                                         // random pick without repeats
                CTFXBot t = c[j]; c[j] = c[i]; c[i] = t;
                Pending q = new Pending();
                q.Slot = t.Slot; q.From = from; q.Summon = false; q.Flag = false;
                q.Due = now + 500 + i * 350 + _m.Rng.Next(300);
                _pending.Add(q);
            }
            _m.MiniLog("[MiniTP] attack call from " + from._alias + " (area " + ca + "): " + Math.Min(c.Count, cap) + " of " + c.Count + " eligible bots called (defenders offered " + give + " of " + homeDef + " at home)");
        }

        void SummonAsk(Player from, int now, int kind)
        {
            SumAsks++;
            if (kind == SumEnemy) SumEnemyAsks++; else if (kind == SumHome) SumHomeAsks++;
            int ask = ++_ask, n = 0, able = 0;
            foreach (CTFXBot b in _m.LiveBots)
            {
                if (b.IsDead || b.Released || b.InDropship || b._team != from._team || b.Slot == null) continue;
                if (!_m.Squads.CarriesSummoner(b)) continue;
                if (_m.Mini != null && (b.Mini == null || (b.Mini.Phase != MiniBot.Defend && b.Mini.Phase != MiniBot.Raid))) continue;   // MiniTP: only a bot in play (not in the ship or the field) answers
                if (kind == SumHome && !(_m.Mini != null ? (b.Mini != null && b.Mini.Phase == MiniBot.Defend) : (b.Group != null && b.Group.HomeEnd != null && _m.InsideEnd(b.Group.HomeEnd, b)))) continue;   // at home (MiniTP: defending its own base)
                if (kind == SumEnemy && !(_m.Mini != null ? (b.Mini != null && b.Mini.Phase == MiniBot.Raid) : AtEnemy(b))) continue;                                                              // at the enemy (MiniTP: raiding the enemy base)
                able++;
                CTFXHuman h = b.Slot.Human;
                double comply = h != null ? h.Comply : 0.6;
                double p = (0.80 + 0.18 * comply) * (b.HasTarget ? 0.6 : 1.0);   // Travis 10-14 eve: nobody summoned him on a team of 20
                bool capt = b.ClassId == CTFXBot.SK_CAPT;
                if (capt) p = b.HasTarget ? 0.85 : 0.98;                          // the Captain anchor: high reaction to summon requests
                if (_m.Rng.NextDouble() >= p) continue;
                Pending q = new Pending();
                q.Slot = b.Slot; q.From = from; q.Summon = true; q.Ask = ask; q.Kind = kind;
                q.Due = now + (capt ? 400 + _m.Rng.Next(1101) : 1000 + _m.Rng.Next(4001));
                _pending.Add(q);
                n++;
            }
            if (able == 0) SumNone++;
            else if (n == 0) SumFailed++;
            else SumHeard++;
        }

        void AttackCall(Player from, int now)
        {
            Calls++;
            List<KeyValuePair<CTFXSlot, double>> c = new List<KeyValuePair<CTFXSlot, double>>();
            int free = 0;
            bool flagCall = _m.Flags != null && _m.Flags.NearFlag(from);
            if (flagCall) FlagCalls++;
            foreach (CTFXBot b in _m.LiveBots)
            {
                if (b.IsDead || b.Released || b.InDropship || b._team != from._team || b.Slot == null) continue;
                if (!Free(b)) continue;
                free++;
                CTFXHuman h = b.Slot.Human;
                double comply = h != null ? h.Comply : 0.6, taste = h != null ? Math.Max(0.5, Math.Min(1.5, h.Taste[(int)CTFXAct.Raid])) : 1.0;
                double dx = b._state.positionX - from._state.positionX, dy = b._state.positionY - from._state.positionY;
                // Travis 10-14 eve: "%24 ... no one was warping to me on a team of 20 ... as I was near a flagger": far stronger, more near a flag
                double p = (0.45 + 0.45 * comply) * taste / (1.0 + Math.Sqrt(dx * dx + dy * dy) / 8000.0);
                if (flagCall) p = Math.Min(1.0, p * 1.6);
                if (b.HasTarget) p *= 0.5;                                     // busy: it may not even read the chat
                if (_m.Rng.NextDouble() < p) c.Add(new KeyValuePair<CTFXSlot, double>(b.Slot, p));
            }
            int cap = Math.Min(flagCall ? 8 : 6, Math.Max(2, (int)Math.Round(free / (flagCall ? 2.0 : 3.0))));
            c.Sort(delegate(KeyValuePair<CTFXSlot, double> a, KeyValuePair<CTFXSlot, double> b2) { return b2.Value.CompareTo(a.Value); });
            for (int i = 0; i < c.Count && i < cap; i++)
            {
                CTFXHuman h = c[i].Key.Human;
                Pending q = new Pending();
                q.Slot = c[i].Key; q.From = from; q.Summon = false; q.Flag = flagCall;
                q.Due = now + (h != null ? h.AlarmMs(_m.Rng) : 4000) + _m.Rng.Next(1501);
                _pending.Add(q);
            }
        }

        /// <summary>Is b at the enemy: inside a base end that is not its own, or fighting away from its own end?</summary>
        bool AtEnemy(CTFXBot b)
        {
            string a = _m.AreaAt(b._state.positionX, b._state.positionY);
            string home = b.Group != null ? b.Group.HomeEnd : null;
            bool inEnd = a != "out" && !a.EndsWith("-core", StringComparison.Ordinal);
            if (inEnd && a != home) return true;
            // Travis 10-14 night: "XXXXX or summon to enemy / nme should be a summoning class that is assaulting another base or team
            // (or intending to)": on a raid, a storm, or a run for a flag the enemy holds
            if (_m.Flags != null && _m.Flags.ChasingEnemy(b)) return true;
            if (b.Slot != null && b.Slot.Intent != null && (b.Slot.Intent.Act == CTFXAct.Raid || b.Slot.Intent.Act == CTFXAct.Storm)) return true;
            if (b.Group != null && b.Group.Storm != null && b.Group.Storm.Phase == CTFXStorm.Hold && b.Group.Storm.Joined.Contains(b._id)) return true;
            return b.HasTarget && a != home;
        }

        // ================================================================ % team macros (Travis 10-13: "%commands so human players can
        // send those team chats as a shortcut"; the bots read them like any team chat)
        static readonly string[][] MacroTable = new string[][]
        {   // names, text ({0} = where the sender stands, {1} = what he typed after it), bong list
            new string[] { "att attack a",  "Attacking enemy {0}{1}",            "attack" },
            new string[] { "home h def",    "At home defending {0}{1}",          "" },
            new string[] { "inc i",         "Incoming at {0}!{1}",               "" },
            new string[] { "x xx xxx",      "XXX{1}",                            "enemysum" },
            new string[] { "s ss sss",      "SSS{1}",                            "homesum" },
            new string[] { "sum summon",    "Summon please{1}",                  "sum" },
            new string[] { "flag f",        "Got the flag at {0}, sum me home{1}", "sum" },
            new string[] { "eng",           "Need an engineer at {0}{1}",        "" },
            new string[] { "med",           "Need a medic at {0}{1}",            "" },
            new string[] { "anchor",        "Anchoring {0}{1}",                  "" },
            new string[] { "storm",         "Storming {0}{1}",                   "attack" },
            new string[] { "tdown td",      "Turret down at {0}{1}",             "" },
        };

        /// <summary>A player %command: one of the team macros (team chat as him, with its sound), or %macros for the list.</summary>
        internal bool Comm(Player p, string command, string payload)
        {
            if (p == null || p._team == null || string.IsNullOrEmpty(command)) return false;
            string c = command.ToLowerInvariant();
            if (c == "macros" || c == "macro" || c == "cmds")
            {
                StringBuilder sb = new StringBuilder("Team macros: ");
                foreach (string[] m in MacroTable) sb.Append('%').Append(m[0].Split(' ')[0]).Append(' ');
                p.sendMessage(0, sb.ToString().Trim() + " (add words after it to say more)");
                return true;
            }
            foreach (string[] m in MacroTable)
            {
                bool hit = false;
                foreach (string n in m[0].Split(' ')) if (n == c) hit = true;
                if (!hit) continue;
                string where = Place(_m.AreaAt(p._state.positionX, p._state.positionY));
                if (where == "out") where = "outside";
                string text = string.Format(m[1], where, string.IsNullOrEmpty(payload) ? "" : " - " + payload.Trim());
                List<int> bl = m[2] == "attack" ? AttackBongs : m[2] == "sum" ? SummonBongs : m[2] == "enemysum" ? EnemySummonBongs : m[2] == "homesum" ? HomeSummonBongs : null;
                byte bong = (byte)(bl != null && bl.Count > 0 ? bl[0] : 0);
                SC_Chat chat = new SC_Chat();
                chat.chatType = Helpers.Chat_Type.Team; chat.from = p._alias; chat.message = text; chat.bong = bong;
                foreach (Player t in p._team.getChatTargets()) if (t != null && t._client != null) t._client.sendReliable(chat);
                Macros++;
                Hear(p, text, bong);                                    // his own team bots read it as well
                return true;
            }
            return false;
        }

        /// <summary>Not holding a job the team gave it: no anchor, defender, post, engineer at work, storm squad member.</summary>
        bool Free(CTFXBot b)
        {
            CTFXSlot s = b.Slot;
            if (s.AttachTo != null) return false;
            if (s.Style == CTFXStyle.Post || s.Style == CTFXStyle.Engineer || s.Style == CTFXStyle.Stormer) return false;
            if (s.Intent != null)
            {
                CTFXAct a = s.Intent.Act;
                if (a == CTFXAct.Anchor || a == CTFXAct.Defend || a == CTFXAct.Build || a == CTFXAct.Storm) return false;
            }
            if (s.Group != null && s.Group.Storm != null && (s.Style == CTFXStyle.Assault)) return false;
            return true;
        }

        // ================================================================ step (manager poll, every 250 ms)
        internal void Step(int now)
        {
            if (!On || now - _lastStep < 250) return;
            _lastStep = now;
            for (int i = 0; i < _pending.Count; )
            {
                Pending q = _pending[i];
                if (now - q.Due < 0) { i++; continue; }
                _pending.RemoveAt(i);
                CTFXBot b = _m.GetBot(q.Slot.BotId);
                Player p = q.From;
                if (b == null || b.IsDead || b.Released || b.InDropship || p == null || p.IsDead || p.IsSpectator || p._team != b._team || p._arena != _m.Arena)
                {
                    if (q.Summon) SumLost++;                                // the summoner died or he did before it answered
                    continue;
                }
                if (q.Summon)
                {
                    if (_answered.Contains(q.Ask)) continue;                // someone else already pulled him in
                    if (_m.Squads.SummonHuman(b, p, now))
                    {
                        _answered.Add(q.Ask);
                        Summoned++;
                        if (_m.Mini != null) Say(b, Pick("Acknowledged --[" + p._alias + "]--", "Acknowledged --[" + p._alias + "]--", "Summoning --[" + p._alias + "]--"), 0.9, true, 22);      // MiniTP: the players' own macro (Acknowledged %22)
                        else Say(b, q.Kind == SumHome ? Pick("summoning home " + p._alias, "sum home " + p._alias)
                            : q.Kind == SumEnemy ? Pick("summoning in " + p._alias, "sum in, " + Place(_m.AreaAt(b._state.positionX, b._state.positionY)))
                            : Pick("summoning " + p._alias, "sum " + p._alias, "got you " + p._alias), 0.5, true);
                    }
                    else
                    {   // a summoner who tried says why, as a player would (one line per request)
                        string why = _m.Squads.LastSummonRefusal ?? "";
                        string line = why.StartsWith("he is anti") ? Pick("cant sum, anti on you", "anti on you " + p._alias, "ur antid")
                            : why.StartsWith("summoner anti") ? Pick("anti on me cant sum", "im antid")
                            : why.StartsWith("summoner energy") ? Pick("no energy to sum", "out of energy, 1 sec")
                            : why.StartsWith("summoner recharging") ? Pick("sum recharging", "recharging, ask again") : null;
                        if (_m.Mini != null && why.StartsWith("summoner anti") && q.Retry < 2)
                        {   // MiniTP: the summoner stands inside an enemy Teleport Disruptor: it walks out of its range and the ask is tried again by itself (Pub night 11-07: "no ignores")
                            _m.Mini.RelocForSummon(b, now);
                            q.Due = now + 4500 + _m.Rng.Next(1500); q.Retry++;
                            _pending.Add(q);
                            if (line != null) Say(b, line, 0.6, true);
                        }
                        else if (line != null && Say(b, line, 0.6, true) && _m.Mini == null) _answered.Add(q.Ask);
                    }
                }
                else
                {
                    if (_m.Mini == null && !Free(b)) continue;                // MiniTP: the seat logic picked who comes (MiniAttack)
                    AttachTo(b, p, now, true);
                    if (q.Flag && _m.Flags != null) _m.Flags.JoinNearestFlag(b, p);
                }
            }
            if (_answered.Count > 64) _answered.Clear();
            // attached bots: drop the ones whose time or caller is gone; re-attach to a caller who warped far away
            foreach (CTFXBot b in _m.LiveBots)
            {
                CTFXSlot s = b.Slot;
                if (s == null || s.AttachTo == null) continue;
                Player p = s.AttachTo;
                bool gone = p._arena != _m.Arena || p.IsSpectator || p._team != b._team;
                // an anchor / defender goes back to its post - unless he summoned it with his own summoner (10-21, Waylander: "I summon them
                // and they just walk away"): that one stays with him for the whole attachment
                if (gone || now - s.AttachUntil >= 0 || (!s.AttachSummoned && s.Intent != null && (s.Intent.Act == CTFXAct.Anchor || s.Intent.Act == CTFXAct.Defend)))
                {
                    s.AttachTo = null; s.AttachSummoned = false; b.ForceGoal(); continue;
                }
                if (p.IsDead) { if (s.AttachUntil - now > 6000) s.AttachUntil = now + 6000; continue; }   // he died: it lingers a little
                if (b.IsDead || b.InDropship) continue;
                double dx = p._state.positionX - b._state.positionX, dy = p._state.positionY - b._state.positionY, d = Math.Sqrt(dx * dx + dy * dy);
                if (d > 1500.0 && now - b.WarpReadyAt >= 0 && !b.HasTarget && _m.Rng.NextDouble() < 0.25)
                {
                    if (_m.Squads.TeleportToPx(b, p._state.positionX, p._state.positionY, now, "attach")) Reattached++;
                }
                // he has moved on from where it is headed: follow
                int gx, gy;
                if (b.GoalPx(out gx, out gy))
                {
                    double ex = p._state.positionX - gx, ey = p._state.positionY - gy;
                    if (ex * ex + ey * ey > 320.0 * 320.0) b.ForceGoal();
                }
            }
        }

        /// <summary>b attaches to human p: Suit Teleport onto him when far, then fights around him for a while.</summary>
        internal void AttachTo(CTFXBot b, Player p, int now, bool reply)
        {
            CTFXSlot s = b.Slot;
            CTFXHuman h = s.Human;
            s.AttachTo = p;
            s.AttachSummoned = false;                                   // SummonBotByPlayer sets it after this, for his own summons only
            s.AttachUntil = now + 60000 + _m.Rng.Next(90001);
            Attached++;
            double dx = p._state.positionX - b._state.positionX, dy = p._state.positionY - b._state.positionY;
            if (dx * dx + dy * dy > 500.0 * 500.0) _m.Squads.TeleportToPx(b, p._state.positionX, p._state.positionY, now, "attach");   // warps to him
            b.ForceGoal();
            if (reply) Say(b, Pick("omw", "coming", "with you " + p._alias, "on you"), 0.3, true);
        }

        /// <summary>The attached bot goal: an open spot 60-220 px around the human it is with. False = not attached.</summary>
        internal bool AttachGoal(CTFXBot b, out int tx, out int ty)
        {
            tx = 0; ty = 0;
            CTFXSlot s = b.Slot;
            if (s == null || s.AttachTo == null || s.AttachTo.IsDead) return false;
            Player p = s.AttachTo;
            double a = _m.Rng.NextDouble() * Math.PI * 2, r = 60 + _m.Rng.Next(161);
            _m.NearestOpenPx(p._state.positionX + (int)(Math.Cos(a) * r), p._state.positionY + (int)(Math.Sin(a) * r), out tx, out ty);
            return true;
        }

        internal string Summary()
        {
            if (!On) return "";
            StringBuilder sb = new StringBuilder(" | chat: said ").Append(Said).Append(" (no one to hear ").Append(Unheard).Append("), heard ").Append(Heard)
                .Append(", sum asks ").Append(SumAsks).Append(" summoned ").Append(Summoned).Append(" (refused ").Append(_m.Squads.SummonRefusals).Append(", no summoner on the team ").Append(SumNone).Append(", none noticed ").Append(SumFailed).Append(", noticed ").Append(SumHeard).Append(", lost before the answer ").Append(SumLost)
                .Append("), attack calls ").Append(Calls).Append(" (near a flag ").Append(FlagCalls).Append(')')
                .Append(", home calls %28 ").Append(HomeCalls).Append(" / %26 ").Append(HomeSightings).Append(" (raised the alarm ").Append(HomeCallsHeard).Append(')').Append(" attached ").Append(Attached).Append(" re-attached ").Append(Reattached)
                .Append(", summoned by a player ").Append(ItemSummons).Append(" | X asks ").Append(SumEnemyAsks).Append(", S asks ").Append(SumHomeAsks)
                .Append(", % macros ").Append(Macros);
            return sb.ToString();
        }

        internal IEnumerable<string> Recent { get { return _recent; } }
    }
}
/* PARSER GUARD - keep last */
