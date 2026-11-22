using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using Streemu.Proto.Ui;

namespace Streemu.Proto.Player;

/// <summary>A opção "Aviso de qualidade na tela" (ligada por padrão), guardada nas preferências do app.</summary>
static class AvisoDeQualidade
{
    const string Chave = "aviso_qualidade";
    public static bool Ligado(Context c) => c.GetSharedPreferences("streemu", FileCreationMode.Private)!.GetBoolean(Chave, true);
    public static void Definir(Context c, bool ligado) => c.GetSharedPreferences("streemu", FileCreationMode.Private)!.Edit()!.PutBoolean(Chave, ligado)!.Apply();
}

/// <summary>
/// O aviso de qualidade por cima do vídeo de uma peça: uma pílula pequena (bolinha amarela ou vermelha e um ícone da causa) e, ao tocar, uma faixa
/// com a frase por 4 s. Fica Gone enquanto a qualidade está boa. Só desenha quando o estado mostrado muda: sem animação contínua nem invalidate periódico.
/// A área de toque é a pílula mais 12 dp em volta (48 dp de altura); fora dela o toque passa para a peça.
/// </summary>
sealed class IndicadorDeQualidade : LinearLayout
{
    const int Amarelo = unchecked((int)0xFFFFC107), Vermelho = unchecked((int)0xFFF44336), Verde = unchecked((int)0xFF4CAF50);
    readonly Pilula _pilula;
    readonly TextView _frase;
    readonly Java.Lang.Runnable _esconderFrase;
    ResultadoDaQualidade _atual;

    public IndicadorDeQualidade(Context c) : base(c)
    {
        Orientation = Android.Widget.Orientation.Horizontal;
        SetGravity(GravityFlags.CenterVertical);
        Visibility = ViewStates.Gone;
        Alpha = 0;
        Clickable = true;
        _frase = Tema.Texto(c, "", 13, Color.White);
        _frase.Background = Tema.Forma(c, Color.Argb(115, 0, 0, 0), 12);
        _frase.SetPadding(Tema.Dp(c, 10), Tema.Dp(c, 4), Tema.Dp(c, 10), Tema.Dp(c, 4));
        _frase.Visibility = ViewStates.Gone;
        AddView(_frase, new LayoutParams(Tema.Ajusta, Tema.Ajusta) { RightMargin = Tema.Dp(c, 6) });
        _pilula = new Pilula(c);
        AddView(_pilula, new LayoutParams(Tema.Ajusta, Tema.Ajusta));
        _esconderFrase = new Java.Lang.Runnable(() => _frase.Visibility = ViewStates.Gone);
        Click += (_, _) => AlternarFrase();
        Tamanho(false);
    }

    /// <summary>Peça pequena (largura menor que 240 dp): só a bolinha de 10 dp, com a mesma área de toque de 48 dp.</summary>
    public void Tamanho(bool pequena)
    {
        _pilula.Pequena = pequena;
        int pad = Tema.Dp(Context!, pequena ? 19 : 12);
        SetPadding(pad, pad, pad, pad);
    }

    public void Limitar(int larguraMaxima) => _frase.SetMaxWidth(Math.Max(Tema.Dp(Context!, 120), larguraMaxima));

    /// <summary>
    /// Mostra, troca ou esconde o aviso conforme o estado final (só chamado quando ele mudou). Trocar de um estado visível para outro não pisca: só muda a cor e o ícone.
    /// Some em 200 ms quando a qualidade volta ao normal, e em 600 ms quando o aviso é recolhido ou depois do verde.
    /// </summary>
    public void Mostrar(ResultadoDaQualidade r)
    {
        bool tinha = _atual.Estado is EstadoDoAviso.Alerta or EstadoDoAviso.Ruim or EstadoDoAviso.Normalizado;
        var antes = _atual.Estado;
        _atual = r;
        RemoveCallbacks(_esconderFrase);
        if (r.Estado is not (EstadoDoAviso.Alerta or EstadoDoAviso.Ruim or EstadoDoAviso.Normalizado))
        {
            _frase.Visibility = ViewStates.Gone;
            ContentDescription = null;
            if (!tinha) return;
            long ms = antes == EstadoDoAviso.Normalizado || r.Estado == EstadoDoAviso.Recolhido ? 600 : 200;
            Animate()!.Cancel();
            Animate()!.Alpha(0).SetDuration(ms).WithEndAction(new Java.Lang.Runnable(() => { if (_atual.Estado is not (EstadoDoAviso.Alerta or EstadoDoAviso.Ruim or EstadoDoAviso.Normalizado)) Visibility = ViewStates.Gone; })).Start();
            return;
        }
        if (r.Estado == EstadoDoAviso.Normalizado) _pilula.Definir(Verde, r.Causa, certo: true);
        else _pilula.Definir(r.Estado == EstadoDoAviso.Ruim ? Vermelho : Amarelo, r.Causa, certo: false);
        _frase.Text = r.Frase;
        ContentDescription = r.Frase;
        if (_frase.Visibility == ViewStates.Visible) PostDelayed(_esconderFrase, 4000);
        if (Visibility != ViewStates.Visible) { Alpha = 0; Visibility = ViewStates.Visible; }
        Animate()!.Cancel();
        Animate()!.Alpha(1).SetDuration(200).Start();
    }

    void AlternarFrase()
    {
        RemoveCallbacks(_esconderFrase);
        if (_frase.Visibility == ViewStates.Visible) { _frase.Visibility = ViewStates.Gone; return; }
        _frase.Visibility = ViewStates.Visible;
        PostDelayed(_esconderFrase, 4000);
    }

    protected override void OnDetachedFromWindow()
    {
        RemoveCallbacks(_esconderFrase);
        base.OnDetachedFromWindow();
    }

    /// <summary>A pílula desenhada: fundo preto a 45%, bolinha colorida e o ícone da causa em linhas brancas.</summary>
    sealed class Pilula : View
    {
        readonly Paint _fundo = new(PaintFlags.AntiAlias), _bola = new(PaintFlags.AntiAlias), _linha = new(PaintFlags.AntiAlias);
        readonly RectF _r = new();
        readonly Android.Graphics.Path _caminho = new();
        readonly float _dp;
        CausaDaQualidade _causa;
        bool _pequena, _certo;

        public Pilula(Context c) : base(c)
        {
            _dp = c.Resources!.DisplayMetrics!.Density;
            ImportantForAccessibility = ImportantForAccessibility.No;
            _fundo.Color = Color.Argb(115, 0, 0, 0);
            _linha.Color = Color.White;
            _linha.SetStyle(Paint.Style.Stroke);
            _linha.StrokeWidth = 1.6f * _dp;
            _linha.StrokeCap = Paint.Cap.Round;
            _linha.StrokeJoin = Paint.Join.Round;
        }

        public bool Pequena { set { _pequena = value; RequestLayout(); Invalidate(); } }

        public void Definir(int cor, CausaDaQualidade causa, bool certo) { _bola.Color = new Color(cor); _causa = causa; _certo = certo; Invalidate(); }

        protected override void OnMeasure(int w, int h) => SetMeasuredDimension((int)((_pequena ? 10 : 46) * _dp), (int)((_pequena ? 10 : 24) * _dp));

        protected override void OnDraw(Canvas canvas)
        {
            float largura = Width, altura = Height;
            if (_pequena) { canvas.DrawCircle(largura / 2, altura / 2, largura / 2, _bola); return; }
            canvas.DrawRoundRect(0, 0, largura, altura, altura / 2, altura / 2, _fundo);
            canvas.DrawCircle(altura / 2, altura / 2, 5 * _dp, _bola);
            Icone(canvas, altura + (largura - altura) / 2 - _dp, altura / 2, 7 * _dp);
        }

        // ícones de ~14 dp centrados em (cx, cy); r é a meia altura
        void Icone(Canvas c, float cx, float cy, float r)
        {
            if (_certo)
            {
                // "certo": um ✓ em linha
                _caminho.Reset();
                _caminho.MoveTo(cx - r * 0.8f, cy + r * 0.05f);
                _caminho.LineTo(cx - r * 0.25f, cy + r * 0.6f);
                _caminho.LineTo(cx + r * 0.85f, cy - r * 0.6f);
                c.DrawPath(_caminho, _linha);
                return;
            }
            switch (_causa)
            {
                case CausaDaQualidade.Rede:
                    // Wi-Fi: três arcos e um ponto
                    for (int i = 1; i <= 3; i++)
                    {
                        float raio = r * i / 3f * 1.5f;
                        _r.Set(cx - raio, cy + r * 0.7f - raio, cx + raio, cy + r * 0.7f + raio);
                        c.DrawArc(_r, 225, 90, false, _linha);
                    }
                    Ponto(c, cx, cy + r * 0.7f);
                    break;
                case CausaDaQualidade.Aparelho:
                    // celular: retângulo em pé com um traço embaixo
                    _r.Set(cx - r * 0.55f, cy - r, cx + r * 0.55f, cy + r);
                    c.DrawRoundRect(_r, 2 * _dp, 2 * _dp, _linha);
                    c.DrawLine(cx - r * 0.2f, cy + r * 0.62f, cx + r * 0.2f, cy + r * 0.62f, _linha);
                    break;
                case CausaDaQualidade.Transmissor:
                    // monitor: tela e pé
                    _r.Set(cx - r, cy - r * 0.75f, cx + r, cy + r * 0.35f);
                    c.DrawRoundRect(_r, 1.5f * _dp, 1.5f * _dp, _linha);
                    c.DrawLine(cx, cy + r * 0.35f, cx, cy + r * 0.8f, _linha);
                    c.DrawLine(cx - r * 0.5f, cy + r * 0.85f, cx + r * 0.5f, cy + r * 0.85f, _linha);
                    break;
                default:
                    // seta circular: três quartos de volta com a ponta
                    float rr = r * 0.85f;
                    _r.Set(cx - rr, cy - rr, cx + rr, cy + rr);
                    c.DrawArc(_r, -60, 280, false, _linha);
                    _caminho.Reset();
                    float ax = cx + rr * (float)Math.Cos(220 * Math.PI / 180), ay = cy + rr * (float)Math.Sin(220 * Math.PI / 180);
                    _caminho.MoveTo(ax + 3.2f * _dp, ay - 0.5f * _dp);
                    _caminho.LineTo(ax, ay);
                    _caminho.LineTo(ax + 0.2f * _dp, ay + 3.4f * _dp);
                    c.DrawPath(_caminho, _linha);
                    break;
            }
        }

        void Ponto(Canvas c, float x, float y)
        {
            _linha.SetStyle(Paint.Style.Fill);
            c.DrawCircle(x, y, 1.3f * _dp, _linha);
            _linha.SetStyle(Paint.Style.Stroke);
        }
    }
}
