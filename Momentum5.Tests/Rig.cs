using Microsoft.Extensions.Time.Testing;
using Momentum5.Core.Settings;
using Momentum5.Core.Transport;
using Momentum5.Protocol;
using Momentum5.Simulation;

namespace Momentum5.Tests;

/// <summary>Client side of a loopback link wired to a <see cref="FakeM5"/>.</summary>
internal sealed class Rig : IAsyncDisposable
{
	private readonly GaiaDeframer _deframer = new();
	private readonly byte[] _buffer = new byte[512];

	private Rig(FakeM5 fake, LoopbackTransport transport, ILinkConnection client, FakeTimeProvider? time)
	{
		Fake = fake;
		Transport = transport;
		Client = client;
		Time = time;
	}

	public FakeM5 Fake { get; }

	public LoopbackTransport Transport { get; }

	public ILinkConnection Client { get; }

	public FakeTimeProvider? Time { get; }

	public long DiscardedBytes => _deframer.DiscardedBytes;

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	public static async Task<Rig> StartAsync(FakeM5Options? options = null, LoopbackOptions? loop = null, FakeTimeProvider? time = null)
	{
		var fake = new FakeM5(options, time);
		var transport = new LoopbackTransport(loop);
		fake.Attach(transport);
		var client = await transport.ConnectAsync(new DeviceIdentity(0, null, null, true, true), Ct);
		return new Rig(fake, transport, client, time);
	}

	public async Task SendAsync(ushort command, string hex = "", GaiaVendor vendor = GaiaVendor.Sennheiser) =>
		await Client.WriteAsync(new GaiaFrame(vendor, command, Hex.Parse(hex)).Encode(), Ct);

	public async Task<GaiaFrame> ReadAsync(TimeSpan? timeout = null)
	{
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
		cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
		while (true)
		{
			if (_deframer.TryRead(out var frame))
			{
				return frame;
			}

			var n = await Client.ReadAsync(_buffer, cts.Token);
			if (n == 0)
			{
				throw new EndOfStreamException("Link closed.");
			}

			_deframer.Append(_buffer.AsSpan(0, n));
		}
	}

	/// <summary>Null when nothing arrives within <paramref name="wait"/> (real time).</summary>
	public async Task<GaiaFrame?> TryReadAsync(TimeSpan wait)
	{
		try
		{
			return await ReadAsync(wait);
		}
		catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
		{
			return null;
		}
	}

	public async Task<GaiaFrame> RequestAsync(ushort command, string hex = "", GaiaVendor vendor = GaiaVendor.Sennheiser)
	{
		await SendAsync(command, hex, vendor);
		return await ReadAsync();
	}

	/// <summary>Sends the SET and returns the first reply frame (ACK or error).</summary>
	public async Task<GaiaFrame> SetAsync<T>(SettingKey<T> key, T value)
	{
		var binding = Support.Of(key);
		Assert.True(binding.TryEncodeSet(value, key.Index, out var payload, out var error), error);
		await Client.WriteAsync(new GaiaFrame(binding.Vendor, binding.SetCmd!.Value, payload).Encode(), Ct);
		return await ReadAsync();
	}

	public async Task<T> GetAsync<T>(SettingKey<T> key)
	{
		var binding = Support.Of(key);
		await Client.WriteAsync(
			new GaiaFrame(binding.Vendor, binding.GetCmd!.Value, binding.BuildGetPayload(key.Index)).Encode(), Ct);
		var frame = await ReadAsync();
		Assert.Equal(GaiaMessageKind.Response, GaiaWord.Kind(frame.Command));
		Assert.True(binding.Format.TryDecode(frame.Payload.Span, key.Index, out var value), $"decode failed for {key.Name}");
		return value;
	}

	/// <summary>Reads one frame while advancing fake time; returns the frame and the number of steps taken.</summary>
	public async Task<(GaiaFrame Frame, int Steps)> ReadAdvancingAsync(TimeSpan step, int maxSteps = 200)
	{
		Assert.NotNull(Time);
		var read = ReadAsync(TimeSpan.FromSeconds(30));
		var steps = 0;
		while (!read.IsCompleted && steps < maxSteps)
		{
			Time.Advance(step);
			steps++;
			await Task.Delay(10, Ct);
		}

		return (await read, steps);
	}

	public static string PayloadHex(GaiaFrame frame) => Convert.ToHexString(frame.Payload.Span);

	public ValueTask DisposeAsync() => Client.DisposeAsync();
}
