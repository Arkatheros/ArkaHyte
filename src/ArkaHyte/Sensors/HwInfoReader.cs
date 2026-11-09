using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ArkaHyte.Sensors;

/// <summary>
/// Reads the values HWiNFO publishes for its Gadget feature (HKCU\Software\HWiNFO64\VSB).
/// Unlike shared memory, this channel has no 12 hour limit in the free edition.
/// Entries are matched by sensor and label names so their order in HWiNFO does not matter.
/// </summary>
public sealed class HwInfoReader
{
	private const string GadgetKey = @"Software\HWiNFO64\VSB";

	private readonly record struct Entry(string Sensor, string Label, double Value);

	private readonly Dictionary<SensorSelector, Regex[]> _patterns = new();
	private List<Entry> _entries = [];

	public bool IsAvailable => _entries.Count > 0;

	/// <summary>Takes one consistent read of every Gadget entry; call once per refresh.</summary>
	public void Refresh()
	{
		var entries = new List<Entry>();
		using var key = Registry.CurrentUser.OpenSubKey(GadgetKey);
		if (key is not null)
		{
			for (var i = 0; ; i++)
			{
				if (key.GetValue($"Label{i}") is not string label)
				{
					break;
				}
				var sensor = key.GetValue($"Sensor{i}") as string ?? string.Empty;
				if (TryParse(key.GetValue($"ValueRaw{i}") as string, out var value))
				{
					entries.Add(new Entry(sensor, label, value));
				}
			}
		}
		_entries = entries;
	}

	public double? Get(SensorSelector? selector)
	{
		if (selector is null || string.IsNullOrWhiteSpace(selector.Label))
		{
			return null;
		}
		if (!_patterns.TryGetValue(selector, out var patterns))
		{
			patterns = [ToRegex(selector.Sensor), ToRegex(selector.Label)];
			_patterns[selector] = patterns;
		}
		foreach (var entry in _entries)
		{
			if (patterns[0].IsMatch(entry.Sensor) && patterns[1].IsMatch(entry.Label))
			{
				return entry.Value;
			}
		}
		return null;
	}

	public IEnumerable<string> Describe() =>
		_entries.Select(e => $"{e.Sensor} | {e.Label} = {e.Value.ToString(CultureInfo.InvariantCulture)}");

	private static Regex ToRegex(string wildcard)
	{
		var pattern = "^" + Regex.Escape(string.IsNullOrEmpty(wildcard) ? "*" : wildcard).Replace(@"\*", ".*") + "$";
		return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
	}

	// HWiNFO writes raw values with a dot, but accept a comma in case the locale leaks in
	private static bool TryParse(string? raw, out double value) =>
		double.TryParse(raw?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
