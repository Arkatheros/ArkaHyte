using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ArkaHyte.Native;

/// <summary>
/// Icons of any shell item ("shell:AppsFolder\&lt;app id&gt;", files, Store apps) through
/// IShellItemImageFactory, returned as PNG data URLs with their transparency preserved.
/// Call from an STA thread.
/// </summary>
internal static class ShellIcons
{
	private const int SIIGBF_BIGGERSIZEOK = 0x1;
	private const int SIIGBF_ICONONLY = 0x4;

	[StructLayout(LayoutKind.Sequential)]
	private struct NativeSize
	{
		public int Width;
		public int Height;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct BitmapHeader
	{
		public int Type;
		public int Width;
		public int Height;
		public int WidthBytes;
		public ushort Planes;
		public ushort BitsPixel;
		public IntPtr Bits;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct BitmapInfoHeader
	{
		public uint Size;
		public int Width;
		public int Height;
		public ushort Planes;
		public ushort BitCount;
		public uint Compression;
		public uint SizeImage;
		public int XPelsPerMeter;
		public int YPelsPerMeter;
		public uint ClrUsed;
		public uint ClrImportant;
	}

	[ComImport]
	[Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IShellItemImageFactory
	{
		[PreserveSig]
		int GetImage(NativeSize size, int flags, out IntPtr bitmap);
	}

	[DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
	private static extern void SHCreateItemFromParsingName(
		string path,
		IntPtr bindContext,
		[MarshalAs(UnmanagedType.LPStruct)] Guid riid,
		[MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

	[DllImport("gdi32.dll")]
	private static extern int GetObject(IntPtr handle, int size, out BitmapHeader bitmap);

	[DllImport("gdi32.dll")]
	private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);

	[DllImport("gdi32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DeleteObject(IntPtr handle);

	[DllImport("user32.dll")]
	private static extern IntPtr GetDC(IntPtr window);

	[DllImport("user32.dll")]
	private static extern int ReleaseDC(IntPtr window, IntPtr dc);

	public static string? GetPngDataUrl(string parsingName, int size)
	{
		IShellItemImageFactory factory;
		try
		{
			SHCreateItemFromParsingName(parsingName, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out factory);
		}
		catch (Exception exception) when (exception is COMException or FileNotFoundException or ArgumentException or UnauthorizedAccessException)
		{
			return null;
		}

		try
		{
			var hr = factory.GetImage(new NativeSize { Width = size, Height = size }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out var handle);
			if (hr != 0 || handle == IntPtr.Zero)
			{
				return null;
			}
			try
			{
				return ToPngDataUrl(handle);
			}
			finally
			{
				DeleteObject(handle);
			}
		}
		finally
		{
			Marshal.ReleaseComObject(factory);
		}
	}

	// Copies the HBITMAP as top-down 32-bit BGRA: Image.FromHbitmap would drop the alpha channel
	private static string? ToPngDataUrl(IntPtr handle)
	{
		if (GetObject(handle, Marshal.SizeOf<BitmapHeader>(), out var header) == 0 || header.Width <= 0 || header.Height <= 0)
		{
			return null;
		}
		var width = header.Width;
		var height = header.Height;
		var info = new BitmapInfoHeader
		{
			Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
			Width = width,
			Height = -height,
			Planes = 1,
			BitCount = 32
		};
		var pixels = new byte[width * height * 4];
		var dc = GetDC(IntPtr.Zero);
		try
		{
			if (GetDIBits(dc, handle, 0, (uint)height, pixels, ref info, 0) == 0)
			{
				return null;
			}
		}
		finally
		{
			ReleaseDC(IntPtr.Zero, dc);
		}

		// Legacy icons come back without any alpha: treat them as opaque instead of invisible
		var hasAlpha = false;
		for (var i = 3; i < pixels.Length; i += 4)
		{
			if (pixels[i] != 0)
			{
				hasAlpha = true;
				break;
			}
		}
		if (!hasAlpha)
		{
			for (var i = 3; i < pixels.Length; i += 4)
			{
				pixels[i] = 255;
			}
		}

		using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
		var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
		try
		{
			for (var row = 0; row < height; row++)
			{
				Marshal.Copy(pixels, row * width * 4, data.Scan0 + row * data.Stride, width * 4);
			}
		}
		finally
		{
			bitmap.UnlockBits(data);
		}
		using var stream = new MemoryStream();
		bitmap.Save(stream, ImageFormat.Png);
		return $"data:image/png;base64,{Convert.ToBase64String(stream.ToArray())}";
	}
}
