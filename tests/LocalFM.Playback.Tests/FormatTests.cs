using LocalFM.Native;

namespace LocalFM.Playback.Tests;

/// <summary>
/// The formats the scanner accepts decode through the FFmpeg LocalFM runs with: the audio-only build it ships
/// (native/ffmpeg/build.sh enables components one by one, so a missing demuxer or decoder shows up here), or the
/// system's on a Linux dev build. Formats this machine's ffmpeg CLI cannot write are skipped.
/// </summary>
public class FormatTests
{
    public static TheoryData<string, string> Formats => new()
    {
        { "mp3", "-c:a libmp3lame" },
        { "flac", "" },
        { "ogg", "-c:a libvorbis" },
        { "opus", "-c:a libopus" },
        { "spx", "-c:a libspeex -ar 16000" },
        { "m4a", "-c:a aac" },
        { "m4b", "-c:a aac -f mp4" },
        { "mp4", "-c:a aac" },
        { "aac", "-c:a aac" },
        { "alac.m4a", "-c:a alac" },
        { "wav", "" },
        { "aiff", "" },
        { "aifc", "-c:a pcm_s16be -f aiff" },
        { "wv", "-c:a wavpack" },
        { "tta", "" },
        { "wma", "-c:a wmav2" },
        { "mka", "-c:a flac" },
        { "webm", "-c:a libopus" },
        { "ac3.mka", "-c:a ac3" },
    };

    [SkippableTheory]
    [MemberData(nameof(Formats))]
    public void Decodes(string ext, string encoder)
    {
        Skip.IfNot(Fixtures.HaveFfmpeg);
        Directory.CreateDirectory(Fixtures.Dir);
        string file = Path.Combine(Fixtures.Dir, $"format.{ext}");
        if (!File.Exists(file))
        {
            try
            {
                Fixtures.Run($"-f lavfi -i \"sine=frequency=440:duration=1\" -ac 2 {encoder} -metadata title=Format \"{file}\"");
            }
            catch (InvalidOperationException ex)
            {
                Skip.If(true, $"this ffmpeg cannot write .{ext}: {ex.Message.Split('\n')[^1]}");
            }
        }

        using (var probe = MediaProbe.Open(file))
        {
            Assert.InRange(probe.Duration?.TotalSeconds ?? 0, 0.8, 1.3);
            Assert.True(probe.SampleRate > 0, $"{ext}: no sample rate");
        }
        using var dec = Decoder.Open(file, 48000);
        var buf = new float[4096 * 2];
        long frames = 0;
        int n;
        while ((n = dec.Read(buf)) > 0) frames += n;
        Assert.InRange(frames, 48000 * 8 / 10, 48000 * 13 / 10);
    }

    /// <summary>Embedded covers come out of the container without any image decoder.</summary>
    [SkippableTheory]
    [InlineData("mp3")]
    [InlineData("flac")]
    [InlineData("m4a")]
    public void Reads_embedded_covers(string ext)
    {
        Skip.IfNot(Fixtures.HaveFfmpeg);
        Directory.CreateDirectory(Fixtures.Dir);
        string png = Path.Combine(Fixtures.Dir, "cover.png");
        if (!File.Exists(png)) Fixtures.Run($"-f lavfi -i color=c=red:s=64x64 -frames:v 1 \"{png}\"");
        string file = Path.Combine(Fixtures.Dir, $"cover.{ext}");
        if (!File.Exists(file))
            Fixtures.Run($"-f lavfi -i \"sine=duration=1\" -i \"{png}\" -map 0:a -map 1:v -c:v copy -disposition:v attached_pic -ac 2 \"{file}\"");

        using var probe = MediaProbe.Open(file);
        var picture = probe.Picture;
        Assert.NotNull(picture);
        Assert.Equal((byte)0x89, picture.Value.Data[0]); // PNG signature
    }
}
