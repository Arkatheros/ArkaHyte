using System.Runtime.InteropServices;
using ArkaHyte.Native;

namespace ArkaHyte.Sensors;

/// <summary>
/// Current Wi-Fi link through the native WLAN API, independent of the Windows display language.
/// Structures are read at fixed offsets (x64 layout of wlanapi.h) instead of being marshalled.
/// </summary>
public sealed class WifiReader : IDisposable
{
	// WLAN_INTERFACE_INFO_LIST / WLAN_INTERFACE_INFO
	private const int InterfaceListHeader = 8;
	private const int InterfaceInfoSize = 532;
	private const int InterfaceStateOffset = 528;

	// WLAN_CONNECTION_ATTRIBUTES
	private const int SsidLengthOffset = 520;
	private const int SsidOffset = 524;
	private const int BssidOffset = 560;
	private const int PhyTypeOffset = 568;
	private const int SignalQualityOffset = 576;
	private const int RxRateOffset = 580;
	private const int AuthAlgorithmOffset = 596;

	// WLAN_BSS_LIST / WLAN_BSS_ENTRY
	private const int BssListHeader = 8;
	private const int BssEntrySize = 360;
	private const int BssEntryBssidOffset = 40;
	private const int BssEntryRssiOffset = 56;
	private const int BssEntryFrequencyOffset = 92;

	private IntPtr _handle;

	public WifiData? Read()
	{
		if (_handle == IntPtr.Zero && NativeMethods.WlanOpenHandle(NativeMethods.WLAN_CLIENT_VERSION_2, IntPtr.Zero, out _, out _handle) != 0)
		{
			_handle = IntPtr.Zero;
			return null;
		}

		if (NativeMethods.WlanEnumInterfaces(_handle, IntPtr.Zero, out var list) != 0)
		{
			return null;
		}
		try
		{
			var count = Marshal.ReadInt32(list);
			for (var i = 0; i < count; i++)
			{
				var info = list + InterfaceListHeader + i * InterfaceInfoSize;
				if (Marshal.ReadInt32(info, InterfaceStateOffset) != NativeMethods.WlanInterfaceStateConnected)
				{
					continue;
				}
				var guid = Marshal.PtrToStructure<Guid>(info);
				var wifi = ReadInterface(guid);
				if (wifi is not null)
				{
					return wifi;
				}
			}
			return null;
		}
		finally
		{
			NativeMethods.WlanFreeMemory(list);
		}
	}

	private WifiData? ReadInterface(Guid guid)
	{
		if (NativeMethods.WlanQueryInterface(_handle, ref guid, NativeMethods.WlanIntfOpcodeCurrentConnection, IntPtr.Zero, out _, out var data, IntPtr.Zero) != 0)
		{
			return null;
		}

		string ssid;
		byte[] bssid;
		int phyType;
		int signal;
		double linkMbps;
		int authAlgorithm;
		try
		{
			var ssidLength = Math.Clamp(Marshal.ReadInt32(data, SsidLengthOffset), 0, 32);
			var ssidBytes = new byte[ssidLength];
			Marshal.Copy(data + SsidOffset, ssidBytes, 0, ssidLength);
			ssid = System.Text.Encoding.UTF8.GetString(ssidBytes);
			bssid = new byte[6];
			Marshal.Copy(data + BssidOffset, bssid, 0, 6);
			phyType = Marshal.ReadInt32(data, PhyTypeOffset);
			signal = Marshal.ReadInt32(data, SignalQualityOffset);
			linkMbps = (uint)Marshal.ReadInt32(data, RxRateOffset) / 1000d;
			authAlgorithm = Marshal.ReadInt32(data, AuthAlgorithmOffset);
		}
		finally
		{
			NativeMethods.WlanFreeMemory(data);
		}

		int? channel = null;
		if (NativeMethods.WlanQueryInterface(_handle, ref guid, NativeMethods.WlanIntfOpcodeChannelNumber, IntPtr.Zero, out _, out var channelData, IntPtr.Zero) == 0)
		{
			channel = Marshal.ReadInt32(channelData);
			NativeMethods.WlanFreeMemory(channelData);
		}

		var (rssi, frequencyKhz) = ReadBss(guid, bssid);
		double? band = frequencyKhz switch
		{
			null or 0 => null,
			< 3_000_000 => 2.4,
			< 5_925_000 => 5,
			_ => 6
		};

		return new WifiData(
			ssid,
			signal,
			rssi,
			channel,
			band,
			StandardName(phyType),
			GenerationName(phyType, band),
			SecurityName(authAlgorithm),
			linkMbps);
	}

	/// <summary>RSSI and centre frequency of the access point we are associated with.</summary>
	private (int? Rssi, uint? FrequencyKhz) ReadBss(Guid guid, byte[] bssid)
	{
		if (NativeMethods.WlanGetNetworkBssList(_handle, ref guid, IntPtr.Zero, 3, false, IntPtr.Zero, out var list) != 0)
		{
			return (null, null);
		}
		try
		{
			var count = Marshal.ReadInt32(list, 4);
			var candidate = new byte[6];
			for (var i = 0; i < count; i++)
			{
				var entry = list + BssListHeader + i * BssEntrySize;
				Marshal.Copy(entry + BssEntryBssidOffset, candidate, 0, 6);
				if (candidate.AsSpan().SequenceEqual(bssid))
				{
					return (Marshal.ReadInt32(entry, BssEntryRssiOffset), (uint)Marshal.ReadInt32(entry, BssEntryFrequencyOffset));
				}
			}
			return (null, null);
		}
		finally
		{
			NativeMethods.WlanFreeMemory(list);
		}
	}

	// DOT11_PHY_TYPE
	private static string StandardName(int phyType) => phyType switch
	{
		4 => "802.11a",
		5 => "802.11b",
		6 => "802.11g",
		7 => "802.11n",
		8 => "802.11ac",
		9 => "802.11ad",
		10 => "802.11ax",
		11 => "802.11be",
		_ => "802.11"
	};

	private static string GenerationName(int phyType, double? band) => phyType switch
	{
		7 => "Wi-Fi 4",
		8 => "Wi-Fi 5",
		10 => band >= 6 ? "Wi-Fi 6E" : "Wi-Fi 6",
		11 => "Wi-Fi 7",
		_ => "Wi-Fi"
	};

	// DOT11_AUTH_ALGORITHM, sent as stable codes; the UI picks the wording
	private static string SecurityName(int algorithm) => algorithm switch
	{
		1 => "open",
		2 => "wep",
		3 or 4 => "wpa",
		6 => "wpa2-enterprise",
		7 => "wpa2-personal",
		8 or 11 => "wpa3-enterprise",
		9 => "wpa3-personal",
		10 => "owe",
		_ => "unknown"
	};

	public void Dispose()
	{
		if (_handle != IntPtr.Zero)
		{
			NativeMethods.WlanCloseHandle(_handle, IntPtr.Zero);
			_handle = IntPtr.Zero;
		}
	}
}
