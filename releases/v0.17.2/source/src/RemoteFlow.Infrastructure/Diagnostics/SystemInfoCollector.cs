using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RemoteFlow.Core.Diagnostics;

namespace RemoteFlow.Infrastructure.Diagnostics;

/// <summary>
/// 基础系统信息采集。跨平台部分用 BCL；CPU 型号 / 主频 / 内存可用量 / 屏幕按操作系统分支
/// （Windows 用 kernel32 + 注册表，macOS / Linux 用 sysctl / /proc）。
/// 所有平台专属调用都先判断当前操作系统，未覆盖时留空而不抛异常 —— 采集失败绝不能影响登录。
/// </summary>
public sealed class SystemInfoCollector : ISystemInfoCollector
{
    public SystemInfo Collect()
    {
        try
        {
            return CollectCore();
        }
        catch
        {
            // 采集是尽力而为：任何异常都不应影响客户端主流程。
            return new SystemInfo(
                SafeHostName(), SafeOsName(), SafeOsVersion(), NormalizeArchitecture(),
                string.Empty, 0, Environment.ProcessorCount, 0, 0, 0, string.Empty, [], string.Empty,
                SafeUserName(), SafeTimeZone(), SafeUptime(), string.Empty,
                RuntimeInformation.FrameworkDescription, ClientVersion());
        }
    }

    private static SystemInfo CollectCore()
    {
        var disks = CollectDisks();
        var (cpuModel, cpuMhz) = CollectCpu();
        var (memTotal, memAvailable) = CollectMemory();
        var (physicalCores, logicalCores) = CollectCores();

        return new SystemInfo(
            SafeHostName(),
            SafeOsName(),
            SafeOsVersion(),
            NormalizeArchitecture(),
            cpuModel,
            physicalCores,
            logicalCores,
            cpuMhz,
            memTotal,
            memAvailable,
            SummarizeDisks(disks),
            disks,
            PrimaryIpv4(),
            SafeUserName(),
            SafeTimeZone(),
            SafeUptime(),
            CollectScreen(),
            RuntimeInformation.FrameworkDescription,
            ClientVersion());
    }

    // ── 基础 ────────────────────────────────────────────────────

    private static string SafeHostName() => Environment.MachineName;

    /// <summary>架构规范化：X64 → x64（枚举的大写形式对用户不友好）。</summary>
    private static string NormalizeArchitecture() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.X86 => "x86",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm",
        var other => other.ToString().ToLowerInvariant(),
    };

    private static string SafeUserName() => Environment.UserName;

    private static string SafeTimeZone() => TimeZoneInfo.Local.Id;

    private static long SafeUptime() => Environment.TickCount64 / 1000;

    /// <summary>操作系统名称（含版本族与版本，如「Windows 11 专业版」/「macOS」/「Ubuntu 24.04」）。</summary>
    private static string SafeOsName()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsOsName();
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macOS";
        }

        if (OperatingSystem.IsLinux())
        {
            // /etc/os-release 的 PRETTY_NAME 比内核描述可读得多（如 Ubuntu 24.04.1 LTS）。
            try
            {
                foreach (var line in File.ReadLines("/etc/os-release"))
                {
                    if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
                    {
                        return line["PRETTY_NAME=".Length..].Trim().Trim('"');
                    }
                }
            }
            catch (IOException)
            {
                // 退回简单名称。
            }

            return "Linux";
        }

        return RuntimeInformation.OSDescription;
    }

    /// <summary>操作系统版本（Windows：版本号 + 构建，如「25H2 (26200.6584)」）。</summary>
    private static string SafeOsVersion()
    {
        if (OperatingSystem.IsWindows())
        {
            const string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            var display = WindowsRegistryRead(key, "DisplayVersion");
            var build = WindowsRegistryRead(key, "CurrentBuild");
            var revision = WindowsRegistryRead(key, "UBR");
            // 版本串收成「25H2 (26200.9445)」：版本号 + 构建号（含修订）。
            var buildText = build is { Length: > 0 }
                ? $"({build}" + (revision is { Length: > 0 } ? $".{revision}" : string.Empty) + ")"
                : string.Empty;

            return string.Join(' ', new[] { display, buildText }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        if (OperatingSystem.IsMacOS())
        {
            var version = SysctlString("kern.osproductversion");
            var build = SysctlString("kern.osversion");
            return string.Join(' ', new[]
            {
                string.IsNullOrWhiteSpace(version) ? RuntimeInformation.OSDescription : version,
                string.IsNullOrWhiteSpace(build) ? string.Empty : $"({build})",
            }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        return RuntimeInformation.OSDescription;
    }

    /// <summary>
    /// Windows 版本族 + 版本：<c>ProductName</c> 在 Win11 上仍可能写「Windows 10」，
    /// 因此以构建号（≥22000 为 Windows 11）判定版本族；再按 ProductName 里的版本关键字映射中文。
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string WindowsOsName()
    {
        const string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var productName = WindowsRegistryRead(key, "ProductName") ?? "Windows";
        var editionId = WindowsRegistryRead(key, "EditionID") ?? string.Empty;
        var build = int.TryParse(WindowsRegistryRead(key, "CurrentBuild"), out var parsed) ? parsed : 0;

        if (productName.Contains("Server", StringComparison.OrdinalIgnoreCase)
            || editionId.StartsWith("Server", StringComparison.OrdinalIgnoreCase))
        {
            return productName.Trim();      // 服务器版直接用官方名（如 Windows Server 2022 Datacenter）
        }

        // 版本族看构建号：Win11 的 ProductName 常仍写「Windows 10 Pro」，不能用它判族。
        var family = build >= 22000 ? "Windows 11" : "Windows 10";
        return string.IsNullOrEmpty(WindowsEdition(editionId, productName))
            ? family
            : $"{family} {WindowsEdition(editionId, productName)}";
    }

    /// <summary>
    /// 版本（SKU）中文名：优先用 EditionID —— 它不随系统语言变（中文系统上 ProductName 是中文，
    /// 英文系统上是英文，且 Win11 常误写 Windows 10），ProductName 只作兜底。
    /// </summary>
    private static string WindowsEdition(string id, string productName)
    {
        if (id.StartsWith("ProfessionalWorkstation", StringComparison.OrdinalIgnoreCase)) return "专业工作站版";
        if (id.StartsWith("ProfessionalEducation", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("ProfessionalEducationN", StringComparison.OrdinalIgnoreCase)) return "专业教育版";
        if (id.StartsWith("Professional", StringComparison.OrdinalIgnoreCase)) return "专业版";
        if (id.StartsWith("Education", StringComparison.OrdinalIgnoreCase)) return "教育版";
        if (id.StartsWith("Enterprise", StringComparison.OrdinalIgnoreCase)) return "企业版";
        if (id.StartsWith("Core", StringComparison.OrdinalIgnoreCase)) return "家庭版";
        if (id.StartsWith("Starter", StringComparison.OrdinalIgnoreCase)) return "简易版";

        // EditionID 拿不到时退回 ProductName（兼容英文与中文写法）。
        return productName switch
        {
            var n when n.Contains("Professional Workstation", StringComparison.OrdinalIgnoreCase)
                || n.Contains("专业工作站") => "专业工作站版",
            var n when n.Contains("Professional", StringComparison.OrdinalIgnoreCase)
                || n.Contains(" Pro", StringComparison.OrdinalIgnoreCase)
                || n.Contains("专业版") => "专业版",
            var n when n.Contains("Education", StringComparison.OrdinalIgnoreCase)
                || n.Contains("教育版") => "教育版",
            var n when n.Contains("Enterprise", StringComparison.OrdinalIgnoreCase)
                || n.Contains("企业版") => "企业版",
            var n when n.Contains("Home", StringComparison.OrdinalIgnoreCase)
                || n.Contains("家庭") => "家庭版",
            _ => string.Empty,
        };
    }

    private static string ClientVersion() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? string.Empty;

    // ── 磁盘 ────────────────────────────────────────────────────

    private static List<SystemDisk> CollectDisks()
    {
        var disks = new List<SystemDisk>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                disks.Add(new SystemDisk(
                    drive.Name.TrimEnd('\\', '/'),
                    drive.DriveFormat,
                    drive.TotalSize,
                    drive.AvailableFreeSpace));
            }
            catch (IOException)
            {
                // 不可用卷（光驱/未挂载）——跳过。
            }
        }

        return disks;
    }

    private static string SummarizeDisks(IReadOnlyList<SystemDisk> disks) =>
        string.Join(" · ", disks.Select(d =>
            $"{d.Name} {Gb(d.FreeBytes)}/{Gb(d.TotalBytes)} GB"));

    private static string Gb(long bytes) => (bytes / 1024.0 / 1024 / 1024).ToString("0.#", CultureInfo.InvariantCulture);

    // ── CPU ─────────────────────────────────────────────────────

    private static (string Model, int Mhz) CollectCpu()
    {
        if (OperatingSystem.IsWindows())
        {
            var model = WindowsRegistryRead(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")?.Trim() ?? string.Empty;
            var mhzText = WindowsRegistryRead(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "~MHz");
            return (model, int.TryParse(mhzText, out var mhz) ? mhz : 0);
        }

        if (OperatingSystem.IsMacOS())
        {
            return (SysctlString("machdep.cpu.brand_string"), SysctlInt("hw.cpufrequency") / 1_000_000);
        }

        if (OperatingSystem.IsLinux())
        {
            var (model, mhz) = ReadLinuxCpuInfo();
            return (model, mhz);
        }

        return (string.Empty, 0);
    }

    private static (int Physical, int Logical) CollectCores()
    {
        var logical = Environment.ProcessorCount;
        if (OperatingSystem.IsMacOS())
        {
            var physical = SysctlInt("hw.physicalcpu");
            return (physical > 0 ? physical : logical, logical);
        }

        if (OperatingSystem.IsLinux())
        {
            var physical = 0;
            try
            {
                foreach (var line in File.ReadLines("/proc/cpuinfo"))
                {
                    if (line.StartsWith("cpu cores", StringComparison.Ordinal))
                    {
                        var value = line.Split(':')[^1].Trim();
                        if (int.TryParse(value, out var cores))
                        {
                            physical = Math.Max(physical, cores);
                        }
                    }
                }
            }
            catch (IOException)
            {
                // 忽略：退回逻辑核数。
            }

            return (physical, logical);
        }

        if (OperatingSystem.IsWindows())
        {
            var physical = WindowsPhysicalCoreCount();
            return (physical > 0 ? physical : logical, logical);
        }

        return (logical, logical);
    }

    private static (string Model, int Mhz) ReadLinuxCpuInfo()
    {
        try
        {
            var model = string.Empty;
            var mhz = 0;
            foreach (var line in File.ReadLines("/proc/cpuinfo"))
            {
                if (model.Length == 0 && line.StartsWith("model name", StringComparison.Ordinal))
                {
                    model = line.Split(':')[^1].Trim();
                }
                else if (mhz == 0 && line.StartsWith("cpu MHz", StringComparison.Ordinal))
                {
                    var value = line.Split(':')[^1].Trim();
                    if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                    {
                        mhz = (int)parsed;
                    }
                }

                if (model.Length > 0 && mhz > 0)
                {
                    break;
                }
            }

            return (model, mhz);
        }
        catch (IOException)
        {
            return (string.Empty, 0);
        }
    }

    // ── 内存 ────────────────────────────────────────────────────

    private static (long Total, long Available) CollectMemory()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx();
            if (GlobalMemoryStatusEx(status))
            {
                return ((long)status.TotalPhys, (long)status.AvailPhys);
            }

            return (0, 0);
        }

        if (OperatingSystem.IsMacOS())
        {
            var total = SysctlLong("hw.memsize");
            var free = SysctlLong("hw.pagesize") * SysctlLong("hw.freepagecount");
            return (total, free);
        }

        if (OperatingSystem.IsLinux())
        {
            long total = 0, available = 0;
            try
            {
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal", StringComparison.Ordinal))
                    {
                        total = ParseKb(line);
                    }
                    else if (line.StartsWith("MemAvailable", StringComparison.Ordinal))
                    {
                        available = ParseKb(line);
                    }
                }
            }
            catch (IOException)
            {
                // 忽略。
            }

            return (total, available);
        }

        return (0, 0);
    }

    private static long ParseKb(string line)
    {
        var value = line.Split(':')[^1].Trim().Split(' ')[0];
        return long.TryParse(value, out var kb) ? kb * 1024 : 0;
    }

    // ── 屏幕 ────────────────────────────────────────────────────

    private static string CollectScreen()
    {
        if (!OperatingSystem.IsWindows())
        {
            return string.Empty;
        }

        var monitors = GetSystemMetrics(SmCMonitors);
        var width = GetSystemMetrics(SmCxScreen);
        var height = GetSystemMetrics(SmCyScreen);
        return monitors <= 0 || width <= 0
            ? string.Empty
            : $"{width}x{height}" + (monitors > 1 ? $" × {monitors} 屏" : string.Empty);
    }

    // ── 网络 ────────────────────────────────────────────────────

    private static string PrimaryIpv4()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        return address.Address.ToString();
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // 忽略。
        }

        return string.Empty;
    }

    // ── Windows P/Invoke ────────────────────────────────────────

    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const int SmCMonitors = 80;

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    /// <summary>读注册表值并转成字符串（REG_DWORD / REG_SZ 都能读）。</summary>
    private static string? WindowsRegistryRead(string subKey, string valueName)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(subKey);
            return key?.GetValue(valueName)?.ToString();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int WindowsPhysicalCoreCount()
    {
        try
        {
            uint length = 0;
            GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length);
            if (length == 0)
            {
                return 0;
            }

            var buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length))
                {
                    return 0;
                }

                var count = 0;
                var offset = 0;
                while (offset < length)
                {
                    var relationship = Marshal.ReadInt32(buffer, offset);
                    var size = Marshal.ReadInt32(buffer, offset + 4);
                    if (size <= 0)
                    {
                        break;
                    }

                    if (relationship == RelationProcessorCore)
                    {
                        count++;
                    }

                    offset += size;
                }

                return count;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (EntryPointNotFoundException)
        {
            return 0;
        }
    }

    private const int RelationProcessorCore = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(
        int relationshipType, IntPtr buffer, ref uint returnedLength);

    // ── macOS / Linux sysctl ───────────────────────────────────

    private static string SysctlString(string name)
    {
        var value = SysctlRaw(name);
        return value is null ? string.Empty : System.Text.Encoding.UTF8.GetString(value).TrimEnd('\0', '\n');
    }

    private static int SysctlInt(string name)
    {
        var bytes = SysctlRaw(name);
        if (bytes is null || bytes.Length < 4)
        {
            return 0;
        }

        return BitConverter.ToInt32(bytes, 0);
    }

    private static long SysctlLong(string name)
    {
        var bytes = SysctlRaw(name);
        if (bytes is null)
        {
            return 0;
        }

        return bytes.Length switch
        {
            8 => BitConverter.ToInt64(bytes, 0),
            4 => BitConverter.ToInt32(bytes, 0),
            _ => 0,
        };
    }

    private static byte[]? SysctlRaw(string name)
    {
        try
        {
            // sysctlbyname(name, oldp, &oldlen, newp, newlen)
            nuint length = 0;
            if (SysctlByName(name, null, ref length, IntPtr.Zero, 0) != 0 || length == 0)
            {
                return null;
            }

            var buffer = new byte[length];
            return SysctlByName(name, buffer, ref length, IntPtr.Zero, 0) == 0 ? buffer : null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int SysctlByName(
        string name, byte[]? oldValue, ref nuint oldLength, IntPtr newValue, nuint newLength);
}
