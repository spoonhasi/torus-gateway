using System;
using System.Collections.Generic;
using TorusGateway.WebServer;
using Xunit;

namespace TorusGateway.Tests;

// CustomController.BuildPlcSignalFilter: plcAddress/plcType/plcValue 쿼리를
// TORUS plcsignal 필터(machine=..&type=..&start=..&end=..)로 변환하는 순수 헬퍼 검증.
public class PlcSignalFilterTests
{
    // 쿼리 순서를 보존해 기대 문자열과 그대로 비교하기 위한 헬퍼.
    private static IEnumerable<KeyValuePair<string, string>> Q(params (string k, string v)[] pairs)
    {
        foreach (var (k, v) in pairs)
            yield return new KeyValuePair<string, string>(k, v);
    }

    // 단일 주소: start=end=주소.
    [Fact]
    public void Single_address_sets_start_equal_end()
    {
        var filter = CustomController.BuildPlcSignalFilter(
            Q(("machine", "2"), ("plcAddress", "D0015"), ("plcType", "0")));
        Assert.Equal("machine=2&type=0&start=D0015&end=D0015", filter);
    }

    // 범위 주소 'A~B': start=A, end=B.
    [Fact]
    public void Range_address_splits_into_start_and_end()
    {
        var filter = CustomController.BuildPlcSignalFilter(
            Q(("machine", "2"), ("plcAddress", "D0015~D0016"), ("plcType", "0")));
        Assert.Equal("machine=2&type=0&start=D0015&end=D0016", filter);
    }

    // plcType → type 로 키가 치환된다(값 매핑 표에 없는 값은 그대로).
    [Fact]
    public void Translates_plctype_key_to_type()
    {
        var filter = CustomController.BuildPlcSignalFilter(
            Q(("plcType", "1"), ("plcAddress", "M100")));
        Assert.Equal("type=1&start=M100&end=M100", filter);
    }

    // plcType 값 매핑: 2→0, 3→1, 4→2, 5→4, 6→5. 표에 없는 값(0/1/7 등)은 그대로 통과.
    [Theory]
    [InlineData("0", "0")]
    [InlineData("1", "1")]
    [InlineData("2", "0")]
    [InlineData("3", "1")]
    [InlineData("4", "2")]
    [InlineData("5", "4")]
    [InlineData("6", "5")]
    [InlineData("7", "7")]
    public void Maps_plctype_value(string input, string expected)
    {
        var filter = CustomController.BuildPlcSignalFilter(
            Q(("plcType", input), ("plcAddress", "D0015")));
        Assert.Equal($"type={expected}&start=D0015&end=D0015", filter);
    }

    // 쿼리 키는 대소문자 무관.
    [Fact]
    public void Keys_are_case_insensitive()
    {
        var filter = CustomController.BuildPlcSignalFilter(
            Q(("MACHINE", "2"), ("PLCADDRESS", "D0015~D0016"), ("PLCTYPE", "0")));
        Assert.Equal("machine=2&type=0&start=D0015&end=D0016", filter);
    }

    // '~' 주변 공백은 제거한다.
    [Fact]
    public void Trims_whitespace_around_range_separator()
    {
        var filter = CustomController.BuildPlcSignalFilter(
            Q(("machine", "2"), ("plcAddress", " D0015 ~ D0016 "), ("plcType", "0")));
        Assert.Equal("machine=2&type=0&start=D0015&end=D0016", filter);
    }

    // 알 수 없는 키는 소문자로 그대로 통과.
    [Fact]
    public void Passes_through_unknown_keys_lowercased()
    {
        var filter = CustomController.BuildPlcSignalFilter(
            Q(("Foo", "bar"), ("plcAddress", "D0015")));
        Assert.Equal("foo=bar&start=D0015&end=D0015", filter);
    }

    // plcAddress가 없으면 예외.
    [Fact]
    public void Missing_address_throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            CustomController.BuildPlcSignalFilter(Q(("machine", "2"), ("plcType", "0"))));
        Assert.Contains("plcAddress", ex.Message);
    }

    // '~'가 둘 이상이면(구간 3개) 예외.
    [Fact]
    public void Address_with_more_than_one_separator_throws()
    {
        Assert.Throws<ArgumentException>(() =>
            CustomController.BuildPlcSignalFilter(
                Q(("plcAddress", "D0015~D0016~D0017"))));
    }

    // 범위 한쪽이 비어 있으면 예외.
    [Fact]
    public void Range_with_empty_side_throws()
    {
        Assert.Throws<ArgumentException>(() =>
            CustomController.BuildPlcSignalFilter(Q(("plcAddress", "D0015~"))));
    }

    // 빈 plcAddress면 예외.
    [Fact]
    public void Empty_address_throws()
    {
        Assert.Throws<ArgumentException>(() =>
            CustomController.BuildPlcSignalFilter(Q(("plcAddress", ""))));
    }
}
