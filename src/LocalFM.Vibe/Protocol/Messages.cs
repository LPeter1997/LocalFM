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
[JsonDerivedType(typeof(LibraryRequest), "lib")]
[JsonDerivedType(typeof(LibraryReply), "libReply")]
public abstract record Message;

public enum ConnectionRole { Control, Data }

/// <summary>First message on every connection. Control connections prove the invite's secret; the data
/// connection proves the token the host handed out in <see cref="Welcome"/>. <see cref="Capabilities"/>: optional
/// extras the peer speaks (<see cref="Capability.Library"/>), so newer peers can opt in without a version bump.</summary>
public sealed record Hello(int Version, ConnectionRole Role, string Secret, string Name, List<string>? Capabilities = null) : Message;

/// <summary><see cref="Capabilities"/>: the extras the host grants this listener.</summary>
public sealed record Welcome(string ListenerId, string DataToken, string HostName, List<string>? Capabilities = null) : Message;

public static class Capability
{
    /// <summary>Browse the host's library and read any of its tracks (<see cref="LibraryRequest"/>).</summary>
    public const string Library = "library";
}

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

/// <summary>A library question (see <see cref="LibraryOp"/>). <see cref="Id"/>: the album, artist, playlist or
/// track the op is about; <see cref="Search"/>: a filter for list ops.</summary>
public sealed record LibraryRequest(uint Request, string Op, string? Id = null, string? Search = null, int? Limit = null) : Message;

/// <summary>The answer to a <see cref="LibraryRequest"/>: <see cref="Error"/> or the fields the op fills.</summary>
public sealed record LibraryReply(uint Request) : Message
{
    public string? Error { get; init; }
    public List<RemoteAlbum>? Albums { get; init; }
    public RemoteAlbum? Album { get; init; }
    public List<RemoteTrack>? Tracks { get; init; }
    public List<RemoteArtist>? Artists { get; init; }
    public RemoteArtistProfile? Artist { get; init; }
    public List<RemotePlaylist>? Playlists { get; init; }
    public RemotePlaylist? Playlist { get; init; }
    public RemoteLyrics? Lyrics { get; init; }
    public RemoteCounts? Counts { get; init; }
}

public static class LibraryOp
{
    public const string Albums = "albums";       // Search?, Id = artist?   → Albums
    public const string Album = "album";         // Id = album             → Album, Tracks
    public const string Artists = "artists";     // Search?                → Artists
    public const string Artist = "artist";       // Id = artist            → Artist, Albums
    public const string Tracks = "tracks";       // Search?, Limit?        → Tracks
    public const string Playlists = "playlists"; //                        → Playlists
    public const string Playlist = "playlist";   // Id = playlist          → Playlist, Tracks
    public const string Lyrics = "lyrics";       // Id = track             → Lyrics (null: none)
    public const string Counts = "counts";       //                        → Counts
    public const string Search = "search";       // Search                 → Albums, Artists, Tracks (a few of each)
}

// Library views for remote peers. Ids only, never file paths: audio is read by track id, pictures by image id
// (see LocalFM.Vibe.Library.MediaResolver).

public sealed record RemoteAlbum(string Id, string Title, string? Artist, string? Date, string? Edition, string? Cover,
    int TrackCount, int OwnedCount);

public sealed record RemoteTrack(string Id, string Title, string? Artist, string? Album, string AlbumId, int Disc, int Position,
    long? DurationMs, string? Cover, string? Codec, double? TrackGainDb, double? TrackPeak);

public sealed record RemoteArtist(string Id, string Name, int AlbumCount, int TrackCount, string? Photo);

public sealed record RemoteArtistProfile(string Id, string Name, string? Type, string? Country, string? Begin, string? End,
    string? Description, string? Bio, string? BioSource, string? Photo, List<string> Genres, List<string> Members, List<string> MemberOf);

public sealed record RemotePlaylist(string Id, string Name, string? Description, int EntryCount);

/// <summary><see cref="Lines"/>: one per line; <c>Ms</c> is null for plain lyrics.</summary>
public sealed record RemoteLyrics(string? Provider, string Format, List<RemoteLyricLine> Lines);

public sealed record RemoteLyricLine(long? Ms, string Text);

public sealed record RemoteCounts(int Tracks, int Albums, int Artists);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(Message))]
internal sealed partial class WireJson : JsonSerializerContext;
