using System;
using UnityEngine;

namespace UnityMcp
{
    /// <summary>
    /// 运行时桥：编辑器侧的 MCP server 通过这里的静态字段请求「运行中的游戏」执行截屏。
    /// 编辑器与运行时同处一个 AppDomain（Unity 编辑器进程），静态字段可跨编辑器/运行时代码共享。
    /// </summary>
    public sealed class RuntimeBridge : MonoBehaviour
    {
        public static volatile bool CaptureRequested;
        public static volatile string PendingScreenshotPath;
        public static volatile bool CaptureDone;
        public static volatile string CaptureResult;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Init()
        {
            var go = new GameObject("UnityMcpRuntimeBridge");
            DontDestroyOnLoad(go);
            go.AddComponent<RuntimeBridge>();
        }

        private void Update()
        {
            if (!CaptureRequested || string.IsNullOrEmpty(PendingScreenshotPath)) return;
            CaptureRequested = false;
            try
            {
                // CaptureScreenshotAsTexture 同步返回，EncodeToPNG 后自行写盘，避免异步写盘时序不确定
                var tex = ScreenCapture.CaptureScreenshotAsTexture();
                var bytes = tex.EncodeToPNG();
                Destroy(tex);
                System.IO.File.WriteAllBytes(PendingScreenshotPath, bytes);
                CaptureResult = "ok";
            }
            catch (Exception e)
            {
                CaptureResult = "error: " + e.Message;
            }
            CaptureDone = true;
        }
    }
}
