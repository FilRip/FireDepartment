using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OpenFD.MG;

/// <summary>
/// Version de démonstration fournie avec OpenFD : le sous-dossier « demo » du dossier courant (ou, à défaut, de celui
/// du programme), utilisé quand aucun dossier de jeu n'est donné en ligne de commande. Une fenêtre le dit au joueur.
/// </summary>
public static class DemoFolder
{
    public const string Name = "demo";
    private const string Notice = "You did not specify a game directory. Using the DEMO version.";
    private const string Title = "Open Fire Department";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SDL_ShowSimpleMessageBox(uint flags, [MarshalAs(UnmanagedType.LPUTF8Str)] string title, [MarshalAs(UnmanagedType.LPUTF8Str)] string message, IntPtr window);

    private const uint MbIconInformation = 0x40;
    private const uint SdlMessageBoxInformation = 0x40;

    /// <summary>Le dossier « demo » (celui du jeu de démonstration, avec son dossier Data), ou null s'il n'est pas là.</summary>
    public static string? Find()
    {
        foreach (string root in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            string dir = Path.Combine(root, Name);
            if (File.Exists(Path.Combine(dir, "Data", "design.pak")) || File.Exists(Path.Combine(dir, "design.pak")))
                return dir;
        }
        return null;
    }

    /// <summary>
    /// Annonce au joueur que la version de démonstration est utilisée : dans le journal, et dans une fenêtre simple
    /// (pas pendant les essais automatiques « --capture », que la fenêtre bloquerait).
    /// </summary>
    public static void Announce(bool window)
    {
        Console.WriteLine(Notice);
        if (!window)
            return;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                MessageBoxW(IntPtr.Zero, Notice, Title, MbIconInformation);
                return;
            }
            // ailleurs : la fenêtre de SDL (bibliothèque de MonoGame), trouvée grâce au chargeur de Display
            RuntimeHelpers.RunClassConstructor(typeof(Display).TypeHandle);
            SDL_ShowSimpleMessageBox(SdlMessageBoxInformation, Title, Notice, IntPtr.Zero);
        }
        catch (Exception e)
        {
            Console.WriteLine("Fenêtre d'information : " + e.Message);
        }
    }
}
