using System;
using System.Collections.Generic;
using TorusGateway.WebServer;
using Xunit;

namespace TorusGateway.Tests;

// MachineController.BuildAddressAndFilter: URL 경로/쿼리 → TORUS 주소/필터 매핑(논문 Fig. 5)과
// 구분자 주입 거부를 검증한다.
public class BuildAddressAndFilterTests
{
    private static IEnumerable<KeyValuePair<string, string>> Q(params (string k, string v)[] pairs)
    {
        foreach (var (k, v) in pairs)
            yield return new KeyValuePair<string, string>(k, v);
    }

    // 논문 Fig. 5의 예시 그대로.
    [Fact]
    public void Maps_paper_example()
    {
        var (address, filter) = MachineController.BuildAddressAndFilter(
            "channel/axis/machinePosition", Q(("machine", "1"), ("channel", "1"), ("axis", "1")));

        Assert.Equal("data://machine/channel/axis/machinePosition", address);
        Assert.Equal("machine=1&channel=1&axis=1", filter);
    }

    // 키·값은 가공하지 않는다(대소문자, 범위 표기 등 그대로).
    [Fact]
    public void Passes_keys_and_values_as_is()
    {
        var (_, filter) = MachineController.BuildAddressAndFilter(
            "channel/axis/machinePosition", Q(("Machine", "1"), ("axis", "1-3")));

        Assert.Equal("Machine=1&axis=1-3", filter);
    }

    // 빈 세그먼트(중복 '/')는 무시하고, 쿼리가 없으면 필터는 빈 문자열.
    [Fact]
    public void Ignores_empty_segments_and_allows_no_query()
    {
        var (address, filter) = MachineController.BuildAddressAndFilter("/channel//variable/userVariable/", Q());

        Assert.Equal("data://machine/channel/variable/userVariable", address);
        Assert.Equal("", filter);
    }

    // 경로가 없으면(/machine) 루트 주소.
    [Fact]
    public void Null_segments_map_to_root()
    {
        var (address, _) = MachineController.BuildAddressAndFilter(null, Q());
        Assert.Equal("data://machine/", address);
    }

    // 디코딩된 값에 '&'·'='가 있으면 필터에 키를 끼워 넣을 수 있으므로 거부.
    [Theory]
    [InlineData("machine", "1&machine=2")]
    [InlineData("machine", "1=2")]
    [InlineData("ma&chine", "1")]
    [InlineData("ma=chine", "1")]
    public void Rejects_delimiters_in_query(string key, string value)
    {
        Assert.Throws<ArgumentException>(() =>
            MachineController.BuildAddressAndFilter("channel/axis/machinePosition", Q((key, value))));
    }

    // 경로 세그먼트에 '?'·'&'·'='가 있으면 주소에 필터를 섞을 수 있으므로 거부.
    [Theory]
    [InlineData("channel/axis?machine=2")]
    [InlineData("channel/axis&x")]
    [InlineData("channel/a=b")]
    public void Rejects_delimiters_in_path(string segments)
    {
        Assert.Throws<ArgumentException>(() => MachineController.BuildAddressAndFilter(segments, Q()));
    }
}
