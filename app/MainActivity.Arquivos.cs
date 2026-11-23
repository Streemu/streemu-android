using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Widget;
using Streemu.Arquivos;
using Streemu.Net;
using Streemu.Proto.EnvioArquivos;
using Streemu.Proto.Ui;

namespace Streemu.Proto;

/// <summary>
/// A aba Arquivos (spec 030): o seletor de arquivos, as permissões e as ações da tela (enviar, aceitar, recusar, cancelar, abrir, pedir de novo).
/// A tela só desenha (<see cref="Telas.Arquivos"/>); o fluxo mora no <see cref="FluxoDeArquivos"/>. Escondida enquanto <see cref="EnvioDeArquivos.Liberado"/> for falso.
/// </summary>
public partial class MainActivity
{
    const int CodigoEscolherArquivos = 46, CodigoPermissaoArquivos = 47, CodigoNotificacaoArquivos = 48, CodigoEscolherPasta = 49;
    const string AvisoDePermissaoDeNotificacao = "arquivos_notificacao_pedida";

    readonly EstadoDeArquivos _estadoArquivos = new();
    bool _ouvindoArquivos, _redesenhoDeArquivosPendente;
    string? _pedidoEsperandoPermissao;

    AcoesDeArquivos AcoesArquivos() => new(
        SelecionarArquivos, TirarArquivoEscolhido, EnviarArquivosEscolhidos,
        AceitarPedidoArquivo, p => Fluxo?.Recusar(p), p => Fluxo?.Cancelar(p),
        (p, ok) => Fluxo?.ResponderRelay(p, ok), (p, ok) => Fluxo?.ResponderDados(p, ok),
        AbrirArquivoRecebido, MostrarArquivoNaPasta, PedirArquivoDeNovo, LimparHistoricoDeArquivos, () => MostrarAba(),
        TrocarPastaDeRecebidos, VoltarParaDownloads, () => { RedeMovel.VoltarAPerguntar(this); MostrarAba(); });

    static FluxoDeArquivos? Fluxo => EnvioDeArquivos.Fluxo;

    /// <summary>Os amigos que estão na sala agora, por nome (a tela não conhece a sala).</summary>
    static List<AmigoDeArquivos> AmigosOnline() =>
        Sessao.Sala is { Connected: true } sala ? sala.Friends.OrderBy(f => f.Nick).Select(f => new AmigoDeArquivos(f.Id, f.Nick)).ToList() : new();

    /// <summary>Liga o redesenho da aba às mudanças do fluxo, uma vez. Muitas mudanças por segundo (cada pedaço recebido) viram no máximo quatro redesenhos.</summary>
    void OuvirArquivos()
    {
        if (_ouvindoArquivos || Fluxo is not { } f) return;
        _ouvindoArquivos = true;
        f.Mudou += () =>
        {
            if (_redesenhoDeArquivosPendente) return;
            _redesenhoDeArquivosPendente = true;
            new Handler(Looper.MainLooper!).PostDelayed(() =>
            {
                _redesenhoDeArquivosPendente = false;
                if (_aba != Aba.Arquivos || IsDestroyed || Fluxo is not { } fluxo) return;
                // só o progresso mudou: atualiza as barras no lugar. Refazer a tela a cada 250 ms a fazia piscar durante o download
                if (Telas.MudouDeForma(fluxo)) MostrarAba(); else Telas.AtualizarProgresso(fluxo);
            }, 250);
        };
    }

    /// <summary>A foto de um amigo online no Início: abre "Enviar arquivo" já com ele escolhido (D9). O que a pessoa tinha escolhido antes continua.</summary>
    void EnviarArquivoPara(Member amigo)
    {
        _estadoArquivos.AmigoId = amigo.Id;
        _estadoArquivos.NoHistorico = false;
        EsquecerSubtela();
        _aba = Aba.Arquivos;
        MostrarAba();
        AoEntrarNaAba(Aba.Arquivos);
    }

    // ------------------------------------------------------------------ Compartilhar → Streemu (D8)

    /// <summary>
    /// Arquivos compartilhados por outro app (repassados pela <see cref="CompartilharActivity"/>): abre a aba Arquivos com eles já escolhidos; a pessoa só
    /// escolhe o amigo e toca em Enviar. Chamado na abertura e em <c>OnNewIntent</c> (a atividade é SingleTask).
    /// </summary>
    void ReceberCompartilhamento(Intent? intent)
    {
        if (intent?.Action != CompartilharActivity.Acao || !EnvioDeArquivos.Liberado) return;
        var uris = Escolha.UrisDe(intent);
        if (uris.Count == 0) return;
        _aba = Aba.Arquivos;
        _estadoArquivos.NoHistorico = false;
        _ = Task.Run(async () =>
        {
            var lidos = await Escolha.LerAsync(this, uris, CancellationToken.None);
            RunOnUiThread(() =>
            {
                foreach (var x in lidos.Aceitos.Where(x => _estadoArquivos.Escolhidos.All(e => e.Identidade != x.Identidade)).Take(MensagemArquivo.MaxArquivos - _estadoArquivos.Escolhidos.Count))
                    _estadoArquivos.Escolhidos.Add(x);
                string aviso = lidos.Recusados.Count > 0 ? $"{lidos.Recusados[0].Nome}: {lidos.Recusados[0].Motivo}"
                    : $"{lidos.Aceitos.Count} arquivo(s) prontos. Escolha o amigo e toque em Enviar arquivos.";
                Streemu.Util.Log.Info($"[arquivo] compartilhado: {lidos.Aceitos.Count} aceito(s), {lidos.Recusados.Count} recusado(s)");
                Toast.MakeText(this, aviso, ToastLength.Long)?.Show();
                if (_raiz != null && _mostrandoInicio) MostrarAba();
            });
        });
    }

    // ------------------------------------------------------------------ escolher e enviar

    void SelecionarArquivos()
    {
        try { StartActivityForResult(Escolha.Seletor(), CodigoEscolherArquivos); }
        catch (Exception e) { Toast.MakeText(this, "Não deu para abrir o seletor de arquivos: " + e.Message, ToastLength.Long)?.Show(); }
    }

    void AoEscolherArquivosParaEnviar(Result resultado, Intent? dados)
    {
        if (resultado != Result.Ok) return;
        var uris = Escolha.UrisDe(dados);
        _ = Task.Run(async () =>
        {
            var lidos = await Escolha.LerAsync(this, uris, CancellationToken.None);
            RunOnUiThread(() =>
            {
                foreach (var x in lidos.Aceitos.Where(x => _estadoArquivos.Escolhidos.All(e => e.Identidade != x.Identidade)).Take(MensagemArquivo.MaxArquivos - _estadoArquivos.Escolhidos.Count))
                    _estadoArquivos.Escolhidos.Add(x);
                if (lidos.Recusados.Count > 0) Toast.MakeText(this, $"{lidos.Recusados[0].Nome}: {lidos.Recusados[0].Motivo}", ToastLength.Long)?.Show();
                MostrarAba();
            });
        });
    }

    void TirarArquivoEscolhido(ArquivoEscolhido x) { _estadoArquivos.Escolhidos.Remove(x); MostrarAba(); }

    void EnviarArquivosEscolhidos()
    {
        if (Atualizacao.BloqueiaSala) { MostrarAtualizacao(); return; }
        if (_estadoArquivos.AmigoId is not string amigo || _estadoArquivos.Escolhidos.Count == 0) return;
        string nome = Sessao.Sala?.Find(amigo)?.Nick ?? "amigo";
        var arquivos = _estadoArquivos.Escolhidos.Select(x => new ArquivoParaEnviar(x.Uri.ToString()!, x.Nome, x.Tamanho, x.Identidade)).ToList();
        PedirNotificacoesSeFaltar();
        _ = Task.Run(async () =>
        {
            string? erro = await EnvioDeArquivos.OferecerAsync(this, amigo, nome, arquivos);
            RunOnUiThread(() =>
            {
                if (erro == null) _estadoArquivos.Escolhidos.Clear();
                Toast.MakeText(this, erro ?? $"Pedido enviado. Esperando {nome} aceitar.", ToastLength.Long)?.Show();
                MostrarAba();
            });
        });
    }

    // ------------------------------------------------------------------ receber

    /// <summary>No Android 8 e 9 o primeiro recebimento pede, com uma frase que diz para quê, a permissão de gravar em Downloads/Streemu (D4).</summary>
    void AceitarPedidoArquivo(string pedido)
    {
        if (Atualizacao.BloqueiaSala) { MostrarAtualizacao(); return; }
        if (Fluxo is { } f && Recebidos.PrecisaPedirPermissao(this, f.Historico.PastaRecebidos)) { ExplicarPermissaoDeArquivos(pedido); return; }
        ContinuarAceite(pedido);
    }

    void ExplicarPermissaoDeArquivos(string pedido) =>
        new AlertDialog.Builder(this)!
            .SetTitle("Guardar na pasta Downloads")!
            .SetMessage("Para guardar o arquivo em Downloads/Streemu, o Android do seu celular pede permissão para gravar arquivos. Se você não permitir, o arquivo fica numa pasta interna do Streemu, mais difícil de achar.")!
            .SetPositiveButton("Continuar", (_, _) => { _pedidoEsperandoPermissao = pedido; PermissaoDeArmazenamento.Pedir(this, CodigoPermissaoArquivos); })!
            .SetNegativeButton("Agora não", (_, _) => ContinuarAceite(pedido))!
            .Show();

    /// <summary>A resposta do sistema à permissão de gravar: com ela ou sem ela o arquivo chega (sem ela, na pasta interna, com um aviso).</summary>
    void AoResponderPermissaoDeArquivos()
    {
        if (_pedidoEsperandoPermissao is not string p) return;
        _pedidoEsperandoPermissao = null;
        ContinuarAceite(p);
    }

    void ContinuarAceite(string pedido)
    {
        PedirNotificacoesSeFaltar();
        _ = Task.Run(async () =>
        {
            string? erro = await EnvioDeArquivos.AceitarAsync(this, pedido);
            RunOnUiThread(() => { if (erro != null) Toast.MakeText(this, erro, ToastLength.Long)?.Show(); MostrarAba(); });
        });
    }

    /// <summary>Android 13+: pede a permissão de notificar uma vez, no primeiro envio ou recebimento. Sem ela tudo funciona; só os avisos ficam dentro do app.</summary>
    void PedirNotificacoesSeFaltar()
    {
        var prefs = GetSharedPreferences("streemu", FileCreationMode.Private)!;
        if (NotificacaoArquivos.PodeNotificar(this) || prefs.GetBoolean(AvisoDePermissaoDeNotificacao, false)) return;
        prefs.Edit()!.PutBoolean(AvisoDePermissaoDeNotificacao, true)!.Apply();
        RequestPermissions(new[] { "android.permission.POST_NOTIFICATIONS" }, CodigoNotificacaoArquivos);
    }

    // ------------------------------------------------------------------ pasta de recebidos (opção avançada)

    void TrocarPastaDeRecebidos()
    {
        Toast.MakeText(this, "Escolha uma pasta (ou crie uma). O Android não deixa usar a pasta Downloads inteira.", ToastLength.Long)?.Show();
        try { StartActivityForResult(PastaEscolhida.Seletor(), CodigoEscolherPasta); }
        catch (Exception e) { Toast.MakeText(this, "Não deu para abrir o seletor de pastas: " + e.Message, ToastLength.Long)?.Show(); }
    }

    void AoEscolherPastaDeRecebidos(Result resultado, Intent? dados)
    {
        if (resultado != Result.Ok || dados?.Data is not { } arvore || Fluxo is not { } f) return;
        try { f.Historico.PastaRecebidos = PastaEscolhida.Guardar(this, arvore); }
        catch (IOException e) { Toast.MakeText(this, e.Message, ToastLength.Long)?.Show(); return; }
        Toast.MakeText(this, $"Os recebidos agora vão para {Recebidos.TextoDoDestino(this, f.Historico.PastaRecebidos)}.", ToastLength.Long)?.Show();
        MostrarAba();
    }

    void VoltarParaDownloads() { if (Fluxo is { } f) f.Historico.PastaRecebidos = ""; MostrarAba(); }

    // ------------------------------------------------------------------ histórico

    ArquivoPublicado PublicadoDe(ItemHistorico i)
    {
        string? c = i.Caminho;
        bool uri = c != null && c.StartsWith("content://", StringComparison.Ordinal);
        return new ArquivoPublicado(i.Nome, Recebidos.TextoDoDestino(this, Fluxo?.Historico.PastaRecebidos), uri ? c : null, uri ? null : c);
    }

    /// <summary>"Abrir": programas, scripts e pacotes de instalação perguntam antes, e o padrão é só mostrar onde está (o Android não tem a marca de "baixado da internet").</summary>
    void AbrirArquivoRecebido(ItemHistorico i)
    {
        if (!TipoArriscado.Eh(i.Nome)) { AbrirDeVerdade(i); return; }
        new AlertDialog.Builder(this)!
            .SetTitle("Abrir esse arquivo?")!
            .SetMessage($"\"{i.Nome}\" pode instalar ou executar programas no seu aparelho. Só abra se você confia em {i.Apelido} e esperava esse arquivo.")!
            .SetPositiveButton("Mostrar na pasta", (_, _) => MostrarArquivoNaPasta(i))!
            .SetNegativeButton("Abrir mesmo assim", (_, _) => AbrirDeVerdade(i))!
            .Show();
    }

    void AbrirDeVerdade(ItemHistorico i)
    {
        if (!Recebidos.Abrir(this, PublicadoDe(i))) Toast.MakeText(this, $"Não achei um app para abrir {i.Nome}. Ele está em {Recebidos.TextoDoDestino(this, Fluxo?.Historico.PastaRecebidos)}.", ToastLength.Long)?.Show();
    }

    void MostrarArquivoNaPasta(ItemHistorico i)
    {
        if (!Recebidos.MostrarNaPasta(this, Fluxo?.Historico.PastaRecebidos))
            Toast.MakeText(this, $"O arquivo está em {Recebidos.TextoDoDestino(this, Fluxo?.Historico.PastaRecebidos)}.", ToastLength.Long)?.Show();
    }

    void PedirArquivoDeNovo(ItemHistorico i)
    {
        if (Fluxo is not { } f) return;
        _ = Task.Run(async () =>
        {
            string? erro = await f.PedirDeNovoAsync(i);
            RunOnUiThread(() => Toast.MakeText(this, erro ?? $"Pedido enviado a {i.Apelido}.", ToastLength.Long)?.Show());
        });
    }

    void LimparHistoricoDeArquivos() { Fluxo?.Historico.Limpar(); MostrarAba(); }
}
