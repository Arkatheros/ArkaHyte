using Windows.Media.Control;
using Windows.Storage.Streams;

namespace ArkaHyte.Sensors;

/// <summary>
/// Now-playing information from the Windows media session API (the one behind the volume flyout).
/// Covers Spotify and browser tabs (YouTube, Twitch...) without any extension.
/// </summary>
public sealed class MediaMonitor
{
	private static readonly string[] Browsers = ["chrome", "msedge", "firefox", "opera", "brave", "vivaldi"];

	private GlobalSystemMediaTransportControlsSessionManager? _manager;
	private string? _artKey;

	/// <summary>Raised with (key, data URL) when the cover of the current item changes.</summary>
	public event Action<string, string>? CoverChanged;

	public async Task<MediaData?> ReadAsync()
	{
		_manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
		var session = _manager.GetCurrentSession();
		if (session is null)
		{
			_artKey = null;
			return null;
		}

		var properties = await session.TryGetMediaPropertiesAsync();
		if (properties is null || string.IsNullOrWhiteSpace(properties.Title))
		{
			return null;
		}

		var playback = session.GetPlaybackInfo();
		var playing = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
		var timeline = session.GetTimelineProperties();
		var duration = (timeline.EndTime - timeline.StartTime).TotalSeconds;
		var position = (timeline.Position - timeline.StartTime).TotalSeconds;
		if (playing && timeline.LastUpdatedTime > DateTimeOffset.MinValue)
		{
			// Players only push the position on events; extrapolate between them
			position += (DateTimeOffset.Now - timeline.LastUpdatedTime).TotalSeconds;
		}
		var live = duration <= 0;
		var (source, kind) = DescribeSource(session.SourceAppUserModelId, live);

		var key = $"{source}|{properties.Title}|{properties.Artist}";
		var artKey = properties.Thumbnail is null ? null : key;
		if (artKey is not null && artKey != _artKey)
		{
			var dataUrl = await ReadThumbnailAsync(properties.Thumbnail!);
			if (dataUrl is not null)
			{
				CoverChanged?.Invoke(artKey, dataUrl);
			}
			else
			{
				artKey = null;
			}
		}
		_artKey = artKey;

		return new MediaData(
			source,
			kind,
			properties.Title,
			properties.Artist,
			live ? null : Math.Clamp(position, 0, duration),
			live ? null : duration,
			playing,
			live,
			artKey,
			!live && playback?.Controls?.IsPlaybackPositionEnabled == true);
	}

	/// <summary>Moves the current item to a position, in seconds from its start.</summary>
	public async Task SeekAsync(double seconds)
	{
		_manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
		var session = _manager.GetCurrentSession();
		if (session is null)
		{
			return;
		}
		var timeline = session.GetTimelineProperties();
		var duration = (timeline.EndTime - timeline.StartTime).TotalSeconds;
		var target = timeline.StartTime + TimeSpan.FromSeconds(Math.Clamp(seconds, 0, Math.Max(0, duration)));
		await session.TryChangePlaybackPositionAsync(target.Ticks);
	}

	public async Task ControlAsync(string action)
	{
		_manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
		var session = _manager.GetCurrentSession();
		if (session is null)
		{
			return;
		}
		_ = action switch
		{
			"toggle" => await session.TryTogglePlayPauseAsync(),
			"next" => await session.TrySkipNextAsync(),
			"previous" => await session.TrySkipPreviousAsync(),
			_ => false
		};
	}

	// Browsers do not say which site is playing; a live item without duration is most likely Twitch
	private static (string Source, string Kind) DescribeSource(string appId, bool live)
	{
		if (appId.Contains("spotify", StringComparison.OrdinalIgnoreCase))
		{
			return ("Spotify", "music");
		}
		if (Browsers.Any(b => appId.Contains(b, StringComparison.OrdinalIgnoreCase)))
		{
			return live ? ("Twitch", "live") : ("YouTube", "video");
		}
		var name = Path.GetFileNameWithoutExtension(appId.Split('!')[0]);
		return (string.IsNullOrWhiteSpace(name) ? "Media" : name, live ? "live" : "music");
	}

	private static async Task<string?> ReadThumbnailAsync(IRandomAccessStreamReference reference)
	{
		try
		{
			using var stream = await reference.OpenReadAsync();
			if (stream.Size == 0 || stream.Size > 4 * 1024 * 1024)
			{
				return null;
			}
			var buffer = new byte[stream.Size];
			using var reader = new DataReader(stream);
			await reader.LoadAsync((uint)stream.Size);
			reader.ReadBytes(buffer);
			var contentType = string.IsNullOrWhiteSpace(stream.ContentType) ? "image/png" : stream.ContentType;
			return $"data:{contentType};base64,{Convert.ToBase64String(buffer)}";
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
		{
			return null;
		}
	}
}
