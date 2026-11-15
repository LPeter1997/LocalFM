using System.Runtime.InteropServices;

namespace LocalFM.Native;

[StructLayout(LayoutKind.Sequential)]
public struct OutputInfo
{
    public int SampleRate;
    public int Channels;
    public long RingFrames;
}

[StructLayout(LayoutKind.Sequential)]
public struct DecoderInfo
{
    public long DurationFrames;
    public int SourceRate;
    public int SourceChannels;
    public int OutputRate;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct ProbeInfo
{
    public long DurationMs;
    public long BitRate;
    public int SampleRate;
    public int Channels;
    public int BitsPerSample;
    public int TagCount;
    public int PictureSize;
    public fixed byte Codec[32];
    public fixed byte Container[32];
}

public enum EqBandType
{
    Peak = 0,
    LowShelf = 1,
    HighShelf = 2,
}

/// <summary>One equalizer filter (mirrors lfm_eq_band).</summary>
[StructLayout(LayoutKind.Sequential)]
public record struct EqBand(EqBandType Type, float Frequency, float Q, float GainDb);

public enum OutputMode
{
    Device = 0,
    Null = 1,
    Offline = 2,
}

/// <summary>Raw bindings to localfm_native (see native/localfm_native/localfm_native.h).</summary>
public static unsafe partial class NativeMethods
{
    private const string Lib = "localfm_native";

    // output
    [LibraryImport(Lib, EntryPoint = "lfm_output_open")]
    public static partial int OutputOpen(int mode, int rate, int ringMs, out nint output);
    [LibraryImport(Lib, EntryPoint = "lfm_output_close")]
    public static partial void OutputClose(nint output);
    [LibraryImport(Lib, EntryPoint = "lfm_output_get_info")]
    public static partial void OutputGetInfo(nint output, out OutputInfo info);
    [LibraryImport(Lib, EntryPoint = "lfm_output_read_clock")]
    public static partial long OutputReadClock(nint output, out long advance, out long period, out long ageNs);
    [LibraryImport(Lib, EntryPoint = "lfm_output_latency_frames")]
    public static partial long OutputLatencyFrames(nint output);
    [LibraryImport(Lib, EntryPoint = "lfm_output_write")]
    public static partial int OutputWrite(nint output, float* frames, int count);
    [LibraryImport(Lib, EntryPoint = "lfm_output_writable")]
    public static partial long OutputWritable(nint output);
    [LibraryImport(Lib, EntryPoint = "lfm_output_write_index")]
    public static partial long OutputWriteIndex(nint output);
    [LibraryImport(Lib, EntryPoint = "lfm_output_flush")]
    public static partial void OutputFlush(nint output);
    [LibraryImport(Lib, EntryPoint = "lfm_output_truncate")]
    public static partial int OutputTruncate(nint output, long writeIndex);
    [LibraryImport(Lib, EntryPoint = "lfm_output_read_index")]
    public static partial long OutputReadIndex(nint output);
    [LibraryImport(Lib, EntryPoint = "lfm_output_underruns")]
    public static partial long OutputUnderruns(nint output);
    [LibraryImport(Lib, EntryPoint = "lfm_output_set_volume")]
    public static partial void OutputSetVolume(nint output, float linear);
    [LibraryImport(Lib, EntryPoint = "lfm_output_set_paused")]
    public static partial void OutputSetPaused(nint output, int paused);
    [LibraryImport(Lib, EntryPoint = "lfm_output_set_eq")]
    public static partial void OutputSetEq(nint output, EqBand* bands, int count, float preampDb);
    [LibraryImport(Lib, EntryPoint = "lfm_output_tap")]
    public static partial long OutputTap(nint output, float* frames, int count);
    public const int TapFrames = 4096;
    [LibraryImport(Lib, EntryPoint = "lfm_output_render")]
    public static partial void OutputRender(nint output, float* frames, int count);

    // decoder
    [LibraryImport(Lib, EntryPoint = "lfm_decoder_open", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int DecoderOpen(string path, int outputRate, out nint decoder);
    public const int SeekSize = 0x10000;
    [LibraryImport(Lib, EntryPoint = "lfm_decoder_open_io")]
    public static partial int DecoderOpenIo(delegate* unmanaged<nint, byte*, int, int> read,
        delegate* unmanaged<nint, long, int, long> seek, nint user, int outputRate, out nint decoder);
    [LibraryImport(Lib, EntryPoint = "lfm_decoder_close")]
    public static partial void DecoderClose(nint decoder);
    [LibraryImport(Lib, EntryPoint = "lfm_decoder_get_info")]
    public static partial void DecoderGetInfo(nint decoder, out DecoderInfo info);
    [LibraryImport(Lib, EntryPoint = "lfm_decoder_read")]
    public static partial int DecoderRead(nint decoder, float* frames, int count);
    [LibraryImport(Lib, EntryPoint = "lfm_decoder_seek")]
    public static partial int DecoderSeek(nint decoder, long frame);

    // probe
    [LibraryImport(Lib, EntryPoint = "lfm_probe_open", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int ProbeOpen(string path, out nint probe);
    [LibraryImport(Lib, EntryPoint = "lfm_probe_close")]
    public static partial void ProbeClose(nint probe);
    [LibraryImport(Lib, EntryPoint = "lfm_probe_get_info")]
    public static partial void ProbeGetInfo(nint probe, out ProbeInfo info);
    [LibraryImport(Lib, EntryPoint = "lfm_probe_get_tag")]
    public static partial int ProbeGetTag(nint probe, int index, out byte* key, out byte* value);
    [LibraryImport(Lib, EntryPoint = "lfm_probe_get_picture")]
    public static partial byte* ProbeGetPicture(nint probe, out byte* mime);
    [LibraryImport(Lib, EntryPoint = "lfm_probe_next_packet")]
    public static partial int ProbeNextPacket(nint probe, out byte* data);

    [LibraryImport(Lib, EntryPoint = "lfm_version")]
    public static partial byte* Version();
}
