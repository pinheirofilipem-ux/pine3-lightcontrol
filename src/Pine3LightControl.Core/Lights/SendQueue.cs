namespace Pine3LightControl.Core.Lights;

/// <summary>
/// Fila "o último ganha" por chave, com um intervalo mínimo entre envios.
/// Arrastar um slider gera dezenas de valores por segundo: só o mais recente de
/// cada tipo (cor, brilho...) chega ao dispositivo, e os envios nunca se sobrepõem.
/// Re-publicar uma chave pendente move-a para o fim, para manter a ordem do utilizador
/// (ex.: cor e depois desligar → a cor não volta a ligar a luz).
/// </summary>
public sealed class SendQueue
{
    private readonly object _gate = new();
    private readonly List<string> _order = new();
    private readonly Dictionary<string, Func<Task>> _pending = new();
    private readonly TimeSpan _gap;
    private TaskCompletionSource _idle = Completed();
    private bool _running;

    public event Action<Exception>? Error;

    public SendQueue(TimeSpan gap) => _gap = gap;

    private static TaskCompletionSource Completed()
    {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.SetResult();
        return t;
    }

    public void Post(string key, Func<Task> send)
    {
        lock (_gate)
        {
            _order.Remove(key);
            _order.Add(key);
            _pending[key] = send;
            if (_running) return;
            _running = true;
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _ = Task.Run(PumpAsync);
    }

    /// <summary>Descarta tudo o que ainda não foi enviado.</summary>
    public void Clear()
    {
        lock (_gate) { _order.Clear(); _pending.Clear(); }
    }

    /// <summary>Completa quando a fila fica vazia.</summary>
    public Task WhenIdle()
    {
        lock (_gate) return _idle.Task;
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            Func<Task> send;
            lock (_gate)
            {
                if (_order.Count == 0)
                {
                    _running = false;
                    _idle.TrySetResult();
                    return;
                }
                var key = _order[0];
                _order.RemoveAt(0);
                send = _pending[key];
                _pending.Remove(key);
            }
            try { await send().ConfigureAwait(false); }
            catch (Exception e) { try { Error?.Invoke(e); } catch { } }
            if (_gap > TimeSpan.Zero) await Task.Delay(_gap).ConfigureAwait(false);
        }
    }
}
