using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using System.Runtime.InteropServices;
using SourceGit.ViewModels;
using SourceGit.Views;

internal static class UiTimeChecks
{
    internal static void Run(bool nativeWindows = false)
    {
        if (nativeWindows && !OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native window verification requires Windows");
        var builder = AppBuilder.Configure<Application>();
        if (nativeWindows) builder.UsePlatformDetect().SetupWithoutStarting();
        else builder.UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        var old = new CommandLog("old");
        var control = new CommandLogTime { DataContext = old, FontSize = 24 };
        Check(!control.IsRefreshing, "Unloaded control started a timer");
        var window = new Window { Width = 320, Height = 120, Content = control };
        if (nativeWindows) window.Position = new PixelPoint(-10000, -10000);
        window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            var nativeHandle = nativeWindows ? window.TryGetPlatformHandle()!.Handle : IntPtr.Zero;
            if (nativeWindows) Check(nativeHandle != IntPtr.Zero && IsWindow(nativeHandle), "No actual Win32 window was created");
            Check(control.IsRefreshing && !string.IsNullOrEmpty(control.Text), "Loaded active log failed to start display timing");
            var current = new CommandLog("current");
            control.DataContext = current;
            var displayed = control.Text;
            old.Complete();
            Check(control.IsRefreshing && control.Text == displayed, "Replaced log retained its completion subscription");
            current.Complete();
            Check(!control.IsRefreshing && !string.IsNullOrEmpty(control.Text), "Completion waited for a timer tick or lost final text");
            var final = control.Text;
            Dispatcher.UIThread.RunJobs();
            Check(control.Text == final && !control.IsRefreshing, "Queued UI work restarted a completed timer");
            Console.WriteLine("PASS Actual loaded CommandLogTime stops immediately on completion and ignores completion from its replaced log");

            var next = new CommandLog("next"); control.DataContext = next;
            control.IsVisible = false;
            Check(!control.IsRefreshing, "Hidden control kept its display timer");
            control.IsVisible = true;
            Check(control.IsRefreshing, "Visible active control did not resume timing");
            window.Content = null; Dispatcher.UIThread.RunJobs();
            Check(!control.IsRefreshing, "Unloaded control kept its display timer");
            next.Complete();
            Check(!control.IsRefreshing, "Unloaded completion restarted its timer");
            window.Content = control; Dispatcher.UIThread.RunJobs();
            Check(!control.IsRefreshing && !string.IsNullOrEmpty(control.Text), "Reloaded completed control lost final text or restarted a timer");
            Console.WriteLine("PASS Actual visibility, unload and reload lifecycle parks timing and releases old completion subscriptions");

            using var frame = RenderText(window, nativeWindows);
            var output = Path.GetFullPath(nativeWindows ? "artifacts/polling-ui-native-time.png" : "artifacts/polling-ui-time.png");
            frame.Save(output);
            Check(frame.PixelSize.Width > 0 && frame.PixelSize.Height > 0 && new FileInfo(output).Length > 0, "Rendered elapsed-time frame is empty");
            Console.WriteLine("PASS Actual Skia rendered elapsed-time control produces a standalone PNG frame");

            Application.Current!.Resources["Text.Period.JustNow"] = "just now";
            var commit = new CommitTimeTextBlock { Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ShowAsDateTime = false, FontSize = 24 };
            Check(!commit.IsRefreshing, "Unloaded relative time started its timer");
            window.Content = commit; Dispatcher.UIThread.RunJobs();
            Check(commit.IsRefreshing && commit.Text == "just now", "Loaded relative time failed to start or render");
            commit.IsVisible = false;
            Check(!commit.IsRefreshing, "Hidden relative time kept its timer");
            commit.IsVisible = true; commit.ShowAsDateTime = true;
            Check(!commit.IsRefreshing && commit.Text != "just now", "Absolute date retained a relative refresh timer");
            commit.ShowAsDateTime = false;
            Check(commit.IsRefreshing, "Relative display did not resume");
            window.Content = null; Dispatcher.UIThread.RunJobs();
            commit.ShowAsDateTime = true; commit.ShowAsDateTime = false;
            Check(!commit.IsRefreshing, "Unloaded display-mode changes restarted its old timer");
            window.Content = commit; Dispatcher.UIThread.RunJobs();
            Check(commit.IsRefreshing && commit.Text == "just now", "Reloaded relative time did not resume once");
            using var commitFrame = RenderText(window, nativeWindows);
            commitFrame.Save(Path.GetFullPath(nativeWindows ? "artifacts/polling-ui-native-commit-time.png" : "artifacts/polling-ui-commit-time.png"));
            Console.WriteLine("PASS Actual relative/absolute commit-time modes, visibility and reload control the permitted 10-second display timer");

            var activeLog = new CommandLog("ancestor-visible");
            var elapsed = new CommandLogTime { DataContext = activeLog };
            var relative = new CommitTimeTextBlock { Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ShowAsDateTime = false };
            var panel = new StackPanel { Children = { elapsed, relative } };
            var outer = new Border { Child = panel };
            window.Content = outer; Dispatcher.UIThread.RunJobs();
            Check(elapsed.IsRefreshing && relative.IsRefreshing, "Visible nested controls did not start");
            outer.IsVisible = false;
            Check(elapsed.IsVisible && relative.IsVisible && !elapsed.IsRefreshing && !relative.IsRefreshing,
                "Hidden ancestor left time displays refreshing");
            relative.ShowAsDateTime = true; relative.ShowAsDateTime = false;
            elapsed.DataContext = new CommandLog("hidden-replacement");
            Check(!elapsed.IsRefreshing && !relative.IsRefreshing, "Hidden content changes restarted timing");
            outer.IsVisible = true;
            Check(elapsed.IsRefreshing && relative.IsRefreshing, "Ancestor visibility event did not resume timing");
            Console.WriteLine("PASS Hidden ancestors park both display timers; content/mode changes stay stopped and showing resumes through events");

            window.WindowState = WindowState.Minimized;
            if (nativeWindows) Check(IsIconic(nativeHandle), "Native HWND did not minimize");
            Check(window.WindowState == WindowState.Minimized && !elapsed.IsRefreshing && !relative.IsRefreshing,
                "Minimized window retained display timers");
            window.WindowState = WindowState.Normal;
            if (nativeWindows) Check(!IsIconic(nativeHandle), "Native HWND did not restore");
            Check(elapsed.IsRefreshing && relative.IsRefreshing, "Window restore did not resume display timers");
            ((CommandLog)elapsed.DataContext!).Complete();
            relative.ShowAsDateTime = true;
            window.WindowState = WindowState.Minimized; window.WindowState = WindowState.Normal;
            outer.IsVisible = false; outer.IsVisible = true;
            Check(!elapsed.IsRefreshing && !relative.IsRefreshing, "Restore restarted completed/absolute display timers");
            Console.WriteLine("PASS Window minimize/restore controls both timers without restarting completed logs or absolute dates");

            window.Content = null; Dispatcher.UIThread.RunJobs();
            elapsed.DataContext = new CommandLog("detached"); relative.ShowAsDateTime = false;
            outer.IsVisible = false; outer.IsVisible = true;
            window.WindowState = WindowState.Minimized; window.WindowState = WindowState.Normal;
            Check(!elapsed.IsRefreshing && !relative.IsRefreshing, "Detached controls retained ancestor/window subscriptions");
            outer.Child = null;
            var replacementParent = new Border { IsVisible = false, Child = panel };
            window.Content = replacementParent; Dispatcher.UIThread.RunJobs();
            Check(!elapsed.IsRefreshing && !relative.IsRefreshing, "Reparented hidden controls used former ancestor visibility");
            replacementParent.IsVisible = true;
            Check(elapsed.IsRefreshing && relative.IsRefreshing, "Reparented controls did not subscribe to their new ancestor");
            outer.IsVisible = false;
            Check(elapsed.IsRefreshing && relative.IsRefreshing, "Detached old parent still controls timing");
            Console.WriteLine("PASS Unload detaches ancestor events; reparented controls follow only their current visual tree");
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    private static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }

    private static Bitmap RenderText(Window window, bool nativeWindows)
    {
        if (nativeWindows)
        {
            var frame = new RenderTargetBitmap(new PixelSize(320, 120));
            frame.Render(window);
            return frame;
        }
        // A render tick can enqueue additional layout/compositor work. Drain bounded frames and inspect pixels,
        // rather than treating an existing blank framebuffer as proof that the control rendered.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            var frame = window.CaptureRenderedFrame();
            if (frame == null) continue;
            var visibleText = false;
            using (var pixels = frame.Lock())
            {
                var data = new byte[pixels.RowBytes * pixels.Size.Height];
                Marshal.Copy(pixels.Address, data, 0, data.Length);
                for (var y = 0; y < pixels.Size.Height && !visibleText; y++)
                    for (var x = 0; x < pixels.Size.Width; x++)
                    {
                        var offset = y * pixels.RowBytes + x * 4;
                        if (data[offset] < 128 && data[offset + 1] < 128 && data[offset + 2] < 128) { visibleText = true; break; }
                    }
            }
            if (visibleText) return frame;
            frame.Dispose();
        }
        throw new Exception("Skia frame contains no rendered text after draining layout/render jobs");
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr handle);
}
