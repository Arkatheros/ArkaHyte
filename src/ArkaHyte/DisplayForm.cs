using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ArkaHyte.Native;
using ArkaHyte.Sensors;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ArkaHyte;

/// <param name="Demo">Scenario name forwarded to the page ("idle", "stress", "game", "desktop") to preview it with simulated data.</param>
/// <param name="OpenEditor">Dock button whose shortcut editor opens at start (diagnostics).</param>
/// <param name="PreviewSpeedTest">"preview" (dial during upload) or "done" (summary): speed test view with sample values, no network use.</param>
/// <param name="OpenSetup">Opens the setup wizard at start even when it was already completed.</param>
public sealed record DisplayOptions(bool DevTools, string? ScreenshotPath, string? Demo, int? OpenEditor = null, string? PreviewSpeedTest = null, bool OpenSetup = false);

/// <summary>
/// Borderless, non-activating window pinned to the case display that hosts the web UI.
/// Touching it never steals focus from the foreground app (a fullscreen game keeps running).
/// </summary>
public sealed class DisplayForm : Form
{
	private const string VirtualHost = "hyte.local";
	private const string DemoScenario = "stress";

	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	// Replaced when the on-screen settings save new sensor choices or thresholds
	private AppSettings _settings;
	private readonly SensorHub _hub;
	private readonly ShortcutStore _shortcuts;
	private readonly ShortcutLauncher _launcher;
	private readonly DisplayOptions _options;
	private readonly TouchGuard _touchGuard;
	private readonly AppUpdater _updater;
	private readonly WebView2 _webView;
	private readonly TrayIcon? _tray;
	private readonly System.Windows.Forms.Timer _placementRetry = new() { Interval = 5000 };
	private readonly SpeedTest _speedTest = new();
	private readonly WeatherService _weather = new();
	// Volume, microphone and default output (audio controls of the now playing widget)
	private readonly Audio.AudioService _audio = new();
	private bool _audioStarted;
	private int _audioPostPending;
	private CancellationTokenSource? _speedTestRun;
	private string? _demo;
	private bool _webReady;
	private bool _firstReadyLogged;
	private IntPtr _displayStateNotification;
	private bool _displayOn = true;
	private bool _sessionLocked;
	private List<object>? _dockButtons;
	private SetupForm? _setup;
	private string _theme;

	/// <summary>Set when the window closed so that a new instance applies freshly saved settings.</summary>
	public bool RestartRequested { get; private set; }

	public DisplayForm(AppSettings settings, SensorHub hub, TouchGuard touchGuard, AppUpdater updater, DisplayOptions options)
	{
		_settings = settings;
		_hub = hub;
		_touchGuard = touchGuard;
		_updater = updater;
		_options = options;
		_demo = options.Demo;
		_shortcuts = new ShortcutStore(settings.Shortcuts);
		_theme = Themes.Normalize(settings.Theme);
		_launcher = new ShortcutLauncher(_shortcuts, settings.CpuClickTarget);

		Text = TrayIcon.AppName;
		FormBorderStyle = FormBorderStyle.None;
		ShowInTaskbar = false;
		StartPosition = FormStartPosition.Manual;
		BackColor = Color.Black;

		_webView = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
		Controls.Add(_webView);

		// Diagnostic screenshot runs are short-lived: no tray icon for them
		if (options.ScreenshotPath is null)
		{
			// "dev" tells a development build (scripts\Test-Dev.ps1) apart from the installed copy
			_tray = new TrayIcon(_demo is not null, updater.IsInstalled ? updater.CurrentVersion : $"{updater.CurrentVersion} dev");
			_tray.DemoToggled += enabled => SetDemo(enabled ? DemoScenario : null);
			_tray.ReloadRequested += () => _webView.CoreWebView2?.Reload();
			_tray.ExitRequested += Close;
			_tray.UpdateRequested += () => _ = _updater.ActAsync();
			_tray.SetupRequested += OpenSetup;
			_tray.ThemeSelected += SetTheme;
			_tray.SetTheme(_theme);
			_updater.StateChanged += state => PostToUiThread(() => OnUpdateState(state));
			_updater.ExitRequested += () => PostToUiThread(Close);
			Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
		}

		_weather.Updated += data => PostToUiThread(() => PostWeather(data));
		if (options.ScreenshotPath is null)
		{
			_weather.Configure(settings.Weather);
		}

		_placementRetry.Tick += (_, _) => PlaceOnCaseDisplay(showIfFound: true);
		Load += async (_, _) => await InitializeAsync();
	}

	protected override bool ShowWithoutActivation => true;

	protected override CreateParams CreateParams
	{
		get
		{
			var parameters = base.CreateParams;
			parameters.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
			return parameters;
		}
	}

	protected override void OnHandleCreated(EventArgs e)
	{
		base.OnHandleCreated(e);
		if (_options.ScreenshotPath is null)
		{
			var guid = NativeMethods.GuidConsoleDisplayState;
			_displayStateNotification = NativeMethods.RegisterPowerSettingNotification(Handle, ref guid, NativeMethods.DEVICE_NOTIFY_WINDOW_HANDLE);

			// From the handle rather than Load: the window stays hidden (no Load) while the case display
			// is not found, which is exactly when the wizard is needed
			if (!_settings.SetupCompleted || _options.OpenSetup)
			{
				BeginInvoke(async () =>
				{
					// Lets the display's own WebView2 start first
					await Task.Delay(TimeSpan.FromSeconds(2));
					OpenSetup();
				});
			}
		}
	}

	/// <summary>Shows the setup wizard, or brings it forward when it is already open.</summary>
	private void OpenSetup()
	{
		if (_setup is { IsDisposed: false })
		{
			_setup.Activate();
			return;
		}
		_setup = new SetupForm(_settings);
		_setup.Saved += restart =>
		{
			if (restart)
			{
				RestartRequested = true;
				Close();
			}
			else
			{
				ReloadSettings();
			}
		};
		_setup.FormClosed += (_, _) => _setup = null;
		_setup.Show();
		_setup.Activate();
	}

	protected override void OnHandleDestroyed(EventArgs e)
	{
		if (_displayStateNotification != IntPtr.Zero)
		{
			NativeMethods.UnregisterPowerSettingNotification(_displayStateNotification);
			_displayStateNotification = IntPtr.Zero;
		}
		base.OnHandleDestroyed(e);
	}

	protected override void WndProc(ref Message message)
	{
		if (message.Msg == NativeMethods.WM_MOUSEACTIVATE)
		{
			message.Result = NativeMethods.MA_NOACTIVATE;
			return;
		}
		if (message.Msg == NativeMethods.WM_POWERBROADCAST && message.WParam == NativeMethods.PBT_POWERSETTINGCHANGE)
		{
			var setting = Marshal.PtrToStructure<NativeMethods.PowerBroadcastSetting>(message.LParam);
			if (setting.PowerSetting == NativeMethods.GuidConsoleDisplayState)
			{
				// Dimmed (2) still shows the display
				_displayOn = setting.Data != 0;
				UpdateRendering();
			}
		}
		if (message.Msg == NativeMethods.WM_DISPLAYCHANGE)
		{
			// Monitors are still settling when this arrives; re-check shortly after
			BeginInvoke(async () =>
			{
				await Task.Delay(1500);
				PlaceOnCaseDisplay(showIfFound: true);
			});
		}
		base.WndProc(ref message);
	}

	protected override void SetVisibleCore(bool value)
	{
		// The window only becomes visible once the case display has been found
		if (value && !IsHandleCreated)
		{
			CreateHandle();
			value = PlaceOnCaseDisplay(showIfFound: false);
		}
		base.SetVisibleCore(value);
	}

	/// <summary>Finds the case display by its native size (indexes change with drivers) and covers it.</summary>
	private bool PlaceOnCaseDisplay(bool showIfFound)
	{
		var width = _settings.Display.Width;
		var height = _settings.Display.Height;
		var screen = Screen.AllScreens.FirstOrDefault(s =>
			(s.Bounds.Width == width && s.Bounds.Height == height) ||
			(s.Bounds.Width == height && s.Bounds.Height == width));

		if (screen is null)
		{
			if (Visible)
			{
				Log.Info("Case display lost, hiding window");
				Hide();
			}
			_placementRetry.Start();
			return false;
		}

		_placementRetry.Stop();
		if (Bounds != screen.Bounds)
		{
			Bounds = screen.Bounds;
			Log.Info($"Placed on {screen.DeviceName} at {screen.Bounds}");
		}
		if (showIfFound && !Visible)
		{
			Show();
		}
		return true;
	}

	private void OnUpdateState(UpdateState state)
	{
		_tray?.SetUpdateState(state);
		PostUpdateState(state);
	}

	// The page keeps a banner up while a version is available (also re-sent after a reload)
	private void PostUpdateState(UpdateState state) =>
		Post(new { type = "update", stage = state.Stage.ToString().ToLowerInvariant(), version = state.Version, progress = state.Progress });

	private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs args)
	{
		if (args.Reason is not (Microsoft.Win32.SessionSwitchReason.SessionLock or Microsoft.Win32.SessionSwitchReason.SessionUnlock))
		{
			return;
		}
		var locked = args.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock;
		PostToUiThread(() =>
		{
			_sessionLocked = locked;
			UpdateRendering();
		});
	}

	/// <summary>
	/// Nobody sees the case screen while the monitors sleep or the session is locked: the WebView is
	/// hidden (Chromium stops painting and throttles the page timers) and sampling pauses.
	/// </summary>
	private void UpdateRendering()
	{
		var active = _displayOn && !_sessionLocked;
		if (_hub.Paused == !active)
		{
			return;
		}
		_hub.Paused = !active;
		_webView.Visible = active;
		if (_webView.CoreWebView2 is { } core)
		{
			// Low lets Chromium drop its caches while hidden; restored before it paints again
			core.MemoryUsageTargetLevel = active ? CoreWebView2MemoryUsageTargetLevel.Normal : CoreWebView2MemoryUsageTargetLevel.Low;
		}
		Log.Info(active ? "Display visible again, rendering resumed" : "Display off or session locked, rendering paused");
	}

	// Null hides the weather line (turned off, or no reading yet)
	private void PostWeather(WeatherData? data) => Post(new { type = "weather", weather = data });

	private void PostToUiThread(Action action)
	{
		if (!IsDisposed && IsHandleCreated)
		{
			BeginInvoke(action);
		}
	}

	private async Task InitializeAsync()
	{
		try
		{
			var environment = await CoreWebView2Environment.CreateAsync(null, DataFolder.Ui);
			await _webView.EnsureCoreWebView2Async(environment);
		}
		catch (Exception exception)
		{
			Log.Error("WebView2 initialisation failed", exception);
			return;
		}

		var core = _webView.CoreWebView2;
		var settings = core.Settings;
		settings.AreDefaultContextMenusEnabled = false;
		settings.AreDevToolsEnabled = _options.DevTools;
		settings.IsStatusBarEnabled = false;
		settings.IsZoomControlEnabled = false;
		settings.AreBrowserAcceleratorKeysEnabled = _options.DevTools;
		settings.IsSwipeNavigationEnabled = false;
		settings.IsPinchZoomEnabled = false;
		settings.IsGeneralAutofillEnabled = false;
		settings.IsPasswordAutosaveEnabled = false;

		core.SetVirtualHostNameToFolderMapping(
			VirtualHost,
			Path.Combine(AppContext.BaseDirectory, "wwwroot"),
			CoreWebView2HostResourceAccessKind.Deny);
		core.WebMessageReceived += OnWebMessage;
		core.NavigationStarting += (_, _) => _webReady = false;
		core.ProcessFailed += (_, args) =>
		{
			Log.Error($"WebView2 process failed: {args.ProcessFailedKind}");
			_webReady = false;
			BeginInvoke(() => _webView.Reload());
		};

		_hub.SnapshotReady += snapshot => PostFromAnyThread(snapshot);
		_hub.CoverChanged += (key, dataUrl) => PostFromAnyThread(new { type = "cover", key, dataUrl });

		Navigate();
		_ = WarmUpAppCatalogAsync();
		if (_tray is not null)
		{
			_updater.Start();
			_tray.SetUpdateState(_updater.State);
		}
	}

	// Listing ~200 apps with icons takes a few seconds: do it ahead so the editor opens instantly
	private static async Task WarmUpAppCatalogAsync()
	{
		await Task.Delay(TimeSpan.FromSeconds(20));
		try
		{
			await AppCatalog.ListJsonAsync();
		}
		catch (Exception exception)
		{
			Log.Error("App catalog warm-up failed", exception);
		}
	}

	private void Navigate()
	{
		// In the address so the first paint already has the theme's colours
		var parameters = new List<string> { $"theme={_theme}" };
		if (!string.IsNullOrWhiteSpace(_demo))
		{
			parameters.Add($"demo={Uri.EscapeDataString(_demo)}");
		}
		if (_options.OpenEditor is { } editorIndex)
		{
			parameters.Add($"editor={editorIndex}");
		}
		if (_options.PreviewSpeedTest is { } preview)
		{
			parameters.Add($"speedtest={Uri.EscapeDataString(preview)}");
		}
		var query = parameters.Count == 0 ? string.Empty : "?" + string.Join('&', parameters);
		_webView.Source = new Uri($"https://{VirtualHost}/index.html{query}");
	}

	/// <summary>Tray choice: switched live on the page, kept in the user settings.</summary>
	private void SetTheme(string theme)
	{
		theme = Themes.Normalize(theme);
		if (theme == _theme)
		{
			return;
		}
		_theme = theme;
		_tray?.SetTheme(theme);
		Post(new { type = "config", theme });
		try
		{
			AppSettings.SaveUser(root => root["Theme"] = theme);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
		{
			Log.Error("Saving the theme failed", exception);
		}
		Log.Info($"Theme '{theme}'");
	}

	private void SetDemo(string? scenario)
	{
		_demo = scenario;
		_tray?.SetDemoChecked(scenario is not null);
		Log.Info(scenario is null ? "Demo mode off" : $"Demo mode '{scenario}'");
		if (_webView.CoreWebView2 is not null)
		{
			Navigate();
		}
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
			Log.Error("Invalid message from the web UI", exception);
			return;
		}

		var type = ReadString(root, "type");
		var action = ReadString(root, "action");
		switch (type)
		{
			case "ready":
				OnWebReady();
				break;
			case "launch" when action is not null:
				// Before the app opens: an elevated one would block the cursor restore
				_touchGuard.ReturnCursorNow();
				if (!_launcher.Launch(action))
				{
					Post(new { type = "toast", text = "Raccourci indisponible" });
				}
				break;
			case "media" when action is not null:
				_ = ControlMediaAsync(action);
				break;
			case "listApps":
				_ = SendAppCatalogAsync();
				break;
			case "hwinfoList":
				PostHwInfoList();
				break;
			case "saveMetric":
				SaveMetric(root);
				break;
			case "saveShortcut":
				SaveShortcut(root);
				break;
			case "speedtest" when action == "start":
				StartSpeedTest();
				break;
			case "speedtest" when action == "cancel":
				_speedTestRun?.Cancel();
				break;
			case "audio" when action is not null:
				ControlAudio(action, root);
				break;
			case "update" when action == "install":
				// Tapping the banner: same as the tray entry (installs the version found)
				_ = _updater.ActAsync();
				break;
		}
	}

	private void StartSpeedTest()
	{
		if (_speedTestRun is not null)
		{
			return;
		}
		_speedTestRun = new CancellationTokenSource();
		_ = RunSpeedTestAsync(_speedTestRun);
	}

	private async Task RunSpeedTestAsync(CancellationTokenSource run)
	{
		try
		{
			await Task.Run(() => _speedTest.RunAsync(update => PostFromAnyThread(update), run.Token));
		}
		catch (OperationCanceledException)
		{
			PostFromAnyThread(new SpeedTestUpdate("cancelled", 0));
		}
		catch (SpeedTestInstallException exception)
		{
			Log.Error("Speed test CLI install failed", exception.InnerException);
			PostFromAnyThread(new SpeedTestUpdate("error", 0, Reason: "install"));
		}
		catch (Exception exception)
		{
			Log.Error("Speed test failed", exception);
			PostFromAnyThread(new SpeedTestUpdate("error", 0, Reason: "test"));
		}
		finally
		{
			// Continuation of an awaited Task.Run may land on a pool thread: clear the run on the UI thread
			BeginInvoke(() =>
			{
				run.Dispose();
				if (ReferenceEquals(_speedTestRun, run))
				{
					_speedTestRun = null;
				}
			});
		}
	}

	private static string? ReadString(JsonElement root, string name) =>
		root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;

	private void OnWebReady()
	{
		if (!_firstReadyLogged)
		{
			// Startup cost on our side, to compare with the delay before Windows starts the process
			_firstReadyLogged = true;
			using var process = Process.GetCurrentProcess();
			Log.Info($"Web UI ready {(DateTime.Now - process.StartTime).TotalSeconds:0.0} s after process start");
		}
		_webReady = true;
		SendConfig();
		PostUpdateState(_updater.State);
		PostWeather(_weather.Latest);
		_ = Task.Run(() =>
		{
			if (!_audioStarted)
			{
				_audioStarted = _audio.Start();
				_audio.Changed += ScheduleAudioPost;
			}
			PostAudioState();
		});
		if (_hub.LatestCover is { } cover)
		{
			Post(new { type = "cover", key = cover.Key, dataUrl = cover.DataUrl });
		}
		if (_hub.Latest is { } snapshot)
		{
			Post(snapshot);
		}
		if (_options.ScreenshotPath is not null)
		{
			_ = CaptureAndExitAsync(_options.ScreenshotPath);
		}
	}

	private void SendConfig()
	{
		var hardware = _settings.Hardware;
		// Icons are resolved once (file reads / icon extraction) and reused until a shortcut changes
		_dockButtons ??= _shortcuts.Items
			.Select(s => new { label = s.Label, target = s.Target, icon = IconProvider.Resolve(s.Icon) })
			.ToList<object>();
		Post(new
		{
			type = "config",
			cpuMaker = hardware.CpuMaker,
			cpuModel = hardware.CpuModel,
			gpuMaker = hardware.GpuMaker,
			gpuModel = hardware.GpuModel,
			theme = _theme,
			thresholds = _settings.EffectiveThresholds,
			// The place is picked in the setup wizard; the case screen only turns the weather on or off
			weather = new
			{
				enabled = _settings.Weather?.Enabled ?? false,
				place = _settings.Weather?.Place ?? string.Empty,
				located = _settings.Weather is { Latitude: not null, Longitude: not null }
			},
			gameView = _settings.EffectiveGameView,
			cpuDetails = _settings.CpuDetails,
			shortcuts = _dockButtons
		});
	}

	private async Task SendAppCatalogAsync()
	{
		try
		{
			// Already JSON from the child process: wrapped as is, never deserialised here
			var apps = await AppCatalog.ListJsonAsync();
			var json = $$"""{"type":"apps","apps":{{apps}}}""";
			// Continuation may run on a pool thread: hop back to the UI thread to post
			PostToUiThread(() => PostJson(json));
		}
		catch (Exception exception)
		{
			Log.Error("Listing applications failed", exception);
			PostFromAnyThread(new { type = "apps", apps = Array.Empty<AppEntry>() });
		}
	}

	/// <summary>Applies a choice made in the on-screen editor: { index, appId, label, icon: "app" | built-in name }.</summary>
	/// <summary>On-screen settings: the Gadget entries with live values, and what each widget reads now.</summary>
	private void PostHwInfoList()
	{
		var reader = _hub.HwInfo;
		Post(new
		{
			type = "hwinfo",
			entries = reader.Entries.Select(e => new { sensor = e.Sensor, label = e.Label, text = e.Text }),
			matches = SensorSettings.Matches(reader, _settings)
		});
	}

	/// <summary>
	/// On-screen settings saved: { sensors, drives, thresholds } (see <see cref="SensorSettings"/>), written to the
	/// user settings and applied right away, without restarting.
	/// </summary>
	private void SaveMetric(JsonElement message)
	{
		try
		{
			AppSettings.SaveUser(root =>
			{
				SensorSettings.ApplyChoices(root, message);
				SensorSettings.ApplyThresholds(root, message);
				SensorSettings.ApplyWeather(root, message);
				SensorSettings.ApplyDisplay(root, message);
			});
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
		{
			Log.Error("Saving the on-screen settings failed", exception);
			Post(new { type = "toast", text = "Enregistrement impossible" });
			return;
		}
		ReloadSettings();
		Post(new { type = "toast", text = "Réglages enregistrés" });
	}

	/// <summary>Settings file changed (on-screen settings, settings window): applied without restarting.</summary>
	private void ReloadSettings()
	{
		var weatherBefore = _settings.Weather;
		_settings = AppSettings.Load();
		_hub.UpdateSettings(_settings);
		if (_settings.Weather != weatherBefore)
		{
			_weather.Configure(_settings.Weather);
		}
		SendConfig();
	}

	private void SaveShortcut(JsonElement root)
	{
		var appId = ReadString(root, "appId");
		var label = ReadString(root, "label");
		var icon = ReadString(root, "icon");
		if (!root.TryGetProperty("index", out var indexElement) || !indexElement.TryGetInt32(out var index)
			|| string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(label))
		{
			Log.Error("Incomplete shortcut received from the web UI");
			return;
		}

		var target = AppCatalog.AppsFolderPrefix + appId;
		var iconSetting = icon is null or "app" ? target : icon;
		if (_shortcuts.Replace(index, new ShortcutSettings(label, target, iconSetting)))
		{
			_dockButtons = null;
			SendConfig();
			Post(new { type = "toast", text = $"{label} enregistré" });
		}
	}

	private async Task ControlMediaAsync(string action)
	{
		try
		{
			await _hub.Media.ControlAsync(action);
		}
		catch (Exception exception)
		{
			Log.Error($"Media control '{action}' failed", exception);
		}
	}

	private void PostFromAnyThread(object message) => PostToUiThread(() => Post(message));

	/// <summary>Audio commands from the page: volume { value }, output { id }, mic (toggle); off the UI thread.</summary>
	private void ControlAudio(string action, JsonElement message)
	{
		if (!_audioStarted)
		{
			return;
		}
		switch (action)
		{
			case "volume" when message.TryGetProperty("value", out var value) && value.TryGetInt32(out var percent):
				_ = Task.Run(() => _audio.SetVolume(percent));
				break;
			case "output" when ReadString(message, "id") is { } id:
				_ = Task.Run(() =>
				{
					_audio.SetDefaultOutput(id);
					PostAudioState();
				});
				break;
			case "mic":
				_ = Task.Run(_audio.ToggleMic);
				break;
		}
	}

	// Notifications come in bursts (a volume drag): one state sent per short window
	private void ScheduleAudioPost()
	{
		if (Interlocked.Exchange(ref _audioPostPending, 1) == 1)
		{
			return;
		}
		_ = Task.Run(async () =>
		{
			await Task.Delay(60);
			Interlocked.Exchange(ref _audioPostPending, 0);
			PostAudioState();
		});
	}

	private void PostAudioState()
	{
		if (_audioStarted)
		{
			PostFromAnyThread(new { type = "audio", audio = _audio.GetState() });
		}
	}

	private void Post(object message) => PostJson(JsonSerializer.Serialize(message, message.GetType(), JsonOptions));

	private void PostJson(string json)
	{
		if (!_webReady || IsDisposed || _webView.CoreWebView2 is null)
		{
			return;
		}
		_webView.CoreWebView2.PostWebMessageAsJson(json);
	}

	/// <summary>Diagnostic mode: lets ignition and a few samples play, saves a PNG of the page, then quits.</summary>
	private async Task CaptureAndExitAsync(string path)
	{
		await Task.Delay(TimeSpan.FromSeconds(6));
		try
		{
			await using var file = File.Create(path);
			await _webView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
			Log.Info($"Screenshot saved to {path}");
		}
		catch (Exception exception)
		{
			Log.Error("Screenshot failed", exception);
		}
		Close();
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			// Static event: would keep the form alive otherwise
			Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
			_speedTestRun?.Cancel();
			_weather.Dispose();
			_audio.Dispose();
			_tray?.Dispose();
			_setup?.Dispose();
			_placementRetry.Dispose();
			_webView.Dispose();
		}
		base.Dispose(disposing);
	}
}
