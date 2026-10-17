
namespace FB2AccentConverter.Parser;

/// <summary>
/// Интерфейс для парсеров документов. Возвращает DocumentModel — чистую модель данных без WPF.
/// </summary>
public interface IDocumentParser
{
    /// <summary>
    /// Парсит байты документа в DocumentModel. Stateless-метод.
    /// </summary>
    DocumentModel Parse(byte[] bytes);
    
    /// <summary>
    /// Парсит байты документа с поддержкой прогресса и отмены.
    /// </summary>
    DocumentModel Parse(byte[] bytes, IProgress<double>? progress, System.Threading.CancellationToken? ct = null);
}
