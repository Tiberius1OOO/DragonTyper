using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace DragonTyper.Windows;

internal static class Theme
{
    public static readonly Vector4 Bg = new(0.06f, 0.07f, 0.10f, 0.96f);
    public static readonly Vector4 BgChild = new(0.09f, 0.10f, 0.14f, 0.90f);
    public static readonly Vector4 Border = new(0.78f, 0.62f, 0.28f, 0.35f);
    public static readonly Vector4 Gold = new(0.90f, 0.74f, 0.35f, 1f);
    public static readonly Vector4 GoldDim = new(0.90f, 0.74f, 0.35f, 0.55f);
    public static readonly Vector4 Cyan = new(0.25f, 0.85f, 0.95f, 1f);
    public static readonly Vector4 CyanSoft = new(0.25f, 0.85f, 0.95f, 0.18f);
    public static readonly Vector4 Blue = new(0.18f, 0.42f, 0.85f, 1f);
    public static readonly Vector4 Text = new(0.92f, 0.94f, 0.98f, 1f);
    public static readonly Vector4 TextMuted = new(0.62f, 0.68f, 0.78f, 1f);
    public static readonly Vector4 ActiveOn = new(0.18f, 0.72f, 0.55f, 1f);
    public static readonly Vector4 ActiveOff = new(0.55f, 0.28f, 0.32f, 1f);
    public static readonly Vector4 Frame = new(0.12f, 0.14f, 0.20f, 1f);
    public static readonly Vector4 FrameHover = new(0.16f, 0.20f, 0.28f, 1f);
    public static readonly Vector4 FrameActive = new(0.20f, 0.28f, 0.40f, 1f);

    public static void Push()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 14f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 12f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 10f);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 10f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 10f);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 10f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(18, 16));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(12, 8));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(12, 10));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, new Vector2(8, 6));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.2f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);

        ImGui.PushStyleColor(ImGuiCol.WindowBg, Bg);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, BgChild);
        ImGui.PushStyleColor(ImGuiCol.Border, Border);
        ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, TextMuted);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Frame);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, FrameHover);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, FrameActive);
        ImGui.PushStyleColor(ImGuiCol.CheckMark, Cyan);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, Gold);
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, Cyan);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.14f, 0.18f, 0.26f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.20f, 0.32f, 0.48f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.22f, 0.45f, 0.70f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Header, CyanSoft);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.25f, 0.85f, 0.95f, 0.28f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(0.25f, 0.85f, 0.95f, 0.40f));
        ImGui.PushStyleColor(ImGuiCol.Separator, GoldDim);
        ImGui.PushStyleColor(ImGuiCol.TitleBg, Bg);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, new Vector4(0.08f, 0.10f, 0.16f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgCollapsed, Bg);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, new Vector4(0.05f, 0.06f, 0.08f, 0.6f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, GoldDim);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, Gold);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, Cyan);
    }

    public const int StyleVarCount = 13;
    public const int StyleColorCount = 25;

    public static void Pop()
    {
        ImGui.PopStyleColor(StyleColorCount);
        ImGui.PopStyleVar(StyleVarCount);
    }

    public static float Pulse(float speed = 2.2f, float min = 0.35f, float max = 1f)
    {
        var t = (float)ImGui.GetTime() * speed;
        var wave = (MathF.Sin(t) + 1f) * 0.5f;
        return min + (max - min) * wave;
    }
}
