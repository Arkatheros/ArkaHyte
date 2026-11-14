using System.Text.Json;

namespace ArkaHyte;

/// <summary>
/// Dock shortcuts: defaults from appsettings.json, replaced by the user's choices made on the
/// case screen, which are kept in %LOCALAPPDATA%\ArkaHyte\shortcuts.json (survives republishing).
/// Delete that file to go back to appsettings.json.
/// </summary>
public sealed class ShortcutStore
{
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

	private readonly List<ShortcutSettings> _items;

	public ShortcutStore(IReadOnlyList<ShortcutSettings> defaults)
	{
		_items = LoadOverrides() ?? defaults.ToList();
	}

	public static string OverridePath => Path.Combine(DataFolder.Root, "shortcuts.json");

	public IReadOnlyList<ShortcutSettings> Items => _items;

	public bool Replace(int index, ShortcutSettings shortcut)
	{
		if (index < 0 || index >= _items.Count)
		{
			return false;
		}
		_items[index] = shortcut;
		Directory.CreateDirectory(DataFolder.Root);
		File.WriteAllText(OverridePath, JsonSerializer.Serialize(_items, JsonOptions));
		Log.Info(string.IsNullOrEmpty(shortcut.Target) ? $"Shortcut {index} cleared" : $"Shortcut {index} set to '{shortcut.Label}' -> {shortcut.Target}");
		return true;
	}

	private static List<ShortcutSettings>? LoadOverrides()
	{
		try
		{
			if (!File.Exists(OverridePath))
			{
				return null;
			}
			var items = JsonSerializer.Deserialize<List<ShortcutSettings>>(File.ReadAllText(OverridePath), JsonOptions);
			return items is { Count: > 0 } ? items : null;
		}
		catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
		{
			Log.Error("Ignoring unreadable shortcuts.json", exception);
			return null;
		}
	}
}
