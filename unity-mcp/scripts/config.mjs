/**
 * 共享配置：读 skill 根目录 config.json + 端口推导。
 * 端口规则：实际端口 = basePort + hash(工程路径) % 200 —— 同一工程恒定、不同工程错开，
 * 从而避免「多个 Unity 工程同时打开时 MCP server 抢同一个端口」的冲突。
 */
import * as fs from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";

const SCRIPT_DIR = path.dirname(fileURLToPath(import.meta.url));
const CONFIG_PATH = path.join(SCRIPT_DIR, "..", "config.json");

/** 读配置：{ port }，默认 6400；config.json 缺失/损坏时回退默认 */
export function loadConfig() {
	try {
		const c = JSON.parse(fs.readFileSync(CONFIG_PATH, "utf8"));
		const port = Number(c && c.port);
		return { port: Number.isInteger(port) && port > 0 && port < 65536 ? port : 6400 };
	} catch {
		return { port: 6400 };
	}
}

/** 由工程路径推导稳定端口（同一工程恒定，不同工程大概率错开） */
export function derivePort(projectPath, basePort) {
	const normalized = path.resolve(String(projectPath || "")).toLowerCase().replace(/[\\/]+$/, "");
	let h = 5381;
	for (let i = 0; i < normalized.length; i++) h = ((h * 33) ^ normalized.charCodeAt(i)) >>> 0;
	return basePort + (h % 200);
}

/** 解析目标工程路径 → 实际端口（start-unity 与客户端用同一规则，保证一致） */
export function projectPort(projectPath, basePort) {
	return derivePort(projectPath, basePort);
}
