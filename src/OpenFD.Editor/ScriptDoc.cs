namespace OpenFD.MG;

/// <summary>
/// Un fichier .scp ouvert dans la fenêtre des scripts : ses lignes, le curseur (ligne, colonne), la sélection (de l'ancre
/// au curseur), l'historique pour annuler (Ctrl+Z) et l'enregistrement sur le disque (Latin-1, comme le jeu).
/// </summary>
public sealed class ScriptDoc
{
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
    public List<string> Lines { get; private set; } = new() { "" };
    public int Line, Col;
    public int AnchorLine = -1, AnchorCol;
    public float Scroll;          // première ligne visible (peut être fractionnaire)
    public float ScrollX;         // décalage horizontal (colonnes)
    public bool Dirty;
    public double EditedAt;
    private readonly List<(string text, int line, int col)> _undo = new();
    private double _lastUndoAt = -10;

    public string Text => string.Join("\r\n", Lines);

    public static ScriptDoc Load(string path)
    {
        ScriptDoc d = new ScriptDoc { Path = path };
        if (File.Exists(path))
            d.SetText(File.ReadAllText(path, System.Text.Encoding.Latin1));
        return d;
    }

    public void SetText(string text)
    {
        Lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        if (Lines.Count == 0)
            Lines.Add("");
        Line = Math.Clamp(Line, 0, Lines.Count - 1);
        Col = Math.Clamp(Col, 0, Lines[Line].Length);
        AnchorLine = -1;
    }

    public void Save()
    {
        File.WriteAllText(Path, Text, System.Text.Encoding.Latin1);
        Dirty = false;
    }

    public bool HasSelection => AnchorLine >= 0 && (AnchorLine != Line || AnchorCol != Col);

    /// <summary>Début et fin de la sélection, dans l'ordre.</summary>
    public ((int line, int col) a, (int line, int col) b) Selection
    {
        get
        {
            (int, int) p = (AnchorLine, AnchorCol), q = (Line, Col);
            return Before(p, q) ? (p, q) : (q, p);
        }
    }

    private static bool Before((int line, int col) p, (int line, int col) q) => p.line < q.line || (p.line == q.line && p.col <= q.col);

    public string SelectedText
    {
        get
        {
            if (!HasSelection)
                return "";
            ((int l0, int c0), (int l1, int c1)) = Selection;
            if (l0 == l1)
                return Lines[l0][c0..c1];
            List<string> parts = new List<string> { Lines[l0][c0..] };
            for (int i = l0 + 1; i < l1; i++)
                parts.Add(Lines[i]);
            parts.Add(Lines[l1][..c1]);
            return string.Join("\r\n", parts);
        }
    }

    /// <summary>État gardé pour Ctrl+Z (les frappes rapprochées ne font qu'un seul pas).</summary>
    private void Snapshot(double now, bool group)
    {
        if (group && now - _lastUndoAt < 1.2 && _undo.Count > 0)
        {
            _lastUndoAt = now;
            return;
        }
        _undo.Add((Text, Line, Col));
        if (_undo.Count > 200)
            _undo.RemoveAt(0);
        _lastUndoAt = now;
    }

    private void Changed(double now)
    {
        Dirty = true;
        EditedAt = now;
    }

    public void Undo(double now)
    {
        if (_undo.Count == 0)
            return;
        (string text, int line, int col) u = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        SetText(u.text);
        Line = Math.Clamp(u.line, 0, Lines.Count - 1);
        Col = Math.Clamp(u.col, 0, Lines[Line].Length);
        _lastUndoAt = -10;
        Changed(now);
    }

    private void DeleteSelectionRaw()
    {
        ((int l0, int c0), (int l1, int c1)) = Selection;
        string head = Lines[l0][..c0], tail = Lines[l1][c1..];
        Lines.RemoveRange(l0, l1 - l0 + 1);
        Lines.Insert(l0, head + tail);
        Line = l0;
        Col = c0;
        AnchorLine = -1;
    }

    public void DeleteSelection(double now)
    {
        if (!HasSelection)
            return;
        Snapshot(now, false);
        DeleteSelectionRaw();
        Changed(now);
    }

    /// <summary>Texte tapé ou collé (plusieurs lignes possibles), à la place de la sélection.</summary>
    public void Insert(string s, double now)
    {
        Snapshot(now, s.Length == 1 && s != "\n");
        if (HasSelection)
            DeleteSelectionRaw();
        AnchorLine = -1;
        string[] parts = s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        string head = Lines[Line][..Col], tail = Lines[Line][Col..];
        if (parts.Length == 1)
        {
            Lines[Line] = head + parts[0] + tail;
            Col += parts[0].Length;
        }
        else
        {
            Lines[Line] = head + parts[0];
            for (int i = 1; i < parts.Length; i++)
                Lines.Insert(Line + i, parts[i]);
            Line += parts.Length - 1;
            Col = Lines[Line].Length;
            Lines[Line] += tail;
        }
        Changed(now);
    }

    /// <summary>Entrée : nouvelle ligne, avec le même retrait que la ligne d'avant (un cran de plus après « { »).</summary>
    public void NewLine(double now)
    {
        string cur = Lines[Line];
        string indent = new string(cur.TakeWhile(ch => ch is ' ' or '\t').ToArray());
        if (Col > 0 && cur[..Col].TrimEnd().EndsWith('{'))
            indent += "\t";
        Insert("\n" + indent, now);
    }

    public void Backspace(double now)
    {
        if (HasSelection)
        {
            DeleteSelection(now);
            return;
        }
        if (Col == 0 && Line == 0)
            return;
        Snapshot(now, true);
        if (Col > 0)
        {
            Lines[Line] = Lines[Line].Remove(Col - 1, 1);
            Col--;
        }
        else
        {
            Col = Lines[Line - 1].Length;
            Lines[Line - 1] += Lines[Line];
            Lines.RemoveAt(Line);
            Line--;
        }
        Changed(now);
    }

    public void Delete(double now)
    {
        if (HasSelection)
        {
            DeleteSelection(now);
            return;
        }
        if (Col < Lines[Line].Length)
        {
            Snapshot(now, true);
            Lines[Line] = Lines[Line].Remove(Col, 1);
            Changed(now);
        }
        else if (Line + 1 < Lines.Count)
        {
            Snapshot(now, true);
            Lines[Line] += Lines[Line + 1];
            Lines.RemoveAt(Line + 1);
            Changed(now);
        }
    }

    /// <summary>Déplace le curseur ; avec <paramref name="select"/>, la sélection suit (Maj).</summary>
    public void MoveTo(int line, int col, bool select)
    {
        if (select && AnchorLine < 0)
        {
            AnchorLine = Line;
            AnchorCol = Col;
        }
        else if (!select)
            AnchorLine = -1;
        Line = Math.Clamp(line, 0, Lines.Count - 1);
        Col = Math.Clamp(col, 0, Lines[Line].Length);
    }

    public void Left(bool select, bool word)
    {
        if (!select && HasSelection)
        {
            ((int l, int c), _) = Selection;
            MoveTo(l, c, false);
            return;
        }
        if (Col > 0)
        {
            int c = Col - 1;
            if (word)
                while (c > 0 && char.IsLetterOrDigit(Lines[Line][c - 1]))
                    c--;
            MoveTo(Line, c, select);
        }
        else if (Line > 0)
            MoveTo(Line - 1, Lines[Line - 1].Length, select);
    }

    public void Right(bool select, bool word)
    {
        if (!select && HasSelection)
        {
            (_, (int l, int c)) = Selection;
            MoveTo(l, c, false);
            return;
        }
        if (Col < Lines[Line].Length)
        {
            int c = Col + 1;
            if (word)
                while (c < Lines[Line].Length && char.IsLetterOrDigit(Lines[Line][c]))
                    c++;
            MoveTo(Line, c, select);
        }
        else if (Line + 1 < Lines.Count)
            MoveTo(Line + 1, 0, select);
    }

    /// <summary>Début de ligne : d'abord après le retrait, puis tout au début.</summary>
    public void Home(bool select)
    {
        int indent = Lines[Line].TakeWhile(ch => ch is ' ' or '\t').Count();
        MoveTo(Line, Col == indent ? 0 : indent, select);
    }

    public void SelectAll()
    {
        AnchorLine = 0;
        AnchorCol = 0;
        Line = Lines.Count - 1;
        Col = Lines[Line].Length;
    }
}
