using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using InfServer.Bots;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>
    /// Local pool of display strings for Eol bots (assign in bot ctor). GitHub server build has no <c>Bot._alias</c>;
    /// this registry is the only bot display name source for Eol.
    /// </summary>
    public static class EolBotNameRegistry
    {
        static readonly object _lock = new object();
        static readonly HashSet<string> _inUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<Bot, string> _botToName = new Dictionary<Bot, string>();
        static int _fallbackSeq;

        static readonly string[] DefaultNames =
        {
            "Tom Bot", "Bob Bot", "Sandy Bot", "Betty Bot", "Dan Bot", "Jess Bot", "Jim Bot", "Ted Bot", "Sam Bot", "Adam Bot",
            "Luke Bot", "Lee Bot", "Kelly Bot", "Amy Bot", "Eva Bot", "Eli Bot", "Zack Bot", "Leo Bot", "Zoe Bot", "Ivy Bot",
            "Mia Bot", "Rory Bot", "Remi Bot", "Ian Bot", "Jude Bot", "Niko Bot", "Finn Bot", "Ares Bot", "Wade Bot", "Cali Bot",
            "Karen Bot", "Jeb Bot", "Frank Bot", "Karl Bot", "Tim Bot", "Bill Bot", "Mike Bot", "Captain Bot", "Queen Bot", "King Bot",
            "Leader Bot", "Fred Bot", "Phil Bot", "Ro Bot", "Archie Bot", "Tory Bot", "Alex Bot", "Nao Bot", "Milo Bot", "Doc Bot",
            "Ryder Bot", "Frodo Bot", "Joe Bot", "Evan Bot", "Hand Bot", "Steve Bot", "Rita Bot", "Shelly Bot", "Teller Bot", "Wick Bot",
            "Calvin Bot", "Habs Bot", "Roe Bot", "Ross Bot", "Gandalf Bot", "Sauron Bot", "Arwen Bot", "Bilbo Bot", "Gimli Bot", "Gollum Bot",
            "Legolas Bot", "Pippin Bot", "Elrond Bot", "Saruman Bot", "Samwise Bot", "Theoden Bot", "Boromir Bot", "Faramir Bot", "Galadriel Bot", "Eowyn Bot",
            "Merry Bot", "Harry Bot", "Ron Bot", "Hermione Bot", "Luna Bot", "Sirius Bot", "Draco Bot", "Cedric Bot", "Hagrid Bot", "Snape Bot",
            "Sin Bot", "Sun Bot", "Sharon Bot", "Ken Bot", "Barbie Bot", "Chuck Bot", "Orla Bot", "Lia Bot", "Sky Bot", "Bo Bot",
            "Peter Bot", "Pat Bot", "NOD Bot", "Mode Bot", "Ace Bot", "Jovan Bot", "GDI Bot", "Kaze Bot", "DP Bot", "Kev Bot",
            "Emp Bot", "Deeks Bot"
        };

        static readonly List<string> NormalizedPool = DefaultNames
            .Select((string n) => n.Length > 31 ? n.Substring(0, 31) : n)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        public static void Initialize()
        {
            lock (_lock)
            {
                _inUse.Clear();
                _botToName.Clear();
            }
        }

        /// <summary>Call from bot ctor after <c>base(...)</c>.</summary>
        public static void AssignToBot(Bot bot)
        {
            if (bot == null)
                return;
            lock (_lock)
            {
                if (_botToName.ContainsKey(bot))
                    return;

                string chosen = null;
                foreach (string n in NormalizedPool)
                {
                    if (!_inUse.Contains(n))
                    {
                        chosen = n;
                        break;
                    }
                }

                if (chosen == null)
                    chosen = "EolBot-" + Interlocked.Increment(ref _fallbackSeq);

                _inUse.Add(chosen);
                _botToName[bot] = chosen;
            }
        }

        /// <summary>Display string for messages; falls back to vehicle type name if unregistered.</summary>
        public static string GetDisplayName(Bot bot)
        {
            if (bot == null)
                return "?";
            // Alien wave bots use vehicle type name only (no pool names).
            if (bot is AlienHorde)
                return bot._type != null ? bot._type.Name : "?";
            lock (_lock)
            {
                string name;
                if (_botToName.TryGetValue(bot, out name) && !string.IsNullOrEmpty(name))
                    return name;
            }
            return bot._type != null ? bot._type.Name : "?";
        }

        /// <summary>Killfeed / arena line: same as <see cref="GetDisplayName"/> (registry name; no server alias field).</summary>
        public static string GetKillfeedName(Bot bot)
        {
            if (bot == null)
                return "Bot";
            return GetDisplayName(bot);
        }

        /// <summary>Return name to pool. Call from <c>Bot.Death</c> after notifications that need the name.</summary>
        public static void Release(Bot bot)
        {
            if (bot == null)
                return;
            lock (_lock)
            {
                string a;
                if (!_botToName.TryGetValue(bot, out a))
                    return;
                _botToName.Remove(bot);
                if (!string.IsNullOrEmpty(a))
                    _inUse.Remove(a);
            }
        }
    }
}
