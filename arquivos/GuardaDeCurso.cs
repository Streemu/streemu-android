using System.Text.Json;
using System.Text.Json.Serialization;

namespace Streemu.Arquivos;

/// <summary>Um pedido que estava andando (ligação aberta ou transferindo). Só neste aparelho; tem os nomes dos arquivos como o histórico.</summary>
/// <param name="Referencias">Quem manda: de onde sai cada arquivo (identidade → referência). Sem isto, o "pedir de novo" de um envio que parou não acharia o arquivo.</param>
public sealed record PedidoDeCurso(Sentido Sentido, string Pedido, string AmigoId, string Apelido, List<ArquivoOferecido> Arquivos, DateTime Desde, Dictionary<string, string>? Referencias = null);

/// <summary>
/// Lembra, em disco, dos pedidos que estão andando, para o app saber o que aconteceu se o sistema o matar no meio (economia de bateria, pouca
/// memória, fabricante agressivo): ao reabrir, cada um vira "parou" no histórico, em vez de sumir sem rastro ou ficar para sempre "em andamento"
/// (spec 030, seção 3). O que já veio fica guardado no <c>.streemuparcial</c>, e o "pedir de novo" retoma de onde parou.
/// Ex.: <c>var guarda = new GuardaDeCurso(Path.Combine(filesDir, "arquivos-curso.json"), Log.Info);</c>
/// </summary>
public sealed class GuardaDeCurso
{
    readonly string? _arquivo;
    readonly Action<string> _log;
    readonly object _trava = new();
    readonly Dictionary<string, PedidoDeCurso> _pedidos = new();

    /// <param name="arquivo">Onde gravar; nulo = só na memória (testes).</param>
    public GuardaDeCurso(string? arquivo, Action<string> log)
    {
        _arquivo = arquivo; _log = log;
        foreach (var p in Carregar()) _pedidos[p.Pedido] = p;
    }

    /// <summary>Os pedidos que estavam andando quando o app foi fechado (lidos do disco na criação), do mais antigo para o mais novo.</summary>
    public IReadOnlyList<PedidoDeCurso> Interrompidos { get { lock (_trava) return _pedidos.Values.OrderBy(p => p.Desde).ToList(); } }

    public void Gravar(PedidoEmCurso p)
    {
        lock (_trava) _pedidos[p.Pedido] = new PedidoDeCurso(p.Sentido, p.Pedido, p.AmigoId, p.Apelido, p.Oferta.Arquivos.ToList(), p.Criado,
            p.Sentido == Sentido.Enviado ? new Dictionary<string, string>(p.Caminhos) : null);
        Salvar();
    }

    public void Remover(string pedido)
    {
        lock (_trava) if (!_pedidos.Remove(pedido)) return;
        Salvar();
    }

    public void Limpar()
    {
        lock (_trava) _pedidos.Clear();
        Salvar();
    }

    List<PedidoDeCurso> Carregar()
    {
        if (_arquivo == null || !File.Exists(_arquivo)) return new();
        try { return JsonSerializer.Deserialize(File.ReadAllText(_arquivo), ContextosJson.Curso.ListPedidoDeCurso) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            _log($"[arquivo] a lista de pedidos em curso estava ilegível, começo vazia: {e.Message}");
            return new();
        }
    }

    /// <summary>Grava num .tmp e troca: o arquivo nunca fica pela metade se o app for morto no meio (o mesmo cuidado do histórico).</summary>
    void Salvar()
    {
        if (_arquivo == null) return;
        try
        {
            string texto;
            lock (_trava) texto = JsonSerializer.Serialize(_pedidos.Values.ToList(), ContextosJson.Curso.ListPedidoDeCurso);
            Directory.CreateDirectory(Path.GetDirectoryName(_arquivo)!);
            File.WriteAllText(_arquivo + ".tmp", texto);
            File.Move(_arquivo + ".tmp", _arquivo, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log($"[arquivo] não consegui gravar os pedidos em curso: {e.Message}"); }
    }
}
