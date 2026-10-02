using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using StudentAgeHarness.Engine;
using UnityEngine;

namespace StudentAgeHarness.Imaging
{
    /// <summary>
    /// 截图。主路径：WaitForEndOfFrame 之后 ScreenCapture.CaptureScreenshotAsTexture + EncodeToPNG 同步写盘；
    /// 备选：ScreenCapture.CaptureScreenshot 异步写文件再轮询。
    /// 截图前检查窗口分辨率是否等于本次运行要求的分辨率，不等时尝试改回来并在报告里留记录。
    /// </summary>
    internal static class HarnessScreenshot
    {
        private const float FallbackWaitSec = 3f;
        private const int BlankRetryFrames = 10;

        internal static IEnumerator Capture(string absolutePath, RunSettings settings, HarnessReport report,
            string step, string label, Action<bool> done)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath));
            yield return new WaitForEndOfFrame();

            if (settings != null && settings.HasExpectedResolution &&
                (Screen.width != settings.Width || Screen.height != settings.Height))
            {
                int beforeWidth = Screen.width, beforeHeight = Screen.height;
                string correctionError = null;
                try { Screen.SetResolution(settings.Width, settings.Height, FullScreenMode.Windowed); }
                catch (Exception ex) { correctionError = ex.Message; }
                yield return null;
                yield return null;
                yield return new WaitForEndOfFrame();
                bool corrected = Screen.width == settings.Width && Screen.height == settings.Height;
                report?.AddFinding(new HarnessFinding
                {
                    Severity = corrected ? HarnessSeverity.Info : HarnessSeverity.Warning,
                    Category = "harness",
                    Rule = "resolution-drift",
                    Path = label,
                    Message = corrected
                        ? "截图前发现窗口分辨率被改动，已改回要求的分辨率。"
                        : "截图前发现窗口分辨率和要求不一致，而且没能改回来；这张截图的尺寸不可信。"
                }
                .WithMetric("requestedWidth", settings.Width)
                .WithMetric("requestedHeight", settings.Height)
                .WithMetric("beforeWidth", beforeWidth)
                .WithMetric("beforeHeight", beforeHeight)
                .WithMetric("afterWidth", Screen.width)
                .WithMetric("afterHeight", Screen.height)
                .WithMetric("correctionError", correctionError));
            }

            // 后台窗口偶尔会交出一帧全黑的画面：最多再等几帧，仍然全黑就照样保存并记一条 warning。
            for (int attempt = 0; attempt <= BlankRetryFrames; attempt++)
            {
                bool lastAttempt = attempt == BlankRetryFrames;
                CaptureResult result = TryCaptureSync(absolutePath, lastAttempt);
                if (result == CaptureResult.Saved)
                {
                    done?.Invoke(true);
                    yield break;
                }
                if (result == CaptureResult.SavedBlank)
                {
                    report?.AddFinding(HarnessSeverity.Warning, "harness", "screenshot-blank",
                        "截图画面几乎全黑（窗口可能被最小化或没有渲染），已保存但不能当作界面证据。",
                        Path.GetFileName(absolutePath));
                    done?.Invoke(true);
                    yield break;
                }
                if (result == CaptureResult.Failed) break;
                yield return new WaitForEndOfFrame();
            }

            bool requested = false;
            try
            {
                ScreenCapture.CaptureScreenshot(absolutePath);
                requested = true;
            }
            catch (Exception ex)
            {
                HarnessLog.Warning("备选截图方式也失败：" + ex.Message);
            }
            if (!requested)
            {
                done?.Invoke(false);
                yield break;
            }
            float deadline = Time.realtimeSinceStartup + FallbackWaitSec;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (FileHasContent(absolutePath))
                {
                    done?.Invoke(true);
                    yield break;
                }
                yield return null;
            }
            done?.Invoke(false);
        }

        private enum CaptureResult
        {
            Saved,
            SavedBlank,
            Blank,
            Failed
        }

        private static CaptureResult TryCaptureSync(string absolutePath, bool keepBlank)
        {
            Texture2D texture = null;
            try
            {
                texture = ScreenCapture.CaptureScreenshotAsTexture();
                if (texture == null) return CaptureResult.Failed;
                bool blank = IsBlank(texture);
                if (blank && !keepBlank) return CaptureResult.Blank;
                byte[] png = ImageConversion.EncodeToPNG(texture);
                if (png == null || png.Length == 0) return CaptureResult.Failed;
                File.WriteAllBytes(absolutePath, png);
                return blank ? CaptureResult.SavedBlank : CaptureResult.Saved;
            }
            catch (Exception ex)
            {
                HarnessLog.Warning("截图主路径失败，改用备选方式：" + ex.Message);
                return CaptureResult.Failed;
            }
            finally
            {
                if (texture != null) UnityEngine.Object.Destroy(texture);
            }
        }

        private static bool IsBlank(Texture2D texture)
        {
            Color32[] pixels = texture.GetPixels32();
            if (pixels == null || pixels.Length == 0) return true;
            int step = Math.Max(1, pixels.Length / 2048);
            int visible = 0;
            for (int index = 0; index < pixels.Length; index += step)
            {
                Color32 pixel = pixels[index];
                if (pixel.r > 8 || pixel.g > 8 || pixel.b > 8)
                {
                    visible++;
                    if (visible >= 3) return false;
                }
            }
            return true;
        }

        private static bool FileHasContent(string path)
        {
            try { return File.Exists(path) && new FileInfo(path).Length > 0; }
            catch { return false; }
        }
    }
}
