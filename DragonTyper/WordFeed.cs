using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;

namespace DragonTyper;

/// <summary>
/// Live word feed: KTGEWord slots + HUD arrays (kind/cast) + locale typed forms.
/// </summary>
internal sealed class WordFeed : IDisposable
{
    private readonly object gate = new();
    private readonly WordBook book = new();
    private List<KtgInput.WordEntry> live = [];
    private long liveAt;
    private string lastNote = "idle";

    public WordBook Book => book;

    public string LastNote
    {
        get { lock (gate) return lastNote; }
    }

    public void Enable(IAddonLifecycle lifecycle)
    {
        book.EnsureLoaded();
        lifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, "KTGInput", OnUpdate);
        lifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, "KTGEWord", OnUpdate);
        Plugin.Log.Information("WordFeed enabled");
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

    public bool IsSheetWord(string? text)
    {
        book.EnsureLoaded();
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var typed = book.ToTypedForm(text);
        return !string.IsNullOrWhiteSpace(typed);
    }

    private void OnUpdate(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (args is not AddonRequestedUpdateArgs)
                return;

            book.EnsureLoaded();
            var slots = KtgWordSlots.ReadAll(book);
            var byEntity = new Dictionary<ulong, KtgInput.WordEntry>();
            var byText = new Dictionary<string, KtgInput.WordEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var w in slots)
                Index(w, byEntity, byText);

            if (KtgHudArrays.TryRead(out var hud))
            {
                var japanese = Plugin.ClientState.ClientLanguage == ClientLanguage.Japanese;
                foreach (var h in hud)
                {
                    if (!h.Visible && h.Kind != 2)
                        continue;

                    var label = japanese
                        ? (!string.IsNullOrWhiteSpace(h.Reading) ? h.Reading : h.Name)
                        : (!string.IsNullOrWhiteSpace(h.Name) ? h.Name : h.Reading);
                    var typed = book.ToTypedForm(label);
                    if (string.IsNullOrWhiteSpace(typed) || !KtgInput.IsMinigameWordPublic(typed))
                        continue;

                    if (byEntity.TryGetValue(h.EntityId, out var existing)
                        || byText.TryGetValue(typed, out existing))
                    {
                        var merged = existing with
                        {
                            EntityId = existing.EntityId != 0 ? existing.EntityId : h.EntityId,
                            Kind = h.Kind != 0 ? h.Kind : existing.Kind,
                            CastProgress = Math.Max(existing.CastProgress, h.CastProgress),
                            ImportantCast = existing.ImportantCast || h.ImportantCast,
                            ScreenY = h.ScreenY != 0 ? h.ScreenY : existing.ScreenY,
                        };
                        Index(merged, byEntity, byText);
                    }
                    else
                    {
                        Index(
                            new KtgInput.WordEntry(
                                h.EntityId, typed, h.Kind, h.CastProgress, h.ImportantCast, h.ScreenY),
                            byEntity,
                            byText);
                    }
                }
            }

            var found = byText.Values.ToList();
            if (found.Count == 0)
                return;

            lock (gate)
            {
                live = found;
                liveAt = Environment.TickCount64;
                lastNote = $"feed={found.Count} via {args.AddonName}";
            }
        }
        catch (Exception ex)
        {
            PluginLogHelpers.WarnOnce("wordfeed-update", 5000, "WordFeed update failed: {Message}", ex.Message);
        }
    }

    private static void Index(
        KtgInput.WordEntry w,
        Dictionary<ulong, KtgInput.WordEntry> byEntity,
        Dictionary<string, KtgInput.WordEntry> byText)
    {
        byText[w.Text] = w;
        if (w.EntityId != 0)
            byEntity[w.EntityId] = w;
    }
}
