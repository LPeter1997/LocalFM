using LocalFM.Native;
namespace LocalFM.Playback;

/// <summary>
/// What to play. <paramref name="Token"/> is opaque to the engine and is handed back in events
/// (the app uses its queue item id). <paramref name="Gain"/> is a linear factor (ReplayGain etc.).
/// <paramref name="JoinGapless"/> asks never to crossfade into this track (it continues the previous one,
/// like the next track of a live album).
/// </summary>
/// <param name="Open">Opens the decoder at the given output rate instead of <see cref="Decoder.Open(string, int)"/>
/// on <paramref name="Path"/> (e.g. a file streamed from another LocalFM). Called on the thread pool.</param>
public sealed record TrackHandle(string Path, object? Token = null, float Gain = 1f, bool JoinGapless = false,
    Func<int, Decoder>? Open = null)
{
    internal Decoder OpenDecoder(int rate) => Open is { } open ? open(rate) : Decoder.Open(Path, rate);
}

public enum PlaybackState { Stopped, Playing, Paused }

public abstract record EngineEvent;

/// <summary>The track became audible (sample-accurate to within one engine tick).</summary>
public sealed record TrackStarted(TrackHandle Track, TimeSpan? Duration) : EngineEvent;

/// <summary>The track stopped being audible. <paramref name="Completed"/> is true when it played to its end.</summary>
public sealed record TrackEnded(TrackHandle Track, bool Completed) : EngineEvent;

/// <summary>The last track finished and nothing was queued after it.</summary>
public sealed record PlaybackFinished : EngineEvent;

/// <summary>A track could not be opened or decoded. Playback continues with whatever comes next.</summary>
public sealed record TrackFailed(TrackHandle Track, string Reason) : EngineEvent;

public sealed record StateChanged(PlaybackState State) : EngineEvent;

/// <summary>An in-place DSP stage (EQ, effects). Runs on the engine thread; must not allocate or block.</summary>
public interface IAudioProcessor
{
    void Process(Span<float> interleavedStereo, int sampleRate);
}
