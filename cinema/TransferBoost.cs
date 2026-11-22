namespace Streemu.Util;

/// <summary>No Windows, o app pede prioridade ao sistema durante a transferência (ver o TransferBoost do app Windows). Aqui é um no-op: o Android não limita o laço desse jeito.</summary>
public static class TransferBoost
{
    public static IDisposable Request() => new Nada();
    sealed class Nada : IDisposable { public void Dispose() { } }
}
