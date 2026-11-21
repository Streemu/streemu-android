namespace Streemu.Util;

/// <summary>Log simples em arquivo (%LOCALAPPDATA%\Streemu\logs) e em memória para a tela e o relatório.</summary>
public static class Log
{
    static readonly object Gate = new();
    static StreamWriter? _writer;
    static readonly LinkedList<string> Recent = new();

    public static string FilePath { get; private set; } = "";
    /// <summary>Por que o arquivo de log não está sendo gravado (nulo = gravando). O indicador do diagnóstico fica vermelho com isso.</summary>
    public static string? FalhaGravacao { get; private set; }
    public static string Folder { get; private set; } = "";
    public static event Action<string>? Line;
    /// <summary>
    /// Filtro opcional aplicado à mensagem antes de gravar e de disparar <see cref="Line"/> (plano 019, etapa 7): o <c>MainForm</c> instala um que troca o apelido
    /// local de um amigo pelo nome real, para o apelido que só a pessoa vê nunca ir ao arquivo, ao relatório nem à telemetria. Nulo (sem apelidos) = não filtra.
    /// </summary>
    public static Func<string, string>? Filtro { get; set; }

    public static void Init()
    {
        Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Streemu", "logs");
        Directory.CreateDirectory(Folder);
        FilePath = Path.Combine(Folder, $"sessao-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
        try { _writer = new StreamWriter(FilePath, append: true, System.Text.Encoding.UTF8) { AutoFlush = true }; }
        catch (Exception e) { _writer = null; FalhaGravacao = e.Message; }
        // as linhas só têm a hora local: a data e o fuso ficam no começo, para alinhar PCs diferentes (spec 009)
        var agora = DateTimeOffset.Now;
        Info($"Log de {agora:yyyy-MM-dd}, fuso UTC{agora:zzz} (agora são {agora.UtcDateTime:HH:mm:ss} UTC).");
    }

    public static void Info(string message)
    {
        if (Filtro is { } filtro) { try { message = filtro(message); } catch { message = "(linha omitida: o filtro do log falhou)"; } }
        string line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        lock (Gate)
        {
            // disco cheio ou arquivo bloqueado não pode derrubar quem chamou o log (era exceção solta na thread que logava)
            try { _writer?.WriteLine(line); if (_writer != null) FalhaGravacao = null; }
            catch (Exception e) { FalhaGravacao = e.Message; }
            Recent.AddLast(line);
            while (Recent.Count > 1500) Recent.RemoveFirst();
        }
        Line?.Invoke(line);
    }

    public static string[] Tail(int count)
    {
        lock (Gate) return Recent.Skip(Math.Max(0, Recent.Count - count)).ToArray();
    }

    /// <summary>
    /// Últimas linhas para o relatório, com no máximo <paramref name="childBudget"/> linhas de ffmpeg/ffplay:
    /// os eventos do app (conexão, transmissão, assistir) ficam mesmo que um processo filho tenha gritado
    /// (no teste 0.1.8 um ffplay do grupo tomou o log inteiro e a linha do tempo se perdeu).
    /// </summary>
    public static string[] TailForReport(int count, int childBudget)
    {
        var picked = new List<string>();
        int child = 0, skipped = 0;
        lock (Gate)
        {
            for (var n = Recent.Last; n != null && picked.Count < count; n = n.Previous)
            {
                bool isChild = IsChildLine(n.Value);
                if (isChild && child >= childBudget) { skipped++; continue; }
                if (isChild) child++;
                picked.Add(n.Value);
            }
        }
        picked.Reverse();
        if (skipped > 0) picked.Insert(0, $"({skipped} linhas de ffmpeg/ffplay omitidas no relatório; o arquivo de log tem tudo)");
        return picked.ToArray();
    }

    static bool IsChildLine(string line)
    {
        // "HH:mm:ss.fff [ffmpeg] ..." (a linha do comando "[ffmpeg] ffmpeg.exe ..." conta como evento do app)
        int i = line.IndexOf(' ');
        if (i < 0 || i + 1 >= line.Length || line[i + 1] != '[') return false;
        int j = line.IndexOf(']', i);
        if (j < 0) return false;
        string tag = line[(i + 2)..j];
        if (tag is not ("ffmpeg" or "ffmpeg-srt" or "ffplay" or "sonda" or "teste-encoder" or "teste-captura" or "teste-ouvinte")) return false;
        return !line.AsSpan(j + 1).TrimStart().StartsWith(tag + ".exe");
    }
}
