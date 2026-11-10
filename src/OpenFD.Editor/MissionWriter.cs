using OpenFD.Formats;
using OpenFD.Game;

namespace OpenFD.MG;

/// <summary>
/// Sauvegarde d'une mission par l'éditeur, dans le dossier Data du jeu (comme la mission Vegas) :
///  • Design\Mission\&lt;nom&gt;\ : &lt;nom&gt;.level, &lt;nom&gt;.grid, mission_&lt;nom&gt;.mis (toujours en fichiers à part) ;
///  • Script\Mission\&lt;nom&gt;\*.scp ;
///  • graphismes et textures (si le nom n'est pas celui de la mission d'origine) : Gfx\Mission\&lt;nom&gt;.pak et
///    &lt;nom&gt;_tex_high / _med / _low.bank, ou bien des fichiers à part (Gfx\Mission\&lt;nom&gt;\, Gfx\Textures\&lt;nom&gt;\).
/// Un fichier remplacé est d'abord gardé en « .bak » (une seule fois : la première version reste).
/// </summary>
public sealed class MissionWriter
{
    public required GameFiles Files { get; init; }
    public required SceneAssets Assets { get; init; }
    /// <summary>Mission ouverte dans l'éditeur : ses graphismes et ses textures sont repris.</summary>
    public required string BaseMission { get; init; }
    /// <summary>Nom court de la mission (dossiers et fichiers).</summary>
    public required string Name { get; init; }
    public required Level Level { get; init; }
    public byte[]? Grid { get; init; }
    public required string MisText { get; init; }
    public required IReadOnlyDictionary<string, string> Scripts { get; init; }
    /// <summary>Archives .pak / .bank (sinon des fichiers à part dans Data).</summary>
    public bool Packed { get; init; }
    /// <summary>Textures des objets posés (noms des matériaux) : recopiées si elles viennent d'une autre mission.</summary>
    public IReadOnlyCollection<string> ObjectTextures { get; init; } = Array.Empty<string>();
    /// <summary>Classes propres à l'éditeur (bloqueurs, masques) : chemin dans Data, texte.</summary>
    public IReadOnlyDictionary<string, string> ExtraClasses { get; init; } = new Dictionary<string, string>();
    /// <summary>Autres fichiers propres à l'éditeur (modèles des masques de toit : Gfx\Placeable\ROOF\openfd_mask_…), chemin dans Data → contenu.</summary>
    public IReadOnlyDictionary<string, byte[]> ExtraFiles { get; init; } = new Dictionary<string, byte[]>();
    /// <summary>Mission créée par l'éditeur (sans décor) : pas de graphismes ni de textures à recopier.</summary>
    public bool NewMission { get; init; }
    public List<string> Report { get; } = new();

    private string Data => Files.DataDir;
    private bool SameAsBase => Name.Equals(BaseMission, StringComparison.OrdinalIgnoreCase);

    /// <summary>Fichiers que <see cref="Write"/> remplacerait (ils existent déjà dans Data).</summary>
    public List<string> ExistingFiles()
    {
        List<string> files = new List<string>();
        string design = Path.Combine(Data, "Design", "Mission", Name);
        files.Add(Path.Combine(design, Name + ".level"));
        if (Grid != null)
            files.Add(Path.Combine(design, Name + ".grid"));
        files.Add(Path.Combine(design, "mission_" + Name + ".mis"));
        string scripts = Path.Combine(Data, "Script", "Mission", Name);
        foreach (string file in Scripts.Keys)
            files.Add(Path.Combine(scripts, file));
        if (Directory.Exists(scripts))
            files.AddRange(Directory.EnumerateFiles(scripts, "*.scp").Where(f => !Scripts.ContainsKey(Path.GetFileName(f))));
        List<string> res = files.Where(File.Exists).ToList();
        if (!SameAsBase && !NewMission)
        {
            string mdir = Path.Combine(Data, "Gfx", "Mission");
            List<string> gfx = new List<string>();
            if (Packed)
            {
                gfx.Add(Path.Combine(mdir, Name + ".pak"));
                foreach (string q in new[] { "high", "med", "low" })
                    gfx.Add(Path.Combine(mdir, $"{Name}_tex_{q}.bank"));
                res.AddRange(gfx.Where(File.Exists));
            }
            else
            {
                foreach (string dir in new[] { Path.Combine(mdir, Name), Path.Combine(Data, "Gfx", "Textures", Name) })
                    if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
                        res.Add(dir + Path.DirectorySeparatorChar);
            }
        }
        return res.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void Write()
    {
        string design = Path.Combine(Data, "Design", "Mission", Name);
        Directory.CreateDirectory(design);
        LevelEdit.SetMissionPath(Level, $".\\Data\\Design\\Mission\\{Name}\\mission_{Name}.mis");
        Save(Path.Combine(design, Name + ".level"), Level.Serialize());
        if (Grid != null)
            Save(Path.Combine(design, Name + ".grid"), Grid);
        Save(Path.Combine(design, "mission_" + Name + ".mis"), System.Text.Encoding.Latin1.GetBytes(MisText));
        Report.Add(EditorText.T("WriteDesign", "Design\\Mission\\{0} : niveau, grille, .mis", Name));

        string scripts = Path.Combine(Data, "Script", "Mission", Name);
        Directory.CreateDirectory(scripts);
        foreach (string old in Directory.EnumerateFiles(scripts, "*.scp"))
            if (!Scripts.ContainsKey(Path.GetFileName(old)))
            {
                Backup(old);
                File.Delete(old);
            }
        foreach ((string file, string text) in Scripts)
            Save(Path.Combine(scripts, file), System.Text.Encoding.Latin1.GetBytes(text));
        Report.Add(EditorText.T("WriteScripts", "Script\\Mission\\{0} : {1} script(s)", Name, Scripts.Count));

        foreach ((string rel, string text) in ExtraClasses)
        {
            string path = Path.Combine(Data, rel.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path) || File.ReadAllText(path) != text)
                File.WriteAllText(path, text, System.Text.Encoding.Latin1);
        }
        foreach ((string rel, byte[] bytes) in ExtraFiles)
        {
            string path = Path.Combine(Data, rel.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                File.WriteAllBytes(path, bytes);
        }

        if (NewMission)
            Report.Add(EditorText.T("WriteTerrain", "Sol tiré de la grille (pas de décor)"));
        else if (SameAsBase)
            Report.Add(EditorText.T("WriteSameGfx", "Graphismes et textures : ceux de la mission d'origine (même nom), pas recopiés"));
        else
        {
            WriteGfx();
            WriteTextures();
        }
    }

    // ---- graphismes du décor : tout le dossier Gfx\Mission\<origine>, les .sg renommés pour la nouvelle mission ----
    private void WriteGfx()
    {
        string dir;
        try
        {
            dir = Assets.GfxDir(BaseMission);
        }
        catch (Exception)
        {
            Report.Add(EditorText.T("WriteNoGfx", "Graphismes de {0} introuvables : pas recopiés", BaseMission));
            return;
        }
        string prefix = $"Gfx\\Mission\\{dir}\\";
        List<string> list = Files.List(prefix).ToList();
        string mdir = Path.Combine(Data, "Gfx", "Mission");
        PakWriter? pak = null;
        if (Packed)
        {
            string path = Path.Combine(mdir, Name + ".pak");
            Backup(path);
            pak = new PakWriter(path);
        }
        try
        {
            foreach (string rel in list)
            {
                string inner = RenameScene(rel[prefix.Length..], dir);
                byte[] data = Files.Read(rel);
                if (pak != null)
                    pak.Add(inner, data);
                else
                    Save(Path.Combine(mdir, Name, inner.Replace('\\', Path.DirectorySeparatorChar)), data);
            }
        }
        finally
        {
            pak?.Dispose();
        }
        Report.Add(Packed ? EditorText.T("WritePak", "Gfx\\Mission\\{0}.pak : {1} fichiers", Name, list.Count) : EditorText.T("WriteGfxDir", "Gfx\\Mission\\{0}\\ : {1} fichiers", Name, list.Count));
    }

    /// <summary>Décor « London.sg », sol « FGD_London.sg », « level_institute.sg » : renommés pour la nouvelle mission.</summary>
    private string RenameScene(string inner, string dir)
    {
        if (inner.Contains('\\'))
            return inner;
        foreach (string b in new[] { BaseMission, dir })
        {
            if (inner.Equals(b + ".sg", StringComparison.OrdinalIgnoreCase))
                return Name + ".sg";
            if (inner.Equals("FGD_" + b + ".sg", StringComparison.OrdinalIgnoreCase))
                return "FGD_" + Name + ".sg";
            if (inner.Equals("level_" + b + ".sg", StringComparison.OrdinalIgnoreCase))
                return "level_" + Name + ".sg";
        }
        return inner;
    }

    /// <summary>Éclairage précalculé : « FLM_London_Bank_ROOM9_0 » → « FLM_&lt;nom&gt;_Bank_ROOM9_0 ».</summary>
    private string RenameTexture(string entry)
    {
        string flm = "FLM_" + BaseMission + "_";
        return entry.StartsWith(flm, StringComparison.OrdinalIgnoreCase) ? "FLM_" + Name + "_" + entry[flm.Length..] : entry;
    }

    // ---- textures : celles de la mission d'origine, plus celles des objets venus d'autres missions ----
    private void WriteTextures()
    {
        string mdir = Path.Combine(Data, "Gfx", "Mission");
        // textures des objets prises dans les banques d'autres missions (chemin Gfx\Textures\<mission>\<entrée>)
        List<string> extra = new List<string>();
        foreach (string t in ObjectTextures)
            if (Assets.ResolveTex(BaseMission, t) is { } p && p.Split('\\') is { Length: > 3 } parts
                && !parts[2].Equals(BaseMission, StringComparison.OrdinalIgnoreCase) && !parts[2].StartsWith('_'))
                extra.Add(p);
        extra = extra.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string? high = BankPath(mdir, "high");
        foreach (string q in Packed ? new[] { "high", "med", "low" } : new[] { "high" })
        {
            string? src = BankPath(mdir, q) ?? high;
            List<(string name, Func<byte[]> read)> entries = new List<(string name, Func<byte[]> read)>();
            if (src != null)
            {
                Pak bank = new Pak(src);
                foreach (Pak.Entry e in bank.Entries)
                    entries.Add((e.Name, () => bank.Read(e)));
            }
            else
            {
                // textures à part (Gfx\Textures\<mission>\…)
                string prefix = $"Gfx\\Textures\\{BaseMission}\\";
                foreach (string rel in Files.List(prefix))
                    entries.Add((rel[prefix.Length..], () => Files.Read(rel)));
            }
            foreach (string p in extra)
                entries.Add((p[(p.LastIndexOf('\\') + 1)..], () => Files.Read(p)));
            int n = 0;
            if (Packed)
            {
                string path = Path.Combine(mdir, $"{Name}_tex_{q}.bank");
                Backup(path);
                using PakWriter w = new PakWriter(path);
                foreach ((string name, Func<byte[]> read) in entries)
                    w.Add(RenameTexture(name), read());
                n = w.Count;
            }
            else
            {
                string dir = Path.Combine(Data, "Gfx", "Textures", Name);
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach ((string name, Func<byte[]> read) in entries)
                {
                    string leaf = RenameTexture(name);
                    if (seen.Add(leaf))
                        Save(Path.Combine(dir, leaf), read());
                }
                n = seen.Count;
            }
            Report.Add(Packed ? EditorText.T("WriteBank", "Gfx\\Mission\\{0}_tex_{1}.bank : {2} textures", Name, q, n) : EditorText.T("WriteTexDir", "Gfx\\Textures\\{0}\\ : {1} textures", Name, n));
        }
        if (extra.Count > 0)
            Report.Add(EditorText.T("WriteExtraTex", "{0} texture(s) d'objets prise(s) dans d'autres missions", extra.Count));
    }

    private string? BankPath(string mdir, string quality)
    {
        if (!Directory.Exists(mdir))
            return null;
        return Directory.EnumerateFiles(mdir, "*.bank").FirstOrDefault(f => Path.GetFileName(f).Equals($"{BaseMission}_tex_{quality}.bank", StringComparison.OrdinalIgnoreCase));
    }

    private static void Backup(string path)
    {
        string bak = path + ".bak";
        if (File.Exists(path) && !File.Exists(bak))
            File.Copy(path, bak);
    }

    private static void Save(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Backup(path);
        File.WriteAllBytes(path, data);
    }
}
