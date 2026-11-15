using System.Text.Json.Serialization;

namespace LocalFM.Vibe.Protocol;

/// <summary>Control messages (JSON on the control connection). Unknown fields are ignored, so later versions can
/// add fields without breaking older listeners; anything incompatible bumps <see cref="Wire.Version"/>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(Hello), "hello")]
[JsonDerivedType(typeof(Welcome), "welcome")]
[JsonDerivedType(typeof(Rejected), "rejected")]
[JsonDerivedType(typeof(Ping), "ping")]
[JsonDerivedType(typeof(Pong), "pong")]
[JsonDerivedType(typeof(Timeline), "timeline")]
[JsonDerivedType(typeof(QueueWindow), "queue")]
[JsonDerivedType(typeof(Bye), "bye")]
[JsonDerivedType(typeof(LyricsRequest), "lyricsReq")]
[JsonDerivedType(typeof(LyricsReply), "lyrics")]
[JsonDerivedType(typeof(Presence), "presence")]
public abstract record Message;

public enum ConnectionRole { Control, Data }

/// <summary>First message on every connection. Control connections prove the invite's secret; the data
/// connection proves the token the host handed out in <see cref="Welcome"/>.</summary>
public sealed record Hello(int Version, ConnectionRole Role, string Secret, string Name) : Message;

public sealed record Welcome(string ListenerId, string DataToken, string HostName) : Message;

public sealed record Rejected(string Reason) : Message;

public sealed record Ping(long T0) : Message;

/// <summary><see cref="T1"/>: the host's clock when it answered.</summary>
public sealed record Pong(long T0, long T1) : Message;

/// <summary>Where the host is: at host time <see cref="HostTimeUs"/> it heard <see cref="ItemId"/> at
/// <see cref="PositionUs"/> (what comes out of its speaker, not what it decodes; negative right after a track
/// change, while the speaker still plays the end of the previous track).</summary>
public sealed record Timeline(string? ItemId, long PositionUs, long HostTimeUs, bool Playing, double CrossfadeSeconds) : Message;

/// <summary>The current item and what follows it, in play order (index 0 = current).</summary>
public sealed record QueueWindow(List<TrackOffer> Items) : Message;

/// <summary>Why the session ends (host stopped, kicked, …).</summary>
public sealed record Bye(string Reason) : Message;

/// <summary>Asks the host for the lyrics it has (or finds) for an item in the window.</summary>
public sealed record LyricsRequest(uint Request, string ItemId) : Message;

/// <summary>No content: the host has none. <see cref="Format"/>: plain, lrc or elrc.</summary>
public sealed record LyricsReply(uint Request, string? Provider, string? Format, string? Content) : Message;

/// <summary>Who is in the vibe (the host first).</summary>
public sealed record Presence(List<string> Names) : Message;

/// <summary>A track in the window: what to show and where its bytes come from.</summary>
public sealed record TrackOffer
{
    public required string ItemId { get; init; }
    /// <summary>Opaque id for range reads of the file on the data connection.</summary>
    public required string MediaId { get; init; }
    public required long Size { get; init; }
    public string? Codec { get; init; }
    public required string Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Date { get; init; }
    public int Disc { get; init; }
    public int Position { get; init; }
    public long? DurationMs { get; init; }
    public double? TrackGainDb { get; init; }
    public double? TrackPeak { get; init; }
    public double? AlbumGainDb { get; init; }
    public double? AlbumPeak { get; init; }
    /// <summary>The cover as a readable media id (null: none), and its size.</summary>
    public string? CoverId { get; init; }
    public long CoverSize { get; init; }
    /// <summary>Plays gaplessly after the previous item in the window (album continuations).</summary>
    public bool JoinGapless { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(Message))]
internal sealed partial class WireJson : JsonSerializerContext;
