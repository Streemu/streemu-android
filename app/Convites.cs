using Android.Content;
using Android.Views;
using Android.Widget;
using Streemu.Net;
using Streemu.Proto.Ui;
using Logcat = Android.Util.Log;

namespace Streemu.Proto;

/// <summary>
/// Grupos pelo celular: criar, convidar (link para compartilhar ou copiar), entrar por código ou link, sair, e o convite que chegou por link
/// (https://www.streemu.com.br/g/&lt;código&gt; ou streemu://grupo/&lt;código&gt;). Só a tela principal chama isto, sempre na thread da tela;
/// a rede roda em Task.Run e o resultado volta por RunOnUiThread. Nunca entra num grupo sem um toque da pessoa em "Entrar"
/// (no "Entrar com convite", digitar o código já é o consentimento).
/// </summary>
static class Convites
{
    const string ChaveCodigo = "convite", ChaveHora = "convite_hora";
    static readonly TimeSpan Validade = TimeSpan.FromHours(24);

    // estado de tela única: um convite, uma entrada e um diálogo de cada vez
    static bool _entrando, _convidando, _criando, _saindo;
    static Android.App.AlertDialog? _dialogo;

    // ------------------------------------------------------------------ convite pendente (guardado em SharedPreferences "streemu")

    static ISharedPreferences Prefs(Context c) => c.GetSharedPreferences("streemu", FileCreationMode.Private)!;

    /// <summary>
    /// Guarda o convite que veio no link que abriu o app (código normalizado e hora). Link inválido é ignorado sem aviso.
    /// Depois de ler, tira o endereço do Intent: girar a tela ou voltar pelo Recentes não deve reaproveitar o mesmo link.
    /// </summary>
    public static void Receber(Context c, Intent? intent)
    {
        if (intent?.DataString is not { Length: > 0 } dado) return;
        intent.SetData(null);
        if (ContaLeve.NormalizarConvite(dado) is not { } codigo) return;
        Prefs(c).Edit()!.PutString(ChaveCodigo, codigo)!.PutLong(ChaveHora, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())!.Apply();
    }

    /// <summary>O convite guardado, se ainda vale (24 h). Vencido ou estragado, é apagado.</summary>
    public static string? Pendente(Context c)
    {
        var p = Prefs(c);
        var codigo = p.GetString(ChaveCodigo, null);
        if (codigo == null) return null;
        long hora = p.GetLong(ChaveHora, 0);
        if (ContaLeve.NormalizarConvite(codigo) is not { } ok || hora <= 0 || DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(hora) > Validade)
        {
            Limpar(c);
            return null;
        }
        return ok;
    }

    // Commit, não Apply: recusado ou aceito, o convite não pode voltar se o app fechar logo em seguida
    public static void Limpar(Context c) => Prefs(c).Edit()!.Remove(ChaveCodigo)!.Remove(ChaveHora)!.Commit();

    /// <summary>A tela está sendo destruída (girou, fechou): o diálogo aberto some junto; o convite continua guardado e volta a ser perguntado.</summary>
    public static void Fechar()
    {
        try { _dialogo?.Dismiss(); } catch (Exception) { }
        _dialogo = null;
    }

    // ------------------------------------------------------------------ "Entrar no grupo?"

    /// <summary>Com conta e um convite guardado: pergunta se a pessoa quer entrar (ou só abre o grupo, se já é dela). Sem conta, não faz nada: a entrada mostra o aviso.</summary>
    public static void ProcessarPendente(MainActivity a)
    {
        if (Atualizacao.BloqueiaSala || !Sessao.Iniciada || !Sessao.Conta.TemConta || _dialogo != null || _entrando) return;
        if (Pendente(a) is not { } codigo) return;
        var id = ContaLeve.IdDoConvite(codigo);
        if (Sessao.Conta.Grupos.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase)) is { } ja)
        {
            Limpar(a);
            Sessao.AbrirSala(ja.Id);
            a.IrParaAoVivo();
            Aviso(a, "Você já está nesse grupo.");
            return;
        }
        var d = Caixa(a)
            .SetTitle("Convite para um grupo")!
            .SetMessage("Você recebeu um convite para entrar num grupo do Streemu.\n\nEntrando, as pessoas do grupo passam a ver quando você está online e ao vivo, e você vê as delas. Só aceite se você conhece quem mandou.")!
            .SetPositiveButton("Entrar", (_, _) => EntrarPeloConvite(a, codigo))!
            .SetNegativeButton("Agora não", (_, _) => Recusar(a))!
            .Create()!;
        d.CancelEvent += (_, _) => Recusar(a);
        Mostrar(d);
    }

    static void Recusar(MainActivity a)
    {
        Limpar(a);
        Aviso(a, "Você não entrou no grupo.");
    }

    static void EntrarPeloConvite(MainActivity a, string codigo)
    {
        Aviso(a, "Entrando no grupo...");
        EntrarNoGrupo(a, codigo, r =>
        {
            // erro de conta (sem rede, sessão encerrada) deixa o convite guardado; qualquer outro (inválido, grupo cheio) o torna inútil
            if (r.Ok || r.Erro != "sem_conta") Limpar(a);
            if (r.Ok) AposEntrar(a, r);
            else Aviso(a, r.Texto ?? "Não deu certo. Tente de novo em instantes.", longo: true);
        });
    }

    /// <summary>Chama o servidor sem travar a tela e devolve o resultado na thread da tela. Uma entrada por vez.</summary>
    static void EntrarNoGrupo(MainActivity a, string codigo, Action<ResultadoGrupo> aoTerminar)
    {
        if (_entrando) { aoTerminar(new(false, "ocupado", "Já estou entrando num grupo. Espere um instante.")); return; }
        _entrando = true;
        var apelido = Sessao.Apelido;
        _ = Task.Run(async () =>
        {
            ResultadoGrupo r;
            try { r = await Sessao.Conta.EntrarNoGrupoAsync(codigo, apelido); }
            catch (Exception e) { Logcat.Warn("Streemu", "entrar no grupo: " + e.GetType().Name); r = new(false, "erro", "Não deu certo. Tente de novo em instantes."); }
            a.RunOnUiThread(() =>
            {
                _entrando = false;
                try { Sessao.Avisar(); aoTerminar(r); }
                catch (Exception e) { Logcat.Warn("Streemu", "entrar no grupo (tela): " + e.GetType().Name); }
            });
        });
    }

    /// <summary>Entrou: abre a sala do grupo novo (a lista de grupos já foi renovada) e mostra a aba Ao vivo.</summary>
    static void AposEntrar(MainActivity a, ResultadoGrupo r)
    {
        if (r.Id != null) Sessao.AbrirSala(r.Id);
        a.IrParaAoVivo();
        var nome = r.Nome is { Length: > 0 } n ? n : Sessao.Conta.Grupos.FirstOrDefault(g => g.Id == r.Id)?.Nome;
        Aviso(a, string.IsNullOrEmpty(nome) ? "Você entrou no grupo." : $"Você entrou no grupo {nome}.");
    }

    // ------------------------------------------------------------------ Entrar com convite (colando código ou link)

    public static void PedirConvite(MainActivity a)
    {
        if (_dialogo != null) return;
        var (campo, erro, moldura) = CampoComErro(a, "Cole o link ou o código", 400);
        var d = Caixa(a)
            .SetTitle("Entrar com convite")!
            .SetMessage("Cole o link que você recebeu ou digite o código do convite.")!
            .SetView(moldura)!
            .SetPositiveButton("Entrar", (IDialogInterfaceOnClickListener?)null)!
            .SetNegativeButton("Cancelar", (_, _) => { })!
            .Create()!;
        Mostrar(d);
        var entrar = d.GetButton((int)DialogButtonType.Positive)!;
        entrar.Click += (_, _) =>
        {
            if (ContaLeve.NormalizarConvite(campo.Text) is not { } codigo) { erro.Text = "Esse convite não está completo. Confira o link ou o código."; erro.Visibility = ViewStates.Visible; return; }
            erro.Visibility = ViewStates.Gone;
            entrar.Enabled = false;
            entrar.Text = "Entrando...";
            EntrarNoGrupo(a, codigo, r =>
            {
                if (r.Ok) { Limpar(a); d.Dismiss(); AposEntrar(a, r); return; }
                entrar.Enabled = true;
                entrar.Text = "Entrar";
                erro.Text = r.Texto ?? "Não deu certo. Tente de novo em instantes.";
                erro.Visibility = ViewStates.Visible;
            });
        };
        campo.RequestFocus();
    }

    // ------------------------------------------------------------------ Criar grupo

    public static void PedirCriacao(MainActivity a)
    {
        if (_dialogo != null) return;
        var (campo, erro, moldura) = CampoComErro(a, "Nome do grupo", 40);
        var d = Caixa(a)
            .SetTitle("Criar grupo")!
            .SetMessage("Depois de criar, você recebe o link para chamar o pessoal.")!
            .SetView(moldura)!
            .SetPositiveButton("Criar", (IDialogInterfaceOnClickListener?)null)!
            .SetNegativeButton("Cancelar", (_, _) => { })!
            .Create()!;
        Mostrar(d);
        var criar = d.GetButton((int)DialogButtonType.Positive)!;
        criar.Click += (_, _) =>
        {
            if (_criando) return;
            var nome = campo.Text?.Trim() ?? "";
            if (nome.Length == 0) { erro.Text = "Dê um nome ao grupo."; erro.Visibility = ViewStates.Visible; return; }
            erro.Visibility = ViewStates.Gone;
            _criando = true;
            criar.Enabled = false;
            criar.Text = "Criando...";
            var apelido = Sessao.Apelido;
            _ = Task.Run(async () =>
            {
                ResultadoGrupo r;
                string? site = null;
                try
                {
                    r = await Sessao.Conta.CriarGrupoAsync(nome, apelido);
                    if (r.Ok) site = await Sessao.Conta.SiteAsync();
                }
                catch (Exception e) { Logcat.Warn("Streemu", "criar grupo: " + e.GetType().Name); r = new(false, "erro", "Não deu certo. Tente de novo em instantes."); }
                a.RunOnUiThread(() =>
                {
                    _criando = false;
                    try
                    {
                        Sessao.Avisar();
                        if (!r.Ok)
                        {
                            criar.Enabled = true;
                            criar.Text = "Criar";
                            erro.Text = r.Texto ?? "Não deu certo. Tente de novo em instantes.";
                            erro.Visibility = ViewStates.Visible;
                            return;
                        }
                        d.Dismiss();
                        if (r.Id != null) Sessao.AbrirSala(r.Id);
                        if (r.Convite != null && site != null) MostrarConvite(a, "Grupo criado", r.Nome ?? nome, ContaLeve.LinkDoConvite(site, r.Convite));
                        else Aviso(a, $"Grupo {r.Nome ?? nome} criado. Toque em Convidar para pegar o link.", longo: true);
                    }
                    catch (Exception e) { Logcat.Warn("Streemu", "criar grupo (tela): " + e.GetType().Name); }
                });
            });
        };
        campo.RequestFocus();
    }

    // ------------------------------------------------------------------ Convidar: link para compartilhar ou copiar

    public static void Convidar(MainActivity a, GrupoDaConta g)
    {
        if (_convidando) return;
        _convidando = true;
        Aviso(a, "Pegando o convite...");
        _ = Task.Run(async () =>
        {
            ResultadoGrupo r;
            string? site = null;
            try
            {
                r = await Sessao.Conta.InfoDoGrupoAsync(g.Id);
                if (r.Ok && r.Convite != null) site = await Sessao.Conta.SiteAsync();
            }
            catch (Exception e) { Logcat.Warn("Streemu", "convidar: " + e.GetType().Name); r = new(false, "erro", "Não deu certo. Tente de novo em instantes."); }
            a.RunOnUiThread(() =>
            {
                _convidando = false;
                try
                {
                    Sessao.Avisar();   // o grupo pode ter sumido da conta (nao_membro): a lista já foi refeita
                    if (!r.Ok) Aviso(a, r.Texto ?? "Não deu certo. Tente de novo em instantes.", longo: true);
                    else if (r.Convite == null || site == null) Aviso(a, "Não consegui pegar o convite deste grupo. Tente de novo em instantes.", longo: true);
                    else MostrarConvite(a, "Convidar para o grupo", string.IsNullOrEmpty(r.Nome) ? g.Nome : r.Nome, ContaLeve.LinkDoConvite(site, r.Convite));
                }
                catch (Exception e) { Logcat.Warn("Streemu", "convidar (tela): " + e.GetType().Name); }
            });
        });
    }

    static void MostrarConvite(MainActivity a, string titulo, string nome, string link)
    {
        if (_dialogo != null) return;
        var d = Caixa(a)
            .SetTitle(titulo)!
            .SetMessage($"Quem abrir este link entra no grupo {nome}. Mande só para quem você conhece.\n\n{link}")!
            .SetPositiveButton("Compartilhar", (_, _) => Compartilhar(a, nome, link))!
            .SetNeutralButton("Copiar", (_, _) => Copiar(a, link))!
            .SetNegativeButton("Fechar", (_, _) => { })!
            .Create()!;
        Mostrar(d);
    }

    static void Compartilhar(MainActivity a, string nome, string link)
    {
        try
        {
            var enviar = new Intent(Intent.ActionSend);
            enviar.SetType("text/plain");
            enviar.PutExtra(Intent.ExtraText, $"Entra no meu grupo \"{nome}\" no Streemu: {link}");
            a.StartActivity(Intent.CreateChooser(enviar, "Convidar para o grupo"));
        }
        catch (Exception e) when (e is ActivityNotFoundException or Android.OS.RemoteException) { Aviso(a, "Não encontrei um app para compartilhar. Use Copiar.", longo: true); }
    }

    static void Copiar(MainActivity a, string link)
    {
        try
        {
            if (a.GetSystemService(Context.ClipboardService) is ClipboardManager cm)
            {
                cm.PrimaryClip = ClipData.NewPlainText("Convite do Streemu", link);
                Aviso(a, "Link copiado.");
                return;
            }
        }
        catch (Exception e) { Logcat.Warn("Streemu", "copiar: " + e.GetType().Name); }
        Aviso(a, "Não consegui copiar o link.", longo: true);
    }

    // ------------------------------------------------------------------ Sair do grupo

    public static void ConfirmarSaidaDoGrupo(MainActivity a, GrupoDaConta g)
    {
        if (_dialogo != null) return;
        var d = Caixa(a)
            .SetTitle($"Sair do grupo {g.Nome}?")!
            .SetMessage("Você deixa de ver as pessoas do grupo e elas deixam de ver você. Para voltar, vai precisar de um convite.")!
            .SetPositiveButton("Sair", (_, _) => Sair(a, g))!
            .SetNegativeButton("Cancelar", (_, _) => { })!
            .Create()!;
        Mostrar(d);
    }

    static void Sair(MainActivity a, GrupoDaConta g)
    {
        if (_saindo) return;
        _saindo = true;
        _ = Task.Run(async () =>
        {
            ResultadoGrupo r;
            try { r = await Sessao.Conta.SairDoGrupoAsync(g.Id); }
            catch (Exception e) { Logcat.Warn("Streemu", "sair do grupo: " + e.GetType().Name); r = new(false, "erro", "Não deu certo. Tente de novo em instantes."); }
            a.RunOnUiThread(() =>
            {
                _saindo = false;
                try
                {
                    // a lista já foi renovada: se o grupo aberto saiu dela, a sala passa para o primeiro que sobrou (ou fecha)
                    Sessao.AbrirSala(Sessao.GrupoId);
                    Sessao.Avisar();
                    Aviso(a, r.Ok ? $"Você saiu do grupo {g.Nome}." : r.Texto ?? "Não deu certo. Tente de novo em instantes.", longo: !r.Ok);
                }
                catch (Exception e) { Logcat.Warn("Streemu", "sair do grupo (tela): " + e.GetType().Name); }
            });
        });
    }

    // ------------------------------------------------------------------ peças de diálogo

    static Android.App.AlertDialog.Builder Caixa(Context c) => new Android.App.AlertDialog.Builder(c, Android.Resource.Style.ThemeDeviceDefaultDialogAlert)!;

    /// <summary>Mostra e guarda o diálogo: só um por vez, e a tela o fecha ao ser destruída.</summary>
    static void Mostrar(Android.App.AlertDialog d)
    {
        _dialogo = d;
        d.DismissEvent += (_, _) => { if (ReferenceEquals(_dialogo, d)) _dialogo = null; };
        d.Show();
    }

    /// <summary>Campo de uma linha com uma linha de erro escondida embaixo, para o erro aparecer sem fechar o diálogo.</summary>
    static (EditText Campo, TextView Erro, View Moldura) CampoComErro(Context c, string dica, int maximo)
    {
        var campo = new EditText(c) { Hint = dica };
        campo.SetFilters(new Android.Text.IInputFilter[] { new Android.Text.InputFilterLengthFilter(maximo) });
        campo.SetSingleLine(true);
        campo.InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextFlagNoSuggestions;
        var erro = Tema.Texto(c, "", 14, Tema.Erro);
        erro.Visibility = ViewStates.Gone;
        erro.SetPadding(0, Tema.Dp(c, 6), 0, 0);
        var coluna = Tema.Coluna(c);
        coluna.SetPadding(Tema.Dp(c, 22), Tema.Dp(c, 8), Tema.Dp(c, 22), 0);
        coluna.AddView(campo, Tema.Lp(Tema.Cheio, Tema.Ajusta));
        coluna.AddView(erro, Tema.Lp(Tema.Cheio, Tema.Ajusta));
        return (campo, erro, coluna);
    }

    static void Aviso(Context c, string texto, bool longo = false) =>
        Toast.MakeText(c, texto, longo ? ToastLength.Long : ToastLength.Short)!.Show();
}
