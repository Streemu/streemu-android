using Android.Content;
using Android.Graphics;
using Android.Views;

namespace Streemu.Proto.Ui;

/// <summary>Avatar redondo: a foto do amigo, ou a inicial sobre uma cor fixa da pessoa. Com <see cref="AoVivo"/>, ganha o anel vermelho da live.</summary>
sealed class Avatar : View
{
    readonly Paint _pinta = new(PaintFlags.AntiAlias | PaintFlags.FilterBitmap);
    readonly Paint _anel = new(PaintFlags.AntiAlias) { Color = Tema.AoVivo };
    readonly Paint _texto = new(PaintFlags.AntiAlias) { Color = Color.White, TextAlign = Paint.Align.Center };
    readonly Android.Graphics.Path _circulo = new();
    Bitmap? _foto;
    string _inicial = "?";
    Color _cor = Tema.Destaque;
    bool _aoVivo;

    public Avatar(Context c) : base(c)
    {
        _texto.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal));
        _anel.SetStyle(Paint.Style.Stroke);
    }

    public void Mostrar(string conta, string apelido, Bitmap? foto, bool aoVivo = false)
    {
        _foto = foto;
        _inicial = apelido.Trim() is { Length: > 0 } a ? char.ToUpperInvariant(a[0]).ToString() : "?";
        _cor = Tema.CorDe(conta);
        _aoVivo = aoVivo;
        Invalidate();
    }

    public bool AoVivo { set { _aoVivo = value; Invalidate(); } }

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
        if (_foto != null)
        {
            canvas.Save();
            _circulo.Reset();
            _circulo.AddCircle(w / 2, h / 2, ri, Android.Graphics.Path.Direction.Cw!);
            canvas.ClipPath(_circulo);
            canvas.DrawBitmap(_foto, null, new RectF(w / 2 - ri, h / 2 - ri, w / 2 + ri, h / 2 + ri), _pinta);
            canvas.Restore();
            return;
        }
        _pinta.Color = _cor;
        canvas.DrawCircle(w / 2, h / 2, ri, _pinta);
        _texto.TextSize = ri * 0.95f;
        var fm = _texto.GetFontMetrics();
        canvas.DrawText(_inicial, w / 2, h / 2 - (fm!.Ascent + fm.Descent) / 2, _texto);
    }
}
