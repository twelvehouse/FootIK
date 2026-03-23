using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.Havok.Animation.Rig;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace FootIK.Services;

/// <summary>
/// Main per-frame IK orchestration.
///
/// Two-phase design (mirrors Unity FootIK reference):
///   Phase A — FeetSolver: ground detection via BGCollision (Framework.Update).
///   Phase B — ApplyFootIK: TwoJointIK solve + ankle tilt (UpdateBonePhysics hook).
/// </summary>
public unsafe class FootIKService : IDisposable
{
    // ── Bone name constants ──────────────────────────────────────────────────
    // Confirmed from Brio source (PoseInfo.cs / BoneIKInfo.CalculateDefault):
    //   FirstBone=3 (j_asi_a, thigh), SecondBone=1 (j_asi_c, calf), EndBone=0 (j_asi_d, foot)
    //   j_asi_b (knee) is skipped — TwoJointIK only needs the two hinge joints + end effector.
    private const string BoneThighL = "j_asi_a_l";
    private const string BoneKneeL  = "j_asi_c_l";   // calf — the actual hinge for TwoJointIK
    private const string BoneAnkleL = "j_asi_d_l";   // foot — IK end effector target
    private const string BoneThighR = "j_asi_a_r";
    private const string BoneKneeR  = "j_asi_c_r";
    private const string BoneAnkleR = "j_asi_d_r";
    private const string BoneToeL   = "j_asi_e_l";   // toe — propagated after ankle rotation restore
    private const string BoneToeR   = "j_asi_e_r";

    // Front skirt / equipment bones.
    private const string BoneSkirtAL = "j_sk_f_a_l"; // left-leg front skirt root
    private const string BoneSkirtBL = "j_sk_f_b_l"; // left-leg front skirt mid
    private const string BoneSkirtCL = "j_sk_f_c_l"; // left-leg front skirt tip
    private const string BoneSkirtAR = "j_sk_f_a_r"; // right-leg front skirt root
    private const string BoneSkirtBR = "j_sk_f_b_r"; // right-leg front skirt mid
    private const string BoneSkirtCR = "j_sk_f_c_r"; // right-leg front skirt tip

    // ── Per-character state ───────────────────────────────────────────────────
    private sealed class CharState
    {
        public float FalloffWeight;
        public float AnimFilterWeight = 1f; // smoothly tracks IsAnimationAllowed (0=blocked, 1=allowed)
        public readonly float[]   LastFootY    = new float[2];
        public readonly float[]   GroundY      = new float[2];
        public readonly float[]   GroundYToe   = new float[2];
        public readonly bool[]    Grounded     = new bool[2];
        public readonly Vector3[] HeelWorldPos = new Vector3[2];
        public Vector3 LastPosition;
        public float   Velocity = 1f;
        // Populated during Phase B for debug snapshot (used by local player only).
        public Vector3    DbgCharPos;
        public Quaternion DbgCharRot;
        public Vector3    DbgIkTargetL, DbgIkTargetR;
    }

    private readonly Dictionary<ulong, CharState> _charStates = new();

    private CharState GetOrCreate(ulong id)
    {
        if (!_charStates.TryGetValue(id, out var s))
            _charStates[id] = s = new CharState();
        return s;
    }

    // ── Debug snapshot (local player only, when ShowDebugOverlay is on) ───────
    public struct DebugSnapshot
    {
        public bool    Valid;
        public Vector3 ThighL, KneeL, AnkleL, ToeL;
        public Vector3 ThighR, KneeR, AnkleR, ToeR;
        public Vector3 HeelGroundL, ToeGroundL;
        public Vector3 HeelGroundR, ToeGroundR;
        public Vector3 IkTargetL, IkTargetR;
    }

    public DebugSnapshot LastDebug;

    // ── Animation filter ─────────────────────────────────────────────────────
    // Hardcoded base rules (not user-editable).
    private static readonly string[] BaseAllow =
    [
        @"^emote/",     // confirmed emotes
        @"^normal/",    // normal idle / standing animations
    ];
    private static readonly string[] BaseDeny =
    [
        @"^emote/s_",   // chair-sit emotes  (emote/s_chair_…)
        @"^emote/l_",   // lying emotes       (emote/l_sleep_…)
        @"^emote/j_",   // ground-sit emotes  (emote/j_sit_…)
        @"^emote/sit",  // base sit           (emote/sit)
        @"^emote/jmn",  // base ground-sit    (emote/jmn)
        @"^emote/dance", // dance emotes
        // emote_bed_liedown_{start,loop,end} uses "_" not "/" → blocked by base allow already
    ];

    // Cache: animId → key string looked up from ActionTimeline sheet.
    private readonly Dictionary<ushort, string> _animKeyCache = new();
    // Cache: animId → IsAllowed result (invalidated when user exception lists change).
    private readonly Dictionary<ushort, bool>   _animAllowCache = new();
    private int _lastExtraPatternVersion = -1;

    private readonly ExcelSheet<ActionTimeline>? _actionTimelineSheet;

    private bool _ikAvailabilityLogged;

    private readonly Config                 _config;
    private readonly IObjectTable           _objects;
    private readonly IFramework             _framework;
    private readonly GroundDetectionService _ground;
    private readonly HavokIKService         _havokIK;
    private readonly SkeletonAccessService  _skeleton;
    private readonly IPluginLog             _log;

    public FootIKService(
        Config config,
        IObjectTable objects,
        IFramework framework,
        GroundDetectionService ground,
        HavokIKService havokIK,
        SkeletonAccessService skeleton,
        IDataManager dataManager,
        IPluginLog log)
    {
        _config    = config;
        _objects   = objects;
        _framework = framework;
        _ground    = ground;
        _havokIK   = havokIK;
        _skeleton  = skeleton;
        _log       = log;
        _actionTimelineSheet = dataManager.GetExcelSheet<ActionTimeline>();

        _framework.Update             += OnFrameworkUpdate;
        _skeleton.OnBonePhysicsUpdate += OnBonePhysicsUpdate;
    }

    public void Dispose()
    {
        _framework.Update             -= OnFrameworkUpdate;
        _skeleton.OnBonePhysicsUpdate -= OnBonePhysicsUpdate;
    }

    // ── Phase A — FeetSolver (Framework.Update) ──────────────────────────────

    private void OnFrameworkUpdate(IFramework fw)
    {
        if (!_config.Enabled)
        {
            _charStates.Clear();
            return;
        }

        float dt      = (float)fw.UpdateDelta.TotalSeconds;
        ulong localId = _objects.LocalPlayer?.GameObjectId ?? ulong.MaxValue;

        var activeIds = new HashSet<ulong>();

        foreach (var obj in _objects)
        {
            if (obj is not ICharacter chr) continue;
            bool isLocal = obj.GameObjectId == localId;
            if (!isLocal && !_config.ApplyToAll) continue;

            var charaBase = SkeletonAccessService.GetCharaBase(chr);
            if (charaBase == null) continue;
            var skel = charaBase->Skeleton;
            if (skel == null || skel->PartialSkeletonCount == 0) continue;

            activeIds.Add(obj.GameObjectId);
            var state = GetOrCreate(obj.GameObjectId);

            var rootPos = chr.Position;
            var charRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, chr.Rotation);

            state.Velocity     = MathF.Max(1f, Vector3.Distance(rootPos, state.LastPosition) / MathF.Max(dt, 0.001f));
            state.LastPosition = rootPos;

            // Find ankle/toe bones across all partial skeletons.
            hkaPose* pose  = null;
            int ankleL = -1, ankleR = -1, toeL = -1, toeR = -1;
            for (int pi = 0; pi < skel->PartialSkeletonCount; pi++)
            {
                var p = SkeletonAccessService.GetPose(&skel->PartialSkeletons[pi]);
                if (p == null) continue;
                int al = _skeleton.FindBoneIndex(p, BoneAnkleL);
                int ar = _skeleton.FindBoneIndex(p, BoneAnkleR);
                if (al >= 0 || ar >= 0)
                {
                    pose   = p;
                    ankleL = al;
                    ankleR = ar;
                    toeL   = _skeleton.FindBoneIndex(p, BoneToeL);
                    toeR   = _skeleton.FindBoneIndex(p, BoneToeR);
                    break;
                }
            }
            if (pose == null) continue;

            FeetSolver(state, pose, 0, ankleL, toeL, rootPos, charRot);
            FeetSolver(state, pose, 1, ankleR, toeR, rootPos, charRot);

            bool isGrounded = state.Grounded[0] || state.Grounded[1];
            // velocity=1f: falloff blending should not be velocity-scaled.
            // Velocity scaling on FalloffWeight caused IK to snap off instantly while running.
            state.FalloffWeight = MoveTowards(
                state.FalloffWeight,
                isGrounded ? 1f : 0f,
                _config.IkFalloffIncreaseSpeed,
                _config.IkFalloffDecreaseSpeed,
                1f, dt);

        }

        // Remove stale states (characters that left the object table).
        foreach (var key in _charStates.Keys.Where(k => !activeIds.Contains(k)).ToList())
            _charStates.Remove(key);

    }

    private void FeetSolver(CharState state, hkaPose* pose, int side, int ankleIdx, int toeIdx,
        Vector3 rootWorldPos, Quaternion charRot)
    {
        var ankleMS    = SkeletonAccessService.GetBonePosMS(pose, ankleIdx);
        var ankleWorld = rootWorldPos + Vector3.Transform(ankleMS, charRot);

        // Physical heel estimate: extend behind ankle in foot's own direction.
        Vector3 heelWorld;
        if (toeIdx >= 0)
        {
            var toeMS  = SkeletonAccessService.GetBonePosMS(pose, toeIdx);
            var toHeel = ankleMS - toeMS;
            var heelMS = ankleMS + toHeel * 0.5f;
            heelWorld  = rootWorldPos + Vector3.Transform(heelMS, charRot);
        }
        else
        {
            heelWorld = ankleWorld;
        }
        state.HeelWorldPos[side] = heelWorld;

        var hit = _ground.Query(heelWorld, rootWorldPos.Y, _config.MaxStep);
        if (hit.DidHit)
        {
            state.GroundY[side]  = hit.GroundY;
            state.Grounded[side] = (rootWorldPos.Y - hit.GroundY) < _config.MaxStep;
        }
        else
        {
            state.GroundY[side]  = rootWorldPos.Y - _config.MaxStep;
            state.Grounded[side] = false;
        }

        if (toeIdx >= 0)
        {
            var toeMS    = SkeletonAccessService.GetBonePosMS(pose, toeIdx);
            var toeWorld = rootWorldPos + Vector3.Transform(toeMS, charRot);
            var toeHit   = _ground.Query(toeWorld, rootWorldPos.Y, _config.MaxStep);
            state.GroundYToe[side] = toeHit.DidHit ? toeHit.GroundY : state.GroundY[side];
        }
        else
        {
            state.GroundYToe[side] = state.GroundY[side];
        }
    }

    // ── Phase B — ApplyFootIK (UpdateBonePhysics hook) ───────────────────────

    private void OnBonePhysicsUpdate()
    {
        if (!_config.Enabled) return;

        if (!_ikAvailabilityLogged)
        {
            _ikAvailabilityLogged = true;
            if (!_havokIK.IsAvailable)
                _log.Warning("[FootIK] TwoJointIK solver unavailable — leg IK disabled.");
            else
                _log.Information("[FootIK] TwoJointIK solver active.");
        }

        ulong localId = _objects.LocalPlayer?.GameObjectId ?? ulong.MaxValue;

        foreach (var obj in _objects)
        {
            if (obj is not ICharacter chr) continue;
            bool isLocal = obj.GameObjectId == localId;
            if (!isLocal && !_config.ApplyToAll) continue;

            if (!_charStates.TryGetValue(obj.GameObjectId, out var state)) continue;
            if (state.FalloffWeight <= 0f) continue;

            var charaBase = SkeletonAccessService.GetCharaBase(chr);
            if (charaBase == null) continue;
            var skel = charaBase->Skeleton;
            if (skel == null || skel->PartialSkeletonCount == 0) continue;

            hkaPose* legPose = null;
            int thighL = -1, kneeL = -1, ankleL = -1, toeL = -1;
            int thighR = -1, kneeR = -1, ankleR = -1, toeR = -1;
            int skirtAL = -1, skirtBL = -1, skirtCL = -1;
            int skirtAR = -1, skirtBR = -1, skirtCR = -1;

            for (int pi = 0; pi < skel->PartialSkeletonCount; pi++)
            {
                var p = SkeletonAccessService.GetPose(&skel->PartialSkeletons[pi]);
                if (p == null) continue;
                int tl = _skeleton.FindBoneIndex(p, BoneThighL);
                int kl = _skeleton.FindBoneIndex(p, BoneKneeL);
                int al = _skeleton.FindBoneIndex(p, BoneAnkleL);
                if (tl >= 0 && kl >= 0 && al >= 0)
                {
                    legPose = p;
                    thighL = tl; kneeL = kl; ankleL = al;
                    toeL   = _skeleton.FindBoneIndex(p, BoneToeL);
                    thighR = _skeleton.FindBoneIndex(p, BoneThighR);
                    kneeR  = _skeleton.FindBoneIndex(p, BoneKneeR);
                    ankleR = _skeleton.FindBoneIndex(p, BoneAnkleR);
                    toeR   = _skeleton.FindBoneIndex(p, BoneToeR);
                    // Skirt bones — may be in same partial or -1 if absent/different partial
                    skirtAL = _skeleton.FindBoneIndex(p, BoneSkirtAL);
                    skirtBL = _skeleton.FindBoneIndex(p, BoneSkirtBL);
                    skirtCL = _skeleton.FindBoneIndex(p, BoneSkirtCL);
                    skirtAR = _skeleton.FindBoneIndex(p, BoneSkirtAR);
                    skirtBR = _skeleton.FindBoneIndex(p, BoneSkirtBR);
                    skirtCR = _skeleton.FindBoneIndex(p, BoneSkirtCR);
                    break;
                }
            }
            if (legPose == null) continue;

            float rootY = chr.Position.Y;
            float dt    = (float)_framework.UpdateDelta.TotalSeconds;

            state.DbgCharPos = chr.Position;
            state.DbgCharRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, chr.Rotation);

            // Animation filter: smoothly blend in/out instead of hard cut.
            float animTarget = IsAnimationAllowed((Character*)chr.Address) ? 1f : 0f;
            state.AnimFilterWeight = MoveTowards(
                state.AnimFilterWeight, animTarget,
                _config.IkFalloffIncreaseSpeed, _config.IkFalloffDecreaseSpeed, 1f, dt);

            float eff = state.FalloffWeight * state.AnimFilterWeight * _config.Weight;
            if (eff <= 0.001f) continue;

            // Both-feet cancel: if both smoothed corrections exceed the threshold in the same
            // direction, the whole body is likely elevated (e.g. a jumping emote). Cancel IK
            // for both feet and smoothly return their corrections to zero.
            float cancelThreshold = _config.BothFeetCancelThreshold;
            bool cancelBoth = cancelThreshold > 0f
                && MathF.Abs(state.LastFootY[0]) >= cancelThreshold
                && MathF.Abs(state.LastFootY[1]) >= cancelThreshold
                && MathF.Sign(state.LastFootY[0]) == MathF.Sign(state.LastFootY[1]);

            if (cancelBoth)
            {
                float decaySpeed = _config.IkFalloffDecreaseSpeed;
                state.LastFootY[0] = MoveTowards(state.LastFootY[0], 0f, decaySpeed, decaySpeed, state.Velocity, dt);
                state.LastFootY[1] = MoveTowards(state.LastFootY[1], 0f, decaySpeed, decaySpeed, state.Velocity, dt);
            }
            else if (_config.SingleFootOnly)
            {
                // Determine dominant foot from raw ground geometry (not smoothed state).
                // This avoids instability when both corrections are near zero.
                float d0 = MathF.Abs(Math.Clamp(state.GroundY[0] - rootY, -_config.MaxStep, _config.MaxStep));
                float d1 = MathF.Abs(Math.Clamp(state.GroundY[1] - rootY, -_config.MaxStep, _config.MaxStep));
                bool leftDominant = d0 >= d1;
                MoveIK(state, legPose, 0, thighL, kneeL, ankleL, toeL, skirtAL, skirtBL, skirtCL, rootY, eff, dt, apply: leftDominant);
                MoveIK(state, legPose, 1, thighR, kneeR, ankleR, toeR, skirtAR, skirtBR, skirtCR, rootY, eff, dt, apply: !leftDominant);
            }
            else
            {
                MoveIK(state, legPose, 0, thighL, kneeL, ankleL, toeL, skirtAL, skirtBL, skirtCL, rootY, eff, dt);
                MoveIK(state, legPose, 1, thighR, kneeR, ankleR, toeR, skirtAR, skirtBR, skirtCR, rootY, eff, dt);
            }

            // Build debug snapshot for local player.
            if (isLocal)
            {
                if (_config.ShowDebugOverlay)
                {
                    var cp = state.DbgCharPos;
                    var cr = state.DbgCharRot;
                    var aL = BoneWorld(legPose, ankleL, cp, cr);
                    var tL = BoneWorld(legPose, toeL,   cp, cr);
                    var aR = BoneWorld(legPose, ankleR, cp, cr);
                    var tR = BoneWorld(legPose, toeR,   cp, cr);
                    LastDebug = new DebugSnapshot
                    {
                        Valid       = true,
                        ThighL      = BoneWorld(legPose, thighL, cp, cr),
                        KneeL       = BoneWorld(legPose, kneeL,  cp, cr),
                        AnkleL      = aL, ToeL = tL,
                        ThighR      = BoneWorld(legPose, thighR, cp, cr),
                        KneeR       = BoneWorld(legPose, kneeR,  cp, cr),
                        AnkleR      = aR, ToeR = tR,
                        HeelGroundL = new Vector3(state.HeelWorldPos[0].X, state.GroundY[0],    state.HeelWorldPos[0].Z),
                        ToeGroundL  = new Vector3(tL.X,                    state.GroundYToe[0], tL.Z),
                        HeelGroundR = new Vector3(state.HeelWorldPos[1].X, state.GroundY[1],    state.HeelWorldPos[1].Z),
                        ToeGroundR  = new Vector3(tR.X,                    state.GroundYToe[1], tR.Z),
                        IkTargetL   = state.DbgIkTargetL,
                        IkTargetR   = state.DbgIkTargetR,
                    };
                }
                else
                {
                    LastDebug = default;
                }
            }
        }
    }

    // ── Animation filter ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if IK should be applied for this character's current base animation.
    /// Precedence (highest first):
    ///   1. ExtraDeny  → block
    ///   2. ExtraAllow → allow (overrides base deny)
    ///   3. BaseDeny   → block
    ///   4. BaseAllow  → allow
    ///   5. default    → block
    /// </summary>
    private bool IsAnimationAllowed(Character* native)
    {
        // Invalidate cache when user exception lists are modified.
        int version = _config.ExtraAllowPatterns.Count ^ (_config.ExtraDenyPatterns.Count << 16);
        if (version != _lastExtraPatternVersion)
        {
            _animAllowCache.Clear();
            _lastExtraPatternVersion = version;
        }

        var animId = (ushort)native->Timeline.TimelineSequencer.TimelineIds[0]; // slot 0 = Base
        if (animId == 0) return true; // no animation, allow

        if (_animAllowCache.TryGetValue(animId, out var cached)) return cached;

        var key = GetAnimationKey(animId);

        bool result = false;
        if (key != null)
        {
            var extraDeny  = _config.ExtraDenyPatterns;
            var extraAllow = _config.ExtraAllowPatterns;

            if (extraDeny.Count  > 0 && extraDeny.Any(p  => Regex.IsMatch(key, p))) result = false;
            else if (extraAllow.Count > 0 && extraAllow.Any(p => Regex.IsMatch(key, p))) result = true;
            else if (BaseDeny.Any(p => Regex.IsMatch(key, p))) result = false;
            else result = BaseAllow.Any(p => Regex.IsMatch(key, p));
        }

        _animAllowCache[animId] = result;
        return result;
    }

    private string? GetAnimationKey(ushort animId)
    {
        if (_animKeyCache.TryGetValue(animId, out var cached)) return cached;
        var key = _actionTimelineSheet?.GetRow(animId).Key.ToString() ?? string.Empty;
        _animKeyCache[animId] = key;
        return string.IsNullOrEmpty(key) ? null : key;
    }

    private static Vector3 BoneWorld(hkaPose* pose, int idx, Vector3 charPos, Quaternion charRot)
    {
        if (idx < 0) return charPos;
        return charPos + Vector3.Transform(SkeletonAccessService.GetBonePosMS(pose, idx), charRot);
    }

    // ── MoveIK (per-leg) ─────────────────────────────────────────────────────

    private void MoveIK(
        CharState state,
        hkaPose* pose,
        int side, int thigh, int knee, int ankle, int toe,
        int skirtA, int skirtB, int skirtC,
        float rootY, float eff, float dt, bool apply = true)
    {
        if (thigh < 0 || knee < 0 || ankle < 0) return;

        var animAnkleMS = SkeletonAccessService.GetBonePosMS(pose, ankle);

        float deltaY = state.GroundY[side] - rootY;
        deltaY = Math.Clamp(deltaY, -_config.MaxStep, +_config.MaxStep);

        state.LastFootY[side] = MoveTowards(
            state.LastFootY[side], deltaY,
            _config.FeetPositionSpeed, _config.FeetPositionSpeed,
            1f, dt);

        // Smoothing always runs so LastFootY stays current even for the non-dominant foot.
        // The actual IK solve is skipped when apply=false.
        if (!apply) return;

        var   ikTarget   = new Vector3(animAnkleMS.X, animAnkleMS.Y + state.LastFootY[side], animAnkleMS.Z);
        float posWeight  = _config.FootPositionWeight * eff;

        if (_config.ShowDebugOverlay)
        {
            var tw = state.DbgCharPos + Vector3.Transform(ikTarget, state.DbgCharRot);
            if (side == 0) state.DbgIkTargetL = tw;
            else           state.DbgIkTargetR = tw;
        }

        var preIkAnkleRot = SkeletonAccessService.GetBoneRotMS(pose, ankle);
        var preThighRot   = SkeletonAccessService.GetBoneRotMS(pose, thigh);

        _havokIK.SolveTwoJointIK(pose, thigh, knee, ankle, ikTarget, posWeight);

        var postThighRot  = SkeletonAccessService.GetBoneRotMS(pose, thigh);
        var deltaThighRot = Quaternion.Normalize(postThighRot * Quaternion.Inverse(preThighRot));

        float rotWeight = _config.FootRotationWeight * eff;
        if (rotWeight > 0f)
        {
            var postIkAnkleRot = SkeletonAccessService.GetBoneRotMS(pose, ankle);
            var restored       = Quaternion.Normalize(Quaternion.Slerp(postIkAnkleRot, preIkAnkleRot, rotWeight));
            SkeletonAccessService.SetBoneRotMS(pose, ankle, restored);

            // Propagate ankle rotation delta to the toe child bone.
            // SetBoneRotMS uses DontPropagate, so without this the toe appears bent/disconnected.
            if (toe >= 0)
            {
                var deltaRot = Quaternion.Normalize(restored * Quaternion.Inverse(postIkAnkleRot));
                var anklePos = SkeletonAccessService.GetBonePosMS(pose, ankle);
                var toePos   = SkeletonAccessService.GetBonePosMS(pose, toe);
                var toeRot   = SkeletonAccessService.GetBoneRotMS(pose, toe);
                SkeletonAccessService.SetBonePosMS(pose, toe,
                    anklePos + Vector3.Transform(toePos - anklePos, deltaRot));
                SkeletonAccessService.SetBoneRotMS(pose, toe,
                    Quaternion.Normalize(deltaRot * toeRot));
            }
        }

        if (_config.SkirtCorrectionEnabled && skirtA >= 0)
            ApplySkirtCorrection(pose, skirtA, skirtB, skirtC, deltaThighRot);
    }

    // ── Skirt correction ─────────────────────────────────────────────────────

    /// <summary>
    /// Applies a fraction of the thigh IK rotation delta to front-skirt bones to reduce clipping.
    /// Each bone in the chain (A→B→C) gets a user-configured fraction (mult0/1/2) of the delta.
    /// Positions are propagated from parent to child so the chain stays visually intact.
    /// </summary>
    private void ApplySkirtCorrection(hkaPose* pose, int skirtA, int skirtB, int skirtC,
        Quaternion deltaThighRot)
    {
        var id = Quaternion.Identity;

        // ── Bone A (root) ────────────────────────────────────────────────────
        var posA   = SkeletonAccessService.GetBonePosMS(pose, skirtA);
        var rotA   = SkeletonAccessService.GetBoneRotMS(pose, skirtA);
        var deltaA = Quaternion.Normalize(Quaternion.Slerp(id, deltaThighRot, _config.SkirtMult0));
        SkeletonAccessService.SetBoneRotMS(pose, skirtA, Quaternion.Normalize(deltaA * rotA));

        if (skirtB < 0) return;

        // ── Bone B (mid) ─────────────────────────────────────────────────────
        var posB    = SkeletonAccessService.GetBonePosMS(pose, skirtB);
        var rotB    = SkeletonAccessService.GetBoneRotMS(pose, skirtB);
        var newPosB = posA + Vector3.Transform(posB - posA, deltaA);
        SkeletonAccessService.SetBonePosMS(pose, skirtB, newPosB);
        var deltaB = Quaternion.Normalize(Quaternion.Slerp(id, deltaThighRot, _config.SkirtMult1));
        SkeletonAccessService.SetBoneRotMS(pose, skirtB, Quaternion.Normalize(deltaB * rotB));

        if (skirtC < 0) return;

        // ── Bone C (tip) ─────────────────────────────────────────────────────
        var posC          = SkeletonAccessService.GetBonePosMS(pose, skirtC);
        var rotC          = SkeletonAccessService.GetBoneRotMS(pose, skirtC);
        // Propagate: A's delta moves C around A's pivot first, then B's incremental delta
        // moves it around B's new pivot.
        var posCafterA    = posA + Vector3.Transform(posC - posA, deltaA);
        var deltaBonly    = Quaternion.Normalize(deltaB * Quaternion.Inverse(deltaA));
        var newPosC       = newPosB + Vector3.Transform(posCafterA - newPosB, deltaBonly);
        SkeletonAccessService.SetBonePosMS(pose, skirtC, newPosC);
        var deltaC = Quaternion.Normalize(Quaternion.Slerp(id, deltaThighRot, _config.SkirtMult2));
        SkeletonAccessService.SetBoneRotMS(pose, skirtC, Quaternion.Normalize(deltaC * rotC));
    }

    // ── Math helpers ─────────────────────────────────────────────────────────

    private static float MoveTowards(float current, float target,
        float increaseSpeed, float decreaseSpeed, float velocity, float dt)
    {
        if (current == target) return target;
        float speed = ((current < target) ? increaseSpeed : decreaseSpeed) * velocity;
        float delta = target - current;
        return MathF.Abs(delta) <= speed * dt
            ? target
            : current + MathF.Sign(delta) * speed * dt;
    }
}
