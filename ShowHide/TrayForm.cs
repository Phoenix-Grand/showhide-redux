using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Drawing;

namespace ShowHide
{
    public class TrayForm : Form
    {
        // Global hotkey id
        private const int HOTKEY_ID = 0x1234;
        private NotifyIcon _trayIcon;
        private ContextMenuStrip _menu;

        // Win32 interop
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string lclassName, string windowTitle);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool IsWindowVisible(IntPtr hWnd);

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const int VK_D = 0x44;

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        public TrayForm()
        {
            // Invisible main window
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            Opacity = 0;

            // Tray UI
            _menu = new ContextMenuStrip();
            _menu.Items.Add("Toggle Desktop Icons", null, (_, __) => ToggleDesktopIcons());
            _menu.Items.Add("Exit", null, (_, __) => Close());

            _trayIcon = new NotifyIcon
            {
                Text = "Show/Hide Desktop Icons",
                Visible = true,
                ContextMenuStrip = _menu,
                Icon = SystemIcons.Application
            };

            _trayIcon.DoubleClick += (s, e) => ToggleDesktopIcons();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // Register Ctrl+Alt+D as the toggle hotkey
            bool ok = RegisterHotKey(Handle, HOTKEY_ID, MOD_CONTROL | MOD_ALT, VK_D);
            if (!ok)
            {
                MessageBox.Show("Could not register hotkey Ctrl+Alt+D.", "ShowHide", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Unregister hotkey
            UnregisterHotKey(Handle, HOTKEY_ID);

            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }

            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_HOTKEY = 0x0312;

            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
            {
                ToggleDesktopIcons();
            }

            base.WndProc(ref m);
        }

        private void ToggleDesktopIcons()
        {
            IntPtr desktopListView = GetDesktopListViewHandle();
            if (desktopListView == IntPtr.Zero)
            {
                MessageBox.Show("Could not find desktop icons window.", "ShowHide", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            bool visible = IsWindowVisible(desktopListView);
            ShowWindow(desktopListView, visible ? SW_HIDE : SW_SHOW);
        }

        /// <summary>
        /// Gets the handle of the SysListView32 that actually hosts the desktop icons.
        /// </summary>
        private IntPtr GetDesktopListViewHandle()
        {
            // Step 1: Get the "Progman" window.
            IntPtr progman = FindWindow("Progman", null);

            // Step 2: In modern Windows, actual desktop icons are often on a WorkerW behind Progman.
            IntPtr shellViewWin = IntPtr.Zero;
            IntPtr workerW = IntPtr.Zero;

            // Search under Progman
            shellViewWin = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (shellViewWin == IntPtr.Zero)
            {
                // If not under Progman, try WorkerW chain
                workerW = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "WorkerW", null);
                while (workerW != IntPtr.Zero && shellViewWin == IntPtr.Zero)
                {
                    shellViewWin = FindWindowEx(workerW, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (shellViewWin == IntPtr.Zero)
                    {
                        workerW = FindWindowEx(IntPtr.Zero, workerW, "WorkerW", null);
                    }
                }
            }

            if (shellViewWin == IntPtr.Zero)
                return IntPtr.Zero;

            // The ListView that holds the icons
            IntPtr listView = FindWindowEx(shellViewWin, IntPtr.Zero, "SysListView32", null);
            return listView;
        }
    }
}
