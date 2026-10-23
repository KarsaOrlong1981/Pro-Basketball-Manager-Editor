using System.Windows;

namespace PbmEditor.App;

public partial class App : Application
{
    public App()
    {
        Core.Loc.LoadSetting();
        DispatcherUnhandledException += (_, e) =>
        {
            Dialogs.Error(Core.Loc.T("Unexpected error:") + "\n" + e.Exception.Message);
            e.Handled = true;
        };
    }
}
