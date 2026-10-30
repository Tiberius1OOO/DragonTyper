using System;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;

namespace DragonTyper.Windows;

/// <summary>
/// Undismissable logo bubble while inside Keybound Brawler.
/// Click reopens the full panel; X / Escape cannot remove it in-zone.
/// </summary>
public sealed class FloatLauncher : Window, IDisposable
{
    private readonly Plugin plugin;
    private ISharedImmediateTexture? logoTexture;

    public FloatLauncher(Plugin plugin)
        : base("###DragonTyperFloat")
    {
        this.plugin = plugin;
        Size = new Vector2(72, 72);
        SizeCondition = ImGuiCond.Always;
        PositionCondition = ImGuiCond.FirstUseEver;
        Position = new Vector2(40, 220);
        Flags = ImGuiWindowFlags.NoTitleBar
                | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.NoScrollbar
                | ImGuiWindowFlags.NoScrollWithMouse
                | ImGuiWindowFlags.NoCollapse
                | ImGuiWindowFlags.AlwaysAutoResize
                | ImGuiWindowFlags.NoBackground;
        RespectCloseHotkey = false;
        ShowCloseButton = false;
        AllowClickthrough = false;
        IsOpen = false;
    }

    public void Dispose() => logoTexture = null;

    public override void PreDraw()
    {
        // Keep pinned open while in the duty — nowhere else to reopen menus.
        if (plugin.IsInMinigame && !plugin.IsConfigOpen)
            IsOpen = true;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6, 6));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 18f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.06f, 0.07f, 0.10f, 0.55f));
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(1);
        ImGui.PopStyleVar(3);
    }

    public override void Draw()
    {
        EnsureLogo();

        var running = plugin.Configuration.Active;
        var pulse = running ? Theme.Pulse(2.4f, 0.45f, 1f) : 0.7f;
        var ring = running
            ? new Vector4(Theme.ActiveOn.X, Theme.ActiveOn.Y, Theme.ActiveOn.Z, pulse)
            : new Vector4(Theme.Gold.X, Theme.Gold.Y, Theme.Gold.Z, 0.75f);

        const float size = 56f;
        var wrap = logoTexture?.GetWrapOrEmpty();
        var clicked = false;

        if (wrap != null && wrap.Handle != default)
        {
            var cursor = ImGui.GetCursorScreenPos();
            var draw = ImGui.GetWindowDrawList();
            var center = cursor + new Vector2(size * 0.5f + 2f, size * 0.5f + 2f);
            var ringCol = ImGui.ColorConvertFloat4ToU32(ring);
            draw.AddCircle(center, size * 0.52f, ringCol, 48, 2.4f);

            ImGui.SetCursorPos(ImGui.GetCursorPos() + new Vector2(2, 2));
            clicked = ImGui.ImageButton(wrap.Handle, new Vector2(size, size));
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(ring.X, ring.Y, ring.Z, 0.85f));
            clicked = ImGui.Button("DT", new Vector2(size, size));
            ImGui.PopStyleColor();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(running
                ? "DragonTyper — RUNNING\nClick to open panel"
                : "DragonTyper — click to open panel");

        if (clicked)
            plugin.ExpandConfigFromFloat();
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

            var path = Path.Combine(dir, "images", "icon.png");
            if (!File.Exists(path))
                path = Path.Combine(dir, "images", "logo.png");
            if (!File.Exists(path))
                return;

            logoTexture = Plugin.TextureProvider.GetFromFile(path);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not load float launcher logo");
        }
    }
}
