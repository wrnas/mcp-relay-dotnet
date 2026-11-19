# AD LDAP 快速配置（个人助手）

这个补丁只改变了 `LdapQueryExecutor.cs` 的密码配置与连接错误提示。不会修改现有 Skill、Python MCP 或 AD 域控设置。

## 1. 最简配置

将下列 `ActiveDirectory` 节点合并到现有 `appsettings.json`，替换为真实域控 FQDN、域名及凭据：

```json
"ActiveDirectory": {
  "Enabled": true,
  "Servers": {
    "CORP_AD": {
      "Host": "dc01.example.local",
      "Port": 636,
      "UseSsl": true,
      "AuthType": "Basic",
      "Username": "ops-query@example.local",
      "Password": "这里填写账号密码",
      "BaseDn": "DC=example,DC=local",
      "TimeoutSeconds": 15,
      "MaxResults": 100,
      "DeniedAttributes": []
    }
  }
}
```

- `Password`：允许直接写密码，方便仅在本机保存、调试的个人部署。这个字段是新增支持的配置。
- `PasswordEnv`：可选的环境变量名，适合长期部署。若同时配置，优先从 `PasswordEnv` 读取；该变量不存在时将明确报错，不会回退到 `Password`。
- `Host`：尽量使用 **实际域控制器的 FQDN**，且要与 LDAPS 证书 DNS 名称匹配。不能仅凭域名 `example.local` 的 636 端口可达就认定该名称适合 LDAPS。
- `DeniedAttributes`：可选；默认禁止查询少数凭据/恢复密钥类敏感属性。每个 Skill 仍通过 `ldap_attributes` 指定需要查询的字段。
- 实际 LDAP 查询使用所配置账号的 AD 权限；此版本仅实现只读 LDAP Skill，没有 AD 写操作功能。

**不要把含真实 Password 的 appsettings.json 提交到 Git。** 单机私有使用也建议限制文件访问权限；后续可以随时改成 `PasswordEnv`。

## 2. Swagger 测试

先确认 `skills/ad_ldap.json` 已部署；重启 RelayMcp 后调用 `GET /skills` 应找到 `ad_ldap_get_user`。

`POST /skills/execute`：

```json
{
  "tool_name": "ad_ldap_get_user",
  "args": { "username": "testuser" }
}
```

## 3. LDAP 81 的排查顺序

LDAP 81 对应 `LDAP_SERVER_DOWN`，常见于连接或 TLS 建立失败。它不表示属性黑名单或账号权限不足。

1. 在 **RelayMcp.exe 所在机器** 上执行：`Test-NetConnection dc01.example.local -Port 636`。
2. 在同一台机器上运行 `ldp.exe`，选择 `Connection > Connect`，填与 `Host` **完全相同**的域控 FQDN，端口 `636`，并勾选 `SSL`。
3. 如果 TCP 通但 LDP 失败，重点检查 LDAPS 证书 DNS 名称、证书信任链、系统 TLS 策略和实际目标域控。**不要通过关闭证书验证来规避问题。**
4. 如果 LDP 成功但网关仍失败，查看网关日志中的原始 `LdapException`，新版会标明失败阶段是“连接/认证”还是“搜索”。

注意：`UseSsl=true` 和 `AuthType=Basic` 的组合在连接加密通道中进行 LDAP Simple Bind。当前版本继续要求 TLS，以避免密码明文传输。

## 4. 本次修改范围

- 只需覆盖 `LdapQueryExecutor.cs`，修改配置后重新编译和发布。
- 不需要覆盖 `appsettings.json`，可手动添加 `Password` 并移除 `PasswordEnv`。
- 没有改动 AD 属性读取方式、审批逻辑或其他执行器。
- 当前环境无法执行 .NET 编译，也无法连接你的实际域控，请在本地先编译、再按上述步骤联调。
