using System.Runtime.InteropServices;
using ArkaHyte.Native;
using static ArkaHyte.Native.CoreAudio;

namespace ArkaHyte.Audio;

/// <param name="Kind">headset, speaker or display: the icon shown.</param>
public sealed record AudioOutput(string Id, string Name, string Kind, bool Default);

public sealed record AudioMic(string Name, bool Muted);

/// <param name="Volume">Default output volume in percent; null without an output.</param>
public sealed record AudioState(int? Volume, IReadOnlyList<AudioOutput> Outputs, AudioMic? Mic);

/// <summary>
/// Volume of the default output, mute of the default microphone and choice of the default output, through
/// Core Audio. Changes made elsewhere (keyboard keys, Windows, a headset button) arrive as notifications,
/// never by polling: <see cref="Changed"/>. Calls are serialised; callers stay off the UI thread (COM
/// objects created on the thread pool, which is MTA).
/// </summary>
public sealed class AudioService : IDisposable
{
	private readonly object _gate = new();
	private readonly DeviceNotifications _devices;
	private readonly VolumeNotifications _volumes;
	// Context of our own volume changes (the notification still comes back, harmlessly)
	private Guid _context = Guid.NewGuid();
	private IMMDeviceEnumerator? _enumerator;
	private IAudioEndpointVolume? _output;
	private IAudioEndpointVolume? _mic;
	private string? _micName;
	private bool _disposed;

	public event Action? Changed;

	public AudioService()
	{
		_devices = new DeviceNotifications(this);
		_volumes = new VolumeNotifications(this);
	}

	/// <summary>Opens Core Audio; false when unavailable (no audio service), the widget then stays hidden.</summary>
	public bool Start()
	{
		lock (_gate)
		{
			try
			{
				var type = Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid, throwOnError: true)!;
				_enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
				_enumerator.RegisterEndpointNotificationCallback(_devices);
				Bind();
				return true;
			}
			catch (Exception exception) when (exception is COMException or InvalidCastException or TypeLoadException)
			{
				Log.Error("Core Audio unavailable", exception);
				_enumerator = null;
				return false;
			}
		}
	}

	public AudioState GetState()
	{
		lock (_gate)
		{
			if (_enumerator is null)
			{
				return new AudioState(null, [], null);
			}
			int? volume = null;
			if (_output is not null && _output.GetMasterVolumeLevelScalar(out var level) == 0)
			{
				volume = (int)Math.Round(level * 100);
			}
			AudioMic? mic = null;
			if (_mic is not null && _mic.GetMute(out var muted) == 0)
			{
				mic = new AudioMic(_micName ?? "Micro", muted);
			}
			return new AudioState(volume, ReadOutputs(), mic);
		}
	}

	public void SetVolume(int percent)
	{
		lock (_gate)
		{
			_output?.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, ref _context);
		}
	}

	public void ToggleMic()
	{
		lock (_gate)
		{
			if (_mic is not null && _mic.GetMute(out var muted) == 0)
			{
				_mic.SetMute(!muted, ref _context);
			}
		}
	}

	/// <summary>Default output for every role (what Windows' sound settings do).</summary>
	public void SetDefaultOutput(string id)
	{
		lock (_gate)
		{
			object? policy = null;
			try
			{
				var type = Type.GetTypeFromCLSID(PolicyConfigClsid, throwOnError: true)!;
				policy = Activator.CreateInstance(type)!;
				var config = (IPolicyConfig)policy;
				foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
				{
					Marshal.ThrowExceptionForHR(config.SetDefaultEndpoint(id, role));
				}
			}
			catch (Exception exception) when (exception is COMException or InvalidCastException or TypeLoadException)
			{
				Log.Error("Changing the default output failed", exception);
			}
			finally
			{
				Release(policy);
			}
		}
	}

	// ---------- Endpoints ----------

	/// <summary>Volume objects of the current default output and microphone, notifications on both.</summary>
	private void Bind()
	{
		Unbind();
		if (_enumerator is null)
		{
			return;
		}
		_output = OpenVolume(DataFlow.Render, out _);
		_mic = OpenVolume(DataFlow.Capture, out _micName);
		_output?.RegisterControlChangeNotify(_volumes);
		_mic?.RegisterControlChangeNotify(_volumes);
	}

	private void Unbind()
	{
		if (_output is not null)
		{
			_output.UnregisterControlChangeNotify(_volumes);
			Release(_output);
			_output = null;
		}
		if (_mic is not null)
		{
			_mic.UnregisterControlChangeNotify(_volumes);
			Release(_mic);
			_mic = null;
		}
	}

	private IAudioEndpointVolume? OpenVolume(DataFlow flow, out string? name)
	{
		name = null;
		if (_enumerator!.GetDefaultAudioEndpoint(flow, Role.Console, out var device) != 0 || device is null)
		{
			return null;
		}
		try
		{
			name = ReadString(device, FriendlyName);
			var iid = AudioEndpointVolumeIid;
			return device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var instance) == 0 ? (IAudioEndpointVolume)instance : null;
		}
		finally
		{
			Release(device);
		}
	}

	private List<AudioOutput> ReadOutputs()
	{
		var outputs = new List<AudioOutput>();
		string? defaultId = null;
		if (_enumerator!.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console, out var current) == 0 && current is not null)
		{
			current.GetId(out defaultId);
			Release(current);
		}
		if (_enumerator.EnumAudioEndpoints(DataFlow.Render, DEVICE_STATE_ACTIVE, out var devices) != 0)
		{
			return outputs;
		}
		try
		{
			devices.GetCount(out var count);
			for (uint i = 0; i < count; i++)
			{
				if (devices.Item(i, out var device) != 0)
				{
					continue;
				}
				try
				{
					device.GetId(out var id);
					var name = ReadString(device, FriendlyName) ?? id;
					var kind = (FormFactorKind)(ReadUInt(device, FormFactor) ?? (uint)FormFactorKind.Speakers) switch
					{
						FormFactorKind.Headphones or FormFactorKind.Headset or FormFactorKind.Handset => "headset",
						FormFactorKind.DigitalAudioDisplayDevice => "display",
						_ => "speaker"
					};
					outputs.Add(new AudioOutput(id, name, kind, id == defaultId));
				}
				finally
				{
					Release(device);
				}
			}
		}
		finally
		{
			Release(devices);
		}
		return outputs;
	}

	private static string? ReadString(IMMDevice device, PropertyKey key)
	{
		if (device.OpenPropertyStore(STGM_READ, out var store) != 0)
		{
			return null;
		}
		try
		{
			var property = key;
			if (store.GetValue(ref property, out var value) != 0)
			{
				return null;
			}
			try
			{
				return value.Type == VT_LPWSTR ? Marshal.PtrToStringUni(value.Pointer) : null;
			}
			finally
			{
				PropVariantClear(ref value);
			}
		}
		finally
		{
			Release(store);
		}
	}

	private static uint? ReadUInt(IMMDevice device, PropertyKey key)
	{
		if (device.OpenPropertyStore(STGM_READ, out var store) != 0)
		{
			return null;
		}
		try
		{
			var property = key;
			if (store.GetValue(ref property, out var value) != 0)
			{
				return null;
			}
			try
			{
				return value.Type == VT_UI4 ? value.UInt32 : null;
			}
			finally
			{
				PropVariantClear(ref value);
			}
		}
		finally
		{
			Release(store);
		}
	}

	// COM objects hold native memory until finalisation otherwise (the host allocates too little to collect)
	private static void Release(object? instance)
	{
		if (instance is not null && Marshal.IsComObject(instance))
		{
			Marshal.ReleaseComObject(instance);
		}
	}

	// ---------- Notifications (COM threads) ----------

	private void OnDefaultChanged()
	{
		// Off the notification thread: Core Audio forbids calls back into it from its callbacks
		Task.Run(() =>
		{
			lock (_gate)
			{
				if (!_disposed)
				{
					Bind();
				}
			}
			Changed?.Invoke();
		});
	}

	private void OnChanged() => Changed?.Invoke();

	public void Dispose()
	{
		lock (_gate)
		{
			_disposed = true;
			Unbind();
			if (_enumerator is not null)
			{
				_enumerator.UnregisterEndpointNotificationCallback(_devices);
				Release(_enumerator);
				_enumerator = null;
			}
		}
	}

	[ComVisible(true)]
	private sealed class DeviceNotifications(AudioService owner) : IMMNotificationClient
	{
		public int OnDeviceStateChanged(string id, uint newState)
		{
			owner.OnChanged();
			return 0;
		}

		public int OnDeviceAdded(string id)
		{
			owner.OnChanged();
			return 0;
		}

		public int OnDeviceRemoved(string id)
		{
			owner.OnChanged();
			return 0;
		}

		public int OnDefaultDeviceChanged(DataFlow flow, Role role, string? id)
		{
			// One notification per role: the console role is enough
			if (role == Role.Console)
			{
				owner.OnDefaultChanged();
			}
			return 0;
		}

		public int OnPropertyValueChanged(string id, PropertyKey key) => 0;
	}

	[ComVisible(true)]
	private sealed class VolumeNotifications(AudioService owner) : IAudioEndpointVolumeCallback
	{
		public int OnNotify(IntPtr notifyData)
		{
			owner.OnChanged();
			return 0;
		}
	}
}
