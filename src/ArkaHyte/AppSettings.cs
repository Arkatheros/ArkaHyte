using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ArkaHyte;

public sealed record DisplaySettings(int Width = 682, int Height = 2560);

public sealed record HardwareSettings(
	string CpuMaker = "",
	string CpuModel = "",
	string GpuMaker = "",
	string GpuModel = "",
	double VramTotalGb = 24);

/// <summary>HWiNFO Gadget entry selector; both parts accept '*' wildcards and ignore case.</summary>
public sealed record SensorSelector(string Sensor = "*", string Label = "");

public sealed record HwInfoSettings(
	SensorSelector? CpuTemp = null,
	SensorSelector? CpuPower = null,
	SensorSelector? GpuTemp = null,
	SensorSelector? GpuPower = null,
	SensorSelector? GpuLoad = null,
	SensorSelector? GpuVramPercent = null,
	// Game framerate: HWiNFO's PresentMon sensor (or RTSS), only present while a game runs
	SensorSelector? GpuFps = null,
	// Optional PresentMon values of the game view; computed from the framerate when not chosen
	SensorSelector? GpuFpsLow = null,
	SensorSelector? GpuFrameTime = null,
	// Processor details (side readouts, detail panel); HWiNFO's own names when left out (CpuDetailDefaults)
	SensorSelector? CpuClock = null,
	SensorSelector? CpuVoltage = null,
	SensorSelector? CpuPptLimit = null,
	SensorSelector? CpuActiveCores = null)
{
	/// <summary>Readings looked for when the settings name none: HWiNFO in English, then in French.</summary>
	public static readonly SensorSelector[] CpuClockDefaults =
		[new("CPU [#*", "Average Effective Clock"), new("CPU [#*", "Horloge efficace moyenne")];
	public static readonly SensorSelector[] CpuVoltageDefaults =
		[new("CPU [#*", "CPU Core Voltage (SVI3 TFN)"), new("CPU [#*", "CPU VDDCR_VDD Voltage (SVI3 TFN)"), new("CPU [#*", "CPU VDDCR_VDD Tension (SVI3 TFN)")];
	public static readonly SensorSelector[] CpuPptLimitDefaults = [new("CPU [#*", "CPU PPT Limit")];
	public static readonly SensorSelector[] CpuActiveCoresDefaults =
		[new("CPU [#*", "Average Active Core Count"), new("CPU [#*", "Nombre moyen de cœurs actifs")];
}

/// <summary>
/// Display themes: the world shown (background, particles, hilt of the VRAM saber) and the palette it sets when
/// it is chosen (console.css, console.js).
/// </summary>
public static class Themes
{
	public const string Sith = "sith";
	public const string Jedi = "jedi";
	public const string Coruscant = "coruscant";
	public const string Dagobah = "dagobah";

	/// <summary>Theme id, the name shown in the settings and the palette it sets when it is chosen.</summary>
	public static readonly IReadOnlyList<(string Id, string Name, string Palette)> All =
	[
		(Sith, "Mustafar · volcan", Palettes.Red),
		(Jedi, "Tatooine · désert", Palettes.Blue),
		(Coruscant, "Coruscant · ville", Palettes.Violet),
		(Dagobah, "Dagobah · marais", Palettes.Green)
	];

	public static string Normalize(string? theme) => All.Any(t => t.Id == theme) ? theme! : Sith;

	/// <summary>The palette a theme sets when it is chosen, and the one used while none is saved.</summary>
	public static string PaletteOf(string theme) => All.FirstOrDefault(t => t.Id == theme).Palette ?? Palettes.Red;
}

/// <summary>
/// A saber colour applied to the whole display, whatever the theme: its full set of CSS variables lives in
/// console.css (:root[data-palette]); here, what the tray menu and the settings swatches need.
/// </summary>
public sealed record Palette(string Id, string Name, Color Blade, Color Panel, Color Hover, Color Bone);

public static class Palettes
{
	public const string Red = "red";
	public const string Blue = "blue";
	public const string Violet = "violet";
	public const string Green = "green";

	public static readonly IReadOnlyList<Palette> All =
	[
		new(Red, "Rouge", Color.FromArgb(255, 31, 51), Color.FromArgb(15, 6, 7), Color.FromArgb(58, 10, 16), Color.FromArgb(243, 233, 234)),
		new(Blue, "Bleu", Color.FromArgb(40, 140, 255), Color.FromArgb(5, 10, 19), Color.FromArgb(10, 32, 62), Color.FromArgb(232, 239, 246)),
		new(Green, "Vert", Color.FromArgb(46, 214, 92), Color.FromArgb(5, 14, 8), Color.FromArgb(10, 46, 22), Color.FromArgb(236, 245, 238)),
		new(Violet, "Violet", Color.FromArgb(166, 70, 255), Color.FromArgb(10, 5, 16), Color.FromArgb(38, 14, 60), Color.FromArgb(242, 236, 250)),
		new("yellow", "Jaune", Color.FromArgb(255, 204, 34), Color.FromArgb(14, 12, 5), Color.FromArgb(52, 42, 8), Color.FromArgb(246, 242, 228)),
		new("orange", "Orange", Color.FromArgb(255, 122, 26), Color.FromArgb(15, 8, 4), Color.FromArgb(58, 26, 6), Color.FromArgb(246, 238, 230)),
		new("white", "Blanc", Color.FromArgb(226, 234, 246), Color.FromArgb(10, 11, 13), Color.FromArgb(40, 43, 50), Color.FromArgb(244, 246, 250))
	];

	/// <summary>A known palette id; otherwise (none saved, former "auto") the theme's.</summary>
	public static string Normalize(string? palette, string theme) =>
		All.Any(p => p.Id == palette) ? palette! : Themes.PaletteOf(Themes.Normalize(theme));

	public static Palette Get(string palette) => All.FirstOrDefault(p => p.Id == palette) ?? All[0];
}

/// <summary>
/// Custom background (Appearance step of the settings). Image: the frame chosen on the user's picture, rendered
/// once at the display size; the only file kept (the picture itself is not, choosing it again re-crops it).
/// The theme's embers or sand (born on the hot spots of its own picture) are not drawn over it. Shade darkens
/// the top of the screen behind the clock, 0 to <see cref="MaxShade"/>. Back to the theme's background, nothing
/// is kept.
/// </summary>
public sealed record BackgroundSettings(
	bool Enabled = false,
	string? Image = null,
	double Shade = BackgroundSettings.DefaultShade,
	// A video cannot be rendered once: it is played as chosen and cut to this frame by the display
	CropFrame? Crop = null)
{
	public const double DefaultShade = 0.4;
	public const double MaxShade = 0.8;
}

/// <summary>Area kept from a picture, in fractions (0 to 1) of its upright size (EXIF orientation applied).</summary>
public sealed record CropFrame(double X, double Y, double Width, double Height)
{
	[JsonIgnore]
	public bool IsValid =>
		double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height)
		&& X >= 0 && Y >= 0 && Width > 0 && Height > 0 && X + Width <= 1.0001 && Y + Height <= 1.0001;
}

/// <summary>Gauge range of a metric and its alert levels: warm tints the value, hot raises the alert.</summary>
public sealed record Threshold(double Min, double Max, double Warm, double Hot);

/// <summary>Per-metric thresholds; a metric left out uses <see cref="Defaults"/>.</summary>
public sealed record ThresholdSettings(
	Threshold? CpuTemp = null,
	Threshold? CpuPower = null,
	Threshold? GpuTemp = null,
	Threshold? GpuPower = null,
	Threshold? DiskTemp = null)
{
	public static readonly ThresholdSettings Defaults = new(
		new Threshold(30, 95, 75, 85),
		new Threshold(0, 160, 120, 150),
		new Threshold(30, 90, 70, 80),
		new Threshold(0, 450, 350, 420),
		new Threshold(0, 80, 50, 60));

	public ThresholdSettings WithDefaults() => new(
		CpuTemp ?? Defaults.CpuTemp,
		CpuPower ?? Defaults.CpuPower,
		GpuTemp ?? Defaults.GpuTemp,
		GpuPower ?? Defaults.GpuPower,
		DiskTemp ?? Defaults.DiskTemp);
}

/// <summary>Weather under the clock (Open-Meteo): the place picked in the setup wizard.</summary>
public sealed record WeatherSettings(bool Enabled = false, string Place = "", double? Latitude = null, double? Longitude = null);

/// <summary>
/// Game view: shown by itself when a game starts (AutoGame; otherwise only by a swipe), back to the normal
/// view once the game has been in the background (another window has the focus) for AutoNormalDelaySec,
/// back to the game view when it returns to the foreground.
/// Transition: animation between the two views, one of <see cref="Transitions"/>.
/// Games are those Windows recognises (Game Bar list), plus ExtraGames (added by hand, matched by
/// executable name) and, with DetectEngines, any Unreal Engine or Unity game window.
/// </summary>
public sealed record GameViewSettings(
	bool AutoGame = true,
	bool AutoNormal = true,
	int AutoNormalDelaySec = 20,
	string Transition = "morph",
	bool DetectEngines = true,
	IReadOnlyList<GameEntry>? ExtraGames = null)
{
	public const int MaxExtraGames = 50;

	public const int MinDelaySec = 5;
	public const int MaxDelaySec = 600;

	/// <summary>morph: widgets glide to their new place; slide: the views slide sideways; fade; none.</summary>
	public static readonly string[] Transitions = ["morph", "slide", "fade", "none"];
}

/// <summary>
/// Case screen layout: the full-width bands of each view, top to bottom, each shown or hidden (settings,
/// Layout step). The dock stays at the bottom. Bands left out (added by a later version) come last, shown;
/// unknown ones are dropped (<see cref="Normalized"/>). The pages know each band's place and height.
/// </summary>
public sealed record LayoutSettings(IReadOnlyList<LayoutBand>? Normal = null, IReadOnlyList<LayoutBand>? Game = null)
{
	/// <summary>Bands of the normal view in their default order (ids shared with console.js and setup.js).</summary>
	public static readonly string[] NormalBands = ["clock", "cpu", "gpu", "memory", "network", "media"];

	/// <summary>Bands of the game view in their default order.</summary>
	public static readonly string[] GameBands = ["header", "fps", "gpu", "cpu", "network"];

	public LayoutSettings Normalized() => new(Normalize(Normal, NormalBands), Normalize(Game, GameBands));

	public static IReadOnlyList<LayoutBand> Normalize(IEnumerable<LayoutBand>? bands, string[] known)
	{
		var result = new List<LayoutBand>();
		foreach (var band in bands ?? [])
		{
			if (band is not null && known.Contains(band.Id) && !result.Any(b => b.Id == band.Id))
			{
				result.Add(band);
			}
		}
		result.AddRange(known.Where(id => !result.Any(b => b.Id == id)).Select(id => new LayoutBand(id)));
		return result;
	}
}

public sealed record LayoutBand(string Id, bool Show = true);

/// <summary>
/// Home Assistant page (left of the normal view): server address and the groups of tiles, in order, each with
/// its number of tiles per row (1 to 3) and its tiles (an entity and how many columns it spans, 1 to 3).
/// Entities: the first format (a flat list), read once as a single "Maison" group. The access token is kept
/// apart, encrypted for the Windows user (<see cref="Home.HomeToken"/>).
/// </summary>
public sealed record HomeSettings(bool Enabled = false, string Url = "", IReadOnlyList<HomeGroup>? Groups = null, IReadOnlyList<HomeEntity>? Entities = null)
{
	public const int MaxEntities = 120;
	public const int MaxGroups = 20;
	public const int MaxColumns = 3;
	public const int MaxNameLength = 40;
	public const string DefaultGroupName = "Maison";
	public const int MinSeparator = 8;
	public const int MaxSeparator = 600;

	/// <summary>The groups as shown: columns and spans brought in range, each entity once, the old flat list as one group.</summary>
	[JsonIgnore]
	public IReadOnlyList<HomeGroup> EffectiveGroups
	{
		get
		{
			var source = Groups ?? (Entities is { Count: > 0 } legacy
				? [new HomeGroup(DefaultGroupName, MaxColumns, legacy.Select(e => new HomeTile(e.Id, e.Wide ? 2 : 1)).ToList())]
				: []);
			var seen = new HashSet<string>();
			var groups = new List<HomeGroup>();
			foreach (var group in source.Take(MaxGroups))
			{
				// A separator: its height only (and its line), no name, no tiles
				if (group.Separator is { } height)
				{
					groups.Add(new HomeGroup(string.Empty, 1, [], Math.Clamp(height, MinSeparator, MaxSeparator), group.Line));
					continue;
				}
				var columns = Math.Clamp(group.Columns, 1, MaxColumns);
				var tiles = (group.Tiles ?? [])
					.Where(t => !string.IsNullOrWhiteSpace(t.Id) && seen.Count < MaxEntities && seen.Add(t.Id))
					.Select(t => t with { Span = Math.Clamp(t.Span, 1, columns) })
					.ToList();
				var name = string.IsNullOrWhiteSpace(group.Name) ? DefaultGroupName : group.Name.Trim();
				groups.Add(new HomeGroup(name.Length > MaxNameLength ? name[..MaxNameLength] : name, columns, tiles));
			}
			return groups;
		}
	}

	/// <summary>Every entity shown, in page order.</summary>
	[JsonIgnore]
	public IEnumerable<string> EntityIds => EffectiveGroups.SelectMany(g => g.Tiles ?? []).Select(t => t.Id);
}

/// <summary>A group of tiles, or a separator (Separator: its height in pixels; Line: a rule drawn across it).</summary>
public sealed record HomeGroup(string Name, int Columns = 3, IReadOnlyList<HomeTile>? Tiles = null, int? Separator = null, bool Line = false);

/// <summary>A tile: its entity, the columns it spans, and an icon chosen in the settings (js/home-icons.js name; none: automatic).</summary>
public sealed record HomeTile(string Id, int Span = 1, string? Icon = null);

/// <summary>First format of the page's entities (one flat list), still read.</summary>
public sealed record HomeEntity(string Id, string? Area = null, bool Wide = false);

/// <summary>Game added by hand: executable file name ("TFTClient-Win64-Shipping.exe") and the name shown.</summary>
public sealed record GameEntry(string Exe, string Name = "");

public sealed record DriveSettings(string Name, SensorSelector? Temp = null);

public sealed record NetworkSettings(string Adapter = "", string PingHost = "1.1.1.1", string FallbackPingHost = "8.8.8.8");

/// <summary>
/// Dock button. Icon is either a built-in icon name (gamepad, music, chat, pulse, globe, folder,
/// video, code, headset, camera) or a path to an image (.png/.jpg/.svg/.webp/.ico) or to an
/// executable/DLL whose embedded icon is extracted. Empty: built-in "app" icon.
/// </summary>
public sealed record ShortcutSettings(string Label, string Target, string? Icon = null, string? Arguments = null);

/// <summary>
/// Action defined in the settings window and offered by the dock editor: a URL (Kind "url") or a PowerShell
/// script (Kind "script", Target = path of the .ps1). Dock buttons refer to it as "custom:{Id}".
/// </summary>
public sealed record CustomAction(string Id, string Name, string Kind, string Target)
{
	public const string Url = "url";
	public const string Script = "script";
	public const int Max = 20;
}

/// <summary>
/// Electricity cost (EnergyMeter): price per kWh, a flat allowance for what HWiNFO does not measure
/// (motherboard, memory, drives, fans, pump), the power supply efficiency, in percent, and a correction
/// of the CPU and GPU power read in HWiNFO, in percent. With <see cref="Components"/>, the allowance is
/// their sum (<see cref="OtherWatts"/> is still written, for older versions).
/// </summary>
public sealed record EnergySettings(
	double PricePerKwh = 0.1952,
	int OtherWatts = 40,
	int PsuEfficiency = 90,
	bool Show = true,
	int CpuPercent = 100,
	int GpuPercent = 100,
	EnergyComponents? Components = null)
{
	public const double MaxPrice = 5;
	public const int MaxOtherWatts = 500;
	public const int MinEfficiency = 50;
	public const int MaxEfficiency = 100;
	public const int MinCorrection = 50;
	public const int MaxCorrection = 150;
}

/// <summary>Watts of each part HWiNFO does not measure; the defaults add up to the former 40 W allowance.</summary>
public sealed record EnergyComponents(
	int Motherboard = 15,
	int Memory = 6,
	int Storage = 5,
	int Fans = 6,
	int Pump = 4,
	int Lighting = 4,
	int CaseDisplay = 0,
	int Other = 0)
{
	public const int Max = 300;

	public static readonly string[] Names = [nameof(Motherboard), nameof(Memory), nameof(Storage), nameof(Fans), nameof(Pump), nameof(Lighting), nameof(CaseDisplay), nameof(Other)];

	public EnergyComponents Clamped() => new(
		Math.Clamp(Motherboard, 0, Max),
		Math.Clamp(Memory, 0, Max),
		Math.Clamp(Storage, 0, Max),
		Math.Clamp(Fans, 0, Max),
		Math.Clamp(Pump, 0, Max),
		Math.Clamp(Lighting, 0, Max),
		Math.Clamp(CaseDisplay, 0, Max),
		Math.Clamp(Other, 0, Max));

	public int Sum() => Motherboard + Memory + Storage + Fans + Pump + Lighting + CaseDisplay + Other;
}

public sealed record AppSettings(
	DisplaySettings Display,
	HardwareSettings Hardware,
	HwInfoSettings HwInfo,
	IReadOnlyList<DriveSettings> Drives,
	NetworkSettings Network,
	IReadOnlyList<ShortcutSettings> Shortcuts,
	string CpuClickTarget = "taskmgr.exe",
	bool SetupCompleted = false,
	string Theme = Themes.Sith,
	// One of Palettes.All, set to the theme's when a theme is chosen; none: the theme's (EffectivePalette)
	string? Palette = null,
	ThresholdSettings? Thresholds = null,
	WeatherSettings? Weather = null,
	GameViewSettings? GameView = null,
	// Clock and voltage next to the CPU dial (toggled from the processor detail panel)
	bool CpuDetails = true,
	IReadOnlyList<CustomAction>? CustomActions = null,
	EnergySettings? Energy = null,
	BackgroundSettings? Background = null,
	LayoutSettings? Layout = null,
	HomeSettings? Home = null)
{
	public GameViewSettings EffectiveGameView => GameView ?? new GameViewSettings();

	/// <summary>Every band of both views, in the order chosen (a hand-edited file brought back to known bands).</summary>
	public LayoutSettings EffectiveLayout => (Layout ?? new LayoutSettings()).Normalized();

	/// <summary>The palette shown: the one saved, or the theme's.</summary>
	public string EffectivePalette => Palettes.Normalize(Palette, Theme);

	/// <summary>Energy settings, out-of-range values from a hand-edited file brought back in range.</summary>
	public EnergySettings EffectiveEnergy => Energy is { } energy
		? energy with
		{
			PricePerKwh = Math.Clamp(energy.PricePerKwh, 0, EnergySettings.MaxPrice),
			// The parts, when detailed, are the allowance
			OtherWatts = energy.Components?.Clamped().Sum() ?? Math.Clamp(energy.OtherWatts, 0, EnergySettings.MaxOtherWatts),
			PsuEfficiency = Math.Clamp(energy.PsuEfficiency, EnergySettings.MinEfficiency, EnergySettings.MaxEfficiency),
			CpuPercent = Math.Clamp(energy.CpuPercent, EnergySettings.MinCorrection, EnergySettings.MaxCorrection),
			GpuPercent = Math.Clamp(energy.GpuPercent, EnergySettings.MinCorrection, EnergySettings.MaxCorrection),
			Components = energy.Components?.Clamped()
		}
		: new EnergySettings();

	/// <summary>Thresholds with the defaults filled in for the metrics the file leaves out.</summary>
	public ThresholdSettings EffectiveThresholds => (Thresholds ?? ThresholdSettings.Defaults).WithDefaults();

	private static readonly JsonSerializerOptions Options = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	public const string FileName = "appsettings.json";

	/// <summary>Copy kept in the data folder by installed versions, so updates never overwrite it.</summary>
	public static string UserFilePath => Path.Combine(DataFolder.Root, FileName);

	/// <summary>The user copy when there is one, otherwise the file shipped next to the executable.</summary>
	public static AppSettings Load() => Load(CurrentFilePath);

	internal static string CurrentFilePath =>
		File.Exists(UserFilePath) ? UserFilePath : Path.Combine(AppContext.BaseDirectory, FileName);

	/// <summary>
	/// Applies changes to the settings file and writes the result to the user copy (created from the
	/// shipped file when missing). Edits the JSON tree so entries the change does not touch stay as written.
	/// </summary>
	public static void SaveUser(Action<JsonObject> change)
	{
		var source = CurrentFilePath;
		var root = File.Exists(source)
			? JsonNode.Parse(File.ReadAllText(source), new JsonNodeOptions { PropertyNameCaseInsensitive = true }, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
			: null;
		root ??= new JsonObject();
		change(root);
		Directory.CreateDirectory(DataFolder.Root);
		var json = root.ToJsonString(new JsonSerializerOptions
		{
			WriteIndented = true,
			IndentCharacter = '\t',
			IndentSize = 1,
			// Keeps accents readable ("Tâches", "Température GPU") instead of \u escapes
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
		});
		File.WriteAllText(UserFilePath, json + Environment.NewLine);
		Log.Info($"Settings saved to {UserFilePath}");
	}

	public static AppSettings Load(string path)
	{
		var settings = File.Exists(path)
			? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options)
			: null;

		return settings ?? new AppSettings(
			new DisplaySettings(),
			new HardwareSettings(),
			new HwInfoSettings(),
			[new DriveSettings("C:")],
			new NetworkSettings(),
			[new ShortcutSettings("Tâches", "taskmgr.exe", "pulse")]);
	}
}
