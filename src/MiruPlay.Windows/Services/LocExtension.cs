using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace MiruPlay.Windows.Services;

/// <summary>
/// XAML string lookup: Text="{services:Loc nav.library}".
/// With a bound argument: Name="{services:Loc Key=wpf.play.playFor, Arg={Binding DisplayTitle}}".
/// Resolves at load through <see cref="AppStrings"/>.</summary>
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key) => Key = key;

    public string? Key { get; set; }

    public object? Arg { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var key = Key ?? string.Empty;
        if (Arg is BindingBase binding)
        {
            var multi = new MultiBinding { Converter = new LocFormatConverter(key), Mode = BindingMode.OneWay };
            multi.Bindings.Add(binding);
            return multi.ProvideValue(serviceProvider);
        }
        if (Arg is null) return AppStrings.Get(key);
        return string.Format(CultureInfo.CurrentCulture, AppStrings.Get(key), Arg);
    }

    private sealed class LocFormatConverter(string key) : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            string.Format(culture, AppStrings.Get(key), values.Length > 0 ? values[0] ?? string.Empty : string.Empty);

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
