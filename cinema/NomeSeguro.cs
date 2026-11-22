namespace Streemu.Arquivos;

/// <summary>
/// Monta o caminho final de um arquivo recebido de outro PC (spec 020, critério 18). O nome e a extensão vêm do outro lado e não
/// são confiáveis: até 2026-11-17 o download do Cinema limpava o nome mas colava a extensão crua, e uma extensão como
/// <c>\..\..\x.exe</c> gravava fora da pasta. Aqui os dois são limpos juntos, e o resultado é conferido contra a pasta.
/// </summary>
public static class NomeSeguro
{
    const int MaxNome = 180, MaxExtensao = 16;

    static readonly HashSet<string> Reservados = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Caminho livre dentro de <paramref name="pasta"/> para o arquivo, com " (2)", " (3)"... se o nome já existir.
    /// Ex.: <c>NomeSeguro.Destino(@"C:\Down", "mumu", ".png", "arquivo")</c> → <c>C:\Down\mumu.png</c>.
    /// </summary>
    public static string Destino(string pasta, string? nome, string? extensao, string nomePadrao)
    {
        string ext = Extensao(extensao), raiz = Raiz(nome, nomePadrao);
        string final = Path.Combine(pasta, raiz + ext);
        for (int k = 2; File.Exists(final); k++) final = Path.Combine(pasta, $"{raiz} ({k}){ext}");
        ConferirDentro(pasta, final);
        return final;
    }

    /// <summary>A extensão só com caracteres válidos, começando por ponto e curta; senão, nenhuma.</summary>
    static string Extensao(string? bruta)
    {
        string limpa = SemInvalidos(bruta ?? "").TrimEnd('.', ' ');
        if (!limpa.StartsWith('.') || limpa.Length < 2 || limpa.Length > MaxExtensao || limpa.LastIndexOf('.') != 0) return "";
        return limpa;
    }

    /// <summary>O nome sem caracteres inválidos, sem ponto ou espaço no fim (o Windows os corta), curto e sem nome reservado.</summary>
    static string Raiz(string? bruto, string nomePadrao)
    {
        string limpo = SemInvalidos(bruto ?? "").Trim().TrimEnd('.', ' ');
        if (limpo.Length > MaxNome) limpo = limpo[..MaxNome].TrimEnd('.', ' ');
        if (limpo.Length == 0) return nomePadrao;
        string antesDoPonto = limpo.Split('.')[0].TrimEnd(' ');
        return Reservados.Contains(antesDoPonto) ? "_" + limpo : limpo;
    }

    // sem \ / : nenhum pedaço vira pasta; ".." sozinho vira vazio pelo TrimEnd de quem chama.
    // Os invisíveis saem antes: Path.GetInvalidFileNameChars() não inclui U+202E (inverte a direção do texto), então "foto\u202Egnp.exe"
    // aparecia na tela e no Explorer como "fotoexe.png" sendo um .exe.
    static string SemInvalidos(string texto) => string.Concat(SemInvisiveis(texto).Split(Path.GetInvalidFileNameChars()));

    /// <summary>
    /// Tira os caracteres de formatação (categoria Cf: U+202A-202E, U+2066-2069, U+200B-200F, U+FEFF, tags) e de controle, que mudam a
    /// direção ou escondem pedaços do nome. Efeito colateral aceito: some também o ZWJ que junta emojis compostos (a família vira
    /// três emojis soltos); um nome vindo de outro PC vale menos que a certeza do que está escrito.
    /// </summary>
    static string SemInvisiveis(string texto)
    {
        var sb = new System.Text.StringBuilder(texto.Length);
        foreach (var r in texto.EnumerateRunes()) // por Rune: os caracteres Cf fora do plano básico (tags U+E00xx) são pares de surrogates
        {
            var cat = System.Text.Rune.GetUnicodeCategory(r);
            if (cat is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.Control) continue;
            sb.Append(r.ToString());
        }
        return sb.ToString();
    }

    /// <summary>
    /// O nome de um arquivo que veio de outro PC, pronto para aparecer na tela: sem os invisíveis de direção do texto, que fariam
    /// "foto\u202Egnp.exe" parecer "fotoexe.png". Não corta a extensão: quem mostra o nome mostra sempre ele inteiro.
    /// Ex.: <c>NomeSeguro.ParaMostrar("foto\u202Egnp.exe")</c> → <c>fotognp.exe</c>.
    /// </summary>
    public static string ParaMostrar(string? nome) => SemInvisiveis(nome ?? "");

    static void ConferirDentro(string pasta, string final)
    {
        string dentro = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pasta));
        string pai = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(final)) ?? "");
        if (!string.Equals(dentro, pai, StringComparison.OrdinalIgnoreCase))
            // sem o nome na mensagem: ela vai para o log, que sobe com a telemetria (spec 020, critério 15)
            throw new IOException($"O nome que o outro PC mandou gravaria fora de \"{pasta}\"; o arquivo esperado ficaria direto nessa pasta. Nada foi gravado.");
    }
}
