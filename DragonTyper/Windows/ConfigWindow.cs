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
        Size = new Vector2(460, 440);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 390),
            MaximumSize = new Vector2(720, 900),
        };
        Flags = ImGuiWindowFlags.NoScrollbar;
        RespectCloseHotkey = true;
    }

    public void Dispose() => logoTexture = null;

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
        DrawActiveCard(cfg, typer);
        ImGui.Spacing();
        DrawDelayCard(cfg);
        ImGui.Spacing();
        DrawStatusCard(typer);
        ImGui.Spacing();
        DrawFooter(typer);
    }

    private void DrawHeader()
    {
        var wrap = logoTexture?.GetWrapOrEmpty();
        const float logoSize = 76f;

        if (wrap != null && wrap.Handle != default)
        {
            ImGui.Image(wrap.Handle, new Vector2(logoSize, logoSize));
            ImGui.SameLine();
        }

        ImGui.BeginGroup();
        ImGui.Dummy(new Vector2(0, 10));
        ImGui.TextColored(Theme.Gold, "DragonTyper");
        ImGui.TextColored(Theme.TextMuted, "Keybound Brawler auto typer");
        ImGui.EndGroup();

        ImGui.Dummy(new Vector2(0, 4));
        var draw = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var cyan = ImGui.ColorConvertFloat4ToU32(Theme.Cyan);
        var fade = ImGui.ColorConvertFloat4ToU32(new Vector4(Theme.Gold.X, Theme.Gold.Y, Theme.Gold.Z, 0.05f));
        draw.AddRectFilledMultiColor(p, p + new Vector2(width * 0.62f, 2.5f), cyan, fade, fade, cyan);
        ImGui.Dummy(new Vector2(0, 10));
    }

    private void DrawActiveCard(Configuration cfg, TyperEngine typer)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.08f, 0.10f, 0.15f, 0.95f));
        ImGui.BeginChild("##activeCard", new Vector2(0, 92), true);

        var active = cfg.Active;
        var pulse = active ? Theme.Pulse(2.6f, 0.55f, 1f) : 1f;
        var accent = active
            ? new Vector4(Theme.ActiveOn.X, Theme.ActiveOn.Y, Theme.ActiveOn.Z, pulse)
            : Theme.ActiveOff;

        ImGui.TextColored(accent, active ? "●  LIVE" : "○  IDLE");
        ImGui.SameLine();
        ImGui.TextColored(Theme.TextMuted, active
            ? "Typing when the Keybound Brawler input is open"
            : "Passive until you enable Active");

        ImGui.Spacing();
        var buttonLabel = active ? "Disable Active" : "Enable Active";
        var btnColor = active ? Theme.ActiveOff : Theme.ActiveOn;
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(btnColor.X, btnColor.Y, btnColor.Z, 0.78f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(btnColor.X, btnColor.Y, btnColor.Z, 0.95f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, btnColor);
        if (ImGui.Button(buttonLabel, new Vector2(-1, 36)))
        {
            cfg.Active = !active;
            cfg.Save();
            typer.ResetIdle();
        }

        ImGui.PopStyleColor(3);
        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    private void DrawDelayCard(Configuration cfg)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.08f, 0.10f, 0.15f, 0.95f));
        ImGui.BeginChild("##delayCard", new Vector2(0, 152), true);

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

        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    private void DrawStatusCard(TyperEngine typer)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.08f, 0.10f, 0.15f, 0.95f));
        ImGui.BeginChild("##statusCard", new Vector2(0, 82), true);

        ImGui.TextColored(Theme.Gold, "Status");
        ImGui.TextWrapped(typer.Status);

        ImGui.Spacing();
        ImGui.TextColored(Theme.Cyan, $"Hits  {typer.Hits}");
        ImGui.SameLine(0, 24);
        ImGui.TextColored(Theme.TextMuted, $"Misses  {typer.Misses}");

        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    private void DrawFooter(TyperEngine typer)
    {
        if (ImGui.Button("Dump KTGInput to log", new Vector2(190, 0)))
        {
            typer.DumpAddon();
            Plugin.ChatGui.Print("[DragonTyper] Dump written to /xllog.");
        }

        ImGui.SameLine();
        ImGui.TextColored(Theme.TextMuted, "/dragontyper   on | off | toggle | dump");
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
