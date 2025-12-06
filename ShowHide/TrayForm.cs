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

        // ===== Mouse polling stuff =====
        private const int VK_LBUTTON = 0x01;
        private const int DOUBLE_CLICK_MAX_DISTANCE = 8; // pixels (a bit generous)

        private Timer _mouseTimer;
        private bool _wasLeftDown = false;
        private int _lastClickTime = 0;
        private POINT _lastClickPoint;

        private static uint _doubleClickTime = 0;
        private static IntPtr _desktopListView = IntPtr.Zero;

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

        [DllImport("user32.dll")]
        private static extern uint GetDoubleClickTime();

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        // NEW: needed to know what window is under the cursor
        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT Point);

        // NEW: convert screen -> client coordinates for the listview
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        // NEW: SendMessage for listview hit testing
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, ref LVHITTESTINFO lParam);

        // ListView messages
        private const uint LVM_FIRST = 0x1000;
        private const uint LVM_HITTEST = LVM_FIRST + 18;

        // structs

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        // NEW: for LVM_HITTEST
        [StructLayout(LayoutKind.Sequential)]
        private struct LVHITTESTINFO
        {
            public POINT pt;
            public uint flags;
            public int iItem;
            public int iSubItem;
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

            // Mouse polling timer
            _mouseTimer = new Timer
            {
                Interval = 20 // ms
            };
            _mouseTimer.Tick += MouseTimer_Tick;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // Get desktop listview right away
            _desktopListView = GetDesktopListViewHandle();

            // Register Ctrl+Alt+D as the toggle hotkey
            bool ok = RegisterHotKey(Handle, HOTKEY_ID, MOD_CONTROL | MOD_ALT, VK_D);
            if (!ok)
            {
                MessageBox.Show("Could not register hotkey Ctrl+Alt+D.", "ShowHide",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            // Get double-click time from system
            _doubleClickTime = GetDoubleClickTime();
            if (_doubleClickTime == 0)
                _doubleClickTime = 500; // fallback

            // Start polling mouse
            _mouseTimer.Start();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Unregister hotkey
            UnregisterHotKey(Handle, HOTKEY_ID);

            // Stop timer
            if (_mouseTimer != null)
            {
                _mouseTimer.Stop();
                _mouseTimer.Tick -= MouseTimer_Tick;
                _mouseTimer.Dispose();
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
            const int WM_HOTKEY_MSG = 0x0312;

            if (m.Msg == WM_HOTKEY_MSG && m.WParam.ToInt32() == HOTKEY_ID)
            {
                ToggleDesktopIcons();
            }

            base.WndProc(ref m);
        }

        // ===== Mouse polling logic =====

        private void MouseTimer_Tick(object sender, EventArgs e)
        {
            // High bit of GetAsyncKeyState means key currently down
            bool isDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;

            if (isDown && !_wasLeftDown)
            {
                // This is a new left-click press
                OnLeftClick();
            }

            _wasLeftDown = isDown;
        }

        private void OnLeftClick()
        {
            // Get current cursor position
            if (!GetCursorPos(out POINT ptScreen))
                return;

            int now = Environment.TickCount;
            bool isWithinTime = (now - _lastClickTime) >= 0 &&
                                (now - _lastClickTime) <= _doubleClickTime;
            bool isWithinDistance = DistanceSquared(ptScreen, _lastClickPoint)
                                    <= DOUBLE_CLICK_MAX_DISTANCE * DOUBLE_CLICK_MAX_DISTANCE;

            if (isWithinTime && isWithinDistance)
            {
                // NEW: Only toggle if it's a double-click on EMPTY desktop background
                if (IsDoubleClickOnEmptyDesktop(ptScreen))
                {
                    ToggleIconsStatic();
                }
                _lastClickTime = 0; // reset (regardless)
            }
            else
            {
                _lastClickTime = now;
                _lastClickPoint = ptScreen;
            }
        }

        private static int DistanceSquared(POINT a, POINT b)
        {
            int dx = a.X - b.X;
            int dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }

        // NEW: check that the double-click is on the desktop listview AND not on an item
        private static bool IsDoubleClickOnEmptyDesktop(POINT screenPoint)
        {
            // Ensure we have the desktop listview
            IntPtr lv = _desktopListView;
            if (lv == IntPtr.Zero)
            {
                lv = GetDesktopListViewHandle();
                _desktopListView = lv;
            }

            if (lv == IntPtr.Zero)
                return false;

            // Confirm the window under the cursor is the desktop listview
            IntPtr hwndAtPoint = WindowFromPoint(screenPoint);
            if (hwndAtPoint != lv)
            {
                // Not even on the desktop listview → ignore
                return false;
            }

            // Convert to listview client coordinates
            POINT ptClient = screenPoint;
            if (!ScreenToClient(lv, ref ptClient))
                return false;

            // Hit-test the listview to see if there's an item under the cursor
            LVHITTESTINFO ht = new LVHITTESTINFO
            {
                pt = ptClient,
                flags = 0,
                iItem = -1,
                iSubItem = 0
            };

            SendMessage(lv, LVM_HITTEST, IntPtr.Zero, ref ht);

            // iItem == -1 -> no icon under cursor = empty background
            return ht.iItem == -1;
        }

        // Static entry for polling logic
        private static void ToggleIconsStatic()
        {
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
            _desktopListView = desktopListView; // cache

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
