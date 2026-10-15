using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Momentum5.Client;
using Windows.Graphics;

namespace Momentum5.App.Shell;

/// <summary>
/// Remembers and restores the window rectangle. Monitors are identified by their device-interface name (it embeds the EDID-derived PnP id),
/// restoring falls back to the same monitor model, then the monitor the old rectangle overlapped most, then the primary one.
/// </summary>
internal static class WindowPlacements
{
	private const uint EddGetDeviceInterfaceName = 1;

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct MonitorInfoEx
	{
		public uint Size;
		public RectInt32 Monitor;
		public RectInt32 Work;
		public uint Flags;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
		public string Device;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct DisplayDevice
	{
		public uint Size;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
		public string DeviceName;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
		public string DeviceString;

		public uint StateFlags;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
		public string DeviceId;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
		public string DeviceKey;
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfoEx info);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern bool EnumDisplayDevicesW(string? device, uint index, ref DisplayDevice displayDevice, uint flags);

	[DllImport("shcore.dll")]
	private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

	private static (string? Id, string? Pnp) Identify(DisplayArea area)
	{
		try
		{
			var monitor = Win32Interop.GetMonitorFromDisplayId(area.DisplayId);
			var info = new MonitorInfoEx { Size = (uint)Marshal.SizeOf<MonitorInfoEx>(), Device = string.Empty };
			if (!GetMonitorInfoW(monitor, ref info))
			{
				return (null, null);
			}

			var device = new DisplayDevice { Size = (uint)Marshal.SizeOf<DisplayDevice>(), DeviceName = string.Empty, DeviceString = string.Empty, DeviceId = string.Empty, DeviceKey = string.Empty };
			if (!EnumDisplayDevicesW(info.Device, 0, ref device, EddGetDeviceInterfaceName) || device.DeviceId.Length == 0)
			{
				return (info.Device, null);
			}

			var parts = device.DeviceId.Split('#');
			return (device.DeviceId, parts.Length > 1 ? parts[1] : null);
		}
		catch (Exception ex)
		{
			AppLog.StaticWrite($"monitor id: {ex.Message}");
			return (null, null);
		}
	}

	private static double ScaleOf(DisplayArea area)
	{
		try
		{
			var monitor = Win32Interop.GetMonitorFromDisplayId(area.DisplayId);
			if (GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0)
			{
				return dpi / 96.0;
			}
		}
		catch (Exception ex)
		{
			AppLog.StaticWrite($"monitor dpi: {ex.Message}");
		}

		return 1.0;
	}

	private static long Overlap(RectInt32 a, RectInt32 b)
	{
		var w = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
		var h = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
		return w > 0 && h > 0 ? (long)w * h : 0;
	}

	public static WindowPlacement? Capture(AppWindow window, bool maximized, WindowPlacement? previous)
	{
		var area = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Nearest);
		if (area is null)
		{
			return null;
		}

		var (id, pnp) = Identify(area);
		if (maximized && previous is not null)
		{
			// A maximized rectangle is the monitor, not the user's choice: keep the last normal rectangle and only track the monitor.
			return previous with { Maximized = true, MonitorId = id ?? previous.MonitorId, MonitorPnp = pnp ?? previous.MonitorPnp };
		}

		var scale = ScaleOf(area);
		var work = area.WorkArea;
		var pos = window.Position;
		var size = window.Size;
		return new WindowPlacement(
			(pos.X - work.X) / scale,
			(pos.Y - work.Y) / scale,
			size.Width / scale,
			size.Height / scale,
			maximized,
			id,
			pnp,
			pos.X,
			pos.Y,
			size.Width,
			size.Height);
	}

	private static DisplayArea Choose(IReadOnlyList<DisplayArea> areas, WindowPlacement p)
	{
		var saved = new RectInt32(p.AbsLeft, p.AbsTop, Math.Max(1, p.AbsWidth), Math.Max(1, p.AbsHeight));
		var identified = areas.Select(a => (Area: a, Key: Identify(a))).ToList();

		var exact = p.MonitorId is null ? [] : identified.Where(x => string.Equals(x.Key.Id, p.MonitorId, StringComparison.OrdinalIgnoreCase)).ToList();
		var model = p.MonitorPnp is null ? [] : identified.Where(x => string.Equals(x.Key.Pnp, p.MonitorPnp, StringComparison.OrdinalIgnoreCase)).ToList();
		var pool = exact.Count > 0 ? exact : model.Count > 0 ? model : identified;
		var best = pool.OrderByDescending(x => Overlap(saved, x.Area.OuterBounds)).First();
		if (exact.Count > 0 || model.Count > 0 || Overlap(saved, best.Area.OuterBounds) > 0)
		{
			return best.Area;
		}

		return DisplayArea.Primary ?? areas[0];
	}

	public static void Restore(AppWindow window, WindowPlacement p, bool allowMaximize)
	{
		try
		{
			var areas = DisplayArea.FindAll();
			if (areas.Count == 0)
			{
				return;
			}

			var area = Choose(areas, p);
			var scale = ScaleOf(area);
			var work = area.WorkArea;
			var width = Math.Clamp((int)Math.Round(p.Width * scale), 1, work.Width);
			var height = Math.Clamp((int)Math.Round(p.Height * scale), 1, work.Height);
			var x = Math.Clamp(work.X + (int)Math.Round(p.X * scale), work.X, work.X + work.Width - width);
			var y = Math.Clamp(work.Y + (int)Math.Round(p.Y * scale), work.Y, work.Y + work.Height - height);

			// Move first so the window takes the target monitor's DPI, then size it in that monitor's pixels.
			window.Move(new PointInt32(x, y));
			window.MoveAndResize(new RectInt32(x, y, width, height));
			if (p.Maximized && allowMaximize && window.Presenter is OverlappedPresenter presenter)
			{
				presenter.Maximize();
			}
		}
		catch (Exception ex)
		{
			AppLog.StaticWrite($"restore window placement: {ex.Message}");
		}
	}
}
