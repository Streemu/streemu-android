#if DEBUG
using Android.App;
using Android.Graphics;
using Android.OS;
using Android.Widget;
using Streemu.Arquivos;
using Streemu.Arquivos.Teste;
using Streemu.Proto.Ui;
using Logcat = Android.Util.Log;

namespace Streemu.Proto.EnvioArquivos;

/// <summary>
/// Só Debug (spec 030, T9). Mostra a tela "Arquivos" com dados de mentira, sem conta nem sala: um amigo online, um pedido que chegou e um histórico com
/// resultados diferentes. Serve para conferir o desenho e os botões no aparelho (e tirar capturas de tela).
/// adb shell am start -n br.com.streemu.proto.arquivos/streemu.proto.arquivos.TesteTelaArquivosActivity [--ez historico true] [--ez dados true] [--ez escolhidos true]
/// </summary>
[Activity(Name = "streemu.proto.arquivos.TesteTelaArquivosActivity", Label = "Teste tela arquivos", Exported = true)]
public class TesteTelaArquivosActivity : Activity
{
    readonly EstadoDeArquivos _estado = new();
    FluxoDeArquivos _eu = null!;
    FrameLayout _raiz = null!;

    protected override void OnCreate(Bundle? b)
    {
        base.OnCreate(b);
        _estado.NoHistorico = Intent?.GetBooleanExtra("historico", false) == true;
        _estado.MaisOpcoes = Intent?.GetBooleanExtra("mais", false) == true;
        _raiz = new FrameLayout(this);
        _raiz.SetBackgroundColor(Tema.Fundo);
        SetContentView(_raiz);
        Window?.SetStatusBarColor(Tema.Barra);
        Montar();
        Desenhar();
    }

    void Montar()
    {
        var sala = new SalaDeMentira();
        var (pa, pb) = (sala.Criar("c-a", "Ana"), sala.Criar("c-b", "Bia"));
        string pasta = FilesDir!.AbsolutePath;
        var hist = new Historico(null, () => DateTime.Now, _ => { }, () => "Downloads/Streemu");
        _eu = new FluxoDeArquivos(pb, new FabricaLocal("Bia"), new AparelhoAndroid(this, () => null), hist);
        var outro = new FluxoDeArquivos(pa, new FabricaLocal("Ana"), new AparelhoAndroid(this, () => null), new Historico(null, () => DateTime.Now, _ => { }, () => "x"));
        pa.Fluxo = outro; pb.Fluxo = _eu;
        const string idA = "1000-0123456789abcdef0123456789abcdef", idB = "2000-0123456789abcdef0123456789abcdef", idC = "3000-0123456789abcdef0123456789abcdef";
        var agora = DateTime.Now;
        hist.Registrar(new ItemHistorico(Sentido.Recebido, "p1", "c-a", "Ana", "Imagem do ChatGPT.png", 1_600_000, idA, agora.AddMinutes(-3), Resultado.Falhou, null));
        hist.Registrar(new ItemHistorico(Sentido.Recebido, "p2", "c-a", "Ana", "mumu-final.png", 4_200_000, idB, agora.AddHours(-2), Resultado.Conferido, "content://x/mumu-final.png"));
        hist.Registrar(new ItemHistorico(Sentido.Recebido, "p3", "c-a", "Ana", "mod-pack.zip", 1_200_000_000, idC, agora.AddDays(-1), Resultado.Parou, null));
        // a mesma regra da MainActivity: no máximo 4 por segundo, e só refaz a tela se a forma mudou (senão, atualiza o progresso no lugar)
        _eu.Mudou += () =>
        {
            if (_pendente) return;
            _pendente = true;
            new Handler(Looper.MainLooper!).PostDelayed(() =>
            {
                _pendente = false;
                if (Telas.MudouDeForma(_eu)) { Contagem("redesenho"); Desenhar(); } else { Contagem("progresso"); Telas.AtualizarProgresso(_eu); }
            }, 250);
        };
        if (Intent?.GetStringExtra("uris") is { Length: > 0 } uris) { _ = TransferenciaDeVerdade(outro, uris); return; }
        var fakes = new[] { Fake("clipe-da-partida.mp4", 48_000_000), Fake("print.png", 900_000) };
        _ = outro.OferecerAsync("c-b", fakes.Select(f => new ArquivoParaEnviar("file:///nada/" + f.Nome, f.Nome, f.Tamanho, f.Identidade)).ToList());
        if (Intent?.GetBooleanExtra("escolhidos", false) == true)
        {
            _estado.AmigoId = "c-a";
            _estado.Escolhidos.Add(new ArquivoEscolhido(Android.Net.Uri.Parse("file:///nada/a.bin")!, "ferias-2026.mp4", 230_000_000, "230000000-0123456789abcdef0123456789abcdef"));
            _estado.Escolhidos.Add(new ArquivoEscolhido(Android.Net.Uri.Parse("file:///nada/b.bin")!, "foto.jpg", 3_100_000, "3100000-0123456789abcdef0123456789abcdef"));
        }
    }

    bool _pendente;
    int _redesenhos, _progressos;

    void Contagem(string o) { if (o == "redesenho") _redesenhos++; else _progressos++; Logcat.Info("Streemu", $"TELA {o} (redesenhos={_redesenhos}, atualizacoes de progresso={_progressos})"); }

    /// <summary>--es uris file:///...: o outro lado oferece arquivos de verdade e este aceita sozinho; a tela mostra o progresso real (loopback).</summary>
    async Task TransferenciaDeVerdade(FluxoDeArquivos outro, string uris)
    {
        _ = Task.Run(async () => { while (true) { await Task.Delay(1000); _eu.Tick(); outro.Tick(); } });
        var lidos = await Escolha.LerAsync(this, uris.Split(',').Select(s => Android.Net.Uri.Parse(s)!).ToList(), CancellationToken.None);
        await outro.OferecerAsync("c-b", lidos.Aceitos.Select(x => new ArquivoParaEnviar(x.Uri.ToString()!, x.Nome, x.Tamanho, x.Identidade)).ToList());
        while (!_eu.Pedidos.Any(p => p.Fase == FaseArquivo.Esperando)) await Task.Delay(100);
        await _eu.AceitarAsync(_eu.Pedidos.First(p => p.Fase == FaseArquivo.Esperando).Pedido);
    }

    /// <summary>A identidade tem de começar com o tamanho do arquivo (o app recusa a oferta se não bater).</summary>
    static ArquivoOferecido Fake(string nome, long tamanho) => new(nome, tamanho, $"{tamanho}-0123456789abcdef0123456789abcdef");

    void Desenhar()
    {
        var amigos = new List<AmigoDeArquivos> { new("c-a", "Ana"), new("c-g", "Caio") };
        var acoes = new AcoesDeArquivos(() => Aviso("selecionar"), x => { _estado.Escolhidos.Remove(x); Desenhar(); }, () => Aviso("enviar"),
            p => _eu.AceitarAsync(p).ContinueWith(t => Aviso("aceitar: " + (t.Result ?? "ok"))), p => _eu.Recusar(p), p => _eu.Cancelar(p),
            (p, ok) => _eu.ResponderRelay(p, ok), (p, ok) => _eu.ResponderDados(p, ok),
            i => Aviso("abrir " + i.Nome), i => Aviso("mostrar " + i.Nome), i => Aviso("pedir de novo " + i.Nome), () => { _eu.Historico.Limpar(); Desenhar(); }, Desenhar,
            () => Aviso("trocar pasta"), () => Aviso("pasta padrão"), () => Aviso("voltar a perguntar"));
        _raiz.RemoveAllViews();
        _raiz.AddView(Telas.Arquivos(this, _estado, acoes, _eu, amigos));
        if (!_estado.NoHistorico) _raiz.AddView(Telas.RodapeDeArquivos(this, _estado, acoes), new FrameLayout.LayoutParams(Tema.Cheio, Tema.Ajusta, Android.Views.GravityFlags.Bottom));
    }

    void Aviso(string t) { Logcat.Info("Streemu", "TELA " + t); RunOnUiThread(() => Toast.MakeText(this, t, ToastLength.Short)?.Show()); }
}
#endif
