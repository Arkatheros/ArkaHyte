namespace ArkaHyte;

/// <summary>
/// The user data folder, %LOCALAPPDATA%\ArkaHyte: settings and shortcuts at its root; logs, the browser
/// profile, HWiNFO files and downloaded tools in folders of their own.
/// </summary>
public static class DataFolder
{
	private const string LegacyUiName = "WebView2";

	public static string Root { get; } = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArkaHyte");

	/// <summary>One log file per day, the last week kept (<see cref="Log"/>).</summary>
	public static string Logs => Path.Combine(Root, "logs");

	/// <summary>Files of the HWiNFO configuration: registry backup, catalog, entries added.</summary>
	public static string HwInfo => Path.Combine(Root, "hwinfo");

	/// <summary>Programs downloaded on first use (Speedtest CLI).</summary>
	public static string Tools => Path.Combine(Root, "tools");

	private static readonly Lazy<string> UiFolder = new(ResolveUi);

	/// <summary>
	/// WebView2 profile shared by the display and the settings window: "ui", formerly "WebView2". The old
	/// folder is renamed on first use; while a browser process still holds it, it is used as is this time.
	/// </summary>
	public static string Ui => UiFolder.Value;

	private static string ResolveUi()
	{
		var ui = Path.Combine(Root, "ui");
		var legacy = Path.Combine(Root, LegacyUiName);
		if (!Directory.Exists(legacy))
		{
			return ui;
		}
		if (Directory.Exists(ui))
		{
			// Left by an older copy started meanwhile (it still uses the old name): removed when free
			try
			{
				Directory.Delete(legacy, recursive: true);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// In use: another try at the next start
			}
			return ui;
		}
		try
		{
			Directory.Move(legacy, ui);
			Log.Info("Browser profile folder renamed to ui");
			return ui;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			Log.Info("Browser profile folder still in use: kept as WebView2 for now");
			return legacy;
		}
	}

	/// <summary>Files of earlier versions, all at the root, moved to their folders (once; best effort).</summary>
	public static void MoveLegacyFiles()
	{
		try
		{
			if (!Directory.Exists(Root))
			{
				return;
			}
			Directory.CreateDirectory(Logs);
			Directory.CreateDirectory(HwInfo);
			MoveDatedLog("display.log", "display");
			MoveDatedLog("launcher.log", "launcher");
			Delete(Path.Combine(Root, "display.log.old"));
			MoveIfAbsent(Path.Combine(Root, "hwinfo-added.json"), Path.Combine(HwInfo, "added.json"));
			MoveIfAbsent(Path.Combine(Root, "hwinfo-catalog.json"), Path.Combine(HwInfo, "catalog.json"));
			// Only the latest registry backup is worth keeping
			var backups = Directory.GetFiles(Root, "hwinfo-backup-*.reg*").OrderByDescending(File.GetLastWriteTimeUtc).ToList();
			if (backups.Count > 0 && !Directory.EnumerateFiles(HwInfo, "backup-*.reg").Any())
			{
				var latest = backups[0];
				var name = Path.GetFileName(latest).Replace("hwinfo-", string.Empty, StringComparison.Ordinal).Replace(".keep", string.Empty, StringComparison.Ordinal);
				MoveIfAbsent(latest, Path.Combine(HwInfo, name));
			}
			backups.ForEach(Delete);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			Log.Error("Moving files of an earlier version failed", exception);
		}
	}

	private static void MoveDatedLog(string name, string prefix)
	{
		var source = Path.Combine(Root, name);
		if (File.Exists(source))
		{
			MoveIfAbsent(source, Path.Combine(Logs, $"{prefix}-{File.GetLastWriteTime(source):yyyy-MM-dd}.log"));
			Delete(source);
		}
	}

	private static void MoveIfAbsent(string source, string target)
	{
		if (File.Exists(source) && !File.Exists(target))
		{
			File.Move(source, target);
		}
	}

	private static void Delete(string path)
	{
		if (File.Exists(path))
		{
			File.Delete(path);
		}
	}
}
