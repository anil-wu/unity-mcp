using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityMcp
{
    /// <summary>
    /// 内嵌 MCP server：HTTP + JSON-RPC 2.0，把编辑器操作暴露为工具，供 AI agent 驱动 Unity 做运行态验收。
    /// 线程模型：HTTP 监听在后台线程，工具执行统一回主线程（EditorApplication.update 轮询队列），
    /// 异步工具（截屏等）注册为 pending job，主线程每帧 poll 完成。
    /// </summary>
    [InitializeOnLoad]
    public static class McpServer
    {
        private const int DefaultPort = 6400;
        private const string ProtocolVersion = "2024-11-05";
        private const string ServerName = "unity-mcp";
        private const string ServerVersion = "0.1.0";
        private const int RequestTimeoutMs = 30000;
        private const int MaxLogBuffer = 1000;

        private static volatile HttpListener _listener;
        private static readonly object _jobsLock = new object();
        private static readonly Queue<McpJob> _jobs = new Queue<McpJob>();
        private static readonly List<PendingJob> _pendingJobs = new List<PendingJob>();

        private static readonly List<LogEntry> _logBuffer = new List<LogEntry>();
        private static readonly object _logLock = new object();

        // ==================== 数据结构 ====================

        private sealed class LogEntry
        {
            public string type;
            public string message;
            public string stackTrace;
            public double time;
        }

        private sealed class McpJob
        {
            public object id;
            public bool isNotification;
            public Dictionary<string, object> request;
            public object response;
            public readonly ManualResetEvent doneEvent = new ManualResetEvent(false);
        }

        private sealed class PendingJob
        {
            public McpJob job;
            public Func<PollResult> poll;
            public long deadlineMs;
        }

        internal sealed class PollResult
        {
            public string text;
            public bool isError;
        }

        private sealed class Tool
        {
            public string name;
            public string description;
            public Dictionary<string, object> inputSchema;
            public Action<Dictionary<string, object>, McpJob> handler;
        }

        private static readonly List<Tool> _tools = new List<Tool>();

        // ==================== 启动 ====================

        static McpServer()
        {
            RegisterTools();
            Application.logMessageReceived += OnLog;
            EditorApplication.update += Tick;
            // 域重载（脚本重编译）时主动关闭监听，避免旧 HttpListener 占用端口导致新域绑定失败
            AppDomain.CurrentDomain.DomainUnload += (_, _) => CloseListener();
            StartListener();
        }

        private static void CloseListener()
        {
            var l = _listener;
            _listener = null;
            if (l == null) return;
            try { if (l.IsListening) l.Stop(); } catch { /* 忽略 */ }
            try { l.Close(); } catch { /* 忽略 */ }
        }

        private static void StartListener()
        {
            // 后台线程创建 + 退避重试：域重载后 http.sys 可能延迟释放端口，避免阻塞主线程
            var thread = new Thread(() =>
            {
                for (var attempt = 0; attempt < 6; attempt++)
                {
                    if (_listener != null) return; // 已就绪或已关闭
                    try
                    {
                        var l = new HttpListener();
                        l.Prefixes.Add($"http://localhost:{DefaultPort}/");
                        l.Prefixes.Add($"http://127.0.0.1:{DefaultPort}/");
                        l.Start();
                        _listener = l;
                        Debug.Log($"[UnityMcp] MCP server 监听 http://localhost:{DefaultPort}/mcp");
                        ListenLoop();
                        return;
                    }
                    catch (Exception e)
                    {
                        if (attempt == 5) Debug.LogWarning($"[UnityMcp] 启动 HTTP 监听失败: {e.Message}");
                        else Thread.Sleep(1000 * (attempt + 1));
                    }
                }
            })
            { IsBackground = true, Name = "UnityMcp-Listener" };
            thread.Start();
        }

        private static void ListenLoop()
        {
            while (_listener != null && _listener.IsListening)
            {
                try
                {
                    var ctx = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(state => HandleRequest(ctx));
                }
                catch
                {
                    break; // listener 关闭
                }
            }
        }

        // ==================== HTTP 请求处理（后台线程） ====================

        private static void HandleRequest(HttpListenerContext ctx)
        {
            object responseObj;
            try
            {
                if (ctx.Request.HttpMethod != "POST")
                {
                    Respond(ctx, 405, "{\"error\":\"仅支持 POST\"}");
                    return;
                }
                var body = new StreamReader(ctx.Request.InputStream, Encoding.UTF8).ReadToEnd();
                var req = MiniJson.Deserialize(body) as Dictionary<string, object>;
                if (req == null)
                {
                    Respond(ctx, 400, "{\"error\":\"请求体不是 JSON 对象\"}");
                    return;
                }

                var job = new McpJob
                {
                    id = req.ContainsKey("id") ? req["id"] : null,
                    isNotification = !req.ContainsKey("id"),
                    request = req,
                };

                lock (_jobsLock) _jobs.Enqueue(job);
                if (!job.doneEvent.WaitOne(RequestTimeoutMs))
                {
                    // 超时（理论上主线程总会处理；防御性兜底）
                    responseObj = BuildError(job.id, -32000, "主线程处理超时");
                }
                else
                {
                    responseObj = job.response;
                }
            }
            catch (Exception e)
            {
                responseObj = BuildError(null, -32700, "解析错误: " + e.Message);
            }

            if (responseObj == null)
            {
                // 通知（无 id）不返回 JSON-RPC 响应
                Respond(ctx, 202, "");
            }
            else
            {
                Respond(ctx, 200, MiniJson.Serialize(responseObj));
            }
        }

        private static void Respond(HttpListenerContext ctx, int status, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }

        // ==================== 主线程 tick ====================

        private static void Tick()
        {
            // 1. 处理排队请求（同步工具在此完成）
            while (true)
            {
                McpJob job;
                lock (_jobsLock)
                {
                    if (_jobs.Count == 0) break;
                    job = _jobs.Dequeue();
                }
                try
                {
                    Dispatch(job);
                }
                catch (Exception e)
                {
                    Complete(job, "{\"error\":\"" + Escape(e.Message) + "\"}", true);
                }
            }

            // 2. poll 异步工具（截屏等）
            if (_pendingJobs.Count > 0)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                for (var i = _pendingJobs.Count - 1; i >= 0; i--)
                {
                    var p = _pendingJobs[i];
                    var result = p.poll();
                    if (result != null)
                    {
                        _pendingJobs.RemoveAt(i);
                        Complete(p.job, result.text, result.isError);
                    }
                    else if (now >= p.deadlineMs)
                    {
                        _pendingJobs.RemoveAt(i);
                        Complete(p.job, "{\"error\":\"异步工具超时\"}", true);
                    }
                }
            }
        }

        private static void Dispatch(McpJob job)
        {
            var method = job.request.ContainsKey("method") ? job.request["method"] as string : null;
            if (string.IsNullOrEmpty(method))
            {
                Complete(job, "{\"error\":\"缺少 method\"}", true);
                return;
            }

            if (job.isNotification)
            {
                // 通知（如 notifications/initialized）：不响应
                job.response = null;
                job.doneEvent.Set();
                return;
            }

            switch (method)
            {
                case "initialize":
                    HandleInitialize(job);
                    break;
                case "ping":
                    job.response = BuildResult(job.id, new Dictionary<string, object>());
                    job.doneEvent.Set();
                    break;
                case "tools/list":
                    HandleToolsList(job);
                    break;
                case "tools/call":
                    HandleToolsCall(job);
                    break;
                default:
                    Complete(job, "{\"error\":\"未知方法: " + Escape(method) + "\"}", true);
                    break;
            }
        }

        // ==================== MCP 方法处理 ====================

        private static void HandleInitialize(McpJob job)
        {
            var result = new Dictionary<string, object>
            {
                { "protocolVersion", ProtocolVersion },
                { "capabilities", new Dictionary<string, object> { { "tools", new Dictionary<string, object>() } } },
                { "serverInfo", new Dictionary<string, object> { { "name", ServerName }, { "version", ServerVersion } } },
            };
            job.response = BuildResult(job.id, result);
            job.doneEvent.Set();
        }

        private static void HandleToolsList(McpJob job)
        {
            var list = new List<object>();
            foreach (var t in _tools)
            {
                list.Add(new Dictionary<string, object>
                {
                    { "name", t.name },
                    { "description", t.description },
                    { "inputSchema", t.inputSchema },
                });
            }
            job.response = BuildResult(job.id, new Dictionary<string, object> { { "tools", list } });
            job.doneEvent.Set();
        }

        private static void HandleToolsCall(McpJob job)
        {
            var parameters = job.request.ContainsKey("params") ? job.request["params"] as Dictionary<string, object> : null;
            if (parameters == null)
            {
                Complete(job, "{\"error\":\"缺少 params\"}", true);
                return;
            }
            var name = parameters.ContainsKey("name") ? parameters["name"] as string : null;
            var arguments = parameters.ContainsKey("arguments") ? parameters["arguments"] as Dictionary<string, object> : null;
            arguments = arguments ?? new Dictionary<string, object>();

            foreach (var t in _tools)
            {
                if (t.name == name)
                {
                    t.handler(arguments, job);
                    return;
                }
            }
            Complete(job, "{\"error\":\"未知工具: " + Escape(name ?? "") + "\"}", true);
        }

        // ==================== 工具注册 ====================

        private static void RegisterTools()
        {
            _tools.Add(new Tool
            {
                name = "ping",
                description = "健康检查：返回编辑器状态（是否播放中、活动场景、Unity 版本）。",
                inputSchema = Obj(),
                handler = (args, job) => Complete(job, MiniJson.Serialize(EditorState()), false),
            });

            _tools.Add(new Tool
            {
                name = "editor_play",
                description = "进入播放模式。",
                inputSchema = Obj(),
                handler = (args, job) =>
                {
                    if (EditorApplication.isPlaying) { Complete(job, JsonResult("already_playing")); return; }
                    EditorApplication.EnterPlaymode();
                    Complete(job, JsonResult("play_requested"));
                },
            });

            _tools.Add(new Tool
            {
                name = "editor_pause",
                description = "设置播放模式暂停状态。",
                inputSchema = Obj(("paused", "boolean 是否暂停")),
                handler = (args, job) =>
                {
                    if (!EditorApplication.isPlaying) { Complete(job, JsonError("未在播放模式"), true); return; }
                    var paused = GetBool(args, "paused", !EditorApplication.isPaused);
                    EditorApplication.isPaused = paused;
                    Complete(job, JsonResult(paused ? "paused" : "resumed"));
                },
            });

            _tools.Add(new Tool
            {
                name = "editor_stop",
                description = "退出播放模式。",
                inputSchema = Obj(),
                handler = (args, job) =>
                {
                    if (!EditorApplication.isPlaying) { Complete(job, JsonResult("already_stopped")); return; }
                    EditorApplication.ExitPlaymode();
                    Complete(job, JsonResult("stop_requested"));
                },
            });

            _tools.Add(new Tool
            {
                name = "console_logs",
                description = "拉取缓冲的控制台日志，可按级别过滤。",
                inputSchema = Obj(
                    ("level", "可选：Error/Assert/Warning/Log/Exception"),
                    ("count", "可选：最多返回条数，默认 200")
                ),
                handler = (args, job) =>
                {
                    var level = GetString(args, "level", null);
                    var count = GetInt(args, "count", 200);
                    var result = new List<object>();
                    lock (_logLock)
                    {
                        for (var i = _logBuffer.Count - 1; i >= 0 && result.Count < count; i--)
                        {
                            var e = _logBuffer[i];
                            if (level != null && !string.Equals(e.type, level, StringComparison.OrdinalIgnoreCase)) continue;
                            result.Add(new Dictionary<string, object>
                            {
                                { "type", e.type },
                                { "message", e.message },
                                { "stackTrace", e.stackTrace },
                                { "time", e.time },
                            });
                        }
                    }
                    result.Reverse();
                    Complete(job, MiniJson.Serialize(new Dictionary<string, object> { { "count", result.Count }, { "logs", result } }), false);
                },
            });

            _tools.Add(new Tool
            {
                name = "scene_info",
                description = "只读场景信息：活动场景名、根对象数、构建场景列表。",
                inputSchema = Obj(),
                handler = (args, job) =>
                {
                    var scene = EditorSceneManager.GetActiveScene();
                    var roots = scene.IsValid() ? scene.GetRootGameObjects().Length : 0;
                    var buildScenes = new List<string>();
                    foreach (var s in EditorBuildSettings.scenes) buildScenes.Add(s.path);
                    Complete(job, MiniJson.Serialize(new Dictionary<string, object>
                    {
                        { "activeScene", scene.name },
                        { "rootCount", roots },
                        { "buildScenes", buildScenes },
                    }), false);
                },
            });

            _tools.Add(new Tool
            {
                name = "scene_tree",
                description = "返回活动场景的完整 GameObject 层级树（名称/路径/active/tag/组件类型/子节点）。",
                inputSchema = Obj(),
                handler = (args, job) =>
                {
                    var scene = EditorSceneManager.GetActiveScene();
                    var roots = new List<object>();
                    if (scene.IsValid())
                    {
                        foreach (var go in scene.GetRootGameObjects())
                        {
                            roots.Add(BuildTreeNode(go.transform));
                        }
                    }
                    Complete(job, MiniJson.Serialize(new Dictionary<string, object>
                    {
                        { "activeScene", scene.name },
                        { "rootCount", roots.Count },
                        { "tree", roots },
                    }), false);
                },
            });

            _tools.Add(new Tool
            {
                name = "get_components",
                description = "查询场景树上一个 GameObject 的组件信息（按名称或路径定位）。",
                inputSchema = Obj(
                    ("name", "GameObject 名称"),
                    ("path", "完整路径（如 /Main Camera/Child），与 name 二选一")
                ),
                handler = (args, job) =>
                {
                    var name = GetString(args, "name", null);
                    var pathStr = GetString(args, "path", null);
                    if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(pathStr))
                    {
                        Complete(job, JsonError("需要 name 或 path 之一"), true);
                        return;
                    }
                    var go = FindGameObject(name, pathStr);
                    if (go == null)
                    {
                        Complete(job, JsonError("未找到 GameObject（name=" + (name ?? "") + ", path=" + (pathStr ?? "") + "）"), true);
                        return;
                    }
                    var comps = new List<object>();
                    foreach (var c in go.GetComponents<Component>())
                    {
                        if (c == null) continue;
                        var info = new Dictionary<string, object> { { "type", c.GetType().FullName } };
                        if (c is Behaviour behaviour) info["enabled"] = behaviour.enabled;
                        comps.Add(info);
                    }
                    Complete(job, MiniJson.Serialize(new Dictionary<string, object>
                    {
                        { "name", go.name },
                        { "path", GetGameObjectPath(go.transform) },
                        { "active", go.activeSelf },
                        { "components", comps },
                    }), false);
                },
            });

            _tools.Add(new Tool
            {
                name = "execute_menu",
                description = "执行编辑器菜单项（如 File/Save Project）。",
                inputSchema = Obj(("path", "菜单路径，如 File/Save Project")),
                handler = (args, job) =>
                {
                    var path = GetString(args, "path", null);
                    if (string.IsNullOrEmpty(path)) { Complete(job, JsonError("缺少 path"), true); return; }
                    var ok = EditorApplication.ExecuteMenuItem(path);
                    Complete(job, MiniJson.Serialize(new Dictionary<string, object> { { "ok", ok } }), false);
                },
            });

            _tools.Add(new Tool
            {
                name = "capture_screenshot",
                description = "截取游戏画面保存为 PNG（需在播放模式下）。",
                inputSchema = Obj(("path", "输出文件绝对路径或相对项目根的路径")),
                handler = CaptureScreenshot,
            });

            _tools.Add(new Tool
            {
                name = "run_tests",
                description = "运行 Unity Test Framework 用例（v1 仅 EditMode）。返回通过/失败/跳过统计与失败详情。",
                inputSchema = Obj(
                    ("testNames", "可选：逗号分隔的测试全名过滤"),
                    ("category", "可选：按分类过滤")
                ),
                handler = (args, job) =>
                {
                    var poll = McpTestRunner.Begin(args);
                    if (poll == null) { Complete(job, McpTestRunner.LastError ?? "{\"error\":\"启动测试失败\"}", true); return; }
                    RegisterPending(job, poll);
                },
            });
        }

        // ==================== 工具实现 ====================

        private static Dictionary<string, object> EditorState()
        {
            var scene = EditorSceneManager.GetActiveScene();
            return new Dictionary<string, object>
            {
                { "ok", true },
                { "unityVersion", Application.unityVersion },
                { "isPlaying", EditorApplication.isPlaying },
                { "isPaused", EditorApplication.isPaused },
                { "activeScene", scene.name },
            };
        }

        private static void CaptureScreenshot(Dictionary<string, object> args, McpJob job)
        {
            var path = GetString(args, "path", null);
            if (string.IsNullOrEmpty(path))
            {
                Complete(job, JsonError("缺少 path 参数"), true);
                return;
            }
            if (!EditorApplication.isPlaying)
            {
                Complete(job, JsonError("截屏需在播放模式下（先调用 editor_play）"), true);
                return;
            }
            // 相对路径基于项目根
            var abs = Path.IsPathRooted(path) ? path : Path.Combine(Directory.GetCurrentDirectory(), path);
            RuntimeBridge.CaptureDone = false;
            RuntimeBridge.CaptureResult = null;
            RuntimeBridge.PendingScreenshotPath = abs;
            RuntimeBridge.CaptureRequested = true;

            RegisterPending(job, () =>
            {
                if (!RuntimeBridge.CaptureDone) return null;
                if (RuntimeBridge.CaptureResult == "ok")
                {
                    return new PollResult
                    {
                        text = MiniJson.Serialize(new Dictionary<string, object> { { "ok", true }, { "path", path } }),
                        isError = false,
                    };
                }
                return new PollResult
                {
                    text = MiniJson.Serialize(new Dictionary<string, object> { { "ok", false }, { "error", RuntimeBridge.CaptureResult } }),
                    isError = true,
                };
            });
        }

        // ==================== 场景树 / 组件查询辅助 ====================

        private static Dictionary<string, object> BuildTreeNode(Transform t)
        {
            var go = t.gameObject;
            var node = new Dictionary<string, object>
            {
                { "name", go.name },
                { "path", GetGameObjectPath(t) },
                { "active", go.activeSelf },
                { "tag", go.tag },
                { "layer", go.layer },
                { "components", GetComponentTypeNames(go) },
            };
            var children = new List<object>();
            foreach (Transform child in t)
            {
                children.Add(BuildTreeNode(child));
            }
            node["children"] = children;
            return node;
        }

        private static List<string> GetComponentTypeNames(GameObject go)
        {
            var names = new List<string>();
            foreach (var c in go.GetComponents<Component>())
            {
                if (c != null) names.Add(c.GetType().Name);
            }
            return names;
        }

        private static string GetGameObjectPath(Transform t)
        {
            var parts = new List<string>();
            while (t != null)
            {
                parts.Insert(0, t.name);
                t = t.parent;
            }
            return "/" + string.Join("/", parts);
        }

        private static GameObject FindGameObject(string name, string pathStr)
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid()) return null;
            foreach (var go in scene.GetRootGameObjects())
            {
                if (!string.IsNullOrEmpty(pathStr))
                {
                    var byPath = FindByPath(go.transform, pathStr);
                    if (byPath != null) return byPath;
                }
                if (!string.IsNullOrEmpty(name))
                {
                    var byName = FindByName(go.transform, name);
                    if (byName != null) return byName;
                }
            }
            return null;
        }

        private static GameObject FindByPath(Transform t, string pathStr)
        {
            if (GetGameObjectPath(t) == pathStr) return t.gameObject;
            foreach (Transform child in t)
            {
                var found = FindByPath(child, pathStr);
                if (found != null) return found;
            }
            return null;
        }

        private static GameObject FindByName(Transform t, string name)
        {
            if (t.name == name) return t.gameObject;
            foreach (Transform child in t)
            {
                var found = FindByName(child, name);
                if (found != null) return found;
            }
            return null;
        }

        // ==================== 结果/响应辅助 ====================

        private static void Complete(McpJob job, string text, bool isError = false)
        {
            job.response = BuildCallResult(job.id, text, isError);
            job.doneEvent.Set();
        }

        private static void RegisterPending(McpJob job, Func<PollResult> poll)
        {
            lock (_jobsLock)
            {
                _pendingJobs.Add(new PendingJob
                {
                    job = job,
                    poll = poll,
                    deadlineMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + RequestTimeoutMs,
                });
            }
        }

        private static object BuildResult(object id, object result)
        {
            return new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "id", id },
                { "result", result },
            };
        }

        private static object BuildCallResult(object id, string text, bool isError)
        {
            var content = new Dictionary<string, object> { { "type", "text" }, { "text", text } };
            var result = new Dictionary<string, object>
            {
                { "content", new List<object> { content } },
                { "isError", isError },
            };
            return BuildResult(id, result);
        }

        private static object BuildError(object id, int code, string message)
        {
            return new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "id", id },
                { "error", new Dictionary<string, object> { { "code", code }, { "message", message } } },
            };
        }

        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            lock (_logLock)
            {
                _logBuffer.Add(new LogEntry
                {
                    type = type.ToString(),
                    message = condition,
                    stackTrace = stackTrace,
                    time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                });
                if (_logBuffer.Count > MaxLogBuffer) _logBuffer.RemoveAt(0);
            }
        }

        // ==================== 参数/JSON 小工具 ====================

        private static Dictionary<string, object> Obj(params (string, string)[] props)
        {
            var properties = new Dictionary<string, object>();
            foreach (var (k, v) in props)
            {
                properties[k] = new Dictionary<string, object> { { "type", "string" }, { "description", v } };
            }
            return new Dictionary<string, object> { { "type", "object" }, { "properties", properties } };
        }

        private static string JsonResult(string s)
        {
            return MiniJson.Serialize(new Dictionary<string, object> { { "result", s } });
        }

        private static string JsonError(string msg)
        {
            return MiniJson.Serialize(new Dictionary<string, object> { { "error", msg } });
        }

        private static string GetString(Dictionary<string, object> args, string key, string def)
        {
            if (args != null && args.TryGetValue(key, out var v) && v != null) return Convert.ToString(v);
            return def;
        }

        private static bool GetBool(Dictionary<string, object> args, string key, bool def)
        {
            if (args != null && args.TryGetValue(key, out var v) && v is bool b) return b;
            if (args != null && args.TryGetValue(key, out var v2) && v2 != null)
            {
                if (bool.TryParse(Convert.ToString(v2), out var parsed)) return parsed;
            }
            return def;
        }

        private static int GetInt(Dictionary<string, object> args, string key, int def)
        {
            if (args != null && args.TryGetValue(key, out var v))
            {
                try { return Convert.ToInt32(v); } catch { /* fallthrough */ }
            }
            return def;
        }

        private static string Escape(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }
    }
}
