using System;
using System.Windows.Forms;

namespace Gmix.App
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new MainForm(AppEnvironment.Detect()));
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动失败：" + ex.Message, "GM-IX", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
