using TorusGateway.WebServer;
using Xunit;

namespace TorusGateway.Tests;

public class FilterStatusOnlyTests
{
    [Fact]
    public void Strips_value_and_other_keys()
    {
        string result = TorusController.FilterStatusOnly("{\"status\":0,\"value\":[1],\"datatype\":\"int\"}");
        Assert.Equal("{\"status\":0}", result);
    }

    [Fact]
    public void Adds_error_key_when_status_nonzero()
    {
        // 0x21B00000 = 565182464 = "Internal exception"
        string result = TorusController.FilterStatusOnly("{\"status\":565182464}");
        Assert.Contains("\"status\":565182464", result);
        Assert.Contains("\"error\":\"Internal exception\"", result);
    }

    [Fact]
    public void Returns_input_when_not_object()
    {
        string input = "42";
        string result = TorusController.FilterStatusOnly(input);
        Assert.Equal(input, result);
    }
}
