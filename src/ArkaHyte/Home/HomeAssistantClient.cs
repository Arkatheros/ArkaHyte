using System.Text.Json;
using System.Text.Json.Nodes;

namespace ArkaHyte.Home;

/// <summary>
/// Keeps the Home Assistant page fed: one WebSocket connection, subscribed (subscribe_entities, the compressed
/// feed of Home Assistant's own frontend) to the entities shown only. Holds their state and posts the changes,
/// grouped over 150 ms, as one "home" message (JSON text, see <see cref="Updated"/>). Reconnects on its own
/// (2 s up to 60 s apart); a refused token stops it until the settings change. Runs on pool threads.
/// </summary>
public sealed class HomeAssistantClient : IDisposable
{
	private static readonly TimeSpan FlushDelay = TimeSpan.FromMilliseconds(150);
	private static readonly TimeSpan PingPeriod = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);

	/// <summary>Attributes the page reads; the others (often large: effect lists, colour tables) are dropped.</summary>
	private static readonly HashSet<string> KeptAttributes =
	[
		"friendly_name", "unit_of_measurement", "device_class", "brightness", "current_position", "percentage",
		"current_temperature", "temperature", "target_temp_step", "min_temp", "max_temp", "hvac_action", "preset_mode",
		// Long-press panels: what a light, a shutter or a heater can be set to
		"supported_color_modes", "color_mode", "min_color_temp_kelvin", "max_color_temp_kelvin", "color_temp_kelvin",
		"rgb_color", "hvac_modes", "preset_modes",
		// Effects of a light, and the one playing: a Nanoleaf's favourites (names only, no colours in Home Assistant)
		"effect_list", "effect",
		// A light group's members (a favourite is sent to each of them, see CallAsync)
		"entity_id"
	];

	/// <summary>Commands the page may send, per domain (anything else is refused).</summary>
	private static readonly Dictionary<string, string[]> AllowedServices = new()
	{
		["light"] = ["toggle", "turn_on", "turn_off"],
		["switch"] = ["toggle", "turn_on", "turn_off"],
		["fan"] = ["toggle", "turn_on", "turn_off"],
		["input_boolean"] = ["toggle", "turn_on", "turn_off"],
		["cover"] = ["toggle", "open_cover", "close_cover", "stop_cover", "set_cover_position"],
		["climate"] = ["set_temperature", "set_hvac_mode", "set_preset_mode"],
		["scene"] = ["turn_on"],
		["script"] = ["turn_on"]
	};

	private readonly object _gate = new();
	private readonly Dictionary<string, EntityState> _states = new();
	private readonly HashSet<string> _changed = new();
	private readonly HashSet<string> _removed = new();
	private string _status = "off";
	private string _version = string.Empty;
	private bool _fullPending = true;
	private bool _paused;
	private System.Threading.Timer? _flushTimer;
	private CancellationTokenSource? _run;
	private HomeSocket? _socket;
	private string _configKey = string.Empty;
	private HashSet<string> _entityIds = new();
	// Lights of the light groups shown, followed too (their favourites, the one playing) and allowed as targets
	private readonly HashSet<string> _memberIds = new();

	/// <summary>A "home" message for the page, as JSON text; raised on a pool thread.</summary>
	public event Action<string>? Updated;

	private sealed class EntityState
	{
		public string State = string.Empty;
		public JsonObject Attributes = new();
		public double Changed;
	}

	/// <summary>Starts, restarts or stops the connection for these settings (same settings: nothing happens).</summary>
	public void Configure(HomeSettings? settings, string? token)
	{
		// Sorted: the page's order (groups moved in the settings) does not change what is followed, nor reconnects
		var ids = (settings?.EntityIds ?? []).Order(StringComparer.Ordinal).ToList();
		var enabled = settings is { Enabled: true } && !string.IsNullOrWhiteSpace(settings.Url) && !string.IsNullOrEmpty(token) && ids.Count > 0;
		// The token is part of the key without being kept in clear anywhere else
		var key = enabled ? $"{settings!.Url}\n{token!.GetHashCode()}\n{string.Join(',', ids)}" : string.Empty;
		if (key == _configKey)
		{
			return;
		}
		_configKey = key;
		_run?.Cancel();
		_run = null;
		lock (_gate)
		{
			_states.Clear();
			_changed.Clear();
			_removed.Clear();
			_entityIds = ids.ToHashSet();
			_fullPending = true;
		}
		if (!enabled)
		{
			SetStatus("off");
			return;
		}
		var run = new CancellationTokenSource();
		_run = run;
		_ = Task.Run(() => RunAsync(settings!.Url, token!, ids, run.Token));
	}

	/// <summary>Screens off or session locked: nothing is posted; everything is sent again when it resumes.</summary>
	public bool Paused
	{
		get => _paused;
		set
		{
			_paused = value;
			if (!value)
			{
				lock (_gate)
				{
					_fullPending = true;
				}
				ScheduleFlush();
			}
		}
	}

	/// <summary>The page (re)loaded: it gets the whole state again.</summary>
	public void SendAll()
	{
		lock (_gate)
		{
			_fullPending = true;
		}
		ScheduleFlush();
	}

	/// <summary>
	/// Runs a command from the page on one of the entities shown: { domain, service, entityId, data }.
	/// False when it is not allowed, not connected or refused by Home Assistant.
	/// </summary>
	public async Task<bool> CallAsync(string domain, string service, string entityId, JsonObject? data)
	{
		if (!AllowedServices.TryGetValue(domain, out var services) || !services.Contains(service)
			|| !entityId.StartsWith(domain + ".", StringComparison.Ordinal) || !(_entityIds.Contains(entityId) || IsMember(entityId)) || _socket is not { } socket)
		{
			return false;
		}
		// The state shown when the command left (the reply comes after the new state); a light group's members
		string seen;
		List<string> members;
		lock (_gate)
		{
			seen = _states.TryGetValue(entityId, out var state) ? state.State : "?";
			members = state?.Attributes["entity_id"] is JsonArray list
				? list.Select(node => node?.GetValue<string>()).OfType<string>().Where(id => id.StartsWith("light.", StringComparison.Ordinal)).ToList()
				: [];
		}
		// A favourite (effect) on a light group: Home Assistant passes it to every member and fails on those that do
		// not have it, after the others applied it. Sent member by member instead: those without it are only lit.
		if (domain == "light" && service == "turn_on" && data?["effect"] is not null && members.Count > 0)
		{
			return await CallEffectOnGroupAsync(socket, entityId, members, data);
		}
		try
		{
			await socket.RequestAsync(CallMessage(domain, service, entityId, data));
			Log.Info($"Home Assistant {domain}.{service} on {entityId} (state shown before: {seen})");
			return true;
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
		{
			Log.Error($"Home Assistant command {domain}.{service} on {entityId} failed", exception);
			return false;
		}
	}

	private static JsonObject CallMessage(string domain, string service, string entityId, JsonObject? data)
	{
		var message = new JsonObject
		{
			["type"] = "call_service",
			["domain"] = domain,
			["service"] = service,
			["target"] = new JsonObject { ["entity_id"] = entityId }
		};
		if (data is not null)
		{
			message["service_data"] = data.DeepClone();
		}
		return message;
	}

	/// <summary>A favourite to each light of a group: true when at least one took it; the others are turned on plainly.</summary>
	private static async Task<bool> CallEffectOnGroupAsync(HomeSocket socket, string groupId, List<string> members, JsonObject data)
	{
		var plain = data.DeepClone().AsObject();
		plain.Remove("effect");
		var applied = new List<string>();
		foreach (var member in members)
		{
			try
			{
				await socket.RequestAsync(CallMessage("light", "turn_on", member, data));
				applied.Add(member);
			}
			catch (InvalidOperationException)
			{
				// Not one of its effects: lit with the rest of the data (brightness...), never left off
				try
				{
					await socket.RequestAsync(CallMessage("light", "turn_on", member, plain.Count > 0 ? plain : null));
				}
				catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
				{
					Log.Error($"Home Assistant light.turn_on on {member} failed", exception);
				}
			}
			catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
			{
				Log.Error($"Home Assistant light.turn_on on {member} failed", exception);
			}
		}
		Log.Info($"Home Assistant effect '{data["effect"]}' on group {groupId}: taken by {(applied.Count > 0 ? string.Join(", ", applied) : "none")}");
		return applied.Count > 0;
	}

	/// <summary>
	/// Last 24 hours of a numeric sensor shown on the page, as a "homeHistory" message (JSON text): one point per
	/// 5 minutes at most (the last value of each slot), times in Unix seconds. Null when it cannot be read.
	/// </summary>
	public async Task<string?> HistoryAsync(string entityId)
	{
		if (!entityId.StartsWith("sensor.", StringComparison.Ordinal) || !_entityIds.Contains(entityId) || _socket is not { } socket)
		{
			return null;
		}
		var end = DateTimeOffset.UtcNow;
		var start = end.AddHours(-24);
		try
		{
			var result = await socket.RequestAsync(new JsonObject
			{
				["type"] = "history/history_during_period",
				["start_time"] = start.ToString("o"),
				["end_time"] = end.ToString("o"),
				["entity_ids"] = new JsonArray(entityId),
				["minimal_response"] = true,
				["no_attributes"] = true,
				["significant_changes_only"] = false
			});
			const int SlotSeconds = 300;
			var startSeconds = start.ToUnixTimeSeconds();
			var slots = new SortedDictionary<long, double>();
			if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty(entityId, out var states) && states.ValueKind == JsonValueKind.Array)
			{
				foreach (var item in states.EnumerateArray())
				{
					// Compressed rows: s (state), lu (last updated) or lc (last changed), Unix seconds
					if (!item.TryGetProperty("s", out var s) || !double.TryParse(s.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
					{
						continue;
					}
					var time = item.TryGetProperty("lu", out var lu) && lu.TryGetDouble(out var updated) ? updated
						: item.TryGetProperty("lc", out var lc) && lc.TryGetDouble(out var changed) ? changed : startSeconds;
					var slot = Math.Max(startSeconds, (long)time) / SlotSeconds * SlotSeconds;
					slots[slot] = value;
				}
			}
			var points = new JsonArray();
			foreach (var (slot, value) in slots)
			{
				points.Add(new JsonArray(slot, Math.Round(value, 3)));
			}
			return new JsonObject
			{
				["type"] = "homeHistory",
				["entityId"] = entityId,
				["start"] = startSeconds,
				["end"] = end.ToUnixTimeSeconds(),
				["points"] = points
			}.ToJsonString();
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or OperationCanceledException or FormatException)
		{
			Log.Error($"Home Assistant history of {entityId} failed", exception);
			return null;
		}
	}

	private async Task RunAsync(string url, string token, List<string> ids, CancellationToken cancellation)
	{
		var delay = TimeSpan.FromSeconds(2);
		while (!cancellation.IsCancellationRequested)
		{
			SetStatus("connecting");
			try
			{
				await using var socket = await HomeSocket.ConnectAsync(url, token, cancellation);
				delay = TimeSpan.FromSeconds(2);
				_version = socket.Version;
				lock (_gate)
				{
					_states.Clear();
					_memberIds.Clear();
					_fullPending = true;
				}
				var entityIds = new JsonArray();
				ids.ForEach(id => entityIds.Add(id));
				await socket.SubscribeAsync(new JsonObject { ["type"] = "subscribe_entities", ["entity_ids"] = entityIds }, OnEntities, cancellation);
				_socket = socket;
				Log.Info($"Home Assistant {socket.Version} connected, {ids.Count} entities followed");
				SetStatus("connected");
				// Alive as long as it answers a ping
				while (!cancellation.IsCancellationRequested)
				{
					await Task.WhenAny(socket.Closed, Task.Delay(PingPeriod, cancellation));
					if (socket.Closed.IsCompleted)
					{
						break;
					}
					await socket.RequestAsync(new JsonObject { ["type"] = "ping" }, cancellation);
				}
			}
			catch (HomeAuthException)
			{
				_socket = null;
				Log.Info("Home Assistant refused the access token");
				SetStatus("unauthorized");
				return;
			}
			catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
			{
				_socket = null;
				return;
			}
			catch (Exception exception)
			{
				Log.Error("Home Assistant connection failed", exception);
			}
			_socket = null;
			if (cancellation.IsCancellationRequested)
			{
				return;
			}
			SetStatus("offline");
			try
			{
				await Task.Delay(delay, cancellation);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
		}
	}

	private bool IsMember(string entityId)
	{
		lock (_gate)
		{
			return _memberIds.Contains(entityId);
		}
	}

	/// <summary>Lights of the groups shown not followed yet: followed now, on a subscription of their own.</summary>
	private void FollowNewMembers()
	{
		List<string> fresh;
		lock (_gate)
		{
			fresh = _states.Values
				.SelectMany(state => state.Attributes["entity_id"] is JsonArray list ? list.Select(node => node?.GetValue<string>()) : Enumerable.Empty<string?>())
				.OfType<string>()
				.Where(id => id.StartsWith("light.", StringComparison.Ordinal) && !_entityIds.Contains(id) && !_memberIds.Contains(id))
				.Distinct()
				.ToList();
			fresh.ForEach(id => _memberIds.Add(id));
		}
		if (fresh.Count > 0)
		{
			_ = Task.Run(() => FollowMembersAsync(fresh));
		}
	}

	private async Task FollowMembersAsync(List<string> ids)
	{
		// The first states come right after the main subscription: the socket is published a moment later
		for (var i = 0; i < 50 && _socket is null; i++)
		{
			await Task.Delay(100);
		}
		if (_socket is not { } socket)
		{
			return;
		}
		try
		{
			var entityIds = new JsonArray();
			ids.ForEach(id => entityIds.Add(id));
			await socket.SubscribeAsync(new JsonObject { ["type"] = "subscribe_entities", ["entity_ids"] = entityIds }, OnEntities);
			Log.Info($"Home Assistant group members followed: {string.Join(", ", ids)}");
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
		{
			Log.Error("Home Assistant group members not followed", exception);
		}
	}

	/// <summary>
	/// subscribe_entities event: a (added: id → { s, a, lc, lu }), c (changed: id → { "+": { s, a, lc, lu },
	/// "-": { a: [names] } }), r (removed ids). Times are Unix seconds; lc is left out when equal to lu.
	/// </summary>
	private void OnEntities(JsonElement change)
	{
		OnEntitiesChanged(change);
		FollowNewMembers();
	}

	private void OnEntitiesChanged(JsonElement change)
	{
		lock (_gate)
		{
			if (change.TryGetProperty("a", out var added))
			{
				var received = new List<string>();
				foreach (var entity in added.EnumerateObject())
				{
					var state = new EntityState();
					Apply(state, entity.Value);
					_states[entity.Name] = state;
					_changed.Add(entity.Name);
					_removed.Remove(entity.Name);
					received.Add($"{entity.Name}={state.State}");
				}
				// What the page starts from (a tile showing a state the device contradicts can be told apart)
				Log.Info($"Home Assistant states: {string.Join(", ", received)}");
			}
			if (change.TryGetProperty("c", out var changed))
			{
				foreach (var entity in changed.EnumerateObject())
				{
					if (!_states.TryGetValue(entity.Name, out var state))
					{
						continue;
					}
					if (entity.Value.TryGetProperty("+", out var plus))
					{
						Apply(state, plus);
					}
					if (entity.Value.TryGetProperty("-", out var minus) && minus.TryGetProperty("a", out var gone))
					{
						foreach (var name in gone.EnumerateArray())
						{
							state.Attributes.Remove(name.GetString() ?? string.Empty);
						}
					}
					_changed.Add(entity.Name);
				}
			}
			if (change.TryGetProperty("r", out var removed))
			{
				foreach (var id in removed.EnumerateArray())
				{
					if (id.GetString() is { } name && _states.Remove(name))
					{
						_changed.Remove(name);
						_removed.Add(name);
					}
				}
			}
		}
		ScheduleFlush();
	}

	private static void Apply(EntityState state, JsonElement values)
	{
		if (values.TryGetProperty("s", out var s))
		{
			state.State = s.ValueKind == JsonValueKind.String ? s.GetString() ?? string.Empty : s.ToString();
		}
		if (values.TryGetProperty("a", out var attributes) && attributes.ValueKind == JsonValueKind.Object)
		{
			foreach (var attribute in attributes.EnumerateObject())
			{
				if (KeptAttributes.Contains(attribute.Name))
				{
					state.Attributes[attribute.Name] = JsonNode.Parse(attribute.Value.GetRawText());
				}
			}
		}
		if (values.TryGetProperty("lc", out var changed) && changed.TryGetDouble(out var lastChanged))
		{
			state.Changed = lastChanged;
		}
		else if (values.TryGetProperty("lu", out var updated) && updated.TryGetDouble(out var lastUpdated) && values.TryGetProperty("s", out _))
		{
			// A new state without lc: it changed at that update
			state.Changed = lastUpdated;
		}
	}

	private void SetStatus(string status)
	{
		if (_status == status)
		{
			return;
		}
		_status = status;
		lock (_gate)
		{
			// The page redraws everything on a status change (greyed or live)
			_fullPending = true;
		}
		ScheduleFlush();
	}

	private void ScheduleFlush()
	{
		lock (_gate)
		{
			_flushTimer ??= new System.Threading.Timer(_ => Flush());
			_flushTimer.Change(FlushDelay, Timeout.InfiniteTimeSpan);
		}
	}

	private void Flush()
	{
		if (_paused)
		{
			return;
		}
		string json;
		lock (_gate)
		{
			var full = _fullPending;
			if (!full && _changed.Count == 0 && _removed.Count == 0)
			{
				return;
			}
			var entities = new JsonObject();
			foreach (var id in full ? (IEnumerable<string>)_states.Keys : _changed)
			{
				if (_states.TryGetValue(id, out var state))
				{
					entities[id] = new JsonObject
					{
						["state"] = state.State,
						["attrs"] = state.Attributes.DeepClone(),
						["changed"] = state.Changed
					};
				}
			}
			var message = new JsonObject
			{
				["type"] = "home",
				["status"] = _status,
				["version"] = _version,
				["full"] = full,
				["entities"] = entities,
				["removed"] = new JsonArray(_removed.Select(id => (JsonNode?)id).ToArray())
			};
			_fullPending = false;
			_changed.Clear();
			_removed.Clear();
			json = message.ToJsonString();
		}
		Updated?.Invoke(json);
	}

	public void Dispose()
	{
		_run?.Cancel();
		lock (_gate)
		{
			_flushTimer?.Dispose();
			_flushTimer = null;
		}
	}
}
