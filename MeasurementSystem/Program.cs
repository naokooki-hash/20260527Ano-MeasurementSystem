using System;
using System.Windows.Forms;

namespace MeasurementSystem
{
    static class Program
    {
        [STAThread] // メソッドの直上
        static void Main()
        {
            AppContext.SetSwitch("System.Drawing.EnableUnixSupport", true);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Form1());
        }
    }
}