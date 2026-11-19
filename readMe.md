# Mcp-Relay.Net

> 面向个人运维人员的轻量级 AI 运维中转工具：连接数据库、REST、SOAP 和 PowerShell，让 AI 协助完成日常数据查询、跨系统排查以及经人工批准的操作。

**技术栈：** .NET 8 / ASP.NET Core Web API / Python 3.10+ / MCP（Model Context Protocol）  
**项目定位：** 个人运维助手，优先解决传统企业多业务系统的日常运维需求。  
**文档适用版本：** 2026-11-18 三模式审批增量补丁（基于前一版 Swagger 修复工程）。

> 本项目目前仍是个人使用阶段的原型，并非已经完成生产安全验证的企业级产品。本文按当前源码描述功能；有些能力仍在规划中，不应视为已实现。

## 目录

1. [为什么开发这个项目](#1-为什么开发这个项目)
2. [定位与典型场景](#2-定位与典型场景)
3. [整体架构与核心原理](#3-整体架构与核心原理)
4. [项目目录与运行环境](#4-项目目录与运行环境)
5. [安装、配置与启动](#5-安装配置与启动)
6. [配置文件说明](#6-配置文件说明)
7. [Skill 机制与配置规范](#7-skill-机制与配置规范)
8. [REST 接口接入](#8-rest-接口接入)
9. [SOAP WebService 接入](#9-soap-webservice-接入)
10. [SQL 查询与固定 SQL Skill](#10-sql-查询与固定-sql-skill)
11. [PowerShell 运维](#11-powershell-运维)
12. [写操作审批：三种可选模式](#12-写操作审批三种可选模式)
13. [MCP 与 HTTP 接口](#13-mcp-与-http-接口)
14. [调试与测试](#14-调试与测试)
15. [常见问题](#15-常见问题)
16. [安全边界与已知限制](#16-安全边界与已知限制)
17. [后续改进方向](#17-后续改进方向)
18. [版本迁移与验证说明](#18-版本迁移与验证说明)

---

## 1. 为什么开发这个项目

在传统企业的信息化环境中，一名运维人员往往同时负责多个业务系统：人事、邮箱、VPN、上网权限、ERP、OA 和其他历史系统。

大量日常工作不是服务器监控或标准化部署，而是：

- 根据用户的要求查询业务数据、核实状态、导出结果。
- 查询业务系统没有提供的统计或组合条件。
- 跨多个系统查找同一员工、账号、设备或业务记录。
- 查清楚某项权限何时到期、何时发生变更、由谁操作。
- 调用已有 REST/SOAP 接口、PowerShell 脚本或已审核的 SQL 模板完成处理。
- 操作完成后再次查询，验证是否真正生效。

这些系统往往已经有数据库和操作接口，但没有面向 AI 的 MCP 能力。为了接入 AI 而修改原有系统通常不现实，也没有必要。

**Mcp-Relay.Net 的目标，是尽量不修改原有业务系统，把已有查询和操作能力以统一方式提供给 AI 助手。**

它并不试图替代业务系统，也不负责独立完成复杂的 AI 推理。AI 负责理解运维人员的请求、选择工具和分析结果；网关负责执行真实操作、限制权限以及返回可检查的结果。

## 2. 定位与典型场景

### 2.1 当前定位

现阶段优先服务单个运维人员：

- 轻量、易启动、尽量少配置。
- 尽量复用已有数据库、脚本和业务接口。
- 常用查询做成固定 Skill，临时查询交给 AI 生成只读 SQL。
- 查询自动化，修改操作由运维人员审核批准。
- AI 使用已有的 MCP 客户端；本项目不内置模型和聊天界面。

**不是当前目标：** 通用工作流平台、多智能体平台、监控告警中心、可视化运维门户、多租户 SaaS。

### 2.2 场景一：员工无法访问外网

假设员工反馈：“公司系统显示我有外网权限，但现在无法访问互联网。”

可能的排查过程：

1. 查询员工身份及账号信息。
2. 查询当前上网权限、权限到期时间和状态。
3. 查询权限历史，核对是否存在删除、冻结或变更记录。
4. 如果权限仍然有效，再查询设备注册或同步状态。
5. 根据证据给出结论；如果需要重新下发权限，提出操作建议。
6. 运维人员审核后，通过已配置的写 Skill 执行。
7. 再次查询业务系统，必要时联系用户验证实际网络是否恢复。

其中不应把“出现一条删除日志”直接等同于“当前权限已被删除”。应关联员工、账号、权限记录的唯一标识，并区分当前状态与历史状态。

### 2.3 场景二：临时数据查询

例如：“查询某部门所有受限制外发的邮箱，并列出员工卡号、姓名和邮箱地址。”

- 如果存在合适的固定 SQL Skill，AI 可以直接调用。
- 如果没有，AI 可以依据**你提供给 AI 的业务表结构和关联规则**生成只读 SQL，再调用 `ExecuteSql`。
- 结果行数较多时，当前程序会截断返回，**尚不支持完整 CSV/Excel 导出**。

### 2.4 场景三：调用已有接口处理业务问题

例如业务系统中已经有“重新下发权限”的 REST/SOAP 接口，不需要重新实现该业务逻辑。

为该接口配置一个写类型 Skill，AI 可以提出执行请求；服务端创建待审批任务。可在配置中选择 **MCP 客户端交互确认、内置审批网页、聊天确认** 三种模式，具体限制见第 12 节。

---

## 3. 整体架构与核心原理

```text
运维人员
   |
   v
AI 客户端（理解问题、规划查询、分析结果）
   |
   | MCP / stdio
   v
Mcp_Server.py（轻量 MCP 适配层）
   |
   | HTTP + X-Api-Key
   v
Mcp-Relay.Net（.NET 8）
   |-- Skill 注册、搜索和参数校验
   |-- SQL 查询与结果处理
   |-- REST / SOAP / PowerShell 执行
   |-- 写操作风险识别和待审批操作队列
   |
   +--> SQL Server / MySQL / SQLite
   +--> REST API
   +--> SOAP WebService
   +--> 本机 PowerShell / 远程 Exchange
```

### 3.1 为什么分成 Python 和 .NET 两层

- `Mcp_Server.py`：负责提供 MCP 工具，并把调用转换为网关 HTTP 请求。
- `.NET`：负责 Skill 管理、参数校验、实际连接数据库或业务系统，以及写操作审批。
- `skills/*.json`：定义固定业务工具，不需要为每个新接口修改 Python MCP 代码。

对于目前支持的固定 Skill，通常增加或修改 JSON 文件即可，无需重新编译服务。对于新的执行协议或复杂业务行为，仍可能需要改动 C# 代码。

### 3.2 Skill 的两个层次

**业务层（给 AI 看）：**

- Skill 名称。
- 中文或英文用途说明（`desc`）。
- 参数的名称、类型、说明。
- 所属业务域和读写风险。

**执行层（由 .NET 保存）：**

- REST URL 与请求方式。
- SOAP 地址、SOAPAction 与 XML 模板。
- SQL 模板与数据库连接别名。
- PowerShell 脚本及目标环境。
- 内部系统鉴权配置。

AI 获取 Skill 详情时不会直接收到内部 Token、完整 SQL 模板等执行配置；但执行结果本身仍可能包含业务敏感数据，需要另外控制数据库权限和可返回字段。

### 3.3 Skill 发现是轻量关键词匹配

目前 `GetSkillList` 根据名称、描述、业务域的关键词匹配和简单评分返回结果，**尚不包含向量检索或语义搜索引擎**。

建议把业务关键词、别名、用途写进 `desc`，提高 AI 查找固定 Skill 的成功率。

---

## 4. 项目目录与运行环境

主要目录结构：

```text
newSources/
|-- RelayMcp.csproj                # .NET 8 项目
|-- Program.cs                     # 启动、Swagger、API Key 中间件
|-- Models.cs                      # Skill 与请求模型
|-- SkillRegistry.cs               # Skill 加载、搜索、校验与执行
|-- DbQueryRunner.cs               # SQL 执行、参数化、结果读取
|-- SqlSafety.cs                   # 临时只读 SQL 预检查
|-- ApprovalQueue.cs               # 内存审批任务队列
|-- ApprovalWebSessions.cs         # 网页审批会话和限速
|-- Controllers/ApprovalPageController.cs # 内置网页审批界面
|-- ResponseHelper.cs              # API 响应封装
|-- Controllers/
|   |-- SkillsController.cs        # /skills
|   |-- SqlController.cs           # /sql/execute
|   `-- ApprovalsController.cs     # /approvals
|-- Mcp_Server.py                  # Python MCP 服务
|-- approve.ps1                    # 旧版终端审批（默认禁用）
|-- appsettings.json               # 服务及连接配置
|-- Properties/launchSettings.json # VS 开发环境启动配置
|-- skills/
|   |-- AD.json                    # AD 示例 Skill（默认启用）
|   `-- test.json                  # REST/SOAP/SQL 示例（默认禁用）
|-- tests/test_offline.py          # 离线基础测试
|-- tools/smoke_api.py             # 需要已启动 .NET 的 API 冒烟测试
|-- CHANGELOG_FIX.md               # 修复及迁移记录
`-- docs/legacy-readMe.md         # 历史说明，仅供对照
```

### 4.1 环境要求

- .NET 8 SDK：编译、启动 ASP.NET Core 服务。
- Python 3.10 或更新版本：运行 MCP 适配层。
- Python 依赖：`mcp`、`httpx`。
- Visual Studio（可选）：用于 F5 调试；也可以使用命令行。
- Node.js（可选）：仅在使用 MCP Inspector 时需要。
- PowerShell/AD/Exchange 的对应模块和访问权限：仅使用相关 Skill 时需要。
- 相应数据库驱动和网络权限：根据使用的数据库与业务系统配置。

项目当前内置以下数据库 Provider：

- SQL Server：`Microsoft.Data.SqlClient`
- MySQL：`MySqlConnector`
- SQLite：`Microsoft.Data.Sqlite`

Oracle、达梦等数据库没有在当前代码中注册 Provider，不能直接当成已支持功能。

---

## 5. 安装、配置与启动

### 5.1 解压并准备配置

将项目解压到本机目录，进入含 `RelayMcp.csproj` 的项目根目录。

本修复版的 `appsettings.json` 中没有真实数据库密码或 API Key；需要你在自己的环境中配置。建议先只使用 SQLite 或测试数据库验证服务。

**请不要把真实的数据库账号、内部 IP、接口 Token 和审批密钥提交到公开仓库。**

### 5.2 启动 .NET 服务

在 PowerShell 中运行：

```powershell
cd C:\path\to\newSources
$env:Relay__ApiKey = '请替换为自己生成的高强度随机密钥'
dotnet restore
dotnet run --project RelayMcp.csproj --launch-profile Mcp-Relay.Net
```

也可以在 Visual Studio 中选择 `Mcp-Relay.Net` 启动配置后按 F5。

默认 HTTP 监听地址：

```text
http://127.0.0.1:54321
```

在 `Development` 环境下，项目默认打开：

```text
http://127.0.0.1:54321/swagger
```

访问根路径 `/` 会重定向至 `/swagger`。如果在非开发环境打开 `/swagger`，Swagger 不会启用；业务 API 仍然需要身份验证。

### 5.3 在 Swagger 中授权

1. 打开 `/swagger`。
2. 点击右上角的 `Authorize`（小锁图标）。
3. 输入 `Relay:ApiKey` 的**原始密钥值**；不要添加 `Bearer` 前缀。
4. 点击 `Authorize`，再使用 `GET /skills` 等接口调试。

Swagger 页面本身不要求 API Key；实际业务 API 必须携带：

```http
X-Api-Key: 你的API密钥
```

若直接打开 `http://127.0.0.1:54321/skills`，没有 Header 时返回 `401` 属于正常行为，不能据此判断服务启动失败。

### 5.4 启动 Python MCP

在另一个 PowerShell 终端中：

```powershell
cd C:\path\to\newSources
python -m pip install "mcp>=1.17,<2" httpx
$env:RELAY_API_KEY = '与.NET端ApiKey完全相同的密钥'
$env:RELAY_BASE = 'http://127.0.0.1:54321'
python Mcp_Server.py
```

Python MCP 端默认使用 `stdio` 通信，通常由 AI 客户端按需启动。直接在终端启动时不一定有可见的交互界面，这属于正常情况。

AI 客户端的 MCP 配置需要指定实际 Python 路径、脚本路径，以及环境变量。例如以下是**示意配置片段**，具体字段依客户端而定：

```json
{
  "command": "python",
  "args": ["C:/path/to/newSources/Mcp_Server.py"],
  "env": {
    "RELAY_API_KEY": "与.NET端相同的密钥",
    "RELAY_BASE": "http://127.0.0.1:54321"
  }
}
```

**不要把 `Relay:ApprovalKey` / `RELAY_APPROVAL_KEY` 放进 AI 客户端的环境变量。** 如果选择 `client` 模式，另需设置 `RELAY_CLIENT_CONFIRM_KEY`（专门用于 MCP 进程向 .NET 证明交互确认完成，详情见第 12 节）。这把密钥也不能放入提示词或 Skill 参数。

---

## 6. 配置文件说明

### 6.1 appsettings.json 示例

下面是结构示例。所有密码、Token、连接字符串都必须换成自己环境中的有效值。

```json
{
  "Urls": "http://127.0.0.1:54321",
  "Relay": {
    "ApiKey": "",
    "ApprovalKey": "",
    "ClientConfirmKey": "",
    "EnableWrites": false,
    "ApprovalMode": "web",
    "ApprovalPublicBaseUrl": "http://127.0.0.1:54321",
    "ChatApprovalEnabled": false,
    "EnableLegacyCliApproval": false,
    "ApprovalTtlMinutes": 5,
    "HttpTimeoutSeconds": 30
  },
  "Tokens": {
    "BUSINESS_TOKEN": ""
  },
  "DatabaseSettings": {
    "CommandTimeoutSeconds": 30,
    "MaxRows": 100,
    "Connections": {
      "APP_READ_DB": {
        "Provider": "Microsoft.Data.SqlClient",
        "ConnectionString": ""
      },
      "APP_MYSQL": {
        "Provider": "MySqlConnector",
        "ConnectionString": ""
      },
      "LOCAL_SQLITE": {
        "Provider": "Microsoft.Data.Sqlite",
        "ConnectionString": ""
      }
    }
  },
  "PowerShellSettings": {
    "ExchangeServerUrl": "",
    "Username": "",
    "Password": ""
  }
}
```

该示例是配置结构演示，项目自带 `appsettings.json` 中的数据库别名可能与示例不同。**Skill 的 `db_alias` 必须与实际 `Connections` 下的别名一致。**

### 6.2 Relay 节点

| 配置项 | 含义 | 默认/建议 |
|---|---|---|
| `ApiKey` | MCP / Swagger / HTTP 业务接口使用的 `X-Api-Key` | 必须设置强密钥 |
| `EnableWrites` | 是否允许写类型 Skill 创建待审批任务 | 默认 `false` |
| `ApprovalMode` | **三选一**：`client` / `web` / `chat` | 默认 `web` |
| `ApprovalKey` | 网页审批的独立登录密码；也供可选的旧版本机脚本使用 | 仅网页模式必须设置，不能与 ApiKey 相同 |
| `ClientConfirmKey` | `client` 模式下 MCP 进程独立向 .NET 确认的密钥 | 仅 `client` 模式必须设置 |
| `ApprovalPublicBaseUrl` | `web` 模式下返回给用户的审批页访问根地址 | 本机示例为 `http://127.0.0.1:54321` |
| `ChatApprovalEnabled` | 是否允许低安全级别聊天审批 | 默认 `false`，需显式启用 |
| `EnableLegacyCliApproval` | 是否保留旧 `approve.ps1` 的执行入口 | 默认 `false` |
| `ApprovalTtlMinutes` | 待审批任务有效期，1～60 分钟 | 默认 `5` |
| `HttpTimeoutSeconds` | 下游 REST/SOAP 请求超时，1～300 秒 | 默认 `30` |

**重要说明：**

- `EnableWrites=true` 只允许**提出**写操作，不会绕过审批。
- `ApprovalMode` 只允许填写 `client`、`web`、`chat`，一次只能选择一种；更换后应重启 .NET 与 Python MCP，并丢弃之前的待审批操作。
- `ApprovalKey` 和 `ClientConfirmKey` 都不能暴露给 AI 提示词，也不要设置为 `ApiKey` 的相同值。
- 在 `chat` 模式中，还必须设置 `ChatApprovalEnabled=true`，且单独在对应写 Skill 里配置 `"chat_approval": true`；否则该 Skill 不能执行。
- 聊天确认**无法证明消息来自真人**，不适合删除、权限提升、重要数据修改等高风险操作。完整对比见第 12 节。

### 6.3 DatabaseSettings 节点

| 配置项 | 含义 |
|---|---|
| `Connections` | 使用别名管理数据库连接 |
| `Provider` | .NET 已注册的数据库 Provider 名称 |
| `ConnectionString` | 数据库连接字符串 |
| `CommandTimeoutSeconds` | SQL 命令超时；代码限制 1～300 秒 |
| `MaxRows` | 单次结果最多返回多少行；代码限制 1～1000，默认 100 |

建议动态 SQL 使用专门的**只读、最小权限数据库账号**。查询与写入最好分别使用不同的数据库别名和权限账户。

### 6.4 Tokens 节点

供 REST/SOAP Skill 引用的内部凭据：

```json
{
  "Tokens": {
    "BUSINESS_TOKEN": ""
  }
}
```

Skill 中写入 `"token_key": "BUSINESS_TOKEN"`，网关会从 `Tokens:BUSINESS_TOKEN` 获取实际值，按 `auth` 类型附加到请求头。

### 6.5 PowerShellSettings 节点

供 `powershell_target: exchange` 使用：

- `ExchangeServerUrl`：远程 Exchange PowerShell 连接地址。
- `Username`：用于远程会话的账号。
- `Password`：对应密码。

这组设置不等于通用 WinRM 多主机管理配置；当前远程逻辑是特定的 Exchange 会话连接。

### 6.6 环境变量覆盖配置

ASP.NET Core 支持使用双下划线 `__` 映射配置层级。例如：

```powershell
$env:Relay__ApiKey = '一段随机密钥'
$env:DatabaseSettings__Connections__APP_READ_DB__ConnectionString = '你的只读连接串'
$env:Tokens__BUSINESS_TOKEN = '内部接口Token'
```

环境变量会覆盖同名的 `appsettings.json` 配置。**如果你修改 JSON 后发现没有生效，先检查系统环境变量。**

---

## 7. Skill 机制与配置规范

### 7.1 文件组织

`skills/` 下的每个 `.json` 文件可以定义多个 Skill：

```text
skills/
|-- hr.json
|-- mail.json
|-- vpn.json
|-- network.json
`-- operations.json
```

按系统或业务域拆分即可。文件名默认作为 `domain`；也可以在具体 Skill 上配置 `domain`。

网关会监听 JSON 文件的创建、修改、删除和重命名，并尝试热加载。若保存过程中 JSON 暂时不合法，运行期间会尽量保留该文件上次成功加载的配置；重启后无法解析的文件则会被跳过。

不同文件不要使用相同的 Skill 名称；重名时只保留先加载的一份并记录警告。

### 7.2 通用字段

| 字段 | 用途 |
|---|---|
| JSON 顶层键名 | Skill 的唯一名称，例如 `hr_get_staff` |
| `desc` | 告诉 AI 何时调用、返回什么信息 |
| `params` | 参数及类型描述，例如 `"code": "string 员工编号"` |
| `domain` | 业务域，可选 |
| `enabled` | 是否启用，默认 `true` |
| `risk` | `read` 或 `write`，说明操作风险 |
| `get`、`post`、`put`、`delete` | REST 执行入口，四选一 |
| `soap` | SOAP 服务地址 |
| `sql` / `db_alias` | SQL 模板及对应数据库别名 |
| `script` / `powershell_target` | PowerShell 脚本及执行目标 |
| `auth` / `token_key` / `header_name` | REST/SOAP 目标系统鉴权配置 |

每个 Skill **必须且只能包含一个执行入口**：`get`、`post`、`put`、`delete`、`soap`、`sql`、`script` 中的一个。

`protocol: "powershell"` 可以出现在旧配置中，但当前代码实际上根据 `script` 字段识别 PowerShell；`protocol` 本身不是执行入口。

### 7.3 参数类型写法

```json
{
  "params": {
    "employee_code": "string 员工编号",
    "page": "?int 页码（可选）",
    "is_active": "?bool 是否只查询有效记录",
    "amount": "?number 金额"
  }
}
```

- 不带 `?`：必填。
- 带 `?`：可选。
- 支持 `string`、`integer`/`int`、`number`、`boolean`/`bool`、`array`、`object` 等类型。
- 未定义的参数会被拒绝；传入类型不匹配会返回参数错误。
- PowerShell 参数目前只接受基本标量类型，数组和对象并不能直接传入 PowerShell 脚本。

### 7.4 risk 的默认行为

- REST GET：默认为 `read`。
- REST POST/PUT/DELETE：默认为 `write`。
- SOAP：默认为 `write`（即使是查询接口，也因为使用 POST 而保守处理）。
- PowerShell：默认为 `write`。
- SQL：由 SQL 预检查判断是否属于只读语句；非只读 SQL 强制按 `write` 处理。

如果确认某个 SOAP 查询接口或 PowerShell 查询脚本不会修改系统，可以在**自己审查过的配置**中标为 `"risk": "read"`。

**重要：** 对 REST、SOAP、PowerShell，`risk: read` 是配置人员作出的声明，不是程序自动证明该操作真的只读。写错风险标签可能导致操作绕开人工审批。

### 7.5 AI 调用 Skill 的流程

```text
GetSkillList（根据关键词查找）
    |
    v
GetSkillDetail（获取参数 Schema 和风险信息）
    |
    v
ExecuteSkill（传入名称和参数）
    |
    +-- read  --> 执行并返回结果
    |
    `-- write --> 检查模式和授权 --> 等待选定的审批方式
```

建议先让 AI 搜索现成 Skill；没有合适工具时，再根据你提供的业务元数据生成临时只读 SQL。

---

## 8. REST 接口接入

### 8.1 GET 查询接口

文件：`skills/hr.json`

```json
{
  "hr_get_staff": {
    "desc": "根据员工卡号查询员工基本信息",
    "params": {
      "staff_code": "string 员工卡号"
    },
    "get": "http://127.0.0.1:8080/api/staff/{staff_code}",
    "risk": "read"
  }
}
```

`{staff_code}` 会作为 URL 参数进行转义后替换。未在 URL 中使用的参数，对于 GET 会追加为查询字符串。

### 8.2 POST 修改接口

文件：`skills/network.json`

```json
{
  "network_resend_permission": {
    "desc": "重新下发指定员工的上网权限；执行前应先确认当前授权有效",
    "params": {
      "staff_code": "string 员工卡号"
    },
    "post": "http://127.0.0.1:8080/api/network/resend",
    "risk": "write"
  }
}
```

对于 POST/PUT/DELETE，未被 URL 占位符使用的参数会序列化为 JSON 请求体。网关不负责理解目标接口的业务事务、幂等性和副作用。

### 8.3 REST 鉴权

示例：

```json
{
  "hr_get_staff_secure": {
    "desc": "查询员工基础信息（需要内部系统Token）",
    "params": {
      "staff_code": "string 员工卡号"
    },
    "get": "http://127.0.0.1:8080/api/staff/{staff_code}",
    "auth": "bearer",
    "token_key": "BUSINESS_TOKEN",
    "risk": "read"
  }
}
```

当前支持：

- `bearer`：`Authorization: Bearer <token>`。
- `basic`：将 `Tokens` 中的 `账号:密码` 编码为 Basic Authorization。
- `header`：使用 `header_name` 指定请求头；未设置时默认 `X-Access-Token`。

**注意：** REST 配置使用的是完整 URL。应避免将任意目标 URL 当成用户参数提供给 AI；当前不是通用安全代理，也没有完整的目标地址白名单控制。

---

## 9. SOAP WebService 接入

许多历史系统提供 `.asmx` / SOAP 接口。当前实现使用 `HttpClient` 发送 SOAP XML，不依赖动态生成 WCF 客户端。

### 9.1 方式一：自动组装简单 SOAP 请求

适用于简单的字符串、数字等平铺参数：

```json
{
  "mail_get_list_info": {
    "desc": "查询邮件列表的基本信息",
    "params": {
      "list_address": "string 邮件列表地址"
    },
    "soap": "http://127.0.0.1:8022/MailService.asmx",
    "action": "http://tempuri.org/GetMailListInfo",
    "soap_method": "GetMailListInfo",
    "soap_namespace": "http://tempuri.org/",
    "risk": "read"
  }
}
```

网关会构造 SOAP 1.1 Envelope，将参数放进对应方法节点，并对参数文本做 XML 转义。

其中：

- `soap`：服务地址，不要填写 `?wsdl` 查询地址。
- `action`：HTTP 请求头中的 `SOAPAction`。
- `soap_method`：SOAP 方法名。
- `soap_namespace`：方法的 XML 命名空间。

虽然这是一个查询接口，SOAP 底层仍然通过 HTTP POST 发送；因此只有确认其语义只读后才能设置 `risk: read`。

### 9.2 方式二：使用完整 XML 模板

适用于方法参数存在固定层级的复杂 XML 请求：

```json
{
  "mail_get_nested_info": {
    "desc": "通过复杂 SOAP 对象查询邮件组信息",
    "params": {
      "list_address": "string 邮件组地址"
    },
    "soap": "http://127.0.0.1:8022/MailService.asmx",
    "action": "http://tempuri.org/GetNestedInfo",
    "body": "<soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns:tem=\"http://tempuri.org/\"><soapenv:Body><tem:GetNestedInfo><tem:request><tem:ListAddress>{list_address}</tem:ListAddress></tem:request></tem:GetNestedInfo></soapenv:Body></soapenv:Envelope>",
    "risk": "read"
  }
}
```

`body` 中的占位符会按 XML 文本内容转义替换；**不能通过参数直接注入任意 XML 子树**。复杂数组或需要原样插入 XML 片段的接口，可能需要编写专用适配逻辑。

### 9.3 SOAP 注意事项

- 当前自动封装采用 **SOAP 1.1**，请求类型为 `text/xml`。
- SOAP 1.2 的 `application/soap+xml` 行为不是当前版本的通用配置能力。
- 成功响应当前主要以 XML 字符串返回，尚未自动解析成业务对象。
- 建议先在 Postman/SoapUI 中确认 SOAPAction、命名空间、请求体、返回格式，再写入 Skill。
- SOAP 修改操作默认应为 `write`；不要通过改成 `read` 绕开审批。

---

## 10. SQL 查询与固定 SQL Skill

这是项目面向个人运维最重要的能力之一：**常用查询通过固定 Skill 复用，临时性问题由 AI 根据已知业务结构生成受控只读 SQL。**

### 10.1 支持的数据库

- SQL Server。
- MySQL。
- SQLite。

数据库连接别名配置在 `DatabaseSettings:Connections`。执行时使用别名，不把连接字符串暴露给 MCP 工具调用者。

### 10.2 动态 SQL：ExecuteSql

适用场景：没有现成 Skill 的临时查询。

例如 AI 生成：

```sql
SELECT employee_code, employee_name
FROM employee_info
WHERE department_code = 'D001';
```

调用 MCP 工具：

```json
{
  "db_alias": "APP_READ_DB",
  "sql": "SELECT employee_code, employee_name FROM employee_info WHERE department_code = 'D001'"
}
```

或通过 HTTP：

```http
POST /sql/execute
X-Api-Key: 你的API密钥
Content-Type: application/json

{"dbAlias":"APP_READ_DB","sql":"SELECT 1 AS result"}
```

**当前动态 SQL 规则：**

- 只允许经过保守预检查的单条 `SELECT` 或 `WITH ... SELECT` 查询。
- `UPDATE`、`DELETE`、`INSERT`、`EXEC`、`CALL` 等修改/执行语句不能从此入口运行。
- 查询结果最多返回 `MaxRows` 行，默认 100。
- 不支持自动导出完整 CSV/Excel。
- SQL 文本检查不是完整解析器，也不是数据库权限控制的替代品；数据库连接账号必须是只读且最小权限。

### 10.3 固定 SQL Skill

适用于频繁使用、业务条件复杂、经过人工验证的 SQL。

```json
{
  "hr_get_staff_by_code": {
    "desc": "根据员工卡号查询员工姓名和部门",
    "params": {
      "staff_code": "string 员工卡号"
    },
    "sql": "SELECT employee_code, employee_name, department_name FROM employee_info WHERE employee_code = {staff_code}",
    "db_alias": "APP_READ_DB",
    "risk": "read"
  }
}
```

调用示例：

```json
{
  "tool_name": "hr_get_staff_by_code",
  "args": {
    "staff_code": "10086"
  }
}
```

网关会把 SQL 中的 `{staff_code}` 转换成数据库参数 `@staff_code`，并使用 ADO.NET 参数绑定。**不要在占位符外额外添加单引号**：

```sql
-- 正确
WHERE employee_code = {staff_code}

-- 错误：不要在参数外加引号
WHERE employee_code = '{staff_code}'
```

SQL 模板仍需人工审查；参数化可以保护参数值，但不能证明查询的业务语义正确，也不保证索引、性能或资源消耗合理。

### 10.4 固定 SQL 写操作

当确实没有业务接口，而必须修改数据库时，应使用**预先定义并审查过的写类型 Skill**，不要让 AI 临时提交任意 UPDATE。

```json
{
  "test_update_ticket_status": {
    "desc": "测试环境：将指定工单改为给定状态，需人工审批",
    "params": {
      "ticket_id": "int 工单ID",
      "status": "string 目标状态"
    },
    "sql": "UPDATE support_ticket SET status = {status} WHERE ticket_id = {ticket_id}",
    "db_alias": "APP_WRITE_DB",
    "risk": "write",
    "enabled": false
  }
}
```

这个示例默认禁用；启用前应创建对应的数据库连接、使用最小必要权限、确认业务规则和回滚方式。即使修改只涉及一条表记录，也可能遗漏系统缓存、事件、审计或设备同步等副作用，**优先使用业务系统提供的接口**。

### 10.5 SQL 返回结果类型

常见类型：

- `empty`：没有结果行。
- `scalar`：一行一列，例如 `COUNT(*)`。
- `singleRow`：一行多列。
- `dataset`：多行或被限制截断的数据集，附带 `rowCount` 和 `truncated`。
- `affectedRows`：部分不返回结果集的数据库操作。

示例（数据集）：

```json
{
  "code": 200,
  "message": "ok",
  "data": {
    "type": "dataset",
    "rowCount": 2,
    "truncated": false,
    "data": [
      {"employee_code": "10001"},
      {"employee_code": "10002"}
    ]
  }
}
```

`rowCount` 表示**本次返回的行数**，不代表数据库中所有符合条件的记录数量。`truncated=true` 时，AI 不能把结果误认为完整名单。

### 10.6 业务表结构和 AI 业务知识

目前项目**没有实现自动发现业务表结构并向 AI 提供元数据的 MCP 工具**。

你可以继续采用当前有效的方法：

1. 把重要表、字段、JOIN 关系、业务状态码整理为 Markdown。
2. 在 AI 客户端的知识文档或提示词中提供给 AI。
3. 重要且常用的查询固化为 SQL Skill。
4. 没有匹配 Skill 的临时查询，让 AI 根据已知结构生成只读 SQL。
5. 对关键业务规则（例如“只查询最新版本记录”），优先通过受控视图或固定模板保障，而不只依靠提示词。

后续可考虑增加数据库结构按需发现，但**本文不把它描述为现有能力**。

---

## 11. PowerShell 运维

当前可通过固定脚本调用 PowerShell，主要面向本机 AD 管理模块和远程 Exchange 管理会话。

### 11.1 本机执行：AD 查询

文件：`skills/ad.json`（示意）

```json
{
  "ad_check_user": {
    "desc": "根据账号名查询 AD 用户启用状态",
    "params": {
      "username": "string AD 登录名"
    },
    "script": "Get-ADUser -Identity $username -Properties Enabled | Select-Object Name, SamAccountName, Enabled",
    "powershell_target": "local",
    "risk": "read"
  }
}
```

使用前提：

- 运行 .NET 的机器具备相应 PowerShell 模块，例如 ActiveDirectory。
- 运行服务的 Windows 身份具备相应查询权限。
- 能连接 AD 域服务。

**注意：** 包内 `skills/AD.json` 中的示例默认启用；没有 AD 环境时调用这些 Skill 可能失败。需要时可将其设为 `enabled: false`。

### 11.2 远程执行：Exchange 会话

```json
{
  "exchange_check_mailbox": {
    "desc": "查询 Exchange 邮箱基本信息",
    "params": {
      "identity": "string 邮箱地址或身份标识"
    },
    "script": "Get-Mailbox -Identity $identity | Select-Object Name, PrimarySmtpAddress, RecipientTypeDetails",
    "powershell_target": "exchange",
    "risk": "read"
  }
}
```

`exchange` 模式使用 `PowerShellSettings` 中的 `ExchangeServerUrl`、`Username` 和 `Password` 创建远程 Exchange Runspace。

它不是任意 Windows 服务器的通用远程 PowerShell 工具，也不支持在 Skill 中自由指定目标 WinRM 主机。

### 11.3 参数传递机制

修复版不再支持旧式占位符直接拼接：

```powershell
# 旧版（不要继续使用）
Get-ADUser -Identity '{username}'
```

应改为：

```powershell
# 修复版
Get-ADUser -Identity $username
```

.NET 会创建脚本参数声明，并把传入值作为参数绑定，而不是把字符串直接嵌入命令源代码。

但**参数绑定不代表任意脚本天然安全**。脚本内容必须由可信的运维人员编写、审查，并限制运行账号权限。

### 11.4 PowerShell 的安全建议

- 仅把必要的固定脚本配置为 Skill。
- 尽量只把 `Get-*` 查询脚本标记为 `read`，同时检查脚本实际调用的命令。
- 修改组成员、权限、服务状态等操作应标记为 `write` 并人工审批。
- 不要向 AI 暴露任意脚本执行入口。
- 不要将高权限密码直接写进 Skill JSON。
- 失败时先检查 WinRM、Exchange 版本、模块、运行账户和日志，不要反复重试有副作用的脚本。

---

## 12. 写操作审批：三种可选模式

所有写类型 Skill 均先创建一个**待审批操作**，绑定当次 Skill 定义与参数；审批后只执行一次，5 分钟未审批则过期（具体期限可配置）。`ExecuteSql` 临时 SQL 始终只读，不走写审批。

默认安全配置（新项目仍然**禁止写入**）：

```json
{
  "Relay": {
    "ApiKey": "请设置自己的强密钥",
    "EnableWrites": false,
    "ApprovalMode": "web",
    "ApprovalKey": "",
    "ClientConfirmKey": "",
    "ApprovalPublicBaseUrl": "http://127.0.0.1:54321",
    "ChatApprovalEnabled": false,
    "EnableLegacyCliApproval": false,
    "ApprovalTtlMinutes": 5,
    "HttpTimeoutSeconds": 30
  }
}
```

### 12.1 三种模式对比

| 模式 | 如何确认 | 需要什么 | 安全级别与限制 |
|---|---|---|---|
| `client` | AI 客户端原生交互表单（MCP Elicitation） | MCP 客户端支持交互，配置专用 `ClientConfirmKey` | 比聊天更可控，但网关仍信任 MCP 客户端/适配进程是否真实向用户展示确认 |
| `web` | 点击 AI 返回的链接，在极简网页确认/取消 | 配置 `ApprovalKey`，浏览器能访问审批页面 | **推荐**；独立网页登录、会话和 CSRF 校验，与 AI 工具调用隔离 |
| `chat` | AI 在聊天里询问，用户回复指定确认文字 | 全局开启聊天模式，并逐个 Skill 允许 | **低安全级别**；AI 能看到确认文字，网关不能证明真实用户回复 |

**请不要把这三种模式当成同等强度的授权。** 对于重要写操作，优先使用 `web`；`client` 需要可信 MCP 客户端；`chat` 只适用于你自愿承担风险、并明确允许的低影响操作。

### 12.2 模式一：AI 客户端原生确认（`client`）

.NET 端配置：

```json
{
  "Relay": {
    "EnableWrites": true,
    "ApprovalMode": "client",
    "ClientConfirmKey": "请设置随机生成的独立密钥"
  }
}
```

Python MCP 进程需要与 .NET 端一致的**专用**环境变量：

```powershell
$env:RELAY_API_KEY = '你的 ApiKey'
$env:RELAY_CLIENT_CONFIRM_KEY = '与 Relay:ClientConfirmKey 相同的独立密钥'
$env:RELAY_BASE = 'http://127.0.0.1:54321'
python Mcp_Server.py
```

如果由 AI 客户端启动 Python MCP，就把 `RELAY_CLIENT_CONFIRM_KEY` 写在该 MCP Server 的**进程环境变量**中，而不是写在对话、提示词或 Skill JSON 中。

使用过程：

1. AI 调用写类型 `ExecuteSkill`；.NET 创建待审批任务。
2. Python MCP 调用 `ctx.elicit(...)`，客户端显示包含 Skill、目标与参数的确认表单。
3. 人工明确选择同意且勾选 `confirm=true` 后，Python MCP 使用私有 `X-Client-Confirm-Key` 调用 .NET 确认接口。
4. .NET 原子化执行一次写操作，并允许 `GetApprovalStatus` 查看结果。

**兼容性限制：** 当前 `Mcp_Server.py` 采用 Python MCP SDK **v1 的 FastMCP** 和传统 `ctx.elicit` 交互请求。安装建议 `mcp>=1.17,<2`。MCP 客户端未声明支持 Elicitation、拒绝、取消或不支持这种交互时，**不会执行写操作**，会返回 `CLIENT_CONFIRM_UNAVAILABLE` / `APPROVAL_DECLINED`。不要误认为任意 MCP 客户端都能弹出按钮。MCP Python SDK v2 和新版协议的多轮交互需要另外适配，本补丁没有声称支持。

此外，某些 AI 客户端可能自行处理或自动回答交互请求；因此这一模式不是独立身份认证，使用前务必验证客户端的交互行为。

### 12.3 模式二：内置极简审批网页（`web`，推荐）

.NET 配置：

```json
{
  "Relay": {
    "EnableWrites": true,
    "ApprovalMode": "web",
    "ApprovalKey": "一段与ApiKey不同的强密码",
    "ApprovalPublicBaseUrl": "http://127.0.0.1:54321"
  }
}
```

运行过程：

1. AI 调用写 Skill；.NET 返回 `REQUIRE_APPROVAL`、`approval_id` 和 `approval_url`。
2. AI 把 `approval_url` 链接显示给你；**不应重复提交相同写 Skill**。
3. 打开该 URL，例如 `http://127.0.0.1:54321/approve/{approval_id}`。
4. 首次在网页输入独立 `ApprovalKey` 登录；登录会话最长保留约 30 分钟（内存中），之后通常只需点击确认或取消。
5. 网页显示已脱敏的操作描述、参数、截止时间，检查无误后点击“确认执行”。
6. 回到 AI 客户端，使用 `GetApprovalStatus(approval_id)` 查询 `completed`、`failed` 等状态。

网页模式不要求浏览器携带 `X-Api-Key`，而是**单独使用审批登录会话 + SameSite=Lax/HttpOnly Cookie + CSRF 令牌**。审批密钥不会发送给 AI。

**网络要求：** 默认仅面向本机 `127.0.0.1`。从另一台电脑打开审批网页，必须给 .NET 服务配置可用的 HTTPS 地址，`ApprovalPublicBaseUrl` 应填写实际可访问的 HTTPS 根地址；不要把本机 `127.0.0.1` 链接交给远程用户，也不要让网页走明文的跨主机 HTTP。使用反向代理时应正确配置受信任的 HTTPS 转发，不要信任来自公网的伪造转发头。

### 12.4 模式三：直接聊天确认（`chat`，低安全级别）

此模式用于 MCP 部署不支持原生交互，用户也无法便利打开审批网页的场合。

.NET 配置（**必须显式打开**）：

```json
{
  "Relay": {
    "EnableWrites": true,
    "ApprovalMode": "chat",
    "ChatApprovalEnabled": true
  }
}
```

对应需要允许聊天审批的低影响写 Skill，额外配置：

```json
{
  "demo_resend_notice": {
    "desc": "测试环境：重新发送通知，仅用于低影响操作",
    "post": "http://127.0.0.1:8080/api/test/resend-notice",
    "params": { "request_id": "string 测试请求编号" },
    "risk": "write",
    "chat_approval": true
  }
}
```

其他未写 `"chat_approval": true` 的写 Skill **不会**被放行。

聊天过程举例：

```text
你：帮我重新发送测试通知 10086。
AI：发现需要执行一个写操作，内容如下：
    Skill: demo_resend_notice
    Args: {"request_id":"10086"}
    请审核；如果同意，请完整回复：确认执行 123456
你：确认执行 123456
AI：调用 ConfirmChatApproval(approval_id, "确认执行 123456")。
网关：检查模式、Skill 允许标记、短语、有效期后执行一次。
```

这里的六位数字只是示例；实际由 .NET 随机生成，单次任务不同。错误确认最多允许 3 次，之后取消任务。

**安全风险必须理解：** 因为确认短语已经出现在 AI 可见的工具响应中，AI 完全可能**自行调用** `ConfirmChatApproval`。服务端无法知道消息是否真的来自你本人，不能认为这种确认具有安全防护效果。它只是适用于可信个人环境的**操作提醒与防误触流程**。不要给权限提升、删除数据、修改重要生产记录等高风险 Skill 设置 `chat_approval=true`。如果需要可验证的人工批准，请改用 `web` 模式。

### 12.5 模式共通的状态与注意事项

```text
pending  -> 等待审批
running  -> 已接受确认，正在执行
completed -> 执行成功
failed   -> 执行失败；后端可能已发生部分副作用
cancelled -> 已取消
expired  -> 已过期
```

- 同一个审批 ID **最多执行一次**；执行中的重复确认只返回当前状态，不会自动重试。
- 待审批任务与结果仅存于 .NET 进程内存，重启服务会丢失。
- 网关仅记录执行过程状态，不是持久化审计系统。
- 如果执行中遇到超时或网络异常，请先检查业务系统现状，不要直接重新触发写操作。
- 保留了旧 `approve.ps1` 管理入口作为可选应急功能，必须同时开启 `EnableLegacyCliApproval=true`，并通过本机 `X-Api-Key` + `X-Approval-Key` 验证；默认关闭。日常使用不需要运行该脚本。

---

## 13. MCP 与 HTTP 接口

### 13.1 对 AI 暴露的 6 个 MCP 工具

| MCP 工具 | 作用 |
|---|---|
| `GetSkillList(query, top_k)` | 根据关键词检索可用 Skill 摘要 |
| `GetSkillDetail(tool_name)` | 查询 Skill 参数定义、协议和风险级别 |
| `ExecuteSkill(tool_name, args)` | 执行已配置 Skill，写操作进入审批流程 |
| `ExecuteSql(db_alias, sql)` | 运行临时只读 SQL |
| `GetApprovalStatus(approval_id)` | 查看待审批/已执行任务状态 |
| `ConfirmChatApproval(approval_id, confirmation)` | **仅聊天审批模式**调用第二步确认；不能独立验证真人来源 |

**当前没有** `GetDatabaseSchema`、`SearchDatabaseSchema`、`ExportCsv` 等工具。

### 13.2 .NET HTTP 接口汇总

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/skills?query=关键字&top_k=8` | 查询 Skill 列表 |
| GET | `/skills/{name}` | 获取 Skill 详情 |
| POST | `/skills/execute` | 执行 Skill |
| POST | `/sql/execute` | 执行只读 SQL |
| GET | `/approvals/{id}/status` | 查看审批状态 |
| POST | `/approvals/{id}/confirm-client` | `client` 模式的私有确认入口；需要 `X-Client-Confirm-Key` 和安全传输 |
| POST | `/approvals/{id}/confirm-chat` | `chat` 模式的低保证确认入口（需显式启用） |
| GET | `/approve/{id}` | `web` 模式的浏览器审批页面（独立登录） |
| POST | `/approve/{id}/login` | 浏览器审批登录 |
| POST | `/approve/{id}/confirm` | 浏览器确认（会话 + CSRF） |
| POST | `/approve/{id}/cancel` | 浏览器取消（会话 + CSRF） |
| GET | `/approvals/pending` | 旧版本机审批管理员查看待处理任务 |
| POST | `/approvals/{id}/execute` | 旧版本机管理员执行；需 `EnableLegacyCliApproval=true` |
| POST | `/approvals/{id}/cancel` | 旧版本机管理员取消 |

所有 JSON 业务接口均需要 `X-Api-Key`；`confirm-client` 还需要独立 `X-Client-Confirm-Key`，远程访问必须使用 HTTPS。`/approve/{id}` 网页入口不使用业务 API Key，但会独立检查登录会话与 CSRF。旧版管理员接口需要 `X-Approval-Key` 和本机回环访问；旧版执行接口默认关闭。

### 13.3 ExecuteSkill 请求示例

```http
POST /skills/execute
X-Api-Key: 你的API密钥
Content-Type: application/json

{
  "tool_name": "hr_get_staff_by_code",
  "args": {
    "staff_code": "10086"
  }
}
```

### 13.4 统一 HTTP 响应结构

```json
{
  "code": 200,
  "message": "ok",
  "errorType": null,
  "data": {}
}
```

出现错误时，常见的 `errorType` 包括：

- `PERMISSION_DENIED`：身份验证失败或审批访问条件不满足。
- `NOT_FOUND`：Skill 或审批任务不存在。
- `INVALID_PARAMS`：参数缺失或类型不合法。
- `SECURITY_DENIED`：动态 SQL 不符合只读限制。
- `WRITE_DISABLED`：写操作未开启、缺少对应模式配置，或聊天 Skill 未单独允许。
- `REQUIRE_APPROVAL`：已创建待审批写操作，响应带审批模式和后续指引。
- `CLIENT_CONFIRM_UNAVAILABLE`：当前客户端无法提供 MCP 原生弹窗，未执行写操作。
- `APPROVAL_DECLINED`：人工取消或明确拒绝客户端原生确认。
- `BACKEND_ERROR`、`TIMEOUT`：后端错误或超时。

Python MCP 层会进一步将响应整理为 `ok`、`data`、`error_type`、`message`，便于 AI 判断是否继续排查。

---

## 14. 调试与测试

### 14.1 先验证 .NET

启动服务后访问：

```text
http://127.0.0.1:54321/swagger
```

点击 `Authorize` 输入 API Key，再依次测试：

1. `GET /skills`：检查 JSON Skill 是否正确加载。
2. `GET /skills/{name}`：查看参数 Schema 和风险级别。
3. `POST /sql/execute`：使用测试 SQLite 数据库执行 `SELECT 1 AS result`。
4. `POST /skills/execute`：验证某个真实存在的只读 Skill。

如果 `GET /skills` 返回空数组，要先确认 Skill 是否被禁用、JSON 是否合法、当前搜索关键词是否匹配。

### 14.2 用命令行验证 API Key

```powershell
$headers = @{ 'X-Api-Key' = '你的API密钥' }
Invoke-RestMethod -Uri 'http://127.0.0.1:54321/skills' -Headers $headers
```

### 14.3 使用 MCP Inspector

安装 Node.js 后，在项目目录运行：

```powershell
$env:RELAY_API_KEY = '你的API密钥'
$env:RELAY_BASE = 'http://127.0.0.1:54321'
npx -y @modelcontextprotocol/inspector python Mcp_Server.py
```

在 Inspector 中连接 Python MCP 服务，依次检查 `GetSkillList`、`GetSkillDetail`、`ExecuteSkill`、`ExecuteSql`、`GetApprovalStatus`、`ConfirmChatApproval`。

Inspector 的具体页面和端口可能随版本变化，应以命令行显示的信息为准。

### 14.4 离线测试

```powershell
python -m pip install pytest
python -m pytest tests/test_offline.py -q
```

该测试主要覆盖示例 JSON、PowerShell 占位符迁移和 Python MCP 请求格式，不等于 .NET 编译或生产联调。

### 14.5 API 冒烟测试

在启动 .NET 前，先为测试 SQLite 连接配置：

```powershell
$env:Relay__ApiKey = '你的API密钥'
$env:DatabaseSettings__Connections__LOCAL_SQLITE__ConnectionString = 'Data Source=:memory:'
dotnet run --project RelayMcp.csproj --launch-profile Mcp-Relay.Net
```

另开终端：

```powershell
$env:RELAY_API_KEY = '同一把API密钥'
python tools/smoke_api.py
```

当前冒烟脚本使用 SQLite 的内存数据库，验证简单查询、单列多行、NULL、写 SQL 拒绝等基础行为。

### 14.6 编译与测试的现实限制

当前文档对应的修复包曾通过 JSON 解析、Python 语法和离线测试检查，但**制作环境没有 .NET SDK，未完成 .NET 编译、真实数据库连接和 AD/Exchange 联调**。

正式使用前，需要在自己的开发机执行：

```powershell
dotnet restore
dotnet build
```

然后在隔离测试环境中验证自己的 REST/SOAP/SQL/PowerShell Skill。

---

## 15. 常见问题

### 15.1 启动浏览器后显示 401，是否启动失败？

不一定。如果打开的是 `/skills` 等业务接口，没有携带 `X-Api-Key`，返回 401 是预期行为。

使用开发环境的 `/swagger` 页面，在 `Authorize` 中输入 API Key 后再调试接口。

如果 `/swagger` 仍返回 401，确认运行的是包含 Swagger 修复的版本，并且 ASP.NET Core 环境是 `Development`。

### 15.2 为什么 Swagger 没有出现？

Swagger 仅在开发环境启用。确认选择了 `Properties/launchSettings.json` 中的 `Mcp-Relay.Net` 启动配置，或检查 `ASPNETCORE_ENVIRONMENT`。

请不要为了打开 Swagger 而在不受信任的公网环境启用开发模式。

### 15.3 .NET 配了 ApiKey，为什么 MCP 仍然返回 401？

Python MCP 不会自动读取 .NET 的 `appsettings.json`。必须设置与 .NET 端一致的环境变量 `RELAY_API_KEY`。

此外，如果设置了 `Relay__ApiKey` 环境变量，它会覆盖 JSON 中的值。

### 15.4 EnableWrites=false 会影响 SQL 查询吗？

不会。它只控制写类型 Skill 是否可以进入审批流程。临时只读 SQL 和正常的只读 Skill 不需要开启写操作。

### 15.5 为什么某个 SOAP/PowerShell 查询被要求审批？

因为 SOAP 和 PowerShell 默认按写操作处理。确认该 Skill 真正只读后，才可在可信 JSON 配置中声明 `risk: read`。

### 15.6 为什么 SQL Skill 的参数没有生效？

检查：

- Skill 是否配置 `sql` 和正确的 `db_alias`。
- SQL 是否用 `{参数名}`，且与 `params` 中名称完全一致。
- 是否错误地把 `{参数名}` 放在了引号中。
- 数据库连接字符串及权限是否有效。

### 15.7 为什么查到了 100 行就停止？

因为 `DatabaseSettings:MaxRows` 默认是 100，且代码最多允许设置到 1000。当前版本没有完整 CSV/Excel 导出接口。

如果需要完整报表，可先使用业务系统已有导出功能，或者在后续版本增加受控的分批导出机制。

### 15.8 PowerShell 提示命令不存在？

`powershell_target: local` 使用服务本机的 PowerShell 运行环境，需要对应模块。AD 命令需要 ActiveDirectory 模块；Exchange 命令则通常要使用正确的远程 Exchange 环境。

### 15.9 为什么修改了 Skill 没有生效？

先检查 JSON 格式与日志。配置文件无效时，运行中的服务可能继续保留上一次正确的版本。确认修改的是真正运行目录下的 `skills` 文件，而不是另一个解压目录中的副本。

### 15.10 为什么审批列表为空？

可能是：写操作被禁用、请求未成功入队、任务过期、任务已经执行、服务已重启，或查询的不是同一个 .NET 实例。新版本建议直接查看 `GetApprovalStatus`；不再需要为了日常审批使用旧的列表命令。

### 15.11 为什么客户端没有弹出确认窗口？

`client` 模式依赖 MCP 客户端对 Elicitation 的支持。当前 Python 适配器使用 SDK v1 的 `ctx.elicit`。不支持时只会返回 `CLIENT_CONFIRM_UNAVAILABLE`，**不会自动改用聊天审批，也不会执行写操作**。如客户端不支持，请在 .NET 配置中手动切换为 `web`（推荐）或谨慎选择 `chat`。

### 15.12 为什么点击审批 URL 打不开或返回 403？

本机默认地址 `127.0.0.1` 只对 .NET 所在机器有效。若浏览器位于另一台电脑，请配置可到达的 HTTPS 根地址到 `ApprovalPublicBaseUrl`，并将 .NET 服务绑定在正确的 HTTPS 监听地址。没有独立 `ApprovalKey` 时也无法登录网页。

### 15.13 为什么聊天确认被拒绝？

需要满足 `ApprovalMode=chat`、`ChatApprovalEnabled=true`、该写 Skill 中 `chat_approval=true`，同时确认文字必须与当前审批任务完全一致，且没有过期。聊天模式不能验证真人身份，请只用于低影响操作。

---

## 16. 安全边界与已知限制

### 16.1 已有保护

- API 请求需要 `X-Api-Key`。
- 默认监听本机 `127.0.0.1:54321`。
- 数据库连接和内部 Token 由 .NET 端管理，不直接作为 MCP 工具参数提供。
- 固定 SQL Skill 的值参数使用 ADO.NET 参数绑定。
- 临时 SQL 有只读关键字预检查、语句数量限制、超时和行数限制。
- 写类型 Skill 默认禁用，启用后根据 `ApprovalMode` 进入三种审批之一；网页确认使用独立密码与防跨站请求验证。
- 写任务只能执行一次，支持取消和超时。
- PowerShell 值参数不再使用简单文本插值。

### 16.2 仍需由使用者保证的安全条件

1. **数据库权限。** SQL 文本过滤不是完整语法分析，也不能证明数据库函数没有副作用；动态 SQL 必须使用数据库只读账号和最小数据访问权限。
2. **业务权限。** 能从数据库查到某些数据，不意味着用户有权查看或导出；尤其是员工个人信息、邮箱、审计记录等。
3. **Skill 审查。** `risk: read` 对 REST/SOAP/PowerShell 是人工声明，错误标记会产生真实风险。
4. **写操作的业务影响。** 直接修改数据库可能绕开业务校验、缓存更新、异步通知和审计记录；优先使用已有业务 API。
5. **网络访问。** 当前默认 HTTP 只适合可信本机环境；跨主机部署应自行提供 TLS、访问控制和网络隔离。不要直接暴露到公网。
6. **审批信任。** 网页审批密码与客户端确认密钥应独立保存。`client` 依赖客户端如实展示确认，`chat` 无法验证真人回复；高风险操作请使用独立网页。
7. **凭据保护。** 不要把真实密码、内部 URL、Token、企业数据或包含这些内容的日志提交到公开仓库。

### 16.3 当前尚未实现的能力

- 自动获取并搜索业务数据库的完整表结构、主外键及业务语义。
- 自动维护跨系统员工、账号、设备身份关联。
- CSV/Excel 大数据量文件导出与下载接口。
- 持久化的完整调查记录、审计数据库和审批队列。
- 通用 SOAP 1.2 适配器、SOAP 响应结构化解析。
- 完整的多用户权限/RBAC、租户隔离、集中管理后台。
- 通用 PowerShell 多服务器目标管理。
- 工作流编排或内置大模型 Agent。

这些属于未来可能扩展的方向，不应被理解为已包含在当前修复版源码中。

---

## 17. 后续改进方向

坚持“个人助手优先，解决真实工作问题”的原则，建议按如下顺序推进：

**第一阶段：可靠性与安全（优先）**

- 在实际 Windows/.NET 环境编译并联调全部已有 Skill。
- 为 SQL、REST、SOAP、PowerShell 补齐回归测试。
- 核查只读数据库权限、审批边界以及重复执行风险。
- 完善异常信息，避免真实凭据和敏感数据进入日志。

**第二阶段：降低日常配置工作量**

- 按需发现数据库表、字段和关联关系。
- 保留 Markdown 业务知识，记录业务状态码、关键 JOIN 和查询规则。
- 改进 Skill 检索与描述质量。
- 增加可控的 CSV 导出，避免把大量数据塞进 AI 上下文。

**第三阶段：沉淀个人运维经验**

- 允许把已经验证的临时 SQL 转换为 Skill 草稿。
- 为跨系统调查记录调用过程、证据和验证结果。
- 逐步积累常见业务问题的知识与操作约束。

暂时不优先投入大型 Web 后台、多智能体、复杂 RAG、通用工作流编辑器等功能。

---

## 18. 版本迁移与验证说明

如果你从旧 README 对应的原版项目升级，需要注意以下变化：

1. **API Key**：不再使用旧的硬编码认证值；必须配置 `Relay:ApiKey` 或 `Relay__ApiKey`。
2. **Swagger**：开发环境访问 `/swagger`，通过 `Authorize` 设置 `X-Api-Key`。
3. **MCP**：Python MCP 使用环境变量 `RELAY_API_KEY`，并对外提供 6 个工具（新增 `ConfirmChatApproval`）。
4. **临时 SQL**：只允许查询，已移除 `confirm_token` 和旧 `AllowedOperations` 的修改机制。
5. **写操作**：只能通过固定写 Skill 提出请求，并由配置指定的 `client` / `web` / `chat` 模式确认；默认 `web` 且写入关闭。
6. **PowerShell 参数**：旧 `{username}` 形式需要改成 `$username` 形式。
7. **SQL JSON 字段**：修复版支持 `sql`、`db_alias`、`soap_method`、`soap_namespace`、`token_key` 等字段映射。
8. **示例凭据**：修复包移除了原有示例密码和内部地址，迁移时必须重新配置。
9. **热加载**：增加了无效 JSON 配置的保留策略，但重启后无效文件仍会被跳过。
10. **审批改动**：新增 `ApprovalMode`、网页审批、客户端弹窗和聊天确认；原 `approve.ps1` 的执行入口默认禁用。升级后请检查新配置与 Python MCP 的依赖版本。

详细修复记录见 `CHANGELOG_FIX.md`；旧的详细使用文档保留在 `docs/legacy-readMe.md`，但其中某些安全流程和配置字段已失效。

## 最后说明

Mcp-Relay.Net 的价值不是提供最多协议，也不是替代现有运维平台，而是让**一个人维护多个传统业务系统**时，能把已有的 SQL、接口和脚本安全、便捷地交给 AI 使用。

当前最重要的目标是：**查得准确、操作可控、出错可追查、配置尽量少。**

如果在真实工作中证明这些能力可靠、有用，再考虑扩展成适合其他运维人员使用的开源工具。

---

## AD 域服务查询（LDAP/LDAPS）

Mcp-Relay.Net 可通过 **LDAPS** 使用非域计算机上的独立查询账号连接 Windows Active Directory。它作为现有 Skill 的执行方式之一，不需要在域控上安装网关，也不依赖 Exchange 的 PowerShell 连接。

**当前能力**：按登录名查询用户、按姓名搜索、查询用户直接所属组、查询组的直接成员、查询账号启用/锁定状态。全部为**只读操作**，暂不提供 AD 创建账号、修改属性、解锁或加减组；这些不应使用通用 LDAP 查询接口来实现。

### 安装和准备

1. 将 `LdapQueryExecutor.cs` 和其他补丁文件放入对应目录，项目 NuGet 依赖 `System.DirectoryServices.Protocols` 8.0.0。
2. 在域控制器上准备可用的 LDAPS（TCP 636）与受信任的服务器证书，确保网关能够解析证书上的主机名，并信任签发 CA。**不要关闭证书校验**。
3. 创建只具有必要目录读取权限的服务账号。非域场景示例使用 LDAPS + LDAP Simple Bind，账号建议填写 UPN（例如 `svc_relay@example.local`），不是域控管理员账号。
4. 把补丁里的 `AD-config.example.json` 中 `ActiveDirectory` 节点合并进你现有的 `appsettings.json`（**不要覆盖原来的 appsettings.json**），修改 Host、BaseDn、Username、AllowedAttributes。
5. 在运行 .NET 进程的环境中设置 `MCP_AD_PASSWORD` 密码变量，重启进程让新环境变量生效；切勿把密码写进 Skill 文件或提交到 Git。
6. 把 `skills/ad_ldap.json` 复制进 `skills/` 目录；启用 `ActiveDirectory:Enabled=true`，启动服务并在 Swagger 中调用 `/skills/execute`。

配置片段：

```json
{
  "ActiveDirectory": {
    "Enabled": true,
    "Servers": {
      "CORP_AD": {
        "Host": "dc01.example.local",
        "Port": 636,
        "UseSsl": true,
        "AuthType": "Basic",
        "Username": "svc_relay@example.local",
        "PasswordEnv": "MCP_AD_PASSWORD",
        "BaseDn": "DC=example,DC=local",
        "TimeoutSeconds": 15,
        "MaxResults": 100,
        "AllowedAttributes": ["cn", "sAMAccountName", "displayName", "mail", "department", "title", "userAccountControl", "msDS-User-Account-Control-Computed", "accountExpires", "pwdLastSet", "lockoutTime", "memberOf", "member"]
      }
    }
  }
}
```

PowerShell（仅当前终端进程及从此终端启动的子进程可见）：

```powershell
$env:MCP_AD_PASSWORD = '这里填AD查询账号的密码'
# 然后在同一个终端启动 dotnet run
```

Visual Studio 直接 F5 启动时，不会自动读取另一个 PowerShell 窗口临时设置的变量；需在实际启动进程的环境里设置。生产环境建议用系统凭据管理方案。

### Skill 配置与调用

LDAP Skill 使用 `ldap_server`、`ldap_filter`、`ldap_attributes`、可选 `ldap_scope`（`base`、`one`、`sub`）、可选 `ldap_base_dn`。查询过滤器中的 `{username}` 等占位符会被网关按 LDAP 过滤器语法转义，而不是直接拼接输入。`ldap_base_dn` 必须处于该服务器配置的 `BaseDn` 范围内；属性必须位于 `AllowedAttributes` 白名单。

```json
{
  "tool_name": "ad_ldap_get_user",
  "args": { "username": "zhangsan" }
}
```

通过 Swagger 的 `GET /skills`、`GET /skills/ad_ldap_get_user` 或 `POST /skills/execute` 测试；实际接口请求仍需在 Swagger `Authorize` 中填写 `X-Api-Key`。

### 返回字段与限制

- `enabled`：基于 `userAccountControl` 中的禁用位推导。
- `locked`：仅当域控返回 `msDS-User-Account-Control-Computed` 时才会返回；没有该属性时**不能认定用户未锁定**。
- `pwdLastSetUtc`、`accountExpiresUtc`、`lockoutTimeUtc`：在有效 FILETIME 情况下附带 UTC 字符串；原始目录属性仍保留。
- `memberOf` 和 `member` 返回直接成员关系，不展开嵌套组，且 `memberOf` 不表示用户的主组；AD 超大组的范围属性可能需要额外支持。
- 查询使用分页并限制最大返回对象数，`limit_reached=true` 只表示达到了本次上限，**不表示取得了全部对象**。
- 当前为仅查询执行器，没有通用 LDAP 自由搜索入口、任意 LDAP 写入或凭据输出。

### 常见连接问题

- `LDAP_CONFIG`：确认 `ActiveDirectory:Enabled`、服务器配置、环境变量密码、Skill 属性白名单。
- `LDAP_ERROR`：依次检查域控 DNS、TCP 636、证书信任链、账号 UPN/密码、账号查询权限。
- `NOT_FOUND` 或空 `entries`：先核对查询账号、BaseDn 和筛选器，再确认目录中是否存在对应对象。

**说明**：此模块基于 Windows Server 2012 AD 的常见 LDAP 行为设计。当前交付没有生产 AD 环境可用，需在测试域或只读账号下实际验证证书、身份认证以及用户/组结果。
