# RemoteFlow：经 WMI（DCOM）检查 / 启用目标电脑的 PowerShell 远程。
# 固定脚本，唯一替换项是随机管道名；主机、账号、密码、模式经管道传入。结果也经管道以「键=值」返回。
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$HKLM = [uint32]2147483650
$PolicyKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', '__PIPE__', [System.IO.Pipes.PipeDirection]::InOut)
$pipe.Connect(15000)
$reader = New-Object System.IO.BinaryReader($pipe, [System.Text.Encoding]::UTF8)
$writer = New-Object System.IO.BinaryWriter($pipe, (New-Object System.Text.UTF8Encoding($false)))

$computer = $reader.ReadString()
$user = $reader.ReadString()
$mode = $reader.ReadString()
$length = $reader.ReadInt32()
$secure = New-Object System.Security.SecureString
if ($length -gt 0) {
    $chars = $reader.ReadChars($length)
    foreach ($c in $chars) { $secure.AppendChar($c) }
    [Array]::Clear($chars, 0, $chars.Length)
}
$secure.MakeReadOnly()
$credential = New-Object System.Management.Automation.PSCredential($user, $secure)

function Open-Target {
    New-CimSession -ComputerName $computer -Credential $credential -SessionOption (New-CimSessionOption -Protocol Dcom) -OperationTimeoutSec 25
}

function Read-Dword($session, $name) {
    $r = Invoke-CimMethod -CimSession $session -Namespace root/default -ClassName StdRegProv -MethodName GetDWORDValue -Arguments @{ hDefKey = $HKLM; sSubKeyName = $PolicyKey; sValueName = $name }
    if ($r.ReturnValue -eq 0) { [string]$r.uValue } else { '' }
}

function Read-Target($session) {
    $os = Get-CimInstance -CimSession $session -ClassName Win32_OperatingSystem
    $cs = Get-CimInstance -CimSession $session -ClassName Win32_ComputerSystem
    $service = Get-CimInstance -CimSession $session -ClassName Win32_Service -Filter "Name='WinRM'"
    # NetworkCategory: 0 公用 / 1 专用 / 2 域
    $public = @(Get-CimInstance -CimSession $session -Namespace root/StandardCimv2 -ClassName MSFT_NetConnectionProfile |
        Where-Object { [int]$_.NetworkCategory -eq 0 } | ForEach-Object { $_.InterfaceAlias })
    [ordered]@{
        name = [string]$cs.Name
        os = [string]$os.Caption
        server = [string]($os.ProductType -ne 1)
        domain = [string][bool]$cs.PartOfDomain
        state = [string]$service.State
        startMode = [string]$service.StartMode
        public = ($public -join '|')
        uac = [string]((Read-Dword $session 'EnableLUA') -ne '0')
        policy = (Read-Dword $session 'LocalAccountTokenFilterPolicy')
    }
}

function Send-Result($values) {
    $writer.Write($true)
    $writer.Write([int]$values.Count)
    foreach ($key in $values.Keys) { $writer.Write([string]$key); $writer.Write([string]$values[$key]) }
    $writer.Flush()
}

$session = $null
try {
    $session = Open-Target
    $before = Read-Target $session
    $result = [ordered]@{}
    foreach ($key in $before.Keys) { $result["before.$key"] = $before[$key] }

    if ($mode -eq 'enable') {
        # 公用网络上开启远程管理风险过高：在目标上做任何改动之前就拒绝。
        if ($before['public']) { throw 'RF_PUBLIC_NETWORK' }

        $started = Invoke-CimMethod -CimSession $session -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = 'powershell.exe -NoProfile -NonInteractive -Command "Enable-PSRemoting -Force"' }
        if ($started.ReturnValue -ne 0) { throw "RF_START_FAILED:$($started.ReturnValue)" }

        # 启用过程会重启服务，期间查询可能失败：丢掉连接重建后继续等。
        $deadline = (Get-Date).AddSeconds(120)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 2
            try {
                if (-not $session) { $session = Open-Target }
                if (-not (Get-CimInstance -CimSession $session -ClassName Win32_Process -Filter "ProcessId=$($started.ProcessId)")) { break }
            }
            catch { if ($session) { Remove-CimSession $session -ErrorAction SilentlyContinue }; $session = $null }
        }

        $after = $null
        for ($attempt = 0; $attempt -lt 5 -and -not $after; $attempt++) {
            try {
                if (-not $session) { $session = Open-Target }
                $after = Read-Target $session
            }
            catch { if ($session) { Remove-CimSession $session -ErrorAction SilentlyContinue }; $session = $null; Start-Sleep -Seconds 3 }
        }
        if (-not $after) { throw 'RF_VERIFY_FAILED' }

        # Enable-PSRemoting 在非域电脑上可能顺带设置 LocalAccountTokenFilterPolicy。
        # 目标没开 UAC 时它不起任何作用，恢复原状；开着 UAC 时保留（撤掉可能导致本地管理员账户无法远程登录）。
        $outcome = 'Unchanged'
        if ($after['policy'] -ne $before['policy']) {
            $outcome = 'Kept'
            if ($after['uac'] -eq 'False') {
                if ($before['policy'] -eq '') {
                    $undo = Invoke-CimMethod -CimSession $session -Namespace root/default -ClassName StdRegProv -MethodName DeleteValue -Arguments @{ hDefKey = $HKLM; sSubKeyName = $PolicyKey; sValueName = 'LocalAccountTokenFilterPolicy' }
                }
                else {
                    $undo = Invoke-CimMethod -CimSession $session -Namespace root/default -ClassName StdRegProv -MethodName SetDWORDValue -Arguments @{ hDefKey = $HKLM; sSubKeyName = $PolicyKey; sValueName = 'LocalAccountTokenFilterPolicy'; uValue = [uint32]$before['policy'] }
                }
                if ($undo.ReturnValue -eq 0) { $outcome = 'Restored'; $after['policy'] = $before['policy'] }
            }
        }
        foreach ($key in $after.Keys) { $result["after.$key"] = $after[$key] }
        $result['outcome'] = $outcome
    }

    Send-Result $result
}
catch {
    $hresult = 0
    if ($_.Exception) { $hresult = [int]$_.Exception.HResult }
    # WMI 异常的真实错误码不在 HResult 里，而在 MessageId（形如 "HRESULT 0x80070005"）与 NativeErrorCode。
    $errorId = [string]$_.FullyQualifiedErrorId
    foreach ($name in 'NativeErrorCode', 'MessageId') {
        if ($_.Exception.PSObject.Properties[$name]) { $errorId = $errorId + ';' + [string]$_.Exception.$name }
    }
    $writer.Write($false)
    $writer.Write($errorId)
    $writer.Write([int]$hresult)
    $writer.Write([string]($_.Exception.Message -replace '\s+', ' '))
    $writer.Flush()
}
finally {
    if ($session) { Remove-CimSession $session -ErrorAction SilentlyContinue }
    $secure.Dispose()
}
Start-Sleep -Milliseconds 500
