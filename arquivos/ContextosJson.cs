using System.Text.Json.Serialization;

namespace Streemu.Arquivos;

/// <summary>
/// JSON gerado em tempo de compilação. O build de release do app Android (trim completo) desliga o JSON por reflexão e o
/// <c>JsonSerializer.Serialize</c> comum lança <c>JsonSerializerIsReflectionDisabled</c> logo na abertura (0.1.19 e 0.1.20 travavam por isso).
/// O formato gravado em disco é o mesmo de antes: enums como texto.
/// </summary>
static class ContextosJson
{
    public static CursoJson Curso { get; } = CursoJson.Default;
    public static HistoricoJson Historico { get; } = HistoricoJson.Default;
}

[JsonSourceGenerationOptions(WriteIndented = false, Converters = new[] { typeof(JsonStringEnumConverter) })]
[JsonSerializable(typeof(List<PedidoDeCurso>))]
partial class CursoJson : JsonSerializerContext { }

[JsonSourceGenerationOptions(WriteIndented = true, Converters = new[] { typeof(JsonStringEnumConverter) })]
[JsonSerializable(typeof(Historico.Gravado))]
partial class HistoricoJson : JsonSerializerContext { }
