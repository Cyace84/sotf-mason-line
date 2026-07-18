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
    /// <summary>One placed string line: geometry + its own world visuals. Kit economy: 1 kit =
    /// 1 line, so several crafted kits = several simultaneous lines (user bug 2026-07-17: the old
    /// static singleton allowed only one line in the world).</summary>
    private sealed class Line
    {
        public Vector3 Origin;      // ground point A
        public Vector3 Dir;         // horizontal, normalized
        public Vector3 GroundB;
        public float SegLen;
        public GameObject? StakeA, StakeB, Rope, KnotA, KnotB;

        public void Destroy()
        {
            if (StakeA != null) Object.Destroy(StakeA);
            if (StakeB != null) Object.Destroy(StakeB);
            if (Rope != null) Object.Destroy(Rope);
            if (KnotA != null) Object.Destroy(KnotA);
            if (KnotB != null) Object.Destroy(KnotB);
        }
    }

    private static readonly System.Collections.Generic.List<Line> _lines = new();

    public static bool HasLine => _lines.Count > 0;
    public static bool SnapActive;

    private static bool _haveA;
    private static Vector3 _pointA;
    private static GameObject? _pendingStake;   // stake A of the line being defined

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

    /// <summary>L: drop the next defining point. First press = A (stake), second = B (stake + rope).
    /// Each completed line consumes one kit; with no kit in the inventory nothing plants.</summary>
    public static void DropPoint()
    {
        if (!_haveA && !LineTool.HasKit())
        {
            RLog.Warning("[BuildingLaser] no string line kit in the inventory — craft another (stick + rope)");
            return;
        }
        if (!TryAimPoint(out var p))
        {
            RLog.Warning("[BuildingLaser] no surface under the crosshair to plant the stake");
            return;
        }

        if (!_haveA)
        {
            _pointA = p;
            _haveA = true;
            _pendingStake = CreateStake("BuildingLaserStakeA");
            PlaceStake(_pendingStake, p);
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

        var line = new Line
        {
            Origin = _pointA,
            Dir = dir.normalized,
            // keep B at its OWN ground height (stake sits on the ground even on a slope)
            SegLen = new Vector2(p.x - _pointA.x, p.z - _pointA.z).magnitude,
            GroundB = p,
            StakeA = _pendingStake,
        };
        _pendingStake = null;
        _haveA = false;

        line.StakeB = CreateStake("BuildingLaserStakeB");
        PlaceStake(line.StakeB, p);
        PlayPlaceSound(p);
        BuildRope(line);
        _lines.Add(line);
        SnapActive = true;   // the line is there to be used — arm the snap immediately (K toggles off)
        LineTool.ConsumeKit();   // kit economy: the placed line IS the kit — it leaves the inventory
        RLog.Msg(System.ConsoleColor.Green, $"[BuildingLaser] string line #{_lines.Count} set, {line.SegLen:0.0}m — snap ON (K toggles, C on a stake collects)");
    }

    /// <summary>Is the crosshair pointing at one of the planted stakes? Vanilla-style: the game
    /// sphere-casts r=0.2 over 2.25m from the camera against the stick's capsule (layer-21 recon
    /// 2026-07-17). Our stakes carry no colliders, so the equivalent cheap test is the distance
    /// between the camera's view segment and the stake's axis segment (base → base+StakeHeight):
    /// aim anywhere along the stick, even looking up at it point-blank.</summary>
    private const float CollectReach = 2.5f;          // camera-to-stick reach (vanilla 2.475)
    private const float CollectCastRadius = 0.2f;     // vanilla sphere-cast radius

    private static Line? _aimedLine;        // line whose stake the crosshair is on (this frame)
    private static bool _aimedPending;      // aiming the not-yet-completed stake A
    private static Vector3 _aimedPos;       // ground point of the aimed stake (sound anchor)

    public static bool AimingAtStake()
    {
        _aimedLine = null;
        _aimedPending = false;
        if (!HasLine && !_haveA) return false;
        var cam = Camera.main;
        if (cam == null) return false;
        var o = cam.transform.position;
        var e = o + cam.transform.forward * CollectReach;
        float hit = CollectCastRadius + StakeRadius;
        float hit2 = hit * hit;

        if (_haveA && SegSegDistSqr(o, e, _pointA, _pointA + Vector3.up * StakeHeight) < hit2)
        { _aimedPending = true; _aimedPos = _pointA; return true; }
        foreach (var ln in _lines)
        {
            if (SegSegDistSqr(o, e, ln.Origin, ln.Origin + Vector3.up * StakeHeight) < hit2)
            { _aimedLine = ln; _aimedPos = ln.Origin; return true; }
            if (SegSegDistSqr(o, e, ln.GroundB, ln.GroundB + Vector3.up * StakeHeight) < hit2)
            { _aimedLine = ln; _aimedPos = ln.GroundB; return true; }
        }
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
    private static GameObject? _shakeStakeA, _shakeStakeB;   // stakes captured at shake/nudge start
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
        // shake the AIMED target only: the pending stake alone, or both stakes of the aimed line
        _shakeStakeA = _aimedPending ? _pendingStake : _aimedLine?.StakeA;
        _shakeStakeB = _aimedPending ? null : _aimedLine?.StakeB;
        if (_shakeStakeA != null) { _shakeBaseA = _shakeStakeA.transform.rotation; _shakePosA = _shakeStakeA.transform.position; }
        if (_shakeStakeB != null) { _shakeBaseB = _shakeStakeB.transform.rotation; _shakePosB = _shakeStakeB.transform.position; }
    }

    private static void ApplyJitter(Quaternion jitter, Vector3 offset)
    {
        if (_shakeStakeA != null)
        { _shakeStakeA.transform.rotation = _shakeBaseA * jitter; _shakeStakeA.transform.position = _shakePosA + offset; }
        if (_shakeStakeB != null)
        { _shakeStakeB.transform.rotation = _shakeBaseB * jitter; _shakeStakeB.transform.position = _shakePosB + offset; }
    }

    private static void RestoreShakeBases()
    {
        if (_shakeStakeA != null) { _shakeStakeA.transform.rotation = _shakeBaseA; _shakeStakeA.transform.position = _shakePosA; }
        if (_shakeStakeB != null) { _shakeStakeB.transform.rotation = _shakeBaseB; _shakeStakeB.transform.position = _shakePosB; }
        _shakeStakeA = null;
        _shakeStakeB = null;
    }

    public static void Shake(float t)
    {
        if (!_shaking)
        {
            if (_aimedLine == null && !_aimedPending) return;
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
        if (_aimedLine == null && !_aimedPending) return;
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
        try { FMODCommon.PlayOneshot("event:/SotF Events/player sounds/Build Sounds/build_log_wobble", _aimedPos); }
        catch { }
    }

    /// <summary>Pull-out completion: the inventory stick-pickup thud (user 2026-07-17: the foley
    /// 'new_pickups/pickup_sticks' is the loud ground-sticks grab — wrong; the dull ending is the
    /// Inv pickup).</summary>
    private static void PlayCollectSound(Vector3 pos)
    {
        try { FMODCommon.PlayOneshot("event:/SotF Events/player sounds/Inv/Pickups/pickup_Stick", pos); }
        catch { }
    }

    /// <summary>Hold-C completed: pull out the AIMED line (kit back) or cancel the pending stake A
    /// (nothing was consumed yet). Other lines stay standing.</summary>
    public static void CollectAimed()
    {
        if (_aimedPending)
        {
            if (_pendingStake != null) Object.Destroy(_pendingStake);
            _pendingStake = null;
            _haveA = false;
            PlayCollectSound(_aimedPos);
            RLog.Msg(System.ConsoleColor.Yellow, "[BuildingLaser] pending stake removed");
            return;
        }
        if (_aimedLine == null) return;
        _aimedLine.Destroy();
        _lines.Remove(_aimedLine);
        _aimedLine = null;
        LineTool.RefundKit();
        PlayCollectSound(_aimedPos);
        RLog.Msg(System.ConsoleColor.Yellow, $"[BuildingLaser] line collected — {_lines.Count} still standing");
    }

    /// <summary>World unload/load: the lines are NOT part of the save and the stakes are DDoL — left
    /// alone they survive into a reloaded older save whose inventory still holds the kits
    /// (user-repro'd dupe 2026-07-17). Destroy every visual, drop all cached scene assets (materials/
    /// meshes/clips unload with the world; the getters re-find them lazily). NO kit refunds here —
    /// the save-time marker handles inventory restitution on load.</summary>
    public static void ResetWorld()
    {
        foreach (var ln in _lines) ln.Destroy();
        _lines.Clear();
        if (_pendingStake != null) { Object.Destroy(_pendingStake); _pendingStake = null; }
        _haveA = false;
        SnapActive = false;
        _aimedLine = null;
        _aimedPending = false;
        _shaking = false;
        _nudgeT = -1f;
        _shakeStakeA = null;
        _shakeStakeB = null;
        if (_ghost != null) { Object.Destroy(_ghost); _ghost = null; }
        if (_wobbleRoot != null) Object.Destroy(_wobbleRoot);
        _wobbleRoot = null; _wobblePayload = null; _wobbleClip = null; _nudgeClip = null;
        _wobbleInitTried = false;
        // null the asset AND its tried-flag together — nulling only the asset left the getter
        // returning the cached null in the next world => materialless renderers = PINK stick on the
        // crafting mat (user repro 2026-07-18)
        _woodMat = null; _woodTried = false;
        _ropeMat = null; _ropeMatTried = false;
        _stakeMat = null; _stakeMatTried = false;
        _knotMesh = null; _knotMeshTried = false;
        _stakeMesh = null; _stakeMeshTried = false;
    }

    /// <summary>J: tear down EVERYTHING — all lines (one kit refunded each) + the pending stake.</summary>
    public static void Clear()
    {
        foreach (var ln in _lines) { ln.Destroy(); LineTool.RefundKit(); }
        _lines.Clear();
        if (_pendingStake != null) Object.Destroy(_pendingStake);
        _pendingStake = null;
        _haveA = false;
        RLog.Msg(System.ConsoleColor.Yellow, "[BuildingLaser] all lines cleared");
    }

    /// <summary>Snap capture zone: max sideways distance (m) from the string for a placement to
    /// be pulled onto it; beyond that the build is unrelated — leave it vanilla.</summary>
    private const float SnapRadius = 2.0f;
    /// <summary>How far (m) past a stake the snap zone extends along the line.</summary>
    private const float SnapEndMargin = 1.0f;

    /// <summary>Project only if the point is actually NEAR a strung segment (within
    /// <see cref="SnapRadius"/> sideways and between the stakes ±<see cref="SnapEndMargin"/>).
    /// With several lines standing, the laterally-nearest one wins. Placements elsewhere on the
    /// map must stay vanilla — an armed line is not a magnet.</summary>
    public static bool TryProject(Vector3 p, out Vector3 projected, out Vector3 dir)
    {
        projected = p;
        dir = Vector3.forward;
        Line? best = null;
        float bestLatSq = float.MaxValue, bestD = 0f;
        foreach (var ln in _lines)
        {
            var rel = p - ln.Origin; rel.y = 0f;
            float d = Vector3.Dot(rel, ln.Dir);
            if (d < -SnapEndMargin || d > ln.SegLen + SnapEndMargin) continue;   // beyond the stakes
            float latSq = (rel - ln.Dir * d).sqrMagnitude;
            if (latSq > SnapRadius * SnapRadius || latSq >= bestLatSq) continue; // too far sideways
            best = ln; bestLatSq = latSq; bestD = d;
        }
        if (best == null) return false;
        var c = best.Origin + best.Dir * bestD;
        projected = new Vector3(c.x, p.y, c.z);
        dir = best.Dir;
        return true;
    }

    /// <summary>Per-frame: translucent ghost stake at the aim point while defining the line
    /// (aiming A, or B after A) — shows exactly where the stake will be planted.</summary>
    public static void UpdateGhost(bool toolHeld)
    {
        // ghost shows while finishing a line (B pending) or when another kit is ready to start one
        bool aiming = toolHeld && (_haveA || LineTool.HasKit());
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
        foreach (var ln in _lines)
        {
            if (ln.Rope == null) continue;
            var rend = ln.Rope.GetComponent<Renderer>();
            if (rend != null) rend.material.color = SnapActive ? Color.white : new Color(0.6f, 0.6f, 0.6f);
        }
    }

    // ---- rope: one continuous tube mesh along a catenary ----

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
        // the whole slope" between uneven anchors (user 2026-07-18).
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
        // chords read as visible kinks (user screenshot 2026-07-18); a sharp rock edge (one vertex)
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

        var ropeGo = new GameObject("BuildingLaserRopeMesh");
        Object.DontDestroyOnLoad(ropeGo);
        ropeGo.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<MeshFilter>());
        ropeGo.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<MeshRenderer>());
        ropeGo.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        ropeGo.GetComponent<MeshFilter>().mesh = mesh;
        var rmat = RopeMaterial();
        var rr = ropeGo.GetComponent<Renderer>();
        if (rmat != null) rr.material = new Material(rmat);   // instance so tint doesn't touch the shared mat
        line.Rope = ropeGo;

        line.KnotA = MakeKnot("BuildingLaserKnotA", tieA, line.Dir);
        line.KnotB = MakeKnot("BuildingLaserKnotB", tieB, line.Dir);
        RefreshRopeCue();
    }

    /// <summary>Build one smooth tube mesh (world-space) following a path; smooth normals = no seams.</summary>
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
    /// 2026-07-18: a passing squirrel left its silhouette in the string. Anything with an attached
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

    // ---- stakes + aim dot ----

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
