using Microsoft.Win32;

namespace ArkaHyte;

/// <summary>
/// What the installer adds around the app files (Velopack hooks): start at logon through the
/// per-user Run key (no rights needed) and a user copy of the settings. HYTE Nexus is left alone:
/// disabling or uninstalling it is up to the user.
/// </summary>
public static class Installation
{
	private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
	private const string RunValueName = TrayIcon.AppName;

	/// <summary>Velopack after-install hook (30 s budget, then the process exits).</summary>
	public static void OnInstalled()
	{
		RegisterStartup();
		KeepUserSettings();
	}

	/// <summary>Velopack after-update hook: the executable path is stable, but re-assert it.</summary>
	public static void OnUpdated() => RegisterStartup();

	/// <summary>Velopack before-uninstall hook.</summary>
	public static void OnUninstalling()
	{
		using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
		key?.DeleteValue(RunValueName, throwOnMissingValue: false);
		Log.Info("Start at logon removed");
	}

	private static void RegisterStartup()
	{
		using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
		key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
		Log.Info($"Start at logon registered: {Environment.ProcessPath}");
	}

	/// <summary>
	/// Updates replace the app folder: the settings the user edits live in the data folder,
	/// seeded once from the shipped defaults (see <see cref="AppSettings.Load()"/>).
	/// </summary>
	private static void KeepUserSettings()
	{
		var shipped = Path.Combine(AppContext.BaseDirectory, AppSettings.FileName);
		if (!File.Exists(AppSettings.UserFilePath) && File.Exists(shipped))
		{
			Directory.CreateDirectory(Log.Directory);
			File.Copy(shipped, AppSettings.UserFilePath);
			Log.Info($"Settings copied to {AppSettings.UserFilePath}");
		}
	}
}
