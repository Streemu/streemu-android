using System.Text.Json.Nodes;

namespace Streemu.Util;

/// <summary>
/// Leitura de campos JSON que vêm de outro aparelho (um amigo, o PC que transmite). GetValue&lt;T&gt; lança exceção se o tipo não bater,
/// e numa thread de fundo ou na thread da tela isso derruba o app: aqui, tipo errado vira nulo.
/// </summary>
public static class JsonSeguro
{
    public static string? Texto(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static long? Inteiro(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var l) ? l : null;

    /// <summary>Uma hora em ms Unix que o DateTimeOffset aceita (de 0 até o ano 9999); fora disso, 0.</summary>
    public static long HoraUnixMs(long ms) => ms is > 0 and <= 253_402_300_799_999 ? ms : 0;
}
