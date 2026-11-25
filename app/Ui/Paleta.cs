using Android.Content;
using Android.Graphics;

namespace Streemu.Proto.Ui;

/// <summary>Marca a tela que fica escura nos dois temas (player e filme: o fundo é o vídeo). <see cref="Paleta.De"/> devolve a escura para ela.</summary>
interface IFixoEscuro { }

/// <summary>
/// Os valores de cor de um tema. Escura: a identidade da spec 014 do app Windows. Clara: a <c>ThemePalette.Light</c> do app Windows (conferida em 2026-11-25).
/// Os nomes daqui são os do Android; o do Windows vem no comentário quando é diferente.
/// Uso: <c>var fundo = Paleta.Clara.Fundo;</c> ou, para uma tela, <c>Paleta.De(contexto).CorTexto</c>.
/// </summary>
sealed record Paleta
{
    public required bool Claro { get; init; }
    public required Color Fundo { get; init; }            // Surface
    public required Color Cartao { get; init; }           // SurfaceRaised
    public required Color Campo { get; init; }            // SurfaceInput
    public required Color Barra { get; init; }            // TitleBar
    public required Color Borda { get; init; }            // CardBorder
    public required Color Fio { get; init; }              // CardBorder a 45%: o fio sob a barra de cima, mais discreto que uma borda
    public required Color CorTexto { get; init; }         // Text
    public required Color TextoApagado { get; init; }     // TextMuted
    public required Color Destaque { get; init; }         // Accent
    public required Color DestaqueSuave { get; init; }    // AccentSoft
    public required Color Selecionado { get; init; }      // no claro: Selection (o CardSelected quase some sobre o cartão)
    public required Color Neutro { get; init; }
    public required Color TextoNeutro { get; init; }      // NeutralText
    public required Color Ok { get; init; }
    public required Color Aviso { get; init; }            // Warn
    public required Color Erro { get; init; }             // Error (o selo AO VIVO usa o mesmo vermelho)
    public required Color Brilho { get; init; }           // o ripple do toque: véu branco no escuro, véu escuro no claro
    public required Color TrilhoDesligado { get; init; }  // a Chave desligada
    public required Color PuxadorLigado { get; init; }    // SwitchThumbOn
    public required Color PuxadorDesligado { get; init; } // SwitchThumbOff
    public required BotaoDeTemaCores Botao { get; init; }

    public Color AoVivo => Erro;

    /// <summary>A paleta de uma tela: a escura fixa para as <see cref="IFixoEscuro"/>, a ativa para as outras. Ex.: <c>Paleta.De(this).Fundo</c></summary>
    public static Paleta De(Context c) => c is IFixoEscuro ? Escura : Atual;

    static Color Hex(string html) => Color.ParseColor(html);

    public static readonly Paleta Escura = new()
    {
        Claro = false,
        Fundo = Hex("#2A2640"), Cartao = Hex("#1E1B2E"), Campo = Hex("#25213A"), Barra = Hex("#171523"), Borda = Hex("#4B4463"),
        Fio = Color.Argb(115, 0x4B, 0x44, 0x63), CorTexto = Hex("#F4F0FA"), TextoApagado = Hex("#C5BADB"),
        Destaque = Hex("#7C62DC"), DestaqueSuave = Hex("#B9A8F4"), Selecionado = Hex("#3A3160"), Neutro = Hex("#49415F"), TextoNeutro = Hex("#F4F0FA"),
        Ok = Hex("#3DDC6A"), Aviso = Hex("#F0B232"), Erro = Hex("#E5484D"),
        Brilho = Color.Argb(60, 255, 255, 255), TrilhoDesligado = Hex("#49415F"), PuxadorLigado = Color.White, PuxadorDesligado = Color.White,
        Botao = new BotaoDeTemaCores
        {
            TrilhoA = Hex("#302A46"), TrilhoB = Hex("#211E30"), BordaDoTrilho = Hex("#51476F"), Ocioso = Hex("#A79ABD"),
            PuxadorA = Hex("#9278ED"), PuxadorB = Hex("#6E55C4"), Brilhinho = Hex("#B9A8F4"), Sombra = Color.Argb(70, 8, 6, 18),
            Lua = Hex("#F6F1FF"), Sol = Hex("#FFF9ED"), Aro = Color.Argb(107, 0xC9, 0xBC, 0xFF),
        },
    };

    public static readonly Paleta Clara = new()
    {
        Claro = true,
        Fundo = Hex("#F4F3F8"), Cartao = Hex("#E5E4ED"), Campo = Hex("#FFFFFF"), Barra = Hex("#E8E7EF"), Borda = Hex("#D3CFE0"),
        Fio = Color.Argb(115, 0xD3, 0xCF, 0xE0), CorTexto = Hex("#343144"), TextoApagado = Hex("#6B687A"),
        Destaque = Hex("#7469AD"), DestaqueSuave = Hex("#7568AE"), Selecionado = Hex("#D5D3E5"), Neutro = Hex("#DEDDE9"), TextoNeutro = Hex("#4A465B"),
        Ok = Hex("#16814B"), Aviso = Hex("#8B5B08"), Erro = Hex("#BD3544"),
        Brilho = Color.Argb(40, 52, 49, 68), TrilhoDesligado = Hex("#CFCCDC"), PuxadorLigado = Color.White, PuxadorDesligado = Hex("#8C879C"),
        Botao = new BotaoDeTemaCores
        {
            TrilhoA = Hex("#EEEAF5"), TrilhoB = Hex("#E3DFED"), BordaDoTrilho = Hex("#D2CBE0"), Ocioso = Hex("#A69EB8"),
            PuxadorA = Hex("#9278ED"), PuxadorB = Hex("#6E55C4"), Brilhinho = Hex("#796CA8"), Sombra = Color.Argb(41, 52, 43, 74),
            Lua = Hex("#F6F1FF"), Sol = Hex("#FFF9ED"), Aro = Color.Argb(107, 0xC9, 0xBC, 0xFF),
        },
    };

    /// <summary>A paleta que vale para as telas normais: a escolhida (<see cref="Tema.Aplicar"/>). O padrão, até alguém escolher, é a escura. Fica depois de
    /// <see cref="Escura"/> de propósito: os campos estáticos são inicializados na ordem do texto.</summary>
    public static Paleta Atual { get; set; } = Escura;
}

/// <summary>As cores do botão sol/lua (o pacote "Botão" do app Windows: <c>ThemePalette.Toggle*</c>).</summary>
sealed record BotaoDeTemaCores
{
    public required Color TrilhoA { get; init; }
    public required Color TrilhoB { get; init; }
    public required Color BordaDoTrilho { get; init; }
    public required Color Ocioso { get; init; }
    public required Color PuxadorA { get; init; }
    public required Color PuxadorB { get; init; }
    public required Color Brilhinho { get; init; }
    public required Color Sombra { get; init; }
    public required Color Lua { get; init; }
    public required Color Sol { get; init; }
    public required Color Aro { get; init; }
}
