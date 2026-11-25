using Android.App;
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
    static Dialog? _dialogo;

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
        // fechar sem tocar em "Entrar" (Agora não, toque fora, voltar) é recusar
        Registrar(CaixaDoApp.Confirmar(a, "Convite para um grupo",
            "Você recebeu um convite para entrar num grupo do Streemu.\n\nEntrando, as pessoas do grupo passam a ver quando você está online e ao vivo, e você vê as delas. Só aceite se você conhece quem mandou.",
            "Entrar", () => EntrarPeloConvite(a, codigo), rotuloCancelar: "Agora não", aoRecusar: () => Recusar(a)));
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
        Registrar(CaixaDeTexto.Mostrar(a, "Entrar com convite", "Cole o link que você recebeu ou digite o código do convite.", "", 400,
            texto => EntrarComTexto(a, texto), () => { }, rotuloSalvar: "Entrar", rotuloOcupado: "Entrando...", dica: "Cole o link ou o código", semSugestoes: true));
    }

    /// <summary>O "Entrar" da caixa: devolve null se entrou (a caixa fecha) ou a frase de erro (a caixa fica aberta com o que foi digitado).</summary>
    static Task<string?> EntrarComTexto(MainActivity a, string texto)
    {
        if (ContaLeve.NormalizarConvite(texto) is not { } codigo)
            return Task.FromResult<string?>("Esse convite não está completo. Confira o link ou o código.");
        var fim = new TaskCompletionSource<string?>();
        EntrarNoGrupo(a, codigo, r =>
        {
            if (!r.Ok) { fim.SetResult(r.Texto ?? "Não deu certo. Tente de novo em instantes."); return; }
            Limpar(a);
            fim.SetResult(null);
            AposEntrar(a, r);
        });
        return fim.Task;
    }

    // ------------------------------------------------------------------ Criar grupo

    public static void PedirCriacao(MainActivity a)
    {
        if (_dialogo != null) return;
        Registrar(CaixaDeTexto.Mostrar(a, "Criar grupo", "Depois de criar, você recebe o link para chamar o pessoal.", "", 40,
            nome => CriarGrupo(a, nome), () => { }, rotuloSalvar: "Criar", rotuloOcupado: "Criando...", dica: "Nome do grupo"));
    }

    /// <summary>O "Criar" da caixa: devolve null se criou (a caixa fecha e o link do convite aparece) ou a frase de erro (a caixa fica aberta).</summary>
    static async Task<string?> CriarGrupo(MainActivity a, string digitado)
    {
        var nome = digitado.Trim();
        if (nome.Length == 0) return "Dê um nome ao grupo.";
        if (_criando) return "Já estou criando um grupo. Espere um instante.";
        _criando = true;
        var apelido = Sessao.Apelido;
        ResultadoGrupo r;
        string? site = null;
        try
        {
            r = await Task.Run(() => Sessao.Conta.CriarGrupoAsync(nome, apelido));
            if (r.Ok) site = await Task.Run(() => Sessao.Conta.SiteAsync());
        }
        catch (Exception e) { Logcat.Warn("Streemu", "criar grupo: " + e.GetType().Name); r = new(false, "erro", "Não deu certo. Tente de novo em instantes."); }
        _criando = false;
        Sessao.Avisar();
        if (!r.Ok) return r.Texto ?? "Não deu certo. Tente de novo em instantes.";
        if (r.Id != null) Sessao.AbrirSala(r.Id);
        // a caixa de criar fecha assim que esta função devolve null; o link do convite abre logo depois, numa mensagem nova da tela
        a.Window!.DecorView.Post(() => { _dialogo = null; MostrarConviteDoGrupoCriado(a, r, nome, site); });
        return null;
    }

    static void MostrarConviteDoGrupoCriado(MainActivity a, ResultadoGrupo r, string nome, string? site)
    {
        if (r.Convite != null && site != null) MostrarConvite(a, "Grupo criado", r.Nome ?? nome, ContaLeve.LinkDoConvite(site, r.Convite));
        else Aviso(a, $"Grupo {r.Nome ?? nome} criado. Toque em Convidar para pegar o link.", longo: true);
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
        var botoes = new[]
        {
            new BotaoDaCaixa("Compartilhar", () => Compartilhar(a, nome, link), Principal: true),
            new BotaoDaCaixa("Copiar", () => Copiar(a, link)),
            new BotaoDaCaixa("Fechar"),
        };
        Registrar(CaixaDoApp.Mostrar(a, titulo, $"Quem abrir este link entra no grupo {nome}. Mande só para quem você conhece.\n\n{link}", botoes, selecionavel: true));
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
        Registrar(CaixaDoApp.Confirmar(a, $"Sair do grupo {g.Nome}?",
            "Você deixa de ver as pessoas do grupo e elas deixam de ver você. Para voltar, vai precisar de um convite.",
            "Sair", () => Sair(a, g), perigosa: true));
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

    /// <summary>Guarda o diálogo que acabou de abrir: só um por vez, e a tela o fecha ao ser destruída (<see cref="Fechar"/>).</summary>
    static void Registrar(Dialog d)
    {
        _dialogo = d;
        d.DismissEvent += (_, _) => { if (ReferenceEquals(_dialogo, d)) _dialogo = null; };
    }

    static void Aviso(Context c, string texto, bool longo = false) =>
        Toast.MakeText(c, texto, longo ? ToastLength.Long : ToastLength.Short)!.Show();
}
