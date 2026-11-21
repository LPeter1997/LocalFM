using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using LocalFM.App;
using LocalFM.Core;
using LocalFM.Playback;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace LocalFM.Desktop;

/// <summary>
/// Keeps the computer from going to sleep on its own while music plays: a long album must not stop halfway because
/// nobody touched the mouse. Only idle sleep is held off; the screen may still turn off, and the person can still
/// put the computer to sleep or close a laptop's lid. Linux: the desktop portal (GNOME, KDE and others, also inside
/// Flatpak), else logind; Windows: SetThreadExecutionState; macOS: an IOKit power assertion.
/// All requests run on one thread of their own (Windows ties the request to the thread that made it).
/// </summary>
public sealed partial class SleepGuard : IDisposable
{
    private const string Reason = "Playing music";

    private readonly PlayerService _player;
    private readonly BlockingCollection<bool> _wants = new();
    private readonly Thread _thread;
    private readonly IHold? _hold;
    private bool _held;

    public SleepGuard(PlayerService player)
    {
        _player = player;
        _hold = OperatingSystem.IsWindows() ? new WindowsHold()
            : OperatingSystem.IsMacOS() ? new MacHold()
            : OperatingSystem.IsLinux() ? new LinuxHold()
            : null;
        _thread = new Thread(Run) { Name = "LocalFM sleep guard", IsBackground = true };
        _thread.Start();
        _player.Changed += Update;
        Update();
    }

    private void Update()
    {
        if (!_wants.IsAddingCompleted)
            try { _wants.Add(_player.State == PlaybackState.Playing); } catch (InvalidOperationException) { }
    }

    private void Run()
    {
        foreach (bool want in _wants.GetConsumingEnumerable())
        {
            // Only the latest wish counts.
            bool latest = want;
            while (_wants.TryTake(out bool next)) latest = next;
            Apply(latest);
        }
        Apply(false);
    }

    private void Apply(bool want)
    {
        if (want == _held || _hold is null) return;
        try
        {
            if (want)
            {
                _held = _hold.Hold();
                if (_held) Log.Info("Power", $"Sleep held off while playing ({_hold.Name})");
                else Log.Warn("Power", "Could not keep the computer awake while playing");
            }
            else
            {
                _hold.Release();
                _held = false;
                Log.Info("Power", "Sleep allowed again");
            }
        }
        catch (Exception ex)
        {
            _held = false;
            Log.Warn("Power", $"Sleep guard failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _player.Changed -= Update;
        _wants.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(3));
        (_hold as IDisposable)?.Dispose();
    }

    private interface IHold
    {
        string Name { get; }
        /// <summary>True when the system took the request.</summary>
        bool Hold();
        void Release();
    }

    // ------------------------------------------------------------------ Windows

    private sealed partial class WindowsHold : IHold
    {
        private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;

        [LibraryImport("kernel32.dll")]
        private static partial uint SetThreadExecutionState(uint flags);

        public string Name => "Windows";
        public bool Hold() => SetThreadExecutionState(EsContinuous | EsSystemRequired) != 0;
        public void Release() => SetThreadExecutionState(EsContinuous);
    }

    // ------------------------------------------------------------------ macOS

    private sealed partial class MacHold : IHold
    {
        private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const uint Utf8 = 0x08000100, LevelOn = 255;
        private uint _id;

        [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
        private static partial nint CFStringCreateWithCString(nint allocator, string text, uint encoding);

        [LibraryImport(CoreFoundation)]
        private static partial void CFRelease(nint cf);

        [LibraryImport(IOKit)]
        private static partial int IOPMAssertionCreateWithName(nint type, uint level, nint name, out uint id);

        [LibraryImport(IOKit)]
        private static partial int IOPMAssertionRelease(uint id);

        public string Name => "macOS";

        public bool Hold()
        {
            nint type = CFStringCreateWithCString(0, "PreventUserIdleSystemSleep", Utf8);
            nint name = CFStringCreateWithCString(0, "LocalFM: " + Reason, Utf8);
            try { return IOPMAssertionCreateWithName(type, LevelOn, name, out _id) == 0; }
            finally { CFRelease(type); CFRelease(name); }
        }

        public void Release()
        {
            if (_id != 0) IOPMAssertionRelease(_id);
            _id = 0;
        }
    }

    // ------------------------------------------------------------------ Linux

    /// <summary>
    /// The desktop portal's Inhibit (Suspend) asks the desktop itself, which is what decides about idle sleep on
    /// GNOME and KDE; released by closing the request it returns. Without a portal: a logind "idle" inhibitor, a
    /// file handle held open for as long as it lasts.
    /// </summary>
    private sealed class LinuxHold : IHold, IDisposable
    {
        private DBusConnection? _session, _system;
        private string? _request;
        private SafeFileHandle? _logind;

        public string Name { get; private set; } = "Linux";

        public bool Hold()
        {
            try
            {
                _session ??= Connect(DBusAddress.Session);
                if (_session is not null)
                {
                    _request = Portal(_session).GetAwaiter().GetResult();
                    Name = "desktop portal";
                    return true;
                }
            }
            catch (Exception ex) { Log.Info("Power", $"No desktop portal to keep awake with: {ex.Message}"); }

            _system ??= Connect(DBusAddress.System);
            if (_system is null) return false;
            _logind = Logind(_system).GetAwaiter().GetResult();
            Name = "logind";
            return true;
        }

        public void Release()
        {
            if (_request is { } request && _session is { } bus)
            {
                _request = null;
                using var w = bus.GetMessageWriter();
                w.WriteMethodCallHeader("org.freedesktop.portal.Desktop", request, "org.freedesktop.portal.Request", "Close", null, MessageFlags.None);
                bus.CallMethodAsync(w.CreateMessage()).GetAwaiter().GetResult();
            }
            _logind?.Dispose();
            _logind = null;
        }

        private static DBusConnection? Connect(string? address)
        {
            if (string.IsNullOrEmpty(address)) return null;
            var conn = new DBusConnection(address);
            conn.ConnectAsync().AsTask().GetAwaiter().GetResult();
            return conn;
        }

        private static Task<string> Portal(DBusConnection bus)
        {
            using var w = bus.GetMessageWriter();
            w.WriteMethodCallHeader("org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop", "org.freedesktop.portal.Inhibit", "Inhibit", "sua{sv}", MessageFlags.None);
            w.WriteString(""); // no window: the request is for the app
            w.WriteUInt32(4); // Suspend
            w.WriteDictionary(new Dictionary<string, VariantValue> { ["reason"] = Reason });
            return bus.CallMethodAsync(w.CreateMessage(), static (m, _) => m.GetBodyReader().ReadObjectPathAsString(), null);
        }

        private static Task<SafeFileHandle> Logind(DBusConnection bus)
        {
            using var w = bus.GetMessageWriter();
            w.WriteMethodCallHeader("org.freedesktop.login1", "/org/freedesktop/login1", "org.freedesktop.login1.Manager", "Inhibit", "ssss", MessageFlags.None);
            w.WriteString("idle");
            w.WriteString("LocalFM");
            w.WriteString(Reason);
            w.WriteString("block");
            return bus.CallMethodAsync(w.CreateMessage(), static (m, _) => m.GetBodyReader().ReadHandle<SafeFileHandle>()!, null);
        }

        public void Dispose()
        {
            _session?.Dispose();
            _system?.Dispose();
        }
    }
}
