namespace Streemu.Filme;

/// <summary>
/// A fila de blocos de UM espectador do filme pela transmissão (spec 028, 5.2). O <see cref="EnvioFilmeCopia"/> enfileira (nunca espera); a thread de SRT dele chama <see cref="Proximo"/>.
/// Uma saída vive enquanto o espectador está na sessão; a cada época o SRT dele reabre e a saída é <see cref="EnvioFilmeCopia.Conectar"/>-ada de novo.
/// Quem não dá conta do upload não atrasa os outros: passou de <see cref="FilaMaxBytes"/>, a fila dele é jogada fora e recomeça no próximo quadro-chave.
/// </summary>
public sealed class SaidaFilme
{
    /// <summary>Teto da fila: ~3 s de um filme de 40 Mbps. O SRT live descarta por atraso; acumular mais só atrasa quem assiste.</summary>
    public const int FilaMaxBytes = 16_000_000;

    public string Id { get; }
    public string Apelido { get; }
    /// <summary>A época em que esta saída foi conectada; o laço de SRT fecha a conexão quando o envio passa para outra.</summary>
    public int EpocaConectada { get; internal set; } = -1;
    public long BytesEnviados;
    public long BlocosDescartados;

    internal double Inicio = double.NaN;   // só sob o _dist do envio: s do primeiro quadro-chave que este espectador recebeu na época
    internal bool Ativa;                   // só sob o _dist do envio

    readonly object _g = new();
    readonly Queue<byte[]> _fila = new();
    int _bytes;
    bool _esperaChave = true, _fechada;

    public SaidaFilme(string id, string apelido) { Id = id; Apelido = apelido; }

    public int BytesNaFila => Volatile.Read(ref _bytes);

    internal void Zerar(bool esperarChave)
    {
        lock (_g) { _fila.Clear(); _bytes = 0; _esperaChave = esperarChave; }
    }

    /// <summary>Chamado pela thread da época: nunca espera.</summary>
    internal void Enfileirar(byte[] bloco, bool chave)
    {
        lock (_g)
        {
            if (_fechada) return;
            if (_esperaChave)
            {
                if (!chave) return;
                _esperaChave = false;
            }
            if (_bytes + bloco.Length > FilaMaxBytes)
            {
                Interlocked.Add(ref BlocosDescartados, _fila.Count + 1);
                _fila.Clear(); _bytes = 0;
                if (!chave) { _esperaChave = true; return; }
            }
            _fila.Enqueue(bloco);
            _bytes += bloco.Length;
            Monitor.Pulse(_g);
        }
    }

    /// <summary>O próximo bloco para o SRT, ou nulo se nada chegou em <paramref name="esperaMs"/> (ou a saída fechou).</summary>
    public byte[]? Proximo(int esperaMs)
    {
        lock (_g)
        {
            if (_fila.Count == 0 && !_fechada) Monitor.Wait(_g, esperaMs);
            if (_fila.Count == 0) return null;
            var b = _fila.Dequeue();
            _bytes -= b.Length;
            return b;
        }
    }

    /// <summary>O SRT não deu conta de um bloco: o que vem depois não decodifica sem um quadro-chave novo.</summary>
    public void PerderAteChave()
    {
        lock (_g) { Interlocked.Add(ref BlocosDescartados, _fila.Count + 1); _fila.Clear(); _bytes = 0; _esperaChave = true; }
    }

    internal void Fechar()
    {
        lock (_g) { _fechada = true; _fila.Clear(); _bytes = 0; Monitor.PulseAll(_g); }
    }
}
