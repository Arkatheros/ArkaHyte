namespace ArkaHyte;

/// <summary>Minimal append-only log next to the launcher log, rotated when it grows past 1 MB.</summary>
public static class Log
{
	private const long MaxBytes = 1024 * 1024;
	private static readonly object Gate = new();

	public static string Directory { get; } = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArkaHyte");

	// Data folder used before the rename to ArkaHyte (logs, shortcuts.json, WebView2 profile)
	private static readonly string LegacyDirectory = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HyteDisplay");

	private static string FilePath => Path.Combine(Directory, "display.log");

	/// <summary>Moves the pre-rename data folder once, so custom shortcuts and logs are kept.</summary>
	public static void MigrateLegacyData()
	{
		if (System.IO.Directory.Exists(Directory) || !System.IO.Directory.Exists(LegacyDirectory))
		{
			return;
		}
		try
		{
			System.IO.Directory.Move(LegacyDirectory, Directory);
			Info($"Migrated data folder from {LegacyDirectory}");
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Folder busy (old version still running): keep at least the user's dock shortcuts
			var shortcuts = Path.Combine(LegacyDirectory, "shortcuts.json");
			System.IO.Directory.CreateDirectory(Directory);
			if (File.Exists(shortcuts))
			{
				File.Copy(shortcuts, Path.Combine(Directory, "shortcuts.json"), overwrite: false);
			}
			Error("Data folder migration incomplete, shortcuts copied", exception);
		}
	}

	public static void Info(string message) => Write("INFO", message);

	public static void Error(string message, Exception? exception = null) =>
		Write("ERROR", exception is null ? message : $"{message}: {exception.GetType().Name}: {exception.Message}");

	private static void Write(string level, string message)
	{
		lock (Gate)
		{
			try
			{
				System.IO.Directory.CreateDirectory(Directory);
				var file = new FileInfo(FilePath);
				if (file.Exists && file.Length > MaxBytes)
				{
					File.Move(FilePath, FilePath + ".old", overwrite: true);
				}
				File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
			}
			catch (IOException)
			{
				// Logging must never take the display down
			}
		}
	}
}
