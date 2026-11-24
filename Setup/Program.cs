using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NightLightControl.Core;

namespace Setup;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        bool silent = args.Contains("--silent");
        string Option(string name, string fallback) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        string directory = Option("--dir", Installation.RegisteredDirectory ?? Installation.DefaultDirectory);
        bool startup = Option("--startup", Installation.RegisteredDirectory != null && !Settings.StartWithWindows ? "false" : "true") == "true", desktop = Option("--desktop", "false") == "true";
        if (int.TryParse(Option("--wait-pid", ""), out int pid))
        {
            try { using var parent = System.Diagnostics.Process.GetProcessById(pid); if (!parent.WaitForExit(30000)) return 2; } catch (ArgumentException) { }
        }
        int Install(string destination, bool start, bool shortcut)
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip") ?? throw new InvalidDataException("Falta el paquete de instalación.");
                Installation.Install(stream, destination, start, shortcut); return 0;
            }
            catch (Exception ex) { Controller.Log("Instalador: " + ex); if (!silent) MessageBox.Show(ex.Message, "Night Light Control"); return 1; }
        }
        if (silent) return Install(directory, startup, desktop);
        int result = 1;
        var app = new Application();
        var window = new Window { Title = "Instalar Night Light Control", Width = 570, Height = 380, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = Brushes.Black, Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI"), FontSize = 14 };
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock { Text = "Night Light Control " + Updates.CurrentVersionText, FontSize = 25, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Instalación para tu usuario de Windows", Margin = new Thickness(0, 12, 0, 18) });
        var path = new TextBox { Text = directory, Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(8) }; panel.Children.Add(path);
        var startCheck = new CheckBox { Content = "Iniciar con Windows, en la bandeja", IsChecked = startup, Foreground = Brushes.White, Margin = new Thickness(0, 6, 0, 6) }; panel.Children.Add(startCheck);
        var desktopCheck = new CheckBox { Content = "Crear acceso directo en el escritorio", IsChecked = desktop, Foreground = Brushes.White, Margin = new Thickness(0, 6, 0, 16) }; panel.Children.Add(desktopCheck);
        panel.Children.Add(new TextBlock { Text = "Se añadirán un acceso en Inicio y un desinstalador. Tus perfiles se conservan.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray, Margin = new Thickness(0, 0, 0, 18) });
        var button = new Button { Content = "Instalar", Padding = new Thickness(20, 8, 20, 8), HorizontalAlignment = HorizontalAlignment.Right }; panel.Children.Add(button);
        button.Click += (_, _) => { result = Install(path.Text, startCheck.IsChecked == true, desktopCheck.IsChecked == true); if (result == 0) { MessageBox.Show("Instalación completada. Encontrarás Night Light Control en el menú Inicio.", "Night Light Control"); window.Close(); } };
        window.Content = panel; app.Run(window); return result;
    }
}
