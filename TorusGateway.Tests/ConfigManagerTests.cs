using System;
using System.IO;
using Xunit;

namespace TorusGateway.Tests;

// ConfigManager는 AppDomain.CurrentDomain.BaseDirectory(테스트 출력 폴더)를 기준으로 INI 파일을 다룬다.
// 각 테스트마다 고유한 파일명을 사용하여 격리하고, Dispose에서 정리한다.
public class ConfigManagerTests : IDisposable
{
    private readonly string _fileName = "test_config_" + Guid.NewGuid().ToString("N") + ".ini";
    private string FullPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _fileName);

    public void Dispose()
    {
        try { if (File.Exists(FullPath)) File.Delete(FullPath); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Write_then_Read_returns_same_value()
    {
        ConfigManager.Write(_fileName, "Section1", "Key1", "Value1");
        Assert.Equal("Value1", ConfigManager.Read(_fileName, "Section1", "Key1"));
    }

    [Fact]
    public void Read_missing_key_returns_default()
    {
        Assert.Equal("fallback", ConfigManager.Read(_fileName, "MissingSection", "MissingKey", "fallback"));
    }

    [Fact]
    public void Read_missing_key_writes_default_for_future_reads()
    {
        ConfigManager.Read(_fileName, "S", "K", "initial");
        Assert.Equal("initial", ConfigManager.Read(_fileName, "S", "K"));
    }

    [Fact]
    public void Write_overwrites_existing_value()
    {
        ConfigManager.Write(_fileName, "S", "K", "first");
        ConfigManager.Write(_fileName, "S", "K", "second");
        Assert.Equal("second", ConfigManager.Read(_fileName, "S", "K"));
    }

    [Fact]
    public void Separate_sections_do_not_clash()
    {
        ConfigManager.Write(_fileName, "SectionA", "Key", "A");
        ConfigManager.Write(_fileName, "SectionB", "Key", "B");
        Assert.Equal("A", ConfigManager.Read(_fileName, "SectionA", "Key"));
        Assert.Equal("B", ConfigManager.Read(_fileName, "SectionB", "Key"));
    }
}
