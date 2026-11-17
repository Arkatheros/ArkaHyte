using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ArkaHyte.Home;

/// <summary>Entity offered in the settings: its id, name, room (area id and name) and current state.</summary>
public sealed record HomeCatalogEntity(string Id, string Name, string Domain, string? Area, string? AreaName, string State, string? Unit, string? DeviceClass);

/// <summary>What the settings window learns from a server: version, its entities shown on the page, the error otherwise.</summary>
public sealed record HomeCatalogResult(bool Ok, string? Version = null, IReadOnlyList<HomeCatalogEntity>? Entities = null, int Areas = 0, string? Error = null);

/// <summary>
/// One-off connection for the settings window: checks the address and token, lists the entities the page can
/// show (domains it knows), each with its room: its own area, or else its device's.
/// </summary>
public static class HomeCatalog
{
	/// <summary>Domains the page has a tile for.</summary>
	public static readonly string[] Domains = ["light", "switch", "fan", "input_boolean", "cover", "climate", "sensor", "binary_sensor", "scene", "script"];

	public static async Task<HomeCatalogResult> ReadAsync(string url, string token)
	{
		try
		{
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
			await using var socket = await HomeSocket.ConnectAsync(url, token, timeout.Token);
			var states = await socket.RequestAsync(new JsonObject { ["type"] = "get_states" }, timeout.Token);
			var areas = await socket.RequestAsync(new JsonObject { ["type"] = "config/area_registry/list" }, timeout.Token);
			var registry = await socket.RequestAsync(new JsonObject { ["type"] = "config/entity_registry/list" }, timeout.Token);
			var devices = await socket.RequestAsync(new JsonObject { ["type"] = "config/device_registry/list" }, timeout.Token);

			var areaNames = new Dictionary<string, string>();
			foreach (var area in areas.EnumerateArray())
			{
				if (Text(area, "area_id") is { } id && Text(area, "name") is { } name)
				{
					areaNames[id] = name;
				}
			}
			var deviceAreas = new Dictionary<string, string>();
			foreach (var device in devices.EnumerateArray())
			{
				if (Text(device, "id") is { } id && Text(device, "area_id") is { } area)
				{
					deviceAreas[id] = area;
				}
			}
			var entityAreas = new Dictionary<string, string>();
			foreach (var entry in registry.EnumerateArray())
			{
				if (Text(entry, "entity_id") is not { } id)
				{
					continue;
				}
				var area = Text(entry, "area_id") ?? (Text(entry, "device_id") is { } device && deviceAreas.TryGetValue(device, out var deviceArea) ? deviceArea : null);
				if (area is not null)
				{
					entityAreas[id] = area;
				}
			}

			var entities = new List<HomeCatalogEntity>();
			foreach (var state in states.EnumerateArray())
			{
				if (Text(state, "entity_id") is not { } id)
				{
					continue;
				}
				var domain = id[..Math.Max(0, id.IndexOf('.'))];
				if (!Domains.Contains(domain))
				{
					continue;
				}
				var attributes = state.TryGetProperty("attributes", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
				var area = entityAreas.GetValueOrDefault(id);
				entities.Add(new HomeCatalogEntity(
					id,
					(attributes.ValueKind == JsonValueKind.Object ? Text(attributes, "friendly_name") : null) ?? id,
					domain,
					area,
					area is not null ? areaNames.GetValueOrDefault(area) : null,
					Text(state, "state") ?? string.Empty,
					attributes.ValueKind == JsonValueKind.Object ? Text(attributes, "unit_of_measurement") : null,
					attributes.ValueKind == JsonValueKind.Object ? Text(attributes, "device_class") : null));
			}
			entities.Sort((x, y) =>
			{
				var byArea = string.Compare(x.AreaName ?? "￿", y.AreaName ?? "￿", StringComparison.CurrentCultureIgnoreCase);
				return byArea != 0 ? byArea : string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase);
			});
			return new HomeCatalogResult(true, socket.Version, entities, areaNames.Count);
		}
		catch (HomeAuthException)
		{
			return new HomeCatalogResult(false, Error: "unauthorized");
		}
		catch (UriFormatException)
		{
			return new HomeCatalogResult(false, Error: "address");
		}
		catch (Exception exception) when (exception is WebSocketException or IOException or OperationCanceledException or TimeoutException or InvalidOperationException or System.Net.Http.HttpRequestException)
		{
			Log.Error("Home Assistant catalog failed", exception);
			return new HomeCatalogResult(false, Error: "unreachable");
		}
	}

	private static string? Text(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
