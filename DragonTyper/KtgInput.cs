using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.NativeWrapper;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using CsAtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;
using CStringPointer = InteropGenerator.Runtime.CStringPointer;
using Utf8String = FFXIVClientStructs.FFXIV.Client.System.String.Utf8String;

namespace DragonTyper;

/// <summary>
/// Keybound Brawler word reader.
/// KTGInput only holds input flags. Live words live on KTGEWord (and related KTG* addons).
/// NamePlate is intentionally ignored — it is full of player/pet names.
/// </summary>
internal static unsafe class KtgInput
{
    public const string AddonName = "KTGInput";
    public const string WordAddonName = "KTGEWord";
    private const int AgentKtgHud = (int)AgentId.KTGHud;
    private const int AgentKtgResult = (int)AgentId.KTGResult;

    public readonly record struct WordEntry(ulong EntityId, string Text);

    private static readonly object Gate = new();
    private static List<WordEntry> cachedWords = [];
    private static long lastCacheAt;
    private static string lastSource = "none";

    private static readonly HashSet<string> NoiseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "MP", "CP", "GP", "TP", "EP", "HP", "Limit Break", "Support", "Fair Skies",
        "Playing Recorded Duty", "Recording Duty", "Bard", "Phoenix", "SCORE",
        "TYPING FEVER!", "TYPING FEVER", "Duty Information", "In Duty", "Viewing Cutscene",
        "The Gold Saucer", "Immortal Flames", "Order of the Twin Adder", "Maelstrom",
        "Defeat the airbuster", "The reinforcements fade...", "Miss", "Reflect", "Reflected",
        "Interrupted!", "Interrupted", "Resist", "Charge", "BONUS", "HP",
        "Enter", "Delete Text/Quit",
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
            Dalamud.Game.NativeWrapper.AtkValueType.Bool => value.TryGet<bool>(out var b) && b,
            Dalamud.Game.NativeWrapper.AtkValueType.Int => value.TryGet<int>(out var i) && i != 0,
            Dalamud.Game.NativeWrapper.AtkValueType.UInt => value.TryGet<uint>(out var u) && u != 0,
            _ => AsBool(value.GetValue()),
        };
        return true;
    }

    public static List<WordEntry> ReadWords(AtkUnitBasePtr inputAddon)
    {
        // Only KTGEWord balloons — other KTG* addons leak cast-bar / HUD text.
        var fromWord = ReadWordsFromNamedAddon(WordAddonName, "KTGEWord");
        if (fromWord.Count > 0)
            return fromWord;

        // Brief cache so a one-frame flicker does not clear the queue.
        lock (Gate)
        {
            if (cachedWords.Count > 0 && Environment.TickCount64 - lastCacheAt < 800)
                return cachedWords.ToList();
        }

        _ = inputAddon;
        return [];
    }

    private static List<WordEntry> ReadWordsFromNamedAddon(string name, string label)
    {
        try
        {
            var addon = Plugin.GameGui.GetAddonByName(name);
            if (addon.IsNull || !addon.IsVisible)
                return [];

            var words = CollectFromAddon(addon, includeInvisible: true);
            if (words.Count > 0)
            {
                StoreCache(words, label);
                return words;
            }
        }
        catch (Exception ex)
        {
            PluginLogHelpers.WarnOnce($"addon-{name}", 5000, "Addon {Name} read failed: {Message}", name, ex.Message);
        }

        return [];
    }

    private static List<WordEntry> ReadWordsFromLoadedKtgWordAddons()
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
                if (name.Equals(WordAddonName, StringComparison.OrdinalIgnoreCase))
                    continue;
                // Skip chrome / gauges / logs — keep Word / Type / Enemy balloons.
                var keep = name.Contains("Word", StringComparison.OrdinalIgnoreCase)
                           || name.Contains("Type", StringComparison.OrdinalIgnoreCase)
                           || name.Contains("Enemy", StringComparison.OrdinalIgnoreCase);
                if (!keep)
                    continue;

                var addon = Plugin.GameGui.GetAddonByName(name);
                if (addon.IsNull || !addon.IsVisible)
                    continue;

                words.AddRange(CollectFromAddon(addon, includeInvisible: true));
            }

            words = FilterAndDedup(words);
            if (words.Count > 0)
                StoreCache(words, "loaded KTG word addons");
        }
        catch (Exception ex)
        {
            PluginLogHelpers.WarnOnce("ktg-word-enum", 5000, "KTG word addon enum failed: {Message}", ex.Message);
        }

        return words;
    }

    private static List<WordEntry> ReadWordsFromRawStringArrays()
    {
        var words = new List<WordEntry>();
        try
        {
            var module = RaptureAtkModule.Instance();
            if (module == null)
                return words;

            var holder = module->AtkArrayDataHolder;
            var count = Math.Min((int)holder.StringArrayCount, 256);
            // NamePlate is usually a known enum index — skip by content filter instead.
            for (var i = 0; i < count; i++)
            {
                var sad = module->GetStringArrayData(i);
                if (sad == null || sad->Size <= 0 || sad->Size > 512 || sad->StringArray == null)
                    continue;

                // Skip huge chrome arrays and NamePlate-sized player lists.
                if (sad->Size > 120)
                    continue;

                for (var s = 0; s < sad->Size; s++)
                {
                    var text = ReadCString(sad->StringArray[s]);
                    if (IsMinigameWord(text))
                        words.Add(new WordEntry(0, text!.Trim()));
                }
            }

            words = FilterAndDedup(words);
            if (words.Count > 0)
                StoreCache(words, "raw StringArray");
        }
        catch (Exception ex)
        {
            PluginLogHelpers.WarnOnce("raw-arrays", 5000, "Raw string array read failed: {Message}", ex.Message);
        }

        return words;
    }

    private static void StoreCache(List<WordEntry> words, string source)
    {
        lock (Gate)
        {
            cachedWords = FilterAndDedup(words);
            lastCacheAt = Environment.TickCount64;
            lastSource = source;
        }
    }

    private static List<WordEntry> CollectFromAddon(AtkUnitBasePtr addon, bool includeInvisible)
    {
        var words = new List<WordEntry>();
        if (addon.IsNull)
            return words;

        words.AddRange(ReadAtkValueStrings(addon));
        words.AddRange(ReadTextNodesFromAddon(addon, 500, includeInvisible));
        return FilterAndDedup(words);
    }

    private static List<WordEntry> ReadAtkValueStrings(AtkUnitBasePtr addon)
    {
        var words = new List<WordEntry>();
        if (addon.IsNull)
            return words;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || unit->AtkValues == null || unit->AtkValuesCount == 0)
            return words;

        var count = Math.Min((int)unit->AtkValuesCount, 64);
        for (var i = 0; i < count; i++)
        {
            try
            {
                CollectAtkValueStrings(&unit->AtkValues[i], words, depth: 0);
            }
            catch
            {
                // ignore one bad slot
            }
        }

        return words;
    }

    private static void CollectAtkValueStrings(AtkValue* value, List<WordEntry> words, int depth)
    {
        if (value == null || depth > 3)
            return;

        var type = value->Type;
        if (type is CsAtkValueType.String or CsAtkValueType.ManagedString or CsAtkValueType.ConstString)
        {
            var text = value->String.ToString();
            if (IsMinigameWord(text))
                words.Add(new WordEntry(0, text.Trim()));
            return;
        }

        if (type == CsAtkValueType.WideString)
        {
            var text = Marshal.PtrToStringUni((nint)value->WideString);
            if (IsMinigameWord(text))
                words.Add(new WordEntry(0, text!.Trim()));
            return;
        }

        if (type is CsAtkValueType.Vector or CsAtkValueType.ManagedVector)
        {
            try
            {
                var size = value->GetVectorSize();
                for (uint i = 0; i < size && i < 32; i++)
                {
                    var child = value->GetVectorValue(i);
                    if (child != null)
                        CollectAtkValueStrings(child, words, depth + 1);
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    private static List<WordEntry> ReadTextNodesFromAddon(AtkUnitBasePtr addon, int maxNodes, bool includeInvisible)
    {
        var words = new List<WordEntry>();
        if (addon.IsNull)
            return words;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || unit->RootNode == null)
            return words;

        var visited = 0;
        WalkChildren(unit->RootNode, words, ref visited, maxNodes, includeInvisible);
        return words;
    }

    private static void WalkChildren(AtkResNode* parent, List<WordEntry> words, ref int visited, int maxNodes, bool includeInvisible)
    {
        if (parent == null)
            return;

        for (var node = parent->ChildNode; node != null; node = node->PrevSiblingNode)
        {
            if (visited++ >= maxNodes)
                return;
            Visit(node, words, ref visited, maxNodes, includeInvisible);
        }
    }

    private static void Visit(AtkResNode* node, List<WordEntry> words, ref int visited, int maxNodes, bool includeInvisible)
    {
        if (node == null || visited > maxNodes)
            return;

        try
        {
            var visibleOk = includeInvisible || node->IsVisible();
            if (visibleOk && node->Type == NodeType.Text)
            {
                var text = ((AtkTextNode*)node)->NodeText.ToString();
                if (IsMinigameWord(text))
                    words.Add(new WordEntry(0, text.Trim()));
            }

            if ((int)node->Type >= 1000)
            {
                var comp = (AtkComponentNode*)node;
                if (comp->Component != null)
                {
                    var uld = comp->Component->UldManager;
                    var count = Math.Min((int)uld.NodeListCount, 128);
                    for (var i = 0; i < count; i++)
                    {
                        if (visited++ >= maxNodes)
                            return;
                        var n = uld.NodeList[i];
                        if (n == null || n->Type != NodeType.Text)
                            continue;
                        if (!includeInvisible && !n->IsVisible())
                            continue;
                        var text = ((AtkTextNode*)n)->NodeText.ToString();
                        if (IsMinigameWord(text))
                            words.Add(new WordEntry(0, text.Trim()));
                    }
                }
            }

            WalkChildren(node, words, ref visited, maxNodes, includeInvisible);
        }
        catch (Exception ex)
        {
            PluginLogHelpers.WarnOnce("node-walk", 5000, "Text node walk issue: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Minion does control:Action("SubmitWord", text). That maps to the KTGInput
    /// text field + Enter, or a FireCallback with close/updateState=false.
    /// Earlier builds always passed close=true and ignored the return value.
    /// </summary>
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

        // 1) Real input path: set TextInput then fire Enter (UI hint: [Enter] Submit Text).
        try
        {
            if (TrySubmitViaTextInput(unit, word))
            {
                Plugin.Log.Information("Submit via TextInput+Enter for '{Word}'", word);
                return true;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "TextInput submit failed for '{Word}'", word);
        }

        // 2) Minion-style named action: FireCallback("SubmitWord", text), close=false.
        if (TryFire(unit, close: false, "SubmitWord", word))
        {
            Plugin.Log.Information("Submit via FireCallback SubmitWord for '{Word}'", word);
            return true;
        }

        // 3) Set AtkValues[1] (input buffer; dump showed a fullwidth space there) then callback.
        try
        {
            if (unit->AtkValues != null && unit->AtkValuesCount > 1)
            {
                unit->AtkValues[1].SetManagedString(word);
                if (TryFire(unit, close: false, "SubmitWord", word)
                    || TryFire(unit, close: false, 0)
                    || TryFire(unit, close: false, word))
                {
                    Plugin.Log.Information("Submit via AtkValues[1]+callback for '{Word}'", word);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Submit AtkValues fallback failed for '{Word}'", word);
        }

        // 4) Last resorts (still close=false).
        if (TryFire(unit, close: false, word))
            return true;
        if (TryFire(unit, close: false, 0, word))
            return true;

        Plugin.Log.Error("All SubmitWord paths failed for '{Word}'", word);
        return false;
    }

    private static bool TrySubmitViaTextInput(AtkUnitBase* unit, string word)
    {
        var textInput = FindTextInput(unit);
        if (textInput == null)
            return false;

        textInput->SetText(word);

        var inputBase = (AtkComponentInputBase*)textInput;
        if (inputBase->Callback == null || inputBase->OwnerAddon == null)
            return false;

        // Prefer evaluated/raw strings after SetText.
        var rawPtr = inputBase->RawString.StringPtr;
        var evalPtr = inputBase->EvaluatedString.StringPtr;
        if (!rawPtr.HasValue && !evalPtr.HasValue)
            return false;

        var result = inputBase->Callback(
            inputBase->OwnerAddon,
            InputCallbackType.Enter,
            rawPtr.HasValue ? rawPtr : evalPtr,
            evalPtr.HasValue ? evalPtr : rawPtr,
            inputBase->CallbackEventKind);

        Plugin.Log.Debug("TextInput Enter callback result={Result} for '{Word}'", result, word);
        return result is InputCallbackResult.None or InputCallbackResult.ClearText;
    }

    private static AtkComponentTextInput* FindTextInput(AtkUnitBase* unit)
    {
        if (unit == null)
            return null;

        try
        {
            var uld = unit->UldManager;
            var count = Math.Min((int)uld.NodeListCount, 256);
            for (var i = 0; i < count; i++)
            {
                var node = uld.NodeList[i];
                if (node == null || (int)node->Type < 1000)
                    continue;
                var comp = ((AtkComponentNode*)node)->Component;
                if (comp == null)
                    continue;
                if (comp->GetComponentType() == ComponentType.TextInput)
                    return (AtkComponentTextInput*)comp;
            }
        }
        catch (Exception ex)
        {
            PluginLogHelpers.WarnOnce("find-textinput", 5000, "FindTextInput failed: {Message}", ex.Message);
        }

        return null;
    }

    /// <summary>ECommons-style FireCallback: UTF-8 String values, close/updateState flag, real return.</summary>
    private static bool TryFire(AtkUnitBase* unit, bool close, params object[] args)
    {
        if (unit == null || args.Length == 0)
            return false;

        var values = (AtkValue*)Marshal.AllocHGlobal(args.Length * sizeof(AtkValue));
        if (values == null)
            return false;

        var stringAllocs = new List<IntPtr>();
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                values[i] = default;
                switch (args[i])
                {
                    case string s:
                    {
                        // Match ECommons: Type=String with UTF-8 heap buffer (not ManagedString).
                        var bytes = Encoding.UTF8.GetBytes(s);
                        var alloc = Marshal.AllocHGlobal(bytes.Length + 1);
                        Marshal.Copy(bytes, 0, alloc, bytes.Length);
                        Marshal.WriteByte(alloc, bytes.Length, 0);
                        stringAllocs.Add(alloc);
                        values[i].Type = CsAtkValueType.String;
                        values[i].String = (byte*)alloc;
                        break;
                    }
                    case int n:
                        values[i].Type = CsAtkValueType.Int;
                        values[i].Int = n;
                        break;
                    case uint u:
                        values[i].Type = CsAtkValueType.UInt;
                        values[i].UInt = u;
                        break;
                    case bool b:
                        values[i].Type = CsAtkValueType.Bool;
                        values[i].Byte = (byte)(b ? 1 : 0);
                        break;
                    default:
                        return false;
                }
            }

            var ok = unit->FireCallback((uint)args.Length, values, close);
            if (!ok)
                Plugin.Log.Debug("FireCallback returned false (close={Close}, args={Args})", close, args.Length);
            return ok;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "FireCallback variant failed");
            return false;
        }
        finally
        {
            foreach (var alloc in stringAllocs)
                Marshal.FreeHGlobal(alloc);
            Marshal.FreeHGlobal((IntPtr)values);
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

        DumpNamedAddon(sb, WordAddonName);
        DumpNamedAddon(sb, AddonName);
        DumpAgent(sb, AgentKtgHud, "KTGHud");
        DumpAgent(sb, AgentKtgResult, "KTGResult");

        sb.AppendLine("-- Loaded KTG addons (raw Atk + text, no filter) --");
        foreach (var name in ListLoadedAddonNames())
        {
            if (!name.Contains("KTG", StringComparison.OrdinalIgnoreCase))
                continue;
            var a = Plugin.GameGui.GetAddonByName(name);
            sb.AppendLine($"  {name} visible={a.IsVisible} ready={a.IsReady} values={a.AtkValuesCount}");
            if (a.IsNull || !a.IsVisible)
                continue;
            DumpAddonAtkValues(sb, a, "    ");
            foreach (var raw in ReadAllTextNodesRaw(a, 60))
                sb.AppendLine($"    text \"{raw}\"");
        }

        sb.AppendLine("-- Raw string arrays (compact, word-like only) --");
        try
        {
            var module = RaptureAtkModule.Instance();
            if (module != null)
            {
                var holder = module->AtkArrayDataHolder;
                var count = Math.Min((int)holder.StringArrayCount, 256);
                var hits = 0;
                for (var i = 0; i < count && hits < 60; i++)
                {
                    var sad = module->GetStringArrayData(i);
                    if (sad == null || sad->Size <= 0 || sad->Size > 256 || sad->StringArray == null)
                        continue;
                    for (var s = 0; s < sad->Size && hits < 60; s++)
                    {
                        var text = ReadCString(sad->StringArray[s]);
                        if (!IsMinigameWord(text) && !LooksInteresting(text))
                            continue;
                        sb.AppendLine($"  str[{i}][{s}]=\"{text}\"");
                        hits++;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  (raw scan skipped: {ex.Message})");
        }

        return sb.ToString();
    }

    private static void DumpNamedAddon(StringBuilder sb, string name)
    {
        var a = Plugin.GameGui.GetAddonByName(name);
        sb.AppendLine($"-- {name} null={a.IsNull} visible={(!a.IsNull && a.IsVisible)} ready={(!a.IsNull && a.IsReady)} values={(!a.IsNull ? a.AtkValuesCount : 0)} --");
        if (a.IsNull)
            return;
        DumpAddonAtkValues(sb, a, "  ");
        foreach (var w in CollectFromAddon(a, includeInvisible: true))
            sb.AppendLine($"  word \"{w.Text}\"");
        foreach (var raw in ReadAllTextNodesRaw(a, 80))
            sb.AppendLine($"  rawtext \"{raw}\"");
    }

    private static void DumpAddonAtkValues(StringBuilder sb, AtkUnitBasePtr addon, string indent)
    {
        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || unit->AtkValues == null)
            return;
        var count = Math.Min((int)unit->AtkValuesCount, 32);
        for (var i = 0; i < count; i++)
        {
            var v = &unit->AtkValues[i];
            sb.Append($"{indent}Atk[{i}] {v->Type} ");
            try
            {
                sb.AppendLine(Quote(v->GetValueAsString()));
            }
            catch
            {
                sb.AppendLine("(err)");
            }
        }
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
            DumpAddonAtkValues(sb, addon, "  ");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  agent dump failed: {ex.Message}");
        }
    }

    private static List<string> ReadAllTextNodesRaw(AtkUnitBasePtr addon, int maxNodes)
    {
        var result = new List<string>();
        if (addon.IsNull)
            return result;
        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || unit->RootNode == null)
            return result;

        var visited = 0;
        void Walk(AtkResNode* parent)
        {
            if (parent == null)
                return;
            for (var node = parent->ChildNode; node != null; node = node->PrevSiblingNode)
            {
                if (visited++ >= maxNodes)
                    return;
                try
                {
                    if (node->Type == NodeType.Text)
                    {
                        var text = ((AtkTextNode*)node)->NodeText.ToString();
                        if (!string.IsNullOrWhiteSpace(text))
                            result.Add(text.Trim());
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
                                if (n == null || n->Type != NodeType.Text)
                                    continue;
                                var text = ((AtkTextNode*)n)->NodeText.ToString();
                                if (!string.IsNullOrWhiteSpace(text))
                                    result.Add(text.Trim());
                            }
                        }
                    }

                    Walk(node);
                }
                catch
                {
                    // ignore
                }
            }
        }

        Walk(unit->RootNode);
        return result.Distinct(StringComparer.Ordinal).Take(40).ToList();
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

    /// <summary>True for typeable Keybound Brawler phrases; rejects player/pet/HUD noise.</summary>
    private static bool IsMinigameWord(string? text)
    {
        if (!LooksInteresting(text))
            return false;
        text = text!.Trim();

        if (NoiseWords.Contains(text))
            return false;
        if (IsKnownObjectName(text))
            return false;

        // Abbreviated nameplates like "Thalia L." / "Sosha S."
        if (IsAbbreviatedPlayerName(text))
            return false;

        return true;
    }

    private static bool LooksInteresting(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        text = text.Trim();
        if (text.Length is < 2 or > 48)
            return false;
        if (text.All(char.IsDigit))
            return false;
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

        return letter >= 2;
    }

    private static bool IsAbbreviatedPlayerName(string text)
    {
        // "First L." pattern used on crowded nameplates.
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return false;
        return parts[1].Length == 2 && parts[1][1] == '.' && char.IsLetter(parts[1][0]);
    }

    private static bool IsKnownObjectName(string text)
    {
        try
        {
            foreach (var obj in Plugin.ObjectTable)
            {
                if (obj == null || string.IsNullOrEmpty(obj.Name.TextValue))
                    continue;
                var name = obj.Name.TextValue;
                if (name.Equals(text, StringComparison.OrdinalIgnoreCase))
                    return true;

                // Match abbreviated form of a known player.
                if (obj is IPlayerCharacter && IsAbbreviatedFormOf(name, text))
                    return true;
            }
        }
        catch
        {
            // ObjectTable can throw during zone transitions.
        }

        return false;
    }

    private static bool IsAbbreviatedFormOf(string fullName, string maybeAbbrev)
    {
        var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return false;
        var abbrev = $"{parts[0]} {char.ToUpperInvariant(parts[^1][0])}.";
        return abbrev.Equals(maybeAbbrev, StringComparison.OrdinalIgnoreCase);
    }

    private static List<WordEntry> FilterAndDedup(List<WordEntry> words)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<WordEntry>();
        foreach (var w in words)
        {
            if (!IsMinigameWord(w.Text))
                continue;
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

    private static string Quote(string? s) => s == null ? "(null)" : $"\"{s}\"";
}
