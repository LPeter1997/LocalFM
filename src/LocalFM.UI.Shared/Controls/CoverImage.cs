using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LocalFM.Core;

namespace LocalFM.UI.Controls;

/// <summary>
/// An image that loads a cover file off the UI thread, decoded at the size it is shown, with a shared
/// LRU cache. Scrolling a big album grid never decodes full-size JPEGs on the UI thread.
/// </summary>
public sealed class CoverImage : Image
{
    public static readonly StyledProperty<string?> PathProperty =
        AvaloniaProperty.Register<CoverImage, string?>(nameof(Path));

    public static readonly StyledProperty<int> DecodeWidthProperty =
        AvaloniaProperty.Register<CoverImage, int>(nameof(DecodeWidth), 256);

    private const int CacheSize = 600;
    private static readonly Dictionary<(string, int), LinkedListNode<((string, int) Key, Bitmap Bmp)>> Cache = new();
    private static readonly LinkedList<((string, int) Key, Bitmap Bmp)> Lru = new();
    private static readonly Lock CacheLock = new();
    private static readonly SemaphoreSlim Decoders = new(Math.Max(2, Environment.ProcessorCount / 2));

    private int _generation;

    static CoverImage()
    {
        PathProperty.Changed.AddClassHandler<CoverImage>((c, _) => c.Load());
        DecodeWidthProperty.Changed.AddClassHandler<CoverImage>((c, _) => c.Load());
        StretchProperty.OverrideDefaultValue<CoverImage>(Avalonia.Media.Stretch.UniformToFill);
    }

    public string? Path
    {
        get => GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    public int DecodeWidth
    {
        get => GetValue(DecodeWidthProperty);
        set => SetValue(DecodeWidthProperty, value);
    }

    private void Load()
    {
        int gen = ++_generation;
        string? path = Path;
        int width = DecodeWidth;
        if (string.IsNullOrEmpty(path)) { Source = null; return; }

        var key = (path, width);
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var node))
            {
                Lru.Remove(node);
                Lru.AddFirst(node);
                Source = node.Value.Bmp;
                return;
            }
        }
        Source = null;
        _ = Task.Run(async () =>
        {
            await Decoders.WaitAsync().ConfigureAwait(false);
            Bitmap? bmp = null;
            try
            {
                if (gen != _generation) return;
                await using var fs = File.OpenRead(path);
                bmp = Bitmap.DecodeToWidth(fs, width, BitmapInterpolationMode.HighQuality);
            }
            catch (Exception ex)
            {
                Log.Debug("Cover", $"Cannot load '{path}': {ex.Message}");
                return;
            }
            finally { Decoders.Release(); }

            lock (CacheLock)
            {
                if (!Cache.ContainsKey(key))
                {
                    Cache[key] = Lru.AddFirst((key, bmp));
                    // Evicted bitmaps are not disposed: an off-screen control may still show one; the GC frees them.
                    while (Lru.Count > CacheSize)
                    {
                        Cache.Remove(Lru.Last!.Value.Key);
                        Lru.RemoveLast();
                    }
                }
            }
            Dispatcher.UIThread.Post(() => { if (gen == _generation) Source = bmp; });
        });
    }
}
