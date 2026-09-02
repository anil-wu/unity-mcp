# Unity MCP

内嵌于 Unity 编辑器的 **MCP (Model Context Protocol) Server**：让 AI agent（如 pi 的 qa 子代理）通过 HTTP + JSON-RPC 直接驱动 Unity 做**运行态验收**——进入/退出播放、读控制台日志、跑 Test Framework 用例、读场景、截屏取证。

> 面向 Unity 2022.3 LTS（本仓库测试工程使用 2022.3.62f3；插件代码亦兼容 2022.2）。Unity 6.2+ 官方已内置 MCP，本仓库针对**无内置 MCP 的 2022 系列**做「移植」：把 MCP 协议层直接做进编辑器，去掉 Python 中间层。

## 仓库结构

```
unity-mcp/                          # 本仓库 = Unity 测试工程
├── Assets/                         # 测试资产 / 场景
├── Packages/
│   ├── manifest.json               # 测试工程依赖
│   └── com.anil-wu.unity-mcp/      # ★ 插件本体（embedded package）
│       ├── package.json
│       ├── Runtime/                # 运行时桥（截屏等）
│       ├── Editor/                 # MCP server + 编辑器工具
│       └── Tests/                  # 插件单测
└── ProjectSettings/
    └── ProjectVersion.txt          # Unity 版本
```

## 快速开始

1. 用 Unity Hub 打开本仓库目录（Unity 2022.3.62f3）。
2. 编辑器启动后，MCP server 自动监听 `http://localhost:6400`（可在 `Assets/UnityMcpConfig.asset` 或 Preferences 里改端口）。
3. 验证：

```bash
curl -X POST http://localhost:6400/mcp \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

## 提供的 MCP 工具

| 工具 | 说明 |
|---|---|
| `ping` | 健康检查 + 编辑器状态 |
| `editor_play` / `editor_pause` / `editor_stop` | 播放模式控制 |
| `console_logs` | 拉取缓冲的控制台日志（按级别过滤） |
| `scene_info` | 只读场景信息（活动场景、根对象数） |
| `run_tests` | 运行 Unity Test Framework 用例 |
| `execute_menu` | 执行编辑器菜单项 |
| `capture_screenshot` | 游戏画面截屏（运行时桥） |

## 配套 skill

AI 侧的调用封装见 [unity-mcp/](unity-mcp/)（pi skill：`unity-mcp.mjs` 客户端 + QA 工作流）。

## License

MIT
