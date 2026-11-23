namespace Streemu.Arquivos;

/// <summary>
/// Tipos de arquivo que executam código ou mudam o aparelho quando abertos. O aparelho de um amigo pode estar infectado e mandar um deles
/// disfarçado, então o app não abre esses direto: pergunta antes. No Android, o perigo principal é o .apk (e .apks, .xapk, .apkm), que
/// o instalador do sistema aceitaria; a lista do Windows segue valendo porque o arquivo pode ser aberto depois num PC. Só a extensão decide; o nome vem do
/// outro PC e não é prova de nada.
/// Por que a marca da internet (MarcaDaInternet) não basta: o SmartScreen avisa de .exe, mas scripts (.vbs, .js, .wsf, .hta...)
/// abrem direto no Windows Script Host, sem aviso forte, e contêineres (.iso, .img, .vhd, .vhdx) podem montar um disco cujos
/// arquivos de dentro não herdam a marca. Atalhos (.lnk, .url, .scf) e as macros do Office (.docm, .xlsm, .pptm) também executam.
/// Ex.: <c>TipoArriscado.Eh(@"C:\Down\foto.png.scr")</c> → <c>true</c>.
/// </summary>
public static class TipoArriscado
{
    static readonly HashSet<string> Extensoes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta",
        ".msi", ".msp", ".msix", ".appx", ".appxbundle", ".lnk", ".url", ".scf", ".reg", ".cpl", ".jar", ".iso", ".img", ".vhd",
        ".vhdx", ".apk", ".apks", ".xapk", ".apkm", ".sh", ".chm", ".application", ".gadget", ".inf", ".library-ms", ".settingcontent-ms", ".xll", ".docm", ".xlsm", ".pptm",
    };

    /// <summary>
    /// A extensão real é a última, depois de limpar o que o Windows ignora no fim do nome (pontos e espaços: "a.exe." roda como
    /// "a.exe") e os invisíveis; assim "foto.png.exe" e "a.exe ." não passam por imagem.
    /// </summary>
    public static bool Eh(string caminho)
    {
        string nome = NomeSeguro.ParaMostrar(Path.GetFileName(caminho)).TrimEnd('.', ' ');
        string ext = Path.GetExtension(nome);
        return ext.Length > 1 && Extensoes.Contains(ext);
    }
}
