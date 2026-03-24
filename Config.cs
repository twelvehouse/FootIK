using Dalamud.Configuration;

namespace FootIK;

[Serializable]
public class Config : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // ── Global toggles ───────────────────────────────────────────────────────
    public bool Enabled { get; set; } = true;

    /// <summary>When true, apply IK to all nearby characters in addition to the local player.</summary>
    public bool ApplyToAll { get; set; } = false;

    /// <summary>Master blend weight [0, 1].</summary>
    public float Weight { get; set; } = 1f;

    // ── Ground detection ─────────────────────────────────────────────────────
    /// <summary>Maximum ground height deviation to track (metres).</summary>
    public float MaxStep { get; set; } = 0.5f;

    // ── Speeds ───────────────────────────────────────────────────────────────
    public float FeetPositionSpeed { get; set; } = 2f;

    public float IkFalloffIncreaseSpeed { get; set; } = 5f;
    public float IkFalloffDecreaseSpeed { get; set; } = 10f;

    // ── Weights ──────────────────────────────────────────────────────────────
    public float FootPositionWeight { get; set; } = 1f;
    public float FootRotationWeight { get; set; } = 1f;

    /// <summary>
    /// If both feet's smoothed correction exceeds this value (metres) in the same direction,
    /// IK is cancelled for both feet and corrections smoothly return to zero.
    /// Prevents both legs from stretching during elevated emotes.
    /// Set to 0 to disable the check.
    /// </summary>
    public float BothFeetCancelThreshold { get; set; } = 0.10f;

    /// <summary>
    /// When true, IK is applied only to the foot that requires the larger correction
    /// (determined from ground geometry each frame). The other foot's smoothing
    /// continues but its IK solve is skipped, preserving its animation pose.
    /// </summary>
    public bool SingleFootOnly { get; set; } = true;

    // ── Animation filter ─────────────────────────────────────────────────────
    // ── Skirt correction ─────────────────────────────────────────────────────
    /// <summary>Apply IK thigh-delta rotation to front skirt/equipment bones.</summary>
    public bool  SkirtCorrectionEnabled { get; set; } = true;
    /// <summary>Rotation multiplier for the root skirt bone (j_sk_f_a).</summary>
    public float SkirtMult0 { get; set; } = 1.00f;
    /// <summary>Rotation multiplier for the mid skirt bone (j_sk_f_b).</summary>
    public float SkirtMult1 { get; set; } = 0.50f;
    /// <summary>Rotation multiplier for the tip skirt bone (j_sk_f_c).</summary>
    public float SkirtMult2 { get; set; } = 0.25f;

    // ── Animation filter — user exceptions ───────────────────────────────────
    // Base allow/deny rules are hardcoded in FootIKService.
    // These lists let users add exceptions on top of those rules.
    // Precedence (highest first):
    //   1. ExtraDeny  → always block
    //   2. ExtraAllow → always allow (overrides base deny)
    //   3. Base deny  → block
    //   4. Base allow → allow
    //   5. default    → block

    /// <summary>User-defined allow exceptions. Override base deny rules.</summary>
    public List<string> ExtraAllowPatterns { get; set; } = [];

    /// <summary>User-defined deny exceptions. Override everything.</summary>
    public List<string> ExtraDenyPatterns  { get; set; } = [];

    // ── Condition-based disable ──────────────────────────────────────────────
    /// <summary>Disable IK during in-engine cutscenes.</summary>
    public bool DisableInCutscene { get; set; } = true;
    /// <summary>Disable IK while in Group Pose (GPose).</summary>
    public bool DisableInGPose    { get; set; } = true;
    /// <summary>Disable IK while bound by duty (instances, trials, raids).</summary>
    public bool DisableInDuty     { get; set; } = false;

    // ── Debug ────────────────────────────────────────────────────────────────
    public bool ShowDebugOverlay { get; set; } = false;
}
