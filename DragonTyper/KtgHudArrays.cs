using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DragonTyper;

/// <summary>
/// NumberArray 161 + StringArray 128: per-enemy word, reading, cast, kind, progress.
/// Kind 2 = barrier/defense phrase.
/// </summary>
internal static unsafe class KtgHudArrays
{
    private const int NumberArrayIndex = 161;
    private const int StringArrayIndex = 128;
    private const int SlotCount = 20;
    private const int Stride = 13;
    private const int InvalidEntity = unchecked((int)0xE0000000);

    public readonly record struct HudWord(
        ulong EntityId,
        string Name,
        string Reading,
        string CastName,
        int Kind,
        int CastProgress,
        bool ImportantCast,
        bool Visible,
        int ScreenX,
        int ScreenY);

    public static bool TryRead(out List<HudWord> words)
    {
        words = [];
        try
        {
            var module = RaptureAtkModule.Instance();
            if (module == null)
                return false;

            var nad = module->GetNumberArrayData(NumberArrayIndex);
            var sad = module->GetStringArrayData(StringArrayIndex);
            if (nad == null || sad == null || nad->IntArray == null || sad->StringArray == null)
                return false;
            if (nad->Size < 1 + Stride || sad->Size < 3)
                return false;

            var count = nad->IntArray[0];
            if (count is < 0 or > SlotCount)
                return false;
            if (nad->Size < 1 + count * Stride)
                return false;
            if (sad->Size < count * 3)
                return false;

            for (var slot = 0; slot < count; slot++)
            {
                var baseIdx = 1 + slot * Stride;
                var entityRaw = nad->IntArray[baseIdx];
                if (entityRaw == 0 || entityRaw == InvalidEntity)
                    continue;

                var entity = unchecked((uint)entityRaw);
                var screenX = nad->IntArray[baseIdx + 1];
                var screenY = nad->IntArray[baseIdx + 2];
                var visible = nad->IntArray[baseIdx + 4] != 0;
                var castProgress = nad->IntArray[baseIdx + 7];
                var important = nad->IntArray[baseIdx + 8] != 0;
                var kind = nad->IntArray[baseIdx + 10];

                var name = ReadString(sad, slot * 3);
                var reading = ReadString(sad, slot * 3 + 1);
                var castName = ReadString(sad, slot * 3 + 2);

                words.Add(new HudWord(
                    entity, name, reading, castName, kind, castProgress, important, visible, screenX, screenY));
            }

            return words.Count > 0;
        }
        catch (Exception ex)
        {
            PluginLogHelpers.WarnOnce("ktg-arrays", 5000, "HUD array read failed: {Message}", ex.Message);
            return false;
        }
    }

    private static string ReadString(StringArrayData* sad, int index)
    {
        try
        {
            if (sad == null || sad->StringArray == null || index < 0 || index >= sad->Size)
                return string.Empty;
            var ptr = sad->StringArray[index];
            if (!ptr.HasValue)
                return string.Empty;
            return KtgWordSlots.DecodeSeString(ptr.ToString());
        }
        catch
        {
            return string.Empty;
        }
    }
}
