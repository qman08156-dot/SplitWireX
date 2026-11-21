#nullable enable
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace SplitWireTurkey
{
    /// <summary>
    /// SplitWireX bridge. The original SplitWire window keeps ALL of its logic; when SplitWireX
    /// is running it simply lives hidden in the background and the small panel drives its
    /// buttons/switches/presets. Nothing in MainWindow.xaml.cs is reimplemented here.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>True when the window is hosted behind the SplitWireX panel.</summary>
        internal bool XHosted { get; set; }

        /// <summary>Set when Windows is logging off / shutting down so closing is never blocked.</summary>
        internal static bool XForceClose { get; set; }

        /// <summary>Raised when SplitWire's own "loading" overlay is shown/hidden.</summary>
        internal event Action<bool>? XBusyChanged;

        private bool _xWarmedUp;
        private bool _xHooked;

        internal bool XIsBusy => loadingOverlay != null && loadingOverlay.Visibility == Visibility.Visible;

        internal void XHookBusy()
        {
            if (_xHooked || loadingOverlay == null) return;
            _xHooked = true;
            DependencyPropertyDescriptor
                .FromProperty(UIElement.VisibilityProperty, loadingOverlay.GetType())
                .AddValueChanged(loadingOverlay, (_, _) => XBusyChanged?.Invoke(XIsBusy));
        }

        /// <summary>
        /// Shows the window once far off-screen and hides it again, so its Loaded logic runs and
        /// dialogs that use it as Owner work, without the user ever seeing it.
        /// </summary>
        internal void XWarmUp()
        {
            if (_xWarmedUp) return;
            _xWarmedUp = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -32000;
            Top = -32000;
            ShowInTaskbar = false;
            ShowActivated = false;
            Show();
            Hide();
            ShowInTaskbar = true;
            ShowActivated = true;
        }

        /// <summary>Opens the original full SplitWire window (for advanced settings).</summary>
        internal void XShowFull()
        {
            XWarmUp();
            var wa = SystemParameters.WorkArea;
            double w = ActualWidth > 0 ? ActualWidth : Width;
            double h = ActualHeight > 0 ? ActualHeight : Height;
            Left = wa.Left + (wa.Width - w) / 2;
            Top = wa.Top + (wa.Height - h) / 2;
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        internal T? XFind<T>(string name) where T : class => FindName(name) as T;

        /// <summary>Clicks one of the original buttons exactly as if the user had clicked it.</summary>
        internal void XClick(string buttonName)
        {
            if (FindName(buttonName) is ButtonBase b && b.IsEnabled)
            {
                b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, b));
            }
        }

        /// <summary>Real exit (from the panel / tray), skipping the "are you sure" prompt.</summary>
        internal void XExit()
        {
            _isExitingFromButton = true;
            System.Windows.Application.Current.Shutdown();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // In SplitWireX mode closing the full window just tucks it away again;
            // the app keeps running in the tray with the small panel.
            if (XHosted && !XForceClose && !_isExitingFromButton && !_isRestarting && !_isUninstalling && !_isSingleInstanceReject)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnClosing(e);
        }
    }
}
