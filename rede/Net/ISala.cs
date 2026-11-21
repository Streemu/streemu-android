using System.Text.Json.Nodes;

namespace Streemu.Net;

/// <summary>
/// O que o resto do app precisa de uma sala do grupo (spec 019): uma conexão com conta (<see cref="GroupRoom"/>) ou várias juntas (<see cref="SalaUnida"/>).
/// Desde a spec 022 a sala antiga (com a senha do grupo) não existe mais. Os arquivos de <c>MainForm.*.cs</c> falam só com esta interface.
/// </summary>
public interface ISala : IDisposable
{
    string MyId { get; }
    bool Connected { get; }
    /// <summary>Última situação da sala, em português, para mostrar na tela.</summary>
    string Status { get; }
    /// <summary>Quem está online agora (com a folga que evita a lista piscar). NÃO inclui quem está offline: ver <see cref="Ausentes"/>.</summary>
    IReadOnlyList<Member> Members { get; }
    IEnumerable<Member> Friends { get; }
    /// <summary>Membros guardados do grupo que não estão conectados. Sempre vazia na sala antiga, que não guarda membros.</summary>
    IReadOnlyList<Ausente> Ausentes { get; }
    /// <summary>Atividades recentes do grupo, da mais nova para a mais antiga (no máximo 50; plano 019, etapa 9). Vazia na sala antiga, que não guarda nada.</summary>
    IReadOnlyList<Atividade> Atividades => Array.Empty<Atividade>();
    /// <summary>A lista de <see cref="Atividades"/> mudou (chegou a lista inicial ou uma atividade nova).</summary>
    event Action? AtividadesMudaram { add { } remove { } }
    /// <summary>Conta ao servidor uma atividade que só o app sabe: <c>assistindo</c> (com o <paramref name="alvo"/>, a conta de quem transmite) ou <c>filme</c>. Não faz nada na sala antiga.</summary>
    void EnviarAtividade(string tipo, string? alvo = null) { }
    bool IsPresent(string id);
    Member? Find(string id);
    Member? FindByStream(string streamId);
    void SetNick(string nick);
    void SetLive(LiveInfo? live);
    Task SendTo(string id, JsonObject msg);
    Task SendTelemetryNow(IEnumerable<string> ids);

    event Action? Changed;
    /// <summary>Mensagem de um amigo: (id, apelido, mensagem).</summary>
    event Action<string, string, JsonObject>? Message;
    /// <summary>A sala não achou o destinatário (ele fechou o app): (id, mensagem que não foi entregue).</summary>
    event Action<string, JsonObject?>? Undeliverable;
    /// <summary>Alguém marcou um problema numa live em que estou, ou o admin pediu: enviar a telemetria já (spec 009).</summary>
    event Action? TelemetryNow;
    /// <summary>A sala com conta acabou para esta pessoa e NÃO reconecta: "removido" (o dono tirou do grupo), "sessao" (a sessão foi encerrada) ou
    /// "saiu" (saiu do grupo). A sala antiga nunca dispara isto.</summary>
    event Action<string>? Encerrada;
}

/// <summary>Membro do grupo que não está conectado agora (só a sala com conta sabe quem são).</summary>
public sealed record Ausente(string Id, string Nick);
