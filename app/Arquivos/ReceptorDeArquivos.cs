using Android.App;
using Android.Content;
using Streemu.Arquivos;

namespace Streemu.Proto.EnvioArquivos;

/// <summary>
/// Os botões das notificações do envio de arquivo: Aceitar, Recusar e Cancelar. Aceitar liga o serviço em primeiro plano antes de qualquer
/// outra coisa: tocar num botão de notificação é uma das ações que o Android 12+ aceita para começar um serviço desses com o app em segundo plano.
/// </summary>
[BroadcastReceiver(Enabled = true, Exported = false)]
public class ReceptorDeArquivos : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context == null || intent == null) return;
        string? pedido = intent.GetStringExtra(NotificacaoArquivos.ExtraPedido);
        switch (intent.Action)
        {
            case NotificacaoArquivos.AcaoAceitar when pedido != null && PresencaDeArquivos.FluxoDoPedidoRecebido(pedido) is { } f: Aceitar(context, f, pedido, GoAsync()); break;
            case NotificacaoArquivos.AcaoRecusar when pedido != null && PresencaDeArquivos.FluxoDoPedidoRecebido(pedido) is { } f:
                f.Recusar(pedido);
                NotificacaoArquivos.Remover(context, pedido);
                break;
            case NotificacaoArquivos.AcaoDadosSim or NotificacaoArquivos.AcaoDadosNao when pedido != null && PresencaDeArquivos.FluxoDoPedidoRecebido(pedido) is { } f:
                f.ResponderDados(pedido, intent.Action == NotificacaoArquivos.AcaoDadosSim);
                NotificacaoArquivos.RemoverGastoDeDados(context, pedido);
                break;
            case NotificacaoArquivos.AcaoCancelar: CancelarTudo(); break;
        }
    }

    static void CancelarTudo()
    {
        foreach (var f in PresencaDeArquivos.TodosOsFluxos())
            foreach (var p in f.Pedidos.Where(PresencaDeArquivos.Ativo)) f.Cancelar(p.Pedido);
    }

    static void Aceitar(Context c, FluxoDeArquivos fluxo, string pedido, PendingResult? pendente)
    {
        ServicoArquivos.Iniciar(c);
        _ = Task.Run(async () =>
        {
            try
            {
                if (await fluxo.AceitarAsync(pedido) is string erro) { NotificacaoArquivos.Avisar(c, "Não deu para aceitar o arquivo", erro); PresencaDeArquivos.Reagir(); }
                NotificacaoArquivos.Remover(c, pedido);
            }
            finally { pendente?.Finish(); }
        });
    }
}
