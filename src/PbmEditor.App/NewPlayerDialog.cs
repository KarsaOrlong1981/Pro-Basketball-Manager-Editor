using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using PbmEditor.Core;

namespace PbmEditor.App;

/// <summary>Collects the data of a new player. Attributes are derived from overall and position and can be fine-tuned in the editor afterwards.</summary>
public sealed class NewPlayerDialog : Window
{
    readonly PbmDatabase _db;
    readonly TextBox _first = new(), _last = new(), _day = new() { Text = "1" }, _month = new() { Text = "1" }, _year = new();
    readonly TextBox _height = new() { Text = "195" }, _weight = new() { Text = "90" }, _overall = new() { Text = "8" }, _potential = new() { Text = "12" };
    readonly TextBox _number = new(), _salary = new(), _years = new() { Text = "1" };
    readonly ComboBox _gender = Combo(), _country = Combo(), _position = Combo(), _skin = Combo(), _hair = Combo(), _hairColor = Combo(), _beard = Combo(), _team = Combo();
    readonly TextBlock _error = new() { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, MinHeight = 34, MaxWidth = 520 };
    readonly Button _ok = new() { IsDefault = true, MinWidth = 80, Margin = new Thickness(3), IsEnabled = false };
    bool _salaryTouched;

    public NewPlayer? Result { get; private set; }

    static ComboBox Combo()
    {
        var c = new ComboBox { MinWidth = 260, DisplayMemberPath = "Label", SelectedValuePath = "Value", IsEditable = false };
        TextSearch.SetTextPath(c, "Label");
        return c;
    }

    /// <summary>Asks for the data and writes the player into the open transaction. Returns the new player, or null if cancelled or failed.</summary>
    public static (long Id, long TeamId)? Run(PbmDatabase db, long defaultTeamId)
    {
        var dlg = new NewPlayerDialog(db, defaultTeamId) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true || dlg.Result == null) return null;
        try
        {
            return (PlayerCreator.Create(db, dlg.Result), dlg.Result.TeamId);
        }
        catch (Exception e) when (e is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            Dialogs.Error(Loc.F("The player could not be created: {0}", e.Message));
            return null;
        }
    }

    static void Fill(ComboBox box, IReadOnlyList<Choice> items, long select = 0)
    {
        box.ItemsSource = items;
        box.SelectedValue = items.Any(i => i.Value == select) ? select : items.FirstOrDefault()?.Value;
    }

    public NewPlayerDialog(PbmDatabase db, long defaultTeamId)
    {
        _db = db;
        Title = Loc.T("New player");
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var (year, _, _) = GameService.CurrentDate(db);
        _year.Text = (year - 20).ToString(CultureInfo.InvariantCulture);
        Fill(_gender, GameService.GenderChoices(db));
        Fill(_country, GameService.CountryChoices(db), 2);
        Fill(_position, new[]
        {
            new Choice((long)PlayerPosition.PointGuard, Loc.T("Point guard")), new Choice((long)PlayerPosition.ShootingGuard, Loc.T("Shooting guard")),
            new Choice((long)PlayerPosition.Wing, Loc.T("Wing")), new Choice((long)PlayerPosition.PowerForward, Loc.T("Power forward")),
            new Choice((long)PlayerPosition.Center, Loc.T("Center")),
        });
        Fill(_skin, GameService.SkinChoices(db));
        Fill(_hairColor, GameService.HairColorChoices(db));
        Fill(_beard, GameService.BeardChoices(db));
        Fill(_team, GameService.TeamChoicesWithFreeAgent(db), defaultTeamId);
        FillHair();
        SuggestSalary();

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

        Header("Identity");
        Add("First name:", _first);
        Add("Last name:", _last);
        Add("Gender:", _gender);
        Add("Nationality:", _country);
        Add("Birth date (day, month, year):", Line(_day, _month, _year));
        Add("Height (cm) / weight (kg):", Line(_height, _weight));
        Header("Skills");
        Add("Main position:", _position);
        Add("Overall rating (attributes are derived from it):", Line(_overall));
        Add("Potential (hidden, 0-20):", Line(_potential));
        Header("In-game look");
        Add("Skin colour:", _skin);
        Add("Hairstyle:", _hair);
        Add("Hair colour:", _hairColor);
        Add("Beard:", _beard);
        Header("Team and contract");
        Add("Team:", _team);
        Add("Jersey number (empty = next free):", Line(_number));
        Add("Monthly salary:", Line(_salary));
        Add("Contract length (years):", Line(_years));

        _ok.Content = Loc.T("Create");
        _ok.Click += (_, _) => { Result = Read(out _); DialogResult = true; };
        var cancel = new Button { Content = Loc.T("Cancel"), IsCancel = true, MinWidth = 80, Margin = new Thickness(3) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_ok); buttons.Children.Add(cancel);

        var panel = new StackPanel();
        panel.Children.Add(grid);
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("The 26 attributes are generated from the overall rating and position. Fine-tune them, the photo and the contract in the editor after creating."),
            TextWrapping = TextWrapping.Wrap, MaxWidth = 520, Margin = new Thickness(14, 0, 14, 0), Foreground = System.Windows.Media.Brushes.Gray,
        });
        _error.Margin = new Thickness(14, 6, 14, 0);
        panel.Children.Add(_error);
        buttons.Margin = new Thickness(14, 0, 14, 12);
        panel.Children.Add(buttons);
        Content = panel;

        foreach (var tb in new[] { _first, _last, _day, _month, _year, _height, _weight, _potential, _number, _years })
            tb.TextChanged += (_, _) => Validate();
        _salary.TextChanged += (_, _) => { if (_salary.IsKeyboardFocusWithin) _salaryTouched = true; Validate(); };
        _overall.TextChanged += (_, _) => { SuggestSalary(); Validate(); };
        _gender.SelectionChanged += (_, _) => FillHair();
        _team.SelectionChanged += (_, _) => { UpdateTeamControls(); Validate(); };
        foreach (var c in new[] { _country, _position, _skin, _hair, _hairColor, _beard }) c.SelectionChanged += (_, _) => Validate();
        Loaded += (_, _) => { UpdateTeamControls(); _first.Focus(); Validate(); };
    }

    void FillHair()
    {
        var female = _gender.SelectedValue is 1L;
        Fill(_hair, GameService.HairstyleChoicesFor(_db, female, 0), _hair.SelectedValue is long v ? v : 0);
    }

    void SuggestSalary()
    {
        if (_salaryTouched || !double.TryParse(_overall.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var o)) return;
        _salary.Text = PlayerCreator.SuggestSalary(o).ToString(CultureInfo.InvariantCulture);
    }

    void UpdateTeamControls()
    {
        var team = _team.SelectedValue is long t && t != 0;
        _number.IsEnabled = _salary.IsEnabled = _years.IsEnabled = team;
    }

    static bool Int(TextBox t, out int v) => int.TryParse(t.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v);

    NewPlayer? Read(out string? error)
    {
        error = null;
        if (!Int(_day, out var d) || !Int(_month, out var m) || !Int(_year, out var y) || !Int(_height, out var h) || !Int(_weight, out var w) ||
            !Int(_years, out var years))
        { error = Loc.T("Please enter whole numbers for date, height, weight and contract length."); return null; }
        if (!double.TryParse(_overall.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var overall) || overall is < 1 or > 20 ||
            !double.TryParse(_potential.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var potential) || potential is < 0 or > 20)
        { error = Loc.T("Overall and potential must be numbers between 1 and 20 (potential 0-20)."); return null; }
        if (!long.TryParse(_salary.Text.Trim().Length == 0 ? "0" : _salary.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var salary))
        { error = Loc.T("The salary must be a whole number."); return null; }
        long? number = null;
        if (_number.Text.Trim().Length > 0)
        {
            if (!int.TryParse(_number.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) { error = Loc.T("The jersey number must be a whole number."); return null; }
            number = n;
        }
        long Sel(ComboBox c) => c.SelectedValue is long v ? v : 0;
        return new NewPlayer(_first.Text, _last.Text, d, m, y, Sel(_gender) == 1, Sel(_country), h, w, (PlayerPosition)Sel(_position), overall, potential,
            Sel(_skin), Sel(_hair), Sel(_hairColor), Sel(_beard), Sel(_team), number, salary, years);
    }

    void Validate()
    {
        var p = Read(out var msg);
        if (p != null) msg = PlayerCreator.Validate(_db, p);
        _error.Text = msg ?? "";
        _ok.IsEnabled = msg == null;
    }
}
