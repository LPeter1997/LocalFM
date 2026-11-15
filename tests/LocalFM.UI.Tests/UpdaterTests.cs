using LocalFM.UI.Controls;
using LocalFM.UI.Updates;
using LocalFM.UI.ViewModels;

namespace LocalFM.UI.Tests;

/// <summary>The update banner's flow (ported from CheeseGit), against a fake release source and a fake app.</summary>
public sealed class UpdaterTests
{
    private sealed class FakeUpdates(UpdateMode mode = UpdateMode.InApp) : IUpdateService
    {
        public UpdateMode Mode => mode;
        public AvailableUpdate? Latest { get; set; } = new("1.2.0", "- New things", "https://example.invalid/1.2.0");
        public Exception? CheckFails { get; set; }
        public Exception? DownloadFails { get; set; }
        public List<string> Calls { get; } = [];

        public Task<AvailableUpdate?> CheckAsync(CancellationToken ct = default)
        {
            Calls.Add("check");
            return CheckFails is { } e ? Task.FromException<AvailableUpdate?>(e) : Task.FromResult(Latest);
        }

        public Task DownloadAsync(AvailableUpdate update, Action<int> progress, CancellationToken ct = default)
        {
            Calls.Add("download " + update.Version);
            return DownloadFails is { } e ? Task.FromException(e) : Task.CompletedTask;
        }

        public void ApplyAndRestart(AvailableUpdate update) => Calls.Add("restart " + update.Version);
        public void ApplyOnExit(AvailableUpdate update) => Calls.Add("on-quit " + update.Version);
    }

    private sealed class FakeHost : IUpdaterHost
    {
        public Dictionary<string, string> Store { get; } = new();
        public List<string> Notices { get; } = [];
        public List<string> Calls { get; } = [];
        public bool IsPlaying { get; set; }
        public bool InVibe { get; set; }

        public string? Get(string key) => Store.GetValueOrDefault(key);
        public void Set(string key, string value) => Store[key] = value;
        public void Notify(string message) => Notices.Add(message);

        public Task PrepareRestartAsync()
        {
            Calls.Add("prepare");
            Store[UpdaterViewModel.ResumeKey] = IsPlaying ? "1" : "";
            return Task.CompletedTask;
        }

        public Task OpenUrlAsync(string url)
        {
            Calls.Add("open " + url);
            return Task.CompletedTask;
        }
    }

    private static UpdaterViewModel Create(FakeHost host, FakeUpdates updates, string version = "1.1.0") => new(host, updates, version);

    [Fact]
    public async Task Startup_check_offers_a_newer_version()
    {
        var vm = Create(new FakeHost(), new FakeUpdates());
        await vm.CheckOnStartAsync();
        Assert.True(vm.ShowBanner);
        Assert.Equal("LocalFM 1.2.0 is available", vm.BannerText);
        Assert.True(vm.CanRestart);
    }

    [Fact]
    public async Task Startup_check_respects_the_setting_and_the_skipped_version()
    {
        var off = new FakeUpdates();
        var host = new FakeHost();
        host.Store[UpdaterViewModel.CheckKey] = "0";
        await Create(host, off).CheckOnStartAsync();
        Assert.Empty(off.Calls);

        var skipped = new FakeUpdates();
        var host2 = new FakeHost();
        host2.Store[UpdaterViewModel.SkippedKey] = "1.2.0";
        var vm = Create(host2, skipped);
        await vm.CheckOnStartAsync();
        Assert.False(vm.ShowBanner);
        await vm.CheckNowCommand.ExecuteAsync(null); // asked for: offered anyway
        Assert.True(vm.ShowBanner);
    }

    [Fact]
    public async Task A_failed_startup_check_is_silent_but_a_manual_one_says_why()
    {
        var updates = new FakeUpdates { CheckFails = new HttpRequestException("offline") };
        var host = new FakeHost();
        var vm = Create(host, updates);
        await vm.CheckOnStartAsync();
        Assert.Empty(host.Notices);
        Assert.Null(vm.StatusText);
        await vm.CheckNowCommand.ExecuteAsync(null);
        Assert.Equal("Could not check for updates: offline", vm.StatusText);

        updates.CheckFails = null;
        updates.Latest = null;
        await vm.CheckNowCommand.ExecuteAsync(null);
        Assert.Equal("LocalFM 1.1.0 is up to date.", vm.StatusText);
    }

    [Fact]
    public async Task When_I_quit_downloads_keeps_the_notes_and_hands_over_to_velopack()
    {
        var updates = new FakeUpdates();
        var host = new FakeHost();
        var vm = Create(host, updates);
        await vm.CheckOnStartAsync();
        await vm.UpdateOnQuitCommand.ExecuteAsync(null);
        Assert.Equal(["check", "download 1.2.0", "on-quit 1.2.0"], updates.Calls);
        Assert.False(vm.ShowBanner);
        Assert.True(vm.ReadyOnExit);
        Assert.Equal("- New things", host.Store[UpdaterViewModel.NotesKey]);
        Assert.Equal("1.2.0", host.Store[UpdaterViewModel.NotesVersionKey]);
        Assert.Empty(host.Calls); // the music plays on
        await vm.CheckNowCommand.ExecuteAsync(null);
        Assert.False(vm.ShowBanner); // not offered twice
    }

    [Fact]
    public async Task Restart_now_saves_the_music_first_and_asks_to_resume()
    {
        var updates = new FakeUpdates();
        var host = new FakeHost { IsPlaying = true };
        var vm = Create(host, updates);
        await vm.CheckOnStartAsync();
        await vm.RestartNowCommand.ExecuteAsync(null);
        Assert.Equal(["prepare"], host.Calls);
        Assert.Equal("1", host.Store[UpdaterViewModel.ResumeKey]);
        Assert.Equal("restart 1.2.0", updates.Calls[^1]);
    }

    [Fact]
    public async Task Restart_is_not_offered_in_a_vibe()
    {
        var updates = new FakeUpdates();
        var host = new FakeHost { InVibe = true };
        var vm = Create(host, updates);
        await vm.CheckOnStartAsync();
        Assert.True(vm.ShowBanner);
        Assert.False(vm.CanRestart);
        await vm.RestartNowCommand.ExecuteAsync(null);
        Assert.DoesNotContain(updates.Calls, c => c.StartsWith("restart"));

        host.InVibe = false;
        vm.VibeChanged();
        Assert.True(vm.CanRestart);
    }

    [Fact]
    public async Task A_failed_download_keeps_the_banner_and_says_why()
    {
        var updates = new FakeUpdates { DownloadFails = new IOException("disk full") };
        var host = new FakeHost();
        var vm = Create(host, updates);
        await vm.CheckOnStartAsync();
        await vm.UpdateOnQuitCommand.ExecuteAsync(null);
        Assert.True(vm.ShowBanner);
        Assert.False(vm.IsDownloading);
        Assert.Equal("Could not download the update: disk full", Assert.Single(host.Notices));
        Assert.DoesNotContain(UpdaterViewModel.NotesVersionKey, host.Store.Keys);
    }

    [Fact]
    public async Task Skip_remembers_the_version_and_dismiss_does_not()
    {
        var host = new FakeHost();
        var vm = Create(host, new FakeUpdates());
        await vm.CheckOnStartAsync();
        vm.DismissCommand.Execute(null);
        Assert.False(vm.ShowBanner);
        Assert.DoesNotContain(UpdaterViewModel.SkippedKey, host.Store.Keys);
        await vm.CheckOnStartAsync();
        vm.SkipCommand.Execute(null);
        Assert.Equal("1.2.0", host.Store[UpdaterViewModel.SkippedKey]);
    }

    [Fact]
    public async Task Notice_only_builds_never_download()
    {
        var updates = new FakeUpdates(UpdateMode.NoticeOnly);
        var host = new FakeHost();
        var vm = Create(host, updates);
        await vm.CheckOnStartAsync();
        Assert.True(vm.IsNoticeOnly);
        Assert.False(vm.CanRestart);
        await vm.UpdateOnQuitCommand.ExecuteAsync(null);
        await vm.OpenReleasePageCommand.ExecuteAsync(null);
        Assert.Equal(["check"], updates.Calls);
        Assert.Equal(["open https://example.invalid/1.2.0"], host.Calls);
    }

    [Fact]
    public async Task Uninstalled_copies_never_check()
    {
        var updates = new FakeUpdates(UpdateMode.None);
        var vm = Create(new FakeHost(), updates);
        await vm.CheckOnStartAsync();
        Assert.False(vm.CanCheck);
        Assert.False(vm.CheckNowCommand.CanExecute(null));
        Assert.Empty(updates.Calls);
    }

    [Fact]
    public void Whats_new_shows_once_for_the_installed_version()
    {
        var host = new FakeHost();
        host.Store[UpdaterViewModel.NotesKey] = "- New things";
        host.Store[UpdaterViewModel.NotesVersionKey] = "1.2.0";
        var vm = Create(host, new FakeUpdates(), "1.2.0");
        vm.ShowWhatsNewIfUpdated();
        Assert.True(vm.WhatsNewOpen);
        Assert.Equal("- New things", vm.WhatsNew);
        Assert.Equal("What's new in 1.2.0", vm.WhatsNewTitle);

        var again = Create(host, new FakeUpdates(), "1.2.0");
        again.ShowWhatsNewIfUpdated();
        Assert.False(again.WhatsNewOpen);
    }

    [Fact]
    public void Whats_new_waits_for_the_install_and_drops_stale_notes()
    {
        var host = new FakeHost();
        host.Store[UpdaterViewModel.NotesKey] = "- New things";
        host.Store[UpdaterViewModel.NotesVersionKey] = "1.2.0";
        Create(host, new FakeUpdates(), "1.1.0").ShowWhatsNewIfUpdated(); // not installed yet (still waiting for the quit)
        Assert.Equal("1.2.0", host.Store[UpdaterViewModel.NotesVersionKey]);

        var later = Create(host, new FakeUpdates(), "1.3.0"); // 1.2.0 never installed; a later version did
        later.ShowWhatsNewIfUpdated();
        Assert.False(later.WhatsNewOpen);
        Assert.Equal("", host.Store[UpdaterViewModel.NotesVersionKey]);
    }

    [Theory]
    [InlineData("1.2.0", "1.1.9", 1)]
    [InlineData("v1.2.0", "1.2.0", 0)]
    [InlineData("1.10.0", "1.9.0", 1)]
    [InlineData("1.2.0-beta.1", "1.2.0", -1)]
    [InlineData("1.2.0-beta.2", "1.2.0-beta.1", 1)]
    [InlineData("1.2", "1.2.0", 0)]
    [InlineData("2", "1.9.9", 1)]
    [InlineData("1.2.0+abc", "1.2.0", 0)]
    [InlineData("nightly", "1.0.0", -1)]
    [InlineData("1.0.0", "dev build", 1)]
    public void Compare_versions(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(ReleaseSource.CompareVersions(a, b)));

    [Fact]
    public void Markdown_blocks()
    {
        var blocks = MarkdownView.Parse("""
            ## Highlights

            - First item
              continued here
            - Second
              - Nested
            1. Numbered

            A paragraph
            on two lines.
            ```
            code *not* parsed
            ```
            """);
        Assert.Equal(
            [
                new(MarkdownView.BlockKind.Heading, "Highlights", 2),
                new(MarkdownView.BlockKind.Bullet, "First item continued here", 0),
                new(MarkdownView.BlockKind.Bullet, "Second", 0),
                new(MarkdownView.BlockKind.Bullet, "Nested", 1),
                new(MarkdownView.BlockKind.Numbered, "Numbered", 0, "1."),
                new(MarkdownView.BlockKind.Paragraph, "A paragraph on two lines."),
                new MarkdownView.Block(MarkdownView.BlockKind.Code, "code *not* parsed"),
            ],
            blocks);
    }

    [Fact]
    public void Markdown_inline()
    {
        var spans = MarkdownView.ParseInline("Use **bold**, *it*, `a*b` and [the page](https://x.invalid); snake_case and a * b stay.");
        Assert.Equal(
            [
                new("Use "),
                new("bold", Bold: true),
                new(", "),
                new("it", Italic: true),
                new(", "),
                new("a*b", Code: true),
                new(" and "),
                new("the page", Link: true),
                new("; snake_case and a * b stay."),
            ],
            spans);
    }
}
