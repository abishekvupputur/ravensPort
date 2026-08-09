using RavensPort.Core.Models;

namespace RavensPort.Core.Tests;

public class UrlValidationTests
{
    [Theory]
    [InlineData("https://accounts.google.com")]
    [InlineData("https://cloud.example.com/apps/oauth2/api/v1/token")]
    public void ValidateEndpoint_AllowsHttps(string url) =>
        Assert.Null(UrlValidation.ValidateEndpoint(url, "Token endpoint"));

    [Theory]
    [InlineData("http://127.0.0.1:8080/token")]
    [InlineData("http://localhost:3000")]
    public void ValidateEndpoint_AllowsPlainHttpOnLoopback(string url) =>
        Assert.Null(UrlValidation.ValidateEndpoint(url, "Upstream base URL"));

    [Theory]
    // The plain-http URLs below are the input this test rejects, not a transport this project
    // uses, so DevSkim's insecure-URL rule is suppressed on them rather than the strings changed.
    [InlineData("http://example.com/token")] // DevSkim: ignore DS137138
    [InlineData("http://192.168.1.50/api")] // DevSkim: ignore DS137138
    public void ValidateEndpoint_RejectsPlainHttpOffMachine(string url)
    {
        // The whole point: these carry the client secret or access token, so cleartext here
        // puts them on the network.
        var error = UrlValidation.ValidateEndpoint(url, "Token endpoint");
        Assert.NotNull(error);
        Assert.Contains("cleartext", error);
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ftp://example.com")]
    public void ValidateEndpoint_RejectsNonHttpSchemes(string url) =>
        Assert.NotNull(UrlValidation.ValidateEndpoint(url, "Authority"));

    [Fact]
    public void ValidateEndpoint_RejectsNonAbsoluteUrls() =>
        Assert.NotNull(UrlValidation.ValidateEndpoint("not a url", "Authority"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateEndpoint_TreatsBlankAsUnset(string? url) =>
        Assert.Null(UrlValidation.ValidateEndpoint(url, "Authority"));

    [Theory]
    [InlineData("https://accounts.google.com/o/oauth2/auth", true)]
    [InlineData("http://127.0.0.1:51005/callback/", true)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("ms-settings:privacy", false)]
    [InlineData("\\\\server\\share\\payload.exe", false)]
    public void IsSafeToOpenInBrowser_OnlyAllowsWebUrls(string url, bool expected)
    {
        // Guards Process.Start(UseShellExecute: true), where a non-http value makes Windows
        // launch a program or protocol handler instead of a browser.
        Assert.Equal(expected, UrlValidation.IsSafeToOpenInBrowser(url));
    }
}
