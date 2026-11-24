using Android.App;
using Android.Content;
using Android.Graphics.Drawables;
using Streemu.Net;
using LogApp = Streemu.Util.Log;

namespace Streemu.Proto.Avisos;

/// <summary>
/// "Fulano está ao vivo": o pop-up de quando um amigo do grupo aberto começa a transmitir. A sala já traz quem está ao vivo; aqui só se decide se
/// aquela live merece um aviso: de amigo (nunca a própria), começada agora há pouco (ao reconectar a sala a lista traz lives antigas, que não
/// viram aviso), uma vez por live, e com o app fora da tela (na tela a pessoa já vê). Quando a live acaba o aviso some sozinho.
/// Só vale para o grupo que está aberto: o app mantém uma sala por vez.
/// </summary>
static class AvisoDeLive
{
    public const string Canal = "avisos-lives";
    const int IdBase = 6000;
    static readonly TimeSpan Recente = TimeSpan.FromMinutes(3);
    static readonly object _trava = new();
    static readonly HashSet<string> _jaAvisadas = new();
    static readonly Dictionary<string, int> _naTela = new();

    public static void Preparar(Context c)
    {
        var canal = new NotificationChannel(Canal, "Lives dos amigos", NotificationImportance.High) { Description = "Quando um amigo do seu grupo começa a transmitir." };
        canal.EnableVibration(true);
        ((NotificationManager)c.GetSystemService(Context.NotificationService)!).CreateNotificationChannel(canal);
    }

    /// <summary>Chamado a cada mudança da sala. Ex.: <c>AvisoDeLive.Conferir(app, sala.Friends);</c></summary>
    public static void Conferir(Context c, IEnumerable<Member> amigos)
    {
        if (!AvisosDeSegundoPlano.Ligado(c) || !EnvioArquivos.NotificacaoArquivos.PodeNotificar(c)) return;
        var aoVivo = amigos.Where(m => m.Live != null).ToList();
        lock (_trava)
        {
            RemoverAsQueAcabaram(c, aoVivo);
            foreach (var amigo in aoVivo)
                if (_jaAvisadas.Add(amigo.Live!.StreamId) && Recem(amigo.Live) && !Sessao.TelaVisivel) Avisar(c, amigo);
        }
    }

    static bool Recem(LiveInfo live) => DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(live.SinceUnixMs) < Recente;

    static void RemoverAsQueAcabaram(Context c, List<Member> aoVivo)
    {
        var ainda = aoVivo.Select(m => m.Live!.StreamId).ToHashSet();
        foreach (var (stream, id) in _naTela.Where(k => !ainda.Contains(k.Key)).ToList())
        {
            try { ((NotificationManager)c.GetSystemService(Context.NotificationService)!).Cancel(id); } catch (Exception e) { LogApp.Info("[avisos] tirar aviso de live: " + e.Message); }
            _naTela.Remove(stream);
        }
    }

    static void Avisar(Context c, Member amigo)
    {
        try
        {
            Preparar(c);
            int id = IdBase + (amigo.Live!.StreamId.GetHashCode() & 0xFFF);
            var abrir = PendingIntent.GetActivity(c, id, new Intent(c, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop | ActivityFlags.ReorderToFront), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            string jogo = amigo.Live!.Game.Trim();
            var n = new Notification.Builder(c, Canal)
                .SetSmallIcon(Icon.CreateWithResource(c, Resource.Drawable.ic_ao_vivo)!)!
                .SetContentTitle($"{amigo.Nick} está ao vivo")!.SetContentText(jogo.Length > 0 ? jogo : "Abriu uma live no seu grupo")!
                .SetCategory(Notification.CategoryEvent)!.SetAutoCancel(true)!.SetContentIntent(abrir)!
                .SetTimeoutAfter((long)TimeSpan.FromHours(3).TotalMilliseconds)!.Build()!;
            ((NotificationManager)c.GetSystemService(Context.NotificationService)!).Notify(id, n);
            _naTela[amigo.Live.StreamId] = id;
        }
        catch (Exception e) { LogApp.Info($"[avisos] aviso de live não saiu ({e.GetType().Name}: {e.Message})"); }
    }
}
