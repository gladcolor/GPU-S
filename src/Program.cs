using System;
using System.Windows.Forms;

namespace GpuS
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // Child-process mode: probe DirectX / Vulkan / OpenCL, print results, exit.
            if (args.Length > 0 && args[0] == GpuDetector.ProbeArg)
            {
                try { GpuDetector.RunProbe(Console.Out); } catch (Exception) { }
                return 0;
            }

            Lang.Init(Settings.Load().Language);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Command-line logging: no window, tray icon only.
            if (Array.Exists(args, a => a.Equals("--log", StringComparison.OrdinalIgnoreCase)))
                return CliLogger.Run(args);

            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                MessageBox.Show(e.Exception.Message, MainForm.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            var form = new MainForm();
            string[] tabs = { "--monitor", "--processes", "--details", "--sensors", "--about" };
            foreach (string a in args)
            {
                int i = Array.FindIndex(tabs, t => t.Equals(a, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) form.StartTab = i;
                if (a.Equals("--all-gpus", StringComparison.OrdinalIgnoreCase)) { form.StartTab = 0; form.StartAllGpus = true; }
            }
            Application.Run(form);
            return 0;
        }
    }
}
