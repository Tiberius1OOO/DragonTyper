using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.NativeWrapper;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DragonTyper;

/// <summary>
/// Holds typing during Letterlock windows:
/// KTGInput Atk[2]/[4], halt casts (Jammer / Cursed Gaze), post-cast tail, UI banners.
/// </summary>
internal sealed class LetterlockGuard
{
    private const float PauseFromProgress = 0.50f;
    private const long TailMs = 3000;
    private const float PreCastHaltSeconds = 1.8f;
    private const long PostCastHoldMs = 3800;

    private static readonly HashSet<uint> HaltActionIds =
    [
        49529, 49532, 49702,
        21396, 17407, 8342, 8341, 7354, 7353, 32448, 512,
    ];

    private static readonly string[] HaltSpellNames =
    [
        "Dynamic Sensory Jammer", "Cursed Gaze",
        "動体感知ジャマー", "呪いの視線",
        "Dynamischer Störsender", "Fluchblick",
        "Brouilleur sensoriel", "Regard maudit",
    ];

    private static readonly string[] InputsDisabled =
    [
        "Inputs disabled", "が付与された", "Schreibblockade verursacht", "Impossible de saisir du texte",
    ];

    private static readonly string[] InputsReEnabled =
    [
        "Inputs re-enabled", "が切れた", "Schreibblockade aufgehoben", "s'est dissipé",
    ];

    private long holdUntil;
    private bool wasCastingHalt;
    private float lastHaltCurrent;
    private float lastHaltRemaining;
    private string lastHaltName = "";

    public void Reset()
    {
        holdUntil = 0;
        wasCastingHalt = false;
        lastHaltCurrent = 0;
        lastHaltRemaining = 0;
        lastHaltName = "";
    }

    public bool ShouldHold(AtkUnitBasePtr inputAddon, long now, out string reason)
    {
        reason = "";

        if (BannerMatches(InputsReEnabled))
        {
            holdUntil = 0;
            wasCastingHalt = false;
        }

        if (now < holdUntil)
        {
            reason = $"Letterlock hold ({Math.Max(0, holdUntil - now)}ms)";
            return true;
        }

        if (TryReadInputLock(inputAddon, out var allowed, out var lockSeconds, out var lockReason))
        {
            if (!allowed)
            {
                reason = lockReason;
                if (lockSeconds > 0)
                    holdUntil = Math.Max(holdUntil, now + lockSeconds * 1000L);
                return true;
            }
        }

        if (BannerMatches(InputsDisabled))
        {
            reason = "Inputs disabled banner";
            return true;
        }

        if (TryFindHaltCast(out var name, out var current, out var total, out var remaining, out var progress))
        {
            wasCastingHalt = true;
            lastHaltName = name;
            lastHaltCurrent = current;
            lastHaltRemaining = remaining;

            // Pause from half cast bar onward, and always in the last 1.8s.
            if (progress >= PauseFromProgress || remaining <= PreCastHaltSeconds)
            {
                holdUntil = Math.Max(holdUntil, now + TailMs);
                reason = $"Letterlock cast: {name} ({remaining:0.0}s left)";
                return true;
            }
        }
        else if (wasCastingHalt)
        {
            wasCastingHalt = false;
            if (lastHaltCurrent >= 3.0f || lastHaltRemaining <= 2.0f)
            {
                holdUntil = Math.Max(holdUntil, now + PostCastHoldMs);
                reason = $"Letterlock after {lastHaltName}";
                return true;
            }
        }

        // Array cast-name fallback (localized).
        if (KtgHudArrays.TryRead(out var hud))
        {
            foreach (var w in hud)
            {
                if (string.IsNullOrEmpty(w.CastName) || !IsHaltSpell(w.CastName, 0))
                    continue;
                if (w.CastProgress < 50)
                    continue;
                holdUntil = Math.Max(holdUntil, now + TailMs);
                reason = $"Letterlock cast (HUD): {w.CastName}";
                return true;
            }
        }

        return false;
    }

    private static bool TryReadInputLock(AtkUnitBasePtr addon, out bool allowed, out int lockSeconds, out string reason)
    {
        allowed = false;
        lockSeconds = 0;
        reason = "Input lock unreadable";
        if (addon.IsNull || addon.AtkValuesCount < 5)
            return false;

        if (!KtgInput.TryGetInputEnabled(addon, out var enabled))
            return false;

        lockSeconds = ReadLockSeconds(addon);
        if (lockSeconds > 0)
        {
            reason = $"Typing prohibited ({lockSeconds}s)";
            return true;
        }

        if (!enabled)
        {
            reason = "Activity prohibits typing";
            return true;
        }

        allowed = true;
        reason = "Ready";
        return true;
    }

    private static int ReadLockSeconds(AtkUnitBasePtr addon)
    {
        try
        {
            var i = 0;
            foreach (var value in addon.AtkValues)
            {
                if (i == 4)
                {
                    return value.ValueType switch
                    {
                        Dalamud.Game.NativeWrapper.AtkValueType.Int => value.TryGet<int>(out var n) ? Math.Max(0, n) : 0,
                        Dalamud.Game.NativeWrapper.AtkValueType.UInt => value.TryGet<uint>(out var u) ? (int)u : 0,
                        _ => 0,
                    };
                }

                i++;
            }
        }
        catch
        {
            // ignore
        }

        return 0;
    }

    private static bool TryFindHaltCast(
        out string name, out float current, out float total, out float remaining, out float progress)
    {
        name = "";
        current = total = remaining = progress = 0;
        try
        {
            foreach (var obj in Plugin.ObjectTable)
            {
                if (obj is not IBattleChara battle || battle.IsDead || !battle.IsCasting)
                    continue;
                var actionId = battle.CastActionId;
                if (!IsHaltSpell(null, actionId))
                    continue;

                current = battle.CurrentCastTime;
                total = Math.Max(0.1f, battle.TotalCastTime);
                remaining = Math.Max(0f, total - current);
                progress = current / total;
                name = $"action:{actionId}";
                return true;
            }
        }
        catch
        {
            // zone transition
        }

        // Cast-bar caption fallback.
        try
        {
            var bar = Plugin.GameGui.GetAddonByName("KTGEnemyCastBar");
            if (!bar.IsNull && bar.IsVisible)
            {
                foreach (var label in ReadCastBarLabels(bar))
                {
                    if (!IsHaltSpell(label, 0))
                        continue;
                    name = label;
                    // Unknown timer from UI alone — treat as mid-cast danger.
                    progress = PauseFromProgress;
                    remaining = PreCastHaltSeconds;
                    current = 3f;
                    total = 5f;
                    return true;
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static unsafe List<string> ReadCastBarLabels(AtkUnitBasePtr addon)
    {
        var labels = new List<string>();
        try
        {
            var unit = (AtkUnitBase*)addon.Address;
            if (unit == null || unit->UldManager.NodeList == null)
                return labels;

            CollectTextNodes(&unit->UldManager, labels, 0);
        }
        catch
        {
            // ignore
        }

        return labels;
    }

    private static unsafe void CollectTextNodes(AtkUldManager* mgr, List<string> labels, int depth)
    {
        if (mgr == null || mgr->NodeList == null || depth > 8)
            return;
        var count = Math.Min((int)mgr->NodeListCount, 256);
        for (var i = 0; i < count; i++)
        {
            var node = mgr->NodeList[i];
            if (node == null)
                continue;
            if (node->Type == NodeType.Text && node->NodeId == 4)
            {
                var text = KtgWordSlots.DecodeSeString(((AtkTextNode*)node)->NodeText.ToString());
                if (!string.IsNullOrWhiteSpace(text))
                    labels.Add(text);
            }
            else if ((int)node->Type >= 1000)
            {
                var comp = ((AtkComponentNode*)node)->Component;
                if (comp != null)
                    CollectTextNodes(&comp->UldManager, labels, depth + 1);
            }
        }
    }

    private static bool BannerMatches(string[] needles)
    {
        return AddonContainsAny("_ScreenText", needles) || AddonContainsAny("KTGHudLog", needles);
    }

    private static unsafe bool AddonContainsAny(string addonName, string[] needles)
    {
        try
        {
            var addon = Plugin.GameGui.GetAddonByName(addonName);
            if (addon.IsNull || !addon.IsVisible)
                return false;
            var unit = (AtkUnitBase*)addon.Address;
            if (unit == null || unit->UldManager.NodeList == null)
                return false;

            var texts = new List<string>();
            CollectAllText(&unit->UldManager, texts, 0);
            foreach (var t in texts)
            {
                foreach (var n in needles)
                {
                    if (t.Contains(n, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static unsafe void CollectAllText(AtkUldManager* mgr, List<string> texts, int depth)
    {
        if (mgr == null || mgr->NodeList == null || depth > 6 || texts.Count > 40)
            return;
        var count = Math.Min((int)mgr->NodeListCount, 128);
        for (var i = 0; i < count; i++)
        {
            var node = mgr->NodeList[i];
            if (node == null)
                continue;
            if (node->Type == NodeType.Text)
            {
                var text = KtgWordSlots.DecodeSeString(((AtkTextNode*)node)->NodeText.ToString());
                if (!string.IsNullOrWhiteSpace(text))
                    texts.Add(text);
            }
            else if ((int)node->Type >= 1000)
            {
                var comp = ((AtkComponentNode*)node)->Component;
                if (comp != null)
                    CollectAllText(&comp->UldManager, texts, depth + 1);
            }
        }
    }

    public static bool IsHaltSpell(string? spellName, uint actionId)
    {
        if (actionId != 0 && HaltActionIds.Contains(actionId))
            return true;
        if (string.IsNullOrWhiteSpace(spellName))
            return false;
        foreach (var n in HaltSpellNames)
        {
            if (spellName.Contains(n, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
