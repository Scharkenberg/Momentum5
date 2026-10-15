using Momentum5.Core.Transport;
using Windows.Devices.Bluetooth;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace Momentum5.Transport.Rfcomm;

/// <summary>Connected RFCOMM byte stream. Created by <see cref="RfcommGaiaTransport"/>; I/O hardening in T-02.</summary>
internal sealed class RfcommConnection : ILinkConnection
{
	private const uint MaxRead = 1024;

	private readonly StreamSocket _socket;
	private readonly BluetoothDevice _device;
	private readonly DataReader _reader;
	private readonly DataWriter _writer;
	private readonly SemaphoreSlim _writeLock = new(1, 1);
	private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private int _disposed;

	public RfcommConnection(StreamSocket socket, BluetoothDevice device)
	{
		_socket = socket;
		_device = device;
		_reader = new DataReader(socket.InputStream) { InputStreamOptions = InputStreamOptions.Partial };
		_writer = new DataWriter(socket.OutputStream);
		_device.ConnectionStatusChanged += OnConnectionStatusChanged;
	}

	public string TransportId => "rfcomm-gaia";

	public Task Closed => _closed.Task;

	/// <summary>Why the link ended, when caused by an I/O error (null for EOF or dispose).</summary>
	public BtError? CloseReason { get; private set; }

	public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
	{
		if (buffer.IsEmpty)
		{
			throw new ArgumentException("Buffer must not be empty.", nameof(buffer));
		}

		if (Volatile.Read(ref _disposed) != 0)
		{
			return 0;
		}

		try
		{
			var want = (uint)Math.Min(buffer.Length, (int)MaxRead);
			var loaded = await _reader.LoadAsync(want).AsTask(ct).ConfigureAwait(false);
			if (loaded == 0)
			{
				_closed.TrySetResult();
				return 0;
			}

			var tmp = new byte[loaded];
			_reader.ReadBytes(tmp);
			tmp.CopyTo(buffer);
			return (int)loaded;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			CloseReason = BtErrors.FromHResult(ex.HResult);
			_closed.TrySetResult();
			return 0;
		}
	}

	public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
	{
		if (Volatile.Read(ref _disposed) != 0)
		{
			throw new LinkException(BtError.ConnectionAborted, unchecked((int)0x80072745), "Link is closed.");
		}

		await _writeLock.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			_writer.WriteBytes(data.ToArray());
			await _writer.StoreAsync().AsTask(ct).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			_closed.TrySetResult();
			throw RfcommGaiaTransport.Wrap(ex, "RFCOMM write failed");
		}
		finally
		{
			_writeLock.Release();
		}
	}

	public ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
		{
			return ValueTask.CompletedTask;
		}

		// ref/06: cancel read -> dispose socket -> unsubscribe -> dispose device
		_socket.Dispose();
		_reader.Dispose();
		_writer.Dispose();
		_device.ConnectionStatusChanged -= OnConnectionStatusChanged;
		_device.Dispose();
		_closed.TrySetResult();
		return ValueTask.CompletedTask;
	}

	private void OnConnectionStatusChanged(BluetoothDevice sender, object args)
	{
		if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
		{
			_closed.TrySetResult();
		}
	}
}
