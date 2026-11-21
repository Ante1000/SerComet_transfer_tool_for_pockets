using System;
using System.Threading;
using System.Windows.Forms;

namespace UartSlTest
{
    static class Program
    {
        // Only one instance of SerComet may run: a second one would fight with the first over the COM port.
        // Global\ makes the lock work across user sessions too (a COM port belongs to the whole machine).
        const string MutexName = @"Global\SerComet_transfer_tool_for_pockets";

        [STAThread]
        static void Main()
        {
            bool created;
            Mutex mutex;
            try { mutex = new Mutex(true, MutexName, out created); }
            catch (UnauthorizedAccessException) { mutex = null; created = false; }   // owned by another user's session
            if (!created)
            {
                MessageBox.Show("SerComet is already running. Only one instance can be started.",
                    "SerComet", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new Form1());
            }
            finally { mutex.ReleaseMutex(); mutex.Dispose(); }
        }
    }
}
