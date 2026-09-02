using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace UnityMcp
{
    /// <summary>
    /// 通过 Unity Test Framework 运行测试（异步：Test Runner 回调在主线程，这里封装成 McpServer 的 pending poll）。
    /// v1 仅支持 EditMode（PlayMode 需独立 player，后续再做）。
    /// </summary>
    internal static class McpTestRunner
    {
        public static string LastError;

        private sealed class Callbacks : ICallbacks
        {
            public bool finished;
            public int passed, failed, skipped, inconclusive;
            public readonly List<Dictionary<string, object>> failures = new List<Dictionary<string, object>>();

            public void RunStarted(ITestAdaptor testsToRun) { }

            public void RunFinished(ITestResultAdaptor result)
            {
                finished = true;
            }

            public void TestStarted(ITestAdaptor test) { }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (result.HasChildren) return; // 只统计叶子用例
                switch (result.TestStatus)
                {
                    case TestStatus.Passed: passed++; break;
                    case TestStatus.Failed: failed++; break;
                    case TestStatus.Skipped: skipped++; break;
                    case TestStatus.Inconclusive: inconclusive++; break;
                }
                if (result.TestStatus == TestStatus.Failed)
                {
                    failures.Add(new Dictionary<string, object>
                    {
                        { "name", result.FullName ?? result.Name },
                        { "message", result.Message },
                        { "stackTrace", result.StackTrace },
                    });
                }
            }
        }

        private sealed class State
        {
            public TestRunnerApi api;
            public Callbacks callbacks;
        }

        /// <summary>开始运行测试；返回 poll 函数（非 null 成功启动），失败返回 null 并设置 LastError。</summary>
        public static Func<McpServer.PollResult> Begin(Dictionary<string, object> args)
        {
            LastError = null;

            var filter = new Filter { testMode = TestMode.EditMode };
            var testNames = GetStr(args, "testNames");
            if (!string.IsNullOrEmpty(testNames)) filter.testNames = testNames.Split(',');
            var category = GetStr(args, "category");
            if (!string.IsNullOrEmpty(category)) filter.categoryNames = new[] { category };

            TestRunnerApi api;
            try
            {
                api = ScriptableObject.CreateInstance<TestRunnerApi>();
            }
            catch (Exception e)
            {
                LastError = MiniJson.Serialize(new Dictionary<string, object> { { "error", "创建 TestRunnerApi 失败: " + e.Message } });
                return null;
            }

            var callbacks = new Callbacks();
            var state = new State { api = api, callbacks = callbacks };
            api.RegisterCallbacks(callbacks);

            try
            {
                api.Execute(new ExecutionSettings(filter));
            }
            catch (Exception e)
            {
                LastError = MiniJson.Serialize(new Dictionary<string, object> { { "error", "执行测试失败: " + e.Message } });
                return null;
            }

            return () =>
            {
                var c = state.callbacks;
                if (!c.finished) return null;
                var result = new Dictionary<string, object>
                {
                    { "passed", c.passed },
                    { "failed", c.failed },
                    { "skipped", c.skipped },
                    { "inconclusive", c.inconclusive },
                    { "total", c.passed + c.failed + c.skipped + c.inconclusive },
                    { "failures", c.failures },
                };
                return new McpServer.PollResult { text = MiniJson.Serialize(result), isError = false };
            };
        }

        private static string GetStr(Dictionary<string, object> args, string key)
        {
            if (args != null && args.TryGetValue(key, out var v) && v != null) return Convert.ToString(v);
            return null;
        }
    }
}
