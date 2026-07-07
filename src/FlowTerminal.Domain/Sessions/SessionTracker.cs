namespace FlowTerminal.Domain.Sessions;

/// <summary>Where an instant sits in the trading week: its CME trading date, whether it
/// is inside Regular Trading Hours, and whether this event crossed a session boundary.</summary>
public readonly record struct SessionState(DateOnly TradingDate, bool IsRth, bool RolledOver);

/// <summary>
/// Incremental session awareness for an event stream. Feeds every event's UTC timestamp
/// through the shared <see cref="TradingCalendar"/> (Globex convention: the trading day
/// rolls at 17:00 CT, weekends fold into Monday) and the RTH template (08:30–15:00 CT,
/// DST-aware), and reports the first event of each new trading date as a rollover so
/// session-scoped analytics (profiles, session CVD, TPO, opening range, session
/// percentiles, session VWAP anchors) can reset at the correct boundary. Deterministic:
/// depends only on the event timestamps, so live and replay roll identically.
/// </summary>
public sealed class SessionTracker
{
    private readonly TradingCalendar _calendar = new();
    private readonly SessionTemplate _rth = SessionTemplate.RegularTradingHours();
    private DateOnly _current;
    private bool _hasCurrent;

    /// <summary>The trading date of the last event seen, or null before any event.</summary>
    public DateOnly? CurrentTradingDate => _hasCurrent ? _current : null;

    /// <summary>Advances with the next event's UTC timestamp.</summary>
    public SessionState OnEvent(DateTime utc)
    {
        var date = _calendar.TradingDate(utc);
        bool rolled = _hasCurrent && date != _current;
        _current = date;
        _hasCurrent = true;
        return new SessionState(date, _rth.Contains(utc, date), rolled);
    }

    public void Reset() => _hasCurrent = false;
}
