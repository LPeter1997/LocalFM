namespace LocalFM.UI.ViewModels;

/// <summary>The phone's "More" tab: the pages that do not fit the bottom bar, and the library's counts.</summary>
public sealed class MoreViewModel(MainViewModel main, IReadOnlyList<NavItem> items) : PageViewModel(main)
{
    public override string Title => "More";

    public IReadOnlyList<NavItem> Items { get; } = items;

    public MainViewModel MainVm => Main;
}
