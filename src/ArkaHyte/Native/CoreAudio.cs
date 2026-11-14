using System.Runtime.InteropServices;

namespace ArkaHyte.Native;

/// <summary>
/// Windows Core Audio (MMDevice API, endpoint volume) declarations, plus the undocumented IPolicyConfig
/// that Windows' own sound settings use to change the default device. No shell component involved.
/// </summary>
internal static class CoreAudio
{
	public const uint DEVICE_STATE_ACTIVE = 0x1;
	public const uint CLSCTX_ALL = 0x17;
	public const uint STGM_READ = 0x0;
	public const ushort VT_LPWSTR = 31;
	public const ushort VT_UI4 = 19;

	public static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
	public static readonly Guid PolicyConfigClsid = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
	public static readonly Guid AudioEndpointVolumeIid = new("5CDF2C82-841E-4546-9722-0CF74078229A");

	public static readonly PropertyKey FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
	public static readonly PropertyKey FormFactor = new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);

	public enum DataFlow
	{
		Render = 0,
		Capture = 1
	}

	public enum Role
	{
		Console = 0,
		Multimedia = 1,
		Communications = 2
	}

	/// <summary>EndpointFormFactor values the display tells apart.</summary>
	public enum FormFactorKind : uint
	{
		RemoteNetworkDevice = 0,
		Speakers = 1,
		LineLevel = 2,
		Headphones = 3,
		Microphone = 4,
		Headset = 5,
		Handset = 6,
		UnknownDigitalPassthrough = 7,
		Spdif = 8,
		DigitalAudioDisplayDevice = 9
	}

	[StructLayout(LayoutKind.Sequential)]
	public readonly struct PropertyKey(Guid formatId, int propertyId)
	{
		public readonly Guid FormatId = formatId;
		public readonly int PropertyId = propertyId;
	}

	/// <summary>PROPVARIANT, 24 bytes on x64: only the string and 32-bit integer cases are read.</summary>
	[StructLayout(LayoutKind.Explicit, Size = 24)]
	public struct PropVariant
	{
		[FieldOffset(0)] public ushort Type;
		[FieldOffset(8)] public IntPtr Pointer;
		[FieldOffset(8)] public uint UInt32;
	}

	[DllImport("ole32.dll")]
	public static extern int PropVariantClear(ref PropVariant value);

	[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	public interface IMMDeviceEnumerator
	{
		[PreserveSig] int EnumAudioEndpoints(DataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
		[PreserveSig] int GetDefaultAudioEndpoint(DataFlow dataFlow, Role role, out IMMDevice endpoint);
		[PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
		[PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
		[PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
	}

	[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	public interface IMMDeviceCollection
	{
		[PreserveSig] int GetCount(out uint count);
		[PreserveSig] int Item(uint index, out IMMDevice device);
	}

	[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	public interface IMMDevice
	{
		[PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
		[PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
		[PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
		[PreserveSig] int GetState(out uint state);
	}

	[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	public interface IPropertyStore
	{
		[PreserveSig] int GetCount(out uint count);
		[PreserveSig] int GetAt(uint index, out PropertyKey key);
		[PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
		[PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
		[PreserveSig] int Commit();
	}

	[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	public interface IAudioEndpointVolume
	{
		[PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
		[PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
		[PreserveSig] int GetChannelCount(out uint count);
		[PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
		[PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
		[PreserveSig] int GetMasterVolumeLevel(out float levelDb);
		[PreserveSig] int GetMasterVolumeLevelScalar(out float level);
		[PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
		[PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
		[PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
		[PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
		[PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
		[PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
	}

	[ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	public interface IAudioEndpointVolumeCallback
	{
		[PreserveSig] int OnNotify(IntPtr notifyData);
	}

	[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	public interface IMMNotificationClient
	{
		[PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint newState);
		[PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
		[PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
		[PreserveSig] int OnDefaultDeviceChanged(DataFlow flow, Role role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
		[PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
	}

	/// <summary>Undocumented (Windows 7 and later): only SetDefaultEndpoint is called, earlier slots keep the order.</summary>
	[ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	public interface IPolicyConfig
	{
		[PreserveSig] int GetMixFormat(IntPtr deviceId, IntPtr format);
		[PreserveSig] int GetDeviceFormat(IntPtr deviceId, int isDefault, IntPtr format);
		[PreserveSig] int ResetDeviceFormat(IntPtr deviceId);
		[PreserveSig] int SetDeviceFormat(IntPtr deviceId, IntPtr endpointFormat, IntPtr mixFormat);
		[PreserveSig] int GetProcessingPeriod(IntPtr deviceId, int isDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
		[PreserveSig] int SetProcessingPeriod(IntPtr deviceId, IntPtr period);
		[PreserveSig] int GetShareMode(IntPtr deviceId, IntPtr mode);
		[PreserveSig] int SetShareMode(IntPtr deviceId, IntPtr mode);
		[PreserveSig] int GetPropertyValue(IntPtr deviceId, int isFx, IntPtr key, IntPtr value);
		[PreserveSig] int SetPropertyValue(IntPtr deviceId, int isFx, IntPtr key, IntPtr value);
		[PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);
		[PreserveSig] int SetEndpointVisibility(IntPtr deviceId, int visible);
	}
}
