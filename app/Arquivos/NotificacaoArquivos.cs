using Android.App;
using Android.Content;
using Android.Graphics.Drawables;
using Android.OS;
using Streemu.Arquivos;
using LogApp = Streemu.Util.Log;

namespace Streemu.Proto.EnvioArquivos;

/// <summary>
/// As notificações do envio de arquivo (spec 030, seção 4): o andamento (que também é a do serviço em primeiro plano), o pedido que chega com
/// Aceitar e Recusar, o fim de cada pedido e os avisos do sistema (pausou, o app fechou no meio). Dois canais: "Transferências" (discreto) e
/// "Pedidos de arquivo" (o que a pessoa quer ver na hora). No Android 13+ sem a permissão de notificar nada aparece, e o pedido fica só no sino
/// do app; o serviço e as transferências seguem normalmente (o sistema ainda mostra o serviço no gerenciador de tarefas).
/// </summary>
static class NotificacaoArquivos
{
    public const string CanalAndamento = "arquivos", CanalPedidos = "arquivos-pedidos-alto", CanalPedidosAntigo = "arquivos-pedidos";
    public const int IdDoServico = 4301, IdAvisos = 4302;
    public const string AcaoAceitar = "br.com.streemu.proto.ARQUIVO_ACEITAR", AcaoRecusar = "br.com.streemu.proto.ARQUIVO_RECUSAR", AcaoCancelar = "br.com.streemu.proto.ARQUIVO_CANCELAR";
    public const string AcaoDadosSim = "br.com.streemu.proto.ARQUIVO_DADOS_SIM", AcaoDadosNao = "br.com.streemu.proto.ARQUIVO_DADOS_NAO";
    public const string ExtraPedido = "pedido";

    /// <summary>Cria os canais (o Android 8+ exige). Pode chamar quantas vezes quiser.</summary>
    public static void Preparar(Context c)
    {
        var gerente = Gerente(c);
        var andamento = new NotificationChannel(CanalAndamento, "Transferências de arquivo", NotificationImportance.Low) { Description = "Aparece enquanto você envia ou recebe arquivos." };
        andamento.SetShowBadge(false);
        gerente.CreateNotificationChannel(andamento);
        gerente.DeleteNotificationChannel(CanalPedidosAntigo);   // era Default (sem pop-up); a importância de um canal criado não muda
        gerente.CreateNotificationChannel(new NotificationChannel(CanalPedidos, "Pedidos de arquivo", NotificationImportance.High) { Description = "Quando um amigo quer te mandar um arquivo: aparece por cima da tela, com Aceitar e Recusar." });
    }

    static NotificationManager Gerente(Context c) => (NotificationManager)c.GetSystemService(Context.NotificationService)!;

    /// <summary>No Android 13+, o app pode notificar? (Antes disso, sempre.) A tela pede a permissão antes do primeiro envio.</summary>
    public static bool PodeNotificar(Context c) =>
        !OperatingSystem.IsAndroidVersionAtLeast(33) || c.CheckSelfPermission("android.permission.POST_NOTIFICATIONS") == Android.Content.PM.Permission.Granted;

    static PendingIntent AbrirApp(Context c) =>
        PendingIntent.GetActivity(c, 0, new Intent(c, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop | ActivityFlags.ReorderToFront), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;

    static PendingIntent Acao(Context c, string acao, string? pedido, int codigo)
    {
        var i = new Intent(c, typeof(ReceptorDeArquivos)).SetAction(acao);
        if (pedido != null) i.PutExtra(ExtraPedido, pedido);
        return PendingIntent.GetBroadcast(c, codigo, i, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }

    static Icon Icone(Context c) => Icon.CreateWithResource(c, Resource.Drawable.ic_arquivos)!;

    /// <summary>A notificação do serviço: o texto do andamento e, se houver progresso, a barra. <paramref name="pct"/> &lt; 0 = sem barra de progresso conhecida.</summary>
    public static Notification Andamento(Context c, string texto, int pct)
    {
        var b = new Notification.Builder(c, CanalAndamento)
            .SetSmallIcon(Icone(c))!.SetContentTitle("Arquivos do Streemu")!.SetContentText(texto)!
            .SetOngoing(true)!.SetOnlyAlertOnce(true)!.SetContentIntent(AbrirApp(c))!
            .AddAction(new Notification.Action.Builder(Icone(c), "Cancelar", Acao(c, AcaoCancelar, null, 1)).Build())!;
        b.SetProgress(100, Math.Max(0, pct), pct < 0);
        return b.Build()!;
    }

    public static void AtualizarAndamento(Context c, string texto, int pct)
    {
        if (!PodeNotificar(c)) return;
        Notificar(c, IdDoServico, Andamento(c, texto, pct));
    }

    /// <summary>"Bia quer te mandar mumu.png (4 MB)", com Aceitar e Recusar. Sem som novo se o aviso já estiver na tela (só alerta uma vez).</summary>
    public static void NotificarPedido(Context c, PedidoEmCurso p)
    {
        if (!PodeNotificar(c)) return;
        var n = new Notification.Builder(c, CanalPedidos)
            .SetSmallIcon(Icone(c))!.SetContentTitle($"{p.Apelido} quer te mandar um arquivo")!.SetContentText(TextoDaOferta(p.Oferta))!
            .SetAutoCancel(true)!.SetOnlyAlertOnce(true)!.SetContentIntent(AbrirApp(c))!
            .AddAction(new Notification.Action.Builder(Icone(c), "Aceitar", Acao(c, AcaoAceitar, p.Pedido, IdDoPedido(p.Pedido))).Build())!
            .AddAction(new Notification.Action.Builder(Icone(c), "Recusar", Acao(c, AcaoRecusar, p.Pedido, IdDoPedido(p.Pedido) + 1)).Build())!
            .SetTimeoutAfter((long)PedidoEmCurso.Validade.TotalMilliseconds)!.Build()!;
        Notificar(c, IdDoPedido(p.Pedido), n);
    }

    /// <summary>"Baixar usando dados móveis?": o gasto do plano antes do primeiro byte (D5). Baixar ou Agora não, sem abrir o app.</summary>
    public static void NotificarGastoDeDados(Context c, PedidoEmCurso p)
    {
        if (!PodeNotificar(c)) return;
        var n = new Notification.Builder(c, CanalPedidos)
            .SetSmallIcon(Icone(c))!.SetContentTitle("Baixar usando dados móveis?")!
            .SetContentText($"{TextoDaOferta(p.Oferta)} vai gastar {Tamanho(p.Oferta.Total)} do seu plano.")!
            .SetStyle(new Notification.BigTextStyle().BigText($"{TextoDaOferta(p.Oferta)}, de {p.Apelido}, vai gastar {Tamanho(p.Oferta.Total)} do seu plano de dados. Em Wi-Fi não gasta."))!
            .SetOnlyAlertOnce(true)!.SetContentIntent(AbrirApp(c))!
            .AddAction(new Notification.Action.Builder(Icone(c), "Baixar", Acao(c, AcaoDadosSim, p.Pedido, IdDoPedido(p.Pedido) + 4)).Build())!
            .AddAction(new Notification.Action.Builder(Icone(c), "Agora não", Acao(c, AcaoDadosNao, p.Pedido, IdDoPedido(p.Pedido) + 5)).Build())!
            .SetTimeoutAfter((long)PedidoEmCurso.Validade.TotalMilliseconds)!.Build()!;
        Notificar(c, IdDoPedido(p.Pedido) + 3, n);
    }

    public static void RemoverGastoDeDados(Context c, string pedido)
    {
        try { Gerente(c).Cancel(IdDoPedido(pedido) + 3); } catch (Exception e) { LogApp.Info("[arquivo] tirar notificação: " + e.Message); }
    }

    public static void Remover(Context c, string pedido)
    {
        try { Gerente(c).Cancel(IdDoPedido(pedido)); } catch (Exception e) { LogApp.Info("[arquivo] tirar notificação: " + e.Message); }
    }

    /// <summary>Uma linha de resultado quando o pedido acaba (principalmente para quem está com o app em segundo plano).</summary>
    public static void NotificarFim(Context c, PedidoEmCurso p, string titulo, string texto)
    {
        if (!PodeNotificar(c)) return;
        var n = new Notification.Builder(c, CanalAndamento)
            .SetSmallIcon(Icone(c))!.SetContentTitle(titulo)!.SetContentText(texto)!.SetAutoCancel(true)!.SetContentIntent(AbrirApp(c))!.Build()!;
        Notificar(c, IdDoPedido(p.Pedido) + 2, n);
    }

    /// <summary>Um aviso solto (o sistema pausou o serviço, o app fechou no meio). Toque abre o app, o que também zera o limite do Android 15.</summary>
    public static void Avisar(Context c, string titulo, string texto)
    {
        if (!PodeNotificar(c)) return;
        var n = new Notification.Builder(c, CanalAndamento)
            .SetSmallIcon(Icone(c))!.SetContentTitle(titulo)!.SetContentText(texto)!.SetStyle(new Notification.BigTextStyle().BigText(texto))!
            .SetAutoCancel(true)!.SetContentIntent(AbrirApp(c))!.Build()!;
        Notificar(c, IdAvisos, n);
    }

    static void Notificar(Context c, int id, Notification n)
    {
        try { Gerente(c).Notify(id, n); }
        catch (Exception e) { LogApp.Info($"[arquivo] notificação não saiu ({e.GetType().Name})"); }
    }

    static int IdDoPedido(string pedido) => 5000 + ((pedido.GetHashCode() & 0x3FFF) * 4);

    /// <summary>"mumu.png (4,2 MB)" ou "3 arquivos (1,2 GB)". O nome só aparece neste aparelho, na própria notificação.</summary>
    public static string TextoDaOferta(OfertaDeArquivos o) =>
        o.Arquivos.Count == 1 ? $"{o.Arquivos[0].Nome} ({Tamanho(o.Total)})" : $"{o.Arquivos.Count} arquivos ({Tamanho(o.Total)})";

    public static string Tamanho(long bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1e9:N1} GB",
        >= 1_000_000 => $"{bytes / 1e6:N1} MB",
        _ => $"{Math.Max(1, bytes / 1000)} KB",
    };
}
