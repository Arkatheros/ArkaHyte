namespace ArkaHyte;

/// <summary>Minimal append-only log next to the launcher log, rotated when it grows past 1 MB.</summary>
public static class Log
{
	private const long MaxBytes = 1024 * 1024;
	private static readonly object Gate = new();

	public static string Directory { get; } = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArkaHyte");

	private static string FilePath => Path.Combine(Directory, "display.log");

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
