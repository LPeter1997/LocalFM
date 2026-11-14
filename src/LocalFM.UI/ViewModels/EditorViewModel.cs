using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalFM.Core;
using LocalFM.Library;
using LocalFM.Library.Edit;

namespace LocalFM.UI.ViewModels;

/// <summary>One editable field with its lock. Changing the value locks it (the person's word wins over lookups).</summary>
public sealed partial class EditField : ObservableObject
{
    public EditField(string entityType, Ulid id, string field, string label, string? value, FieldState? state,
        IReadOnlyList<string>? suggestions = null, string? placeholder = null)
    {
        Placeholder = placeholder;
        EntityType = entityType;
        Id = id;
        Field = field;
        Label = label;
        Original = value ?? "";
        _value = Original;
        OriginalLocked = state?.Locked ?? false;
        _locked = OriginalLocked;
        Source = Describe(state?.Source);
        Suggestions = suggestions;
    }

    public string EntityType { get; }
    public Ulid Id { get; }
    public string Field { get; }
    public string Label { get; }
    public string Original { get; }
    public bool OriginalLocked { get; }
    public string Source { get; }
    public IReadOnlyList<string>? Suggestions { get; }
    /// <summary>What an empty field means (a track without its own artist is by the album artist).</summary>
    public string? Placeholder { get; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsChanged))] private string _value;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LockTip))] private bool _locked;

    public bool IsChanged => (Value ?? "").Trim() != Original;
    public bool LockChanged => Locked != OriginalLocked;
    public string LockTip => Locked ? "Locked: online lookups never change this (click to unlock)" : "Click to lock against online lookups";

    partial void OnValueChanged(string value)
    {
        if (IsChanged) Locked = true;
        else Locked = OriginalLocked;
    }

    private static string Describe(string? source) => source switch
    {
        null or "tags" => "from file tags",
        "user" => "edited by you",
        "cleanup" => "tidied up",
        "musicbrainz" => "from MusicBrainz",
        var s => $"from {s}",
    };
}

public sealed class TrackEditRow(TrackEditData t, IReadOnlyList<string> artists, bool focused, string? albumArtist)
{
    public Ulid TrackId { get; } = t.TrackId;
    public bool Focused { get; } = focused;
    public EditField Disc { get; } = new("track", t.TrackId, "disc", "Disc", t.Disc.ToString(), t.States.GetValueOrDefault("disc"));
    public EditField Position { get; } = new("track", t.TrackId, "position", "#", t.Position.ToString(), t.States.GetValueOrDefault("position"));
    public EditField Title { get; } = new("track", t.TrackId, "title", "Title", t.Title, t.States.GetValueOrDefault("title"));
    public EditField Artist { get; } = new("track", t.TrackId, "artist", "Artist", t.Artist, t.States.GetValueOrDefault("artist"), artists, albumArtist);
    public IEnumerable<EditField> Fields => [Disc, Position, Title, Artist];
}

/// <summary>
/// Edits an album and its tracks in one place. Saved changes lock their fields; locks can also be toggled by hand.
/// Writes go through the library writer (never the playback path); the queue picks up new titles afterwards.
/// </summary>
public sealed partial class AlbumEditorViewModel(MainViewModel main, Ulid releaseId, Ulid? focusTrack = null) : PageViewModel(main)
{
    private bool _loaded;

    public override string Title => "Edit album";

    public ObservableCollection<EditField> AlbumFields { get; } = new();
    public ObservableCollection<TrackEditRow> Tracks { get; } = new();

    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _busy;

    public EditField? AlbumArtist => AlbumFields.FirstOrDefault(f => f.Field == "artist");

    // Library writes refresh the current page; an editor must not throw away what is being typed.
    public override Task RefreshAsync() => _loaded ? Task.CompletedTask : Load(
        () => (Data: MetadataEditor.LoadAlbum(Host.Db, releaseId), Artists: Host.Queries.ArtistNames()),
        r =>
        {
            _loaded = true;
            if (r.Data is not { } d) { Status = "This album no longer exists."; return; }
            var s = d.States;
            Replace(AlbumFields,
            [
                new EditField("release", releaseId, "title", "Album title", d.Title, s.GetValueOrDefault("title")),
                new EditField("release", releaseId, "artist", "Album artist", d.Artist, s.GetValueOrDefault("artist"), r.Artists),
                new EditField("release", releaseId, "date", "Release date", d.Date, s.GetValueOrDefault("date")),
                new EditField("release", releaseId, "label", "Label", d.Label, s.GetValueOrDefault("label")),
                new EditField("release", releaseId, "edition", "Edition", d.Edition, s.GetValueOrDefault("edition")),
                new EditField("release", releaseId, "country", "Country", d.Country, s.GetValueOrDefault("country")),
            ]);
            Replace(Tracks, d.Tracks.Select(t => new TrackEditRow(t, r.Artists, t.TrackId == focusTrack, d.Artist)));
            OnPropertyChanged(nameof(AlbumArtist));
        });

    private IEnumerable<EditField> AllFields => AlbumFields.Concat(Tracks.SelectMany(t => t.Fields));

    /// <summary>Fills in the cleanup rules' proposals (nothing is saved until Save).</summary>
    [RelayCommand]
    private void SuggestFixes()
    {
        int n = 0;
        foreach (var f in AllFields)
        {
            if (f.Value is not { Length: > 0 } v) continue;
            string? fixedValue = f.Field switch
            {
                "artist" => Cleanup.CleanArtist(v)?.Text,
                "title" => Cleanup.CleanTitle(v, ArtistFor(f))?.Text,
                _ => null,
            };
            if (fixedValue is null || fixedValue == v) continue;
            f.Value = fixedValue;
            n++;
        }
        Status = n == 0 ? "Nothing to tidy up here." : $"{n} field{(n == 1 ? "" : "s")} tidied up. Review, then Save.";
    }

    private string? ArtistFor(EditField f) =>
        Tracks.FirstOrDefault(t => t.Title == f)?.Artist.Value is { Length: > 0 } a ? a : AlbumArtist?.Value;

    /// <summary>Sets every track's artist to the album artist (for albums by one artist).</summary>
    [RelayCommand]
    private void AlbumArtistForAll()
    {
        if (AlbumArtist?.Value is not { Length: > 0 } a) return;
        foreach (var t in Tracks) t.Artist.Value = a;
    }

    [RelayCommand] private void Cancel() => Main.BackCommand.Execute(null);

    [RelayCommand]
    private async Task Save()
    {
        var fields = AllFields.ToList();
        if (fields.Any(f => f.Field == "title" && f.IsChanged && string.IsNullOrWhiteSpace(f.Value)))
        {
            Status = "Titles cannot be empty.";
            return;
        }
        if (fields.Any(f => f.Field is "disc" or "position" && f.IsChanged && !(int.TryParse(f.Value, out int n) && n > 0)))
        {
            Status = "Disc and track numbers must be whole numbers from 1.";
            return;
        }
        var lockedChanges = fields.Where(f => f.IsChanged && f.Locked).Select(Change).ToList();
        var unlockedChanges = fields.Where(f => f.IsChanged && !f.Locked).Select(Change).ToList();
        var lockToggles = fields.Where(f => !f.IsChanged && f.LockChanged).ToList();
        if (lockedChanges.Count + unlockedChanges.Count + lockToggles.Count == 0) { Main.BackCommand.Execute(null); return; }

        Busy = true;
        try
        {
            await Host.Db.Writer.Run((c, t) =>
            {
                var tx = new Tx(c, t);
                // Unlock first: an unlocked edit of a previously locked field would otherwise be refused.
                foreach (var f in fields.Where(f => !f.Locked && f.OriginalLocked))
                    MetadataEditor.SetLocked(tx, f.EntityType, f.Id, f.Field, false);
                MetadataEditor.Apply(tx, lockedChanges, MetadataEditor.UserSource, lockFields: true);
                MetadataEditor.Apply(tx, unlockedChanges, MetadataEditor.UserSource, lockFields: false);
                foreach (var f in lockToggles.Where(f => f.Locked)) MetadataEditor.SetLocked(tx, f.EntityType, f.Id, f.Field, true);
            });
            _ = Host.Player.RefreshMetadataAsync();
            Main.Notify("Changes saved");
            Main.BackCommand.Execute(null);
        }
        catch (Exception ex)
        {
            Status = $"Saving failed: {ex.Message}";
            Log.Error("Editor", "Save failed", ex);
        }
        finally { Busy = false; }
    }

    private static FieldChange Change(EditField f) => new(f.EntityType, f.Id, f.Field, f.Value?.Trim());
}

public sealed partial class SuggestionViewModel(CleanupSuggestion s) : ObservableObject
{
    public CleanupSuggestion Suggestion { get; } = s;
    [ObservableProperty] private bool _selected = true;
}

/// <summary>Library-wide cleanup: lists fixable names, applies the chosen ones (unlocked, so MusicBrainz may refine them).</summary>
public sealed partial class TidyViewModel(MainViewModel main) : PageViewModel(main)
{
    private bool _scanned;

    public override string Title => "Tidy up";
    public ObservableCollection<SuggestionViewModel> Suggestions { get; } = new();
    [ObservableProperty] private string? _status;

    public override Task RefreshAsync() => _scanned ? Task.CompletedTask : Scan();

    [RelayCommand]
    private Task Scan() => Load(() => Cleanup.Find(Host.Db), list =>
    {
        _scanned = true;
        Replace(Suggestions, list.Select(s => new SuggestionViewModel(s)));
        Status = list.Count == 0 ? "Everything looks tidy." : $"{list.Count} suggestion{(list.Count == 1 ? "" : "s")}. Untick any you want to keep as they are.";
    });

    [RelayCommand] private void SelectAll() { foreach (var s in Suggestions) s.Selected = true; }
    [RelayCommand] private void SelectNone() { foreach (var s in Suggestions) s.Selected = false; }

    [RelayCommand]
    private async Task ApplySelected()
    {
        var chosen = Suggestions.Where(s => s.Selected).ToList();
        if (chosen.Count == 0) return;
        var changes = chosen.SelectMany(s => s.Suggestion.Changes).ToList();
        await Host.Db.Writer.Run((c, t) => MetadataEditor.Apply(new Tx(c, t), changes, MetadataEditor.CleanupSource, lockFields: false));
        _ = Host.Player.RefreshMetadataAsync();
        Main.Notify($"Applied {chosen.Count} fix{(chosen.Count == 1 ? "" : "es")}");
        await Scan();
    }
}
