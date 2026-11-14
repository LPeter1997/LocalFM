using Avalonia.Data.Converters;

namespace LocalFM.UI;

public static class Converters
{
    public static string Time(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    public static readonly IValueConverter Duration =
        new FuncValueConverter<long?, string>(ms => ms is { } v ? Time(TimeSpan.FromMilliseconds(v)) : "");

    public static readonly IValueConverter Year =
        new FuncValueConverter<string?, string>(d => d is { Length: >= 4 } ? d[..4] : "");

    public static readonly IValueConverter NotNull =
        new FuncValueConverter<object?, bool>(o => o is not null);
}
