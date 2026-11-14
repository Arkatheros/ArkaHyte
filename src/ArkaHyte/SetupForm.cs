using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArkaHyte.Native;
using ArkaHyte.Sensors;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace ArkaHyte;

/// <summary>
/// Setup wizard on the main screen (setup.html): picks the case display, checks that HWiNFO runs and
/// reports to Gadget, maps each widget to a Gadget entry and fills in the hardware captions.
/// Opens by itself until it has been completed once, then from the tray menu.
/// </summary>
public sealed class SetupForm : Form
{
	private const string VirtualHost = "hyte.local";
	private const string HwInfoDownloadUrl = "https://www.hwinfo.com/download/";
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	private AppSettings _settings;
	private readonly int _startStep;
	private readonly string? _previewPath;
	private readonly WebView2 _webView;
	private readonly HwInfoReader _hwInfo = new();
	private readonly System.Windows.Forms.Timer _poll = new() { Interval = 2000 };
	private bool _webReady;

	/// <summary>
	/// Settings written. True: the display must restart to apply them (first setup, case screen changed);
	/// false: it reloads them in place (settings opened from the tray).
	/// </summary>
	public event Action<bool>? Saved;

	/// <param name="previewPath">Diagnostics: capture the page to this PNG and quit, never saving anything.</param>
	public SetupForm(AppSettings settings, int startStep = 0, string? previewPath = null)
	{
		_settings = settings;
		_startStep = startStep;
		_previewPath = previewPath;

		// Once set up, the same pages are the settings: free navigation, saved without restarting
		Text = settings.SetupCompleted ? $"{TrayIcon.AppName} · Paramètres" : $"{TrayIcon.AppName} · Configuration";
		Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
		AutoScaleMode = AutoScaleMode.Dpi;
		AutoScaleDimensions = new SizeF(96, 96);
		ClientSize = new Size(960, 700);
		MinimumSize = new Size(760, 560);
		StartPosition = FormStartPosition.CenterScreen;
		BackColor = Color.Black;
		MaximizeBox = false;

		_webView = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
		Controls.Add(_webView);

		_poll.Tick += (_, _) => SendHwInfo();
		Load += async (_, _) => await InitializeAsync();
	}

	protected override void OnHandleCreated(EventArgs e)
	{
		base.OnHandleCreated(e);
		var dark = 1;
		NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
	}

	private async Task InitializeAsync()
	{
		try
		{
			// Same data folder as the display: both windows share one browser process
			var environment = await CoreWebView2Environment.CreateAsync(null, DataFolder.Ui);
			await _webView.EnsureCoreWebView2Async(environment);
		}
		catch (Exception exception)
		{
			Log.Error("Setup wizard WebView2 initialisation failed", exception);
			Close();
			return;
		}

		var core = _webView.CoreWebView2;
		core.Settings.AreDefaultContextMenusEnabled = false;
		core.Settings.AreDevToolsEnabled = false;
		core.Settings.IsStatusBarEnabled = false;
		core.Settings.IsZoomControlEnabled = false;
		core.Settings.IsGeneralAutofillEnabled = false;
		core.SetVirtualHostNameToFolderMapping(
			VirtualHost,
			Path.Combine(AppContext.BaseDirectory, "wwwroot"),
			CoreWebView2HostResourceAccessKind.Deny);
		core.WebMessageReceived += OnWebMessage;
		core.NavigationStarting += (_, _) => _webReady = false;
		_webView.Source = new Uri($"https://{VirtualHost}/setup.html?step={_startStep}");
	}

	private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
	{
		JsonElement root;
		try
		{
			using var document = JsonDocument.Parse(args.WebMessageAsJson);
			root = document.RootElement.Clone();
		}
		catch (JsonException exception)
		{
			Log.Error("Invalid message from the setup wizard", exception);
			return;
		}

		switch (root.TryGetProperty("type", out var type) ? type.GetString() : null)
		{
			case "ready":
				_webReady = true;
				SendState();
				_poll.Start();
				if (_previewPath is not null)
				{
					_ = CaptureAndExitAsync(_previewPath);
				}
				break;
			case "identify":
				ScreenBadge.ShowAll();
				break;
			case "openHwInfoSite":
				Open(HwInfoDownloadUrl);
				break;
			case "startHwInfo":
				StartHwInfo();
				break;
			case "configureHwInfo":
				_ = ConfigureHwInfoAsync();
				break;
			case "save":
				Save(root);
				break;
			case "searchPlace":
				_ = SearchPlaceAsync(root.TryGetProperty("query", out var query) ? query.GetString() : null);
				break;
			case "skip":
				Complete(_ => { }, restart: false, close: true);
				break;
			case "close":
				Close();
				break;
			case "listProcesses":
				SendProcesses();
				break;
		}
	}

	// ---------- State sent to the page ----------

	private void SendState()
	{
		var screens = Screen.AllScreens;
		var detected = HardwareDetector.Detect(_settings.Hardware);
		Post(new
		{
			type = "state",
			preview = _previewPath is not null,
			completed = _settings.SetupCompleted,
			caseSize = new { width = _settings.Display.Width, height = _settings.Display.Height },
			screens = screens.Select((screen, index) => new
			{
				number = index + 1,
				name = screen.DeviceName.TrimStart('\\', '.'),
				x = screen.Bounds.X,
				y = screen.Bounds.Y,
				width = screen.Bounds.Width,
				height = screen.Bounds.Height,
				primary = screen.Primary
			}),
			hardware = _settings.Hardware,
			detected,
			drives = ReadDrives(),
			hwInfoPath = HwInfoInstall.FindExecutable(),
			weather = _settings.Weather,
			gameView = _settings.EffectiveGameView
		});
		SendHwInfo();
	}

	/// <summary>Game view step: applications with a window, one of which can be added as a game.</summary>
	private void SendProcesses()
	{
		var own = Environment.ProcessId;
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "explorer.exe", "ApplicationFrameHost.exe", "TextInputHost.exe" };
		var processes = new List<(string Exe, string Name)>();
		foreach (var process in Process.GetProcesses())
		{
			using (process)
			{
				try
				{
					if (process.Id == own || process.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(process.MainWindowTitle))
					{
						continue;
					}
					var path = ForegroundGame.ProcessPath((uint)process.Id);
					var exe = path is null ? null : Path.GetFileName(path);
					if (exe is not null && seen.Add(exe))
					{
						processes.Add((exe, process.MainWindowTitle.Trim()));
					}
				}
				catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
				{
					// Exited while listed
				}
			}
		}
		Post(new
		{
			type = "processes",
			processes = processes.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Select(p => new { exe = p.Exe, name = p.Name })
		});
	}

	/// <summary>Repeated while the wizard is open: HWiNFO can be started or configured meanwhile.</summary>
	private void SendHwInfo()
	{
		if (!_webReady)
		{
			return;
		}
		_hwInfo.Refresh();
		Post(new
		{
			type = "hwinfo",
			running = IsHwInfoRunning(),
			entries = _hwInfo.Entries.Select(e => new { sensor = e.Sensor, label = e.Label, text = e.Text }),
			matches = SensorSettings.Matches(_hwInfo, _settings)
		});
	}

	/// <summary>Configured drives that exist, with the disk model used to suggest their S.M.A.R.T. sensor.</summary>
	private object[] ReadDrives()
	{
		var drives = new List<object>();
		foreach (var drive in _settings.Drives)
		{
			try
			{
				var info = new DriveInfo(drive.Name);
				if (!info.IsReady)
				{
					continue;
				}
				var disk = HardwareDetector.ReadDiskNumber(drive.Name);
				var model = disk is { } number ? HardwareDetector.ReadDiskModel(number) : null;
				drives.Add(new { name = drive.Name, label = info.VolumeLabel, disk, model });
			}
			catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
			{
				Log.Error($"Drive {drive.Name} unreadable", exception);
			}
		}
		return [.. drives];
	}

	private static bool IsHwInfoRunning()
	{
		foreach (var name in HwInfoInstall.ProcessNames)
		{
			var processes = Process.GetProcessesByName(name);
			var found = processes.Length > 0;
			foreach (var process in processes)
			{
				process.Dispose();
			}
			if (found)
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>
	/// Reports the useful HWiNFO readings to Gadget: an elevated copy of this executable stops HWiNFO,
	/// rewrites its sensor settings and starts it again (one UAC prompt); its result comes back as a file.
	/// </summary>
	private async Task ConfigureHwInfoAsync()
	{
		var resultPath = Path.Combine(Path.GetTempPath(), $"arkahyte-hwinfo-{Guid.NewGuid():N}.json");
		try
		{
			using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"{HwInfoConfigurator.Argument} \"{resultPath}\"")
			{
				UseShellExecute = true,
				Verb = "runas"
			});
			if (process is null)
			{
				Post(new { type = "hwinfoConfigured", ok = false, error = "failed" });
				return;
			}
			await process.WaitForExitAsync();
			var result = File.Exists(resultPath) ? File.ReadAllText(resultPath) : null;
			PostJson(result is null
				? """{"type":"hwinfoConfigured","ok":false,"error":"failed"}"""
				: $$"""{"type":"hwinfoConfigured","result":{{result}}}""");
		}
		catch (System.ComponentModel.Win32Exception exception)
		{
			// Also a declined UAC prompt
			Log.Error("HWiNFO configuration not started", exception);
			Post(new { type = "hwinfoConfigured", ok = false, error = "declined" });
		}
		finally
		{
			File.Delete(resultPath);
		}
	}

	private static void StartHwInfo()
	{
		if (HwInfoInstall.FindExecutable() is not { } path)
		{
			return;
		}
		try
		{
			// HWiNFO asks for elevation: the shell shows the UAC prompt
			Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
		}
		catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			// Win32Exception also covers a UAC prompt the user declined
			Log.Error("Starting HWiNFO failed", exception);
		}
	}

	private static void Open(string url)
	{
		try
		{
			Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
		}
		catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			Log.Error($"Opening {url} failed", exception);
		}
	}

	/// <summary>Weather step: places matching what the user typed (Open-Meteo geocoding).</summary>
	private async Task SearchPlaceAsync(string? query)
	{
		if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
		{
			Post(new { type = "places", places = Array.Empty<WeatherPlace>() });
			return;
		}
		try
		{
			var places = await WeatherService.SearchAsync(query.Trim());
			Post(new { type = "places", places });
		}
		catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
		{
			Log.Error("Place search failed", exception);
			Post(new { type = "places", places = Array.Empty<WeatherPlace>(), failed = true });
		}
	}

	// ---------- Saving ----------

	/// <summary>
	/// { display: { width, height }, hardware: { cpuMaker, ... }, sensors: { cpuTemp: { sensor, label } | null },
	/// drives: { "C:": { sensor, label } | null } }. Entries left out keep their current selector (wildcards included).
	/// </summary>
	private void Save(JsonElement message)
	{
		// The case screen is only looked for at start: changing it restarts the display, the rest is
		// applied in place once the first setup is done
		var displayChanged = false;
		Complete(root =>
		{
			if (message.TryGetProperty("display", out var display)
				&& display.TryGetProperty("width", out var width) && width.TryGetInt32(out var w) && w > 0
				&& display.TryGetProperty("height", out var height) && height.TryGetInt32(out var h) && h > 0)
			{
				root["Display"] = new JsonObject { ["Width"] = w, ["Height"] = h };
				displayChanged = w != _settings.Display.Width || h != _settings.Display.Height;
			}
			if (message.TryGetProperty("hardware", out var hardwareElement)
				&& hardwareElement.Deserialize<HardwareSettings>(JsonOptions) is { } hardware)
			{
				root["Hardware"] = JsonSerializer.SerializeToNode(hardware);
			}
			SensorSettings.ApplyChoices(root, message);
			SensorSettings.ApplyWeather(root, message);
			SensorSettings.ApplyGameView(root, message);
		}, restart: () => !_settings.SetupCompleted || displayChanged, close: false);
	}

	private void Complete(Action<JsonObject> change, bool restart, bool close) => Complete(change, () => restart, close);

	/// <param name="restart">Asked once the change is written (it may depend on what was written).</param>
	/// <param name="close">Closes the window even when the display does not restart.</param>
	private void Complete(Action<JsonObject> change, Func<bool> restart, bool close)
	{
		if (_previewPath is not null)
		{
			return;
		}
		try
		{
			AppSettings.SaveUser(root =>
			{
				change(root);
				root["SetupCompleted"] = true;
			});
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
		{
			Log.Error("Saving the setup failed", exception);
			Post(new { type = "saveFailed" });
			return;
		}
		var mustRestart = restart();
		if (mustRestart || close)
		{
			Close();
		}
		else
		{
			// Stays open: later saves compare with what is now written
			_settings = AppSettings.Load();
			Post(new { type = "saved" });
		}
		Saved?.Invoke(mustRestart);
	}

	private void Post(object message)
	{
		if (_webReady && !IsDisposed && _webView.CoreWebView2 is { } core)
		{
			core.PostWebMessageAsJson(JsonSerializer.Serialize(message, message.GetType(), JsonOptions));
		}
	}

	private void PostJson(string json)
	{
		if (_webReady && !IsDisposed && _webView.CoreWebView2 is { } core)
		{
			core.PostWebMessageAsJson(json);
		}
	}

	private async Task CaptureAndExitAsync(string path)
	{
		await Task.Delay(TimeSpan.FromSeconds(3));
		try
		{
			await using var file = File.Create(path);
			await _webView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
			Log.Info($"Setup capture saved to {path}");
		}
		catch (Exception exception)
		{
			Log.Error("Setup capture failed", exception);
		}
		Close();
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			_poll.Dispose();
			_hwInfo.Dispose();
			_webView.Dispose();
		}
		base.Dispose(disposing);
	}
}

/// <summary>Large number shown for a few seconds in the middle of each screen, to tell them apart.</summary>
internal sealed class ScreenBadge : Form
{
	private static readonly TimeSpan Duration = TimeSpan.FromSeconds(3);
	private readonly string _number;
	private readonly string _caption;

	private ScreenBadge(int number, Screen screen)
	{
		_number = number.ToString();
		_caption = $"{screen.Bounds.Width} × {screen.Bounds.Height}";
		FormBorderStyle = FormBorderStyle.None;
		ShowInTaskbar = false;
		TopMost = true;
		StartPosition = FormStartPosition.Manual;
		BackColor = Color.FromArgb(10, 2, 4);
		var size = Math.Min(260, Math.Min(screen.Bounds.Width, screen.Bounds.Height) - 40);
		Bounds = new Rectangle(
			screen.Bounds.X + (screen.Bounds.Width - size) / 2,
			screen.Bounds.Y + (screen.Bounds.Height - size) / 2,
			size,
			size);
		DoubleBuffered = true;
	}

	protected override bool ShowWithoutActivation => true;

	// Tool window like the display itself: on this setup an ordinary window placed on the case
	// display is pushed back onto the primary screen right after it appears
	protected override CreateParams CreateParams
	{
		get
		{
			var parameters = base.CreateParams;
			parameters.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
			return parameters;
		}
	}

	public static void ShowAll()
	{
		var badges = Screen.AllScreens.Select((screen, index) => new ScreenBadge(index + 1, screen)).ToList();
		var timer = new System.Windows.Forms.Timer { Interval = (int)Duration.TotalMilliseconds };
		timer.Tick += (_, _) =>
		{
			timer.Dispose();
			badges.ForEach(badge => badge.Close());
		};
		badges.ForEach(badge => badge.Show());
		timer.Start();
	}

	protected override void OnPaint(PaintEventArgs e)
	{
		var graphics = e.Graphics;
		graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
		graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
		var bounds = ClientRectangle;
		using var border = new Pen(Color.FromArgb(255, 31, 51), 4);
		graphics.DrawRectangle(border, 2, 2, bounds.Width - 4, bounds.Height - 4);
		using var numberFont = new Font("Segoe UI Light", bounds.Height * 0.42f, GraphicsUnit.Pixel);
		using var captionFont = new Font("Segoe UI", bounds.Height * 0.08f, GraphicsUnit.Pixel);
		using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
		var numberArea = new RectangleF(0, 0, bounds.Width, bounds.Height * 0.78f);
		var captionArea = new RectangleF(0, bounds.Height * 0.7f, bounds.Width, bounds.Height * 0.22f);
		graphics.DrawString(_number, numberFont, Brushes.White, numberArea, center);
		using var captionBrush = new SolidBrush(Color.FromArgb(255, 143, 118, 121));
		graphics.DrawString(_caption, captionFont, captionBrush, captionArea, center);
	}
}
