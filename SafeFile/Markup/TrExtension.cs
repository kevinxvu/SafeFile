using System;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using SafeFile.Services;

namespace SafeFile.Markup;

public sealed class TrExtension(string key) : MarkupExtension
{
    public string Key { get; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]")
        {
            Source = LocalizationService.Instance,
            Mode = BindingMode.OneWay
        };
}
