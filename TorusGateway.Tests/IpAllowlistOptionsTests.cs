using System.Net;
using TorusGateway.WebServer;
using Xunit;

namespace TorusGateway.Tests;

public class IpAllowlistOptionsTests
{
    [Fact]
    public void Empty_csv_denies_all()
    {
        var opts = new IpAllowlistOptions("");
        Assert.False(opts.IsAllowed(IPAddress.Loopback));
        Assert.False(opts.IsAllowed(IPAddress.Parse("192.168.1.1")));
    }

    [Fact]
    public void Star_allows_all()
    {
        var opts = new IpAllowlistOptions("*");
        Assert.True(opts.AllowAll);
        Assert.True(opts.IsAllowed(IPAddress.Loopback));
        Assert.True(opts.IsAllowed(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void Single_ip_matches_exact_only()
    {
        var opts = new IpAllowlistOptions("192.168.1.10");
        Assert.True(opts.IsAllowed(IPAddress.Parse("192.168.1.10")));
        Assert.False(opts.IsAllowed(IPAddress.Parse("192.168.1.11")));
    }

    [Fact]
    public void Cidr_matches_subnet()
    {
        var opts = new IpAllowlistOptions("192.168.1.0/24");
        Assert.True(opts.IsAllowed(IPAddress.Parse("192.168.1.1")));
        Assert.True(opts.IsAllowed(IPAddress.Parse("192.168.1.255")));
        Assert.False(opts.IsAllowed(IPAddress.Parse("192.168.2.1")));
    }

    [Fact]
    public void Multiple_entries_or_combined()
    {
        var opts = new IpAllowlistOptions("127.0.0.1, 10.0.0.0/8");
        Assert.True(opts.IsAllowed(IPAddress.Parse("127.0.0.1")));
        Assert.True(opts.IsAllowed(IPAddress.Parse("10.1.2.3")));
        Assert.False(opts.IsAllowed(IPAddress.Parse("192.168.1.1")));
    }

    [Fact]
    public void Ipv6_loopback_matches_ipv4_loopback_rule()
    {
        // ::1도 127.0.0.1 규칙에 매칭되어야 함 (loopback 정규화)
        var opts = new IpAllowlistOptions("127.0.0.1");
        Assert.True(opts.IsAllowed(IPAddress.Parse("::1")));
    }

    [Fact]
    public void Null_ip_is_denied_when_not_star()
    {
        var opts = new IpAllowlistOptions("127.0.0.1");
        Assert.False(opts.IsAllowed(null));
    }

    [Fact]
    public void Null_ip_allowed_when_star()
    {
        var opts = new IpAllowlistOptions("*");
        Assert.True(opts.IsAllowed(null));
    }

    [Fact]
    public void Invalid_entries_are_ignored()
    {
        var opts = new IpAllowlistOptions("garbage, 192.168.1.10, !!!");
        Assert.True(opts.IsAllowed(IPAddress.Parse("192.168.1.10")));
        Assert.False(opts.IsAllowed(IPAddress.Parse("192.168.1.11")));
    }

    [Fact]
    public void Update_replaces_state()
    {
        var opts = new IpAllowlistOptions("192.168.1.10");
        Assert.True(opts.IsAllowed(IPAddress.Parse("192.168.1.10")));
        Assert.False(opts.IsAllowed(IPAddress.Parse("10.0.0.1")));

        opts.Update("10.0.0.0/8");
        Assert.False(opts.IsAllowed(IPAddress.Parse("192.168.1.10")));
        Assert.True(opts.IsAllowed(IPAddress.Parse("10.0.0.1")));
    }

    [Fact]
    public void Update_to_star_grants_all()
    {
        var opts = new IpAllowlistOptions("127.0.0.1");
        Assert.False(opts.IsAllowed(IPAddress.Parse("192.168.1.1")));

        opts.Update("*");
        Assert.True(opts.AllowAll);
        Assert.True(opts.IsAllowed(IPAddress.Parse("192.168.1.1")));
    }

    [Fact]
    public void Update_to_empty_revokes_all()
    {
        var opts = new IpAllowlistOptions("*");
        Assert.True(opts.AllowAll);
        opts.Update("");
        Assert.False(opts.AllowAll);
        Assert.False(opts.IsAllowed(IPAddress.Loopback));
    }
}
