using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ArkaHyte.Sensors;

/// <summary>
/// Turns on, in HWiNFO, the Gadget reporting of the readings the display uses, without the user ticking
/// "Report value in Gadget" by hand. Runs in an elevated child process (<see cref="Argument"/>): HWiNFO
/// runs elevated, so only an elevated process can stop and restart it.
///
/// HWiNFO keeps one registry key per customised reading, HKCU\Software\HWiNFO64\Sensors\{sensor}\{type}{n}
/// (InVSB = reported to Gadget, VSBidx = rank, Color). Which number is which reading is not known
/// beforehand, so a discovery run reports every possible one, each with a colour of its own: Gadget
/// writes that colour next to the reading's name, which tells them apart. Then only the user's own
/// entries and the wanted readings are kept. HWiNFO reads these keys when it starts and writes them back
/// when it exits: they are only changed while it is stopped.
/// </summary>
internal static partial class HwInfoConfigurator
{
	public const string Argument = "--configure-hwinfo";

	private const string RootKey = @"Software\HWiNFO64";
	private const string SensorsKey = RootKey + @"\Sensors";
	private const string GadgetKey = RootKey + @"\VSB";
	private static readonly string[] EntryTypes = ["Temp", "Volt", "Fan", "Current", "Power", "Clock", "Usage", "Other"];
	private const int MaxEntryIndex = 64;
	// Discovery entries rank after every existing one and carry a colour from a range of their own
	private const int DiscoveryRankBase = 100000;
	private const int DiscoveryColorBase = 0xAB0000;
	private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(90);

	/// <summary>
	/// Readings worth reporting: sensor name and exact label (HWiNFO in English or French; French names
	/// checked against a real catalog). Discrete GPU only: the Ryzen's integrated one is "iGPU".
	/// </summary>
	private static readonly (Regex Sensor, Regex Label)[] Wanted =
	[
		// Display widgets
		(Cpu(), new(@"^CPU \(Tctl/Tdie\)$", RegexOptions.IgnoreCase)),
		(Cpu(), new(@"^(CPU Package Power|Consommation d'énergie totale du CPU)$", RegexOptions.IgnoreCase)),
		(DiscreteGpu(), new(@"^(GPU Temperature|Température GPU)$", RegexOptions.IgnoreCase)),
		(DiscreteGpu(), new(@"^(GPU Power|GPU Consommation d'énergie)$", RegexOptions.IgnoreCase)),
		(DiscreteGpu(), new(@"^(GPU Core Load|Charge du cœur du GPU)$", RegexOptions.IgnoreCase)),
		(DiscreteGpu(), new(@"^(GPU Memory Usage|Utilisation de la mémoire GPU)$", RegexOptions.IgnoreCase)),
		(new(@"^S\.M\.A\.R\.T\.", RegexOptions.IgnoreCase), new(@"^(Drive Temperature|Température du disque)$", RegexOptions.IgnoreCase)),
		// Processor details: clock, core voltage, power limit, active cores, clock of each core
		(Cpu(), new(@"^(Average Effective Clock|Horloge efficace moyenne)$", RegexOptions.IgnoreCase)),
		(Cpu(), new(@"^CPU (Core Voltage|VDDCR_VDD Voltage|VDDCR_VDD Tension) \(SVI3 TFN\)$", RegexOptions.IgnoreCase)),
		(Cpu(), new(@"^CPU PPT Limit$", RegexOptions.IgnoreCase)),
		(Cpu(), new(@"^(Average Active Core Count|Nombre moyen de cœurs actifs)$", RegexOptions.IgnoreCase)),
		(Cpu(), new(@"^Core \d+ (Clock|Horloge) \(perf", RegexOptions.IgnoreCase))
	];

	[GeneratedRegex(@"^CPU \[#\d+\]", RegexOptions.IgnoreCase)]
	private static partial Regex Cpu();

	[GeneratedRegex(@"^(dGPU|GPU) \[#\d+\]", RegexOptions.IgnoreCase)]
	private static partial Regex DiscreteGpu();

	/// <summary>Result written for the settings window: what was added, or why nothing was.</summary>
	public sealed record Result(bool Ok, string? Error, IReadOnlyList<string> Added, int Discovered, string? Backup);

	private sealed record Candidate(string Sensor, string Entry, int Color, Dictionary<string, object>? Before);

	/// <summary>
	/// Entry this tool turned on (kept in <see cref="AddedFile"/>): what it was before, so that it can be
	/// turned off again once no longer wanted, without touching the entries the user reported himself.
	/// </summary>
	private sealed record AddedEntry(string Key, string Entry, string Sensor, string Label, bool Existed, int? Color);

	private static string AddedFile => Path.Combine(DataFolder.HwInfo, "added.json");

	public static int Run(string resultPath)
	{
		Result result;
		try
		{
			result = Configure();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException
			or InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			Log.Error("HWiNFO configuration failed", exception);
			result = new Result(false, "failed", [], 0, null);
		}
		File.WriteAllText(resultPath, JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
		return result.Ok ? 0 : 1;
	}

	private static Result Configure()
	{
		if (HwInfoInstall.FindExecutable() is not { } executable)
		{
			return new Result(false, "notInstalled", [], 0, null);
		}
		using (var gadget = Registry.CurrentUser.OpenSubKey(GadgetKey))
		{
			if (gadget is null)
			{
				return new Result(false, "gadgetOff", [], 0, null);
			}
		}
		Log.Info($"HWiNFO configuration started, elevated: {IsElevated()}");
		var backup = Backup();
		StopHwInfo();

		// Entries this tool added earlier and no longer wants go back to how they were
		var addedBefore = RevertUnwanted();

		// From the first discovery entry on, any failure puts every entry back as it was
		var candidates = new List<Candidate>();
		List<(int Slot, string Sensor, string Label, int Color)> catalog;
		try
		{
			WriteDiscovery(candidates);
			Log.Info($"HWiNFO discovery: {candidates.Count} possible readings reported");
			StartHwInfo(executable);
			catalog = WaitForGadget();
			StopHwInfo();
		}
		catch
		{
			Abandon(candidates, executable);
			throw;
		}
		if (!catalog.Exists(entry => entry.Slot >= DiscoveryRankBase))
		{
			// HWiNFO did not report the discovery entries (not started, Gadget off)
			Abandon(candidates, executable);
			return new Result(false, "noReadings", [], 0, backup);
		}

		// Readings found: Gadget slot = rank in the settings, the colour confirms it; the wanted ones are kept,
		// everything else goes back as it was
		var found = new Dictionary<Candidate, (string Sensor, string Label)>();
		foreach (var (slot, sensor, label, color) in catalog)
		{
			var index = slot - DiscoveryRankBase;
			if (index >= 0 && index < candidates.Count && candidates[index].Color == color)
			{
				found[candidates[index]] = (sensor, label);
			}
		}
		var added = new List<string>();
		var nextRank = NextFreeRank();
		using (var sensors = Registry.CurrentUser.CreateSubKey(SensorsKey))
		{
			foreach (var candidate in candidates)
			{
				using var sensorKey = sensors.CreateSubKey(candidate.Sensor);
				if (found.TryGetValue(candidate, out var reading) && Wanted.Any(w => w.Sensor.IsMatch(reading.Sensor) && w.Label.IsMatch(reading.Label)))
				{
					using var entryKey = sensorKey.CreateSubKey(candidate.Entry);
					entryKey.SetValue("InVSB", 1, RegistryValueKind.DWord);
					entryKey.SetValue("VSBidx", nextRank++, RegistryValueKind.DWord);
					RestoreValue(entryKey, candidate.Before, "Color");
					added.Add($"{reading.Sensor} | {reading.Label}");
					addedBefore.Add(new AddedEntry(candidate.Sensor, candidate.Entry, reading.Sensor, reading.Label,
						candidate.Before is not null, candidate.Before is not null && candidate.Before.TryGetValue("Color", out var color) && color is int value ? value : null));
					continue;
				}
				Restore(sensorKey, candidate);
			}
		}
		SaveCatalog(found.Values);
		File.WriteAllText(AddedFile, JsonSerializer.Serialize(addedBefore, new JsonSerializerOptions { WriteIndented = true }));
		ClearDiscoverySlots();
		StartHwInfo(executable);
		Log.Info($"HWiNFO configured: {found.Count} readings found, {added.Count} added");
		return new Result(true, null, added, found.Count, backup);
	}

	/// <summary>
	/// Turns off the entries added by an earlier run that the wanted list no longer names (HWiNFO stopped);
	/// returns those still wanted.
	/// </summary>
	private static List<AddedEntry> RevertUnwanted()
	{
		List<AddedEntry> entries;
		try
		{
			entries = File.Exists(AddedFile) ? JsonSerializer.Deserialize<List<AddedEntry>>(File.ReadAllText(AddedFile)) ?? [] : [];
		}
		catch (JsonException exception)
		{
			Log.Error("Unreadable list of HWiNFO entries added earlier", exception);
			return [];
		}
		var kept = new List<AddedEntry>();
		using var sensors = Registry.CurrentUser.CreateSubKey(SensorsKey);
		foreach (var added in entries)
		{
			if (Wanted.Any(w => w.Sensor.IsMatch(added.Sensor) && w.Label.IsMatch(added.Label)))
			{
				kept.Add(added);
				continue;
			}
			using var sensorKey = sensors.OpenSubKey(added.Key, writable: true);
			if (sensorKey is null)
			{
				continue;
			}
			if (!added.Existed)
			{
				sensorKey.DeleteSubKeyTree(added.Entry, throwOnMissingSubKey: false);
			}
			else
			{
				using var entryKey = sensorKey.CreateSubKey(added.Entry);
				entryKey.DeleteValue("InVSB", throwOnMissingValue: false);
				entryKey.DeleteValue("VSBidx", throwOnMissingValue: false);
				if (added.Color is { } color)
				{
					entryKey.SetValue("Color", color, RegistryValueKind.DWord);
				}
			}
			Log.Info($"HWiNFO entry no longer reported: {added.Sensor} | {added.Label}");
		}
		return kept;
	}

	/// <summary>Every reading of every known sensor reported to Gadget, each with a colour of its own.</summary>
	private static void WriteDiscovery(List<Candidate> candidates)
	{
		using var sensors = Registry.CurrentUser.CreateSubKey(SensorsKey);
		foreach (var sensor in sensors.GetSubKeyNames().Where(name => SensorKeyName().IsMatch(name)))
		{
			using var sensorKey = sensors.CreateSubKey(sensor);
			foreach (var type in EntryTypes)
			{
				for (var index = 0; index < MaxEntryIndex; index++)
				{
					var entry = type + index.ToString(CultureInfo.InvariantCulture);
					var before = ReadValues(sensorKey, entry);
					if (before is not null && before.TryGetValue("InVSB", out var inGadget) && inGadget is int reported && reported == 1)
					{
						// Already reported by the user: kept as it is
						continue;
					}
					var color = DiscoveryColorBase + candidates.Count;
					// Listed before it is written, so a failure halfway still undoes it
					candidates.Add(new Candidate(sensor, entry, color, before));
					using var entryKey = sensorKey.CreateSubKey(entry);
					entryKey.SetValue("InVSB", 1, RegistryValueKind.DWord);
					entryKey.SetValue("VSBidx", DiscoveryRankBase + candidates.Count - 1, RegistryValueKind.DWord);
					entryKey.SetValue("Color", color, RegistryValueKind.DWord);
				}
			}
		}
	}

	/// <summary>Every discovery entry back as it was, and HWiNFO running again; best effort.</summary>
	private static void Abandon(List<Candidate> candidates, string executable)
	{
		try
		{
			StopHwInfo();
			using var sensors = Registry.CurrentUser.CreateSubKey(SensorsKey);
			foreach (var candidate in candidates)
			{
				using var sensorKey = sensors.CreateSubKey(candidate.Sensor);
				Restore(sensorKey, candidate);
			}
			Log.Info($"HWiNFO discovery undone: {candidates.Count} entries restored");
			ClearDiscoverySlots();
			StartHwInfo(executable);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException
			or InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			Log.Error("Undoing the HWiNFO discovery failed: restore the .reg backup in the data folder", exception);
		}
	}

	/// <summary>
	/// Gadget slots of the discovery (HWiNFO leaves the values of slots it no longer fills): removed, or
	/// the display would read them as frozen readings.
	/// </summary>
	private static void ClearDiscoverySlots()
	{
		using var gadget = Registry.CurrentUser.OpenSubKey(GadgetKey, writable: true);
		foreach (var name in gadget?.GetValueNames() ?? [])
		{
			var digits = name.AsSpan(name.TrimEnd("0123456789".ToCharArray()).Length);
			if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var slot) && slot >= DiscoveryRankBase)
			{
				gadget!.DeleteValue(name, throwOnMissingValue: false);
			}
		}
	}

	private static bool IsElevated()
	{
		using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
		return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
	}

	/// <summary>Registry copy of every HWiNFO setting, in the hwinfo folder, before anything changes.</summary>
	private static string? Backup()
	{
		Directory.CreateDirectory(DataFolder.HwInfo);
		var path = Path.Combine(DataFolder.HwInfo, $"backup-{DateTime.Now:yyyyMMdd-HHmmss}.reg");
		using var process = Process.Start(new ProcessStartInfo("reg.exe", $"export \"HKCU\\{RootKey}\" \"{path}\" /y")
		{
			CreateNoWindow = true,
			UseShellExecute = false
		});
		process?.WaitForExit(15000);
		if (!File.Exists(path))
		{
			return null;
		}
		// Only the latest backup is kept: the settings just before the last change
		foreach (var older in Directory.EnumerateFiles(DataFolder.HwInfo, "backup-*.reg").Where(p => p != path))
		{
			File.Delete(older);
		}
		return path;
	}

	private static void StopHwInfo()
	{
		foreach (var process in HwInfoInstall.ProcessNames.SelectMany(Process.GetProcessesByName))
		{
			using (process)
			{
				// A normal exit when its window is open (it then saves its settings, which are only changed
				// afterwards); minimised to the tray it has none to close
				if (!process.CloseMainWindow() || !process.WaitForExit(StopTimeout))
				{
					process.Kill();
					process.WaitForExit(StopTimeout);
				}
			}
		}
		// Its tray icon may keep it alive after the window closes
		foreach (var process in HwInfoInstall.ProcessNames.SelectMany(Process.GetProcessesByName))
		{
			using (process)
			{
				process.Kill();
				process.WaitForExit(StopTimeout);
			}
		}
	}

	// Through the shell, which handles HWiNFO's elevation request (a direct start fails with "requires elevation")
	private static void StartHwInfo(string executable) =>
		Process.Start(new ProcessStartInfo(executable)
		{
			UseShellExecute = true,
			WorkingDirectory = Path.GetDirectoryName(executable)!
		})?.Dispose();

	/// <summary>
	/// Gadget entries once HWiNFO has listed them all: discovery entries present (the previous list stays in
	/// the registry while HWiNFO is stopped), then the same count for a few seconds in a row.
	/// </summary>
	private static List<(int Slot, string Sensor, string Label, int Color)> WaitForGadget()
	{
		var deadline = DateTime.UtcNow + DiscoveryTimeout;
		var entries = new List<(int Slot, string Sensor, string Label, int Color)>();
		var stableSince = DateTime.UtcNow;
		var lastCount = -1;
		while (DateTime.UtcNow < deadline)
		{
			Thread.Sleep(1000);
			entries = ReadGadget();
			if (!entries.Exists(entry => entry.Slot >= DiscoveryRankBase))
			{
				continue;
			}
			if (entries.Count != lastCount)
			{
				lastCount = entries.Count;
				stableSince = DateTime.UtcNow;
			}
			else if (entries.Count > 0 && DateTime.UtcNow - stableSince >= TimeSpan.FromSeconds(5))
			{
				break;
			}
		}
		return entries;
	}

	/// <summary>Every Gadget slot: HWiNFO numbers them after each entry's rank, gaps included.</summary>
	private static List<(int Slot, string Sensor, string Label, int Color)> ReadGadget()
	{
		var entries = new List<(int, string, string, int)>();
		using var gadget = Registry.CurrentUser.OpenSubKey(GadgetKey);
		if (gadget is null)
		{
			return entries;
		}
		foreach (var i in HwInfoReader.Slots(gadget))
		{
			if (gadget.GetValue($"Sensor{i}") is not string sensor)
			{
				continue;
			}
			var label = gadget.GetValue($"Label{i}") as string ?? string.Empty;
			// "rrggbb" in Gadget, 0x00BBGGRR (COLORREF) in the sensor settings
			var color = -1;
			if (gadget.GetValue($"Color{i}") is string hex && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
			{
				color = ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);
			}
			entries.Add((i, sensor, label, color));
		}
		return entries;
	}

	private static int NextFreeRank()
	{
		var highest = -1;
		using var sensors = Registry.CurrentUser.OpenSubKey(SensorsKey);
		foreach (var sensor in sensors?.GetSubKeyNames() ?? [])
		{
			using var sensorKey = sensors!.OpenSubKey(sensor);
			foreach (var entry in sensorKey?.GetSubKeyNames() ?? [])
			{
				using var entryKey = sensorKey!.OpenSubKey(entry);
				if (entryKey?.GetValue("InVSB") is int reported && reported == 1 && entryKey.GetValue("VSBidx") is int rank && rank < DiscoveryRankBase)
				{
					highest = Math.Max(highest, rank);
				}
			}
		}
		return highest + 1;
	}

	private static Dictionary<string, object>? ReadValues(RegistryKey sensorKey, string entry)
	{
		using var entryKey = sensorKey.OpenSubKey(entry);
		if (entryKey is null)
		{
			return null;
		}
		return entryKey.GetValueNames().ToDictionary(name => name, name => entryKey.GetValue(name)!);
	}

	/// <summary>The entry as it was before the discovery: its values back, or no key at all.</summary>
	private static void Restore(RegistryKey sensorKey, Candidate candidate)
	{
		if (candidate.Before is null)
		{
			using (var created = sensorKey.OpenSubKey(candidate.Entry))
			{
				if (created is null || created.SubKeyCount > 0)
				{
					return;
				}
			}
			sensorKey.DeleteSubKey(candidate.Entry, throwOnMissingSubKey: false);
			return;
		}
		using var entryKey = sensorKey.CreateSubKey(candidate.Entry);
		foreach (var name in entryKey.GetValueNames().Where(name => !candidate.Before.ContainsKey(name)))
		{
			entryKey.DeleteValue(name, throwOnMissingValue: false);
		}
		foreach (var (name, value) in candidate.Before)
		{
			entryKey.SetValue(name, value);
		}
	}

	private static void RestoreValue(RegistryKey entryKey, Dictionary<string, object>? before, string name)
	{
		if (before is not null && before.TryGetValue(name, out var value))
		{
			entryKey.SetValue(name, value);
		}
		else
		{
			entryKey.DeleteValue(name, throwOnMissingValue: false);
		}
	}

	/// <summary>Every reading HWiNFO offers, kept to choose the next ones to report.</summary>
	private static void SaveCatalog(IEnumerable<(string Sensor, string Label)> readings)
	{
		var list = readings.Select(r => new { sensor = r.Sensor, label = r.Label }).OrderBy(r => r.sensor).ThenBy(r => r.label);
		Directory.CreateDirectory(DataFolder.HwInfo);
		File.WriteAllText(Path.Combine(DataFolder.HwInfo, "catalog.json"),
			JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
	}

	[GeneratedRegex(@"^[0-9A-F]{8}_\d+$", RegexOptions.IgnoreCase)]
	private static partial Regex SensorKeyName();
}
