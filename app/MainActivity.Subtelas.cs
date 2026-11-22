using Android.Content;

namespace Streemu.Proto;

/// <summary>
/// As subtelas: uma tela que abre por cima de uma aba, no lugar do conteúdo dela, com a barra de baixo à vista (Transmitir no Início, Novidades em
/// Mais). Fecham pela seta, pelo voltar do sistema ou trocando de aba.
/// </summary>
public partial class MainActivity
{
    enum Subtela { Nenhuma, Transmitir, Novidades }

    Subtela _subtela, _subtelaDaTela;
    VoltarDaSubtela? _voltar;

    /// <summary>Ex.: <c>AbrirSubtela(Subtela.Novidades)</c></summary>
    void AbrirSubtela(Subtela qual)
    {
        _subtela = qual;
        RegistrarVoltar();
        MostrarAba();
        _telaAtual?.ScrollTo(0, 0);
    }

    void FecharSubtela()
    {
        if (!EsquecerSubtela()) return;
        MostrarAba();
    }

    /// <summary>Tira a subtela sem redesenhar (quem chama já vai redesenhar). Falso se nenhuma estava aberta.</summary>
    bool EsquecerSubtela()
    {
        if (_subtela == Subtela.Nenhuma) return false;
        _subtela = Subtela.Nenhuma;
        TirarVoltar();
        return true;
    }

    bool FecharSeAberta()
    {
        if (_subtela == Subtela.Nenhuma) return false;
        FecharSubtela();
        return true;
    }

    // Android 13+ com o voltar preditivo (o padrão de quem mira o Android 16): o voltar do sistema não chama o OnBackPressed, passa por este registro
    void RegistrarVoltar()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33) || _voltar != null) return;
        _voltar = new VoltarDaSubtela(FecharSubtela);
        OnBackInvokedDispatcher.RegisterOnBackInvokedCallback(0, _voltar);   // 0 = PRIORITY_DEFAULT
    }

    void TirarVoltar()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33) || _voltar == null) return;
        OnBackInvokedDispatcher.UnregisterOnBackInvokedCallback(_voltar);
        _voltar = null;
    }

    // antes do Android 13 (ou sem o voltar preditivo), o voltar chega por aqui
#pragma warning disable CS0672, CA1422
    public override void OnBackPressed()
    {
        if (FecharSeAberta()) return;
        base.OnBackPressed();
    }
#pragma warning restore CS0672, CA1422

    sealed class VoltarDaSubtela : Java.Lang.Object, Android.Window.IOnBackInvokedCallback
    {
        readonly Action _fechar;
        public VoltarDaSubtela(Action fechar) { _fechar = fechar; }
        public void OnBackInvoked() => _fechar();
    }
}
