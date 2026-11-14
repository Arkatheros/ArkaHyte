namespace ArkaHyte;

/// <summary>
/// Append-only log, one file per day in the logs folder (display-2026-11-14.log); files older than a week
/// are deleted. A day's file stops growing past a size cap, so a fault repeating every second cannot fill
/// the disk.
/// </summary>
public static class Log
{
	private const int KeptDays = 7;
	private const long MaxBytesPerDay = 10 * 1024 * 1024;
	private static readonly object Gate = new();
	private static DateTime _cleanedFor;

	/// <summary>The logs folder (opened from the tray menu).</summary>
	public static string Directory => DataFolder.Logs;

	private static string FilePath => Path.Combine(Directory, $"display-{DateTime.Now:yyyy-MM-dd}.log");

	public static void Info(string message) => Write("INFO", message);

	public static void Error(string message, Exception? exception = null) =>
		Write("ERROR", exception is null ? message : $"{message}: {exception.GetType().Name}: {exception.Message}");

	/// <summary>Managed heap next to the whole process, to tell GC growth apart from native memory.</summary>
	public static void Memory()
	{
		const double Mb = 1024 * 1024;
		var info = GC.GetGCMemoryInfo();
		using var process = System.Diagnostics.Process.GetCurrentProcess();
		Info($"Memory: GC heap {GC.GetTotalMemory(false) / Mb:F1} MB (committed {info.TotalCommittedBytes / Mb:F1} MB at last GC), "
			+ $"private {process.PrivateMemorySize64 / Mb:F1} MB, working set {process.WorkingSet64 / Mb:F1} MB, "
			+ $"GCs {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}, threads {process.Threads.Count}, handles {process.HandleCount}");
	}

	private static void Write(string level, string message)
	{
		lock (Gate)
		{
			try
			{
				System.IO.Directory.CreateDirectory(Directory);
				// Once a day: the files of the days past the last week go
				if (_cleanedFor != DateTime.Today)
				{
					_cleanedFor = DateTime.Today;
					DeleteOldFiles();
				}
				var file = new FileInfo(FilePath);
				if (file.Exists && file.Length > MaxBytesPerDay)
				{
					return;
				}
				File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// Logging must never take the display down
			}
		}
	}

	/// <summary>Every log of the folder (display, development launcher) older than the kept days.</summary>
	private static void DeleteOldFiles()
	{
		var limit = DateTime.Today.AddDays(-(KeptDays - 1));
		foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*.log"))
		{
			if (File.GetLastWriteTime(path) < limit)
			{
				File.Delete(path);
			}
		}
	}
}
