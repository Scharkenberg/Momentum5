using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Momentum5.App.Services;

/// <summary>Named mutex plus activation event: a second launch wakes the first and exits.</summary>
public sealed class SingleInstance : IDisposable
{
	private const string MutexName = @"Local\Momentum5.SingleInstance";
	private const string EventName = @"Local\Momentum5.Activate";

	private readonly Mutex _mutex;
	private readonly EventWaitHandle _activate;
	private readonly CancellationTokenSource _cts = new();
	private Thread? _listener;

	private SingleInstance(Mutex mutex, EventWaitHandle activate)
	{
		_mutex = mutex;
		_activate = activate;
	}

	[DllImport("user32.dll")]
	private static extern bool AllowSetForegroundWindow(int processId);

	/// <summary>Returns the instance guard when this is the first process, otherwise signals the first one and returns null.</summary>
	public static SingleInstance? TryAcquire()
	{
		var mutex = new Mutex(true, MutexName, out var first);
		var activate = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
		if (!first)
		{
			_ = AllowSetForegroundWindow(-1);
			activate.Set();
			activate.Dispose();
			mutex.Dispose();
			return null;
		}

		return new SingleInstance(mutex, activate);
	}

	public void Listen(Action onActivated)
	{
		ArgumentNullException.ThrowIfNull(onActivated);
		_listener = new Thread(() =>
		{
			var handles = new[] { _activate, _cts.Token.WaitHandle };
			while (WaitHandle.WaitAny(handles) == 0)
			{
				onActivated();
			}
		})
		{
			IsBackground = true,
			Name = "Momentum5.SingleInstance",
		};
		_listener.Start();
	}

	public void Dispose()
	{
		_cts.Cancel();
		_listener?.Join(500);
		try
		{
			_mutex.ReleaseMutex();
		}
		catch (ApplicationException)
		{
		}

		_mutex.Dispose();
		_activate.Dispose();
		_cts.Dispose();
	}
}
