using System.Collections.Generic;

using Axiom.Math;

namespace InfServer.Script.GameType_Eol
{
    /// <summary>Pathfinder fields passed without ref into pathfinder callbacks (script compiler CS1628).</summary>
    internal struct EolBotPathState
    {
        public List<Vector3> Path;
        public int PathTarget;
        public int TickLastPath;
        /// <summary>When true, <see cref="Path"/> / <see cref="PathTarget"/> are written even if Path is null (clear).</summary>
        public bool AssignPath;
    }
}
