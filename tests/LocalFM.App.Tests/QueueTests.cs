using LocalFM.App;
using LocalFM.Core;
using LocalFM.Library;

namespace LocalFM.App.Tests;

public class QueueTests
{
    private static TrackInfo T(string title) => new(Ulid.NewUlid(), Ulid.NewUlid(), Ulid.NewUlid(), "/x/" + title, title,
        null, null, null, Ulid.NewUlid(), null, 1, 1, 1000, null, null, null, null, null, null);

    private static string[] Titles(PlayQueue q) => q.Items.Select(i => i.Track.Title).ToArray();

    [Fact]
    public void Insert_next_and_append()
    {
        var q = new PlayQueue();
        q.Replace([T("a"), T("b"), T("c")], 0);
        q.InsertNext([T("x"), T("y")]);
        q.Append([T("z")]);
        Assert.Equal(["a", "x", "y", "b", "c", "z"], Titles(q));
        Assert.Equal("x", q.PeekNext()!.Track.Title);
    }

    [Fact]
    public void Move_keeps_current()
    {
        var q = new PlayQueue();
        q.Replace([T("a"), T("b"), T("c"), T("d")], 1);
        q.Move(3, 0);
        Assert.Equal(["d", "a", "b", "c"], Titles(q));
        Assert.Equal("b", q.Current!.Track.Title);
        Assert.Equal(2, q.CurrentIndex);
    }

    [Fact]
    public void Remove_before_and_at_current()
    {
        var q = new PlayQueue();
        q.Replace([T("a"), T("b"), T("c")], 1);
        q.Remove(q.Items[0].Id);
        Assert.Equal("b", q.Current!.Track.Title);
        q.Remove(q.Current.Id);
        Assert.Equal("c", q.Current!.Track.Title);
    }

    [Fact]
    public void Shuffle_only_touches_upcoming_and_unshuffle_restores()
    {
        var q = new PlayQueue(new Random(42));
        var tracks = Enumerable.Range(0, 20).Select(i => T(i.ToString())).ToList();
        q.Replace(tracks, 5);
        var before = Titles(q);
        q.SetShuffle(true);
        var shuffled = Titles(q);
        Assert.Equal(before[..6], shuffled[..6]);
        Assert.NotEqual(before, shuffled);
        Assert.Equal(before.Order(), shuffled.Order());
        q.SetShuffle(false);
        Assert.Equal(before, Titles(q));
    }

    [Fact]
    public void Repeat_modes()
    {
        var q = new PlayQueue();
        q.Replace([T("a"), T("b")], 1);
        Assert.Null(q.PeekNext());
        q.Repeat = RepeatMode.All;
        Assert.Equal("a", q.PeekNext()!.Track.Title);
        q.Repeat = RepeatMode.One;
        Assert.Equal("b", q.PeekNext()!.Track.Title);
    }

    [Fact]
    public void Failed_items_are_skipped()
    {
        var q = new PlayQueue();
        q.Replace([T("a"), T("b"), T("c")], 0);
        q.MarkFailed(q.Items[1].Id);
        Assert.Equal("c", q.PeekNext()!.Track.Title);
    }

    [Fact]
    public void Clear_upcoming_keeps_history()
    {
        var q = new PlayQueue();
        q.Replace([T("a"), T("b"), T("c")], 1);
        q.ClearUpcoming();
        Assert.Equal(["a", "b"], Titles(q));
    }

    [Fact]
    public void Updated_metadata_keeps_entries_and_files()
    {
        var q = new PlayQueue();
        var a = T("a");
        q.Replace([a, T("b")]);
        var before = q.Items.Select(i => i.Id).ToList();
        int changes = 0;
        q.Changed += () => changes++;

        var edited = a with { Title = "A (edited)", Path = "/elsewhere/a" };
        Assert.True(q.UpdateTracks(new Dictionary<Ulid, TrackInfo> { [a.TrackId] = edited }));

        Assert.Equal(before, q.Items.Select(i => i.Id));
        Assert.Equal("A (edited)", q.Items[0].Track.Title);
        Assert.Equal(a.Path, q.Items[0].Track.Path);   // the file being played never changes under the engine
        Assert.Equal(1, changes);
        Assert.False(q.UpdateTracks(new Dictionary<Ulid, TrackInfo> { [a.TrackId] = edited }));
    }
}
