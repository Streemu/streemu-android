using Android.App;
using Android.Content;
using LogApp = Streemu.Util.Log;

namespace Streemu.Proto.Avisos;

/// <summary>
/// O interruptor "Avisos de lives e arquivos" (aba Mais) e o que ele liga: o <see cref="ServicoDeAvisos"/>, que mantém a sala do grupo conectada
/// com o app fechado, para o aviso de live e o pedido de arquivo aparecerem por cima da tela como os de qualquer app. Ligado por padrão.
/// Ex.: <c>AvisosDeSegundoPlano.Definir(this, false);</c> desliga o serviço e os avisos de live.
/// </summary>
static class AvisosDeSegundoPlano
{
    const string Chave = "avisos_segundo_plano";
    /// <summary>Código do pedido da permissão de notificar (Android 13+) feito por aqui; <c>MainActivity</c> religa o serviço quando a resposta chega.</summary>
    public const int CodigoPermissao = 4403;

    public static bool Ligado(Context c) => c.GetSharedPreferences("streemu", FileCreationMode.Private)!.GetBoolean(Chave, true);

    public static void Definir(Context c, bool ligado)
    {
        c.GetSharedPreferences("streemu", FileCreationMode.Private)!.Edit()!.PutBoolean(Chave, ligado)!.Apply();
        if (ligado) IniciarSeLigado(c, insistir: true);
        else ServicoDeAvisos.Parar(c);
    }

    /// <summary>
    /// Liga o serviço se o interruptor está ligado e a conta existe. Sem a permissão de notificar (Android 13+) o aviso fixo do serviço não aparece e
    /// os pop-ups tampouco: pede uma vez (sempre, se <paramref name="insistir"/>: a pessoa acabou de ligar o interruptor) e só liga quando ela vier. Chamar só com o app na frente: o Android 12+
    /// não deixa começar serviço em primeiro plano de segundo plano (a exceção é o receptor de boot, que usa <see cref="IniciarDoBoot"/>).
    /// </summary>
    public static void IniciarSeLigado(Context c, bool insistir = false)
    {
        if (!Ligado(c) || !Sessao.Conta.TemConta) return;
        if (!EnvioArquivos.NotificacaoArquivos.PodeNotificar(c))
        {
            var prefs = c.GetSharedPreferences("streemu", FileCreationMode.Private)!;
            if (c is Activity a && (insistir || !prefs.GetBoolean("avisos_pediu_permissao", false)))
            {
                prefs.Edit()!.PutBoolean("avisos_pediu_permissao", true)!.Apply();
                a.RequestPermissions(new[] { "android.permission.POST_NOTIFICATIONS" }, CodigoPermissao);
            }
            return;
        }
        ServicoDeAvisos.Iniciar(c);
    }

    /// <summary>Depois de reiniciar o aparelho ou atualizar o app: o receptor de boot é uma das saídas que o Android aceita para ligar o serviço.</summary>
    public static void IniciarDoBoot(Context c)
    {
        if (!Ligado(c) || !EnvioArquivos.NotificacaoArquivos.PodeNotificar(c)) return;
        LogApp.Info("[avisos] religando o serviço depois do boot ou da atualização");
        ServicoDeAvisos.Iniciar(c);
    }
}
