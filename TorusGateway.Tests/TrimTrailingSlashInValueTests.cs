using System.Text.Json;
using TorusGateway.WebServer;
using Xunit;

namespace TorusGateway.Tests
{
    public class TrimTrailingSlashInValueTests
    {
        [Fact]
        public void RemovesTrailingSlash_FromStringValue()
        {
            string result = CustomController.TrimTrailingSlashInValue("{\"status\":0,\"value\":\"//NC/MPF.DIR/\"}");

            using var doc = JsonDocument.Parse(result);
            Assert.Equal("//NC/MPF.DIR", doc.RootElement.GetProperty("value").GetString());
            Assert.Equal(0, doc.RootElement.GetProperty("status").GetInt32());
        }

        [Fact]
        public void RemovesAllTrailingSlashes()
        {
            string result = CustomController.TrimTrailingSlashInValue("{\"value\":\"abc///\"}");

            using var doc = JsonDocument.Parse(result);
            Assert.Equal("abc", doc.RootElement.GetProperty("value").GetString());
        }

        [Fact]
        public void PreservesLeadingSlashes()
        {
            string result = CustomController.TrimTrailingSlashInValue("{\"value\":\"//NC/X/\"}");

            using var doc = JsonDocument.Parse(result);
            Assert.Equal("//NC/X", doc.RootElement.GetProperty("value").GetString());
        }

        [Fact]
        public void NoTrailingSlash_ReturnsUnchanged()
        {
            string input = "{\"value\":\"//NC/MPF.DIR\"}";
            Assert.Equal(input, CustomController.TrimTrailingSlashInValue(input));
        }

        [Fact]
        public void NonStringValue_ReturnsUnchanged()
        {
            string input = "{\"value\":123}";
            Assert.Equal(input, CustomController.TrimTrailingSlashInValue(input));
        }

        [Fact]
        public void MissingValueKey_ReturnsUnchanged()
        {
            string input = "{\"status\":0}";
            Assert.Equal(input, CustomController.TrimTrailingSlashInValue(input));
        }

        [Fact]
        public void NonObjectRoot_ReturnsUnchanged()
        {
            string input = "[\"a/\"]";
            Assert.Equal(input, CustomController.TrimTrailingSlashInValue(input));
        }

        [Fact]
        public void PreservesOtherKeys()
        {
            string result = CustomController.TrimTrailingSlashInValue("{\"status\":0,\"value\":\"p/\",\"extra\":\"keep\"}");

            using var doc = JsonDocument.Parse(result);
            Assert.Equal("p", doc.RootElement.GetProperty("value").GetString());
            Assert.Equal("keep", doc.RootElement.GetProperty("extra").GetString());
        }
    }
}
