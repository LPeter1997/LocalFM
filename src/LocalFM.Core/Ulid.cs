using System.Buffers.Binary;
using System.Security.Cryptography;

namespace LocalFM.Core;

/// <summary>
/// Universally unique, lexicographically sortable identifier (48-bit ms timestamp + 80 random bits),
/// rendered as 26 Crockford base32 characters. Every knowledge-base entity uses one, so records can be
/// exported/merged between libraries without id collisions.
/// </summary>
public readonly struct Ulid : IEquatable<Ulid>, IComparable<Ulid>
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private readonly ulong _hi; // 48 bits time + 16 bits random
    private readonly ulong _lo; // 64 bits random

    private Ulid(ulong hi, ulong lo) { _hi = hi; _lo = lo; }

    public static readonly Ulid Empty = default;

    public static Ulid NewUlid() => NewUlid(DateTimeOffset.UtcNow);

    public static Ulid NewUlid(DateTimeOffset time)
    {
        Span<byte> rnd = stackalloc byte[10];
        RandomNumberGenerator.Fill(rnd);
        ulong ms = (ulong)time.ToUnixTimeMilliseconds() & 0xFFFF_FFFF_FFFFUL;
        ulong hi = (ms << 16) | BinaryPrimitives.ReadUInt16BigEndian(rnd);
        ulong lo = BinaryPrimitives.ReadUInt64BigEndian(rnd[2..]);
        return new Ulid(hi, lo);
    }

    public DateTimeOffset Time => DateTimeOffset.FromUnixTimeMilliseconds((long)(_hi >> 16));

    public bool IsEmpty => _hi == 0 && _lo == 0;

    public override string ToString()
    {
        Span<char> c = stackalloc char[26];
        // 128 bits -> 26 chars of 5 bits; first char carries the top 3 bits.
        UInt128 v = ((UInt128)_hi << 64) | _lo;
        for (int i = 25; i >= 0; i--)
        {
            c[i] = Alphabet[(int)(v & 31)];
            v >>= 5;
        }
        return new string(c);
    }

    public static Ulid Parse(string s) =>
        TryParse(s, out var u) ? u : throw new FormatException($"Invalid ULID '{s}'.");

    public static bool TryParse(ReadOnlySpan<char> s, out Ulid result)
    {
        result = default;
        if (s.Length != 26) return false;
        UInt128 v = 0;
        foreach (char ch in s)
        {
            int d = Decode(ch);
            if (d < 0) return false;
            v = (v << 5) | (uint)d;
        }
        result = new Ulid((ulong)(v >> 64), (ulong)v);
        return true;
    }

    private static int Decode(char ch)
    {
        ch = char.ToUpperInvariant(ch);
        return ch switch
        {
            'O' => 0,
            'I' or 'L' => 1,
            _ => Alphabet.IndexOf(ch),
        };
    }

    public bool Equals(Ulid other) => _hi == other._hi && _lo == other._lo;
    public override bool Equals(object? obj) => obj is Ulid u && Equals(u);
    public override int GetHashCode() => HashCode.Combine(_hi, _lo);
    public int CompareTo(Ulid other) => _hi != other._hi ? _hi.CompareTo(other._hi) : _lo.CompareTo(other._lo);
    public static bool operator ==(Ulid a, Ulid b) => a.Equals(b);
    public static bool operator !=(Ulid a, Ulid b) => !a.Equals(b);
}
