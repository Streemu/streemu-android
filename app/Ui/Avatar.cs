using Android.Content;
using Android.Graphics;
using Android.Views;
using Streemu.Proto.Avatares;

namespace Streemu.Proto.Ui;

/// <summary>
/// Avatar redondo: a foto do amigo, ou a inicial sobre uma cor fixa da pessoa. Com <see cref="AoVivo"/>, ganha o anel vermelho da live.
/// Um avatar de GIF fica parado no primeiro quadro; <see cref="AnimarAoTocar"/> e <see cref="AnimarSozinho"/> ligam a animação (spec 005).
/// </summary>
sealed class Avatar : View
{
    readonly Paint _pinta = new(PaintFlags.AntiAlias | PaintFlags.FilterBitmap);
    readonly Paint _anel = new(PaintFlags.AntiAlias) { Color = Tema.AoVivo };
    readonly Paint _texto = new(PaintFlags.AntiAlias) { Color = Color.White, TextAlign = Paint.Align.Center };
    readonly Android.Graphics.Path _circulo = new();
    Bitmap? _foto;
    string _conta = "";
    string _inicial = "?";
    Color _cor = Tema.Destaque;
    bool _aoVivo, _desenhouAnimado;

    public Avatar(Context c) : base(c)
    {
        _texto.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal));
        _anel.SetStyle(Paint.Style.Stroke);
    }

    public void Mostrar(string conta, string apelido, Bitmap? foto, bool aoVivo = false)
    {
        _conta = conta;
        _foto = foto;
        _inicial = apelido.Trim() is { Length: > 0 } a ? char.ToUpperInvariant(a[0]).ToString() : "?";
        _cor = Tema.CorDe(conta);
        _aoVivo = aoVivo;
        Invalidate();
    }

    public bool AoVivo { set { _aoVivo = value; Invalidate(); } }

    /// <summary>
    /// Só se esta pessoa tem um GIF: o toque nele liga ou desliga a animação. Sem GIF o avatar nem fica clicável, e o toque segue para quem está embaixo.
    /// Chamar depois do <see cref="Mostrar"/>. Ex.: <c>av.AnimarAoTocar();</c>
    /// </summary>
    public void AnimarAoTocar()
    {
        if (Sessao.AvatarAnimado(_conta) is not { } animacao) return;
        Click += (_, _) => RelogioDeAvatar.Alternar(_conta, animacao);
        ContentDescription = "Avatar animado. Toque para mexer ou parar";
    }

    /// <summary>Anima desde que aparece na tela (o meu avatar no cartão "Você"). Chamar depois do <see cref="Mostrar"/>.</summary>
    public void AnimarSozinho()
    {
        if (Sessao.AvatarAnimado(_conta) is { } animacao) RelogioDeAvatar.Ligar(_conta, animacao);
    }

    protected override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        RelogioDeAvatar.Quadro += AoQuadro;
    }

    protected override void OnDetachedFromWindow()
    {
        RelogioDeAvatar.Quadro -= AoQuadro;
        base.OnDetachedFromWindow();
    }

    /// <summary>Redesenha só quem está animando, e uma vez mais quem acabou de parar (para voltar à foto parada).</summary>
    void AoQuadro()
    {
        if (_desenhouAnimado || RelogioDeAvatar.EstaTocando(_conta)) Invalidate();
    }

    protected override void OnDraw(Canvas canvas)
    {
        float w = Width, h = Height, r = Math.Min(w, h) / 2f;
        float folga = _aoVivo ? Math.Max(3f, r * 0.12f) : 0;
        float ri = r - folga;
        if (_aoVivo)
        {
            _anel.StrokeWidth = Math.Max(2f, r * 0.07f);
            canvas.DrawCircle(w / 2, h / 2, r - _anel.StrokeWidth / 2, _anel);
        }
        var animado = RelogioDeAvatar.QuadroDe(_conta);
        _desenhouAnimado = animado != null;
        var imagem = animado ?? _foto;
        if (imagem != null)
        {
            DesenharFoto(canvas, imagem, w, h, ri);
            return;
        }
        _pinta.Color = _cor;
        canvas.DrawCircle(w / 2, h / 2, ri, _pinta);
        _texto.TextSize = ri * 0.95f;
        var fm = _texto.GetFontMetrics();
        canvas.DrawText(_inicial, w / 2, h / 2 - (fm!.Ascent + fm.Descent) / 2, _texto);
    }

    void DesenharFoto(Canvas canvas, Bitmap foto, float w, float h, float ri)
    {
        canvas.Save();
        _circulo.Reset();
        _circulo.AddCircle(w / 2, h / 2, ri, Android.Graphics.Path.Direction.Cw!);
        canvas.ClipPath(_circulo);
        canvas.DrawBitmap(foto, null, new RectF(w / 2 - ri, h / 2 - ri, w / 2 + ri, h / 2 + ri), _pinta);
        canvas.Restore();
    }
}
