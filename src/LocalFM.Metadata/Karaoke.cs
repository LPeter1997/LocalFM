namespace LocalFM.Metadata;

/// <summary>When a word of a line is sung and which characters it covers.</summary>
public readonly record struct WordTiming(TimeSpan Start, TimeSpan End, int CharStart, int CharLength);

/// <summary>
/// Word-by-word timing for synced lyrics. Word-synced (enhanced LRC) lyrics carry their own timings; lyrics
/// timed only by line can get an estimate that sweeps each line at a typical singing pace, never running past
/// the next line.
/// </summary>
public static class Karaoke
{
    /// <summary>Roughly 13 characters a second: a comfortable singing pace.</summary>
    public static readonly TimeSpan PerChar = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan MinLine = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan LastLineRoom = TimeSpan.FromSeconds(8);

    /// <summary>Timings for every line (empty when a line has none and <paramref name="estimate"/> is off).</summary>
    public static IReadOnlyList<WordTiming>[] Timings(IReadOnlyList<LyricLine> lines, bool estimate)
    {
        var result = new IReadOnlyList<WordTiming>[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            result[i] = [];
            if (line.Time is not { } start || line.Text.Length == 0) continue;
            TimeSpan? next = null;
            for (int j = i + 1; j < lines.Count; j++)
                if (lines[j].Time is { } t && t > start) { next = t; break; }
            var limit = next ?? start + LastLineRoom;

            if (line.Words.Count > 0) result[i] = Real(line, limit);
            else if (estimate) result[i] = Estimate(line.Text, start, limit);
        }
        return result;
    }

    private static List<WordTiming> Real(LyricLine line, TimeSpan limit)
    {
        var list = new List<WordTiming>(line.Words.Count);
        foreach (var w in line.Words)
        {
            // The last word often has no end tag: give it a sung length of its own, but stop at the next line.
            var end = w.End ?? Min(limit, w.Start + Max(TimeSpan.FromMilliseconds(300), PerChar * (w.CharLength * 1.5)));
            if (end <= w.Start) end = w.Start + TimeSpan.FromMilliseconds(50);
            list.Add(new WordTiming(w.Start, end, w.CharStart, w.CharLength));
        }
        return list;
    }

    private static List<WordTiming> Estimate(string text, TimeSpan start, TimeSpan limit)
    {
        var gap = limit - start;
        var sung = Min(gap * 0.9, Max(MinLine, PerChar * text.Length));
        var list = new List<WordTiming>();
        double perChar = sung.TotalSeconds / text.Length;
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            int from = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            if (i > from)
                list.Add(new WordTiming(start + TimeSpan.FromSeconds(from * perChar), start + TimeSpan.FromSeconds(i * perChar), from, i - from));
        }
        return list;
    }

    /// <summary>
    /// How many characters of the line have been sung at <paramref name="position"/>, with a fraction for the
    /// word being sung right now (so the highlight can sweep through it). 0 before the first word.
    /// </summary>
    public static double SungChars(IReadOnlyList<WordTiming> words, TimeSpan position)
    {
        double sung = 0;
        foreach (var w in words)
        {
            if (position < w.Start) break;
            if (position >= w.End) { sung = w.CharStart + w.CharLength; continue; }
            double f = (position - w.Start) / (w.End - w.Start);
            sung = w.CharStart + w.CharLength * Math.Clamp(f, 0, 1);
            break;
        }
        return sung;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
