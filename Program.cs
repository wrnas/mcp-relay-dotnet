using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.OpenApi.Models;
using MySqlConnector;
using System.Data.Common;

namespace RelayMcp
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            //注册三种数据库的 Provider 工厂
            DbProviderFactories.RegisterFactory("Microsoft.Data.SqlClient", SqlClientFactory.Instance);
            DbProviderFactories.RegisterFactory("Microsoft.Data.Sqlite", SqliteFactory.Instance);
            DbProviderFactories.RegisterFactory("MySqlConnector", MySqlConnectorFactory.Instance);

            // 允许局域网或容器访问（
            builder.WebHost.UseUrls("http://0.0.0.0:54321");

            builder.Services.AddControllers();
            builder.Services.AddSingleton<SkillRegistry>();
            builder.Services.AddHttpClient();

            builder.Services.AddEndpointsApiExplorer();


            builder.Services.AddSwaggerGen(c =>
            {
                // 1. 定义鉴权方案
                c.AddSecurityDefinition("X-Api-Key", new OpenApiSecurityScheme
                {
                    Description = "请输入您的 API Key (例如: 你设定的密钥)",
                    Name = "X-Api-Key", // 请求头名称
                    In = ParameterLocation.Header, // 放在请求头里
                    Type = SecuritySchemeType.ApiKey, // 类型是 ApiKey
                    Scheme = "ApiKeyScheme"
                });

                // 2. 让所有接口默认应用这个鉴权方案
                c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "X-Api-Key"
                }
            },
            new string[] { }
        }
    });
            });



            var app = builder.Build();

            // 1. 先启用 Swagger
            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "RelayMcp API V1");
                c.RoutePrefix = string.Empty;
            });

            // 2. 鉴权中间件（只拦截 API 请求，放行 Swagger 相关的页面）
            app.Use(async (context, next) =>
            {
                // 如果是访问 swagger 页面或者 json，直接放行
                if (context.Request.Path.StartsWithSegments("/swagger") ||
                    context.Request.Path.Value == "/" ||
                    context.Request.Path.Value?.EndsWith(".js") == true ||
                    context.Request.Path.Value?.EndsWith(".css") == true)
                {
                    await next();
                    return;
                }

                // 校验 API Key
                if (!context.Request.Headers.TryGetValue("X-Api-Key", out var key) || key != "myMcpKey")
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsJsonAsync(ResponseHelper.Fail("未授权，请提供正确的 X-Api-Key", 401, "PERMISSION_DENIED"));
                    return;
                }
                await next();
            });

            app.MapControllers();

            app.Run();
        }
    }
}