using Earmarked.Web.Services;

namespace Earmarked.Tests;

public class ReturnUrlsTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/status")]
    [InlineData("/podcasts/0199f0a2-0000-7000-8000-000000000000?tab=episodes")]
    public void Local_paths_are_followed(string url) => Assert.Equal(url, ReturnUrls.LocalOrHome(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("status")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("/\t/evil.example")]
    [InlineData("/\n/evil.example")]
    [InlineData("https://evil.example")]
    [InlineData("javascript:alert(1)")]
    public void Anything_else_goes_home(string? url) => Assert.Equal("/", ReturnUrls.LocalOrHome(url));
}
