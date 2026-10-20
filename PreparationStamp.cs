using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace FB2AccentConverter;

/// <summary>
/// Отметка «здесь и на этой машине окружение уже подготовлено».
///
/// Нужна, чтобы не нажимать «Подготовка к работе» и «Проверить GPU / CUDA» вручную
/// при каждом запуске. Подготовка проходит сама, когда: это первый запуск, приложение
/// перенесли в другое место, компьютер изменился, вышла новая версия DSR или выбрана
/// другая модель обработки. Окружение Python создаётся из системного Python или из
/// переносимого CPython, поэтому перенос на другой компьютер требует новой подготовки.
/// </summary>
internal sealed class PreparationStamp
{
    public string Version { get; set; } = string.Empty;
    public string ResourcesPath { get; set; } = string.Empty;
    public string Machine { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Device { get; set; } = string.Empty;
    public string Gpu { get; set; } = string.Empty;
    public string PreparedAt { get; set; } = string.Empty;

    private static string FilePath => Path.Combine(Program.DataDirectory, ".dsr-подготовлено.json");

    public static PreparationStamp? TryRead()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<PreparationStamp>(File.ReadAllText(FilePath))
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Program.DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error)
        {
            Program.LogError("Не удалось сохранить отметку о подготовке", error);
        }
    }

    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch (Exception) { /* отметка не критична: худшее — подготовимся лишний раз */ }
    }

    /// <summary>Готово ли окружение здесь, на этой машине и для этой модели.</summary>
    public bool IsValidFor(string model) =>
        Version == Program.AppVersion
        && string.Equals(ResourcesPath, Fingerprint_ResourcesPath(), StringComparison.OrdinalIgnoreCase)
        && Machine == Fingerprint_Machine()
        && Model == model
        && File.Exists(Program.EnvironmentPythonPath)
        && Program.IsModelPrepared(model);

    public static PreparationStamp Create(string model, string device, string gpu) => new()
    {
        Version = Program.AppVersion,
        ResourcesPath = Fingerprint_ResourcesPath(),
        Machine = Fingerprint_Machine(),
        Model = model,
        Device = device,
        Gpu = gpu,
        PreparedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
    };

    private static string Fingerprint_ResourcesPath()
    {
        try { return Path.GetFullPath(Program.DataDirectory); }
        catch (Exception) { return Program.DataDirectory; }
    }

    // Отпечаток машины: другой компьютер или другая система — снова готовим окружение.
    private static string Fingerprint_Machine()
    {
        string os;
        try { os = RuntimeInformation.OSDescription; }
        catch (Exception) { os = Environment.OSVersion.VersionString; }
        return string.Join("|", os, Environment.ProcessorCount, Environment.Is64BitProcess);
    }
}