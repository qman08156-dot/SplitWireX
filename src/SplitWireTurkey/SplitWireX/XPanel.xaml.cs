#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SplitWireTurkey.SplitWireX
{
    /// <summary>
    /// The compact SplitWireX flyout. Every control is wired to the matching control of the
    /// (hidden) original SplitWire window, so the original code does all the real work.
    /// </summary>
    public partial class XPanel : Window
    {
        private static readonly Duration Fast = TimeSpan.FromMilliseconds(110);
        private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        private readonly MainWindow _main;
        private readonly DispatcherTimer _poll;
        private readonly List<(ComboBox panel, string source)> _combos = new();
        private readonly Dictionary<RadioButton, FrameworkElement> _pages;
        private readonly HashSet<string> _activeOnce = new();
        private DateTime _lastAutoHide = DateTime.MinValue;
        private DateTime _shownAt = DateTime.MinValue;
        private bool _hiding;
        private bool _refreshing;

        public event Action<XStatus.Snapshot>? StatusUpdated;

        public XPanel(MainWindow main)
        {
            InitializeComponent();
            _main = main;

            _pages = new()
            {
                [TabWireSock] = PageWireSock,
                [TabByeDPI] = PageByeDPI,
                [TabZapret] = PageZapret,
                [TabZapret2] = PageZapret2,
                [TabGdpi] = PageGdpi,
                [TabTools] = PageTools,
            };

            BindToggle(WsBrowsers, "chkBrowserTunneling");
            BindToggle(WsRepeater, "chkWireSockRepeater");
            BindButton(WsStandard, "btnStart");
            BindButton(WsAlternative, "btnAlternativeSetup");
            BindButton(WsRemove, "btnWireSockRemove");

            BindToggle(BdBrowsers, "chkByeDPIBrowserTunneling");
            BindButton(BdSetup, "btnByeDPISetup");
            BindButton(BdDll, "btnByeDPIDLLSetup");
            BindButton(BdRemove, "btnRemoveByeDPI");

            BindCombo(ZpPreset, "cmbZapretPresets");
            BindCombo(ZpScan, "cmbScanLevel");
            BindButton(ZpService, "btnZapretCustomService");
            BindOnce(ZpOnce, "btnZapretCustomBatch", "Zapret");
            BindButton(ZpAuto, "btnZapretAutoInstall");
            BindButton(ZpRemove, "btnRemoveZapret");

            BindCombo(Z2Preset, "cmbZapret2Presets");
            BindCombo(Z2Scan, "cmbScanLevel2");
            BindButton(Z2Service, "btnZapret2CustomService");
            BindOnce(Z2Once, "btnZapret2CustomBatch", "Zapret2");
            BindButton(Z2Auto, "btnZapret2AutoInstall");
            BindButton(Z2Remove, "btnRemoveZapret2");

            BindCombo(GdPreset, "cmbGoodbyeDPIPresets");
            BindToggle(GdBlacklist, "chkGoodbyeDPIUseBlacklist");
            BindButton(GdService, "btnGoodbyeDPIService");
            BindOnce(GdOnce, "btnGoodbyeDPIBatch", "GoodbyeDPI");
            BindButton(GdRemove, "btnRemoveGoodbyeDPI");

            BindButton(TlDiscordRepair, "btnDiscordRepair");
            BindButton(TlResetDns, "btnResetDNSDoH");
            BindButton(TlRemoveAll, "btnRemoveAllServices");

            TabWireSock.IsChecked = true;

            _main.XBusyChanged += busy => Dispatcher.BeginInvoke(() => SetBusy(busy));
            SetBusy(_main.XIsBusy);
            XConsole.RunsChanged += () => Dispatcher.BeginInvoke(RefreshSoon);

            _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _poll.Tick += async (_, _) => await RefreshStatusAsync();

            Deactivated += OnDeactivated;
        }

        // ------------------------------------------------------------------ wiring

        private void BindToggle(ToggleButton panelToggle, string sourceName)
        {
            var src = _main.XFind<ToggleButton>(sourceName);
            if (src == null) { panelToggle.Visibility = Visibility.Collapsed; return; }
            panelToggle.SetBinding(ToggleButton.IsCheckedProperty,
                new Binding(nameof(ToggleButton.IsChecked)) { Source = src, Mode = BindingMode.TwoWay });
            panelToggle.SetBinding(IsEnabledProperty,
                new Binding(nameof(IsEnabled)) { Source = src, Mode = BindingMode.OneWay });
        }

        private void BindButton(Button panelButton, string sourceName)
        {
            var src = _main.XFind<ButtonBase>(sourceName);
            if (src == null) { panelButton.Visibility = Visibility.Collapsed; return; }
            panelButton.SetBinding(IsEnabledProperty,
                new Binding(nameof(IsEnabled)) { Source = src, Mode = BindingMode.OneWay });
            panelButton.Click += (_, _) => { _main.XClick(sourceName); RefreshSoon(); };
        }

        /// <summary>"Run once" button that turns into "Stop" while the hidden run is alive.</summary>
        private void BindOnce(Button panelButton, string sourceName, string runKey)
        {
            if (_main.XFind<ButtonBase>(sourceName) == null) { panelButton.Visibility = Visibility.Collapsed; return; }
            panelButton.Tag = runKey;
            panelButton.Click += (_, _) =>
            {
                if (_activeOnce.Contains(runKey)) XConsole.StopRun(runKey);
                else _main.XClick(sourceName);
                RefreshSoon();
            };
        }

        private void BindCombo(ComboBox panelCombo, string sourceName)
        {
            var src = _main.XFind<ComboBox>(sourceName);
            if (src == null) { panelCombo.Visibility = Visibility.Collapsed; return; }
            _combos.Add((panelCombo, sourceName));
            panelCombo.SelectionChanged += (_, _) =>
            {
                if (panelCombo.SelectedIndex >= 0 && src.SelectedIndex != panelCombo.SelectedIndex)
                    src.SelectedIndex = panelCombo.SelectedIndex;
            };
            src.SelectionChanged += (_, _) =>
            {
                if (panelCombo.SelectedIndex != src.SelectedIndex) panelCombo.SelectedIndex = src.SelectedIndex;
            };
        }

        private void SyncCombos()
        {
            foreach (var (panel, sourceName) in _combos)
            {
                var src = _main.XFind<ComboBox>(sourceName);
                if (src == null) continue;
                var names = src.Items.Cast<object>().Select(DisplayText).ToList();
                if (sourceName.StartsWith("cmbScanLevel") && names.Count == 3)
                    names = new List<string> { "Fast", "Standard", "Full" };
                if (panel.ItemsSource is not List<string> cur || !cur.SequenceEqual(names))
                    panel.ItemsSource = names;
                panel.SelectedIndex = src.SelectedIndex;
                panel.IsEnabled = src.IsEnabled;
            }
        }

        private static string DisplayText(object item) => item switch
        {
            ComboBoxItem cbi => cbi.Content?.ToString() ?? "",
            null => "",
            _ => item.ToString() ?? ""
        };

        // ------------------------------------------------------------------ tabs

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (_pages == null) return;
            foreach (var (tab, page) in _pages)
            {
                if (tab == sender)
                {
                    page.Visibility = Visibility.Visible;
                    page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Fast) { EasingFunction = Ease });
                }
                else
                {
                    page.BeginAnimation(OpacityProperty, null);
                    page.Visibility = Visibility.Hidden; // keeps the panel height constant
                }
            }
            if (IsLoaded) SyncCombos();
        }

        // ------------------------------------------------------------------ status

        private void SetBusy(bool busy)
        {
            BusyLine.IsIndeterminate = busy;
            BusyLine.BeginAnimation(OpacityProperty, new DoubleAnimation(busy ? 1 : 0, Fast));
            Pages.IsEnabled = !busy;
            TabsHost.IsEnabled = !busy;
            if (!busy)
            {
                SyncCombos();
                RefreshSoon();
            }
        }

        private async void RefreshSoon()
        {
            await Task.Delay(400);
            await RefreshStatusAsync();
        }

        public async Task RefreshStatusAsync()
        {
            if (_refreshing) return;
            _refreshing = true;
            try
            {
                var snap = await Task.Run(XStatus.Capture);
                ApplyStatus(snap);
                StatusUpdated?.Invoke(snap);
            }
            catch { }
            finally { _refreshing = false; }
        }

        private void ApplyStatus(XStatus.Snapshot snap)
        {
            string text;
            string brush;
            if (snap.ActiveNames.Count > 0)
            {
                brush = "XOk";
                text = snap.ActiveNames.Count == 1 ? snap.ActiveNames[0] : $"{snap.ActiveNames[0]} +{snap.ActiveNames.Count - 1}";
            }
            else if (snap.Methods.Any(m => m.State == XStatus.State.Stopped && m.Name != "DNSCrypt"))
            {
                brush = "XWarn";
                text = "Stopped";
            }
            else
            {
                brush = "XOff";
                text = "Off";
            }
            StatusDot.Fill = (Brush)FindResource(brush);
            StatusText.Foreground = (Brush)FindResource(brush == "XOff" ? "XMuted" : "XText");
            StatusText.Text = text;
            StatusPill.ToolTip = string.Join("\n", snap.Methods.Select(m =>
                $"{m.Name}: {m.State switch { XStatus.State.Running => "running", XStatus.State.Stopped => "stopped", _ => "—" }}"));

            _activeOnce.Clear();
            foreach (var k in snap.OneTime) _activeOnce.Add(k);
            foreach (var b in new[] { ZpOnce, Z2Once, GdOnce })
            {
                bool running = b.Tag is string key && _activeOnce.Contains(key);
                b.Content = running ? "Stop" : "Run once";
                b.Foreground = (Brush)FindResource(running ? "XAccent" : "XText");
            }
        }

        // ------------------------------------------------------------------ show / hide

        public void Toggle()
        {
            // A tray click first deactivates (and auto-hides) the panel; don't re-open it then.
            if ((DateTime.Now - _lastAutoHide).TotalMilliseconds < 400) return;
            if (IsVisible && !_hiding) HidePanel();
            else ShowPanel();
        }

        public void ShowPanel()
        {
            _hiding = false;
            SyncCombos();
            if (!IsVisible)
            {
                Root.Opacity = 0;
                Show();
                UpdateLayout();
            }
            PlaceBottomRight();
            Activate();
            _shownAt = DateTime.Now;
            _poll.Start();
            _ = RefreshStatusAsync();

            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Fast));
            RootShift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(140)) { EasingFunction = Ease });
        }

        public void HidePanel()
        {
            if (!IsVisible || _hiding) return;
            _hiding = true;
            _poll.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(80));
            fade.Completed += (_, _) => { if (_hiding) { Hide(); _hiding = false; } };
            Root.BeginAnimation(OpacityProperty, fade);
            RootShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 6, TimeSpan.FromMilliseconds(80)));
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        private async void OnDeactivated(object? sender, EventArgs e)
        {
            // Let focus settle, then hide like a flyout -- unless SplitWire is busy or one of
            // its own dialogs (confirmations, results) just took focus.
            await Task.Delay(60);
            if (IsActive || _main.XIsBusy) return;
            if ((DateTime.Now - _shownAt).TotalMilliseconds < 1500) return; // just opened
            GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
            if (pid == (uint)Environment.ProcessId) return;
            _lastAutoHide = DateTime.Now;
            HidePanel();
        }

        private void PlaceBottomRight()
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - ActualWidth;
            Top = wa.Bottom - ActualHeight;
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            if (IsVisible) PlaceBottomRight();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!XShell.IsQuitting)
            {
                e.Cancel = true;
                HidePanel();
                return;
            }
            base.OnClosing(e);
        }

        private void BtnFull_Click(object sender, RoutedEventArgs e)
        {
            HidePanel();
            _main.XShowFull();
        }

        private void BtnQuit_Click(object sender, RoutedEventArgs e) => XShell.Quit();
    }
}
