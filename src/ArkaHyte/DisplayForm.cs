using System.Text.Json;
using ArkaHyte.Native;
using ArkaHyte.Sensors;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ArkaHyte;

/// <param name="Demo">Scenario name forwarded to the page ("idle", "stress") to preview it with simulated data.</param>
/// <param name="OpenEditor">Dock button whose shortcut editor opens at start (diagnostics).</param>
public sealed record DisplayOptions(bool DevTools, string? ScreenshotPath, string? Demo, int? OpenEditor = null);

/// <summary>
/// Borderless, non-activating window pinned to the case display that hosts the web UI.
/// Touching it never steals focus from the foreground app (a fullscreen game keeps running).
/// </summary>
public sealed class DisplayForm : Form
{
	private const string VirtualHost = "hyte.local";
	private const string DemoScenario = "stress";

	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	private readonly AppSettings _settings;
	private readonly SensorHub _hub;
	private readonly ShortcutStore _shortcuts;
	private readonly ShortcutLauncher _launcher;
	private readonly DisplayOptions _options;
	private readonly WebView2 _webView;
	private readonly TrayIcon? _tray;
	private readonly System.Windows.Forms.Timer _placementRetry = new() { Interval = 5000 };
	private string? _demo;
	private bool _webReady;
	private List<object>? _dockButtons;

	public DisplayForm(AppSettings settings, SensorHub hub, DisplayOptions options)
	{
		_settings = settings;
		_hub = hub;
		_options = options;
		_demo = options.Demo;
		_shortcuts = new ShortcutStore(settings.Shortcuts);
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
			_tray = new TrayIcon(_demo is not null);
			_tray.DemoToggled += enabled => SetDemo(enabled ? DemoScenario : null);
			_tray.ReloadRequested += () => _webView.CoreWebView2?.Reload();
			_tray.ExitRequested += Close;
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

	protected override void WndProc(ref Message message)
	{
		if (message.Msg == NativeMethods.WM_MOUSEACTIVATE)
		{
			message.Result = NativeMethods.MA_NOACTIVATE;
			return;
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

	private async Task InitializeAsync()
	{
		try
		{
			var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Log.Directory, "WebView2"));
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
	}

	// Listing ~200 apps with icons takes a few seconds: do it ahead so the editor opens instantly
	private static async Task WarmUpAppCatalogAsync()
	{
		await Task.Delay(TimeSpan.FromSeconds(20));
		try
		{
			await AppCatalog.ListAsync();
		}
		catch (Exception exception)
		{
			Log.Error("App catalog warm-up failed", exception);
		}
	}

	private void Navigate()
	{
		var parameters = new List<string>();
		if (!string.IsNullOrWhiteSpace(_demo))
		{
			parameters.Add($"demo={Uri.EscapeDataString(_demo)}");
		}
		if (_options.OpenEditor is { } editorIndex)
		{
			parameters.Add($"editor={editorIndex}");
		}
		var query = parameters.Count == 0 ? string.Empty : "?" + string.Join('&', parameters);
		_webView.Source = new Uri($"https://{VirtualHost}/index.html{query}");
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
			case "saveShortcut":
				SaveShortcut(root);
				break;
		}
	}

	private static string? ReadString(JsonElement root, string name) =>
		root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;

	private void OnWebReady()
	{
		_webReady = true;
		SendConfig();
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
			shortcuts = _dockButtons
		});
	}

	private async Task SendAppCatalogAsync()
	{
		try
		{
			var apps = await AppCatalog.ListAsync();
			// Continuation may run on a pool thread: hop back to the UI thread to post
			PostFromAnyThread(new { type = "apps", apps });
		}
		catch (Exception exception)
		{
			Log.Error("Listing applications failed", exception);
			PostFromAnyThread(new { type = "apps", apps = Array.Empty<AppEntry>() });
		}
	}

	/// <summary>Applies a choice made in the on-screen editor: { index, appId, label, icon: "app" | built-in name }.</summary>
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

	private void PostFromAnyThread(object message)
	{
		if (IsDisposed || !IsHandleCreated)
		{
			return;
		}
		BeginInvoke(() => Post(message));
	}

	private void Post(object message)
	{
		if (!_webReady || IsDisposed || _webView.CoreWebView2 is null)
		{
			return;
		}
		_webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, message.GetType(), JsonOptions));
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
			_tray?.Dispose();
			_placementRetry.Dispose();
			_webView.Dispose();
		}
		base.Dispose(disposing);
	}
}
