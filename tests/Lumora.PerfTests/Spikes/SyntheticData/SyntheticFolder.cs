namespace Lumora.PerfTests.Spikes.SyntheticData;

internal sealed record SyntheticFolder(
    int Id,
    int? ParentId,
    string Path,
    string Name);
