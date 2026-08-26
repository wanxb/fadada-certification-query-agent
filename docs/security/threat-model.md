# 法大大认证信息查询威胁模型

> 更新日期：2026-08-25
> 范围：`Fadada.CertificationQueryAgent` 当前参考架构

## 资产

- 本地账号、密码哈希、SecurityStamp 和认证 Cookie。
- 法大大与模型网关凭据。
- 人员、企业、关系和印章认证信息。
- 会话、消息、模型调用、工具调用、外部调用和安全决策审计。
- Prompt、工具 Schema、固定端点目录和 Data Protection 密钥。

## 信任边界

```text
Browser --HTTPS/Cookie/CSRF--> Web/API
Web ----authenticated UserId--> AgentHost
AgentHost --deterministic policy--> registered tools
AgentHost --HTTP or HTTPS/Bearer--> model gateway
Tools --fixed HTTPS endpoints--> Fadada
Infrastructure --parameterized SQL--> FddAgent schema
Application --redacted telemetry--> OTLP collector
```

浏览器输入、模型输出、工具返回和外部响应全部不可信。SQL Server 2012 Lab、模型网关、法大大和 OTLP 均属于进程外边界。

## 攻击者能力

- 未认证网络访问者可访问登录页和 live health。
- 已认证内部用户可提交任意自然语言并访问自己的会话。
- 恶意或被污染的模型/外部结果可尝试诱导工具调用或泄露信息。
- 具备服务器或数据库权限的管理员可能误配置、读取秘密或破坏审计完整性。

## 主要滥用路径与控制

| 滥用路径 | 关键控制 |
|---|---|
| 凭据猜测与会话劫持 | 本地密码哈希、锁定、SecurityStamp、Secure/HttpOnly/SameSite Cookie、持久化 DPAPI 密钥、登录限流 |
| 以 IP 冒充用户 | IP 不参与身份；所有资源使用认证 `UserId` 所有权检查 |
| CSRF 与跨站脚本 | Antiforgery、严格 CSP、安全响应头、同源静态资源、Blazor/JSON 文本编码、禁止原始 HTML 注入 |
| 水平越权 | 会话读取、归档、恢复和 Turn 均携带认证所有者；不存在客户端可控 provider/session 身份 |
| Prompt 或工具结果注入 | 用户输入按不可信自然语言处理；参数来源标签、工具 allowlist、固定端点、外部结果内容分类与净化、零容忍非法工具 Eval |
| 任意工具/写操作/SSRF | 仅四个粗粒度只读工具；模型不能控制 URL、HTTP 方法、SQL 或内部调用顺序 |
| 数据外泄到日志和遥测 | 凭据净化、敏感内容采集默认关闭、低基数标签、发布包逐值秘密扫描 |
| 审计绕过 | Turn 与外部调用前置审计；审计不可用时失败关闭；审计与遥测职责分离 |
| SQL 注入或 Schema 漂移 | 参数化访问、隔离适配器、只读 readiness、固定 Schema version 2、应用不自动迁移 |
| HTTP 模型网关泄露 Bearer 密钥和查询内容 | 负责人接受内部 HTTP 网关约束；应用禁用重定向和 Cookie、净化诊断，外部依赖网络隔离、访问控制和网关审计；具备条件时迁移 HTTPS |
| 资源耗尽 | 登录/Turn 限流、每 Turn 模型与工具预算、超时、取消、单会话并发控制 |

## 残余风险

- 内部授权用户仍可查询其工具能力允许的认证信息；当前不区分工具权限，必须依赖人员管理和审计追责。
- SQL Server 2012 不满足现代生产安全基线，仅允许隔离的本地 Lab 兼容测试。
- HTTP 模型网关无法提供传输机密性或服务器身份验证；Bearer API Key、提示词、工具证据和输出可能被同链路攻击者观察或篡改。
- 离线脚本化 Eval 不证明真实模型语义质量或面对未知攻击时的泛化能力。
- 单机本地账号没有企业身份生命周期联动，离职、轮换和禁用依赖管理员流程。
- 服务器管理员或数据库高权限账号可绕过应用层控制，需操作系统、数据库和秘密系统的独立最小权限与审计。

## 验证要求

- 默认离线测试必须覆盖认证、CSRF、所有权、注入、工具 allowlist、审计失败关闭和安全输出。
- 当前 Agent 36 案例门禁必须 100% 通过，安全违规和非法工具调用均为 0。
- 发布包不得包含 `appsettings.Local.json` 或任何已知真实秘密值。
- 生产上线前必须重新进行目标环境威胁审查、渗透测试、秘密轮换演练和数据库恢复演练。
