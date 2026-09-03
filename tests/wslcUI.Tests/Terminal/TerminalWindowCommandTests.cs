using wslcUI;
using Xunit;

namespace wslcUI.Tests.Terminal;

/// <summary>
/// Attach 模式命令拼接回归（对应路线图「attach 到已运行进程」）：
/// Exec 保持既有 `exec -it /bin/sh`；Attach 用 `wslc attach <name>`（无 -it，
/// 无附加 shell：attach 连的是容器内已存在的前台进程，与 docker attach 语义一致）。
/// </summary>
public class TerminalWindowCommandTests
{
    [Fact]
    public void Exec_BuildsExecShellCommand()
    {
        var cmd = TerminalWindow.BuildCommand(
            @"C:\Program Files\WSL\wslc.exe", "web", TerminalWindow.Mode.Exec);

        Assert.Equal(
            "\"C:\\Program Files\\WSL\\wslc.exe\" exec -it \"web\" /bin/sh",
            cmd);
    }

    [Fact]
    public void Attach_BuildsPlainAttachCommand()
    {
        var cmd = TerminalWindow.BuildCommand(
            @"C:\Program Files\WSL\wslc.exe", "web", TerminalWindow.Mode.Attach);

        // 无 -it、无 cmd 字段——attach 仅附加，目标是容器内现有 PID 1 /
        // 前台进程的 stdin/stdout。容器名仍加引号：CreateProcessW 仍按命令行解析。
        Assert.Equal(
            "\"C:\\Program Files\\WSL\\wslc.exe\" attach \"web\"",
            cmd);
    }
}
