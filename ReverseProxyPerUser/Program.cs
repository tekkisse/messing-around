using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.Authentication;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;
using ReverseProxyPerUser.Hubs;
using ReverseProxyPerUser.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options => { options.LoginPath = "/login"; });

builder.Services.AddAuthorization();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();
builder.Services.AddSignalR();

builder.Services.AddSingleton<KubernetesStartupService>();
builder.Services.AddSingleton<StartupCheckService>();

builder.Services.AddReverseProxy()
    .LoadFromMemory(new[]
    {
        new RouteConfig()
        {
            RouteId = "default",
            ClusterId = "default",
            Match = new RouteMatch()
            {
                Path = "/{**catch-all}"
            }
        }
    }, new[]
    {
        new ClusterConfig()
        {
            ClusterId = "default",
            Destinations = new Dictionary<string, DestinationConfig>()
            {
                { "default", new DestinationConfig() { Address = "http://localhost" } }
            }
        }
    })
    .AddTransforms(transformContext =>
    {
        transformContext.AddRequestTransform(async context =>
        {
            var user = context.HttpContext.User.Identity?.Name;
            if (string.IsNullOrEmpty(user))
            {
                context.HttpContext.Response.StatusCode = 403;
                await context.HttpContext.Response.WriteAsync("Not authenticated");
                return;
            }

            var startupCheck = context.HttpContext.RequestServices.GetRequiredService<StartupCheckService>();
            var cache = context.HttpContext.RequestServices.GetRequiredService<IMemoryCache>();

            bool isUp = await startupCheck.IsBackendAvailable(user);
            if (!isUp)
            {
                string connectionId = context.HttpContext.Request.Query["cid"];
                if (!string.IsNullOrEmpty(connectionId))
                {
                    _ = startupCheck.StartBackendCheckAsync(user, connectionId);
                    cache.Set($"start:{user}", true, TimeSpan.FromMinutes(5));
                }

                context.HttpContext.Response.ContentType = "text/html";
                await context.HttpContext.Response.SendFileAsync("wwwroot/holding.html");
                return;
            }

            var uriBuilder = new UriBuilder(context.ProxyRequest.RequestUri!)
            {
                Scheme = "https",
                Host = $"{user}.example.com",
                Port = -1
            };
            context.ProxyRequest.RequestUri = uriBuilder.Uri;
        });
    });

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.UseStaticFiles();

app.MapHub<StartupNotifierHub>("/startupHub");

app.MapGet("/login", async context =>
{
    var username = context.Request.Query["user"].ToString();
    if (!string.IsNullOrEmpty(username))
    {
        var claims = new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, username ?? string.Empty) };
        var identity = new System.Security.Claims.ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new System.Security.Claims.ClaimsPrincipal(identity);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        await context.Response.WriteAsync($"Logged in as {username}");
    }
    else
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Missing ?user=username");
    }
});

app.MapReverseProxy();

app.Run();
