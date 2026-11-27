# RemoteFlow 启动脚本（嵌入资源，经 -EncodedCommand 传给系统 powershell.exe）。
# 脚本是固定文本，唯一的替换项是随机管道名；主机、账号、密码全部经管道传入，不进命令行、环境变量或文件。

# ── 失败即关闭，必须最先定义 ─────────────────────────────────────
# 远程会话中调用的是远端的 prompt。本机的 prompt 只有在「不在远程会话里」时才会被调用
# （连接失败、exit、Exit-PSSession、远端断开）。一旦被调用立即结束本进程，
# 用户永远拿不到本机命令行，避免以为还在服务器上而在自己电脑上执行命令。
# 用 Stop-Process 而不是 .NET 调用：受限语言模式下方法调用会被禁止，cmdlet 仍可用。
function global:prompt {
    try { if ($global:rfSession) { Remove-PSSession -Session $global:rfSession } } catch { }
    Stop-Process -Id $PID -Force
}

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
# 远程会话里敲的命令由本机 PSReadLine 记录，不关掉会写进用户自己的命令历史。
try { Set-PSReadLineOption -HistorySaveStyle SaveNothing } catch { Remove-Module PSReadLine -ErrorAction SilentlyContinue }

$writer = $null
try {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', '__PIPE__', [System.IO.Pipes.PipeDirection]::InOut)
    $pipe.Connect(15000)
    $reader = New-Object System.IO.BinaryReader($pipe, [System.Text.Encoding]::UTF8)
    $writer = New-Object System.IO.BinaryWriter($pipe, (New-Object System.Text.UTF8Encoding($false)))

    $computer = $reader.ReadString()
    $user = $reader.ReadString()
    $loopback = $reader.ReadBoolean()
    $length = $reader.ReadInt32()
    $secure = New-Object System.Security.SecureString
    if ($length -gt 0) {
        $chars = $reader.ReadChars($length)
        foreach ($c in $chars) { $secure.AppendChar($c) }
        [Array]::Clear($chars, 0, $chars.Length)
    }
    $secure.MakeReadOnly()

    $options = @{ ComputerName = $computer }
    if ($user) { $options.Credential = New-Object System.Management.Automation.PSCredential($user, $secure) }
    # 仅自动化测试连本机回环时使用。
    if ($loopback) { $options.EnableNetworkAccess = $true }
    $options.SessionOption = New-PSSessionOption -OpenTimeout 30000
    $global:rfSession = New-PSSession @options

    $writer.Write($true)
    $writer.Flush()

    # 关闭通道：应用关标签时断开管道（应用崩溃时系统也会断开），后台线程随即关闭远端会话并结束进程，
    # 远端命令执行中也有效。不做这一步，服务器上的会话会长时间残留。
    $watcher = [PowerShell]::Create()
    [void]$watcher.AddScript({
        param($channel, $session, $self)
        try { [void]$channel.ReadByte() } catch { }
        try { $session.Runspace.Close() } catch { }
        Stop-Process -Id $self -Force
    }).AddArgument($pipe).AddArgument($global:rfSession).AddArgument($PID)
    [void]$watcher.BeginInvoke()
}
catch {
    try {
        if ($writer) {
            $code = 0
            if ($_.Exception.PSObject.Properties['ErrorCode']) { $code = [int]$_.Exception.ErrorCode }
            $writer.Write($false)
            $writer.Write([string]$_.FullyQualifiedErrorId)
            $writer.Write([int]$code)
            $writer.Write([string]($_.Exception.Message -replace '\s+', ' '))
            $writer.Flush()
        }
    }
    catch { }
    Start-Sleep -Milliseconds 800
    Stop-Process -Id $PID -Force
}

Enter-PSSession -Session $global:rfSession
