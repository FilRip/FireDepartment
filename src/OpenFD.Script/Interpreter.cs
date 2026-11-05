using System.Globalization;

namespace OpenFD.Script;

public enum VKind { Int, Float, Str }

/// <summary>Valeur du langage : entier (s32, EntityID, mcBool), flottant (f32) ou texte (mcString).</summary>
public readonly struct Value
{
    public readonly VKind Kind; public readonly double N; public readonly string? S;
    private Value(VKind k, double n, string? s) { Kind = k; N = n; S = s; }
    public static Value I(long v) => new(VKind.Int, v, null);
    public static Value F(double v) => new(VKind.Float, v, null);
    public static Value T(string v) => new(VKind.Str, 0, v);
    public static readonly Value Zero = I(0);
    public bool Truthy => Kind == VKind.Str ? !string.IsNullOrEmpty(S) : N != 0;
    public long AsInt => Kind == VKind.Str ? 0 : (long)Math.Truncate(N);
    public double AsNum => Kind == VKind.Str ? 0 : N;
    public string AsText => Kind switch
    {
        VKind.Str => S ?? "",
        VKind.Int => ((long)N).ToString(CultureInfo.InvariantCulture),
        _ => N.ToString("0.######", CultureInfo.InvariantCulture),
    };
    public override string ToString() => Kind == VKind.Str ? $"\"{S}\"" : AsText;

    public static Value Convert(string type, Value v) => type switch
    {
        "f32" or "float" => F(v.AsNum),
        "mcString" => T(v.AsText),
        "void" => v,
        _ => I(v.AsInt),
    };
}

/// <summary>Argument passé à une fonction du moteur ; certains sont modifiables (ENT_GetPosition(e, x, y)).</summary>
public sealed class Arg
{
    public Value Value;
    internal Action<Value>? Setter;
    public void Set(Value v) => Setter?.Invoke(v);
}

/// <summary>Le moteur de jeu vu par les scripts.</summary>
public interface IScriptHost
{
    double GameTime { get; }
    /// <summary>Renvoie null si la fonction n'est pas connue du moteur.</summary>
    Value? Call(string name, IReadOnlyList<Arg> args);
}

public class ScriptError(string message) : Exception(message);
/// <summary>Erreur qui doit arrêter toute la fonction (boucle infinie, récursion) : on ne passe pas à la ligne suivante.</summary>
public sealed class ScriptAbort(string message) : ScriptError(message);

public sealed class Interpreter
{
    private readonly Dictionary<string, Function> _functions = new(StringComparer.OrdinalIgnoreCase);
    public IScriptHost Host { get; }
    public Dictionary<string, Value> Globals { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> UnknownCalls { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Log { get; } = new();
    public int StepBudget = 2_000_000;

    private readonly PriorityQueue<(string fn, Value[] args), (double t, long seq)> _queue = new();
    private long _seq;
    private int _steps;

    public Interpreter(IScriptHost host) => Host = host;

    public IReadOnlyCollection<string> FunctionNames => _functions.Keys;
    public bool HasFunction(string name) => _functions.ContainsKey(name);

    public void Load(IEnumerable<Function> functions)
    {
        foreach (Function f in functions)
            if (f.Body.Body.Count > 0 || !_functions.ContainsKey(f.Name))
                _functions[f.Name] = f; // une définition remplace un prototype
    }

    // ---------- planificateur ----------
    public void Schedule(string fn, double delay, params Value[] args) => _queue.Enqueue((fn, args), (Host.GameTime + Math.Max(0, delay), _seq++));

    /// <summary>Exécute toutes les fonctions différées dont l'heure est passée.</summary>
    public void Update()
    {
        int guard = 0;
        while (_queue.TryPeek(out (string fn, Value[] args) item, out (double t, long seq) when) && when.t <= Host.GameTime + 1e-9 && guard++ < 10_000)
        {
            _queue.Dequeue();
            TryRun(item.fn, item.args);
        }
    }
    public int Pending => _queue.Count;

    private int _inMainLoop;

    /// <summary>Lance la boucle principale de la mission (MissionScript1/2), appelée régulièrement.</summary>
    public Value? RunMainLoop(string fn)
    {
        _inMainLoop++;
        try
        {
            return TryRun(fn);
        }
        finally
        {
            _inMainLoop--;
        }
    }

    public Value? TryRun(string fn, params Value[] args)
    {
        try { _steps = 0; return Invoke(fn, args, 0); }
        catch (ScriptError e) { Log.Add($"[{Host.GameTime:0.0}] {fn} : {e.Message}"); return null; }
    }

    // ---------- exécution ----------
    private sealed class Frame
    {
        public readonly Dictionary<string, (string type, Value v)> Vars = new(StringComparer.OrdinalIgnoreCase);
    }
    private sealed class ReturnSignal(Value v) : Exception { public Value V { get; } = v; }

    private Value Invoke(string name, Value[] args, int depth)
    {
        if (depth > 200)
            throw new ScriptAbort("récursion trop profonde");
        if (!_functions.TryGetValue(name, out Function? f))
            throw new ScriptError($"fonction de script inconnue « {name} »");
        Frame fr = new Frame();
        for (int i = 0; i < f.Params.Count; i++)
        {
            (string? ty, string? pn) = f.Params[i];
            fr.Vars[pn] = (ty, Value.Convert(ty, i < args.Length ? args[i] : Value.Zero));
        }
        string caller = _current;
        _current = f.Name;
        try
        {
            Exec(f.Body, fr, depth);
        }
        catch (ReturnSignal r)
        {
            return Value.Convert(f.ReturnType, r.V);
        }
        finally
        {
            _current = caller;
        }
        return Value.Zero;
    }

    private void Tick()
    {
        if (++_steps > StepBudget)
            throw new ScriptAbort("boucle trop longue (arrêtée)");
    }

    /// <summary>Fonction en cours d'exécution, pour situer les erreurs dans le journal.</summary>
    private string _current = "";

    /// <summary>Exécute une instruction d'un bloc ; en cas d'erreur, la note dans le journal et passe à l'instruction suivante,
    /// comme le moteur d'origine qui ne s'arrête pas sur une erreur d'exécution.</summary>
    private void ExecSafe(Stmt x, Frame fr, int depth)
    {
        try
        {
            Exec(x, fr, depth);
        }
        catch (ScriptAbort)
        {
            throw;
        }
        catch (ScriptError e)
        {
            Log.Add($"[{Host.GameTime:0.0}] {_current} : {e.Message}");
        }
        catch (Exception e) when (e is not ReturnSignal)
        {
            Log.Add($"[{Host.GameTime:0.0}] {_current} : erreur interne ({e.GetType().Name} : {e.Message})");
        }
    }

    private void Exec(Stmt s, Frame fr, int depth)
    {
        Tick();
        switch (s)
        {
            case Block b: foreach (Stmt x in b.Body) ExecSafe(x, fr, depth); break;
            case Decl d:
                foreach ((string? n, Expr? init) in d.Vars)
                    fr.Vars[n] = (d.Type, Value.Convert(d.Type, init == null ? (d.Type == "mcString" ? Value.T("") : Value.Zero) : Eval(init, fr, depth)));
                break;
            case ExprStmt e: Eval(e.E, fr, depth); break;
            case If i:
                if (Eval(i.Cond, fr, depth).Truthy)
                    Exec(i.Then, fr, depth);
                else if (i.Else != null)
                    Exec(i.Else, fr, depth);
                break;
            case For f:
                if (f.Init != null)
                    Exec(f.Init, fr, depth);
                while (f.Cond == null || Eval(f.Cond, fr, depth).Truthy)
                {
                    Exec(f.Body, fr, depth);
                    if (f.Step != null)
                        Eval(f.Step, fr, depth);
                    Tick();
                }
                break;
            case While w:
                while (Eval(w.Cond, fr, depth).Truthy) { Exec(w.Body, fr, depth); Tick(); }
                break;
            case Return r: throw new ReturnSignal(r.Value == null ? Value.Zero : Eval(r.Value, fr, depth));
            case Empty: break;
        }
    }

    private Value Eval(Expr e, Frame fr, int depth)
    {
        switch (e)
        {
            case Num n: return n.IsInt ? Value.I((long)n.Value) : Value.F(n.Value);
            case Str s: return Value.T(s.Value);
            case Var v:
                if (fr.Vars.TryGetValue(v.Name, out (string type, Value v) slot))
                    return slot.v;
                throw new ScriptError($"ligne {v.Line} : variable inconnue « {v.Name} »");
            case Cast c: return Value.Convert(c.Type, Eval(c.E, fr, depth));
            case Unary u:
            {
                Value x = Eval(u.E, fr, depth);
                return u.Op switch
                {
                    "-" => x.Kind == VKind.Int ? Value.I(-x.AsInt) : Value.F(-x.AsNum),
                    "!" => Value.I(x.Truthy ? 0 : 1),
                    _ => x,
                };
            }
            case Assign a:
            {
                if (!fr.Vars.TryGetValue(a.Name, out (string type, Value v) cur))
                    throw new ScriptError($"ligne {a.Line} : variable inconnue « {a.Name} »");
                Value rhs = Eval(a.Value, fr, depth);
                Value nv = a.Op == "=" ? rhs : Arith(a.Op[..1], cur.v, rhs);
                nv = Value.Convert(cur.type, nv);
                fr.Vars[a.Name] = (cur.type, nv);
                return nv;
            }
            case Binary b:
                if (b.Op == "&&")
                    return Value.I(Eval(b.L, fr, depth).Truthy && Eval(b.R, fr, depth).Truthy ? 1 : 0);
                if (b.Op == "||")
                    return Value.I(Eval(b.L, fr, depth).Truthy || Eval(b.R, fr, depth).Truthy ? 1 : 0);
                return Arith(b.Op, Eval(b.L, fr, depth), Eval(b.R, fr, depth));
            case Call c: return CallFn(c, fr, depth);
        }
        throw new ScriptError("expression non gérée");
    }

    private static Value Arith(string op, Value l, Value r)
    {
        if (op == "+" && (l.Kind == VKind.Str || r.Kind == VKind.Str))
            return Value.T(l.AsText + r.AsText);
        if (op is "==" or "!=")
        {
            bool eq = l.Kind == VKind.Str || r.Kind == VKind.Str ? string.Equals(l.AsText, r.AsText, StringComparison.Ordinal) : l.AsNum == r.AsNum;
            return Value.I(eq == (op == "==") ? 1 : 0);
        }
        bool ints = l.Kind == VKind.Int && r.Kind == VKind.Int;
        double a = l.AsNum, b = r.AsNum;
        switch (op)
        {
            case "+": return ints ? Value.I((long)(a + b)) : Value.F(a + b);
            case "-": return ints ? Value.I((long)(a - b)) : Value.F(a - b);
            case "*": return ints ? Value.I((long)(a * b)) : Value.F(a * b);
            case "/":
                if (b == 0)
                    return ints ? Value.Zero : Value.F(0);
                return ints ? Value.I((long)a / (long)b) : Value.F(a / b);
            case "%": return b == 0 ? Value.Zero : Value.I((long)a % (long)b);
            case "<": return Value.I(a < b ? 1 : 0);
            case ">": return Value.I(a > b ? 1 : 0);
            case "<=": return Value.I(a <= b ? 1 : 0);
            case ">=": return Value.I(a >= b ? 1 : 0);
        }
        throw new ScriptError("opérateur inconnu " + op);
    }

    private Value CallFn(Call c, Frame fr, int depth)
    {
        Tick();
        List<Arg> args = new List<Arg>(c.Args.Count);
        foreach (Expr ae in c.Args)
        {
            Arg a = new Arg { Value = Eval(ae, fr, depth) };
            if (ae is Var v && fr.Vars.ContainsKey(v.Name))
            {
                string name = v.Name;
                a.Setter = nv => { string t = fr.Vars[name].type; fr.Vars[name] = (t, Value.Convert(t, nv)); };
            }
            args.Add(a);
        }
        // fonctions de script de l'utilisateur d'abord
        if (_functions.ContainsKey(c.Name))
            return Invoke(c.Name, args.Select(a => a.Value).ToArray(), depth + 1);
        Value? builtin = Builtin(c.Name, args);
        if (builtin.HasValue)
            return builtin.Value;
        Value? r = Host.Call(c.Name, args);
        if (r.HasValue)
            return r.Value;
        UnknownCalls[c.Name] = UnknownCalls.TryGetValue(c.Name, out int n) ? n + 1 : 1;
        return Value.Zero;
    }

    // ---------- fonctions gérées par l'interpréteur lui-même ----------
    private Value? Builtin(string name, List<Arg> a)
    {
        string N(int i) => i < a.Count ? a[i].Value.AsText : "";
        Value V(int i) => i < a.Count ? a[i].Value : Value.Zero;
        switch (name.ToUpperInvariant())
        {
            case "SCR_SETVARS32": Globals[N(0)] = Value.I(V(1).AsInt); return Value.Zero;
            case "SCR_SETVARF32": Globals[N(0)] = Value.F(V(1).AsNum); return Value.Zero;
            case "SCR_SETVARSTRING": Globals[N(0)] = Value.T(V(1).AsText); return Value.Zero;
            case "SCR_GETVARS32": return Value.I(Globals.TryGetValue(N(0), out Value g1) ? g1.AsInt : 0);
            case "SCR_GETVARF32": return Value.F(Globals.TryGetValue(N(0), out Value g2) ? g2.AsNum : 0);
            case "SCR_GETVARSTRING": return Value.T(Globals.TryGetValue(N(0), out Value g3) ? g3.AsText : "");
            case "SCR_VAREXISTS": return Value.I(Globals.ContainsKey(N(0)) ? 1 : 0);
            case "SCR_RUNDELAYED":
                // la boucle principale de la mission est relancée sans cesse : tant qu'un appel identique qu'elle a demandé
                // attend encore, on ne le redemande pas (Sawmill : « add_obj_bridge » à 5 s, sinon cinématique et objectif × 11)
                if (_inMainLoop > 0 && _queue.UnorderedItems.Any(q => q.Element.args.Length == 0 && q.Element.fn.Equals(N(0), StringComparison.OrdinalIgnoreCase)))
                    return Value.Zero;
                Schedule(N(0), V(1).AsNum);
                return Value.Zero;
            case "SCR_RUNDELAYEDENTITY": Schedule(N(0), V(1).AsNum, V(2)); return Value.Zero;
            case "SCR_RUNDELAYEDARGS": Schedule(N(0), V(1).AsNum, V(2), V(3), V(4)); return Value.Zero;
            case "SCR_RUNDELAYEDMULTIARGS": Schedule(N(0), V(1).AsNum, a.Skip(2).Select(x => x.Value).ToArray()); return Value.Zero;
            case "SCR_ISRUNNING": return Value.I(_queue.UnorderedItems.Any(q => q.Element.fn.Equals(N(0), StringComparison.OrdinalIgnoreCase)) ? 1 : 0);
            case "UTL_GETGAMETIME": return Value.F(Host.GameTime);
            case "COS": return Value.F(Math.Cos(V(0).AsNum));
            case "SIN": return Value.F(Math.Sin(V(0).AsNum));
            case "SQRT": return Value.F(Math.Sqrt(V(0).AsNum));
        }
        return null;
    }
}
