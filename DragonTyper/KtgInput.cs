using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Game.NativeWrapper;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AtkValueType = Dalamud.Game.NativeWrapper.AtkValueType;

namespace DragonTyper;

/// <summary>Reads the Keybound Brawler input addon (KTGInput) and submits words.</summary>
internal static unsafe class KtgInput
{
    public const string AddonName = "KTGInput";

    public readonly record struct WordEntry(ulong EntityId, string Text);

    public static AtkUnitBasePtr GetAddon()
    {
        var addon = Plugin.GameGui.GetAddonByName(AddonName);
        if (addon.IsNull || !addon.IsReady || !addon.IsVisible)
            return default;
        return addon;
    }

    /// <summary>
    /// Whether the text box currently accepts input.
    /// Letterlock sets this false. Index 2 is the addon's third AtkValue.
    /// </summary>
    public static bool TryGetInputEnabled(AtkUnitBasePtr addon, out bool enabled)
    {
        enabled = false;
        if (addon.IsNull || addon.AtkValuesCount < 3)
            return false;

        var value = GetValueAt(addon, 2);
        if (value.IsNull)
            return false;

        enabled = value.ValueType switch
        {
            AtkValueType.Bool => value.TryGet<bool>(out var b) && b,
            AtkValueType.Int => value.TryGet<int>(out var i) && i != 0,
            AtkValueType.UInt => value.TryGet<uint>(out var u) && u != 0,
            _ => AsBool(value.GetValue()),
        };
        return true;
    }

    /// <summary>
    /// Collects visible words from AtkValues.
    /// Pattern after the header: entity id + text (+ optional reading string).
    /// </summary>
    public static List<WordEntry> ReadWords(AtkUnitBasePtr addon)
    {
        var words = new List<WordEntry>();
        if (addon.IsNull)
            return words;

        var count = addon.AtkValuesCount;
        for (var i = 3; i < count; i++)
        {
            var idValue = GetValueAt(addon, i);
            if (!TryReadEntityId(idValue, out var entityId) || entityId == 0)
                continue;

            if (i + 1 >= count)
                break;

            var text = ReadString(GetValueAt(addon, i + 1));
            string? reading = null;
            var consumed = 2;

            if (i + 2 < count)
            {
                var maybeReading = GetValueAt(addon, i + 2);
                if (IsStringType(maybeReading.ValueType))
                {
                    reading = ReadString(maybeReading);
                    consumed = 3;
                }
            }

            var chosen = !string.IsNullOrEmpty(text) ? text : reading;
            if (!string.IsNullOrEmpty(chosen))
                words.Add(new WordEntry(entityId, chosen!));

            i += consumed - 1;
        }

        return words;
    }

    public static bool SubmitWord(AtkUnitBasePtr addon, string word)
    {
        if (addon.IsNull || string.IsNullOrEmpty(word))
            return false;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null)
            return false;

        var values = stackalloc AtkValue[2];
        values[0] = default;
        values[1] = default;
        try
        {
            values[0].SetManagedString("SubmitWord");
            values[1].SetManagedString(word);
            unit->FireCallback(2, values, true);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "SubmitWord FireCallback failed for '{Word}'", word);
            return false;
        }
        finally
        {
            values[0].ReleaseManagedMemory();
            values[1].ReleaseManagedMemory();
        }
    }

    public static string Dump(AtkUnitBasePtr addon)
    {
        if (addon.IsNull)
            return "KTGInput not open.";

        var sb = new StringBuilder();
        sb.AppendLine($"KTGInput AtkValuesCount={addon.AtkValuesCount} visible={addon.IsVisible} ready={addon.IsReady}");

        var i = 0;
        foreach (var value in addon.AtkValues)
        {
            sb.Append($"[{i}] type={value.ValueType} ");
            try
            {
                var raw = value.GetValue();
                sb.Append(raw switch
                {
                    string s => $"\"{s}\"",
                    null => "(null)",
                    _ => raw.ToString(),
                });
            }
            catch
            {
                sb.Append("(read error)");
            }

            sb.AppendLine();
            i++;
        }

        return sb.ToString();
    }

    private static AtkValuePtr GetValueAt(AtkUnitBasePtr addon, int index)
    {
        var i = 0;
        foreach (var value in addon.AtkValues)
        {
            if (i == index)
                return value;
            i++;
        }

        return default;
    }

    private static bool TryReadEntityId(AtkValuePtr value, out ulong entityId)
    {
        entityId = 0;
        if (value.IsNull)
            return false;

        if (value.TryGet<uint>(out var u))
        {
            entityId = u;
            return true;
        }

        if (value.TryGet<int>(out var i) && i > 0)
        {
            entityId = (ulong)i;
            return true;
        }

        if (value.TryGet<ulong>(out var ul))
        {
            entityId = ul;
            return true;
        }

        return false;
    }

    private static bool IsStringType(AtkValueType type) =>
        type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.String8
            or AtkValueType.WideString or AtkValueType.ConstString;

    private static string? ReadString(AtkValuePtr value)
    {
        if (value.IsNull || !IsStringType(value.ValueType))
            return null;

        try
        {
            return value.GetValue() as string;
        }
        catch
        {
            return null;
        }
    }

    private static bool AsBool(object? value) =>
        value switch
        {
            bool b => b,
            int i => i != 0,
            uint u => u != 0,
            string s => s.Equals("true", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
}
