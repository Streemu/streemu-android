#if DEBUG
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Streemu.Arquivos;
using Logcat = Android.Util.Log;

namespace Streemu.Proto.EnvioArquivos;

/// <summary>
/// Só Debug (spec 030, T3). Publica um arquivo de teste duas vezes pela pasta de recebidos e escreve o resultado no logcat (tag Streemu, prefixo ARQUIVOS).
/// adb shell am start -n br.com.streemu.proto.arquivos/streemu.proto.arquivos.TesteRecebidosActivity --es arquivo /data/local/tmp/x.bin
/// Extras: <c>--es pasta content://...</c> (pasta já escolhida), <c>--ez escolher true</c> (abre o seletor de pastas de verdade e usa a escolhida),
/// <c>--ei sdk 28</c> (finge Android 9) com <c>--ez permissao true|false</c> (finge ter ou não a permissão de escrita: com ela grava direto em Downloads/Streemu, sem ela cai na pasta do app), <c>--ez abrir true</c> (tenta abrir o primeiro arquivo no app do sistema),
/// <c>--el falta_gb 500</c> (pergunta se cabem N GB e mostra a mensagem de falta de espaço).
/// A segunda publicação do mesmo nome tem que virar "x (2).bin".
/// </summary>
[Activity(Name = "streemu.proto.arquivos.TesteRecebidosActivity", Label = "Teste recebidos", Exported = true)]
public class TesteRecebidosActivity : Activity
{
    const int CodigoPasta = 78, CodigoPermissao = 79;
    string? _origem, _pasta;
    long _faltaGb;
    bool _abrir;

    protected override void OnCreate(Bundle? b)
    {
        base.OnCreate(b);
        var i = Intent;
        _origem = i?.GetStringExtra("arquivo"); _pasta = i?.GetStringExtra("pasta");
        _faltaGb = i?.GetLongExtra("falta_gb", 0) ?? 0;
        _abrir = i?.GetBooleanExtra("abrir", false) ?? false;
        if (i != null && i.HasExtra("sdk")) Recebidos.SdkForcado = i.GetIntExtra("sdk", 0);
        if (i != null && i.HasExtra("permissao")) Recebidos.PermissaoForcada = i.GetBooleanExtra("permissao", false);
        if (i?.GetBooleanExtra("escolher", false) == true) { StartActivityForResult(PastaEscolhida.Seletor(), CodigoPasta); return; }
        if (i?.GetBooleanExtra("pedir", false) == true && Recebidos.PrecisaPedirPermissao(this, _pasta))
        {
            Logcat.Info("Streemu", "ARQUIVOS pedindo a permissão de escrita");
            PermissaoDeArmazenamento.Pedir(this, CodigoPermissao);
            return;
        }
        Iniciar();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != CodigoPermissao) return;
        Logcat.Info("Streemu", "ARQUIVOS permissão de escrita " + (grantResults.Length > 0 && grantResults[0] == Permission.Granted ? "concedida" : "negada"));
        Iniciar();
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != CodigoPasta || resultCode != Result.Ok || data?.Data == null) { Logcat.Info("Streemu", "ARQUIVOS seletor de pastas cancelado"); Finish(); return; }
        try { _pasta = PastaEscolhida.Guardar(this, data.Data); Logcat.Info("Streemu", "ARQUIVOS pasta guardada " + _pasta); }
        catch (Exception e) { Logcat.Error("Streemu", "ARQUIVOS pasta recusada: " + e.Message); Finish(); return; }
        Iniciar();
    }

    void Iniciar()
    {
        _ = Task.Run(Rodar);
        if (!_abrir) Finish();
    }

    void Rodar()
    {
        try
        {
            Logcat.Info("Streemu", $"ARQUIVOS destino={Recebidos.TextoDoDestino(this, _pasta)} via={Recebidos.DestinoPara(this, _pasta)} android={(int)Build.VERSION.SdkInt}");
            if (_faltaGb > 0) Logcat.Info("Streemu", "ARQUIVOS espaço: " + (Recebidos.FaltaEspaco(this, _faltaGb << 30, 0, _pasta) ?? "cabe"));
            if (_origem == null) return;
            ArquivoPublicado? primeiro = null;
            for (int vez = 1; vez <= 2; vez++) { var p = Publicar(vez); primeiro ??= p; }
            if (_abrir && primeiro != null) RunOnUiThread(() => Logcat.Info("Streemu", "ARQUIVOS abrir=" + Recebidos.Abrir(this, primeiro)));
        }
        catch (Exception e) { Logcat.Error("Streemu", "ARQUIVOS erro " + e); }
    }

    ArquivoPublicado Publicar(int vez)
    {
        string local = Path.Combine(Recebidos.PastaDoApp(this), Path.GetFileName(_origem!));
        File.Copy(_origem!, local, overwrite: true);
        var p = Recebidos.PublicarAsync(this, local, _pasta, CancellationToken.None).GetAwaiter().GetResult();
        Logcat.Info("Streemu", $"ARQUIVOS publicado#{vez} nome={p.Nome} onde={p.Onde} uri={p.UriConteudo} caminho={p.Caminho} aviso={p.Aviso}");
        return p;
    }
}
#endif
