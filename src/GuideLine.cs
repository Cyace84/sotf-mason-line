using Il2CppInterop.Runtime.InteropTypes.Arrays;
using RedLoader;
using UnityEngine;

namespace MasonLine;

/// <summary>
/// The active guide line + its in-world visuals (two wooden stakes + a tied, sagging rope) + the
/// projection math.
///
/// The line is defined by TWO aimed points (A, B), so it runs exactly through two real-world spots.
/// Visually it's a mason line: a wooden stake at each end and a rope strung between them.
/// The rope is ONE continuous procedurally-generated tube mesh that sags between the stakes (so lighting
/// flows smoothly with no visible segment seams), skinned with the game's own RopeLogATileable material,
/// plus a rope-knot mesh at each stake. While a line stands, the build-ghost placement position is projected
/// onto this line (see <see cref="PlacePatch"/>), so a freely-placed log lays exactly along it.
/// </summary>
internal static partial class GuideLine
{
    /// <summary>One placed string line: geometry + its own world visuals. Kit economy: 1 bundle =
    /// 1 line, so several crafted bundles = several simultaneous lines (user bug: the old
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
            if (Rope != null)
            {
                // the tube mesh is generated per line (BuildTube) — destroying the GO does NOT
                // free it; without this a long place->collect session leaks a mesh per line
                var mf = Rope.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) Object.Destroy(mf.sharedMesh);
                Object.Destroy(Rope);
            }
            if (KnotA != null) Object.Destroy(KnotA);
            if (KnotB != null) Object.Destroy(KnotB);
        }
    }

    private static readonly System.Collections.Generic.List<Line> _lines = new();

    public static bool HasLine => _lines.Count > 0;

    private static bool _haveA;
    private static Vector3 _pointA;
    private static GameObject? _pendingStake;   // stake A of the line being defined

    // ---- cross-line snap: new stakes bind to the stakes of FINISHED lines ----
    /// <summary>Aim closer than this (m) to a finished line's stake and the new first stake lands
    /// exactly on it — corners and continuations share a point instead of almost sharing one.
    /// Player-set (mod menu); 0 disables the magnet.</summary>
    private static float StakeMagnetR => MasonLineConfig.Magnet;

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

    /// <summary>Ground positions of the stake nearest to <paramref name="near"/> among FINISHED
    /// lines (horizontal distance), plus that stake's far end. False when none is within
    /// <paramref name="within"/> meters.</summary>
    private static bool TryNearestStake(Vector3 near, float within, out Vector3 anchor, out Vector3 partner)
    {
        anchor = partner = default;
        float best = within * within;
        bool found = false;
        foreach (var ln in _lines)
        {
            float dA = new Vector2(ln.Origin.x - near.x, ln.Origin.z - near.z).sqrMagnitude;
            if (dA < best) { best = dA; anchor = ln.Origin; partner = ln.GroundB; found = true; }
            float dB = new Vector2(ln.GroundB.x - near.x, ln.GroundB.z - near.z).sqrMagnitude;
            if (dB < best) { best = dB; anchor = ln.GroundB; partner = ln.Origin; found = true; }
        }
        return found;
    }

    /// <summary>First stake of a new line: aimed within <see cref="StakeMagnetR"/> of a finished
    /// line's stake, it lands exactly on that stake.</summary>
    private static bool TrySnapStart(ref Vector3 p)
    {
        if (_haveA || _lines.Count == 0) return false;
        float r = StakeMagnetR;
        if (r <= 0f) return false;                 // 0 in the settings = magnet off
        if (!TryNearestStake(p, r, out var anchor, out _)) return false;
        p = anchor;
        return true;
    }

    /// <summary>Drop the next defining point: first click plants stake A, second plants stake B and
    /// strings the rope. Each completed line consumes one bundle; with no bundle in the pack nothing
    /// plants.</summary>
    public static void DropPoint()
    {
        if (!_haveA && !LineTool.HasKit())
        {
            RLog.Warning("[MasonLine] no string line bundle in the inventory. Craft another (2 sticks + rope)");
            return;
        }
        if (!TryAimPoint(out var p))
        {
            RLog.Warning("[MasonLine] no surface under the crosshair to plant the stake");
            return;
        }

        if (!_haveA)
        {
            TrySnapStart(ref p);   // exactly onto a neighbour stake when aimed at one
            _pointA = p;
            _haveA = true;
            _pendingStake = CreateStake("MasonLineStakeA");
            PlaceStake(_pendingStake, p);
            PlayPlaceSound(p);
            RLog.Msg(System.ConsoleColor.Green, "[MasonLine] stake A planted. Aim the far end and click again");
            return;
        }

        var dir = p - _pointA; dir.y = 0f;
        if (dir.sqrMagnitude < 1e-4f)
        {
            RLog.Warning("[MasonLine] second point too close, aim further along the wall");
            return;
        }

        // The bundle pays for the line, so take it BEFORE anything is built. Paying afterwards meant a
        // failed RemoveItem left a working line standing that no bundle had bought and that collecting
        // could not refund.
        if (!LineTool.ConsumeKit()) return;

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

        line.StakeB = CreateStake("MasonLineStakeB");
        PlaceStake(line.StakeB, p);
        PlayPlaceSound(p);
        BuildRope(line);
        _lines.Add(line);
        RLog.Msg(System.ConsoleColor.Green, $"[MasonLine] string line #{_lines.Count} set, {line.SegLen:0.0}m. Logs placed near it snap on (hold Dismantle on a stake to collect)");
    }

    /// <summary>Is the crosshair pointing at one of the planted stakes? Vanilla-style: the game
    /// sphere-casts r=0.2 from the camera against the stick's capsule (layer-21 recon). Our
    /// stakes carry no colliders, so the equivalent cheap test is the distance
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

    /// <summary>Hold-to-dismantle completed: pull out the AIMED line (bundle back) or cancel the pending stake A
    /// (nothing was consumed yet). Other lines stay standing.</summary>
    public static void CollectAimed()
    {
        if (_aimedPending)
        {
            if (_pendingStake != null) Object.Destroy(_pendingStake);
            _pendingStake = null;
            _haveA = false;
            PlayCollectSound(_aimedPos);
            RLog.Msg(System.ConsoleColor.Yellow, "[MasonLine] pending stake removed");
            return;
        }
        if (_aimedLine == null) return;
        _aimedLine.Destroy();
        _lines.Remove(_aimedLine);
        _aimedLine = null;
        LineTool.RefundKit();
        PlayCollectSound(_aimedPos);
        RLog.Msg(System.ConsoleColor.Yellow, $"[MasonLine] line collected, {_lines.Count} still standing");
    }

    /// <summary>The tool left the hands with stake A planted and no far end yet. Nothing has been
    /// paid for it — the bundle is only consumed when a line completes — so the stake is simply
    /// dropped. Left standing it is invisible state: minutes later and metres away the next click
    /// silently strings a line back to it (user-reported).</summary>
    public static void CancelPending()
    {
        if (!_haveA && _pendingStake == null) return;
        if (_pendingStake != null) Object.Destroy(_pendingStake);
        _pendingStake = null;
        _haveA = false;
        _aimedPending = false;
        RLog.Msg(System.ConsoleColor.Yellow, "[MasonLine] stake A pulled: the tool left the hands before the far end was set");
    }

    /// <summary>World unload/load: the lines are NOT part of the save and the stakes are DDoL — left
    /// alone they survive into a reloaded older save whose inventory still holds the bundles
    /// (user-repro'd dupe). Destroy every visual, drop all cached scene assets (materials/
    /// meshes/clips unload with the world; the getters re-find them lazily). NO bundle refunds here —
    /// the save-time marker handles inventory restitution on load.</summary>
    public static void ResetWorld()
    {
        foreach (var ln in _lines) ln.Destroy();
        _lines.Clear();
        if (_pendingStake != null) { Object.Destroy(_pendingStake); _pendingStake = null; }
        _haveA = false;
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
        // crafting mat
        _woodMat = null; _woodTried = false;
        _ropeMat = null; _ropeMatTried = false;
        _stakeMat = null; _stakeMatTried = false;
        _knotMesh = null; _knotMeshTried = false;
        _stakeMesh = null; _stakeMeshTried = false;
        // the adopted ghost material died with the world — without this reset the next world's
        // ghost would skip adoption forever and degrade to the flat tint
        _ghostVanillaMat = false;
    }

    // Capture zone, set by the player in the mod menu. It started at a fixed 2m sideways, which was
    // far too greedy: every ground placement within arm's reach of the cord got pulled onto it, so
    // you could not put anything down beside a standing line.
    private static float SnapRadius => MasonLineConfig.Radius;
    private static float SnapEndMargin => MasonLineConfig.EndMargin;

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

}
