using System;
using Dalamud.Game.Addon.Lifecycle;
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
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;

    public Configuration Configuration { get; }
    public TyperEngine Typer { get; }
    internal LiveCapture Capture { get; }
    internal static WordFeed Words { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("DragonTyper");
    private readonly ConfigWindow configWindow;
    private bool wasLoggedIn;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Typer = new TyperEngine(this);
        Capture = new LiveCapture();
        Words = new WordFeed();
        configWindow = new ConfigWindow(this);
        windowSystem.AddWindow(configWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open DragonTyper. /dragontyper start|stop|toggle|dump|capture",
        });

        Framework.Update += OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfig;
        PluginInterface.UiBuilder.OpenMainUi += ToggleConfig;
        Words.Enable(AddonLifecycle);

        var version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown";
        Log.Information(
            "DragonTyper {Version} loaded (solo, crash-safe word reader). Press Start once to auto-type the whole minigame.",
            version);
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfig;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleConfig;
        try
        {
            Words.Disable(AddonLifecycle);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "WordFeed disable failed");
        }

        Words.Dispose();
        windowSystem.RemoveAllWindows();
        configWindow.Dispose();
        CommandManager.RemoveHandler(CommandName);
    }

    public void ToggleConfig() => configWindow.Toggle();

    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            var loggedIn = ClientState.IsLoggedIn;
            if (!loggedIn)
            {
                if (wasLoggedIn)
                {
                    Log.Information("Logged out. Typer paused.");
                    Typer.ResetIdle();
                }

                wasLoggedIn = false;
                return;
            }

            wasLoggedIn = true;
            try
            {
                Capture.Tick();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Live capture tick failed");
            }

            Typer.Tick();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Framework update failed");
        }
    }

    private void OnCommand(string command, string args)
    {
        var arg = (args ?? string.Empty).Trim().ToLowerInvariant();
        switch (arg)
        {
            case "on":
            case "start":
                SetRunning(true);
                break;
            case "off":
            case "stop":
                SetRunning(false);
                break;
            case "toggle":
                SetRunning(!Configuration.Active);
                break;
            case "dump":
                Typer.DumpAddon();
                ChatGui.Print("[DragonTyper] Dump written to /xllog.");
                break;
            case "capture":
            case "probe":
            case "record":
                ChatGui.Print("[DragonTyper] " + Capture.Start());
                break;
            default:
                ToggleConfig();
                break;
        }
    }

    private void SetRunning(bool running)
    {
        Configuration.Active = running;
        Configuration.Save();
        Typer.ResetIdle();
        ChatGui.Print(running
            ? "[DragonTyper] Started. Will type words for the whole minigame."
            : "[DragonTyper] Stopped.");
    }
}

