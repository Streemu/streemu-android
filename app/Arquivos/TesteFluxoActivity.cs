#if DEBUG
using Android.App;
using Android.OS;
using Streemu.Arquivos;
using Streemu.Arquivos.Teste;
using Logcat = Android.Util.Log;

namespace Streemu.Proto.EnvioArquivos;

/// <summary>
/// Só Debug (spec 030, T5 e T6). Roda o fluxo inteiro do envio de arquivo com os DOIS lados neste aparelho (sala de mentira e túneis em 127.0.0.1),
/// ligado ao serviço em primeiro plano e às notificações de verdade. O lado que manda lê pelo <see cref="Escolha"/> e o que recebe entrega em
/// Downloads/Streemu pelo <see cref="Recebidos"/>. Histórico e lista de pedidos em curso ficam em arquivos (sobrevivem a matar o app).
/// Resultado no logcat (tag Streemu, prefixo FLUXO).
/// adb shell am start -n br.com.streemu.proto.arquivos/streemu.proto.arquivos.TesteFluxoActivity --es uris file:///data/local/tmp/a.bin,file:///data/local/tmp/b.bin
/// Extras: <c>--ez estragar true</c>; <c>--ez manual true</c> (não aceita sozinho: o pedido aparece como notificação com Aceitar/Recusar);
/// <c>--ez dados true</c> (finge rede de dados móveis: acima de 100 MB o recebimento pergunta o gasto; com <c>--es responder_dados sim|nao</c> responde 6 s depois),
/// <c>--ei espera_s 120</c> (quanto esperar o fim); <c>--ei sdk 28 --ez permissao true</c> (finge Android 9).
/// </summary>
[Activity(Name = "streemu.proto.arquivos.TesteFluxoActivity", Label = "Teste fluxo", Exported = true)]
public class TesteFluxoActivity : Activity
{
    bool _dados;
    string? _responderDados;

    protected override void OnCreate(Bundle? b)
    {
        base.OnCreate(b);
        var i = Intent;
        if (i != null && i.HasExtra("sdk")) Recebidos.SdkForcado = i.GetIntExtra("sdk", 0);
        if (i != null && i.HasExtra("permissao")) Recebidos.PermissaoForcada = i.GetBooleanExtra("permissao", false);
        if (i?.GetBooleanExtra("estragar", false) == true) System.Environment.SetEnvironmentVariable("STREEMU_TEST_FILME_ESTRAGAR", "1");
        var uris = (i?.GetStringExtra("uris") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => Android.Net.Uri.Parse(s)!).ToList();
        bool manual = i?.GetBooleanExtra("manual", false) == true;
        int espera = i?.GetIntExtra("espera_s", 120) ?? 120;
        bool retomar = i?.GetBooleanExtra("retomar", false) == true;
        _dados = i?.GetBooleanExtra("dados", false) == true;
        _responderDados = i?.GetStringExtra("responder_dados");
        Streemu.Util.Log.Line += l => Logcat.Info("Streemu", "LOG " + l);   // o que o Download e o Envio dizem (ex.: "continuando o download")
        _ = Task.Run(() => retomar ? Retomar(espera) : Rodar(uris, manual, espera));
        Finish();
    }

    /// <summary>Depois de matar o app no meio: B pede de novo pelo histórico, A atende e o download continua de onde parou.</summary>
    async Task Retomar(int espera)
    {
        try
        {
            var (pa, pb) = (new SalaDeMentira(), (SalaDeMentira?)null);
            var sala = pa;
            var (ta, tb) = (sala.Criar("c-a", "Ana"), sala.Criar("c-b", "Bia"));
            var a = Montar(ta, "c-b", "Bia", "A"); var b = Montar(tb, "c-a", "Ana", "B");
            ta.Fluxo = a; tb.Fluxo = b;
            _ = Task.Run(async () => { while (true) { await Task.Delay(1000); a.Tick(); b.Tick(); } });
            var parou = b.Historico.Itens.FirstOrDefault(h => h.Sentido == Sentido.Recebido && h.Resultado == Resultado.Parou) ?? throw new Exception("não há item \"parou\" no histórico de B");
            Logcat.Info("Streemu", $"FLUXO pedir de novo ({parou.Nome}): " + (await b.PedirDeNovoAsync(parou) ?? "saiu"));
            await Esperar(() => a.Pedidos.Any(p => p.Fase == FaseArquivo.PedidoDeNovo), "o pedido chegar em A");
            ServicoArquivos.Iniciar(this);
            Logcat.Info("Streemu", "FLUXO A atende: " + (await a.MandarDeNovoAsync(a.Pedidos.First(p => p.Fase == FaseArquivo.PedidoDeNovo).Pedido) ?? "ok"));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Esperar(() => b.Pedidos.Any(p => p.Terminado) && a.Pedidos.Any(p => p.Terminado), "o fim", espera);
            Logcat.Info("Streemu", $"FLUXO retomada fim A={a.Pedidos.First().Fase} B={b.Pedidos.First().Fase} em {sw.Elapsed.TotalSeconds:N1} s");
            foreach (var h in b.Historico.Itens) Logcat.Info("Streemu", $"FLUXO historico B {h.Resultado} {h.Nome} -> {h.Caminho}");
        }
        catch (Exception e) { Logcat.Error("Streemu", "FLUXO erro " + e); }
    }

    async Task Rodar(List<Android.Net.Uri> uris, bool manual, int espera)
    {
        try
        {
            var escolha = await Escolha.LerAsync(this, uris, CancellationToken.None);
            foreach (var r in escolha.Recusados) Logcat.Info("Streemu", $"FLUXO recusado {r.Nome}: {r.Motivo}");
            var sala = new SalaDeMentira();
            var (pa, pb) = (sala.Criar("c-a", "Ana"), sala.Criar("c-b", "Bia"));
            var a = Montar(pa, "c-b", "Bia", "A"); var b = Montar(pb, "c-a", "Ana", "B");
            pa.Fluxo = a; pb.Fluxo = b;
            _ = Task.Run(async () => { while (true) { await Task.Delay(1000); a.Tick(); b.Tick(); } });
            var enviar = escolha.Aceitos.Select(x => new ArquivoParaEnviar(x.Uri.ToString()!, x.Nome, x.Tamanho, x.Identidade)).ToList();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ServicoArquivos.Iniciar(this);   // como a tela faz: com o app na frente, ao oferecer
            Logcat.Info("Streemu", "FLUXO oferta: " + (await a.OferecerAsync("c-b", enviar) ?? "saiu"));
            await Esperar(() => b.Pedidos.Count > 0, "a oferta chegar");
            if (manual) Logcat.Info("Streemu", "FLUXO esperando o aceite pela notificação");
            else Logcat.Info("Streemu", "FLUXO aceite: " + (await b.AceitarAsync(b.Pedidos[0].Pedido) ?? "ok"));
            if (_responderDados != null) await ResponderDados(b);
            await Esperar(() => a.Pedidos[0].Terminado && b.Pedidos[0].Terminado, "o fim dos dois lados", espera);
            Logcat.Info("Streemu", $"FLUXO fim A={a.Pedidos[0].Fase} B={b.Pedidos[0].Fase} em {sw.Elapsed.TotalSeconds:N1} s, refeitos={b.Pedidos[0].PedacosRefeitos}, aviso={b.Pedidos[0].Motivo}");
            foreach (var h in b.Historico.Itens) Logcat.Info("Streemu", $"FLUXO historico B {h.Resultado} {h.Nome} -> {h.Caminho}");
        }
        catch (Exception e) { Logcat.Error("Streemu", "FLUXO erro " + e); }
    }

    /// <summary>Espera o aviso de gasto de dados aparecer, deixa a notificação na tela por 6 s (para conferir) e responde como a pessoa faria.</summary>
    async Task ResponderDados(FluxoDeArquivos b)
    {
        await Esperar(() => b.Pedidos[0].Fase == FaseArquivo.ConfirmandoDados, "o aviso de dados móveis");
        Logcat.Info("Streemu", "FLUXO aviso de dados móveis na tela");
        await Task.Delay(6000);
        b.ResponderDados(b.Pedidos[0].Pedido, _responderDados == "sim");
        Logcat.Info("Streemu", "FLUXO respondi " + _responderDados);
    }

    FluxoDeArquivos Montar(SalaDeMentira.Ponto ponto, string amigoId, string amigoNome, string lado)
    {
        string pasta = FilesDir!.AbsolutePath;
        var historico = new Historico(System.IO.Path.Combine(pasta, $"teste-historico-{lado}.json"), () => DateTime.Now, m => Logcat.Info("Streemu", "FLUXO " + m), () => "Downloads/Streemu");
        var guarda = new GuardaDeCurso(System.IO.Path.Combine(pasta, $"teste-curso-{lado}.json"), m => Logcat.Info("Streemu", "FLUXO " + m));
        var f = new FluxoDeArquivos(ponto, new FabricaLocal(ponto.MeuNome), new AparelhoAndroid(this, () => null), historico,
            log: m => Logcat.Info("Streemu", $"FLUXO [{ponto.MeuNome}] {m}"), telemetria: (tipo, dados) => EnvioDeArquivos.EnviarEvento(this, tipo, dados), guarda: guarda);
        f.LembrarNome(amigoId, amigoNome);
        if (_dados) f.RedeMedida = () => true;
        Logcat.Info("Streemu", $"FLUXO recuperados {lado}={f.RecuperarInterrompidos()}");
        PresencaDeArquivos.Ligar(this, f);
        return f;
    }

    static async Task Esperar(Func<bool> cond, string oQue, int segundos = 30)
    {
        var fim = DateTime.UtcNow.AddSeconds(segundos);
        while (!cond()) { if (DateTime.UtcNow > fim) throw new TimeoutException("esperando " + oQue); await Task.Delay(50); }
    }
}
#endif
