namespace InfServer.Bots.NavMesh
{
    /// <summary>
    /// A single navmesh triangle with adjacency through shared edges.
    /// </summary>
    public struct NavTriangle
    {
        /// <summary>Vertex indices into the parent mesh vertex list.</summary>
        public int V0, V1, V2;

        /// <summary>Neighbor triangle indices for edges V0-V1, V1-V2, V2-V0 (-1 if none).</summary>
        public int N0, N1, N2;

        public float CentroidX;
        public float CentroidY;

        public NavTriangle(int v0, int v1, int v2)
        {
            V0 = v0;
            V1 = v1;
            V2 = v2;
            N0 = N1 = N2 = -1;
            CentroidX = CentroidY = 0;
        }
    }
}
