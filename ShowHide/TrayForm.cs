using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Drawing;

namespace ShowHide
{
    public class TrayForm : Form
    {
        // ===== Hotkey stuff =====
        private const int HOTKEY_ID = 0x1234;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const int VK_D = 0x44;

        // ===== Desktop show/hide =====
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        // ===== Mouse hook stuff =====
        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;

        private static IntPtr _mouseHook = IntPtr.Zero;
        private static LowLevelMouseProc _mouseProc;   // keep delegate alive

        private static IntPtr _desktopListView = IntPtr.Zero;
        private static uint _doubleClickTime = 0;
        private static uint _lastClickTime = 0;
        private static POINT _lastClickPoint;

        // consider clicks within this distance to be the "same spot"
        private const int DOUBLE_CLICK_MAX_DISTANCE = 4; // pixels

        // Tray UI
        private NotifyIcon _trayIcon;
        private ContextMenuStrip _menu;

        // ==== Win32 interop ====

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string lclassName, string windowTitle);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern uint GetDoubleClickTime();

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT Point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        // structs / delegates

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        // ===== Form logic =====

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
                MessageBox.Show("Could not register hotkey Ctrl+Alt+D.", "ShowHide",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            // Setup mouse hook for desktop double-click
            _mouseProc = MouseHookCallback;
            IntPtr hModule = GetModuleHandle(null);
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hModule, 0);

            if (_mouseHook == IntPtr.Zero)
            {
                MessageBox.Show("Could not install mouse hook.", "ShowHide",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            // Get double-click time from system
            _doubleClickTime = GetDoubleClickTime();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Unregister hotkey
            UnregisterHotKey(Handle, HOTKEY_ID);

            // Unhook mouse
            if (_mouseHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }

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

        // ===== Mouse hook callback =====

        private static IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam.ToInt32() == WM_LBUTTONDOWN)
            {
                MSLLHOOKSTRUCT data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                HandlePossibleDoubleClick(data);
            }

            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        private static void HandlePossibleDoubleClick(MSLLHOOKSTRUCT data)
        {
            if (_doubleClickTime == 0)
                _doubleClickTime = GetDoubleClickTime();

            uint now = data.time;
            POINT currentPoint = data.pt;

            bool isWithinTime = (now - _lastClickTime) <= _doubleClickTime;
            bool isWithinDistance = DistanceSquared(currentPoint, _lastClickPoint)
                                    <= DOUBLE_CLICK_MAX_DISTANCE * DOUBLE_CLICK_MAX_DISTANCE;

            if (isWithinTime && isWithinDistance && IsClickOnDesktop(currentPoint))
            {
                // It's a double-click on the desktop → toggle icons
                ToggleIconsStatic();
                // Reset to avoid triple-click toggling twice
                _lastClickTime = 0;
            }
            else
            {
                // Store current click as the last click
                _lastClickTime = now;
                _lastClickPoint = currentPoint;
            }
        }

        private static int DistanceSquared(POINT a, POINT b)
        {
            int dx = a.X - b.X;
            int dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }

        private static bool IsClickOnDesktop(POINT pt)
        {
            if (_desktopListView == IntPtr.Zero)
            {
                _desktopListView = GetDesktopListViewHandle();
                if (_desktopListView == IntPtr.Zero)
                    return false;
            }

            IntPtr hwndAtPoint = WindowFromPoint(pt);
            if (hwndAtPoint == IntPtr.Zero)
                return false;

            // Walk up the parent chain to see if we land on the desktop list view
            IntPtr current = hwndAtPoint;
            while (current != IntPtr.Zero)
            {
                if (current == _desktopListView)
                    return true;

                current = GetParent(current);
            }

            return false;
        }

        // Static entry so the hook can call it
        private static void ToggleIconsStatic()
        {
            // We need an instance method to interact with UI (MessageBox etc.)
            // but the core toggling is static-friendly.
            IntPtr lv = _desktopListView;
            if (lv == IntPtr.Zero)
            {
                lv = GetDesktopListViewHandle();
                _desktopListView = lv;
            }

            if (lv == IntPtr.Zero)
                return;

            bool visible = IsWindowVisible(lv);
            ShowWindow(lv, visible ? SW_HIDE : SW_SHOW);
        }

        // Instance helper used by tray & hotkey
        private void ToggleDesktopIcons()
        {
            IntPtr desktopListView = GetDesktopListViewHandle();
            _desktopListView = desktopListView; // cache for hook

            if (desktopListView == IntPtr.Zero)
            {
                MessageBox.Show("Could not find desktop icons window.", "ShowHide",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            bool visible = IsWindowVisible(desktopListView);
            ShowWindow(desktopListView, visible ? SW_HIDE : SW_SHOW);
        }

        /// <summary>
        /// Gets the handle of the SysListView32 that actually hosts the desktop icons.
        /// </summary>
        private static IntPtr GetDesktopListViewHandle()
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
