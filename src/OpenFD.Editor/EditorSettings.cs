using System.Globalization;

namespace OpenFD.MG;

/// <summary>
/// Réglages de l'éditeur gardés d'une session à l'autre : « OpenFD.Editor.ini », à côté de l'exécutable, section
/// [LAYOUT] (barres de séparation). L'éditeur de missions et celui des scripts y écrivent chacun leurs clés : le
/// fichier est relu juste avant d'écrire, le reste est gardé tel quel.
/// </summary>
internal static class EditorSettings
{
    private const string Section = "LAYOUT";

    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "OpenFD.Editor.ini");

    private static string ReadText()
    {
        try
        {
            return File.Exists(FilePath) ? File.ReadAllText(FilePath) : "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>Nombre lu (point décimal) ; la valeur par défaut s'il manque ou n'est pas lisible.</summary>
    public static float Get(string key, float fallback)
    {
        string? v = IniText.Get(ReadText(), Section, key);
        if (v != null && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && float.IsFinite(f))
            return f;
        return fallback;
    }

    /// <summary>Écrit ces nombres (les autres clés et sections restent).</summary>
    public static void Set(params (string key, float value)[] values)
    {
        try
        {
            string text = ReadText();
            if (text.Length == 0)
                text = "; Open Fire Department - réglages de l'éditeur" + Environment.NewLine + "[" + Section + "]" + Environment.NewLine;
            foreach ((string key, float value) in values)
                text = IniText.Set(text, Section, key, value.ToString("0.####", CultureInfo.InvariantCulture));
            File.WriteAllText(FilePath, text);
        }
        catch (Exception e)
        {
            Console.WriteLine($"OpenFD.Editor.ini : {e.Message}");
        }
    }
}
