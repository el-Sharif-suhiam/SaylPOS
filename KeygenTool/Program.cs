using System;
using System.Windows.Forms;

namespace PosLicensing.KeygenTool
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new KeygenForm());
        }
    }
}
