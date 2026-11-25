using Android.Content;
using Android.Content.Res;

namespace Streemu.Proto.Ui;

enum ModoDeTema { Escuro, Claro, Automatico }

/// <summary>
/// A escolha do tema (Mais, Aparência), guardada nas preferências do app. Sem escolha salva o padrão é o escuro, como no app Windows: quem atualiza
/// não vê o app mudar de cara sozinho só porque o celular usa o tema claro.
/// Uso: <c>bool claro = PreferenciaDeTema.EhClaro(this);</c>
/// </summary>
static class PreferenciaDeTema
{
    const string Chave = "tema";

    public static ModoDeTema Ler(Context c)
    {
        string? salvo = c.GetSharedPreferences("streemu", FileCreationMode.Private)!.GetString(Chave, null);
        return Enum.TryParse(salvo, out ModoDeTema modo) && Enum.IsDefined(modo) ? modo : ModoDeTema.Escuro;
    }

    public static void Gravar(Context c, ModoDeTema modo) =>
        c.GetSharedPreferences("streemu", FileCreationMode.Private)!.Edit()!.PutString(Chave, modo.ToString())!.Apply();

    /// <summary>O tema que vale agora: o escolhido ou, no automático, o do celular (o modo noturno do sistema).</summary>
    public static bool EhClaro(Context c) => Resolver(Ler(c), c);

    public static bool Resolver(ModoDeTema modo, Context c) => modo switch
    {
        ModoDeTema.Claro => true,
        ModoDeTema.Escuro => false,
        _ => !CelularEstaNoEscuro(c),
    };

    /// <summary>Lê a configuração do contexto da tela, que o Android atualiza e recria a tela quando o celular troca de tema.</summary>
    public static bool CelularEstaNoEscuro(Context c) =>
        (c.Resources!.Configuration!.UiMode & UiMode.NightMask) == UiMode.NightYes;
}
