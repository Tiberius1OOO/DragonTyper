using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.NativeWrapper;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using CsAtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace DragonTyper;

/// <summary>
/// Crash-safe timed capture: for ~5s writes everything readable about the KTG UI
/// into a file under the plugin config folder (and mirrors headers to /xllog).
/// </summary>
internal sealed unsafe class LiveCapture
{
    private const int DurationMs = 5000;
    private const int SnapshotEveryMs = 500;
    private const int MaxFileBytes = 4 * 1024 * 1024;
    private const int MaxLinesPerSnapshot = 400;

    private StreamWriter? writer;
    private string? filePath;
    private long startedAt;
    private long nextSnapshotAt;
    private int snapshotIndex;
    private int totalLines;
    private bool active;

    public bool IsRunning => active;
    public string? FilePath => filePath;
    public int RemainingMs =>
        active ? Math.Max(0, (int)(startedAt + DurationMs - Environment.TickCount64)) : 0;

    public string Start()
    {
        if (active)
            return "Capture already running.";

        try
        {
            var dir = Plugin.PluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(dir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            filePath = Path.Combine(dir, $"capture-{stamp}.txt");
            var latest = Path.Combine(dir, "last-capture.txt");

            writer = new StreamWriter(filePath, append: false, Encoding.UTF8)
            {
                AutoFlush = true,
            };

            startedAt = Environment.TickCount64;
            nextSnapshotAt = 0;
            snapshotIndex = 0;
            totalLines = 0;
            active = true;

            WriteLine($"=== DragonTyper live capture start {DateTime.Now:O} ===");
            WriteLine($"Version={typeof(Plugin).Assembly.GetName().Version}");
            WriteLine($"Territory={Plugin.ClientState.TerritoryType}");
            WriteLine($"DurationMs={DurationMs} SnapshotEveryMs={SnapshotEveryMs}");
            WriteLine($"File={filePath}");
            WriteLine("Search this file for the on-screen phrase from your screenshot.");
            WriteLine("");

            try
            {
                File.Copy(filePath, latest, overwrite: true);
            }
            catch
            {
                // latest copy is convenience only
            }

            Plugin.Log.Information("Live capture started → {Path} ({Ms}ms)", filePath, DurationMs);
            return $"Recording {DurationMs / 1000}s → {filePath}";
        }
        catch (Exception ex)
        {
            StopInternal("start failed");
            Plugin.Log.Error(ex, "Live capture failed to start");
            return $"Capture start failed: {ex.Message}";
        }
    }

    public void Tick()
    {
        if (!active)
            return;

        var now = Environment.TickCount64;
        try
        {
            if (now >= startedAt + DurationMs)
            {
                Finish("time up");
                return;
            }

            if (now < nextSnapshotAt)
                return;
            nextSnapshotAt = now + SnapshotEveryMs;

            if (writer == null)
            {
                Finish("writer missing");
                return;
            }

            if (writer.BaseStream.Length > MaxFileBytes)
            {
                Finish("size cap");
                return;
            }

            Snapshot(now);
            RefreshLatestCopy();
        }
        catch (Exception ex)
        {
            SafeWrite($"!! Tick fault: {ex.GetType().Name}: {ex.Message}");
            PluginLogHelpers.ErrorOnce("capture-tick", 3000, ex, "Live capture tick fault (continuing)");
        }
    }

    public void Cancel()
    {
        if (!active)
            return;
        Finish("cancelled");
    }

    private void Snapshot(long now)
    {
        snapshotIndex++;
        var elapsed = now - startedAt;
        WriteLine($"----- snapshot #{snapshotIndex} t={elapsed}ms {DateTime.Now:HH:mm:ss.fff} -----");

        SafeSection("KTGInput", DumpKtgInput);
        SafeSection("KTGEWord multi", DumpKtgEWordMulti);
        SafeSection("All KTG addons", DumpAllKtgAddons);
        SafeSection("NumberArray entity/word pairs", DumpNumberWordPairs);
        SafeSection("Agents", DumpAgents);
        SafeSection("String arrays", DumpStringArrays);
        SafeSection("Nearby objects", DumpNearbyObjects);
        SafeSection("Word reader result", DumpReaderResult);
        WriteLine("");
    }

    private void SafeSection(string title, Action action)
    {
        WriteLine($"-- {title} --");
        try
        {
            action();
        }
        catch (Exception ex)
        {
            WriteLine($"  !! {title} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void DumpKtgInput()
    {
        var addon = KtgInput.GetAddon();
        if (addon.IsNull)
        {
            WriteLine("  (KTGInput not open)");
            return;
        }

        WriteLine($"  visible={addon.IsVisible} ready={addon.IsReady} values={addon.AtkValuesCount}");
        if (KtgInput.TryGetInputEnabled(addon, out var enabled))
            WriteLine($"  enabled={enabled}");
        DumpAddonDeep(addon, "  ");
    }

    private void DumpNamedAddon(string name)
    {
        var addon = Plugin.GameGui.GetAddonByName(name);
        if (addon.IsNull)
        {
            WriteLine($"  ({name} null)");
            return;
        }

        WriteLine($"  visible={addon.IsVisible} ready={addon.IsReady} values={addon.AtkValuesCount}");
        DumpAddonDeep(addon, "  ");
    }

    private void DumpKtgEWordMulti()
    {
        for (var i = 1; i <= 16; i++)
        {
            try
            {
                var addon = Plugin.GameGui.GetAddonByName("KTGEWord", i);
                if (addon.IsNull)
                {
                    WriteLine($"  KTGEWord[{i}] null");
                    if (i > 2)
                        break;
                    continue;
                }

                WriteLine($"  KTGEWord[{i}] vis={addon.IsVisible} ready={addon.IsReady} values={addon.AtkValuesCount}");
                DumpAddonDeep(addon, "    ");
            }
            catch (Exception ex)
            {
                WriteLine($"  KTGEWord[{i}] err {ex.Message}");
            }
        }
    }

    private void DumpNumberWordPairs()
    {
        try
        {
            var sheet = Plugin.Words?.SheetWords;
            if (sheet == null || sheet.Count == 0)
            {
                WriteLine("  (word sheet empty)");
                return;
            }

            var module = RaptureAtkModule.Instance();
            if (module == null)
                return;

            var holder = module->AtkArrayDataHolder;
            var count = Math.Min((int)holder.NumberArrayCount, 200);
            var hits = 0;
            for (var i = 0; i < count && hits < 40; i++)
            {
                var nad = module->GetNumberArrayData(i);
                if (nad == null || nad->IntArray == null || nad->Size is <= 0 or > 96)
                    continue;
                var size = nad->Size;
                for (var s = 0; s + 1 < size && hits < 40; s++)
                {
                    var a = nad->IntArray[s];
                    var b = nad->IntArray[s + 1];
                    var ua = unchecked((uint)a);
                    var ub = unchecked((uint)b);
                    string? text = null;
                    uint entity = 0;
                    if (ua is >= 0x40000000 and <= 0x4FFFFFFF && sheet.TryGetValue(ub, out text))
                        entity = ua;
                    else if (ub is >= 0x40000000 and <= 0x4FFFFFFF && sheet.TryGetValue(ua, out text))
                        entity = ub;
                    else
                        continue;

                    WriteLine($"  num[{i}][{s}] entity={entity} wordId → \"{text}\"");
                    hits++;
                }
            }

            if (hits == 0)
                WriteLine("  (no entity/wordId pairs this snapshot)");
        }
        catch (Exception ex)
        {
            WriteLine($"  num-pair err {ex.Message}");
        }
    }

    private void DumpAllKtgAddons()
    {
        foreach (var name in SafeListAddonNames())
        {
            if (!name.Contains("KTG", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Word", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Type", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var addon = Plugin.GameGui.GetAddonByName(name);
                WriteLine($"  [{name}] null={addon.IsNull} vis={(!addon.IsNull && addon.IsVisible)} ready={(!addon.IsNull && addon.IsReady)} values={(!addon.IsNull ? addon.AtkValuesCount : 0)}");
                if (!addon.IsNull && addon.IsVisible)
                    DumpAddonDeep(addon, "    ");
            }
            catch (Exception ex)
            {
                WriteLine($"  [{name}] err {ex.Message}");
            }
        }
    }

    private void DumpAgents()
    {
        foreach (var (id, label) in new (int, string)[]
                 {
                     ((int)AgentId.KTGHud, "KTGHud"),
                     ((int)AgentId.KTGEnterDialogue, "KTGEnterDialogue"),
                     ((int)AgentId.KTGResult, "KTGResult"),
                 })
        {
            try
            {
                var agent = Plugin.GameGui.GetAgentById(id);
                WriteLine($"  Agent {label}({id}) null={agent.IsNull} active={(!agent.IsNull && agent.IsAgentActive)} addonId={(!agent.IsNull ? agent.AddonId : 0)}");
                if (agent.IsNull)
                    continue;
                var addon = agent.Addon;
                if (!addon.IsNull)
                {
                    WriteLine($"    addon=\"{addon.Name}\" vis={addon.IsVisible} values={addon.AtkValuesCount}");
                    DumpAddonDeep(addon, "    ");
                }
            }
            catch (Exception ex)
            {
                WriteLine($"  Agent {label} err {ex.Message}");
            }
        }
    }

    private void DumpStringArrays()
    {
        var module = RaptureAtkModule.Instance();
        if (module == null)
        {
            WriteLine("  (RaptureAtkModule null)");
            return;
        }

        var holder = module->AtkArrayDataHolder;
        var count = Math.Min((int)holder.StringArrayCount, 200);
        WriteLine($"  StringArrayCount={holder.StringArrayCount} scanning={count}");

        var printed = 0;
        for (var i = 0; i < count && printed < 120; i++)
        {
            try
            {
                var sad = module->GetStringArrayData(i);
                if (sad == null || sad->Size <= 0 || sad->Size > 512 || sad->StringArray == null)
                    continue;

                // Print array header only when it has at least one non-empty string.
                var any = false;
                var buf = new List<string>();
                var size = Math.Min(sad->Size, 64);
                for (var s = 0; s < size; s++)
                {
                    var text = SafeCString(sad->StringArray[s]);
                    if (string.IsNullOrWhiteSpace(text))
                        continue;
                    // Cap very long chrome strings.
                    if (text.Length > 80)
                        text = text[..80] + "…";
                    buf.Add($"[{s}]=\"{text}\"");
                    any = true;
                    if (buf.Count >= 24)
                        break;
                }

                if (!any)
                    continue;

                WriteLine($"  str[{i}] size={sad->Size}");
                foreach (var line in buf)
                {
                    WriteLine($"    {line}");
                    printed++;
                    if (printed >= 120)
                        break;
                }
            }
            catch (Exception ex)
            {
                WriteLine($"  str[{i}] err {ex.Message}");
            }
        }
    }

    private void DumpNearbyObjects()
    {
        try
        {
            var me = Plugin.ObjectTable.LocalPlayer;
            WriteLine($"  local=\"{me?.Name.TextValue}\" pos={me?.Position}");
            var n = 0;
            foreach (var obj in Plugin.ObjectTable)
            {
                if (obj == null || string.IsNullOrEmpty(obj.Name.TextValue))
                    continue;
                if (n++ > 40)
                {
                    WriteLine("  ...(truncated objects)");
                    break;
                }

                var kind = obj.GetType().Name;
                var battle = obj as IBattleChara;
                var cast = battle is { IsCasting: true } ? $" cast={battle.CastActionId}" : "";
                WriteLine(
                    $"  obj kind={kind} id={obj.EntityId} name=\"{obj.Name.TextValue}\" targetable={obj.IsTargetable}{cast}");
            }
        }
        catch (Exception ex)
        {
            WriteLine($"  objects err {ex.Message}");
        }
    }

    private void DumpReaderResult()
    {
        try
        {
            var addon = KtgInput.GetAddon();
            var words = KtgInput.ReadWords(addon);
            WriteLine($"  source={KtgInput.LastSource} count={words.Count}");
            foreach (var w in words.Take(30))
                WriteLine($"  word entity={w.EntityId} \"{w.Text}\"");
        }
        catch (Exception ex)
        {
            WriteLine($"  reader err {ex.Message}");
        }
    }

    private void DumpAddonDeep(AtkUnitBasePtr addon, string indent)
    {
        if (addon.IsNull)
            return;

        try
        {
            var unit = (AtkUnitBase*)addon.Address;
            if (unit == null)
                return;

            if (unit->AtkValues != null && unit->AtkValuesCount > 0)
            {
                var count = Math.Min((int)unit->AtkValuesCount, 48);
                for (var i = 0; i < count; i++)
                {
                    try
                    {
                        var v = &unit->AtkValues[i];
                        var rendered = SafeAtkString(v);
                        WriteLine($"{indent}Atk[{i}] {v->Type} {rendered}");
                        // If vector, peek a few children.
                        if (v->Type is CsAtkValueType.Vector or CsAtkValueType.ManagedVector)
                        {
                            try
                            {
                                var size = Math.Min(v->GetVectorSize(), 12u);
                                for (uint c = 0; c < size; c++)
                                {
                                    var child = v->GetVectorValue(c);
                                    if (child == null)
                                        continue;
                                    WriteLine($"{indent}  vec[{c}] {child->Type} {SafeAtkString(child)}");
                                }
                            }
                            catch (Exception ex)
                            {
                                WriteLine($"{indent}  vec err {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        WriteLine($"{indent}Atk[{i}] err {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            WriteLine($"{indent}AtkValues err {ex.Message}");
        }

        try
        {
            foreach (var text in ReadTextNodes(addon, 80))
                WriteLine($"{indent}text \"{text}\"");
        }
        catch (Exception ex)
        {
            WriteLine($"{indent}text-walk err {ex.Message}");
        }
    }

    private static List<string> ReadTextNodes(AtkUnitBasePtr addon, int maxNodes)
    {
        var result = new List<string>();
        if (addon.IsNull)
            return result;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || unit->RootNode == null)
            return result;

        var visited = 0;
        void Walk(AtkResNode* parent)
        {
            if (parent == null || visited > maxNodes)
                return;
            for (var node = parent->ChildNode; node != null; node = node->PrevSiblingNode)
            {
                if (visited++ >= maxNodes)
                    return;
                try
                {
                    if (node->Type == NodeType.Text)
                    {
                        var text = ((AtkTextNode*)node)->NodeText.ToString();
                        if (!string.IsNullOrWhiteSpace(text))
                            result.Add(text.Trim());
                    }

                    if ((int)node->Type >= 1000)
                    {
                        var comp = (AtkComponentNode*)node;
                        if (comp->Component != null)
                        {
                            var uld = comp->Component->UldManager;
                            var count = Math.Min((int)uld.NodeListCount, 96);
                            for (var i = 0; i < count; i++)
                            {
                                if (visited++ >= maxNodes)
                                    return;
                                var n = uld.NodeList[i];
                                if (n == null || n->Type != NodeType.Text)
                                    continue;
                                var text = ((AtkTextNode*)n)->NodeText.ToString();
                                if (!string.IsNullOrWhiteSpace(text))
                                    result.Add(text.Trim());
                            }
                        }
                    }

                    Walk(node);
                }
                catch
                {
                    // skip bad node
                }
            }
        }

        Walk(unit->RootNode);
        return result.Distinct(StringComparer.Ordinal).Take(40).ToList();
    }

    private static List<string> SafeListAddonNames()
    {
        var names = new List<string>();
        try
        {
            var stage = AtkStage.Instance();
            var mgr = stage != null ? stage->RaptureAtkUnitManager : null;
            if (mgr == null)
                return names;
            foreach (var entry in mgr->AllLoadedUnitsList.Entries)
            {
                try
                {
                    var unit = entry.Value;
                    if (unit == null)
                        continue;
                    var name = unit->NameString;
                    if (!string.IsNullOrEmpty(name))
                        names.Add(name);
                }
                catch
                {
                    // skip
                }
            }
        }
        catch
        {
            // ignore
        }

        return names;
    }

    private static string SafeAtkString(AtkValue* v)
    {
        if (v == null)
            return "(null ptr)";
        try
        {
            return $"\"{v->GetValueAsString()}\"";
        }
        catch (Exception ex)
        {
            return $"(read err {ex.Message})";
        }
    }

    private static string? SafeCString(InteropGenerator.Runtime.CStringPointer ptr)
    {
        try
        {
            if (!ptr.HasValue)
                return null;
            return ptr.ToString();
        }
        catch
        {
            return null;
        }
    }

    private void WriteLine(string line)
    {
        if (writer == null)
            return;
        if (totalLines > MaxLinesPerSnapshot * ((DurationMs / SnapshotEveryMs) + 2))
            return;
        writer.WriteLine(line);
        totalLines++;
    }

    private void SafeWrite(string line)
    {
        try
        {
            WriteLine(line);
        }
        catch
        {
            // ignore
        }
    }

    private void RefreshLatestCopy()
    {
        try
        {
            if (filePath == null || writer == null)
                return;
            writer.Flush();
            var latest = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "last-capture.txt");
            File.Copy(filePath, latest, overwrite: true);
        }
        catch
        {
            // ignore
        }
    }

    private void Finish(string reason)
    {
        try
        {
            WriteLine($"=== capture end reason={reason} snapshots={snapshotIndex} lines={totalLines} {DateTime.Now:O} ===");
            writer?.Flush();
            writer?.Dispose();
            writer = null;
            RefreshLatestCopy();
            Plugin.Log.Information(
                "Live capture finished ({Reason}). File: {Path}",
                reason,
                filePath ?? "(none)");
            Plugin.ChatGui.Print($"[DragonTyper] Capture done ({reason}). File saved.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Live capture finish failed");
        }
        finally
        {
            StopInternal(reason);
        }
    }

    private void StopInternal(string _)
    {
        active = false;
        try
        {
            writer?.Dispose();
        }
        catch
        {
            // ignore
        }

        writer = null;
    }
}
