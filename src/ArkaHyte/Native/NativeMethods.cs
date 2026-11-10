using System.Runtime.InteropServices;

namespace ArkaHyte.Native;

internal static class NativeMethods
{
	// ---------- Window styles / messages ----------
	public const int WS_EX_TOOLWINDOW = 0x00000080;
	public const int WS_EX_NOACTIVATE = 0x08000000;
	public const int WM_MOUSEACTIVATE = 0x0021;
	public const int WM_DISPLAYCHANGE = 0x007E;
	public const int MA_NOACTIVATE = 3;

	// ---------- Dark title bar (Windows 10 2004+) ----------
	public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

	[DllImport("dwmapi.dll")]
	public static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

	// ---------- Monitor power state ----------
	public const int WM_POWERBROADCAST = 0x0218;
	public const int PBT_POWERSETTINGCHANGE = 0x8013;
	public const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;
	// GUID_CONSOLE_DISPLAY_STATE: data is 0 = off, 1 = on, 2 = dimmed
	public static readonly Guid GuidConsoleDisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");

	[StructLayout(LayoutKind.Sequential)]
	public struct PowerBroadcastSetting
	{
		public Guid PowerSetting;
		public uint DataLength;
		public uint Data;
	}

	[DllImport("user32.dll", SetLastError = true)]
	public static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSettingGuid, int flags);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool UnregisterPowerSettingNotification(IntPtr handle);

	// ---------- CPU / memory ----------
	[StructLayout(LayoutKind.Sequential)]
	public struct MemoryStatusEx
	{
		public uint Length;
		public uint MemoryLoad;
		public ulong TotalPhys;
		public ulong AvailPhys;
		public ulong TotalPageFile;
		public ulong AvailPageFile;
		public ulong TotalVirtual;
		public ulong AvailVirtual;
		public ulong AvailExtendedVirtual;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

	// ---------- Native Wi-Fi (wlanapi) ----------
	public const uint WLAN_CLIENT_VERSION_2 = 2;
	public const int WlanIntfOpcodeCurrentConnection = 7;
	public const int WlanIntfOpcodeChannelNumber = 8;
	public const int WlanInterfaceStateConnected = 1;

	[DllImport("wlanapi.dll")]
	public static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

	[DllImport("wlanapi.dll")]
	public static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

	[DllImport("wlanapi.dll")]
	public static extern uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

	[DllImport("wlanapi.dll")]
	public static extern uint WlanQueryInterface(
		IntPtr clientHandle,
		ref Guid interfaceGuid,
		int opCode,
		IntPtr reserved,
		out int dataSize,
		out IntPtr data,
		IntPtr opcodeValueType);

	[DllImport("wlanapi.dll")]
	public static extern uint WlanGetNetworkBssList(
		IntPtr clientHandle,
		ref Guid interfaceGuid,
		IntPtr ssid,
		int bssType,
		[MarshalAs(UnmanagedType.Bool)] bool securityEnabled,
		IntPtr reserved,
		out IntPtr bssList);

	[DllImport("wlanapi.dll")]
	public static extern void WlanFreeMemory(IntPtr memory);
}
