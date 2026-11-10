using System.Runtime.InteropServices;
using ArkaHyte.Native;

namespace ArkaHyte.Sensors;

/// <summary>CPU load, physical memory and drive usage from plain Windows APIs.</summary>
public sealed class SystemMetrics
{
	private const double BytesPerGb = 1024d * 1024 * 1024;

	private long _lastIdle;
	private long _lastTotal;

	public SystemMetrics()
	{
		// Prime the counters so the first real sample covers a full interval
		ReadCpuLoad();
	}

	/// <summary>Overall CPU load in percent since the previous call.</summary>
	public double? ReadCpuLoad()
	{
		if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
		{
			return null;
		}
		// Kernel time already includes idle time
		var total = kernel + user;
		var idleDelta = idle - _lastIdle;
		var totalDelta = total - _lastTotal;
		_lastIdle = idle;
		_lastTotal = total;
		if (totalDelta <= 0)
		{
			return null;
		}
		return Math.Clamp(100d * (totalDelta - idleDelta) / totalDelta, 0, 100);
	}

	public static RamData ReadMemory()
	{
		var status = new NativeMethods.MemoryStatusEx { Length = (uint)Marshal.SizeOf<NativeMethods.MemoryStatusEx>() };
		if (!NativeMethods.GlobalMemoryStatusEx(ref status))
		{
			return new RamData(0, 0);
		}
		return new RamData((status.TotalPhys - status.AvailPhys) / BytesPerGb, status.TotalPhys / BytesPerGb);
	}

	public static DiskData? ReadDrive(string name, double? temp, int? disk)
	{
		try
		{
			var drive = new DriveInfo(name);
			if (!drive.IsReady)
			{
				return null;
			}
			var total = drive.TotalSize / BytesPerGb;
			var used = (drive.TotalSize - drive.TotalFreeSpace) / BytesPerGb;
			return new DiskData(name.TrimEnd('\\'), drive.VolumeLabel, used, total, temp, disk);
		}
		catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
		{
			return null;
		}
	}
}
