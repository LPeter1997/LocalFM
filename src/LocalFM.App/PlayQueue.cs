using LocalFM.Core;
using LocalFM.Library;

namespace LocalFM.App;

public enum RepeatMode { Off, All, One }

/// <summary>One entry in the play queue. The id is unique per entry, so the same track can be queued twice.</summary>
public sealed record QueueItem(Ulid Id, TrackInfo Track)
{
    public static QueueItem For(TrackInfo t) => new(Ulid.NewUlid(), t);
}

/// <summary>
/// The "now playing" list: items before <see cref="CurrentIndex"/> were played, items after it are up next.
/// Pure logic (no I/O, no engine); not thread-safe — <see cref="PlayerService"/> serializes access.
/// </summary>
public sealed class PlayQueue
{
    private readonly List<QueueItem> _items = new();
    private readonly HashSet<Ulid> _failed = new();
    private List<Ulid>? _originalOrder; // order before shuffle, for unshuffle
    private readonly Random _random;

    public PlayQueue(Random? random = null) => _random = random ?? Random.Shared;

    public IReadOnlyList<QueueItem> Items => _items;
    public int CurrentIndex { get; private set; } = -1;
    public QueueItem? Current => CurrentIndex >= 0 && CurrentIndex < _items.Count ? _items[CurrentIndex] : null;
    public RepeatMode Repeat { get; set; }
    public bool Shuffled => _originalOrder is not null;

    /// <summary>Raised after any change to items or the current index.</summary>
    public event Action? Changed;

    public void Replace(IEnumerable<TrackInfo> tracks, int startIndex = 0)
    {
        _items.Clear();
        _items.AddRange(tracks.Select(QueueItem.For));
        _failed.Clear();
        CurrentIndex = _items.Count == 0 ? -1 : Math.Clamp(startIndex, 0, _items.Count - 1);
        if (Shuffled)
        {
            _originalOrder = _items.Select(i => i.Id).ToList();
            ShuffleRange(CurrentIndex + 1);
        }
        Changed?.Invoke();
    }

    /// <summary>Swaps in fresh metadata (after an edit) for queued tracks. Entries, order and files stay the same,
    /// so playback is unaffected. Returns whether anything changed.</summary>
    public bool UpdateTracks(IReadOnlyDictionary<Ulid, TrackInfo> fresh)
    {
        bool any = false;
        for (int i = 0; i < _items.Count; i++)
        {
            var old = _items[i].Track;
            if (!fresh.TryGetValue(old.TrackId, out var t)) continue;
            t = t with { Path = old.Path, FileId = old.FileId };
            if (t == old) continue;
            _items[i] = _items[i] with { Track = t };
            any = true;
        }
        if (any) Changed?.Invoke();
        return any;
    }

    /// <summary>Restores a saved queue exactly (no shuffling).</summary>
    public void Restore(IEnumerable<QueueItem> items, int currentIndex)
    {
        _items.Clear();
        _items.AddRange(items);
        _failed.Clear();
        _originalOrder = null;
        CurrentIndex = _items.Count == 0 ? -1 : Math.Clamp(currentIndex, 0, _items.Count - 1);
        Changed?.Invoke();
    }

    public void Append(IEnumerable<TrackInfo> tracks)
    {
        var added = tracks.Select(QueueItem.For).ToList();
        _items.AddRange(added);
        _originalOrder?.AddRange(added.Select(a => a.Id));
        if (CurrentIndex < 0 && _items.Count > 0) CurrentIndex = 0;
        Changed?.Invoke();
    }

    /// <summary>Inserts right after the current item ("play next"), keeping the given order.</summary>
    public void InsertNext(IEnumerable<TrackInfo> tracks)
    {
        var added = tracks.Select(QueueItem.For).ToList();
        int at = CurrentIndex + 1;
        _items.InsertRange(at, added);
        if (_originalOrder is not null)
        {
            int origAt = Current is { } c ? _originalOrder.IndexOf(c.Id) + 1 : 0;
            _originalOrder.InsertRange(origAt, added.Select(a => a.Id));
        }
        if (CurrentIndex < 0 && _items.Count > 0) CurrentIndex = 0;
        Changed?.Invoke();
    }

    public void Move(int from, int to)
    {
        if (from == to || from < 0 || from >= _items.Count) return;
        to = Math.Clamp(to, 0, _items.Count - 1);
        var item = _items[from];
        var current = Current;
        _items.RemoveAt(from);
        _items.Insert(to, item);
        CurrentIndex = current is null ? -1 : _items.IndexOf(current);
        Changed?.Invoke();
    }

    public void Remove(Ulid itemId)
    {
        int idx = _items.FindIndex(i => i.Id == itemId);
        if (idx < 0) return;
        _items.RemoveAt(idx);
        _originalOrder?.Remove(itemId);
        if (idx < CurrentIndex) CurrentIndex--;
        else if (idx == CurrentIndex && CurrentIndex >= _items.Count) CurrentIndex = _items.Count - 1;
        Changed?.Invoke();
    }

    public void ClearUpcoming()
    {
        if (CurrentIndex + 1 < _items.Count)
        {
            foreach (var i in _items.Skip(CurrentIndex + 1)) _originalOrder?.Remove(i.Id);
            _items.RemoveRange(CurrentIndex + 1, _items.Count - CurrentIndex - 1);
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        _items.Clear();
        _failed.Clear();
        _originalOrder = null;
        CurrentIndex = -1;
        Changed?.Invoke();
    }

    /// <summary>Turns shuffle on (shuffles upcoming items) or off (restores their original order).</summary>
    public void SetShuffle(bool on)
    {
        if (on == Shuffled) { if (on) Reshuffle(); return; }
        if (on)
        {
            _originalOrder = _items.Select(i => i.Id).ToList();
            ShuffleRange(CurrentIndex + 1);
        }
        else
        {
            var order = _originalOrder!.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
            int start = CurrentIndex + 1;
            var upcoming = _items.Skip(start).OrderBy(i => order.TryGetValue(i.Id, out int o) ? o : int.MaxValue).ToList();
            _items.RemoveRange(start, _items.Count - start);
            _items.AddRange(upcoming);
            _originalOrder = null;
        }
        Changed?.Invoke();
    }

    /// <summary>Shuffles what is up next again.</summary>
    public void Reshuffle()
    {
        ShuffleRange(CurrentIndex + 1);
        Changed?.Invoke();
    }

    private void ShuffleRange(int start)
    {
        for (int i = _items.Count - 1; i > start; i--)
        {
            int j = _random.Next(start, i + 1);
            (_items[i], _items[j]) = (_items[j], _items[i]);
        }
    }

    public void MarkFailed(Ulid itemId) => _failed.Add(itemId);

    /// <summary>The item that should play after the current one, honouring repeat mode and skipping failures.</summary>
    public QueueItem? PeekNext()
    {
        if (Current is null) return null;
        if (Repeat == RepeatMode.One) return Current;
        for (int step = 1; step <= _items.Count; step++)
        {
            int i = CurrentIndex + step;
            if (i >= _items.Count)
            {
                if (Repeat != RepeatMode.All) return null;
                i %= _items.Count;
            }
            if (!_failed.Contains(_items[i].Id)) return _items[i];
        }
        return null;
    }

    public QueueItem? PeekPrevious() => CurrentIndex > 0 ? _items[CurrentIndex - 1]
        : Repeat == RepeatMode.All && _items.Count > 0 ? _items[^1] : null;

    /// <summary>Makes the item with this id current. Returns false if it is not in the queue.</summary>
    public bool SetCurrent(Ulid itemId)
    {
        int idx = _items.FindIndex(i => i.Id == itemId);
        if (idx < 0) return false;
        if (idx != CurrentIndex)
        {
            CurrentIndex = idx;
            Changed?.Invoke();
        }
        return true;
    }
}
