# 法大大认证信息查询 Agent

基于 `.NET 10` 和 Microsoft Agent Framework 的企业内部专业领域查询 Agent。用户可以使用自然语言查询法大大侧的个人认证、企业认证、企业管理员关系和印章授权信息，系统负责将问题转换为受控的只读工具调用，并保留会话、证据和审计记录。

本项目同时用于学习和验证当前主流 AI Agent 架构在企业内网的落地方式。它不是通用聊天机器人，也不让模型直接访问数据库或任意 HTTP 接口：模型负责理解意图和选择工具，身份、参数来源、调用预算、外部端点、结果证据、会话所有权和审计均由确定性代码控制。

## 主要能力

- 自然语言、多轮澄清和上下文续问。
- 个人账户与认证信息查询。
- 企业账户、认证信息及企业管理员查询。
- 个人、企业及管理员关系的组合查询。
- 企业印章及个人授权关系查询。
- 当前会话、历史会话、归档、恢复和只读归档详情。
- Markdown 回答渲染与输出清洗。
- 本地账号登录、Cookie 会话、CSRF 防护和登录限流。
- SQL Server 持久化、结构化审计和 OpenTelemetry 可观测性。
- 离线单元、契约、架构、集成和 Agent Eval 门禁。

## 架构原则

```text
Blazor Server / Minimal API
            |
       Application ports
            |
 Microsoft Agent Framework
            |
 ownership -> provenance -> schema -> budget -> audit -> sanitize
            |
 query_person | query_company | query_relationship | query_seals
            |
       法大大固定只读接口

SQL Server  <-> 账号、会话、状态、证据和审计
OpenTelemetry -> 非敏感 Trace 与指标
Eval Harness  -> 离线回归和安全门禁
```

关键约束：

- 单 Agent 架构，避免为简单查询引入多 Agent 协作复杂度。
- 四个粗粒度 Function Tools，不允许模型构造任意 URL、SQL 或工具名。
- 单回合最多执行 3 个领域工具和 4 次模型调用。
- 人员、企业和管理员关系组合问题优先使用 `query_relationship` 一次取得完整证据。
- 工具参数必须来自用户明确输入或多轮确认，模型推断值不能越过 provenance 校验。
- 所有外部访问均为固定端点、只读调用，并写入关联审计。
- IP 只用于限流和审计上下文，不作为用户身份。

当前契约版本：

| 契约 | 版本 |
|---|---|
| Prompt | `query-agent.v2` |
| Function Tools | `domain-tools.v1` |
| REST API | `/api/v1` |
| SQL Schema | `2` |

## 技术栈

- .NET SDK `10.0.400`
- ASP.NET Core / Blazor Interactive Server
- Microsoft Agent Framework `Microsoft.Agents.AI`
- Microsoft.Extensions.AI / Responses API
- SQL Server 持久化，Lab Profile 兼容 SQL Server 2012
- Markdig + HtmlSanitizer
- OpenTelemetry
- xUnit + Microsoft.Extensions.AI.Evaluation

依赖版本由 `Directory.Packages.props` 集中管理，各项目启用锁文件、Nullable、Analyzers 和 warnings-as-errors。

## 工程结构

```text
src/
  Fadada.CertificationQueryAgent.Domain                 领域值、证据与规则
  Fadada.CertificationQueryAgent.Application            用例端口与应用契约
  Fadada.CertificationQueryAgent.AgentHost              Agent、Prompt、工具和策略管线
  Fadada.CertificationQueryAgent.Infrastructure         模型、法大大、认证和遥测适配
  Fadada.CertificationQueryAgent.Infrastructure.SqlServer2012
                                                         SQL Server 隔离适配
  Fadada.CertificationQueryAgent.Web                    Blazor UI、API 和 Web 安全
tests/
  Fadada.CertificationQueryAgent.UnitTests
  Fadada.CertificationQueryAgent.IntegrationTests
  Fadada.CertificationQueryAgent.ContractTests
  Fadada.CertificationQueryAgent.ArchitectureTests
  Fadada.CertificationQueryAgent.WebTests
  Fadada.CertificationQueryAgent.Evals
tools/
  Fadada.CertificationQueryAgent.Admin                  本地账号管理 CLI
database/v2/                                            Schema V2 脚本
docs/                                                   ADR、报告、威胁模型和运维手册
scripts/                                                开发启动脚本
```

## 快速体验

UiDemo 使用内存中的合成数据，不连接 SQL Server、模型网关或法大大，适合验证 UI 和基本交互：

```powershell
dotnet restore FadadaCertificationQueryAgent.slnx --locked-mode
.\scripts\Start-CertificationQueryAgent.ps1 -UiDemo
```

默认访问地址为 `http://localhost:5256`。UiDemo 只能在 `Development` 环境运行，不能用于真实查询或生产部署。

## 真实开发环境

真实开发配置放在以下被 Git 忽略的文件中：

```text
src/Fadada.CertificationQueryAgent.Web/appsettings.Local.json
```

该文件不会复制到构建输出或发布包。最小结构如下，所有值均需替换为当前环境的真实配置：

```json
{
  "ConnectionStrings": {
    "FddDomainAgent": "Server=<server>;Database=<database>;User ID=<user>;Password=<password>;Encrypt=False"
  },
  "Persistence": {
    "Profile": "LabSqlServer2012"
  },
  "Model": {
    "BaseUrl": "http://<internal-model-gateway>:<port>/",
    "ApiKey": "<model-api-key>",
    "Name": "<model-name>"
  },
  "Fadada": {
    "BaseUrl": "https://<approved-fadada-endpoint>/",
    "AppId": "<app-id>",
    "AppSecret": "<app-secret>"
  },
  "Security": {
    "DataProtectionKeysPath": "C:/ProgramData/FadadaAgent/DataProtectionKeys"
  }
}
```

JSON 中的 Windows 路径建议使用 `/`，或将每个反斜杠写成 `\\`，否则类似 `C:\ProgramData` 的单反斜杠会造成 JSON 解析失败。

启动真实链路：

```powershell
.\scripts\Start-CertificationQueryAgent.ps1
```

脚本检测到 `appsettings.Local.json` 后会使用 Development 环境。需要开发 HTTPS 时可以显式指定地址：

```powershell
.\scripts\Start-CertificationQueryAgent.ps1 -Urls "https://localhost:7073"
```

## 数据库初始化

应用启动不会创建或迁移数据库。请在明确选中的独立数据库中人工按以下顺序执行：

1. `database/v2/001-create-schema.sql`
2. `database/v2/002-create-indexes.sql`
3. `database/v2/004-enable-bounded-multi-tool-turns.sql`
4. `database/v2/003-readiness-check.sql`

最终检查必须返回 `IsReady = 1` 且 `SchemaVersion = 2`。脚本只创建和访问 `dbo.FddAgent*` 对象，不应连接 PSP 业务数据库。完整边界见 [数据库说明](database/README.md)。

## 账号管理

系统不提供注册、找回密码、角色和工具权限菜单。所有内部账号拥有相同查询能力，但会话仍按 `UserId` 隔离。

管理工具只在受信服务器或管理机本地运行：

```powershell
$env:FDD_STORE_PROFILE = "ProductionReference"
$env:FDD_STORE_CONNECTION_STRING = "<protected-connection-string>"

dotnet run --project tools\Fadada.CertificationQueryAgent.Admin -c Release -- user create alice --display-name "Alice"
dotnet run --project tools\Fadada.CertificationQueryAgent.Admin -c Release -- user reset-password alice
dotnet run --project tools\Fadada.CertificationQueryAgent.Admin -c Release -- user disable alice
dotnet run --project tools\Fadada.CertificationQueryAgent.Admin -c Release -- user enable alice
```

密码通过隐藏终端提示输入，不作为命令参数。当前内网策略要求 6–128 位并同时包含字母和数字；该策略不适用于公网服务。

## 构建与测试

```powershell
dotnet restore FadadaCertificationQueryAgent.slnx --locked-mode
dotnet build FadadaCertificationQueryAgent.slnx -c Release --no-restore -p:TreatWarningsAsErrors=true
dotnet test FadadaCertificationQueryAgent.slnx -c Release --no-build --no-restore
dotnet run --project tests\Fadada.CertificationQueryAgent.Evals -c Release --no-build --no-restore
```

默认测试和 Eval 必须离线运行，不访问模型网关、法大大或真实数据库。真实环境联调应单独执行并使用合成或获准数据。

## IIS 发布

生成全新的发布目录，避免旧程序集残留：

```powershell
$publishDirectory = Join-Path "artifacts\publish\iis" (Get-Date -Format "yyyyMMdd-HHmmss")
dotnet publish src\Fadada.CertificationQueryAgent.Web\Fadada.CertificationQueryAgent.Web.csproj `
  -c Release --no-restore `
  -o $publishDirectory
```

IIS 要点：

- 安装与目标框架匹配的 .NET 10 Hosting Bundle。
- 使用独立应用池，`.NET CLR Version` 为“无托管代码”，最大工作进程为 1。
- IIS 开启 Anonymous Authentication，由应用 Cookie 负责登录认证。
- 使用 HTTPS 绑定和有效证书；HTTP 只能用于跳转到 HTTPS。
- Data Protection 密钥目录必须位于发布目录之外，并授予应用池身份读写权限。
- 生产环境设置 `ASPNETCORE_ENVIRONMENT=Production`。
- `appsettings.Local.json` 不会进入发布包，生产配置必须由服务器环境、受 ACL 保护的配置或秘密系统提供。

生产模式强制认证 Cookie 和 Antiforgery Cookie 使用 Secure。若 IIS 只有 HTTP 绑定，访问 `/login` 会返回：

```json
{"errorCode":"SERVICE_REQUEST_FAILED","traceId":"..."}
```

其实际原因是 Antiforgery 拒绝在非 SSL 请求中签发 Secure Cookie。修复方式是配置 HTTPS，而不是关闭 CSRF 或降低生产 Cookie 安全级别。

部署后从服务器本机验证：

```text
GET /health/live   应返回 200
GET /health/ready  应返回 200，且只允许回环地址访问
GET /login         应返回登录页面并签发 Antiforgery Cookie
```

完整步骤见 [IIS 部署手册](docs/iis-deployment.md)。

## 配置与安全边界

真实值不得提交到 Git、日志、Trace、截图或发布包：

```text
ConnectionStrings__FddDomainAgent
Persistence__Profile
Model__BaseUrl
Model__ApiKey
Model__Name
Fadada__BaseUrl
Fadada__AppId
Fadada__AppSecret
Security__DataProtectionKeysPath
```

`ProductionReference` 应使用受支持的 SQL Server 和加密连接；`LabSqlServer2012` 只用于已批准的隔离实验环境。模型适配器可以兼容内部 HTTP 网关，但这意味着 API Key、Prompt、工具证据和模型输出依赖内网隔离保护；法大大端点始终要求 HTTPS。

## 文档索引

- [架构决策记录](docs/adr/001-agent-runtime-and-model-boundary.md)
- [配置与账号](docs/runbooks/configuration-and-accounts.md)
- [日常运维](docs/runbooks/operations.md)
- [故障演练与回滚](docs/runbooks/failure-drills-and-rollback.md)
- [IIS 部署](docs/iis-deployment.md)
- [威胁模型](docs/security/threat-model.md)
- [Agent Eval 门禁](docs/reports/agent-evaluation-release-gate.md)

## 项目状态

当前实现已完成架构重构、Schema V2、四个受控领域工具、本地账号、会话归档/恢复、Markdown 安全渲染、离线测试与 Eval。公开版本仅包含通用本地 Lab 示例和合成测试数据，不包含任何内部环境配置或验收记录。

该结论表示代码通过本地工程门禁，不等同于生产批准。正式部署仍需要受支持的数据库、可信服务器证书、秘密管理、网络边界、备份恢复演练和独立上线审批。
