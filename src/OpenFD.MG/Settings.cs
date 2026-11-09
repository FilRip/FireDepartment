using System.Text.Json;
using OpenFD.Game;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SN = System.Numerics;

namespace OpenFD.MG;

/// <summary>Réglages du joueur, enregistrés dans options.json (dossier de l'utilisateur : %APPDATA%\FD3_CSharp).</summary>
public sealed class Settings
{
    public bool InvertX { get; set; }
    public bool InvertY { get; set; }
    public bool EdgeScroll { get; set; }            // souris au bord de l'écran : la caméra se déplace (comme les flèches)
    public int Volume { get; set; } = 100;          // % (volume général)
    public int VolumeVoice { get; set; } = 100;     // % interface et voix (VOCAL_…)
    public int VolumeEffects { get; set; } = 80;    // % effets (tout le reste)
    public int VolumeMusic { get; set; } = 50;      // % ambiance (musiques)
    public int Brightness { get; set; } = 100;      // % (100 : comme le jeu d'origine)
    public bool BrightnessNeutral { get; set; }     // réglage déjà passé de l'ancienne valeur par défaut (150 %) à 100 %
    public string Resolution { get; set; } = "";    // « 1920x1080 » ; vide : fenêtre à la taille de l'écran
    public bool Fullscreen { get; set; }
    /// <summary>Réglages du jeu sans place dans le menu : modifiables à la main dans ce fichier.</summary>
    public GameplaySettings Gameplay { get; set; } = new();
    public string Textures { get; set; } = "high";  // high, med, low
    public string Language { get; set; } = "";      // vide : celle des voix installées ; sinon FRA, ENG…
    public string Nationality { get; set; } = "";   // vide : celle de la mission ; sinon us, fr, gb, gr, ua
    public string PlayerName { get; set; } = "";    // pseudo en multijoueur
    public string LastMission { get; set; } = "";   // dernière mission jouée : page affichée en ouvrant la campagne
    public string LastDifficulty { get; set; } = "";   // sa difficulté (EASY, NORMAL, HARD) : choisie d'office dans les menus
    public bool Reflections { get; set; } = true;   // reflets des vitres et carrosseries (réglage caché : seulement dans le fichier)

    /// <summary>Dossier « Save » du jeu (à côté de Data) : options.json y est enregistré.</summary>
    public static string SaveDir = "";
    private static string PathOf() => System.IO.Path.Combine(SaveDir, "FD3_CSharp_options.json");

    public static Settings Load(string dataDir)
    {
        string root = Directory.GetParent(dataDir)?.FullName ?? dataDir;
        SaveDir = Directory.Exists(root) ? Directory.EnumerateDirectories(root).FirstOrDefault(d => System.IO.Path.GetFileName(d).Equals("Save", StringComparison.OrdinalIgnoreCase)) ?? System.IO.Path.Combine(root, "Save") : System.IO.Path.Combine(root, "Save");
        // anciennes options (%APPDATA%\FD3_CSharp\options.json) : reprises une fois
        string old = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FD3_CSharp", "options.json");
        try
        {
            if (!File.Exists(PathOf()) && File.Exists(old))
            {
                Directory.CreateDirectory(SaveDir);
                File.Copy(old, PathOf());
            }
        } catch { }
        try
        {
            if (File.Exists(PathOf()))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathOf())) ?? new();
        } catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathOf())!);
            File.WriteAllText(PathOf(), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { Console.WriteLine("Options : " + e.Message); }
    }
}
