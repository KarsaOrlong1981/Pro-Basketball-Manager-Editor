using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PbmEditor.Core;

namespace PbmEditor.App;

/// <summary>Collects the data of a new league. The league, its clubs, rosters and staff are generated; names, logos and players can be edited afterwards.</summary>
public sealed class NewLeagueDialog : Window
{
    readonly PbmDatabase _db;
    readonly TextBox _name = new(), _abbr = new(), _division = new() { Text = "4" }, _reputation = new() { Text = "20" }, _teams = new() { Text = "14" };
    readonly TextBox _promoted = new() { Text = "4" }, _relegated = new() { Text = "2" }, _level = new(), _roster = new() { Text = "13" };
    readonly TextBox _names = new() { AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 90, MinWidth = 260 };
    readonly ComboBox _country = Combo(), _template = Combo(), _promoteTo = Combo();
    readonly TextBlock _error = new() { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, MinHeight = 34, MaxWidth = 560 };
    readonly Button _ok = new() { IsDefault = true, MinWidth = 80, Margin = new Thickness(3), IsEnabled = false };
    readonly int _seed = Environment.TickCount & 0x7fffffff;
    bool _levelTouched;

    public NewLeague? Result { get; private set; }

    static ComboBox Combo()
    {
        var c = new ComboBox { MinWidth = 260, DisplayMemberPath = "Label", SelectedValuePath = "Value", IsEditable = false };
        TextSearch.SetTextPath(c, "Label");
        return c;
    }

    static void Fill(ComboBox box, IReadOnlyList<Choice> items, long select)
    {
        box.ItemsSource = items;
        box.SelectedValue = items.Any(i => i.Value == select) ? select : items.FirstOrDefault()?.Value;
    }

    /// <summary>Asks for the data and writes the league into the open transaction. Returns null if cancelled or failed.</summary>
    public static LeagueResult? Run(PbmDatabase db, LeagueItem? selected)
    {
        var dlg = new NewLeagueDialog(db, selected) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true || dlg.Result == null) return null;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var result = LeagueCreator.Create(db, dlg.Result);
            GameService.InvalidateCache(db);
            return result;
        }
        catch (Exception e) when (e is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            Dialogs.Error(Loc.F("The league could not be created: {0}", e.Message));
            return null;
        }
        finally { Mouse.OverrideCursor = null; }
    }

    public NewLeagueDialog(PbmDatabase db, LeagueItem? selected)
    {
        _db = db;
        Title = Loc.T("New league");
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var leagues = GameService.LeagueChoices(db);
        Fill(_country, GameService.CountryChoices(db), selected?.Country ?? 0);
        Fill(_template, leagues, selected?.Id ?? 15);
        Fill(_promoteTo, GameService.LeagueChoicesWithNone(db), selected?.Id ?? 0);
        SuggestLevel();

        var grid = new Grid { Margin = new Thickness(14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var row = 0;
        void Add(string label, UIElement control)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var t = new TextBlock { Text = Loc.T(label), Margin = new Thickness(0, 5, 12, 5), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(t, row); Grid.SetRow(control, row); Grid.SetColumn(control, 1);
            if (control is FrameworkElement fe) fe.Margin = new Thickness(0, 2, 0, 2);
            grid.Children.Add(t); grid.Children.Add(control);
            row++;
        }
        void Header(string text)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var t = new TextBlock { Text = Loc.T(text), FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 2) };
            Grid.SetRow(t, row); Grid.SetColumnSpan(t, 2);
            grid.Children.Add(t);
            row++;
        }
        StackPanel Line(params UIElement[] items)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var i in items) { if (i is TextBox tb) tb.MinWidth = 50; if (i is FrameworkElement f) f.Margin = new Thickness(0, 0, 6, 0); p.Children.Add(i); }
            return p;
        }

        Header("League");
        Add("Name:", _name);
        Add("Abbreviation:", _abbr);
        Add("Country:", _country);
        Add("Division level (1 = top):", Line(_division));
        Add("Reputation (0-100):", Line(_reputation));
        Add("Copy structure from (single group):", _template);
        Header("Teams");
        Add("Number of teams (even):", Line(_teams));
        Add("Players per team (10-13):", Line(_roster));
        Add("Team strength (average of the ten best):", Line(_level));
        Add("Team names (one per line, empty = generated):", _names);
        Header("Promotion and relegation");
        Add("Promoted teams move up to:", _promoteTo);
        Add("Teams promoted / relegated:", Line(_promoted, _relegated));

        _ok.Content = Loc.T("Create");
        _ok.Click += (_, _) => { Result = Read(out _); DialogResult = true; };
        var cancel = new Button { Content = Loc.T("Cancel"), IsCancel = true, MinWidth = 80, Margin = new Thickness(3) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_ok); buttons.Children.Add(cancel);

        var panel = new StackPanel();
        panel.Children.Add(grid);
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("Creates the league with schedule rules, clubs, players and staff. Edit names, logos and players afterwards. Only new careers use the result."),
            TextWrapping = TextWrapping.Wrap, MaxWidth = 560, Margin = new Thickness(14, 0, 14, 0), Foreground = System.Windows.Media.Brushes.Gray,
        });
        _error.Margin = new Thickness(14, 6, 14, 0);
        panel.Children.Add(_error);
        buttons.Margin = new Thickness(14, 0, 14, 12);
        panel.Children.Add(buttons);
        Content = panel;

        foreach (var tb in new[] { _name, _abbr, _division, _reputation, _teams, _promoted, _relegated, _roster, _names })
            tb.TextChanged += (_, _) => Validate();
        _level.TextChanged += (_, _) => { if (_level.IsKeyboardFocusWithin) _levelTouched = true; Validate(); };
        _promoteTo.SelectionChanged += (_, _) => { SuggestLevel(); Validate(); };
        foreach (var c in new[] { _country, _template }) c.SelectionChanged += (_, _) => Validate();
        Loaded += (_, _) => { _name.Focus(); Validate(); };
    }

    /// <summary>A lower division is weaker than the league above it: default to its level minus one.</summary>
    void SuggestLevel()
    {
        if (_levelTouched) return;
        var upper = _promoteTo.SelectedValue is long u && u != 0 ? u : _template.SelectedValue is long t ? t : 0;
        _level.Text = (upper == 0 ? 8.0 : LeagueCreator.SuggestTeamLevel(_db, upper)).ToString("0.0", CultureInfo.InvariantCulture);
    }

    static bool Int(TextBox t, out int v) => int.TryParse(t.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v);

    NewLeague? Read(out string? error)
    {
        error = null;
        if (!Int(_division, out var division) || !Int(_reputation, out var reputation) || !Int(_teams, out var teams) ||
            !Int(_promoted, out var promoted) || !Int(_relegated, out var relegated) || !Int(_roster, out var roster))
        { error = Loc.T("Please enter whole numbers for division, reputation, teams, promotion, relegation and players."); return null; }
        if (!double.TryParse(_level.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var level) || level is < 4 or > 16)
        { error = Loc.T("The team strength must be a number between 4 and 16."); return null; }
        long Sel(ComboBox c) => c.SelectedValue is long v ? v : 0;
        var names = _names.Text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return new NewLeague(_name.Text, _abbr.Text, Sel(_country), Sel(_template), division, reputation, teams, Sel(_promoteTo), promoted, relegated,
            level, _seed, names, roster);
    }

    void Validate()
    {
        var l = Read(out var msg);
        if (l != null) msg = LeagueCreator.Validate(_db, l);
        _error.Text = msg ?? "";
        _ok.IsEnabled = msg == null;
    }
}
