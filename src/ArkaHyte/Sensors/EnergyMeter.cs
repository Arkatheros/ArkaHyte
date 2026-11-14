using System.Diagnostics;
using System.Text.Json;

namespace ArkaHyte.Sensors;

/// <summary>
/// Electricity used by the PC, integrated once per sample from the CPU and GPU power plus a flat
/// allowance for the rest of the machine, divided by the power supply efficiency. Kept per day in
/// %LOCALAPPDATA%\ArkaHyte\energy.json (survives restarts); a new day starts at midnight.
/// Costs are worked out by the page with the current price, so a price change applies to the history too.
/// </summary>
public sealed class EnergyMeter
{
	// A longer gap between two samples is sleep, hibernation or a stall: not counted
	private const double MaxStepSeconds = 5;
	private const int KeepDays = 400;
	private const int HistoryDays = 14;
	private static readonly TimeSpan SaveEvery = TimeSpan.FromMinutes(1);
	private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

	private readonly object _lock = new();
	private readonly SortedDictionary<DateOnly, DayEnergy> _days = [];
	private SessionEnergy? _session;
	private long _lastSample;
	private DateTime _lastSave = DateTime.UtcNow;
	private bool _dirty;

	public static string FilePath => Path.Combine(DataFolder.Root, "energy.json");

	public EnergyMeter() => Load();

	/// <summary>
	/// Counts the time since the previous sample at today's power. <paramref name="game"/> is the tracked
	/// game (null: none, its session ends); <paramref name="keepSession"/> leaves the session as it is
	/// (samples taken while the display sleeps do not know the game).
	/// </summary>
	public EnergyData Add(double? cpuWatts, double? gpuWatts, string? game, bool keepSession, EnergySettings settings)
	{
		var now = Stopwatch.GetTimestamp();
		var seconds = _lastSample == 0 ? 0 : Stopwatch.GetElapsedTime(_lastSample, now).TotalSeconds;
		_lastSample = now;
		if (seconds > MaxStepSeconds)
		{
			seconds = 0;
		}

		// Without HWiNFO there is nothing to estimate from: the allowance alone would be a made-up figure
		double? total = null;
		double loss = 0;
		if (cpuWatts is not null || gpuWatts is not null)
		{
			var direct = (cpuWatts ?? 0) + (gpuWatts ?? 0) + settings.OtherWatts;
			total = direct * 100 / settings.PsuEfficiency;
			loss = total.Value - direct;
		}

		lock (_lock)
		{
			if (!keepSession)
			{
				TrackSession(game);
			}
			if (total is { } watts && seconds > 0)
			{
				var kwh = watts * seconds / 3_600_000;
				var day = Today();
				day.Kwh += kwh;
				day.Seconds += seconds;
				if (_session is { Active: true } session)
				{
					session.Kwh += kwh;
					session.Seconds += seconds;
				}
				_dirty = true;
			}
			if (_dirty && DateTime.UtcNow - _lastSave >= SaveEvery)
			{
				SaveLocked();
			}
			var today = Today();
			return new EnergyData(
				today.Kwh,
				today.Seconds,
				total,
				cpuWatts,
				gpuWatts,
				total is null ? null : settings.OtherWatts,
				total is null ? null : loss,
				_session is { } s ? new EnergySession(s.Name, s.Kwh, s.Seconds, s.Active) : null);
		}
	}

	/// <summary>Days before today for the detail panel (today comes live with each snapshot).</summary>
	public object History()
	{
		lock (_lock)
		{
			var today = DateOnly.FromDateTime(DateTime.Now);
			double Sum(DateOnly from, DateOnly to) => _days.Where(d => d.Key >= from && d.Key <= to).Sum(d => d.Value.Kwh);
			var monthStart = new DateOnly(today.Year, today.Month, 1);
			return new
			{
				type = "energy",
				today = today.ToString("yyyy-MM-dd"),
				// Oldest first, today excluded, days without data at zero
				days = Enumerable.Range(1, HistoryDays - 1).Reverse()
					.Select(back => today.AddDays(-back))
					.Select(date => new { date = date.ToString("yyyy-MM-dd"), kwh = _days.TryGetValue(date, out var day) ? day.Kwh : 0 }),
				yesterdayKwh = _days.TryGetValue(today.AddDays(-1), out var yesterday) ? yesterday.Kwh : (double?)null,
				weekKwh = Sum(today.AddDays(-6), today.AddDays(-1)),
				monthKwh = Sum(monthStart, today.AddDays(-1)),
				monthDays = _days.Keys.Count(d => d >= monthStart && d < today)
			};
		}
	}

	public void Save()
	{
		lock (_lock)
		{
			if (_dirty)
			{
				SaveLocked();
			}
		}
	}

	private void TrackSession(string? game)
	{
		if (game is null)
		{
			if (_session is { Active: true })
			{
				_session.Active = false;
				_dirty = true;
			}
			return;
		}
		if (_session is not { Active: true } || _session.Name != game)
		{
			_session = new SessionEnergy { Name = game, Active = true };
			_dirty = true;
		}
	}

	private DayEnergy Today()
	{
		var date = DateOnly.FromDateTime(DateTime.Now);
		if (!_days.TryGetValue(date, out var day))
		{
			day = new DayEnergy();
			_days[date] = day;
			while (_days.Count > KeepDays)
			{
				_days.Remove(_days.Keys.First());
			}
		}
		return day;
	}

	private void Load()
	{
		try
		{
			if (!File.Exists(FilePath))
			{
				return;
			}
			var file = JsonSerializer.Deserialize<EnergyFile>(File.ReadAllText(FilePath), JsonOptions);
			foreach (var (key, day) in file?.Days ?? [])
			{
				if (DateOnly.TryParseExact(key, "yyyy-MM-dd", out var date) && day is not null)
				{
					_days[date] = day;
				}
			}
			// A game still running when the app stopped: its session is over for the meter
			if (file?.Session is { } session)
			{
				session.Active = false;
				_session = session;
			}
		}
		catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
		{
			Log.Error("Ignoring unreadable energy.json", exception);
		}
	}

	private void SaveLocked()
	{
		try
		{
			var file = new EnergyFile
			{
				Days = _days.ToDictionary(d => d.Key.ToString("yyyy-MM-dd"), d => (DayEnergy?)d.Value),
				Session = _session
			};
			Directory.CreateDirectory(DataFolder.Root);
			var temp = FilePath + ".tmp";
			File.WriteAllText(temp, JsonSerializer.Serialize(file, JsonOptions));
			File.Move(temp, FilePath, overwrite: true);
			_dirty = false;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			Log.Error("Could not save energy.json", exception);
		}
		_lastSave = DateTime.UtcNow;
	}

	private sealed class EnergyFile
	{
		public Dictionary<string, DayEnergy?>? Days { get; set; }
		public SessionEnergy? Session { get; set; }
	}

	private sealed class DayEnergy
	{
		public double Kwh { get; set; }
		public double Seconds { get; set; }
	}

	private sealed class SessionEnergy
	{
		public string Name { get; set; } = "";
		public double Kwh { get; set; }
		public double Seconds { get; set; }
		public bool Active { get; set; }
	}
}
