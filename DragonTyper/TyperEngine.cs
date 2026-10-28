using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;

namespace DragonTyper;

/// <summary>
/// Picks and submits Keybound Brawler words while Active is on and the input window accepts typing.
/// </summary>
public sealed class TyperEngine(Plugin plugin)
{
    private const int CheckEveryMs = 100;
    private const float Jitter = 0.20f;
    private const int RecentForMs = 3000;
    private const int GuardMs = 500;
    private const uint BigHp = 1000;
    private const int LongWordChars = 28;
    private const int LongMaxMs = 5000;
    private const int WarnCooldownMs = 5000;

    private static readonly HashSet<uint> LetterlockCasts = [49529, 49532, 49702];

    private readonly Dictionary<string, long> recent = new(StringComparer.Ordinal);
    private readonly Random random = new();

    private long nextCheckAt;
    private long? waitFrom;
    private float waitRoll;
    private long? holdUntil;
    private string status = "Idle";
    private bool sawAddon;
    private bool loggedSessionStart;

    public string Status => status;
    public string? LastError { get; private set; }
    public int Hits { get; private set; }
    public int Misses { get; private set; }

    public void ResetIdle()
    {
        waitFrom = null;
        holdUntil = null;
        nextCheckAt = 0;
        recent.Clear();
        sawAddon = false;
        loggedSessionStart = false;
        LastError = null;
        status = plugin.Configuration.Active ? "Waiting for Keybound Brawler input" : "Inactive";
    }

    public void DumpAddon()
    {
        try
        {
            var addon = KtgInput.GetAddon();
            if (addon.IsNull)
            {
                Plugin.Log.Warning("Dump requested but KTGInput is not open/ready/visible.");
                LastError = "Dump failed: KTGInput not open";
                return;
            }

            var dump = KtgInput.Dump(addon);
            Plugin.Log.Information("=== KTGInput dump begin ===");
            foreach (var line in dump.Split('\n'))
            {
                if (!string.IsNullOrWhiteSpace(line))
                    Plugin.Log.Information("[dump] {Line}", line.TrimEnd());
            }

            Plugin.Log.Information("=== KTGInput dump end (AtkValuesCount={Count}) ===", addon.AtkValuesCount);
        }
        catch (Exception ex)
        {
            LastError = $"Dump failed: {ex.Message}";
            PluginLogHelpers.Error(ex, "DumpAddon crashed");
        }
    }

    public void Tick()
    {
        var now = Environment.TickCount64;
        if (now < nextCheckAt)
            return;
        nextCheckAt = now + CheckEveryMs;

        try
        {
            TickInner(now);
        }
        catch (Exception ex)
        {
            Misses++;
            LastError = ex.Message;
            status = $"Error: {ex.Message}";
            PluginLogHelpers.ErrorOnce("tick-crash", WarnCooldownMs, ex, "Typer tick failed");
        }
    }

    private void TickInner(long now)
    {
        if (!plugin.Configuration.Active)
        {
            waitFrom = null;
            status = "Inactive";
            return;
        }

        if (!loggedSessionStart)
        {
            loggedSessionStart = true;
            Plugin.Log.Information(
                "Active session started. Delay={Delay}ms LongSentenceDelay={Long}",
                plugin.Configuration.WordDelayMs,
                plugin.Configuration.LongSentenceDelay);
        }

        var addon = KtgInput.GetAddon();
        if (addon.IsNull)
        {
            if (sawAddon)
            {
                Plugin.Log.Information("KTGInput closed. Returning to wait state.");
                sawAddon = false;
            }

            waitFrom = null;
            status = "Waiting for Keybound Brawler input";
            return;
        }

        if (!sawAddon)
        {
            sawAddon = true;
            Plugin.Log.Information(
                "KTGInput opened. AtkValuesCount={Count} Ready={Ready} Visible={Visible}",
                addon.AtkValuesCount,
                addon.IsReady,
                addon.IsVisible);
        }

        if (!KtgInput.TryGetInputEnabled(addon, out var enabled))
        {
            status = "Cannot read input enabled flag";
            LastError = "KTGInput AtkValues too short or unreadable (index 2)";
            PluginLogHelpers.WarnOnce(
                "enabled-unreadable",
                WarnCooldownMs,
                "Cannot read KTGInput enabled flag. AtkValuesCount={Count}. Run /dragontyper dump.",
                addon.AtkValuesCount);
            return;
        }

        if (!enabled)
        {
            waitFrom = null;
            status = "Letterlock: holding";
            return;
        }

        List<KtgInput.WordEntry> words;
        try
        {
            words = KtgInput.ReadWords(addon);
        }
        catch (Exception ex)
        {
            LastError = $"Word read failed: {ex.Message}";
            status = "Error reading words";
            PluginLogHelpers.ErrorOnce("read-words", WarnCooldownMs, ex, "Failed to read words from KTGInput");
            return;
        }

        if (words.Count == 0)
        {
            waitFrom = null;
            status = "No words parsed from KTGInput";
            LastError = "Addon open but no words parsed. Layout may have changed.";
            PluginLogHelpers.WarnOnce(
                "no-words-parsed",
                WarnCooldownMs,
                "KTGInput is open and enabled, but no words were parsed (AtkValuesCount={Count}). Run /dragontyper dump.",
                addon.AtkValuesCount);
            return;
        }

        var text = Pick(words, now);
        if (text == null)
        {
            waitFrom = null;
            status = "No matching enemy for available words";
            PluginLogHelpers.WarnOnce(
                "no-match",
                WarnCooldownMs,
                "Parsed {Count} word(s) but none matched a living targetable enemy. First='{First}' EntityId={EntityId}",
                words.Count,
                words[0].Text,
                words[0].EntityId);
            return;
        }

        if (waitFrom is null)
            StartWait(now);

        var delay = DelayMs(text, waitRoll);
        var waitStarted = waitFrom ?? now;
        if (now < waitStarted + delay)
        {
            status = $"Waiting {waitStarted + delay - now}ms: {text}";
            return;
        }

        if (holdUntil is long hold && now < hold)
        {
            status = "Holding for Letterlock cast";
            return;
        }

        var left = LetterlockCastLeftMs();
        if (left is float remaining && remaining <= GuardMs)
        {
            holdUntil = now + (long)Math.Max(0, remaining) + GuardMs;
            status = "Holding for Letterlock cast";
            Plugin.Log.Debug("Letterlock cast guard for {Ms}ms", holdUntil - now);
            return;
        }

        StartWait(now);
        recent[text] = now + RecentForMs;

        if (KtgInput.SubmitWord(addon, text))
        {
            Hits++;
            LastError = null;
            status = $"Typed: {text}";
            Plugin.Log.Information("Typed '{Text}' (hits={Hits})", text, Hits);
        }
        else
        {
            Misses++;
            LastError = $"SubmitWord failed for '{text}'";
            status = $"Missed: {text}";
            Plugin.Log.Error(
                "SubmitWord failed for '{Text}' (misses={Misses}). Check /xllog for FireCallback details.",
                text,
                Misses);
        }
    }

    private void StartWait(long now)
    {
        waitFrom = now;
        waitRoll = (float)(random.NextDouble() * 2.0 - 1.0);
    }

    private int DelayMs(string text, float roll)
    {
        var cfg = plugin.Configuration;
        var wait = cfg.WordDelayMs * (1f + roll * Jitter);
        if (cfg.LongSentenceDelay && text.Length >= LongWordChars)
            wait = Math.Min(wait * 2f, LongMaxMs);
        return Math.Max(0, (int)wait);
    }

    private string? Pick(List<KtgInput.WordEntry> words, long now)
    {
        string? best = null;
        var bestCasting = false;
        var bestDistance = float.MaxValue;
        var bestHp = 0u;

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
        {
            PluginLogHelpers.WarnOnce("no-player", WarnCooldownMs, "LocalPlayer is null while Active.");
            return null;
        }

        var me = player.Position;

        foreach (var word in words)
        {
            if (string.IsNullOrEmpty(word.Text))
                continue;
            if (recent.TryGetValue(word.Text, out var until) && now < until)
                continue;

            var obj = FindObject(word.EntityId);
            if (obj is not IBattleChara battle)
                continue;
            if (!battle.IsTargetable || battle.IsDead || battle.CurrentHp == 0)
                continue;

            var maxHp = battle.MaxHp;
            var isCasting = maxHp < BigHp && IsCasting(battle);
            var distance = Distance2D(me, battle.Position);

            var better = best == null;
            if (best != null)
            {
                if (isCasting != bestCasting)
                    better = isCasting;
                else if (Math.Abs(distance - bestDistance) > 0.01f)
                    better = distance < bestDistance;
                else
                    better = maxHp > bestHp;
            }

            if (!better)
                continue;

            best = word.Text;
            bestCasting = isCasting;
            bestDistance = distance;
            bestHp = maxHp;
        }

        return best;
    }

    private static IGameObject? FindObject(ulong entityId)
    {
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj.EntityId == entityId || obj.GameObjectId == entityId)
                return obj;
        }

        return null;
    }

    private static bool IsCasting(IBattleChara battle)
    {
        try
        {
            return battle.IsCasting && battle.CastActionId != 0;
        }
        catch (Exception ex)
        {
            PluginLogHelpers.WarnOnce("cast-read", WarnCooldownMs, "IsCasting read failed: {Message}", ex.Message);
            return false;
        }
    }

    private float? LetterlockCastLeftMs()
    {
        float? soonest = null;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not IBattleChara battle || battle.IsDead)
                continue;
            if (!battle.IsCasting || !LetterlockCasts.Contains(battle.CastActionId))
                continue;

            var left = (battle.TotalCastTime - battle.CurrentCastTime) * 1000f;
            if (soonest is null || left < soonest)
                soonest = left;
        }

        return soonest;
    }

    private static float Distance2D(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
