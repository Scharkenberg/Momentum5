using System;
using Microsoft.UI.Dispatching;
using Momentum5.Client;
using Momentum5.Core.Settings;
using Momentum5.Protocol;

namespace Momentum5.App.Shell;

/// <summary>UI-thread marshalling used by the Advanced pages; adapts <see cref="Services.IUiDispatcher"/>.</summary>
public interface IUiDispatcher
{
	/// <summary>Queues <paramref name="action"/> on the UI thread. Returns false when the queue is gone.</summary>
	bool Post(Action action);
}

internal sealed class ShellUiDispatcher(Services.IUiDispatcher inner) : IUiDispatcher
{
	public bool Post(Action action)
	{
		try
		{
			inner.Post(() =>
			{
				try
				{
					action();
				}
				catch (Exception ex)
				{
					AppLog.StaticWrite($"ui dispatcher: {ex.Message}");
				}
			});
			return true;
		}
		catch
		{
			return false;
		}
	}
}

/// <summary>Minimal single-owner settings facade for pages that persist a settings change (maps to <see cref="Services.AppServices"/>).</summary>
public sealed class StartupController
{
	public System.Threading.Tasks.Task UpdateAsync(Func<AppSettings, AppSettings> change)
	{
		App.Services.UpdateSettings(change);
		return System.Threading.Tasks.Task.CompletedTask;
	}
}

/// <summary>Service locator for the Advanced pages (<c>App.Service&lt;T&gt;()</c>); everything resolves lazily from <see cref="App.Services"/>.</summary>
internal static class AdvancedServices
{
	private static readonly object Gate = new();
	private static AppLog? _log;
	private static IUiDispatcher? _dispatcher;
	private static StartupController? _startup;
	private static Momentum5.App.Pages.Advanced.ConsentDialogService? _dialog;
	private static ConsentService? _consent;
	private static UpdateCheck? _update;
	private static PackagePrep? _prep;
	private static UpgradeProtocolProfile? _profile;
	private static Momentum5.App.ViewModels.FirmwareConsentGate? _gate;

	public static T Get<T>() where T : class
	{
		lock (Gate)
		{
			var s = App.Services;
			object? o = typeof(T) switch
			{
				var t when t == typeof(AppLog) => _log ??= Create(),
				var t when t == typeof(IUiDispatcher) => _dispatcher ??= new ShellUiDispatcher(s.Ui),
				var t when t == typeof(StartupController) => _startup ??= new StartupController(),
				var t when t == typeof(IHeadsetState) => s.Store,
				var t when t == typeof(ISettingService) => s.Settings,
				var t when t == typeof(SettingService) => s.Settings,
				var t when t == typeof(ConnectionSupervisor) => s.Supervisor,
				var t when t == typeof(ConsentService) => _consent ??= new ConsentService(),
				var t when t == typeof(Momentum5.App.Pages.Advanced.ConsentDialogService) => _dialog ??= new Momentum5.App.Pages.Advanced.ConsentDialogService(Get<ConsentService>(), Get<AppLog>()),
				var t when t == typeof(UpdateCheck) => _update ??= new UpdateCheck(),
				var t when t == typeof(PackagePrep) => _prep ??= new PackagePrep(System.IO.Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Momentum5", "firmware")),
				var t when t == typeof(UpgradeProtocolProfile) => _profile ??= new UpgradeProtocolProfile(),
				var t when t == typeof(Momentum5.App.ViewModels.FirmwareConsentGate) => _gate ??= new Momentum5.App.ViewModels.FirmwareConsentGate(),
				var t when t == typeof(UpgradeSession) => NewSession(),
				_ => throw new InvalidOperationException($"No service registered for {typeof(T).Name}."),
			};
			return (T)o;
		}
	}

	private static AppLog Create()
	{
		var log = new AppLog();
		log.MakeCurrent();
		return log;
	}

	private static UpgradeSession NewSession()
	{
		var supervisor = App.Services.Supervisor;
		var store = App.Services.Store;
		var profile = Get<UpgradeProtocolProfile>();
		var consent = Get<ConsentService>();
		var gate = Get<Momentum5.App.ViewModels.FirmwareConsentGate>();
		var log = Get<AppLog>();
		return new UpgradeSession(
			profile,
			() => supervisor.Current?.Pipeline,
			word => gate.Issue(profile.UpgradeVendor, word, consent),
			() => System.Threading.Tasks.Task.FromResult(store.Current.TryGet(Keys.BatteryPercent, out var p) ? p : (int?)null),
			null,
			null,
			message => log.Write($"upgrade: {message}"));
	}
}
