using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using NightLightControl.Core;
using Forms = System.Windows.Forms;

namespace NightLightControl.UI;

public partial class MainWindow : Window
{
    private readonly Settings settings;
    private readonly Controller controller;
    private readonly CrashGuard crashGuard = new();
    private readonly PendingSave pendingSave;
    private readonly EventWaitHandle activation;
    private readonly EventWaitHandle shutdown = new(false, EventResetMode.AutoReset, Maintenance.ExitEventName);
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Forms.NotifyIcon tray;
    private readonly Icon trayIcon;
    private bool ready, exiting;
    private bool dirty => pendingSave.IsDirty;
    private int lastGraphMinute = -1;
    private AppRelease? latestRelease;
    private bool checkingUpdate, installingUpdate;
    private CancellationTokenSource? updateCancellation;
    private bool exitAfterDownload, updateHandoff;

    public MainWindow(Settings settings, EventWaitHandle activation)
    {
        this.settings = settings; this.activation = activation; controller = new(settings);
        pendingSave = new(() => settings.Save(), () => DateTimeOffset.UtcNow);
        controller.EnsureProtection = () => !settings.RecoverAfterCrash || crashGuard.SetActive(true);
        controller.OwnershipChanged = active => { if (!active) crashGuard.SetActive(false); };
        InitializeComponent();
        AutoMode.IsChecked = settings.Automatic; ManualMode.IsChecked = !settings.Automatic;
        ManualSlider.Value = settings.ManualStrength; TransitionSlider.Value = settings.TransitionSeconds;
        StartupCheck.IsChecked = Settings.StartWithWindows; FullscreenCheck.IsChecked = settings.PauseFullscreen;
        ExclusionsBox.Text = string.Join(", ", settings.ExcludedApps);
        UpdateStartupCheck.IsChecked = settings.CheckUpdatesAtStartup;
        RecoveryCheck.IsChecked = settings.RecoverAfterCrash;
        InstalledVersionText.Text = "Versión instalada · " + Updates.CurrentVersionText;
        FooterVersionText.Text = "●  Night Light Control v" + Updates.CurrentVersionText;
        BindProfiles(); Editor.Changed += MarkDirty; Editor.SelectionChanged += UpdatePointFields; Editor.ResetSelection();
        trayIcon = MakeIcon();
        Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(trayIcon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir Night Light Control", null, (_, _) => Dispatcher.Invoke(ShowWindow));
        menu.Items.Add("Activar / desactivar control", null, (_, _) => Dispatcher.Invoke(() => ToggleEnabled(this, new RoutedEventArgs())));
        menu.Items.Add("Pausar 1 hora", null, (_, _) => Dispatcher.Invoke(() => controller.Pause(TimeSpan.FromHours(1))));
        menu.Items.Add("Reanudar", null, (_, _) => Dispatcher.Invoke(() => controller.Resume()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Salir y restaurar", null, (_, _) => Dispatcher.Invoke(Exit));
        tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "Night Light Control", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);
        Closing += OnClosing;
        SystemEvents.DisplaySettingsChanged += DisplayChanged; SystemEvents.PowerModeChanged += PowerChanged; SystemEvents.SessionSwitch += SessionChanged;
        SystemEvents.SessionEnding += SessionEnding;
        ready = true; timer.Tick += Tick; timer.Start(); Tick(null, EventArgs.Empty);
        if (settings.CheckUpdatesAtStartup) Dispatcher.BeginInvoke(async () => await CheckForUpdatesAsync());
    }
    private void Tick(object? sender, EventArgs e)
    {
        if (exiting) return;
        if (shutdown.WaitOne(0)) { Exit(); return; }
        if (activation.WaitOne(0)) ShowWindow();
        controller.Tick();
        StatusText.Text = controller.Status;
        BackendText.Text = controller.Backend; DisplayText.Text = controller.Displays;
        bool applying = controller.IsApplying;
        IntensityText.Text = applying ? controller.CurrentStrength.ToString("0", CultureInfo.CurrentCulture) : "—";
        KelvinText.Text = applying ? $"{Curve.Kelvin(controller.CurrentStrength):N0} K · objetivo {controller.TargetStrength:0} %" : "Sin aplicar calidez propia";
        EnableButton.Content = settings.Enabled ? "Desactivar control" : "Activar control";
        PauseButton.Content = controller.PauseUntil.HasValue ? "Reanudar control" : "Pausar 1 hora";
        ManualSlider.IsEnabled = !settings.Automatic;
        ManualLabel.Text = $"{settings.ManualStrength:0} % manual";
        TransitionLabel.Text = $"Transición de 0 a 100 %: {settings.TransitionSeconds} s";
        int minute = (int)DateTime.Now.TimeOfDay.TotalMinutes;
        if (lastGraphMinute != minute) { lastGraphMinute = minute; Editor.CurrentMinute = minute; ProfilePreview.CurrentMinute = minute; Editor.InvalidateVisual(); ProfilePreview.InvalidateVisual(); }
        tray.Text = ("Night Light Control · " + (applying ? $"{controller.CurrentStrength:0} %" : "en pausa"));
        if (pendingSave.IsDue) Save();
        timer.Interval = TimeSpan.FromMilliseconds(dirty && pendingSave.LastError == null ? 250 : controller.RecommendedTickMilliseconds);
    }
    private void MarkDirty()
    {
        if (!ready) return;
        pendingSave.MarkChanged(); SaveText.Text = "Guardando…";
        timer.Interval = TimeSpan.FromMilliseconds(250);
    }
    private bool Save(bool notify = false)
    {
        bool saved = pendingSave.TrySave(); SaveText.Text = saved ? "Cambios guardados" : "Error al guardar · cambios pendientes";
        if (!saved) { Controller.Log(pendingSave.LastError!.ToString()); if (notify) MessageBox.Show(this, pendingSave.LastError.Message, "Guardar configuración"); }
        return saved;
    }
    private void SaveCurveClicked(object sender, RoutedEventArgs e) { if (ApplyPointFields() && Save(true)) SaveText.Text = "Curva guardada"; }
    public void StopAfterUnexpectedError() { settings.Enabled = false; controller.TryRestore(); MarkDirty(); Save(); }
    private void ShowWindow() { Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); }
    private void BindProfiles()
    {
        bool before = ready; ready = false;
        ProfileBox.ItemsSource = null; ProfileBox.ItemsSource = settings.Profiles; ProfileBox.SelectedIndex = settings.ActiveProfile;
        Editor.Points = settings.Current.Points; Editor.ResetSelection(); RefreshProfileLabels(); ready = before;
    }
    private void ProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || ProfileBox.SelectedIndex < 0) return;
        settings.ActiveProfile = ProfileBox.SelectedIndex; Editor.Points = settings.Current.Points; Editor.ResetSelection(); RefreshProfileLabels(); MarkDirty();
    }
    private void RefreshProfileLabels()
    {
        CurveProfileText.Text = "Perfil · " + settings.Current.Name;
        FooterProfileText.Text = settings.Current.Name;
        ProfilePreview.Points = settings.Current.Points; ProfilePreview.InvalidateVisual();
    }
    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (CurveView == null || ProfilesView == null || PreferencesView == null || UpdatesView == null) return;
        string page = (sender as RadioButton)?.Tag as string ?? "Curve";
        CurveView.Visibility = page == "Curve" ? Visibility.Visible : Visibility.Collapsed;
        ProfilesView.Visibility = page == "Profiles" ? Visibility.Visible : Visibility.Collapsed;
        PreferencesView.Visibility = page == "Preferences" ? Visibility.Visible : Visibility.Collapsed;
        UpdatesView.Visibility = page == "Updates" ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = page == "Updates" ? "Actualizaciones" : page == "Profiles" ? "Perfiles" : page == "Preferences" ? "Preferencias" : "Curva diaria";
        PageSubtitle.Text = page == "Updates" ? "Mantén Night Light Control al día." : page == "Profiles" ? "Guarda una curva para cada rutina." : page == "Preferences" ? "Adapta el control a tu forma de usar el equipo." : "Elige la calidez de cada momento del día.";
        if (page == "Profiles") ProfilePreview.InvalidateVisual();
    }
    private void EditCurrentCurve(object sender, RoutedEventArgs e) => CurveNav.IsChecked = true;
    private void MinimizeClicked(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClicked(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClicked(object sender, RoutedEventArgs e) => Close();
    private async void CheckUpdatesClicked(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync();
    private async Task CheckForUpdatesAsync()
    {
        if (checkingUpdate || installingUpdate) return;
        checkingUpdate = true; CheckUpdatesButton.IsEnabled = false; InstallUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "Buscando versiones en GitHub…";
        try
        {
            latestRelease = await Updates.CheckAsync();
            bool newer = latestRelease != null && latestRelease.Version > Updates.CurrentVersion;
            UpdateStatusText.Text = latestRelease == null ? "Todavía no hay una versión descargable publicada." : newer ? "Hay una actualización disponible." : "Tienes la versión más reciente.";
            LatestVersionText.Text = latestRelease == null ? "" : "Última versión publicada · " + latestRelease.Tag;
            InstallUpdateButton.IsEnabled = newer; UpdatesNavText.Text = newer ? "Actualizaciones •" : "Actualizaciones";
        }
        catch (Exception ex) { UpdateStatusText.Text = "No se pudo consultar GitHub. Comprueba la conexión y vuelve a intentarlo."; Controller.Log("Buscar actualizaciones: " + ex); }
        finally { checkingUpdate = false; CheckUpdatesButton.IsEnabled = true; }
    }
    private async void InstallUpdateClicked(object sender, RoutedEventArgs e)
    {
        if (latestRelease == null || latestRelease.Version <= Updates.CurrentVersion || installingUpdate) return;
        installingUpdate = true; CheckUpdatesButton.IsEnabled = false; InstallUpdateButton.IsEnabled = false;
        updateCancellation = new CancellationTokenSource();
        UpdateProgress.Value = 0; UpdateProgress.Visibility = Visibility.Visible;
        try
        {
            UpdateStatusText.Text = "Descargando la actualización…";
            var cancellation = updateCancellation.Token;
            var progress = new Progress<double>(value => { if (!exiting && installingUpdate && !cancellation.IsCancellationRequested) { UpdateProgress.Value = value; UpdateStatusText.Text = $"Descargando la actualización… {value:0} %"; } });
            var download = await Updates.DownloadAsync(latestRelease, progress, cancellation);
            UpdateStatusText.Text = "Descarga comprobada. Preparando instalación…";
            if (!controller.TryRestore()) throw new InvalidOperationException("No se pudo restaurar el color de Windows. La instalación se canceló.");
            if (dirty && !Save()) throw new InvalidOperationException("Guarda tu configuración antes de actualizar.");
            Updates.StartInstall(download); updateHandoff = true; Exit();
        }
        catch (OperationCanceledException) { UpdateStatusText.Text = updateCancellation.IsCancellationRequested ? "Descarga cancelada." : "La descarga agotó el tiempo de espera. Puedes reintentar."; }
        catch (Exception ex) { Controller.Log("Instalar actualización: " + ex); UpdateStatusText.Text = "No se pudo instalar: " + ex.Message; }
        finally
        {
            installingUpdate = false; updateCancellation.Dispose(); updateCancellation = null;
            if (!exiting) { CheckUpdatesButton.IsEnabled = true; InstallUpdateButton.IsEnabled = true; UpdateProgress.Visibility = Visibility.Collapsed; }
            if (exitAfterDownload && !exiting) { exitAfterDownload = false; Exit(); }
        }
    }
    private void UpdatePreferencesChanged(object sender, RoutedEventArgs e) { if (ready) { settings.CheckUpdatesAtStartup = UpdateStartupCheck.IsChecked == true; MarkDirty(); } }
    private void OpenReleases(object sender, RoutedEventArgs e) { try { Process.Start(new ProcessStartInfo(Updates.RepositoryUrl + "/releases") { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(this, ex.Message); } }
    private void DuplicateProfile(object sender, RoutedEventArgs e)
    {
        if (settings.Profiles.Count >= 50) return;
        var name = Ask("Nombre del nuevo perfil", settings.Current.Name + " (copia)"); if (name == null) return;
        settings.Profiles.Add(new Profile { Name = name, Points = settings.Current.Points.Select(p => p.Copy()).ToList() }); settings.ActiveProfile = settings.Profiles.Count - 1;
        BindProfiles(); MarkDirty();
    }
    private void RenameProfile(object sender, RoutedEventArgs e)
    {
        var name = Ask("Renombrar perfil", settings.Current.Name); if (name == null) return;
        settings.Current.Name = name; BindProfiles(); MarkDirty();
    }
    private void DeleteProfile(object sender, RoutedEventArgs e)
    {
        if (settings.Profiles.Count == 1) { MessageBox.Show(this, "Conserva al menos un perfil.", "Perfiles"); return; }
        if (MessageBox.Show(this, $"¿Eliminar el perfil «{settings.Current.Name}»?", "Perfiles", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        settings.Profiles.RemoveAt(settings.ActiveProfile); settings.ActiveProfile = Math.Min(settings.ActiveProfile, settings.Profiles.Count - 1); BindProfiles(); MarkDirty();
    }
    private string? Ask(string title, string initial)
    {
        var input = new TextBox { Text = initial, Margin = new Thickness(0, 12, 0, 14), MaxLength = 80 };
        var dialog = new Window { Title = title, Width = 380, Height = 190, ResizeMode = ResizeMode.NoResize, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Foreground = Foreground };
        dialog.Resources.MergedDictionaries.Add(Resources);
        var panel = new StackPanel { Margin = new Thickness(18) }; panel.Children.Add(new TextBlock { Text = title }); panel.Children.Add(input);
        var button = new Button { Content = "Guardar", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        button.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.DialogResult = true; };
        panel.Children.Add(button); dialog.Content = panel; dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }
    private void UpdatePointFields()
    {
        if (Editor.Selected == null) return;
        PointTime.Text = Curve.Time(Editor.Selected.Minute); PointStrength.Text = Editor.Selected.Strength.ToString(CultureInfo.CurrentCulture);
    }
    private bool ApplyPointFields()
    {
        if (!ready || Editor.Selected == null) return true;
        try
        {
            int minute = Curve.ParseTime(PointTime.Text.Trim());
            if (settings.Current.Points.Any(p => p != Editor.Selected && p.Minute == minute)) throw new FormatException("Ya existe un punto a esa hora.");
            if (!double.TryParse(PointStrength.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) || !double.IsFinite(value) || value is < 0 or > 100) throw new FormatException("La intensidad debe estar entre 0 y 100 %.");
            if (Editor.Selected.Minute != minute || Editor.Selected.Strength != value)
            {
                Editor.Selected.Minute = minute; Editor.Selected.Strength = value; Editor.InvalidateVisual(); MarkDirty();
            }
            return true;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Editar punto"); UpdatePointFields(); return false; }
    }
    private void PointKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { ApplyPointFields(); e.Handled = true; Keyboard.ClearFocus(); } }
    private void PointLostFocus(object sender, KeyboardFocusChangedEventArgs e) { ApplyPointFields(); }
    private void AddPoint(object sender, RoutedEventArgs e)
    {
        var points = settings.Current.Points; if (points.Count >= 96) return;
        int minute = (int)DateTime.Now.TimeOfDay.TotalMinutes;
        while (points.Any(p => p.Minute == minute)) minute = (minute + 5) % 1440;
        var p = new CurvePoint(minute, Math.Round(Curve.Evaluate(points, minute))); points.Add(p); Editor.Select(p); MarkDirty();
    }
    private void DeletePoint(object sender, RoutedEventArgs e)
    {
        if (Editor.Selected == null || settings.Current.Points.Count <= 2) return;
        settings.Current.Points.Remove(Editor.Selected); Editor.ResetSelection(); MarkDirty();
    }
    private void ToggleEnabled(object sender, RoutedEventArgs e)
    {
        settings.Enabled = !settings.Enabled; controller.Resume(); MarkDirty(); Tick(null, EventArgs.Empty);
    }
    private void ModeChanged(object sender, RoutedEventArgs e) { if (ready) { settings.Automatic = AutoMode.IsChecked == true; MarkDirty(); } }
    private void ManualChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (ready) { settings.ManualStrength = ManualSlider.Value; MarkDirty(); } }
    private void PauseClicked(object sender, RoutedEventArgs e) { if (controller.PauseUntil.HasValue) controller.Resume(); else controller.Pause(TimeSpan.FromHours(1)); Tick(null, EventArgs.Empty); }
    private void PreferencesChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        settings.PauseFullscreen = FullscreenCheck.IsChecked == true; settings.TransitionSeconds = (int)TransitionSlider.Value; MarkDirty();
        settings.RecoverAfterCrash = RecoveryCheck.IsChecked == true;
        crashGuard.SetActive(controller.IsApplying && settings.RecoverAfterCrash);
    }
    private void ExclusionsChanged(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!ready) return;
        var previous = settings.ExcludedApps;
        try { settings.ExcludedApps = ExclusionsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); settings.Validate(); MarkDirty(); }
        catch (Exception ex) { settings.ExcludedApps = previous; ExclusionsBox.Text = string.Join(", ", previous); MessageBox.Show(this, ex.Message, "Aplicaciones excluidas"); }
    }
    private void StartupChanged(object sender, RoutedEventArgs e)
    {
        try { Settings.StartWithWindows = StartupCheck.IsChecked == true; } catch (Exception ex) { StartupCheck.IsChecked = Settings.StartWithWindows; MessageBox.Show(this, ex.Message, "Inicio con Windows"); }
    }
    private void OpenWindows(object sender, RoutedEventArgs e) { try { Process.Start(new ProcessStartInfo("ms-settings:nightlight") { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(this, ex.Message); } }
    private void DisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => { controller.Refresh(); timer.Interval = TimeSpan.FromMilliseconds(250); Tick(null, EventArgs.Empty); });
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) => Dispatcher.BeginInvoke(() => { if (e.Mode == PowerModes.Suspend) { controller.Suspended = true; controller.TryRestore(); } else if (e.Mode == PowerModes.Resume) { controller.Suspended = false; controller.Refresh(); } });
    private void SessionChanged(object sender, SessionSwitchEventArgs e) => Dispatcher.BeginInvoke(() => { if (e.Reason == SessionSwitchReason.SessionLock) { controller.SessionLocked = true; controller.TryRestore(); } else if (e.Reason == SessionSwitchReason.SessionUnlock) { controller.SessionLocked = false; controller.Refresh(); } });
    private void SessionEnding(object sender, SessionEndingEventArgs e) => Dispatcher.Invoke(() => controller.TryRestore());
    private void OnClosing(object? sender, CancelEventArgs e) { if (!exiting) { e.Cancel = true; if (!dirty || Save(true)) Hide(); } }
    private void ExitClicked(object sender, RoutedEventArgs e) => Exit();
    private void Exit()
    {
        if (exiting) return;
        if (installingUpdate && !updateHandoff) { exitAfterDownload = true; updateCancellation?.Cancel(); UpdateStatusText.Text = "Cancelando descarga antes de salir…"; return; }
        if (dirty && !Save(true)) return;
        if (!controller.TryRestore()) { MessageBox.Show(this, "Windows no pudo restaurar el color. Abre Luz nocturna de Windows y actívala/desactívala para recuperarlo.\n\n" + controller.LastError, "Restaurar color"); }
        exiting = true; timer.Stop();
        SystemEvents.DisplaySettingsChanged -= DisplayChanged; SystemEvents.PowerModeChanged -= PowerChanged; SystemEvents.SessionSwitch -= SessionChanged; SystemEvents.SessionEnding -= SessionEnding;
        controller.Dispose(); crashGuard.Dispose(); shutdown.Dispose(); tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); trayIcon.Dispose();
        Close(); Application.Current.Shutdown();
    }
    private static Icon MakeIcon()
    {
        using var bitmap = new Bitmap(32, 32); using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; g.Clear(System.Drawing.Color.Transparent);
        using var warm = new SolidBrush(System.Drawing.Color.White); using var dark = new SolidBrush(System.Drawing.Color.FromArgb(7, 7, 8));
        g.FillEllipse(warm, 3, 3, 26, 26); g.FillEllipse(dark, 12, 0, 21, 23);
        nint handle = bitmap.GetHicon(); try { using var icon = System.Drawing.Icon.FromHandle(handle); return (Icon)icon.Clone(); } finally { DestroyIcon(handle); }
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
}
