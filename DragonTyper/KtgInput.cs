using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Game.NativeWrapper;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AtkValueType = Dalamud.Game.NativeWrapper.AtkValueType;
using CStringPointer = InteropGenerator.Runtime.CStringPointer;

namespace DragonTyper;

/// <summary>
/// Safe Keybound Brawler input reader.
/// IMPORTANT: Do NOT index AddonRequestedUpdate StringArrayData** as a 160-slot holder.
/// That caused AccessViolationException / game crash in 1.0.3.
/// </summary>
internal static unsafe class KtgInput
{
    public const string AddonName = "KTGInput";

    public readonly record struct WordEntry(ulong EntityId, string Text);

    private static readonly object Gate = new();
    private static List<WordEntry> cachedWords = [];
    private static long lastCacheAt;
    private static string lastSource = "none";

    public static IReadOnlyList<WordEntry> CachedWords
    {
        get
        {
            lock (Gate)
                return cachedWords.ToList();
        }
    }

    public static string LastSource
    {
        get
        {
            lock (Gate)
                return lastSource;
        }
    }

    public static AtkUnitBasePtr GetAddon()
    {
        var addon = Plugin.GameGui.GetAddonByName(AddonName);
        if (addon.IsNull || !addon.IsReady || !addon.IsVisible)
            return default;
        return addon;
    }

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

    public static List<WordEntry> ReadWords(AtkUnitBasePtr addon)
    {
        if (addon.IsNull)
            return [];

        // 1) Text nodes on KTGInput itself (safe, bounded walk).
        var fromInput = ReadTextNodesFromAddon(addon, maxNodes: 256);
        if (fromInput.Count > 0)
        {
            StoreCache(fromInput, "KTGInput text nodes");
            return fromInput;
        }

        // 2) Other loaded addons whose name starts with KTG (name lookup only, no full unit-list walk).
        foreach (var name in KnownKtgAddonNames)
        {
            if (name.Equals(AddonName, StringComparison.Ordinal))
                continue;
            var other = Plugin.GameGui.GetAddonByName(name);
            if (other.IsNull || !other.IsVisible || !other.IsReady)
                continue;
            var words = ReadTextNodesFromAddon(other, maxNodes: 256);
            if (words.Count > 0)
            {
                StoreCache(words, $"{name} text nodes");
                return words;
            }
        }

        // 3) Cached result from a recent successful read.
        lock (Gate)
        {
            if (cachedWords.Count > 0 && Environment.TickCount64 - lastCacheAt < 2000)
                return cachedWords.ToList();
        }

        return [];
    }

    private static readonly string[] KnownKtgAddonNames =
    [
        "KTGInput",
        "KTGResult",
        "KTGWord",
        "KTGWords",
        "KTGEnemy",
        "KTGEnemyList",
        "KTGBalloon",
        "KTGHud",
        "KTGScreen",
        "KTGMain",
        "KTGBattle",
        "KTGType",
        "KTGTypeWord",
    ];

    private static void StoreCache(List<WordEntry> words, string source)
    {
        lock (Gate)
        {
            cachedWords = Deduplicate(words);
            lastCacheAt = Environment.TickCount64;
            lastSource = source;
        }
    }

    private static List<WordEntry> ReadTextNodesFromAddon(AtkUnitBasePtr addon, int maxNodes)
    {
        var words = new List<WordEntry>();
        if (addon.IsNull)
            return words;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || unit->RootNode == null)
            return words;

        var visited = 0;
        WalkChildren(unit->RootNode, words, ref visited, maxNodes);
        return Deduplicate(words);
    }

    private static void WalkChildren(AtkResNode* parent, List<WordEntry> words, ref int visited, int maxNodes)
    {
        if (parent == null)
            return;

        for (var node = parent->ChildNode; node != null; node = node->PrevSiblingNode)
        {
            if (visited++ >= maxNodes)
                return;
            Visit(node, words, ref visited, maxNodes);
        }
    }

    private static void Visit(AtkResNode* node, List<WordEntry> words, ref int visited, int maxNodes)
    {
        if (node == null || visited > maxNodes)
            return;

        try
        {
            if (node->IsVisible() && node->Type == NodeType.Text)
            {
                var text = ((AtkTextNode*)node)->NodeText.ToString();
                if (IsPlausibleWord(text))
                    words.Add(new WordEntry(0, text.Trim()));
            }

            if ((int)node->Type >= 1000)
            {
                var comp = (AtkComponentNode*)node;
                if (comp->Component != null)
                {
                    var uld = comp->Component->UldManager;
                    var count = Math.Min((int)uld.NodeListCount, 64);
                    for (var i = 0; i < count; i++)
                    {
                        if (visited++ >= maxNodes)
                            return;
                        var n = uld.NodeList[i];
                        if (n == null)
                            continue;
                        if (n->IsVisible() && n->Type == NodeType.Text)
                        {
                            var text = ((AtkTextNode*)n)->NodeText.ToString();
                            if (IsPlausibleWord(text))
                                words.Add(new WordEntry(0, text.Trim()));
                        }
                    }
                }
            }

            WalkChildren(node, words, ref visited, maxNodes);
        }
        catch (Exception ex)
        {
            PluginLogHelpers.WarnOnce("node-walk", 5000, "Text node walk issue: {Message}", ex.Message);
        }
    }

    public static bool SubmitWord(AtkUnitBasePtr addon, string word)
    {
        if (addon.IsNull || string.IsNullOrEmpty(word))
        {
            Plugin.Log.Error("SubmitWord aborted: addon/word missing.");
            return false;
        }

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null)
            return false;

        // Try named callback first.
        if (TryFire(unit, ["SubmitWord", word]))
            return true;

        // Some addons want the word only.
        if (TryFire(unit, [word]))
            return true;

        // Fallback: write into AtkValue[1] (input string slot from dump) and fire 0.
        try
        {
            if (unit->AtkValues != null && unit->AtkValuesCount > 1)
            {
                unit->AtkValues[1].SetManagedString(word);
                if (TryFire(unit, [0]))
                {
                    Plugin.Log.Information("Submit via AtkValues[1]+callback0 for '{Word}'", word);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Submit fallback failed for '{Word}'", word);
        }

        return false;
    }

    private static bool TryFire(AtkUnitBase* unit, object[] args)
    {
        var values = stackalloc AtkValue[args.Length];
        for (var i = 0; i < args.Length; i++)
            values[i] = default;

        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case string s:
                        values[i].SetManagedString(s);
                        break;
                    case int n:
                        values[i].SetInt(n);
                        break;
                    default:
                        return false;
                }
            }

            unit->FireCallback((uint)args.Length, values, true);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "FireCallback variant failed");
            return false;
        }
        finally
        {
            for (var i = 0; i < args.Length; i++)
                values[i].ReleaseManagedMemory();
        }
    }

    public static string Dump(AtkUnitBasePtr addon)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Last word source: {LastSource}");
        lock (Gate)
        {
            sb.AppendLine($"Cached words ({cachedWords.Count}):");
            foreach (var w in cachedWords)
                sb.AppendLine($"  \"{w.Text}\"");
        }

        if (addon.IsNull)
        {
            sb.AppendLine("KTGInput not open.");
        }
        else
        {
            sb.AppendLine($"KTGInput AtkValuesCount={addon.AtkValuesCount} visible={addon.IsVisible} ready={addon.IsReady}");
            var i = 0;
            foreach (var value in addon.AtkValues)
            {
                sb.Append($"  Atk[{i}] type={value.ValueType} ");
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
                catch { sb.Append("(read error)"); }

                sb.AppendLine();
                i++;
            }

            sb.AppendLine("-- KTGInput text nodes --");
            foreach (var w in ReadTextNodesFromAddon(addon, 256))
                sb.AppendLine($"  \"{w.Text}\"");
        }

        sb.AppendLine("-- Probed KTG* addon visibility --");
        foreach (var name in KnownKtgAddonNames)
        {
            var ptr = Plugin.GameGui.GetAddonByName(name);
            if (ptr.IsNull)
                continue;
            sb.AppendLine($"  {name}: visible={ptr.IsVisible} ready={ptr.IsReady} values={ptr.AtkValuesCount}");
            if (ptr.IsVisible)
            {
                foreach (var w in ReadTextNodesFromAddon(ptr, 64))
                    sb.AppendLine($"    text \"{w.Text}\"");
            }
        }

        // Safe stage string-array probe via official getter (not raw event pointers).
        sb.AppendLine("-- Stage string arrays with plausible words (safe getter, first 30) --");
        try
        {
            var stage = AtkStage.Instance();
            if (stage != null)
            {
                var hits = 0;
                var max = Enum.GetValues<StringArrayType>().Length;
                for (var i = 0; i < max && hits < 30; i++)
                {
                    var sad = stage->GetStringArrayData((StringArrayType)i);
                    if (sad == null || sad->Size <= 0 || sad->Size > 128 || sad->StringArray == null)
                        continue;
                    for (var s = 0; s < sad->Size && hits < 30; s++)
                    {
                        var text = ReadCString(sad->StringArray[s]);
                        if (!IsPlausibleWord(text))
                            continue;
                        sb.AppendLine($"  {(StringArrayType)i}[{s}]=\"{text}\"");
                        hits++;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  (stage scan skipped: {ex.Message})");
        }

        return sb.ToString();
    }

    private static bool IsPlausibleWord(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        text = text.Trim();
        if (text.Length is < 2 or > 48)
            return false;
        if (text.All(char.IsDigit))
            return false;

        var letter = 0;
        foreach (var c in text)
        {
            if (char.IsLetter(c))
                letter++;
            else if (!(char.IsWhiteSpace(c) || c is '.' or ',' or '\'' or '-' or '!' or '?' or ':' or ';'))
                return false;
        }

        return letter >= 2;
    }

    private static List<WordEntry> Deduplicate(List<WordEntry> words)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<WordEntry>();
        foreach (var w in words)
        {
            if (seen.Add(w.Text))
                result.Add(w);
        }

        return result;
    }

    private static string? ReadCString(CStringPointer ptr)
    {
        try
        {
            if (!ptr.HasValue)
                return null;
            return ptr.ToString();
        }
        catch
        {
            return null;
        }
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
