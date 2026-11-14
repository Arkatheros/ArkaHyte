using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ArkaHyte.Sensors;

/// <summary>
/// Reads the values HWiNFO publishes for its Gadget feature (HKCU\Software\HWiNFO64\VSB).
/// Unlike shared memory, this channel has no 12 hour limit in the free edition.
/// Entries are matched by sensor and label names so their order in HWiNFO does not matter.
/// </summary>
public sealed partial class HwInfoReader : IDisposable
{
	private const string GadgetKey = @"Software\HWiNFO64\VSB";

	/// <summary>One Gadget entry; Text is the value formatted by HWiNFO with its unit ("52.2 °C").</summary>
	public readonly record struct Entry(string Sensor, string Label, double Value, string Text);

	private readonly Dictionary<SensorSelector, Regex[]> _patterns = new();
	private readonly AutoResetEvent _changed = new(false);
	private RegistryKey? _key;
	private bool _watching;
	private List<Entry> _entries = [];

	public bool IsAvailable => _entries.Count > 0;

	/// <summary>Entries of the last refresh, in HWiNFO order.</summary>
	public IReadOnlyList<Entry> Entries => _entries;

	/// <summary>Number of times the Gadget values were actually read (diagnostics).</summary>
	public int Reads { get; private set; }

	/// <summary>
	/// Call once per sample. HWiNFO rewrites the Gadget values at its own polling period (2 s by
	/// default): Windows signals each write, and the values are only read again after one, so the
	/// same values are never parsed twice whatever the two periods are.
	/// </summary>
	public void Refresh()
	{
		if (_watching && !_changed.WaitOne(0))
		{
			return;
		}
		// Reopened at every change: HWiNFO deletes and recreates the key when it restarts
		_key?.Dispose();
		_key = Registry.CurrentUser.OpenSubKey(GadgetKey);
		// Armed before reading, so a write landing during the read triggers the next one
		_watching = _key is not null && Watch(_key);
		_entries = Read(_key);
		Reads++;
	}

	private bool Watch(RegistryKey key) =>
		RegNotifyChangeKeyValue(key.Handle, false, REG_NOTIFY_CHANGE_LAST_SET | REG_NOTIFY_THREAD_AGNOSTIC, _changed.SafeWaitHandle, true) == 0;

	private static List<Entry> Read(RegistryKey? key)
	{
		var entries = new List<Entry>();
		if (key is not null)
		{
			// Slots follow each entry's rank in HWiNFO's settings: gaps are possible (an entry whose sensor is
			// absent, ranks set by hand), so every slot present is read, in order
			foreach (var i in Slots(key))
			{
				if (key.GetValue($"Label{i}") is not string label)
				{
					continue;
				}
				var sensor = key.GetValue($"Sensor{i}") as string ?? string.Empty;
				if (TryParse(key.GetValue($"ValueRaw{i}") as string, out var value))
				{
					var text = key.GetValue($"Value{i}") as string ?? value.ToString(CultureInfo.InvariantCulture);
					entries.Add(new Entry(sensor, label, value, text));
				}
			}
		}
		return entries;
	}

	/// <summary>Numbers of the Gadget slots present ("Label12" → 12), in ascending order.</summary>
	public static IEnumerable<int> Slots(RegistryKey key) =>
		key.GetValueNames()
			.Where(name => name.StartsWith("Label", StringComparison.Ordinal))
			.Select(name => int.TryParse(name.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var slot) ? slot : -1)
			.Where(slot => slot >= 0)
			.Order();

	public void Dispose()
	{
		_key?.Dispose();
		_changed.Dispose();
	}

	private const uint REG_NOTIFY_CHANGE_LAST_SET = 0x4;
	// The notification would otherwise be cancelled when the registering thread exits (thread pool)
	private const uint REG_NOTIFY_THREAD_AGNOSTIC = 0x10000000;

	[System.Runtime.InteropServices.DllImport("advapi32.dll")]
	private static extern int RegNotifyChangeKeyValue(
		Microsoft.Win32.SafeHandles.SafeRegistryHandle key,
		[System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool watchSubtree,
		uint filter,
		Microsoft.Win32.SafeHandles.SafeWaitHandle eventHandle,
		[System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool asynchronous);

	public double? Get(SensorSelector? selector) => IndexOf(selector) is var index and >= 0 ? _entries[index].Value : null;

	/// <summary>The configured reading, or the first default HWiNFO reports.</summary>
	public double? Get(SensorSelector? selector, SensorSelector[] defaults)
	{
		if (selector is not null)
		{
			return Get(selector);
		}
		foreach (var fallback in defaults)
		{
			if (Get(fallback) is { } value)
			{
				return value;
			}
		}
		return null;
	}

	/// <summary>
	/// Clock of each core of the processor sensor ("Core 3 Clock (perf #2)", "Core 3 Horloge (perf #2)"),
	/// in core order; effective clocks and ratios excluded.
	/// </summary>
	public IReadOnlyList<double> CoreClocks()
	{
		var clocks = new SortedList<int, double>();
		foreach (var entry in _entries)
		{
			if (CoreClockLabel().Match(entry.Label) is { Success: true } match && entry.Sensor.StartsWith("CPU [#", StringComparison.Ordinal))
			{
				clocks.TryAdd(int.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture), entry.Value);
			}
		}
		return [.. clocks.Values];
	}

	[GeneratedRegex(@"^Core (\d+) (Clock|Horloge) \(perf", RegexOptions.IgnoreCase)]
	private static partial Regex CoreClockLabel();

	/// <summary>Position in <see cref="Entries"/> of the first entry the selector matches, or -1.</summary>
	public int IndexOf(SensorSelector? selector)
	{
		if (selector is null || string.IsNullOrWhiteSpace(selector.Label))
		{
			return -1;
		}
		Regex[]? patterns;
		// The display's settings panel matches from the UI thread while the sampling thread does too
		lock (_patterns)
		{
			if (!_patterns.TryGetValue(selector, out patterns))
			{
				patterns = [ToRegex(ProcessAgnostic(selector.Sensor)), ToRegex(selector.Label)];
				_patterns[selector] = patterns;
			}
		}
		var entries = _entries;
		for (var i = 0; i < entries.Count; i++)
		{
			if (patterns[0].IsMatch(entries[i].Sensor) && patterns[1].IsMatch(entries[i].Label))
			{
				return i;
			}
		}
		return -1;
	}

	public IEnumerable<string> Describe() =>
		_entries.Select(e => $"{e.Sensor} | {e.Label} = {e.Value.ToString(CultureInfo.InvariantCulture)}");

	/// <summary>
	/// HWiNFO names its PresentMon sensor after the foreground process ("PresentMon [game.exe]"): a
	/// selector saved with one process name must keep matching whichever process comes next.
	/// </summary>
	private static string ProcessAgnostic(string sensor) => ProcessSuffix().Replace(sensor, "[*]");

	[GeneratedRegex(@"\[[^\]]+\.exe\]$", RegexOptions.IgnoreCase)]
	private static partial Regex ProcessSuffix();

	private static Regex ToRegex(string wildcard)
	{
		var pattern = "^" + Regex.Escape(string.IsNullOrEmpty(wildcard) ? "*" : wildcard).Replace(@"\*", ".*") + "$";
		return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
	}

	// HWiNFO writes raw values with a dot, but accept a comma in case the locale leaks in
	private static bool TryParse(string? raw, out double value) =>
		double.TryParse(raw?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
