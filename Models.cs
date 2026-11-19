using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelayMcp
{
    /// <summary>
    /// 对应 skills/xxx.json 文件里的每一个键值对（技能配置）。
    /// 这里的属性名与 JSON 里的字段名对应，用于接收配置。
    /// </summary>
    public class SimpleSkill
    {
        /// <summary>
        /// 技能的描述，供大模型理解该接口的用途（如："按订单号查询 ERP 订单详情"）。
        /// </summary>
        public string? Desc { get; set; }

        /// <summary>
        /// 技能的参数定义。键为参数名，值为 "类型 描述" 格式（如："order_id": "string 订单号"）。
        /// 前缀带 "?" 表示可选参数（如："?int 页码"）。
        /// </summary>
        public Dictionary<string, string>? Params { get; set; }

        /// <summary>
        /// 业务域（如 "erp"、"oa"）。不填则默认取 JSON 文件名。
        /// </summary>
        public string? Domain { get; set; }

        /// <summary>
        /// REST 接口配置：填写即为 GET 请求，值填完整 URL（支持 {参数名} 占位符）。
        /// </summary>
        public string? Get { get; set; }

        /// <summary>
        /// REST 接口配置：填写即为 POST 请求。
        /// </summary>
        public string? Post { get; set; }

        /// <summary>
        /// REST 接口配置：填写即为 PUT 请求。
        /// </summary>
        public string? Put { get; set; }

        /// <summary>
        /// REST 接口配置：填写即为 DELETE 请求。
        /// </summary>
        public string? Delete { get; set; }

        /// <summary>
        /// SOAP WebService 接口地址（如 "http://.../User.asmx"）。
        /// </summary>
        public string? Soap { get; set; }

        /// <summary>
        /// SOAP 请求头中的 SOAPAction（如 "http://tempuri.org/AddUser"）。
        /// </summary>
        public string? Action { get; set; }

        /// <summary>
        /// 用于复杂 SOAP 接口的全手动拼装。如果填了此项，会直接替换 {参数名} 后发送。
        /// </summary>
        public string? Body { get; set; }

        /// <summary>
        /// 鉴权类型。支持："bearer"、"basic"、"header"（需配合 token_key 使用）。
        /// </summary>
        public string? Auth { get; set; }

        /// <summary>
        /// 凭证键名。程序启动时会拿这个值去 appsettings.json 的 "Tokens" 节点下读取真实 Token。
        /// </summary>
        [JsonPropertyName("token_key")]
        public string? TokenKey { get; set; }

        /// <summary>
        /// 用于简单 SOAP 接口的半自动拼装：指定要调用的方法名（如 "AddUser"）。
        /// </summary>
        [JsonPropertyName("soap_method")]
        public string? SoapMethod { get; set; }

        /// <summary>
        /// 用于简单 SOAP 接口的半自动拼装：指定命名空间（如 "http://tempuri.org/"）。
        /// </summary>
        [JsonPropertyName("soap_namespace")]
        public string? SoapNamespace { get; set; }

        /// <summary>
        /// 固定的 SQL 模板。底层使用参数化查询防注入（如 "SELECT * FROM Users WHERE id = {id}"）。
        /// </summary>
        [JsonPropertyName("sql")]
        public string? SqlTemplate { get; set; }

        /// <summary>
        /// 目标数据库别名，需与 appsettings.json 中的 DatabaseSettings:Connections 下的键名对应。
        /// </summary>
        [JsonPropertyName("db_alias")]
        public string? DbAlias { get; set; }

        // 读写风险：read 或 write。未指定时根据协议保守推断。
        public string? Risk { get; set; }

        // 聊天确认安全级别较低，仅被明确允许的写 Skill 才可使用。
        [JsonPropertyName("chat_approval")]
        public bool ChatApproval { get; set; } = false;

        [JsonPropertyName("header_name")]
        public string? HeaderName { get; set; }

        // local 为本机 PowerShell (需安装相应命令模块)，exchange 为远程 Exchange。
        [JsonPropertyName("powershell_target")]
        public string? PowerShellTarget { get; set; }

        // 用于接收 JSON 中的 PowerShell 脚本
        public string? Script { get; set; }

        /// <summary>
        /// 技能开关。默认为 true。若设为 false，智能体将无法搜索或调用此技能。
        /// </summary>
        public bool Enabled { get; set; } = true;


    }

    /// <summary>
    /// 内部运行实体。由 SimpleSkill 经过解析、补全信息后生成，存入内存字典供实际调度使用。
    /// </summary>
    public class SkillDefinition
    {
        /// <summary>
        /// 技能唯一标识（即 JSON 文件中的 Key 名）。
        /// </summary>
        public string ToolName { get; set; } = "";

        /// <summary>
        /// 技能描述（直接取自 SimpleSkill.Desc）。
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// 所属业务域。
        /// </summary>
        public string? Domain { get; set; }

        /// <summary>
        /// 动态生成的 JSON Schema，供智能体识别参数格式。
        /// </summary>
        public JsonElement InputSchema { get; set; }

        /// <summary>
        /// 底层执行配置（包含了真实的 URL、Token、SQL 模板等）。
        /// </summary>
        public BackendConfig? Backend { get; set; }

        /// <summary>
        /// 技能启用状态。
        /// </summary>
        public bool Enabled { get; set; } = true;

        public bool IsWrite { get; set; }
        public bool ChatApprovalAllowed { get; set; }

        /// <summary>
        /// 将内部实体转化为大模型可见的“技能详情”。
        /// 注意：这里刻意隐藏了 Backend 中的真实 URL、Token 和 SQL 模板，确保安全。
        /// </summary>
        public object ToDetailDto() => new
        {
            tool_name = ToolName,
            description = Description,
            domain = Domain,
            input_schema = InputSchema,
            protocol = Backend?.Protocol,
            risk = IsWrite ? "write" : "read"
        };
    }

    /// <summary>
    /// 解析后的底层执行配置，供网关发起实际请求时使用。
    /// </summary>
    public class BackendConfig
    {
        /// <summary>
        /// 协议类型。由解析器根据配置自动识别："rest"、"soap" 或 "sql"。
        /// </summary>
        public string? Protocol { get; set; }

        /// <summary>
        /// HTTP 方法（GET/POST/PUT/DELETE），仅在 Protocol 为 "rest" 时生效。
        /// </summary>
        public string? Method { get; set; }

        /// <summary>
        /// 后端接口的完整 URL（已替换/准备好路径参数）。
        /// </summary>
        public string? Url { get; set; }

        /// <summary>
        /// SOAP 协议的 SOAPAction 请求头。
        /// </summary>
        public string? SoapAction { get; set; }

        /// <summary>
        /// 全手动写死的 SOAP XML 报文模板。
        /// </summary>
        public string? SoapBody { get; set; }

        /// <summary>
        /// 鉴权类型（对应 JSON 的 auth）。
        /// 支持："bearer"（标准 Bearer Token）、"basic"（Base64 账号密码）、"header"（自定义请求头）。
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

        /// <summary>
        /// 半自动拼装 SOAP 报文时使用的方法名（如 "RemoveUser"）。
        /// </summary>
        public string? SoapMethod { get; set; }

        /// <summary>
        /// 半自动拼装 SOAP 报文时使用的命名空间（如 "http://MyWebService/"）。
        /// </summary>
        public string? SoapNamespace { get; set; }

        /// <summary>
        /// 执行固定 SQL 时使用的模板语句。
        /// </summary>
        public string? SqlTemplate { get; set; }

        /// <summary>
        /// 执行固定 SQL 时使用的目标数据库别名。
        /// </summary>
        public string? DbAlias { get; set; }

        public string? PowerShellTarget { get; set; }

        public string? HeaderName { get; set; }

        // 用于存储 PowerShell 脚本模板
        public string? ScriptTemplate { get; set; }

        // 如果不同脚本需要在不同的服务器上执行，可以在这里加服务器地址，否则在代码里全局读配置
        public string? TargetServer { get; set; }
    }

    /// <summary>
    /// 接收来自 MCP 端 ExecuteSkill 请求的数据模型。
    /// </summary>
    public class ExecuteRequest
    {
        /// <summary>
        /// 要执行的技能名称（必须是 JSON 中配置过的 Key）。
        /// 因 MCP 侧传过来的是 snake_case，所以必须使用 JsonPropertyName 映射。
        /// </summary>
        [JsonPropertyName("tool_name")]
        public string ToolName { get; set; } = "";

        /// <summary>
        /// 大模型填写的参数对象。
        /// </summary>
        [JsonPropertyName("args")]
        public JsonElement Args { get; set; }
    }

    /// <summary>
    /// 返回给大模型的技能摘要信息（用于 GetSkillList）。
    /// </summary>
    public record SkillSummary(string tool_name, string? description, string? domain);

    /// <summary>
    /// 网关内部统一的标准执行结果模型。
    /// </summary>
    /// <param name="Ok">是否执行成功</param>
    /// <param name="Data">成功时返回的数据内容</param>
    /// <param name="ErrorType">失败时的错误类型（如 NOT_FOUND, INVALID_PARAMS, BACKEND_ERROR 等）</param>
    /// <param name="Message">失败时的具体错误提示信息</param>
    /// <param name="Code">HTTP 状态码</param>
    public record ExecResult(bool Ok, object? Data, string? ErrorType, string? Message, int Code)
    {
        /// <summary>
        /// 构造一个成功的返回结果。
        /// </summary>
        public static ExecResult Success(object? data) => new(true, data, null, null, 200);

        /// <summary>
        /// 构造一个失败的返回结果。
        /// </summary>
        public static ExecResult Fail(string errorType, string message, int code, object? data = null)
            => new(false, data, errorType, message, code);
    }
}