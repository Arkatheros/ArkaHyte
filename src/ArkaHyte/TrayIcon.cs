using System.Diagnostics;

namespace ArkaHyte;

/// <summary>Notification-area icon: demo mode toggle, reload, logs folder, updates and exit.</summary>
public sealed class TrayIcon : IDisposable
{
	// Display name; the executable, folders and logs use the technical name "ArkaHyte"
	public const string AppName = "Arka Hyte";

	private readonly NotifyIcon _notifyIcon;
	private readonly ToolStripMenuItem _demoItem;
	private readonly ToolStripMenuItem _updateItem;
	private readonly ToolStripMenuItem _themeItem;
	private readonly Icon _icon;
	// "No update" is only shown for a moment after a check, so it never stays on screen once outdated
	private readonly System.Windows.Forms.Timer _noUpdateReset = new() { Interval = 4000 };
	private UpdateStage _updateStage = UpdateStage.Unavailable;
	private bool _keepMenuOpen;

	public event Action<bool>? DemoToggled;
	public event Action? ReloadRequested;
	public event Action? ExitRequested;
	public event Action? UpdateRequested;
	public event Action? SetupRequested;
	public event Action<string>? ThemeSelected;

	public TrayIcon(bool demoEnabled, string? version = null)
	{
		_icon = LoadIcon();
		_demoItem = new ToolStripMenuItem("Mode démo")
		{
			CheckOnClick = true,
			Checked = demoEnabled,
			ToolTipText = "CPU et GPU à 100 %, connexion coupée"
		};
		_demoItem.CheckedChanged += (_, _) => DemoToggled?.Invoke(_demoItem.Checked);

		_updateItem = new ToolStripMenuItem("Rechercher des mises à jour") { Visible = false };
		_noUpdateReset.Tick += (_, _) =>
		{
			_noUpdateReset.Stop();
			_updateItem.Text = "Rechercher des mises à jour";
		};
		_updateItem.Click += (_, _) => UpdateRequested?.Invoke();

		_themeItem = new ToolStripMenuItem("Thème");
		foreach (var (id, name) in Themes.All)
		{
			var item = new ToolStripMenuItem(name) { Tag = id };
			item.Click += (_, _) => ThemeSelected?.Invoke(id);
			_themeItem.DropDownItems.Add(item);
		}

		var menu = new ContextMenuStrip();
		// A label rather than a disabled item: disabled items are always painted greyed out
		menu.Items.Add(new ToolStripLabel(version is null ? AppName : $"{AppName}  {version}", _icon.ToBitmap()));
		menu.Items.Add(new ToolStripSeparator());
		menu.Items.Add(_demoItem);
		menu.Items.Add(_themeItem);
		menu.Items.Add("Recharger l'affichage", null, (_, _) => ReloadRequested?.Invoke());
		menu.Items.Add("Paramètres", null, (_, _) => SetupRequested?.Invoke());
		menu.Items.Add("Ouvrir le dossier des journaux", null, (_, _) => OpenLogs());
		menu.Items.Add(new ToolStripSeparator());
		menu.Items.Add(_updateItem);
		menu.Items.Add("Quitter", null, (_, _) => ExitRequested?.Invoke());
		SithMenuRenderer.Apply(menu);

		// A check stays in view: the menu remains open and its entry turns into the result
		// ("Recherche…" then "Aucune mise à jour disponible" for a moment, or "Installer la version X");
		// installing still closes it
		menu.ItemClicked += (_, args) => _keepMenuOpen = args.ClickedItem == _updateItem && _updateStage != UpdateStage.Available;
		menu.Closing += (_, args) =>
		{
			if (_keepMenuOpen && args.CloseReason == ToolStripDropDownCloseReason.ItemClicked)
			{
				args.Cancel = true;
			}
			_keepMenuOpen = false;
		};

		_notifyIcon = new NotifyIcon
		{
			Icon = _icon,
			Text = AppName,
			ContextMenuStrip = menu,
			Visible = true
		};
	}

	/// <summary>The context menu, exposed for the --menu-preview diagnostic capture.</summary>
	internal ContextMenuStrip Menu => _notifyIcon.ContextMenuStrip!;

	/// <summary>Reflects the update flow in the menu entry (call on the UI thread).</summary>
	public void SetUpdateState(UpdateState state)
	{
		_noUpdateReset.Stop();
		_updateStage = state.Stage;
		_updateItem.Visible = state.Stage != UpdateStage.Unavailable;
		(_updateItem.Text, _updateItem.Enabled) = state.Stage switch
		{
			UpdateStage.Checking => ("Recherche de mises à jour…", false),
			UpdateStage.UpToDate => ("Aucune mise à jour disponible", true),
			UpdateStage.Available => ($"Installer la version {state.Version}", true),
			UpdateStage.Downloading => ($"Téléchargement de la {state.Version}… {state.Progress} %", false),
			UpdateStage.Failed => (state.Version is null ? "Mise à jour impossible · réessayer" : $"Échec de la {state.Version} · réessayer", true),
			_ => ("Rechercher des mises à jour", true)
		};
		if (state.Stage == UpdateStage.UpToDate)
		{
			_noUpdateReset.Start();
		}
	}

	/// <summary>Ticks the active theme and recolours the menu with it (call on the UI thread).</summary>
	public void SetTheme(string theme)
	{
		foreach (ToolStripMenuItem item in _themeItem.DropDownItems)
		{
			item.Checked = Equals(item.Tag, theme);
		}
		SithMenuRenderer.SetTheme(theme);
		_notifyIcon.ContextMenuStrip?.Invalidate();
	}

	public void SetDemoChecked(bool value)
	{
		if (_demoItem.Checked != value)
		{
			_demoItem.Checked = value;
		}
	}

	// The .ico holds hand-tuned small sizes; ask for the one matching the tray at the current DPI
	private static Icon LoadIcon()
	{
		var path = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
		try
		{
			return new Icon(path, SystemInformation.SmallIconSize);
		}
		catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
		{
			Log.Error("Tray icon file missing, using the executable icon", exception);
			return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
		}
	}

	private static void OpenLogs()
	{
		Directory.CreateDirectory(Log.Directory);
		Process.Start(new ProcessStartInfo(Log.Directory) { UseShellExecute = true });
	}

	public void Dispose()
	{
		_notifyIcon.Visible = false;
		_notifyIcon.ContextMenuStrip?.Dispose();
		_notifyIcon.Dispose();
		_icon.Dispose();
		_noUpdateReset.Dispose();
	}
}
