using System.ComponentModel;
using System.Windows;

namespace PbmEditor.App;

public partial class MainWindow : Window
{
    readonly MainVm _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        // dev aid: PBM_EDITOR_SHOT=<png> renders the window to a file and exits (used to check translations)
        var shot = Environment.GetEnvironmentVariable("PBM_EDITOR_SHOT");
        if (!string.IsNullOrEmpty(shot)) ContentRendered += (_, _) => Dispatcher.BeginInvoke(() => Capture(shot), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    void Capture(string path)
    {
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(this);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using (var fs = System.IO.File.Create(path)) enc.Save(fs);
        Application.Current.Shutdown();
    }

    void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_vm.ConfirmDiscardPending()) { e.Cancel = true; return; }
        _vm.Close();
    }
}
