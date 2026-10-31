namespace Pine3LightControl.Core.Bluetooth;

/// <summary>
/// Ligação GATT a um controlador (serviço FFE0 / característica FFE1).
/// Implementada com WinRT na app; nos testes por uma ligação falsa.
/// </summary>
public interface IBleLink : IAsyncDisposable
{
    bool IsConnected { get; }
    /// <summary>Último erro legível (para mostrar na UI).</summary>
    string LastError { get; }

    /// <summary>Liga e subscreve as notificações. Devolve false se não conseguiu.</summary>
    Task<bool> ConnectAsync(CancellationToken ct);
    Task DisconnectAsync();
    Task WriteAsync(byte[] data);

    /// <summary>Notificação recebida na característica.</summary>
    event Action<byte[]>? Notified;
    /// <summary>A ligação caiu ou foi estabelecida.</summary>
    event Action<bool>? ConnectionChanged;
}
