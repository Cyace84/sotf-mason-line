using Il2CppInterop.Runtime.InteropTypes.Arrays;
using RedLoader;
using UnityEngine;

namespace MasonLine;

/// <summary>GuideLine, visual half: stake/ghost/rope construction (procedural tube mesh,
/// taut-string envelope, Chaikin rounding) and the cached game-asset fetchers.</summary>
internal static partial class GuideLine
{
    // Ghost tint: translucent white like the vanilla build ghosts (the old (0.55,0.8,1) blue read as
    // an artifact — user). Live-tunable via tune.sh ghost R G B A.
    private static readonly Color GhostTint = new Color(1f, 1f, 1f, 0.3f);

    private const float StakeHeight = 1.1f;
    private const float StakeRadius = 0.05f;
    // Real-branch stake (live-tuned via tune.sh to match the in-game StandingStickElement):
    // mesh BranchABMeshLOD0 (long axis = local Z) + material BranchA, non-uniform scale — thin X/Y,
    // long Z — stood upright with a 90°-about-X rotation. Fallback = the old wooden cylinder.
    private const float StakeThick = 1.96f;
    private const float StakeLong = 2.7f;
    // Rope/knot tie point relative to the aimed ground point: knot sits 0.75 above the stake GO,
    // which itself sits StakeHeight*0.5 above ground, +0.02 world-X nudge (all user-tuned).
    private static readonly Vector3 TieOffset = new Vector3(0.02f, StakeHeight * 0.5f + 0.75f, 0f);
    private const float RopeRadius = 0.018f;
    private const int RopeSides = 8;
    /// <summary>UV v-units per meter along the rope, tuned by eye against the knot mesh it meets.
    /// The game's own rope mesh works out to 0.0227/m at r=0.04, but simply scaling that by our
    /// radius leaves the texture visibly stretched where the two meet.</summary>
    private const float RopeVPerMeter = 0.15f;
    private const int RopeSegments = 24;
    private const float SagFraction = 0.06f;
    private const float KnotScale = 0.8f;

    private static readonly Color WoodColor = new Color(0.40f, 0.27f, 0.15f);

    private static GameObject? _ghost;

    private static Material? _woodMat;
    private static bool _woodTried;
    private static Material? _ropeMat;
    private static bool _ropeMatTried;
    private static Mesh? _knotMesh;
    private static bool _knotMeshTried;
    private static Mesh? _stakeMesh;
    private static bool _stakeMeshTried;
    private static Material? _stakeMat;
    private static bool _stakeMatTried;

    /// <summary>Per-frame: translucent ghost stake at the aim point while defining the line
    /// (aiming A, or B after A) — shows exactly where the stake will be planted.
    /// NOTE: the adopted-material object may be OUR `new Material(shader)` (one per world at most)
    /// — intentionally not destroyed on reset, we cannot tell it apart from the game's shared
    /// ghost material asset, and destroying THAT would break vanilla ghosts.</summary>
    public static void UpdateGhost(bool toolHeld)
    {
        // ghost shows while finishing a line (B pending) or when another bundle is ready to start one
        bool aiming = toolHeld && (_haveA || LineTool.HasKit());
        if (!aiming) { if (_ghost != null) _ghost.SetActive(false); return; }
        if (!TryAimPoint(out var p)) { if (_ghost != null) _ghost.SetActive(false); return; }
        if (!_haveA) TrySnapStart(ref p);   // ghost jumps onto the neighbour stake = the snap's own indicator
        EnsureGhost();
        TryAdoptVanillaGhostMat();
        _ghost!.SetActive(true);
        _ghost.transform.position = p + Vector3.up * (StakeHeight * 0.5f);
    }

    // The game's blueprint look (see-through + white outline) is NOT a plain material tint: vanilla
    // ghosts get StructureGhostSwapper._ghostMaterial (a global static, populated when the game
    // initialises any structure ghost) and are drawn by the StructuresGhostPass HDRP custom pass.
    // Adopt that material lazily — the static may still be null when our ghost first spawns.
    private static bool _ghostVanillaMat;

    private static void TryAdoptVanillaGhostMat()
    {
        if (_ghostVanillaMat || _ghost == null) return;
        try
        {
            var rend = _ghost.GetComponent<Renderer>();
            if (rend == null) return;

            var m = Sons.Crafting.Structures.StructureGhostSwapper._ghostMaterial;
            if (m == null)
            {
                // fresh world: the static AND the material asset are unloaded until the game spawns a
                // vanilla structure ghost (eval probe: matAsset=NULL) — the 'white ghost'
                // regression. But the SHADER is always findable, and the StructuresGhostPass picks
                // renderers by shader, so a shader-built material should get the same blueprint look
                // (applied cleanly via eval in a fresh world; exact look pending eye-check —
                // shader defaults may differ from the asset's tuned properties).
                var sh = Shader.Find("Sons/Outline/StructuresGhostHLSL");
                if (sh == null) return;
                m = new Material(sh);
            }
            rend.sharedMaterial = m;
            _ghostVanillaMat = true;
            Dbg.Msg($"[MasonLine] ghost adopted the vanilla ghost material: {m.name} ({m.shader?.name})");
        }
        catch { }
    }

    // ---- rope: one continuous tube mesh along the sagging curve ----

    private static void BuildRope(Line line)
    {
        var tieA = line.Origin + TieOffset;
        var tieB = line.GroundB + TieOffset;
        float span = Vector3.Distance(tieA, tieB);
        // adaptive density: a fixed 24 made long spans angular over rough ground — aim for ~1
        // sample per 1.5m, floor at the old constant, capped so a 200m troll line stays cheap
        int segs = Mathf.Clamp(Mathf.RoundToInt(span / 1.5f), RopeSegments, 96);
        float horiz = new Vector2(tieB.x - tieA.x, tieB.z - tieA.z).magnitude;

        // 1) ground profile under the chord (static-only rays; -inf = no ground constraint there)
        var g = new float[segs + 1];
        var sx = new float[segs + 1];   // horizontal distance along the chord
        for (int i = 0; i <= segs; i++)
        {
            float t = i / (float)segs;
            sx[i] = t * horiz;
            g[i] = TryGroundY(Vector3.Lerp(tieA, tieB, t), out var gy)
                ? gy + RopeRadius * 2f : float.NegativeInfinity;
        }
        // median-3 on the profile: one bad sample (stray static collider) cannot fake a crest
        for (int i = 1; i < segs; i++)
        {
            float a = g[i - 1], b = g[i], c = g[i + 1];
            if (float.IsNegativeInfinity(a) || float.IsNegativeInfinity(b) || float.IsNegativeInfinity(c)) continue;
            g[i] = Mathf.Max(Mathf.Min(a, b), Mathf.Min(Mathf.Max(a, b), c));
        }

        // 2) taut-string envelope = UPPER CONVEX HULL over (distance, height) of tie points +
        // ground samples. A real stretched cord hangs in straight runs and breaks ONLY over crests
        // that actually push into it — the old per-sample ground-hug read as "the rope lies down
        // the whole slope" between uneven anchors.
        var e = new float[segs + 1];
        for (int i = 0; i <= segs; i++) e[i] = i == 0 ? tieA.y : i == segs ? tieB.y : g[i];
        var hull = new System.Collections.Generic.List<int> { 0 };
        for (int i = 1; i <= segs; i++)
        {
            if (float.IsNegativeInfinity(e[i]) && i != segs) continue;   // unconstrained sample
            while (hull.Count >= 2)
            {
                int o = hull[hull.Count - 2], a = hull[hull.Count - 1];
                float cross = (sx[a] - sx[o]) * (e[i] - e[o]) - (e[a] - e[o]) * (sx[i] - sx[o]);
                if (cross >= 0f) hull.RemoveAt(hull.Count - 1);   // previous point is not a crest
                else break;
            }
            hull.Add(i);
        }

        // 3) resample: straight run between hull vertices + small per-run sag, never below ground
        var path = new Vector3[segs + 1];
        int seg = 0;
        for (int i = 0; i <= segs; i++)
        {
            while (seg < hull.Count - 2 && sx[i] > sx[hull[seg + 1]]) seg++;
            int ha = hull[seg], hb = hull[seg + 1];
            float u = Mathf.InverseLerp(sx[ha], sx[hb], sx[i]);
            float y = Mathf.Lerp(e[ha], e[hb], u);
            y -= (sx[hb] - sx[ha]) * SagFraction * Mathf.Sin(u * Mathf.PI);   // sag scales with the run
            if (!float.IsNegativeInfinity(g[i]) && y < g[i]) y = g[i];        // sag must not dig in
            var pt = Vector3.Lerp(tieA, tieB, i / (float)segs);
            pt.y = y;
            path[i] = pt;
        }

        // 4) corner rounding: a ROUNDED hummock contributes several close hull vertices whose short
        // chords read as visible kinks (user screenshot); a sharp rock edge (one vertex)
        // is fine. Two Chaikin passes bend the cord smoothly, then re-clamp so cut corners don't
        // dip into the crest they were cut from.
        path = Chaikin(path);
        for (int i = 0; i < path.Length; i++)
        {
            var p = path[i];
            float t = horiz > 0.01f
                ? (new Vector2(p.x - tieA.x, p.z - tieA.z)).magnitude / horiz : 0f;
            float f = Mathf.Clamp(t * segs, 0f, segs - 0.001f);
            int i0 = (int)f;
            float ga = g[i0], gb = g[i0 + 1];
            if (float.IsNegativeInfinity(ga) || float.IsNegativeInfinity(gb)) continue;
            float floorY = Mathf.Lerp(ga, gb, f - i0);
            if (p.y < floorY) { p.y = floorY; path[i] = p; }
        }

        var mesh = BuildTube(path, RopeRadius, RopeSides, line.Dir);

        var ropeGo = new GameObject("MasonLineRopeMesh");
        Object.DontDestroyOnLoad(ropeGo);
        ropeGo.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<MeshFilter>());
        ropeGo.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<MeshRenderer>());
        ropeGo.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        ropeGo.GetComponent<MeshFilter>().mesh = mesh;
        var rmat = RopeMaterial();
        var rr = ropeGo.GetComponent<Renderer>();
        if (rmat != null) rr.sharedMaterial = rmat;   // shared: no tinting anymore, no instance leak
        line.Rope = ropeGo;

        line.KnotA = MakeKnot("MasonLineKnotA", tieA, line.Dir);
        line.KnotB = MakeKnot("MasonLineKnotB", tieB, line.Dir);
    }

    /// <summary>Open-polyline Chaikin corner-cutting, endpoints pinned (the knots must stay tied to
    /// the stakes). Two passes ≈ quarter-circle rounding of every kink; point count ~4x.</summary>
    private static Vector3[] Chaikin(Vector3[] pts)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            int n = pts.Length;
            if (n < 3) return pts;
            var outPts = new Vector3[2 * n - 2];
            int k = 0;
            outPts[k++] = pts[0];
            for (int i = 0; i < n - 1; i++)
            {
                if (i > 0) outPts[k++] = Vector3.Lerp(pts[i], pts[i + 1], 0.25f);
                if (i < n - 2) outPts[k++] = Vector3.Lerp(pts[i], pts[i + 1], 0.75f);
            }
            outPts[k++] = pts[n - 1];
            pts = outPts;
        }
        return pts;
    }

    /// <summary>Ground height under a rope sample — nearest STATIC hit only. The plain Raycast
    /// version returned whatever crossed the ray at build time and baked it into the tube — user
    /// report: a passing squirrel left its silhouette in the string. Anything with an attached
    /// Rigidbody (animals, players, dropped props) is not ground.</summary>
    private static bool TryGroundY(Vector3 pt, out float y)
    {
        y = 0f;
        float best = float.MaxValue;
        var hits = Physics.RaycastAll(pt + Vector3.up * 30f, Vector3.down, 60f, AimMask,
            QueryTriggerInteraction.Ignore);
        foreach (var h in hits)
        {
            var col = h.collider;
            if (col == null || col.attachedRigidbody != null) continue;   // dynamic body — skip
            if (h.distance < best) { best = h.distance; y = h.point.y; }
        }
        return best < float.MaxValue;
    }

    private static Mesh BuildTube(Vector3[] path, float radius, int sides, Vector3 fallbackDir)
    {
        int n = path.Length;
        int ring = sides + 1;
        var verts = new Il2CppStructArray<Vector3>(n * ring);
        var uvs = new Il2CppStructArray<Vector2>(n * ring);
        float vlen = 0f;
        for (int i = 0; i < n; i++)
        {
            var tan = path[Mathf.Min(i + 1, n - 1)] - path[Mathf.Max(i - 1, 0)];
            if (tan.sqrMagnitude < 1e-8f) tan = fallbackDir;
            tan = tan.normalized;
            var nor = Vector3.Cross(tan, Vector3.up);
            if (nor.sqrMagnitude < 1e-6f) nor = Vector3.Cross(tan, Vector3.forward);
            nor = nor.normalized;
            var bin = Vector3.Cross(nor, tan).normalized;
            if (i > 0) vlen += Vector3.Distance(path[i], path[i - 1]);
            for (int j = 0; j < ring; j++)
            {
                float a = (j / (float)sides) * Mathf.PI * 2f;
                var off = nor * (Mathf.Cos(a) * radius) + bin * (Mathf.Sin(a) * radius);
                verts[i * ring + j] = path[i] + off;
                // Game rope UV convention (live-probed mesh 'rope': u = AROUND, one wrap spans the
                // 0.32-wide band [0.34,0.66]; v = ALONG, slow). Old u/v layout tiled v at ~8.85/m ->
                // hundreds of micro-rings = "fabric hose" look. Material RopeLogATileable ST=(2,6).
                uvs[i * ring + j] = new Vector2(0.34f + 0.32f * (j / (float)sides), vlen * RopeVPerMeter);
            }
        }

        var tris = new Il2CppStructArray<int>((n - 1) * sides * 6);
        int ti = 0;
        for (int i = 0; i < n - 1; i++)
            for (int j = 0; j < sides; j++)
            {
                int a = i * ring + j, b = i * ring + j + 1, c = (i + 1) * ring + j, d = (i + 1) * ring + j + 1;
                tris[ti++] = a; tris[ti++] = c; tris[ti++] = b;
                tris[ti++] = b; tris[ti++] = c; tris[ti++] = d;
            }

        var m = new Mesh();
        m.vertices = verts;
        m.uv = uvs;
        m.triangles = tris;
        m.RecalculateNormals();
        m.RecalculateTangents();   // REQUIRED for the rope normal-map to render (else it's a flat brown tube)
        m.RecalculateBounds();
        return m;
    }

    private static GameObject? MakeKnot(string name, Vector3 pos, Vector3 dir)
    {
        var km = KnotMesh();
        if (km == null) return null;
        var knot = new GameObject(name);
        Object.DontDestroyOnLoad(knot);
        knot.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<MeshFilter>());
        knot.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<MeshRenderer>());
        knot.GetComponent<MeshFilter>().mesh = km;
        var rmat = RopeMaterial();
        if (rmat != null) knot.GetComponent<Renderer>().sharedMaterial = rmat;
        knot.transform.localScale = Vector3.one * KnotScale;
        knot.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(dir, Vector3.up));
        return knot;
    }

    // ---- stakes + ghost ----

    private static GameObject CreateStake(string name)
    {
        var mesh = StakeMesh();
        if (mesh != null)
        {
            var stake = new GameObject(name);
            Object.DontDestroyOnLoad(stake);
            stake.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<MeshFilter>());
            stake.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<MeshRenderer>());
            stake.GetComponent<MeshFilter>().mesh = mesh;
            var mat = StakeMat();
            if (mat != null) stake.GetComponent<Renderer>().sharedMaterial = mat;
            stake.transform.localScale = new Vector3(StakeThick, StakeThick, StakeLong);
            stake.transform.rotation = Quaternion.Euler(90f, 0f, 0f);   // branch long axis (local Z) -> upright
            return stake;
        }
        // fallback: the old wooden cylinder (long axis = Y, so no upright rotation needed)
        var cyl = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        cyl.name = name;
        Object.DontDestroyOnLoad(cyl);
        var col = cyl.GetComponent<Collider>();
        if (col != null) Object.Destroy(col);
        cyl.transform.localScale = new Vector3(StakeRadius * 2f, StakeHeight * 0.5f, StakeRadius * 2f);
        var wood = WoodMat();
        if (wood != null) { var r = cyl.GetComponent<Renderer>(); if (r != null) r.sharedMaterial = wood; }
        else TintRenderer(cyl, WoodColor);
        return cyl;
    }

    private static void PlaceStake(GameObject stake, Vector3 ground)
    {
        stake.transform.position = ground + Vector3.up * (StakeHeight * 0.5f);
        // rotation is set once in CreateStake (upright for branch, identity for the cylinder fallback)
    }

    /// <summary>Ghost = a translucent copy of the branch stake at the aim point.</summary>
    private static void EnsureGhost()
    {
        if (_ghost != null) return;
        var mesh = StakeMesh();
        if (mesh != null)
        {
            _ghost = new GameObject("MasonLineGhost");
            Object.DontDestroyOnLoad(_ghost);
            _ghost.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<MeshFilter>());
            _ghost.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<MeshRenderer>());
            _ghost.GetComponent<MeshFilter>().mesh = mesh;
            _ghost.transform.localScale = new Vector3(StakeThick, StakeThick, StakeLong);
            _ghost.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            TintRenderer(_ghost, GhostTint);
            return;
        }
        _ghost = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        _ghost.name = "MasonLineGhost";
        Object.DontDestroyOnLoad(_ghost);
        var gcol = _ghost.GetComponent<Collider>();
        if (gcol != null) Object.Destroy(gcol);
        _ghost.transform.localScale = new Vector3(StakeRadius * 2f, StakeHeight * 0.5f, StakeRadius * 2f);
        TintRenderer(_ghost, GhostTint);
    }

    // ---- game-asset fetchers (cached) ----

    internal static Material? WoodMat()
    {
        if (_woodTried) return _woodMat;
        _woodTried = true;
        try
        {
            var cs = Object.FindObjectOfType(Il2CppInterop.Runtime.Il2CppType.Of<Construction.ConstructionSystem>())
                ?.Cast<Construction.ConstructionSystem>();
            var prefab = cs?._pilarProfileDB?.GetCarouselProfiles()[0]?.ElementPrefab;
            var rend = prefab?.GetComponentInChildren(Il2CppInterop.Runtime.Il2CppType.Of<Renderer>(), true)?.Cast<Renderer>();
            _woodMat = rend?.sharedMaterial;
        }
        catch (System.Exception ex) { RLog.Warning($"[MasonLine] wood material fetch failed: {ex.Message}"); }
        return _woodMat;
    }

    internal static Material? RopeMaterial()
    {
        if (_ropeMatTried) return _ropeMat;
        _ropeMatTried = true;
        _ropeMat = FindByName<Material>("RopeLogATileable");
        if (_ropeMat == null) RLog.Warning("[MasonLine] RopeLogATileable not found");
        return _ropeMat;
    }

    internal static Mesh? KnotMesh()
    {
        if (_knotMeshTried) return _knotMesh;
        _knotMeshTried = true;
        _knotMesh = FindByName<Mesh>("RopeLogAKnotMeshLOD0");
        if (_knotMesh == null) RLog.Warning("[MasonLine] knot mesh not found");
        return _knotMesh;
    }

    /// <summary>The in-game branch mesh used for the standing-stick look (StandingStickElement).</summary>
    internal static Mesh? StakeMesh()
    {
        if (_stakeMeshTried) return _stakeMesh;
        _stakeMeshTried = true;
        _stakeMesh = FindByName<Mesh>("BranchABMeshLOD0");
        if (_stakeMesh == null) RLog.Warning("[MasonLine] BranchABMeshLOD0 not found, falling back to a cylinder");
        return _stakeMesh;
    }

    internal static Material? StakeMat()
    {
        if (_stakeMatTried) return _stakeMat;
        _stakeMatTried = true;
        _stakeMat = FindByName<Material>("BranchA");
        if (_stakeMat == null) RLog.Warning("[MasonLine] BranchA material not found");
        return _stakeMat;
    }

    private static T? FindByName<T>(string name) where T : Object
    {
        var all = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<T>());
        for (int i = 0; i < all.Length; i++)
        {
            var o = all[i].Cast<T>();
            if (o != null && o.name == name) return o;
        }
        return null;
    }

    private static void TintRenderer(GameObject go, Color c)
    {
        var rend = go.GetComponent<Renderer>();
        if (rend == null) return;
        var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
        if (shader != null) rend.material = new Material(shader);
        rend.material.color = c;
    }
}
