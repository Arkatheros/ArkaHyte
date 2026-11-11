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
		("gpuFps", nameof(HwInfoSettings.GpuFps), s => s.GpuFps)
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

	private static JsonObject? ToSelector(JsonElement choice) =>
		choice.ValueKind == JsonValueKind.Object
			? new JsonObject
			{
				["Sensor"] = choice.GetProperty("sensor").GetString(),
				["Label"] = choice.GetProperty("label").GetString()
			}
			: null;
}
