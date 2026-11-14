using LocalFM.Core;
using LocalFM.Library;
using LocalFM.Library.Tests;

using LocalFM.App;

namespace LocalFM.App.Tests;

public class SoundTests
{
    [Fact]
    public void Settings_round_trip_through_the_store()
    {
        using var lib = new TempLibrary();
        var kv = new History(lib.Db);
        var s = new SoundSettings
        {
            EqEnabled = true, Gains = [1, 2, 3, 4, 5, -1, -2, -3, -4, -5.5f], PreampDb = -2, PreventClipping = false,
            Preset = "Mine", CrossfadeSeconds = 6, GaplessAlbums = false, ReplayGain = ReplayGainMode.Track,
            CustomPresets = [new EqPreset("Mine", [1, 2, 3, 4, 5, -1, -2, -3, -4, -5.5f])],
        };
        s.SaveAsync(kv).Wait();
        var t = SoundSettings.Load(kv);
        Assert.True(t.EqEnabled);
        Assert.Equal(s.Gains, t.Gains);
        Assert.Equal(-2, t.PreampDb);
        Assert.False(t.PreventClipping);
        Assert.Equal("Mine", t.Preset);
        Assert.Equal(6, t.CrossfadeSeconds);
        Assert.False(t.GaplessAlbums);
        Assert.Equal(ReplayGainMode.Track, t.ReplayGain);
        Assert.Equal("Mine", Assert.Single(t.CustomPresets).Name);
    }

    [Fact]
    public void Response_curve_matches_the_bands()
    {
        var s = new SoundSettings { EqEnabled = true, PreventClipping = false, Gains = [0, 0, 0, 0, 0, 6, 0, 0, 0, 0] };
        Assert.InRange(s.ResponseDb(1000), 5.8, 6.2);
        Assert.InRange(s.ResponseDb(100), -0.3, 0.3);
        s.PreventClipping = true;
        Assert.InRange(s.ResponseDb(1000), -0.2, 0.2); // preamp takes the boost back
        s.EqEnabled = false;
        Assert.Equal(0, s.ResponseDb(1000));
        Assert.Empty(s.Bands());
    }

    [Fact]
    public void Album_continuation_is_detected()
    {
        var rel = Ulid.NewUlid();
        TrackInfo T(Ulid release, int disc, int pos) => new(Ulid.NewUlid(), Ulid.NewUlid(), Ulid.NewUlid(), "/x", "t",
            null, null, null, release, null, disc, pos, 1000, null, null, null, null, null, null);
        Assert.True(PlayerService.Continues(T(rel, 1, 3), T(rel, 1, 4)));
        Assert.True(PlayerService.Continues(T(rel, 1, 12), T(rel, 2, 1)));
        Assert.False(PlayerService.Continues(T(rel, 1, 3), T(rel, 1, 5)));
        Assert.False(PlayerService.Continues(T(rel, 1, 3), T(Ulid.NewUlid(), 1, 4)));
    }
}
