using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PbmEditor.Core;

namespace PbmEditor.App;

public sealed record LanguageItem(string Code, string Name);

/// <summary>Asks for the new mod's name (and the game folder if it was not found automatically).</summary>
public sealed class NewModDialog : Window
{
    readonly TextBox _name = new() { MinWidth = 320 };
    readonly TextBox _gameDir = new() { MinWidth = 320, IsReadOnly = true };
    readonly TextBlock _error = new() { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, MinHeight = 34 };
    readonly Button _ok = new() { IsDefault = true, MinWidth = 80, IsEnabled = false };
    readonly string _modsDir;

    public string ModName => _name.Text.Trim();
    public string? GameDir => _gameDir.Text.Length == 0 ? null : _gameDir.Text;

    public NewModDialog(string modsDir, string? gameDir)
    {
        _modsDir = modsDir;
        Title = Loc.T("New mod from default data");
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        _gameDir.Text = gameDir ?? "";

        var browse = new Button { Content = Loc.T("Browse..."), Margin = new Thickness(3) };
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = Loc.T("Select the game folder (Pro Basketball Manager 2025)") };
            if (dlg.ShowDialog() == true) { _gameDir.Text = dlg.FolderName; Validate(); }
        };
        _ok.Content = Loc.T("Create");
        _ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = Loc.T("Cancel"), IsCancel = true, MinWidth = 80, Margin = new Thickness(3) };
        _ok.Margin = new Thickness(3);

        var gameRow = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        gameRow.Children.Add(browse);
        gameRow.Children.Add(_gameDir);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = Loc.T("Mod name:") });
        panel.Children.Add(_name);
        panel.Children.Add(new TextBlock { Text = Loc.T("Game folder (source of the default data):"), Margin = new Thickness(0, 10, 0, 0) });
        panel.Children.Add(gameRow);
        panel.Children.Add(new TextBlock
        {
            Text = Loc.F("The mod is created in {0}. About 600 MB are copied, this takes a moment.", modsDir),
            TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Margin = new Thickness(0, 10, 0, 0), Foreground = System.Windows.Media.Brushes.Gray,
        });
        panel.Children.Add(_error);
        panel.Children.Add(buttons);
        Content = panel;

        _name.TextChanged += (_, _) => Validate();
        Loaded += (_, _) => { _name.Focus(); Validate(); };
    }

    void Validate()
    {
        string? msg = null;
        if (GameDir == null || !File.Exists(Path.Combine(GameDir, "PBM2025_Data", "Datas", "BM_database.pbmsave")))
            msg = Loc.T("Default game data not found. Select the game folder.");
        else if (_name.Text.Length > 0) msg = ModCreator.ValidateName(_name.Text, _modsDir);
        _error.Text = msg ?? "";
        _ok.IsEnabled = msg == null && _name.Text.Trim().Length > 0;
    }
}
