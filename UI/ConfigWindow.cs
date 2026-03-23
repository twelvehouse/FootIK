using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace FootIK.UI;

public sealed class ConfigWindow : Window
{
    private readonly Plugin _plugin;
    private Config Cfg => _plugin.Config;

    private static readonly Vector4 ColorMuted   = new(0.6f, 0.6f, 0.6f, 1f);
    private static readonly Vector4 ColorAllow   = new(0.4f, 0.9f, 0.4f, 1f);
    private static readonly Vector4 ColorDeny    = new(0.9f, 0.4f, 0.4f, 1f);
    private static readonly Vector4 ColorBase    = new(0.7f, 0.7f, 0.7f, 0.7f);
    private static readonly Vector4 ColorWarning = new(1.0f, 0.75f, 0.2f, 1f);

    private string _newExtraAllow = string.Empty;
    private string _newExtraDeny  = string.Empty;

    private static readonly string[] BaseAllow = [@"^emote/", @"^normal/"];
    private static readonly string[] BaseDeny  =
    [
        @"^emote/s_",
        @"^emote/l_",
        @"^emote/j_",
        @"^emote/sit",
        @"^emote/jmn",
        @"^emote/dance",
    ];

    public ConfigWindow(Plugin plugin)
        : base("FootIK — Settings###footik-settings")
    {
        _plugin = plugin;

        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(440, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        using var tabs = ImRaii.TabBar("##footik_tabs");
        if (!tabs) return;

        DrawGeneralTab();
        DrawExperimentalTab();
    }

    // ── General tab ───────────────────────────────────────────────────────────

    private void DrawGeneralTab()
    {
        using var tab = ImRaii.TabItem("General");
        if (!tab) return;

        ImGui.Spacing();

        if (CheckboxConfig("Enable FootIK", Cfg.Enabled, out var enabled))
            Cfg.Enabled = enabled;

        using (ImRaii.Disabled(!Cfg.Enabled))
        {
            ImGui.Spacing();

            if (CheckboxConfig("Apply to all characters", Cfg.ApplyToAll, out var all))
                Cfg.ApplyToAll = all;
            ImGui.SameLine();
            ImGui.TextColored(ColorMuted, "(?)");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Apply foot IK to all nearby characters, not just the local player.");

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            if (SliderConfig("Max step (m)", Cfg.MaxStep, 0.05f, 1.5f, out var ms, "%.2f"))
                Cfg.MaxStep = ms;
            HelpMarker(
                "Maximum height difference the IK will try to compensate.\n" +
                "Set this to roughly the height of the tallest step or kerb\n" +
                "you want your character's feet to adapt to.");

            if (CheckboxConfig("Single foot only", Cfg.SingleFootOnly, out var sfo))
                Cfg.SingleFootOnly = sfo;
            ImGui.SameLine();
            ImGui.TextColored(ColorMuted, "(?)");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Apply IK only to the foot that needs the larger correction.\n" +
                    "The other foot's animation pose is left unchanged.\n" +
                    "Helps preserve emote poses on the less-corrected foot.");

            if (SliderConfig("Both-feet cancel (m)", Cfg.BothFeetCancelThreshold, 0f, 0.5f, out var bfc, "%.3f"))
                Cfg.BothFeetCancelThreshold = bfc;
            HelpMarker(
                "If both feet need correction larger than this value in the same\n" +
                "direction, IK is cancelled for both feet. This prevents the legs\n" +
                "from stretching unnaturally during emotes where the whole body\n" +
                "is elevated. Set to 0 to disable.");

            if (CheckboxConfig("Skirt correction", Cfg.SkirtCorrectionEnabled, out var sc))
                Cfg.SkirtCorrectionEnabled = sc;
            ImGui.SameLine();
            ImGui.TextColored(ColorMuted, "(?)");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Apply a fraction of the thigh IK rotation to front skirt/equipment bones\n" +
                    "(j_sk_f_a/b/c) to reduce mesh clipping when the leg is lifted.\n" +
                    "Fine-tune multipliers in the Experimental tab.");

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            if (CheckboxConfig("Show debug overlay", Cfg.ShowDebugOverlay, out var dbg))
                Cfg.ShowDebugOverlay = dbg;
            ImGui.SameLine();
            ImGui.TextColored(ColorMuted, "(?)");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Draw bone positions, raycasts, ground hits, and IK targets\n" +
                    "directly in the game world. Useful for diagnosing issues.");
        }
    }

    // ── Experimental tab ──────────────────────────────────────────────────────

    private void DrawExperimentalTab()
    {
        using var tab = ImRaii.TabItem("Experimental");
        if (!tab) return;

        ImGui.Spacing();
        ImGui.TextColored(ColorWarning,
            "These settings are for fine-tuning and may cause visual artefacts.");
        ImGui.Spacing();

        using (ImRaii.Disabled(!Cfg.Enabled))
        {
            SectionHeader("Blend");

            if (SliderConfig("Master weight", Cfg.Weight, 0f, 1f, out var w))
                Cfg.Weight = w;
            HelpMarker("Global IK blend weight. 1 = full IK, 0 = animation unchanged.");

            if (SliderConfig("Foot position weight", Cfg.FootPositionWeight, 0f, 1f, out var fpw))
                Cfg.FootPositionWeight = fpw;
            HelpMarker("How strongly the foot is moved to the IK target position.");

            if (SliderConfig("Foot rotation weight", Cfg.FootRotationWeight, 0f, 1f, out var frw))
                Cfg.FootRotationWeight = frw;
            HelpMarker(
                "How much of the original animation's ankle rotation is preserved\n" +
                "after the IK solve. 1 = fully restore, 0 = keep the IK solver's result.");

            SectionHeader("Smoothing");

            if (SliderConfig("Feet position speed", Cfg.FeetPositionSpeed, 0.1f, 10f, out var fps))
                Cfg.FeetPositionSpeed = fps;
            HelpMarker("How quickly the foot correction tracks the target height.");

            if (SliderConfig("Falloff increase speed", Cfg.IkFalloffIncreaseSpeed, 0.1f, 20f, out var fis))
                Cfg.IkFalloffIncreaseSpeed = fis;
            HelpMarker("How fast IK blends in when the character becomes grounded.");

            if (SliderConfig("Falloff decrease speed", Cfg.IkFalloffDecreaseSpeed, 0.1f, 20f, out var fds))
                Cfg.IkFalloffDecreaseSpeed = fds;
            HelpMarker("How fast IK blends out when the character leaves the ground.");

            ImGui.Spacing();
            if (ImGui.Button("Reset to defaults"))
            {
                Cfg.Weight                 = 1f;
                Cfg.FootPositionWeight     = 1f;
                Cfg.FootRotationWeight     = 1f;
                Cfg.FeetPositionSpeed      = 2f;
                Cfg.IkFalloffIncreaseSpeed = 5f;
                Cfg.IkFalloffDecreaseSpeed = 10f;
                _plugin.SaveConfig();
            }
            ImGui.SameLine();
            ImGui.TextColored(ColorMuted, "(does not affect Animation Filter)");

            SectionHeader("Skirt Correction");

            using (ImRaii.Disabled(!Cfg.SkirtCorrectionEnabled))
            {
                if (SliderConfig("Root bone mult  (j_sk_f_a)", Cfg.SkirtMult0, 0f, 2f, out var sm0))
                    Cfg.SkirtMult0 = sm0;
                if (SliderConfig("Mid bone mult   (j_sk_f_b)", Cfg.SkirtMult1, 0f, 2f, out var sm1))
                    Cfg.SkirtMult1 = sm1;
                if (SliderConfig("Tip bone mult   (j_sk_f_c)", Cfg.SkirtMult2, 0f, 2f, out var sm2))
                    Cfg.SkirtMult2 = sm2;
            }

            SectionHeader("Animation Filter");

            ImGui.TextColored(ColorMuted,
                "IK applies only when the base animation matches a base allow rule\n" +
                "and no deny rule. Use exceptions below to override.");
            ImGui.Spacing();

            DrawReadOnlyPatterns("Base Allow", BaseAllow, ColorAllow);
            ImGui.Spacing();
            DrawReadOnlyPatterns("Base Deny",  BaseDeny,  ColorDeny);
            ImGui.Spacing();

            DrawEditablePatterns("Extra Allow", Cfg.ExtraAllowPatterns, ColorAllow, ref _newExtraAllow,
                "Force IK on — overrides base deny rules.");
            ImGui.Spacing();
            DrawEditablePatterns("Extra Deny",  Cfg.ExtraDenyPatterns,  ColorDeny,  ref _newExtraDeny,
                "Force IK off — overrides everything.");

        }
    }

    // ── Pattern list helpers ──────────────────────────────────────────────────

    private static void DrawReadOnlyPatterns(string label, string[] patterns, Vector4 color)
    {
        ImGui.TextColored(color, label);
        ImGui.SameLine();
        ImGui.TextColored(ColorMuted, "(built-in)");
        foreach (var p in patterns)
            ImGui.TextColored(color, $"  {p}");
    }

    private void DrawEditablePatterns(string label, List<string> patterns, Vector4 color,
        ref string newPattern, string tooltip)
    {
        ImGui.TextColored(color, label);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);

        int? removeIdx = null;
        if (patterns.Count == 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(ColorMuted, "(empty)");
        }
        else
        {
            for (int i = 0; i < patterns.Count; i++)
            {
                ImGui.PushID($"{label}_{i}");
                ImGui.TextColored(color, "  ");
                ImGui.SameLine();
                ImGui.TextUnformatted(patterns[i]);
                ImGui.SameLine();
                if (ImGui.SmallButton("x")) removeIdx = i;
                ImGui.PopID();
            }
        }

        if (removeIdx.HasValue)
        {
            patterns.RemoveAt(removeIdx.Value);
            _plugin.SaveConfig();
        }

        ImGui.SetNextItemWidth(220);
        ImGui.InputText($"##{label}_new", ref newPattern, 256);
        ImGui.SameLine();
        if (ImGui.Button($"Add##{label}") && !string.IsNullOrWhiteSpace(newPattern))
        {
            patterns.Add(newPattern.Trim());
            newPattern = string.Empty;
            _plugin.SaveConfig();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private bool CheckboxConfig(string label, bool current, out bool result)
    {
        result = current;
        bool changed = ImGui.Checkbox(label, ref result);
        if (changed) _plugin.SaveConfig();
        return changed;
    }

    private bool SliderConfig(string label, float current, float min, float max,
        out float result, string fmt = "%.2f")
    {
        result = current;
        ImGui.SetNextItemWidth(200);
        bool changed = ImGui.SliderFloat(label, ref result, min, max, fmt);
        if (changed) _plugin.SaveConfig();
        return changed;
    }

    private static void SectionHeader(string text)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1f), text);
        ImGui.Separator();
        ImGui.Spacing();
    }

    private static void HelpMarker(string desc)
    {
        ImGui.SameLine();
        ImGui.TextColored(ColorMuted, "(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(desc);
    }
}
