using System.Text.Json;
using Xunit;
// TorusGateway.Torus(네임스페이스)와 Torus(클래스)가 이름이 겹치므로, 충돌 없는 별칭으로 클래스를 가리킨다.
using TorusWrapper = global::TorusGateway.Torus.Torus;

namespace TorusGateway.Tests;

// Torus.InjectStatus: getMachinesInfo 응답에 status 필드를 안전하게 주입하는 헬퍼.
public class InjectStatusTests
{
    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Object_root_adds_status_and_keeps_value()
    {
        string result = TorusWrapper.InjectStatus("{\"value\":[{\"id\":1}]}", 0);
        var root = Root(result);
        Assert.Equal(0, root.GetProperty("status").GetInt32());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("value").ValueKind);
        Assert.Equal(1, root.GetProperty("value")[0].GetProperty("id").GetInt32());
    }

    [Fact]
    public void Object_root_with_existing_status_is_overwritten()
    {
        string result = TorusWrapper.InjectStatus("{\"status\":999,\"value\":1}", 0);
        var root = Root(result);
        Assert.Equal(0, root.GetProperty("status").GetInt32());
    }

    [Fact]
    public void Array_root_is_wrapped_in_value()
    {
        // 과거 문자열 삽입 방식이 깨지던 케이스: 루트가 배열.
        string result = TorusWrapper.InjectStatus("[{\"id\":1},{\"id\":2}]", 0);
        var root = Root(result);
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(0, root.GetProperty("status").GetInt32());
        var value = root.GetProperty("value");
        Assert.Equal(JsonValueKind.Array, value.ValueKind);
        Assert.Equal(2, value.GetArrayLength());
    }

    [Fact]
    public void Scalar_root_is_wrapped_in_value()
    {
        string result = TorusWrapper.InjectStatus("42", 0);
        var root = Root(result);
        Assert.Equal(0, root.GetProperty("status").GetInt32());
        Assert.Equal(42, root.GetProperty("value").GetInt32());
    }

    [Fact]
    public void Non_zero_status_is_propagated()
    {
        string result = TorusWrapper.InjectStatus("{\"value\":[]}", 0x21B60000);
        var root = Root(result);
        Assert.Equal(0x21B60000, root.GetProperty("status").GetInt32());
    }

    [Fact]
    public void Non_ascii_is_not_escaped()
    {
        string result = TorusWrapper.InjectStatus("{\"value\":[{\"name\":\"메인제어기\"}]}", 0);
        Assert.Contains("메인제어기", result); // \uXXXX로 이스케이프되지 않아야 함
    }

    [Fact]
    public void Invalid_json_falls_back_to_status_only()
    {
        string result = TorusWrapper.InjectStatus("not json", -1);
        var root = Root(result);
        Assert.Equal(-1, root.GetProperty("status").GetInt32());
        Assert.False(root.TryGetProperty("value", out _));
    }
}
