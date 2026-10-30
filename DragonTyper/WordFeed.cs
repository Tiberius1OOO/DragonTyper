using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace DragonTyper;

/// <summary>
/// Live word feed from KTGEWord's 20 fixed slots (name/title text + per-slot entity).
/// </summary>
internal sealed class WordFeed : IDisposable
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
                var text = KtgWordSlots.DecodeSeString(row.Unknown0.ToString());
                if (string.IsNullOrWhiteSpace(text))
                    text = KtgWordSlots.DecodeSeString(row.Unknown1.ToString());
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                if (text.Length is < 2 or > 80)
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

            var found = KtgWordSlots.ReadAll();
            if (found.Count == 0)
                return;

            lock (gate)
            {
                live = found;
                liveAt = Environment.TickCount64;
                lastNote = $"feed={found.Count} slots via {args.AddonName}";
            }
        }
        catch (Exception ex)
        {
            PluginLogHelpers.WarnOnce("wordfeed-update", 5000, "WordFeed update failed: {Message}", ex.Message);
        }
    }
}
