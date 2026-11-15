using System.IO.Pipelines;
using System.Net;
using LocalFM.Vibe.Client;
using LocalFM.Vibe.Net;
using LocalFM.Vibe.Protocol;

namespace LocalFM.Vibe.Tests;

public class ProtocolTests
{
    [Fact]
    public void Invite_round_trips()
    {
        var invite = new Invite([IPAddress.Parse("192.168.1.20"), IPAddress.Parse("100.101.102.103"), IPAddress.Parse("2001:db8::1")],
            47811, new byte[32].Select((_, i) => (byte)i).ToArray(), new byte[16].Select((_, i) => (byte)(200 + i)).ToArray(), "Péter");
        string code = invite.Encode();
        Assert.StartsWith("lfmvibe1-", code);
        Assert.True(code.Length < 160, code);
        Assert.True(Invite.TryDecode("  " + code[..20] + "\n" + code[20..] + " ", out var back)); // pasted with line breaks
        Assert.Equal(invite.Addresses, back.Addresses);
        Assert.Equal(invite.Port, back.Port);
        Assert.Equal(invite.Fingerprint, back.Fingerprint);
        Assert.Equal(invite.Secret, back.Secret);
        Assert.Equal("Péter", back.HostName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("lfmvibe1-")]
    [InlineData("lfmvibe1-AAAA")]
    [InlineData("lfmvibe1-!!!!")]
    public void Garbage_is_not_an_invite(string text) => Assert.False(Invite.TryDecode(text, out _));

    [Fact]
    public async Task Frames_round_trip()
    {
        var pipe = new Pipe();
        await using var writer = new Wire(pipe.Writer.AsStream());
        await using var reader = new Wire(pipe.Reader.AsStream());

        var offer = new TrackOffer { ItemId = "i", MediaId = "m", Size = 123, Title = "Song", Artist = "A", DurationMs = 1000, JoinGapless = true };
        await writer.SendAsync(new QueueWindow([offer]));
        await writer.SendAsync(new Timeline("i", 5_000_000, 42, true, 2.5));
        await writer.SendRangeRequestAsync(7, "m", 1L << 33, 65536);
        await writer.SendRangeReplyAsync(7, 0, new byte[] { 1, 2, 3 });

        var w = Assert.IsType<QueueWindow>(await reader.ReadMessageAsync());
        Assert.Equal(offer, w.Items.Single());
        Assert.Equal(new Timeline("i", 5_000_000, 42, true, 2.5), await reader.ReadMessageAsync());
        var req = (await reader.ReadAsync())!.Value.AsRangeRequest();
        Assert.Equal((7u, "m", 1L << 33, 65536), req);
        var (id, status, data) = (await reader.ReadAsync())!.Value.AsRangeReply();
        Assert.Equal(7u, id);
        Assert.Equal(0, status);
        Assert.Equal(new byte[] { 1, 2, 3 }, data.ToArray());
    }

    [Fact]
    public void Clock_sync_prefers_the_fastest_round_trip()
    {
        var c = new ClockSync();
        const long offset = 5_000_000; // host is 5 s ahead
        // slow, asymmetric round trips (queueing on the way back) pull the estimate off …
        c.Add(0, 1_000 + offset, 41_000);
        Assert.InRange(c.OffsetUs - offset, -21_000, -19_000);
        // … a quick symmetric one fixes it
        c.Add(100_000, 101_000 + offset, 102_000);
        Assert.Equal(offset, c.OffsetUs);
        Assert.Equal(2_000, c.BestRttUs);
        Assert.Equal(1_000_000 + offset, c.ToHost(1_000_000));
    }
}
