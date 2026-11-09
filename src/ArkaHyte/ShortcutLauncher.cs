using System.Diagnostics;

namespace ArkaHyte;

/// <summary>Opens the targets bound to the dock buttons and to the CPU ring (executables, URIs, Store apps).</summary>
public sealed class ShortcutLauncher
{
	public const string CpuAction = "cpu";

	private readonly ShortcutStore _shortcuts;
	private readonly string _cpuClickTarget;

	public ShortcutLauncher(ShortcutStore shortcuts, string cpuClickTarget)
	{
		_shortcuts = shortcuts;
		_cpuClickTarget = cpuClickTarget;
	}

	/// <summary>Action is "cpu" or the index of a dock button, as sent by the web UI.</summary>
	public bool Launch(string action)
	{
		var items = _shortcuts.Items;
		var (target, arguments) = action == CpuAction
			? (_cpuClickTarget, null)
			: int.TryParse(action, out var index) && index >= 0 && index < items.Count
				? (items[index].Target, items[index].Arguments)
				: (null, null);

		if (string.IsNullOrWhiteSpace(target))
		{
			Log.Error($"No shortcut configured for '{action}'");
			return false;
		}
		try
		{
			var startInfo = new ProcessStartInfo(Environment.ExpandEnvironmentVariables(target)) { UseShellExecute = true };
			if (!string.IsNullOrWhiteSpace(arguments))
			{
				startInfo.Arguments = arguments;
			}
			Process.Start(startInfo);
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
