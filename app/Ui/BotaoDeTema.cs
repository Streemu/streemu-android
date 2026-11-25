using Android.Content;
using Android.Graphics;
using Android.Views;
using Path = Android.Graphics.Path;

namespace Streemu.Proto.Ui;

/// <summary>
/// O botão sol/lua do tema, o mesmo desenho do app Windows (<c>ThemeToggleArt</c>, geometria de 104 × 52 escalada sem deformar): no escuro o puxador
/// fica à esquerda com a lua; no claro, à direita com o sol. Sem animação: a tela é recriada ao trocar de tema, então só o estado final aparece.
/// Use com a proporção 2:1. Uso: <c>new BotaoDeTema(c) { Claro = Tema.Atual.Claro }</c> em <c>Tema.Lp(Tema.Dp(c, 64), Tema.Dp(c, 32))</c>.
/// </summary>
sealed class BotaoDeTema : View
{
    const float LarguraBase = 104, AlturaBase = 52, RaioDoPuxador = 20, Meio = 26, XEscuro = 26, XClaro = 78;
    static Path? _lua;
    readonly Paint _p = new(PaintFlags.AntiAlias);
    bool _claro;

    /// <summary>Não é clicável de propósito: quem recebe o toque é a linha inteira (<c>LinhaDaAparencia</c>). Uma View clicável sem ouvinte engole o toque
    /// e a linha não vê: o botão só respondia quando o toque caía fora dele (relato de 2026-11-25: "inúmeros cliques").</summary>
    public BotaoDeTema(Context c) : base(c) { ImportantForAccessibility = ImportantForAccessibility.No; }

    public bool Claro
    {
        get => _claro;
        set { _claro = value; Invalidate(); }
    }

    protected override void OnDraw(Canvas canvas)
    {
        var cores = Tema.Atual.Botao;
        canvas.Save();
        canvas.Scale(Width / LarguraBase, Height / AlturaBase);
        DesenharTrilho(canvas, cores);
        if (_claro) DesenharIconesDoClaro(canvas, cores); else DesenharIconesDoEscuro(canvas, cores);
        DesenharPuxador(canvas, cores, _claro ? XClaro : XEscuro);
        canvas.Restore();
    }

    void DesenharTrilho(Canvas canvas, BotaoDeTemaCores k)
    {
        _p.SetStyle(Paint.Style.Fill);
        _p.SetShader(new LinearGradient(8, 4, 95, 48, k.TrilhoA, k.TrilhoB, Shader.TileMode.Clamp!));
        canvas.DrawRoundRect(1, 1, 103, 51, 25, 25, _p);
        _p.SetShader(null);
        _p.SetStyle(Paint.Style.Stroke);
        _p.StrokeWidth = 2;
        _p.Color = k.BordaDoTrilho;
        canvas.DrawRoundRect(1, 1, 103, 51, 25, 25, _p);
        _p.SetStyle(Paint.Style.Fill);
    }

    /// <summary>No escuro o puxador está à esquerda: à direita ficam o sol apagado e as estrelinhas.</summary>
    void DesenharIconesDoEscuro(Canvas canvas, BotaoDeTemaCores k)
    {
        DesenharSol(canvas, 78, Com(k.Ocioso, .72f), 1.7f, 3.2f);
        DesenharEstrela(canvas, 93, 13.4f, 2.4f, .65f, Com(k.Brilhinho, .8f));
        _p.Color = Com(k.Brilhinho, .8f);
        canvas.DrawCircle(87.5f, 39, 1.1f, _p);
    }

    /// <summary>No claro o puxador está à direita: à esquerda ficam a lua apagada e as estrelinhas.</summary>
    void DesenharIconesDoClaro(Canvas canvas, BotaoDeTemaCores k)
    {
        DesenharLua(canvas, 0, Com(k.Ocioso, .8f));
        DesenharEstrela(canvas, 12, 39.4f, 2.4f, .65f, Com(k.Brilhinho, .78f));
        _p.Color = Com(k.Brilhinho, .78f);
        canvas.DrawCircle(17, 14, 1.1f, _p);
    }

    void DesenharPuxador(Canvas canvas, BotaoDeTemaCores k, float cx)
    {
        _p.Color = Com(k.Sombra, .4f);
        for (int i = 3; i >= 1; i--) canvas.DrawCircle(cx, Meio + 2, RaioDoPuxador + i, _p);
        _p.SetShader(new LinearGradient(cx - 16, Meio - RaioDoPuxador, cx + 16, Meio + RaioDoPuxador, k.PuxadorA, k.PuxadorB, Shader.TileMode.Clamp!));
        canvas.DrawCircle(cx, Meio, RaioDoPuxador, _p);
        _p.SetShader(null);
        _p.SetStyle(Paint.Style.Stroke);
        _p.StrokeWidth = 1;
        _p.Color = k.Aro;
        canvas.DrawCircle(cx, Meio, RaioDoPuxador - .5f, _p);
        _p.SetStyle(Paint.Style.Fill);
        if (_claro) DesenharSol(canvas, cx, k.Sol, 1.8f, 3.25f);
        else { DesenharLua(canvas, cx - XEscuro, k.Lua); DesenharEstrela(canvas, 32.1f + cx - XEscuro, 14.6f, 1.5f, .45f, k.Lua); }
    }

    void DesenharSol(Canvas canvas, float cx, Color cor, float traco, float miolo)
    {
        _p.SetStyle(Paint.Style.Stroke);
        _p.StrokeCap = Paint.Cap.Round;
        _p.StrokeWidth = traco;
        _p.Color = cor;
        canvas.DrawCircle(cx, Meio, miolo, _p);
        for (int raio = 0; raio < 8; raio++)
        {
            double angulo = raio * Math.PI / 4;
            float cos = (float)Math.Cos(angulo), sen = (float)Math.Sin(angulo);
            canvas.DrawLine(cx + cos * 7.2f, Meio + sen * 7.2f, cx + cos * 9.2f, Meio + sen * 9.2f, _p);
        }
        _p.SetStyle(Paint.Style.Fill);
    }

    /// <summary>A lua crescente do SVG: dois arcos de raio 8,1 entre as pontas (23,2; 21,5) e (33,5; 31,8), com as pontas para cima e para a direita.</summary>
    void DesenharLua(Canvas canvas, float deslocamento, Color cor)
    {
        _lua ??= MontarLua();
        _p.Color = cor;
        canvas.Save();
        canvas.Translate(deslocamento, 0);
        canvas.DrawPath(_lua, _p);
        canvas.Restore();
    }

    static Path MontarLua()
    {
        var lua = new Path();
        lua.ArcTo(new RectF(25.84f - 8.1f, 29.16f - 8.1f, 25.84f + 8.1f, 29.16f + 8.1f), 251, -232, false);
        lua.ArcTo(new RectF(30.86f - 8.1f, 24.14f - 8.1f, 30.86f + 8.1f, 24.14f + 8.1f), 71, 128, false);
        lua.Close();
        return lua;
    }

    void DesenharEstrela(Canvas canvas, float cx, float cy, float ponta, float cintura, Color cor)
    {
        var e = new Path();
        e.MoveTo(cx, cy - ponta); e.LineTo(cx + cintura, cy - cintura); e.LineTo(cx + ponta, cy); e.LineTo(cx + cintura, cy + cintura);
        e.LineTo(cx, cy + ponta); e.LineTo(cx - cintura, cy + cintura); e.LineTo(cx - ponta, cy); e.LineTo(cx - cintura, cy - cintura);
        e.Close();
        _p.Color = cor;
        canvas.DrawPath(e, _p);
    }

    /// <summary>A mesma cor com a opacidade multiplicada (a arte do Windows desbota os ícones que não estão em uso).</summary>
    static Color Com(Color cor, float opacidade) => Color.Argb((int)(cor.A * Math.Clamp(opacidade, 0f, 1f)), cor.R, cor.G, cor.B);
}
