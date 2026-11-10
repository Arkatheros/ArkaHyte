using System.Runtime.InteropServices;
// Thread-pool timers: the WinForms Timer would need a message loop on the UI thread
using ThreadingTimer = System.Threading.Timer;

namespace ArkaHyte;

/// <summary>
/// Sends the cursor back off the case display after each touch.
/// Windows promotes touch to mouse input and parks the cursor at the touch point, so after a tap
/// the mouse would start from the case display. Detection relies on the monitor under the click,
/// not on the touch signature, which some digitizer drivers do not set.
/// The low-level hook runs on its own thread: Windows waits for the hook on every mouse event,
/// so it must never share a thread with UI work.
/// </summary>
public sealed class TouchGuard : IDisposable
{
	private const int WH_MOUSE_LL = 14;
	private const int WM_MOUSEMOVE = 0x0200;
	private const int WM_LBUTTONDOWN = 0x0201;
	private const int WM_LBUTTONUP = 0x0202;
	private const int WM_RBUTTONDOWN = 0x0204;
	private const int WM_RBUTTONUP = 0x0205;
	private const int WM_QUIT = 0x0012;
	private const uint MONITOR_DEFAULTTONEAREST = 2;
	private const uint MONITOR_DEFAULTTOPRIMARY = 1;
	private const int VK_LBUTTON = 0x01;
	private const int VK_RBUTTON = 0x02;

	// Let the promoted click reach the page before the cursor moves away
	private const int RestoreDelayMs = 80;

	// Safety net in case touch input never reaches the hook: an idle cursor parked on the
	// case display for two ticks is sent back
	private const int WatchdogPeriodMs = 300;
	private const int WatchdogIdleTicks = 2;

	private readonly int _caseWidth;
	private readonly int _caseHeight;
	private readonly bool _verbose;
	private readonly LowLevelMouseProc _callback;
	private readonly ThreadingTimer _restoreTimer;
	private readonly object _positionLock = new();
	private ThreadingTimer? _watchdog;
	private Thread? _thread;
	private uint _threadId;
	private IntPtr _hook;
	private Point _lastOffCasePosition;
	private int _idleTicksOnCase;

	public TouchGuard(DisplaySettings display, bool verbose = false)
	{
		_caseWidth = display.Width;
		_caseHeight = display.Height;
		_verbose = verbose;
		_callback = OnMouseEvent;
		_restoreTimer = new ThreadingTimer(_ => RestoreCursor(), null, Timeout.Infinite, Timeout.Infinite);
	}

	public void Start()
	{
		var ready = new ManualResetEventSlim();
		_thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "TouchGuard" };
		_thread.Start();
		ready.Wait(TimeSpan.FromSeconds(5));
	}

	private void Run(ManualResetEventSlim ready)
	{
		_threadId = GetCurrentThreadId();
		_lastOffCasePosition = GetPrimaryCenter();
		if (GetCursorPos(out var current) && !IsOnCaseDisplay(current))
		{
			_lastOffCasePosition = current;
		}

		_hook = SetWindowsHookEx(WH_MOUSE_LL, _callback, GetModuleHandle(null), 0);
		if (_hook == IntPtr.Zero)
		{
			Log.Error($"Touch guard hook failed (Win32 error {Marshal.GetLastWin32Error()})");
			ready.Set();
			return;
		}
		_watchdog = new ThreadingTimer(_ => CheckIdleCursor(), null, WatchdogPeriodMs, WatchdogPeriodMs);
		Log.Info("Touch guard started");
		ready.Set();

		// Low-level hooks are dispatched through this thread's message loop
		while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
		{
			TranslateMessage(ref message);
			DispatchMessage(ref message);
		}

		UnhookWindowsHookEx(_hook);
		_hook = IntPtr.Zero;
	}

	private IntPtr OnMouseEvent(int code, IntPtr wParam, IntPtr lParam)
	{
		if (code >= 0)
		{
			var data = Marshal.PtrToStructure<MouseHookData>(lParam);
			var message = wParam.ToInt32();
			var onCase = IsOnCaseDisplay(data.Position);

			if (_verbose && (message != WM_MOUSEMOVE || onCase))
			{
				Log.Info($"Touch guard: msg=0x{message:X4} pt={data.Position.X},{data.Position.Y} onCase={onCase} extra=0x{data.ExtraInfo.ToInt64():X}");
			}

			if (!onCase)
			{
				if (message == WM_MOUSEMOVE)
				{
					lock (_positionLock)
					{
						_lastOffCasePosition = data.Position;
					}
				}
			}
			else if (message is WM_LBUTTONUP or WM_RBUTTONUP)
			{
				_restoreTimer.Change(RestoreDelayMs, Timeout.Infinite);
			}
			else if (message is WM_LBUTTONDOWN or WM_RBUTTONDOWN)
			{
				// A new press cancels a pending restore so drags (and long presses) are not interrupted
				_restoreTimer.Change(Timeout.Infinite, Timeout.Infinite);
			}
		}

		return CallNextHookEx(_hook, code, wParam, lParam);
	}

	private void RestoreCursor()
	{
		Point target;
		lock (_positionLock)
		{
			target = _lastOffCasePosition;
		}

		if (GetCursorPos(out var current) && IsOnCaseDisplay(current))
		{
			SetCursorPos(target.X, target.Y);
			if (_verbose)
			{
				Log.Info($"Touch guard: cursor restored to {target.X},{target.Y}");
			}
		}
	}

	private void CheckIdleCursor()
	{
		var buttonHeld = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 || (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;
		if (GetCursorPos(out var current) && IsOnCaseDisplay(current) && !buttonHeld)
		{
			if (++_idleTicksOnCase >= WatchdogIdleTicks)
			{
				_idleTicksOnCase = 0;
				RestoreCursor();
			}
		}
		else
		{
			_idleTicksOnCase = 0;
		}
	}

	private bool IsOnCaseDisplay(Point point)
	{
		var monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
		var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
		if (!GetMonitorInfo(monitor, ref info))
		{
			return false;
		}
		var width = info.Monitor.Right - info.Monitor.Left;
		var height = info.Monitor.Bottom - info.Monitor.Top;
		return (width == _caseWidth && height == _caseHeight) || (width == _caseHeight && height == _caseWidth);
	}

	private static Point GetPrimaryCenter()
	{
		var monitor = MonitorFromPoint(new Point(), MONITOR_DEFAULTTOPRIMARY);
		var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
		GetMonitorInfo(monitor, ref info);
		return new Point
		{
			X = (info.Monitor.Left + info.Monitor.Right) / 2,
			Y = (info.Monitor.Top + info.Monitor.Bottom) / 2
		};
	}

	public void Dispose()
	{
		_watchdog?.Dispose();
		_restoreTimer.Dispose();
		if (_thread is { IsAlive: true } && _threadId != 0)
		{
			PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
			_thread.Join(TimeSpan.FromSeconds(2));
		}
	}

	private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

	[StructLayout(LayoutKind.Sequential)]
	private struct Point
	{
		public int X;
		public int Y;
	}

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

	[StructLayout(LayoutKind.Sequential)]
	private struct MouseHookData
	{
		public Point Position;
		public uint MouseData;
		public uint Flags;
		public uint Time;
		public IntPtr ExtraInfo;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Msg
	{
		public IntPtr Hwnd;
		public uint Message;
		public IntPtr WParam;
		public IntPtr LParam;
		public uint Time;
		public Point Position;
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern IntPtr SetWindowsHookEx(int hookId, LowLevelMouseProc callback, IntPtr module, uint threadId);

	[DllImport("user32.dll")]
	private static extern bool UnhookWindowsHookEx(IntPtr hook);

	[DllImport("user32.dll")]
	private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll")]
	private static extern int GetMessage(out Msg message, IntPtr hwnd, uint filterMin, uint filterMax);

	[DllImport("user32.dll")]
	private static extern bool TranslateMessage(ref Msg message);

	[DllImport("user32.dll")]
	private static extern IntPtr DispatchMessage(ref Msg message);

	[DllImport("user32.dll")]
	private static extern bool PostThreadMessage(uint threadId, int message, IntPtr wParam, IntPtr lParam);

	[DllImport("kernel32.dll")]
	private static extern uint GetCurrentThreadId();

	[DllImport("user32.dll")]
	private static extern bool GetCursorPos(out Point point);

	[DllImport("user32.dll")]
	private static extern bool SetCursorPos(int x, int y);

	[DllImport("user32.dll")]
	private static extern short GetAsyncKeyState(int virtualKey);

	[DllImport("user32.dll")]
	private static extern IntPtr MonitorFromPoint(Point point, uint flags);

	[DllImport("user32.dll")]
	private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr GetModuleHandle(string? moduleName);
}
