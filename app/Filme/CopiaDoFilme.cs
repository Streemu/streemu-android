using Android.Content;
using Streemu.Filme;
using Logcat = Android.Util.Log;

namespace Streemu.Proto.Filme;

/// <summary>
/// O filme pela transmissão em cópia direta no celular anfitrião (spec 028, F5): sonda o arquivo, monta o envio e o liga ao <see cref="AnfitriaoCinema"/>. Guarda a faixa de áudio que o envio manda
/// (<see cref="Faixas"/>) e a troca (<see cref="TrocarAudio"/>): o menu de faixas e o player do anfitrião têm que andar juntos com ela.
/// </summary>
sealed class CopiaDoFilme
{
    /// <summary>Upload que o celular supõe ter (Mbps) para o ritmo do envio adiantado; a rede móvel de quem transmite não é medida.</summary>
    const double UploadSupostoMbps = 30;

    readonly Context _contexto;
    readonly Android.Net.Uri _arquivo;
    readonly AnfitriaoCinema _anfitriao;
    volatile FaixasDoFilme _faixas;

    CopiaDoFilme(Context contexto, Android.Net.Uri arquivo, AnfitriaoCinema anfitriao, FaixasDoFilme faixas)
    {
        _contexto = contexto; _arquivo = arquivo; _anfitriao = anfitriao; _faixas = faixas;
    }

    /// <summary>As faixas do arquivo e a de áudio que o envio manda agora.</summary>
    public FaixasDoFilme Faixas => _faixas;

    /// <summary>
    /// A cópia ligada (o anúncio sai com <c>stream=true</c>), ou nulo se o arquivo pede recompressão (AV1, Opus, DTS...): quem entra baixa o arquivo, como na F4.
    /// Chamar antes do <see cref="AnfitriaoCinema.Iniciar"/>.
    /// </summary>
    public static CopiaDoFilme? Ligar(Context contexto, AnfitriaoCinema anfitriao, Android.Net.Uri arquivo, string nome)
    {
        var faixas = SondaFilme.Sondar(contexto, arquivo, nome);
        if (faixas == null) return null;
        try
        {
            Srt.srt_startup();
            var envio = new EnvioFilmeCopia(faixas.Info, () => new FonteExtrator(contexto, arquivo, faixas)) { UploadMbps = UploadSupostoMbps };
            anfitriao.LigarStream(envio, faixas.Audio?.NomeCodec, (saida, link) => new SaidaSrtFilme(envio, saida, link));
            Logcat.Info("Streemu", $"FILME transmissão em cópia direta ligada: {faixas.Info.Resumo}, enviando a faixa {faixas.Escolhida} de {faixas.Audios.Count}.");
            foreach (var a in faixas.Audios) Logcat.Info("Streemu", $"FILME faixa de áudio do arquivo (MediaExtractor) #{a.Indice}: {a.Mime} {a.Canais} canais, idioma \"{a.Idioma}\", número {a.TrackId ?? "?"}, {(a.PodeCopiar ? "vai em cópia" : "não vai em cópia")}{(a.Indice == faixas.Escolhida ? " <- enviada" : "")}.");
            return new CopiaDoFilme(contexto, arquivo, anfitriao, faixas);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            Logcat.Warn("Streemu", "FILME transmissão não ligou: " + e.Message);
            return null;
        }
    }

    /// <summary>Quem é a faixa de áudio que o envio manda agora (para o player do anfitrião tocar a mesma).</summary>
    public IdentidadeDeAudio? IdentidadeDaEscolhida => _faixas.Escolhida >= 0 ? _faixas.Identidades()[_faixas.Escolhida] : null;

    /// <summary>
    /// Passa o envio para a faixa de áudio <paramref name="alvo"/> (a que o player tocou, achada entre as do MediaExtractor pela identidade). Devolve o motivo (para a tela) se essa faixa não vai em cópia direta; nesse caso nada muda.
    /// Quem assiste e não toca o codec da faixa nova sai da transmissão com o aviso de baixar o arquivo; os outros ganham uma época nova. Chamar na thread da tela (a da Sessão).
    /// </summary>
    public string? TrocarAudio(IdentidadeDeAudio alvo)
    {
        var atual = _faixas;
        int indice = alvo.ProcurarEm(atual.Identidades());
        if (indice < 0) { Logcat.Warn("Streemu", $"FILME a faixa de áudio {alvo} do player não existe entre as do envio; o envio não mudou."); return "Não achei essa faixa de áudio para a transmissão; quem assiste continua com a anterior."; }
        var nova = atual.Audios[indice];
        if (!nova.PodeCopiar) return $"Essa faixa ({nova.NomeCodec}) não vai pela transmissão. Escolha outra, ou feche a transmissão do filme.";
        var faixas = _faixas = atual.ComAudio(indice);
        _anfitriao.TrocarAudio(faixas.Info, () => new FonteExtrator(_contexto, _arquivo, faixas), nova.NomeCodec);
        Logcat.Info("Streemu", $"FILME envio passa para a faixa de áudio {indice + 1}: {nova.Idioma} ({nova.NomeCodec}).");
        return null;
    }
}
