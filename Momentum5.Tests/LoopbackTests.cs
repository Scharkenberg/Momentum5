using Microsoft.Extensions.Time.Testing;
using Momentum5.Core.Transport;
using Momentum5.Simulation;

namespace Momentum5.Tests;

public class LoopbackTests
{
	private static readonly byte[] One = [1];
	private static readonly byte[] Two = [2];
	private static readonly byte[] Three = [1, 2, 3];

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	private static byte[] Pattern(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(i * 7))];

	private static async Task<byte[]> ReadExactAsync(ILinkConnection connection, int count)
	{
		var buffer = new byte[count];
		var got = 0;
		while (got < count)
		{
			var n = await connection.ReadAsync(buffer.AsMemory(got), Ct);
			Assert.True(n > 0, "link closed early");
			got += n;
		}

		return buffer;
	}

	[Theory]
	[InlineData(LoopbackChunking.Whole)]
	[InlineData(LoopbackChunking.SingleByte)]
	[InlineData(LoopbackChunking.Random)]
	public async Task Bytes_round_trip_in_both_directions(LoopbackChunking chunking)
	{
		var transport = new LoopbackTransport(new LoopbackOptions { Chunking = chunking, Seed = 7 });
		var link = transport.CreateLink();
		var data = Pattern(300);

		await link.Client.WriteAsync(data, Ct);
		Assert.Equal(data, await ReadExactAsync(link.Device, data.Length));

		await link.Device.WriteAsync(data, Ct);
		Assert.Equal(data, await ReadExactAsync(link.Client, data.Length));
	}

	[Fact]
	public async Task Random_chunking_is_reproducible_for_a_seed()
	{
		async Task<List<int>> SizesAsync()
		{
			var link = new LoopbackTransport(new LoopbackOptions { Chunking = LoopbackChunking.Random, Seed = 42 }).CreateLink();
			await link.Client.WriteAsync(Pattern(200), Ct);
			var sizes = new List<int>();
			var buffer = new byte[256];
			var total = 0;
			while (total < 200)
			{
				var n = await link.Device.ReadAsync(buffer, Ct);
				sizes.Add(n);
				total += n;
			}

			return sizes;
		}

		Assert.Equal(await SizesAsync(), await SizesAsync());
	}

	[Fact]
	public async Task Eof_makes_reads_return_zero_and_completes_Closed()
	{
		var link = new LoopbackTransport().CreateLink();
		link.InjectEof();

		Assert.Equal(0, await link.Client.ReadAsync(new byte[8], Ct));
		await link.Client.Closed.WaitAsync(TimeSpan.FromSeconds(5), Ct);
	}

	[Fact]
	public async Task Disposing_the_client_ends_the_device_side()
	{
		var link = new LoopbackTransport().CreateLink();
		await link.Client.DisposeAsync();

		Assert.Equal(0, await link.Device.ReadAsync(new byte[8], Ct));
		await link.Device.Closed.WaitAsync(TimeSpan.FromSeconds(5), Ct);
	}

	[Fact]
	public async Task DropNext_discards_exactly_the_requested_writes()
	{
		var link = new LoopbackTransport().CreateLink();
		link.DropNext(LoopbackDirection.ClientToDevice);

		await link.Client.WriteAsync(One, Ct);
		await link.Client.WriteAsync(Two, Ct);

		Assert.Equal(Two, await ReadExactAsync(link.Device, 1));
	}

	[Fact]
	public async Task Datagram_mode_keeps_message_boundaries()
	{
		var transport = new LoopbackTransport(new LoopbackOptions { Framing = LinkFraming.Datagram, Chunking = LoopbackChunking.SingleByte });
		var link = transport.CreateLink();
		var buffer = new byte[64];

		await link.Client.WriteAsync(Pattern(10), Ct);
		await link.Client.WriteAsync(Pattern(5), Ct);

		Assert.Equal(LinkFraming.Datagram, transport.Framing);
		Assert.Equal(10, await link.Device.ReadAsync(buffer, Ct));
		Assert.Equal(5, await link.Device.ReadAsync(buffer, Ct));
	}

	[Fact]
	public async Task Latency_delays_a_write_until_time_advances()
	{
		var time = new FakeTimeProvider();
		var link = new LoopbackTransport(new LoopbackOptions { Latency = TimeSpan.FromSeconds(2), Time = time }).CreateLink();

		var write = link.Client.WriteAsync(Three, Ct).AsTask();
		await Task.Delay(50, Ct);
		Assert.False(write.IsCompleted);

		time.Advance(TimeSpan.FromSeconds(2));
		await write.WaitAsync(TimeSpan.FromSeconds(5), Ct);
		Assert.Equal(Three, await ReadExactAsync(link.Device, 3));
	}

	[Fact]
	public async Task OnConnect_hook_receives_the_device_end()
	{
		var transport = new LoopbackTransport();
		var seen = new TaskCompletionSource<ILinkConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
		transport.OnConnect = connection =>
		{
			seen.SetResult(connection);
			return Task.CompletedTask;
		};

		var client = await transport.ConnectAsync(new DeviceIdentity(1, null, null, true, true), Ct);
		var device = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

		Assert.Equal("loopback", client.TransportId);
		Assert.Same(transport.LastLink!.Device, device);
	}
}
