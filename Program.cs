using System.Runtime.InteropServices;

// ---------------------------------------------------------------------
// KeepAwakeService (v2)
//
// v1 used a polling loop (Thread.Sleep + clock check). That failed:
// Modern Standby can freeze an ordinary background process once Windows
// decides nothing needs to run, and the process was only holding
// ES_SYSTEM_REQUIRED *inside* the window -- so it had no protection
// beforehand and never woke itself to notice the window had arrived.
//
// v2 uses a waitable timer created with the "resume" flag. That flag is
// handled by the kernel power manager directly: it wakes the machine
// (and un-freezes this process) at the due time regardless of whether
// the process itself was suspended. This is the standard Win32
// mechanism for reliable wake alarms.
// ---------------------------------------------------------------------


// dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

// outputs in:  D:\repos\KeepAwakeService\bin\Release\net8.0-windows\win-x64\publish

internal static class Program
{
    // Default windows used only if no command-line times are supplied.
    private static readonly (TimeSpan Start, TimeSpan End)[] DefaultWindows =
    {
        (new TimeSpan(7, 45, 0), new TimeSpan(12, 0, 0)),
    };

    private static (TimeSpan Start, TimeSpan End)[] Windows = DefaultWindows;

    private static readonly TimeSpan HoldDuration = TimeSpan.FromMinutes(15);

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KeepAwakeService", "keepawake.log");

    // --- Win32 ---------------------------------------------------------
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerEx(IntPtr lpTimerAttributes, string? lpTimerName,
        uint dwFlags, uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(IntPtr hTimer, ref long pDueTime, int lPeriod,
        IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine, bool fResume);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(EXECUTION_STATE esFlags);

    [Flags]
    private enum EXECUTION_STATE : uint
    {
        ES_CONTINUOUS = 0x80000000,
        ES_SYSTEM_REQUIRED = 0x00000001,
    }

    private const uint CREATE_WAITABLE_TIMER_MANUAL_RESET = 0x00000001;
    private const uint TIMER_ALL_ACCESS = 0x1F0003;
    private const uint INFINITE = 0xFFFFFFFF;

    private static void Main(string[] args)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);

        if( args.Length > 0 )
        {
            var parsed = ParseWindowArgs(args);
            if( parsed.Length > 0 )
                Windows = parsed;
        }

        Log("KeepAwakeService v2 started (resumable waitable timer).");
        Log($"Windows: {string.Join(", ", Windows.Select(w => w.Start.ToString(@"hh\:mm") + "-" + w.End.ToString(@"hh\:mm")))}");

        IntPtr timer = CreateWaitableTimerEx(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_MANUAL_RESET, TIMER_ALL_ACCESS);
        if( timer == IntPtr.Zero )
        {
            Log($"FATAL: CreateWaitableTimerEx failed, error {Marshal.GetLastWin32Error()}");
            return;
        }

        while( true )
        {
            var (next, holdUntil) = NextOccurrence(DateTime.Now);
            ArmTimer(timer, next);
            Log($"Timer armed for {next:yyyy-MM-dd HH:mm:ss} (resume=true), hold until {holdUntil:HH:mm}.");

            WaitForSingleObject(timer, INFINITE);

            Log("Timer fired - holding system awake.");
            SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED);

            var remaining = holdUntil - DateTime.Now;
            if( remaining > TimeSpan.Zero )
                Thread.Sleep(remaining);

            SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
            Log("Hold released - normal idle timeout resumes.");
        }
    }

    private static readonly string[] AcceptedFormats = { @"h\:mm\:ss", @"hh\:mm\:ss", @"h\:mm", @"hh\:mm" };

    private static (TimeSpan Start, TimeSpan End)[] ParseWindowArgs(string[] args)
    {
        var results = new List<(TimeSpan Start, TimeSpan End)>();
        var ci = System.Globalization.CultureInfo.InvariantCulture;

        foreach( var arg in args )
        {
            var parts = arg.Split('-');
            if( parts.Length > 2 || !TimeSpan.TryParseExact(parts[0], AcceptedFormats, ci, out var start) )
            {
                Log($"Ignoring unparseable argument: '{arg}' (expected H:mm or H:mm-H:mm).");
                continue;
            }

            var end = start + HoldDuration;
            if( parts.Length == 2 && !TimeSpan.TryParseExact(parts[1], AcceptedFormats, ci, out end) )
            {
                Log($"Ignoring unparseable argument: '{arg}' (expected H:mm or H:mm-H:mm).");
                continue;
            }

            results.Add((start, end));
        }
        return results.OrderBy(w => w.Start).ToArray();
    }

    private static (DateTime Start, DateTime End) NextOccurrence(DateTime now)
    {
        return Windows
            .Select(w =>
            {
                var start = now.Date + w.Start;
                if( start <= now ) start = start.AddDays(1);
                var end = start.Date + w.End;
                if( end <= start ) end = end.AddDays(1);
                return (Start: start, End: end);
            })
            .OrderBy(x => x.Start)
            .First();
    }

    private static void ArmTimer(IntPtr timer, DateTime dueTime)
    {
        // SetWaitableTimer's dueTime: negative = relative (100-ns units),
        // positive = absolute FILETIME. We pass an absolute FILETIME.
        long fileTime = dueTime.ToFileTime();
        if( !SetWaitableTimer(timer, ref fileTime, 0, IntPtr.Zero, IntPtr.Zero, fResume: true) )
        {
            Log($"SetWaitableTimer failed, error {Marshal.GetLastWin32Error()}");
        }
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch
        {
            // best-effort logging only
        }
    }
}
