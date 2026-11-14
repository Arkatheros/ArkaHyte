using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArkaHyte.Sensors;

namespace ArkaHyte;

/// <summary>
/// The whole configuration in one JSON file, to bring it back after reinstalling Windows or the app:
/// settings (appsettings.json), dock shortcuts (shortcuts.json) and the electricity history (energy.json).
/// Left out on purpose: logs, browser profile, downloaded tools and the HWiNFO files, which belong to
/// this Windows installation (HWiNFO is configured again from the wizard).
/// </summary>
public static class ConfigBackup
{
	public const string Format = "ArkaHyte.Config";
	public const int FormatVersion = 1;
	public const string Extension = "json";

	private const string SettingsKey = "appsettings";
	private const string ShortcutsKey = "shortcuts";
	private const string EnergyKey = "energy";

	private static readonly JsonSerializerOptions WriteOptions = new()
	{
		WriteIndented = true,
		IndentCharacter = '\t',
		IndentSize = 1,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};

	private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

	/// <summary>Suggested name of an export: ArkaHyte-config-2026-11-14.json.</summary>
	public static string DefaultFileName => $"ArkaHyte-config-{DateTime.Now:yyyy-MM-dd}.{Extension}";

	/// <summary>Writes the saved configuration (not what is still being edited in the wizard) to <paramref name="path"/>.</summary>
	public static void Export(string path, EnergyMeter? energy)
	{
		// The meter keeps up to a minute of counting in memory
		energy?.Save();
		var root = new JsonObject
		{
			["format"] = Format,
			["formatVersion"] = FormatVersion,
			["appVersion"] = AppVersion,
			["exportedAt"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
			[SettingsKey] = ReadJson(AppSettings.CurrentFilePath) ?? new JsonObject()
		};
		if (ReadJson(ShortcutStore.OverridePath) is { } shortcuts)
		{
			root[ShortcutsKey] = shortcuts;
		}
		if (ReadJson(EnergyMeter.FilePath) is { } history)
		{
			root[EnergyKey] = history;
		}
		var temp = path + ".tmp";
		File.WriteAllText(temp, root.ToJsonString(WriteOptions) + Environment.NewLine);
		File.Move(temp, path, overwrite: true);
		Log.Info($"Configuration exported to {path}");
	}

	/// <summary>
	/// Checks an export before anything is replaced; null when it can be imported, otherwise the reason,
	/// in French, for the user.
	/// </summary>
	public static string? Validate(string path, out JsonObject? backup)
	{
		backup = null;
		try
		{
			backup = JsonNode.Parse(File.ReadAllText(path), documentOptions: ReadOptions) as JsonObject;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			Log.Error($"Unreadable configuration file {path}", exception);
			return "Fichier illisible.";
		}
		if (backup?["format"]?.GetValueKind() != JsonValueKind.String || backup["format"]!.GetValue<string>() != Format)
		{
			return "Ce fichier n'est pas une configuration exportée par Arka Hyte.";
		}
		if (backup["formatVersion"] is not JsonValue version || !version.TryGetValue(out int number) || number > FormatVersion)
		{
			return "Configuration exportée par une version plus récente d'Arka Hyte : mettez l'application à jour d'abord.";
		}
		if (backup[SettingsKey] is not JsonObject)
		{
			return "Le fichier ne contient pas de réglages.";
		}
		return null;
	}

	/// <summary>
	/// Replaces the configuration with <paramref name="backup"/> (checked by <see cref="Validate"/>). The
	/// files replaced are copied first to backup\before-import-{date}. The display must restart afterwards.
	/// </summary>
	public static void Import(JsonObject backup, string source, EnergyMeter? energy)
	{
		var keep = Path.Combine(DataFolder.Root, "backup", $"before-import-{DateTime.Now:yyyy-MM-dd-HHmmss}");
		energy?.Save();
		foreach (var file in new[] { AppSettings.UserFilePath, ShortcutStore.OverridePath, EnergyMeter.FilePath })
		{
			if (File.Exists(file))
			{
				Directory.CreateDirectory(keep);
				File.Copy(file, Path.Combine(keep, Path.GetFileName(file)), overwrite: true);
			}
		}

		var settings = (JsonObject)backup[SettingsKey]!.DeepClone();
		// An export always comes from a set up copy: the wizard is not shown again
		settings["SetupCompleted"] = true;
		Write(AppSettings.UserFilePath, settings);

		// No shortcuts in the export: the defaults of the settings apply, as they did there
		if (backup[ShortcutsKey] is JsonArray shortcuts)
		{
			Write(ShortcutStore.OverridePath, shortcuts);
		}
		else if (File.Exists(ShortcutStore.OverridePath))
		{
			File.Delete(ShortcutStore.OverridePath);
		}

		// Through the meter: it would otherwise write its own days back when the display stops
		if (backup[EnergyKey] is JsonObject history)
		{
			if (energy is not null)
			{
				energy.Replace(history.ToJsonString());
			}
			else
			{
				Write(EnergyMeter.FilePath, history);
			}
		}
		Log.Info($"Configuration imported from {source} (previous files kept in {keep})");
	}

	private static JsonNode? ReadJson(string path)
	{
		try
		{
			return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path), documentOptions: ReadOptions) : null;
		}
		catch (JsonException exception)
		{
			// A broken file is not worth exporting; the rest still is
			Log.Error($"Left out of the export, unreadable: {path}", exception);
			return null;
		}
	}

	private static void Write(string path, JsonNode node)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, node.ToJsonString(WriteOptions) + Environment.NewLine);
	}

	private static string AppVersion =>
		Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";
}
