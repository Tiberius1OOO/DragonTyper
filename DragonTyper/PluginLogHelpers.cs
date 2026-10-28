using System;
using System.Collections.Generic;

namespace DragonTyper;

/// <summary>Rate-limited logging so problems show up in /xllog without flooding.</summary>
internal static class PluginLogHelpers
{
    private static readonly Dictionary<string, long> LastByKey = new(StringComparer.Ordinal);

    public static void InfoOnce(string key, int cooldownMs, string message, params object[] args)
    {
        if (!ShouldLog(key, cooldownMs))
            return;
        Plugin.Log.Information(message, args);
    }

    public static void WarnOnce(string key, int cooldownMs, string message, params object[] args)
    {
        if (!ShouldLog(key, cooldownMs))
            return;
        Plugin.Log.Warning(message, args);
    }

    public static void ErrorOnce(string key, int cooldownMs, Exception? ex, string message, params object[] args)
    {
        if (!ShouldLog(key, cooldownMs))
            return;
        if (ex == null)
            Plugin.Log.Error(message, args);
        else
            Plugin.Log.Error(ex, message, args);
    }

    public static void Error(Exception ex, string message, params object[] args) =>
        Plugin.Log.Error(ex, message, args);

    private static bool ShouldLog(string key, int cooldownMs)
    {
        var now = Environment.TickCount64;
        if (LastByKey.TryGetValue(key, out var last) && now - last < cooldownMs)
            return false;
        LastByKey[key] = now;
        return true;
    }
}
