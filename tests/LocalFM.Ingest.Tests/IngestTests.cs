using System.Diagnostics;
using System.IO.Compression;
using LocalFM.Core;
using LocalFM.Library;
using LocalFM.Library.Import;

namespace LocalFM.Ingest.Tests;

/// <summary>A throwaway LocalFM home (database, import folder) with an <see cref="ImportService"/>.</summary>
public sealed class TempHome : IDisposable
{
    public TempHome(IProcessRunner? runner = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "localfm-tests", "ingest-" + Guid.NewGuid().ToString("N")[..8]);
        Paths = new AppPaths(Path.Combine(Root, "home"));
        Db = new Database(Paths.Database);
        Images = new ImageStore(Paths.ImagesDir);
        Scanner = new Scanner(Db, Images);
        Watcher = new FolderWatcher(Scanner);
        Settings = new History(Db);
        Settings.SetAsync(ImportService.FolderSetting, Path.Combine(Root, "Music")).Wait();
        Runner = runner;
        Service = NewService();
    }

    public string Root { get; }
    public AppPaths Paths { get; }
    public Database Db { get; }
    public ImageStore Images { get; }
    public Scanner Scanner { get; }
    public FolderWatcher Watcher { get; }
    public History Settings { get; }
    public IProcessRunner? Runner { get; }
    public ImportService Service { get; private set; }

    public ImportService NewService() => Service = new ImportService(Db, Paths, Scanner, Watcher, Settings, Runner);

    public static bool HaveFfmpeg { get; } = Probe();
    private static bool Probe() { try { Ffmpeg("-version"); return true; } catch { return false; } }

    public static void Ffmpeg(string args)
    {
        var psi = new ProcessStartInfo("ffmpeg", "-hide_banner -loglevel error -y " + args) { RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"ffmpeg {args}: {err}");
    }

    public string MakeAudio(string path, Dictionary<string, string>? tags = null, int freq = 440)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string meta = string.Join(" ", (tags ?? []).Select(kv => $"-metadata \"{kv.Key}={kv.Value}\""));
        Ffmpeg($"-f lavfi -i \"sine=frequency={freq}:duration=1\" -ac 2 -map_metadata -1 {meta} \"{path}\"");
        return path;
    }

    public async Task<ImportJob> WaitForAsync(Func<ImportJob, bool> which)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (Service.Jobs.FirstOrDefault(which) is { Status: not (ImportStatus.Queued or ImportStatus.Running) } job) return job;
            await Task.Delay(50);
        }
        throw new TimeoutException("Import job did not finish");
    }

    public void Dispose()
    {
        Service.Dispose();
        Watcher.Dispose();
        Db.Dispose();
        try { Directory.Delete(Root, true); } catch { }
    }
}

/// <summary>Stands in for yt-dlp: canned JSON for -J, and a prepared file for downloads.</summary>
public sealed class FakeYtDlp : IProcessRunner
{
    public string ResolveJson { get; set; } = "{}";
    public string? DownloadSource { get; set; }
    public string DownloadJson { get; set; } = "{}";
    public List<IReadOnlyList<string>> Calls { get; } = [];

    public Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, Action<string, bool>? onLine = null, CancellationToken ct = default)
    {
        Calls.Add(args);
        if (args.Contains("--version")) return Task.FromResult(new ProcessResult(0, "2026.10.22\n", ""));
        if (args.Contains("-J")) return Task.FromResult(new ProcessResult(0, ResolveJson, "WARNING: No supported JavaScript runtime could be found\n"));

        string template = args[args.ToList().IndexOf("-o") + 1];
        string dir = Path.GetDirectoryName(template)!;
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, "vid00000001" + Path.GetExtension(DownloadSource));
        File.Copy(DownloadSource!, target, true);
        onLine?.Invoke("LFM 512 1024", true);
        onLine?.Invoke(target, false);
        onLine?.Invoke(DownloadJson, false);
        return Task.FromResult(new ProcessResult(0, "", ""));
    }
}

public class ParsingTests
{
    private const string MusicTrack = """
        {"id":"vid00000001","title":"Welcome to the Jungle","track":"Welcome to the Jungle","artist":"Guns N' Roses",
         "album":"Appetite for Destruction","release_year":1987,"duration":271.0,"channel":"Guns N' Roses - Topic",
         "webpage_url":"https://www.youtube.com/watch?v=vid00000001",
         "thumbnails":[{"url":"https://i.ytimg.com/vi/x/hq.jpg","width":480,"height":360},
                       {"url":"https://lh3.googleusercontent.com/a=w544-h544","width":544,"height":544}]}
        """;

    [Fact]
    public void Music_track_uses_youtube_music_fields()
    {
        var p = YtDlpProvider.ParsePreview(MusicTrack, "https://music.youtube.com/watch?v=vid00000001");
        var c = Assert.Single(p.Items);
        Assert.Equal("Welcome to the Jungle", c.Title);
        Assert.Equal("Guns N' Roses", c.Artist);
        Assert.Equal("Appetite for Destruction", c.Album);
        Assert.Equal("1987", c.Date);
        Assert.Equal(271_000, c.DurationMs);
        Assert.Equal("vid00000001", c.ExternalId);
        Assert.Equal("https://lh3.googleusercontent.com/a=w544-h544", c.ThumbnailUrl);
    }

    [Fact]
    public void Plain_video_title_is_split_and_cleaned()
    {
        const string json = """
            {"id":"abcDEF12345","title":"Scorpions - Wind Of Change (Official Music Video)","channel":"ScorpionsVEVO",
             "license":"Creative Commons Attribution license (reuse allowed)",
             "thumbnails":[{"url":"https://i.ytimg.com/vi/x/hq.jpg","width":480,"height":360}]}
            """;
        var c = Assert.Single(YtDlpProvider.ParsePreview(json, "u").Items);
        Assert.Equal("Scorpions", c.Artist);
        Assert.Equal("Wind Of Change", c.Title);
        Assert.Null(c.Album);
        Assert.Null(c.ThumbnailUrl); // a video still is not a cover
        Assert.Equal("CC BY", c.License);
    }

    [Fact]
    public void Music_album_playlist_numbers_tracks()
    {
        const string json = """
            {"_type":"playlist","id":"OLAK5uy_abc","title":"Album - Appetite for Destruction","channel":"Guns N' Roses - Topic",
             "entries":[{"id":"aaaaaaaaaa1","title":"Welcome to the Jungle","url":"https://www.youtube.com/watch?v=aaaaaaaaaa1","duration":271},
                        {"id":"aaaaaaaaaa2","title":"It's So Easy","url":"https://www.youtube.com/watch?v=aaaaaaaaaa2","duration":201}]}
            """;
        var p = YtDlpProvider.ParsePreview(json, "u");
        Assert.Equal("Appetite for Destruction · Guns N' Roses", p.Title);
        Assert.Equal(2, p.Items.Count);
        Assert.Equal(2, p.Items[1].TrackNumber);
        Assert.All(p.Items, c => Assert.Equal("Appetite for Destruction", c.Album));
        Assert.All(p.Items, c => Assert.Equal("Guns N' Roses", c.AlbumArtist));
        Assert.Equal("https://www.youtube.com/watch?v=aaaaaaaaaa2", p.Items[1].Url);
    }

    [Theory]
    [InlineData("LFM 50 200", 0.25)]
    [InlineData("LFM 200 200", 1.0)]
    [InlineData("LFM 10 NA", null)]
    [InlineData("[download] 10%", null)]
    public void Progress_lines(string line, double? expected) => Assert.Equal(expected, YtDlpProvider.ParseProgress(line));

    [Fact]
    public void Errors_come_from_the_last_error_line() =>
        Assert.Equal("Video unavailable", YtDlpProvider.LastError(new ProcessResult(1, "", "WARNING: x\nERROR: [youtube] abc: Video unavailable\n".Replace("[youtube] abc: ", ""))));
}

public class LayoutTests
{
    [Theory]
    [InlineData("AC/DC", "AC-DC")]
    [InlineData("What?  Now: \"yes\"", "What_ Now- _yes_")]
    [InlineData("CON", "_CON")]
    [InlineData("...", "_")]
    [InlineData("Trailing dot.", "Trailing dot")]
    public void Sanitize(string input, string expected) => Assert.Equal(expected, LibraryLayout.Sanitize(input));

    [Fact]
    public void Destination_uses_album_folders_and_keeps_the_video_id()
    {
        string root = Path.Combine(Path.GetTempPath(), "layout-" + Guid.NewGuid().ToString("N")[..6]);
        var album = new ImportCandidate(YtDlpProvider.ProviderId, "u", "Paradise City")
            { Artist = "Guns N' Roses", Album = "Appetite for Destruction", TrackNumber = 6, ExternalId = "abcDEF12345" };
        Assert.Equal(Path.Combine(root, "Guns N' Roses", "Appetite for Destruction", "06 Paradise City [abcDEF12345].m4a"),
            LibraryLayout.Destination(root, album, ".m4a"));

        var single = new ImportCandidate(ArchiveProvider.ProviderId, "f", "Song") { Artist = "Someone", TrackNumber = 3 };
        Assert.Equal(Path.Combine(root, "Someone", "Singles", "Song.flac"), LibraryLayout.Destination(root, single, ".flac"));
    }
}

public class ProviderTests
{
    [Fact]
    public async Task Missing_configured_ytdlp_is_reported()
    {
        var p = new YtDlpProvider(new FakeYtDlp(), () => "/nonexistent/yt-dlp");
        var s = await p.DetectAsync();
        Assert.False(s.Available);
        Assert.Contains("/nonexistent/yt-dlp", s.Note);
    }

    [Fact]
    public async Task Configured_ytdlp_reports_its_version()
    {
        string exe = Path.GetTempFileName();
        try
        {
            var s = await new YtDlpProvider(new FakeYtDlp(), () => exe).DetectAsync();
            Assert.True(s.Available);
            Assert.Equal("2026.10.22", s.Version);
        }
        finally { File.Delete(exe); }
    }

    [Fact]
    public void Links_go_to_ytdlp_and_paths_to_archives()
    {
        var yt = new YtDlpProvider(new FakeYtDlp(), () => null);
        var archive = new ArchiveProvider(Path.GetTempPath());
        Assert.True(yt.CanHandle("https://music.youtube.com/playlist?list=OLAK5uy_x"));
        Assert.False(yt.CanHandle("/home/me/music.zip"));
        Assert.False(archive.CanHandle("https://example.com/a.zip"));
        Assert.True(archive.CanHandle(Path.GetTempPath()));
    }

    [Fact]
    public void Zip_entries_cannot_escape_the_preview_folder()
    {
        string root = Path.Combine(Path.GetTempPath(), "zipslip-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(root);
        try
        {
            string zipPath = Path.Combine(root, "evil.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(zip.CreateEntry("../escaped.mp3").Open())) w.Write("x");
                using (var w = new StreamWriter(zip.CreateEntry("Album/01 ok.mp3").Open())) w.Write("x");
                using (var w = new StreamWriter(zip.CreateEntry("readme.txt").Open())) w.Write("x");
            }
            string previews = Path.Combine(root, "previews");
            string dir = new ArchiveProvider(previews).Extract(zipPath, default);
            Assert.False(File.Exists(Path.Combine(previews, "escaped.mp3")));
            Assert.True(File.Exists(Path.Combine(dir, "Album", "01 ok.mp3")));
            Assert.False(File.Exists(Path.Combine(dir, "readme.txt")));
        }
        finally { Directory.Delete(root, true); }
    }
}

public class ServiceTests
{
    private const string Download = """
        {"id":"vid00000001","title":"Nightrain","track":"Nightrain","artist":"Guns N' Roses","album":"Appetite for Destruction",
         "release_year":1987,"webpage_url":"https://www.youtube.com/watch?v=vid00000001"}
        """;

    [SkippableFact]
    public async Task Ytdlp_download_lands_in_the_library_with_its_metadata()
    {
        Skip.IfNot(TempHome.HaveFfmpeg, "ffmpeg CLI needed to make test audio");
        var fake = new FakeYtDlp
        {
            ResolveJson = """{"id":"vid00000001","title":"Nightrain","webpage_url":"https://www.youtube.com/watch?v=vid00000001"}""",
            DownloadJson = Download.ReplaceLineEndings(""),
        };
        using var home = new TempHome(fake);
        fake.DownloadSource = home.MakeAudio(Path.Combine(home.Root, "src", "untagged.mp3")); // no tags at all
        string exe = Path.Combine(home.Root, "yt-dlp");
        File.WriteAllText(exe, "");
        await home.Settings.SetAsync(ImportService.YtDlpPathSetting, exe);

        var preview = await home.Service.ResolveAsync("https://www.youtube.com/watch?v=vid00000001");
        Assert.Equal("No supported JavaScript runtime could be found", preview.Note);
        var candidate = Assert.Single(preview.Items);
        Assert.False(candidate.AlreadyInLibrary);

        await home.Service.EnqueueAsync([candidate]);
        var job = await home.WaitForAsync(_ => true);
        Assert.Equal(ImportStatus.Done, job.Status);
        Assert.Equal(Path.Combine(home.Root, "Music", "Guns N' Roses", "Appetite for Destruction", "Nightrain [vid00000001].mp3"), job.FilePath);
        Assert.True(File.Exists(job.FilePath));
        Assert.False(Directory.Exists(Path.Combine(home.Service.StagingRoot, job.Id.ToString())));

        using (var c = home.Db.Open())
        {
            var row = c.QuerySingle("""
                SELECT t.title, ac.display_name, r.title FROM audio_file f
                JOIN track t ON t.id = f.track_id JOIN medium m ON m.id = t.medium_id JOIN release r ON r.id = m.release_id
                JOIN artist_credit ac ON ac.id = t.artist_credit_id WHERE f.path = $1
                """, r => (Title: r.GetString(0), Artist: r.GetString(1), Album: r.GetString(2)), job.FilePath!);
            Assert.Equal(("Nightrain", "Guns N' Roses", "Appetite for Destruction"), row);
            Assert.Equal(1, c.Scalar<long>("SELECT COUNT(*) FROM external_id WHERE provider = 'youtube' AND entity_type = 'recording'"));
            Assert.Equal(1, c.Scalar<long>("SELECT COUNT(*) FROM attribute WHERE key = 'source_url'"));
        }

        // The same video again is recognised.
        var again = await home.Service.ResolveAsync("https://www.youtube.com/watch?v=vid00000001");
        Assert.True(Assert.Single(again.Items).AlreadyInLibrary);
    }

    [SkippableFact]
    public async Task A_files_own_tags_win_over_the_hint()
    {
        Skip.IfNot(TempHome.HaveFfmpeg, "ffmpeg CLI needed to make test audio");
        using var home = new TempHome();
        var source = await home.Service.EnsureSourceAsync();
        string path = home.MakeAudio(Path.Combine(source.Path, "a.mp3"), new() { ["title"] = "Real Title", ["artist"] = "Real Artist" });
        await ImportHints.PutAsync(home.Db, new ImportHint(path, "ytdlp", "zzzzzzzzzz1", null, "Hint Title", "Hint Artist", null, "Hint Album", null, null));
        await home.Scanner.ScanAsync(source);

        using var c = home.Db.Open();
        Assert.Equal("Real Title", c.Scalar<string>("SELECT t.title FROM audio_file f JOIN track t ON t.id = f.track_id"));
        Assert.Equal("Hint Album", c.Scalar<string>("SELECT title FROM release"));
    }

    [SkippableFact]
    public async Task Archive_import_files_by_tags_with_the_cover()
    {
        Skip.IfNot(TempHome.HaveFfmpeg, "ffmpeg CLI needed to make test audio");
        using var home = new TempHome();
        // A Takeout-style zip with a tagged track and a cover.
        string src = Path.Combine(home.Root, "zipsrc", "Takeout", "YouTube and YouTube Music", "music-uploads");
        home.MakeAudio(Path.Combine(src, "upload.mp3"), new() { ["title"] = "Song", ["artist"] = "Band", ["album"] = "Record", ["track"] = "2" });
        TempHome.Ffmpeg($"-f lavfi -i \"color=c=red:s=64x64:d=1\" -frames:v 1 \"{Path.Combine(src, "cover.jpg")}\"");
        string zipPath = Path.Combine(home.Root, "takeout.zip");
        ZipFile.CreateFromDirectory(Path.Combine(home.Root, "zipsrc"), zipPath);

        var preview = await home.Service.ResolveAsync(zipPath);
        Assert.Equal("Song", Assert.Single(preview.Items).Title);
        await home.Service.EnqueueAsync(preview.Items);
        var job = await home.WaitForAsync(_ => true);

        Assert.Equal(ImportStatus.Done, job.Status);
        string dir = Path.Combine(home.Root, "Music", "Band", "Record");
        Assert.Equal(Path.Combine(dir, "02 Song.mp3"), job.FilePath);
        Assert.True(File.Exists(Path.Combine(dir, "cover.jpg")));
        using var c = home.Db.Open();
        Assert.Equal(1, c.Scalar<long>("SELECT COUNT(*) FROM entity_image WHERE role = 'front' AND source = 'folder'"));
    }

    [Fact]
    public async Task Queue_survives_a_restart()
    {
        using var home = new TempHome(new FakeYtDlp());
        await home.Settings.SetAsync(ImportService.YtDlpPathSetting, "/nonexistent/yt-dlp");
        await home.Service.EnqueueAsync([new ImportCandidate(YtDlpProvider.ProviderId, "https://example.com/v", "Lost Song") { ExternalId = "x" }]);
        var failed = await home.WaitForAsync(_ => true);
        Assert.Equal(ImportStatus.Failed, failed.Status);
        Assert.Equal("yt-dlp is not installed.", failed.Error);

        home.Service.Dispose();
        var fresh = home.NewService();
        fresh.Start();
        var sw = Stopwatch.StartNew();
        while (fresh.Jobs.Count == 0 && sw.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
        var job = Assert.Single(fresh.Jobs);
        Assert.Equal(ImportStatus.Failed, job.Status);
        Assert.Equal("Lost Song", job.Candidate.Title);
        Assert.Equal("https://example.com/v", job.Candidate.Url);

        await fresh.ClearFinishedAsync();
        Assert.Empty(fresh.Jobs);
    }
}
