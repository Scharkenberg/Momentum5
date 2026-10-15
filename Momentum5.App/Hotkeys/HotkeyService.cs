using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Momentum5.App.Services;

namespace Momentum5.App.Hotkeys;

public sealed record HotkeyBinding(QuickAction Action, string Gesture);

/// <summary>
/// Global hotkeys via RegisterHotKey. WM_HOTKEY is received through a window subclass on the shell's HWND, which keeps
/// working while the window is hidden in the tray. Failures (e.g. combination taken by another app) are kept in <see cref="Status"/>.
/// </summary>
public sealed class HotkeyService : IDisposable
{
	private const uint ModAlt = 0x1;
	private const uint ModControl = 0x2;
	private const uint ModShift = 0x4;
	private const uint ModWin = 0x8;
	private const uint ModNoRepeat = 0x4000;
	private const int WmHotkey = 0x0312;
	private const nuint SubclassId = 0x4D35;

	private delegate nint SubclassProc(nint hwnd, uint msg, nuint wParam, nint lParam, nuint id, nuint data);

	private readonly nint _hwnd;
	private readonly AppServices _services;
	private readonly SubclassProc _proc;
	private readonly Dictionary<int, QuickAction> _registered = [];
	private readonly Dictionary<QuickAction, string> _status = [];
	private bool _disposed;

	public HotkeyService(nint hwnd, AppServices services)
	{
		_hwnd = hwnd;
		_services = services;
		_proc = WndProc;
		_ = SetWindowSubclass(hwnd, _proc, SubclassId, 0);
		services.SettingsChanged += OnSettingsChanged;
		Reload();
	}

	public static IReadOnlyDictionary<QuickAction, string> Defaults { get; } = new Dictionary<QuickAction, string>
	{
		[QuickAction.Anc] = "Win+Alt+A",
		[QuickAction.Transparency] = "Win+Alt+T",
		[QuickAction.Immersive] = "Win+Alt+I",
	};

	public event EventHandler? StatusChanged;

	/// <summary>Per action: "Registered", "Disabled" or the failure reason.</summary>
	public IReadOnlyDictionary<QuickAction, string> Status => _status;

	public static string GestureFor(AppServices services, QuickAction action) =>
		services.Current.Hotkeys.TryGetValue(QuickActions.Id(action), out var g) ? g : Defaults[action];

	/// <summary>Parses "Win+Alt+A", "Ctrl+Shift+F5". Requires at least one modifier and exactly one key.</summary>
	public static bool TryParse(string? gesture, out uint modifiers, out uint key)
	{
		modifiers = 0;
		key = 0;
		if (string.IsNullOrWhiteSpace(gesture))
		{
			return false;
		}

		var parts = gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
		foreach (var part in parts)
		{
			switch (part.ToUpperInvariant())
			{
				case "WIN":
					modifiers |= ModWin;
					break;
				case "ALT":
					modifiers |= ModAlt;
					break;
				case "CTRL" or "CONTROL":
					modifiers |= ModControl;
					break;
				case "SHIFT":
					modifiers |= ModShift;
					break;
				default:
					if (key != 0 || !TryKey(part, out key))
					{
						return false;
					}

					break;
			}
		}

		return modifiers != 0 && key != 0;
	}

	private static bool TryKey(string text, out uint key)
	{
		key = 0;
		if (text.Length == 1 && char.IsAsciiLetterOrDigit(text[0]))
		{
			key = char.ToUpperInvariant(text[0]);
			return true;
		}

		if (text.Length is 2 or 3 && (text[0] is 'F' or 'f') && int.TryParse(text.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var f) && f is >= 1 and <= 24)
		{
			key = (uint)(0x70 + f - 1);
			return true;
		}

		return false;
	}

	public void Reload()
	{
		foreach (var id in _registered.Keys)
		{
			_ = UnregisterHotKey(_hwnd, id);
		}

		_registered.Clear();
		_status.Clear();
		var id2 = 1;
		foreach (var action in Enum.GetValues<QuickAction>())
		{
			var gesture = GestureFor(_services, action);
			if (string.IsNullOrWhiteSpace(gesture))
			{
				_status[action] = "Disabled";
				continue;
			}

			if (!TryParse(gesture, out var mods, out var key))
			{
				_status[action] = $"Invalid gesture '{gesture}' (example: Win+Alt+A)";
				continue;
			}

			if (RegisterHotKey(_hwnd, id2, mods | ModNoRepeat, key))
			{
				_registered[id2] = action;
				_status[action] = "Registered";
				id2++;
			}
			else
			{
				var error = Marshal.GetLastWin32Error();
				_status[action] = error == 1409 ? $"{gesture} is already used by another app" : $"Registration failed (Win32 error {error})";
			}
		}

		StatusChanged?.Invoke(this, EventArgs.Empty);
	}

	private void OnSettingsChanged(object? sender, EventArgs e) => Reload();

	private nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam, nuint id, nuint data)
	{
		if (msg == WmHotkey && _registered.TryGetValue((int)wParam, out var action))
		{
			_services.Ui.Post(() => _ = QuickActions.ToggleAsync(_services, action));
			return 0;
		}

		return DefSubclassProc(hwnd, msg, wParam, lParam);
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_services.SettingsChanged -= OnSettingsChanged;
		foreach (var id in _registered.Keys)
		{
			_ = UnregisterHotKey(_hwnd, id);
		}

		_ = RemoveWindowSubclass(_hwnd, _proc, SubclassId);
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool UnregisterHotKey(nint hwnd, int id);

	[DllImport("comctl32.dll")]
	private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);

	[DllImport("comctl32.dll")]
	private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);

	[DllImport("comctl32.dll")]
	private static extern nint DefSubclassProc(nint hwnd, uint msg, nuint wParam, nint lParam);
}
