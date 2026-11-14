using Microsoft.Win32;

namespace ArkaHyte.Sensors;

/// <summary>Where HWiNFO is installed and how its processes are named.</summary>
internal static class HwInfoInstall
{
	public static readonly string[] ProcessNames = ["HWiNFO64", "HWiNFO32", "HWiNFO"];

	/// <summary>HWiNFO executable from its uninstall entry, or the default install folder.</summary>
	public static string? FindExecutable()
	{
		const string uninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
		try
		{
			foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
			{
				using var uninstall = hive.OpenSubKey(uninstallKey);
				foreach (var name in uninstall?.GetSubKeyNames() ?? [])
				{
					if (!name.StartsWith("HWiNFO", StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}
					using var entry = uninstall!.OpenSubKey(name);
					if (entry?.GetValue("InstallLocation") is string folder)
					{
						var path = ProcessNames.Select(p => Path.Combine(folder, p + ".exe")).FirstOrDefault(File.Exists);
						if (path is not null)
						{
							return path;
						}
					}
				}
			}
		}
		catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
		{
			Log.Error("HWiNFO install lookup failed", exception);
		}
		var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HWiNFO64", "HWiNFO64.exe");
		return File.Exists(fallback) ? fallback : null;
	}
}
