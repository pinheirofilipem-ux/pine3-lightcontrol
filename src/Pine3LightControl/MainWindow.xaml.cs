using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Pine3LightControl.Bluetooth;
using Pine3LightControl.Core;
using Pine3LightControl.Core.Bluetooth;
using Pine3LightControl.Core.HomeAssistant;
using Pine3LightControl.Core.Lights;
using Pine3LightControl.ViewModels;
using Forms = System.Windows.Forms;

namespace Pine3LightControl;

public partial class MainWindow : Window
{
    private readonly LightHub _hub;
    private bool _loading = true;
    private bool _quitting;

    // Lights
    private readonly ObservableCollection<LightVm> _bleCards = new();
    private readonly ObservableCollection<LightVm> _haCards = new();
    private readonly HashSet<string> _dirty = new();
    private bool _syncQueued;

    // Bluetooth
    private readonly ObservableCollection<BleDeviceVm> _bleDevices = new();
    private readonly ObservableCollection<ScanResultVm> _scan = new();
    private readonly BleScanner _scanner = new();
    private readonly DispatcherTimer _scanTimer = new() { Interval = TimeSpan.FromSeconds(20) };

    // Home Assistant
    private readonly ObservableCollection<EntityVm> _entities = new();
    private readonly DispatcherTimer _haTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public MainWindow(LightHub hub)
    {
        _hub = hub;
        InitializeComponent();

        var v = typeof(App).Assembly.GetName().Version;
        var ver = v is null ? "" : $"Version {v.Major}.{v.Minor}.{v.Build}";
        AboutVersion.Text = ver;
        NavFooter.Text = ver;
        DataPath.Text = AppPaths.DataDir;

        BleLightsList.ItemsSource = _bleCards;
        HaLightsList.ItemsSource = _haCards;
        BleDevicesList.ItemsSource = _bleDevices;
        ScanList.ItemsSource = _scan;
        CollectionViewSource.GetDefaultView(_scan).Filter = o => ScanAll.IsChecked == true || ((ScanResultVm)o).LooksLikeLedHue;
        HaEntities.ItemsSource = _entities;
        var ev = CollectionViewSource.GetDefaultView(_entities);
        ev.Filter = EntityFilter;
        ev.SortDescriptions.Add(new SortDescription(nameof(EntityVm.Domain), ListSortDirection.Ascending));
        ev.SortDescriptions.Add(new SortDescription(nameof(EntityVm.Name), ListSortDirection.Ascending));

        HaDomain.Items.Add("All types");
        foreach (var d in HaEntity.SupportedDomains) HaDomain.Items.Add(d);
        HaDomain.SelectedIndex = 0;

        LoadSettings();
        RebuildLights();
        RebuildEntities();
        UpdateHaStatus();

        _hub.ListChanged += () => Dispatcher.BeginInvoke(RebuildLights);
        _hub.LightChanged += OnLightChanged;
        _hub.Ha.ConnectionChanged += () => Dispatcher.BeginInvoke(UpdateHaStatus);
        _hub.Ha.EntitiesReloaded += () => Dispatcher.BeginInvoke(RebuildEntities);

        _scanner.Found += a => Dispatcher.BeginInvoke(() => OnAdvert(a));
        _scanner.Stopped += err => Dispatcher.BeginInvoke(() => ScanStopped(err));
        _scanTimer.Tick += (_, _) => StopScan();
        _haTimer.Tick += (_, _) => RefreshEntityStates();

        // Só agora: selecionar no XAML dispara o evento durante o InitializeComponent,
        // antes de as páginas existirem.
        Nav.SelectedIndex = 0;
        _loading = false;
    }

    // ══ Janela / tray ═══════════════════════════════════════════════════════

    protected override void OnClosing(CancelEventArgs e)
    {
        StopScan();
        if (!_quitting)
        {
            // Fechar só esconde: as luzes continuam controláveis pela tray.
            e.Cancel = true;
            Hide();
            if (!_hub.Config.TrayHintShown)
            {
                App.Tray?.ShowHint("Pine3 Light Control is still running",
                    "It's in the tray. Right-click the icon for quick controls, or choose Quit to exit.");
                _hub.Config.TrayHintShown = true;
                _hub.Config.Save();
            }
        }
        base.OnClosing(e);
    }

    public void CloseForQuit()
    {
        _quitting = true;
        _scanner.Dispose();
        Close();
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageAbout is null) return;   // ainda a construir a janela
        if (Nav.SelectedItem is not ListBoxItem item) return;
        var tag = (string)item.Tag;
        PageLights.Visibility = tag == "Lights" ? Visibility.Visible : Visibility.Collapsed;
        PageBluetooth.Visibility = tag == "Bluetooth" ? Visibility.Visible : Visibility.Collapsed;
        PageHomeAssistant.Visibility = tag == "HomeAssistant" ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = tag == "About" ? Visibility.Visible : Visibility.Collapsed;

        if (tag == "HomeAssistant") { RefreshEntityStates(); _haTimer.Start(); }
        else _haTimer.Stop();
        if (tag != "Bluetooth") StopScan();
    }

    /// <summary>Tags das páginas, pela ordem do menu (usado pelo auto-teste).</summary>
    internal IEnumerable<string> PageTags => Nav.Items.OfType<ListBoxItem>().Select(i => (string)i.Tag);

    internal void Go(string tag) =>
        Nav.SelectedItem = Nav.Items.OfType<ListBoxItem>().First(i => (string)i.Tag == tag);

    private void GoBluetooth_Click(object sender, RoutedEventArgs e) => Go("Bluetooth");
    private void GoHa_Click(object sender, RoutedEventArgs e) => Go("HomeAssistant");

    // ══ Lights ══════════════════════════════════════════════════════════════

    private void RebuildLights()
    {
        Sync(_bleCards, _hub.BleLights.Cast<ILight>().ToList());
        Sync(_haCards, _hub.HaLights.Cast<ILight>().ToList());
        BleHeader.Visibility = _bleCards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HaHeader.Visibility = _haCards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LightsEmpty.Visibility = _bleCards.Count + _haCards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RebuildBleDevices();
        foreach (var e in _entities) e.SetSelectedSilently(_hub.IsHaEntitySelected(e.EntityId));
    }

    /// <summary>Mantém os cartões existentes (não perde o foco/arrasto) e acerta a ordem.</summary>
    private static void Sync(ObservableCollection<LightVm> cards, IList<ILight> lights)
    {
        var byLight = cards.ToDictionary(c => c.Light);
        cards.Clear();
        foreach (var l in lights)
        {
            if (!byLight.TryGetValue(l, out var vm)) vm = new LightVm(l);
            vm.Sync();
            cards.Add(vm);
        }
    }

    /// <summary>As luzes avisam de qualquer thread e muitas vezes: agrega num só refresh.</summary>
    private void OnLightChanged(ILight light)
    {
        lock (_dirty)
        {
            _dirty.Add(light.Id);
            if (_syncQueued) return;
            _syncQueued = true;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            string[] ids;
            lock (_dirty)
            {
                ids = _dirty.ToArray();
                _dirty.Clear();
                _syncQueued = false;
            }
            foreach (var c in _bleCards.Concat(_haCards))
                if (ids.Contains(c.Light.Id)) c.Sync();
            foreach (var d in _bleDevices)
                if (ids.Contains(d.Light.Id)) d.Sync();
        });
    }

    private static T? Vm<T>(object sender) where T : class => (sender as FrameworkElement)?.DataContext as T;

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (Vm<LightVm>(sender) is not { } vm || sender is not Button { Background: SolidColorBrush b }) return;
        vm.SetColor(new RgbColor(b.Color.R, b.Color.G, b.Color.B));
    }

    private void CustomColor_Click(object sender, RoutedEventArgs e)
    {
        if (Vm<LightVm>(sender) is not { } vm) return;
        var c = vm.Light.Color;
        using var dlg = new Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B),
        };
        if (dlg.ShowDialog() == Forms.DialogResult.OK)
            vm.SetColor(new RgbColor(dlg.Color.R, dlg.Color.G, dlg.Color.B));
    }

    private void Activate_Click(object sender, RoutedEventArgs e) => Vm<LightVm>(sender)?.Activate();
    private void AllOn_Click(object sender, RoutedEventArgs e) => _hub.SetAllPower(true);
    private void AllOff_Click(object sender, RoutedEventArgs e) => _hub.SetAllPower(false);

    // ══ Bluetooth ═══════════════════════════════════════════════════════════

    private void RebuildBleDevices()
    {
        var existing = _bleDevices.ToDictionary(d => d.Light);
        _bleDevices.Clear();
        foreach (var l in _hub.BleLights)
            _bleDevices.Add(existing.TryGetValue(l, out var vm) ? vm : new BleDeviceVm(l, _hub.Config));
        BleEmpty.Visibility = _bleDevices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var s in _scan) s.Added = IsAdded(s.Address);
    }

    private bool IsAdded(string address) =>
        _hub.BleLights.Any(l => string.Equals(l.Config.Address, address, StringComparison.OrdinalIgnoreCase));

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_scanner.IsRunning) { StopScan(); return; }
        var problem = await BleAdapter.ProblemAsync();
        if (problem is not null)
        {
            ScanStatus.Text = $"Can't scan: {problem}.";
            return;
        }
        _scan.Clear();
        try
        {
            _scanner.Start();
            _scanTimer.Start();
            ScanButton.Content = "Stop";
            ScanStatus.Text = "Scanning…";
        }
        catch (Exception ex)
        {
            ScanStatus.Text = $"Can't scan: {ex.Message}";
            Log.Write($"[BLE] scan: {ex}");
        }
    }

    private void StopScan()
    {
        _scanTimer.Stop();
        if (_scanner.IsRunning) _scanner.Stop();
        ScanStopped(null);
    }

    private void ScanStopped(string? error)
    {
        if (ScanButton is null) return;   // janela não chegou a ser construída
        _scanTimer.Stop();
        ScanButton.Content = "Scan";
        int hits = _scan.Count(s => s.LooksLikeLedHue);
        ScanStatus.Text = error is not null ? $"Scan stopped: {error}" :
            _scan.Count == 0 ? "" :
            $"Found {_scan.Count} device(s), {hits} look like LED Hue controllers.";
    }

    private void OnAdvert(BleAdvert a)
    {
        var vm = _scan.FirstOrDefault(s => s.Address == a.Address);
        if (vm is null)
        {
            vm = new ScanResultVm(a.Address) { Added = IsAdded(a.Address) };
            _scan.Add(vm);
        }
        if (!string.IsNullOrWhiteSpace(a.Name)) vm.Name = a.Name;
        if (a.AddressType is "Public" or "Random")
        {
            vm.AddressType = a.AddressType;
            if (vm.Added) _hub.LearnAddressType(a.Address, a.AddressType);
        }
        vm.Rssi = a.Rssi;
        if (a.HasFfe0) vm.HasFfe0 = true;
        CollectionViewSource.GetDefaultView(_scan).Refresh();
        if (_scanner.IsRunning) ScanStatus.Text = $"Scanning… {_scan.Count(s => s.LooksLikeLedHue)} controller(s) found";
    }

    private void ScanFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading) CollectionViewSource.GetDefaultView(_scan).Refresh();
    }

    private void AddScanned_Click(object sender, RoutedEventArgs e)
    {
        if (Vm<ScanResultVm>(sender) is not { } s) return;
        var name = string.IsNullOrWhiteSpace(s.Name) ? s.Address : s.Name;
        _hub.AddBleDevice(s.Address, name, s.AddressType);
        s.Added = true;
    }

    private void BleConnect_Click(object sender, RoutedEventArgs e)
    {
        if (Vm<BleDeviceVm>(sender) is not { } d) return;
        d.Light.Start();
        d.Sync();
    }

    private async void BleDisconnect_Click(object sender, RoutedEventArgs e)
    {
        if (Vm<BleDeviceVm>(sender) is not { } d) return;
        await d.Light.StopAsync();
        d.Sync();
    }

    private async void BleRemove_Click(object sender, RoutedEventArgs e)
    {
        if (Vm<BleDeviceVm>(sender) is not { } d) return;
        if (MessageBox.Show(this, $"Remove {d.Name}?", "Pine3 Light Control", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await _hub.RemoveBleDeviceAsync(d.Light);
    }

    private void BleRename_Click(object sender, RoutedEventArgs e)
    {
        if (Vm<BleDeviceVm>(sender) is not { } d || string.IsNullOrWhiteSpace(d.Name)) return;
        if (!d.IsConnected) { Info("Connect to the controller first."); return; }
        d.Light.Rename(d.Name);
        Info("Name sent. The controller shows the new name after it restarts (power-cycle it).");
    }

    private void BleApplyStrip_Click(object sender, RoutedEventArgs e)
    {
        if (Vm<BleDeviceVm>(sender) is not { } d) return;
        if (!d.IsConnected) { Info("Connect to the controller first."); return; }
        if (!int.TryParse(d.PixelCount, out var px) || px < 1 || px > Sp110e.MaxPixels)
        {
            Info($"Number of LEDs must be between 1 and {Sp110e.MaxPixels}.");
            return;
        }
        d.Light.ApplyStripSettings(d.IcModel, d.RgbOrder, px);
    }

    private async void BleDiagnose_Click(object sender, RoutedEventArgs e)
    {
        if (Vm<BleDeviceVm>(sender) is not { } d) return;
        DiagCard.Visibility = Visibility.Visible;
        DiagText.Text = "Reading services…";
        try { DiagText.Text = await BleAdapter.DescribeAsync(d.Address, d.Light.Config.AddressType); }
        catch (Exception ex) { DiagText.Text = ex.Message; }
    }

    private void DiagClose_Click(object sender, RoutedEventArgs e) => DiagCard.Visibility = Visibility.Collapsed;

    private void Info(string text) =>
        MessageBox.Show(this, text, "Pine3 Light Control", MessageBoxButton.OK, MessageBoxImage.Information);

    // ══ Home Assistant ══════════════════════════════════════════════════════

    private void UpdateHaStatus()
    {
        HaStatus.Text = _hub.Ha.StateText;
        HaListEmpty.Visibility = _hub.Ha.State == HaConnectionState.Connected && _entities.Count > 0
            ? Visibility.Collapsed : Visibility.Visible;
        if (_hub.Ha.State == HaConnectionState.Connected) RebuildEntities();
    }

    private void RebuildEntities()
    {
        var snapshot = _hub.Ha.Entities.Values.Where(e => e.IsSupported).ToList();
        var byId = _entities.ToDictionary(e => e.EntityId);
        var seen = new HashSet<string>();
        foreach (var ent in snapshot)
        {
            seen.Add(ent.EntityId);
            if (!byId.TryGetValue(ent.EntityId, out var vm))
            {
                vm = new EntityVm(ent.EntityId, (id, on) => _hub.SetHaEntitySelected(id, on));
                _entities.Add(vm);
            }
            vm.Name = ent.FriendlyName;
            vm.State = ent.State;
            vm.SetSelectedSilently(_hub.IsHaEntitySelected(ent.EntityId));
        }
        // Entidades escolhidas que o HA (ainda) não reporta continuam visíveis para se poderem desmarcar
        foreach (var id in _hub.Config.HomeAssistant.Entities.Where(id => !seen.Contains(id)))
        {
            seen.Add(id);
            if (!byId.TryGetValue(id, out var vm))
            {
                vm = new EntityVm(id, (i, on) => _hub.SetHaEntitySelected(i, on)) { Name = id };
                _entities.Add(vm);
            }
            vm.State = "not found";
            vm.SetSelectedSilently(true);
        }
        foreach (var gone in _entities.Where(e => !seen.Contains(e.EntityId)).ToList()) _entities.Remove(gone);
        HaListEmpty.Visibility = _entities.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshEntityStates()
    {
        foreach (var vm in _entities)
            if (_hub.Ha.Entities.TryGetValue(vm.EntityId, out var ent))
            {
                vm.State = ent.State;
                vm.Name = ent.FriendlyName;
            }
    }

    private bool EntityFilter(object o)
    {
        var e = (EntityVm)o;
        if (HaOnlySelected.IsChecked == true && !e.Selected) return false;
        if (HaDomain.SelectedIndex > 0 && e.Domain != (string)HaDomain.SelectedItem) return false;
        var q = HaSearch.Text.Trim();
        return q.Length == 0 ||
               e.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               e.EntityId.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void HaFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading) CollectionViewSource.GetDefaultView(_entities).Refresh();
    }

    private void HaMoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (Vm<EntityVm>(sender) is { } vm) _hub.MoveHaEntity(vm.EntityId, -1);
    }

    private void HaMoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (Vm<EntityVm>(sender) is { } vm) _hub.MoveHaEntity(vm.EntityId, +1);
    }

    /// <summary>Token novo se escrito; senão mantém o guardado.</summary>
    private string CurrentToken() =>
        HaToken.Password.Length > 0 ? HaToken.Password.Trim() : _hub.Config.HomeAssistant.Token;

    private async void HaConnect_Click(object sender, RoutedEventArgs e)
    {
        var h = _hub.Config.HomeAssistant;
        if (HaUrl.ToWebSocket(HaUrlBox.Text) is null) { HaStatus.Text = "Invalid address."; return; }
        h.Url = HaUrlBox.Text.Trim();
        if (HaToken.Password.Length > 0) h.Token = HaToken.Password.Trim();
        h.AllowInvalidCertificate = HaInsecure.IsChecked == true;
        _hub.Config.Save();
        HaToken.Password = "";
        SetTokenHint();
        await _hub.ReconnectHaAsync();
    }

    private async void HaTest_Click(object sender, RoutedEventArgs e)
    {
        HaStatus.Text = "Testing…";
        var (_, msg) = await HomeAssistantTest();
        HaStatus.Text = msg;
    }

    private System.Threading.Tasks.Task<(bool, string)> HomeAssistantTest() =>
        HaClient.TestAsync(HaUrlBox.Text, CurrentToken(), HaInsecure.IsChecked == true);

    private async void HaDisconnect_Click(object sender, RoutedEventArgs e) => await _hub.Ha.StopAsync();

    private void HaTokenHelp_Click(object sender, RoutedEventArgs e)
    {
        var url = HaUrl.ToWebSocket(HaUrlBox.Text) is { } ws
            ? $"{(ws.Scheme == "wss" ? "https" : "http")}://{ws.Authority}/profile/security"
            : "https://www.home-assistant.io/docs/authentication/#your-account-profile";
        Shell.Open(url);
    }

    private void SetTokenHint()
    {
        HaToken.ToolTip = _hub.Config.HomeAssistant.IsConfigured
            ? "A token is saved (encrypted for your Windows user). Type a new one to replace it."
            : "Home Assistant → your profile → Security → Long-lived access tokens → Create token";
    }

    // ══ Settings ════════════════════════════════════════════════════════════

    private void LoadSettings()
    {
        var c = _hub.Config;
        HaUrlBox.Text = c.HomeAssistant.Url;
        HaInsecure.IsChecked = c.HomeAssistant.AllowInvalidCertificate;
        SetTokenHint();
        AppearanceCombo.SelectedItem = AppearanceCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Tag == c.Appearance.ToString());
        StartupCheck.IsChecked = c.StartWithWindows;
        MinimizedCheck.IsChecked = c.StartMinimized;
    }

    private void Appearance_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AppearanceCombo.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        var mode = Enum.Parse<AppearanceMode>(tag);
        _hub.Config.Appearance = mode;
        _hub.Config.Save();
        Appearance.Apply(mode);
    }

    private void Startup_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool on = StartupCheck.IsChecked == true;
        var (ok, msg) = StartupManager.Set(on);
        if (!ok)
        {
            Info($"Couldn't change the startup setting: {msg}");
            _loading = true;
            StartupCheck.IsChecked = !on;
            _loading = false;
            return;
        }
        _hub.Config.StartWithWindows = on;
        _hub.Config.Save();
    }

    private void Minimized_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _hub.Config.StartMinimized = MinimizedCheck.IsChecked == true;
        _hub.Config.Save();
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        Shell.Open(AppPaths.DataDir);
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(AppPaths.LogFile)) Shell.Open(AppPaths.LogFile);
    }
}
