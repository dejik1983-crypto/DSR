// Проверка Program.ApplyIdioms на строках из книги: сборка DSR.dll, внутренний метод.
//
// Запуск из папки _diag:
//
//   dotnet run                 (возьмёт bin\Debug основного проекта)
//   dotnet run -- <путь к DSR.dll>
//
// Объекты сборки попадают в _diag\obj — основному проекту они не мешают:
// в FB2AccentConverter.csproj есть <Compile Remove="_diag\**\*.cs" />.

using System;
using System.IO;
using System.Reflection;

// Путь к DSR.dll: аргумент или соседний bin\Debug основного проекта (не его исходная
// па́пка — иначе hardcoded путь ломается на другой машине и в git worktree).
// Ищем DSR.dll вверх от папки прогона: пробник лежит в _diag-obj\IdiomProbe\bin\…,
// а сборка основного проекта — в <корень>\bin\Debug\net8.0-windows\win-x64.
static string? FindBuild()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FB2AccentConverter.csproj")))
        dir = dir.Parent;
    if (dir == null) return null;
    var candidate = Path.Combine(dir.FullName, "bin", "Debug", "net8.0-windows", "win-x64", "DSR.dll");
    return File.Exists(candidate) ? candidate : null;
}

var dll = args.Length > 0 ? args[0] : FindBuild() ?? "";
if (!File.Exists(dll))
{
    Console.WriteLine($"Нет сборки: {dll} — передайте путь к DSR.dll аргументом.");
    return 2;
}

var asm = Assembly.LoadFrom(dll);
var program = asm.GetType("FB2AccentConverter.Program")!;
var apply = program.GetMethod("ApplyIdioms", BindingFlags.NonPublic | BindingFlags.Static)!;

// Строки заданы escape-последовательностями: в файле не должно быть литеральных
// знаков ударения и «съедаемых» пробелов. A — комбинирующий акут U+0301.
const string A = "\u0301";
var cases = new (string Source, string Expected)[]
{
    // Фраза с предлогом «до», в начале строки и с точкой в конце.
    ("Всех, от ма" + A + "ла до вели" + A + "ка, подняли" + A + ".",
     "Всех, от мала до велика, подняли" + A + "."),
    ("от ма" + A + "ла до вели" + A + "ка.", "от мала до велика."),
    ("Каза" + A + "ки от ма" + A + "ла до вели" + A + "ка подня" + A + "ли ору" + A + "жие.",
     "Каза" + A + "ки от мала до велика подня" + A + "ли ору" + A + "жие."),
    // Вне фразы слова не трогаем: обычные формы и соседние слова.
    ("больша" + A + "я и ма" + A + "лая ры" + A + "ба",
     "больша" + A + "я и ма" + A + "лая ры" + A + "ба"),
    // Запятая разрывает фразу: это уже не оборот.
    ("ма" + A + "ла, вели" + A + "ка", "ма" + A + "ла, вели" + A + "ка"),
    // Вторая пара словаря — «малу … велику».
    ("с ма" + A + "лу до вели" + A + "ку", "с малу до велику"),
    // Неразрывный пробел между словами фразы — тоже разделитель.
    ("от ма" + A + "ла\u00a0до\u00a0вели" + A + "ка", "от мала\u00a0до\u00a0велика"),
};

var failed = 0;
foreach (var (source, expected) in cases)
{
    var actual = (string)apply.Invoke(null, new object[] { source })!;
    var ok = actual == expected;
    if (!ok) failed++;
    Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {source} -> {actual}");
}
Console.WriteLine(failed == 0 ? "итог: всё прошло" : $"провалов: {failed}");
return failed == 0 ? 0 : 1;
