using System.Text.Json;
using System.Text.Json.Serialization;
using Assets;
using InfServer.Bots.NavMesh;

namespace NavMeshExporter;

internal static class Program
{
    private const int DefaultClearance = 3;

    private static readonly string[] PhysicsPalette =
    {
        "Clear",
        "Red Solid", "Red Upper Left", "Red Upper Right", "Red Lower Left", "Red Lower Right",
        "Green Solid", "Green Upper Left", "Green Upper Right", "Green Lower Left", "Green Lower Right",
        "Yellow Solid", "Yellow Upper Left", "Yellow Upper Right", "Yellow Lower Left", "Yellow Lower Right",
        "Orange Solid", "Orange Upper Left", "Orange Upper Right", "Orange Lower Left", "Orange Lower Right",
        "Purple Solid", "Purple Upper Left", "Purple Upper Right", "Purple Lower Left", "Purple Lower Right",
        "Red Move Right", "Red Move Left", "Red Move Down", "Red Move Up",
        "Teal Solid",
        "Blue Solid",
    };

    static int Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        string inputPath = args[0];
        string? outputPath = null;
        int clearance = DefaultClearance;

        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "-o" || args[i] == "--output")
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("Missing value for --output");
                    return 1;
                }

                outputPath = args[++i];
            }
            else if (args[i] == "-c" || args[i] == "--clearance")
            {
                if (i + 1 >= args.Length || !int.TryParse(args[++i], out clearance) || clearance < 0)
                {
                    Console.Error.WriteLine("Invalid --clearance value");
                    return 1;
                }
            }
        }

        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine("File not found: " + inputPath);
            return 1;
        }

        if (outputPath == null)
            outputPath = Path.ChangeExtension(inputPath, ".navmesh.json");

        try
        {
            LvlInfo level = LvlInfo.Load(inputPath);
            NavMeshExportDocument doc = BuildDocument(level, inputPath, clearance);

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            };
            string json = JsonSerializer.Serialize(doc, options);
            File.WriteAllText(outputPath, json);

            Console.WriteLine("Exported: " + outputPath);
            Console.WriteLine($"  Size: {level.Width}x{level.Height} tiles");
            Console.WriteLine($"  Clearance mesh: {doc.Stats.ClearanceTriangles} triangles ({doc.Stats.ClearanceVertices} vertices)");
            Console.WriteLine($"  Raw mesh: {doc.Stats.RawTriangles} triangles");
            Console.WriteLine($"  Walkable (clearance): {doc.Stats.ClearanceWalkablePercent:F1}%");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Export failed: " + ex.Message);
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    static void PrintUsage()
    {
        Console.WriteLine("NavMeshExporter — export .lvl nav mesh to JSON for the HTML viewer");
        Console.WriteLine();
        Console.WriteLine("Usage: NavMeshExporter <map.lvl> [-o output.json] [-c clearance]");
        Console.WriteLine("  -o, --output     Output path (default: <map>.navmesh.json)");
        Console.WriteLine($"  -c, --clearance  Bot clearance in tiles (default: {DefaultClearance})");
    }

    static NavMeshExportDocument BuildDocument(LvlInfo level, string sourcePath, int clearance)
    {
        int w = level.Width;
        int h = level.Height;
        int tileCount = w * h;

        var blocked = new byte[tileCount];
        var physics = new byte[tileCount];
        for (int i = 0; i < tileCount; i++)
        {
            blocked[i] = level.Tiles[i].Blocked ? (byte)1 : (byte)0;
            physics[i] = (byte)(level.Tiles[i].PhysicsVision & 0x1F);
        }

        bool[] rawMask = NavMeshBuilder.BuildWalkableMask(level, 0);
        bool[] clrMask = NavMeshBuilder.BuildWalkableMask(level, clearance);
        NavMeshData rawMesh = NavMeshBuilder.Build(level, rawMask);
        NavMeshData clrMesh = NavMeshBuilder.Build(level, clrMask);

        int rawWalk = 0;
        int clrWalk = 0;
        for (int i = 0; i < tileCount; i++)
        {
            if (rawMask[i])
                rawWalk++;
            if (clrMask[i])
                clrWalk++;
        }

        return new NavMeshExportDocument
        {
            Version = 1,
            Source = Path.GetFileName(sourcePath),
            Width = w,
            Height = h,
            TileSize = 16,
            Clearance = clearance,
            PhysicsPalette = PhysicsPalette,
            Layers = new LayerRleDocument
            {
                Blocked = RunLengthEncode(blocked),
                RawWalkable = RunLengthEncodeBool(rawMask),
                ClearanceWalkable = RunLengthEncodeBool(clrMask),
                Physics = RunLengthEncode(physics),
            },
            Vertices = clrMesh.Vertices
                .ConvertAll(v => new[] { v.X, v.Y }),
            Triangles = BuildTriangles(clrMesh),
            Stats = new StatsDocument
            {
                RawTriangles = rawMesh.Triangles.Count,
                ClearanceTriangles = clrMesh.Triangles.Count,
                RawVertices = rawMesh.Vertices.Count,
                ClearanceVertices = clrMesh.Vertices.Count,
                BlockedTiles = tileCount - rawWalk,
                RawWalkableTiles = rawWalk,
                ClearanceWalkableTiles = clrWalk,
                RawWalkablePercent = tileCount > 0 ? 100.0 * rawWalk / tileCount : 0,
                ClearanceWalkablePercent = tileCount > 0 ? 100.0 * clrWalk / tileCount : 0,
            },
        };
    }

    static List<TriangleDocument> BuildTriangles(NavMeshData mesh)
    {
        var list = new List<TriangleDocument>(mesh.Triangles.Count);
        for (int i = 0; i < mesh.Triangles.Count; i++)
        {
            NavTriangle t = mesh.Triangles[i];
            list.Add(new TriangleDocument
            {
                I = i,
                V = new[] { t.V0, t.V1, t.V2 },
                N = new[] { t.N0, t.N1, t.N2 },
                C = new[] { t.CentroidX, t.CentroidY },
            });
        }

        return list;
    }

    /// <summary>Run-length pairs: [[value, count], ...]</summary>
    static int[][] RunLengthEncode(byte[] data)
    {
        if (data.Length == 0)
            return Array.Empty<int[]>();

        var runs = new List<int[]>();
        int value = data[0];
        int count = 1;

        for (int i = 1; i < data.Length; i++)
        {
            if (data[i] == value)
            {
                count++;
                continue;
            }

            runs.Add(new[] { value, count });
            value = data[i];
            count = 1;
        }

        runs.Add(new[] { value, count });
        return runs.ToArray();
    }

    static int[][] RunLengthEncodeBool(bool[] data)
    {
        var bytes = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
            bytes[i] = data[i] ? (byte)1 : (byte)0;
        return RunLengthEncode(bytes);
    }
}

internal sealed class NavMeshExportDocument
{
    public int Version { get; set; }
    public string Source { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int TileSize { get; set; }
    public int Clearance { get; set; }
    public string[] PhysicsPalette { get; set; } = Array.Empty<string>();
    public LayerRleDocument Layers { get; set; } = new();
    public List<float[]> Vertices { get; set; } = new();
    public List<TriangleDocument> Triangles { get; set; } = new();
    public StatsDocument Stats { get; set; } = new();
}

internal sealed class LayerRleDocument
{
    public int[][] Blocked { get; set; } = Array.Empty<int[]>();
    public int[][] RawWalkable { get; set; } = Array.Empty<int[]>();
    public int[][] ClearanceWalkable { get; set; } = Array.Empty<int[]>();
    public int[][] Physics { get; set; } = Array.Empty<int[]>();
}

internal sealed class TriangleDocument
{
    public int I { get; set; }
    public int[] V { get; set; } = Array.Empty<int>();
    public int[] N { get; set; } = Array.Empty<int>();
    public float[] C { get; set; } = Array.Empty<float>();
}

internal sealed class StatsDocument
{
    public int RawTriangles { get; set; }
    public int ClearanceTriangles { get; set; }
    public int RawVertices { get; set; }
    public int ClearanceVertices { get; set; }
    public int BlockedTiles { get; set; }
    public int RawWalkableTiles { get; set; }
    public int ClearanceWalkableTiles { get; set; }
    public double RawWalkablePercent { get; set; }
    public double ClearanceWalkablePercent { get; set; }
}
