using SurfaceTexture = Android.Graphics.SurfaceTexture;
using Android.Opengl;
using Android.OS;
using Android.Views;
using Java.Nio;
using Log = Streemu.Util.Log;

namespace Streemu.Proto.Transmissao;

/// <summary>
/// O passo OpenGL ES 2 entre o VirtualDisplay e o encoder: o VirtualDisplay desenha numa SurfaceTexture (textura OES) e uma thread própria, num relógio
/// fixo de 1/fps, desenha o último quadro na Surface de entrada do encoder (com o tempo do relógio como PTS). Com a tela parada o display não manda
/// quadro e o encoder da Qualcomm não solta nada (nem IDR); assim o fps é constante e o IDR sai sempre no tique seguinte ao pedido.
/// Todo o EGL/GL vive na thread de render (nasce, roda e é liberado nela).
/// </summary>
sealed class CapturaGl
{
    readonly int _fps;
    readonly Handler _avisos;
    readonly Action<string> _falhou;
    readonly Thread _thread;
    readonly ManualResetEventSlim _pronto = new(false);
    readonly object _gate = new();      // só protege o pedido de troca (curto)
    readonly object _gateTroca = new(); // quem pede a troca espera dentro dele
    Exception? _erroInicio;
    volatile bool _parar;
    volatile bool _novoQuadro;

    // pedido de troca da saída (rotação), atendido pela thread de render
    Surface? _saidaNova; int _wNovo, _hNovo;
    readonly ManualResetEventSlim _trocou = new(false);
    Exception? _erroTroca;

    Surface _saida;
    int _w, _h;
    SurfaceTexture? _st;
    Surface? _entradaVd;

    public long Quadros;   // desenhados (para os números)

    /// <summary>A Surface em que o VirtualDisplay desenha.</summary>
    public Surface EntradaVd => _entradaVd!;

    public CapturaGl(Surface saida, int w, int h, int fps, Handler avisos, Action<string> falhou)
    {
        _saida = saida; _w = w; _h = h; _fps = fps; _avisos = avisos; _falhou = falhou;
        _thread = new Thread(Laco) { IsBackground = true, Name = "captura-gl" };
    }

    /// <summary>Sobe a thread e espera o EGL/GL ficarem prontos (lança se não deu).</summary>
    public void Iniciar()
    {
        _thread.Start();
        if (!_pronto.Wait(5000)) throw new InvalidOperationException("O OpenGL da captura não iniciou.");
        if (_erroInicio != null) throw new InvalidOperationException($"O OpenGL da captura falhou: {_erroInicio.Message}", _erroInicio);
    }

    /// <summary>Rotação: passa a desenhar em outra Surface de encoder, com o tamanho novo. Volta quando a troca foi feita.</summary>
    public void Trocar(Surface saida, int w, int h)
    {
        lock (_gateTroca)
        {
            _trocou.Reset(); _erroTroca = null;
            lock (_gate) { _wNovo = w; _hNovo = h; _saidaNova = saida; }
            if (!_trocou.Wait(3000)) throw new InvalidOperationException("A thread do OpenGL não atendeu a troca de tamanho.");
            if (_erroTroca != null) throw new InvalidOperationException(_erroTroca.Message, _erroTroca);
        }
    }

    public void Parar()
    {
        _parar = true;
        try { if (_thread.IsAlive && _thread != Thread.CurrentThread) _thread.Join(2000); } catch { }
    }

    // ------------------------------------------------------------------ thread de render

    sealed class OnFrame(CapturaGl g) : Java.Lang.Object, SurfaceTexture.IOnFrameAvailableListener
    {
        public void OnFrameAvailable(SurfaceTexture? st) => g._novoQuadro = true;
    }

    const string Vertice = "uniform mat4 uTex;attribute vec4 aPos;attribute vec4 aTex;varying vec2 vTex;void main(){gl_Position=aPos;vTex=(uTex*aTex).xy;}";
    const string Fragmento = "#extension GL_OES_EGL_image_external:require\nprecision mediump float;varying vec2 vTex;uniform samplerExternalOES uImg;void main(){gl_FragColor=texture2D(uImg,vTex);}";

    void Laco()
    {
        Process.SetThreadPriority(Android.OS.ThreadPriority.UrgentDisplay);
        EGLDisplay? dpy = null; EGLContext? ctx = null; EGLSurface? surf = null; EGLConfig? cfg = null;
        int tex = 0, prog = 0;
        try
        {
            dpy = EGL14.EglGetDisplay(EGL14.EglDefaultDisplay);
            var v = new int[2];
            if (dpy == EGL14.EglNoDisplay || !EGL14.EglInitialize(dpy, v, 0, v, 1)) throw new InvalidOperationException("EGL não inicializou");
            var attr = new[] { EGL14.EglRedSize, 8, EGL14.EglGreenSize, 8, EGL14.EglBlueSize, 8, EGL14.EglAlphaSize, 8,
                               EGL14.EglRenderableType, EGL14.EglOpenglEs2Bit, EGLExt.EglRecordableAndroid, 1, EGL14.EglNone };
            var cfgs = new EGLConfig?[1]; var n = new int[1];
            if (!EGL14.EglChooseConfig(dpy, attr, 0, cfgs, 0, 1, n, 0) || n[0] < 1 || cfgs[0] == null) throw new InvalidOperationException("nenhuma config EGL gravável");
            cfg = cfgs[0]!;
            ctx = EGL14.EglCreateContext(dpy, cfg, EGL14.EglNoContext, [EGL14.EglContextClientVersion, 2, EGL14.EglNone], 0);
            if (ctx == null || ctx == EGL14.EglNoContext) throw new InvalidOperationException("EglCreateContext falhou");
            surf = EGL14.EglCreateWindowSurface(dpy, cfg, _saida, [EGL14.EglNone], 0);
            if (surf == null || surf == EGL14.EglNoSurface) throw new InvalidOperationException("EglCreateWindowSurface falhou");
            if (!EGL14.EglMakeCurrent(dpy, surf, surf, ctx)) throw new InvalidOperationException("EglMakeCurrent falhou");

            prog = Programa();
            int aPos = GLES20.GlGetAttribLocation(prog, "aPos"), aTex = GLES20.GlGetAttribLocation(prog, "aTex");
            int uTex = GLES20.GlGetUniformLocation(prog, "uTex"), uImg = GLES20.GlGetUniformLocation(prog, "uImg");
            var ids = new int[1];
            GLES20.GlGenTextures(1, ids, 0); tex = ids[0];
            GLES20.GlBindTexture(GLES11Ext.GlTextureExternalOes, tex);
            GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureMinFilter, GLES20.GlLinear);
            GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureMagFilter, GLES20.GlLinear);
            GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureWrapS, GLES20.GlClampToEdge);
            GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureWrapT, GLES20.GlClampToEdge);

            _st = new SurfaceTexture(tex);
            _st.SetDefaultBufferSize(_w, _h);
            _st.SetOnFrameAvailableListener(new OnFrame(this), _avisos);
            _entradaVd = new Surface(_st);

            var pos = Buf([-1, -1, 1, -1, -1, 1, 1, 1]);
            var tc = Buf([0, 0, 1, 0, 0, 1, 1, 1]);
            var mat = new float[16];
            Android.Opengl.Matrix.SetIdentityM(mat, 0);
            _pronto.Set();

            long periodo = 1_000_000_000L / Math.Max(1, _fps);
            long proximo = Java.Lang.JavaSystem.NanoTime();
            bool temQuadro = false;
            int falhas = 0, semAviso = 0;
            while (!_parar)
            {
                // espera o tique (dorme quase tudo, o resto em espera ativa curta)
                long agora = Java.Lang.JavaSystem.NanoTime();
                long falta = proximo - agora;
                if (falta > 2_000_000) { Thread.Sleep((int)((falta - 1_500_000) / 1_000_000)); continue; }
                while (Java.Lang.JavaSystem.NanoTime() < proximo) Thread.Yield();
                long pts = proximo;
                proximo += periodo;
                if (Java.Lang.JavaSystem.NanoTime() - proximo > 2 * periodo) proximo = Java.Lang.JavaSystem.NanoTime() + periodo;   // ficou muito para trás: recomeça a grade

                // troca de saída (rotação)
                Surface? nova;
                lock (_gate) { nova = _saidaNova; _saidaNova = null; }
                if (nova != null)
                {
                    try
                    {
                        var s2 = EGL14.EglCreateWindowSurface(dpy, cfg, nova, [EGL14.EglNone], 0);
                        if (s2 == null || s2 == EGL14.EglNoSurface) throw new InvalidOperationException("EglCreateWindowSurface falhou");
                        if (!EGL14.EglMakeCurrent(dpy, s2, s2, ctx)) { EGL14.EglDestroySurface(dpy, s2); throw new InvalidOperationException("EglMakeCurrent falhou"); }
                        EGL14.EglDestroySurface(dpy, surf);
                        surf = s2; _saida = nova; _w = _wNovo; _h = _hNovo;
                        _st.SetDefaultBufferSize(_w, _h);
                        // não zerar _novoQuadro: um quadro já enfileirado sem aviso novo travaria o VirtualDisplay (a fila da SurfaceTexture enche)
                    }
                    catch (Exception e) { _erroTroca = e; }
                    _trocou.Set();
                }

                if (_novoQuadro || (++semAviso >= _fps / 4 && temQuadro))   // sem aviso há 250 ms: consome mesmo assim, para a fila nunca ficar presa
                {
                    _novoQuadro = false; semAviso = 0;
                    try { _st.UpdateTexImage(); _st.GetTransformMatrix(mat); temQuadro = true; } catch (Exception e) { Log.Info($"Captura: UpdateTexImage falhou: {e.Message}"); }
                }
                if (!temQuadro) continue;

                GLES20.GlViewport(0, 0, _w, _h);
                GLES20.GlUseProgram(prog);
                GLES20.GlActiveTexture(GLES20.GlTexture0);
                GLES20.GlBindTexture(GLES11Ext.GlTextureExternalOes, tex);
                GLES20.GlUniform1i(uImg, 0);
                GLES20.GlUniformMatrix4fv(uTex, 1, false, mat, 0);
                GLES20.GlEnableVertexAttribArray(aPos); GLES20.GlVertexAttribPointer(aPos, 2, GLES20.GlFloat, false, 0, pos);
                GLES20.GlEnableVertexAttribArray(aTex); GLES20.GlVertexAttribPointer(aTex, 2, GLES20.GlFloat, false, 0, tc);
                GLES20.GlDrawArrays(GLES20.GlTriangleStrip, 0, 4);
                EGLExt.EglPresentationTimeANDROID(dpy, surf, pts);
                if (EGL14.EglSwapBuffers(dpy, surf)) { falhas = 0; Interlocked.Increment(ref Quadros); }
                else if (++falhas >= 30) { _falhou("O encoder de vídeo não aceita mais quadros."); break; }
            }
        }
        catch (Exception e)
        {
            _erroInicio ??= e;
            if (!_pronto.IsSet) _pronto.Set();
            else if (!_parar) _falhou($"O OpenGL da captura parou: {e.Message}");
        }
        finally
        {
            // libera tudo aqui, na thread que criou
            try { _entradaVd?.Release(); } catch { }
            try { _st?.Release(); } catch { }
            try { if (tex != 0) GLES20.GlDeleteTextures(1, [tex], 0); } catch { }
            try { if (prog != 0) GLES20.GlDeleteProgram(prog); } catch { }
            try
            {
                if (dpy != null)
                {
                    EGL14.EglMakeCurrent(dpy, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);
                    if (surf != null) EGL14.EglDestroySurface(dpy, surf);
                    if (ctx != null) EGL14.EglDestroyContext(dpy, ctx);
                    EGL14.EglReleaseThread();
                    EGL14.EglTerminate(dpy);
                }
            }
            catch { }
            _trocou.Set();   // quem esperava uma troca não fica preso
        }
    }

    static FloatBuffer Buf(float[] d)
    {
        var b = ByteBuffer.AllocateDirect(d.Length * 4)!.Order(ByteOrder.NativeOrder())!.AsFloatBuffer()!;
        b.Put(d); b.Position(0);
        return b;
    }

    static int Programa()
    {
        int Compilar(int tipo, string src)
        {
            int s = GLES20.GlCreateShader(tipo);
            GLES20.GlShaderSource(s, src);
            GLES20.GlCompileShader(s);
            var ok = new int[1];
            GLES20.GlGetShaderiv(s, GLES20.GlCompileStatus, ok, 0);
            if (ok[0] == 0) throw new InvalidOperationException($"shader: {GLES20.GlGetShaderInfoLog(s)}");
            return s;
        }
        int p = GLES20.GlCreateProgram();
        GLES20.GlAttachShader(p, Compilar(GLES20.GlVertexShader, Vertice));
        GLES20.GlAttachShader(p, Compilar(GLES20.GlFragmentShader, Fragmento));
        GLES20.GlLinkProgram(p);
        var l = new int[1];
        GLES20.GlGetProgramiv(p, GLES20.GlLinkStatus, l, 0);
        if (l[0] == 0) throw new InvalidOperationException($"programa GL: {GLES20.GlGetProgramInfoLog(p)}");
        return p;
    }
}
