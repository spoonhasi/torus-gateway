using TorusGateway.WebServer;
using Xunit;

namespace TorusGateway.Tests;

// CustomController.IsPlcValueNumeric: PLC 쓰기 body의 "value"가 number(정수/실수)인지 검사.
// 문자열 "0" 등이 그대로 기록돼 오작동한 사례를 막기 위한 가드.
public class PlcValueNumericTests
{
    [Theory]
    [InlineData("{\"value\":1}")]
    [InlineData("{\"value\":0}")]
    [InlineData("{\"value\":-3}")]
    [InlineData("{\"value\":1.5}")]
    [InlineData("{\"value\":[1]}")]      // 이미 배열로 온 number
    [InlineData("{\"value\":[1,2]}")]    // number 배열
    public void Accepts_number_or_number_array(string json)
    {
        Assert.True(CustomController.IsPlcValueNumeric(json));
    }

    [Theory]
    [InlineData("{\"value\":\"0\"}")]    // 보고된 버그: 문자열 "0"
    [InlineData("{\"value\":\"1.5\"}")]
    [InlineData("{\"value\":true}")]
    [InlineData("{\"value\":null}")]
    [InlineData("{\"value\":{}}")]
    [InlineData("{\"value\":[\"0\"]}")]  // 문자열 원소 배열
    [InlineData("{\"value\":[1,\"0\"]}")] // 일부만 문자열
    [InlineData("{\"value\":[]}")]        // 빈 배열
    [InlineData("{\"foo\":1}")]           // value 없음
    public void Rejects_non_number(string json)
    {
        Assert.False(CustomController.IsPlcValueNumeric(json));
    }

    // 루트가 객체가 아니면 거부.
    [Theory]
    [InlineData("\"0\"")]
    [InlineData("0")]
    [InlineData("[1,2]")]
    public void Rejects_non_object_root(string json)
    {
        Assert.False(CustomController.IsPlcValueNumeric(json));
    }
}
