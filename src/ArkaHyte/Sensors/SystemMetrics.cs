using System.Runtime.InteropServices;
using ArkaHyte.Native;

namespace ArkaHyte.Sensors;

/// <summary>CPU load, physical memory and drive usage from plain Windows APIs.</summary>
public sealed class SystemMetrics
{
	private const double BytesPerGb = 1024d * 1024 * 1024;

	private long _lastIdle;
	private long _lastTotal;
	// Per logical processor, for the detail panel (first processor group, up to 64)
	private readonly NativeMethods.ProcessorPerformance[] _processors = new NativeMethods.ProcessorPerformance[Math.Min(Environment.ProcessorCount, 64)];
	private readonly long[] _lastProcessorIdle = new long[Math.Min(Environment.ProcessorCount, 64)];
	private readonly long[] _lastProcessorTotal = new long[Math.Min(Environment.ProcessorCount, 64)];

	public SystemMetrics()
	{
		// Prime the counters so the first real sample covers a full interval
		ReadCpuLoad();
		ReadThreadLoads();
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

	/// <summary>Load of each logical processor in percent since the previous call; null when unreadable.</summary>
	public IReadOnlyList<double>? ReadThreadLoads()
	{
		var size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.ProcessorPerformance>() * _processors.Length;
		if (NativeMethods.NtQuerySystemInformation(NativeMethods.SystemProcessorPerformanceInformation, _processors, size, out _) != 0)
		{
			return null;
		}
		var loads = new double[_processors.Length];
		for (var i = 0; i < _processors.Length; i++)
		{
			// Kernel time already includes idle time
			var total = _processors[i].KernelTime + _processors[i].UserTime;
			var idle = _processors[i].IdleTime;
			var totalDelta = total - _lastProcessorTotal[i];
			var idleDelta = idle - _lastProcessorIdle[i];
			_lastProcessorTotal[i] = total;
			_lastProcessorIdle[i] = idle;
			loads[i] = totalDelta > 0 ? Math.Clamp(100d * (totalDelta - idleDelta) / totalDelta, 0, 100) : 0;
		}
		return loads;
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
