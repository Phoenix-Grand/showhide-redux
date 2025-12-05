using System;
using System.Windows.Forms;

namespace ShowHide
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayForm());
        }
    }
}
