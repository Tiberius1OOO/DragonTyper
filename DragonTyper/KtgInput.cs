using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.NativeWrapper;
using FFXIVClientStructs.FFXIV.Component.GUI;
using CsAtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace DragonTyper;

/// <summary>
/// Keybound Brawler word reader.
/// Primary source: KTGEWord fixed slots via WordFeed / KtgWordSlots.
/// Do not read KTGInput TextInput as a word source — that is our SetText echo.
/// </summary>
internal static unsafe class KtgInput
{
    public const string AddonName = "KTGInput";
    public const string WordAddonName = "KTGEWord";
    /// <param name="Kind">HUD type; 2 = barrier/defense phrase.</param>
    public readonly record struct WordEntry(
        ulong EntityId,
        string Text,
        int Kind = 0,
        int CastProgress = 0,
        bool ImportantCast = false,
        int ScreenY = 0)
    {
        public bool IsBarrier => Kind == 2;
    }

    private static readonly object Gate = new();
    private static List<WordEntry> cachedWords = [];
    private static long lastCacheAt;
    private static string lastSource = "none";

    private static readonly HashSet<string> NoiseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "MP", "CP", "GP", "TP", "EP", "HP", "Limit Break", "Fair Skies",
        "Playing Recorded Duty", "Recording Duty", "SCORE",
        "TYPING FEVER!", "TYPING FEVER", "Duty Information", "In Duty", "Viewing Cutscene",
        "The Gold Saucer", "Immortal Flames", "Order of the Twin Adder", "Maelstrom",
        "Defeat the airbuster", "The reinforcements fade...",
        "Interrupted!", "Interrupted", "BONUS",
        "Enter", "Delete Text/Quit",
        "Ability", "Range", "Radius", "Recast", "Cast", "Instant",
        "BRD", "PLD", "WAR", "DRK", "GNB", "WHM", "SCH", "AST", "SGE",
        "MNK", "DRG", "NIN", "SAM", "RPR", "VPR", "MCH", "DNC", "BLM", "SMN", "RDM", "PCT",
        "gil", "Tankbuster",
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
        // 0) Live balloon feed.
        try
        {
            var feed = Plugin.Words?.GetLiveWords() ?? [];
            if (feed.Count > 0)
            {
                StoreCache(feed, $"WordFeed:{Plugin.Words?.LastNote}");
                return feed;
            }
        }
        catch (Exception ex)
        {
            PluginLogHelpers.WarnOnce("wordfeed-read", 5000, "WordFeed read failed: {Message}", ex.Message);
        }

        // 1) Direct slot read (same path as WordFeed).
        try
        {
            var slots = KtgWordSlots.ReadAll();
            if (slots.Count > 0)
            {
                StoreCache(slots, "KTGEWord:slots");
                return slots;
            }
        }
        catch (Exception ex)
        {
            PluginLogHelpers.WarnOnce("slots-read", 5000, "Slot read failed: {Message}", ex.Message);
        }

        // 2) Legacy deep walk fallback.
        var fromAll = ReadAllKtgEWordInstances();
        if (fromAll.Count > 0)
            return fromAll;

        lock (Gate)
        {
            if (cachedWords.Count > 0 && Environment.TickCount64 - lastCacheAt < 800)
                return cachedWords.ToList();
        }

        _ = inputAddon;
        return [];
    }

    public static bool IsMinigameWordPublic(string? text) => IsMinigameWord(text);

    private static List<WordEntry> ReadAllKtgEWordInstances()
    {
        var words = new List<WordEntry>();
        try
        {
            for (var i = 1; i <= 32; i++)
            {
                var addon = Plugin.GameGui.GetAddonByName(WordAddonName, i);
                if (addon.IsNull)
                {
                    if (i > 4)
                        break;
                    continue;
                }

                if (!addon.IsVisible)
                    continue;

                ulong entity = 0;
                try
                {
                    var unit = (AtkUnitBase*)addon.Address;
                    if (unit->AtkValues != null && unit->AtkValuesCount > 1
                        && unit->AtkValues[1].Type == CsAtkValueType.UInt)
                        entity = unit->AtkValues[1].UInt;
                }
                catch
                {
                    // ignore
                }

                foreach (var w in CollectFromAddon(addon, includeInvisible: true))
                    words.Add(new WordEntry(entity != 0 ? entity : w.EntityId, w.Text));
            }

            words = FilterAndDedup(words);
            if (words.Count > 0)
                StoreCache(words, "KTGEWord[all]");
        }
        catch (Exception ex)
        {
            PluginLogHelpers.WarnOnce("ktgeword-multi", 5000, "KTGEWord multi-read failed: {Message}", ex.Message);
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
                KtgIdleKeepalive.Pulse(unit);
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
            KtgIdleKeepalive.Pulse(unit);
            Plugin.Log.Information("Submit via FireCallback SubmitWord for '{Word}'", word);
            return true;
        }

        // 3) Set AtkValues[1] (input buffer; often a fullwidth space) then callback.
        try
        {
            if (unit->AtkValues != null && unit->AtkValuesCount > 1)
            {
                unit->AtkValues[1].SetManagedString(word);
                if (TryFire(unit, close: false, "SubmitWord", word)
                    || TryFire(unit, close: false, 0)
                    || TryFire(unit, close: false, word))
                {
                    KtgIdleKeepalive.Pulse(unit);
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
        {
            KtgIdleKeepalive.Pulse(unit);
            return true;
        }

        if (TryFire(unit, close: false, 0, word))
        {
            KtgIdleKeepalive.Pulse(unit);
            return true;
        }

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

    /// <summary>True for typeable Keybound Brawler phrases; rejects player/HUD noise only.</summary>
    private static bool IsMinigameWord(string? text)
    {
        text = KtgWordSlots.DecodeSeString(text);
        if (!LooksInteresting(text))
            return false;

        if (NoiseWords.Contains(text))
            return false;

        // Only reject player names — enemy names often ARE the typeable word.
        if (IsKnownPlayerName(text))
            return false;
        if (IsAbbreviatedPlayerName(text))
            return false;

        return true;
    }

    private static bool LooksInteresting(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        text = text.Trim();
        if (text.Length is < 2 or > 80)
            return false;
        if (text.All(char.IsDigit))
            return false;

        var letter = 0;
        foreach (var c in text)
        {
            if (char.IsLetter(c) || IsCjk(c))
                letter++;
            else if (!(char.IsWhiteSpace(c) || char.IsDigit(c)
                       || c is '.' or ',' or '\'' or '-' or '!' or '?' or ':' or ';' or '"' or '/'
                       or '！' or '？' or '、' or '。' or '「' or '」' or '・' or '～' or '〜'))
                return false;
        }

        return letter >= 2;
    }

    private static bool IsCjk(char c) =>
        c is (>= '\u3040' and <= '\u30FF')
            or (>= '\u3400' and <= '\u9FFF')
            or (>= '\uFF66' and <= '\uFF9D');

    private static bool IsAbbreviatedPlayerName(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return false;
        return parts[1].Length == 2 && parts[1][1] == '.' && char.IsLetter(parts[1][0]);
    }

    private static bool IsKnownPlayerName(string text)
    {
        try
        {
            foreach (var obj in Plugin.ObjectTable)
            {
                if (obj is not IPlayerCharacter player)
                    continue;
                var name = player.Name.TextValue;
                if (string.IsNullOrEmpty(name))
                    continue;
                if (name.Equals(text, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (IsAbbreviatedFormOf(name, text))
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
