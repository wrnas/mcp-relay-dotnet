using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.OpenApi.Models;
using MySqlConnector;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;

namespace RelayMcp;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var apiKey = builder.Configuration["Relay:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("请先设置环境变量 Relay__ApiKey，不能使用默认/硬编码凭据。");

        // Validate the configured approval mode. Credentials remain separate across modes.
        var approvalMode = (builder.Configuration["Relay:ApprovalMode"] ?? "web").ToLowerInvariant();
        if (approvalMode is not ("client" or "web" or "chat"))
            throw new InvalidOperationException("Relay:ApprovalMode 只能配置 client / web / chat");
        if (approvalMode == "web")
        {
            var approvalBase = builder.Configuration["Relay:ApprovalPublicBaseUrl"] ?? "http://127.0.0.1:54321";
            if (!Uri.TryCreate(approvalBase, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
                || uri.AbsolutePath != "/"
                || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new InvalidOperationException("Relay:ApprovalPublicBaseUrl 必须是本机 HTTP 或 HTTPS 的根地址");
        }

        DbProviderFactories.RegisterFactory("Microsoft.Data.SqlClient", SqlClientFactory.Instance);
        DbProviderFactories.RegisterFactory("Microsoft.Data.Sqlite", SqliteFactory.Instance);
        DbProviderFactories.RegisterFactory("MySqlConnector", MySqlConnectorFactory.Instance);

        builder.Services.AddControllers();
        builder.Services.AddSingleton<ApprovalQueue>();
        builder.Services.AddSingleton<ApprovalWebSessions>();
        builder.Services.AddSingleton<LdapQueryExecutor>();
        builder.Services.AddSingleton<SkillRegistry>();
        builder.Services.AddHttpClient();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(c =>
        {
            c.AddSecurityDefinition("X-Api-Key", new OpenApiSecurityScheme
            {
                Description = "Relay__ApiKey 环境变量提供的 API Key",
                Name = "X-Api-Key",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.ApiKey
            });
            c.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference
                        {
                            Type = ReferenceType.SecurityScheme, Id = "X-Api-Key"
                        }
                    }, Array.Empty<string>()
                }
            });
        });

        var app = builder.Build();

        // Serve the Swagger UI before API authentication in Development only.
        // Swagger's Authorize button sends X-Api-Key on subsequent API calls.
        if (app.Environment.IsDevelopment())
        {
            app.Use(async (context, next) =>
            {
                if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path == "/")
                {
                    context.Response.Redirect("/swagger");
                    return;
                }
                await next();
            });
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        // Web approval has its OWN login session and CSRF protection. It never uses
        // an AI-facing ApiKey, and remote browsers must connect with HTTPS.
        app.Use(async (context, next) =>
        {
            if (approvalMode == "web" && context.Request.Path.StartsWithSegments("/approve"))
            {
                var ip = context.Connection.RemoteIpAddress;
                if (!context.Request.IsHttps && (ip is null || !System.Net.IPAddress.IsLoopback(ip)))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsync("Remote approvals require HTTPS.");
                    return;
                }
                await next();
                return;
            }
            var supplied = context.Request.Headers["X-Api-Key"].ToString();
            if (!FixedTimeEquals(supplied, apiKey))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(ResponseHelper.Fail("未授权", 401, "PERMISSION_DENIED"));
                return;
            }
            await next();
        });

        app.MapControllers();
        app.Run();
    }

    internal static bool FixedTimeEquals(string? supplied, string? expected)
    {
        if (string.IsNullOrEmpty(supplied) || string.IsNullOrEmpty(expected)) return false;
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
