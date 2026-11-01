using System;
using System.Linq;
using System.Windows;
using Pine3LightControl.Core.Lights;
using Forms = System.Windows.Forms;

namespace Pine3LightControl;

/// <summary>
/// Ícone na área de notificação. Clique esquerdo abre o painel; o menu permite
/// ligar/desligar cada luz e ativar cenas sem abrir a janela.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly LightHub _hub;
    private readonly Action _showMain;
    private readonly Action _quit;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu = new();

    public TrayIcon(LightHub hub, Action showMain, Action quit)
    {
        _hub = hub;
        _showMain = showMain;
        _quit = quit;

        var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/icon.ico"));
        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(res!.Stream, Forms.SystemInformation.SmallIconSize),
            Text = "Pine3 Light Control",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) _showMain(); };
        _menu.Opening += (_, _) => Rebuild();
        Rebuild();
    }

    private void Rebuild()
    {
        _menu.Items.Clear();
        var lights = _hub.SceneLights;   // rato e teclado ficam na janela (PC RGB)
        int on = lights.Count(l => l.IsAvailable && l.IsOn && (l.Features & LightFeatures.Action) == 0);
        _icon.Text = on == 0 ? "Pine3 Light Control" : $"Pine3 Light Control — {on} on";

        _menu.Items.Add(new Forms.ToolStripMenuItem("Pine3 Light Control") { Enabled = false });
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("Open control panel", null, (_, _) => _showMain());

        var scenes = _hub.Scenes;
        if (scenes.Count > 0)
        {
            _menu.Items.Add(new Forms.ToolStripSeparator());
            foreach (var sc in scenes)
            {
                var scene = sc;
                _menu.Items.Add(new Forms.ToolStripMenuItem("\u2605  " + scene.Name.Replace("&", "&&"), null,
                    (_, _) => _hub.ActivateScene(scene)));
            }
        }

        var toggles = lights.Where(l => (l.Features & LightFeatures.OnOff) != 0).ToList();
        var actions = lights.Where(l => (l.Features & LightFeatures.Action) != 0).ToList();

        if (toggles.Count > 0)
        {
            _menu.Items.Add(new Forms.ToolStripSeparator());
            foreach (var l in toggles)
            {
                var light = l;
                var item = new Forms.ToolStripMenuItem(Label(light))
                {
                    Checked = light.IsOn,
                    Enabled = light.IsAvailable,
                    ToolTipText = light.StatusText,
                };
                item.Click += (_, _) => light.SetPower(!light.IsOn);
                _menu.Items.Add(item);
            }
            _menu.Items.Add("All on", null, (_, _) => _hub.SetAllPower(true));
            _menu.Items.Add("All off", null, (_, _) => _hub.SetAllPower(false));
        }

        if (actions.Count > 0)
        {
            var sub = new Forms.ToolStripMenuItem("Home Assistant scenes && scripts");
            foreach (var a in actions)
            {
                var action = a;
                sub.DropDownItems.Add(new Forms.ToolStripMenuItem(Label(action), null, (_, _) => action.Activate())
                    { Enabled = action.IsAvailable });
            }
            _menu.Items.Add(sub);
        }

        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("Quit", null, (_, _) => _quit());
    }

    // '&' no WinForms é atalho; duplicar para aparecer literal
    private static string Label(ILight l) => l.Name.Replace("&", "&&");

    public void ShowHint(string title, string text) =>
        _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
