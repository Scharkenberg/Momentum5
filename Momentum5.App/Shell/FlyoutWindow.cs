using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Momentum5.App.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace Momentum5.App.Shell;

/// <summary>
/// Tray quick panel: borderless, always on top, hidden from Alt+Tab and the taskbar, closes itself when it loses focus.
/// It sits against the taskbar edge (top, bottom, left or right) next to the tray icon and is exactly as tall as its quick controls.
/// </summary>
public sealed class FlyoutWindow : Window
{
	private const double GapDip = 8;
	private const double MinHeightDip = 160;
	private const double FallbackHeightDip = 480;
	private const double BottomPaddingDip = 8;
	private double _lastFitDip;

	private readonly CompactView _view = new() { ShowOpenFull = true };
	private readonly Grid _root = new();
	private DateTime _hiddenAt = DateTime.MinValue;
	private PointInt32 _anchor;

	public FlyoutWindow()
	{
		_root.Children.Add(_view);
		Content = _root;
		SystemBackdrop = new DesktopAcrylicBackdrop();
		Title = "Momentum5 quick panel";
		ExtendsContentIntoTitleBar = false;

		var presenter = OverlappedPresenter.Create();
		presenter.IsResizable = false;
		presenter.IsMaximizable = false;
		presenter.IsMinimizable = false;
		presenter.IsAlwaysOnTop = true;
		presenter.SetBorderAndTitleBar(true, false);
		AppWindow.SetPresenter(presenter);
		AppWindow.IsShownInSwitchers = false;

		_view.OpenFullRequested += (_, _) =>
		{
			HidePanel();
			App.ShowWindow();
		};
		_view.LayoutChanged += (_, _) =>
		{
			if (AppWindow.IsVisible && _view.IsHome && Math.Abs(_view.HomeContentHeight - _lastFitDip) > 1)
			{
				PlaceAndFit();
			}
		};
		Activated += (_, args) =>
		{
			if (args.WindowActivationState == WindowActivationState.Deactivated)
			{
				HidePanel();
			}
		};
	}

	/// <summary>Shows the panel next to the tray (or hides it when it is already open).</summary>
	public void Toggle()
	{
		if (AppWindow.IsVisible)
		{
			HidePanel();
			return;
		}

		// The tray click that deactivated the panel also arrives here: ignore it so the panel does not flicker back.
		if ((DateTime.UtcNow - _hiddenAt).TotalMilliseconds < 300)
		{
			return;
		}

		ApplyTheme();
		_view.Refresh();
		_ = GetCursorPos(out var cursor);
		_anchor = new PointInt32(cursor.X, cursor.Y);
		PlaceAndFit();
		AppWindow.Show();
		Activate();
		_ = SetForegroundWindow(WindowNative.GetWindowHandle(this));
	}

	public void HidePanel()
	{
		if (AppWindow.IsVisible)
		{
			AppWindow.Hide();
			_hiddenAt = DateTime.UtcNow;
		}
	}

	public void Shutdown()
	{
		_view.Shutdown();
		Close();
	}

	/// <summary>Sizes the client area to the quick controls and docks the window to the taskbar edge nearest the anchor (tray click).</summary>
	private void PlaceAndFit()
	{
		var display = DisplayArea.GetFromPoint(_anchor, DisplayAreaFallback.Nearest);
		var work = display.WorkArea;
		var outer = display.OuterBounds;

		// Make sure the window lives on the target monitor so the DPI we read is that monitor's.
		AppWindow.Move(new PointInt32(work.X + 8, work.Y + 8));
		var scale = Math.Max(1.0, GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0);

		var gap = (int)Math.Round(GapDip * scale);
		var widthPx = (int)Math.Round(CompactView.PreferredWidth * scale);
		var wantedDip = _view.HomeContentHeight > 1 ? _view.HomeContentHeight + BottomPaddingDip : FallbackHeightDip;
		_lastFitDip = _view.HomeContentHeight;
		var heightPx = (int)Math.Round(Math.Max(MinHeightDip, wantedDip) * scale);
		widthPx = Math.Min(widthPx, work.Width - (2 * gap));
		heightPx = Math.Min(heightPx, work.Height - (2 * gap));
		AppWindow.ResizeClient(new SizeInt32(widthPx, heightPx));
		var size = AppWindow.Size;

		// Which edge hosts the taskbar: the work area is inset on that side. Auto-hide taskbars leave no inset, so use the edge nearest the tray click.
		var insetTop = work.Y - outer.Y;
		var insetBottom = (outer.Y + outer.Height) - (work.Y + work.Height);
		var insetLeft = work.X - outer.X;
		var insetRight = (outer.X + outer.Width) - (work.X + work.Width);
		var edge = Edge.Bottom;
		var best = Math.Max(Math.Max(insetTop, insetBottom), Math.Max(insetLeft, insetRight));
		if (best > 0)
		{
			edge = best == insetTop ? Edge.Top : best == insetBottom ? Edge.Bottom : best == insetLeft ? Edge.Left : Edge.Right;
		}
		else
		{
			var dTop = _anchor.Y - outer.Y;
			var dBottom = outer.Y + outer.Height - _anchor.Y;
			var dLeft = _anchor.X - outer.X;
			var dRight = outer.X + outer.Width - _anchor.X;
			var min = Math.Min(Math.Min(dTop, dBottom), Math.Min(dLeft, dRight));
			edge = min == dTop ? Edge.Top : min == dBottom ? Edge.Bottom : min == dLeft ? Edge.Left : Edge.Right;
		}

		int x;
		int y;
		switch (edge)
		{
			case Edge.Top:
				y = work.Y + gap;
				x = _anchor.X - (size.Width / 2);
				break;
			case Edge.Left:
				x = work.X + gap;
				y = _anchor.Y - (size.Height / 2);
				break;
			case Edge.Right:
				x = work.X + work.Width - size.Width - gap;
				y = _anchor.Y - (size.Height / 2);
				break;
			default:
				y = work.Y + work.Height - size.Height - gap;
				x = _anchor.X - (size.Width / 2);
				break;
		}

		x = Math.Clamp(x, work.X + gap, Math.Max(work.X + gap, work.X + work.Width - size.Width - gap));
		y = Math.Clamp(y, work.Y + gap, Math.Max(work.Y + gap, work.Y + work.Height - size.Height - gap));
		AppWindow.Move(new PointInt32(x, y));
	}

	private void ApplyTheme() =>
		_root.RequestedTheme = App.Services.Current.Theme switch
		{
			Momentum5.Client.AppTheme.Light => ElementTheme.Light,
			Momentum5.Client.AppTheme.Dark => ElementTheme.Dark,
			_ => ElementTheme.Default,
		};

	private enum Edge
	{
		Top,
		Bottom,
		Left,
		Right,
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NativePoint
	{
		public int X;
		public int Y;
	}

	[DllImport("user32.dll")]
	private static extern bool GetCursorPos(out NativePoint point);

	[DllImport("user32.dll")]
	private static extern uint GetDpiForWindow(nint hwnd);

	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(nint hwnd);
}
