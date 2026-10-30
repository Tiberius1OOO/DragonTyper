using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Game;
using Lumina.Excel.Sheets;

namespace DragonTyper;

/// <summary>
/// KTGTypeWordTextData helpers: preferred typed form per client language,
/// optional-punctuation stripping (EN/DE/FR), JP reading column.
/// </summary>
internal sealed class WordBook
{
    private readonly Dictionary<string, string> typedByAny = new(StringComparer.OrdinalIgnoreCase);
    private bool loaded;

    public void EnsureLoaded()
    {
        if (loaded)
            return;
        loaded = true;
        try
        {
            var lang = Plugin.ClientState.ClientLanguage;
            var sheet = Plugin.DataManager.GetExcelSheet<KTGTypeWordTextData>(lang)
                        ?? Plugin.DataManager.GetExcelSheet<KTGTypeWordTextData>();
            if (sheet == null)
                return;

            var japanese = lang == ClientLanguage.Japanese;
            foreach (var row in sheet)
            {
                var display = KtgWordSlots.DecodeSeString(row.Unknown0.ToString());
                var reading = KtgWordSlots.DecodeSeString(row.Unknown1.ToString());
                if (string.IsNullOrWhiteSpace(display) && string.IsNullOrWhiteSpace(reading))
                    continue;

                var typed = japanese
                    ? (!string.IsNullOrWhiteSpace(reading) ? reading : display)
                    : (!string.IsNullOrWhiteSpace(display) ? display : reading);
                if (string.IsNullOrWhiteSpace(typed))
                    continue;

                typed = typed.Trim();
                Register(display, typed);
                Register(reading, typed);
                Register(StripOptionalPunct(display), typed);
                Register(StripOptionalPunct(reading), typed);
                Register(typed, typed);
            }

            Plugin.Log.Information("WordBook loaded {Count} typed forms (lang={Lang})", typedByAny.Count, lang);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "WordBook load failed");
        }
    }

    private void Register(string? key, string typed)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;
        key = key.Trim();
        if (key.Length is < 2 or > 80)
            return;
        typedByAny.TryAdd(key, typed);
    }

    /// <summary>Map on-screen label to the string we should submit.</summary>
    public string ToTypedForm(string? onScreen)
    {
        EnsureLoaded();
        onScreen = KtgWordSlots.DecodeSeString(onScreen);
        if (string.IsNullOrWhiteSpace(onScreen))
            return string.Empty;

        onScreen = onScreen.Trim();
        if (typedByAny.TryGetValue(onScreen, out var typed))
            return typed;

        var stripped = StripOptionalPunct(onScreen);
        if (typedByAny.TryGetValue(stripped, out typed))
            return typed;

        // Client accepts optional punctuation omitted.
        return Plugin.ClientState.ClientLanguage == ClientLanguage.Japanese
            ? onScreen
            : stripped.Length >= 2 ? stripped : onScreen;
    }

    public static string StripOptionalPunct(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var sb = new StringBuilder(input.Length);
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            // French " !" → drop the space before stripped punct.
            if (c == ' ' && i + 1 < input.Length
                && input[i + 1] is '!' or '?' or '！' or '？')
                continue;

            if (c is '!' or '?' or '\'' or '-' or '’' or '‘'
                or '！' or '？' or '、' or '。' or '「' or '」' or '・' or '～' or '〜'
                or '\u2010' or '\u2011' or '\u2012' or '\u2013')
                continue;

            sb.Append(c);
        }

        return sb.ToString().Trim();
    }
}
