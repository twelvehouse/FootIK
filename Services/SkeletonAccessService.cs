using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation.Rig;
using static FFXIVClientStructs.Havok.Animation.Rig.hkaPose;

namespace FootIK.Services;

/// <summary>
/// Hooks UpdateBonePhysics (post-animation, pre-render) and exposes:
/// <list type="bullet">
///   <item>An event fired once per physics update tick.</item>
///   <item>Helpers to find bone indices by name and read/write hkaPose data.</item>
/// </list>
/// </summary>
public unsafe class SkeletonAccessService : IDisposable
{
    // Signature from Brio — matches the UpdateBonePhysics function body.
    private const string UpdateBonePhysicsSig =
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 56 48 83 EC ?? 48 8B 59 ?? 45 33 E4";

    // Exact delegate from Brio: one nint arg, returns nint.
    private delegate nint UpdateBonePhysicsDelegate(nint a1);

    /// <summary>
    /// Fired after the game's UpdateBonePhysics returns.
    /// Subscribers should access skeletons via IClientState / CharacterBase.
    /// </summary>
    public event Action? OnBonePhysicsUpdate;

    private readonly Hook<UpdateBonePhysicsDelegate>? _hook;
    private readonly IPluginLog _log;

    // Bone index cache: (hkaSkeleton* as nint) -> { boneName -> index }
    private readonly Dictionary<nint, Dictionary<string, int>> _boneCache = new();

    public SkeletonAccessService(ISigScanner scanner, IGameInteropProvider interop, IPluginLog log)
    {
        _log = log;

        try
        {
            _hook = interop.HookFromAddress<UpdateBonePhysicsDelegate>(
                scanner.ScanText(UpdateBonePhysicsSig),
                Detour);
            _hook.Enable();
            log.Information("[FootIK] UpdateBonePhysics hook installed.");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[FootIK] Failed to install UpdateBonePhysics hook.");
        }
    }

    public void Dispose()
    {
        _hook?.Disable();
        _hook?.Dispose();
    }

    // ── Hook detour ──────────────────────────────────────────────────────────

    private nint Detour(nint a1)
    {
        // Call original first — animation is fully updated after this returns.
        var result = _hook!.Original(a1);

        try
        {
            OnBonePhysicsUpdate?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[FootIK] Exception in UpdateBonePhysics detour.");
        }

        return result;
    }

    // ── Bone index helpers ───────────────────────────────────────────────────

    /// <summary>Finds a bone index by exact name, caching results per skeleton rig.</summary>
    public int FindBoneIndex(hkaPose* pose, string boneName)
    {
        if (pose == null || pose->Skeleton == null) return -1;

        var key = (nint)pose->Skeleton;
        if (!_boneCache.TryGetValue(key, out var dict))
        {
            dict = BuildCache(pose->Skeleton);
            _boneCache[key] = dict;
        }

        return dict.GetValueOrDefault(boneName, -1);
    }

    private static Dictionary<string, int> BuildCache(hkaSkeleton* rig)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < rig->Bones.Length; i++)
        {
            var name = rig->Bones[i].Name.String;
            if (name != null)
                d[name] = i;
        }
        return d;
    }

    /// <summary>
    /// Dumps all bone names and indices in the given pose's skeleton to the log.
    /// Use via /footik bones for diagnostics.
    /// </summary>
    public void DumpBoneNames(hkaPose* pose, IPluginLog log)
    {
        if (pose == null || pose->Skeleton == null)
        {
            log.Information("[FootIK] pose or skeleton is null.");
            return;
        }
        var rig = pose->Skeleton;
        log.Information($"[FootIK] Skeleton has {rig->Bones.Length} bones:");
        for (int i = 0; i < rig->Bones.Length; i++)
        {
            var name = rig->Bones[i].Name.String ?? "(null)";
            log.Information($"[FootIK]   [{i:D3}] {name}");
        }
    }

    // ── Pose read/write helpers ──────────────────────────────────────────────

    /// <summary>Returns the model-space position of a bone as Vector3.</summary>
    public static Vector3 GetBonePosMS(hkaPose* pose, int idx)
    {
        if (idx < 0) return Vector3.Zero;
        var t = pose->AccessBoneModelSpace(idx, PropagateOrNot.Propagate);
        return new Vector3(t->Translation.X, t->Translation.Y, t->Translation.Z);
    }

    /// <summary>Sets the model-space XYZ position of a bone in-place.</summary>
    public static void SetBonePosMS(hkaPose* pose, int idx, Vector3 pos)
    {
        if (idx < 0) return;
        var t = pose->AccessBoneModelSpace(idx, PropagateOrNot.DontPropagate);
        t->Translation.X = pos.X;
        t->Translation.Y = pos.Y;
        t->Translation.Z = pos.Z;
    }

    /// <summary>Returns the model-space rotation of a bone as Quaternion.</summary>
    public static Quaternion GetBoneRotMS(hkaPose* pose, int idx)
    {
        if (idx < 0) return Quaternion.Identity;
        var r = pose->AccessBoneModelSpace(idx, PropagateOrNot.DontPropagate)->Rotation;
        return new Quaternion(r.X, r.Y, r.Z, r.W);
    }

    /// <summary>Writes a model-space rotation to a bone in-place.</summary>
    public static void SetBoneRotMS(hkaPose* pose, int idx, Quaternion q)
    {
        if (idx < 0) return;
        var r = &pose->AccessBoneModelSpace(idx, PropagateOrNot.DontPropagate)->Rotation;
        r->X = q.X; r->Y = q.Y; r->Z = q.Z; r->W = q.W;
    }

    // ── CharacterBase helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Returns the CharacterBase* draw object for a game object, or null.
    /// For local player / party members the DrawObject is always CharacterBase.
    /// </summary>
    public static CharacterBase* GetCharaBase(IGameObject obj)
    {
        var chr  = (Character*)obj.Address;
        var draw = chr->GameObject.DrawObject;
        return draw == null ? null : (CharacterBase*)draw;
    }

    /// <summary>Returns the hkaPose* for slot 0 of a partial skeleton, or null.</summary>
    public static hkaPose* GetPose(PartialSkeleton* ps) => ps->GetHavokPose(0);
}
