using System.Text.Json;
using System.Text.Json.Nodes;
using ArkaHyte.Sensors;

namespace ArkaHyte;

/// <summary>
/// Sensor choices and alert thresholds as the setup wizard and the on-screen settings edit them:
/// the keys shared with setup.js / console.js, how they map to appsettings.json, and how a choice
/// sent by a page is written back (as an edit of the JSON tree, see <see cref="AppSettings.SaveUser"/>).
/// </summary>
public static class SensorSettings
{
	/// <summary>Widgets fed by HWiNFO: key shared with the pages, property name in appsettings.json.</summary>
	public static readonly (string Key, string Property, Func<HwInfoSettings, SensorSelector?> Read)[] Metrics =
	[
		("cpuTemp", nameof(HwInfoSettings.CpuTemp), s => s.CpuTemp),
		("cpuPower", nameof(HwInfoSettings.CpuPower), s => s.CpuPower),
		("gpuTemp", nameof(HwInfoSettings.GpuTemp), s => s.GpuTemp),
		("gpuPower", nameof(HwInfoSettings.GpuPower), s => s.GpuPower),
		("gpuLoad", nameof(HwInfoSettings.GpuLoad), s => s.GpuLoad),
		("gpuVram", nameof(HwInfoSettings.GpuVramPercent), s => s.GpuVramPercent),
		("gpuFps", nameof(HwInfoSettings.GpuFps), s => s.GpuFps),
		("gpuFpsLow", nameof(HwInfoSettings.GpuFpsLow), s => s.GpuFpsLow),
		("gpuFrameTime", nameof(HwInfoSettings.GpuFrameTime), s => s.GpuFrameTime)
	];

	/// <summary>Metrics with a gauge and alert levels: key shared with the pages, property in "Thresholds".</summary>
	public static readonly (string Key, string Property)[] ThresholdKeys =
	[
		("cpuTemp", nameof(ThresholdSettings.CpuTemp)),
		("cpuPower", nameof(ThresholdSettings.CpuPower)),
		("gpuTemp", nameof(ThresholdSettings.GpuTemp)),
		("gpuPower", nameof(ThresholdSettings.GpuPower)),
		("diskTemp", nameof(ThresholdSettings.DiskTemp))
	];

	/// <summary>Drive temperatures are keyed "drive:C:" next to the metric keys.</summary>
	public const string DriveKeyPrefix = "drive:";

	/// <summary>Position in the reader's entries of what each widget and drive currently reads (-1: nothing).</summary>
	public static Dictionary<string, int> Matches(HwInfoReader reader, AppSettings settings) =>
		Metrics
			.Select(m => (m.Key, Index: reader.IndexOf(m.Read(settings.HwInfo))))
			.Concat(settings.Drives.Select(d => (DriveKeyPrefix + d.Name, Index: reader.IndexOf(d.Temp))))
			.ToDictionary(m => m.Item1, m => m.Index);

	/// <summary>
	/// Writes the choices of a page message: sensors { cpuTemp: { sensor, label } | null }, drives
	/// { "C:": { sensor, label } | null }. Keys left out keep their current selector (wildcards included).
	/// </summary>
	public static void ApplyChoices(JsonObject root, JsonElement message)
	{
		if (message.TryGetProperty("sensors", out var sensors) && sensors.ValueKind == JsonValueKind.Object)
		{
			if (root["HwInfo"] is not JsonObject hwInfo)
			{
				hwInfo = new JsonObject();
				root["HwInfo"] = hwInfo;
			}
			foreach (var (key, property, _) in Metrics)
			{
				if (sensors.TryGetProperty(key, out var choice))
				{
					hwInfo[property] = ToSelector(choice);
				}
			}
		}
		if (message.TryGetProperty("drives", out var drives) && drives.ValueKind == JsonValueKind.Object
			&& root["Drives"] is JsonArray driveNodes)
		{
			foreach (var choice in drives.EnumerateObject())
			{
				var node = driveNodes.OfType<JsonObject>().FirstOrDefault(d =>
					string.Equals(d["Name"]?.GetValue<string>(), choice.Name, StringComparison.OrdinalIgnoreCase));
				if (node is not null)
				{
					node["Temp"] = ToSelector(choice.Value);
				}
			}
		}
	}

	/// <summary>Writes thresholds { cpuTemp: { min, max, warm, hot } }; incoherent levels are refused.</summary>
	public static void ApplyThresholds(JsonObject root, JsonElement message)
	{
		if (!message.TryGetProperty("thresholds", out var thresholds) || thresholds.ValueKind != JsonValueKind.Object)
		{
			return;
		}
		if (root["Thresholds"] is not JsonObject node)
		{
			node = new JsonObject();
			root["Thresholds"] = node;
		}
		foreach (var (key, property) in ThresholdKeys)
		{
			if (thresholds.TryGetProperty(key, out var value)
				&& value.Deserialize<Threshold>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) is { } threshold
				&& threshold.Min < threshold.Max && threshold.Warm <= threshold.Hot)
			{
				node[property] = JsonSerializer.SerializeToNode(threshold);
			}
		}
	}

	/// <summary>
	/// Writes the weather settings: weather { enabled?, place?, latitude?, longitude? } (the place only
	/// together with both coordinates).
	/// </summary>
	public static void ApplyWeather(JsonObject root, JsonElement message)
	{
		if (!message.TryGetProperty("weather", out var weather) || weather.ValueKind != JsonValueKind.Object)
		{
			return;
		}
		if (root["Weather"] is not JsonObject node)
		{
			node = new JsonObject();
			root["Weather"] = node;
		}
		if (weather.TryGetProperty("enabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
		{
			node["Enabled"] = enabled.GetBoolean();
		}
		if (weather.TryGetProperty("place", out var place) && place.ValueKind == JsonValueKind.String
			&& weather.TryGetProperty("latitude", out var latitude) && latitude.TryGetDouble(out var lat)
			&& weather.TryGetProperty("longitude", out var longitude) && longitude.TryGetDouble(out var lon))
		{
			node["Place"] = place.GetString();
			node["Latitude"] = lat;
			node["Longitude"] = lon;
		}
	}

	/// <summary>
	/// Writes the user's actions: customActions [{ id, name, kind: url | script, target }] (the whole list,
	/// entries that do not hold together left out).
	/// </summary>
	public static void ApplyCustomActions(JsonObject root, JsonElement message)
	{
		if (!message.TryGetProperty("customActions", out var actions) || actions.ValueKind != JsonValueKind.Array)
		{
			return;
		}
		var list = new JsonArray();
		var ids = new HashSet<string>(StringComparer.Ordinal);
		foreach (var action in actions.EnumerateArray())
		{
			if (action.ValueKind != JsonValueKind.Object || list.Count >= CustomAction.Max)
			{
				continue;
			}
			string? Read(string name) => action.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
			var id = Read("id");
			var name = Read("name");
			var kind = Read("kind");
			var target = Read("target");
			var valid = !string.IsNullOrEmpty(id) && id.Length <= 32 && id.All(char.IsAsciiLetterOrDigit) && ids.Add(id)
				&& !string.IsNullOrEmpty(name) && name.Length <= 40
				&& !string.IsNullOrEmpty(target) && target.Length <= 2048
				&& (kind == CustomAction.Url && Uri.TryCreate(target, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
					|| kind == CustomAction.Script && target.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) && Path.IsPathFullyQualified(target));
			if (valid)
			{
				list.Add(new JsonObject { ["Id"] = id, ["Name"] = name, ["Kind"] = kind, ["Target"] = target });
			}
		}
		root["CustomActions"] = list;
	}

	/// <summary>Writes the display options set on the case screen: cpuDetails (bool).</summary>
	public static void ApplyDisplay(JsonObject root, JsonElement message)
	{
		if (message.TryGetProperty("cpuDetails", out var cpuDetails) && cpuDetails.ValueKind is JsonValueKind.True or JsonValueKind.False)
		{
			root["CpuDetails"] = cpuDetails.GetBoolean();
		}
	}

	/// <summary>
	/// Writes the electricity cost settings: energy { price?, otherWatts?, efficiency?, show?, cpuPercent?,
	/// gpuPercent?, components?: { motherboard?, memory?, … } }. Detailed parts also rewrite OtherWatts (their sum).
	/// </summary>
	public static void ApplyEnergy(JsonObject root, JsonElement message)
	{
		if (!message.TryGetProperty("energy", out var energy) || energy.ValueKind != JsonValueKind.Object)
		{
			return;
		}
		if (root["Energy"] is not JsonObject node)
		{
			node = new JsonObject();
			root["Energy"] = node;
		}
		if (energy.TryGetProperty("price", out var price) && price.TryGetDouble(out var perKwh) && double.IsFinite(perKwh))
		{
			node["PricePerKwh"] = Math.Round(Math.Clamp(perKwh, 0, EnergySettings.MaxPrice), 4);
		}
		if (energy.TryGetProperty("otherWatts", out var other) && other.TryGetInt32(out var watts))
		{
			node["OtherWatts"] = Math.Clamp(watts, 0, EnergySettings.MaxOtherWatts);
		}
		if (energy.TryGetProperty("efficiency", out var efficiency) && efficiency.TryGetInt32(out var percent))
		{
			node["PsuEfficiency"] = Math.Clamp(percent, EnergySettings.MinEfficiency, EnergySettings.MaxEfficiency);
		}
		if (energy.TryGetProperty("show", out var show) && show.ValueKind is JsonValueKind.True or JsonValueKind.False)
		{
			node["Show"] = show.GetBoolean();
		}
		foreach (var (key, name) in new[] { ("cpuPercent", "CpuPercent"), ("gpuPercent", "GpuPercent") })
		{
			if (energy.TryGetProperty(key, out var correction) && correction.TryGetInt32(out var value))
			{
				node[name] = Math.Clamp(value, EnergySettings.MinCorrection, EnergySettings.MaxCorrection);
			}
		}
		if (energy.TryGetProperty("components", out var components) && components.ValueKind == JsonValueKind.Object)
		{
			if (node["Components"] is not JsonObject parts)
			{
				parts = new JsonObject();
				node["Components"] = parts;
			}
			foreach (var name in EnergyComponents.Names)
			{
				if (components.TryGetProperty(JsonNamingPolicy.CamelCase.ConvertName(name), out var part) && part.TryGetInt32(out var partWatts))
				{
					parts[name] = Math.Clamp(partWatts, 0, EnergyComponents.Max);
				}
			}
			// Kept in step for older versions, which only know the flat allowance
			// (parts left out keep their default, as when the settings are read)
			node["OtherWatts"] = (parts.Deserialize<EnergyComponents>() ?? new EnergyComponents()).Clamped().Sum();
		}
	}

	/// <summary>
	/// Writes the game view settings: gameView { autoGame?, autoNormal?, delaySec?, transition?, detectEngines?,
	/// extraGames?: [{ exe, name }] }.
	/// </summary>
	public static void ApplyGameView(JsonObject root, JsonElement message)
	{
		if (!message.TryGetProperty("gameView", out var gameView) || gameView.ValueKind != JsonValueKind.Object)
		{
			return;
		}
		if (root["GameView"] is not JsonObject node)
		{
			node = new JsonObject();
			root["GameView"] = node;
		}
		if (gameView.TryGetProperty("autoGame", out var autoGame) && autoGame.ValueKind is JsonValueKind.True or JsonValueKind.False)
		{
			node["AutoGame"] = autoGame.GetBoolean();
		}
		if (gameView.TryGetProperty("autoNormal", out var autoNormal) && autoNormal.ValueKind is JsonValueKind.True or JsonValueKind.False)
		{
			node["AutoNormal"] = autoNormal.GetBoolean();
		}
		if (gameView.TryGetProperty("delaySec", out var delay) && delay.TryGetInt32(out var seconds))
		{
			node["AutoNormalDelaySec"] = Math.Clamp(seconds, GameViewSettings.MinDelaySec, GameViewSettings.MaxDelaySec);
		}
		if (gameView.TryGetProperty("transition", out var transition) && transition.ValueKind == JsonValueKind.String
			&& GameViewSettings.Transitions.Contains(transition.GetString()))
		{
			node["Transition"] = transition.GetString();
		}
		if (gameView.TryGetProperty("detectEngines", out var engines) && engines.ValueKind is JsonValueKind.True or JsonValueKind.False)
		{
			node["DetectEngines"] = engines.GetBoolean();
		}
		if (gameView.TryGetProperty("extraGames", out var extraGames) && extraGames.ValueKind == JsonValueKind.Array)
		{
			var games = new JsonArray();
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var game in extraGames.EnumerateArray())
			{
				// A bare executable name: the match must survive the game moving folders
				var exe = game.ValueKind == JsonValueKind.Object && game.TryGetProperty("exe", out var exeElement) ? exeElement.GetString()?.Trim() : null;
				if (string.IsNullOrEmpty(exe) || exe.Length > 260 || exe.IndexOfAny(['\\', '/', ':']) >= 0
					|| !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !seen.Add(exe) || games.Count >= GameViewSettings.MaxExtraGames)
				{
					continue;
				}
				var name = game.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String ? nameElement.GetString()!.Trim() : string.Empty;
				games.Add(new JsonObject { ["Exe"] = exe, ["Name"] = name.Length > 80 ? name[..80] : name });
			}
			node["ExtraGames"] = games;
		}
	}

	/// <summary>layout: { normal: [{ id, show }], game: [...] } as the Layout step sends it; null when absent.</summary>
	public static LayoutSettings? ReadLayout(JsonElement message)
	{
		if (!message.TryGetProperty("layout", out var layout) || layout.ValueKind != JsonValueKind.Object)
		{
			return null;
		}
		return new LayoutSettings(
			LayoutSettings.Normalize(ReadBands(layout, "normal"), LayoutSettings.NormalBands),
			LayoutSettings.Normalize(ReadBands(layout, "game"), LayoutSettings.GameBands));
	}

	private static IEnumerable<LayoutBand> ReadBands(JsonElement layout, string view)
	{
		if (!layout.TryGetProperty(view, out var bands) || bands.ValueKind != JsonValueKind.Array)
		{
			yield break;
		}
		foreach (var band in bands.EnumerateArray())
		{
			if (band.ValueKind == JsonValueKind.Object
				&& band.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
			{
				var show = !band.TryGetProperty("show", out var shown) || shown.ValueKind != JsonValueKind.False;
				yield return new LayoutBand(id.GetString()!, show);
			}
		}
	}

	/// <summary>
	/// home: { enabled, url, groups: [{ name, columns, tiles: [{ id, span }] }] } from the Maison step; null when
	/// absent. Ranges and duplicates are settled by <see cref="HomeSettings.EffectiveGroups"/>. The token travels
	/// beside it ("homeToken") and is stored encrypted, never here.
	/// </summary>
	public static HomeSettings? ReadHome(JsonElement message)
	{
		if (!message.TryGetProperty("home", out var home) || home.ValueKind != JsonValueKind.Object)
		{
			return null;
		}
		var url = home.TryGetProperty("url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String ? urlElement.GetString()!.Trim() : string.Empty;
		if (url.Length > 300 || (url.Length > 0 && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")))
		{
			url = string.Empty;
		}
		var groups = new List<HomeGroup>();
		if (home.TryGetProperty("groups", out var list) && list.ValueKind == JsonValueKind.Array)
		{
			foreach (var group in list.EnumerateArray())
			{
				if (group.ValueKind != JsonValueKind.Object)
				{
					continue;
				}
				var name = group.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String ? nameElement.GetString()! : string.Empty;
				var columns = group.TryGetProperty("columns", out var columnsElement) && columnsElement.TryGetInt32(out var c) ? c : HomeSettings.MaxColumns;
				var tiles = new List<HomeTile>();
				if (group.TryGetProperty("tiles", out var tileList) && tileList.ValueKind == JsonValueKind.Array)
				{
					foreach (var tile in tileList.EnumerateArray())
					{
						var id = tile.ValueKind == JsonValueKind.Object && tile.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String ? idElement.GetString()! : string.Empty;
						// domain.object_id, as Home Assistant writes them
						if (id.Length is 0 or > 255 || id.Count(ch => ch == '.') != 1)
						{
							continue;
						}
						var span = tile.TryGetProperty("span", out var spanElement) && spanElement.TryGetInt32(out var sp) ? sp : 1;
						// An icon name of js/home-icons.js (lower case and dashes), or none for the automatic one
						var icon = tile.TryGetProperty("icon", out var iconElement) && iconElement.ValueKind == JsonValueKind.String ? iconElement.GetString() : null;
						if (icon is not null && (icon.Length > 40 || !icon.All(ch => ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')))
						{
							icon = null;
						}
						tiles.Add(new HomeTile(id, span, string.IsNullOrEmpty(icon) ? null : icon));
					}
				}
				// A separator ({ separator: height, line }) carries no name or tiles
				int? separator = group.TryGetProperty("separator", out var separatorElement) && separatorElement.TryGetInt32(out var height) ? height : null;
				var line = group.TryGetProperty("line", out var lineElement) && lineElement.ValueKind == JsonValueKind.True;
				groups.Add(separator is null ? new HomeGroup(name, columns, tiles) : new HomeGroup(string.Empty, 1, [], separator, line));
			}
		}
		var enabled = home.TryGetProperty("enabled", out var enabledElement) && enabledElement.ValueKind == JsonValueKind.True;
		return new HomeSettings(enabled, url, new HomeSettings(Groups: groups).EffectiveGroups);
	}

	public static void ApplyHome(JsonObject root, JsonElement message)
	{
		if (ReadHome(message) is { } home)
		{
			root["Home"] = JsonSerializer.SerializeToNode(home);
		}
	}

	public static void ApplyLayout(JsonObject root, JsonElement message)
	{
		if (ReadLayout(message) is { } layout)
		{
			root["Layout"] = JsonSerializer.SerializeToNode(layout);
		}
	}

	private static JsonObject? ToSelector(JsonElement choice) =>
		choice.ValueKind == JsonValueKind.Object
			? new JsonObject
			{
				["Sensor"] = choice.GetProperty("sensor").GetString(),
				["Label"] = choice.GetProperty("label").GetString()
			}
			: null;
}
