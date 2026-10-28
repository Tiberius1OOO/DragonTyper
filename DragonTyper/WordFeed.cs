using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace DragonTyper;

/// <summary>
/// Live word feed approximating Minion's KTGInput:GetData().
/// Boss/interrupt balloons often have no readable Text nodes; they show up as
/// entityId + wordRowId pairs in NumberArrays that we resolve via KTGTypeWordTextData.
/// </summary>
internal sealed unsafe class WordFeed : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<uint, string> sheetWords = new();
    private List<KtgInput.WordEntry> live = [];
    private long liveAt;
    private bool sheetLoaded;
    private string lastNote = "idle";

    public string LastNote
    {
        get { lock (gate) return lastNote; }
    }

    public void Enable(IAddonLifecycle lifecycle)
    {
        LoadSheet();
        lifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, "KTGInput", OnUpdate);
        lifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, "KTGEWord", OnUpdate);
        Plugin.Log.Information("WordFeed enabled (sheet words={Count})", sheetWords.Count);
    }

    public void Disable(IAddonLifecycle lifecycle)
    {
        lifecycle.UnregisterListener(AddonEvent.PreRequestedUpdate, "KTGInput", OnUpdate);
        lifecycle.UnregisterListener(AddonEvent.PreRequestedUpdate, "KTGEWord", OnUpdate);
    }

    public void Dispose()
    {
        // Plugin dispose unregisters via Disable.
    }

    public List<KtgInput.WordEntry> GetLiveWords()
    {
        lock (gate)
        {
            if (live.Count == 0)
                return [];
            if (Environment.TickCount64 - liveAt > 1500)
                return [];
            return live.ToList();
        }
    }

    public IReadOnlyDictionary<uint, string> SheetWords
    {
        get
        {
            LoadSheet();
            return sheetWords;
        }
    }

    public string? ResolveWordId(uint id)
    {
        LoadSheet();
        return sheetWords.TryGetValue(id, out var t) ? t : null;
    }

    private void LoadSheet()
    {
        if (sheetLoaded)
            return;
        sheetLoaded = true;
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<KTGTypeWordTextData>();
            if (sheet == null)
            {
                Plugin.Log.Warning("KTGTypeWordTextData sheet missing");
                return;
            }

            foreach (var row in sheet)
            {
                var text = row.Unknown0.ToString();
                if (string.IsNullOrWhiteSpace(text))
                    text = row.Unknown1.ToString();
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                // Strip SeString noise if any control chars slipped through.
                text = text.Trim();
                if (text.Length is < 2 or > 48)
                    continue;
                sheetWords[row.RowId] = text;
            }

            Plugin.Log.Information("Loaded {Count} Keybound word texts from sheet", sheetWords.Count);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed loading KTGTypeWordTextData");
        }
    }

    private void OnUpdate(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (args is not AddonRequestedUpdateArgs)
                return;

            LoadSheet();
            if (sheetWords.Count == 0)
                return;

            var found = new List<KtgInput.WordEntry>();

            // 1) Multi-instance KTGEWord text nodes (normal balloons).
            CollectAllKtgEWordText(found);

            // 2) NumberArray entityId ↔ wordRowId pairs (boss / interrupt path).
            CollectFromNumberArrays(found);

            // 3) String arrays: keep as supplement when plain text exists.
            CollectFromStringArrays(found);

            found = Dedup(found);
            if (found.Count == 0)
                return;

            lock (gate)
            {
                live = found;
                liveAt = Environment.TickCount64;
                lastNote = $"feed={found.Count} via update/{args.AddonName}";
            }
        }
        catch (Exception ex)
        {
            PluginLogHelpers.WarnOnce("wordfeed-update", 5000, "WordFeed update failed: {Message}", ex.Message);
        }
    }

    private static void CollectAllKtgEWordText(List<KtgInput.WordEntry> into)
    {
        for (var i = 1; i <= 32; i++)
        {
            try
            {
                var addon = Plugin.GameGui.GetAddonByName(KtgInput.WordAddonName, i);
                if (addon.IsNull || !addon.IsVisible)
                {
                    if (i > 4 && addon.IsNull)
                        break;
                    continue;
                }

                ulong entity = 0;
                try
                {
                    var unit = (AtkUnitBase*)addon.Address;
                    if (unit->AtkValues != null && unit->AtkValuesCount > 1
                        && unit->AtkValues[1].Type == AtkValueType.UInt)
                        entity = unit->AtkValues[1].UInt;
                }
                catch
                {
                    // ignore
                }

                foreach (var w in KtgInput.CollectWordsFromAddonPublic(addon))
                    into.Add(new KtgInput.WordEntry(entity != 0 ? entity : w.EntityId, w.Text));
            }
            catch
            {
                // ignore one index
            }
        }
    }

    private void CollectFromNumberArrays(List<KtgInput.WordEntry> into)
    {
        var module = RaptureAtkModule.Instance();
        if (module == null)
            return;

        var holder = module->AtkArrayDataHolder;
        var count = Math.Min((int)holder.NumberArrayCount, 200);
        for (var i = 0; i < count; i++)
        {
            NumberArrayData* nad;
            try
            {
                nad = module->GetNumberArrayData(i);
            }
            catch
            {
                continue;
            }

            if (nad == null || nad->IntArray == null || nad->Size <= 0 || nad->Size > 256)
                continue;

            var size = Math.Min(nad->Size, 128);
            // Prefer compact arrays — huge HUD number arrays cause false sheet hits.
            if (size > 96)
                continue;

            for (var s = 0; s + 1 < size; s++)
            {
                int a;
                int b;
                try
                {
                    a = nad->IntArray[s];
                    b = nad->IntArray[s + 1];
                }
                catch
                {
                    break;
                }

                TryPair(into, a, b);
                TryPair(into, b, a);
            }
        }
    }

    private void TryPair(List<KtgInput.WordEntry> into, int maybeEntity, int maybeWord)
    {
        if (!LooksLikeEntityId(maybeEntity))
            return;
        if (maybeWord <= 0)
            return;
        if (!sheetWords.TryGetValue((uint)maybeWord, out var text))
            return;
        into.Add(new KtgInput.WordEntry(unchecked((uint)maybeEntity), text));
    }

    private static bool LooksLikeEntityId(int value)
    {
        // Battle/object entity ids used in captures: ~1073879xxx (0x4000xxxx).
        var u = unchecked((uint)value);
        return u is >= 0x40000000 and <= 0x4FFFFFFF;
    }

    private void CollectFromStringArrays(List<KtgInput.WordEntry> into)
    {
        var module = RaptureAtkModule.Instance();
        if (module == null)
            return;

        var holder = module->AtkArrayDataHolder;
        var count = Math.Min((int)holder.StringArrayCount, 200);
        for (var i = 0; i < count; i++)
        {
            StringArrayData* sad;
            try
            {
                sad = module->GetStringArrayData(i);
            }
            catch
            {
                continue;
            }

            if (sad == null || sad->StringArray == null || sad->Size <= 0 || sad->Size > 64)
                continue;

            for (var s = 0; s < sad->Size; s++)
            {
                try
                {
                    var ptr = sad->StringArray[s];
                    if (!ptr.HasValue)
                        continue;
                    var text = ptr.ToString();
                    if (KtgInput.IsMinigameWordPublic(text))
                        into.Add(new KtgInput.WordEntry(0, text!.Trim()));
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    private static List<KtgInput.WordEntry> Dedup(List<KtgInput.WordEntry> words)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<KtgInput.WordEntry>();
        foreach (var w in words)
        {
            if (string.IsNullOrWhiteSpace(w.Text))
                continue;
            if (!KtgInput.IsMinigameWordPublic(w.Text))
                continue;
            if (seen.Add(w.Text))
                result.Add(w);
        }

        return result;
    }
}
