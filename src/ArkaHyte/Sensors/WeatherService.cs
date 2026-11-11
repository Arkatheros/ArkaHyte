using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ArkaHyte.Sensors;

/// <summary>Current weather shown under the clock: WMO weather code, today's range.</summary>
public sealed record WeatherData(string Place, double Temperature, int Code, bool IsDay, double? Min, double? Max);

/// <summary>A place found by name, to pick the weather location in the setup wizard.</summary>
public sealed record WeatherPlace(string Name, string Detail, double Latitude, double Longitude);

/// <summary>
/// Weather from Open-Meteo (free, no account or key; data CC BY 4.0, credited in the README):
/// refreshed every 15 minutes while enabled, kept as is when a request fails. Events fire on pool threads.
/// </summary>
public sealed class WeatherService : IDisposable
{
	private static readonly TimeSpan Period = TimeSpan.FromMinutes(15);
	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

	private System.Threading.Timer? _timer;
	private WeatherSettings? _settings;

	/// <summary>Raised with each new reading, and with null when the weather is turned off.</summary>
	public event Action<WeatherData?>? Updated;

	public WeatherData? Latest { get; private set; }

	/// <summary>Applies the settings: starts, moves or stops the periodic refresh.</summary>
	public void Configure(WeatherSettings? settings)
	{
		_settings = settings;
		_timer?.Dispose();
		_timer = null;
		if (settings is not { Enabled: true, Latitude: not null, Longitude: not null })
		{
			Latest = null;
			Updated?.Invoke(null);
			return;
		}
		_timer = new System.Threading.Timer(_ => _ = RefreshAsync(), null, TimeSpan.Zero, Period);
	}

	private async Task RefreshAsync()
	{
		if (_settings is not { Latitude: { } latitude, Longitude: { } longitude } settings)
		{
			return;
		}
		const string Fields = "&current=temperature_2m,weather_code,is_day&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=1";
		var url = string.Create(CultureInfo.InvariantCulture, $"https://api.open-meteo.com/v1/forecast?latitude={latitude}&longitude={longitude}{Fields}");
		try
		{
			var forecast = await Http.GetFromJsonAsync<Forecast>(url);
			if (forecast?.Current is not { } current)
			{
				return;
			}
			Latest = new WeatherData(
				settings.Place,
				current.Temperature,
				current.WeatherCode,
				current.IsDay == 1,
				forecast.Daily?.Min?.FirstOrDefault(),
				forecast.Daily?.Max?.FirstOrDefault());
			Updated?.Invoke(Latest);
		}
		catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
		{
			// Offline or service down: the last reading stays on screen, the next period retries
			Log.Error("Weather refresh failed", exception);
		}
	}

	/// <summary>Places matching a name (Open-Meteo geocoding), in French.</summary>
	public static async Task<IReadOnlyList<WeatherPlace>> SearchAsync(string name)
	{
		var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(name)}&count=8&language=fr&format=json";
		var result = await Http.GetFromJsonAsync<Geocoding>(url);
		return (result?.Results ?? [])
			.Select(r => new WeatherPlace(
				r.Name,
				string.Join(", ", new[] { r.Admin1, r.Country }.Where(p => !string.IsNullOrWhiteSpace(p))),
				r.Latitude,
				r.Longitude))
			.ToList();
	}

	public void Dispose() => _timer?.Dispose();

	private sealed record Forecast(CurrentWeather? Current, DailyWeather? Daily);

	private sealed record CurrentWeather(
		[property: JsonPropertyName("temperature_2m")] double Temperature,
		[property: JsonPropertyName("weather_code")] int WeatherCode,
		[property: JsonPropertyName("is_day")] int IsDay);

	private sealed record DailyWeather(
		[property: JsonPropertyName("temperature_2m_min")] double[]? Min,
		[property: JsonPropertyName("temperature_2m_max")] double[]? Max);

	private sealed record Geocoding(GeocodingResult[]? Results);

	private sealed record GeocodingResult(string Name, double Latitude, double Longitude, string? Country, string? Admin1);
}
