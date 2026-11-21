using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalFM.Library;

namespace LocalFM.Vibe.Web;

/// <summary>A device (a TV, a browser) that paired with a PIN. Only a hash of its token is kept.</summary>
public sealed record PairedDevice(string Id, string Name, string TokenHash, long PairedAt, long LastSeen);

/// <summary>
/// The devices allowed to use the library share, kept in the kv table. A device proves itself with the random
/// token it got when it paired; removing it here locks it out. Thread-safe.
/// </summary>
public sealed class PairedDevices
{
    public const string Key = "remote.devices";
    private static readonly TimeSpan SeenGranularity = TimeSpan.FromMinutes(5);

    private readonly History? _kv;
    private readonly Lock _gate = new();
    private List<PairedDevice> _devices;

    public PairedDevices(History? kv)
    {
        _kv = kv;
        _devices = Load(kv?.Get(Key));
    }

    public event Action? Changed;

    public IReadOnlyList<PairedDevice> List
    {
        get { lock (_gate) return [.. _devices]; }
    }

    /// <summary>Adds a device and returns the token it must keep.</summary>
    public (PairedDevice Device, string Token) Pair(string name)
    {
        string token = Base64Url(RandomNumberGenerator.GetBytes(32));
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        name = string.Concat(name.Where(c => !char.IsControl(c))).Trim();
        if (name.Length == 0) name = "Device";
        if (name.Length > 40) name = name[..40];
        var d = new PairedDevice(Base64Url(RandomNumberGenerator.GetBytes(6)), name, Hash(token), now, now);
        lock (_gate) _devices = [.. _devices, d];
        Save();
        return (d, token);
    }

    /// <summary>The device the token belongs to, or null.</summary>
    public PairedDevice? Find(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 100) return null;
        var hash = Encoding.ASCII.GetBytes(Hash(token));
        PairedDevice? found = null;
        lock (_gate)
            foreach (var d in _devices)
                if (CryptographicOperations.FixedTimeEquals(hash, Encoding.ASCII.GetBytes(d.TokenHash))) found = d;
        return found;
    }

    /// <summary>Notes that the device was just used (saved at most every few minutes).</summary>
    public void Seen(PairedDevice d)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now - d.LastSeen < SeenGranularity.TotalMilliseconds) return;
        lock (_gate)
        {
            int i = _devices.FindIndex(x => x.Id == d.Id);
            if (i < 0) return;
            _devices = [.. _devices];
            _devices[i] = _devices[i] with { LastSeen = now };
        }
        Save();
    }

    public void Remove(string id)
    {
        lock (_gate) _devices = _devices.Where(d => d.Id != id).ToList();
        Save();
    }

    private void Save()
    {
        string json;
        lock (_gate) json = JsonSerializer.Serialize(_devices, DevicesJson.Default.ListPairedDevice);
        if (_kv is not null) _ = _kv.SetAsync(Key, json);
        Changed?.Invoke();
    }

    private static List<PairedDevice> Load(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try { return JsonSerializer.Deserialize(json, DevicesJson.Default.ListPairedDevice) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    internal static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

[JsonSerializable(typeof(List<PairedDevice>))]
internal sealed partial class DevicesJson : JsonSerializerContext;
