# Active Directory（AD）接入与 Skill 配置说明

> 本节记录 Mcp-Relay.Net 的 AD 接入调试、当前能力及 Skill 编写规范，适用于负责维护本项目的运维人员和开发人员。以下配置和返回值均为示例；正式部署请使用实际 AD 域信息和经授权的账号。

## 1. 本次开发与调试总结

Mcp-Relay.Net 通过 .NET LDAP 执行器访问 Active Directory，向 MCP/AI 客户端提供可发现、可调用的 AD 工具。设计原则是：**基础操作写成 Skill，AI 可以组合基础操作完成日常任务，少量高频复合操作由程序直接实现。** 不引入额外的通用工作流引擎；写操作继续复用网关已有的开关和审批机制。

本次完成或确认的事项：

1. **网关启动与接口调用正常。** `RelayMcp.exe` 能运行，Swagger 可访问；`POST /skills/execute` 要求 `args` 是 JSON 对象，不能传入一个包含 JSON 内容的字符串。
2. **定位了 LDAPS 连接故障。** 原网关使用的 DNS 不能解析 AD 域控名称，改用本机名称映射后，域控 FQDN 可以解析且 TCP 636 连通；进一步从 Windows Schannel 事件 `36882` 确认，TLS 失败涉及客户端不信任服务器证书的 CA。仅端口连通不代表 TLS 握手成功。
3. **只读 AD 查询已经实际成功。** 采用 `AllowUntrustedCertificate=true` 作为测试期间的临时设置后，`ad_ldap_get_user_groups` 通过 Swagger 返回 HTTP 200，成功取得用户以及其 `memberOf` 中的直接所属组 DN。这是已验证的结果。
4. **完善读写代码结构。** 只读查询由 `LdapQueryExecutor.cs` 处理；新补丁增加 `LdapMutationExecutor.cs`，提供 18 个固定 AD 写入动作，包括用户、组、OU、计算机的常用创建、修改、删除和成员管理。
5. **简化配置策略。** 删除 `DeniedAttributes` 配置及相应查询拦截逻辑；增加每个 AD 服务器独立的 `DisabledOperations`，按操作名或 Skill 名禁用操作。查询字段仍由每个 Skill 的 `ldap_attributes` 明确声明，实际读取权限由 AD 连接账号决定。
6. **复用原有写入审批机制。** `Relay:EnableWrites` 是全局写入开关；`Relay:ApprovalMode` 继续支持 `web`、`client`、`chat` 三种已有模式，不另建审批系统。
7. **统一文件管理方向。** 约定最终由一个 `skills/ad_ldap.json` 统一管理 AD 的读写 Skill。**注意：上一版补丁包仍提供单独的 `skills/ad_write.json`，两类 Skill 尚未实际合并成一个文件并验证；可以将其 JSON 顶层条目合并，且必须保证名称不重复。**

**验证状态：** 已在真实 AD 环境中验证只读查询成功；新 AD 写入补丁已完成静态检查，但尚未在当前环境完成 `dotnet build`、真实 AD 写入测试。不要把“补丁已生成”理解成“全部写操作已验证”。

## 2. AD Skill 的基本机制

AD Skill 定义在项目的 `skills/*.json` 文件中。每个文件的最外层是一个 JSON 对象，**每个键是一个唯一的工具名称**，每个值描述该工具的用途、参数和执行方式。项目可将所有 AD Skill 统一放入 `skills/ad_ldap.json`。

网关调用流程如下：

```text
用户自然语言请求
    ↓
AI 查找 Skill（GetSkillList / GetSkillDetail）
    ↓
AI 生成 tool_name + args
    ↓
POST /skills/execute
    ↓
SkillRegistry 加载定义、校验参数、检查 DisabledOperations
    ├─ risk=read  → LdapQueryExecutor → LDAP 查询 → 返回结果
    └─ risk=write → EnableWrites / 既有审批 → LdapMutationExecutor → LDAP 修改 → 返回结果
```

同一个 JSON 文件中可以同时存在 `risk: "read"` 和 `risk: "write"` 的 Skill。**JSON 是否分文件，与底层 C# 执行器是否分开是两回事。**

### 2.1 `appsettings.json` 与 Skill JSON 的分工

`appsettings.json` 存放服务器连接方式、账号、连接参数和操作禁用列表；`skills/ad_ldap.json` 存放具体工具定义。Skill 通过 `ldap_server` 的别名引用连接配置，不把服务器密码重复写入每个 Skill。

例如，下面仅是 `appsettings.json` 的 AD 节点示例：

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
        "Password": "请填写实际密码",
        "BaseDn": "DC=example,DC=local",
        "TimeoutSeconds": 15,
        "MaxResults": 100,
        "AllowUntrustedCertificate": false,
        "DisabledOperations": []
      }
    }
  }
}
```

- `Enabled`：总 AD 功能开关。
- `Servers.CORP_AD`：服务器别名，与 Skill 的 `ldap_server` 对应。
- `Host`、`Port`、`UseSsl`：LDAP 目标地址及 LDAPS 连接设置。
- `AuthType`、`Username`、`Password`：连接账号；也可使用 `PasswordEnv` 指定环境变量。**如果设置了 `PasswordEnv`，代码优先读取环境变量而不是回退到 `Password`。**
- `BaseDn`：默认的 LDAP 搜索基准 DN。
> PowerShell（仅当前终端进程及从此终端启动的子进程可见）：
```powershell
$env:MCP_AD_PASSWORD = '这里填AD查询账号的密码'
# 然后在同一个终端启动 dotnet run
```
> Visual Studio 直接 F5 启动时，不会自动读取另一个 PowerShell 窗口临时设置的变量；需在实际启动进程的环境里设置。生产环境建议用系统凭据管理方案。


- `TimeoutSeconds`、`MaxResults`：查询超时及结果数量限制。
- `AllowUntrustedCertificate`：设置为 `true` 会跳过服务器证书验证，适合临时定位问题；实际生产读写建议使用受信任证书并设置为 `false`。
- `DisabledOperations`：禁用指定 AD 操作或 Skill。空数组代表不额外禁用已定义的操作；并不意味着绕过 `EnableWrites`、审批或 AD 账号权限。

## 3. 只读 Skill 详细示例：查询用户直接所属组

以下是实际采用的 `ad_ldap_get_user_groups` 定义：

```json
"ad_ldap_get_user_groups": {
  "desc": "根据登录账号查询AD用户直接所属组的DN（不包含嵌套组或主组）",
  "domain": "active-directory",
  "params": {
    "username": "string AD登录账号"
  },
  "ldap_server": "CORP_AD",
  "ldap_filter": "(&(objectCategory=person)(objectClass=user)(sAMAccountName={username}))",
  "ldap_attributes": [
    "sAMAccountName",
    "displayName",
    "memberOf"
  ],
  "risk": "read"
}
```

**提示：** 这是 `skills/ad_ldap.json` 内的一个条目，不是完整的 JSON 文件。放入完整文件时，应由最外层 `{ ... }` 包裹；相邻 Skill 条目之间用逗号分隔。

### 3.1 逐字段解释

| 字段 | 含义 | 在本例中的作用 |
|---|---|---|
| `ad_ldap_get_user_groups` | **Skill 唯一名称**，也是调用时的 `tool_name` | AI 或 Swagger 通过该名称选择工具 |
| `desc` | 面向 AI 和运维人员的中文用途说明 | 明确只返回**直接**所属组的 DN，不保证完整权限组列表 |
| `domain` | 工具所属业务域，便于搜索和分类 | `active-directory` 表示 AD 业务域 |
| `params` | 输入参数名称、类型及描述 | 声明必填参数 `username`，类型为 `string` |
| `ldap_server` | AD 连接配置的别名 | 读取 `ActiveDirectory:Servers:CORP_AD` 下的连接信息 |
| `ldap_filter` | LDAP 搜索过滤条件 | 精确查找 `sAMAccountName` 与输入用户名相同的用户对象 |
| `ldap_attributes` | 要从匹配对象读取的 LDAP 属性 | 本例读取登录名、显示名、直接所属组列表 |
| `risk` | 此 Skill 的读写类型 | `read` 直接执行查询，不进入写入审批流程 |

### 3.2 `params` 如何定义用户输入？

```json
"params": {
  "username": "string AD登录账号"
}
```

左侧的 `username` 是**参数名**；右侧的 `string` 是参数类型，后面的“AD登录账号”是参数描述。此写法表示参数**必填**。`SkillRegistry` 会将其转为 JSON Schema，并检查实际调用是否携带正确类型。

例如使用 Swagger：

```json
{
  "tool_name": "ad_ldap_get_user_groups",
  "args": {
    "username": "test.user"
  }
}
```

`args` **必须是 JSON 对象**，不能写成下面这种字符串：

```json
{
  "tool_name": "ad_ldap_get_user_groups",
  "args": "{\"username\":\"test.user\"}"
}
```

在本项目的参数定义规则中，`"?string 描述"` 表示可选字符串；没有 `?` 的参数默认必填。

### 3.3 `ldap_filter` 是如何匹配用户的？

```text
(&(objectCategory=person)(objectClass=user)(sAMAccountName={username}))
```

表达式中的：

- 外层 `(& ... )` 表示多个条件必须**同时成立**（逻辑 AND）。
- `(objectCategory=person)` 表示对象类别为人员。
- `(objectClass=user)` 表示对象属于用户类。
- `(sAMAccountName={username})` 表示通过用户的 AD 登录名精确匹配。
- `{username}` 是 Skill 参数占位符，不是字面上的 AD 属性值。

当调用传入 `"username": "test.user"` 时，执行器会先对参数值进行 LDAP 过滤器转义，再生成类似如下条件：

```text
(&(objectCategory=person)(objectClass=user)(sAMAccountName=test.user))
```

该查询会以 `BaseDn`（或 Skill 可选的 `ldap_base_dn`）作为搜索起点。未设置 `ldap_scope` 时，当前执行器默认使用 `sub`，即**搜索基准 DN 下的所有层级**。

常见的 `ldap_scope` 值：`base`（仅当前对象）、`one`（仅直属子对象）、`sub`（当前对象及所有后代对象）。

### 3.4 `ldap_attributes` 返回什么？

```json
"ldap_attributes": [
  "sAMAccountName",
  "displayName",
  "memberOf"
]
```

| 属性 | 说明 | 示例 |
|---|---|---|
| `sAMAccountName` | 用户登录名 | `test.user` |
| `displayName` | AD 显示名称 | `Test User` |
| `memberOf` | 用户直接所属组的**完整 DN 列表** | `CN=M-Mail,CN=Users,DC=example,DC=local` |

现有查询执行器还会在允许的情况下统一返回匹配对象的 `distinguishedName`。注意：LDAP 只会返回对象实际具备、且连接账号有权读取的属性；请求了某个字段，不代表结果中一定存在该字段。

示意结果：

```json
{
  "server": "CORP_AD",
  "returned": 1,
  "entries": [
    {
      "distinguishedName": "CN=test.user,OU=IT,DC=example,DC=local",
      "sAMAccountName": "test.user",
      "displayName": "Test User",
      "memberOf": [
        "CN=M-Mail,CN=Users,DC=example,DC=local",
        "CN=A-Desktop,CN=Users,DC=example,DC=local"
      ]
    }
  ]
}
```

`CN=M-Mail,CN=Users,DC=example,DC=local` 是一个**组的 DN**：`CN=M-Mail` 是组名，后面部分是该组在 AD 中的位置。它不是完整的组详情。如果还想知道组的描述、组作用域、组类型、成员数量等，需要再查询**组对象**，不能只读取用户的 `memberOf`。

尤其需要理解：

- **直接成员组**：`memberOf` 可以反映用户对象的普通直接组成员关系。
- **嵌套组**：用户属于 A 组，A 组再属于 B 组；`memberOf` 不会自动展开完整嵌套链。
- **主组（Primary Group）**：通常不会体现在 `memberOf` 中，例如常见的 `Domain Users`。因此这个 Skill 的结果**不能直接称为“用户的全部组权限”**。
- **实际访问权限**：还可能取决于组嵌套、ACL、用户令牌等，不能仅凭 `memberOf` 判断全部有效权限。

需要更完整的成员关系时，应通过专门的组查询和嵌套成员关系 Skill 处理，而不是简单地扩充这一条 `ldap_attributes` 列表。

## 4. 写操作 Skill 如何定义？

写操作不使用 `ldap_filter`，而通过 `ldap_action` 指定程序已经实现的固定动作。例如“若不在指定组，则加入”：

```json
"ad_ensure_user_in_group": {
  "desc": "检查用户是否已在指定组，不在则加入（直接成员）",
  "domain": "active-directory",
  "params": {
    "username": "string AD登录名",
    "group": "string 组sAMAccountName、CN或完整DN"
  },
  "ldap_server": "CORP_AD",
  "ldap_action": "ensure_user_in_group",
  "risk": "write"
}
```

此配置与只读示例的主要区别：

| 项目 | 只读 Skill | 写入 Skill |
|---|---|---|
| 执行入口 | `ldap_filter` | `ldap_action` |
| 执行器 | `LdapQueryExecutor` | `LdapMutationExecutor` |
| 常见用途 | 查找用户/组/计算机 | 创建、修改、删除、移动、加组等 |
| 风险标识 | `risk: "read"` | `risk: "write"` |
| 调用处理 | 参数校验后直接查询 | 检查写入开关和禁用列表，经过现有审批流程后执行 |

**同一个 Skill 只能选一个执行入口**，不能同时定义 `ldap_filter` 和 `ldap_action`。当前补丁要求 `ldap_action` 类型必须显式声明 `risk: "write"`。

`ensure_user_in_group` 的预期行为：查找唯一用户与目标组 → 检查是否已经是目标组**直接成员** → 已是成员返回 `already_member`（无修改） → 否则执行添加并返回 `added`。它不会仅因为用户通过嵌套组间接属于目标组就认为存在直接成员关系。

## 5. `DisabledOperations` 控制哪些操作？

在 `appsettings.json` 的具体服务器配置中：

```json
"DisabledOperations": [
  "delete_user",
  "delete_group",
  "reset_user_password"
]
```

可以写底层的 `ldap_action`（例如 `delete_user`），也可以写完整 Skill 名（例如 `ad_delete_user`）。如果要禁用一个只读 Skill，可以写其完整名称，例如 `ad_ldap_get_user_groups`。此策略只影响 AD 的 `ldap` / `ldap_write` Skill，不影响其他 REST、SQL、SOAP 和 PowerShell 工具。

`DisabledOperations: []` 表示不额外禁用，但**不代表无条件可执行**：工具仍须存在且启用、参数合法，写入还受 `Relay:EnableWrites`、审批配置和 AD 账号自身权限约束。

此前的 `DeniedAttributes` 用于阻止读取某些 LDAP 属性，不是禁用“操作”。在本次简化补丁中，它与内置读取属性黑名单均已删除，配置文件中不再保留该字段。现在查询能请求哪些属性主要由 Skill 的 `ldap_attributes` 定义，能否实际取得值由 AD 账号权限决定。

## 6. 复合操作的约定

采用**基础 Skill + AI 自行组合 + 少量常用复合 Skill** 的方式：

- 临时任务交由 AI 按结果组合已有基础 Skill，例如先查用户、查组、再判断是否需要调用添加或移除工具。
- “确保用户属于组”“确保用户不属于组”“确保用户位于某个 OU”等高频且重复性强的动作，可以由执行器实现单个复合 Skill，减少多次工具调用和重复写入。
- **加到组**与**移动到 OU**是不同动作：前者修改组成员关系，后者修改用户对象的目录位置；修改用户 `department` 属性又是另一种独立操作。
- 不开发通用工作流引擎，也不为每一种业务场景单独编写大型 Skill。

## 7. 当前代码文件职责与上线前检查

| 文件 | 职责 |
|---|---|
| `Program.cs` | 应用启动、依赖注册、API Key 中间件 |
| `Models.cs` | Skill 配置模型与请求/返回模型 |
| `SkillRegistry.cs` | Skill 加载、校验、分流、`DisabledOperations` 检查与已有写入审批入口 |
| `LdapQueryExecutor.cs` | 只读 LDAP 搜索 |
| `LdapMutationExecutor.cs` | 固定 AD 写入动作，包括常见的复合操作 |
| `SkillsController.cs` | 对外提供 `/skills`、`/skills/{name}`、`/skills/execute` |
| `skills/ad_ldap.json` | **约定的最终统一配置文件**；集合 AD 读写 Skill |

新增或修改 Skill 后，检查 JSON 合法性、名称唯一性、参数名与占位符/动作要求是否一致。Skills 目录支持热加载，但修改 C# 执行器后仍需重新编译、部署并重启。

建议依次验证：

1. 执行 `dotnet build`，确认补丁能够编译。
2. 在 Swagger 使用 `GET /skills` 和 `GET /skills/{name}` 验证 Skill 加载及参数定义。
3. 先测试只读 `ad_ldap_get_user_groups`。
4. 再用**测试用户和测试组**验证 `ad_ensure_user_in_group` 的 `added` / `already_member` 行为及审批流程。
5. 将 `ensure_user_in_group` 加入 `DisabledOperations`，检查是否返回 `AD_OPERATION_DISABLED`，再恢复配置。

目前可以确定的是**只读组查询在实际 AD 上已返回成功**；其余新写入功能仍需按上述步骤在目标环境完成编译和实测。
