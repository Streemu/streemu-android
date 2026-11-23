using System.Text.Json;
using System.Text.Json.Serialization;

namespace Streemu.Arquivos;

public enum Sentido { Recebido, Enviado }

public enum Resultado { Conferido, Recusado, Expirou, Cancelado, Falhou, Parou }

/// <summary>
/// Uma linha do histórico (spec 020). <paramref name="Caminho"/>: no recebido, onde o arquivo ficou (Abrir, Mostrar na pasta);
/// no enviado, de onde saiu (o "Pedir de novo" confere se ainda é o mesmo arquivo, D6). Fica só neste aparelho.
/// </summary>
public sealed record ItemHistorico(
    Sentido Sentido, string Pedido, string AmigoId, string Apelido, string Nome, long Tamanho, string Identidade,
    DateTime Quando, Resultado Resultado, string? Caminho);

/// <summary>
/// Histórico de arquivos recebidos e enviados e a pasta de recebidos, num arquivo próprio (<c>arquivos.json</c>) e não no
/// <c>estado.json</c>: o histórico cresce e muda a cada envio, e o <c>AppState</c> é regravado inteiro por outras partes do app.
/// Guarda os últimos <see cref="MaxItens"/> ou <see cref="MaxDias"/> dias, o que vier primeiro (D8).
/// Ex.: <c>var h = new Historico(Path.Combine(filesDir, "arquivos.json"), () => DateTime.Now, Log.Info, () => padrao); h.Registrar(item);</c>
/// </summary>
public sealed class Historico
{
    public const int MaxItens = 200, MaxDias = 90;

    sealed class Gravado
    {
        public int V { get; set; } = 1;
        public string? Pasta { get; set; }
        public List<ItemHistorico> Itens { get; set; } = new();
    }

    static readonly JsonSerializerOptions Formato = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    readonly string? _arquivo;
    readonly Func<DateTime> _agora;
    readonly Action<string> _log;
    readonly Func<string> _pastaPadrao;
    readonly object _trava = new();
    Gravado _dados;

    /// <param name="arquivo">Onde gravar; nulo = só na memória (testes).</param>
    /// <param name="pastaPadrao">A pasta de recebidos quando a pessoa não escolheu uma. No Android quem sabe é o sistema (Downloads/Streemu, ou a pasta do app nas versões antigas), então o app diz.</param>
    public Historico(string? arquivo, Func<DateTime> agora, Action<string> log, Func<string> pastaPadrao)
    {
        _arquivo = arquivo; _agora = agora; _log = log; _pastaPadrao = pastaPadrao;
        _dados = Carregar();
        Cortar();
    }

    /// <summary>Do mais novo para o mais velho.</summary>
    public IReadOnlyList<ItemHistorico> Itens { get { lock (_trava) return _dados.Itens.ToList(); } }

    /// <summary>A pasta de recebidos escolhida; sem escolha, a padrão que o app informou.</summary>
    public string PastaRecebidos
    {
        get { lock (_trava) return _dados.Pasta is { Length: > 0 } p ? p : _pastaPadrao(); }
        set { lock (_trava) _dados.Pasta = value; Salvar(); }
    }

    /// <summary>Acrescenta no topo, ou troca a linha do mesmo pedido e arquivo (um envio que parou e depois terminou vira uma linha só).</summary>
    public void Registrar(ItemHistorico item)
    {
        lock (_trava)
        {
            _dados.Itens.RemoveAll(i => i.Sentido == item.Sentido && i.Pedido == item.Pedido && i.Identidade == item.Identidade);
            _dados.Itens.Insert(0, item);
        }
        Cortar();
        Salvar();
    }

    /// <summary>"Limpar histórico" (D8): apaga as linhas, mantém a pasta escolhida.</summary>
    public void Limpar()
    {
        lock (_trava) _dados.Itens.Clear();
        Salvar();
    }

    void Cortar()
    {
        DateTime limite = _agora().AddDays(-MaxDias);
        lock (_trava)
        {
            _dados.Itens.RemoveAll(i => i.Quando < limite);
            if (_dados.Itens.Count > MaxItens) _dados.Itens.RemoveRange(MaxItens, _dados.Itens.Count - MaxItens);
        }
    }

    Gravado Carregar()
    {
        if (_arquivo == null || !File.Exists(_arquivo)) return new Gravado();
        try { return JsonSerializer.Deserialize<Gravado>(File.ReadAllText(_arquivo), Formato) ?? new Gravado(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            _log($"[arquivo] histórico ilegível, começo vazio: {e.Message}");
            return new Gravado();
        }
    }

    /// <summary>Grava num .tmp e troca: o arquivo nunca fica pela metade se o app fechar no meio (o mesmo cuidado do AppState.Save).</summary>
    void Salvar()
    {
        if (_arquivo == null) return;
        try
        {
            string texto;
            lock (_trava) texto = JsonSerializer.Serialize(_dados, Formato);
            Directory.CreateDirectory(Path.GetDirectoryName(_arquivo)!);
            File.WriteAllText(_arquivo + ".tmp", texto);
            File.Move(_arquivo + ".tmp", _arquivo, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log($"[arquivo] não consegui gravar o histórico: {e.Message}"); }
    }
}
