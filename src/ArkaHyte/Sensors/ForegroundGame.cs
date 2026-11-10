using System.Runtime.InteropServices;
using System.Text;

namespace ArkaHyte.Sensors;

/// <summary>
/// Tells whether a game is likely in the foreground: HWiNFO's PresentMon sensor follows whatever window
/// has the focus (the desktop, an editor...), so its framerate only means "in-game FPS" while the
/// foreground window covers its whole monitor (exclusive or borderless fullscreen).
/// </summary>
internal static class ForegroundGame
{
	// Desktop and taskbar windows also cover their monitor
	private static readonly string[] ShellClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

	public static bool IsRunning()
	{
		var window = GetForegroundWindow();
		if (window == IntPtr.Zero || !GetWindowRect(window, out var rect))
		{
			return false;
		}
		var className = new StringBuilder(64);
		if (GetClassName(window, className, className.Capacity) > 0 && ShellClasses.Contains(className.ToString()))
		{
			return false;
		}
		var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
		if (!GetMonitorInfo(MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST), ref info))
		{
			return false;
		}
		var monitor = info.Monitor;
		return rect.Left <= monitor.Left && rect.Top <= monitor.Top && rect.Right >= monitor.Right && rect.Bottom >= monitor.Bottom;
	}

	private const uint MONITOR_DEFAULTTONEAREST = 2;

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

	[DllImport("user32.dll")]
	private static extern IntPtr GetForegroundWindow();

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetWindowRect(IntPtr window, out Rect rect);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

	[DllImport("user32.dll")]
	private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
