using TorusGateway.WebServer;
using Xunit;

namespace TorusGateway.Tests;

public class WrapValueToArrayTests
{
    [Fact]
    public void Wraps_scalar_value_in_array()
    {
        string result = TorusController.WrapValueToArray("{\"value\":42}");
        Assert.Equal("{\"value\":[42]}", result);
    }

    [Fact]
    public void Wraps_string_value_in_array()
    {
        string result = TorusController.WrapValueToArray("{\"value\":\"hello\"}");
        Assert.Equal("{\"value\":[\"hello\"]}", result);
    }

    [Fact]
    public void Leaves_array_value_unchanged()
    {
        string input = "{\"value\":[1,2,3]}";
        string result = TorusController.WrapValueToArray(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void Returns_input_when_no_value_key()
    {
        string input = "{\"other\":1}";
        string result = TorusController.WrapValueToArray(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void Returns_input_when_not_object()
    {
        string input = "[1,2,3]";
        string result = TorusController.WrapValueToArray(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void Only_wraps_value_key_not_others()
    {
        string result = TorusController.WrapValueToArray("{\"value\":42,\"other\":7}");
        Assert.Contains("\"value\":[42]", result);
        Assert.Contains("\"other\":7", result);
    }
}
