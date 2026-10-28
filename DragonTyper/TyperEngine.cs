using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;

namespace DragonTyper;

/// <summary>
/// After Start: types Keybound Brawler words until Stop or you leave the duty.
/// </summary>
public sealed class TyperEngine(Plugin plugin)
{
    private const int CheckEveryMs = 100;
    private const float Jitter = 0.20f;
    private const int RecentForMs = 2500;
    private const int GuardMs = 500;
    private const uint BigHp = 1000;
    private const int LongWordChars = 28;
    private const int LongMaxMs = 5000;
    private const int WarnCooldownMs = 8000;

    private static readonly HashSet<uint> LetterlockCasts = [49529, 49532, 49702];

    private readonly Dictionary<string, long> recent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random random = new();

    private long nextCheckAt;
    private long? waitFrom;
    private float waitRoll;
    private long? holdUntil;
    private string status = "Idle";
    private bool sawAddon;
    private bool loggedSessionStart;
    private int emptyParseStreak;

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
        emptyParseStreak = 0;
        LastError = null;
        status = plugin.Configuration.Active ? "Started. Waiting for the minigame..." : "Stopped";
    }

    public void DumpAddon()
    {
        try
        {
            var addon = KtgInput.GetAddon();
            var dump = KtgInput.Dump(addon);
            Plugin.Log.Information("=== DragonTyper dump begin ===");
            foreach (var line in dump.Split('\n'))
            {
                if (!string.IsNullOrWhiteSpace(line))
                    Plugin.Log.Information("[dump] {Line}", line.TrimEnd());
            }

            Plugin.Log.Information("=== DragonTyper dump end ===");
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
            status = "Stopped";
            return;
        }

        if (!loggedSessionStart)
        {
            loggedSessionStart = true;
            Plugin.Log.Information(
                "Start pressed. Delay={Delay}ms LongSentence={Long}. Will type until Stop or duty end.",
                plugin.Configuration.WordDelayMs,
                plugin.Configuration.LongSentenceDelay);
        }

        var addon = KtgInput.GetAddon();
        if (addon.IsNull)
        {
            if (sawAddon)
            {
                Plugin.Log.Information("KTGInput closed (wave/end). Still running until Stop.");
                sawAddon = false;
            }

            waitFrom = null;
            status = "Running. Waiting for next words...";
            return;
        }

        if (!sawAddon)
        {
            sawAddon = true;
            Plugin.Log.Information(
                "KTGInput open. AtkValues={Count} cachedWords={Cached} source={Source}",
                addon.AtkValuesCount,
                KtgInput.CachedWords.Count,
                KtgInput.LastSource);
        }

        if (!KtgInput.TryGetInputEnabled(addon, out var enabled))
        {
            status = "Running. Input flag unreadable";
            LastError = "Cannot read KTGInput enabled flag (AtkValue[2])";
            PluginLogHelpers.WarnOnce("enabled-unreadable", WarnCooldownMs, "Cannot read enabled flag. AtkValuesCount={Count}", addon.AtkValuesCount);
            return;
        }

        if (!enabled)
        {
            waitFrom = null;
            status = "Running. Letterlock hold";
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
            PluginLogHelpers.ErrorOnce("read-words", WarnCooldownMs, ex, "Failed to read words");
            return;
        }

        if (words.Count == 0)
        {
            waitFrom = null;
            emptyParseStreak++;
            status = "Running. No words visible yet";
            if (emptyParseStreak >= 30)
            {
                LastError = "No words parsed while input is open. Use Dump for live arrays.";
                PluginLogHelpers.WarnOnce(
                    "no-words-parsed",
                    WarnCooldownMs,
                    "Input open/enabled but no words parsed (AtkValues={Count}, cache={Cache}). Press Dump.",
                    addon.AtkValuesCount,
                    KtgInput.CachedWords.Count);
            }

            return;
        }

        emptyParseStreak = 0;
        LastError = null;

        var text = Pick(words, now);
        if (text == null)
        {
            // All words were recently submitted; wait for new ones.
            status = $"Running. {words.Count} word(s) on cooldown";
            return;
        }

        if (waitFrom is null)
            StartWait(now);

        var delay = DelayMs(text, waitRoll);
        var waitStarted = waitFrom ?? now;
        if (now < waitStarted + delay)
        {
            status = $"Typing soon ({waitStarted + delay - now}ms): {text}";
            return;
        }

        if (holdUntil is long hold && now < hold)
        {
            status = "Running. Letterlock cast hold";
            return;
        }

        var left = LetterlockCastLeftMs();
        if (left is float remaining && remaining <= GuardMs)
        {
            holdUntil = now + (long)Math.Max(0, remaining) + GuardMs;
            status = "Running. Letterlock cast hold";
            return;
        }

        StartWait(now);
        recent[text] = now + RecentForMs;

        if (KtgInput.SubmitWord(addon, text))
        {
            Hits++;
            status = $"Typed: {text}";
            Plugin.Log.Information("Typed '{Text}' (hits={Hits})", text, Hits);
        }
        else
        {
            Misses++;
            LastError = $"Submit failed for '{text}'";
            status = $"Missed: {text}";
            Plugin.Log.Error("SubmitWord failed for '{Text}' (misses={Misses})", text, Misses);
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
        string? fallback = null;
        var fallbackScore = int.MinValue;

        var player = Plugin.ObjectTable.LocalPlayer;
        var me = player?.Position ?? Vector3.Zero;

        foreach (var word in words)
        {
            if (string.IsNullOrEmpty(word.Text))
                continue;
            if (recent.TryGetValue(word.Text, out var until) && now < until)
                continue;

            // Fallback score: sheet words and longer phrases beat hotbar leftovers.
            var score = 0;
            if (Plugin.Words.IsSheetWord(word.Text))
                score += 100;
            if (word.EntityId != 0)
                score += 50;
            score += Math.Min(word.Text.Length, 40);
            if (fallback is null || score > fallbackScore)
            {
                fallback = word.Text;
                fallbackScore = score;
            }

            if (word.EntityId == 0)
                continue;

            var obj = FindObject(word.EntityId);
            if (obj is not IBattleChara battle)
                continue;
            if (!battle.IsTargetable || battle.IsDead || battle.CurrentHp == 0)
                continue;

            var maxHp = battle.MaxHp;
            var isCasting = maxHp < BigHp && IsCasting(battle);
            var distance = player == null ? 0f : Distance2D(me, battle.Position);

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

        return best ?? fallback;
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
