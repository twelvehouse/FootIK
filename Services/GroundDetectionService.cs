using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace FootIK.Services;

/// <summary>
/// Wraps BGCollision sphere-sweep / raycast to find ground height and surface normal
/// below a given XZ position.
/// </summary>
public class GroundDetectionService
{
    private readonly IPluginLog _log;

    public GroundDetectionService(IPluginLog log)
    {
        _log = log;
    }

    // ── Result ────────────────────────────────────────────────────────────────

    public readonly struct GroundHit
    {
        public readonly bool    DidHit;
        public readonly float   GroundY;
        public readonly Vector3 Normal;

        public GroundHit(float groundY, Vector3 normal)
        {
            DidHit  = true;
            GroundY = groundY;
            Normal  = normal;
        }

        public static readonly GroundHit Miss = default;
    }

    // ── Query ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Casts downward from (XZ of <paramref name="ankleWorldPos"/>, rootY + maxStep) to
    /// detect the ground beneath the foot.  Uses sphere sweep first, falls back to raycast.
    /// Mirrors the Unity FootIK reference pattern.
    /// </summary>
    public GroundHit Query(Vector3 ankleWorldPos, float characterRootY, float maxStep)
    {
        // Normalize the ray origin to rootY + maxStep (animation-independent).
        var origin  = new Vector3(ankleWorldPos.X, characterRootY + maxStep, ankleWorldPos.Z);
        var dir     = new Vector3(0f, -1f, 0f);
        float range = maxStep * 2f;

        // Sphere sweep (bit 0 set in algorithm type) — better handles thin geometry.
        // Falls back to simple raycast on miss.
        if (BGCollisionModule.SweepSphereMaterialFilter(origin, dir, out var hit, range))
            return new GroundHit(hit.Point.Y, hit.Normal);

        if (BGCollisionModule.RaycastMaterialFilter(origin, dir, out hit, range))
            return new GroundHit(hit.Point.Y, hit.Normal);

        return GroundHit.Miss;
    }
}
