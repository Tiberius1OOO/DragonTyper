using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace DragonTyper.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public ConfigWindow(Plugin plugin)
        : base("DragonTyper###DragonTyperConfig")
    {
        this.plugin = plugin;
        Size = new Vector2(420, 280);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.NoCollapse;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var cfg = plugin.Configuration;
        var typer = plugin.Typer;

        var active = cfg.Active;
        if (ImGui.Checkbox("Active", ref active))
        {
            cfg.Active = active;
            cfg.Save();
            typer.ResetIdle();
        }

        ImGui.SameLine();
        ImGui.TextDisabled(active
            ? "Types when the Keybound Brawler input is open"
            : "Passive — turn on to type");

        ImGui.Separator();

        var delay = cfg.WordDelayMs;
        if (ImGui.SliderInt("Word delay (ms)", ref delay, 250, 5000))
        {
            cfg.WordDelayMs = delay;
            cfg.Save();
        }

        ImGui.TextDisabled("Each wait is randomized by ±20%.");

        var longDelay = cfg.LongSentenceDelay;
        if (ImGui.Checkbox("Longer wait for long sentences", ref longDelay))
        {
            cfg.LongSentenceDelay = longDelay;
            cfg.Save();
        }

        ImGui.TextDisabled("Sentences ≥ 28 characters wait up to 2× (max 5s).");

        ImGui.Separator();
        ImGui.Text($"Status: {typer.Status}");
        ImGui.Text($"Hits: {typer.Hits}   Misses: {typer.Misses}");

        ImGui.Separator();
        if (ImGui.Button("Dump KTGInput to log"))
        {
            typer.DumpAddon();
            Plugin.ChatGui.Print("[DragonTyper] Dump written to /xllog.");
        }

        ImGui.SameLine();
        ImGui.TextDisabled("/dragontyper · /dragontyper on|off|toggle|dump");
    }
}
