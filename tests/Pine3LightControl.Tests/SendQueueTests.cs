using Pine3LightControl.Core.Lights;
using Xunit;

public class SendQueueTests
{
    [Fact]
    public async Task Latest_value_per_key_wins()
    {
        var q = new SendQueue(TimeSpan.FromMilliseconds(20));
        var sent = new List<int>();
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        // O primeiro envio fica preso até libertarmos; entretanto chegam 50 valores.
        q.Post("b", async () => { started.SetResult(); await gate.Task; lock (sent) sent.Add(-1); });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 50; i++)
        {
            int v = i;
            q.Post("b", () => { lock (sent) sent.Add(v); return Task.CompletedTask; });
        }
        gate.SetResult();
        await q.WhenIdle().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { -1, 49 }, sent);
    }

    [Fact]
    public async Task Reposting_a_key_moves_it_to_the_end()
    {
        var q = new SendQueue(TimeSpan.Zero);
        var sent = new List<string>();
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        q.Post("first", async () => { started.SetResult(); await gate.Task; sent.Add("first"); });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        q.Post("power", () => { sent.Add("on"); return Task.CompletedTask; });
        q.Post("color", () => { sent.Add("color"); return Task.CompletedTask; });
        q.Post("power", () => { sent.Add("off"); return Task.CompletedTask; });
        gate.SetResult();
        await q.WhenIdle().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "first", "color", "off" }, sent);
    }

    [Fact]
    public async Task Errors_are_reported_and_queue_continues()
    {
        var q = new SendQueue(TimeSpan.Zero);
        Exception? err = null;
        q.Error += e => err = e;
        bool second = false;
        q.Post("a", () => throw new InvalidOperationException("boom"));
        q.Post("b", () => { second = true; return Task.CompletedTask; });
        await q.WhenIdle().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("boom", err?.Message);
        Assert.True(second);
    }
}
