using System.Text;

namespace OpenFD.MG;

/// <summary>
/// Journal du programme sur le disque : tout ce qu'il écrit (console et journal de la partie) est recopié, ligne par ligne
/// et tout de suite, dans OpenFD.log (à côté de OpenFD.exe ; celui du lancement précédent devient OpenFD.prev.log). Après
/// un plantage brutal (erreur interne de .NET, sans message), ses dernières lignes disent ce qui se passait. Une exception
/// non gérée écrit en plus un rapport crash_AAAAMMJJ_HHMMSS.txt : l'exception, puis les dernières lignes du journal.
/// </summary>
public static class CrashLog
{
    private const int Keep = 400;
    private static readonly object Gate = new object();
    private static readonly Queue<string> Last = new Queue<string>();
    private static StreamWriter? _file;
    private static string _dir = "";
    private static Exception? _reported;   // déjà rapportée (relancée par Program, puis vue comme non gérée)

    /// <summary>Ouvre le journal et prend en charge les exceptions non gérées ; sans effet si le dossier est en lecture seule.</summary>
    public static void Start()
    {
        _dir = AppContext.BaseDirectory;
        try
        {
            string path = Path.Combine(_dir, "OpenFD.log");
            string prev = Path.Combine(_dir, "OpenFD.prev.log");
            if (File.Exists(path))
                File.Copy(path, prev, true);
            _file = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
            Write($"OpenFD — démarrage {DateTime.Now:yyyy-MM-dd HH:mm:ss}, .NET {Environment.Version}, {Environment.OSVersion}, {(Environment.Is64BitProcess ? "64" : "32")} bits");
        }
        catch (Exception e)
        {
            Console.WriteLine("Journal OpenFD.log : " + e.Message);
            _file = null;
        }
        Console.SetOut(TextWriter.Synchronized(new Tee(Console.Out)));
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => Report(ev.ExceptionObject as Exception, "exception non gérée");
        TaskScheduler.UnobservedTaskException += (_, ev) => Report(ev.Exception, "exception d'une tâche");
    }

    /// <summary>Une ligne du journal de la partie (renfort, vidéo, ordre…), seulement dans le fichier.</summary>
    public static void Note(string line) => Write(line);

    /// <summary>Rapport de plantage : crash_….txt à côté du journal (et la fin du journal).</summary>
    public static void Report(Exception? e, string what)
    {
        if (e != null && ReferenceEquals(e, _reported))
            return;
        _reported = e;
        try
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"OpenFD — plantage ({what}) le {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($".NET {Environment.Version}, {Environment.OSVersion}");
            sb.AppendLine();
            sb.AppendLine(e?.ToString() ?? "(exception inconnue)");
            sb.AppendLine();
            sb.AppendLine($"---- dernières lignes du journal ----");
            lock (Gate)
                foreach (string l in Last)
                    sb.AppendLine(l);
            string path = Path.Combine(_dir, $"crash_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            Write($"PLANTAGE ({what}) : {e?.GetType().Name} : {e?.Message} — rapport : {path}");
        }
        catch { }
    }

    private static void Write(string line)
    {
        string stamped = $"{DateTime.Now:HH:mm:ss.fff} {line}";
        lock (Gate)
        {
            Last.Enqueue(stamped);
            while (Last.Count > Keep)
                Last.Dequeue();
            try
            {
                _file?.WriteLine(stamped);
            }
            catch { }
        }
    }

    // la console d'origine, et chaque ligne complète dans le journal
    private sealed class Tee : TextWriter
    {
        private readonly TextWriter _out;
        private readonly StringBuilder _line = new StringBuilder();

        public Tee(TextWriter output)
        {
            _out = output;
        }

        public override Encoding Encoding => _out.Encoding;

        public override void Write(char value)
        {
            _out.Write(value);
            if (value == '\n')
            {
                CrashLog.Write(_line.ToString().TrimEnd('\r'));
                _line.Clear();
            }
            else
                _line.Append(value);
        }

        public override void Write(string? value)
        {
            if (value == null)
                return;
            foreach (char c in value)
                Write(c);
        }

        public override void WriteLine(string? value)
        {
            Write(value);
            Write('\n');
        }

        public override void Flush() => _out.Flush();
    }
}
