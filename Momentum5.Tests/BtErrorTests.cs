using Momentum5.Core.Transport;

namespace Momentum5.Tests;

public sealed class BtErrorTests
{
	[Theory]
	[InlineData(0x80070490u, BtError.ElementNotFound)]
	[InlineData(0x80070005u, BtError.AccessDenied)]
	[InlineData(0x8007048Fu, BtError.DeviceNotConnected)]
	[InlineData(0x8007274Cu, BtError.Timeout)]
	[InlineData(0x8007274Du, BtError.ConnectionRefused)]
	[InlineData(0x80072745u, BtError.ConnectionAborted)]
	[InlineData(0x80072746u, BtError.ConnectionReset)]
	[InlineData(0x8007277Cu, BtError.ServiceNotFound)]
	[InlineData(0x800710DFu, BtError.DeviceUnavailable)]
	[InlineData(0x80004005u, BtError.Unknown)]
	public void MapsHResult(uint hresult, BtError expected) =>
		Assert.Equal(expected, BtErrors.FromHResult(unchecked((int)hresult)));

	[Fact]
	public void EveryErrorHasUserText()
	{
		foreach (var e in Enum.GetValues<BtError>())
		{
			Assert.False(string.IsNullOrWhiteSpace(BtErrors.UserText(e)));
		}
	}

	[Fact]
	public void LinkExceptionCarriesHResult()
	{
		var ex = new LinkException(BtError.Timeout, unchecked((int)0x8007274C), "t");
		Assert.Equal(BtError.Timeout, ex.Error);
		Assert.Equal(unchecked((int)0x8007274C), ex.HResult);
	}
}
