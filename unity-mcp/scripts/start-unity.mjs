#!/usr/bin/env node
/**
 * 启动 Unity 编辑器打开目标工程（编辑器加载后 MCP server 自动监听 6400）。
 * 用法: node start-unity.mjs <目标工程路径> [--unity <Unity.exe 路径>]
 * 自动按工程 ProjectVersion.txt 的版本在 Unity Hub 安装目录里找 Unity.exe。
 */
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import { spawn } from "node:child_process";

function parseArgv(argv) {
	const a = { _: [] };
	for (let i = 0; i < argv.length; i++) {
		const v = argv[i];
		if (v === "--unity") a.unity = argv[++i];
		else if (v === "-h" || v === "--help") a.help = true;
		else a._.push(v);
	}
	return a;
}

function fail(msg) {
	console.error("✗ " + msg);
	process.exit(1);
}

function readVersion(targetRoot) {
	const f = path.join(targetRoot, "ProjectSettings", "ProjectVersion.txt");
	if (!fs.existsSync(f)) return null;
	const m = /m_EditorVersion:\s*(\S+)/.exec(fs.readFileSync(f, "utf8"));
	return m ? m[1] : null;
}

/** 候选 Unity.exe 路径（按工程版本） */
function candidatePaths(version) {
	const list = [];
	if (version) {
		// Unity Hub 默认安装目录
		list.push(path.join("C:", "Program Files", "Unity", "Hub", "Editor", version, "Editor", "Unity.exe"));
		// Unity Hub 自定义安装目录（secondaryInstallPath.json）
		try {
			const cfg = path.join(os.homedir(), "AppData", "Roaming", "UnityHub", "secondaryInstallPath.json");
			if (fs.existsSync(cfg)) {
				const base = JSON.parse(fs.readFileSync(cfg, "utf8"));
				if (typeof base === "string" && base.trim()) {
					list.push(path.join(base.trim(), version, "Editor", "Unity.exe"));
				}
			}
		} catch { /* 忽略配置读取失败 */ }
	}
	// 非 Hub 默认安装
	list.push(path.join("C:", "Program Files", "Unity", "Editor", "Unity.exe"));
	return list;
}

function main() {
	const a = parseArgv(process.argv.slice(2));
	if (a.help || a._.length === 0) {
		console.log(`启动 Unity 编辑器打开目标工程

用法: node start-unity.mjs <目标工程路径> [--unity <Unity.exe 路径>]`);
		return;
	}

	const targetRoot = path.resolve(a._[0]);
	if (!fs.existsSync(path.join(targetRoot, "Assets"))) fail(`不是 Unity 工程（缺 Assets/）：${targetRoot}`);

	let unityExe = a.unity;
	if (!unityExe) {
		const version = readVersion(targetRoot);
		for (const p of candidatePaths(version)) {
			if (fs.existsSync(p)) { unityExe = p; break; }
		}
	}
	if (!unityExe || !fs.existsSync(unityExe)) {
		fail(`找不到 Unity.exe。请用 --unity <路径> 指定（例如 D:/Programs/Unity/${readVersion(targetRoot) || "2022.3.62f3"}/Editor/Unity.exe）`);
	}

	const child = spawn(unityExe, ["-projectPath", targetRoot], { detached: true, stdio: "ignore" });
	child.unref();
	console.log(`✓ 已启动 Unity: ${unityExe}`);
	console.log(`  工程: ${targetRoot}`);
	console.log(`  编辑器加载后 MCP server 监听 http://localhost:6400（用 unity-mcp.mjs ping 验证）`);
}

main();
