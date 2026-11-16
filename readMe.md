# 🌐 Mcp-Relay.Net

> **打通大模型与内部老系统的“最后一公里”** —— 一个基于 .NET 8 的、极其轻量的、配置驱动的 MCP (Model Context Protocol) 网关。

[![.NET 8](https://img.shields.io/badge/.NET-8.0-blue.svg)](https://dotnet.microsoft.com/)
[![Python 3.10+](https://img.shields.io/badge/Python-3.10%2B-yellow.svg)](https://www.python.org/)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](https://opensource.org/licenses/MIT)

## 💡 为什么需要它？

在企业内部接入 AI 智能体时，你往往会面临三大痛点：
1. **接口繁杂**：有标准的 REST (Java/Spring)，有老掉牙的 SOAP (WebService)，还有各种异构数据库。
2. **发版困难**：每加一个内部接口，如果都要重新发布 MCP 服务，运维成本极高。
3. **安全风险**：绝对不能让大模型直接拿着数据库账号密码和内部 Token 裸奔。

**Mcp-Relay.Net** 采用 `智能体 -> MCP (Python) -> 网关 (.NET 8) -> 内部系统` 的分层架构，完美解决了这些问题。**你只需修改 JSON 配置，即可实现“5 分钟接入一个新接口，且无需重启服务”。**

## ✨ 核心特性

*   **🚀 配置驱动，动态热加载**：按业务域编写 `.json` 技能配置，支持 `FileSystemWatcher` 热加载。
*   **🔌 多协议全能转换**：轻松兼容 Java REST、老式 SOAP (支持半自动 XML 拼装)、以及 SQLite / MySQL / SQL Server 数据库。
*   **🛡️ 企业级安全网关**：外层 `X-Api-Key` 校验，内层隐藏内部真实 Token；数据库写操作（UPDATE/DELETE/EXEC）强制“二次确认”拦截。
*   **🧠 大模型友好**：内置渐进式发现（List -> Detail -> Execute），并且将底层数据清洗为最纯净的 JSON 格式返回。

# 📚 内部智能体工具网关 (MCP + .NET) 架构备忘录

> **数据流向：** 智能体平台 -> Python MCP (协议翻译) -> .NET 中转服务 (大脑与路由) -> 内部业务系统 (REST/SOAP)

> **核心优势：** 以后无论增加多少内部接口，**Python MCP 代码永远不需要重新发布**，只需要修改 `.NET` 端的 JSON 配置即可。同时实现了安全隔离，智能体接触不到内部系统的真实 Token。

---

## 二、 三大核心组件（以后去哪改代码？）

### 1. Python MCP 层（`Mcp_Server.py`）

- **定位**：薄适配层，只负责把 MCP 协议翻译成 HTTP 请求，发给 .NET。
- **暴露给智能体的 3 个工具**：
  - `GetSkillList(query, top_k)`：搜索有哪些工具（只返回摘要，防止撑爆上下文）。
  - `GetSkillDetail(tool_name)`：获取某个工具的参数 Schema 说明。
  - `ExecuteSkill(tool_name, args)`：执行该工具。
- **注意**：如果没有特殊需求，**这个文件以后可以封存不用动了**。唯一需要确认的是 `RELAY_TOKEN` 和 `.NET` 的鉴权暗号是否一致。

### 2. .NET 中转层（`RelayMcp` 项目）

- **定位**：真正的执行大脑，负责查表、校验参数、注入 Token 并转发请求。
- **路由约定**（对应 `SkillsController.cs`）：
  - `GET /skills` -> 查列表
  - `GET /skills/{name}` -> 查详情
  - `POST /skills/execute` -> 执行
- **鉴权隔离（双重验证）**：
  - **第一层（MCP -> .NET）**：在 `Program.cs` 中间件里校验 `X-Api-Key`，防止网关被外部随便调用。
  - **第二层（.NET -> 业务系统）**：在 `appsettings.json` 里存真实 Token，调用时由 `InjectAuthHeaders` 动态附加到请求头。

### 3. 技能注册表（`.json` 文件）

- **定位**：所有接口的“花名册”。
- **存放位置**：项目根目录的 `skills` 文件夹下（建议按业务域分文件，如 `erp.json`、`oa.json`）。
- **热加载**：已实现 `FileSystemWatcher`，修改 JSON 文件后，**不用重启 .NET 服务**，系统会自动重新加载。

---

## 三、 ⚡ 日常操作：以后怎么加一个新接口？

以后要接入一个新的业务系统接口，**只需要 3 步，全程不用改 C# 和 Python 代码**：

### 第一步：在 `skills/` 目录下找到对应的 JSON（或新建），加一条配置：

````json
{
  "新技能的名称_例如_hr_get_user": {
    "desc": "一句话描述，供大模型搜索时看懂",
    "params": {
      "user_id": "string 用户工号",
      "page": "?int 页码(可选)"
    },
    "get": "http://内部hr系统IP/api/users/{user_id}",
    "auth": "bearer",
    "token_key": "HR_TOKEN"
  }
}

````
### 第二步：如果是新的业务系统，去 appsettings.json 补 Token：

```json
"Tokens": {
  "HR_TOKEN": "eyJhbGciOi...真实的Token"
}

````
### 第三步：去智能体平台/Inspector 测试：
调一下 GetSkillList 和 ExecuteSkill，测试能否调用成功。


## 🛠️ MCP 调试指南与工具
调试分为两部分：先测通 .NET，再测通 MCP。

### 1. 调试 .NET 中转服务（使用内置 Swagger）
工具：浏览器（无需额外安装）。
启动：按 F5 运行 .NET 项目，保持黑窗口不关。
访问：浏览器打开
http://localhost:50000。
- 操作：
展开 GET /skills，点击 Try it out，直接点 Execute。看下方是否返回了 JSON 数据（如果返回 []，说明没读到 JSON 文件或搜索词不对）。
展开 POST /skills/execute，填入 {"tool_name": "erp_get_order", "args": {"order_id": "A123"}} 进行测试。

### 2. 调试 Python MCP 服务（使用官方 Inspector）
官方提供了一个网页版的可视化调试工具，可以模拟智能体调用 MCP。
前提要求：安装了 Node.js（需要 18+ 版本）。
启动命令（在 Python 脚本所在目录打开终端）：
```bash
npx -y @modelcontextprotocol/inspector python Mcp_Server.py
```
(如果国内下载极慢，先执行 npm config set registry https://registry.npmmirror.com 换源)
操作步骤：
运行命令后，浏览器会自动打开 http://localhost:5173 页面。
点击左侧的 Connect 按钮，等待状态变为绿色的 Connected。
点击顶部菜单栏的 Tools，即可看到三个工具。
点击 GetSkillList，在右侧 query 输入框填词（如 order），点击 Execute Tool。
观察右侧返回的 JSON 结果，看是否和 .NET Swagger 里的结果一致。


## 📡 老式 WebService (SOAP) 接入指南

在 .NET 8 中，使用传统的 `ChannelFactory` 动态解析 WSDL 踩坑极多，为了稳定和简单，本网关放弃了纯动态反射，**采用“ HttpClient 发送 XML + 半自动拼装”的策略**。

日常接入新接口时，只需要写 JSON，**不需要写任何 C# 代码**。

### 方案一：半自动拼装（推荐，适用于 80% 的简单接口）

如果你的接口参数都是扁平的（如 string, int），只需配置命名空间和方法名，网关会自动帮你套上 `<soapenv:Envelope>` 外壳。

**JSON 配置示例**（以 `Exchange2013Maillist` 为例）：
```json
{
  "ws_add_maillist": {
    "desc": "新增邮件列表",
    "params": {
      "groupname": "string 组名",
      "manage": "string 管理员",
      "password": "string 密码"
    },
    "soap": "http://172.16.25.195:8022/Exchange2013Maillist.asmx",
    "action": "http://Exchange2013Maillist/AddNewMaillist2013",
    "soap_method": "AddNewMaillist2013",
    "soap_namespace": "http://Exchange2013Maillist/"
  }
}
```
- soap：服务地址（注意千万不要带 ?wsdl
- action：请求头中的 SOAPAction，通常在 WSDL 文件里搜索 soapAction= 就能看到。
- soap_method / soap_namespace：网关会自动根据这两个字段，结合大模型传入的 params，拼装出完整的 XML 报文。

### 方案二： 全手动写死 XML（兜底，适用于复杂嵌套对象）
如果某个接口的参数包含了嵌套对象（比如前面提到的 S_MailListMember），半自动拼装会失败。此时需要在 JSON 中直接写死 body 字段。
```json
{
  "ws_add_complex": {
    "desc": "复杂对象新建邮件列表",
    "params": {
      "MailListAddress": "string 邮箱地址",
      "UserName": "string 用户名"
    },
    "soap": "http://172.16.25.195:8022/Exchange2013Maillist.asmx",
    "action": "http://Exchange2013Maillist/AddNewMaillist",
    "body": "<soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns:tem=\"http://Exchange2013Maillist/\"><soapenv:Header/><soapenv:Body><tem:AddNewMaillist><tem:smlm><tem:MailListAddress>{MailListAddress}</tem:MailListAddress><tem:UserName>{UserName}</tem:UserName></tem:smlm></tem:AddNewMaillist></soapenv:Body></soapenv:Envelope>"
  }
}
```
- 注意：XML 里的双引号必须转义为 \"。
- 用 {参数名} 作为占位符，网关会自动用大模型传入的值进行替换。

### ⚠️ SOAP 接入避坑指南
- 弃用 WCF 包：请确保项目中没有安装 System.ServiceModel.Http 等包，我们纯靠 HttpClient 发送字符串，不需要那些又老又重的依赖，彻底告别 CS0234 报错。
- *** SOAP 1.1 vs 1.2：目前的自动拼装代码写死了 SOAP 1.1 的协议头（xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/"）。如果对方系统报 415 Unsupported Media Type，说明只支持 SOAP 1.2，需要手动修改 SkillRegistry.cs 里的 ExecuteSoapAsync 方法，将 soapenv 替换为 soap12，并更改对应的 Content-Type。
- 如何调试 XML：如果接口总是报错，强烈建议用 SoapUI 或 Postman 先调通，把调通的 XML 报文直接复制出来，按方案二配置到 JSON 里，保准能通。

**提示**：
这段话里的“避坑指南”第 2 点非常有价值，很多老系统的运维人员自己都搞不清是 1.1 还是 1.2，记下来以后能省去大量排查时间。直接复制粘贴即可。


### 💡 典型配置示例

为了帮助理解，这里提供两个最常见的 JSON 配置模板。你可以直接把它们复制到你的 `skills/` 目录下进行修改。

#### 示例 1：半自动拼装（适用于简单的平铺参数）
假设我们有一个通用的用户服务 WebService（`UserService.asmx`），提供了一个 `addUser` 方法，只需要传用户名、邮箱和年龄。网关会自动帮你把它包装成 XML 信封。

```json
{
  "ws_add_user": {
    "desc": "在系统中添加一个新用户",
    "params": {
      "userName": "string 登录用户名",
      "email": "string 用户邮箱地址",
      "age": "?int 年龄(可选)"
    },
    "soap": "http://your-webservice-domain.com/UserService.asmx",
    "action": "http://tempuri.org/addUser",
    "soap_method": "addUser",
    "soap_namespace": "http://tempuri.org/"
  }
}
```
- 大模型传参：{"userName": "张三", "email": "zhangsan@test.com"}
- 网关自动生成 XML：
 ```xml
<soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/" xmlns:tem="http://tempuri.org/">
  <soapenv:Header/><soapenv:Body>
    <tem:addUser>
      <tem:userName>张三</tem:userName>
      <tem:email>zhangsan@test.com</tem:email>
    </tem:addUser>
  </soapenv:Body>
</soapenv:Envelope>
 ```

### 示例 2：全手动写死 XML（适用于复杂嵌套对象或数组）
假设有一个 GroupService.asmx，提供了一个 addGroupWithMembers 方法，要求传入一个组名，以及一个成员数组。因为包含复杂的 XML 嵌套结构，我们必须在 JSON 里把完整的 body 写死。
```json
{
  "ws_add_group": {
    "desc": "新建一个用户组，并同时指定组内的多个成员",
    "params": {
      "groupName": "string 组名称",
      "admin": "string 管理员账号",
      "membersXml": "string 成员的XML片段(格式如: <string>a@a.com</string><string>b@b.com</string>)"
    },
    "soap": "http://your-webservice-domain.com/GroupService.asmx",
    "action": "http://tempuri.org/addGroupWithMembers",
    "body": "<soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns:tem=\"http://tempuri.org/\"><soapenv:Header/><soapenv:Body><tem:addGroupWithMembers><tem:groupName>{groupName}</tem:groupName><tem:admin>{admin}</tem:admin><tem:members>{membersXml}</tem:members></tem:addGroupWithMembers></soapenv:Body></soapenv:Envelope>"
  }
}
```
- 大模型传参：{"groupName": "技术部", "admin": "admin01", "membersXml": "<string>a@test.com</string><string>b@test.com</string>"}
- 网关行为：网关会找到 JSON 里的 {groupName} 等占位符，直接替换成真实数据，然后将完整的 body 字符串原封不动地发送给 WebService。
- 注意：写死 body 时，JSON 字符串内部的 XML 双引号一定要用反斜杠转义（\"）。


## 🗄️ 智能体数据库查询与执行 (SQL)

除了调用普通的 HTTP 接口和 WebService，网关还内置了**直连数据库**的超强能力，供智能体查询业务数据或执行受控的修改操作。

### 1. 支持的数据库
原生基于 ADO.NET 的 `DbProviderFactories` 实现，目前完美支持：
*   **SQL Server** (`Microsoft.Data.SqlClient`)
*   **MySQL** (`MySqlConnector`)
*   **SQLite** (`Microsoft.Data.Sqlite`)

> *(如需接入 Oracle 或国产达梦等，只需在 `Program.cs` 注册对应的 Provider 工厂即可)*

### 2. 安全防线与“二次确认”机制
由于大模型可能产生幻觉，直接执行 SQL 非常危险，因此网关设计了严格的安全机制：
1.  **只读放行**：所有的 `SELECT` 和 `WITH` 查询直接放行，并自动截断最多返回 100 行，防止撑爆大模型上下文。
2.  **危险操作拦截**：`UPDATE`、`DELETE`、`EXEC`（存储过程）等操作，必须在 `appsettings.json` 的 `AllowedOperations` 白名单中配置，且**必须经过二次确认**。
3.  **确认流程**：
    *   智能体第一次调用时，不带 `confirm_token`，网关会拦截并返回 `error_type="REQUIRE_CONFIRMATION"`，同时下发一个 5 分钟内有效的一次性 Token。
    *   智能体会将该 SQL 展示给用户，询问是否确认。
    *   用户回复“确认”后，智能体带上 Token 第二次调用，网关校验通过后执行，并立即销毁 Token。

### 3. 中转层配置说明 (`appsettings.json`)
```json
"DatabaseSettings": {
  "AllowedOperations": [ "UPDATE", "DELETE", "EXEC" ],
  "Connections": {
    "ERP_SQLSERVER": {
      "Provider": "Microsoft.Data.SqlClient",
      "ConnectionString": "Server=192.168.1.100;Database=Erp;User Id=sa;Password=123;TrustServerCertificate=True"
    },
    "HR_MYSQL": {
      "Provider": "MySqlConnector",
      "ConnectionString": "Server=192.168.1.101;Database=Hr;User Id=root;Password=123;"
    }
  }
}
```
> 注：Provider 的字符串必须与 Program.cs 中注册的工厂名称严格一致。

### 4. 智能体端 (MCP) 工具说明
MCP 对外暴露了一个 ExecuteSql 工具，参数如下：
db_alias：数据库别名（如 "HR_MYSQL"）。
sql：要执行的 SQL 语句。
confirm_token：危险操作时的确认凭证（默认不填）。

### 5. SQL 方言避坑指南 (System Prompt 建议)
由于不同数据库的语法不同，强烈建议在智能体平台的系统提示词中加入以下约束：
``` text
"当你调用 ExecuteSql
 时，请严格遵守目标数据库的语法。例如 MySQL 分页请用 LIMIT，如果是 SQL Server 请用 TOP；
 如果是查询数量，请注意不同数据库返回字段名的差异。
 如需执行修改操作，请严格按照工具返回的提示向用户请求确认。"
 ```

 ## 🎯 七、 精准制导：配置固定的 SQL 模板查询

除了让智能体自由发挥写 SQL（`ExecuteSql`）之外，在业务场景中，我们更推荐把**写好的、涉及多表关联的复杂 SQL**封装成技能。这样不仅能避免大模型产生幻觉，还能保证查询性能和业务逻辑的绝对准确。

我们复用了现有的 `ExecuteSkill` 工具，底层新增了 `"sql"` 协议。

### 1. 配置说明 (`skills/*.json`)

在 JSON 配置文件中，使用 `"sql"` 指定 SQL 模板，使用 `"db_alias"` 指定目标数据库，使用 `{参数名}` 作为占位符。

**示例：查询指定部门的在职员工（带条件）**
```json
{
  "hr_query_active_users_by_dept": {
    "desc": "按部门 ID 查询所有在职员工及所属部门名称",
    "params": {
      "deptId": "int 部门ID"
    },
    "sql": "SELECT u.name, d.dept_name FROM t_user u LEFT JOIN t_dept d ON u.dept_id = d.id WHERE u.status = 1 AND d.id = {deptId}",
    "db_alias": "HR_MYSQL"
  }
}
```
示例：获取全表统计（无参数）
```json
{
  "hr_get_user_count": {
    "desc": "获取全系统用户总数",
    "sql": "SELECT COUNT(*) as total FROM t_user",
    "db_alias": "HR_MYSQL"
  }
}
```
### 2. 底层安全机制：参数化查询防注入
当你使用 {参数名} 占位符时，.NET 网关底层并不是简单地进行字符串替换。
在执行前，网关会自动将 {deptId} 转换为 ADO.NET 的参数化查询 @deptId，并为其绑定值。这从根本上杜绝了 SQL 注入的风险。

### 3. 智能体的调用流程
* 用户问：“帮我查一下技术部（ID 为 5）的员工名单。”
* 智能体调用 GetSkillList("查询员工")。
* 发现技能 hr_query_active_users_by_dept。
* 智能体调用 ExecuteSkill，传入参数：
```json
{
  "tool_name": "hr_query_active_users_by_dept",
  "args": { "deptId": 5 }
}
```
* 网关执行 SQL，返回标准的 JSON 数据集给大模型进行总结。
> 提示：对于核心的、经常复用的业务查询，强烈建议采用这种“配置模板”的方式，而不是让大模型通过 ExecuteSql 自行拼装。前者稳定可控，后者灵活但存在风险。
```text
复制这段内容放入 README 后，你整个 SQL 模块的介绍就非常完整了：既有给大模型自由探索的“动态执行”，也有保障业务绝对准确的“模板配置”。
```