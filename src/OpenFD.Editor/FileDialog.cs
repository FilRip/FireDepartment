using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenFD.MG;

/// <summary>
/// Fenêtre « Enregistrer sous » du système, pour choisir le nom et l'emplacement d'un fichier : celle de Windows
/// (comdlg32, dans son propre fil STA), ailleurs zenity s'il est installé. Null : annulé (ou pas de fenêtre possible).
/// </summary>
internal static class FileDialog
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetSaveFileNameW(ref OpenFileName ofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

    private const int OfnFileMustExist = 0x1000;

    private const int OfnOverwritePrompt = 0x2;
    private const int OfnNoChangeDir = 0x8;
    private const int OfnPathMustExist = 0x800;
    private const int OfnExplorer = 0x80000;
    private const int MaxPath = 1024;

    /// <summary>Nom du zip à écrire : la fenêtre s'ouvre sur <paramref name="folder"/>, avec <paramref name="name"/> proposé.</summary>
    public static string? SaveZip(string title, string folder, string name) => SaveFile(title, folder, name, "zip", "Zip");

    /// <summary>Nom d'un fichier « .<paramref name="ext"/> » à écrire (<paramref name="kind"/> : nom du type, dans le filtre).</summary>
    public static string? SaveFile(string title, string folder, string name, string ext, string kind)
    {
        if (OperatingSystem.IsWindows())
            return SaveWindows(title, folder, name, ext, kind);
        return SaveZenity(title, Path.Combine(folder, name), ext);
    }

    /// <summary>Fenêtre « Ouvrir » : un fichier « .<paramref name="ext"/> » existant à lire (null : annulé).</summary>
    public static string? OpenFile(string title, string folder, string ext, string kind)
    {
        if (OperatingSystem.IsWindows())
            return SaveWindows(title, folder, "", ext, kind, true);
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("zenity")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (string a in new[] { "--file-selection", "--title=" + title, "--filename=" + folder + Path.DirectorySeparatorChar, "--file-filter=*." + ext })
                psi.ArgumentList.Add(a);
            using Process? p = Process.Start(psi);
            if (p == null)
                return null;
            string res = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return p.ExitCode != 0 || res.Length == 0 ? null : res;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? SaveWindows(string title, string folder, string name, string ext, string kind, bool open = false)
    {
        string? result = null;
        Thread t = new Thread(() =>
        {
            IntPtr buf = Marshal.AllocHGlobal(MaxPath * 2);
            try
            {
                char[] init = new char[MaxPath];
                name.CopyTo(0, init, 0, Math.Min(name.Length, MaxPath - 1));
                Marshal.Copy(init, 0, buf, MaxPath);
                OpenFileName ofn = new OpenFileName
                {
                    lStructSize = Marshal.SizeOf<OpenFileName>(),
                    lpstrFilter = $"{kind} (*.{ext})\0*.{ext}\0\0",
                    nFilterIndex = 1,
                    lpstrFile = buf,
                    nMaxFile = MaxPath,
                    lpstrInitialDir = Directory.Exists(folder) ? folder : null,
                    lpstrTitle = title,
                    Flags = (open ? OfnFileMustExist : OfnOverwritePrompt) | OfnNoChangeDir | OfnPathMustExist | OfnExplorer,
                    lpstrDefExt = ext,
                };
                if (open ? GetOpenFileNameW(ref ofn) : GetSaveFileNameW(ref ofn))
                    result = Marshal.PtrToStringUni(buf);
            }
            catch (Exception e)
            {
                Console.WriteLine("Enregistrer sous : " + e.Message);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        });
        if (OperatingSystem.IsWindows())
            t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static string? SaveZenity(string title, string path, string ext)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("zenity")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (string a in new[] { "--file-selection", "--save", "--confirm-overwrite", "--title=" + title, "--filename=" + path, "--file-filter=*." + ext })
                psi.ArgumentList.Add(a);
            using Process? p = Process.Start(psi);
            if (p == null)
                return null;
            string res = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            if (p.ExitCode != 0 || res.Length == 0)
                return null;
            return res.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase) ? res : res + "." + ext;
        }
        catch (Exception)
        {
            // pas de zenity : le fichier va dans le dossier proposé
            return path;
        }
    }
}
