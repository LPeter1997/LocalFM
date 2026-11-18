using Android.Content;
using Android.Net.Wifi;
using Android.OS;
using LocalFM.Core;
using LocalFM.Vibe;

namespace LocalFM.Android;

/// <summary>
/// What a vibe needs from the phone's network. Android drops multicast packets unless an app holds a multicast lock,
/// so LAN discovery (the vibe panel looking for nearby vibes) and announcing our own vibe hold one. During a vibe a
/// Wi-Fi lock keeps the radio from dozing with the screen off, so the stream and the clock sync keep up.
/// </summary>
internal sealed class NetworkLocks
{
    private readonly WifiManager.MulticastLock? _multicast;
    private readonly WifiManager.WifiLock? _wifi;

    public NetworkLocks(Context context)
    {
        var wifi = (WifiManager?)context.GetSystemService(Context.WifiService);
        _multicast = wifi?.CreateMulticastLock("LocalFM:vibe-discovery");
        _multicast?.SetReferenceCounted(false);
        _wifi = Build.VERSION.SdkInt >= BuildVersionCodes.Q
            ? wifi?.CreateWifiLock(global::Android.Net.WifiMode.FullLowLatency, "LocalFM:vibe")
            : wifi?.CreateWifiLock(global::Android.Net.WifiMode.FullHighPerf, "LocalFM:vibe");
        _wifi?.SetReferenceCounted(false);
    }

    /// <param name="discovering">The vibe panel is looking for nearby vibes, or our vibe is announced.</param>
    /// <param name="inVibe">Hosting or listening.</param>
    public void Update(bool discovering, bool inVibe)
    {
        try
        {
            Set(_multicast, discovering || inVibe, l => l.IsHeld, l => l.Acquire(), l => l.Release());
            Set(_wifi, inVibe, l => l.IsHeld, l => l.Acquire(), l => l.Release());
        }
        catch (Exception ex) { Log.Warn("Android", "Network locks", ex); }
    }

    private static void Set<T>(T? @lock, bool want, Func<T, bool> held, Action<T> acquire, Action<T> release) where T : class
    {
        if (@lock is null || held(@lock) == want) return;
        if (want) acquire(@lock);
        else release(@lock);
    }

    public static bool InVibe(VibeService vibe) => vibe.Role != VibeRole.None;
}
