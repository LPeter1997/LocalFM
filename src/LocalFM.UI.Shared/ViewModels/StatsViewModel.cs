using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalFM.Library;
using LocalFM.UI.Controls;

namespace LocalFM.UI.ViewModels;

public sealed record StatsPeriodOption(string Name, Func<DateTimeOffset, DateTimeOffset?> From, string PreviousName);

public sealed record StatTile(string Value, string Label, string? Note = null);

public sealed class TopRow(int rank, TopItem item, long best, bool byTime)
{
    public int Rank { get; } = rank;
    public TopItem Item { get; } = item;
    public string Title => Item.Title;
    public string? Subtitle => Item.Subtitle;
    public string? CoverPath => Item.CoverPath;
    public string Amount => byTime ? StatsViewModel.Duration(Item.ListenedMs) : $"{Item.Plays:N0} play{(Item.Plays == 1 ? "" : "s")}";
    public string Detail => byTime ? $"{Item.Plays:N0} play{(Item.Plays == 1 ? "" : "s")}" : StatsViewModel.Duration(Item.ListenedMs);
    public double Share { get; } = best <= 0 ? 0 : (byTime ? item.ListenedMs : item.Plays) / (double)best;
}

public sealed class RecentRow(RecentPlay p)
{
    public RecentPlay Play { get; } = p;
    public string Title => Play.Title;
    public string? Artist => Play.Artist;
    public string? CoverPath => Play.CoverPath;
    public string When => StatsViewModel.Ago(Play.StartedAt);
    public string Note => Play.Counted ? StatsViewModel.Duration(Play.ListenedMs) : "skipped";
}

/// <summary>Listening statistics, all computed from the local play log.</summary>
public sealed partial class StatsViewModel(MainViewModel main) : PageViewModel(main)
{
    public override string Title => "Stats";

    public IReadOnlyList<StatsPeriodOption> Periods { get; } =
    [
        new("Last 7 days", now => now.AddDays(-7), "previous 7 days"),
        new("Last 30 days", now => now.AddDays(-30), "previous 30 days"),
        new("Last 12 months", now => now.AddYears(-1), "previous 12 months"),
        new("This year", now => new DateTimeOffset(now.LocalDateTime.Year, 1, 1, 0, 0, 0, now.Offset), "same span before"),
        new("All time", _ => null, ""),
    ];

    [ObservableProperty] private StatsPeriodOption? _period;
    [ObservableProperty] private bool _rankByTime;
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private string? _emptyText;
    [ObservableProperty] private ChartData? _timeline;
    [ObservableProperty] private ChartData? _hours;
    [ObservableProperty] private ChartData? _weekdays;
    [ObservableProperty] private string? _timelineTitle;
    [ObservableProperty] private string? _coverage;
    [ObservableProperty] private double _coverageShare;
    [ObservableProperty] private string? _habit;

    public ObservableCollection<StatTile> Tiles { get; } = new();
    public ObservableCollection<TopRow> TopTracks { get; } = new();
    public ObservableCollection<TopRow> TopArtists { get; } = new();
    public ObservableCollection<TopRow> TopAlbums { get; } = new();
    public ObservableCollection<TopRow> Forgotten { get; } = new();
    public ObservableCollection<RecentRow> Recent { get; } = new();

    public bool HasForgotten => Forgotten.Count > 0;

    private StatsReport? _report;

    partial void OnPeriodChanged(StatsPeriodOption? value) => _ = RefreshAsync();
    partial void OnRankByTimeChanged(bool value) { if (_report is { } r) ApplyTops(r); }

    public override Task RefreshAsync()
    {
        var period = Period ??= Periods[1];
        var now = DateTimeOffset.Now;
        var from = period.From(now);
        return Load(() => ListeningStats.Build(Host.Db, Host.Images, from, now.AddSeconds(1)), r => Apply(r, period));
    }

    private void Apply(StatsReport r, StatsPeriodOption period)
    {
        _report = r;
        var s = r.Summary;
        HasData = s.Plays + s.Skips > 0;
        EmptyText = HasData ? null : period.From(DateTimeOffset.Now) is null
            ? "Nothing played yet. Statistics build up as you listen."
            : $"Nothing played in the {period.Name.ToLowerInvariant()}.";

        string? change = null;
        if (s.PreviousListenedMs is { } prev && prev > 0)
        {
            double pct = (s.ListenedMs - prev) * 100.0 / prev;
            change = $"{(pct >= 0 ? "▲" : "▼")} {Math.Abs(pct):0}% vs {period.PreviousName}";
        }
        else if (s.PreviousListenedMs == 0 && s.ListenedMs > 0) change = $"nothing in the {period.PreviousName}";

        Replace(Tiles,
        [
            new StatTile(Duration(s.ListenedMs), "listening time", change),
            new StatTile($"{s.Plays:N0}", "plays", s.Skips > 0 ? $"{s.Skips:N0} skipped" : null),
            new StatTile($"{s.Tracks:N0}", "different tracks", s.NewTracks > 0 ? $"{s.NewTracks:N0} heard for the first time" : null),
            new StatTile($"{s.Artists:N0}", "artists", $"{s.Albums:N0} album{(s.Albums == 1 ? "" : "s")}"),
            new StatTile($"{s.CurrentStreakDays:N0} day{(s.CurrentStreakDays == 1 ? "" : "s")}", "listening streak",
                $"longest {s.LongestStreakDays:N0} day{(s.LongestStreakDays == 1 ? "" : "s")}"),
        ]);

        Coverage = s.LibraryTracks == 0 ? null
            : $"You have played {s.LibraryTracksPlayed:N0} of your {s.LibraryTracks:N0} tracks ({s.LibraryTracksPlayed * 100.0 / s.LibraryTracks:0}%) at least once.";
        CoverageShare = s.LibraryTracks == 0 ? 0 : (double)s.LibraryTracksPlayed / s.LibraryTracks;

        TimelineTitle = r.TimelineByMonth ? "Listening per month" : "Listening per day";
        int every = Math.Max(1, (int)Math.Ceiling(r.Timeline.Count / 12.0));
        Timeline = new ChartData(
            r.Timeline.Select(b => b.ListenedMs / 60000.0).ToList(),
            r.Timeline.Select((b, i) => (r.Timeline.Count - 1 - i) % every == 0 ? (r.TimelineByMonth ? b.Start.ToString("MMM") : b.Start.ToString("d MMM")) : "").ToList(),
            r.Timeline.Select(b => $"{b.Label}: {Duration(b.ListenedMs)}, {b.Plays} play{(b.Plays == 1 ? "" : "s")}").ToList());
        Hours = new ChartData(
            r.ByHour.Select(ms => ms / 60000.0).ToList(),
            Enumerable.Range(0, 24).Select(h => h % 6 == 0 ? $"{h}:00" : "").ToList(),
            r.ByHour.Select((ms, h) => $"{h}:00–{h + 1}:00: {Duration(ms)}").ToList());
        string[] days = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
        Weekdays = new ChartData(
            r.ByWeekday.Select(ms => ms / 60000.0).ToList(), days,
            r.ByWeekday.Select((ms, i) => $"{days[i]}: {Duration(ms)}").ToList());

        Habit = HasData ? Describe(r) : null;
        ApplyTops(r);
        Replace(Recent, r.Recent.Take(12).Select(p => new RecentRow(p)));
    }

    private void ApplyTops(StatsReport r)
    {
        IEnumerable<TopRow> Rows(IReadOnlyList<TopItem> items)
        {
            var ordered = RankByTime ? items.OrderByDescending(i => i.ListenedMs).ToList() : items.ToList();
            long best = ordered.Count == 0 ? 0 : RankByTime ? ordered[0].ListenedMs : ordered[0].Plays;
            return ordered.Select((t, i) => new TopRow(i + 1, t, best, RankByTime));
        }
        Replace(TopTracks, Rows(r.TopTracks));
        Replace(TopArtists, Rows(r.TopArtists));
        Replace(TopAlbums, Rows(r.TopAlbums));
        Replace(Forgotten, r.Forgotten.Select((t, i) => new TopRow(i + 1, t, r.Forgotten[0].Plays, false)));
        OnPropertyChanged(nameof(HasForgotten));
    }

    /// <summary>One plain sentence about when the listening happens.</summary>
    private static string? Describe(StatsReport r)
    {
        long total = r.ByHour.Sum();
        if (total == 0) return null;
        int peak = Enumerable.Range(0, 24).MaxBy(h => r.ByHour[h]);
        string part = peak switch { < 5 => "late at night", < 12 => "in the morning", < 17 => "in the afternoon", < 22 => "in the evening", _ => "late at night" };
        string[] names = ["Mondays", "Tuesdays", "Wednesdays", "Thursdays", "Fridays", "Saturdays", "Sundays"];
        int day = Enumerable.Range(0, 7).MaxBy(d => r.ByWeekday[d]);
        return $"You listen most {part} (around {peak}:00), and most on {names[day]}.";
    }

    // ------------------------------------------------------------------ actions

    [RelayCommand]
    private void Open(TopRow row)
    {
        if (TopArtists.Contains(row))
        {
            if (Host.Queries.Artists().FirstOrDefault(a => a.ArtistId == row.Item.Id) is { } artist) Main.ShowArtist(artist);
        }
        else if (TopAlbums.Contains(row))
        {
            if (Host.Queries.Album(row.Item.Id) is { } album) Main.ShowAlbum(album);
        }
        else PlayRecordings([row.Item.Id]);
    }

    [RelayCommand]
    private void PlayRecent(RecentRow row) => PlayRecordings([row.Play.RecordingId]);

    /// <summary>Plays the top tracks of the period as a mix.</summary>
    [RelayCommand]
    private void PlayTopTracks() => PlayRecordings(TopTracks.Select(t => t.Item.Id).ToList());

    [RelayCommand]
    private void PlayForgotten() => PlayRecordings(Forgotten.Select(t => t.Item.Id).ToList());

    private void PlayRecordings(IReadOnlyList<LocalFM.Core.Ulid> ids)
    {
        var tracks = Host.Queries.ByRecordings(ids);
        // Keep the ranking order, not the library order.
        var order = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        var list = tracks.GroupBy(t => t.RecordingId).Select(g => g.First()).OrderBy(t => order.GetValueOrDefault(t.RecordingId, int.MaxValue)).ToList();
        if (list.Count == 0) { Main.Notify("Those tracks are not in the library any more."); return; }
        Host.Player.PlayTracks(list);
    }

    // ------------------------------------------------------------------ formatting

    public static string Duration(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        if (t.TotalMinutes < 1) return $"{t.Seconds} s";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes} min";
        if (t.TotalHours < 100) return $"{(int)t.TotalHours} h {t.Minutes} min";
        return $"{t.TotalHours:N0} h";
    }

    public static string Ago(DateTimeOffset at)
    {
        var d = DateTimeOffset.Now - at;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        var local = at.ToLocalTime();
        if (local.Date == DateTime.Today) return $"today {local:HH:mm}";
        if (local.Date == DateTime.Today.AddDays(-1)) return $"yesterday {local:HH:mm}";
        if (d.TotalDays < 7) return local.ToString("dddd HH:mm");
        return local.ToString("d MMM yyyy");
    }
}
