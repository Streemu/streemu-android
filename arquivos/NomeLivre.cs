namespace Streemu.Arquivos;

/// <summary>
/// Escolhe o nome que não esbarra em outro arquivo da pasta de destino, no mesmo estilo do PC (spec 020, critério 7): <c>nome (2).ext</c>.
/// Vive aqui, e não no <see cref="NomeSeguro"/>, porque no Android quem diz se um nome está ocupado é o sistema (MediaStore ou a pasta
/// escolhida pela pessoa), não o <c>File.Exists</c>. O nome já vem limpo pelo <see cref="NomeSeguro"/>.
/// Ex.: <c>NomeLivre.De("mumu.png", n =&gt; n == "mumu.png")</c> → <c>mumu (2).png</c>.
/// </summary>
public static class NomeLivre
{
    const int MaxTentativas = 10_000;

    public static string De(string nome, Func<string, bool> ocupado)
    {
        if (!ocupado(nome)) return nome;
        string ext = Path.GetExtension(nome), basico = nome[..^ext.Length];
        for (int n = 2; n <= MaxTentativas; n++)
        {
            string candidato = $"{basico} ({n}){ext}";
            if (!ocupado(candidato)) return candidato;
        }
        throw new IOException($"Não achei nome livre para \"{nome}\" depois de {MaxTentativas} tentativas; esperado: uma pasta de destino com menos de {MaxTentativas} cópias do mesmo nome.");
    }
}
