using Momentum5.Client;
using Momentum5.Core.Transport;
using Momentum5.Protocol;

namespace Momentum5.Cli;

internal static class Runner
{
	/// <summary>Maps failures to exit codes (see <see cref="CliException"/>).</summary>
	public static async Task<int> RunAsync(Func<Task<int>> body)
	{
		try
		{
			return await body().ConfigureAwait(false);
		}
		catch (CliException ex)
		{
			Console.Error.WriteLine(ex.Message);
			return ex.ExitCode;
		}
		catch (SafetyDeniedException ex)
		{
			Console.Error.WriteLine($"Refused: {ex.Message}");
			return 3;
		}
		catch (ArgumentException ex)
		{
			Console.Error.WriteLine(ex.Message);
			return 2;
		}
		catch (LinkException ex)
		{
			Console.Error.WriteLine($"Link error {ex.Error}: {ex.Message}");
			return 1;
		}
		catch (GaiaProtocolException ex)
		{
			Console.Error.WriteLine(ex.Message);
			return 1;
		}
		catch (Exception ex) when (ex is TimeoutException or GaiaDecodeException or InvalidOperationException)
		{
			Console.Error.WriteLine(ex.Message);
			return 1;
		}
		catch (OperationCanceledException)
		{
			Console.Error.WriteLine("Cancelled.");
			return 1;
		}
	}
}
