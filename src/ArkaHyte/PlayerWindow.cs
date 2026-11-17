using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ArkaHyte;

/// <summary>
/// Brings the app behind the now-playing item to the front (tap on the cover). The media session only gives
/// its AppUserModelID: "Spotify.exe", "Chrome", "MSEdge", a packaged "Family!App", or a hash (Firefox). The
/// window is found among the visible top-level ones by process name or package family, else by the item's
/// title (a browser's window title is its active tab). A minimized window is restored as the taskbar does.
/// An app with no visible window (closed to the tray) is started again and shows itself: a hidden window is
/// never shown from outside, the app would not know it is visible and its interface would stay frozen.
/// Runs on the UI thread: attaching to the foreground thread's input needs a message queue.
/// </summary>
public static class PlayerWindow
{
	private const uint GW_OWNER = 4;
	private const int GWL_EXSTYLE = -20;
	private const long WS_EX_TOOLWINDOW = 0x00000080;
	private const int DWMWA_CLOAKED = 14;
	private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
	private const uint WM_SYSCOMMAND = 0x0112;
	private const int SC_RESTORE = 0xF120;

	/// <returns>False when no window was found and nothing could be started.</returns>
	public static bool BringToFront(string appId, string? title)
	{
		var (window, playerPath) = Find(appId, title);
		if (window != IntPtr.Zero)
		{
			var shown = Activate(window);
			Log.Info($"Player window of {appId} brought to the front" + (shown ? string.Empty : " (foreground refused by Windows)"));
			return true;
		}
		// Running without a visible window, or not running: packaged apps all start from their id in the Apps
		// folder, a desktop one from its program (a single-instance player then shows its own window)
		var start = appId.Contains('!')
			? new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{appId}") { UseShellExecute = false }
			: playerPath is not null ? new ProcessStartInfo(playerPath) { UseShellExecute = true } : null;
		if (start is null)
		{
			Log.Info($"No window found for the player {appId}");
			return false;
		}
		try
		{
			Process.Start(start)?.Dispose();
			Log.Info($"Player {appId} started again (no visible window)");
			return true;
		}
		catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			Log.Error($"Player {appId} could not be started", exception);
			return false;
		}
	}

	private sealed record Candidate(IntPtr Window, string Title);

	private sealed record ProcessInfo(string Name, string? Path, string? Family);

	/// <returns>The window to bring up (zero: none visible) and the player's program when it runs.</returns>
	private static (IntPtr Window, string? PlayerPath) Find(string appId, string? title)
	{
		var family = appId.Contains('!') ? appId[..appId.IndexOf('!')] : null;
		var exe = Path.GetFileNameWithoutExtension(appId).ToLowerInvariant();
		var ownProcess = (uint)Environment.ProcessId;
		var processes = new Dictionary<uint, ProcessInfo>();
		var matched = new List<Candidate>();
		var byTitle = new List<Candidate>();
		string? playerPath = null;

		ProcessInfo Describe(uint processId)
		{
			if (!processes.TryGetValue(processId, out var info))
			{
				info = ReadProcess(processId);
				processes[processId] = info;
			}
			return info;
		}

		bool IsPlayer(uint processId)
		{
			var info = Describe(processId);
			var player = family is not null
				? string.Equals(info.Family, family, StringComparison.OrdinalIgnoreCase)
				: info.Name.Length > 0 && info.Name == exe;
			if (player)
			{
				playerPath ??= info.Path;
			}
			return player;
		}

		EnumWindows((window, _) =>
		{
			if (!IsWindowVisible(window) || IsCloaked(window) || GetWindow(window, GW_OWNER) != IntPtr.Zero
				|| (GetWindowLongPtr(window, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0)
			{
				return true;
			}
			var text = ReadTitle(window);
			GetWindowThreadProcessId(window, out var processId);
			if (text.Length == 0 || processId == ownProcess)
			{
				return true;
			}
			var player = IsPlayer(processId);
			// Store apps live in a frame of ApplicationFrameHost: the app is the process of one of its children
			if (!player && family is not null && Describe(processId).Name == "applicationframehost")
			{
				EnumChildWindows(window, (child, _) =>
				{
					GetWindowThreadProcessId(child, out var childProcess);
					player = childProcess != processId && IsPlayer(childProcess);
					return !player;
				}, IntPtr.Zero);
			}
			if (player)
			{
				matched.Add(new Candidate(window, text));
			}
			else if (!string.IsNullOrWhiteSpace(title) && text.Contains(title, StringComparison.OrdinalIgnoreCase))
			{
				byTitle.Add(new Candidate(window, text));
			}
			return true;
		}, IntPtr.Zero);

		// The player's processes without a visible window still say where its program is
		if (playerPath is null && family is null)
		{
			foreach (var process in Process.GetProcessesByName(exe))
			{
				using (process)
				{
					playerPath ??= Describe((uint)process.Id).Path;
				}
			}
		}

		// Front to back (EnumWindows order): the player's window showing the item, any of its windows, a window
		// titled after the item
		var pick = matched.FirstOrDefault(c => !string.IsNullOrWhiteSpace(title) && c.Title.Contains(title, StringComparison.OrdinalIgnoreCase))
			?? matched.FirstOrDefault()
			?? byTitle.FirstOrDefault();
		return (pick?.Window ?? IntPtr.Zero, playerPath);
	}

	private static bool Activate(IntPtr window)
	{
		// As a click on its taskbar button: the app restores itself
		if (IsIconic(window))
		{
			PostMessage(window, WM_SYSCOMMAND, SC_RESTORE, IntPtr.Zero);
		}
		// The display window never takes the focus: borrow the foreground thread's right to change it
		var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
		var ownThread = GetCurrentThreadId();
		var attached = foregroundThread != 0 && foregroundThread != ownThread && AttachThreadInput(ownThread, foregroundThread, true);
		try
		{
			return SetForegroundWindow(window);
		}
		finally
		{
			if (attached)
			{
				AttachThreadInput(ownThread, foregroundThread, false);
			}
		}
	}

	private static ProcessInfo ReadProcess(uint processId)
	{
		var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
		if (process == IntPtr.Zero)
		{
			return new ProcessInfo(string.Empty, null, null);
		}
		try
		{
			var buffer = new StringBuilder(1024);
			var size = buffer.Capacity;
			var path = QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString() : null;
			var familyBuffer = new StringBuilder(256);
			var familyLength = (uint)familyBuffer.Capacity;
			var family = GetPackageFamilyName(process, ref familyLength, familyBuffer) == 0 ? familyBuffer.ToString() : null;
			return new ProcessInfo(path is null ? string.Empty : Path.GetFileNameWithoutExtension(path).ToLowerInvariant(), path, family);
		}
		finally
		{
			CloseHandle(process);
		}
	}

	private static string ReadTitle(IntPtr window)
	{
		var length = GetWindowTextLength(window);
		if (length <= 0)
		{
			return string.Empty;
		}
		var text = new StringBuilder(length + 1);
		GetWindowText(window, text, text.Capacity);
		return text.ToString();
	}

	// Windows of other virtual desktops and suspended Store apps are "visible" but cloaked
	private static bool IsCloaked(IntPtr window) =>
		DwmGetWindowAttribute(window, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

	private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

	[DllImport("user32.dll")]
	private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

	[DllImport("user32.dll")]
	private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);

	[DllImport("user32.dll")]
	private static extern IntPtr GetWindow(IntPtr window, uint command);

	[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
	private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetWindowTextLength(IntPtr window);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(IntPtr window);

	[DllImport("user32.dll")]
	private static extern bool IsIconic(IntPtr window);

	[DllImport("user32.dll")]
	private static extern bool PostMessage(IntPtr window, uint message, int wParam, IntPtr lParam);

	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(IntPtr window);

	[DllImport("user32.dll")]
	private static extern IntPtr GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);

	[DllImport("kernel32.dll")]
	private static extern uint GetCurrentThreadId();

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(IntPtr handle);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetPackageFamilyName(IntPtr process, ref uint length, StringBuilder familyName);

	[DllImport("dwmapi.dll")]
	private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
}
