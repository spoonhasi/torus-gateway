using TorusGateway.WebServer;
using Xunit;

namespace TorusGateway.Tests;

public class FilterStatusAndValueTests
{
    [Fact]
    public void Unwraps_singleton_array_when_no_list_datatype()
    {
        string result = TorusController.FilterStatusAndValue("{\"status\":0,\"value\":[42],\"datatype\":\"int\"}");
        Assert.Equal("{\"status\":0,\"value\":42}", result);
    }

    [Fact]
    public void Keeps_array_when_datatype_contains_list()
    {
        string result = TorusController.FilterStatusAndValue("{\"status\":0,\"value\":[42],\"datatype\":\"list-int\"}");
        Assert.Equal("{\"status\":0,\"value\":[42]}", result);
    }

    [Fact]
    public void Keeps_array_when_length_gt_1()
    {
        string result = TorusController.FilterStatusAndValue("{\"status\":0,\"value\":[1,2,3]}");
        Assert.Equal("{\"status\":0,\"value\":[1,2,3]}", result);
    }

    [Fact]
    public void Strips_extra_keys_like_timestamp()
    {
        string result = TorusController.FilterStatusAndValue("{\"status\":0,\"value\":[1],\"timestamp\":\"2026-01-01\",\"type\":\"x\"}");
        Assert.Equal("{\"status\":0,\"value\":1}", result);
    }

    [Fact]
    public void Adds_error_key_when_status_nonzero()
    {
        // 0x21B00000 = 565182464 = "Internal exception"
        string result = TorusController.FilterStatusAndValue("{\"status\":565182464,\"value\":[]}");
        Assert.Contains("\"status\":565182464", result);
        Assert.Contains("\"error\":\"Internal exception\"", result);
    }

    [Fact]
    public void Uses_unknown_message_for_unmapped_status()
    {
        string result = TorusController.FilterStatusAndValue("{\"status\":99999,\"value\":[]}");
        Assert.Contains("\"error\":\"Unknown status code: 0x0001869F\"", result);
    }

    [Fact]
    public void Does_not_add_error_key_on_success()
    {
        string result = TorusController.FilterStatusAndValue("{\"status\":0,\"value\":[1]}");
        Assert.DoesNotContain("error", result);
    }

    [Fact]
    public void Returns_input_unchanged_when_not_object()
    {
        string input = "[1,2,3]";
        string result = TorusController.FilterStatusAndValue(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void UnwrapSingletonArray_false_keeps_array_even_without_list_datatype()
    {
        string result = TorusController.FilterStatusAndValue("{\"status\":0,\"value\":[42]}", unwrapSingletonArray: false);
        Assert.Equal("{\"status\":0,\"value\":[42]}", result);
    }

    [Fact]
    public void Preserves_korean_characters_without_escape()
    {
        string result = TorusController.FilterStatusAndValue("{\"status\":0,\"value\":[\"메인제어기\"]}");
        Assert.Contains("메인제어기", result);
        Assert.DoesNotContain("\\u", result);
    }
}
