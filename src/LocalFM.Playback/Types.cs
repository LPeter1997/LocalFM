namespace LocalFM.Playback;

/// <summary>
/// What to play. <paramref name="Token"/> is opaque to the engine and is handed back in events
/// (the app uses its queue item id). <paramref name="Gain"/> is a linear factor (ReplayGain etc.).
/// </summary>
public sealed record TrackHandle(string Path, object? Token = null, float Gain = 1f);

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
