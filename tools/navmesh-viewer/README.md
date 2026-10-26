# Nav mesh tools

Export bot navigation meshes from Infantry `.lvl` files and inspect them in the browser.

## 1. Export JSON

From the repo root:

```bash
dotnet run --project "dotnetcore/NavMeshExporter/NavMeshExporter.csproj" -- "path/to/map.lvl"
```

Options:

- `-o path.json` — output file (default: `map.navmesh.json` next to the `.lvl`)
- `-c 3` — clearance in tiles (default **3**, matches live `NavMeshPathfinder`)

Example:

```bash
dotnet run --project "dotnetcore/NavMeshExporter/NavMeshExporter.csproj" -- "zones/MyZone/map.lvl" -o "tools/navmesh-viewer/sample.navmesh.json"
```

## 2. Open the viewer

Open `tools/navmesh-viewer/index.html` in a browser (double-click or drag the file into Chrome/Edge/Firefox).

- **Load** or **drop** a `.navmesh.json` file
- Toggle layers: blocked tiles, walkable areas, nav triangles, portal edges, physics overlay
- **Click two points** to run A* on the triangle graph and draw a test path
- **Export PNG** for screenshots

### Controls

| Action | Input |
|--------|--------|
| Pan | Drag |
| Zoom | Mouse wheel |
| Path test | Click start, then end |
| Clear path | Sidebar button |

## JSON format

- Tile coordinates match server nav mesh (`NavVec2` tile space; ×16 for world raw units)
- `layers.*` — run-length encoded tile masks
- `vertices` / `triangles` — clearance mesh used by bots
- `physicsPalette` — human-readable physics names per tile
