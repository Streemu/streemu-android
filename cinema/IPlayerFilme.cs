namespace Streemu.Filme;

/// <summary>
/// O que a <see cref="Sessao"/> precisa de um player de filme (mpv no Windows, ExoPlayer no Android): neutro, sem WinForms nem API só do Windows.
/// Os métodos que mudam o player só voltam quando ele aplicou o comando.
/// </summary>
public interface IPlayerFilme : IDisposable
{
    /// <summary>Muda a velocidade de reprodução (1 = normal; o tom é mantido).</summary>
    Task Velocidade(double v);
    /// <summary>Pausa ou retoma.</summary>
    Task Pausar(bool pausar);
    /// <summary>Vai para a posição absoluta, em segundos, de forma exata.</summary>
    Task Ir(double segundos);
    /// <summary>Espera o último <see cref="Ir"/> terminar de carregar. False se estourou o tempo.</summary>
    Task<bool> EsperarIrAsync();
    /// <summary>Posição pelo áudio, em segundos (NaN se não houver).</summary>
    Task<double> PosicaoAudioAsync();
    /// <summary>Posição pelo vídeo/relógio do player, em segundos (NaN se não houver).</summary>
    Task<double> PosicaoAsync();
    /// <summary>O player terminou ou morreu.</summary>
    bool Saiu { get; }
    /// <summary>Quanto o player leva, em s, entre despausar e o som andar de fato (média medida). A Sessao despausa esse tanto antes da hora marcada. Mpv: 0.</summary>
    double AtrasoPartidaS { get; }
    /// <summary>Quanto a velocidade pode se afastar de 1 quando o erro passa de 200 ms (Mpv: 0,05; Android: 0,04).</summary>
    double VelocidadeMaxDesvio { get; }
    /// <summary>Desvio máximo da velocidade para erros até 200 ms, o caso frequente (Mpv: 0,05, como sempre; Android: 0,02, porque lá a velocidade muda o tom junto).</summary>
    double VelocidadeDesvioPequeno { get; }
    /// <summary>Erro, em s, abaixo do qual a Sessao não corrige (Mpv: 0,015; Android: 0,040). Depois de começar a corrigir, só para abaixo de um terço disso.</summary>
    double ZonaMortaS { get; }
}
