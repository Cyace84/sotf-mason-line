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
    // 0.45 was too tight to hit without a visual cue (user 2026-07-16); with the ShowMessage prompt
    // the stake is still the clear target, so a generous radius just removes the pixel-hunt.
    private const float CollectAimRadius = 1.0f;

    // Ghost tint: translucent white like the vanilla build ghosts (the old (0.55,0.8,1) blue read as
    // an artifact — user 2026-07-16). Live-tunable via tune.sh ghost R G B A.
    private static readonly Color GhostTint = new Color(1f, 1f, 1f, 0.3f);

    private const float StakeHeight = 1.1f;
    private const float StakeRadius = 0.05f;
    private const float TieHeight = StakeHeight - 0.12f;
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
    /// <summary>UV v-units per meter along the rope. Live-tuned 2026-07-06 against the knot mesh
    /// (eval ladder 0.05→0.10→0.08→0.15, user-approved 0.15). The GAME 'rope' mesh uses 0.0227/m
    /// at r=0.04 (probed live); scaling by radius gave 0.05 — still too stretched next to the knot.</summary>
    private const float RopeVPerMeter = 0.15f;
    private const int RopeSegments = 24;
    private const float SagFraction = 0.06f;
    private const float KnotScale = 0.8f;

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
    private static Mesh? _stakeMesh;
    private static bool _stakeMeshTried;
    private static Material? _stakeMat;
    private static bool _stakeMatTried;

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
            PlayPlaceSound(p);
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
        PlayPlaceSound(p);
        BuildRope(_pointA, p);
        LineTool.ConsumeKit();   // kit economy: the placed line IS the kit — it leaves the inventory
        RLog.Msg(System.ConsoleColor.Green, $"[BuildingLaser] string line set, {_segLen:0.0}m — snap ON (K toggles, C on a stake collects)");
    }

    /// <summary>Is the crosshair pointing at one of the planted stakes? Vanilla-style: the game
    /// sphere-casts r=0.2 over 2.25m from the camera against the stick's capsule (layer-21 recon
    /// 2026-07-17). Our stakes carry no colliders, so the equivalent cheap test is the distance
    /// between the camera's view segment and the stake's axis segment (base → base+StakeHeight):
    /// aim anywhere along the stick, even looking up at it point-blank.</summary>
    private const float CollectReach = 2.5f;          // camera-to-stick reach (vanilla 2.475)
    private const float CollectCastRadius = 0.2f;     // vanilla sphere-cast radius

    public static bool AimingAtStake()
    {
        if (!HasLine && !_haveA) return false;
        var cam = Camera.main;
        if (cam == null) return false;
        var o = cam.transform.position;
        var e = o + cam.transform.forward * CollectReach;
        float hit = CollectCastRadius + StakeRadius;
        float hit2 = hit * hit;

        if (_haveA && SegSegDistSqr(o, e, _pointA, _pointA + Vector3.up * StakeHeight) < hit2) return true;
        if (HasLine &&
            (SegSegDistSqr(o, e, Origin, Origin + Vector3.up * StakeHeight) < hit2 ||
             SegSegDistSqr(o, e, _groundB, _groundB + Vector3.up * StakeHeight) < hit2)) return true;
        return false;
    }

    /// <summary>Squared distance between segments p1q1 and p2q2 (Ericson, RTCD 5.1.9).</summary>
    private static float SegSegDistSqr(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
    {
        Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
        float a = Vector3.Dot(d1, d1), e2 = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
        float s, t;
        if (a <= 1e-6f && e2 <= 1e-6f) return r.sqrMagnitude;
        if (a <= 1e-6f) { s = 0f; t = Mathf.Clamp01(f / e2); }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e2 <= 1e-6f) { t = 0f; s = Mathf.Clamp01(-c / a); }
            else
            {
                float b = Vector3.Dot(d1, d2), denom = a * e2 - b * b;
                s = denom > 1e-6f ? Mathf.Clamp01((b * f - c * e2) / denom) : 0f;
                t = (b * s + f) / e2;
                if (t < 0f) { t = 0f; s = Mathf.Clamp01(-c / a); }
                else if (t > 1f) { t = 1f; s = Mathf.Clamp01((b - c) / a); }
            }
        }
        var c1 = p1 + d1 * s;
        var c2 = p2 + d2 * t;
        return (c1 - c2).sqrMagnitude;
    }

    // ---- hold-to-collect shake (vanilla dismantle wobble, ScrewStructureDestruction.Routine style:
    // damped organic forces, not a mechanical sine — Perlin noise at ~8Hz + amplitude that grows with
    // hold progress). Rotation-only jitter around the stakes' settled rotation: the stake BASES don't
    // move, so AimingAtStake stays stable during the hold. A short C TAP = decaying nudge (vanilla
    // gives a kick per press even if you let go).
    private static Quaternion _shakeBaseA, _shakeBaseB;
    private static Vector3 _shakePosA, _shakePosB;
    private static bool _shaking;
    private static float _nudgeT = -1f;   // >=0 = tap-nudge decay in progress

    // ---- vanilla wobble rig: the game's dismantle shake is the authored legacy clip
    // 'PreviewAnim -  Wobble' whose curves target a child named "Renderable" (live-probed on
    // PreviewAnimationManager._animationShell 2026-07-17: max ~1.6deg / ~5mm — SUBTLE). We sample
    // the clip onto a hidden proxy (root + "Renderable" child) and copy the child's local pose
    // onto the stakes. WobbleExtraLite drives the tap-nudge.
    private const float WobbleSeconds = 0.4f;         // vanilla dismantle hold duration
    private static GameObject? _wobbleRoot;
    private static Transform? _wobblePayload;
    private static AnimationClip? _wobbleClip;        // hold
    private static AnimationClip? _nudgeClip;         // tap
    private static bool _wobbleInitTried;

    private static void EnsureWobbleRig()
    {
        if (_wobbleRoot != null || _wobbleInitTried) return;
        _wobbleInitTried = true;
        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<AnimationClip>());
            foreach (var o in all)
            {
                var c = o.TryCast<AnimationClip>();
                if (c == null) continue;
                if (c.name == "PreviewAnim -  Wobble") _wobbleClip = c;
                else if (c.name == "PreviewAnim -  WobbleExtraLite") _nudgeClip = c;
            }
            if (_wobbleClip == null) { RLog.Warning("[BuildingLaser] vanilla Wobble clip not loaded — using soft fallback shake"); return; }
            if (_nudgeClip == null) _nudgeClip = _wobbleClip;

            _wobbleRoot = new GameObject("BuildingLaserWobbleProxy");
            Object.DontDestroyOnLoad(_wobbleRoot);
            _wobbleRoot.transform.position = new Vector3(0f, -2000f, 0f);
            var payload = new GameObject("Renderable");   // clip curves bind to this child name
            payload.transform.SetParent(_wobbleRoot.transform, false);
            _wobblePayload = payload.transform;
            RLog.Msg(System.ConsoleColor.Cyan, "[BuildingLaser] vanilla wobble rig ready");
        }
        catch (System.Exception e) { RLog.Warning($"[BuildingLaser] wobble rig init failed: {e.Message}"); }
    }

    /// <summary>Sample a wobble clip at 0..1 progress; false when the rig is unavailable.</summary>
    private static bool SampleWobble(AnimationClip? clip, float progress, out Vector3 pos, out Quaternion rot)
    {
        pos = Vector3.zero; rot = Quaternion.identity;
        EnsureWobbleRig();
        if (clip == null || _wobbleRoot == null || _wobblePayload == null) return false;
        clip.SampleAnimation(_wobbleRoot, Mathf.Clamp01(progress) * clip.length);
        pos = _wobblePayload.localPosition;
        rot = _wobblePayload.localRotation;
        return true;
    }

    private static void CaptureShakeBases()
    {
        if (_stakeA != null) { _shakeBaseA = _stakeA.transform.rotation; _shakePosA = _stakeA.transform.position; }
        if (_stakeB != null) { _shakeBaseB = _stakeB.transform.rotation; _shakePosB = _stakeB.transform.position; }
    }

    private static void ApplyJitter(Quaternion jitter, Vector3 offset)
    {
        if (_stakeA != null && _stakeA.activeSelf)
        { _stakeA.transform.rotation = _shakeBaseA * jitter; _stakeA.transform.position = _shakePosA + offset; }
        if (_stakeB != null && _stakeB.activeSelf)
        { _stakeB.transform.rotation = _shakeBaseB * jitter; _stakeB.transform.position = _shakePosB + offset; }
    }

    private static void RestoreShakeBases()
    {
        if (_stakeA != null) { _stakeA.transform.rotation = _shakeBaseA; _stakeA.transform.position = _shakePosA; }
        if (_stakeB != null) { _stakeB.transform.rotation = _shakeBaseB; _stakeB.transform.position = _shakePosB; }
    }

    public static void Shake(float t)
    {
        if (_stakeA == null && _stakeB == null) return;
        if (!_shaking)
        {
            _shaking = true;
            if (_nudgeT < 0f) CaptureShakeBases();   // a nudge already captured settled bases
            _nudgeT = -1f;
            PlayWobbleSound();                       // creak starts WITH the shake, not at the end
        }
        if (SampleWobble(_wobbleClip, t / WobbleSeconds, out var pos, out var rot))
            ApplyJitter(rot, pos);
        else
        {   // rig unavailable — soft procedural fallback (far gentler than the old shake)
            float amp = 0.8f + 1.2f * Mathf.Clamp01(t / WobbleSeconds);
            ApplyJitter(Quaternion.Euler((Mathf.PerlinNoise(t * 5f, 0.31f) - 0.5f) * 2f * amp, 0f,
                                         (Mathf.PerlinNoise(0.73f, t * 5.5f) - 0.5f) * 2f * amp), Vector3.zero);
        }
    }

    public static void EndShake()
    {
        if (!_shaking) return;
        _shaking = false;
        RestoreShakeBases();
    }

    /// <summary>Short C tap on a stake: one decaying kick, vanilla per-press dismantle feedback.</summary>
    public static void Nudge()
    {
        if (_shaking) return;
        if (_stakeA == null && _stakeB == null) return;
        if (_nudgeT < 0f) CaptureShakeBases();        // only capture from a settled pose
        _nudgeT = 0f;
    }

    /// <summary>Per-frame decay of the tap-nudge (no-op unless one is running).</summary>
    public static void UpdateNudge()
    {
        if (_nudgeT < 0f || _shaking) return;
        _nudgeT += Time.deltaTime;
        if (_nudgeT >= 0.4f) { _nudgeT = -1f; RestoreShakeBases(); return; }
        if (SampleWobble(_nudgeClip, _nudgeT / 0.4f, out var pos, out var rot))
            ApplyJitter(rot, pos);
        else
        {
            float amp = 1.5f * Mathf.Exp(-7f * _nudgeT);
            ApplyJitter(Quaternion.Euler(Mathf.Sin(_nudgeT * 55f) * amp, 0f, Mathf.Cos(_nudgeT * 47f) * amp * 0.7f), Vector3.zero);
        }
    }

    /// <summary>Vanilla stick-plant sound (user-identified via FMOD bank browse 2026-07-17:
    /// the first sound of a vanilla standing-stick placement).</summary>
    public static void PlayPlaceSound(Vector3 pos)
    {
        try { FMODCommon.PlayOneshot("event:/SotF Events/player sounds/Build Sounds/Sticks/Stick Stab Ground", pos); }
        catch { }
    }

    /// <summary>Wood creak at hold start — plays the moment the dismantle shake begins
    /// (user A/B test 2026-07-17: correct creak, must fire on hold, not on completion).</summary>
    public static void PlayWobbleSound()
    {
        try
        {
            var pos = HasLine ? Origin : _pointA;
            FMODCommon.PlayOneshot("event:/SotF Events/player sounds/Build Sounds/build_log_wobble", pos);
        }
        catch { }
    }

    /// <summary>Pull-out completion: the inventory stick-pickup thud (user 2026-07-17: the foley
    /// 'new_pickups/pickup_sticks' is the loud ground-sticks grab — wrong; the dull ending is the
    /// Inv pickup).</summary>
    public static void PlayCollectSound()
    {
        try
        {
            var pos = HasLine ? Origin : _pointA;
            FMODCommon.PlayOneshot("event:/SotF Events/player sounds/Inv/Pickups/pickup_Stick", pos);
        }
        catch { }
    }

    public static void Clear()
    {
        LineTool.RefundKit();    // no-op unless a completed line consumed the kit
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
                // vanilla structure ghost (eval probe 2026-07-16: matAsset=NULL) — the 'white ghost'
                // regression. But the SHADER is always findable, and the StructuresGhostPass picks
                // renderers by shader, so a shader-built material should get the same blueprint look
                // (applied cleanly via eval in a fresh world 2026-07-17; exact look pending eye-check —
                // shader defaults may differ from the asset's tuned properties).
                var sh = Shader.Find("Sons/Outline/StructuresGhostHLSL");
                if (sh == null) return;
                m = new Material(sh);
            }
            rend.sharedMaterial = m;
            _ghostVanillaMat = true;
            RLog.Msg($"[BuildingLaser] ghost adopted the vanilla ghost material: {m.name} ({m.shader?.name})");
        }
        catch { }
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
        var tieA = groundA + TieOffset;
        var tieB = groundB + TieOffset;
        float span = Vector3.Distance(tieA, tieB);
        float sag = span * SagFraction;

        var path = new Vector3[RopeSegments + 1];
        for (int i = 0; i <= RopeSegments; i++)
        {
            float t = i / (float)RopeSegments;
            var pt = Vector3.Lerp(tieA, tieB, t);
            pt.y -= sag * Mathf.Sin(t * Mathf.PI);
            // Keep the rope OUT of the ground: on a crest the straight chord (+sag) dips under the
            // terrain downslope (user 2026-07-16). Lift any interior sample to just above whatever
            // the down-ray hits — the string then lies over the bump like a real taut cord.
            // KNOWN RISK: the ray starts 30m up, so a dense canopy above the line could false-lift
            // a sample onto a tree — revisit with a terrain-only mask if that shows up in play.
            if (i > 0 && i < RopeSegments &&
                Physics.Raycast(pt + Vector3.up * 30f, Vector3.down, out var ghit, 60f, AimMask,
                    QueryTriggerInteraction.Ignore) &&
                ghit.point.y + RopeRadius * 2f > pt.y)
                pt.y = ghit.point.y + RopeRadius * 2f;
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
        var mesh = StakeMesh();
        if (mesh != null)
        {
            stake = new GameObject(name);
            Object.DontDestroyOnLoad(stake);
            stake.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<MeshFilter>());
            stake.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<MeshRenderer>());
            stake.GetComponent<MeshFilter>().mesh = mesh;
            var mat = StakeMat();
            if (mat != null) stake.GetComponent<Renderer>().sharedMaterial = mat;
            stake.transform.localScale = new Vector3(StakeThick, StakeThick, StakeLong);
            stake.transform.rotation = Quaternion.Euler(90f, 0f, 0f);   // branch long axis (local Z) -> upright
            return;
        }
        // fallback: the old wooden cylinder (long axis = Y, so no upright rotation needed)
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
        // rotation is set once in EnsureStake (upright for branch, identity for the cylinder fallback)
    }

    /// <summary>Ghost = a translucent copy of the branch stake at the aim point.</summary>
    private static void EnsureGhost()
    {
        if (_ghost != null) return;
        var mesh = StakeMesh();
        if (mesh != null)
        {
            _ghost = new GameObject("BuildingLaserGhost");
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
        _ghost.name = "BuildingLaserGhost";
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

    /// <summary>The in-game branch mesh used for the standing-stick look (StandingStickElement).</summary>
    internal static Mesh? StakeMesh()
    {
        if (_stakeMeshTried) return _stakeMesh;
        _stakeMeshTried = true;
        _stakeMesh = FindByName<Mesh>("BranchABMeshLOD0");
        if (_stakeMesh == null) RLog.Warning("[BuildingLaser] BranchABMeshLOD0 not found — using cylinder fallback");
        return _stakeMesh;
    }

    internal static Material? StakeMat()
    {
        if (_stakeMatTried) return _stakeMat;
        _stakeMatTried = true;
        _stakeMat = FindByName<Material>("BranchA");
        if (_stakeMat == null) RLog.Warning("[BuildingLaser] BranchA material not found");
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
