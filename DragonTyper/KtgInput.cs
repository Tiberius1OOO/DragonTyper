using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Game.NativeWrapper;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AtkValueType = Dalamud.Game.NativeWrapper.AtkValueType;
using CStringPointer = InteropGenerator.Runtime.CStringPointer;

namespace DragonTyper;

/// <summary>
/// Safe Keybound Brawler word reader.
/// KTGInput only holds input flags. Live words come from Agent KTGHud's addon / NamePlate-like UI.
/// </summary>
internal static unsafe class KtgInput
{
    public const string AddonName = "KTGInput";
    private const int AgentKtgHud = (int)AgentId.KTGHud;         // 397
    private const int AgentKtgResult = (int)AgentId.KTGResult;   // 401

    public readonly record struct WordEntry(ulong EntityId, string Text);

    private static readonly object Gate = new();
    private static List<WordEntry> cachedWords = [];
    private static long lastCacheAt;
    private static string lastSource = "none";

    private static readonly HashSet<string> NoiseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "MP", "CP", "GP", "TP", "EP", "HP", "Limit Break", "Support", "Fair Skies",
        "Playing Recorded Duty", "Recording Duty", "Bard", "Phoenix",
    };

    public static IReadOnlyList<WordEntry> CachedWords
    {
        get { lock (Gate) return cachedWords.ToList(); }
    }

    public static string LastSource
    {
        get { lock (Gate) return lastSource; }
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

    public static List<WordEntry> ReadWords(AtkUnitBasePtr inputAddon)
    {
        // 1) Agent KTGHud addon (word balloons / HUD for the minigame).
        var fromHud = ReadWordsFromAgentAddon(AgentKtgHud, "AgentKTGHud");
        if (fromHud.Count > 0)
            return fromHud;

        // 2) NamePlate string array (bubbles sometimes ride nameplates).
        var fromPlates = ReadWordsFromStringArray(StringArrayType.NamePlate, "NamePlate");
        if (fromPlates.Count > 0)
            return fromPlates;

        // 3) KTGInput itself (usually empty, but cheap).
        if (!inputAddon.IsNull)
        {
            var local = ReadTextNodesFromAddon(inputAddon, 128);
            if (local.Count > 0)
            {
                StoreCache(local, "KTGInput text nodes");
                return local;
            }
        }

        // 4) Any loaded addon whose name contains KTG (safe unit-list scan, dump-validated path).
        var fromAny = ReadWordsFromLoadedKtgAddons();
        if (fromAny.Count > 0)
            return fromAny;

        lock (Gate)
        {
            if (cachedWords.Count > 0 && Environment.TickCount64 - lastCacheAt < 1500)
                return cachedWords.ToList();
        }

        return [];
    }

    private static List<WordEntry> ReadWordsFromAgentAddon(int agentId, string label)
    {
        try
        {
            var agent = Plugin.GameGui.GetAgentById(agentId);
            if (agent.IsNull || !agent.IsAgentActive)
                return [];

            var addon = agent.Addon;
            if (addon.IsNull || !addon.IsVisible)
                return [];

            var words = ReadTextNodesFromAddon(addon, 400);
            // Also AtkValues strings on that addon.
            words.AddRange(ReadAtkValueStrings(addon));

            words = Deduplicate(words);
            if (words.Count > 0)
            {
                StoreCache(words, $"{label} addon={addon.Name}");
                return words;
            }
        }
        catch (Exception ex)
        {
            PluginLogHelpers.WarnOnce($"agent-{agentId}", 5000, "Agent {Id} read failed: {Message}", agentId, ex.Message);
        }

        return [];
    }

    private static List<WordEntry> ReadWordsFromStringArray(StringArrayType type, string label)
    {
        var words = new List<WordEntry>();
        try
        {
            var stage = AtkStage.Instance();
            if (stage == null)
                return words;

            var sad = stage->GetStringArrayData(type);
            if (sad == null || sad->Size <= 0 || sad->Size > 512 || sad->StringArray == null)
                return words;

            for (var i = 0; i < sad->Size; i++)
            {
                var text = ReadCString(sad->StringArray[i]);
                if (IsTargetWord(text))
                    words.Add(new WordEntry(0, text!.Trim()));
            }

            words = Deduplicate(words);
            if (words.Count > 0)
                StoreCache(words, label);
        }
        catch (Exception ex)
        {
            PluginLogHelpers.WarnOnce($"array-{type}", 5000, "StringArray {Type} read failed: {Message}", type, ex.Message);
        }

        return words;
    }

    private static List<WordEntry> ReadWordsFromLoadedKtgAddons()
    {
        var words = new List<WordEntry>();
        try
        {
            foreach (var name in ListLoadedAddonNames())
            {
                if (!name.Contains("KTG", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (name.Equals(AddonName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var addon = Plugin.GameGui.GetAddonByName(name);
                if (addon.IsNull || !addon.IsVisible)
                    continue;

                var found = ReadTextNodesFromAddon(addon, 300);
                found.AddRange(ReadAtkValueStrings(addon));
                if (found.Count == 0)
                    continue;

                words.AddRange(found);
            }

            words = Deduplicate(words);
            if (words.Count > 0)
                StoreCache(words, "loaded KTG* addons");
        }
        catch (Exception ex)
        {
            PluginLogHelpers.WarnOnce("ktg-enum", 5000, "KTG addon enum failed: {Message}", ex.Message);
        }

        return words;
    }

    private static void StoreCache(List<WordEntry> words, string source)
    {
        lock (Gate)
        {
            cachedWords = Deduplicate(words);
            lastCacheAt = Environment.TickCount64;
            lastSource = source;
        }
    }

    private static List<WordEntry> ReadAtkValueStrings(AtkUnitBasePtr addon)
    {
        var words = new List<WordEntry>();
        if (addon.IsNull)
            return words;

        foreach (var value in addon.AtkValues)
        {
            try
            {
                if (value.GetValue() is string s && IsTargetWord(s))
                    words.Add(new WordEntry(0, s.Trim()));
            }
            catch
            {
                // ignore
            }
        }

        return words;
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
                if (IsTargetWord(text))
                    words.Add(new WordEntry(0, text.Trim()));
            }

            if ((int)node->Type >= 1000)
            {
                var comp = (AtkComponentNode*)node;
                if (comp->Component != null)
                {
                    var uld = comp->Component->UldManager;
                    var count = Math.Min((int)uld.NodeListCount, 96);
                    for (var i = 0; i < count; i++)
                    {
                        if (visited++ >= maxNodes)
                            return;
                        var n = uld.NodeList[i];
                        if (n == null || !n->IsVisible() || n->Type != NodeType.Text)
                            continue;
                        var text = ((AtkTextNode*)n)->NodeText.ToString();
                        if (IsTargetWord(text))
                            words.Add(new WordEntry(0, text.Trim()));
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

        if (TryFire(unit, ["SubmitWord", word]))
            return true;
        if (TryFire(unit, [word]))
            return true;

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

    public static string Dump(AtkUnitBasePtr inputAddon)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Last word source: {LastSource}");
        lock (Gate)
        {
            sb.AppendLine($"Cached words ({cachedWords.Count}):");
            foreach (var w in cachedWords)
                sb.AppendLine($"  \"{w.Text}\"");
        }

        DumpAgent(sb, AgentKtgHud, "KTGHud");
        DumpAgent(sb, AgentKtgResult, "KTGResult");

        if (!inputAddon.IsNull)
        {
            sb.AppendLine($"KTGInput values={inputAddon.AtkValuesCount} visible={inputAddon.IsVisible}");
            var i = 0;
            foreach (var value in inputAddon.AtkValues)
            {
                sb.Append($"  Atk[{i}] {value.ValueType} ");
                try
                {
                    var raw = value.GetValue();
                    sb.Append(raw switch
                    {
                        string s => $"\"{s}\"",
                        null => "(null)",
                        _ => raw?.ToString(),
                    });
                }
                catch { sb.Append("(err)"); }
                sb.AppendLine();
                i++;
            }
        }
        else
        {
            sb.AppendLine("KTGInput not open.");
        }

        sb.AppendLine("-- Loaded addons containing KTG / Word / Type --");
        foreach (var name in ListLoadedAddonNames())
        {
            if (name.Contains("KTG", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Word", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Type", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Keybound", StringComparison.OrdinalIgnoreCase))
            {
                var a = Plugin.GameGui.GetAddonByName(name);
                sb.AppendLine($"  {name} visible={a.IsVisible} ready={a.IsReady} values={a.AtkValuesCount}");
                if (!a.IsNull && a.IsVisible)
                {
                    foreach (var w in ReadTextNodesFromAddon(a, 80))
                        sb.AppendLine($"    text \"{w.Text}\"");
                }
            }
        }

        sb.AppendLine("-- NamePlate target-like strings --");
        foreach (var w in ReadWordsFromStringArray(StringArrayType.NamePlate, "NamePlate-dump"))
            sb.AppendLine($"  \"{w.Text}\"");

        sb.AppendLine("-- Stage arrays: target-like words only (skip Hud noise) --");
        try
        {
            var stage = AtkStage.Instance();
            if (stage != null)
            {
                var hits = 0;
                foreach (StringArrayType type in Enum.GetValues<StringArrayType>())
                {
                    if (type is StringArrayType.Hud or StringArrayType.Hud2 or StringArrayType.DTR or StringArrayType.NaviMap)
                        continue;
                    var sad = stage->GetStringArrayData(type);
                    if (sad == null || sad->Size <= 0 || sad->Size > 256 || sad->StringArray == null)
                        continue;
                    for (var s = 0; s < sad->Size && hits < 40; s++)
                    {
                        var text = ReadCString(sad->StringArray[s]);
                        if (!IsTargetWord(text))
                            continue;
                        sb.AppendLine($"  {type}[{s}]=\"{text}\"");
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

    private static void DumpAgent(StringBuilder sb, int agentId, string label)
    {
        try
        {
            var agent = Plugin.GameGui.GetAgentById(agentId);
            sb.AppendLine($"-- Agent {label} ({agentId}) null={agent.IsNull} active={(!agent.IsNull && agent.IsAgentActive)} addonId={(!agent.IsNull ? agent.AddonId : 0)} --");
            if (agent.IsNull)
                return;

            var addon = agent.Addon;
            if (addon.IsNull)
            {
                sb.AppendLine("  (no addon linked)");
                return;
            }

            sb.AppendLine($"  addon name=\"{addon.Name}\" visible={addon.IsVisible} ready={addon.IsReady} values={addon.AtkValuesCount}");
            foreach (var w in ReadTextNodesFromAddon(addon, 120))
                sb.AppendLine($"  text \"{w.Text}\"");
            foreach (var w in ReadAtkValueStrings(addon))
                sb.AppendLine($"  atk \"{w.Text}\"");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  agent dump failed: {ex.Message}");
        }
    }

    private static List<string> ListLoadedAddonNames()
    {
        var names = new List<string>();
        try
        {
            var stage = AtkStage.Instance();
            var mgr = stage != null ? stage->RaptureAtkUnitManager : null;
            if (mgr == null)
                return names;

            foreach (var entry in mgr->AllLoadedUnitsList.Entries)
            {
                var unit = entry.Value;
                if (unit == null)
                    continue;
                var name = unit->NameString;
                if (!string.IsNullOrEmpty(name))
                    names.Add(name);
            }
        }
        catch (Exception ex)
        {
            names.Add($"(enumerate failed: {ex.Message})");
        }

        return names;
    }

    /// <summary>Words shown above enemies: short letter texts, not HUD chrome / player names with spaces+title.</summary>
    private static bool IsTargetWord(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        text = text.Trim();
        if (NoiseWords.Contains(text))
            return false;
        if (text.Length is < 2 or > 48)
            return false;
        if (text.All(char.IsDigit))
            return false;

        // Strip common SeString junk leftovers.
        if (text.Contains('\u0002') || text.Contains('\u0003'))
            return false;

        var letter = 0;
        foreach (var c in text)
        {
            if (char.IsLetter(c))
                letter++;
            else if (!(char.IsWhiteSpace(c) || c is '.' or ',' or '\'' or '-' or '!' or '?' or ':' or ';'))
                return false;
        }

        if (letter < 2)
            return false;

        // Player-like "First Last" with two capitals is usually not a target word,
        // but allow single-token words and short phrases used by the minigame.
        return true;
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
