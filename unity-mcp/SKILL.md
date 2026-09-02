---
name: unity-mcp
description: 通过 Unity 编辑器内嵌 MCP server 做运行态 QA/验收：安装插件、启动 Unity、进入/退出播放、读控制台日志、跑 EditMode 测试、读场景、截屏取证。当游戏在 Unity 里、需要真实运行起来验证功能或画面时使用。
---

# Unity MCP QA

驱动 Unity 编辑器里的 MCP server 做**运行态验收**。你（QA agent）用 `bash` 调 `scripts/` 下的脚本，把 Unity 真正跑起来、取证、写报告。

## 前置准备（一次）

1. **安装插件**到目标 Unity 工程：

```bash
node scripts/install-plugin.mjs <目标工程路径>                       # 默认 git 方式
node scripts/install-plugin.mjs <目标工程路径> --source copy --plugin <插件文件夹路径>   # 本地拷贝
```

2. **启动 Unity**（编辑器加载后 MCP server 自动监听 `http://localhost:6400`）：

```bash
node scripts/start-unity.mjs <目标工程路径>                          # 自动按版本找 Unity.exe
node scripts/start-unity.mjs <目标工程路径> --unity <Unity.exe路径>  # 手动指定
```

3. **验证连通**：

```bash
node scripts/unity-mcp.mjs ping
```

失败（连不上）→ 提示用户：Unity 未打开 / MCP 未启动；端口不同用 `UNITY_MCP_URL` 覆盖。

## 命令速查

```bash
node scripts/unity-mcp.mjs ping                    # 健康检查
node scripts/unity-mcp.mjs tools                   # 列出可用工具
node scripts/unity-mcp.mjs play                    # 进入播放模式
node scripts/unity-mcp.mjs pause | resume | stop   # 暂停/恢复/退出
node scripts/unity-mcp.mjs console --level Error   # 拉取控制台日志
node scripts/unity-mcp.mjs scene                   # 只读场景信息
node scripts/unity-mcp.mjs tree                    # 场景 GameObject 层级树
node scripts/unity-mcp.mjs components --name Main Camera   # 查某对象的组件
node scripts/unity-mcp.mjs screenshot --out docs/qa/screenshots/xxx.png
node scripts/unity-mcp.mjs test --testNames "MyTests.Case1"
node scripts/unity-mcp.mjs call <工具名> '<json>'  # 通用兜底
```

## QA 工作流

1. `ping` 确认就绪；
2. `play` 进入播放模式；
3. 按验收点逐条触发场景（`call` 发编辑器事件 / `execute_menu` 走菜单 / 读场景核对状态）；
4. `console --level Error` 拉日志，记录崩溃/报错证据；
5. `screenshot --out docs/qa/screenshots/<场景>.png` 截屏存证（命名带场景）；
6. `stop` 退出播放；
7. 输出契约 JSON：验收点逐项 ✓/✗ + 问题清单（按严重度排序、附复现步骤）+ 总体结论。

## 约定（与 game-harness 对齐）

- 截图统一存 `docs/qa/screenshots/`，并在 artifacts 里登记 `kind: "image"`（harness 会再自动归档一份）；
- 你只有 `bash`/`read` 等只读工具，**不得修改被验证的代码和资产**，发现问题只报告；
- MCP 工具返回 JSON 文本时直接原文引用到报告，作为证据。

## 错误处理

- `无法连接 Unity MCP server` → 先跑 `install-plugin.mjs` + `start-unity.mjs`，再 `ping`；仍失败报告 blocked；
- `找不到 Unity.exe` → 用 `--unity <路径>` 指定，或确认目标工程 `ProjectVersion.txt` 版本已安装；
- `截屏需在播放模式下` → 先 `play` 再截屏；
- `run_tests` 当前仅支持 EditMode（PlayMode 待后续）；
- 工具返回 `isError` 或含 `"error"` 字段 → 把错误原文记入问题清单。
