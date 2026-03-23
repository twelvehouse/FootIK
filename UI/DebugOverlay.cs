using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FootIK.Services;

namespace FootIK.UI;

/// <summary>
/// Draws a 3D debug overlay using ImGui's background draw list.
/// Shows bone positions, raycasts, ground hits, and IK targets.
/// </summary>
public class DebugOverlay
{
    // IM_COL32(r,g,b,a) — the uint color format ImGui draw lists use.
    private static uint Col(byte r, byte g, byte b, byte a = 255) =>
        (uint)(a << 24 | b << 16 | g << 8 | r);

    private static readonly uint ColBone       = Col(255, 220,   0);       // yellow  — skeleton
    private static readonly uint ColRay        = Col(255,  80,  80, 160);  // red     — raycasts
    private static readonly uint ColHeelGround = Col(255, 160,   0);       // orange  — heel ground hit
    private static readonly uint ColToeGround  = Col(  0, 220,  80);       // green   — toe ground hit
    private static readonly uint ColTarget     = Col( 50, 210, 255);       // cyan    — IK target
    private static readonly uint ColLabel      = Col(255, 255, 255, 210);  // white   — text

    private readonly IGameGui _gameGui;

    public DebugOverlay(IGameGui gameGui) => _gameGui = gameGui;

    public void Draw(in FootIKService.DebugSnapshot snap)
    {
        if (!snap.Valid) return;

        var dl = ImGui.GetBackgroundDrawList();

        DrawLeg(dl,
            snap.ThighL, snap.KneeL, snap.AnkleL, snap.ToeL,
            snap.HeelGroundL, snap.ToeGroundL, snap.IkTargetL, "L");

        DrawLeg(dl,
            snap.ThighR, snap.KneeR, snap.AnkleR, snap.ToeR,
            snap.HeelGroundR, snap.ToeGroundR, snap.IkTargetR, "R");
    }

    private void DrawLeg(
        ImDrawListPtr dl,
        Vector3 thigh, Vector3 knee, Vector3 ankle, Vector3 toe,
        Vector3 heelGround, Vector3 toeGround, Vector3 ikTarget,
        string side)
    {
        // ── Raycasts (drawn first so they appear behind dots) ────────────────
        Line(dl, ankle, heelGround, ColRay, 1f);
        Line(dl, toe,   toeGround,  ColRay, 1f);

        // ── Skeleton chain ──────────────────────────────────────────────────
        Line(dl, thigh, knee,  ColBone, 2f);
        Line(dl, knee,  ankle, ColBone, 2f);
        Line(dl, ankle, toe,   ColBone, 1.5f);

        // ── IK correction line (ankle → IK target) ──────────────────────────
        Line(dl, ankle, ikTarget, ColTarget, 1f);

        // ── Ground hit dots ─────────────────────────────────────────────────
        // Drawn before bone dots so bone dots appear on top.
        Dot(dl, heelGround, 6f, ColHeelGround);  // orange = heel
        Dot(dl, toeGround,  6f, ColToeGround);   // green  = toe

        // ── Bone dots ───────────────────────────────────────────────────────
        Dot(dl, thigh,    4f, ColBone);
        Dot(dl, knee,     4f, ColBone);
        Dot(dl, ankle,    5f, ColBone);
        Dot(dl, toe,      4f, ColBone);

        // ── IK target ───────────────────────────────────────────────────────
        Dot(dl, ikTarget, 7f, ColTarget);

        // ── Labels (drawn last, always on top) ──────────────────────────────
        Label(dl, thigh,      $"Thigh{side}",   new Vector2( 6, -6));
        Label(dl, knee,       $"Knee{side}",    new Vector2( 6, -6));
        Label(dl, ankle,      $"Ankle{side}",   new Vector2( 6,  4));
        Label(dl, toe,        $"Toe{side}",     new Vector2( 6,  4));
        Label(dl, heelGround, $"Heel{side}",    new Vector2(-38, -14)); // offset left+up to avoid ankle dot
        Label(dl, toeGround,  $"ToeGnd{side}",  new Vector2( 6, -14));
        Label(dl, ikTarget,   $"IK{side}",      new Vector2( 8,  0));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private bool ToScreen(Vector3 world, out Vector2 screen) =>
        _gameGui.WorldToScreen(world, out screen);

    private void Line(ImDrawListPtr dl, Vector3 a, Vector3 b, uint col, float thickness)
    {
        if (ToScreen(a, out var sa) && ToScreen(b, out var sb))
            dl.AddLine(sa, sb, col, thickness);
    }

    private void Dot(ImDrawListPtr dl, Vector3 pos, float radius, uint col)
    {
        if (ToScreen(pos, out var sp))
            dl.AddCircleFilled(sp, radius, col);
    }

    private void Label(ImDrawListPtr dl, Vector3 pos, string text, Vector2 offset)
    {
        if (ToScreen(pos, out var sp))
            dl.AddText(sp + offset, ColLabel, text);
    }
}
