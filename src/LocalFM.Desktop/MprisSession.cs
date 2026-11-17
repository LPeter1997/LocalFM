using System.Text;
using LocalFM.App;
using LocalFM.Core;
using LocalFM.Library;
using LocalFM.Playback;
using Tmds.DBus.Protocol;

namespace LocalFM.Desktop;

/// <summary>
/// MPRIS 2 (org.mpris.MediaPlayer2) on the session bus: media keys, desktop "now playing" widgets,
/// playerctl, KDE Connect and so on. Written against the low-level, AOT-safe Tmds.DBus.Protocol.
/// Calls arrive on the bus thread and go straight to the thread-safe <see cref="PlayerService"/>.
/// </summary>
public sealed class MprisSession : IPathMethodHandler, IDisposable
{
    public const string ObjectPathName = "/org/mpris/MediaPlayer2";
    public const string BusName = "org.mpris.MediaPlayer2.localfm";
    private const string RootIface = "org.mpris.MediaPlayer2";
    private const string PlayerIface = "org.mpris.MediaPlayer2.Player";
    private const string PropsIface = "org.freedesktop.DBus.Properties";
    private const string NoTrack = "/org/mpris/MediaPlayer2/TrackList/NoTrack";

    private readonly DBusConnection _conn;
    private readonly PlayerService _player;
    private readonly Action _raise;
    private readonly Action _quit;
    private readonly Lock _gate = new();
    private Dictionary<string, string> _published = new(); // property -> fingerprint, to signal only real changes

    private MprisSession(DBusConnection conn, PlayerService player, Action raise, Action quit)
    {
        _conn = conn;
        _player = player;
        _raise = raise;
        _quit = quit;
    }

    public string Path => ObjectPathName;
    public bool HandlesChildPaths => false;
    public string? Name { get; private set; }

    /// <summary>Connects and publishes the player. Returns null (and logs) when there is no session bus.</summary>
    public static async Task<MprisSession?> StartAsync(PlayerService player, Action raise, Action quit, string? busAddress = null)
    {
        DBusConnection? conn = null;
        try
        {
            string? address = busAddress ?? DBusAddress.Session;
            if (string.IsNullOrEmpty(address)) return null;
            conn = new DBusConnection(address);
            // The connection dispatches calls on the context it was connected from. Connect without one so
            // desktop requests are served on the thread pool and never wait for (or block) the UI thread.
            var context = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            ValueTask connecting;
            try { connecting = conn.ConnectAsync(); }
            finally { SynchronizationContext.SetSynchronizationContext(context); }
            await connecting.ConfigureAwait(false);
            var session = new MprisSession(conn, player, raise, quit);
            conn.AddMethodHandler(session);
            // A second instance gets its own name, as the spec suggests.
            string name = BusName;
            if (!await conn.TryRequestNameAsync(name, RequestNameOptions.Default).ConfigureAwait(false))
            {
                name = $"{BusName}.instance{Environment.ProcessId}";
                await conn.RequestNameAsync(name, RequestNameOptions.Default).ConfigureAwait(false);
            }
            // Replies complete on the connection's receive loop; leave it before running the caller's code,
            // or a caller that blocks would stop every later call from being dispatched.
            await Task.Yield();
            session.Name = name;
            session.Snapshot(out session._published);
            player.Changed += session.OnChanged;
            player.VolumeChanged += _ => session.OnChanged();
            player.Seeked += session.OnSeeked;
            Log.Info("Mpris", $"Published as {name}");
            return session;
        }
        catch (Exception ex)
        {
            Log.Warn("Mpris", $"Media keys unavailable: {ex.Message}");
            conn?.Dispose();
            return null;
        }
    }

    public void Dispose()
    {
        _player.Changed -= OnChanged;
        _player.Seeked -= OnSeeked;
        _conn.Dispose();
    }

    // ------------------------------------------------------------------ properties

    private TrackInfo? Track => _player.Current?.Track;

    private Dictionary<string, VariantValue> RootProps() => new()
    {
        ["CanQuit"] = true,
        ["CanRaise"] = true,
        ["HasTrackList"] = false,
        ["Identity"] = "LocalFM",
        ["DesktopEntry"] = LocalFM.Core.AppInfo.AppId,
        ["SupportedUriSchemes"] = VariantValue.Array(new[] { "file" }),
        ["SupportedMimeTypes"] = VariantValue.Array(Array.Empty<string>()),
    };

    private Dictionary<string, VariantValue> PlayerProps()
    {
        var item = _player.Current;
        bool hasTrack = item is not null;
        int count = _player.Snapshot(out int idx).Count;
        bool any = count > 0;
        bool own = !_player.IsFollowing;
        return new()
        {
            ["PlaybackStatus"] = Status(),
            ["LoopStatus"] = _player.Repeat switch { RepeatMode.One => "Track", RepeatMode.All => "Playlist", _ => "None" },
            ["Rate"] = 1.0,
            ["MinimumRate"] = 1.0,
            ["MaximumRate"] = 1.0,
            ["Shuffle"] = _player.Queue.Shuffled,
            ["Metadata"] = Metadata(item),
            ["Volume"] = (double)_player.Volume,
            ["Position"] = Micros(_player.Position),
            // Listening to a vibe: the host controls playback (volume stays ours).
            ["CanGoNext"] = own && (idx + 1 < count || _player.Repeat == RepeatMode.All && count > 0),
            ["CanGoPrevious"] = own && hasTrack,
            ["CanPlay"] = own && any,
            ["CanPause"] = own && any,
            ["CanSeek"] = own && hasTrack,
            ["CanControl"] = own,
        };
    }

    private string Status() => _player.State switch
    {
        PlaybackState.Playing => "Playing",
        PlaybackState.Paused => "Paused",
        _ => "Stopped",
    };

    private VariantValue Metadata(QueueItem? item)
    {
        var d = new Dict<string, VariantValue>();
        if (item is null)
        {
            d.Add("mpris:trackid", VariantValue.ObjectPath(new ObjectPath(NoTrack)));
            return d.AsVariantValue();
        }
        var t = item.Track;
        d.Add("mpris:trackid", VariantValue.ObjectPath(new ObjectPath(TrackId(item))));
        if (_player.Duration is { } dur) d.Add("mpris:length", Micros(dur));
        d.Add("xesam:title", t.Title);
        if (t.Artist is { } a) d.Add("xesam:artist", VariantValue.Array(new[] { a }));
        if (t.Album is { } al) d.Add("xesam:album", al);
        if (t.AlbumArtist is { } aa) d.Add("xesam:albumArtist", VariantValue.Array(new[] { aa }));
        d.Add("xesam:trackNumber", t.Position);
        d.Add("xesam:discNumber", t.Disc);
        if (t.CoverPath is { } cover) d.Add("mpris:artUrl", new Uri(cover).AbsoluteUri);
        d.Add("xesam:url", new Uri(t.Path).AbsoluteUri);
        return d.AsVariantValue();
    }

    private static string TrackId(QueueItem item) => "/org/localfm/track/" + item.Id;
    private static long Micros(TimeSpan t) => t.Ticks / 10;

    /// <summary>Fingerprints of the properties that are signalled (Position never is, per spec).</summary>
    private Dictionary<string, VariantValue> Snapshot(out Dictionary<string, string> fingerprints)
    {
        var props = PlayerProps();
        props.Remove("Position");
        var t = Track;
        fingerprints = props.ToDictionary(p => p.Key, p => p.Key == "Metadata"
            ? $"{_player.Current?.Id}|{t?.Title}|{t?.Artist}|{t?.Album}|{t?.CoverPath}|{_player.Duration}"
            : p.Value.ToString() ?? "");
        return props;
    }

    private void OnChanged()
    {
        try
        {
            Dictionary<string, VariantValue> changed;
            // Read the player before taking our lock: the player may be raising this event under its own.
            var props = Snapshot(out var fp);
            lock (_gate)
            {
                changed = props.Where(p => !_published.TryGetValue(p.Key, out var old) || old != fp[p.Key])
                               .ToDictionary(p => p.Key, p => p.Value);
                _published = fp;
            }
            if (changed.Count == 0) return;
            using var w = _conn.GetMessageWriter();
            w.WriteSignalHeader(null, ObjectPathName, PropsIface, "PropertiesChanged", "sa{sv}as");
            w.WriteString(PlayerIface);
            w.WriteDictionary(changed);
            w.WriteArray(Array.Empty<string>());
            _conn.TrySendMessage(w.CreateMessage());
        }
        catch (Exception ex) { Log.Debug("Mpris", $"Signal failed: {ex.Message}"); }
    }

    private void OnSeeked(TimeSpan position)
    {
        try
        {
            using var w = _conn.GetMessageWriter();
            w.WriteSignalHeader(null, ObjectPathName, PlayerIface, "Seeked", "x");
            w.WriteInt64(Micros(position));
            _conn.TrySendMessage(w.CreateMessage());
        }
        catch (Exception ex) { Log.Debug("Mpris", $"Signal failed: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ calls

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        try { Handle(context); }
        catch (Exception ex)
        {
            Log.Warn("Mpris", $"Call failed: {ex.Message}");
            if (!context.ReplySent && !context.NoReplyExpected) context.ReplyError("org.mpris.MediaPlayer2.LocalFM.Error", ex.Message);
        }
        return default;
    }

    private void Handle(MethodContext context)
    {
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([IntrospectXml], Array.Empty<string>());
            return;
        }
        var request = context.Request;
        string iface = request.InterfaceAsString ?? "", member = request.MemberAsString ?? "";
        // Which client asked ("busctl --user status <name>" names its process), for the session log.
        string by = $"MPRIS {request.SenderAsString ?? "?"}";

        switch (iface, member)
        {
            case (RootIface, "Raise"): _raise(); ReplyEmpty(context); return;
            case (RootIface, "Quit"): ReplyEmpty(context); _quit(); return;

            case (PlayerIface, "Play"): _player.Play(by); break;
            case (PlayerIface, "Pause"): _player.Pause(by); break;
            case (PlayerIface, "PlayPause"): _player.PlayPause(by); break;
            case (PlayerIface, "Stop"): _player.Stop(by); break;
            case (PlayerIface, "Next"): _player.Next(by); break;
            case (PlayerIface, "Previous"): _player.Previous(by); break;
            case (PlayerIface, "Seek"):
            {
                var r = request.GetBodyReader();
                long offset = r.ReadInt64();
                var target = _player.Position + TimeSpan.FromTicks(offset * 10);
                if (_player.Duration is { } d && target >= d) _player.Next(by);
                else _player.Seek(target < TimeSpan.Zero ? TimeSpan.Zero : target, by);
                break;
            }
            case (PlayerIface, "SetPosition"):
            {
                var r = request.GetBodyReader();
                string trackId = r.ReadObjectPathAsString();
                long pos = r.ReadInt64();
                // Ignored unless it is about the current track and within it (spec).
                if (_player.Current is { } item && trackId == TrackId(item) && pos >= 0
                    && (_player.Duration is not { } d || pos <= Micros(d)))
                    _player.Seek(TimeSpan.FromTicks(pos * 10), by);
                break;
            }
            case (PlayerIface, "OpenUri"):
                context.ReplyError("org.mpris.MediaPlayer2.LocalFM.NotSupported", "Add folders to the library instead.");
                return;

            case (PropsIface, "Get"):
            {
                var r = request.GetBodyReader();
                string i = r.ReadString(), prop = r.ReadString();
                var props = PropsOf(i);
                if (props is null || !props.TryGetValue(prop, out var value))
                {
                    context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", $"No property {i}.{prop}");
                    return;
                }
                using var w = context.CreateReplyWriter("v");
                w.WriteVariant(value);
                context.Reply(w.CreateMessage());
                return;
            }
            case (PropsIface, "GetAll"):
            {
                var r = request.GetBodyReader();
                var props = PropsOf(r.ReadString()) ?? new Dictionary<string, VariantValue>();
                using var w = context.CreateReplyWriter("a{sv}");
                w.WriteDictionary(props);
                context.Reply(w.CreateMessage());
                return;
            }
            case (PropsIface, "Set"):
            {
                var r = request.GetBodyReader();
                string i = r.ReadString(), prop = r.ReadString();
                var value = r.ReadVariantValue();
                if (i == PlayerIface) SetProperty(prop, value, by);
                break;
            }
            default:
                context.ReplyUnknownMethodError();
                return;
        }
        ReplyEmpty(context);
    }

    private void SetProperty(string prop, VariantValue value, string by)
    {
        switch (prop)
        {
            case "Volume":
                float volume = (float)Math.Clamp(value.GetDouble(), 0, 1);
                Log.Info("Mpris", $"Volume {_player.Volume:0.##} -> {volume:0.##} by {by}");
                _player.Volume = volume;
                break;
            case "Shuffle": _player.SetShuffle(value.GetBool()); break;
            case "LoopStatus":
                _player.Repeat = value.GetString() switch { "Track" => RepeatMode.One, "Playlist" => RepeatMode.All, _ => RepeatMode.Off };
                break;
        }
        OnChanged();
    }

    private Dictionary<string, VariantValue>? PropsOf(string iface) => iface switch
    {
        RootIface => RootProps(),
        PlayerIface => PlayerProps(),
        _ => null,
    };

    private static void ReplyEmpty(MethodContext context)
    {
        if (context.NoReplyExpected || context.ReplySent) return;
        using var w = context.CreateReplyWriter(null!);
        context.Reply(w.CreateMessage());
    }

    private static readonly ReadOnlyMemory<byte> IntrospectXml = Encoding.UTF8.GetBytes("""
        <interface name="org.mpris.MediaPlayer2">
          <method name="Raise"/>
          <method name="Quit"/>
          <property name="CanQuit" type="b" access="read"/>
          <property name="CanRaise" type="b" access="read"/>
          <property name="HasTrackList" type="b" access="read"/>
          <property name="Identity" type="s" access="read"/>
          <property name="DesktopEntry" type="s" access="read"/>
          <property name="SupportedUriSchemes" type="as" access="read"/>
          <property name="SupportedMimeTypes" type="as" access="read"/>
        </interface>
        <interface name="org.mpris.MediaPlayer2.Player">
          <method name="Next"/>
          <method name="Previous"/>
          <method name="Pause"/>
          <method name="PlayPause"/>
          <method name="Stop"/>
          <method name="Play"/>
          <method name="Seek"><arg direction="in" name="Offset" type="x"/></method>
          <method name="SetPosition"><arg direction="in" name="TrackId" type="o"/><arg direction="in" name="Position" type="x"/></method>
          <method name="OpenUri"><arg direction="in" name="Uri" type="s"/></method>
          <signal name="Seeked"><arg name="Position" type="x"/></signal>
          <property name="PlaybackStatus" type="s" access="read"/>
          <property name="LoopStatus" type="s" access="readwrite"/>
          <property name="Rate" type="d" access="readwrite"/>
          <property name="Shuffle" type="b" access="readwrite"/>
          <property name="Metadata" type="a{sv}" access="read"/>
          <property name="Volume" type="d" access="readwrite"/>
          <property name="Position" type="x" access="read"/>
          <property name="MinimumRate" type="d" access="read"/>
          <property name="MaximumRate" type="d" access="read"/>
          <property name="CanGoNext" type="b" access="read"/>
          <property name="CanGoPrevious" type="b" access="read"/>
          <property name="CanPlay" type="b" access="read"/>
          <property name="CanPause" type="b" access="read"/>
          <property name="CanSeek" type="b" access="read"/>
          <property name="CanControl" type="b" access="read"/>
        </interface>
        """);
}
