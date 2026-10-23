// Standalone reproduction of Microsoft.Extensions.FileProviders.Physical.PhysicalFileProvider's
// path-containment logic, copied verbatim from:
//   src/libraries/Microsoft.Extensions.FileProviders.Physical/src/PhysicalFileProvider.cs
//   src/libraries/Microsoft.Extensions.FileProviders.Physical/src/Internal/PathUtils.cs
// (dotnet/runtime, as of this test)
//
// Goal: run the REAL algorithm on a REAL Windows filesystem (via GitHub Actions windows-latest)
// and see whether any crafted subpath can make GetFullPath() resolve outside Root.

using System.Globalization;

string root = Path.Combine(Path.GetTempPath(), "wwwroot_test_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string fullRoot = Path.GetFullPath(root);
string Root = EnsureTrailingSlash(fullRoot);

Console.WriteLine($"[info] Root = {Root}");
Console.WriteLine($"[info] OS   = {Environment.OSVersion}");
Console.WriteLine();

string[] candidates = new[]
{
    // classic traversal (should be caught by PathNavigatesAboveRoot)
    "../../../Windows/win.ini",
    "..\\..\\..\\Windows\\win.ini",

    // mixed separators
    "..\\../..\\../Windows/win.ini",

    // my original hypothesis: smuggle a drive letter past IsPathRooted
    "?\\C:\\Windows\\win.ini",
    "?/C:/Windows/win.ini",

    // UNC / device-namespace smuggling attempts (leading separators get TrimStart'ed first)
    "\\\\?\\C:\\Windows\\win.ini",
    "\\\\server\\share\\file.txt",

    // absolute path (should be rejected outright by IsPathRooted)
    "C:\\Windows\\win.ini",

    // IIS-style "...." bypass idea (not a real ".." token to the tokenizer)
    "....//....//Windows/win.ini",
    "....\\\\....\\\\Windows\\\\win.ini",

    // drive-relative (no backslash after colon) — different from fully-rooted on Windows
    "C:Windows\\win.ini",

    // colon smuggling mid-path
    "foo\\C:\\Windows\\win.ini",

    // trailing dot/space tricks (legacy Windows shell quirks)
    "..\\..\\..\\Windows\\win.ini.",
    "..\\..\\..\\Windows\\win.ini ",
};

int bypassCount = 0;

foreach (var raw in candidates)
{
    string subpath = raw;

    // --- begin verbatim PhysicalFileProvider.GetFileInfo logic ---
    if (string.IsNullOrEmpty(subpath) || HasInvalidPathChars(subpath))
    {
        Console.WriteLine($"[BLOCKED-invalidchars] {Escape(raw)}");
        continue;
    }

    subpath = subpath.TrimStart(PathSeparators);

    if (Path.IsPathRooted(subpath))
    {
        Console.WriteLine($"[BLOCKED-rooted]       {Escape(raw)}  (trimmed: {Escape(subpath)})");
        continue;
    }

    string? fullPath = GetFullPath(subpath, Root);
    if (fullPath == null)
    {
        Console.WriteLine($"[BLOCKED-navigate/err] {Escape(raw)}");
        continue;
    }
    // --- end verbatim logic ---

    // fullPath is guaranteed to string-start-with Root here (that's what GetFullPath()
    // just enforced). The real question is whether it ALSO points outside Root in
    // practice, i.e. whether the live Windows filesystem resolves it somewhere the
    // string comparison didn't anticipate (trailing dot/space stripping, short names,
    // reparse points, etc). We check that empirically rather than trusting the string.
    bool reallyEscaped = false;
    string note = "";
    try
    {
        if (File.Exists(fullPath))
        {
            var fi = new FileInfo(fullPath);
            string canonical = fi.FullName; // .NET's own canonicalization of the resolved handle's path
            reallyEscaped = !canonical.StartsWith(Root, StringComparison.OrdinalIgnoreCase);
            note = $"canonical={canonical}";
        }
        else
        {
            note = "(no file exists there in this test root, as expected)";
        }
    }
    catch (Exception ex)
    {
        note = $"(exception probing: {ex.GetType().Name}: {ex.Message})";
    }

    if (reallyEscaped) bypassCount++;
    Console.WriteLine($"[{(reallyEscaped ? "BYPASS" : "OK")}] {Escape(raw)}");
    Console.WriteLine($"         -> fullPath (passed library's own check) = {fullPath}");
    Console.WriteLine($"         -> {note}");
}

Console.WriteLine();
Console.WriteLine(bypassCount == 0
    ? "[RESULT] No candidate escaped Root. Hypothesis NOT confirmed."
    : $"[RESULT] {bypassCount} candidate(s) escaped Root! Needs manual confirmation.");

// Exit code signals result for the workflow step to pick up without parsing text.
return bypassCount == 0 ? 0 : 1;

static string Escape(string s) => s.Replace("\\", "\\\\");

static readonly char[] PathSeparators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };

static bool IsValidDriveChar(char c) => (uint)((c | 0x20) - 'a') <= 'z' - 'a';

static bool HasInvalidPathChars(string path)
{
    foreach (char c in Path.GetInvalidFileNameChars())
    {
        if (c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar) continue;
        if (path.Contains(c)) return true;
    }
    return false;
}

static string EnsureTrailingSlash(string path)
{
    if (!string.IsNullOrEmpty(path) &&
        path[path.Length - 1] != Path.DirectorySeparatorChar &&
        path[path.Length - 1] != Path.AltDirectorySeparatorChar)
    {
        return path + Path.DirectorySeparatorChar;
    }
    return path;
}

static bool PathNavigatesAboveRoot(string path)
{
    var tokens = path.Split(PathSeparators);
    int depth = 0;
    foreach (var segment in tokens)
    {
        if (segment == "." || segment == "") continue;
        else if (segment == "..")
        {
            depth--;
            if (depth == -1) return true;
        }
        else depth++;
    }
    return false;
}

static string? GetFullPath(string path, string Root)
{
    if (PathNavigatesAboveRoot(path)) return null;
    string fullPath;
    try
    {
        fullPath = Path.GetFullPath(Path.Combine(Root, path));
    }
    catch
    {
        return null;
    }
    if (!IsUnderneathRoot(fullPath, Root)) return null;
    return fullPath;
}

static bool IsUnderneathRoot(string fullPath, string Root)
{
    return fullPath.StartsWith(Root, StringComparison.OrdinalIgnoreCase);
}
