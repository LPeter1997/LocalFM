using LocalFM.Native;

namespace LocalFM.Playback.Tests;

public class TapTests
{
    [Fact]
    public void Tap_holds_what_was_heard_before_volume()
    {
        using var output = AudioOutput.Open(OutputMode.Offline, 48000, 1000);
        output.SetVolume(0.25f);
        var input = new float[2048 * 2];
        for (int i = 0; i < 2048; i++) input[i * 2] = input[i * 2 + 1] = MathF.Sin(i * 0.05f) * 0.5f;
        Assert.Equal(2048, output.Write(input));

        var heard = new float[2048 * 2];
        output.Render(heard);
        var tap = new float[1024 * 2];
        long total = output.ReadTap(tap);
        Assert.Equal(2048, total);

        // The last 1024 frames, at full scale (the knob does not shrink the picture), as the speaker got them.
        for (int i = 0; i < 1024; i++)
        {
            float expected = input[(1024 + i) * 2];
            Assert.Equal(expected, tap[i * 2], 3);
        }

        // Paused: the tap fills with silence so pictures settle.
        output.SetPaused(true);
        output.Render(new float[8192 * 2]);
        output.ReadTap(tap);
        Assert.All(tap, v => Assert.Equal(0f, v));
    }
}
