using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using NightLightControl.Core;
using NightLightControl.UI;

internal static class Program
{
    // Parse the actual interface as a passive visual tree: no Window, native
    // controller, tray icon, input injection, or changes to Windows settings.
    [STAThread]
    private static int Main(string[] args)
    {
        string repository = Path.GetFullPath(args[0]); string output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
        var events = new HashSet<string> { "Click", "Checked", "KeyDown", "LostKeyboardFocus", "SelectionChanged", "ValueChanged" };
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var source = XDocument.Load(Path.Combine(repository, "UI", "MainWindow.xaml"));
        var rootXml = source.Root!;
        foreach (var element in rootXml.DescendantsAndSelf())
        {
            if (element.Name.NamespaceName == "clr-namespace:NightLightControl.UI") element.Name = XName.Get(element.Name.LocalName, "clr-namespace:NightLightControl.UI;assembly=NightLightControl");
            foreach (var attribute in element.Attributes().ToList())
            {
                if (events.Contains(attribute.Name.LocalName) || attribute.Name.LocalName == "Class") attribute.Remove();
                else if (attribute.IsNamespaceDeclaration && attribute.Value == "clr-namespace:NightLightControl.UI") attribute.Value += ";assembly=NightLightControl";
            }
        }
        rootXml.Elements().Where(e => e.Name.LocalName == "WindowChrome.WindowChrome").Remove();
        foreach (var attribute in rootXml.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.LocalName is not ("Width" or "Height" or "Background")).ToList()) attribute.Remove();
        rootXml.Name = presentation + "Border";
        var resources = rootXml.Element(presentation + "Window.Resources")!; resources.Name = presentation + "Border.Resources";
        var merged = resources.Descendants(presentation + "ResourceDictionary.MergedDictionaries").Single();
        merged.Elements().Remove();
        merged.Add(new XElement(presentation + "ResourceDictionary", new XAttribute("Source", "/NightLightControl;component/UI/Theme.xaml")));
        var root = (Border)XamlReader.Parse(source.ToString());
        TextElement.SetFontFamily(root, new System.Windows.Media.FontFamily("Segoe UI")); TextElement.SetFontSize(root, 13); TextElement.SetForeground(root, System.Windows.Media.Brushes.White);
        var curve = (CurveEditor)root.FindName("Editor"); curve.Points = Profile.Defaults(); curve.CurrentMinute = 1290; curve.ResetSelection();
        var preview = (CurveEditor)root.FindName("ProfilePreview"); preview.Points = curve.Points; preview.CurrentMinute = 1290;
        ((TextBox)root.FindName("PointTime")).Text = "00:00"; ((TextBox)root.FindName("PointStrength")).Text = "65";
        ((TextBlock)root.FindName("StatusText")).Text = "Control desactivado"; ((TextBlock)root.FindName("BackendText")).Text = "Motor de color nativo de Windows";
        ((TextBlock)root.FindName("DisplayText")).Text = "Pantallas de Windows";
        ((Slider)root.FindName("ManualSlider")).Value = 40; ((Slider)root.FindName("ManualSlider")).IsEnabled = false;
        ((Slider)root.FindName("TransitionSlider")).Value = 10;
        ((RadioButton)root.FindName("AutoMode")).IsChecked = true;
        ((CheckBox)root.FindName("UpdateStartupCheck")).IsChecked = true;
        var profiles = (ComboBox)root.FindName("ProfileBox"); profiles.ItemsSource = new[] { new Profile() }; profiles.SelectedIndex = 0;
        foreach (string page in new[] { "Curve", "Profiles", "Preferences", "Updates" })
        {
            foreach (string name in new[] { "Curve", "Profiles", "Preferences", "Updates" })
            {
                ((FrameworkElement)root.FindName(name + "View")).Visibility = page == name ? Visibility.Visible : Visibility.Collapsed;
                ((RadioButton)root.FindName(name == "Curve" ? "CurveNav" : name + "Nav")).IsChecked = page == name;
            }
            ((TextBlock)root.FindName("PageTitle")).Text = page == "Updates" ? "Actualizaciones" : page == "Curve" ? "Curva diaria" : page == "Profiles" ? "Perfiles" : "Preferencias";
            ((TextBlock)root.FindName("PageSubtitle")).Text = page == "Updates" ? "Mantén Night Light Control al día." : page == "Curve" ? "Elige la calidez de cada momento del día." : page == "Profiles" ? "Guarda una curva para cada rutina." : "Adapta el control a tu forma de usar el equipo.";
            Render(root, 1280, 850, Path.Combine(output, page.ToLowerInvariant() + ".png"));
        }
        ((FrameworkElement)root.FindName("CurveView")).Visibility = Visibility.Visible;
        ((FrameworkElement)root.FindName("PreferencesView")).Visibility = Visibility.Collapsed;
        ((FrameworkElement)root.FindName("UpdatesView")).Visibility = Visibility.Collapsed;
        ((RadioButton)root.FindName("CurveNav")).IsChecked = true;
        ((TextBlock)root.FindName("PageTitle")).Text = "Curva diaria";
        ((TextBlock)root.FindName("PageSubtitle")).Text = "Elige la calidez de cada momento del día.";
        Render(root, 1080, 740, Path.Combine(output, "curve-compact.png"));
        Console.WriteLine("Rendered five passive previews without creating or controlling a window."); return 0;
    }
    private static void Render(FrameworkElement root, int width, int height, string file)
    {
        root.Width = width; root.Height = height; root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(file); encoder.Save(stream);
    }
}
