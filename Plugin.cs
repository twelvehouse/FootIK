using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FootIK.Services;
using FootIK.UI;
using System.Numerics;

namespace FootIK;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/footik";

    // ── Dalamud services ─────────────────────────────────────────────────────
    public IDalamudPluginInterface Interface { get; init; }
    public IPluginLog Log { get; init; }
    public IObjectTable ObjectTable { get; init; }
    public IFramework Framework { get; init; }
    public ICommandManager CommandManager { get; init; }
    public ISigScanner SigScanner { get; init; }
    public IGameInteropProvider GameInterop { get; init; }
    public IGameGui GameGui { get; init; }
    public IDataManager DataManager { get; init; }

    // ── Plugin state ─────────────────────────────────────────────────────────
    public Config Config { get; init; }

    public GroundDetectionService GroundDetection { get; }
    public HavokIKService HavokIK { get; }
    public SkeletonAccessService SkeletonAccess { get; }
    public FootIKService FootIK { get; }

    public WindowSystem WindowSystem { get; } = new("FootIK");
    public ConfigWindow ConfigWindow { get; }
    public DebugOverlay DebugOverlay { get; }

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IPluginLog log,
        IObjectTable objectTable,
        IFramework framework,
        ICommandManager commandManager,
        ISigScanner sigScanner,
        IGameInteropProvider gameInterop,
        IGameGui gameGui,
        IDataManager dataManager)
    {
        Interface      = pluginInterface;
        Log            = log;
        ObjectTable    = objectTable;
        Framework      = framework;
        CommandManager = commandManager;
        SigScanner     = sigScanner;
        GameInterop    = gameInterop;
        GameGui        = gameGui;
        DataManager    = dataManager;

        Config = Interface.GetPluginConfig() as Config ?? new Config();

        GroundDetection = new GroundDetectionService(Log);
        HavokIK         = new HavokIKService(SigScanner, Log);
        SkeletonAccess  = new SkeletonAccessService(SigScanner, GameInterop, Log);
        FootIK          = new FootIKService(Config, ObjectTable, Framework,
                              GroundDetection, HavokIK, SkeletonAccess, DataManager, Log);

        ConfigWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(ConfigWindow);
        DebugOverlay = new DebugOverlay(GameGui);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open FootIK settings window.",
        });

        Interface.UiBuilder.Draw         += WindowSystem.Draw;
        Interface.UiBuilder.Draw         += DrawDebugOverlay;
        Interface.UiBuilder.OpenConfigUi += OnOpenConfigUi;
        Interface.UiBuilder.OpenMainUi   += OnOpenConfigUi;

        Log.Information("[FootIK] Plugin v1.2.5 loaded.");
    }

    public void SaveConfig() => Interface.SavePluginConfig(Config);

    private unsafe void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("bones", StringComparison.OrdinalIgnoreCase))
        {
            var player = ObjectTable.LocalPlayer;
            if (player == null) { Log.Information("[FootIK] No local player."); return; }
            var charaBase = Services.SkeletonAccessService.GetCharaBase(player);
            if (charaBase == null) { Log.Information("[FootIK] No CharacterBase."); return; }
            var skel = charaBase->Skeleton;
            if (skel == null || skel->PartialSkeletonCount == 0) { Log.Information("[FootIK] No skeleton."); return; }

            Log.Information($"[FootIK] Total partial skeletons: {skel->PartialSkeletonCount}");
            for (int pi = 0; pi < skel->PartialSkeletonCount; pi++)
            {
                var ps   = &skel->PartialSkeletons[pi];
                var pose = Services.SkeletonAccessService.GetPose(ps);
                Log.Information($"[FootIK] === Partial skeleton {pi} ===");
                SkeletonAccess.DumpBoneNames(pose, Log);
            }
            return;
        }

        ConfigWindow.IsOpen = !ConfigWindow.IsOpen;
    }

    private void DrawDebugOverlay() => DebugOverlay.Draw(FootIK.LastDebug);

    private void OnOpenConfigUi()
    {
        ConfigWindow.IsOpen = true;
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(CommandName);

        Interface.UiBuilder.Draw         -= WindowSystem.Draw;
        Interface.UiBuilder.Draw         -= DrawDebugOverlay;
        Interface.UiBuilder.OpenConfigUi -= OnOpenConfigUi;
        Interface.UiBuilder.OpenMainUi   -= OnOpenConfigUi;

        WindowSystem.RemoveAllWindows();

        FootIK.Dispose();
        SkeletonAccess.Dispose();
        HavokIK.Dispose();

        SaveConfig();
    }
}
