using FlowTerminal.Domain.Sessions;
using Xunit;

namespace FlowTerminal.Domain.Tests;

/// <summary>Session awareness: Globex trading-date rollover at 17:00 CT, weekend folding,
/// RTH/ETH detection, and DST correctness (all boundaries are Chicago-local).</summary>
public class SessionTrackerTests
{
    [Fact]
    public void Rollover_Fires_Exactly_At_1700_Chicago()
    {
        var t = new SessionTracker();

        // 2024-06-03 is CDT (UTC-5): 16:59 CT = 21:59 UTC, 17:01 CT = 22:01 UTC.
        var before = t.OnEvent(new DateTime(2024, 6, 3, 21, 59, 0, DateTimeKind.Utc));
        Assert.Equal(new DateOnly(2024, 6, 3), before.TradingDate);
        Assert.False(before.RolledOver);

        var after = t.OnEvent(new DateTime(2024, 6, 3, 22, 1, 0, DateTimeKind.Utc));
        Assert.Equal(new DateOnly(2024, 6, 4), after.TradingDate); // next trading day
        Assert.True(after.RolledOver);                              // boundary detected once

        var later = t.OnEvent(new DateTime(2024, 6, 3, 23, 0, 0, DateTimeKind.Utc));
        Assert.False(later.RolledOver);                             // not re-fired
    }

    [Fact]
    public void Rollover_Respects_Winter_Time()
    {
        var t = new SessionTracker();
        // 2024-01-15 is CST (UTC-6): 17:00 CT = 23:00 UTC.
        Assert.Equal(new DateOnly(2024, 1, 15),
            t.OnEvent(new DateTime(2024, 1, 15, 22, 59, 0, DateTimeKind.Utc)).TradingDate);
        var rolled = t.OnEvent(new DateTime(2024, 1, 15, 23, 1, 0, DateTimeKind.Utc));
        Assert.Equal(new DateOnly(2024, 1, 16), rolled.TradingDate);
        Assert.True(rolled.RolledOver);
    }

    [Fact]
    public void Friday_Evening_Folds_Into_Monday()
    {
        var t = new SessionTracker();
        // Friday 2024-06-07 17:01 CT (22:01 UTC) → Saturday calendar → Monday trading date.
        var s = t.OnEvent(new DateTime(2024, 6, 7, 22, 1, 0, DateTimeKind.Utc));
        Assert.Equal(new DateOnly(2024, 6, 10), s.TradingDate);
    }

    [Fact]
    public void Rth_And_Eth_Are_Detected()
    {
        var t = new SessionTracker();
        // 09:00 CT (14:00 UTC, June) → RTH.
        Assert.True(t.OnEvent(new DateTime(2024, 6, 3, 14, 0, 0, DateTimeKind.Utc)).IsRth);
        // 07:00 CT (12:00 UTC) → overnight ETH.
        Assert.False(t.OnEvent(new DateTime(2024, 6, 3, 12, 0, 0, DateTimeKind.Utc)).IsRth);
        // 15:30 CT (20:30 UTC) → post-RTH close-out, not RTH.
        Assert.False(t.OnEvent(new DateTime(2024, 6, 3, 20, 30, 0, DateTimeKind.Utc)).IsRth);
    }

    [Fact]
    public void First_Event_Never_Reports_A_Rollover_And_Reset_Clears_State()
    {
        var t = new SessionTracker();
        Assert.False(t.OnEvent(new DateTime(2024, 6, 3, 14, 0, 0, DateTimeKind.Utc)).RolledOver);

        t.Reset();
        // After a reset (replay seek / contract change) the next event is a fresh start,
        // even if it lands on a different trading date — no phantom rollover reset.
        Assert.False(t.OnEvent(new DateTime(2024, 6, 5, 14, 0, 0, DateTimeKind.Utc)).RolledOver);
    }
}
