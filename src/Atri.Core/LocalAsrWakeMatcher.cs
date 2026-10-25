using System.Globalization;
using System.Text;

namespace Atri.Core;

public sealed record LocalWakeMatch(bool Matched, string Phrase, string Command);

/// <summary>Controlled wake-prefix matching; mentions later in conversation do not wake the app.</summary>
public static class LocalAsrWakeMatcher
{
    public static readonly string[] DefaultPhrases = ["亚托莉", "アトリ", "亚托丽", "阿托莉"];
    private static readonly string[] Greetings = ["", "你好", "嗨", "嘿", "喂", "ねえ", "ねぇ", "おい"];

    private static (string Text, List<int> Ends) Normalize(string input)
    {
        var text = new StringBuilder(); var ends = new List<int>();
        var sourceEnd = 0;
        foreach (var rune in input.EnumerateRunes())
        {
            sourceEnd += rune.Utf16SequenceLength;
            var part = rune.ToString().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
            foreach (var character in part.EnumerateRunes())
            {
                if (!Rune.IsLetterOrDigit(character)) continue;
                text.Append(character.ToString());
                for (var count = 0; count < character.Utf16SequenceLength; count++) ends.Add(sourceEnd);
            }
        }
        return (text.ToString(), ends);
    }

    public static LocalWakeMatch Match(string transcript, IEnumerable<string>? phrases = null)
    {
        if (string.IsNullOrWhiteSpace(transcript) || transcript.Length > 12000) return new(false, "", "");
        var normalized = Normalize(transcript);
        foreach (var phrase in (phrases ?? DefaultPhrases).Where(p => p.Length is > 0 and <= 40).Take(32))
        {
            var wake = Normalize(phrase).Text;
            if (wake.Length == 0) continue;
            foreach (var greeting in Greetings)
            {
                var prefix = greeting + wake;
                if (!normalized.Text.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (wake == "アトリ" && (normalized.Text.StartsWith(greeting + "アトリエ", StringComparison.Ordinal) || normalized.Text.StartsWith(greeting + "アトリウム", StringComparison.Ordinal))) continue;
                var after = normalized.Ends[prefix.Length - 1];
                var command = transcript[after..].TrimStart(' ', '\t', '\r', '\n', '、', ',', '，', '。', '！', '!', '?', '？', '：', ':');
                return new(true, phrase, command);
            }
        }
        return new(false, "", "");
    }
}
