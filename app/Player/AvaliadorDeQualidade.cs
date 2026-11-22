namespace Streemu.Proto.Player;

public enum NivelDaQualidade { Bom, Alerta, Ruim }

public enum CausaDaQualidade { Nenhuma, Rede, Aparelho, Transmissor, Reconectando }

/// <summary>Uma amostra de 2 s da live (o que o Painel já mede). <see cref="FpsDaLive"/> é 0 quando o app não sabe a taxa da qualidade.</summary>
public readonly record struct AmostraDeQualidade(
    bool Conectado, bool SrtAberto, bool PrimeiroVideoVisto, long BytesNoIntervalo,
    long SrtRecebidos, long SrtPerdidos, long SrtDescartados, long BuracosNoIntervalo, double RttMs,
    double EntreguesPorS, double MostradosPorS, double FpsDaLive,
    long RcvbufErrosNoIntervalo, bool FilaCheiaAumentou, string Termico, double Segundos = 2);

/// <summary>O que a tela mostra: nada (Bom), Alerta, Ruim, nada porque o problema virou o normal (Recolhido) ou o verde "Tudo certo de novo" (Normalizado).</summary>
public enum EstadoDoAviso { Bom, Alerta, Ruim, Recolhido, Normalizado }

/// <summary><see cref="Nivel"/> e <see cref="Causa"/> são o diagnóstico (continuam valendo recolhido); <see cref="Estado"/> é o que exibir.</summary>
public readonly record struct ResultadoDaQualidade(NivelDaQualidade Nivel, CausaDaQualidade Causa, string Frase, EstadoDoAviso Estado = EstadoDoAviso.Bom);

/// <summary>
/// Diz se a live está boa, em alerta ou ruim e por quê (rede, aparelho, quem transmite ou reconectando). Sem dependência de Android: recebe uma
/// amostra por chamada e devolve o estado a EXIBIR, já com histerese: Ruim e reconectando na primeira amostra, Alerta com 2 seguidas,
/// de volta a Bom com 2 boas seguidas, e cada estado fica pelo menos 3 s (subir de nível é na hora). Os primeiros 4 s depois do primeiro quadro de cada ligação são ignorados.
/// </summary>
public sealed class AvaliadorDeQualidade
{
    public const double SegundosIgnorados = 4, SegundosMinimosNoEstado = 3;
    public const int AmostrasParaMostrar = 2, AmostrasParaEsconder = 2;
    // episódio: do primeiro estado não-Bom até a volta para Bom; um problema novo até 60 s depois do fim é o mesmo episódio
    public const double SegundosParaRecolher = 30, SegundosParaMesmoEpisodio = 60, SegundosMinimosDoEpisodioParaVerde = 8, SegundosDoVerde = 3;
    public const int AmostrasBoasParaVerde = 3;

    readonly List<double> _entregues = new();   // últimas ~60 s, para o p90 quando a taxa da live não é conhecida
    NivelDaQualidade _nivel;
    CausaDaQualidade _causa;
    double _desdeOPrimeiroQuadro;
    int _boas, _ruinsSeguidas;
    double _noEstado;
    // episódio
    bool _emEpisodio, _recolhido, _verde, _verdeDado;
    double _duracaoDoEpisodio, _foraDoEpisodio, _noVerde;
    NivelDaQualidade _refNivel;   // o último nível visto enquanto recolhido: só piorar em relação a ele reaparece
    CausaDaQualidade _refCausa;

    public ResultadoDaQualidade Atual => Montar(_nivel, _causa, Estado());

    /// <summary>Segundos do episódio atual (ou do último, depois de normalizado): tempo com problema, exibido ou recolhido.</summary>
    public double DuracaoDoEpisodioS => _duracaoDoEpisodio;

    EstadoDoAviso Estado() =>
        _nivel != NivelDaQualidade.Bom ? (_recolhido ? EstadoDoAviso.Recolhido : _nivel == NivelDaQualidade.Ruim ? EstadoDoAviso.Ruim : EstadoDoAviso.Alerta)
        : _verde ? EstadoDoAviso.Normalizado : EstadoDoAviso.Bom;

    /// <summary>Ligação nova (outro túnel): esquece o histórico e volta a ignorar os primeiros segundos.</summary>
    public void Reiniciar()
    {
        _entregues.Clear();
        (_nivel, _causa, _desdeOPrimeiroQuadro) = (NivelDaQualidade.Bom, CausaDaQualidade.Nenhuma, 0);
        (_boas, _ruinsSeguidas, _noEstado) = (0, 0, 0);
        EncerrarEpisodio();
        _verde = false;
    }

    void EncerrarEpisodio() => (_emEpisodio, _recolhido, _verdeDado, _duracaoDoEpisodio, _foraDoEpisodio) = (false, false, false, 0, 0);

    public ResultadoDaQualidade Avaliar(in AmostraDeQualidade a)
    {
        if (!a.PrimeiroVideoVisto) return Atual;
        _desdeOPrimeiroQuadro += a.Segundos;
        if (_desdeOPrimeiroQuadro <= SegundosIgnorados) { Guardar(a.EntreguesPorS); return Atual; }
        var (nivel, causa) = Instantaneo(a);
        Guardar(a.EntreguesPorS);
        return Suavizar(nivel, causa, a.Segundos);
    }

    void Guardar(double entreguesPorS)
    {
        _entregues.Add(entreguesPorS);
        if (_entregues.Count > 30) _entregues.RemoveAt(0);
    }

    double Esperada(in AmostraDeQualidade a)
    {
        if (a.FpsDaLive > 0) return a.FpsDaLive;
        if (_entregues.Count == 0) return 0;
        var o = _entregues.ToArray();
        Array.Sort(o);
        return Math.Max(24, o[(int)Math.Floor((o.Length - 1) * 0.9)]);
    }

    (NivelDaQualidade, CausaDaQualidade) Instantaneo(in AmostraDeQualidade a)
    {
        if (!a.Conectado || !a.SrtAberto || a.BytesNoIntervalo <= 0) return (NivelDaQualidade.Ruim, CausaDaQualidade.Reconectando);
        // rede
        double rec = a.SrtRecebidos;
        if ((rec > 0 && a.SrtDescartados > rec * 0.005) || a.BuracosNoIntervalo >= 3) return (NivelDaQualidade.Ruim, CausaDaQualidade.Rede);
        if ((rec > 0 && a.SrtPerdidos > rec * 0.02) || a.BuracosNoIntervalo >= 1 || a.RttMs > 250) return (NivelDaQualidade.Alerta, CausaDaQualidade.Rede);
        // aparelho
        double esperada = Esperada(a);
        double chegando = esperada > 0 ? a.EntreguesPorS / esperada : 1, mostrando = esperada > 0 ? a.MostradosPorS / esperada : 1;
        var nivelAparelho = NivelDaQualidade.Bom;
        if (chegando >= 0.9 && mostrando < 0.5) nivelAparelho = NivelDaQualidade.Ruim;
        else if (chegando >= 0.9 && mostrando < 0.85) nivelAparelho = NivelDaQualidade.Alerta;
        if (a.RcvbufErrosNoIntervalo > 0) nivelAparelho = Pior(nivelAparelho, NivelDaQualidade.Alerta);
        if (a.FilaCheiaAumentou) nivelAparelho = NivelDaQualidade.Ruim;
        nivelAparelho = Pior(nivelAparelho, NivelTermico(a.Termico));
        if (nivelAparelho != NivelDaQualidade.Bom) return (nivelAparelho, CausaDaQualidade.Aparelho);
        // quem transmite
        if (chegando < 0.75) return (NivelDaQualidade.Alerta, CausaDaQualidade.Transmissor);
        return (NivelDaQualidade.Bom, CausaDaQualidade.Nenhuma);
    }

    static NivelDaQualidade NivelTermico(string t) => t switch
    {
        "moderate" => NivelDaQualidade.Alerta,
        "severe" or "critical" or "emergency" or "shutdown" => NivelDaQualidade.Ruim,
        _ => NivelDaQualidade.Bom,
    };

    static NivelDaQualidade Pior(NivelDaQualidade x, NivelDaQualidade y) => (NivelDaQualidade)Math.Max((int)x, (int)y);

    ResultadoDaQualidade Suavizar(NivelDaQualidade nivel, CausaDaQualidade causa, double segundos)
    {
        _noEstado += segundos;
        if (_nivel != NivelDaQualidade.Bom) _duracaoDoEpisodio += segundos;
        else if (_emEpisodio && (_foraDoEpisodio += segundos) > SegundosParaMesmoEpisodio) EncerrarEpisodio();
        if (_verde && (_noVerde += segundos) >= SegundosDoVerde) _verde = false;
        bool podeTrocar = _noEstado >= SegundosMinimosNoEstado;
        if (nivel == NivelDaQualidade.Bom)
        {
            _ruinsSeguidas = 0;
            _boas++;
            if (_boas >= AmostrasParaEsconder && _nivel != NivelDaQualidade.Bom && podeTrocar) Definir(NivelDaQualidade.Bom, CausaDaQualidade.Nenhuma);
            // normalizou: um episódio de pelo menos 8 s ganha o verde, com 3 boas seguidas (e uma vez só por fim de episódio)
            if (_boas >= AmostrasBoasParaVerde && _nivel == NivelDaQualidade.Bom && _emEpisodio && !_verdeDado && _duracaoDoEpisodio >= SegundosMinimosDoEpisodioParaVerde)
                (_verde, _verdeDado, _noVerde) = (true, true, 0);
            return Atual;
        }
        _boas = 0;
        _ruinsSeguidas++;
        if (_nivel == NivelDaQualidade.Bom)
        {
            // Ruim (e reconectando) aparece na hora; Alerta com 2 amostras ruins seguidas
            if (nivel == NivelDaQualidade.Ruim || _ruinsSeguidas >= AmostrasParaMostrar) Definir(nivel, causa);
        }
        else if (nivel > _nivel) Definir(nivel, causa);   // sobe na hora
        else if ((nivel < _nivel || causa != _causa) && podeTrocar) Definir(nivel, causa);
        // o mesmo estado por 30 s vira o normal: recolhe
        if (!_recolhido && _nivel != NivelDaQualidade.Bom && _noEstado >= SegundosParaRecolher) (_recolhido, _refNivel, _refCausa) = (true, _nivel, _causa);
        return Atual;
    }

    void Definir(NivelDaQualidade nivel, CausaDaQualidade causa)
    {
        if (nivel == _nivel && causa == _causa) return;
        var nivelAntes = _nivel;
        (_nivel, _causa, _noEstado) = (nivel, causa, 0);
        if (nivel == NivelDaQualidade.Bom) { _foraDoEpisodio = 0; return; }   // o episódio acabou (e pode voltar em até 60 s)
        _verde = false;
        if (nivelAntes == NivelDaQualidade.Bom)
        {
            if (!_emEpisodio) (_emEpisodio, _duracaoDoEpisodio, _recolhido) = (true, 0, false);   // episódio novo
            (_foraDoEpisodio, _verdeDado) = (0, false);   // o mesmo episódio continua recolhido, se estava
        }
        if (!_recolhido) return;
        // recolhido só reaparece se piorar (em relação ao último visto) ou se a causa virar reconectando
        if (nivel > _refNivel || (causa == CausaDaQualidade.Reconectando && _refCausa != CausaDaQualidade.Reconectando)) _recolhido = false;
        else (_refNivel, _refCausa) = (nivel, causa);
    }

    static ResultadoDaQualidade Montar(NivelDaQualidade nivel, CausaDaQualidade causa, EstadoDoAviso estado) => new(nivel, causa, estado == EstadoDoAviso.Normalizado ? "Tudo certo de novo" : nivel == NivelDaQualidade.Bom ? "Qualidade boa" : causa switch
    {
        CausaDaQualidade.Reconectando => "Reconectando com quem transmite…",
        CausaDaQualidade.Rede => nivel == NivelDaQualidade.Ruim ? "Rede instável: partes da imagem chegam atrasadas" : "Rede oscilando",
        CausaDaQualidade.Aparelho => "O celular não está dando conta (decodificação/temperatura)",
        CausaDaQualidade.Transmissor => "Quem transmite está enviando poucos quadros",
        _ => "Qualidade boa",
    }, estado);
}
