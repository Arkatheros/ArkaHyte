using System.Diagnostics;

namespace ArkaHyte;

/// <summary>Notification-area icon: demo mode toggle, reload, logs folder and exit.</summary>
public sealed class TrayIcon : IDisposable
{
	// Display name; the executable, folders and logs use the technical name "ArkaHyte"
	public const string AppName = "Arka Hyte";

	private readonly NotifyIcon _notifyIcon;
	private readonly ToolStripMenuItem _demoItem;
	private readonly Icon _icon;

	public event Action<bool>? DemoToggled;
	public event Action? ReloadRequested;
	public event Action? ExitRequested;

	public TrayIcon(bool demoEnabled)
	{
		_icon = LoadIcon();
		_demoItem = new ToolStripMenuItem("Mode démo")
		{
			CheckOnClick = true,
			Checked = demoEnabled,
			ToolTipText = "CPU et GPU à 100 %, connexion coupée"
		};
		_demoItem.CheckedChanged += (_, _) => DemoToggled?.Invoke(_demoItem.Checked);

		var menu = new ContextMenuStrip();
		// A label rather than a disabled item: disabled items are always painted greyed out
		menu.Items.Add(new ToolStripLabel(AppName, _icon.ToBitmap()));
		menu.Items.Add(new ToolStripSeparator());
		menu.Items.Add(_demoItem);
		menu.Items.Add("Recharger l'affichage", null, (_, _) => ReloadRequested?.Invoke());
		menu.Items.Add("Ouvrir le dossier des journaux", null, (_, _) => OpenLogs());
		menu.Items.Add(new ToolStripSeparator());
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
