#!/usr/bin/env node
/**
 * Unity MCP 客户端（QA/验收用）
 * 通过 HTTP + JSON-RPC 调用 Unity 编辑器内嵌的 MCP server（本仓库 Packages/com.anil-wu.unity-mcp）。
 * 零依赖，Node ≥ 18。
 *
 * 用法见 SKILL.md，或运行 `node unity-mcp.mjs --help`。
 */

import { loadConfig, projectPort } from "./config.mjs";

const config = loadConfig();
let MCP_URL = "http://localhost:6400/mcp";

/** 解析连接地址：UNITY_MCP_URL（完整地址，最高优先）> --port / UNITY_MCP_PORT > 按工程路径推导 */
function resolveMcpUrl(args) {
	if (process.env.UNITY_MCP_URL) return process.env.UNITY_MCP_URL;
	const p = args.port ?? process.env.UNITY_MCP_PORT;
	const port = p ? Number(p) : projectPort(process.cwd(), config.port);
	return `http://localhost:${port}/mcp`;
}

async function rpc(method, params) {
	let res;
	try {
		res = await fetch(MCP_URL, {
			method: "POST",
			headers: { "Content-Type": "application/json" },
			body: JSON.stringify({ jsonrpc: "2.0", id: Date.now(), method, params }),
		});
	} catch (e) {
		throw new Error(`无法连接 Unity MCP server（${MCP_URL}）：${e.message}\n` +
			`请确认：1) Unity 编辑器已打开本工程；2) MCP server 已启动（默认端口 6400）。`);
	}
	if (res.status === 202) return null; // 通知无响应
	const text = await res.text();
	if (!text) throw new Error(`HTTP ${res.status}：空响应`);
	let json;
	try { json = JSON.parse(text); } catch { throw new Error(`非 JSON 响应（HTTP ${res.status}）：${text.slice(0, 200)}`); }
	if (json.error) throw new Error(`JSON-RPC 错误 ${json.error.code}: ${json.error.message}`);
	return json.result;
}

async function callTool(name, args) {
	const result = await rpc("tools/call", { name, arguments: args ?? {} });
	const content = result?.content ?? [];
	const text = content.map((c) => (c && c.text != null ? c.text : "")).join("");
	if (result?.isError) throw new Error(text || `工具 ${name} 执行失败`);
	return text;
}

function out(s) { process.stdout.write(String(s).trimEnd() + "\n"); }

const help = `Unity MCP 客户端

用法: node unity-mcp.mjs <command> [选项]

命令:
  ping                            健康检查（Unity 是否就绪）
  tools                           列出 MCP 工具
  play | pause | resume | stop    播放模式控制
  console [--level <级别>] [--count <N>]   拉取控制台日志（级别: Error/Warning/Log/Assert/Exception）
  scene                           只读场景信息
  tree                            场景 GameObject 层级树
  components --name <名称> | --path <路径>   查询某 GameObject 的组件
  screenshot --out <路径>         截取游戏画面保存为 PNG（需播放模式）
  test [--testNames <a,b>] [--category <c>]  运行 EditMode 测试
  call <工具名> '<json 参数>'     通用工具调用

端口: 默认按工程路径推导（config.json 的 port 为基准 + hash%200，多工程自动错开）；可用 --port <N> 或 UNITY_MCP_PORT 覆盖；UNITY_MCP_URL 为完整地址（最高优先）`;

function parseArgv(argv) {
	const a = { _: [] };
	for (let i = 0; i < argv.length; i++) {
		const v = argv[i];
		if (v === "--help" || v === "-h") { a.help = true; continue; }
		if (v.startsWith("--")) {
			const key = v.slice(2);
			const next = argv[i + 1];
			if (next === undefined || next.startsWith("--")) a[key] = true;
			else a[key] = next, i++;
		} else a._.push(v);
	}
	return a;
}

async function main() {
	const a = parseArgv(process.argv.slice(2));
	if (a.help || a._.length === 0) { out(help); return; }
	MCP_URL = resolveMcpUrl(a);
	const cmd = a._[0];

	try {
		switch (cmd) {
			case "ping": {
				const r = await rpc("tools/call", { name: "ping", arguments: {} });
				out(r.content?.[0]?.text ?? JSON.stringify(r));
				return;
			}
			case "tools": {
				const r = await rpc("tools/list");
				const tools = r?.tools ?? [];
				if (tools.length === 0) { out("（无工具）"); return; }
				out(`共 ${tools.length} 个工具：`);
				for (const t of tools) out(`  - ${t.name}: ${t.description}`);
				return;
			}
			case "play": out(await callTool("editor_play")); return;
			case "pause": out(await callTool("editor_pause", { paused: true })); return;
			case "resume": out(await callTool("editor_pause", { paused: false })); return;
			case "stop": out(await callTool("editor_stop")); return;
			case "console": {
				const args = {};
				if (a.level) args.level = a.level;
				if (a.count) args.count = parseInt(a.count, 10);
				out(await callTool("console_logs", args));
				return;
			}
			case "scene": out(await callTool("scene_info")); return;
			case "tree": out(await callTool("scene_tree")); return;
			case "components": {
				const args = {};
				if (a.name) args.name = a.name;
				if (a.path) args.path = a.path;
				if (!a.name && !a.path) { out("✗ 需要 --name 或 --path"); process.exitCode = 1; return; }
				out(await callTool("get_components", args));
				return;
			}
			case "screenshot": {
				if (!a.out) { out("✗ 需要 --out <路径>"); process.exitCode = 1; return; }
				out(await callTool("capture_screenshot", { path: a.out }));
				return;
			}
			case "test": {
				const args = {};
				if (a.testNames) args.testNames = a.testNames;
				if (a.category) args.category = a.category;
				out(await callTool("run_tests", args));
				return;
			}
			case "call": {
				const tool = a._[1];
				if (!tool) { out("✗ 需要工具名: call <工具名> '<json>'"); process.exitCode = 1; return; }
				let args = {};
				if (a._[2]) { try { args = JSON.parse(a._[2]); } catch { out("✗ 参数不是合法 JSON"); process.exitCode = 1; return; } }
				out(await callTool(tool, args));
				return;
			}
			default:
				out(`✗ 未知命令: ${cmd}\n\n${help}`);
				process.exitCode = 1;
		}
	} catch (e) {
		out("✗ " + (e && e.message ? e.message : e));
		process.exitCode = 1;
	}
}

main();
