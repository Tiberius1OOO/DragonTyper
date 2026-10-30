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
    private const uint BigHp = 1000;
    private const int LongWordChars = 28;
    private const int LongMaxMs = 5000;
    private const int WarnCooldownMs = 8000;

    private readonly Dictionary<string, long> recent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random random = new();
    private readonly LetterlockGuard letterlock = new();

    private long nextCheckAt;
    private long? waitFrom;
    private float waitRoll;
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
        nextCheckAt = 0;
        recent.Clear();
        letterlock.Reset();
        sawAddon = false;
        loggedSessionStart = false;
        emptyParseStreak = 0;
        LastError = null;
        status = plugin.Configuration.Active ? "Started. Waiting for the minigame..." : "Stopped";
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

        // SetText/callback does not count as key activity — duty kicks at ~60s idle.
        unsafe
        {
            KtgIdleKeepalive.Pulse((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addon.Address);
        }

        if (letterlock.ShouldHold(addon, now, out var lockReason))
        {
            waitFrom = null;
            status = $"Running. {lockReason}";
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
                LastError = "No words parsed while input is open.";
                PluginLogHelpers.WarnOnce(
                    "no-words-parsed",
                    WarnCooldownMs,
                    "Input open/enabled but no words parsed (AtkValues={Count}, cache={Cache}).",
                    addon.AtkValuesCount,
                    KtgInput.CachedWords.Count);
            }

            return;
        }

        emptyParseStreak = 0;
        LastError = null;

        var text = Pick(words, now, out var onCooldown);
        if (text == null)
        {
            status = onCooldown > 0
                ? $"Running. {onCooldown} word(s) just typed, waiting"
                : "Running. No typeable word";
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

        // Re-check letterlock right before submit.
        if (letterlock.ShouldHold(addon, now, out lockReason))
        {
            waitFrom = null;
            status = $"Running. {lockReason}";
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

    /// <summary>
    /// Priority: barrier → important/interrupt cast → casting add → nearest → bigger HP.
    /// </summary>
    private string? Pick(List<KtgInput.WordEntry> words, long now, out int onCooldown)
    {
        string? best = null;
        var bestRank = int.MinValue;
        var bestCastProgress = -1;
        var bestDistance = float.MaxValue;
        var bestHp = 0u;
        string? fallback = null;
        onCooldown = 0;

        var player = Plugin.ObjectTable.LocalPlayer;
        var me = player?.Position ?? Vector3.Zero;

        foreach (var word in words)
        {
            if (string.IsNullOrEmpty(word.Text))
                continue;
            if (recent.TryGetValue(word.Text, out var until) && now < until)
            {
                onCooldown++;
                continue;
            }

            fallback ??= word.Text;

            var rank = 0;
            if (word.IsBarrier)
                rank = 3;
            else if (word.ImportantCast || word.CastProgress > 0)
                rank = 2;

            var distance = float.MaxValue;
            var hp = 0u;
            var casting = false;

            if (word.EntityId != 0)
            {
                var obj = FindObject(word.EntityId);
                if (obj is IBattleChara battle && battle.IsTargetable && !battle.IsDead && battle.CurrentHp > 0)
                {
                    hp = battle.MaxHp;
                    distance = player == null ? 0f : Distance2D(me, battle.Position);
                    casting = hp < BigHp && IsCasting(battle);
                    if (rank < 2 && casting)
                        rank = 1;
                }
                else if (word.ScreenY != 0)
                {
                    // No object resolve — use screen Y as crude "closer to barrier" hint (higher Y = closer).
                    distance = -word.ScreenY;
                }
            }
            else if (word.ScreenY != 0)
            {
                distance = -word.ScreenY;
            }

            var better = best == null;
            if (best != null)
            {
                if (rank != bestRank)
                    better = rank > bestRank;
                else if (word.CastProgress != bestCastProgress && rank >= 2)
                    better = word.CastProgress > bestCastProgress;
                else if (Math.Abs(distance - bestDistance) > 0.01f)
                    better = distance < bestDistance;
                else
                    better = hp > bestHp;
            }

            if (!better)
                continue;

            best = word.Text;
            bestRank = rank;
            bestCastProgress = word.CastProgress;
            bestDistance = distance;
            bestHp = hp;
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
            return battle.IsCasting && battle.CastActionId != 0
                   && !LetterlockGuard.IsHaltSpell(null, battle.CastActionId);
        }
        catch (Exception ex)
        {
            PluginLogHelpers.WarnOnce("cast-read", WarnCooldownMs, "IsCasting read failed: {Message}", ex.Message);
            return false;
        }
    }

    private static float Distance2D(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
