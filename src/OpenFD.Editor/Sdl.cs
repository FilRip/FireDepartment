using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenFD.MG;

/// <summary>
/// Quelques fonctions de SDL2 (la bibliothèque de fenêtres de MonoGame DesktopGL) : presse-papiers, et remettre la
/// fenêtre devant. Le nom du fichier change selon le système : il est cherché parmi les noms habituels.
/// </summary>
internal static class Sdl
{
    private const string Lib = "SDL2";
    private static bool _resolver;

    private static void Resolver()
    {
        if (_resolver)
            return;
        _resolver = true;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(Sdl).Assembly, (name, asm, path) =>
            {
                if (name != Lib)
                    return IntPtr.Zero;
                foreach (string n in new[] { "SDL2.dll", "libSDL2-2.0.so.0", "libSDL2-2.0.0.dylib", "libSDL2.so", "SDL2" })
                    if (NativeLibrary.TryLoad(n, asm, path, out IntPtr h))
                        return h;
                return IntPtr.Zero;
            });
        }
        catch (InvalidOperationException)
        {
        }
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int SDL_SetClipboardText(byte[] text);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SDL_GetClipboardText();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_free(IntPtr p);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_RaiseWindow(IntPtr window);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_RestoreWindow(IntPtr window);

    public static void SetClipboard(string text)
    {
        Resolver();
        try
        {
            byte[] b = Encoding.UTF8.GetBytes(text + "\0");
            SDL_SetClipboardText(b);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    public static string GetClipboard()
    {
        Resolver();
        try
        {
            IntPtr p = SDL_GetClipboardText();
            if (p == IntPtr.Zero)
                return "";
            string s = Marshal.PtrToStringUTF8(p) ?? "";
            SDL_free(p);
            return s;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return "";
        }
    }

    /// <summary>La fenêtre (pointeur SDL : Window.Handle de MonoGame DesktopGL) passe devant les autres.</summary>
    public static void Raise(IntPtr window)
    {
        Resolver();
        try
        {
            SDL_RestoreWindow(window);
            SDL_RaiseWindow(window);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }
}
