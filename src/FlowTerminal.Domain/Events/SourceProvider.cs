namespace FlowTerminal.Domain.Events;

/// <summary>Identifies the origin of a canonical event for diagnostics and audit.</summary>
public enum SourceProvider : byte
{
    Unknown = 0,
    Mock = 1,
    SyntheticHistorical = 2,
    RecordedReplay = 3,
    Rithmic = 4,
    Databento = 5,
}
