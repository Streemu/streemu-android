#if DEBUG
using Android.App;
using Android.Content;
using Android.OS;
using Logcat = Android.Util.Log;

namespace Streemu.Proto.EnvioArquivos;

/// <summary>
/// Só Debug (spec 030, T4). Lê arquivos por endereço (como o seletor devolve) e escreve nome, tamanho e identidade no logcat (prefixo ESCOLHA).
/// adb shell am start -n br.com.streemu.proto.arquivos/streemu.proto.arquivos.TesteEscolhaActivity --es uris "content://media/external/downloads/1,content://..."
/// Sem <c>--es uris</c>, abre o seletor de verdade e lê o que a pessoa escolher.
/// </summary>
[Activity(Name = "streemu.proto.arquivos.TesteEscolhaActivity", Label = "Teste escolha", Exported = true)]
public class TesteEscolhaActivity : Activity
{
    const int Codigo = 77;

    protected override void OnCreate(Bundle? b)
    {
        base.OnCreate(b);
        if (Intent?.GetStringExtra("uris") is { Length: > 0 } lista)
        {
            _ = Ler(lista.Split(',').Select(s => Android.Net.Uri.Parse(s)!).ToList());
            Finish();
            return;
        }
        StartActivityForResult(Escolha.Seletor(), Codigo);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == Codigo && resultCode == Result.Ok) _ = Ler(Escolha.UrisDe(data));
        Finish();
    }

    async Task Ler(IReadOnlyList<Android.Net.Uri> uris)
    {
        try
        {
            var r = await Escolha.LerAsync(this, uris, CancellationToken.None);
            foreach (var a in r.Aceitos) Logcat.Info("Streemu", $"ESCOLHA aceito nome={a.Nome} tamanho={a.Tamanho} id={a.Identidade}");
            foreach (var x in r.Recusados) Logcat.Info("Streemu", $"ESCOLHA recusado nome={x.Nome} motivo={x.Motivo}");
        }
        catch (Exception e) { Logcat.Error("Streemu", "ESCOLHA erro " + e); }
    }
}
#endif
