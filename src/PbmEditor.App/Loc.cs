using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;
using PbmEditor.Core;

namespace PbmEditor.App;

/// <summary>Translated text that follows language changes at runtime.</summary>
public sealed class LocText : INotifyPropertyChanged
{
    readonly string _key;
    readonly Action _refresh; // strong reference: Loc holds listeners weakly

    public LocText(string key)
    {
        _key = key;
        _refresh = () => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        Loc.Subscribe(_refresh);
    }

    public string Value => Loc.T(_key);
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>XAML: Content="{loc:T 'English text'}" (English text is the translation key).</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public string Key { get; set; }

    public TExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(nameof(LocText.Value)) { Source = new LocText(Key), Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
