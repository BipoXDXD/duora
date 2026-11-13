namespace Duora.Core.Media;

/// <summary>
/// O object storage falhou de um jeito que pode passar sozinho (erro 5xx, limite de taxa, rede, timeout): repetir mais
/// tarde pode dar certo. É um <see cref="IOException"/> de propósito, para seguir o caminho de todo erro de I/O do
/// storage, que os gates da API respondem com 503 e Retry-After e os trabalhadores repetem. Defeito de credencial ou
/// de bucket não vira este tipo: continua subindo como o erro original, que é 500 e alerta. A mensagem não leva a
/// chave do objeto (TM-42); a causa fica em <see cref="Exception.InnerException"/>.
/// </summary>
public sealed class StorageUnavailableException(string message, Exception innerException)
    : IOException(message, innerException);
