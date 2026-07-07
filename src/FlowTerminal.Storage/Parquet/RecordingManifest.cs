using System.Text.Json;
using FlowTerminal.Domain.Events;

namespace FlowTerminal.Storage.Parquet;

/// <summary>
/// Session-level metadata written beside a recording's part files (<c>manifest.json</c>).
/// It makes a recording self-describing and verifiable: schema/app versions for
/// migration, instrument identity and tick size so replay cannot misinterpret prices,
/// the event count and time range for completeness checks, and an order-sensitive
/// FNV-1a hash over every event's identifying fields so silent corruption or partial
/// loss is detectable rather than silently replayed.
/// </summary>
public sealed record RecordingManifest(
    int SchemaVersion,
    string AppVersion,
    string Root,
    string ContractSymbol,
    decimal TickSize,
    string TradingDate,
    string SourceMode,
    long EventCount,
    DateTime FirstEventUtc,
    DateTime LastEventUtc,
    ulong EventHash)
{
    public const int CurrentSchemaVersion = 1;
    public const string FileName = "manifest.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Folds one event into the running order-sensitive hash (FNV-1a).</summary>
    public static ulong FoldEvent(ulong hash, in MarketEvent e)
    {
        unchecked
        {
            void Mix(long v) { for (int b = 0; b < 8; b++) { hash ^= (byte)(v >> (b * 8)); hash *= 1099511628211UL; } }
            Mix(e.ExchangeSequence);
            Mix((byte)e.Type);
            Mix(e.PriceTicks);
            Mix(e.Quantity);
            Mix(e.ExchangeTimestampUtc.Ticks);
            return hash;
        }
    }

    public const ulong HashSeed = 14695981039346656037UL;

    /// <summary>Atomically writes the manifest into the session directory.</summary>
    public void Save(string sessionDirectory)
    {
        Directory.CreateDirectory(sessionDirectory);
        string path = Path.Combine(sessionDirectory, FileName);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Loads a manifest, or null when absent/corrupt (a recording without one
    /// is still readable — it just cannot be integrity-verified).</summary>
    public static RecordingManifest? Load(string sessionDirectory)
    {
        try
        {
            string path = Path.Combine(sessionDirectory, FileName);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<RecordingManifest>(File.ReadAllText(path))
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Verifies a replayed event stream against this manifest (count + order-sensitive
    /// hash). Returns null when valid, otherwise a human-readable reason.
    /// </summary>
    public string? Validate(IEnumerable<MarketEvent> events)
    {
        long count = 0;
        ulong hash = HashSeed;
        foreach (var e in events)
        {
            hash = FoldEvent(hash, e);
            count++;
        }

        if (count != EventCount)
            return $"event count mismatch: manifest {EventCount:N0}, stream {count:N0}";
        if (hash != EventHash)
            return "event hash mismatch: the stream differs from what was recorded";
        return null;
    }
}
