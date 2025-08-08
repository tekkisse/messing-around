using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using ReverseProxyPerUser.Hubs;
using ReverseProxyPerUser.Services;
using System.Net.Http;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;

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
    .ConfigureHttpClient((context, handler) =>
    {
        if (handler is SocketsHttpHandler socketsHandler)
        {
            socketsHandler.SslOptions.RemoteCertificateValidationCallback =
                (sender, certificate, chain, sslPolicyErrors) => true; // Accept all certs
        }
    })
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
            Console.WriteLine("Raw URL: " + context.HttpContext.Request.GetDisplayUrl());
             
            var user = context.HttpContext.User.Identity?.Name;
            if (string.IsNullOrEmpty(user))
            {
                context.HttpContext.Response.StatusCode = 403;
                await context.HttpContext.Response.WriteAsync("Not authenticated");
                return;
            }

            Console.WriteLine("User: "+user);
            
            var startupCheck = context.HttpContext.RequestServices.GetRequiredService<StartupCheckService>();
            var cache = context.HttpContext.RequestServices.GetRequiredService<IMemoryCache>();

            bool isUp = await startupCheck.IsBackendAvailable(user);
            if (!isUp)
            {

                string connectionId = context.HttpContext.Request.Query["cid"];
                if (!string.IsNullOrEmpty(connectionId))
                {                
                    _ = startupCheck.StartBackendCheckAsync(user, connectionId);
                }

                context.HttpContext.Response.ContentType = "text/html";
                await context.HttpContext.Response.SendFileAsync("wwwroot/holding.html");
                return;
            }

            var originalRequest = context.HttpContext.Request;

            var uriBuilder = new UriBuilder()
            {
                Scheme = Uri.UriSchemeHttps, //originalRequest.Scheme,
                //Host = originalRequest.Host.Host,
                // Port = originalRequest.Host.Port ?? (originalRequest.Scheme == "https" ? 443 : 80),
                // Host = $"{user}.example.com",
                Host = $"{user}-svc",
                Port = 8088,
                Path = originalRequest.Path,
                Query = originalRequest.QueryString.ToString()
            };
            //var uriBuilder = new UriBuilder(context.ProxyRequest.RequestUri!)
            //{
            //    Scheme = "https",
            //    Host = $"{user}.example.com",
            //    Port = -1
            //};
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
