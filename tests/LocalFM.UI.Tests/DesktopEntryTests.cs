using LocalFM.UI.Updates;

namespace LocalFM.UI.Tests;

/// <summary>The AppImage's applications-menu entry (ported from CheeseGit).</summary>
public sealed class DesktopEntryTests : IDisposable
{
    private const string FileName = "io.github.LPeter1997.LocalFM.desktop";
    private readonly string _dataHome = Directory.CreateTempSubdirectory("localfm-xdg-").FullName;

    [Fact]
    public void Not_an_AppImage_is_not_applicable() =>
        Assert.Equal(DesktopEntryStatus.NotApplicable, DesktopEntry.Check(null, _dataHome));

    [Fact]
    public void Register_makes_it_current_and_a_move_makes_it_stale()
    {
        Assert.Equal(DesktopEntryStatus.Missing, DesktopEntry.Check("/opt/apps/LocalFM.AppImage", _dataHome));
        DesktopEntry.Register("/opt/apps/LocalFM.AppImage", _dataHome, [1, 2, 3]);
        Assert.Equal(DesktopEntryStatus.Current, DesktopEntry.Check("/opt/apps/LocalFM.AppImage", _dataHome));
        Assert.Equal(DesktopEntryStatus.Stale, DesktopEntry.Check("/home/me/LocalFM.AppImage", _dataHome));

        var text = File.ReadAllText(Path.Combine(_dataHome, "applications", FileName));
        Assert.Contains("Exec=/opt/apps/LocalFM.AppImage\n", text);
        Assert.Contains("StartupWMClass=io.github.LPeter1997.LocalFM\n", text);
        var icon = Path.Combine(_dataHome, "icons", "hicolor", "512x512", "apps", "io.github.LPeter1997.LocalFM.png");
        Assert.Contains($"Icon={icon}\n", text);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(icon));
    }

    [Theory]
    [InlineData("/opt/LocalFM.AppImage", "/opt/LocalFM.AppImage")]
    [InlineData("/home/me/My Apps/LocalFM.AppImage", "\"/home/me/My Apps/LocalFM.AppImage\"")]
    [InlineData("/home/me/a$b `c`/x", "\"/home/me/a\\\\$b \\\\`c\\\\`/x\"")]
    [InlineData("/home/me/100%/x", "/home/me/100%%/x")]
    public void Exec_quoting(string program, string expected) => Assert.Equal(expected, DesktopEntry.ExecLine(program));

    [Fact]
    public void A_desktop_file_validator_accepts_the_entry()
    {
        // desktop-file-validate is part of desktop-file-utils; skipped where it is not installed.
        var validator = Environment.GetEnvironmentVariable("PATH")?.Split(':').Select(d => Path.Combine(d, "desktop-file-validate")).FirstOrDefault(File.Exists);
        if (validator is null) return;
        DesktopEntry.Register("/home/me/My Apps/a$b.AppImage", _dataHome, [1]);
        var psi = new System.Diagnostics.ProcessStartInfo(validator, Path.Combine(_dataHome, "applications", FileName)) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0 && !output.Contains("error", StringComparison.OrdinalIgnoreCase), output);
    }

    public void Dispose() => Directory.Delete(_dataHome, recursive: true);
}
