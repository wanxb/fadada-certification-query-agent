# 配置、秘密与账号运行手册

## 配置来源

Web 使用 ASP.NET Core 标准配置层级。生产环境只在服务器受保护配置中提供真实值，发布目录中的 `appsettings.json` 只保存非敏感默认值。

开发机可使用被 Git 忽略的 `src/Fadada.CertificationQueryAgent.Web/appsettings.Local.json`。应用会在标准配置之后加载它，但项目文件强制 `CopyToOutputDirectory=Never` 和 `CopyToPublishDirectory=Never`；每次发布仍必须执行发布包秘密扫描。该文件只适合单机开发，不替代生产秘密系统。

| 环境键 | 必需 | 说明 |
|---|---:|---|
| `ConnectionStrings__FddDomainAgent` | 是 | 独立 V2 数据库连接串 |
| `Persistence__Profile` | 是 | 生产为 `ProductionReference`；本地兼容实验为 `LabSqlServer2012` |
| `Model__BaseUrl` | 是 | Responses 网关绝对 HTTP/HTTPS 基址；优先使用 HTTPS |
| `Model__ApiKey` | 是 | 模型网关秘密 |
| `Model__Name` | 是 | 已通过兼容性验证的模型名 |
| `Model__MaximumRetries` | 否 | 瞬时 502/503/504 重试次数，默认 1，最大 2 |
| `Fadada__BaseUrl` | 是 | 获准的法大大 HTTPS 基址 |
| `Fadada__AppId` | 是 | 服务端应用标识，按秘密管理 |
| `Fadada__AppSecret` | 是 | 服务端应用秘密 |
| `Security__DataProtectionKeysPath` | 是 | 应用池身份独占的持久密钥目录 |
| `OpenTelemetry__OtlpEndpoint` | 否 | 受信 OTLP HTTPS 或回环地址 |

`OpenTelemetry__CaptureSensitiveContent` 在生产必须为 `false`。不要启用 ASP.NET、SQL 或生成式 AI 的正文/参数采集。

模型适配器兼容内部 HTTP 网关，但 HTTP 无法保护 Bearer API Key、提示词、工具证据和模型输出的传输机密性。使用 HTTP 时必须由网络隔离、访问控制和网关侧审计承担风险控制；法大大基址仍强制使用 HTTPS。

## 秘密处理

1. 通过服务器环境、受 ACL 保护的配置提供者或组织秘密系统注入。
2. 生产秘密不得写入受版本控制的 `appsettings*.json`、`web.config`、发布目录、命令历史、日志、Trace、截图或工单。经负责人明确授权时，开发机可使用已被 Git 忽略且禁止构建/发布复制的 `appsettings.Local.json`，发布前仍须逐值扫描。
3. 发布前运行全量测试；Architecture 测试包含高置信秘密扫描，只报告文件和行号。
4. 轮换模型或法大大凭据后回收应用池，并验证 `/health/ready` 和一条获准的只读查询。
5. 连接串轮换必须保持数据库目标不变。公开示例的 `LabSqlServer2012` 只接受 `localhost/FadadaAgentLab`；`ProductionReference` 会拒绝该 Lab 目标、未加密或信任任意证书的连接。

## Data Protection

- 密钥目录只授予应用池身份读写权限，其他普通账号无权访问。
- Windows 上密钥再由本机 DPAPI 保护。应用池身份、服务器或 DPAPI 作用域变化前必须制定迁移方案。
- 数据库与 Data Protection 密钥必须纳入同一次恢复演练，否则 Cookie、会话状态和诊断密文可能无法解密。
- 密钥不可放在临时目录或发布覆盖目录。

## 管理 CLI

管理工具只在服务器本机运行，没有远程管理 API。所有账号具有相同工具能力；账号隔离仍由 `UserId` 和会话所有权实现。

本地 Lab 的开发账号元数据可以保存在被忽略的 `appsettings.Local.json` 的 `DevelopmentAccount` 节点，仅供本机管理命令和人工登录测试读取；应用不会在启动时自动创建或重置账号，生产环境不得使用该节点。

先在当前管理进程注入数据库配置：

```powershell
$env:FDD_STORE_PROFILE = "ProductionReference"
$env:FDD_STORE_CONNECTION_STRING = <从受保护来源注入>
```

执行账号操作：

```powershell
dotnet run --project tools\Fadada.CertificationQueryAgent.Admin\Fadada.CertificationQueryAgent.Admin.csproj -c Release -- user create alice --display-name "Alice"
dotnet run --project tools\Fadada.CertificationQueryAgent.Admin\Fadada.CertificationQueryAgent.Admin.csproj -c Release -- user reset-password alice
dotnet run --project tools\Fadada.CertificationQueryAgent.Admin\Fadada.CertificationQueryAgent.Admin.csproj -c Release -- user disable alice
dotnet run --project tools\Fadada.CertificationQueryAgent.Admin\Fadada.CertificationQueryAgent.Admin.csproj -c Release -- user enable alice
```

密码在隐藏终端提示中输入两次，不作为命令参数。创建、重置、停用和启用均写结构化审计；重置密码或停用会增加 `SecurityStamp`，现有 Cookie 在下一次请求时失效。

完成后清除当前进程环境，关闭管理终端，并核对命令只输出稳定状态码，没有连接串或秘密。

## 账号策略

- 账号由管理员预创建，不提供注册、找回密码、角色或权限菜单。
- 密码长度为 6–128 位，且至少同时包含一个字母和一个数字。该宽松策略仅适用于受控内网和少量内部用户；不得据此将登录入口直接暴露到公网。
- 连续失败达到阈值后锁定；登录端点同时按来源 IP 限流。
- 不使用 IP 作为身份。反向代理存在时，只有配置了受信代理列表后才能采纳转发头。
- 离职或不再需要访问时先停用账号，再按组织流程保留相关审计。
