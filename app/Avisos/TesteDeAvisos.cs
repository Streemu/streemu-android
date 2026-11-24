#if DEBUG
using Android.App;
using Android.Content;
using Streemu.Arquivos;
using Streemu.Net;

namespace Streemu.Proto.Avisos;

/// <summary>
/// Só no Debug: dispara os avisos sem precisar de um amigo online (os avisos de verdade vêm da sala). Roda no processo do app, então também vale para
/// "app fechado, serviço vivo". Ex.: <c>adb shell am broadcast -n br.com.streemu.proto.spike/.TesteDeAvisos -a br.com.streemu.proto.TESTE_AVISO --es tipo live</c>
/// (tipo: <c>live</c> ou <c>arquivo</c>).
/// </summary>
[BroadcastReceiver(Enabled = true, Exported = true, Name = "br.com.streemu.proto.TesteDeAvisos")]
[IntentFilter(new[] { "br.com.streemu.proto.TESTE_AVISO" })]
public class TesteDeAvisos : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context == null || intent == null) return;
        switch (intent.GetStringExtra("tipo"))
        {
            case "live": FalsaLive(context); break;
            case "arquivo": FalsoPedido(context); break;
        }
    }

    static void FalsaLive(Context c)
    {
        long agora = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var live = new LiveInfo("teste-" + agora, "DOKAPON KINGDOM CONNECT", "720p60", agora, 0);
        AvisoDeLive.Conferir(c, new[] { new Member("amigo-teste", "Amigo Teste", "0.1", live) });
    }

    static void FalsoPedido(Context c)
    {
        var oferta = new OfertaDeArquivos("teste" + DateTime.Now.Ticks, new[] { new ArquivoOferecido("foto-da-viagem.png", 4_200_000, "4200000-teste") });
        EnvioArquivos.NotificacaoArquivos.Preparar(c);
        EnvioArquivos.NotificacaoArquivos.NotificarPedido(c, new PedidoEmCurso(Sentido.Recebido, "amigo-teste", "Amigo Teste", oferta, DateTime.Now));
    }
}
#endif
