using System.Collections.Generic;
using Assets;

namespace InfServer.Bots
{
    public struct BotUtilityMovementModifiers
    {
        public int TopSpeedBonus;
        public int ThrustBonus;
        public int StrafeThrustBonus;
        public int ForwardThrustBonus;
        public int ReverseThrustBonus;
        public int RotateBonus;
    }

    /// <summary>
    /// Aggregates active utility item stats for movement and damage.
    /// </summary>
    public static class BotUtilityEffects
    {
        public enum DamageChannel
        {
            Kinetic = 0,
            Explosive = 1,
            Electronic = 2,
            Psionic = 3,
            Bypass = 4,
            Energy = 5,
        }

        public static BotUtilityMovementModifiers ComputeMovementModifiers(IEnumerable<ItemInfo.UtilityItem> utilities)
        {
            var mods = new BotUtilityMovementModifiers();

            foreach (ItemInfo.UtilityItem util in utilities)
            {
                mods.TopSpeedBonus += util.vehicleTopSpeed;
                mods.ThrustBonus += util.vehicleThrust;
                mods.StrafeThrustBonus += util.strafeThrust;
                mods.ForwardThrustBonus += util.forwardThrust;
                mods.ReverseThrustBonus += util.reverseThrust;
                mods.RotateBonus += util.vehicleRotate;
            }

            return mods;
        }

        /// <summary>
        /// Applies vehicle base armor then stacks active utility ignore/percent for one damage channel.
        /// </summary>
        public static double ApplyArmorChannel(
            double grossDamage,
            ArmorValues vehicleArmor,
            int utilityIgnore,
            int utilityPercent)
        {
            double net = (grossDamage - vehicleArmor.SelfIgnore) * (1.0d - vehicleArmor.SelfReduction / 1000.0d);
            if (net <= 0)
                return 0;

            net = (net - utilityIgnore) * (1.0d - utilityPercent / 1000.0d);
            return net > 0 ? net : 0;
        }

        public static void AccumulateUtilityArmor(
            IEnumerable<ItemInfo.UtilityItem> utilities,
            out int[] ignore,
            out int[] percent)
        {
            ignore = new int[6];
            percent = new int[6];

            foreach (ItemInfo.UtilityItem util in utilities)
            {
                ignore[(int)DamageChannel.Kinetic] += util.kineticIgnore;
                percent[(int)DamageChannel.Kinetic] += util.kineticPercent;

                ignore[(int)DamageChannel.Explosive] += util.explosiveIgnore;
                percent[(int)DamageChannel.Explosive] += util.explosivePercent;

                ignore[(int)DamageChannel.Electronic] += util.electronicIgnore;
                percent[(int)DamageChannel.Electronic] += util.electronicPercent;

                ignore[(int)DamageChannel.Psionic] += util.psionicIgnore;
                percent[(int)DamageChannel.Psionic] += util.psionicPercent;

                ignore[(int)DamageChannel.Bypass] += util.bypassIgnore;
                percent[(int)DamageChannel.Bypass] += util.bypassPercent;

                ignore[(int)DamageChannel.Energy] += util.energyIgnore;
                percent[(int)DamageChannel.Energy] += util.energyPercent;
            }

            for (int i = 0; i < 6; i++)
            {
                if (percent[i] > 1000)
                    percent[i] = 1000;
            }
        }
    }
}
