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
