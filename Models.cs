using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelayMcp
{
    // 对应 JSON 里的每一个 key (如 "erp_get_order")
    public class SimpleSkill
    {
        public string? Desc { get; set; }
        public Dictionary<string, string>? Params { get; set; }
        public string? Domain { get; set; }
        public string? Get { get; set; }
        public string? Post { get; set; }
        public string? Put { get; set; }
        public string? Delete { get; set; }
        public string? Soap { get; set; }
        public string? Action { get; set; }
        public string? Body { get; set; }
        /// <summary>
        /// 为接口认证保留
        /// </summary>
        public string? Auth { get; set; }
        public string? TokenKey { get; set; }

        public string? SoapMethod { get; set; }
        public string? SoapNamespace { get; set; }

        public string? SqlTemplate { get; set; } // 新增
        public string? DbAlias { get; set; }     // 新增
    }

    // 内部运行的实体类
    public class SkillDefinition
    {
        public string ToolName { get; set; } = "";
        public string? Description { get; set; }
        public string? Domain { get; set; }
        public JsonElement InputSchema { get; set; }
        public BackendConfig? Backend { get; set; }
        public bool Enabled { get; set; } = true;

        public object ToDetailDto() => new
        {
            tool_name = ToolName,
            description = Description,
            domain = Domain,
            input_schema = InputSchema,
            protocol = Backend?.Protocol
        };
    }

    public class BackendConfig
    {
        public string? Protocol { get; set; }   // 协议类型：rest 或 soap
        public string? Method { get; set; }     // HTTP 请求方法：GET/POST/PUT/DELETE（仅 REST 有效）
        public string? Url { get; set; }        // 后端接口的完整 URL
        public string? SoapAction { get; set; } // SOAP 协议的 SOAPAction 请求头
        public string? SoapBody { get; set; }   // SOAP 协议的 XML 请求体模板

        /// <summary>
        /// 认证类型。支持："bearer"（标准 Bearer Token）、"basic"（Base64 账号密码）、"header"（自定义请求头）
        /// </summary>
        public string? AuthType { get; set; }

        /// <summary>
        /// 真实的认证凭证。由 SkillRegistry 启动时根据 JSON 里的 token_key 去 appsettings.json 中读取并赋值。
        /// （例如："eyJhbGci..." 或 "admin:password123"）
        /// </summary>
        public string? Token { get; set; }

        /// <summary>
        /// 自定义 Header 的键名（仅在 AuthType 为 "header" 时生效）。
        /// （例如："X-Api-Key"、"X-Access-Token"）
        /// </summary>
        public string? TokenKey { get; set; }

        public string? SoapMethod { get; set; }    // 方法名，比如 "RemoveMaillist2013"
        public string? SoapNamespace { get; set; } // 命名空间，比如 "http://Exchange2013Maillist/"

        public string? SqlTemplate { get; set; } // 固定的 SQL 模板
        public string? DbAlias { get; set; }     // 数据库别名
    }

    public class ExecuteRequest
    {
        [JsonPropertyName("tool_name")]
        public string ToolName { get; set; } = "";

        [JsonPropertyName("args")]
        public JsonElement Args { get; set; }
    }

    public record SkillSummary(string tool_name, string? description, string? domain);

    public record ExecResult(bool Ok, object? Data, string? ErrorType, string? Message, int Code)
    {
        public static ExecResult Success(object? data) => new(true, data, null, null, 200);
        public static ExecResult Fail(string errorType, string message, int code)
            => new(false, null, errorType, message, code);
    }
}