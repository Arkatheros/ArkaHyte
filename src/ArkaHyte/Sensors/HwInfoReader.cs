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

	/// <summary>One Gadget entry; Text is the value formatted by HWiNFO with its unit ("52.2 °C").</summary>
	public readonly record struct Entry(string Sensor, string Label, double Value, string Text);

	private readonly Dictionary<SensorSelector, Regex[]> _patterns = new();
	private List<Entry> _entries = [];

	public bool IsAvailable => _entries.Count > 0;

	/// <summary>Entries of the last refresh, in HWiNFO order.</summary>
	public IReadOnlyList<Entry> Entries => _entries;

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
					var text = key.GetValue($"Value{i}") as string ?? value.ToString(CultureInfo.InvariantCulture);
					entries.Add(new Entry(sensor, label, value, text));
				}
			}
		}
		_entries = entries;
	}

	public double? Get(SensorSelector? selector) => IndexOf(selector) is var index and >= 0 ? _entries[index].Value : null;

	/// <summary>Position in <see cref="Entries"/> of the first entry the selector matches, or -1.</summary>
	public int IndexOf(SensorSelector? selector)
	{
		if (selector is null || string.IsNullOrWhiteSpace(selector.Label))
		{
			return -1;
		}
		if (!_patterns.TryGetValue(selector, out var patterns))
		{
			patterns = [ToRegex(selector.Sensor), ToRegex(selector.Label)];
			_patterns[selector] = patterns;
		}
		for (var i = 0; i < _entries.Count; i++)
		{
			if (patterns[0].IsMatch(_entries[i].Sensor) && patterns[1].IsMatch(_entries[i].Label))
			{
				return i;
			}
		}
		return -1;
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
