using TorusGateway.WebServer;
using Xunit;

namespace TorusGateway.Tests;

public class NormalizeValueKeyTests
{
    [Fact]
    public void Lowercase_value_is_unchanged()
    {
        string result = TorusController.NormalizeValueKey("{\"value\":30}");
        Assert.Equal("{\"value\":30}", result);
    }

    [Fact]
    public void Capitalized_Value_is_normalized()
    {
        string result = TorusController.NormalizeValueKey("{\"Value\":30}");
        Assert.Equal("{\"value\":30}", result);
    }

    [Fact]
    public void Uppercase_VALUE_is_normalized()
    {
        string result = TorusController.NormalizeValueKey("{\"VALUE\":30}");
        Assert.Equal("{\"value\":30}", result);
    }

    [Fact]
    public void Preserves_other_keys_and_order()
    {
        string result = TorusController.NormalizeValueKey("{\"a\":1,\"Value\":30,\"b\":2}");
        Assert.Equal("{\"a\":1,\"value\":30,\"b\":2}", result);
    }

    [Fact]
    public void Exact_value_wins_over_variant_when_both_present()
    {
        // 충돌 시 정식 키 보존, 변형 키는 건드리지 않음
        string result = TorusController.NormalizeValueKey("{\"Value\":1,\"value\":2}");
        Assert.Equal("{\"Value\":1,\"value\":2}", result);
    }

    [Fact]
    public void Missing_value_key_returns_input_unchanged()
    {
        string result = TorusController.NormalizeValueKey("{\"other\":30}");
        Assert.Equal("{\"other\":30}", result);
    }

    [Fact]
    public void Array_value_variant_is_normalized()
    {
        string result = TorusController.NormalizeValueKey("{\"VALUE\":[1,2,3]}");
        Assert.Equal("{\"value\":[1,2,3]}", result);
    }

    [Fact]
    public void Non_object_root_returns_input_unchanged()
    {
        string result = TorusController.NormalizeValueKey("[1,2,3]");
        Assert.Equal("[1,2,3]", result);
    }
}
