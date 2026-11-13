using System;
using System.Text;

namespace InfServer.Bots
{
    /// <summary>
    /// A bot's player-like combat traits: reaction to a new target, settle before the first shot,
    /// a held aim bias and lead misjudgement that shrink while it tracks, and per-shot noise.
    /// Seeded from the bot's alias, so the same name plays the same way every boot.
    /// </summary>
    public sealed class BotSkillProfile
    {
        public string Tier { get; private set; }
        /// <summary>0 = rookie … 1 = ace.</summary>
        public double TierFactor { get; private set; }

        public int ReactMean { get; private set; }
        public int ReactSd { get; private set; }
        /// <summary>Extra ms per 45° the bot must turn to face a new target.</summary>
        public int TurnPer45 { get; private set; }
        public int SettleLo { get; private set; }
        public int SettleHi { get; private set; }
        /// <summary>Held aim bias sd, yaw units (1 unit = 1.5°).</summary>
        public double AimSd { get; private set; }
        /// <summary>Per-shot noise sd, yaw units.</summary>
        public double ShotSd { get; private set; }
        /// <summary>Fraction of the lead misjudged per engagement.</summary>
        public double LeadSd { get; private set; }

        BotSkillProfile() { }

        /// <summary>FNV-1a over the UTF-8 name (string.GetHashCode is randomised per process).</summary>
        public static int NameSeed(string name)
        {
            uint h = 2166136261u;
            foreach (byte c in Encoding.UTF8.GetBytes(name))
            {
                h ^= c;
                h *= 16777619u;
            }
            return (int)(h & 0x7FFFFFFFu);
        }

        public static BotSkillProfile Roll(string name, int fallbackSeed) => Roll(name, fallbackSeed, null);

        /// <summary><paramref name="modeOverride"/> (e.g. "ace") forces this bot's tier regardless of the zone mix.</summary>
        public static BotSkillProfile Roll(string name, int fallbackSeed, string modeOverride)
        {
            // name + id: bots are named after their class now ("Infantry 2"), so the name alone would give every
            // "Infantry" the same skill tier (local 2026-11-14)
            int seed = string.IsNullOrEmpty(name) ? fallbackSeed : (NameSeed(name) ^ fallbackSeed);
            Random r = new Random(seed);
            double t = r.NextDouble();
            switch (modeOverride ?? BotFeatures.SkillMode)
            {
                case "rookie": t = 0.10; break;
                case "regular": t = 0.50; break;
                case "veteran": t = 0.80; break;
                case "ace": t = 0.97; break;
                case "ultra": t = 0.995; break;   // assassins: beyond ace (tightened below)
                case "elite": t = 0.78 + t * 0.22; break;
                case "weak": t = t * 0.62; break;
            }

            BotSkillProfile p = new BotSkillProfile();
            if (t < 0.25)
                p.Set("rookie", 0.0, 430, 110, 75, 220, 420, 3.2, 1.6, 0.45);
            else if (t < 0.70)
                p.Set("regular", 0.4, 310, 80, 50, 140, 300, 2.3, 1.2, 0.30);
            else if (t < 0.92)
                p.Set("veteran", 0.7, 240, 60, 35, 90, 210, 1.6, 0.9, 0.20);
            else
                p.Set("ace", 1.0, 195, 45, 25, 60, 150, 1.1, 0.6, 0.12);

            // Same tier still differs bot to bot: ±15 % on timings and aim.
            double k = 0.85 + r.NextDouble() * 0.3;
            p.ReactMean = (int)(p.ReactMean * k);
            p.SettleLo = (int)(p.SettleLo * k);
            p.SettleHi = (int)(p.SettleHi * k);
            p.AimSd *= 0.85 + r.NextDouble() * 0.3;
            if ((modeOverride ?? BotFeatures.SkillMode) == "ultra")
            {   // ultra elite: half the aim error, faster reactions, near-perfect lead
                p.Tier = "ultra";
                p.AimSd *= 0.5;
                p.ShotSd *= 0.5;
                p.LeadSd *= 0.4;
                p.ReactMean = (int)(p.ReactMean * 0.7);
                p.SettleLo = (int)(p.SettleLo * 0.6);
                p.SettleHi = (int)(p.SettleHi * 0.6);
            }
            return p;
        }

        void Set(string tier, double factor, int reactMean, int reactSd, int turnPer45,
            int settleLo, int settleHi, double aimSd, double shotSd, double leadSd)
        {
            Tier = tier;
            TierFactor = factor;
            ReactMean = reactMean;
            ReactSd = reactSd;
            TurnPer45 = turnPer45;
            SettleLo = settleLo;
            SettleHi = settleHi;
            AimSd = aimSd;
            ShotSd = shotSd;
            LeadSd = leadSd;
        }

        /// <summary>Interpolate a value from the rookie end to the ace end by this bot's tier.</summary>
        public double TierPick(double rookie, double ace) => rookie + (ace - rookie) * TierFactor;

        /// <summary>ms before the bot answers a target it must turn <paramref name="turnDeg"/> degrees to face.</summary>
        public int ReactionMs(Random r, double turnDeg)
        {
            double ms = Gauss(r, ReactMean, ReactSd) + TurnPer45 * Math.Abs(turnDeg) / 45.0;
            return (int)Math.Max(120, Math.Min(1600, ms));
        }

        public int SettleMs(Random r) => SettleLo + r.Next(Math.Max(1, SettleHi - SettleLo + 1));

        /// <summary>Normal draw (Box-Muller).</summary>
        public static double Gauss(Random r, double mean, double sd)
        {
            double u1 = 1.0 - r.NextDouble();
            double u2 = r.NextDouble();
            return mean + sd * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        }
    }
}
