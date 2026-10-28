using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.NativeWrapper;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AtkValueType = Dalamud.Game.NativeWrapper.AtkValueType;
using CStringPointer = InteropGenerator.Runtime.CStringPointer;

namespace DragonTyper;

/// <summary>
/// Live Keybound Brawler input. Words are NOT in AtkValues (only flags live there).
/// They arrive via StringArrayData / NumberArrayData on PreRequestedUpdate, and as text nodes.
/// </summary>
internal static unsafe class KtgInput
{
    public const string AddonName = "KTGInput";

    public readonly record struct WordEntry(ulong EntityId, string Text);

    private static readonly object Gate = new();
    private static List<WordEntry> cachedWords = [];
    private static string[]? lastStrings;
    private static int[]? lastNumbers;
    private static bool listenerRegistered;
    private static long lastCacheAt;

    public static IReadOnlyList<WordEntry> CachedWords
    {
        get
        {
            lock (Gate)
                return cachedWords.ToList();
        }
    }

    public static void Register(IAddonLifecycle lifecycle)
    {
        if (listenerRegistered)
            return;
        lifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, AddonName, OnRequestedUpdate);
        lifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, AddonName, OnRequestedUpdate);
        listenerRegistered = true;
        Plugin.Log.Information("Registered KTGInput array listeners.");
    }

    public static void Unregister(IAddonLifecycle lifecycle)
    {
        if (!listenerRegistered)
            return;
        lifecycle.UnregisterListener(AddonEvent.PreRequestedUpdate, AddonName, OnRequestedUpdate);
        lifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, AddonName, OnRequestedUpdate);
        listenerRegistered = false;
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
        var fromCache = CachedWords;
        if (fromCache.Count > 0)
            return fromCache.ToList();

        var fromNodes = ReadWordsFromTextNodes();
        if (fromNodes.Count > 0)
            return fromNodes;

        if (!addon.IsNull)
            return ScanGlobalStringArraysForWords();

        return [];
    }

    private static void OnRequestedUpdate(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (args is not AddonRequestedUpdateArgs update)
                return;

            ParseArrays(update.NumberArrayData, update.StringArrayData);
        }
        catch (Exception ex)
        {
            PluginLogHelpers.ErrorOnce("ktg-update", 5000, ex, "KTGInput update parse failed");
        }
    }

    private static void ParseArrays(nint numberArrayData, nint stringArrayData)
    {
        if (stringArrayData == nint.Zero)
            return;

        var stringHolder = (StringArrayData**)stringArrayData;
        var numberHolder = numberArrayData == nint.Zero ? null : (NumberArrayData**)numberArrayData;

        var strings = new List<string>();
        var numbers = new List<int>();
        var words = new List<WordEntry>();

        CollectFromHolders(stringHolder, numberHolder, strings, numbers, words);

        lock (Gate)
        {
            lastStrings = strings.ToArray();
            lastNumbers = numbers.ToArray();
            if (words.Count > 0 || strings.Count > 0)
            {
                if (words.Count == 0)
                {
                    foreach (var s in strings)
                    {
                        if (IsPlausibleWord(s))
                            words.Add(new WordEntry(0, s));
                    }
                }

                cachedWords = Deduplicate(words);
                lastCacheAt = Environment.TickCount64;
            }
        }
    }

    private static void CollectFromHolders(
        StringArrayData** stringHolder,
        NumberArrayData** numberHolder,
        List<string> strings,
        List<int> numbers,
        List<WordEntry> words)
    {
        const int maxArrays = 160;

        for (var i = 0; i < maxArrays; i++)
        {
            StringArrayData* sad;
            try { sad = stringHolder[i]; }
            catch { break; }
            if (sad == null || sad->Size <= 0 || sad->Size > 512 || sad->StringArray == null)
                continue;

            var localStrings = new List<string>();
            for (var s = 0; s < sad->Size; s++)
            {
                var text = ReadCString(sad->StringArray[s]);
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                var trimmed = text.Trim();
                localStrings.Add(trimmed);
                strings.Add(trimmed);
            }

            var localNumbers = new List<int>();
            if (numberHolder != null)
            {
                NumberArrayData* nad;
                try { nad = numberHolder[i]; }
                catch { nad = null; }
                if (nad != null && nad->Size > 0 && nad->Size <= 512 && nad->IntArray != null)
                {
                    for (var n = 0; n < nad->Size; n++)
                    {
                        var v = nad->IntArray[n];
                        localNumbers.Add(v);
                        numbers.Add(v);
                    }
                }
            }

            PairWords(localStrings, localNumbers, words);
        }
    }

    private static void PairWords(List<string> stringList, List<int> numberList, List<WordEntry> words)
    {
        var wordStrings = stringList.Where(IsPlausibleWord).ToList();
        if (wordStrings.Count == 0)
            return;

        var entityIds = numberList.Where(n => n > 0x10000).Select(n => (ulong)(uint)n).ToList();
        if (entityIds.Count == 0)
            entityIds = numberList.Where(n => n > 0).Select(n => (ulong)(uint)n).ToList();

        if (entityIds.Count == wordStrings.Count)
        {
            for (var i = 0; i < wordStrings.Count; i++)
                words.Add(new WordEntry(entityIds[i], wordStrings[i]));
            return;
        }

        if (numberList.Count > 0 && numberList[0] == wordStrings.Count && numberList.Count >= wordStrings.Count + 1)
        {
            for (var i = 0; i < wordStrings.Count; i++)
                words.Add(new WordEntry((ulong)(uint)numberList[i + 1], wordStrings[i]));
            return;
        }

        foreach (var s in wordStrings)
            words.Add(new WordEntry(0, s));
    }

    private static List<WordEntry> ReadWordsFromTextNodes()
    {
        var words = new List<WordEntry>();
        foreach (var name in ListOpenAddonNames())
        {
            if (!name.StartsWith("KTG", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Word", StringComparison.OrdinalIgnoreCase))
                continue;

            var ptr = Plugin.GameGui.GetAddonByName(name);
            if (ptr.IsNull || !ptr.IsVisible)
                continue;
            CollectTextNodes((AtkUnitBase*)ptr.Address, words);
        }

        return Deduplicate(words);
    }

    private static void CollectTextNodes(AtkUnitBase* addon, List<WordEntry> words)
    {
        if (addon == null || addon->RootNode == null)
            return;
        WalkChildren(addon->RootNode, words);
    }

    private static void WalkChildren(AtkResNode* parent, List<WordEntry> words)
    {
        for (var node = parent->ChildNode; node != null; node = node->PrevSiblingNode)
            Visit(node, words);
    }

    private static void Visit(AtkResNode* node, List<WordEntry> words)
    {
        if (node == null || !node->IsVisible())
            return;

        if (node->Type == NodeType.Text)
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
                for (var i = 0; i < uld.NodeListCount; i++)
                    Visit(uld.NodeList[i], words);
            }
        }

        WalkChildren(node, words);
    }

    private static List<WordEntry> ScanGlobalStringArraysForWords()
    {
        var words = new List<WordEntry>();
        var stage = AtkStage.Instance();
        if (stage == null)
            return words;

        for (var i = 0; i < 160; i++)
        {
            var sad = stage->GetStringArrayData((StringArrayType)i);
            if (sad == null || sad->Size <= 0 || sad->Size > 256 || sad->StringArray == null)
                continue;

            for (var s = 0; s < sad->Size; s++)
            {
                var text = ReadCString(sad->StringArray[s]);
                if (IsPlausibleWord(text))
                    words.Add(new WordEntry(0, text!.Trim()));
            }
        }

        return Deduplicate(words);
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

        if (Fire(unit, "SubmitWord", word))
            return true;

        try
        {
            if (unit->AtkValues != null && unit->AtkValuesCount > 1)
            {
                unit->AtkValues[1].SetManagedString(word);
                var values = stackalloc AtkValue[1];
                values[0] = default;
                values[0].SetInt(0);
                unit->FireCallback(1, values, true);
                values[0].ReleaseManagedMemory();
                Plugin.Log.Information("Submit fallback via AtkValues[1] for '{Word}'", word);
                return true;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Submit fallback failed for '{Word}'", word);
        }

        return false;
    }

    private static bool Fire(AtkUnitBase* unit, string action, string word)
    {
        var values = stackalloc AtkValue[2];
        values[0] = default;
        values[1] = default;
        try
        {
            values[0].SetManagedString(action);
            values[1].SetManagedString(word);
            unit->FireCallback(2, values, true);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "FireCallback {Action} failed for '{Word}'", action, word);
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
        var sb = new StringBuilder();
        if (addon.IsNull)
        {
            sb.AppendLine("KTGInput not open.");
        }
        else
        {
            sb.AppendLine($"KTGInput AtkValuesCount={addon.AtkValuesCount} visible={addon.IsVisible} ready={addon.IsReady} id={addon.Id}");
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

            sb.AppendLine("-- Text nodes on KTGInput --");
            DumpTextNodes((AtkUnitBase*)addon.Address, sb);
        }

        lock (Gate)
        {
            sb.AppendLine($"-- Cached words ({cachedWords.Count}), age={Environment.TickCount64 - lastCacheAt}ms --");
            foreach (var w in cachedWords)
                sb.AppendLine($"  entity={w.EntityId} text=\"{w.Text}\"");

            if (lastStrings != null)
            {
                sb.AppendLine($"-- Last string snapshot ({lastStrings.Length}) --");
                for (var i = 0; i < lastStrings.Length && i < 64; i++)
                    sb.AppendLine($"  S[{i}]=\"{lastStrings[i]}\"");
            }

            if (lastNumbers != null)
            {
                sb.AppendLine($"-- Last number snapshot ({lastNumbers.Length}) --");
                for (var i = 0; i < lastNumbers.Length && i < 64; i++)
                    sb.AppendLine($"  N[{i}]={lastNumbers[i]}");
            }
        }

        sb.AppendLine("-- Open addons matching KTG / Word / Type --");
        foreach (var name in ListOpenAddonNames())
        {
            if (name.Contains("KTG", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Word", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Type", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Keybound", StringComparison.OrdinalIgnoreCase))
                sb.AppendLine($"  {name}");
        }

        sb.AppendLine("-- Global string arrays with plausible words (first 40) --");
        var hits = 0;
        var stage = AtkStage.Instance();
        if (stage != null)
        {
            for (var i = 0; i < 160 && hits < 40; i++)
            {
                var sad = stage->GetStringArrayData((StringArrayType)i);
                if (sad == null || sad->Size <= 0 || sad->Size > 256 || sad->StringArray == null)
                    continue;
                for (var s = 0; s < sad->Size && hits < 40; s++)
                {
                    var text = ReadCString(sad->StringArray[s]);
                    if (!IsPlausibleWord(text))
                        continue;
                    sb.AppendLine($"  Array[{i}][{s}]=\"{text}\"");
                    hits++;
                }
            }
        }

        return sb.ToString();
    }

    private static void DumpTextNodes(AtkUnitBase* addon, StringBuilder sb)
    {
        if (addon == null || addon->RootNode == null)
            return;
        DumpWalkChildren(addon->RootNode, sb, 0);
    }

    private static void DumpWalkChildren(AtkResNode* parent, StringBuilder sb, int depth)
    {
        if (parent == null || depth > 40)
            return;
        for (var node = parent->ChildNode; node != null; node = node->PrevSiblingNode)
        {
            if (node->Type == NodeType.Text)
            {
                var text = ((AtkTextNode*)node)->NodeText.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                    sb.AppendLine($"  TextNode \"{text}\" visible={node->IsVisible()}");
            }

            if ((int)node->Type >= 1000)
            {
                var comp = (AtkComponentNode*)node;
                if (comp->Component != null)
                {
                    var uld = comp->Component->UldManager;
                    for (var i = 0; i < uld.NodeListCount; i++)
                    {
                        var n = uld.NodeList[i];
                        if (n != null && n->Type == NodeType.Text)
                        {
                            var text = ((AtkTextNode*)n)->NodeText.ToString();
                            if (!string.IsNullOrWhiteSpace(text))
                                sb.AppendLine($"  CompText \"{text}\" visible={n->IsVisible()}");
                        }
                    }
                }
            }

            DumpWalkChildren(node, sb, depth + 1);
        }
    }

    private static List<string> ListOpenAddonNames()
    {
        var names = new List<string>();
        try
        {
            var stage = AtkStage.Instance();
            var mgr = stage != null ? stage->RaptureAtkUnitManager : null;
            if (mgr == null)
                return names;

            var span = mgr->AllLoadedUnitsList.Entries;
            foreach (var entry in span)
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
