namespace OpenFD.ConvFD1;

/// <summary>Fichiers du jeu FD1 installé : maillages (.SKIN/.SKEL) et images, retrouvés par leur nom (sans tenir compte de la casse).</summary>
public sealed class Fd1Data
{
    public string Root { get; }
    private readonly Dictionary<string, List<string>> _skins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _images = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] ImageExt = { ".bmp", ".tga", ".dds", ".jpg", ".png" };

    public Fd1Data(string root)
    {
        Root = root;
        foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext == ".skin")
                Add(_skins, Path.GetFileNameWithoutExtension(f), f);
            else if (ImageExt.Contains(ext))
                Add(_images, Path.GetFileName(f), f);
        }
    }

    private static void Add(Dictionary<string, List<string>> d, string key, string path)
    {
        if (!d.TryGetValue(key, out List<string>? l))
        {
            l = new List<string>();
            d[key] = l;
        }
        l.Add(path);
    }

    /// <summary>Fichier .SKIN d'un modèle ; s'il y en a plusieurs, celui du dossier de la mission d'abord.</summary>
    public string? Skin(string model, string mission)
    {
        if (!_skins.TryGetValue(model, out List<string>? l))
            return null;
        string sep = Path.DirectorySeparatorChar.ToString();
        return l.FirstOrDefault(p => p.Contains(sep + mission + sep, StringComparison.OrdinalIgnoreCase))
            ?? l.FirstOrDefault(p => p.Contains(sep + "new_" + mission + sep, StringComparison.OrdinalIgnoreCase))
            ?? l[0];
    }

    /// <summary>Image d'une texture : le nom exact, sinon le même nom avec une autre extension (BMP, TGA, puis DDS, JPG, PNG).</summary>
    public string? Image(string name)
    {
        string file = Path.GetFileName(name.Replace('\\', '/'));
        if (_images.TryGetValue(file, out List<string>? l))
            return l[0];
        string b = Path.GetFileNameWithoutExtension(file);
        foreach (string ext in ImageExt)
            if (_images.TryGetValue(b + ext, out List<string>? l2))
                return l2[0];
        return null;
    }
}
