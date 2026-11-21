using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalFM.Vibe.Web;

namespace LocalFM.UI.ViewModels;

/// <summary>A paired TV or browser, as the Sources page lists it.</summary>
public sealed record ShareDeviceRow(string Id, string Name, string Detail);

/// <summary>"TV and other devices" on the Sources page: share the library at home, pair devices with a PIN.</summary>
public sealed partial class ShareViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly LibraryShareService _share;
    private bool _applying;

    public ShareViewModel(MainViewModel main)
    {
        _main = main;
        _share = main.Host.Share;
        Available = main.Host.Capabilities.ShareLibrary;
        _share.Changed += () => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    public bool Available { get; }

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string? _address;
    [ObservableProperty] private string? _otherAddresses;
    [ObservableProperty] private string? _error;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Pairing))] private string? _pin;
    [ObservableProperty] private bool _hasDevices;
    public bool Pairing => Pin is not null;
    public ObservableCollection<ShareDeviceRow> Devices { get; } = new();

    partial void OnEnabledChanged(bool value)
    {
        if (_applying) return;
        _ = _share.SetEnabledAsync(value);
    }

    [RelayCommand] private void Pair() => _share.NewPin();

    [RelayCommand] private void StopPairing() => _share.ClosePin();

    [RelayCommand] private void Remove(ShareDeviceRow row) => _share.Devices.Remove(row.Id);

    [RelayCommand] private async Task CopyAddress()
    {
        if (Address is null || _main.CopyText is not { } copy) return;
        await copy(Address);
        _main.Notify("Address copied.");
    }

    private void Refresh()
    {
        _applying = true;
        Enabled = _share.Running;
        _applying = false;
        Error = _share.Error;
        var urls = _share.Urls;
        Address = urls.FirstOrDefault();
        OtherAddresses = urls.Count > 1 ? "Also reachable at " + string.Join(", ", urls.Skip(1)) : null;
        Pin = _share.Pin;
        var now = DateTimeOffset.UtcNow;
        var rows = _share.Devices.List
            .OrderByDescending(d => d.LastSeen)
            .Select(d => new ShareDeviceRow(d.Id, d.Name, "Last used " + Ago(now - DateTimeOffset.FromUnixTimeMilliseconds(d.LastSeen))))
            .ToList();
        Devices.Clear();
        foreach (var r in rows) Devices.Add(r);
        HasDevices = rows.Count > 0;
    }

    private static string Ago(TimeSpan t) => t.TotalMinutes < 10 ? "just now"
        : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} minutes ago"
        : t.TotalDays < 1 ? $"{(int)t.TotalHours} hours ago"
        : t.TotalDays < 2 ? "yesterday"
        : $"{(int)t.TotalDays} days ago";
}
