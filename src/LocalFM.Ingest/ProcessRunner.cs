using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using LocalFM.Core;

namespace LocalFM.Ingest;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>Runs external tools. An interface so tests can stand in for yt-dlp.</summary>
public interface IProcessRunner
{
    /// <summary>Runs <paramref name="exe"/> to completion. Lines are passed to <paramref name="onLine"/> as they arrive
    /// (stderr ones flagged), and also collected. Cancelling kills the whole process tree.
    /// Throws <see cref="FileNotFoundException"/> when the program cannot be started.</summary>
    Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, Action<string, bool>? onLine = null, CancellationToken ct = default);
}

/// <summary>Runs tools at below-normal priority, so a download or extraction never competes with playback.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    private const int MaxStdErr = 64 * 1024;

    public async Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, Action<string, bool>? onLine = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Python tools print UTF-8 only when asked on some platforms (Windows consoles).
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";

        using var p = new Process { StartInfo = psi };
        try { p.Start(); }
        catch (Win32Exception ex) { throw new FileNotFoundException($"Cannot run '{exe}': {ex.Message}", exe, ex); }
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { /* not allowed everywhere; harmless */ }
        p.StandardInput.Close();

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        async Task Pump(StreamReader reader, StringBuilder sink, bool isErr)
        {
            while (await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
            {
                lock (sink)
                {
                    if (!isErr || sink.Length < MaxStdErr) sink.AppendLine(line);
                }
                try { onLine?.Invoke(line, isErr); }
                catch (Exception ex) { Log.Warn("Process", $"Line handler failed: {ex.Message}"); }
            }
        }
        var outTask = Pump(p.StandardOutput, stdout, false);
        var errTask = Pump(p.StandardError, stderr, true);

        using (ct.Register(() =>
               {
                   try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
                   catch (Exception) { /* already gone */ }
               }))
        {
            await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        return new ProcessResult(p.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Finds a program on PATH (with the platform's executable extensions). Null when absent.</summary>
    public static string? FindOnPath(string name)
    {
        string[] exts = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts)
            {
                string candidate;
                try { candidate = Path.Combine(dir.Trim('"'), name + ext.ToLowerInvariant()); }
                catch (ArgumentException) { continue; }
                if (File.Exists(candidate)) return candidate;
            }
        }
        // Common install spots that are not always on a GUI app's PATH (pipx, Homebrew).
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var dir in new[] { Path.Combine(home, ".local", "bin"), "/opt/homebrew/bin", "/usr/local/bin" })
        {
            string candidate = Path.Combine(dir, name);
            if (!OperatingSystem.IsWindows() && File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
