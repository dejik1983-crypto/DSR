// Проверка Program.ApplyIdioms на строках из книги: сборка DSR.dll, внутренний метод.
//
// Запуск из папки _diag (объекты сборки кладутся в _diag\IdiomProbe-obj, чтобы не
// оказаться внутри основного проекта — иначе .NET подхватывает их как второй набор
// AssemblyInfo и сборка проекта падает с CS0579):
//
//   cd _diag
//   dotnet run --project ..\..\_diag\IdiomProbe.csproj -- <путь к DSR.dll>
//
// Готовую сборку берём из bin\Debug\net8.0-windows\win-x64\DSR.dll после dotnet build.

using System;
using System.Reflection;

var dll = args.Length > 0
    ? args[0]
    : @"F:\DeepReader\DeepReader DSR\DSR\bin\Debug\net8.0-windows\win-x64\DSR.dll";

var asm = Assembly.LoadFrom(dll);
var program = asm.GetType("FB2AccentConverter.Program")!;
var apply = program.GetMethod("ApplyIdioms", BindingFlags.NonPublic | BindingFlags.Static)!;

var cases = new (string Source, string Expected)[]
{
    ("Всех, от ма́ла до вели́ка, подняли́.",
     "Всех, от мала до велика, подняли́."),
    ("от ма́ла до вели́ка.",
     "от мала до велика."),
    ("Каза́ки от ма́ла до вели́ка подня́ли ору́жие.",
     "Каза́ки от мала до велика подня́ли ору́жие."),
    ("больша́я и ма́лая ры́ба",
     "больша́я и ма́лая ры́ба"),
    ("ма́ла, вели́ка",
     "ма́ла, вели́ка"),
    ("с ма́лу до вели́ку",
     "с малу до велику"),
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
