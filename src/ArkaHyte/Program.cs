using System.Text.Json;
using ArkaHyte.Sensors;

namespace ArkaHyte;

internal static class Program
{
	/// <summary>
	/// Usage:
	///   ArkaHyte.exe                      run the case display
	///   ArkaHyte.exe --devtools           same, with F12 developer tools enabled
	///   ArkaHyte.exe --touch-log          same, logging every click/touch seen by the touch guard
	///   ArkaHyte.exe --screenshot out.png render, save a capture of the page and exit
	///   ArkaHyte.exe --demo stress        show a simulated scenario (idle, stress) instead of live data
	///   ArkaHyte.exe --open-editor 0      open the shortcut editor of dock button 0 at start
	///   ArkaHyte.exe --preview-speedtest [done]  speed test dial (or final summary) with sample values
	///   ArkaHyte.exe --speedtest out.txt  run a real speed test and write every update
	///   ArkaHyte.exe --list-apps out.json list the applications offered by the shortcut editor and exit
	///   ArkaHyte.exe --dump out.json      sample every sensor once, write the result and exit
	/// </summary>
	[STAThread]
	private static int Main(string[] args)
	{
		Log.MigrateLegacyData();
		var settings = AppSettings.Load(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
		var dumpPath = ArgumentValue(args, "--dump");
		if (dumpPath is not null)
		{
			return Dump(settings, dumpPath);
		}
		var speedTestPath = ArgumentValue(args, "--speedtest");
		if (speedTestPath is not null)
		{
			// Full real test (uses the connection), every update written as one line
			var lines = new List<string>();
			new SpeedTest().RunAsync(update => lines.Add(JsonSerializer.Serialize(update)), CancellationToken.None).GetAwaiter().GetResult();
			File.WriteAllLines(speedTestPath, lines);
			return 0;
		}
		var appsPath = ArgumentValue(args, "--list-apps");
		if (appsPath is not null)
		{
			var apps = AppCatalog.ListAsync().GetAwaiter().GetResult();
			var summary = apps.Select(a => new { a.Name, a.Id, icon = a.Icon is null ? "none" : $"{a.Icon.Length} chars" });
			File.WriteAllText(appsPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
			return 0;
		}

		var menuPreviewPath = ArgumentValue(args, "--menu-preview");
		if (menuPreviewPath is not null)
		{
			ApplicationConfiguration.Initialize();
			return MenuPreview(menuPreviewPath);
		}

		using var singleInstance = new Mutex(true, @"Local\ArkaHyte", out var isFirstInstance);
		if (!isFirstInstance)
		{
			return 0;
		}

		Log.Info("Starting");
		ApplicationConfiguration.Initialize();
		using var hub = new SensorHub(settings);
		hub.Start();

		// Keeps the mouse cursor off the case display after touches (skipped for one-shot captures)
		using var touchGuard = new TouchGuard(settings.Display, verbose: args.Contains("--touch-log"));
		if (ArgumentValue(args, "--screenshot") is null)
		{
			touchGuard.Start();
		}

		var options = new DisplayOptions(
			args.Contains("--devtools"),
			ArgumentValue(args, "--screenshot"),
			ArgumentValue(args, "--demo"),
			int.TryParse(ArgumentValue(args, "--open-editor"), out var editorIndex) ? editorIndex : null,
			args.Contains("--preview-speedtest") ? (ArgumentValue(args, "--preview-speedtest") == "done" ? "done" : "preview") : null);
		Application.Run(new DisplayForm(settings, hub, options));
		Log.Info("Stopped");
		return 0;
	}

	private static int Dump(AppSettings settings, string path)
	{
		using var hub = new SensorHub(settings);
		// Rates and CPU load are deltas: take a first sample, wait, then keep the second one
		hub.SampleAsync().GetAwaiter().GetResult();
		Thread.Sleep(1500);
		var snapshot = hub.SampleAsync().GetAwaiter().GetResult();
		var report = new
		{
			snapshot,
			hwInfoEntries = hub.HwInfo.Describe().ToList(),
			cover = hub.LatestCover is { } cover ? $"{cover.Key} ({cover.DataUrl.Length} chars)" : null
		};
		File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
		return 0;
	}

	// Renders the tray menu (demo checked, one item hovered) to a PNG without showing it
	private static int MenuPreview(string path)
	{
		using var tray = new TrayIcon(demoEnabled: true);
		var menu = tray.Menu;
		menu.CreateControl();
		menu.Size = menu.GetPreferredSize(Size.Empty);
		menu.PerformLayout();
		menu.Items.OfType<ToolStripMenuItem>().ElementAt(1).Select();
		using var bitmap = new Bitmap(menu.Width, menu.Height);
		menu.DrawToBitmap(bitmap, new Rectangle(Point.Empty, menu.Size));
		bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
		return 0;
	}

	private static string? ArgumentValue(string[] args, string name)
	{
		var index = Array.IndexOf(args, name);
		return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
	}
}
