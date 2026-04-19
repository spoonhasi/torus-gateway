using TorusGateway.WebServer;
using Xunit;

namespace TorusGateway.Tests;

public class RenameValueArrayKeyTests
{
    [Fact]
    public void Renames_key_in_every_array_element()
    {
        string input = "{\"status\":0,\"value\":[{\"vendorCode\":\"KCNC\"},{\"vendorCode\":\"FANUC\"}]}";
        string result = CustomController.RenameValueArrayKey(input, "vendorCode", "vendor");
        Assert.Contains("\"vendor\":\"KCNC\"", result);
        Assert.Contains("\"vendor\":\"FANUC\"", result);
        Assert.DoesNotContain("vendorCode", result);
    }

    [Fact]
    public void Leaves_elements_without_old_key_unchanged()
    {
        string input = "{\"value\":[{\"id\":1},{\"vendorCode\":\"KCNC\"}]}";
        string result = CustomController.RenameValueArrayKey(input, "vendorCode", "vendor");
        Assert.Contains("\"id\":1", result);
        Assert.Contains("\"vendor\":\"KCNC\"", result);
    }

    [Fact]
    public void Preserves_other_keys_on_same_element()
    {
        string input = "{\"value\":[{\"id\":1,\"vendorCode\":\"KCNC\",\"name\":\"M1\"}]}";
        string result = CustomController.RenameValueArrayKey(input, "vendorCode", "vendor");
        Assert.Contains("\"id\":1", result);
        Assert.Contains("\"name\":\"M1\"", result);
        Assert.Contains("\"vendor\":\"KCNC\"", result);
    }

    [Fact]
    public void Returns_input_when_value_is_not_array()
    {
        string input = "{\"status\":0,\"value\":\"str\"}";
        string result = CustomController.RenameValueArrayKey(input, "vendorCode", "vendor");
        Assert.Equal(input, result);
    }

    [Fact]
    public void Returns_input_when_no_value_key()
    {
        string input = "{\"status\":0}";
        string result = CustomController.RenameValueArrayKey(input, "vendorCode", "vendor");
        Assert.Equal(input, result);
    }

    [Fact]
    public void Returns_input_when_not_object()
    {
        string input = "[1,2,3]";
        string result = CustomController.RenameValueArrayKey(input, "vendorCode", "vendor");
        Assert.Equal(input, result);
    }

    [Fact]
    public void Preserves_non_object_elements()
    {
        string input = "{\"value\":[1,\"two\",{\"vendorCode\":\"KCNC\"}]}";
        string result = CustomController.RenameValueArrayKey(input, "vendorCode", "vendor");
        Assert.Contains("1", result);
        Assert.Contains("\"two\"", result);
        Assert.Contains("\"vendor\":\"KCNC\"", result);
    }
}
