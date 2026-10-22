using System.IO;
using TorusGateway.OpcUa;
using Xunit;

namespace TorusGateway.Tests;

// OPC UA NodeId ↔ TORUS 주소, TORUS Item ↔ OPC UA 값 변환과 설정 검증.
public class OpcUaValuesTests
{
    // NodeId 규칙은 HTTP 경로+쿼리와 같다(TORUS 주소에서 "data:/"를 뗀 것).
    [Fact]
    public void Parses_node_path_like_http_path()
    {
        Assert.True(OpcUaValues.TryParseNodePath(
            "/machine/channel/axis/machinePosition?machine=1&channel=1&axis=1", out var path, out var filter));
        Assert.Equal("/machine/channel/axis/machinePosition", path);
        Assert.Equal("machine=1&channel=1&axis=1", filter);
        Assert.Equal("data://machine/channel/axis/machinePosition", OpcUaValues.TorusAddress(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("data://machine/channel/axis/machinePosition?machine=1")] // 논문 초기 형식은 받지 않음
    [InlineData("/machine/channel/axis/machinePosition")]                // 필터 없음 (폴더 NodeId와 겹침)
    [InlineData("/machine/channel/axis/machinePosition?")]               // 빈 필터
    [InlineData("/machine?machine=1")]                                   // 루트 자체
    [InlineData("/other/x?machine=1")]
    [InlineData("/machine/channel/?machine=1")]
    [InlineData("/machine//axis?machine=1")]
    [InlineData(" /machine/channel/axis?machine=1")]
    public void Rejects_invalid_node_paths(string? identifier)
    {
        Assert.False(OpcUaValues.TryParseNodePath(identifier, out _, out _));
    }

    [Fact]
    public void Folder_paths_below_root()
    {
        Assert.Equal(new[] { "/machine/channel", "/machine/channel/axis", "/machine/channel/axis/machinePosition" },
            OpcUaValues.FolderPathsBelowRoot("/machine/channel/axis/machinePosition"));
        Assert.Equal(new[] { "/machine/ncMemory" }, OpcUaValues.FolderPathsBelowRoot("/machine/ncMemory"));
    }

    // 논문 Fig. 8과 같은 TORUS 응답.
    [Fact]
    public void Reads_double_array_item()
    {
        const string json = "{\"value\":[84.81207,-14.17339,159.56296],\"status\":0,\"datatype\":\"double\"," +
                            "\"time\":\"2024-09-08T23:42:27.730\",\"address\":\"data://machine/channel/axis/machineposition\"}";
        Assert.True(OpcUaValues.TryReadItem(json, out var v));
        Assert.Equal(TorusValueKind.Double, v!.Kind);
        Assert.True(v.IsArray);
        Assert.Equal(new[] { 84.81207, -14.17339, 159.56296 }, (double[])v.Value);
    }

    [Theory]
    [InlineData("{\"value\":[3.5],\"status\":0,\"datatype\":\"double\"}", "Double", 3.5)]
    [InlineData("{\"value\":[7],\"status\":0,\"datatype\":\"long\"}", "Long", 7L)]
    [InlineData("{\"value\":[true],\"status\":0,\"datatype\":\"boolean\"}", "Boolean", true)]
    [InlineData("{\"value\":[\"O0001\"],\"status\":0,\"datatype\":\"string\"}", "String", "O0001")]
    public void Single_element_of_scalar_type_is_scalar(string json, string kind, object expected)
    {
        Assert.True(OpcUaValues.TryReadItem(json, out var v));
        Assert.Equal(kind, v!.Kind.ToString());
        Assert.False(v.IsArray);
        Assert.Equal(expected, v.Value);
    }

    // list 타입은 원소가 하나여도 배열 (노드 ValueRank와 값이 어긋나지 않도록).
    [Fact]
    public void List_type_is_always_array()
    {
        Assert.True(OpcUaValues.TryReadItem("{\"value\":[1],\"status\":0,\"datatype\":\"listlong\"}", out var one));
        Assert.True(one!.IsArray);
        Assert.Equal(new long[] { 1 }, (long[])one.Value);

        Assert.True(OpcUaValues.TryReadItem("{\"value\":[],\"status\":0,\"datatype\":\"liststring\"}", out var empty));
        Assert.True(empty!.IsArray);
        Assert.Empty((string[])empty.Value);
    }

    // liststring 원소가 객체면 JSON 원문 문자열로.
    [Fact]
    public void Liststring_object_elements_become_raw_json()
    {
        Assert.True(OpcUaValues.TryReadItem("{\"value\":[{\"a\":1},\"x\"],\"status\":0,\"datatype\":\"liststring\"}", out var v));
        Assert.Equal(new[] { "{\"a\":1}", "x" }, (string[])v!.Value);
    }

    [Theory]
    [InlineData("{\"status\":566099968}")]                                   // TORUS 오류 (Null value)
    [InlineData("{\"value\":[1],\"status\":1,\"datatype\":\"double\"}")]     // status != 0
    [InlineData("{\"value\":[],\"status\":0,\"datatype\":\"double\"}")]      // 스칼라 타입인데 값 없음
    [InlineData("{\"value\":[1],\"status\":0,\"datatype\":\"float\"}")]      // 모르는 타입
    [InlineData("{\"value\":[\"a\"],\"status\":0,\"datatype\":\"long\"}")]   // 타입과 다른 원소
    [InlineData("{\"value\":[1.5],\"status\":0,\"datatype\":\"long\"}")]
    [InlineData("{\"value\":1,\"status\":0,\"datatype\":\"double\"}")]       // value가 배열이 아님
    [InlineData("not json")]
    public void Rejects_unusable_items(string json)
    {
        Assert.False(OpcUaValues.TryReadItem(json, out _));
    }

    // 쓰기 body는 항상 {"value":[...]} 배열. 문자열은 제대로 이스케이프하고, 실수는 문화권과 무관하게 '.'.
    [Fact]
    public void Builds_array_body_for_scalars_and_arrays()
    {
        Assert.Equal("{\"value\":[12.34]}", OpcUaValues.BuildUpdateBody(12.34));
        Assert.Equal("{\"value\":[5]}", OpcUaValues.BuildUpdateBody(5L));
        Assert.Equal("{\"value\":[true]}", OpcUaValues.BuildUpdateBody(true));
        Assert.Equal("{\"value\":[1.5,2]}", OpcUaValues.BuildUpdateBody(new[] { 1.5, 2.0 }));
        Assert.Equal("{\"value\":[\"a\\\"b\",\"한글\"]}", OpcUaValues.BuildUpdateBody(new[] { "a\"b", "한글" }));
    }

    [Fact]
    public void Body_uses_invariant_decimal_separator()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("{\"value\":[12.34]}", OpcUaValues.BuildUpdateBody(12.34));
            Assert.Equal("12.34", OpcUaValues.Signature(12.34));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void Refuses_values_that_must_not_reach_torus()
    {
        Assert.Null(OpcUaValues.BuildUpdateBody(null));
        Assert.Null(OpcUaValues.BuildUpdateBody(Array.Empty<double>()));
        Assert.Null(OpcUaValues.BuildUpdateBody(double.NaN));
        Assert.Null(OpcUaValues.BuildUpdateBody(1.5f));      // 노드 타입(Double)과 다른 형
        Assert.Null(OpcUaValues.BuildUpdateBody(new object()));
    }

    [Fact]
    public void Signature_detects_changes()
    {
        Assert.Equal(OpcUaValues.Signature(new[] { 1.0, 2.0 }), OpcUaValues.Signature(new[] { 1.0, 2.0 }));
        Assert.NotEqual(OpcUaValues.Signature(new[] { 1.0, 2.0 }), OpcUaValues.Signature(new[] { 1.0, 2.5 }));
        Assert.NotEqual(OpcUaValues.Signature(true), OpcUaValues.Signature(false));
    }

    [Fact]
    public void Default_settings_are_valid()
    {
        Assert.Null(OpcUaSettings.Validate(new OpcUaSettings()));
    }

    [Theory]
    [InlineData("http://0.0.0.0:4840", "None", "None", false)]
    [InlineData("opc.tcp://0.0.0.0", "None", "None", false)]                   // 포트 없음
    [InlineData("opc.tcp://0.0.0.0:4840", "Basic256", "Sign", false)]          // 폐기된 정책
    [InlineData("opc.tcp://0.0.0.0:4840", "None", "Sign", false)]              // 정책 None + 모드 Sign
    [InlineData("opc.tcp://0.0.0.0:4840", "Basic256Sha256", "None", false)]    // 정책 있음 + 모드 None
    [InlineData("opc.tcp://0.0.0.0:4840", "None", "None", true)]               // 사용자 이름 모드인데 계정 없음
    public void Rejects_invalid_settings(string endpoint, string policy, string mode, bool useUsername)
    {
        var s = new OpcUaSettings { EndpointUrl = endpoint, SecurityPolicy = policy, SecurityMode = mode, UseUsername = useUsername };
        Assert.NotNull(OpcUaSettings.Validate(s));
    }

    // 저장한 설정이 다음 실행에서 그대로 읽혀야 한다(자동 수락 체크가 저장되지 않던 문제, 암호화한 비밀번호가 잘리던 문제).
    [Fact]
    public void Settings_round_trip_through_config_file()
    {
        string file = "test_opcua_" + Guid.NewGuid().ToString("N") + ".ini";
        try
        {
            new OpcUaSettings
            {
                EndpointUrl = "opc.tcp://localhost:4841", SecurityPolicy = "Basic256Sha256", SecurityMode = "Sign",
                UseUsername = true, Username = "user", Password = "p@ss 한글", AutoAcceptUntrustedCertificates = true,
            }.Save(file);

            var loaded = OpcUaSettings.Load(file);
            Assert.Equal("opc.tcp://localhost:4841", loaded.EndpointUrl);
            Assert.Equal("Basic256Sha256", loaded.SecurityPolicy);
            Assert.Equal("Sign", loaded.SecurityMode);
            Assert.True(loaded.UseUsername);
            Assert.Equal("user", loaded.Username);
            Assert.Equal("p@ss 한글", loaded.Password);
            Assert.True(loaded.AutoAcceptUntrustedCertificates);
        }
        finally
        {
            File.Delete(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, file));
        }
    }

    [Fact]
    public void Accepts_secured_username_settings()
    {
        var s = new OpcUaSettings
        {
            EndpointUrl = "opc.tcp://localhost:4840", SecurityPolicy = "Aes256_Sha256_RsaPss",
            SecurityMode = "SignAndEncrypt", UseUsername = true, Username = "user", Password = "pw",
        };
        Assert.Null(OpcUaSettings.Validate(s));
    }
}
