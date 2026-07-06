using FlowTerminal.MarketData.Databento;
using Xunit;

namespace FlowTerminal.MarketData.Tests;

public class DatabentoSessionTests
{
    private static DatabentoCredentials Complete() => new()
    {
        ApiKey = "db-live-SECRETKEY123",
        Dataset = "GLBX.MDP3",
        Symbol = "NQZ5",
    };

    [Fact]
    public void IsComplete_Requires_Key_Dataset_And_Symbol()
    {
        Assert.True(Complete().IsComplete);
        Assert.False((Complete() with { ApiKey = "" }).IsComplete);
        Assert.False((Complete() with { Symbol = "" }).IsComplete);
    }

    [Fact]
    public void ToString_Never_Leaks_The_Api_Key()
    {
        var c = Complete();
        Assert.DoesNotContain("SECRETKEY123", c.ToString());
        Assert.Contains("***", c.ToString());
        Assert.DoesNotContain("SECRETKEY123", $"{c}");
        Assert.DoesNotContain("SECRETKEY123", $"connect {c}");
    }

    [Fact]
    public async Task Connect_Reports_TransportUnavailable_In_Normal_Build()
    {
        var result = await new DatabentoSession().ConnectAsync(Complete());
        if (DatabentoSession.TransportCompiledIn)
        {
            Assert.NotEqual(DatabentoConnectionOutcome.InvalidCredentials, result.Outcome);
        }
        else
        {
            Assert.Equal(DatabentoConnectionOutcome.TransportUnavailable, result.Outcome);
            Assert.False(result.IsConnected);
            Assert.DoesNotContain("SECRETKEY123", result.Message);
        }
    }

    [Fact]
    public async Task Connect_Rejects_Incomplete_Credentials()
    {
        var result = await new DatabentoSession().ConnectAsync(Complete() with { ApiKey = "" });
        Assert.Equal(DatabentoConnectionOutcome.InvalidCredentials, result.Outcome);
    }

    [Fact]
    public void Provider_Advertises_Honest_Capabilities()
    {
        var p = new DatabentoMarketDataProvider(Complete());
        Assert.Equal(FlowTerminal.Domain.Events.SourceProvider.Databento, p.Source);
        Assert.True(p.Capabilities.Has(FlowTerminal.Domain.Capabilities.DataCapabilities.MarketByPriceDepth));
        Assert.True(p.Capabilities.Has(FlowTerminal.Domain.Capabilities.DataCapabilities.AggressorSideFlags));
        Assert.False(p.Capabilities.AggressorIsEstimated); // native aggressor side
        Assert.False(p.Capabilities.Has(FlowTerminal.Domain.Capabilities.DataCapabilities.MarketByOrderDepth)); // no MBO claim
    }
}
