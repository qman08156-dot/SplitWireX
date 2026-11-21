#nullable enable
using System;
using System.Drawing;
using System.Linq;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace SplitWireTurkey.SplitWireX
{
    /// <summary>
    /// SplitWireX host: keeps the original SplitWire window hidden, shows a tray icon and the
    /// small bottom-right panel instead.
    /// </summary>
    public static class XShell
    {
        private static WinForms.NotifyIcon? _tray;
        private static XPanel? _panel;
        private static MainWindow? _main;

        public static bool IsQuitting { get; private set; }

        public static void Start(bool showPanel)
        {
            var app = System.Windows.Application.Current;
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.SessionEnding += (_, _) => { MainWindow.XForceClose = true; IsQuitting = true; };
            app.Exit += (_, _) => Cleanup();

            XConsole.StartConsoleGuard();

            _main = new MainWindow { XHosted = true };
            app.MainWindow = _main;
            _main.XWarmUp();
            _main.XHookBusy();

            _panel = new XPanel(_main);
            _panel.StatusUpdated += UpdateTray;

            CreateTray();

            if (showPanel) _panel.ShowPanel();
            else _ = _panel.RefreshStatusAsync();
        }

        private static void CreateTray()
        {
            _tray = new WinForms.NotifyIcon
            {
                Text = "SplitWireX",
                Icon = LoadIcon(),
                Visible = true
            };

            _tray.MouseClick += (_, e) =>
            {
                if (e.Button == WinForms.MouseButtons.Left) _panel?.Toggle();
            };

            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Open SplitWireX panel", null, (_, _) => _panel?.ShowPanel());
            menu.Items.Add("Open full SplitWire", null, (_, _) => { _panel?.HidePanel(); _main?.XShowFull(); });
            var stopItem = new WinForms.ToolStripMenuItem("Stop one-time runs", null, (_, _) => XConsole.StopAllRuns());
            menu.Items.Add(stopItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Quit SplitWireX", null, (_, _) => Quit());
            menu.Opening += (_, _) => stopItem.Enabled = XConsole.GetActiveRuns().Count > 0;
            _tray.ContextMenuStrip = menu;
        }

        private static Icon LoadIcon()
        {
            try
            {
                var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Resources/splitwire.ico"));
                if (res != null)
                {
                    using var s = res.Stream;
                    return new Icon(s, WinForms.SystemInformation.SmallIconSize);
                }
            }
            catch { }
            return SystemIcons.Shield;
        }

        private static void UpdateTray(XStatus.Snapshot snap)
        {
            if (_tray == null) return;
            var text = snap.ActiveNames.Count > 0
                ? "SplitWireX — " + string.Join(", ", snap.ActiveNames)
                : "SplitWireX — not protected";
            _tray.Text = text.Length > 63 ? text[..63] : text; // NotifyIcon limit
        }

        public static void Quit()
        {
            if (XConsole.GetActiveRuns().Count > 0)
            {
                var r = System.Windows.MessageBox.Show(
                    "One-time runs are still active. Stop them and quit?\n\n(Installed services keep running either way.)",
                    "SplitWireX", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return;
                XConsole.StopAllRuns();
            }
            IsQuitting = true;
            Cleanup();
            if (_main != null) _main.XExit();
            else System.Windows.Application.Current.Shutdown();
        }

        private static void Cleanup()
        {
            XConsole.StopConsoleGuard();
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
        }
    }
}
