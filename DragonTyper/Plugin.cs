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

    /// <summary>Keybound Brawler duty TerritoryType.</summary>
    private const ushort KeyboundTerritory = 1359;

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
    internal static WordFeed Words { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("DragonTyper");
    private readonly ConfigWindow configWindow;
    private readonly FloatLauncher floatLauncher;
    private bool wasLoggedIn;
    private bool wasInMinigame;

    public bool IsInMinigame =>
        ClientState.IsLoggedIn
        && (ClientState.TerritoryType == KeyboundTerritory || !KtgInput.GetAddon().IsNull);

    public bool IsConfigOpen => configWindow.IsOpen;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Typer = new TyperEngine(this);
        Words = new WordFeed();
        configWindow = new ConfigWindow(this);
        floatLauncher = new FloatLauncher(this);
        windowSystem.AddWindow(configWindow);
        windowSystem.AddWindow(floatLauncher);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open DragonTyper. /dragontyper start|stop|toggle",
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
        floatLauncher.Dispose();
        CommandManager.RemoveHandler(CommandName);
    }

    public void ToggleConfig()
    {
        if (configWindow.IsOpen)
        {
            configWindow.IsOpen = false;
            if (IsInMinigame)
                CollapseConfigToFloat();
            return;
        }

        ExpandConfigFromFloat();
    }

    public void ExpandConfigFromFloat()
    {
        floatLauncher.IsOpen = false;
        configWindow.IsOpen = true;
    }

    public void CollapseConfigToFloat()
    {
        configWindow.IsOpen = false;
        if (IsInMinigame)
            floatLauncher.IsOpen = true;
    }

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
                    wasInMinigame = false;
                    floatLauncher.IsOpen = false;
                }

                wasLoggedIn = false;
                return;
            }

            wasLoggedIn = true;
            UpdateMinigameUi();
            Typer.Tick();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Framework update failed");
        }
    }

    /// <summary>
    /// Enter duty → full panel open.
    /// Stay in duty → always either panel or undismissable float logo.
    /// Leave duty → hide float; panel can close normally.
    /// </summary>
    private void UpdateMinigameUi()
    {
        var inMinigame = IsInMinigame;

        if (inMinigame && !wasInMinigame)
        {
            ExpandConfigFromFloat();
            Log.Information("Entered Keybound Brawler — DragonTyper UI forced open.");
        }
        else if (inMinigame)
        {
            // Never allow both closed while menus are locked.
            if (!configWindow.IsOpen && !floatLauncher.IsOpen)
                floatLauncher.IsOpen = true;
        }
        else if (wasInMinigame)
        {
            floatLauncher.IsOpen = false;
        }

        wasInMinigame = inMinigame;
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
