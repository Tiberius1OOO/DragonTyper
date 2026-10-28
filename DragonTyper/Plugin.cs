using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DragonTyper.Windows;

namespace DragonTyper;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/dragontyper";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    public Configuration Configuration { get; }
    public TyperEngine Typer { get; }

    private readonly WindowSystem windowSystem = new("DragonTyper");
    private readonly ConfigWindow configWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Typer = new TyperEngine(this);
        configWindow = new ConfigWindow(this);
        windowSystem.AddWindow(configWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open DragonTyper. /dragontyper on|off|toggle|dump",
        });

        Framework.Update += OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfig;
        PluginInterface.UiBuilder.OpenMainUi += ToggleConfig;

        Log.Information("DragonTyper loaded. Use /dragontyper — Active starts off.");
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfig;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleConfig;
        windowSystem.RemoveAllWindows();
        configWindow.Dispose();
        CommandManager.RemoveHandler(CommandName);
    }

    public void ToggleConfig() => configWindow.Toggle();

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!ClientState.IsLoggedIn)
        {
            Typer.ResetIdle();
            return;
        }

        Typer.Tick();
    }

    private void OnCommand(string command, string args)
    {
        var arg = (args ?? string.Empty).Trim().ToLowerInvariant();
        switch (arg)
        {
            case "on":
                SetActive(true);
                break;
            case "off":
                SetActive(false);
                break;
            case "toggle":
                SetActive(!Configuration.Active);
                break;
            case "dump":
                Typer.DumpAddon();
                ChatGui.Print("[DragonTyper] Addon dump written to /xllog.");
                break;
            default:
                ToggleConfig();
                break;
        }
    }

    private void SetActive(bool active)
    {
        Configuration.Active = active;
        Configuration.Save();
        Typer.ResetIdle();
        ChatGui.Print(active
            ? "[DragonTyper] Active — will type when the Keybound Brawler input is open."
            : "[DragonTyper] Inactive — passive.");
    }
}
