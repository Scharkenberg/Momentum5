namespace Momentum5.Core.Domain;

public enum ChargingState
{
	None = 0,
	Charging = 1,
	Full = 2,
}

/// <summary>Auto power-off; the value is the timeout in seconds.</summary>
public enum AutoOffTimer
{
	Never = 0,
	Minutes15 = 900,
	Minutes30 = 1800,
	Minutes60 = 3600,
}

public enum SoundMode
{
	Off = 0,
	GraphicEq = 1,
	SpeechClarity = 2,

	/// <summary>Read-only: set through Sound Personalization only.</summary>
	Personalization = 3,
}

public enum PromptMode
{
	Off = 0,
	TonesOnly = 1,
	Voice = 2,
}

public enum BtCompatibility
{
	BetterAudio = 0,
	Compatibility = 1,
}
