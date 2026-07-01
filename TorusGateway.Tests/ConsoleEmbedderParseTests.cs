using System.Linq;
using TorusGateway.Torus;
using Xunit;

namespace TorusGateway.Tests;

// ConsoleEmbedder.ParseStartTargets: Platform_Run.bat의 start 줄에서 실행 대상(exe)을 추출하는
// 순수 헬퍼 검증. (P/Invoke·재부모지정은 라이브 콘솔+UI 스레드 필요 → 수동 검증.)
public class ConsoleEmbedderParseTests
{
    // 실제 배포된 Platform_Run.bat 형식.
    [Fact]
    public void Parses_three_managers_from_canonical_bat()
    {
        const string bat =
            "echo off\r\n" +
            "start \"TORUS Application Manager\" /MIN mgrApplication.exe\r\n" +
            "start \"TORUS Communication Manager\" /MIN mgrCommunication.exe\r\n" +
            "start \"TORUS Command Manager\" /MIN mgrCommand.exe";

        var result = ConsoleEmbedder.ParseStartTargets(bat);

        Assert.Equal(
            new[] { "mgrApplication.exe", "mgrCommunication.exe", "mgrCommand.exe" },
            result.ToArray());
    }

    [Fact]
    public void Ignores_echo_rem_and_blank_lines()
    {
        const string bat =
            "@echo off\r\n" +
            "rem this is a comment\r\n" +
            ":: another comment\r\n" +
            "\r\n" +
            "start /MIN only.exe\r\n";

        var result = ConsoleEmbedder.ParseStartTargets(bat);

        Assert.Equal(new[] { "only.exe" }, result.ToArray());
    }

    [Fact]
    public void Drops_quoted_title_and_keeps_exe()
    {
        var result = ConsoleEmbedder.ParseStartTargets("start \"My Title\" /MIN foo.exe");
        Assert.Equal(new[] { "foo.exe" }, result.ToArray());
    }

    [Fact]
    public void Handles_start_without_title()
    {
        var result = ConsoleEmbedder.ParseStartTargets("start /MIN bar.exe");
        Assert.Equal(new[] { "bar.exe" }, result.ToArray());
    }

    [Fact]
    public void Handles_multiple_switches()
    {
        var result = ConsoleEmbedder.ParseStartTargets("start \"T\" /MIN /WAIT /B baz.exe");
        Assert.Equal(new[] { "baz.exe" }, result.ToArray());
    }

    [Fact]
    public void Handles_quoted_exe_path_with_spaces()
    {
        var result = ConsoleEmbedder.ParseStartTargets("start \"T\" \"C:\\Program Files\\x\\app.exe\"");
        Assert.Equal(new[] { "C:\\Program Files\\x\\app.exe" }, result.ToArray());
    }

    [Fact]
    public void Start_keyword_is_case_insensitive()
    {
        var result = ConsoleEmbedder.ParseStartTargets("START /min q.exe");
        Assert.Equal(new[] { "q.exe" }, result.ToArray());
    }

    [Fact]
    public void Empty_or_whitespace_returns_empty_list()
    {
        Assert.Empty(ConsoleEmbedder.ParseStartTargets(""));
        Assert.Empty(ConsoleEmbedder.ParseStartTargets("   \r\n  \r\n"));
    }

    [Fact]
    public void Non_start_lines_are_excluded()
    {
        const string bat =
            "taskkill /f /im mgrCommand.exe\r\n" +
            "pause\r\n" +
            "echo done";

        Assert.Empty(ConsoleEmbedder.ParseStartTargets(bat));
    }

    // 줄 끝(LF vs CRLF) 무관하게 동일 결과.
    [Theory]
    [InlineData("start /MIN a.exe\nstart /MIN b.exe")]
    [InlineData("start /MIN a.exe\r\nstart /MIN b.exe")]
    public void Handles_lf_and_crlf_line_endings(string bat)
    {
        var result = ConsoleEmbedder.ParseStartTargets(bat);
        Assert.Equal(new[] { "a.exe", "b.exe" }, result.ToArray());
    }
}
