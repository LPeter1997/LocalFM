using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalFM.App;

namespace LocalFM.UI.ViewModels;

public sealed partial class EqBandViewModel : ObservableObject
{
    private readonly Action<EqBandViewModel> _changed;

    public EqBandViewModel(int index, float gain, Action<EqBandViewModel> changed)
    {
        Index = index;
        _gain = gain;
        _changed = changed;
        float f = SoundSettings.Frequencies[index];
        Label = f >= 1000 ? $"{f / 1000:0.#}k" : $"{f:0}";
    }

    public int Index { get; }
    public string Label { get; }
    public string GainText => $"{Gain:+0.#;-0.#;0}";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(GainText))] private double _gain;

    partial void OnGainChanged(double value) => _changed(this);

    /// <summary>Sets the value without reporting it as a user change (presets).</summary>
    public void Load(double gain)
    {
#pragma warning disable MVVMTK0034 // deliberately bypass the change hook
        _gain = gain;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(Gain));
        OnPropertyChanged(nameof(GainText));
    }
}

/// <summary>Equalizer, crossfade and loudness. EQ changes are heard immediately (they run on the audio thread).</summary>
public sealed partial class SoundViewModel : PageViewModel
{
    private const double CurveWidth = 1000, CurveHeight = 240;
    private readonly SoundSettings _s;
    private readonly DispatcherTimer _save;
    private bool _loading;

    public SoundViewModel(MainViewModel main) : base(main)
    {
        _s = Host.Sound;
        _save = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) =>
        {
            _save!.Stop();
            _ = _s.SaveAsync(Host.History);
        });
        Bands = new(SoundSettings.Frequencies.Select((_, i) => new EqBandViewModel(i, _s.Gains[i], OnBand)));
        Presets = new(_s.AllPresets);
        _eqEnabled = _s.EqEnabled;
        _preamp = _s.PreampDb;
        _preventClipping = _s.PreventClipping;
        _crossfade = _s.CrossfadeSeconds;
        _gaplessAlbums = _s.GaplessAlbums;
        _replayGainIndex = (int)_s.ReplayGain;
        _selectedPreset = Presets.FirstOrDefault(p => p.Name == _s.Preset);
        UpdateCurve();
    }

    public override string Title => "Sound";
    public ObservableCollection<EqBandViewModel> Bands { get; }
    public ObservableCollection<EqPreset> Presets { get; }
    public IReadOnlyList<string> ReplayGainModes { get; } = ["Off", "Per track", "Per album"];

    [ObservableProperty] private bool _eqEnabled;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PreampText))] private double _preamp;
    [ObservableProperty] private bool _preventClipping;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanDeletePreset))] private EqPreset? _selectedPreset;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CrossfadeText))] private double _crossfade;
    [ObservableProperty] private bool _gaplessAlbums;
    [ObservableProperty] private int _replayGainIndex;
    [ObservableProperty] private string? _newPresetName;
    [ObservableProperty] private IList<Point> _curve = [];
    [ObservableProperty] private double _zeroLine = CurveHeight / 2;

    public string PreampText => $"{Preamp:+0.#;-0.#;0} dB";
    public string CrossfadeText => Crossfade < 0.1 ? "Off (gapless)" : $"{Crossfade:0.#} s";
    public bool CanDeletePreset => SelectedPreset is { BuiltIn: false };

    private void OnBand(EqBandViewModel b)
    {
        _s.Gains[b.Index] = (float)b.Gain;
        if (!_loading && SelectedPreset is { } p && p.Gains[b.Index] != (float)b.Gain)
        {
            _loading = true;
            SelectedPreset = null; // now a custom curve
            _s.Preset = "";
            _loading = false;
        }
        if (!EqEnabled && Math.Abs(b.Gain) > 0.01) EqEnabled = true; // touching a slider means "I want EQ"
        EqChanged();
    }

    partial void OnEqEnabledChanged(bool value) { _s.EqEnabled = value; EqChanged(); }
    partial void OnPreampChanged(double value) { _s.PreampDb = (float)value; EqChanged(); }
    partial void OnPreventClippingChanged(bool value) { _s.PreventClipping = value; EqChanged(); }

    partial void OnSelectedPresetChanged(EqPreset? value)
    {
        if (_loading || value is null) return;
        _loading = true;
        _s.Preset = value.Name;
        for (int i = 0; i < Bands.Count; i++)
        {
            _s.Gains[i] = value.Gains[i];
            Bands[i].Load(value.Gains[i]);
        }
        _loading = false;
        if (!EqEnabled) EqEnabled = true; else EqChanged();
    }

    partial void OnCrossfadeChanged(double value) { _s.CrossfadeSeconds = Math.Round(value * 2) / 2; OtherChanged(); }
    partial void OnGaplessAlbumsChanged(bool value) { _s.GaplessAlbums = value; OtherChanged(); }
    partial void OnReplayGainIndexChanged(int value) { _s.ReplayGain = (ReplayGainMode)Math.Clamp(value, 0, 2); OtherChanged(); }

    [RelayCommand]
    private void ResetEq() => SelectedPreset = Presets[0];

    [RelayCommand]
    private void SavePreset()
    {
        string name = (NewPresetName ?? "").Trim();
        if (name.Length == 0) return;
        _s.CustomPresets.RemoveAll(p => p.Name == name);
        var preset = new EqPreset(name, [.. _s.Gains]);
        _s.CustomPresets.Add(preset);
        _loading = true;
        Replace(Presets, _s.AllPresets);
        SelectedPreset = Presets.First(p => p.Name == name);
        _s.Preset = name;
        _loading = false;
        NewPresetName = null;
        Save();
    }

    [RelayCommand]
    private void DeletePreset()
    {
        if (SelectedPreset is not { BuiltIn: false } p) return;
        _s.CustomPresets.Remove(p);
        _loading = true;
        Replace(Presets, _s.AllPresets);
        SelectedPreset = null;
        _s.Preset = "";
        _loading = false;
        Save();
    }

    private void EqChanged()
    {
        Host.Player.ApplyEqualizer(_s);
        UpdateCurve();
        Save();
    }

    private void OtherChanged()
    {
        Host.Player.ApplySound(_s);
        Save();
    }

    private void Save()
    {
        _save.Stop();
        _save.Start();
    }

    /// <summary>Response from 20 Hz to 20 kHz on a log axis, ±15 dB.</summary>
    private void UpdateCurve()
    {
        const int n = 160;
        const double range = 15;
        var pts = new List<Point>(n);
        for (int i = 0; i < n; i++)
        {
            double f = 20 * Math.Pow(1000, i / (double)(n - 1));
            double db = Math.Clamp(_s.ResponseDb(f), -range, range);
            pts.Add(new Point(CurveWidth * i / (n - 1), CurveHeight / 2 - db / range * (CurveHeight / 2)));
        }
        Curve = pts;
    }
}

public static class SoundConverters
{
    public static readonly Avalonia.Data.Converters.FuncValueConverter<bool, double> EnabledOpacity = new(on => on ? 1.0 : 0.4);
}
