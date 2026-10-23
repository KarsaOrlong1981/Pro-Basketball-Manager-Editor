using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace PbmEditor.App.Views;

public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b && !b;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b && !b;
}

public partial class FieldsPanel : UserControl
{
    public static readonly DependencyProperty FieldsProperty =
        DependencyProperty.Register(nameof(Fields), typeof(IEnumerable), typeof(FieldsPanel),
            new PropertyMetadata(null, (d, e) => ((FieldsPanel)d).Rebuild()));

    public IEnumerable? Fields
    {
        get => (IEnumerable?)GetValue(FieldsProperty);
        set => SetValue(FieldsProperty, value);
    }

    public FieldsPanel()
    {
        InitializeComponent();
    }

    void Rebuild()
    {
        if (Fields == null) { List.ItemsSource = null; return; }
        var view = new ListCollectionView(Fields.Cast<object>().ToList());
        view.GroupDescriptions.Add(new PropertyGroupDescription("Group"));
        List.ItemsSource = view;
    }
}
