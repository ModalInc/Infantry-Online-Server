(function () {
  "use strict";

  const PHYSICS_COLORS = [
    null,
    "#e53935", "#c62828", "#ef5350", "#b71c1c", "#ff7043",
    "#43a047", "#2e7d32", "#66bb6a", "#1b5e20", "#81c784",
    "#fdd835", "#f9a825", "#ffee58", "#f57f17", "#fff176",
    "#fb8c00", "#ef6c00", "#ffb74d", "#e65100", "#ffcc80",
    "#8e24aa", "#6a1b9a", "#ab47bc", "#4a148c", "#ce93d8",
    "#ff5722", "#ff5722", "#ff5722", "#ff5722",
    "#00897b",
    "#1565c0",
  ];

  const canvas = document.getElementById("canvas");
  const ctx = canvas.getContext("2d");
  const fileInput = document.getElementById("fileInput");
  const fileNameEl = document.getElementById("fileName");
  const statsPanel = document.getElementById("statsPanel");
  const hoverInfo = document.getElementById("hoverInfo");
  const legendEl = document.getElementById("legend");

  let doc = null;
  let blocked = null;
  let rawWalk = null;
  let clrWalk = null;
  let physics = null;
  let tileToTri = null;

  let scale = 6;
  let panX = 0;
  let panY = 0;
  let dragging = false;
  let dragStart = { x: 0, y: 0, panX: 0, panY: 0 };

  let pathStart = null;
  let pathEnd = null;
  let pathTriIndices = null;

  const layers = {
    physics: document.getElementById("layerPhysics"),
    blocked: document.getElementById("layerBlocked"),
    rawWalk: document.getElementById("layerRawWalk"),
    clrWalk: document.getElementById("layerClrWalk"),
    triFill: document.getElementById("layerTriFill"),
    triEdge: document.getElementById("layerTriEdge"),
    portals: document.getElementById("layerPortals"),
  };

  function rleDecode(rle, length) {
    const out = new Uint8Array(length);
    let idx = 0;
    for (const pair of rle) {
      const val = pair[0];
      const count = pair[1];
      out.fill(val, idx, idx + count);
      idx += count;
    }
    return out;
  }

  function triColor(i) {
    const hue = (i * 47) % 360;
    return `hsla(${hue}, 58%, 48%, 0.38)`;
  }

  function triColorStroke(i) {
    const hue = (i * 47) % 360;
    return `hsla(${hue}, 70%, 62%, 0.85)`;
  }

  function resizeCanvas() {
    const wrap = canvas.parentElement;
    const dpr = window.devicePixelRatio || 1;
    canvas.width = Math.floor(wrap.clientWidth * dpr);
    canvas.height = Math.floor(wrap.clientHeight * dpr);
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    draw();
  }

  function worldToScreen(tx, ty) {
    return [(tx - panX) * scale, (ty - panY) * scale];
  }

  function screenToWorld(sx, sy) {
    return [sx / scale + panX, sy / scale + panY];
  }

  function buildTileToTri() {
    if (!doc) return;
    const n = doc.width * doc.height;
    tileToTri = new Int32Array(n);
    tileToTri.fill(-1);
    for (const tri of doc.triangles) {
      const cx = Math.floor(tri.c[0]);
      const cy = Math.floor(tri.c[1]);
      if (cx >= 0 && cy >= 0 && cx < doc.width && cy < doc.height)
        tileToTri[cy * doc.width + cx] = tri.i;
    }
  }

  function loadDocument(json) {
    doc = json;
    const n = doc.width * doc.height;
    blocked = rleDecode(doc.layers.blocked, n);
    rawWalk = rleDecode(doc.layers.rawWalkable, n);
    clrWalk = rleDecode(doc.layers.clearanceWalkable, n);
    physics = rleDecode(doc.layers.physics, n);
    buildTileToTri();
    pathStart = pathEnd = pathTriIndices = null;
    panX = doc.width * 0.5 - canvas.clientWidth / scale * 0.5;
    panY = doc.height * 0.5 - canvas.clientHeight / scale * 0.5;
    updateStats();
    updateLegend();
    draw();
  }

  function updateStats() {
    if (!doc) {
      statsPanel.innerHTML = "";
      return;
    }
    const s = doc.stats || {};
    statsPanel.innerHTML = `
      <dt>Source</dt><dd>${doc.source}</dd>
      <dt>Size</dt><dd>${doc.width} × ${doc.height} tiles</dd>
      <dt>Clearance</dt><dd>${doc.clearance} tiles</dd>
      <dt>Triangles</dt><dd>${s.clearanceTriangles ?? doc.triangles.length}</dd>
      <dt>Vertices</dt><dd>${s.clearanceVertices ?? doc.vertices.length}</dd>
      <dt>Walkable (clearance)</dt><dd>${(s.clearanceWalkablePercent ?? 0).toFixed(1)}%</dd>
    `;
  }

  function updateLegend() {
    legendEl.innerHTML = `
      <li><span class="swatch" style="background:#3d1f1f;border-color:#ff5252"></span> Blocked</li>
      <li><span class="swatch" style="background:rgba(46,204,113,0.35)"></span> Clearance walkable</li>
      <li><span class="swatch" style="background:rgba(102,187,106,0.25)"></span> Raw walkable</li>
      <li><span class="swatch" style="background:hsla(200,58%,48%,0.38)"></span> Nav triangle (varies by index)</li>
      <li><span class="swatch" style="background:#ffd54f"></span> Portal edge</li>
      <li><span class="swatch" style="background:#ff9800"></span> Boundary edge</li>
      <li><span class="swatch" style="background:#ffeb3b"></span> Test path</li>
    `;
  }

  function draw() {
    const w = canvas.clientWidth;
    const h = canvas.clientHeight;
    ctx.fillStyle = "#0a0a12";
    ctx.fillRect(0, 0, w, h);

    if (!doc) {
      ctx.fillStyle = "#666";
      ctx.font = "14px Segoe UI, sans-serif";
      ctx.textAlign = "center";
      ctx.fillText("Load a .navmesh.json file (or drop it here)", w / 2, h / 2);
      return;
    }

    const x0 = Math.max(0, Math.floor(panX));
    const y0 = Math.max(0, Math.floor(panY));
    const x1 = Math.min(doc.width, Math.ceil(panX + w / scale));
    const y1 = Math.min(doc.height, Math.ceil(panY + h / scale));

    if (layers.physics.checked) {
      for (let y = y0; y < y1; y++) {
        for (let x = x0; x < x1; x++) {
          const p = physics[y * doc.width + x];
          if (p === 0) continue;
          const col = PHYSICS_COLORS[p] || "#888";
          const [sx, sy] = worldToScreen(x, y);
          ctx.fillStyle = col + "55";
          ctx.fillRect(sx, sy, scale + 0.5, scale + 0.5);
        }
      }
    }

    if (layers.blocked.checked) {
      ctx.fillStyle = "rgba(61, 31, 31, 0.85)";
      for (let y = y0; y < y1; y++) {
        for (let x = x0; x < x1; x++) {
          if (!blocked[y * doc.width + x]) continue;
          const [sx, sy] = worldToScreen(x, y);
          ctx.fillRect(sx, sy, scale + 0.5, scale + 0.5);
        }
      }
    }

    if (layers.rawWalk.checked) {
      ctx.fillStyle = "rgba(102, 187, 106, 0.22)";
      for (let y = y0; y < y1; y++) {
        for (let x = x0; x < x1; x++) {
          if (!rawWalk[y * doc.width + x]) continue;
          const [sx, sy] = worldToScreen(x, y);
          ctx.fillRect(sx, sy, scale + 0.5, scale + 0.5);
        }
      }
    }

    if (layers.clrWalk.checked) {
      ctx.fillStyle = "rgba(46, 204, 113, 0.18)";
      for (let y = y0; y < y1; y++) {
        for (let x = x0; x < x1; x++) {
          if (!clrWalk[y * doc.width + x]) continue;
          const [sx, sy] = worldToScreen(x, y);
          ctx.fillRect(sx, sy, scale + 0.5, scale + 0.5);
        }
      }
    }

    const verts = doc.vertices;
    const tris = doc.triangles;

    if (layers.triFill.checked || layers.triEdge.checked || layers.portals.checked) {
      for (const tri of tris) {
        const v0 = verts[tri.v[0]];
        const v1 = verts[tri.v[1]];
        const v2 = verts[tri.v[2]];
        const cx = (v0[0] + v1[0] + v2[0]) / 3;
        const cy = (v0[1] + v1[1] + v2[1]) / 3;
        if (cx < x0 - 2 || cx > x1 + 2 || cy < y0 - 2 || cy > y1 + 2) continue;

        const p0 = worldToScreen(v0[0], v0[1]);
        const p1 = worldToScreen(v1[0], v1[1]);
        const p2 = worldToScreen(v2[0], v2[1]);

        ctx.beginPath();
        ctx.moveTo(p0[0], p0[1]);
        ctx.lineTo(p1[0], p1[1]);
        ctx.lineTo(p2[0], p2[1]);
        ctx.closePath();

        if (layers.triFill.checked) {
          ctx.fillStyle = triColor(tri.i);
          ctx.fill();
        }

        if (layers.triEdge.checked) {
          ctx.strokeStyle = triColorStroke(tri.i);
          ctx.lineWidth = Math.max(0.5, scale * 0.08);
          ctx.stroke();
        }

        if (layers.portals.checked) {
          drawEdgePortal(p0, p1, tri.n[0]);
          drawEdgePortal(p1, p2, tri.n[1]);
          drawEdgePortal(p2, p0, tri.n[2]);
        }
      }
    }

    if (pathTriIndices && pathTriIndices.length > 0) {
      ctx.strokeStyle = "#ffeb3b";
      ctx.lineWidth = Math.max(2, scale * 0.2);
      ctx.setLineDash([]);
      for (let i = 0; i < pathTriIndices.length; i++) {
        const tri = tris[pathTriIndices[i]];
        const c = tri.c;
        const [sx, sy] = worldToScreen(c[0], c[1]);
        if (i === 0) {
          ctx.beginPath();
          ctx.moveTo(sx, sy);
        } else {
          ctx.lineTo(sx, sy);
        }
      }
      ctx.stroke();
    }

    if (pathStart) drawMarker(pathStart, "#4fc3f7");
    if (pathEnd) drawMarker(pathEnd, "#ff5252");
  }

  function drawEdgePortal(pA, pB, neighbor) {
    const mx = (pA[0] + pB[0]) / 2;
    const my = (pA[1] + pB[1]) / 2;
    const len = Math.hypot(pB[0] - pA[0], pB[1] - pA[1]);
    if (len < 2) return;

    ctx.beginPath();
    ctx.moveTo(pA[0], pA[1]);
    ctx.lineTo(pB[0], pB[1]);
    if (neighbor >= 0) {
      ctx.strokeStyle = "rgba(255, 213, 79, 0.75)";
      ctx.lineWidth = Math.max(1.5, scale * 0.12);
      ctx.setLineDash([]);
    } else {
      ctx.strokeStyle = "rgba(255, 152, 0, 0.55)";
      ctx.lineWidth = Math.max(1, scale * 0.08);
      ctx.setLineDash([4, 3]);
    }
    ctx.stroke();
    ctx.setLineDash([]);
  }

  function drawMarker(pt, color) {
    const [sx, sy] = worldToScreen(pt[0], pt[1]);
    ctx.fillStyle = color;
    ctx.beginPath();
    ctx.arc(sx + scale * 0.5, sy + scale * 0.5, Math.max(4, scale * 0.35), 0, Math.PI * 2);
    ctx.fill();
    ctx.strokeStyle = "#fff";
    ctx.lineWidth = 1.5;
    ctx.stroke();
  }

  function findTriangleAt(tx, ty) {
    if (!doc) return -1;
    const ix = Math.floor(tx);
    const iy = Math.floor(ty);
    if (ix < 0 || iy < 0 || ix >= doc.width || iy >= doc.height) return -1;

    let tri = tileToTri[iy * doc.width + ix];
    if (tri >= 0) return tri;

    let best = -1;
    let bestD = Infinity;
    for (const t of doc.triangles) {
      const dx = t.c[0] - tx;
      const dy = t.c[1] - ty;
      const d = dx * dx + dy * dy;
      if (d < bestD) {
        bestD = d;
        best = t.i;
      }
    }
    return bestD < 64 ? best : -1;
  }

  function findTrianglePath(startTri, endTri) {
    if (startTri < 0 || endTri < 0) return null;
    if (startTri === endTri) return [startTri];

    const tris = doc.triangles;
    const count = tris.length;
    const g = new Float64Array(count);
    const f = new Float64Array(count);
    const from = new Int32Array(count);
    const closed = new Uint8Array(count);
    g.fill(Infinity);
    f.fill(Infinity);
    from.fill(-1);

    function heuristic(a, b) {
      const ta = tris[a].c;
      const tb = tris[b].c;
      const dx = ta[0] - tb[0];
      const dy = ta[1] - tb[1];
      return Math.hypot(dx, dy);
    }

    g[startTri] = 0;
    f[startTri] = heuristic(startTri, endTri);

    const open = [startTri];
    const openSet = new Set([startTri]);

    while (open.length > 0) {
      open.sort((a, b) => f[a] - f[b]);
      const current = open.shift();
      openSet.delete(current);

      if (current === endTri) {
        const path = [];
        let c = current;
        while (c >= 0) {
          path.push(c);
          c = from[c];
        }
        path.reverse();
        return path;
      }

      if (closed[current]) continue;
      closed[current] = 1;

      const tri = tris[current];
      for (const nb of tri.n) {
        if (nb < 0 || closed[nb]) continue;
        const tent = g[current] + heuristic(current, nb);
        if (tent >= g[nb]) continue;
        from[nb] = current;
        g[nb] = tent;
        f[nb] = tent + heuristic(nb, endTri);
        if (!openSet.has(nb)) {
          open.push(nb);
          openSet.add(nb);
        }
      }
    }
    return null;
  }

  function onCanvasClick(ev) {
    if (!doc || dragging) return;
    const rect = canvas.getBoundingClientRect();
    const [wx, wy] = screenToWorld(ev.clientX - rect.left, ev.clientY - rect.top);

    let tri = findTriangleAt(wx, wy);
    if (tri < 0) return;

    const snap = document.getElementById("snapCentroid").checked;
    const pt = snap ? doc.triangles[tri].c.slice() : [wx, wy];

    if (!pathStart) {
      pathStart = pt;
      pathEnd = null;
      pathTriIndices = null;
    } else if (!pathEnd) {
      pathEnd = pt;
      const t0 = findTriangleAt(pathStart[0], pathStart[1]);
      const t1 = findTriangleAt(pathEnd[0], pathEnd[1]);
      pathTriIndices = findTrianglePath(t0, t1);
    } else {
      pathStart = pt;
      pathEnd = null;
      pathTriIndices = null;
    }
    draw();
  }

  function onCanvasMove(ev) {
    if (!doc) return;
    const rect = canvas.getBoundingClientRect();
    const [wx, wy] = screenToWorld(ev.clientX - rect.left, ev.clientY - rect.top);
    const tx = Math.floor(wx);
    const ty = Math.floor(wy);

    if (tx < 0 || ty < 0 || tx >= doc.width || ty >= doc.height) {
      hoverInfo.textContent = "—";
      return;
    }

    const idx = ty * doc.width + tx;
    const tri = findTriangleAt(wx, wy);
    const pal = doc.physicsPalette || [];
    const physName = pal[physics[idx]] || ("Physics " + physics[idx]);

    let text = `Tile (${tx}, ${ty})\n`;
    text += `Blocked: ${blocked[idx] ? "yes" : "no"}\n`;
    text += `Raw walk: ${rawWalk[idx] ? "yes" : "no"}\n`;
    text += `Clear walk: ${clrWalk[idx] ? "yes" : "no"}\n`;
    text += `${physName}\n`;
    if (tri >= 0) {
      const t = doc.triangles[tri];
      text += `\nTriangle #${tri}\n`;
      text += `Centroid: (${t.c[0].toFixed(2)}, ${t.c[1].toFixed(2)})\n`;
      text += `Neighbors: [${t.n.join(", ")}]`;
    }
    hoverInfo.textContent = text;
  }

  function handleFile(file) {
    if (!file) return;
    const reader = new FileReader();
    reader.onload = () => {
      try {
        const json = JSON.parse(reader.result);
        if (!json.width || !json.triangles) throw new Error("Not a navmesh export");
        fileNameEl.textContent = file.name;
        loadDocument(json);
      } catch (e) {
        alert("Failed to parse JSON: " + e.message);
      }
    };
    reader.readAsText(file);
  }

  fileInput.addEventListener("change", (ev) => handleFile(ev.target.files[0]));

  document.body.addEventListener("dragover", (ev) => {
    ev.preventDefault();
  });
  document.body.addEventListener("drop", (ev) => {
    ev.preventDefault();
    if (ev.dataTransfer.files.length > 0) handleFile(ev.dataTransfer.files[0]);
  });

  canvas.addEventListener("mousedown", (ev) => {
    if (ev.button !== 0) return;
    dragging = false;
    dragStart = { x: ev.clientX, y: ev.clientY, panX, panY };
  });
  window.addEventListener("mousemove", (ev) => {
    if (ev.buttons === 1 && dragStart) {
      const dx = ev.clientX - dragStart.x;
      const dy = ev.clientY - dragStart.y;
      if (Math.abs(dx) > 3 || Math.abs(dy) > 3) dragging = true;
      if (dragging) {
        panX = dragStart.panX - dx / scale;
        panY = dragStart.panY - dy / scale;
        draw();
      }
    }
    onCanvasMove(ev);
  });
  window.addEventListener("mouseup", () => {
    dragStart = null;
  });
  canvas.addEventListener("click", onCanvasClick);

  canvas.addEventListener("wheel", (ev) => {
    ev.preventDefault();
    const rect = canvas.getBoundingClientRect();
    const mx = ev.clientX - rect.left;
    const my = ev.clientY - rect.top;
    const [wx, wy] = screenToWorld(mx, my);
    const factor = ev.deltaY > 0 ? 0.9 : 1.1;
    scale = Math.min(24, Math.max(1, scale * factor));
    panX = wx - mx / scale;
    panY = wy - my / scale;
    document.getElementById("zoomSlider").value = scale;
    draw();
  }, { passive: false });

  document.getElementById("zoomSlider").addEventListener("input", (ev) => {
    scale = parseFloat(ev.target.value);
    draw();
  });

  document.getElementById("resetView").addEventListener("click", () => {
    if (!doc) return;
    scale = 6;
    document.getElementById("zoomSlider").value = scale;
    panX = doc.width * 0.5 - canvas.clientWidth / scale * 0.5;
    panY = doc.height * 0.5 - canvas.clientHeight / scale * 0.5;
    draw();
  });

  document.getElementById("clearPath").addEventListener("click", () => {
    pathStart = pathEnd = pathTriIndices = null;
    draw();
  });

  document.getElementById("exportPng").addEventListener("click", () => {
    const a = document.createElement("a");
    a.download = (doc ? doc.source.replace(/\.[^.]+$/, "") : "navmesh") + ".png";
    a.href = canvas.toDataURL("image/png");
    a.click();
  });

  Object.values(layers).forEach((el) => el.addEventListener("change", draw));

  window.addEventListener("resize", resizeCanvas);
  resizeCanvas();
  updateLegend();
})();
