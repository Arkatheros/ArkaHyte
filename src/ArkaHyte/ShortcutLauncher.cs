using System.Diagnostics;

namespace ArkaHyte;

/// <summary>
/// Opens the targets bound to the dock buttons and to the CPU panel: executables, URIs, Store apps, system
/// actions ("action:lock", "action:sleep", "action:screenshot") and the user's own actions ("custom:{id}",
/// a URL or a PowerShell script defined in the settings, looked up at launch so edits apply at once).
/// </summary>
public sealed class ShortcutLauncher
{
	public const string CpuAction = "cpu";
	public const string ActionPrefix = "action:";
	public const string CustomPrefix = "custom:";
	public static readonly string[] SystemActions = ["lock", "sleep", "screenshot"];

	private readonly ShortcutStore _shortcuts;
	private readonly Func<AppSettings> _settings;

	public ShortcutLauncher(ShortcutStore shortcuts, Func<AppSettings> settings)
	{
		_shortcuts = shortcuts;
		_settings = settings;
	}

	/// <summary>Action is "cpu" or the index of a dock button, as sent by the web UI.</summary>
	public bool Launch(string action)
	{
		var items = _shortcuts.Items;
		var (target, arguments) = action == CpuAction
			? (_settings().CpuClickTarget, null)
			: int.TryParse(action, out var index) && index >= 0 && index < items.Count
				? (items[index].Target, items[index].Arguments)
				: (null, null);

		if (string.IsNullOrWhiteSpace(target))
		{
			Log.Error($"No shortcut configured for '{action}'");
			return false;
		}
		if (target.StartsWith(ActionPrefix, StringComparison.OrdinalIgnoreCase))
		{
			return RunSystemAction(target[ActionPrefix.Length..]);
		}
		if (target.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase))
		{
			var id = target[CustomPrefix.Length..];
			var custom = _settings().CustomActions?.FirstOrDefault(a => a.Id == id);
			if (custom is null)
			{
				Log.Error($"Custom action '{id}' no longer exists");
				return false;
			}
			(target, arguments) = custom.Kind == CustomAction.Script
				? ("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{custom.Target}\"")
				: (custom.Target, null);
		}
		return Start(target, arguments, action);
	}

	private static bool RunSystemAction(string name)
	{
		switch (name)
		{
			case "lock":
				return SystemCommands.Lock();
			case "sleep":
				SystemCommands.Sleep();
				return true;
			case "screenshot":
				// Windows' screen snip, on the main screen (Win+Shift+S)
				return Start("ms-screenclip:", null, name);
			default:
				Log.Error($"Unknown system action '{name}'");
				return false;
		}
	}

	private static bool Start(string target, string? arguments, string action)
	{
		try
		{
			var startInfo = new ProcessStartInfo(Environment.ExpandEnvironmentVariables(target)) { UseShellExecute = true };
			if (!string.IsNullOrWhiteSpace(arguments))
			{
				startInfo.Arguments = arguments;
			}
			Process.Start(startInfo)?.Dispose();
			Log.Info($"Launched '{action}' -> {target}");
			return true;
		}
		catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
		{
			Log.Error($"Failed to launch '{action}' ({target})", exception);
			return false;
		}
	}
}
