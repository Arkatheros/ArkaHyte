using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ArkaHyte.Sensors;

/// <summary>
/// Tells whether a game is being played. HWiNFO's PresentMon sensor follows whatever window has the
/// focus (the desktop, an editor...), so its framerate only means "in-game FPS" while the foreground
/// window covers its whole monitor (exclusive or borderless fullscreen), or belongs to a game Windows
/// itself recognised (Game Bar list), which also covers games played in a window.
/// A recognised game is also followed until its process exits, even when another window takes the
/// focus (Alt+Tab): it drives the game view of the display.
/// </summary>
internal static partial class ForegroundGame
{
	/// <param name="FramerateCounts">The PresentMon framerate is the game's (foreground game).</param>
	/// <param name="Game">Recognised game still running, null when none.</param>
	public readonly record struct Sample(bool FramerateCounts, GameData? Game);

	// Desktop and taskbar windows also cover their monitor
	private static readonly string[] ShellClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

	// Executables Windows flagged as games, one subkey each (MatchedExeFullPath)
	private const string GameConfigStoreKey = @"System\GameConfigStore\Children";
	private static readonly TimeSpan KnownGamesLifetime = TimeSpan.FromMinutes(1);

	// Only touched by the sampling thread: the verdict is kept while the same process stays in front
	private static HashSet<string> _knownGames = new(StringComparer.OrdinalIgnoreCase);
	private static DateTime _knownGamesRead = DateTime.MinValue;
	private static uint _lastProcessId;
	private static string? _lastGamePath;

	// Game being followed: its process handle tells when it exits
	private static uint _trackedId;
	private static IntPtr _trackedProcess;
	private static GameData? _tracked;

	public static Sample Read()
	{
		var window = GetForegroundWindow();
		if (window == IntPtr.Zero || !GetWindowRect(window, out var rect) || IsShellWindow(window))
		{
			return new Sample(false, TrackedGame(foreground: false));
		}
		var processId = ForegroundProcess(window);
		var gamePath = KnownGamePath(processId);
		if (gamePath is not null)
		{
			Track(processId, gamePath, window);
		}
		return new Sample(gamePath is not null || CoversMonitor(window, rect), TrackedGame(processId == _trackedId));
	}

	private static bool IsShellWindow(IntPtr window)
	{
		var className = new StringBuilder(64);
		return GetClassName(window, className, className.Capacity) > 0 && ShellClasses.Contains(className.ToString());
	}

	private static bool CoversMonitor(IntPtr window, Rect rect)
	{
		var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
		if (!GetMonitorInfo(MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST), ref info))
		{
			return false;
		}
		var monitor = info.Monitor;
		return rect.Left <= monitor.Left && rect.Top <= monitor.Top && rect.Right >= monitor.Right && rect.Bottom >= monitor.Bottom;
	}

	private static uint ForegroundProcess(IntPtr window)
	{
		GetWindowThreadProcessId(window, out var processId);
		return processId;
	}

	/// <summary>Executable path of the process when Windows lists it as a game, null otherwise.</summary>
	private static string? KnownGamePath(uint processId)
	{
		if (processId == 0)
		{
			return null;
		}
		// A game freshly launched for the first time is added by Windows while it runs: refresh the list
		// when a new process comes to the front, at most once a minute
		var stale = DateTime.UtcNow - _knownGamesRead >= KnownGamesLifetime;
		if (processId == _lastProcessId && !stale)
		{
			return _lastGamePath;
		}
		if (stale)
		{
			_knownGames = ReadKnownGames();
			_knownGamesRead = DateTime.UtcNow;
		}
		var path = ProcessPath(processId);
		_lastProcessId = processId;
		_lastGamePath = path is not null && _knownGames.Contains(path) ? path : null;
		return _lastGamePath;
	}

	private static void Track(uint processId, string path, IntPtr window)
	{
		if (processId == _trackedId && _tracked is not null)
		{
			return;
		}
		Untrack();
		_trackedProcess = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
		if (_trackedProcess == IntPtr.Zero)
		{
			return;
		}
		_trackedId = processId;
		_tracked = new GameData(GameName(path, window), RefreshRate(window));
	}

	private static GameData? TrackedGame(bool foreground)
	{
		// Signalled handle: the process has exited
		if (_tracked is not null && WaitForSingleObject(_trackedProcess, 0) == WAIT_OBJECT_0)
		{
			Untrack();
		}
		return _tracked is null || _tracked.Foreground == foreground ? _tracked : _tracked = _tracked with { Foreground = foreground };
	}

	private static void Untrack()
	{
		if (_trackedProcess != IntPtr.Zero)
		{
			CloseHandle(_trackedProcess);
		}
		_trackedProcess = IntPtr.Zero;
		_trackedId = 0;
		_tracked = null;
	}

	/// <summary>
	/// Name shown on the display: Steam's install folder is the store name ("ELDEN RING"), executables
	/// often are not ("FactoryGameSteam-Win64-Shipping"); the window title comes next.
	/// </summary>
	private static string GameName(string path, IntPtr window)
	{
		var steam = SteamFolder().Match(path);
		if (steam.Success)
		{
			return steam.Groups[1].Value;
		}
		var title = new StringBuilder(256);
		if (GetWindowText(window, title, title.Capacity) > 0)
		{
			var cleaned = Spaces().Replace(TradeMarks().Replace(title.ToString(), " "), " ").Trim();
			if (cleaned.Length > 0)
			{
				return cleaned;
			}
		}
		return Path.GetFileNameWithoutExtension(path);
	}

	/// <summary>Refresh rate of the monitor showing the window, the top of the FPS scale.</summary>
	private static int? RefreshRate(IntPtr window)
	{
		var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
		if (!GetMonitorInfo(MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST), ref info))
		{
			return null;
		}
		var mode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
		// 0 and 1 mean "hardware default"
		return EnumDisplaySettings(info.Device, ENUM_CURRENT_SETTINGS, ref mode) && mode.DisplayFrequency > 1
			? (int)mode.DisplayFrequency
			: null;
	}

	private static HashSet<string> ReadKnownGames()
	{
		var games = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		try
		{
			using var children = Registry.CurrentUser.OpenSubKey(GameConfigStoreKey);
			if (children is null)
			{
				return games;
			}
			foreach (var name in children.GetSubKeyNames())
			{
				using var child = children.OpenSubKey(name);
				if (child?.GetValue("MatchedExeFullPath") is string path && path.Length > 0)
				{
					games.Add(path);
				}
			}
		}
		catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
		{
			// Unreadable list: fullscreen detection still works
		}
		return games;
	}

	private static string? ProcessPath(uint processId)
	{
		var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
		if (process == IntPtr.Zero)
		{
			return null;
		}
		try
		{
			var buffer = new StringBuilder(1024);
			var size = buffer.Capacity;
			return QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
		}
		finally
		{
			CloseHandle(process);
		}
	}

	[GeneratedRegex(@"\\steamapps\\common\\([^\\]+)\\", RegexOptions.IgnoreCase)]
	private static partial Regex SteamFolder();

	[GeneratedRegex(@"\((TM|R)\)|™|®", RegexOptions.IgnoreCase)]
	private static partial Regex TradeMarks();

	[GeneratedRegex(@"\s+")]
	private static partial Regex Spaces();

	private const uint MONITOR_DEFAULTTONEAREST = 2;
	private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
	private const uint SYNCHRONIZE = 0x00100000;
	private const uint WAIT_OBJECT_0 = 0;
	private const int ENUM_CURRENT_SETTINGS = -1;

	[StructLayout(LayoutKind.Sequential)]
	private struct Rect
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MonitorInfo
	{
		public int Size;
		public Rect Monitor;
		public Rect WorkArea;
		public uint Flags;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct MonitorInfoEx
	{
		public int Size;
		public Rect Monitor;
		public Rect WorkArea;
		public uint Flags;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
		public string Device;
	}

	// DEVMODEW, display variant of the union
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct DevMode
	{
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
		public string DeviceName;
		public ushort SpecVersion;
		public ushort DriverVersion;
		public ushort Size;
		public ushort DriverExtra;
		public uint Fields;
		public int PositionX;
		public int PositionY;
		public uint DisplayOrientation;
		public uint DisplayFixedOutput;
		public short Color;
		public short Duplex;
		public short YResolution;
		public short TTOption;
		public short Collate;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
		public string FormName;
		public ushort LogPixels;
		public uint BitsPerPel;
		public uint PelsWidth;
		public uint PelsHeight;
		public uint DisplayFlags;
		public uint DisplayFrequency;
		public uint IcmMethod;
		public uint IcmIntent;
		public uint MediaType;
		public uint DitherType;
		public uint Reserved1;
		public uint Reserved2;
		public uint PanningWidth;
		public uint PanningHeight;
	}

	[DllImport("user32.dll")]
	private static extern IntPtr GetForegroundWindow();

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetWindowRect(IntPtr window, out Rect rect);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

	[DllImport("kernel32.dll")]
	private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

	[DllImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(IntPtr handle);

	[DllImport("user32.dll")]
	private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool EnumDisplaySettings(string deviceName, int modeNumber, ref DevMode mode);
}
