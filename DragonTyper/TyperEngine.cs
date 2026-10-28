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

    private static readonly HashSet<uint> LetterlockCasts = [49529, 49532, 49702];

    private readonly Dictionary<string, long> recent = new(StringComparer.Ordinal);
    private readonly Random random = new();

    private long nextCheckAt;
    private long? waitFrom;
    private float waitRoll;
    private long? holdUntil;
    private string status = "Idle";

    public string Status => status;
    public int Hits { get; private set; }
    public int Misses { get; private set; }

    public void ResetIdle()
    {
        waitFrom = null;
        holdUntil = null;
        nextCheckAt = 0;
        recent.Clear();
        status = plugin.Configuration.Active ? "Waiting for Keybound Brawler input" : "Inactive";
    }

    public void DumpAddon()
    {
        var dump = KtgInput.Dump(KtgInput.GetAddon());
        foreach (var line in dump.Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line))
                Plugin.Log.Information("[dump] {Line}", line.TrimEnd());
        }
    }

    public void Tick()
    {
        var now = Environment.TickCount64;
        if (now < nextCheckAt)
            return;
        nextCheckAt = now + CheckEveryMs;

        if (!plugin.Configuration.Active)
        {
            waitFrom = null;
            status = "Inactive";
            return;
        }

        var addon = KtgInput.GetAddon();
        if (addon.IsNull)
        {
            waitFrom = null;
            status = "Waiting for Keybound Brawler input";
            return;
        }

        if (!KtgInput.TryGetInputEnabled(addon, out var enabled) || !enabled)
        {
            waitFrom = null;
            status = "Letterlock: holding";
            return;
        }

        var words = KtgInput.ReadWords(addon);
        var text = Pick(words, now);
        if (text == null)
        {
            waitFrom = null;
            status = "No word to type";
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
            return;
        }

        StartWait(now);
        recent[text] = now + RecentForMs;

        if (KtgInput.SubmitWord(addon, text))
        {
            Hits++;
            status = $"Typed: {text}";
            Plugin.Log.Debug("Typed '{Text}'", text);
        }
        else
        {
            Misses++;
            status = $"Missed: {text}";
            Plugin.Log.Warning("Missed '{Text}'", text);
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
            return null;

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

            // Prefer casting over non-casting; among equals prefer nearer; then larger HP.
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
        catch
        {
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

            // Remaining cast time in seconds → ms.
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
