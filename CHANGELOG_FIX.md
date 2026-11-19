# 修复清单（个人助手版本）

## 已修改

- `Models.cs`：修复 `sql`、`db_alias`、`soap_method`、`soap_namespace`、`token_key` 的配置映射；补充 `risk`、`powershell_target`、`header_name`。
- `Controllers/SqlController.cs` + `DbQueryRunner.cs`：修复单列查询跳过第一行、单行标量越界、NULL/重复列名处理；加 `rowCount`、`truncated`、超时和参数绑定。
- `SqlSafety.cs`：临时 SQL 只允许保守判定的单条 SELECT / WITH ... SELECT；不再接受从 MCP 动态执行 UPDATE/DELETE 等。
- `SkillRegistry.cs`：统一参数校验、SQL 模板参数化执行、Skill 读写风险标记；修复热加载异常文件丢 Skill、文件监听器生命周期；检查重复名称。
- `SkillRegistry.cs`：PowerShell 改为 `param()` 与 `AddArgument()` 绑定，拒绝旧 `{参数}` 拼接；支持本机/Exchange 两种运行目标，改善输出序列化。
- `ApprovalQueue.cs` + `Controllers/ApprovalsController.cs` + `approve.ps1`：写操作单独审批；默认禁用，批准需本机独立密钥且仅能执行一次，支持状态查询与取消。
- `Program.cs` + `Mcp_Server.py`：删除硬编码 API 密钥；默认只监听回环地址；调整 MCP 工具及错误字段兼容；Swagger 仅开发环境启用且受 API Key 保护。
- `appsettings.json`：清除原示例密码与内部地址；提供空的环境变量配置入口。
- `skills/AD.json`：迁移为 `$参数` 和 `powershell_target: local`，显式标为只读。
- `skills/test.json`：所有演示 Skill 默认禁用；必须修改地址并验证后才能启用。
- `tools/smoke_api.py`、`tests/test_offline.py`：增加本地 API 冒烟测试和无需 .NET 的基础验证。

## 重要不兼容变更

1. `ExecuteSql` **只允许临时查询**，删除 `confirm_token`；数据库写操作请放入固定 SQL Skill 并审批。
2. `AllowedOperations` 已不再使用；写操作通过 `Relay:EnableWrites` + 本机独立审批控制。
3. Python MCP 需要设置环境变量 `RELAY_API_KEY`；.NET 服务需要 `Relay__ApiKey`。不能继续使用硬编码的 `myMcpKey`。
4. PowerShell 自定义 Skill 必须将 `{username}` 等替换成 `$username` 等参数变量；默认仍使用 exchange，示例 AD 改为 local。
5. SOAP、REST POST/PUT/DELETE、PowerShell 未标明风险时默认进入写操作审批。只在确认真正只读后标记 `risk: read`。
6. 配置中移除了演示连接串、Token 和密码；请在你自己的受保护环境中恢复合法配置，不要将企业真实密钥放入 ZIP 或提交到 Git。

## 验证范围

- 已通过：Python 语法检查、所有 JSON 文件解析、离线 pytest 基础测试、源码括号配对检查。
- 未验证：`.NET 8` 编译、真实 SQL Server/MySQL/AD/Exchange/REST/SOAP 联调。制作环境没有安装 .NET SDK，也无法联网下载。
- 上生产前必须：`dotnet build`、启动服务并执行 `python tools/smoke_api.py`、在隔离测试环境审查写操作审批和所有现有业务 Skill。

## 本轮未新增的功能

数据库结构自动发现、CSV/Excel 导出和调查记录，仍作为后续可选增强，本轮只修复现有核心功能。

## 2026-11-18：Swagger 调试体验修正

- `Program.cs`：`Development` 环境中的 Swagger 静态 UI 与 JSON 在业务 API 认证之前提供，Swagger 页面本身不再返回 401；接口仍需 `X-Api-Key`。
- `GET /`：开发环境下跳转至 `/swagger`。
- 新增 `Properties/launchSettings.json`：Visual Studio / `dotnet run` 启用 Development 并以 Swagger 为启动页。
- README 补充 `Relay` 五项配置解释与 Swagger 授权步骤。
