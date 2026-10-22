using TorusGateway.WebServer;
using Xunit;

namespace TorusGateway.Tests;

// PUT body 검사(MachineController.ValidateUpdateBody)와 ncpath 파일 이름 추출(FileController.FileNameOf).
public class RequestValidationTests
{
    // TORUS Item 형식: 루트 객체 + 소문자 value + 비어 있지 않은 배열. 원소 타입은 제한하지 않음.
    [Theory]
    [InlineData("{\"value\":[12.34]}")]
    [InlineData("{\"value\":[1,2,3]}")]
    [InlineData("{\"value\":[\"O0001\"]}")]
    [InlineData("{\"value\":[12.34],\"extra\":1}")]
    public void Accepts_item_with_non_empty_value_array(string body)
    {
        Assert.Null(MachineController.ValidateUpdateBody(body));
    }

    // value가 배열이 아니면 TORUS 통신 매니저가 크래시하므로 반드시 거부.
    [Theory]
    [InlineData("{\"value\":12.34}")]          // 실제로 mgrCommunication을 크래시시킨 형식
    [InlineData("{\"value\":\"12.34\"}")]
    [InlineData("{\"value\":null}")]
    [InlineData("{\"value\":{}}")]
    [InlineData("{\"value\":[]}")]
    [InlineData("{\"Value\":[12.34]}")]        // TORUS는 소문자 value만 인식
    [InlineData("{}")]
    [InlineData("[12.34]")]
    [InlineData("12.34")]
    public void Rejects_non_array_or_missing_value(string body)
    {
        Assert.NotNull(MachineController.ValidateUpdateBody(body));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{value:[1]}")]
    [InlineData("{\"value\":[1]")]
    public void Rejects_invalid_json(string body)
    {
        Assert.Equal("Invalid JSON in request body", MachineController.ValidateUpdateBody(body));
    }

    [Theory]
    [InlineData("//NC/Partprograms/TEST.MPF", "TEST.MPF")]
    [InlineData("//CNC_MEM/USER/PATH1/O9998", "O9998")]
    [InlineData("C:\\nc\\O0001", "O0001")]
    [InlineData("//NC/", "")]                  // 폴더(구분자로 끝남)
    [InlineData("//CNC_MEM/USER\\", "")]
    [InlineData("///", "")]
    public void FileNameOf_returns_last_segment_or_empty_for_folders(string ncPath, string expected)
    {
        Assert.Equal(expected, FileController.FileNameOf(ncPath));
    }
}
