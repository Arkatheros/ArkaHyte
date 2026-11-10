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
	private readonly Icon _icon;

	public event Action<bool>? DemoToggled;
	public event Action? ReloadRequested;
	public event Action? ExitRequested;
	public event Action? UpdateRequested;

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
		_updateItem.Click += (_, _) => UpdateRequested?.Invoke();

		var menu = new ContextMenuStrip();
		// A label rather than a disabled item: disabled items are always painted greyed out
		menu.Items.Add(new ToolStripLabel(version is null ? AppName : $"{AppName}  {version}", _icon.ToBitmap()));
		menu.Items.Add(new ToolStripSeparator());
		menu.Items.Add(_demoItem);
		menu.Items.Add("Recharger l'affichage", null, (_, _) => ReloadRequested?.Invoke());
		menu.Items.Add("Ouvrir le dossier des journaux", null, (_, _) => OpenLogs());
		menu.Items.Add(new ToolStripSeparator());
		menu.Items.Add(_updateItem);
		menu.Items.Add("Quitter", null, (_, _) => ExitRequested?.Invoke());
		SithMenuRenderer.Apply(menu);

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
		_updateItem.Visible = state.Stage != UpdateStage.Unavailable;
		(_updateItem.Text, _updateItem.Enabled) = state.Stage switch
		{
			UpdateStage.Checking => ("Recherche de mises à jour…", false),
			UpdateStage.UpToDate => ("À jour · rechercher à nouveau", true),
			UpdateStage.Available => ($"Installer la version {state.Version}", true),
			UpdateStage.Downloading => ($"Téléchargement de la {state.Version}… {state.Progress} %", false),
			UpdateStage.Failed => (state.Version is null ? "Mise à jour impossible · réessayer" : $"Échec de la {state.Version} · réessayer", true),
			_ => ("Rechercher des mises à jour", true)
		};
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
	}
}
