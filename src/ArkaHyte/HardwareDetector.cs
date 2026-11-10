using System.Text.RegularExpressions;
using Microsoft.Win32;

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
