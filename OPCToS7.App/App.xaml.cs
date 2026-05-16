using System.Windows;
using OPCToS7.Storage.Context;

namespace OPCToS7.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DatabaseInitializer.Initialize();

    
    }
}