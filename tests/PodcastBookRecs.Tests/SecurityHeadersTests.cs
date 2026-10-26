using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using PodcastBookRecs.Web.Services;

namespace PodcastBookRecs.Tests;

public class SecurityHeadersTests
{
    [Fact]
    public async Task Every_response_gets_nosniff_and_no_referrer()
    {
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        app.UseSecurityHeaders();
        app.Run(context => context.Response.WriteAsync("ok", context.RequestAborted));
        var context = new DefaultHttpContext();

        await app.Build()(context);

        Assert.Equal("nosniff", context.Response.Headers.XContentTypeOptions);
        Assert.Equal("no-referrer", context.Response.Headers["Referrer-Policy"]);
    }
}
