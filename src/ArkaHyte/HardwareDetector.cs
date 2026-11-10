using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ArkaHyte;

/// <summary>
/// Reads the CPU and GPU names from the registry (no WMI, nothing loaded in the host) so the setup
/// wizard can fill in the hardware captions shown above the CPU and GPU sections.
/// </summary>
public static partial class HardwareDetector
{
	private const string CpuKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
	// Display adapters device class
	private const string GpuClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

	private static readonly string[] KnownMakers = ["AMD", "Intel", "NVIDIA", "Qualcomm"];

	public static HardwareSettings Detect(HardwareSettings fallback)
	{
		var (cpuMaker, cpuModel) = Split(ReadCpuName());
		var gpu = ReadGpu();
		var (gpuMaker, gpuModel) = Split(gpu?.Name);
		return new HardwareSettings(
			cpuMaker ?? fallback.CpuMaker,
			cpuModel ?? fallback.CpuModel,
			gpuMaker ?? fallback.GpuMaker,
			gpuModel ?? fallback.GpuModel,
			gpu?.MemoryGb ?? fallback.VramTotalGb);
	}

	private static string? ReadCpuName()
	{
		try
		{
			using var key = Registry.LocalMachine.OpenSubKey(CpuKey);
			if (key?.GetValue("ProcessorNameString") is not string name)
			{
				return null;
			}
			// "AMD Ryzen 7 9800X3D 8-Core Processor", "Intel(R) Core(TM) i9-14900K CPU @ 3.20GHz"
			name = TrademarkPattern().Replace(name, string.Empty);
			name = CpuSuffixPattern().Replace(name, string.Empty);
			name = GenerationPattern().Replace(name, string.Empty);
			return SpacesPattern().Replace(name, " ").Trim();
		}
		catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
		{
			Log.Error("CPU name unreadable", exception);
			return null;
		}
	}

	private sealed record GpuInfo(string Name, double? MemoryGb);

	/// <summary>The adapter with the most dedicated memory: the discrete card rather than the iGPU.</summary>
	private static GpuInfo? ReadGpu()
	{
		var adapters = new List<GpuInfo>();
		try
		{
			using var classKey = Registry.LocalMachine.OpenSubKey(GpuClassKey);
			foreach (var subKeyName in classKey?.GetSubKeyNames() ?? [])
			{
				if (!int.TryParse(subKeyName, out _))
				{
					continue;
				}
				try
				{
					using var key = classKey!.OpenSubKey(subKeyName);
					if (key?.GetValue("DriverDesc") is not string name || name.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}
					adapters.Add(new GpuInfo(TrademarkPattern().Replace(name, string.Empty).Trim(), ReadMemoryGb(key)));
				}
				catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException)
				{
					// The "Properties" subkey and some adapters are not readable: skip them
				}
			}
		}
		catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
		{
			Log.Error("GPU list unreadable", exception);
		}
		return adapters.OrderByDescending(a => a.MemoryGb ?? 0).FirstOrDefault();
	}

	private static double? ReadMemoryGb(RegistryKey key)
	{
		// The QWORD value holds the real size; the older DWORD one tops out at 4 GB
		var bytes = key.GetValue("HardwareInformation.qwMemorySize") switch
		{
			long value => value,
			byte[] { Length: 8 } raw => BitConverter.ToInt64(raw),
			_ => key.GetValue("HardwareInformation.MemorySize") switch
			{
				int value => (uint)value,
				byte[] { Length: 4 } raw => BitConverter.ToUInt32(raw),
				_ => 0L
			}
		};
		return bytes > 0 ? Math.Round(bytes / (1024.0 * 1024 * 1024)) : null;
	}

	/// <summary>
	/// Physical disk holding a volume ("C:" → 0): drives on the same disk share its temperature.
	/// IOCTLs used here need no elevation.
	/// </summary>
	public static int? ReadDiskNumber(string driveName)
	{
		try
		{
			using var volume = OpenDevice(@"\\.\" + driveName.TrimEnd('\\'));
			// VOLUME_DISK_EXTENTS: extent count, padding, then the first extent's disk number
			var extents = new byte[256];
			if (volume.IsInvalid || !DeviceIoControl(volume, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, null, 0, extents, extents.Length, out _, IntPtr.Zero)
				|| BitConverter.ToInt32(extents, 0) < 1)
			{
				return null;
			}
			return BitConverter.ToInt32(extents, 8);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
		{
			Log.Error($"Disk of {driveName} unreadable", exception);
			return null;
		}
	}

	/// <summary>Model of a physical disk ("Samsung SSD 990 PRO 2TB"), as HWiNFO names its S.M.A.R.T. sensors.</summary>
	public static string? ReadDiskModel(int diskNumber)
	{
		try
		{
			using var disk = OpenDevice($@"\\.\PhysicalDrive{diskNumber}");
			// STORAGE_PROPERTY_QUERY: StorageDeviceProperty, PropertyStandardQuery
			var query = new byte[12];
			var descriptor = new byte[1024];
			if (disk.IsInvalid || !DeviceIoControl(disk, IOCTL_STORAGE_QUERY_PROPERTY, query, query.Length, descriptor, descriptor.Length, out var length, IntPtr.Zero))
			{
				return null;
			}
			var vendor = ReadAnsi(descriptor, BitConverter.ToInt32(descriptor, 12), length);
			var product = ReadAnsi(descriptor, BitConverter.ToInt32(descriptor, 16), length);
			// NVMe drives report "NVMe" or nothing as vendor, the full name is in the product id
			var model = vendor is null || product is null || product.StartsWith(vendor, StringComparison.OrdinalIgnoreCase) || vendor.Equals("NVMe", StringComparison.OrdinalIgnoreCase)
				? product
				: $"{vendor} {product}";
			return string.IsNullOrWhiteSpace(model) ? null : model;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
		{
			Log.Error($"Model of disk {diskNumber} unreadable", exception);
			return null;
		}
	}

	private static string? ReadAnsi(byte[] buffer, int offset, int length)
	{
		if (offset <= 0 || offset >= length)
		{
			return null;
		}
		var end = Array.IndexOf(buffer, (byte)0, offset);
		var text = Encoding.ASCII.GetString(buffer, offset, (end < 0 ? length : end) - offset).Trim();
		return text.Length == 0 ? null : text;
	}

	private const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
	private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

	// No access right needed for these queries: works without elevation
	private static SafeFileHandle OpenDevice(string path) =>
		CreateFile(path, 0, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);

	/// <summary>"NVIDIA GeForce RTX 4090" → ("NVIDIA", "GeForce RTX 4090").</summary>
	private static (string? Maker, string? Model) Split(string? name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return (null, null);
		}
		foreach (var maker in KnownMakers)
		{
			if (name.StartsWith(maker + " ", StringComparison.OrdinalIgnoreCase))
			{
				return (maker, name[(maker.Length + 1)..].Trim());
			}
		}
		// Radeon cards report themselves without the maker name
		return name.StartsWith("Radeon", StringComparison.OrdinalIgnoreCase) ? ("AMD", name) : (string.Empty, name);
	}

	[GeneratedRegex(@"\((R|TM|C)\)", RegexOptions.IgnoreCase)]
	private static partial Regex TrademarkPattern();

	[GeneratedRegex(@"(\s+\d+-Core)?\s+(Processor|CPU)\b.*$|\s+(with|w/)\s+Radeon.*$", RegexOptions.IgnoreCase)]
	private static partial Regex CpuSuffixPattern();

	// "13th Gen Intel Core i7-13700K": the maker must come first
	[GeneratedRegex(@"^\s*\d+(st|nd|rd|th)\s+Gen\s+", RegexOptions.IgnoreCase)]
	private static partial Regex GenerationPattern();

	[GeneratedRegex(@"\s{2,}")]
	private static partial Regex SpacesPattern();
}
