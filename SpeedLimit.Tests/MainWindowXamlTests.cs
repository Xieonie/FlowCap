using System.Windows;
using SpeedLimit;

public class MainWindowXamlTests
{
    // Lädt nur das XAML (ohne Konstruktor/Treiber, der Adminrechte bräuchte) – fängt fehlende Ressourcen und kaputte Stile ab,
    // die sonst erst beim Programmstart als Absturz auffallen.
    [Fact]
    public void Xaml_Loads()
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                Application.ResourceAssembly ??= typeof(MainWindow).Assembly;
                var win = (MainWindow)Activator.CreateInstance(typeof(MainWindow), nonPublic: true)!;
                win.Close();
            }
            catch (Exception e)
            {
                error = e;
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        Assert.Null(error);
    }
}
