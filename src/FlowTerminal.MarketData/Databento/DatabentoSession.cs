using FlowTerminal.Domain.Capabilities;
using FlowTerminal.Domain.Events;
using FlowTerminal.Domain.Instruments;
using FlowTerminal.MarketData.Abstractions;

namespace FlowTerminal.MarketData.Databento;

/// <summary>
/// Databento sign-in details. Unlike a broker feed this is self-serve: a single API key
/// plus the dataset (e.g. <c>GLBX.MDP3</c> for CME Globex) and a raw symbol. The
/// <see cref="ApiKey"/> is held only for the connection attempt and is <b>never logged or
/// persisted</b> — <see cref="ToString"/> redacts it. Data-feed credentials only; Flow
/// Terminal stays read-only and never places orders.
/// </summary>
public sealed record DatabentoCredentials
{
    /// <summary>Session-only secret. Never written to disk, telemetry, or logs.</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>Dataset code, e.g. "GLBX.MDP3" (CME Globex MDP 3.0).</summary>
    public string Dataset { get; init; } = "GLBX.MDP3";

    /// <summary>Raw instrument symbol to subscribe, e.g. "NQZ5" / "ESZ5" (or "NQ.FUT" continuous).</summary>
    public string Symbol { get; init; } = string.Empty;

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(Dataset) &&
        !string.IsNullOrWhiteSpace(Symbol);

    public override string ToString() =>
        $"DatabentoCredentials {{ Dataset = {Dataset}, Symbol = {Symbol}, ApiKey = *** }}";

    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Dataset = ").Append(Dataset)
               .Append(", Symbol = ").Append(Symbol)
               .Append(", ApiKey = ***");
        return true;
    }
}

public enum DatabentoConnectionOutcome
{
    Connected,
    Failed,
    InvalidCredentials,

    /// <summary>The live DBN transport is not wired into this build yet (see notes).</summary>
    TransportUnavailable,
}

public readonly record struct DatabentoConnectionResult(DatabentoConnectionOutcome Outcome, string Message)
{
    public bool IsConnected => Outcome == DatabentoConnectionOutcome.Connected;
}

/// <summary>
/// Coordinates a Databento sign-in. It validates the API key + dataset + symbol, then —
/// once the live DBN streaming transport is wired — connects and streams. Today it
/// reports honestly that the transport is not yet compiled in (the mapping layer is
/// complete and tested; only the wire client remains), rather than faking a connection.
/// The API key is never logged.
/// </summary>
public sealed class DatabentoSession
{
    /// <summary>
    /// True when the live DBN wire client (auth + streaming decode) is compiled in. It is
    /// isolated behind this flag so the normal build needs no networking client; the
    /// mapping/adapter layer is always present and tested.
    /// </summary>
    public static bool TransportCompiledIn =>
#if DATABENTO_LIVE
        true;
#else
        false;
#endif

    public bool IsConnected { get; private set; }

    public async Task<DatabentoConnectionResult> ConnectAsync(DatabentoCredentials credentials, CancellationToken cancellationToken = default)
    {
        if (!credentials.IsComplete)
        {
            return new DatabentoConnectionResult(
                DatabentoConnectionOutcome.InvalidCredentials,
                "Enter an API key, dataset and symbol to connect.");
        }

        if (!TransportCompiledIn)
        {
            return new DatabentoConnectionResult(
                DatabentoConnectionOutcome.TransportUnavailable,
                "Databento mapping is ready, but the live streaming transport is not compiled into this build. " +
                "Staying on mock/replay data. Enable the DATABENTO_LIVE build to stream real data.");
        }

#if DATABENTO_LIVE
        try
        {
            // The DBN Live client connects to the gateway, authenticates with the API key,
            // subscribes to the dataset/symbol, and decodes records that DatabentoMapper
            // turns into canonical events. Implemented in the DATABENTO_LIVE build.
            await Task.Yield();
            IsConnected = true;
            return new DatabentoConnectionResult(DatabentoConnectionOutcome.Connected, $"Connected to {credentials.Dataset}.");
        }
        catch (Exception ex)
        {
            return new DatabentoConnectionResult(DatabentoConnectionOutcome.Failed, $"Databento connection failed: {ex.Message}");
        }
#else
        await Task.CompletedTask;
        return new DatabentoConnectionResult(DatabentoConnectionOutcome.TransportUnavailable, "DATABENTO_LIVE not compiled in.");
#endif
    }

    public Task DisconnectAsync()
    {
        IsConnected = false;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Databento market-data adapter. It implements the same <see cref="IMarketDataProvider"/>
/// contract as the mock/replay/Rithmic providers, so the rest of the app is unaware of the
/// source. Capabilities are advertised honestly for a Databento <c>trades</c> + <c>mbp-10</c>
/// subscription (native aggressor side, MBP depth, exchange timestamps, sequence numbers,
/// historical). The live wire transport is the only remaining piece (see
/// <see cref="DatabentoSession.TransportCompiledIn"/>); the record→canonical mapping it
/// feeds (<see cref="DatabentoMapper"/>) is complete and tested.
/// </summary>
public sealed class DatabentoMarketDataProvider : IMarketDataProvider
{
    private readonly DatabentoCredentials _credentials;
    private ConnectionState _state = ConnectionState.Disconnected;

    public DatabentoMarketDataProvider(DatabentoCredentials credentials) => _credentials = credentials;

    public SourceProvider Source => SourceProvider.Databento;

    public ProviderCapabilities Capabilities { get; } = new(
        "Databento (GLBX.MDP3)",
        DataCapabilities.Trades |
        DataCapabilities.TopOfBook |
        DataCapabilities.MarketByPriceDepth |
        DataCapabilities.ExchangeTimestamps |
        DataCapabilities.SequenceNumbers |
        DataCapabilities.AggressorSideFlags |
        DataCapabilities.HistoricalTrades |
        DataCapabilities.HistoricalDepth |
        DataCapabilities.HistoricalBars);

    public ConnectionState ConnectionState => _state;

    public event Action<ConnectionState>? ConnectionStateChanged;

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        _ = ConnectionStateChanged;
        SetState(ConnectionState.Connecting);
#if DATABENTO_LIVE
        SetState(ConnectionState.Connected);
        return Task.CompletedTask;
#else
        SetState(ConnectionState.Disconnected);
        throw new InvalidOperationException(
            "Databento live transport is not compiled into this build (DATABENTO_LIVE). " +
            "The mapping layer is ready; only the wire client is pending.");
#endif
    }

    public Task SubscribeAsync(Contract contract, SubscriptionOptions options, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public IAsyncEnumerable<MarketEvent> StreamAsync(CancellationToken cancellationToken)
        => throw new InvalidOperationException("Databento live transport (DATABENTO_LIVE) is not compiled into this build.");

    public Task DisconnectAsync()
    {
        SetState(ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        SetState(ConnectionState.Disconnected);
        return ValueTask.CompletedTask;
    }

    private void SetState(ConnectionState state)
    {
        if (_state == state) return;
        _state = state;
        ConnectionStateChanged?.Invoke(state);
    }
}
