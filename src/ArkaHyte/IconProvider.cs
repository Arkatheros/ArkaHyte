using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ArkaHyte;

/// <summary>Dock icon as sent to the web UI: a built-in glyph name or an image data URL.</summary>
public sealed record DockIcon(string Kind, string Value);

/// <summary>Turns the "Icon" setting of a shortcut into something the web UI can draw. Call from an STA thread.</summary>
public static class IconProvider
{
	private const int ExtractSize = 128;

	private static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
	{
		[".png"] = "image/png",
		[".jpg"] = "image/jpeg",
		[".jpeg"] = "image/jpeg",
		[".webp"] = "image/webp",
		[".svg"] = "image/svg+xml",
		[".gif"] = "image/gif"
	};

	public static DockIcon Resolve(string? icon)
	{
		if (string.IsNullOrWhiteSpace(icon))
		{
			return new DockIcon("builtin", "app");
		}

		var path = Environment.ExpandEnvironmentVariables(icon.Trim());

		// Start menu entries (desktop or Store apps) chosen from the on-screen editor
		if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
		{
			var dataUrl = AppCatalog.GetIcon(path, ExtractSize);
			return dataUrl is null ? new DockIcon("builtin", "app") : new DockIcon("image", dataUrl);
		}

		var looksLikePath = path.Contains('\\') || path.Contains('/') || Path.HasExtension(path);
		if (!looksLikePath)
		{
			return new DockIcon("builtin", path.ToLowerInvariant());
		}

		try
		{
			if (!File.Exists(path))
			{
				Log.Error($"Dock icon not found: {path}");
				return new DockIcon("builtin", "app");
			}
			var extension = Path.GetExtension(path);
			if (ImageTypes.TryGetValue(extension, out var mime))
			{
				return new DockIcon("image", $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}");
			}
			// .ico, .exe, .dll: take the largest embedded icon, PNG-encoded
			using var extracted = Icon.ExtractIcon(path, 0, ExtractSize) ?? Icon.ExtractAssociatedIcon(path);
			if (extracted is null)
			{
				return new DockIcon("builtin", "app");
			}
			using var bitmap = extracted.ToBitmap();
			using var stream = new MemoryStream();
			bitmap.Save(stream, ImageFormat.Png);
			return new DockIcon("image", $"data:image/png;base64,{Convert.ToBase64String(stream.ToArray())}");
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or ExternalException)
		{
			Log.Error($"Could not load dock icon {path}", exception);
			return new DockIcon("builtin", "app");
		}
	}
}
