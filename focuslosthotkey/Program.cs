using System.Diagnostics;
using System.Runtime.InteropServices;

class FocusLostHotkeyProgram
{
    // =======================
    // App Info
    // =======================

    const string APP_NAME = "Focus Lost Hotkey Helper";
    const string APP_VERSION = "v1.1"; // Only update on publish-worthy changes
    const string APP_URL = "https://citizen-history.com";
    const string INI_FILE = "focuslosthotkey.ini";

    // =======================
    // Config (loaded from INI)
    // =======================

    static string TargetProcessName = null!;
    static int ProcessRescanDelayMs;

    static ushort ScanCodeModifier;
    static ushort ScanCodeKey;

    static int DelayAfterFocusLostMs;
    static int DelayAfterFocusGainedMs;
    static int DelayAfterInputMs;
    static int FocusDebounceMs;

    // =======================
    // Win32 constants
    // =======================

    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const uint KEYEVENTF_SCANCODE = 0x0008;

    // =======================
    // Program state
    // =======================

    static IntPtr targetWindow = IntPtr.Zero;
    static bool targetFocused = false;
    static bool running = true;
    static long lastFocusEventTicks = 0;
    static long lastProcessCheckTicks = 0;
    static WinEventHook? hook;

    static readonly char[] Spinner = { '|', '/', '-', '\\' };

    // =======================
    // Entry
    // =======================

    static void Main()
    {
        Console.Clear();
        PrintHeader();

        if (!LoadAndValidateIni())
        {
            Log("Configuration error. Exiting.", ConsoleColor.Red);
            Environment.Exit(1);
        }

        PrintConfigSummary();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            running = false;
        };

        WaitForTargetProcess();

        hook = new WinEventHook(WinEventHook.WinEventHookType.EVENT_SYSTEM_FOREGROUND);

        Log("Monitoring focus changes (Ctrl+C to exit)", ConsoleColor.Cyan);

        while (running)
        {
            Thread.Sleep(100);

            CheckProcessAlive();

            if (targetWindow == IntPtr.Zero)
                continue;

            bool isFocusedNow = GetForegroundWindow() == targetWindow;

            if (targetFocused && !isFocusedNow)
            {
                if (!ShouldDebounce())
                {
                    Log("Focus lost   -> Hotkey triggered", ConsoleColor.Yellow);
                    SendConfiguredHotkey(DelayAfterFocusLostMs);
                }
            }
            else if (!targetFocused && isFocusedNow)
            {
                if (!ShouldDebounce())
                {
                    Log("Focus gained -> Hotkey triggered", ConsoleColor.Green);
                    SendConfiguredHotkey(DelayAfterFocusGainedMs);
                }
            }

            targetFocused = isFocusedNow;
        }

        hook?.Dispose();
        Log("Exited cleanly.", ConsoleColor.DarkGray);
    }

    // =======================
    // INI handling (required)
    // =======================

    static bool LoadAndValidateIni()
    {
        if (!File.Exists(INI_FILE))
        {
            Log($"Missing required config file: {INI_FILE}", ConsoleColor.Red);
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in File.ReadAllLines(INI_FILE))
        {
            var line = raw.Split('#')[0].Trim();
            if (string.IsNullOrWhiteSpace(line) || !line.Contains("="))
                continue;

            var parts = line.Split('=', 2);
            values[parts[0].Trim()] = parts[1].Trim();
        }

        try
        {
            TargetProcessName = Require(values, "ProcessName");
            ProcessRescanDelayMs = RequireInt(values, "ProcessRescanDelayMs", min: 1000);

            ScanCodeModifier = RequireUShort(values, "ScanCodeModifier");
            ScanCodeKey = RequireUShort(values, "ScanCodeKey");

            DelayAfterFocusLostMs = RequireInt(values, "DelayAfterFocusLostMs", min: 0);
            DelayAfterFocusGainedMs = RequireInt(values, "DelayAfterFocusGainedMs", min: 0);
            DelayAfterInputMs = RequireInt(values, "DelayAfterInputMs", min: 0);
            FocusDebounceMs = RequireInt(values, "FocusDebounceMs", min: 100);
        }
        catch (Exception ex)
        {
            Log(ex.Message, ConsoleColor.Red);
            return false;
        }

        return true;
    }

    static string Require(Dictionary<string, string> d, string key)
    {
        if (!d.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v))
            throw new Exception($"Missing required config key: {key}");
        return v;
    }

    static int RequireInt(Dictionary<string, string> d, string key, int min)
    {
        if (!d.TryGetValue(key, out var v) || !int.TryParse(v, out int i) || i < min)
            throw new Exception($"Invalid or missing integer config key: {key}");
        return i;
    }

    static ushort RequireUShort(Dictionary<string, string> d, string key)
    {
        if (!d.TryGetValue(key, out var v))
            throw new Exception($"Missing config key: {key}");

        try
        {
            return v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToUInt16(v, 16)
                : Convert.ToUInt16(v);
        }
        catch
        {
            throw new Exception($"Invalid ushort value for config key: {key}");
        }
    }

    // =======================
    // Debounce
    // =======================

    static bool ShouldDebounce()
    {
        long now = Environment.TickCount64;
        if (now - lastFocusEventTicks < FocusDebounceMs)
            return true;

        lastFocusEventTicks = now;
        return false;
    }

    // =======================
    // Process monitoring
    // =======================

    static void CheckProcessAlive()
    {
        long now = Environment.TickCount64;
        if (now - lastProcessCheckTicks < ProcessRescanDelayMs)
            return;

        lastProcessCheckTicks = now;

        if (targetWindow != IntPtr.Zero && IsWindow(targetWindow))
            return;

        targetWindow = IntPtr.Zero;
        targetFocused = false;

        Log("Target process not found, waiting for restart...", ConsoleColor.DarkYellow);
        WaitForTargetProcess();
    }

    static void WaitForTargetProcess()
    {
        Log($"Waiting for {TargetProcessName}.exe ...", ConsoleColor.Gray);

        int i = 0;
        while (targetWindow == IntPtr.Zero && running)
        {
            targetWindow = FindWindowByProcessName(TargetProcessName);
            Console.Write($"\r {Spinner[i++ % Spinner.Length]} waiting... ");
            Thread.Sleep(ProcessRescanDelayMs);
        }

        Console.WriteLine();
        Log($"Window handle: 0x{targetWindow:X}", ConsoleColor.DarkGray);
    }

    static IntPtr FindWindowByProcessName(string name)
    {
        var procs = Process.GetProcessesByName(name);
        return procs.Length > 0 ? procs[0].MainWindowHandle : IntPtr.Zero;
    }

    // =======================
    // Input injection
    // =======================

    static void SendConfiguredHotkey(int focusDelayMs)
    {
        if (!IsWindow(targetWindow))
            return;

        IntPtr previousWindow = GetForegroundWindow();

        SetForegroundWindow(targetWindow);
        Thread.Sleep(focusDelayMs);

        INPUT[] inputs = ScanCodeModifier != 0
            ? new[]
            {
                KeyboardInput(ScanCodeModifier, false),
                KeyboardInput(ScanCodeKey, false),
                KeyboardInput(ScanCodeKey, true),
                KeyboardInput(ScanCodeModifier, true)
            }
            : new[]
            {
                KeyboardInput(ScanCodeKey, false),
                KeyboardInput(ScanCodeKey, true)
            };

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        Thread.Sleep(DelayAfterInputMs);

        if (previousWindow != IntPtr.Zero && IsWindow(previousWindow))
            SetForegroundWindow(previousWindow);
    }

    static INPUT KeyboardInput(ushort scanCode, bool keyUp)
    {
        INPUT i = new INPUT();
        i.type = INPUT_KEYBOARD;

        i.data.mouse = new MOUSEINPUT();
        i.data.hardware = new HARDWAREINPUT();

        i.data.keyboard.wVk = 0;
        i.data.keyboard.wScan = scanCode;
        i.data.keyboard.dwFlags = KEYEVENTF_SCANCODE | (keyUp ? KEYEVENTF_KEYUP : 0);
        i.data.keyboard.dwTime = 0;
        i.data.keyboard.dwExtraInfo = IntPtr.Zero;

        return i;
    }

    // =======================
    // UI
    // =======================

    static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
 _____                 __            _   _____         
|   __|___ ___ _ _ ___|  |   ___ ___| |_|  |  |___ _ _ 
|   __| . |  _| | |_ -|  |__| . |_ -|  _|    -| -_| | |
|__|  |___|___|___|___|_____|___|___|_| |__|__|___|_  |
                                                  |___|
");
        Console.WriteLine($" {APP_NAME} {APP_VERSION}");
        Console.WriteLine($" {APP_URL}");
        Console.WriteLine("================================================");
        Console.ResetColor();
    }

    static void PrintConfigSummary()
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(
            $" Process  : {TargetProcessName}.exe\n" +
            $" Hotkey   : {(ScanCodeModifier != 0 ? $"0x{ScanCodeModifier:X} + " : "")}0x{ScanCodeKey:X}\n" +
            $" Delays   : lost={DelayAfterFocusLostMs}ms | gained={DelayAfterFocusGainedMs}ms | input={DelayAfterInputMs}ms\n" +
            $" Debounce : {FocusDebounceMs}ms\n"
        );
        Console.ResetColor();
    }

    static void Log(string msg, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
        Console.ResetColor();
    }

    // =======================
    // Win32
    // =======================

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // =======================
    // Structures
    // =======================

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion data; }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mouse;
        [FieldOffset(0)] public KEYBDINPUT keyboard;
        [FieldOffset(0)] public HARDWAREINPUT hardware;
    }

#pragma warning disable CS0649
    struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, dwTime;
        public IntPtr dwExtraInfo;
    }

    struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, dwTime;
        public IntPtr dwExtraInfo;
    }

    struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL, wParamH;
    }
#pragma warning restore CS0649
}

// =======================================================
// WinEventHook
// =======================================================

class WinEventHook : IDisposable
{
    private IntPtr hookHandle;
    private WinEventDelegate callback;

    public WinEventHook(WinEventHookType eventType)
    {
        callback = WinEventProc;
        hookHandle = SetWinEventHook(
            (uint)eventType, 0, IntPtr.Zero,
            callback, 0, 0,
            (uint)SetWinEventHookFlags.EVENT_HOOK_OUT_OF_CONTEXT);
    }

    public void Dispose()
    {
        if (hookHandle != IntPtr.Zero)
            UnhookWinEvent(hookHandle);
    }

    private static void WinEventProc(
        IntPtr h, uint e, IntPtr hwnd, int o, int c, uint t, uint ms)
    { }

    private delegate void WinEventDelegate(
        IntPtr h, uint e, IntPtr hwnd, int o, int c, uint t, uint ms);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint a, uint b, IntPtr c, WinEventDelegate d, uint e, uint f, uint g);

    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr h);

    public enum WinEventHookType : uint { EVENT_SYSTEM_FOREGROUND = 3 }
    public enum SetWinEventHookFlags : uint { EVENT_HOOK_OUT_OF_CONTEXT = 0 }
}
