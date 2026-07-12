using Il2CppInterop.Runtime.InteropTypes.Arrays;
using RedLoader;
using UnityEngine;

namespace BuildingLaser;

/// <summary>
/// The active guide line + its in-world visuals (two wooden stakes + a tied, sagging rope) + the
/// projection math.
///
/// The line is defined by TWO aimed points (A, B), so it runs exactly through two real-world spots.
/// Visually it's a builder's string line: a wooden stake at each end and a rope strung between them.
/// The rope is ONE continuous procedurally-generated tube mesh following a catenary sag (so lighting
/// flows smoothly with no visible segment seams), skinned with the game's own RopeLogATileable material,
/// plus a rope-knot mesh at each stake. While snap is on, the build-ghost placement position is projected
/// onto this line (see <see cref="PlacePatch"/>), so a freely-placed log lays exactly along it.
/// </summary>
internal static class LaserLine
{
    public static bool HasLine;
    public static bool SnapActive;
    public static Vector3 Origin;
    public static Vector3 Dir = Vector3.forward;

    private static bool _haveA;
    private static Vector3 _pointA;
    private static Vector3 _groundB;
    private static float _segLen = 20f;

    /// <summary>Aim distance (m) to a stake base that counts as "looking at it" for C-collect.</summary>
    private const float CollectAimRadius = 0.45f;

    private const float StakeHeight = 1.1f;
    private const float StakeRadius = 0.05f;
    private const float TieHeight = StakeHeight - 0.12f;
    private const float RopeRadius = 0.018f;
    private const int RopeSides = 8;
    /// <summary>UV v-units per meter along the rope. Live-tuned 2026-07-06 against the knot mesh
    /// (eval ladder 0.05→0.10→0.08→0.15, user-approved 0.15). The GAME 'rope' mesh uses 0.0227/m
    /// at r=0.04 (probed live); scaling by radius gave 0.05 — still too stretched next to the knot.</summary>
    private const float RopeVPerMeter = 0.15f;
    private const int RopeSegments = 24;
    private const float SagFraction = 0.06f;
    private const float KnotScale = 0.7f;

    private static readonly Color WoodColor = new Color(0.40f, 0.27f, 0.15f);

    private static GameObject? _ropeGo;
    private static GameObject? _knotA;
    private static GameObject? _knotB;
    private static GameObject? _stakeA;
    private static GameObject? _stakeB;
    private static GameObject? _ghost;

    private static Material? _woodMat;
    private static bool _woodTried;
    private static Material? _ropeMat;
    private static bool _ropeMatTried;
    private static Mesh? _knotMesh;
    private static bool _knotMeshTried;

    private const int AimMask = ~((1 << 29) | (1 << 14));

    private static bool TryAimPoint(out Vector3 p)
    {
        p = default;
        var cam = Camera.main;
        if (cam == null) return false;
        var t = cam.transform;
        if (Physics.Raycast(t.position, t.forward, out var hit, 500f, AimMask, QueryTriggerInteraction.Ignore))
        {
            p = hit.point;
            return true;
        }
        return false;
    }

    /// <summary>L: drop the next defining point. First press = A (stake), second = B (stake + rope).</summary>
    public static void DropPoint()
    {
        if (!TryAimPoint(out var p))
        {
            RLog.Warning("[BuildingLaser] no surface under the crosshair to plant the stake");
            return;
        }

        if (!_haveA)
        {
            _pointA = p;
            _haveA = true;
            EnsureStake(ref _stakeA, "BuildingLaserStakeA");
            PlaceStake(_stakeA!, p);
            RLog.Msg(System.ConsoleColor.Green, "[BuildingLaser] stake A planted — aim the far end and press L again");
            return;
        }

        var dir = p - _pointA; dir.y = 0f;
        if (dir.sqrMagnitude < 1e-4f)
        {
            RLog.Warning("[BuildingLaser] second point too close — aim further along the wall");
            return;
        }

        Origin = _pointA;
        Dir = dir.normalized;
        // keep B at its OWN ground height (stake sits on the ground even on a slope)
        _segLen = new Vector2(p.x - _pointA.x, p.z - _pointA.z).magnitude;
        _groundB = p;
        HasLine = true;
        _haveA = false;
        SnapActive = true;   // the line is there to be used — arm the snap immediately (K toggles off)

        EnsureStake(ref _stakeB, "BuildingLaserStakeB");
        PlaceStake(_stakeB!, p);
        BuildRope(_pointA, p);
        RLog.Msg(System.ConsoleColor.Green, $"[BuildingLaser] string line set, {_segLen:0.0}m — snap ON (K toggles, C on a stake collects)");
    }

    /// <summary>Is the crosshair pointing at one of the planted stakes? (Stakes carry no
    /// colliders, so this is an aim-point proximity test against the stake bases.)</summary>
    public static bool AimingAtStake()
    {
        if (!HasLine && !_haveA) return false;
        if (!TryAimPoint(out var p)) return false;
        if (_haveA && (p - _pointA).sqrMagnitude < CollectAimRadius * CollectAimRadius) return true;
        if (HasLine &&
            ((p - Origin).sqrMagnitude < CollectAimRadius * CollectAimRadius ||
             (p - _groundB).sqrMagnitude < CollectAimRadius * CollectAimRadius)) return true;
        return false;
    }

    public static void Clear()
    {
        HasLine = false;
        _haveA = false;
        if (_ropeGo != null) _ropeGo.SetActive(false);
        if (_knotA != null) _knotA.SetActive(false);
        if (_knotB != null) _knotB.SetActive(false);
        if (_stakeA != null) _stakeA.SetActive(false);
        if (_stakeB != null) _stakeB.SetActive(false);
        RLog.Msg(System.ConsoleColor.Yellow, "[BuildingLaser] line cleared");
    }

    /// <summary>Snap capture zone: max sideways distance (m) from the string for a placement to
    /// be pulled onto it; beyond that the build is unrelated — leave it vanilla.</summary>
    private const float SnapRadius = 2.0f;
    /// <summary>How far (m) past a stake the snap zone extends along the line.</summary>
    private const float SnapEndMargin = 1.0f;

    /// <summary>Project a world point onto the infinite line in XZ, keeping the point's own height.</summary>
    public static Vector3 Project(Vector3 p)
    {
        var rel = p - Origin; rel.y = 0f;
        float d = Vector3.Dot(rel, Dir);
        var c = Origin + Dir * d;
        return new Vector3(c.x, p.y, c.z);
    }

    /// <summary>Project only if the point is actually NEAR the strung segment (within
    /// <see cref="SnapRadius"/> sideways and between the stakes ±<see cref="SnapEndMargin"/>).
    /// Placements elsewhere on the map must stay vanilla — an armed line is not a magnet.</summary>
    public static bool TryProject(Vector3 p, out Vector3 projected)
    {
        projected = p;
        var rel = p - Origin; rel.y = 0f;
        float d = Vector3.Dot(rel, Dir);
        if (d < -SnapEndMargin || d > _segLen + SnapEndMargin) return false;   // beyond the stakes
        var c = Origin + Dir * d;
        float lateralSq = (rel - Dir * d).sqrMagnitude;
        if (lateralSq > SnapRadius * SnapRadius) return false;                 // too far sideways
        projected = new Vector3(c.x, p.y, c.z);
        return true;
    }

    /// <summary>Per-frame: translucent ghost stake at the aim point while defining the line
    /// (aiming A, or B after A) — shows exactly where the stake will be planted.</summary>
    public static void UpdateGhost(bool toolHeld)
    {
        bool aiming = toolHeld && (!HasLine || _haveA);
        if (!aiming) { if (_ghost != null) _ghost.SetActive(false); return; }
        if (!TryAimPoint(out var p)) { if (_ghost != null) _ghost.SetActive(false); return; }
        EnsureGhost();
        _ghost!.SetActive(true);
        _ghost.transform.position = p + Vector3.up * (StakeHeight * 0.5f);
    }

    /// <summary>Snap cue: tint the rope material instance (warm = armed, grey = off). HDRP may ignore
    /// .color; harmless if so (the K-toggle also logs state).</summary>
    public static void RefreshRopeCue()
    {
        if (_ropeGo == null || !HasLine) return;
        var rend = _ropeGo.GetComponent<Renderer>();
        if (rend != null) rend.material.color = SnapActive ? Color.white : new Color(0.6f, 0.6f, 0.6f);
    }

    // ---- rope: one continuous tube mesh along a catenary ----

    private static void BuildRope(Vector3 groundA, Vector3 groundB)
    {
        var tieA = groundA + Vector3.up * TieHeight;
        var tieB = groundB + Vector3.up * TieHeight;
        float span = Vector3.Distance(tieA, tieB);
        float sag = span * SagFraction;

        var path = new Vector3[RopeSegments + 1];
        for (int i = 0; i <= RopeSegments; i++)
        {
            float t = i / (float)RopeSegments;
            var pt = Vector3.Lerp(tieA, tieB, t);
            pt.y -= sag * Mathf.Sin(t * Mathf.PI);
            path[i] = pt;
        }

        var mesh = BuildTube(path, RopeRadius, RopeSides);

        if (_ropeGo == null)
        {
            _ropeGo = new GameObject("BuildingLaserRopeMesh");
            Object.DontDestroyOnLoad(_ropeGo);
            _ropeGo.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<MeshFilter>());
            _ropeGo.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<MeshRenderer>());
        }
        _ropeGo.SetActive(true);
        _ropeGo.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        _ropeGo.GetComponent<MeshFilter>().mesh = mesh;
        var rmat = RopeMaterial();
        var rr = _ropeGo.GetComponent<Renderer>();
        if (rmat != null) rr.material = new Material(rmat);   // instance so tint doesn't touch the shared mat

        PlaceKnot(ref _knotA, "BuildingLaserKnotA", tieA);
        PlaceKnot(ref _knotB, "BuildingLaserKnotB", tieB);
        RefreshRopeCue();
    }

    /// <summary>Build one smooth tube mesh (world-space) following a path; smooth normals = no seams.</summary>
    private static Mesh BuildTube(Vector3[] path, float radius, int sides)
    {
        int n = path.Length;
        int ring = sides + 1;
        var verts = new Il2CppStructArray<Vector3>(n * ring);
        var uvs = new Il2CppStructArray<Vector2>(n * ring);
        float vlen = 0f;
        for (int i = 0; i < n; i++)
        {
            var tan = path[Mathf.Min(i + 1, n - 1)] - path[Mathf.Max(i - 1, 0)];
            if (tan.sqrMagnitude < 1e-8f) tan = Dir;
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

    private static void PlaceKnot(ref GameObject? knot, string name, Vector3 pos)
    {
        var km = KnotMesh();
        if (km == null) return;
        if (knot == null)
        {
            knot = new GameObject(name);
            Object.DontDestroyOnLoad(knot);
            knot.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<MeshFilter>());
            knot.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<MeshRenderer>());
            knot.GetComponent<MeshFilter>().mesh = km;
            var rmat = RopeMaterial();
            if (rmat != null) knot.GetComponent<Renderer>().sharedMaterial = rmat;
            knot.transform.localScale = Vector3.one * KnotScale;
        }
        knot.SetActive(true);
        knot.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(Dir, Vector3.up));
    }

    // ---- stakes + aim dot ----

    private static void EnsureStake(ref GameObject? stake, string name)
    {
        if (stake != null) { stake.SetActive(true); return; }
        stake = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        stake.name = name;
        Object.DontDestroyOnLoad(stake);
        var col = stake.GetComponent<Collider>();
        if (col != null) Object.Destroy(col);
        stake.transform.localScale = new Vector3(StakeRadius * 2f, StakeHeight * 0.5f, StakeRadius * 2f);
        var wood = WoodMat();
        if (wood != null) { var r = stake.GetComponent<Renderer>(); if (r != null) r.sharedMaterial = wood; }
        else TintRenderer(stake, WoodColor);
    }

    private static void PlaceStake(GameObject stake, Vector3 ground)
    {
        stake.SetActive(true);
        stake.transform.position = ground + Vector3.up * (StakeHeight * 0.5f);
        stake.transform.rotation = Quaternion.identity;
    }

    /// <summary>Ghost = stake-sized translucent cylinder (construction-ghost blue).</summary>
    private static void EnsureGhost()
    {
        if (_ghost != null) return;
        _ghost = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        _ghost.name = "BuildingLaserGhost";
        Object.DontDestroyOnLoad(_ghost);
        var col = _ghost.GetComponent<Collider>();
        if (col != null) Object.Destroy(col);
        _ghost.transform.localScale = new Vector3(StakeRadius * 2f, StakeHeight * 0.5f, StakeRadius * 2f);
        TintRenderer(_ghost, new Color(0.55f, 0.8f, 1f, 0.35f));
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
        catch (System.Exception ex) { RLog.Warning($"[BuildingLaser] wood material fetch failed: {ex.Message}"); }
        return _woodMat;
    }

    internal static Material? RopeMaterial()
    {
        if (_ropeMatTried) return _ropeMat;
        _ropeMatTried = true;
        _ropeMat = FindByName<Material>("RopeLogATileable");
        if (_ropeMat == null) RLog.Warning("[BuildingLaser] RopeLogATileable not found");
        return _ropeMat;
    }

    internal static Mesh? KnotMesh()
    {
        if (_knotMeshTried) return _knotMesh;
        _knotMeshTried = true;
        _knotMesh = FindByName<Mesh>("RopeLogAKnotMeshLOD0");
        if (_knotMesh == null) RLog.Warning("[BuildingLaser] knot mesh not found");
        return _knotMesh;
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
