using System.Runtime.InteropServices;

namespace ArkaHyte;

/// <summary>Session and power commands of the dock actions: lock the PC, put it to sleep.</summary>
internal static class SystemCommands
{
	private const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
	private const uint TOKEN_QUERY = 0x8;
	private const uint SE_PRIVILEGE_ENABLED = 0x2;

	public static bool Lock()
	{
		if (LockWorkStation())
		{
			return true;
		}
		Log.Error($"Locking the session failed (error {Marshal.GetLastWin32Error()})");
		return false;
	}

	/// <summary>
	/// Sleep (not hibernation). The shutdown privilege every interactive user holds must be enabled first;
	/// runs off the UI thread, the call only returns once the PC wakes up.
	/// </summary>
	public static void Sleep() => Task.Run(() =>
	{
		EnableShutdownPrivilege();
		if (!SetSuspendState(false, false, false))
		{
			Log.Error($"Sleep failed (error {Marshal.GetLastWin32Error()})");
		}
	});

	private static void EnableShutdownPrivilege()
	{
		if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
		{
			return;
		}
		try
		{
			if (LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid))
			{
				var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
				AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
			}
		}
		finally
		{
			CloseHandle(token);
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct TokenPrivileges
	{
		public uint Count;
		public long Luid;
		public uint Attributes;
	}

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool LockWorkStation();

	[DllImport("powrprof.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.U1)]
	private static extern bool SetSuspendState(
		[MarshalAs(UnmanagedType.U1)] bool hibernate,
		[MarshalAs(UnmanagedType.U1)] bool force,
		[MarshalAs(UnmanagedType.U1)] bool wakeupEventsDisabled);

	[DllImport("kernel32.dll")]
	private static extern IntPtr GetCurrentProcess();

	[DllImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(IntPtr handle);

	[DllImport("advapi32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

	[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

	[DllImport("advapi32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
		ref TokenPrivileges newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);
}
