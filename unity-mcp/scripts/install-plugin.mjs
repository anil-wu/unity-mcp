#!/usr/bin/env node
/**
 * 把 com.anil-wu.unity-mcp 插件装进目标 Unity 工程。
 * 用法: node install-plugin.mjs <目标工程路径> [--source git|file|copy] [--plugin <插件文件夹路径>]
 * 默认 --source git：用 GitHub 上的插件（需仓库 public，UPM 自动解析 test-framework 依赖）。
 */
import * as fs from "node:fs";
import * as path from "node:path";

const PKG = "com.anil-wu.unity-mcp";
const GIT_URL = "https://github.com/anil-wu/unity-mcp.git?path=/Packages/com.anil-wu.unity-mcp";

function parseArgv(argv) {
	const a = { _: [] };
	for (let i = 0; i < argv.length; i++) {
		const v = argv[i];
		if (v === "--source") a.source = argv[++i];
		else if (v === "--plugin") a.plugin = argv[++i];
		else if (v === "-h" || v === "--help") a.help = true;
		else a._.push(v);
	}
	return a;
}

function fail(msg) {
	console.error("✗ " + msg);
	process.exit(1);
}

function main() {
	const a = parseArgv(process.argv.slice(2));
	if (a.help || a._.length === 0) {
		console.log(`把 ${PKG} 装进目标 Unity 工程

用法: node install-plugin.mjs <目标工程路径> [--source git|file|copy] [--plugin <插件文件夹路径>]

  --source git   默认：用 GitHub 插件（${GIT_URL}）
  --source file  用本地插件文件夹（--plugin 必填），写入 file: 依赖
  --source copy  把插件文件夹直接拷进目标工程 Packages/（--plugin 必填）`);
		return;
	}

	const targetRoot = path.resolve(a._[0]);
	const manifestPath = path.join(targetRoot, "Packages", "manifest.json");
	if (!fs.existsSync(manifestPath)) fail(`不是 Unity 工程（缺 ${manifestPath}）`);

	const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
	manifest.dependencies = manifest.dependencies || {};
	if (manifest.dependencies[PKG]) {
		console.log(`✓ ${PKG} 已存在（${manifest.dependencies[PKG]}），无需重复安装`);
		return;
	}

	const source = a.source || "git";

	if (source === "copy") {
		const plugin = a.plugin ? path.resolve(a.plugin) : null;
		if (!plugin || !fs.existsSync(path.join(plugin, "package.json"))) fail("--source copy 需要 --plugin 指向插件文件夹（含 package.json）");
		const dst = path.join(targetRoot, "Packages", PKG);
		if (fs.existsSync(dst)) fail(`目标已存在: ${dst}`);
		fs.cpSync(plugin, dst, { recursive: true });
		console.log(`✓ 已拷贝插件到 ${dst}\n  回 Unity 等编译（MCP server 自动监听 6400）`);
		return;
	}

	let ref;
	if (source === "file") {
		const plugin = a.plugin ? path.resolve(a.plugin) : null;
		if (!plugin || !fs.existsSync(path.join(plugin, "package.json"))) fail("--source file 需要 --plugin 指向插件文件夹（含 package.json）");
		ref = "file:" + plugin;
	} else if (source === "git") {
		ref = GIT_URL;
	} else {
		fail(`未知 --source: ${source}`);
	}

	fs.copyFileSync(manifestPath, manifestPath + ".bak"); // 备份原 manifest
	manifest.dependencies[PKG] = ref;
	fs.writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + "\n");
	console.log(`✓ 已写入依赖: "${PKG}": "${ref}"`);
	console.log(`  已备份原 manifest 到 ${manifestPath}.bak`);
	console.log(`  回 Unity（会自动解析包），编辑器加载后 MCP server 监听 http://localhost:6400`);
}

main();
