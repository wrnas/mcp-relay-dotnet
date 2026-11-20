using ModelContextProtocol.AspNetCore;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.OpenApi.Models;
using MySqlConnector;
using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace RelayMcp;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(
                        new WebApplicationOptions
                        {
                            Args = args,
                            ContentRootPath = AppContext.BaseDirectory
                        });
        var apiKey = builder.Configuration["Relay:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("请先设置环境变量 Relay__ApiKey，不能使用默认/硬编码凭据。");

        // 审批模式依然由配置决定，三种模式相互独立。
        var approvalMode = (builder.Configuration["Relay:ApprovalMode"] ?? "web").ToLowerInvariant();
        if (approvalMode is not ("client" or "web" or "chat"))
            throw new InvalidOperationException("Relay:ApprovalMode 只能配置 client / web / chat");

        // 默认 false：远程网页审批必须使用 HTTPS。
        // 仅在受控内网、没有 HTTPS 证书且明确接受传输风险时才设置为 true。
        // 注意：此开关只放宽网页审批的传输限制，不会取消审批密码、登录会话或 CSRF 校验。
        var allowInsecureLanApproval = builder.Configuration.GetValue<bool>("Relay:AllowInsecureLanApproval");

        if (approvalMode == "web")
        {
            var approvalBase = builder.Configuration["Relay:ApprovalPublicBaseUrl"] ?? "http://127.0.0.1:54321";
            if (!Uri.TryCreate(approvalBase, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                // 只有明确开启配置项，才允许把非本机 HTTP 地址作为审批网页入口。
                || (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback && !allowInsecureLanApproval)
                // 0.0.0.0 / :: 只能用于监听，不能作为浏览器的访问地址。
                || uri.Host == "0.0.0.0" || uri.Host == "::"
                || !string.IsNullOrEmpty(uri.UserInfo)
                || uri.AbsolutePath != "/"
                || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new InvalidOperationException(
                    "Relay:ApprovalPublicBaseUrl 必须是有效的 HTTP/HTTPS 根地址；非本机 HTTP 审批需设置 Relay:AllowInsecureLanApproval=true");
        }

        DbProviderFactories.RegisterFactory("Microsoft.Data.SqlClient", SqlClientFactory.Instance);
        DbProviderFactories.RegisterFactory("Microsoft.Data.Sqlite", SqliteFactory.Instance);
        DbProviderFactories.RegisterFactory("MySqlConnector", MySqlConnectorFactory.Instance);

        builder.Services.AddControllers();
        builder.Services.AddSingleton<ApprovalQueue>();
        builder.Services.AddSingleton<ApprovalWebSessions>();
        builder.Services.AddSingleton<LdapQueryExecutor>();
        builder.Services.AddSingleton<LdapMutationExecutor>();
        builder.Services.AddSingleton<SkillRegistry>();
        builder.Services.AddHttpClient();

        // Native MCP HTTP endpoint. Disabled only when explicitly configured as false.
        // Existing REST/Swagger endpoints and SkillRegistry remain unchanged.
        var mcpEnabled = builder.Configuration.GetValue("Mcp:Enabled", true);
        if (mcpEnabled)
        {
            builder.Services.AddMcpServer()
                .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
                .WithTools<McpTools>();
        }

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

        if (approvalMode == "web" && allowInsecureLanApproval)
        {
            // 明确在启动日志中提醒：HTTP 无法防止内网传输过程中的密码和 Cookie 窃听。
            app.Logger.LogWarning("已启用局域网 HTTP 网页审批。请通过防火墙限制访问来源；建议在具备条件时改用 HTTPS。");
        }

        app.UseSwagger();
        app.UseSwaggerUI();

        // Swagger 已在上方注册；开发环境仅额外把网站根路径重定向到 /swagger。
        // Swagger 的 Authorize 按钮仍需为实际业务 API 请求附加 X-Api-Key。
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
        }

        // 网页审批使用独立的 ApprovalKey、会话 Cookie 和 CSRF 令牌，不能用 AI 的 ApiKey 代替。
        // 默认为：HTTPS 可访问，HTTP 仅允许本机；开关启用后才额外允许私有网段客户端通过 HTTP。
        app.Use(async (context, next) =>
        {
            if (approvalMode == "web" && context.Request.Path.StartsWithSegments("/approve"))
            {
                if (!IsApprovalTransportAllowed(context, allowInsecureLanApproval))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsync("网页审批需要 HTTPS；如需在受控内网使用 HTTP，请显式开启 AllowInsecureLanApproval。");
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
        if (mcpEnabled)
        {
            // The existing X-Api-Key middleware also protects /mcp.
            app.MapMcp("/mcp");
            app.Logger.LogInformation("MCP Streamable HTTP available at /mcp");
        }
        app.Run();
    }

    /// <summary>
    /// 网页审批连接的传输协议限制（统一供中间件和审批控制器调用）。
    /// 默认只接受 HTTPS 或本机 HTTP；显式开启 AllowInsecureLanApproval 后，
    /// 还可接受来源为 RFC1918 私有 IPv4 或 IPv6 ULA 的 HTTP 请求。
    /// 重要：私有 IP 不等于可信用户，仍必须依赖独立审批密码、会话与防火墙。
    /// </summary>
    internal static bool IsApprovalTransportAllowed(HttpContext context, bool allowInsecureLanApproval)
    {
        if (context.Request.IsHttps) return true;

        var address = context.Connection.RemoteIpAddress;
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;

        return allowInsecureLanApproval && IsPrivateNetworkAddress(address);
    }

    /// <summary>
    /// 判断请求来源是否处于私有地址范围。注意此判断不替代网络 ACL / Windows 防火墙。
    /// </summary>
    private static bool IsPrivateNetworkAddress(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // fc00::/7 为 IPv6 Unique Local Addresses（ULA）。
            var bytes = address.GetAddressBytes();
            return (bytes[0] & 0xFE) == 0xFC;
        }

        return false;
    }

    internal static bool FixedTimeEquals(string? supplied, string? expected)
    {
        if (string.IsNullOrEmpty(supplied) || string.IsNullOrEmpty(expected)) return false;
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
