# 数据库脚本入口

本项目只使用 [`database/v2`](v2/README.md) 下的 12 张 `FddAgent*` 业务、状态与审计表。执行顺序为 `001-create-schema.sql`、`002-create-indexes.sql`、`004-enable-bounded-multi-tool-turns.sql`，最后运行只读的 `003-readiness-check.sql`。这些脚本只能人工执行；公开示例的 `localhost/FadadaAgentLab` 仅用于本地兼容实验，生产目标仍须独立审批和执行。

已退役架构的根目录 DDL 和检查脚本均已删除。当前脚本不查询或修改 PSP 业务表，也不会由应用启动过程自动执行。

## 其他环境执行前确认

任何环境执行 V2 DDL 前，必须由负责人明确确认：

1. 目标服务器和独立审计数据库名称。
2. DDL 执行账号具备建表、建索引权限。
3. 目标数据库是本服务的审计库，不是需要隔离的 PSP 业务库。
4. 应用运行账号只获得 V2 Repository 所需的最小 DML 权限，不授予任意 DDL 或 PSP 业务表权限。

脚本不包含 `USE`，必须先在数据库工具中明确选择目标审计数据库。应用启动不会自动执行 DDL。

具体表、权限、执行顺序、回滚边界和数据生命周期约束以 [V2 数据库说明](v2/README.md) 为准。
