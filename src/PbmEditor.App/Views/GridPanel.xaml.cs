using System.Data;
using System.Windows;
using System.Windows.Controls;
using PbmEditor.Core;

namespace PbmEditor.App.Views;

/// <summary>DataContext: a TableGrid (or null).</summary>
public partial class GridPanel : UserControl
{
    public GridPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bind();
    }

    TableGrid? Model => DataContext as TableGrid;

    void Bind()
    {
        Grid.ItemsSource = Model?.Data.DefaultView;
        Grid.IsReadOnly = Model?.ReadOnly ?? true;
        Info.Text = Model == null ? "(table not available in this database)" :
            Model.ReadOnly ? "Read-only (no primary key)" : Model.Truncated ? $"Limited to {TableGrid.MaxRows} rows" : "";
    }

    void Add_Click(object sender, RoutedEventArgs e)
    {
        if (Model == null || Model.ReadOnly) return;
        Model.NewRow();
        Grid.ScrollIntoView(Grid.Items[^1]);
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Model == null || Model.ReadOnly) return;
        var rows = Grid.SelectedItems.OfType<DataRowView>().ToList();
        if (rows.Count == 0 || !Dialogs.Confirm($"Delete {rows.Count} row(s)? (applied on Save)")) return;
        foreach (var r in rows) r.Row.Delete();
    }
}
