using System.IO;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using PbmEditor.Core;

namespace PbmEditor.App;

public sealed class MainVm : ObservableObject
{
    PbmDatabase? _db;
    ModEntry? _selectedMod;
    string _status = Loc.T("Choose a mod or save game.");
    string _pending = "";
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public ObservableCollection<ModEntry> Mods { get; } = new();
    public ImageStore Images { get; } = new();
    public ICommand BrowseCommand { get; }
    public ICommand RefreshModsCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DiscardCommand { get; }
    public ICommand ShowChangesCommand { get; }
    public ICommand NewModCommand { get; }
    public IReadOnlyList<LanguageItem> Languages { get; } = Loc.Languages.Select(l => new LanguageItem(l.Code, l.Name)).ToList();
    bool _busy;
    public bool IsNotBusy => !_busy;

    public string LanguageCode
    {
        get => Loc.Language;
        set
        {
            if (value == null || value == Loc.Language) return;
            try { foreach (var p in Parts) p?.Flush(); } // pending field edits are kept in the transaction
            catch (FormatException e)
            {
                Dialogs.Error(e.Message);
                Application.Current.Dispatcher.BeginInvoke(() => Notify(nameof(LanguageCode)));
                return;
            }
            Loc.Language = value;
            Loc.SaveSetting();
            if (_db != null) GameService.InvalidateCache(_db);
            foreach (var p in Parts) p?.Reload(); // rebuild editors with translated labels
            Notify();
        }
    }

    public TeamsVm? Teams { get; private set; }
    public FreeAgentsVm? FreeAgents { get; private set; }
    public LeaguesVm? Leagues { get; private set; }
    public RawVm? Raw { get; private set; }
    public bool IsOpen => _db != null;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Pending { get => _pending; private set => Set(ref _pending, value); }

    public ModEntry? SelectedMod
    {
        get => _selectedMod;
        set
        {
            if (value == null || value == _selectedMod) return;
            if (!OpenDatabase(value.DbPath, value)) { Application.Current.Dispatcher.BeginInvoke(() => Notify(nameof(SelectedMod))); return; }
            Set(ref _selectedMod, value);
        }
    }

    public MainVm()
    {
        BrowseCommand = new RelayCommand(Browse);
        RefreshModsCommand = new RelayCommand(LoadMods);
        SaveCommand = new RelayCommand(Save, () => IsOpen);
        DiscardCommand = new RelayCommand(Discard, () => IsOpen);
        ShowChangesCommand = new RelayCommand(ShowChanges, () => IsOpen);
        NewModCommand = new RelayCommand(NewMod);
        _timer.Tick += (_, _) => UpdatePending();
        _timer.Start();
        LoadMods();
    }

    void LoadMods()
    {
        Mods.Clear();
        foreach (var m in ModLocator.Discover()) Mods.Add(m);
    }

    async void NewMod()
    {
        if (!ConfirmDiscardPending()) return;
        var modsDir = System.IO.Path.Combine(ModLocator.GameDataDir, "Mods");
        var dlg = new NewModDialog(modsDir, ModCreator.FindGameDir()) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true || dlg.GameDir == null) return;

        _busy = true; Notify(nameof(IsNotBusy));
        try
        {
            Directory.CreateDirectory(modsDir);
            var progress = new Progress<string>(m => Status = m);
            var result = await Task.Run(() => ModCreator.Create(dlg.ModName, modsDir, dlg.GameDir, progress));
            LoadMods();
            var entry = Mods.FirstOrDefault(m => string.Equals(m.DbPath, result.DatabasePath, StringComparison.OrdinalIgnoreCase));
            if (entry != null) SelectedMod = entry; else OpenDatabase(result.DatabasePath, null);
            Status = Loc.F("Mod '{0}' created from the default data. Choose it under 'Local mods' when starting a new career.", dlg.ModName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Dialogs.Error(Loc.F("The mod could not be created: {0}", e.Message));
        }
        finally
        {
            _busy = false; Notify(nameof(IsNotBusy));
        }
    }

    void Browse()
    {
        var dlg = new OpenFileDialog { Title = Loc.T("Open PBM database"), Filter = Loc.T("PBM database") + " (*.pbmsave)|*.pbmsave|" + Loc.T("All files") + "|*.*" };
        if (dlg.ShowDialog() != true) return;
        if (OpenDatabase(dlg.FileName, null))
        {
            var entry = new ModEntry(System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(dlg.FileName)) + "\\" + System.IO.Path.GetFileName(dlg.FileName),
                "File", dlg.FileName, Images.Root, false);
            Mods.Add(entry);
            _selectedMod = entry;
            Notify(nameof(SelectedMod));
        }
    }

    public bool ConfirmDiscardPending()
    {
        if (_db == null) return true;
        try { foreach (var p in Parts) p?.Flush(); } // pending field edits count as changes
        catch (FormatException) { return Dialogs.Confirm(Loc.T("There are invalid, unsaved edits. Discard them?")); }
        if (!_db.HasPendingChanges && !Images.HasPending) return true;
        return Dialogs.Confirm(Loc.T("There are unsaved changes. Discard them?"));
    }

    bool OpenDatabase(string path, ModEntry? entry)
    {
        if (!ConfirmDiscardPending()) return false;
        if (Process.GetProcessesByName("PBM2025").Length > 0 &&
            !Dialogs.Confirm(Loc.T("Pro Basketball Manager is running. Editing a mod or save while the game runs can lose changes or corrupt the file.\n\nOpen anyway?")))
            return false;
        if (entry?.ReadOnlyHint == true &&
            !Dialogs.Confirm(Loc.T("This is a Steam workshop mod. Steam overwrites it on updates. Copy it to your local Mods folder for permanent edits.\n\nOpen anyway?")))
            return false;

        _db?.Dispose();
        _db = null;
        try
        {
            _db = PbmDatabase.Open(path);
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or System.IO.IOException or System.IO.InvalidDataException)
        {
            Dialogs.Error(Loc.T("Cannot open database:") + "\n" + e.Message);
            Teams = null; FreeAgents = null; Leagues = null; Raw = null;
            RaiseAll();
            return false;
        }
        Images.Discard();
        Images.Root = ModLocator.ImageRootFor(path, _db) ?? entry?.ImageRoot;
        Teams = new TeamsVm(_db, Images);
        FreeAgents = new FreeAgentsVm(_db, Images);
        Leagues = new LeaguesVm(_db, Images);
        Raw = new RawVm(_db);
        Status = Loc.F("{0}  |  {1}  |  images: {2}", path, _db.VersionInfo(), Images.Root ?? Loc.T("none (no image folder found)"));
        RaiseAll();
        return true;
    }

    void RaiseAll()
    {
        foreach (var p in new[] { nameof(Teams), nameof(FreeAgents), nameof(Leagues), nameof(Raw), nameof(IsOpen) }) Notify(p);
    }

    IEnumerable<IWorkspacePart?> Parts => new IWorkspacePart?[] { Teams, FreeAgents, Leagues, Raw };

    void Save()
    {
        if (_db == null) return;
        try
        {
            foreach (var p in Parts) p?.Flush();
            var n = _db.Log.Count;
            _db.Commit();
            Images.Flush();
            GameService.InvalidateCache(_db);
            foreach (var p in Parts) p?.Reload();
            Status = Loc.F("Saved ({0} statements). Integrity check: {1}. Changes in a mod only apply to NEW careers.", n, _db.IntegrityCheck());
        }
        catch (Exception e) when (e is FormatException or Microsoft.Data.Sqlite.SqliteException or System.IO.IOException)
        {
            Dialogs.Error(Loc.T("Save failed - nothing was committed:") + "\n" + e.Message);
        }
    }

    void Discard()
    {
        if (_db == null || !Dialogs.Confirm(Loc.T("Discard all unsaved changes?"))) return;
        _db.Rollback();
        Images.Discard();
        GameService.InvalidateCache(_db);
        foreach (var p in Parts) p?.Reload();
        Status = Loc.T("Changes discarded.");
    }

    void ShowChanges()
    {
        if (_db == null) return;
        try { foreach (var p in Parts) p?.Flush(); }
        catch (FormatException e) { Dialogs.Error(e.Message); return; }
        MessageBox.Show(_db.Log.Count == 0 ? Loc.T("No pending changes.") : string.Join("\n", _db.Log.TakeLast(60)) +
            (_db.BackupPath != null ? "\n\n" + Loc.F("Backup: {0}", _db.BackupPath) : ""), Loc.T("Pending changes"));
    }

    void UpdatePending()
    {
        Pending = _db == null ? "" : _db.HasPendingChanges || Images.HasPending
            ? Loc.F("Unsaved: {0} statements", _db.Log.Count) + (Images.HasPending ? Loc.T(" + images") : "") : "";
    }

    public void Close() => _db?.Dispose();
}
