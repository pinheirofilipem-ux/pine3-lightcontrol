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
using Pine3LightControl.Core;
using Pine3LightControl.Core.HomeAssistant;
using Pine3LightControl.Core.Lights;
using Pine3LightControl.Core.Scenes;
using Pine3LightControl.ViewModels;
using Forms = System.Windows.Forms;

namespace Pine3LightControl;

public partial class MainWindow : Window
{
    private readonly LightHub _hub;
    private bool _loading = true;
    private bool _quitting;

    // Lights
    private readonly ObservableCollection<LightVm> _haCards = new();
    private readonly ObservableCollection<LightVm> _pcCards = new();
    private readonly ObservableCollection<OpenRgbDeviceVm> _orgbDevices = new();
    private readonly HashSet<string> _dirty = new();
    private bool _syncQueued;

    // Scenes
    private readonly ObservableCollection<SceneVm> _scenes = new();
    private readonly ObservableCollection<SceneLightChoice> _newSceneLights = new();

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

        HaLightsList.ItemsSource = _haCards;
        PcLightsList.ItemsSource = _pcCards;
        OrgbDevices.ItemsSource = _orgbDevices;
        ScenesList.ItemsSource = _scenes;
        QuickScenes.ItemsSource = _scenes;
        NewSceneLights.ItemsSource = _newSceneLights;
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

        _hub.ScenesChanged += () => Dispatcher.BeginInvoke(RebuildScenes);
        _hub.OpenRgb.ConnectionChanged += () => Dispatcher.BeginInvoke(UpdateOrgbStatus);
        _hub.OpenRgb.DevicesReloaded += () => Dispatcher.BeginInvoke(RebuildOrgbDevices);
        UpdateOrgbStatus();
        RebuildOrgbDevices();
        RebuildScenes();
        _haTimer.Tick += (_, _) => RefreshEntityStates();

        // Só agora: selecionar no XAML dispara o evento durante o InitializeComponent,
        // antes de as páginas existirem.
        Nav.SelectedIndex = 0;
        _loading = false;
    }

    // ══ Janela / tray ═══════════════════════════════════════════════════════

    protected override void OnClosing(CancelEventArgs e)
    {
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
        Close();
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageAbout is null) return;   // ainda a construir a janela
        if (Nav.SelectedItem is not ListBoxItem item) return;
        var tag = (string)item.Tag;
        PageLights.Visibility = tag == "Lights" ? Visibility.Visible : Visibility.Collapsed;
        PageScenes.Visibility = tag == "Scenes" ? Visibility.Visible : Visibility.Collapsed;
        PagePc.Visibility = tag == "Pc" ? Visibility.Visible : Visibility.Collapsed;
        PageHomeAssistant.Visibility = tag == "HomeAssistant" ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = tag == "About" ? Visibility.Visible : Visibility.Collapsed;

        if (tag == "HomeAssistant") { RefreshEntityStates(); _haTimer.Start(); }
        else _haTimer.Stop();
        if (tag == "Scenes") RebuildNewSceneLights();
    }

    /// <summary>Tags das páginas, pela ordem do menu (usado pelo auto-teste).</summary>
    internal IEnumerable<string> PageTags => Nav.Items.OfType<ListBoxItem>().Select(i => (string)i.Tag);

    internal void Go(string tag) =>
        Nav.SelectedItem = Nav.Items.OfType<ListBoxItem>().First(i => (string)i.Tag == tag);

    private void GoScenes_Click(object sender, RoutedEventArgs e) => Go("Scenes");
    private void GoPc_Click(object sender, RoutedEventArgs e) => Go("Pc");
    private void GoHa_Click(object sender, RoutedEventArgs e) => Go("HomeAssistant");

    // ══ Lights ══════════════════════════════════════════════════════════════

    private void RebuildLights()
    {
        Sync(_haCards, _hub.HaLights.Cast<ILight>().ToList());
        Sync(_pcCards, _hub.OpenRgbLights.Cast<ILight>().ToList());
        PcHeader.Visibility = _pcCards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HaHeader.Visibility = _haCards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LightsEmpty.Visibility = _haCards.Count + _pcCards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RebuildNewSceneLights();
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
            foreach (var c in _haCards.Concat(_pcCards))
                if (ids.Contains(c.Light.Id)) c.Sync();
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

    // ══ PC RGB (OpenRGB) ══════════════════════════════════════════════════

    private void UpdateOrgbStatus()
    {
        OrgbStatus.Text = _hub.OpenRgb.StateText;
    }

    private void RebuildOrgbDevices()
    {
        _orgbDevices.Clear();
        foreach (var (name, type, count) in _hub.OpenRgbGroups)
            _orgbDevices.Add(new OpenRgbDeviceVm(name, type, count, _hub.IsOpenRgbSelected(name),
                (n, on) => _hub.SetOpenRgbSelected(n, on)));
        // Escolhidos que o OpenRGB não está a mostrar continuam na lista para se poderem desmarcar
        foreach (var name in _hub.Config.OpenRgb.Devices.Where(n => _orgbDevices.All(d => d.Name != n)).ToList())
            _orgbDevices.Add(new OpenRgbDeviceVm(name, "not found", 0, true, (n, on) => _hub.SetOpenRgbSelected(n, on)));
        OrgbEmpty.Visibility = _orgbDevices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateOrgbStatus();
    }

    private async void OrgbConnect_Click(object sender, RoutedEventArgs e)
    {
        var o = _hub.Config.OpenRgb;
        if (!int.TryParse(OrgbPort.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            OrgbStatus.Text = "Invalid port.";
            return;
        }
        o.Host = string.IsNullOrWhiteSpace(OrgbHost.Text) ? "127.0.0.1" : OrgbHost.Text.Trim();
        o.Port = port;
        o.Enabled = OrgbEnabled.IsChecked == true;
        _hub.Config.Save();
        await _hub.ReconnectOpenRgbAsync();
        UpdateOrgbStatus();
    }

    private void OrgbDownload_Click(object sender, RoutedEventArgs e) => Shell.Open("https://openrgb.org");

    // ══ Scenes ════════════════════════════════════════════════════════════

    private void RebuildScenes()
    {
        var byId = _scenes.ToDictionary(v => v.Scene.Id);
        _scenes.Clear();
        foreach (var sc in _hub.Scenes)
        {
            if (!byId.TryGetValue(sc.Id, out var vm)) vm = new SceneVm(sc);
            vm.Refresh();
            _scenes.Add(vm);
        }
        ScenesEmpty.Visibility = _scenes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        QuickScenesBar.Visibility = _scenes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Lista de luzes para a nova cena (mantém o que estava marcado).</summary>
    private void RebuildNewSceneLights()
    {
        var was = _newSceneLights.ToDictionary(c => c.Id, c => c.Include);
        _newSceneLights.Clear();
        foreach (var l in _hub.Lights)
        {
            bool isAction = (l.Features & LightFeatures.Action) != 0;
            _newSceneLights.Add(new SceneLightChoice(l.Id, l.Name, isAction ? "Activate" : DescribeNow(l))
            {
                // Por omissão: todas as luzes; cenas/scripts do HA só se marcados
                Include = was.TryGetValue(l.Id, out var inc) ? inc : !isAction,
            });
        }
        NewSceneEmpty.Visibility = _newSceneLights.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string DescribeNow(ILight l)
    {
        if (!l.IsAvailable) return "unavailable";
        var st = SceneEngine.CaptureLight(l);
        if (!st.On) return "off";
        var parts = new List<string> { "on" };
        if (st.Brightness is { } b) parts.Add($"{Math.Round(b * 100.0 / 255)}%");
        if (st.ColorTempKelvin is { } k) parts.Add($"{k} K");
        if (st.Color is { } c) parts.Add(c);
        if (st.Effect is { } e) parts.Add(e);
        return string.Join(" · ", parts);
    }

    private void CreateScene_Click(object sender, RoutedEventArgs e)
    {
        var name = NewSceneName.Text.Trim();
        if (name.Length == 0) { Info("Give the scene a name."); return; }
        var ids = _newSceneLights.Where(c => c.Include).Select(c => c.Id).ToList();
        if (ids.Count == 0) { Info("Choose at least one light."); return; }
        _hub.CreateScene(name, ids);
        NewSceneName.Text = "";
    }

    private void ActivateScene_Click(object sender, RoutedEventArgs e)
    {
        if (Vm<SceneVm>(sender) is not { } vm) return;
        var missing = _hub.ActivateScene(vm.Scene);
        if (missing > 0 && PageScenes.Visibility == Visibility.Visible)
            Info($"{missing} light(s) in this scene are unavailable right now.");
    }

    private void UpdateScene_Click(object sender, RoutedEventArgs e)
    {
        if (Vm<SceneVm>(sender) is not { } vm) return;
        if (MessageBox.Show(this, $"Save the current state of the lights into \"{vm.Name}\"?", "Pine3 Light Control",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _hub.UpdateScene(vm.Scene);
    }

    private void DeleteScene_Click(object sender, RoutedEventArgs e)
    {
        if (Vm<SceneVm>(sender) is not { } vm) return;
        if (MessageBox.Show(this, $"Delete the scene \"{vm.Name}\"?", "Pine3 Light Control",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _hub.DeleteScene(vm.Scene);
    }

    private void SceneUp_Click(object sender, RoutedEventArgs e)
    {
        if (Vm<SceneVm>(sender) is { } vm) _hub.MoveScene(vm.Scene, -1);
    }

    private void SceneDown_Click(object sender, RoutedEventArgs e)
    {
        if (Vm<SceneVm>(sender) is { } vm) _hub.MoveScene(vm.Scene, +1);
    }

    private void SceneName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: SceneVm vm } tb) _hub.RenameScene(vm.Scene, tb.Text);
    }

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
        OrgbHost.Text = c.OpenRgb.Host;
        OrgbPort.Text = c.OpenRgb.Port.ToString();
        OrgbEnabled.IsChecked = c.OpenRgb.Enabled;
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
