using System.Text;
using TorusGateway;
using Xunit;

namespace TorusGateway.Tests;

// NcExplorerWindow.TryDecodeNcText: 업로드할 로컬 파일이 NC 텍스트(G-code)로 안전한지 판정.
// NULL 바이트가 있거나 엄격 UTF-8 디코딩에 실패하면 바이너리로 보고 false.
public class TryDecodeNcTextTests
{
    [Fact]
    public void Plain_ascii_is_text()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("O0001\r\nG0 X0 Y0\r\nM30\r\n");
        Assert.True(NcExplorerWindow.TryDecodeNcText(bytes, out string text));
        Assert.Equal("O0001\r\nG0 X0 Y0\r\nM30\r\n", text);
    }

    [Fact]
    public void Utf8_korean_is_text()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("주석 안녕\nG1 X10\n");
        Assert.True(NcExplorerWindow.TryDecodeNcText(bytes, out string text));
        Assert.Equal("주석 안녕\nG1 X10\n", text);
    }

    [Fact]
    public void Utf8_bom_is_stripped()
    {
        byte[] body = Encoding.UTF8.GetBytes("O0002\nM30\n");
        byte[] bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray();
        Assert.True(NcExplorerWindow.TryDecodeNcText(bytes, out string text));
        Assert.Equal("O0002\nM30\n", text);   // BOM 제거됨
    }

    [Fact]
    public void Empty_file_is_text()
    {
        Assert.True(NcExplorerWindow.TryDecodeNcText(System.Array.Empty<byte>(), out string text));
        Assert.Equal("", text);
    }

    [Fact]
    public void Tabs_and_newlines_are_text()
    {
        // 일반 제어문자(\t \r \n)는 텍스트로 허용 — 과잉 거부하지 않는다.
        byte[] bytes = Encoding.ASCII.GetBytes("\tG0\tX0\r\n\tY0\n");
        Assert.True(NcExplorerWindow.TryDecodeNcText(bytes, out _));
    }

    [Fact]
    public void Null_byte_is_binary()
    {
        byte[] bytes = new byte[] { (byte)'O', (byte)'0', 0x00, (byte)'1' };
        Assert.False(NcExplorerWindow.TryDecodeNcText(bytes, out _));
    }

    [Fact]
    public void Invalid_utf8_sequence_is_binary()
    {
        // 0xC3은 연속 바이트를 기대하지만 0x28('(')은 연속 바이트가 아님 → 유효하지 않은 UTF-8
        byte[] bytes = new byte[] { 0xC3, 0x28 };
        Assert.False(NcExplorerWindow.TryDecodeNcText(bytes, out _));
    }

    [Fact]
    public void Png_header_is_binary()
    {
        // PNG 시그니처 — NULL 바이트 포함
        byte[] bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D };
        Assert.False(NcExplorerWindow.TryDecodeNcText(bytes, out _));
    }
}
