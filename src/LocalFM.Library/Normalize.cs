using System.Globalization;
using System.Text;

namespace LocalFM.Library;

/// <summary>Matching keys for names/titles: case-, accent-, punctuation- and whitespace-insensitive.</summary>
public static class Normalize
{
    public static string Key(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        string d = s.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(d.Length);
        bool space = false;
        foreach (char c in d)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c))
            {
                if (space && sb.Length > 0) sb.Append(' ');
                sb.Append(char.ToLowerInvariant(c));
                space = false;
            }
            else if (c == '&') { if (sb.Length > 0) sb.Append(' '); sb.Append("and"); space = true; }
            else space = true;
        }
        return sb.ToString();
    }

    /// <summary>Clean display text: trimmed, inner whitespace collapsed, null when empty.</summary>
    public static string? Text(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var sb = new StringBuilder(s.Length);
        bool ws = false;
        foreach (char c in s.Trim())
        {
            if (char.IsWhiteSpace(c)) { ws = true; continue; }
            if (ws) sb.Append(' ');
            sb.Append(c);
            ws = false;
        }
        return sb.ToString();
    }
}
