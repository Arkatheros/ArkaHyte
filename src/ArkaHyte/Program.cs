using System.Diagnostics;
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
	///   ArkaHyte.exe --memory-log         same, logging the memory use every 10 s (hourly otherwise)
	///   ArkaHyte.exe --setup              same, opening the setup wizard at start
	///   ArkaHyte.exe --simulate-updates   development build: tray update entry with a fake check (nothing downloaded)
	///   ArkaHyte.exe --identify-screens   show each screen's number for 3 s (as the setup wizard does) and exit
	///   ArkaHyte.exe --setup-preview out.png [--setup-step 2]  render the setup wizard alone, capture it and exit
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
		// Installer and updater hooks first: Velopack runs them and exits during install, update and uninstall
		Velopack.VelopackApp.Build()
			.OnAfterInstallFastCallback(_ => Installation.OnInstalled())
			.OnAfterUpdateFastCallback(_ => Installation.OnUpdated())
			.OnBeforeUninstallFastCallback(_ => Installation.OnUninstalling())
			.Run();

		var settings = AppSettings.Load();
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
		var exportPath = ArgumentValue(args, AppCatalog.ExportArgument);
		if (exportPath is not null)
		{
			// Child process started by the display to list applications away from its own memory
			return AppCatalog.Export(exportPath);
		}
		var appsPath = ArgumentValue(args, "--list-apps");
		if (appsPath is not null)
		{
			var apps = AppCatalog.EnumerateOnStaAsync().GetAwaiter().GetResult();
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

		if (args.Contains("--identify-screens"))
		{
			// Same numbers as the setup wizard, for 3 s
			ApplicationConfiguration.Initialize();
			ScreenBadge.ShowAll();
			using var exit = new System.Windows.Forms.Timer { Interval = 3500 };
			exit.Tick += (_, _) => Application.ExitThread();
			exit.Start();
			Application.Run();
			return 0;
		}

		var setupPreviewPath = ArgumentValue(args, "--setup-preview");
		if (setupPreviewPath is not null)
		{
			// Standalone and read-only: works next to a running instance
			ApplicationConfiguration.Initialize();
			var step = int.TryParse(ArgumentValue(args, "--setup-step"), out var stepIndex) ? stepIndex : 0;
			Application.Run(new SetupForm(settings, step, setupPreviewPath));
			return 0;
		}

		using var singleInstance = new Mutex(true, @"Local\ArkaHyte", out var isFirstInstance);
		// A restart (after the setup wizard) waits for the previous instance to finish closing
		if (!isFirstInstance && !(args.Contains(RestartArgument) && WaitForPreviousInstance(singleInstance)))
		{
			return 0;
		}

		Log.Info("Starting");
		ApplicationConfiguration.Initialize();
		using var hub = new SensorHub(settings);
		hub.Start();

		// Memory trend in display.log: hourly, or every 10 s with --memory-log while investigating
		var memoryLogPeriod = args.Contains("--memory-log") ? TimeSpan.FromSeconds(10) : TimeSpan.FromHours(1);
		using var memoryLog = new System.Threading.Timer(_ => Log.Memory(), null, memoryLogPeriod, memoryLogPeriod);

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
			args.Contains("--preview-speedtest") ? (ArgumentValue(args, "--preview-speedtest") == "done" ? "done" : "preview") : null,
			args.Contains("--setup"));
		using var updater = new AppUpdater(simulate: args.Contains("--simulate-updates"));
		Log.Info($"Version {updater.CurrentVersion}");
		var display = new DisplayForm(settings, hub, touchGuard, updater, options);
		Application.Run(display);
		if (display.RestartRequested)
		{
			Log.Info("Restarting to apply the new settings");
			singleInstance.ReleaseMutex();
			Process.Start(new ProcessStartInfo(Environment.ProcessPath!, RestartArgument) { UseShellExecute = false })?.Dispose();
		}
		Log.Info("Stopped");
		return 0;
	}

	private const string RestartArgument = "--restarted";

	private static bool WaitForPreviousInstance(Mutex singleInstance)
	{
		try
		{
			return singleInstance.WaitOne(TimeSpan.FromSeconds(15));
		}
		catch (AbandonedMutexException)
		{
			// The previous instance exited without releasing it: the mutex is ours now
			return true;
		}
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
		// The demo item: hovered and checked, the busiest row (blade bar next to the check box)
		menu.Items.OfType<ToolStripMenuItem>().First().Select();
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
