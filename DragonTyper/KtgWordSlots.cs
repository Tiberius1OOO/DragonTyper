using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DragonTyper;

/// <summary>
/// Reads KTGEWord's 20 fixed word slots (component nodes 20000+i).
/// Name = text node 20, title/reading = text node 22, per-slot EntityID.
/// Remembers words briefly while a slot is obscured / label flash-hidden.
/// </summary>
internal static unsafe class KtgWordSlots
{
    private const int WordEntriesOffset = 568;
    private const int WordEntrySize = 200;
    private const int SlotCount = 20;
    private const int InvalidEntityId = unchecked((int)0xE0000000);
    private const int MemoryTtlMs = 3500;

    private static readonly object Gate = new();
    private static readonly Dictionary<int, SlotMemory> Memory = new();

    private readonly record struct SlotMemory(string Text, ulong EntityId, long SeenAt);

    [StructLayout(LayoutKind.Explicit, Size = 200)]
    private struct SlotLayout
    {
        [FieldOffset(0)] public AtkComponentNode* ComponentNode;
        [FieldOffset(112)] public AtkTextNode* NameTextNode;
        [FieldOffset(128)] public AtkTextNode* TitleTextNode;
        [FieldOffset(168)] public int EntityId;
        [FieldOffset(180)] public byte Obstructed;
    }

    public static List<KtgInput.WordEntry> ReadAll()
    {
        var words = new List<KtgInput.WordEntry>();
        var seenText = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var now = Environment.TickCount64;
        var aliveSlots = new HashSet<int>();

        try
        {
            var addon = Plugin.GameGui.GetAddonByName(KtgInput.WordAddonName);
            if (addon.IsNull || !addon.IsVisible || !addon.IsReady)
            {
                lock (Gate) Memory.Clear();
                return words;
            }

            var unit = (AtkUnitBase*)addon.Address;
            if (unit == null)
                return words;

            var fromTable = TryReadFromSlotTable(unit, now, aliveSlots, seenText, words);
            if (!fromTable)
                ReadFromNodeList(unit, now, aliveSlots, seenText, words);

            lock (Gate)
            {
                foreach (var key in Memory.Keys.ToList())
                {
                    if (!aliveSlots.Contains(key) || now - Memory[key].SeenAt > MemoryTtlMs)
                        Memory.Remove(key);
                }
            }
        }
        catch (Exception ex)
        {
            PluginLogHelpers.WarnOnce("ktg-slots", 5000, "KTGEWord slot read failed: {Message}", ex.Message);
        }

        return words;
    }

    private static bool TryReadFromSlotTable(
        AtkUnitBase* unit,
        long now,
        HashSet<int> aliveSlots,
        HashSet<string> seenText,
        List<KtgInput.WordEntry> words)
    {
        try
        {
            if (unit->UldManager.NodeListCount < 1)
                return false;

            var basePtr = (byte*)unit + WordEntriesOffset;
            var anyComponent = false;

            for (var slot = 0; slot < SlotCount; slot++)
            {
                var entry = (SlotLayout*)(basePtr + slot * WordEntrySize);
                var comp = entry->ComponentNode;
                if (comp == null)
                    continue;
                anyComponent = true;

                if (!IsNodeChainVisible((AtkResNode*)comp))
                    continue;

                aliveSlots.Add(slot);
                var entity = NormalizeEntity(entry->EntityId);
                var obstructed = entry->Obstructed != 0;
                var name = DecodeTextNode(entry->NameTextNode);
                var title = DecodeTextNode(entry->TitleTextNode);
                var nameVisible = entry->NameTextNode != null && IsNodeChainVisible((AtkResNode*)entry->NameTextNode);
                var titleVisible = entry->TitleTextNode != null && IsNodeChainVisible((AtkResNode*)entry->TitleTextNode);
                var noise = comp->Component != null && HasNoiseOverlay(&comp->Component->UldManager);

                AddSlotCandidate(
                    slot, entity, name, title, nameVisible, titleVisible, obstructed || noise, now, seenText, words);
            }

            return anyComponent;
        }
        catch
        {
            return false;
        }
    }

    private static void ReadFromNodeList(
        AtkUnitBase* unit,
        long now,
        HashSet<int> aliveSlots,
        HashSet<string> seenText,
        List<KtgInput.WordEntry> words)
    {
        var mgr = &unit->UldManager;
        if (mgr->NodeList == null)
            return;

        var count = Math.Min((int)mgr->NodeListCount, 4096);
        for (var i = 0; i < count; i++)
        {
            var node = mgr->NodeList[i];
            if (node == null || (int)node->Type < 1000)
                continue;
            if (node->NodeId is < 20000 or >= 20020)
                continue;
            if (!IsNodeChainVisible(node))
                continue;

            var slot = (int)(node->NodeId - 20000);
            aliveSlots.Add(slot);

            var comp = ((AtkComponentNode*)node)->Component;
            if (comp == null)
                continue;

            var nameNode = FindTextNode(&comp->UldManager, 20);
            var titleNode = FindTextNode(&comp->UldManager, 22);
            var name = DecodeTextNode(nameNode);
            var title = DecodeTextNode(titleNode);
            var nameVisible = nameNode != null && IsNodeChainVisible((AtkResNode*)nameNode);
            var titleVisible = titleNode != null && IsNodeChainVisible((AtkResNode*)titleNode);
            var noise = HasNoiseOverlay(&comp->UldManager);

            var entity = 0UL;
            try
            {
                var entry = (SlotLayout*)((byte*)unit + WordEntriesOffset + slot * WordEntrySize);
                entity = NormalizeEntity(entry->EntityId);
            }
            catch
            {
                // ignore
            }

            AddSlotCandidate(slot, entity, name, title, nameVisible, titleVisible, noise, now, seenText, words);
        }
    }

    private static void AddSlotCandidate(
        int slot,
        ulong entity,
        string name,
        string title,
        bool nameVisible,
        bool titleVisible,
        bool obscured,
        long now,
        HashSet<string> seenText,
        List<KtgInput.WordEntry> words)
    {
        var live = PickTypeable(name, title, nameVisible, titleVisible);
        if (live != null)
        {
            lock (Gate)
                Memory[slot] = new SlotMemory(live, entity, now);
            TryAdd(words, seenText, entity, live);
            return;
        }

        if (!obscured && nameVisible)
            return;

        SlotMemory mem;
        lock (Gate)
        {
            if (!Memory.TryGetValue(slot, out mem))
                return;
            if (now - mem.SeenAt > MemoryTtlMs)
            {
                Memory.Remove(slot);
                return;
            }
        }

        var id = entity != 0 ? entity : mem.EntityId;
        TryAdd(words, seenText, id, mem.Text);
    }

    private static string? PickTypeable(string name, string title, bool nameVisible, bool titleVisible)
    {
        if (nameVisible && KtgInput.IsMinigameWordPublic(name))
            return name.Trim();
        if (titleVisible && KtgInput.IsMinigameWordPublic(title))
            return title.Trim();
        if (!string.IsNullOrWhiteSpace(name) && KtgInput.IsMinigameWordPublic(name))
            return name.Trim();
        if (!string.IsNullOrWhiteSpace(title) && KtgInput.IsMinigameWordPublic(title))
            return title.Trim();
        return null;
    }

    private static void TryAdd(List<KtgInput.WordEntry> words, HashSet<string> seen, ulong entity, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        text = text.Trim();
        if (!KtgInput.IsMinigameWordPublic(text))
            return;
        if (!seen.Add(text))
            return;
        words.Add(new KtgInput.WordEntry(entity, text));
    }

    private static ulong NormalizeEntity(int entityId)
    {
        if (entityId == 0 || entityId == InvalidEntityId)
            return 0;
        return unchecked((uint)entityId);
    }

    private static string DecodeTextNode(AtkTextNode* node)
    {
        if (node == null)
            return string.Empty;
        try
        {
            return DecodeSeString(node->NodeText.ToString());
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Strip SeString payloads / newlines into a single typeable line.</summary>
    internal static string DecodeSeString(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        var sb = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c == '\u0002')
            {
                while (i + 1 < raw.Length && raw[i + 1] != '\u0003')
                    i++;
                if (i + 1 < raw.Length && raw[i + 1] == '\u0003')
                    i++;
                sb.Append(' ');
                continue;
            }

            if (c == '\u0003')
                continue;
            if (c is '\r' or '\n' or '\t')
            {
                sb.Append(' ');
                continue;
            }

            sb.Append(c);
        }

        var collapsed = sb.ToString();
        var outSb = new StringBuilder(collapsed.Length);
        var space = false;
        foreach (var c in collapsed)
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space && outSb.Length > 0)
                outSb.Append(' ');
            space = false;
            outSb.Append(c);
        }

        return outSb.ToString().Trim();
    }

    private static bool HasNoiseOverlay(AtkUldManager* mgr)
    {
        for (uint id = 17; id <= 18; id++)
        {
            var n = FindNode(mgr, id);
            if (n != null && n->Type == NodeType.Image && IsNodeChainVisible(n))
                return true;
        }

        return false;
    }

    private static AtkTextNode* FindTextNode(AtkUldManager* mgr, uint id)
    {
        var n = FindNode(mgr, id);
        if (n == null || n->Type != NodeType.Text)
            return null;
        return (AtkTextNode*)n;
    }

    private static AtkResNode* FindNode(AtkUldManager* mgr, uint id)
    {
        if (mgr == null || mgr->NodeList == null)
            return null;
        var count = Math.Min((int)mgr->NodeListCount, 4096);
        for (var i = 0; i < count; i++)
        {
            var n = mgr->NodeList[i];
            if (n != null && n->NodeId == id)
                return n;
        }

        return null;
    }

    private static bool IsNodeChainVisible(AtkResNode* node)
    {
        for (var depth = 0; node != null && depth < 32; depth++, node = node->ParentNode)
        {
            try
            {
                if (!node->IsVisible())
                    return false;
            }
            catch
            {
                return false;
            }
        }

        return true;
    }
}
