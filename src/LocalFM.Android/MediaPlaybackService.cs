using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Media;
using Android.Media.Session;
using Android.OS;
using LocalFM.App;
using LocalFM.Core;
using PlaybackState = LocalFM.Playback.PlaybackState;
using SessionState = Android.Media.Session.PlaybackState;

namespace LocalFM.Android;

/// <summary>
/// Playing in the background: a foreground service while music plays, the media session (lock screen, quick
/// settings, headset and Bluetooth buttons) and its notification. It also asks for audio focus (a call or another
/// player pauses us, a short interruption resumes us), pauses when headphones are unplugged, and keeps the CPU awake
/// while playing. The player lives in <see cref="AndroidPlatform"/>, not here or in the activity, so neither the
/// activity going away nor this service stopping touches playback.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public sealed class MediaPlaybackService : Service
{
    private const string Channel = "playback";
    private const int NotificationId = 1;
    private const string ActionPlayPause = "localfm.playpause", ActionNext = "localfm.next",
        ActionPrevious = "localfm.previous", ActionDismiss = "localfm.dismiss";

    private static PlayerService? _watched;
    private static bool _running;

    private PlayerService? _player;
    private MediaSession? _session;
    private NotificationManager? _notifications;
    private AudioManager? _audio;
    private AudioFocusRequestClass? _focusRequest;
    private bool _hasFocus, _resumeOnGain, _foreground, _updatePosted;
    private PowerManager.WakeLock? _wakeLock;
    private NoisyReceiver? _noisy;
    private readonly Handler _main = new(Looper.MainLooper!);
    private string? _coverPath;
    private Bitmap? _cover;

    /// <summary>Starts the service whenever playback starts (it stops itself when it is no longer needed).</summary>
    public static void Watch(PlayerService player)
    {
        if (_watched is not null) return;
        _watched = player;
        var main = new Handler(Looper.MainLooper!);
        player.Changed += () => main.Post(() =>
        {
            if (_running || player.State != PlaybackState.Playing) return;
            var context = global::Android.App.Application.Context;
            try { context.StartForegroundService(new Intent(context, typeof(MediaPlaybackService))); }
            catch (Exception ex) { Log.Warn("Android", "Could not start the playback service", ex); }
        });
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        _running = true;
        _player = AndroidPlatform.Instance.Host?.Player;
        _notifications = (NotificationManager?)GetSystemService(NotificationService);
        _notifications?.CreateNotificationChannel(new NotificationChannel(Channel, "Playback", NotificationImportance.Low)
        {
            Description = "What is playing, with play, pause and skip",
        });
        _audio = (AudioManager?)GetSystemService(AudioService);
        _wakeLock = ((PowerManager?)GetSystemService(PowerService))?.NewWakeLock(WakeLockFlags.Partial, "LocalFM:playback");
        _wakeLock?.SetReferenceCounted(false);

        _session = new MediaSession(this, "LocalFM");
        _session.SetCallback(new SessionCallback(this));
        var open = PackageManager?.GetLaunchIntentForPackage(PackageName!);
        if (open is not null)
            _session.SetSessionActivity(PendingIntent.GetActivity(this, 0, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent));
        _session.Active = true;

        if (_player is not null)
        {
            _player.Changed += OnPlayerChanged;
            _player.Seeked += OnSeeked;
        }
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        switch (intent?.Action)
        {
            case ActionPlayPause: _player?.PlayPause(); break;
            case ActionNext: _player?.Next(); break;
            case ActionPrevious: _player?.Previous(); break;
            case ActionDismiss:
                if (_player?.State != PlaybackState.Playing) StopSelf();
                return StartCommandResult.NotSticky;
        }
        // A start for a foreground service must reach startForeground in a few seconds, whatever the state.
        Update(forceForeground: intent?.Action is null);
        return StartCommandResult.NotSticky;
    }

    /// <summary>Swiping the app away keeps the music going; with nothing playing the notification goes too.</summary>
    public override void OnTaskRemoved(Intent? rootIntent)
    {
        base.OnTaskRemoved(rootIntent);
        if (_player?.State != PlaybackState.Playing) StopSelf();
    }

    public override void OnDestroy()
    {
        _running = false;
        if (_player is not null)
        {
            _player.Changed -= OnPlayerChanged;
            _player.Seeked -= OnSeeked;
        }
        SetPlayingResources(false);
        AbandonFocus();
        _session?.Release();
        _session = null;
        _cover?.Dispose();
        base.OnDestroy();
    }

    // ------------------------------------------------------------------ state

    // The player may raise these under its own lock: read it later, on the main thread, once per burst.
    private void OnPlayerChanged() => PostUpdate();

    private void OnSeeked(TimeSpan _) => PostUpdate();

    private void PostUpdate()
    {
        if (_updatePosted) return;
        _updatePosted = true;
        _main.Post(() =>
        {
            _updatePosted = false;
            Update();
        });
    }

    private void Update(bool forceForeground = false)
    {
        if (_player is not { } p || _session is null) return;
        bool playing = p.State == PlaybackState.Playing;
        var track = p.Current?.Track;
        bool own = !p.IsFollowing;

        long actions = SessionState.ActionPlayPause | SessionState.ActionPlay | SessionState.ActionPause | SessionState.ActionStop;
        if (own) actions |= SessionState.ActionSkipToNext | SessionState.ActionSkipToPrevious | SessionState.ActionSeekTo;
        _session.SetPlaybackState(new SessionState.Builder()!
            .SetActions(actions)!
            .SetState(playing ? PlaybackStateCode.Playing : track is null ? PlaybackStateCode.Stopped : PlaybackStateCode.Paused,
                      (long)p.Position.TotalMilliseconds, playing ? 1f : 0f, SystemClock.ElapsedRealtime())!
            .Build());

        var cover = Cover(track?.CoverPath);
        var meta = new MediaMetadata.Builder()!
            .PutString(MediaMetadata.MetadataKeyTitle, track?.Title ?? "LocalFM")!
            .PutString(MediaMetadata.MetadataKeyArtist, track?.Artist ?? "")!
            .PutString(MediaMetadata.MetadataKeyAlbum, track?.Album ?? "")!
            .PutLong(MediaMetadata.MetadataKeyDuration, (long)(p.Duration?.TotalMilliseconds ?? 0))!;
        if (cover is not null) meta.PutBitmap(MediaMetadata.MetadataKeyAlbumArt, cover);
        _session.SetMetadata(meta.Build());

        var notification = BuildNotification(track, playing, own, cover);
        if (playing || forceForeground)
        {
            // Android 14 wants the service type named here as well as in the manifest.
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q) StartForeground(NotificationId, notification, ForegroundService.TypeMediaPlayback);
            else StartForeground(NotificationId, notification);
            _foreground = true;
        }
        if (!playing && _foreground)
        {
            // Paused: the notification stays (swipeable) so play is one tap away, but nothing keeps us alive.
            StopForeground(StopForegroundFlags.Detach);
            _foreground = false;
        }
        if (!playing) _notifications?.Notify(NotificationId, notification);

        if (playing) RequestFocus();
        SetPlayingResources(playing);
    }

    private Notification BuildNotification(LocalFM.Library.TrackInfo? track, bool playing, bool own, Bitmap? cover)
    {
        var builder = new Notification.Builder(this, Channel)!
            .SetSmallIcon(Resource.Drawable.ic_stat_note)!
            .SetContentTitle(track?.Title ?? "LocalFM")!
            .SetContentText(track?.Artist ?? "")!
            .SetSubText(own ? null : "In a vibe")!
            .SetVisibility(NotificationVisibility.Public)!
            .SetOngoing(playing)!
            .SetShowWhen(false)!
            .SetDeleteIntent(ServiceIntent(ActionDismiss, 4));
        if (_session?.Controller?.SessionActivity is { } open) builder.SetContentIntent(open);
        if (cover is not null) builder.SetLargeIcon(cover);

        var style = new Notification.MediaStyle().SetMediaSession(_session!.SessionToken)!;
        if (own)
        {
            builder.AddAction(Action(Resource.Drawable.ic_prev, "Previous", ActionPrevious, 1));
            builder.AddAction(Action(playing ? Resource.Drawable.ic_pause : Resource.Drawable.ic_play, playing ? "Pause" : "Play", ActionPlayPause, 2));
            builder.AddAction(Action(Resource.Drawable.ic_next, "Next", ActionNext, 3));
            style.SetShowActionsInCompactView(0, 1, 2);
        }
        else
        {
            // In a friend's vibe the host decides what plays; only pausing locally would make sense, and that
            // leaves the vibe's timing, so the controls stay in the app.
            style.SetShowActionsInCompactView();
        }
        builder.SetStyle(style);
        return builder.Build()!;
    }

    private Notification.Action Action(int icon, string title, string action, int code) =>
        new Notification.Action.Builder(global::Android.Graphics.Drawables.Icon.CreateWithResource(this, icon), title, ServiceIntent(action, code)).Build()!;

    private PendingIntent ServiceIntent(string action, int code) =>
        PendingIntent.GetService(this, code, new Intent(this, typeof(MediaPlaybackService)).SetAction(action),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;

    /// <summary>The cover, decoded once per album at notification size.</summary>
    private Bitmap? Cover(string? path)
    {
        if (path == _coverPath) return _cover;
        _coverPath = path;
        _cover?.Dispose();
        _cover = null;
        if (path is null || !File.Exists(path)) return null;
        try
        {
            var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
            BitmapFactory.DecodeFile(path, bounds);
            int sample = 1;
            while (Math.Max(bounds.OutWidth, bounds.OutHeight) / (sample * 2) >= 512) sample *= 2;
            _cover = BitmapFactory.DecodeFile(path, new BitmapFactory.Options { InSampleSize = sample });
        }
        catch (Exception ex) { Log.Warn("Android", $"Could not decode the cover {path}", ex); }
        return _cover;
    }

    // ------------------------------------------------------------------ while playing

    private void SetPlayingResources(bool playing)
    {
        if (playing)
        {
            if (_wakeLock is { IsHeld: false }) _wakeLock.Acquire();
            if (_noisy is null)
            {
                _noisy = new NoisyReceiver(this);
                var filter = new IntentFilter(AudioManager.ActionAudioBecomingNoisy);
                if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu) RegisterReceiver(_noisy, filter, ReceiverFlags.NotExported);
                else RegisterReceiver(_noisy, filter);
            }
        }
        else
        {
            if (_wakeLock is { IsHeld: true }) _wakeLock.Release();
            if (_noisy is not null)
            {
                UnregisterReceiver(_noisy);
                _noisy = null;
            }
        }
    }

    private void RequestFocus()
    {
        if (_hasFocus || _audio is null) return;
        _focusRequest ??= new AudioFocusRequestClass.Builder(AudioFocus.Gain)!
            .SetAudioAttributes(new AudioAttributes.Builder().SetUsage(AudioUsageKind.Media)!.SetContentType(AudioContentType.Music)!.Build()!)
            .SetOnAudioFocusChangeListener(new FocusListener(this))!
            .Build()!;
        _hasFocus = _audio.RequestAudioFocus(_focusRequest) == AudioFocusRequest.Granted;
        _resumeOnGain = false;
    }

    private void AbandonFocus()
    {
        if (_focusRequest is not null && _audio is not null) _audio.AbandonAudioFocusRequest(_focusRequest);
        _hasFocus = false;
    }

    /// <summary>Another app or a call takes the audio. A short interruption resumes us when it ends; ducking (a
    /// navigation prompt) the system does by itself.</summary>
    private void OnFocusChange(AudioFocus change)
    {
        if (_player is not { } p) return;
        switch (change)
        {
            case AudioFocus.Loss:
                _hasFocus = false;
                _resumeOnGain = false;
                p.Pause();
                break;
            case AudioFocus.LossTransient:
                _resumeOnGain = p.State == PlaybackState.Playing;
                p.Pause();
                break;
            case AudioFocus.Gain:
                _hasFocus = true;
                if (_resumeOnGain) p.Play();
                _resumeOnGain = false;
                break;
        }
    }

    private sealed class FocusListener(MediaPlaybackService service) : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener
    {
        public void OnAudioFocusChange(AudioFocus focusChange) => service.OnFocusChange(focusChange);
    }

    /// <summary>Headphones unplugged or Bluetooth gone: pause rather than play out loud.</summary>
    private sealed class NoisyReceiver(MediaPlaybackService service) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action == AudioManager.ActionAudioBecomingNoisy) service._player?.Pause();
        }
    }

    /// <summary>Lock screen, quick settings, headset and Bluetooth buttons.</summary>
    private sealed class SessionCallback(MediaPlaybackService service) : MediaSession.Callback
    {
        private PlayerService? Player => service._player;
        public override void OnPlay() => Player?.Play();
        public override void OnPause() => Player?.Pause();
        public override void OnStop() => Player?.Pause();
        public override void OnSkipToNext() => Player?.Next();
        public override void OnSkipToPrevious() => Player?.Previous();
        public override void OnSeekTo(long pos) => Player?.Seek(TimeSpan.FromMilliseconds(pos));
    }
}
