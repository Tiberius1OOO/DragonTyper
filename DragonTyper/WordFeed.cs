using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace DragonTyper;

/// <summary>
/// Live word feed from on-screen KTGEWord balloon text only.
/// KTGEWord is one pooled addon: Atk[1] is a shared "last updated" entity hint,
/// not a per-word owner — never gate typing on that id alone.
/// Do not scrape UI string/number arrays (invented Shinra / Price of Progress).
/// Do not read KTGInput TextInput (that is our own SetText echo after submit).
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

            var balloons = ReadBalloons();
            var found = new List<KtgInput.WordEntry>();

            foreach (var b in balloons.Where(b => b.Texts.Count > 0))
            {
                foreach (var text in b.Texts)
                {
                    // EntityId is only a soft hint (shared Atk[1] on the pooled addon).
                    found.Add(new KtgInput.WordEntry(b.EntityId, text));
                }
            }

            found = Dedup(found);
            if (found.Count == 0)
                return;

            lock (gate)
            {
                live = found;
                liveAt = Environment.TickCount64;
                lastNote = $"feed={found.Count} balloons={balloons.Count} via {args.AddonName}";
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
                    var unit = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addon.Address;
                    if (unit->AtkValues != null && unit->AtkValuesCount > 1
                        && unit->AtkValues[1].Type == FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.UInt)
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
