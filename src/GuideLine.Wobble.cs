using RedLoader;
using UnityEngine;

namespace MasonLine;

/// <summary>GuideLine, feedback half: the vanilla-clip dismantle wobble (hold-shake +
/// tap-nudge on the stakes) and the FMOD one-shots for plant/creak/collect.</summary>
internal static partial class GuideLine
{
    // ---- hold-to-collect shake (vanilla dismantle wobble, ScrewStructureDestruction.Routine style:
    // damped organic forces, not a mechanical sine — Perlin noise at ~8Hz + amplitude that grows with
    // hold progress). Rotation-only jitter around the stakes' settled rotation: the stake BASES don't
    // move, so AimingAtStake stays stable during the hold. A short TAP = decaying nudge (vanilla
    // gives a kick per press even if you let go).
    private static GameObject? _shakeStakeA, _shakeStakeB;   // stakes captured at shake/nudge start
    private static Quaternion _shakeBaseA, _shakeBaseB;
    private static Vector3 _shakePosA, _shakePosB;
    private static bool _shaking;
    private static float _nudgeT = -1f;   // >=0 = tap-nudge decay in progress

    // ---- vanilla wobble rig: the game's dismantle shake is the authored legacy clip
    // 'PreviewAnim -  Wobble' whose curves target a child named "Renderable" (live-probed on
    // PreviewAnimationManager._animationShell: max ~1.6deg / ~5mm — SUBTLE). We sample
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
            if (_wobbleClip == null) { RLog.Warning("[MasonLine] vanilla Wobble clip not loaded, using soft fallback shake"); return; }
            if (_nudgeClip == null) _nudgeClip = _wobbleClip;

            _wobbleRoot = new GameObject("MasonLineWobbleProxy");
            Object.DontDestroyOnLoad(_wobbleRoot);
            _wobbleRoot.transform.position = new Vector3(0f, -2000f, 0f);
            var payload = new GameObject("Renderable");   // clip curves bind to this child name
            payload.transform.SetParent(_wobbleRoot.transform, false);
            _wobblePayload = payload.transform;
            Dbg.Msg(System.ConsoleColor.Cyan, "[MasonLine] vanilla wobble rig ready");
        }
        catch (System.Exception e) { RLog.Warning($"[MasonLine] wobble rig init failed: {e.Message}"); }
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

    /// <summary>A short dismantle tap on a stake: one decaying kick, the same feedback vanilla gives
    /// for a tap too short to dismantle anything.</summary>
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

    /// <summary>Vanilla stick-plant sound (user-identified via FMOD bank browse:
    /// the first sound of a vanilla standing-stick placement).</summary>
    public static void PlayPlaceSound(Vector3 pos)
    {
        try { FMODCommon.PlayOneshot("event:/SotF Events/player sounds/Build Sounds/Sticks/Stick Stab Ground", pos); }
        catch { }
    }

    /// <summary>Wood creak at hold start — plays the moment the dismantle shake begins
    /// (user A/B test: correct creak, must fire on hold, not on completion).</summary>
    public static void PlayWobbleSound()
    {
        try { FMODCommon.PlayOneshot("event:/SotF Events/player sounds/Build Sounds/build_log_wobble", _aimedPos); }
        catch { }
    }

    /// <summary>Pull-out completion: the inventory stick-pickup thud (user: the foley
    /// 'new_pickups/pickup_sticks' is the loud ground-sticks grab — wrong; the dull ending is the
    /// Inv pickup).</summary>
    private static void PlayCollectSound(Vector3 pos)
    {
        try { FMODCommon.PlayOneshot("event:/SotF Events/player sounds/Inv/Pickups/pickup_Stick", pos); }
        catch { }
    }
}
