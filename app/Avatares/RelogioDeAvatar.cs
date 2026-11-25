using Android.OS;
using Android.Graphics;

namespace Streemu.Proto.Avatares;

/// <summary>
/// Quem está animando agora (no máximo <see cref="Maximo"/>) e em que quadro. É um relógio único, e não de cada <c>Avatar</c>: as abas se reconstroem a cada
/// mudança da sala e a View nova perderia o ponto da animação. Só roda enquanto há alguém animando e alguma tela ouvindo; sem isso para sozinho (spec 005:
/// não pode pesar). Tudo na thread da tela.
/// Uso: <c>RelogioDeAvatar.Alternar(conta, animacao);</c> e, ao desenhar, <c>var quadro = RelogioDeAvatar.QuadroDe(conta) ?? foto;</c>
/// </summary>
static class RelogioDeAvatar
{
    public const int Maximo = 2;
    const int PassoMs = 33;   // até ~30 quadros por segundo; o tempo de cada quadro do GIF manda no que aparece

    sealed record Tocando(string Conta, Animacao Animacao, long InicioMs);

    static readonly List<Tocando> _tocando = new();
    static readonly Handler _tela = new(Looper.MainLooper!);
    static readonly Java.Lang.Runnable _passo = new(Passo);
    static bool _agendado;

    /// <summary>Um quadro novo para desenhar: as Views de avatar que estão animando se invalidam.</summary>
    public static event Action? Quadro;

    public static bool EstaTocando(string conta) => _tocando.Any(t => t.Conta == conta);

    /// <summary>Liga se estava parado, desliga se estava tocando. Passando de <see cref="Maximo"/>, a animação mais antiga para.</summary>
    public static void Alternar(string conta, Animacao animacao)
    {
        if (_tocando.RemoveAll(t => t.Conta == conta) == 0)
        {
            Adicionar(conta, animacao);
            while (_tocando.Count > Maximo) _tocando.RemoveAt(0);   // o toque é vontade da pessoa: vale mais que o que já tocava sozinho
        }
        Quadro?.Invoke();
        Agendar();
    }

    /// <summary>
    /// Liga sozinho (o meu avatar em "Você", os amigos online no Início). Não reinicia a que já toca e, com <see cref="Maximo"/> tocando, não expulsa ninguém:
    /// a tela se reconstrói a cada mudança da sala, e expulsar faria as animações trocarem de lugar a cada redesenho.
    /// </summary>
    public static void Ligar(string conta, Animacao animacao)
    {
        if (EstaTocando(conta) || _tocando.Count >= Maximo) return;
        Adicionar(conta, animacao);
    }

    static void Adicionar(string conta, Animacao animacao)
    {
        _tocando.Add(new Tocando(conta, animacao, SystemClock.UptimeMillis()));
        Agendar();
    }

    /// <summary>Para tudo (o app foi ao fundo ou a tela mudou).</summary>
    public static void ParaTudo()
    {
        _tocando.Clear();
        Quadro?.Invoke();
    }

    /// <summary>O quadro de agora de quem está animando, ou null (a tela desenha a foto parada).</summary>
    public static Bitmap? QuadroDe(string conta)
    {
        var t = _tocando.FirstOrDefault(x => x.Conta == conta);
        if (t == null) return null;
        long decorrido = (SystemClock.UptimeMillis() - t.InicioMs) % Math.Max(1, t.Animacao.DuracaoMs);
        for (int i = 0; i < t.Animacao.Quadros.Length; i++)
        {
            if (decorrido < t.Animacao.TemposMs[i]) return t.Animacao.Quadros[i];
            decorrido -= t.Animacao.TemposMs[i];
        }
        return t.Animacao.Quadros[^1];
    }

    static void Agendar()
    {
        if (_agendado || _tocando.Count == 0) return;
        _agendado = true;
        _tela.PostDelayed(_passo, PassoMs);
    }

    static void Passo()
    {
        _agendado = false;
        if (Quadro == null) _tocando.Clear();   // nenhuma tela ouvindo: ninguém vê, então para (voltar à tela liga de novo)
        if (_tocando.Count == 0) return;
        Quadro?.Invoke();
        Agendar();
    }
}
