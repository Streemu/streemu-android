using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using Streemu.Net;
using Streemu.Proto.Transmissao;
using Streemu.Proto.Ui;
using Logcat = Android.Util.Log;

namespace Streemu.Proto;

/// <summary>
/// Transmitir a tela: a tela Transmitir (qualidade e começar), as permissões e o pedido de captura, que voltam para a Activity.
/// </summary>
public partial class MainActivity
{
    // ================================================================================================ transmitir a tela

    const int CodigoPermissoes = 41, CodigoProjecao = 42;

    /// <summary>
    /// "Transmitir a tela": primeiro as permissões (notificação no Android 13+, que o serviço em primeiro plano usa; áudio, só com "Som do celular" e opcional: sem ele a live sai só com imagem),
    /// depois o pedido do Android para capturar a tela, e no resultado o serviço começa. A resposta das permissões não bloqueia nada: a captura segue com o que foi dado.
    /// </summary>
    void Transmitir()
    {
        if (Atualizacao.BloqueiaSala) { MostrarAtualizacao(); return; }
        if (Sessao.Sala is not { Connected: true }) { Toast.MakeText(this, "Conecte-se ao grupo para transmitir.", ToastLength.Long)?.Show(); return; }
        if (ServicoTransmissao.AoVivo || ServicoTransmissao.Preparando) return;
        var faltam = new List<string>();
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && CheckSelfPermission("android.permission.POST_NOTIFICATIONS") != Permission.Granted) faltam.Add("android.permission.POST_NOTIFICATIONS");
        if (SomDaTransmissao.Ligado(this) && !AudioPermitido()) faltam.Add("android.permission.RECORD_AUDIO");   // "Sem som": nem pergunta
        if (faltam.Count > 0) RequestPermissions(faltam.ToArray(), CodigoPermissoes);
        else PedirProjecao();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == CodigoPermissoes) PedirProjecao();
    }

    void PedirProjecao()
    {
        try
        {
            var gerente = (Android.Media.Projection.MediaProjectionManager)GetSystemService(MediaProjectionService)!;
            StartActivityForResult(gerente.CreateScreenCaptureIntent(), CodigoProjecao);
        }
        catch (Exception e) { Toast.MakeText(this, "Não deu para pedir a captura da tela: " + e.Message, ToastLength.Long)?.Show(); }
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != CodigoProjecao) return;
        if (resultCode != Result.Ok || data == null) { Toast.MakeText(this, "A captura da tela não foi permitida.", ToastLength.Short)?.Show(); return; }
        bool audio = SomDaTransmissao.Ligado(this) && AudioPermitido();
        ServicoTransmissao.Iniciar(this, (int)resultCode, data, Qualidade.IndiceEscolhido(this), audio);
    }

    // ------------------------------------------------------------------------------------ a tela Transmitir

    /// <summary>O cartão do Início abre a tela Transmitir no lugar dele (uma subtela, <c>MainActivity.Subtelas.cs</c>).</summary>
    void AbrirTelaTransmitir()
    {
        if (Atualizacao.BloqueiaSala) { MostrarAtualizacao(); return; }
        AbrirSubtela(Subtela.Transmitir);
    }

    /// <summary>"Começar a transmitir": volta ao Início, que passa a mostrar a live, e segue o caminho de sempre (permissões e captura).</summary>
    void ComecarTransmissao()
    {
        FecharSubtela();
        Transmitir();
    }

    bool AudioPermitido() => CheckSelfPermission("android.permission.RECORD_AUDIO") == Permission.Granted;

    void EscolherSom(bool ligado)
    {
        SomDaTransmissao.Escolher(this, ligado);
        MarcarRedesenho();
    }

    void EscolherQualidade(int indice)
    {
        Qualidade.Escolher(this, indice);
        MarcarRedesenho();
    }
}
