using System;

namespace FB2AccentConverter.Parser;

public interface ILogger
{
    void LogInformation(string message, Exception? exception = null);
    void LogWarning(string message, Exception? exception = null);
    void LogError(string message, Exception? exception = null);
    void LogDebug(string message, Exception? exception = null);
}
