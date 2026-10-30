using System;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DragonTyper;

/// <summary>
/// Keybound Brawler exits the duty after ~60s with no real key activity.
/// SetText+callback does not count — so we zero AgentKTGInput idle floats
/// (and the generic InputTimerModule) while we are actively typing in-duty.
/// </summary>
internal static unsafe class KtgIdleKeepalive
{
    private const AgentId InputAgentId = (AgentId)398;
    private const int Territory = 1359;

    [StructLayout(LayoutKind.Explicit, Size = 0x60)]
    private struct InputAgent
    {
        [FieldOffset(0x00)] public AgentInterface Agent;
        [FieldOffset(0x28)] public AgentInterface* Hud;
        [FieldOffset(0x50)] public float IdleSeconds;
        [FieldOffset(0x54)] public float KeyHoldSeconds;
    }

    private static long lastOkLogAt;
    private static long lastFailLogAt;

    /// <summary>Call every typer tick while KTGInput is open and Active.</summary>
    public static void Pulse(AtkUnitBase* ktgInputAddon)
    {
        try
        {
            if (!Plugin.ClientState.IsLoggedIn)
                return;
            if (Plugin.ClientState.TerritoryType != Territory)
                return;
            if (ktgInputAddon == null || !ktgInputAddon->IsVisible || !ktgInputAddon->IsReady)
                return;

            var module = AgentModule.Instance();
            if (module == null)
                return;

            var hud = module->GetAgentByInternalId(AgentId.KTGHud);
            var input = (InputAgent*)module->GetAgentByInternalId(InputAgentId);
            if (input == null || hud == null)
                return;

            // Only touch memory when the input agent is wired to the live HUD.
            if ((AgentInterface*)input == hud)
                return;
            if (input->Hud != hud)
            {
                WarnFail("hud pointer mismatch");
                return;
            }

            if (!float.IsFinite(input->IdleSeconds) || input->IdleSeconds is < 0 or > 120)
            {
                WarnFail($"idle out of range ({input->IdleSeconds})");
                return;
            }

            if (!float.IsFinite(input->KeyHoldSeconds) || input->KeyHoldSeconds < 0)
            {
                WarnFail($"keyHold out of range ({input->KeyHoldSeconds})");
                return;
            }

            var before = input->IdleSeconds;
            input->IdleSeconds = 0;
            input->KeyHoldSeconds = 0;

            ResetClientTimers();

            var now = Environment.TickCount64;
            if (before > 5f && now - lastOkLogAt > 15000)
            {
                lastOkLogAt = now;
                Plugin.Log.Debug("KTG idle reset (was {Idle:0.0}s)", before);
            }
        }
        catch (Exception ex)
        {
            PluginLogHelpers.WarnOnce("ktg-idle", 8000, "Idle keepalive failed: {Message}", ex.Message);
        }
    }

    private static void ResetClientTimers()
    {
        try
        {
            var timers = InputTimerModule.Instance();
            if (timers == null)
                return;
            timers->ContentInputTimer = 0;
            timers->InputTimer = 0;
            timers->AfkTimer = 0;
        }
        catch
        {
            // InputTimerModule layout can drift; agent idle is the critical one.
        }
    }

    private static void WarnFail(string why)
    {
        var now = Environment.TickCount64;
        if (now - lastFailLogAt < 20000)
            return;
        lastFailLogAt = now;
        Plugin.Log.Debug("KTG idle keepalive skipped: {Why}", why);
    }
}
