using FlowTerminal.Domain.Events;
using FlowTerminal.Domain.Instruments;
using FlowTerminal.MarketData.Abstractions;
using Parquet.Serialization;

namespace FlowTerminal.Storage.Parquet;

/// <summary>
/// Records the canonical event stream to ordered Parquet part files. Events are
/// buffered and written in batches — a session file is never rewritten per event.
/// Each flush writes the buffered batch to a temporary file which is atomically
/// renamed into place, so a crash can leave at most one incomplete temp file and
/// never corrupts a committed part.
/// </summary>
public sealed class ParquetMarketDataRecorder : IMarketDataRecorder
{
    private readonly RecordingLayout _layout;
    private readonly RootSymbol _root;
    private readonly string _contractSymbol;
    private readonly DateOnly _tradingDate;
    private readonly int _batchSize;
    private readonly List<MarketEventRecord> _buffer;
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private int _partIndex;
    private bool _recording = true;

    public ParquetMarketDataRecorder(
        RecordingLayout layout, RootSymbol root, string contractSymbol, DateOnly tradingDate, int batchSize = 10_000)
    {
        _layout = layout;
        _root = root;
        _contractSymbol = contractSymbol;
        _tradingDate = tradingDate;
        _batchSize = batchSize < 1 ? 1 : batchSize;
        _buffer = new List<MarketEventRecord>(_batchSize);
        Directory.CreateDirectory(layout.SessionDirectory(root, contractSymbol, tradingDate));
    }

    public bool IsRecording => _recording;

    public long RecordedCount { get; private set; }

    // Manifest accumulators (order-sensitive hash + time range), folded per event.
    private ulong _hash = RecordingManifest.HashSeed;
    private DateTime _firstUtc;
    private DateTime _lastUtc;
    private SourceProvider _source = SourceProvider.Unknown;
    private decimal _tickSize;

    /// <summary>Tick size stamped into the manifest (so replay cannot misinterpret prices).</summary>
    public void SetTickSize(decimal tickSize) => _tickSize = tickSize;

    public void Record(in MarketEvent marketEvent)
    {
        if (!_recording)
        {
            return;
        }

        List<MarketEventRecord>? toFlush = null;
        lock (_buffer)
        {
            _buffer.Add(MarketEventRecord.From(marketEvent));
            _hash = RecordingManifest.FoldEvent(_hash, marketEvent);
            if (RecordedCount == 0) _firstUtc = marketEvent.ExchangeTimestampUtc;
            _lastUtc = marketEvent.ExchangeTimestampUtc;
            _source = marketEvent.Source;
            RecordedCount++;
            if (_buffer.Count >= _batchSize)
            {
                toFlush = new List<MarketEventRecord>(_buffer);
                _buffer.Clear();
            }
        }

        if (toFlush is not null)
        {
            // Fire-and-forget within the recorder; FlushAsync/Dispose guarantee completion.
            WritePartAsync(toFlush, CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        List<MarketEventRecord> toFlush;
        lock (_buffer)
        {
            if (_buffer.Count == 0)
            {
                return;
            }

            toFlush = new List<MarketEventRecord>(_buffer);
            _buffer.Clear();
        }

        await WritePartAsync(toFlush, cancellationToken).ConfigureAwait(false);
    }

    private async Task WritePartAsync(List<MarketEventRecord> records, CancellationToken ct)
    {
        await _flushLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int index = _partIndex++;
            string finalPath = _layout.PartFile(_root, _contractSymbol, _tradingDate, index);
            string tempPath = finalPath + ".tmp";

            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await ParquetSerializer.SerializeAsync(records, stream, cancellationToken: ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            // Atomic publish: a reader only ever sees a complete part.
            File.Move(tempPath, finalPath, overwrite: true);
        }
        finally
        {
            _flushLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _recording = false;
        await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        WriteManifest();
        _flushLock.Dispose();
    }

    /// <summary>Writes the session manifest so the recording is self-describing and verifiable.</summary>
    private void WriteManifest()
    {
        if (RecordedCount == 0) return;
        var manifest = new RecordingManifest(
            RecordingManifest.CurrentSchemaVersion,
            typeof(ParquetMarketDataRecorder).Assembly.GetName().Version?.ToString() ?? "unknown",
            _root.ToString(), _contractSymbol, _tickSize, _tradingDate.ToString("yyyy-MM-dd"),
            _source.ToString(), RecordedCount, _firstUtc, _lastUtc, _hash);
        manifest.Save(_layout.SessionDirectory(_root, _contractSymbol, _tradingDate));
    }
}
