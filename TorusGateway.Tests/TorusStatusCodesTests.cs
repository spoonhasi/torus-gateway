using TorusGateway.WebServer;
using Xunit;

namespace TorusGateway.Tests;

public class TorusStatusCodesTests
{
    [Fact]
    public void Returns_success_for_zero()
    {
        Assert.Equal("Success", TorusStatusCodes.GetDescription(0));
    }

    [Fact]
    public void Returns_mapped_description_for_known_code()
    {
        Assert.Equal("No machine object", TorusStatusCodes.GetDescription(0x21B61000));
    }

    [Fact]
    public void Returns_unknown_message_with_hex_for_unmapped_code()
    {
        Assert.Equal("Unknown status code: 0x12345678", TorusStatusCodes.GetDescription(0x12345678));
    }

    [Fact]
    public void Korean_entry_preserves_korean_text()
    {
        // 0x20206028 entry is in Korean ("MgrCommunication Address 또는 Filter 오류")
        string desc = TorusStatusCodes.GetDescription(0x20206028);
        Assert.Contains("또는", desc);
    }
}
