using System.Runtime.InteropServices;

namespace InfinityNikki.LaunchBridge;

public static class FailureNotifier
{
    private const uint MbOk = 0x00000000;
    private const uint MbIconError = 0x00000010;

    public static void Show(string message, BridgeLogger logger)
    {
        try
        {
            MessageBox(
                IntPtr.Zero,
                message
                + $"\n\n日志：{logger.LogPath}"
                + (logger.WriteFailure is null ? "" : $"\n日志写入失败：{logger.WriteFailure}"),
                "InfinityNikkiLaunchBridge 启动失败",
                MbOk | MbIconError);
        }
        catch
        {
            // Error reporting must not replace the original bridge failure.
        }
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);
}
