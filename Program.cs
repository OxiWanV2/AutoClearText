using System;
using System.Threading;
using System.Windows.Forms;

namespace AutoClearText
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            using var mutex = new Mutex(true, "AutoClearText_SingleInstance", out bool isNewInstance);

            if (!isNewInstance)
            {
                MessageBox.Show(
                    "AutoClearText est déjà en cours d'exécution.\nVérifiez l'icône dans la barre des tâches.",
                    "AutoClearText",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new CorrectorContext());
        }
    }
}