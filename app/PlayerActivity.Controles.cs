using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using Streemu.Proto.Player;
using Streemu.Proto.Ui;

namespace Streemu.Proto;

// Os controles por cima das peças: a barra de cima (de quem está tocando o som), os botões embaixo à direita, os números e o aviso do vídeo.
public partial class PlayerActivity
{
    LinearLayout _barra = null!, _botoes = null!;
    TextView _numeros = null!, _avisoVideo = null!, _barNome = null!, _barSub = null!;
    Avatar _barAvatar = null!;
    ImageView _icSom = null!, _icProporcao = null!, _icGirar = null!, _btMais = null!, _btModo = null!, _btMinis = null!, _btLayout = null!;
    Android.App.AlertDialog? _editorProporcao;
    Toast? _avisoControle;
    Enquadramento _enquadramento;
    (int Largura, int Altura)? _proporcaoPersonalizada;
    bool _controlesVisiveis, _mostrarNumeros, _mudoGlobal;

    void MontarControles()
    {
        // por cima do vídeo: a barra de cima (voltar, quem, o jogo) num degradê, e os botões embaixo à direita
        _barra = Tema.Linha(this);
        _barra.Background = new GradientDrawable(GradientDrawable.Orientation.TopBottom!, new int[] { Color.Argb(190, 0, 0, 0), Color.Transparent });
        _barra.SetPadding(Tema.Dp(this, 8), Tema.Dp(this, 8), Tema.Dp(this, 20), Tema.Dp(this, 28));
        _barra.AddView(BotaoRedondo(Resource.Drawable.ic_voltar, "Voltar", Finish));
        _barAvatar = new Avatar(this);
        var lpAv = Tema.Lp(Tema.Dp(this, 36), Tema.Dp(this, 36));
        lpAv.LeftMargin = Tema.Dp(this, 4);
        _barra.AddView(_barAvatar, lpAv);
        var textos = Tema.Coluna(this);
        textos.SetPadding(Tema.Dp(this, 10), 0, 0, 0);
        _barNome = Tema.Texto(this, "", 16, Color.White, negrito: true);
        textos.AddView(_barNome);
        _barSub = Tema.Texto(this, "", 13, Color.Argb(220, 255, 255, 255));
        _barSub.SetMaxLines(1);
        _barSub.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        textos.AddView(_barSub);
        _barra.AddView(textos, Tema.Lp(0, Tema.Ajusta, 1));
        _barra.AddView(Tema.Selo(this, "AO VIVO", Tema.AoVivo));
        _barra.Visibility = ViewStates.Gone;
        _raiz.AddView(_barra, new FrameLayout.LayoutParams(Tema.Cheio, Tema.Ajusta, GravityFlags.Top));

        _botoes = Tema.Linha(this);
        _botoes.SetPadding(Tema.Dp(this, 12), Tema.Dp(this, 12), Tema.Dp(this, 12), Tema.Dp(this, 12));
        _btMais = BotaoRedondo(Resource.Drawable.ic_mais_live, "Assistir mais uma live", AdicionarLive);
        _btModo = BotaoRedondo(Resource.Drawable.ic_destaque, "Pôr uma live em destaque", AlternarModo);
        _btMinis = BotaoRedondo(Resource.Drawable.ic_olho, "Esconder as miniaturas", AlternarMiniaturas);
        _icSom = BotaoRedondo(Resource.Drawable.ic_som, "Sem som", AlternarSom);
        _icGirar = BotaoRedondo(Resource.Drawable.ic_girar, "Girar tela", GirarTela);
        _icGirar.LongClick += (_, e) =>
        {
            e.Handled = true;
            RequestedOrientation = ScreenOrientation.User;
            AtualizarDescricaoRotacao();
            AvisarControle("Rotação do sistema");
            Controles(true);
        };
        AtualizarDescricaoRotacao();
        _icProporcao = BotaoRedondo(Resource.Drawable.ic_proporcao, "Proporção do vídeo: Original. Segure para personalizar.", AlternarProporcao);
        _icProporcao.LongClick += (_, e) => { e.Handled = true; EditarProporcao(); };
        foreach (var b in new View[] { _btMais, _btModo, _btMinis, _icSom, _icGirar, _icProporcao }) _botoes.AddView(b);
        _botoes.AddView(BotaoRedondo(Resource.Drawable.ic_info, "Números da conexão", () => { _mostrarNumeros = !_mostrarNumeros; _numeros.Visibility = _mostrarNumeros ? ViewStates.Visible : ViewStates.Gone; Controles(true); }));
        _botoes.Visibility = ViewStates.Gone;
        _raiz.AddView(_botoes, new FrameLayout.LayoutParams(Tema.Ajusta, Tema.Ajusta, GravityFlags.Bottom | GravityFlags.End));
        MontarBotaoDeLayout();

        _numeros = Tema.Texto(this, "", 11, Color.White);
        _numeros.Typeface = Typeface.Monospace;
        _numeros.Background = Tema.Forma(this, Color.Argb(170, 0, 0, 0), 10);
        _numeros.SetPadding(Tema.Dp(this, 10), Tema.Dp(this, 8), Tema.Dp(this, 10), Tema.Dp(this, 8));
        _numeros.Visibility = ViewStates.Gone;
        var lpN = new FrameLayout.LayoutParams(Tema.Ajusta, Tema.Ajusta, GravityFlags.Top | GravityFlags.Start);
        lpN.SetMargins(Tema.Dp(this, 16), Tema.Dp(this, 72), Tema.Dp(this, 16), 0);
        _raiz.AddView(_numeros, lpN);

        // o aviso de que o celular talvez não dê conta da live, por cima do vídeo por alguns segundos quando a imagem abre
        _avisoVideo = Tema.Texto(this, "", 13, Color.White);
        _avisoVideo.Background = Tema.Forma(this, Color.Argb(200, 0, 0, 0), 10);
        _avisoVideo.SetPadding(Tema.Dp(this, 14), Tema.Dp(this, 8), Tema.Dp(this, 14), Tema.Dp(this, 8));
        _avisoVideo.Visibility = ViewStates.Gone;
        var lpAviso = new FrameLayout.LayoutParams(Tema.Ajusta, Tema.Ajusta, GravityFlags.Top | GravityFlags.CenterHorizontal);
        lpAviso.TopMargin = Tema.Dp(this, 72);
        _raiz.AddView(_avisoVideo, lpAviso);
    }

    ImageView BotaoRedondo(int icone, string descricao, Action tocar)
    {
        var b = Tema.Icone(this, icone, Color.White, 48);
        b.SetPadding(Tema.Dp(this, 12), Tema.Dp(this, 12), Tema.Dp(this, 12), Tema.Dp(this, 12));
        b.Background = new RippleDrawable(Android.Content.Res.ColorStateList.ValueOf(Color.Argb(80, 255, 255, 255)), null, Tema.Forma(this, Color.White, 24));
        b.ContentDescription = descricao;
        b.Clickable = true;
        b.Click += (_, _) => { tocar(); if (_controlesVisiveis) Controles(true); };
        return b;
    }

    sealed class Recorte(PlayerActivity a) : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
        {
            int l = 0, r = 0, t = 0;
            if (OperatingSystem.IsAndroidVersionAtLeast(28) && insets.DisplayCutout is { } c) { l = c.SafeInsetLeft; r = c.SafeInsetRight; t = c.SafeInsetTop; }
            foreach (var view in new View[] { a._barra, a._botoes, a._numeros })
                if (view.LayoutParameters is FrameLayout.LayoutParams p) { p.LeftMargin = Math.Max(p.LeftMargin, l); p.RightMargin = Math.Max(p.RightMargin, r); p.TopMargin = Math.Max(view == a._numeros ? p.TopMargin : 0, t); view.LayoutParameters = p; }
            return insets;
        }
    }

    /// <summary>Mostra (e agenda para esconder em 3 s) ou esconde os controles, com um esmaecer curto.</summary>
    void Controles(bool mostrar)
    {
        _ui.RemoveCallbacks(_esconder);
        _controlesVisiveis = mostrar;
        foreach (var v in new View[] { _barra, _botoes })
        {
            v.Animate()!.Cancel();
            if (mostrar)
            {
                if (v.Visibility != ViewStates.Visible) { v.Alpha = 0; v.Visibility = ViewStates.Visible; }
                v.Animate()!.Alpha(1).SetDuration(150).Start();
            }
            else v.Animate()!.Alpha(0).SetDuration(200).WithEndAction(new Java.Lang.Runnable(() => { if (!_controlesVisiveis) v.Visibility = ViewStates.Gone; })).Start();
        }
        AtualizarBotaoDeLayout();
        if (!mostrar) TelaCheia();
        else if (_editorProporcao == null) _ui.PostDelayed(_esconder, 3000);
    }

    /// <summary>O avatar, o nome e o jogo da barra de cima: os da live que está tocando o som.</summary>
    void AtualizarBarra()
    {
        if (_doSom == null) return;
        var p = _doSom.Peca;
        _barAvatar.Mostrar(p.Conta, p.Apelido, Sessao.Avatar(p.Conta), aoVivo: true);
        _barNome.Text = p.CodigoTeste != null ? "Modo de teste" : p.Apelido;
        var sub = string.Join(" · ", new[] { p.Jogo, p.Qualidade, Tema.AoVivoHa(p.Desde) }.Where(s => s.Length > 0));
        _barSub.Text = sub;
        _barSub.Visibility = sub.Length > 0 ? ViewStates.Visible : ViewStates.Gone;
    }

    void AtualizarNumeros()
    {
        var ativas = _molduras.Where(m => m.Peca.Numeros.Length > 0).ToList();
        _numeros.Text = ativas.Count == 1 && _molduras.Count == 1 ? ativas[0].Peca.Numeros
            : string.Join("\n\n", ativas.Select(m => $"{m.Peca.Apelido}\n{m.Peca.Numeros}"));
    }

    /// <summary>O mesmo aviso da espera, por cima do vídeo por 6 s quando a imagem abre numa live que o celular talvez não acompanhe.</summary>
    void MostrarAvisoDeVideo(string texto)
    {
        if (_avisoVideo.Visibility == ViewStates.Visible) return;
        _avisoVideo.Text = texto;
        _avisoVideo.Alpha = 1;
        _avisoVideo.Visibility = ViewStates.Visible;
        _avisoVideo.Animate()!.Alpha(0).SetStartDelay(6000).SetDuration(400).WithEndAction(new Java.Lang.Runnable(() => _avisoVideo.Visibility = ViewStates.Gone)).Start();
    }

    void AlternarSom()
    {
        _mudoGlobal = !_mudoGlobal;
        AplicarSom();
        _icSom.SetImageResource(_mudoGlobal ? Resource.Drawable.ic_sem_som : Resource.Drawable.ic_som);
        _icSom.ContentDescription = _mudoGlobal ? "Ligar o som" : "Sem som";
    }

    void GirarTela()
    {
        bool deitado = RequestedOrientation switch
        {
            ScreenOrientation.Landscape => true,
            ScreenOrientation.Portrait => false,
            _ => Resources!.Configuration!.Orientation == Android.Content.Res.Orientation.Landscape,
        };
        RequestedOrientation = deitado ? ScreenOrientation.Portrait : ScreenOrientation.Landscape;
        AtualizarDescricaoRotacao();
        AvisarControle(deitado ? "Vertical" : "Horizontal");
    }

    void AtualizarDescricaoRotacao()
    {
        _icGirar.ContentDescription = RequestedOrientation switch
        {
            ScreenOrientation.Portrait => "Girar para horizontal. Segure para seguir a rotação do sistema.",
            ScreenOrientation.Landscape => "Girar para vertical. Segure para seguir a rotação do sistema.",
            _ => "Girar tela. Seguindo a rotação do sistema.",
        };
    }

    void AlternarProporcao()
    {
        var ultimo = _proporcaoPersonalizada.HasValue ? Enquadramento.Personalizado : Enquadramento.Vertical;
        SelecionarProporcao(_enquadramento == ultimo ? Enquadramento.Original : _enquadramento + 1);
    }

    void AvisarControle(string texto)
    {
        _avisoControle?.Cancel();
        _avisoControle = Toast.MakeText(this, texto, ToastLength.Short);
        _avisoControle?.Show();
    }

    void EditarProporcao()
    {
        if (_doSom?.Peca.EstaTocando != true || _editorProporcao != null) return;
        var (larguraAtual, alturaAtual) = _proporcaoPersonalizada ?? (16, 9);
        var campo = new EditText(this) { Text = $"{larguraAtual}:{alturaAtual}", Hint = "Largura:altura" };
        campo.SetSingleLine(true);
        campo.SetSelectAllOnFocus(true);
        var moldura = new FrameLayout(this);
        moldura.SetPadding(Tema.Dp(this, 22), 0, Tema.Dp(this, 22), 0);
        moldura.AddView(campo);
        var caixa = new Android.App.AlertDialog.Builder(this, Android.Resource.Style.ThemeDeviceDefaultDialogAlert)
            .SetTitle("Proporção personalizada")!
            .SetMessage("Digite largura:altura, por exemplo 19:9.")!
            .SetView(moldura)!
            .SetPositiveButton("Aplicar", (_, _) => { })!
            .SetNegativeButton("Cancelar", (_, _) => { })!
            .Create()!;
        campo.RequestFocus();
        caixa.Window?.SetSoftInputMode(SoftInput.StateAlwaysVisible);
        _editorProporcao = caixa;
        _ui.RemoveCallbacks(_esconder);
        caixa.DismissEvent += (_, _) =>
        {
            _editorProporcao = null;
            if (!_fechando && !IsFinishing) Controles(true);
        };
        caixa.Show();
        caixa.GetButton((int)Android.Content.DialogButtonType.Positive)!.Click += (_, _) =>
        {
            var partes = campo.Text?.Split(':');
            if (partes is not { Length: 2 }
                || !int.TryParse(partes[0], out int largura) || !int.TryParse(partes[1], out int altura)
                || largura <= 0 || altura <= 0)
            {
                campo.Error = "Use dois números inteiros positivos, como 19:9.";
                return;
            }
            _proporcaoPersonalizada = (largura, altura);
            caixa.Dismiss();
            SelecionarProporcao(Enquadramento.Personalizado);
        };
    }

    void SelecionarProporcao(Enquadramento modo)
    {
        string descricao = modo switch
        {
            Enquadramento.Ampliar => "Ampliar",
            Enquadramento.Preencher => "Preencher tela",
            Enquadramento.Widescreen => "16:9",
            Enquadramento.Classico => "4:3",
            Enquadramento.Ultrawide => "21:9",
            Enquadramento.Vertical => "9:16",
            Enquadramento.Personalizado when _proporcaoPersonalizada is { } p => $"{p.Largura}:{p.Altura}",
            _ => "Original",
        };
        _enquadramento = modo;
        _icProporcao.ContentDescription = $"Proporção do vídeo: {descricao}. Segure para personalizar.";
        Organizar();
        AvisarControle(descricao);
    }

    /// <summary>
    /// O botão de layout discreto: um círculo pequeno e translúcido no canto de baixo à direita, sempre à vista com mais de uma live (some quando os
    /// controles aparecem, que trazem o mesmo botão). Alterna entre as lives divididas em grade e uma só em destaque com as outras em miniaturas embaixo;
    /// o ícone mostra o layout para o qual o toque leva.
    /// </summary>
    void MontarBotaoDeLayout()
    {
        _btLayout = Tema.Icone(this, Resource.Drawable.ic_destaque, Color.White, 40);
        _btLayout.SetPadding(Tema.Dp(this, 10), Tema.Dp(this, 10), Tema.Dp(this, 10), Tema.Dp(this, 10));
        _btLayout.Background = Tema.Forma(this, Color.Argb(120, 0, 0, 0), 20);
        _btLayout.Alpha = 0.85f;
        _btLayout.Clickable = true;
        _btLayout.Click += (_, _) => AlternarModo();
        _btLayout.Visibility = ViewStates.Gone;
        var lp = new FrameLayout.LayoutParams(Tema.Dp(this, 40), Tema.Dp(this, 40), GravityFlags.Bottom | GravityFlags.End);
        lp.SetMargins(0, 0, Tema.Dp(this, 12), Tema.Dp(this, 12));
        _raiz.AddView(_btLayout, lp);
    }

    /// <summary>Ícone e texto do botão para o layout seguinte, e se o botão discreto aparece agora.</summary>
    void AtualizarBotaoDeLayout()
    {
        int icone = _destaque ? Resource.Drawable.ic_grade : Resource.Drawable.ic_destaque;
        string texto = _destaque ? "Dividir as lives em grade" : "Uma live grande e as outras embaixo";
        foreach (var b in new[] { _btModo, _btLayout }) { b.SetImageResource(icone); b.ContentDescription = texto; }
        _btLayout.Visibility = _molduras.Count > 1 && !_controlesVisiveis ? ViewStates.Visible : ViewStates.Gone;
    }
}
