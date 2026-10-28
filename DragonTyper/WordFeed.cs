using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace DragonTyper;

/// <summary>
/// Live word feed.
/// 1) Visible KTGEWord text nodes (normal balloons).
/// 2) If a balloon has entity but no text (boss/interrupt): pull only
///    KTGTypeWordTextData sheet members from compact string arrays / number pairs.
/// Never accepts hotbar chrome (Ability/Range/…) — those are not in the sheet.
/// </summary>
internal sealed unsafe class WordFeed : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<uint, string> sheetWords = new();
    private readonly HashSet<string> sheetWordSet = new(StringComparer.OrdinalIgnoreCase);
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

    public bool IsSheetWord(string? text)
    {
        LoadSheet();
        return !string.IsNullOrWhiteSpace(text) && sheetWordSet.Contains(text.Trim());
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
                text = text.Trim();
                if (text.Length is < 2 or > 48)
                    continue;
                sheetWords[row.RowId] = text;
                sheetWordSet.Add(text);
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

            var balloons = ReadBalloons();
            var found = new List<KtgInput.WordEntry>();

            foreach (var b in balloons.Where(b => b.Texts.Count > 0))
            {
                foreach (var text in b.Texts)
                {
                    // Balloon text is authoritative even if not in sheet (safety).
                    found.Add(new KtgInput.WordEntry(b.EntityId, text));
                }
            }

            var emptyEntities = balloons
                .Where(b => b.Texts.Count == 0 && b.EntityId != 0 && IsEnemyEntity(b.EntityId))
                .Select(b => b.EntityId)
                .ToHashSet();

            // Boss/interrupt path: no Text on balloon, but phrase is in sheet-backed arrays.
            if (emptyEntities.Count > 0)
            {
                var sheetHits = new List<KtgInput.WordEntry>();
                CollectSheetWordsFromStringArrays(sheetHits);
                CollectSheetWordsForEntities(sheetHits, emptyEntities);

                // Attach empty balloon entity when only one empty balloon (typical boss).
                var entity = emptyEntities.Count == 1 ? emptyEntities.First() : 0UL;
                foreach (var hit in sheetHits)
                {
                    var id = hit.EntityId != 0 ? hit.EntityId : entity;
                    found.Add(new KtgInput.WordEntry(id, hit.Text));
                }
            }

            found = Dedup(found);
            if (found.Count == 0)
                return;

            // Prefer real balloon text / sheet words; drop anything not on sheet unless it came from a balloon node.
            var balloonTexts = new HashSet<string>(
                balloons.SelectMany(b => b.Texts),
                StringComparer.OrdinalIgnoreCase);
            found = found
                .Where(w => balloonTexts.Contains(w.Text) || sheetWordSet.Contains(w.Text))
                .ToList();
            found = Dedup(found);
            if (found.Count == 0)
                return;

            lock (gate)
            {
                live = found;
                liveAt = Environment.TickCount64;
                lastNote =
                    $"feed={found.Count} balloons={balloons.Count} empty={emptyEntities.Count} via {args.AddonName}";
            }
        }
        catch (Exception ex)
        {
            PluginLogHelpers.WarnOnce("wordfeed-update", 5000, "WordFeed update failed: {Message}", ex.Message);
        }
    }

    private readonly record struct Balloon(ulong EntityId, List<string> Texts);

    private static List<Balloon> ReadBalloons()
    {
        var list = new List<Balloon>();
        for (var i = 1; i <= 32; i++)
        {
            try
            {
                var addon = Plugin.GameGui.GetAddonByName(KtgInput.WordAddonName, i);
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
                        && unit->AtkValues[1].Type == AtkValueType.UInt)
                        entity = unit->AtkValues[1].UInt;
                }
                catch
                {
                    // ignore
                }

                var texts = KtgInput.CollectWordsFromAddonPublic(addon)
                    .Select(w => w.Text)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                list.Add(new Balloon(entity, texts));
            }
            catch
            {
                // ignore one index
            }
        }

        return list;
    }

    private void CollectSheetWordsFromStringArrays(List<KtgInput.WordEntry> into)
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

            // Hotbar/tooltip arrays are large; live KTG word lists seen in captures are compact.
            if (sad == null || sad->StringArray == null || sad->Size is <= 0 or > 48)
                continue;

            for (var s = 0; s < sad->Size; s++)
            {
                try
                {
                    var ptr = sad->StringArray[s];
                    if (!ptr.HasValue)
                        continue;
                    var text = ptr.ToString()?.Trim();
                    if (string.IsNullOrEmpty(text))
                        continue;
                    if (!sheetWordSet.Contains(text))
                        continue;
                    if (!KtgInput.IsMinigameWordPublic(text))
                        continue;
                    into.Add(new KtgInput.WordEntry(0, text));
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    private void CollectSheetWordsForEntities(List<KtgInput.WordEntry> into, HashSet<ulong> entities)
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

            if (nad == null || nad->IntArray == null || nad->Size <= 0)
                continue;

            var size = Math.Min(nad->Size, 64);
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

                TryEntityWordPair(into, entities, a, b);
                TryEntityWordPair(into, entities, b, a);
            }
        }
    }

    private void TryEntityWordPair(List<KtgInput.WordEntry> into, HashSet<ulong> entities, int maybeEntity, int maybeWord)
    {
        var entity = unchecked((uint)maybeEntity);
        if (!entities.Contains(entity))
            return;
        if (maybeWord <= 0)
            return;
        if (!sheetWords.TryGetValue((uint)maybeWord, out var text))
            return;
        if (!KtgInput.IsMinigameWordPublic(text))
            return;
        into.Add(new KtgInput.WordEntry(entity, text));
    }

    private static bool IsEnemyEntity(ulong entityId)
    {
        if (entityId == 0)
            return false;
        try
        {
            foreach (var obj in Plugin.ObjectTable)
            {
                if (obj == null)
                    continue;
                if (obj.EntityId != entityId && obj.GameObjectId != entityId)
                    continue;
                if (obj is IPlayerCharacter)
                    return false;
                return obj is IBattleChara;
            }
        }
        catch
        {
            // zone transition
        }

        return entityId is >= 0x40000000 and <= 0x4FFFFFFF;
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
