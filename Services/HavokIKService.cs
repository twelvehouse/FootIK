using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.Havok.Animation.Rig;

namespace FootIK.Services;

/// <summary>
/// Wraps Havok's TwoJointIK solver.
/// Ported from Brio's IKService — uses unmanaged function pointer and 16-byte-aligned setup memory.
/// </summary>
public unsafe class HavokIKService : IDisposable
{
    // ── TwoJointIKSetup ──────────────────────────────────────────────────────
    // Verified against Brio (IKService.cs) — must match the game binary exactly.
    [StructLayout(LayoutKind.Explicit, Size = 0x82)]
    private struct TwoJointIKSetup
    {
        [FieldOffset(0x00)] public short FirstJointIdx     = -1;
        [FieldOffset(0x02)] public short SecondJointIdx    = -1;
        [FieldOffset(0x04)] public short EndBoneIdx        = -1;
        [FieldOffset(0x06)] public short FirstJointTwistIdx  = -1;
        [FieldOffset(0x08)] public short SecondJointTwistIdx = -1;

        /// <summary>
        /// Hinge axis in second joint's local space.
        /// Default (0,0,1) for Hyur — validate empirically per race.
        /// </summary>
        [FieldOffset(0x10)] public Vector4 HingeAxisLS     = Vector4.Zero;

        [FieldOffset(0x20)] public float CosineMaxHingeAngle = -1f;
        [FieldOffset(0x24)] public float CosineMinHingeAngle =  1f;

        [FieldOffset(0x28)] public float FirstJointIkGain  = 1f;
        [FieldOffset(0x2C)] public float SecondJointIkGain = 1f;
        [FieldOffset(0x30)] public float EndJointIkGain    = 1f;

        [FieldOffset(0x40)] public Vector4    EndTargetMS             = Vector4.Zero;
        [FieldOffset(0x50)] public Quaternion EndTargetRotationMS     = Quaternion.Identity;
        [FieldOffset(0x60)] public Vector4    EndBoneOffsetLS         = Vector4.Zero;
        [FieldOffset(0x70)] public Quaternion EndBoneRotationOffsetLS = Quaternion.Identity;

        [FieldOffset(0x80)] public bool EnforceEndPosition = true;
        [FieldOffset(0x81)] public bool EnforceEndRotation = false;

        public TwoJointIKSetup() { }
    }

    // ── Solver function pointer ──────────────────────────────────────────────
    // Brio calls: _twoJointSolverSolve(&notSure, setup, pose)
    // ScanText on an E8 pattern resolves the call target automatically in Dalamud.
    private const string TwoJointIKSig = "E8 ?? ?? ?? ?? 0F 28 55 ?? 41 0F 28 D8";

    private readonly delegate* unmanaged<byte*, TwoJointIKSetup*, hkaPose*, byte*> _solve;

    // Persistent 16-byte-aligned allocation for TwoJointIKSetup (Havok requires alignment).
    private readonly TwoJointIKSetup* _setupMem;

    public bool IsAvailable => _solve != null;

    public HavokIKService(ISigScanner scanner, IPluginLog log)
    {
        // Allocate 16-byte-aligned memory for setup struct.
        _setupMem = (TwoJointIKSetup*)NativeMemory.AlignedAlloc(
            (nuint)sizeof(TwoJointIKSetup), 16);
        *_setupMem = new TwoJointIKSetup();

        try
        {
            // ScanText with E8 signature auto-follows the relative call to the function body.
            _solve = (delegate* unmanaged<byte*, TwoJointIKSetup*, hkaPose*, byte*>)
                scanner.ScanText(TwoJointIKSig);
            log.Information($"[FootIK] TwoJointIK solver resolved at 0x{(nint)_solve:X}");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[FootIK] Failed to resolve TwoJointIK solver — leg IK disabled.");
        }
    }

    public void Dispose()
    {
        NativeMemory.AlignedFree(_setupMem);
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Solves the thigh → knee → ankle IK chain so the ankle reaches
    /// <paramref name="targetMS"/> (model space), blended by <paramref name="weight"/>.
    /// </summary>
    public void SolveTwoJointIK(
        hkaPose* pose,
        int thighIdx, int kneeIdx, int ankleIdx,
        Vector3 targetMS,
        float weight)
    {
        if (_solve == null || weight <= 0f || pose == null) return;

        // For weight < 1: lerp target toward the animated ankle position.
        // The solver has no weight parameter, so we offset the target proportionally.
        if (weight < 1f)
        {
            var animT   = pose->AccessBoneModelSpace(ankleIdx,
                FFXIVClientStructs.Havok.Animation.Rig.hkaPose.PropagateOrNot.DontPropagate);
            var animPos = new Vector3(animT->Translation.X, animT->Translation.Y, animT->Translation.Z);
            targetMS    = Vector3.Lerp(animPos, targetMS, weight);
        }

        _setupMem->FirstJointIdx  = (short)thighIdx;
        _setupMem->SecondJointIdx = (short)kneeIdx;
        _setupMem->EndBoneIdx     = (short)ankleIdx;
        // Confirmed from Brio PoseInfo.cs: foot IK uses RotationAxis = -Vector3.UnitZ
        _setupMem->HingeAxisLS    = new Vector4(0f, 0f, -1f, 0f);
        _setupMem->EndTargetMS    = new Vector4(targetMS.X, targetMS.Y, targetMS.Z, 0f);

        byte notSure = 0;
        _solve(&notSure, _setupMem, pose);
    }
}
