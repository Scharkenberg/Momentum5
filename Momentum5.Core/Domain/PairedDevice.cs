namespace Momentum5.Core.Domain;

public sealed record PairedDevice(int Index, int Priority, bool Connected, string Name);
