# IIS 部署与验收

## 部署边界

部署对象只有 `src/Fadada.CertificationQueryAgent.Web`。应用采用 Blazor Interactive Server 和 Cookie 本地账号，必须使用 HTTPS、单实例应用池并禁用 Web Garden。

SQL Server 2012 只允许作为隔离的本地实验 Profile。生产部署必须使用受支持的 SQL Server、有效证书和现代 TLS；本手册不把 Lab 兼容结果解释为生产批准。

## 前置条件

- 目标 Windows Server 安装与 `global.json`/目标框架匹配的 .NET 10 Hosting Bundle，安装后重启 IIS。
- 已创建独立数据库并由 DBA 人工执行 `database/v2/001-create-schema.sql`、`002-create-indexes.sql` 和只读 readiness 检查。
- 应用池身份只拥有发布目录读取执行、Data Protection 目录读写及 V2 表所需最小 DML 权限。
- 服务器可访问获准的 Responses 模型网关（HTTP/HTTPS）以及法大大 HTTPS 端点和 OTLP 端点；模型使用 HTTP 时必须记录风险接受并依赖内部网络隔离，任何客户端均不得允许任意重定向。
- 已准备服务器受保护配置。发布包中不得包含连接串、密码、Cookie、App Secret 或 API Key。

## 生成发布包

```powershell
dotnet restore FadadaCertificationQueryAgent.slnx --locked-mode
$publishDirectory = Join-Path "artifacts\publish\iis" (Get-Date -Format "yyyyMMdd-HHmmss")
dotnet publish src\Fadada.CertificationQueryAgent.Web\Fadada.CertificationQueryAgent.Web.csproj `
  -c Release --no-restore `
  -o $publishDirectory
```

每次发布必须使用新的空目录，避免旧版本程序集残留。发布只生成文件，不修改 IIS、不执行 DDL、不连接真实外部服务。部署前对包执行恶意软件、秘密和文件清单检查，并记录 Git 提交、SDK、包锁和 SHA-256。

## IIS 设置

1. 创建专用应用池，`.NET CLR Version` 选择“无托管代码”，Pipeline 为 Integrated。
2. `Maximum Worker Processes` 设置为 1；关闭重叠发布期间的双实例写入。
3. 使用专用低权限应用池身份，不与 PSP 站点共用身份。
4. 创建 HTTPS 站点绑定并配置有效证书；HTTP 只用于重定向到 HTTPS。
5. 启用 IIS Anonymous Authentication，让 ASP.NET Core Cookie 完成应用身份认证；禁用 Basic 和 Windows Authentication。
6. 将应用池的 `loadUserProfile` 设置与 DPAPI 方案保持一致，变更身份前先处理密钥迁移。
7. WebSocket/长连接和代理超时应允许 Blazor Server 与 SSE 完成，但总查询预算仍由应用控制。

## 服务器配置

按 [配置、秘密与账号手册](runbooks/configuration-and-accounts.md) 注入必需键。推荐的生产非敏感覆盖：

```text
ASPNETCORE_ENVIRONMENT=Production
Persistence__Profile=ProductionReference
DataLifecycle__Enabled=true
DataLifecycle__ArchivedConversationRetentionDays=180
DataLifecycle__RunIntervalHours=24
DataLifecycle__BatchSize=500
OpenTelemetry__CaptureSensitiveContent=false
```

`Security__DataProtectionKeysPath` 必须位于发布目录之外。不要在 `web.config` 的 `<environmentVariables>` 中明文保存秘密；使用组织批准的服务器配置/秘密提供者，并限制管理员读取范围。

## 数据库权限

- 部署身份：仅在变更窗口获得 V2 DDL 权限。
- 运行身份：V2 Repository 所需的 `SELECT`、`INSERT`、`UPDATE` 和经评审的数据生命周期删除权限。
- 不授予 PSP 数据库、V1 表、跨库视图、任意 DDL 或数据库所有者权限。
- 应用启动只做 readiness，不自动建表或迁移。

## 上线验收

1. 从服务器本机请求 `/health/live`，应为 200。
2. 从服务器本机请求 `/health/ready`，应为 200；从非回环来源访问 ready 应被拒绝。
3. 匿名访问 `/api/v1/conversations` 应为 401；登录页和 Blazor 启动静态资源可匿名加载。
4. 使用合成账号登录，确认 Cookie 为 Secure、HttpOnly、SameSite=Strict，状态修改要求 Antiforgery。
5. 创建、读取、归档本人会话，确认其只出现在“已归档”并保持只读；执行恢复后应回到“当前”并可继续查询。用另一合成账号读取、归档或恢复均应返回 404。
6. 执行获准只读查询，核对 typed SSE、证据状态、Turn/Model/Tool/External 审计和 Trace 关联。
7. 执行数据库、模型、法大大和 OTLP 故障演练，结果符合运行手册。
8. 检查页面、日志、数据库、Trace 和发布目录没有秘密或原始外部载荷。

## 当前未完成的环境验收

公开仓库仅证明本地 Release、默认离线测试和 Eval 可以通过，不包含任何目标 IIS、真实模型网关、法大大账号或数据库环境的验收结论。完成独立环境验证前，状态只能是“参考实现通过门禁”，不能标记为生产上线完成。
