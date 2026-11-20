using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SigmaCompta.App
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            AjusterTailleInitiale();
            SourceInitialized += (s, e) =>
            {
                var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
                if (source != null) source.AddHook(Crochet);
            };
        }

        /// <summary>
        /// Taille de départ adaptée à l'écran (DPI compris) : 1400×900 au plus, 94 % de la zone utile sinon.
        /// Sur un petit écran (ex. 1920×1080 à 150 % = 1280×720 utiles), la fenêtre s'ouvre agrandie.
        /// </summary>
        private void AjusterTailleInitiale()
        {
            var zone = SystemParameters.WorkArea;
            Width = Math.Min(1400, Math.Floor(zone.Width * 0.94));
            Height = Math.Min(900, Math.Floor(zone.Height * 0.94));
            MinWidth = Math.Min(MinWidth, zone.Width);
            MinHeight = Math.Min(MinHeight, zone.Height);
            if (zone.Width < 1366 || zone.Height < 800) WindowState = WindowState.Maximized;
        }

        private void Reduire(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void Agrandir(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void Fermer(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // ---------------------------------------------------------------------
        // Fenêtre agrandie : sans ce réglage, WindowChrome déborde hors de l'écran
        // et passe sous la barre des tâches. On limite l'agrandissement à la zone
        // utile du moniteur courant (barre des tâches exclue), quel que soit le DPI.
        // ---------------------------------------------------------------------
        private const int WM_GETMINMAXINFO = 0x0024;
        private const int MONITOR_DEFAULTTONEAREST = 2;

        private static IntPtr Crochet(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_GETMINMAXINFO) LimiterAgrandissement(hwnd, lParam);
            return IntPtr.Zero;
        }

        private static void LimiterAgrandissement(IntPtr hwnd, IntPtr lParam)
        {
            var moniteur = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (moniteur == IntPtr.Zero) return;
            var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(moniteur, ref info)) return;

            var mmi = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO));
            mmi.ptMaxPosition.x = info.rcWork.left - info.rcMonitor.left;
            mmi.ptMaxPosition.y = info.rcWork.top - info.rcMonitor.top;
            mmi.ptMaxSize.x = info.rcWork.right - info.rcWork.left;
            mmi.ptMaxSize.y = info.rcWork.bottom - info.rcWork.top;
            Marshal.StructureToPtr(mmi, lParam, true);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    }
}
