using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PbmEditor.Core;

namespace PbmEditor.App;

public sealed class RelayCommand : ICommand
{
    readonly Action _run;
    readonly Func<bool>? _can;
    public RelayCommand(Action run, Func<bool>? can = null) { _run = run; _can = can; }
    public bool CanExecute(object? parameter) => _can?.Invoke() ?? true;
    public void Execute(object? parameter) => _run();
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}

/// <summary>A part of the workspace that holds editors: Flush writes them into the DB transaction.</summary>
public interface IWorkspacePart
{
    void Flush();
    void Reload();
}

public static class Dialogs
{
    public static void Error(string msg) => MessageBox.Show(msg, "PBM Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
    public static void Info(string msg) => MessageBox.Show(msg, "PBM Editor", MessageBoxButton.OK, MessageBoxImage.Information);
    public static bool Confirm(string msg) =>
        MessageBox.Show(msg, "PBM Editor", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}

public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = (value as string ?? "").Trim();
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(s)); }
        catch (FormatException) { return Brushes.Transparent; }
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public static class ImageTools
{
    public static BitmapSource Decode(byte[] bytes)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = new MemoryStream(bytes);
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    static BitmapSource Bgra(BitmapSource s) =>
        s.Format == PixelFormats.Bgra32 ? s : new FormatConvertedBitmap(s, PixelFormats.Bgra32, null, 0);

    /// <summary>Crop to 140:160 (top-aligned: head and shoulders) and scale to 140x160, as the game expects.</summary>
    public static BitmapSource Face(BitmapSource src)
    {
        src = Bgra(src);
        const double ratio = 140.0 / 160.0;
        int w = src.PixelWidth, h = src.PixelHeight, cw, ch, x;
        if (w / (double)h > ratio) { ch = h; cw = (int)(h * ratio); x = (w - cw) / 2; }
        else { cw = w; ch = (int)(w / ratio); x = 0; }
        var crop = new CroppedBitmap(src, new Int32Rect(x, 0, cw, Math.Min(ch, h)));
        var scaled = new TransformedBitmap(crop, new ScaleTransform(140.0 / cw, 160.0 / crop.PixelHeight));
        scaled.Freeze();
        return scaled;
    }

    public static BitmapSource FitMax(BitmapSource src, int maxW, int maxH)
    {
        src = Bgra(src);
        var k = Math.Min(1.0, Math.Min(maxW / (double)src.PixelWidth, maxH / (double)src.PixelHeight));
        if (k >= 1.0) return src;
        var t = new TransformedBitmap(src, new ScaleTransform(k, k));
        t.Freeze();
        return t;
    }

    public static BitmapSource SideBySide(BitmapSource left, BitmapSource right)
    {
        left = Bgra(left); right = Bgra(right);
        var h = Math.Max(left.PixelHeight, right.PixelHeight);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(left, new Rect(0, 0, left.PixelWidth, left.PixelHeight));
            dc.DrawImage(right, new Rect(left.PixelWidth, 0, right.PixelWidth, right.PixelHeight));
        }
        var rtb = new RenderTargetBitmap(left.PixelWidth + right.PixelWidth, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    public static byte[] Png(BitmapSource s) => Encode(new PngBitmapEncoder(), s);
    public static byte[] Jpeg(BitmapSource s) => Encode(new JpegBitmapEncoder { QualityLevel = 90 }, s);

    static byte[] Encode(BitmapEncoder enc, BitmapSource s)
    {
        enc.Frames.Add(BitmapFrame.Create(s));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}

/// <summary>Images are staged in memory and written on Save, so Discard works for them too.</summary>
public sealed class ImageStore
{
    readonly Dictionary<string, byte[]> _staged = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _deleted = new(StringComparer.OrdinalIgnoreCase);
    public string? Root { get; set; }
    public bool HasPending => _staged.Count > 0 || _deleted.Count > 0;

    string? Full(string folder, string file) => Root == null ? null : Path.Combine(Root, folder, file);

    public byte[]? Read(string folder, string file)
    {
        var p = Full(folder, file);
        if (p == null || _deleted.Contains(p)) return null;
        if (_staged.TryGetValue(p, out var b)) return b;
        return File.Exists(p) ? File.ReadAllBytes(p) : null;
    }

    public void Stage(string folder, string file, byte[] bytes)
    {
        var p = Full(folder, file) ?? throw new InvalidOperationException(Loc.T("This database has no image folder."));
        _staged[p] = bytes;
        _deleted.Remove(p);
    }

    /// <summary>Marks a file for deletion on Save (the game then falls back to its default).</summary>
    public void Remove(string folder, string file)
    {
        var p = Full(folder, file) ?? throw new InvalidOperationException(Loc.T("This database has no image folder."));
        _staged.Remove(p);
        _deleted.Add(p);
    }

    public void Flush()
    {
        foreach (var (path, bytes) in _staged)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, true);
        }
        _staged.Clear();
        foreach (var path in _deleted) File.Delete(path);
        _deleted.Clear();
    }

    public void Discard() { _staged.Clear(); _deleted.Clear(); }
}

public enum ImageKind { Face, Fit, Jpeg }

public sealed class ImageSlot : ObservableObject
{
    readonly ImageStore _store;
    readonly string _folder, _pattern;
    readonly ImageKind _kind;
    readonly int _maxW, _maxH;
    long? _id;
    BitmapSource? _image;
    string _info = "";
    readonly Func<BitmapSource?>? _defaultKit;
    readonly string _labelKey;
    readonly Action _langChanged; // strong reference: Loc holds listeners weakly

    public string Label => Loc.T(_labelKey);
    public double PreviewHeight { get; }
    public BitmapSource? Image { get => _image; private set => Set(ref _image, value); }
    public string Info { get => _info; private set => Set(ref _info, value); }
    public ICommand Browse { get; }
    public ICommand GenerateCommand { get; }
    public ICommand CombineCommand { get; }
    public ICommand RemoveCommand { get; }
    /// <summary>Jersey slots get extra tools: generate from colours, combine front+back, remove.</summary>
    public bool IsJersey => _defaultKit != null;

    /// <param name="defaultKit">Renders the game default kit from the team colours (jersey slots only).</param>
    public ImageSlot(ImageStore store, string label, string folder, string pattern, ImageKind kind, int maxW = 800, int maxH = 800,
        double previewHeight = 110, Func<BitmapSource?>? defaultKit = null)
    {
        _store = store; _labelKey = label; _folder = folder; _pattern = pattern; _kind = kind; _maxW = maxW; _maxH = maxH; PreviewHeight = previewHeight;
        _defaultKit = defaultKit;
        _langChanged = () => { Notify(nameof(Label)); Refresh(); };
        Loc.Subscribe(_langChanged);
        Browse = new RelayCommand(Choose, () => _id != null && _store.Root != null);
        GenerateCommand = new RelayCommand(Generate, () => _id != null && _store.Root != null && _defaultKit != null);
        CombineCommand = new RelayCommand(Combine, () => _id != null && _store.Root != null);
        RemoveCommand = new RelayCommand(RemoveImage, () => _id != null && _store.Root != null);
    }

    string FileName => string.Format(_pattern, _id);

    public void SetId(long? id)
    {
        _id = id;
        Refresh();
    }

    public void Refresh()
    {
        Image = null;
        if (_id == null) { Info = ""; return; }
        if (_store.Root == null) { Info = Loc.T("no image folder"); return; }
        try
        {
            var b = _store.Read(_folder, FileName);
            Image = b == null ? null : ImageTools.Decode(b);
            if (b != null) Info = $"{FileName} {Image!.PixelWidth}x{Image.PixelHeight}";
            else if (_defaultKit != null) { Image = _defaultKit(); Info = Loc.F("{0} missing: the game draws its default kit (front + back) from the team colours", FileName); }
            else Info = Loc.F("{0} (missing)", FileName);
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or IOException)
        {
            Info = Loc.F("{0}: cannot read image ({1})", FileName, e.Message);
        }
    }

    void Generate()
    {
        var kit = _defaultKit?.Invoke();
        if (kit == null) { Dialogs.Error(Loc.T("Game kit templates not found (PBM2025_Data\\images\\decor\\jerseys).")); return; }
        _store.Stage(_folder, FileName, ImageTools.Png(kit));
        Refresh();
        Info += Loc.T(" (unsaved)");
    }

    /// <summary>Front photo + back photo side by side in one image, as the game shows one picture per kit.</summary>
    void Combine()
    {
        var dlg = new OpenFileDialog { Multiselect = true, Title = Loc.T("Select exactly two images: FRONT and BACK"), Filter = Loc.T("Images") + "|*.png;*.jpg;*.jpeg;*.webp;*.bmp" };
        if (dlg.ShowDialog() != true) return;
        if (dlg.FileNames.Length != 2) { Dialogs.Error(Loc.T("Select exactly two images: front and back.")); return; }
        try
        {
            var imgs = dlg.FileNames.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Select(f => ImageTools.FitMax(ImageTools.Decode(File.ReadAllBytes(f)), 4000, 700)).ToList();
            var combined = ImageTools.SideBySide(imgs[0], imgs[1]);
            _store.Stage(_folder, FileName, ImageTools.Png(ImageTools.FitMax(combined, _maxW * 2, _maxH)));
            Refresh();
            Info += Loc.T(" (unsaved)");
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or IOException or InvalidOperationException)
        {
            Dialogs.Error(Loc.F("Image could not be loaded: {0}", e.Message));
        }
    }

    void RemoveImage()
    {
        if (!Dialogs.Confirm(Loc.F("Delete {0} on Save? The game then uses its default for this slot.", FileName))) return;
        _store.Remove(_folder, FileName);
        Refresh();
        Info += Loc.T(" (will be deleted)");
    }

    void Choose()
    {
        var dlg = new OpenFileDialog { Filter = Loc.T("Images") + "|*.png;*.jpg;*.jpeg;*.webp;*.bmp|" + Loc.T("All files") + "|*.*" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var src = ImageTools.Decode(File.ReadAllBytes(dlg.FileName));
            var bytes = _kind switch
            {
                ImageKind.Face => ImageTools.Png(ImageTools.Face(src)),
                ImageKind.Jpeg => ImageTools.Jpeg(ImageTools.FitMax(src, _maxW, _maxH)),
                _ => ImageTools.Png(ImageTools.FitMax(src, _maxW, _maxH)),
            };
            _store.Stage(_folder, FileName, bytes);
            Refresh();
            Info += Loc.T(" (unsaved)");
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or IOException or InvalidOperationException)
        {
            Dialogs.Error(Loc.F("Image could not be loaded: {0}", e.Message));
        }
    }
}


/// <summary>
/// Approximation of the game default kit: jersey_0.png (front + shorts) tinted with the main colour,
/// jersey_1.png (back with number) tinted with the secondary colour, merged into one picture.
/// The game shows this picture when no custom jersey file exists.
/// </summary>
public static class JerseyKit
{
    public static string? TemplateDir
    {
        get
        {
            var game = ModCreator.FindGameDir();
            if (game == null) return null;
            var d = Path.Combine(game, "PBM2025_Data", "images", "decor", "jerseys");
            return File.Exists(Path.Combine(d, "jersey_0.png")) ? d : null;
        }
    }

    public static (byte A, byte R, byte G, byte B) ParseArgb(string? text)
    {
        if (!long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v))
            return (255, 255, 255, 255);
        var u = (uint)v;
        return ((byte)(u >> 24 == 0 ? 255 : u >> 24), (byte)(u >> 16), (byte)(u >> 8), (byte)u);
    }

    public static BitmapSource? Render(string? mainArgb, string? secondaryArgb)
    {
        var dir = TemplateDir;
        if (dir == null) return null;
        try
        {
            var t0 = Load(Path.Combine(dir, "jersey_0.png"));
            var t1 = Load(Path.Combine(dir, "jersey_1.png"));
            var w = Math.Max(t0.PixelWidth, t1.PixelWidth);
            var h = Math.Max(t0.PixelHeight, t1.PixelHeight);
            var dst = new byte[w * h * 4];
            Blend(dst, w, t0, ParseArgb(mainArgb));
            Blend(dst, w, t1, ParseArgb(secondaryArgb));
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, dst, w * 4);
            bmp.Freeze();
            return bmp;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or FileFormatException)
        {
            return null;
        }
    }

    static BitmapSource Load(string path) => new FormatConvertedBitmap(ImageTools.Decode(File.ReadAllBytes(path)), PixelFormats.Bgra32, null, 0);

    // multiply the white template by the colour, then alpha-composite over what is already there
    static void Blend(byte[] dst, int dstW, BitmapSource layer, (byte A, byte R, byte G, byte B) c)
    {
        var w = layer.PixelWidth; var h = layer.PixelHeight;
        var src = new byte[w * h * 4];
        layer.CopyPixels(src, w * 4, 0);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var si = (y * w + x) * 4; var di = (y * dstW + x) * 4;
                double sa = src[si + 3] / 255.0 * c.A / 255.0;
                if (sa <= 0) continue;
                var sb = src[si] * c.B / 255.0; var sg = src[si + 1] * c.G / 255.0; var sr = src[si + 2] * c.R / 255.0;
                double da = dst[di + 3] / 255.0;
                var oa = sa + da * (1 - sa);
                dst[di] = (byte)((sb * sa + dst[di] * da * (1 - sa)) / oa);
                dst[di + 1] = (byte)((sg * sa + dst[di + 1] * da * (1 - sa)) / oa);
                dst[di + 2] = (byte)((sr * sa + dst[di + 2] * da * (1 - sa)) / oa);
                dst[di + 3] = (byte)(oa * 255);
            }
    }
}
