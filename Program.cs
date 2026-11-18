using System;
using System.Threading;
using System.Windows.Forms;
namespace MuseDesktop;

internal static class Program
{
    // Per-session names: one running instance per logon session.
    private const string MutexName = @"Local\MuseDesktop.SingleInstance";
    private const string ShowSignalName = @"Local\MuseDesktop.ShowWindow";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: false, MutexName);
        bool isFirst;
        try
        {
            isFirst = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // The previous instance died without releasing the mutex;
            // this instance owns it now and is the first live one.
            isFirst = true;
        }

        if (!isFirst)
        {
            // An instance is already running: ask it to surface its
            // window and exit, instead of opening a second window
            // whose bridge cannot bind the port anyway.
            try
            {
                using var signal = EventWaitHandle.OpenExisting(ShowSignalName);
                signal.Set();
            }
            catch { /* the other instance is exiting; nothing to do */ }
            return;
        }

        using var showSignal = new EventWaitHandle(
            initialState: false, EventResetMode.AutoReset, ShowSignalName);

        ApplicationConfiguration.Initialize();
        var form = new MainForm();

        var listener = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    showSignal.WaitOne();
                    form.BeginInvoke(new Action(form.Surface));
                }
                catch
                {
                    // The form is gone or the signal was disposed:
                    // the listener's job is over.
                    break;
                }
            }
        })
        { IsBackground = true };
        listener.Start();

        Application.Run(form);
    }
}
