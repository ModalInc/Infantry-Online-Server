// MiniTP bots - GAMER TAGS (Travis 11-07: "rename these bots to something other than the CTFX bots, a new naming convention more like 2000-era gamer tags, more variety").
// The CTF:X bots are "Jett 45" (a callsign and a number). MiniTP bots get the kind of handle a pub player had around 2000-2004: a plain word, a word and a
// year or number, two words run together, leet spelling, an xX_..._Xx wrapper, a made-up clan tag in front, a title, lowercase, tildes and dots, a silly food word.
// Deterministic: tag number k is the same on every boot (the personality is seeded by the name), no two alike, <= 16 characters, nothing a pub would filter, none of the
// numbers that read as something else, no real clan or player names (the tags are made up).
/* PARSER GUARD: keep a block comment after the last line comment */

using System;
using System.Text;

namespace InfServer.Script.GameType_CTF
{
    internal static class CTFXGamerTags
    {
        static readonly string[] Handles =
        {
            "Viper", "Havoc", "Nomad", "Reaper", "Phantom", "Raven", "Cobra", "Falcon", "Blaze", "Shadow", "Ghost", "Hunter", "Striker", "Maverick", "Outlaw",
            "Bandit", "Renegade", "Gladiator", "Predator", "Warlock", "Demon", "Dragon", "Wolf", "Tiger", "Panther", "Scorpion", "Hawk", "Eagle", "Phoenix", "Zero",
            "Omega", "Alpha", "Delta", "Nova", "Vortex", "Static", "Frost", "Inferno", "Thunder", "Lightning", "Storm", "Cyclone", "Venom", "Toxic", "Rogue",
            "Rebel", "Vandal", "Slayer", "Crusher", "Wrecker", "Bomber", "Gunner", "Trooper", "Ranger", "Spartan", "Samurai", "Ninja", "Pirate", "Cowboy", "Hammer",
            "Anvil", "Bullet", "Dagger", "Razor", "Spike", "Fang", "Talon", "Claw", "Jinx", "Mojo", "Chaos", "Rampage", "Bruiser", "Brawler", "Tornado", "Avalanche",
            "Cipher", "Enigma", "Specter", "Wraith", "Banshee", "Gargoyle", "Goliath", "Cyclops", "Minotaur", "Kraken", "Hydra", "Griffin", "Basilisk", "Sphinx",
            "Comet", "Meteor", "Eclipse", "Zenith", "Apex", "Vertex", "Matrix", "Nexus", "Pulse", "Surge", "Voltage", "Turbo", "Nitro", "Rocket", "Missile",
            "Bishop", "Rook", "Pawn", "Joker", "Wildcard", "Gambit", "Ace", "Drifter", "Wanderer", "Scout", "Pathfinder", "Tracker"
        };

        static readonly string[] Adjectives =
        {
            "Dark", "Silent", "Crazy", "Iron", "Steel", "Black", "Red", "Cold", "Lone", "Mad", "Angry", "Sneaky", "Lucky", "Wicked", "Savage", "Rapid", "Deadly",
            "Quiet", "Frozen", "Burning", "Rusty", "Atomic", "Cosmic", "Hyper", "Mega", "Ultra", "Turbo", "Rabid", "Shady", "Slick", "Swift", "Brutal", "Nasty",
            "Grim", "Stone", "Silver", "Golden", "Crimson", "Midnight", "Electric", "Toxic", "Savage"
        };

        static readonly string[] Nouns =
        {
            "Wolf", "Hawk", "Fist", "Blade", "Skull", "Viper", "Storm", "Ghost", "Knight", "Ranger", "Rider", "Hunter", "Dagger", "Bullet", "Fury", "Shark",
            "Raptor", "Bear", "Fox", "Pilot", "Spider", "Falcon", "Cobra", "Tiger", "Dragon", "Reaper", "Soldier", "Warrior", "Bandit", "Sniper", "Angel", "Devil",
            "Hammer", "Anvil", "Phantom", "Thunder", "Lightning", "Wizard", "Monk", "Samurai"
        };

        static readonly string[] Silly =
        {
            "Spork", "Waffle", "Pickle", "Noodle", "Biscuit", "Muffin", "Tater", "Pretzel", "Nacho", "Bagel", "Taco", "Pancake", "Gravy", "Sprout", "Pudding",
            "Cheddar", "Meatball", "Sandwich", "Cupcake", "Marshmallow", "Banana", "Turnip", "Walnut", "Pepper", "Pickles"
        };

        static readonly string[] Titles = { "Mr", "Dr", "Sgt", "Cpt", "Lil", "Big", "DJ", "Prof", "Gen", "Agent", "Uncle" };

        static readonly string[] ClanTags = { "NFX", "DRK", "WLF", "KRZ", "SLX", "VPR", "HXM", "OZT", "ZRK", "TMX", "BLK", "RGN", "XTR", "PHX", "DGR", "SNK", "FTL", "ORB", "GRX", "ZNT" };

        // numbers a pub tag might carry (none that read as something else)
        static readonly string[] Numbers =
        {
            "1", "2", "3", "5", "7", "9", "10", "11", "12", "13", "21", "22", "23", "24", "27", "33", "34", "42", "44", "45", "55", "57", "64", "72", "77", "78", "81",
            "82", "84", "86", "92", "93", "94", "95", "96", "97", "98", "99", "00", "01", "02", "03", "04", "2k", "2K", "k2", "007"
        };

        // Travis 11-08: a few fixed "AzN~" handles (a classic pub clan prefix), made up here, not anyone's real name. Tag numbers 1, 9 and 17 always get these, so one is on
        // as soon as two bots are; everything else is generated as before. NameFor skips a name a player already has and moves on to the next number.
        static readonly string[] Pinned = { "AzN~Dumpling", "AzN~Mochi", "AzN~Wasabi" };

        static string Cap(string s) { return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1); }

        static string Leet(string s, Random r, double p)
        {
            StringBuilder b = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                char o = c;
                if (r.NextDouble() < p)
                    switch (char.ToLowerInvariant(c))
                    {
                        case 'a': o = '4'; break;
                        case 'e': o = '3'; break;
                        case 'i': o = '1'; break;
                        case 'o': o = '0'; break;
                        case 's': o = '5'; break;
                    }
                b.Append(o);
            }
            return b.ToString();
        }

        static string Pick(string[] a, Random r) { return a[r.Next(a.Length)]; }

        /// <summary>Gamer tag number k (the same k, the same tag, every boot).</summary>
        internal static string Make(int k)
        {
            if (k >= 1 && k % 8 == 1 && k / 8 < Pinned.Length) return Pinned[k / 8];
            Random r = new Random(unchecked(k * 2654435 + 40503));
            string tag = null;
            for (int attempt = 0; attempt < 8 && (tag == null || tag.Length > 16 || tag.Length < 3); attempt++)
            {
                string h = Pick(Handles, r), h2 = Pick(Handles, r), adj = Pick(Adjectives, r), noun = Pick(Nouns, r), num = Pick(Numbers, r);
                double u = r.NextDouble();
                if (u < 0.13) tag = h;                                                                                  // Viper
                else if (u < 0.29) tag = h + (r.Next(3) == 0 ? "_" : "") + num;                                         // Havoc77  Raven_03
                else if (u < 0.40) tag = adj + noun;                                                                    // IronFist
                else if (u < 0.46) tag = adj + "_" + noun;                                                              // Dark_Wolf
                else if (u < 0.52) tag = h + h2;                                                                        // NightHawk-ish
                else if (u < 0.63) tag = Leet(r.Next(2) == 0 ? h : adj + noun, r, 0.7);                                // Ph4nt0m  Cr1m50nF15t
                else if (u < 0.69) tag = "xX" + (r.Next(2) == 0 ? h : adj + noun) + "Xx";                               // xXReaperXx
                else if (u < 0.74) tag = "xX_" + h + "_Xx";                                                             // xX_Havoc_Xx
                else if (u < 0.84)
                {   // a made-up clan tag in front: [WLF]Havoc  =NFX=Raven  -KRZ-Viper  ~PHX~Nova
                    string t = Pick(ClanTags, r), body = r.Next(3) == 0 ? h + num : h;
                    int style = r.Next(5);
                    tag = style == 0 ? "[" + t + "]" + body : style == 1 ? "=" + t + "=" + body : style == 2 ? "-" + t + "-" + body : style == 3 ? "~" + t + "~" + body : t + "_" + body;
                }
                else if (u < 0.89) tag = Pick(Titles, r) + (r.Next(2) == 0 ? "_" : "") + (r.Next(3) == 0 ? Pick(Silly, r) : h);   // Mr_Havoc  DrVenom  Lil_Waffle
                else if (u < 0.93) tag = (r.Next(2) == 0 ? h : adj + noun).ToLowerInvariant();                          // nightwolf
                else if (u < 0.96) tag = r.Next(3) == 0 ? "~" + h + "~" : r.Next(2) == 0 ? "_" + h + "_" : h.Substring(0, 1) + "." + Cap(h2);   // ~Vortex~  _Static_  K.Rad-ish
                else if (u < 0.985) tag = Pick(Silly, r) + (r.Next(2) == 0 ? num : "_" + noun);                         // Waffle99  Pickle_Wolf
                else tag = h.ToUpperInvariant();                                                                        // BLAZE
            }
            if (tag == null || tag.Length > 16 || tag.Length < 3) tag = Pick(Handles, r) + Pick(Numbers, r);
            if (tag.Length > 16) tag = tag.Substring(0, 16);
            return tag;
        }
    }
}
