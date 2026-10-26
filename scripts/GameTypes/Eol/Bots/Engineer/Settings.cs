using InfServer.Bots;

namespace InfServer.Script.GameType_Eol
{
    public partial class Engineer : Bot
    {
        /// <summary>Vehicle chassis for the defense engineer.</summary>
        public const ushort EngineerVehicleId = 463;

        /// <summary>How often to re-evaluate which turret needs repair.</summary>
        public const int RetargetIntervalMs = 2500;

        /// <summary>Path requeue while traveling to a damaged turret.</summary>
        public const int ApproachPathRequeueMs = 2800;

        /// <summary>Path requeue while walking the healthy turret circuit.</summary>
        public const int CircuitPathRequeueMs = 2200;

        /// <summary>Close enough to a circuit stop to count as arrived (raw units).</summary>
        public const int CircuitArriveRangeRaw = 72;

        /// <summary>Stand at each turret briefly before moving to the next.</summary>
        public const int CircuitDwellMs = 2800;

        /// <summary>
        /// When the repair item has repairDistance == 0, require this proximity (raw)
        /// so the eng is effectively on top of the turret.
        /// </summary>
        public const int OnTopFallbackRangeRaw = 48;

        /// <summary>Energy cost channel used when terrain-specific costs are unset.</summary>
        public const int DefaultEnergyCostIndex = 0;
    }
}
