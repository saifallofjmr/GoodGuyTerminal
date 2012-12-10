using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace GG_TerminalOverlay
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(CurrentDomain_UnhandledException);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Form1());
        }

        
        static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception exception = e.ExceptionObject as Exception;
            System.IO.File.AppendAllText("output.log", exception.Message + Environment.NewLine);
            System.IO.File.AppendAllText("output.log", exception.StackTrace + Environment.NewLine);
        }

    }
}
