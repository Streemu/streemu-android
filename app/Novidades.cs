using System.Globalization;
using System.Text.RegularExpressions;

namespace Streemu.Proto;

/// <summary>Um item das novidades: "- **Título:** detalhe."</summary>
sealed record ItemDeNovidade(string Titulo, string Detalhe);

/// <summary>Um grupo de itens de uma versão ("Novo", "Melhorias", "Correções").</summary>
sealed record GrupoDeNovidades(string Tipo, IReadOnlyList<ItemDeNovidade> Itens);

/// <summary>Uma seção do NOVIDADES.md: "## 0.1.13 — 2026-11-21" ou "## Não lançado".</summary>
sealed record VersaoDeNovidades(string Versao, DateOnly? Data, string? Resumo, IReadOnlyList<GrupoDeNovidades> Grupos)
{
    public const string NaoLancado = "Não lançado";
    public bool NaoLancada => Versao == NaoLancado;
}

/// <summary>
/// Lê o <c>NOVIDADES.md</c> embutido no app, no mesmo formato do NOVIDADES.md do app Windows (plano 027 de lá). O texto vai dentro do app,
/// então funciona sem internet e é sempre o do código que está rodando. Nunca lança: arquivo ausente ou malformado vira lista vazia ou parcial.
/// Ex.: <c>var versoes = Novidades.Ler();</c>
/// </summary>
static class Novidades
{
    const string Arquivo = "NOVIDADES.md";
    static readonly Regex Comentario = new(@"<!--.*?-->", RegexOptions.Singleline);
    static readonly Regex ItemComTitulo = new(@"^- \*\*(.+?)\*\*\s*(.*)$");

    public static IReadOnlyList<VersaoDeNovidades> Ler()
    {
        try
        {
            using var arquivo = typeof(Novidades).Assembly.GetManifestResourceStream(Arquivo) ?? throw new FileNotFoundException(Arquivo);
            using var leitor = new StreamReader(arquivo);
            return Interpretar(leitor.ReadToEnd());
        }
        catch (Exception e)
        {
            Streemu.Util.Log.Info("[novidades] não deu para ler o arquivo: " + e.GetType().Name);
            return Array.Empty<VersaoDeNovidades>();
        }
    }

    /// <summary>O texto do arquivo vira as versões, na ordem em que aparecem. Linha que não é de nenhum formato conhecido é ignorada.</summary>
    public static IReadOnlyList<VersaoDeNovidades> Interpretar(string texto)
    {
        var leitura = new Leitura();
        foreach (var bruta in Comentario.Replace(texto, "").Split('\n')) leitura.Linha(bruta.Trim());
        return leitura.Fechar();
    }

    /// <summary>"0.1.13 — 2026-11-21" vira a versão e a data; sem data (ou "Não lançado"), só a versão.</summary>
    static (string Versao, DateOnly? Data) Cabecalho(string titulo)
    {
        var partes = titulo.Split(new[] { " — ", " – ", " - " }, 2, StringSplitOptions.TrimEntries);
        DateOnly? data = partes.Length == 2 && DateOnly.TryParseExact(partes[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        return (partes[0], data);
    }

    static ItemDeNovidade Item(string linha)
    {
        string corpo = linha[2..].Trim();
        var m = ItemComTitulo.Match(linha);
        if (!m.Success) return new(corpo, "");
        return new(m.Groups[1].Value.TrimEnd(':').Trim(), m.Groups[2].Value.TrimStart(':').Trim());
    }

    /// <summary>O estado da leitura linha a linha: a versão e o grupo abertos agora.</summary>
    sealed class Leitura
    {
        readonly List<VersaoDeNovidades> _versoes = new();
        string? _versao;
        DateOnly? _data;
        string? _resumo;
        List<GrupoDeNovidades> _grupos = new();
        string? _tipo;
        List<ItemDeNovidade> _itens = new();

        public void Linha(string linha)
        {
            if (linha.StartsWith("### ")) { FecharGrupo(); _tipo = linha[4..].Trim(); return; }
            if (linha.StartsWith("## ")) { FecharVersao(); (_versao, _data) = Cabecalho(linha[3..].Trim()); return; }
            if (_versao == null || linha.Length == 0 || linha.StartsWith("#")) return;
            if (linha.StartsWith("- ")) { _itens.Add(Item(linha)); return; }
            if (_tipo == null && _resumo == null) _resumo = linha;   // a frase solta logo abaixo da versão
        }

        public IReadOnlyList<VersaoDeNovidades> Fechar()
        {
            FecharVersao();
            return _versoes;
        }

        void FecharGrupo()
        {
            if (_itens.Count > 0) _grupos.Add(new GrupoDeNovidades(_tipo ?? "Novidades", _itens));
            _itens = new();
            _tipo = null;
        }

        void FecharVersao()
        {
            FecharGrupo();
            if (_versao != null) _versoes.Add(new VersaoDeNovidades(_versao, _data, _resumo, _grupos));
            _versao = null;
            _data = null;
            _resumo = null;
            _grupos = new();
        }
    }
}
