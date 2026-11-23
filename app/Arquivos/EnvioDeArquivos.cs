using System.Text.Json.Nodes;
using Android.Content;
using Streemu.Arquivos;
using LogApp = Streemu.Util.Log;

namespace Streemu.Proto.EnvioArquivos;

/// <summary>
/// O envio de arquivo do app (spec 030): um <see cref="FluxoDeArquivos"/> só, ligado à sala (<see cref="Sessao"/>), ao seletor, à pasta de
/// recebidos, ao serviço em segundo plano e às notificações. **Liberado para o grupo desde 2026-11-23** (antes ficava escondido até o teste de campo).
/// <see cref="Liberado"/> continua sendo o interruptor: com ele falso o app não mostra a seção, não oferece e não responde a <c>arquivo_*</c>
/// (para quem manda isso parece uma versão antiga, "precisa atualizar") e o Streemu some da folha de compartilhar.
/// </summary>
static class EnvioDeArquivos
{
    /// <summary>Uma propriedade, e não uma constante: assim o compilador não acha inalcançável o que vem depois de um <c>when Liberado</c>.</summary>
    public static bool Liberado { get; } = true;

    static Context? _app;
    static FluxoDeArquivos? _fluxo;
    static Timer? _relogio;
    static readonly object _trava = new();

    /// <summary>Guarda o contexto do app e, com a seção liberada, cria o fluxo já (a recuperação dos pedidos que o sistema matou roda na abertura). Chamado por <see cref="Sessao.Iniciar"/>.</summary>
    public static void Iniciar(Context c)
    {
        _app = c.ApplicationContext;
        CompartilharActivity.Ligar(c, Liberado);   // o Streemu só aparece na folha de compartilhar com a seção liberada
        _ = Fluxo;
    }

    /// <summary>O fluxo, ou nulo se a seção está escondida.</summary>
    public static FluxoDeArquivos? Fluxo
    {
        get
        {
            if (!Liberado || _app == null) return null;
            lock (_trava) return _fluxo ??= Criar(_app);
        }
    }

    /// <summary>Uma mensagem <c>arquivo_*</c> da sala (chamado pelo <see cref="Sessao"/>). Escondida: ignora, como faria uma versão sem o recurso.</summary>
    public static void AoReceber(string de, string apelido, JsonObject msg) => Fluxo?.AoReceber(de, apelido, msg);

    /// <summary>
    /// Oferece os arquivos a um amigo. O serviço em primeiro plano começa AQUI, com o app na frente (único momento em que o Android deixa); se
    /// a oferta não sair, o serviço sai junto. Devolve o motivo para a tela, ou nulo.
    /// </summary>
    public static async Task<string?> OferecerAsync(Context c, string amigoId, string nomeDoAmigo, IReadOnlyList<ArquivoParaEnviar> arquivos)
    {
        if (Fluxo is not { } fluxo) return "O envio de arquivo ainda não está liberado.";
        ServicoArquivos.Iniciar(c);
        fluxo.LembrarNome(amigoId, nomeDoAmigo);
        string? erro = await fluxo.OferecerAsync(amigoId, arquivos);
        if (erro != null) PresencaDeArquivos.Reagir();
        return erro;
    }

    /// <summary>Aceita um pedido que chegou. Também liga o serviço antes, pelo mesmo motivo de <see cref="OferecerAsync"/>.</summary>
    public static async Task<string?> AceitarAsync(Context c, string pedido)
    {
        if (Fluxo is not { } fluxo) return "O envio de arquivo ainda não está liberado.";
        ServicoArquivos.Iniciar(c);
        string? erro = await fluxo.AceitarAsync(pedido);
        if (erro != null) PresencaDeArquivos.Reagir();
        return erro;
    }

    /// <summary>A pessoa voltou ao app (chamado pela tela principal): religa o serviço se o Android o pausou e ainda há pedido andando.</summary>
    public static void AoVoltarParaFrente(Context c)
    {
        if (_fluxo != null) PresencaDeArquivos.AoVoltarParaFrente(c);
    }

    /// <summary>
    /// O evento <c>arquivo_fim</c> do PC (sentido, arquivos, faixa de tamanho, segundos, caminho, resultado, pedaços refeitos) mais o que só faz
    /// sentido no celular: <c>plataforma</c>, a versão do Android e a faixa de memória (sem modelo, sem nome do arquivo nem caminho, spec 030).
    /// Respeita o diagnóstico desligado e o modo anônimo, porque passa pela mesma <see cref="Telemetria"/> dos outros eventos.
    /// </summary>
    internal static void EnviarEvento(Context c, string tipo, System.Text.Json.Nodes.JsonObject dados)
    {
        dados["plataforma"] = "android";
        dados["android"] = (int)Android.OS.Build.VERSION.SdkInt;
        dados["ram_faixa"] = FaixaDeMemoria(c);
        LogApp.Info($"[arquivo] {tipo} {dados.ToJsonString()}");
        Telemetria.Evento(tipo, dados);
    }

    /// <summary>Faixa de memória do aparelho, para separar aparelho fraco de forte sem dizer qual é.</summary>
    static string FaixaDeMemoria(Context c)
    {
        var info = new Android.App.ActivityManager.MemoryInfo();
        (c.GetSystemService(Context.ActivityService) as Android.App.ActivityManager)?.GetMemoryInfo(info);
        long gb = info.TotalMem >> 30;
        return gb < 3 ? "<3GB" : gb < 6 ? "3-6GB" : gb < 12 ? "6-12GB" : ">=12GB";
    }

    static FluxoDeArquivos Criar(Context c)
    {
        string pasta = c.FilesDir!.AbsolutePath;
        var historico = new Historico(Path.Combine(pasta, "arquivos.json"), () => DateTime.Now, LogApp.Info, () => "Downloads/Streemu");
        var guarda = new GuardaDeCurso(Path.Combine(pasta, "arquivos-curso.json"), LogApp.Info);
        var fluxo = new FluxoDeArquivos(new SalaAndroid(), new FabricaAndroid(), new AparelhoAndroid(c, () => historico.PastaRecebidos), historico,
            log: LogApp.Info, telemetria: (tipo, dados) => EnviarEvento(c, tipo, dados), guarda: guarda)
        {
            RedeMedida = () => RedeMovel.Medida(c),
            NaoPerguntarDados = () => RedeMovel.NaoPerguntar(c),
            AoEscolherNaoPerguntarDados = () => RedeMovel.LembrarNaoPerguntar(c),
        };
        PresencaDeArquivos.Ligar(c, fluxo);
        AvisarSeHouveInterrompidos(c, fluxo);
        // o relógio de 1 s cuida dos prazos e do progresso; é leve (uma passada pela lista de pedidos) e fica ligado depois do primeiro uso
        _relogio = new Timer(_ => fluxo.Tick(), null, 1000, 1000);
        return fluxo;
    }

    static void AvisarSeHouveInterrompidos(Context c, FluxoDeArquivos fluxo)
    {
        int n = fluxo.RecuperarInterrompidos();
        if (n > 0) NotificacaoArquivos.Avisar(c, "O Streemu fechou no meio de uma transferência",
            $"{n} {(n == 1 ? "transferência parou" : "transferências pararam")} quando o app foi encerrado. O que já veio fica guardado; peça de novo no histórico para continuar.");
    }
}
