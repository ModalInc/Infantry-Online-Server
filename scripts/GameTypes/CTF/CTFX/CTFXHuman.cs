// CTF:X server-side bots - the HUMAN LAYER: each seat's player-like traits (Travis 10-12: "not swarmy, not instantaneous reaction
// time and instant perfect target selection, not the same roaming paths or styles, even within the same teams").
//
// A seat rolls one profile at its first spawn and keeps it for every life, the way a player keeps his habits:
//   tier       rookie 25 % / regular 45 % / veteran 22 % / ace 8 %, so every team is a mix
//   reaction   ms from an enemy coming into view (or a hit from off-screen) to the bot turning on it, + more for a bigger turn
//   settle     ms from facing a new target to the first shot
//   aim        a held aim bias (re-drawn every 0.6-1.6 s, shrinking while it tracks), per-shot noise, and a per-engagement
//              lead error (over- or under-leading a moving target by a fraction, shrinking while it tracks)
//   view       the screen it sees: 420 x 320 px each way at 1.0 (an enemy outside it is not seen until it shoots the bot)
//   stick      a new target must score this fraction of the current one to pull the bot off it
//   lane/arrive its own line through the route book: px left or right of each route line, and how early it cuts a corner
//
// CTFXBot reads it (ScanTargets, Think's aim, RouteMove). Rolls come from the seat's own seed, so a respawn is the same "player".
//
// DRIVE (Travis 10-12: "a lot more wandering autonomy for individual players even on teams, they work way too well together"):
//   taste      a personal liking per activity (CTFXAct: skirmish, raid, wander, hunt, anchor), 0.4-1.6 on top of its style's
//   comply     0.25-0.95: how readily it does what the team asks (hold a choke, answer a base alarm)
//   alarm      ms before it reacts to a base alarm at all (it has to notice the call), 1.5-9 s
//   bored      ms one activity holds it before it picks again, 35-150 s
//   restless   its pause at a goal, x0.6 (fidgety) to x1.5 (patient)
// CTFXCommand reads it.
/* PARSER GUARD: keep a block comment after the last line comment */

using System;

namespace InfServer.Script.GameType_CTF
{
    internal sealed class CTFXHuman
    {
        public string Tier;
        public int ReactMean, ReactSd;      // ms
        public int TurnPer45;               // extra ms per 45 degrees of turn (noticing what is behind; the turn itself is physics)
        public int SettleLo, SettleHi;      // ms, first shot after facing a new target
        public double AimSd;                // yaw units (1.5 deg) of the held aim bias
        public double ShotSd;               // yaw units of per-shot noise
        public double LeadSd;               // fraction of the lead misjudged per engagement
        public double View;                 // screen-awareness scale
        public double Stick;                // switch only when the new score is below Stick x the current one
        public int Lane;                    // px off the route lines (+ = right of the way it walks)
        public int Arrive;                  // px from a waypoint at which it heads on to the next
        public int Seed;
        // drive (CTFXCommand)
        public double[] Taste;              // per CTFXAct index (None .. Outside); 1 = indifferent
        public double Comply;               // 0.25-0.95
        public int AlarmLo, AlarmHi;        // ms
        public int BoredLo, BoredHi;        // ms
        public double Restless;             // pause scale

        // Travis 10-22 (Galaxy: "Jett 45 the medic ... doesn't stay home"): a seat rolls from a seed made of its FINAL callsign, so the same name
        // is the same player every boot and every *ctfxbots on (tier, reaction, aim, tastes, comply). FNV-1a 32-bit over the name's UTF-8
        // bytes: string.GetHashCode is randomized per process in .NET, so it cannot be used. No name = the old random seed.
        internal static int NameSeed(string name)
        {
            uint h = 2166136261u;
            foreach (byte c in System.Text.Encoding.UTF8.GetBytes(name)) { h ^= c; h *= 16777619u; }
            return (int)(h & 0x7FFFFFFFu);
        }

        /// <summary>Travis 11-08: "bot level difficulty easy to adjust by a moderator so we can test all really good bots, or a spread of skill". *bots skill: mix (the natural spread),
        /// rookie / regular / veteran / ace (everybody that tier), elite (veterans and aces), weak (rookies and regulars). Only the tier roll changes: the seed still gives each callsign
        /// its other traits (lane, taste, timing jitter).</summary>
        internal static string SkillMode = "mix";
        internal static bool SetSkillMode(string m)
        {
            switch (m)
            {
                case "mix": case "rookie": case "regular": case "veteran": case "ace": case "elite": case "weak": SkillMode = m; return true;
            }
            return false;
        }

        internal static CTFXHuman Roll(Random rng, string name = null)
        {
            CTFXHuman h = new CTFXHuman();
            h.Seed = string.IsNullOrEmpty(name) ? rng.Next() : NameSeed(name);
            Random r = new Random(h.Seed);
            double t = r.NextDouble();
            switch (SkillMode)
            {
                case "rookie": t = 0.10; break;
                case "regular": t = 0.50; break;
                case "veteran": t = 0.80; break;
                case "ace": t = 0.97; break;
                case "elite": t = 0.78 + t * 0.22; break;
                case "weak": t = t * 0.62; break;
            }
            if (t < 0.25)
            {
                h.Tier = "rookie"; h.ReactMean = 430; h.ReactSd = 110; h.TurnPer45 = 75; h.SettleLo = 220; h.SettleHi = 420;
                h.AimSd = 3.2; h.ShotSd = 1.6; h.LeadSd = 0.45; h.View = 0.85; h.Stick = 0.55;
            }
            else if (t < 0.70)
            {
                h.Tier = "regular"; h.ReactMean = 310; h.ReactSd = 80; h.TurnPer45 = 50; h.SettleLo = 140; h.SettleHi = 300;
                h.AimSd = 2.3; h.ShotSd = 1.2; h.LeadSd = 0.30; h.View = 1.0; h.Stick = 0.65;
            }
            else if (t < 0.92)
            {
                h.Tier = "veteran"; h.ReactMean = 240; h.ReactSd = 60; h.TurnPer45 = 35; h.SettleLo = 90; h.SettleHi = 210;
                h.AimSd = 1.6; h.ShotSd = 0.9; h.LeadSd = 0.20; h.View = 1.1; h.Stick = 0.75;
            }
            else
            {
                h.Tier = "ace"; h.ReactMean = 195; h.ReactSd = 45; h.TurnPer45 = 25; h.SettleLo = 60; h.SettleHi = 150;
                h.AimSd = 1.1; h.ShotSd = 0.6; h.LeadSd = 0.12; h.View = 1.2; h.Stick = 0.8;
            }
            // the same tier still differs player to player: +-15 % on the timings and aim
            double k = 0.85 + r.NextDouble() * 0.3;
            h.ReactMean = (int)(h.ReactMean * k); h.SettleLo = (int)(h.SettleLo * k); h.SettleHi = (int)(h.SettleHi * k);
            h.AimSd *= 0.85 + r.NextDouble() * 0.3;
            h.Lane = (r.Next(2) == 0 ? -1 : 1) * (6 + r.Next(21));      // 6-26 px either side
            h.Arrive = 14 + r.Next(23);                                  // 14-36 px
            // drive: drawn after the rest so the traits above stay what each seed gave before
            h.Taste = new double[10];
            for (int i = 0; i < h.Taste.Length; i++) h.Taste[i] = 0.4 + r.NextDouble() * 1.2;
            int[] fav = new int[] { 3, 4, 5, 6, 9 };
            h.Taste[fav[r.Next(fav.Length)]] *= 1.6;                     // everyone has a favourite (skirmish, raid, wander, hunt, outside)
            h.Comply = 0.25 + r.NextDouble() * 0.7;
            h.AlarmLo = 1500 + r.Next(2500); h.AlarmHi = h.AlarmLo + 1500 + r.Next(5000);
            h.BoredLo = 35000 + r.Next(40000); h.BoredHi = h.BoredLo + 15000 + r.Next(60000);
            h.Restless = 0.6 + r.NextDouble() * 0.9;
            return h;
        }

        /// <summary>An elite seat (bountiers): a veteran or an ace, whatever the roll said; the rest of the profile is kept.</summary>
        internal void MakeElite(Random r)
        {
            if (Tier == "veteran" || Tier == "ace") return;
            bool ace = r.NextDouble() < 0.4;
            Tier = ace ? "ace" : "veteran";
            ReactMean = ace ? 195 : 240; ReactSd = ace ? 45 : 60; TurnPer45 = ace ? 25 : 35;
            SettleLo = ace ? 60 : 90; SettleHi = ace ? 150 : 210;
            AimSd = ace ? 1.1 : 1.6; ShotSd = ace ? 0.6 : 0.9; LeadSd = ace ? 0.12 : 0.20; View = ace ? 1.2 : 1.1; Stick = ace ? 0.8 : 0.75;
        }

        internal int AlarmMs(Random r) { return AlarmLo + r.Next(Math.Max(1, AlarmHi - AlarmLo + 1)); }

        internal int BoredMs(Random r) { return BoredLo + r.Next(Math.Max(1, BoredHi - BoredLo + 1)); }

        /// <summary>A normal draw (Box-Muller).</summary>
        internal static double Gauss(Random r, double mean, double sd)
        {
            double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble();
            return mean + sd * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        }

        /// <summary>ms before the bot answers an enemy it has to turn <paramref name="turnDeg"/> degrees to face.</summary>
        internal int ReactionMs(Random r, double turnDeg)
        {
            double ms = Gauss(r, ReactMean, ReactSd) + TurnPer45 * Math.Abs(turnDeg) / 45.0;
            return (int)Math.Max(120, Math.Min(1600, ms));
        }

        internal int SettleMs(Random r)
        {
            return SettleLo + r.Next(Math.Max(1, SettleHi - SettleLo + 1));
        }
    }
}
/* PARSER GUARD - keep last */
