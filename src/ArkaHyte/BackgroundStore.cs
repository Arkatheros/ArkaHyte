using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;

namespace ArkaHyte;

/// <summary>
/// Appearance being edited in the settings window, sent to the case screen: theme, background (Url null for the
/// theme's own) and palette.
/// </summary>
public sealed record AppearancePreview(string? Url, CropFrame? Crop, double Shade, string Palette, string Theme);

/// <summary>
/// Files of the custom background, in <see cref="DataFolder.Backgrounds"/>: the picture chosen (source-*), only
/// while it is being cropped, and the chosen frame rendered at the display size (background-*.jpg), under a new
/// name each time so the browser cache never shows an old one. Once saved, only that rendering stays. Both pages
/// read them from a virtual host of their own; the display never loads the original picture once saved.
/// </summary>
public static class BackgroundStore
{
	public const string VirtualHost = "hyte-user.local";

	/// <summary>Size of the display page (index.html): the rendering fills it exactly.</summary>
	public const int Width = 682;
	public const int Height = 2560;

	/// <summary>Pictures GDI+ decodes on every Windows; the file dialog offers these only.</summary>
	public static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".bmp"];

	private const long MaxSourceBytes = 64L * 1024 * 1024;
	// About 480 MB once decoded: beyond that the picture is refused rather than risk running out of memory
	private const long MaxSourcePixels = 120_000_000;
	private const int ExifOrientation = 0x0112;
	private const long JpegQuality = 92;

	/// <summary>
	/// Serves the folder to the pages on hyte.local, CORS included: the display reads a tiny copy of the picture
	/// back from a canvas to measure its brightness (readability aids). Only this app's own pages load here.
	/// </summary>
	public static void Map(CoreWebView2 core)
	{
		Directory.CreateDirectory(DataFolder.Backgrounds);
		core.SetVirtualHostNameToFolderMapping(VirtualHost, DataFolder.Backgrounds, CoreWebView2HostResourceAccessKind.Allow);
	}

	public static string UrlOf(string fileName) => $"https://{VirtualHost}/{Uri.EscapeDataString(fileName)}";

	/// <summary>Address of the rendered background the display shows; null for the theme's own picture.</summary>
	public static string? ActiveUrl(BackgroundSettings? settings) =>
		settings is { Enabled: true, Image: { } image } && Exists(image) ? UrlOf(image) : null;

	/// <summary>A file of this folder, named without any path (the names come from messages and a hand-editable file).</summary>
	public static bool Exists(string? name) =>
		IsOwnName(name) && File.Exists(Path.Combine(DataFolder.Backgrounds, name!));

	private static bool IsOwnName(string? name) =>
		!string.IsNullOrWhiteSpace(name) && name == Path.GetFileName(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

	/// <summary>Picture chosen in the settings window, checked then copied here: its name and upright size.</summary>
	/// <exception cref="InvalidOperationException">Not a picture GDI+ reads, or too large.</exception>
	public static (string Name, int Width, int Height) Import(string path)
	{
		var extension = Path.GetExtension(path).ToLowerInvariant();
		if (!Extensions.Contains(extension))
		{
			throw new InvalidOperationException($"Unsupported picture type: {path}");
		}
		if (new FileInfo(path).Length > MaxSourceBytes)
		{
			throw new InvalidOperationException($"Picture larger than {MaxSourceBytes / 1024 / 1024} MB: {path}");
		}
		var (width, height) = ReadUprightSize(path);
		Directory.CreateDirectory(DataFolder.Backgrounds);
		var name = $"source-{Stamp()}{(extension == ".jpeg" ? ".jpg" : extension)}";
		File.Copy(path, Path.Combine(DataFolder.Backgrounds, name));
		Log.Info($"Background picture {path} ({width} × {height}) copied as {name}");
		return (name, width, height);
	}

	/// <summary>Size as shown (EXIF orientation applied), from the header only: nothing is decoded.</summary>
	private static (int Width, int Height) ReadUprightSize(string path)
	{
		try
		{
			using var stream = File.OpenRead(path);
			using var picture = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
			if ((long)picture.Width * picture.Height > MaxSourcePixels)
			{
				throw new InvalidOperationException($"Picture of {picture.Width} × {picture.Height} pixels is too large: {path}");
			}
			return Orientation(picture) >= 5 ? (picture.Height, picture.Width) : (picture.Width, picture.Height);
		}
		catch (Exception exception) when (exception is ArgumentException or ExternalException or OutOfMemoryException)
		{
			// GDI+ reports a file it cannot decode as an invalid argument or as out of memory
			throw new InvalidOperationException($"Unreadable picture: {path}", exception);
		}
	}

	/// <summary>Renders <paramref name="crop"/> of a source picture at the display size; returns the new file's name.</summary>
	/// <exception cref="InvalidOperationException">Missing or unreadable source, invalid frame.</exception>
	public static string Render(string source, CropFrame crop)
	{
		if (!Exists(source) || !crop.IsValid)
		{
			throw new InvalidOperationException($"Background source {source} missing or frame invalid");
		}
		var sourcePath = Path.Combine(DataFolder.Backgrounds, source);
		try
		{
			using var stream = File.OpenRead(sourcePath);
			using var picture = Image.FromStream(stream, useEmbeddedColorManagement: true, validateImageData: true);
			Upright(picture);
			var area = new RectangleF(
				(float)(crop.X * picture.Width),
				(float)(crop.Y * picture.Height),
				(float)(crop.Width * picture.Width),
				(float)(crop.Height * picture.Height));
			using var target = new Bitmap(Width, Height, PixelFormat.Format24bppRgb);
			using (var graphics = Graphics.FromImage(target))
			{
				graphics.CompositingQuality = CompositingQuality.HighQuality;
				graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
				graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
				// Mirrored edges: the filter never blends the transparent outside of the picture into the borders
				using var attributes = new ImageAttributes();
				attributes.SetWrapMode(WrapMode.TileFlipXY);
				graphics.DrawImage(picture, new Rectangle(0, 0, Width, Height), area.X, area.Y, area.Width, area.Height, GraphicsUnit.Pixel, attributes);
			}
			var name = $"background-{Stamp()}.jpg";
			SaveJpeg(target, Path.Combine(DataFolder.Backgrounds, name));
			Log.Info($"Background rendered from {source} ({area.Width:0} × {area.Height:0} px at {area.X:0}, {area.Y:0}) as {name}");
			return name;
		}
		catch (Exception exception) when (exception is ArgumentException or ExternalException or OutOfMemoryException or IOException)
		{
			throw new InvalidOperationException($"Rendering the background from {source} failed", exception);
		}
	}

	private static int Orientation(Image picture)
	{
		if (!picture.PropertyIdList.Contains(ExifOrientation))
		{
			return 1;
		}
		var value = picture.GetPropertyItem(ExifOrientation)?.Value;
		return value is { Length: >= 2 } ? BitConverter.ToUInt16(value, 0) : 1;
	}

	/// <summary>Turns a camera picture the way it is shown (and the way the settings page drew the frame on it).</summary>
	private static void Upright(Image picture)
	{
		var turn = Orientation(picture) switch
		{
			2 => RotateFlipType.RotateNoneFlipX,
			3 => RotateFlipType.Rotate180FlipNone,
			4 => RotateFlipType.Rotate180FlipX,
			5 => RotateFlipType.Rotate90FlipX,
			6 => RotateFlipType.Rotate90FlipNone,
			7 => RotateFlipType.Rotate270FlipX,
			8 => RotateFlipType.Rotate270FlipNone,
			_ => RotateFlipType.RotateNoneFlipNone
		};
		if (turn != RotateFlipType.RotateNoneFlipNone)
		{
			picture.RotateFlip(turn);
		}
	}

	private static void SaveJpeg(Bitmap bitmap, string path)
	{
		var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
		using var parameters = new EncoderParameters(1);
		parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, JpegQuality);
		var temp = path + ".tmp";
		bitmap.Save(temp, codec, parameters);
		File.Move(temp, path, overwrite: true);
	}

	private static string Stamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);

	/// <summary>
	/// Writes the background chosen in the settings window: background { enabled, source?, crop? { x, y, width,
	/// height }, shade? }. A picture being cropped (source) is rendered here; without one, the saved rendering
	/// stays. The theme's background keeps no file.
	/// </summary>
	/// <exception cref="InvalidOperationException">The rendering failed: nothing is written.</exception>
	public static void Apply(JsonObject root, JsonElement message, BackgroundSettings? current)
	{
		if (!message.TryGetProperty("background", out var background) || background.ValueKind != JsonValueKind.Object)
		{
			return;
		}
		var settings = current ?? new BackgroundSettings();
		if (background.TryGetProperty("shade", out var shade) && shade.TryGetDouble(out var level) && double.IsFinite(level))
		{
			settings = settings with { Shade = Math.Round(Math.Clamp(level, 0, BackgroundSettings.MaxShade), 2) };
		}
		var enabled = background.TryGetProperty("enabled", out var enabledElement) && enabledElement.ValueKind == JsonValueKind.True;
		if (enabled && ReadSource(background) is { } source && ReadCrop(background) is { } crop)
		{
			settings = settings with { Image = Render(source, crop) };
		}
		// Files no longer named here are deleted after the save (CleanUp)
		settings = enabled && Exists(settings.Image)
			? settings with { Enabled = true }
			: settings with { Enabled = false, Image = null };
		root["Background"] = JsonSerializer.SerializeToNode(settings);
	}

	private static string? ReadSource(JsonElement message) =>
		message.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String && Exists(source.GetString())
			? source.GetString()
			: null;

	/// <summary>
	/// Appearance being edited in the settings window, shown live on the case screen: previewAppearance { theme,
	/// palette, background: { enabled, source?, crop?, shade? } }. A picture being cropped is cut from its original;
	/// without one, the saved rendering is shown as is. Url null: the theme's own background.
	/// </summary>
	public static AppearancePreview ReadPreview(JsonElement message, BackgroundSettings? current)
	{
		string? Text(string name) =>
			message.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;
		var palette = Palettes.Normalize(Text("palette"), Text("theme") ?? Themes.Sith);
		var theme = Themes.Normalize(Text("theme"));
		if (!message.TryGetProperty("background", out var background) || background.ValueKind != JsonValueKind.Object)
		{
			return new AppearancePreview(ActiveUrl(current), null, current?.Shade ?? BackgroundSettings.DefaultShade, palette, theme);
		}
		var enabled = background.TryGetProperty("enabled", out var enabledElement) && enabledElement.ValueKind == JsonValueKind.True;
		var shade = background.TryGetProperty("shade", out var shadeElement) && shadeElement.TryGetDouble(out var level) && double.IsFinite(level)
			? Math.Clamp(level, 0, BackgroundSettings.MaxShade)
			: BackgroundSettings.DefaultShade;
		if (!enabled)
		{
			return new AppearancePreview(null, null, shade, palette, theme);
		}
		if (ReadSource(background) is { } source && ReadCrop(background) is { } crop)
		{
			return new AppearancePreview(UrlOf(source), crop, shade, palette, theme);
		}
		return new AppearancePreview(Exists(current?.Image) ? UrlOf(current!.Image!) : null, null, shade, palette, theme);
	}

	private static CropFrame? ReadCrop(JsonElement background)
	{
		if (!background.TryGetProperty("crop", out var crop) || crop.ValueKind != JsonValueKind.Object)
		{
			return null;
		}
		double Read(string name) =>
			crop.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? Math.Round(number, 5) : double.NaN;
		var frame = new CropFrame(Read("x"), Read("y"), Read("width"), Read("height"));
		return frame.IsValid ? frame : null;
	}

	/// <summary>
	/// Deletes every file but the saved rendering: pictures picked (cropped or abandoned), former renderings, all
	/// of them once back to the theme's background. A file still open (being served) is left for the next time.
	/// </summary>
	public static void CleanUp(BackgroundSettings? settings)
	{
		if (!Directory.Exists(DataFolder.Backgrounds))
		{
			return;
		}
		var keep = settings is { Enabled: true } ? settings.Image : null;
		foreach (var file in Directory.EnumerateFiles(DataFolder.Backgrounds))
		{
			if (string.Equals(Path.GetFileName(file), keep, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			try
			{
				File.Delete(file);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				Log.Error($"Background file {file} not deleted", exception);
			}
		}
	}

	/// <summary>Files the configuration export carries, with their content: the saved rendering.</summary>
	public static IEnumerable<string> FilesOf(BackgroundSettings? settings) =>
		settings is { Enabled: true, Image: { } image } && Exists(image) ? [image] : [];

	/// <summary>Writes a file of an imported configuration; names with a path are refused.</summary>
	public static void Restore(string name, byte[] content)
	{
		if (!IsOwnName(name))
		{
			throw new InvalidOperationException($"Invalid background file name in the configuration: {name}");
		}
		Directory.CreateDirectory(DataFolder.Backgrounds);
		File.WriteAllBytes(Path.Combine(DataFolder.Backgrounds, name), content);
	}
}
