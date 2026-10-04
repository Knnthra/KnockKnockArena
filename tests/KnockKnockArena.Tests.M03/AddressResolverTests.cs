using System.Net;
using System.Net.Sockets;
using KnockKnockArena.Shared.Protocol;

namespace KnockKnockArena.Tests.M03;

/// <summary>
/// Module 3: AddressResolver - what the player types in the Host field, turned into
/// the IPv4 address a socket can use. Your resolver must pass all of these.
///
/// No test depends on the internet: "localhost" is answered by the machine itself,
/// IP literals never reach DNS, and ".invalid" is a name DNS is guaranteed never to know.
/// </summary>
public class AddressResolverTests
{
    // ------------------------------------------------------------------ IP literals

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("192.168.50.141")]
    [InlineData("10.0.0.1")]
    [InlineData("192.0.2.1")]   // TEST-NET: not reachable, but a valid address - no DNS involved
    public void AnIPv4Literal_IsUsedAsItIs(string literal)
    {
        Assert.True(AddressResolver.TryResolve(literal, out IPAddress address, out string error), error);
        Assert.Equal(IPAddress.Parse(literal), address);
        Assert.Equal("", error);
    }

    [Theory]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("2606:4700:10::6814:179a")]
    public void AnIPv6Literal_IsRefused_BecauseTheServerListensOnIPv4(string literal)
    {
        Assert.False(AddressResolver.TryResolve(literal, out _, out string error));
        Assert.Equal($"'{literal}' is an IPv6 address; the server listens on IPv4", error);
    }

    // ------------------------------------------------------------------ localhost

    [Fact]
    public void Localhost_GivesTheIPv4Loopback()
    {
        Assert.True(AddressResolver.TryResolve("localhost", out IPAddress address, out string error), error);
        Assert.Equal(IPAddress.Loopback, address);
    }

    [Fact]
    public void Localhost_PicksIPv4_EvenWhenDnsListsIPv6()
    {
        // On most machines DNS gives ::1 AND 127.0.0.1 for localhost - often ::1 first.
        // Taking the first address blindly would pick ::1, where no server listens.
        IPAddress[] all = Dns.GetHostAddresses("localhost");
        Assert.True(AddressResolver.TryResolve("localhost", out IPAddress address, out _));

        Assert.Equal(AddressFamily.InterNetwork, address.AddressFamily);
        Assert.Contains(address, all);
    }

    [Theory]
    [InlineData(" localhost")]
    [InlineData("localhost ")]
    [InlineData("  127.0.0.1  ")]
    [InlineData("\t127.0.0.1\n")]
    public void SpacesAroundTheHost_AreIgnored(string typed)
    {
        Assert.True(AddressResolver.TryResolve(typed, out IPAddress address, out string error), error);
        Assert.Equal(IPAddress.Loopback, address);
    }

    // ------------------------------------------------------------------ nothing typed

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NoHost_IsRefused(string? typed)
    {
        Assert.False(AddressResolver.TryResolve(typed!, out _, out string error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    // ------------------------------------------------------------------ unknown names

    [Fact]
    public void AnUnknownName_GivesAnErrorNamingTheHost()
    {
        // ".invalid" is reserved (RFC 2606): DNS never has an address for it. Offline, the
        // resolver may get another error than "host not found"; both name the host.
        Assert.False(AddressResolver.TryResolve("no-such-host.invalid", out _, out string error));
        Assert.True(
            error == "unknown host 'no-such-host.invalid' (DNS has no address for it)" ||
            error.StartsWith("could not resolve 'no-such-host.invalid': "),
            $"got: {error}");
    }

    [Fact]
    public void TryResolve_DoesNotThrow_ForAnUnknownName()
    {
        Exception? thrown = Record.Exception(() => AddressResolver.TryResolve("no-such-host.invalid", out _, out _));
        Assert.Null(thrown);
    }

    [Fact]
    public void TryResolve_LeavesNoAddress_WhenItFails()
    {
        AddressResolver.TryResolve("::1", out IPAddress address, out _);
        Assert.Equal(IPAddress.None, address);
    }

    // ------------------------------------------------------------------ Resolve

    [Fact]
    public void Resolve_ReturnsTheAddress()
    {
        Assert.Equal(IPAddress.Loopback, AddressResolver.Resolve("localhost"));
        Assert.Equal(IPAddress.Parse("192.168.50.141"), AddressResolver.Resolve("192.168.50.141"));
    }

    [Fact]
    public void Resolve_ThrowsAProtocolException_WithTheSameMessage()
    {
        Assert.Throws<ProtocolException>(() => AddressResolver.Resolve("::1"));
    }

    [Fact]
    public void Resolve_ThrowsForNoHost()
    {
        Assert.Throws<ProtocolException>(() => AddressResolver.Resolve(""));
    }
}
