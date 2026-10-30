using System;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;

namespace DragonTyper.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private ISharedImmediateTexture? logoTexture;

    public ConfigWindow(Plugin plugin)
        : base("DragonTyper###DragonTyperConfig")
    {
        this.plugin = plugin;
        Size = new Vector2(440, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 420),
            MaximumSize = new Vector2(700, 900),
        };
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        RespectCloseHotkey = true;
    }

    public void Dispose() => logoTexture = null;

    /// <summary>
    /// Inside the duty, X only collapses to the floating logo — never fully gone.
    /// Outside the duty, normal close.
    /// </summary>
    public override void OnClose()
    {
        if (plugin.IsInMinigame)
            plugin.CollapseConfigToFloat();
    }

    public override void PreDraw()
    {
        Theme.Push();
        EnsureLogo();
    }

    public override void PostDraw() => Theme.Pop();

    public override void Draw()
    {
        var cfg = plugin.Configuration;
        var typer = plugin.Typer;

        DrawHeader();
        ImGui.Spacing();
        DrawStartStop(cfg, typer);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawTiming(cfg);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawStatus(typer);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(Theme.TextMuted, "/dragontyper start|stop|toggle");
        if (plugin.IsInMinigame)
        {
            ImGui.TextColored(Theme.TextMuted, "X collapses to a floating logo — always reachable in this duty.");
        }
    }

    private void DrawHeader()
    {
        var wrap = logoTexture?.GetWrapOrEmpty();
        const float logoSize = 72f;

        if (wrap != null && wrap.Handle != default)
        {
            ImGui.Image(wrap.Handle, new Vector2(logoSize, logoSize));
            ImGui.SameLine();
        }

        ImGui.BeginGroup();
        ImGui.Dummy(new Vector2(0, 10));
        ImGui.TextColored(Theme.Gold, "DragonTyper");
        ImGui.TextColored(Theme.Cyan, "AFK kick protection on while Start is running");
        ImGui.TextColored(Theme.TextMuted, "Press Start once — it types the whole minigame");
        ImGui.EndGroup();

        ImGui.Dummy(new Vector2(0, 4));
        var draw = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var cyan = ImGui.ColorConvertFloat4ToU32(Theme.Cyan);
        var fade = ImGui.ColorConvertFloat4ToU32(new Vector4(Theme.Gold.X, Theme.Gold.Y, Theme.Gold.Z, 0.05f));
        draw.AddRectFilledMultiColor(p, p + new Vector2(width * 0.62f, 2.5f), cyan, fade, fade, cyan);
        ImGui.Dummy(new Vector2(0, 8));
    }

    private void DrawStartStop(Configuration cfg, TyperEngine typer)
    {
        var running = cfg.Active;
        var pulse = running ? Theme.Pulse(2.6f, 0.55f, 1f) : 1f;
        var accent = running
            ? new Vector4(Theme.ActiveOn.X, Theme.ActiveOn.Y, Theme.ActiveOn.Z, pulse)
            : Theme.ActiveOff;

        ImGui.TextColored(accent, running ? "●  RUNNING" : "○  STOPPED");
        ImGui.TextColored(Theme.TextMuted, running
            ? "Auto-typing words until you press Stop"
            : "Start when you are ready, then play the duty");

        ImGui.Spacing();
        var label = running ? "Stop" : "Start";
        var color = running ? Theme.ActiveOff : Theme.ActiveOn;
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(color.X, color.Y, color.Z, 0.82f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(color.X, color.Y, color.Z, 0.95f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, color);
        if (ImGui.Button(label, new Vector2(-1, 40)))
        {
            cfg.Active = !running;
            cfg.Save();
            typer.ResetIdle();
            Plugin.ChatGui.Print(cfg.Active
                ? "[DragonTyper] Started. Will type words for the whole minigame."
                : "[DragonTyper] Stopped.");
        }

        ImGui.PopStyleColor(3);
    }

    private void DrawTiming(Configuration cfg)
    {
        ImGui.TextColored(Theme.Cyan, "Timing");
        ImGui.TextColored(Theme.TextMuted, "Each wait is randomized by ±20%");

        var delay = cfg.WordDelayMs;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderInt("##delay", ref delay, 250, 5000, "%d ms"))
        {
            cfg.WordDelayMs = delay;
            cfg.Save();
        }

        var lo = (int)(delay * 0.8f);
        var hi = (int)(delay * 1.2f);
        ImGui.TextColored(Theme.GoldDim, $"Effective range  {lo} - {hi} ms");

        var longDelay = cfg.LongSentenceDelay;
        if (ImGui.Checkbox("Longer wait for long sentences", ref longDelay))
        {
            cfg.LongSentenceDelay = longDelay;
            cfg.Save();
        }

        ImGui.TextColored(Theme.TextMuted, "28+ characters wait up to 2x (max 5s)");
    }

    private void DrawStatus(TyperEngine typer)
    {
        ImGui.TextColored(Theme.Gold, "Status");
        ImGui.TextWrapped(typer.Status);

        if (!string.IsNullOrEmpty(typer.LastError))
        {
            ImGui.Spacing();
            ImGui.TextColored(Theme.ActiveOff, "Last error");
            ImGui.TextWrapped(typer.LastError);
            ImGui.TextColored(Theme.TextMuted, "Details: /xllog  filter DragonTyper");
        }

        ImGui.Spacing();
        ImGui.TextColored(Theme.Cyan, $"Hits  {typer.Hits}");
        ImGui.SameLine(0, 24);
        ImGui.TextColored(Theme.TextMuted, $"Misses  {typer.Misses}");
    }

    private void EnsureLogo()
    {
        if (logoTexture != null)
            return;

        try
        {
            var dir = Plugin.PluginInterface.AssemblyLocation.DirectoryName;
            if (string.IsNullOrEmpty(dir))
                return;

            var path = Path.Combine(dir, "images", "logo.png");
            if (!File.Exists(path))
                path = Path.Combine(dir, "images", "icon.png");
            if (!File.Exists(path))
                return;

            logoTexture = Plugin.TextureProvider.GetFromFile(path);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not load DragonTyper logo");
        }
    }
}
