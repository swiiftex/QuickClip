using System.IO.Pipes;
using System.Text;
using Microsoft.Win32;

namespace QuickClip.Shell;

/// <summary>"Start with Windows": a per-user Run entry that launches QuickClip into the tray.</summary>
internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "QuickClip";

    private static string Command => $"\"{Environment.ProcessPath}\" --tray";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string cmd && cmd.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>Keeps one QuickClip per user: later launches hand their arguments to the running one.</summary>
internal static class SingleInstance
{
    private static readonly string PipeName = "QuickClip.Instance." + Environment.UserName;
    private static Mutex? _mutex;

    public static bool TryBecomePrimary()
    {
        _mutex = new Mutex(true, @"Local\QuickClip.Instance", out bool created);
        return created;
    }

    public static bool SendToPrimary(string[] args)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(3000);
            var bytes = Encoding.UTF8.GetBytes(string.Join("\n", args.Length == 0 ? ["--show"] : args));
            pipe.Write(bytes);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Handing over to the running QuickClip failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>Calls <paramref name="onArgs"/> (on a worker thread) with each later launch's arguments.</summary>
    public static void Listen(Action<string[]> onArgs)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    string text = await reader.ReadToEndAsync();
                    onArgs(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                }
                catch (Exception ex)
                {
                    Log.Write("Instance pipe error: " + ex.Message);
                    await Task.Delay(1000);
                }
            }
        });
    }
}
