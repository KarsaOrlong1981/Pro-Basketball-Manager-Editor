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
    bool _isBusy;
    bool _rawUnlocked;
    string _cheatCode = "";
    string _cheatResult = "";
    /// <summary>Codes live outside the app: one "feature=CODE" per line, '#' starts a comment. No file, no codes.</summary>
    static string CheatFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PbmEditor", "cheatcodes.txt");

    static string NormalizeCode(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    static bool CodeUnlocks(string feature, string entered)
    {
        if (!File.Exists(CheatFile)) return false;
        return File.ReadLines(CheatFile).Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != '#')
            .Select(l => l.Split('=', 2)).Any(p => p.Length == 2 && p[0].Trim().Equals(feature, StringComparison.OrdinalIgnoreCase) && NormalizeCode(p[1]) == entered);
    }
    string _busyText = "";
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

    public TeamsVm? Teams { get; private set; }
    public FreeAgentsVm? FreeAgents { get; private set; }
    public LeaguesVm? Leagues { get; private set; }
    public RawVm? Raw { get; private set; }
    public bool IsOpen => _db != null;
    /// <summary>Raw table browser (shows SQL and table names): hidden until the cheat code is entered (per session).</summary>
    public bool ShowRaw => _rawUnlocked;
    /// <summary>Shown bottom right, e.g. "v0.0.1-alpha" (build metadata after "+" is cut).</summary>
    public string AppVersion { get; } = "v" + (System.Reflection.Assembly.GetExecutingAssembly()
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "?").Split('+')[0];
    public ICommand ApplyCheatCommand { get; }
    public string CheatCode { get => _cheatCode; set => Set(ref _cheatCode, value); }
    public string CheatResult { get => _cheatResult; private set => Set(ref _cheatResult, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Pending { get => _pending; private set => Set(ref _pending, value); }

    /// <summary>True while a long operation runs: the window shows an overlay with BusyText and blocks input.</summary>
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) Notify(nameof(IsNotBusy)); } }
    public bool IsNotBusy => !_isBusy;
    public string BusyText { get => _busyText; private set => Set(ref _busyText, value); }

    public string LanguageCode
    {
        get => Loc.Language;
        set
        {
            if (value == null || value == Loc.Language) return;
            _ = ChangeLanguageAsync(value);
        }
    }

    public ModEntry? SelectedMod
    {
        get => _selectedMod;
        set
        {
            if (value == null || value == _selectedMod) return;
            if (IsBusy || !ConfirmDiscardPending() || !ConfirmOpen(value))
            {
                Application.Current.Dispatcher.BeginInvoke(() => Notify(nameof(SelectedMod)));
                return;
            }
            var previous = _selectedMod;
            _selectedMod = value;
            Notify();
            _ = OpenSelectedAsync(value, previous);
        }
    }

    public MainVm()
    {
        BrowseCommand = new RelayCommand(Browse, () => !IsBusy);
        RefreshModsCommand = new RelayCommand(LoadMods, () => !IsBusy);
        SaveCommand = new RelayCommand(Save, () => IsOpen && !IsBusy);
        DiscardCommand = new RelayCommand(Discard, () => IsOpen && !IsBusy);
        ShowChangesCommand = new RelayCommand(ShowChanges, () => IsOpen && !IsBusy);
        NewModCommand = new RelayCommand(NewMod, () => !IsBusy);
        ApplyCheatCommand = new RelayCommand(ApplyCheat);
        _timer.Tick += (_, _) => UpdatePending();
        _timer.Start();
        LoadMods();
    }

    void LoadMods()
    {
        Mods.Clear();
        foreach (var m in ModLocator.Discover()) Mods.Add(m);
    }

    // ---- busy handling ----

    /// <summary>Shows the busy overlay, lets WPF paint it, then runs the work. Progress text can be changed via SetBusyText.</summary>
    async Task RunBusy(string text, Func<Task> work)
    {
        if (IsBusy) return;
        BusyText = text;
        IsBusy = true;
        try
        {
            await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background); // overlay is painted first
            await work();
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    Task YieldToUi() => Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background).Task;

    // ---- language ----

    async Task ChangeLanguageAsync(string code)
    {
        try { foreach (var p in Parts) p?.Flush(); } // pending field edits are kept in the transaction
        catch (FormatException e)
        {
            Dialogs.Error(e.Message);
            Notify(nameof(LanguageCode));
            return;
        }
        Loc.Language = code;
        Loc.SaveSetting();
        Notify();
        if (_db == null) return;
        await RunBusy(Loc.T("Switching language..."), async () =>
        {
            GameService.InvalidateCache(_db);
            await YieldToUi();
            foreach (var p in Parts) p?.Reload(); // rebuild editors with translated labels
        });
    }

    // ---- new mod / open ----

    async void NewMod()
    {
        if (!ConfirmDiscardPending()) return;
        var modsDir = Path.Combine(ModLocator.GameDataDir, "Mods");
        var dlg = new NewModDialog(modsDir, ModCreator.FindGameDir()) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true || dlg.GameDir == null) return;
        // read the text boxes here: WPF controls cannot be touched from the background task below
        var (modName, gameDir) = (dlg.ModName, dlg.GameDir);

        NewModResult? result = null;
        await RunBusy(Loc.T("Creating mod..."), async () =>
        {
            try
            {
                Directory.CreateDirectory(modsDir);
                var progress = new Progress<string>(m => BusyText = m);
                result = await Task.Run(() => ModCreator.Create(modName, modsDir, gameDir, progress));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Dialogs.Error(Loc.F("The mod could not be created: {0}", e.Message));
            }
        });
        if (result == null) return;

        LoadMods();
        var entry = Mods.FirstOrDefault(m => string.Equals(m.DbPath, result.DatabasePath, StringComparison.OrdinalIgnoreCase));
        if (entry != null) SelectedMod = entry;
        else if (await OpenAsync(result.DatabasePath, null)) { }
        Status = Loc.F("Mod '{0}' created from the default data. Choose it under 'Local mods' when starting a new career.", modName);
    }

    async void Browse()
    {
        var dlg = new OpenFileDialog { Title = Loc.T("Open PBM database"), Filter = Loc.T("PBM database") + " (*.pbmsave)|*.pbmsave|" + Loc.T("All files") + "|*.*" };
        if (dlg.ShowDialog() != true) return;
        if (!ConfirmDiscardPending() || !ConfirmOpen(null)) return;
        if (!await OpenAsync(dlg.FileName, null)) return;
        var entry = new ModEntry(Path.GetFileName(Path.GetDirectoryName(dlg.FileName)) + "\\" + Path.GetFileName(dlg.FileName),
            "File", dlg.FileName, Images.Root, false);
        Mods.Add(entry);
        _selectedMod = entry;
        Notify(nameof(SelectedMod));
    }

    public bool ConfirmDiscardPending()
    {
        if (_db == null) return true;
        try { foreach (var p in Parts) p?.Flush(); } // pending field edits count as changes
        catch (FormatException) { return Dialogs.Confirm(Loc.T("There are invalid, unsaved edits. Discard them?")); }
        if (!_db.HasPendingChanges && !Images.HasPending) return true;
        return Dialogs.Confirm(Loc.T("There are unsaved changes. Discard them?"));
    }

    static bool ConfirmOpen(ModEntry? entry)
    {
        if (Process.GetProcessesByName("PBM2025").Length > 0 &&
            !Dialogs.Confirm(Loc.T("Pro Basketball Manager is running. Editing a mod or save while the game runs can lose changes or corrupt the file.\n\nOpen anyway?")))
            return false;
        if (entry?.ReadOnlyHint == true &&
            !Dialogs.Confirm(Loc.T("This is a Steam workshop mod. Steam overwrites it on updates. Copy it to your local Mods folder for permanent edits.\n\nOpen anyway?")))
            return false;
        return true;
    }

    async Task OpenSelectedAsync(ModEntry entry, ModEntry? previous)
    {
        if (await OpenAsync(entry.DbPath, entry)) return;
        _selectedMod = previous; // opening failed: show the previous selection again
        Notify(nameof(SelectedMod));
    }

    async Task<bool> OpenAsync(string path, ModEntry? entry)
    {
        var ok = false;
        await RunBusy(Loc.T("Opening database..."), async () =>
        {
            _db?.Dispose();
            _db = null;
            try
            {
                var (db, version, root) = await Task.Run(() =>
                {
                    var d = PbmDatabase.Open(path);
                    return (d, d.VersionInfo(), ModLocator.ImageRootFor(path, d));
                });
                _db = db;
                Images.Discard();
                Images.Root = root ?? entry?.ImageRoot;
                BusyText = Loc.T("Loading teams, players and leagues...");
                await YieldToUi();
                Teams = new TeamsVm(_db, Images) { FreeAgentCreated = () => FreeAgents?.Reload() };
                FreeAgents = new FreeAgentsVm(_db, Images) { TeamPlayerCreated = ReloadTeams };
                Leagues = new LeaguesVm(_db, Images) { LeagueCreated = ReloadTeams };
                Raw = _rawUnlocked ? new RawVm(_db) : null;
                Status = Loc.F("{0}  |  {1}  |  images: {2}", path, version, Images.Root ?? Loc.T("none (no image folder found)"));
                ok = true;
            }
            catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidDataException)
            {
                Dialogs.Error(Loc.T("Cannot open database:") + "\n" + ErrorText.For(e));
                Teams = null; FreeAgents = null; Leagues = null; Raw = null;
            }
            RaiseAll();
        });
        return ok;
    }

    /// <summary>Refreshes the team view after a player was created elsewhere (pending edits are written first).</summary>
    void ReloadTeams()
    {
        try { Teams?.Flush(); }
        catch (FormatException e) { Dialogs.Error(e.Message); return; }
        Teams?.Reload();
    }

    void ApplyCheat()
    {
        var entered = NormalizeCode(CheatCode);
        CheatCode = "";
        bool ok;
        try { ok = entered.Length > 0 && CodeUnlocks("rawtables", entered); }
        catch (IOException e) { CheatResult = Loc.F("Cheat codes could not be read: {0}", e.Message); return; }
        if (!ok) { CheatResult = Loc.T("Unknown cheat code."); return; }
        if (_rawUnlocked) { CheatResult = Loc.T("Already active: Raw tables."); return; }
        _rawUnlocked = true;
        if (_db != null) Raw = new RawVm(_db);
        Notify(nameof(ShowRaw)); Notify(nameof(Raw));
        CheatResult = Loc.T("Cheat activated: the Raw tables tab is now available (until you close the editor).");
    }

    void RaiseAll()
    {
        foreach (var p in new[] { nameof(Teams), nameof(FreeAgents), nameof(Leagues), nameof(Raw), nameof(IsOpen) }) Notify(p);
    }

    IEnumerable<IWorkspacePart?> Parts => new IWorkspacePart?[] { Teams, FreeAgents, Leagues, Raw };

    // ---- save / discard ----

    async void Save()
    {
        if (_db == null) return;
        var db = _db;
        await RunBusy(Loc.T("Saving..."), async () =>
        {
            try
            {
                foreach (var p in Parts) p?.Flush(); // editors -> transaction (first write also takes the backup)
                var n = db.Changes.Count;
                BusyText = Loc.T("Writing changes...");
                await Task.Run(() => { db.Commit(); Images.Flush(); });
                BusyText = Loc.T("Checking database integrity...");
                var check = await Task.Run(db.IntegrityCheck);
                BusyText = Loc.T("Refreshing...");
                await YieldToUi();
                GameService.InvalidateCache(db);
                foreach (var p in Parts) p?.Reload();
                Status = Loc.F("Saved ({0} changes). Integrity check: {1}. Changes in a mod only apply to NEW careers.", n, check == "ok" ? "ok" : Loc.T("problems found"));
            }
            catch (Exception e) when (e is FormatException or Microsoft.Data.Sqlite.SqliteException or IOException)
            {
                Dialogs.Error(Loc.T("Save failed - nothing was committed:") + "\n" + ErrorText.For(e));
            }
        });
    }

    async void Discard()
    {
        if (_db == null || !Dialogs.Confirm(Loc.T("Discard all unsaved changes?"))) return;
        var db = _db;
        await RunBusy(Loc.T("Discarding changes..."), async () =>
        {
            db.Rollback();
            Images.Discard();
            GameService.InvalidateCache(db);
            await YieldToUi();
            foreach (var p in Parts) p?.Reload();
            Status = Loc.T("Changes discarded.");
        });
    }

    void ShowChanges()
    {
        if (_db == null) return;
        try { foreach (var p in Parts) p?.Flush(); }
        catch (FormatException e) { Dialogs.Error(e.Message); return; }
        MessageBox.Show(_db.Changes.Count == 0 ? Loc.T("No pending changes.") : string.Join("\n", _db.Changes.Take(60)) +
            (_db.Changes.Count > 60 ? "\n" + Loc.F("... and {0} more", _db.Changes.Count - 60) : "") +
            (_db.BackupPath != null ? "\n\n" + Loc.F("Backup: {0}", _db.BackupPath) : ""), Loc.T("Pending changes"));
    }

    void UpdatePending()
    {
        Pending = _db == null ? "" : _db.HasPendingChanges || Images.HasPending
            ? Loc.F("Unsaved: {0} changes", _db.Changes.Count) + (Images.HasPending ? Loc.T(" + images") : "") : "";
    }

    public void Close() => _db?.Dispose();
}
