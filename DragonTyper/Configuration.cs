using Dalamud.Configuration;

namespace DragonTyper;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Master switch. Must be turned on before any typing happens.</summary>
    public bool Active { get; set; }

    /// <summary>Base wait before submitting a word, in milliseconds.</summary>
    public int WordDelayMs { get; set; } = 1250;

    /// <summary>Wait longer (up to 2x, capped) for long sentences.</summary>
    public bool LongSentenceDelay { get; set; } = true;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
