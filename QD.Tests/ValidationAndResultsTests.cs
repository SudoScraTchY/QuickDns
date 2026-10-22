using QD.Core.Helper;

namespace QD.Tests;

public class OperationResultTests
{
    [Fact]
    public void NewResult_IsFailedWithEmptyMessage()
    {
        var result = new OperationResult();

        Assert.False(result.IsSucceeded);
        Assert.Equal(string.Empty, result.Message);
    }

    [Fact]
    public void Succeeded_SetsFlagAndMessage()
    {
        var result = new OperationResult().Succeeded("all good");

        Assert.True(result.IsSucceeded);
        Assert.Equal("all good", result.Message);
    }

    [Fact]
    public void Failed_SetsFlagFalseAndMessage()
    {
        var result = new OperationResult().Succeeded().Failed("boom");

        Assert.False(result.IsSucceeded);
        Assert.Equal("boom", result.Message);
    }
}

public class DnsValidatorTests
{
    [Theory]
    [InlineData("1.1.1.1", true)]
    [InlineData(" 8.8.8.8 ", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("::1", true)]
    public void ValidAddresses_Pass(string value, bool expected)
    {
        Assert.Equal(expected, DnsValidator.IsValidIpAddress(value));
    }

    [Theory]
    [InlineData("1")]              // IPAddress.TryParse would accept this as 0.0.0.1
    [InlineData("1234")]
    [InlineData("300.1.1.1")]
    [InlineData("1.1.1.1.5")]
    [InlineData("example.com")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void InvalidAddresses_Fail(string? value)
    {
        Assert.False(DnsValidator.IsValidIpAddress(value));
    }
}
