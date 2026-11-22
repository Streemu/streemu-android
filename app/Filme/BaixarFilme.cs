using System.Text.Json.Nodes;
using Android.Content;
using Android.OS;
using Android.Util;
using Streemu.Filme;
using Streemu.Net;
using JsonSeguro = Streemu.Util.JsonSeguro;
using LogApp = Streemu.Util.Log;

[assembly: Android.App.UsesPermission(Android.Manifest.Permission.WakeLock)]

namespace Streemu.Proto.Filme;

/// <summary>
/// Cinema, modo B (spec 028 F2b): o celular baixa o filme do anfitrião pela ligação "filme" e depois toca do disco. Faz o que o BaixarAsync do
/// FilmeTela (Windows) faz: filme_quero, ligação, <see cref="Download"/> (retoma o .streemuparcial), "baixando" ao anfitrião a cada 1 s e a
/// conferência do contêiner antes de abrir. Roda fora da tela (a aba Cinema só mostra o <see cref="Estado"/>); a FilmeActivity abre o arquivo pronto
/// e avisa "pronto". Só segue com o app aberto (WakeLock parcial); serviço em primeiro plano fica para depois.
/// </summary>
static class BaixarFilme
{
    public sealed class Estado
    {
        public string Fase = "parado";          // parado | conectando | baixando | conferindo | pronto | erro
        public int Pct;
        public double EtaS = -1, Taxa;
        public string? Mensagem;                // erro ou aviso para o cartão
        public string? Caminho;                 // quando pronto
        public CancellationTokenSource? Cts;
        public bool Ocupado => Fase is "conectando" or "baixando" or "conferindo";
    }

    static readonly Dictionary<string, Estado> Estados = new();

    public static Estado? De(string filme) { lock (Estados) return Estados.GetValueOrDefault(filme); }

    /// <summary>A pasta própria do app (sem permissão de armazenamento): Android/data/app/files/Movies.</summary>
    public static string Pasta(Context c)
    {
        string p = (c.GetExternalFilesDir(Android.OS.Environment.DirectoryMovies) ?? c.FilesDir)!.AbsolutePath;
        Directory.CreateDirectory(p);
        Download.EspacoLivre ??= pasta => { try { return new StatFs(pasta).AvailableBytes; } catch { return null; } };
        return p;
    }

    /// <summary>Quanto já veio de um download parado (para o botão dizer "Continuar"); nulo se não há.</summary>
    public static int? PctParado(Context c, string filme)
    {
        long tam = Download.TamanhoDe(filme);
        return tam > 0 && Download.JaBaixado(Pasta(c), filme) is long f ? (int)Math.Min(100, f * 100 / tam) : null;
    }

    public static void Parar(string filme) { lock (Estados) if (Estados.TryGetValue(filme, out var e)) e.Cts?.Cancel(); }

    /// <summary>Começa (ou continua) o download do filme do anúncio. Volta logo; o resto roda em segundo plano.</summary>
    public static void Iniciar(Context c, JsonObject anuncio)
    {
        string filme = JsonSeguro.Texto(anuncio["filme"]) ?? "", dono = JsonSeguro.Texto(anuncio["anfitriao"]) ?? "o anfitrião", anfitriao = JsonSeguro.Texto(anuncio["de"]) ?? "";
        if (!Identidade.Valida(filme) || anfitriao.Length == 0) return;
        Estado e;
        lock (Estados)
        {
            if (Estados.TryGetValue(filme, out var antigo) && antigo.Ocupado) return;
            Estados[filme] = e = new Estado { Fase = "conectando", Cts = new CancellationTokenSource() };
        }
        string pasta = Pasta(c);
        long tam = Download.TamanhoDe(filme);
        if (Download.FaltaEspaco(pasta, tam - (Download.JaBaixado(pasta, filme) ?? 0)) is string semEspaco)
        {
            e.Fase = "erro"; e.Mensagem = semEspaco; Sessao.Avisar();
            return;
        }
        var app = c.ApplicationContext!;
        _ = Task.Run(() => RodarAsync(app, filme, dono, anfitriao, pasta, e));
        Sessao.Avisar();
    }

    static async Task RodarAsync(Context app, string filme, string dono, string anfitriao, string pasta, Estado e)
    {
        var ct = e.Cts!.Token;
        PowerManager.WakeLock? wl = null;
        PeerLink? link = null;
        string sid = "";
        string? recusa = null;
        bool aceito = false;
        void AoSala(string de, string apelido, JsonObject msg)
        {
            if (de != anfitriao || JsonSeguro.Texto(msg["sessao"]) != sid || link == null) return;
            switch (JsonSeguro.Texto(msg["tipo"]))
            {
                case "filme_aceito" when !aceito:
                    aceito = true;
                    try { link.Connect(JsonSeguro.Texto(msg["codigo"]) ?? ""); }
                    catch (Exception ex) { recusa = $"{apelido} mandou um código inválido ({ex.Message}). Vocês estão em versões diferentes?"; }
                    break;
                case "filme_recusado": recusa = JsonSeguro.Texto(msg["motivo"]) ?? $"{apelido} recusou."; break;
            }
        }
        void Mudar(string fase, string? msg = null) { e.Fase = fase; e.Mensagem = msg; Sessao.Avisar(); }
        try
        {
            wl = (app.GetSystemService(Context.PowerService) as PowerManager)?.NewWakeLock(WakeLockFlags.Partial, "streemu:download");
            wl?.Acquire();
            var sala = Sessao.Sala;
            if (sala is not { Connected: true }) { Mudar("erro", "Sem conexão com o grupo."); return; }
            if (sala.Find(anfitriao) is not { } amigo) { Mudar("erro", $"{dono} saiu da sala do grupo."); return; }
            sid = IdAleatorio(12);
            var lan = Streemu.Proto.Player.PecaDoPlayer.IpLocal();
            Sessao.Mensagem += AoSala;
            link = await PeerLink.CreateAsync(sid, anfitriao, amigo.Nick, iAmSender: false, localMode: false, Sessao.Apelido,
                lan != null && Upnp.IsCarrierOrPrivate(lan) ? lan : null, RelayConfig.Load(null), "filme");
            await sala.SendTo(anfitriao, new JsonObject { ["tipo"] = "filme_quero", ["sessao"] = sid, ["codigo"] = link.MyCode, ["filme"] = filme });
            var ate = DateTime.Now.AddSeconds(25);
            while (DateTime.Now < ate && !link.Connected)
            {
                if (recusa != null) { Mudar("erro", $"{dono} não aceitou: {recusa}"); return; }
                await Task.Delay(100, ct);
            }
            if (!link.Connected) { Mudar("erro", $"Não consegui conectar com {dono} em 25 s. Tente de novo."); return; }
            var t = link.Tunnel;
            string meuId = Sessao.Sala?.MyId ?? Sessao.Conta.ContaId ?? "";
            string meuNome = Sessao.Apelido;
            JsonObject Msg(JsonObject extra)
            {
                var m = new JsonObject { ["type"] = "filme_pronto", ["filme"] = filme, ["pronto"] = false, ["nome"] = meuNome, ["id"] = meuId };
                foreach (var kv in extra) m[kv.Key] = kv.Value?.DeepClone();
                return m;
            }
            var d = new Download(t, filme, pasta);
            var ultimoAviso = DateTime.MinValue;
            string final = await d.RodarAsync(p =>
            {
                e.Fase = p.Fase == "conferindo" ? "conferindo" : p.Fase == "baixando" ? "baixando" : "conectando";
                e.Pct = p.Pct; e.EtaS = p.EtaS; e.Taxa = p.BytesPorSegundo;
                Sessao.Avisar();
                if (p.Fase != "baixando" || DateTime.Now - ultimoAviso < TimeSpan.FromSeconds(1)) return;
                ultimoAviso = DateTime.Now;
                t.SendControl(Msg(new JsonObject { ["baixando"] = new JsonObject { ["pct"] = p.Pct, ["eta_s"] = Math.Round(p.EtaS) }, ["primeiro"] = true }));
            }, ct);
            // veio do aparelho de outra pessoa: só vai ao player se o começo for de um vídeo (spec 028, 5.3)
            if (ConferirContainer.Motivo(final) is string ruim)
            {
                LogApp.Info($"[filme] arquivo baixado recusado, não abri no player: {ruim}");
                try { File.Delete(final); } catch (Exception ex) { LogApp.Info($"[filme] não consegui apagar o arquivo recusado: {ex.GetType().Name}"); }
                Mudar("erro", $"O arquivo que veio de {dono} não é um vídeo que o app reconhece ({ruim}); não abri.");
                return;
            }
            e.Caminho = final; e.Pct = 100;
            Mudar("pronto");   // o "pronto" ao anfitrião sai da FilmeActivity, na ligação dela, quando o player abre
        }
        catch (System.OperationCanceledException) { Mudar("parado", "Download parado. O que já veio fica guardado: toque em Continuar."); }
        catch (Exception ex)
        {
            LogApp.Info("[filme] download parou: " + ex.Message);
            Mudar("erro", $"O download parou: {ex.Message}");
        }
        finally
        {
            Sessao.Mensagem -= AoSala;
            try { if (wl is { IsHeld: true }) wl.Release(); } catch { }
            if (link != null)
            {
                var l = link;
                if (Sessao.Sala is { } s && sid.Length > 0) _ = s.SendTo(anfitriao, new JsonObject { ["tipo"] = "filme_parar", ["sessao"] = sid });
                _ = Task.Run(l.Dispose);
            }
            e.Cts?.Dispose(); e.Cts = null;
        }
    }

    static string IdAleatorio(int n)
    {
        const string abc = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return new string(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>(abc, n));
    }
}
