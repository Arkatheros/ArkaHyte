using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ArkaHyte;

/// <summary>
/// Tray menu styled like the case display: red-black panel, saber-red border and accents,
/// a blade bar on the hovered item, red check box, rounded Windows 11 corners.
/// </summary>
internal sealed class SithMenuRenderer : ToolStripProfessionalRenderer
{
	private static readonly Color Panel = Color.FromArgb(15, 6, 7);
	private static readonly Color Hover = Color.FromArgb(58, 10, 16);
	private static readonly Color Blade = Color.FromArgb(255, 31, 51);
	private static readonly Color Bone = Color.FromArgb(243, 233, 234);
	private static readonly Color Line = Color.FromArgb(110, 255, 31, 51);

	// Bahnschrift ships with Windows 10+ and is the closest system face to the display's Rajdhani
	public static Font CreateFont() => new("Bahnschrift", 10.5f, FontStyle.Regular, GraphicsUnit.Point);

	private const int TitleGap = 7;

	private readonly Font _titleFont = new("Bahnschrift", 10.5f, FontStyle.Bold, GraphicsUnit.Point);

	public SithMenuRenderer()
		: base(new ProfessionalColorTable())
	{
		RoundedEdges = false;
	}

	/// <summary>Applies the theme to a menu: renderer, font, roomier items, rounded corners.</summary>
	public static void Apply(ContextMenuStrip menu)
	{
		menu.Renderer = new SithMenuRenderer();
		menu.Font = CreateFont();
		menu.ShowImageMargin = true;
		menu.Padding = new Padding(2, 6, 2, 6);
		foreach (ToolStripItem item in menu.Items)
		{
			if (item is ToolStripMenuItem)
			{
				item.Padding = new Padding(4, 7, 4, 7);
				item.ForeColor = Bone;
			}
			else if (item is ToolStripLabel title)
			{
				// Extra right padding makes room for the gap added before the title text
				title.Padding = new Padding(4, 6, 4 + TitleGap, 6);
				title.ImageScaling = ToolStripItemImageScaling.SizeToFit;
			}
		}
		menu.Opened += (_, _) => RoundCorners(menu.Handle);
	}

	protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
	{
		using var brush = new SolidBrush(Panel);
		e.Graphics.FillRectangle(brush, e.AffectedBounds);
	}

	protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
	{
		using var brush = new SolidBrush(Panel);
		e.Graphics.FillRectangle(brush, e.AffectedBounds);
	}

	protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
	{
		using var pen = new Pen(Line);
		var bounds = e.ToolStrip.ClientRectangle;
		e.Graphics.DrawRectangle(pen, 0, 0, bounds.Width - 1, bounds.Height - 1);
	}

	protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
	{
		if (!e.Item.Selected || !e.Item.Enabled)
		{
			return;
		}
		var bounds = new Rectangle(3, 1, e.Item.Width - 6, e.Item.Height - 2);
		using var hover = new SolidBrush(Hover);
		e.Graphics.FillRectangle(hover, bounds);
		using var blade = new SolidBrush(Blade);
		e.Graphics.FillRectangle(blade, bounds.Left, bounds.Top + 3, 3, bounds.Height - 6);
	}

	protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
	{
		// The label at the top is the menu title: saber red and bold
		var isTitle = e.Item is ToolStripLabel;
		var font = isTitle ? _titleFont : e.TextFont ?? e.Item.Font;
		var color = isTitle ? Blade : e.Item.Selected ? Color.White : Bone;

		// Bahnschrift has a tall ascent, so the stock line-box centering sits the letters too low.
		// Centre the visible glyphs instead: put the middle of the x-to-cap height on the row centre.
		var family = font.FontFamily;
		var sizePx = font.SizeInPoints * e.Graphics.DpiY / 72f;
		var ascentPx = sizePx * family.GetCellAscent(font.Style) / family.GetEmHeight(font.Style);
		var baseline = e.Item.ContentRectangle.Top + e.Item.ContentRectangle.Height / 2f + sizePx * 0.30f;
		var top = (int)Math.Round(baseline - ascentPx);
		TextRenderer.DrawText(
			e.Graphics,
			e.Text,
			font,
			// Breathing room between the title's logo and its text
			new Point(e.TextRectangle.Left + (isTitle ? TitleGap : 0), top),
			color,
			TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
	}

	protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
	{
		var y = e.Item.Height / 2;
		using var pen = new Pen(Line);
		e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
	}

	protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
	{
		// Centred on the whole row, not on the image slot, so it lines up with the text
		var size = Math.Min(e.ImageRectangle.Width, e.ImageRectangle.Height) - 2;
		var box = new Rectangle(
			e.ImageRectangle.Left + (e.ImageRectangle.Width - size) / 2,
			(e.Item.Height - size) / 2,
			size,
			size);
		e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
		using var fill = new SolidBrush(Blade);
		e.Graphics.FillRectangle(fill, box);
		using var tick = new Pen(Color.White, Math.Max(1.6f, size / 8f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
		e.Graphics.DrawLines(tick,
		[
			new PointF(box.Left + size * 0.22f, box.Top + size * 0.52f),
			new PointF(box.Left + size * 0.42f, box.Top + size * 0.72f),
			new PointF(box.Left + size * 0.78f, box.Top + size * 0.30f)
		]);
	}

	protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
	{
		e.ArrowColor = Blade;
		base.OnRenderArrow(e);
	}

	private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
	private const int DWMWCP_ROUNDSMALL = 3;

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

	// Windows 11 only; ignored silently on older systems
	private static void RoundCorners(IntPtr handle)
	{
		var preference = DWMWCP_ROUNDSMALL;
		_ = DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
	}
}
