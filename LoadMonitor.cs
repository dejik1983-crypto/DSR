using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FB2AccentConverter;

/// <summary>
/// Живой мониторинг нагрузки во время ИИ-прохода: процессор, память и видеокарта.
/// Нужен, чтобы видеть, что модель действительно считает и компьютер не подвисает.
/// Данные берутся из ядра Windows, а для видеокарты — из nvidia-smi (если он есть).
/// </summary>
internal sealed class LoadMonitor : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTimeRaw
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtended;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out FileTimeRaw idle, out FileTimeRaw kernel, out FileTimeRaw user);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    private readonly string _pythonRoot;
    private readonly Dictionary<int, (long CpuTicks, DateTime At)> _workerCpu = new();
    private Process? _nvidia;
    private string _gpu = string.Empty;
    private bool _gpuStarted;
    private bool _cpuPrimed;
    private ulong _idle;
    private ulong _kernel;
    private ulong _user;

    public LoadMonitor()
    {
        _pythonRoot = Path.Combine(Program.DataDirectory, "python");
    }

    /// <summary>Короткая строка для интерфейса: «ЦП 42 % · ОЗУ … · GPU …».</summary>
    public string Describe()
    {
        var parts = new List<string> { "ЦП " + SystemCpuPercent().ToString("0", CultureInfo.InvariantCulture) + " %" };

        var (workerMemory, workerCpu) = WorkerLoad();
        if (workerMemory > 0)
            parts.Add("ИИ-процесс: ЦП " + workerCpu.ToString("0", CultureInfo.InvariantCulture) + " %, ОЗУ " + Size(workerMemory));

        if (SystemMemoryUsed() is { } memory)
            parts.Add("ОЗУ всего " + Size(memory.Used) + " из " + Size(memory.Total));

        var gpu = Gpu();
        if (gpu.Length > 0) parts.Add(gpu);

        return string.Join("  ·  ", parts);
    }

    // ── процессор и память ───────────────────────────────────────────────────

    private double SystemCpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        var idleValue = Ticks(idle);
        var kernelValue = Ticks(kernel);
        var userValue = Ticks(user);
        if (!_cpuPrimed)
        {
            _idle = idleValue; _kernel = kernelValue; _user = userValue;
            _cpuPrimed = true;
            return 0;
        }
        var idleDelta = idleValue - _idle;
        var totalDelta = (kernelValue - _kernel) + (userValue - _user);
        _idle = idleValue; _kernel = kernelValue; _user = userValue;
        if (totalDelta <= 0) return 0;
        return 100.0 * (totalDelta - idleDelta) / totalDelta;
    }

    private static ulong Ticks(FileTimeRaw value) => ((ulong)value.High << 32) | value.Low;

    private (ulong Used, ulong Total)? SystemMemoryUsed()
    {
        var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref memory) || memory.TotalPhys == 0) return null;
        return (memory.TotalPhys - memory.AvailPhys, memory.TotalPhys);
    }

    // ── процесс воркера (python из нашего окружения) ─────────────────────────

    private (ulong Memory, double Cpu) WorkerLoad()
    {
        ulong memory = 0;
        double cpu = 0;
        var now = DateTime.UtcNow;
        var alive = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("python").Concat(Process.GetProcessesByName("pythonw")))
        {
            try
            {
                if (!IsOurPython(process)) continue;
                alive.Add(process.Id);
                memory += (ulong)process.WorkingSet64;
                var ticks = process.TotalProcessorTime.Ticks;
                if (_workerCpu.TryGetValue(process.Id, out var previous))
                {
                    var seconds = (now - previous.At).TotalMilliseconds;
                    if (seconds > 0)
                        cpu += 100.0 * (ticks - previous.CpuTicks) / (seconds * 1000.0 * Environment.ProcessorCount);
                }
                _workerCpu[process.Id] = (ticks, now);
            }
            catch { /* процесс мог завершиться — это нормально */ }
            finally { process.Dispose(); }
        }
        foreach (var gone in _workerCpu.Keys.Where(id => !alive.Contains(id)).ToList()) _workerCpu.Remove(gone);
        return (memory, Math.Clamp(cpu, 0, 100 * Environment.ProcessorCount));
    }

    private bool IsOurPython(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName;
            return !string.IsNullOrEmpty(path) &&
                   path.StartsWith(_pythonRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Нет прав читать модуль процесса — считаем чужим python, чтобы не врать в цифрах.
            return false;
        }
    }

    // ── видеокарта ───────────────────────────────────────────────────────────

    private string Gpu()
    {
        if (!_gpuStarted)
        {
            _gpuStarted = true;
            StartNvidiaSmi();
        }
        if (_gpu.Length == 0) return string.Empty;
        return "GPU " + _gpu;
    }

    private void StartNvidiaSmi()
    {
        var candidates = new[]
        {
            "nvidia-smi",
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
        };
        foreach (var candidate in candidates)
        {
            try
            {
                var info = new ProcessStartInfo(candidate)
                {
                    Arguments = "--query-gpu=utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits -l 1",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                var process = Process.Start(info);
                if (process == null) continue;
                process.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data)) _gpu = NormalizeGpu(e.Data);
                };
                process.ErrorDataReceived += (_, _) => { };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                _nvidia = process;
                return;
            }
            catch { /* утилиты нет — покажем только процессор и память */ }
        }
    }

    // nvidia-smi отдаёт память в МиБ, Size() ждёт байты.
    private string NormalizeGpu(string line)
    {
        var values = line.Split(',', StringSplitOptions.TrimEntries);
        if (values.Length < 3) return line;
        return values[0] + " %, " + SizeFromMiB(ReadNumber(values[1])) + " из " + SizeFromMiB(ReadNumber(values[2]));
    }

    private static ulong ReadNumber(string value)
    {
        // Единицы измерения бывают и есть, и нет — берём только цифры.
        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        return ulong.TryParse(digits, out var number) ? number : 0;
    }

    private static string SizeFromMiB(ulong mib) => Size(mib * 1024UL * 1024UL);

    private static string Size(ulong bytes)
    {
        const double megabyte = 1024 * 1024;
        const double gigabyte = megabyte * 1024;
        // Формат по языку интерфейса: в русской локали получается «8,0 ГБ».
        if (bytes >= gigabyte) return (bytes / gigabyte).ToString("0.0") + " ГБ";
        return (bytes / megabyte).ToString("0") + " МБ";
    }

    public void Dispose()
    {
        try
        {
            if (_nvidia is { HasExited: false }) _nvidia.Kill();
        }
        catch { /* уже завершён */ }
        finally { _nvidia?.Dispose(); }
    }
}