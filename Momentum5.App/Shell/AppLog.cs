using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Momentum5.App.Shell;

/// <summary>
/// Process-wide, thread-safe ring buffer of log lines (last 2000) with a changed event. Feeds diagnostics
/// export (U-15) and the in-app log surfaces; never throws.
/// </summary>
public sealed class AppLog
{
	private const int Capacity = 2000;
	private readonly Lock _gate = new();
	private readonly Queue<(DateTimeOffset Time, string Line)> _lines = new();
	private string[] _snapshot = [];

	public event EventHandler? Changed;

	/// <summary>Last lines, oldest first (cached snapshot; cheap to read from any thread).</summary>
	public IReadOnlyList<string> Lines => Snapshot();

	/// <summary>Appends one line (timestamp + text). Never throws.</summary>
	public void Write(string message)
	{
		ArgumentNullException.ThrowIfNull(message);
		lock (_gate)
		{
			_lines.Enqueue((_timeNow(), message));
			while (_lines.Count > Capacity)
			{
				_lines.Dequeue();
			}

			_snapshot = [];
		}

		Changed?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>For non-DI callers (static UI dispatch paths). May be null until the host is built.</summary>
	public static AppLog? Current { get; internal set; }

	/// <summary>Appends via <see cref="Current"/> when set; otherwise only Debug-writes.</summary>
	public static void StaticWrite(string message)
	{
		System.Diagnostics.Debug.WriteLine(message);
		Current?.Write(message);
	}

	private string[] Snapshot()
	{
		lock (_gate)
		{
			if (_snapshot.Length == 0 && _lines.Count > 0)
			{
				_snapshot = _lines.Select(e => $"{e.Time:HH:mm:ss.fff} {e.Line}").ToArray();
			}

			return _snapshot;
		}
	}

	private DateTimeOffset _timeNow() => DateTimeOffset.Now;

	/// <summary>Sets the process-wide instance (called once by the composition root).</summary>
	internal void MakeCurrent()
	{
		Current = this;
	}
}
