using LocalFM.Native;

namespace LocalFM.Playback.Tests;

public class OutputStatsTests
{
    [Fact]
    public void Offline_output_counts_underruns_and_describes_itself()
    {
        using var output = AudioOutput.Open(OutputMode.Offline, 48000, 1000);
        Assert.Equal("offline, 48000 Hz", output.Describe());

        var block = new float[200];
        output.Render(block); // empty before anything was written: not an underrun
        output.Write(new float[20]);
        output.Render(block); // runs dry after 10 frames
        var s = output.ReadStats();
        Assert.Equal(1, s.Underruns);
        Assert.Equal(0, s.LateCallbacks); // no device, no callbacks
        Assert.Equal(0, s.Stopped);
    }

    [Fact]
    public void Device_output_names_its_backend()
    {
        using var output = AudioOutput.Open(OutputMode.Null);
        Assert.StartsWith("Null, ", output.Describe());
    }
}
