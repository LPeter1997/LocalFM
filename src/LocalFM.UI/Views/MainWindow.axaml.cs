using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LocalFM.UI.ViewModels;

namespace LocalFM.UI.Views;

/// <summary>The desktop window: our own chrome around <see cref="MainView"/>.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        View.CaptionButtonSlot.Content = new CaptionButtons { Margin = new Thickness(0, 0, 9, 0) };
        WindowChrome.MakeBorderless(this);
        WindowChrome.TitleBar(this, View.TitleRow);
        WindowChrome.ResizeGrips(this, RootPanel);
        WindowChrome.Outline(this, Frame);
        View.MiniPlayerRequested += () => MiniPlayerRequested?.Invoke();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>The person asked to switch to the miniplayer.</summary>
    public event Action? MiniPlayerRequested;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (Program.SnapshotPath is { } path)
        {
            // Snapshots may go below the minimum size: phone-sized shots check the compact layout on the desktop.
            if (int.TryParse(Environment.GetEnvironmentVariable("LOCALFM_SNAPSHOT_HEIGHT"), out int height)) { MinHeight = Math.Min(MinHeight, height); Height = height; }
            if (int.TryParse(Environment.GetEnvironmentVariable("LOCALFM_SNAPSHOT_WIDTH"), out int width)) { MinWidth = Math.Min(MinWidth, width); Width = width; }
            _ = SnapshotAndExit(path);
        }
        else if (Vm is { } vm)
        {
            vm.Updater.ShowWhatsNewIfUpdated();
            vm.Updater.CheckMenuEntry();
            _ = vm.Updater.CheckOnStartAsync();
        }
    }

    // ------------------------------------------------------------------ snapshot mode

    private async Task SnapshotAndExit(string path)
    {
        try
        {
            if (Vm is { } vm)
                await SnapshotScript.RunAsync(vm, View, Environment.GetEnvironmentVariable, () => MiniPlayerRequested?.Invoke());
            // The miniplayer replaces this window when it is open.
            Window shown = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?
                .Windows.FirstOrDefault(w => w is MiniPlayerWindow && w.IsVisible) ?? this;
            var root = (Visual?)shown.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.VisualLayerManager>().FirstOrDefault() ?? (Visual)shown.Content!;
            var visual = ElementComposition.GetElementVisual(root)!;
            using Bitmap bmp = await visual.Compositor.CreateCompositionVisualSnapshot(visual, shown.RenderScaling);
            bmp.Save(path, PngBitmapEncoderOptions.Default);
            Console.WriteLine($"snapshot {path}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"snapshot failed: {ex}");
        }
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var w in (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows.ToList() ?? [])
                if (w != this) w.Close();
            Close();
        });
    }
}
