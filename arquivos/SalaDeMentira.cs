using System.Text.Json.Nodes;
using Streemu.Net;

namespace Streemu.Arquivos.Teste;

// Apoio de teste: duas pontas do envio de arquivo no mesmo processo, sem sala nem internet. Fica no projeto portátil (e não em testes/) para o
// app Android de Debug também rodar o fluxo no próprio celular, com o ContentResolver e o Downloads de verdade (spec 030, T5).

/// <summary>A sala do grupo de mentira: entrega as mensagens entre os fluxos como a rede faria (uma cópia, em outra thread).</summary>
public sealed class SalaDeMentira
{
    readonly Dictionary<string, Ponto> _pontos = new();

    public Ponto Criar(string id, string nome) => _pontos[id] = new Ponto(this, id, nome);

    public sealed class Ponto : IPortaDaSala
    {
        readonly SalaDeMentira _sala;
        public Ponto(SalaDeMentira sala, string id, string nome) { _sala = sala; Id = id; MeuNome = nome; }
        public string Id { get; }
        public string MeuNome { get; }
        public FluxoDeArquivos? Fluxo { get; set; }
        /// <summary>Faz de conta que este app é de uma versão sem o envio de arquivo: nada do que chega é tratado.</summary>
        public bool VersaoAntiga { get; set; }
        public bool SaiuDaSala { get; set; }

        public Task EnviarAsync(string paraId, JsonObject mensagem)
        {
            if (!_sala._pontos.TryGetValue(paraId, out var destino) || destino.VersaoAntiga || destino.SaiuDaSala) return Task.CompletedTask;
            var copia = (JsonObject)JsonNode.Parse(mensagem.ToJsonString())!;
            _ = Task.Run(() => destino.Fluxo!.AoReceber(Id, MeuNome, copia));
            return Task.CompletedTask;
        }

        public bool AmigoNaSala(string id) => id != Id && PresenteNaSala(id);
        public bool PresenteNaSala(string id) => _sala._pontos.TryGetValue(id, out var p) && !p.SaiuDaSala;
    }
}

/// <summary>Ligação direta em modo local (127.0.0.1), como os testes do Cinema.</summary>
public sealed class FabricaLocal : IFabricaDeLigacao
{
    readonly string _meuNome;
    public FabricaLocal(string meuNome) => _meuNome = meuNome;

    public Task<PeerLink> CriarAsync(string sessao, string amigoId, string amigoNome, bool souEmissor) =>
        PeerLink.CreateAsync(sessao, amigoId, amigoNome, souEmissor, localMode: true, _meuNome, null, null, "arquivo");
}

